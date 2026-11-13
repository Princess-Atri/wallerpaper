using System.Diagnostics;

namespace Wallerpaper.Shell.Services;

public static class FrameRateProbe
{
    public static async Task<double> ProbeAsync(string videoHostExe, string videoPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoHostExe) || !File.Exists(videoPath))
            return 0;

        var psi = new ProcessStartInfo
        {
            FileName = videoHostExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(videoHostExe)!
        };
        psi.ArgumentList.Add("--probe");
        psi.ArgumentList.Add("--video");
        psi.ArgumentList.Add(videoPath);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return 0;
        }

        var text = await stdout;
        var error = await stderr;
        foreach (var line in (text + "\n" + error).Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("FPS=", StringComparison.OrdinalIgnoreCase))
                continue;
            if (double.TryParse(trimmed[4..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var fps))
                return fps;
        }
        return 0;
    }
}
