using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Saves and restores the normal Windows desktop wallpaper. Pausing
/// Wallerpaper stops the renderer and restores this image, rather than
/// leaving the last decoded video frame on the WorkerW surface.
///
/// The desktop image is replaced with the first frame of the playing
/// wallpaper (see <see cref="DesktopFirstFrameService"/>). Windows only
/// reports the wallpaper as <c>...\Themes\TranscodedWallpaper</c>, a single
/// mutable file that is rewritten on every change, so the user's wallpaper is
/// snapshotted to a stable PNG once and restored from there.
/// </summary>
public static class DesktopWallpaperService
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private static string? _original;

    private static string OriginalFolder => Path.Combine(AppStorage.CacheRoot,
        "desktop-original");

    /// <summary>Stable copy of the user's own desktop wallpaper.</summary>
    public static string OriginalCopyPath => Path.Combine(OriginalFolder, "wallpaper.png");

    /// <summary>Present while our generated frame is the live wallpaper.</summary>
    internal static string FrameMarkerPath => Path.Combine(OriginalFolder, "frame-active.flag");

    private static string SavedOriginalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wallerpaper", "desktop-original.txt");

    /// <summary>Path of the wallpaper Windows is currently displaying.</summary>
    public static string? CurrentPath
    {
        get
        {
            try
            {
                var buffer = new StringBuilder(1024);
                if (!SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Capacity,
                        buffer, 0))
                {
                    return null;
                }
                var path = buffer.ToString();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }
    }

    public static void Capture()
    {
        try
        {
            // A frame applied by a previous session is still on the desktop;
            // the wallpaper to restore is the snapshot taken before it.
            if (File.Exists(FrameMarkerPath) && File.Exists(OriginalCopyPath))
            {
                _original = OriginalCopyPath;
                return;
            }

            var path = CurrentPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            if (DesktopFirstFrameService.IsFrameFile(path))
            {
                _original = File.Exists(OriginalCopyPath) ? OriginalCopyPath : null;
                return;
            }

            _original = Snapshot(path) ? OriginalCopyPath : path;
            SaveOriginal(path);
        }
        catch { }
    }

    public static bool SetImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            return SystemParametersInfoString(SPI_SETDESKWALLPAPER, 0, path,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch
        {
            return false;
        }
    }

    public static bool Restore()
    {
        CaptureIfNeeded();
        if (string.IsNullOrWhiteSpace(_original) || !File.Exists(_original))
            return false;
        try
        {
            var ok = SystemParametersInfoString(SPI_SETDESKWALLPAPER, 0, _original,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            if (ok) ClearFrameMark();
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records that the desktop now shows a generated first frame.</summary>
    internal static void MarkFrameApplied()
    {
        try
        {
            Directory.CreateDirectory(OriginalFolder);
            File.WriteAllText(FrameMarkerPath, DateTime.Now.ToString("O"));
        }
        catch { }
    }

    internal static void ClearFrameMark()
    {
        try
        {
            if (File.Exists(FrameMarkerPath)) File.Delete(FrameMarkerPath);
        }
        catch { }
    }

    private static void CaptureIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_original))
            Capture();
    }

    /// <summary>Copies any wallpaper image into a stable PNG snapshot.</summary>
    private static bool Snapshot(string path)
    {
        try
        {
            Directory.CreateDirectory(OriginalFolder);
            var temp = Path.Combine(OriginalFolder, "snapshot.tmp");
            // TranscodedWallpaper is held open by Explorer while it is the
            // live wallpaper, so copy first and decode the copy.
            File.Copy(path, temp, overwrite: true);
            try
            {
                using var image = Image.FromFile(temp);
                image.Save(OriginalCopyPath, ImageFormat.Png);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
            return File.Exists(OriginalCopyPath) && new FileInfo(OriginalCopyPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveOriginal(string path)
    {
        try
        {
            var file = SavedOriginalPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && File.ReadAllText(file).Trim() == path)
                return;
            File.WriteAllText(file, path);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoString(uint uiAction, uint uiParam,
        string pvParam, uint fWinIni);
}
