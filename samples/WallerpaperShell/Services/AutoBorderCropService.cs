using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Wallerpaper.Shell.Services;

public sealed record AutoBorderCropResult(
    string OutputPath,
    int Left,
    int Top,
    int Width,
    int Height,
    int SourceWidth,
    int SourceHeight);

public sealed record AutoCropCandidate(
    int Index,
    int Left,
    int Top,
    int Width,
    int Height,
    string ThumbnailPath);

/// <summary>
/// Finds the non-black picture rectangle across sampled frames and crops only
/// that rectangle. The visible picture is not resized or cut; the final encode
/// uses High-profile H.264 so Windows Media Foundation can preview it.
/// </summary>
public sealed class AutoBorderCropService
{
    public async Task<AutoBorderCropResult> RunAsync(
        string inputPath,
        Action<string>? progress = null,
        Func<IReadOnlyList<AutoCropCandidate>, Task<int?>>? chooseScene = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("视频不存在", inputPath);

        var ffmpegFrames = AiInterpolationService.FindFfmpegFrames()
            ?? throw new InvalidOperationException("未找到拆帧用 FFmpeg。");
        var ffmpegEncode = AiInterpolationService.FindFfmpegEncode()
            ?? throw new InvalidOperationException("未找到编码用 FFmpeg。");

        var work = Path.Combine(Path.GetTempPath(), "WallerpaperAutoCrop",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            progress?.Invoke("正在抽取检测帧…");
            var framePattern = Path.Combine(work, "f%04d.png");
            await Run(ffmpegFrames, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", inputPath,
                "-vf", "fps=2",
                framePattern
            }, cancellationToken);

            var frames = Directory.GetFiles(work, "f*.png").OrderBy(x => x).ToList();
            if (frames.Count == 0)
                throw new InvalidOperationException("无法抽取检测帧。");

            var cropInput = inputPath;
            var tilt = DetectTilt(frames[0]);
            if (Math.Abs(tilt) > 0.6)
            {
                progress?.Invoke($"检测到倾斜 {tilt:0.0}°，正在扶正…");
                var rotated = Path.Combine(work, "rotated.mkv");
                var radians = (-tilt * Math.PI / 180.0)
                    .ToString("0.######", CultureInfo.InvariantCulture);
                await Run(ffmpegFrames, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", inputPath,
                    "-vf", $"rotate={radians}:ow=rotw(iw):oh=roth(ih):fillcolor=black",
                    "-c:v", "libvpx-vp9", "-lossless", "1",
                    "-row-mt", "1", "-cpu-used", "4", "-an", rotated
                }, cancellationToken);

                var rotatedDir = Path.Combine(work, "rot");
                Directory.CreateDirectory(rotatedDir);
                await Run(ffmpegFrames, new[]
                {
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", rotated, "-vf", "fps=2",
                    Path.Combine(rotatedDir, "f%04d.png")
                }, cancellationToken);
                frames = Directory.GetFiles(rotatedDir, "f*.png")
                    .OrderBy(x => x).ToList();
                cropInput = rotated;
            }

            progress?.Invoke("正在分析黑边…");
            var candidates = DetectCandidates(frames, work);
            (int Left, int Top, int Width, int Height, int Sw, int Sh)? bounds = null;
            if (candidates.Count > 1 && chooseScene is not null)
            {
                progress?.Invoke("检测到多个画面，请选择要保留的场景…");
                int? chosen = await chooseScene(candidates);
                if (chosen is null)
                    throw new OperationCanceledException();
                var selected = candidates[Math.Clamp(chosen.Value, 0, candidates.Count - 1)];
                using var probe = new Bitmap(frames[0]);
                bounds = (selected.Left, selected.Top, selected.Width, selected.Height,
                    probe.Width, probe.Height);
            }
            else
            {
                bounds = Analyze(frames);
            }
            if (bounds is null)
                throw new InvalidOperationException("没有找到有效画面区域。");

            var (left, top, width, height, sourceWidth, sourceHeight) = bounds.Value;
            if (width >= sourceWidth - 4 && height >= sourceHeight - 4)
                throw new InvalidOperationException("没有检测到大面积黑边。");

            var outDir = AppStorage.CachePath("auto-crop");
            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var output = Path.Combine(outDir,
                $"{stem}_noborder_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            var intermediate = Path.Combine(work, "lossless.mkv");

            progress?.Invoke("正在无损裁剪画面…");
            await Run(ffmpegFrames, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", cropInput,
                "-vf", $"crop={width}:{height}:{left}:{top}",
                "-c:v", "libvpx-vp9", "-lossless", "1",
                "-row-mt", "1", "-cpu-used", "4", "-an", intermediate
            }, cancellationToken);

            progress?.Invoke("正在生成无损壁纸…");
            await Run(ffmpegEncode, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", intermediate,
                "-i", inputPath,
                "-map", "0:v:0", "-map", "1:a?",
                // Lossless x264 uses High 4:4:4 Predictive, which Windows
                // Media Foundation cannot decode (detail preview turns black).
                // CRF 12 High profile is visually lossless and compatible.
                "-c:v", "libx264", "-preset", "slow", "-crf", "12",
                "-profile:v", "high", "-level", "4.1",
                "-pix_fmt", "yuv420p",
                "-c:a", "copy", "-movflags", "+faststart",
                output
            }, cancellationToken);

            progress?.Invoke("完成");
            return new AutoBorderCropResult(output, left, top, width, height,
                sourceWidth, sourceHeight);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>
    /// Estimates the tilt of the non-black picture by histogramming the
    /// gradient normals of the black/content boundary. A rectangle's four
    /// sides collapse to one angle modulo 90 degrees.
    /// </summary>
    private static double DetectTilt(string framePath)
    {
        try
        {
            using var bitmap = new Bitmap(framePath);
            var width = bitmap.Width;
            var height = bitmap.Height;
            var rect = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            var mask = new bool[width * height];
            try
            {
                var pixels = new int[width * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                for (var i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    var red = (c >> 16) & 0xFF;
                    var green = (c >> 8) & 0xFF;
                    var blue = c & 0xFF;
                    mask[i] = Math.Max(red, Math.Max(green, blue)) > 24;
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            // 0.5-degree bins over one quadrant.
            var bins = new int[180];
            var total = 0;
            for (var y = 2; y < height - 2; y += 2)
            {
                for (var x = 2; x < width - 2; x += 2)
                {
                    var center = mask[y * width + x];
                    if (center == mask[y * width + x - 1] &&
                        center == mask[y * width + x + 1] &&
                        center == mask[(y - 1) * width + x] &&
                        center == mask[(y + 1) * width + x])
                        continue;

                    var gx = (mask[y * width + x + 1] ? 1 : 0) -
                             (mask[y * width + x - 1] ? 1 : 0);
                    var gy = (mask[(y + 1) * width + x] ? 1 : 0) -
                             (mask[(y - 1) * width + x] ? 1 : 0);
                    if (gx == 0 && gy == 0) continue;
                    var angle = Math.Atan2(gy, gx) * 180.0 / Math.PI;
                    if (angle < 0) angle += 180;
                    angle %= 90;
                    var bin = Math.Clamp((int)Math.Round(angle * 2), 0, 179);
                    bins[bin]++;
                    total++;
                }
            }

            if (total < 100) return 0;
            var peak = 0;
            for (var i = 1; i < bins.Length; i++)
                if (bins[i] > bins[peak]) peak = i;
            if (bins[peak] < total / 12) return 0;

            var tilt = peak / 2.0;
            if (tilt > 45) tilt -= 90;
            return Math.Abs(tilt) < 0.6 ? 0 : tilt;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Split-screen videos contain two detached picture areas separated by a
    /// large black band. Return those regions so the user can choose one
    /// instead of keeping the black gap.
    /// </summary>
    private static List<AutoCropCandidate> DetectCandidates(
        IReadOnlyList<string> frames, string work)
    {
        var result = new List<AutoCropCandidate>();
        if (frames.Count == 0) return result;
        using var bitmap = new Bitmap(frames[0]);
        var width = bitmap.Width;
        var height = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        bool[] mask = new bool[width * height];
        try
        {
            var pixels = new int[width * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                var red = (c >> 16) & 0xFF;
                var green = (c >> 8) & 0xFF;
                var blue = c & 0xFF;
                mask[i] = Math.Max(red, Math.Max(green, blue)) > 24;
            }
        }
        finally { bitmap.UnlockBits(data); }

        var columnHas = new bool[width];
        var minPixels = Math.Max(3, height / 40);
        for (var x = 0; x < width; x++)
        {
            var count = 0;
            for (var y = 0; y < height; y++)
                if (mask[y * width + x]) count++;
            columnHas[x] = count >= minPixels;
        }

        var runs = new List<(int Start, int End)>();
        var inRun = false;
        var start = 0;
        for (var x = 0; x < width; x++)
        {
            if (columnHas[x] && !inRun) { inRun = true; start = x; }
            else if (!columnHas[x] && inRun)
            {
                inRun = false;
                if (x - start > width / 20) runs.Add((start, x));
            }
        }
        if (inRun && width - start > width / 20) runs.Add((start, width));
        if (runs.Count < 2) return result;

        // Require a real black gap between the two largest runs.
        for (var i = 0; i < runs.Count; i++)
        {
            var (x0, x1) = runs[i];
            var top = height;
            var bottom = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    if (!mask[y * width + x]) continue;
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y + 1);
                    break;
                }
            }
            if (bottom - top < height / 5) continue;
            if ((x1 - x0) * (bottom - top) < width * height / 20) continue;

            var left = Math.Max(0, x0 - (x0 & 1));
            var topEven = Math.Max(0, top - (top & 1));
            var right = Math.Min(width, x1 + (x1 & 1));
            var bottomEven = Math.Min(height, bottom + (bottom & 1));
            var thumb = Path.Combine(work, $"candidate-{result.Count}.png");
            try
            {
                using var full = new Bitmap(frames[0]);
                using var crop = full.Clone(new Rectangle(left, topEven,
                    right - left, bottomEven - topEven), PixelFormat.Format24bppRgb);
                crop.Save(thumb, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch
            {
                thumb = frames[0];
            }
            result.Add(new AutoCropCandidate(result.Count, left, topEven,
                right - left, bottomEven - topEven, thumb));
        }
        return result;
    }

    private static (int Left, int Top, int Width, int Height, int Sw, int Sh)? Analyze(
        IReadOnlyList<string> frames)
    {
        // Cap the number of frames so a long 4K clip does not allocate a huge
        // hit-map for minutes of video.
        var sampled = frames.Count <= 60
            ? frames
            : frames.Where((_, index) => index % Math.Max(1, frames.Count / 60) == 0)
                .Take(60).ToList();
        var width = 0;
        var height = 0;
        ushort[]? hits = null;

        foreach (var file in sampled)
        {
            using var bitmap = new Bitmap(file);
            if (width == 0)
            {
                width = bitmap.Width;
                height = bitmap.Height;
                hits = new ushort[width * height];
            }
            if (bitmap.Width != width || bitmap.Height != height) continue;

            var rect = new Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new int[width * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                for (var i = 0; i < pixels.Length; i++)
                {
                    var c = pixels[i];
                    var red = (c >> 16) & 0xFF;
                    var green = (c >> 8) & 0xFF;
                    var blue = c & 0xFF;
                    if (Math.Max(red, Math.Max(green, blue)) > 24 && hits[i] < ushort.MaxValue)
                        hits[i]++;
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        if (width < 2 || height < 2 || hits is null) return null;

        // Use the outer content bounds, not the largest inner rectangle.
        // The inner-rectangle approach zoomed normal letterboxed videos and
        // threw away valid left/right picture content.
        var threshold = Math.Max(2, sampled.Count / 10);
        var rowHits = new int[height];
        var colHits = new int[width];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (hits[row + x] < threshold) continue;
                rowHits[y]++;
                colHits[x]++;
            }
        }

        // A black bar can contain a few stray colored pixels (compression
        // noise, particles). Require a real content fraction on the edge,
        // otherwise the outer bbox expands back to the full frame and the
        // crop appears to do nothing.
        var minCol = Math.Max(3, height / 20);
        var minRow = Math.Max(3, width / 20);
        var xs = Enumerable.Range(0, width).Where(x => colHits[x] >= minCol).ToList();
        var ys = Enumerable.Range(0, height).Where(y => rowHits[y] >= minRow).ToList();
        if (xs.Count == 0 || ys.Count == 0) return null;

        var left = xs[0];
        var right = xs[^1] + 1;
        var top = ys[0];
        var bottom = ys[^1] + 1;
        var area = (right - left) * (bottom - top);
        // A tiny rectangle is more likely a bright inset/object than the main
        // picture. Reject it instead of exporting a low-resolution crop.
        // A single small picture inside a black frame is still a valid
        // wallpaper source (CH0287 is one). Do not reject it just because the
        // frame is mostly black; multi-panel sources are handled by the scene
        // chooser before this point.
        if (area < width * height / 50)
            return null;

        // H.264 needs even dimensions. Expand outward, never inward, so the
        // colored picture remains inside the rectangle.
        left = Math.Max(0, left - (left & 1));
        top = Math.Max(0, top - (top & 1));
        right = Math.Min(width, right + (right & 1));
        bottom = Math.Min(height, bottom + (bottom & 1));
        return (left, top, right - left, bottom - top, width, height);
    }

    private static async Task Run(string exe, IEnumerable<string> args,
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
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) stderr.AppendLine(e.Data);
        };
        process.Start();
        process.BeginErrorReadLine();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromHours(2));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg 失败（{Path.GetFileName(exe)}）：{stderr}");
    }
}
