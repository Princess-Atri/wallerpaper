using System.Diagnostics;
using System.Text.Json;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Owns the headless WallerpaperHost process. The shell writes the effective
/// scheduler.json and talks to the host over stdin (pause/resume/quit).
/// </summary>
public sealed class HostBridge : IDisposable
{
    private readonly ShellConfig _config;
    private readonly WallpaperCatalog _catalog;
    private Process? _process;
    private HostJob? _job;
    private bool _disposed;

    public HostBridge(ShellConfig config, WallpaperCatalog catalog)
    {
        _config = config;
        _catalog = catalog;
    }

    public event Action<string>? Log;
    public event Action<double, int>? FpsReported;
    public event Action<string>? SwitchApplied;
    public event Action<string>? SwitchFailed;
    public bool IsRunning => _process is { HasExited: false };

    public void Start()
    {
        if (_disposed || IsRunning)
            return;
        if (!File.Exists(_config.Host.HostExe))
        {
            Log?.Invoke($"host not found: {_config.Host.HostExe}");
            return;
        }

        try
        {
            // Renderer children can outlive a crashed Shell/Host. PauseAll
            // only reaches the current HostSession, so orphans keep drawing
            // and the global pause appears broken. Clear them before starting.
            KillOrphanedRenderers();
            WriteSchedulerConfig();
            try { _job = new HostJob(); }
            catch (Exception ex) { Log?.Invoke($"host job unavailable: {ex.Message}"); }

            var psi = new ProcessStartInfo
            {
                FileName = _config.Host.HostExe,
                Arguments = $"--config \"{_config.Host.SchedulerConfig}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_config.Host.HostExe)!
            };
            // A self-contained Shell may inherit DOTNET_ROOT=<app dir>; a
            // framework-dependent child would then search the app folder for
            // shared/Microsoft.NETCore.App and report "No frameworks were found".
            psi.Environment.Remove("DOTNET_ROOT");
            psi.Environment.Remove("DOTNET_ROOT_X64");
            psi.Environment.Remove("DOTNET_ROOT_X86");
            psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "1";
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                Log?.Invoke(e.Data);

                if (e.Data.Contains("switch-applied device=", StringComparison.OrdinalIgnoreCase))
                {
                    var at = e.Data.IndexOf("switch-applied device=", StringComparison.OrdinalIgnoreCase);
                    SwitchApplied?.Invoke(e.Data[(at + "switch-applied device=".Length)..].Trim());
                }
                else if (e.Data.Contains("switch-failed device=", StringComparison.OrdinalIgnoreCase))
                {
                    var at = e.Data.IndexOf("switch-failed device=", StringComparison.OrdinalIgnoreCase);
                    SwitchFailed?.Invoke(e.Data[(at + "switch-failed device=".Length)..].Trim());
                }

                var marker = e.Data.IndexOf("fps source=", StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                    return;
                var start = marker + "fps source=".Length;
                var targetMarker = e.Data.IndexOf(" target=", start, StringComparison.OrdinalIgnoreCase);
                if (targetMarker < 0)
                    return;
                var sourceText = e.Data.Substring(start, targetMarker - start)
                    .Trim().TrimEnd('f', 'F');
                var targetText = e.Data[(targetMarker + " target=".Length)..].Trim();
                if (double.TryParse(sourceText, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var source) &&
                    int.TryParse(targetText, out var target))
                    FpsReported?.Invoke(source, target);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Log?.Invoke("ERR " + e.Data);
            };
            _process.Exited += (_, _) => Log?.Invoke($"host exited: {_process?.ExitCode}");
            _process.Start();
            try { _job?.Assign(_process); }
            catch (Exception ex) { Log?.Invoke($"host job assign failed: {ex.Message}"); }
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Log?.Invoke($"host started pid={_process.Id}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"host start failed: {ex.Message}");
            _process?.Dispose();
            _process = null;
            _job?.Dispose();
            _job = null;
        }
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public void PauseAll() => Send("pause-all");
    public void ResumeAll() => Send("resume-all");

    public bool SwitchVideo(string device, string path)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(device) || !File.Exists(path))
            return false;
        Send(JsonSerializer.Serialize(new { cmd = "switch", device, path }));
        return true;
    }

    public void SetColor(string device, WallpaperItem item)
    {
        Send(JsonSerializer.Serialize(new
        {
            cmd = "color",
            device,
            brightness = item.Brightness,
            contrast = item.Contrast,
            highlights = item.Highlights,
            hue = item.Hue,
            temperature = item.Temperature,
            exposure = item.Exposure,
            sharpness = item.Sharpness
        }));
    }

    public void SetProperty(string device, string name, double value)
    {
        var json = JsonSerializer.Serialize(new
        {
            cmd = name,
            device,
            value
        });
        Send(json);
    }

    public void Send(string command)
    {
        try
        {
            if (_process is not { HasExited: false })
                return;
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"host command failed: {ex.Message}");
        }
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.StandardInput.WriteLine("quit");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(4000))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
            _job?.Dispose();
            _job = null;
        }
    }

    private void KillOrphanedRenderers()
    {
        foreach (var name in new[] { "VideoWallpaperHost", "WebWallpaperHost", "SceneWallpaperHost" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    Log?.Invoke($"killing orphaned renderer: {name} pid={process.Id}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
    }

    private void WriteSchedulerConfig()
    {
        var path = _config.Host.SchedulerConfig;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var first = _config.Wallpapers.FirstOrDefault(x => x.Kind != WallpaperKind.Static);
        var monitorDtos = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var (device, assignment) in _config.Monitors)
        {
            var item = _catalog.Find(assignment.WallpaperId);
            if (item is null || item.Kind == WallpaperKind.Static || !File.Exists(item.Path))
                continue;
            monitorDtos[device] = new
            {
                type = Kind(item.Kind),
                wallpaperPath = item.Path,
                secondPath = item.SecondPath,
                scene = item.Kind == WallpaperKind.Scene ? item.Path : "",
                volume = item.Muted ? 0 : item.Volume,
                speed = item.Speed,
                fit = item.Fit,
                zoom = item.Zoom,
                renderScale = item.RenderScale,
                fps = item.TargetFps > 0 ? item.TargetFps : _config.Performance.AcFps,
                brightness = item.Brightness,
                contrast = item.Contrast,
                highlights = item.Highlights,
                hue = item.Hue,
                temperature = item.Temperature,
                exposure = item.Exposure,
                sharpness = item.Sharpness,
                shaderIntensity = item.ShaderParameters.TryGetValue("intensity", out var intensity)
                    ? intensity
                    : 1.0
            };
        }

        var dto = new
        {
            childExe = _config.Host.ChildExe,
            videoChildExe = _config.Host.VideoChildExe,
            webChildExe = _config.Host.WebChildExe,
            type = first is null ? "scene" : Kind(first.Kind),
            wallpaperPath = first?.Path ?? "",
            secondPath = first?.SecondPath ?? "",
            scene = first?.Kind == WallpaperKind.Scene ? first.Path : "",
            fallbackImage = _config.Host.FallbackImage,
            policy = _config.Performance.Policy,
            triggerOnMaximized = _config.Performance.TriggerOnMaximized,
            volume = first is { Muted: false } ? first.Volume : 0,
            speed = first?.Speed ?? 1.0,
            fit = first?.Fit ?? "cover",
            zoom = first?.Zoom ?? 1.0,
            renderScale = first?.RenderScale ?? 1.0,
            brightness = first?.Brightness ?? 0,
            contrast = first?.Contrast ?? 0,
            highlights = first?.Highlights ?? 0,
            hue = first?.Hue ?? 0,
            temperature = first?.Temperature ?? 0,
            exposure = first?.Exposure ?? 0,
            sharpness = first?.Sharpness ?? 0,
            shaderIntensity = first is not null &&
                              first.ShaderParameters.TryGetValue("intensity", out var si) ? si : 1.0,
            acFps = _config.Performance.AcFps,
            batteryFps = _config.Performance.BatteryFps,
            monitors = monitorDtos
        };

        File.WriteAllText(path, JsonSerializer.Serialize(dto, ConfigStore.JsonOptions));
    }

    private static string Kind(WallpaperKind kind) => kind switch
    {
        WallpaperKind.Video => "video",
        WallpaperKind.Web => "web",
        WallpaperKind.Sequence => "sequence",
        _ => "scene"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
