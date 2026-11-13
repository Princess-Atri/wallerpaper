using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Thumbnail cache:
/// - video: Windows Shell IShellItemImageFactory (codec thumbnail)
/// - scene: first image texture from scene.json
/// - web: deterministic placeholder (no hidden WebView2 process)
/// </summary>
public sealed class ThumbnailService
{
    private readonly ShellConfig _config;
    private readonly SemaphoreSlim _gate = new(2, 2);

    public ThumbnailService(ShellConfig config)
    {
        _config = config;
        Directory.CreateDirectory(_config.Cache.Directory);
    }

    public async Task<string?> GetAsync(WallpaperItem item, CancellationToken ct = default)
    {
        var width = _config.Cache.ThumbnailWidth;
        var height = _config.Cache.ThumbnailHeight;
        var cache = Path.Combine(_config.Cache.Directory, $"{item.Id}_{width}x{height}.png");
        if (File.Exists(cache))
        {
            item.ThumbnailPath = cache;
            return cache;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(cache))
                return cache;

            await Task.Run(() =>
            {
                var source = item.Kind == WallpaperKind.Scene
                    ? FindSceneImage(item.Path)
                    : item.Path;

                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                {
                    CreatePlaceholder(item, width, height, cache);
                    return;
                }
                if (item.Kind == WallpaperKind.Video &&
                    TryFfmpegThumbnail(source, width, height, cache))
                    return;
                if (!TryShellThumbnail(source, width, height, cache) &&
                    !TryImageFile(source, width, height, cache))
                    CreatePlaceholder(item, width, height, cache);
            }, ct);

            item.ThumbnailPath = cache;
            TrimCache();
            return cache;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Clear()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_config.Cache.Directory, "*.png"))
                File.Delete(f);
        }
        catch { }
    }

    private void TrimCache()
    {
        try
        {
            var maxBytes = (long)Math.Max(64, _config.Cache.MaxSizeMb) * 1024 * 1024;
            var files = new DirectoryInfo(_config.Cache.Directory)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => x.LastAccessTimeUtc)
                .ToList();
            var total = files.Sum(x => x.Length);
            foreach (var file in files)
            {
                if (total <= maxBytes) break;
                total -= file.Length;
                file.Delete();
            }
        }
        catch { }
    }

    public long CacheBytes()
    {
        try
        {
            return new DirectoryInfo(_config.Cache.Directory)
                .EnumerateFiles("*.png", SearchOption.TopDirectoryOnly)
                .Sum(x => x.Length);
        }
        catch { return 0; }
    }

    private static string? FindSceneImage(string scenePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(scenePath)!;
            using var doc = JsonDocument.Parse(File.ReadAllText(scenePath));
            if (!doc.RootElement.TryGetProperty("layers", out var layers))
                return null;

            foreach (var layer in layers.EnumerateArray())
            {
                var type = layer.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var key in new[] { "texture", "path", "file", "image" })
                {
                    if (!layer.TryGetProperty(key, out var p))
                        continue;
                    var value = p.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    var full = Path.IsPathRooted(value) ? value : Path.Combine(dir, value);
                    if (File.Exists(full))
                        return full;
                }
            }
        }
        catch { }
        return null;
    }

    private static bool TryFfmpegThumbnail(string path, int width, int height, string cache)
    {
        try
        {
            var ffmpeg = AiInterpolationService.FindFfmpegFrames();
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // A frame a little after the start avoids fade-in black frames.
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add("00:00:00.5");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add(
                $"scale={width}:{height}:force_original_aspect_ratio=increase," +
                $"crop={width}:{height}");
            psi.ArgumentList.Add(cache);
            using var process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(true); } catch { }
                return false;
            }
            return process.ExitCode == 0 && File.Exists(cache) &&
                   new FileInfo(cache).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryImageFile(string path, int width, int height, string cache)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var source = Image.FromFile(path);
            using var bmp = Fit(source, width, height);
            bmp.Save(cache, ImageFormat.Png);
            return true;
        }
        catch { return false; }
    }

    private static bool TryShellThumbnail(string path, int width, int height, string cache)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            if (!File.Exists(path))
                return false;

            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid,
                out var factoryObj);
            var factory = (IShellItemImageFactory)factoryObj;
            var size = new SIZE { cx = width, cy = height };
            if (factory.GetImage(size, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out hbitmap) != 0 ||
                hbitmap == IntPtr.Zero)
                return false;

            using var source = Image.FromHbitmap(hbitmap);
            using var bmp = Fit(source, width, height);
            bmp.Save(cache, ImageFormat.Png);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
                DeleteObject(hbitmap);
        }
    }

    private static void CreatePlaceholder(WallpaperItem item, int width, int height, string cache)
    {
        using var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var kindColor = item.Kind switch
        {
            WallpaperKind.Video => Color.FromArgb(38, 112, 180),
            WallpaperKind.Web => Color.FromArgb(122, 78, 170),
            _ => Color.FromArgb(34, 150, 128)
        };

        using (var brush = new LinearGradientBrush(
                   new Rectangle(0, 0, width, height),
                   kindColor, Color.FromArgb(20, 22, 30), 35f))
            g.FillRectangle(brush, 0, 0, width, height);

        using var accent = new SolidBrush(Color.FromArgb(70, Color.White));
        g.FillRectangle(accent, 0, height - 52, width, 52);

        using var titleFont = new Font("Segoe UI", Math.Max(12f, width / 28f), FontStyle.Bold);
        using var smallFont = new Font("Segoe UI", Math.Max(9f, width / 48f));
        using var white = new SolidBrush(Color.White);
        g.DrawString(item.Kind.ToString().ToUpperInvariant(), smallFont, white, 20, 18);
        g.DrawString(item.Title, titleFont, white,
            new RectangleF(20, height - 46, width - 40, 38));

        bmp.Save(cache, ImageFormat.Png);
    }

    private static Bitmap Fit(Image source, int width, int height)
    {
        var scale = Math.Max((float)width / source.Width, (float)height / source.Height);
        var w = (int)(source.Width * scale);
        var h = (int)(source.Height * scale);
        var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, (width - w) / 2, (height - h) / 2, w, h);
        return bmp;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
