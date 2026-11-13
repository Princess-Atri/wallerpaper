using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace WebWallpaperParent;

/// <summary>
/// WASAPI loopback capture + small FFT. Captures the default render device
/// (system audio) and exposes normalized 32-band spectrum data.
/// </summary>
internal sealed class AudioSpectrum : IDisposable
{
    private const int FftSize = 1024;
    private const int Bands = 32;

    private readonly object _gate = new();
    private readonly List<float> _samples = new(16384);
    private readonly double[] _window = new double[FftSize];

    private WasapiLoopbackCapture? _capture;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private bool _disposed;
    private string? _error;

    public AudioSpectrum()
    {
        for (var i = 0; i < FftSize; i++)
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));

        try
        {
            _capture = new WasapiLoopbackCapture();
            _sampleRate = _capture.WaveFormat.SampleRate;
            _channels = Math.Max(1, _capture.WaveFormat.Channels);
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            Available = true;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Available = false;
            _capture?.Dispose();
            _capture = null;
        }
    }

    public bool Available { get; }
    public string? Error => _error;

    public bool TryGetSpectrum(out double[] bins, out double level)
    {
        bins = new double[Bands];
        level = 0;

        float[] copy;
        lock (_gate)
        {
            if (_samples.Count < FftSize)
                return false;

            copy = _samples.GetRange(_samples.Count - FftSize, FftSize).ToArray();
        }

        var re = new double[FftSize];
        var im = new double[FftSize];
        double energy = 0;
        for (var i = 0; i < FftSize; i++)
        {
            var v = copy[i] * _window[i];
            re[i] = v;
            energy += copy[i] * copy[i];
        }

        Fft(re, im);

        level = Math.Clamp(Math.Sqrt(energy / FftSize) * 4.0, 0, 1);
        var nyquist = _sampleRate / 2.0;
        var minHz = 40.0;
        var maxHz = Math.Min(16000.0, nyquist);
        var logMin = Math.Log10(minHz);
        var logMax = Math.Log10(maxHz);

        for (var b = 0; b < Bands; b++)
        {
            var f0 = Math.Pow(10, logMin + (logMax - logMin) * b / Bands);
            var f1 = Math.Pow(10, logMin + (logMax - logMin) * (b + 1) / Bands);
            var i0 = Math.Max(1, (int)(f0 / nyquist * FftSize / 2.0));
            var i1 = Math.Min(FftSize / 2 - 1, Math.Max(i0 + 1, (int)(f1 / nyquist * FftSize / 2.0)));

            double sum = 0;
            for (var i = i0; i <= i1; i++)
                sum += Math.Sqrt(re[i] * re[i] + im[i] * im[i]);

            var avg = sum / (i1 - i0 + 1);
            bins[b] = Math.Clamp(avg * 9.0, 0, 1);
        }

        return true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || _capture is null)
            return;

        var format = _capture.WaveFormat;
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
            return;

        var frameSize = bytesPerSample * format.Channels;
        if (frameSize <= 0)
            return;

        lock (_gate)
        {
            for (var offset = 0; offset + frameSize <= e.BytesRecorded; offset += frameSize)
            {
                double sum = 0;
                for (var ch = 0; ch < format.Channels; ch++)
                {
                    var p = offset + ch * bytesPerSample;
                    if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
                        sum += BitConverter.ToSingle(e.Buffer, p);
                    else if (format.BitsPerSample == 16)
                        sum += BitConverter.ToInt16(e.Buffer, p) / 32768.0;
                    else if (format.BitsPerSample == 24)
                        sum += ((e.Buffer[p] | (e.Buffer[p + 1] << 8) | (e.Buffer[p + 2] << 16)) - 0x800000) / 8388608.0;
                    else if (format.BitsPerSample == 32)
                        sum += BitConverter.ToInt32(e.Buffer, p) / 2147483648.0;
                }

                _samples.Add((float)(sum / format.Channels));
            }

            // keep enough history for the 1024-sample FFT, but do not grow forever
            if (_samples.Count > 8192)
                _samples.RemoveRange(0, _samples.Count - 4096);
        }
    }

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wlenRe = Math.Cos(ang);
            var wlenIm = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                var wRe = 1.0;
                var wIm = 0.0;
                for (var j = 0; j < len / 2; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = re[i + j + len / 2] * wRe - im[i + j + len / 2] * wIm;
                    var vIm = re[i + j + len / 2] * wIm + im[i + j + len / 2] * wRe;
                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + len / 2] = uRe - vRe;
                    im[i + j + len / 2] = uIm - vIm;
                    var nextRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = nextRe;
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.StopRecording();
                _capture.Dispose();
                _capture = null;
            }
        }
        catch
        {
            // ignore shutdown errors
        }
    }
}
