using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using QuickJS;

namespace SceneWallpaperHost;

internal sealed class ScriptOp
{
    public string Op { get; set; } = "";
    public string Layer { get; set; } = "main";
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? Value { get; set; }
}

/// <summary>
/// QuickJS binding. This intentionally uses a small JS prelude and batches
/// layer mutations as JSON ops per frame. It avoids native callbacks into the
/// render thread and keeps the host scriptable without exposing .NET objects.
/// </summary>
internal sealed class SceneScript : IDisposable
{
    private const string Prelude = """
        const __ops = [];
        function __makeLayer(name) {
            return {
                name: name,
                setPosition: function (x, y) { __ops.push({ op: 'position', layer: name, x: x, y: y }); },
                setOpacity: function (v) { __ops.push({ op: 'opacity', layer: name, value: v }); },
                setScale: function (x, y) { __ops.push({ op: 'scale', layer: name, x: x, y: y }); },
                setRotation: function (r) { __ops.push({ op: 'rotation', layer: name, value: r }); },
                setParticleCount: function (n) { __ops.push({ op: 'particleCount', layer: name, value: n }); },
                setParticleSpeed: function (v) { __ops.push({ op: 'particleSpeed', layer: name, value: v }); }
            };
        }
        const time = { time: 0, deltaTime: 0 };
        const input = { mouseX: 0, mouseY: 0, mouseDown: false };
        const audio = { level: 0, bass: 0, mid: 0, treble: 0, bins: [] };
        const layer = __makeLayer('main');
        function getLayer(name) { return __makeLayer(name); }
        function update(dt) { }
        """;

    private readonly Action<string> _log;
    private readonly QuickJSRuntime? _runtime;
    private readonly QuickJSContext? _context;
    private bool _disposed;
    private bool _failed;
    private long _deadlineTicks = long.MaxValue;
    private const int MaxOpsPerFrame = 256;
    private const int ScriptFrameBudgetMs = 50;

    public SceneScript(string? scriptPath, Action<string> log)
    {
        _log = log;

        try
        {
            _runtime = new QuickJSRuntime();
            _runtime.Interrupt += OnInterrupt;
            _context = _runtime.CreateContext();

            _deadlineTicks = Environment.TickCount64 + 2000;
            _context.Eval(Prelude, "prelude.js", JSEvalFlags.Global);

            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                var code = File.ReadAllText(scriptPath);
                _context.Eval(code, Path.GetFileName(scriptPath), JSEvalFlags.Global);
                _log($"QuickJS script loaded: {scriptPath}");
            }
            _deadlineTicks = long.MaxValue;
        }
        catch (Exception ex)
        {
            _log($"QuickJS init failed: {ex.Message}");
            _failed = true;
            Dispose();
        }
    }

    public bool Available => !_failed && _context is not null;

    private void OnInterrupt(object? sender, HandledEventArgs e)
    {
        if (Environment.TickCount64 > _deadlineTicks)
        {
            e.Handled = true;
            _log("QuickJS frame budget exceeded; interrupting script");
        }
    }

    public IReadOnlyList<ScriptOp> Update(
        double time,
        double dt,
        double mouseX,
        double mouseY,
        AudioFrame audio)
    {
        if (!Available || _disposed)
            return Array.Empty<ScriptOp>();

        try
        {
            var bins = string.Join(",", audio.Bins.Take(32).Select(b => b.ToString("F4", CultureInfo.InvariantCulture)));
            var code = string.Format(
                CultureInfo.InvariantCulture,
                "time.time={0:F6}; time.deltaTime={1:F6}; " +
                "input.mouseX={2:F2}; input.mouseY={3:F2}; " +
                "audio.level={4:F4}; audio.bass={5:F4}; " +
                "audio.mid={6:F4}; audio.treble={7:F4}; " +
                "audio.bins=[{8}]; " +
                "if (typeof update === 'function') update(time.deltaTime); " +
                "JSON.stringify(__ops.splice(0, __ops.length));",
                time, dt, mouseX, mouseY, audio.Level, audio.Bass, audio.Mid, audio.Treble, bins);

            _deadlineTicks = Environment.TickCount64 + ScriptFrameBudgetMs;
            try
            {
                var result = _context!.Eval(code, "frame.js", JSEvalFlags.Global);
                if (result is not string json || json.Length == 0)
                    return Array.Empty<ScriptOp>();

                var ops = JsonSerializer.Deserialize<List<ScriptOp>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (ops is null)
                    return Array.Empty<ScriptOp>();

                // Never let a runaway script flood the renderer with layer ops.
                if (ops.Count > MaxOpsPerFrame)
                {
                    _log($"QuickJS ops capped: {ops.Count} -> {MaxOpsPerFrame}");
                    return ops.Take(MaxOpsPerFrame).ToList();
                }
                return ops;
            }
            finally
            {
                _deadlineTicks = long.MaxValue;
            }
        }
        catch (Exception ex)
        {
            _log($"QuickJS update failed (script disabled): {ex.Message}");
            _failed = true;
            return Array.Empty<ScriptOp>();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _context?.Dispose();
        _runtime?.Dispose();
    }
}

internal sealed class AudioFrame
{
    public double Level { get; set; }
    public double Bass { get; set; }
    public double Mid { get; set; }
    public double Treble { get; set; }
    public long SentAtUnixMs { get; set; }
    public double[] Bins { get; set; } = new double[32];
}
