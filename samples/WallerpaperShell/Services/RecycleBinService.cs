using System.Text.Json;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

public sealed class RecycleEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DeletedAt { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    public WallpaperItem? Item { get; set; }
    public List<string> OriginalPaths { get; set; } = new();
    public List<string> StoredPaths { get; set; } = new();
}

/// <summary>
/// A real recycle bin: deleting a library item keeps a private copy of its
/// source files until the user permanently deletes the recycle entry.
/// </summary>
public static class RecycleBinService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string Root => AppStorage.RecycleRoot;

    private static string IndexPath => Path.Combine(Root, "recycle.json");

    public static async Task AddAsync(WallpaperItem item, bool deleteOriginal,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        var entry = new RecycleEntry
        {
            Id = id,
            Title = item.Title,
            Kind = item.Kind.ToString(),
            DeletedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ThumbnailPath = item.ThumbnailPath,
            Item = item
        };

        foreach (var source in new[] { item.Path, item.SecondPath }
                     .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var target = Path.Combine(folder,
                    Path.GetFileNameWithoutExtension(source) + "_" +
                    Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(source));
                await Task.Run(() => File.Copy(source, target, true), ct);
                entry.OriginalPaths.Add(source);
                entry.StoredPaths.Add(target);
            }
            catch
            {
                // A missing or locked source must not abort deleting the
                // library entry; the recycle record still keeps metadata.
            }
        }

        if (deleteOriginal)
        {
            foreach (var source in entry.OriginalPaths)
            {
                try { File.Delete(source); } catch { }
            }
        }

        lock (Gate)
        {
            var list = LoadUnsafe();
            list.Insert(0, entry);
            SaveUnsafe(list);
        }
    }

    public static List<RecycleEntry> Load()
    {
        lock (Gate) return LoadUnsafe();
    }

    public static WallpaperItem Restore(RecycleEntry entry)
    {
        var restored = entry.Item ?? new WallpaperItem
        {
            Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
            Title = entry.Title,
            Kind = Enum.TryParse<WallpaperKind>(entry.Kind, true, out var kind)
                ? kind : WallpaperKind.Video
        };

        var restoredPaths = new List<string>();
        for (var i = 0; i < entry.StoredPaths.Count; i++)
        {
            var stored = entry.StoredPaths[i];
            var original = i < entry.OriginalPaths.Count
                ? entry.OriginalPaths[i]
                : Path.Combine(Path.GetTempPath(), Path.GetFileName(stored));
            if (!File.Exists(stored))
            {
                if (File.Exists(original)) restoredPaths.Add(original);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Copy(stored, original, true);
            restoredPaths.Add(original);
        }

        if (restoredPaths.Count > 0) restored.Path = restoredPaths[0];
        if (restoredPaths.Count > 1) restored.SecondPath = restoredPaths[1];
        restored.Missing = false;

        lock (Gate)
        {
            var list = LoadUnsafe();
            list.RemoveAll(x => x.Id == entry.Id);
            SaveUnsafe(list);
            DeleteFiles(entry);
        }
        return restored;
    }

    public static void Delete(RecycleEntry entry)
    {
        lock (Gate)
        {
            var list = LoadUnsafe();
            list.RemoveAll(x => x.Id == entry.Id);
            SaveUnsafe(list);
            DeleteFiles(entry);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            var list = LoadUnsafe();
            foreach (var entry in list) DeleteFiles(entry);
            SaveUnsafe(new List<RecycleEntry>());
        }
    }

    private static void DeleteFiles(RecycleEntry entry)
    {
        foreach (var file in entry.StoredPaths)
        {
            try { File.Delete(file); } catch { }
        }
        try
        {
            var folder = Path.Combine(Root, entry.Id);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch { }
    }

    private static List<RecycleEntry> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<RecycleEntry>();
            return JsonSerializer.Deserialize<List<RecycleEntry>>(
                File.ReadAllText(IndexPath), JsonOptions) ?? new List<RecycleEntry>();
        }
        catch
        {
            return new List<RecycleEntry>();
        }
    }

    private static void SaveUnsafe(List<RecycleEntry> list)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(list, JsonOptions));
    }
}
