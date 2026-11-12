namespace InkEngine;

internal enum ShapeGroup
{
    Common = 0,
    Curve = 1,
    Solid = 2,
}

/// <summary>
/// 图形面板的一张卡：工具 + 档位。2026-11-02 起不再“一个工具一张卡、再点换档”，
/// 而是每个档位各占一张卡（直线 3 张、抛物线 2 张、棱柱四档 4 张……）。
/// </summary>
internal sealed class ShapePreset
{
    public Tool Tool;
    public string Label;
    public ShapeGroup Group;
    /// <summary>档位值：直线=StrokeDash、抛物线=0上/1右、双曲线/椭圆/坐标=0带/1不带、立体=3..6 边。</summary>
    public int Variant;

    public ShapePreset(Tool tool, string label, ShapeGroup group, int variant)
    {
        Tool = tool; Label = label; Group = group; Variant = variant;
    }
}

/// <summary>
/// 图形面板的固定目录（39 张卡）。
/// 顺序：常用（基础 + 梯形 + 五角星 + 扇形 + 坐标系 2 档）→ 曲线（10）→ 立体（17，空间坐标系开头）。
/// “全部”只是把这三组按顺序拼起来，不另维护一张表。
/// </summary>
internal static class ShapeCatalog
{
    public static readonly ShapePreset[] All =
    {
        // ---- 常用 14 ----
        // 直线 3 张：实线 / 虚线 / 点线（2026-11-12 用户定）。
        // 面板自己决定线型，不受工具栏那边“上一次切到哪档”影响；
        // 反过来点卡会同步 LineDash，工具栏图标也跟着变。
        new(Tool.Line, "直线（实线）", ShapeGroup.Common, 0),
        new(Tool.Line, "直线（虚线）", ShapeGroup.Common, 1),
        new(Tool.Line, "直线（点线）", ShapeGroup.Common, 2),
        new(Tool.Rectangle, "矩形", ShapeGroup.Common, 0),
        new(Tool.Ellipse, "椭圆", ShapeGroup.Common, 0),
        new(Tool.Circle, "圆", ShapeGroup.Common, 0),
        new(Tool.Triangle, "三角形", ShapeGroup.Common, 0),
        new(Tool.Trapezoid, "梯形", ShapeGroup.Common, 0),
        new(Tool.Parallelogram, "平行四边形", ShapeGroup.Common, 0),
        new(Tool.Arrow, "箭头", ShapeGroup.Common, 0),
        new(Tool.Star, "五角星", ShapeGroup.Common, 0),
        new(Tool.Sector, "扇形", ShapeGroup.Common, 0),
        new(Tool.Coordinate, "网格坐标系", ShapeGroup.Common, 0),
        new(Tool.Coordinate, "无网格坐标系", ShapeGroup.Common, 1),

        // ---- 曲线 10 ----
        new(Tool.ConicEllipse, "带焦点三角", ShapeGroup.Curve, 0),
        new(Tool.ConicEllipse, "无焦点三角", ShapeGroup.Curve, 1),
        new(Tool.Hyperbola, "带渐近线", ShapeGroup.Curve, 0),
        new(Tool.Hyperbola, "无渐近线", ShapeGroup.Curve, 1),
        new(Tool.Parabola, "开口上", ShapeGroup.Curve, 0),
        new(Tool.Parabola, "开口右", ShapeGroup.Curve, 1),
        new(Tool.Sine, "正弦", ShapeGroup.Curve, 0),
        new(Tool.Cosine, "余弦", ShapeGroup.Curve, 0),
        new(Tool.Wave, "波浪", ShapeGroup.Curve, 0),
        new(Tool.Tangent, "正切", ShapeGroup.Curve, 0),

        // ---- 立体 17 ----
        new(Tool.SpaceCoordinate, "空间坐标系", ShapeGroup.Solid, 0),
        new(Tool.Cylinder, "圆柱", ShapeGroup.Solid, 0),
        new(Tool.Cone, "圆锥", ShapeGroup.Solid, 0),
        new(Tool.ConeFrustum, "圆台", ShapeGroup.Solid, 0),
        new(Tool.Sphere, "球", ShapeGroup.Solid, 0),
        new(Tool.Prism, "三棱柱", ShapeGroup.Solid, 3),
        new(Tool.Prism, "四棱柱", ShapeGroup.Solid, 4),
        new(Tool.Prism, "五棱柱", ShapeGroup.Solid, 5),
        new(Tool.Prism, "六棱柱", ShapeGroup.Solid, 6),
        new(Tool.Pyramid, "三棱锥", ShapeGroup.Solid, 3),
        new(Tool.Pyramid, "四棱锥", ShapeGroup.Solid, 4),
        new(Tool.Pyramid, "五棱锥", ShapeGroup.Solid, 5),
        new(Tool.Pyramid, "六棱锥", ShapeGroup.Solid, 6),
        new(Tool.Frustum, "三棱台", ShapeGroup.Solid, 3),
        new(Tool.Frustum, "四棱台", ShapeGroup.Solid, 4),
        new(Tool.Frustum, "五棱台", ShapeGroup.Solid, 5),
        new(Tool.Frustum, "六棱台", ShapeGroup.Solid, 6),
    };

    public static int Count => All.Length;

    /// <summary>分类页签（顺序固定）：全部 / 常用 / 曲线 / 立体。默认「全部」。</summary>
    public static readonly ShapeCategoryInfo[] Categories =
    {
        new("all", "全部", null),
        new("common", "常用", ShapeGroup.Common),
        new("curve", "曲线", ShapeGroup.Curve),
        new("solid", "立体", ShapeGroup.Solid),
    };

    public static ShapePreset[] ForCategory(string id)
    {
        var cat = Array.Find(Categories, c => c.Id == id);
        if (cat.Id == null || cat.Group == null) return All;
        var list = new List<ShapePreset>(All.Length);
        foreach (var p in All) if (p.Group == cat.Group.Value) list.Add(p);
        return list.ToArray();
    }

    public static int CountFor(string id) => ForCategory(id).Length;

    public static string NameFor(string id)
    {
        var cat = Array.Find(Categories, c => c.Id == id);
        return cat.Id == null ? "全部" : cat.Name;
    }
}

internal readonly struct ShapeCategoryInfo
{
    public readonly string Id;
    public readonly string Name;
    public readonly ShapeGroup? Group;
    public ShapeCategoryInfo(string id, string name, ShapeGroup? group)
    {
        Id = id; Name = name; Group = group;
    }
}
