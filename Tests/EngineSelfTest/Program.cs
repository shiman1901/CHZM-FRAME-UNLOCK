using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CHZMFRAMEUNLOCK1.Services;

namespace EngineSelfTest;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length >= 2 &&
            args[0].Equals("--install-smooth", StringComparison.OrdinalIgnoreCase))
        {
            using var installer = new TensorAutoService();
            TensorToggleResult result = installer.ApplyEngineToDirectory(
                EngineMode.SmoothMotion, args[1], "sp26-cod");

            Console.WriteLine($"{result.Action}: {result.Title} — {result.Detail}");
            return result.Action == TensorToggleAction.Failed ? 1 : 0;
        }

        if (args.Length >= 2 &&
            args[0].Equals("--install-classic", StringComparison.OrdinalIgnoreCase))
        {
            using var installer = new TensorAutoService();
            TensorToggleResult result = installer.SetClassicMaxFrames(5, args[1]);

            Console.WriteLine($"{result.Action}: {result.Title} — {result.Detail}");
            return result.Action == TensorToggleAction.Failed ? 1 : 0;
        }

        Console.WriteLine("CHZM Frame Unlock — автотест трёх движков");
        Console.WriteLine($"Проект: {TensorAutoService.ModSourceDirectory}");
        Console.WriteLine($"Паки:   {TensorAutoService.SupplementSourceRoot}");
        Console.WriteLine();

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "CHZM-selftest-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);

            try { File.Delete(Path.Combine(AppContext.BaseDirectory, "chzm-settings.json")); } catch { }

            using var service = new TensorAutoService();

            string fake = CreateFakeGame(root, "fake-main", ownDbgHelp: true);
            RunMainScenario(service, fake);

            string guarded = CreateFakeGame(root, "fake-guarded", ownDbgHelp: false,
                ownVersion: 777, ownDxgi: 888);
            RunOwnFileProtectionScenario(service, guarded);

            string noDlss = CreateFakeGame(root, "fake-nodlss", ownDbgHelp: false, withDlss: false);
            RunNoDlssScenario(service, noDlss);

            RunAutoPilotScenario(service, root);
            RunMenuKeyScenario(service);
            RunLiveFlowSettingsScenario(service);
            RunScannerScenario(service);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FATAL: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"ИТОГ: PASS {_passed} / FAIL {_failed}");

        return _failed == 0 ? 0 : 1;
    }

    private static string CreateFakeGame(string root, string name, bool ownDbgHelp,
        long ownVersion = 0, long ownDxgi = 0, bool withDlss = true)
    {
        string dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "game.exe"), "FAKE-GAME-EXE");

        if (withDlss)
            File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), new byte[1024]);

        File.WriteAllBytes(Path.Combine(dir, "game-data.bin"), new byte[2048]);

        if (ownDbgHelp)
            File.WriteAllBytes(Path.Combine(dir, "dbghelp.dll"), new byte[12345]);

        if (ownVersion > 0)
            File.WriteAllBytes(Path.Combine(dir, "version.dll"), new byte[ownVersion]);

        if (ownDxgi > 0)
            File.WriteAllBytes(Path.Combine(dir, "dxgi.dll"), new byte[ownDxgi]);

        return dir;
    }

    private static void RunMainScenario(TensorAutoService service, string dir)
    {
        Console.WriteLine("=== 1. Classic → настройки → Encore → Smooth → Classic → очистка ===");

        long classicProxySize = new FileInfo(
            Path.Combine(TensorAutoService.ModSourceDirectory, "version.dll")).Length;
        long encoreSize = new FileInfo(
            Path.Combine(TensorAutoService.EncoreSourceDirectory, "alternative-proxies", "version.dll")).Length;

        // ---- Classic install ----
        var result = service.ApplyEngineToDirectory(EngineMode.ClassicSm86, dir, "game");
        Check(result.Action is TensorToggleAction.Installed or TensorToggleAction.Enabled,
            "Classic: установка выполнена");

        string ini = Path.Combine(dir, "dlssg_sm86.ini");
        Check(File.Exists(ini), "Classic: dlssg_sm86.ini создан");
        Check(Directory.Exists(Path.Combine(dir, "dlssg_sm86")), "Classic: папка dlssg_sm86 создана");
        Check(File.Exists(Path.Combine(dir, "dlssg_sm86", "runtime", "nvngx_dlssg.dll")),
            "Classic: runtime nvngx_dlssg.dll на месте");
        Check(File.Exists(Path.Combine(dir, "dlssg_sm86", "sr", "sr_kernels.dll")),
            "Classic: SR-файлы (INT8) на месте");
        Check(TensorAutoService.ReadMarkerEngine(dir) == "classic",
            "Classic: маркер engine=classic");
        Check(new FileInfo(Path.Combine(dir, "version.dll")).Length == classicProxySize,
            "Classic: version.dll — наш прокси");
        Check(new FileInfo(Path.Combine(dir, "dbghelp.dll")).Length == 12345,
            "Classic: родной dbghelp.dll игры не перезаписан");

        // ---- 2X / 3X / 4X / 6X / Auto ----
        var frames = new (int Frames, string Label)[]
        {
            (1, "2X"), (2, "3X"), (3, "4X"), (5, "6X"), (0, "Auto")
        };

        foreach (var (value, label) in frames)
        {
            var r = service.SetClassicMaxFrames(value, dir);
            Check(r.Action is TensorToggleAction.Enabled or TensorToggleAction.Installed,
                $"Classic: кнопка {label} записалась");
            Check(TensorAutoService.ReadClassicMaxFrames(dir) == value,
                $"Classic: INI MaxGeneratedFrames={value} ({label})");
        }

        // ---- M-INT8 / L-INT8 / M-FP16 / Off ----
        foreach (string mode in new[] { "M-INT8", "L-INT8", "M-FP16", "Off" })
        {
            var r = service.SetClassicSuperResolution(mode, dir);
            Check(r.Action is TensorToggleAction.Enabled or TensorToggleAction.Installed,
                $"Classic: SR {mode} записан");
            Check(TensorAutoService.ReadClassicSuperResolution(dir) == mode,
                $"Classic: INI SuperResolution={mode}");
        }

        // ---- FG preset: A / C ----
        service.SetClassicPreset("C", dir);
        Check(TensorAutoService.ReadClassicPreset(dir) == "C", "Classic: Preset C (FG Lite) записан");
        service.SetClassicPreset("A", dir);
        Check(TensorAutoService.ReadClassicPreset(dir) == "A", "Classic: Preset A записан");

        // вернём боевые настройки
        service.SetClassicMaxFrames(3, dir);
        service.SetClassicSuperResolution("M-INT8", dir);

        // ---- switch to RTX Encore ----
        result = service.ApplyEngineToDirectory(EngineMode.RtxEncore, dir, "game");
        Check(result.Action == TensorToggleAction.Installed, "Encore: установка выполнена");
        Check(TensorAutoService.ReadMarkerEngine(dir) == "rtx-encore", "Encore: маркер engine=rtx-encore");
        Check(!File.Exists(ini), "Encore: dlssg_sm86.ini удалён (конфликт убран)");
        Check(!Directory.Exists(Path.Combine(dir, "dlssg_sm86")), "Encore: папка dlssg_sm86 удалена");
        Check(File.Exists(Path.Combine(dir, "rtx-encore.jsonc")), "Encore: rtx-encore.jsonc создан");
        Check(new FileInfo(Path.Combine(dir, "version.dll")).Length == encoreSize,
            "Encore: version.dll — 36 МБ движок Encore");
        Check(File.ReadAllText(Path.Combine(dir, "rtx-encore.jsonc"))
                .Contains("\"smoothMotionEnabled\": false", StringComparison.OrdinalIgnoreCase),
            "Encore: Smooth Motion по умолчанию выключен");
        Check(new FileInfo(Path.Combine(dir, "dbghelp.dll")).Length == 12345,
            "Encore: родной dbghelp.dll игры не тронут");

        // ---- Encore toggle off / on ----
        result = service.ApplyEngineToDirectory(EngineMode.RtxEncore, dir, "game");
        Check(result.Action == TensorToggleAction.Disabled, "Encore: повторное нажатие выключает");
        Check(!File.Exists(Path.Combine(dir, "version.dll")), "Encore: прокси убран при выключении");

        result = service.ApplyEngineToDirectory(EngineMode.RtxEncore, dir, "game");
        Check(result.Action == TensorToggleAction.Installed, "Encore: повторное включение");
        Check(File.Exists(Path.Combine(dir, "version.dll")), "Encore: прокси вернулся");

        // ---- switch to Smooth Motion ----
        result = service.ApplyEngineToDirectory(EngineMode.SmoothMotion, dir, "game");
        Check(result.Action == TensorToggleAction.Installed, "Smooth: установка выполнена");
        Check(TensorAutoService.ReadMarkerEngine(dir) == "smooth-motion", "Smooth: маркер engine=smooth-motion");
        Check(TensorAutoService.IsSmoothMotionConfigured(dir), "Smooth: smoothMotionEnabled=true");
        Check(File.Exists(Path.Combine(dir, "rtx-encore.jsonc")), "Smooth: конфиг Encore создан");
        Check(File.ReadAllText(Path.Combine(dir, "rtx-encore.jsonc"))
                .Contains("\"smoothMotionApi\": \"d3d12\"", StringComparison.OrdinalIgnoreCase),
            "Smooth: API по умолчанию d3d12");
        Check(!File.Exists(ini), "Smooth: Classic INI отсутствует");
        Check(new FileInfo(Path.Combine(dir, "version.dll")).Length == encoreSize,
            "Smooth: прокси Encore v1.4.5 на месте");
        Check(File.ReadAllText(Path.Combine(dir, "rtx-encore.jsonc"))
                .Contains("\"disableInGameMenu\": true", StringComparison.OrdinalIgnoreCase),
            "Smooth: меню отключено (без хука Present)");

        // ---- CoD sp26: меню обязано быть отключено принудительно ----
        string codDir = CreateFakeGame(Path.GetDirectoryName(dir)!, "CoD-Campaign-sp26", ownDbgHelp: false, withDlss: true);
        result = service.ApplyEngineToDirectory(EngineMode.SmoothMotion, codDir, "sp26-cod");
        Check(result.Action == TensorToggleAction.Installed, "Smooth CoD: установка выполнена");
        Check(File.ReadAllText(Path.Combine(codDir, "rtx-encore.jsonc"))
                .Contains("\"disableInGameMenu\": true", StringComparison.OrdinalIgnoreCase),
            "Smooth CoD: меню принудительно отключено");

        // ---- back to Classic ----
        result = service.ApplyEngineToDirectory(EngineMode.ClassicSm86, dir, "game");
        Check(result.Action == TensorToggleAction.Installed, "Classic: возврат после Smooth");
        Check(!File.Exists(Path.Combine(dir, "rtx-encore.jsonc")), "Classic: rtx-encore.jsonc удалён");
        Check(!Directory.Exists(Path.Combine(dir, "rtx-encore-logs")), "Classic: логи Encore удалены");
        Check(File.Exists(ini), "Classic: INI вернулся");
        Check(TensorAutoService.ReadMarkerEngine(dir) == "classic", "Classic: маркер снова classic");

        // ---- full cleanup ----
        result = service.RemoveEverythingFromDirectory(dir);
        Check(result.Action == TensorToggleAction.Disabled, "Очистка: выполнена");

        foreach (string name in new[]
                 {
                     "version.dll", "winmm.dll", "dinput8.dll", "dbghelp.dll.chzm", "dxgi.dll",
                     "dlssg_sm86.ini", "rtx-encore.jsonc", ".leno4ka-tensor.json",
                     ".leno4ka-optiscaler.json", "OptiScaler.ini"
                 })
        {
            if (name == "dbghelp.dll.chzm") continue;
            if (name == "dbghelp.dll")
            {
                Check(File.Exists(Path.Combine(dir, name)), "Очистка: родной dbghelp.dll игры сохранён");
                continue;
            }

            Check(!File.Exists(Path.Combine(dir, name)), $"Очистка: {name} удалён");
        }

        Check(!Directory.Exists(Path.Combine(dir, "dlssg_sm86")), "Очистка: папка dlssg_sm86 удалена");
        Check(!Directory.Exists(Path.Combine(dir, "rtx-encore-logs")), "Очистка: папка rtx-encore-logs удалена");
        Check(File.ReadAllText(Path.Combine(dir, "game.exe")) == "FAKE-GAME-EXE",
            "Очистка: game.exe игры сохранён");
        Check(File.Exists(Path.Combine(dir, "nvngx_dlss.dll")), "Очистка: родной nvngx_dlss.dll сохранён");
        Check(File.Exists(Path.Combine(dir, "game-data.bin")), "Очистка: данные игры сохранены");

        bool anyOff = Directory.EnumerateFiles(dir, "*.off").Any();
        Check(!anyOff, "Очистка: .off-файлов не осталось");
    }

    private static void RunOwnFileProtectionScenario(TensorAutoService service, string dir)
    {
        Console.WriteLine();
        Console.WriteLine("=== 2. Защита родных version.dll / dxgi.dll игры ===");

        long encoreSize = new FileInfo(
            Path.Combine(TensorAutoService.EncoreSourceDirectory, "alternative-proxies", "winmm.dll")).Length;

        var result = service.ApplyEngineToDirectory(EngineMode.ClassicSm86, dir, "game");
        Check(result.Action is TensorToggleAction.Installed or TensorToggleAction.Enabled,
            "Защита: Classic установлен");
        Check(new FileInfo(Path.Combine(dir, "version.dll")).Length == 777,
            "Защита: родной version.dll (777 Б) не перезаписан");
        Check(new FileInfo(Path.Combine(dir, "dxgi.dll")).Length == 888,
            "Защита: родной dxgi.dll (888 Б) не перезаписан");
        Check(File.Exists(Path.Combine(dir, "winmm.dll")),
            "Защита: Classic выбрал свободный winmm.dll");

        result = service.ApplyEngineToDirectory(EngineMode.RtxEncore, dir, "game");
        Check(result.Action == TensorToggleAction.Installed, "Защита: Encore установлен");
        Check(new FileInfo(Path.Combine(dir, "version.dll")).Length == 777,
            "Защита: Encore не тронул родной version.dll");
        Check(new FileInfo(Path.Combine(dir, "dxgi.dll")).Length == 888,
            "Защита: Encore не тронул родной dxgi.dll");
        Check(new FileInfo(Path.Combine(dir, "winmm.dll")).Length == encoreSize,
            "Защита: Encore занял освободившийся winmm.dll");

        service.RemoveEverythingFromDirectory(dir);

        Check(new FileInfo(Path.Combine(dir, "version.dll")).Length == 777,
            "Защита: после очистки родной version.dll цел");
        Check(new FileInfo(Path.Combine(dir, "dxgi.dll")).Length == 888,
            "Защита: после очистки родной dxgi.dll цел");
        Check(!File.Exists(Path.Combine(dir, "winmm.dll")),
            "Защита: после очистки наш winmm.dll удалён");
    }

    private static void RunNoDlssScenario(TensorAutoService service, string dir)
    {
        Console.WriteLine();
        Console.WriteLine("=== 3. Smooth Motion в игре БЕЗ nvngx_dlss.dll ===");

        Check(!File.Exists(Path.Combine(dir, "nvngx_dlss.dll")), "Без DLSS: в игре нет nvngx_dlss.dll");

        var result = service.ApplyEngineToDirectory(EngineMode.SmoothMotion, dir, "game");
        Check(result.Action == TensorToggleAction.Installed, "Без DLSS: Smooth Motion установлен");
        Check(TensorAutoService.ReadMarkerEngine(dir) == "smooth-motion", "Без DLSS: маркер smooth-motion");
        Check(TensorAutoService.IsSmoothMotionConfigured(dir), "Без DLSS: smoothMotionEnabled=true");
        Check(File.Exists(Path.Combine(dir, "version.dll")), "Без DLSS: прокси установлен");

        service.RemoveEverythingFromDirectory(dir);

        Check(!File.Exists(Path.Combine(dir, "version.dll")), "Без DLSS: после очистки прокси удалён");
        Check(!File.Exists(Path.Combine(dir, "rtx-encore.jsonc")), "Без DLSS: конфиг удалён");
        Check(File.ReadAllText(Path.Combine(dir, "game.exe")) == "FAKE-GAME-EXE",
            "Без DLSS: exe игры сохранён");
    }

    private static void RunAutoPilotScenario(TensorAutoService service, string root)
    {
        Console.WriteLine();
        Console.WriteLine("=== 4. Умный автопилот ===");

        TensorAutoService.ResetNativeFrameGenCache();

        // Игра с настоящим DLSS-G
        string fgDir = CreateFakeGame(root, "auto-fg", ownDbgHelp: false, withDlss: false);
        File.WriteAllBytes(Path.Combine(fgDir, "nvngx_dlssg.dll"), new byte[1024]);
        var fgGame = new ActiveGameInfo("game", Path.Combine(fgDir, "game.exe"), fgDir);

        service.ClearManualMode(fgGame.ExecutablePath);
        AutoPilotInfo info = service.EvaluateAutoPilot(fgGame);

        Check(info.HasFrameGen, "Автопилот: nvngx_dlssg.dll найден");
        Check(!info.Blocked, "Автопилот: обычная игра не заблокирована");
        Check(info.ClassicAvailable, "Автопилот: Classic доступен при DLSS-G");
        Check(info.Recommended == EngineMode.RtxEncore, "Автопилот: при DLSS-G рекомендован Encore");
        Check(info.LiveFlowAvailable == LiveFlowBridge.IsInstalled,
            "Автопилот: статус LIVE FLOW совпадает с установкой");
        Check(info.LiveFlowRecommended == LiveFlowBridge.IsInstalled,
            "Автопилот: без прокси предлагает LIVE FLOW");

        service.SelectEngineAuto(EngineMode.SmoothMotion);
        service.ApplyAutoPilot(fgGame);
        Check(service.SelectedEngine == EngineMode.RtxEncore,
            "Автопилот: выбор сам переключился на Encore");

        // Ручной выбор для этой игры не перебивается
        service.SelectEngineManual(EngineMode.ClassicSm86, fgGame.ExecutablePath);
        info = service.ApplyAutoPilot(fgGame);
        Check(info.ManualOverride, "Автопилот: ручной выбор замечен");
        Check(service.SelectedEngine == EngineMode.ClassicSm86,
            "Автопилот: ручной Classic не перебит");

        using (var reloaded = new TensorAutoService())
        {
            Check(reloaded.HasManualMode(fgGame.ExecutablePath),
                "Автопилот: ручной выбор сохраняется между запусками");
            reloaded.ClearManualMode(fgGame.ExecutablePath);
        }

        service.ClearManualMode(fgGame.ExecutablePath);

        // sl.dlss_g.dll тоже считается DLSS-G
        string slDir = CreateFakeGame(root, "auto-slfg", ownDbgHelp: false, withDlss: false);
        File.WriteAllBytes(Path.Combine(slDir, "sl.dlss_g.dll"), new byte[1024]);
        TensorAutoService.ResetNativeFrameGenCache();
        Check(TensorAutoService.HasNativeDlssFrameGen(slDir),
            "Автопилот: sl.dlss_g.dll распознан как DLSS-G");

        // Игра без DLSS-G
        string noFgDir = CreateFakeGame(root, "auto-nofg", ownDbgHelp: false, withDlss: false);
        var noFgGame = new ActiveGameInfo("game", Path.Combine(noFgDir, "game.exe"), noFgDir);

        service.ClearManualMode(noFgGame.ExecutablePath);
        info = service.ApplyAutoPilot(noFgGame);
        Check(!info.HasFrameGen, "Автопилот: DLSS-G отсутствует");
        Check(!info.ClassicAvailable, "Автопилот: Classic недоступен без DLSS-G");
        Check(info.Recommended == EngineMode.SmoothMotion,
            "Автопилот: без DLSS-G рекомендован Smooth Motion");
        Check(service.SelectedEngine == EngineMode.SmoothMotion,
            "Автопилот: выбор сам переключился на Smooth Motion");
        Check(info.Message.Contains("нет DLSS-G", StringComparison.OrdinalIgnoreCase),
            "Автопилот: подпись про отсутствие DLSS-G");

        // Даже старый ручной Classic без DLSS-G исправляется на Smooth
        service.SelectEngineManual(EngineMode.ClassicSm86, noFgGame.ExecutablePath);
        service.ApplyAutoPilot(noFgGame);
        Check(service.SelectedEngine == EngineMode.SmoothMotion,
            "Автопилот: невалидный ручной Classic заменён на Smooth");
        Check(!service.HasManualMode(noFgGame.ExecutablePath),
            "Автопилот: невалидный override снят");

        // Наш собственный runtime не должен выглядеть как DLSS-G игры
        string installedDir = CreateFakeGame(root, "auto-installed", ownDbgHelp: false, withDlss: false);
        service.ApplyEngineToDirectory(EngineMode.ClassicSm86, installedDir, "game");
        TensorAutoService.ResetNativeFrameGenCache();
        Check(!TensorAutoService.HasNativeDlssFrameGen(installedDir),
            "Автопилот: наш dlssg_sm86\\runtime не считается DLSS-G игры");
        service.RemoveEverythingFromDirectory(installedDir);

        // EAC / BattlEye / preloader.dll
        string eacDir = CreateFakeGame(root, "auto-eac", ownDbgHelp: false, withDlss: true);
        Directory.CreateDirectory(Path.Combine(eacDir, "EasyAntiCheat"));
        File.WriteAllText(Path.Combine(eacDir, "EasyAntiCheat", "EasyAntiCheat.exe"), "x");
        var eacGame = new ActiveGameInfo("game", Path.Combine(eacDir, "game.exe"), eacDir);

        // Античит-игры заблокированы ВСЕГДА: обхода больше нет.
        info = service.EvaluateAutoPilot(eacGame);
        Check(info.Blocked, "Автопилот: EAC-игра заблокирована");
        Check(info.Message.Contains("Античит", StringComparison.OrdinalIgnoreCase),
            "Автопилот: понятная подпись защищённой игры");
        Check(info.LiveFlowRecommended,
            "Автопилот: в защищённой игре предлагается LIVE FLOW");

        string preDir = CreateFakeGame(root, "auto-preloader", ownDbgHelp: false, withDlss: false);
        File.WriteAllText(Path.Combine(preDir, "preloader.dll"), "x");
        Check(TensorAutoService.IsEacProtected(preDir, out string preReason) &&
              preReason.Contains("preloader", StringComparison.OrdinalIgnoreCase),
            "Автопилот: preloader.dll считается защитой");

        Check(TensorAutoService.IsEacProtected(
                  @"C:\Games\Wardogs\Wardogs\Binaries\Win64", out string wardogsReason) &&
              wardogsReason.Contains("Elytra", StringComparison.OrdinalIgnoreCase),
            "Античит: WARDOGS определяется как Elytra");

        Check(TensorAutoService.IsEacProtected(
                  @"C:\Games\Call.of.Duty.Modern.Warfare\multiplayer", out string codReason) &&
              codReason.Contains("Ricochet", StringComparison.OrdinalIgnoreCase),
            "Античит: Call of Duty с точками в пути — Ricochet");

        Check(!TensorAutoService.IsEacProtected(
                  @"C:\Games\Call.of.Duty.Modern.Warfare.4.Campaign-InsaneRamZes\sp26", out _),
            "Античит: CoD Campaign sp26 разблокирован");
    }

    private static void RunMenuKeyScenario(TensorAutoService service)
    {
        Console.WriteLine();
        Console.WriteLine("=== 5. Клавиша меню вместо INSERT ===");

        Check(service.MenuKey == 0x77 && service.MenuKeyName == "F8",
            "Клавиша меню: по умолчанию F8 (0x77)");

        service.SetMenuKey(0x76, "F7");
        Check(service.MenuKey == 0x76 && service.MenuKeyName == "F7",
            "Клавиша меню: F7 сохранена в памяти");

        string settingsPath = Path.Combine(AppContext.BaseDirectory, "chzm-settings.json");
        bool persisted = File.Exists(settingsPath) &&
                         File.ReadAllText(settingsPath).Contains("\"MenuKey\": 118", StringComparison.Ordinal);
        Check(persisted, "Клавиша меню: MenuKey=118 записан в chzm-settings.json");

        using (var second = new TensorAutoService())
        {
            Check(second.MenuKey == 0x76 && second.MenuKeyName == "F7",
                "Клавиша меню: переживает перезапуск программы");
        }

        service.SetMenuKey(0x77, "F8");
    }

    private static void RunLiveFlowSettingsScenario(TensorAutoService service)
    {
        Console.WriteLine();
        Console.WriteLine("=== 6. LIVE FLOW мост и настройки ===");

        Check(LiveFlowBridge.IsInstalled, "LIVE FLOW: установленный DLSS 5 MANAGER найден");
        Check(LiveFlowBridge.InstallReport.Contains("LIVE FLOW", StringComparison.OrdinalIgnoreCase),
            "LIVE FLOW: отчёт об установке");

        LiveFlowOptions defaults = service.GetLiveFlowOptions();
        Check(defaults.Multiplier == 2, "LIVE FLOW: множитель по умолчанию x2");
        Check(defaults.QualityPreset, "LIVE FLOW: пресет Quality по умолчанию");
        Check(defaults.LowLatency, "LIVE FLOW: Low Latency включён");
        Check(defaults.KeepStillPixels, "LIVE FLOW: Keep still pixels включён");

        service.SetLiveFlowMultiplier(6);
        service.SetLiveFlowQualityPreset(false);
        service.SetLiveFlowLowLatency(false);
        service.SetLiveFlowKeepStill(false);

        LiveFlowOptions changed = service.GetLiveFlowOptions();
        Check(changed.Multiplier == 2, "LIVE FLOW: всегда честный 2X");
        Check(changed.QualityPreset, "LIVE FLOW: Quality всегда включён");
        Check(!changed.LowLatency, "LIVE FLOW: Low Latency off сохранён");
        Check(!changed.KeepStillPixels, "LIVE FLOW: Keep still off сохранён");

        using (var reloaded = new TensorAutoService())
        {
            LiveFlowOptions saved = reloaded.GetLiveFlowOptions();
            Check(saved.Multiplier == 2 &&
                  saved.QualityPreset &&
                  !saved.LowLatency &&
                  !saved.KeepStillPixels,
                "LIVE FLOW: настройки переживают перезапуск");
        }

        service.SetLiveFlowMultiplier(2);
        service.SetLiveFlowQualityPreset(true);
        service.SetLiveFlowLowLatency(true);
        service.SetLiveFlowKeepStill(true);

        service.SetIgnoreAntiCheatWarnings(true);
        using (var allowed = new TensorAutoService())
        {
            Check(allowed.IgnoreAntiCheatWarnings,
                "Античит: галочка «разрешить все моды» переживает перезапуск");
        }
        service.SetIgnoreAntiCheatWarnings(false);
    }

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("  PASS  " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("  FAIL  " + name);
        }
    }

    private static void RunScannerScenario(TensorAutoService service)
    {
        Console.WriteLine();
        Console.WriteLine("=== 7. Сканер игр ===");

        string? steamRoot = GameLibraryScanner.FindSteamRoot();
        Check(steamRoot is not null && Directory.Exists(steamRoot),
            "Сканер: Steam найден" + (steamRoot is null ? " (не установлен)" : ""));
        if (steamRoot is not null)
            Console.WriteLine($"  Steam: {steamRoot}");

        // --- проверка поиска exe в фейковой папке игры ---
        string fakeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "CHZM-scanner-" + Guid.NewGuid().ToString("N"));

        try
        {
            string gameDir = Path.Combine(fakeRoot, "CLEANGAME");
            string projectDir = Path.Combine(gameDir, "CLEANGAME");
            Directory.CreateDirectory(Path.Combine(projectDir, "Binaries", "Win64"));
            string fakeExe = Path.Combine(projectDir, "Binaries", "Win64", "CLEANGAME-Win64-Shipping.exe");
            File.WriteAllText(fakeExe, "fake");
            File.WriteAllText(Path.Combine(gameDir, "cover.jpg"), new string('x', 2048));

            string? foundExe = GameLibraryScanner.FindExecutable(gameDir, "CLEANGAME");
            Check(foundExe is not null &&
                  string.Equals(Path.GetFileName(foundExe), "CLEANGAME-Win64-Shipping.exe",
                      StringComparison.OrdinalIgnoreCase),
                "Сканер: найдён .exe игры в Binaries\\Win64");

            string? foundCover = GameLibraryScanner.FindLocalCover(gameDir);
            Check(foundCover is not null, "Сканер: локальная обложка найдена");

            GameCapabilityProfile profile = GameLibraryScanner.BuildProfile(gameDir);
            Check(!profile.HasAntiCheatOrPreloader,
                "Сканер: чистая игра не помечена античитом");

            Check(GameLibraryScanner.LooksLikeJunkDirectory("_CommonRedist"),
                "Сканер: служебная папка распознана");
            Check(!GameLibraryScanner.LooksLikeJunkDirectory("CLEANGAME"),
                "Сканер: игровая папка не считается служебной");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("  FAIL  Сканер: исключение — " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(fakeRoot, recursive: true); } catch { }
        }

        // --- реальный скан установленных игр ---
        try
        {
            var scanner = new GameLibraryScanner(service);
            List<GameEntry> games = scanner.Scan();

            Check(games is not null, "Сканер: список игр получен");
            Check(games!.Count > 0, "Сканер: игры найдены");
            Console.WriteLine($"  Найдено игр: {games.Count}");

            foreach (GameEntry game in games)
            {
                string cover = game.CoverPath is null ? "без обложки" : "с обложкой";
                string status = game.BadgeText.Length > 0 ? $" [{game.BadgeText}]" : string.Empty;
                Console.WriteLine($"    • {game.Name} ({game.Source}) — {cover}{status}");
            }

            string[] expected = { "Arc Raiders", "Call of Duty", "The Finals", "The Witcher 3", "WARDOGS" };
            foreach (string name in expected)
            {
                GameEntry? hit = games.FirstOrDefault(g =>
                    g.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

                if (hit is not null)
                {
                    _passed++;
                    string cover = hit.CoverPath is null ? "без обложки" : "с обложкой";
                    Console.WriteLine($"  PASS  Сканер: {name} найдена ({hit.Source}) — {cover}");
                }
                else
                {
                    Console.WriteLine($"  SKIP  Сканер: {name} не найдена (не установлена)");
                }
            }
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("  FAIL  Сканер: исключение при реальном скане — " + ex.Message);
        }
    }
}
