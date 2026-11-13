using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SceneWallpaperHost;

internal sealed class SceneConfig
{
    public string Name { get; set; } = "scene";
    public List<LayerConfig> Layers { get; set; } = new();
    public string? Script { get; set; }
    public int MaxParticles { get; set; } = 1024;
    public int MaxTextureSize { get; set; } = 2048;
    public string TextureCompression { get; set; } = "rgba"; // rgba | bc3 | auto
    public int MaxTextureMemoryMb { get; set; } = 512;
    public int Fps { get; set; } = 60;
    public string? BaseDirectory { get; set; }

    public static SceneConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        var cfg = JsonSerializer.Deserialize<SceneConfig>(
            File.ReadAllText(full),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? throw new InvalidDataException("empty scene json");

        cfg.BaseDirectory = Path.GetDirectoryName(full)!;
        cfg.MaxTextureSize = Math.Clamp(cfg.MaxTextureSize, 16, 8192);
        cfg.TextureCompression = cfg.TextureCompression.ToLowerInvariant();
        if (cfg.TextureCompression is not ("rgba" or "bc3" or "bc7" or "auto"))
            cfg.TextureCompression = "rgba";
        cfg.MaxTextureMemoryMb = Math.Clamp(cfg.MaxTextureMemoryMb, 16, 8192);
        cfg.MaxParticles = Math.Clamp(cfg.MaxParticles, 0, 100_000);
        cfg.Fps = cfg.Fps is 30 or 60 ? cfg.Fps : 60;
        return cfg;
    }

    public string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("empty path in scene");
        return Path.GetFullPath(Path.Combine(BaseDirectory ?? ".", path));
    }
}

internal sealed class LayerConfig
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "color"; // color | image | shader
    public string? Shader { get; set; }
    public string? Image { get; set; }
    public List<string> Textures { get; set; } = new();
    public float[] Color { get; set; } = new[] { 0.1f, 0.2f, 0.5f, 1.0f };
    public float[] Position { get; set; } = new[] { 0.5f, 0.5f };
    public float[] Size { get; set; } = new[] { 1.0f, 1.0f };
    public float[] Scale { get; set; } = new[] { 1.0f, 1.0f };
    public float Rotation { get; set; }
    public float Opacity { get; set; } = 1.0f;
    public bool Visible { get; set; } = true;
    public int Count { get; set; } = 256;
    public float ParticleSize { get; set; } = 4.0f;

    public static LayerConfig Normalize(LayerConfig c, string name)
    {
        c.Name = string.IsNullOrWhiteSpace(c.Name) ? name : c.Name;
        c.Type = c.Type.ToLowerInvariant();
        c.Position = ClampPair(c.Position, 0.5f, 0.5f);
        c.Size = ClampPair(c.Size, 1.0f, 1.0f);
        c.Scale = ClampPair(c.Scale, 1.0f, 1.0f);
        c.Color = c.Color.Length >= 4 ? c.Color : new[] { 0.1f, 0.2f, 0.5f, 1.0f };
        c.Opacity = Math.Clamp(c.Opacity, 0, 1);
        return c;
    }

    private static float[] ClampPair(float[]? v, float x, float y) =>
        v is { Length: >= 2 } ? new[] { v[0], v[1] } : new[] { x, y };
}
