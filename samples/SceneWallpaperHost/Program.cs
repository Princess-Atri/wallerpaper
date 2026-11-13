using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wallerpaper.Diagnostics;
using Wallerpaper.RenderHost.WorkerW;

namespace SceneWallpaperHost;

internal static class Program
{
    private static volatile bool _running = true;
    private static volatile bool _paused;
    private static readonly AudioFrame Audio = new();
    private static readonly object AudioLock = new();
    private static SceneRenderer? _renderer;
    private static SceneScript? _script;
    private static StatsPanel? _statsPanel;
    private static volatile bool _statsToggleRequested;
    private static double _panelFrames;
    private static double _panelLastAt;
    private static double _scriptTime;
    private static double _speed = 1.0;
    private static double _shaderIntensity = 1.0;
    private static bool _stdinClosed;
    private static bool _captured;
    private static int _lastOps;
    private static long _frames;
    private static double _lastStatsAt;
    private static double _lastCpuSeconds;
    private static double _lastScriptMs;
    private static double _lastSizeCheck;

    [STAThread]
    private static int Main(string[] args)
    {
        AppLog.Initialize("SceneWallpaperHost");
        CrashReporter.Install("SceneWallpaperHost");
        AppLog.Info("SceneWallpaperHost starting");
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintHelp();
            return 0;
        }

        try
        {
            // WorkerW and the scene swap chain must use physical pixels; without
            // this the client rect is virtualized (e.g. 1228x768 for a 1536x960
            // window at 125%).
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2

            var config = SceneConfig.Load(options.ScenePath);
            if (options.Fps is 30 or 60)
                config.Fps = options.Fps;

            using var worker = new WorkerWWindow(Log);
            if (options.Bounds is { } bounds)
                worker.SetTargetRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            worker.WindowLost += () =>
            {
                Log("WorkerW lost");
                _running = false;
            };

            if (!worker.CreateAndAttach())
            {
                Console.Error.WriteLine("E_WORKERW: CreateAndAttach failed");
                return 3;
            }

            Log($"WorkerW attached: hwnd=0x{worker.WindowHandle.ToInt64():X}");

            _renderer = new SceneRenderer(Log)
        {
            ShaderIntensity = (float)_shaderIntensity
        };
            try
            {
                _renderer.Initialize(worker.WindowHandle, config, options.RenderScale);
            }
            catch (Exception ex)
            {
                Log($"E_D3D11: {ex}");
                return 4;
            }

            _script = new SceneScript(
                string.IsNullOrWhiteSpace(config.Script) ? null : config.ResolvePath(config.Script),
                Log);

            if (options.ShowStats)
                _statsPanel = new StatsPanel();

            StartIpcReader();
            SendEvent(new { type = "ready", renderer = "d3d11", layers = config.Layers.Count, fps = config.Fps });

            timeBeginPeriod(1);
            var sw = Stopwatch.StartNew();
            var last = sw.Elapsed.TotalSeconds;
            var lastStats = last;
            _lastStatsAt = last;
            _panelLastAt = last;
            _lastCpuSeconds = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
            var nextFrame = last;
            var autoStop = options.AutoStopSeconds > 0 ? options.AutoStopSeconds : double.MaxValue;

            while (_running && sw.Elapsed.TotalSeconds < autoStop)
            {
                worker.PumpMessages();
                var sizeCheckTime = sw.Elapsed.TotalSeconds;
                if (sizeCheckTime - _lastSizeCheck >= 1.0)
                {
                    _lastSizeCheck = sizeCheckTime;
                    _renderer?.ResizeToClient(worker.WindowHandle);
                }
                if (worker.IsWindowLost)
                {
                    _running = false;
                    break;
                }

                if (_stdinClosed && !_paused)
                {
                    // No parent owner: do not leave an orphan wallpaper process.
                    Log("stdin closed; stopping");
                    break;
                }

                var now = sw.Elapsed.TotalSeconds;
                if (now < nextFrame)
                {
                    var waitMs = (int)((nextFrame - now) * 1000.0);
                    if (waitMs > 1)
                        Thread.Sleep(waitMs - 1);
                    else
                        Thread.Sleep(0);
                    continue;
                }

                var dt = Math.Clamp(now - last, 0.0001, 0.1);
                last = now;
                if (!_paused)
                {
                    var mouse = GetMouse(worker.WindowHandle);
                    var scaledDt = dt * _speed;
                    _scriptTime += scaledDt;

                    IReadOnlyList<ScriptOp> ops = Array.Empty<ScriptOp>();
                    AudioFrame audioSnapshot;
                    lock (AudioLock)
                    {
                        audioSnapshot = new AudioFrame
                        {
                            Level = Audio.Level,
                            Bass = Audio.Bass,
                            Mid = Audio.Mid,
                            Treble = Audio.Treble,
                            SentAtUnixMs = Audio.SentAtUnixMs,
                            Bins = (double[])Audio.Bins.Clone()
                        };
                    }

                    var scriptTimer = Stopwatch.StartNew();
                    ops = _script?.Update(_scriptTime, scaledDt, mouse.X, mouse.Y, audioSnapshot) ?? ops;
                    _lastScriptMs = scriptTimer.Elapsed.TotalMilliseconds;
                    _lastOps = ops.Count;
                    _renderer!.ApplyScriptOps(ops);
                    string? capturePath = null;
                    if (!_captured && _scriptTime >= 2.0)
                    {
                        capturePath = Environment.GetEnvironmentVariable("WALLERPAPER_CAPTURE");
                        if (!string.IsNullOrWhiteSpace(capturePath))
                        {
                            _captured = true;
                            try
                            {
                                _renderer.Render(_scriptTime, scaledDt, mouse.X, mouse.Y, audioSnapshot, config.Fps, capturePath, options.Vsync);
                                Log("captured frame: " + capturePath);
                            }
                            catch (Exception ex)
                            {
                                Log("capture failed: " + ex.Message);
                                _renderer.Render(_scriptTime, scaledDt, mouse.X, mouse.Y, audioSnapshot, config.Fps, null, options.Vsync);
                            }
                        }
                    }
                    else
                    {
                        _renderer.Render(_scriptTime, scaledDt, mouse.X, mouse.Y, audioSnapshot, config.Fps, null, options.Vsync);
                    }
                    _frames++;
                    _panelFrames++;
                    if (_statsToggleRequested)
                    {
                        _statsToggleRequested = false;
                        _statsPanel?.Toggle();
                    }
                    if (_statsPanel is not null && now - _panelLastAt >= 0.5)
                    {
                        var panelSpan = Math.Max(0.001, now - _panelLastAt);
                        var panelFps = _panelFrames / panelSpan;
                        _renderer?.UpdateVramInfo();
                        var proc = Process.GetCurrentProcess();
                        _statsPanel.Update(
                            $"FPS {panelFps,5:0.0}   frame {1000.0 / Math.Max(1, panelFps),5:0.0} ms\n" +
                            $"draw {_renderer?.LastDrawMs ?? 0,5:0.00} ms   present {_renderer?.LastPresentMs ?? 0,5:0.00} ms\n" +
                            $"gpu  {_renderer?.LastGpuMs ?? 0,5:0.00} ms\n" +
                            $"layers {config.Layers.Count}   particles {_renderer?.ActiveParticles ?? 0}\n" +
                            $"render {_renderer?.RenderWidth ?? 0}x{_renderer?.RenderHeight ?? 0}  scale {_renderer?.RenderScale ?? 1:0.##}x\n" +
                            $"VRAM {(_renderer?.VramUsedBytes ?? 0) / 1048576.0,6:0.0}/{(_renderer?.VramBudgetBytes ?? 0) / 1048576.0:0.0} MB\n" +
                            $"WS {proc.WorkingSet64 / 1048576.0,6:0.0} MB   {config.Fps} fps cap");
                        _panelFrames = 0;
                        _panelLastAt = now;
                    }

                    if (now - lastStats >= 5)
                    {
                        var span = Math.Max(0.001, now - _lastStatsAt);
                        var cpuNow = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
                        var cpuPercent = (cpuNow - _lastCpuSeconds) / span / Math.Max(1, Environment.ProcessorCount) * 100.0;
                        var actualFps = _frames / span;
                        _renderer?.UpdateVramInfo();
                        if (_renderer is not null && _renderer.RenderScale > 0.5f)
                        {
                            var budget = Math.Min(
                                _renderer.VramBudgetBytes,
                                (ulong)config.MaxTextureMemoryMb * 1024UL * 1024UL);
                            if (budget > 0 && _renderer.VramUsedBytes > budget * 0.9)
                            {
                                Log($"VRAM threshold {_renderer.VramUsedBytes / 1048576.0:0}MB > 90% of {budget / 1048576.0:0}MB; degrade to half-res");
                                _renderer.SetRenderScale(0.5f);
                            }
                        }
                        _lastStatsAt = now;
                        _lastCpuSeconds = cpuNow;
                        _frames = 0;
                        lastStats = now;
                        var proc = Process.GetCurrentProcess();
                        SendEvent(new
                        {
                            type = "stats",
                            time = Math.Round(_scriptTime, 2),
                            fpsCap = config.Fps,
                            fps = Math.Round(actualFps, 1),
                            renderScale = _renderer?.RenderScale ?? 1,
                            renderWidth = _renderer?.RenderWidth ?? 0,
                            renderHeight = _renderer?.RenderHeight ?? 0,
                            cpuPercent = Math.Round(cpuPercent, 1),
                            scriptMs = Math.Round(_lastScriptMs, 2),
                            drawMs = Math.Round(_renderer?.LastDrawMs ?? 0, 2),
                            presentMs = Math.Round(_renderer?.LastPresentMs ?? 0, 2),
                            gpuMs = Math.Round(_renderer?.LastGpuMs ?? 0, 2),
                            layers = config.Layers.Count,
                            culledLayers = _renderer?.CulledLayers ?? 0,
                            particles = _renderer?.ActiveParticles ?? 0,
                            particleSpeed = Math.Round(_renderer?.ActiveParticleSpeed ?? 0, 2),
                            scriptOps = _lastOps,
                            audioLevel = Math.Round(audioSnapshot.Level, 3),
                            audioBass = Math.Round(audioSnapshot.Bass, 3),
                            audioAgeMs = audioSnapshot.SentAtUnixMs > 0
                                ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - audioSnapshot.SentAtUnixMs)
                                : 0,
                            workingSetMb = Math.Round(proc.WorkingSet64 / 1048576.0, 1),
                            privateMb = Math.Round(proc.PrivateMemorySize64 / 1048576.0, 1),
                            vramUsedMb = Math.Round((_renderer?.VramUsedBytes ?? 0) / 1048576.0, 1),
                            vramBudgetMb = Math.Round((_renderer?.VramBudgetBytes ?? 0) / 1048576.0, 1),
                            handles = proc.HandleCount,
                            threads = proc.Threads.Count
                        });
                    }
                }

                nextFrame = now + 1.0 / Math.Max(1, config.Fps);
            }

            SendEvent(new { type = "stopped" });
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("E_FATAL", ex);
            CrashReporter.WriteDump(ex, "scene-main");
            Console.Error.WriteLine($"E_FATAL: {ex}");
            return 1;
        }
        finally
        {
            timeEndPeriod(1);
            _statsPanel?.Dispose();
            _statsPanel = null;
            _script?.Dispose();
            _renderer?.Dispose();
        }
    }

    private static (double X, double Y) GetMouse(IntPtr host)
    {
        if (!GetCursorPos(out var p))
            return (0, 0);
        if (!ScreenToClient(host, ref p))
            return (p.X, p.Y);
        return (p.X, p.Y);
    }

    private static void StartIpcReader()
    {
        var thread = new Thread(() =>
        {
            try
            {
                string? line;
                while (_running && (line = Console.In.ReadLine()) is not null)
                {
                    if (line.Length > 1_000_000)
                        continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("cmd", out var cmdProp))
                            continue;

                        switch (cmdProp.GetString())
                        {
                            case "audio":
                                if (root.TryGetProperty("bins", out var binsProp))
                                {
                                    var bins = new double[32];
                                    var i = 0;
                                    foreach (var b in binsProp.EnumerateArray())
                                    {
                                        if (i >= 32) break;
                                        bins[i++] = Math.Clamp(b.GetDouble(), 0, 1);
                                    }
                                    var level = root.TryGetProperty("level", out var lp) ? lp.GetDouble() : bins.Max();
                                    var sentAt = root.TryGetProperty("sentAt", out var sp) ? sp.GetInt64() : 0;
                                    lock (AudioLock)
                                    {
                                        Audio.Bins = bins;
                                        Audio.Level = level;
                                        Audio.Bass = Average(bins, 0, 8);
                                        Audio.Mid = Average(bins, 8, 12);
                                        Audio.Treble = Average(bins, 20, 12);
                                        Audio.SentAtUnixMs = sentAt;
                                    }
                                }
                                break;
                            case "pause":
                                _paused = true;
                                SendEvent(new { type = "paused" });
                                break;
                            case "resume":
                                _paused = false;
                                SendEvent(new { type = "resumed" });
                                break;
                            case "speed":
                                if (root.TryGetProperty("value", out var speedProp))
                                {
                                    _speed = Math.Clamp(speedProp.GetDouble(), 0.1, 4.0);
                                    SendEvent(new { type = "speed", value = _speed });
                                }
                                break;
                            case "shader":
                                if (root.TryGetProperty("value", out var shaderProp))
                                {
                                    _shaderIntensity = Math.Clamp(shaderProp.GetDouble(), 0, 2);
                                    if (_renderer is not null)
                                        _renderer.ShaderIntensity = (float)_shaderIntensity;
                                    SendEvent(new { type = "shader", value = _shaderIntensity });
                                }
                                break;
                            case "status":
                                SendEvent(new
                                {
                                    type = "status",
                                    time = Math.Round(_scriptTime, 2),
                                    paused = _paused,
                                    layers = _renderer?.Width > 0
                                });
                                break;
                            case "stats":
                                _statsToggleRequested = true;
                                break;
                            case "stop":
                                _running = false;
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"IPC parse error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"IPC reader ended: {ex.Message}");
            }
            finally
            {
                _stdinClosed = true;
            }
        })
        {
            IsBackground = true,
            Name = "scene-ipc"
        };
        thread.Start();
    }

    private static double Average(double[] bins, int start, int count)
    {
        var sum = 0.0;
        var n = 0;
        for (var i = start; i < start + count && i < bins.Length; i++)
        {
            sum += bins[i];
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private static void SendEvent(object obj)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(obj));
        Console.Out.Flush();
    }

    private static void Log(string message)
    {
        AppLog.Info(message);
        Console.Error.WriteLine($"[scene] [{DateTime.Now:HH:mm:ss.fff}] {message}");
        Console.Error.Flush();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
}

internal sealed class Options
{
    public string ScenePath { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "scenes", "audio_gradient", "scene.json");
    public int Fps { get; private set; } = 60;
    public double AutoStopSeconds { get; private set; }
    public bool Vsync { get; private set; }
    public bool ShowStats { get; private set; }
    public (int X, int Y, int Width, int Height)? Bounds { get; private set; }
    public float RenderScale { get; private set; } = 1.0f;
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scene":
                    o.ScenePath = args[++i];
                    break;
                case "--fps":
                    o.Fps = int.TryParse(args[++i], CultureInfo.InvariantCulture, out var fps) ? fps : 60;
                    break;
                case "--seconds":
                    o.AutoStopSeconds = double.TryParse(args[++i], CultureInfo.InvariantCulture, out var sec) ? sec : 0;
                    break;
                case "--vsync":
                    o.Vsync = true;
                    break;
                case "--half-res":
                    o.RenderScale = 0.5f;
                    break;
                case "--supersample":
                    if (int.TryParse(args[++i], CultureInfo.InvariantCulture, out var ss) && ss is >= 1 and <= 4)
                        o.RenderScale = ss;
                    break;
                case "--render-scale":
                    if (float.TryParse(args[++i], CultureInfo.InvariantCulture, out var rs))
                        o.RenderScale = Math.Clamp(rs, 0.25f, 4.0f);
                    break;
                case "--stats":
                    o.ShowStats = true;
                    break;
                case "--bounds":
                    if (i + 4 < args.Length &&
                        int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bx) &&
                        int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var by) &&
                        int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bw) &&
                        int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bh))
                    {
                        o.Bounds = (bx, by, bw, bh);
                    }
                    break;
                case "--help":
                case "-h":
                    o.ShowHelp = true;
                    break;
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("SceneWallpaperHost --scene <scene.json> [--fps 30|60] [--seconds N] [--vsync]");
        Console.WriteLine("  [--supersample 1|2|4] [--half-res] [--stats] [--bounds x y w h]");
        Console.WriteLine("Reads commands from stdin: audio / pause / resume / status / stop");
    }
}
