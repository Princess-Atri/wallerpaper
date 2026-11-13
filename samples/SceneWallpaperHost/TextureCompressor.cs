using System;
using System.Numerics;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace SceneWallpaperHost;

/// <summary>
/// Small CPU BC3 (DXT5) encoder. It is intentionally simple and deterministic:
/// endpoint min/max per channel, 8-value alpha palette, 4-color RGB palette.
/// Textures are uploaded as BC3_UNORM; mipmapping is intentionally left to the
/// caller / offline asset pipeline for compressed textures.
/// </summary>
internal static class TextureCompressor
{
    /// <summary>BC7 mode 6/5 encoder via BCnEncoder.Net (MIT).</summary>
    public static byte[] EncodeBc7(byte[] rgba, int width, int height)
    {
        var encoder = new BcEncoder(CompressionFormat.Bc7);
        encoder.OutputOptions.Quality = CompressionQuality.Fast;
        var data = encoder.EncodeToRawBytes(rgba, width, height, BCnEncoder.Encoder.PixelFormat.Rgba32);
        return data[0];
    }

    public static byte[] EncodeBc3(byte[] rgba, int width, int height)
    {
        var bw = Math.Max(1, (width + 3) / 4);
        var bh = Math.Max(1, (height + 3) / 4);
        var output = new byte[bw * bh * 16];

        var r = new byte[16];
        var g = new byte[16];
        var b = new byte[16];
        var a = new byte[16];

        for (var by = 0; by < bh; by++)
        {
            for (var bx = 0; bx < bw; bx++)
            {
                for (var py = 0; py < 4; py++)
                {
                    for (var px = 0; px < 4; px++)
                    {
                        var x = Math.Min(width - 1, bx * 4 + px);
                        var y = Math.Min(height - 1, by * 4 + py);
                        var si = (y * width + x) * 4;
                        var di = py * 4 + px;
                        r[di] = rgba[si + 0];
                        g[di] = rgba[si + 1];
                        b[di] = rgba[si + 2];
                        a[di] = rgba[si + 3];
                    }
                }

                var offset = (by * bw + bx) * 16;
                EncodeAlpha(a, output, offset);
                EncodeColor(r, g, b, output, offset + 8);
            }
        }

        return output;
    }

    private static void EncodeAlpha(byte[] a, byte[] output, int offset)
    {
        byte min = 255, max = 0;
        for (var i = 0; i < 16; i++)
        {
            min = Math.Min(min, a[i]);
            max = Math.Max(max, a[i]);
        }

        output[offset + 0] = max;
        output[offset + 1] = min;

        if (max == min)
        {
            for (var i = 2; i < 8; i++)
                output[offset + i] = 0;
            return;
        }

        var palette = new int[8];
        palette[0] = max;
        palette[1] = min;
        for (var i = 1; i <= 6; i++)
            palette[i + 1] = ((7 - i) * max + i * min) / 7;

        ulong bits = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = 0;
            var bestDist = int.MaxValue;
            for (var j = 0; j < 8; j++)
            {
                var d = Math.Abs(a[i] - palette[j]);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = j;
                }
            }
            bits |= (ulong)best << (i * 3);
        }

        for (var i = 0; i < 6; i++)
            output[offset + 2 + i] = (byte)(bits >> (i * 8));
    }

    private static void EncodeColor(byte[] r, byte[] g, byte[] b, byte[] output, int offset)
    {
        var min = new Vector3(255, 255, 255);
        var max = new Vector3(0, 0, 0);
        for (var i = 0; i < 16; i++)
        {
            min = Vector3.Min(min, new Vector3(r[i], g[i], b[i]));
            max = Vector3.Max(max, new Vector3(r[i], g[i], b[i]));
        }

        var c0 = To565(max);
        var c1 = To565(min);
        if (c0 < c1)
            (c0, c1) = (c1, c0);

        output[offset + 0] = (byte)c0;
        output[offset + 1] = (byte)(c0 >> 8);
        output[offset + 2] = (byte)c1;
        output[offset + 3] = (byte)(c1 >> 8);

        var p0 = From565(c0);
        var p1 = From565(c1);
        var p2 = (2 * p0 + p1) / 3;
        var p3 = (p0 + 2 * p1) / 3;

        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var c = new Vector3(r[i], g[i], b[i]);
            var d0 = Vector3.DistanceSquared(c, p0);
            var d1 = Vector3.DistanceSquared(c, p1);
            var d2 = Vector3.DistanceSquared(c, p2);
            var d3 = Vector3.DistanceSquared(c, p3);
            var index = 0;
            var best = d0;
            if (d1 < best) { best = d1; index = 1; }
            if (d2 < best) { best = d2; index = 2; }
            if (d3 < best) { index = 3; }
            indices |= (uint)index << (i * 2);
        }

        output[offset + 4] = (byte)indices;
        output[offset + 5] = (byte)(indices >> 8);
        output[offset + 6] = (byte)(indices >> 16);
        output[offset + 7] = (byte)(indices >> 24);
    }

    private static int To565(Vector3 c)
    {
        var r = (int)Math.Clamp(c.X, 0, 255);
        var g = (int)Math.Clamp(c.Y, 0, 255);
        var b = (int)Math.Clamp(c.Z, 0, 255);
        return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
    }

    private static Vector3 From565(int c)
    {
        var r = ((c >> 11) & 0x1F) * 255 / 31;
        var g = ((c >> 5) & 0x3F) * 255 / 63;
        var b = (c & 0x1F) * 255 / 31;
        return new Vector3(r, g, b);
    }
}
