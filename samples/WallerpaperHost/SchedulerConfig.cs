using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallerpaper.Host;

internal enum PerformancePolicy
{
    Continue,
    Pause,
    Stop
}

internal sealed class SchedulerConfig
{
    public string ChildExe { get; set; } = "";
    public string? VideoChildExe { get; set; }
    public string? WebChildExe { get; set; }
    public string Type { get; set; } = "scene"; // scene | video | web | sequence
    public string WallpaperPath { get; set; } = "";
    public string SecondPath { get; set; } = "";
    public string Scene { get; set; } = "";
    public string Policy { get; set; } = "pause";
    public bool TriggerOnMaximized { get; set; }
    public double Volume { get; set; }
    public double Speed { get; set; } = 1.0;
    public string Fit { get; set; } = "cover";
    public double Zoom { get; set; } = 1.0;
    public double RenderScale { get; set; } = 1.0;
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Hue { get; set; }
    public double Temperature { get; set; }
    public double Exposure { get; set; }
    public double Sharpness { get; set; }
    public double ShaderIntensity { get; set; } = 1.0;
    public int AcFps { get; set; } = 60;
    public int BatteryFps { get; set; } = 30;
    public string FallbackImage { get; set; } = "";
    public List<string> ExcludeProcesses { get; set; } = new();
    public List<string> FullscreenScanProcesses { get; set; } = new();
    public List<string> ExcludeTitles { get; set; } = new();
    public List<string> ExcludeClasses { get; set; } = new();
    public Dictionary<string, MonitorWallpaper> Monitors { get; set; } = new();
    public WatchdogConfig Watchdog { get; set; } = new();
    public string? BaseDirectory { get; set; }

    public PerformancePolicy PolicyValue =>
        Enum.TryParse<PerformancePolicy>(Policy, true, out var p) ? p : PerformancePolicy.Pause;

    public static SchedulerConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        var cfg = JsonSerializer.Deserialize<SchedulerConfig>(
            File.ReadAllText(full),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? throw new InvalidDataException("empty scheduler config");

        cfg.BaseDirectory = Path.GetDirectoryName(full)!;
        cfg.ChildExe = cfg.Resolve(cfg.ChildExe);
        cfg.VideoChildExe = string.IsNullOrWhiteSpace(cfg.VideoChildExe) ? null : cfg.Resolve(cfg.VideoChildExe);
        cfg.WebChildExe = string.IsNullOrWhiteSpace(cfg.WebChildExe) ? null : cfg.Resolve(cfg.WebChildExe);
        cfg.Type = (cfg.Type ?? "scene").ToLowerInvariant();
        cfg.Scene = cfg.Resolve(cfg.Scene);
        cfg.WallpaperPath = string.IsNullOrWhiteSpace(cfg.WallpaperPath) ? cfg.Scene : cfg.Resolve(cfg.WallpaperPath);
        cfg.FallbackImage = string.IsNullOrWhiteSpace(cfg.FallbackImage) ? "" : cfg.Resolve(cfg.FallbackImage);

        if (cfg.ExcludeProcesses.Count == 0)
        {
            cfg.ExcludeProcesses.AddRange(new[]
            {
                "Taskmgr.exe", "SystemSettings.exe", "ApplicationFrameHost.exe",
                "ShellExperienceHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe",
                "LockApp.exe", "WallerpaperHost.exe", "SceneWallpaperHost.exe"
            });
        }
        if (cfg.FullscreenScanProcesses.Count == 0)
        {
            // Background fullscreen scanning is deliberately scoped to known
            // media/media-capable processes. Foreground fullscreen is still
            // detected for every process. Add game/player exes here as needed.
            cfg.FullscreenScanProcesses.AddRange(new[]
            {
                "PotPlayerMini64.exe", "mpv.exe", "vlc.exe", "wmplayer.exe",
                "msedge.exe", "chrome.exe", "firefox.exe"
            });
        }
        if (cfg.ExcludeTitles.Count == 0)
            cfg.ExcludeTitles.AddRange(new[] { "Task Manager", "任务管理器", "Settings", "设置" });
        cfg.Volume = Math.Clamp(cfg.Volume, 0, 100);
        cfg.Speed = Math.Clamp(cfg.Speed, 0.1, 4.0);
        cfg.Fit = cfg.Fit?.ToLowerInvariant() switch
        {
            "contain" => "contain",
            "blur" => "blur",
            _ => "cover"
        };
        cfg.Zoom = Math.Clamp(cfg.Zoom, 0.5, 2.0);
        cfg.RenderScale = Math.Clamp(cfg.RenderScale, 0.5, 2.0);
        cfg.ShaderIntensity = Math.Clamp(cfg.ShaderIntensity, 0, 2);
        if (cfg.ExcludeClasses.Count == 0)
            cfg.ExcludeClasses.AddRange(new[] { "TaskManagerWindow", "ApplicationFrameWindow" });

        foreach (var m in cfg.Monitors.Values)
        {
            m.Scene = cfg.Resolve(m.Scene);
            m.WallpaperPath = string.IsNullOrWhiteSpace(m.WallpaperPath) ? m.Scene : cfg.Resolve(m.WallpaperPath);
            m.ChildExe = string.IsNullOrWhiteSpace(m.ChildExe) ? "" : cfg.Resolve(m.ChildExe);
            m.Type = (m.Type ?? "").ToLowerInvariant();
            m.Volume = Math.Clamp(m.Volume, 0, 100);
            m.Speed = Math.Clamp(m.Speed, 0.1, 4.0);
            m.Fit = m.Fit?.ToLowerInvariant() switch
            {
                "contain" => "contain",
                "blur" => "blur",
                _ => "cover"
            };
            m.Zoom = Math.Clamp(m.Zoom, 0.5, 2.0);
            m.RenderScale = Math.Clamp(m.RenderScale, 0.5, 2.0);
            m.Brightness = Math.Clamp(m.Brightness, -100, 100);
            m.Contrast = Math.Clamp(m.Contrast, -100, 100);
            m.Highlights = Math.Clamp(m.Highlights, -100, 100);
            m.Hue = Math.Clamp(m.Hue, -100, 100);
            m.Temperature = Math.Clamp(m.Temperature, -100, 100);
            m.Exposure = Math.Clamp(m.Exposure, -100, 100);
            m.Sharpness = Math.Clamp(m.Sharpness, -100, 100);
            m.ShaderIntensity = Math.Clamp(m.ShaderIntensity, 0, 2);
        }

        return cfg;
    }

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(BaseDirectory ?? ".", path));
    }
}

internal sealed class MonitorWallpaper
{
    public string Type { get; set; } = "";
    public string WallpaperPath { get; set; } = "";
    public string SecondPath { get; set; } = "";
    public string Scene { get; set; } = "";
    public string ChildExe { get; set; } = "";
    public double Volume { get; set; }
    public double Speed { get; set; } = 1.0;
    public string Fit { get; set; } = "cover";
    public double Zoom { get; set; } = 1.0;
    public double RenderScale { get; set; } = 1.0;
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Hue { get; set; }
    public double Temperature { get; set; }
    public double Exposure { get; set; }
    public double Sharpness { get; set; }
    public double ShaderIntensity { get; set; } = 1.0;
    public int Fps { get; set; }
    public bool Enabled { get; set; } = true;
}

internal sealed class WatchdogConfig
{
    public int MaxRestarts { get; set; } = 3;
    public int RestartWindowSeconds { get; set; } = 60;
    public int RestartDelayMs { get; set; } = 1000;
}
