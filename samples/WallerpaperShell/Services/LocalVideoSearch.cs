using System.Collections.Concurrent;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

public sealed class LocalVideoResult
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Display => $"{Name}    {Path}";
}

/// <summary>
/// Small local video index used by the top search box. It walks the user's
/// common folders and fixed drives (bounded depth, skipping system folders),
/// then answers name queries from memory. The walk runs on a background thread
/// and is cancelled when the window closes.
/// </summary>
public sealed class LocalVideoSearch
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".m4v", ".flv", ".ts", ".m2ts"
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "program files", "program files (x86)", "programdata",
        "appdata", "$recycle.bin", "system volume information", "recovery",
        "msocache", "node_modules", ".git", ".vs", "bin", "obj", "packages", "cache"
    };

    private readonly ShellConfig _config;
    private readonly ConcurrentDictionary<string, byte> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _built;

    public LocalVideoSearch(ShellConfig config) => _config = config;

    public async Task<IReadOnlyList<LocalVideoResult>> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<LocalVideoResult>();

        await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);

        var needle = query.Trim();
        return _paths.Keys
            .Select(path => new { path, name = System.IO.Path.GetFileNameWithoutExtension(path) })
            .Where(x => x.name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .Select(x => new LocalVideoResult
            {
                Path = x.path,
                Name = x.name
            })
            .ToList();
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_built)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_built)
                return;

            await Task.Run(() => BuildIndex(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            _built = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void BuildIndex(CancellationToken cancellationToken)
    {
        foreach (var (root, depth) in EnumerateRoots())
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            try
            {
                Walk(root, 0, depth, cancellationToken);
            }
            catch
            {
                // Inaccessible folder; the rest of the index is still useful.
            }
        }
    }

    private IEnumerable<(string Root, int Depth)> EnumerateRoots()
    {
        var roots = new List<(string, int)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path, int depth)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            var full = System.IO.Path.GetFullPath(path);
            if (!seen.Add(full))
                return;
            roots.Add((full, depth));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "Videos", "Downloads", "Desktop", "Documents", "Pictures", "Music" })
            Add(System.IO.Path.Combine(home, name), 8);

        foreach (var root in _config.Library.Where(x => x.Enabled))
        {
            if (Directory.Exists(root.Path))
                Add(root.Path, 8);
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                    Add(drive.RootDirectory.FullName, 4);
            }
            catch { }
        }

        return roots;
    }

    private void Walk(string directory, int depth, int maxDepth, CancellationToken cancellationToken)
    {
        if (depth > maxDepth || cancellationToken.IsCancellationRequested)
            return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(directory); }
        catch { return; }

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (VideoExtensions.Contains(System.IO.Path.GetExtension(file)))
                _paths.TryAdd(file, 0);
        }

        if (depth == maxDepth)
            return;

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(directory); }
        catch { return; }

        foreach (var child in children)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            try
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System |
                                        FileAttributes.ReparsePoint)) != 0)
                    continue;
                if (SkipDirectories.Contains(info.Name))
                    continue;
                Walk(info.FullName, depth + 1, maxDepth, cancellationToken);
            }
            catch { }
        }
    }
}
