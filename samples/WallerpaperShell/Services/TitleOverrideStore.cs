using System.Text.Json;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Keeps custom wallpaper names independent from shell-config.json rescans.
/// A catalog refresh rebuilds items from files; without this store a title
/// could fall back to the file name whenever an entry is recreated.
/// </summary>
public static class TitleOverrideStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _titles;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wallerpaper", "title-overrides.json");

    public static void Apply(IEnumerable<WallpaperItem> items)
    {
        var map = Load();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Path)) continue;
            if (map.TryGetValue(Key(item.Kind, item.Path), out var title) &&
                !string.IsNullOrWhiteSpace(title))
                item.Title = title;
        }
    }

    public static void Capture(IEnumerable<WallpaperItem> items)
    {
        var map = Load();
        var changed = false;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.Title)) continue;
            var key = Key(item.Kind, item.Path);
            if (map.ContainsKey(key)) continue;
            var fallback = Path.GetFileNameWithoutExtension(item.Path);
            if (!string.Equals(item.Title, fallback, StringComparison.Ordinal))
            {
                map[key] = item.Title;
                changed = true;
            }
        }
        if (changed) Save(map);
    }

    public static void Set(string path, string title,
        WallpaperKind kind = WallpaperKind.Video)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title))
            return;
        var map = Load();
        map[Key(kind, path)] = title;
        Save(map);
    }

    private static string Key(WallpaperKind kind, string path) =>
        $"{(int)kind}|{Normalize(path)}";

    private static Dictionary<string, string> Load()
    {
        lock (Gate)
        {
            if (_titles is not null) return _titles;
            try
            {
                if (File.Exists(FilePath))
                {
                    _titles = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(FilePath)) ?? new(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            _titles ??= new(StringComparer.OrdinalIgnoreCase);
            return _titles;
        }
    }

    private static void Save(Dictionary<string, string> map)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }
}
