using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CHZMFRAMEUNLOCK1.Services;

/// <summary>Три движка, между которыми переключается CHZM Frame Unlock.</summary>
public enum EngineMode
{
    /// <summary>Проверенный пак DLSS\ (dlssg_sm86 v0.4.1): 4X + INT8, без оверлея.</summary>
    ClassicSm86,

    /// <summary>RTX Encore: оверлейное меню INSERT, MFG 2X-6X, Dynamic, Preset C, NR, Smooth Motion.</summary>
    RtxEncore,

    /// <summary>Smooth Motion: драйверная интерполяция для DX11/DX12/Vulkan, DLSS в игре не нужен.</summary>
    SmoothMotion
}

public sealed record EngineStatus(
    EngineMode? Mode,
    string Proxy,
    bool Installed,
    bool Enabled,
    int MaxFrames,
    string SuperResolution,
    string Detail);

/// <summary>Содержимое chzm-settings.json (совместимо со старым однострочным файлом).</summary>
public sealed class ChzmSettingsData
{
    public string Engine { get; set; } = "classic";
    public int MaxFrames { get; set; } = 3;
    public string SuperResolution { get; set; } = "M-INT8";
    public string SmoothApi { get; set; } = "auto";
    public int MenuKey { get; set; } = 0x77;
    public string MenuKeyName { get; set; } = "F8";
    public int LiveFlowKey { get; set; } = 0x78;
    public string LiveFlowKeyName { get; set; } = "F9";
    public int LiveFlowMultiplier { get; set; } = 2;
    public bool LiveFlowQualityPreset { get; set; } = true;
    public bool LiveFlowLowLatency { get; set; } = true;
    public bool LiveFlowKeepStill { get; set; } = true;
    public bool ShowEncoreMenu { get; set; }
    public bool IgnoreAntiCheatWarnings { get; set; }
    public Dictionary<string, string> ManualGames { get; set; } = new();
    public string By { get; set; } = "Leno4kaControl";
}

/// <summary>
/// Движки и полная очистка. Частичный класс: вся проверенная логика поиска игры,
/// античита и Classic SM86 осталась в TensorAutoService.cs без изменений.
/// </summary>
public sealed partial class TensorAutoService
{
    // ===================== ИСТОЧНИКИ ПАКОВ =====================

    public const string SupplementFolderName = "ДОПОЛНЕНИЕ";

    private const string EncoreFolderName = "rtx-encore";
    private const string EncoreDllName = "rtx-encore.dll";
    private const string EncoreConfigName = "rtx-encore.jsonc";
    private const string EncoreConfigBackupName = "rtx-encore.jsonc.chzm-backup";
    private const string EncoreLogsFolderName = "rtx-encore-logs";
    private const string EncoreCapturesFolderName = "rtx-encore-captures";

    private const string SmoothMotionFolderName = "smooth-motion";
    private const string SmoothMotionCliName = "sm86.exe";
    private const string SmoothMotionGuiName = "SmoothMotionSM86.exe";
    private const string SmoothRtx30FolderName = "rtx30";
    private const string SmoothRtx30DllName = "version.dll";
    private const long SmoothRtx30KnownSize = 1064448;

    private const string EngineClassic = "classic";
    private const string EngineEncore = "rtx-encore";
    private const string EngineSmooth = "smooth-motion";

    private const string ChzmSettingsName = "chzm-settings.json";

    /// <summary>Корень папки ДОПОЛНЕНИЕ: рядом с EXE либо в корне проекта при dev-запуске.</summary>
    public static string SupplementSourceRoot
    {
        get
        {
            var candidates = new List<string>();

            try
            {
                candidates.Add(Path.Combine(AppContext.BaseDirectory, SupplementFolderName));

                string? dir = AppContext.BaseDirectory;
                for (int i = 0; i < 5 && dir is not null; i++)
                {
                    dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
                    if (dir is null) break;
                    candidates.Add(Path.Combine(dir, SupplementFolderName));
                }
            }
            catch
            {
                // ignore
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (Directory.Exists(candidate)) return candidate;
                }
                catch
                {
                    // ignore
                }
            }

            return candidates.Count > 0 ? candidates[0] : Path.Combine(AppContext.BaseDirectory, SupplementFolderName);
        }
    }

    public static string EncoreSourceDirectory => Path.Combine(SupplementSourceRoot, EncoreFolderName);
    public static string SmoothMotionSourceDirectory => Path.Combine(SupplementSourceRoot, SmoothMotionFolderName);
    public static string SmoothRtx30SourceDirectory => Path.Combine(SmoothMotionSourceDirectory, SmoothRtx30FolderName);
    public static string PipoMfgSourceDirectory => Path.Combine(SupplementSourceRoot, "pipo-mfg");

    public static bool IsEncoreReady()
    {
        try
        {
            return File.Exists(Path.Combine(EncoreSourceDirectory, EncoreDllName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Готов ли новый драйверный комплект SmoothMotion 2.8.5 Fix37 (RTX 30).</summary>
    public static bool IsSmoothMotionRtx30Ready()
    {
        try
        {
            string dll = Path.Combine(SmoothRtx30SourceDirectory, SmoothRtx30DllName);
            return File.Exists(dll) && new FileInfo(dll).Length > 500_000;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSmoothMotionReady()
    {
        try
        {
            return File.Exists(Path.Combine(SmoothMotionSourceDirectory, SmoothMotionCliName)) ||
                   File.Exists(Path.Combine(SmoothMotionSourceDirectory, SmoothMotionGuiName)) ||
                   IsSmoothMotionRtx30Ready();
        }
        catch
        {
            return false;
        }
    }

    // ===================== ИМЕНА / РАЗМЕРЫ ПРОКСИ =====================

    private static readonly string[] EncoreProxyOrder =
    {
        "version.dll", "winmm.dll", "dxgi.dll", "dinput8.dll", "d3d12.dll",
        "d3d11.dll", "d3d9.dll", "winhttp.dll", "wininet.dll", "dsound.dll",
        "xinput1_3.dll", "xinput1_4.dll"
    };

    /// <summary>Все имена, которыми мы когда-либо ставили прокси (Classic + Encore).</summary>
    private static readonly string[] AllProxyNames =
    {
        "version.dll", "winmm.dll", "dinput8.dll", "dbghelp.dll", "dxgi.dll", "d3d12.dll",
        "d3d11.dll", "d3d9.dll", "winhttp.dll", "wininet.dll", "dsound.dll",
        "xinput1_3.dll", "xinput1_4.dll"
    };

    private static readonly Lazy<long[]> ClassicProxySizes = new(() =>
    {
        var sizes = new List<long>();
        try
        {
            void Add(string file)
            {
                try
                {
                    if (File.Exists(file)) sizes.Add(new FileInfo(file).Length);
                }
                catch { }
            }

            Add(Path.Combine(ModSourceDirectory, ProxyDllName));

            string alternatives = Path.Combine(ModSourceDirectory, "alternatives");
            if (Directory.Exists(alternatives))
            {
                foreach (string file in Directory.EnumerateFiles(alternatives, "*.dll"))
                    sizes.Add(new FileInfo(file).Length);
            }
        }
        catch
        {
            // ignore
        }

        return sizes.Distinct().ToArray();
    });

    private static readonly Lazy<long[]> EncoreProxySizes = new(() =>
    {
        var sizes = new List<long>();
        try
        {
            void Add(string file)
            {
                try
                {
                    if (File.Exists(file)) sizes.Add(new FileInfo(file).Length);
                }
                catch { }
            }

            string root = EncoreSourceDirectory;
            Add(Path.Combine(root, EncoreDllName));

            string alternatives = Path.Combine(root, "alternative-proxies");
            if (Directory.Exists(alternatives))
            {
                foreach (string file in Directory.EnumerateFiles(alternatives, "*.dll"))
                    sizes.Add(new FileInfo(file).Length);
            }
        }
        catch
        {
            // ignore
        }

        return sizes.Distinct().ToArray();
    });

    private static bool FileMatchesAny(long length, long[] known) => Array.IndexOf(known, length) >= 0;

    private static bool FileMatchesClassicProxy(string path)
    {
        try { return File.Exists(path) && FileMatchesAny(new FileInfo(path).Length, ClassicProxySizes.Value); }
        catch { return false; }
    }

    private static bool FileMatchesEncoreProxy(string path)
    {
        try { return File.Exists(path) && FileMatchesAny(new FileInfo(path).Length, EncoreProxySizes.Value); }
        catch { return false; }
    }

    /// <summary>Файл — один из наших прокси (Classic или Encore), а не файл самой игры.</summary>
    private static bool FileMatchesKnownProxy(string path)
        => FileMatchesClassicProxy(path) || FileMatchesEncoreProxy(path);

    private static string GetEncoreProxySource(string proxyName)
    {
        string alternative = Path.Combine(EncoreSourceDirectory, "alternative-proxies", proxyName);
        return File.Exists(alternative) ? alternative : Path.Combine(EncoreSourceDirectory, EncoreDllName);
    }

    // ===================== НАСТРОЙКИ UI =====================

    private static readonly JsonSerializerOptions ChzmJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };


    public EngineMode SelectedEngine { get; private set; } = EngineMode.ClassicSm86;
    public int ClassicMaxFrames { get; private set; } = 3;
    public string ClassicSuperResolution { get; private set; } = "M-INT8";
    public string SmoothMotionApi { get; private set; } = "auto";

    /// <summary>Виртуальный код клавиши, которой в игре открывается меню Encore (по умолчанию F8 = 0x77).</summary>
    public int MenuKey { get; private set; } = 0x77;
    public string MenuKeyName { get; private set; } = "F8";

    /// <summary>Клавиша мгновенного включения/выключения LIVE FLOW (по умолчанию F9 = 0x78).</summary>
    public int LiveFlowKey { get; private set; } = 0x78;
    public string LiveFlowKeyName { get; private set; } = "F9";

    // LIVE FLOW (через установленный DLSS 5 MANAGER)
    public int LiveFlowMultiplier { get; private set; } = 2;
    public bool LiveFlowQualityPreset { get; private set; } = true;
    public bool LiveFlowLowLatency { get; private set; } = true;
    public bool LiveFlowKeepStill { get; private set; } = true;

    /// <summary>Показывать внутриигровое меню Encore (по умолчанию выключено).</summary>
    public bool ShowEncoreMenu { get; private set; }

    /// <summary>
    /// Разрешить все моды в играх с античитом (галочка пользователя).
    /// Сохраняется в chzm-settings.json.
    /// </summary>
    public bool IgnoreAntiCheatWarnings { get; private set; }

    // Ручной выбор режима запоминается ПО ИГРЕ (полный путь exe), чтобы автопилот
    // не перебивал его при следующем запуске этой же игры.
    private readonly Dictionary<string, string> _manualGameModes = new(StringComparer.OrdinalIgnoreCase);

    public static string EngineDisplayName(EngineMode mode) => mode switch
    {
        EngineMode.RtxEncore => "RTX ENCORE",
        EngineMode.SmoothMotion => "SMOOTH MOTION",
        _ => "CLASSIC SM86"
    };

    public void SelectEngine(EngineMode mode) => SelectEngineAuto(mode);

    /// <summary>Выбор движка автопилотом — без записи ручного override для игры.</summary>
    public void SelectEngineAuto(EngineMode mode)
    {
        if (SelectedEngine == mode) return;

        SelectedEngine = mode;
        SaveChzmSettings();
    }

    /// <summary>Пользователь сам нажал режим — запоминаем это для конкретной игры.</summary>
    public void SelectEngineManual(EngineMode mode, string? gameExecutablePath)
    {
        SelectedEngine = mode;

        if (!string.IsNullOrWhiteSpace(gameExecutablePath))
            _manualGameModes[NormalizeGameKey(gameExecutablePath)] = EngineToString(mode);

        SaveChzmSettings();
    }

    public bool HasManualMode(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath)) return false;
        return _manualGameModes.ContainsKey(NormalizeGameKey(gameExecutablePath));
    }

    public EngineMode? GetManualMode(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath)) return null;

        return _manualGameModes.TryGetValue(NormalizeGameKey(gameExecutablePath), out string? value)
            ? EngineFromString(value)
            : null;
    }

    public void ClearManualMode(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath)) return;

        if (_manualGameModes.Remove(NormalizeGameKey(gameExecutablePath)))
            SaveChzmSettings();
    }

    public void SetMenuKey(int virtualKey, string displayName)
    {
        if (virtualKey < 8 || virtualKey > 255) return;

        MenuKey = virtualKey;
        MenuKeyName = string.IsNullOrWhiteSpace(displayName) ? $"VK {virtualKey}" : displayName.Trim();
        SaveChzmSettings();
    }

    public void SetLiveFlowKey(int virtualKey, string displayName)
    {
        if (virtualKey < 8 || virtualKey > 255) return;

        LiveFlowKey = virtualKey;
        LiveFlowKeyName = string.IsNullOrWhiteSpace(displayName) ? $"VK {virtualKey}" : displayName.Trim();
        SaveChzmSettings();
    }

    public void SetLiveFlowMultiplier(int multiplier)
    {
        LiveFlowMultiplier = Math.Clamp(multiplier, 2, 6);
        SaveChzmSettings();
    }

    public void SetLiveFlowQualityPreset(bool quality)
    {
        LiveFlowQualityPreset = quality;
        SaveChzmSettings();
    }

    public void SetLiveFlowLowLatency(bool enabled)
    {
        LiveFlowLowLatency = enabled;
        SaveChzmSettings();
    }

    public void SetLiveFlowKeepStill(bool enabled)
    {
        LiveFlowKeepStill = enabled;
        SaveChzmSettings();
    }

    public void SetIgnoreAntiCheatWarnings(bool value)
    {
        if (IgnoreAntiCheatWarnings == value) return;

        IgnoreAntiCheatWarnings = value;
        SaveChzmSettings();
    }

    /// <summary>
    /// Внутриигровое меню Encore: по умолчанию выключено, чтобы в игре не
    /// появлялось перегруженное английское окно. Включается только вручную.
    /// </summary>
    public void SetShowEncoreMenu(bool show, string? gameDirectory = null)
    {
        ShowEncoreMenu = show;
        SaveChzmSettings();

        try
        {
            string? dir = gameDirectory;

            if (string.IsNullOrWhiteSpace(dir))
                dir = ResolveTargetGame()?.Directory;

            if (!string.IsNullOrWhiteSpace(dir))
                SetEncoreInGameMenu(dir, show);
        }
        catch
        {
            // конфиг игры может быть занят — применится при следующей установке
        }
    }

    public LiveFlowOptions GetLiveFlowOptions() => new(
        Multiplier: 2,
        QualityPreset: true,
        LowLatency: LiveFlowLowLatency,
        KeepStillPixels: LiveFlowKeepStill,
        RealtimePriority: true);

    public void SelectSmoothMotionApi(string api)
    {
        string normalized = NormalizeApi(api);
        if (normalized == SmoothMotionApi) return;

        SmoothMotionApi = normalized;
        SaveChzmSettings();
    }

    /// <summary>Сменить API Smooth Motion и сразу переписать rtx-encore.jsonc (без toggle-логики).</summary>
    public TensorToggleResult SetSmoothMotionApi(string api, string? gameDirectory = null)
    {
        SmoothMotionApi = NormalizeApi(api);
        SaveChzmSettings();

        try
        {
            string? dir = gameDirectory;

            if (dir is null)
            {
                ActiveGameInfo? game = ResolveTargetGame();
                dir = game?.Directory;
            }

            if (dir is not null && IsRtx30SmoothProxy(dir))
            {
                return new TensorToggleResult(TensorToggleAction.Enabled,
                    "🚀 SMOOTH (RTXMFG)",
                    "RTXMFG Smooth Motion сам выбирает API драйвера · настройка не нужна");
            }

            string resolved = dir is null
                ? (SmoothMotionApi == "auto" ? "d3d12" : SmoothMotionApi)
                : ResolveApi(dir, SmoothMotionApi);

            if (dir is not null &&
                (File.Exists(Path.Combine(dir, EncoreConfigName)) || FindEncoreProxy(dir) is not null))
            {
                SetEncoreSmoothMotionConfig(dir, enabled: true, resolved);
            }

            return new TensorToggleResult(TensorToggleAction.Enabled,
                "🚀 SMOOTH MOTION",
                $"API: {resolved} · перезапустите игру");
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🚀 SMOOTH MOTION", "Ошибка: " + ex.Message);
        }
    }

    private static string NormalizeApi(string? api)
    {
        string value = (api ?? "auto").Trim().ToLowerInvariant();
        return value switch
        {
            "d3d12" or "dx12" => "d3d12",
            "d3d11" or "dx11" => "d3d11",
            "vulkan" or "vk" => "vulkan",
            _ => "auto"
        };
    }

    private static string ResolveApi(string gameDirectory, string preferred)
    {
        string api = NormalizeApi(preferred);
        if (api != "auto") return api;

        try
        {
            foreach (string dir in new[] { gameDirectory, GetGameRoot(gameDirectory) })
            {
                if (string.IsNullOrEmpty(dir)) continue;

                if (File.Exists(Path.Combine(dir, "vulkan-1.dll"))) return "vulkan";

                foreach (string file in Directory.EnumerateFiles(dir, "*.dll").Take(400))
                {
                    string name = Path.GetFileName(file).ToLowerInvariant();
                    if (name.Contains("vulkan") || name.StartsWith("dxvk", StringComparison.Ordinal))
                        return "vulkan";
                }
            }

            if (File.Exists(Path.Combine(gameDirectory, "nvngx_dlssg.dll"))) return "d3d12";

            foreach (string file in Directory.EnumerateFiles(gameDirectory, "d3d12*.dll").Take(20))
            {
                if (File.Exists(file)) return "d3d12";
            }
        }
        catch
        {
            // не смогли определить — самый частый современный путь
        }

        return "d3d12";
    }

    private void LoadChzmSettings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ChzmSettingsName);

        try
        {
            if (!File.Exists(path)) return;

            string text = File.ReadAllText(path);
            ChzmSettingsData? data = null;

            try
            {
                data = JsonSerializer.Deserialize<ChzmSettingsData>(text, ChzmJsonOptions);
            }
            catch
            {
                // старый/повреждённый формат — ниже разберём регулярками
            }

            if (data is null)
            {
                LoadLegacyChzmSettings(text);
                return;
            }

            EngineMode? engine = EngineFromString(data.Engine);
            if (engine is not null) SelectedEngine = engine.Value;

            ClassicMaxFrames = Math.Clamp(data.MaxFrames, 0, 5);

            if (IsValidSuperResolution(data.SuperResolution))
                ClassicSuperResolution = data.SuperResolution;

            SmoothMotionApi = NormalizeApi(data.SmoothApi);

            MenuKey = data.MenuKey is >= 8 and <= 255 ? data.MenuKey : 0x77;
            MenuKeyName = string.IsNullOrWhiteSpace(data.MenuKeyName) ? "F8" : data.MenuKeyName.Trim();

            LiveFlowKey = data.LiveFlowKey is >= 8 and <= 255 ? data.LiveFlowKey : 0x78;
            LiveFlowKeyName = string.IsNullOrWhiteSpace(data.LiveFlowKeyName) ? "F9" : data.LiveFlowKeyName.Trim();

            LiveFlowMultiplier = Math.Clamp(data.LiveFlowMultiplier, 2, 6);
            LiveFlowQualityPreset = data.LiveFlowQualityPreset;
            LiveFlowLowLatency = data.LiveFlowLowLatency;
            LiveFlowKeepStill = data.LiveFlowKeepStill;
            ShowEncoreMenu = data.ShowEncoreMenu;
            IgnoreAntiCheatWarnings = data.IgnoreAntiCheatWarnings;

            _manualGameModes.Clear();

            if (data.ManualGames is not null)
            {
                foreach (KeyValuePair<string, string> pair in data.ManualGames)
                {
                    EngineMode? manual = EngineFromString(pair.Value);
                    if (manual is null) continue;

                    _manualGameModes[NormalizeGameKey(pair.Key)] = EngineToString(manual.Value);
                }
            }
        }
        catch
        {
            // повреждённый файл настроек не должен мешать запуску
        }
    }

    private void LoadLegacyChzmSettings(string text)
    {
        try
        {
            EngineMode? engine = EngineFromString(ExtractMarkerValue(text, "engine"));
            if (engine is not null) SelectedEngine = engine.Value;

            string? frames = ExtractMarkerValue(text, "maxFrames");
            if (int.TryParse(frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxFrames))
                ClassicMaxFrames = Math.Clamp(maxFrames, 0, 5);

            string? sr = ExtractMarkerValue(text, "superResolution");
            if (IsValidSuperResolution(sr)) ClassicSuperResolution = sr!;

            string? api = ExtractMarkerValue(text, "smoothApi");
            if (api is not null) SmoothMotionApi = NormalizeApi(api);
        }
        catch
        {
            // ignore
        }
    }

    private void SaveChzmSettings()
    {
        try
        {
            var data = new ChzmSettingsData
            {
                Engine = EngineToString(SelectedEngine),
                MaxFrames = ClassicMaxFrames,
                SuperResolution = ClassicSuperResolution,
                SmoothApi = SmoothMotionApi,
                MenuKey = MenuKey,
                MenuKeyName = MenuKeyName,
                LiveFlowKey = LiveFlowKey,
                LiveFlowKeyName = LiveFlowKeyName,
                LiveFlowMultiplier = LiveFlowMultiplier,
                LiveFlowQualityPreset = LiveFlowQualityPreset,
                LiveFlowLowLatency = LiveFlowLowLatency,
                LiveFlowKeepStill = LiveFlowKeepStill,
                ShowEncoreMenu = ShowEncoreMenu,
                IgnoreAntiCheatWarnings = IgnoreAntiCheatWarnings,
                ManualGames = new Dictionary<string, string>(_manualGameModes, StringComparer.OrdinalIgnoreCase),
                By = "Leno4kaControl"
            };

            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, ChzmSettingsName),
                JsonSerializer.Serialize(data, ChzmJsonOptions), new UTF8Encoding(false));
        }
        catch
        {
            // нет прав на папку — настройки просто не сохранятся
        }
    }

    private static string EngineToString(EngineMode mode) => mode switch
    {
        EngineMode.RtxEncore => EngineEncore,
        EngineMode.SmoothMotion => EngineSmooth,
        _ => EngineClassic
    };

    private static EngineMode? EngineFromString(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        EngineEncore => EngineMode.RtxEncore,
        EngineSmooth => EngineMode.SmoothMotion,
        EngineClassic => EngineMode.ClassicSm86,
        _ => null
    };

    private static string NormalizeGameKey(string exePath)
    {
        try
        {
            return Path.GetFullPath(exePath).TrimEnd('\\').ToLowerInvariant();
        }
        catch
        {
            return exePath.Trim().ToLowerInvariant();
        }
    }

    private static bool IsValidSuperResolution(string? mode)
        => mode is "M-INT8" or "L-INT8" or "M-FP16" or "L-FP16" or "Off";

    // ===================== МАРКЕР ДВИЖКА =====================

    public static string? ReadMarkerEngine(string gameDirectory)
    {
        try
        {
            string marker = Path.Combine(gameDirectory, ProxyMarkerName);
            if (!File.Exists(marker)) return null;

            return ExtractMarkerValue(File.ReadAllText(marker), "engine");
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractMarkerValue(string text, string key)
    {
        try
        {
            Match match = Regex.Match(text,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"?([^\",}\\r\\n]+)\"?",
                RegexOptions.IgnoreCase);

            if (!match.Success) return null;

            string value = match.Groups[1].Value.Trim().Trim('"');
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsEncoreEngineInstalled(string gameDirectory)
    {
        try
        {
            string? engine = ReadMarkerEngine(gameDirectory);
            if (engine is EngineEncore or EngineSmooth) return true;

            if (FindEncoreProxy(gameDirectory) is not null) return true;
            if (File.Exists(Path.Combine(gameDirectory, EncoreConfigName))) return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string? FindEncoreProxy(string gameDirectory)
    {
        try
        {
            string? engine = ReadMarkerEngine(gameDirectory);
            if (engine is EngineEncore or EngineSmooth)
            {
                string? markerProxy = ExtractMarkerValue(
                    File.ReadAllText(Path.Combine(gameDirectory, ProxyMarkerName)), "proxy");

                if (markerProxy is not null)
                {
                    string path = Path.Combine(gameDirectory, markerProxy);
                    if (File.Exists(path) || File.Exists(path + ".off")) return markerProxy;
                }
            }

            foreach (string proxy in EncoreProxyOrder)
            {
                string path = Path.Combine(gameDirectory, proxy);
                if (FileMatchesEncoreProxy(path) || FileMatchesEncoreProxy(path + ".off")) return proxy;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Реально загружаемый (не .off) прокси Encore/SM, который ещё остался в папке игры.</summary>
    private static string? FindActiveEncoreProxy(string gameDirectory)
    {
        try
        {
            string? engine = ReadMarkerEngine(gameDirectory);
            if (engine is EngineEncore or EngineSmooth)
            {
                string? markerProxy = ExtractMarkerValue(
                    File.ReadAllText(Path.Combine(gameDirectory, ProxyMarkerName)), "proxy");

                if (markerProxy is not null && File.Exists(Path.Combine(gameDirectory, markerProxy)))
                    return markerProxy;
            }

            foreach (string proxy in EncoreProxyOrder)
            {
                if (FileMatchesEncoreProxy(Path.Combine(gameDirectory, proxy))) return proxy;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Реально загружаемый (не .off) прокси Classic, который ещё остался в папке игры.</summary>
    private static string? FindActiveClassicProxy(string gameDirectory)
    {
        try
        {
            string? engine = ReadMarkerEngine(gameDirectory);
            if (engine == EngineClassic)
            {
                string? markerProxy = ExtractMarkerValue(
                    File.ReadAllText(Path.Combine(gameDirectory, ProxyMarkerName)), "proxy");

                if (markerProxy is not null && File.Exists(Path.Combine(gameDirectory, markerProxy)))
                    return markerProxy;
            }

            foreach (string proxy in TensorProxyOrder)
            {
                if (FileMatchesClassicProxy(Path.Combine(gameDirectory, proxy))) return proxy;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? InferEngine(string gameDirectory)
    {
        string? engine = ReadMarkerEngine(gameDirectory);
        if (engine is not null) return engine;

        if (HasModPayload(gameDirectory)) return EngineClassic;

        // Новый RTXMFG Smooth Motion живёт как version.dll ~1 МБ.
        if (IsRtx30SmoothProxy(gameDirectory)) return EngineSmooth;

        string? encoreProxy = FindEncoreProxy(gameDirectory);
        if (encoreProxy is not null)
            return IsSmoothMotionConfigured(gameDirectory) ? EngineSmooth : EngineEncore;

        if (File.Exists(Path.Combine(gameDirectory, EncoreConfigName))) return EngineEncore;

        return null;
    }

    // ===================== СТАТУС ДЛЯ UI =====================

    public EngineStatus GetEngineStatus(ActiveGameInfo game)
    {
        string dir = game.Directory;
        string? engine = InferEngine(dir);
        string? proxy = GetInstalledProxy(dir);

        EngineMode? mode = engine switch
        {
            EngineEncore => EngineMode.RtxEncore,
            EngineSmooth => EngineMode.SmoothMotion,
            EngineClassic => EngineMode.ClassicSm86,
            _ => null
        };

        int maxFrames = ReadClassicMaxFrames(dir);
        string sr = ReadClassicSuperResolution(dir);

        bool installed = false;
        bool enabled = false;

        if (mode == EngineMode.ClassicSm86)
        {
            installed = HasModPayload(dir);
            enabled = installed && proxy is not null &&
                      File.Exists(Path.Combine(dir, proxy)) &&
                      (ReadEnabled(Path.Combine(dir, IniFileName)) ?? false);
        }
        else if (mode == EngineMode.RtxEncore)
        {
            installed = proxy is not null &&
                        (FileMatchesEncoreProxy(Path.Combine(dir, proxy)) ||
                         File.Exists(Path.Combine(dir, proxy)));
            enabled = proxy is not null && File.Exists(Path.Combine(dir, proxy));
        }
        else if (mode == EngineMode.SmoothMotion)
        {
            bool rtx30 = IsRtx30SmoothProxy(dir);

            installed = rtx30 ||
                        (proxy is not null &&
                         (FileMatchesEncoreProxy(Path.Combine(dir, proxy)) ||
                          File.Exists(Path.Combine(dir, proxy))));

            // RTXMFG Smooth Motion не использует rtx-encore.jsonc — он включён,
            // пока лежит его version.dll. Старый Encore SM проверяем по конфигу.
            enabled = installed && (rtx30 || IsSmoothMotionConfigured(dir));
        }

        string api = ResolveApi(dir, SmoothMotionApi);

        string detail = mode switch
        {
            EngineMode.ClassicSm86 => installed
                ? $"{FramesToMultiplier(maxFrames)} · SR: {sr}" + (enabled ? string.Empty : " · выключен")
                : "мод не установлен",
            EngineMode.RtxEncore => installed
                ? $"{proxy} · меню INSERT · 2X-6X · Dynamic · Preset C" + (enabled ? string.Empty : " · выключен")
                : "мод не установлен",
            EngineMode.SmoothMotion => installed
                ? (IsRtx30SmoothProxy(dir)
                    ? "version.dll · RTXMFG Smooth Motion · 2X · драйвер"
                    : $"{proxy} · API {api} · DLSS не требуется") +
                  (enabled ? string.Empty : " · выключен")
                : "мод не установлен",
            _ => "мод не установлен"
        };

        return new EngineStatus(mode, proxy ?? string.Empty, installed, enabled, maxFrames, sr, detail);
    }

    public static int ReadClassicMaxFrames(string gameDirectory)
    {
        string? value = ReadIniValue(Path.Combine(gameDirectory, IniFileName), "FrameGeneration", "MaxGeneratedFrames");
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int frames))
            return Math.Clamp(frames, 0, 5);

        return 3;
    }

    public static string ReadClassicSuperResolution(string gameDirectory)
    {
        string? value = ReadIniValue(Path.Combine(gameDirectory, IniFileName), "SuperResolution", "Mode");
        return IsValidSuperResolution(value) ? value! : "M-INT8";
    }

    /// <summary>Множитель RTX Encore из rtx-encore.jsonc (по умолчанию 4X).</summary>
    public static int ReadEncoreMultiplier(string gameDirectory)
    {
        try
        {
            string path = Path.Combine(gameDirectory, EncoreConfigName);
            if (!File.Exists(path)) return 4;

            string text = File.ReadAllText(path);
            Match match = Regex.Match(text, "\"multiplier\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);

            if (match.Success &&
                int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value))
                return Math.Clamp(value, 2, 6);
        }
        catch
        {
            // ignore
        }

        return 4;
    }

    public static string FramesToMultiplier(int frames) => Math.Clamp(frames, 0, 5) switch
    {
        0 => "AUTO",
        1 => "2X",
        2 => "3X",
        3 => "4X",
        4 => "5X",
        _ => "6X"
    };

    // ===================== ГЛАВНОЕ ДЕЙСТВИЕ =====================

    /// <summary>Установить/включить/выключить выбранный в UI движок для активной игры.</summary>
    public TensorToggleResult ApplySelectedEngine()
    {
        try
        {
            string? sourceError = CheckSelectedEngineSources(SelectedEngine);
            if (sourceError is not null)
            {
                return new TensorToggleResult(TensorToggleAction.Failed,
                    "⚡ " + EngineDisplayName(SelectedEngine), sourceError);
            }

            ActiveGameInfo? game = ResolveTargetGame();
            if (game is null)
            {
                return new TensorToggleResult(TensorToggleAction.NoGame,
                    "⚡ " + EngineDisplayName(SelectedEngine),
                    "Активная игра не найдена — запустите игру");
            }

            if (HasAntiCheatOrPreloader(game.Directory, out string reason) && !IgnoreAntiCheatWarnings)
            {
                return new TensorToggleResult(TensorToggleAction.BlockedByAntiCheat,
                    "🛡 EAC ЗАЩИТА",
                    $"Движок отключен · {reason} · {game.ProcessName}");
            }

            return ApplyEngineToGame(SelectedEngine, game, remember: true);
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "⚡ " + EngineDisplayName(SelectedEngine), "Ошибка: " + ex.Message);
        }
    }

    /// <summary>Точка входа для автотеста: установка в произвольную папку без живого процесса.</summary>
    public TensorToggleResult ApplyEngineToDirectory(EngineMode mode, string gameDirectory, string? processName = null)
    {
        try
        {
            string? sourceError = CheckSelectedEngineSources(mode);
            if (sourceError is not null)
                return new TensorToggleResult(TensorToggleAction.Failed, "⚡ " + EngineDisplayName(mode), sourceError);

            Directory.CreateDirectory(gameDirectory);

            string exe = Path.Combine(gameDirectory, (processName ?? "game") + ".exe");
            var game = new ActiveGameInfo(
                Path.GetFileNameWithoutExtension(exe), exe, gameDirectory);

            return ApplyEngineToGame(mode, game, remember: false);
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "⚡ " + EngineDisplayName(mode), "Ошибка: " + ex.Message);
        }
    }

    private static string? CheckSelectedEngineSources(EngineMode mode) => mode switch
    {
        EngineMode.ClassicSm86 when !IsModSourceReady()
            => $"Пак Classic SM86 не найден: {ModSourceDirectory}",
        EngineMode.RtxEncore when !IsEncoreReady()
            => $"Пак RTX Encore не найден: {EncoreSourceDirectory}",
        EngineMode.SmoothMotion when !IsEncoreReady() && !IsSmoothMotionReady()
            => $"Не найдены ни RTX Encore, ни Smooth Motion: {SupplementSourceRoot}",
        _ => null
    };

    /// <summary>Точка входа для лаунчера: установка в конкретную игру по её .exe.</summary>
    public TensorToggleResult ApplyEngineToGamePath(EngineMode mode, string gameDirectory, string executablePath)
    {
        try
        {
            string? sourceError = CheckSelectedEngineSources(mode);
            if (sourceError is not null)
                return new TensorToggleResult(TensorToggleAction.Failed, "⚡ " + EngineDisplayName(mode), sourceError);

            Directory.CreateDirectory(gameDirectory);

            var game = new ActiveGameInfo(
                Path.GetFileNameWithoutExtension(executablePath), executablePath, gameDirectory);

            return ApplyEngineToGame(mode, game, remember: true);
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "⚡ " + EngineDisplayName(mode), "Ошибка: " + ex.Message);
        }
    }

    /// <summary>Множитель RTX Encore в rtx-encore.jsonc выбранной игры.</summary>
    public TensorToggleResult SetEncoreMultiplier(int multiplier, string? gameDirectory = null)
    {
        multiplier = Math.Clamp(multiplier, 2, 6);

        try
        {
            string? dir = gameDirectory ?? ResolveTargetGame()?.Directory;
            if (string.IsNullOrWhiteSpace(dir))
                return new TensorToggleResult(TensorToggleAction.Enabled,
                    "🔥 RTX ENCORE", $"{multiplier}X сохранено · игра не выбрана");

            string path = Path.Combine(dir, EncoreConfigName);
            if (!File.Exists(path))
                return new TensorToggleResult(TensorToggleAction.Enabled,
                    "🔥 RTX ENCORE", $"{multiplier}X · конфиг появится при установке");

            string text = File.ReadAllText(path);

            if (Regex.IsMatch(text, "\"multiplier\"\\s*:\\s*\\d+"))
            {
                text = Regex.Replace(text, "(\"multiplier\"\\s*:\\s*)\\d+",
                    m => m.Groups[1].Value + multiplier, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }

            return new TensorToggleResult(TensorToggleAction.Enabled,
                "🔥 RTX ENCORE", $"множитель {multiplier}X · перезапустите игру");
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🔥 RTX ENCORE", "Ошибка: " + ex.Message);
        }
    }

    private TensorToggleResult ApplyEngineToGame(EngineMode mode, ActiveGameInfo game, bool remember)
    {
        // Жёсткий предохранитель: ни один внутренний метод не имеет права
        // копировать DLL в папку игры с античитом. Обхода «галочкой» больше нет.
        if (HasAntiCheatOrPreloader(game.Directory, out string reason) && !IgnoreAntiCheatWarnings)
        {
            return new TensorToggleResult(TensorToggleAction.BlockedByAntiCheat,
                "🛡 АНТИЧИТ: ВНУТРЕННИЕ ПРОКСИ ЗАПРЕЩЕНЫ",
                $"{game.ProcessName} · {reason} · доступен только внешний LIVE FLOW");
        }

        return mode switch
        {
            EngineMode.RtxEncore => ApplyEncoreEngine(game, smoothOnly: false, remember),
            EngineMode.SmoothMotion => ApplySmoothMotionEngine(game, remember),
            _ => ApplyClassicEngine(game, remember)
        };
    }

    // ===================== CLASSIC SM86 =====================

    private TensorToggleResult ApplyClassicEngine(ActiveGameInfo game, bool remember)
    {
        string dir = game.Directory;
        string ini = Path.Combine(dir, IniFileName);

        string? engine = InferEngine(dir);
        string? proxy = GetInstalledProxy(dir);

        bool classicInstalled = HasModPayload(dir) && proxy is not null &&
                                File.Exists(Path.Combine(dir, proxy)) &&
                                FileMatchesClassicProxy(Path.Combine(dir, proxy));

        bool classicEnabled = classicInstalled &&
                              (ReadEnabled(ini) ?? false);

        // Повторное нажатие на уже включённый Classic — выключаем (прокси → .off, Enabled=0).
        if (engine == EngineClassic && classicEnabled)
        {
            WriteIni(ini, enabled: false, ClassicSuperResolution, ClassicMaxFrames);
            string note = DisableProxyNamed(dir, proxy!);
            SaveProxyMarker(dir, proxy!, good: true, EngineClassic, ClassicMaxFrames, ClassicSuperResolution, SmoothMotionApi);
            if (remember) RememberGame(game);

            return new TensorToggleResult(TensorToggleAction.Disabled,
                "🎯 CLASSIC SM86: ВЫКЛЮЧЕН",
                $"{game.ProcessName}{note} · перезапустите игру");
        }

        // Переключение с другого движка: сначала полностью убираем чужой хук.
        string cleanup = RemoveOptiScalerLeftovers(dir);
        cleanup += RemoveEncoreArtifacts(dir, force: true, removeConfig: true, removeLogs: true);

        string? leftoverEncore = FindActiveEncoreProxy(dir);
        if (leftoverEncore is not null)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🎯 CLASSIC SM86",
                $"{game.ProcessName} · не удалось убрать {leftoverEncore} движка Encore " +
                "(файл держит игра) · закройте игру и повторите");
        }

        bool copied = false;
        string chosen = proxy ?? TensorProxyOrder[0];

        // Если в папке игры лежит НАШ старый .off — освобождаем имя и копируем свежий прокси.
        foreach (string candidate in TensorProxyOrder)
        {
            try
            {
                string off = Path.Combine(dir, candidate + ".off");
                if (FileMatchesClassicProxy(off)) DeleteOrSchedule(off);
            }
            catch { }
        }

        foreach (string candidate in TensorProxyOrder)
        {
            if (CopyProxy(dir, candidate))
            {
                chosen = candidate;
                copied = true;
                break;
            }
        }

        if (!copied)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🎯 CLASSIC SM86",
                $"{game.ProcessName} · не удалось положить прокси (имена заняты файлами игры)");
        }

        CopyModFiles(dir, includeSuperResolution: true, chosen);
        WriteIni(ini, enabled: true, ClassicSuperResolution, ClassicMaxFrames);
        SaveProxyMarker(dir, chosen, good: true, EngineClassic, ClassicMaxFrames, ClassicSuperResolution, SmoothMotionApi);

        if (remember) RememberGame(game);

        return new TensorToggleResult(TensorToggleAction.Installed,
            "🎯 CLASSIC SM86: ВКЛЮЧЕН",
            $"{game.ProcessName} · {chosen} · {FramesToMultiplier(ClassicMaxFrames)} · SR {ClassicSuperResolution}" +
            $"{cleanup} · перезапустите игру");
    }

    /// <summary>Потолок кадров Classic: сразу пишется в dlssg_sm86.ini папки игры.</summary>
    public TensorToggleResult SetClassicMaxFrames(int maxFrames, string? gameDirectory = null)
    {
        ClassicMaxFrames = Math.Clamp(maxFrames, 0, 5);
        SaveChzmSettings();

        return ApplyClassicSetting(gameDirectory, $"потолок {FramesToMultiplier(ClassicMaxFrames)}");
    }

    /// <summary>Тензорный апскейл Classic: Off / M-INT8 / L-INT8 / M-FP16 / L-FP16.</summary>
    public TensorToggleResult SetClassicSuperResolution(string mode, string? gameDirectory = null)
    {
        if (!IsValidSuperResolution(mode))
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🎯 CLASSIC SM86", $"Неизвестный режим SR: {mode}");
        }

        ClassicSuperResolution = mode;
        SaveChzmSettings();

        return ApplyClassicSetting(gameDirectory, $"SR {mode}");
    }

    /// <summary>Пресет генерации Classic: A (качество), B, C (FG Lite, быстрее на 30–46%).</summary>
    public TensorToggleResult SetClassicPreset(string preset, string? gameDirectory = null)
    {
        preset = (preset ?? "").Trim().ToUpperInvariant();
        if (preset is not ("A" or "B" or "C"))
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🎯 CLASSIC SM86", $"Неизвестный пресет FG: {preset}");
        }

        string? dir = gameDirectory;

        if (string.IsNullOrWhiteSpace(dir))
            dir = ResolveTargetGame()?.Directory;

        if (string.IsNullOrWhiteSpace(dir))
        {
            return new TensorToggleResult(TensorToggleAction.Enabled,
                "🎯 CLASSIC SM86", $"Preset {preset} сохранён · игра не выбрана");
        }

        string ini = Path.Combine(dir, IniFileName);
        if (!File.Exists(ini))
        {
            TensorToggleResult install = ApplyEngineToGame(EngineMode.ClassicSm86,
                new ActiveGameInfo(Path.GetFileName(dir.TrimEnd('\\')),
                    Path.Combine(dir, "game.exe"), dir), remember: false);

            if (install.Action == TensorToggleAction.Failed) return install;
        }

        WriteIniPreset(ini, preset);

        return new TensorToggleResult(TensorToggleAction.Enabled,
            "🎯 CLASSIC SM86",
            $"Preset {preset} записан в dlssg_sm86.ini · перезапустите игру");
    }

    public static string ReadClassicPreset(string gameDirectory)
    {
        string? value = ReadIniValue(Path.Combine(gameDirectory, IniFileName), "DLSSG", "Preset");
        if (string.IsNullOrWhiteSpace(value)) return "A";

        string preset = value.Trim().ToUpperInvariant();
        return preset is "A" or "B" or "C" ? preset : "A";
    }

    private static void WriteIniPreset(string iniPath, string preset)
    {
        try
        {
            (List<string> lines, Encoding encoding) = ReadIniLines(iniPath);
            SetIniValue(lines, "DLSSG", "Preset", preset);
            File.WriteAllLines(iniPath, lines, encoding);
        }
        catch
        {
            // файл занят — применится при следующей установке
        }
    }

    private TensorToggleResult ApplyClassicSetting(string? gameDirectory, string what)
    {
        try
        {
            ActiveGameInfo? game = null;

            if (gameDirectory is not null)
            {
                game = new ActiveGameInfo(
                    Path.GetFileName(gameDirectory.TrimEnd('\\')),
                    Path.Combine(gameDirectory, "game.exe"),
                    gameDirectory);
            }
            else
            {
                game = ResolveTargetGame();
            }

            if (game is null)
            {
                return new TensorToggleResult(TensorToggleAction.Enabled,
                    "🎯 CLASSIC SM86",
                    $"{what} сохранено · игра не найдена, применится при установке");
            }

            if (!HasModPayload(game.Directory) || !File.Exists(Path.Combine(game.Directory, IniFileName)))
            {
                // Ещё не установлен — ставим Classic с текущими настройками.
                TensorToggleResult install = ApplyEngineToGame(EngineMode.ClassicSm86, game, remember: false);
                if (install.Action == TensorToggleAction.Failed) return install;

                return new TensorToggleResult(TensorToggleAction.Installed,
                    "🎯 CLASSIC SM86: ВКЛЮЧЕН",
                    $"{game.ProcessName} · {what} · перезапустите игру");
            }

            string ini = Path.Combine(game.Directory, IniFileName);
            string? proxy = GetInstalledProxy(game.Directory) ?? ProxyDllName;

            WriteIni(ini, enabled: true, ClassicSuperResolution, ClassicMaxFrames);
            SaveProxyMarker(game.Directory, proxy, good: true, EngineClassic,
                ClassicMaxFrames, ClassicSuperResolution, SmoothMotionApi);

            return new TensorToggleResult(TensorToggleAction.Enabled,
                "🎯 CLASSIC SM86",
                $"{game.ProcessName} · {what} записано в dlssg_sm86.ini · перезапустите игру");
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🎯 CLASSIC SM86", "Ошибка: " + ex.Message);
        }
    }

    // ===================== RTX ENCORE / SMOOTH MOTION =====================

    private TensorToggleResult ApplyEncoreEngine(ActiveGameInfo game, bool smoothOnly, bool remember)
    {
        string dir = game.Directory;
        string previous = InferEngine(dir) ?? string.Empty;

        // Если включённый RTX Encore нажат повторно — выключаем (прокси → .off).
        // Smooth Motion сюда не попадает: у него smoothOnly = true.
        if (!smoothOnly && previous == EngineEncore)
        {
            string? active = FindEncoreProxy(dir);
            if (active is not null && File.Exists(Path.Combine(dir, active)))
            {
                RemoveProxyFile(Path.Combine(dir, active));
                SaveProxyMarker(dir, active, good: true, EngineEncore,
                    ClassicMaxFrames, ClassicSuperResolution, SmoothMotionApi);

                if (remember) RememberGame(game);

                return new TensorToggleResult(TensorToggleAction.Disabled,
                    "🔥 RTX ENCORE: ВЫКЛЮЧЕН",
                    $"{game.ProcessName} · {active} → .off · перезапустите игру");
            }
        }

        string cleanup = RemoveOptiScalerLeftovers(dir);
        cleanup += RemoveClassicArtifacts(dir, force: true);

        string? leftoverClassic = FindActiveClassicProxy(dir);
        if (leftoverClassic is not null)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                smoothOnly ? "🚀 SMOOTH MOTION" : "🔥 RTX ENCORE",
                $"{game.ProcessName} · не удалось убрать {leftoverClassic} движка Classic " +
                "(файл держит игра) · закройте игру и повторите");
        }

        // Если раньше был Smooth Motion — при возврате в Encore выключаем SM,
        // чтобы режимы не смешивались. Остальные настройки пользователя сохраняются.
        if (!smoothOnly && previous == EngineSmooth)
            SetEncoreSmoothMotionConfig(dir, enabled: false, ResolveApi(dir, SmoothMotionApi));

        string? proxy = FindEncoreProxy(dir);
        string? source = null;

        if (proxy is null || !File.Exists(Path.Combine(dir, proxy)))
        {
            proxy = ChooseEncoreProxy(dir, out source);
            if (proxy is null)
            {
                return new TensorToggleResult(TensorToggleAction.Failed,
                    "⚡ RTX ENCORE",
                    $"{game.ProcessName} · все имена прокси заняты файлами игры");
            }
        }
        else
        {
            source = GetEncoreProxySource(proxy);
        }

        CopyFile(source!, Path.Combine(dir, proxy!), force: true);

        string nrNote = EnsureNeuralRenderingRuntime(dir);

        if (smoothOnly)
        {
            SetEncoreSmoothMotionConfig(dir, enabled: true, ResolveApi(dir, SmoothMotionApi));
        }
        else if (!File.Exists(Path.Combine(dir, EncoreConfigName)))
        {
            WriteEncoreDefaultConfig(dir, smoothMotion: false, ResolveApi(dir, SmoothMotionApi));
        }

        SetEncoreInGameMenu(dir, ShowEncoreMenu);

        SaveProxyMarker(dir, proxy!, good: true,
            smoothOnly ? EngineSmooth : EngineEncore,
            ClassicMaxFrames, ClassicSuperResolution, SmoothMotionApi);

        if (remember) RememberGame(game);

        string menu = smoothOnly || !ShowEncoreMenu ? string.Empty : " · F8 → меню";
        string api = ResolveApi(dir, SmoothMotionApi);

        return new TensorToggleResult(TensorToggleAction.Installed,
            smoothOnly ? "🚀 SMOOTH MOTION: ВКЛЮЧЕН" : "🔥 RTX ENCORE: ВКЛЮЧЕН",
            smoothOnly
                ? $"{game.ProcessName} · {proxy} · API {api}{nrNote} · DLSS не требуется · перезапустите игру"
                : $"{game.ProcessName} · {proxy}{menu}{nrNote}{cleanup} · перезапустите игру");
    }

    /// <summary>
    /// Кладёт nvngx_dlssnr.dll рядом с прокси, чтобы Encore/Smooth никогда не
    /// писали «Neural Rendering runtime not found». Источники по приоритету:
    /// портативный native проекта, затем установленный DLSS 5 MANAGER.
    /// </summary>
    private static string EnsureNeuralRenderingRuntime(string gameDirectory)
    {
        try
        {
            const string fileName = "nvngx_dlssnr.dll";
            string destination = Path.Combine(gameDirectory, fileName);

            if (File.Exists(destination) && new FileInfo(destination).Length > 1_000_000)
                return string.Empty;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var candidates = new[]
            {
                Path.Combine(SupplementSourceRoot, "live-flow", "native", "dlss 5", fileName),
                Path.Combine(SupplementSourceRoot, "live-flow", "native", fileName),
                Path.Combine(EncoreSourceDirectory, "dlss 5", fileName),
                Path.Combine(EncoreSourceDirectory, fileName),
                Path.Combine(localAppData, "Programs", "DLSS 5 MANAGER", "mod files", "dlss 5", fileName),
                Path.Combine(localAppData, "Programs", "DLSS 5 MANAGER", "mod files", "native", "dlss 5", fileName),
                Path.Combine(localAppData, "Programs", "DLSS 5 MANAGER", "mod files", "native", fileName)
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate) || new FileInfo(candidate).Length <= 1_000_000) continue;

                    File.Copy(candidate, destination, overwrite: true);
                    return " · NR runtime ✓";
                }
                catch
                {
                    // следующий источник
                }
            }

            return " · NR runtime не найден (NR off)";
        }
        catch
        {
            return string.Empty;
        }
    }

    private TensorToggleResult ApplySmoothMotionEngine(ActiveGameInfo game, bool remember)
    {
        string dir = game.Directory;
        string previous = InferEngine(dir) ?? string.Empty;
        string? activeProxy = FindEncoreProxy(dir);

        // Повторное нажатие — выключаем SM, не трогая Classic-файлы игры.
        if (previous == EngineSmooth && activeProxy is not null &&
            File.Exists(Path.Combine(dir, activeProxy)))
        {
            RemoveProxyFile(Path.Combine(dir, activeProxy));
            SaveProxyMarker(dir, activeProxy, good: true, EngineSmooth,
                ClassicMaxFrames, ClassicSuperResolution, SmoothMotionApi);

            if (remember) RememberGame(game);

            return new TensorToggleResult(TensorToggleAction.Disabled,
                "🚀 SMOOTH MOTION: ВЫКЛЮЧЕН",
                $"{game.ProcessName} · {activeProxy} → .off · перезапустите игру");
        }

        // 1) RTX Encore v1.4.5 — официально поддерживает драйвер 617.42.
        //    Smooth Motion включается в самом конфиге Encore, без sm86.exe.
        if (IsEncoreReady())
            return ApplyEncoreSmoothEngine(game, remember);

        // 2) Резерв: RTXMFG SmoothMotion-next только если Encore нет
        //    и игра не конфликтует с хуком Present.
        if (IsSmoothMotionRtx30Ready() && !MustDisableEncoreMenu(dir))
            return ApplyRtx30SmoothEngine(game, remember);

        // 3) Самый старый путь: sm86.exe (616.64). На 617.42 он fail-closed.
        string helper = TryPrepareSmoothMotionRuntime(game.ExecutablePath, out string helperNote);

        TensorToggleResult result;

        if (IsEncoreReady())
        {
            result = ApplyEncoreEngine(game, smoothOnly: true, remember);
        }
        else
        {
            string nrNote = EnsureNeuralRenderingRuntime(dir);

            result = new TensorToggleResult(TensorToggleAction.Installed,
                "🚀 SMOOTH MOTION: ХЕЛПЕР",
                $"{game.ProcessName} · драйверный хелпер SM86{nrNote} · запускайте игру через Smooth Motion SM86");
        }

        if (result.Action == TensorToggleAction.Failed) return result;

        return new TensorToggleResult(result.Action, result.Title,
            result.Detail + " · " + helperNote + helper);
    }

    /// <summary>
    /// SMOOTH через Encore: драйверный Smooth Motion 2X + конфиг Encore.
    /// Меню принудительно выключаем — оно не нужно для SM и ломается
    /// на играх с защитой Present (CoD/Randgrid, Detours 156).
    /// </summary>
    private TensorToggleResult ApplyEncoreSmoothEngine(ActiveGameInfo game, bool remember)
    {
        TensorToggleResult result = ApplyEncoreEngine(game, smoothOnly: true, remember);
        if (result.Action == TensorToggleAction.Failed) return result;

        string dir = game.Directory;

        try
        {
            SetEncoreSmoothMotionConfig(dir, enabled: true, ResolveApi(dir, SmoothMotionApi));
            ForceEncoreMenuDisabled(dir);
        }
        catch
        {
            // конфиг мог быть занят — применится при следующем запуске
        }

        string note = MustDisableEncoreMenu(dir)
            ? " · меню отключено (защита Present)"
            : " · меню отключено (SMOOTH работает автоматически)";

        return new TensorToggleResult(result.Action,
            "🚀 SMOOTH (ENCORE): ВКЛЮЧЕН",
            result.Detail + note);
    }

    /// <summary>
    /// RTX 30 путь: кладём в игру RTXMFG SmoothMotion-next (version.dll 1 МБ),
    /// который включает драйверный Smooth Motion NVIDIA. Никакого sm86.exe.
    /// </summary>
    private TensorToggleResult ApplyRtx30SmoothEngine(ActiveGameInfo game, bool remember)
    {
        string dir = game.Directory;
        string source = Path.Combine(SmoothRtx30SourceDirectory, SmoothRtx30DllName);
        string destination = Path.Combine(dir, SmoothRtx30DllName);

        try
        {
            // Убираем предыдущий движок: Encore-конфиг/прокси и Classic.
            string cleanup = RemoveOptiScalerLeftovers(dir);
            cleanup += RemoveEncoreArtifacts(dir, force: true, removeConfig: true, removeLogs: true);
            cleanup += RemoveClassicArtifacts(dir, force: true);

            // Файл version.dll может принадлежать самой игре — его не трогаем.
            if (File.Exists(destination))
            {
                long sourceLength = new FileInfo(source).Length;
                long destinationLength = new FileInfo(destination).Length;

                bool ours = destinationLength == sourceLength ||
                            MarkerNamesProxy(dir, SmoothRtx30DllName);

                if (!ours)
                {
                    // Игра сама использует version.dll — RTXMFG туда не встанет.
                    // Тогда остаётся Encore Smooth Motion через другой прокси.
                    if (IsEncoreReady())
                    {
                        TensorToggleResult fallback = ApplyEncoreEngine(game, smoothOnly: true, remember);

                        return new TensorToggleResult(fallback.Action, fallback.Title,
                            fallback.Detail + " · version.dll занят игрой, включён Encore Smooth Motion");
                    }

                    return new TensorToggleResult(TensorToggleAction.Failed,
                        "🚀 SMOOTH MOTION",
                        $"{game.ProcessName} · version.dll занят файлом игры — " +
                        "RTXMFG Smooth Motion не может загрузиться · выберите ENCORE");
                }
            }

            DeleteOrSchedule(destination + ".off");
            CopyFile(source, destination, force: true);

            if (!File.Exists(destination))
            {
                return new TensorToggleResult(TensorToggleAction.Failed,
                    "🚀 SMOOTH MOTION",
                    $"{game.ProcessName} · не удалось скопировать RTXMFG Smooth Motion");
            }

            SaveProxyMarker(dir, SmoothRtx30DllName, good: true, EngineSmooth,
                ClassicMaxFrames, ClassicSuperResolution, SmoothMotionApi);

            if (remember) RememberGame(game);

            return new TensorToggleResult(TensorToggleAction.Installed,
                "🚀 SMOOTH (RTXMFG): ВКЛЮЧЕН",
                $"{game.ProcessName} · RTXMFG Smooth Motion-next · драйверный 2X" +
                $"{cleanup} · перезапустите игру");
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🚀 SMOOTH MOTION", "Ошибка: " + ex.Message);
        }
    }

    private static string? ChooseEncoreProxy(string gameDirectory, out string? source)
    {
        source = null;

        foreach (string proxy in EncoreProxyOrder)
        {
            try
            {
                string dest = Path.Combine(gameDirectory, proxy);
                string? candidateSource = GetEncoreProxySource(proxy);

                if (!File.Exists(candidateSource)) continue;

                if (!File.Exists(dest))
                {
                    // имя свободно; заодно убираем наш старый .off
                    string off = dest + ".off";
                    if (FileMatchesEncoreProxy(off)) DeleteOrSchedule(off);

                    source = candidateSource;
                    return proxy;
                }

                // имя занято, но файл — наш (по размеру): можно перезаписать
                if (FileMatchesEncoreProxy(dest))
                {
                    source = candidateSource;
                    return proxy;
                }

                // имя занято файлом игры (родной dxgi/winmm и т.п.) — не трогаем
            }
            catch
            {
                // пробуем следующее имя
            }
        }

        return null;
    }

    // ===================== КОНФИГ RTX ENCORE =====================

    public static bool IsSmoothMotionConfigured(string gameDirectory)
    {
        try
        {
            string path = Path.Combine(gameDirectory, EncoreConfigName);
            if (!File.Exists(path)) return false;

            string text = File.ReadAllText(path);
            Match match = Regex.Match(text,
                "\"smoothMotionEnabled\"\\s*:\\s*(true|false)",
                RegexOptions.IgnoreCase);

            return match.Success && match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>В игре лежит именно новый RTXMFG Smooth Motion (version.dll 1 МБ).</summary>
    public static bool IsRtx30SmoothProxy(string gameDirectory)
    {
        try
        {
            string path = Path.Combine(gameDirectory, SmoothRtx30DllName);
            if (!File.Exists(path)) return false;

            long size = new FileInfo(path).Length;
            long expected = SmoothRtx30KnownSize;

            try
            {
                string source = Path.Combine(SmoothRtx30SourceDirectory, SmoothRtx30DllName);
                if (File.Exists(source)) expected = new FileInfo(source).Length;
            }
            catch { }

            return size == expected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Игры, где хуку Present/меню мешает защита (CoD sp26 / Randgrid).</summary>
    private static bool MustDisableEncoreMenu(string gameDirectory)
    {
        try
        {
            string normalized = gameDirectory.ToLowerInvariant()
                .Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

            return normalized.Contains("sp26") ||
                   normalized.Contains("call of duty") ||
                   normalized.Contains("campaign") ||
                   normalized.Contains("insaneramzes") ||
                   normalized.Contains("randgrid");
        }
        catch
        {
            return false;
        }
    }

    private static void ForceEncoreMenuDisabled(string gameDirectory)
    {
        SetEncoreInGameMenu(gameDirectory, false);
    }

    /// <summary>Включает/выключает внутриигровое меню в уже существующем конфиге Encore.</summary>
    private static void SetEncoreInGameMenu(string gameDirectory, bool show)
    {
        try
        {
            if (!show || MustDisableEncoreMenu(gameDirectory)) show = false;
            else show = true;

            string path = Path.Combine(gameDirectory, EncoreConfigName);
            if (!File.Exists(path)) return;

            string text = File.ReadAllText(path);
            string value = show ? "false" : "true";

            if (Regex.IsMatch(text, "\"disableInGameMenu\"\\s*:", RegexOptions.IgnoreCase))
            {
                text = Regex.Replace(text,
                    "(\"disableInGameMenu\"\\s*:\\s*)(true|false)",
                    m => m.Groups[1].Value + value,
                    RegexOptions.IgnoreCase);
            }
            else
            {
                int close = text.LastIndexOf('}');
                if (close > 0)
                {
                    int compat = text.IndexOf("\"compatibility\"", StringComparison.OrdinalIgnoreCase);
                    if (compat >= 0)
                    {
                        int brace = text.IndexOf('{', compat);
                        if (brace > 0)
                            text = text.Insert(brace + 1,
                                "\n    \"disableInGameMenu\": " + value + ",");
                    }
                }
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // файл занят — применится при следующей установке
        }
    }

    /// <summary>Включает/выключает Smooth Motion в rtx-encore.jsonc, сохраняя остальные настройки.</summary>
    private void SetEncoreSmoothMotionConfig(string gameDirectory, bool enabled, string api)
    {
        string path = Path.Combine(gameDirectory, EncoreConfigName);

        try
        {
            if (!File.Exists(path))
            {
                WriteEncoreDefaultConfig(gameDirectory, enabled, api);
                return;
            }

            string text = File.ReadAllText(path);

            text = Regex.Replace(text,
                "(\"smoothMotionEnabled\"\\s*:\\s*)(true|false)",
                m => m.Groups[1].Value + (enabled ? "true" : "false"),
                RegexOptions.IgnoreCase);

            text = Regex.Replace(text,
                "(\"smoothMotionApi\"\\s*:\\s*\")[^\"]*(\")",
                m => m.Groups[1].Value + api + m.Groups[2].Value,
                RegexOptions.IgnoreCase);

            text = Regex.Replace(text,
                "(\"disableInGameMenu\"\\s*:\\s*)(true|false)",
                m => m.Groups[1].Value +
                     ((ShowEncoreMenu && !MustDisableEncoreMenu(gameDirectory)) ? "false" : "true"),
                RegexOptions.IgnoreCase);

            if (!Regex.IsMatch(text, "\"smoothMotionEnabled\"\\s*:", RegexOptions.IgnoreCase))
            {
                int close = text.LastIndexOf('}');
                if (close > 0)
                {
                    string block =
                        ",\n  \"smoothMotion\": {\n" +
                        "    \"smoothMotionEnabled\": " + (enabled ? "true" : "false") + ",\n" +
                        "    \"smoothMotionTuringVariant\": \"v1\",\n" +
                        "    \"smoothMotionApi\": \"" + api + "\"\n" +
                        "  }\n";
                    text = text.Insert(close, block);
                }
            }

            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // файл занят/нет прав — мод создаст дефолт при первом запуске
        }
    }

    private void WriteEncoreDefaultConfig(string gameDirectory, bool smoothMotion, string api)
    {
        try
        {
            File.WriteAllText(Path.Combine(gameDirectory, EncoreConfigName),
                BuildEncoreConfig(smoothMotion, api,
                    ShowEncoreMenu && !MustDisableEncoreMenu(gameDirectory)), new UTF8Encoding(false));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Полный шаблон rtx-encore.jsonc (configVersion 4), снятый с самого мода v1.4.5.
    /// Все секции на месте, поэтому мод не теряет дефолты; меняются только SM-ключи.
    /// </summary>
    private static string BuildEncoreConfig(bool smoothMotion, string api, bool showInGameMenu = false) => $$"""
{
  "configVersion": 4,
  "renderingEngines": { "reconstructionEngine": "game", "frameGenerationEngine": "game" },
  "xefg": { "xefgMfg4VariablePacing": true, "xefgUiHandling": "interpolate", "xefgUiAlpha": "unknown" },
  "frameGeneration": {
    "mode": "game",
    "multiplier": 4,
    "dynamicTargetFrameRate": 0,
    "dynamicExperimental56": false,
    "flipMeteringBypass": false
  },
  "smoothMotion": {
    "smoothMotionEnabled": {{(smoothMotion ? "true" : "false")}},
    "smoothMotionTuringVariant": "v1",
    "smoothMotionApi": "{{api}}"
  },
  "dlssSuperResolution": { "dlssRenderScale": "game", "dlssCustomScale": 67, "fastSuperResolution": false },
  "superResolutionInputs": { "srHdr": "game", "srAutoExposure": "game" },
  "superResolutionSharpening": { "srSharpening": "off", "srSharpness": 30 },
  "dlssPresets": {
    "dlssSrPreset": "game",
    "dlssRrPreset": "game",
    "dlssFgPreset": "game",
    "dlssFgLite": "game",
    "dlssNrPreset": "b"
  },
  "nativeMenuFeatures": { "reflexFrameLimit": 0, "vsyncOff": false, "hairEnabled": true },
  "neuralRendering": {
    "core": {
      "nrEnabled": false,
      "nrPlacement": "after-sr",
      "nrPasses": 1,
      "nrLaterPassLocalTone": 0,
      "nrResolution": "render",
      "nrResolutionScale": 67
    },
    "appearance": {
      "nrIntensity": 1,
      "nrStyle": 0,
      "nrLocalTone": 1,
      "nrLocalStructure": 1,
      "nrAutoMask": false,
      "nrSkinStructure": 1,
      "nrHdrExposure": 1
    },
    "passStyles": {
      "nrPass1Style": "inherit",
      "nrPass2Style": "inherit",
      "nrPass3Style": "inherit",
      "nrPass4Style": "inherit"
    },
    "passModels": {
      "nrPass1Preset": "inherit",
      "nrPass2Preset": "inherit",
      "nrPass3Preset": "inherit",
      "nrPass4Preset": "inherit"
    },
    "passTuning": {
      "nrPass1Intensity": "inherit",
      "nrPass1LocalStructure": "inherit",
      "nrPass1LocalTone": "inherit",
      "nrPass1SkinStructure": "inherit",
      "nrPass1AutoMask": "inherit",
      "nrPass2Intensity": "inherit",
      "nrPass2LocalStructure": "inherit",
      "nrPass2LocalTone": "inherit",
      "nrPass2SkinStructure": "inherit",
      "nrPass2AutoMask": "inherit",
      "nrPass3Intensity": "inherit",
      "nrPass3LocalStructure": "inherit",
      "nrPass3LocalTone": "inherit",
      "nrPass3SkinStructure": "inherit",
      "nrPass3AutoMask": "inherit",
      "nrPass4Intensity": "inherit",
      "nrPass4LocalStructure": "inherit",
      "nrPass4LocalTone": "inherit",
      "nrPass4SkinStructure": "inherit",
      "nrPass4AutoMask": "inherit"
    },
    "optimizations": { "nrPrecision": "exact", "nrFastProcessing": true },
    "experimental": {
      "nrFoveation": false,
      "nrFoveaPreset": "balanced",
      "nrFoveaWidth": 55,
      "nrFoveaHeight": 55,
      "nrFoveaFalloff": 8,
      "nrFoveaEdge": 50,
      "nrPaddingAware": false,
      "nrMaxInFlight": 0
    },
    "advanced": { "nrUiCorrection": false }
  },
  "overlay": {
    "showOverlay": false,
    "overlayPosition": "top-left",
    "overlayFontSize": 14,
    "metrics": {
      "overlayShowNr": false,
      "overlayShowFramePacing": false,
      "overlayShowGpu": false,
      "overlayShowVram": false,
      "overlayShowVersions": false
    },
    "diagnostics": {
      "overlayShowUiRecomposition": false,
      "overlayShowHudless": false,
      "overlayShowUiAlpha": false,
      "overlayShowDebug": false
    }
  },
  "imageQuality": {
    "highMultiplierQuality": true,
    "frameProtection": true,
    "fastFrameGeneration": true,
    "protectionTuning": "refined"
  },
  "hudUi": { "autoUiRecomposition": true, "uiAssist": true, "forceUiRecomposition": false },
  "general": { "gpuSeries": "auto" },
  "compatibility": {
    "disableMenuDetection": false,
    "disableInGameMenu": {{(showInGameMenu ? "false" : "true")}},
    "forceOTA": false,
    "disableMvDilation": false
  },
  "diagnostics": {
    "logGraphics": false,
    "logStreamline": false,
    "logDxgiWrappers": false,
    "crashDetails": false,
    "logPerformance": false,
    "logNr": false,
    "profileNrGpu": false,
    "profileFrameGpu": false,
    "srCapture": false,
    "srCaptureFrames": 96,
    "logHudUi": false,
    "logMotionTracing": false,
    "logFilesKept": 3
  },
  "keyboardShortcuts": {
    "hotkeyFixed2": "Ctrl+Alt+2, Ctrl+Alt+Num2",
    "hotkeyFixed3": "Ctrl+Alt+3, Ctrl+Alt+Num3",
    "hotkeyFixed4": "Ctrl+Alt+4, Ctrl+Alt+Num4",
    "hotkeyFixed5": "Ctrl+Alt+5, Ctrl+Alt+Num5",
    "hotkeyFixed6": "Ctrl+Alt+6, Ctrl+Alt+Num6",
    "hotkeyMultiplierUp": "Ctrl+Alt+PageUp",
    "hotkeyMultiplierDown": "Ctrl+Alt+PageDown",
    "hotkeyToggleDynamic": "Ctrl+Alt+D",
    "hotkeyTargetFpsUp": "Ctrl+Alt+Up, Ctrl+Alt+Plus, Ctrl+Alt+NumAdd",
    "hotkeyTargetFpsDown": "Ctrl+Alt+Down, Ctrl+Alt+Minus, Ctrl+Alt+NumSubtract",
    "hotkeyGameMode": "Ctrl+Alt+G",
    "hotkeyOverlay": "Ctrl+Alt+O",
    "hotkeyOverlayPosition": "Ctrl+Alt+P",
    "hotkeySrCapture": "Ctrl+Alt+F9",
    "hotkeyToggleNr": "Ctrl+Alt+N",
    "disableKeybinds": false
  }
}
""";

    // ===================== SMOOTH MOTION HELPER =====================

    /// <summary>
    /// Автоматизация хелпера xikarioz/Smooth-Motion-RTX30:
    /// doctor (read-only) → repair (только при supported=true) → settings --master on --game &lt;exe&gt; --enable.
    /// На неизвестном драйвере хелпер ничего не меняет (fail-closed), и мы честно сообщаем об этом.
    /// </summary>
    private static string TryPrepareSmoothMotionRuntime(string gameExecutable, out string note)
    {
        note = "SM86-хелпер: не найден";

        try
        {
            string cli = Path.Combine(SmoothMotionSourceDirectory, SmoothMotionCliName);
            if (!File.Exists(cli)) return string.Empty;
            if (!File.Exists(gameExecutable)) return string.Empty;

            (int doctorExit, string doctorOut) = RunTool(cli, SmoothMotionSourceDirectory, 40000, "doctor");
            bool supported = Regex.IsMatch(doctorOut, "\"supported\"\\s*:\\s*true", RegexOptions.IgnoreCase);

            if (!supported)
            {
                string diagnosis = ExtractMarkerValue(doctorOut, "diagnosis") ?? "NOT_SUPPORTED";
                note = $"SM86-хелпер: {diagnosis} (используется RTX Encore SM)";
                return string.Empty;
            }

            (int repairExit, _) = RunTool(cli, SmoothMotionSourceDirectory, 120000, "repair");
            if (repairExit != 0)
            {
                note = "SM86-хелпер: repair не удался (используется RTX Encore SM)";
                return string.Empty;
            }

            (int enableExit, _) = RunTool(cli, SmoothMotionSourceDirectory, 30000,
                "settings", "--master", "on", "--game", gameExecutable, "--enable");

            note = enableExit == 0
                ? "SM86-хелпер: драйвер подготовлен"
                : "SM86-хелпер: настройка игры не применилась";

            return " · SM86 helper OK";
        }
        catch
        {
            note = "SM86-хелпер: ошибка запуска";
            return string.Empty;
        }
    }

    private static void TryDisableSmoothMotionForGame(string gameExecutable)
    {
        try
        {
            string cli = Path.Combine(SmoothMotionSourceDirectory, SmoothMotionCliName);
            if (!File.Exists(cli) || !File.Exists(gameExecutable)) return;

            RunTool(cli, SmoothMotionSourceDirectory, 20000,
                "settings", "--game", gameExecutable, "--disable");
        }
        catch
        {
            // best effort
        }
    }

    private static (int ExitCode, string Output) RunTool(string exe, string workingDirectory,
        int timeoutMs, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in arguments) psi.ArgumentList.Add(argument);

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("process not started");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty);
            }

            string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return (process.ExitCode, output);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    // ===================== ОЧИСТКА =====================

    /// <summary>Снять ВСЁ: прокси, .off, ini, json-маркеры, логи, dlssg_sm86\ и OptiScaler.</summary>
    public TensorToggleResult RemoveEverything()
    {
        try
        {
            ActiveGameInfo? game = ResolveTargetGame();
            if (game is null)
            {
                return new TensorToggleResult(TensorToggleAction.NoGame,
                    "🗑 ПОЛНАЯ ОЧИСТКА",
                    "Активная игра не найдена — запустите игру");
            }

            string note = RemoveAllArtifacts(game.Directory);
            TryDisableSmoothMotionForGame(game.ExecutablePath);

            return new TensorToggleResult(TensorToggleAction.Disabled,
                "🗑 МОД СНЯТ ПОЛНОСТЬЮ",
                $"{game.ProcessName} · папка игры возвращена в заводское состояние{note}");
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🗑 ПОЛНАЯ ОЧИСТКА", "Ошибка: " + ex.Message);
        }
    }

    /// <summary>Очистка произвольной папки (для автотеста).</summary>
    public TensorToggleResult RemoveEverythingFromDirectory(string gameDirectory)
    {
        try
        {
            string note = RemoveAllArtifacts(gameDirectory);
            return new TensorToggleResult(TensorToggleAction.Disabled,
                "🗑 МОД СНЯТ ПОЛНОСТЬЮ", "папка очищена" + note);
        }
        catch (Exception ex)
        {
            return new TensorToggleResult(TensorToggleAction.Failed,
                "🗑 ПОЛНАЯ ОЧИСТКА", "Ошибка: " + ex.Message);
        }
    }

    /// <summary>
    /// Аварийная авто-очистка античит-игры: убирает ЛЮБЫЕ наши прокси,
    /// маркеры и конфиги из папки. Возвращает true, если это античит-игра.
    /// </summary>
    public bool PurgeAntiCheatDirectory(string gameDirectory, out string note)
    {
        note = string.Empty;

        try
        {
            if (!HasAntiCheatOrPreloader(gameDirectory, out string reason)) return false;

            note = RemoveAllArtifacts(gameDirectory);
            RemoveOurNeuralRuntime(gameDirectory);
            if (string.IsNullOrWhiteSpace(note))
                note = "чисто";

            return true;
        }
        catch
        {
            note = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Убирает наш nvngx_dlssnr.dll (NR-рантайм DLSS 5), но только если его размер
    /// совпадает с нашим источником — файл самой игры не трогаем.
    /// </summary>
    private static void RemoveOurNeuralRuntime(string gameDirectory)
    {
        try
        {
            const string fileName = "nvngx_dlssnr.dll";
            string target = Path.Combine(gameDirectory, fileName);
            if (!File.Exists(target)) return;

            long size = new FileInfo(target).Length;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var candidates = new[]
            {
                Path.Combine(SupplementSourceRoot, "live-flow", "native", "dlss 5", fileName),
                Path.Combine(SupplementSourceRoot, "live-flow", "native", fileName),
                Path.Combine(EncoreSourceDirectory, "dlss 5", fileName),
                Path.Combine(EncoreSourceDirectory, fileName),
                Path.Combine(localAppData, "Programs", "DLSS 5 MANAGER", "mod files", "dlss 5", fileName),
                Path.Combine(localAppData, "Programs", "DLSS 5 MANAGER", "mod files", "native", "dlss 5", fileName),
                Path.Combine(localAppData, "Programs", "DLSS 5 MANAGER", "mod files", "native", fileName)
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    if (new FileInfo(candidate).Length != size) continue;

                    DeleteOrSchedule(target);
                    return;
                }
                catch { }
            }

            // Известный размер нашего NR из DLSS 5 MANAGER.
            if (size == 276_162_560) DeleteOrSchedule(target);
        }
        catch
        {
            // ignore
        }
    }

    private static string RemoveAllArtifacts(string dir)
    {
        var notes = new List<string>();

        notes.Add(RemoveOptiScalerLeftovers(dir));
        notes.Add(RemoveClassicArtifacts(dir, force: true));
        notes.Add(RemoveEncoreArtifacts(dir, force: true, removeConfig: true, removeLogs: true));

        // все .off-файлы наших прокси
        foreach (string proxy in AllProxyNames)
        {
            string off = Path.Combine(dir, proxy + ".off");
            if (FileMatchesKnownProxy(off)) DeleteOrSchedule(off);
        }

        // логи/диагностика, которые могли остаться
        DeleteOrSchedule(Path.Combine(dir, "dlssg_sm75_inject.log"));
        DeleteOrSchedule(Path.Combine(dir, "rtx-encore_perf.csv"));
        DeleteOrSchedule(Path.Combine(dir, "rtx-encore.jsonc.chzm-backup"));

        try
        {
            foreach (string csv in Directory.EnumerateFiles(dir, "rtx-encore_frames_*.jsonl"))
                DeleteOrSchedule(csv);
        }
        catch { }

        RemoveDirectoryBestEffort(Path.Combine(dir, EncoreCapturesFolderName));

        DeleteOrSchedule(Path.Combine(dir, ProxyMarkerName));
        DeleteOrSchedule(Path.Combine(dir, OptiScalerMarker));

        DeleteOrSchedule(Path.Combine(dir, "dlssg_sm86.ini"));
        RemoveDirectoryBestEffort(Path.Combine(dir, ModFolderName));

        // на всякий случай: пустые папки нашего мода
        RemoveDirectoryBestEffort(Path.Combine(dir, "xefg"));

        bool residue = false;
        try
        {
            foreach (string artifact in new[]
                     {
                         "dlssg_sm86.ini", EncoreConfigName, ProxyMarkerName, OptiScalerMarker,
                         "OptiScaler.ini", "OptiScaler.log"
                     })
            {
                if (File.Exists(Path.Combine(dir, artifact))) residue = true;
            }

            if (Directory.Exists(Path.Combine(dir, ModFolderName))) residue = true;
            if (Directory.Exists(Path.Combine(dir, EncoreLogsFolderName))) residue = true;
            if (Directory.Exists(Path.Combine(dir, "OptiScaler"))) residue = true;
            if (Directory.EnumerateFiles(dir, "*.off").Any()) residue = true;
        }
        catch { }

        string note = notes.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;
        if (residue) note += " · часть файлов занята игрой, удалится после её закрытия/перезагрузки";

        return note;
    }

    private static string RemoveClassicArtifacts(string dir, bool force)
    {
        try
        {
            string? engine = ReadMarkerEngine(dir);
            bool ours = force || engine == EngineClassic || HasModPayload(dir);

            if (!ours) return string.Empty;

            bool removed = false;

            foreach (string proxy in TensorProxyOrder)
            {
                string path = Path.Combine(dir, proxy);

                bool shouldRemove = engine == EngineClassic && MarkerNamesProxy(dir, proxy);
                if (!shouldRemove && FileMatchesClassicProxy(path)) shouldRemove = true;
                if (!shouldRemove && FileMatchesClassicProxy(path + ".off")) shouldRemove = true;

                if (!shouldRemove) continue;

                RemoveProxyFile(path);
                RemoveProxyFile(path + ".off");
                removed = true;
            }

            DeleteOrSchedule(Path.Combine(dir, IniFileName));
            DeleteOrSchedule(Path.Combine(dir, "dlssg_sm75_inject.log"));
            RemoveDirectoryBestEffort(Path.Combine(dir, ModFolderName));

            if (engine == EngineClassic) DeleteOrSchedule(Path.Combine(dir, ProxyMarkerName));

            return removed ? " · убран Classic" : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string RemoveEncoreArtifacts(string dir, bool force, bool removeConfig, bool removeLogs)
    {
        try
        {
            string? engine = ReadMarkerEngine(dir);
            bool markerOurs = engine is EngineEncore or EngineSmooth;

            string? foundProxy = FindEncoreProxy(dir);

            if (!force && !markerOurs && foundProxy is null)
                return string.Empty;

            bool removed = false;

            foreach (string proxy in EncoreProxyOrder)
            {
                string path = Path.Combine(dir, proxy);

                bool shouldRemove = markerOurs && MarkerNamesProxy(dir, proxy);
                if (!shouldRemove && FileMatchesEncoreProxy(path)) shouldRemove = true;
                if (!shouldRemove && FileMatchesEncoreProxy(path + ".off")) shouldRemove = true;

                if (!shouldRemove) continue;

                RemoveProxyFile(path);
                RemoveProxyFile(path + ".off");
                removed = true;
            }

            if (removeConfig && (force || markerOurs || foundProxy is not null))
            {
                DeleteOrSchedule(Path.Combine(dir, EncoreConfigName));
                DeleteOrSchedule(Path.Combine(dir, EncoreConfigBackupName));
            }

            if (removeLogs)
                RemoveDirectoryBestEffort(Path.Combine(dir, EncoreLogsFolderName));

            if (markerOurs) DeleteOrSchedule(Path.Combine(dir, ProxyMarkerName));

            return removed ? " · убран Encore/SM" : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool MarkerNamesProxy(string dir, string proxy)
    {
        try
        {
            string marker = Path.Combine(dir, ProxyMarkerName);
            if (!File.Exists(marker)) return false;

            string? value = ExtractMarkerValue(File.ReadAllText(marker), "proxy");
            return string.Equals(value, proxy, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Удаляет прокси. Если DLL загружена игрой, File.Delete не работает, зато
    /// переименование в &lt;имя&gt;.off проходит сразу — имя освобождается, и новый
    /// движок можно ставить без перезапуска. Сам .off уйдёт после закрытия игры.
    /// </summary>
    private static string RemoveProxyFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
        }
        catch
        {
            return string.Empty;
        }

        try
        {
            File.Delete(path);
            return "removed";
        }
        catch
        {
            // DLL загружена — переименовываем
        }

        string off = path + ".off";

        try
        {
            DeleteQuietly(off);
            File.Move(path, off, overwrite: true);
            DeleteOrSchedule(off);
            return "renamed";
        }
        catch
        {
            // не вышло и переименовать — планируем на перезагрузку
        }

        try
        {
            if (MoveFileEx(path, null, MoveFileDelayUntilReboot)) return "scheduled";
        }
        catch { }

        return "busy";
    }

    private static void RemoveDirectoryBestEffort(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
        }
        catch
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch
        {
            // DLL/лог заняты — удаляем по файлам, что можно, остальное после перезагрузки
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                DeleteOrSchedule(file);
        }
        catch { }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // папка уйдёт после перезагрузки/закрытия игры
        }
    }
}
