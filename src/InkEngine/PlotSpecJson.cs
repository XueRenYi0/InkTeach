using System.Text;
using System.Text.Json;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// PlotSpec ↔ JSON。AI 解析、交互矢量对象存档、剪贴板都共用这一份，
/// 避免"模型输出一套、存档另一套"两套解析慢慢跑偏。
/// AOT 安全：只用 Utf8JsonWriter / JsonDocument，不用反射序列化。
/// </summary>
internal static class PlotSpecJson
{
    private static readonly Color4[] Palette =
    {
        new(0.12f, 0.14f, 0.18f, 1f),
        new(0.12f, 0.38f, 0.86f, 1f),
        new(0.82f, 0.16f, 0.16f, 1f),
        new(0.10f, 0.58f, 0.30f, 1f),
    };

    public static string Write(PlotSpec spec)
    {
        if (spec == null) return "";
        using var ms = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteString("kind", spec.Kind ?? "functions");
            w.WriteString("title", spec.Title ?? "");
            w.WriteNumber("xmin", spec.XMin);
            w.WriteNumber("xmax", spec.XMax);

            w.WritePropertyName("params");
            w.WriteStartObject();
            foreach (var p in spec.Params) w.WriteNumber(p.Key, p.Value);
            w.WriteEndObject();

            w.WritePropertyName("series");
            w.WriteStartArray();
            foreach (var s in spec.Series)
            {
                w.WriteStartObject();
                w.WriteString("expr", s.Expr ?? "");
                w.WriteString("label", s.Label ?? "");
                w.WriteNumber("color", ToUInt(s.Color));
                w.WriteString("dash", s.Dashed ? "1" : "0");
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (spec.Conic != null)
            {
                var c = spec.Conic;
                w.WritePropertyName("conic");
                w.WriteStartObject();
                w.WriteString("type", c.Type ?? "ellipse");
                w.WriteNumber("h", c.H);
                w.WriteNumber("k", c.K);
                w.WriteNumber("a", c.A);
                w.WriteNumber("b", c.B);
                w.WriteString("axis", c.Axis ?? "right");
                w.WriteString("equation", c.Equation ?? "");
                w.WriteString("label", c.Label ?? "");
                w.WriteNumber("color", ToUInt(c.Color));
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static PlotSpec Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Read(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 AI 返回/存档 JSON 的根节点读 PlotSpec。</summary>
    public static PlotSpec Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        string kind = Str(root, "kind").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(kind))
        {
            if (root.TryGetProperty("conic", out _)) kind = "conic";
            else if (root.TryGetProperty("figure", out var fg))
                kind = Str(fg, "kind") == "geometry3d" ? "solid" : "plane";
            else kind = "functions";
        }
        var spec = new PlotSpec
        {
            Kind = kind,
            Title = Str(root, "title"),
            XMin = Num(root, "xmin", -5f),
            XMax = Num(root, "xmax", 5f),
        };

        // AI 协议用 domain:[xmin,xmax]；存档用 xmin/xmax。两者都认。
        if (root.TryGetProperty("domain", out var dom) && dom.ValueKind == JsonValueKind.Array)
        {
            var nums = new List<float>();
            foreach (var n in dom.EnumerateArray())
                if (n.ValueKind == JsonValueKind.Number) nums.Add((float)n.GetDouble());
            if (nums.Count >= 2 && nums[1] > nums[0])
            { spec.XMin = nums[0]; spec.XMax = nums[1]; }
        }
        if (spec.XMax <= spec.XMin) { spec.XMin = -5f; spec.XMax = 5f; }

        if (root.TryGetProperty("params", out var prm) && prm.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in prm.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number)
                    spec.Params[p.Name.ToLowerInvariant()] = p.Value.GetDouble();
        }

        if (root.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (var e in series.EnumerateArray())
            {
                string expr = e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : Str(e, "expr");
                if (string.IsNullOrWhiteSpace(expr)) continue;
                var s = new PlotSeries
                {
                    Expr = expr.Trim(),
                    Label = Str(e, "label"),
                    Color = ColorOf(e, "color", Palette[idx % Palette.Length]),
                    Dashed = Str(e, "dash") == "1",
                };
                if (string.IsNullOrWhiteSpace(s.Label)) s.Label = "y=" + s.Expr;
                spec.Series.Add(s);
                idx++;
            }
        }
        // AI 的 functions 数组（兼容旧协议）。
        else if (root.TryGetProperty("functions", out var fns) && fns.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (var f in fns.EnumerateArray())
            {
                string expr = f.ValueKind == JsonValueKind.String ? f.GetString() : Str(f, "expr");
                if (string.IsNullOrWhiteSpace(expr)) continue;
                spec.Series.Add(new PlotSeries
                {
                    Expr = expr.Trim(),
                    Label = "y=" + expr.Trim(),
                    Color = Palette[idx % Palette.Length],
                });
                idx++;
            }
        }
        else
        {
            string expr = Str(root, "expr");
            if (!string.IsNullOrWhiteSpace(expr))
                spec.Series.Add(new PlotSeries
                {
                    Expr = expr.Trim(), Label = "y=" + expr.Trim(), Color = Palette[0],
                });
        }

        if (root.TryGetProperty("conic", out var cr) && cr.ValueKind == JsonValueKind.Object)
        {
            string type = Str(cr, "type").Trim().ToLowerInvariant();
            if (type is not ("ellipse" or "hyperbola" or "parabola")) type = "ellipse";
            var c = new PlotConic
            {
                Type = type,
                H = Num(cr, "h"),
                K = Num(cr, "k"),
                A = Num(cr, "a", type == "parabola" ? 2f : 3f),
                B = Num(cr, "b", 2f),
                Axis = Str(cr, "axis"),
                Equation = Str(cr, "equation"),
                Label = Str(cr, "label"),
                Color = ColorOf(cr, "color", Palette[0]),
            };
            if (string.IsNullOrWhiteSpace(c.Axis)) c.Axis = "right";
            if (!string.IsNullOrWhiteSpace(c.Equation)
                && !string.IsNullOrWhiteSpace(c.Label)
                && !c.Label.Contains(c.Equation, StringComparison.Ordinal))
                c.Label += "  " + c.Equation;
            spec.Conic = c;
        }

        if (spec.Kind is "geometry2d" or "geometry3d")
            spec.Kind = spec.Kind == "geometry3d" ? "solid" : "plane";
        if (spec.Kind == "conic" && spec.Conic == null) return null;
        if (spec.Kind == "functions" && spec.Series.Count == 0) return null;
        return spec;
    }

    private static uint ToUInt(Color4 c)
        => ((uint)Math.Clamp((int)(c.A * 255f), 0, 255) << 24)
         | ((uint)Math.Clamp((int)(c.R * 255f), 0, 255) << 16)
         | ((uint)Math.Clamp((int)(c.G * 255f), 0, 255) << 8)
         | (uint)Math.Clamp((int)(c.B * 255f), 0, 255);

    private static Color4 FromUInt(uint v) => new(
        ((v >> 16) & 0xFF) / 255f,
        ((v >> 8) & 0xFF) / 255f,
        (v & 0xFF) / 255f,
        ((v >> 24) & 0xFF) / 255f);

    private static Color4 ColorOf(JsonElement e, string name, Color4 fallback)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number) return FromUInt((uint)v.GetDouble());
        if (v.ValueKind == JsonValueKind.Array)
        {
            var nums = new List<float>();
            foreach (var n in v.EnumerateArray())
                if (n.ValueKind == JsonValueKind.Number) nums.Add((float)n.GetDouble());
            if (nums.Count >= 3)
                return new Color4(nums[0], nums[1], nums[2], nums.Count >= 4 ? nums[3] : 1f);
        }
        return fallback;
    }

    private static float Num(JsonElement e, string name, float fallback = 0f)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (float)v.GetDouble() : fallback;

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
           && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : "";
}
