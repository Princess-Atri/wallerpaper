using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Wallerpaper.Host;

internal enum ChildState
{
    Starting,
    Running,
    Paused,
    Stopped,
    Restarting,
    Degraded,
    Faulted
}

/// <summary>
/// One renderer process for one monitor. The host owns the lifecycle; the child
/// owns its WorkerW HWND, swap chain and wallpaper renderer.
/// </summary>
internal sealed class ChildSession : IDisposable
{
    private readonly Action<string> _log;
    private readonly JobObject? _job;
    private readonly object _gate = new();
    private readonly Queue<DateTime> _restarts = new();
    private Process? _process;
    private bool _intentionalStop;
    private bool _pauseRequested;
    private bool _ready;
    private DateTime _startedAt;
    private DateTime _lastExitAt;
    private int _restartCount;

    public ChildSession(MonitorInfo monitor, string wallpaperType, string wallpaperPath,
        string secondPath, string childExe, int fps, string fit, double zoom,
        double renderScale, double brightness, double contrast, double highlights,
        double hue, double temperature, double exposure, double sharpness,
        JobObject? job, Action<string> log)
    {
        Monitor = monitor;
        WallpaperType = wallpaperType;
        WallpaperPath = wallpaperPath;
        SecondPath = secondPath;
        ChildExe = childExe;
        Fps = fps;
        Fit = fit;
        Zoom = zoom;
        RenderScale = renderScale;
        Brightness = brightness;
        Contrast = contrast;
        Highlights = highlights;
        Hue = hue;
        Temperature = temperature;
        Exposure = exposure;
        Sharpness = sharpness;
        _job = job;
        _log = log;
    }

    public MonitorInfo Monitor { get; }
    public string WallpaperType { get; }
    public string WallpaperPath { get; }
    public string SecondPath { get; }
    public string ChildExe { get; }
    public string Fit { get; }
    public double Zoom { get; }
    public double RenderScale { get; }
    public double Brightness { get; }
    public double Contrast { get; }
    public double Highlights { get; }
    public double Hue { get; }
    public double Temperature { get; }
    public double Exposure { get; }
    public double Sharpness { get; }
    public int Fps { get; private set; }
    public ChildState State { get; private set; } = ChildState.Stopped;
    public int Pid { get; private set; }
    public string LastError { get; private set; } = "";
    public bool IsRunning => _process is { HasExited: false };

    private static void AddColorArgument(System.Collections.Generic.ICollection<string> args,
        string name, double value)
    {
        if (Math.Abs(value) < 0.01) return;
        args.Add(name);
        args.Add(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public event Action<ChildSession>? UnexpectedExit;

    public void Start()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
                return;

            if (!File.Exists(ChildExe))
            {
                State = ChildState.Faulted;
                LastError = "child exe not found";
                _log($"[{Monitor.Device}] child not found: {ChildExe}");
                return;
            }

            _intentionalStop = false;
            _pauseRequested = false;
            _ready = false;
            var psi = new ProcessStartInfo
            {
                FileName = ChildExe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ChildExe)!
            };
            switch (WallpaperType.ToLowerInvariant())
            {
                case "video":
                case "sequence":
                    psi.ArgumentList.Add("--video");
                    psi.ArgumentList.Add(WallpaperPath);
                    if (WallpaperType.Equals("sequence", StringComparison.OrdinalIgnoreCase))
                    {
                        psi.ArgumentList.Add("--second");
                        psi.ArgumentList.Add(SecondPath);
                    }
                    psi.ArgumentList.Add("--fps");
                    psi.ArgumentList.Add(Fps.ToString(CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("--fit");
                    psi.ArgumentList.Add(
                        string.Equals(Fit, "contain", StringComparison.OrdinalIgnoreCase) ? "contain" :
                        string.Equals(Fit, "blur", StringComparison.OrdinalIgnoreCase) ? "blur" : "cover");
                    AddColorArgument(psi.ArgumentList, "--brightness", Brightness);
                    AddColorArgument(psi.ArgumentList, "--contrast", Contrast);
                    AddColorArgument(psi.ArgumentList, "--highlights", Highlights);
                    AddColorArgument(psi.ArgumentList, "--hue", Hue);
                    AddColorArgument(psi.ArgumentList, "--temperature", Temperature);
                    AddColorArgument(psi.ArgumentList, "--exposure", Exposure);
                    AddColorArgument(psi.ArgumentList, "--sharpness", Sharpness);
                    // 5K super-resolution outputs can fail D3D11VA init on
                    // some drivers; fall back to software instead of exiting
                    // and exposing the desktop.
                    psi.ArgumentList.Add("--allow-software");
                    break;
                case "web":
                    if (WallpaperPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        psi.ArgumentList.Add("--url");
                    else
                        psi.ArgumentList.Add("--html");
                    psi.ArgumentList.Add(WallpaperPath);
                    psi.ArgumentList.Add("--zoom");
                    psi.ArgumentList.Add(Zoom.ToString("0.##", CultureInfo.InvariantCulture));
                    break;
                default:
                    psi.ArgumentList.Add("--scene");
                    psi.ArgumentList.Add(WallpaperPath);
                    psi.ArgumentList.Add("--fps");
                    psi.ArgumentList.Add(Fps.ToString(CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("--render-scale");
                    psi.ArgumentList.Add(RenderScale.ToString("0.##", CultureInfo.InvariantCulture));
                    break;
            }
            psi.ArgumentList.Add("--bounds");
            psi.ArgumentList.Add(Monitor.X.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(Monitor.Y.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(Monitor.Width.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(Monitor.Height.ToString(CultureInfo.InvariantCulture));
            // Do not let a self-contained parent's app-local DOTNET_ROOT
            // override the machine-wide .NET installation for a
            // framework-dependent renderer.
            psi.Environment.Remove("DOTNET_ROOT");
            psi.Environment.Remove("DOTNET_ROOT_X64");
            psi.Environment.Remove("DOTNET_ROOT_X86");
            psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "1";

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnStdout(e.Data);
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log($"[{Monitor.Device}] {e.Data}");
            };
            p.Exited += (_, _) => OnExited(p);

            if (!p.Start())
            {
                State = ChildState.Faulted;
                LastError = "Start failed";
                _log($"[{Monitor.Device}] child Start failed");
                return;
            }

            _process = p;
            _startedAt = DateTime.UtcNow;
            Pid = p.Id;
            State = ChildState.Starting;
            try { _job?.Assign(p); } catch (Exception ex) { LastError = ex.Message; }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _log($"[{Monitor.Device}] child started pid={p.Id} bounds={Monitor.X},{Monitor.Y} {Monitor.Width}x{Monitor.Height} fps={Fps}");
        }
    }

    public void SetFps(int fps)
    {
        Fps = Math.Clamp(fps, 24, 120);
    }

    public bool SwitchTo(string path)
    {
        if (WallpaperType is not ("video" or "sequence") || !File.Exists(path) || !IsRunning)
            return false;
        Send(new { cmd = "switch", path });
        _log($"[{Monitor.Device}] live switch requested: {path}");
        return true;
    }

    /// <summary>
    /// Live property change forwarded to the renderer over its stdin IPC.
    /// Supported names: volume, speed, shader.
    /// </summary>
    public void SetProperty(string name, double value)
    {
        var normalized = name switch
        {
            "shader" or "shaderIntensity" => "shader",
            "volume" => "volume",
            "speed" => "speed",
            _ => null
        };
        if (normalized is null)
            return;

        var type = WallpaperType.ToLowerInvariant();
        if (normalized == "shader" && type != "scene")
            return;
        if (normalized == "volume" && type == "scene")
            return;

        Send(new { cmd = normalized, value });
        _log($"[{Monitor.Device}] property {normalized}={value:0.##} sent");
    }

    public void SetColor(double brightness, double contrast, double highlights,
        double hue, double temperature, double exposure, double sharpness)
    {
        if (WallpaperType is not ("video" or "sequence") || !IsRunning)
            return;
        Send(new
        {
            cmd = "color",
            brightness,
            contrast,
            highlights,
            hue,
            temperature,
            exposure,
            sharpness
        });
        _log($"[{Monitor.Device}] color live update");
    }

    public void Pause()
    {
        _pauseRequested = true;
        if (!IsRunning) return;
        Send(new { cmd = "pause" });
        State = ChildState.Paused;
        _log($"[{Monitor.Device}] pause requested");
    }

    public void Resume()
    {
        _pauseRequested = false;
        if (!IsRunning) return;
        Send(new { cmd = "resume" });
        State = ChildState.Running;
        _log($"[{Monitor.Device}] resume requested");
    }

    public void Stop(bool intentional = true)
    {
        Process? p;
        lock (_gate)
        {
            _intentionalStop = intentional;
            p = _process;
            if (p is null)
            {
                State = ChildState.Stopped;
                return;
            }
        }

        try
        {
            if (!p.HasExited)
                SendTo(p, new { cmd = "stop" });
            if (!p.WaitForExit(3000))
            {
                _log($"[{Monitor.Device}] child did not stop in 3s, killing tree");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log($"[{Monitor.Device}] stop failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, p))
                    _process = null;
                Pid = 0;
                State = ChildState.Stopped;
            }
            try { p.Dispose(); } catch { }
            _log($"[{Monitor.Device}] stopped");
        }
    }

    public void RequestRestart()
    {
        lock (_gate)
        {
            _restarts.Enqueue(DateTime.UtcNow);
            while (_restarts.Count > 0 && (DateTime.UtcNow - _restarts.Peek()).TotalSeconds > 60)
                _restarts.Dequeue();
            _restartCount++;
            State = ChildState.Restarting;
        }
    }

    public int RestartCount => _restartCount;
    public int RecentRestarts
    {
        get
        {
            lock (_gate)
            {
                while (_restarts.Count > 0 && (DateTime.UtcNow - _restarts.Peek()).TotalSeconds > 60)
                    _restarts.Dequeue();
                return _restarts.Count;
            }
        }
    }

    public void MarkDegraded()
    {
        lock (_gate)
        {
            State = ChildState.Degraded;
            _pauseRequested = false;
        }
    }

    public bool IsReady => _ready;
    public TimeSpan Uptime => _process is { HasExited: false } ? DateTime.UtcNow - _startedAt : TimeSpan.Zero;

    public void Tick()
    {
        Process? p;
        lock (_gate) p = _process;
        if (p is null || p.HasExited) return;
        if (!_ready && DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(20))
        {
            LastError = "ready timeout";
            _log($"[{Monitor.Device}] ready timeout; killing");
            try { p.Kill(entireProcessTree: true); } catch { }
        }
    }

    private void Send(object obj)
    {
        var p = _process;
        if (p is not null)
            SendTo(p, obj);
    }

    private void SendTo(Process p, object obj)
    {
        try
        {
            if (p.HasExited) return;
            p.StandardInput.WriteLine(JsonSerializer.Serialize(obj));
            p.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log($"[{Monitor.Device}] send failed: {ex.Message}");
        }
    }

    private void OnStdout(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            // Scene/Web use "type"; VideoWallpaperHost uses "ev".
            var type = doc.RootElement.TryGetProperty("type", out var t)
                ? t.GetString()
                : doc.RootElement.TryGetProperty("ev", out var ev) ? ev.GetString() : null;
            switch (type)
            {
                case "ready":
                    _ready = true;
                    if (_pauseRequested)
                    {
                        Send(new { cmd = "pause" });
                        State = ChildState.Paused;
                        _log($"[{Monitor.Device}] ready pid={Pid}; pause re-applied");
                    }
                    else
                    {
                        State = ChildState.Running;
                        _log($"[{Monitor.Device}] ready pid={Pid}");
                    }
                    break;
                case "paused":
                    State = ChildState.Paused;
                    _log($"[{Monitor.Device}] paused");
                    break;
                case "resumed":
                    State = ChildState.Running;
                    _log($"[{Monitor.Device}] resumed");
                    break;
                case "volume":
                case "speed":
                case "shader":
                    _log($"[{Monitor.Device}] {line}");
                    break;
                case "fps":
                    var sourceFps = doc.RootElement.TryGetProperty("source", out var sf) &&
                                    sf.TryGetDouble(out var sfd) ? sfd : 0;
                    var targetFps = doc.RootElement.TryGetProperty("target", out var tf) &&
                                    tf.TryGetInt32(out var tfi) ? tfi : 0;
                    _log($"[{Monitor.Device}] fps source={sourceFps:0.###} target={targetFps}");
                    break;
                case "switched":
                    _log($"switch-applied device={Monitor.Device}");
                    break;

                case "stats":
                    // Keep the renderer observable without flooding host logs.
                    _log($"[{Monitor.Device}] {line}");
                    break;
                case "stopped":
                    // Renderer announces shutdown before its process exits.
                    // Do not mark this as an intentional stop here: WorkerW
                    // loss also emits "stopped", and the host must still run
                    // OnUnexpectedExit and restart the child.
                    if (_intentionalStop)
                        State = ChildState.Stopped;
                    break;
            }
        }
        catch
        {
            _log($"[{Monitor.Device}] stdout {line}");
        }
    }

    private void OnExited(Process p)
    {
        int code;
        try { code = p.ExitCode; } catch { code = -1; }
        var intentional = _intentionalStop;
        bool unexpected;
        lock (_gate)
        {
            if (!ReferenceEquals(p, _process))
                return;
            _process = null;
            Pid = 0;
            _lastExitAt = DateTime.UtcNow;
            unexpected = !intentional && State is not (ChildState.Stopped or ChildState.Degraded);
            if (unexpected)
            {
                State = ChildState.Faulted;
                LastError = $"exit code {code}";
                _log($"[{Monitor.Device}] child exited unexpectedly code={code}");
            }
            else
            {
                State = ChildState.Stopped;
                _log($"[{Monitor.Device}] child exited code={code} intentional={intentional}");
            }
        }
        p.Dispose();
        if (unexpected)
            UnexpectedExit?.Invoke(this);
    }

    public void Dispose() => Stop(false);
}
