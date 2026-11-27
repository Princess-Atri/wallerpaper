namespace Wallerpaper.Shell.Models;

/// <summary>
/// A letter section in the wallpaper grid ("A", "B", … or "#" for titles that
/// do not start with a latin letter). GridView needs the group to be an
/// collection itself, with the header text exposed as <see cref="Key"/>.
/// </summary>
public sealed class WallpaperGroup : List<WallpaperItem>
{
    public WallpaperGroup(string key) => Key = key;

    public string Key { get; }
}
