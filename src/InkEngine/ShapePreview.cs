using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 图形卡上的**真实墨迹小样**：用画布同一套 <see cref="Stroke"/> 几何造一条预览对象，
/// 再由 <see cref="Overlay"/> 用 <see cref="Overlay.DrawStroke"/> 的正式渲染路径画出来。
///
/// 为什么不再手绘图标：手绘只能近似；真实小样自带虚线、渐近线、焦点三角、3~6 边棱柱，
/// 和老师画到画布上的东西是同一条几何。预览对象在 100×100 的局部坐标里造一次，
/// 绘制时用矩阵缩到卡片，**线宽不跟着缩**（和画布上的图形同一条规则）。
/// </summary>
internal static class ShapePreview
{
    /// <summary>预览局部坐标的边长；卡片的图标区域按这个比例缩放。</summary>
    public const float Size = 100f;

    public static Stroke Create(ShapePreset p)
    {
        var s = new Stroke
        {
            Tool = p.Tool,
            Kind = InkEngine.KindOfShapeTool(p.Tool),
            Color = new Color4(0f, 0f, 0f, 1f),
            Width = 2.4f,
            Dash = p.Tool == Tool.Line ? (StrokeDash)p.Variant : StrokeDash.Solid,
            Grid = p.Tool == Tool.Coordinate && p.Variant == 0,
            CurveAxis = p.Tool == Tool.Parabola && p.Variant == 1
                ? CurveAxis.OpenRight : CurveAxis.OpenUp,
            PrismSides = p.Tool is Tool.Prism or Tool.Pyramid or Tool.Frustum
                ? p.Variant : Stroke.DefaultPrismSides,
            FocusTriangle = p.Tool != Tool.ConicEllipse || p.Variant == 0,
            ShowAsymptotes = p.Tool == Tool.Hyperbola && p.Variant == 0,
        };
        s.PreviewSmooth = true;
        s.PreviewFocusDots = p.Tool == Tool.ConicEllipse;

        const float pad = 12f;
        const float lo = pad, hi = Size - pad, mid = Size * 0.5f;
        switch (p.Tool)
        {
            case Tool.Line:
            case Tool.Arrow:
                s.AddPoint(lo, hi, 1f, 0);
                s.SetEnd(hi, lo);
                break;
            case Tool.Rectangle:
                s.AddPoint(lo, lo, 1f, 0);
                s.SetEnd(hi, hi);
                break;
            case Tool.Ellipse:
                s.AddPoint(mid, mid, 1f, 0);
                s.SetEnd(hi, mid + 22f);
                break;
            case Tool.ConicEllipse:
                s.AddPoint(mid, mid, 1f, 0);
                s.SetEnd(hi, mid + 22f);
                // 焦点三角形的顶点放在椭圆正上方：两条边对称地落在卡片里，
                // 不会被卡片边缘裁成一条斜线。
                s.SetConicEllipsePointAngle(-MathF.PI * 0.5f);
                break;
            case Tool.Circle:
                s.AddPoint(mid, mid, 1f, 0);
                s.SetEnd(hi, mid);
                break;
            case Tool.Triangle:
            case Tool.Parallelogram:
                s.AddPoint(lo, lo, 1f, 0);
                s.SetShapeBox(lo, lo, hi, hi);
                break;
            case Tool.Star:
                s.AddPoint(mid, mid, 1f, 0);
                s.SetStarFromCenter(mid, mid, hi, mid);
                break;
            case Tool.Trapezoid:
                s.AddPoint(lo, lo, 1f, 0);
                s.SetTrapezoidBox(lo, lo, hi, hi);
                break;
            case Tool.Sector:
                s.AddPoint(mid, mid, 1f, 0);
                s.SetSectorFromCenter(mid, mid, hi, mid, 90f);
                break;
            case Tool.Coordinate:
                s.AddPoint(mid, mid, 1f, 0);
                // 原点就是卡片中心，四条轴臂等长；不这么写会偏到右下角。
                s.SetAxisBox(mid, mid, hi, lo);
                // 小样里的箭头不能按画布线宽 5 倍算：卡片缩到 44px 后会变成大三角。
                s.PreviewArrowHeadLen = 8.5f;
                break;
            case Tool.Parabola:
                if (p.Variant == 0)
                {
                    s.AddPoint(mid, hi, 1f, 0);
                    s.SetParabolaVertex(mid, hi);
                    s.SetParabolaThroughPoint(hi, lo);
                }
                else
                {
                    s.AddPoint(lo, mid, 1f, 0);
                    s.SetParabolaVertex(lo, mid);
                    s.SetParabolaThroughPoint(hi, lo);
                }
                break;
            case Tool.Hyperbola:
                s.AddPoint(mid, mid, 1f, 0);
                s.SetHyperbolaFromAsymptote(mid, mid, hi, lo, 2f);
                // 经过点要**离开横轴**，否则 a = 到中心的距离、t = 0，两支会缩成两个小竖点。
                // 取靠近渐近线的点，让两支真正伸到卡片上下边，而不是中间两段小括号。
                s.SetHyperbolaThroughPoint(80f, 22f);
                s.PreviewAuxWidthScale = 0.5f;
                s.PreviewAuxAlpha = 0.75f;
                s.PreviewAsymptoteScale = 0.8f;
                break;
            case Tool.Sine:
            case Tool.Cosine:
            case Tool.Wave:
                s.AddPoint(lo, mid, 1f, 0);
                // 余弦的振幅定义是 |dy|/2，要和其他两条一样高，终点得多拉一倍。
                float waveEndY = p.Tool == Tool.Cosine ? mid - 64f : mid - 32f;
                s.SetWaveBox(lo, mid, hi, waveEndY, 2f);
                float cycles = p.Tool == Tool.Wave ? 2.5f : 1f;
                s.PreviewWaveCycles = cycles;
                // 波浪铺满图标宽：2.5 个周期压缩进同一个 76 宽的框，而不是把周期也画到框外。
                s.PreviewWavePeriod = (hi - lo) / cycles;
                s.PreviewStandardCosine = p.Tool == Tool.Cosine;
                break;
            case Tool.Tangent:
                s.AddPoint(mid, mid, 1f, 0);
                // 正切本来就被 3:1 的“最小高宽比”撑得很高；小样只留方一点的一段，
                // 上下剪断，两条渐近线留在框边，比例就和别的卡接近了。
                s.SetTangentBox(mid, mid, 82f, 18f, 2f);
                s.PreviewTangentHalfHeight = 32f;
                break;
            case Tool.Cylinder:
            case Tool.Cone:
            case Tool.ConeFrustum:
            case Tool.Sphere:
                s.AddPoint(20f, 14f, 1f, 0);
                s.SetSolidBox(20f, 14f, 80f, 86f);
                break;
            case Tool.Prism:
            case Tool.Pyramid:
            case Tool.Frustum:
                // 三兄弟是两笔定义：底面框 → 顶点；只给外框会变成一张平的多边形。
                s.AddPoint(20f, 86f, 1f, 0);
                s.SetPrismBase(20f, 70f, 80f, 86f);
                s.SetPrismApex(50f, 12f);
                break;
            default:
                s.AddPoint(lo, hi, 1f, 0);
                s.SetEnd(hi, lo);
                break;
        }
        return s;
    }
}
