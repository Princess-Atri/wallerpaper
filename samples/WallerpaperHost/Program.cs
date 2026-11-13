using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wallerpaper.Diagnostics;

namespace Wallerpaper.Host;

internal enum HostState
{
    Starting,
    Running,
    Paused,
    Degraded,
    ShuttingDown
}

internal static class Program
{
    private static readonly Dictionary<string, ChildSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FullscreenMonitors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> PendingFullscreen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static SchedulerConfig _config = null!;
    private static JobObject? _job;
    private static MonitorManager _monitors = null!;
    private static ForegroundWatcher _foreground = null!;
    private static WinEventWatcher? _winEvents;
    private static WallpaperFallback? _fallback;
    private static HostState _state = HostState.Starting;
    private static volatile bool _shuttingDown;
    private static bool _powerAc = true;
    private static bool _forceBattery;
    private static int _currentFps = 60;
    private static double _lastForegroundPoll;
    private static double _lastMonitorPoll;
    private static double _lastPowerPoll;

    [STAThread]
    private static int Main(string[] args)
    {
        var configPath = "scheduler.json";
        var seconds = 0.0;
        var forceBattery = false;
        var testFallback = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": configPath = args[++i]; break;
                case "--seconds":
                    if (double.TryParse(args[++i], out var s)) seconds = s;
                    break;
                case "--force-battery":
                    forceBattery = true;
                    break;
                case "--test-fallback":
                    testFallback = true;
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("WallerpaperHost --config scheduler.json [--seconds N] [--force-battery] [--test-fallback]");
                    return 0;
            }
        }

        AppLog.Initialize("WallerpaperHost");
        CrashReporter.Install("WallerpaperHost");
        AppLog.Info("WallerpaperHost starting");
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _shuttingDown = true;
        };

        try
        {
            _config = SchedulerConfig.Load(Path.GetFullPath(configPath));
            _forceBattery = forceBattery;
            if (forceBattery)
                _powerAc = false;
            else if (GetSystemPowerStatus(out var power))
                _powerAc = power.ACLineStatus == 1;
            _currentFps = _powerAc ? _config.AcFps : _config.BatteryFps;
            _log($"config: policy={_config.PolicyValue} child={_config.ChildExe} scene={_config.Scene}");
            _log($"power: {(_powerAc ? "AC" : "battery")} fps={_currentFps}");

            _monitors = new MonitorManager(_log);
            _monitors.Refresh();
            _foreground = new ForegroundWatcher(
                _config.ExcludeProcesses, _config.FullscreenScanProcesses,
                _config.ExcludeTitles, _config.ExcludeClasses, _log);
            _winEvents = new WinEventWatcher();
            _winEvents.Start();
            if (!string.IsNullOrWhiteSpace(_config.FallbackImage) && File.Exists(_config.FallbackImage))
                _fallback = new WallpaperFallback(_log);

            try { _job = new JobObject(); }
            catch (Exception ex) { _log($"job object unavailable: {ex.Message}"); }

            foreach (var monitor in _monitors.Monitors)
                AddMonitor(monitor);

            if (testFallback && _fallback is not null)
            {
                foreach (var monitor in _monitors.Monitors)
                    _fallback.Apply(monitor, _config.FallbackImage);
            }

            _state = HostState.Running;
            _log($"host running; monitors={_monitors.Monitors.Count}");
            _ = Task.Run(CommandLoop);

            var sw = Stopwatch.StartNew();
            var last = sw.Elapsed.TotalSeconds;
            while (!_shuttingDown && (seconds <= 0 || sw.Elapsed.TotalSeconds < seconds))
            {
                var now = sw.Elapsed.TotalSeconds;
                Pump(now);
                Thread.Sleep(50);
            }

            _state = HostState.ShuttingDown;
            _log("shutdown requested");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("E_HOST", ex);
            CrashReporter.WriteDump(ex, "host-main");
            Console.Error.WriteLine("E_HOST: " + ex);
            return 1;
        }
        finally
        {
            Cleanup();
        }
    }

    private static void CommandLoop()
    {
        try
        {
            string? line;
            while (!_shuttingDown && (line = Console.ReadLine()) is not null)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('{'))
                {
                    HandlePropertyCommand(trimmed);
                    continue;
                }

                switch (trimmed.ToLowerInvariant())
                {
                    case "pause":
                    case "pause-all":
                        foreach (var session in Sessions.Values)
                            session.Pause();
                        _state = HostState.Paused;
                        _log("command: pause-all");
                        break;
                    case "resume":
                    case "resume-all":
                        foreach (var session in Sessions.Values)
                            session.Resume();
                        _state = HostState.Running;
                        _log("command: resume-all");
                        break;
                    case "status":
                        _log($"status: state={_state}, monitors={Sessions.Count}, " +
                             $"sessions={string.Join(",", Sessions.Select(x => $"{x.Key}:{x.Value.State}"))}");
                        break;
                    case "quit":
                    case "shutdown":
                        _shuttingDown = true;
                        _log("command: quit");
                        break;
                }
            }
        }
        catch
        {
            // stdin is optional; a parent that does not redirect it is fine.
        }
    }

    private static void HandlePropertyCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
            if (cmd == "switch")
            {
                var switchDevice = root.TryGetProperty("device", out var sd) ? sd.GetString() : null;
                var switchPath = root.TryGetProperty("path", out var sp) ? sp.GetString() : null;
                if (string.IsNullOrWhiteSpace(switchPath))
                    return;
                if (!string.IsNullOrWhiteSpace(switchDevice) &&
                    Sessions.TryGetValue(switchDevice, out var switchSession))
                {
                    if (!switchSession.SwitchTo(switchPath))
                        _log($"switch-failed device={switchDevice}");
                }
                else
                {
                    _log($"switch-failed device={switchDevice}");
                }
                return;
            }
            if (cmd == "color")
            {
                double Color(string name) =>
                    root.TryGetProperty(name, out var p) && p.TryGetDouble(out var value) ? value : 0;
                var colorDevice = root.TryGetProperty("device", out var cd) ? cd.GetString() : null;
                var colorSession = colorDevice is not null &&
                                   Sessions.TryGetValue(colorDevice, out var found)
                    ? found
                    : Sessions.Values.FirstOrDefault();
                colorSession?.SetColor(Color("brightness"), Color("contrast"),
                    Color("highlights"), Color("hue"), Color("temperature"),
                    Color("exposure"), Color("sharpness"));
                return;
            }
            if (cmd is not ("volume" or "speed" or "shader"))
                return;
            if (!root.TryGetProperty("value", out var v) || !v.TryGetDouble(out var value))
                return;

            var device = root.TryGetProperty("device", out var d) ? d.GetString() : null;
            if (!string.IsNullOrWhiteSpace(device) &&
                Sessions.TryGetValue(device, out var session))
            {
                session.SetProperty(cmd, value);
                return;
            }

            foreach (var s in Sessions.Values)
                s.SetProperty(cmd, value);
            _log($"property {cmd}={value:0.##} applied to all monitors");
        }
        catch (Exception ex)
        {
            _log($"property command failed: {ex.Message}");
        }
    }

    private static void Pump(double now)
    {
        // WinEvent is only a wake-up hint; polling still applies the 500ms debounce.
        if (_winEvents is not null && _winEvents.Pump())
            _lastForegroundPoll = 0;

        // 1. Foreground fullscreen, 250ms.
        if ((now - _lastForegroundPoll) >= 0.25)
        {
            _lastForegroundPoll = now;
            PollForeground();
            // A child that was still Starting when fullscreen was detected must
            // still be paused/stopped once it becomes ready.
            foreach (var device in FullscreenMonitors)
                ApplyFullscreen(device);
        }

        // 2. Child health / watchdog, 1s.
        foreach (var session in Sessions.Values.ToList())
        {
            if (session.State is ChildState.Faulted or ChildState.Degraded)
                continue;
            session.Tick();
        }

        // 3. Hot-plug, 2s.
        if ((now - _lastMonitorPoll) >= 2.0)
        {
            _lastMonitorPoll = now;
            PollMonitors();
        }

        // 4. Power, 5s.
        if ((now - _lastPowerPoll) >= 5.0)
        {
            _lastPowerPoll = now;
            PollPower();
        }

        if (_config.PolicyValue == PerformancePolicy.Continue && _state != HostState.Running)
        {
            _state = HostState.Running;
            _log("state: Running (policy=continue)");
        }
    }

    private static void PollForeground()
    {
        var fg = _foreground.Poll();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Any visible borderless fullscreen window counts, even when it is not
        // foreground (e.g. a video player behind the terminal).
        foreach (var monitorHandle in _foreground.FindFullscreenWindows())
        {
            var m = _monitors.Monitors.FirstOrDefault(x => x.Handle == monitorHandle);
            if (!string.IsNullOrEmpty(m.Device) && Sessions.ContainsKey(m.Device))
                candidates.Add(m.Device);
        }

        var suppresses = !fg.IsExcluded &&
            (fg.IsFullscreen || fg.IsExclusiveD3D ||
             (_config.TriggerOnMaximized && fg.IsMaximized));
        if (suppresses)
        {
            var monitor = _monitors.Monitors.FirstOrDefault(m => m.Handle == fg.MonitorHandle);
            if (!string.IsNullOrEmpty(monitor.Device) && Sessions.ContainsKey(monitor.Device))
                candidates.Add(monitor.Device);
        }

        // Debounce: fullscreen/restore must be stable for two 250ms polls.
        // This avoids alt-tab/modals/focus flicker causing pause/resume storms.
        foreach (var device in Sessions.Keys.ToList())
        {
            var desired = candidates.Contains(device);
            var current = FullscreenMonitors.Contains(device);
            if (desired == current)
            {
                PendingFullscreen[device] = 0;
                continue;
            }

            PendingFullscreen.TryGetValue(device, out var count);
            count++;
            PendingFullscreen[device] = count;
            if (count < 2)
                continue;

            PendingFullscreen[device] = 0;
            if (desired)
            {
                FullscreenMonitors.Add(device);
                var fgMonitor = _monitors.Monitors.FirstOrDefault(m => m.Handle == fg.MonitorHandle);
                if (string.Equals(fgMonitor.Device, device, StringComparison.OrdinalIgnoreCase) && suppresses)
                {
                    var kind = fg.IsFullscreen || fg.IsExclusiveD3D ? "fullscreen" : "maximized";
                    _log($"[{device}] {kind}: {fg.ProcessName} \"{fg.Title}\" class={fg.ClassName}");
                }
                else
                {
                    _log($"[{device}] fullscreen window (background)");
                }
                ApplyFullscreen(device);
            }
            else
            {
                FullscreenMonitors.Remove(device);
                _log($"[{device}] fullscreen cleared: {fg.ProcessName} \"{fg.Title}\"");
                ReleaseFullscreen(device);
            }
        }

        if (_state != HostState.Starting && _config.PolicyValue != PerformancePolicy.Continue)
        {
            var anyPaused = Sessions.Values.Any(s => s.State == ChildState.Paused);
            _state = anyPaused ? HostState.Paused : HostState.Running;
        }
    }

    private static void ApplyFullscreen(string device)
    {
        if (!Sessions.TryGetValue(device, out var session))
            return;

        switch (_config.PolicyValue)
        {
            case PerformancePolicy.Continue:
                _log($"[{device}] policy=continue; no action");
                break;
            case PerformancePolicy.Pause:
                if (!session.IsRunning || session.State == ChildState.Paused)
                    return;
                session.Pause();
                break;
            case PerformancePolicy.Stop:
                if (!session.IsRunning)
                    return;
                _log($"[{device}] policy=stop-and-release");
                session.Stop(intentional: true);
                break;
        }
    }

    private static void ReleaseFullscreen(string device)
    {
        if (!Sessions.TryGetValue(device, out var session))
            return;

        switch (_config.PolicyValue)
        {
            case PerformancePolicy.Continue:
                break;
            case PerformancePolicy.Pause:
                if (session.State == ChildState.Paused && session.IsRunning)
                    session.Resume();
                else if (!session.IsRunning)
                    StartOrRestart(session, "resume after fullscreen");
                break;
            case PerformancePolicy.Stop:
                StartOrRestart(session, "restart after fullscreen");
                break;
        }
    }

    private static void AddMonitor(MonitorInfo monitor)
    {
        var type = _config.Type;
        var path = _config.WallpaperPath;
        var secondPath = _config.SecondPath;
        var childExe = _config.ChildExe;
        var volume = _config.Volume;
        var speed = _config.Speed;
        var fit = _config.Fit;
        var zoom = _config.Zoom;
        var renderScale = _config.RenderScale;
        var shaderIntensity = _config.ShaderIntensity;
        var brightness = _config.Brightness;
        var contrast = _config.Contrast;
        var highlights = _config.Highlights;
        var hue = _config.Hue;
        var temperature = _config.Temperature;
        var exposure = _config.Exposure;
        var sharpness = _config.Sharpness;
        var fps = _currentFps;
        if (_config.Monitors.TryGetValue(monitor.Device, out var perMonitor))
        {
            if (!perMonitor.Enabled)
            {
                _log($"[{monitor.Device}] disabled by config");
                return;
            }
            if (!string.IsNullOrWhiteSpace(perMonitor.Type)) type = perMonitor.Type;
            if (!string.IsNullOrWhiteSpace(perMonitor.WallpaperPath)) path = perMonitor.WallpaperPath;
            if (!string.IsNullOrWhiteSpace(perMonitor.SecondPath)) secondPath = perMonitor.SecondPath;
            if (!string.IsNullOrWhiteSpace(perMonitor.ChildExe)) childExe = perMonitor.ChildExe;
            volume = perMonitor.Volume;
            speed = perMonitor.Speed;
            fit = perMonitor.Fit;
            zoom = perMonitor.Zoom;
            renderScale = perMonitor.RenderScale;
            brightness = perMonitor.Brightness;
            contrast = perMonitor.Contrast;
            highlights = perMonitor.Highlights;
            hue = perMonitor.Hue;
            temperature = perMonitor.Temperature;
            exposure = perMonitor.Exposure;
            sharpness = perMonitor.Sharpness;
            shaderIntensity = perMonitor.ShaderIntensity;
            if (perMonitor.Fps > 0) fps = perMonitor.Fps;
        }

        if (type is "video" or "sequence" && !string.IsNullOrWhiteSpace(_config.VideoChildExe))
            childExe = _config.VideoChildExe;
        if (type == "web" && !string.IsNullOrWhiteSpace(_config.WebChildExe)) childExe = _config.WebChildExe;
        if (string.IsNullOrWhiteSpace(path))
        {
            _log($"[{monitor.Device}] no wallpaper path configured");
            return;
        }

        _fallback?.Restore(monitor);
        var session = new ChildSession(monitor, type, path, secondPath, childExe, fps,
            fit, zoom, renderScale, brightness, contrast, highlights, hue,
            temperature, exposure, sharpness, _job, _log);
        session.UnexpectedExit += OnUnexpectedExit;
        Sessions[monitor.Device] = session;
        session.Start();
        session.SetProperty("volume", volume);
        session.SetProperty("speed", speed);
        session.SetProperty("shader", shaderIntensity);
    }

    private static void PollMonitors()
    {
        if (!_monitors.Refresh())
            return;

        var current = _monitors.Monitors;
        foreach (var old in Sessions.Keys.ToList())
        {
            if (current.All(m => !string.Equals(m.Device, old, StringComparison.OrdinalIgnoreCase)))
            {
                _log($"[{old}] monitor removed");
                if (Sessions.Remove(old, out var removed))
                {
                    removed.UnexpectedExit -= OnUnexpectedExit;
                    removed.Dispose();
                }
                FullscreenMonitors.Remove(old);
                PendingFullscreen.Remove(old);
            }
        }

        foreach (var monitor in current)
        {
            if (Sessions.TryGetValue(monitor.Device, out var existing))
            {
                // Bounds changes (resolution/position) require a fresh swap chain.
                if (existing.Monitor.X != monitor.X || existing.Monitor.Y != monitor.Y ||
                    existing.Monitor.Width != monitor.Width || existing.Monitor.Height != monitor.Height)
                {
                    _log($"[{monitor.Device}] bounds changed; restarting");
                    existing.Dispose();
                    Sessions.Remove(monitor.Device);
                    AddMonitor(monitor);
                }
            }
            else
            {
                _log($"[{monitor.Device}] monitor added");
                AddMonitor(monitor);
            }
        }
    }

    private static void PollPower()
    {
        if (_forceBattery)
            return;
        if (!GetSystemPowerStatus(out var status))
            return;
        var ac = status.ACLineStatus == 1;
        if (ac == _powerAc)
            return;

        _powerAc = ac;
        _currentFps = ac ? _config.AcFps : _config.BatteryFps;
        _log($"power: {(ac ? "AC" : "battery")} -> fps={_currentFps}");

        foreach (var session in Sessions.Values.ToList())
        {
            if (FullscreenMonitors.Contains(session.Monitor.Device) &&
                _config.PolicyValue is PerformancePolicy.Pause or PerformancePolicy.Stop)
                continue;

            _log($"[{session.Monitor.Device}] applying fps={_currentFps} via restart");
            StopAndStart(session);
        }
    }

    private static void StartOrRestart(ChildSession session, string reason)
    {
        if (_shuttingDown)
            return;
        _log($"[{session.Monitor.Device}] {reason}");
        session.RequestRestart();
        session.Start();
    }

    private static void StopAndStart(ChildSession session)
    {
        session.Stop(intentional: true);
        session.SetFps(_currentFps);
        session.Start();
    }

    private static void OnUnexpectedExit(ChildSession session)
    {
        if (_shuttingDown)
            return;
        var limit = Math.Max(1, _config.Watchdog.MaxRestarts);
        if (session.RecentRestarts >= limit)
        {
            _log($"[{session.Monitor.Device}] watchdog: {limit} restarts in 60s; degraded");
            session.MarkDegraded();
            if (_fallback is not null && !string.IsNullOrWhiteSpace(_config.FallbackImage))
                _fallback.Apply(session.Monitor, _config.FallbackImage);
            return;
        }

        session.RequestRestart();
        var delay = Math.Max(100, _config.Watchdog.RestartDelayMs);
        _log($"[{session.Monitor.Device}] watchdog restart in {delay}ms " +
             $"({session.RecentRestarts}/{limit})");
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            if (_shuttingDown) return;
            if (!Sessions.TryGetValue(session.Monitor.Device, out var current) || !ReferenceEquals(current, session))
                return;
            if (FullscreenMonitors.Contains(session.Monitor.Device) &&
                _config.PolicyValue == PerformancePolicy.Stop)
                return;
            session.Start();
        });
    }

    private static void Cleanup()
    {
        _shuttingDown = true;
        lock (Gate)
        {
            foreach (var session in Sessions.Values.ToList())
            {
                try
                {
                    session.UnexpectedExit -= OnUnexpectedExit;
                    session.Stop(intentional: true);
                }
                catch (Exception ex)
                {
                    _log($"[{session.Monitor.Device}] cleanup failed: {ex}");
                }
            }
            Sessions.Clear();
        }

        _winEvents?.Dispose();
        _winEvents = null;
        _fallback?.RestoreAll();
        _fallback?.Dispose();
        _fallback = null;
        _job?.Dispose();
        _job = null;
        _log("cleanup complete; no child/job object remains");
        Console.Out.Flush();
    }

    private static void _log(string message)
    {
        AppLog.Info(message);
        Console.WriteLine($"[host {DateTime.Now:HH:mm:ss.fff}] {message}");
        Console.Out.Flush();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
