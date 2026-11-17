// [职责] 界面层合成与界面位图上传。
// [测试] --uitest

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>Process memory sampling, used to attribute the footprint to each
/// stage of start-up rather than quoting a single number.</summary>

internal sealed partial class OverlayWindow
{
    // ------------------------------------------------------------------
    //  界面层
    // ------------------------------------------------------------------

    /// <summary>
    /// 界面矩形是否盖住这个**逻辑**屏幕坐标（用于输入的第一次命中测试）。
    /// 传进来的坐标已由引擎除以 DPI 缩放，和界面自己的坐标系一致。
    /// </summary>
    public bool UiBoundsLogicalContains(float screenX, float screenY)
    {
        if (_uiLogicalBounds.IsEmpty) return false;
        return screenX >= _uiLogicalBounds.MinX && screenX < _uiLogicalBounds.MaxX
            && screenY >= _uiLogicalBounds.MinY && screenY < _uiLogicalBounds.MaxY;
    }

    /// <summary>诊断用：界面这一帧占的屏幕矩形。</summary>
    public string UiDebug => _uiBounds.IsEmpty
        ? "无"
        : $"({_uiBounds.MinX:F0},{_uiBounds.MinY:F0})-({_uiBounds.MaxX:F0},{_uiBounds.MaxY:F0})";

    /// <summary>
    /// 布局界面。返回界面这一帧是否可见。
    ///
    /// 坐标约定（这是引擎和界面之间最容易出错的一点，写死在这里）：
    /// 界面拿到的是"自己那块画布"的坐标，永远从 (0,0) 开始，单位是逻辑像素；
    /// 它把悬浮条摆在自己画布里的哪个位置，返回哪个矩形。引擎负责把逻辑矩形
    /// 乘以 dpiScale 变成屏幕上的物理矩形——界面不需要知道物理像素和 DPI。
    /// </summary>
    private bool PrepareUi(InkEngine app)
    {
        // 截图取景/调整期间：界面整块收起来（8.3.0）。
        //
        // 遮罩是"模态"的：工具条留在上面会跟遮罩打架（它在遮罩之上画、会亮着），
        // 而且"直接截取"会把它拍进图里。收起这一块由引擎做（不管界面自己怎么想），
        // 只是这一帧不画/不接输入——"界面崩了再造一个"那三级阶梯说的是 Ui 对象，不受影响。
        if (app.CaptureActive || app.ColorPickActive || app.LibraryPlacementMode)
        {
            _uiLogicalBounds = RectF.Empty;
            _uiBounds = RectF.Empty;
            return false;
        }

        if (!app.UiVisibleNow)
        {
            _uiLogicalBounds = RectF.Empty;
            _uiBounds = RectF.Empty;
            return false;
        }

        float k = Dpi / 96f * app.UiScale;   // 物理 = 界面逻辑 × k（DPI × 界面缩放）

        // 界面看到的"屏幕"是**整块虚拟桌面**，和 `IUiHost.Screen` 是同一个东西。
        //
        // 以前这里给的是**本窗口那一块显示器**的矩形（OriginX/Width 是覆盖窗口自己的）：
        // 同一个概念两个答案，界面按哪个算都会有一个是错的——副屏上"贴右下角"
        // 立刻偏一块。多显示器下每个覆盖窗口都会用这个值调一次 Layout，
        // 现在它们传的是同一个矩形，界面那边就是幂等的。
        //
        // 注：这里的逻辑尺寸仍用全局 DpiScale（第一块屏）。副屏 DPI 不同的完整支持
        // 是二期（难点 4），届时改成按窗口取。
        var uiScreen = app.UiLogicalScreen;   // 已除以 UiScale（界面放大后，它能摆的地方变小）

        // 界面只在布局会变的时候调（尺寸/DPI 变化）。正常每帧都走缓存。
        if (_uiLayoutBounds.IsEmpty || _uiLayoutDpi != k
            || !_uiLayoutScreen.Equals(uiScreen))
        {
            _uiLayoutBounds = app.UiLayoutNow(uiScreen, k);
        _uiLayoutScreen = uiScreen;
        _uiLayoutDpi = k;
        }

        // 占用矩形每帧都问一次：悬浮条被拖动、展开调色板、折叠收起、暂时消失，
        // 都靠这个方法告诉引擎；否则命中测试和脏区会一直停在旧位置。
        var live = app.UiQueryBoundsNow();
        _uiLogicalBounds = live;
        // 物理像素的占用矩形。**连"画到外面那一圈"一起算**（投影，见 IOverlayUi.PaintMargin）：
        // 不算进去的话，面板一移动（拖动/展开/收起），旧投影就擦不掉——脏区里没有它，
        // 屏幕上会留下一条越来越脏的印子。命中测试用的是上面那份**没放大的**逻辑矩形。
        float paintPad = app.UiPaintMarginNow * k;
        _uiBounds = live.IsEmpty ? RectF.Empty : new RectF
        {
            MinX = live.MinX * k - paintPad,
            MinY = live.MinY * k - paintPad,
            MaxX = live.MaxX * k + paintPad,
            MaxY = live.MaxY * k + paintPad,
        };

        return !live.IsEmpty;
    }

    private RectF _uiLayoutScreen = RectF.Empty;
    private float _uiLayoutDpi = -1f;
    /// <summary>Layout 上一次返回的逻辑矩形，用来判断是否需要重新布局。</summary>
    private RectF _uiLayoutBounds = RectF.Empty;

    /// <summary>
    /// 换界面时必须把它清掉：不然新界面**永远不会被调 Layout**（屏幕和 DPI 都没变，
    /// 缓存看起来还有效），它的 QueryBounds 会一直返回空——
    /// 表现就是"换上去的界面看不见、也点不到"。
    /// </summary>
    internal void InvalidateUiLayout()
    {
        _uiLayoutScreen = RectF.Empty;
        _uiLayoutDpi = -1f;
        _uiLayoutBounds = RectF.Empty;
    }

    /// <summary>
    /// 把内容层**整层作废**（回放切换"影子文档 / 真文档"、往回拖进度时用）。
    ///
    /// 为什么不能只靠 `doc.Version`：两份文档的版本号可能撞上，SyncTiles 里
    /// `_tilesVersion != doc.Version` 一比就"看起来没变"，屏幕上是上一份文档的像素。
    /// </summary>
    internal void ForceContentRebuild()
    {
        _tilesVersion = -1;
        _fullFramesLeft = 2;
        _tiles.MarkAllDirty();
    }

    /// <summary>
    /// 把界面画到后缓冲上。
    ///
    /// 每帧只重画界面自己那一小块矩形（裁剪 + 按其逻辑坐标绘制），整屏的其它
    /// 部分一律不动。实测这块的代价在 1 毫秒以内，和一个脏区补丁同量级。
    ///
    /// 两种"更省"的做法都试过，都不成立，别走回头路：
    ///  1) 把界面画进一张位图再 DrawBitmap 贴上去 —— Direct2D 的位图一旦作为
    ///     渲染目标，就不能再当绘制源，贴上去永远是一张空图（内容层因此才需要
    ///     _contentTarget / _contentSource 两个视图）。
    ///  2) 把界面的绘制录成 ID2D1CommandList 再重放 —— 录下来的命令重放时
    ///     在 2 倍屏上会被放大四次或被推到画布外，本机未能拿到稳定结果。
    /// 等界面真的变得很复杂（大量模糊/阴影）再回来解决，届时要连 4K 一起量。
    /// </summary>
    private void DrawUi(InkEngine app)
    {
        float k = Dpi / 96f * app.UiScale;
        // 界面画在自己的绝对逻辑坐标里（和它 Layout 拿到的逻辑屏幕同一套），
        // 引擎负责换算成物理像素：先乘 k（DPI × 界面缩放），再减去窗口原点。
        //
        // **这里绝对不能带相机**：界面是贴在屏幕上的工具条，画布滚它不动。
        // 以前这里用的是 CanvasToWindow（含 ViewOffsetY），相机为 0 时看不出
        // 问题，一滚动整个界面就会跟着内容往上跑——正是"坐标换算漏一处"
        // 那一类 bug 的又一例。
        _ctx.SetDpi(96f, 96f);
        _ctx.Transform = Matrix3x2.CreateScale(k)
                       * Matrix3x2.CreateTranslation(-OriginX, -OriginY);
        // 裁剪矩形同样用逻辑坐标（会被上面的变换一起作用）。
        // **往外放一圈**：界面会画到占用矩形外面（投影、浮出的预览，见 IOverlayUi.PaintMargin）。
        // 占用矩形本身**不放**——它同时是命中测试与输入小窗的矩形。
        float pad = app.UiPaintMarginNow;
        var clip = new Vortice.RawRectF(_uiLogicalBounds.MinX - pad, _uiLogicalBounds.MinY - pad,
                                        _uiLogicalBounds.MaxX + pad, _uiLogicalBounds.MaxY + pad);
        _ctx.PushAxisAlignedClip(clip, AntialiasMode.Aliased);
        // 防弹入口在引擎那边（`UiRenderNow`）：界面连抛三次就整体停用，笔迹照常。
        // 主题参数给的是"当前浮层主题"（界面推上来的那套）——我们的界面自己带令牌，
        // 不读这个参数；但万一将来有个界面想用引擎给的配色，拿到的应该是同一套。
        app.UiRenderNow(_ctx, app.FloatingTheme);
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.PopAxisAlignedClip();
    }

    /// <summary>供分辨率实测复用同一套绘制路径。</summary>
    public void DrawStrokeForTest(Stroke s) => DrawStroke(s);

    /// <summary>
    /// 把一批对象画到**离屏位图**上并读回 BGRA（复制到剪贴板时给外部程序那张图用）。
    ///
    /// 背景是**全透明**——粘到别处时不该带上我们的白底或桌面。
    /// <paramref name="region"/> 是这批对象在画布坐标里的范围（调用方算好，含一点留白），
    /// 位图尺寸 = 它的像素尺寸（画布坐标就是物理像素，1:1）。
    /// 返回 null = 建位图/绘制失败（调用方就当"这次没图"，对象格式照放）。
    /// </summary>
    /// <summary>字形轮廓填充渲染（开发/自检用）：走 <see cref="GlyphFill"/>。</summary>
    public byte[] RenderGlyphToBgra(string svg, Color4 color, out int w, out int h)
        => GlyphFill.Render(_ctx, svg, color, out w, out h);

    public byte[] RenderStrokesToBgra(IReadOnlyList<Stroke> strokes, RectF region, int w, int h)
    {
        if (_ctx == null || strokes == null || strokes.Count == 0 || w <= 0 || h <= 0) return null;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        ID2D1Bitmap1 target = null, cpu = null;
        try
        {
            target = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            cpu = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            _ctx.Target = target;
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0f, 0f, 0f, 0f));
            _ctx.Transform = Matrix3x2.CreateTranslation(-region.MinX, -region.MinY);
            foreach (var s in strokes) DrawStroke(s);
            _ctx.Transform = Matrix3x2.Identity;
            var hr = _ctx.EndDraw();
            _ctx.Target = null;
            if (hr.Failure) { LastError = "复制到剪贴板的离屏绘制失败: " + hr.Description; return null; }

            // D2D 位图 → CPU 可读位图 → 拷进内存
            cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
            var m = cpu.Map(MapOptions.Read);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
            cpu.Unmap();
            return buf;
        }
        catch (Exception ex)
        {
            LastError = "复制到剪贴板失败: " + ex.Message;
            return null;
        }
        finally
        {
            target?.Dispose();
            cpu?.Dispose();
        }
    }

    /// <summary>
    /// 把**界面**离屏画进一张位图并读回 BGRA（开发期出图用）。
    ///
    /// 为什么单开这条路：平时的出图是"截屏"，那要求屏幕亮着、会话没锁，
    /// 图里还会混进桌面上的东西。这条路把界面画到自己的位图上——
    /// **锁屏 / 远程 / 这台机器上没人看着**的时候照样能出图，以后也便于接 CI。
    ///
    /// 背景用一层浅灰（不是透明）：半透明白面板压在透明底上看不清边界。
    /// </summary>
    public byte[] RenderUiToBgra(InkEngine app, int padPx, out int w, out int h)
    {
        if (app.Ui == null) { w = h = 0; return null; }
        return RenderUiToBgra(app, padPx, app.UiQueryBoundsNow(), out w, out h);
    }

    /// <summary>
    /// 同上，但**画哪一块自己指定**（逻辑像素）。
    ///
    /// 界面自己的占用区只够拍那颗球和那条带子；像"选中框 + 操作条 + 导出格式面板"
    /// 这类画在浮动层上的东西压根不在里面，得把范围一起给进来。
    /// </summary>
    public byte[] RenderUiToBgra(InkEngine app, int padPx, in RectF bounds, out int w, out int h)
        => RenderToBgra(app, padPx, bounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: false, out w, out h);

    /// <summary>
    /// 同上，但底色**透明**（给"用界面自己的渲染生成程序图标"用，见 `--makeicon`）。
    ///
    /// 为什么单开一条而不是给上面那条加参数：那条的灰底是给"出图给人看"用的
    /// （图里得有底色才看得清白卡片），而图标要的是"只有那个圆"。
    /// </summary>
    public byte[] RenderUiToBgraTransparent(InkEngine app, int padPx, in RectF bounds,
                                            out int w, out int h)
        => RenderToBgra(app, padPx, bounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: false, out w, out h,
                        clear: new Color4(0f, 0f, 0f, 0f));

    /// <summary>
    /// **浮动层**离屏出图：选中框、八个手柄、操作条、挂在条下面的小面板（颜色/层级/导出格式）。
    ///
    /// 和上面那条的区别只在**坐标系**：界面画在自己的逻辑屏幕坐标里（乘 DPI 就完事），
    /// 浮动层画的是**画布坐标**（相机还没减）。所以这里不乘缩放，直接把
    /// "画布坐标 → 位图"写出来，`bounds` 也要给画布坐标。
    ///
    /// 单开这条的理由和界面那条一样：这些家具全在浮动层上，桌面截图截不到干净的一版，
    /// 而它们的排版（字有没有居中、两条线离多远）**只能靠图看**，几何自检看不出来。
    /// </summary>
    public byte[] RenderFloatingToBgra(InkEngine app, int padPx, in RectF canvasBounds, out int w, out int h)
        => RenderToBgra(app, padPx, canvasBounds, dpiScale: Dpi / 96f,
                        floatingCanvasSpace: true, out w, out h);

    private byte[] RenderToBgra(InkEngine app, int padPx, in RectF bounds, float dpiScale,
                                bool floatingCanvasSpace, out int w, out int h,
                                Color4? clear = null)
    {
        w = h = 0;
        if (_ctx == null) return null;
        // 浮动层这条路不画界面，所以不要求界面挂着；界面那条必须挂着才有东西可画。
        if (!floatingCanvasSpace && (app.Ui == null || !app.UiVisibleNow)) return null;
        if (bounds.IsEmpty) return null;
        _app = app;                 // 画的过程中有几处要读"当前窗口"的状态

        float dpi = dpiScale;
        // 界面的离屏出图必须和 DrawUi 用同一个乘数（DPI × 界面缩放）；
        // 浮动层那条不乘（画布坐标本来就是物理像素，见下面 unit 一段）。
        float uiK = dpi * app.UiScale;
        // **两条路的单位不一样**（这一条踩过）：界面画在自己的**逻辑**坐标里，要乘 DPI 换成
        // 物理像素；画布坐标**本身就是物理像素**（上下文 DPI 固定 96，`CanvasToWindow`
        // 也不带缩放），所以 1 个单位就是 1 个位图像素。第一版给浮动层也乘了 DPI，
        // 位图开成两倍大、内容却按 1:1 画，整个画面被推到左边去了。
        float unit = floatingCanvasSpace ? 1f : uiK;
        w = (int)MathF.Ceiling((bounds.MaxX - bounds.MinX) * unit) + padPx * 2;
        h = (int)MathF.Ceiling((bounds.MaxY - bounds.MinY) * unit) + padPx * 2;
        if (w <= 0 || h <= 0 || w > 8000 || h > 8000) return null;

        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        ID2D1Bitmap1 target = null, cpu = null;
        try
        {
            target = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            cpu = _ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

            _ctx.Target = target;
            _ctx.BeginDraw();
            // 底色：**没指定**才铺浅灰（出图给人看时要有底）；指定了就照指定的来——
            // 尤其是"指定成全透明"（图标那条路），以前用 `clear.A > 0` 判，
            // 把"没指定"和"要透明"混成了一件事，结果图标里永远垫着一块浅灰方块
            //（2026-09-29 用户："图标虽然是圆的，但它实际上是一个方形"）。
            _ctx.Clear(clear ?? new Color4(0.93f, 0.94f, 0.96f, 1f));
            _ctx.SetDpi(96f, 96f);

            if (floatingCanvasSpace)
            {
                // 画布坐标就是物理像素：只挪，不平移缩放（见上面 unit 那一段）。
                _ctx.Transform = Matrix3x2.CreateTranslation(padPx - bounds.MinX,
                                                             padPx - bounds.MinY);
                // 拍这一块里的墨（内容层），否则取景框、选中框都浮在空白上，
                // 看不出"框有没有圈住东西"。只画和这一块相交的那几条。
                foreach (var s in app.Doc.Strokes)
                    if (s.PaddedBounds.Intersects(bounds) && !app.IsContentDetached(s)) DrawStroke(s);
                // 拖端点手势中：模型还是旧几何，临时几何只活在浮动层上——
                // 出图这条路不补画它，拍出来的就是"手柄不见了、线也没动"的空镜头。
                // （被摘出去的那一批上面已经跳过，所以不会出现"旧线 + 新线"双影。）
                DrawVertexPreview(app);
                // **拖动预览**（移动/旋转手势中被摘出内容层的那一批）同理：
                // 不补画的话，"旋转中"那张图会只剩一个框、线不见了（2026-09-18 出图核对时踩到）。
                DrawDragPreview(app);
                // **正在书写的那一笔**（画线中的实时几何）也不在 Doc 里，得单独补——
                // 不然"画线过程中的 α 读数"这张图拍出来只有一颗标签、没有线。
                if (app.ActiveStroke != null && !app.SuppressActiveStroke
                    && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
                    DrawStroke(app.ActiveStroke);
                if (app.PredictTailActive && !app.ActiveStroke.HiddenForPairing
                    && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
                    DrawPredictTail(app.ActiveStroke.Color, app.PredictTailWidth);
                DrawSelection(app);
                // **图库面板**同理（2026-09-22 加）：它是浮动层上的东西，不补画的话
                // "面板长什么样"这张图永远拍不到（这条路是给自检/出图用的，
                // 少画一样就是"看了一张没有面板的图还以为面板没开"）。
                DrawLibraryGhost(app);
                DrawLibraryPanel(app);
                DrawShapeInclination(app);
                DrawCaptureOverlay(app, _ctx.Transform);   // 截图取景（含遮罩/冻结帧）——不在截图态就直接返回
            }
            else
            {
                // 和 DrawUi 同一套坐标：先乘 DPI×界面缩放，再把界面左上角挪到留白处
                _ctx.Transform = Matrix3x2.CreateScale(uiK)
                               * Matrix3x2.CreateTranslation(-bounds.MinX * uiK + padPx,
                                                             -bounds.MinY * uiK + padPx);
                app.UiRenderNow(_ctx, app.FloatingTheme);
            }
            _ctx.Transform = Matrix3x2.Identity;
            var hr = _ctx.EndDraw();
            _ctx.Target = null;
            if (hr.Failure) { LastError = "界面离屏绘制失败: " + hr.Description; return null; }

            cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
            var m = cpu.Map(MapOptions.Read);
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
            cpu.Unmap();
            return buf;
        }
        catch (Exception ex)
        {
            LastError = "界面离屏绘制失败: " + ex.Message;
            return null;
        }
        finally
        {
            target?.Dispose();
            cpu?.Dispose();
        }
    }

    /// <summary>供分辨率实测复用同一套"擦掉一块"逻辑。</summary>
    public void ClearRectForTest(RectF r)
    {
        _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        _ctx.FillRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), _transparentBrush);
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
    }

    /// <summary>
    /// 这一帧"**只画辅助几何、不画主体**"的那一个对象：
    /// 双曲线三步式的第一步（正在晃渐近线）——屏幕上先只出那两条虚线，
    /// 曲线要等第 2 下点击之后才出现（见 <see cref="InkEngine.HyperAsymptotePreviewOnly"/>）。
    ///
    /// 由 <see cref="RenderFrame"/> 每帧开头设一次，**不进数据对象、不进存档、也不影响导出**
    /// （导出走的是另一条渲染路径，根本不设它）。
    /// </summary>
    private Stroke _auxOnlyStroke;

    /// <summary>
    /// 画一个对象。**调用方负责把 ctx 变换设成"画布坐标 → 窗口坐标"**，
    /// 这里只在对象自己带变换时再左乘一下。
    ///
    /// 单位变换（绝大多数对象）走的是原路，一次多余的取/设变换都不做——
    /// 这条路径每帧要给上万个对象跑，不能为了"以后可能用到"先付成本。
    ///
    /// <paramref name="extra"/> 是"额外再叠一层实时矩阵"的场合（拖动预览），
    /// 传了就不去动 ctx 变换（见下面的分岔）。
    /// </summary>
    private void DrawStroke(Stroke s, Matrix3x2? extra = null)
    {
        // **配对期的那一笔先别画**（用户 2026-09-25 定："识别的那一刻，第一笔就该消失"）。
        //
        // ⚠ 放在**逐笔绘制**这一层、不是放在主循环里：这样**所有调用方**都会跳过它
        //   （主渲染 / 导出 / 自检 / 剪贴板），一处就够，不会出现"某条路忘了过滤、
        //   导出的图里多出一条墨"那种不一致。
        //   这一位只管**画**：文档不动、撤销不动（见 `Stroke.HiddenForPairing`）。
        if (s.HiddenForPairing) return;

        // **"线宽不变"的对象**（图形，见 Stroke.KeepsWidth）：变换折进几何里
        //（BuildCanvasGeometry），描边发生在**画布空间** —— 宽度就是 Width，
        // 不会被缩放。好处很直接：横着拉一个矩形，四条边还是原来那么粗。
        if (s.KeepsWidth && (!s.Transform.IsIdentity || extra.HasValue))
        {
            DrawStrokeCore(s, extra);
            return;
        }

        if (s.Transform.IsIdentity && extra == null)
        {
            DrawStrokeCore(s);
            return;
        }

        // 局部 → 画布（s.Transform × extra），再 画布 → 窗口（调用方设的）。
        // 乘法顺序按 System.Numerics 的约定：先作用左边的。
        var canvasToWindow = _ctx.Transform;
        _ctx.Transform = s.Transform * (extra ?? Matrix3x2.Identity) * canvasToWindow;
        DrawStrokeCore(s);
        _ctx.Transform = canvasToWindow;
    }

    /// <summary>
    /// B4 预测尾（`--predict2`）：**只画显示的"影子"**——独立的一小段几何、每帧重建，
    /// 不进文档/存档/命中/撤销（它连 Stroke 都不是）。起点锚在"正在显示的平滑输出末点"上
    /// （见 Engine.UpdatePredictTail），接头处连续；长度由引擎侧限速平滑——
    /// 旧系统"突突跳"的正身（甩出去/缩回来）就是被这个限速和"断笔后缓慢回收"治的。
    /// </summary>
    private void DrawPredictTail(Color4 color, float width)
    {
        var eng = _app;
        if (eng == null) return;
        var pts = eng.PredictTailPoints;
        if (pts.Count < 2) return;
        using var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Hollow);
            for (int i = 1; i < pts.Count; i++) sink.AddLine(pts[i]);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();               // ⚠ 必须显式 Close：Dispose 不保证把几何封口（不封 = 空几何）
        }
        _ctx.DrawGeometry(geo, Brush(color), MathF.Max(1f, width), Gfx.StyleFor(StrokeDash.Solid));
    }

    /// <summary>字形填充对象：逐条闭合轮廓 → even-odd 填充。</summary>
    private void DrawGlyphStroke(Stroke s)
    {
        if (s.Points.Count < 3 || _ctx == null) return;
        using var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.SetFillMode(Vortice.Direct2D1.FillMode.Alternate);
            int i = 0;
            while (i < s.Points.Count)
            {
                var p0 = s.Points[i];
                sink.BeginFigure(new Vector2(p0.X, p0.Y), FigureBegin.Filled);
                i++;
                while (i < s.Points.Count && s.Points[i].P >= 0)
                {
                    var p = s.Points[i];
                    sink.AddLine(new Vector2(p.X, p.Y));
                    i++;
                }
                sink.EndFigure(FigureEnd.Closed);
            }
            sink.Close();
        }
        _ctx.FillGeometry(geo, Brush(s.Color));
    }

    private void DrawStrokeCore(Stroke s, Matrix3x2? extra = null)
    {
        if (s.Kind == StrokeKind.Glyph) { DrawGlyphStroke(s); return; }

        // AI 题卡：结构化内容，由 DirectWrite 逐行画；不是位图、也不是几何。
        // **必须放在最前面**：它和图像一样没有可描边的中心线。
        if (s.Kind == StrokeKind.TextBox)
        {
            DrawTextBox(s);
            return;
        }

        // 交互矢量图（AI 作图 v2）：D2D 直接画曲线/文字，不生成位图。
        // 和 TextBox 一样必须在几何分支之前分流：它没有可描边的中心线。
        if (s.Kind == StrokeKind.Figure)
        {
            DrawFigure(s, extra);
            return;
        }

        // 图像对象：画的是位图，不是几何。**必须放在最前面**——它和图形一样
        // 属于"非自由笔迹"，走到下面那条 DrawGeometry 分支就会被描一个矩形边框。
        if (s.IsImage)
        {
            var bmp = s.Image?.GetBitmap(_ctx);
            if (bmp == null) return;
            var a = new Vector2(s.Points[0].X, s.Points[0].Y);
            var b = s.Points.Count > 1
                ? new Vector2(s.Points[^1].X, s.Points[^1].Y)
                : a;
            var dst = new Vortice.RawRectF(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y),
                                           MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
            // 缩放时用线性插值：最近邻在投影上会让文字边缘全是锯齿（放大看更明显）。
            _ctx.DrawBitmap(bmp, dst, 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            return;
        }

        // **只画辅助几何、先不画主体**的那个判据（见 _auxOnlyStroke 那段说明）。
        bool auxOnly = ReferenceEquals(s, _auxOnlyStroke);
        // 几何走 BuildCanvasGeometry：图形的变换**折进几何里**（线宽不跟着缩放，
        // 见 Stroke.KeepsWidth），笔迹按老样子返回局部几何（ctx 上带着它的变换）。
        var geo = auxOnly ? null : s.BuildCanvasGeometry(Gfx.D2DFactory, aux: false, extra);
        if (geo == null && !auxOnly) return;
        // **只画辅助几何、不画主体**：双曲线三步式的第一步（正在晃渐近线）——
        // 屏幕上先只出那两条虚线渐近线，曲线要等第 2 下点击之后才跟着指针出现
        //（照 InkClass 的第一笔，见 Engine.HyperAsymptotePreviewOnly）。
        // 主体不画时 `geo` 是 null，下面那两行描边自然跳过，辅助几何照画。
        if (geo != null)
        {
            // 两种画法：
            //   · 单点笔迹 → 几何本身就是一个圆，填充它（零长度的线描边什么都画不出来）；
            //   · 其余（笔迹的中心线、直线/矩形/椭圆/箭头）→ 统一交给 D2D 描边：
            //     宽度、端帽、拐角全由它算（2026-09-14 起，我们自己的轮廓代码已删除）；
            //     **线型（实线/虚线/点线）就在这一步按 <see cref="StrokeDash"/> 选描边样式** ——
            //     全引擎只此一处，任何"能画出图形的路径"（内容层、浮动预览、导出、自检出图）
            //     都经过 DrawStroke，所以线型自动都生效，不用逐处补。
            if (s.RendersAsDot) _ctx.FillGeometry(geo, Brush(s.Color));
            else if (!DrawPressureInk(s))
                _ctx.DrawGeometry(geo, Brush(s.Color), MathF.Max(1f, s.Width), Gfx.StyleFor(s.Dash));
        }

        // **焦点小样**：正式画布上是空心小圆；卡片里缩到 44px 后会变成两个大圈，
        // 这里改成固定大小的小实心点（只影响图形卡小样）。
        if (s.Kind == StrokeKind.ConicEllipse && s.PreviewFocusDots)
        {
            var m = s.Transform * (extra ?? Matrix3x2.Identity);
            var (f1, f2) = s.ConicEllipseFociLocal();
            float r = 1.3f * (Dpi / 96f);
            _ctx.FillEllipse(new Ellipse(Vector2.Transform(f1, m), r, r), Brush(s.Color));
            _ctx.FillEllipse(new Ellipse(Vector2.Transform(f2, m), r, r), Brush(s.Color));
        }

        // **辅助几何**（双曲线的两条虚线渐近线、立体图形被挡的半圈、**坐标系的网格**）：
        // 同一支笔的墨色，但**风格由身份定**——
        //   · 辅助线（渐近线 / 被挡的棱）：恒定细虚线，粗细 0.6 倍（辅助线的本分是不抢主线）；
        //   · **坐标系网格**（用户 2026-09-24："变细变淡"）：**细一半、淡一半的实线**——
        //     整屏格子铺开时，"淡"比"细"更决定它抢不抢眼。
        // 放在主几何之后画：两者重叠的地方（顶点附近 / 原点）以主线为准。
        if (!s.PreviewNoAux)
        {
            var aux = s.BuildCanvasGeometry(Gfx.D2DFactory, aux: true, extra);
            if (aux != null)
            {
                bool grid = s.Kind == StrokeKind.Coordinate && s.Grid;
                var auxColor = grid ? Stroke.Fade(s.Color, Stroke.AxisGridAlpha) : s.Color;
                if (s.PreviewAuxAlpha > 0.01f) auxColor = Stroke.Fade(auxColor, s.PreviewAuxAlpha);
                float auxScale = grid ? Stroke.AxisGridWidthFactor
                                : (s.PreviewAuxWidthScale > 0.01f ? s.PreviewAuxWidthScale : 0.6f);
                _ctx.DrawGeometry(aux, Brush(auxColor),
                                  MathF.Max(1f, s.Width * auxScale),
                                  Gfx.StyleFor(grid ? StrokeDash.Solid : StrokeDash.Dashed));
            }
        }

        // **圆心点是墨迹本身**（2026-10-18 用户定）：只有圆要，一直存在，
        // 选中不消失、导出/存档都带着。画在主几何之后，颜色同笔迹；大小固定。
        DrawShapeCenterInk(s, extra);
    }

    /// <summary>
    /// 画交互矢量图（AI 作图 v2）：白卡 + 坐标轴/网格/刻度 + 曲线 + 标题/图例/参数。
    /// 坐标系是**局部 1100×760**，`s.Transform` 负责放置/缩放；字号和线宽跟着缩放
    /// （像图片，不像几何图形）。不生成位图、不进额外缓存。
    /// </summary>
    private void DrawFigure(Stroke s, Matrix3x2? extra)
    {
        var spec = s.Figure;
        if (spec == null || s.Points.Count < 2) return;

        var p0 = new Vector2(s.Points[0].X, s.Points[0].Y);
        var p1 = new Vector2(s.Points[1].X, s.Points[1].Y);
        float w = MathF.Abs(p1.X - p0.X);
        float h = MathF.Abs(p1.Y - p0.Y);
        if (w < 20f || h < 20f) return;
        float ox = MathF.Min(p0.X, p1.X), oy = MathF.Min(p0.Y, p1.Y);

        var saved = _ctx.Transform;
        // DrawStroke 已经在外面把 s.Transform × extra 放进 ctx 了；
        // 这里直接用局部坐标画，不能再乘一次（会双重缩放/偏移）。
        _ctx.PushAxisAlignedClip(new Vortice.RawRectF(ox, oy, ox + w, oy + h),
                                 AntialiasMode.Aliased);
        try
        {
            var bg = new RoundedRectangle(new Vortice.RawRectF(ox, oy, ox + w, oy + h), 26f, 26f);
            _ctx.FillRoundedRectangle(bg, Brush(new Color4(1f, 1f, 1f, 1f)));

            float top = spec.Params.Count > 0 ? 94f : 64f;
            float left = 92f, right = 44f, bottom = 74f;
            float pw = w - left - right;
            float ph = h - top - bottom;
            if (pw < 50f || ph < 50f) return;

            float xMin = spec.XMin, xMax = spec.XMax;
            int samples = Math.Clamp((int)(pw * 1.4f), 600, 1800);
            var curves = PlotSampling.Build(spec, samples, out float yMin, out float yMax);
            PlotSampling.FitConicAspect(spec, ref xMin, ref xMax, ref yMin, ref yMax, pw, ph);

            float X(float x) => ox + left + (x - xMin) / (xMax - xMin) * pw;
            float Y(float y) => oy + top + (yMax - y) / (yMax - yMin) * ph;

            var gridColor = new Color4(0.91f, 0.92f, 0.94f, 1f);
            var axisColor = new Color4(0.27f, 0.29f, 0.32f, 1f);
            var tickColor = new Color4(0.35f, 0.37f, 0.40f, 1f);
            var labelColor = new Color4(0.32f, 0.34f, 0.37f, 1f);
            var faint = new Color4(0.48f, 0.50f, 0.55f, 0.82f);

            float xStep = NiceStep(xMax - xMin, 8);
            float yStep = NiceStep(yMax - yMin, 7);
            for (float x = MathF.Ceiling(xMin / xStep) * xStep;
                 x <= xMax + 1e-4f; x += xStep)
                _ctx.DrawLine(new Vector2(X(x), oy + top),
                              new Vector2(X(x), oy + top + ph),
                              Brush(gridColor), 1f);
            for (float y = MathF.Ceiling(yMin / yStep) * yStep;
                 y <= yMax + 1e-4f; y += yStep)
                _ctx.DrawLine(new Vector2(ox + left, Y(y)),
                              new Vector2(ox + left + pw, Y(y)),
                              Brush(gridColor), 1f);

            float axisY = Y(0f);
            float axisX = X(0f);
            if (axisY < oy + top || axisY > oy + top + ph) axisY = oy + top + ph;
            if (axisX < ox + left || axisX > ox + left + pw) axisX = ox + left;

            _ctx.DrawLine(new Vector2(ox + left, axisY),
                          new Vector2(ox + left + pw, axisY), Brush(axisColor), 2f);
            _ctx.DrawLine(new Vector2(axisX, oy + top + ph),
                          new Vector2(axisX, oy + top), Brush(axisColor), 2f);
            // 箭头
            _ctx.DrawLine(new Vector2(ox + left + pw, axisY),
                          new Vector2(ox + left + pw - 12f, axisY - 6f), Brush(axisColor), 2f);
            _ctx.DrawLine(new Vector2(ox + left + pw, axisY),
                          new Vector2(ox + left + pw - 12f, axisY + 6f), Brush(axisColor), 2f);
            _ctx.DrawLine(new Vector2(axisX, oy + top),
                          new Vector2(axisX - 6f, oy + top + 12f), Brush(axisColor), 2f);
            _ctx.DrawLine(new Vector2(axisX, oy + top),
                          new Vector2(axisX + 6f, oy + top + 12f), Brush(axisColor), 2f);

            var tickFmt = AiTextFormat(20f, false, true);
            for (float x = MathF.Ceiling(xMin / xStep) * xStep;
                 x <= xMax + 1e-4f; x += xStep)
            {
                if (MathF.Abs(x) < xStep * 0.01f) continue;
                _ctx.DrawLine(new Vector2(X(x), axisY - 4f),
                              new Vector2(X(x), axisY + 4f), Brush(tickColor), 1.6f);
                FigureText(Fmt(x), X(x), axisY + 29f, 40f, 24f, tickFmt, labelColor);
            }
            for (float y = MathF.Ceiling(yMin / yStep) * yStep;
                 y <= yMax + 1e-4f; y += yStep)
            {
                if (MathF.Abs(y) < yStep * 0.01f) continue;
                _ctx.DrawLine(new Vector2(axisX - 4f, Y(y)),
                              new Vector2(axisX + 4f, Y(y)), Brush(tickColor), 1.6f);
                FigureText(Fmt(y), axisX - 30f, Y(y), 48f, 24f, tickFmt, labelColor);
            }
            FigureText("O", axisX - 14f, axisY + 24f, 30f, 26f, tickFmt, labelColor);
            FigureText("x", ox + left + pw - 14f, axisY - 16f, 30f, 26f, tickFmt, labelColor);
            FigureText("y", axisX + 16f, oy + top + 10f, 30f, 26f, tickFmt, labelColor);

            // 曲线
            _ctx.PushAxisAlignedClip(
                new Vortice.RawRectF(ox + left, oy + top, ox + left + pw, oy + top + ph),
                AntialiasMode.Aliased);
            foreach (var curve in curves)
            {
                using var geo = BuildCurveGeometry(curve.Points, X, Y, ph);
                if (geo == null) continue;
                _ctx.DrawGeometry(geo, Brush(curve.Series.Color), 3.2f,
                                  Gfx.StyleFor(curve.Series.Dashed
                                      ? StrokeDash.Dashed : StrokeDash.Solid));
                if (curve.Breaks != null)
                {
                    var whiteDot = Brush(new Color4(1f, 1f, 1f, 1f));
                    var colorDot = Brush(curve.Series.Color);
                    foreach (var (bx, by, filled) in curve.Breaks)
                    {
                        if (!float.IsFinite(bx) || !float.IsFinite(by)) continue;
                        var c = new Vector2(X(bx), Y(by));
                        if (filled)
                            _ctx.FillEllipse(new Ellipse(c, 4.4f, 4.4f), colorDot);
                        else
                        {
                            _ctx.FillEllipse(new Ellipse(c, 4.4f, 4.4f), whiteDot);
                            _ctx.DrawEllipse(new Ellipse(c, 3.5f, 3.5f), colorDot, 1.8f);
                        }
                    }
                }
            }
            if (spec.Kind == "conic" && spec.Conic != null)
                DrawConicFeaturesD2D(spec.Conic, X, Y, ox + left, oy + top, pw, ph,
                                     xMin, xMax);
            _ctx.PopAxisAlignedClip();

            // 图例
            bool wantLegend = curves.Count > 1
                              || (spec.Kind == "conic" && curves.Count > 0);
            if (wantLegend)
            {
                var legendFmt = AiTextFormat(17f, false, false);
                float maxText = 0f;
                foreach (var c in curves)
                {
                    string text = string.IsNullOrWhiteSpace(c.Series.Label)
                        ? "y=" + c.Series.Expr : c.Series.Label;
                    maxText = MathF.Max(maxText, MeasureTextWidth(text, legendFmt));
                }
                float lw = maxText + 56f;
                float lh = curves.Count * 26f + 16f;
                float lx = ox + left + pw - lw - 10f;
                float ly = oy + top + 12f;
                var lr = new Vortice.RawRectF(lx, ly, lx + lw, ly + lh);
                _ctx.FillRoundedRectangle(new RoundedRectangle(lr, 8f, 8f),
                    Brush(new Color4(1f, 1f, 1f, 0.92f)));
                _ctx.DrawRoundedRectangle(new RoundedRectangle(lr, 8f, 8f),
                    Brush(new Color4(0.86f, 0.88f, 0.90f, 1f)), 1f);
                for (int i = 0; i < curves.Count; i++)
                {
                    var c = curves[i];
                    float yy = ly + 8f + 13f + i * 26f;
                    _ctx.DrawLine(new Vector2(lx + 10f, yy), new Vector2(lx + 38f, yy),
                                  Brush(c.Series.Color), 3.2f);
                    string text = string.IsNullOrWhiteSpace(c.Series.Label)
                        ? "y=" + c.Series.Expr : c.Series.Label;
                    _ctx.DrawText(text, legendFmt,
                        new Rect(lx + 46f, yy - 11f, lw - 52f, 24f),
                        Brush(new Color4(0.23f, 0.25f, 0.28f, 1f)));
                }
            }

            // 标题 / 参数
            if (!string.IsNullOrWhiteSpace(spec.Title))
            {
                var titleFmt = AiTextFormat(26f, false, false);
                _ctx.DrawText(spec.Title, titleFmt,
                    new Rect(ox + left, oy + 12f, pw, 36f),
                    Brush(new Color4(0.13f, 0.15f, 0.18f, 1f)));
            }
            if (spec.Params.Count > 0)
            {
                var keys = new List<string>(spec.Params.Keys);
                keys.Sort(StringComparer.Ordinal);
                var parts = new List<string>(keys.Count);
                foreach (var k in keys)
                    parts.Add(k + " = " + Fmt((float)spec.Params[k]));
                var paramFmt = AiTextFormat(18f, false, false);
                _ctx.DrawText(Loc.T("参数：{0}", string.Join("，", parts)), paramFmt,
                    new Rect(ox + left, oy + 46f, pw, 26f),
                    Brush(new Color4(0.36f, 0.38f, 0.42f, 1f)));
            }
        }
        finally
        {
            _ctx.PopAxisAlignedClip();
            _ctx.Transform = saved;
        }
    }

    private static ID2D1PathGeometry BuildCurveGeometry(
        List<(float X, float Y)> pts, Func<float, float> X, Func<float, float> Y,
        float plotHeight)
    {
        if (pts == null || pts.Count < 2) return null;
        var geo = Gfx.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            bool open = false;
            float prevY = 0f;
            foreach (var (x, y) in pts)
            {
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    if (open) { sink.EndFigure(FigureEnd.Open); open = false; }
                    continue;
                }
                float sx = X(x), sy = Y(y);
                bool jump = open && MathF.Abs(sy - prevY) > plotHeight * 1.4f;
                if (!open || jump)
                {
                    if (open) sink.EndFigure(FigureEnd.Open);
                    sink.BeginFigure(new Vector2(sx, sy), FigureBegin.Hollow);
                    open = true;
                }
                else sink.AddLine(new Vector2(sx, sy));
                prevY = sy;
            }
            if (open) sink.EndFigure(FigureEnd.Open);
            sink.Close();               // ⚠ 必须显式 Close：Dispose 不保证封口（不封 = 空几何）
        }
        return geo;
    }

    private void DrawConicFeaturesD2D(PlotConic c, Func<float, float> X, Func<float, float> Y,
                                      float left, float top, float pw, float ph,
                                      float xMin, float xMax)
    {
        float a = MathF.Max(0.0001f, MathF.Abs(c.A));
        float b = MathF.Max(0.0001f, MathF.Abs(c.B));
        string type = (c.Type ?? "").ToLowerInvariant();
        var dashColor = new Color4(0.48f, 0.50f, 0.55f, 0.82f);
        var dotColor = new Color4(0.77f, 0.30f, 0.24f, 1f);
        var labelColor = new Color4(0.67f, 0.27f, 0.21f, 1f);
        var tiny = AiTextFormat(15f, false, false);
        var dashStyle = Gfx.StyleFor(StrokeDash.Dashed);

        void Vertical(float x, string text)
        {
            float sx = X(x);
            if (sx < left - 4f || sx > left + pw + 4f) return;
            _ctx.DrawLine(new Vector2(sx, top), new Vector2(sx, top + ph),
                          Brush(dashColor), 1.6f, dashStyle);
            _ctx.DrawText(text, tiny, new Rect(sx + 4f, top + ph - 24f, 60f, 20f),
                          Brush(labelColor));
        }
        void Horizontal(float y, string text)
        {
            float sy = Y(y);
            if (sy < top - 4f || sy > top + ph + 4f) return;
            _ctx.DrawLine(new Vector2(left, sy), new Vector2(left + pw, sy),
                          Brush(dashColor), 1.6f, dashStyle);
            _ctx.DrawText(text, tiny, new Rect(left + 6f, sy - 22f, 60f, 20f),
                          Brush(labelColor));
        }
        void Focus(float x, float y, string text)
        {
            float sx = X(x), sy = Y(y);
            if (sx < left - 8f || sx > left + pw + 8f ||
                sy < top - 8f || sy > top + ph + 8f) return;
            _ctx.FillEllipse(new Ellipse(new Vector2(sx, sy), 5f, 5f), Brush(dotColor));
            _ctx.DrawText(text, tiny, new Rect(sx + 7f, sy - 24f, 40f, 20f),
                          Brush(labelColor));
        }

        if (type == "ellipse")
        {
            float cc = MathF.Sqrt(MathF.Max(0f, a * a - b * b));
            if (cc > 0.001f)
            {
                Focus(c.H - cc, c.K, "F1");
                Focus(c.H + cc, c.K, "F2");
                Vertical(c.H - a * a / cc, "准线");
                Vertical(c.H + a * a / cc, "准线");
            }
        }
        else if (type == "hyperbola")
        {
            float cc = MathF.Sqrt(a * a + b * b);
            bool vertical = (c.Axis ?? "x").ToLowerInvariant() is "y" or "vertical";
            if (vertical)
            {
                Focus(c.H, c.K - cc, "F1");
                Focus(c.H, c.K + cc, "F2");
                Horizontal(c.K - a * a / cc, "准线");
                Horizontal(c.K + a * a / cc, "准线");
                float slope = a / b;
                foreach (float sgn in new[] { -1f, 1f })
                {
                    float y1 = c.K + sgn * slope * (xMin - c.H);
                    float y2 = c.K + sgn * slope * (xMax - c.H);
                    _ctx.DrawLine(new Vector2(X(xMin), Y(y1)),
                                  new Vector2(X(xMax), Y(y2)),
                                  Brush(dashColor), 1.6f, dashStyle);
                }
            }
            else
            {
                Focus(c.H - cc, c.K, "F1");
                Focus(c.H + cc, c.K, "F2");
                Vertical(c.H - a * a / cc, "准线");
                Vertical(c.H + a * a / cc, "准线");
                float slope = b / a;
                foreach (float sgn in new[] { -1f, 1f })
                {
                    float y1 = c.K + sgn * slope * (xMin - c.H);
                    float y2 = c.K + sgn * slope * (xMax - c.H);
                    _ctx.DrawLine(new Vector2(X(xMin), Y(y1)),
                                  new Vector2(X(xMax), Y(y2)),
                                  Brush(dashColor), 1.6f, dashStyle);
                }
            }
        }
        else
        {
            float p = a;
            float half = p * 0.5f;
            switch ((c.Axis ?? "right").ToLowerInvariant())
            {
                case "left":
                    Focus(c.H - half, c.K, "F");
                    Vertical(c.H + half, "准线");
                    break;
                case "up":
                    Focus(c.H, c.K + half, "F");
                    Horizontal(c.K - half, "准线");
                    break;
                case "down":
                    Focus(c.H, c.K - half, "F");
                    Horizontal(c.K + half, "准线");
                    break;
                default:
                    Focus(c.H + half, c.K, "F");
                    Vertical(c.H - half, "准线");
                    break;
            }
        }
    }

    private void FigureText(string text, float cx, float cy, float w, float h,
                            IDWriteTextFormat fmt, Color4 color)
        => _ctx.DrawText(text, fmt, new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h),
                         Brush(color));

    private static string Fmt(float v)
    {
        if (MathF.Abs(v) < 1e-6f) return "0";
        return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static float NiceStep(float range, int target)
    {
        if (range <= 0 || !float.IsFinite(range)) return 1f;
        float raw = range / Math.Max(2, target);
        float mag = MathF.Pow(10f, MathF.Floor(MathF.Log10(raw)));
        float norm = raw / mag;
        float nice = norm <= 1.5f ? 1f : norm <= 3f ? 2f : norm <= 7f ? 5f : 10f;
        return nice * mag;
    }

    /// <summary>
    /// 画一张 AI 题卡。**逐行画**：排版（断行、行高、折叠）在 <see cref="TextBoxLayout"/> 里算好，
    /// 这里只负责把每一行贴到卡片局部坐标上——折叠/变宽时重排一次，不需要重新截图。
    /// </summary>
    private void DrawTextBox(Stroke s)
    {
        // 正在编辑的文本框由无窗口 RichEdit 宿主完整绘制（纸面/背景 + 文字）；
        // 这里必须跳过，否则透明背景时会出现“旧字/双份字”。
        if (_app != null && _app.TextBoxEditing && _app.IsTextBoxEditingStroke(s)) return;
        var card = s.TextBox;
        if (card == null) return;
        if (s.Points.Count == 0) return;
        var lines = card.EnsureLayout();
        float u = MathF.Max(0.5f, card.Unit);
        Vector2 o = new(s.Points[0].X, s.Points[0].Y);
        var rect = new Vortice.RawRectF(o.X, o.Y, o.X + card.Width, o.Y + card.LayoutHeight);
        Color4 fill = card.Fill;
        Color4 border = card.Border;
        float r = Math.Clamp(card.Radius, 0f, 40f) * u;
        var rr = new RoundedRectangle(rect, r, r);
        if (fill.A > 0.001f) _ctx.FillRoundedRectangle(rr, Brush(fill));
        if (border.A > 0.001f) _ctx.DrawRoundedRectangle(rr, Brush(border), 1f);

        // 卡片内容裁剪在里面：公式/长英文万一超宽也不会画到卡片外。
        _ctx.PushAxisAlignedClip(rect, AntialiasMode.Aliased);
        try
        {
            float pad = 18f * u;
            string family = TextBoxFaces.FamilyOf(card);
            // ??????????????????????????? ScrollY?
            float scrolledY = o.Y - card.ScrollY;
            foreach (var line in lines)
            {
                switch (line.Kind)
                {
                    case TextBoxLineKind.Divider:
                        _ctx.DrawLine(
                            new Vector2(o.X + line.X, scrolledY + line.Y),
                            new Vector2(o.X + line.Box.MaxX, scrolledY + line.Y),
                            Brush(new Color4(0.90f, 0.91f, 0.93f, 1f)), MathF.Max(1f, u));
                        break;

                    case TextBoxLineKind.Toggle:
                    {
                        var b = new Vortice.RawRectF(o.X + line.Box.MinX, scrolledY + line.Box.MinY,
                                                     o.X + line.Box.MaxX, scrolledY + line.Box.MaxY);
                        var pill = new RoundedRectangle(b, 8f * u, 8f * u);
                        _ctx.FillRoundedRectangle(pill, Brush(AiRedSoft));
                        _ctx.DrawRoundedRectangle(pill, Brush(new Color4(0.88f, 0.60f, 0.57f, 1f)), 1f);
                        _ctx.DrawText(line.Text, AiTextFormat(line.Size, false, true),
                            new Rect(b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top),
                            Brush(AiRed));
                        break;
                    }

                    case TextBoxLineKind.Text when line.Runs != null:
                    {
                        foreach (var run in line.Runs)
                        {
                            float rx = o.X + line.X + run.X;
                            float ry = scrolledY + line.Y + run.Y;
                            if (run.IsMath)
                            {
                                var bmp = run.MathImage.GetBitmap(_ctx);
                                if (bmp == null) continue;
                                _ctx.DrawBitmap(bmp, new Vortice.RawRectF(rx, ry, rx + run.Width, ry + run.Height),
                                                1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
                            }
                            else
                            {
                                Color4 ink = run.Tint
                                    ?? line.Tint
                                    ?? (card.Role == TextBoxRole.Normal ? s.Color : AiColor(line.Ink));
                                _ctx.DrawText(run.Text, AiTextFormat(run.Size, run.Bold, false, run.Italic, family),
                                    new Rect(rx, ry, run.Width + 2f, run.Height), Brush(ink));
                            }
                        }
                        break;
                    }

                    case TextBoxLineKind.Image:
                    {
                        var bmp = line.Image?.GetBitmap(_ctx);
                        if (bmp == null) break;
                        var dst = new Vortice.RawRectF(o.X + line.X, scrolledY + line.Y,
                                                       o.X + line.X + line.ImageW,
                                                       scrolledY + line.Y + line.ImageH);
                        _ctx.DrawBitmap(bmp, dst, 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
                        break;
                    }

                    case TextBoxLineKind.Math:
                    {
                        var bmp = line.MathImage?.GetBitmap(_ctx);
                        if (bmp == null) break;
                        var dst = new Vortice.RawRectF(o.X + line.X, scrolledY + line.Y,
                                                       o.X + line.X + line.MathW, scrolledY + line.Y + line.MathH);
                        _ctx.DrawBitmap(bmp, dst, 1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
                        break;
                    }

                    default:
                    {
                        float w = MathF.Max(10f, card.Width - line.X - pad);
                        Color4 ink = line.Tint
                            ?? (card.Role == TextBoxRole.Normal && line.Ink == TextBoxInk.Black
                                ? s.Color : AiColor(line.Ink));
                        _ctx.DrawText(line.Text, AiTextFormat(line.Size, line.Bold, false, false, family),
                            new Rect(o.X + line.X, scrolledY + line.Y, w, line.Height),
                            Brush(ink));
                        break;
                    }
                }
            }

            // 内容溢出时右侧一根细滚动条：告诉老师"下面还有"，滚轮可滚。
            if (card.MaxScroll > 0.5f)
            {
                float trackW = 3f * u;
                float gap = 14f * u;
                float tx = o.X + card.Width - gap - trackW;
                float top = o.Y + pad;
                float trackH = Math.Max(8f, card.LayoutHeight - pad * 2f);
                float thumbH = MathF.Max(20f * u, trackH * (card.Height / MathF.Max(1f, card.ContentHeight)));
                thumbH = MathF.Min(thumbH, trackH);
                float thumbY = top + (trackH - thumbH) * (card.ScrollY / card.MaxScroll);
                Color4 trackColor = card.Role == TextBoxRole.Normal
                    ? new Color4(s.Color.R, s.Color.G, s.Color.B, 0.10f)
                    : new Color4(AiMuted.R, AiMuted.G, AiMuted.B, 0.18f);
                Color4 barColor = card.Role == TextBoxRole.Normal
                    ? new Color4(s.Color.R, s.Color.G, s.Color.B, 0.42f)
                    : new Color4(AiMuted.R, AiMuted.G, AiMuted.B, 0.55f);
                var track = new RoundedRectangle(
                    new Vortice.RawRectF(tx, top, tx + trackW, top + trackH),
                    trackW * 0.5f, trackW * 0.5f);
                _ctx.FillRoundedRectangle(track, Brush(trackColor));
                var thumb = new RoundedRectangle(
                    new Vortice.RawRectF(tx, thumbY, tx + trackW, thumbY + thumbH),
                    trackW * 0.5f, trackW * 0.5f);
                _ctx.FillRoundedRectangle(thumb, Brush(barColor));
            }

            // 卡片内容里的操作提示（只在选择工具 + 已选中时才响应命中）。
            // 画在内容层，所以**不能随选中状态变样式**：否则选区一变就要重烘分块。
            if (card.Role == TextBoxRole.AiQuestion)
            {
                var close = new Rect(o.X + card.Width - 38f * u, o.Y + 6f * u, 30f * u, 30f * u);
                _ctx.DrawText("✕", AiTextFormat(13f * u, false, true), close, Brush(AiMuted));
            }
        }
        finally
        {
            _ctx.PopAxisAlignedClip();
        }
    }

    private static Color4 AiColor(TextBoxInk ink) => ink switch
    {
        TextBoxInk.Red => AiRed,
        TextBoxInk.Muted => AiMuted,
        TextBoxInk.Accent => AiRed,
        _ => AiBlack,
    };

    private IDWriteTextFormat AiTextFormat(float size, bool bold, bool center, bool italic = false,
                                           string family = null)
    {
        int px = Math.Max(8, (int)MathF.Round(size));
        family = string.IsNullOrWhiteSpace(family) ? Loc.FontFamily : family;
        var key = (px, bold, center, italic, family);
        if (_aiFormats.TryGetValue(key, out var f)) return f;
        f = Gfx.WriteFactory.CreateTextFormat(family, null,
            bold ? FontWeight.SemiBold : FontWeight.Normal,
            italic ? FontStyle.Italic : FontStyle.Normal,
            FontStretch.Normal, px, Loc.LocaleName);
        f.WordWrapping = WordWrapping.NoWrap;
        if (center)
        {
            f.TextAlignment = TextAlignment.Center;
            f.ParagraphAlignment = ParagraphAlignment.Center;
        }
        _aiFormats[key] = f;
        return f;
    }

    /// <summary>
    /// **圆**的圆心点（**墨迹**，不是界面装饰；2026-10-18 用户定）。
    ///
    /// 规则：
    ///   · **只有圆**要，椭圆不要；
    ///   · 选中/未选中、移动/缩放后、导出/存档/复制都带着；
    ///   · 点**大小固定**：半径约 1.25 逻辑像素，不随圆半径/选中框缩放变粗变大；
    ///     圆心位置跟着图形变换走。
    ///
    /// 为什么放在 DrawStrokeCore 而不是浮动层：它就是图形墨迹的一部分，必须进
    /// 内容分块和导出；选中切换不需要额外重烘。
    /// </summary>
    private void DrawShapeCenterInk(Stroke s, Matrix3x2? extra)
    {
        if (s.Kind != StrokeKind.Circle) return;

        float unit = Dpi / 96f;
        var m = s.Transform * (extra ?? Matrix3x2.Identity);
        var c = Vector2.Transform(s.ShapeCenterLocal, m);

        // 固定大小：不按半径收、不按变换缩放。
        float r = 1.25f * unit;
        _ctx.FillEllipse(new Ellipse(c, r, r), Brush(s.Color));
    }

    /// <summary>
    /// **有压感的自由笔迹**走 D2D 的原生墨迹（`ID2D1Ink`：每个点自带半径 → 变宽）。
    ///
    /// 为什么不自己拼变宽轮廓：官方对这套图元写得很直白——"比过去应用自己用一串椭圆和
    /// 四边形去管墨迹**更快也更漂亮**"，而我们 2026-09-14 删掉的那一层正是那种自拼轮廓
    /// （折角被削、自交挖洞、起笔毛边都是它带来的）。用它等于把"变宽"这件事交回给 D2D。
    ///
    /// **四个前提，缺一个就返回 false 走回等宽描边**（老那条路一个字没改）：
    ///   · 这台机器拿得到 `ID2D1DeviceContext2`（见 Gfx.InkAvailable）；
    ///   · 这一笔**真的有压感**（设备报的，不是我们猜的，见 Stroke.HasPressure）；
    ///   · **实线**——光栅化墨迹不吃 dash 图案，虚线/点线笔迹必须走老路；
    ///   · **没被像素橡皮擦断**（擦除区间是"按段不画"，而 ink 是整条一次性铺出来的）。
    ///
    /// 两个实现细节：
    ///   · **段数封顶 120**：压力是慢变量，长笔按步长抽稀即可（位置精度由 D2D 在段内插值补）。
    ///     抽稀也让"每条笔每次重画"的代价封住——我们不缓存 ink 对象（它只能从设备上下文建，
    ///     缓存就得按窗口分开管生命周期，不划算）；
    ///   · **压力先做一次指数平滑**（只在这一层做，存档里留的是原始值）：
    ///     10 bit 的原始压力在轻压段抖动明显，直接喂给宽度会"笔墨发毛"。
    /// </summary>
    private bool DrawPressureInk(Stroke s)
    {
        if (_ctx2 == null || _inkStyle == null) return false;
        if (!InkAvailable || !PressureWidth.Enabled) return false;
        if (!s.HasPressure || s.Dash != StrokeDash.Solid) return false;
        if (s.Erased.Count > 0 || s.Kind != StrokeKind.Freehand) return false;

        // 段由两条路生成（旧的"按步长抽稀" / `--smooth` 的"曲线化"），
        // 生成完的 `_inkSegs` ＋ `nSeg` 在这里交给同一条 D2D 墨迹通道画出去。
        if (!BuildPressureSegments(s, out float startRadius, out int nSeg)) return false;

        try
        {
            var ink = _ctx2.CreateInk(new Vortice.Direct2D1.InkPoint
            {
                X = s.Points[0].X, Y = s.Points[0].Y, Radius = startRadius,
            });
            try
            {
                ink.AddSegments(_inkSegs, (uint)nSeg);
                PressureInkDraws++;
                _ctx2.DrawInk(ink, Brush(s.Color), _inkStyle);
            }
            finally { ink.Dispose(); }
            return true;
        }
        catch (Exception ex)
        {
            // **出问题就整体退回等宽描边，绝不静默画成空白**——而且只报一次、
            // 之后不再走这条路（否则每一帧都要抛一次，日志会被刷屏）。
            InkAvailable = false;
            InkNote = "运行中失败（" + ex.Message + "）→ 压感笔迹退回等宽描边";
            Console.WriteLine("墨迹通道（ID2D1Ink）失败：" + ex.Message + " → 压感笔迹退回等宽描边");
            return false;
        }
    }

    /// <summary>
    /// 生成压感墨迹的段（含渲染尾），写进复用的 <c>_inkSegs</c>。两条路：
    ///   · **曲线化开着**（`--smooth`）→ <see cref="StrokeSmoothing"/> 的过点贝塞尔；
    ///     段数按点数走，**不再按 120 抽稀**（抽稀会把刚算出来的弯又拉直）；
    ///   · **关着**（默认）→ 原来的"按步长抽稀到 ≤120 段 ＋ 控制点落在直线上"，行为一字不改。
    ///
    /// <paramref name="maxParam"/> 是**回放前缀**用的点序号上限（可带小数；整笔传 MaxValue）：
    /// 只喂到那一处，必要时在末尾补一个插值点——这样回放里"正在长"的压感笔迹
    /// 仍然走 D2D 原生变宽，而不是先等宽、写完突然变粗。前缀**不接渲染尾**。
    ///
    /// 返回 false = 没有段，调用方直接不画。
    /// </summary>
    private bool BuildPressureSegments(Stroke s, out float startRadius, out int count,
                                       float maxParam = float.MaxValue)
    {
        var pts = s.Points;
        int n = pts.Count;
        count = 0;
        startRadius = 0f;
        if (n < 2) return false;

        bool clipped = maxParam < n - 1 - 1e-4f;
        // 运动模型（实验，`--motion`）：整条用选中的模型输出 + 压力（M2 已按时间加权插值压力）。
        // 只对"整笔、没被橡皮擦过"生效；回放前缀/擦除过的一律照旧走原始点。
        bool useModel = !clipped && s.Erased.Count == 0 && StrokeMotion.Build(s);
        if (useModel) n = StrokeMotion.Count;
        int lastIdx = clipped ? Math.Clamp((int)MathF.Floor(maxParam), 0, n - 1) : n - 1;
        float frac = clipped ? maxParam - lastIdx : 0f;
        bool tailPoint = frac > 1e-4f;
        if (clipped && lastIdx < 1 && !tailPoint) return false;   // 还没长到第二个点

        // 源点访问器：原始采样点 / 建模输出（x, y, 压力）二选一。
        float Px(int i) => useModel ? StrokeMotion.At(i).X : pts[i].X;
        float Py(int i) => useModel ? StrokeMotion.At(i).Y : pts[i].Y;
        float Pp(int i) => useModel ? StrokeMotion.At(i).Z : pts[i].P;

        startRadius = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, Pp(0)));
        float lastX = Px(0), lastY = Py(0), lastR = startRadius;

        // `--nosmooth`（StrokeSmoothing.Enabled=false）时整体退回折线：
        // 模型量出来的点再密，也不上曲线。这样"开/关"才是一个有效对照。
        bool curveBranch = StrokeSmoothing.Enabled && (useModel || (!useModel && !s.RawWhileLive));
        Stroke.GeomTrace(s, "B", useModel ? "模型" : "原始", curveBranch,
                         $"clipped={clipped} erased={s.Erased.Count} model={useModel} live={s.RawWhileLive} enabled={StrokeSmoothing.Enabled}");
        if (curveBranch)
        {
            // 把源点（原始采样点 / 建模输出）喂进过点曲线：建模输出本来已经去过抖，
            // 再过一次曲线只是为了消掉"输出点之间的折线"（mean2 的快写折线感）。
            StrokeSmoothing.Begin();
            for (int i = 0; i <= lastIdx; i++) StrokeSmoothing.Add(Px(i), Py(i), Pp(i));
            if (tailPoint)
            {
                int j = lastIdx + 1;
                StrokeSmoothing.Add(Px(lastIdx) + (Px(j) - Px(lastIdx)) * frac,
                                    Py(lastIdx) + (Py(j) - Py(lastIdx)) * frac,
                                    Pp(lastIdx) + (Pp(j) - Pp(lastIdx)) * frac);
            }
            int segs = StrokeSmoothing.Finish();
            if (segs <= 0) return false;
            EnsureInkSegs(segs);

            var cs = StrokeSmoothing.Out;
            float ema = StrokeSmoothing.PressureAt(0);
            for (int k = 0; k < segs; k++)
            {
                float pk2 = StrokeSmoothing.PressureAt(k + 1);
                ema += (pk2 - ema) * (pk2 > ema ? InkPressureEma : InkPressureEmaDown);
                float r1 = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, ema));
                _inkSegs[count++] = new InkBezierSegment
                {
                    Point1 = new Vortice.Direct2D1.InkPoint
                    {
                        X = cs[k].C1.X, Y = cs[k].C1.Y, Radius = lastR + (r1 - lastR) / 3f,
                    },
                    Point2 = new Vortice.Direct2D1.InkPoint
                    {
                        X = cs[k].C2.X, Y = cs[k].C2.Y, Radius = lastR + (r1 - lastR) * 2f / 3f,
                    },
                    Point3 = new Vortice.Direct2D1.InkPoint { X = cs[k].P1.X, Y = cs[k].P1.Y, Radius = r1 },
                };
                lastR = r1;
                lastX = cs[k].P1.X;
                lastY = cs[k].P1.Y;
            }
        }
        else
        {
            // 采样步长：保证段数 ≤ 上限，且**最后一点一定画到**。
            // 建模输出的点已经很密（≥180Hz），上限给大得多——按 120 抽会把刚平滑出来的弯拉直。
            int maxSegs = useModel ? InkMaxSegmentsModeled : InkMaxSegments;
            int stride = Math.Max(1, (int)MathF.Ceiling((n - 1) / (float)maxSegs));
            if (useModel) EnsureInkSegs(Math.Min(n, maxSegs));
            int cap = Math.Min(_inkSegs.Length, maxSegs);
            float sm = Pp(0);
            float sx = Px(0), sy = Py(0), sr = startRadius;
            for (int i = 1; i <= lastIdx && count < cap; i++)
            {
                float pi2 = Pp(i);
                sm += (pi2 - sm) * (pi2 > sm ? InkPressureEma : InkPressureEmaDown);   // 平滑只作用于压力，不动位置
                if (i % stride != 0 && i != lastIdx) continue; // 中间的按步长抽稀（末点必留）

                float ex = Px(i), ey = Py(i);
                float er = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, sm));
                // 直线段写成三次贝塞尔：控制点落在两端之间 → 位置是直线，半径沿途线性插值。
                //
                // 注意**必须全限定** `Vortice.Direct2D1.InkPoint`：我们自己也有一个
                // `InkEngine.InkPoint`（笔迹的采样点 X/Y/P/T），写裸名会被它遮住，
                // 报错是"未包含 Radius 的定义"——一个不容易一眼看懂的编译错误。
                _inkSegs[count++] = new InkBezierSegment
                {
                    Point1 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) / 3f, Y = sy + (ey - sy) / 3f,
                        Radius = sr + (er - sr) / 3f,
                    },
                    Point2 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) * 2f / 3f, Y = sy + (ey - sy) * 2f / 3f,
                        Radius = sr + (er - sr) * 2f / 3f,
                    },
                    Point3 = new Vortice.Direct2D1.InkPoint { X = ex, Y = ey, Radius = er },
                };
                sx = ex; sy = ey; sr = er;
                lastX = sx; lastY = sy; lastR = sr;
            }
            // 前缀落在两个采样点之间：补一个插值终点（位置/压力都线性插），
            // 不然回放的笔尖会"一格一格跳"。
            if (tailPoint && count < cap)
            {
                int j = lastIdx + 1;
                float ex = Px(lastIdx) + (Px(j) - Px(lastIdx)) * frac;
                float ey = Py(lastIdx) + (Py(j) - Py(lastIdx)) * frac;
                float piTail = Pp(lastIdx) + (Pp(j) - Pp(lastIdx)) * frac;
                sm += (piTail - sm) * (piTail > sm ? InkPressureEma : InkPressureEmaDown);
                float er = MathF.Max(InkMinRadius, PressureWidth.HalfWidth(s.Width, sm));
                _inkSegs[count++] = new InkBezierSegment
                {
                    Point1 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) / 3f, Y = sy + (ey - sy) / 3f,
                        Radius = sr + (er - sr) / 3f,
                    },
                    Point2 = new Vortice.Direct2D1.InkPoint
                    {
                        X = sx + (ex - sx) * 2f / 3f, Y = sy + (ey - sy) * 2f / 3f,
                        Radius = sr + (er - sr) * 2f / 3f,
                    },
                    Point3 = new Vortice.Direct2D1.InkPoint { X = ex, Y = ey, Radius = er },
                };
                lastX = ex; lastY = ey; lastR = er;
            }
        }

        if (count == 0) return false;

        // [删除 2026-10-05] 渲染尾（预测段）拼接：随老预测系统移除。
        return true;
    }

    /// <summary>段缓冲按需增长（曲线化时一条笔的段数可能远超 120）。</summary>
    private void EnsureInkSegs(int need)
    {
        if (_inkSegs.Length >= need) return;
        Array.Resize(ref _inkSegs, Math.Max(need, _inkSegs.Length * 2));
    }

    /// <summary>
    /// 抓屏专用的一帧：整窗清成全透明，不画内容/界面/光标。
    /// 窗口不藏，DWM 仍然合成桌面，所以 BitBlt 能直接看到下层；比 HiddenOverlay 少一次闪屏。
    /// </summary>
    private void RenderTransparentForCapture()
    {
        _frameDirty.Clear();
        _frameDirty.Add(FullWindowRect);

        _ctx.Target = _backBuffer;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        _ctx.FillRectangle(new Vortice.RawRectF(0f, 0f, Width, Height),
                           Brush(new Color4(0f, 0f, 0f, 0f)));
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
        var hr = _ctx.EndDraw();
        _ctx.Target = null;
        if (hr.Failure) LastError = "transparent capture EndDraw: " + hr.Description;

        _uiBoundsHistory[0] = RectF.Empty;
        _uiBoundsHistory[1] = RectF.Empty;
        _libraryRectPrev = RectF.Empty;
    }

}
