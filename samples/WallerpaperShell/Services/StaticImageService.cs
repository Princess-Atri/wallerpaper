using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

/// <summary>Static image adjustments and Real-ESRGAN image upscaling.</summary>
public static class StaticImageService
{
    public static string RenderAdjusted(WallpaperItem item)
    {
        if (item.Kind != WallpaperKind.Static || !File.Exists(item.Path))
            return item.Path;

        var key = string.Join(",",
            item.Path, item.Brightness, item.Contrast, item.Highlights,
            item.Hue, item.Temperature, item.Exposure, item.Sharpness,
            item.RgbR, item.RgbG, item.RgbB);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        var dir = AppStorage.CachePath("static");
        var output = Path.Combine(dir, $"{item.Id}_{hash}.png");
        if (File.Exists(output)) return output;

        using var source = new Bitmap(item.Path);
        using var target = new Bitmap(source.Width, source.Height,
            PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var srcData = source.LockBits(rect, ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        var dstData = target.LockBits(rect, ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var src = new int[source.Width * source.Height];
            var dst = new int[src.Length];
            System.Runtime.InteropServices.Marshal.Copy(
                srcData.Scan0, src, 0, src.Length);

            var brightness = Math.Clamp(item.Brightness + item.Exposure * 0.5,
                -100, 100) / 100.0;
            var contrast = 1.0 + Math.Clamp(item.Contrast, -100, 100) / 100.0;
            var gamma = 1.0 - Math.Clamp(item.Highlights, -100, 100) / 300.0;
            var temperature = Math.Clamp(item.Temperature, -100, 100) / 300.0;
            var hue = Math.Clamp(item.Hue, -100, 100) / 500.0;
            var gammaR = 1.0 - Math.Clamp(item.RgbR, -100, 100) / 300.0;
            var gammaG = 1.0 - Math.Clamp(item.RgbG, -100, 100) / 300.0;
            var gammaB = 1.0 - Math.Clamp(item.RgbB, -100, 100) / 300.0;

            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                var a = (c >> 24) & 0xFF;
                var r = ((c >> 16) & 0xFF) / 255.0;
                var g = ((c >> 8) & 0xFF) / 255.0;
                var b = (c & 0xFF) / 255.0;

                r = Math.Clamp((r - 0.5) * contrast + 0.5 + brightness, 0, 1);
                g = Math.Clamp((g - 0.5) * contrast + 0.5 + brightness, 0, 1);
                b = Math.Clamp((b - 0.5) * contrast + 0.5 + brightness, 0, 1);
                r = Math.Pow(r, gamma * gammaR) * (1 + temperature + hue);
                g = Math.Pow(g, gamma * gammaG);
                b = Math.Pow(b, gamma * gammaB) * (1 - temperature - hue);

                var rr = (int)Math.Clamp(r * 255, 0, 255);
                var gg = (int)Math.Clamp(g * 255, 0, 255);
                var bb = (int)Math.Clamp(b * 255, 0, 255);
                dst[i] = (a << 24) | (rr << 16) | (gg << 8) | bb;
            }
            System.Runtime.InteropServices.Marshal.Copy(
                dst, 0, dstData.Scan0, dst.Length);
        }
        finally
        {
            source.UnlockBits(srcData);
            target.UnlockBits(dstData);
        }
        target.Save(output, ImageFormat.Png);
        return output;
    }

    public static async Task<string> UpscaleAsync(string inputPath, int factor,
        string model, Action<int, string>? progress, CancellationToken ct)
    {
        var esrgan = SuperResolutionService.FindRealEsrgan()
            ?? throw new InvalidOperationException("未找到 Real-ESRGAN。");
        factor = factor == 4 ? 4 : 2;
        model = string.IsNullOrWhiteSpace(model) ? "realesrgan-x4plus" : model;
        if (model.Contains("x4plus", StringComparison.OrdinalIgnoreCase))
            factor = 4;

        var dir = AppStorage.CachePath("static");
        var output = Path.Combine(dir,
            $"{Path.GetFileNameWithoutExtension(inputPath)}_SR{factor}x_" +
            $"{DateTime.Now:yyyyMMdd-HHmmss}.png");

        var psi = new ProcessStartInfo
        {
            FileName = esrgan,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(esrgan)!
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(output);
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(factor.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("png");

        progress?.Invoke(10, "Real-ESRGAN 处理图片…");
        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("图片超分失败。");
        progress?.Invoke(100, "图片超分完成");
        return output;
    }
}
