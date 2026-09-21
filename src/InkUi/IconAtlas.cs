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
        if (name == "hyperbola") { DrawHyperbola(ctx, x, y, size, brush); return; }
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
        // 函数曲线（第十七批）：指数 / 对数 / 幂那一档的 5 个值。**只给一个名字前缀**，
        // 具体哪一张由 `FullUi.PowerIconName` 按当前档拼出来（power1 … power5）。
        if (name == "exp") { DrawFunctionIcon(ctx, x, y, size, brush, Tool.Exponential, 0); return; }
        if (name == "log") { DrawFunctionIcon(ctx, x, y, size, brush, Tool.Logarithm, 0); return; }
        if (name.StartsWith("power") && name.Length == 6 && char.IsDigit(name[5]))
        {
            DrawFunctionIcon(ctx, x, y, size, brush, Tool.Power, name[5] - '1');
            return;
        }
        // 直线那三档线型的图标（用户 2026-09-20 定：图形面板里"直线"那一段再点一次
        // 就在实线 / 虚线 / 点线之间换，所以要有三张）。同样只能自绘：
        // 上游那张 `lineWeight` 是实线的，一个虚线 / 点线专名都没有。
        if (name == "line") { DrawLineStyle(ctx, x, y, size, brush, StrokeDash.Solid); return; }
        if (name == "lineDash") { DrawLineStyle(ctx, x, y, size, brush, StrokeDash.Dashed); return; }
        if (name == "lineDot") { DrawLineStyle(ctx, x, y, size, brush, StrokeDash.Dotted); return; }

        var geo = SvgPath.Get(PanelIcons.Get(name));
        if (geo == null) return;

        // 有些上游图标不是 24 网格（Material Symbols 用 960），而且原点可能是负的
        // （viewBox="0 -960 960 960"）。只按宽度缩放、不按原点平移，图标会画到框外——
        // 看着就是"这个图标只有一个小角"（自检出图时当场看到过一次）。
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
        ctx.Transform = Matrix3x2.CreateScale(size / box)
                      * Matrix3x2.CreateTranslation(-ox, -oy)
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
                                 float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

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
    /// 自绘的双曲线图标：左右两支（对应"实轴沿 x"那一档）。
    ///
    /// 参数方程就是画布上那一份 `x = ±a·cosh t、y = b·sinh t`（a = b = 4）——
    /// 两条外向的弧，中间留白，一眼和抛物线分得开（抛物线只有一支、也没有中间的空）。
    /// </summary>
    private static void DrawHyperbola(ID2D1DeviceContext ctx, float x, float y,
                                      float size, ID2D1Brush brush)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        const float tMax = 1.3f;
        const int seg = 8;
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
    /// **函数曲线的小图**（指数 / 对数 / 幂共用）：把那一档"长什么样"画进 24 格里。
    ///
    /// 三条约定：
    ///   · **式子和画布同源**：取值一律走 <see cref="ShapeSpec"/> 的那几个函数
    ///     （`ExpValueOf` / `LogValueOf` / `PowerValueOf`），所以"图标像 2ˣ、画出来是别的"
    ///     这种事不会发生；底数 / α 也读同一张表。
    ///   · **图标是示意**：画布上"一个单位"是拖出来的（可以是 40 像素，也可以是 200），
    ///     图标里固定取 **3 格 = 1 个单位**、纵向只画到 **±2 个单位**（24 格里放不下画布上那个 ±6）。
    ///   · **指数 / 对数没有档位**（底数是拖出来的），所以图标给的是**典型样子**
    ///     （2ˣ / log₂x）——它们本来就没有"当前档"可言。
    ///   · 幂函数 1/x 那一档多画两条**细虚线渐近线**（x = 0 与 y = 0），和画布上一致。
    /// </summary>
    private static void DrawFunctionIcon(ID2D1DeviceContext ctx, float x, float y, float size,
                                         ID2D1Brush brush, Tool tool, int index)
    {
        var saved = ctx.Transform;
        ctx.Transform = Matrix3x2.CreateScale(size / 24f)
                      * Matrix3x2.CreateTranslation(x, y)
                      * saved;

        // 一个单位几格、纵向截到几（**都是"图标里"的示意值**，画布上"一个单位"是拖出来的）：
        // 指数那条**必须把单位放大**——它长得太快，单位小了只看得见贴着 x 轴的那一小段
        // （第一版就是那么画的：24 格里成了一条平线，出图标对照表才发现）。
        float unit, clip;
        Vector2 anchor, anchorMath;
        switch (tool)
        {
            case Tool.Logarithm:
                unit = 3f; clip = 2f; anchor = new Vector2(7f, 12f); anchorMath = new Vector2(1f, 0f); break;
            case Tool.Power:
                unit = 3f; clip = 2f; anchor = new Vector2(7f, 12f); anchorMath = new Vector2(1f, 1f); break;
            default:
                unit = 4f; clip = 2.5f; anchor = new Vector2(12f, 19f); anchorMath = new Vector2(0f, 1f); break;
        }
        // 数学坐标 → 格坐标
        Vector2 P(float mx, float my)
            => new(anchor.X + (mx - anchorMath.X) * unit, anchor.Y - (my - anchorMath.Y) * unit);

        // 三条曲线各画哪一段（数学 x 的范围；只画这一段够看出形状了）
        float xFrom, xTo;
        switch (tool)
        {
            case Tool.Exponential: xFrom = -2.5f; xTo = 4f; break;      // 左边贴着 x 轴那一截
            case Tool.Logarithm: xFrom = 0.25f; xTo = 4f; break;        // 左端已经贴着 y 轴
            default: xFrom = 1f - clip; xTo = 1f + clip; break;         // 幂：左右各 2 个单位
        }

        // 按 x 均匀采样（图标只要"看得出形状"）：**域外 / 冲出可视高度就断开一笔**——
        // 断开这一手同时解决了"√x 没有左半""1/x 在 0 处断成两支"两件事。
        const int seg = 48;
        var prev = Vector2.Zero;
        bool pen = false;
        for (int i = 0; i <= seg; i++)
        {
            float mx = xFrom + (xTo - xFrom) * i / seg;
            float my = 0f;
            bool ok;
            switch (tool)
            {
                case Tool.Exponential:
                    ok = (my = ShapeSpec.ExpValueOf(ShapeSpec.IconTypicalBase, mx)) <= clip; break;
                case Tool.Logarithm:
                    ok = (my = ShapeSpec.LogValueOf(ShapeSpec.IconTypicalBase, mx)) <= clip; break;
                default: ok = ShapeSpec.PowerValueOf(index, mx, out my) && my <= clip; break;
            }
            if (!ok || my < -clip) { pen = false; continue; }
            var q = P(mx, my);
            if (pen) ctx.DrawLine(prev, q, brush, 1.5f, _round);
            prev = q;
            pen = true;
        }

        // 1/x 那两条渐近线：x = 0（竖）与 y = 0（横）——**细虚线**，和画布上一个排法。
        if (tool == Tool.Power && ShapeSpec.PowerHasAsymptotes(index))
        {
            DashedLine(ctx, P(0f, -clip), P(0f, clip), 2.2f, 1.8f, 1.1f, brush);
            DashedLine(ctx, P(1f - clip, 0f), P(1f + clip, 0f), 2.2f, 1.8f, 1.1f, brush);
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
    /// </summary>
    public static void DrawLaser(ID2D1DeviceContext ctx, RectF box, float size,
                                ID2D1Brush brush, ID2D1Brush softBrush = null,
                                int variant = LaserDefault)
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

    /// <summary>产品里用哪一个激光笔图标（改这一个数字就能换）。</summary>
    public const int LaserDefault = 2;

    /// <summary>
    /// **没选中时的激光笔图标：线条版。**
    ///
    /// 用户 2026-09-17："这个笔使用的时候图标里面没有颜色，但是激光笔里面有颜色。"
    /// 查下来不是配色漏了，是**两套图标来源的手感不一样**：
    /// Fluent 那批是"regular（线描）／filled（实心）"一对，未选中用线描；
    /// 而激光笔是我们自绘的，只有一副"实心"的画法（笔身 3.6 像素粗、落点是实心点），
    /// 于是它在一排线描图标里显得最"重"、像被填了色。
    ///
    /// 现在补齐：**未选中＝线条版（细一号、落点空心），选中＝原来的实心版**——
    /// 和 Fluent 那批同一套规矩。
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
