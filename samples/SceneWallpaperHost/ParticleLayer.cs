using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SceneWallpaperHost;

/// <summary>
/// CPU simulation + GPU-instanced-style quad batch. The CPU writes one quad
/// (6 vertices) per particle into a dynamic vertex buffer; the GPU does the
/// pixel work. Hard cap is the scene MaxParticles (and an internal 4096 cap).
/// </summary>
internal sealed class ParticleLayer
{
    public const int MaxHardwareParticles = 4096;
    public const int VerticesPerParticle = 6;
    public const int VertexStride = 32; // float2 pos + float2 local + float4 color

    public string Name { get; }
    public int Count { get; private set; }
    public int MaxCount { get; }
    public float SpeedScale { get; set; } = 1.0f;
    public Vector4 Tint { get; }
    public float Opacity { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float SizeX { get; set; }
    public float SizeY { get; set; }
    public float ScaleX { get; set; }
    public float ScaleY { get; set; }
    public float Rotation { get; set; }
    public bool Visible { get; set; } = true;

    public ParticleVertex[] Vertices { get; }
    public int VertexCount { get; private set; }

    private static readonly (float X, float Y)[] LocalCorners =
    {
        (-1, -1), (1, -1), (-1, 1),
        (-1, 1), (1, -1), (1, 1)
    };

    private readonly float _particleSize;
    private readonly Random _random;
    private readonly Particle[] _particles;

    public ParticleLayer(LayerConfig cfg, int maxParticles)
    {
        Name = cfg.Name;
        MaxCount = Math.Clamp(Math.Min(Math.Max(0, maxParticles), MaxHardwareParticles), 0, MaxHardwareParticles);
        Count = Math.Clamp(cfg.Count, 0, MaxCount);
        Tint = cfg.Color.Length >= 4
            ? new Vector4(cfg.Color[0], cfg.Color[1], cfg.Color[2], cfg.Color[3])
            : new Vector4(0.4f, 0.8f, 1, 0.8f);
        Opacity = Math.Clamp(cfg.Opacity, 0, 1);
        PositionX = cfg.Position[0];
        PositionY = cfg.Position[1];
        SizeX = Math.Max(0.001f, cfg.Size[0]);
        SizeY = Math.Max(0.001f, cfg.Size[1]);
        ScaleX = Math.Max(0.001f, cfg.Scale[0]);
        ScaleY = Math.Max(0.001f, cfg.Scale[1]);
        Rotation = cfg.Rotation;
        _particleSize = Math.Clamp(cfg.ParticleSize, 1, 32);
        _random = new Random(0x51CE + cfg.Name.GetHashCode(StringComparison.Ordinal));
        _particles = new Particle[Math.Max(1, MaxCount)];
        Vertices = new ParticleVertex[Math.Max(1, MaxCount * VerticesPerParticle)];

        for (var i = 0; i < Count; i++)
            InitParticle(i);
    }

    /// <summary>Runtime resize; clamped to the buffer allocated from maxParticles.</summary>
    public void SetCount(int count)
    {
        count = Math.Clamp(count, 0, MaxCount);
        for (var i = Count; i < count; i++)
            InitParticle(i);
        Count = count;
    }

    private void InitParticle(int index)
    {
        _particles[index] = new Particle
        {
            X = (float)_random.NextDouble(),
            Y = (float)_random.NextDouble(),
            Vx = (float)(_random.NextDouble() - 0.5) * 0.035f,
            Vy = (float)(_random.NextDouble() - 0.5) * 0.025f,
            Size = 0.45f + (float)_random.NextDouble() * 1.1f,
            Phase = (float)_random.NextDouble() * MathF.Tau
        };
    }

    public void Update(double time, double dt, AudioFrame audio)
    {
        if (!Visible || Opacity <= 0.001f || Count == 0)
        {
            VertexCount = 0;
            return;
        }

        var speed = (1.0f + (float)Math.Clamp(audio.Bass, 0, 1) * 2.2f) * Math.Clamp(SpeedScale, 0, 20);
        var swirl = 0.15f + (float)Math.Clamp(audio.Level, 0, 1) * 0.75f;
        var step = (float)Math.Clamp(dt, 0.0001, 0.1);

        for (var i = 0; i < Count; i++)
        {
            ref var p = ref _particles[i];
            p.X += p.Vx * step * speed;
            p.Y += p.Vy * step * speed;
            var angle = p.Phase + (float)time * 0.25f + p.X * 6.0f;
            p.X += MathF.Cos(angle) * 0.0008f * swirl;
            p.Y += MathF.Sin(angle) * 0.0008f * swirl;

            if (p.X < -0.1f) p.X = 1.1f;
            if (p.X > 1.1f) p.X = -0.1f;
            if (p.Y < -0.1f) p.Y = 1.1f;
            if (p.Y > 1.1f) p.Y = -0.1f;
        }
    }

    public int BuildVertices(int screenWidth, int screenHeight, AudioFrame audio)
    {
        if (Count == 0 || !Visible || Opacity <= 0.001f)
            return 0;

        var cx = PositionX * screenWidth;
        var cy = PositionY * screenHeight;
        var layerW = SizeX * screenWidth * ScaleX;
        var layerH = SizeY * screenHeight * ScaleY;
        var cos = MathF.Cos(Rotation);
        var sin = MathF.Sin(Rotation);
        var pulse = 1.0f + (float)Math.Clamp(audio.Level, 0, 1) * 0.9f;
        var index = 0;

        for (var i = 0; i < Count; i++)
        {
            ref var p = ref _particles[i];
            var lx = (p.X - 0.5f) * layerW;
            var ly = (p.Y - 0.5f) * layerH;
            var rx = lx * cos - ly * sin;
            var ry = lx * sin + ly * cos;
            var px = cx + rx;
            var py = cy + ry;
            var radius = _particleSize * p.Size * pulse;

            // Fade at the layer border so a full-screen particle layer does not
            // pop at the desktop edges.
            var edge = MathF.Min(MathF.Min(p.X, 1 - p.X), MathF.Min(p.Y, 1 - p.Y));
            var alpha = Math.Clamp(edge * 7.0f, 0, 1) * Tint.W * Opacity;
            var color = new Vector4(Tint.X, Tint.Y, Tint.Z, alpha);

            for (var v = 0; v < VerticesPerParticle; v++)
            {
                var qx = px + LocalCorners[v].X * radius;
                var qy = py + LocalCorners[v].Y * radius;
                Vertices[index++] = new ParticleVertex
                {
                    X = qx,
                    Y = qy,
                    LocalX = LocalCorners[v].X,
                    LocalY = LocalCorners[v].Y,
                    R = color.X,
                    G = color.Y,
                    B = color.Z,
                    A = color.W
                };
            }
        }

        VertexCount = index;
        return index;
    }

    private struct Particle
    {
        public float X, Y, Vx, Vy, Size, Phase;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ParticleVertex
{
    public float X, Y;
    public float LocalX, LocalY;
    public float R, G, B, A;
}
