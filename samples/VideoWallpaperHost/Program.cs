using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wallerpaper.Diagnostics;
using Wallerpaper.RenderHost.WorkerW;

namespace VideoWallpaperHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppLog.Initialize("VideoWallpaperHost");
        CrashReporter.Install("VideoWallpaperHost");
        AppLog.Info("VideoWallpaperHost starting");
        Console.OutputEncoding = new UTF8Encoding(false);
        DpiAwareness.EnablePerMonitorV2();

        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        if (!File.Exists(options.VideoPath))
        {
            Console.Error.WriteLine($"video not found: {options.VideoPath}");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(options.SecondPath) && !File.Exists(options.SecondPath))
        {
            Console.Error.WriteLine($"second video not found: {options.SecondPath}");
            return 2;
        }

        if (options.Probe)
        {
            try
            {
                var fps = LibMpv.ProbeSourceFps(options.VideoPath);
                Console.WriteLine($"FPS={fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PROBE_ERROR=" + ex.Message);
                return 5;
            }
        }

        var app = new VideoHostApp(options);
        return app.Run();
    }
}

internal sealed class Options
{
    public string VideoPath { get; private set; } = "";
    public string SecondPath { get; private set; } = "";
    public int Fps { get; private set; } = 60;
    public int Volume { get; private set; }
    public string Fit { get; private set; } = "cover";
    public string Hwdec { get; private set; } = "";
    public double Brightness { get; private set; }
    public double Contrast { get; private set; }
    public double Highlights { get; private set; }
    public double Hue { get; private set; }
    public double Temperature { get; private set; }
    public double Exposure { get; private set; }
    public double Sharpness { get; private set; }
    public bool AllowSoftware { get; private set; }
    public int AutoExitSeconds { get; private set; }
    public (int X, int Y, int Width, int Height)? Bounds { get; private set; }
    public bool Probe { get; private set; }
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--video":
                case "-v":
                    if (i + 1 < args.Length) o.VideoPath = args[++i];
                    break;
                case "--second":
                    if (i + 1 < args.Length) o.SecondPath = args[++i];
                    break;
                case "--probe":
                    o.Probe = true;
                    break;
                case "--fps":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var fps)) o.Fps = fps;
                    break;
                case "--volume":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var vol)) o.Volume = vol;
                    break;
                case "--fit":
                    if (i + 1 < args.Length) o.Fit = args[++i];
                    break;
                case "--hwdec":
                    if (i + 1 < args.Length) o.Hwdec = args[++i];
                    break;
                case "--brightness":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var brightness)) o.Brightness = brightness;
                    break;
                case "--contrast":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var contrast)) o.Contrast = contrast;
                    break;
                case "--highlights":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var highlights)) o.Highlights = highlights;
                    break;
                case "--hue":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var hue)) o.Hue = hue;
                    break;
                case "--temperature":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var temperature)) o.Temperature = temperature;
                    break;
                case "--exposure":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var exposure)) o.Exposure = exposure;
                    break;
                case "--sharpness":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out var sharpness)) o.Sharpness = sharpness;
                    break;
                case "--allow-software":
                    o.AllowSoftware = true;
                    break;
                case "--seconds":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sec)) o.AutoExitSeconds = sec;
                    break;
                case "--bounds":
                    if (i + 4 < args.Length &&
                        int.TryParse(args[++i], out var bx) &&
                        int.TryParse(args[++i], out var by) &&
                        int.TryParse(args[++i], out var bw) &&
                        int.TryParse(args[++i], out var bh))
                        o.Bounds = (bx, by, bw, bh);
                    break;
                case "--help":
                case "-h":
                    o.ShowHelp = true;
                    break;
            }
        }

        if (o.Fps is not (30 or 60)) o.Fps = 60;
        if (o.Fit is not ("cover" or "contain" or "blur")) o.Fit = "cover";
        o.Volume = Math.Clamp(o.Volume, 0, 100);
        o.Brightness = Math.Clamp(o.Brightness, -100, 100);
        o.Contrast = Math.Clamp(o.Contrast, -100, 100);
        o.Highlights = Math.Clamp(o.Highlights, -100, 100);
        o.Hue = Math.Clamp(o.Hue, -100, 100);
        o.Temperature = Math.Clamp(o.Temperature, -100, 100);
        o.Exposure = Math.Clamp(o.Exposure, -100, 100);
        o.Sharpness = Math.Clamp(o.Sharpness, -100, 100);
        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("VideoWallpaperHost (libmpv + WorkerW)");
        Console.WriteLine();
        Console.WriteLine("  VideoWallpaperHost.exe --video <file> [--fps 30|60] [--volume 0-100]");
        Console.WriteLine("      [--fit cover|contain] [--hwdec d3d11va|dxva2] [--allow-software] [--seconds N]");
        Console.WriteLine("      [--bounds x y w h] for per-monitor WallpaperHost");
    }
}

internal static class DpiAwareness
{
    private static readonly IntPtr PerMonitorV2 = new(-4);

    public static void EnablePerMonitorV2()
    {
        try
        {
            if (NativeDpi.SetProcessDpiAwarenessContext(PerMonitorV2))
            {
                NativeDpi.SetThreadDpiAwarenessContext(PerMonitorV2);
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            if (NativeDpi.SetProcessDpiAwareness(2) == 0)
                return;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        NativeDpi.SetProcessDPIAware();
    }

    private static class NativeDpi
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();
    }
}

internal sealed class VideoHostApp
{
    private readonly Options _options;
    private readonly object _stdoutLock = new();
    private WorkerWWindow? _worker;
    private LibMpv? _mpv;
    private Timer? _autoExitTimer;
    private Timer? _fpsTimer;
    private int _exitCode;
    private bool _shuttingDown;

    public VideoHostApp(Options options)
    {
        _options = options;
    }

    public int Run()
    {
        try
        {
            var explorers = Process.GetProcessesByName("explorer");
            Log($"process: pid={Environment.ProcessId}, " +
                $"user={Environment.UserDomainName}\\{Environment.UserName}, " +
                $"privileged={Environment.IsPrivilegedProcess}, " +
                $"session={Process.GetCurrentProcess().SessionId}, explorerProcesses={explorers.Length}");
            if (explorers.Length == 0)
            {
                Log("WARNING: explorer.exe is not visible in this session. WorkerW cannot be attached " +
                    "until the interactive Explorer shell is running.");
            }

            var lowIntegrity = LogProcessContext();
            if (lowIntegrity)
            {
                SendError("E_INTEGRITY",
                    "The child is running at Low integrity (S-1-16-4096), while Explorer is normally " +
                    "Medium. SetParent is blocked by UIPI. Run the parent from a normal Medium-integrity " +
                    "PowerShell launched from the desktop, not from a sandboxed/DSH terminal.");
                _exitCode = 3;
                return _exitCode;
            }

            _worker = new WorkerWWindow(Log);
            if (_options.Bounds is { } bounds)
                _worker.SetTargetRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _worker.WindowLost += () =>
            {
                _exitCode = 3;
                Log("WorkerW window lost; exiting so parent can restart child.");
            };

            if (!_worker.CreateAndAttach())
            {
                SendError("E_WORKERW",
                    "Failed to find/attach WorkerW. Check that explorer.exe is running in this " +
                    "interactive session and that the child is not elevated.");
                _exitCode = 3;
                return _exitCode;
            }

            if (!InitializeMpv())
                return _exitCode;

            // A wallpaper must start playing immediately; the parent may still
            // send pause/play later. Previously the child stayed paused until the
            // parent sent an explicit play, so a "duration" test measured a paused
            // video (time-pos stayed 0.00).
            try
            {
                _mpv!.Play();
                Log("autoplay: video started");
                _fpsTimer = new Timer(_ =>
                {
                    try
                    {
                        SendEvent("fps", new Dictionary<string, object?>
                        {
                            ["source"] = Math.Round(_mpv.SourceFps, 3),
                            ["target"] = _options.Fps,
                            ["hwdec"] = _mpv.HwdecCurrent
                        });
                    }
                    catch { }
                }, null, 1800, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Log($"autoplay failed: {ex.Message}");
                SendError("E_PLAY", ex.Message);
                _exitCode = 5;
                return _exitCode;
            }

            SendEvent("ready", new Dictionary<string, object?>
            {
                ["pid"] = Environment.ProcessId,
                ["hwnd"] = _worker.WindowHandle.ToInt64(),
                ["hwdec"] = _mpv!.HwdecCurrent,
                ["width"] = _mpv.Width,
                ["height"] = _mpv.Height,
                ["duration"] = _mpv.Duration,
                ["fps"] = _options.Fps,
                ["fpsCap"] = _mpv.FpsCapActive
            });

            StartIpcReader();
            SetupAutoExit();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                _worker.RequestClose();
            };

            _worker.RunMessageLoop();
            return _exitCode;
        }
        catch (Exception ex)
        {
            Log($"fatal: {ex}");
            CrashReporter.WriteDump(ex, "video-run");
            SendError("E_INTERNAL", ex.Message);
            _exitCode = 1;
            return _exitCode;
        }
        finally
        {
            Shutdown();
        }
    }

    private bool InitializeMpv(string? fitOverride = null)
    {
        var effectiveFit = fitOverride ?? _options.Fit;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.Hwdec))
            candidates.Add(_options.Hwdec);

        // d3d11va: D3D11VA hardware decode (Windows 8+ preferred).
        // dxva2: DXVA2 fallback (older drivers/Windows).
        candidates.Add("d3d11va");
        candidates.Add("dxva2");
        if (_options.AllowSoftware)
            candidates.Add("auto-safe");

        var unique = new List<string>();
        foreach (var c in candidates)
        {
            if (!unique.Exists(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)))
                unique.Add(c);
        }
        if (string.Equals(effectiveFit, "blur", StringComparison.OrdinalIgnoreCase))
        {
            // The lavfi graph needs system-memory frames; do not try hwdec.
            unique.Clear();
            unique.Add("no");
        }

        MpvException? last = null;
        foreach (var hwdec in unique)
        {
            var requireHardware = !string.Equals(hwdec, "auto-safe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(hwdec, "no", StringComparison.OrdinalIgnoreCase);
            var attempt = new LibMpv(Log, _worker!.PumpMessages);
            try
            {
                attempt.Initialize(
                    _options.VideoPath,
                    _worker.WindowHandle,
                    _options.Fps,
                    _options.Volume,
                    hwdec,
                    effectiveFit,
                    requireHardware,
                    string.IsNullOrWhiteSpace(_options.SecondPath) ? null : _options.SecondPath,
                    _options.Bounds?.Width ?? 0,
                    _options.Bounds?.Height ?? 0,
                    _options.Brightness, _options.Contrast, _options.Highlights,
                    _options.Hue, _options.Temperature, _options.Exposure, _options.Sharpness);

                _mpv = attempt;
                Log($"video initialized with hwdec={hwdec}, actual={_mpv.HwdecCurrent}");
                return true;
            }
            catch (DllNotFoundException)
            {
                Log("libmpv-2.dll not found next to the executable.");
                _exitCode = 4;
                SendError("E_LIBMPV_MISSING", "libmpv-2.dll not found. Put the mpv runtime next to VideoWallpaperHost.exe.");
                attempt.Dispose();
                return false;
            }
            catch (MpvException ex)
            {
                last = ex;
                Log($"hwdec attempt '{hwdec}' failed: {ex.Message}");
                attempt.Dispose();

                // A container/format error will not be fixed by trying another
                // hardware decoder; fail fast instead of initializing mpv twice.
                if (ex.Kind == MpvErrorKind.Load &&
                    (ex.Message.Contains("recognize", StringComparison.OrdinalIgnoreCase) ||
                     ex.Message.Contains("format", StringComparison.OrdinalIgnoreCase)))
                {
                    Log("format/container error: skip remaining hwdec fallbacks.");
                    break;
                }
            }
            catch (Exception ex)
            {
                last = new MpvException(MpvErrorKind.Init, -1, ex.Message);
                Log($"hwdec attempt '{hwdec}' crashed: {ex}");
                attempt.Dispose();
            }
        }

        if (fitOverride is null &&
            string.Equals(effectiveFit, "blur", StringComparison.OrdinalIgnoreCase))
        {
            Log("blur-fill filter unavailable; falling back to contain playback.");
            return InitializeMpv("contain");
        }

        _exitCode = last?.Kind == MpvErrorKind.HardwareDecode ? 4 : 5;
        SendError(last?.Kind == MpvErrorKind.HardwareDecode ? "E_HWDEC" : "E_DECODE",
            last?.Message ?? "No usable libmpv configuration.");
        return false;
    }

    private void StartIpcReader()
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        Log("stdin closed by parent; stopping.");
                        _worker?.RequestClose();
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    HandleCommand(line);
                }
            }
            catch (Exception ex)
            {
                Log($"stdin reader failed: {ex.Message}");
                SendError("E_IPC", ex.Message);
                _worker?.RequestClose();
            }
        });
    }

    private void HandleCommand(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("cmd", out var cmdProp))
            {
                SendError("E_CMD", "missing cmd");
                return;
            }

            var cmd = cmdProp.GetString()?.ToLowerInvariant();
            switch (cmd)
            {
                // The scheduler uses "resume" for all renderer kinds; older
                // VideoWallpaperHost builds only understood "play", which left
                // the decoder released after fullscreen pause -> black desktop.
                case "resume":
                case "play":
                    _mpv?.Play();
                    SendEvent("playing", null);
                    break;

                case "pause":
                    {
                        var release = true;
                        if (doc.RootElement.TryGetProperty("release", out var releaseProp) &&
                            releaseProp.ValueKind is JsonValueKind.False or JsonValueKind.True)
                        {
                            release = releaseProp.GetBoolean();
                        }

                        _mpv?.Pause(release);
                        SendEvent("paused", new Dictionary<string, object?>
                        {
                            ["released"] = release
                        });
                        break;
                    }

                case "stop":
                    _exitCode = 0;
                    _worker?.RequestClose();
                    break;

                case "volume":
                    if (doc.RootElement.TryGetProperty("value", out var volumeProp))
                    {
                        _mpv?.SetVolume(volumeProp.GetInt32());
                        SendEvent("volume", new Dictionary<string, object?> { ["value"] = volumeProp.GetInt32() });
                    }
                    break;

                case "speed":
                    if (doc.RootElement.TryGetProperty("value", out var speedProp))
                    {
                        var speed = speedProp.GetDouble();
                        _mpv?.SetSpeed(speed);
                        SendEvent("speed", new Dictionary<string, object?> { ["value"] = speed });
                    }
                    break;

                case "fps":
                    if (doc.RootElement.TryGetProperty("value", out var fpsProp))
                    {
                        var fps = fpsProp.GetInt32();
                        _mpv?.SetFps(fps);
                        SendEvent("fps", new Dictionary<string, object?> { ["value"] = fps });
                    }
                    break;

                case "color":
                    double Color(string name)
                    {
                        return doc.RootElement.TryGetProperty(name, out var p) &&
                               p.TryGetDouble(out var value) ? value : 0;
                    }
                    _mpv?.SetColor(Color("brightness"), Color("contrast"),
                        Color("highlights"), Color("hue"), Color("temperature"),
                        Color("exposure"), Color("sharpness"));
                    SendEvent("color", new Dictionary<string, object?>
                    {
                        ["brightness"] = Color("brightness"),
                        ["contrast"] = Color("contrast"),
                        ["highlights"] = Color("highlights"),
                        ["hue"] = Color("hue"),
                        ["temperature"] = Color("temperature"),
                        ["exposure"] = Color("exposure"),
                        ["sharpness"] = Color("sharpness")
                    });
                    break;

                case "switch":
                    if (doc.RootElement.TryGetProperty("path", out var switchPath) &&
                        switchPath.GetString() is { } next && _mpv is not null)
                    {
                        try
                        {
                            _mpv.SwitchTo(next);
                            SendEvent("switched", new Dictionary<string, object?>
                            {
                                ["path"] = next
                            });
                        }
                        catch (Exception ex)
                        {
                            SendError("E_SWITCH", ex.Message);
                        }
                    }
                    break;

                case "status":
                    SendEvent("stats", _mpv?.GetStats());
                    break;

                default:
                    SendError("E_CMD", $"unknown cmd '{cmd}'");
                    break;
            }
        }
        catch (JsonException ex)
        {
            SendError("E_CMD", $"invalid json: {ex.Message}");
        }
        catch (Exception ex)
        {
            SendError("E_CMD", ex.Message);
        }
    }

    private void SetupAutoExit()
    {
        if (_options.AutoExitSeconds <= 0)
            return;

        _autoExitTimer = new Timer(_ =>
        {
            _exitCode = 0;
            _worker?.RequestClose();
        }, null, _options.AutoExitSeconds * 1000, Timeout.Infinite);
    }

    private void Shutdown()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;

        _autoExitTimer?.Dispose();
        _autoExitTimer = null;
        _fpsTimer?.Dispose();
        _fpsTimer = null;

        try
        {
            _mpv?.Dispose();
            _mpv = null;
        }
        catch (Exception ex)
        {
            Log($"mpv shutdown: {ex.Message}");
        }

        try
        {
            _worker?.Dispose();
            _worker = null;
        }
        catch (Exception ex)
        {
            Log($"window shutdown: {ex.Message}");
        }

        SendEvent("stopped", new Dictionary<string, object?>
        {
            ["exitCode"] = _exitCode
        });
    }

    private void SendError(string code, string message)
    {
        Log($"ERROR {code}: {message}");
        SendEvent("error", new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message
        });
    }

    private void SendEvent(string ev, Dictionary<string, object?>? data)
    {
        var payload = new Dictionary<string, object?>(data ?? new Dictionary<string, object?>())
        {
            ["ev"] = ev,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        try
        {
            lock (_stdoutLock)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload));
                Console.Out.Flush();
            }
        }
        catch (IOException)
        {
            // Parent pipe closed; nothing useful to do.
        }
    }

    private static bool LogProcessContext()
    {
        var lowIntegrity = false;

        try
        {
            Log($"visibleProcesses={Process.GetProcesses().Length}");
        }
        catch (Exception ex)
        {
            Log($"visibleProcesses failed: {ex.Message}");
        }

        try
        {
            var winsta = DesktopInfo.GetWindowStationName();
            var desktop = DesktopInfo.GetThreadDesktopName();
            Log($"winsta={winsta}, desktop={desktop}");
        }
        catch (Exception ex)
        {
            Log($"desktop query failed: {ex.Message}");
        }

        try
        {
            var whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
            var psi = new ProcessStartInfo(whoami)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/groups");

            using var process = Process.Start(psi);
            if (process is null)
                return false;

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Contains("S-1-16", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("AppContainer", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Mandatory", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Integrity", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"whoami: {line}");
                }

                if (line.Contains("S-1-16-4096", StringComparison.OrdinalIgnoreCase))
                    lowIntegrity = true;
            }
        }
        catch (Exception ex)
        {
            Log($"whoami failed: {ex.Message}");
        }

        return lowIntegrity;
    }

    private static void Log(string message)
    {
        AppLog.Info(message);
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private static class DesktopInfo
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetUserObjectInformation(
            IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

        public static string GetWindowStationName() => GetName(GetProcessWindowStation());

        public static string GetThreadDesktopName() => GetName(GetThreadDesktop(GetCurrentThreadId()));

        private static string GetName(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return "<null>";

            var sb = new StringBuilder(256);
            return GetUserObjectInformation(handle, 2, sb, sb.Capacity * 2, out _)
                ? sb.ToString()
                : $"<error {Marshal.GetLastWin32Error()}>";
        }
    }
}

