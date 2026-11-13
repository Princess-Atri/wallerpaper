using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VideoWallpaperHost;

internal enum MpvErrorKind
{
    Init,
    HardwareDecode,
    Load,
    Command
}

internal sealed class MpvException : Exception
{
    public MpvErrorKind Kind { get; }
    public int Code { get; }

    public MpvException(MpvErrorKind kind, int code, string message) : base(message)
    {
        Kind = kind;
        Code = code;
    }
}

/// <summary>
/// Minimal libmpv client wrapper for one video wallpaper.
/// Native DLL: libmpv-2.dll (no NuGet wrapper; direct P/Invoke).
/// </summary>
internal sealed class LibMpv : IDisposable
{
    private const string Dll = "libmpv-2.dll";

    private const int MPV_FORMAT_INT64 = 4;
    private const int MPV_FORMAT_DOUBLE = 5;

    private const int MPV_EVENT_SHUTDOWN = 1;
    private const int MPV_EVENT_LOG_MESSAGE = 2;
    private const int MPV_EVENT_END_FILE = 7;
    private const int MPV_EVENT_FILE_LOADED = 8;
    private const int MPV_EVENT_PLAYBACK_RESTART = 21;

    private readonly Action<string> _log;
    private readonly Action _pump;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _fileLoaded = new(false);
    private readonly CancellationTokenSource _cts = new();

    private IntPtr _ctx;
    private Thread? _eventThread;
    private int _fileError;
    private string? _lastError;
    private bool _released;
    private double _resumePosition;
    private bool _fpsCapActive;
    private bool _disposed;
    private string? _nextFile;
    private bool _sequenceSwitched;
    private Timer? _fadeTimer;
    private Timer? _sequenceTimer;

    public LibMpv(Action<string> log, Action pump)
    {
        _log = log;
        _pump = pump;
    }

    public string? HwdecCurrent { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Duration { get; private set; }
    public double ContainerFps { get; private set; }
    public bool IsPaused { get; private set; }
    public bool DecoderReleased => _released;
    public bool FpsCapActive => _fpsCapActive;

    public static double ProbeSourceFps(string path)
    {
        using var probe = new LibMpv(_ => { }, () => { });
        return probe.ProbeSourceFpsCore(path);
    }

    private double ProbeSourceFpsCore(string path)
    {
        _ctx = Native.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new MpvException(MpvErrorKind.Init, -1, "mpv_create returned NULL");
        SetOption("vo", "null");
        SetOption("audio", "no");
        SetOption("hwdec", "no");
        SetOption("pause", "yes");
        SetOption("config", "no");
        Check(Native.mpv_initialize(_ctx), MpvErrorKind.Init, "mpv_initialize (probe)");
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "libmpv-probe" };
        _eventThread.Start();
        LoadFile(path);
        return GetDouble("container-fps") ?? 0;
    }

    public double SourceFps
    {
        get
        {
            // container-fps is the source rate. estimated-vf-fps is the
            // post-filter output rate (usually 60), so never use it as the
            // source value while the fps filter is active. VFR containers can
            // legitimately report 0 here; the UI then says "unknown/VFR".
            return GetDouble("container-fps") ?? 0;
        }
    }

    public void Initialize(
        string videoPath,
        IntPtr videoWindow,
        int targetFps,
        int volume,
        string hwdec,
        string fit,
        bool requireHardware,
        string? nextFile = null,
        int targetWidth = 0,
        int targetHeight = 0,
        double brightness = 0,
        double contrast = 0,
        double highlights = 0,
        double hue = 0,
        double temperature = 0,
        double exposure = 0,
        double sharpness = 0)
    {
        _nextFile = nextFile;
        if (!File.Exists(videoPath))
            throw new MpvException(MpvErrorKind.Load, -2, $"Video file not found: {videoPath}");

        var apiVersion = Native.mpv_client_api_version();
        _log($"libmpv client API: {(apiVersion >> 16) & 0xFFFF}.{apiVersion & 0xFFFF}");

        _ctx = Native.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new MpvException(MpvErrorKind.Init, -1, "mpv_create returned NULL");

        Check(Native.mpv_request_log_messages(_ctx, "info"),
            MpvErrorKind.Init, "mpv_request_log_messages");

        // ---- key video options (before mpv_initialize) --------------------
        // wid: embed mpv's video output into our WorkerW child window.
        SetOption("wid", videoWindow.ToInt64().ToString(CultureInfo.InvariantCulture));
        // Direct3D 11 backend. Windows 8+ preferred.
        SetOption("vo", "gpu");
        SetOption("gpu-api", "d3d11");
        // Force hardware decoding: d3d11va first, dxva2 is the fallback chosen by host.
        var blurFill = string.Equals(fit, "blur", StringComparison.OrdinalIgnoreCase);
        SetOption("hwdec", blurFill ? "no" : hwdec);
        SetOption("hwdec-codecs", "all");
        if (blurFill)
        {
            // Blur-fill is a real second layer inside mpv: background is
            // upscaled/cropped/blurred, foreground is contained and overlaid.
            // This requires software filtering, so normal direct-wid playback
            // keeps its hardware decoder untouched.
            var fw = Math.Max(2, targetWidth > 0 ? targetWidth : 1920);
            var fh = Math.Max(2, targetHeight > 0 ? targetHeight : 1080);
            var graph =
                "split=2[bg][fg];" +
                $"[bg]scale={fw}:{fh}:force_original_aspect_ratio=increase:flags=lanczos," +
                $"crop={fw}:{fh},boxblur=20:2[b];" +
                $"[fg]scale={fw}:{fh}:force_original_aspect_ratio=decrease:flags=lanczos[f];" +
                "[b][f]overlay=(W-w)/2:(H-h)/2:shortest=1";
            TrySetOption("vf", "lavfi=[" + graph + "]");
            _log("blur-fill filter enabled (lavfi, software decode)");
        }
        // A normal wallpaper loops forever. A sequence plays the first file
        // once, then switches to the second and loops that one forever.
        SetOption("loop-file", _nextFile is null ? "inf" : "no");
        SetOption("loop", _nextFile is null ? "inf" : "no");
        SetOption("keep-open", "always");
        SetOption("idle", "yes");
        TrySetOption("force-window", "yes");
        TrySetOption("hr-seek", "yes");
        // Do not load user config/scripts/OSD/input bindings.
        // Some libmpv builds do not expose OSD/terminal/OSC options; treat these as optional.
        TrySetOption("config", "no");
        TrySetOption("load-scripts", "no");
        TrySetOption("osc", "no");
        TrySetOption("input-default-bindings", "no");
        TrySetOption("input-cursor", "no");
        TrySetOption("terminal", "no");
        TrySetOption("audio-client-name", "Wallerpaper");
        TrySetOption("stop-screensaver", "no");
        TrySetOption("ytdl", "no");
        // Local-file memory limits. Disable the demux cache where supported;
        // otherwise keep a small cache so a looping 1080p file cannot grow it
        // to 20+ MiB over a long run.
        TrySetOption("cache", "no");
        TrySetOption("demuxer-max-bytes", "8388608");   // 8 MiB
        TrySetOption("demuxer-max-back-bytes", "2097152"); // 2 MiB
        TrySetOption("demuxer-readahead-secs", "0.5");
        TrySetOption("vd-lavc-threads", "4");
        // Keep a small but safe D3D11 surface pool. The default six extra
        // frames is a large VRAM cost at 4K; four still leaves headroom for
        // decode jitter.
        TrySetOption("hwdec-extra-frames", "4");
        TrySetOption("audio-buffer", "0.2");
        // Aspect-preserving cover: crop overflow, never stretch.
        SetOption("keepaspect", blurFill ? "no" : "yes");
        SetOption("video-unscaled", "no");
        SetOption("panscan", blurFill || string.Equals(fit, "contain", StringComparison.OrdinalIgnoreCase)
            ? "0.0" : "1.0");
        // Per-wallpaper picture adjustment. mpv's equalizer properties are
        // live and cost nothing when left at zero.
        static string ColorValue(double value) =>
            Math.Clamp(value, -100, 100).ToString("0.###", CultureInfo.InvariantCulture);
        SetOption("brightness", ColorValue(brightness + exposure * 0.5));
        SetOption("contrast", ColorValue(contrast + highlights * 0.25));
        SetOption("gamma", ColorValue(highlights * 0.6));
        SetOption("hue", ColorValue(hue + temperature * 0.35));
        SetOption("saturation", ColorValue(temperature * 0.3));
        if (Math.Abs(sharpness) > 0.01)
            TrySetOption("sharpen", (sharpness / 100.0 * 1.5)
                .ToString("0.###", CultureInfo.InvariantCulture));
        // Quality-first scaling. Bilinear softened 1440p/4K content visibly;
        // Lanczos keeps line art and text sharp when the wallpaper is scaled.
        SetOption("scale", "lanczos");
        SetOption("cscale", "lanczos");
        SetOption("dscale", "lanczos");
        TrySetOption("scale-antiring", "0.7");
        TrySetOption("correct-downscaling", "yes");
        // Prefer display-vdrop + display-fps for a 30/60 cap on mpv builds where
        // max-fps and the fps video filter are unavailable.
        var displayFps = TrySetOption("display-fps", targetFps.ToString(CultureInfo.InvariantCulture));
        var maxFps = TrySetOption("max-fps", targetFps.ToString(CultureInfo.InvariantCulture));
        _fpsCapActive = displayFps || maxFps;
        if (!TrySetOption("video-sync", "display-vdrop"))
            TrySetOption("video-sync", "audio");
        TrySetOption("correct-downscaling", "no");

        Check(Native.mpv_initialize(_ctx), MpvErrorKind.Init, "mpv_initialize");

        SetProperty("volume", volume.ToString(CultureInfo.InvariantCulture));
        SetProperty("pause", "yes");

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "libmpv-events"
        };
        _eventThread.Start();

        LoadFile(videoPath);
        SetVolume(volume);

        HwdecCurrent = GetString("hwdec-current");
        Width = (int)(GetInt("width") ?? 0);
        Height = (int)(GetInt("height") ?? 0);
        Duration = GetDouble("duration") ?? 0;
        ContainerFps = GetDouble("container-fps") ?? 0;
        ConfigureFpsCap(targetFps);
        if (_nextFile is not null)
            StartSequenceWatcher();

        if (requireHardware && !IsHardwareDecoder(HwdecCurrent))
        {
            throw new MpvException(
                MpvErrorKind.HardwareDecode,
                -3,
                $"Hardware decode is not active (hwdec-current='{HwdecCurrent ?? "<null>"}', requested='{hwdec}')");
        }

        _log($"mpv initialized: hwdec-current={HwdecCurrent}, size={Width}x{Height}, " +
             $"duration={Duration:F2}s, container-fps={ContainerFps:F2}");
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_released)
            {
                SetProperty("vid", "auto");
                if (_resumePosition > 0)
                {
                    Command("seek",
                        _resumePosition.ToString("0.###", CultureInfo.InvariantCulture),
                        "absolute");
                }

                _released = false;
            }

            SetProperty("pause", "no");
            IsPaused = false;
        }
    }

    /// <summary>
    /// Pause and optionally release the decoder. With release=true we disable the
    /// video track (vid=no); this drops the decoder/GPU surfaces while keeping the
    /// mpv instance and audio graph alive. Resume re-enables vid=auto and seeks back.
    /// </summary>
    public void Pause(bool releaseDecoder)
    {
        lock (_gate)
        {
            _resumePosition = GetDouble("time-pos") ?? _resumePosition;
            SetProperty("pause", "yes");
            IsPaused = true;

            if (releaseDecoder && !_released)
            {
                SetProperty("vid", "no");
                _released = true;
                _log($"pause: decoder released at {_resumePosition:F2}s");
            }
        }
    }

    public void SetVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        SetProperty("volume", volume.ToString(CultureInfo.InvariantCulture));
        // At volume 0, disable the audio track to release the audio decoder/buffers.
        TrySetProperty("aid", volume == 0 ? "no" : "auto");
    }

    public void SetSpeed(double speed)
    {
        speed = Math.Clamp(speed, 0.25, 4.0);
        SetProperty("speed", speed.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public void SetColor(double brightness, double contrast, double highlights,
        double hue, double temperature, double exposure, double sharpness)
    {
        static string V(double value) =>
            Math.Clamp(value, -100, 100).ToString("0.###", CultureInfo.InvariantCulture);
        TrySetProperty("brightness", V(brightness + exposure * 0.5));
        TrySetProperty("contrast", V(contrast + highlights * 0.25));
        TrySetProperty("gamma", V(highlights * 0.6));
        TrySetProperty("hue", V(hue + temperature * 0.35));
        TrySetProperty("saturation", V(temperature * 0.3));
        if (Math.Abs(sharpness) > 0.01)
            TrySetProperty("sharpen", (sharpness / 100.0 * 1.5)
                .ToString("0.###", CultureInfo.InvariantCulture));
    }

    public void SetFps(int fps)
    {
        if (fps is not (30 or 60 or 0))
            throw new ArgumentOutOfRangeException(nameof(fps), "fps must be 0, 30 or 60");

        ConfigureFpsCap(fps);
    }

    /// <summary>
    /// Target fps is a cap, not a request to duplicate frames. A 30fps source at
    /// a 60fps cap stays 30fps; only a source above the cap gets the fps filter.
    /// </summary>
    private void ConfigureFpsCap(int fps)
    {
        try
        {
            Command("vf", "remove", "@wp_fps");
        }
        catch (MpvException)
        {
            // No fps filter yet.
        }

        if (fps == 0)
        {
            _fpsCapActive = true;
            _log("fps cap disabled");
            return;
        }

        // Never duplicate frames just to reach the cap. A 30fps source on a
        // 60fps cap has no extra visual information; the fps filter only adds
        // a 4K frame queue (hundreds of MB). Only cap sources above the target.
        if (ContainerFps > 0.01 && ContainerFps <= fps + 0.5)
        {
            _fpsCapActive = true;
            _log($"fps cap={fps}: source={ContainerFps:F2}, no duplication needed");
            return;
        }

        _log($"fps cap={fps}: source={ContainerFps:F2}, applying fps filter");

        // Source is above the cap: old mpv builds use max-fps.
        if (TrySetProperty("max-fps", fps.ToString(CultureInfo.InvariantCulture)))
        {
            _fpsCapActive = true;
            _log($"fps cap via max-fps={fps}");
            return;
        }

        // Newer/lean builds: force the display clock and drop frames.
        var display = TrySetProperty("display-fps", fps.ToString(CultureInfo.InvariantCulture)) ||
                      TrySetDoubleProperty("display-fps", fps);
        var sync = TrySetProperty("video-sync", "display-vdrop");
        if (display && sync)
        {
            _fpsCapActive = true;
            _log($"fps cap via display-fps={fps}, video-sync=display-vdrop");
            return;
        }

        // Full mpv builds expose the fps filter through the vf command.
        try
        {
            Command("vf", "add", $"@wp_fps:fps={fps}");
            _fpsCapActive = true;
            _log($"fps cap via vf add @wp_fps:fps={fps}");
        }
        catch (MpvException ex)
        {
            _log($"fps cap unsupported: {ex.Message}");
        }
    }

    public Dictionary<string, object?> GetStats()
    {
        using var process = Process.GetCurrentProcess();

        return new Dictionary<string, object?>
        {
            ["workingSetMB"] = Math.Round(process.WorkingSet64 / 1048576.0, 1),
            ["privateMB"] = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1),
            ["cpuSeconds"] = Math.Round(process.TotalProcessorTime.TotalSeconds, 2),
            ["threads"] = process.Threads.Count,
            ["handles"] = process.HandleCount,
            ["hwdec"] = GetString("hwdec-current"),
            ["width"] = GetInt("width"),
            ["height"] = GetInt("height"),
            ["fps"] = GetDouble("estimated-vf-fps") ?? GetDouble("container-fps"),
            ["time"] = GetDouble("time-pos"),
            ["duration"] = GetDouble("duration"),
            ["cache"] = GetDouble("demuxer-cache-duration"),
            ["paused"] = IsPaused,
            ["released"] = _released
        };
    }

    private void TryApplyFadeOut()
    {
        try
        {
            var duration = GetDouble("duration") ?? 0;
            if (duration < 0.8)
                return;
            const double fade = 0.35;
            var start = Math.Max(0, duration - fade);
            Command("vf", "add",
                $"@wp_seqfade:fade=out:st={start.ToString("0.###", CultureInfo.InvariantCulture)}" +
                $":d={fade.ToString("0.###", CultureInfo.InvariantCulture)}");
            _log($"sequence: fade-out at {start:F2}s");
        }
        catch (Exception ex)
        {
            // A libmpv build without the fade filter still gets a hard cut.
            _log($"sequence: fade-out unavailable: {ex.Message}");
        }
    }

    private void StartSequenceWatcher()
    {
        // Some libmpv builds keep the last frame at EOF without delivering
        // MPV_EVENT_END_FILE. Poll the clock so the second video still starts.
        _sequenceTimer = new Timer(_ =>
        {
            if (_disposed || _sequenceSwitched || _nextFile is null)
                return;
            try
            {
                var pos = GetDouble("time-pos");
                var duration = GetDouble("duration");
                if (pos is not null && duration is > 0.1 &&
                    pos.Value >= duration.Value - 0.25)
                {
                    _sequenceSwitched = true;
                    var next = _nextFile;
                    _nextFile = null;
                    _log("sequence: first video finished; switching to second");
                    _ = Task.Run(() => SwitchToNextFile(next!));
                }
            }
            catch
            {
                // mpv may be between files; the next tick retries.
            }
        }, null, 500, 250);
    }

    private void SwitchToNextFile(string next)
    {
        _sequenceTimer?.Dispose();
        _sequenceTimer = null;
        try
        {
            _log("sequence: loading second video");
            LoadFile(next);
            SetProperty("loop-file", "inf");
            SetProperty("loop", "inf");
            Duration = GetDouble("duration") ?? Duration;
            Width = (int)(GetInt("width") ?? Width);
            Height = (int)(GetInt("height") ?? Height);
            Play();
            _log("sequence: now looping second video");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log($"sequence switch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Live video switch in the existing mpv/WorkerW instance. This avoids
    /// tearing down the child window for a normal wallpaper change.
    /// </summary>
    public void SwitchTo(string path)
    {
        if (!File.Exists(path))
            throw new MpvException(MpvErrorKind.Load, -2, $"Video file not found: {path}");
        _nextFile = null;
        _sequenceSwitched = true;
        SetProperty("loop-file", "inf");
        SetProperty("loop", "inf");
        LoadFile(path);
        Duration = GetDouble("duration") ?? Duration;
        Width = (int)(GetInt("width") ?? Width);
        Height = (int)(GetInt("height") ?? Height);
        ContainerFps = GetDouble("container-fps") ?? ContainerFps;
        _log($"live switch: {path}");
    }

    public void LoadFile(string path)
    {
        _fileLoaded.Reset();
        _fileError = 0;
        _lastError = null;

        Command("loadfile", path, "replace");

        var started = Stopwatch.StartNew();
        while (!_fileLoaded.Wait(20))
        {
            _pump();
            if (started.Elapsed > TimeSpan.FromSeconds(15))
                throw new MpvException(MpvErrorKind.Load, -4, "Timed out waiting for MPV_EVENT_FILE_LOADED");
        }

        _pump();

        if (_fileError < 0)
        {
            throw new MpvException(MpvErrorKind.Load, _fileError,
                $"mpv failed to load video: {_lastError ?? ErrorText(_fileError)}");
        }
    }

    private void EventLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var evPtr = Native.mpv_wait_event(_ctx, 0.2);
                if (evPtr == IntPtr.Zero)
                    continue;

                var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
                switch (ev.event_id)
                {
                    case MPV_EVENT_SHUTDOWN:
                        return;

                    case MPV_EVENT_LOG_MESSAGE:
                        HandleLogMessage(ev.data);
                        break;

                    case MPV_EVENT_FILE_LOADED:
                        _fileLoaded.Set();
                        break;

                    case MPV_EVENT_END_FILE:
                        {
                            var reason = Marshal.ReadInt32(ev.data, 0);
                            var error = Marshal.ReadInt32(ev.data, 4);
                            if (error < 0)
                            {
                                _fileError = error;
                                _lastError = ErrorText(error);
                                _log($"mpv END_FILE reason={reason} error={error}: {_lastError}");
                            }
                            else if (reason == 0)
                            {
                                if (_nextFile is { } next && !_sequenceSwitched)
                                {
                                    _sequenceSwitched = true;
                                    _log("mpv EOF reached; switching to second video");
                                    _ = Task.Run(() => SwitchToNextFile(next));
                                }
                                else if (!_sequenceSwitched)
                                {
                                    // Defensive seamless loop: even if a build ignores
                                    // loop-file/keep-open, never leave the desktop exposed.
                                    _log("mpv EOF reached; restarting loop in-place");
                                    Command("seek", "0", "absolute");
                                    SetProperty("pause", "no");
                                }
                            }

                            _fileLoaded.Set();
                            break;
                        }

                    case MPV_EVENT_PLAYBACK_RESTART:
                        _log("mpv playback restart");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"mpv event loop crashed: {ex.Message}");
        }
    }

    private void HandleLogMessage(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return;

        var msg = Marshal.PtrToStructure<MpvEventLogMessage>(data);
        var level = Marshal.PtrToStringUTF8(msg.level) ?? "?";
        var text = Marshal.PtrToStringUTF8(msg.text) ?? string.Empty;

        if (level is "error" or "fatal")
        {
            _lastError = text;
            _log($"[mpv/{level}] {text}");
        }
        else if (level is "warn")
        {
            _log($"[mpv/{level}] {text}");
        }
    }

    private void SetOption(string name, string value)
    {
        var rc = Native.mpv_set_option_string(_ctx, name, value);
        if (rc < 0)
            throw new MpvException(MpvErrorKind.Init, rc, $"mpv_set_option_string({name}) failed: {ErrorText(rc)}");
    }

    private bool TrySetOption(string name, string value)
    {
        var rc = Native.mpv_set_option_string(_ctx, name, value);
        if (rc < 0)
        {
            _log($"optional mpv option '{name}' not supported: {ErrorText(rc)}");
            return false;
        }

        return true;
    }

    private void SetProperty(string name, string value)
    {
        var rc = Native.mpv_set_property_string(_ctx, name, value);
        if (rc < 0)
            throw new MpvException(MpvErrorKind.Command, rc,
                $"mpv_set_property_string({name}={value}) failed: {ErrorText(rc)}");
    }

    private bool TrySetProperty(string name, string value)
    {
        var rc = Native.mpv_set_property_string(_ctx, name, value);
        if (rc < 0)
            _log($"mpv property {name}={value} failed: {ErrorText(rc)}");
        return rc >= 0;
    }

    private bool TrySetDoubleProperty(string name, double value)
    {
        var rc = Native.mpv_set_property(_ctx, name, MPV_FORMAT_DOUBLE, ref value);
        if (rc < 0)
            _log($"mpv double property {name}={value} failed: {ErrorText(rc)}");
        return rc >= 0;
    }

    private void Command(params string[] args)
    {
        if (_ctx == IntPtr.Zero)
            throw new MpvException(MpvErrorKind.Command, -1, "mpv context is NULL");

        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[^1] = IntPtr.Zero;

            var rc = Native.mpv_command(_ctx, ptrs);
            if (rc < 0)
                throw new MpvException(MpvErrorKind.Command, rc,
                    $"mpv command [{string.Join(' ', args)}] failed: {ErrorText(rc)}");
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (ptrs[i] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(ptrs[i]);
            }
        }
    }

    private string? GetString(string name)
    {
        if (_ctx == IntPtr.Zero)
            return null;

        var ptr = Native.mpv_get_property_string(_ctx, name);
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            Native.mpv_free(ptr);
        }
    }

    private long? GetInt(string name)
    {
        if (_ctx == IntPtr.Zero)
            return null;

        var rc = Native.mpv_get_property(_ctx, name, MPV_FORMAT_INT64, out long value);
        return rc >= 0 ? value : null;
    }

    private double? GetDouble(string name)
    {
        if (_ctx == IntPtr.Zero)
            return null;

        var rc = Native.mpv_get_property(_ctx, name, MPV_FORMAT_DOUBLE, out double value);
        return rc >= 0 ? value : null;
    }

    private static bool IsHardwareDecoder(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("no", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("software", StringComparison.OrdinalIgnoreCase);

    private static string ErrorText(int code)
    {
        var ptr = Native.mpv_error_string(code);
        return ptr == IntPtr.Zero ? $"error {code}" : Marshal.PtrToStringUTF8(ptr) ?? $"error {code}";
    }

    private static void Check(int rc, MpvErrorKind kind, string operation)
    {
        if (rc < 0)
            throw new MpvException(kind, rc, $"{operation} failed: {ErrorText(rc)}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        _fadeTimer?.Dispose();
        _fadeTimer = null;
        _sequenceTimer?.Dispose();
        _sequenceTimer = null;

        try
        {
            if (_ctx != IntPtr.Zero)
            {
                Native.mpv_wakeup(_ctx);
                _eventThread?.Join(2000);
                Native.mpv_terminate_destroy(_ctx);
                _ctx = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _log($"mpv dispose failed: {ex.Message}");
        }

        _fileLoaded.Dispose();
        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent
    {
        public int event_id;
        public int error;
        public ulong reply_userdata;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEventLogMessage
    {
        public IntPtr prefix;
        public IntPtr level;
        public IntPtr text;
        public int log_level;
    }

    private static class Native
    {
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_create();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_initialize(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_option_string(
            IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property_string(
            IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_get_property_string(
            IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(
            IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            int format,
            ref double data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(
            IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            int format,
            out long data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_get_property(
            IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            int format,
            out double data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free(IntPtr data);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_wakeup(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_terminate_destroy(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_error_string(int error);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_request_log_messages(
            IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong mpv_client_api_version();
    }
}
