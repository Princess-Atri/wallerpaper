using System.Diagnostics;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Converts raw percentage reports into a human-readable ETA. The estimate
/// uses the elapsed time since a stage started, so it adapts to machine
/// speed instead of using a fixed table.
/// </summary>
public sealed class ProgressEta
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _lastPercent = -1;
    private TimeSpan _lastChange = TimeSpan.Zero;

    /// <summary>Formats "message · 已完成 42% · 预计剩余 3 分 20 秒".</summary>
    public string Format(int percent, string message)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var elapsed = _watch.Elapsed;

        // Only recompute when the percentage really moved; otherwise an ETA
        // would jitter on every repaint.
        if (Math.Abs(clamped - _lastPercent) >= 1)
        {
            if (_lastPercent >= 0 && clamped > _lastPercent)
                _lastChange = elapsed;
            _lastPercent = clamped;
        }

        if (clamped >= 100) return message + " · 已完成";
        if (clamped <= 0) return message;

        var baseElapsed = _lastChange > TimeSpan.Zero ? _lastChange : elapsed;
        if (baseElapsed.TotalSeconds < 1.5)
            return $"{message} · {clamped}% · 估算中…";

        var remaining = TimeSpan.FromSeconds(
            baseElapsed.TotalSeconds * (100 - clamped) / Math.Max(1, clamped));
        if (remaining.TotalSeconds > 86400) remaining = TimeSpan.FromHours(24);
        return $"{message} · {clamped}% · 预计剩余 {Humanize(remaining)}";
    }

    public static string Humanize(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
            return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
    }
}
