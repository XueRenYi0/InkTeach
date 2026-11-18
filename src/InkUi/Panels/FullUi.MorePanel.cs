// [职责] 更多面板：首页/设置/说明/日志/AI 服务入口。
// [测试] --paneltest

using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using InkEngine;
using InkLocalization;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 完整界面：收起态是**一个球**，点开是一条**按钮带**；球就是带子最左那一格。
///
/// 这是第一版，刻意只做"按钮 ＋ 命中测试"（色带、粗细滑块、「更多」抽屉都还没有）：
/// 先把**坐标 / DPI / 脏区 / 输入拦截**这四件最容易出错的事验对，
/// 后面搬色带和抽屉都只是堆代码（见 计划-底层对接界面.md 阶段 2）。
///
/// 它只认引擎的三个接口：画自己、解释点击、改状态走命令。
/// </summary>

public sealed partial class FullUi
{
    // ---- 绘制 ---------------------------------------------------------------

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_host == null) return;
        // 状态推进已经挪到 PrepareFrame（引擎在 QueryBounds 之前调用）。
        // 这里**只画**：再在绘制里改位置/带高，就会出现"画在新位置、脏区按旧值"。
        RenderShifted(ctx);
    }

    private void RenderShifted(ID2D1DeviceContext ctx)
    {
        var saved = ctx.Transform;
        var shift = Shift();
        if (shift != Vector2.Zero)
            ctx.Transform = Matrix3x2.CreateTranslation(shift) * saved;
        try
        {
            RenderCore(ctx);
            DrawTip(ctx);               // 悬停提示画在最上面（主条/带子/「更多」面板之上）
        }
        finally
        {
            ctx.Transform = saved;
        }
    }

    private void RenderCore(ID2D1DeviceContext ctx)
    {
        float e = _expand.Value;
        var bar = BarRect();
        var panel = UnionRect();

        // **整块面板一张卡片**（主条 ＋ 色带共用一个圆角）：圆角随展开从 24 收到 18，
        // 和假面板一样（它写的是 L.Radius = 24 - 6 * e）。
        float radius = Tokens.PillRadius(Tokens.BarHeight) - 6f * e;
        // **收起态贴边隐藏时，球本身那张卡片不画**：画了的话它的圆和投影会一起露出来，
        // 把手下面还压着一团（露出 8 像素时看得见那一片弧——正是要收拾的那个观感）。
        // 这一条必须在这里判：下面那个 `e < 0.5` 分支里再 return 已经晚了。
        if (!(e < 0.5f && PeekTabShown)) DrawCard(ctx, panel, radius);

        var st = _host.State;

        // 贴边隐藏沉下去之后（露头已经只剩十来个像素），**不再露球的那一小片弧**，
        // 改成一条直的把手（见 Tokens.PeekTab：那一片弧看着像残影）。
        // 换的时机见 PeekTabShown：再早换，球里的色环和笔图标会提前消失。
        if (e < 0.5f && PeekTabShown)
        {
            DrawPeekTab(ctx);
            return;
        }

        // ---- 内容是**两段式**的：形状先长，内容再交叉换 ----
        //
        // 原来是在 e = 0.5 上硬切（球在 e<0.5 画、14 格在 e≥0.5 画）。出图当场看出两个毛病
        // （`--panelshow <图> --expand 0.15` / `0.5`）：
        //   ① 色环和笔图标是按**面板的一半宽**算的，面板一长它们就被撑成一整个大图标，
        //      而且还跟着面板中心往右跑；
        //   ② 硬切那一下 14 格"啪"地出现，而且**没被面板裁住**——右边几个工具跑到面板外面。
        // 现在：形状照旧按 e 长（200ms、ease-out），
        //   球的内容：e 0.05→0.45 淡出；  14 格：e 0.45→0.80 淡入。
        // 中间那段两者同时在，位置也是同一个（球那一格），读起来就是"球摊开成格子"。
        float ballA = 1f - Smooth01((e - 0.05f) / 0.40f);
        float cellsA = Smooth01((e - 0.45f) / 0.35f);

        if (ballA > 0.004f) DrawBallContent(ctx, st, e, ballA);

        // 色带那条**凹槽**：把色带那一块裁出来、填一层淡淡的暗色（照假面板：
        // 裁进面板的圆角形状，顶部两个角自然跟着圆）。
        var band = BandRect();
        if (cellsA > 0.004f)
        {
            // 形变期间**把内容裁进面板**：面板还没长到全宽时，右边那几格会数到面板外面去。
            // 平时（e≈1）一次多余的裁剪都不做。
            bool clip = e < 0.999f;
            if (clip)
                ctx.PushAxisAlignedClip(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY),
                                        AntialiasMode.Aliased);
            // 淡入整层做（不透明度图层），而不是把 alpha 一路传进每个绘制函数：
            // 要淡的东西有几十处（14 格的图标、组分隔线、色片、滑条…），传 alpha 得改十几处签名。
            bool layered = cellsA < 0.996f && BeginFade(ctx, cellsA);
            DrawExpandedContent(ctx, st, panel, band, radius);
            if (layered) EndFade(ctx);
            if (clip) ctx.PopAxisAlignedClip();
        }

        // 更新红点：收起态在球上，展开态在「更多」格上。它不占面板内容层
        // （图层只给形变淡入用），所以单独按当前 alpha 画；「更多」面板打开时
        // 由面板里的「检查更新」格接手。
        if (!_moreOpen) DrawUpdateBadge(ctx, e, ballA, cellsA);

        // 面板上/下沿那道 1 像素的"收边"。**两个主题走相反的方向**（理由见 Tokens）：
        //   深色 → 顶沿一道极淡的白高光（读"接光"）；
        //   浅色 → 底沿一道极淡的暗线（读"卷边"）。白高光画在白面板上等于没画。
        //
        // 画法是"裁出一条 1.5 像素高的横带、带子里描一遍圆角矩形"：这样线到两端
        // 自然顺着圆角收进去。老代码是在带子里填一条**直**矩形，两端会戳出圆角外
        // 十几像素（浅色那条是白高光，落在亮背景上就是一条看得见的飞边）。
        if (!PerfSkipChrome && band.MaxY - band.MinY > 20f)
        {
            bool top = _dark;
            float y = top ? panel.MinY + 1f : panel.MaxY - 1f;
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(panel.MinX, y - 0.75f, panel.MaxX, y + 0.75f),
                                    AntialiasMode.Aliased);
            var edge = new Vortice.RawRectF(panel.MinX + 0.5f, panel.MinY + 0.5f,
                                            panel.MaxX - 0.5f, panel.MaxY - 0.5f);
            ctx.DrawRoundedRectangle(new RoundedRectangle(edge, radius - 0.5f, radius - 0.5f),
                                     Brush(ctx, top ? Tokens.TopSheenDark : Tokens.EdgeLightBottom), 1f);
            ctx.PopAxisAlignedClip();

            // 色带那条凹槽**朝主条的那一侧**再压一道 1 像素暗线（只有浅色）。
            // 凹槽的填充已经降到黑 6%，光靠它自己只是一条淡灰带；这一道线才是
            // "这道槽是刻进面板的"的凭据（Windows 的口径：浅色用描边定形）。
            // 位置跟着带子在主条的哪一侧走：面板贴底时带子在上面，线就落在带子下沿。
            //
            // 这条线**可以是一条直横线**（不像上面那条收边要顺着圆角走）：它在面板中间，
            // 而上面那个 `> 20` 的门槛保证了它离面板那条边至少 20 像素，
            // 同一刻的圆角也只有 20 出头——两端伸出去的不到半个像素，量不出来。
            if (!_dark)
            {
                float wy = BandAbove() ? band.MaxY - 0.5f : band.MinY + 0.5f;
                ctx.FillRectangle(new Vortice.RawRectF(panel.MinX + 1f, wy - 0.5f,
                                                       panel.MaxX - 1f, wy + 0.5f),
                                  Brush(ctx, Tokens.WellEdgeLight));
            }
        }

        // 这里原来还画一条"踢脚线"（`DrawGroove`：面板下沿 6 像素处、笔色 25% 的 2 像素横线）。
        // 2026-09-18 删了：它当年的任务是"让面板下沿永远有东西，看着是块完整的板子"，
        // 而现在下沿有**投影 ＋ 描边 ＋ 底沿内阴影**三条，这句话不用它承担了；
        // 留下的坏处更实在——颜色跟着笔走（红笔时它是整块白面板上最响的东西，像根进度条），
        // 而它想表达的"现在拿的是哪支笔"，球里那道色环已经说了、还更准。
        // 粗细预览**最后画**：它可能伸到面板外面，压在上面的东西得过它一层
        DrawSizePreview(ctx, st);
        // 「更多」面板**比一切都后**：遮罩要压住主条和粗细预览，面板再压住遮罩。
        // 它打开时 QueryBounds 已经是整块屏幕，不会被引擎裁掉。
        if (_moreOpen || _more.Running) DrawMorePanel(ctx);
        // AI 面板和「更多」互斥；它也画在最后，遮罩压住主条。
        if (_aiOpen || _ai.Running) DrawAiPanel(ctx);
    }

    // ---- 颜色（深色主题只是一整套换过来，形状一个都不动）------------------

    /// <summary>
    /// 展开态那一片内容：凹槽 ＋ 工具格 ＋ 设置条。抽出来是为了能在形变期间**整层淡入**
    /// （用一个不透明度图层包一次就够，不必把 alpha 传进十几个绘制函数）。
    /// </summary>
    private void DrawExpandedContent(ID2D1DeviceContext ctx, in UiState st,
                                     in RectF panel, in RectF band, float radius)
    {
        if (!PerfSkipChrome && band.MaxY - band.MinY >= 2f)
        {
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(band.MinX, band.MinY, band.MaxX, band.MaxY),
                                    AntialiasMode.Aliased);
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY), radius, radius),
                Brush(ctx, _dark ? Tokens.TroughDark : Tokens.TroughLight));
            ctx.PopAxisAlignedClip();
        }

        // 球缩进最左一格，右边是这一档的工具格（极简档就只有六格）
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++) DrawCell(ctx, k, st);

        if (BandVisible()) DrawBand(ctx, st);
        if (_penPickerOpen) DrawPenColorPicker(ctx, st);
        if (_textFontMenuOpen) DrawTextFontMenu(ctx, st);
    }

    /// <summary>
    /// 收起态球里面的东西：一圈当前笔色 ＋ 一个笔图标。
    ///
    /// 两处**必须按球算、不能按面板算**——老代码是按"面板的一半宽"算的，面板一长
    /// 图标就被撑成一整个大图标（出图 `--expand 0.15` 一眼就看见）：
    ///   · 大小 = 球的半径 × 系数（照假面板的数字）；
    ///   · 位置 = **球那一格的中心**。面板是往右长的，而球在展开态是第 0 格，
    ///     第 0 格的中心比球的中心靠右 4 像素（BarPad 8 ＋ 按钮 40 的一半 − 球半径 24），
    ///     所以按 e 插过去——顺手把"球缩进最左一格"这件事也演了出来。
    /// </summary>
    private void DrawBallContent(ID2D1DeviceContext ctx, in UiState st, float e, float alpha)
    {
        var bar = BarRect();
        float ballR = Tokens.Ball * 0.5f;
        float cx = bar.MinX + ballR + (Tokens.BarPad + Tokens.Button * 0.5f - ballR) * e;
        var c = new Vector2(cx, (bar.MinY + bar.MaxY) * 0.5f);
        float a = QA(alpha);

        // 数字照抄假面板：圆内那圈半径 = 0.76 × 球的半径、线宽 2.5，
        // 中间那个笔图标 = 0.70 × 球的半径。**两个比例都在 Tokens 里**（BallRing / BallIcon），
        // 改一个数的几何关系写在那儿——产品原来把 0.70 写成 1.40，笔正好顶到色圈内沿。
        var ink = st.PaletteBase;
        ctx.DrawEllipse(new Ellipse(c, ballR * Tokens.BallRing, ballR * Tokens.BallRing),
                        Brush(ctx, new Color4(ink.R, ink.G, ink.B, a)), 2.5f);

        var iconBox = new RectF
        {
            MinX = c.X - ballR, MinY = c.Y - ballR, MaxX = c.X + ballR, MaxY = c.Y + ballR,
        };
        var iconInk = InkCol;
        IconAtlas.DrawCentered(ctx, "pen", iconBox, ballR * Tokens.BallIcon,
                               Brush(ctx, new Color4(iconInk.R, iconInk.G, iconInk.B, iconInk.A * a)));
    }

    /// <summary>是不是有已发现、还没安装的新版本；红点只看这一个状态。</summary>
    private bool UpdateAvailable => _host != null && _host.State.UpdateStage == UpdateStage.Available;

    /// <summary>
    /// 「有新版本」的小红点。**挂在用户下一步会点的地方**，不做独立通知：
    ///   · 收起态 → 球右上角（球是唯一常驻的入口）；
    ///   · 展开态 → 「更多」格右上角（更新入口就在「更多」里）；
    ///   · 更多面板 → 「检查更新」格右上角（已经打开面板时不用再找主条）。
    /// 只有已发现新版本才画；检查中 / 已是最新 / 失败都不打扰。
    /// </summary>
    private void DrawUpdateBadge(ID2D1DeviceContext ctx, float e, float ballA, float cellsA)
    {
        if (!UpdateAvailable) return;

        if (ballA > 0.004f)
        {
            var br = BallRect();
            // 球本身有一圈**当前笔色**（默认就是红色）；红点放在球右上角的
            // 白色小底上，才不会被同色的笔环吃掉（用户 2026-10-19 的场景）。
            DrawUpdateDot(ctx, new Vector2(br.MaxX - 6f, br.MinY + 6f), 3.5f, ballA, halo: 2.5f);
        }

        if (cellsA > 0.004f)
        {
            int pos = PosOf(14);                 // 主条最后一格「更多」（13 是重做）
            if (pos >= 0)
            {
                var cr = CellRect(pos);
                DrawUpdateDot(ctx, new Vector2(cr.MaxX - 7f, cr.MinY + 7f), 4f, cellsA);
            }
        }
    }

    /// <summary>红点本体：白色描边垫底，深色 / 浅色主题上都看得清。</summary>
    private void DrawUpdateDot(ID2D1DeviceContext ctx, Vector2 c, float radius, float alpha,
                               float halo = 1.5f)
    {
        float a = QA(alpha);
        if (a <= 0.004f) return;
        var red = new Color4(0.93f, 0.18f, 0.14f, a);
        var ring = new Color4(1f, 1f, 1f, a * 0.95f);
        ctx.FillEllipse(new Ellipse(c, radius + halo, radius + halo), Brush(ctx, ring));
        ctx.FillEllipse(new Ellipse(c, radius, radius), Brush(ctx, red));
    }

    /// <summary>
    /// 开一个不透明度图层（形变期间整片内容淡入用）。**建不出来就返回 false**，
    /// 让调用方照常画——不能因为一个观感把这一帧丢掉（界面连抛三次会被引擎整体停用）。
    /// 图层**每帧现建现销**，不缓存：设备丢了之后手里就是过期的 COM 对象，
    /// 而这条路只在形变那 200 毫秒里走，代价可以忽略（安静时一次都不走）。
    /// </summary>
    private bool BeginFade(ID2D1DeviceContext ctx, float opacity)
    {
        try { _fadeLayer = ctx.CreateLayer(null); }
        catch { _fadeLayer = null; }
        if (_fadeLayer == null) return false;

        var p = new LayerParameters1
        {
            // 内容盒给"无限大"：给小了 D2D 会照它裁，内容就缺一块
            ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
            Opacity = QA(opacity),
        };
        ctx.PushLayer(ref p, _fadeLayer);
        return true;
    }

    private void EndFade(ID2D1DeviceContext ctx)
    {
        ctx.PopLayer();
        _fadeLayer?.Dispose();
        _fadeLayer = null;
    }

    /// <summary>
    /// 把 0..1 之外的截掉，里面用 smoothstep（两端速度为 0）——交叉淡入的两头才不起棱。
    /// </summary>
    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// 把透明度量化到 1/32 一档。**画刷是按颜色缓存的（建了就留着）**，逐帧喂连续值
    /// 会往缓存里塞几百个画刷；量化之后最多多 32 个，而 1/32 的台阶看不出来。
    /// </summary>
    private static float QA(float a) => MathF.Round(Math.Clamp(a, 0f, 1f) * 32f) / 32f;


    private Color4 PanelFill => _dark ? Tokens.PanelDark : Tokens.PanelLight;
    private Color4 BorderCol => _dark ? Tokens.BorderDark : Tokens.BorderLight;
    private Color4 InkCol => _dark ? Tokens.InkDark : Tokens.InkLight;
    private Color4 MutedCol => _dark ? Tokens.InkMutedDark : Tokens.InkMutedLight;
    private Color4 HoverCol => _dark ? Tokens.HoverDark : Tokens.HoverLight;

    /// <summary>
    /// 一张"卡片"：投影（逐层往外胀）＋ 底 ＋ 1px 描边（没有这道边，圆角会糊进背景里）。
    ///
    /// 投影有几层、每层胀多少、多深，全在 `Tokens.ShadowLight` / `ShadowDark` 里，
    /// 这里只负责"照单子一层层画"。**从最大的一层往最小的一层画**：大的先铺、小的后盖，
    /// 靠近面板的地方叠得最厚，往外逐渐变薄——落差就是这么来的。
    /// </summary>
    private void DrawCard(ID2D1DeviceContext ctx, RectF r, float radius)
    {
        if (!PerfSkipShadow)
        {
            var layers = _dark ? Tokens.ShadowDark : Tokens.ShadowLight;
            for (int i = layers.Length - 1; i >= 0; i--)
            {
                var s = layers[i];
                // 四边一起往外胀，圆角也加上同样的量：这样每一层都和面板**同心**。
                // 只胀矩形、不加大圆角的话，四个转角会缺一块（尖角戳出来）。
                var layerRect = new Vortice.RawRectF(r.MinX - s.Inflate, r.MinY - s.Inflate + s.Dy,
                                                     r.MaxX + s.Inflate, r.MaxY + s.Inflate + s.Dy);
                float layerRad = radius + s.Inflate;
                ctx.FillRoundedRectangle(new RoundedRectangle(layerRect, layerRad, layerRad),
                                         Brush(ctx, s.Color));
            }
        }

        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, PanelFill));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
    }

    /// <summary>
    /// 贴边隐藏时露出来的那条**把手**：一条直的药丸（收起态专用，见 Tokens.PeekTab）。
    ///
    /// 位置**直接取引擎算给我们的占用矩形**（<see cref="QueryBounds"/>）——那正是屏幕上
    /// 真正露出来的那一条，已经含了平移和"夹在屏幕内"。好处是**看得见的**和**点得到的**
    /// 天然是同一个地方，不会出现"看见一条却点不到"。
    ///
    /// 贴着左右边时露出来的那条是**竖的**，所以长边要按方向选。
    /// </summary>
    private void DrawPeekTab(ID2D1DeviceContext ctx)
    {
        var vis = QueryBounds();
        if (vis.IsEmpty) return;

        // 渲染时外面套着一层"贴边平移"的变换（见 RenderShifted），这里先减掉，
        // 画出来的位置才和占用矩形对得上。
        var s = Shift();
        var sliver = new RectF
        {
            MinX = vis.MinX - s.X, MinY = vis.MinY - s.Y,
            MaxX = vis.MaxX - s.X, MaxY = vis.MaxY - s.Y,
        };

        bool horiz = (sliver.MaxX - sliver.MinX) >= (sliver.MaxY - sliver.MinY);
        float cx = (sliver.MinX + sliver.MaxX) * 0.5f;
        float cy = (sliver.MinY + sliver.MaxY) * 0.5f;
        // 长边 = 把手长度（但不比露头那一条本身更长）；短边 = 露头有多厚（就填满它）
        float half = MathF.Min(Tokens.PeekTab,
                               horiz ? sliver.MaxX - sliver.MinX : sliver.MaxY - sliver.MinY) * 0.5f;
        float halfT = (horiz ? sliver.MaxY - sliver.MinY : sliver.MaxX - sliver.MinX) * 0.5f;

        var box = horiz
            ? new Vortice.RawRectF(cx - half, cy - halfT, cx + half, cy + halfT)
            : new Vortice.RawRectF(cx - halfT, cy - half, cx + halfT, cy + half);
        float rad = MathF.Min(halfT, half);
        var shape = new RoundedRectangle(box, rad, rad);

        // **颜色 = 当前笔色**，和展开态露出来的那条色线一模一样（用户 2026-09-18：
        // "展开条贴边以后有颜色的，这个小圆球贴边没颜色"）。
        // 它顺手把"手里是哪支笔"这件事也说了——露头时球里的色环是看不见的。
        var ink = _host.State.PaletteBase;
        ctx.FillRoundedRectangle(shape, Brush(ctx, new Color4(ink.R, ink.G, ink.B, 1f)));

        // 近白的时候补一道暗边：白笔的把手落在浅色桌面上会**什么都看不见**。
        // 这条规矩和色线（`DrawBandLine`）是同一条——那边早就这么干了。
        // 深色主题不用管：白在暗底上本来就看得见。
        if (!_dark && ink.R > 0.9f && ink.G > 0.9f && ink.B > 0.9f)
            ctx.DrawRoundedRectangle(shape, Brush(ctx, new Color4(0f, 0f, 0f, 0.2f)), 1f);
    }

    /// <summary>画上带的内容。每一项都对应引擎里真实存在的能力，摆不出来的就不摆。</summary>
    private void DrawBand(ID2D1DeviceContext ctx, in UiState st)
    {
        // 平时就是一条 **10 像素的色线，整条用当前笔色**（照假面板：不分段、没有文字），
        // 碰到才长成完整的设置条。中间那一段是"线淡出、控件淡入"的过渡
        // ——假面板的公式：t = (带高 - 12) / 14，0 = 还是一条线，1 = 完全是控件。
        float t = Math.Clamp((BandHeightFull() - 12f) / 14f, 0f, 1f);
        if (t < 0.999f) DrawBandLine(ctx, st, 1f - t);
        if (t <= 0.001f) return;

        if (!RailOpen) return;

        if (BandHasTextSize) DrawTextSize(ctx, st);

        if (BandHasSwatches)
        {
            for (int i = 0; i < SwatchCount; i++)
            {
                var r = SwatchRect(i);
                var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
                var rr = new RoundedRectangle(box, 7f, 7f);
                ctx.FillRoundedRectangle(rr, Brush(ctx, SwatchColor(i)));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

                // 一道内高光：色片才有"实体感"（假面板里写着"颜值上最便宜的一笔"）
                if (!PerfSkipSwatchDetail)
                {
                    var hl = new Vortice.RawRectF(r.MinX + 2, r.MinY + 1, r.MaxX - 2, r.MinY + 1 + (r.MaxY - r.MinY) * 0.26f);
                    ctx.FillRoundedRectangle(new RoundedRectangle(hl, 3f, 3f),
                        Brush(ctx, new Color4(1f, 1f, 1f, _dark ? 0.13f : 0.35f)));
                }

                if (!PerfSkipSwatchDetail && IsSwatchActive(st, i))
                {
                    // 选中的色块要有**第二重标记**（只靠颜色区分不符合无障碍要求）：
                    // 往里缩 2 像素再描一圈（照假面板的 1.6 线宽）。
                    var inner = new Vortice.RawRectF(r.MinX + 2, r.MinY + 2, r.MaxX - 2, r.MaxY - 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 5f, 5f), Brush(ctx, InkCol), 1.6f);
                }
                else if (_hover == 100 + i)
                {
                    var outer = new Vortice.RawRectF(r.MinX - 2, r.MinY - 2, r.MaxX + 2, r.MaxY + 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(outer, 9f, 9f), Brush(ctx, InkCol), 1.5f);
                }

                // 2026-10-28：8 个色位是同一种东西，不再给最后一格画“自定义”徽标。
            }
        }

        DrawDashToggle(ctx, st);

        int n = BandSegmentCount;
        for (int i = 0; i < n; i++) DrawSegment(ctx, i, n, st);

        if (BandHasSlider) DrawBandSlider(ctx, st);
        else if (BandHasWidthButton) DrawWidthButton(ctx, st);
        DrawBandAction(ctx);
        if (_aiMenuOpen) DrawAiMenu(ctx);

        // ⚠ 「第 N 屏」原来是**单画在带子右边**的一段文字（用的是写死的 `SegmentRect(4, 5)`），
        // 2026-09-27 改成翻页器中间那一段（见 <see cref="BoardSegKind.PageLabel"/>）——
        // 那块就是原来那个入口，只是搬到了两个箭头中间，成了"翻页器"的一部分。
    }

    /// <summary>
    /// 设置条右端的**粗细滑条**：底轨 ＋ 已选段（用当前颜色）＋ 滑钮 ＋ 右端一个
    /// 跟着变大的笔尖预览（粗细一眼看得见，比数字直观）。
    ///
    /// 滑钮**一直画**、不再"只有拖动/悬停才浮出来"：设置条本来就只在
    /// "焦点在面板上"时才出现（见 RailHoverZone），等于永远处在悬停态。
    /// 只在拖动时才出现的话，老师会以为那是个静态的分隔符。
    /// </summary>
    /// <summary>4 格及以下的紧凑粗细按钮：三根不同粗细的横线，点一下换档。</summary>
    private void DrawWidthButton(ID2D1DeviceContext ctx, in UiState st)
    {
        var r = WidthButtonRect();
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f, 7f);
        if (_hover == 501) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        float cx = (r.MinX + r.MaxX) * 0.5f;
        float cy = (r.MinY + r.MaxY) * 0.5f;
        var ink = st.PaletteBase;
        // 三根递增的横线 = 粗细语义；当前档用一根圆点标在右下角。
        for (int i = 0; i < 3; i++)
        {
            float y = cy - 6f + i * 6f;
            float half = 4f + i * 1.8f;
            ctx.DrawLine(new Vector2(cx - half, y), new Vector2(cx + half, y),
                         Brush(ctx, ink), 1f + i * 0.8f);
        }
        float t = SliderT(st);
        float dot = 2.2f + t * 2.2f;
        ctx.FillEllipse(new Ellipse(new Vector2(r.MaxX - 7f, r.MaxY - 7f), dot, dot),
                        Brush(ctx, ink));
    }

    private void DrawTextSize(ID2D1DeviceContext ctx, in UiState st)
    {
        var r = TextSizeButtonRect();
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f, 7f);
        if (_hover == 610 || _textFontMenuOpen) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
        _widgets.Text(ctx, $"{st.TextFontSize:F0} ▾", r, 12.5f, Brush(ctx, InkCol));
    }

    private void DrawTextFontMenu(ID2D1DeviceContext ctx, in UiState st)
    {
        var p = TextFontMenuRect();
        DrawCard(ctx, p, 10f);
        _widgets.Text(ctx, "字号",
                      new RectF
                      {
                          MinX = p.MinX + TextFontPad, MinY = p.MinY + 6f,
                          MaxX = p.MaxX - TextFontPad, MaxY = p.MinY + 6f + TextFontHeaderH,
                      },
                      11.5f, Brush(ctx, MutedCol), center: false);

        for (int i = 0; i < TextFontSizes.Length; i++)
        {
            var r = TextFontCellRect(i);
            bool active = MathF.Abs(st.TextFontSize - TextFontSizes[i]) < 0.1f;
            bool hover = _hover == 620 + i;
            var rr = new RoundedRectangle(
                new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f, 7f);
            if (active) ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
            else if (hover) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);
            _widgets.Text(ctx, $"{TextFontSizes[i]:F0}", r, 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
        }
    }

    private void DrawBandSlider(ID2D1DeviceContext ctx, in UiState st)
    {
        var box = SliderRect();
        var (left, right) = SliderTrackRange();
        float cy = (box.MinY + box.MaxY) * 0.5f;
        float t = SliderT(st);
        var ink = st.PaletteBase;

        float h = _sliderDragging ? 6f : 5f;
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(left, cy - h * 0.5f, right, cy + h * 0.5f),
                                 h * 0.5f, h * 0.5f),
            Brush(ctx, _dark ? Tokens.TrackDark : Tokens.TrackLight));

        float kx = left + (right - left) * t;
        if (kx - left > 0.5f)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(left, cy - h * 0.5f, kx, cy + h * 0.5f),
                                     h * 0.5f, h * 0.5f),
                Brush(ctx, new Color4(ink.R, ink.G, ink.B, _sliderDragging ? 0.85f : 0.55f)));

        // （档位点 2026-10-09 白天加、当晚撤：用户"不大好看，直接滑动取大小"——
        //   现在拖动吸附到最近一档，滑条上不再画小点。）

        ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 7f, 7f), Brush(ctx, Tokens.AccentInk));
        ctx.DrawEllipse(new Ellipse(new Vector2(kx, cy), 7f, 7f),
                        Brush(ctx, new Color4(0.19f, 0.20f, 0.24f, 1f)), 1f);

        // 右端那个"笔尖预览"：**白板那一格不画**。
        // 它画的是"这一笔有多粗"（半径跟着滑条位置变），而白板这一格拖的是**板面不透明度**，
        // 跟笔宽没有半点关系——它还会拿**当前笔色**（所以在白板上是颗红点）。
        // 用户 2026-09-27 就是在图上看见它才问的："调整透明的的滑块似乎后面跟着一个小圆点？"
        // 拖滑条 / 悬停时本来就有气泡在显示板色和百分比（DrawSizePreview），信息不缺。
        if (_bandCell != 2)
        {
            float pr = Math.Clamp(2f + t * 6.5f, 2f, 8.5f);
            ctx.FillEllipse(new Ellipse(new Vector2(box.MaxX - SliderPreviewW * 0.5f - 2f, cy), pr, pr),
                            Brush(ctx, ink));
        }
    }

    /// <summary>
    /// 虚实线切换那一格。**画的是"下一笔会长什么样"**：一小段按当前线型画出来的线
    /// （和选中面板里那三格同一个语言——老师看的是线本身，不是文字）。
    ///
    /// 样本线的节长比例照抄引擎那两条 D2D dash 图案（虚线"节 3 缝 2"、点线"节≈0 缝 2"，
    /// 单位都是笔宽，见 <c>Gfx</c>），但**不能直接用那条描边样式**：
    /// 它的节长按**真实笔宽**算，46×26 的小格子里会碎成一串看不清的点。
    /// 这里按这一格自己的样本线宽算节长，看着才是"缩小的虚线"。
    /// </summary>
    private void DrawDashToggle(ID2D1DeviceContext ctx, in UiState st)
    {
        if (!BandHasDashToggle || !RailOpen) return;
        var r = DashToggleRect();
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);

        if (_hover == 500) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        // 样本线用**当前笔色**：这一格顺手把"下一笔是什么颜色"也说了一遍。
        float lx0 = r.MinX + 9f, lx1 = r.MaxX - 9f;
        float ly = (r.MinY + r.MaxY) * 0.5f;
        float v = _dashFade.Value;                    // 0 = 全是旧档，1 = 全是新档
        if (v < 0.999f) DrawDashSample(ctx, _dashFadeFrom, lx0, lx1, ly, st.PaletteBase, 1f - v);
        DrawDashSample(ctx, st.Dash, lx0, lx1, ly, st.PaletteBase, v);

        // 换挡那一瞬间描一道强调色：三档的样本线在 46 像素里差别不大，
        // 光看"线变了"不容易确认"我刚才按到了没有"。
        if (v < 0.999f)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, Alpha(Tokens.Accent, 1f - v)), 1.6f);
    }

    /// <summary>
    /// 按线型画一小段样本。<paramref name="alpha"/> ≤ 0.01 就整段不画（换挡动画的淡出那半程走这条）。
    /// </summary>
    private void DrawDashSample(ID2D1DeviceContext ctx, StrokeDash dash, float x0, float x1, float y,
                                in Color4 ink, float alpha)
    {
        if (alpha <= 0.01f) return;
        var brush = Brush(ctx, Alpha(ink, alpha));
        const float w = 2.4f;                  // 样本线宽（和选中面板那三格的 2.6 一个量级）
        switch (dash)
        {
            case StrokeDash.Dashed:
                // 节 3w、缝 2w —— 和 Gfx 里那对数字同一个比例
                for (float x = x0; x < x1; x += w * 5f)
                    ctx.DrawLine(new Vector2(x, y),
                                 new Vector2(MathF.Min(x + w * 3f, x1), y), brush, w);
                break;
            case StrokeDash.Dotted:
                // 缝 2w、每个点是一个"直径 = w"的圆（引擎那边靠圆头 dash cap 鼓出来，同一个样子）
                for (float x = x0 + w * 0.5f; x < x1; x += w * 2f)
                    ctx.FillEllipse(new Ellipse(new Vector2(x, y), w * 0.5f, w * 0.5f), brush);
                break;
            default:
                ctx.DrawLine(new Vector2(x0, y), new Vector2(x1, y), brush, w);
                break;
        }
    }

    /// <summary>
    /// 换个透明度（换挡动画要淡入淡出用）。
    ///
    /// **透明度按 1/32 量化**：<see cref="Brush"/> 那个缓存是按颜色查的，
    /// 每帧一个新透明度就是一"把"新笔刷（167 毫秒 × 每帧两次 ≈ 几十把）。
    /// 量化之后一趟动画最多用 33 个值——肉眼看不出差别，缓存也不再被动画撑大。
    /// </summary>
    private static Color4 Alpha(in Color4 c, float a)
        => new(c.R, c.G, c.B, Math.Clamp(MathF.Round(a * 32f) / 32f, 0f, 1f));

    /// <summary>这一片色片是不是"当前色"（笔/荧光笔都按色位编号判）。</summary>
    private bool IsSwatchActive(in UiState st, int i)
    {
        if (UsingTextSwatches) return SameColor(st.TextColor, SwatchColor(i));
        if (st.Tool == Tool.Highlighter) return st.HighlighterSwatchSelected == SwatchIdx[i];
        return st.PenSwatchSelected == SwatchIdx[i];
    }

    /// <summary>
    /// 平时那条色线：**整条用当前笔色**，不分段、没有文字（照假面板）。
    /// `alpha` 是过渡用的——它长成设置条的过程中，这条线淡出。
    ///
    /// 一处细节：**白笔在浅底上会看不见**，所以给白线补一道极淡的描边兜底
    /// （假面板里专门为这一种情况写了这个分支）。
    /// </summary>
    private void DrawBandLine(ID2D1DeviceContext ctx, in UiState st, float alpha)
    {
        var r = BandRect();
        if (r.MaxY - r.MinY < 0.5f) return;

        var panel = UnionRect();
        float radius = Tokens.PillRadius(Tokens.BarHeight) - 6f * _expand.Value;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY), radius, radius);
        var line = st.PaletteBase;

        // **贴着面板顶边的圆角走**（照假面板）：整条线的两端跟着面板的圆角收进去，
        // 而不是在面板里缩进一条独立的小线——那样看着像"贴了张纸条"。
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
                                AntialiasMode.Aliased);
        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(line.R, line.G, line.B, alpha)));
        ctx.PopAxisAlignedClip();

        bool nearWhite = line.R > 0.9f && line.G > 0.9f && line.B > 0.9f;
        if (nearWhite && !_dark)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, new Color4(0f, 0f, 0f, 0.2f * alpha)), 1f);
    }

    /// <summary>
    /// 画白板色带上的一段（段表见 <see cref="BoardBand"/>）。六种段各有各的画法：
    ///   · **上一屏 / 下一屏**：圆角小格 ＋ 上下箭头；到顶了"上一屏"压暗（反馈在这里给）；
    ///   · **第 N 屏**：**光写字，不画框、不响应悬停**——它夹在两个箭头中间，和它们是一体的；
    ///   · **板色**：直接把那块板的颜色铺出来（颜色本身就是内容，写字反而多余），
    ///     当前板色描一圈品牌色 ＋ 里面再套一圈白（第二重标记，不靠颜色单独区分）；
    ///   · **底纹 / 间距**：文字写"现在在哪一档" ＋ 右边一竖列**档位点**。
    ///
    /// 底纹 / 间距这两格是**循环档**（点一下换下一档），这套手感是它们原来在「更多」抽屉里
    /// 就有的，搬过来没改。档位点是 2026-09-20 在图形那一格上定的规矩：
    /// **只换文字不给点的话，"这一格还能点"是隐形的**。
    /// </summary>
    private void DrawBoardSegment(ID2D1DeviceContext ctx, int i, in RectF r, in UiState st)
    {
        var seg = BoardSegAt(i);
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, 8f, 8f);
        bool hover = _hover == 200 + i;

        switch (seg.Kind)
        {
            case BoardSegKind.PageUp:
            case BoardSegKind.PageDown:
            {
                // 这条判据**与放映状态无关**——这一格只管白板（见 ActivateSegment 的说明）。
                bool enabled = _host.State.Board
                               && (seg.Kind == BoardSegKind.PageDown || _host.State.CanFlipPageUp);
                var fg = enabled ? InkCol : new Color4(InkCol.R, InkCol.G, InkCol.B, 0.30f);
                if (hover && enabled) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
                IconAtlas.DrawCentered(ctx,
                    seg.Kind == BoardSegKind.PageUp ? "chevronUp" : "chevronDown",
                    r, 16f, Brush(ctx, fg));
                return;
            }

            case BoardSegKind.PageLabel:
                _widgets.Text(ctx, $"第 {st.ScreenIndex} 屏", r, 12.5f, Brush(ctx, InkCol));
                return;

            case BoardSegKind.Color:
            {
                ctx.FillRoundedRectangle(rr, Brush(ctx, InkPalette.BoardPresets[seg.Idx].Color));
                bool active = BoardColorIs(st, seg.Idx);
                ctx.DrawRoundedRectangle(rr, active ? Brush(ctx, Tokens.Accent) : Brush(ctx, BorderCol),
                                         active ? 2f : 1f);
                if (active)
                {
                    var inner = new Vortice.RawRectF(r.MinX + 2, r.MinY + 2, r.MaxX - 2, r.MaxY - 2);
                    ctx.DrawRoundedRectangle(new RoundedRectangle(inner, 6f, 6f),
                                             Brush(ctx, Tokens.AccentInk), 1.5f);
                }
                return;
            }

            default:                       // 底纹 / 间距
            {
                if (hover) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

                // 右边留一条给竖排的档位点（宽度和图形那一格同一个 12，见 DrawSegment）
                const float PipStripW = 12f;
                var textBox = new RectF
                {
                    MinX = r.MinX, MinY = r.MinY, MaxX = r.MaxX - PipStripW, MaxY = r.MaxY,
                };
                string label = seg.Kind == BoardSegKind.Pattern
                    ? PatternName(st.BoardPattern)
                    : $"{st.BoardPatternStep:F0}";
                _widgets.Text(ctx, label, textBox, 12.5f, Brush(ctx, InkCol));

                var (count, cur) = BoardPips(seg.Kind, st);
                DrawPips(ctx, r.MaxX - PipStripW * 0.5f, (r.MinY + r.MaxY) * 0.5f, cur, count, InkCol);
                return;
            }
        }
    }

    private void DrawSegment(ID2D1DeviceContext ctx, int i, int count, in UiState st)
    {
        var r = SegmentRect(i, count);
        bool active = IsSegmentActive(st, i);
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, 8f, 8f);

        // 白板那一格：段表说了算（见 BoardBand / DrawBoardSegment）
        if (_bandCell == 2) { DrawBoardSegment(ctx, i, r, st); return; }

        if (active)
        {
            ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
        }
        else if (_hover == 200 + i || _press == 200 + i)
        {
            ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        }
        ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);

        string label = SegmentLabel(i);
        if (label.Length > 0)
        {
            _widgets.Text(ctx, label, r, (r.MaxX - r.MinX) < 62f ? 11f : 12.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
            return;
        }

        var segInk = active ? Tokens.AccentInk : InkCol;
        // **有档位的那几格**（点它一下换一档，见 ActivateSegment 的 case 8）：
        //   · 「直线」= 3 档线型（实 / 虚 / 点）；
        //   · 「抛物线」= 2 档（上下 / 左右）；
        //   · 「双曲线」= 2 档（有 / 无渐近线）、「椭圆」= 2 档（有 / 无焦点三角形）；
        //   · 「坐标系」= 2 档（带网格 / 不带网格，2026-09-24 从「更多」抽屉挪进来的）；
        //   · 「棱柱 / 棱锥 / 棱台」= 4 档边数（三 / 四 / 五 / 六）。
        // 图标照旧画当前那一档，**右边再加一竖列档位点**——大而浓的那个是当前档。
        // 用户 2026-09-20 定：只换图标的话，老师"不知道这一格还能点"（可选的状态是隐形的）。
        // 抛物线那格是**同一天稍后补上的**（用户："抛物线页增加小圆点"）——
        // 它从加进来那天起就是"再点一次换一档"，却一直没给点，是漏的。
        //
        // ⚠ **三格各有各的档**（`st.SidesOf(tool)`）：第一版三格共用一个数，结果是
        // "点棱锥那一格，棱柱、棱台的点跟着一起动"——用户上手就报了这个。
        // 档位点画的是**这一格自己的档**，所以这里必须问 `SidesOf(segTool)`，
        // 而不是随手读一个 `st.PrismSides`。
        //
        // **点从"图标下面"挪到了"图标右边"**（还是 2026-09-20，用户看出来的）：
        // 图形段是"宽 × 26"的长方形，而图标只占 18 —— 横排放在下面的时候，为了挤出
        // 那 7 像素高，图标得**压到 16 并整体上移 3.5**；竖着放到右边之后那一列点
        // 只占约 4 宽，图标就能回到 18 并留在正中。
        //
        // **"这一格有几档、现在是第几档"只有 `PipsOf` 那一处**（绘制与自检共用）：
        // 在这里再列一遍工具名，加一种图形就会漏一处。
        var segTool = ShapeToolAt(i);
        var (pipCount, pipCur) = PipsOf(segTool, st);
        if (pipCount > 0)
        {
            // 右边让出这么宽的一条给竖排的点（点距/半径在 `DrawPips` 里，最浓的那个半径 2，
            //  所以这一列实际占 ~4 宽、居中在这条带的中间）。
            const float PipStripW = 12f;
            var iconBox = new RectF
            {
                MinX = r.MinX, MinY = r.MinY, MaxX = r.MaxX - PipStripW, MaxY = r.MaxY,
            };
            IconAtlas.DrawCentered(ctx, ShapeIcon(segTool), iconBox, 18f, Brush(ctx, segInk));
            DrawPips(ctx, r.MaxX - PipStripW * 0.5f, (r.MinY + r.MaxY) * 0.5f, pipCur, pipCount, segInk);
            return;
        }

        IconAtlas.DrawCentered(ctx, ShapeIcon(segTool), r, 18f, Brush(ctx, segInk));
    }

    /// <summary>
    /// 这一格右边该点**几个档位点**、第几个是当前档（`Count = 0` ＝ 这一格没有档位，
    /// 绘制那边就照原样画大图标）。**判据只有这一处**——绘制和自检都问它：
    ///   · 「直线」= 3 档线型，当前档就是 <c>LineDash</c>；
    ///   · 「抛物线」= **2 档**（上下 / 左右），当前档按 <see cref="ParabolaAxisIndex"/> 折算
    ///     ——注意 `CurveAxis` 本身是四个值（上下左右各有两向），面板这一格只管"哪一对"；
    ///   · 「棱柱 / 棱锥 / 棱台」= 4 档边数，**三格各记各的**（见 `UiState.SidesOf`）；
    ///   · 别的图形 0（它们还没有第二档）。
    /// 档位范围**来自引擎**（`st.SolidMin/MaxSides`）——`Stroke` 是引擎内部类型，
    /// 界面看不到它，也不该在这里写死一份 3/6。
    /// </summary>
    private static (int Count, int Current) PipsOf(Tool tool, in UiState st)
    {
        if (tool == Tool.Line) return (3, (int)st.LineDash);
        if (tool == Tool.Parabola) return (2, ParabolaAxisIndex(st.ParabolaAxis));
        // **双曲线**（2026-09-22）：2 档 = 有 / 无渐近线。
        // 顺序按"播放先后"排：第 1 档 = 有渐近线（默认）、第 2 档 = 无。
        if (tool == Tool.Hyperbola) return (2, st.HyperbolaAsymptotes ? 0 : 1);
        // **椭圆（带焦点）**（2026-09-22）：2 档 = 有 / 无焦点三角形（默认有）。
        if (tool == Tool.ConicEllipse) return (2, st.EllipseFocusTriangle ? 0 : 1);
        // **坐标系**（2026-09-24）：2 档 = 带网格 / 不带网格。
        // 用户那天原话："我打算把它挪到图形里面的那个坐标系……点一下切换成网格，点一下网格没了"，
        // 紧接着又说"它档位之间是有切换按钮的，你可以参照一下其他那个切换逻辑"——
        // 说的就是这个**档位点**：只换图标不给点的话，"这一格还能点"是隐形的
        //（用户 2026-09-20 就为抛物线补过一次，见上面那段）。
        // 顺序按"播放先后"排：第 1 档 = 不带网格（默认，就是画出来没格子的那个）、第 2 档 = 带网格。
        if (tool == Tool.Coordinate) return (2, st.CoordGridDefault ? 1 : 0);
        if (ShapeSpec.HasSideCount(tool))
            return (st.SolidMaxSides - st.SolidMinSides + 1,
                    Math.Clamp(st.SidesOf(tool), st.SolidMinSides, st.SolidMaxSides) - st.SolidMinSides);
        return (0, 0);
    }

    /// <summary>
    /// 画一**竖列档位点**：告诉老师"这一格有几档、现在是第几档"（从上往下 = 第 1 档 → 第 n 档）。
    ///
    /// 当前档用**大 + 浓**两个差别，其余的小一半、透明度 35%：
    /// 用"大小"而不是只用颜色，是因为这一段可能是**选中态**（整个格子铺着品牌色、
    /// 前景是白的），那时候用颜色区分就完全失效了。
    ///
    /// 点距 5、半径 2 / 1.5：4 个点竖排连起来占 19 高，段高 26 里塞得下（上下各余 3.5），
    /// 横着只占 4 宽——这就是"点挪到图标右边"能省出来给图标的那点地方（见 `DrawSegment`）。
    /// </summary>
    private void DrawPips(ID2D1DeviceContext ctx, float cx, float cy,
                          int cur, int count, Color4 fg)
    {
        const float gap = 5f;
        float y0 = cy - (count - 1) * gap * 0.5f;
        for (int k = 0; k < count; k++)
        {
            bool on = k == cur;
            var c = on ? fg : new Color4(fg.R, fg.G, fg.B, 0.35f);
            float rad = on ? 2f : 1.5f;
            ctx.FillEllipse(new Ellipse(new Vector2(cx, y0 + k * gap), rad, rad), Brush(ctx, c));
        }
    }

    /// <summary>
    /// 图形那一排**用图标不用文字**（照假面板）：图形的轮廓比"直线/矩形/椭圆"几个字
    /// 一眼得多，而且不用为几个字去量宽度（七段之后每段只有 ~79 像素，写"平行四边形"
    /// 四个字根本放不下）。
    /// </summary>
    private string ShapeIcon(int i) => ShapeIcon(ShapeToolAt(i));

    /// <summary>
    /// **图形种类 → 图标名**（带上状态的那一份）：目前几处跟状态有关——
    /// 抛物线要**转成当前开口方向**（见 <see cref="ParabolaIconName"/>）、
    /// 直线要**换成当前线型**（见 <see cref="LineIconName"/>）、
    /// 棱柱 / 棱锥 / 棱台要**换成当前档的边数**（见 <see cref="SolidIconName"/>）、
    /// 坐标系要**换成"带不带网格"**（2026-09-24，见下面那一行）。
    ///
    /// 为什么非跟状态不可：这几格"点第二下换一档"，图标不跟着换的话，
    /// 老师看不出那一下到底有没有生效（三处都是用户 2026-09-20 定的）。
    /// </summary>
    private string ShapeIcon(Tool t) => t switch
    {
        Tool.Parabola => ParabolaIconName(_host.State.ParabolaAxis),
        Tool.Line => LineIconName(_host.State.LineDash),
        // 双曲线：有 / 无渐近线两张（2026-09-22）——那一格"再点一次换一档"，
        // 图标不跟着换的话，老师看不出这一笔到底会不会带那两条虚线。
        Tool.Hyperbola => _host.State.HyperbolaAsymptotes ? "hyperbola" : "hyperbolaNoAsym",
        // 椭圆（带焦点）：有 / 无焦点三角形两张（2026-09-22），理由同上。
        Tool.ConicEllipse => _host.State.EllipseFocusTriangle ? "ovalFocusTri" : "ovalFocus",
        // 坐标系：带网格 / 不带网格两张（2026-09-24）——这一档从「更多」抽屉挪到了这一格
        //（用户："点一下切换成网格，点一下网格没了"），图标同样必须跟着换，否则
        // "这一笔画出来带不带格子"看不出来。显示的是**"以后新画的那些"**那一档
        //（已经画在板上的各存各的，见 Stroke.Grid）。
        Tool.Coordinate => _host.State.CoordGridDefault ? "axesGrid" : "axes",
        Tool.Star => "star",
        _ when ShapeSpec.HasSideCount(t) => SolidIconName(t, _host.State.SidesOf(t)),
        _ => ShapeIconFor(t),
    };

    /// <summary>
    /// 立体图形那一族的图标名：`prism3` ～ `frustum6`（前缀按工具、后缀按当前档边数）。
    ///
    /// 和直线 / 抛物线同一条理由：那几格"再点一次换一档"，图标不跟着换就看不出来。
    /// ⚠ **18 像素下"五"和"六"、以及棱柱 / 棱台**可能不太分得开，所以那一格右边
    /// 还有 **4 个档位点**兜底（见 `DrawPips`）——数点比数边可靠。
    /// </summary>
    private static string SolidIconName(Tool t, int sides)
    {
        string stem = t switch
        {
            Tool.Pyramid => "pyramid",
            Tool.Frustum => "frustum",
            _ => "prism",                              // 棱柱（也是兜底）
        };
        return stem + Math.Clamp(sides, 3, 6);
    }

    /// <summary>
    /// 抛物线的图标名按**当前档位**换（`parabola` = 上下抛物 / `parabolaRight` = 左右抛物，
    /// 见 <see cref="IconAtlas.Draw"/>）。
    ///
    /// 为什么非得变：用户 2026-09-20 把"上下还是左右"从选中框挪到了**画之前**定
    /// （那格已经选中抛物线时再点一次，见 `ActivateSegment` 的 case 8）。
    /// 图标不跟着换的话，"点第二下到底有没有生效"就没法看出来。
    ///
    /// 只有两个名字：**具体朝哪边由画的时候那一拖定**（用户 2026-09-20 更晚的口径：
    /// "感觉不对，还是参考他的逻辑"；InkClass 也是两个按钮 `case 20/21`）。
    /// </summary>
    private static string ParabolaIconName(CurveAxis axis)
        => ParabolaAxisIndex(axis) == 1 ? "parabolaRight" : "parabola";

    /// <summary>
    /// 抛物线那一格**两档**里的第几档（0 = 上下抛物、1 = 左右抛物）。
    ///
    /// 为什么要有它：`CurveAxis` 是**四个值**（上下左右各一个方向，具体朝哪边由画的时候
    /// 那一拖定），而面板这一格只管"**哪一对**"。所以"图标画哪张"和"档位点点第几个"
    /// 都得先把四个值折成两档——**这个折算只有这一处**，两处各写一遍迟早对不上
    /// （表现是"图标换了、点没跟着动"）。
    /// </summary>
    private static int ParabolaAxisIndex(CurveAxis axis)
        => axis is CurveAxis.OpenRight or CurveAxis.OpenLeft ? 1 : 0;

    /// <summary>
    /// 直线的图标名按**当前线型**换（`line` / `lineDash` / `lineDot`，见 <see cref="IconAtlas.Draw"/>）。
    ///
    /// 和抛物线那条同一个理由：那一格"再点一次换一档"（用户 2026-09-20 定：
    /// "点击直线的图标，它会变成虚线，再点击变成点虚线，再点击又变成直线……省了好几个空间格"），
    /// 图标不跟着换的话，老师点完看不出下一笔会是实线还是虚线。
    ///
    /// 三张都是**自绘**的（`IconAtlas` 里同一根线只换画法）——上游图标库里一个虚线专名都没有
    /// （连 `lineWeight` 都只有实线那一张），而且三张必须**一眼看出是同一根线的三档**，
    /// 凑上游的三张图反而会花。
    /// </summary>
    private static string LineIconName(StrokeDash dash) => dash switch
    {
        StrokeDash.Dashed => "lineDash",
        StrokeDash.Dotted => "lineDot",
        _ => "line",                               // 实线（也是兜底）
    };

    /// <summary>
    /// **图形种类 → 图标名**。上带那几段和主条"图形"那一格**共用这一份**：
    /// 主条上显示的必须就是当前种类的形状，两处各写一份迟早对不上
    /// （表现是"上带里点了三角形，主条那格还是矩形"）。
    ///
    /// 名字对不上的那几个是自绘的（`oval` / `parallelogram` / `axes` / `numberline`、
    /// 2026-09-20 加的四种曲线 `parabola` / `hyperbola` / `sine` / `cosine`，
    /// 以及直线的三档线型 `line` / `lineDash` / `lineDot`），
    /// 见 <see cref="IconAtlas.Draw"/>：上游图标库里没有这些专名。
    /// </summary>
    private static string ShapeIconFor(Tool t) => t switch
    {
        Tool.Rectangle => "square",
        Tool.Ellipse => "oval",                    // 自绘：Fluent 只有正圆
        Tool.Circle => "circle",
        Tool.Triangle => "triangle",
        Tool.Trapezoid => "trapezoid",
        Tool.Sector => "sector",
        Tool.Star => "star",
        Tool.Parallelogram => "parallelogram",     // 自绘：Fluent 没有这个专名
        // 坐标系 / 数轴也是自绘的（见 IconAtlas.DrawAxes / DrawNumberLine）：
        // 上游图标库里没有"两条轴"和"一条带刻度的轴"这两个专名。
        Tool.Coordinate => "axes",
        Tool.SpaceCoordinate => "spaceaxes",
        Tool.NumberLine => "numberline",
        // 四种曲线：同样自绘（见 IconAtlas.DrawParabola / DrawHyperbola / DrawWave）。
        // 图标画的是"理想样子"（开口向上的抛物线 / a = b 的双曲线 / 一个周期）。
        // **抛物线那一张会跟着当前开口方向转**——它不是这一个名字的事：
        // `ShapeIcon(Tool)` 会替它换成 `parabolaRight` / `parabolaDown` / `parabolaLeft`，
        // 所以这里给的是"默认（开口向上）"那一档，也是名字不对时的兜底。
        Tool.Parabola => "parabola",
        Tool.Hyperbola => "hyperbola",
        // 椭圆（带焦点）（2026-09-22）：自绘（见 IconAtlas.DrawOvalFocus）——椭圆 ＋ 两焦点
        // ＋（默认那一档）焦点三角形。具体哪一张由 `ShapeIcon(Tool)` 按当前档换
        //（见那里的注释），这里是**默认（有焦点三角形）**那张，也是认不出来时的兜底。
        Tool.ConicEllipse => "ovalFocusTri",
        Tool.Sine => "sine",
        Tool.Cosine => "cosine",
        // 波浪线（第十六批）：自绘（见 IconAtlas.DrawWaveLine）——**好几个周期**的正弦波，
        // 和「正弦」那一张（一个周期）一眼能分开。
        Tool.Wave => "wave",
        // 正切（第十五批）：自绘（见 IconAtlas.DrawTangent）——一支曲线 ＋ 两条渐近线。
        Tool.Tangent => "tangent",
        // 立体图形（2026-09-20 第五批）：同样自绘（见 IconAtlas.DrawCylinder / DrawCone 等）。
        Tool.Cylinder => "cylinder",
        Tool.Cone => "cone",
        // 圆台（第十三批）：自绘的第三张旋转体图标（见 IconAtlas.DrawConeFrustum）。
        Tool.ConeFrustum => "conefrustum",
        // 球（第十四批）：自绘（见 IconAtlas.DrawSphere）——轮廓圆 ＋ 赤道椭圆。
        Tool.Sphere => "sphere",
        // ⚠ 长方体 / 四面体 2026-09-20 第十二批**撤了面板入口**（见 ShapeRows 里立体那行），
        // 但它们的两张图标留着——主条那一格在"选中的是旧板书里的一个长方体"时还要画它。
        Tool.Cuboid => "cuboid",
        Tool.Tetrahedron => "tetrahedron",
        // 棱柱 / 棱锥 / 棱台：具体哪一张由 `ShapeIcon(Tool)` 按当前档换（见 SolidIconName）。
        // 这里是**默认（四棱）**那张，也是认不出来的兜底。
        Tool.Prism => "prism4",
        Tool.Pyramid => "pyramid4",
        Tool.Frustum => "frustum4",
        Tool.Arrow => "arrowRight",
        // 直线：自绘三张（实线 / 虚线 / 点线，见 IconAtlas）。这里给的是**实线**那一张，
        // 具体哪一张由 `ShapeIcon(Tool)` 按当前线型换（见 LineIconName）。
        Tool.Line => "line",
        _ => "lineWeight",                         // 认不出来的兜底
    };

    private string SegmentLabel(int i) => _bandCell switch
    {
        // 窄档两字：像素 / 整笔 / 清空（绝不缩成单个“清”，歧义太大）。
        // 顺序按用户优先级：像素橡皮第一，整笔橡皮第二。
        6 => Density <= BandDensity.Compact
                ? (i == 0 ? "像素" : "整笔")
                : (i == 0 ? "像素橡皮" : "整笔橡皮"),
        8 => i == 0 ? "矩形" : "套索",
        // 截图：名字照微信那套（8.3.1）——"截图" = 屏幕原样（连我们的板书一起），
        // "隐藏窗口截图" = 先把我们这层藏起来，拍到的只有下层内容。
        9 => i == 0 ? "截图"
                    : (Density <= BandDensity.Compact ? "隐窗截图" : "隐藏窗口截图"),
        _ => "",
    };

    private bool IsSegmentActive(in UiState st, int i) => _bandCell switch
    {
        2 => BoardSegAt(i).Kind == BoardSegKind.Color && BoardColorIs(st, BoardSegAt(i).Idx),
        6 => i == 0 ? st.Tool == Tool.PixelEraser : st.Tool == Tool.Eraser,
        8 => i == 0 ? st.SelectMode == SelectMode.Rect : st.SelectMode == SelectMode.Lasso,
        10 => i >= 0 && i < ShapeSegmentCount && st.Tool == ShapeToolAt(i),
        9 => i == (st.CaptureHideInk ? 1 : 0),    // 截图：当前是哪一种截法就高亮哪一段
        _ => false,
    };

    private static bool BoardColorIs(in UiState st, int i)
    {
        var c = InkPalette.BoardPresets[i].Color;
        return MathF.Abs(c.R - st.BoardColor.R) < 0.02f
            && MathF.Abs(c.G - st.BoardColor.G) < 0.02f
            && MathF.Abs(c.B - st.BoardColor.B) < 0.02f;
    }

    private bool IsOn(int i) => Rows[i].Kind switch
    {
        Row.DarkTheme => _dark,
        // 色带常开（2026-10-05）：同贴边隐藏，界面自己的偏好。
        Row.RailPin => _railPinned,
        // 停顿成型：**默认开**，所以配置里没有这一项时显示的就是"开"
        //（见 LoadPrefs 里那一行：只有读到 "0" 才关）。
        Row.DwellShape => _host == null || _host.State.DwellShapeOn,
        // 压感粗细：状态在**引擎**（渲染期开关），界面只是显示它
        Row.Pressure => _host == null || _host.State.PressureOn,
        // 笔锋：状态在**引擎**（渲染期开关），界面只是显示它
        Row.Taper => _host == null || _host.State.TaperOn,
        // 墨迹预测：同样在引擎（默认关，2026-10-16 起），界面只是显示它
        Row.Predict => _host == null || _host.State.PredictTailOn,
        // 设备与性能统计：引擎是权威（首次弹窗同意/设置页切换）。
        Row.Privacy => _host != null && _host.State.PrivacyConsentOn,
        // 墨迹两条开关：读偏好（restoreInk 默认关、pptAutoSave 默认开）。
        Row.RestoreInk => _host.GetPref("restoreInk") == "1",
        Row.PptAutoSave => _host.GetPref("pptAutoSave") != "0",
        Row.AutoStart => _host != null && _host.State.AutoStartOn,
        // 「箭头清屏」：引擎持有（只在桌面批注生效，界面只显示开关）。
        Row.ClearOnArrow => _host != null && _host.State.ClearOnArrow,
        _ => _hideEnabled,
    };

    private static string ProfileName(int i) => i switch { 0 => "极简", 1 => "自定义", _ => "完整" };

    /// <summary>开关：打开的用强调色，关的是浅底 + 描边；滑钮在右/左。</summary>
    private void DrawSwitch(ID2D1DeviceContext ctx, RectF r, bool on)
    {
        float radius = (r.MaxY - r.MinY) * 0.5f;
        var box = new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY);
        var rr = new RoundedRectangle(box, radius, radius);
        ctx.FillRoundedRectangle(rr, Brush(ctx, on ? Tokens.Accent : HoverCol));
        if (!on) ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        float k = 14f;
        float cx = on ? r.MaxX - k * 0.5f - 3f : r.MinX + k * 0.5f + 3f;
        var c = new Vector2(cx, (r.MinY + r.MaxY) * 0.5f);
        ctx.FillEllipse(new Ellipse(c, k * 0.5f, k * 0.5f), Brush(ctx, Tokens.AccentInk));
    }

    /// <summary>画第 pos 个显示出来的格子（pos 是"当前档位里的序号"，i 是完整档下标）。</summary>
    private void DrawCell(ID2D1DeviceContext ctx, int pos, in UiState st)
    {
        var vis = VisibleCells();
        int i = vis[pos];
        var r = CellRect(pos);
        bool active = IsActive(i, st);
        bool hover = _hover == i || _press == i;

        if (i > 0)
        {
            // 分隔线画在"组变了"的地方——按**显示出来的邻居**判，不是按完整档的下标
            if (pos + 1 < vis.Length && GroupOf(i) != GroupOf(vis[pos + 1]))
            {
                float x = r.MaxX + Tokens.GroupDivider * 0.5f;
                ctx.DrawLine(new Vector2(x, r.MinY + 10), new Vector2(x, r.MaxY - 10),
                             Brush(ctx, BorderCol), 1f);
            }

            if (active)
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, Tokens.Accent));
            }
            else if (hover && !CellUnavailable(i, st))
            {
                var bg = new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4);
                ctx.FillRoundedRectangle(new RoundedRectangle(bg, 8f, 8f), Brush(ctx, HoverCol));
            }
        }

        if (i == 0)
        {
            // 收起格：一个小球 + 当前笔色那圈（和收起态那个球是同一个东西，只是变小）
            var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
            float d = 32f;
            var ring = new Ellipse(c, d * 0.5f, d * 0.5f);
            ctx.DrawEllipse(ring, Brush(ctx, st.PaletteBase), 2.5f);
            IconAtlas.DrawCentered(ctx, "pen", r, 16f, Brush(ctx, InkCol));
            return;
        }

        // **图形那一格（8）的图标**：**这一格亮着（手里就是某种图形）才跟着当前种类走**；
        // 没亮的时候画 Fluent 那个通用的"两个图形叠在一起"（`Cells[8].Icon`）。
        //
        // 为什么加了后半个条件（用户 2026-09-22 定）：这一格原来**任何时候**都画当前种类，
        // 于是"上次用过矩形"之后，手里明明是笔，图标还画着一个矩形——没选中就是通用的
        // 图形入口，画成某一种具体的图形，不熟悉的人会以为"这一格就是矩形"。
        // 选中之后跟着种类变的那半条照旧（2026-09-19 定的理由）：七种图形挤在一格里，
        // "shapes" 那个通用图标什么也没说——手里是三角形还是平行四边形，只能打开上带才知道。
        // 所以现在是**当前那一种**（和上带里高亮的那一段同一张图，共用 ShapeIconFor）。
        //
        // 代价（明确接受）：七种图形**都没有 filled 变体**（Fluent 表里没有生成），
        // 所以选中态不再像别的格那样变实心，而是"同一个轮廓 + 强调色底 + 白图标"
        // ——和上带里选中的那一段是同一种画法。
        var ink = active ? Tokens.AccentInk : InkCol;
        // **撤销/重做栈空 → 压暗**（用户 2026-09-17："撤销重做灰度"）。
        // 引擎早就把 UndoDepth / RedoDepth 递给界面了，只是界面一直没用。
        // 压暗而不是藏起来：位置固定、老师不用去找；点它也没事（引擎那边是空操作）。
        if (CellUnavailable(i, st)) ink = new Color4(ink.R, ink.G, ink.B, 0.30f);
        // 垃圾桶是“点一下立刻执行”的动作格，没有设置条可看；点击后闪一圈强调色，
        // 空页清空时老师也能确认“点到了”（否则只有状态行变化，屏幕上像没反应）。
        if (i == (int)Cell.Trash && _host != null && _host.NowMs < _actionFlashUntil)
            ctx.DrawRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(r.MinX + 2, r.MinY + 4, r.MaxX - 2, r.MaxY - 4), 8f, 8f),
                Brush(ctx, Tokens.Accent), 2f);
        if (PerfSkipIcons) return;
        DrawCellIcon(ctx, i, r, Tokens.Icon, ink, active, st);
    }

    /// <summary>
    /// **画某一格的图标**——主条（<see cref="DrawCell"/>）和「更多」面板里那排钉住宫格
    /// **共用这一处**。
    ///
    /// 为什么必须收成一处：用户 2026-09-26 特意提醒"更多里面有一个设置，那里面的图标也要同步起来"。
    /// 当年两处各写了一遍（弹出面板那句只认 `Cells[cell].Icon`），于是白板那一格在带子上是自绘的板、
    /// 在面板里还是 Fluent 那个"窗口布局"；橡皮 / 选择这一轮换了图标之后也会立刻再犯一次。
    /// 仓库里"同一个名单写两处、改一处必漏一处"已经栽过好几次（见 架构-分层与规则.md 五-7），
    /// 所以这不是"顺手合并"，是修那个毛病本身。
    ///
    /// <paramref name="active"/>：这一格亮不亮（面板里恒为 false——那里的格都不是选中态）。
    /// </summary>
    private void DrawCellIcon(ID2D1DeviceContext ctx, int cell, RectF r, float size,
                              in Color4 ink, bool active, in UiState st)
    {
        var brush = Brush(ctx, ink);
        switch (cell)
        {
            // 箭头 + 「箭头清屏」：箭头保持原样，右上角加一个小圆点
            // （2026-11-12 用户从三版里选的：比圆点大一圈、勉强能看清，又不抢箭头）。
            case 1 when st.ClearOnArrowActive:
            {
                IconAtlas.DrawCentered(ctx, CellIconName(1, st), r, size, brush);
                float ix = (r.MinX + r.MaxX) * 0.5f - size * 0.5f;
                float iy = (r.MinY + r.MaxY) * 0.5f - size * 0.5f;
                var c = new Vector2(ix + 20.0f / 24f * size, iy + 4.6f / 24f * size);
                float rad = 1.9f / 24f * size;
                ctx.FillEllipse(new Ellipse(c, rad, rad), brush);
                return;
            }
            // 白板：板开着的时候板面填成**这块板的颜色**（见 IconAtlas.DrawBoard）
            case 2:
                IconAtlas.DrawBoard(ctx, r, size, brush, active ? st.BoardColor : null);
                return;
            // 激光笔：那两张（见 IconAtlas.DrawLaserCell）
            case 5:
                IconAtlas.DrawLaserCell(ctx, r, size, brush, active);
                return;
            default:
                IconAtlas.DrawCentered(ctx, CellIconName(cell, st), r, size, brush);
                return;
        }
    }

    /// <summary>
    /// **这一格画哪个图标名**——"格子 → 图标"的**唯一一处判据**：
    /// 绘制（<see cref="DrawCellIcon"/>）和自检（<see cref="CellIconForTest"/>）都问它，
    /// 免得又出现"自检说画的是 A、屏幕上其实是 B"（那条 ⚠ 见 CellIconForTest）。
    /// 白板 / 激光那两格要额外参数（板色、两态），不在这张表里，由 DrawCellIcon 直接调绘制。
    ///
    /// 2026-09-26 用户挑的这一轮：
    ///   · **橡皮（6）**：未选中＝`eraserPure`（**Radix 那张纯橡皮**，MIT，15 网格；
    ///     用户 2026-09-26 从候选里挑的 B2——理由是这一族里它路径中**一根横线都没有**）；
    ///     选中＝跟着当前是哪种橡皮换：
    ///     整笔擦＝`eraser`（Fluent 原版，带横线）、面积擦＝`eraserMedium`（橡皮＋一个大圈）；
    ///   · **选择（7）**：未选中＝`lucideSelectPointer`（Lucide 虚线框＋指针）；
    ///     选中＝框选 `mdiSelection`（MDI 四角括号＋断续边）/ 套索 `lasso`（Fluent）。
    /// </summary>
    private string CellIconName(int cell, in UiState st) => cell switch
    {
        // 箭头那一格：**文档模式里画手掌**（2026-11-15：PDF 像放映——点它拖页，不是穿透）。
        // 文档没开时才回到那两张鼠标箭头（穿透：常态描边 / 选中实心）。
        1 => st.DocOpen
                ? (IsActive(1, st) ? "handRightFilled" : "handRight")
                : (IsActive(1, st) ? Cells[1].Filled : Cells[1].Icon),
        // 图形那一格：亮着（手里就是某种图形）才跟着当前种类走，没亮画通用的那张（见 DrawCell 那段）
        10 => IsActive(10, st) ? ShapeIcon(st.Tool) : Cells[10].Icon,
        6 => IsActive(6, st)
                ? (st.Tool == Tool.PixelEraser ? "eraserMedium" : "eraser")
                : "eraserPure",
        8 => IsActive(8, st)
                ? (st.SelectMode == SelectMode.Lasso ? "lasso" : "mdiSelection")
                : "lucideSelectPointer",
        _ => IsActive(cell, st) ? Cells[cell].Filled : Cells[cell].Icon,
    };

    /// <summary>
    /// 这一格现在算不算"选中的"。
    ///
    /// **穿透和工具是互斥的**（用户 2026-09-17 定）：穿透开着的时候点击落到下层程序上，
    /// 画布根本收不到笔。所以这时候工具格一律不高亮——不然会出现"鼠标"和"笔"同时亮着，
    /// 老师以为在写字、写出来一个字都没有。
    ///
    /// 白板（2）是例外：它表示的是"板开着"这个事实，和能不能写字无关，穿透时照常显示。
    /// 引擎那边配合着改了：**换工具会自动关掉穿透**（`InkEngine.SwitchTool`），
    /// 所以点一下工具格就能立刻写字，不用先去点"鼠标"把它关掉。
    /// </summary>
    private bool IsActive(int i, in UiState st)
    {
        // 文档模式里那一格是**手掌**（不是穿透）：亮不亮看文档手掌开没开。
        if (i == 1) return st.DocOpen ? st.DocPanMode : st.PassThrough;
        if (i == 2) return st.Board;
        if (i == 11) return st.LibraryOpen && !st.LibraryShapesTab;
        // 文档「手掌」开着 = 手里是"拖页"，工具格一律不高亮——和穿透同一条道理：
        // 亮着笔却一笔也画不出来，老师会以为坏了（2026-11-15）。
        if (st.DocPanMode) return false;
        if (st.PassThrough) return false;
        return i switch
        {
            3 => st.Tool == Tool.Pen,
            4 => st.Tool == Tool.Highlighter,
            5 => st.Tool == Tool.Laser,
            6 => st.Tool is Tool.Eraser or Tool.PixelEraser,
            7 => st.Tool == Tool.Text,
            8 => st.Tool == Tool.Marquee,
            9 => st.Tool == Tool.Capture,
            10 => st.LibraryOpen && st.LibraryShapesTab,
            _ => false,
        };
    }

    /// <summary>
    /// 这一格现在是不是"不可用"（暂时压暗的那种）。
    ///
    /// 现在只有一种：**撤销/重做栈空**。用户 2026-09-17 要的"灰度"。
    /// 判据直接用引擎给的 UndoDepth / RedoDepth——界面不去猜"上一次操作是不是能撤销"，
    /// 那种猜法迟早和引擎对不上。
    /// </summary>
    private static bool CellUnavailable(int i, in UiState st) =>
        (i == 12 && st.UndoDepth == 0) || (i == 13 && st.RedoDepth == 0);

    /// <summary>
    /// 画刷按颜色缓存。**不能每帧重建**（性能账里点过名：几何与画刷都要缓存）。
    /// 注意：设备丢失（Gfx 重建）之后这份缓存要重建——届时应重新 SetUiFactory 挂一遍界面。
    /// </summary>
    private ID2D1SolidColorBrush Brush(ID2D1DeviceContext ctx, Color4 c)
    {
        uint key = ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16)
                 | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = ctx.CreateSolidColorBrush(c, null);
        _brushes[key] = b;
        return b;
    }

    private void Invalidate()
    {
        _host?.InvalidateUi();
    }

    // ---- 悬停提示（Tooltip；2026-10-02）--------------------------------------
    //
    // 设计规格与调研见《调研-悬停提示-Tooltip.md》：
    //   · **停留 500ms 才出**（WPF `ToolTipService.InitialShowDelay` 默认 400ms，
    //     取松一档——扫过一排格子不闪）；移开 / 按下 / 拖动立刻收；
    //   · 内容 = **名称 + 当前键位 + 一句说明**；键位从引擎查（`IUiHost.KeyText`），
    //     界面不抄第二份，用户改了 settings.json 提示跟着变；
    //   · 画在整块面板的旁边（默认上方 8px，顶到屏幕就翻到下方），**不跟手、不遮指针**；
    //   · **触屏没有悬停**，所以它只是增强：面板上的常显文字一个字都不动。

    /// <summary>
    /// 这一刻"该不该出现提示"（**悬停那条路**；触摸长按有自己的开关，见 UpdateTip）。
    /// 悬停要求：开关开着、有目标、有文案、没在按/拖、也没有触摸长按正在计时。
    /// </summary>
    private bool TipWanted
        => _tipTarget != TipNoTarget
           && (_tipHoldFired
               || (_press == -1 && !_dragging && !_sliderDragging
                   && _tipHoldStart == double.NegativeInfinity))
           && TipContent(_tipTarget).Title != null;

    /// <summary>
    /// 手指/笔按住不动 = 长按候选（鼠标永不进这里）。到 600ms 由 <see cref="UpdateTip"/> 点亮。
    /// 只给"有提示文案、且长按没有被别的功能占用"的元素武装：滑条、
    /// PPT 页码格（长按菜单）都不会走到这儿（调用点已经排除）。
    /// </summary>
    private void ArmTipHold(int target)
    {
        _tipHoldStart = _host.NowMs;
        _tipHoldTarget = target;
        _tipHoldFired = false;
        _tipLingerUntil = 0;
    }

    /// <summary>撤销长按候选（移动超阈值、移开、开关关掉、新按下都走它）。</summary>
    private void CancelTipHold()
    {
        _tipHoldStart = double.NegativeInfinity;
        _tipHoldTarget = TipNoTarget;
        _tipHoldFired = false;
    }

    /// <summary>「更多」面板里这一刻的提示目标（空白/无 = TipNoTarget）。
    /// 编号 = 1000 + 命中码，和 `MoreHitAt` 一一对应（画、命中、提示同源）。</summary>
    private static int MoreTipTargetAt(int hit)
        => hit == MoreHitNone || hit == MoreHitInside ? TipNoTarget : 1000 + hit;

    /// <summary>指针停到某个目标上（target 用 `HoverAt` 那套编号；没有就传 TipNoTarget）。</summary>
    private void SetTipTarget(int target)
    {
        if (target == TipNoTarget) { HideTip(); return; }
        if (target == _tipTarget) return;
        _tipTarget = target;
        _tipSinceMs = _host?.NowMs ?? 0;
        _tipShown = false;
        _tipFade.Jump(0f);
        Invalidate();   // "等 500ms"从这一刻起算，要一帧帧跟到点
    }

    /// <summary>收起提示（移开、按下、拖动、关开关都用它）。</summary>
    private void HideTip()
    {
        bool hadLinger = _tipLingerUntil > 0;
        _tipLingerUntil = 0;
        if (_tipTarget == TipNoTarget && !_tipShown && _tipFade.Value <= 0f)
        {
            if (hadLinger) Invalidate();
            return;
        }
        _tipTarget = TipNoTarget;
        _tipShown = false;
        _tipFade.Jump(0f);
        Invalidate();
    }

    /// <summary>
    /// 每帧推进提示状态（Render 开头调一次，和 UpdatePeek / UpdateRail 同一个位置）。
    /// 三条路：① 触摸/笔长按到点 → 直接点亮；② 停留期到点 → 收；
    /// ③ 悬停延迟两档（首次/隔久了 1.5s；出过提示后 5 秒窗口内 400ms）。
    /// </summary>
    private void UpdateTip()
    {
        // "刚看过提示"窗口的刷新（2026-10-16）：亮着期间持续刷新——"刚收起"也算"刚看过"，
        // 下一个提示走快延迟（Tokens.TipFastDelayMs；WPF BetweenShowDelay 的口径）。
        if (_tipShown) _lastTipShownAtMs = _host.NowMs;

        // ① 长按到点：这一次按下就算"作废"了（松手不执行，见 PointerUp）
        if (_tipHoldStart > double.NegativeInfinity && !_tipHoldFired
            && _host.NowMs - _tipHoldStart >= Tokens.TipHoldMs)
        {
            _tipHoldFired = true;
            SetTipTarget(_tipHoldTarget);
            _tipShown = true;
            _lastTipShownAtMs = _host.NowMs;
            _tipFade.To(1f, Tokens.TipFadeMs);
            Invalidate();
        }

        // ② 松手后的停留到期 → 收（触摸长按专用；悬停提示是移开即收）
        if (_tipShown && _tipLingerUntil > 0 && _host.NowMs >= _tipLingerUntil)
        {
            HideTip();
            return;
        }

        if (!TipWanted)
        {
            if (_tipShown || _tipFade.Value > 0f)
            {
                _tipShown = false;
                _tipFade.Jump(0f);
            }
            return;
        }
        double tipDelay = _host.NowMs - _lastTipShownAtMs <= Tokens.TipBetweenWindowMs
            ? Tokens.TipFastDelayMs : Tokens.TipDelayMs;
        if (!_tipShown && _host.NowMs - _tipSinceMs >= tipDelay)
        {
            _tipShown = true;
            _lastTipShownAtMs = _host.NowMs;
            _tipFade.To(1f, Tokens.TipFadeMs);
        }
    }

    /// <summary>某个动作当前的键位文本（查引擎的键位表；没有绑定就空字符串）。</summary>
    private string Key(KeyAction a) => _host?.KeyText(a) ?? "";

    /// <summary>
    /// 提示文案：**名称 + 键位 + 一句说明**。`Title == null` = 这个东西不出提示。
    ///
    /// **范围是收窄过的**（2026-10-02 第二轮，见《调研-悬停提示-Tooltip.md》10.2/10.8）：
    /// 只有"图标-only / 带快捷键 / 隐藏手势 / 认不出来"的才配；已经写上字、点一下当场
    /// 见结果的一律不配——触摸长按会吞掉那一次点击，零信息的提示不值这个代价。判据：
    /// **这条提示有没有带来新信息**。
    ///
    /// 名字取"唯一来源"：图形名读 `ToolNames.Of`（引擎和界面共用），键位读
    /// `IUiHost.KeyText`——界面里不写死任何一个键。
    /// </summary>
    /// <summary>上带右端动作按钮的提示（「选择」那条是三个：全选 / 复制 / 粘贴）。</summary>
    private (string Title, string Key, string Note) ActionTip(int i)
    {
        return ActionAt(i) switch
        {
            BandAction.CloseBoard => ("关闭白板", Key(KeyAction.ToggleBoard), "只关白板，墨迹留着"),
            BandAction.SelectAll => ("全选", Key(KeyAction.SelectAll), "选中当前这一屏的全部对象"),
            BandAction.Copy => ("复制", Key(KeyAction.Copy), "复制选中对象到剪贴板；可粘到本软件或别的程序"),
            BandAction.Paste => ("粘贴", Key(KeyAction.Paste), "落在原件旁边 24px；剪贴板里是图就贴图"),
            _ => default,
        };
    }

    private (string Title, string Key, string Note) TipContent(int id)
    {
        // ---- 「更多」面板（编号 = 1000 + 命中码；2026-10-02 第二批）----------------
        // 面板是触摸主场，所以这些提示**鼠标悬停和手指长按都能出**。
        // 启动器格子和设置行原来那行小灰字已从画面撤掉，这里就是它们的去处。
        if (id >= 1000)
        {
            int hit = id - 1000;
            if (hit >= MoreHitTile && hit <= MoreHitTile + HubTileTotal - 1)
            {
                var (label, hint, _, _) = HubTileInfo(hit - MoreHitTile);
                return hint.Length == 0 ? default : (label, "", hint);
            }
            if (hit >= MoreHitBottom && hit < MoreHitBottom + MoreBottomTiles.Length)
            {
                var t = MoreBottomTiles[hit - MoreHitBottom];
                // 版本号是运行时拼进去的；模板留在表里，提示出口再格式化。
                return (t.Label, "", Loc.T(t.Hint, ProductInfo.Version));
            }
            // 「最近文档」页的行：全名 + 完整路径（名字被省略号截过，这里给全）
            if (hit >= MoreHitRecentPageRow && hit < MoreHitRecentPageRow + RecentPageMax * RecentPageGroups)
            {
                int slot = hit - MoreHitRecentPageRow;
                var rowView = RecentPageRow(slot / RecentPageMax, slot % RecentPageMax);
                if (!rowView.Clickable) return default;
                return (rowView.Title, "", rowView.Note);
            }
            if (hit >= MoreHitRow && hit < MoreHitRow + Rows.Length)
            {
                int i = hit - MoreHitRow;
                return (Rows[i].Label, "", Rows[i].Hint);
            }            if (hit >= MoreHitChip && hit < MoreHitChip + Cells.Length)
            {
                int cell = hit - MoreHitChip;
                return (Cells[cell].Tip, "",
                        _pinned[cell] ? "已钉在工具条上（点一下取消）" : "点一下钉到工具条上");
            }
            if (hit >= MoreHitProfile && hit < MoreHitProfile + 3)
                return (ProfileName(hit - MoreHitProfile), "", "切到这一档");
            if (hit == MoreHitBack) return ("返回", "", "回到启动器主页");
            if (hit == MoreHitClose) return ("关闭", "", "点面板外也能关");
            return default;
        }

        if (id >= 0 && id < Cells.Length)
        {
            var c = Cells[id];
            return id switch
            {
                // 0 号收起格、球（-2）：自解释，不配（收窄）
                // 1 号（鼠标 / 手掌）：文档模式里它**不是穿透**，是"拖页"（2026-11-15），
                // 提示必须跟着换——不然悬停写着"打开后点击落到底下的程序"，点下去却是拖页。
                1 => _host != null && _host.State.DocOpen
                    ? ("手掌（拖页）", "H / 空格",
                       _host.State.DocPanMode
                           ? "拖动画布上下看页；再点一次回到工具（H 也能关）"
                           : "点一下开手掌：拖动上下看页（不落墨）；H 开关、按住空格临时用")
                    : (_host != null && _host.State.ClearOnArrowActive ? "清屏并穿透" : c.Tip,
                       Key(KeyAction.TogglePassThrough),
                       _host != null && _host.State.ClearOnArrowActive
                           ? "清空桌面批注（Ctrl+Z 撤销）"
                           : "打开后点击落到底下的程序；再点回来是画笔"),
                2 => (c.Tip, Key(KeyAction.ToggleBoard),
                      Loc.T("{0} / {1} 翻屏", Key(KeyAction.FlipPageUp), Key(KeyAction.FlipPageDown))),
                3 => (c.Tip, Key(KeyAction.ToolPen), "已经是笔 → 再按换颜色"),
                4 => (c.Tip, Key(KeyAction.ToolHighlighter), ""),
                5 => (c.Tip, Key(KeyAction.ToolLaser), ""),
                6 => (c.Tip, Key(KeyAction.ToolEraser), "整笔橡皮 / 像素橡皮在上带里选"),
                7 => (c.Tip, "", "点画布输入；点已有文字选中，再点编辑"),
                8 => (c.Tip, Key(KeyAction.ToolMarquee), "矩形 / 套索在上带里选；双击 = 全选"),
                9 => (c.Tip, Key(KeyAction.ToolCapture), ""),
                10 => (c.Tip, "", "22 种图形都在面板里挑"),
                11 => (c.Tip, "", "打开收藏；选中内容后可存进来"),
                12 => (c.Tip, Key(KeyAction.Undo), ""),
                13 => (c.Tip, Key(KeyAction.Redo), ""),
                14 => (c.Tip, "", UpdateAvailable ? "有新版本，点开去「检查更新」" : "课堂工具、墨迹、设置都在这儿"),
                15 => (c.Tip, Key(KeyAction.Clear), "只清当前页 / 当前幻灯片 / 当前这一屏；可撤销"),
                _ => default,
            };
        }

        // 色片（100+）：默认色片本来就一眼看懂，但“再点当前色片换色”是隐藏手势，
        // 所以每一格都给一条短提示（画笔和荧光笔现在同一套交互）。
        if (id >= 100 && id < 100 + SwatchCount)
        {
            int slot = SwatchIdx[id - 100];
            if (UsingTextSwatches)
                return (SwatchTable[slot].Name, "", "文字颜色");
            bool highlighter = UsingHighlighterSwatches;
            bool active = highlighter
                ? _host?.State.HighlighterSwatchSelected == slot
                : _host?.State.PenSwatchSelected == slot;
            var table = highlighter ? Tokens.HighlighterPalette : Tokens.Palette;
            string name = slot >= 0 && slot < table.Length
                          && SameColor(SwatchColor(id - 100), table[slot].Color)
                ? table[slot].Name : "自定义色";
            return (name, "", active ? "再点一下换色" : "单击使用；已选中时再点换色");
        }

        if (id >= 200 && id < 200 + BandSegmentCount) return SegmentTip(id - 200);

        if (id >= 400 && id < 400 + ActionCount) return ActionTip(id - 400);

        if (id == 500) return ("线型", "", "实线 / 虚线 / 点线，点一下换");
        if (id == 501) return ("粗细", "", "点一下换一档");
        if (id == 610) return ("字号", "", "点开直接选字号");
        if (id == 611) return default;
        return default;      // 滑条（300）不出第二张卡，见上文
    }

    private (string Title, string Key, string Note) SegmentTip(int i)
    {
        switch (_bandCell)
        {
            case 2:
            {
                // 白板：只留翻页两个（有键）；页码读数、板色、底纹、间距都"点一下就知道"，不配（收窄）
                var seg = BoardSegAt(i);
                return seg.Kind switch
                {
                    BoardSegKind.PageUp => ("上一屏", Key(KeyAction.FlipPageUp), ""),
                    BoardSegKind.PageDown => ("下一屏", Key(KeyAction.FlipPageDown), ""),
                    _ => default,
                };
            }
            // 橡皮 / 框选 / 截图的分段上已经写着"整笔擦 / 面积擦 / 矩形 / 套索 / 截图…"，
            // 不配（收窄）；只有图形那 22 段是纯图标，全配。
            case 10:
                return (ToolNames.Of(ShapeToolAt(i)), "", "");
        }
        return default;
    }

    /// <summary>某个提示目标贴着哪块画（本地布局坐标；没有就空矩形）。</summary>
    private RectF TipAnchor(int id)
    {
        if (id == -2) return BallRect();
        if (id >= 1000 + MoreHitRecentPageRow
            && id < 1000 + MoreHitRecentPageRow + RecentPageMax * RecentPageGroups)
        {
            int slot = id - 1000 - MoreHitRecentPageRow;
            return RecentPageRowRect(slot / RecentPageMax, slot % RecentPageMax);
        }
        if (id >= 0 && id < Cells.Length)
        {
            int pos = PosOf(id);
            return pos < 0 ? RectF.Empty : CellRect(pos);
        }
        if (id >= 100 && id < 100 + SwatchCount) return SwatchRect(id - 100);
        if (id >= 200 && id < 200 + BandSegmentCount) return SegmentRect(id - 200, BandSegmentCount);
        if (id >= 400 && id < 400 + ActionCount) return ActionRectAt(id - 400);
        if (id == 500) return DashToggleRect();
        if (id == 501) return BandHasWidthButton ? WidthButtonRect() : RectF.Empty;
        return RectF.Empty;
    }

    /// <summary>
    /// 提示卡这一刻的矩形（本地布局坐标；空 = 没得画）。
    /// 横向夹在屏幕里；纵向上**躲开整块面板**（带子在主条上方时，卡画在带子上面，
    /// 不压住色片），上方放不下就翻到面板下方。
    /// </summary>
    private RectF TipBox()
    {
        var (title, key, note) = TipContent(_tipTarget);
        if (title == null) return RectF.Empty;
        var anchor = TipAnchor(_tipTarget);
        if (anchor.IsEmpty) return RectF.Empty;

        float titleOnly = _widgets.Measure(title, Tokens.TipTitleSize);
        float titleW = titleOnly + (key.Length == 0 ? 0f
                                  : 12f + _widgets.Measure(key, Tokens.TipTitleSize));
        float noteW = note.Length == 0 ? 0f : _widgets.Measure(note, Tokens.TipNoteSize);
        // 完整路径可能很长：卡最宽只到屏幕内（多出来的由 DrawTip 用省略号收口）。
        float maxW = MathF.Max(120f, _screen.MaxX - _screen.MinX - 8f);
        float w = MathF.Min(MathF.Max(titleW, noteW) + Tokens.TipPadX * 2f, maxW);
        float h = note.Length == 0
            ? Tokens.TipTitleSize + Tokens.TipPadY * 2f + 4f
            : Tokens.TipTitleSize + 2f + Tokens.TipNoteSize + Tokens.TipPadY * 2f + 4f;

        float cx = (anchor.MinX + anchor.MaxX) * 0.5f;
        float x0 = cx - w * 0.5f, x1 = cx + w * 0.5f;
        float lo = _screen.MinX + 4f, hi = _screen.MaxX - 4f;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }

        float gap = Tokens.TipGap + (1f - _tipFade.Value) * 3f;    // 淡入时上浮 3px
        float top;
        if (_tipTarget >= 1000)
        {
            // 「更多」面板里的行（最近文档）：贴着**那一行**，不是贴着工具条。
            top = anchor.MinY - gap - h;
            if (top < _screen.MinY + 4f) top = anchor.MaxY + gap;
        }
        else
        {
            var panel = PanelRect();
            top = panel.MinY - gap - h;
            if (top < _screen.MinY + 4f) top = panel.MaxY + gap;
        }
        if (top + h > _screen.MaxY - 4f) top = MathF.Max(_screen.MinY + 4f, _screen.MaxY - 4f - h);
        return new RectF { MinX = x0, MinY = top, MaxX = x1, MaxY = top + h };
    }

    /// <summary>画提示卡。永远最后画（压在主条/带子/更多面板之上）。</summary>
    private void DrawTip(ID2D1DeviceContext ctx)
    {
        if (_tipFade.Value <= 0.001f) return;
        var (title, key, note) = TipContent(_tipTarget);
        if (title == null) return;
        var box = TipBox();
        if (box.IsEmpty) return;

        bool layered = BeginFade(ctx, _tipFade.Value);
        DrawCard(ctx, box, 8f);

        float innerW = box.MaxX - box.MinX - Tokens.TipPadX * 2f;
        var line1 = new RectF
        {
            MinX = box.MinX + Tokens.TipPadX, MinY = box.MinY + Tokens.TipPadY,
            MaxX = box.MaxX - Tokens.TipPadX, MaxY = box.MinY + Tokens.TipPadY + Tokens.TipTitleSize + 2f,
        };
        float keyW = key.Length == 0 ? 0f : 12f + _widgets.Measure(Loc.Tr(key), Tokens.TipTitleSize);
        string titleShown = Ellipsize(Loc.Tr(title), MathF.Max(24f, innerW - keyW), Tokens.TipTitleSize);
        _widgets.Text(ctx, titleShown, line1, Tokens.TipTitleSize, Brush(ctx, InkCol), center: false);
        if (key.Length != 0)
        {
            float titleOnly = _widgets.Measure(titleShown, Tokens.TipTitleSize);
            _widgets.Text(ctx, key,
                          new RectF { MinX = line1.MinX + titleOnly + 12f, MinY = line1.MinY,
                                      MaxX = line1.MaxX, MaxY = line1.MaxY },
                          Tokens.TipTitleSize, Brush(ctx, MutedCol), center: false);
        }
        if (note.Length != 0)
            _widgets.Text(ctx, Ellipsize(Loc.Tr(note), innerW, Tokens.TipNoteSize),
                          new RectF { MinX = line1.MinX, MinY = line1.MaxY,
                                      MaxX = line1.MaxX, MaxY = box.MaxY - Tokens.TipPadY },
                          Tokens.TipNoteSize, Brush(ctx, MutedCol), center: false);
        if (layered) EndFade(ctx);
    }

    // ---- 自检钩子（开发期用；产品代码不碰）--------------------------------

    /// <summary>
    /// 自检用：某一格（按**完整档的下标**）的逻辑矩形。
    /// 注意参数是"格子的编号"不是"第几个"——档位一换，显示的格子数就变了，
    /// 按序号取会跑到界外（自检第一版就是这么点空了整整一条用例）。
    /// </summary>
    internal RectF CellRectForTest(int cell) => CellRect(PosOf(cell));

    /// <summary>自检用：上带这一刻的矩形（没长出来就是空；穿透里是那条 10 像素色线，不为空）。</summary>
    internal RectF BandRectForTest => BandVisible() ? BandRect() : RectF.Empty;

    /// <summary>自检用：设置条张开到什么程度（0 = 平时那条色线，1 = 完整设置条）。
    /// "穿透只收成线、不许张开"这条自检靠它——判的是折叠动画真的走完了。</summary>
    internal float RailValueForTest => _rail.Value;

    /// <summary>自检用：主条（不含上带）的矩形。</summary>
    internal RectF BarRectForTest => BarRect();

    /// <summary>自检用：第 i 个色片的矩形。</summary>
    internal RectF SwatchRectForTest(int i) => SwatchRect(i);

    /// <summary>
    /// 自检用：这一格的设置条上**有没有**那个虚实线切换（笔 + 完整档才有，见 <see cref="BandHasDashToggle"/>）。
    /// 极简档那一条断言靠它——"挤不下所以不给"这件事必须验得出来，不然下一轮加宽了会静默变样。
    /// </summary>
    internal bool DashToggleVisibleForTest => BandHasDashToggle;

    /// <summary>自检用：虚实线那一格的矩形（点它要按物理像素）。</summary>
    internal RectF DashToggleRectForTest => DashToggleRect();

    /// <summary>自检用：换挡动画是不是还在跑（换挡完了要停，空闲帧要回 0）。</summary>
    internal bool DashFadeRunningForTest => _dashFade.Running;

    /// <summary>自检用：上带里第 i 个分段的矩形。</summary>
    internal RectF SegmentRectForTest(int i) => SegmentRect(i, BandSegmentCount);

    /// <summary>
    /// 自检用：上带现在有几段。
    /// 自检要断言"图形那格是七段"，但**段数只能从绘制/命中用的这一份真值里读**：
    /// <see cref="SegmentRectForTest"/> 不校验下标（越界的下标照样算得出一个矩形），
    /// 光靠它数不出上界。
    /// </summary>
    internal int BandSegmentCountForTest => BandSegmentCount;

    /// <summary>
    /// 自检用：白板色带上"某一种段"排第几个（找不到 -1）。名字：翻页那两个是 `PageUp` /
    /// `PageDown`、页码是 `PageLabel`、底纹 `Pattern`、间距 `Step`；板色带下标，`Color0/1/2`
    /// （0/1/2 ＝ 白/绿/黑）。
    ///
    /// 为什么要这么一个入口：**自检不许写死段下标**。段序是会被重排的——2026-09-27 这一轮
    /// 就把 `[上一屏][白][绿][黑][下一屏]` 重排成了 `[‹] 第 N 屏 [›] [白][绿][黑] [底纹][间距]`，
    /// 而写死下标的自检**改错了是静默的**：点到了别的段，红的却是那一条断言（这条教训
    /// 在抽屉那几行上吃过一次，见 `RowRectByLabelForTest` 的注释）。
    /// </summary>
    internal int BoardSegIndexOfForTest(string name)
    {
        if (_bandCell != 2) return -1;
        var segs = BoardBand;
        for (int i = 0; i < segs.Length; i++)
            if (BoardSegName(segs[i]) == name) return i;
        return -1;
    }

    /// <summary>自检用：白板色带这一刻走的是**紧凑布局**（极简档那种，没有翻页和滑条）。</summary>
    internal bool BoardCompactBandForTest => BoardCompactBand;

    private static string BoardSegName((BoardSegKind Kind, int Idx) s) =>
        s.Kind == BoardSegKind.Color ? $"Color{s.Idx}" : s.Kind.ToString();

    /// <summary>
    /// 自检用：某一格现在画的是哪个图标名。
    /// 图形那一格（8）的图标**跟着当前种类变**，所以它得问一次状态；
    /// 其余的格子图标是写死在 Cells 表里的，直接给。
    /// ⚠ 图形这一格的判据和绘制必须**同一条**（"亮着才画当前种类，没亮画通用图标"）：
    /// 自检要是无条件问 ShapeIcon，就会出现"自检说画的是三角形、屏幕上其实是通用图标"
    /// ——这正是这条自检要盯的东西（见 ShapeBandTest 的 D 段）。
    /// </summary>
    internal string CellIconForTest(int cell)
    {
        var st = _host.State;
        // 白板那一格：板开着时画的是**自绘的板 ＋ 这块板的颜色**，名字写成 "board:白板" 这样
        // ——盘里没有这个名字的图标，它只是给自检一个"现在画的是哪一块板"的判据
        //（真画法是 IconAtlas.DrawBoard，见 DrawCell）。
        if (cell == 2)
            return st.Board ? "board:" + BoardColorName(st.BoardColor) : "board";
        // 激光笔那一格：**判据直接问 IconAtlas**（`LaserIconNameForTest`）——换一支图标时
        // 只要改那边一处，这里跟着变，不会出现"自检说 Material、屏幕上其实是自绘的那支"。
        if (cell == 5)
            return IconAtlas.LaserIconNameForTest(IsActive(5, st));
        // 其余格子（含橡皮 / 选择那两格的"选中显示选中什么"）：**和绘制问同一个函数**
        //（`CellIconName`）。两处各写一遍的话，自检就成了"测另一个东西"，见上面那条 ⚠。
        return CellIconName(cell, st);
    }

    /// <summary>自检用：现在这块板是白的 / 绿的 / 黑的（按板色和三个预设比，见 InkPalette.BoardPresets）。</summary>
    private static string BoardColorName(in Color4 c)
    {
        foreach (var (name, preset) in InkPalette.BoardPresets)
            if (MathF.Abs(preset.R - c.R) < 0.02f && MathF.Abs(preset.G - c.G) < 0.02f
                && MathF.Abs(preset.B - c.B) < 0.02f) return name;
        return "自定义色";
    }

    /// <summary>
    /// 自检用：图形那一格**第 i 段**的图标名。
    /// 用来验"抛物线那一段的图标跟着当前开口方向转"（见 <see cref="ParabolaIconName"/>）——
    /// 图标不转的话，老师看不出"再点一次"到底有没有生效。
    /// </summary>
    internal string ShapeIconNameForTest(int i) => ShapeIcon(i);

    /// <summary>
    /// 自检用：图形那一格**第 i 段**右边有几个档位点、第几个是当前档（见 <see cref="PipsOf"/>）。
    /// 拿它验"哪几格该有档位点、各有几档"——用户 2026-09-20 问"抛物线那格怎么没有小圆点"
    /// 就是这类漏了才发现的，所以这条要有断言卡住。
    /// </summary>
    internal (int Count, int Current) ShapePipsForTest(int i)
        => PipsOf(ShapeToolAt(i), _host.State);

    /// <summary>自检用：这个工具在图形面板里有没有入口（见 <see cref="HasShapeEntry"/>）。</summary>
    internal static bool HasShapeEntryForTest(Tool t) => HasShapeEntry(t);

    /// <summary>自检用：图形那一格排了**几行**（自检要靠它把各段分回行里，去验"行不重叠"）。</summary>
    internal static int ShapeRowCountForTest => ShapeBandRows;

    /// <summary>自检用：第 `row` 行**几段**（自检按"行"分组时要知道每行的段数）。</summary>
    internal static int ShapeRowLengthForTest(int row) => ShapeRowCount(row);

    /// <summary>
    /// 自检用：这个工具在图形那一格里是**第几段**（两行拉平编号；没入口就 −1）。
    ///
    /// **为什么要有它**：自检里写死段号会随"加一种图形 / 撤一种图形"**静默失效**
    /// ——这次加棱锥 / 棱台时第二行往下挪了两格，写死的 16 就不声不响点了别的段。
    /// 按工具名问一句就跟着表走，挪多少次都不怕（仓库教训里那一条）。
    /// </summary>
    internal static int ShapeSegmentIndexForTest(Tool t)
    {
        int seen = 0;
        foreach (var row in ShapeRows)
        {
            int j = Array.IndexOf(row, t);
            if (j >= 0) return seen + j;
            seen += row.Length;
        }
        return -1;
    }

    /// <summary>自检用：滑条的矩形。</summary>
    internal RectF SliderRectForTest => SliderRect();

    /// <summary>
    /// 自检用：滑条轨道的左右端（逻辑坐标）。自检要拖到**两端**去验
    /// "这个工具的粗细真的走到了范围的端点"，所以必须拿到和绘制同一份的两个端点。
    /// </summary>
    internal (float Left, float Right) SliderTrackRangeForTest => SliderTrackRange();

    /// <summary>自检用：某工具的粗细档位表（和引擎那张表逐项比对，见 --paneltest ⑥.1）。</summary>
    internal float[] WidthGradesForTest(Tool tool) => WidthGrades(tool);

    /// <summary>自检用：某个档位值在滑条上的 x（逻辑坐标）——照着它点 = 点中那个档位点。</summary>
    internal float WidthGradeXForTest(float grade)
    {
        var (min, max) = WidthRange(_host.State.Tool);
        var (left, right) = SliderTrackRange();
        return left + (right - left) * ((grade - min) / (max - min));
    }

    /// <summary>自检用：「更多」面板 / 启动器格子 / 底栏 / 设置行 / 档位 / 钉住宫格的矩形。</summary>
    internal bool MoreOpenForTest => _moreOpen;
    internal RectF MoreRectForTest => MoreRect();
    internal int MorePageForTest => (int)_morePage;
    internal RectF MoreCloseRectForTest => MoreCloseRect();
    internal RectF MoreBackRectForTest => MoreBackRect();
    internal RectF MoreRowRectForTest(int i) => SettingsRowRectFor(Rows[i].Kind);
    internal RectF MoreProfileRectForTest(int i) => MoreProfileRect(i);
    internal RectF MoreChipRectForTest(int cell) => MoreChipRect(cell);
    /// <summary>自检用：启动器格子的矩形（按**全局编号**，各分组自动展开；越界返回空）。</summary>
    internal RectF HubTileRectForTest(int code)
    {
        for (int s = 0; s < 2; s++)
            for (int i = 0; i < HubCount(s); i++)
                if (HubTileCode(s, i) == code) return HubTileRect(s, i);
        return RectF.Empty;
    }
    internal bool HubTileEnabledForTest(int code) => HubTileInfo(code).Enabled;
    internal string HubHintForTest => _hubHint;
    internal RectF LogNoteRectForTest => LogNoteRect();
    internal RectF LogUploadRectForTest => LogUploadRect();
    internal RectF LogExportRectForTest => LogExportRect();
    internal RectF LogToggleRectForTest => LogToggleRect();
    internal RectF HubBottomRectForTest(int i) => HubBottomRect(i);
    /// <summary>自检用：底栏命中的基码（钉子用它算"第 i 格应返回什么"）。</summary>
    internal int MoreHitBottomBaseForTest => MoreHitBottom;
    /// <summary>自检用：直接问"这一点命中什么"（不点击、不执行——点"退出"会杀掉测试进程，
    /// 所以这类**命中表漏项**只能靠这个出口钉住；2026-10-17 的"退出点不动"就是它抓的类）。</summary>
    internal int MoreHitAtForTest(float x, float y) => MoreHitAt(x, y);
    /// <summary>自检用：某个坐标这一刻的命中码（面板里"点得中吗"的探针）。</summary>
    internal int MoreHitForTest(float x, float y) => MoreHitAt(x, y);

    /// <summary>自检用：行表里有几行（不写死数字——插一行/删一行自检要自己跟上）。</summary>
    internal int MoreRowCountForTest => Rows.Length;

    // ---- A0 动作表/稳定命中的自检钩子（2026-11-08）------------------------

    internal int UiActionCountForTest
    {
        get
        {
            EnsureUiActions();
            return _actions.Count;
        }
    }

    internal string[] UiActionIdsForTest
    {
        get
        {
            EnsureUiActions();
            var ids = new string[_actions.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = _actions.All[i].Id;
            return ids;
        }
    }

    internal bool HasUiActionForTest(string id)
    {
        EnsureUiActions();
        return _actions.Get(id) != null;
    }

    internal string RowActionIdForTest(int i) => RowActionId(Rows[i].Kind);

    internal HitTarget MoreHitTargetAtForTest(float x, float y) => MoreHitTargetAt(x, y);

    internal string FocusedSettingForTest => _settingsFocusId;
    internal bool SettingsFocusVisibleForTest => _settingsFocusVisible;

    // ---- A1 PageHost 自检钩子（2026-11-08）--------------------------------
    internal int MorePageStackCountForTest => _pageHost?.Count ?? 0;
    internal string MorePageIdForTest => _pageHost?.Current?.Id ?? "";
    internal float MoreTargetHForTest => MoreTargetH();
    internal void BackMorePageForTest() => GoBackMorePage();

    /// <summary>出图用：切界面缩放档（走产品同一条 SetUiScale 命令，重排后出图）。</summary>
    internal void SetUiScaleForTest(float scale) => _host?.Commands.SetUiScale(scale);

    /// <summary>自检/出图用：打开「更多」面板（产品里只能点主条「…」那一格）。</summary>
    internal void OpenMoreForTest() { OpenMore(); _more.Jump(1f); _moreH.Jump(MoreTargetH()); }

    /// <summary>自检/出图用：按真实指针路径点设置页第 i 行（按下/抬起同一行才执行）。</summary>
    internal void ClickSettingsRowForTest(int i)
    {
        var r = SettingsRowRectForTest(i);
        if (r.IsEmpty) return;
        var e = new UiPointerEvent((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f,
                                   1f, false, false);
        PointerDown(e);
        PointerUp(e);
    }

    /// <summary>自检/出图用：语言下拉是否打开、卡片矩形（都是绘制/命中同一份）。</summary>
    internal bool LangMenuOpenForTest => _langMenuOpen;
    internal RectF LangMenuRectForTest => LangMenuRect();

    /// <summary>自检/出图用：等价于在下拉里点了第 i 项（只落偏好，不重启）。</summary>
    internal void PickLangForTest(int i) => SelectLangMenu(i);

    /// <summary>自检用：关掉「更多」面板（产品里是点面板外 / 点 ✕）。</summary>
    internal void CloseMoreForTest() { CloseMore(); _more.Jump(0f); }

    /// <summary>自检/出图用：切到启动器（0）/ 设置子页（1）/ 最近文档（5）。</summary>
    internal void SetMorePageForTest(int i)
    {
        SetMorePage((MorePage)Math.Clamp(i, 0, (int)MorePage.Recent), push: true);
        _moreHover = MoreHitNone;
        _moreH.Jump(MoreTargetH());
        Invalidate();
    }

    /// <summary>自检/出图用：直接切深色主题（**不落盘**，只改这一刻的显示）。</summary>
    internal void SetDarkForTest(bool on)
    {
        _dark = on;
        PushFloatingTheme();     // 浮层（操作条/小面板）也得跟着换——和点那行开关同一条路
        Invalidate();
    }

    /// <summary>
    /// 自检用：**按行标签**找那一行的矩形（找不到返回空矩形）。
    ///
    /// 行现在长在「更多」面板里（2026-10-01 起）；这个钩子跟着走，标签表的"唯一一份"不变。
    ///
    /// 为什么不让自检写行下标：行是会被插来插去的——2026-09-17 插了底纹两行、
    /// 2026-09-19 把灰着的「学科工具」换成了坐标系 / 数轴 / 坐标系网格三行，
    /// 2026-10-01 又整个搬进了中央面板。每插一次，写死下标的自检就要去改一处引用，
    /// 而且**改错了是静默的**：点到了别的行，红色的却是那一条断言（"点重启没反应"）。
    /// 按标签找，以后插行就不会再碰到自检。
    /// </summary>
    internal RectF RowRectByLabelForTest(string labelPart)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Label.Contains(labelPart, StringComparison.Ordinal))
                return SettingsRowRectFor(Rows[i].Kind);
        return RectF.Empty;
    }

    /// <summary>自检：直接打开设置页（不模拟点“更多”里的设置格）。</summary>
    internal void OpenSettingsPageForTest()
    {
        _moreOpen = true;
        EnsurePageHost();
        SetMorePage(MorePage.Home, push: false);
        SetMorePage(MorePage.Settings, push: true);
        _settingsCat = SettingsCategory.Toolbar;
        _settingsScroll = 0f;
        _settingsScrollDrag = false;
        _more.Jump(1f);
        _moreH.Jump(MoreTargetH());
        _moreHover = MoreHitNone;
        _morePress = -1;
        ResetSettingsFocus();
        Invalidate();
    }

    // ---- 设置页连续滚动的自检钩子（2026-11-07）----------------------------

    internal void AttachHostForTest(IUiHost host)
    {
        _host = host;
        EnsureUiActions();
        EnsurePageHost();
        _peek.Bind(host);
        _rail.Bind(host);
    }

    /// <summary>无窗口自检：直接摆好设置页的几何，不创建任何 HWND。</summary>
    internal void PrepareSettingsAuditForTest(RectF screen)
    {
        _screen = screen;
        _work = screen;
        _moreOpen = true;
        EnsurePageHost();
        _pageHost?.SetWorkArea(_work);
        SetMorePage(MorePage.Home, push: false);
        SetMorePage(MorePage.Settings, push: true);
        _settingsCat = SettingsCategory.Toolbar;
        _settingsScroll = 0f;
        _settingsScrollDrag = false;
        _moreHover = MoreHitNone;
        _morePress = -1;
        ResetSettingsFocus();
    }

    internal int SettingsCategoryCountForTest => SettingsCatNames.Length;
    internal int SettingsRowCountForTest => Rows.Length;
    internal RectF SettingsRowRectForTest(int i) => MoreRowRect(i);
    internal RectF SettingsNavRectForTest(int i) => SettingsNavItemRect(i);
    internal RectF SettingsViewportRectForTest => SettingsViewportRect();
    internal void SettingsScrollToCategoryForTest(int c)
        => ScrollSettingsToCategory((SettingsCategory)Math.Clamp(c, 0, SettingsCatNames.Length - 1));
    internal float SettingsScrollMaxForTest => SettingsScrollMax();
    internal float SettingsScrollValueForTest => _settingsScroll;
    internal int SettingsActiveCategoryForTest => (int)_settingsCat;
    internal float SettingsContentHForTest => SettingsContentH();
    internal float SettingsSectionTopForTest(int c)
        => SettingsSectionTop((SettingsCategory)Math.Clamp(c, 0, SettingsCatNames.Length - 1));
    internal RectF SettingsScrollbarRectForTest => SettingsScrollbarRect();
    internal RectF SettingsScrollThumbRectForTest => SettingsScrollThumbRect();

    internal int SettingsRowCategoryForTest(int i)
    {
        var kind = Rows[i].Kind;
        for (int c = 0; c < SettingsCatNames.Length; c++)
            if (Array.IndexOf(SettingsRows((SettingsCategory)c), kind) >= 0) return c;
        return -1;
    }

    internal void SettingsScrollToForTest(float scroll) => ScrollSettingsTo(scroll);
    internal void SettingsSetCategoryForTest(int c)
    {
        int n = Math.Clamp(c, 0, SettingsCatNames.Length - 1);
        ScrollSettingsToCategory((SettingsCategory)n);
    }
    internal void ActivateRowForTest(int i) => ActivateRow(i);

    /// <summary>自检：按标签触发设置行（等同点整行）。</summary>
    internal bool ActivateRowByLabelForTest(string labelPart)
    {
        for (int i = 0; i < Rows.Length; i++)
        {
            if (!Rows[i].Label.Contains(labelPart, StringComparison.Ordinal)) continue;
            ActivateRow(i);
            return true;
        }
        return false;
    }

    /// <summary>自检：设置页切到第 i 个左侧分类。</summary>
    internal void SelectSettingsCategoryForTest(int i)
    {
        _settingsCat = (SettingsCategory)Math.Clamp(i, 0, SettingsCatNames.Length - 1);
        _moreHover = MoreHitNone;
        _moreH.Jump(MoreTargetH());
        Invalidate();
    }

    /// <summary>自检用：第 i 行的矩形（现在指「更多」面板里的行——产品里行只在这儿）。</summary>
    internal RectF RowRectForTest(int i) => MoreRowRect(i);

    /// <summary>自检用：深色主题与贴边隐藏的开关状态。</summary>
    internal bool DarkForTest => _dark;
    internal bool HideEnabledForTest => _hideEnabled;

    /// <summary>自检/出图用：直接切到某一档（产品里在「更多 → 设置 → 工具条」里点）。</summary>
    internal void SetProfileForTest(int i) => SetProfile((Profile)i);

    /// <summary>自检/出图用：把色线张开成设置条（产品里是鼠标碰到它）。</summary>
    internal void OpenRailForTest() { _railHover = true; _rail.Jump(1f); }

    /// <summary>出图用：把上带掰到某一格（等价于点它一下，但不执行那一格的动作）。</summary>
    internal void SelectBandCellForTest(int cell)
    {
        _bandCell = cell;
        _railHover = true;      // 出图时假装"焦点就在面板上"
        _rail.Jump(1f);
    }

    /// <summary>自检用：上带这一刻停在哪一格（"现在是谁的设置条"）。</summary>
    internal int BandCellForTest => _bandCell;

    /// <summary>出图用：把"粗细预览"摆出来（产品里是拖滑条、或指针停在滑条上时出现）。</summary>
    internal void ShowSizePreviewForTest()
    {
        _hover = 300;
        _sliderDragging = true;
        _railHover = true;
        _rail.Jump(1f);
    }

    /// <summary>自检用：上带右端动作按钮（关闭白板 / 全选三连 / 粘贴 / 完成）的矩形。</summary>
    internal RectF ActionRectForTest => CurAction == BandAction.None ? RectF.Empty : ActionRect();
    /// <summary>自检用：选择那条右端三连里第 i 个（0 全选 / 1 复制 / 2 粘贴）的矩形。</summary>
    internal RectF ActionRectAtForTest(int i) => i >= 0 && i < ActionCount ? ActionRectAt(i) : RectF.Empty;
    /// <summary>自检用：这一条带上有几个动作按钮。</summary>
    internal int ActionCountForTest => ActionCount;
    /// <summary>自检用：上带的悬停命中编号（400 + 第几个动作）。</summary>
    internal int HoverAtForTest(float x, float y) => HoverAt(x, y);
    internal RectF TextFontButtonRectForTest => TextSizeButtonRect();
    internal RectF TextFontMenuRectForTest => TextFontMenuRect();
    internal RectF TextFontCellRectForTest(int i) => TextFontCellRect(i);
    internal int TextFontSizeCountForTest => TextFontSizes.Length;
    internal float TextFontSizeAtForTest(int i) => TextFontSizes[i];
    internal bool TextFontMenuOpenForTest => _textFontMenuOpen;

    /// <summary>自检用：这一格现在压暗没有（撤销/重做栈空）。</summary>
    internal bool CellUnavailableForTest(int cell) => CellUnavailable(cell, _host.State);

    /// <summary>自检用：这一刻色片有几个（极简档应该是 4）。</summary>
    internal int SwatchCountForTest => SwatchCount;

    /// <summary>自检/截图用：自定义色取色板是否打开、卡片矩形。</summary>
    internal bool PenPickerOpenForTest => _penPickerOpen;
    internal bool PenPickerTextForTest => _pickerText;
    internal RectF PenPickerRectForTest => PenPickerRect();
    internal RectF PenPickerSvRectForTest => PenPickerSvRect();
    internal RectF PenPickerEyeRectForTest => PenPickerEyeRect();
    internal RectF PenPickerCommonRectForTest(int i) => PenPickerCommonRect(i);
    internal RectF PenPickerRecentRectForTest(int i) => PenPickerRecentRect(i);
    internal RectF PenPickerOpacityTrackRectForTest => PenPickerOpacityTrackRect();
    internal RectF PenPickerOpacityStopRectForTest(int level)
    {
        var tr = PenPickerOpacityTrackRect();
        float left = tr.MinX + 6f, right = tr.MaxX - 6f;
        float x = left + (right - left) * (Math.Clamp(level, 0, 4) / 4f);
        float cy = (tr.MinY + tr.MaxY) * 0.5f;
        return new RectF { MinX = x - 9f, MinY = cy - 9f, MaxX = x + 9f, MaxY = cy + 9f };
    }
    internal int PenPickerCommonCountForTest => PickerCommonCount;
    internal bool PenPickerHighlighterForTest => _pickerHighlighter;
    internal void OpenPenPickerForTest() =>
        OpenPenColorPicker(_host.State.PenSwatchSelected >= 0 ? _host.State.PenSwatchSelected : PenLastSlot);
    internal void OpenHighlighterPickerForTest(int slot) => OpenHighlighterColorPicker(slot);

    /// <summary>自检用："允许自动收起"的开关（启动时应该是 false）。</summary>
    internal bool PeekArmedForTest => _peekArmed;

    // ---- 自检用：悬停提示 ----------------------------------------------------

    /// <summary>提示这一刻真的画出来了没有（过了延迟 + 淡入中有值）。</summary>
    internal bool TipVisibleForTest => _tipShown && _tipFade.Value > 0.01f;
    /// <summary>任意目标的提示文案（名称 / 键位 / 说明；Title 为 null = 不出提示）。</summary>
    internal (string Title, string Key, string Note) TipContentForTest(int id) => TipContent(id);

    /// <summary>
    /// 语言审计用：把所有悬停提示过一遍 `Loc.Tr`，把没翻译的源文记下来。
    /// 只读当前状态，不改界面；`--tipsweep` 调用。
    /// </summary>
    internal void AuditTipsForTest()
    {
        void Probe(int id)
        {
            var (title, key, note) = TipContent(id);
            if (!string.IsNullOrEmpty(title)) Loc.Tr(title);
            if (!string.IsNullOrEmpty(key)) Loc.Tr(key);
            if (!string.IsNullOrEmpty(note)) Loc.Tr(note);
        }

        for (int id = 0; id < Cells.Length; id++) Probe(id);
        for (int id = 100; id < 100 + SwatchCount; id++) Probe(id);
        for (int id = 400; id < 400 + ActionCount; id++) Probe(id);
        Probe(500); Probe(501); Probe(610); Probe(611);
        for (int id = 1000; id < 1000 + 560; id++) Probe(id);

        // 分段提示跟当前上带有关：图形 22 段、白板翻页两段各扫一遍。
        int oldBand = _bandCell;
        _bandCell = 10;
        for (int i = 0; i < BandSegmentCount; i++) Probe(200 + i);
        _bandCell = 2;
        for (int i = 0; i < BandSegmentCount; i++) Probe(200 + i);
        _bandCell = oldBand;
    }
    /// <summary>自检用：触摸/笔长按已经弹过提示没有（弹过 = 这一次松手不执行）。</summary>
    internal bool TipHoldFiredForTest => _tipHoldFired;
    /// <summary>自检用：触摸/笔长按正在计时（还没到点）。</summary>
    internal bool TipHoldRunningForTest => _tipHoldStart > double.NegativeInfinity && !_tipHoldFired;
    /// <summary>提示卡的矩形（屏幕坐标；没显示就是空矩形）。</summary>
    internal RectF TipRectForTest
    {
        get
        {
            if (!TipVisibleForTest) return RectF.Empty;
            var r = TipBox();
            if (r.IsEmpty) return RectF.Empty;
            var s = Shift();
            return new RectF
            {
                MinX = r.MinX + s.X, MinY = r.MinY + s.Y,
                MaxX = r.MaxX + s.X, MaxY = r.MaxY + s.Y,
            };
        }
    }
    /// <summary>自检/出图用：直接把某个目标的提示摆出来（产品里靠真悬停等 500ms，出图等不起）。</summary>
    internal void ShowTipForTest(int id)
    {
        SetTipTarget(id);
        _tipShown = true;
        _tipFade.Jump(1f);
        Invalidate();
    }

    /// <summary>自检用：粗细预览这一刻的矩形（屏幕坐标；没显示就是空矩形）。</summary>
    internal RectF SizePreviewRectForTest
    {
        get
        {
            if (!SizePreviewVisible || _host == null) return RectF.Empty;
            var r = SizePreviewRect(_host.State);
            var s = Shift();
            return new RectF
            {
                MinX = r.MinX + s.X, MinY = r.MinY + s.Y,
                MaxX = r.MaxX + s.X, MaxY = r.MaxY + s.Y,
            };
        }
    }

    /// <summary>自检用：这一档显示几格 / 现在是第几档 / 某一格钉着没有。</summary>
    internal int VisibleCountForTest => VisibleCells().Length;
    internal int ProfileForTest => (int)_profile;
    internal bool PinnedForTest(int cell) => _pinned[cell];
    internal bool CanUnpinForTest(int cell) => CanUnpin(cell);
    internal int DensityForTest => (int)Density;
    internal float SwatchWidthForTest(int i) => SwatchRect(i).MaxX - SwatchRect(i).MinX;
    internal int ActiveGradeCountForTest(Tool tool) => ActiveWidthGrades(tool).Length;
    internal bool BandHasWidthButtonForTest => BandHasWidthButton;
    internal bool BandHasSliderForTest => BandHasSlider;
    internal RectF WidthButtonRectForTest => WidthButtonRect();

    internal void TogglePinForTest(int cell) => TogglePin(cell);
    internal void SetPinForTest(int cell, bool pinned)
    {
        if (cell <= 0 || cell >= Cells.Length || !CanUnpin(cell)) return;
        _pinned[cell] = pinned;
    }

    /// <summary>自检用：档位条第 i 段、钉住栏第 cell 格的矩形（与产品同一处：更多面板里）。</summary>
    internal RectF ProfileRectForTest(int i) => MoreProfileRect(i);
    internal RectF ChipRectForTest(int cell) => MoreChipRect(cell);

    /// <summary>自检用：现在算"展开"吗。</summary>
    internal bool ExpandedForTest => Expanded;

    /// <summary>自检用：界面看到的屏幕（核对它和 IUiHost.Screen 是不是同一个）。</summary>
    internal RectF ScreenForTest => _screen;

    /// <summary>自检用：界面看到的**工作区**（默认位置按它算，见 <see cref="RawAnchor"/>）。</summary>
    internal RectF WorkAreaForTest => _work;

    /// <summary>
    /// 自检用：直接指定"拖过的位置"（`null` = 回默认位置），不经过真鼠标拖动。
    /// 用来验"进放映时归位"——那条要看的是**归位前**已经拖到别处这个前提，
    /// 而不是拖动手势本身（手势另有专测）。
    /// </summary>
    internal void SetAnchorForTest(Vector2? anchor)
    {
        _anchor = anchor;
        Invalidate();
    }

    /// <summary>自检用：贴边隐藏的进度（1 = 完全显示，0 = 只剩露头）。</summary>
    internal float PeekForTest => _peek.Value;

    /// <summary>
    /// 自检/出图用：把"球 → 带子"的展开进度一把按到某个值（0 = 球，1 = 完全展开）。
    /// 中间态平时只看得到 200 毫秒，想核对这一段长什么样就只能这么钉住它。
    /// </summary>
    internal void SetExpandForTest(float v) { _expand.Jump(v); _frameShiftValid = false; }

    /// <summary>
    /// 自检/出图用：把贴边隐藏一把按到某个进度（顺带允许"离开就收"）。
    /// 平时这条路要"指针离开过 700 毫秒"，出图时等不起。
    /// </summary>
    internal void ForcePeekForTest(float v)
    {
        _peekArmed = true;
        _peek.Jump(v);
        _frameShiftValid = false;
    }

    /// <summary>自检用：色线张开没有（false = 平时那条 6 像素的线）。</summary>
    internal bool RailOpenForTest => RailOpen;

    /// <summary>
    /// 自检用：焦点还在不在面板上（设置条该不该开着看它）。
    /// 旧的 `RailPinnedForTest` 随"点一次钉住"一起删掉了——那套状态已经不存在。
    /// </summary>
    internal bool RailHoverForTest => _railHover;

    /// <summary>自检用：某一格这一刻算不算"选中的"（穿透与工具互斥那条靠它看）。</summary>
    internal bool CellActiveForTest(int cell) => IsActive(cell, _host.State);

    /// <summary>
    /// 自检用：**这一刻档位下、完全展开后的带子宽**。
    /// 默认位置（"展开后那条带子居中"）按它算，自检要按同一个数反推球该在哪儿。
    /// </summary>
    internal float ExpandedWidthForTest => ExpandedWidth();

    /// <summary>自检用：上带这一刻多高（6 = 色线，34 = 完整设置条）。</summary>
    internal float BandHeightForTest => BandRect().MaxY - BandRect().MinY;

    // =====================================================================
    //  「最近文档」页（2026-11-16：原主页那一条 ＋ 「历史板书」格合并成一个有名字的页）
    //
    //  位置：第 7 格（保存/打开/回放/关文档 之后、位置不变）→ 面板内换页，标题「最近文档」，
    //  返回箭头回主页；**不弹系统对话框**（这是"看一个列表"的入口，不是"打开文件"）。
    //
    //  三组：PDF / 图片、PPT、板书；每组最多 4 条（最新的在前）。
    //  空组**不隐藏**，画一行灰字「暂无」；三组全空 = 居中一句空态。
    //  行：类型徽标 + 单行省略的名字 + 右对齐相对时间；悬停有高亮 + 提示（全名 + 完整路径）。
    //
    //  数据来源：
    //    · PDF / 图片 / PPT ← `RecentDocs` 偏好（引擎写、界面读，唯一一份）；
    //    · 板书 ← 新增引擎命令 `GetBoardHistory(4)`（BoardHistory.ListRecent，扫盘在引擎里）。
    //  扫盘 + File.Exists 有 2 秒缓存：绘制/命中每帧都问，但不会每帧都碰磁盘。
    // =====================================================================

    private const float RecentPageRowH = 34f;
    private const float RecentPageRowGap = 4f;
    private const int RecentPageMax = 4;            // 每组最多几条
    private const int RecentPageGroups = 3;         // 0 PDF/图片、1 PPT、2 板书
    private const float RecentPageGroupGap = 14f;
    private static readonly string[] RecentPageHeadNames = { "PDF / 图片", "PPT", "板书" };

    private static readonly (Color4 Fill, Color4 Ink, string Label) BoardBadge =
        (new Color4(0.16f, 0.55f, 0.42f, 0.16f), new Color4(0.12f, 0.48f, 0.36f, 1f), "板");

    /// <summary>一行的显示内容（绘制、命中、提示、自检读同一份）。</summary>
    private readonly struct RecentRowView
    {
        public RecentRowView(string badge, Color4 badgeFill, Color4 badgeInk,
                             string title, string time, string note, bool clickable)
        {
            Badge = badge;
            BadgeFill = badgeFill;
            BadgeInk = badgeInk;
            Title = title;
            Time = time;
            Note = note;
            Clickable = clickable;
        }

        public string Badge { get; }
        public Color4 BadgeFill { get; }
        public Color4 BadgeInk { get; }
        public string Title { get; }
        public string Time { get; }
        public string Note { get; }
        public bool Clickable { get; }
    }

    private readonly List<RecentDoc>[] _recentPageDocs = { new(), new() };
    private List<(string Path, DateTime LocalTime, long Bytes)> _recentPageBoards = new();
    private string _recentPageRaw;
    private double _recentPageAtMs;

    /// <summary>读一遍两份列表（2 秒缓存；条目里的文件不存在就跳过，不显示、不报错）。</summary>
    private void RefreshRecentPage()
    {
        string raw = _host?.GetPref(RecentDocs.PrefKey) ?? "";
        double now = _host?.NowMs ?? 0;
        if (string.Equals(raw, _recentPageRaw, StringComparison.Ordinal)
            && now - _recentPageAtMs < 2000)
            return;

        _recentPageRaw = raw;
        _recentPageAtMs = now;
        _recentPageDocs[0].Clear();
        _recentPageDocs[1].Clear();
        foreach (var d in RecentDocs.Parse(raw))
        {
            if (d == null || d.Paths.Length == 0) continue;
            string p = d.PrimaryPath;
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
            int group = RecentIsPpt(p) ? 1 : RecentIsPdfOrImage(p) ? 0 : -1;
            if (group < 0) continue;
            if (_recentPageDocs[group].Count < RecentPageMax) _recentPageDocs[group].Add(d);
        }

        var boards = _host?.Commands.GetBoardHistory(RecentPageMax);
        _recentPageBoards = boards ?? new List<(string Path, DateTime LocalTime, long Bytes)>();
    }

    private static bool RecentIsPpt(string path)
    {
        var ext = Path.GetExtension(path ?? "");
        return string.Equals(ext, ".ppt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".pptx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".pps", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".ppsx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RecentIsPdfOrImage(string path)
    {
        var ext = Path.GetExtension(path ?? "");
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tiff", StringComparison.OrdinalIgnoreCase);
    }

    private int RecentPageCount(int section)
    {
        RefreshRecentPage();
        return section switch
        {
            0 => _recentPageDocs[0].Count,
            1 => _recentPageDocs[1].Count,
            _ => Math.Min(RecentPageMax, _recentPageBoards.Count),
        };
    }

    /// <summary>
    /// 矮屏上每组最多显示几行（≤4）：先保证面板不把最后一行切掉（AGENTS §2「窄屏先减行」）。
    /// 条数仍以 <see cref="RecentPageCount"/> 为准，这里只决定"这一刻画几行、哪几行可点"。
    /// </summary>
    private int RecentPageFitRows()
    {
        float avail = (_work.MaxY - _work.MinY) - 24f
                      - (MorePad * 2 + MoreHeaderH + 10f)
                      - RecentPageGroups * MoreGroupHeadH
                      - (RecentPageGroups - 1) * RecentPageGroupGap;
        int fit = (int)MathF.Floor(avail / (RecentPageRowH + RecentPageRowGap));
        return Math.Clamp(fit / RecentPageGroups, 1, RecentPageMax);
    }

    /// <summary>这一组真画几行：有内容 = 内容条数（矮屏上截到能放下的行数）；空组 = 1 行「暂无」。</summary>
    private int RecentPageShownCount(int section)
        => Math.Max(1, Math.Min(RecentPageCount(section), RecentPageFitRows()));

    /// <summary>可点的行数（「暂无」和矮屏上没画出来的行都不算）。</summary>
    private int RecentPageClickableCount(int section)
        => Math.Min(RecentPageCount(section), RecentPageFitRows());

    private bool RecentPageAllEmpty
        => RecentPageCount(0) == 0 && RecentPageCount(1) == 0 && RecentPageCount(2) == 0;

    /// <summary>「最近文档」页高：按各组真显示几行算；三组全空 = 一句空态的高度。</summary>
    private float MoreRecentPageH()
    {
        if (RecentPageAllEmpty) return MorePad * 2 + MoreHeaderH + 8f + 112f;
        float h = MorePad * 2 + MoreHeaderH + 10f;
        for (int s = 0; s < RecentPageGroups; s++)
        {
            h += MoreGroupHeadH + RecentPageShownCount(s) * (RecentPageRowH + RecentPageRowGap);
            if (s < RecentPageGroups - 1) h += RecentPageGroupGap;
        }
        return h;
    }

    private RectF RecentPageHeadRect(int section)
    {
        float y = MoreHeaderRect().MaxY + 10f;
        for (int s = 0; s < section; s++)
            y += MoreGroupHeadH + RecentPageShownCount(s) * (RecentPageRowH + RecentPageRowGap)
                 + RecentPageGroupGap;
        return new RectF
        {
            MinX = MoreLeftX(), MinY = y,
            MaxX = MoreLeftX() + MoreContentW(), MaxY = y + MoreGroupHeadH,
        };
    }

    private RectF RecentPageRowRect(int section, int i)
    {
        var head = RecentPageHeadRect(section);
        float y = head.MaxY + i * (RecentPageRowH + RecentPageRowGap);
        return new RectF
        {
            MinX = MoreLeftX(), MinY = y,
            MaxX = MoreLeftX() + MoreContentW(), MaxY = y + RecentPageRowH,
        };
    }

    /// <summary>第 section 组第 i 行显示什么。<paramref name="i"/> 越界（空组）= 灰字「暂无」。</summary>
    private RecentRowView RecentPageRow(int section, int i)
    {
        if (i >= RecentPageCount(section))
            return new RecentRowView("", default, default, "暂无", "", "", false);

        if (section == 2)
        {
            var b = _recentPageBoards[i];
            string stamp = b.LocalTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            return new RecentRowView(BoardBadge.Label, BoardBadge.Fill, BoardBadge.Ink,
                $"板书 · {stamp}", RecentTimeText(b.LocalTime), b.Path, true);
        }

        var doc = _recentPageDocs[section][i];
        var style = RecentBadgeStyle(doc.PrimaryPath);
        return new RecentRowView(style.Label, style.Fill, style.Ink,
            RecentDocTitle(doc), RecentTimeText(doc.OpenedAtUnixSeconds),
            string.Join("；", doc.Paths), true);
    }

    private void DrawRecentPage(ID2D1DeviceContext ctx)
    {
        if (RecentPageAllEmpty)
        {
            _widgets.Text(ctx, "还没有最近使用的文件或板书",
                new RectF
                {
                    MinX = MoreLeftX(), MinY = MoreHeaderRect().MaxY + 8f,
                    MaxX = MoreLeftX() + MoreContentW(), MaxY = MoreRect().MaxY - MorePad,
                },
                13f, Brush(ctx, MutedCol));
            return;
        }

        for (int s = 0; s < RecentPageGroups; s++)
        {
            DrawMoreGroupHead(ctx, RecentPageHeadRect(s), RecentPageHeadNames[s]);
            int shown = RecentPageShownCount(s);
            for (int i = 0; i < shown; i++) DrawRecentPageRow(ctx, s, i);
        }
    }

    private void DrawRecentPageRow(ID2D1DeviceContext ctx, int section, int i)
    {
        var row = RecentPageRowRect(section, i);
        var view = RecentPageRow(section, i);

        if (!view.Clickable)
        {
            // 空组：一行灰字，组头照旧——"这里现在没有"必须看得见，不能整组消失。
            _widgets.TextLeftMiddle(ctx, view.Title,
                new RectF { MinX = row.MinX + 12f, MinY = row.MinY, MaxX = row.MaxX - 10f, MaxY = row.MaxY },
                12f, Brush(ctx, MutedCol));
            return;
        }

        int code = MoreHitRecentPageRow + section * RecentPageMax + i;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(row.MinX, row.MinY, row.MaxX, row.MaxY), 8f, 8f);
        // 每行垫一层很轻的槽底（和滑条/设置页同一个语言），不开悬停也看得出是一行。
        ctx.FillRoundedRectangle(rr, Brush(ctx, _dark ? Tokens.TroughDark : Tokens.TroughLight));
        if (_moreHover == code)
        {
            ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            ctx.DrawRoundedRectangle(rr,
                Brush(ctx, new Color4(Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, 0.55f)), 1f);
        }

        // 类型徽标：24×18，垂直居中。
        var badge = new RectF
        {
            MinX = row.MinX + 8f,
            MinY = row.MinY + (RecentPageRowH - 18f) * 0.5f,
            MaxX = row.MinX + 32f,
            MaxY = row.MinY + (RecentPageRowH - 18f) * 0.5f + 18f,
        };
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(badge.MinX, badge.MinY, badge.MaxX, badge.MaxY), 5f, 5f),
            Brush(ctx, view.BadgeFill));
        _widgets.Text(ctx, view.Badge, badge, 9.5f, Brush(ctx, view.BadgeInk));

        // 右侧时间：右对齐；名字拿剩下的宽度，放不下就省略号（提示里有全名）。
        float timeW = view.Time.Length == 0 ? 0f : MathF.Min(96f, _widgets.Measure(view.Time, 11f) + 2f);
        float nameLeft = badge.MaxX + 8f;
        float nameRight = row.MaxX - 10f - (timeW > 0f ? timeW + 8f : 0f);
        if (nameRight < nameLeft + 40f) nameRight = nameLeft + 40f;

        string name = Ellipsize(view.Title, nameRight - nameLeft, 12.5f);
        _widgets.TextLeftMiddle(ctx, name,
            new RectF { MinX = nameLeft, MinY = row.MinY, MaxX = nameRight, MaxY = row.MaxY },
            12.5f, Brush(ctx, InkCol));

        if (timeW > 0f)
            _widgets.Text(ctx, view.Time,
                new RectF { MinX = row.MaxX - 10f - timeW, MinY = row.MinY, MaxX = row.MaxX - 8f, MaxY = row.MaxY },
                11f, Brush(ctx, MutedCol));
    }

    /// <summary>点「最近文档」页某一行：关面板 → 真正打开（文件没了由引擎兜底成状态行）。</summary>
    private void ActivateRecentPageRow(int slot)
    {
        int section = slot / RecentPageMax;
        int i = slot % RecentPageMax;
        if (section < 0 || section >= RecentPageGroups || i >= RecentPageClickableCount(section)) return;

        if (section == 2)
        {
            string path = _recentPageBoards[i].Path;
            CloseMore();
            _host?.Commands.OpenBoardHistoryEntry(path);
        }
        else
        {
            var paths = _recentPageDocs[section][i].Paths;
            CloseMore();
            _host?.Commands.OpenRecentDocument(paths);
        }
        Invalidate();
    }

    private static string RecentDocTitle(RecentDoc d)
    {
        if (d == null || d.Paths.Length == 0) return "";
        string name = Path.GetFileName(d.PrimaryPath);
        return d.Paths.Length > 1 ? $"{name} 等 {d.Paths.Length} 个文件" : name;
    }

    /// <summary>相对时间：今天 HH:mm / 昨天 / MM-dd（跨年补年份）。</summary>
    private static string RecentTimeText(DateTime local)
    {
        if (local == default) return "";
        var now = DateTime.Now;
        if (local.Date == now.Date) return $"今天 {local:HH:mm}";
        if (local.Date == now.Date.AddDays(-1)) return "昨天";
        return local.Year == now.Year
            ? local.ToString("MM-dd", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string RecentTimeText(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime(); }
        catch { return ""; }
        return RecentTimeText(at.LocalDateTime);
    }

    /// <summary>文件类型小徽标：PDF 红 / PPT 橙 / 图片蓝 / 板书绿（跟主色一套，不引新颜色）。</summary>
    private static (Color4 Fill, Color4 Ink, string Label) RecentBadgeStyle(string path)
    {
        var ext = Path.GetExtension(path ?? "");
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            return (new Color4(0.85f, 0.22f, 0.22f, 0.16f), new Color4(0.80f, 0.17f, 0.17f, 1f), "PDF");
        if (RecentIsPpt(path))
            return (new Color4(0.92f, 0.48f, 0.12f, 0.16f), new Color4(0.86f, 0.42f, 0.08f, 1f), "PPT");
        if (RecentIsPdfOrImage(path))
            return (new Color4(Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, 0.14f), Tokens.Accent, "图");
        return BoardBadge;
    }

    /// <summary>单行省略（中文按字截）：放不下就二分找最长的可显示前缀 + "…"。</summary>
    private readonly Dictionary<(string Text, int Width, int Size), string> _ellipsisCache = new();

    private string Ellipsize(string text, float width, float size)
    {
        if (string.IsNullOrEmpty(text) || width <= 4f) return "";
        int wKey = (int)MathF.Round(width);
        int sKey = (int)MathF.Round(size * 100f);
        var key = (text, wKey, sKey);
        if (_ellipsisCache.TryGetValue(key, out var cached)) return cached;

        string fitted = text;
        if (_widgets.Measure(text, size) > width)
        {
            const string tail = "…";
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                int cut = mid;
                // 别把 emoji / 生僻字（代理对）劈成两半
                if (cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1])) cut--;
                if (_widgets.Measure(text[..cut] + tail, size) <= width) lo = mid;
                else hi = mid - 1;
            }
            int take = lo;
            if (take > 0 && take < text.Length && char.IsHighSurrogate(text[take - 1])) take--;
            fitted = take <= 0 ? tail : text[..take] + tail;
        }

        if (_ellipsisCache.Count > 256) _ellipsisCache.Clear();
        _ellipsisCache[key] = fitted;
        return fitted;
    }

    // ---- 「最近文档」页自检 / 出图钩子 ---------------------------------------

    internal int RecentPageGroupCountForTest(int section) => RecentPageCount(section);
    internal bool RecentPageEmptyForTest => RecentPageAllEmpty;
    internal float MoreHomeHForTest => MoreHomeH();
    internal float MoreRecentPageHForTest => MoreRecentPageH();
    internal RectF RecentPageRowRectForTest(int section, int i) => RecentPageRowRect(section, i);
    internal RectF RecentPageHeadRectForTest(int section) => RecentPageHeadRect(section);
    internal string RecentPageRowTitleForTest(int section, int i) => RecentPageRow(section, i).Title;
    internal bool RecentPageRowClickableForTest(int section, int i) => RecentPageRow(section, i).Clickable;
    internal int MoreHitRecentPageRowBaseForTest => MoreHitRecentPageRow;
    internal string HubTileLabelForTest(int code) => HubTileInfo(code).Label;
    internal string HubTileHintForTest(int code) => HubTileInfo(code).Hint;

    /// <summary>自检用：单行省略的结果（长中文文件名不许溢出）。</summary>
    internal string EllipsizeForTest(string text, float width, float size) => Ellipsize(text, width, size);

    /// <summary>
    /// 自检/出图用：直接写一条「最近文档」偏好（不真打开文件）。每条 path 算一份，
    /// 时间依次往前推一天——出图能同时看到"今天 / 昨天 / 日期"三种写法。
    /// 不传 = 清空（回到 0 条）。
    /// </summary>
    internal void SeedRecentDocsForTest(params string[] paths)
    {
        if (_host == null) return;
        if (paths == null || paths.Length == 0)
        {
            _host.SetPref(RecentDocs.PrefKey, null);
            Invalidate();
            return;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var list = new List<RecentDoc>(paths.Length);
        for (int i = 0; i < paths.Length; i++)
            list.Add(new RecentDoc(now - i * 86400L, new[] { paths[i] }));
        _host.SetPref(RecentDocs.PrefKey, RecentDocs.Format(list));
        Invalidate();
    }

    /// <summary>自检/出图用：清掉 2 秒缓存，立刻重读（种完假数据不用干等）。</summary>
    internal void RefreshRecentPageForTest()
    {
        _recentPageRaw = null;
        _recentPageAtMs = 0;
        RefreshRecentPage();
        Invalidate();
    }
}
