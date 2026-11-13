using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wallerpaper.Shell.Services;

public sealed record VideoCropResult(string OutputPath, TimeSpan Elapsed);

/// <summary>
/// Spatial video crop. This changes the frame geometry (top/bottom/left/right
/// edges) and re-encodes once at high quality; it is separate from time trim.
/// The frame-capable FFmpeg performs crop + VP9 lossless; the libx264 FFmpeg
/// performs the final encode because the latter build has no crop filter.
/// </summary>
public sealed class VideoCropService
{
    public static async Task<string> ExtractFirstFrameAsync(string inputPath)
    {
        var ffmpeg = AiInterpolationService.FindFfmpegFrames()
            ?? throw new InvalidOperationException("未找到拆帧用 FFmpeg。");
        var dir = AppStorage.CachePath("crop-preview");
        var frame = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[]
                 {
                     "-y", "-hide_banner", "-loglevel", "error",
                     "-ss", "0", "-i", inputPath,
                     "-frames:v", "1", "-f", "image2", frame
                 })
            psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(frame))
            throw new InvalidOperationException("无法加载视频第一帧。");
        return frame;
    }

    public async Task<string> CreatePreviewAsync(
        string inputPath,
        double leftPercent,
        double rightPercent,
        double topPercent,
        double bottomPercent,
        CancellationToken cancellationToken = default)
    {
        var ffmpegFrames = AiInterpolationService.FindFfmpegFrames()
            ?? throw new InvalidOperationException("未找到拆帧/滤镜 FFmpeg。");
        var ffmpegEncode = AiInterpolationService.FindFfmpegEncode()
            ?? throw new InvalidOperationException("未找到编码用 FFmpeg。");
        var outDir = AppStorage.CachePath("crop-preview");
        var intermediate = Path.Combine(outDir, Guid.NewGuid().ToString("N") + ".mkv");
        var output = Path.Combine(outDir, Guid.NewGuid().ToString("N") + ".mp4");
        var l = Math.Clamp(leftPercent, 0, 45) / 100.0;
        var r = Math.Clamp(rightPercent, 0, 45) / 100.0;
        var t = Math.Clamp(topPercent, 0, 45) / 100.0;
        var b = Math.Clamp(bottomPercent, 0, 45) / 100.0;
        var filter =
            $"crop=w='trunc(iw*(1-{l.ToString("0.####", CultureInfo.InvariantCulture)}-" +
            $"{r.ToString("0.####", CultureInfo.InvariantCulture)})/2)*2':" +
            $"h='trunc(ih*(1-{t.ToString("0.####", CultureInfo.InvariantCulture)}-" +
            $"{b.ToString("0.####", CultureInfo.InvariantCulture)})/2)*2':" +
            $"x='iw*{l.ToString("0.####", CultureInfo.InvariantCulture)}':" +
            $"y='ih*{t.ToString("0.####", CultureInfo.InvariantCulture)}'";
        try
        {
            if (!await Run(ffmpegFrames, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", inputPath, "-vf", filter,
                    "-c:v", "libvpx-vp9", "-b:v", "4M", "-an", intermediate
                }, cancellationToken))
                throw new InvalidOperationException("预览裁剪视频生成失败。");
            if (!await Run(ffmpegEncode, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", intermediate,
                    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "27",
                    "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", output
                }, cancellationToken))
                throw new InvalidOperationException("预览视频编码失败。");
            return output;
        }
        finally
        {
            try { File.Delete(intermediate); } catch { }
        }
    }

    public async Task<VideoCropResult> CropAsync(
        string inputPath,
        double leftPercent,
        double rightPercent,
        double topPercent,
        double bottomPercent,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("视频不存在", inputPath);
        leftPercent = Math.Clamp(leftPercent, 0, 45);
        rightPercent = Math.Clamp(rightPercent, 0, 45);
        topPercent = Math.Clamp(topPercent, 0, 45);
        bottomPercent = Math.Clamp(bottomPercent, 0, 45);
        if (leftPercent + rightPercent >= 90 || topPercent + bottomPercent >= 90)
            throw new InvalidOperationException("左右或上下裁剪过多，至少保留 10% 画面。");

        var ffmpegFrames = AiInterpolationService.FindFfmpegFrames()
            ?? throw new InvalidOperationException("未找到拆帧/滤镜 FFmpeg。");
        var ffmpegEncode = AiInterpolationService.FindFfmpegEncode()
            ?? throw new InvalidOperationException("未找到编码用 FFmpeg。");
        var started = Stopwatch.StartNew();
        var outDir = AppStorage.CachePath("crop");
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var output = Path.Combine(outDir, $"{stem}_crop_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var intermediate = Path.Combine(Path.GetTempPath(), "WallerpaperCrop",
            Guid.NewGuid().ToString("N") + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(intermediate)!);

        var l = leftPercent / 100.0;
        var r = rightPercent / 100.0;
        var t = topPercent / 100.0;
        var b = bottomPercent / 100.0;
        var filter =
            $"crop=w='trunc(iw*(1-{l.ToString("0.####", CultureInfo.InvariantCulture)}-" +
            $"{r.ToString("0.####", CultureInfo.InvariantCulture)})/2)*2':" +
            $"h='trunc(ih*(1-{t.ToString("0.####", CultureInfo.InvariantCulture)}-" +
            $"{b.ToString("0.####", CultureInfo.InvariantCulture)})/2)*2':" +
            $"x='iw*{l.ToString("0.####", CultureInfo.InvariantCulture)}':" +
            $"y='ih*{t.ToString("0.####", CultureInfo.InvariantCulture)}'";

        try
        {
            progress?.Invoke(15, "裁剪画面并写入无损中间层…");
            var cropOk = await Run(ffmpegFrames, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", inputPath,
                "-vf", filter,
                "-c:v", "libvpx-vp9", "-lossless", "1", "-pix_fmt", "yuv444p", "-an",
                intermediate
            }, cancellationToken);
            if (!cropOk || !File.Exists(intermediate))
                throw new InvalidOperationException("画面裁剪失败（FFmpeg crop）。");

            progress?.Invoke(82, "高质量编码并写入音频…");
            var encodeOk = await Run(ffmpegEncode, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", intermediate,
                "-i", inputPath,
                "-map", "0:v:0",
                "-map", "1:a:0?",
                "-c:v", "libx264", "-preset", "medium", "-crf", "14", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k", "-shortest",
                "-movflags", "+faststart",
                output
            }, cancellationToken);
            if (!encodeOk || !File.Exists(output) || new FileInfo(output).Length < 1024)
                throw new InvalidOperationException("画面裁剪后的 H.264 编码失败。");

            progress?.Invoke(100, "画面裁剪完成");
            return new VideoCropResult(output, started.Elapsed);
        }
        finally
        {
            try { File.Delete(intermediate); } catch { }
        }
    }

    private static async Task<bool> Run(string exe, IEnumerable<string> args,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                stderr.AppendLine(e.Data);
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return process.ExitCode == 0;
    }
}
