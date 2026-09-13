using System.Numerics;

namespace InkEngine.Optimize;

/// <summary>
/// 笔迹优化器的默认实现，也就是原来那句"手写美化"。
///
/// 它做的四件事（顺序就是下面的执行顺序）：
///
///   1. **输入平滑**：1€ 滤波器。慢写时强去抖，快写时几乎不滤波。
///   2. **笔锋轮廓**：落笔时把采样点变成一圈闭合轮廓（速度→粗细、
///      起收笔渐细、圆头端帽、拐角圆弧）。这一步在
///      <see cref="StrokeBeautifier"/> 里，算法复刻自 perfect-freehand。
///   3. **抽稀 + 拟合**：RDP 压掉冗余点，再用三次贝塞尔拟合。
///      渲染用不上这条线（渲染用轮廓），它是给"以后要编辑顶点"和
///      自检报告留的：一笔到底被压成了几个数。
///   4. **外扩系数**：告诉引擎脏区要按多大的倍数留边，否则快速书写
///      会在笔画边缘留下没擦干净的残影。
///
/// 核心引擎完全不知道这些。不装这个类（<see cref="InkOptimizers.Current"/>
/// 保持 null），引擎画的就是最原始的采样点。
/// </summary>
internal sealed class InkBeautifier : IInkOptimizer
{
    private readonly OneEuroFilter _fx = new();
    private readonly OneEuroFilter _fy = new();

    /// <summary>
    /// 两个采样点之间的最小间距（**逻辑**像素）。原来的实现是按 0.7 逻辑
    /// 像素抽稀的：更密的点对形状没有贡献，只是白白占内存和渲染时间。
    /// 引擎会自己乘上 DPI 缩放。
    /// </summary>
    public float SampleStepLogicalPx => 0.7f;

    /// <summary>诊断用：算完轮廓后把拐角附近的点打到控制台。</summary>
    public bool DumpOutline;

    /// <summary>
    /// 轮廓可能超出名义笔宽的最大倍数，脏区与命中测试要用。
    ///
    /// 为什么是 1.45：速度模拟出来的压力会超过 1（连续慢写时压力累积），
    /// 加上圆头端帽和粗糙边缘，实测最宽的毛笔档会到名义宽度的约 1.43 倍。
    /// 取小了不是"笔迹看着细"，而是脏区算小了、快速书写留下残影。
    /// </summary>
    internal const float OutlineInflateFactor = 1.45f;

    public string LastReport { get; private set; }

    /// <summary>
    /// 新的一笔开始。必须复位滤波器：不复位的话，上一笔结束时的速度会被
    /// 当成这一笔的初速度，起笔处就会被滤波拖出一个疙瘩。
    /// </summary>
    public void BeginStroke(float x, float y)
    {
        _fx.Reset();
        _fy.Reset();
        // 用落笔点打底：不打底的话第一个移动采样会被当成初始值直接放行，
        // 起笔那一小段就等于没有滤波。
        _fx.Filter(x, 1.0 / 120);
        _fy.Filter(y, 1.0 / 120);
    }

    /// <summary>1€ 滤波。核心引擎只负责把坐标和间隔时间交进来。</summary>
    public void Smooth(ref float x, ref float y, double dtSeconds)
    {
        x = _fx.Filter(x, dtSeconds);
        y = _fy.Filter(y, dtSeconds);
    }

    /// <summary>
    /// 抬笔后处理。只对自由笔迹、且点数够多时生效——太短的笔画算轮廓
    /// 没有意义，反而容易被端帽和渐细捏变形。
    /// </summary>
    public void EndStroke(Stroke stroke, float dpiScale)
    {
        LastReport = null;
        if (stroke == null) return;
        if (stroke.Kind != StrokeKind.Freehand || stroke.Points.Count < 4) return;

        int rawCount = stroke.Points.Count;

        // ---- 笔锋轮廓 ----
        // 有真实压感就用压感，没有（鼠标、多数触摸屏）就用速度模拟压力，
        // 这样没有压感笔的设备也能写出粗细变化。
        bool hasPressure = HasRealPressure(stroke);
        var outline = StrokeBeautifier.BuildOutline(
            stroke.Points, hasPressure, stroke.Width,
            StrokeBeautifier.Preset(stroke.Preset), dpiScale);

        if (outline != null)
        {
            stroke.Outline = outline;
            stroke.BeautifiedWidths = StrokeBeautifier.LastWidths;
            stroke.BoundsInflateFactor = OutlineInflateFactor;

            if (DumpOutline) DumpCorner(outline);
        }

        // ---- 抽稀 + 拟合 ----
        // 渲染用的是上面那圈轮廓，这里的结果不参与绘制；它有两个用途：
        // 一是报告"这一笔被压成了几个数"，二是为将来"顶点可拖动"留接口。
        var simplified = Simplify.Rdp(stroke.Points, 0.5f * dpiScale);
        if (simplified.Count < 2) return;

        var verts = new List<Vector2>(simplified.Count);
        foreach (var p in simplified) verts.Add(new Vector2(p.X, p.Y));

        var curves = CurveFit.FitCurve(verts, 0.75f * dpiScale);
        if (curves.Count == 0) return;

        float maxErrorPx = MaxFitError(verts, curves);
        stroke.ReplacePoints(simplified);

        LastReport = $"{rawCount} 点 -> {stroke.Points.Count} 点 / "
                   + $"{curves.Count} 段贝塞尔 / "
                   + $"拟合最大偏差 {maxErrorPx:F2} px"
                   + $"（{maxErrorPx / dpiScale:F2} 逻辑像素）";
    }

    /// <summary>
    /// 输入是否带真实压感。判据是压力值有没有真的变化过：鼠标和多数触摸屏
    /// 上报的是恒定值（0.5 或 1），会被判成"没有压感"，从而改用速度模拟。
    /// </summary>
    private static bool HasRealPressure(Stroke s)
    {
        if (s.Points.Count < 3) return false;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in s.Points)
        {
            if (p.P < min) min = p.P;
            if (p.P > max) max = p.P;
        }
        return max - min > 0.02f;
    }

    /// <summary>
    /// 拟合出来的曲线离原始采样点最远有多少像素。这是个体检数字：
    /// 太大就说明所谓"平滑"其实是在扭曲原笔迹。
    /// </summary>
    private static float MaxFitError(List<Vector2> verts, List<Vector2[]> curves)
    {
        float worst = 0;
        foreach (var v in verts)
        {
            float best = float.MaxValue;
            foreach (var seg in curves)
            {
                // 按每 2 像素采样一次。取太少的话，量到的误差其实是采样
                // 本身的误差，会把好曲线判成坏的。
                float approx = Vector2.Distance(seg[0], seg[1])
                             + Vector2.Distance(seg[1], seg[2])
                             + Vector2.Distance(seg[2], seg[3]);
                int steps = Math.Clamp((int)MathF.Ceiling(approx / 2f), 8, 160);
                for (int i = 0; i <= steps; i++)
                {
                    float d2 = Vector2.DistanceSquared(CurveFit.Evaluate(seg, i / (float)steps), v);
                    if (d2 < best) best = d2;
                }
            }
            if (best > worst) worst = best;
        }
        return MathF.Sqrt(worst);
    }

    /// <summary>
    /// 把轮廓里拐角附近的点打到控制台。拐角在中心线的中途，看这一段点
    /// 就能判断"洞是不是轮廓自己就缺"，而不用猜栅格化。
    /// </summary>
    private static void DumpCorner(Vector2[] outline)
    {
        var corner = new Vector2(outline[0].X, outline[0].Y);
        Console.WriteLine($"  [轮廓] 共 {outline.Length} 点");
        foreach (var p in outline)
        {
            if (MathF.Abs(p.X - corner.X) < 60 && MathF.Abs(p.Y - corner.Y) < 60)
                Console.WriteLine($"    ({p.X - corner.X,7:F1},{p.Y - corner.Y,7:F1})");
        }
    }
}
