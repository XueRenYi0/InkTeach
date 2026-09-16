using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 图标绘制。路径数据来自 `Icons.g.cs`（`tools/gen-ui-icons.ps1` 从 Fluent UI
/// System Icons 生成，MIT），几何由引擎的 <see cref="SvgPath"/> 解析并按 d 缓存。
///
/// 三条规矩：
///
///   1. **绝不每帧重建几何**。WPF 里每帧 `Geometry.Parse` 没感觉，D2D 里是要命的；
///      缓存放在 <see cref="SvgPath"/> 里（按 d 字符串），这里只负责算变换。
///   2. 几何按 24×24 的原始坐标存，绘制时用变换缩放到目标尺寸——
///      同一份几何在任意尺寸/DPI 下复用。
///   3. 名字拼错**当场抛**（生成的 `Get` 就是这么写的），不画一个空图标了事：
///      空图标在界面上太难被发现。
/// </summary>
internal static class IconAtlas
{
    private static ID2D1StrokeStyle _round;

    /// <summary>
    /// 建一次"圆头圆角"的描边样式（自绘图标要用）。由界面在 Attach 时调一次。
    /// 上游图标都是填充路径，不需要它；我们自己画的那几个（激光笔）是**线条**，
    /// 线头不圆的话，那个"笔＋光束"会像三根火柴棍。
    /// </summary>
    public static void Init(ID2D1Factory1 factory)
    {
        if (_round != null || factory == null) return;
        _round = factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
    }

    /// <summary>按 24 网格画的图标，画的左上角是 (x, y)，边长 size。</summary>
    public static void Draw(ID2D1DeviceContext ctx, string name, float x, float y,
                            float size, ID2D1Brush brush)
    {
        var geo = SvgPath.Get(PanelIcons.Get(name));
        if (geo == null) return;

        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;
        ctx.FillGeometry(geo, brush);
        ctx.Transform = saved;
    }

    /// <summary>在方块里居中画一个图标（按钮上用它，省得每处都自己算居中）。</summary>
    public static void DrawCentered(ID2D1DeviceContext ctx, string name, RectF box,
                                    float size, ID2D1Brush brush)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        Draw(ctx, name, cx - size * 0.5f, cy - size * 0.5f, size, brush);
    }

    /// <summary>
    /// **自绘的激光笔图标**：笔 ＋ 光束 ＋ 落点。
    ///
    /// 为什么不用上游图标：Fluent 里没有"激光笔"这个专名（只有 Flash 闪电、
    /// Record 圆点这些近义）；Material Symbols 有专名但要引第二个图标库，
    /// 混库会让线宽和光学尺寸对不上。这三个元素照抄假面板里量好的坐标
    /// （24 网格：笔身 (17,4.6)→(12.4,9.2) 粗 3、光束 (11.6,10)→(9.4,12.2) 细 1.4、
    /// 落点圆心 (6.6,15) 半径 2.1），画出来和界面上其它 Fluent 图标是一套手感。
    /// </summary>
    public static void DrawLaser(ID2D1DeviceContext ctx, RectF box, float size, ID2D1Brush brush)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;

        ctx.DrawLine(new Vector2(17f, 4.6f), new Vector2(12.4f, 9.2f), brush, 3f, _round);
        ctx.DrawLine(new Vector2(11.6f, 10f), new Vector2(9.4f, 12.2f), brush, 1.4f, _round);
        ctx.FillEllipse(new Ellipse(new Vector2(6.6f, 15f), 2.1f, 2.1f), brush);

        ctx.Transform = saved;
    }
}
