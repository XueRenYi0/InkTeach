// [职责] 坐标系/数轴/四种曲线的几何构建。
// [测试] --shapetest

using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

internal sealed partial class Stroke
{
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

}
