namespace InkEngine;

/// <summary>
/// 工具的中文名（HUD / 日志 / 悬停提示共用）。
///
/// 为什么单独成一份：这张 switch 原来只活在 `Engine.ToolName` 里（HUD 和橡皮日志用），
/// 2026-10-02 悬停提示要在界面层显示"双曲线 / 正弦 / 棱台"这些名字——两处各写一份
/// 就会在"加图形忘了补名字"时静默漂移（仓库教训：`Engine.ToolName` 自己就为这漏过五个，
/// 见原注释）。所以把它提成公开静态表，引擎和界面都读它。
///
/// ⚠ **加图形时别忘了这里**：漏了不报错，只是 HUD / 日志 / 提示里显示成 "?"。
/// `--shapebandtest` 的"逐个真拖一笔"会打印名字，看到问号就回来补。
/// </summary>
public static class ToolNames
{
    /// <summary>工具的中文名（认不出来返回 "?"）。</summary>
    public static string Of(Tool t) => t switch
    {
        Tool.Pen => "笔",
        Tool.Highlighter => "荧光笔",
        Tool.Laser => "激光笔",
        Tool.Eraser => "橡皮擦",
        Tool.PixelEraser => "像素橡皮",
        Tool.Capture => "截图",
        Tool.Marquee => "框选",
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
        _ => "?",
    };
}
