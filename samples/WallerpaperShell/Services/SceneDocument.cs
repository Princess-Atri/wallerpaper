using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Reads and writes scene.json layers for the in-app scene editor. The
/// document keeps unknown properties so hand-written scenes survive a round
/// trip through the editor.
/// </summary>
public static class SceneDocument
{
    public sealed class Layer
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "color"; // color | image | particles | shader
        public string Image { get; set; } = "";
        public double PosX { get; set; } = 0.5;
        public double PosY { get; set; } = 0.5;
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        public double Rotation { get; set; }
        public double Opacity { get; set; } = 1.0;
        public bool Visible { get; set; } = true;
        public int Count { get; set; } = 320;
        public double ParticleSize { get; set; } = 4.0;
        public string ColorHex { get; set; } = "#0B1020";
        public JsonObject Raw { get; set; } = new();
    }

    public sealed class Document
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int Fps { get; set; } = 60;
        public List<Layer> Layers { get; } = new();
        public JsonObject Root { get; set; } = new();
    }

    public static Document Load(string scenePath)
    {
        var text = File.ReadAllText(scenePath);
        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidDataException("scene.json 不是有效对象");
        var doc = new Document
        {
            Path = scenePath,
            Name = root["name"]?.GetValue<string>() ?? "scene",
            Fps = root["fps"]?.GetValue<int>() ?? 60,
            Root = root
        };
        if (root["layers"] is JsonArray layers)
        {
            foreach (var node in layers)
            {
                if (node is not JsonObject layer) continue;
                doc.Layers.Add(ReadLayer(layer));
            }
        }
        return doc;
    }

    public static void Save(Document doc)
    {
        var array = new JsonArray();
        foreach (var layer in doc.Layers)
        {
            var obj = layer.Raw;
            obj["name"] = layer.Name;
            obj["type"] = layer.Type;
            obj["position"] = new JsonArray(layer.PosX, layer.PosY);
            obj["scale"] = new JsonArray(layer.ScaleX, layer.ScaleY);
            obj["rotation"] = layer.Rotation;
            obj["opacity"] = layer.Opacity;
            obj["visible"] = layer.Visible;
            if (layer.Type == "image")
                obj["image"] = layer.Image;
            if (layer.Type == "particles")
            {
                obj["count"] = layer.Count;
                obj["particleSize"] = layer.ParticleSize;
            }
            if (layer.Type is "color" or "particles")
                obj["color"] = HexToRgba(layer.ColorHex);
            array.Add(obj);
        }
        doc.Root["name"] = doc.Name;
        doc.Root["fps"] = doc.Fps;
        doc.Root["layers"] = array;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(doc.Path)!);
        File.WriteAllText(doc.Path,
            doc.Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static Layer ReadLayer(JsonObject obj)
    {
        var layer = new Layer { Raw = (JsonObject)obj.DeepClone() };
        layer.Name = obj["name"]?.GetValue<string>() ?? "layer";
        layer.Type = obj["type"]?.GetValue<string>()?.ToLowerInvariant() ?? "color";
        layer.Image = obj["image"]?.GetValue<string>() ?? "";
        if (obj["position"] is JsonArray pos && pos.Count >= 2)
        {
            layer.PosX = pos[0]?.GetValue<double>() ?? 0.5;
            layer.PosY = pos[1]?.GetValue<double>() ?? 0.5;
        }
        if (obj["scale"] is JsonArray scale && scale.Count >= 2)
        {
            layer.ScaleX = scale[0]?.GetValue<double>() ?? 1.0;
            layer.ScaleY = scale[1]?.GetValue<double>() ?? 1.0;
        }
        layer.Rotation = obj["rotation"]?.GetValue<double>() ?? 0;
        layer.Opacity = obj["opacity"]?.GetValue<double>() ?? 1.0;
        layer.Visible = obj["visible"]?.GetValue<bool>() ?? true;
        layer.Count = obj["count"]?.GetValue<int>() ?? 320;
        layer.ParticleSize = obj["particleSize"]?.GetValue<double>() ?? 4.0;
        layer.ColorHex = RgbaToHex(obj["color"] as JsonArray);
        return layer;
    }

    public static JsonArray HexToRgba(string hex)
    {
        var value = (hex ?? "#000000").TrimStart('#');
        if (value.Length == 8) value = value[..6];
        if (value.Length != 6) value = "000000";
        var r = Convert.ToInt32(value[..2], 16) / 255.0;
        var g = Convert.ToInt32(value[2..4], 16) / 255.0;
        var b = Convert.ToInt32(value[4..6], 16) / 255.0;
        return new JsonArray(Math.Round(r, 4), Math.Round(g, 4), Math.Round(b, 4), 1.0);
    }

    private static string RgbaToHex(JsonArray? color)
    {
        if (color is null || color.Count < 3) return "#0B1020";
        int Channel(int i) =>
            (int)Math.Clamp(Math.Round((color[i]?.GetValue<double>() ?? 0) * 255), 0, 255);
        return $"#{Channel(0):X2}{Channel(1):X2}{Channel(2):X2}";
    }

    /// <summary>Mirrors the layer into a fresh scene folder so edits never
    /// overwrite the source scene the user imported.</summary>
    public static string CreateWorkingCopy(string sourceScenePath, string name)
    {
        var root = Path.Combine(AppStorage.SoftwareRoot, "scenes");
        var safe = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))
            .ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "scene-edit";
        var folder = Path.Combine(root, $"{safe}-edit-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        var sourceDir = Path.GetDirectoryName(sourceScenePath)!;
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var target = Path.Combine(folder, Path.GetFileName(file));
            try { File.Copy(file, target, overwrite: true); } catch { }
        }
        return Path.Combine(folder, "scene.json");
    }
}
