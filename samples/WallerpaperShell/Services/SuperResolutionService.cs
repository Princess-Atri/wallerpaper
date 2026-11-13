using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;

namespace Wallerpaper.Shell.Services;

public sealed record SuperResolutionResult(
    string OutputPath,
    int Factor,
    string Model,
    TimeSpan Elapsed);

/// <summary>
/// Offline Real-ESRGAN video upscaling. Frames are extracted losslessly,
/// upscaled by the Vulkan ncnn tool, then encoded through the same
/// VP9-lossless -> libx264 path used by AI interpolation.
/// </summary>
public sealed class SuperResolutionService
{
    public static string ToolRoot
    {
        get
        {
            var requested = @"D:\Desktop\wallerpaper c\REAL-ERGAN";
            if (Directory.Exists(requested))
                return requested;
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wallerpaper", "ai-tools");
            if (Directory.Exists(local))
                return local;
            return @"D:\Desktop\wallerpaper c\tools\ai";
        }
    }

    public static string? FindRealEsrgan()
    {
        foreach (var root in CandidateRoots())
        {
            var hit = Directory.EnumerateFiles(root, "realesrgan-ncnn-vulkan.exe",
                SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null)
                return hit;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var root in new[]
                 {
                     @"D:\Desktop\wallerpaper c\REAL-ERGAN",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Wallerpaper", "ai-tools"),
                     Path.Combine(AppContext.BaseDirectory, "ai-tools"),
                     @"D:\Desktop\wallerpaper c\tools\ai"
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(root))
                yield return root;
        }
    }

    public async Task<SuperResolutionResult> UpscaleAsync(
        string inputPath,
        double sourceFps,
        int factor,
        string model,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("视频不存在", inputPath);
        if (factor is not (2 or 4))
            throw new ArgumentOutOfRangeException(nameof(factor), "factor must be 2 or 4");
        if (sourceFps <= 0.1)
            sourceFps = 30;

        var esrgan = FindRealEsrgan() ?? throw new InvalidOperationException(
            "未找到 realesrgan-ncnn-vulkan.exe。请放到 D:\\Desktop\\wallerpaper c\\REAL-ERGAN。");
        var ffmpegFrames = AiInterpolationService.FindFfmpegFrames()
            ?? throw new InvalidOperationException("未找到拆帧用 FFmpeg。");
        var ffmpegEncode = AiInterpolationService.FindFfmpegEncode()
            ?? throw new InvalidOperationException("未找到 H.264 编码用 FFmpeg。");
        model = string.IsNullOrWhiteSpace(model) ? "realesr-animevideov3" : model;
        if (model is "realesrgan-x4plus" or "realesrgan-x4plus-anime" && factor != 4)
            factor = 4;
        var modelDir = Path.Combine(Path.GetDirectoryName(esrgan)!, "models");
        // realesr-animevideov3 ships as -x2/-x3/-x4 files, while
        // realesrgan-x4plus ships without a scale suffix.
        var modelStem = model;
        if (!File.Exists(Path.Combine(modelDir, modelStem + ".param")) ||
            !File.Exists(Path.Combine(modelDir, modelStem + ".bin")))
            modelStem = $"{model}-x{factor}";
        if (!File.Exists(Path.Combine(modelDir, modelStem + ".param")) ||
            !File.Exists(Path.Combine(modelDir, modelStem + ".bin")))
            throw new InvalidOperationException($"Real-ESRGAN 模型缺失：{modelStem}");

        var started = Stopwatch.StartNew();
        // Real-ESRGAN's WIC PNG writer fails on non-ASCII paths (for example
        // D:\Desktop\记忆dating) and on the C: temp tree. Prefer an ASCII
        // workspace on D: even when the source itself lives under C:\Users.
        var workRoot = @"D:\Desktop\wallerpaper c\cache\sr-work";
        try
        {
            Directory.CreateDirectory(workRoot);
        }
        catch
        {
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(inputPath));
            workRoot = string.IsNullOrWhiteSpace(driveRoot)
                ? Path.GetTempPath()
                : Path.Combine(driveRoot, "WallerpaperSR");
            Directory.CreateDirectory(workRoot);
        }
        var work = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
        var frames = Path.Combine(work, "frames");
        var upscaled = Path.Combine(work, "up");
        var audio = Path.Combine(work, "audio.m4a");
        Directory.CreateDirectory(frames);
        Directory.CreateDirectory(upscaled);

        var outputDir = AppStorage.CachePath("superres");
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var output = Path.Combine(outputDir,
            $"{stem}_SR{factor}x_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

        try
        {
            progress?.Invoke(3, "提取音频…");
            var hasAudio = await Run(ffmpegFrames, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", inputPath, "-vn", "-c:a", "aac", "-b:a", "192k", audio
            }, cancellationToken);

            progress?.Invoke(8, "拆分视频帧…");
            var extract = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", inputPath, "-map", "0:v:0", "-vsync", "0"
            };
            if (sourceFps > 0.1)
            {
                extract.Add("-vf");
                extract.Add($"fps={sourceFps.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            extract.Add("-f");
            extract.Add("image2");
            extract.Add(Path.Combine(frames, "%08d.png"));
            if (!await Run(ffmpegFrames, extract, cancellationToken))
                throw new InvalidOperationException("FFmpeg 拆帧失败。");

            var inputFrames = Directory.EnumerateFiles(frames, "*.png").Count();
            if (inputFrames < 2)
                throw new InvalidOperationException("视频帧数不足，无法超分辨率。");

            var firstFrame = Directory.EnumerateFiles(frames, "*.png").First();
            using (var image = Image.FromFile(firstFrame))
            {
                var outWidth = image.Width * factor;
                var outHeight = image.Height * factor;
                if (outWidth > 8192 || outHeight > 8192)
                    throw new InvalidOperationException(
                        $"输出尺寸 {outWidth}×{outHeight} 超出 Real-ESRGAN/显存安全范围。" +
                        "请改用 2×，或先降低源视频分辨率。");
            }

            progress?.Invoke(20, $"Real-ESRGAN 超分辨率中：{inputFrames} 帧 → {factor}×…");
            var esrganArgs = new List<string>
            {
                "-i", frames,
                "-o", upscaled,
                "-n", model,
                "-s", factor.ToString(CultureInfo.InvariantCulture),
                "-f", "png"
            };
            if (!await Run(esrgan, esrganArgs, cancellationToken, line =>
                {
                    var percent = ParsePercent(line);
                    if (percent >= 0)
                        progress?.Invoke(20 + (int)(percent * 0.55), $"Real-ESRGAN {factor}×：{percent:0}%");
                }))
                throw new InvalidOperationException(
                    "Real-ESRGAN 失败，请检查 Vulkan 驱动、显存和模型文件。");

            var outputFrames = Directory.EnumerateFiles(upscaled, "*.png").Count();
            if (outputFrames < 2)
                throw new InvalidOperationException("Real-ESRGAN 没有输出帧。");

            progress?.Invoke(78, "写入无损中间视频…");
            var intermediate = Path.Combine(work, "lossless.mkv");
            var firstUpscaled = Directory.EnumerateFiles(upscaled, "*.png").First();
            var capPlayback = false;
            using (var image = Image.FromFile(firstUpscaled))
                capPlayback = image.Width > 3840 || image.Height > 2160;
            var losslessOk = false;
            foreach (var pixelFormat in new[] { "yuv444p", "yuv420p" })
            {
                try { if (File.Exists(intermediate)) File.Delete(intermediate); } catch { }
                var vp9 = new List<string>
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "image2",
                    "-framerate", sourceFps.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", Path.Combine(upscaled, "%08d.png")
                };
                if (capPlayback)
                {
                    // 5K/8K textures make desktop video startup slow and can make
                    // D3D11VA initialization flaky. 4K is still a real 1.5×-2×
                    // increase over common 1440p sources and starts reliably.
                    vp9.Add("-vf");
                    vp9.Add("scale=3840:2160:force_original_aspect_ratio=decrease");
                }
                vp9.AddRange(new[]
                {
                    "-c:v", "libvpx-vp9", "-lossless", "1", "-pix_fmt", pixelFormat, "-an",
                    intermediate
                });
                losslessOk = await Run(ffmpegFrames, vp9, cancellationToken) &&
                             File.Exists(intermediate) &&
                             new FileInfo(intermediate).Length > 0;
                if (losslessOk) break;
            }
            if (!losslessOk)
            {
                // Same compatibility fallback as AI interpolation: some
                // FFmpeg/VP9 builds reject lossless upload of 4K/5K frames.
                progress?.Invoke(82, "无损中间层不可用，改用高质量 H.264 中间层…");
                var hardware = Path.Combine(work, "intermediate-h264.mp4");
                var hw = new List<string>
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-f", "image2",
                    "-framerate", sourceFps.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", Path.Combine(upscaled, "%08d.png")
                };
                if (capPlayback)
                {
                    hw.Add("-vf");
                    hw.Add("scale=3840:2160:force_original_aspect_ratio=decrease");
                }
                hw.AddRange(new[]
                {
                    "-c:v", "h264_mf", "-quality", "100", "-b:v", "24M",
                    "-pix_fmt", "yuv420p", "-an", hardware
                });
                if (!await Run(ffmpegFrames, hw, cancellationToken) ||
                    !File.Exists(hardware) || new FileInfo(hardware).Length == 0)
                    throw new InvalidOperationException("无损中间视频生成失败（VP9/FFmpeg 帧序列不可用）。");
                intermediate = hardware;
            }

            progress?.Invoke(90, "H.264 高质量编码…");
            var encode = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", intermediate
            };
            if (hasAudio && File.Exists(audio))
            {
                encode.Add("-i");
                encode.Add(audio);
            }
            encode.AddRange(new[] { "-map", "0:v:0" });
            if (hasAudio && File.Exists(audio))
                encode.AddRange(new[] { "-map", "1:a:0" });
            encode.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "medium", "-crf", "14", "-pix_fmt", "yuv420p"
            });
            if (hasAudio && File.Exists(audio))
                encode.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-shortest" });
            encode.AddRange(new[] { "-movflags", "+faststart", output });
            try { File.Delete(output); } catch { }
            if (!await Run(ffmpegEncode, encode, cancellationToken) ||
                !File.Exists(output) || new FileInfo(output).Length < 1024)
                throw new InvalidOperationException("H.264 编码失败。");

            progress?.Invoke(100, "超分辨率完成");
            return new SuperResolutionResult(output, factor, model, started.Elapsed);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static double ParsePercent(string line)
    {
        var text = line.Trim().TrimEnd('%');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100)
            : -1;
    }

    private static async Task<bool> Run(string exe, IEnumerable<string> args,
        CancellationToken cancellationToken, Action<string>? onLine = null)
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
            {
                stderr.AppendLine(e.Data);
                onLine?.Invoke(e.Data);
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                onLine?.Invoke(e.Data);
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(3));
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
