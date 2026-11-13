using System.Text.Json;
using System.Text.Json.Serialization;
using Wallerpaper.Shell.Services;

namespace Wallerpaper.Shell.Models;

public enum WallpaperKind
{
    Video,
    Static,
    Web,
    Scene,
    Sequence
}

public sealed class ShellConfig
{
    public int Version { get; set; } = 1;
    public GeneralConfig General { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public CacheConfig Cache { get; set; } = new();
    public HostConfig Host { get; set; } = new();
    public PlaybackConfig Playback { get; set; } = new();
    public List<LibraryRoot> Library { get; set; } = new();
    public List<WallpaperItem> Wallpapers { get; set; } = new();
    public List<string> ExcludedPaths { get; set; } = new();
    public Dictionary<string, MonitorAssignment> Monitors { get; set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; set; } = "";

    public static ShellConfig CreateDefault()
    {
        var cfg = new ShellConfig();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        cfg.Cache.Directory = AppStorage.CacheRoot;
        cfg.Library.Add(new LibraryRoot
        {
            Path = Path.Combine(home, "Videos", "Wallerpaper"),
            Kind = WallpaperKind.Video,
            Recursive = true
        });
        cfg.Library.Add(new LibraryRoot
        {
            Path = Path.Combine(docs, "Wallerpaper", "Web"),
            Kind = WallpaperKind.Web,
            Recursive = true
        });
        cfg.Library.Add(new LibraryRoot
        {
            Path = Path.Combine(docs, "Wallerpaper", "Scenes"),
            Kind = WallpaperKind.Scene,
            Recursive = true
        });
        cfg.Host.HostExe = Path.Combine(AppContext.BaseDirectory, "WallerpaperHost.exe");
        cfg.Host.SchedulerConfig = Path.Combine(local, "Wallerpaper", "scheduler.json");
        cfg.Host.ChildExe = Path.Combine(AppContext.BaseDirectory, "SceneWallpaperHost.exe");
        cfg.Host.VideoChildExe = Path.Combine(AppContext.BaseDirectory, "VideoWallpaperHost.exe");
        cfg.Host.WebChildExe = Path.Combine(AppContext.BaseDirectory, "WebWallpaperHost.exe");
        return cfg;
    }

    public void Normalize()
    {
        General ??= new();
        Performance ??= new();
        Cache ??= new();
        Host ??= new();
        Playback ??= new();
        Playback.DeleteCurrentBehavior = Playback.DeleteCurrentBehavior?.ToLowerInvariant() switch
        {
            "first" => "first",
            "pause" => "pause",
            _ => "previous"
        };
        Library ??= new();
        Wallpapers ??= new();
        ExcludedPaths ??= new();
        Monitors ??= new();

        if (string.IsNullOrWhiteSpace(Cache.Directory))
            Cache.Directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wallerpaper", "cache");
        Cache.Directory = Environment.ExpandEnvironmentVariables(Cache.Directory);
        if (Cache.Directory.StartsWith(
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "Wallerpaper", "cache"),
                StringComparison.OrdinalIgnoreCase))
            Cache.Directory = AppStorage.CacheRoot;
        Cache.ThumbnailWidth = Math.Clamp(Cache.ThumbnailWidth, 160, 1280);
        Cache.ThumbnailHeight = Math.Clamp(Cache.ThumbnailHeight, 90, 720);
        Performance.AcFps = Math.Clamp(Performance.AcFps, 24, 120);
        Performance.BatteryFps = Math.Clamp(Performance.BatteryFps, 24, 120);
        Performance.RenderScale = Math.Clamp(Performance.RenderScale, 0.5, 2.0);
        Performance.MaxTextureMemoryMb = Math.Clamp(Performance.MaxTextureMemoryMb, 64, 8192);
        Performance.MaxParticles = Math.Clamp(Performance.MaxParticles, 0, 4096);

        Host.HostExe = Environment.ExpandEnvironmentVariables(Host.HostExe ?? "");
        Host.SchedulerConfig = Environment.ExpandEnvironmentVariables(Host.SchedulerConfig ?? "");
        Host.ChildExe = Environment.ExpandEnvironmentVariables(Host.ChildExe ?? "");
        Host.VideoChildExe = Environment.ExpandEnvironmentVariables(Host.VideoChildExe ?? "");
        Host.WebChildExe = Environment.ExpandEnvironmentVariables(Host.WebChildExe ?? "");
        Host.FallbackImage = Environment.ExpandEnvironmentVariables(Host.FallbackImage ?? "");

        // The installed app and the portable build both keep Host binaries
        // next to WallerpaperShell.exe. Prefer that layout over stale absolute
        // paths left in an old shell-config.json.
        var baseDir = AppContext.BaseDirectory;
        string Beside(string name) => Path.Combine(baseDir, name);
        if (File.Exists(Beside("WallerpaperHost.exe"))) Host.HostExe = Beside("WallerpaperHost.exe");
        if (File.Exists(Beside("SceneWallpaperHost.exe"))) Host.ChildExe = Beside("SceneWallpaperHost.exe");
        if (File.Exists(Beside("VideoWallpaperHost.exe"))) Host.VideoChildExe = Beside("VideoWallpaperHost.exe");
        if (File.Exists(Beside("WebWallpaperHost.exe"))) Host.WebChildExe = Beside("WebWallpaperHost.exe");
        if (string.IsNullOrWhiteSpace(Host.SchedulerConfig))
            Host.SchedulerConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wallerpaper", "scheduler.json");

        foreach (var root in Library)
            root.Path = Environment.ExpandEnvironmentVariables(root.Path ?? "");

        foreach (var item in Wallpapers)
        {
            item.Volume = Math.Clamp(item.Volume, 0, 100);
            item.Speed = Math.Clamp(item.Speed, 0.1, 4.0);
            item.SourceFps = Math.Clamp(item.SourceFps, 0, 240);
            item.TargetFps = Math.Clamp(item.TargetFps, 0, 120);
            item.Fit = item.Fit?.ToLowerInvariant() switch
            {
                "contain" => "contain",
                "blur" => "blur",
                _ => "cover"
            };
            item.Zoom = Math.Clamp(item.Zoom, 0.5, 2.0);
            item.RenderScale = Math.Clamp(item.RenderScale, 0.5, 2.0);
            item.Brightness = Math.Clamp(item.Brightness, -100, 100);
            item.Contrast = Math.Clamp(item.Contrast, -100, 100);
            item.Highlights = Math.Clamp(item.Highlights, -100, 100);
            item.Hue = Math.Clamp(item.Hue, -100, 100);
            item.Temperature = Math.Clamp(item.Temperature, -100, 100);
            item.Exposure = Math.Clamp(item.Exposure, -100, 100);
            item.Sharpness = Math.Clamp(item.Sharpness, -100, 100);
            item.RgbR = Math.Clamp(item.RgbR, -100, 100);
            item.RgbG = Math.Clamp(item.RgbG, -100, 100);
            item.RgbB = Math.Clamp(item.RgbB, -100, 100);
            item.ShaderParameters ??= new();
            item.ThumbnailPath ??= "";
            item.SecondPath ??= "";
            item.Playlist ??= new();
            foreach (var entry in item.Playlist)
            {
                entry.Path = Environment.ExpandEnvironmentVariables(entry.Path ?? "");
                entry.StartSeconds = Math.Max(0, entry.StartSeconds);
                entry.EndSeconds = Math.Max(0, entry.EndSeconds);
                if (entry.EndSeconds > 0 && entry.EndSeconds <= entry.StartSeconds)
                    entry.EndSeconds = entry.StartSeconds + 0.1;
            }
        }
    }
}

public sealed class GeneralConfig
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
}

public sealed class PlaybackConfig
{
    // previous | first | pause
    public string DeleteCurrentBehavior { get; set; } = "previous";
}

public sealed class PerformanceConfig
{
    public string Policy { get; set; } = "pause"; // continue | pause | stop
    public bool TriggerOnMaximized { get; set; }
    public int AcFps { get; set; } = 60;
    public int BatteryFps { get; set; } = 30;
    public double RenderScale { get; set; } = 1.0;
    public int MaxTextureMemoryMb { get; set; } = 512;
    public int MaxParticles { get; set; } = 1024;
    public int MaxConcurrentHosts { get; set; } = 3;
    public bool PauseOnBatterySaver { get; set; }
}

public sealed class CacheConfig
{
    public string Directory { get; set; } = "";
    public int MaxSizeMb { get; set; } = 2048;
    public int ThumbnailWidth { get; set; } = 480;
    public int ThumbnailHeight { get; set; } = 270;
    public bool KeepThumbnails { get; set; } = true;
}

public sealed class HostConfig
{
    public string HostExe { get; set; } = "";
    public string SchedulerConfig { get; set; } = "";
    public string ChildExe { get; set; } = "";
    public string VideoChildExe { get; set; } = "";
    public string WebChildExe { get; set; } = "";
    public bool AutoStartHost { get; set; } = true;
    public string FallbackImage { get; set; } = "";
}

public sealed class LibraryRoot
{
    public string Path { get; set; } = "";
    public WallpaperKind Kind { get; set; }
    public bool Recursive { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public sealed class WallpaperItem : System.ComponentModel.INotifyPropertyChanged
{
    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;
    private string _title = "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = "";
    public string Title
    {
        get => _title;
        set
        {
            if (string.Equals(_title, value, StringComparison.Ordinal))
                return;
            _title = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Title)));
        }
    }
    public WallpaperKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string SecondPath { get; set; } = "";
    public List<PlaylistEntry> Playlist { get; set; } = new();
    public string Folder { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    public double Volume { get; set; } = 0;
    public double Speed { get; set; } = 1.0;
    public double SourceFps { get; set; }
    public int TargetFps { get; set; }
    public string Fit { get; set; } = "cover";
    public bool IsFavorite { get; set; }
    public double Zoom { get; set; } = 1.0;
    public double RenderScale { get; set; } = 1.0;
    public bool Muted { get; set; } = true;
    // Per-wallpaper picture adjustments, all -100..100. Zero means untouched.
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Hue { get; set; }
    public double Temperature { get; set; }
    public double Exposure { get; set; }
    public double Sharpness { get; set; }
    public double RgbR { get; set; }
    public double RgbG { get; set; }
    public double RgbB { get; set; }
    public Dictionary<string, double> ShaderParameters { get; set; } = new();
    public bool Missing { get; set; }

    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }
}

public sealed class PlaylistEntry
{
    public string Path { get; set; } = "";
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }

    [JsonIgnore]
    public string Display =>
        $"{System.IO.Path.GetFileName(Path)}  [{StartSeconds:0.##}–{EndSeconds:0.##}s]";
}

public sealed class MonitorAssignment
{
    public string WallpaperId { get; set; } = "";
    public int Fps { get; set; } = 60;
    public bool Enabled { get; set; } = true;
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wallerpaper", "shell-config.json");

    public static ShellConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return ShellConfig.CreateDefault();

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<ShellConfig>(json, JsonOptions)
                      ?? ShellConfig.CreateDefault();
            cfg.ConfigPath = path;
            cfg.Normalize();
            TitleOverrideStore.Capture(cfg.Wallpapers);
            TitleOverrideStore.Apply(cfg.Wallpapers);
            return cfg;
        }
        catch
        {
            var fallback = ShellConfig.CreateDefault();
            fallback.ConfigPath = path;
            return fallback;
        }
    }

    public static void Save(ShellConfig config, string? path = null)
    {
        path ??= string.IsNullOrWhiteSpace(config.ConfigPath)
            ? DefaultPath
            : config.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        config.ConfigPath = path;
        config.Normalize();

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
