using System.Security.Cryptography;
using System.Text;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Owns the app's private copy of source videos. The catalog points at this
/// copy, so moving/deleting the user's original file cannot break a wallpaper.
/// </summary>
public static class ManagedLibraryService
{
    public static string Root
    {
        get
        {
            // Keep the copied wallpapers beside the D: software folder, not
            // in the C: user profile. WALLERPAPER_LIBRARY can override this
            // for portable installations.
            var overridePath = Environment.GetEnvironmentVariable("WALLERPAPER_LIBRARY");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return Path.GetFullPath(overridePath);

            var appFolder = @"D:\Desktop\wallerpaper c";
            if (Directory.Exists(appFolder))
                return Path.Combine(appFolder, "library");
            return @"D:\Wallerpaper\library";
        }
    }

    public static bool IsManaged(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> ImportAsync(string sourcePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("壁纸文件不存在", sourcePath);
        var full = Path.GetFullPath(sourcePath);
        if (IsManaged(full))
            return full;

        Directory.CreateDirectory(Root);
        var hash = Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes(full))).ToLowerInvariant()[..10];
        var name = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        var target = Path.Combine(Root, $"{name}_{hash}{extension}");

        if (File.Exists(target))
            return target;

        var temp = target + ".importing";
        await Task.Run(() => File.Copy(full, temp, true), ct);
        File.Move(temp, target, true);
        return target;
    }

    public static long TotalBytes()
    {
        try
        {
            if (!Directory.Exists(Root)) return 0;
            return new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(x => x.Length);
        }
        catch { return 0; }
    }
}
