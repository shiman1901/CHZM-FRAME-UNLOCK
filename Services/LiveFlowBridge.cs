using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CHZMFRAMEUNLOCK1.Services;

/// <summary>Состояние LIVE FLOW: холодный → прогрев → спит в памяти ⇄ работает.</summary>
public enum LiveFlowState
{
    /// <summary>Процессов нет.</summary>
    Cold,
    /// <summary>Идёт прогрев (запуск python + worker).</summary>
    Warming,
    /// <summary>Движок в памяти, спит (0% нагрузки), готов к мгновенному включению.</summary>
    Warm,
    /// <summary>Фреймген работает.</summary>
    Active
}

/// <summary>Настройки LIVE FLOW, которые передаются установленному DLSS 5 MANAGER.</summary>
public sealed record LiveFlowOptions(
    int Multiplier = 2,
    bool QualityPreset = true,
    bool LowLatency = true,
    bool KeepStillPixels = true,
    bool RealtimePriority = true);

/// <summary>
/// Мост к уже установленному DLSS 5 MANAGER (NODIX-TECH).
///
/// ВАЖНО: бинарники LIVE FLOW / MFG / XeSS не копируются в проект.
/// Они принадлежат NODIX-TECH и NVIDIA и используются на месте, из
/// %LOCALAPPDATA%\Programs\DLSS 5 MANAGER. Мост запускает официальный
/// controller.py и общается с ним по его же JSON-пайпу — без реверса
/// и без перепаковки чужих файлов.
/// </summary>
public sealed class LiveFlowBridge : IDisposable
{
    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Портативная копия native-папки, если пользователь сам положил её в проект.</summary>
    public static string PortableNativeDirectory => Path.Combine(
        TensorAutoService.SupplementSourceRoot, "live-flow", "native");

    /// <summary>Оригинальная установка DLSS 5 MANAGER.</summary>
    public static string InstalledNativeDirectory => Path.Combine(
        LocalAppData, "Programs", "DLSS 5 MANAGER", "mod files", "native");

    private static bool HasNativeLayout(string directory) =>
        File.Exists(Path.Combine(directory, "runtime", "python.exe")) &&
        File.Exists(Path.Combine(directory, "screen", "controller.py")) &&
        File.Exists(Path.Combine(directory, "nvngx.dll"));

    public static bool UsesPortableNative => HasNativeLayout(PortableNativeDirectory);

    /// <summary>Сначала портативная папка проекта, затем установленный менеджер.</summary>
    public static string ManagerNativeDirectory => UsesPortableNative
        ? PortableNativeDirectory
        : InstalledNativeDirectory;

    public static string PythonPath => Path.Combine(ManagerNativeDirectory, "runtime", "python.exe");
    public static string ControllerPath => Path.Combine(ManagerNativeDirectory, "screen", "controller.py");
    public static string WorkerPath => Path.Combine(ManagerNativeDirectory, "nvngx.dll");

    /// <summary>
    /// NR-рантайм не обязателен для FLOW MFG: без него LIVE FLOW работает
    /// с выключенным DLSS 5 NR, но генерация кадров остаётся.
    /// </summary>
    public static string? NrRuntimePath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(PortableNativeDirectory, "dlss 5", "nvngx_dlssnr.dll"),
                Path.GetFullPath(Path.Combine(PortableNativeDirectory, "..", "dlss 5", "nvngx_dlssnr.dll")),
                Path.GetFullPath(Path.Combine(InstalledNativeDirectory, "..", "dlss 5", "nvngx_dlssnr.dll")),
                Path.Combine(PortableNativeDirectory, "nvngx_dlssnr.dll")
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            return null;
        }
    }

    public static string DataDirectory => Path.Combine(
        AppContext.BaseDirectory, "ДОПОЛНЕНИЕ", "live-flow", "runtime");

    public LiveFlowBridge()
    {
        EnsureRtssProfiles();
    }

    // ===================== RIVATUNER / RTSS =====================

    private static readonly string[] RtssProfileDirectories =
    {
        @"C:\Program Files (x86)\RivaTuner Statistics Server\Profiles",
        @"C:\Program Files\RivaTuner Statistics Server\Profiles"
    };

    /// <summary>Профили процессов, которым RTSS не должен рисовать второй счётчик.</summary>
    private static readonly string[] RtssProfileNames =
    {
        "python.exe.cfg",
        "pythonw.exe.cfg",
        "nvngx.dll.cfg",
        "nvngx.exe.cfg",
        "liveflow_radeon.exe.cfg"
    };

    /// <summary>
    /// Отключает хуки и OSD RivaTuner для python/worker LIVE FLOW, чтобы
    /// Afterburner не рисовал второй FPS поверх окна воркера.
    /// </summary>
    private static void EnsureRtssProfiles()
    {
        foreach (string directory in RtssProfileDirectories)
        {
            try
            {
                if (!Directory.Exists(directory)) continue;

                foreach (string name in RtssProfileNames)
                {
                    try
                    {
                        EnsureRtssProfile(Path.Combine(directory, name));
                    }
                    catch
                    {
                        // один профиль не должен ломать запуск
                    }
                }
            }
            catch
            {
                // нет прав / RTSS нет
            }
        }
    }

    private static void EnsureRtssProfile(string path)
    {
        List<string> lines;

        try
        {
            lines = File.Exists(path)
                ? new List<string>(File.ReadAllLines(path))
                : new List<string>();
        }
        catch
        {
            lines = new List<string>();
        }

        SetIniValue(lines, "Hooking", "EnableHooking", "0");
        SetIniValue(lines, "OSD", "Enable", "0");

        try
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }
        catch
        {
            // RTSS может быть запущен / нет прав
        }
    }

    private static void SetIniValue(List<string> lines, string section, string key, string value)
    {
        int sectionIndex = lines.FindIndex(line =>
            line.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));

        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add(string.Empty);
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
            return;
        }

        int end = lines.Count;
        for (int i = sectionIndex + 1; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        for (int i = sectionIndex + 1; i < end; i++)
        {
            string trimmed = lines[i].Trim();
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            if (trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = key + "=" + value;
                return;
            }
        }

        lines.Insert(sectionIndex + 1, key + "=" + value);
    }

    public static bool IsInstalled =>
        HasNativeLayout(ManagerNativeDirectory);

    public static string InstallReport
    {
        get
        {
            if (IsInstalled)
            {
                string where = UsesPortableNative ? "портативная native" : "установленный DLSS 5 MANAGER";
                return NrRuntimePath is null
                    ? $"LIVE FLOW: {where} · NR-рантайм не найден (MFG работает, NR off)"
                    : $"LIVE FLOW: {where}";
            }

            var missing = new List<string>();
            if (!File.Exists(PythonPath)) missing.Add("runtime\\python.exe");
            if (!File.Exists(ControllerPath)) missing.Add("screen\\controller.py");
            if (!File.Exists(WorkerPath)) missing.Add("nvngx.dll (LIVE FLOW worker)");

            return "LIVE FLOW не найден: " + string.Join(", ", missing) +
                   $" · искал {PortableNativeDirectory} и {InstalledNativeDirectory}";
        }
    }

    private readonly object _stateLock = new();
    private Process? _process;
    private StreamWriter? _input;
    private int _workerPid;
    private int _frames;
    private string _lastError = string.Empty;
    private string _status = string.Empty;
    private bool _disposed;

    /// <summary>Текущее состояние: Cold → Warming → Warm ⇄ Active.</summary>
    public LiveFlowState State { get; private set; } = LiveFlowState.Cold;

    /// <summary>Полный путь к .exe игры, с которой сейчас работаем (живёт до Stop).</summary>
    public string GameExecutable { get; private set; } = string.Empty;

    /// <summary>Папка игры (для проверки «вернулся ли в игру» при Alt+Tab).</summary>
    public string GameDirectory { get; private set; } = string.Empty;

    /// <summary>Последняя игра, для которой включали фреймген.</summary>
    public ActiveGameInfo? LastGame { get; private set; }

    /// <summary>Фреймген сейчас реально работает.</summary>
    public bool IsRunning => State == LiveFlowState.Active;

    /// <summary>Движок уже в памяти (спит или работает).</summary>
    public bool IsWarm => State is LiveFlowState.Warm or LiveFlowState.Active;

    public int WorkerPid
    {
        get { lock (_stateLock) return _workerPid; }
    }

    public int Frames
    {
        get { lock (_stateLock) return _frames; }
    }

    public string LastError
    {
        get { lock (_stateLock) return _lastError; }
    }

    public string Status
    {
        get { lock (_stateLock) return _status; }
    }

    // ===================== ПРОГРЕВ (СТЕНДБАЙ) =====================

    /// <summary>
    /// Тихо прогреть движок в памяти: worker уже загружен с хуками фреймгена,
    /// но presentation выключена и обработки нет — почти 0% GPU.
    /// </summary>
    public bool WarmUp(ActiveGameInfo? game, out string detail)
    {
        detail = string.Empty;

        if (IsWarm)
        {
            detail = "LIVE FLOW уже прогрет";
            return true;
        }

        if (State == LiveFlowState.Warming)
        {
            detail = "LIVE FLOW уже прогревается";
            return false;
        }

        if (!IsInstalled)
        {
            detail = InstallReport;
            return false;
        }

        State = LiveFlowState.Warming;

        try
        {
            Directory.CreateDirectory(DataDirectory);

            IntPtr gameWindow = IntPtr.Zero;
            if (game is not null)
            {
                GameWindow found = FindGameWindow(game);
                if (found.Handle != IntPtr.Zero) gameWindow = found.Handle;
            }

            WriteConfig(GetMonitor(gameWindow), new LiveFlowOptions());

            StartController();
            if (!WaitForWorker(20000))
            {
                detail = string.IsNullOrWhiteSpace(LastError)
                    ? "LIVE FLOW worker не ответил за 20 секунд"
                    : LastError;
                Stop();
                return false;
            }

            // Страховка: убедиться, что спим (флаги идемпотентные).
            SendSet("_leno4ka_standby", true);
            SendSet("nr", false);

            State = LiveFlowState.Warm;
            detail = "LIVE FLOW прогрет и спит в памяти";
            return true;
        }
        catch (Exception ex)
        {
            detail = "LIVE FLOW: " + ex.Message;
            Stop();
            return false;
        }
    }

    // ===================== ВКЛ / ВЫКЛ =====================

    /// <summary>
    /// Мгновенно включить фреймген на окне игры: у прогретого движка забирается
    /// окно (borderless), снимается спящий режим. Холодный старт — один раз дольше.
    /// </summary>
    public bool Activate(ActiveGameInfo game, LiveFlowOptions options, out string detail)
    {
        detail = string.Empty;

        if (State == LiveFlowState.Active)
        {
            detail = "Фреймген уже включён";
            return true;
        }

        if (State == LiveFlowState.Warming)
        {
            detail = "LIVE FLOW ещё прогревается — секунду";
            return false;
        }

        if (!IsInstalled)
        {
            detail = InstallReport;
            return false;
        }

        try
        {
            if (State == LiveFlowState.Cold)
            {
                if (!WarmUp(game, out detail)) return false;
            }

            GameWindow window = FindGameWindow(game);
            if (window.Handle == IntPtr.Zero)
            {
                detail = "Не найдено окно запущенной игры";
                return false;
            }

            string title = window.Title.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (title.Length > 120) title = title[..120];

            // 1) окно: движок делает его borderless и подключает захват.
            SendSet("window", $"{window.Handle.ToInt64():X}: {title}");
            // 2) выводим из спячки — кадры пойдут сразу.
            SendSet("_leno4ka_standby", false);
            SendSet("nr", false);

            WaitForFrames(6000);
            TrySetWorkerPriority(options.RealtimePriority);

            if (Math.Clamp(options.Multiplier, 2, 6) != 2)
            {
                SendSet("frame_multiplier", options.Multiplier);
                SendSet("flow_multiplier", options.Multiplier);
            }

            LastGame = game;
            GameExecutable = game.ExecutablePath;
            GameDirectory = game.Directory;
            State = LiveFlowState.Active;

            detail = $"LIVE FLOW MFG x{Math.Clamp(options.Multiplier, 2, 6)} · DXGI + NVIDIA Optical Flow";
            if (Frames == 0)
                detail += " · первые кадры ещё считаются";

            return true;
        }
        catch (Exception ex)
        {
            detail = "LIVE FLOW: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Мгновенная пауза БЕЗ убийства процесса: presentation выключается,
    /// worker остаётся прогретым в памяти.
    /// </summary>
    public void Deactivate()
    {
        if (State != LiveFlowState.Active) return;

        SendSet("nr", false);
        SendSet("_leno4ka_standby", true);

        State = LiveFlowState.Warm;
    }

    /// <summary>Поменять множитель/пресет/латентность на лету, без перезапуска.</summary>
    public void UpdateOptions(LiveFlowOptions options)
    {
        if (State != LiveFlowState.Active) return;

        SendSet("frame_multiplier", Math.Clamp(options.Multiplier, 2, 6));
        SendSet("flow_multiplier", Math.Clamp(options.Multiplier, 2, 6));
        SendSet("flow_performance", !options.QualityPreset);
        SendSet("low_latency", options.LowLatency);
        SendSet("skip_static", options.KeepStillPixels);
        SendSet("motion_backend", "nvofa");
    }

    public void Stop()
    {
        Process? process = _process;
        _process = null;

        try
        {
            if (_input is not null)
            {
                try
                {
                    _input.WriteLine("{\"action\":\"quit\"}");
                    _input.Flush();
                }
                catch { }

                try { _input.Close(); } catch { }
                _input = null;
            }

            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.WaitForExit(5000);
                }
                catch { }

                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }

                try { process.Dispose(); } catch { }
            }
        }
        catch
        {
            // ignore
        }

        int pid;
        lock (_stateLock) pid = _workerPid;

        if (pid > 0)
        {
            try
            {
                using Process worker = Process.GetProcessById(pid);
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            }
            catch { }
        }

        lock (_stateLock)
        {
            _workerPid = 0;
            _frames = 0;
            _status = string.Empty;
        }

        State = LiveFlowState.Cold;
        GameExecutable = string.Empty;
        GameDirectory = string.Empty;
        LastGame = null;
    }

    private void StartController()
    {
        EnsureRtssProfiles();

        var psi = new ProcessStartInfo(PythonPath)
        {
            WorkingDirectory = DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add(ControllerPath);
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(Path.Combine(DataDirectory, "config.json"));
        psi.Environment["DLSS_MANAGER_SCREEN_DATA"] = DataDirectory;
        psi.Environment["DLSS_MANAGER_EXE"] = Environment.ProcessPath ?? "CHZMFRAMEUNLOCK1.exe";
        psi.Environment["DLSS_MANAGER_PID"] = Environment.ProcessId.ToString();
        psi.Environment["LIVEFLOW_RADEON"] = "0";

        // Чистый генератор кадров: NR-рантайм намеренно НЕ подгружаем,
        // чтобы не жечь видеопамять и не просаживать базовый FPS.
        psi.Environment.Remove("LF_NR_DLL");

        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.StandardInputEncoding = new UTF8Encoding(false);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnError;
        _process.Exited += (_, _) =>
        {
            if (IsRunning)
            {
                lock (_stateLock) _lastError = "LIVE FLOW завершился";
            }
        };

        if (!_process.Start())
            throw new InvalidOperationException("python controller.py не запустился");

        _input = _process.StandardInput;
        _input.AutoFlush = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        lock (_stateLock)
        {
            _workerPid = 0;
            _frames = 0;
            _lastError = string.Empty;
            _status = "starting";
        }
    }

    private bool WaitForWorker(int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (_process is null || HasExited(_process)) return false;
            if (WorkerPid > 0) return true;
            Thread.Sleep(120);
        }

        return WorkerPid > 0;
    }

    private void WaitForFrames(int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (Frames > 0) return;
            if (_process is null || HasExited(_process)) return;
            Thread.Sleep(120);
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; } catch { return true; }
    }

    private bool SendSet(string key, object value)
    {
        StreamWriter? input = _input;
        if (input is null) return false;

        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["set"] = key,
                ["value"] = value
            });

            input.WriteLine(json);
            input.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnOutput(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(e.Data);
            if (!document.RootElement.TryGetProperty("state", out JsonElement state)) return;

            lock (_stateLock)
            {
                if (state.TryGetProperty("worker_pid", out JsonElement worker) &&
                    worker.TryGetInt32(out int pid))
                    _workerPid = pid;

                if (state.TryGetProperty("frames", out JsonElement frames) &&
                    frames.TryGetDouble(out double frameCount))
                    _frames = (int)Math.Max(0, frameCount);

                if (state.TryGetProperty("status", out JsonElement status))
                    _status = status.GetString() ?? string.Empty;

                if (state.TryGetProperty("error", out JsonElement error))
                {
                    string? text = error.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) _lastError = text;
                }
            }
        }
        catch
        {
            // stdout controller.py — только JSON; строки логов идут в stderr
        }
    }

    private void OnError(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        lock (_stateLock)
        {
            if (e.Data.Contains("[engine]", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("Traceback", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
                _status = e.Data;
        }
    }

    private void TrySetWorkerPriority(bool realtime)
    {
        int pid = WorkerPid;
        if (pid <= 0) return;

        try
        {
            using Process worker = Process.GetProcessById(pid);
            worker.PriorityClass = realtime
                ? ProcessPriorityClass.RealTime
                : ProcessPriorityClass.High;
        }
        catch
        {
            // Windows может отказать — это не критично для работы LIVE FLOW
        }
    }

    private static void WriteConfig(MonitorTarget monitor, LiveFlowOptions options)
    {
        var config = new Dictionary<string, object?>
        {
            ["monitor"] = monitor.DeviceName,
            ["width"] = monitor.Width,
            ["height"] = monitor.Height,
            ["fullscreen"] = true,
            ["warmup"] = 4,
            ["work_scale"] = 0.65,
            ["profile"] = "Natural",
            ["intensity"] = 1.0,
            ["local_tone"] = 0.5,
            ["local_structure"] = 1.0,
            ["skin_structure"] = -1.0,
            ["nr_small"] = true,
            ["worker_present"] = true,
            ["motion_on_gpu"] = true,
            ["capture_in_worker"] = true,
            ["open_menu_on_start"] = false,
            ["theme"] = "dark",
            ["lang"] = "en",
            ["halo"] = 0.05,
            ["denoiser_v2"] = true,
            ["menu_scale"] = 1.0,
            ["menu_height"] = null,
            ["split"] = 0.0,
            ["menu_offset"] = new[] { 0, 0 },
            ["style"] = 1,
            ["rec_indicator"] = false,
            ["screenshot_dir"] = "",
            ["spout"] = false,
            ["passes"] = 1,
            ["hdr"] = false,
            ["color_space"] = "auto",
            ["keep_colors"] = 0.0,
            ["low_light"] = 0.0,
            ["motion_backend"] = "nvofa",
            ["motion_backend_gpu_default"] = true,
            ["gpu"] = 0,
            ["gpu_no_nr"] = Array.Empty<int>(),
            ["skip_static"] = options.KeepStillPixels,
            ["sharpness"] = 0.0,
            ["edge_smooth"] = 0.0,
            ["brightness"] = 0.0,
            ["contrast"] = 0.0,
            ["saturation"] = 0.0,
            ["warmth"] = 0.0,
            ["upscaler"] = false,
            ["vrr"] = false,
            ["low_latency"] = options.LowLatency,
            ["flow_performance"] = !options.QualityPreset,
            ["frame_generation"] = true,
            ["frame_multiplier"] = Math.Clamp(options.Multiplier, 2, 6),
            ["frame_generation_height"] = monitor.Height,
            ["frame_generation_queue"] = 1,
            ["flow_mfg"] = false,
            ["flow_multiplier"] = Math.Clamp(options.Multiplier, 2, 6),
            ["frame_limit"] = 0,
            ["presets"] = new Dictionary<string, object>(),
            ["record_audio"] = false,
            ["looks_round"] = 40,
            ["keep_round"] = 41,
            ["nr_model"] = "quality",
            // Движок стартует спящим, но с уже загруженным фреймгеном:
            // включение потом занимает доли секунды.
            ["_leno4ka_standby"] = true
        };

        string path = Path.Combine(DataDirectory, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static GameWindow FindGameWindow(ActiveGameInfo game)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(foreground, out uint foregroundPid);
            string? path = TensorAutoService.TryGetProcessPath((int)foregroundPid);
            if (BelongsToGame(path, game))
            {
                string title = ReadTitle(foreground);
                if (title.Length > 0) return new GameWindow(foreground, title);
            }
        }

        int gamePid = 0;

        try
        {
            foreach (Process process in Process.GetProcessesByName(game.ProcessName))
            {
                try
                {
                    string? path = TensorAutoService.TryGetProcessPath(process.Id);
                    if (BelongsToGame(path, game))
                    {
                        gamePid = process.Id;
                        break;
                    }
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

        if (gamePid > 0)
        {
            IntPtr found = IntPtr.Zero;
            string foundTitle = string.Empty;

            try
            {
                EnumWindows((hwnd, lParam) =>
                {
                    _ = GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != (uint)gamePid || !IsWindowVisible(hwnd)) return true;

                    string title = ReadTitle(hwnd);
                    if (title.Length == 0) return true;

                    found = hwnd;
                    foundTitle = title;
                    return false;
                }, IntPtr.Zero);
            }
            catch
            {
                // ignore
            }

            if (found != IntPtr.Zero) return new GameWindow(found, foundTitle);
        }

        try
        {
            using Process process = Process.GetProcessById(gamePid > 0 ? gamePid : 0);
            if (process.MainWindowHandle != IntPtr.Zero)
                return new GameWindow(process.MainWindowHandle, process.MainWindowTitle);
        }
        catch { }

        return new GameWindow(IntPtr.Zero, string.Empty);
    }

    /// <summary>
    /// Steam-игра часто запускает дочерний .exe из подпапки (ARC Raiders,
    /// The Finals), поэтому сверяем не только точный путь, но и папку игры.
    /// </summary>
    private static bool BelongsToGame(string? path, ActiveGameInfo game)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (string.Equals(path, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(game.Directory).TrimEnd('\\') + "\\";

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadTitle(IntPtr hwnd)
    {
        try
        {
            int length = GetWindowTextLength(hwnd);
            if (length <= 0) return string.Empty;

            var buffer = new StringBuilder(length + 1);
            _ = GetWindowText(hwnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static MonitorTarget GetMonitor(IntPtr hwnd)
    {
        try
        {
            IntPtr monitor = MonitorFromWindow(hwnd, 2);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    int width = Math.Max(1, info.rcMonitor.Right - info.rcMonitor.Left);
                    int height = Math.Max(1, info.rcMonitor.Bottom - info.rcMonitor.Top);
                    string name = string.IsNullOrWhiteSpace(info.szDevice) ? "\\\\.\\DISPLAY1" : info.szDevice;
                    return new MonitorTarget(name, width, height);
                }
            }
        }
        catch
        {
            // ignore
        }

        return new MonitorTarget("\\\\.\\DISPLAY1",
            Math.Max(1, GetSystemMetrics(0)),
            Math.Max(1, GetSystemMetrics(1)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private readonly record struct GameWindow(IntPtr Handle, string Title);
    private readonly record struct MonitorTarget(string DeviceName, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
