using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

public sealed record CacheCleanupResult(int DeletedFiles, long FreedBytes, int KeptInUse);

/// <summary>
/// Keeps the derived-file cache (crop/auto-crop/superres/ai/…) from growing
/// without limit. Files still referenced by the library, the recycle bin, or
/// the currently applied wallpaper are never deleted.
/// </summary>
public static class CacheCleanupService
{
    /// <summary>Folders that only hold regenerable derivatives.</summary>
    private static readonly string[] DerivativeFolders =
    {
        "crop", "auto-crop", "superres", "mp4-export", "ai", "queue",
        "crop-preview", "recycle-thumbs", "static", "sr-work"
    };

    /// <summary>Never pruned by age; only orphan pruning applies.</summary>
    private static readonly string[] PersistentFolders = { "lock-screen" };

    /// <summary>
    /// Build/test output under <c>artifacts</c> is disposable but is never a
    /// product dependency, so it gets a small grace period to keep the
    /// screenshots from the current debugging session around.
    /// </summary>
    private const int ArtifactsMinAgeDays = 3;

    /// <summary>
    /// Crash dumps are diagnostic only (and each one can be hundreds of MB):
    /// keep the newest dump, drop the rest after a few days.
    /// </summary>
    private const int DumpRetentionDays = 3;

    public static CacheCleanupResult Cleanup(ShellConfig config, int maxAgeDays)
    {
        var cacheRoot = AppStorage.CacheRoot;
        var deleted = 0;
        long freed = 0;
        var kept = 0;

        // Snapshot the config before touching any file, so a wrong "orphan"
        // decision can always be rolled back.
        BackupConfig(config);

        if (Directory.Exists(cacheRoot))
        {
            var protectedPaths = CollectProtected(config);
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, maxAgeDays));

            foreach (var folderName in DerivativeFolders)
            {
                var folder = Path.Combine(cacheRoot, folderName);
                if (!Directory.Exists(folder)) continue;
                foreach (var file in SafeEnumerate(folder))
                {
                    if (IsProtected(file, protectedPaths))
                    {
                        kept++;
                        continue;
                    }
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoff) continue;
                    TryDelete(info, ref deleted, ref freed);
                }
            }

            // Orphan pruning is age gated too: a cache file can look unreferenced
    // simply because the config was written before it was added, or because
    // two configs were in use during a rename/migration. Deleting it
    // immediately used to wipe library items that lived in the cache.
            foreach (var folderName in PersistentFolders)
            {
                var folder = Path.Combine(cacheRoot, folderName);
                if (!Directory.Exists(folder)) continue;
                foreach (var file in SafeEnumerate(folder))
                {
                    if (IsProtected(file, protectedPaths)) { kept++; continue; }
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoff) continue;
                    TryDelete(info, ref deleted, ref freed);
                }
            }

            RemoveEmptyFolders(cacheRoot);
        }

        // Only the frame of the wallpaper that is currently playing is needed;
        // a crashed switch may have left several behind.
        DesktopFirstFrameService.Cleanup();

        // Build/test output is regenerable by tools\build-release.ps1 and is
        // never read by the running app, so the timed pass covers it too.
        var (artifactsDeleted, artifactsFreed) = CleanupArtifacts(maxAgeDays);
        var (dumpsDeleted, dumpsFreed) = CleanupCrashDumps();
        return new CacheCleanupResult(
            deleted + artifactsDeleted + dumpsDeleted,
            freed + artifactsFreed + dumpsFreed,
            kept);
    }

    /// <summary>Drops old crash dumps, always keeping the newest one.</summary>
    public static (int Files, long Bytes) CleanupCrashDumps()
    {
        var folder = Wallerpaper.Diagnostics.AppLog.DumpDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
            return (0, 0);
        if (!Directory.Exists(folder)) return (0, 0);

        var cutoff = DateTime.UtcNow.AddDays(-DumpRetentionDays);
        var deleted = 0;
        long freed = 0;
        try
        {
            var dumps = new DirectoryInfo(folder)
                .EnumerateFiles("*.dmp", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(1) // the newest dump stays for post-mortem analysis
                .ToList();
            foreach (var info in dumps)
            {
                if (info.LastWriteTimeUtc > cutoff) continue;
                TryDelete(info, ref deleted, ref freed);
            }
        }
        catch { }
        return (deleted, freed);
    }

    /// <summary>Total size of the crash-dump folder.</summary>
    public static (int Files, long Bytes) MeasureCrashDumps()
    {
        var folder = Wallerpaper.Diagnostics.AppLog.DumpDirectory;
        var files = 0;
        long bytes = 0;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return (0, 0);
        foreach (var file in SafeEnumerate(folder))
        {
            try
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
            catch { }
        }
        return (files, bytes);
    }

    /// <summary>Deletes stale build/test output; returns (files, bytes).</summary>
    public static (int Files, long Bytes) CleanupArtifacts(int maxAgeDays)
    {
        var root = AppStorage.ArtifactsRoot;
        if (!Directory.Exists(root)) return (0, 0);

        var cutoff = DateTime.UtcNow
            .AddDays(-Math.Max(ArtifactsMinAgeDays, maxAgeDays));
        var deleted = 0;
        long freed = 0;
        foreach (var file in SafeEnumerate(root))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > cutoff) continue;
                TryDelete(info, ref deleted, ref freed);
            }
            catch { }
        }
        RemoveEmptyFoldersDeep(root);
        return (deleted, freed);
    }

    private static bool TryDelete(FileInfo info, ref int deleted, ref long freed)
    {
        try
        {
            var length = info.Length;
            info.Delete();
            deleted++;
            freed += length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reports current usage without deleting anything.</summary>
    public static (int Files, long Bytes) Measure()
    {
        var cacheRoot = AppStorage.CacheRoot;
        if (!Directory.Exists(cacheRoot)) return (0, 0);
        var files = 0;
        long bytes = 0;
        foreach (var file in SafeEnumerate(cacheRoot))
        {
            try
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
            catch { }
        }
        return (files, bytes);
    }

    /// <summary>Size of the disposable build/test output folder.</summary>
    public static (int Files, long Bytes) MeasureArtifacts()
    {
        var root = AppStorage.ArtifactsRoot;
        if (!Directory.Exists(root)) return (0, 0);
        var files = 0;
        long bytes = 0;
        foreach (var file in SafeEnumerate(root))
        {
            try
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
            catch { }
        }
        return (files, bytes);
    }

    private static HashSet<string> CollectProtected(ShellConfig config)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { set.Add(Path.GetFullPath(path)); } catch { }
        }

        foreach (var item in config.Wallpapers)
        {
            Add(item.Path);
            Add(item.SecondPath);
            Add(item.ThumbnailPath);
            foreach (var entry in item.Playlist ?? new List<PlaylistEntry>())
                Add(entry.Path);
        }
        foreach (var root in config.Library)
        {
            if (string.IsNullOrWhiteSpace(root.Path)) continue;
            if (File.Exists(root.Path))
                Add(root.Path);
            else if (Directory.Exists(root.Path))
                // Trailing separator marks a whole subtree as protected: every
                // media file a user keeps in a configured library folder is part
                // of the library, not a disposable derivative.
                Add(root.Path.TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar);
        }
        try
        {
            foreach (var entry in RecycleBinService.Load())
                foreach (var stored in entry.StoredPaths)
                    Add(stored);
        }
        catch { }
        // The applied desktop/lock-screen image is derived but must survive.
        var lockImage = Path.Combine(AppStorage.CacheRoot, "lock-screen", "current.png");
        Add(lockImage);
        return set;
    }

    /// <summary>Keeps the five newest shell-config snapshots next to the live
    /// config file. Cleaning without a backup once cost the user library items,
    /// so every run now leaves a rollback point.</summary>
    private static void BackupConfig(ShellConfig config)
    {
        try
        {
            var path = config.ConfigPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            var dir = Path.GetDirectoryName(path)!;
            var target = Path.Combine(dir,
                $"shell-config.bak-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            if (!File.Exists(target))
                File.Copy(path, target);
            foreach (var old in Directory.EnumerateFiles(dir, "shell-config.bak-*.json")
                         .OrderByDescending(x => x)
                         .Skip(5))
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch
        {
            // A backup failure must never block the cleanup itself.
        }
    }

    private static bool IsProtected(string file, HashSet<string> protectedPaths)
    {
        try
        {
            var full = Path.GetFullPath(file);
            if (protectedPaths.Contains(full))
                return true;
            // Entries ending with a separator protect a whole library subtree:
            // files a user put into the cache on purpose (crop / superres
            // output that lives in the library folder) must never be cleaned
            // just because the config did not reference them yet.
            foreach (var entry in protectedPaths)
            {
                if (entry.Length > 3 && entry.EndsWith(Path.DirectorySeparatorChar) &&
                    full.StartsWith(entry, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }
        foreach (var file in files.ToList())
            yield return file;
    }

    private static void RemoveEmptyFolders(string root)
    {
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(sub).Any())
                        Directory.Delete(sub);
                }
                catch { }
            }
        }
    }

    /// <summary>Bottom-up prune so nested publish/test trees collapse fully.</summary>
    private static void RemoveEmptyFoldersDeep(string root)
    {
        try
        {
            var folders = Directory
                .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Length)
                .ToList();
            foreach (var folder in folders)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                        Directory.Delete(folder);
                }
                catch { }
            }
        }
        catch { }
    }
}
