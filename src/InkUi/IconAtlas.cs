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
    private static ID2D1Factory1 _factory;
    private static ID2D1PathGeometry _cone;
    private static ID2D1PathGeometry _nibQuad;      // 激光笔那支"笔头"（见 LaserNibQuad，建一次）

    /// <summary>
    /// 建一次"圆头圆角"的描边样式（自绘图标要用）。由界面在 Attach 时调一次。
    /// 上游图标都是填充路径，不需要它；我们自己画的那几个（激光笔）是**线条**，
    /// 线头不圆的话，那个"笔＋光束"会像三根火柴棍。
    /// </summary>
    public static void Init(ID2D1Factory1 factory)
    {
        if (factory == null) return;
        _factory = factory;
        if (_round != null) return;
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
        // **上游图标库里没有的那两个图形图标，在这里先接住**：
        //   · 平行四边形：Fluent / Material Symbols 都没有这个专名；
        //   · 椭圆：Fluent 只有正圆（Circle），而"圆"和"椭圆"现在是上带上挨着的两段，
        //     两个都画正圆的话老师分不出哪段是哪个。
        // 接住它们而不是往 Icons.g.cs 里塞：那张表是 tools/gen-ui-icons.ps1 生成的
        // （文件头写着"请勿手改"），手写的路径进去，下次重跑脚本就没了。
        // 这里的画法照仓库里已有的自绘图标（激光笔、两种橡皮）：网格 24、圆头圆角、
        // 线宽和上游 regular 那一套（1.5）对齐，混在一排里看不出是谁画的。
        if (name == "parallelogram") { DrawParallelogram(ctx, x, y, size, brush); return; }
        if (name == "oval") { DrawOval(ctx, x, y, size, brush); return; }
        if (name == "axes") { DrawAxes(ctx, x, y, size, brush); return; }
        // "坐标系 + 网格"那一档（2026-09-24）：同一张图多一层细格线。
        if (name == "axesGrid") { DrawAxes(ctx, x, y, size, brush, grid: true); return; }
        if (name == "numberline") { DrawNumberLine(ctx, x, y, size, brush); return; }
        // 2026-09-20 第五批：四种曲线的图标也只能自绘（上游图标库里没有抛物线 / 双曲线，
        // 更没有"一个周期的正弦"这种专名）。
        //
        // 抛物线有**两个名字**（parabola = 上下抛物 / parabolaRight = 左右抛物）：
        // 面板那一格要能看出下一笔是"上下"还是"左右"（用户 2026-09-20 定：
        // 这一档画之前选）。**具体朝上朝下、朝左朝右**由画的时候那一拖定
        //（照 InkClass 的 case 20/21），所以不需要四个名字。
        if (name == "parabola") { DrawParabola(ctx, x, y, size, brush, 0f); return; }
        if (name == "parabolaRight") { DrawParabola(ctx, x, y, size, brush, 90f); return; }
        if (name == "hyperbola") { DrawHyperbola(ctx, x, y, size, brush, asymptotes: true); return; }
        // 「无渐近线」那一档（2026-09-22）：同两支曲线，只是不画那两条虚线辅助线。
        if (name == "hyperbolaNoAsym") { DrawHyperbola(ctx, x, y, size, brush, asymptotes: false); return; }
        // 椭圆（带焦点）（2026-09-22）：椭圆 ＋ 两个焦点（小点）＋（有那一档）焦点三角形。
        // 和「椭圆」那张（`oval`）必须一眼分得开：多了两个点、以及两条连到 P 的线。
        if (name == "ovalFocusTri") { DrawOvalFocus(ctx, x, y, size, brush, triangle: true); return; }
        if (name == "ovalFocus") { DrawOvalFocus(ctx, x, y, size, brush, triangle: false); return; }
        // 立体图形（2026-09-20 第五批）：图标同样自绘。
        if (name == "cylinder") { DrawCylinder(ctx, x, y, size, brush); return; }
        if (name == "cone") { DrawCone(ctx, x, y, size, brush); return; }
        if (name == "conefrustum") { DrawConeFrustum(ctx, x, y, size, brush); return; }
        if (name == "sphere") { DrawSphere(ctx, x, y, size, brush); return; }
        if (name == "cuboid") { DrawCuboid(ctx, x, y, size, brush); return; }
        if (name == "tetrahedron") { DrawTetrahedron(ctx, x, y, size, brush); return; }
        // 棱柱 / 棱锥 / 棱台各四档（三/四/五/六）：各自段画法只换底面边数
        //（见 DrawPrism / DrawPyramid / DrawFrustum，三者共用底面与可见性那份）。
        if (name == "prism3") { DrawPrism(ctx, x, y, size, brush, 3); return; }
        if (name == "prism4") { DrawPrism(ctx, x, y, size, brush, 4); return; }
        if (name == "prism5") { DrawPrism(ctx, x, y, size, brush, 5); return; }
        if (name == "prism6") { DrawPrism(ctx, x, y, size, brush, 6); return; }
        if (name == "pyramid3") { DrawPyramid(ctx, x, y, size, brush, 3); return; }
        if (name == "pyramid4") { DrawPyramid(ctx, x, y, size, brush, 4); return; }
        if (name == "pyramid5") { DrawPyramid(ctx, x, y, size, brush, 5); return; }
        if (name == "pyramid6") { DrawPyramid(ctx, x, y, size, brush, 6); return; }
        if (name == "frustum3") { DrawFrustum(ctx, x, y, size, brush, 3); return; }
        if (name == "frustum4") { DrawFrustum(ctx, x, y, size, brush, 4); return; }
        if (name == "frustum5") { DrawFrustum(ctx, x, y, size, brush, 5); return; }
        if (name == "frustum6") { DrawFrustum(ctx, x, y, size, brush, 6); return; }
        if (name == "sine") { DrawWave(ctx, x, y, size, brush, cosine: false); return; }
        if (name == "cosine") { DrawWave(ctx, x, y, size, brush, cosine: true); return; }
        if (name == "wave") { DrawWaveLine(ctx, x, y, size, brush); return; }
        if (name == "tangent") { DrawTangent(ctx, x, y, size, brush); return; }
        // 直线那三档线型的图标（用户 2026-09-20 定：图形面板里"直线"那一段再点一次
        // 就在实线 / 虚线 / 点线之间换，所以要有三张）。同样只能自绘：
        // 上游那张 `lineWeight` 是实线的，一个虚线 / 点线专名都没有。
        if (name == "line") { DrawLineStyle(ctx, x, y, size, brush, StrokeDash.Solid); return; }
        if (name == "lineDash") { DrawLineStyle(ctx, x, y, size, brush, StrokeDash.Dashed); return; }
        if (name == "lineDot") { DrawLineStyle(ctx, x, y, size, brush, StrokeDash.Dotted); return; }
        // 「图库」那一段的图标（2026-09-22，用户要的"图像收藏"）：**书架**那个意思
        //（Fluent 上游有 Library，但沿用"平行四边形 / 抛物线"那几位的既成做法——
        //  没进生成表的名字在这里自绘，别去手改 `Icons.g.cs`，那份脚本一重跑就没了）。
        if (name == "library") { DrawLibrary(ctx, x, y, size, brush); return; }

        var geo = SvgPath.Get(PanelIcons.Get(name));
        if (geo == null) return;

        // 有些上游图标不是 24 网格（Material Symbols 用 960），而且原点可能是负的
        // （viewBox="0 -960 960 960"）。这些图标要先把**原点挪到 (0,0)**、再缩放。
        //
        // ⚠ 顺序不能反（2026-09-26 修）：矩阵乘法的语义是"**左边先作用**"
        // （`A * B` ＝ 先 A 后 B，仓库里所有图标都靠这条才画得对）。
        // 原来写的是"先缩放、再平移 (-ox,-oy)"，那个平移**不会被缩放**——
        // 960 网格的图标于是被扔到目标格子下方 960 像素处，画面上什么都没有。
        // 当时的症状是"Material 那一行三个格子全空"（做激光笔候选图时才第一次真去画它们，
        // 之前没有任何界面用过这 5 个外库图标，所以一直没被发现）。
        float box = 24f, ox = 0f, oy = 0f;
        if (PanelIcons.TryGetBox(name, out var vb))
        {
            var parts = vb.Split(' ');
            if (parts.Length >= 4
                && float.TryParse(parts[0], out var vx) && float.TryParse(parts[1], out var vy)
                && float.TryParse(parts[2], out var vw) && vw > 0)
            {
                box = vw; ox = vx; oy = vy;
            }
        }

        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateTranslation(-ox, -oy)   // ① 原点挪到 (0,0)
                      * Matrix3x2.CreateScale(size / box)       // ② 缩放到目标尺寸
                      * Matrix3x2.CreateTranslation(x, y)       // ③ 放到目标格子
                      * saved;

        // **描边型**的上游图（Lucide 那套）要"描"不要"填"：它们路径给的是**线的中心线**，
        // 当成填充画出来会变成一根细长条（不是一条线）。宽度用上游自己写的那个
        //（24 网格下的值，跟着上面那层缩放一起缩）。
        if (StrokedIcons.TryGetValue(name, out var strokeWidth))
        {
            ctx.DrawGeometry(geo, brush, DevForceStroke ?? strokeWidth, _round);
        }
        else
        {
            ctx.FillGeometry(geo, brush);
            // **外库口径补粗**（见 EmboldenIcons 的注释）：先按原样填一遍，
            // 再用同色在**路径边界**上描一圈——描边以边界为中心两边各摊一半，
            // 于是整条线正好加粗表里那个数。只有口径偏细的外库图标才走这一步
            //（本排 Fluent 的线宽本来就是 1.5，不在表里、一点都不动）。
            if (EmboldenIcons.TryGetValue(name, out var embolden))
                ctx.DrawGeometry(geo, brush, embolden, _round);
        }

        ctx.Transform = saved;
    }

    /// <summary>
    /// **哪些上游图标是"描边型"**（fill="none" + stroke），以及各自在 24 网格下的描边宽度。
    ///
    /// 目前两张：Lucide 的 `square-dashed-mouse-pointer`（选择工具那格子上的固定图标，ISC 许可）、
    /// Tabler 的 `pointer`（穿透那一格，MIT；它的**实心版** cursorArrowFilled 是填充型，不在这张表里）。
    ///
    /// ⚠ **线宽统一成 1.5，不照抄上游的 2**（2026-09-27 改，用户报的口径问题）：
    ///   Lucide 和 Tabler 默认都写 `stroke-width="2"`＝整格的 8.3%，而我们这一排
    ///   （Fluent UI System Icons regular）是 **24 网格、1.5 ＝ 6.25%**。
    ///   照抄的结果就是那两格比旁边**粗 33%**，缩到 24 像素并排一眼就看出来
    ///   （用户原话："穿透的鼠标图标 / 选中图标……这些感觉有点粗啊"）。
    ///   线宽是**整套的公共约定**，不是某一支图标的私有属性——形状照上游，
    ///   线宽跟本排走，这条比"原封不动照抄"重要（同一张图的路径一个坐标都没改）。
    ///   想看一眼粗细对比：`--panelshow <路径> 2` 会把这些描边强制回 2。
    /// </summary>
    private static readonly Dictionary<string, float> StrokedIcons = new()
    {
        ["lucideSelectPointer"] = 1.5f,
        ["cursorArrow"] = 1.5f,
    };

    /// <summary>
    /// **开发期专用**（`--panelshow <路径> [描边宽]`）：把描边型图标的线宽**强制**成这个值。
    /// null ＝ 按 <see cref="StrokedIcons"/> 那一份走（产品永远走这条）。
    /// 出"改之前 / 改之后"的对照图时用它，别为了一张图去改产品常量。
    /// </summary>
    internal static float? DevForceStroke;

    /// <summary>
    /// **哪些外库图标要"加粗"**（名字 → 额外描一圈的宽度，单位是 24 网格）。
    ///
    /// 为什么需要这张表：各家图标库的**线宽口径不一样**。Fluent 那套是 24 网格、
    /// 线粗 1.5（＝整格的 6.25%）；外库的图标按自己的 Box 缩放过来之后，线宽也跟着缩，
    /// 摆进那一排就会明显细一档。做法：这些图标是**填充型**（路径本身就是那条线的轮廓），
    /// 不能改 stroke-width，于是改成"填一遍 ＋ 再描一圈同色的边"——描边以路径边界为中心
    /// 两边各摊一半，所以总粗细 ＝ 原线宽 ＋ 表里这个数。
    ///
    /// ⚠ **原线宽必须按路径真量，不能信文档上的数字**（2026-09-27 的教训）：
    ///   Radix 的文档写"15 网格、线粗 0.5"，但那张橡皮的 `d` 里，
    ///   外轮廓和内轮廓两条平行边之间真实距离是 **1.0**（15 网格）——
    ///   放大到 24 网格是 **1.6**，本来就比 Fluent 的 1.5 略粗一点点。
    ///   当时按 0.5 算，补了 0.70，结果它的壁厚变成 **2.3**（比整排粗 53%），
    ///   做出来一排在带子里就它最重——用户 2026-09-27 一眼看出来："那个橡皮有点粗啊"。
    ///   所以现在**这一格不补**（1.6 ≈ 1.5，差 6.7%，和 Fluent 自己的几个图标一样在误差带里）。
    ///
    /// 表空着是**正常状态**：说明现在这一排所有图标的口径本来就对得上。
    /// </summary>
    private static readonly Dictionary<string, float> EmboldenIcons = new();

    /// <summary>在方块里居中画一个图标（按钮上用它，省得每处都自己算居中）。</summary>
    public static void DrawCentered(ID2D1DeviceContext ctx, string name, RectF box,
                                    float size, ID2D1Brush brush)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        Draw(ctx, name, cx - size * 0.5f, cy - size * 0.5f, size, brush);
    }

    /// <summary>
    /// **开发期出图用**：画一张**不在这张图标表里**的图标（拉上游候选笔时用，见 `--penshow`）。
    ///
    /// 和 <see cref="Draw"/> 同一套口径：按"盒子"缩放到目标尺寸、填充型、不补粗——
    /// 所以候选图上看到的样子就是它进了产品之后的样子。
    /// <paramref name="box"/> 是这个图标自己的网格（Fluent 一般是 24，
    /// 个别图标上游只出 20，见 PenIcons.BoxOf）。
    /// </summary>
    internal static void DrawCandidate(ID2D1DeviceContext ctx, string d, float box,
                                       RectF cell, float size, ID2D1Brush brush)
    {
        var geo = SvgPath.Get(d);
        if (geo == null) return;
        float cx = (cell.MinX + cell.MaxX) * 0.5f;
        float cy = (cell.MinY + cell.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / box)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;
        ctx.FillGeometry(geo, brush);
        ctx.Transform = saved;
    }

    /// <summary>
    /// **开发期出图用**：给候选笔加上"三道杠"（用户要的"表示发光的意思"，
    /// 见 `--penshow bars`）。位置就是激光笔那一套（见 <see cref="LaserNibFancy"/>）：
    /// 以 (7.60,16.40) 当笔尖，朝 135°±30°（左下，也就是笔尖射出去的方向）三道，
    /// 从 3.4 格起笔、长 2.4、粗 1.4 —— 这样各支笔看到的是**同一副杠**，才好比。
    /// </summary>
    internal static void DrawGlowBars(ID2D1DeviceContext ctx, RectF cell, float size, ID2D1Brush brush)
    {
        float cx = (cell.MinX + cell.MaxX) * 0.5f;
        float cy = (cell.MinY + cell.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;
        var tip = new Vector2(7.60f, 16.40f);
        for (int i = -1; i <= 1; i++)
            LaserRay(ctx, brush, tip, 135f + i * 30f, 3.4f, 2.4f, 1.4f);
        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的平行四边形图标**（Fluent 里没有这个专名）。
    ///
    /// 形状＝引擎里真正画出来的那个：**底边水平、上边右移 1/4 宽**
    /// （见 <see cref="InkEngine.StrokeKind.Parallelogram"/>）——图标和它代表的图形
    /// 是同一个形状，老师不用二次翻译。
    ///
    /// 24 网格里的四个角（左下 → 右下 → 右上 → 左上）：
    /// 底边 x 2→18、上边 x 6→22（右移 4 ＝ 边长的 1/4）、y 6→18。
    /// 线宽 1.5 ＝ 上游 regular 图标那一套（正圆的环厚就是 1.5），
    /// 线头圆角用 <see cref="_round"/>：四个角看起来是小小的圆角，
    /// 和 Fluent 的方块（角半径 2.5）摆在一起不会显得一个是方的一个是圆的。
    /// </summary>
    private static void DrawParallelogram(ID2D1DeviceContext ctx, float x, float y,
                                        float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        var bl = new Vector2(2f, 18f);      // 左下
        var br = new Vector2(18f, 18f);     // 右下
        var tr = new Vector2(22f, 6f);      // 右上（相对左上右移 4 = 1/4 边长）
        var tl = new Vector2(6f, 6f);       // 左上
        ctx.DrawLine(bl, br, brush, 1.5f, _round);
        ctx.DrawLine(br, tr, brush, 1.5f, _round);
        ctx.DrawLine(tr, tl, brush, 1.5f, _round);
        ctx.DrawLine(tl, bl, brush, 1.5f, _round);

        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的椭圆图标**。Fluent 那套里只有正圆（Circle）——而"椭圆"和"圆"在
    /// 上带上是相邻的两段，两个都画正圆的话，老师只能靠左右位置猜哪个是哪个。
    ///
    /// 尺寸是**从上游那个正圆量出来的**，所以并排看是一家的：
    /// 正圆是外径 20（半径 10）、环厚 1.5（内径 17）的环；这里横向不动、纵向压扁，
    /// 画成"中心线 rx 9.25 / ry 6.25、线宽 1.5"的椭圆——外沿正好还是 20×14，
    /// 厚度也是 1.5。宽度保持 20 是故意的：椭圆的"宽"和圆的"直径"一样宽，
    /// 只差在高度上，一眼就知道这两个是一对。
    /// </summary>
    private static void DrawOval(ID2D1DeviceContext ctx, float x, float y,
                                float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        ctx.DrawEllipse(new Ellipse(new Vector2(12f, 12f), 9.25f, 6.25f), brush, 1.5f);

        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的坐标系图标**（2026-09-19 第三批）：带箭头的十字轴。
    ///
    /// 和"数轴"那个图标**必须一眼分得开**：两个都是一条带箭头的线的话，
    /// 抽屉里那两行就成了"两个一样的图标"。所以这里画**两条轴**（横＋竖），
    /// 数轴只画一条横的——差别就是"几条轴"，不用看细节。
    ///
    /// 24 网格里的摆法：原点 (8.5, 16.5) 偏左下（第一象限大一点，正是老师最常用的画法），
    /// x 轴伸到 21、y 轴伸到 2.8；箭头画成 V 形两条短线，和画布上那两个箭头同一个套路。
    /// 线宽 1.5、圆头圆角——和上游 Fluent 那一套（正圆环厚 1.5）混在一排里看不出是谁画的。
    ///
    /// 2026-09-19 用户报"图形里面坐标系的图标是带刻度线的"之后**去掉了两条刻度**：
    /// 画布上的坐标系同一天已经定了"不画刻度"（见 StrokeKind.Coordinate），
    /// 图标还留着刻度就是**画给用户看一个不存在的东西**。
    /// </summary>
    private static void DrawAxes(ID2D1DeviceContext ctx, float x, float y,
                                 float size, ID2D1Brush brush, bool grid = false)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        // **格线先画、轴线压在上面**（2026-09-24：面板那一格现在"再点一次"换要不要网格，
        // 图标得跟着换——理由和抛物线 / 直线那几格一样）。
        // 格线用 **0.9 的细线**（轴线 1.5）：图标是单色的，"淡"只能靠"细"表达，
        // 这正好和画布上那条"网格比轴线细一半"（Stroke.AxisGridWidthFactor）对上。
        if (grid)
        {
            for (float gx = 5f; gx <= 19f; gx += 3.5f)
                ctx.DrawLine(new Vector2(gx, 3.2f), new Vector2(gx, 20.8f), brush, 0.9f, _round);
            for (float gy = 6f; gy <= 20f; gy += 3.5f)
                ctx.DrawLine(new Vector2(2.6f, gy), new Vector2(20.6f, gy), brush, 0.9f, _round);
        }

        // 两条轴线
        ctx.DrawLine(new Vector2(2.5f, 16.5f), new Vector2(19.5f, 16.5f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(8.5f, 21.5f), new Vector2(8.5f, 4.5f), brush, 1.5f, _round);

        // 两个箭头（V 形各两条，尖端在轴的末端）
        ctx.DrawLine(new Vector2(17.4f, 14.6f), new Vector2(21f, 16.5f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(21f, 16.5f), new Vector2(17.4f, 18.4f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(6.6f, 6.2f), new Vector2(8.5f, 2.8f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(8.5f, 2.8f), new Vector2(10.4f, 6.2f), brush, 1.5f, _round);

        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的数轴图标**：一条带箭头的水平线 ＋ 三根刻度。
    ///
    /// 只画一条轴——这就是它和 <see cref="DrawAxes"/> 的差别，也是老师眼里
    /// "数轴"和"坐标系"的差别本身。刻度**骑在线上**（上下各出一截），
    /// 和画布上数轴的画法一致（见 Stroke.BuildAxes）。
    /// </summary>
    private static void DrawNumberLine(ID2D1DeviceContext ctx, float x, float y,
                                       float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        ctx.DrawLine(new Vector2(2.5f, 12f), new Vector2(19f, 12f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(17.2f, 10.1f), new Vector2(21.2f, 12f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(21.2f, 12f), new Vector2(17.2f, 13.9f), brush, 1.5f, _round);

        ctx.DrawLine(new Vector2(6f, 9.4f), new Vector2(6f, 14.6f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(11f, 9.4f), new Vector2(11f, 14.6f), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(16f, 9.4f), new Vector2(16f, 14.6f), brush, 1.5f, _round);

        ctx.Transform = saved;
    }

    // =====================================================================
    //  四种曲线的图标（2026-09-20 第五批，全部自绘）
    //
    //  三张都是"把画布上那个形状缩到 24 网格里"：抛物线是那一支 ∪、双曲线是那两支、
    //  正弦余弦是那一个周期。**尺寸口径和上游 regular 图标一致**（线宽 1.5、圆头圆角、
    //  留边 3 左右），混在一排里看不出是谁画的——这一条照 DrawParallelogram / DrawOval
    //  那两个先例。
    //  图的形状**刻意画成"理想样子"**（抛物线的顶点在正中、双曲线的 a = b），
    //  而不是某个具体对象的实时样子：图标要一眼认得出是哪一种图形。
    //  （"操作条上那一格"画的是**当前朝向**，那是另一回事，见 Overlay.DrawCurveAxisGlyph。）
    // =====================================================================

    /// <summary>
    /// 抛物线图标（"开口向上"那一张）。<paramref name="deg"/> 是**开口方向的旋转角**：
    /// 0 = 向上、90 = 向右、180 = 向下、−90 = 向左（见 `Draw` 里的四个名字）。
    /// </summary>
    private static void DrawParabola(ID2D1DeviceContext ctx, float x, float y,
                                     float size, ID2D1Brush brush, float deg)
    {
        var saved = ctx.Transform;
        // 顺序（System.Numerics 约定：先作用左边）：绕**图标自己 24 网格的中心** (12,12) 转，
        // 再缩放到目标框、平移到目标位置。绕 (0,0) 转会画到框外。
        ctx.Transform = Matrix3x2.CreateRotation(deg * MathF.PI / 180f, new Vector2(12f, 12f))
                      * Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        // 顶点 (12, 19)、两臂到 (3, 6) / (21, 6)：就是 x = 12 ± 9t、y = 19 − 13t²，
        // 取 9 个点连成折线。点数够密，缩到 18 像素也看不出是折线。
        var prev = new Vector2(12f, 19f);
        for (int i = 1; i <= 4; i++)
        {
            float t = i / 4f;
            var q = new Vector2(12f + 9f * t, 19f - 13f * t * t);
            ctx.DrawLine(prev, q, brush, 1.5f, _round);
            prev = q;
        }
        prev = new Vector2(12f, 19f);
        for (int i = 1; i <= 4; i++)
        {
            float t = i / 4f;
            var q = new Vector2(12f - 9f * t, 19f - 13f * t * t);
            ctx.DrawLine(prev, q, brush, 1.5f, _round);
            prev = q;
        }

        // **对称轴上的双向箭头**（用户 2026-09-20 定）：
        // 面板这一格只回答"上下"还是"左右"，**具体朝上还是朝下由那一拖定**
        //（见 Stroke.ParabolaAxisOfDrag）——所以图标要说出"这一档两个方向都行"，
        // 只画一根开口向上的曲线看不出这件事。
        //
        // 画在抛物线**里面那块空白**里：抛物线的内部正好是 x = 12 那条竖线上
        // 从顶点 (12,19) 往上的一段（外面那条曲线一个点都不占那里，所以不会搅在一起）。
        // 线宽 1.1 比曲线细（曲线 1.5）：它是**注释**，不能看着像曲线的一部分；
        // 两个箭头各两笔，加最后一段枪尖，缩到 18 像素刚好读得出是个"双向"。
        const float aw = 1.1f;
        ctx.DrawLine(new Vector2(12f, 7f), new Vector2(12f, 17.5f), brush, aw, _round);
        ctx.DrawLine(new Vector2(10.4f, 8.9f), new Vector2(12f, 7f), brush, aw, _round);
        ctx.DrawLine(new Vector2(13.6f, 8.9f), new Vector2(12f, 7f), brush, aw, _round);
        ctx.DrawLine(new Vector2(10.4f, 15.6f), new Vector2(12f, 17.5f), brush, aw, _round);
        ctx.DrawLine(new Vector2(13.6f, 15.6f), new Vector2(12f, 17.5f), brush, aw, _round);

        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的双曲线图标：左右两支（对应"实轴沿 x"那一档）＋（可选）**两条渐近线**。
    ///
    /// 参数方程就是画布上那一份 `x = ±a·cosh t、y = b·sinh t`（a = b = 4）——
    /// 两条外向的弧，中间留白，一眼和抛物线分得开（抛物线只有一支、也没有中间的空）。
    ///
    /// **2026-09-22 加了 `asymptotes` 这一档**（用户："图标就按照有渐近线和无渐近线"）：
    /// 面板那一格有两档（画不画那两条虚线），图标必须能分开这两档。
    /// 两条斜线画成**细的直线**（1.0，比曲线那 1.5 细）：图标只有 18 像素，
    /// 画成虚线会糊成一团，而"比曲线细"这一点在屏幕上就够读成"这是辅助线"了。
    /// 它们交于图标中心、斜率照 24 网格里那对 4 / 4 的框（和曲线同一个 a = b）。
    /// </summary>
    private static void DrawHyperbola(ID2D1DeviceContext ctx, float x, float y,
                                      float size, ID2D1Brush brush, bool asymptotes = true)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float tMax = 1.3f;
        const int seg = 8;
        // 渐近线先画（在曲线下面），两条都过中心 (12, 12)，斜率的绝对值 = 1（a = b）。
        if (asymptotes)
        {
            ctx.DrawLine(new Vector2(12f - 9f, 12f - 9f), new Vector2(12f + 9f, 12f + 9f),
                         brush, 1f, _round);
            ctx.DrawLine(new Vector2(12f - 9f, 12f + 9f), new Vector2(12f + 9f, 12f - 9f),
                         brush, 1f, _round);
        }
        for (int branch = 0; branch < 2; branch++)
        {
            var prev = Vector2.Zero;
            for (int i = 0; i <= seg; i++)
            {
                float t = -tMax + 2f * tMax * i / seg;
                float ch = MathF.Cosh(t) * 4f, sh = MathF.Sinh(t) * 4f;
                var q = new Vector2(12f + (branch == 0 ? ch : -ch), 12f + sh);
                if (i > 0) ctx.DrawLine(prev, q, brush, 1.5f, _round);
                prev = q;
            }
        }

        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的「椭圆（带焦点）」图标**（2026-09-22）：椭圆 ＋ 两个焦点（小点）
    /// ＋（`triangle` 为真时）焦点三角形。
    ///
    /// 摆法照画布上的几何（24 网格里取 a = 9.25、b = 6.25 → c = √(a²−b²) ≈ 6.82）：
    ///   · 两个焦点在长轴（水平）上，画成**实心小点**（半径 1.6 的小圆涂实）；
    ///   · 焦点三角形的顶点 P **落在椭圆上**（参数角 −120° = 左上方，和画布上那个默认角
    ///     **同一个数**：`ShapeSpec.FocusPointDefaultU`）——用户 2026-09-22 要的就是
    ///     "那个顶点弄在椭圆上面"：P 贴着椭圆那条线，一眼看出它在这个椭圆上；
    ///   · 椭圆本体沿用 <see cref="DrawOval"/> 那对半轴（9.25 / 6.25）——和「椭圆」那一格
    ///     摆在一起时"看得出是同一个椭圆、多了焦点"。
    ///
    /// 两档的差别就是**连不连那两条边**：不连时只剩两个点（用户 2026-09-22 定的口径：
    /// "还是画焦点，只是不连三角形"）。
    ///
    /// ⚠ 顶点为什么**不取正上方**（`u = −π/2`）：正上方那个点正好落在椭圆的**上端点手柄**
    /// 上（见 `ShapeHandle.AxisTop`）——画布上 P 会把手柄抢走。图标和画布**共用同一个角**
    /// （`ShapeSpec.FocusPointDefaultU`），所以图标也用左上方那个点。
    /// </summary>
    private static void DrawOvalFocus(ID2D1DeviceContext ctx, float x, float y,
                                      float size, ID2D1Brush brush, bool triangle)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float cx = 12f, cy = 12f, rx = 9.25f, ry = 6.25f;
        const float c = 6.82f;                   // √(9.25² − 6.25²)
        var f1 = new Vector2(cx - c, cy);
        var f2 = new Vector2(cx + c, cy);
        // P：椭圆上"参数角 120°"那个点（＝画布上焦点三角形顶点 P 的默认位置，
        //    见 ShapeSpec.FocusPointDefaultU——**两边同一个数**）。
        var p = new Vector2(cx + rx * MathF.Cos(ShapeSpec.FocusPointDefaultU),
                            cy + ry * MathF.Sin(ShapeSpec.FocusPointDefaultU));

        ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), brush, 1.5f);
        if (triangle)
        {
            ctx.DrawLine(f1, p, brush, 1.5f, _round);
            ctx.DrawLine(p, f2, brush, 1.5f, _round);
        }
        ctx.FillEllipse(new Ellipse(f1, 1.6f, 1.6f), brush);
        ctx.FillEllipse(new Ellipse(f2, 1.6f, 1.6f), brush);

        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的**圆柱**图标（2026-09-20 第五批：立体图形）。
    ///
    /// 和画布上画出来的一样：顶面整圈、底面只画看得见的下半圈、两条母线。
    /// 被挡住的上半圈在本体里是细虚线（辅助几何槽），图标太小就不画那一笔了。
    /// </summary>
    private static void DrawCylinder(ID2D1DeviceContext ctx, float x, float y,
                                     float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float cx = 12f, topCy = 6.5f, botCy = 17.5f, rx = 7f, ry = 2.6f;
        DrawEllipseArc(ctx, new Vector2(cx, topCy), rx, ry, 0f, 1f, brush);
        DrawEllipseArc(ctx, new Vector2(cx, botCy), rx, ry, 0f, 0.5f, brush);   // 下半圈
        ctx.DrawLine(new Vector2(cx - rx, topCy), new Vector2(cx - rx, botCy), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(cx + rx, topCy), new Vector2(cx + rx, botCy), brush, 1.5f, _round);

        ctx.Transform = saved;
    }

    /// <summary>自绘的**圆锥**图标：底面下半圈 ＋ 两条母线连到顶点（顶点在上边中点）。</summary>
    private static void DrawCone(ID2D1DeviceContext ctx, float x, float y,
                                 float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float cx = 12f, botCy = 17f, rx = 7f, ry = 2.6f;
        var apex = new Vector2(cx, 4.5f);
        DrawEllipseArc(ctx, new Vector2(cx, botCy), rx, ry, 0f, 0.5f, brush);
        ctx.DrawLine(new Vector2(cx - rx, botCy), apex, brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(cx + rx, botCy), apex, brush, 1.5f, _round);

        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的**圆台**图标：下底下半圈 ＋ 上底整圈（**小一圈**）＋ 两条往中间收的母线。
    ///
    /// 上底的比例读 <see cref="ShapeSpec.FrustumTopScale"/>——和画布上**同一个数**，
    /// 也和三兄弟里那个「棱台」同一个数（两个台体挨着放，收法该是一样的）。
    /// 比例写死的话，图标和画出来的东西就不是一回事（这一族最容易出的错）。
    /// </summary>
    private static void DrawConeFrustum(ID2D1DeviceContext ctx, float x, float y,
                                        float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float cx = 12f, botCy = 17f, rx = 7f, ry = 2.6f;
        float k = ShapeSpec.FrustumTopScale;
        float trx = rx * k, try_ = ry * k;
        // 上底圆心**从上边往里缩 try_**（不是缩 ry）——于是上底正好与图标上边相切，
        // 和圆柱 / 圆锥那两张同一个口径（画出来的东西刚好占满那一格）。
        var top = new Vector2(cx, 4.5f + try_);
        DrawEllipseArc(ctx, top, trx, try_, 0f, 1f, brush);                      // 上底整圈
        DrawEllipseArc(ctx, new Vector2(cx, botCy), rx, ry, 0f, 0.5f, brush);    // 下底下半圈
        ctx.DrawLine(new Vector2(cx - rx, botCy), new Vector2(top.X - trx, top.Y), brush, 1.5f, _round);
        ctx.DrawLine(new Vector2(cx + rx, botCy), new Vector2(top.X + trx, top.Y), brush, 1.5f, _round);

        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的**球**图标：**轮廓圆 ＋ 赤道椭圆**（只画看得见的那半圈，和别的立体同一个口径）。
    ///
    /// 为什么非要那个椭圆：光一个圆，18 像素下和「圆」那一格**完全一样**——
    /// 赤道才是"这是个球"的唯一线索。
    /// 扁率读 <see cref="ShapeSpec.SolidEllipseRatio"/>——**和画布上是同一个数**
    ///（那是"从上往下看"的俯角，图标要是不跟着，看着会比画出来的"躺得更平"）。
    /// </summary>
    private static void DrawSphere(ID2D1DeviceContext ctx, float x, float y,
                                   float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float c = 12f, r = 7.5f;
        float eqRy = r * ShapeSpec.SolidEllipseRatio;
        DrawEllipseArc(ctx, new Vector2(c, c), r, r, 0f, 1f, brush);              // 轮廓整圆
        DrawEllipseArc(ctx, new Vector2(c, c), r, eqRy, 0f, 0.5f, brush);         // 赤道近侧

        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的**长方体**图标：正面矩形 ＋ 往后上方 45° 退出来的背面（和他一样，
    /// 被挡住的那三条棱本来就是虚线，图标太小就只画看得见的部分）。
    /// </summary>
    private static void DrawCuboid(ID2D1DeviceContext ctx, float x, float y,
                                   float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float x0 = 4f, y0 = 9f, x1 = 16f, y1 = 20f, d = 4f;
        void Line(float ax, float ay, float bx, float by)
            => ctx.DrawLine(new Vector2(ax, ay), new Vector2(bx, by), brush, 1.5f, _round);
        // 正面
        Line(x0, y0, x1, y0); Line(x1, y0, x1, y1); Line(x1, y1, x0, y1); Line(x0, y1, x0, y0);
        // 背面（上横 ＋ 右竖）
        Line(x0 + d, y0 - d, x1 + d, y0 - d); Line(x1 + d, y0 - d, x1 + d, y1 - d);
        // 三条看得见的斜棱
        Line(x0, y0, x0 + d, y0 - d); Line(x1, y0, x1 + d, y0 - d); Line(x1, y1, x1 + d, y1 - d);

        ctx.Transform = saved;
    }

    /// <summary>自绘的**四面体**图标：底面三角形 ＋ 顶点与三条棱（照他，全是实线）。</summary>
    private static void DrawTetrahedron(ID2D1DeviceContext ctx, float x, float y,
                                        float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        var bl = new Vector2(3.5f, 19f);
        var br = new Vector2(20.5f, 19f);
        var bt = new Vector2(12f, 12.5f);
        var apex = new Vector2(13.5f, 3.5f);
        void Line(Vector2 a, Vector2 b) => ctx.DrawLine(a, b, brush, 1.5f, _round);
        Line(bl, br); Line(br, bt); Line(bt, bl);      // 底面
        Line(bl, apex); Line(br, apex); Line(bt, apex); // 三条棱

        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的**棱柱**图标（三 / 四 / 五 / 六棱柱）：**立着**的底面正 n 边形 ＋ 顶面 ＋ 侧棱。
    ///
    /// 判据和画布上那份**是同一条**（计划-图形工具.md §32.4 / §34）：底面中点在底心下方 = 近侧；
    /// 侧棱只要相邻两个侧面里有一个看得见就画。**只画看得见的棱**——被挡住的那些在 18 像素下
    /// 就是一团虚线，反而看不出形（和 <see cref="DrawCuboid"/> 同一个口径）。
    ///
    /// ⚠ 五 / 六两张在 18 像素下**不太分得开**（只差底面上一条边）——那一格右边有
    /// **4 个档位点**兜底（见 FullUi 的 DrawPips）：数点比数边可靠。
    /// </summary>
    private static void DrawPrism(ID2D1DeviceContext ctx, float x, float y,
                                  float size, ID2D1Brush brush, int sides)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        var bc = new Vector2(12f, 17f);
        const float rx = 8f, ry = 3.4f;      // 底面是"俯视压扁"的，和画布上那个观感一致
        const float height = 11f;
        var b = IconBasePoints(bc, rx, ry, sides);
        var top = new Vector2[sides];
        for (int k = 0; k < sides; k++) top[k] = new Vector2(b[k].X, b[k].Y - height);

        DrawRingSolid(ctx, b, top, hasTopRing: true, rx, ry, brush);
        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的**棱锥**图标：底面正 n 边形 ＋ 一个顶点（n 条棱收到一点）。
    /// 和 <see cref="DrawPrism"/> 共用底面与可见性判据，只把"顶面"换成**一个点**。
    ///
    /// ⚠ **只有三棱锥画完整线框**（藏起来的棱也画），这是用户 2026-09-20 看出来的
    ///（"三棱锥图标看着不对"，见 `--shapeiconshow` 那张对照表）：
    /// 三棱锥一共 6 条棱，按"只画看得见的"筛完只剩 **3 条**（前面那条底边 ＋ 两条侧棱），
    /// 而这三条画出来**正好是一个平面正三角形**——和「三角形」那一格**一模一样**，
    /// 完全看不出是个立体。补上藏起来的那 3 条（另外两条底边 ＋ 后面那条侧棱）就立起来了。
    ///
    /// **四棱以上不补**：它们的可见棱里至少还有**两条底边**（前后各一条），
    /// 形状立得住；补成全线框反而在 18 像素下发糊（出图比过，2026-09-20）。
    /// </summary>
    private static void DrawPyramid(ID2D1DeviceContext ctx, float x, float y,
                                    float size, ID2D1Brush brush, int sides)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        var bc = new Vector2(12f, 18.5f);
        const float rx = 8f, ry = 3.4f;
        var b = IconBasePoints(bc, rx, ry, sides);
        var apex = new Vector2(12f, 4f);                 // 顶点（图标里就立在底心正上方）
        var top = new Vector2[sides];
        for (int k = 0; k < sides; k++) top[k] = apex;   // 顶面退化成一个点

        DrawRingSolid(ctx, b, top, hasTopRing: false, rx, ry, brush, fullWire: sides == 3);
        ctx.Transform = saved;
    }

    /// <summary>
    /// 自绘的**棱台**图标：底面正 n 边形 ＋ 一个**缩小**的同形上底 ＋ n 条收进去的侧棱。
    /// 上底比例读 <see cref="ShapeSpec.FrustumTopScale"/>——和画布上**同一个数**，
    /// 不然图标和画出来的东西不一样（那是这一族最容易出的错）。
    /// </summary>
    private static void DrawFrustum(ID2D1DeviceContext ctx, float x, float y,
                                    float size, ID2D1Brush brush, int sides)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        var bc = new Vector2(12f, 18.5f);
        const float rx = 8f, ry = 3.4f;
        var b = IconBasePoints(bc, rx, ry, sides);
        var apex = new Vector2(12f, 6f);                 // 上底中心（和棱柱那个"顶面中心"同一含义）
        float k0 = ShapeSpec.FrustumTopScale;
        var top = new Vector2[sides];
        for (int k = 0; k < sides; k++) top[k] = apex + (b[k] - bc) * k0;

        DrawRingSolid(ctx, b, top, hasTopRing: true, rx, ry, brush);
        ctx.Transform = saved;
    }

    /// <summary>
    /// 图标里那 `n` 个底面顶点：**和画布上同一套算式**（`ShapeSpec.PrismBasePointOffset`：
    /// 压扁 ＋ 错切，起始角也是同一个判据）——两边不一致的话，图标画的和画出来的
    /// 就不是一个东西。三个立体图标（<see cref="DrawPrism"/> / <see cref="DrawPyramid"/> /
    /// <see cref="DrawFrustum"/>）都从它出发。
    /// </summary>
    private static Vector2[] IconBasePoints(Vector2 bc, float rx, float ry, int n)
    {
        var b = new Vector2[n];
        for (int k = 0; k < n; k++)
        {
            float a = (ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f) + k * MathF.Tau / n;
            b[k] = bc + ShapeSpec.PrismBasePointOffset(MathF.Cos(a), MathF.Sin(a), rx, ry);
        }
        return b;
    }

    /// <summary>
    /// 把"底面 ＋ 顶面"这两圈点画成一个立体图标（三个立体的共用收尾）。
    ///
    /// 每个侧面看得见吗：**和画布上同一个判据**（`ShapeSpec.PrismFaceVisible`，
    /// 连错切那一项都算进去——漏了它，四棱柱的图标会少画右侧那一面）。
    /// `hasTopRing = false` 是棱锥（顶面只是一个点，画"面"就成了墨疙瘩）。
    ///
    /// `fullWire`：**画完整线框**（藏起来的棱也画）。只有**棱锥**需要它，理由见 DrawPyramid。
    /// </summary>
    private static void DrawRingSolid(ID2D1DeviceContext ctx, Vector2[] b, Vector2[] top,
                                      bool hasTopRing, float rx, float ry, ID2D1Brush brush,
                                      bool fullWire = false)
    {
        int n = b.Length;
        void Line(Vector2 p, Vector2 q) => ctx.DrawLine(p, q, brush, 1.5f, _round);

        float ang0 = ShapeSpec.PrismBaseOffsetDegrees(n) * MathF.PI / 180f;
        var face = new bool[n];
        for (int k = 0; k < n; k++)
        {
            float mid = ang0 + (k + 0.5f) * MathF.Tau / n;
            face[k] = ShapeSpec.PrismFaceVisible(MathF.Cos(mid), MathF.Sin(mid), rx, ry);
        }

        for (int k = 0; k < n; k++)
        {
            int nx = (k + 1) % n;
            if (hasTopRing) Line(top[k], top[nx]);    // 顶面：俯视下恒可见
            if (fullWire || face[k]) Line(b[k], b[nx]);           // 底面
            // 侧棱：相邻两个侧面有一个看得见就画（棱锥时 top[k] 就是顶点）
            if (fullWire || face[k] || face[(k + n - 1) % n]) Line(b[k], top[k]);
        }
    }

    /// <summary>图标里画一段椭圆弧（`u0 → u1`，和画布上那套同一个角度约定）。</summary>
    private static void DrawEllipseArc(ID2D1DeviceContext ctx, Vector2 c, float rx, float ry,
                                       float u0, float u1, ID2D1Brush brush)
    {
        const int n = 16;
        var prev = EllipseAt(c, rx, ry, u0);
        for (int i = 1; i <= n; i++)
        {
            var q = EllipseAt(c, rx, ry, u0 + (u1 - u0) * i / n);
            ctx.DrawLine(prev, q, brush, 1.5f, _round);
            prev = q;
        }
    }

    /// <summary>`u ∈ [0,1)`：0 = 右、0.25 = 下、0.5 = 左、0.75 = 上（屏幕 y 向下）。</summary>
    private static Vector2 EllipseAt(Vector2 c, float rx, float ry, float u)
    {
        float a = u * MathF.Tau;
        return new Vector2(c.X + rx * MathF.Cos(a), c.Y + ry * MathF.Sin(a));
    }

    /// <summary>
    /// 自绘的正弦 / 余弦图标：**一个周期**（和画布上画出来的完全一样：
    /// 正弦从轴起、余弦从峰起——这正是这两个工具的区别，图标也要说清）。
    ///
    /// 中线 y = 12、振幅 6、一个周期横向铺满 3→21。
    /// </summary>
    private static void DrawWave(ID2D1DeviceContext ctx, float x, float y,
                                 float size, ID2D1Brush brush, bool cosine)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const int seg = 16;
        var prev = Vector2.Zero;
        for (int i = 0; i <= seg; i++)
        {
            float u = i / (float)seg;
            double phase = u * Math.Tau;
            float v = cosine ? (float)Math.Cos(phase) : (float)Math.Sin(phase);
            var q = new Vector2(3f + 18f * u, 12f - 6f * v);
            if (i > 0) ctx.DrawLine(prev, q, brush, 1.5f, _round);
            prev = q;
        }

        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的波浪线图标**：**四个周期**的正弦波（用户 2026-09-20 要的"很多个周期的波浪线"）。
    ///
    /// 和「正弦」那一张（`DrawWave`：**一个周期**、铺满整格）**必须一眼分得开**——
    /// 这正是用户把这两件事分成两格的原因（"正弦和余弦用一个周期的图，还有一个另外的
    /// 很多周期的波浪的弦函数线"）。所以这一张走"**周期明显变小、个数明显变多**"：
    /// 同样铺满 3→21，周期 4.5（正弦那张是 18），振幅 3.2（正弦那张是 6）。
    ///
    /// ⚠ 图标是**示意**：画布上的真比例是"**一个周期 = 一个振幅**"（见 `Stroke.WavePeriodPerAmplitude`），
    /// 照那个比例在 24 格里挤不出"几个周期还看得出是波"的样子，所以这里画得略松一点
    ///（**4 个周期、振幅 3.2** → 比例约 1.4）。
    /// </summary>
    private static void DrawWaveLine(ID2D1DeviceContext ctx, float x, float y,
                                     float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const int seg = 48;                      // 四个周期共 48 段（每周期 12 段，够滑）
        const float amp = 3.2f;
        var prev = Vector2.Zero;
        for (int i = 0; i <= seg; i++)
        {
            float u = i / (float)seg;                       // 0 → 1 走完 3 → 21
            double ph = u * Math.Tau * 4.0;                 // 四个周期
            var q = new Vector2(3f + 18f * u, 12f - amp * (float)Math.Sin(ph));
            if (i > 0) ctx.DrawLine(prev, q, brush, 1.5f, _round);
            prev = q;
        }

        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的正切图标**：一支曲线 ＋ 两条**渐近线**（虚线）。
    ///
    /// 画的是 y = tan x 在 (−π/2, π/2) 上的那一支：过中点 (12,12)、左右各一条竖渐近线。
    ///
    /// ⚠ 它是**示意**（不是画布上那个等比例的形状）：真画布上"贴着渐近线"要求高度是宽度的
    /// 3 倍（见 `Stroke.TangentMinAspect`），24 格里放不下那种细高条，
    /// 所以这里把曲线**压扁着画**——两个端头都画到 θ = 0.9·(π/2)，也就是**贴着那两条虚线**为止。
    /// 那两条虚线是这个图形唯一的识别特征（少了它们，这一张会和「双曲线的一支」看不出区别）。
    /// </summary>
    private static void DrawTangent(ID2D1DeviceContext ctx, float x, float y,
                                    float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float hx = 4.5f;                   // 渐近线离中点多远（虚线就画在那儿）
        const float hy = 8.5f;                   // 曲线冲到多高（＝虚线的上下端）
        const float frac = 0.9f;                 // 画到 π/2 的 90%（再往外就贴着渐近线了）
        float thetaMax = frac * MathF.PI * 0.5f;
        float tanMax = MathF.Tan(thetaMax);

        const int seg = 20;
        var prev = new Vector2(12f - hx * frac, 12f + hy);
        for (int i = 1; i <= seg; i++)
        {
            float th = -thetaMax + 2f * thetaMax * i / seg;
            var q = new Vector2(12f + hx * (th / (MathF.PI * 0.5f)), 12f - hy * (MathF.Tan(th) / tanMax));
            ctx.DrawLine(prev, q, brush, 1.5f, _round);
            prev = q;
        }

        // 两条渐近线：细虚线，上下铺满（±hy 就是曲线的截断高度）。
        DashedLine(ctx, new Vector2(12f - hx, 12f - hy), new Vector2(12f - hx, 12f + hy), 2.4f, 2f, 1.2f, brush);
        DashedLine(ctx, new Vector2(12f + hx, 12f - hy), new Vector2(12f + hx, 12f + hy), 2.4f, 2f, 1.2f, brush);

        ctx.Transform = saved;
    }

    /// <summary>
    /// **自绘的激光笔图标**：笔 ＋ 光束 ＋ 落点。
    ///
    /// 为什么不用上游图标：Fluent 里没有"激光笔"这个专名（只有 Flash 闪电、
    /// Record 圆点这些近义）；Material Symbols 有专名但要引第二个图标库，
    /// 混库会让线宽和光学尺寸对不上。这三个元素照抄假面板里量好的坐标
    /// （24 网格：笔身 (17,4.6)→(12.4,9.2) 粗 3、光束 (11.6,10)→(9.4,12.2) 细 1.4、
    /// 落点圆心 (6.6,15) 半径 2.1），画出来和界面上其它 Fluent 图标是一套手感。
    ///
    /// **2026-09-26 加了 6 / 7 两档**（用户："我喜欢那种'笔射出一道光'的形态"）：
    /// 老那几档的问题出在**光束太短**（(11.6,10)→(9.4,12.2) 只有 3 格），
    /// 缩到 24 像素看不出"射出去"，只剩下"一支笔带个小点"。新的两档把光束拉长到
    /// 5～7 格，笔尖还专门收细一截（不然笔和光束长成一根，像是笔杆延长线）。
    /// 候选对照见 `--toolicons` 出的那张图（[ToolIconSheet]），选谁由眼睛定。
    ///
    /// <paramref name="outline"/> = **未选中那一档**（笔细一号、落点空心）：
    /// 和 Fluent 那批"regular 常态／filled 选中"是一套规矩——一排里只有它是实心的
    /// 就会显得像被填了色（用户 2026-09-17 报过，见 <see cref="LaserOutline"/>）。
    /// 老那几档（0～5）各有各的画法，这个开关只对新两档（6／7）生效。
    /// </summary>
    public static void DrawLaser(ID2D1DeviceContext ctx, RectF box, float size,
                                ID2D1Brush brush, ID2D1Brush softBrush = null,
                                int variant = LaserDefault, bool outline = false)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;

        switch (variant)
        {
            case 0:     // 第一版：笔 ＋ 细光束 ＋ 小落点（用户说"有点单薄"）
                ctx.DrawLine(new Vector2(17f, 4.6f), new Vector2(12.4f, 9.2f), brush, 3f, _round);
                ctx.DrawLine(new Vector2(11.6f, 10f), new Vector2(9.4f, 12.2f), brush, 1.4f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(6.6f, 15f), 2.1f, 2.1f), brush);
                break;

            case 1:     // 锥形光束（照假面板那一版）：一片 30% 的锥 ＋ 两条边 ＋ 大一点的落点
                if (Cone() != null) ctx.FillGeometry(Cone(), softBrush ?? brush);
                ctx.DrawLine(new Vector2(7.6f, 16.4f), new Vector2(20.5f, 3.5f), brush, 1.7f, _round);
                ctx.DrawLine(new Vector2(8.4f, 18.4f), new Vector2(21.5f, 13.5f), brush, 1.7f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(6f, 18f), 2.7f, 2.7f), brush);
                break;

            case 2:     // 加重版：笔更粗、光束更粗、落点更大
                ctx.DrawLine(new Vector2(17.5f, 4.2f), new Vector2(12.6f, 9.1f), brush, 3.6f, _round);
                ctx.DrawLine(new Vector2(11.8f, 9.9f), new Vector2(8.8f, 12.9f), brush, 2.4f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(7f, 14.8f), 2.9f, 2.9f), brush);
                break;

            case LaserOutline:  // 线条版：**没选中时用这个**（见下面 LaserOutline 的注释）
                ctx.DrawLine(new Vector2(17.6f, 4.4f), new Vector2(12.9f, 9.1f), brush, 1.8f, _round);
                ctx.DrawLine(new Vector2(12.0f, 10.0f), new Vector2(9.1f, 12.9f), brush, 1.6f, _round);
                ctx.DrawEllipse(new Ellipse(new Vector2(7.2f, 14.8f), 2.6f, 2.6f), brush, 1.8f);
                break;

            case 3:     // 锥形 ＋ 落点光环（落点外面再套一圈，像"正在打的那一点"）
                ctx.DrawLine(new Vector2(7.6f, 16.4f), new Vector2(20.5f, 3.5f), brush, 1.7f, _round);
                ctx.DrawLine(new Vector2(8.4f, 18.4f), new Vector2(21.5f, 13.5f), brush, 1.7f, _round);
                ctx.FillEllipse(new Ellipse(new Vector2(6f, 18f), 2.7f, 2.7f), brush);
                ctx.DrawEllipse(new Ellipse(new Vector2(6f, 18f), 4.3f, 4.3f), brush, 1.1f);
                break;

            // ---- 2026-09-26 定稿（用户挑的 A′）：**一支大笔 ＋ 两道明显断开的虚线 ＋ 一个点** ----
            // 走这条路之前试过六版（长直光束 / 楔形 / 实心锥 / 光柱 / 折角 / 照参考图那支
            // 带卡口的笔），用户都不满意；最后要的是："笔下面有一道很细的虚线，虚线下面
            // 有一个点，不用长，有激光射出去的感觉"，而且"不能太瘦弱单薄"。
            //
            // 实现的两条规矩（都是这轮踩出来的，改之前先看）：
            //   1. **笔要大**——用的是工具里那支 Fluent Pen（同一个形状！），放大到占整格
            //      约六成，笔尖落在 (9.3,15.3)；光只占左下角那一小段；
            //   2. **虚线的缝必须大于线粗**——圆头线帽会让每段两头各鼓出"线粗的一半"，
            //      缝 ≤ 线粗时相邻两段会接上、整段黏成一条实线（用户 2026-09-26 报的
            //      "看起来是连成一体的，不像是虚线"就是这个）。现在缝 2.6、线粗 1.05，
            //      露出来的可见断口约 1.6 像素，是线粗的 1.5 倍。
            case 16:
                LaserPenBeam(ctx, brush, outline);
                break;

            // ---- 2026-09-27：照 ClassIn 的"**笔头 ＋ 一个发光提示**" ----
            // 用户原话："你看 ClassIn 的这个激光笔，它就是一个笔头加了一个发光提示，
            // 看起来也挺形象的，而且图标也不奇怪，对不对？所以我们是不是也可以仿照他这种，
            // 在正常的笔头上加一个发光的，但是风格要是我们现在这种的风格。"
            // 即：**笔就用那支正常的笔（和笔工具同一支）**，把左下角那截"虚线 ＋ 点"
            // 换成**一小朵放射状的发光标记**（点 ＋ 几道短线），风格照旧（24 网格、圆头）。
            //
            // ⚠ 旁边那几支"不搭"的根子不在构图，在**线宽**（2026-09-27 第二轮才找到）：
            //   笔是按 0.68 缩的，路径里那 1.5 的线宽跟着缩成 **1.02**，
            //   而这一排别的图标全是 1.5 → 那支笔看着虚、细一档。
            //   修法见 <see cref="LaserPenNib"/>（填一遍再补描一圈，把线宽顶回 1.5）。
            case 17:
                LaserNibGlow(ctx, brush, outline, 3);
                break;

            case 18:
                LaserNibGlow(ctx, brush, outline, 4);
                break;

            // 19（2026-09-27 第二轮 · 方案 B）：**点 ＋ 一圈光波**——
            // 光不画成"放射的几道线"，画成围在亮点外面、**开口朝笔尖**的一段圆弧
            //（像水波那样一圈圈荡开）。为什么另给一种：ClassIn 那种"点 ＋ 几道射线"
            // 读起来更像"闪一下"，而这个更像"在发光"；两套摆一起让眼睛挑。
            case 19:
                LaserNibHalo(ctx, brush, outline);
                break;

            // 20（2026-09-27 第三轮 · 照 ClassIn 那张图）：**只画笔头 ＋ 三道光**。
            // 用户给了 ClassIn 工具栏的截图、指名要第三个图标那个样式——它和 17／19 的
            // 根本区别是：**没有整支笔，只有一颗笔头**（一个斜的宽笔头 ＋ 笔尖外三道光）。
            // 为什么这样反而更和谐：17 是"一支缩小的笔 ＋ 一点光"，那支笔缩到 0.68 之后
            // 笔画又细又碎（补了线宽之后仍然比整排单薄），而**一颗实心笔头**面积大、
            // 和旁边"笔 / 荧光笔 / 橡皮"那几格的份量看得齐（用户："这样是不是看起来会和谐很多"）。
            case 20:
                LaserNibPointer(ctx, brush, outline);
                break;

            // 21～24（2026-09-27 第四轮 · 用户自己点名的形态）：**钢笔尖 ＋ 三道杠**。
            // 用户把 ClassIn 那颗笔头的原图（放大）发过来，要求："和 ClassIn 一样，
            // 在**笔**下面加 3 杠表示光亮；笔的尺寸我们自己找一个合适的"。
            // 所以这一档把"笔头"画成**真正的钢笔尖**（圆背 ＋ 收颈 ＋ 一条缺口），
            // 再在外面配三道杠；**四个档号只差一个缩放**（1.00 / 0.92 / 0.84 / 0.76），
            // 就是给眼睛挑尺寸用的（见 <see cref="LaserNibFancy"/> 的 scale 参数）。
            case 21: LaserNibFancy(ctx, brush, outline, 1.00f); break;
            case 22: LaserNibFancy(ctx, brush, outline, 0.92f); break;
            case 23: LaserNibFancy(ctx, brush, outline, 0.84f); break;
            case 24: LaserNibFancy(ctx, brush, outline, 0.76f); break;

            default:    // "PowerPoint 那颗红点"：一个实心红点 ＋ 一圈很淡的光晕
                ctx.FillEllipse(new Ellipse(new Vector2(12f, 12f), 7.5f, 7.5f),
                                BrushOf(ctx, new Color4(0.95f, 0.18f, 0.18f, 0.22f)));
                ctx.FillEllipse(new Ellipse(new Vector2(12f, 12f), 3.2f, 3.2f),
                                BrushOf(ctx, new Color4(0.95f, 0.18f, 0.18f, 1f)));
                break;
        }

        ctx.Transform = saved;
    }

    /// <summary>
    /// **主界面那一格用的激光笔图标**：未选中画一张、选中画另一张
    /// （和 Fluent 那批"regular 常态／filled 选中"同一套规矩）。
    ///
    /// **现在是 Fluent 自家的 `Laser Tool`**（MIT；见 Icons.g.cs 的 laserTool / laserToolFilled）：
    /// 用户 2026-09-27 把上游那一批"带笔的"图标（14 支）摆成对照图挑出来的，
    /// 原话"把 `Laser Tool` 原样（不加杠），就使用这个吧"——
    /// 它本身就是"**一颗笔头 ＋ 笔尖外一圈短射线**"，和用户提供的 ClassIn 参考图
    /// **是同一个构成**，所以什么也不用再加（前一版自绘的 17～24 档都是为了凑这个构成）。
    ///
    /// ⚠ 它是 **20 网格**（上游只出了 20 像素版，没有 24 的）：`Draw` 会按 viewBox
    ///   `0 0 20 20` 缩放到目标尺寸，线宽也跟着放大 1.2 倍（≈1.6，和这一排的 1.5 一个量级）。
    ///
    /// **自绘的那几档（16～24）没有删**：`--panelshow <路径> [描边] [档号]` 里给第 3 个参数
    /// 就能把它们画回主条上出对照图（见 <see cref="DevLaserVariant"/>），`--toolicons` 那张
    /// 候选表也还摆着它们。
    /// </summary>
    public static void DrawLaserCell(ID2D1DeviceContext ctx, RectF box, float size,
                                     ID2D1Brush brush, bool active)
    {
        if (DevLaserVariant.HasValue)
        {
            DrawLaser(ctx, box, size, brush, null, DevLaserVariant.Value, outline: !active);
            return;
        }
        DrawCentered(ctx, active ? "laserToolFilled" : "laserTool", box, size, brush);
    }

    /// <summary>
    /// **开发期专用**（`--panelshow <路径> [描边宽] [激光档号]`）：把激光笔那一格强制成
    /// 自绘的那几档（16～24）里的一档，方便出"自绘 vs 上游"的对照图。
    /// null ＝ 产品现在那一支（Fluent 的 `Laser Tool`，见 <see cref="DrawLaserCell"/>）。
    /// </summary>
    internal static int? DevLaserVariant;

    /// <summary>
    /// 自检用：现在这一格画的是哪张激光笔图标——**和 <see cref="DrawLaserCell"/> 同一处判据**
    ///（那一处改了、这里没跟着改的话，自检就变成"测另一个东西"了，
    /// 所以 FullUi.CellIconForTest 直接问它，而不是自己另写一遍）。
    /// 返回的是"哪一支"的记号，不是盘里的图标名。
    /// </summary>
    internal static string LaserIconNameForTest(bool active)
    {
        if (DevLaserVariant.HasValue)
            return active ? $"laser{DevLaserVariant}" : $"laser{DevLaserVariant}-outline";
        return active ? "laserToolFilled" : "laserTool";
    }

    /// <summary>
    /// **激光笔图标**（档号 16，2026-09-26 用户挑的 A′）：一支**大笔**、两道**明显断开**
    /// 的虚线、一个点。
    ///
    /// 形状从哪来：笔＝**工具里那支 Fluent Pen（同一个形状）**，放大到占整格约六成
    /// （用户："就和第一个笔图标很像""不能太瘦弱单薄"）；光＝从笔尖朝左下出去的
    /// 两道短线 ＋ 落点（用户："笔下面有一道很细的虚线，虚线下面有一个点，不用长"）。
    ///
    /// 数字全部照候选图 A′ 那一版抄的（`--toolicons` 那张对照图里也留着）：
    ///   · 笔：`scale 0.68` ＋ `translate(7.702, 0.612)` → 笔尖正好落在 (9.3, 15.3)；
    ///   · 两道虚线：(8.946,15.654)→(8.133,16.467)、(6.295,18.305)→(5.482,19.118)，
    ///     线粗 1.05（选中 1.25），**缝 2.6**；
    ///   · 落点：圆心 (3.502,21.098)，半径 1.3（选中 1.55）。
    ///
    /// ⚠ **缝必须大于线粗**（这条别再改小）：圆头线帽会让每段两头各鼓出"线粗的一半"，
    /// 缝 ≤ 线粗时相邻两段会接上、整段黏成一条实线——用户 2026-09-26 报的
    /// "看起来是连成一体的，不像是虚线"就是这个原因（当时缝 0.55、线粗 1.05）。
    /// 现在缝 2.6 是线粗的 2.5 倍，露出来的可见断口约 1.6 个屏幕像素。
    ///
    /// 两态：未选中＝Fluent Pen 的 **regular**（线描那版，填起来是空心笔）、
    /// 选中＝**filled**（实心笔）——和整排 Fluent 图标一个规矩。
    /// </summary>
    private static void LaserPenBeam(ID2D1DeviceContext ctx, ID2D1Brush brush, bool outline)
    {
        // ① 那支笔：直接用上游的路径，不自己描（形状要和工具里那支**一模一样**）
        var pen = SvgPath.Get(outline ? PanelIcons.pen : PanelIcons.penFilled);
        if (pen != null)
        {
            var saved = ctx.Transform;
            ctx.Transform = Matrix3x2.CreateScale(0.68f)
                          * Matrix3x2.CreateTranslation(7.702f, 0.612f)
                          * saved;
            ctx.FillGeometry(pen, brush);
            ctx.Transform = saved;
        }

        // ② 那道光：两道短线（缝 2.6 ＝ 线粗的 2.5 倍，见上面那条 ⚠）
        float w = outline ? 1.05f : 1.25f;
        ctx.DrawLine(new Vector2(8.946f, 15.654f), new Vector2(8.133f, 16.467f), brush, w, _round);
        ctx.DrawLine(new Vector2(6.295f, 18.305f), new Vector2(5.482f, 19.118f), brush, w, _round);

        // ③ 落点：线末那个点（和最后一道之间留出约 1 像素的断口，别贴上）
        float r = outline ? 1.3f : 1.55f;
        ctx.FillEllipse(new Ellipse(new Vector2(3.502f, 21.098f), r, r), brush);
    }

    /// <summary>
    /// **激光笔图标里那支笔**（17／18／19 共用）。
    ///
    /// 形状＝**和笔工具同一支**（上游 Fluent Pen 的路径，两态分别用 regular / filled，
    /// 和 <see cref="LaserPenBeam"/> 同一个规矩），缩到 <paramref name="scale"/>、
    /// 摆到"**笔尖**落在 (tipX, tipY)"的位置——`pen` 这条路径的笔尖在 24 网格的
    /// (2.35, 21.6)（由档号 16 那组 `scale 0.68 ＋ translate(7.702,0.612)` 反推出来的）。
    ///
    /// ⚠ **缩小之后必须把线宽补回来**（2026-09-27 第二轮找到的根子）：路径里本来就带着
    /// 1.5 的线宽，按 0.68 缩完只剩 **1.02**，而这一排别的图标全是 1.5 ——
    /// 摆在一起那支笔就是虚的、细一档（用户："做了好几个都不大搭"有一半是这个）。
    /// 补法同 <see cref="EmboldenIcons"/>：**填一遍，再沿路径边界补描一圈**
    /// `1.5 × (1 − scale)` 宽——描边两边各摊一半、内侧那半和填充重叠，
    /// 于是总粗细正好回到 1.5，而**形状一个坐标都没动**。
    /// </summary>
    private static void LaserPenNib(ID2D1DeviceContext ctx, ID2D1Brush brush, bool outline,
                                    float scale, float tipX, float tipY)
    {
        var pen = SvgPath.Get(outline ? PanelIcons.pen : PanelIcons.penFilled);
        if (pen == null) return;
        const float nibX = 2.35f, nibY = 21.6f;      // 上游 pen 的笔尖（24 网格）
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(scale)
                      * Matrix3x2.CreateTranslation(tipX - nibX * scale, tipY - nibY * scale)
                      * saved;
        ctx.FillGeometry(pen, brush);
        ctx.DrawGeometry(pen, brush, 1.5f * (1f - scale), _round);
        ctx.Transform = saved;
    }

    /// <summary>
    /// **激光笔图标的"笔头 ＋ 发光提示"版**（档号 17＝三道射线、18＝四道射线，2026-09-27）。
    ///
    /// 照的是 ClassIn 那一格：用户看它"就是一个笔头加了一个发光提示，看起来也挺形象的，
    /// 而且图标也不奇怪"，要我们参考这个**构成**、但**风格保持现在这套**。
    ///
    /// 构成拆开就两件：
    ///   ① **一支正常的笔**——见 <see cref="LaserPenNib"/>（scale 0.68，笔尖在 (9.3,15.3)）；
    ///   ② **笔尖外一点点那个亮点 ＋ 一圈短射线**——"正在发光"的提示。
    ///      位置取"从笔尖沿笔轴（左下 45°）再走 2.4 格"；射线从亮点边上起、朝左下扇开
    ///      （<paramref name="rays"/> ＝ 3 时扇 50°，＝ 4 时扇 90°）。
    ///
    /// ⚠ **射线不能用圆头线帽贴到点上**：圆头会让每段两头各鼓出半个线粗（0.75），
    ///   贴上去就并成一个小疙瘩（和 A′ 那条"缝必须大于线粗"是同一个坑）。
    ///   所以起笔半径取"点半径 ＋ 1.2"，露出来的断口约 0.45 格。
    /// </summary>
    private static void LaserNibGlow(ID2D1DeviceContext ctx, ID2D1Brush brush, bool outline, int rays)
    {
        // ① 那支笔（线宽已经补回 1.5，见 LaserPenNib）
        LaserPenNib(ctx, brush, outline, 0.68f, 9.3f, 15.3f);

        // ② 发光提示：亮点 ＋ 四/三道短射线
        var c = new Vector2(7.60f, 17.00f);              // 亮点圆心（笔尖沿轴再走 2.4 格）
        float dotR = outline ? 1.00f : 1.15f;            // 选中那档点大一点
        const float w = 1.5f;                            // 射线粗细 ＝ 这一排的口径
        float rs = dotR + 1.2f;                          // 起笔半径（留出断口，见上面那条 ⚠）
        const float len = 1.85f;                         // 每道射线的长度

        // 方向：全是把"笔尖射出去的那个方向"转一个小角度得来的
        //（y 轴朝下的屏幕坐标里，左下 45° ＝ **135°**）
        float[] degs = rays == 4 ? new[] { 45f, 18f, -18f, -45f }
                                 : new[] { 25f, 0f, -25f };
        foreach (float deg in degs)
            LaserRay(ctx, brush, c, 135f + deg, rs, len, w);

        ctx.FillEllipse(new Ellipse(c, dotR, dotR), brush);
    }

    /// <summary>
    /// 从 <paramref name="c"/> 出发、朝 <paramref name="deg"/> 方向画**一道光**（圆头短线）。
    ///
    /// ⚠ 两头都有圆头线帽、各会鼓出"半个线粗"，所以 <paramref name="start"/> 必须**明显大于**
    ///   半个线粗，那道光和别的东西（亮点、笔头）之间才留得出断口——不然会粘成一个小疙瘩
    ///（A′ 那两道虚线、17 的三道射线、20 的三道光，三处踩的都是同一个坑）。
    ///
    /// 角度用**屏幕角**（x 向右、y 向下）：**135° 就是左下**，也就是笔尖射出去的方向。
    /// </summary>
    private static void LaserRay(ID2D1DeviceContext ctx, ID2D1Brush brush, Vector2 c,
                                 float deg, float start, float len, float w)
    {
        float rad = deg * MathF.PI / 180f;
        var d = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
        var a = c + d * start;
        ctx.DrawLine(a, a + d * len, brush, w, _round);
    }

    /// <summary>
    /// **激光笔图标：只画笔头 ＋ 三道光**（档号 20，2026-09-27 第三轮，照 ClassIn 的样式）。
    ///
    /// 用户把 ClassIn 工具栏的截图发过来、指名要"第三个小图标"那个样式：**一颗笔头，
    /// 左下角三道很短的光**——没有笔杆、没有笔帽。我们前几版（16～19）都是"一整支笔 ＋ 一点光"，
    /// 那支笔缩到 0.68 之后又细又碎，摆在"笔 / 荧光笔 / 橡皮"旁边明显单薄；
    /// **一颗实心笔头**面积大、份量够，才和旁边几格看得齐（用户："这样是不是看起来会和谐很多"）。
    ///
    /// 几何（24 网格，四个角写死在 <see cref="LaserNibQuad"/> 里）：
    ///   · 笔头 ＝ 一个**斜 45° 的梯形**（后边宽 6.8、尖上 3.0，像马克笔的头 / 钢笔尖），
    ///     轴线沿 135°（左下），笔尖中心在 (8.60, 15.90)；
    ///   · 三道光：起点同上，方向 **135°±30°**，从 3.2 格处起笔、长 2.4、粗 1.4。
    ///     为什么是 30°/3.2：圆头线帽各吃 0.7，相邻两道在起笔处的间距约 1.7 格，
    ///     刚好留出一线断口；再往外就散得更开（参考图里那三道光也是"根部挨着、梢上分开"）。
    ///   · ⚠ **整支要占满格子**：第一版按"笔长 7 格"做完，只占了 24 格里的一半，
    ///     摆在别的图标旁边明显小一号（出图一眼看到）——现在笔头长 12.6 格、
    ///     连光一共占 16.7 格，才和上游那批（字形普遍占 18～20 格）对得上。
    ///
    /// 两态（和这一排一个规矩）：
    ///   · 未选中 ＝ **线描**（笔头轮廓 1.5，空心）；
    ///   · 选中 ＝ **实心**（填满，再补描 1.2 的一圈把四个角磨圆——填充路径的角是尖的，
    ///     而这一排上游图标的角都是圆的）。
    /// </summary>
    private static void LaserNibPointer(ID2D1DeviceContext ctx, ID2D1Brush brush, bool outline)
    {
        var nib = LaserNibQuad();
        if (nib != null)
        {
            if (outline)
            {
                ctx.DrawGeometry(nib, brush, 1.5f, _round);
            }
            else
            {
                ctx.FillGeometry(nib, brush);
                ctx.DrawGeometry(nib, brush, 1.2f, _round);
            }
        }

        var tip = new Vector2(8.60f, 15.90f);
        LaserRay(ctx, brush, tip, 135f, 3.2f, 2.4f, 1.4f);
        LaserRay(ctx, brush, tip, 105f, 3.2f, 2.4f, 1.4f);
        LaserRay(ctx, brush, tip, 165f, 3.2f, 2.4f, 1.4f);
    }

    /// <summary>
    /// 激光笔那颗**笔头**（24 网格里的一副固定坐标，建一次就缓存）。
    ///
    /// 四个角（顺时针）：后左 (15.10,4.60) → 后右 (19.90,9.40) → 尖右 (9.66,16.96)
    /// → 尖左 (7.54,14.84)。它是一条 45° 的梯形：后边 6.79 宽、尖上 3.00 宽、
    /// 沿轴线长 12.6，笔尖中心落在 (8.60,15.90)——和 <see cref="Cone"/> /
    /// <see cref="EraserBody"/> 一个路子：自绘图标要填一块形状时用它，不每帧重建几何。
    /// </summary>
    private static ID2D1PathGeometry LaserNibQuad()
    {
        if (_nibQuad != null) return _nibQuad;
        if (_factory == null) return null;
        _nibQuad = _factory.CreatePathGeometry();
        using (var sink = _nibQuad.Open())
        {
            sink.BeginFigure(new Vector2(15.10f, 4.60f), FigureBegin.Filled);   // 后左
            sink.AddLine(new Vector2(19.90f, 9.40f));                           // 后右
            sink.AddLine(new Vector2(9.66f, 16.96f));                           // 尖右
            sink.AddLine(new Vector2(7.54f, 14.84f));                           // 尖左
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return _nibQuad;
    }

    /// <summary>
    /// **激光笔图标：钢笔尖 ＋ 三道杠**（档号 21～24，2026-09-27 第四轮）。
    ///
    /// 用户把 ClassIn 那颗笔头的原图（放大版）发过来，要求：
    /// "和 ClassIn 一样，在**笔**下面加 3 杠，表示光亮的意思；笔的图标我们需要自己找一个合适的尺寸。"
    /// 所以这一版不是"简化成一块梯形"（那是 20），而是把笔头画成**真正的钢笔尖**：
    ///   · **圆背**（后头两个角都是圆的，像笔杆那一头）；
    ///   · **收颈**（中间细一段）；
    ///   · **一条缺口**（右下侧那道 V 形切口——就是它让整个形状读成"钢笔尖"
    ///     而不是一块斜方块，也是参考图里那个像 B 的轮廓的来源）。
    /// 路径见 <see cref="NibFancyPath"/>（一条 `d` 串，走 <see cref="SvgPath"/> 的既成缓存）。
    ///
    /// 三道杠：笔尖外沿 135°（左下，也就是笔尖射出去的方向）±30°，从 3.4 格处起笔、
    /// 长 2.4、粗 1.4——"根部挨着、梢上散开"，和参考图里那三道一样。
    ///
    /// <paramref name="scale"/> ＝ **整组（笔尖 ＋ 三道杠）的缩放**，就是这一档要挑的东西：
    /// 1.00 时整组占满 15.4 格见方（和上游那批字形 18～20 格比略小、四周透气），
    /// 越小四周留白越多。**缩放中心取整组的中心 (9.68,14.32)**，所以缩了之后仍然居中。
    ///
    /// ⚠ 缩了之后**线宽要按屏幕补回来**（`1.5 / scale`）：世界变换会把描边一起缩，
    ///   不补的话 0.76 那一档的轮廓只有 1.14，摆进一排又变成"细一档"（这轮踩过的坑）。
    /// </summary>
    private static void LaserNibFancy(ID2D1DeviceContext ctx, ID2D1Brush brush, bool outline, float scale)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(scale, scale, new Vector2(9.35f, 14.65f)) * saved;

        var nib = SvgPath.Get(NibFancyPath);
        if (nib != null)
        {
            if (outline)
            {
                ctx.DrawGeometry(nib, brush, 1.5f / scale, _round);
            }
            else
            {
                ctx.FillGeometry(nib, brush);
                ctx.DrawGeometry(nib, brush, 1.2f / scale, _round);   // 磨圆那几个角
            }
        }

        var tip = new Vector2(7.60f, 16.40f);
        for (int i = -1; i <= 1; i++)
            LaserRay(ctx, brush, tip, 135f + i * 30f, 3.4f, 2.4f, 1.4f / scale);

        ctx.Transform = saved;
    }

    /// <summary>
    /// **钢笔尖那条路径**（24 网格，轴线沿 135°、笔尖在 (7.60,16.40)）。
    ///
    /// 所有点都是按"沿轴走 s 格、垂直偏 k 格"算出来的（u 指向左下、v 垂直于它，都按 45°）：
    /// P(s,k) = (7.60 + 0.7071×(s+k), 16.40 + 0.7071×(k−s))
    ///
    /// 骨架（s ＝ 沿轴离笔尖多远，k ＝ 偏离轴线多远）：
    ///   · 笔尖 s=0、k=±1.25 —— 两个点 (6.72,15.52)、(8.48,17.28)，正好相距 2.50，
    ///     中间用**半径 1.25 的半圆**收口（圆头，不是尖角；参考图里那个头是**粗短**的，
    ///     不是尖的，所以这里给得比较宽）；
    ///   · 收颈 s=3.0、k=±1.35 —— 笔身最细的那一段（8.77,13.32）／（10.68,15.23）；
    ///   · 肩 s=6.2、k=±3.40 —— 笔身最宽处（9.58,9.61）／（14.39,14.42）；
    ///   · 圆背 s=9.4、k=±3.40 —— 后端两个角各一个 `Q` 圆角（半径 1.0，
    ///     控制点就是那个直角顶点，所以写起来只有一组数）。
    ///
    /// **缺口（那道缝）**：右下侧从肩往笔尖方向走，在 (11.75,15.00) 处切开，
    /// 往笔身里切到 (10.18,13.04)（深入约 2.2 格、几乎切到中线），再回到颈的右下角。
    /// 这条窄而深的缝就是钢笔尖的"墨水槽"，也是参考图里那个轮廓最像"笔尖"的一处
    ///（早先我只切了个浅 V，出图一看整块像个斜方块，不像笔尖，所以改深）。
    /// </summary>
    private const string NibFancyPath =
        "M 6.72 15.52 L 8.77 13.32 L 9.43 10.29 Q 9.58 9.61 10.15 9.04 " +
        "L 11.13 8.06 Q 11.84 7.35 12.55 8.06 L 15.94 11.45 Q 16.65 12.16 15.94 12.87 " +
        "L 14.39 14.42 L 11.75 15.00 L 10.18 13.04 L 10.68 15.23 L 8.48 17.28 " +
        "A 1.25 1.25 0 1 1 6.72 15.52 Z";

    /// <summary>
    /// **激光笔图标的"笔头 ＋ 光晕圈"版**（档号 19，2026-09-27 第二轮 · 方案 B）。
    ///
    /// 和 <see cref="LaserNibGlow"/>（方案 A：点 ＋ 几道放射的短线）的区别只在"光"怎么画：
    /// 这里是**中心一个亮点 ＋ 外面一整圈细光环**（同心），像光点外面那层晕。
    /// 为什么另给一种：用户嫌"做了好几个都不大搭"，而这一排上游图标里"圆环"是很常见的语言
    ///（Fluent 的 Target / Record / Point Scan 都是圆环），比几道射线更贴这一排的手感；
    /// 两套摆一起让眼睛挑。
    ///
    /// ⚠ 第一版试的是"一圈**开口**的圆弧（像水波）"——出图一看不行：半径 2.55 的弧
    ///   缩到 24 像素只剩一小钩，读成了"笔在画一个小圈"，不是光。所以改成整圈。
    ///
    /// 数字（都按 24 网格）：
    ///   · 圆心 (7.00, 17.60) ＝ 离笔尖 (9.3,15.3) 有 3.25 格，**比光晕外沿 2.95 大一档**，
    ///     所以环和笔尖之间留着一道缝，不会粘在一起；
    ///   · 光环半径 2.2、线宽 1.5 → 外沿 2.95、内沿 1.45；
    ///   · 中心亮点半径 1.0，和光环内沿之间留 0.45 的缝（和方案 A 那条"缝要大于线粗的一半"一个道理）。
    /// </summary>
    private static void LaserNibHalo(ID2D1DeviceContext ctx, ID2D1Brush brush, bool outline)
    {
        LaserPenNib(ctx, brush, outline, 0.68f, 9.3f, 15.3f);

        var c = new Vector2(7.00f, 17.60f);
        ctx.DrawEllipse(new Ellipse(c, 2.2f, 2.2f), brush, 1.5f, _round);

        float dotR = outline ? 1.00f : 1.15f;
        ctx.FillEllipse(new Ellipse(c, dotR, dotR), brush);
    }

    /// <summary>
    /// **自绘的白板图标**（2026-09-26，用户："白板：感觉不像"）。
    ///
    /// 为什么不能用上游那个：Fluent 的 `Board` 是"圆角方框劈成四块窗格"，
    /// 缩到 24 像素看着像"窗口布局"或者"网格"，不是一块能写字的板；
    /// Fluent 里也没有 `Whiteboard` 这个专名（生成脚本的候选名里试过，下载不到）。
    /// 所以照仓库既成做法**自绘**（和激光笔、两种橡皮、平行四边形那几位一个路子）。
    ///
    /// 形状照各家白板图标的通行画法（一块**带两条腿的板** ＝ 教室里挂的那块板）：
    ///   · <paramref name="variant"/> 1 = 板 ＋ 两条腿；
    ///   · 2 = 板 ＋ 两条腿 ＋ **两行板书**（长短不一的两条线）——只有板和腿的话，
    ///     18 像素下和"一块屏幕 / 一个相框"分不开，加两笔"写上去的东西"才像黑板；
    ///   · 3 = 板 ＋ 两条腿 ＋ 下边的**托盘**（放笔/粉笔那条沿）。
    /// 24 网格、线宽 1.5、圆头圆角——和上游 regular 那一套（正圆环厚就是 1.5）
    /// 混在一排里看不出是谁画的。
    ///
    /// <paramref name="fill"/> 不为空时，**板面填成那个颜色**——这就是"选中显示选中什么"
    /// 那条逻辑（见 FullUi 里白板那一格）：板开着的时候图标就是**这块板的颜色**
    /// （白 / 绿 / 黑，见 InkPalette.BoardPresets）。描边照旧画：白板在白底面板上
    /// 光靠填充看不出边界，得靠这道描边兜住。
    /// </summary>
    public static void DrawBoard(ID2D1DeviceContext ctx, RectF box, float size,
                                 ID2D1Brush brush, Color4? fill = null,
                                 int variant = BoardDefault)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;

        // 板面：三种款式共用这一块圆角矩形（x 3.4→20.6、y 3.8→15.6，四角半径 1.7）
        const float x0 = 3.4f, y0 = 3.8f, x1 = 20.6f, y1 = 15.6f, radius = 1.7f;
        var face = new RoundedRectangle(new Vortice.RawRectF(x0, y0, x1, y1), radius, radius);
        if (fill.HasValue) ctx.FillRoundedRectangle(face, BrushOf(ctx, fill.Value));
        ctx.DrawRoundedRectangle(face, brush, 1.5f);

        // 局部小函数：往图标上画一条线（省得每句都写一遍 Vector2 和圆头样式）
        void Line(float ax, float ay, float bx, float by, float w)
            => ctx.DrawLine(new Vector2(ax, ay), new Vector2(bx, by), brush, w, _round);
        // 腿：**长腿、往外撇**（4.6 / 19.4 那条线）——出图比过，短腿（1 那档）在 24 像素下
        // 像"电视机的底座"，长腿才像一块立着的板。
        void Legs()
        {
            Line(7.0f, 15.6f, 4.6f, 21.0f, 1.5f);
            Line(17.0f, 15.6f, 19.4f, 21.0f, 1.5f);
        }
        // 板书：长短不一的两行。**填了板色的时候换颜色画**——白板上用深色（马克笔）、
        // 绿板/黑板上用浅色（粉笔）。不换的话，白板那一格就是"白底上画白线"，
        // 整支图标糊成一块白方块（出图时就是这样，读成了电视机）。
        var chalk = brush;
        if (fill.HasValue)
        {
            bool light = fill.Value.R * 0.299f + fill.Value.G * 0.587f + fill.Value.B * 0.114f > 0.5f;
            chalk = light ? BrushOf(ctx, new Color4(0.16f, 0.17f, 0.20f, 0.85f))
                          : BrushOf(ctx, new Color4(1f, 1f, 1f, 0.92f));
        }
        void Chalk(float ax, float ay, float bx, float by)
            => ctx.DrawLine(new Vector2(ax, ay), new Vector2(bx, by), chalk, 1.5f, _round);

        switch (variant)
        {
            case 1:      // 板 ＋ 两条**短**腿（对照用：短腿在 24 像素下像电视机底座）
                Line(7.4f, 15.6f, 5.6f, 20.6f, 1.5f);
                Line(16.6f, 15.6f, 18.4f, 20.6f, 1.5f);
                break;

            case 3:      // 板 ＋ 长腿 ＋ 托盘（腿之间那道横沿）
                Legs();
                Line(5.4f, 18.4f, 18.6f, 18.4f, 1.5f);
                break;

            case 4:      // 板 ＋ 长腿（没有板书）
                Legs();
                break;

            case 5:      // 板 ＋ 一支**正在写的马克笔**（不画腿）：笔斜搭在板的右下角上
                // ⚠ 这支笔要用 **chalk（跟随板色对比）**画、不能用图标色：
                // 选中那一档整块板是**白的**（图标色），笔再用图标色画就并进板里，
                // 整支图标读成"一个聊天气泡"（出图一眼看到的，就是这条）。
                // 换成对比色之后：白板上是深色马克笔、绿/黑板上是浅色笔——两边都认得出。
                //
                // ⚠ 笔身原来画的是 **2.8** 宽（笔尖 1.6）——那是这一整排里最粗的一笔
                //   （旁边的图标一律 1.5），2026-09-27 用户就是冲它说的"白板图标……
                //   感觉有点粗啊"。现在压到 **2.0 / 1.5**：还是一眼能看出是支笔
                //   （比板书线粗一档、有笔尖），但不再比左右两格重。
                ctx.DrawLine(new Vector2(12.4f, 17.6f), new Vector2(19.0f, 11.0f), chalk, 2.0f, _round);
                ctx.DrawLine(new Vector2(12.4f, 17.6f), new Vector2(10.8f, 19.2f), chalk, 1.5f, _round);
                break;

            case 6:      // 板 ＋ 两行板书（**不画腿**，2026-09-27 第二轮加的候选）
                // 和 5 号的区别：不画那支笔，改回"写上去的两行字"。为什么给这一档：
                // 板里那支深色的笔，缩到 24 像素和橡皮那一格的形状很像（用户："白板图标
                // 感觉差不多就是那个橡皮"）；两行板书没有这个问题，而且笔更少、更轻。
                Chalk(6.9f, 8.4f, 14.6f, 8.4f);
                Chalk(6.9f, 11.5f, 11.1f, 11.5f);
                break;

            default:     // 2：板 ＋ 长腿 ＋ 两行板书（长的在上、短的在下）
                Legs();
                Chalk(6.9f, 8.4f, 14.6f, 8.4f);
                Chalk(6.9f, 11.5f, 11.1f, 11.5f);
                break;
        }

        ctx.Transform = saved;
    }

    /// <summary>
    /// 产品里用哪一档白板图标（改这一个数字就能换，候选见 ToolIconSheet）。
    ///
    /// 2026-09-26 用户挑的是 **5（板 ＋ 正在写的马克笔）**：原话"白板那个，我感觉 5 号笔比较好"。
    /// 它没有腿、不画板书，"一块板 ＋ 一支搭在右下角的笔"就够说明"这是块能写的板"，
    /// 24 像素下也不会读成"电视机"（带腿那几版出图比过，短腿像底座、长腿也还是像支架）。
    /// </summary>
    public const int BoardDefault = 5;

    /// <summary>
    /// 自绘图标偶尔需要一个**固定颜色**的画刷（比如那颗红点：它不是"图标色"，
    /// 它就是激光本身的颜色）。按颜色缓存，不每帧重建。
    /// </summary>
    private static ID2D1SolidColorBrush BrushOf(ID2D1DeviceContext ctx, Color4 c)
    {
        uint key = ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16)
                 | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);
        if (_fixed.TryGetValue(key, out var b)) return b;
        b = ctx.CreateSolidColorBrush(c, null);
        _fixed[key] = b;
        return b;
    }

    private static readonly Dictionary<uint, ID2D1SolidColorBrush> _fixed = new();

    /// <summary>
    /// **自绘激光笔图标里"最后用过的那一档"**（档号 20），2026-09-27 起**产品已经不用它了**：
    /// 那一格换成了上游 Fluent 的 `Laser Tool`（见 <see cref="DrawLaserCell"/>）。
    /// 这个常量留着只为两处：
    ///   · `--toolicons` 那张候选表要按档号摆出自绘的每一版给眼睛比；
    ///   · 以后想退回自绘时，改 <see cref="DrawLaserCell"/> 一处即可。
    ///
    /// 20 是"**只有一颗笔头 ＋ 三道光**"（<see cref="LaserNibPointer"/>），
    /// 到它为止一共试过这些（换档前先看一眼，别再走回头路）：
    ///   · **16 ＝ A′**：大笔 ＋ 两道断开的虚线 ＋ 一个点（2026-09-26 定的，用户当时要"不能太瘦弱单薄"）；
    ///   · **17 ＝ 方案 A**：笔头 ＋ 亮点 ＋ 三道射线（照 ClassIn 的构成，第二轮）；
    ///   · **18**：17 的四道射线版；
    ///   · **19 ＝ 方案 B**：笔头 ＋ 亮点 ＋ 一圈光晕（第二轮）；
    ///   · **20 ＝ 方案 C**：只有一颗笔头 ＋ 三道光（第三轮）；
    ///   · **21～24 ＝ 方案 D①～④**：钢笔尖 ＋ 三道杠，四档只差缩放（第四轮，
    ///     见 <see cref="LaserNibFancy"/>）——**用户最后没要自绘的，改用了上游现成的那张**。
    /// 更早否定掉的：长直光束 / 楔形 / 实心锥 / 光柱 / 折角 / 参考图那支带卡口的笔。
    /// </summary>
    public const int LaserDefault = 20;

    /// <summary>
    /// **没选中时的激光笔图标：线条版**（档号 5，老那一版）。
    ///
    /// 用户 2026-09-17："这个笔使用的时候图标里面没有颜色，但是激光笔里面有颜色。"
    /// 查下来不是配色漏了，是**两套图标来源的手感不一样**：
    /// Fluent 那批是"regular（线描）／filled（实心）"一对，未选中用线描；
    /// 而激光笔是我们自绘的，只有一副"实心"的画法（笔身 3.6 像素粗、落点是实心点），
    /// 于是它在一排线描图标里显得最"重"、像被填了色。
    ///
    /// ⚠ 2026-09-26 起这个"另一档号"的写法**已经被 `outline` 那个开关取代**：
    /// 现在选中与未选中是**同一支图标**，只是粗细与落点实心/空心不同（见 <see cref="DrawLaser"/>）。
    /// 这个常量留着只为一处：对照图里还要把"老那一版"摆出来给眼睛看（ToolIconSheet）。
    /// </summary>
    public const int LaserOutline = 5;

    /// <summary>
    /// **自绘的两种橡皮图标**（<paramref name="area"/> = 面积擦 / 像素橡皮）。
    ///
    /// 为什么不用 Fluent 的 Eraser（用户 2026-09-17："笔迹擦除的图标不合理"）：
    /// 它是一块**斜着的圆角方块**，20 像素下读起来像个菱形 / 一片叶子，
    /// 而且整笔擦和面积擦**用的是同一个图标**——两个行为完全不同的工具长得一模一样，
    /// 老师只能靠上面的文字分。
    ///
    /// 现在两个分开画，各自的图形就是它**在屏幕上的样子**：
    ///   · 整笔擦：一块橡皮压在一条线上，线**从橡皮底下钻出来**（碰到哪条就整条没了）；
    ///   · 面积擦：**竖着的黄金比例矩形**＋中心十字＋一层淡填充——和落点光标
    ///     （`Overlay.DrawEraserRectCursor`）是同一个形状，老师一眼对得上。
    ///
    /// 网格 24、线宽 1.8～2.6，和自绘的激光笔同一套手感。
    /// </summary>
    public static void DrawEraser(ID2D1DeviceContext ctx, RectF box, float size,
                                 ID2D1Brush brush, bool area, bool outline = false)
    {
        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = (box.MinY + box.MaxY) * 0.5f;
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(cx - size * 0.5f, cy - size * 0.5f)
                      * saved;

        if (area)
        {
            // **虚线框**（竖着的黄金比例矩形：9 × 14.6）。
            //
            // 第一版画的是"实线框＋中心十字"，出图一看**像个加号按钮**（12 像素宽的框
            // 在 20 像素的按钮里几乎成了正方形，"＋"又最抢眼）。改成虚线框之后：
            // 虚线是各家通用的"一块区域"的说法，和落点那个矩形是同一个意思，
            // 也不会再和"加号/放大"混。
            float hw = 4.5f, hh = 7.3f;
            const float dl = 2.6f, gp = 2.1f, lw = 1.8f;
            DashedLine(ctx, new Vector2(12f - hw, 12f - hh), new Vector2(12f + hw, 12f - hh), dl, gp, lw, brush);
            DashedLine(ctx, new Vector2(12f + hw, 12f - hh), new Vector2(12f + hw, 12f + hh), dl, gp, lw, brush);
            DashedLine(ctx, new Vector2(12f + hw, 12f + hh), new Vector2(12f - hw, 12f + hh), dl, gp, lw, brush);
            DashedLine(ctx, new Vector2(12f - hw, 12f + hh), new Vector2(12f - hw, 12f - hh), dl, gp, lw, brush);
        }
        else
        {
            // 先画那条**笔画**（横着一条），再用**实心**橡皮块把它的左半截压住——
            // 看起来就是"线从橡皮底下钻出来"，一眼明白"碰到就整条没了"。
            //
            // 橡皮用**实心**（不是描边）：20 像素的按钮里，描边的斜方块会糊成一圈线，
            // 实心的块才读得出来"这是一块橡皮"（出图比过两版）。
            // 线的起点故意留在方块**里面**（y=9.2 时方块占 x∈[10.3,13.4]），
            // 不然会从方块左上角外面露出一小截，像线穿过去了。
            // 没选中时线宽收一号、橡皮块**画成空心**——同一排 Fluent 图标都是线描的，
            // 只有这一个实心块的话，一眼就它最重（和激光笔同一个毛病）。
            ctx.DrawLine(new Vector2(11.0f, 9.2f), new Vector2(21.2f, 9.2f), brush,
                         outline ? 1.9f : 2.6f, _round);
            if (outline)
            {
                var (c, ax, pe) = EraserFrame();
                const float hl = 5.6f, hw = 3.5f;
                var p1 = c + ax * hl + pe * hw;
                var p2 = c + ax * hl - pe * hw;
                var p3 = c - ax * hl - pe * hw;
                var p4 = c - ax * hl + pe * hw;
                ctx.DrawLine(p1, p2, brush, 1.8f, _round);
                ctx.DrawLine(p2, p3, brush, 1.8f, _round);
                ctx.DrawLine(p3, p4, brush, 1.8f, _round);
                ctx.DrawLine(p4, p1, brush, 1.8f, _round);
                // 那道"用到哪儿"的分界（几何留缝那块，空心版就画一条线）
                var m = c - ax * 2.6f;
                ctx.DrawLine(m + pe * hw, m - pe * hw, brush, 1.6f, _round);
            }
            else
            {
                var body = EraserBody();
                if (body != null) ctx.FillGeometry(body, brush);
            }
        }

        ctx.Transform = saved;
    }

    /// <summary>锥形光束那片半透明填充（建一次）。</summary>
    private static ID2D1PathGeometry Cone()
    {
        if (_cone != null) return _cone;
        if (_factory == null) return null;
        _cone = _factory.CreatePathGeometry();
        using (var sink = _cone.Open())
        {
            sink.BeginFigure(new Vector2(6f, 18f), FigureBegin.Filled);
            sink.AddLine(new Vector2(20.5f, 3.5f));
            sink.AddLine(new Vector2(21.5f, 13.5f));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        return _cone;
    }

    /// <summary>
    /// 整笔橡皮那块**斜 42° 的实心方块**（24 网格里的一副固定坐标，建一次）。
    /// 和 <see cref="Cone"/> 一样：自绘图标要填一块形状时用它，不每帧重建几何。
    /// </summary>
    private static ID2D1PathGeometry EraserBody()
    {
        if (_eraserBody != null) return _eraserBody;
        if (_factory == null) return null;

        var (c, ax, pe) = EraserFrame();
        const float hl = 5.6f, hw = 3.5f;
        var p1 = c + ax * hl + pe * hw;
        var p2 = c + ax * hl - pe * hw;
        var p3 = c - ax * hl - pe * hw;
        var p4 = c - ax * hl + pe * hw;

        var g = _factory.CreatePathGeometry();
        using (var sink = g.Open())
        {
            // 画成**两块**，中间留一道 1.1 像素的缝——就是橡皮上那道"用到哪儿"的分界。
            // 用"几何留缝"而不是"再画一条背景色的线"：图标底色可能是面板底、
            // 也可能是选中态的强调色，背景色画不对就成了脏点；留缝是**真的透过去**，
            // 两种底色下都对。
            Quad(sink, ax, pe, hw, -hl, -hl * 0.30f);
            Quad(sink, ax, pe, hw, -hl * 0.10f, hl);
            sink.Close();
        }
        _eraserBody = g;
        return _eraserBody;
    }

    /// <summary>橡皮块那副固定坐标（中心 ＋ 长轴 ＋ 短轴）。实心版和空心版共用一份。</summary>
    private static (Vector2 C, Vector2 Ax, Vector2 Pe) EraserFrame()
    {
        var c = new Vector2(10.2f, 14.0f);
        float r = -42f * MathF.PI / 180f;
        var ax = new Vector2(MathF.Cos(r), MathF.Sin(r));   // 长轴（指向右上）
        var pe = new Vector2(-ax.Y, ax.X);                   // 短轴
        return (c, ax, pe);
    }

    /// <summary>往几何里加一块"长轴从 a 到 b、半宽 hw"的矩形（自绘图标拼形状用）。</summary>
    private static void Quad(ID2D1GeometrySink sink, Vector2 ax, Vector2 pe, float hw, float a, float b)
    {
        var c0 = ax * a; var c1 = ax * b; var w = pe * hw;
        sink.BeginFigure(c0 + w, FigureBegin.Filled);
        sink.AddLine(c1 + w);
        sink.AddLine(c1 - w);
        sink.AddLine(c0 - w);
        sink.EndFigure(FigureEnd.Closed);
    }

    private static ID2D1PathGeometry _eraserBody;

    /// <summary>虚线：自绘图标画"一块区域"时用（面积橡皮）。圆头线头，比分段方头好看。</summary>
    private static void DashedLine(ID2D1DeviceContext ctx, Vector2 a, Vector2 b,
                                   float dash, float gap, float w, ID2D1Brush brush)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 0.01f) return;
        d /= len;
        for (float s = 0f; s < len; s += dash + gap)
        {
            float e = MathF.Min(s + dash, len);
            ctx.DrawLine(a + d * s, a + d * e, brush, w, _round);
        }
    }

    /// <summary>
    /// **直线那三档线型的图标**：一根 24 网格里的横线（x 3→21、y 12），只换"怎么画"。
    ///
    /// 三个要点：
    ///   · **同一根线**——三张图只有线型不同，老师一眼能把它们对成"同一格的三种状态"
    ///     （凑上游三张不同的图反而会花）；
    ///   · 段长 / 间隙按线型走：实线一整条、虚线长段（4 格）、点线短段（圆头 + 0.7 格
    ///     ≈ 一个圆点）；点线的**间隙比虚线大**，不然点会连成一串看不清是点线；
    ///   · 线宽 1.8：比上游 regular 那一套（1.5）粗一点点——它是"一根线"这个概念的图标，
    ///     和旁边那些有轮廓的方块摆在一起时不该显得更细。
    /// </summary>
    /// <summary>
    /// 「图库」的图标（2026-09-22，用户要的"图像收藏"）：**书架**——两根直的书 ＋ 一根靠着的书。
    ///
    /// 和"平行四边形 / 抛物线 / 立体图形"那几位同一个处境：上游 Fluent 有 `Library`，
    /// 但我们的图标表是 `tools/gen-ui-icons.ps1` 生成的，**手写进去下次重跑就没了**，
    /// 所以按仓库既成做法**在这里自绘**（24 网格、圆头圆角、线宽和上游 regular 对齐）。
    /// </summary>
    private static void DrawLibrary(ID2D1DeviceContext ctx, float x, float y,
                                    float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float w = 2.6f;
        ctx.DrawLine(new Vector2(7f, 5.5f), new Vector2(7f, 18.5f), brush, w, _round);
        ctx.DrawLine(new Vector2(12f, 5.5f), new Vector2(12f, 18.5f), brush, w, _round);
        ctx.DrawLine(new Vector2(17f, 5.5f), new Vector2(20f, 18.5f), brush, w, _round);

        ctx.Transform = saved;
    }

    private static void DrawLineStyle(ID2D1DeviceContext ctx, float x, float y,
                                     float size, ID2D1Brush brush, StrokeDash dash)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        var a = new Vector2(3f, 12f);
        var b = new Vector2(21f, 12f);
        const float w = 1.8f;
        switch (dash)
        {
            case StrokeDash.Dashed: DashedLine(ctx, a, b, 4f, 2.4f, w, brush); break;
            case StrokeDash.Dotted: DashedLine(ctx, a, b, 0.7f, 3.4f, w, brush); break;
            default: ctx.DrawLine(a, b, brush, w, _round); break;      // 实线
        }

        ctx.Transform = saved;
    }
}
