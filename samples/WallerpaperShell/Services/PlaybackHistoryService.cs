using System.Text.Json;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

public sealed class PlaybackHistoryEntry
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";
    public string AppliedAt { get; set; } = "";
}

/// <summary>
/// Recent wallpaper applications. Used both for the playback-history view and
/// for choosing the previous wallpaper when the current one is deleted.
/// </summary>
public static class PlaybackHistoryService
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int MaxEntries = 80;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wallerpaper", "playback-history.json");

    public static void Record(WallpaperItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) return;
        lock (Gate)
        {
            var list = LoadUnsafe();
            list.RemoveAll(x => x.Id == item.Id);
            list.Insert(0, new PlaybackHistoryEntry
            {
                Id = item.Id,
                Path = item.Path,
                Title = item.Title,
                Kind = item.Kind.ToString(),
                AppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            if (list.Count > MaxEntries)
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            SaveUnsafe(list);
        }
    }

    public static List<PlaybackHistoryEntry> Load()
    {
        lock (Gate) return LoadUnsafe();
    }

    public static WallpaperItem? FindPrevious(
        IEnumerable<WallpaperItem> catalog, ISet<string> excludedIds)
    {
        var items = catalog.ToList();
        foreach (var entry in Load())
        {
            if (excludedIds.Contains(entry.Id)) continue;
            var item = items.FirstOrDefault(x =>
                string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (item is not null) return item;
        }
        return null;
    }

    private static List<PlaybackHistoryEntry> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<PlaybackHistoryEntry>();
            return JsonSerializer.Deserialize<List<PlaybackHistoryEntry>>(
                File.ReadAllText(FilePath), JsonOptions) ?? new List<PlaybackHistoryEntry>();
        }
        catch
        {
            return new List<PlaybackHistoryEntry>();
        }
    }

    private static void SaveUnsafe(List<PlaybackHistoryEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOptions));
    }
}
