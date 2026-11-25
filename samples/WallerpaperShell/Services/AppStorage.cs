namespace Wallerpaper.Shell.Services;

/// <summary>
/// All large wallpaper/derived files live on the D: software folder. Small
/// settings and logs can remain in the user profile.
/// </summary>
public static class AppStorage
{
    public static string SoftwareRoot
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("BAIACHA_ROOT") ?? Environment.GetEnvironmentVariable("WALLERPAPER_ROOT");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return Path.GetFullPath(overridePath);
            var d = @"D:\Desktop\wallerpaper c";
            return Directory.Exists(d) ? d : @"D:\BaiAcha";
        }
    }

    public static string CacheRoot => Path.Combine(SoftwareRoot, "cache");

    public static string CachePath(params string[] parts)
    {
        var path = CacheRoot;
        foreach (var part in parts) path = Path.Combine(path, part);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string RecycleRoot => Path.Combine(SoftwareRoot, "recycle");

    /// <summary>开发期构建/测试产物（发布目录、安装包、截图、超分试样）。</summary>
    public static string ArtifactsRoot => Path.Combine(SoftwareRoot, "artifacts");

    public static string ExportBesideSource(string tempFile, string sourceFile, string suffix)
    {
        var folder = Path.GetDirectoryName(sourceFile) ?? Path.GetDirectoryName(tempFile) ?? "";
        var stem = Path.GetFileNameWithoutExtension(sourceFile);
        var ext = Path.GetExtension(tempFile);
        var candidate = Path.Combine(folder, stem + suffix + ext);
        var index = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{stem}{suffix}_{index++}{ext}");
        File.Copy(tempFile, candidate, overwrite: false);
        return candidate;
    }
}
