using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace Wallerpaper.Shell.Services;

public sealed record VideoToWebResult(string Folder, string IndexPath, string VideoPath);

/// <summary>
/// Wraps a video in a Web wallpaper without re-encoding it. The source file is
/// hard-linked when possible (same volume), otherwise copied byte-for-byte, so
/// the Web wallpaper never has lower quality than the original video.
/// </summary>
public static class VideoToWebConverter
{
    public static async Task<VideoToWebResult> ConvertAsync(
        string sourceVideo,
        string webRoot,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceVideo))
            throw new FileNotFoundException("视频不存在", sourceVideo);

        Directory.CreateDirectory(webRoot);
        var safe = Sanitize(title);
        var folder = Path.Combine(webRoot, $"{safe}-web-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);

        var extension = Path.GetExtension(sourceVideo);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".mp4";
        var targetVideoName = "video" + extension.ToLowerInvariant();
        var targetVideo = Path.Combine(folder, targetVideoName);

        var linked = false;
        if (string.Equals(Path.GetPathRoot(sourceVideo), Path.GetPathRoot(targetVideo),
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                linked = CreateHardLink(targetVideo, sourceVideo, IntPtr.Zero);
            }
            catch { }
        }

        if (!linked)
            File.Copy(sourceVideo, targetVideo, overwrite: true);

        // No ffmpeg, no bitrate, no scaling: the bytes are either the same
        // inode or an exact copy.
        if (new FileInfo(sourceVideo).Length != new FileInfo(targetVideo).Length)
            throw new IOException("视频复制长度不一致，已中止以避免画质下降。");

        var index = Path.Combine(folder, "index.html");
        var encodedVideo = Uri.EscapeDataString(targetVideoName);
        var html = $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{HtmlEncoder.Default.Encode(title)}}</title>
<style>
  html, body { margin:0; width:100%; height:100%; overflow:hidden; background:#000; }
  video { position:fixed; inset:0; width:100%; height:100%; object-fit:cover;
          background:#000; pointer-events:none; }
</style>
</head>
<body>
<video id="wp-video" src="{{encodedVideo}}" autoplay muted loop playsinline
       preload="auto" disablepictureinpicture></video>
<script>
  const video = document.getElementById('wp-video');
  const play = () => video.play().catch(() => {});
  video.addEventListener('loadeddata', play);
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) play();
  });
  play();
  document.addEventListener('contextmenu', e => e.preventDefault());
  document.addEventListener('dragstart', e => e.preventDefault());
</script>
</body>
</html>
""";
        await File.WriteAllTextAsync(index, html, new UTF8Encoding(false), cancellationToken);
        return new VideoToWebResult(folder, index, targetVideo);
    }

    public static async Task<VideoToWebResult> CreatePageAsync(
        string webRoot, string title, string html, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(webRoot);
        var safe = Sanitize(title);
        var folder = Path.Combine(webRoot, $"{safe}-web-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        var index = Path.Combine(folder, "index.html");
        await File.WriteAllTextAsync(index, html, new UTF8Encoding(false), cancellationToken);
        return new VideoToWebResult(folder, index, "");
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in value.Trim())
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        var result = sb.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(result) ? "video" : result[..Math.Min(result.Length, 80)];
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
