using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>A single drawn item. Freehand strokes are filled ribbons so that
/// pressure can vary the width; shapes are stroked outlines.</summary>
internal sealed class Stroke
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

        public InkPiece(List<Vector2> pts, bool aux) : this(pts, aux, false) { }

        public InkPiece(List<Vector2> pts, bool aux, bool grid)
        {
            Pts = pts; Aux = aux; Grid = grid;
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
    public List<Stroke> MeltToInkParts()
    {
        var list = new List<Stroke>();
        foreach (var piece in InkPieces())
        {
            if (piece.Pts.Count < 2) continue;
            var m = new Stroke
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
                Dash = piece.Aux ? StrokeDash.Dashed : Dash,
            };
            foreach (var p in piece.Pts)
            {
                var q = Transform.IsIdentity ? p : Vector2.Transform(p, Transform);
                m.AddPoint(q.X, q.Y, 1f, 0);
            }
            list.Add(m);
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
    public List<InkPiece> InkPieces()
    {
        var list = new List<InkPiece>();

        switch (Kind)
        {
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
                foreach (var part in SplitOutlineParts(ShapeOutline()))
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
                foreach (var part in SplitOutlineParts(ShapeOutline()))
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
                foreach (var part in SplitOutlineParts(ShapeOutline()))
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

    /// <summary>椭圆上一段弧的采样点列（`u0 → u1`）：**立体图形的实线几何、虚线半圈、
    /// 以及熔墨三处共用这一份点列**（渲染与熔出来的逐点相同）。</summary>
    private List<Vector2> ArcPoints(Vector2 c, float rx, float ry, float u0, float u1)
    {
        int n = PreviewSmooth
            ? Math.Clamp((int)(MathF.Max(rx, ry) / 2f), 64, 160)
            : Math.Clamp((int)(MathF.Max(rx, ry) / 4f), 12, 48);
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

    /// <summary>扇形轮廓：圆心 → 圆弧 → 圆心；接近 359° 时直接按整圆画。</summary>
    public List<Vector2> SectorOutlinePoints()
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

    // =====================================================================
    //  坐标系 / 数轴（2026-09-19 第三批）
    //
    //  两个新种类**共用同一套定义**（四个点：外框两角 / 原点 / 单位长度点），
    //  差别只在"外框怎么理解"和"哪几端带箭头"。共用是刻意的：刻度线、单位长度、
    //  手柄、紧框、存档这些事两边一模一样，各写一份迟早会分叉。
    //  存法与理由见 StrokeKind.Coordinate 的注释。
    // =====================================================================

    /// <summary>
    /// 网格那一格的**最小边长**（局部坐标）：防"外框小得离谱时网格密成一片实心"。
    /// </summary>
    public const float AxisMinGridStepLocal = 6f;

    /// <summary>
    /// **网格那一格的边长**（局部坐标）——2026-09-24 用户要的"拖那个格点改方格大小"。
    ///
    /// `0` = **自动**：短边 ÷ 4（老行为，见 <see cref="AxisGridStepLocal"/>）。
    /// 所以老板书打开还是原来那个格子密度，**不用迁移**（存档 v24 新加的那一位读到 0 就是这个意思）。
    ///
    /// **为什么这一次可以存**：2026-09-19 立过一条"单位长度**现算，不存**"——
    /// 理由是那时那个点**画都不画**，留着它就是个"看不见却点得到"的死元素
    /// （仓库里那条"画都不画的东西也不该点得到"）。现在它**画出来了**
    ///（第一象限第一个格子的外角一颗空心点），拖它屏幕上的格子当场变大变小：
    /// 有了可付的可见效果，就该存。
    /// </summary>
    public float AxisGridStep;

    /// <summary>格距的**上限**：再大整个外框里就只剩轴线了（夹住手柄，别拖出个"没有格子的网格"）。</summary>
    public static float AxisMaxGridStepLocal(Vector2 frameA, Vector2 frameB)
        => MathF.Max(AxisMinGridStepLocal,
                     MathF.Min(MathF.Abs(frameB.X - frameA.X), MathF.Abs(frameB.Y - frameA.Y)));

    /// <summary>外框归一化之后的四个边界（局部坐标）。两个点顺序随意，这里统一成 min/max。</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) AxisFrameLocal()
    {
        float ax = Points[0].X, ay = Points[0].Y;
        float bx = Points.Count > 1 ? Points[1].X : ax;
        float by = Points.Count > 1 ? Points[1].Y : ay;
        return (MathF.Min(ax, bx), MathF.Min(ay, by), MathF.Max(ax, bx), MathF.Max(ay, by));
    }

    /// <summary>
    /// **原点 O**（局部坐标，坐标系才有；数轴返回 Zero，别处不读它）。
    /// </summary>
    public Vector2 AxisOriginLocal()
        => Points.Count > 2 ? new Vector2(Points[2].X, Points[2].Y) : Vector2.Zero;

    /// <summary>
    /// **格距手柄的位置**（局部坐标）= 第一象限第一个格子的外角 = 原点 + (格距, −格距)。
    ///
    /// 屏幕 y 向下，所以"第一象限"是**右上** = y 取 −格距（同 <see cref="ShapeHandleLocal"/>
    /// 里 AxisTop 那条注释）。
    /// 它只在 <see cref="Grid"/> 为真时作为手柄存在（没画格子就没有"第一个格子"，
    /// 也就没有"格子多大"这件事可说——见 SelectionHandles.ShapeHandlesOf）。
    /// </summary>
    public Vector2 AxisGridStepHandleLocal()
    {
        float step = AxisGridStepLocal();
        var o = AxisOriginLocal();
        return new Vector2(o.X + step, o.Y - step);
    }

    /// <summary>
    /// **网格那一格有多大**（局部坐标）。只服务坐标系那个可选网格。
    ///
    /// 优先用**存下来的那个值**（<see cref="AxisGridStep"/>，2026-09-24 起：用户拖过那颗
    /// 格点手柄就存下来）——夹在 [最小, 外框短边] 之间，所以把外框拉小之后格子会跟着变小，
    /// 不会出现"格子比外框还大、一个格子都看不见"。
    ///
    /// 没拖过（存的是 0）就**现算**：规则沿用刻度时代那档密度 = **短边 ÷ 4**，
    /// 所以开网格时看着还是课本上那个格子。
    /// </summary>
    public float AxisGridStepLocal()
    {
        var p0 = new Vector2(Points[0].X, Points[0].Y);
        var p1 = Points.Count > 1 ? new Vector2(Points[1].X, Points[1].Y) : p0;
        if (AxisGridStep > 0f)
            return Math.Clamp(AxisGridStep, AxisMinGridStepLocal, AxisMaxGridStepLocal(p0, p1));
        var (minX, minY, maxX, maxY) = AxisFrameLocal();
        return MathF.Max(AxisMinGridStepLocal, MathF.Min(maxX - minX, maxY - minY) / 4f);
    }

    /// <summary>箭头头部的长度（和 <see cref="ArrowHeadPoints"/> 同一口径，跟着笔宽走）。</summary>
    private static float AxisArrowHeadLen(float width) => MathF.Max(width * 5f, 12f);

    /// <summary>
    /// 坐标系 / 数轴的**画法**：由"按下的点 + 当前指针"一次算出全部定义元素。
    ///
    /// 和 <see cref="SetShapeBox"/> 同一个套路（拖动期每帧都要调，所以不分配、不走 SetPoints）：
    ///   · **坐标系**：**三个点** —— 外框一角 / 对角 / **原点 = 按下点**，
    ///     外框以原点为**中心对称展开**（拖出去的那两截就是半宽半高）；
    ///   · **数轴**：**两个点** —— 左端 / 右端，y 一律取**按下点**的 y
    ///     （往斜上方拖也还是水平线，见 StrokeKind.NumberLine：数轴歪了就不是数轴了）。
    ///
    /// 2026-09-19 用户定"两个种类都不要刻度"之后，这里从四个点缩到三个 / 两个：
    /// 少掉的正是"单位长度点"和数轴的"零点"——它们唯一的作用就是给刻度定间距。
    ///
    /// 同一天用户又要求**纠正坐标系的起手**（原话："应该是先确定原点，原点确定以后，
    /// 再把它展开"）：旧版把按下点与指针当成"外框的两个角"、原点硬写在框中心，
    /// 于是"我先按下的那个位置"**永远不是原点**，画完还得再拖一次原点。
    /// 现在按下那一刻原点就定了，拖出去只是让它长大。
    /// **定义元素仍然是三个**，所以手柄 / 紧框 / 命中 / 存档 / 撤销全都不用动。
    /// </summary>
    public void SetAxisBox(float x0, float y0, float x1, float y1)
    {
        if (Kind == StrokeKind.NumberLine)
        {
            // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到两个（控制点数固定）。
            while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
            float lx = MathF.Min(x0, x1), rx = MathF.Max(x0, x1);
            SetPoint(0, new Vector2(lx, y0));          // 左端
            SetPoint(1, new Vector2(rx, y0));          // 右端（箭头在这头）
            return;
        }

        // 起手只有 BeginShapeAt 铺的那一个占位点，这里补齐到三个（控制点数固定）。
        while (Points.Count < 3) AddPoint(x0, y0, 1f, 0);
        // 半宽半高 = 指针离原点多远（取绝对值，往哪个方向拖都长一样大）。
        float hw = MathF.Abs(x1 - x0), hh = MathF.Abs(y1 - y0);
        SetPoint(0, new Vector2(x0 - hw, y0 - hh));    // 外框一角
        SetPoint(1, new Vector2(x0 + hw, y0 + hh));    // 外框对角
        SetPoint(2, new Vector2(x0, y0));              // 原点 = 按下点（拖到哪都不动）
    }

    /// <summary>
    /// **空间直角坐标系的 y 轴默认方向**（局部坐标）：**棱柱族的斜二测**
    /// `(PrismDepthSkew, −1)`（屏幕 y 向下，所以"向后上方"是负 y）。
    /// 面板里的「四棱柱」就是按这个投影画的；真正的老「长方体」才是 45°。
    /// 画的时候会根据拖动象限换成左后 / 左前 / 右前；这个常量作为默认值和图标参考。
    /// </summary>
    public static readonly Vector2 SpaceAxisYDirection =
        Vector2.Normalize(new Vector2(ShapeSpec.PrismDepthSkew, -1f));

    /// <summary>
    /// **空间直角坐标系**的一笔画法（2026-11-03）：按下点 `(x0,y0)` = 原点 O，
    /// 指针 `(x1,y1)` 除了定 **x / z 长度**，还决定原点在长方体哪个底角：
    ///
    /// - **x 的正方向 = 拖动的左右方向**（往左拖 → x 向左）；
    /// - **y 的"后/前" = 拖动的上下方向**（往上拖 → y 向后上方，往下拖 → y 向前下方）；
    /// - **z 永远向上**（即使往下拖，长度取 |dy|）。
    ///
    /// 四个拖动象限正好对应四棱柱四个底角；松手后还能拖 X / Y 轴端把方向翻过去。
    /// 默认（往右上拖）就是课本最常见的"front-left：x 右、y 后右、z 上"。
    /// `Points` = `[O, +x 端, +y 端, +z 端]`，只画正半轴。
    /// </summary>
    public void SetSpaceAxisBox(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 4) AddPoint(x0, y0, 1f, 0);
        float dx = x1 - x0, dy = y1 - y0;
        float xLen = MathF.Max(0.5f, MathF.Abs(dx));
        float zLen = MathF.Max(0.5f, MathF.Abs(dy));
        float yLen = MathF.Max(0.5f, MathF.Min(xLen, zLen) * 0.5f);
        float xSign = dx < 0f ? -1f : 1f;
        bool back = dy <= 0f;
        // 棱柱族斜二测：**底面是平行四边形**，四条侧边全都平行——
        // 所以 y 的左右分量只由"前/后"决定，**不能**再乘 x 的左右符号：
        //   · 前角（往上拖）→ y 后右；(+skew, −1)
        //   · 后角（往下拖）→ y 前左；(−skew, +1)
        // x 的左右只改 x 自己。这样四个底角才都能和四棱柱的底边对上。
        var yDir = Vector2.Normalize(new Vector2(
            (back ? 1f : -1f) * ShapeSpec.PrismDepthSkew,
            back ? -1f : 1f));
        var o = new Vector2(x0, y0);
        SetPoint(0, o);
        SetPoint(1, o + new Vector2(xSign * xLen, 0f));
        SetPoint(2, o + yDir * yLen);
        SetPoint(3, o + new Vector2(0f, -zLen));
    }

    /// <summary>空间坐标系的四个定义点（局部坐标）：O / +x / +y / +z。</summary>
    public Vector2[] SpaceAxisPointsLocal()
    {
        if (Points.Count < 4) return Array.Empty<Vector2>();
        return new[]
        {
            new Vector2(Points[0].X, Points[0].Y),
            new Vector2(Points[1].X, Points[1].Y),
            new Vector2(Points[2].X, Points[2].Y),
            new Vector2(Points[3].X, Points[3].Y),
        };
    }

    /// <summary>
    /// 坐标系 / 数轴的**画布空间墨迹框** = 定义点的外接 ＋ 半笔宽。
    ///
    /// 它以前要多算一截"刻度半长"（刻度是唯一伸出外框的东西）；**刻度取消之后那一截
    /// 就该跟着取消**——框虚胖就是每帧白重画，而这一条是脏区、命中粗筛、导出裁切的依据。
    /// 箭头仍然不用额外算：它的两个翅膀尖在尖端**后面**（见 <see cref="ArrowHeadPoints"/>），
    /// 伸不到外框之外。
    ///
    /// <paramref name="extra"/> 与另外几处同一个用途：拖动预览要再叠一层实时矩阵。
    /// </summary>
    private static RectF AxisInkBoundsOf(ReadOnlySpan<Vector2> pts, in Matrix3x2 transform,
                                         in Matrix3x2 extra, float width)
    {
        var m = transform * extra;
        var r = RectF.Empty;
        for (int i = 0; i < pts.Length; i++)
        {
            var q = Vector2.Transform(pts[i], m);
            r.Add(q.X, q.Y);
        }
        if (r.IsEmpty) return r;
        return r.Inflate(width * 0.5f);
    }

    /// <summary>同上，读模型里的定义点（静止态用）。**定义点最多三个**，所以给三格的 span 就够。</summary>
    public RectF AxisInkBounds(in Matrix3x2 extra)
    {
        int n = Math.Min(Points.Count, 3);
        Span<Vector2> pts = stackalloc Vector2[3];
        for (int i = 0; i < n; i++) pts[i] = new Vector2(Points[i].X, Points[i].Y);
        return AxisInkBoundsOf(pts[..n], Transform, extra, Width);
    }

    /// <summary>
    /// 空间坐标系的墨迹紧框：四个定义点 ＋ **三个箭头的翅膀**。
    /// 普通坐标系的箭头翅膀在轴线末端"后面"，点框就能包住；空间坐标系的 y 轴
    /// 斜着走，箭头的两个翅膀会伸到点框外面——不把它们算进来，选中框会比图形小，
    /// 翻方向时旧箭头也会留在脏区外（用户 2026-11-03 看到的"残影"）。
    /// </summary>
    private static RectF SpaceAxisInkBoundsOf(ReadOnlySpan<Vector2> pts, in Matrix3x2 transform,
                                              in Matrix3x2 extra, float width)
    {
        if (pts.Length < 4) return RectF.Empty;
        var m = transform * extra;
        var r = RectF.Empty;

        void Add(Vector2 p) { var q = Vector2.Transform(p, m); r.Add(q.X, q.Y); }
        void Arrow(Vector2 tip, Vector2 dir)
        {
            foreach (var p in AxisArrowPoints(tip, dir, AxisArrowHeadLen(width))) Add(p);
        }

        for (int i = 0; i < 4; i++) Add(pts[i]);
        var xDir = pts[1] - pts[0];
        Arrow(pts[1], xDir.LengthSquared() > 1e-6f ? Vector2.Normalize(xDir) : new Vector2(1f, 0f));
        var yDir = pts[2] - pts[0];
        Arrow(pts[2], yDir.LengthSquared() > 1e-6f ? Vector2.Normalize(yDir) : SpaceAxisYDirection);
        Arrow(pts[3], new Vector2(0f, -1f));
        return r.IsEmpty ? r : r.Inflate(width * 0.5f);
    }

    // =====================================================================
    //  四种曲线（2026-09-20 第四批）：抛物线 / 双曲线 / 正弦 / 余弦
    //
    //  这一段只有三件事，但它们必须**严格同源**（仓库的老教训：
    //  "同一个名单写在多处 = 加一项必漏一处"）：
    //    ① 画法算式（ParabolaPointAt / HyperbolaPoint / WavePointAt）；
    //    ② 曲线自己的那个矩形（CurveBoxOf）—— 紧框、脏区、命中粗筛、导出裁切全用它；
    //    ③ 采样段数（CurveSegments）。
    //
    //  **一条硬规矩**：这四种曲线的紧框**绝不能**用"控制点的外接"。
    //  双曲线的两支伸得比"中心 ↔ 外角点"远得多（两个方向都要伸到 2 倍），
    //  正弦起点那一侧的半个周期也不在控制点的外接里——而这个框是空间索引的依据，
    //  小掉的后果是"曲线上有些地方点不中"（椭圆当年踩过一模一样的坑，见
    //  WorldInkBounds 里那段注释：紧框算成 256×16）。
    // =====================================================================

    /// <summary>第 `i` 个定义元素（局部坐标）。下标越界返回原点，不抛异常。</summary>
    public Vector2 CurvePointLocal(int i)
        => i >= 0 && i < Points.Count ? new Vector2(Points[i].X, Points[i].Y) : Vector2.Zero;

    /// <summary>
    /// 这种图形**至少**要几个定义元素才算完整。
    ///
    /// 目前只有双曲线需要它（给 3）：它的第三个点 ——"**曲线经过的那个点**"（2026-09-20 加的）
    /// 是**可缺的**（刚起手、或迁移前的老对象只有两个点），而"拖曲线上的那个点"这个手柄
    /// 照样得能按下去；按下去就要往第三格写，格数不够就是**数组越界**
    ///（自检里当场崩过一次，见 Engine.BeginVertexDrag）。
    /// 别的一律 2（它们本来就只有两个定义元素）。
    /// </summary>
    public static int MinCurvePoints(StrokeKind kind) => kind switch
    {
        StrokeKind.Hyperbola => 3,       // 中心 ＋ 渐近线角点 ＋ 曲线经过的点
        StrokeKind.Cuboid => 3,          // 正面矩形两角 ＋ 背面右下角（深度）
        StrokeKind.Tetrahedron => 4,     // 底面三点 ＋ 顶点
        StrokeKind.Prism => 3,           // 底面外接框两角 ＋ 顶面中心（侧棱向量）
        // 棱锥 / 棱台和棱柱**控制点完全一样**（底面外接框两角 ＋ 顶上那个中心）：
        // 差别只在"顶上那个中心算出来的面长什么样"（见 PrismTopLocal），所以只多两行。
        StrokeKind.Pyramid => 3,
        StrokeKind.Frustum => 3,
        StrokeKind.SpaceCoordinate => 4, // 原点 + 三个轴端
        _ => 2,
    };

    /// <summary>
    /// 抛物线**开口方向的单位向量**（屏幕坐标：y 向下，所以"向上"是 `(0,−1)`）。
    ///
    /// 用户 2026-09-20 改的口径（"先设定开口向左，然后用两点画出来"）：**方向从"拖动角度"
    /// 里拿出来，变成四个正方向里选一个**（存进 <see cref="CurveAxis"/> 的 OpenUp / Down / Left / Right）。
    ///
    /// 为什么这么改：原来方向和大小**挤在同一个拖动里**（靠 ±12° 死区赌你要开哪个口），
    /// 两个参数互相拽，用户的原话是"这两个参数很不好调整"。拆开之后
    /// **方向用选（离散四个）、大小用拖（连续）**，各自都简单；而且老师在黑板上画的抛物线
    /// 本来就只有正的四种（不再支持"斜的抛物线"——那本来就是上一版为了"自由"加的，
    /// 实际没人用，反而让"想画正的"这件事变得要小心翼翼）。
    ///
    /// 给到四个 Open* 之外的值（坏字节等）一律当**开口向上**（也是枚举的默认值 0）。
    /// </summary>
    public static Vector2 ParabolaDir(CurveAxis axis) => axis switch
    {
        CurveAxis.OpenDown => new Vector2(0f, 1f),
        CurveAxis.OpenRight => new Vector2(1f, 0f),
        CurveAxis.OpenLeft => new Vector2(-1f, 0f),
        _ => new Vector2(0f, -1f),              // OpenUp（兼兜底）
    };

    /// <summary>
    /// 抛物线的**两个单位方向**：`dir` 朝开口外、`perp` 横跨（`dir` 转 90°）。
    /// 几何、手柄、紧框全走它。
    /// </summary>
    public static (Vector2 dir, Vector2 perp) ParabolaBasis(CurveAxis axis)
    {
        var dir = ParabolaDir(axis);
        return (dir, new Vector2(-dir.Y, dir.X));
    }

    /// <summary>
    /// **抛物线开口朝哪边**：由**这一拖的符号**定（照 InkClass 的 `case 20/21`）。
    ///
    /// `pair` 只是"哪一对"：`OpenUp` = **上下抛物**（他的 `y = ax²`）、
    /// `OpenRight` = **左右抛物**（他的 `y² = ax`）——图形面板那一格点一次就在这两档之间换。
    /// 具体朝上还是朝下、朝左还是朝右，看 `q` 落在顶点哪一侧：
    ///
    ///   · 上下档：`q` 在顶点**上方** → 开口向上（屏幕 y 向下，所以比的是 `q.Y < v.Y`）；
    ///   · 左右档：`q` 在顶点**左侧** → 开口向左。
    ///
    /// **为什么方向要跟着拖动走**（用户 2026-09-20："感觉不对，还是参考他的逻辑"）：
    /// 方向由面板选死的时候，"面板选着向上、手却往下拖"会反解出负的沿轴分量，
    /// `p` 直接掉到下限 —— 曲线当场缩成一条细针。
    /// 方向交给这一拖，怎么拖都画得出来；面板只需要回答"上下还是左右"这件推不出来的事。
    /// </summary>
    public static CurveAxis ParabolaAxisOfDrag(Vector2 v, Vector2 q, CurveAxis pair)
    {
        if (pair == CurveAxis.OpenRight || pair == CurveAxis.OpenLeft)      // 左右抛物
            return q.X < v.X ? CurveAxis.OpenLeft : CurveAxis.OpenRight;
        return q.Y < v.Y ? CurveAxis.OpenUp : CurveAxis.OpenDown;           // 上下抛物
    }

    /// <summary>
    /// **焦准距 `p`**（课本里 `y² = 2px` 的那个），抛物线的**唯一**形状参数：
    /// 顶点 ＋ 方向 ＋ `p` 就把整条曲线定死了。焦点在 `dir·(p/2)`、准线过 `−dir·(p/2)`、通径长 `2p`。
    ///
    /// 它由"**曲线要经过的那个点**"`q` 反解（用户的口径："另一个是抛物线的末端点"）——
    /// 把 `q − v` 拆到 `(dir, perp)` 上得 `(s, t)`，而曲线在参数 `u` 处的点是
    /// `v + perp·(u·p) + dir·(u²p/2)`，要求它等于 `q`：
    ///
    ///     u·p = t   →   u = t/p
    ///     u²·p/2 = s   →   t²/(2p) = s   →   **p = t² / (2s)**
    ///
    /// 和双曲线第二步（`a² = dx² − dy²/k²`）是同一类"拿曲线上的一个点反解参数"。
    ///
    /// 两个分量都取**绝对值**：方向是由这一拖的符号定的（见 <see cref="ParabolaAxisOfDrag"/>），
    /// 所以沿轴的分量本该是正的；这里再兜一层，免得"方向没跟上"时反解出负数。
    /// 拖得**几乎平行于轴**（`s ≈ 0`）时张口会趋近无限，所以给 `s` 一个下限 ——
    /// 画出来是一条很扁的抛物线（而不是"没反应"或"一条直线"）。
    /// `t ≈ 0`（点落在对称轴上）没有信息定张口，给下限 `minP`。
    /// </summary>
    public static float ParabolaPThroughPoint(Vector2 v, Vector2 q, CurveAxis axis, float minP)
    {
        var (dir, perp) = ParabolaBasis(axis);
        var d = q - v;
        float s = MathF.Abs(Vector2.Dot(d, dir));      // 沿开口方向的分量
        float t = MathF.Abs(Vector2.Dot(d, perp));     // 横跨方向的分量
        s = MathF.Max(s, minP * 0.5f);
        if (t < 1e-3f) return minP;
        return MathF.Max(minP, t * t / (2f * s));
    }

    /// <summary>
    /// 反解出的 `p` 的**下限**（画布单位）：`p` 太小整条曲线就缩成一个点。
    /// **不乘 DpiScale**——这是几何下限、不是交互容差；画布缩放时它跟着缩放，正是想要的。
    /// </summary>
    public const float ParabolaMinP = 8f;

    /// <summary>实例版（读数 / 紧框 / 手柄 / 渲染都用它）。</summary>
    public float ParabolaPLocal()
        => ParabolaPThroughPoint(CurvePointLocal(0), CurvePointLocal(1), EffectiveAxis, ParabolaMinP);

    /// <summary>实例版的轴方向（单位向量）。</summary>
    public Vector2 ParabolaDirLocal() => ParabolaDir(EffectiveAxis);

    /// <summary>实例版的两个单位方向（单位向量）。</summary>
    public (Vector2 dir, Vector2 perp) ParabolaBasisLocal() => ParabolaBasis(EffectiveAxis);

    /// <summary>
    /// 抛物线在参数 `t` 上的点（**以顶点为原点、轴为 x′** 的那个局部系）：
    /// `x′ = t²·p/2`、`y′ = t·p` —— 这就是 `y′² = 2p·x′` 的现成参数化。
    ///
    /// `t = 0` 是顶点、`t = ±1` 正好落在**通径端点**上（`x′ = p/2、y′ = ±p` ✓ 通径长 2p）；
    /// 画到哪为止见 <see cref="ParabolaSpanOf"/>（**画到"经过点"那儿**，照 InkClass）。
    /// </summary>
    public static Vector2 ParabolaPointAt(Vector2 v, Vector2 q, CurveAxis axis, float t)
    {
        float p = ParabolaPThroughPoint(v, q, axis, ParabolaMinP);
        var (dir, perp) = ParabolaBasis(axis);
        return v + perp * (t * p) + dir * (t * t * p * 0.5f);
    }

    /// <summary>实例版：按模型里的顶点 / 经过点 / 朝向算（渲染与轮廓折线走它）。</summary>
    public Vector2 ParabolaPointAt(float t)
        => ParabolaPointAt(CurvePointLocal(0), CurvePointLocal(1), EffectiveAxis, t);

    /// <summary>
    /// 抛物线**画到 `t` 的哪里为止**（照 InkClass 的 `case 20`：`for (i = 0; i &lt;= |dx|)`）：
    ///
    /// 参数 `u` 处的横跨偏移是 `u·p`，而"曲线经过的那个点"正是老师拖到的位置，
    /// 所以让它落在端点：`u = |t_经过点| / p`。于是曲线**正好停在你拖到的那个点**，
    /// 左右（或上下）对称地铺开 —— 这就是他那个"拖到哪、画到哪"的手感。
    ///
    /// 兜一个下限 `ParabolaMinSpan`：鼠标只是点了一下（几乎没拖）时不至于缩成一个点。
    /// </summary>
    public static float ParabolaSpanOf(Vector2 v, Vector2 q, CurveAxis axis, float minP)
    {
        float p = ParabolaPThroughPoint(v, q, axis, minP);
        var (_, perp) = ParabolaBasis(axis);
        float t = MathF.Abs(Vector2.Dot(q - v, perp));      // 那个点的横跨偏移
        return MathF.Max(ParabolaMinSpan, t / p);
    }

    /// <summary>实例版（渲染 / 轮廓折线 / 紧框都用它）。</summary>
    public float ParabolaSpanLocal()
        => ParabolaSpanOf(CurvePointLocal(0), CurvePointLocal(1), EffectiveAxis, ParabolaMinP);

    /// <summary>抛物线画出来至少铺这么宽（见 <see cref="ParabolaSpanOf"/>）。</summary>
    public const float ParabolaMinSpan = 0.35f;

    /// <summary>
    /// 折线里的**抬笔标记**（双曲线两支之间那一下）。
    ///
    /// <see cref="ShapeOutline"/> 返回的是一条 `List&lt;Vector2&gt;`，而它的三个消费者
    /// （橡皮判交 / 套索判圈 / 打散成笔迹）都是**按相邻两点连线段**读的——
    /// 双曲线有两支，中间直接接过去就凭空多一条横穿包围盒的线。
    /// 用一个"NaN 点"当抬笔，是改动最小的做法：消费者只要跳过 NaN 段即可
    /// （它们本来就各自有一处循环）。
    /// </summary>
    public static readonly Vector2 OutlineBreak = new(float.NaN, float.NaN);

    /// <summary>这个轮廓点是不是"抬笔标记"（见 <see cref="OutlineBreak"/>）。</summary>
    public static bool IsOutlineBreak(Vector2 p) => float.IsNaN(p.X);

    /// <summary>
    /// 按种类**归一之后**的朝向。
    ///
    /// **抛物线和双曲线都需要它**（另外两条曲线没有朝向可言）：
    ///   · 抛物线 = **四个正方向**里选一个（OpenUp / Down / Left / Right，见 <see cref="ParabolaDir"/>）
    ///     —— 它是**选出来的**（图形面板那一格，两点里没有这个信息），只能读字段；
    ///   · 双曲线 = **实轴沿 x 还是沿 y**（TransverseX / TransverseY）
    ///     —— 它是**推出来的**：第三个点（"曲线经过的那个点"）落在渐近线的哪一侧就是哪个
    ///     （见 <see cref="HyperbolaAxisThroughPoint"/>）。
    ///
    /// 双曲线为什么**现推**、不读字段（2026-09-20 定）：朝向和形状必须永远一致。
    /// 读字段的话，拖渐近线角点把斜率扳过对角线之后，那条曲线会卡在
    /// "朝向说左右、点却落在上下那一侧"—— 半轴反解出一个负数 → 曲线**当场缩成一个点**。
    /// 现推就天然一致：点在哪一侧就是哪个朝向；越过对角线的那一刻，两个朝向给出的 `a`
    /// 都趋近于最小，顺滑地翻过去、不会跳。
    ///
    /// 字段本身照样**要写、要存**：只有两个点（刚起手 / 迁移前的老对象）时没有第三个点可推，
    /// 那时它就是兜底；`NormalizeAxis` 还要管"坏字节 / 老文件"落到合法值上。
    /// </summary>
    public CurveAxis EffectiveAxis
    {
        get
        {
            // 双曲线且已经有"曲线经过的那个点" → 现推（理由见上面那段注释）。
            if (Kind == StrokeKind.Hyperbola && Points.Count >= 3)
                return HyperbolaAxisThroughPoint(CurvePointLocal(2) - CurvePointLocal(0),
                                                 HyperbolaBLocal() / MathF.Max(1e-4f, HyperbolaALocal()));
            return NormalizeAxis(Kind, CurveAxis);
        }
    }

    /// <summary>见 <see cref="EffectiveAxis"/>（静态版：读端拿到 kind 之后就地归一）。</summary>
    public static CurveAxis NormalizeAxis(StrokeKind kind, CurveAxis axis)
    {
        if (kind == StrokeKind.Hyperbola)
            return axis == CurveAxis.TransverseY ? CurveAxis.TransverseY : CurveAxis.TransverseX;
        if (kind == StrokeKind.Parabola)
            // 四个正方向之外的（含其它族的值、坏字节）一律当**开口向上**。
            return axis is CurveAxis.OpenDown or CurveAxis.OpenRight or CurveAxis.OpenLeft
                ? axis : CurveAxis.OpenUp;
        return axis;
    }

    /// <summary>
    /// **渐近线框的横向半宽**（`A`，= 角点到中心的 x 距离，恒非负）。
    ///
    /// ⚠ 双曲线**有两套"半轴"，别混**（2026-09-20 拆开，这是"渐近线不再乱动"的关键）：
    ///
    ///   ① **渐近线框** —— `HyperbolaALocal()` / `HyperbolaBLocal()`：
    ///      第二步拖出来的那个矩形（`±A × ±B`），**两条虚线渐近线就是它的对角线**。
    ///      用户定的口径是"渐近线画好以后大小完全不动，长度也不动"——
    ///      所以锁定之后这两个值**一个都不许改**（曲线怎么变都跟它们无关）。
    ///      `A` / `B` 是 **x 方向半宽 / y 方向半高**，不是"谁是实半轴"。
    ///
    ///   ② **曲线自己的半轴** —— `HyperbolaCurveALocal()` / `HyperbolaCurveBLocal()`：
    ///      第三步由"曲线经过的那个点"反解（见 <see cref="HyperbolaCurveAThroughPoint"/>），
    ///      比例恒等于 `B/A`（这样曲线才以那两条虚线为渐近线）。
    ///      **几何 / 截断 / 紧框 / 采样只认这一套。**
    ///
    /// "谁是实半轴"是**第三件事**，随实轴朝向互换（见 <see cref="HyperbolaRealLocal"/>），
    /// 名字只出现在读数文案里。
    /// </summary>
    public float HyperbolaALocal() => HyperbolaAOf(CurvePointLocal(0), CurvePointLocal(1));

    /// <summary>渐近线框的**纵向半高**（`B`，见 <see cref="HyperbolaALocal"/> 那段）。</summary>
    public float HyperbolaBLocal() => HyperbolaBOf(CurvePointLocal(0), CurvePointLocal(1));

    /// <summary>上面两个量的静态核心（拖手柄时要用临时几何算读数，理由同 <see cref="ParabolaHalfSpanOf"/>）。</summary>
    public static float HyperbolaAOf(Vector2 o, Vector2 e) => MathF.Abs(e.X - o.X);

    /// <summary>见 <see cref="HyperbolaAOf"/>。</summary>
    public static float HyperbolaBOf(Vector2 o, Vector2 e) => MathF.Abs(e.Y - o.Y);

    /// <summary>双曲线的两个**轴方向**（单位向量）：`real` = 实轴、`imag` = 虚轴。</summary>
    public static (Vector2 real, Vector2 imag) HyperbolaBasis(CurveAxis axis)
        => axis == CurveAxis.TransverseY
            ? (new Vector2(0f, 1f), new Vector2(1f, 0f))
            : (new Vector2(1f, 0f), new Vector2(0f, 1f));

    /// <summary>
    /// 实轴方向 / 虚轴方向的**分量**（局部坐标，恒非负）：
    /// 拖手柄时把指针位置拆到两个轴上用它（"拖顶点只改 a、拖渐近线点只改斜率"）。
    /// </summary>
    public static (float along, float across) HyperbolaComponents(Vector2 o, Vector2 p, CurveAxis axis)
    {
        var (real, imag) = HyperbolaBasis(axis);
        var d = p - o;
        return (MathF.Abs(Vector2.Dot(d, real)), MathF.Abs(Vector2.Dot(d, imag)));
    }

    /// <summary>
    /// **实半轴**（**曲线自己的**，不是渐近线框的 `A`/`B`）：随实轴朝向互换 ——
    /// 实轴沿 x → 就是曲线的 x 方向半宽 `a`；沿 y → 是曲线的 y 方向半高 `b`。
    /// 只用在**读数文案**和"顶点在哪"上（几何那边一律用 <see cref="HyperbolaCurveALocal"/>）。
    /// </summary>
    public float HyperbolaRealLocal()
        => EffectiveAxis == CurveAxis.TransverseX ? HyperbolaCurveALocal() : HyperbolaCurveBLocal();

    /// <summary>虚半轴（详见 <see cref="HyperbolaRealLocal"/> 那段）。</summary>
    public float HyperbolaImagLocal()
        => EffectiveAxis == CurveAxis.TransverseX ? HyperbolaCurveBLocal() : HyperbolaCurveALocal();

    /// <summary>
    /// **曲线自己的 x 方向半宽系数**（`a`，**不是**渐近线框那个 `A`）：
    /// 由第三个定义元素 —— "**曲线经过的那个点**" —— 反解（见
    /// <see cref="HyperbolaCurveAThroughPoint"/>）。
    ///
    /// 还没定过大小（只有两个控制点）时给 `A/2`（= 曲线画到 `2a = A`，正好铺满框）。
    /// 这种情况**不该发生**（读存档时会补齐第三个点），但兜底不能返回 0。
    /// </summary>
    public float HyperbolaCurveALocal()
        => Points.Count >= 3
            ? HyperbolaCurveAThroughPoint(CurvePointLocal(0), CurvePointLocal(2),
                                          HyperbolaALocal(), HyperbolaBLocal(), EffectiveAxis)
            : MathF.Max(HyperbolaMinA, HyperbolaALocal() * 0.5f);

    /// <summary>曲线的 y 方向半高系数（= `a × B/A`，见 <see cref="HyperbolaCurveALocal"/>）。</summary>
    public float HyperbolaCurveBLocal()
    {
        float a = HyperbolaCurveALocal();
        float A = HyperbolaALocal();
        return a * (A > 1e-4f ? HyperbolaBLocal() / A : 1f);
    }

    /// <summary>
    /// 由"**曲线经过的那个点**"反解曲线的 x 方向半宽系数 `a`。
    ///
    /// 把点拆成 x / y 分量 `(px, py)`，配上"曲线必须以那两条虚线为渐近线"这条锁
    /// （`b/a = B/A`），标准式两边各是一个朝向的式子：
    ///   · **实轴沿 x**：`x²/a² − y²/b² = 1` → `a² = px² − py²·(A/B)²`；
    ///   · **实轴沿 y**：`y²/b² − x²/a² = 1` → `a² = py²·(A/B)² − px²`。
    ///
    /// ⚠ **两个朝向的符号是反的**，这是最容易漏的一处：只写前一个式子的话，
    /// 上下双曲线永远解出一个负数 → `a` 被下限兜住 → 曲线缩成一个点。
    /// 朝向由 <see cref="EffectiveAxis"/> 给（它也是从"这一个点"推出来的，
    /// 见 <see cref="HyperbolaAxisThroughPoint"/>），所以两边永远配套。
    ///
    /// **只夹下限**、不再夹"不超过 `A/2`"（2026-09-20 第二次改）：
    /// 用户的口径是"**根据最后一个点确定双曲线**"—— 点在哪，曲线就必须过哪。
    /// 超出框怎么办？交给**画多长**那头管（见 <see cref="HyperbolaTMaxOf"/>：
    /// 出框就停笔），而不是把曲线的形状掐掉 ——
    /// 掐掉的话，用户点在远处会看到"点了没反应"，那正是他说"很不适应"的那一类手感。
    /// 点落在两支之间（`a² ≤ 0`）时给下限 —— 曲线缩到最小但**不消失**
    /// （"看不见却占着一条对象"是仓库里的老忌）。
    /// </summary>
    public static float HyperbolaCurveAThroughPoint(Vector2 o, Vector2 p, float A, float B, CurveAxis axis)
    {
        if (A < 1e-3f || B < 1e-3f) return HyperbolaMinA;
        float dx = p.X - o.X, dy = p.Y - o.Y;
        float k2 = (A * A) / (B * B);                       // (A/B)²
        float a2 = axis == CurveAxis.TransverseY
            ? dy * dy * k2 - dx * dx                     // 上下：实轴沿 y
            : dx * dx - dy * dy * k2;                    // 左右：实轴沿 x
        float a = a2 > 0f ? MathF.Sqrt(a2) : 0f;
        return MathF.Max(a, HyperbolaMinA);
    }

    /// <summary>
    /// 曲线半轴 `a` 的**下限**（画布单位）：太小整条曲线就缩成一个点。
    /// **不乘 DpiScale**——这是几何下限、不是交互容差（和 <see cref="ParabolaMinP"/> 同一个理由）。
    /// </summary>
    public const float HyperbolaMinA = 8f;

    /// <summary>
    /// **顶点**（实轴两端，落在曲线上）：`side` = ±1 选哪一端。
    /// 用的是**曲线的**实半轴（不是渐近线框的 A/B）。
    /// </summary>
    public Vector2 HyperbolaVertexLocal(int side)
    {
        var o = CurvePointLocal(0);
        var (real, _) = HyperbolaBasis(EffectiveAxis);
        return o + real * (HyperbolaRealLocal() * side);
    }

    /// <summary>
    /// **渐近线框的角点**（就是第二个定义元素本身）：拖它 = 改**渐近线框**
    ///（斜率与长度一起变），曲线的大小不受它影响（曲线有自己的第三个点）。
    /// </summary>
    public Vector2 HyperbolaCornerLocal() => CurvePointLocal(1);

    /// <summary>
    /// **曲线上的那个点**（第三个定义元素 = 第二步拖出来的"经过点"）。
    /// 拖它 = 曲线跟着经过新位置（改的是**曲线的**半轴，**渐近线一个字都不动**）。
    ///
    /// 还没有第三个点的老对象（迁移前）退化成顶点——不该发生（读存档时会补齐，
    /// 见 InkSerializer 的 v14 迁移），但绝不能返回"原点"那种看着正常的错值。
    /// </summary>
    public Vector2 HyperbolaCurvePointLocal()
        => Points.Count >= 3 ? CurvePointLocal(2) : HyperbolaVertexLocal(1);

    /// <summary>
    /// 双曲线的**两条渐近线**（`side` = ±1 选哪一条）：返回它在这条对象范围内的**两端点**。
    ///
    /// 端点 = 中心 ±(A, B)，也就是**第二步拖出来的那个角**（见 <see cref="HyperbolaCornerLocal"/>）
    /// —— "拖到哪就画到哪"，而且**锁定之后再也不会变**（用户 2026-09-20 定的：
    /// "渐近线画好以后大小完全不动，长度也不动"）。
    ///
    /// 这两个端点正是**紧框的四个角**（紧框 = 渐近线框，见 CurveBoxOf 的双曲线那一档），
    /// 所以画渐近线**不会把紧框撑大**；曲线本体又"出框就停笔"
    /// （见 <see cref="HyperbolaTMaxOf"/>），屏幕上就是"曲线严丝合缝地待在两条虚线张开的范围里"。
    /// 这一条很重要：紧框是脏区 / 命中粗筛 / 导出裁切的依据，撑大了就是每帧白重画一大片。
    ///
    /// ⚠ 端点**不能**拿 `HyperbolaBasis` 拆成"实轴方向 A、虚轴方向 B"：
    /// `A` / `B` 恒是 **x / y 方向的半宽半高**（见 <see cref="HyperbolaALocal"/> 那段），
    /// 而渐近线的方向本来就是"横向 A、纵向 B"—— 拿基向量一转，上下双曲线的渐近线
    /// 就会画到框外面去（自检里"渐近线的端点就是紧框的角"那条就是这么红的）。
    /// </summary>
    public (Vector2 from, Vector2 to) HyperbolaAsymptoteLocal(int side)
    {
        var o = CurvePointLocal(0);
        // 方向恒为 (±A, ±B)：两种朝向同一个式子（A / B 本身就是按 x / y 定义的量）。
        float k = PreviewAsymptoteScale > 0.01f ? PreviewAsymptoteScale : 1f;
        var dir = new Vector2(HyperbolaALocal() * k, side * HyperbolaBLocal() * k);
        return (o - dir, o + dir);
    }

    /// <summary>双曲线要不要画那两条虚线渐近线（对象自己的属性，进存档，默认画）。</summary>
    public bool ShowAsymptotes = true;

    /// <summary>
    /// 仅“图形卡小样”用：不画辅助几何（正切那两条虚线渐近线）。
    /// 小样只要形状本身，辅助线在 44px 的卡片里会变成两条竖杠、抢掉曲线。
    /// </summary>
    public bool PreviewNoAux;

    /// <summary>仅“图形卡小样”用：正切的可见半高（把无限高的分支截断成方形小样）。</summary>
    public float PreviewTangentHalfHeight;

    /// <summary>仅“图形卡小样”用：坐标系箭头长度；不设则按线宽算。</summary>
    public float PreviewArrowHeadLen;

    /// <summary>仅“图形卡小样”用：双曲线渐近线缩到中心的倍率；0 = 画满整个渐近线框。</summary>
    public float PreviewAsymptoteScale;

    /// <summary>仅“图形卡小样”用：余弦画成标准余弦（从峰开始），而不是画布上的“单峰”。</summary>
    public bool PreviewStandardCosine;

    /// <summary>仅“图形卡小样”用：辅助线（渐近线/被挡棱）的线宽倍率；0 = 默认 0.6。</summary>
    public float PreviewAuxWidthScale;

    /// <summary>仅“图形卡小样”用：辅助线不透明度；0 = 默认不透明。</summary>
    public float PreviewAuxAlpha;

    /// <summary>仅“图形卡小样”用：焦点画成小实心点，不画空心小圆。</summary>
    public bool PreviewFocusDots;

    /// <summary>仅“图形卡小样”用：圆弧/椭圆用更高段数，缩到卡片上不显多边形。</summary>
    public bool PreviewSmooth;

    /// <summary>仅“图形卡小样”用：正弦/余弦/波浪固定画几个周期；0 = 按真实长度算。</summary>
    public float PreviewWaveCycles;

    /// <summary>仅“图形卡小样”用：一个周期占多宽；0 = 按真实长度/振幅算。</summary>
    public float PreviewWavePeriod;

    /// <summary>
    /// 改"画不画那两条虚线渐近线"，**并且让几何缓存失效**。
    ///
    /// ⚠ **光给字段赋值是不够的**（2026-09-22 用户报的就是这件事："我选择的不带渐近线的，
    /// 但是画完以后还有渐近线？"）：渐近线走的是**辅助几何槽**，那个槽按 `Revision` 缓存
    /// （见 <see cref="BuildAuxGeometry"/>）。而画的过程中**恒为"画"**（那是画法的向导，
    /// 见 `Engine.BeginShapeAt`），松手那一刻才按面板那一档收口——如果只是把字段改成 false，
    /// 屏幕上那份"带渐近线"的缓存几何**原样留着**，于是看着像"这一档根本没生效"
    ///（下一次编辑把它重建出来才会消失，更让人摸不着头脑）。
    ///
    /// ⚠ 教训（记在 计划-图形工具.md §39.5）：**改一个进几何的字段，就得让它对应的缓存失效**；
    /// 而且只查字段的自检**照不出这个 bug**（第一版就是这么漏的）——现在两处自检都改成
    /// **数屏幕像素**（有那一档那块必须有墨、无那一档必须一个墨点都没有）。
    /// </summary>
    /// <summary>
    /// **配对期暂时别画**（用户 2026-09-25 定："识别的那一刻，第一笔就该消失"）。
    ///
    /// 只影响**画**：`Overlay.DrawStroke` 开头一句就返回 —— 文档、撤销、存档都不动，
    /// 那一笔还躺在原处，提交时 `DwellShapeAction` 靠它的引用把它记进撤销栈。
    ///
    /// ⚠ **提交时必须清掉**（`Engine` 里那段）：不清的话，Ctrl+Z 把这一笔放回来时
    /// 它还是隐藏的 —— 屏幕上"撤销之后什么都没回来"，而且**再也变不回来** ✗
    /// 这也是自检里专门钉的一条（见 `--dwelltest` 的"两笔"那组）。
    ///
    /// ⚠ **不存档**：它是纯运行期的临时状态（和 `ShowAsymptotes` 那种"用户设定"不是一类）。
    /// </summary>
    internal bool HiddenForPairing;

    public void SetShowAsymptotes(bool on)
    {
        if (ShowAsymptotes == on) return;
        ShowAsymptotes = on;
        Revision++;                     // 辅助几何（那两条虚线）重建的触发点
    }

    /// <summary>
    /// **椭圆（带焦点）要不要画焦点三角形**（对象自己的档，进存档，默认画）。
    ///
    /// 两档的含义（用户 2026-09-22 定的口径）：**两个焦点永远画**（"要带焦点"），
    /// 这一档只管**连不连** F₁P、F₂P 那两条边——
    ///   · `true`  ＝ 椭圆 ＋ 两焦点 ＋ 焦点三角形（P 可拖，讲 `|PF₁| + |PF₂| = 2a` 用）；
    ///   · `false` ＝ 椭圆 ＋ 两焦点（只有点，没有三角形）。
    ///
    /// 只对 <see cref="StrokeKind.ConicEllipse"/> 有意义（别的种类恒 true，照旧不读它）。
    /// </summary>
    public bool FocusTriangle = true;

    /// <summary>
    /// 画焦点那两个**小圆点**的半径（局部坐标，逻辑像素）。
    ///
    /// 3 是试出来的：再小（1.5）就只是一个 5 像素的小疙瘩、在板书上找不着；
    /// 再大（6）就盖住了它附近那段椭圆弧，看着像个洞。描边用的是这一笔自己的宽度，
    /// 所以小圆点画出来是"一个略粗的实心点"（内圈只剩 1~2 像素）。
    /// </summary>
    public const float FocusDotRadius = 3f;

    /// <summary>
    /// 焦点三角形第三个顶点 P **在椭圆上的参数角**（弧度，见 <see cref="ConicEllipsePointLocal"/>）：
    /// `P = O + (a·cos u, b·sin u)`。
    ///
    /// **`NaN` = "还没定过"**，这时按 <see cref="DefaultFocusPointU"/> 取**短半轴那一端**
    /// （见那里的注释）。用 NaN 当"自动"这个语义是仓库里已有的约定
    /// （<see cref="OutlineBreak"/> 就是这么标"这里抬笔"的），好处是**不用再加一个
    /// "用户动过没有"的布尔位**——那个位一旦加进来，就又多一处"复制 / 存档 / 撤销
    /// 忘记带上它"的地方。
    /// </summary>
    public float FocusPointU = float.NaN;

    /// <summary>
    /// **P 还没被拖过时的默认位置**：椭圆**左上方**那个点（参数角 −120°）。
    ///
    /// 这个数是**公开规则层**的常量（<see cref="ShapeSpec.FocusPointDefaultU"/>），
    /// 转发一下只是为了"读起来贴着 P"——**画布和图标读的是同一个数**，
    /// 所以图标上那个焦点三角形和真画出来的一模一样。
    /// 为什么选左上方（而不是"正上方"）、为什么是常数（不随 a / b 变），见那边的注释。
    /// </summary>
    public const float DefaultFocusPointU = ShapeSpec.FocusPointDefaultU;

    /// <summary>P 的参数角：没定过（NaN）就取默认那个（左上方，见 <see cref="DefaultFocusPointU"/>）。</summary>
    public float FocusPointAngleLocal
        => float.IsNaN(FocusPointU) ? DefaultFocusPointU : FocusPointU;

    /// <summary>
    /// 焦点三角形第三个顶点 **P**（局部坐标）：按参数角算出来的椭圆上的一个点
    /// （`P = O + (a·cos u, b·sin u)`）。
    ///
    /// **存角不存点**的收益就在这里：a / b 被拖手柄改掉之后 P **自动还在椭圆上**，
    /// 不需要任何一处"改完几何记得把 P 拉回椭圆"的补丁（那种补丁必定有人漏）。
    /// </summary>
    public Vector2 ConicEllipsePointLocal()
    {
        var c = ShapeCenterLocal;
        float u = FocusPointAngleLocal;
        return new Vector2(c.X + MathF.Cos(u) * SemiAxisALocal,
                           c.Y + MathF.Sin(u) * SemiAxisBLocal);
    }

    /// <summary>
    /// **两个焦点**（局部坐标，`F1` 在长轴的负方向那头）：`c = √(a² − b²)`，
    /// 长轴是哪一条由 a / b 谁大决定。
    ///
    /// 退化（正圆，a == b）时 `c = 0`，两个焦点都落在中心——照画（两个点叠在一起），
    /// 不做"正圆不画焦点"那种特例：老师画圆的时候本来就不该用这个工具，
    /// 而半路上把它拖成圆的，看到"焦点收进圆心"正是他想要的那个事实。
    /// </summary>
    public (Vector2 F1, Vector2 F2) ConicEllipseFociLocal()
    {
        var c = ShapeCenterLocal;
        float a = SemiAxisALocal, b = SemiAxisBLocal;
        float cc = a * a - b * b;
        if (cc <= 0f) return (c, c);                 // 圆（或更扁的反向）：焦点收在中心
        float f = MathF.Sqrt(cc);
        return a >= b ? (new Vector2(c.X - f, c.Y), new Vector2(c.X + f, c.Y))
                      : (new Vector2(c.X, c.Y - f), new Vector2(c.X, c.Y + f));
    }

    /// <summary>
    /// 把 P **拖到哪儿**（画布上拖那个圆点时调）：把那个位置折成参数角存起来
    /// （`u = atan2(Δy/b, Δx/a)`）。
    ///
    /// 折算这一步就是"P 永远在椭圆上"的保证：拖到椭圆里面 / 外面都无所谓，
    /// 落到屏幕上的永远是椭圆上离那个方向最近的一点。
    /// </summary>
    public void SetConicEllipsePointFromLocal(float x, float y)
        => SetConicEllipsePointAngle(ConicEllipseAngleOf(new Vector2(x, y)));

    /// <summary>直接写 P 的参数角（拖 P 松手时走这条，见 <see cref="ConicEllipseAngleOf"/>）。</summary>
    public void SetConicEllipsePointAngle(float u)
    {
        FocusPointU = u;
        Revision++;                                  // 几何变了：缓存失效、脏区重算（同 SetPoint）
    }

    /// <summary>
    /// 一个**局部坐标**的点折成 P 的参数角（`atan2(Δy/b, Δx/a)`）。
    ///
    /// 拖 P 的**预览**和**落笔**都调它——两处各写一份 atan2 的话，
    /// 屏幕上拖到的地方和松手之后停的地方会差一点点（"松手跳一下"）。
    /// </summary>
    public float ConicEllipseAngleOf(Vector2 local)
    {
        var c = ShapeCenterLocal;
        float a = MathF.Max(1e-3f, SemiAxisALocal), b = MathF.Max(1e-3f, SemiAxisBLocal);
        return MathF.Atan2((local.Y - c.Y) / b, (local.X - c.X) / a);
    }

    /// <summary>换一下"画不画焦点三角形"（面板那一格再点一次，只作用于**下一笔**）。</summary>
    public void SetFocusTriangle(bool on)
    {
        if (FocusTriangle == on) return;
        FocusTriangle = on;
        Revision++;
    }

    /// <summary>
    /// 双曲线**画到哪为止**（照 InkClass 的 `case 24`：`for (i = a; i &lt;= |dx|)`）：
    /// **画到"曲线经过的那个点"那条轴为止** —— 老师拖到哪，曲线就在那儿收笔。
    ///
    /// 参数 `t` 处的实轴坐标是 `r·cosh t`（`r` = 实半轴），而"经过点"的实轴坐标是 `along`，
    /// 所以让它在端点上：`t = acosh(along / r)`。因为 `r ≤ along` 恒成立
    ///（`a` 就是从这个点反解出来的），`acosh` 的参数一定 ≥ 1，不会无解。
    ///
    /// 这比原来那条"出框就停笔 / 最多画到 2a"更直白：**拖到哪、曲线就画到哪**，
    /// 也和他一模一样（原来那条会让曲线在离指针还有一截的地方就停住，用户说"感觉不对"）。
    ///
    /// ⚠ 于是曲线**可能伸到渐近线框外面**（他本来就没有框这个概念）——
    /// 紧框那头已经改成"框 ∪ 曲线的实际范围"（见 <see cref="CurveBoxOf"/>）。
    /// 兜一个下限 `HyperbolaMinT`：点正好落在实轴上时 `along = r`、`t = 0`，
    /// 不兜的话曲线缩成一个点（"看不见却占着一条对象"是仓库里的老忌）。
    /// </summary>
    public static float HyperbolaTMaxOf(Vector2 o, Vector2 q, CurveAxis axis, float realSemi)
    {
        var (real, _) = HyperbolaBasis(axis);
        float along = MathF.Abs(Vector2.Dot(q - o, real));     // 那个点沿实轴的坐标
        float t = realSemi > 1e-4f ? MathF.Acosh(MathF.Max(1f, along / realSemi)) : 0f;
        return MathF.Max(t, HyperbolaMinT);
    }

    /// <summary>实例版（画曲线、采样折线、紧框都用它，见 <see cref="HyperbolaTMaxOf"/>）。</summary>
    public float HyperbolaTMaxLocal()
        => HyperbolaTMaxOf(CurvePointLocal(0), HyperbolaCurvePointLocal(),
                           EffectiveAxis, HyperbolaRealLocal());

    /// <summary>双曲线画出来至少有这么一段参数（见 <see cref="HyperbolaTMaxOf"/>）。</summary>
    public const float HyperbolaMinT = 0.25f;

    /// <summary>
    /// 双曲线的一支上、参数 `t ∈ [−T, T]` 处的点。
    ///
    /// `branch = 0` 是"第一个分支"、`1` 是另一个（横向时是右支 / 左支，纵向时是上支 / 下支）。
    /// 参数方程用双曲函数：`x = ±a·cosh t、y = b·sinh t` —— 这正是 `x²/a² − y²/b² = 1`
    /// 的现成参数化，`cosh² − sinh² = 1` 就是那个恒等式。
    /// 屏幕 y 向下，所以"上支"是 `y` 变小。
    /// </summary>
    public static Vector2 HyperbolaPoint(Vector2 o, float a, float b, CurveAxis axis, int branch, float t)
    {
        float ch = MathF.Cosh(t), sh = MathF.Sinh(t);
        float side = branch == 0 ? 1f : -1f;
        // `a` / `b` 一律是**x / y 方向的半宽半高**（见 HyperbolaALocal 那段注释），
        // 所以两种朝向下"谁是实轴"体现在：实轴方向用 cosh 铺开、另一个方向用 sinh。
        return axis == CurveAxis.TransverseY
            ? new Vector2(o.X + a * sh, o.Y - side * b * ch)     // 实轴沿 y：上支 / 下支
            : new Vector2(o.X + side * a * ch, o.Y + b * sh);    // 实轴沿 x：右支 / 左支
    }

    /// <summary>正弦 / 余弦的**起点**（局部坐标）：余弦是峰顶、正弦是（第一个）零点。</summary>
    public Vector2 WaveStartLocal() => CurvePointLocal(0);

    /// <summary>
    /// **终点**：`(起点.x ＋ 一个周期, 起点.y ＋ dy)`。它一次定下**周期**和**振幅**
    /// （用户 2026-09-20 定："画的时候就根据起点为原点，还有终点控制周期和振幅"）。
    ///
    /// `dy` **带符号**，符号的含义是"曲线先往哪边去"：
    ///   · 正弦：`dy < 0`（往上拖）= 先上后下，就是课本的 `y = sin x`；
    ///   · 余弦：`dy > 0`（往下拖）= 从峰顶往下，就是课本的 `y = cos x`。
    /// 带符号之后"拖出来的框"和"曲线实际占的地方"一致——上一版取绝对值，
    /// 往下拖时曲线还是往上跑，手感是别扭的。
    /// </summary>
    public Vector2 WaveEndLocal() => CurvePointLocal(1);

    /// <summary>终点相对起点的**纵向偏移**（带符号，见 <see cref="WaveEndLocal"/>）。</summary>
    public static float WaveDyOf(Vector2 start, Vector2 end) => end.Y - start.Y;

    /// <summary>实例版（读数与画法都用它）。</summary>
    public float WaveDyLocal() => WaveDyOf(WaveStartLocal(), WaveEndLocal());

    /// <summary>
    /// 波形**一个周期的长度 ÷ 振幅**（＝"这条波浪画出来有多密"）。
    ///
    /// **用户 2026-09-20 定：一个周期 = 一个振幅**（原话："这个波浪线的比例我想了想
    /// 设置成振幅 = 一个周期"）。所以取 **1**：振幅拖多高，一个周期就有多宽 ——
    /// 拖 300 宽、振幅 100 就是**三个整周期**，数周期不用拿尺子量。
    ///
    /// ⚠ 这条只对**波浪线**（`StrokeKind.Wave`）生效；正弦 / 余弦走"框宽 = 一个周期"，
    /// 和这个常数无关（见 <see cref="WavePeriodLocal"/>）。
    ///
    /// 沿革：这条常数换过两次含义 —— 最早是"框宽 = 一个周期"（多周期得重复画好几条），
    /// 第十五批改成 `4`（周期由振幅定、画多长拖多长），第十六批用户定成 `1`。
    /// 原来的 `4` 是照"屏幕上别太扁"估的（数学上 `y = sin x` 是 `2π ≈ 6.283`：
    /// 用户早在第一版就说过"高度 1 和周期 2π 那么图形很扁"）。
    ///
    /// ⚠ **将来做"曲线挂坐标系 / 按格吸附"时要回到 2π**：那时"1 个单位"由格数定，
    /// 比例就不该由我们定（否则图象和坐标系对不上）。
    /// </summary>
    public const float WavePeriodPerAmplitude = 1f;

    /// <summary>
    /// **一个周期的长度**（像素）。**两种波浪的算法不一样**（2026-09-20 第十六批分的工）：
    ///   · **正弦 / 余弦**（`Sine` / `Cosine`）：**框宽就是一个周期** ——
    ///     `周期 = 框宽`。拖多宽就是"画一个多大周期的图象"，讲课时最直白；
    ///   · **波浪线**（`Wave`）：**周期由振幅定** —— `周期 = WavePeriodPerAmplitude × 振幅`，
    ///     框宽是"**要画多长**"（可以好几个周期）。
    ///
    /// 为什么波浪线要"周期由振幅定"：用户 2026-09-20 那句"**在画正余弦很多个周期的波浪线**"
    /// —— 旧定义里"框宽"和"框高"**互相独立**，拖一个又宽又矮的框会把波形**拉变形**
    ///（画出来的根本不是正弦的样子）。让周期跟着振幅走，**波形比例就恒定了**，
    /// 横向那一拖只剩下一个含义：画多长。
    ///
    /// ★ **2026-09-26：波浪线的周期可以"自带一个"了**（见 <see cref="WavePeriodOf"/>）。
    /// 图形工具那条路一个字节没改（它不写第三个点 → 还是上面这条 T = A）；
    /// 只有**停顿识别**那条路会写 —— 因为老师手画的波，周期和振幅**本来就没关系**
    ///（手画的三个波 T/A 通常是 3~6），锁死 T = A 就只能认"我们自己工具画出来那种比例"。
    /// </summary>
    public float WavePeriodLocal()
    {
        if (PreviewWavePeriod > 0.01f) return PreviewWavePeriod;
        return WavePeriodOf(Kind, WaveStartLocal(), WaveEndLocal(), CurvePointLocal(2));
    }

    /// <summary>
    /// **一个周期的宽度**（三种波共用的一份算式，p0/p1/p2 = 起点 / 终点 / 第三个点）。
    ///
    /// 第三个点**可缺**（`CurvePointLocal(2)` 在点数不够时给 `Vector2.Zero`）：
    ///   · **有**它 → **`周期 = |p2.x − p0.x|`** —— 周期是**对象自己带的一个量**，
    ///     和振幅**互不相干**（识别那条路写的就是它）；
    ///   · **没有**它 → 落回老规矩（波浪线 `T = 振幅`、正弦 / 余弦 `T = 框宽`）。
    ///
    /// ⚠ 为什么用 `Vector2.Zero` 当"没有"的哨兵：和双曲线第三个点**同一个约定**
    ///（见 `CurveBoxOf` 里那句"第三个点可缺"，那边也是这么判的）。
    /// 不会误判：识别写进去的是 `p0 + (周期, 0)`，而周期 ≥ 1 像素，
    /// 只有"p0 正好在原点且周期为 0"才会撞上 —— 那个状态不存在。
    ///
    /// ⚠ **只写这一份**：`WavePeriodLocal`（实例、渲染/手柄/读数走它）和
    /// `CurveBoxOf`（静态、紧框走它）都调它 —— 以前是两份手抄的同一句话，
    /// 加这个自由度时正好收敛掉（"同一个名单写两处必漏一处"是本仓库的老账）。
    /// </summary>
    public static float WavePeriodOf(StrokeKind kind, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        if (kind != StrokeKind.Wave) return MathF.Abs(p1.X - p0.X);    // 正弦 / 余弦：框宽就是周期
        if (p2 != Vector2.Zero) return MathF.Max(1e-3f, MathF.Abs(p2.X - p0.X));
        return WavePeriodPerAmplitude * WaveAmplitudeOf(p0, p1, kind); // 老规矩：周期 = 振幅
    }

    /// <summary>
    /// **给波浪线写"一个周期的宽度"**（第三个定义元素，2026-09-26 加）。
    ///
    /// 只有**停顿识别**那条路调它（`ShapeRecognize.ApplyAxis`）：老师手画的多周期波，
    /// 周期和振幅没有固定比例，锁死 `T = 振幅` 会把波形**压扁或拉长** ——
    /// 那正是"变出来和画的不一样 = 比不变更糟"。
    ///
    /// 图形的**第三个点只用到 x**（周期 = `|p2.x − p0.x|`），y 写成起点的 y 就行
    ///（`CurveBoxOf` / `WaveTracedPointAt` 都不读它的 y）。
    /// ⚠ 它**不是手柄**：四种曲线走通用框（见 `Selection.HasShapeHandles`），
    /// 所以识别出来的周期**暂时拖不动**——想改得再补一个手柄。
    /// </summary>
    public void SetWavePeriod(float period)
    {
        while (Points.Count < 3) AddPoint(Points[0].X, Points[0].Y, 1f, 0);
        SetPoint(2, new Vector2(Points[0].X + MathF.Max(1f, period), Points[0].Y));
    }

    /// <summary>**框宽**（＝横向拖了多远）。正弦 / 余弦里它就是"一个周期"，波浪线里是"要画多长"。</summary>
    public float WaveLengthLocal() => MathF.Abs(WaveEndLocal().X - WaveStartLocal().X);

    /// <summary>
    /// 画出来一共**几个周期**。
    ///   · 正弦 / 余弦：**恒为 1**（框宽就是一个周期）；
    ///   · 波浪线：`要画多长 ÷ 周期`，**可以不是整数**（拖到哪儿画到哪儿，最后一段是半截）。
    /// 渲染的采样密度也按它算（见 <see cref="WavePointAt"/> 的调用方）。
    /// </summary>
    public float WaveCyclesLocal()
    {
        if (PreviewWaveCycles > 0.01f) return PreviewWaveCycles;
        float t = WavePeriodLocal();
        return t > 1e-3f ? WaveLengthLocal() / t : 1f;
    }

    /// <summary>
    /// 振幅 A（恒非负）。**余弦和另外两种的换算不一样**，这是本族唯一一处不对称：
    ///   · 正弦 / **波浪线**：起点在轴上，峰到轴的距离就是 A → `A = |dy|`；
    ///   · 余弦：起点在峰顶，半个周期后到谷，谷离峰顶 `2A` → `A = |dy| / 2`。
    ///
    /// 它俩共同点是"**拖出来的终点都落在曲线的极值点上**"（正弦拖到峰的高度、
    /// 余弦从峰拖到谷），所以"拖到哪儿"和"看着多高"是一致的，所见即所得。
    /// </summary>
    public float WaveAmplitudeLocal() => WaveAmplitudeOf(WaveStartLocal(), WaveEndLocal(), Kind);

    /// <summary>振幅的静态核心（同一个式子只写一份）。</summary>
    public static float WaveAmplitudeOf(Vector2 start, Vector2 end, StrokeKind kind)
        => kind == StrokeKind.Cosine
            ? MathF.Abs(WaveDyOf(start, end)) * 0.5f
            : MathF.Abs(WaveDyOf(start, end));

    /// <summary>
    /// 波形上 `u` 处的点（`u` 的**单位是周期**：`u = 0` 起点、`u = 1` 一个周期之后）。
    /// 画出来的那一段是 `u ∈ [0, <see cref="WaveCyclesLocal"/>]`（可以不是整数）。
    ///
    ///   · **正弦**：`y = 起点.y + dy·sin(2πu)`（`dy < 0` 就是先上后下 ✓）；
    ///   · **余弦**：`y = 起点.y + dy·(1 − cos(2πu))/2`（起点在峰顶、中间到谷 ✓）。
    /// 两式在 u = 0 处分别给出"轴"和"峰"，正是"从起点（y 轴）开始画"。
    ///
    /// **横向一律往右画**（长度取框宽的绝对值）：左右拖动只影响"画多长"，
    /// 不影响朝向（朝向由**上下**拖的符号定）——和抛物线"开口朝哪边由这一拖定"是两回事。
    /// </summary>
    public static float WaveYAt(Vector2 start, Vector2 end, StrokeKind kind, float u)
    {
        float dy = WaveDyOf(start, end);
        double phase = u * MathF.Tau;
        return kind == StrokeKind.Cosine
            ? start.Y + (float)(dy * (1.0 - Math.Cos(phase)) * 0.5)
            : start.Y + (float)(dy * Math.Sin(phase));
    }

    /// <summary>实例版：按模型里的两个定义元素算（渲染与轮廓折线走它）。</summary>
    public Vector2 WavePointAt(float u)
    {
        var start = WaveStartLocal();
        var end = WaveEndLocal();
        float y = PreviewStandardCosine
            ? start.Y + (end.Y - start.Y) * 0.5f * MathF.Cos(u * MathF.Tau)
            : WaveYAt(start, end, Kind, u);
        return new Vector2(start.X + WavePeriodLocal() * u, y);
    }

    /// <summary>
    /// **画出来的那一段**上 `t ∈ [0, 1]` 处的点（`t = 0` 起点、`t = 1` 终点）。
    ///
    /// 渲染 / 轮廓折线 / 紧框采样都走它——它们要的是"整条曲线从这头到那头"，
    /// 而不是"第几个周期"。**换算只有这一处**（把"按周期数"折成"按比例"）。
    /// </summary>
    public Vector2 WaveTracedPointAt(float t) => WavePointAt(WaveCyclesLocal() * t);

    /// <summary>波形那一段的**采样段数**：按总长算，并且保证每个周期至少 24 段（多个周期才够滑）。</summary>
    private int WaveSegmentsLocal()
    {
        int n = Math.Clamp((int)MathF.Ceiling(WaveLengthLocal() / 16f * MathF.Max(1f, WaveCyclesLocal())),
                           24, 512);
        return PreviewSmooth ? Math.Max(n, 160) : n;
    }


    /// <summary>
    /// 峰 / 谷这两个**极值点**（用户说的"统一根据最大最小点来控制图形"）。
    ///
    /// 它们是**算出来的、不存**——存的是起点和终点两个定义元素，
    /// 和"平行四边形的第四点现推"是同一条规矩：只存定义，其余现推。
    /// 位置直接取曲线上那个周期数（正弦峰 1/4、谷 3/4；余弦峰在起点、
    /// 谷在 1/2），所以**一定落在曲线上**，不会悬空。
    ///
    /// ⚠ 2026-09-20：`u` 现在要**夹在画出来的范围里**（`WaveCyclesLocal`）——
    /// 画多长由拖动定，**可以不足四分之一个周期**（那时峰还没到就收笔了），
    /// 不夹的话这两个点会落在曲线外面。
    /// </summary>
    private Vector2 WaveExtremumAt(bool crest)
    {
        var start = WaveStartLocal();
        var end = WaveEndLocal();
        bool cos = Kind == StrokeKind.Cosine;
        float u = cos ? (crest ? 1f : 0.5f) : (crest ? 0.25f : 0.75f);
        u = MathF.Min(u, WaveCyclesLocal());
        return new Vector2(start.X + WavePeriodLocal() * u, WaveYAt(start, end, Kind, u));
    }

    /// <summary>**峰点**（在曲线上）：正弦是 1/4 处那个；余弦是周期末端那个（起点那个就是起点本身）。</summary>
    public Vector2 WaveCrestLocal() => WaveExtremumAt(crest: true);

    /// <summary>**谷点**（在曲线上）：正弦是 3/4 处那个；余弦是 1/2 处那个。</summary>
    public Vector2 WaveTroughLocal() => WaveExtremumAt(crest: false);

    // =====================================================================
    //  **正切 y = tan x**（2026-09-20 第十五批）
    //
    //  一支（定义域 (−π/2, π/2)）画成"以原点为中心的一个框"：
    //    按下 = **原点**（这一支的中心、图象与 x 轴相交处），拖出 = 那个框的一角。
    //    横向 = **半支长**（渐近线就在 ±这个距离上）、纵向 = **可视半高**（曲线冲到那儿截断）。
    //
    //  为什么它和正弦 / 余弦的画法不一样：
    //    · 正弦 / 余弦的形状比例（周期 : 振幅 = 2π : 1）是**数学事实**，只能有一个自由度，
    //      所以纵向拖的是"振幅"、横向拖的是"长度"，`ShapeSpec` 之外那个比例常数负责固定形状；
    //    · 正切**值域无界**，"画到多高"是个**显示选择**（课本一般画到 ±3 左右），
    //      所以纵向那一拖本来就该是"看得见多少"。
    //    · 而它的**形状**由"横竖同一个单位"钉死（π/2 个单位 = 半支长）→
    //      **不会被拖变形**，所以不需要"周期 : 振幅"那种比例常数；
    //      只有一个**下限比例**（`TangentMinAspect`）兜着"别画成一条斜线"（见那一处注释）。
    // =====================================================================

    /// <summary>正切那**一支的中心**（＝按下那个点：图象与 x 轴的交点，也是原点）。</summary>
    public Vector2 TangentOriginLocal() => CurvePointLocal(0);

    /// <summary>拖出去的那个点（它和原点一起定下"半支长"和"可视半高"）。</summary>
    public Vector2 TangentEndLocal() => CurvePointLocal(1);

    /// <summary>**半支长**：渐近线离原点这么远（两条渐近线在 `原点 ± 半支长`）。</summary>
    public float TangentHalfSpanLocal()
        => MathF.Max(0.5f, MathF.Abs(TangentEndLocal().X - TangentOriginLocal().X));

    /// <summary>
    /// **可视半高**（像素）：曲线冲到离原点这么高就截断（＝框的上下边）。
    ///
    /// ⚠ **有一个下限比例**（<see cref="TangentMinAspect"/>）：横竖既然共用一个单位，
    /// "看得见多高"就直接决定"两头离渐近线还有多远"——框太矮的时候曲线只画到一半就截断了，
    /// 看着是**一条斜线**、不像正切（用户 2026-09-20 上手就发现了：他要"很窄很高"才贴着渐近线，
    /// 而那本来不该靠手感去拖）。所以这里把高度**顶到"贴着渐近线"的那个最小比例**：
    /// 随手拖个方框，画出来也是课本那个样子；拖得更高仍然照你的来（越高越贴）。
    /// </summary>
    public float TangentHalfHeightLocal()
    {
        if (PreviewTangentHalfHeight > 0.5f)
            return MathF.Max(0.5f, PreviewTangentHalfHeight);
        float dragged = MathF.Max(0.5f, MathF.Abs(TangentEndLocal().Y - TangentOriginLocal().Y));
        return MathF.Max(dragged, TangentMinAspect * TangentHalfSpanLocal());
    }

    /// <summary>
    /// 正切的**最小高宽比**（可视半高 ÷ 半支长）＝ **3**。
    ///
    /// 横竖共用一个单位（见 <see cref="TangentUnitLocal"/>），所以这个比例等价于
    /// "曲线画到 `θ = arctan(3·π/2) ≈ 1.36 rad ≈ 78°`"，也就是**画到离渐近线只差 13% 的地方**
    /// —— 课本上的正切正是这个"两头几乎竖直、贴着渐近线"的样子（再往下拖就该截断了）。
    /// 用户 2026-09-20 的原话："很窄很高的时候会贴着渐近线，说明这个比例的设置比较重要"：
    /// 把这条**做进引擎**，就不用老师靠手感去拖高拖窄了。
    /// </summary>
    public const float TangentMinAspect = 3f;

    /// <summary>
    /// **一个单位有多长**（像素）＝ 半支长 ÷ (π/2)。
    ///
    /// 因为 `x = ±π/2` 正好是渐近线，而渐近线又落在框的左右边上 ——
    /// 于是"横向拖多宽"就把 x 方向的**单位**定死了；正切图象要求**横竖同尺度**
    ///（`y = tan x` 这条曲线本身），所以 y 方向的单位**同一个**。
    /// </summary>
    public float TangentUnitLocal() => TangentHalfSpanLocal() / (MathF.PI * 0.5f);

    /// <summary>画得到的最大参数：`|tan θ| ≤ 可视半高 / 单位` 处（再往外就冲出框了）。</summary>
    public float TangentThetaMaxLocal()
    {
        float u = TangentUnitLocal();
        return MathF.Min(MathF.PI * 0.5f * 0.999f, MathF.Atan(TangentHalfHeightLocal() / u));
    }

    /// <summary>
    /// 正切一支上、参数 `θ ∈ (−π/2, π/2)` 处的点（局部坐标）。
    /// 屏幕 y 向下，所以 `tan` 越大越**往上**（`y` 越小）——曲线从左下冲到右上，正是课本的样子。
    /// </summary>
    public Vector2 TangentPointAtTheta(float theta)
    {
        var o = TangentOriginLocal();
        float u = TangentUnitLocal();
        return new Vector2(o.X + theta * u, o.Y - MathF.Tan(theta) * u);
    }

    /// <summary>
    /// **画出来的那一支**上 `t ∈ [0, 1]` 处的点（`t = 0` 左下、`t = 1` 右上）。
    /// 渲染 / 轮廓折线共用它（和正弦那条 `WaveTracedPointAt` 同一个套路）。
    /// </summary>
    public Vector2 TangentTracedPointAt(float t)
    {
        float m = TangentThetaMaxLocal();
        return TangentPointAtTheta(-m + 2f * m * t);
    }

    /// <summary>
    /// 正切的**两条渐近线**（`side = −1` 左边、`+1` 右边）：都从框的上下边穿过，
    /// 所以端点就是框的上下角。走**辅助几何槽**画成细虚线（和双曲线的渐近线同一种画法）。
    /// </summary>
    public (Vector2 From, Vector2 To) TangentAsymptoteLocal(int side)
    {
        var o = TangentOriginLocal();
        float x = o.X + side * TangentHalfSpanLocal();
        float h = TangentHalfHeightLocal();
        return (new Vector2(x, o.Y - h), new Vector2(x, o.Y + h));
    }

    /// <summary>
    /// **正切的画法**：按下 = 原点（这一支的中心），拖出 = 以它为中心的框的一角
    ///（横向 = 半支长、纵向 = 可视半高）。两个方向都取绝对值：
    /// 这一支**永远是"左下 → 右上"**，往哪个方向拖都是同一支（它没有"开口朝哪边"这回事）。
    ///
    /// ⚠ 存下来的是**你拖的那个值**，"看得见多高"另有下限（见 `TangentHalfHeightLocal`）——
    /// 读的时候才顶上去，所以**画、紧框、渐近线三者用的是同一个数**。
    /// </summary>
    public void SetTangentBox(float x0, float y0, float x1, float y1, float minSize)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        // 半支长 / 半高都有下限（拖得太小会退化成一个点那么高，没法看）
        float hx = MathF.Max(minSize, MathF.Abs(x1 - x0));
        float hy = MathF.Max(minSize, MathF.Abs(y1 - y0));
        SetPoint(0, new Vector2(x0, y0));                        // 原点
        SetPoint(1, new Vector2(x0 + hx, y0 + hy));              // 框的一角
    }

    /// <summary>
    /// 正切那一支的**采样段数**：两端越靠近渐近线越陡（`tan` 在那儿变化极快），
    /// 所以按"半支长 **或可视半高**的像素"给密度（高度被下限顶高之后，尾巴也变长了，
    /// 只看半支长会让那一段看着是折线）。
    /// </summary>
    private int TangentSegmentsLocal()
        => Math.Clamp((int)MathF.Ceiling(
               MathF.Max(TangentHalfSpanLocal(), TangentHalfHeightLocal()) / 4f), 24, 512);

    /// <summary>
    /// 曲线自己的那个矩形（**局部坐标**）。**全引擎唯一一份**，紧框 / 包围盒 / 脏区都问它。
    ///
    /// 四档的算式：
    ///   · 抛物线：顶点 ↔ 控制点那个矩形（开口朝上时它正好是曲线的最小外接；
    ///     朝左 / 朝右时控制点管的是"深度 ＋ 半高"，同样是那个矩形）；
    ///   · 双曲线：**`±A × ±B`，就是渐近线的那个矩形**（A / B = x / y 方向的半宽半高）——
    ///     两个朝向同一个式子，见下面那一档；
    ///   · 正弦：`一个周期宽 × ±A`；余弦：`一个周期宽 × (起点 → 谷底 2A)`；
    ///     波浪线：`画多长 × 按周期数算到的峰 / 谷`（拖不足一个周期时峰还没到，框跟着收）。
    ///
    /// `axis`（曲线朝向）：**这一档现在谁都没用**——双曲线原来按实/虚分流、各带一个 `√5`，
    /// 改成"渐近线框"之后两个朝向是同一个式子。参数留在签名里是因为它属于"曲线框"这一层的
    /// 一般化描述（调用方按同一套签名传参，删了要在好几处签名里绕一圈，得不偿失）。
    /// </summary>
    public static RectF CurveBoxOf(StrokeKind kind, CurveAxis axis, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        switch (kind)
        {
            case StrokeKind.Parabola:
            {
                // 抛物线：**顶点 ＋ 经过点 ＋ 朝向**定形 —— 朝向给轴、`p` 由经过点反解
                //（见 ParabolaPThroughPoint）。画出来的范围是 `|t| ≤ ParabolaSpanOf`，于是
                //   横跨方向：±(Span·p)，沿轴方向：0 … Span²·p/2
                // 取"顶点 + 两个端点"这三个点的外接就够（O(1)，不用逐点采样——
                // 紧框每帧都要问，逐点算就白费了）。
                float p = ParabolaPThroughPoint(p0, p1, axis, ParabolaMinP);
                var (dir, perp) = ParabolaBasis(axis);
                float t = ParabolaSpanOf(p0, p1, axis, ParabolaMinP);
                var tip = dir * (t * t * p * 0.5f);
                var span = perp * (t * p);
                var r = RectF.Empty;
                r.Add(p0.X, p0.Y);
                r.Add(p0.X + tip.X + span.X, p0.Y + tip.Y + span.Y);
                r.Add(p0.X + tip.X - span.X, p0.Y + tip.Y - span.Y);
                return r;
            }

            case StrokeKind.Hyperbola:
            {
                // 紧框 = **渐近线框 ∪ 曲线实际画到的范围**（A / B 是 x / y 方向的半宽半高）。
                //
                // 这一条把三件事一次对齐：
                //   · 渐近线的两个端点**就是** `±(A, B)`（见 HyperbolaAsymptoteLocal）→ 端点在框角上；
                //   · 曲线"**画到"经过点"那儿为止**"（见 HyperbolaTMaxOf，照 InkClass）——
                //     那个点通常落在框里，于是整条曲线在框内；**拖到框外时曲线也跟着出去**，
                //     所以紧框要取**两者的并集**（下面那几行就是干这个的）。
                //     不能只取 `±A × ±B`：曲线伸出去的那一截会被裁掉（脏区 / 导出都跟着错）。
                //   · 于是紧框既不会被渐近线白白撑大、也不会比曲线小。
                // 朝向**不影响**"框"这一半：A、B 本来就是"x / y 方向的量"，虚实互换不影响外接矩形。
                float halfX = MathF.Abs(p1.X - p0.X);
                float halfY = MathF.Abs(p1.Y - p0.Y);
                // 第三个点**可缺**（刚起手 / 三道静态调用点会传 Zero 进来）：那时曲线根本没画
                //（`HyperAsymptotePreviewOnly` 只画虚线），紧框就只是渐近线框本身。
                if (p2 != Vector2.Zero)
                {
                    float a = HyperbolaCurveAThroughPoint(p0, p2, halfX, halfY, axis);
                    float b = a * (halfX > 1e-4f ? halfY / halfX : 1f);
                    float r = axis == CurveAxis.TransverseY ? b : a;              // 实半轴
                    float sh = MathF.Sinh(HyperbolaTMaxOf(p0, p2, axis, r));
                    float dx = MathF.Abs(p2.X - p0.X), dy = MathF.Abs(p2.Y - p0.Y);
                    // 实轴那一侧的伸展**正好停在那个点的坐标上**（画到那儿为止），另一侧是 `半轴 × sinh`
                    halfX = axis == CurveAxis.TransverseY ? MathF.Max(halfX, a * sh) : MathF.Max(halfX, dx);
                    halfY = axis == CurveAxis.TransverseY ? MathF.Max(halfY, dy) : MathF.Max(halfY, b * sh);
                }
                return new RectF
                {
                    MinX = p0.X - halfX, MinY = p0.Y - halfY,
                    MaxX = p0.X + halfX, MaxY = p0.Y + halfY,
                };
            }

            case StrokeKind.Tangent:
            {
                // 正切一支：**正好就是那个框**（两条渐近线在左右边上、曲线截断在上下边上）——
                // 所以紧框 = 框本身，一句就够，而且**精确**（见 TangentAsymptoteLocal）。
                //
                // ⚠ 高度那一维必须**和 `TangentHalfHeightLocal` 用同一份算式**（含下限比例）：
                // 少算这一条，被顶高的那一截就落在紧框外面 —— 脏区少算 → 曲线尾巴留旧像素。
                float hx = MathF.Max(0.5f, MathF.Abs(p1.X - p0.X));
                float hy = MathF.Max(MathF.Abs(p1.Y - p0.Y), TangentMinAspect * hx);
                return new RectF
                {
                    MinX = p0.X - hx, MinY = p0.Y - hy,
                    MaxX = p0.X + hx, MaxY = p0.Y + hy,
                };
            }

            default:
            {
                // 正弦 / 余弦 / **波浪线**：**按解析式给纵向极值**。
                //
                // 2026-09-20 改过两轮：先是从"一个周期宽 × 各自的纵向范围"改成
                // "框宽 = 画多长"（那时多周期是正弦自己干的），第十六批又把这两件事**分给两种图形**：
                //   · 正弦 / 余弦：**框宽就是一个周期** → 周期数恒为 1（峰和谷都在里面）；
                //   · 波浪线：**周期由振幅定** → 周期数可以是小数（峰还没到就收笔）也可以是好几个。
                // 所以下面这套"按周期数算纵向极值"的算式对两种都成立，是同一份。
                //
                // ⚠ **必须精确，不能采样**：采样写的话极值点大多落在两个采样点之间，
                // 框会比曲线**小一点点** —— 那点差值正好是脏区少算的部分（曲线末梢会留旧像素）。
                // 分两种情况讨论虽然啰嗦，但每个都是教科书上的初等结论。
                float len = MathF.Abs(p1.X - p0.X);
                // **周期走共用的那一份算式**（见 `WavePeriodOf`）—— 波浪线的周期现在
                // **可以自带**（第三个定义元素，识别那条路写的），所以这里不能再用
                // "周期 = 振幅"那一句手抄；没有第三个点时它自己就落回老规矩。
                float t = WavePeriodOf(kind, p0, p1, p2);
                float cycles = t > 1e-3f ? len / t : 1f;
                float dy = WaveDyOf(p0, p1);
                float lo, hi;                       // 纵向占到的比例（乘 dy 就是位移）
                if (kind == StrokeKind.Cosine)
                {
                    // `f(u) = (1 − cos 2πu)/2`：u=0 处为 0，之后**单调升**到 u=1 处的 1
                    //（所以"谷"不一定出现：只有一个周期以上才落到 1）。
                    lo = 0f;
                    hi = cycles >= 1f ? 1f : (1f - MathF.Cos(MathF.Tau * cycles)) * 0.5f;
                }
                else
                {
                    // 正弦：峰在 1/4 处、谷在 3/4 处 —— **画到哪儿就认到哪儿**。
                    hi = cycles >= 0.25f ? 1f : MathF.Sin(MathF.Tau * cycles);
                    lo = cycles >= 0.75f ? -1f : 0f;
                }
                float yA = p0.Y + dy * lo, yB = p0.Y + dy * hi;
                return new RectF
                {
                    MinX = p0.X, MinY = MathF.Min(yA, yB),
                    MaxX = p0.X + t * cycles, MaxY = MathF.Max(yA, yB),
                };
            }
        }
    }

    /// <summary>实例版：读模型里的定义元素（静止态走它）。</summary>
    public RectF CurveBoxLocal()
        => CurveBoxOf(Kind, EffectiveAxis, CurvePointLocal(0), CurvePointLocal(1), CurvePointLocal(2));

    /// <summary>
    /// 四种曲线的**画布空间墨迹框** = 曲线矩形过变换取外接 ＋ 半笔宽。
    ///
    /// 和 <see cref="ParametricInkBoundsOf"/> 同一个套路（先把矩形整体过变换再取外接）：
    /// 只有这样，转过 30° 的曲线框才不会虚胖一大圈（那是脏区白重画的经典来源）。
    /// </summary>
    private static RectF CurveInkBoundsOf(StrokeKind kind, CurveAxis axis, in Matrix3x2 transform,
                                          in Matrix3x2 extra, Vector2 p0, Vector2 p1, Vector2 p2,
                                          float width)
        => TransformRect(CurveBoxOf(kind, axis, p0, p1, p2), transform * extra)
           .Inflate(width * 0.5f);

    /// <summary>
    /// 曲线的折线近似**段数**：按尺寸定（每段弦长 ≈ 16 像素），夹在 [24, 128]。
    /// 和 <see cref="EllipseSegments"/> 同一个口径——不然会出现"圆很顺、抛物线是折的"。
    /// </summary>
    private static int CurveSegments(float extent)
        => Math.Clamp((int)MathF.Ceiling(extent / 16f), 24, 128);

    /// <summary>
    /// 一组**局部坐标点**在给定变换下占的画布范围（= <see cref="PaddedBounds"/> 的算法，
    /// 但用的是**传进来的点**，不碰对象）。
    ///
    /// 需要的场景只有一个：**改几何的动作要在动手之前**算出"改完之后占哪块"，
    /// 好把新位置的脏区标出来（见 SetStrokeGeometryAction）。先改后算的话，
    /// 旧位置就再也问不出来了。
    ///
    /// **必须按"这个图形是怎么定义的"分流，和 <see cref="PaddedBounds"/> /
    /// <see cref="PreviewInkBounds"/> 一张表**（这三个是同一件事的三个入口）：
    ///   · 圆 / 椭圆 → 参数化外接（圆心 ± 半轴）。它们的两个控制点是**圆心 + 圆周点**，
    ///     那两个点的外接只是形体的一角——拿它当脏区，2026-09-19 实测过一次：
    ///     拖椭圆的轴端点松手后**左边一整块不画**（脏区窄了一列分块，那一列从
    ///     "起手把形体摘出内容层"之后就再没被重画过）。
    ///   · 三角形 / 平行四边形 → **每个顶点各自过变换再取外接**：转过的图形不能
    ///     "先取局部外接再整体转"，那样框明显虚胖。平行四边形的第四个顶点不在点表里，
    ///     少了它，脏区会漏掉整个右上角（那条边挪过去之后原地就留下一条擦不掉的残影）。
    ///   · 直线 / 箭头 / 矩形 / 图像 / 自由笔迹 → 照旧"点的外接过变换"。
    ///     它们不需要另开一条：端点的外接就是形体的外接（箭头的翅膀尖虽然伸到轴外
    ///     ~22 像素，但那个距离远小于块边长 256，"漏掉的那一小条"永远和端点带同处一块，
    ///     所以看不出缺块——2026-09-19 用像素探针验过）。
    ///   · **曲线**（抛物线 / 双曲线 / 正弦 / 余弦）→ 曲线自己的矩形（见
    ///     <see cref="CurveBoxOf"/>）。它们**必须**单独一档：双曲线的两支比"中心 ↔ 外角点"
    ///     远得多，正弦起点那一侧的半个周期也不在控制点的外接里。
    ///
    /// <paramref name="axis"/> 只有曲线用得上（抛物线开哪个口 / 双曲线哪条是实轴）：
    /// 朝向是"对象的样子"（见 <see cref="Stroke.CurveAxis"/>），所以**静态这一份也得知道它**，
    /// 否则"改几何前先算新位置占哪块"（这只在动手前算，对象还是旧的）就会算错。
    /// </summary>
    public static RectF PaddedBoundsOf(IReadOnlyList<Vector2> local, in Matrix3x2 transform, float width,
                                       StrokeKind kind = StrokeKind.Freehand,
                                       CurveAxis axis = CurveAxis.OpenUp)
    {
        if (local == null || local.Count == 0) return RectF.Empty;

        // 四种曲线（抛物线 / 双曲线 / 正弦 / 余弦）：走**曲线自己的那个矩形**
        // （不能是"控制点的外接"：双曲线会小掉一大圈、正弦会漏掉起点那一侧）。
        if (IsCurveKind(kind) && local.Count >= 2)
        {
            var q2 = local.Count >= 3 ? local[2] : Vector2.Zero;
            // 朝向先按种类归一（见 Stroke.NormalizeAxis）：调用方给的可能还是"另一族"的值
            // （比如 `new Stroke{Kind=Hyperbola}` 的字段默认是 0），不归一会把框算歪。
            return CurveInkBoundsOf(kind, NormalizeAxis(kind, axis), transform, Matrix3x2.Identity,
                                    local[0], local[1], q2, width).Inflate(2f);
        }

        // 圆 / 椭圆（含带焦点的那种）：走参数化外接（里面已经含了半个笔宽，见 ParametricInkBoundsOf）。
        if (local.Count >= 2 && (kind == StrokeKind.Circle || IsSemiAxisEllipse(kind)))
            return ParametricInkBoundsOf(kind, transform, Matrix3x2.Identity,
                                         local[0], local[1], width).Inflate(2f);

        // 三角形 / 平行四边形：顶点各自过变换（含平行四边形现推的第四个顶点）。
        if (kind is StrokeKind.Triangle or StrokeKind.Parallelogram && local.Count >= 3)
            return PolygonInkBoundsOf(kind, transform, Matrix3x2.Identity,
                                      local[0], local[1], local[2], width).Inflate(2f);

        // 空间坐标系：四个点 ＋ 箭头翅膀（翅膀会伸出点框）。
        if (kind == StrokeKind.SpaceCoordinate && local.Count >= 4)
        {
            Span<Vector2> pts = stackalloc Vector2[4];
            for (int i = 0; i < 4; i++) pts[i] = local[i];
            return SpaceAxisInkBoundsOf(pts, transform, Matrix3x2.Identity, width).Inflate(2f);
        }

        // 坐标系 / 数轴：定义点的外接 + 半笔宽。
        if (kind is StrokeKind.Coordinate or StrokeKind.NumberLine && local.Count >= 2)
        {
            int n = Math.Min(local.Count, 3);
            Span<Vector2> pts = stackalloc Vector2[3];
            for (int i = 0; i < n; i++) pts[i] = local[i];
            return AxisInkBoundsOf(pts[..n], transform, Matrix3x2.Identity, width).Inflate(2f);
        }

        var b = RectF.Empty;
        for (int i = 0; i < local.Count; i++) b.Add(local[i].X, local[i].Y);
        if (b.IsEmpty) return b;
        return TransformRect(b, transform).Inflate(width * 0.5f + 2f);
    }

    /// <summary>Bumped whenever the shape of this item changes. The cached GPU
    /// geometry is only trusted while it matches, which is what makes "add a
    /// point, redraw" work while a stroke is still being drawn.</summary>
    public int Revision { get; private set; }

    private int _builtRevision = -1;

    /// <summary>
    /// **辅助几何**的缓存（目前只有双曲线的两条虚线渐近线，见
    /// <see cref="BuildAuxGeometry"/>）。它是"一个对象、两段几何、两种线型"的落点：
    /// 主几何的线型/颜色是这个对象自己的（老师可能把它设成虚线了），
    /// 而渐近线**恒定是细虚线**，所以只能两段几何、两次描边。
    ///
    /// 它**不参与命中**（和坐标系网格同一条口径：辅助线点不中），
    /// 也**不进紧框**（画到 `±2a, ±2b` 为止，正好落在曲线自己的框里，见
    /// <see cref="HyperbolaAsymptoteLocal"/>）。
    /// </summary>
    public ID2D1Geometry Geometry2;
    private int _builtRevision2 = -1;

    /// <summary>
    /// 这个对象的**线宽跟不跟着缩放**（用户 2026-09-13 拍板、2026-09-20 落地）：
    ///
    ///   · **图形**（直线 / 矩形 / 椭圆 / 三角形 / 四种曲线 / 立体图形 / 坐标系…）→ **不跟着缩放**：
    ///     线宽是"这个图元自己的属性"，拉伸只是改形状。横着拉一个矩形，四条边还是原来那么粗
    ///     （否则横边粗、竖边不变，看着像书法笔）；
    ///   · **手写墨迹**（自由笔迹 / 荧光笔）与**图像** → **跟着缩放**（像图片一样）：
    ///     放大一段板书，笔迹当然要跟着变粗，不然就"糊"在放大的字里了。
    ///
    /// 落点在描边那一步：图形的几何**带变换**画（<see cref="BuildCanvasGeometry"/>），
    /// 于是描边发生在画布空间、宽度恒等于 <see cref="Width"/>。
    /// </summary>
    public bool KeepsWidth => Kind != StrokeKind.Freehand && Kind != StrokeKind.Image
                              && Kind != StrokeKind.TextBox && Kind != StrokeKind.Figure;

    /// <summary>
    /// **画布空间**几何（"线宽不变"那条路，见 <see cref="KeepsWidth"/>）。
    ///
    /// 为什么不能在描边前把 `Transform` 塞进 D2D 上下文：那样描边发生在**局部空间**，
    /// 变换作用在描边**之后** —— 线宽被一起缩放（非等比拉伸时"竖线比横线粗"）。
    /// 把变换折进几何里，描边就发生在画布空间，宽度就恒等于 `Width`、虚线的长短也不变。
    ///
    /// <paramref name="extra"/> 是拖动预览那种"额外再叠一层实时矩阵"的场合（见
    /// Overlay.DrawDragPreview）——注意它是**画布空间里再乘**，顺序在 `Transform` 之后。
    ///
    /// 缓存：局部几何按 <see cref="Revision"/>，这一层再记住**上次的矩阵**——
    /// 拖动时每帧矩阵都在变，所以每帧重建一次；一个图形几百个点，代价可忽略。
    /// 「手写墨迹」不走这条路（<see cref="KeepsWidth"/> 为 false 时直接返回局部几何），
    /// 所以"一次框选一万条笔迹去拉伸"不会在这里每帧重建一万次。
    /// </summary>
    public ID2D1Geometry BuildCanvasGeometry(ID2D1Factory1 factory, bool aux = false,
                                             Matrix3x2? extra = null)
    {
        var src = aux ? BuildAuxGeometry(factory) : BuildGeometry(factory);
        if (src == null) return null;
        if (!KeepsWidth) return src;

        var m = Transform * (extra ?? Matrix3x2.Identity);
        if (_canvasGeoRevision != Revision || _canvasGeoMatrix != m)
        {
            if (_canvasGeo != null) { _canvasGeo.Dispose(); _canvasGeo = null; LiveGeometries--; }
            if (_canvasGeoAux != null) { _canvasGeoAux.Dispose(); _canvasGeoAux = null; LiveGeometries--; }
            _canvasGeoRevision = Revision;
            _canvasGeoMatrix = m;
        }

        var slot = aux ? _canvasGeoAux : _canvasGeo;
        if (slot == null)
        {
            if (m.IsIdentity) return src;          // 单位变换：不用白包一层
            slot = factory.CreateTransformedGeometry(src, m);
            LiveGeometries++;
            if (aux) _canvasGeoAux = slot; else _canvasGeo = slot;
        }
        return slot;
    }

    private ID2D1Geometry _canvasGeo;
    private ID2D1Geometry _canvasGeoAux;
    private Matrix3x2 _canvasGeoMatrix = Matrix3x2.Identity;
    private int _canvasGeoRevision = -1;

    /// <summary>
    /// 颜色 / 粗细这类"不改几何、但改了墨迹范围"的属性变过之后调用。
    ///
    /// 为什么需要：<see cref="InkBounds"/> 按 <see cref="Revision"/> 缓存，
    /// 而 `Color` / `Width` 是裸字段——直接改它们缓存不会失效，于是**改完粗细，
    /// 选中框还按旧的半宽算**（框和墨对不上）。这里顺手也让几何缓存失效，
    /// 多一次重建，一次性代价可以接受。
    /// </summary>
    public void InvalidateMetrics() => Revision++;

    /// <summary>Scratch field used by the spatial index to avoid returning the
    /// same stroke twice for one query without allocating a set.</summary>
    public int QueryStamp;

    /// <summary>
    /// 空间索引登记过的格子键。**移除时按这份快照走**——对象的包围盒
    /// 可能在 Insert 之后变过（文本框长高、改宽），按当前包围盒 Remove
    /// 会漏掉旧格子，留下"框选还能选中、再点删除没反应"的幽灵。
    /// </summary>
    internal long[] GridKeys;

    public static long ReleasedGeometries;

    /// <summary>
    /// 当前**存活**的几何对象数（诊断用）。
    ///
    /// 删了东西内存不降时，第一个要问的就是"几何释放了没有"。只有
    /// ReleasedGeometries（累计释放）看不出这个——累计值是单调涨的，
    /// 释放了 100 条又新建了 100 条，它照样涨。
    /// </summary>
    public static long LiveGeometries;

    /// <summary>Releases the cached Direct2D geometry.</summary>
    public void Release()
    {
        if (Geometry != null)
        {
            ReleasedGeometries++;
            Geometry.Dispose();
            Geometry = null;
            LiveGeometries--;
        }
        // 辅助几何（双曲线的虚线渐近线）也是缓存，同样要放——理由和下面那张位图一样：
        // 漏一处，"删掉之后内存不降"就是必然的。
        Geometry2?.Dispose();
        Geometry2 = null;
        // "线宽不变"那条路的画布空间几何（见 BuildCanvasGeometry）：同样是缓存，同样要放。
        if (_canvasGeo != null) { _canvasGeo.Dispose(); _canvasGeo = null; LiveGeometries--; }
        if (_canvasGeoAux != null) { _canvasGeoAux.Dispose(); _canvasGeoAux = null; LiveGeometries--; }
        _canvasGeoRevision = -1;
        // 图像对象还挂着一张 D2D 位图（可能很大：一张 800×600 的截图约 2MB）。
        // 漏掉这一句，"擦掉截图之后内存不降"就是必然的。释放之后再画会按需重建。
        Image?.Release();
        _builtRevision = -1;
        _builtRevision2 = -1;
    }

    /// <summary>
    /// 深拷贝（复制 / 粘贴用）。**Id 留 0**，由文档入册时分配——
    /// 复制出来的必须是新身份，否则撤销和多选会指向错的对象。
    /// </summary>
    public Stroke Clone()
    {
        var c = new Stroke
        {
            Tool = Tool,
            Kind = Kind,
            Color = Color,
            Width = Width,
            Dash = Dash,          // 线型也是"这一条的样子"，复制要跟着走
            Grid = Grid,          // 坐标系网格同理（它是对象自己的样子，不是全局设置）
            CurveAxis = CurveAxis,// 曲线朝向同理（双曲线哪条是实轴；抛物线现在是现推的）
            ShowAsymptotes = ShowAsymptotes,   // 双曲线画不画那两条虚线渐近线
            FocusTriangle = FocusTriangle,     // 椭圆（带焦点）画不画焦点三角形
            FocusPointU = FocusPointU,         // 焦点三角形的顶点 P 在椭圆上哪个位置（NaN = 还没定过）
            Transform = Transform,
            TextBox = TextBox?.Clone(),   // 题卡内容也是"这一条的样子"，复制要独立一份
            // 交互矢量图：JSON 深拷贝一份，复制/收藏后改参数不会串到原对象。
            Figure = Figure == null ? null : PlotSpecJson.Read(PlotSpecJson.Write(Figure)),
        };
        // 用 AddPoint 加：它会顺便把 Bounds 和 Revision 收拾好。
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            c.AddPoint(p.X, p.Y, p.P, p.T);
        }
        // 图像像素**共享不复制**：一张截图几兆字节，复制一份纯属浪费；
        // 而且像素是不可变的（没有任何代码会改它），共享没有风险。
        c.Image = Image;
        // 擦除区间要跟着复制：不然"复制一份"会把已经擦掉的部分又画回来。
        c.Erased.AddRange(Erased);
        return c;
    }

    public void AddPoint(float x, float y, float p, double t)
    {
        Points.Add(new InkPoint { X = x, Y = y, P = p, T = t });
        Bounds.Add(x, y);
        Revision++;
    }

    /// <summary>Shapes are defined by their first and last point only.</summary>
    public void SetEnd(float x, float y)
    {
        if (Points.Count == 0) { AddPoint(x, y, 1f, 0); return; }
        if (Points.Count == 1) AddPoint(x, y, 1f, 0);
        else
        {
            var p = Points[1];
            p.X = x; p.Y = y;
            Points[1] = p;
        }
        RecomputeBounds();
        Revision++;
    }

    /// <summary>
    /// 整批换掉控制点（**图形的定义**从这里改）。
    /// 参数收 <see cref="IReadOnlyList{T}"/>：调用方手里常常只有"只读的一组点"
    /// （例如浮动层画临时几何），没必要为了传参再复制一个数组。
    /// </summary>
    public void SetPoints(IReadOnlyList<Vector2> pts)
    {
        Points.Clear();
        for (int i = 0; i < pts.Count; i++)
            Points.Add(new InkPoint { X = pts[i].X, Y = pts[i].Y, P = 1f, T = 0 });
        RecomputeBounds();
        Revision++;
    }

    /// <summary>
    /// 文本框**实际画出来的**世界矩形（含 Transform）。用 <see cref="TextBoxData.LayoutHeight"/>
    /// 而不是 Points/AABB 的高度：排版长高之后、包围盒还没来得及同步时，删除/重绘
    /// 必须按这张真实卡片擦，否则最后几行会留在屏幕上。
    /// </summary>
    internal RectF TextBoxWorldRect()
    {
        if (Kind != StrokeKind.TextBox || TextBox == null || Points.Count == 0)
            return RectF.Empty;
        var o = new Vector2(Points[0].X, Points[0].Y);
        var r = new RectF
        {
            MinX = o.X, MinY = o.Y,
            MaxX = o.X + TextBox.Width, MaxY = o.Y + TextBox.LayoutHeight,
        };
        if (Transform.IsIdentity) return r;
        var p0 = Vector2.Transform(new Vector2(r.MinX, r.MinY), Transform);
        var p1 = Vector2.Transform(new Vector2(r.MaxX, r.MinY), Transform);
        var p2 = Vector2.Transform(new Vector2(r.MinX, r.MaxY), Transform);
        var p3 = Vector2.Transform(new Vector2(r.MaxX, r.MaxY), Transform);
        return new RectF
        {
            MinX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X)),
            MinY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y)),
            MaxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X)),
            MaxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y)),
        };
    }

    /// <summary>把题卡的包围盒控制点同步成"左上角 + 宽高"。
    /// 宽度/折叠状态一变就调用；只改 `Points` 的两个角，**不动 Transform、不动内容**。
    /// </summary>
    internal void SyncTextBoxBounds()
    {
        if (Kind != StrokeKind.TextBox || TextBox == null) return;
        _ = TextBox.EnsureLayout();      // 折叠/改宽之后先把高度算出来，再同步两个角
        Vector2 o = Points.Count > 0 ? new Vector2(Points[0].X, Points[0].Y) : Vector2.Zero;
        SetTextBoxRect(o, TextBox.Width, TextBox.LayoutHeight);
    }

    /// <summary>
    /// 按"左上角 + 宽高"重设文本框的局部矩形（拖左边/上边改大小时原点也要跟着走）。
    /// 不动 Transform、不动内容；只负责包围盒和几何版本。
    /// </summary>
    internal void SetTextBoxRect(Vector2 origin, float width, float height)
    {
        if (Kind != StrokeKind.TextBox || TextBox == null) return;
        TextBox.Width = MathF.Max(1f, width);
        TextBox.Height = MathF.Max(1f, height);
        Points.Clear();
        Points.Add(new InkPoint { X = origin.X, Y = origin.Y, P = 1f, T = 0 });
        Points.Add(new InkPoint { X = origin.X + TextBox.Width, Y = origin.Y + TextBox.Height, P = 1f, T = 0 });
        RecomputeBounds();
        Revision++;
    }

    /// <summary>只挪一个控制点（顶点拖动走这里，**不动其它点、不动变换**）。</summary>
    public void SetPoint(int index, Vector2 p)
    {
        if (index < 0 || index >= Points.Count) return;
        var v = Points[index];
        v.X = p.X; v.Y = p.Y;
        Points[index] = v;
        RecomputeBounds();
        Revision++;
    }

    /// <summary>包围盒重算。它同时是脏区、命中测试和空间索引的依据，改点之后必须重算。</summary>
    internal void RecomputeBounds()
    {
        Bounds = RectF.Empty;

        // 四种新曲线**不能直接用"控制点的外接"**当包围盒：
        //   · 双曲线的两支伸得比"中心 ↔ 外角点"远得多（两个方向都要伸到 2 倍），
        //     照控制点算出来的框会小掉一大圈——而这个框是空间索引（命中粗筛）的依据，
        //     小掉的后果是"曲线上有些地方点不中"；
        //   · 正弦 / 余弦的三个控制点里有一个是极值点，但**起点那一侧的半个周期**
        //     本来就不在控制点的外接里（起点在轴上，曲线却先往上跑）。
        // 所以这几档走"曲线自己的那个矩形"（和 <see cref="CurveBoxLocal"/> 同一份算式）。
        if (IsCurveKind(Kind) && Points.Count >= 2)
        {
            Bounds = CurveBoxLocal();
            return;
        }

        if (Kind == StrokeKind.Sector && Points.Count >= 3)
        {
            Bounds = RectF.Empty;
            foreach (var p in SectorOutlinePoints()) Bounds.Add(p.X, p.Y);
            return;
        }

        // **长方体**同理：它的第三条棱伸到"正面 ∪ 背面"，
        // 而背面左上 / 右下两个角里只有一个是控制点（第三个点存的是背面右下角），
        // 照控制点算出来的框会**少了背面那一角**（命中粗筛在那儿就点不中）。
        if (Kind == StrokeKind.Cuboid && Points.Count >= 2)
        {
            var (x0, y0, x1, y1) = CuboidFrontLocal();
            float d = CuboidDepthLocal();
            Bounds.Add(x0, y0);
            Bounds.Add(x1, y1);
            Bounds.Add(x0 + d, y0 - d);
            Bounds.Add(x1 + d, y1 - d);
            return;
        }

        // **球**：包围盒是**那个圆的外接方形**（圆心 ± 半径），不是拖出来的矩形——
        // 拖长了（400×200）的时候圆是内切的，照矩形算框会左右各空出一截。
        if (Kind == StrokeKind.Sphere && Points.Count >= 2)
        {
            var (sc, sr) = SphereLocal();
            Bounds.Add(sc.X - sr, sc.Y - sr);
            Bounds.Add(sc.X + sr, sc.Y + sr);
            return;
        }

        // **棱柱 / 棱锥 / 棱台**：包围盒取**真正的那些顶点**（底面 n 个 ∪ 顶面 n 个），
        // 不是"底面外接框 ∪ 框+侧棱"——后者会**偏大**：底面的正 n 边形并不填满外接框
        //（六棱柱的顶端只到 0.866·ry），于是选中框的上沿会空出一条缝
        //（用户 2026-09-20："外接矩形上面会漏一块"）。逐点算既准又不受错切影响。
        // ⚠ 顶面那一份要用 `PrismTopLocal()` 现算，不能拿"底面 ＋ 侧棱向量"——
        //   棱锥（缩成一点）和棱台（缩小一份）的顶面都不在那儿。
        if (IsPrismFamily(Kind) && Points.Count >= 2)
        {
            foreach (var v in PrismBaseLocal()) Bounds.Add(v.X, v.Y);
            if (Points.Count >= 3)
                foreach (var v in PrismTopLocal()) Bounds.Add(v.X, v.Y);
            return;
        }

        // 空间坐标系：四个点 ＋ 三个箭头的翅膀（y 斜着，翅膀会伸到点框外面）。
        if (Kind == StrokeKind.SpaceCoordinate && Points.Count >= 4)
        {
            Span<Vector2> sp = stackalloc Vector2[4];
            for (int i = 0; i < 4; i++) sp[i] = new Vector2(Points[i].X, Points[i].Y);
            Bounds = SpaceAxisInkBoundsOf(sp, Matrix3x2.Identity, Matrix3x2.Identity, Width);
            return;
        }

        if (Kind == StrokeKind.TextBox && TextBox != null && Points.Count >= 1)
        {
            var o = new Vector2(Points[0].X, Points[0].Y);
            Bounds.Add(o.X, o.Y);
            Bounds.Add(o.X + TextBox.Width, o.Y + TextBox.LayoutHeight);
            return;
        }

        foreach (var p in Points) Bounds.Add(p.X, p.Y);
    }

    /// <summary>
    /// 脏区与命中测试用的外扩包围盒（**画布坐标**）。
    /// 在墨迹范围基础上再留 2 像素余量——抗锯齿的边缘会跑出精确包围盒一点点，
    /// 漏了就会在屏幕上留一条发丝一样的残影。
    ///
    /// **两种口径**（和 <see cref="WorldInkBounds"/> 对齐，别让它俩走岔）：
    ///   · **直线 / 箭头**：`墨迹框 + 2`。它们的墨迹框已经含了半个笔宽（圆头线帽的半径），
    ///     旧写法 `WorldBounds.Inflate(半宽 + 2)` 里的 `WorldBounds` 是"局部中心线框转过去
    ///     再取外接"，对转过的直线同样虚胖——脏区会白重画一大片、命中也会连带变松。
    ///   · **其余种类**：照旧 `WorldBounds.Inflate(半宽 + 2)`，**一个数都不改**。
    ///
    /// 外扩量 = 半个笔宽（+2 像素余量）。**2026-09-14 起这个数是精确的**：
    /// 墨迹现在是"中心线 + 等宽描边 + 圆头圆角"，离中心线最远就是半个笔宽，
    /// 而端帽/拐角都是半径 = 半宽 的圆弧，不会再多伸出去。
    /// （以前自己拼轮廓、内角要补到两条内边的交点，最远能到好几倍半宽，
    /// 那时这个系数必须留得很大——现在就按事实来。）
    /// </summary>
    public RectF PaddedBounds
    {
        get
        {
            // 文本框按**实际画出来的卡片**（含排版后的高度）算：Points 的包围盒
            // 可能在自动长高后还没同步，按它做脏区/命中会漏掉最后几行。
            if (Kind == StrokeKind.TextBox && TextBox != null)
            {
                var r = TextBoxWorldRect();
                if (!r.IsEmpty) return r.Inflate(2f);
            }
            return IsParametricShape
                ? WorldInkBounds.Inflate(2f)
                : WorldBounds.Inflate(MaxHalfWidth + 2f);
        }
    }

    private RectF _inkBounds = RectF.Empty;
    private int _inkBoundsRevision = -1;

    /// <summary>
    /// **墨迹**的包围盒（局部坐标）：中心线往外扩半个笔宽。
    ///
    /// 和 <see cref="Bounds"/>（中心线）是两件事。要"这一笔在屏幕上占了多大一块"，
    /// 必须用它——中心线包围盒对宽笔来说小得离谱：64 像素宽的荧光笔，
    /// 屏幕上是一条 64 像素宽的带子，而中心线包围盒只是一个点或一条细线。
    /// **选中框就吃这个数**（以前用中心线，宽笔选中之后框压在墨里面，看着就不对）。
    ///
    /// 按 Revision 缓存：选区每帧都要问它，不能每次都遍历点。
    /// </summary>
    public RectF InkBounds
    {
        get
        {
            if (_inkBoundsRevision == Revision) return _inkBounds;

            if (Kind == StrokeKind.Sector && Points.Count >= 3)
            {
                float hw0 = Width * 0.5f;
                var rr = RectF.Empty;
                foreach (var p in SectorOutlinePoints()) rr.Add(p.X, p.Y);
                rr.MinX -= hw0; rr.MinY -= hw0; rr.MaxX += hw0; rr.MaxY += hw0;
                _inkBounds = rr;
                _maxHalfWidth = hw0;
                _inkBoundsRevision = Revision;
                return rr;
            }

            // **长方体 / 棱柱一族 / 球**是仅有的几个例外：它们的墨迹伸到"控制点的外接"之外
            //（球反过来——是**控制在画出来的东西之外**，所以照控制点算框会虚胖）——
            //   · 长方体：背面左上 / 右下两个角里只有一个是控制点；
            //   · 棱柱一族：第三个控制点是**顶面中心**，而顶面那一圈是从"底面 ＋ 顶上那个中心"
            //     算出来的，**整个在控制点外面**（照控制点算出来的框，上沿会少掉一大块：
            //     用户 2026-09-20 说的"外接矩形上面会漏一块"）；
            //   · 球：拖长了的时候圆是内切的，控制点（矩形）比圆大。
            // 这几族的 `Bounds` 都已经按**真实几何**算过了（见 RecomputeBounds）——直接用"它 ＋ 半笔宽"。
            if ((Kind == StrokeKind.Cuboid || Kind == StrokeKind.Sphere || IsPrismFamily(Kind))
                && Points.Count >= 2)
            {
                float hw0 = Width * 0.5f;
                var rr = Bounds;
                rr.MinX -= hw0; rr.MinY -= hw0; rr.MaxX += hw0; rr.MaxY += hw0;
                _inkBounds = rr;
                _maxHalfWidth = hw0;
                _inkBoundsRevision = Revision;
                return rr;
            }

            // 空间坐标系：四个点 ＋ 三个箭头的翅膀（翅膀会伸出点框，见那个函数的注释）。
            if (Kind == StrokeKind.SpaceCoordinate && Points.Count >= 4)
            {
                Span<Vector2> sp = stackalloc Vector2[4];
                for (int i = 0; i < 4; i++) sp[i] = new Vector2(Points[i].X, Points[i].Y);
                var rr = SpaceAxisInkBoundsOf(sp, Transform, Matrix3x2.Identity, Width);
                _inkBounds = rr;
                _maxHalfWidth = Width * 0.5f;
                _inkBoundsRevision = Revision;
                return rr;
            }

            var r = RectF.Empty;
            // **初值必须是 0，不能是"标称半宽"**：有压感的笔迹可能比标称**细**
            // （满压才等于标称），初值取标称的话这些笔迹会一律报标称值——
            // 症状是脏区偏大、细笔迹的命中范围偏宽（2026-09-20 `--pressuretest` 抓到的：
            // "轻写那条按它自己的最粗处算" 那一条报出 6.00 vs 6.00）。
            float maxHalf = 0f;
            for (int i = 0; i < Points.Count; i++)
            {
                // 有压感的笔迹**逐点各算各的半宽**（宽的地方要算进去，否则紧框框不住墨）。
                // 图像对象 Width = 0、也没有压感，所以这里算出来就是它自己的矩形。
                float hw = HalfWidthAt(i);
                if (hw > maxHalf) maxHalf = hw;
                r.Add(Points[i].X - hw, Points[i].Y - hw);
                r.Add(Points[i].X + hw, Points[i].Y + hw);
            }
            _inkBounds = r;
            _maxHalfWidth = maxHalf > 0f ? maxHalf : Width * 0.5f;   // 没有点时退回标称
            _inkBoundsRevision = Revision;
            return r;
        }
    }

    /// <summary>
    /// 第 <paramref name="i"/> 个采样点处的**半宽**（局部像素）。
    ///
    /// 没压感（或压感关掉）时恒等于 <c>Width / 2</c>——也就是 2026-09-14 以来那条等宽口径，
    /// 所以"没有压感的设备"走的还是原来那条路，一个像素都不变。
    /// </summary>
    public float HalfWidthAt(int i)
    {
        float hw = Width * 0.5f;
        if (!HasPressure || !PressureWidth.Enabled) return hw;
        if (i < 0 || i >= Points.Count) return hw;
        return PressureWidth.HalfWidth(Width, Points[i].P);
    }

    private float _maxHalfWidth = -1f;

    /// <summary>
    /// 这一笔**最粗那一处**的半宽（局部像素）——紧框、命中、两种橡皮都用它。
    ///
    /// 为什么不继续用 <c>Width / 2</c>：重压的地方比标称宽 50%，按标称算就会
    /// "看得见却点不中"（老师的原话会是"这一笔选不上"），脏区也会漏掉最粗的那一圈。
    /// 和 <see cref="InkBounds"/> 共用同一档缓存，每帧问它不心疼。
    /// </summary>
    public float MaxHalfWidth
    {
        get
        {
            _ = InkBounds;                      // 顺带把 _maxHalfWidth 算出来（同一趟循环）
            return _maxHalfWidth >= 0f ? _maxHalfWidth : Width * 0.5f;
        }
    }

    /// <summary>
    /// 是不是"由**两个端点**定义、中间是空的"那两种图形（直线 / 箭头）。
    ///
    /// 这一个判断现在管着"墨迹范围"那条特殊口径（见 <see cref="LineLikeInkBounds"/>）：
    /// 直线/箭头的局部 AABB 四个角**根本不在线上**，所以"先把框转过去再取外接矩形"会虚胖。
    /// </summary>
    internal bool IsLineLike => Kind is StrokeKind.Line or StrokeKind.Arrow;

    /// <summary>
    /// 是不是"由**参数**定义的图形"（端点 / 圆心半径 / 中心半轴）。
    ///
    /// 这一类对象的局部 AABB **不是**它自己的边界（直线斜着时角不在线上、圆的 AABB
    /// 四角在圆外、三角形的外接框有一半是空的），所以墨迹框一律按参数算（见
    /// <see cref="LineLikeInkBounds(Vector2, Vector2, in Matrix3x2)"/>、
    /// <see cref="ParametricInkBounds"/> 与 <see cref="PolygonInkBounds(Vector2, Vector2, Vector2, in Matrix3x2)"/>），
    /// **不能**用"把局部框整体转过去再取外接"那一套（2026-09-18/19 两轮踩过）。
    /// 自由笔迹 / 矩形 / 图像的局部 AABB 就是它们自己的边界，走老口径。
    /// </summary>
    internal bool IsParametricShape => IsShapeKind(Kind);

    /// <summary>
    /// 这几种是**参数化曲线**（抛物线 / 双曲线 / 正弦 / 余弦 / 波浪线 / 正切）：它们的墨迹框**不能**
    /// 用"控制点的外接"来算（理由见 <see cref="CurveBoxOf"/>）。
    ///
    /// **2026-09-20 收敛**：这句话原来在**六个地方各手写了一遍**（紧框 / 重算包围盒 /
    /// 世界墨迹框 / 预览墨迹框 / 是不是参数化图形 / 能不能编辑），加第五种曲线就得同时改六处，
    /// 漏一处就是"框算小了、曲线上有些地方点不中"（椭圆当年就这么错过）。现在只有这一份。
    /// </summary>
    public static bool IsCurveKind(StrokeKind kind)
        => kind is StrokeKind.Parabola or StrokeKind.Hyperbola
                or StrokeKind.Sine or StrokeKind.Cosine or StrokeKind.Wave
                or StrokeKind.Tangent;

    /// <summary>
    /// 这几种是**图形**（相对于自由笔迹 / 图像）：用"定义元素"描述、选中后能拖手柄改参数。
    ///
    /// **2026-09-20 收敛**：同一份名单原来写了三遍——这份（按 Kind）、
    /// <see cref="SelectionHandles.ShapeEditable"/>（按 Kind，现在转发到这里）、
    /// 以及引擎里的 `IsShapeTool`（**按 Tool**，判的是"这个工具画出的是图形还是自由笔迹"，
    /// 维度不同，那份留着，两边靠自检卡一致）。
    ///
    /// 立体图形（圆柱 / 圆锥）也算：它们同样是"两个控制点定形"，选中的框、移动、旋转、
    /// 存档都走图形那一套（`--shapebandtest` 的"名单一致"那条会卡住）。
    /// </summary>
    public static bool IsShapeKind(StrokeKind kind)
        => kind is StrokeKind.Line or StrokeKind.Arrow or StrokeKind.Circle or StrokeKind.Ellipse
                or StrokeKind.ConicEllipse
                or StrokeKind.Triangle or StrokeKind.Parallelogram
                or StrokeKind.Coordinate or StrokeKind.NumberLine
                or StrokeKind.Cylinder or StrokeKind.Cone or StrokeKind.ConeFrustum
                or StrokeKind.Sphere
                or StrokeKind.Cuboid or StrokeKind.Tetrahedron
                or StrokeKind.Prism or StrokeKind.Pyramid or StrokeKind.Frustum
                or StrokeKind.Star or StrokeKind.Trapezoid or StrokeKind.Sector
                or StrokeKind.SpaceCoordinate
           || IsCurveKind(kind);

    /// <summary>
    /// **"中心 ＋ 两个半轴"定义的那两种椭圆**（<see cref="StrokeKind.Ellipse"/> 与
    /// <see cref="StrokeKind.ConicEllipse"/>）——几何、紧框、两个半轴手柄、存档
    /// **全部共用一份**，差别只有"带不带焦点"。
    ///
    /// **为什么要有这一个判据函数**：仓库里"圆 / 椭圆"出现过的地方有六七处
    /// （紧框两处、轮廓折线、几何构建、手柄、姿态角读数……），加一种椭圆要是逐处去补
    /// `or StrokeKind.ConicEllipse`，那就是"同一个名单写多处"的老毛病——
    /// 漏一处的表现是"新椭圆画得出来，但点不中 / 紧框不对"（这种最难查）。
    /// 所以凡是"按这一族分支"的地方都问它。
    /// </summary>
    public static bool IsSemiAxisEllipse(StrokeKind kind)
        => kind is StrokeKind.Ellipse or StrokeKind.ConicEllipse;

    /// <summary>
    /// **棱柱 / 棱锥 / 棱台这一族**吗：底面都是那个正 n 边形、控制点都是"底面外接框两角 ＋
    /// 顶上那个中心"，所以几何、实虚判据、包围盒、档位、直／斜吸附**全是同一套**
    /// （见 <see cref="PrismBaseLocal"/> / <see cref="PrismTopLocal"/> / <see cref="PrismFamilyEdges"/>）。
    ///
    /// **为什么要有这一个判据函数**：这一族凡是"按种类分支"的地方都该问它，不该各写一份名单——
    /// 写三处就得改三处，漏一处就是"加了一种图形、某一处还按老样子算"（仓库里为这类事
    /// 栽过好几次，见 计划-图形工具.md 的教训那几条）。
    /// </summary>
    public static bool IsPrismFamily(StrokeKind kind)
        => kind is StrokeKind.Prism or StrokeKind.Pyramid or StrokeKind.Frustum;

    /// <summary>
    /// **旋转体那一族**吗（圆柱 / 圆锥 / 圆台 / 球）：四个**都是一次拖出外接矩形、一笔画完**，
    /// 几何全由那两个角派生（见 <see cref="SolidRectLocal"/>），而且被挡住的部分都是
    /// "远侧那一圈/半圈"（见 <see cref="SolidPieces"/>）。
    ///
    /// **为什么要有这个判据**：2026-09-20 加圆台和球时，"圆柱 / 圆锥"这两个名字
    /// 又散在了好几处（`Engine.UpdateShapePreview` 的逐帧写法、各处的 case 组）——
    /// **加一种旋转体就要挨个补**。凡是"按这一族分支"的地方都该问它
    /// （和 <see cref="IsPrismFamily"/> 是同一条规矩：名单只写一处）。
    /// </summary>
    public static bool IsRevolutionSolid(StrokeKind kind)
        => kind is StrokeKind.Cylinder or StrokeKind.Cone
                or StrokeKind.ConeFrustum or StrokeKind.Sphere;

    /// <summary>圆的**圆心** / 椭圆的**中心**（局部坐标，= 第一个控制点）。</summary>
    public Vector2 ShapeCenterLocal
    {
        get
        {
            var (c, _) = Endpoints();
            return new Vector2(c.X, c.Y);
        }
    }

    /// <summary>圆的**半径**（局部坐标）：圆心到圆周点的距离。</summary>
    public float CircleRadiusLocal
    {
        get
        {
            var (c, r) = Endpoints();
            return MathF.Sqrt((r.X - c.X) * (r.X - c.X) + (r.Y - c.Y) * (r.Y - c.Y));
        }
    }

    /// <summary>椭圆的**横半轴 a**（局部坐标，恒非负）：中心到外角点的横向距离。</summary>
    public float SemiAxisALocal
    {
        get
        {
            var (c, e) = Endpoints();
            return MathF.Abs(e.X - c.X);
        }
    }

    /// <summary>椭圆的**纵半轴 b**（局部坐标，恒非负）。</summary>
    public float SemiAxisBLocal
    {
        get
        {
            var (c, e) = Endpoints();
            return MathF.Abs(e.Y - c.Y);
        }
    }


    /// <summary>
    /// 墨迹包围盒的**画布坐标**版本。
    ///
    /// **两种口径**，按对象是怎么定义的分：
    ///   · **直线 / 箭头**：两个端点（含箭头翅膀尖）变换到画布空间后取外接矩形，再外扩半笔宽。
    ///     它们"由两个点定义"，局部 AABB 的四个角不在线上——旧口径
    ///     （`TransformRect(InkBounds, Transform)` = 框角整体转过去再取外接）对一条
    ///     转过 30° 的直线会把框撑到**接近两倍宽**（实测 962 vs 线自己 490，见 --shapetooltest）。
    ///   · **其余种类**（椭圆 / 圆 / 三角形 / 平行四边形 / 矩形 / 图像 / 自由笔迹）：
    ///     照旧"把局部墨迹框过一遍变换"——但**参数化图形**（前三类）走的也是自己那条
    ///     按定义算的路（见下面几行），只有矩形 / 图像 / 自由笔迹的局部 AABB
    ///     就是它们自己的边界，用这一条才是对的。
    /// </summary>
    public RectF WorldInkBounds
    {
        get
        {
            if (Kind == StrokeKind.SpaceCoordinate && Points.Count >= 4)
            {
                Span<Vector2> sp = stackalloc Vector2[4];
                for (int i = 0; i < 4; i++) sp[i] = new Vector2(Points[i].X, Points[i].Y);
                return SpaceAxisInkBoundsOf(sp, Transform, Matrix3x2.Identity, Width);
            }

            // **参数化图形一律自己算**，连"变换是单位阵"这一档也不例外：
            // 圆的墨迹圈是"圆心 ± r"，而它的两个控制点（圆心 + 圆周点）的 AABB 只是一条
            // 从圆心伸出去的细条（2026-09-19 出图核对时踩到：紧框算成 256×16）。
            // 这三条都是 O(1)，短路省不了什么。
            if (IsLineLike) return LineLikeInkBounds(Matrix3x2.Identity);
            if (Kind == StrokeKind.Circle || IsSemiAxisEllipse(Kind))
                return ParametricInkBounds(ShapeCenterLocal, RimLocalPoint(), Matrix3x2.Identity);
            if (Kind is StrokeKind.Triangle or StrokeKind.Parallelogram && Points.Count >= 3)
                return PolygonInkBounds(new Vector2(Points[0].X, Points[0].Y),
                                        new Vector2(Points[1].X, Points[1].Y),
                                        new Vector2(Points[2].X, Points[2].Y),
                                        Matrix3x2.Identity);
            // 坐标系 / 数轴：四个定义元素的外接 + **刻度半长**（刻度是唯一伸出外框的东西）。
            if (Kind is StrokeKind.Coordinate or StrokeKind.NumberLine && Points.Count >= 4)
                return AxisInkBounds(Matrix3x2.Identity);

            // 四种曲线：**曲线自己的那个矩形** ＋ 半笔宽。
            // 这一条**不能省**（省了就落到最后那条"点的外接过变换"上）：
            // 双曲线的两支会伸出"中心 ↔ 外角点"很远，正弦会漏掉起点那一侧的半个周期，
            // 而紧框是脏区 / 命中粗筛 / 导出裁切的依据（见 CurveBoxOf 的注释）。
            if (IsCurveKind(Kind) && Points.Count >= 2)
                return CurveInkBoundsOf(Kind, EffectiveAxis, Transform, Matrix3x2.Identity,
                                        CurvePointLocal(0), CurvePointLocal(1), CurvePointLocal(2), Width);

            var r = InkBounds;
            if (r.IsEmpty) return r;
            if (Transform.IsIdentity) return r;
            return TransformRect(r, Transform);
        }
    }

    /// <summary>第二个控制点（局部坐标）：直线的终点 / 圆的圆周点 / 椭圆的外角点。</summary>
    public Vector2 RimLocalPoint()
    {
        var (_, b) = Endpoints();
        return new Vector2(b.X, b.Y);
    }

    /// <summary>
    /// 圆 / 椭圆的**画布空间墨迹框**（参数化外接 + 半笔宽）。
    ///
    /// 为什么不能沿用"把局部框整体转过去再取外接"：椭圆局部是 `(a·cos t, b·sin t)`，
    /// 它的 AABB 是"中心 ±(a,b)"那个矩形；把**那个矩形**转 30° 再取外接，
    /// 得到的框比椭圆本身大一圈（和直线那轮的 962 vs 490 是同一个毛病）。
    ///
    /// 正确的外接（对任意仿射变换都成立，含镜像 / 缩放）：
    ///   x 的极值 = √((a·M11)² + (b·M21)²)、y 的极值 = √((a·M12)² + (b·M22)²)
    /// —— 因为 `A·cos t + B·sin t` 的振幅就是 `√(A² + B²)`。
    /// 旋转 θ 时代进去正是规格 9.3 写的 `√((a·cosθ)² + (b·sinθ)²)` / `√((a·sinθ)² + (b·cosθ)²)`：
    /// 是同一个东西，这样写连镜像、缩放都不用特判。
    ///
    /// 圆就是 `a = b = r` 的特例，所以两者共用一个函数（圆的紧框 = `圆心 ± r`）。
    /// <paramref name="extra"/> 与直线那边同一个用途：拖动预览要叠一层实时矩阵。
    /// </summary>
    public RectF ParametricInkBounds(Vector2 centerLocal, Vector2 rimLocal, in Matrix3x2 extra)
        => ParametricInkBoundsOf(Kind, Transform, extra, centerLocal, rimLocal, Width);

    /// <summary>
    /// 上面那条式子的**静态核心**（不读实例字段：种类 / 变换 / 笔宽都由调用方给）。
    ///
    /// 抽出来的理由：改几何的动作要在**动手之前**算"改完占哪块"（见
    /// <see cref="PaddedBoundsOf"/>），而那时手里只有一组点，没有对象。
    /// 同一个式子写两份的下场，2026-09-19 已经实测过一次：脏区那份漏掉了圆 / 椭圆的
    /// 参数化那一档，于是"拖轴端点松手之后形体缺一块"。
    /// </summary>
    private static RectF ParametricInkBoundsOf(StrokeKind kind, in Matrix3x2 transform, in Matrix3x2 extra,
                                               Vector2 centerLocal, Vector2 rimLocal, float width)
    {
        var m = transform * extra;
        float dx = rimLocal.X - centerLocal.X, dy = rimLocal.Y - centerLocal.Y;
        float a = kind == StrokeKind.Circle
            ? MathF.Sqrt(dx * dx + dy * dy)      // 圆：a = b = 半径
            : MathF.Abs(dx);
        float b = kind == StrokeKind.Circle
            ? a
            : MathF.Abs(dy);

        float hx = MathF.Sqrt((a * m.M11) * (a * m.M11) + (b * m.M21) * (b * m.M21));
        float hy = MathF.Sqrt((a * m.M12) * (a * m.M12) + (b * m.M22) * (b * m.M22));
        var c = Vector2.Transform(centerLocal, m);
        var r2 = new RectF
        {
            MinX = c.X - hx, MinY = c.Y - hy,
            MaxX = c.X + hx, MaxY = c.Y + hy,
        };
        return r2.Inflate(width * 0.5f);
    }

    /// <summary>
    /// 三角形 / 平行四边形的**画布空间墨迹框**：**各个顶点**过变换后取外接 ＋ 半笔宽。
    ///
    /// 为什么不能沿用"把局部框整体转过去再取外接"（H 段那条老账）：三角形的局部 AABB
    /// 有一半是空的（斜边那一侧的角根本不在三角形上），转过去再取外接会明显虚胖，
    /// 而这个框是**脏区、命中粗筛、导出裁切**的依据——虚胖就是每帧白重画。
    /// 平行四边形的第四个顶点不在控制点表里，这里现推（<see cref="ParallelogramFourth"/>）。
    ///
    /// <paramref name="extra"/> 与另外两处同一个用途：拖动预览要再叠一层实时矩阵。
    /// </summary>
    public RectF PolygonInkBounds(Vector2 p0, Vector2 p1, Vector2 p2, in Matrix3x2 extra)
        => PolygonInkBoundsOf(Kind, Transform, extra, p0, p1, p2, Width);

    /// <summary>
    /// 上面那条式子的**静态核心**，理由和 <see cref="ParametricInkBoundsOf"/> 一样：
    /// 改几何的动作要在动手之前按"一组点"算包围盒（见 <see cref="PaddedBoundsOf"/>）。
    /// </summary>
    private static RectF PolygonInkBoundsOf(StrokeKind kind, in Matrix3x2 transform, in Matrix3x2 extra,
                                            Vector2 p0, Vector2 p1, Vector2 p2, float width)
    {
        var m = transform * extra;
        var r = RectF.Empty;
        var q0 = Vector2.Transform(p0, m); r.Add(q0.X, q0.Y);
        var q1 = Vector2.Transform(p1, m); r.Add(q1.X, q1.Y);
        var q2 = Vector2.Transform(p2, m); r.Add(q2.X, q2.Y);
        if (kind == StrokeKind.Parallelogram)
        {
            var q3 = Vector2.Transform(ParallelogramFourth(p0, p1, p2), m);
            r.Add(q3.X, q3.Y);
        }
        return r.Inflate(width * 0.5f);
    }

    /// <summary>
    /// 用**临时控制点**算墨迹紧框——手势期（模型还没动）的选中框与脏区都按它算。
    ///
    /// 存在的理由：<see cref="WorldInkBounds"/> 读的是模型里的点，而拖动预览那几个点
    /// 只在 <see cref="Engine"/> 的预览数组里（见 计划-图形工具.md 8.1①）。
    /// 口径必须和静止态**一模一样**，否则拖动中每边差几个像素、松手那一瞬框会跳一下。
    /// 三档按对象是"怎么定义的"分流，和 <see cref="WorldInkBounds"/> 那张表一一对应。
    /// </summary>
    public RectF PreviewInkBounds(IReadOnlyList<Vector2> local, in Matrix3x2 extra)
    {
        if (local == null || local.Count < 2) return RectF.Empty;
        if (IsLineLike) return LineLikeInkBounds(local[0], local[^1], extra);
        if (Kind == StrokeKind.Circle || IsSemiAxisEllipse(Kind))
            return ParametricInkBounds(local[0], local[1], extra);
        // 曲线：除了点表还要知道"朝向"，所以转给专门那个入口（<see cref="CurvePreviewInkBounds"/>），
        // 口径和静止态、脏区那两份**完全同源**。
        if (IsCurveKind(Kind))
            return CurvePreviewInkBounds(local, extra);
        if (Kind is StrokeKind.Triangle or StrokeKind.Parallelogram && local.Count >= 3)
            return PolygonInkBounds(local[0], local[1], local[2], extra);
        // 坐标系 / 数轴 / 空间坐标系：和静止态同一条式子（定义点的外接 + 半笔宽），口径不能差一分。
        if (Kind is StrokeKind.Coordinate or StrokeKind.NumberLine && local.Count >= 2)
        {
            int n = Math.Min(local.Count, 3);
            Span<Vector2> pts = stackalloc Vector2[3];
            for (int i = 0; i < n; i++) pts[i] = local[i];
            return AxisInkBoundsOf(pts[..n], Transform, extra, Width);
        }
        if (Kind == StrokeKind.SpaceCoordinate && local.Count >= 4)
        {
            Span<Vector2> pts = stackalloc Vector2[4];
            for (int i = 0; i < 4; i++) pts[i] = local[i];
            return SpaceAxisInkBoundsOf(pts, Transform, extra, Width);
        }

        var b = RectF.Empty;
        for (int i = 0; i < local.Count; i++) b.Add(local[i].X, local[i].Y);
        // 半宽按**最粗处**算（有压感的那一笔，重压的地方比标称宽 50%）。
        return b.IsEmpty ? b : TransformRect(b, Transform * extra).Inflate(MaxHalfWidth);
    }

    /// <summary>
    /// 曲线（抛物线 / 双曲线 / 正弦 / 余弦）的**临时几何**包围盒：
    /// 拖手柄 / 拖控制点时模型还没动，框必须按预览点算，口径和静止态一字不差。
    ///
    /// 单独一个入口、不塞进 <see cref="PreviewInkBounds"/>，是因为那条路按"点表"分流，
    /// 而曲线这里要多传一个 <see cref="CurveAxis"/>（朝向）——两者签名对不上。
    /// </summary>
    public RectF CurvePreviewInkBounds(IReadOnlyList<Vector2> local, in Matrix3x2 extra)
    {
        if (local == null || local.Count < 2) return RectF.Empty;
        var q2 = local.Count >= 3 ? local[2] : Vector2.Zero;
        return CurveInkBoundsOf(Kind, EffectiveAxis, Transform, extra, local[0], local[1], q2, Width);
    }

    /// <summary>
    /// 直线 / 箭头的**画布空间墨迹框**：端点（+ 箭头翅膀尖）过变换之后取 min/max，
    /// 再外扩 `Width * 0.5`（圆头线帽的半径——这是"墨迹"该有的口径）。
    ///
    /// <paramref name="extra"/> 是"再叠一层"的矩阵：拖动预览（移动/旋转手势）里，
    /// 屏幕上的姿态是"对象自己的变换 × 实时矩阵"，框必须按**同一个式子**算才贴得住
    /// （否则一条转 30° 的直线，框还是会按"把紧框整体转过去再取外接"撑成两倍宽）。
    ///
    /// 三个细节：
    ///   · 端点是取 `Points` 的**首尾两个**（不写死下标，形状万一多几个点也成立）；
    ///   · **箭头**要把头部的两个翅膀尖也算进来：它们不在两个端点之间，只按端点算会让
    ///     翅膀落在框外，而框又是脏区/命中/导出裁切的依据（漏了就是残影）；
    ///   · 外扩在**画布空间**做（不是先外扩再过变换）：描边宽度不随变换缩放，
    ///     这也是命中测试那边的既有近似（见 HitTestExact 的注释）。
    /// </summary>
    public RectF LineLikeInkBounds(in Matrix3x2 extra)
    {
        var (a, b) = Endpoints();
        return LineLikeInkBounds(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), extra);
    }

    /// <summary>
    /// 同上，但端点由调用方给（**局部坐标**）。
    ///
    /// 存在的理由只有一个：**拖端点的手势期模型还没动**，框必须按"临时几何"的两个端点算。
    /// 三种情况的框（静止 / 拖动预览 / 拖端点预览）因此共用同一个式子，也就不会出现
    /// "拖的时候每边多 2 像素、松手缩一下"那种口径不一致（2026-09-19）。
    /// 箭头按**传进来的端点**重算头部：预览改了端点，屏幕上那两条翅膀也跟着变，
    /// 框要是还按模型里的旧翅膀算，就会和画出来的箭头对不上。
    /// </summary>
    public RectF LineLikeInkBounds(Vector2 a, Vector2 b, in Matrix3x2 extra)
    {
        var m = Transform * extra;
        var r = RectF.Empty;
        void Add(Vector2 p) => r.Add(p.X, p.Y);
        Add(Vector2.Transform(a, m));
        Add(Vector2.Transform(b, m));
        if (Kind == StrokeKind.Arrow)
        {
            var (_, wingA, wingB) = ArrowHeadPoints(a, b);
            Add(Vector2.Transform(wingA, m));
            Add(Vector2.Transform(wingB, m));
        }
        return r.Inflate(Width * 0.5f);
    }

    /// <summary>Distance in pixels from a point to this item's outline.</summary>
    /// <summary>
    /// 画布坐标下的"点到这一笔有多远"。内部先反变换回局部坐标再量。
    ///
    /// 反变换之后距离的尺度会跟着变（对象被放大时局部距离看起来变小），
    /// 所以这里按对象的平均缩放折算回画布尺度。**这是个近似**；
    /// 精确判定用 HitTestExact（让 Direct2D 带着变换直接算）。
    /// </summary>
    public float DistanceToCanvas(float x, float y)
    {
        if (Transform.IsIdentity) return DistanceTo(x, y);

        Matrix3x2.Invert(Transform, out var inv);
        var p = Vector2.Transform(new Vector2(x, y), inv);
        return DistanceTo(p.X, p.Y) * AverageScale(Transform);
    }

    /// <summary>矩阵的平均缩放倍数（|行列式| 开方），用来在局部 / 画布尺度之间折算。</summary>
    private static float AverageScale(in Matrix3x2 m)
    {
        float det = m.M11 * m.M22 - m.M12 * m.M21;
        float s = MathF.Sqrt(MathF.Abs(det));
        return s > 1e-6f ? s : 1f;
    }

    /// <summary>
    /// 精确命中测试（**画布坐标**），<paramref name="tolerance"/> 是容差半径。
    ///
    /// 和 <see cref="DistanceToCanvas"/> 的分工：
    ///
    ///   · **自由笔迹**：带子本来就是"中心线两侧各半个笔宽"，所以"点到中心线的
    ///     距离"已经等价于精确判定，用那个更快，不必走这里。
    ///   · **图形**（直线 / 矩形 / 椭圆 / 箭头）：真实轮廓不是两个端点之间的
    ///     线段，DistanceTo 里只能用归一化半径之类的近似，缩放或旋转之后明显偏。
    ///     这里让 Direct2D 照着**画出来时用的同一条描边**去算，是精确的。
    ///
    /// 代价：一次几何构建（有缓存）+ 一次 COM 调用。所以只对**粗筛之后的
    /// 少量候选**调用，不要拿它去遍历整个文档。
    ///
    /// **线型（虚线/点线）刻意不参与这里**（下面照样传 `Gfx.Round`）：
    /// 虚线中间是**空的**，真按虚线去判，老师点在一段空白上就会"点不中"——
    /// 而他心里点的就是"那条虚线"。所以命中一律按**实线**算，虚线整条都能点中。
    /// </summary>
    public bool HitTestExact(float canvasX, float canvasY, float tolerance = 0f)
    {
        // AI 题卡是"填满的一块矩形"（和图像同义）：点在卡片里就算命中。
        // 不走几何：卡片没有描边中心线，硬按两点的线去算会出现"看得见却点不中"。
        if (Kind == StrokeKind.TextBox && TextBox != null && Points.Count >= 1)
        {
            var b = TextBoxWorldRect();
            return canvasX >= b.MinX - tolerance && canvasX <= b.MaxX + tolerance
                && canvasY >= b.MinY - tolerance && canvasY <= b.MaxY + tolerance;
        }

        // 交互矢量图：整张卡片都算命中（和 Image 一样），不像几何图形只认描边。
        if (Kind == StrokeKind.Figure && Points.Count >= 2)
        {
            var b = WorldBounds;
            return canvasX >= b.MinX - tolerance && canvasX <= b.MaxX + tolerance
                && canvasY >= b.MinY - tolerance && canvasY <= b.MaxY + tolerance;
        }

        // 字形填充对象：点在**填色区域**里就算命中（even-odd，孔里不算）。
        if (Kind == StrokeKind.Glyph && Points.Count >= 3)
        {
            bool inside = false;
            int i = 0;
            while (i < Points.Count)
            {
                int start = i;
                int end = i + 1;
                while (end < Points.Count && Points[end].P >= 0) end++;
                for (int k = start; k < end; k++)
                {
                    var a = Points[k];
                    var b = Points[k + 1 < end ? k + 1 : start];
                    if ((a.Y > canvasY) != (b.Y > canvasY) &&
                        canvasX < (b.X - a.X) * (canvasY - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
                i = end;
            }
            return inside;
        }

        // **"线宽不变"的对象**（图形，见 KeepsWidth）：几何本身就带变换（画布空间），
        // 所以查询点**不用**反变换，线宽也就是画布单位里的 Width —— 和屏幕上看到的
        // 一致。不这么走的话，拉伸过的图形会出现"看得见却点不中"（局部空间里量的线宽
        // 和画出来的不是一回事）。
        bool canvasSpace = KeepsWidth;
        var geo = canvasSpace ? BuildCanvasGeometry(Gfx.D2DFactory) : BuildGeometry(Gfx.D2DFactory);
        if (geo == null) return false;

        // 几何存在局部坐标里，把查询点反变换回去再测——等价于"带着变换去测"，
        // 但只用最简单的重载，少一层踩坑的机会。
        Vector2 p = new(canvasX, canvasY);
        if (!canvasSpace && !Transform.IsIdentity)
        {
            if (!Matrix3x2.Invert(Transform, out var inv)) return false;
            p = Vector2.Transform(p, inv);
        }

        // 图像是"填满的一块矩形"：点在矩形里就算命中，容差往四周扩一点。
        if (IsImage)
        {
            if (geo.FillContainsPoint(p)) return true;
            return tolerance > 0f && geo.StrokeContainsPoint(p, tolerance * 2f, Gfx.Round);
        }

        // 图形和笔迹现在都是"中心线 + 描边"（笔迹交 D2D 原生描边画，图形本来也是），
        // 所以命中判定统一走描边：线宽算进去，容差靠把线临时加粗来实现——
        // 于是"橡皮圆碰到这条线"就等价于"点落在加粗了 2r 的线上"，不用自己算距离。
        //
        // 线宽用**最粗处**（<see cref="MaxHalfWidth"/> × 2）：有压感的笔迹重压处比标称宽 50%，
        // 按标称算就会出现"看得见却点不中"。
        return geo.StrokeContainsPoint(p, MathF.Max(1f, MaxHalfWidth * 2f) + tolerance * 2f, Gfx.Round);
    }

    public float DistanceTo(float x, float y)
    {
        if (Points.Count == 0) return float.MaxValue;
        if (Points.Count == 1)
        {
            float dx0 = Points[0].X - x, dy0 = Points[0].Y - y;
            return MathF.Sqrt(dx0 * dx0 + dy0 * dy0);
        }

        // Distance to the *segments*, not just to the sample points. Measuring
        // to vertices only makes the eraser feel dead between samples, which is
        // exactly where a fast stroke has the fewest of them.
        float best = float.MaxValue;
        for (int i = 1; i < Points.Count; i++)
        {
            float d2 = DistToSegmentSq(x, y, Points[i - 1].X, Points[i - 1].Y,
                                      Points[i].X, Points[i].Y, out float t);
            // **被擦掉的那一段不算墨**：拆对象的时候缺口是真的（那一段对象都没了），
            // 改成区间表之后缺口只是"不画"，所以命中测试必须自己跳过——否则拿橡皮
            // 去点一个明明已经擦空的缺口，会把整条笔迹删掉。
            if (IsParamErased((i - 1) + t)) continue;
            if (d2 < best) best = d2;
        }

        // 图形（矩形/椭圆/箭头）不走这里：它们的轮廓不是"两个端点之间的线段"，
        // 近似会偏，命中判定统一走 HitTestExact（见 EraseAt）。
        return MathF.Sqrt(best);
    }

    private static float DistToSegmentSq(float px, float py, float ax, float ay, float bx, float by)
        => DistToSegmentSq(px, py, ax, ay, bx, by, out _);

    /// <summary>点到线段的距离平方；顺带给出最近点的参数 t（0..1），命中测试要用。</summary>
    private static float DistToSegmentSq(float px, float py, float ax, float ay,
                                         float bx, float by, out float t)
    {
        float vx = bx - ax, vy = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = vx * vx + vy * vy;
        t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - vx * t, dy = wy - vy * t;
        return dx * dx + dy * dy;
    }

    /// <summary>与矩形是否相交（画布坐标）。粗筛用，只看世界包围盒。</summary>
    public bool IntersectsRect(RectF r) => WorldBounds.Intersects(r);

    public bool IsShape => Kind != StrokeKind.Freehand;

    /// <summary>
    /// 笔迹的墨是"中心线 + 圆头圆角描边"，**由 Direct2D 自己算轮廓**
    /// （`DrawGeometry` + `Gfx.Round`），我们不再自己拼轮廓。
    ///
    /// 2026-09-14：用户实测后拍板——D2D 的观感比我们自己拼的轮廓好，
    /// 于是**把我们那一套整个删掉**（等宽带子、半圆端帽、外侧圆弧、内角补角、
    /// 压感→宽度映射，以及为它服务的几何细分缓存）。
    ///
    /// 代价（明确接受）：**一条笔画只能有一个宽度**——压感不再影响粗细。
    /// 采样点上仍然记着压力值（存档、将来要用还拿得到），只是渲染不吃它。
    /// </summary>
    public bool IsSinglePoint => Kind == StrokeKind.Freehand && Points.Count == 1;

    /// <summary>微移笔画（总路径 ≤ <see cref="TinyDotPx"/>）也算"点"。
    /// 2026-10-08（用户 B 案）：轻点时笔尖总会动 1~3px，按普通笔迹描就会描出小折钩、
    /// 形状各异的痕迹；这里统一当"点"画。半径取**整笔最大压力**（起手那几点压力最低，
    /// 取最大才符合"按多重、点多圆"），再压 <see cref="DotFactorCap"/> 上限。
    /// **live 与落地走同一判据** → 松手不再变形、点完起写不再换个样子。</summary>
    public bool RendersAsDot => IsSinglePoint
        || (Kind == StrokeKind.Freehand && Points.Count > 1 && PathLengthUpTo(TinyDotPx) <= TinyDotPx);

    /// <summary>"微移算点"的路径上限（画布像素）。</summary>
    public const float TinyDotPx = 4f;

    /// <summary>圆点宽度的上限（倍）：0.8 = 不再拱成大圆，但仍保留"压力越大点越大"到近满压。</summary>
    public const float DotFactorCap = 0.8f;

    /// <summary>圆点半径（"点/微移笔画"共用；渲染与回放都调它，保证一处口径）。</summary>
    public float DotRadius()
    {
        if (HasPressure && PressureWidth.Enabled)
        {
            float p = 0f;
            foreach (var q in Points) if (q.P > p) p = q.P;
            return MathF.Max(1f, Width * 0.5f * MathF.Min(PressureWidth.Factor(p), DotFactorCap));
        }
        return MathF.Max(1f, Width * 0.5f);      // 无压感（鼠标/触摸）：档位宽度的一半
    }

    /// <summary>从首点起的累计路径长度；一超过 <paramref name="limit"/> 提前返回（长笔不白算）。</summary>
    private float PathLengthUpTo(float limit)
    {
        float len = 0f;
        for (int i = 1; i < Points.Count; i++)
        {
            len += Vector2.Distance(new Vector2(Points[i - 1].X, Points[i - 1].Y),
                                    new Vector2(Points[i].X, Points[i].Y));
            if (len > limit) return len;
        }
        return len;
    }

    /// <summary>
    /// 套索判据用的**代表点集**（画布坐标），追加到 <paramref name="dst"/>。
    ///
    /// 三种对象各取"它自己那个形状"，不能一律拿 Points 顶上：
    ///   · **自由笔迹**：采样点。**跳过被像素橡皮擦掉的那一段**——擦掉的墨不算墨，
    ///     一条被擦掉中间一段的长横线上，缺口里的点会把"圈住左边半截"判成"圈住整条"；
    ///   · **图形**：<see cref="ShapeOutline"/> 的轮廓折线。**不能用两个端点**——
    ///     矩形 / 椭圆的端点是斜对角，拿它当代表点，等于"圈住外框的左上角"就算
    ///     "圈住了整个矩形"；
    ///   · **图像**：四个角。它是"填满的一块"，四角就是它的边界。
    ///
    /// 全部经 <see cref="Transform"/> 变换到画布坐标：套索是在屏幕上圈的，
    /// 判据必须在同一套坐标里（旋转过的对象尤其明显）。
    /// </summary>
    public void AppendRepresentativePoints(List<Vector2> dst)
    {
        if (Points.Count == 0) return;
        bool ident = Transform.IsIdentity;

        if (IsImage)
        {
            var b = Bounds;
            dst.Add(ToCanvas(b.MinX, b.MinY, ident));
            dst.Add(ToCanvas(b.MaxX, b.MinY, ident));
            dst.Add(ToCanvas(b.MaxX, b.MaxY, ident));
            dst.Add(ToCanvas(b.MinX, b.MaxY, ident));
            return;
        }

        if (Kind != StrokeKind.Freehand)
        {
            foreach (var p in ShapeOutline()) dst.Add(ident ? p : Vector2.Transform(p, Transform));
            return;
        }

        // 自由笔迹：按参数跳擦除区间。参数 = 点序号（见 Stroke.Erased）。
        for (int i = 0; i < Points.Count; i++)
        {
            if (Erased.Count > 0 && IsParamErased(i)) continue;
            var q = Points[i];
            dst.Add(ToCanvas(q.X, q.Y, ident));
        }
    }

    private Vector2 ToCanvas(float x, float y, bool ident)
        => ident ? new Vector2(x, y) : Vector2.Transform(new Vector2(x, y), Transform);

    /// <summary>
    /// 圆 / 椭圆的折线近似段数：**按尺寸定**（每段弦长 ≈ 16 像素），夹在 [24, 128]。
    ///
    /// 固定 32 段对大圆不够圆（半径 400 时一段弦长 78 像素，肉眼就是折线），对小圆又是浪费。
    /// 这条折线只服务"橡皮 / 框选 / 套索判交"，渲染那边走 D2D 原生几何，不受它影响。
    /// </summary>
    private static int EllipseSegments(float radius)
        => Math.Clamp((int)MathF.Ceiling(radius * MathF.Tau / 16f), 24, 128);

    /// <summary>
    /// 图形的**轮廓折线**（局部坐标，首尾相接，自己闭合）。
    ///
    /// 图形不是"点列"，它的轮廓由**定义元素**推出来（见下面一排 Build*）。
    /// 渲染和命中测试都交给 Direct2D，但**"这一笔和一块矩形有没有碰上"不能用包围盒回答**
    /// ——一个画得很大的圆，它的外框矩形中间是空的，用外框判就会"橡皮从圆心里
    /// 划过，整个圆没了"。所以这里按同一套规则把它展开成折线，逐段判交。
    ///
    /// 规则必须和 Build* 保持一致：改了一边就要改另一边（本函数只服务像素橡皮 /
    /// 套索，所以用折线近似；圆和椭圆的段数按尺寸定，见 <see cref="EllipseSegments"/>，
    /// 误差远小于一个笔宽）。
    /// </summary>
    public List<Vector2> ShapeOutline()
    {
        var list = new List<Vector2>();
        if (Points.Count == 0) return list;
        var (a, b) = Endpoints();
        var pa = new Vector2(a.X, a.Y);
        var pb = new Vector2(b.X, b.Y);

        switch (Kind)
        {
            case StrokeKind.Line:
                list.Add(pa);
                list.Add(pb);
                break;

            case StrokeKind.Rectangle:
                list.Add(new Vector2(pa.X, pa.Y));
                list.Add(new Vector2(pb.X, pa.Y));
                list.Add(new Vector2(pb.X, pb.Y));
                list.Add(new Vector2(pa.X, pb.Y));
                list.Add(new Vector2(pa.X, pa.Y));
                break;

            case StrokeKind.Ellipse:
            case StrokeKind.ConicEllipse:
            {
                // 中心 ＋ 半轴（2026-09-19 改；以前是"外框两个对角点"）
                var c = ShapeCenterLocal;
                float rx = MathF.Max(0.5f, SemiAxisALocal);
                float ry = MathF.Max(0.5f, SemiAxisBLocal);
                int n = EllipseSegments(MathF.Max(rx, ry));
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n * MathF.Tau;
                    list.Add(new Vector2(c.X + MathF.Cos(t) * rx, c.Y + MathF.Sin(t) * ry));
                }

                // **带焦点的那种**（2026-09-22）多出三样，都得列进这份折线：
                // 两个焦点 ＋（有档时）焦点三角形的两条边。理由是这一份折线的用途——
                // 像素橡皮 / 套索判"碰到没有"（见 ShapeTouchesRect）：
                // 漏掉谁的后果就是"这两条边明明画着、rub 过去擦不掉"，
                // 而焦点坐在椭圆**里面**，不从这儿单独列一笔，橡皮根本够不着它。
                if (Kind != StrokeKind.ConicEllipse) break;

                var (f1, f2) = ConicEllipseFociLocal();
                if (FocusTriangle)
                {
                    var p = ConicEllipsePointLocal();
                    list.Add(OutlineBreak);        // 抬笔：F1→P、F2→P 是两笔，不能接成一条来回线
                    list.Add(f1);
                    list.Add(p);
                    list.Add(OutlineBreak);
                    list.Add(f2);
                    list.Add(p);
                }
                // 两个焦点：屏幕上是个**小圆点**，折线这里用一小段近似
                // （只要"橡皮 / 套索够得着"，形状不参与渲染，见 BuildConicEllipse）。
                list.Add(OutlineBreak);
                list.Add(f1);
                list.Add(new Vector2(f1.X + FocusDotRadius, f1.Y));
                list.Add(OutlineBreak);
                list.Add(f2);
                list.Add(new Vector2(f2.X + FocusDotRadius, f2.Y));
                break;
            }

            case StrokeKind.Circle:
            {
                var c = ShapeCenterLocal;
                float r = MathF.Max(0.5f, CircleRadiusLocal);
                int n = EllipseSegments(r);
                for (int i = 0; i <= n; i++)
                {
                    float t = i / (float)n * MathF.Tau;
                    list.Add(new Vector2(c.X + MathF.Cos(t) * r, c.Y + MathF.Sin(t) * r));
                }
                break;
            }

            case StrokeKind.Arrow:
            {
                var (_, wingA, wingB) = ArrowHeadPoints();
                list.Add(pa);
                list.Add(pb);
                list.Add(wingA);
                list.Add(pb);
                list.Add(wingB);
                break;
            }

            case StrokeKind.Triangle:
            {
                // 三个顶点直接就是轮廓：上中 → 下左 → 下右 → 回到上中（闭合）。
                if (Points.Count < 3) break;
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                list.Add(new Vector2(Points[1].X, Points[1].Y));
                list.Add(new Vector2(Points[2].X, Points[2].Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Parallelogram:
            {
                // 底左 → 底右 → **推导出来的顶右** → 顶左 → 回到底左。
                if (Points.Count < 3) break;
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                list.Add(new Vector2(Points[1].X, Points[1].Y));
                list.Add(ParallelogramFourthLocal());
                list.Add(new Vector2(Points[2].X, Points[2].Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Star:
            {
                if (Points.Count < 3) break;
                foreach (var p in Points) list.Add(new Vector2(p.X, p.Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Trapezoid:
            {
                if (Points.Count < 4) break;
                for (int i = 0; i < 4; i++)
                    list.Add(new Vector2(Points[i].X, Points[i].Y));
                list.Add(new Vector2(Points[0].X, Points[0].Y));
                break;
            }

            case StrokeKind.Sector:
            {
                list.AddRange(SectorOutlinePoints());
                break;
            }

            // ---- 空间坐标系：三根从原点出发的轴（抬笔分段，避免连出假线）----
            case StrokeKind.SpaceCoordinate:
            {
                if (Points.Count < 4) break;
                var o = new Vector2(Points[0].X, Points[0].Y);
                list.Add(o); list.Add(new Vector2(Points[1].X, Points[1].Y));
                list.Add(OutlineBreak);
                list.Add(o); list.Add(new Vector2(Points[2].X, Points[2].Y));
                list.Add(OutlineBreak);
                list.Add(o); list.Add(new Vector2(Points[3].X, Points[3].Y));
                break;
            }

            case StrokeKind.Coordinate:
            case StrokeKind.NumberLine:
            {
                // 坐标系 / 数轴：折线这里只列**轴线**（坐标系两条、数轴一条）。
                // 网格刻意不列：这个折线只服务像素橡皮与套索的"碰到没有"，
                // "碰到一根网格线"本来就不该算碰到这个坐标系——
                // 真正决定用户能不能点中它的是 HitTestExact（那条走的是完整几何）。
                if (Points.Count < 2) break;
                var (minX, minY, maxX, maxY) = AxisFrameLocal();
                var o = AxisOriginLocal();
                if (Kind == StrokeKind.NumberLine)
                {
                    list.Add(new Vector2(minX, Points[0].Y));
                    list.Add(new Vector2(maxX, Points[0].Y));
                    break;
                }
                list.Add(new Vector2(minX, o.Y));
                list.Add(new Vector2(maxX, o.Y));
                list.Add(new Vector2(o.X, minY));
                list.Add(new Vector2(o.X, maxY));
                break;
            }

            case StrokeKind.Parabola:
            {
                // 抛物线：一条开折线，t 从 −Span 走到 +Span（**和 BuildParabola 同一份算式**），
                // Span 由"经过点"定（画到老师拖到的那个点为止，见 ParabolaSpanOf）。
                if (Points.Count < 2) break;
                float span = ParabolaSpanLocal();
                var box = CurveBoxLocal();
                int n = CurveSegments(MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY));
                for (int i = 0; i <= n; i++)
                    list.Add(ParabolaPointAt(-span + 2f * span * i / n));
                break;
            }

            case StrokeKind.Hyperbola:
            {
                // 双曲线：**两支**。这里有一个必须处理的坑——
                // 这份折线是被"逐段判交 / 逐点判圈"消费的（见 ShapeTouchesRect、
                // AppendRepresentativePoints），**两支之间直接接过去就会凭空多出一条
                // 横穿整个包围盒的长线段**（它正好过中心）：橡皮从曲线中间那段空白
                // 划一下，整条双曲线就被删掉了——正是当年"橡皮从圆心里划过、整个圆没了"
                // 那个 bug 的翻版。
                // 破法：两支之间放一个**抬笔标记**（见 <see cref="OutlineBreak"/>），
                // 消费那三处各自跳过它。
                if (Points.Count < 2) break;
                var o = CurvePointLocal(0);
                // 名字带 h 前缀：本方法开头已经有 `var (a, b) = Endpoints()`（直线的两个端点）。
                // **用曲线的半轴**（不是渐近线框的 A / B —— 那个只画虚线用）。
                float ha = HyperbolaCurveALocal(), hb = HyperbolaCurveBLocal();
                float hT = HyperbolaTMaxLocal();
                var hbox = CurveBoxLocal();
                int hn = CurveSegments(MathF.Max(hbox.MaxX - hbox.MinX, hbox.MaxY - hbox.MinY));
                for (int branch = 0; branch < 2; branch++)
                {
                    if (branch > 0) list.Add(OutlineBreak);       // 抬笔：两支之间不连线
                    for (int i = 0; i <= hn; i++)
                        list.Add(HyperbolaPoint(o, ha, hb, EffectiveAxis, branch,
                                                -hT + 2f * hT * i / hn));
                }
                break;
            }

            case StrokeKind.Cylinder:
            case StrokeKind.Cone:
            case StrokeKind.ConeFrustum:
            case StrokeKind.Sphere:
            {
                // 立体图形：轮廓 = **底面一圈 ＋（圆柱 / 圆台）顶面一圈 /（圆锥）两条母线**
                // ／（球）**赤道那一圈**。拿"外接矩形"四边当轮廓不行：椭圆弧与矩形之间
                // 那块是空的，橡皮从那儿划过会把整个立体删掉（和双曲线"两支之间不能连线"同类）。
                // 两段之间放**抬笔标记**，免得连出一条横穿包围盒的假线。
                if (Points.Count < 2) break;
                if (Kind == StrokeKind.Sphere)
                {
                    // 球的轮廓就是"轮廓整圆 ＋ 赤道整圈"两段（赤道的上下两半**都要列进来**：
                    // 上半虽然画成虚线，但橡皮得够得着它——轮廓只要求"覆盖到画出来的每一笔"）。
                    var (sc, sr) = SphereLocal();
                    var (_, eqRx, eqRy) = SphereEquatorLocal();
                    int sn = Math.Clamp((int)(sr / 4f), 12, 48);
                    for (int i = 0; i <= sn; i++)
                        list.Add(EllipseArcPoint(sc, sr, sr, i / (float)sn));
                    list.Add(OutlineBreak);
                    for (int i = 0; i <= sn; i++)
                        list.Add(EllipseArcPoint(sc, eqRx, eqRy, i / (float)sn));
                    break;
                }
                var (cx, topCy, botCy, rx, ry) = SolidEllipsesLocal();
                int n = Math.Clamp((int)(MathF.Max(rx, ry) / 4f), 12, 48);
                for (int i = 0; i <= n; i++)
                    list.Add(EllipseArcPoint(new Vector2(cx, botCy), rx, ry, i / (float)n));
                list.Add(OutlineBreak);
                if (Kind == StrokeKind.Cylinder)
                {
                    for (int i = 0; i <= n; i++)
                        list.Add(EllipseArcPoint(new Vector2(cx, topCy), rx, ry, i / (float)n));
                    // **两条母线也要列进来**（2026-09-20 补）：橡皮的粗筛就是按这份轮廓判
                    // "够不够得着"，少了它们，橡皮落在这两条母线上会"够不着这个圆柱"——
                    // 明明画着线却擦不掉（圆锥那两条一直都在，圆柱这两条是漏的）。
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx - rx, topCy));
                    list.Add(new Vector2(cx - rx, botCy));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx + rx, topCy));
                    list.Add(new Vector2(cx + rx, botCy));
                }
                else if (Kind == StrokeKind.ConeFrustum)
                {
                    // 圆台：上底那一圈换成**小一圈**的椭圆，两条母线跟着**收进去**
                    //（母线一样要列——理由同上，少了它们橡皮够不着这两条线）。
                    var (tc, trx, try_) = ConeFrustumTopLocal();
                    for (int i = 0; i <= n; i++)
                        list.Add(EllipseArcPoint(tc, trx, try_, i / (float)n));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx - rx, botCy));
                    list.Add(new Vector2(tc.X - trx, tc.Y));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx + rx, botCy));
                    list.Add(new Vector2(tc.X + trx, tc.Y));
                }
                else
                {
                    var apex = ConeApexLocal();
                    list.Add(new Vector2(cx - rx, botCy));
                    list.Add(apex);
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(cx + rx, botCy));
                    list.Add(apex);
                }
                break;
            }

            case StrokeKind.Cuboid:
            case StrokeKind.Tetrahedron:
            case StrokeKind.Prism:
            case StrokeKind.Pyramid:
            case StrokeKind.Frustum:
            {
                // 立体多面体：轮廓 = 它的各条棱（**分段**列，不许连出假线——
                // 拿"外接矩形"当轮廓会让橡皮擦到空白处就把整个立体删掉）。
                if (Points.Count < 3) break;
                if (IsPrismFamily(Kind))
                {
                    // 棱柱一族：底面一圈 ＋（棱锥没有）顶面一圈 ＋ n 条侧棱，**每段之间抬笔**。
                    // 和长方体那支同一个口径：轮廓只要求"覆盖到画出来的每一笔"，
                    // 不分实 / 虚（实虚是渲染与熔墨的事，见 PrismFamilyEdges）。
                    var bp = PrismBaseLocal();
                    var tp = PrismTopLocal();
                    int np = bp.Length;
                    for (int k = 0; k <= np; k++) list.Add(bp[k % np]);   // 底面闭合一圈
                    list.Add(OutlineBreak);
                    if (Kind != StrokeKind.Pyramid)                       // 棱锥的顶面只是一个点
                    {
                        for (int k = 0; k <= np; k++) list.Add(tp[k % np]);   // 顶面闭合一圈
                    }
                    for (int k = 0; k < np; k++)
                    {
                        list.Add(OutlineBreak);
                        list.Add(bp[k]);
                        list.Add(tp[k]);
                    }
                    list.Add(OutlineBreak);
                    break;
                }
                if (Kind == StrokeKind.Cuboid)
                {
                    var (x0, y0, x1, y1) = CuboidFrontLocal();
                    float d = CuboidDepthLocal();
                    // 正面一圈 + 背面一圈（各自闭合，两圈之间抬笔）
                    list.Add(new Vector2(x0, y0));
                    list.Add(new Vector2(x1, y0));
                    list.Add(new Vector2(x1, y1));
                    list.Add(new Vector2(x0, y1));
                    list.Add(new Vector2(x0, y0));
                    list.Add(OutlineBreak);
                    list.Add(new Vector2(x0 + d, y0 - d));
                    list.Add(new Vector2(x1 + d, y0 - d));
                    list.Add(new Vector2(x1 + d, y1 - d));
                    list.Add(new Vector2(x0 + d, y1 - d));
                    list.Add(new Vector2(x0 + d, y0 - d));
                    list.Add(OutlineBreak);
                    // 四条斜棱（每条一段，互相之间抬笔）
                    var corners = new[]
                    {
                        (new Vector2(x0, y0), new Vector2(x0 + d, y0 - d)),
                        (new Vector2(x1, y0), new Vector2(x1 + d, y0 - d)),
                        (new Vector2(x0, y1), new Vector2(x0 + d, y1 - d)),
                        (new Vector2(x1, y1), new Vector2(x1 + d, y1 - d)),
                    };
                    foreach (var (m, n) in corners)
                    {
                        list.Add(m);
                        list.Add(n);
                        list.Add(OutlineBreak);
                    }
                    list.RemoveAt(list.Count - 1);      // 末尾那个抬笔没人接，去掉（免得消费者读到孤零零的 NaN）
                }
                else
                {
                    var p0 = CurvePointLocal(0);
                    var p1 = CurvePointLocal(1);
                    var p2 = CurvePointLocal(2);
                    var apex = TetraApexLocal();
                    var edges = new[]
                    {
                        (p0, p1), (p1, p2), (p2, p0),      // 底面三条边
                        (p0, apex), (p1, apex), (p2, apex) // 三条棱
                    };
                    foreach (var (m, n) in edges)
                    {
                        list.Add(m);
                        list.Add(n);
                        list.Add(OutlineBreak);
                    }
                    list.RemoveAt(list.Count - 1);      // 同上：去掉末尾那个抬笔
                }
                break;
            }

            case StrokeKind.Sine:
            case StrokeKind.Cosine:
            case StrokeKind.Wave:
            {
                // 正弦 / 余弦 / **波浪线**：一条开折线，t 从 0 走到 1
                //（**和 BuildWave 同一份算式**）。
                // 注意 `WaveTracedPointAt` 而不是 `WavePointAt`：前者按"整条曲线"的比例走、
                // 对三种都对；后者按"周期数"走（正弦恒等于前者，波浪线可能是 2.7 个周期，
                // 见 WaveCyclesLocal）。
                if (Points.Count < 2) break;
                int n = WaveSegmentsLocal();
                for (int i = 0; i <= n; i++) list.Add(WaveTracedPointAt(i / (float)n));
                break;
            }

            case StrokeKind.Tangent:
            {
                // 正切：一条开折线，t 从 0 走到 1（左下的截断点 → 右上的截断点；
                // **和 BuildTangent 同一份算式**，所以橡皮的轮廓和画出来的一模一样）。
                if (Points.Count < 2) break;
                int nt = TangentSegmentsLocal();
                for (int i = 0; i <= nt; i++) list.Add(TangentTracedPointAt(i / (float)nt));
                break;
            }

            default:
                // 图像对象：轮廓就是它的矩形（但橡皮不碰图像，见 EraseRectAt）。
                list.Add(new Vector2(pa.X, pa.Y));
                list.Add(new Vector2(pb.X, pa.Y));
                list.Add(new Vector2(pb.X, pb.Y));
                list.Add(new Vector2(pa.X, pb.Y));
                list.Add(new Vector2(pa.X, pa.Y));
                break;
        }
        return list;
    }

    /// <summary>
    /// 这一条对象的**辅助几何**（目前只有双曲线的两条虚线渐近线，见
    /// <see cref="HyperbolaAsymptoteLocal"/>）。
    ///
    /// 为什么要单独一个槽：主几何的线型是这个对象自己的（老师可能已经把曲线
    /// 设成虚线了），而渐近线**恒定是细虚线**——"一个对象两种线"落在这里。
    ///
    /// 它**不参与命中**（和坐标系网格同一条口径：辅助线点不中），也在曲线紧框之内。
    /// 缓存规则和主几何逐字一样（按 <see cref="Revision"/>）。
    /// </summary>
    public ID2D1Geometry BuildAuxGeometry(ID2D1Factory1 factory)
    {
        if (Geometry2 != null && _builtRevision2 == Revision) return Geometry2;
        if (Geometry2 != null) { Geometry2.Dispose(); Geometry2 = null; }
        if (Points.Count < 2)
        {
            _builtRevision2 = Revision;
            return null;
        }

        // **坐标系的网格**（2026-09-24，用户："把那个网格的线变细变淡一些"）：
        // 它走这个槽，是因为"一个对象、两种线"这件事这个槽已经在装了（原来是渐近线）。
        // 描边风格在 Overlay.DrawStroke 里按"是不是网格"分：网格 **细一半、淡一半、实线**，
        // 辅助线照旧 0.6 倍、虚线。坐标系没有网格时这个槽是空的（返回 null）。
        if (Kind == StrokeKind.Coordinate && Grid)
        {
            Geometry2 = BuildAxes(factory, gridOnly: true);
            _builtRevision2 = Revision;
            return Geometry2;
        }

        // **立体图形被挡住的那几笔**（圆柱 / 圆锥 / 圆台的下底上半圈、**球的赤道远侧半圈**、
        // 长方体被挡的三条棱）：恒定细虚线。
        if (Kind is StrokeKind.Cylinder or StrokeKind.Cone or StrokeKind.ConeFrustum
            or StrokeKind.Sphere)
        {
            Geometry2 = BuildSolidHidden(factory);
            _builtRevision2 = Revision;
            return Geometry2;
        }
        if (Kind == StrokeKind.Cuboid)
        {
            Geometry2 = BuildCuboid(factory, hidden: true);
            _builtRevision2 = Revision;
            return Geometry2;
        }
        // **棱柱 / 棱锥 / 棱台**被挡住的那些棱（细虚线）——三兄弟共用（见 PrismFamilyEdges）。
        if (IsPrismFamily(Kind))
        {
            Geometry2 = BuildPrism(factory, hidden: true);
            _builtRevision2 = Revision;
            return Geometry2;
        }
        // **正切的两条竖直渐近线**（x = ±π/2）——恒定细虚线，和双曲线那两条同一种画法。
        // 它**没有开关**：那两条线是"这一支画到哪儿为止"的边界，不是可选的辅助线。
        if (Kind == StrokeKind.Tangent)
        {
            Geometry2 = BuildTangentAsymptotes(factory);
            _builtRevision2 = Revision;
            return Geometry2;
        }

        if (Kind != StrokeKind.Hyperbola || !ShowAsymptotes)
        {
            _builtRevision2 = Revision;
            return null;
        }

        var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            for (int side = -1; side <= 1; side += 2)
            {
                var (from, to) = HyperbolaAsymptoteLocal(side);
                sink.BeginFigure(from, FigureBegin.Hollow);
                sink.AddLine(to);
                sink.EndFigure(FigureEnd.Open);
            }
            sink.Close();
        }
        Geometry2 = geo;
        _builtRevision2 = Revision;
        return Geometry2;
    }

    /// <summary>
    /// **正切**几何：一支（`t` 从 0 走到 1 ＝ 左下的截断点 → 右上的截断点）。
    /// 形状由"横竖同一个单位"钉死，所以它**不会被拖变形**（见 TangentUnitLocal）。
    /// </summary>
    private ID2D1PathGeometry BuildTangent(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        int n = TangentSegmentsLocal();

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(TangentTracedPointAt(0f), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
            sink.AddLine(TangentTracedPointAt(i / (float)n));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>正切的**两条渐近线**（细虚线）——走辅助几何槽，和双曲线那两条同一种画法。</summary>
    private ID2D1PathGeometry BuildTangentAsymptotes(ID2D1Factory1 factory)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        for (int side = -1; side <= 1; side += 2)
        {
            var (from, to) = TangentAsymptoteLocal(side);
            sink.BeginFigure(from, FigureBegin.Hollow);
            sink.AddLine(to);
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    public ID2D1Geometry BuildGeometry(ID2D1Factory1 factory)
    {
        // AI 题卡由 Overlay 用 DirectWrite 画，没有中心线几何。
        if (Kind == StrokeKind.TextBox) return null;

        // 缓存键 = 几何版本（Revision）＋ 曲线化版本 ＋ 活笔/模型版本；
        // 只比 Revision 的话，开关一拨会把旧几何还回来。
        if (Geometry != null && _builtRevision == Revision
            && _builtSmoothVer == StrokeSmoothing.Version && _builtRawLive == RawWhileLive
            && _builtInkModelVer == StrokeMotion.Version)
            return Geometry;
        if (Points.Count == 0) return null;

        if (Geometry != null) { Geometry.Dispose(); Geometry = null; LiveGeometries--; }

        Geometry = Kind switch
        {
            StrokeKind.Rectangle => BuildRectangle(factory),
            StrokeKind.Ellipse => BuildEllipse(factory),
            // 椭圆（带焦点）：椭圆 ＋ 两焦点 ＋（有档时）焦点三角形，见 BuildConicEllipse
            StrokeKind.ConicEllipse => BuildConicEllipse(factory),
            StrokeKind.Circle => BuildCircle(factory),
            StrokeKind.Arrow => BuildArrow(factory),
            StrokeKind.Line => BuildLine(factory),
            StrokeKind.Triangle => BuildPolygon(factory),
            StrokeKind.Parallelogram => BuildPolygon(factory),
            StrokeKind.Star => BuildStar(factory),
            StrokeKind.Trapezoid => BuildTrapezoid(factory),
            StrokeKind.Sector => BuildSector(factory),
            StrokeKind.Coordinate => BuildAxes(factory),
            StrokeKind.NumberLine => BuildAxes(factory),
            StrokeKind.SpaceCoordinate => BuildAxes(factory),
            StrokeKind.Parabola => BuildParabola(factory),
            StrokeKind.Hyperbola => BuildHyperbola(factory),
            StrokeKind.Sine => BuildWave(factory),
            StrokeKind.Cosine => BuildWave(factory),
            StrokeKind.Wave => BuildWave(factory),
            StrokeKind.Tangent => BuildTangent(factory),
            StrokeKind.Cylinder => BuildSolid(factory),
            StrokeKind.Cone => BuildSolid(factory),
            StrokeKind.ConeFrustum => BuildSolid(factory),
            StrokeKind.Sphere => BuildSolid(factory),
            StrokeKind.Cuboid => BuildCuboid(factory, hidden: false),
            StrokeKind.Tetrahedron => BuildTetra(factory),
            StrokeKind.Prism => BuildPrism(factory, hidden: false),
            StrokeKind.Pyramid => BuildPrism(factory, hidden: false),
            StrokeKind.Frustum => BuildPrism(factory, hidden: false),
            StrokeKind.Image => BuildImageRect(factory),
            // 自由笔迹：只给**中心线**，描边（宽度、端帽、拐角）交给 D2D。
            // 单点与"微移笔画"例外——那是一个圆点，几何直接建成圆（渲染那边会填充它）。
            _ => RendersAsDot ? BuildDot(factory) : BuildCenterline(factory),
        };
        if (Geometry != null) LiveGeometries++;
        _builtRevision = Revision;
        _builtSmoothVer = StrokeSmoothing.Version;
        _builtRawLive = RawWhileLive;
        _builtInkModelVer = StrokeMotion.Version;
        return Geometry;
    }

    private (InkPoint a, InkPoint b) Endpoints()
        => (Points[0], Points.Count > 1 ? Points[^1] : Points[0]);

    private ID2D1PathGeometry BuildLine(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    private ID2D1PathGeometry BuildRectangle(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, a.Y));
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.AddLine(new Vector2(a.X, b.Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 多边形几何（**闭合折线**）：三角形 / 平行四边形共用这一份。
    ///
    /// 和矩形同一个写法（`Hollow` 起点 ＋ `Closed` 收尾），于是命中判定天然就是
    /// **只认描边、不认内部**——这正是规格要的（和矩形/椭圆一致，见 HitTestExact）。
    /// 平行四边形的第四个顶点**现推**（<see cref="ParallelogramFourthLocal"/>），
    /// 所以"永远是平行四边形"是几何本身的性质，不是拖动时校正出来的。
    /// </summary>
    private ID2D1PathGeometry BuildPolygon(ID2D1Factory1 factory)
    {
        // 控制点不足三个：不该发生（画法与存档都保证三个），真发生了也别抛异常，
        // 退回一条线就行——一条画错位置的线，比整个渲染循环崩掉好得多。
        if (Points.Count < 3) return BuildLine(factory);

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(Points[0].X, Points[0].Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(Points[1].X, Points[1].Y));
        if (Kind == StrokeKind.Parallelogram) sink.AddLine(ParallelogramFourthLocal());
        sink.AddLine(new Vector2(Points[2].X, Points[2].Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private ID2D1PathGeometry BuildStar(ID2D1Factory1 factory)
    {
        if (Points.Count < 3) return BuildLine(factory);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(Points[0].X, Points[0].Y), FigureBegin.Hollow);
        for (int i = 1; i < Points.Count; i++)
            sink.AddLine(new Vector2(Points[i].X, Points[i].Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private ID2D1PathGeometry BuildTrapezoid(ID2D1Factory1 factory)
    {
        if (Points.Count < 4) return BuildLine(factory);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(Points[0].X, Points[0].Y), FigureBegin.Hollow);
        for (int i = 1; i < 4; i++)
            sink.AddLine(new Vector2(Points[i].X, Points[i].Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    private ID2D1PathGeometry BuildSector(ID2D1Factory1 factory)
    {
        var pts = SectorOutlinePoints();
        if (pts.Count < 2) return BuildLine(factory);
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(pts[0], FigureBegin.Hollow);
        for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 坐标系 / 数轴的几何：**轴线 ＋ 箭头**（坐标系还可能有那个可选网格）。
    ///
    /// **没有刻度线**（用户 2026-09-19 定："数轴和坐标系上面的刻度太多了……不需要刻度"）。
    /// 刻度曾经在这里画过一版，撤掉之后两个种类都清爽了：
    ///   · 坐标系 = 十字轴 + 两个箭头；开网格时再多一组贯穿外框的格线；
    ///   · 数轴 = 一条水平线 + 一个右箭头。
    /// 代价是"单位长度点"和数轴的"零点"这两个定义元素随之退场（见 SetAxisBox）。
    ///
    /// 一条 path 里放**多个 figure**：描边、命中、虚线都按整条处理，正是我们要的
    /// ——虚线时每一段（每条轴、每根网格线）各自起落，不会跨过拐角连成一长条。
    ///
    /// 箭头画成**一个 V（三段折线）**，不填充三角形：和"箭头工具"同一个做法
    /// （见 <see cref="ArrowHeadPoints"/>），任何笔宽下都匀称；填充的话细笔会糊成一个点。
    /// 头长按**笔宽**算而不是按轴长算——轴可以拉得很长，头跟着长就成了怪东西。
    /// </summary>
    private ID2D1PathGeometry BuildAxes(ID2D1Factory1 factory, bool gridOnly = false)
    {
        // 控制点不足两个：不该发生（画法与存档都保证），真发生了退回一条线，
        // 别让整个渲染循环崩掉（和 BuildPolygon 那条护栏同一个理由）。
        if (Points.Count < 2) return gridOnly ? null : BuildLine(factory);

        // **轴线与网格分成两段几何**：一个几何只能有一种描边，而网格要细一半、淡一半
        //（用户 2026-09-24）。这里按 Grid 位分拣，别的地方都当"一个对象"看它。
        var pieces = new List<InkPiece>();
        foreach (var p in AxisPieces())
            if (p.Grid == gridOnly) pieces.Add(p);
        return pieces.Count == 0 ? null : FromPieces(factory, pieces);
    }

    /// <summary>
    /// 坐标系 / 数轴的**逐笔点列**：轴线 ＋ 箭头（坐标系还有网格）。
    ///
    /// **渲染与熔墨共用这一份**（<see cref="BuildAxes"/> 与 <see cref="InkPieces"/>）：
    /// 以前轴线、箭头、网格分别写在三个地方，熔墨只认得轴线那一份，
    /// 于是"擦一下坐标系，网格和箭头就没了"（用户 2026-09-20 反馈的那类变形）。
    /// </summary>
    private List<InkPiece> AxisPieces()
    {
        var list = new List<InkPiece>();
        float head = PreviewArrowHeadLen > 0.5f
            ? PreviewArrowHeadLen : AxisArrowHeadLen(Width);

        // ---- 空间直角坐标系：原点 → 三个正半轴端点，各带一个箭头 ----
        if (Kind == StrokeKind.SpaceCoordinate)
        {
            if (Points.Count < 4) return list;
            var so = new Vector2(Points[0].X, Points[0].Y);
            void Axis(Vector2 end, Vector2 dir)
            {
                list.Add(new InkPiece(new List<Vector2> { so, end }, false));
                list.Add(new InkPiece(AxisArrowPoints(end, dir, head), false));
            }
            var xEnd = new Vector2(Points[1].X, Points[1].Y);
            var xDir = xEnd - so;
            Axis(xEnd, xDir.LengthSquared() > 1e-6f
                ? Vector2.Normalize(xDir) : new Vector2(1f, 0f));
            var yEnd = new Vector2(Points[2].X, Points[2].Y);
            var yDir = yEnd - so;
            Axis(yEnd, yDir.LengthSquared() > 1e-6f
                ? Vector2.Normalize(yDir) : SpaceAxisYDirection);
            Axis(new Vector2(Points[3].X, Points[3].Y), new Vector2(0f, -1f));
            return list;
        }

        var (minX, minY, maxX, maxY) = AxisFrameLocal();

        // ---- 数轴：一条水平线 + 右端一个箭头 ----
        if (Kind == StrokeKind.NumberLine)
        {
            float ly = Points[0].Y;                    // 两个端点 y 恒相等（见 SetAxisBox）
            list.Add(new InkPiece(new List<Vector2> { new(minX, ly), new(maxX, ly) }, false));
            list.Add(new InkPiece(AxisArrowPoints(new Vector2(maxX, ly), new Vector2(1f, 0f), head), false));
            return list;
        }

        // ---- 坐标系：十字轴 + 两个箭头 ----
        var o = AxisOriginLocal();
        list.Add(new InkPiece(new List<Vector2> { new(minX, o.Y), new(maxX, o.Y) }, false));
        // 屏幕坐标 y 向下，所以"向上"是往 minY 那头走。
        list.Add(new InkPiece(new List<Vector2> { new(o.X, maxY), new(o.X, minY) }, false));
        list.Add(new InkPiece(AxisArrowPoints(new Vector2(maxX, o.Y), new Vector2(1f, 0f), head), false));
        list.Add(new InkPiece(AxisArrowPoints(new Vector2(o.X, minY), new Vector2(0f, -1f), head), false));

        if (Grid)
        {
            // 网格线单独一段身份（见 InkPiece.Grid）：**细一半、淡一半的实线**，
            // 而且不参与命中（ShapeOutline 里只列两条轴线）。
            foreach (var (a, b) in AxisGridSegments(minX, minY, maxX, maxY, o))
                list.Add(new InkPiece(new List<Vector2> { a, b }, false, grid: true));
        }
        return list;
    }

    /// <summary>
    /// 坐标系**可选网格**的每一条线（起终点）。规则只有这一份：
    /// <see cref="BuildAxes"/> 与熔墨都从这里取，别各写一遍。
    ///
    /// 间距 = <see cref="AxisGridStepLocal"/>（外框短边 ÷ 4，**现算不存**）。
    /// 上限定 512 条 / 方向：外框可以被拉到上万像素，不设上限的话一条病态的对象
    /// 能造出几万个 figure，一帧就把渲染拖死（和刻度时代那条上限同一个理由）。
    /// 注意网格线**不画在原点那一格上**——那两条正是轴本身，重画一遍只会加深一遍颜色。
    /// </summary>
    private List<(Vector2 a, Vector2 b)> AxisGridSegments(float minX, float minY, float maxX, float maxY,
                                                          Vector2 origin)
    {
        const int MaxLines = 512;
        var list = new List<(Vector2, Vector2)>();
        float step = AxisGridStepLocal();

        for (int k = 1; k <= MaxLines; k++)
        {
            float xr = origin.X + k * step, xl = origin.X - k * step;
            if (xr > maxX + 0.01f && xl < minX - 0.01f) break;
            if (xr <= maxX + 0.01f) list.Add((new Vector2(xr, minY), new Vector2(xr, maxY)));
            if (xl >= minX - 0.01f) list.Add((new Vector2(xl, minY), new Vector2(xl, maxY)));
        }
        for (int k = 1; k <= MaxLines; k++)
        {
            float yd = origin.Y + k * step, yu = origin.Y - k * step;
            if (yd > maxY + 0.01f && yu < minY - 0.01f) break;
            if (yd <= maxY + 0.01f) list.Add((new Vector2(minX, yd), new Vector2(maxX, yd)));
            if (yu >= minY - 0.01f) list.Add((new Vector2(minX, yu), new Vector2(maxX, yu)));
        }
        return list;
    }

    /// <summary>
    /// 一个箭头（坐标轴 / 数轴末端那种）：从尖端往回画一个 **V**
    ///（<c>翅膀根 → 尖端 → 另一侧翅膀根</c>）。
    ///
    /// 比例照抄 <see cref="ArrowHeadPoints"/>（翅膀尖在尖端后方 `head` 处、左右各张 `0.45 × head`），
    /// 这样坐标系上的箭头和"箭头工具"画出来的那支看着是一家人。
    /// </summary>
    private static List<Vector2> AxisArrowPoints(Vector2 tip, Vector2 dir, float head)
    {
        var root = tip - dir * head;
        float spread = head * 0.45f;
        var n = new Vector2(-dir.Y, dir.X);               // 垂直于箭头方向
        return new List<Vector2> { root + n * spread, tip, root - n * spread };
    }

    /// <summary>
    /// **抛物线**几何：把 <see cref="ParabolaPointAt"/> 采成一串折线点（**开折线**，不闭合）。
    ///
    /// 控制点不足两个时退回一条线：不该发生（画法与存档都保证两个），真发生了也别抛异常
    /// ——一条画错位置的线，比整个渲染循环崩掉好得多（和 BuildPolygon 那条护栏同一个理由）。
    /// </summary>
    private ID2D1PathGeometry BuildParabola(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        var box = CurveBoxLocal();
        int n = CurveSegments(MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY));

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        // t 从 −Span 走到 +Span：一个端点 → 顶点 → 另一个端点
        //（Span 见 ParabolaSpanOf：**画到"经过点"那儿为止**，和轮廓折线用同一个值）。
        float span = ParabolaSpanLocal();
        sink.BeginFigure(ParabolaPointAt(-span), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
            sink.AddLine(ParabolaPointAt(-span + 2f * span * i / n));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// **双曲线**几何：两支各一个 figure（`t` 从 `−T` 走到 `+T`）。
    ///
    /// 两支放**同一条 path**里，于是描边、虚线、命中、包围盒都按"一条对象"处理
    /// （和坐标系的多 figure 一个做法，见 <see cref="BuildAxes"/>）——
    /// 如果拆成两个对象，"选中/拖动/撤销/存档"全都要多一层"组"的概念。
    /// </summary>
    private ID2D1PathGeometry BuildHyperbola(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        var o = CurvePointLocal(0);
        // **曲线的半轴**（不是渐近线框的 A / B —— 那两个只用来画虚线渐近线，见 BuildAuxGeometry）。
        float a = HyperbolaCurveALocal(), b = HyperbolaCurveBLocal();
        var box = CurveBoxLocal();
        int n = CurveSegments(MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY));
        float tMax = HyperbolaTMaxLocal();

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        for (int branch = 0; branch < 2; branch++)
        {
            sink.BeginFigure(HyperbolaPoint(o, a, b, EffectiveAxis, branch, -tMax), FigureBegin.Hollow);
            for (int i = 1; i <= n; i++)
                sink.AddLine(HyperbolaPoint(o, a, b, EffectiveAxis, branch, -tMax + 2f * tMax * i / n));
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    /// <summary>
    /// **正弦 / 余弦 / 波浪线**几何：`t` 从 0 走到 1 —— 也就是**从起点画到终点**
    ///（正弦 / 余弦：框宽 = 一个周期；波浪线：框宽 = 画多长、周期由振幅定，
    /// 见 <see cref="WaveCyclesLocal"/>；三个种类共用这一份算式，
    /// 差别只在 <see cref="WaveYAt"/> 里那一行 sin / cos）。
    ///
    /// 两端不闭合、也没有拐回头的部分（它是一条"图象"，不是封闭图形）。
    /// </summary>
    private ID2D1PathGeometry BuildWave(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);

        int n = WaveSegmentsLocal();

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(WaveTracedPointAt(0f), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
            sink.AddLine(WaveTracedPointAt(i / (float)n));
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 椭圆几何（**中心 ＋ 两条半轴**）。
    ///
    /// 2026-09-19 改：以前是"两个对角点"定外框（`BuildEllipse` 从两点算中心与半轴）。
    /// 现在 `Points[0]` 就是中心、`Points[1]` 是外角点（`a = |dx|`、`b = |dy|`），
    /// 所以这里直接读两个半轴——**画法和手柄用的是同一个定义**，
    /// 不会出现"画出来是按外框、手柄按半轴"这种两套账。
    /// </summary>
    private ID2D1Geometry BuildEllipse(ID2D1Factory1 factory)
    {
        return factory.CreateEllipseGeometry(
            new Ellipse(ShapeCenterLocal,
                        MathF.Max(0.5f, SemiAxisALocal),
                        MathF.Max(0.5f, SemiAxisBLocal)));
    }

    /// <summary>圆几何（D2D 原生椭圆几何，两个半径相同）。</summary>
    private ID2D1Geometry BuildCircle(ID2D1Factory1 factory)
    {
        float r = MathF.Max(0.5f, CircleRadiusLocal);
        return factory.CreateEllipseGeometry(new Ellipse(ShapeCenterLocal, r, r));
    }

    /// <summary>
    /// **椭圆（带焦点）**的几何（2026-09-22）：椭圆本体 ＋ 两个焦点（小圆点）
    /// ＋（有档时）焦点三角形的两条边 F₁P / F₂P。
    ///
    /// **为什么不用 `ID2D1EllipseGeometry`**：这里要的是"圆 ＋ 两条边 ＋ 两个点"**四段几何的并**，
    /// 只有路径几何能装；而 `ID2D1GeometrySink` 在 Vortice 里没有 `AddEllipse`，
    /// 所以椭圆本体按**折线**画（`EllipseSegments` 份，和 `ShapeOutline` 同一份口径、
    /// 同一个段数函数——两处差一段就是"橡皮擦得掉、D2D 命中差一点"这种说不清的小账）。
    ///
    /// **为什么焦点和三角形走主几何、不走虚线那个辅助槽**（`Geometry2`）：
    /// 辅助槽是"细虚线的辅助线"（渐近线、被挡住的棱）专用的；而焦点三角形是这道题的
    /// **主体**——它得是实线、和椭圆同一个颜色同一个粗细，放辅助槽里就成了一条虚线。
    /// </summary>
    private ID2D1PathGeometry BuildConicEllipse(ID2D1Factory1 factory)
    {
        var c = ShapeCenterLocal;
        float rx = MathF.Max(0.5f, SemiAxisALocal);
        float ry = MathF.Max(0.5f, SemiAxisBLocal);

        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();

        // ① 椭圆本体：一圈折线（首尾相接 → 闭合）
        int n = EllipseSegments(MathF.Max(rx, ry));
        if (PreviewSmooth) n = Math.Max(n, 72);
        sink.BeginFigure(new Vector2(c.X + rx, c.Y), FigureBegin.Hollow);
        for (int i = 1; i <= n; i++)
        {
            float t = i / (float)n * MathF.Tau;
            sink.AddLine(new Vector2(c.X + MathF.Cos(t) * rx, c.Y + MathF.Sin(t) * ry));
        }
        sink.EndFigure(FigureEnd.Closed);

        var (f1, f2) = ConicEllipseFociLocal();

        // ② 焦点三角形的两条边：**一笔画 F₁ → P → F₂**（两条边共用中间的 P，
        //    写成一条折线就是"画出来的那个三角形去掉底边"）。
        if (FocusTriangle)
        {
            var p = ConicEllipsePointLocal();
            sink.BeginFigure(f1, FigureBegin.Hollow);
            sink.AddLine(p);
            sink.AddLine(f2);
            sink.EndFigure(FigureEnd.Open);
        }

        // ③ 两个焦点：小圆点（描边＝这一笔的宽度，见 FocusDotRadius 的注释）。
        //    同样按折线画一圈（8 段足够小到看不出棱角）。
        //    **两档都画**：用户 2026-09-22 定的"无焦点三角形"只是不连那两条边，焦点还在。
        if (!PreviewFocusDots)
        {
            AddDot(sink, f1);
            AddDot(sink, f2);
        }

        sink.Close();
        return geo;

        // 小工具：画一个"小圆点"（8 段折线的小圆）。
        static void AddDot(ID2D1GeometrySink sink, Vector2 at)
        {
            const int seg = 8;
            sink.BeginFigure(new Vector2(at.X + FocusDotRadius, at.Y), FigureBegin.Hollow);
            for (int i = 1; i <= seg; i++)
            {
                float t = i / (float)seg * MathF.Tau;
                sink.AddLine(new Vector2(at.X + MathF.Cos(t) * FocusDotRadius,
                                         at.Y + MathF.Sin(t) * FocusDotRadius));
            }
            sink.EndFigure(FigureEnd.Closed);
        }
    }

    // =====================================================================
    //  立体图形（2026-09-20 第五批：照 InkClass 的 case 6/7 搬过来）
    //
    //  两个控制点 = 画出来的东西的**外接矩形**（拖到哪就是哪），
    //  椭圆都由它派生：`rx = 宽/2`、`ry = rx / 2.646`（扁率照抄他的常数）。
    //  这样"控制点的外接"**正好就是**真实范围（模型里默认那条包围盒算法就够用），
    //  不用像曲线那样再单独算一份。
    // =====================================================================

    /// <summary>立体图形里椭圆的扁率（短轴 / 长轴）：照 InkClass 的 `2.646`。
    /// **只有一份**在 <see cref="ShapeSpec.SolidEllipseRatio"/>（图标层看不到 `Stroke`，
    /// 而图标上那个椭圆得和画布同一个俯角）——这里只是转发，老代码照旧读这个名。</summary>
    public const float SolidEllipseRatio = ShapeSpec.SolidEllipseRatio;

    /// <summary>外接矩形（局部坐标，已归一成 左上 / 右下）。</summary>
    public (float X0, float Y0, float X1, float Y1) SolidRectLocal()
    {
        var a = CurvePointLocal(0);
        var b = Points.Count >= 2 ? CurvePointLocal(1) : a;
        return (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    /// <summary>写外接矩形（画的时候每一帧都调它；归一成 左上 / 右下，和坐标系那套一致）。</summary>
    public void SetSolidBox(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        SetPoint(0, new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)));
        SetPoint(1, new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)));
    }

    /// <summary>
    /// 上下两个**椭圆**的位置与半径（局部坐标）：`(cx, 上圆心 y, 下圆心 y, rx, ry)`。
    ///
    /// 椭圆的圆心**从矩形边往里缩一个 `ry`**，于是椭圆正好与矩形上下边相切
    ///（画出来的东西刚好占满拖出来的那个矩形）。InkClass 是把圆心放在矩形边上的
    ///（椭圆会往外冒半圈），我们往里收一下：范围好算、紧框不会漏，看着完全一样。
    /// </summary>
    public (float Cx, float TopCy, float BottomCy, float Rx, float Ry) SolidEllipsesLocal()
    {
        var (x0, y0, x1, y1) = SolidRectLocal();
        float rx = MathF.Max(0.5f, (x1 - x0) * 0.5f);
        float ry = MathF.Max(0.5f, rx * SolidEllipseRatio);
        float cx = (x0 + x1) * 0.5f;
        return (cx, y0 + ry, y1 - ry, rx, ry);
    }

    /// <summary>
    /// **球**在画面上的那个圆（圆心 ＋ 半径，局部坐标）。
    ///
    /// 球投影下来**永远是圆**，所以这里不像别的立体那样分 rx / ry，而是取外接矩形里
    /// **能内切的那个正圆**：半径 = `min(半宽, 半高)`、圆心 = 矩形中心。
    /// 拖成方的就是标准球；拖长了（比如 400×200）也**不会**画出一个"扁球"，
    /// 而是画一个内切的圆——"球一定是圆的"这件事由算式保证，不靠用户手稳。
    ///
    /// ⚠ 因此它的**真实范围 = 圆心 ± 半径**，不是那个外接矩形（矩形拖长了就有富余）——
    /// 所以 `RecomputeBounds` 与 `InkBounds` 都得有球这一支（照棱柱一族那套）。
    /// </summary>
    public (Vector2 Center, float R) SphereLocal()
    {
        var (x0, y0, x1, y1) = SolidRectLocal();
        float r = MathF.Max(0.5f, MathF.Min(x1 - x0, y1 - y0) * 0.5f);
        return (new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f), r);
    }

    /// <summary>
    /// **球的赤道**那个椭圆（圆心 = 球心、两个半轴）：
    /// 长半轴 = 球的半径（赤道的左右两端正好落在轮廓圆上那两个点），
    /// 短半轴 = 半径 × <see cref="SolidEllipseRatio"/>。
    ///
    /// ⚠ 扁率**和圆柱 / 圆锥 / 圆台用同一个数**：那是"从上往下看"的俯角，
    /// 一个画面里所有圆都该用同一个俯角，不然球看起来比圆柱"躺得更平"。
    /// </summary>
    public (Vector2 Center, float Rx, float Ry) SphereEquatorLocal()
    {
        var (c, r) = SphereLocal();
        return (c, r, MathF.Max(0.5f, r * SolidEllipseRatio));
    }

    /// <summary>圆锥的顶点（局部坐标）= 外接矩形的**上边中点**（照 InkClass）。</summary>
    public Vector2 ConeApexLocal()
    {
        var (x0, y0, x1, _) = SolidRectLocal();
        return new Vector2((x0 + x1) * 0.5f, y0);
    }

    /// <summary>
    /// **圆台的上底**那个椭圆（圆心 ＋ 两个半轴，局部坐标）——它和 <see cref="Cylinder"/> /
    /// <see cref="Cone"/> 唯一的差别就在这一处。
    ///
    /// 上底 = 下底**同形缩一个固定比例**（<see cref="ShapeSpec.FrustumTopScale"/>，
    /// 和「棱台」用的是**同一个数**——两个台体在面板上挨着，看着该是同一个收法）：
    ///   · 两个半轴**都乘那个比例**（圆还是圆，只是小了）；
    ///   · 圆心**从上边往里缩 `ry × 比例`**（不是缩 `ry`）——这样上底椭圆正好与矩形上边
    ///     **相切**，画出来的东西刚好占满拖出来的那个矩形（和圆柱 / 圆锥同一条老规矩：
    ///     范围好算、紧框不会漏、看着完全一样）。
    /// </summary>
    public (Vector2 Center, float Rx, float Ry) ConeFrustumTopLocal()
    {
        var (cx, _, _, rx, ry) = SolidEllipsesLocal();
        var (_, y0, _, _) = SolidRectLocal();
        float k = ShapeSpec.FrustumTopScale;
        return (new Vector2(cx, y0 + ry * k), rx * k, ry * k);
    }

    // ---- 长方体（两笔：正面矩形 → 深度）---------------------------------

    /// <summary>正面矩形（局部坐标，已归一成 左上 / 右下）。</summary>
    public (float X0, float Y0, float X1, float Y1) CuboidFrontLocal()
    {
        var a = CurvePointLocal(0);
        var b = Points.Count >= 2 ? CurvePointLocal(1) : a;
        return (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    /// <summary>长方体的**最小深度**：拖得太浅就不像一个立体了。</summary>
    public const float CuboidMinDepth = 12f;

    /// <summary>**第 1 笔**：正面矩形（归一成 左上 / 右下）。</summary>
    public void SetCuboidFront(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        SetPoint(0, new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)));
        SetPoint(1, new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)));
    }

    /// <summary>
    /// **第 2 笔**：拖出**深度**。口径照 InkClass：`d = |正面矩形上边 − 指针 y|`，
    /// 方向**恒定往后上方 45°**（他原话："就是懒不想做反向的"——我们也先不做）。
    ///
    /// 存进第三个控制点的是**背面右下角** `(右 + d, 下 − d)`，而不是指针原样：
    /// 那个角正好是画出来的东西最外的一个角，于是"控制点外接"就是真实范围
    ///（包围盒、命中粗筛、导出裁切都直接对）。
    /// </summary>
    public void SetCuboidDepth(float x, float y)
    {
        var (_, y0, x1, y1) = CuboidFrontLocal();
        float d = MathF.Max(CuboidMinDepth, MathF.Abs(y0 - y));
        while (Points.Count < 3) AddPoint(x, y, 1f, 0);
        SetPoint(2, new Vector2(x1 + d, y1 - d));
    }

    /// <summary>深度（由背面右下角反推：`d = 正面下边 − 那个角的 y`）。</summary>
    public float CuboidDepthLocal()
    {
        if (Points.Count < 3) return 0f;
        var (_, _, _, y1) = CuboidFrontLocal();
        return MathF.Max(0f, y1 - CurvePointLocal(2).Y);
    }

    // ---- 四面体（两笔：底面三角形 → 顶点）-------------------------------

    /// <summary>**第 1 笔**：底面三角形（照他：底边水平、顶点居中向上）。</summary>
    public void SetTetraBase(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 3) AddPoint(x0, y0, 1f, 0);
        float minX = MathF.Min(x0, x1), maxX = MathF.Max(x0, x1);
        float minY = MathF.Min(y0, y1), maxY = MathF.Max(y0, y1);
        SetPoint(0, new Vector2(minX, maxY));                       // 底左
        SetPoint(1, new Vector2(maxX, maxY));                       // 底右
        SetPoint(2, new Vector2((minX + maxX) * 0.5f, minY));       // 底顶（居中）
    }

    /// <summary>**第 2 笔**：顶点（拖到哪就是哪）。</summary>
    public void SetTetraApex(float x, float y)
    {
        while (Points.Count < 4) AddPoint(x, y, 1f, 0);
        SetPoint(3, new Vector2(x, y));
    }

    /// <summary>顶点（局部坐标）：第 2 笔拖到的地方；还没拖过时退化成底面那个顶。</summary>
    public Vector2 TetraApexLocal()
        => Points.Count >= 4 ? CurvePointLocal(3) : CurvePointLocal(2);

    // ---- 棱柱 / 棱锥 / 棱台（两笔：底面正 n 边形 → 顶上那个中心）-----------------
    //
    // 用户 2026-09-20 提出，见 计划-图形工具.md §32 / §34。三个"为什么这么定"：
    //   · **立着画**（底面水平俯视压扁 ＋ 侧棱竖直）：课本上正三/五/六棱柱都是这个样子。
    //     长方体是另一个朝向（正面矩形 ＋ 纵深 45°），两者**并存**、各有各的用处。
    //   · **几边形是可选的档位**（3~6），和直线的线型同构：画之前在图形面板那一格选，
    //     画的那一刻写进对象（见 Stroke.PrismSides）。
    //   · **第 2 笔拖到哪就是哪**（照四面体的顶点）：所以"往上拖 = 直棱柱 / 直棱锥 / 直棱台、
    //     拖歪 = 斜的"是**同一个动作的自然结果**，不需要再分两个工具。
    //
    // 三兄弟**控制点完全一样**（底面外接框两角 ＋ 顶上那个中心），差别只在顶面怎么来：
    // 平移一份（棱柱）/ 缩成一点（棱锥）/ 缩小一份（棱台）——见 PrismTopLocal。

    /// <summary>
    /// **底面几边形**（3~6）。和 <see cref="CurveAxis"/>（抛物线朝向）同一个地位：
    /// **画之前选好**，画的那一刻写进对象，之后它就是这条对象自己的属性——
    /// 面板再换档也不会回头改已经画好的（见 Engine.CycleSolidSides）。
    ///
    /// 存成一个**可读的整数**而不是四个枚举值：3/4/5/6 本来就是这条对象唯一想知道的事，
    /// 拆成 `Prism3/Prism4/...` 反而要在四处做映射。
    /// </summary>
    public int PrismSides = 4;

    /// <summary>档位的上下限（<see cref="PrismSides"/> 读出来先夹一道，坏值也不至于画出怪东西）。</summary>
    public const int MinPrismSides = 3;
    public const int MaxPrismSides = 6;

    /// <summary>不开棱柱工具时（别的图形 / 反序列化兜底）用的默认档：**四棱柱**。</summary>
    public const int DefaultPrismSides = 4;

    /// <summary>这一档的边数（夹过上下限的）。**所有几何都读它**，不要直接读字段。</summary>
    public int PrismSidesClamped => Math.Clamp(PrismSides, MinPrismSides, MaxPrismSides);

    /// <summary>底面外接框（局部坐标，已归一成 左上 / 右下）——`Points[0]` 与 `Points[1]`。</summary>
    public (float X0, float Y0, float X1, float Y1) PrismBaseBoxLocal()
    {
        var a = CurvePointLocal(0);
        var b = Points.Count >= 2 ? CurvePointLocal(1) : a;
        return (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    /// <summary>底面中心（局部坐标）= 外接框的中心。也是**侧棱向量的起点**。</summary>
    public Vector2 PrismBaseCenterLocal()
    {
        var (x0, y0, x1, y1) = PrismBaseBoxLocal();
        return new Vector2((x0 + x1) * 0.5f, (y0 + y1) * 0.5f);
    }

    /// <summary>
    /// 顶面中心（局部坐标）= **第 2 笔拖到的地方**；还没拖过时退化成底心
    ///（这时侧棱向量是零，画出来是一条扁的底边，不会崩）。
    /// </summary>
    public Vector2 PrismApexLocal()
        => Points.Count >= 3 ? CurvePointLocal(2) : PrismBaseCenterLocal();

    /// <summary>
    /// **侧棱向量**（顶心 − 底心）。零向量 = 还没拖第二笔。
    /// 它就是"这一步拖出来的方向"：竖直 = 直棱柱，歪 = 斜棱柱。
    /// </summary>
    public Vector2 PrismLateralLocal() => PrismApexLocal() - PrismBaseCenterLocal();

    /// <summary>
    /// 底面的 `n` 个顶点（局部坐标）：底面上是一个**正** n 边形，落到画面上两步——
    /// **压扁**（俯视）＋ **错切**（远侧往右挪一点，斜二测）。
    /// 两条都在 <see cref="ShapeSpec.PrismBasePointOffset"/> 那一份实现里（画布与图标共用）。
    ///
    /// 结果长什么样（都是课本那个样子）：
    ///   · **三 / 五棱柱**：正前方是一条边（帐篷形）；
    ///   · **四棱柱**：底面是**前边水平、后边也水平**的平行四边形（＝斜二测的正方形）；
    ///   · **六棱柱**：左右两个尖点。
    ///
    /// 起始角见 <see cref="ShapeSpec.PrismBaseOffsetDegrees"/>。顶点在底面参数意义下是
    /// **正** n 边形，所以"正三棱柱 / 正六棱柱"是白拿的。
    /// </summary>
    public Vector2[] PrismBaseLocal()
    {
        var (x0, y0, x1, y1) = PrismBaseBoxLocal();
        float rx = MathF.Max(0.5f, (x1 - x0) * 0.5f);
        float ry = MathF.Max(0.5f, (y1 - y0) * 0.5f);
        var c = PrismBaseCenterLocal();
        int n = PrismSidesClamped;
        float offset = ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f;
        var pts = new Vector2[n];
        for (int k = 0; k < n; k++)
        {
            // 屏幕 y 向下，所以参数角 90° 落在**下方**（= "正前方"）。
            float a = offset + k * MathF.Tau / n;
            pts[k] = c + ShapeSpec.PrismBasePointOffset(MathF.Cos(a), MathF.Sin(a), rx, ry);
        }
        return pts;
    }

    /// <summary>
    /// **顶面**的那 `n` 个点（局部坐标）——棱柱／棱锥／棱台**一家人**在这里分岔：
    ///   · **棱柱**：底面整体**平移**一个侧棱向量（顶面和底面全等）；
    ///   · **棱锥**：`n` 个点**全都落在顶点上**（顶面退化成一个点，所以没有顶面那一圈）；
    ///   · **棱台**：从顶点**按固定比例缩回来**（顶面和底面同形、小一圈，所以侧棱会"收"）。
    ///
    /// ⚠ 三支都算完再返回，调用方不用再判一次种类——"侧棱 = 底点 → 顶面同号那个点"
    /// 这条规则对三兄弟**都成立**（棱锥时那些点全部等于顶点，正好就是"底点 → 顶点"）。
    ///
    /// internal 而不是 private：自检要直接数它（"棱锥的 n 个顶面点全等吗"、
    /// "棱台的上底是不是按比例缩的"——这两条从画出来的棱上反推很别扭）。
    /// </summary>
    internal Vector2[] PrismTopLocal()
    {
        var b = PrismBaseLocal();
        int n = b.Length;
        var top = new Vector2[n];

        if (Kind == StrokeKind.Pyramid)
        {
            var apex = PrismApexLocal();
            for (int k = 0; k < n; k++) top[k] = apex;
            return top;
        }

        if (Kind == StrokeKind.Frustum)
        {
            // 上底 = 顶点 ＋（下底的点 − 底心）× 比例：同形缩小的那一份（见 ShapeSpec.FrustumTopScale）。
            var c = PrismBaseCenterLocal();
            var apex = PrismApexLocal();
            float k0 = ShapeSpec.FrustumTopScale;
            for (int k = 0; k < n; k++) top[k] = apex + (b[k] - c) * k0;
            return top;
        }

        var lat = PrismLateralLocal();                     // 棱柱：平移（也是默认那一支）
        for (int k = 0; k < n; k++) top[k] = b[k] + lat;
        return top;
    }

    /// <summary>**第 1 笔**：底面外接框（归一成 左上 / 右下，和长方体那一笔同一套）。</summary>
    public void SetPrismBase(float x0, float y0, float x1, float y1)
    {
        while (Points.Count < 2) AddPoint(x0, y0, 1f, 0);
        SetPoint(0, new Vector2(MathF.Min(x0, x1), MathF.Min(y0, y1)));
        SetPoint(1, new Vector2(MathF.Max(x0, x1), MathF.Max(y0, y1)));
    }

    /// <summary>
    /// **第 2 笔**：顶面中心（拖到哪就是哪）。侧棱向量由它和底心相减得到。
    /// 和长方体的第 2 笔**不同**：那边只吃竖直分量、方向恒定 45°；这边**方向也吃**，
    /// 因为"直还是斜"就是由它定的（用户要的就是这个）。
    /// </summary>
    public void SetPrismApex(float x, float y)
    {
        while (Points.Count < 3) AddPoint(x, y, 1f, 0);
        SetPoint(2, new Vector2(x, y));
    }

    /// <summary>
    /// 把"顶面中心"**吸到竖直**（直棱柱）——只改 x，y 一个字不动。
    /// 由引擎在拖第二笔时先探一次（见 Engine 的 `ApplyStep`），
    /// 所以落进模型的就已经是吸过的位置了（和"拖顶点吸附"同一个口径：吸完才写模型）。
    /// </summary>
    public void SnapPrismApexVertical()
    {
        if (Points.Count < 3) return;
        var p = CurvePointLocal(2);
        SetPoint(2, new Vector2(PrismBaseCenterLocal().X, p.Y));
    }

    /// <summary>
    /// **棱柱／棱锥／棱台这一族的棱**（`hidden = false` 给看得见的、`true` 给被挡住的）。
    ///
    /// 三兄弟共用这一份：底面都是那个正 n 边形，区别只在"顶面"是什么
    /// （棱柱＝平移一份、棱锥＝缩成一点、棱台＝缩小一份，见 <see cref="PrismTopLocal"/>）。
    /// 画出三条线族：
    ///   · **底面的边** n 条；**顶面的边** n 条（棱锥没有——顶面只有一点）；
    ///   · **侧棱** n 条（= 底面的点 → 顶面同号那个点）。
    ///
    /// 实 / 虚的判据**只有一条、而且是通用的**（计划-图形工具.md §32.4）——
    /// 因为底面是"俯视压扁 ＋ 错切"画的，**"远近"就等于屏幕上的上下**：
    ///   · **底面某条边**：中点在底心**下方** → 近侧、看得见；否则被挡住（虚线）；
    ///   · **某条侧棱**：它相邻的两个侧面**只要有一个看得见** → 这条棱看得见；
    ///   · **顶面**：全部看得见（俯视角度下顶面恒可见）。
    ///
    /// 这条判据对**斜棱柱 / 斜棱锥 / 斜棱台**同样成立：侧面的朝向只由底面那条边定，
    /// 和上面歪不歪无关。
    /// 长方体现在还是硬编码三条虚线（它是另一个朝向的画法，见 CuboidEdges），**两边不共用**——
    /// 硬编码那三条是照 InkClass 抄的，换成通用判据会改变长方体的既有外观（那是不许动的）。
    ///
    /// 渲染与熔墨共用这一份（见 <see cref="InkPieces"/>）。自检也数它（`--prismtest` 数
    /// "几条实线几条虚线"），所以是 internal 而不是 private。
    /// </summary>
    internal List<InkPiece> PrismFamilyEdges(bool hidden)
    {
        var list = new List<InkPiece>();
        if (Points.Count < 2) return list;

        var b = PrismBaseLocal();
        var lat = PrismLateralLocal();
        int n = b.Length;

        // **还没拖第二笔**（侧棱还是零）：只画底面那一圈、全实线——
        // 这是第 1 笔的预览（拖出来是个扁的正 n 边形，正好告诉用户"底面画好了"）。
        // 不给它走下面那套判据：那时"顶面"和"底面"重合，虚实分出来的线会叠在一起。
        // （棱锥／棱台同样走这一支：顶点还没定，画出来就该只是那个底面。）
        if (lat.LengthSquared() < 1e-6f)
        {
            if (!hidden)
            {
                var ring = new List<Vector2>(n + 1);
                for (int k = 0; k <= n; k++) ring.Add(b[k % n]);
                list.Add(new InkPiece(ring, false));
            }
            return list;
        }

        var top = PrismTopLocal();
        // 顶面那一圈要不要画：棱锥的顶面退化成一个点，画"面"就成了一个点上的圈（看着像个墨疙瘩）。
        bool hasTopRing = Kind != StrokeKind.Pyramid;

        // 侧面 k（= 底边 k）看得见吗：判据在 `ShapeSpec.PrismFaceVisible`
        //（"外法向朝向观察者"，**错切要算进去**——那是斜二测的四棱柱"右面可见"的来源）。
        var (bx0, by0, bx1, by1) = PrismBaseBoxLocal();
        float rx = MathF.Max(0.5f, (bx1 - bx0) * 0.5f);
        float ry = MathF.Max(0.5f, (by1 - by0) * 0.5f);
        float offset = ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f;
        var faceVisible = new bool[n];
        for (int k = 0; k < n; k++)
        {
            int nx = (k + 1) % n;
            float a0 = offset + k * MathF.Tau / n, a1 = offset + nx * MathF.Tau / n;
            faceVisible[k] = ShapeSpec.PrismFaceVisible(
                (MathF.Cos(a0) + MathF.Cos(a1)) * 0.5f,
                (MathF.Sin(a0) + MathF.Sin(a1)) * 0.5f, rx, ry);
        }

        void Add(Vector2 p, Vector2 q, bool hid)
        {
            if (hid != hidden) return;      // 这一趟只要这一类
            list.Add(new InkPiece(new List<Vector2> { p, q }, hid));
        }

        for (int k = 0; k < n; k++)
        {
            int nx = (k + 1) % n;
            Add(b[k], b[nx], !faceVisible[k]);                  // 底面的边
            if (hasTopRing) Add(top[k], top[nx], false);        // 顶面的边：恒实线
            // 侧棱 k：相邻的两个侧面（k−1 与 k）有一个看得见，这条棱就在轮廓上。
            // 棱锥这里 top[k] 就是顶点，所以自动成了"底点 → 顶点"。
            Add(b[k], top[k], !(faceVisible[k] || faceVisible[(k + n - 1) % n]));
        }
        return list;
    }

    private ID2D1PathGeometry BuildPrism(ID2D1Factory1 factory, bool hidden)
    {
        if (Points.Count < 2) return hidden ? null : BuildLine(factory);
        var pieces = PrismFamilyEdges(hidden);
        return pieces.Count == 0 ? null : FromPieces(factory, pieces);
    }

    /// <summary>椭圆上 `u` 处的点（`u ∈ [0,1)`：0 = 右、0.25 = 下、0.5 = 左、0.75 = 上；屏幕 y 向下）。</summary>
    private static Vector2 EllipseArcPoint(Vector2 c, float rx, float ry, float u)
    {
        float a = u * MathF.Tau;
        return new Vector2(c.X + rx * MathF.Cos(a), c.Y + ry * MathF.Sin(a));
    }

    /// <summary>
    /// 立体图形的**逐笔点列**（`hidden = false` 给看得见的、`true` 给被挡住的）：
    ///   · **圆柱**：顶面整圈 ＋ 底面下半圈 ＋ 两条母线；被挡住的是底面上半圈；
    ///   · **圆锥**：底面下半圈 ＋ 两条母线（顶点 = 上边中点）；被挡住的是底面上半圈；
    ///   · **圆台**：上底整圈（小一圈）＋ 底面下半圈 ＋ 两条**收进去**的母线；被挡住的同样；
    ///   · **球**：轮廓**整圆** ＋ 赤道椭圆的**下半圈**；被挡住的是赤道的**上半圈**
    ///     （远侧那半圈被球自己挡着）。
    ///
    /// **渲染与熔墨共用这一份**（<see cref="BuildSolid"/> / <see cref="BuildSolidHidden"/> /
    /// <see cref="InkPieces"/>）——两边各写一套就会"画出来的和熔出来的不一样"，
    /// 那正是用户 2026-09-20 反馈的那类问题（计划-图形工具.md §25）。
    /// </summary>
    private List<InkPiece> SolidPieces(bool hidden)
    {
        var list = new List<InkPiece>();

        // **球先单独走**：它的圆不是从"上下两个椭圆"派生的（见 SphereLocal 那段），
        // 所以不能落进下面那套 `SolidEllipsesLocal` 的算法里。
        if (Kind == StrokeKind.Sphere)
        {
            var (sc, sr) = SphereLocal();
            var (_, eqRx, eqRy) = SphereEquatorLocal();
            if (hidden)
            {
                list.Add(new InkPiece(ArcPoints(sc, eqRx, eqRy, 0.5f, 1f), true));   // 赤道远侧
            }
            else
            {
                list.Add(new InkPiece(ArcPoints(sc, sr, sr, 0f, 1f), false));        // 轮廓整圆
                list.Add(new InkPiece(ArcPoints(sc, eqRx, eqRy, 0f, 0.5f), false));  // 赤道近侧
            }
            return list;
        }

        var (cx, topCy, botCy, rx, ry) = SolidEllipsesLocal();
        var bot = new Vector2(cx, botCy);

        if (hidden)
        {
            // 底面**上半圈**（`u` 从 0.5 走到 1，走的是 −y 那一侧 = 屏幕上方）：
            // 从上面看下去它是被实体挡住的，课本上就画虚线。
            // **三种旋转体共用这一条**——圆台也一样（被挡住的永远只有下底的上半圈）。
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0.5f, 1f), true));
            return list;
        }

        if (Kind == StrokeKind.Cylinder)
        {
            // 顶面整圈：`u` 从 0 走到 1（闭合）
            list.Add(new InkPiece(ArcPoints(new Vector2(cx, topCy), rx, ry, 0f, 1f), false));
            // 底面**下半圈**（u: 0 → 0.5 走的是 +y 那一侧 = 屏幕上看得见的下半圈）
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0f, 0.5f), false));
            // 两条母线：底面左右端点 → 顶面左右端点
            list.Add(new InkPiece(new List<Vector2> { new(cx - rx, topCy), new(cx - rx, botCy) }, false));
            list.Add(new InkPiece(new List<Vector2> { new(cx + rx, topCy), new(cx + rx, botCy) }, false));
        }
        else if (Kind == StrokeKind.ConeFrustum)
        {
            // 上底整圈：**小一圈的那个椭圆**（圆心 / 半轴的算式只有一处，见 ConeFrustumTopLocal）
            var (tc, trx, try_) = ConeFrustumTopLocal();
            list.Add(new InkPiece(ArcPoints(tc, trx, try_, 0f, 1f), false));
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0f, 0.5f), false));
            // 两条母线：底面左右端点 → **上底**左右端点（所以它们是往中间收的斜线）
            list.Add(new InkPiece(new List<Vector2> { new(cx - rx, botCy), new(tc.X - trx, tc.Y) }, false));
            list.Add(new InkPiece(new List<Vector2> { new(cx + rx, botCy), new(tc.X + trx, tc.Y) }, false));
        }
        else
        {
            var apex = ConeApexLocal();
            list.Add(new InkPiece(ArcPoints(bot, rx, ry, 0f, 0.5f), false));
            // 两条母线：底面左右端点 → 顶点
            list.Add(new InkPiece(new List<Vector2> { new(cx - rx, botCy), apex }, false));
            list.Add(new InkPiece(new List<Vector2> { new(cx + rx, botCy), apex }, false));
        }
        return list;
    }

    /// <summary>立体图形的**实线几何**（看得见的那几笔）——点列在 <see cref="SolidPieces"/>。</summary>
    private ID2D1PathGeometry BuildSolid(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return BuildLine(factory);
        return FromPieces(factory, SolidPieces(hidden: false));
    }

    /// <summary>
    /// 立体图形**被挡住的那半圈**（虚线，走辅助几何槽 —— 和双曲线的渐近线共用一个槽）。
    /// 点列在 <see cref="SolidPieces"/>（`hidden: true`）。
    /// </summary>
    private ID2D1PathGeometry BuildSolidHidden(ID2D1Factory1 factory)
    {
        if (Points.Count < 2) return null;
        return FromPieces(factory, SolidPieces(hidden: true));
    }

    /// <summary>
    /// **长方体的棱**（`hidden = false` 画看得见的、`true` 画被挡住的）。
    ///
    /// 八个顶点由"正面矩形 ＋ 深度 d"派生（深度方向恒定**右上 45°**，照 InkClass）。
    /// 实 / 虚的分配也照他：正面四条边 ＋ 背面**上横 / 右竖** ＋ **左上 / 右上 / 右下**三条斜棱是实线；
    /// 背面**下横**、**左下斜棱**、背面**左竖**是虚线（从正面看过去被挡住）。
    ///
    /// 渲染与熔墨共用这一份（见 <see cref="InkPieces"/>）。
    /// </summary>
    private List<InkPiece> CuboidEdges(bool hidden)
    {
        var list = new List<InkPiece>();
        var (x0, y0, x1, y1) = CuboidFrontLocal();
        float d = CuboidDepthLocal();

        void Edge(Vector2 a, Vector2 b) => list.Add(new InkPiece(new List<Vector2> { a, b }, false));
        void Hidden(Vector2 a, Vector2 b) => list.Add(new InkPiece(new List<Vector2> { a, b }, true));

        if (!hidden)
        {
            // 正面矩形（第 1 笔画的四条边）
            Edge(new(x0, y0), new(x1, y0));
            Edge(new(x1, y0), new(x1, y1));
            Edge(new(x1, y1), new(x0, y1));
            Edge(new(x0, y1), new(x0, y0));
            // 背面：上横、右竖
            Edge(new(x0 + d, y0 - d), new(x1 + d, y0 - d));
            Edge(new(x1 + d, y0 - d), new(x1 + d, y1 - d));
            // 斜棱：左上、右上、右下
            Edge(new(x0, y0), new(x0 + d, y0 - d));
            Edge(new(x1, y0), new(x1 + d, y0 - d));
            Edge(new(x1, y1), new(x1 + d, y1 - d));
        }
        else
        {
            // 被挡住的：背面下横、左下斜棱、背面左竖
            Hidden(new(x0 + d, y1 - d), new(x1 + d, y1 - d));
            Hidden(new(x0, y1), new(x0 + d, y1 - d));
            Hidden(new(x0 + d, y0 - d), new(x0 + d, y1 - d));
        }
        return list;
    }

    private ID2D1PathGeometry BuildCuboid(ID2D1Factory1 factory, bool hidden)
    {
        if (Points.Count < 2) return BuildLine(factory);
        return FromPieces(factory, CuboidEdges(hidden));
    }

    /// <summary>
    /// **四面体**的棱：底面三角形三条边 ＋ 顶点到底面三顶点的三条棱，**全是实线**（照 InkClass）。
    /// 渲染与熔墨共用这一份。
    /// </summary>
    private List<InkPiece> TetraEdges()
    {
        var list = new List<InkPiece>();
        var p0 = CurvePointLocal(0);
        var p1 = CurvePointLocal(1);
        var p2 = CurvePointLocal(2);
        var apex = TetraApexLocal();
        void Edge(Vector2 a, Vector2 b) => list.Add(new InkPiece(new List<Vector2> { a, b }, false));
        Edge(p0, p1);       // 底面三条边
        Edge(p1, p2);
        Edge(p2, p0);
        Edge(p0, apex);     // 三条棱
        Edge(p1, apex);
        Edge(p2, apex);
        return list;
    }

    private ID2D1PathGeometry BuildTetra(ID2D1Factory1 factory)
    {
        if (Points.Count < 3) return BuildLine(factory);
        return FromPieces(factory, TetraEdges());
    }

    private ID2D1PathGeometry BuildArrow(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var (_, wingA, wingB) = ArrowHeadPoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.EndFigure(FigureEnd.Open);
        sink.BeginFigure(wingA, FigureBegin.Hollow);
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.AddLine(wingB);
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 箭头头部的三个关键点（**局部坐标**）：轴线上的"根部"和两个翅膀尖。
    ///
    /// 抽出来是因为它有三个用户，**必须永远一致**：
    ///   ① <see cref="BuildArrow"/>——真正画出来的那条折线；
    ///   ② <see cref="ShapeOutline"/>——橡皮/框选/套索用的轮廓折线；
    ///   ③ 墨迹范围（<see cref="LineLikeInkBounds(Vector2, Vector2, in Matrix3x2)"/>）——箭头那两个
    ///      翅膀尖**不在**"两个端点之间"，只按端点算，翅膀就会落在框外，而框又是脏区/命中/
    ///      导出裁切的依据：脏区漏掉那一块屏幕上会留残影（这几条账见 PaddedBounds 的注释）。
    /// 三处各写一份公式是迟早要咬人的（头部长短、张开比例都在这几行里）。
    ///
    /// 端点由调用方给（而不是自己读 `Points`）：拖端点的手势里要按**预览端点**算，
    /// 这样画出来的箭头、框、脏区才是同一个箭头。
    /// </summary>
    private (Vector2 root, Vector2 wingA, Vector2 wingB) ArrowHeadPoints(Vector2 a, Vector2 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        float head = Math.Clamp(len * 0.28f, 10f, 48f);
        var root = new Vector2(b.X - dx * head, b.Y - dy * head);
        float nx = -dy, ny = dx;
        float spread = head * 0.45f;
        return (root,
                new Vector2(root.X + nx * spread, root.Y + ny * spread),
                new Vector2(root.X - nx * spread, root.Y - ny * spread));
    }

    /// <summary>箭头头部几何，端点取模型自己的首尾两点（画箭头 / 算轮廓用）。</summary>
    private (Vector2 root, Vector2 wingA, Vector2 wingB) ArrowHeadPoints()
    {
        var (a, b) = Endpoints();
        return ArrowHeadPoints(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
    }

    /// <summary>图像对象的矩形几何（命中测试与裁剪用，不做描边）。</summary>
    private ID2D1PathGeometry BuildImageRect(ID2D1Factory1 factory)
    {
        var (a, b) = Endpoints();
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(new Vector2(a.X, a.Y), FigureBegin.Filled);
        sink.AddLine(new Vector2(b.X, a.Y));
        sink.AddLine(new Vector2(b.X, b.Y));
        sink.AddLine(new Vector2(a.X, b.Y));
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>
    /// 中心线（开放折线）。**这就是笔迹的几何**：宽度、圆头端帽、拐角全部由
    /// Direct2D 按描边样式自己算（见 Overlay 里的 `DrawGeometry(..., Gfx.Round)`）。
    ///
    /// 2026-09-14：以前这里画的是"我们自己拼的轮廓"（等宽带子 + 半圆端帽 +
    /// 外侧圆弧 + 内角补角 + 压感宽度），用户实测后认为 D2D 的观感更好，
    /// 于是整套删掉。
    /// </summary>
    private ID2D1PathGeometry BuildCenterline(ID2D1Factory1 factory)
        => BuildCenterlineCore(factory, float.MaxValue);

    /// <summary>
    /// 回放用：只画**前 maxParam**（点序号，0..Points.Count-1，可带小数）那一段中心线。
    /// **不进几何缓存**（每帧都在变，缓存只会不停重建）——调用方画完负责 Dispose。
    /// </summary>
    internal ID2D1PathGeometry BuildCenterlinePrefix(ID2D1Factory1 factory, float maxParam)
        => BuildCenterlineCore(factory, maxParam);

    private ID2D1PathGeometry BuildCenterlineCore(ID2D1Factory1 factory, float maxParam)
    {
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();

        bool clipped = maxParam < Points.Count - 1 - 1e-4f;

        // **每条剩下的段一个 figure，但它们在同一条几何里**——这一点是关键：
        // 一次 DrawGeometry 只混合一次，所以半透明荧光笔即使自相重叠也不会变深。
        // 拆成两个对象（两个 DrawGeometry）就会混合两次（实测差 0 → 56）。
        foreach (var (a, b0) in RemainingRuns())
        {
            if (clipped && a >= maxParam - 1e-6f) break;   // 这一段整个在前缀之后：不画
            float b = clipped ? MathF.Min(b0, maxParam) : b0;

            // **墨迹模型（实验，`--motion`）**：整条用选中的运动模型输出当中心线。
            // 只对"没被橡皮擦过、也不是回放前缀"的整笔生效；擦除/回放照样走旧路
            //（擦除区间是按原始点切的，建模点和参数序号对不上——见 StrokeMotion 的注释）。
            // 上游输出点密度足够（≥180Hz），直接当折线描边即可（弦高误差远小于 1px）。
            if (!clipped && Erased.Count == 0 && StrokeMotion.Build(this))
            {
                // mean2 的曲线层固定为**过点曲线**（拟合档已随停用清理，2026-10-05）。
                // `--nosmooth` 时连模型输出也不上曲线——这样它才是一个真正的
                // "折线对照组"（否则模型路径照样出曲线，开关等于没有）。
                bool drew = StrokeSmoothing.Enabled && AppendSmoothedModeledRun(sink);
                GeomTrace(this, "A", "模型", drew);
                if (!drew)
                {
                    int mn = StrokeMotion.Count;
                    var p0 = StrokeMotion.At(0);
                    sink.BeginFigure(new Vector2(p0.X, p0.Y), FigureBegin.Hollow);
                    for (int k = 1; k < mn; k++)
                    {
                        var p = StrokeMotion.At(k);
                        sink.AddLine(new Vector2(p.X, p.Y));
                    }
                }
            }
            else
            {
                // 曲线化（`--smooth`）：把这一段 run 的采样点喂给曲线器，输出一串三次贝塞尔。
                // **点还是原来那些点**——曲线严格过每一个采样点，直角由角点保护保住；
                // 不生效时（开关关着 / 段数不够）原样退回下面的折线路径。
                bool smoothed = StrokeSmoothing.Enabled && !RawWhileLive && AppendSmoothedRun(sink, a, b);
                GeomTrace(this, "A", "原始", smoothed,
                          $"clipped={clipped} erased={Erased.Count} model={StrokeMotion.Build(this)} live={RawWhileLive} enabled={StrokeSmoothing.Enabled}");
                if (!smoothed)
                {
                    sink.BeginFigure(PointAtParam(a), FigureBegin.Hollow);
                    for (int i = 1; i < Points.Count; i++)
                    {
                        if (i < a - 1e-6f) continue;
                        if (i > b + 1e-6f) break;
                        sink.AddLine(new Vector2(Points[i].X, Points[i].Y));
                    }
                    // 终点只在"切出来的插值点"时才补。**必须是这个条件**：如果这一段的终点正好落在
                    // 某个采样点上，上面的循环已经把它加进去了，再补一次就给几何多出一个零长段——
                    // 没被擦过的笔迹（a=0、b=末尾）必须和"没有区间表"时**逐点一致**，
                    // 否则等于凭空改了笔迹几何。
                    if (MathF.Abs(b - MathF.Round(b)) > 1e-6f) sink.AddLine(PointAtParam(b));
                }
            }
            sink.EndFigure(FigureEnd.Open);
        }
        sink.Close();
        return geo;
    }

    /// <summary>
    /// `--geomtrace` 诊断：把"这一笔的几何走了哪条路"打一行出来（每笔一次）。
    /// 关掉时零开销（一个 bool 判断）。
    /// </summary>
    internal static void GeomTrace(Stroke s, string channel, string srcKind, bool curved, string why = "")
    {
        if (!global::InkEngine.InkEngine.GeomTraceOn) return;
        // **一笔会建两次几何**：正在写的时候一次（RawWhileLive=true）、落笔之后一次。
        // 而且**两条上屏通道可能都建**（A 总是先建、B 随后试），所以按
        // (阶段 × 通道) 去重，四个组合各打一行。少一个组合就少一条线索。
        int bit = (s.RawWhileLive ? 0 : 2) + (channel == "B" ? 1 : 0);
        if ((s.GeomTraceMask & (1 << bit)) != 0) return;
        s.GeomTraceMask |= 1 << bit;
        string stage = s.RawWhileLive ? "活笔" : "落笔后";
        float span = 0f;
        for (int i = 1; i < s.Points.Count; i++)
            span += Vector2.Distance(new Vector2(s.Points[i - 1].X, s.Points[i - 1].Y),
                                     new Vector2(s.Points[i].X, s.Points[i].Y));
        float spacing = s.Points.Count > 1 ? span / (s.Points.Count - 1) : 0f;
        string line = $"  [几何路径] {stage} {channel}-{srcKind}+{(curved ? "曲线" : "折线")}"
                      + $" | 点 {s.Points.Count} 均距 {spacing:F1}px 总长 {span:F0}px"
                      + $" | 压感={s.HasPressure} 活笔={s.RawWhileLive} 曲线开关={StrokeSmoothing.Enabled}"
                      + (why.Length > 0 ? " | " + why : "");
        Console.WriteLine(line);
        // 同时落一份文件：真机上只要把文件发过来，不用截控制台。
        try
        {
            if (s_traceWriter == null)
            {
                string p = Path.Combine(Path.GetTempPath(), "inkteach-geomtrace.txt");
                s_traceWriter = new StreamWriter(p, append: false) { AutoFlush = true };
                Console.WriteLine($"  [几何路径] 轨迹文件：{p}");
            }
            s_traceWriter.WriteLine(line);
        }
        catch { }
    }

    private static StreamWriter s_traceWriter;

    /// <summary>
    /// mean2：把建模输出喂进过点曲线，直接写成三次贝塞尔。
    /// 返回 false = 段数不够，调用方退回直线折线。
    /// </summary>
    private static bool AppendSmoothedModeledRun(ID2D1GeometrySink sink)
    {
        StrokeSmoothing.Begin();
        int n = StrokeMotion.Count;
        for (int i = 0; i < n; i++)
        {
            var p = StrokeMotion.At(i);
            StrokeSmoothing.Add(p.X, p.Y, p.Z);
        }
        int m = StrokeSmoothing.Finish();
        if (m <= 0) return false;

        var segs = StrokeSmoothing.Out;
        sink.BeginFigure(segs[0].P0, FigureBegin.Hollow);
        for (int k = 0; k < m; k++)
            sink.AddBezier(new BezierSegment(segs[k].C1, segs[k].C2, segs[k].P1));
        return true;
    }

    /// <summary>
    /// 曲线化的那一支：把 run `[a, b]` 的采样点（含两端可能被橡皮切出来的插值点）
    /// 喂给 <see cref="StrokeSmoothing"/>，写成三次贝塞尔。
    /// 返回 false = 段数不够、什么都没写（调用方退回折线，所以这里**不许先动 sink**）。
    /// </summary>
    private bool AppendSmoothedRun(ID2D1GeometrySink sink, float a, float b)
    {
        StrokeSmoothing.Begin();

        var start = PointAtParam(a);
        StrokeSmoothing.Add(start.X, start.Y, PressureAtParam(a));
        int i0 = (int)MathF.Floor(a) + 1;
        int i1 = (int)MathF.Floor(b);
        for (int i = i0; i <= i1 && i < Points.Count; i++)
            StrokeSmoothing.Add(Points[i].X, Points[i].Y, Points[i].P);
        if (MathF.Abs(b - MathF.Round(b)) > 1e-6f)
        {
            var end = PointAtParam(b);
            StrokeSmoothing.Add(end.X, end.Y, PressureAtParam(b));
        }

        int n = StrokeSmoothing.Finish();
        if (n <= 0) return false;

        var segs = StrokeSmoothing.Out;
        sink.BeginFigure(segs[0].P0, FigureBegin.Hollow);
        for (int k = 0; k < n; k++)
            sink.AddBezier(new BezierSegment(segs[k].C1, segs[k].C2, segs[k].P1));
        return true;
    }

    /// <summary>
    /// 单点笔迹 = 一个圆点。**不能靠描边**：零长度的线描出来什么都没有，
    /// 所以直接给一个半径为半个笔宽的圆，渲染那一侧会填充它。
    ///
    /// 有压感时半径**取这一点的压力**：轻轻一点是一个小点、按重了是一个大点
    /// （和真笔一样；等宽的写法会让"点一下"永远是一个固定大小的圆）。
    /// </summary>
    private ID2D1Geometry BuildDot(ID2D1Factory1 factory)
    {
        float rad = DotRadius();
        return factory.CreateEllipseGeometry(
            new Ellipse(new Vector2(Points[0].X, Points[0].Y), rad, rad));
    }
}

// ---------------------------------------------------------------------------
//  Undo / redo
// ---------------------------------------------------------------------------

