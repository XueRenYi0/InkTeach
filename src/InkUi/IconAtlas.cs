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
}
