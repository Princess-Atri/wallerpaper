using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wallerpaper.Shell.Services;

public sealed record AiInterpolationResult(
    string OutputPath,
    double SourceFps,
    double TargetFps,
    int Factor,
    TimeSpan Elapsed);

/// <summary>
/// Offline RIFE interpolation: FFmpeg -> PNG frames -> rife-ncnn-vulkan ->
/// FFmpeg encode with the original audio. Deliberately runs outside the
/// wallpaper renderer so the live wallpaper is never blocked.
/// </summary>
public sealed class AiInterpolationService
{
    public static string ToolRoot
    {
        get
        {
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wallerpaper", "ai-tools");
            if (Directory.Exists(local))
                return local;
            var beside = Path.Combine(AppContext.BaseDirectory, "ai-tools");
            if (Directory.Exists(beside))
                return beside;
            return @"D:\Desktop\wallerpaper c\tools\ai";
        }
    }

    public static string? FindRife()
    {
        foreach (var root in CandidateRoots())
        {
            foreach (var path in Directory.EnumerateFiles(root, "rife-ncnn-vulkan.exe",
                         SearchOption.AllDirectories))
                return path;
        }
        return null;
    }

    public static string? FindFfmpeg()
    {
        return FindTool("ffmpeg-encode.exe")
               ?? FindTool("ffmpeg.exe")
               ?? FindTool("ffmpeg-frames.exe");
    }

    public static string? FindFfmpegFrames()
    {
        return FindTool("ffmpeg-frames.exe")
               ?? FindTool("ffmpeg.exe")
               ?? FindTool("ffmpeg-encode.exe");
    }

    public static string? FindFfmpegEncode()
    {
        return FindTool("ffmpeg-encode.exe")
               ?? FindTool("ffmpeg.exe")
               ?? FindTool("ffmpeg-frames.exe");
    }

    private static string? FindTool(string fileName)
    {
        foreach (var root in CandidateRoots())
        {
            var direct = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (direct is not null)
                return direct;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wallerpaper", "ai-tools"),
            Path.Combine(AppContext.BaseDirectory, "ai-tools"),
            @"D:\Desktop\wallerpaper c\tools\ai"
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(root))
                yield return root;
    }

    public async Task<AiInterpolationResult> InterpolateAsync(
        string inputPath,
        double sourceFps,
        int factor,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("source video not found", inputPath);
        if (factor is not (2 or 4))
            throw new ArgumentOutOfRangeException(nameof(factor), "factor must be 2 or 4");
        if (sourceFps <= 0.1)
            sourceFps = 30;

        var rife = FindRife() ?? throw new InvalidOperationException(
            "未找到 rife-ncnn-vulkan.exe。请把 RIFE 放到 Wallerpaper\\ai-tools。");
        var ffmpegFrames = FindFfmpegFrames() ?? throw new InvalidOperationException(
            "未找到拆帧用 FFmpeg。请把 ffmpeg-frames.exe 放到 Wallerpaper\\ai-tools。");
        var ffmpegEncode = FindFfmpegEncode() ?? throw new InvalidOperationException(
            "未找到 H.264 编码用 FFmpeg。请把 ffmpeg-encode.exe 放到 Wallerpaper\\ai-tools。");
        var modelDir = Path.Combine(Path.GetDirectoryName(rife)!, "rife-v4.6");
        if (!Directory.Exists(modelDir))
            throw new InvalidOperationException("未找到 RIFE 模型 rife-v4.6。");

        var targetFps = sourceFps * factor;
        var started = Stopwatch.StartNew();
        var workDir = Path.Combine(Path.GetTempPath(), "WallerpaperAI", Guid.NewGuid().ToString("N"));
        var framesDir = Path.Combine(workDir, "frames");
        var outputFramesDir = Path.Combine(workDir, "out");
        var audioPath = Path.Combine(workDir, "audio.m4a");
        Directory.CreateDirectory(framesDir);
        Directory.CreateDirectory(outputFramesDir);

        var outputDir = AppStorage.CachePath("ai");
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var outputPath = Path.Combine(outputDir,
            $"{stem}_AI{factor}x_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

        try
        {
            progress?.Invoke(3, "提取音频…");
            var hasAudio = await RunFfmpegAsync(ffmpegFrames,
                new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", inputPath,
                    "-vn", "-c:a", "aac", "-b:a", "192k", audioPath
                }, null, cancellationToken);

            progress?.Invoke(8, "拆分视频帧…");
            var extractArgs = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", inputPath,
                "-map", "0:v:0",
                "-vsync", "0"
            };
            if (sourceFps > 0.1)
            {
                extractArgs.Add("-vf");
                extractArgs.Add($"fps={sourceFps.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            extractArgs.Add("-f");
            extractArgs.Add("image2");
            extractArgs.Add(Path.Combine(framesDir, "%08d.png"));
            var extractOk = await RunFfmpegAsync(ffmpegFrames, extractArgs, null, cancellationToken);
            if (!extractOk)
                throw new InvalidOperationException("FFmpeg 拆帧失败。");

            var frameCount = Directory.EnumerateFiles(framesDir, "*.png").Count();
            if (frameCount < 2)
                throw new InvalidOperationException("视频帧数不足，无法插帧。");

            progress?.Invoke(32, $"AI 插帧中：{frameCount} 帧 → {frameCount * factor} 帧…");
            var rifeArgs = new[]
            {
                "-i", framesDir,
                "-o", outputFramesDir,
                "-m", modelDir,
                "-n", (frameCount * factor).ToString(CultureInfo.InvariantCulture),
                "-j", "2:4",
                "-f", "%08d.png"
            };
            var rifeOk = await RunProcessAsync(rife, rifeArgs,
                line => { }, cancellationToken);
            if (!rifeOk)
                throw new InvalidOperationException("RIFE 插帧失败，请检查 GPU/Vulkan 驱动。");
            var outputFrameCount = Directory.EnumerateFiles(outputFramesDir, "*.png").Count();
            if (outputFrameCount < 2)
                throw new InvalidOperationException("RIFE 没有输出有效帧，无法生成插帧视频。");

            // Keep an intermediate that is truly lossless for the frame data.
            // The minimal encoder FFmpeg has libx264 but no Image2/PNG input,
            // so the frame-capable FFmpeg first writes VP9 lossless, then the
            // encoder FFmpeg turns it into H.264 at high quality.
            progress?.Invoke(82, "写入无损中间视频…");
            var intermediate = Path.Combine(workDir, "lossless.mkv");
            // VP9 lossless is the only lossless encoder in this FFmpeg build
            // whose result the H.264 encoder can decode. 4:4:4 is preferred,
            // but some drivers/builds reject it; 4:2:0 lossless is the
            // compatibility fallback.
            var losslessOk = false;
            foreach (var pixelFormat in new[] { "yuv444p", "yuv420p" })
            {
                try { if (File.Exists(intermediate)) File.Delete(intermediate); } catch { }
                var losslessArgs = new List<string>
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "image2",
                    "-framerate", targetFps.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", Path.Combine(outputFramesDir, "%08d.png"),
                    "-c:v", "libvpx-vp9", "-lossless", "1", "-pix_fmt", pixelFormat, "-an",
                    intermediate
                };
                losslessOk =
                    await RunProcessAsync(ffmpegFrames, losslessArgs, line => { }, cancellationToken) &&
                    File.Exists(intermediate) && new FileInfo(intermediate).Length > 0;
                if (losslessOk) break;
                progress?.Invoke(84, $"无损中间视频写入失败，改用 {pixelFormat} 重试…");
            }
            if (!losslessOk)
            {
                // Some FFmpeg builds reject every VP9 lossless variant. Use
                // the Media Foundation H.264 encoder directly from RIFE's
                // PNG frames; quality is still high and the final file is
                // universally decodable.
                progress?.Invoke(85, "无损中间层不可用，改用高质量 H.264 中间层…");
                var hardware = Path.Combine(workDir, "intermediate-h264.mp4");
                var hardwareArgs = new List<string>
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "image2",
                    "-framerate", targetFps.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", Path.Combine(outputFramesDir, "%08d.png"),
                    "-c:v", "h264_mf", "-quality", "100", "-b:v", "20M",
                    "-pix_fmt", "yuv420p", "-an", hardware
                };
                if (!await RunProcessAsync(ffmpegFrames, hardwareArgs, line => { }, cancellationToken) ||
                    !File.Exists(hardware) || new FileInfo(hardware).Length == 0)
                    throw new InvalidOperationException("无损中间视频生成失败（VP9/FFmpeg 帧序列不可用）。");
                intermediate = hardware;
            }

            progress?.Invoke(90, "H.264 高质量编码…");
            var encodeArgs = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", intermediate
            };
            if (hasAudio && File.Exists(audioPath))
            {
                encodeArgs.Add("-i");
                encodeArgs.Add(audioPath);
            }
            encodeArgs.AddRange(new[] { "-map", "0:v:0" });
            if (hasAudio && File.Exists(audioPath))
                encodeArgs.AddRange(new[] { "-map", "1:a:0" });
            encodeArgs.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "slow", "-crf", "14", "-pix_fmt", "yuv420p"
            });
            if (hasAudio && File.Exists(audioPath))
                encodeArgs.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-shortest" });
            encodeArgs.AddRange(new[] { "-movflags", "+faststart", outputPath });
            try { File.Delete(outputPath); } catch { }
            if (!await RunProcessAsync(ffmpegEncode, encodeArgs, line => { }, cancellationToken) ||
                !File.Exists(outputPath) || new FileInfo(outputPath).Length < 1024)
                throw new InvalidOperationException("H.264 高质量编码失败。");

            progress?.Invoke(100, "AI 插帧完成");
            return new AiInterpolationResult(outputPath, sourceFps, targetFps,
                factor, started.Elapsed);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, IEnumerable<string> args,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        return await RunProcessAsync(ffmpeg, args, progress, cancellationToken);
    }

    private static async Task<bool> RunProcessAsync(string exe, IEnumerable<string> args,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
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
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                progress?.Invoke(e.Data);
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
