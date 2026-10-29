using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CHZMFRAMEUNLOCK1.Services;

public enum TensorToggleAction
{
    NoGame,
    BlockedByAntiCheat,
    Installed,
    Enabled,
    Disabled,
    Failed
}

public sealed record TensorToggleResult(TensorToggleAction Action, string Title, string Detail);

public sealed record ActiveGameInfo(string ProcessName, string ExecutablePath, string Directory);

/// <summary>
/// Полный автомат вокруг мода DLSSG-SM86 (папка DLSS внутри проекта):
///  * фоновый престейджинг (мод уже лежит в папках игр в СПЯЩЕМ режиме);
///  * определение активной игры по окну на переднем плане;
///  * жёсткая блокировка папок с античитом (EAC / BattlEye / ARC Raiders / The Finals);
///  * переключение фреймгена по долгому удержанию [N].
///
/// ВАЖНО (сверено с DLSS\docs\INSTALL.en.md и DLSS\dlssg_sm86.ini этой поставки, v0.4.1):
///  * переключатель мода — это [General] Enabled=1 / Enabled=0
///    (ключа "passthrough" в этой версии НЕТ; Enabled=0 — и есть прозрачный пропуск:
///    "the game loads its own DLSSG unchanged, the proxy still forwards the system DLL");
///  * [FrameGeneration] MaxGeneratedFrames: 0..5, где 0 = «оставить счётчик самой игры/рантайма»,
///    1 = 2X, 2 = 3X, 3 = 4X (заводское), 5 = 6X;
///  * [SuperResolution] Mode: Off / M-INT8 / L-INT8 / M-FP16 / L-FP16 — значения "On" НЕ существует
///    (удалено в версии от 2026-10-19, сейчас это configuration_invalid и SR не включается);
///    тензорный INT8-апскейл — это M-INT8;
///  * ГОРЯЧЕЙ ПЕРЕЗАГРУЗКИ INI НЕТ: "Exit the game completely and restart it after editing;
///    there is no hot reload". Поэтому переключение применяется при следующем запуске игры —
///    это честно показывается в OSD.
/// </summary>
public sealed partial class TensorAutoService : IDisposable
{
    // ==== то, что реально включает/выключает мод ====
    private const string IniFileName = "dlssg_sm86.ini";
    private const string ProxyDllName = "version.dll";
    private const string ModFolderName = "dlssg_sm86";

    /// <summary>Заводское ограничение: 4 сгенерированных кадра на каждый реальный (0 = отдать счётчик игре).</summary>
    /// <summary>3 = четыре кадра на один реальный (4X). 0 = отдать счётчик игре/рантайму, 1 = 2X.</summary>
    private const string MaxGeneratedFrames = "3";

    /// <summary>Тензорный INT8-апскейл (единственное допустимое «включено» для SR в этой версии мода).</summary>
    private const string SuperResolutionMode = "M-INT8";

    /// <summary>Грузить ли SR-часть (~180 МБ) при включении. false — только фреймген (~23 МБ).</summary>
    private const bool CopySuperResolutionFiles = true;

    // ==== служебное ====
    private const int MaxPrestageGames = 60;
    private static readonly TimeSpan PrestageTimeBudget = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _cts = new();

    // Последнее игровое окно: нужно кнопке в РАСШИРЕННЫХ, потому что в момент клика
    // передний план — уже наше собственное окно, и GetForegroundWindow игру не вернёт.
    private readonly System.Threading.Timer _foregroundTracker;
    private readonly object _trackLock = new();
    private ActiveGameInfo? _lastGame;
    private int _lastGamePid;

    /// <summary>
    /// Последняя замеченная игра. Срока годности по времени нет: пока процесс физически жив,
    /// цель считается валидной (иначе после 2 минут бездействия игра «терялась»).
    /// </summary>
    public ActiveGameInfo? LastActiveGame
    {
        get
        {
            ActiveGameInfo? game;
            int pid;

            lock (_trackLock)
            {
                game = _lastGame;
                pid = _lastGamePid;
            }

            if (game is null) return null;

            if (pid <= 0)
            {
                // PID не удалось зафиксировать — проверяем по имени процесса и пути к exe
                if (IsGameStillRunning(game)) return game;
            }
            else if (IsProcessAlive(pid, out bool windowStillThere) || windowStillThere)
            {
                return game;
            }

            lock (_trackLock)
            {
                _lastGame = null;
                _lastGamePid = 0;
            }

            return null;
        }
    }

    private static bool IsGameStillRunning(ActiveGameInfo game)
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(game.ProcessName))
            {
                try
                {
                    int id = process.Id;
                    string? path = TryGetProcessPath(id);
                    if (string.Equals(path, game.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool IsProcessAlive(int pid, out bool windowStillThere)
    {
        windowStillThere = false;
        if (pid <= 0) return false;

        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return false;

            windowStillThere = process.MainWindowHandle != IntPtr.Zero;
            return true;
        }
        catch
        {
            // процесс защищён (Win32Exception на MainWindowHandle) — сам процесс ещё жив
            try
            {
                using Process process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public TensorAutoService()
    {
        LoadChzmSettings();
        _foregroundTracker = new System.Threading.Timer(_ => TrackForeground(), null, 700, 700);
    }

    private void TrackForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return; // наше окно — не затираем последнюю игру

            ActiveGameInfo? game = DescribeGame((int)pid, allowFullscreenHeuristic: true);
            if (game is null) return;

            lock (_trackLock)
            {
                _lastGame = game;
                _lastGamePid = (int)pid;
            }
        }
        catch
        {
            // трекер не должен ронять приложение
        }
    }

    /// <summary>Запомнить игру как последнюю цель (вызывается при успешном переключении).</summary>
    private void RememberGame(ActiveGameInfo game)
    {
        int pid = 0;
        try
        {
            foreach (Process process in Process.GetProcessesByName(game.ProcessName))
            {
                try
                {
                    int candidateId = process.Id;
                    string? path = TryGetProcessPath(candidateId);

                    if (string.Equals(path, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        pid = candidateId;
                        break;
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // ignore
        }

        lock (_trackLock)
        {
            _lastGame = game;
            _lastGamePid = pid;
        }
    }

    /// <summary>
    /// Игра, для которой надо переключать фреймген: живой передний план, затем последняя
    /// замеченная игра (пока процесс жив), затем полный скан окон/процессов.
    /// null не возвращается, пока в системе есть хоть одна запущенная игра.
    /// </summary>
    public ActiveGameInfo? ResolveTargetGame()
    {
        ActiveGameInfo? foreground = GetActiveGame();
        if (foreground is not null) return foreground;

        ActiveGameInfo? last = LastActiveGame;
        if (last is not null) return last;

        return ScanForRunningGame();
    }

    private static readonly string[] IgnoredProcesses =
    {
        // система / оболочка Windows
        "explorer", "dwm", "searchhost", "searchapp", "startmenuexperiencehost", "shellexperiencehost",
        "lockapp", "textinputhost", "applicationframehost", "sihost", "ctfmon", "taskmgr", "mmc",
        "conhost", "powershell", "pwsh", "cmd", "windowsterminal", "openconsole", "dllhost", "rundll32",
        // сама программа и её OSD
        "leno4kacontrol", "dlss 5 manager", "dlss5manager", "dlss manager", "dlssmanager",
        // лаунчеры / оверлеи / инструменты
        "steam", "steamwebhelper", "epicgameslauncher", "galaxylauncher", "goggalaxy", "eadesktop",
        "ubisoftconnect", "battle.net", "riotclientservices", "discord", "telegram", "spotify",
        "losslessscaling", "rtss", "msiafterburner", "obs64", "nvidia app", "nvcontainer",
        // браузеры / редакторы / прочее
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "yandex", "iexplore",
        "code", "devenv", "notepad", "notepad++", "explorer.exe"
    };

    private static readonly string[] AntiCheatDirectories =
    {
        "EasyAntiCheat", "EasyAntiCheat_EOS", "EasyAntiCheat_Setup", "BattlEye", "BattlEye_EAC", "AntiCheat",
        // ядерные и онлайн-античиты
        "Elytra", "Riot Vanguard", "Vanguard", "EAAntiCheat", "Javelin",
        "GameGuard", "ACE", "AntiCheatExpert", "Ricochet"
    };

    private static readonly string[] AntiCheatFiles =
    {
        "EasyAntiCheat.exe", "EasyAntiCheat_Setup.exe", "BEService.exe", "BEService_x64.exe",
        "BEClient_x64.dll", "BEService", "PioneerGame.exe", "Discovery.exe",
        "start_protected_game.exe", "start_protected_game", "preloader.dll",
        // Elytra / Vanguard / Ricochet / EA Javelin / ACE / GameGuard
        "Elytra.sys", "Elytra.dll", "vgk.sys", "vgc.exe", "vgc.dll",
        "Ricochet.sys", "Ricochet.dll", "rcs.sys", "rcs.dll",
        "Javelin.sys", "Javelin.dll", "EAAntiCheat.exe", "EAAntiCheat.dll",
        "GameGuard.des", "GameMon.des", "npggNT.des", "npgmup.des", "GameGuard.dll",
        "SGuard64.exe", "SGuardSvc64.exe", "ACE-BASE.sys", "ACE-GUARD.sys", "ACE-TRAY.exe", "ACE.dll"
    };

    /// <summary>Папка-источник мода ищется рядом с EXE, затем в корне проекта (dev-запуск из bin\...).</summary>
    public static string ModSourceDirectory
    {
        get
        {
            // ПРОЕКТ АВТОНОМЕН: базовый пак лежит внутри папки проекта (DLSS\),
            // внешняя C:\Leno4kaControl больше не используется.
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "DLSS")
            };

            // dev-запуск из bin\Debug\net8.0-windows\win-x64 -> поднимаемся к корню проекта
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && dir is not null; i++)
            {
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
                if (dir is null) break;
                candidates.Add(Path.Combine(dir, "DLSS"));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(candidate, ProxyDllName)) &&
                        File.Exists(Path.Combine(candidate, IniFileName)))
                        return candidate;
                }
                catch
                {
                    // игнорируем
                }
            }

            return candidates[0];
        }
    }

    public static bool IsModSourceReady()
    {
        try
        {
            string src = ModSourceDirectory;
            return File.Exists(Path.Combine(src, ProxyDllName))
                   && File.Exists(Path.Combine(src, IniFileName))
                   && Directory.Exists(Path.Combine(src, ModFolderName));
        }
        catch
        {
            return false;
        }
    }

    // ===================== АКТИВНАЯ ИГРА =====================

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint MoveFileDelayUntilReboot = 0x4;

    /// <summary>
    /// Путь к .exe по PID. process.MainModule на защищённых играх (CoD с Ricochet и т.п.)
    /// кидает Win32Exception "Отказано в доступе", поэтому основной путь — WinAPI
    /// OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + QueryFullProcessImageName.
    /// </summary>
    public static string? TryGetProcessPath(int pid)
    {
        if (pid <= 0) return null;

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0)
                {
                    string result = buffer.ToString(0, size);
                    if (result.Length > 0) return result;
                }
            }
            catch
            {
                // падаем в запасной путь
            }
            finally
            {
                try { CloseHandle(handle); } catch { }
            }
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Игра в окне на переднем плане (или null, если в фокусе не игра).</summary>
    public static ActiveGameInfo? GetActiveGame()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        return DescribeGame((int)pid, allowFullscreenHeuristic: true);
    }

    /// <summary>Определяет, является ли процесс игрой, и собирает о нём информацию.</summary>
    private static ActiveGameInfo? DescribeGame(int pid, bool allowFullscreenHeuristic)
    {
        try
        {
            string? exePath = TryGetProcessPath(pid);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;

            string? directory = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(directory)) return null;

            string processName = Path.GetFileNameWithoutExtension(exePath);

            if (!IsGameProcess(pid, processName, exePath, directory, allowFullscreenHeuristic)) return null;

            return new ActiveGameInfo(processName, exePath, directory);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGameProcess(int pid, string processName, string exePath, string directory, bool allowFullscreenHeuristic)
    {
        string name = processName.Trim().ToLowerInvariant();
        if (IgnoredProcesses.Contains(name)) return false;
        if (name.EndsWith("explorer", StringComparison.OrdinalIgnoreCase)) return false;

        string path = exePath.ToLowerInvariant();

        // системные каталоги и пакеты Store — это не игры
        if (path.StartsWith(@"c:\windows\", StringComparison.Ordinal)) return false;
        if (path.Contains(@"\windowsapps\")) return false;
        if (path.Contains(@"\program files\windowsapps\")) return false;

        // менеджеры модов/апскейлеров — не игры, даже если рядом лежат nvngx*.dll
        if (path.Contains(@"\dlss 5 manager\") || path.Contains(@"\dlss manager\") ||
            path.Contains(@"\dlss5manager\"))
            return false;

        // сама программа / её OSD
        try
        {
            string own = AppContext.BaseDirectory.ToLowerInvariant();
            if (path.StartsWith(own, StringComparison.Ordinal)) return false;
        }
        catch
        {
            // ignore
        }

        // (a) путь выдаёт игровую библиотеку
        string lowerDir = directory.ToLowerInvariant();
        if (lowerDir.Contains(@"\games\") || lowerDir.Contains(@"\steamlibrary\") ||
            lowerDir.Contains(@"\steamapps\common\") || lowerDir.Contains(@"\epic games\") ||
            lowerDir.Contains(@"\gog games\") || lowerDir.Contains(@"\gog galaxy\") ||
            lowerDir.Contains(@"\battle.net\") || lowerDir.Contains(@"\xboxgames\"))
            return true;

        // (b) рядом с exe (или на 1-2 уровня выше/ниже) лежат библиотеки апскейлеров/античита движка
        if (HasGameDllMarkers(directory)) return true;

        // (c) полноэкранное тяжёлое окно — почти наверняка игра
        if (allowFullscreenHeuristic && IsFullscreenHeavyWindow(pid, name, directory)) return true;

        return false;
    }

    private static readonly string[] DllMarkers =
    {
        "nvngx_dlss.dll", "nvngx_dlssg.dll", "sl.interposer.dll",
        "amd_fidelityfx_dx12.dll", "libxess.dll"
    };

    private static readonly ConcurrentDictionary<string, bool> DllMarkerCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Папка exe, 2 уровня вверх и 2 уровня вниз: ищем маркеры DLSS/Streamline/XeSS/oo2core.</summary>
    private static bool HasGameDllMarkers(string directory)
    {
        if (DllMarkerCache.TryGetValue(directory, out bool cached)) return cached;

        bool found = false;

        try
        {
            foreach (string dir in CandidateDirectories(directory))
            {
                foreach (string marker in DllMarkers)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir, marker)))
                        {
                            found = true;
                            break;
                        }
                    }
                    catch { }
                }

                if (found) break;

                try
                {
                    if (Directory.EnumerateFiles(dir, "oo2core_*.dll").Any())
                    {
                        found = true;
                        break;
                    }
                }
                catch { }
            }
        }
        catch
        {
            // ignore
        }

        DllMarkerCache[directory] = found;
        return found;
    }

    private static IEnumerable<string> CandidateDirectories(string directory)
    {
        yield return directory;

        string? parent = SafeParent(directory);
        if (parent is not null)
        {
            yield return parent;
            string? grand = SafeParent(parent);
            if (grand is not null) yield return grand;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory).Take(40).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (string child in children)
        {
            yield return child;

            IEnumerable<string> grandChildren;
            try
            {
                grandChildren = Directory.EnumerateDirectories(child).Take(20).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (string grandChild in grandChildren) yield return grandChild;
        }
    }

    private static string? SafeParent(string directory)
    {
        try
        {
            return Path.GetDirectoryName(directory.TrimEnd('\\'));
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] BrowserProcesses =
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "yandex", "iexplore", "explorer"
    };

    /// <summary>Видимое окно ≥ 80% экрана и рабочий набор &gt; 400 МБ (браузеры и explorer исключены).</summary>
    private static bool IsFullscreenHeavyWindow(int pid, string processName, string directory)
    {
        if (BrowserProcesses.Contains(processName)) return false;

        try
        {
            int screenWidth = GetSystemMetrics(0);
            int screenHeight = GetSystemMetrics(1);
            if (screenWidth <= 0 || screenHeight <= 0) return false;

            bool big = false;

            foreach (IntPtr hwnd in EnumerateTopLevelWindows())
            {
                _ = GetWindowThreadProcessId(hwnd, out uint windowPid);
                if (windowPid != (uint)pid) continue;

                if (!IsWindowVisible(hwnd)) continue;
                if (GetWindowTextLength(hwnd) == 0) continue;
                if (!GetWindowRect(hwnd, out Rect rect)) continue;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width <= 0 || height <= 0) continue;

                if (width >= screenWidth * 0.8 && height >= screenHeight * 0.8)
                {
                    big = true;
                    break;
                }
            }

            if (!big) return false;

            long workingSet;
            using (Process process = Process.GetProcessById(pid))
            {
                workingSet = process.WorkingSet64;
            }

            return workingSet > 400L * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    private static List<IntPtr> EnumerateTopLevelWindows()
    {
        var windows = new List<IntPtr>(64);

        try
        {
            EnumWindows((hwnd, _) =>
            {
                windows.Add(hwnd);
                return true; // Z-order сверху вниз
            }, IntPtr.Zero);
        }
        catch
        {
            // ignore
        }

        return windows;
    }

    /// <summary>
    /// Аварийный поиск запущенной игры, когда передний план занят нашим окном / explorer:
    /// сначала окна по Z-order, затем все процессы (берём самый «тяжёлый»).
    /// </summary>
    public static ActiveGameInfo? ScanForRunningGame()
    {
        // 1) окна сверху вниз: первая игра в Z-order — та, что видна поверх остальных
        try
        {
            foreach (IntPtr hwnd in EnumerateTopLevelWindows())
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) continue;
                    if (GetWindowTextLength(hwnd) == 0) continue;

                    _ = GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0 || pid == (uint)Environment.ProcessId) continue;

                    ActiveGameInfo? game = DescribeGame((int)pid, allowFullscreenHeuristic: true);
                    if (game is not null) return game;
                }
                catch
                {
                    // одно окно не должно ломать скан
                }
            }
        }
        catch
        {
            // ignore
        }

        // 2) все процессы: берём самый тяжёлый из похожих на игру
        try
        {
            ActiveGameInfo? best = null;
            long bestMemory = 0;

            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName.Trim().ToLowerInvariant();
                    if (IgnoredProcesses.Contains(name)) continue;

                    ActiveGameInfo? game = DescribeGame(process.Id, allowFullscreenHeuristic: true);
                    if (game is null) continue;

                    long memory;
                    try { memory = process.WorkingSet64; }
                    catch { memory = 0; }

                    if (memory > bestMemory)
                    {
                        bestMemory = memory;
                        best = game;
                    }
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }

            if (best is not null) return best;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    // ===================== РЕЖИМЫ РАБОТЫ =====================

    /// <summary>
    /// Пак OptiScaler (собран из ShyVortex/OptiScaler-DLSSNR-PreSR-Multipass v0.9.72):
    /// нужен для игр, где есть DLSS Super Resolution, но НЕТ нативного DLSS-G
    /// (например CoD). OptiScaler отдаёт игре фреймген через dlssg_sm86
    /// ([DLSSG] AmpereMfgUnlock=true + AmpereMfgMode=Internal).
    /// </summary>
    public static string OptiScalerSourceDirectory
    {
        get
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "OptiScaler")
            };

            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && dir is not null; i++)
            {
                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
                if (dir is null) break;
                candidates.Add(Path.Combine(dir, "OptiScaler"));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(candidate, "OptiScaler.dll")) &&
                        File.Exists(Path.Combine(candidate, "OptiScaler.ini")))
                        return candidate;
                }
                catch
                {
                    // ignore
                }
            }

            return candidates[0];
        }
    }

    public static bool IsOptiScalerReady()
    {
        try
        {
            string src = OptiScalerSourceDirectory;
            return File.Exists(Path.Combine(src, "OptiScaler.dll"))
                   && File.Exists(Path.Combine(src, "OptiScaler.ini"))
                   && Directory.Exists(Path.Combine(src, @"OptiScaler\dlssg_sm86"))
                   && Directory.Exists(Path.Combine(src, @"OptiScaler\streamline"));
        }
        catch
        {
            return false;
        }
    }

    private const string OptiScalerIniName = "OptiScaler.ini";
    private const string OptiScalerFolderName = "OptiScaler";
    private const string OptiScalerMarker = ".leno4ka-optiscaler.json";

    private static readonly string[] ProxyCandidates = { "dxgi.dll", "version.dll", "winmm.dll", "dbghelp.dll" };

    /// <summary>Игра сама умеет DLSS-G — работает наша штатная связка version.dll + dlssg_sm86.</summary>
    public static bool HasNativeFrameGen(string gameDir)
    {
        return HasDllMarkerNearby(gameDir, "nvngx_dlssg.dll");
    }

    /// <summary>В игре есть хотя бы DLSS Super Resolution — можно включить OptiFG.</summary>
    public static bool HasAnyDlss(string gameDir)
    {
        return HasDllMarkerNearby(gameDir, "nvngx_dlss.dll");
    }

    /// <summary>
    /// Игра сама создавала DLSS-G (feature_id 11) — это видно по логу мода.
    /// Если нет, пункт FG в настройках игры недоступен (обычное дело на RTX 20/30)
    /// и нужен OptiFG, который создаёт фреймген вместо игры.
    /// </summary>
    public static bool GameRequestsFrameGen(string gameDir)
    {
        try
        {
            string log = Path.Combine(gameDir, ModFolderName, "logs", "dlssg_sm86.log");
            if (!File.Exists(log)) return false;

            string text = File.ReadAllText(log);

            // строго порядок ключей из формата мода: {"caller":...,"event":"fg_gate_create_feature","feature_id":11,...}
            return text.Contains("\"event\":\"fg_gate_create_feature\",\"feature_id\":11", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static readonly ConcurrentDictionary<string, bool> NearbyDllCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, HashSet<string>> RecursiveDllCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Корень игры. Для Unreal Engine исполняемый файл лежит в
    /// &lt;GameRoot&gt;\&lt;Project&gt;\Binaries\Win64\&lt;Game&gt;.exe, а библиотеки DLSS — в
    /// &lt;GameRoot&gt;\Engine\Plugins\Runtime\Nvidia\... и &lt;GameRoot&gt;\&lt;Project&gt;\Plugins\...
    /// Поэтому от exeDir поднимаемся: \Binaries\Win64|WinGDK → на 2 уровня, \Binaries\X → на 2,
    /// \Bin\Win64 → на 2. Иначе корнем считаем сам exeDir.
    /// </summary>
    public static string GetGameRoot(string exeDir)
    {
        try
        {
            string dir = exeDir.TrimEnd('\\');
            string name = Path.GetFileName(dir);

            // ...\<Project>\Binaries\Win64
            if (name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("WinGDK", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Win32", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("x64", StringComparison.OrdinalIgnoreCase))
            {
                string? binaries = SafeParent(dir);
                if (binaries is not null &&
                    Path.GetFileName(binaries).Equals("Binaries", StringComparison.OrdinalIgnoreCase))
                {
                    string? project = SafeParent(binaries);
                    if (project is not null) return AscendToEngineRoot(project);
                }

                if (binaries is not null) return AscendToEngineRoot(binaries);
            }

            // ...\Game\Binaries (exe прямо в Binaries)
            if (name.Equals("Binaries", StringComparison.OrdinalIgnoreCase))
            {
                return AscendToEngineRoot(SafeParent(dir) ?? dir);
            }

            return dir;
        }
        catch
        {
            return exeDir;
        }
    }

    /// <summary>
    /// У UE-игр <GameRoot> содержит и Engine\, и &lt;Project&gt;\. Если рядом с проектом
    /// (или двумя уровнями выше) есть папка Engine — корнем считаем её родителя.
    /// Выше steamapps\common / SteamLibrary не поднимаемся.
    /// </summary>
    private static string AscendToEngineRoot(string projectDir)
    {
        try
        {
            string current = projectDir;

            for (int i = 0; i < 2; i++)
            {
                string? parent = SafeParent(current);
                if (parent is null) break;

                string parentName = Path.GetFileName(parent);
                if (parentName.Equals("common", StringComparison.OrdinalIgnoreCase) ||
                    parentName.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ||
                    parentName.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase))
                    break;

                if (Directory.Exists(Path.Combine(parent, "Engine")) ||
                    Directory.Exists(Path.Combine(parent, "Engine", "Binaries")))
                {
                    return parent;
                }

                current = parent;
            }
        }
        catch
        {
            // ignore
        }

        return projectDir;
    }

    /// <summary>
    /// Файл лежит рядом с exe, в 2 уровнях вверх/вниз, либо где угодно внутри корня игры
    /// (UE держит nvngx_dlss*.dll в Engine\Plugins\... — это 4-6 уровней вглубь).
    /// </summary>
    private static bool HasDllMarkerNearby(string directory, string fileName)
    {
        string key = directory + "|" + fileName;
        if (NearbyDllCache.TryGetValue(key, out bool cached)) return cached;

        bool found = false;

        // 1) быстрые места: сама папка, ±2 уровня
        try
        {
            foreach (string dir in CandidateDirectories(directory))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, fileName)))
                    {
                        found = true;
                        break;
                    }
                }
                catch { }
            }
        }
        catch
        {
            // ignore
        }

        // 2) рекурсивно по корню игры (Engine\Plugins\..., <Project>\Plugins\...)
        if (!found)
        {
            string root = GetGameRoot(directory);
            HashSet<string> names = GetDllNamesInTree(root);

            found = names.Contains(fileName);
        }

        NearbyDllCache[key] = found;
        return found;
    }

    /// <summary>Один проход по дереву корня игры: все имена *.dll с "dlss"/"sl." в названии.</summary>
    private static HashSet<string> GetDllNamesInTree(string root)
    {
        if (RecursiveDllCache.TryGetValue(root, out HashSet<string>? cached)) return cached;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string[] patterns = { "*dlss*.dll", "nvngx*.dll", "sl.*.dll" };

            foreach (string pattern in patterns)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                    {
                        try { names.Add(Path.GetFileName(file)); } catch { }
                    }
                }
                catch
                {
                    // нет прав на часть дерева — не критично
                }
            }
        }
        catch
        {
            // корень недоступен
        }

        RecursiveDllCache[root] = names;
        return names;
    }

    /// <summary>Прокси OptiScaler уже стоит в папке игры (и это именно он).</summary>
    private static string? FindOptiScalerProxy(string gameDir)
    {
        try
        {
            if (!File.Exists(Path.Combine(gameDir, OptiScalerIniName))) return null;

            foreach (string proxy in ProxyCandidates)
            {
                if (File.Exists(Path.Combine(gameDir, proxy))) return proxy;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Ставит пак OptiScaler рядом с реальным exe игры и включает OptiFG (2X).</summary>
    private static void InstallOptiScaler(string gameDir, bool frameGenEnabled)
    {
        string src = OptiScalerSourceDirectory;

        // 1) прокси: dxgi.dll, но не затираем чужой dxgi (ReShade и т.п.)
        string proxy = "dxgi.dll";
        foreach (string candidate in ProxyCandidates)
        {
            if (!File.Exists(Path.Combine(gameDir, candidate)))
            {
                proxy = candidate;
                break;
            }
        }

        CopyFile(Path.Combine(src, "OptiScaler.dll"), Path.Combine(gameDir, proxy));
        CopyFile(Path.Combine(src, OptiScalerIniName), Path.Combine(gameDir, OptiScalerIniName));

        // 2) только то, что нужно нашему маршруту: dlssg_sm86 (~28 МБ) + streamline (~10 МБ)
        CopyDirectory(Path.Combine(src, OptiScalerFolderName, "dlssg_sm86"),
                      Path.Combine(gameDir, OptiScalerFolderName, "dlssg_sm86"));
        CopyDirectory(Path.Combine(src, OptiScalerFolderName, "streamline"),
                      Path.Combine(gameDir, OptiScalerFolderName, "streamline"));

        // 3) маркер, чтобы отличать нашу установку
        try
        {
            File.WriteAllText(Path.Combine(gameDir, OptiScalerMarker),
                "{\"proxy\":\"" + proxy + "\",\"by\":\"Leno4kaControl\"}");
        }
        catch { }

        WriteOptiScalerConfig(gameDir, frameGenEnabled);
    }

    private static void CopyDirectory(string source, string destination)
    {
        try
        {
            if (!Directory.Exists(source)) return;

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                CopyFile(file, Path.Combine(destination, relative));
            }
        }
        catch
        {
            // одна папка не должна ломать установку
        }
    }

    /// <summary>Ключи OptiScaler для RTX 20/30: свой FG из апскейлера, MFG через dlssg_sm86.</summary>
    private static void WriteOptiScalerConfig(string gameDir, bool enabled)
    {
        string iniPath = Path.Combine(gameDir, OptiScalerIniName);

        try
        {
            (List<string> lines, Encoding encoding) = ReadIniLines(iniPath);

            SetIniValue(lines, "FrameGen", "External", "false");
            SetIniValue(lines, "FrameGen", "Enabled", enabled ? "true" : "false");
            SetIniValue(lines, "FrameGen", "FGInput", "upscaler");
            SetIniValue(lines, "FrameGen", "FGOutput", "dlssg");

            SetIniValue(lines, "DLSSG", "AmpereMfgUnlock", "true");
            SetIniValue(lines, "DLSSG", "AmpereMfgMode", "Internal");
            SetIniValue(lines, "DLSSG", "InterpolationCount", "1"); // 1 = 2X

            File.WriteAllLines(iniPath, lines, encoding);
        }
        catch
        {
            // нет прав / файл занят
        }
    }

    private static bool? ReadOptiScalerFrameGen(string gameDir)
    {
        string? value = ReadIniValue(Path.Combine(gameDir, OptiScalerIniName), "FrameGen", "Enabled");
        if (value is null) return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null
        };
    }

    // ===================== АНТИЧИТ =====================

    /// <summary>
    /// Жёсткий фильтр: EAC / BattlEye / ARC Raiders / The Finals в папке игры и вверх по дереву.
    /// Глубина 6 уровней — потому что рабочий каталог UE-игры это ...\&lt;Game&gt;\Binaries\Win64,
    /// а EasyAntiCheat\ и PioneerGame.exe / Discovery.exe лежат в корне игры (3-4 уровня выше).
    /// </summary>
    public static bool IsEacProtected(string gameDirectory, out string reason)
    {
        reason = string.Empty;

        try
        {
            // Одиночная кампания / sp26 — не античит, даже если рядом Ricochet.
            if (IsSinglePlayerCampaign(gameDirectory)) return false;

            // Быстрое правило по имени: эти игры защищены всегда, независимо от раскладки.
            string full = gameDirectory.ToLowerInvariant();
            string normalized = full.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

            if (normalized.Contains("arc raiders"))
            {
                reason = "ARC Raiders";
                return true;
            }

            if (normalized.Contains("the finals"))
            {
                reason = "The Finals";
                return true;
            }

            // WARDOGS: Elytra Anti-Cheat (C:\Program Files\Elytra, служба Elytra.Service).
            if (normalized.Contains("wardogs"))
            {
                reason = ElytraInstalled ? "Elytra Anti-Cheat (WARDOGS)" : "Elytra (WARDOGS)";
                return true;
            }

            // Остальные известные ядерные/онлайн-античиты по имени игры.
            if (normalized.Contains("valorant") || normalized.Contains("league of legends") || normalized.Contains("riot client"))
            {
                reason = "Riot Vanguard";
                return true;
            }

            if (normalized.Contains("call of duty") || normalized.Contains("modern warfare") || normalized.Contains("black ops"))
            {
                reason = "Ricochet";
                return true;
            }

            if (normalized.Contains("battlefield"))
            {
                reason = "EA Javelin";
                return true;
            }

            if (normalized.Contains("crossfire"))
            {
                reason = "ACE";
                return true;
            }

            if (normalized.Contains("lineage") || normalized.Contains("gameguard"))
            {
                reason = "GameGuard";
                return true;
            }

            string? dir = gameDirectory;

            for (int level = 0; level < 6 && !string.IsNullOrEmpty(dir); level++)
            {
                // Не поднимаемся выше корня игровой библиотеки / не заходим в системные папки:
                // глобальные EasyAntiCheat / BattlEye лежат в %ProgramData% и %LOCALAPPDATA%,
                // и оттуда давали ложные блокировки любой игры.
                if (IsSearchBoundary(dir)) break;
                foreach (string name in AntiCheatDirectories)
                {
                    try
                    {
                        if (Directory.Exists(Path.Combine(dir, name)))
                        {
                            reason = name;
                            return true;
                        }
                    }
                    catch { }
                }

                foreach (string name in AntiCheatFiles)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir, name)))
                        {
                            reason = name;
                            return true;
                        }
                    }
                    catch { }
                }

                // файлы античита могут лежать и внутри одноимённой папки
                foreach (string name in AntiCheatDirectories)
                {
                    try
                    {
                        string sub = Path.Combine(dir, name);
                        if (Directory.Exists(sub) &&
                            (File.Exists(Path.Combine(sub, "EasyAntiCheat.exe")) ||
                             File.Exists(Path.Combine(sub, "BEService.exe")) ||
                             File.Exists(Path.Combine(sub, "EasyAntiCheat_EOS.exe"))))
                        {
                            reason = name;
                            return true;
                        }
                    }
                    catch { }
                }

                dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
            }
        }
        catch
        {
            // не смогли проверить — считаем, что защиты нет; решение всё равно за пользователем
        }

        return false;
    }

    /// <summary>Сюжетка/одиночная кампания: античит не применяем (CoD campaign, sp26).</summary>
    private static bool IsSinglePlayerCampaign(string gameDirectory)
    {
        try
        {
            string normalized = gameDirectory.ToLowerInvariant()
                .Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

            return normalized.Contains("campaign") ||
                   normalized.Contains("singleplayer") ||
                   normalized.Contains("single player") ||
                   normalized.Contains("insaneramzes") ||
                   normalized.Contains("sp26");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Elytra Anti-Cheat установлен на ПК (папка и/или служба).</summary>
    private static bool ElytraInstalled
    {
        get
        {
            try
            {
                return Directory.Exists(@"C:\Program Files\Elytra") ||
                       File.Exists(@"C:\Program Files\Elytra\service.exe");
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Граница обхода вверх: корень игровой библиотеки, корень диска, профиль пользователя, Windows.</summary>
    private static bool IsSearchBoundary(string dir)
    {
        try
        {
            string trimmed = dir.TrimEnd('\\');
            string name = Path.GetFileName(trimmed);

            if (name.Equals("common", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SteamLibrary", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Steam", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Epic Games", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GOG Games", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Games", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("XboxGames", StringComparison.OrdinalIgnoreCase))
                return true;

            string full = trimmed.ToLowerInvariant();

            if (full.EndsWith(":\\", StringComparison.Ordinal)) return true;   // корень диска
            if (full.Contains(@"\appdata\")) return true;                      // глобальные папки античитов
            if (full.StartsWith(@"c:\windows", StringComparison.Ordinal)) return true;

            // C:\Users\Имя  и  C:\Users\Имя\AppData — но не сама папка игры внутри профиля
            if (full.StartsWith(@"c:\users\", StringComparison.Ordinal) &&
                full.Count(c => c == '\\') <= 3)
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    // ===================== ПЕРЕКЛЮЧАТЕЛЬ =====================

    /// <summary>
    /// Режим OptiScaler: игра умеет только DLSS SR. OptiScaler делает из него фреймген
    /// ([FrameGen] FGInput=upscaler + FGOutput=dlssg), а MFG на Ampere отдаёт dlssg_sm86
    /// ([DLSSG] AmpereMfgUnlock=true, AmpereMfgMode=Internal).
    /// </summary>
    private TensorToggleResult ToggleOptiScaler(ActiveGameInfo game, string warning)
    {
        if (!IsOptiScalerReady())
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "⚡ OPTIFG",
                $"Пак OptiScaler не найден: {OptiScalerSourceDirectory}");
        }

        string? proxy = FindOptiScalerProxy(game.Directory);
        bool installed = proxy is not null;

        if (!installed)
        {
            // Убираем конкурирующий прокси режима A: version.dll + dlssg_sm86.ini.
            // Две подмены nvngx_dlssg сразу (своя и OptiScaler Internal) дерутся за хуки NGX.
            string cleaned = RemoveProxy(game.Directory);
            try
            {
                string legacyIni = Path.Combine(game.Directory, IniFileName);
                if (File.Exists(legacyIni)) File.Delete(legacyIni);
            }
            catch
            {
                // игра держит файл — уйдёт после её закрытия
            }

            InstallOptiScaler(game.Directory, frameGenEnabled: true);
            RememberGame(game);

            return new TensorToggleResult(TensorToggleAction.Installed,
                "⚡ OPTIFG УСТАНОВЛЕН",
                $"{game.ProcessName} · перезапустите игру, затем INSERT → Frame Gen{cleaned}{warning}");
        }

        // Повторная уборка: если прокси режима A остался (был занят запущенной игрой),
        // стираем его при каждом нажатии — тогда после закрытия игры хватает одного клика.
        string cleanedAgain = RemoveProxy(game.Directory);
        try
        {
            string legacyIni = Path.Combine(game.Directory, IniFileName);
            if (File.Exists(legacyIni)) File.Delete(legacyIni);
        }
        catch { }

        bool current = ReadOptiScalerFrameGen(game.Directory) ?? true;
        bool next = !current;

        WriteOptiScalerConfig(game.Directory, next);

        if (next && !File.Exists(Path.Combine(game.Directory, proxy!)))
        {
            // прокси кто-то удалил — возвращаем, иначе не будет ни FG, ни меню INSERT
            CopyFile(Path.Combine(OptiScalerSourceDirectory, "OptiScaler.dll"),
                     Path.Combine(game.Directory, proxy!));
        }

        // ВАЖНО: прокси НЕ удаляем ни при включении, ни при выключении.
        // Именно dxgi.dll даёт меню INSERT, которым фреймген переключается на лету;
        // без него «выключено» стало бы необратимым до перезапуска игры.

        RememberGame(game);

        return next
            ? new TensorToggleResult(TensorToggleAction.Enabled,
                "⚡ OPTIFG: ВКЛЮЧЕН",
                $"{game.ProcessName} · в игре INSERT → Frame Gen{warning}")
            : new TensorToggleResult(TensorToggleAction.Disabled,
                "⚡ OPTIFG: ВЫКЛЮЧЕН",
                $"{game.ProcessName} · в игре INSERT → Frame Gen{warning}");
    }

    /// <summary>Долгое удержание [N]: включить/выключить Tensor-фреймген для игры на экране.</summary>
    public TensorToggleResult ToggleForActiveGame()
    {
        try
        {
            if (!IsModSourceReady())
            {
                return new TensorToggleResult(TensorToggleAction.Failed,
                    "⚡ TENSOR FRAMEGEN",
                    $"Папка мода не найдена: {ModSourceDirectory}");
            }

            ActiveGameInfo? game = ResolveTargetGame();
            if (game is null)
            {
                return new TensorToggleResult(TensorToggleAction.NoGame,
                    "⚡ TENSOR FRAMEGEN",
                    "Активная игра не найдена — запустите игру");
            }

            if (HasAntiCheatOrPreloader(game.Directory, out string reason) && !IgnoreAntiCheatWarnings)
            {
                return new TensorToggleResult(TensorToggleAction.BlockedByAntiCheat,
                    "🛡 EAC ЗАЩИТА",
                    $"Фреймген отключен · {reason} · {game.ProcessName} · доступен только LIVE FLOW");
            }

            string warning = IgnoreAntiCheatWarnings &&
                             HasAntiCheatOrPreloader(game.Directory, out _)
                ? " · ⚠ без гарантий"
                : string.Empty;

            // ВАЖНО: ставим ВСЕГДА в папку рабочего .exe (exeDir) — Windows ищет version.dll
            // именно рядом с запущенным процессом. Для UE это <Game>\Binaries\Win64\.
            // Никаких отказов «нет DLSS / игра не просит FG»: решение принимает пользователь,
            // нажав кнопку. DLSS-маркеры используются только для текста OSD.
            string exeDir = game.Directory;
            string iniPath = Path.Combine(exeDir, IniFileName);
            string gameRoot = GetGameRoot(exeDir);

            bool hasAnyDlss = HasAnyDlss(exeDir);
            bool hasNativeFg = HasNativeFrameGen(exeDir);

            // Единственная защита: убираем OptiScaler-остатки, иначе двойной хук sl.dlss_g.dll
            // = гарантированный 0xC0000005.
            string conflict = RemoveOptiScalerLeftovers(exeDir);

            bool hasPayload = HasModPayload(exeDir);

            // Выбор прокси.
            string? installedProxy = GetInstalledProxy(exeDir);
            string proxy = installedProxy ?? TensorProxyOrder[0];

            // Лог есть -> этот прокси доказанно работает, запоминаем и больше не перебираем.
            if (installedProxy is not null && ModLogExists(exeDir))
            {
                proxy = installedProxy;
                SaveProxyMarker(exeDir, installedProxy, good: true);
            }
            // Логов нет и прокси не доказан -> пробуем следующее имя (но не чаще, чем раз в 2 минуты).
            else if (hasPayload && installedProxy is not null && !MarkerGood(exeDir) &&
                     MarkerAgeSeconds(exeDir) > 120)
            {
                proxy = AdvanceProxy(installedProxy);
            }

            if (!hasPayload)
            {
                CopyModFiles(exeDir, includeSuperResolution: CopySuperResolutionFiles, proxy);
                WriteIni(iniPath, enabled: true, superResolutionMode: SuperResolutionMode);
                RememberGame(game);

                return new TensorToggleResult(TensorToggleAction.Installed,
                    "⚡ TENSOR FG + INT8: ВКЛЮЧЕН",
                    $"{game.ProcessName} · перезапустите игру для подхвата DLL{conflict}{warning}");
            }

            // Источник истины — наличие файла-прокси, а не только INI.
            // Иначе INI=1 при отсутствующем прокси делал кнопку односторонней (только «выключить»).
            bool proxyPresent = installedProxy is not null && File.Exists(Path.Combine(exeDir, installedProxy));
            bool currentlyEnabled = proxyPresent && (ReadEnabled(iniPath) ?? true);
            bool next = !currentlyEnabled;

            if (next)
            {
                // Включение: уводим все прочие прокси в .off (иначе два хука = крах),
                // возвращаем нужный, ставим Enabled=1.
                foreach (string other in TensorProxyOrder)
                {
                    if (other.Equals(proxy, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        string path = Path.Combine(exeDir, other);
                        if (File.Exists(path))
                        {
                            DeleteOrSchedule(path + ".off");
                            File.Move(path, path + ".off", overwrite: true);
                        }
                    }
                    catch
                    {
                        // занят игрой — уйдёт при следующем нажатии
                    }
                }

                // Пробуем выбранный прокси, а если его имя занято чужим файлом игры —
                // берём следующее из списка (маркер запоминает реально установленный).
                bool copied = false;
                string chosen = proxy;

                foreach (string candidate in TensorProxyOrder)
                {
                    if (!candidate.Equals(proxy, StringComparison.OrdinalIgnoreCase) &&
                        !IsSameOrLater(candidate, proxy)) continue;

                    DeleteOrSchedule(Path.Combine(exeDir, candidate + ".off"));

                    if (CopyProxy(exeDir, candidate))
                    {
                        chosen = candidate;
                        copied = true;
                        break;
                    }
                }

                if (!copied)
                {
                    return new TensorToggleResult(TensorToggleAction.Failed,
                        "⚡ TENSOR FRAMEGEN",
                        $"{game.ProcessName} · не удалось положить прокси (имена заняты файлами игры)");
                }

                CopyModFiles(exeDir, includeSuperResolution: CopySuperResolutionFiles, chosen);
                WriteIni(iniPath, enabled: true, superResolutionMode: SuperResolutionMode);
                RememberGame(game);

                return new TensorToggleResult(TensorToggleAction.Enabled,
                    "⚡ TENSOR FG + INT8: ВКЛЮЧЕН",
                    $"{game.ProcessName} · {chosen} · перезапустите игру для подхвата DLL{conflict}{warning}");
            }

            // Выключение: Enabled=0 и прокси уводится в <имя>.off.
            // Переименование работает даже для DLL, загруженной процессом игры (File.Delete — нет).
            WriteIni(iniPath, enabled: false, superResolutionMode: SuperResolutionMode);
            string proxyNote = DisableProxyNamed(exeDir, proxy);
            RememberGame(game);

            return new TensorToggleResult(TensorToggleAction.Disabled,
                "⚡ TENSOR FG: ВЫКЛЮЧЕН",
                $"{game.ProcessName}{proxyNote}{warning}");
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "⚡ TENSOR FRAMEGEN",
                "Ошибка: " + ex.Message);
        }
    }

    /// <summary>Текущее состояние мода для игры на экране (для UI/диагностики).</summary>
    public static bool? GetActiveGameFrameGenState()
    {
        try
        {
            ActiveGameInfo? game = GetActiveGame();
            if (game is null) return null;

            string iniPath = Path.Combine(game.Directory, IniFileName);
            if (!File.Exists(iniPath)) return false;

            return ReadEnabled(iniPath);
        }
        catch
        {
            return null;
        }
    }

    // ===================== ПРЕСТЕЙДЖИНГ =====================

    /// <summary>
    /// Фоновая тихая установка мода в СПЯЩЕМ режиме ([General] Enabled=0) во все найденные игры
    /// с nvngx_dlss.dll и без античита: к моменту запуска игры version.dll уже подхватится процессом,
    /// и долгий тап [N] будет переключать только строку Enabled в INI.
    /// </summary>
    public async Task<int> PrestageModInGamesAsync()
    {
        CancellationToken token = _cts.Token;

        if (!IsModSourceReady()) return 0;

        return await Task.Run(() =>
        {
            int installed = 0;

            try
            {
                DateTime deadline = DateTime.UtcNow + PrestageTimeBudget;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string root in EnumerateGameRoots())
                {
                    if (token.IsCancellationRequested || DateTime.UtcNow > deadline) break;
                    if (installed >= MaxPrestageGames) break;

                    foreach (string gameDir in EnumerateCandidateGameDirs(root, token))
                    {
                        if (token.IsCancellationRequested || DateTime.UtcNow > deadline) break;
                        if (installed >= MaxPrestageGames) break;

                        try
                        {
                            if (!visited.Add(gameDir)) continue;
                            if (!LooksLikeDlssGame(gameDir)) continue;
                            if (HasAntiCheatOrPreloader(gameDir, out _) && !IgnoreAntiCheatWarnings) continue;
                            if (HasModPayload(gameDir)) continue;

                            // Игра уже на OptiFG: туда нельзя докладывать version.dll — две подмены
                            // nvngx_dlssg сразу валят Streamline (краш в sl.dlss_g.dll).
                            // Также не трогаем игры, где уже выбран RTX Encore / Smooth Motion.
                            if (File.Exists(Path.Combine(gameDir, OptiScalerIniName)) ||
                                File.Exists(Path.Combine(gameDir, "dxgi.dll")) ||
                                IsEncoreEngineInstalled(gameDir))
                                continue;

                            CopyModFiles(gameDir, includeSuperResolution: false);

                            // СПЯЩИЙ режим: прокси уже на месте, но мод ничего не делает.
                            WriteIni(Path.Combine(gameDir, IniFileName), enabled: false, superResolutionMode: "Off");
                            installed++;
                        }
                        catch
                        {
                            // одна игра не должна ломать весь проход
                        }
                    }
                }
            }
            catch
            {
                // престейджинг никогда не должен ронять приложение
            }

            return installed;
        }, token).ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateGameRoots()
    {
        string[] relativeRoots =
        {
            @"SteamLibrary\steamapps\common",
            @"SteamLibrary",
            @"Steam\steamapps\common",
            @"Program Files (x86)\Steam\steamapps\common",
            @"Games",
            @"Epic Games",
            @"Program Files\Epic Games",
            @"Program Files (x86)\Epic Games",
            @"GOG Games",
            @"Program Files (x86)\GOG Galaxy\Games",
            @"XboxGames"
        };

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            bool ready;
            DriveType type;
            try
            {
                ready = drive.IsReady;
                type = drive.DriveType;
            }
            catch
            {
                continue;
            }

            if (!ready || type != DriveType.Fixed) continue;

            string root;
            try
            {
                root = drive.RootDirectory.FullName;
            }
            catch
            {
                continue;
            }

            foreach (string rel in relativeRoots)
            {
                string path = Path.Combine(root, rel);
                bool exists;
                try { exists = Directory.Exists(path); }
                catch { exists = false; }

                if (exists) yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateGameDirs(string root, CancellationToken token)
    {
        IEnumerable<string> firstLevel;
        try
        {
            firstLevel = Directory.EnumerateDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (string gameDir in firstLevel)
        {
            if (token.IsCancellationRequested) yield break;

            // сам каталог игры может быть контейнером (Games\MyGame\MyGame)
            yield return gameDir;

            string? nested = FindNestedExecutableDir(gameDir);
            if (nested is not null) yield return nested;
        }
    }

    /// <summary>UE-игры держат dll и exe на 1–3 уровня ниже (Binaries\Win64 и т.п.).</summary>
    private static string? FindNestedExecutableDir(string gameDir)
    {
        try
        {
            string[] direct = { "Binaries\\Win64", "Binaries\\Win32", "bin", "x64", "x86", "game", "Game", "win64" };

            foreach (string sub in direct)
            {
                string path = Path.Combine(gameDir, sub);
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "nvngx_dlss.dll")))
                    return path;
            }

            // 2 уровня вглубь — на случай нестандартной раскладки
            foreach (string lvl1 in Directory.EnumerateDirectories(gameDir))
            {
                if (File.Exists(Path.Combine(lvl1, "nvngx_dlss.dll"))) return lvl1;

                foreach (string lvl2 in Directory.EnumerateDirectories(lvl1))
                {
                    if (File.Exists(Path.Combine(lvl2, "nvngx_dlss.dll"))) return lvl2;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Игра вообще пользуется DLSS: рядом лежит nvngx_dlss.dll (или её подпапка).</summary>
    private static bool LooksLikeDlssGame(string gameDir)
    {
        try
        {
            if (File.Exists(Path.Combine(gameDir, "nvngx_dlss.dll"))) return true;
            if (File.Exists(Path.Combine(gameDir, "nvngx_dlssg.dll"))) return true;

            foreach (string sub in Directory.EnumerateDirectories(gameDir))
            {
                if (File.Exists(Path.Combine(sub, "nvngx_dlss.dll"))) return true;
                if (File.Exists(Path.Combine(sub, "nvngx_dlssg.dll"))) return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    // ===================== ФАЙЛЫ МОДА =====================

    /// <summary>
    /// Полезная нагрузка мода на месте (INI + папка dlssg_sm86). Прокси version.dll может
    /// отсутствовать — это и есть «выключено» после быстрого выключения.
    /// </summary>
    public static bool HasModPayload(string gameDir)
    {
        try
        {
            return File.Exists(Path.Combine(gameDir, IniFileName))
                   && Directory.Exists(Path.Combine(gameDir, ModFolderName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Прокси-библиотека мода (version.dll) лежит в папке игры.</summary>
    public static bool HasModFiles(string gameDir)
    {
        try
        {
            return File.Exists(Path.Combine(gameDir, ProxyDllName)) && HasModPayload(gameDir);
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // занято — не критично
        }
    }

    /// <summary>Удаляет файл, а если он ещё замаплен игрой — планирует удаление на перезагрузку.</summary>
    private static void DeleteOrSchedule(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
        }
        catch
        {
            return;
        }

        DeleteQuietly(path);

        try
        {
            if (File.Exists(path)) MoveFileEx(path, null, MoveFileDelayUntilReboot);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Убирает остатки OptiScaler из папки игры. Две подмены nvngx_dlssg одновременно
    /// (наш dlssg_sm86 и OptiScaler Internal) валят Streamline — 0xC0000005 в sl.dlss_g.dll.
    /// Трогаем только то, что ставили мы: по OptiScaler.ini / нашему маркеру.
    /// </summary>
    private static string RemoveOptiScalerLeftovers(string gameDir)
    {
        try
        {
            string marker = Path.Combine(gameDir, OptiScalerMarker);
            string ini = Path.Combine(gameDir, OptiScalerIniName);
            string folder = Path.Combine(gameDir, OptiScalerFolderName);

            bool ours = File.Exists(marker) || File.Exists(ini) ||
                        Directory.Exists(Path.Combine(folder, "dlssg_sm86"));

            if (!ours) return string.Empty;

            foreach (string proxy in ProxyCandidates)
            {
                if (proxy == ProxyDllName) continue; // version.dll — это наш мод, не трогаем
                RemoveProxyFile(Path.Combine(gameDir, proxy));
            }

            DeleteQuietly(Path.Combine(gameDir, OptiScalerIniName));
            DeleteQuietly(Path.Combine(gameDir, "OptiScaler.log"));
            DeleteQuietly(Path.Combine(gameDir, OptiScalerMarker));

            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch
            {
                // папка занята — уйдёт позже
            }

            return " · убран OptiScaler";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Выключает прокси: version.dll → version.dll.off. Переименование проходит даже когда
    /// DLL загружена процессом игры (в отличие от File.Delete), и Windows больше не подхватит
    /// её при следующем запуске. Никакого реестра PendingFileRenameOperations.
    /// </summary>
    private static string DisableProxy(string gameDir)
    {
        return DisableProxyNamed(gameDir, GetInstalledProxy(gameDir) ?? ProxyDllName);
    }

    private static string DisableProxyNamed(string gameDir, string proxyName)
    {
        string proxy = Path.Combine(gameDir, proxyName);
        string off = proxy + ".off";

        try
        {
            if (!File.Exists(proxy)) return " · фреймген выключен";
        }
        catch
        {
            return string.Empty;
        }

        try
        {
            DeleteQuietly(off);
            File.Move(proxy, off, overwrite: true);
            return " · version.dll → version.dll.off";
        }
        catch
        {
            // fallback: обычное удаление (если игра не держит файл)
        }

        try
        {
            File.Delete(proxy);
            return " · version.dll удалён";
        }
        catch
        {
            // fallback: удаление при следующей перезагрузке
        }

        try
        {
            if (MoveFileEx(proxy, null, MoveFileDelayUntilReboot))
                return " · version.dll удалится после перезагрузки";
        }
        catch { }

        return " · version.dll занят игрой";
    }

    /// <summary>
    /// Убирает только прокси-файл, оставляя тяжёлые папки на месте.
    /// Если игра запущена, DLL загружена и файл занят — удаление откладывается до перезагрузки.
    /// </summary>
    private static string RemoveProxy(string gameDir, string proxyName = ProxyDllName)
    {
        string proxy = Path.Combine(gameDir, proxyName);

        try
        {
            if (!File.Exists(proxy)) return string.Empty;
        }
        catch
        {
            return string.Empty;
        }

        try
        {
            File.Delete(proxy);
            return " · version.dll убран";
        }
        catch
        {
            // игра держит DLL: помечаем на удаление при следующей перезагрузке
            try
            {
                if (MoveFileEx(proxy, null, MoveFileDelayUntilReboot))
                    return " · version.dll удалится после перезагрузки";
            }
            catch { }

            return " · version.dll занят игрой";
        }
    }

    /// <summary>
    /// Копирует только то, что реально нужно игре: прокси version.dll, INI и рабочие файлы
    /// dlssg_sm86 (runtime + backend + kernels; sr\ — только если нужен тензорный апскейл).
    /// </summary>
    /// <summary>
    /// Порядок прокси. version.dll не работает в играх, где он к моменту загрузки EOSSDK
    /// уже сидит в памяти из System32 (ARC Raiders: импорт есть только у EOSSDK).
    /// Поэтому дальше идут winmm (импортируется EOSSDK и discord_partner_sdk), dinput8, dbghelp,
    /// и последними — render-path прокси dxgi/d3d12.
    /// </summary>
    private static readonly string[] TensorProxyOrder =
    {
        "version.dll", "winmm.dll", "dinput8.dll", "dbghelp.dll", "dxgi.dll", "d3d12.dll"
    };

    private const string ProxyMarkerName = ".leno4ka-tensor.json";

    /// <summary>Имя прокси, которым мы поставили мод (из маркера), или null.</summary>
    public static string? GetInstalledProxy(string gameDir)
    {
        try
        {
            string marker = Path.Combine(gameDir, ProxyMarkerName);
            if (File.Exists(marker))
            {
                string text = File.ReadAllText(marker);

                foreach (string proxy in AllProxyNames)
                {
                    if (text.Contains("\"" + proxy + "\"", StringComparison.OrdinalIgnoreCase) &&
                        (File.Exists(Path.Combine(gameDir, proxy)) ||
                         File.Exists(Path.Combine(gameDir, proxy + ".off"))))
                        return proxy;
                }
            }
        }
        catch
        {
            // ignore
        }

        // маркера нет — ищем любой НАШ прокси по размеру (файл игры с тем же именем
        // имеет другой размер и никогда не считается нашим).
        foreach (string proxy in AllProxyNames)
        {
            try
            {
                string path = Path.Combine(gameDir, proxy);
                if (File.Exists(path) && FileMatchesKnownProxy(path)) return proxy;
                if (File.Exists(path + ".off") && FileMatchesKnownProxy(path + ".off")) return proxy;
            }
            catch { }
        }

        return null;
    }

    /// <summary>Сколько секунд назад ставился мод (по маркеру).</summary>
    private static double MarkerAgeSeconds(string gameDir)
    {
        try
        {
            string marker = Path.Combine(gameDir, ProxyMarkerName);
            if (!File.Exists(marker)) return double.MaxValue;

            return (DateTime.UtcNow - File.GetLastWriteTimeUtc(marker)).TotalSeconds;
        }
        catch
        {
            return double.MaxValue;
        }
    }

    /// <summary>Мод уже загружался в этой игре (есть лог).</summary>
    private static bool ModLogExists(string gameDir)
    {
        try
        {
            return File.Exists(Path.Combine(gameDir, ModFolderName, "logs", "dlssg_sm86.log"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>candidate стоит в порядке перебора не раньше baseline.</summary>
    private static bool IsSameOrLater(string candidate, string baseline)
    {
        int index = Array.FindIndex(TensorProxyOrder, p => p.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        int start = Array.FindIndex(TensorProxyOrder, p => p.Equals(baseline, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && start >= 0 && index >= start;
    }

    /// <summary>Следующий прокси в порядке перебора (для случая «игра не загрузила DLL»).</summary>
    public static string AdvanceProxy(string? current)
    {
        int index = current is null ? -1 : Array.FindIndex(TensorProxyOrder, p =>
            p.Equals(current, StringComparison.OrdinalIgnoreCase));

        return TensorProxyOrder[Math.Clamp(index + 1, 0, TensorProxyOrder.Length - 1)];
    }

    private static void SaveProxyMarker(string gameDir, string proxy, bool good = false, string engine = "classic",
        int maxFrames = 3, string superResolution = "M-INT8", string smoothApi = "auto")
    {
        try
        {
            File.WriteAllText(Path.Combine(gameDir, ProxyMarkerName),
                "{\"engine\":\"" + engine + "\",\"proxy\":\"" + proxy + "\",\"good\":" + (good ? "true" : "false") +
                ",\"maxFrames\":" + Math.Clamp(maxFrames, 0, 5) +
                ",\"sr\":\"" + superResolution + "\",\"smoothApi\":\"" + smoothApi + "\"" +
                ",\"by\":\"Leno4kaControl\"}");
        }
        catch { }
    }

    /// <summary>Этот прокси уже доказанно загружался игрой (лог мода существовал).</summary>
    private static bool MarkerGood(string gameDir)
    {
        try
        {
            string marker = Path.Combine(gameDir, ProxyMarkerName);
            return File.Exists(marker) && File.ReadAllText(marker).Contains("\"good\":true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Копирует нужный прокси под его именем (для не-version берём alternatives\).
    /// Возвращает false, если в папке игры уже лежит ЧУЖОЙ файл с таким именем
    /// (например, CoD везёт свой dbghelp.dll 1.5 МБ) — его перезаписывать нельзя.
    /// </summary>
    private static bool CopyProxy(string gameDir, string proxy)
    {
        string src = ModSourceDirectory;

        string source = proxy.Equals(ProxyDllName, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(src, ProxyDllName)
            : Path.Combine(src, "alternatives", proxy);

        string destination = Path.Combine(gameDir, proxy);

        try
        {
            if (!File.Exists(source)) return false;

            if (File.Exists(destination))
            {
                long ours = new FileInfo(source).Length;
                long там = new FileInfo(destination).Length;

                // файл другой длины = это файл самой игры, трогать нельзя
                if (там != ours) return false;
            }
        }
        catch
        {
            return false;
        }

        CopyFile(source, destination);
        return true;
    }

    private static void CopyModFiles(string gameDir, bool includeSuperResolution, string proxy = ProxyDllName)
    {
        string src = ModSourceDirectory;

        Directory.CreateDirectory(gameDir);

        CopyProxy(gameDir, proxy);
        SaveProxyMarker(gameDir, proxy);

        string srcMod = Path.Combine(src, ModFolderName);
        string dstMod = Path.Combine(gameDir, ModFolderName);

        string[] required =
        {
            @"runtime\nvngx_dlssg.dll",
            @"backend\sm86_backend.dll",
            @"kernels\fg_kernels.dll"
        };

        foreach (string rel in required)
        {
            CopyFile(Path.Combine(srcMod, rel), Path.Combine(dstMod, rel));
        }

        // INI всегда должен начинаться с ПОЛНОГО шаблона из пака (со всеми секциями и комментариями).
        // Иначе WriteIni создаёт «минимальный» файл из трёх ключей, и мод теряет дефолты.
        string dstIni = Path.Combine(gameDir, IniFileName);

        try
        {
            bool needTemplate = !File.Exists(dstIni) || new FileInfo(dstIni).Length < 1024;
            if (needTemplate) CopyFile(Path.Combine(src, IniFileName), dstIni, force: true);
        }
        catch
        {
            // ignore
        }

        if (includeSuperResolution)
        {
            CopyFile(Path.Combine(srcMod, @"sr\nvngx_dlss.dll"), Path.Combine(dstMod, @"sr\nvngx_dlss.dll"));
            CopyFile(Path.Combine(srcMod, @"sr\sr_kernels.dll"), Path.Combine(dstMod, @"sr\sr_kernels.dll"));
        }
    }

    private static void CopyFile(string source, string destination, bool force = false)
    {
        try
        {
            if (!File.Exists(source)) return;
            if (File.Exists(destination) && !force)
            {
                var srcInfo = new FileInfo(source);
                var dstInfo = new FileInfo(destination);
                if (srcInfo.Length == dstInfo.Length) return; // уже на месте
            }

            string? dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Copy(source, destination, overwrite: true);
        }
        catch
        {
            // файл занят игрой / нет прав — не критично на этом шаге
        }
    }

    // ===================== INI =====================

    public static bool? ReadEnabled(string iniPath)
    {
        string? value = ReadIniValue(iniPath, "General", "Enabled");
        if (value is null) return null;
        if (value == "1") return true;
        if (value == "0") return false;
        return null;
    }

    /// <summary>
    /// Правит dlssg_sm86.ini: [General] Enabled, [FrameGeneration] MaxGeneratedFrames,
    /// [SuperResolution] Mode. Комментарии и прочие ключи сохраняются как есть.
    /// </summary>
    private static void WriteIni(string iniPath, bool enabled, string superResolutionMode, int maxFrames = 3)
    {
        List<string> lines;
        Encoding encoding;

        try
        {
            (lines, encoding) = ReadIniLines(iniPath);
        }
        catch
        {
            return;
        }

        SetIniValue(lines, "General", "Enabled", enabled ? "1" : "0");
        SetIniValue(lines, "FrameGeneration", "MaxGeneratedFrames", Math.Clamp(maxFrames, 0, 5).ToString());
        SetIniValue(lines, "SuperResolution", "Mode", superResolutionMode);

        try
        {
            File.WriteAllLines(iniPath, lines, encoding);
        }
        catch
        {
            // нет прав / файл занят
        }
    }

    private static (List<string> Lines, Encoding Encoding) ReadIniLines(string path)
    {
        if (!File.Exists(path))
            return (new List<string>(), new UTF8Encoding(false));

        byte[] bytes = File.ReadAllBytes(path);

        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text = hasBom
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

        var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));

        // файл заканчивался переводом строки — не размножаем пустые строки при перезаписи
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        Encoding encoding = hasBom ? new UTF8Encoding(true) : new UTF8Encoding(false);
        return (lines, encoding);
    }

    private static string? ReadIniValue(string path, string section, string key)
    {
        try
        {
            (List<string> lines, _) = ReadIniLines(path);
            string current = string.Empty;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    current = line[1..^1].Trim();
                    continue;
                }

                if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line[(eq + 1)..].Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void SetIniValue(List<string> lines, string section, string key, string value)
    {
        string current = string.Empty;
        int sectionHeaderIndex = -1;
        int insertAt = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                current = line[1..^1].Trim();
                if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionHeaderIndex = i;
                    insertAt = i + 1;
                }
                continue;
            }

            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }

            insertAt = i + 1;
        }

        if (sectionHeaderIndex < 0)
        {
            // секции нет — создаём в конце файла
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        if (insertAt < 0) insertAt = sectionHeaderIndex + 1;
        lines.Insert(insertAt, $"{key}={value}");
    }

    public void Dispose()
    {
        try
        {
            _foregroundTracker.Dispose();
        }
        catch { }

        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch { }
    }
}

