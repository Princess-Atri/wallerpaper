using Wallerpaper.Shell.Models;
using Windows.Storage;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Sets the Windows lock-screen picture. Windows only accepts a still image
/// here: the lock screen renders before the user logs in, so no renderer is
/// running and video/scene wallpapers cannot appear on it.
/// </summary>
public static class LockScreenService
{
    /// <summary>Copies the image into the app cache first so the lock screen
    /// keeps working when the original is moved or deleted.</summary>
    public static async Task<bool> SetImageAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return false;

        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
        {
            // Convert anything else (webp/gif) into PNG with GDI+.
            var converted = await Task.Run(() =>
            {
                try
                {
                    var target = Path.Combine(AppStorage.CachePath("lock-screen"),
                        $"{Path.GetFileNameWithoutExtension(sourcePath)}.png");
                    using var image = System.Drawing.Image.FromFile(sourcePath);
                    image.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                    return target;
                }
                catch
                {
                    return null;
                }
            });
            if (converted is null) return false;
            sourcePath = converted;
        }

        var folder = AppStorage.CachePath("lock-screen");
        var stable = Path.Combine(folder, "current" + Path.GetExtension(sourcePath));
        try
        {
            await Task.Run(() => File.Copy(sourcePath, stable, overwrite: true));
            var file = await StorageFile.GetFileFromPathAsync(stable);
            await Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> SetImageAsync(WallpaperItem item)
    {
        if (item.Kind == WallpaperKind.Static)
            return await SetImageAsync(item.Path);

        // Video/scene/sequence/web wallpapers cannot play on the lock screen;
        // use their poster/thumbnail so the lock screen still matches.
        var thumb = item.ThumbnailPath;
        if (!string.IsNullOrWhiteSpace(thumb) && File.Exists(thumb))
            return await SetImageAsync(thumb);
        return false;
    }
}
