using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DrawingIcon = System.Drawing.Icon;

namespace CHZMFRAMEUNLOCK1.Services;

/// <summary>Профиль совместимости игры, который считается ДО запуска игры.</summary>
public sealed record GameCapabilityProfile(
    bool HasAntiCheatOrPreloader,
    string AntiCheatReason,
    bool HasNativeDlssFg,
    bool HasNativeDlss,
    bool HasDlssNr,
    bool ProxyModesBlocked,
    EngineMode? Recommended,
    bool LiveFlowRecommended,
    string ShortRecommendation,
    string Verdict);

/// <summary>Найденная игра в библиотеке.</summary>
public sealed class GameEntry
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Папка найденного рабочего .exe — сюда ставится мод и отсюда читается статус.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Корень игры (библиотека Steam/Epic) — только для античита, обложек и поиска.</summary>
    public string RootDirectory { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string SteamAppId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public GameCapabilityProfile Profile { get; set; } = GameLibraryScanner.EmptyProfile;
    public bool IsManual { get; set; }
    public bool IsRunning { get; set; }

    public EngineMode? InstalledMode { get; set; }
    public bool ModEnabled { get; set; }
    public string BadgeText { get; set; } = string.Empty;

    public string LaunchKey => string.IsNullOrWhiteSpace(SteamAppId) ? ExecutablePath : "steam:" + SteamAppId;

    public override string ToString() => $"{Name} ({Source})";
}

/// <summary>
/// Автоскан установленных игр: Steam (libraryfolders.vdf + appmanifest_*.acf),
/// Epic/GOG, C:\Games и D:\Games, Minecraft, плюс добавленные вручную .exe.
/// Обложки: локальный кэш Steam и иконки/картинки из папки игры.
/// </summary>
public sealed class GameLibraryScanner
{
    public static readonly GameCapabilityProfile EmptyProfile = new(
        HasAntiCheatOrPreloader: false,
        AntiCheatReason: string.Empty,
        HasNativeDlssFg: false,
        HasNativeDlss: false,
        HasDlssNr: false,
        ProxyModesBlocked: false,
        Recommended: null,
        LiveFlowRecommended: false,
        ShortRecommendation: "—",
        Verdict: "Профиль ещё не посчитан.");

    private static readonly string[] JunkDirectoryParts =
    {
        "steamworks shared", "steamworks redistributables", "_commonredist", "commonredist",
        "redist", "redistributable", "directx", "vcredist", "dotnet", "easyanticheat",
        "easyanticheat_eos", "battleye", "anticheat", "support", "installer", "installers",
        "dxsetup", "physx", "crashreport", "crashhandler", "diagnostics", "symbols", "pdb"
    };

    private static readonly string[] BadExecutableParts =
    {
        "unins", "setup", "install", "vcredist", "dxsetup", "dxwebsetup", "easyanticheat",
        "beservice", "battleye", "crash", "report", "helper", "unitycrashhandler",
        "ue4prereqsetup", "ue5prereqsetup", "dotnet", "python", "node", "cefsubprocess",
        "eossdk", "steamerrorreporter", "activate", "register", "configurator", "cleanup"
    };

    private static readonly string[] CoverFileNames =
    {
        "cover.jpg", "cover.jpeg", "cover.png", "poster.jpg", "poster.png",
        "boxart.jpg", "boxart.png", "box_art.jpg", "box_art.png",
        "library_600x900.jpg", "library_600x900.png", "header.jpg", "header.png",
        "logo.png", "icon.png", "game.ico"
    };

    /// <summary>Популярные AppId — для игр, у которых пропал appmanifest, но папка осталась.</summary>
    private static readonly Dictionary<string, string> KnownSteamAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["the witcher 3"] = "292030",
        ["the witcher 3 wild hunt"] = "292030",
        ["cyberpunk 2077"] = "1091500",
        ["grand theft auto v"] = "271590",
        ["red dead redemption 2"] = "1174180",
        ["elden ring"] = "1245620",
        ["baldurs gate 3"] = "1086940",
        ["hogwarts legacy"] = "990080",
        ["starfield"] = "1716740",
        ["space marine 2"] = "1085660",
        ["warhammer 40000 space marine 2"] = "1085660"
    };

    /// <summary>Разрешить один раз скачать отсутствующую обложку Steam в локальный кэш.</summary>
    public static bool EnableOnlineCovers { get; set; } = true;

    private static readonly HttpClient CoverHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly HashSet<string> FailedSteamCovers = new(StringComparer.OrdinalIgnoreCase);

    private readonly TensorAutoService _tensor;
    private readonly string? _steamRootOverride;
    private readonly IReadOnlyList<string>? _extraRootsOverride;
    private readonly string _storePath;

    public GameLibraryScanner(
        TensorAutoService tensor,
        string? steamRoot = null,
        IEnumerable<string>? extraRoots = null,
        string? storePath = null)
    {
        _tensor = tensor;
        _steamRootOverride = steamRoot;
        _extraRootsOverride = extraRoots?.ToArray();
        _storePath = storePath ?? Path.Combine(AppContext.BaseDirectory, "game-library.json");
    }

    public string StorePath => _storePath;

    // ===================== ПУБЛИЧНЫЙ СКАН =====================

    /// <summary>Полный скан библиотеки. Никогда не бросает исключение наружу.</summary>
    public List<GameEntry> Scan()
    {
        var games = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        string? steamRoot = _steamRootOverride ?? FindSteamRoot();

        try
        {
            ScanSteam(steamRoot, games);
        }
        catch
        {
            // Steam может быть не установлен / нет прав
        }

        var running = GetRunningProcessNames();

        foreach (string root in GetGameRoots())
        {
            try
            {
                ScanFolderRoot(root, games);
            }
            catch
            {
                // нет доступа к папке
            }
        }

        try
        {
            ScanEpicManifests(games);
        }
        catch
        {
            // Epic не установлен
        }

        foreach (string exe in LoadManualGames())
        {
            try
            {
                if (!File.Exists(exe)) continue;
                GameEntry entry = CreateEntryFromExecutable(exe, "Добавлено вручную", isManual: true);
                if (entry.Name.Length == 0) continue;

                // Не дублируем игру, которую уже нашли Steam/папки.
                bool duplicate = games.Values.Any(g =>
                    string.Equals(g.ExecutablePath, entry.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(g.Directory, entry.Directory, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(g.RootDirectory) &&
                     string.Equals(g.RootDirectory, entry.RootDirectory, StringComparison.OrdinalIgnoreCase)));

                if (duplicate) continue;

                games[entry.Directory] = entry;
            }
            catch
            {
                // битый путь
            }
        }

        foreach (GameEntry game in games.Values)
        {
            try
            {
                game.Profile = BuildProfile(
                    string.IsNullOrWhiteSpace(game.RootDirectory) ? game.Directory : game.RootDirectory);
                game.CoverPath ??= ResolveCover(game, steamRoot);
                game.IsRunning = game.ProcessName.Length > 0 && running.Contains(game.ProcessName);
                RefreshStatus(game);
            }
            catch
            {
                // одна игра не должна ломать весь список
            }
        }

        return games.Values
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Обновить состояние мода у одной игры (после установки/снятия).</summary>
    public void RefreshStatus(GameEntry game)
    {
        try
        {
            var active = new ActiveGameInfo(game.ProcessName, game.ExecutablePath, game.Directory);
            EngineStatus status = _tensor.GetEngineStatus(active);

            game.InstalledMode = status.Mode;
            game.ModEnabled = status.Enabled;

            if (status.Mode is null || !status.Installed)
            {
                game.BadgeText = string.Empty;
                return;
            }

            game.BadgeText = status.Enabled
                ? "ВКЛЮЧЕНО (" + BadgeMultiplier(status) + ")"
                : "УСТАНОВЛЕНО";
        }
        catch
        {
            game.BadgeText = string.Empty;
        }
    }

    private static string BadgeMultiplier(EngineStatus status) => status.Mode switch
    {
        EngineMode.ClassicSm86 => TensorAutoService.FramesToMultiplier(status.MaxFrames),
        EngineMode.RtxEncore => "ENCORE",
        EngineMode.SmoothMotion => "SM",
        _ => "ON"
    };

    // ===================== STEAM =====================

    public static string? FindSteamRoot()
    {
        var candidates = new List<string>();

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string userPath && userPath.Length > 0)
                candidates.Add(userPath.Replace('/', '\\'));
        }
        catch { }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string machinePath && machinePath.Length > 0)
                candidates.Add(machinePath.Replace('/', '\\'));
        }
        catch { }

        candidates.Add(@"C:\Program Files (x86)\Steam");
        candidates.Add(@"C:\Program Files\Steam");
        candidates.Add(@"D:\Steam");
        candidates.Add(@"D:\SteamLibrary");

        foreach (string candidate in candidates)
        {
            try
            {
                if (Directory.Exists(Path.Combine(candidate, "steamapps"))) return candidate;
            }
            catch { }
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Парсит libraryfolders.vdf (новый и старый формат) и возвращает пути библиотек.</summary>
    public static List<string> ParseLibraryFolders(string vdfPath)
    {
        var result = new List<string>();

        try
        {
            if (!File.Exists(vdfPath)) return result;

            string text = File.ReadAllText(vdfPath).Replace("\\\\", "\\");

            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"",
                         RegexOptions.IgnoreCase))
            {
                string path = match.Groups[1].Value.Trim();
                if (path.Length > 0) result.Add(path);
            }

            // Старый формат: "1"  "D:\\SteamLibrary"
            if (result.Count == 0)
            {
                foreach (Match match in Regex.Matches(text, "\"\\d+\"\\s+\"([^\"]+)\""))
                {
                    string path = match.Groups[1].Value.Trim();
                    if (path.Length > 2 && path.Contains(':')) result.Add(path);
                }
            }
        }
        catch
        {
            // ignore
        }

        return result
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Данные appmanifest_*.acf.</summary>
    public sealed record SteamManifest(string AppId, string Name, string InstallDir);

    public static SteamManifest? ParseAppManifest(string acfPath)
    {
        try
        {
            if (!File.Exists(acfPath)) return null;

            string text = File.ReadAllText(acfPath).Replace("\\\\", "\\");

            string? appId = MatchValue(text, "appid");
            string? name = MatchValue(text, "name");
            string? installDir = MatchValue(text, "installdir");

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(installDir))
                return null;

            return new SteamManifest(appId.Trim(), name?.Trim() ?? installDir.Trim(), installDir.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static string? MatchValue(string text, string key)
    {
        Match match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private void ScanSteam(string? steamRoot, Dictionary<string, GameEntry> games)
    {
        if (steamRoot is null) return;

        var libraries = new List<string> { steamRoot };

        foreach (string library in ParseLibraryFolders(
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf")))
        {
            if (!libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                libraries.Add(library);
        }

        foreach (string library in libraries)
        {
            try
            {
                string apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps)) continue;

                foreach (string manifestPath in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    SteamManifest? manifest = ParseAppManifest(manifestPath);
                    if (manifest is null) continue;

                    string gameDirectory = Path.Combine(apps, "common", manifest.InstallDir);
                    if (!Directory.Exists(gameDirectory)) continue;
                    if (LooksLikeJunkDirectory(manifest.InstallDir)) continue;

                    string? exe = FindExecutable(gameDirectory, manifest.Name);
                    if (exe is null) continue;

                    var entry = new GameEntry
                    {
                        Name = string.IsNullOrWhiteSpace(manifest.Name)
                            ? Path.GetFileName(gameDirectory)
                            : manifest.Name,
                        Directory = Path.GetDirectoryName(exe) ?? gameDirectory,
                        RootDirectory = gameDirectory,
                        ExecutablePath = exe,
                        ProcessName = Path.GetFileNameWithoutExtension(exe),
                        SteamAppId = manifest.AppId,
                        Source = "Steam"
                    };

                    games[gameDirectory] = entry;
                }

                // Папки без appmanifest (игру скопировали/манифест потерялся).
                string common = Path.Combine(apps, "common");
                if (Directory.Exists(common))
                {
                    foreach (string gameDirectory in Directory.EnumerateDirectories(common))
                    {
                        try
                        {
                            if (games.ContainsKey(gameDirectory)) continue;

                            string folder = Path.GetFileName(gameDirectory);
                            if (LooksLikeJunkDirectory(folder)) continue;

                            string? exe = FindExecutable(gameDirectory, folder);
                            if (exe is null) continue;

                            string appId = ResolveKnownAppId(folder);
                            string name = CleanGameName(folder);

                            games[gameDirectory] = new GameEntry
                            {
                                Name = name,
                                Directory = Path.GetDirectoryName(exe) ?? gameDirectory,
                                RootDirectory = gameDirectory,
                                ExecutablePath = exe,
                                ProcessName = Path.GetFileNameWithoutExtension(exe),
                                SteamAppId = appId,
                                Source = "Steam"
                            };
                        }
                        catch
                        {
                            // одна папка — не повод падать
                        }
                    }
                }
            }
            catch
            {
                // одна библиотека недоступна — идём дальше
            }
        }
    }

    private static string ResolveKnownAppId(string folder)
    {
        string normalized = NormalizeName(folder);

        foreach (KeyValuePair<string, string> pair in KnownSteamAppIds)
        {
            if (NormalizeName(pair.Key) == normalized) return pair.Value;
        }

        return string.Empty;
    }

    // ===================== НЕ-STEAM =====================

    private IEnumerable<string> GetGameRoots()
    {
        if (_extraRootsOverride is not null) return _extraRootsOverride;

        var roots = new List<string>();

        void Add(string? path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) roots.Add(path);
            }
            catch { }
        }

        foreach (string drive in new[] { "C:", "D:", "E:", "F:" })
        {
            Add(drive + @"\Games");
            Add(drive + @"\GOG Games");
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "GOG Galaxy", "Games"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher"));

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void ScanFolderRoot(string root, Dictionary<string, GameEntry> games)
    {
        if (!Directory.Exists(root)) return;

        // Minecraft Launcher лежит прямо в корне.
        string launcher = Path.Combine(root, "MinecraftLauncher.exe");
        if (File.Exists(launcher))
        {
            GameEntry minecraft = CreateEntryFromExecutable(launcher, "Minecraft", isManual: false);
            minecraft.Name = "Minecraft";
            games[minecraft.Directory] = minecraft;
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                string name = Path.GetFileName(directory);
                if (LooksLikeJunkDirectory(name)) continue;

                // CoD Campaign: рабочий exe лежит в подпапке sp26, а не в корне.
                string searchDirectory = directory;
                if (LooksLikeSinglePlayerCampaign(name))
                {
                    string sp26 = Path.Combine(directory, "sp26");
                    try
                    {
                        if (Directory.Exists(sp26) &&
                            Directory.EnumerateFiles(sp26, "*.exe").Any())
                            searchDirectory = sp26;
                    }
                    catch { }
                }

                string? exe = FindExecutable(searchDirectory, name);
                if (exe is null) continue;

                var entry = new GameEntry
                {
                    Name = CleanGameName(name),
                    Directory = Path.GetDirectoryName(exe) ?? directory,
                    RootDirectory = directory,
                    ExecutablePath = exe,
                    ProcessName = Path.GetFileNameWithoutExtension(exe),
                    Source = DetectSource(root)
                };

                games[directory] = entry;
            }
            catch
            {
                // нет прав / битая папка
            }
        }
    }

    private static bool LooksLikeSinglePlayerCampaign(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        string normalized = name.ToLowerInvariant()
            .Replace('.', ' ').Replace('_', ' ').Replace('-', ' ');

        return normalized.Contains("campaign") ||
               normalized.Contains("singleplayer") ||
               normalized.Contains("insaneramzes");
    }

    private static string DetectSource(string root)    {
        if (root.Contains("Epic", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (root.Contains("GOG", StringComparison.OrdinalIgnoreCase)) return "GOG";
        if (root.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) return "Minecraft";
        return "Папка";
    }

    private void ScanEpicManifests(Dictionary<string, GameEntry> games)
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string manifests = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifests)) return;

        foreach (string file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = document.RootElement;

                string? location = GetString(root, "InstallLocation");
                if (string.IsNullOrWhiteSpace(location) || !Directory.Exists(location)) continue;

                string displayName = GetString(root, "DisplayName") ?? Path.GetFileName(location);
                string? launch = GetString(root, "LaunchExecutable");
                string? exe = null;

                if (!string.IsNullOrWhiteSpace(launch))
                {
                    string candidate = Path.Combine(location, launch.Replace('/', '\\'));
                    if (File.Exists(candidate)) exe = candidate;
                }

                exe ??= FindExecutable(location, displayName);
                if (exe is null) continue;

                games[location] = new GameEntry
                {
                    Name = displayName,
                    Directory = Path.GetDirectoryName(exe) ?? location,
                    RootDirectory = location,
                    ExecutablePath = exe,
                    ProcessName = Path.GetFileNameWithoutExtension(exe),
                    Source = "Epic Games"
                };
            }
            catch
            {
                // битый манифест
            }
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        }

        return null;
    }

    // ===================== ПОИСК EXE И ОБЛОЖКИ =====================

    public static bool LooksLikeJunkDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;

        string lower = name.Trim().ToLowerInvariant();

        foreach (string part in JunkDirectoryParts)
        {
            if (lower == part || lower.Contains(part, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Главный рабочий .exe игры. Приоритет: Unreal &lt;Project&gt;\Binaries\Win64,
    /// затем exe в корне, затем самый крупный осмысленный exe на глубине до 3.
    /// </summary>
    public static string? FindExecutable(string directory, string? hintName = null)
    {
        if (!Directory.Exists(directory)) return null;

        var candidates = new List<string>();
        string normalizedHint = NormalizeName(hintName ?? Path.GetFileName(directory));

        void Add(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(path);
            }
            catch { }
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.exe"))
                Add(file);

            foreach (string project in Directory.EnumerateDirectories(directory))
            {
                try
                {
                    foreach (string binaries in new[] { "Win64", "WinGDK", "Win32" })
                    {
                        string bin = Path.Combine(project, "Binaries", binaries);
                        if (!Directory.Exists(bin)) continue;

                        foreach (string file in Directory.EnumerateFiles(bin, "*.exe"))
                            Add(file);
                    }
                }
                catch { }
            }

            CollectExecutables(directory, 0, 3, candidates);
        }
        catch
        {
            // ignore
        }

        string? best = null;
        int bestScore = int.MinValue;

        foreach (string candidate in candidates)
        {
            int score = ScoreExecutable(candidate, directory, normalizedHint);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore > -50 ? best : null;
    }

    private static void CollectExecutables(string directory, int depth, int maxDepth,
        List<string> candidates)
    {
        if (depth >= maxDepth) return;

        try
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);

                if (LooksLikeJunkDirectory(name) ||
                    name.Equals("Engine", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (string file in Directory.EnumerateFiles(child, "*.exe"))
                {
                    if (!candidates.Contains(file, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(file);
                }

                CollectExecutables(child, depth + 1, maxDepth, candidates);
            }
        }
        catch
        {
            // нет прав
        }
    }

    private static int ScoreExecutable(string path, string root, string hint)
    {
        int score = 0;

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            string lower = fileName.ToLowerInvariant();
            string full = path.ToLowerInvariant();

            foreach (string bad in BadExecutableParts)
            {
                if (lower.Contains(bad, StringComparison.Ordinal)) score -= 120;
            }

            if (full.Contains(@"\engine\", StringComparison.Ordinal) ||
                full.Contains(@"\thirdparty\", StringComparison.Ordinal) ||
                full.Contains(@"\redist\", StringComparison.Ordinal))
                score -= 100;

            if (full.Contains(@"\binaries\win64\", StringComparison.Ordinal)) score += 90;

            string parent = Path.GetDirectoryName(path) ?? string.Empty;
            if (string.Equals(parent.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                score += 55;

            string normalizedFile = NormalizeName(fileName);
            if (hint.Length > 1)
            {
                if (normalizedFile == hint) score += 80;
                else if (normalizedFile.Contains(hint, StringComparison.Ordinal) ||
                         hint.Contains(normalizedFile, StringComparison.Ordinal))
                    score += 40;
            }

            if (lower.Contains("shipping")) score += 15;
            if (lower.Contains("launcher")) score -= 20;

            long size = new FileInfo(path).Length;
            score += (int)Math.Min(30, Math.Max(0, size / (5L * 1024 * 1024)) * 3);
        }
        catch
        {
            // ignore
        }

        return score;
    }

    private static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);

        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
        }

        return builder.ToString();
    }

    public static string? ResolveCover(GameEntry game, string? steamRoot)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(game.SteamAppId))
            {
                string? steamCover = FindSteamCover(steamRoot, game.SteamAppId);
                if (steamCover is not null) return steamCover;
            }

            string coverRoot = string.IsNullOrWhiteSpace(game.RootDirectory) ? game.Directory : game.RootDirectory;

            string? local = FindLocalCover(coverRoot);
            if (local is not null) return local;

            return ExtractIconCover(game.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }

    public static string? FindSteamCover(string? steamRoot, string appId)
    {
        if (steamRoot is null || string.IsNullOrWhiteSpace(appId)) return null;

        var candidates = new List<string>();

        string cache = Path.Combine(steamRoot, "appcache", "librarycache");
        candidates.Add(Path.Combine(cache, appId + "_library_600x900.jpg"));
        candidates.Add(Path.Combine(cache, appId, "library_600x900.jpg"));
        candidates.Add(Path.Combine(cache, appId + "_library_600x900.png"));
        candidates.Add(Path.Combine(cache, appId, "library_600x900.png"));
        candidates.Add(Path.Combine(cache, appId + "_header.jpg"));
        candidates.Add(Path.Combine(cache, appId, "header.jpg"));
        candidates.Add(Path.Combine(cache, appId + "_logo.png"));

        // Пользовательские обложки Steam Grid.
        try
        {
            string userdata = Path.Combine(steamRoot, "userdata");
            if (Directory.Exists(userdata))
            {
                foreach (string user in Directory.EnumerateDirectories(userdata).Take(6))
                {
                    string grid = Path.Combine(user, "config", "grid");
                    candidates.Add(Path.Combine(grid, appId + "_library_600x900.jpg"));
                    candidates.Add(Path.Combine(grid, appId + "_library_600x900.png"));
                    candidates.Add(Path.Combine(grid, appId + ".jpg"));
                    candidates.Add(Path.Combine(grid, appId + ".png"));
                }
            }
        }
        catch { }

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1024)
                    return candidate;
            }
            catch { }
        }

        return DownloadSteamCover(appId);
    }

    /// <summary>
    /// Если Steam ещё не положил обложку в локальный кэш (частый случай для
    /// новых установок), один раз скачиваем её с CDN и кэшируем локально.
    /// </summary>
    private static string? DownloadSteamCover(string appId)
    {
        if (!EnableOnlineCovers || FailedSteamCovers.Contains(appId)) return null;

        try
        {
            string cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CHZMFRAMEUNLOCK", "covers");

            Directory.CreateDirectory(cache);

            string target = Path.Combine(cache, "steam_" + appId + "_600x900.jpg");
            if (File.Exists(target) && new FileInfo(target).Length > 10_000) return target;

            string[] urls =
            {
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_616x353.jpg"
            };

            foreach (string url in urls)
            {
                try
                {
                    byte[] bytes = CoverHttp.GetByteArrayAsync(url).GetAwaiter().GetResult();
                    if (bytes.Length < 5000) continue;

                    File.WriteAllBytes(target, bytes);
                    return target;
                }
                catch
                {
                    // пробуем следующий размер
                }
            }

            FailedSteamCovers.Add(appId);
            return null;
        }
        catch
        {
            FailedSteamCovers.Add(appId);
            return null;
        }
    }

    public static string? FindLocalCover(string gameDirectory)
    {
        try
        {
            foreach (string name in CoverFileNames)
            {
                string path = Path.Combine(gameDirectory, name);
                if (File.Exists(path) && new FileInfo(path).Length > 1024) return path;
            }

            foreach (string directory in new[] { gameDirectory, Path.Combine(gameDirectory, "Media"),
                         Path.Combine(gameDirectory, "Content"), Path.Combine(gameDirectory, "Assets") })
            {
                if (!Directory.Exists(directory)) continue;

                foreach (string file in Directory.EnumerateFiles(directory)
                             .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                             .Take(80))
                {
                    string name = Path.GetFileName(file).ToLowerInvariant();

                    if ((name.Contains("cover") || name.Contains("poster") ||
                         name.Contains("boxart") || name.Contains("box_art") ||
                         name.Contains("header") || name.Contains("library")) &&
                        new FileInfo(file).Length > 4096)
                        return file;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static string? ExtractIconCover(string executablePath)
    {
        try
        {
            if (!File.Exists(executablePath)) return null;

            string cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CHZMFRAMEUNLOCK", "covers");

            Directory.CreateDirectory(cache);

            string hash;
            using (SHA256 sha = SHA256.Create())
                hash = Convert.ToHexString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(executablePath.ToLowerInvariant())))[..16];

            string target = Path.Combine(cache, hash + ".png");
            if (File.Exists(target) && new FileInfo(target).Length > 512) return target;

            using DrawingIcon? icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
            if (icon is null) return null;

            using Bitmap bitmap = icon.ToBitmap();
            bitmap.Save(target, ImageFormat.Png);

            return File.Exists(target) ? target : null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== ПРОФИЛЬ СОВМЕСТИМОСТИ =====================

    public static GameCapabilityProfile BuildProfile(string gameDirectory)
    {
        bool antiCheat = TensorAutoService.HasAntiCheatOrPreloader(gameDirectory, out string reason);
        bool hasFg = TensorAutoService.HasNativeDlssFrameGen(gameDirectory);
        bool hasDlss = TensorAutoService.HasAnyDlss(gameDirectory);
        bool hasNr = File.Exists(Path.Combine(gameDirectory, "nvngx_dlssnr.dll")) ||
                     File.Exists(Path.Combine(gameDirectory, "dlss 5", "nvngx_dlssnr.dll"));

        if (antiCheat)
        {
            string why = string.IsNullOrWhiteSpace(reason) ? "античит" : reason;

            return new GameCapabilityProfile(
                HasAntiCheatOrPreloader: true,
                AntiCheatReason: why,
                HasNativeDlssFg: hasFg,
                HasNativeDlss: hasDlss,
                HasDlssNr: hasNr,
                ProxyModesBlocked: true,
                Recommended: null,
                LiveFlowRecommended: true,
                ShortRecommendation: "⚡ LIVE FLOW 2X (Quality · B)",
                Verdict: "⚡ Внешний захват DXGI + Optical Flow. Файлы игры не трогаются, " +
                         "перезапуск не нужен. Внутренние прокси недоступны: " + why + ".");
        }

        if (hasFg)
        {
            EngineMode? recommended = TensorAutoService.IsModSourceReady()
                ? EngineMode.ClassicSm86
                : TensorAutoService.IsEncoreReady() ? EngineMode.RtxEncore : null;

            return new GameCapabilityProfile(
                HasAntiCheatOrPreloader: false,
                AntiCheatReason: string.Empty,
                HasNativeDlssFg: true,
                HasNativeDlss: hasDlss,
                HasDlssNr: hasNr,
                ProxyModesBlocked: false,
                Recommended: recommended,
                LiveFlowRecommended: false,
                ShortRecommendation: "🎯 CLASSIC 4X + M-INT8",
                Verdict: "✅ Игра поддерживает нативный генератор NVIDIA. " +
                         "Мод ставится заранее — перезапуск игры не нужен!");
        }

        EngineMode smooth = EngineMode.SmoothMotion;

        return new GameCapabilityProfile(
            HasAntiCheatOrPreloader: false,
            AntiCheatReason: string.Empty,
            HasNativeDlssFg: false,
            HasNativeDlss: hasDlss,
            HasDlssNr: hasNr,
            ProxyModesBlocked: false,
            Recommended: smooth,
            LiveFlowRecommended: false,
            ShortRecommendation: "🚀 SMOOTH MOTION",
            Verdict: "🚀 В игре нет DLSS-G — включаем драйверный Smooth Motion " +
                     "или LIVE FLOW. CLASSIC для такой игры отключён.");
    }

    // ===================== РУЧНОЕ ДОБАВЛЕНИЕ =====================

    public GameEntry AddManualGame(string executablePath)
    {
        string full = Path.GetFullPath(executablePath);
        if (!File.Exists(full)) throw new FileNotFoundException("Игра не найдена", full);

        var entries = LoadManualGames();
        if (!entries.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(full);
            SaveManualGames(entries);
        }

        GameEntry entry = CreateEntryFromExecutable(full, "Добавлено вручную", isManual: true);
        entry.Profile = BuildProfile(entry.Directory);
        entry.CoverPath = ResolveCover(entry, FindSteamRoot());
        RefreshStatus(entry);
        return entry;
    }

    public void RemoveManualGame(string executablePath)
    {
        var entries = LoadManualGames();
        entries.RemoveAll(e => e.Equals(executablePath, StringComparison.OrdinalIgnoreCase));
        SaveManualGames(entries);
    }

    private static GameEntry CreateEntryFromExecutable(string exe, string source, bool isManual)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? string.Empty;
        string fallback = Path.GetFileNameWithoutExtension(exe);

        string name = fallback;

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(info.ProductName) &&
                info.ProductName.Length > 1 &&
                !info.ProductName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                name = info.ProductName.Trim();
        }
        catch { }

        if (name.Equals(fallback, StringComparison.OrdinalIgnoreCase))
            name = CleanGameName(Path.GetFileName(directory));

        return new GameEntry
        {
            Name = name,
            Directory = directory,
            RootDirectory = directory,
            ExecutablePath = exe,
            ProcessName = Path.GetFileNameWithoutExtension(exe),
            Source = source,
            IsManual = isManual
        };
    }

    private List<string> LoadManualGames()
    {
        try
        {
            if (!File.Exists(_storePath)) return new List<string>();

            string text = File.ReadAllText(_storePath);
            var data = JsonSerializer.Deserialize<GameLibraryStore>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return data?.ManualGames?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private void SaveManualGames(List<string> games)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath) ?? AppContext.BaseDirectory);

            var data = new GameLibraryStore { ManualGames = games };
            File.WriteAllText(_storePath,
                JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }),
                new UTF8Encoding(false));
        }
        catch
        {
            // нет прав — библиотека всё равно работает
        }
    }

    public static HashSet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName))
                        names.Add(process.ProcessName);
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

        return names;
    }

    private static string CleanGameName(string value)
    {
        try
        {
            string name = value.Replace('_', ' ').Replace('.', ' ').Trim();

            while (name.Contains("  ")) name = name.Replace("  ", " ");

            return name.Length == 0 ? value : name;
        }
        catch
        {
            return value;
        }
    }

    private sealed class GameLibraryStore
    {
        public List<string> ManualGames { get; set; } = new();
    }
}
