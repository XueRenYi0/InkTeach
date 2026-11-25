// [职责] Stroke 核心：点、颜色、粗细、变换、几何入口（含 InkPiece 嵌套类）。
// [测试] --unittest Serializer / --shapetest

using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>A single drawn item. Freehand strokes are filled ribbons so that
/// pressure can vary the width; shapes are stroked outlines.</summary>
internal sealed partial class Stroke
{
    public Tool Tool;
    public StrokeKind Kind = StrokeKind.Freehand;
    public Color4 Color;
    public float Width;

    /// <summary>
    /// 线型（见 <see cref="StrokeDash"/>）。默认实线。
    ///
    /// **刻意不进 <see cref="InvalidateMetrics"/> 的重算范围**：线型只改"怎么描这条边"，
    /// 不改形状本身，所以包围盒（<see cref="PaddedBounds"/>）一分都不变——
    /// 改线型只需要"旧样子标脏 + 新样子标脏"，几何和缓存都不用重建。
    /// 同理，**命中测试也照样按实线算**（见 <see cref="HitTest"/> 里的注释）。
    /// </summary>
    public StrokeDash Dash = StrokeDash.Solid;

    /// <summary>
    /// **坐标系要不要网格**（只有 <see cref="StrokeKind.Coordinate"/> 用得上，别的种类恒 false）。
    ///
    /// 为什么做成"跟着对象走"而不是"一个全局开关"：对象是**自包含**的——
    /// 存到文件、复制粘贴、发给别人，看到的都得是同一个样子；全局开关的话，
    /// 换台机器（或者改一次设置）整个板书上的坐标系就全变样了。
    /// 画的时候取当时的默认值（见 Engine 的坐标系网格开关），之后选中还能单独改。
    ///
    /// 注意它**改的是几何**（多了那些网格线），所以改它必须让几何缓存失效
    /// （见 SetStrokePropAction：Grid 走 InvalidateMetrics，线型不走）。
    /// </summary>
    public bool Grid;

    /// <summary>
    /// 网格线的**粗细系数**（乘在对象自己的 <see cref="Width"/> 上）。
    ///
    /// 用户 2026-09-24："把那个网格的线**变细变淡**一些"——网格是**底子**，
    /// 不该和两条轴抢眼：一条黑 3 像素的格线铺满整屏，比题目本身还显眼。
    ///
    /// 为什么非要单独一段几何：**一个几何只能有一种描边**（粗细 + 颜色 + 线型是一起给的），
    /// 所以"轴线一种、网格另一种"只能拆成两段——落点见 <see cref="BuildAuxGeometry"/>
    /// 与 `Overlay.DrawStroke` 的辅助几何那一段（同一个槽本来在装渐近线）。
    /// 熔墨（<see cref="MeltToInkParts"/>）也要按这两个系数还回去，不然"擦一下网格变粗"。
    /// </summary>
    internal const float AxisGridWidthFactor = 0.5f;

    /// <summary>网格线的**浓淡系数**（乘在 alpha 上）。理由同 <see cref="AxisGridWidthFactor"/>。</summary>
    internal const float AxisGridAlpha = 0.45f;

    /// <summary>把颜色按比例调淡（只动 alpha）。网格线用它。</summary>
    internal static Color4 Fade(Color4 c, float f) => new Color4(c.R, c.G, c.B, c.A * f);

    /// <summary>
    /// **曲线的朝向**（只有 <see cref="StrokeKind.Parabola"/> / <see cref="StrokeKind.Hyperbola"/>
    /// 用得上，别的种类恒为 <see cref="CurveAxis.OpenUp"/>）。
    ///
    /// 和 <see cref="Grid"/> 完全是同一类东西、也是同一条规矩：
    ///   · 它是**对象自己的样子**（不是全局开关）——存到文件、复制粘贴、发给别人都不该变；
    ///   · 它**改的是几何**（换一种开口，曲线走的完全是另一条路），所以改它必须让几何缓存
    ///     失效（走 <see cref="InvalidateMetrics"/>，和 Grid 一样）；
    ///   · 进存档（v11），老文件读进来是 0（= 开口向上 / 实轴沿 x），正是它们最可能的样子。
    /// </summary>
    public CurveAxis CurveAxis = CurveAxis.OpenUp;

    public readonly List<InkPoint> Points = new();

    /// <summary>
    /// 这一笔**有没有真实压感**（设备报压力、且点处于接触状态）。
    ///
    /// 为什么是"整笔的属性"而不是"看某个点的压力值"：`pressure == 0` 有两种含义
    /// （设备压根不报 / 真的写到 0），所以这件事只能由采集层按 `penMask` 判定
    /// （见 <see cref="PenSample.HasPressure"/> 的注释）。
    ///
    /// 它决定渲染走哪条路：**有压感 + 实线 → D2D 原生变宽墨迹**（见
    /// <c>Overlay.DrawPressureInk</c>）；否则走原来那条等宽描边（和 2026-09-14 之后一样）。
    ///
    /// **进存档（v10）**：不进的话"存盘再打开，压感笔迹就变等宽了"——那是这个功能最刺眼的
    /// 一种 bug（当场看是好的，重开就没了）。老文件读进来是 false（老文件本来也没存压感）。
    /// </summary>
    public bool HasPressure;

    /// <summary>
    /// **这一笔是"精确折线"**（2026-11-25，图形面积擦除零变形）：只有**图形熔出来的墨**
    /// 会置 true，普通手写笔迹恒 false。
    ///
    /// 为什么需要它：熔墨产物是一串"已经算好的、逐点等于原图折线"的点。它再走
    /// <see cref="StrokeMotion"/>（运动模型）或 <see cref="StrokeSmoothing"/>（过点曲线）
    /// 就会被**重新解释**——直角被圆角化、多边形收缩、大圆变多边形。
    /// 置 true 之后渲染**直接按原始折线描边**（见 `BuildCenterlineCore` 与
    /// `Overlay.DrawPressureInk`），并且收尖 / 抽稀都不参与，保证"熔完 = 擦前逐像素一致"。
    ///
    /// **进存档（v36）**：不进的话"擦一下图形、存盘重开又变形了"——最刺眼的那类 bug。
    /// 老文件读进来 false（老文件里没有熔墨产物这个概念）。
    /// </summary>
    public bool ExactPolyline;

    /// <summary>
    /// **正在写的那一笔：按原始折线画**（曲线只用在落笔之后，2026-09-28 用户实测后定的）。
    ///
    /// 为什么要有这个开关：过点曲线（centripetal Catmull-Rom）每来一个新采样点，
    /// 都要**回头重算最后一段**——切线要用到那个新点。于是笔尖后面 ~2 段（几十像素）
    /// 的墨每帧都在微微挪动，屏幕上看就是"一直在闪"（实测：折线在笔尖后 24px 以外
    /// 完全静止，曲线一直动到 46px）。**这是样条画法的固有性质，不是 bug**；
    /// 想要"画的时候一个像素都不许自己动"，就只能让活笔走折线。
    ///
    /// 代价：落笔那一刻形状会换成曲线。密采样（Windows Ink 的笔）下差别是亚像素级、
    /// 看不出来；鼠标/快画这种稀采样才看得出来——而那正是曲线真正有用的地方。
    /// </summary>
    internal bool RawWhileLive;

    /// <summary>
    /// 诊断（`--geomtrace`）：这一笔的几何**最后走了哪条路**，只打印一次。
    ///
    /// 立它是因为"快速画圆变折线"排查了两轮都靠读代码猜路径，结果两次猜错
    /// （改了两个开关，用户实测都没反应）。**路径必须由程序自己报**，不能靠读。
    /// 一个字符就够定性：
    ///   `A-模型+曲线` / `A-模型+折线` / `A-原始+曲线` / `A-原始+折线`
    ///   `B-模型+曲线` / `B-模型+折线` / `B-原始+曲线` / `B-原始+折线`
    /// （A = 等宽描边通道 `BuildCenterlineCore`；B = 压感墨迹通道 `BuildPressureSegments`）
    /// </summary>
    /// <summary>
    /// 诊断（`--geomtrace`）：已打印过的 (阶段 × 通道) 组合，位掩码。
    ///
    /// 必须是**按组合**去重，不能用单个标志：一笔有两条上屏通道，而
    /// `DrawStrokeCore` 总是**先**建通道 A 的几何、再试通道 B——单个标志会被
    /// 通道 A 先占掉，于是通道 B 那行永远打不出来（第二版就是这么漏的：
    /// 用户真机跑出来的 22 行全是 A，B 一条都没有，等于没看到真正上屏的那条）。
    ///   位0 = 活笔×A，位1 = 活笔×B，位2 = 落笔后×A，位3 = 落笔后×B
    /// </summary>
    internal int GeomTraceMask;


    /// <summary>
    /// **锁定**（2026-09-16 加，用户定的语义是"能选中、但拖不动"）：
    ///   · 照样能被点选 / 框选 / 套索 / 全选选中，框和手柄照画；
    ///   · 但拖动 / 缩放 / 旋转 / 翻转**跳过它**（选区里锁定的留在原地）；
    ///   · 两种橡皮、`Delete` 也跳过（橡皮和删除改的是内容，不是位置）；
    ///   · 改颜色 / 改粗细 / 复制 / 导出**不受影响**（那是样式与内容带走）。
    /// 见 调研-选中框-反馈-导出-层级-属性.md 第四节。
    /// </summary>
    public bool Locked;

    public ID2D1Geometry Geometry;

    // [删除 2026-10-05] `RenderTail`（预测渲染尾）、`PredictedTip`（预测笔尖）、
    // `_tailStamp/_builtTailStamp`（它们的几何缓存章）与两个 Set 方法：随老预测系统移除。
    // 原文见 `.revert/2026-10-05-渲染减法/`。

    /// <summary>
    /// 建几何时的"曲线化版本"。`--smoothshow` 出图 / 开关一拨时，`Revision` 没变，
    /// 缓存会把旧折线还回来，所以必须进缓存键。
    /// </summary>
    private int _builtSmoothVer = -1;
    /// <summary>建几何时这一笔是不是"正在写"。和曲线化版本同理，必须进缓存键：
    /// 落笔那一刻 `RawWhileLive` 由真变假，几何要跟着从折线换成曲线。</summary>
    private bool _builtRawLive;

    /// <summary>
    /// 建几何时的"墨迹模型版本"（<see cref="InkModel"/>）：模式/参数一拨，
    /// `Revision` 没变，缓存会把旧几何还回来。
    /// </summary>
    private int _builtInkModelVer = -1;

    /// <summary>
    /// 图像对象的像素（只有 <see cref="StrokeKind.Image"/> 有）。
    /// **核心只把它当"一块要画上去的图"**，不解释内容、不做解码
    /// （见 ImageData 的注释：来源是 GDI 截图或剪贴板 CF_DIB，都是 BGRA）。
    /// </summary>
    public ImageData Image;

    /// <summary>
    /// **AI 题卡内容**（只有 <see cref="StrokeKind.TextBox"/> 有）。
    /// 结构化数据，不是位图；宽/高、折叠状态、题目段落都跟着对象走，进存档/复制/撤销。
    /// </summary>
    public TextBoxData TextBox;

    /// <summary>
    /// **交互矢量图**（只有 <see cref="StrokeKind.Figure"/> 有）：
    /// 表达式/参数/圆锥参数；渲染走 <c>Overlay.DrawFigure</c>，不进位图。
    /// </summary>
    public PlotSpec Figure;

    /// <summary>是不是图像对象。渲染、命中测试、顶点编辑三处都要按它分流。</summary>
    public bool IsImage => Kind == StrokeKind.Image;

    /// <summary>
    /// **被擦掉的参数区间**（参数 = 点序号，可以带小数；空表 = 整条完好）。
    ///
    /// 2026-09-15 从"把笔迹拆成几个新对象"改成"在一条笔迹上记区间"，四个理由：
    ///   ① **自相重叠的墨不会混合两次**：剩下的段还在同一条几何里，一次
    ///      `DrawGeometry` 只混合一次。拆成两个对象就会混合两次——半透明荧光笔
    ///      在自交处会变深（离屏实测：差 0 → 56）；
    ///   ② **对象数不涨**：拆对象时，反复擦同一笔会让对象线性增长
    ///      （一万笔的画面上 25 步切出 3123 个）；
    ///   ③ **身份不变**：`Id` 不换，选中 / 变换 / 复制粘贴的行为完全不变；
    ///   ④ 区间记在**参数**上，与变换无关——拆对象时得把变换烘进画布坐标。
    ///
    /// 代价（明确接受）：几何缓存要按区间重拼（figure 数与"拆出来的段数"同量级）；
    /// 命中测试要跳过被擦的段（见 <see cref="DistanceTo"/>）。
    ///
    /// 约定：**始终有序、不相邻**（<see cref="AddErased"/> 负责合并）。
    /// </summary>
    public readonly List<(float a, float b)> Erased = new();

    /// <summary>最后一个点的参数（参数 = 点序号）。</summary>
    public float LastParam => Points.Count > 1 ? Points.Count - 1 : 0f;

    public bool HasErased => Erased.Count > 0;

    /// <summary>
    /// 加一段被擦掉的参数区间（自动裁剪到 [0, LastParam] 并与相邻区间合并）。
    /// 返回是否真的改变了什么。
    /// </summary>
    public bool AddErased(float a, float b)
    {
        float lo = MathF.Min(a, b), hi = MathF.Max(a, b);
        lo = Math.Clamp(lo, 0f, LastParam);
        hi = Math.Clamp(hi, 0f, LastParam);
        if (MergeInterval(Erased, lo, hi))
        {
            Revision++;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 把一段区间并进一张"有序、互不相邻"的区间表（就地改）。返回是否真的变了。
    ///
    /// 抽成静态是为了让像素橡皮能先在**副本**上算好结果再决定要不要动对象——
    /// 直接改原对象的话，"其实没变化"也会把几何缓存打掉、把块标记成要重画。
    /// </summary>
    public static bool MergeInterval(List<(float a, float b)> list, float lo, float hi)
    {
        // 零长度区间 = 什么都没擦掉（参数的 1 就是相邻两个采样点的间隔）。
        if (hi - lo < 1e-3f) return false;

        // 合并判定的余量：参数 1 = 相邻两个采样点的间隔，1e-3 换到屏幕上是千分之一个
        // 采样间距——远小于一个像素。
        const float eps = 1e-3f;

        int from = 0;
        while (from < list.Count && list[from].b < lo - eps) from++;

        float nlo = lo, nhi = hi;
        int to = from;                       // [from, to) 是要被吃掉的旧区间
        while (to < list.Count && list[to].a <= nhi + eps)
        {
            nlo = MathF.Min(nlo, list[to].a);
            nhi = MathF.Max(nhi, list[to].b);
            to++;
        }

        // 被一个已有区间完整包住 → 状态没变。
        if (to - from == 1 && list[from].a == nlo && list[from].b == nhi) return false;

        list.RemoveRange(from, to - from);
        list.Insert(from, (nlo, nhi));
        return true;
    }

    /// <summary>给一张区间表算"还剩下哪些连续段"（<see cref="RemainingRuns"/> 的静态版）。</summary>
    public static List<(float a, float b)> RemainingRunsOf(
        List<(float a, float b)> list, float lastParam)
    {
        var runs = new List<(float a, float b)>();
        float cursor = 0f;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].a > cursor + 1e-4f) runs.Add((cursor, list[i].a));
            cursor = MathF.Max(cursor, list[i].b);
        }
        if (cursor < lastParam - 1e-4f) runs.Add((cursor, lastParam));
        return runs;
    }

    /// <summary>整表替换（撤销 / 重做 / 反序列化用）。</summary>
    public void SetErased(List<(float a, float b)> src)
    {
        Erased.Clear();
        if (src != null) Erased.AddRange(src);
        Revision++;
    }

    /// <summary>参数 t 是否落在被擦掉的区间里。</summary>
    public bool IsParamErased(float t)
    {
        for (int i = 0; i < Erased.Count; i++)
            if (t >= Erased[i].a && t <= Erased[i].b) return true;
        return false;
    }

    /// <summary>
    /// 还剩下的连续段（参数区间），按顺序。空 = 整条都被擦没了。
    /// 渲染按它拼几何，撤销 / 命中测试也读它。
    /// </summary>
    public List<(float a, float b)> RemainingRuns()
    {
        var runs = new List<(float a, float b)>();
        // 单点笔迹（一个圆点）：参数只有一个 0。它没有"长度"可分，所以要么完整
        // 保留、要么整条被擦掉（后者由像素橡皮那边直接判定并删除，见 EraseRectAt）。
        if (Points.Count <= 1)
        {
            if (Erased.Count == 0) runs.Add((0f, 0f));
            return runs;
        }
        return RemainingRunsOf(Erased, LastParam);
    }

    /// <summary>参数 t 处的坐标（在两个采样点之间线性插值）。</summary>
    public Vector2 PointAtParam(float t)
    {
        int n = Points.Count;
        if (n == 0) return default;
        if (n == 1) return new Vector2(Points[0].X, Points[0].Y);
        float c = Math.Clamp(t, 0f, n - 1);
        int i = (int)MathF.Floor(c);
        if (i >= n - 1) return new Vector2(Points[n - 1].X, Points[n - 1].Y);
        float f = c - i;
        return new Vector2(
            Points[i].X + (Points[i + 1].X - Points[i].X) * f,
            Points[i].Y + (Points[i + 1].Y - Points[i].Y) * f);
    }

    /// <summary>参数 t 处的压力（拆段时给新段的端点用，别把压感丢掉）。</summary>
    public float PressureAtParam(float t)
    {
        int n = Points.Count;
        if (n == 0) return 0.5f;
        if (n == 1) return Points[0].P;
        float c = Math.Clamp(t, 0f, n - 1);
        int i = (int)MathF.Floor(c);
        if (i >= n - 1) return Points[n - 1].P;
        float f = c - i;
        return Points[i].P + (Points[i + 1].P - Points[i].P) * f;
    }

    /// <summary>参数 t 处的时间戳（同上）。</summary>
    public double TimeAtParam(float t)
    {
        int n = Points.Count;
        if (n == 0) return 0;
        if (n == 1) return Points[0].T;
        float c = Math.Clamp(t, 0f, n - 1);
        int i = (int)MathF.Floor(c);
        if (i >= n - 1) return Points[n - 1].T;
        float f = c - i;
        return Points[i].T + (Points[i + 1].T - Points[i].T) * f;
    }

    /// <summary>
    /// 把这一条按**剩下的段**拆成几个新对象（局部坐标 + 原变换，样式继承）。
    /// 只有"用户要单独摆弄某一段"时才调用，见 <see cref="InkDocument.SplitErasedSelection"/>。
    /// </summary>
    public List<Stroke> SplitIntoRuns()
    {
        var parts = new List<Stroke>();
        foreach (var (a, b) in RemainingRuns())
        {
            var p = new Stroke
            {
                Tool = Tool, Kind = StrokeKind.Freehand,
                Color = Color, Width = Width, Transform = Transform,
                // 线型也算"样式"，跟着继承（2026-09-19：自由笔迹有线型之后，
                // 虚线笔迹被拆开的两截必须还是虚线，否则"拆完样子变了"）。
                Dash = Dash,
                // **精确折线也要跟着继承**：图形熔出来的墨在"擦完 → 按剩下段拆开"之后
                // 仍然必须走原始折线，否则拆完这一步又会被手写平滑重新解释一遍
                //（2026-11-25 面积擦除零变形：这里漏一位，圆/椭圆擦完照样变形）。
                ExactPolyline = ExactPolyline,
            };
            var start = PointAtParam(a);
            p.AddPoint(start.X, start.Y, PressureAtParam(a), TimeAtParam(a));
            for (int i = 1; i < Points.Count; i++)
            {
                if (i < a - 1e-6f) continue;
                if (i > b + 1e-6f) break;
                p.AddPoint(Points[i].X, Points[i].Y, Points[i].P, Points[i].T);
            }
            var end = PointAtParam(b);
            p.AddPoint(end.X, end.Y, PressureAtParam(b), TimeAtParam(b));
            parts.Add(p);
        }
        return parts;
    }

    /// <summary>
    /// 熔成墨的时候的**一条折线**：点列 ＋ "它是不是辅助线"。
    ///
    /// 辅助线（双曲线的渐近线、立体图形被挡住的那半圈 / 那几条棱）在屏幕上恒定是
    /// **细虚线**（见 <see cref="BuildAuxGeometry"/> 与 Overlay 里那条 0.6 倍粗细），
    /// 所以熔成墨也得带着这个身份——不然就是用户 2026-09-20 反馈的
    /// "擦一下**虚线变实线**"。
    /// </summary>
    public sealed class InkPiece
    {
        public readonly List<Vector2> Pts;
        public readonly bool Aux;

        /// <summary>
        /// **坐标系网格线**（用户 2026-09-24："网格的线变细变淡"）。
        /// 它和辅助线一样"不参与命中"（见 <see cref="ShapeOutline"/>：轮廓里只列两条轴线），
        /// 但画法不同——网格是**细一半、淡一半的实线**（辅助线是细虚线），
        /// 系数见 <see cref="AxisGridWidthFactor"/> / <see cref="AxisGridAlpha"/>。
        /// </summary>
        public readonly bool Grid;

        /// <summary>
        /// **单点装饰**（圆心的那个小圆点）：> 0 = 这一笔不是折线，而是一个半径这么大的实心点。
        /// 熔墨时它要变成一条**单点笔迹**（<see cref="MeltToInkParts"/>），
        /// 否则"擦一下圆心点就没了"（2026-11-25 面积擦除零变形）。
        /// </summary>
        public readonly float DotRadius;

        /// <summary>这一笔是不是单点圆点（见 <see cref="DotRadius"/>）。</summary>
        public bool IsDot => DotRadius > 0.5f;

        public InkPiece(List<Vector2> pts, bool aux) : this(pts, aux, false, 0f) { }

        public InkPiece(List<Vector2> pts, bool aux, bool grid) : this(pts, aux, grid, 0f) { }

        public InkPiece(List<Vector2> pts, bool aux, bool grid, float dotRadius)
        {
            Pts = pts; Aux = aux; Grid = grid; DotRadius = dotRadius;
        }
    }

    /// <summary>
    /// 把**图形**熔成墨（**一组**笔迹，不是一个）：每一笔取它自己的一段折线，
    /// 坐标换算到画布、变换归一。
    ///
    /// 为什么：用户 2026-09-15 定"橡皮擦中图形，断开了也要单独算"——图形是参数化对象
    /// （两个端点 / 三个控制点），不先变成点列就没法"擦掉中间、剩下两截"。
    /// 熔完就走和墨迹一模一样的那条路（区间擦除那套）。
    ///
    /// **为什么必须是一组**（2026-09-20 用户反馈"擦一下多出来线 / 虚线变实线"）：
    /// 原来是把轮廓（含抬笔标记）**忽略标记压成一条**再熔，于是
    ///   · 段与段之间被连起来 → 双曲线两支之间、长方体各条棱之间凭空多出线；
    ///   · 辅助线（渐近线、被挡的半圈）根本不在轮廓里 → 虚线消失，或（圆柱底面）
    ///     以实线画回来。
    /// 现在按"画出来的每一笔"分开熔（见 <see cref="InkPieces"/>），外形与擦之前逐笔一致。
    ///
    /// 代价（明确接受）：熔完它**不再是图形**——没有顶点手柄、不能改形状参数。
    /// 这一步靠撤销回退（原图形整个进撤销栈，见 EraseIntervalsAction.Item.UndoOriginal）。
    /// </summary>
    public List<Stroke> MeltToInkParts(float dotUnit = 1f)
    {
        var list = new List<Stroke>();
        foreach (var piece in InkPieces(dotUnit))
        {
            if (piece.IsDot)
            {
                // **单点装饰**（圆心点）：折线不足两点，但它必须留下来——不然"擦一下圆心没了"。
                // 单点笔迹的渲染半径 = Width × 0.5（见 RendersAsDot / DotRadius），
                // 所以宽度取直径即可和原来的实心小圆一致。
                var m = new Stroke
                {
                    Tool = Tool, Kind = StrokeKind.Freehand,
                    Color = Color,
                    Width = MathF.Max(1f, piece.DotRadius * 2f),
                    ExactPolyline = true,
                    HasPressure = false,
                };
                var q = Transform.IsIdentity ? piece.Pts[0] : Vector2.Transform(piece.Pts[0], Transform);
                m.AddPoint(q.X, q.Y, 1f, 0);
                list.Add(m);
                continue;
            }
            if (piece.Pts.Count < 2) continue;
            var m2 = new Stroke
            {
                Tool = Tool, Kind = StrokeKind.Freehand,
                // 网格线熔成墨之后也**得是细的淡的**（见 AxisGridWidthFactor / AxisGridAlpha）：
                // 不然"擦一下坐标系，网格变粗变黑"——和辅助线那条是同一类毛病。
                Color = piece.Grid ? Fade(Color, AxisGridAlpha) : Color,
                // 辅助线在屏幕上就是 0.6 倍粗细的细虚线（Overlay.DrawStroke 那个 Max(1, Width*0.6)）。
                Width = piece.Aux ? MathF.Max(1f, Width * 0.6f)
                      : piece.Grid ? MathF.Max(1f, Width * AxisGridWidthFactor)
                      : Width,
                // 线型跟过来：虚线图形熔成笔迹之后还得是虚线（见 SplitIntoRuns 那条同一个理由）。
                // 网格线**恒定实线**（渲染时本来就是 `StyleFor(Solid)`，见 Overlay 的辅助几何那段）。
                Dash = piece.Grid ? StrokeDash.Solid : piece.Aux ? StrokeDash.Dashed : Dash,
                // **精确折线**：熔出来的点就是原图的点，绝不再交给手写平滑 / 运动模型 / 笔锋
                // 重新解释，否则直角会变圆、大圆会变多边形（2026-11-25 用户反馈）。
                ExactPolyline = true,
                HasPressure = false,
            };
            foreach (var p in piece.Pts)
            {
                var q = Transform.IsIdentity ? p : Vector2.Transform(p, Transform);
                m2.AddPoint(q.X, q.Y, 1f, 0);
            }
            list.Add(m2);
        }
        return list;
    }

    /// <summary>
    /// 这个对象**画在屏幕上的每一笔**（局部坐标，逐段）。
    ///
    /// 它和 <see cref="ShapeOutline"/> 的分工**不能互相顶替**：
    ///   · `ShapeOutline` 只服务"碰到没有"（命中、套索、像素橡皮的粗筛）。为了命中方便，
    ///     它**故意**画得比屏幕上多、少——圆柱底面列整圈（实线半圈与虚线半圈都在里面），
    ///     坐标系只列两条轴线（网格、箭头都不列）；
    ///   · 这一份是"擦之前屏幕上有几笔，熔完就得有几笔"，所以实 / 虚分开、箭头网格不能漏。
    ///     （图形工具计划 §25：漏一处就是"擦一下少几笔"。）
    /// </summary>
    public List<InkPiece> InkPieces(float dotUnit = 1f)
    {
        var list = new List<InkPiece>();

        switch (Kind)
        {
            // ---- 圆：轮廓 ＋ **圆心点**（墨迹本身，见 Overlay.DrawShapeCenterInk）----
            // 圆心点在屏幕上是个半径约 1.25 逻辑像素的实心小圆，不随圆半径缩放；
            // 熔墨要把它变成单点笔迹，否则"面积橡皮一碰，圆心就没了"。
            case StrokeKind.Circle:
            {
                if (Points.Count < 2) break;
                foreach (var part in SplitOutlineParts(ShapeOutlineCore(exact: true)))
                    list.Add(new InkPiece(part, false));
                list.Add(new InkPiece(new List<Vector2> { ShapeCenterLocal }, false, false,
                                     1.25f * MathF.Max(0.5f, dotUnit)));
                break;
            }

            // ---- 椭圆（带焦点）：椭圆本体 ＋ 焦点三角形 ＋ 两个焦点小圆 ----
            // 和渲染读同一份（<see cref="ConicEllipsePieces"/>），漏一笔就是"擦一下少一笔"。
            case StrokeKind.ConicEllipse:
                if (Points.Count < 2) break;
                list.AddRange(ConicEllipsePieces());
                break;

            // ---- 圆柱 / 圆锥 / 圆台 / 球：可见的几笔 ＋ 被挡住的那半圈（细虚线）----
            case StrokeKind.Cylinder:
            case StrokeKind.Cone:
            case StrokeKind.ConeFrustum:
            case StrokeKind.Sphere:
                if (Points.Count < 2) break;
                list.AddRange(SolidPieces(hidden: false));
                list.AddRange(SolidPieces(hidden: true));
                break;

            // ---- 长方体：九条看得见的棱 ＋ 三条被挡住的棱（细虚线）----
            case StrokeKind.Cuboid:
                if (Points.Count < 3) break;
                list.AddRange(CuboidEdges(hidden: false));
                list.AddRange(CuboidEdges(hidden: true));
                break;

            // ---- 棱柱 / 棱锥 / 棱台：看得见的棱 ＋ 被挡住的（细虚线）——实虚由通用判据分
            //      （见 PrismFamilyEdges）。三兄弟共用这一支。
            case StrokeKind.Prism:
            case StrokeKind.Pyramid:
            case StrokeKind.Frustum:
                if (Points.Count < 2) break;
                list.AddRange(PrismFamilyEdges(hidden: false));
                list.AddRange(PrismFamilyEdges(hidden: true));
                break;

            // ---- 四面体：六条棱，全实线 ----
            case StrokeKind.Tetrahedron:
                if (Points.Count < 3) break;
                list.AddRange(TetraEdges());
                break;

            // ---- 坐标系 / 数轴 / 空间坐标系：轴线 ＋ 箭头（＋ 网格）----
            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
            case StrokeKind.SpaceCoordinate:
                if (Points.Count < 2) break;
                list.AddRange(AxisPieces());
                break;

            // ---- 正切：一支（一条开曲线）＋ 两条竖直渐近线（细虚线）----
            //      实虚和双曲线同一个排法：曲线走主几何、渐近线走辅助几何。
            case StrokeKind.Tangent:
                if (Points.Count < 2) break;
                foreach (var part in SplitOutlineParts(ShapeOutlineCore(exact: true)))
                    list.Add(new InkPiece(part, false));
                for (int side = -1; side <= 1; side += 2)
                {
                    var (from, to) = TangentAsymptoteLocal(side);
                    list.Add(new InkPiece(new List<Vector2> { from, to }, true));
                }
                break;

            // ---- 双曲线：两支（中间有抬笔）＋ 两条渐近线（细虚线）----
            case StrokeKind.Hyperbola:
                if (Points.Count < 2) break;
                foreach (var part in SplitOutlineParts(ShapeOutlineCore(exact: true)))
                    list.Add(new InkPiece(part, false));
                if (ShowAsymptotes)
                {
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var (from, to) = HyperbolaAsymptoteLocal(side);
                        list.Add(new InkPiece(new List<Vector2> { from, to }, true));
                    }
                }
                break;

            // ---- 其余（直线 / 箭头 / 矩形 / 椭圆 / 圆 / 三角形 / 平行四边形 /
            //      抛物线 / 正弦 / 余弦 / 波浪线）：轮廓本来就是"画出来的那一条"，
            //      按抬笔分段即可 ----
            default:
                foreach (var part in SplitOutlineParts(ShapeOutlineCore(exact: true)))
                    list.Add(new InkPiece(part, false));
                break;
        }
        return list;
    }

    /// <summary>
    /// 把一份轮廓折线按**抬笔标记**（<see cref="OutlineBreak"/>）切成几段。
    /// 抬笔标记本身不进结果（它的坐标是 NaN，写进点列会把整条笔迹的几何变成 NaN——
    /// 那一笔画不出来、点不中，包围盒也会被污染）。
    /// </summary>
    public static List<List<Vector2>> SplitOutlineParts(List<Vector2> outline)
    {
        var parts = new List<List<Vector2>>();
        var cur = new List<Vector2>();
        foreach (var p in outline)
        {
            if (IsOutlineBreak(p))
            {
                if (cur.Count > 0) { parts.Add(cur); cur = new List<Vector2>(); }
                continue;
            }
            cur.Add(p);
        }
        if (cur.Count > 0) parts.Add(cur);
        return parts;
    }

    /// <summary>
    /// **精确采样的弦高误差上限**（逻辑像素）：0.20 = 远小于一个像素，
    /// 熔出来的大圆 / 椭圆与 D2D 原生曲线在屏幕上分辨不出差别。
    /// </summary>
    internal const float ExactChordErrorPx = 0.20f;

    /// <summary>
    /// **自适应圆弧段数**：按"弦高误差 ≤ <see cref="ExactChordErrorPx"/> 像素"反算，夹在 [24,180]。
    ///
    /// 公式：`n = ceil(span / (2·acos(1 − e/r)))`（`e` = 允许的弦高，`span` = 圆心角）。
    /// 例：整圆 `span = 2π`、`r = 300px` → **约 86 段**，每段弦长 22px、弦高 0.2px。
    ///
    /// 纯静态、不读对象：自检直接钉它（`--unittest` / `--shapemelttest`）。
    /// </summary>
    internal static int AdaptiveArcSegments(float maxRadius, float spanRad)
    {
        if (!(maxRadius > 0.5f) || !(spanRad > 1e-4f)) return 24;
        float ratio = 1f - ExactChordErrorPx / maxRadius;
        if (ratio >= 1f) return 24;
        if (ratio <= -1f) return 180;
        float half = MathF.Acos(ratio);
        if (!(half > 1e-4f)) return 180;
        int n = (int)MathF.Ceiling(spanRad / (2f * half));
        return Math.Clamp(n, 24, 180);
    }

    /// <summary>
    /// <see cref="Transform"/> 的**近似缩放**（取两个基向量里较长的一条）。
    ///
    /// 精确采样要按**屏幕上的大小**算段数：图形被放大 3 倍之后，同样 48 段的圆
    /// 在屏幕上的弦高也放大 3 倍，看着就是多边形。用 1 兜底（缩小不减少段数）。
    /// </summary>
    private float TransformScaleEstimate
    {
        get
        {
            var t = Transform;
            float sx = MathF.Sqrt(t.M11 * t.M11 + t.M12 * t.M12);
            float sy = MathF.Sqrt(t.M21 * t.M21 + t.M22 * t.M22);
            return MathF.Max(1f, MathF.Max(sx, sy));
        }
    }

    /// <summary>
    /// 曲线（抛物线 / 双曲线）精确采样的段数：按横向跨度每 1.5px 一段，
    /// 夹在 [96, 480]。曲线不像圆有确定半径，用"跨度密度"是保守做法——
    /// 跨度 500px 时 334 段，弦高远小于 0.2px。
    /// </summary>
    private static int ExactCurveSegments(float extent)
        => Math.Clamp((int)MathF.Ceiling(extent / 1.5f), 96, 480);

    /// <summary>椭圆上一段弧的采样点列（`u0 → u1`）：**立体图形的实线几何、虚线半圈、
    /// 以及熔墨三处共用这一份点列**（渲染与熔出来的逐点相同）。
    ///
    /// 段数取"画法需要的段数"与"弦高 ≤ 0.2px 的自适应段数"的**较大者**，
    /// 并按 <see cref="Transform"/> 的缩放加密（放大过的圆熔墨不能发糙）。</summary>
    private List<Vector2> ArcPoints(Vector2 c, float rx, float ry, float u0, float u1)
    {
        float maxR = MathF.Max(rx, ry);
        int n = PreviewSmooth
            ? Math.Clamp((int)(maxR / 2f), 64, 160)
            : Math.Clamp((int)(maxR / 4f), 12, 48);
        n = Math.Max(n, AdaptiveArcSegments(maxR * TransformScaleEstimate,
                                            MathF.Abs(u1 - u0) * MathF.Tau));
        var pts = new List<Vector2>(n + 1);
        for (int i = 0; i <= n; i++)
            pts.Add(EllipseArcPoint(c, rx, ry, u0 + (u1 - u0) * i / n));
        return pts;
    }

    /// <summary>把几段折线拼成一个 D2D 几何：**每段一条 figure**（互不相连）。
    /// 渲染与熔共用同一份点列，见 <see cref="InkPieces"/>。</summary>
    private static ID2D1PathGeometry FromPieces(ID2D1Factory1 factory, List<InkPiece> pieces)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        foreach (var pc in pieces)
        {
            if (pc.Pts.Count < 2) continue;
            sink.BeginFigure(pc.Pts[0], FigureBegin.Hollow);
            for (int i = 1; i < pc.Pts.Count; i++) sink.AddLine(pc.Pts[i]);
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 几何包围盒，**局部坐标**（对象自己的坐标系，不看 Transform）。
    ///
    /// 要"这个对象在画布上占多大地方"用 <see cref="WorldBounds"/>。
    /// 这两个概念分开是刻意的：变换一变，局部包围盒不该跟着变。
    /// </summary>
    public RectF Bounds = RectF.Empty;

    /// <summary>
    /// 稳定身份，**只增不减、永不复用**。撤销 / 多选 / 复制 / 序列化都靠它：
    /// 用引用相等做不了（复制会产生新对象），用列表下标也不行（删除会错位）。
    /// 0 表示"还没分配"，由文档在入册时给。
    /// </summary>
    public int Id;

    /// <summary>
    /// 局部坐标 → 画布坐标。**移动 / 缩放 / 旋转 / 镜像都只改这个矩阵，
    /// 几何一个点都不动。**
    ///
    /// 这是这一层最重要的约定，它带来三件事：
    ///   ① 变换是 O(1)，跟笔画有多少个点无关；
    ///   ② GPU 几何缓存不用失效（缓存的是局部坐标下的几何）；
    ///   ③ "放大再缩小"能精确回到原样，不会累积浮点损失。
    ///
    /// 反过来做（把变换烘焙进点坐标）会自动继承 InkClass 的两个毛病：
    /// 每次变换都要重写全部点、而且缩放两次之后形状就回不去了。
    ///
    /// 默认单位矩阵：几何直接写在画布坐标里（现存数据就是这个状态）。
    /// </summary>
    public Matrix3x2 Transform = Matrix3x2.Identity;

    private RectF _worldBounds = RectF.Empty;
    private int _worldBoundsRevision = -1;
    private Matrix3x2 _worldBoundsTransform = Matrix3x2.Identity;

    /// <summary>
    /// 画布坐标下的包围盒 = 局部包围盒经 <see cref="Transform"/> 变换之后。
    ///
    /// **脏区、命中测试、空间索引、框选一律用它**，不要用 <see cref="Bounds"/>。
    ///
    /// 两个容易写错的地方：
    ///   ① 非等比缩放 / 旋转 / 镜像之后**不能只变换两个角点**——那得到的是
    ///      "以对角线为边的矩形"，会偏小，脏区就会留下残影。这里变换四个角取并集。
    ///   ② 它有缓存。拖动时每帧都会问它，不能每次都算。
    /// </summary>
    public RectF WorldBounds
    {
        get
        {
            if (_worldBoundsRevision == Revision && _worldBoundsTransform.Equals(Transform))
                return _worldBounds;

            _worldBounds = TransformRect(Bounds, Transform);
            _worldBoundsRevision = Revision;
            _worldBoundsTransform = Transform;
            return _worldBounds;
        }
    }

    /// <summary>把矩形按矩阵变换后取四个角的并集（旋转 / 镜像 / 非等比都正确）。</summary>
    private static RectF TransformRect(RectF r, in Matrix3x2 m)
    {
        if (r.IsEmpty) return RectF.Empty;
        if (m.IsIdentity) return r;

        var p0 = Vector2.Transform(new Vector2(r.MinX, r.MinY), m);
        var p1 = Vector2.Transform(new Vector2(r.MaxX, r.MinY), m);
        var p2 = Vector2.Transform(new Vector2(r.MaxX, r.MaxY), m);
        var p3 = Vector2.Transform(new Vector2(r.MinX, r.MaxY), m);

        var box = RectF.Empty;
        box.Add(p0.X, p0.Y);
        box.Add(p1.X, p1.Y);
        box.Add(p2.X, p2.Y);
        box.Add(p3.X, p3.Y);
        return box;
    }

    /// <summary>
    /// 三角形 / 平行四边形的**画法**：由"拖出来的外框"（按下的点 + 当前指针，
    /// 两个对角点的**顺序随意**，内部先归一化）一次算出三个控制点。
    ///
    /// 每帧都要调（拖动预览），所以既不分配、也不走 <see cref="SetPoints"/>
    /// ——那个要造一个数组。规则就是规格 9.4 / 9.5 那两句：
    ///   · **三角形**：上中 ＋ 下左 ＋ 下右（底边水平、左右对称）；
    ///   · **平行四边形**：底边水平、上边右移 1/4 宽。右移取"底边两端各让出 1/4"
    ///     （而不是整条上边往右挪出框外），于是**外框正好是它的包围盒**，
    ///     松手那一瞬的紧框和用户拖出来的那个框重合，不会"松手就长大一截"。
    /// </summary>
    public void SetShapeBox(float x0, float y0, float x1, float y1)
    {        // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到三个（控制点数固定）。
        while (Points.Count < 3) AddPoint(x0, y0, 1f, 0);

        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        if (Kind == StrokeKind.Triangle)
        {
            SetPoint(0, new Vector2((minX + maxX) * 0.5f, minY));   // 上中
            SetPoint(1, new Vector2(minX, maxY));                   // 下左
            SetPoint(2, new Vector2(maxX, maxY));                   // 下右
        }
        else
        {
            float shift = (maxX - minX) * 0.25f;                    // 上边右移 1/4 宽
            SetPoint(0, new Vector2(minX, maxY));                   // 底左
            SetPoint(1, new Vector2(maxX - shift, maxY));           // 底右
            SetPoint(2, new Vector2(minX + shift, minY));           // 顶左
        }
    }

    /// <summary>五角星：中心 + 外半径，生成 10 个外/内顶点（正五角星）。</summary>
    public void SetStarFromCenter(float cx, float cy, float x, float y)
    {
        float r = MathF.Max(2f, Vector2.Distance(new Vector2(cx, cy), new Vector2(x, y)));
        var pts = new Vector2[10];
        for (int i = 0; i < pts.Length; i++)
        {
            float a = -MathF.PI * 0.5f + i * MathF.PI / 5f;
            float rr = (i & 1) == 0 ? r : r * 0.382f;   // 正五角星内外半径比
            pts[i] = new Vector2(cx + MathF.Cos(a) * rr, cy + MathF.Sin(a) * rr);
        }
        SetPoints(pts);
    }

    /// <summary>梯形：从外框生成默认等腰梯形（上底 = 下底的一半、居中）。</summary>
    public void SetTrapezoidBox(float x0, float y0, float x1, float y1)
    {
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        float cx = (minX + maxX) * 0.5f;
        float halfTop = (maxX - minX) * 0.25f;
        SetPoints(new[]
        {
            new Vector2(minX, maxY), new Vector2(maxX, maxY),
            new Vector2(cx + halfTop, minY), new Vector2(cx - halfTop, minY),
        });
    }

    /// <summary>
    /// 扇形：圆心 + 起始半径端点 + 默认张角。第三点存圆弧端点，
    /// 半径和张角都由三个点现算，不另存字段。
    /// </summary>
    public void SetSectorFromCenter(float cx, float cy, float x, float y, float sweepDegrees = 90f)
    {
        float r = MathF.Max(2f, Vector2.Distance(new Vector2(cx, cy), new Vector2(x, y)));
        float a = MathF.Atan2(y - cy, x - cx);
        float sweep = Math.Clamp(sweepDegrees, 5f, 359f) * MathF.PI / 180f;
        var c = new Vector2(cx, cy);
        SetPoints(new[]
        {
            c,
            c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r,
            c + new Vector2(MathF.Cos(a + sweep), MathF.Sin(a + sweep)) * r,
        });
    }

    public float SectorRadiusLocal()
        => Points.Count >= 2
            ? Vector2.Distance(new Vector2(Points[0].X, Points[0].Y),
                               new Vector2(Points[1].X, Points[1].Y))
            : 0f;

    public float SectorStartAngleLocal()
        => Points.Count >= 2
            ? MathF.Atan2(Points[1].Y - Points[0].Y, Points[1].X - Points[0].X)
            : 0f;

    /// <summary>张角（度，0~359）；按“从起始半径顺时针转到圆弧端点”算。</summary>
    public float SectorSweepDegrees()
    {
        if (Points.Count < 3) return 90f;
        float a0 = SectorStartAngleLocal();
        float a1 = MathF.Atan2(Points[2].Y - Points[0].Y, Points[2].X - Points[0].X);
        float d = (a1 - a0) * 180f / MathF.PI;
        while (d < 0f) d += 360f;
        while (d >= 360f) d -= 360f;
        return Math.Clamp(d, 0f, 359f);
    }

    /// <summary>扇形轮廓：圆心 → 圆弧 → 圆心；接近 359° 时直接按整圆画。
    /// <paramref name="exact"/> = 熔墨用的高精度采样（弦高 ≤ <see cref="ExactChordErrorPx"/>）。</summary>
    public List<Vector2> SectorOutlinePoints(bool exact = false)
    {
        var list = new List<Vector2>();
        if (Points.Count < 3) return list;
        var c = new Vector2(Points[0].X, Points[0].Y);
        float r = SectorRadiusLocal();
        float a0 = SectorStartAngleLocal();
        float sweep = SectorSweepDegrees();
        bool full = sweep >= 358.5f;
        list.Add(c);
        int n = Math.Clamp((int)MathF.Ceiling(MathF.Max(r, 1f) * MathF.Tau / 8f), 32, 160);
        if (exact)
            n = Math.Max(n, AdaptiveArcSegments(r * TransformScaleEstimate,
                                               (full ? 1f : sweep / 360f) * MathF.Tau));
        for (int i = 0; i <= n; i++)
        {
            float u = full ? i / (float)n : sweep / 360f * i / n;
            float a = a0 + u * MathF.Tau;
            list.Add(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r);
        }
        if (!full) list.Add(c);
        return list;
    }

    /// <summary>
    /// **抛物线的第一步**（用户 2026-09-20 改的口径："先设定开口，再用两点画出来"）：
    /// 点一下 = **顶点**，定下来就不再动。
    ///
    /// 朝向**不在这里定**——它由操作条那一格"开口方向"先选好（存 <see cref="CurveAxis"/> 的
    /// OpenUp / OpenDown / OpenRight / OpenLeft），和"大小"彻底分开。
    /// 这就是这一版要解决的事：原来"方向"和"大小"挤在同一个拖动里（靠 ±12° 死区赌你要开哪个口），
    /// 两个参数互相拽、都不好调。
    ///
    /// 起手先给一条"最小张口"的曲线（经过点放在 `u = 1` 那个点上，反解出来正好是下限），
    /// 免得松手前屏幕上什么都没有。
    /// </summary>
    public void SetParabolaVertex(float x, float y)
    {
        // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到两个（控制点数固定）。
        while (Points.Count < 2) AddPoint(x, y, 1f, 0);

        var v = new Vector2(x, y);
        const float p = ParabolaMinP;
        var (dir, perp) = ParabolaBasisLocal();
        SetPoint(0, v);
        SetPoint(1, v + perp * p + dir * (p * 0.5f));      // u = 1 处的曲线点（通径端点）
    }

    /// <summary>
    /// **抛物线的第二步**：把"**曲线要经过的那个点**"写进模型，`p` 由它反解
    /// （见 <see cref="ParabolaPThroughPoint"/>）。**朝向不动**——这正是"先定开口、再两点画"。
    /// </summary>
    public void SetParabolaThroughPoint(float x, float y)
    {
        if (Points.Count < 2) return;
        SetPoint(1, new Vector2(x, y));
    }

    /// <summary>
    /// 换朝向（操作条那格"开口方向"）：把"经过点"**跟着新朝向转一下**
    /// ——保持它在轴上的分量 `s`、横跨分量 `t` 不变，于是 **`p` 原样保住**。
    /// 用户看到的是"整条曲线转了个方向"，而不是"换一下朝向曲线就瘪了"。
    /// </summary>
    public void SetParabolaAxis(CurveAxis axis)
    {
        if (Kind != StrokeKind.Parabola || Points.Count < 2) return;
        var cur = EffectiveAxis;
        var next = NormalizeAxis(Kind, axis);
        CurveAxis = next;                  // 顺手把字段写干净（坏值不留在里面，存档会原样写出去）
        if (next == cur) return;           // 同一个方向就没什么要重组的

        var v = CurvePointLocal(0);
        var d = CurvePointLocal(1) - v;
        var (oldDir, oldPerp) = ParabolaBasis(cur);
        float s = Vector2.Dot(d, oldDir), t = Vector2.Dot(d, oldPerp);

        var (newDir, newPerp) = ParabolaBasis(next);
        SetPoint(1, v + newDir * s + newPerp * t);
    }

    /// <summary>
    /// **双曲线的第一步**（照抄 InkClass 的第一笔，见 `MW_ShapeDrawing.cs:1203-1211`）：
    /// 按下 = **中心（原点）**，拖出去 = **渐近线**。
    ///
    /// 这一步定下的是**整个渐近线框**：中心 ＋ 角点 `E`，于是 `A = |dx|`、`B = |dy|`，
    /// 而渐近线就画到 `±(A, B)` —— **正好是你拖到的地方**（见
    /// <see cref="HyperbolaAsymptoteLocal"/>）。用户 2026-09-20 定的口径：
    /// "渐近线画好以后大小完全不动，长度也不动"。
    ///
    /// 这里**不打对折**（原来存的是 `|dx|/2`）：那时渐近线要画到 `2A、2B` 才够长，
    /// 所以"拖多少 → 存一半"；现在渐近线自己就是框、曲线另有大小
    ///（见 <see cref="HyperbolaCurveALocal"/>），"拖到哪就画到哪"才是直的。
    ///
    /// 朝向这里只是**占位**，第二步拖动时会按"点落在渐近线哪一侧"重定。
    /// </summary>
    public void SetHyperbolaFromAsymptote(float x0, float y0, float x1, float y1, float minSize)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);

        var o = new Vector2(x0, y0);
        CurveAxis = CurveAxis.TransverseX;                     // 占位朝向，第二步重定
        float a = MathF.Max(minSize, MathF.Abs(x1 - x0));
        float b = MathF.Max(minSize, MathF.Abs(y1 - y0));
        SetPoint(0, o);
        SetPoint(1, new Vector2(o.X + a, o.Y + b));
    }

    /// <summary>
    /// 由"曲线经过的那个点"`d`（相对中心）定**实轴朝向**：
    ///   · `d` 落在渐近线**更横**的一侧（`|dy| &lt; m·|dx|`）→ **焦点在 x 轴**（左右双曲线）；
    ///   · 否则（更竖）→ **焦点在 y 轴**（上下双曲线）。
    ///
    /// 判据用乘法（等价于 InkClass 的 `|dy/dx| &lt; k`，但不用除、不会碰除零）。
    /// **曲线的大小不在这里算**——那是 <see cref="HyperbolaCurveAThroughPoint"/> 的事。
    /// 拆开是因为 2026-09-20 那一版把"定朝向"和"定大小"混在一个返回值里，
    /// 而大小又要写回第二个点（渐近线框），于是"定曲线"顺手把渐近线也改了。
    /// </summary>
    public static CurveAxis HyperbolaAxisThroughPoint(Vector2 d, float m)
    {
        if (!(m > 1e-4f)) m = 1f;                       // 斜率兜底：第一步没拖动时按 45°
        return MathF.Abs(d.Y) < m * MathF.Abs(d.X)
            ? CurveAxis.TransverseX : CurveAxis.TransverseY;
    }

    /// <summary>
    /// **双曲线的第二步**（用户 2026-09-20 定的口径："渐近线画好以后大小完全不动，
    /// 长度也不动，在通过第三个点生成双曲线"）：
    ///
    /// 渐近线（<see cref="Points"/>[1]）**一个字都不改**，只把"**曲线经过的那个点**"
    /// 记进第三个控制点；曲线的实/虚半轴由它反解（见 <see cref="HyperbolaCurveALocal"/>）。
    /// 朝向仍在这一步定（<see cref="HyperbolaAxisThroughPoint"/>）。
    ///
    /// **和上一版的区别（这是这次改动的全部意义）**：上一版把两个半轴反解出来**写回第二个点**，
    /// 而渐近线是从第二个点派生的 —— 于是"定曲线大小"这一步**顺手把渐近线也改了**，
    /// 用户的原话是"我画图的时候感觉很不适应"。
    /// 现在渐近线（第二个点）不动、曲线大小走第三个点，两件事彻底分开。
    /// </summary>
    public void SetHyperbolaThroughPoint(float x, float y)
    {
        if (Points.Count < 2) return;

        var o = CurvePointLocal(0);
        // 渐近线斜率 = **B / A**（两种朝向下都成立：渐近线方向恒为 (±A, ±B)。
        // 注意它不是 Imag/Real —— 那个比值在实轴沿 y 时会翻过来）。
        float m = HyperbolaBLocal() / MathF.Max(1e-4f, HyperbolaALocal());
        CurveAxis = HyperbolaAxisThroughPoint(new Vector2(x - o.X, y - o.Y), m);

        while (Points.Count < 3) AddPoint(x, y, 1f, 0);
        SetPoint(2, new Vector2(x, y));
    }

    /// <summary>
    /// **正弦 / 余弦的画法**：按下 = **起点**（曲线的起笔处，也就是老师画的那条
    /// y 轴所在的位置），拖出去 = **终点**。一次拖出**两件事**：
    ///   · **横向 = 要画多长**（可以不足一个周期，也可以三四个周期）；
    ///   · **纵向 = 曲线多高**（正弦从轴拖到峰、余弦从峰拖到谷，见 <see cref="WaveAmplitudeLocal"/>）。
    ///
    /// ⚠ 2026-09-20 **改过一次**：原来横向是"**一个周期**"，纵向是振幅——两个量互相独立，
    /// 于是拖一个又宽又矮的框会把波形**拉变形**（画出来不成正弦）；而且一张图上要画好几个
    /// 周期只能重复画好几条。现在横向是"**长度**"，**周期由振幅定**（见
    /// <see cref="WavePeriodLocal"/>）：波形比例恒定，横向拖多长就画多长（用户："画正余弦
    /// **很多个周期**的波浪线"）。
    ///
    /// 两个点都只由这一式算出，所以"拖动预览"和"松手提交"永远一致（不会松手跳一下）。
    /// 横向恒向右展开；`dy` **带符号**，往哪边拖，曲线就先往哪边走。
    /// </summary>
    public void SetWaveBox(float x0, float y0, float x1, float y1, float minSize)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);

        float length = MathF.Max(minSize, MathF.Abs(x1 - x0));             // 要画多长
        float dy = y1 - y0;
        if (MathF.Abs(dy) < minSize) dy = dy < 0f ? -minSize : minSize;   // 高度也有下限
        SetPoint(0, new Vector2(x0, y0));                                  // 起点
        SetPoint(1, new Vector2(x0 + length, y0 + dy));                    // 长度 ＋ 高度
    }

    /// <summary>
    /// 平行四边形的**第四个顶点**（局部坐标）：`第2 + 第3 − 第1`。
    ///
    /// **不存**（用户定，见 9.5）：存下来就有"存的和算的对不上"的可能，
    /// 而只要每次现推，"永远是平行四边形"就是存法本身的性质。
    /// 这一个式子有四个用户（渲染、轮廓折线、紧框、脏区），所以只写在这里一份。
    /// </summary>
    public Vector2 ParallelogramFourthLocal()
        => ParallelogramFourth(new Vector2(Points[0].X, Points[0].Y),
                               new Vector2(Points[1].X, Points[1].Y),
                               new Vector2(Points[2].X, Points[2].Y));

    /// <summary>同上，但三个点由调用方给（拖动预览要按**临时几何**算，见 PolygonInkBounds）。</summary>
    public static Vector2 ParallelogramFourth(Vector2 p0, Vector2 p1, Vector2 p2)
        => new(p1.X + p2.X - p0.X, p1.Y + p2.Y - p0.Y);

}
