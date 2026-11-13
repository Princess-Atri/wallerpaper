using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

public sealed class WallpaperCatalog
{
    private readonly ShellConfig _config;
    private readonly List<WallpaperItem> _items = new();

    public WallpaperCatalog(ShellConfig config)
    {
        _config = config;
        _items.AddRange(config.Wallpapers);
    }

    public IReadOnlyList<WallpaperItem> Items => _items;

    public WallpaperItem? Find(string id) =>
        _items.FirstOrDefault(x => x.Id == id);

    public void Remove(WallpaperItem item)
    {
        _items.RemoveAll(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        _config.Wallpapers.RemoveAll(x =>
            string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
    }

    public List<WallpaperItem> ByKind(WallpaperKind kind) =>
        _items.Where(x => x.Kind == kind).OrderBy(x => x.Title).ToList();

    public int Refresh()
    {
        var known = _items
            .GroupBy(x => $"{x.Kind}|{x.Path}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var found = new List<WallpaperItem>();

        // Keep explicitly added wallpapers even if a library root was removed,
        // inaccessible, or the app was restarted before the root was rescanned.
        foreach (var existing in known.Values)
        {
            if (string.IsNullOrWhiteSpace(existing.Path) ||
                _config.ExcludedPaths.Any(x =>
                    string.Equals(x, existing.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (existing.Kind == WallpaperKind.Sequence)
            {
                if (File.Exists(existing.Path) && File.Exists(existing.SecondPath))
                    found.Add(existing);
                continue;
            }
            if (File.Exists(existing.Path) || Directory.Exists(existing.Path))
                found.Add(existing);
        }

        foreach (var root in _config.Library.Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(root.Path))
                continue;

            try
            {
                if (File.Exists(root.Path))
                {
                    AddFile(root.Path, root.Kind, root.Path, found);
                }
                else if (Directory.Exists(root.Path))
                {
                    if (root.Kind == WallpaperKind.Scene)
                    {
                        foreach (var scene in Directory.EnumerateFiles(root.Path, "scene.json",
                                     root.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                            AddFile(scene, WallpaperKind.Scene, root.Path, found);
                    }
                    else
                    {
                        foreach (var file in Directory.EnumerateFiles(root.Path, "*.*",
                                     root.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                            AddFile(file, root.Kind, root.Path, found);
                    }
                }
            }
            catch
            {
                // A single inaccessible folder must not break the catalog.
            }
        }

        // Preserve per-wallpaper edits across rescans.
        foreach (var item in found)
        {
            if (known.TryGetValue($"{item.Kind}|{item.Path}", out var old))
            {
                item.Title = old.Title;
                item.SecondPath = old.SecondPath;
                item.Volume = old.Volume;
                item.Speed = old.Speed;
                item.Muted = old.Muted;
                item.ShaderParameters = old.ShaderParameters;
                item.ThumbnailPath = old.ThumbnailPath;
                item.Fit = old.Fit;
                item.IsFavorite = old.IsFavorite;
                item.Brightness = old.Brightness;
                item.Contrast = old.Contrast;
                item.Highlights = old.Highlights;
                item.Hue = old.Hue;
                item.Temperature = old.Temperature;
                item.Exposure = old.Exposure;
                item.Sharpness = old.Sharpness;
                item.RgbR = old.RgbR;
                item.RgbG = old.RgbG;
                item.RgbB = old.RgbB;
                item.Zoom = old.Zoom;
                item.RenderScale = old.RenderScale;
                item.TargetFps = old.TargetFps;
            }
        }

        _items.Clear();
        _items.AddRange(found
            .GroupBy(x => $"{x.Kind}|{x.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Title));

        TitleOverrideStore.Apply(_items);
        _config.Wallpapers = _items.ToList();
        return _items.Count;
    }

    public WallpaperItem? AddPath(string path, WallpaperKind kind)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;

        if (Directory.Exists(path) && kind == WallpaperKind.Scene)
        {
            var scene = Path.Combine(path, "scene.json");
            if (File.Exists(scene))
                path = scene;
        }

        var list = new List<WallpaperItem>();
        AddFile(path, kind, Path.GetDirectoryName(path) ?? "", list);
        var item = list.FirstOrDefault();
        if (item is null)
            return null;

        if (_items.Any(x => x.Kind == item.Kind &&
                            string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            return _items.First(x => x.Kind == item.Kind &&
                string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase));

        _items.Add(item);
        _config.Wallpapers = _items.ToList();
        return item;
    }

    public WallpaperItem AddSequence(string firstPath, string secondPath, string? title = null)
    {
        firstPath = Path.GetFullPath(firstPath);
        secondPath = Path.GetFullPath(secondPath);
        _config.ExcludedPaths.RemoveAll(x =>
            string.Equals(x, firstPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x, secondPath, StringComparison.OrdinalIgnoreCase));
        var existing = _items.FirstOrDefault(x =>
            x.Kind == WallpaperKind.Sequence &&
            string.Equals(x.Path, firstPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.SecondPath, secondPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var item = new WallpaperItem
        {
            Id = IdFor(firstPath + "|" + secondPath),
            Title = string.IsNullOrWhiteSpace(title)
                ? $"{Path.GetFileNameWithoutExtension(firstPath)} → {Path.GetFileNameWithoutExtension(secondPath)}"
                : title,
            Kind = WallpaperKind.Sequence,
            Path = firstPath,
            SecondPath = secondPath,
            Folder = Path.GetDirectoryName(firstPath) ?? ""
        };
        _items.Add(item);
        _config.Wallpapers = _items.ToList();
        return item;
    }

    private void AddFile(string path, WallpaperKind rootKind, string folder, List<WallpaperItem> target)
    {
        if (_config.ExcludedPaths.Any(x =>
                string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
            return;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        WallpaperKind kind;
        if (ext == ".json" && Path.GetFileName(path).Equals("scene.json", StringComparison.OrdinalIgnoreCase))
            kind = WallpaperKind.Scene;
        else if (ext is ".mp4" or ".webm" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".m4v" or ".flv" or ".ts" or ".m2ts")
            kind = WallpaperKind.Video;
        else if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
            kind = WallpaperKind.Static;
        else if (ext is ".html" or ".htm")
            kind = WallpaperKind.Web;
        else
            return;

        // A folder root must only import its own media type. Previously a
        // Video folder could turn index.html into a bogus video item.
        if (kind != rootKind)
            return;

        // The same source file may intentionally appear both as a normal video
        // and as the first/second clip of a sequence. Deduplicate by kind+path,
        // not by path alone.
        if (target.Any(x => x.Kind == kind &&
                            string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        target.Add(new WallpaperItem
        {
            Id = IdFor(path),
            Title = TitleFor(path, kind),
            Kind = kind,
            Path = path,
            Folder = folder
        });
    }

    private static string TitleFor(string path, WallpaperKind kind)
    {
        if (kind == WallpaperKind.Scene)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("name", out var name) &&
                    !string.IsNullOrWhiteSpace(name.GetString()))
                    return name.GetString()!;
            }
            catch { }
            return Path.GetFileName(Path.GetDirectoryName(path)) ?? "Scene";
        }

        if (kind == WallpaperKind.Web)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(stem, "index", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var html = File.ReadAllText(path);
                    var match = System.Text.RegularExpressions.Regex.Match(html,
                        @"<title[^>]*>(?<t>.*?)</title>",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (match.Success)
                    {
                        var title = System.Net.WebUtility.HtmlDecode(match.Groups["t"].Value).Trim();
                        if (!string.IsNullOrWhiteSpace(title))
                            return title;
                    }
                }
                catch { }
                return Path.GetFileName(Path.GetDirectoryName(path)) ?? stem;
            }
            return stem;
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string IdFor(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
