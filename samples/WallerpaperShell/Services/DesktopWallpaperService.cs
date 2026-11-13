using System.Runtime.InteropServices;
using System.Text;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Saves and restores the normal Windows desktop wallpaper. Pausing
/// Wallerpaper stops the renderer and restores this image, rather than
/// leaving the last decoded video frame on the WorkerW surface.
/// </summary>
public static class DesktopWallpaperService
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private static string? _original;

    public static void Capture()
    {
        try
        {
            var buffer = new StringBuilder(1024);
            if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0))
            {
                var path = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    _original = path;
            }
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
            return SystemParametersInfoString(SPI_SETDESKWALLPAPER, 0, _original,
                SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch
        {
            return false;
        }
    }

    private static void CaptureIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_original))
            Capture();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
        StringBuilder pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfoString(uint uiAction, uint uiParam,
        string pvParam, uint fWinIni);
}
