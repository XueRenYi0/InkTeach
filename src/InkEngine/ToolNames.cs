using InkLocalization;

namespace InkEngine;

/// <summary>
/// 工具名（HUD / 日志 / 悬停提示共用）。
///
/// 中文源文保留在这张 switch 里，`Loc.Tr` 在出口统一翻译；加图形时仍然只补这一处。
/// </summary>
public static class ToolNames
{
    /// <summary>工具名（认不出来返回 "?"）。</summary>
    public static string Of(Tool t) => Loc.Tr(t switch
    {
        Tool.Pen => "笔",
        Tool.Highlighter => "荧光笔",
        Tool.Laser => "激光笔",
        Tool.Eraser => "橡皮擦",
        Tool.PixelEraser => "像素橡皮",
        Tool.Capture => "截图",
        Tool.Marquee => "选择",
        Tool.Line => "直线",
        Tool.Rectangle => "矩形",
        Tool.Ellipse => "椭圆",
        Tool.Circle => "圆",
        Tool.Triangle => "三角形",
        Tool.Parallelogram => "平行四边形",
        Tool.Arrow => "箭头",
        Tool.Coordinate => "坐标系",
        Tool.NumberLine => "数轴",
        Tool.Parabola => "抛物线",
        Tool.Hyperbola => "双曲线",
        Tool.Sine => "正弦",
        Tool.Cosine => "余弦",
        Tool.Wave => "波浪线",
        Tool.Tangent => "正切",
        Tool.Cylinder => "圆柱",
        Tool.Cone => "圆锥",
        Tool.Cuboid => "长方体",
        Tool.Tetrahedron => "四面体",
        Tool.ConeFrustum => "圆台",
        Tool.Sphere => "球",
        Tool.Prism => "棱柱",
        Tool.Pyramid => "棱锥",
        Tool.Frustum => "棱台",
        Tool.ConicEllipse => "椭圆（带焦点）",
        Tool.Star => "五角星",
        Tool.Trapezoid => "梯形",
        Tool.Sector => "扇形",
        Tool.Text => "文本",
        _ => "?",
    });
}
