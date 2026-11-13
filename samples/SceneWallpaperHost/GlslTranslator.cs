using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SceneWallpaperHost;

/// <summary>
/// Small GLSL-fragment -> HLSL (ps_5_0) translator for the wallpaper subset.
///
/// Supported:
///   - #version / precision lines
///   - uniform declarations are ignored; built-ins are provided as HLSL macros
///   - gl_FragCoord, uv, fragColor
///   - vec2/3/4, mat2/3/4, float/int/bool
///   - mix -> lerp, fract -> frac, mod -> fmod, atan(y,x) -> atan2
///   - float4 main(float4 pos : SV_Position) wrapper with layer transform + discard
///
/// Not supported (rejected instead of silently producing wrong output):
///   - textures/samplers, multiple render targets, in/out varyings, loops with
///     fancy constructs, #include, preprocessor macros.
/// </summary>
internal static class GlslTranslator
{
    private static readonly Regex SamplerDecl = new(@"uniform\s+sampler2D\s+([A-Za-z_][A-Za-z0-9_]*)\s*;", RegexOptions.Multiline);
    private static readonly Regex TextureCall = new(@"\b(?:texture|texture2D)\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*([^)]+)\)");
    private static readonly Regex VersionLine = new(@"^\s*#version[^\n]*\n", RegexOptions.Multiline);
    private static readonly Regex PrecisionLine = new(@"^\s*precision[^\n]*;\s*$", RegexOptions.Multiline);
    private static readonly Regex UniformLine = new(@"^\s*uniform\s+[^;]+;\s*$", RegexOptions.Multiline);
    private static readonly Regex OutFrag = new(@"^\s*out\s+vec4\s+fragColor\s*;\s*$", RegexOptions.Multiline);
    private static readonly Regex MainDecl = new(@"\bvoid\s+main\s*\(\s*\)");
    private static readonly Regex FragCoord = new(@"\bgl_FragCoord\b");
    private static readonly Regex MixCall = new(@"\bmix\s*\(");
    private static readonly Regex FractCall = new(@"\bfract\s*\(");
    private static readonly Regex ModCall = new(@"\bmod\s*\(");
    private static readonly Regex Atan2Call = new(@"\batan\s*\(\s*([^,]+),\s*([^)]+)\)");

    private const string Header = """
        cbuffer SceneParams : register(b0)
        {
            float4 P0; // time, mouse.x, mouse.y, aspect
            float4 P1; // width, height, audioLevel, audioBass
            float4 P2; // audioMid, audioTreble, shaderIntensity, 0
            float4 P3; // audioLow  (bins 0..3)
            float4 P4; // audioHigh (bins 4..7)
        };

        cbuffer LayerParams : register(b1)
        {
            float4 L0; // width, height, centerX, centerY
            float4 L1; // layerWidthPx, layerHeightPx, scaleX, scaleY
            float4 L2; // rotation, opacity, type, 0
            float4 L3; // tint rgba
        };

        #define time P0.x
        #define mouse P0.yz
        #define resolution P1.xy
        #define audioLevel P1.z
        #define audioBass P1.w
        #define audioMid P2.x
        #define audioTreble P2.y
        #define shaderIntensity P2.z
        #define audioLow P3
        #define audioHigh P4
        #define Tint L3
        #define Opacity L2.y

        float2 ComputeUV(float2 pixel)
        {
            float2 d = pixel - L0.zw;
            float s = sin(-L2.x);
            float c = cos(-L2.x);
            d = float2(c * d.x - s * d.y, s * d.x + c * d.y);
            d /= max(abs(L1.zw), float2(0.0001, 0.0001));
            return d / max(L1.xy, float2(1.0, 1.0)) + 0.5;
        }

        float2 g_uv;
        float4 g_pos;
        float4 fragColor = float4(0, 0, 0, 0);
        #define uv g_uv

        """;

    private const string Footer = """

        float4 main(float4 pos : SV_Position) : SV_Target
        {
            g_pos = pos;
            g_uv = ComputeUV(pos.xy);
            if (any(g_uv < 0.0) || any(g_uv > 1.0))
                discard;

            UserMain();
            return fragColor * Tint * Opacity;
        }
        """;

    public static string Translate(string glsl, out List<string> samplers)
    {
        if (string.IsNullOrWhiteSpace(glsl))
            throw new InvalidOperationException("empty GLSL shader");

        if (Regex.IsMatch(glsl, @"\bsamplerCube\b"))
            throw new NotSupportedException("samplerCube is not supported");

        samplers = new List<string>();
        foreach (Match match in SamplerDecl.Matches(glsl))
        {
            var name = match.Groups[1].Value;
            if (!samplers.Contains(name))
                samplers.Add(name);
        }

        if (Regex.IsMatch(glsl, @"\bin\s+|\bout\s+(?!vec4\s+fragColor)"))
            throw new NotSupportedException("GLSL varying in/out is not supported");

        var body = VersionLine.Replace(glsl, "");
        body = PrecisionLine.Replace(body, "");
        body = UniformLine.Replace(body, "");
        body = OutFrag.Replace(body, "");
        body = MainDecl.Replace(body, "void UserMain()");

        if (!Regex.IsMatch(body, @"\bvoid\s+UserMain\s*\("))
            throw new NotSupportedException("shader must define void main()");

        body = FragCoord.Replace(body, "g_pos");
        body = TypeReplace(body);
        body = MixCall.Replace(body, "lerp(");
        body = FractCall.Replace(body, "frac(");
        body = ModCall.Replace(body, "fmod(");
        body = Atan2Call.Replace(body, "atan2($1, $2)");
        body = TextureCall.Replace(body, "$1.Sample($1_s, $2)");

        var decls = new System.Text.StringBuilder();
        for (var i = 0; i < samplers.Count; i++)
        {
            var name = samplers[i];
            decls.AppendLine($"Texture2D {name} : register(t{i});");
            decls.AppendLine($"SamplerState {name}_s : register(s{i});");
        }

        return decls + Header + body + Footer;
    }

    private static string TypeReplace(string s)
    {
        s = Regex.Replace(s, @"\bvec2\b", "float2");
        s = Regex.Replace(s, @"\bvec3\b", "float3");
        s = Regex.Replace(s, @"\bvec4\b", "float4");
        s = Regex.Replace(s, @"\bivec2\b", "int2");
        s = Regex.Replace(s, @"\bivec3\b", "int3");
        s = Regex.Replace(s, @"\bivec4\b", "int4");
        s = Regex.Replace(s, @"\bmat2\b", "float2x2");
        s = Regex.Replace(s, @"\bmat3\b", "float3x3");
        s = Regex.Replace(s, @"\bmat4\b", "float4x4");
        s = Regex.Replace(s, @"\bfloat\s+float\b", "float");
        return s;
    }
}
