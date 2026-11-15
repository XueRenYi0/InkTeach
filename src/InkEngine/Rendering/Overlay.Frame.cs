// [职责] 覆盖层帧渲染：内容/界面/装饰合成。
// [测试] --tiletest

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
    //  Frame
    // ------------------------------------------------------------------

    public void RenderFrame(InkEngine app)
    {
        _app = app;
        s_frameNo++;

        // 抓屏专用：整窗清成透明（不画内容/界面/光标），让 BitBlt 直接看到桌面。
        // 比“把窗口藏起来再显示”少一次闪屏；见 Engine.CaptureTransparentFrame。
        if (app.CaptureTransparentFrame)
        {
            RenderTransparentForCapture();
            return;
        }

        // 回放：推进"现在演到哪"必须**排在 SyncTiles 之前**。
        //
        // 为什么（用户 2026-10-01 报"每写完一笔还是会闪一下"的根因）：一条笔迹
        // 出完那一帧要做两件事——把前缀撤掉、把这条墨补进内容层分块。两件事必须
        // **同一帧**完成：TickReplay 排在 SyncTiles 后面时，这一帧分块里还没有这条墨，
        // 而前缀已经不再画——屏幕上整整缺一帧（就是那个"闪一下"）。
        app.TickReplay();

        // 双曲线三步式的第一步：**只画渐近线、先不画曲线**（见 _auxOnlyStroke 那段说明）。
        // 每帧在这里设一次——**只有真机渲染这条路会设**，导出/出图那条路不设，
        // 免得半成品在导出时被误当成"该隐藏主体"的对象。
        _auxOnlyStroke = app.HyperAsymptotePreviewOnly ? app.ActiveStroke : null;

        // 界面：先布局、该重画就重画一次，拿到这一帧的矩形。
        bool uiVisible = PrepareUi(app);

        // The content layer must be rebuilt *before* the swap-chain target is
        // bound: switching targets in the middle of BeginDraw/EndDraw puts
        // Direct2D into an error state.
        if (!app.NoContentCache)
            SyncTiles(app);
        else
            SyncDocPages(app);      // 调试路径不碰分块，但文档页层照样要生成/回收/报脏

        // 性能面板先画进自己的缓存位图（必须在绑后缓冲、BeginDraw 之前做）。
        var swHud = Stopwatch.StartNew();
        PrepareHud(app);
        LastHudMs = swHud.Elapsed.TotalMilliseconds;
        LastHudRedrawMs = _hudRedrewThisFrame ? LastHudMs : 0;

        // 动态橡皮：指针停住之后把尺寸**缓释**回去（每帧推一次；续帧由引擎那边的 `_dirty` 保证）。
        app.TickEraserIdleDecay();

        _transientNow = ComputeTransientBounds(app);
        UpdateFrameDirty(app, uiVisible);

        var sw = Stopwatch.StartNew();
        _ctx.Target = _backBuffer;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;

        // 逐块重绘。以前是"全屏清屏 + 全屏贴图 + 全屏上屏"，一帧要动 5.2M 像素；
        // 实测这才是在核显上占 8~9% GPU 的真正原因（不是上屏，Present1 之后
        // 脏区只有 0.1~0.8%，GPU 却一点没降）。现在整条管线都只动脏区。
        foreach (var raw in _frameDirty)
        {
            var c = ClipToWindow(raw);
            if (c.IsEmpty) continue;

            var box = new Vortice.RawRectF(c.MinX, c.MinY, c.MaxX, c.MaxY);
            _ctx.PushAxisAlignedClip(box, AntialiasMode.Aliased);

            // Clear() 不受裁剪影响，所以用 Copy 混合的填充来"擦"这一块。
            // （半透明白板盖文档时垫"文档的纸"，页图随后画、板色再盖——见 RasterizeTile 的同款注释。）
            bool boardOverDocTranslucentDbg =
                app.BoardOn && app.DocView.IsOpen && app.BoardOpacity < 0.999f;
            _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
            _ctx.FillRectangle(box, boardOverDocTranslucentDbg
                ? Brush(app.DocPaperColor) : BoardBrush(app));
            _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

            if (app.NoContentCache)
            {
                // Naive path: re-rasterise every committed stroke, every frame.
                _ctx.Transform = CanvasToWindow;
                // 注意：这里要比的是**画布矩形**。以前直接拿窗口矩形去比，
                // 相机为 0 时看不出问题，一滚动就全错（这也是要收进"类型化
                // 坐标"的那一类坑）。
                var cv = new RectF
                {
                    MinX = WindowToCanvasX(c.MinX),
                    MinY = WindowToCanvasY(c.MinY),
                    MaxX = WindowToCanvasX(c.MaxX),
                    MaxY = WindowToCanvasY(c.MaxY),
                };
                DrawDocPages(app, cv);     // 调试路径：文档页层也要画（否则白屏看不到文档）
                if (boardOverDocTranslucentDbg)
                    _ctx.FillRectangle(box, BoardBrush(app));   // 半透明板色盖在页上
                foreach (var s in app.Doc.Strokes)
                {
                    if (!s.PaddedBounds.Intersects(cv)) continue;
                    if (app.IsContentDetached(s)) continue;   // 拖动预览那一批由浮动层画
                    DrawStroke(s);
                }
            }
            else
            {
                // 内容层：把可见分块按相机贴在窗口上（子矩形拷贝，不重画笔迹）
                CompositeTiles(c);
            }

            _ctx.Transform = CanvasToWindow;

            // 调试用：--trailonly 时不画自己那一笔，用来验证委托墨迹轨迹
            // 是不是真的由系统合成器画出来了。
            // `--syswet`：这一笔的湿墨由系统轨迹画（且它真的在画）→ 我们不自绘，
            // 否则两条叠在一起，系统轨迹的低延迟优势根本看不见。
            if (app.ActiveStroke != null && !app.SuppressActiveStroke
                && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
            {
                // 正在写的那一笔几何每帧都在变，用实现缓存只会不停重建，反而更慢
                DrawStroke(app.ActiveStroke);
                // 这一笔**我们自己画上屏了** → Present 之后把轨迹里同样这一段删掉
                //（不删的后果见 TrimInkTrailAsDrawn 的注释；2026-10-11 真笔实测教训）。
                _drewWetStrokeThisFrame = true;
            }
            if (app.PredictTailActive && !app.ActiveStroke.HiddenForPairing
                && !(TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)))
                DrawPredictTail(app.ActiveStroke.Color, app.PredictTailWidth);

            // 回放：正在"长"的那一条（前缀几何；已经出完的都在内容层里了）
            if (app.ReplayActive) DrawReplayCurrent(app);

            DrawDragPreview(app);
            DrawVertexPreview(app);
            DrawSelection(app);
            DrawSelectionCenterCross(app);
            DrawAlignSnapPulse(app);
            // 图库面板：它从工具条上沿往上长，压在内容之上（不压界面——界面比它更靠下）
            DrawLibraryGhost(app);
            DrawLibraryPanel(app);
            // 画线中的 α 读数画在浮动层最上面（它贴着正在拖的那一端，压住什么都不碍事）。
            DrawShapeInclination(app);
            DrawLaser(app);
            DrawRadialPalette(app);   // 呼出盘压着墨和激光；落点反馈（下面那一句）在它上面
            DrawTouchEraseFeedback(app);   // 多指擦会话的落点（单指橡皮走 DrawToolCursor 那条）
            DrawToolCursor(app);
            DrawMarquee(app);

            _ctx.Transform = Matrix3x2.Identity;

        if (app.ShowHud)
        {
            var swBlit = Stopwatch.StartNew();
            DrawHud();
        DrawTouchHud(app);       // 触点诊断（8.3.3，默认关）
            LastHudMs += swBlit.Elapsed.TotalMilliseconds;
        }

        if (app.EraserHud) DrawEraserHud(app);   // 橡皮读数（8.3.7，默认关；调参用）

        // 滚动条（样式 B：一根细线）。画在浮动层，不进内容层。
        DrawScrollBar(app);

        // 底部那两条 PPT 控件（放映时才画）。**必须画在 `Matrix3x2.Identity` 之下**：
        // 它们的矩形是**屏幕坐标**（贴屏幕底边、只跟屏幕走），画在上面的画布变换里
        // 会被相机整体平移——相机一滚，条就跟着跑偏（和 HUD、滚动条同一批，同一理由）。
        DrawPptBar(app);

        // 回放控制条：和 PPT 条同一层（屏幕坐标、`Identity` 变换之下），贴屏幕底边居中。
        DrawReplayBar(app);

        // 课堂计时卡片：同一层（屏幕坐标），默认顶部居中；双击放大后是屏幕中央的大字。
        DrawTimerCard(app);

        // 课堂点名卡片：同一层，默认屏幕中央偏上。
        DrawRollCard(app);

        // 引擎侧悬停提示（选中操作条 / PPT 条与长按菜单）：画在所有浮层之上、
        // 截图整层之下——截图时 TooltipShown 本来就是假的（进入截图会清掉）。
        DrawEngineTooltip(app);

        // 轻提示（2 秒）：画在所有浮层之上、截图整层之下。
        // 只画不接命中——提示期间笔/鼠标/触摸照常落到画布和按钮上。
        DrawToast(app);

        // 截图整层（8.3.0）：遮罩/冻结帧/取景框/准线/读数/调整手柄。
        // 放在最后 = 盖住上面所有东西（滚动条、PPT 条、HUD）；界面那一块在截图期间
        // 由引擎收起（见 PrepareUi），所以这就是屏幕上看到的全部。
        // 截图结束那一帧顺手把冻结帧位图放掉（它位图里存着整屏像素，不留着占显存/内存）。
        if (app.ColorPickActive) DrawColorPickOverlay(app, CanvasToWindow);
        else if (app.CaptureActive) DrawCaptureOverlay(app, CanvasToWindow);
        else ReleaseFrozenBitmap();

        _ctx.PopAxisAlignedClip();
        }

        // 界面画在所有笔迹之上。它每帧只贴一张缓存位图，不用重画内容。
        if (uiVisible)
            DrawUi(app);

        // 反馈群二维码：最上面一层（连界面一起盖住），点一下 / Esc 关。
        if (app.FeedbackQrVisible)
            DrawFeedbackQr(app);

        var hr = _ctx.EndDraw();
        _ctx.Target = null;
        sw.Stop();

        LastRecordMs = sw.Elapsed.TotalMilliseconds;
        if (hr.Failure) LastError = "frame EndDraw: " + hr.Description;

        // 记住这一帧界面的位置，下一帧靠它把"刚刚消失"的界面擦干净。
        _uiBoundsPrev = uiVisible ? _uiBounds : RectF.Empty;
        // 图库面板同理：下一帧用它把刚刚关掉的那张卡片从屏幕上擦掉。
        // 抽屉可能比窄面板宽，也要一起并进来。
        _libraryRectPrev = RectF.Empty;
        if (app.LibraryPanelVisibleNow && !app.LibraryPlacementMode)
        {
            // 脏区必须带**投影最远半径**：只放 4px 时，面板往左拖，右侧旧投影擦不掉，
            // 表现为“右侧一条虚影”；往右拖因为新面板盖住旧投影才看不出来。
            float pad = FloatingDirtyPad(app, app.UiUnit);
            var pr = CanvasRectToWindow(app.LibraryPanelRectNow()).Inflate(pad);
            if (app.LibraryCategoriesOpen)
            {
                var dr = CanvasRectToWindow(app.LibraryDrawerRectNow()).Inflate(pad);
                pr = new RectF
                {
                    MinX = MathF.Min(pr.MinX, dr.MinX), MinY = MathF.Min(pr.MinY, dr.MinY),
                    MaxX = MathF.Max(pr.MaxX, dr.MaxX), MaxY = MathF.Max(pr.MaxY, dr.MaxY),
                };
            }
            if (app.LibraryMenu != 0)
            {
                var mr = CanvasRectToWindow(app.LibraryMenuRect()).Inflate(pad);
                pr = new RectF
                {
                    MinX = MathF.Min(pr.MinX, mr.MinX), MinY = MathF.Min(pr.MinY, mr.MinY),
                    MaxX = MathF.Max(pr.MaxX, mr.MaxX), MaxY = MathF.Max(pr.MaxY, mr.MaxY),
                };
            }
            _libraryRectPrev = pr;
        }
    }

    /// <summary>请求整屏重画两帧（二维码这种覆盖层出现 / 消失时用）。</summary>
    internal void RequestFullFrames() => _fullFramesLeft = 2;

    /// <summary>
    /// 反馈群二维码（2026-11-03）：**整屏遮罩 + 居中卡片图**。
    ///
    /// 为什么用图而不是画格子：QQ 的群卡是官方生成的，图里自带头像、群号、
    /// "扫一扫二维码，加入群聊"和 QQ 标志——自己重画既不准也白费。
    /// 点任意处 / Esc 关闭（引擎那边处理），右上角再画一个 ✕ 让人看得出来能关。
    /// </summary>
    private void DrawFeedbackQr(InkEngine app)
    {
        var img = app.FeedbackQrImage;
        if (img == null) return;

        float w = Width, h = Height;
        float u = app.UiUnit;
        float capH = 46f * u;
        float maxW = w * 0.70f, maxH = h * 0.86f - capH;
        float k = MathF.Min(maxW / img.Width, maxH / img.Height);
        float dw = img.Width * k, dh = img.Height * k;
        float x0 = (w - dw) * 0.5f, y0 = (h - (dh + capH)) * 0.5f;

        _ctx.Transform = Matrix3x2.Identity;
        _ctx.FillRectangle(new Vortice.RawRectF(0, 0, w, h),
                           Brush(new Color4(0f, 0f, 0f, 0.55f)));

        // 白底卡片：图本身是白底，垫一层圆角白卡把边缘收干净
        float pad = 10f * u;
        var card = new RectF
        {
            MinX = x0 - pad, MinY = y0 - pad,
            MaxX = x0 + dw + pad, MaxY = y0 + dh + capH + pad,
        };
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(card.MinX, card.MinY, card.MaxX, card.MaxY), 18f * u, 18f * u),
            Brush(new Color4(1f, 1f, 1f, 1f)));

        var bmp = img.GetBitmap(_ctx);
        if (bmp != null)
            _ctx.DrawBitmap(bmp, new Vortice.RawRectF(x0, y0, x0 + dw, y0 + dh),
                            1f, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);

        _ctx.DrawText(Loc.T("扫一扫，加 QQ 群 {0}", ProductInfo.FeedbackQQGroup),
                      AiTextFormat(18f * u, bold: false, center: true),
                      new Vortice.Mathematics.Rect(card.MinX + 8f * u, y0 + dh + 4f * u,
                                                   card.MaxX - card.MinX - 16f * u, capH - 4f * u),
                      Brush(new Color4(0.18f, 0.20f, 0.24f, 1f)));

        // 右上角 ✕：点任意处都能关，但这个标记让人知道"能关"
        float cs = 34f * u;
        float cx = card.MaxX - 8f * u - cs * 0.5f, cy = card.MinY + 8f * u + cs * 0.5f;
        _ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), cs * 0.5f, cs * 0.5f),
                         Brush(new Color4(0f, 0f, 0f, 0.55f)));
        float icon = cs - 16f * u;
        DrawIcon(IconPaths.collapse, cx - icon * 0.5f, cy - icon * 0.5f,
                 icon, Brush(new Color4(1f, 1f, 1f, 0.95f)));
    }

    /// <summary>
    /// 每帧都会重绘的元素的包围盒：正在书写的那一笔、激光轨迹、橡皮光标、
    /// 框选矩形、性能面板。它们不进内容层，所以每帧都要重新画，
    /// 也就必须每帧都算进脏区。
    /// </summary>
    private RectF ComputeTransientBounds(InkEngine app)
    {
        var r = RectF.Empty;

        // 自绘的落点反馈（橡皮方框 / 圆环 / 圆盘）：每帧都重画，而且**一帧可能移动超过半径**
        //（快擦；面积擦的框还会随速度变大变小）。所以它必须走这条"最近三帧"的集合，
        // 不能只算当帧或"当帧＋上一帧"——双缓冲里躺着的是**两帧前**的画面（见下面
        // `_transientHistory` 那条注释）。用户 2026-09-30 报的"面积橡皮左侧出现一条条
        // 细密的竖线、颜色像变深了"就是这个：旧框只有一帧的覆盖，第三帧那一条擦不掉。
        if (app.DrawnCursor != InkEngine.ToolCursorShape.None)
        {
            float rad = app.DrawnCursorRadius;
            var c = RectF.Empty;
            c.Add(app.PointerX - rad, app.PointerY - rad);
            c.Add(app.PointerX + rad, app.PointerY + rad);
            r.Add(CanvasRectToWindow(c));
        }

        if (app.ActiveStroke != null)
        {
            r.Add(CanvasRectToWindow(app.ActiveStroke.PaddedBounds));
            // B4（2026-10-08）：预测尾又回来了——它**伸在笔尖前面**，必须算进脏区，
            // 否则被裁剪（那正是"尾巴在画、屏幕上看不见"的根因）。
            if (app.PredictTailActive)
            {
                var tb = RectF.Empty;
                foreach (var p in app.PredictTailPoints) tb.Add(p.X, p.Y);
                if (!tb.IsEmpty)
                    r.Add(CanvasRectToWindow(tb.Inflate(MathF.Max(2f, app.PredictTailWidth))));
            }
        }

        // 呼出盘（Ctrl+Q）：固定画在盘心，但轨迹线跟着指针、内容随扇区变——
        // 脏区按"盘 ＋ 投影 ＋ 盘下那行字 ∪ 当前指针"给（盘一转、线一动，旧像素才擦得掉）。
        if (app.RadialPaletteActive)
        {
            float rad = (96f + app.FloatingTheme.ShadowReachLogical + 4f) * app.DpiScale;
            var box = RectF.Empty;
            box.Add(app.RadialCenterX - rad, app.RadialCenterY - rad);
            box.Add(app.RadialCenterX + rad, app.RadialCenterY + rad);
            box.Add(app.RadialCenterX, app.RadialCenterY + rad + 56f * app.DpiScale);  // 悬停字条
            box.Add(app.PointerX, app.PointerY);
            r.Add(CanvasRectToWindow(box));
        }

        var laser = app.Laser;
        if (laser.Visible)
        {
            // 轨迹只有红边 + 白芯，最外半径就是 0.5 × 主带直径；再加 12px
            // 兜住抗锯齿、圆帽和转向处的斜接。以前那圈 1.6d 的柔光已经撤掉，
            // 不能再按旧系数留 —— 那样会把脏区白放大两三倍，长轨迹时反而拖慢。
            float d = MathF.Max(6f, laser.MaxWidthLogical * app.DpiScale);
            float pad = d * 0.5f + 12f;
            var b = RectF.Empty;
            foreach (var s in laser.Strokes)
                foreach (var p in s.Points) b.Add(p.X, p.Y);
            if (!b.IsEmpty) r.Add(CanvasRectToWindow(b.Inflate(pad)));
        }

        // 自绘的落点反馈（橡皮圆环 / 笔尖环 / 荧光笔圆盘）。它比图形本身大一圈
        // （描边 + 刻度），脏区要跟着放大，否则快速划过会留下残影。
        //
        // 画布坐标 → 窗口坐标必须过 CanvasRectToWindow：圆环跟着笔迹走，
        // 滚动之后两者才会一致（漏了这一步，滚下去之后圆环就擦不干净）。
        if (app.DrawnCursor != InkEngine.ToolCursorShape.None)
        {
            float rad = app.DrawnCursorRadius;
            var c = RectF.Empty;
            c.Add(app.PointerX - rad, app.PointerY - rad);
            c.Add(app.PointerX + rad, app.PointerY + rad);
            var cur = CanvasRectToWindow(c);
            r.Add(cur);
            // **上一帧那一份也要并进来**（同图库面板 / PPT 条的做法）：指针一帧里可能移动
            // 超过半径（快擦；面积擦的框还会随速度变大变小），只算当前位置的话，旧框留在
            // 原地的那一条就成了"擦不掉的竖线 / 变暗的带子"——正是用户报的那个现象。
            if (!_cursorRectPrev.IsEmpty) r.Add(_cursorRectPrev);
            _cursorRectPrev = cur;
        }
        else if (!_cursorRectPrev.IsEmpty)
        {
            // 落点反馈消失了（换工具 / 指针出界）：最后再擦一次它原来的位置。
            r.Add(_cursorRectPrev);
            _cursorRectPrev = RectF.Empty;
        }

        // 多指擦会话（三指 / 手掌）的落点反馈：落点 = 所有触点的中心，一个"鼠标同款"落点，
        // 每帧重画、一帧里可能移动很远——脏区照上面 DrawnCursor 同一套账：当帧 ∪ 上一帧；
        // 结束那一帧把最后的位置再擦一次（不然旧的圆环/方框就"留在屏幕上"）。
        if (app.TouchEraseSessionActive && app.TouchEraseCenter(out var ec))
        {
            float pad = app.TouchEraseSessionPixel
                ? MathF.Max(app.PixelEraserCursorHalfWidthPx, app.PixelEraserCursorHalfHeightPx) + 8f
                : app.EraserRadius * 1.35f + 10f;
            var box = RectF.Empty;
            box.Add(ec.X - pad, ec.Y - pad);
            box.Add(ec.X + pad, ec.Y + pad);
            var cur = CanvasRectToWindow(box);
            r.Add(cur);
            if (!_touchEraseRectPrev.IsEmpty) r.Add(_touchEraseRectPrev);
            _touchEraseRectPrev = cur;
        }
        else if (!_touchEraseRectPrev.IsEmpty)
        {
            r.Add(_touchEraseRectPrev);
            _touchEraseRectPrev = RectF.Empty;
        }

        // 对象吸附的短亮：吸附瞬间只在目标处亮 220ms，不写字、不常驻。
        // 和落点反馈一样，当前帧与上一帧都要并进脏区，否则淡出最后一帧会留一圈残影。
        if (app.AlignSnapPulseActive && app.AlignSnapPulseKind != InkEngine.AlignSnapKind.None)
        {
            float dpi = app.DpiScale;
            var p = app.AlignSnapPulsePoint;
            var b = RectF.Empty;
            if (app.AlignSnapPulseKind == InkEngine.AlignSnapKind.Point)
            {
                b.Add(p.X - 24f * dpi, p.Y - 24f * dpi);
                b.Add(p.X + 24f * dpi, p.Y + 24f * dpi);
            }
            else if (app.AlignSnapPulseKind == InkEngine.AlignSnapKind.Horizontal)
            {
                float h = app.AlignSnapPulseHalfLen + 8f * dpi;
                b.Add(p.X - h, p.Y - 8f * dpi);
                b.Add(p.X + h, p.Y + 8f * dpi);
            }
            else
            {
                float h = app.AlignSnapPulseHalfLen + 8f * dpi;
                b.Add(p.X - 8f * dpi, p.Y - h);
                b.Add(p.X + 8f * dpi, p.Y + h);
            }
            var w = CanvasRectToWindow(b);
            r.Add(w);
            if (!_alignPulseRectPrev.IsEmpty) r.Add(_alignPulseRectPrev);
            _alignPulseRectPrev = w;
        }
        else if (!_alignPulseRectPrev.IsEmpty)
        {
            r.Add(_alignPulseRectPrev);
            _alignPulseRectPrev = RectF.Empty;
        }

        if (app.MarqueeActive)
        {
            var m = RectF.Empty;
            m.Add(app.MqMinX, app.MqMinY);
            m.Add(app.MqMaxX, app.MqMaxY);
            m = CanvasRectToWindow(m);       // 框选矩形是画布坐标，脏区要窗口坐标
            r.Add(m.Inflate(3f));
        }

        // 截图（8.3.0）：整屏遮罩 + 冻结帧挖洞 + 准线 + 取景框，**每帧都在动**，
        // 所以取景期间干脆把整个窗口进脏区（这段时间本来就每帧全屏重画，一两秒的事）；
        // **收场那一帧**同样要整窗——把遮罩擦干净。
        if (app.CaptureActive || _captureWasActive)
            r.Add(new RectF { MinX = OriginX, MinY = OriginY, MaxX = OriginX + Width, MaxY = OriginY + Height });
        _captureWasActive = app.CaptureActive;

        // 屏幕吸管（2026-10-28）：冻结帧铺满整窗，放大镜跟着指针每帧移动，
        // 退出后的第一帧还要把上一次的镜片/读数擦掉。和截图同理，短时模态，
        // **整窗进脏区**最稳；按"旧镜 ∪ 新镜"抠细节省不了多少，还会漏。
        if (app.ColorPickActive || _colorPickWasActive)
            r.Add(new RectF { MinX = OriginX, MinY = OriginY, MaxX = OriginX + Width, MaxY = OriginY + Height });
        _colorPickWasActive = app.ColorPickActive;

        // 图库面板：开合、悬停高亮、勾选都会变，所以**每帧按当前矩形算进脏区**；
        // 关掉之后旧位置也要擦干净——所以上一帧那份矩形同样并进来
        //（和界面 `_uiBoundsPrev` 一模一样的做法：只算当前，屏幕会留一张擦不掉的卡片印子）。
        if (app.LibraryPanelVisibleNow && !app.LibraryPlacementMode)
        {
            float pad = FloatingDirtyPad(app, app.UiUnit);
            r.Add(CanvasRectToWindow(app.LibraryPanelRectNow()).Inflate(pad));
            if (app.LibraryCategoriesOpen)
                r.Add(CanvasRectToWindow(app.LibraryDrawerRectNow()).Inflate(pad));
            if (app.LibraryMenu != 0)
                r.Add(CanvasRectToWindow(app.LibraryMenuRect()).Inflate(pad));
        }
        if (!_libraryRectPrev.IsEmpty) r.Add(_libraryRectPrev);

        // 跟随鼠标的收藏虚影：它每帧都在动，必须和光标圈同一套脏区，
        // 否则只会画在上一块脏区里（表现为“虚影只能在屏幕某一块矩形里移动”）。
        if (app.LibraryGhostVisibleNow && app.LibraryUseSelected?.Strokes != null)
        {
            var box = EditRegion.Of(app.LibraryUseSelected.Strokes);
            if (!box.IsEmpty)
            {
                var move = app.LibraryPlacementMatrix(app.LibraryUseSelected.Strokes,
                                                      app.PointerX, app.PointerY);
                var world = new RectF
                {
                    MinX = box.MinX + move.M31, MinY = box.MinY + move.M32,
                    MaxX = box.MaxX + move.M31, MaxY = box.MaxY + move.M32,
                };
                r.Add(CanvasRectToWindow(world).Inflate(8f * app.DpiScale));
            }
        }

        // 底部页码条（含它上面的页号面板 / 菜单）：悬停、拖动每帧都在变，
        // 必须按当前矩形算进脏区；**退出放映/关文档那一帧**旧位置也要擦掉，所以上一帧
        // 那份矩形同样并进来（同图库面板、界面 `_uiBoundsPrev` 的做法）。
        {
            var cur = RectF.Empty;
            if (app.PageBarVisible)
            {
                cur = app.PptBarRect();
                if (app.PptPagePanelOpen) { app.PptPanelRect(out var pp); cur.Add(pp.MinX, pp.MinY); cur.Add(pp.MaxX, pp.MaxY); }
                // 菜单和首次引导都长在条的上方（引导还比菜单窄一点，用同一个矩形兜住就够）
                if ((!app.BoardOn && app.PptMode && app.PptHintVisible) || app.PptMenuOpen)
                { app.PptMenuRect(out var pm); cur.Add(pm.MinX, pm.MinY); cur.Add(pm.MaxX, pm.MaxY); }
                // 缩放档位浮层（文档模式）同理：它开在条的上方，不收进脏区就不上屏。
                if (app.PptZoomMenuOpen)
                { app.PptZoomMenuRect(out var zm); cur.Add(zm.MinX, zm.MinY); cur.Add(zm.MaxX, zm.MaxY); }
                float barPad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(barPad));
            }
            if (!_pptBarRectPrev.IsEmpty) r.Add(_pptBarRectPrev);
            _pptBarRectPrev = cur;
        }

        // 回放：控制条（悬停/拖动/数值都在变）＋ 正在长的那一条的前缀——都每帧在变，
        // 必须按当前矩形算进脏区；退出回放那一帧旧位置也要擦（同 PPT 条、图库面板的套路）。
        {
            var cur = RectF.Empty;
            if (app.ReplayActive)
            {
                cur = app.ReplayBarRect();
                float pad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(pad));
                int idx = app.ReplayCurrentIndexNow;
                if (idx >= 0 && idx < app.ReplayStrokesNow.Count)
                {
                    var b = app.ReplayStrokesNow[idx].PaddedBounds.Inflate(4f * Dpi / 96f);
                    r.Add(CanvasRectToWindow(b));
                }
            }
            if (!_replayBarRectPrev.IsEmpty) r.Add(_replayBarRectPrev);
            _replayBarRectPrev = cur;
        }

        // 课堂计时卡片：跑秒 / 到点闪烁 / 拖动都在变，按当前矩形算进脏区；
        // 停下 / 收起 / 放大还原那一帧旧位置也要擦（同 PPT 条、回放条的套路）。
        {
            var cur = RectF.Empty;
            if (app.TimerCardOpen)
            {
                cur = app.TimerCardRect();
                float pad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(pad));
            }
            if (!_timerRectPrev.IsEmpty) r.Add(_timerRectPrev);
            _timerRectPrev = cur;
        }

        // 课堂点名卡片：滚动/拖动/开合都在变；关掉那一帧旧位置也要擦（同计时卡）。
        {
            var cur = RectF.Empty;
            if (app.RollCardOpen)
            {
                cur = app.RollCardRect();
                float pad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                r.Add(cur.Inflate(pad));
            }
            if (!_rollRectPrev.IsEmpty) r.Add(_rollRectPrev);
            _rollRectPrev = cur;
        }

        // 引擎侧悬停提示（选中操作条 / PPT 条与长按菜单）：显示、换块、收起都在变，
        // 按当前矩形算进脏区；**收起那一帧**旧位置也要擦（同图库 / PPT 条的做法）。
        {
            var cur = RectF.Empty;
            if (app.TooltipShown)
            {
                cur = TooltipCardRect(app);
                float tipPad = 4f + app.FloatingTheme.ShadowReachLogical * Dpi / 96f;
                if (!cur.IsEmpty) r.Add(cur.Inflate(tipPad));
            }
            if (!_tipRectPrev.IsEmpty) r.Add(_tipRectPrev);
            _tipRectPrev = cur;
        }

        // 轻提示：显示、换文案、到点消失都在变；**消失那一帧**旧矩形也要擦。
        {
            var cur = RectF.Empty;
            if (app.ToastVisible)
            {
                cur = ToastRect(app);
                r.Add(cur.Inflate(4f * Dpi / 96f));
            }
            if (!_toastRectPrev.IsEmpty) r.Add(_toastRectPrev);
            _toastRectPrev = cur;
        }

        // 选中高亮画在浮动层上、不进内容层，所以它的区域必须每帧算进脏区。
        //
        // 注意**不能只算对象自己的包围盒**：选中框这一套 UI 比对象大——
        // 外侧的光晕、跨在边线上的手柄、以及伸到上边外侧的旋转手柄。
        // 漏掉哪一块，哪一块就会在屏幕上留下擦不掉的残影（实测踩过：
        // 旋转手柄旁边留了一小块红色的前帧残留）。
        if (app.Doc.Selected.Count > 0)
        {
            // 拖动预览期间框是"实时"的（模型还没动，见 LiveSelectionFrame）：
            // 脏区必须跟着它走，否则框和手柄会在新位置上留下擦不掉的残影。
            var frame = app.LiveSelectionFrame;
            var sb = frame.CanvasAabb;
            float dpi = app.UiUnit;
            float margin = SelectionHandles.VisualSizeLogical * 0.5f * dpi + 6f * dpi;
            // 手柄画在**最小操作框**上（小对象时比真实框大一圈），脏区也按它算。
            var ui = SelectionHandles.UiBox(sb, dpi).Inflate(margin);

            var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
            float grip = SelectionHandles.RotateGripLogical * 0.5f * dpi + 3f * dpi;
            ui.Add(rot.X - grip, rot.Y - grip);
            ui.Add(rot.X + grip, rot.Y + grip);
            r.Add(CanvasRectToWindow(ui));   // 选中框也是画布坐标（它跟着墨迹走）

            // 操作条在选中框下方，也必须算进来，否则它自己会留下残影。
            // **余量 = 4 ＋ 投影最远伸出多少**：操作条现在带投影（主题给的），
            // 只放 4 像素的话投影会被脏区切掉、在屏幕上留一条印子。
            float cardPad = (4f + app.FloatingTheme.ShadowReachLogical) * dpi;
            r.Add(CanvasRectToWindow(app.SelectionBarCanvasRect.Inflate(cardPad)));

            // 交互矢量图的参数滑杆：浮在选中框上方，**必须每帧算进脏区**，
            // 否则拖动参数时曲线变了、滑杆却停在旧值（2026-11-09 v2.1 实测踩到）。
            if (app.Doc.Selected.Count == 1
                && app.Doc.Selected[0].Kind == StrokeKind.Figure
                && !app.Doc.Selected[0].Locked)
            {
                var keys = FigureParamBar.Keys(app.Doc.Selected[0]);
                if (keys.Count > 0)
                {
                    var pr = FigureParamBar.PanelRect(sb, dpi, app.FurnitureViewport, keys.Count);
                    r.Add(CanvasRectToWindow(pr.Inflate(4f * dpi)));
                }
            }

            // 收起态的圆钮 / 打开的面板 / 提示条：都是"每帧都在变"的浮动层，
            // 漏一块就在屏幕上留一块擦不掉的残影（这条踩过好几次了）。
            r.Add(CanvasRectToWindow(app.SelectionDotCanvasRect.Inflate(cardPad)));
            if (app.SelPanelOpen == SelPanel.Ink)
            {
                r.Add(CanvasRectToWindow(SelectionHandles
                    .PanelRect(sb, dpi, app.FurnitureViewport, SelectionHandles.SwatchCount,
                               app.SingleTextBoxSelection)
                    .Inflate(cardPad + 2f * dpi)));
                if (app.CustomColorOpen)
                    r.Add(CanvasRectToWindow(SelectionHandles
                        .CustomPanelRect(sb, dpi, app.FurnitureViewport, SelectionHandles.SwatchCount,
                                         app.SingleTextBoxSelection)
                        .Inflate(cardPad + 2f * dpi)));
            }
            else if (app.SelPanelOpen == SelPanel.Layer)
                r.Add(CanvasRectToWindow(SelectionHandles.LayerPanelRect(sb, dpi, app.FurnitureViewport,
                        app.SingleTextBoxSelection)
                    .Inflate(cardPad + 2f * dpi)));
            else if (app.SelPanelOpen == SelPanel.FontSize && app.SingleTextBoxSelection)
                r.Add(CanvasRectToWindow(SelectionHandles.FontPanelRect(sb, dpi, app.FurnitureViewport)
                    .Inflate(cardPad + 2f * dpi)));

            // AI / `⋯` 弹层：显示、换菜单、关闭都要擦旧位置。
            var menu = app.SelectionMenuRectNow();
            if (!menu.IsEmpty) r.Add(CanvasRectToWindow(menu.Inflate(cardPad)));
            if (!_selMenuRectPrev.IsEmpty) r.Add(_selMenuRectPrev);
            _selMenuRectPrev = menu.IsEmpty ? RectF.Empty : CanvasRectToWindow(menu);

            // 字体下拉（「更多…」）：**整块都要进脏区**，否则右边那一截不重画，
            // 看起来就是“显示不全 + 残影”（2026-11-11 用户反馈）。
            {
                var fl = app.FontListRectNow();
                var cur = fl.IsEmpty ? RectF.Empty
                                     : CanvasRectToWindow(fl.Inflate(cardPad + 4f * dpi));
                if (!cur.IsEmpty) r.Add(cur);
                if (!_fontListRectPrev.IsEmpty) r.Add(_fontListRectPrev);
                _fontListRectPrev = cur;
            }

            // 旋转读数标签贴在旋转手柄外侧，比选中框本身还高出去一截，
            // 同样必须进脏区；拖动中它每帧都在动，靠 _transientHistory 回溯两帧。
            // **文案要和绘制用同一个函数**：单选直线时它是 `α = xx.x°`、别的对象是 `Δ`，
            // 两者宽度不同——脏区按哪个文案算，就得画哪个文案。
            // 2026-11-11：结束旋转那一帧也要擦旧位置（否则读数会在屏幕上留残影）。
            {
                var cur = RectF.Empty;
                if (app.SelRotating)
                {
                    cur = CanvasRectToWindow(RotationReadoutRect(
                        frame, dpi, CurrentRotationReadout(app)).Inflate(3f * dpi));
                    r.Add(cur);
                }
                if (!_rotReadoutRectPrev.IsEmpty) r.Add(_rotReadoutRectPrev);
                _rotReadoutRectPrev = cur;
            }

            // 拖定义元素 / 拖四角时的读数胶囊：挂在"正在拖的那个元素"上方，每帧都跟着指针走，
            // 而且它在元素外侧、根本不在选中框里——漏了这一块就会在屏幕上留一行残影。
            // **文案与绘制同一个函数**（圆的 `r = / d =`、直线的 `α = `、
            // 吸住时的"等边/正方形"宽度都不一样）。
            // 拖定义元素 / 拖四角时的读数胶囊：挂在"正在拖的那个元素"上方，每帧都跟着指针走，
            // 而且它在元素外侧、根本不在选中框里——漏了这一块就会在屏幕上留一行残影。
            // 2026-11-11：胶囊消失那一帧也要擦旧位置。
            {
                var vp = (app.VertexDragging || app.ShapeSnapKind != ShapeSnapKind.None)
                    ? VertexPillRect(app) : RectF.Empty;
                var cur = vp.IsEmpty ? RectF.Empty : CanvasRectToWindow(vp.Inflate(3f));
                if (!cur.IsEmpty) r.Add(cur);
                if (!_vertexPillRectPrev.IsEmpty) r.Add(_vertexPillRectPrev);
                _vertexPillRectPrev = cur;
            }

            // 多边形角标（规格 9.7）：**拖顶点时**才有（三角形 / 平行四边形都是），而且每一颗
            // 都挂在顶点外侧（横着让到边的外面）、根本不在选中框里——不单独加进来就会被脏区裁掉，
            // 屏幕上留下几行擦不掉的数字。**盒子和绘制走同一个函数**（FillAnglePills），
            // 所以它说这一帧有几颗，这里就加几块。
            for (int i = 0, np = FillAnglePills(app, _anglePills); i < np; i++)
                r.Add(CanvasRectToWindow(_anglePills[i].Rect.Inflate(3f)));
        }

        // 画线过程中的 α 读数（用户 2026-09-18 四条之一）。
        // **必须在"有选中对象"那块之外**：画线的时候没有选中对象（没做画完自动选中），
        // 挂在里面的话这块脏区永远不会被算进来，标签就会在屏幕上留一行残影。
        //
        // ⚠ 2026-11-11：**收起那一帧也要擦旧位置**。只算"当前还显示着"的矩形，
        // 抬手/松开后标签消失，旧像素没人重画 → 用户看到"变直线后提示框有残影、
        // 还显示不全"（脏区少一块，标签被裁掉一角）。
        {
            var cur = RectF.Empty;
            if (app.ShapeInclinationActive)
            {
                cur = CanvasRectToWindow(InclinationReadoutRect(
                    app.ShapeInclinationAnchor, app.DpiScale,
                    ShapeLineReadout(app)).Inflate(3f));
                r.Add(cur);
            }
            if (!_inclPillRectPrev.IsEmpty) r.Add(_inclPillRectPrev);
            _inclPillRectPrev = cur;
        }

        // 多笔图形"这一笔吸到了什么"那颗胶囊（现在只有棱柱的「直棱柱」）。
        // **同样必须在"有选中对象"那块之外**：画棱柱的时候一个对象都没选中。
        {
            var cur = RectF.Empty;
            if (app.StepSnap != ShapeSnapKind.None)
            {
                var stepPill = StepSnapPillRect(app);
                if (!stepPill.IsEmpty)
                {
                    cur = CanvasRectToWindow(stepPill.Inflate(3f));
                    r.Add(cur);
                }
            }
            if (!_stepPillRectPrev.IsEmpty) r.Add(_stepPillRectPrev);
            _stepPillRectPrev = cur;
        }

        if (app.ShowHud)
        {
            // 面板区域每帧都要进脏区：后缓冲里躺着的是两帧前的画面，不重贴就会闪。
            float m = HudMarginLogical * HudScale;
            var h = RectF.Empty;
            h.Add(OriginX + m - 2, OriginY + m - 2);
            h.Add(OriginX + m + HudWidthPx + 2, OriginY + m + HudHeightPx + 2);
            r.Add(h);
        }

        if (app.TouchHud)
        {
            // 触点诊断（8.3.3）：左下角的小浮层，同性能面板的道理——每帧都要进脏区。
            var h = TouchHudRect(app);
            if (!h.IsEmpty) r.Add(h);
        }

        if (app.EraserHud)
        {
            // 橡皮读数（8.3.7）：同一角落，每帧都要进脏区（数字一直在变）。
            var h = EraserHudRect(app);
            if (!h.IsEmpty) r.Add(h);
        }

        // 滚动条画在右边缘，而且要淡出，所以画的那些帧必须算进脏区，
        // 否则它消失之后会在屏幕上留一条擦不掉的线。
        //
        // ⚠ **两条纪律**：
        //   ① 它是**单独一块**，不能并进 `r`（静止图元的联合包围盒）——它整屏高，
        //      只要当前有一笔在屏幕左半边，联合包围盒就会变成"从笔迹到屏幕右缘"，
        //      把"笔迹左边"整块排除在脏区外。2026-10-04 用户报的"滚完轮一按就闪/错位、
        //      只在左侧"就是它兜出来的（配合相机变化只重画一帧）。分开之后互不拖累。
        //   ② **只在它真的会画出来的时候才占脏区**（悬停/拖动/淡出窗口内）。不画就不占，
        //      这样"这一帧什么都要不重画"时 `_frameDirty` 才是真的空、才能走整屏兜底
        //      （对齐 Windows Terminal：没有失效就不 BeginPaint）。
        //      淡出结束那一帧旧位置还要多擦一次，所以 UpdateFrameDirty 里会连上一帧的矩形一起加。
        bool sbShown = TryScrollBar(app, out _)
                       && (app.ScrollBarHover || app.ScrollBarDragging
                           || (app.NowMs - app.ScrollBarActiveAtMs) / 1000.0 <= 3.5);
        _scrollbarRect = sbShown
            ? new RectF
              {
                  MinX = OriginX + Width - 30f * app.DpiScale,
                  MinY = OriginY,
                  MaxX = OriginX + Width,
                  MaxY = OriginY + Height,
              }
            : RectF.Empty;

        // 选中被清空那一帧：上面那段 `Selected.Count > 0` 不再执行，
        // 弹层的旧位置要在这里补擦一次，否则关菜单会留一块卡片。
        if (app.Doc.Selected.Count == 0 && !_selMenuRectPrev.IsEmpty)
        {
            r.Add(_selMenuRectPrev);
            _selMenuRectPrev = RectF.Empty;
        }

        return r;
    }

    /// <summary>汇总这一帧要上屏的矩形交给 Present1。</summary>
    private void UpdateFrameDirty(InkEngine app, bool uiVisible)
    {
        _frameDirty.Clear();

        var full = new RectF
        {
            MinX = OriginX, MinY = OriginY,
            MaxX = OriginX + Width, MaxY = OriginY + Height,
        };

        // 脏区也看 **RenderDoc**（回放中是影子文档——它才是内容层这一帧画的东西）。
        // 只看真文档的话，回放里"刚出完的那一条"不会进上屏脏区，贴出来的是上一帧。
        var doc = app.RenderDoc;
        _contentDirtyNow.Clear();
        if (doc.Dirty.Full)
            _contentDirtyNow.Add(full);
        else
            foreach (var raw in doc.Dirty.Rects) _contentDirtyNow.Add(CanvasRectToWindow(raw));

        if (_fullFramesLeft > 0)
        {
            // 首帧 / 相机变了 / 整层重建 / 换底色：**连续两帧**整屏重画——两个后缓冲
            // 都要更新到新状态（见 _fullFramesLeft 的说明；只给一帧就是"滚完按下闪"的根因）。
            _frameDirty.Add(full);
            _contentDirtyPrev.Clear();
            _fullFramesLeft--;
        }
        else
        {
            // 这一帧和上一帧的内容改动都要重画（后缓冲里是两帧前的画面）
            AddClipped(_frameDirty, _contentDirtyPrev);
            AddClipped(_frameDirty, _contentDirtyNow);
            // 文档页层同理：页生成/回收要覆盖这一帧和上一帧两处
            AddClipped(_frameDirty, _pageDirtyPrev);
            AddClipped(_frameDirty, _pageDirtyNow);
        }

        // 【8.4.3】8.4.1 那记"面积橡皮框可见时整窗重画"已经**撤掉**：
        // 它是排查期间的保险/对照实验，真正的根因是下面 `AddClipped` 里的**像素对齐**
        // （小数边界会让每个脏区矩形的左边缘漏一列像素）。用户 8.4.2 实测确认干净之后，
        // 就没有必要为它付"每帧整窗重画"的钱了（弱机上那是 20~40ms/帧的顿感）。
        // 万一以后再看到那种"跟着框走的竖线"，先回来看 `AddClipped`：那里是唯一一处
        // 决定"哪些像素会被擦到"的地方。

        // 临时图元：这一帧 + 前两帧（双缓冲里躺着的是两帧前的画面）
        var t = _transientNow;
        for (int i = 0; i < Math.Clamp(TransientHistoryFrames, 0, 2); i++)
            t.Add(_transientHistory[i]);
        if (!t.IsEmpty) AddClipped(_frameDirty, t);

        // 右缘滚动条那一条：**单独加**，不参与上面那个联合包围盒（理由见 ComputeTransientBounds）。
        // 上一帧那一份也要加：它可能是"淡出结束、这一帧不再画"的第一帧，少加一帧就留下一条残影。
        if (!_scrollbarRect.IsEmpty) AddClipped(_frameDirty, _scrollbarRect);
        if (!_scrollbarRectPrev.IsEmpty) AddClipped(_frameDirty, _scrollbarRectPrev);
        _scrollbarRectPrev = _scrollbarRect;

        // 界面每帧都会重新贴到后缓冲上，所以它那块区域每帧都得算进上屏的脏区，
        // 否则双缓冲一交换，界面就会闪一下或干脆不见了。
        // 界面这一帧和上一帧占的地方都要算进来：界面可能是刚刚消失的
        // （比如穿透提示淡出、调色板收起），双缓冲里还留着它的旧画面。
        if (uiVisible) AddClipped(_frameDirty, _uiBounds);
        if (!_uiBoundsPrev.IsEmpty) AddClipped(_frameDirty, _uiBoundsPrev);

        // 兜底：真要是一个矩形都没有，就整屏来一次，避免出现没擦干净的画面
        if (_frameDirty.Count == 0) _frameDirty.Add(full);

        if (Trace)
        {
            string R(RectF r) => r.IsEmpty ? "-" : $"({r.MinX:F0},{r.MinY:F0})..({r.MaxX:F0},{r.MaxY:F0})";
            string RL(List<RectF> l) => l.Count == 0 ? "-" : string.Join(",", l.ConvertAll(x => R(x)));
            Console.WriteLine($"    [dirtysrc] now={RL(_contentDirtyNow)} prev={RL(_contentDirtyPrev)} "
                              + $"trans={R(_transientNow)} h0={R(_transientHistory[0])} h1={R(_transientHistory[1])} "
                              + $"ui={R(_uiBounds)} uiprev={R(_uiBoundsPrev)} cursor={R(_cursorRectPrev)} uiVis={uiVisible}");
        }

        _transientHistory[1] = _transientHistory[0];
        _transientHistory[0] = _transientNow;

        _contentDirtyPrev.Clear();
        _contentDirtyPrev.AddRange(_contentDirtyNow);

        // 文档页层同一套：这一帧的留给下一帧当"上一帧"
        _pageDirtyPrev.Clear();
        _pageDirtyPrev.AddRange(_pageDirtyNow);
    }

    private void AddClipped(List<RectF> list, RectF r)
    {
        // **像素对齐**（8.4.2 修）：脏区是从画布/逻辑坐标换算过来的，边界常带小数
        // （DPI 2.0、相机亚像素偏移、奇数尺寸）。小数边界交给 `PushAxisAlignedClip` + `Copy`
        // 填充时，**最左（最上）那一列像素可能只被裁进去一半——擦不掉**，屏幕上就留下一条
        // 竖线（旧内容）。用户 2026-09-30 报的两条正是它：
        //   · "面积橡皮左侧始终跟着一根黑线"——每个脏区矩形各漏一条左边缘；
        //     整窗重画后只剩一个矩形，所以"大残影带没了、只剩一根"；
        //   · "悬浮栏收起展开会残留黑竖线"——同一件事（界面那块脏区）。
        // 往外取整（floor 左/上、ceil 右/下）之后，裁剪框和上屏矩形都落在整像素上。
        var c = ClipToWindow(new RectF
        {
            MinX = MathF.Floor(r.MinX), MinY = MathF.Floor(r.MinY),
            MaxX = MathF.Ceiling(r.MaxX), MaxY = MathF.Ceiling(r.MaxY),
        });
        if (!c.IsEmpty) list.Add(c);
    }

    private void AddClipped(List<RectF> list, List<RectF> rects)
    {
        foreach (var r in rects) AddClipped(list, r);
    }

    /// <summary>
    /// 拖动预览：手势中把被"摘出内容层"的那一批按**实时变换**画在最上层。
    ///
    /// 为什么要有这一层：拖动期间模型一个字都不改（所以内容层那些块全部有效、
    /// 一帧都不用重画，最坏帧不再随"走过的面积"涨），实时位移只活在
    /// <see cref="InkEngine.DragPreviewMatrix"/> 里；旧位置则是从内容层贴回来的，
    /// 像素级还原、不会闪。
    ///
    /// 得到的两个副作用，都是想要的：
    ///   · 被拖的那块**自动浮起来**（它画在整层内容之上）——不用去压暗周围；
    ///   · 拖动期间 z 序暂时在最上层（松手回到正确层次，这是"拖动预览"的常规取舍）。
    ///
    /// 画法必须与内容层**像素一致**，否则松手那一下会"跳"：这里左乘的矩阵正是
    /// 内容层会把对象变换成的那个（对象自己的变换 × 实时矩阵），
    /// 自检里有一条"松手前后同一段墨的墨量一致"盯着它。
    /// </summary>
    private void DrawDragPreview(InkEngine app)
    {
        // **先问"拖动预览到底活着没有"**（DragPreviewActive），不能只看"摘出去了几条"：
        // 拖端点的手势复用了同一套"摘出去"的机制（DetachForDrag），但那时画在浮动层上的
        // 是**临时几何**（DrawVertexPreview），不是"这批对象按实时矩阵位移"。
        // 少了这一句，拖端点时会用**单位矩阵**把那条旧线再画一遍——屏幕上就是"旧线不动 +
        // 新线跟着指针"两条线，而内容层明明已经跳过它了（2026-09-18 出图核对时抓到）。
        if (!app.DragPreviewActive) return;

        var strokes = app.DragPreviewStrokes;
        if (strokes.Count == 0) return;

        // 实时矩阵**当成 extra 传下去**，而不是塞进 ctx：`KeepsWidth` 的图形
        //（线宽不变，见 Stroke.KeepsWidth）要把这层矩阵折进几何里；塞进 ctx 的话，
        // 拖动过程中线宽会被一起拉粗、松手又弹回去（看着像抖一下）。
        foreach (var s in strokes) DrawStroke(s, app.DragPreviewMatrix);
    }

    /// <summary>
    /// 拖端点时的**临时几何**：手势期模型一个字不改，只在浮动层把
    /// "改过端点的那一条图形"按**实时端点**画出来（见 计划-图形工具.md 8.1①）。
    ///
    /// 和拖动预览是同一套路子（画在浮动层、松手回归内容层），区别只有一个：
    /// 那边改的是**变换矩阵**，这里改的是**几何**——所以画的是同一条对象、换一组点，
    /// 变换仍用它自己那个（局部坐标 → 画布）。
    /// </summary>
    private void DrawVertexPreview(InkEngine app)
    {
        var src = app.VertexPreviewStroke;
        if (src == null) return;
        var pts = app.VertexPreviewPoints;
        if (pts == null || pts.Count == 0) return;

        _vertexGhost ??= new Stroke();
        var g = _vertexGhost;
        g.Tool = src.Tool;
        g.Kind = src.Kind;
        g.Color = src.Color;
        g.Width = src.Width;
        g.Dash = src.Dash;          // 线型也要跟着，不然拖一条虚线时预览会突然变实线
        // **曲线朝向也必须跟着**：抛物线开了哪个口 / 双曲线哪条是实轴，
        // 是这个对象几何的一部分。漏了它，拖动预览会按"默认那一档"画，
        // 屏幕上就是"一个朝上的抛物线预览、松手变成朝右的"。
        g.CurveAxis = src.CurveAxis;
        // **渐近线开关 / 焦点三角形**也要跟着（2026-09-22）：它们同样"是这个对象几何的一部分"
        // ——漏了的话，一条"无渐近线"的双曲线在拖动预览里会突然长出两条虚线，松手又没了。
        g.ShowAsymptotes = src.ShowAsymptotes;
        g.FocusTriangle = src.FocusTriangle;
        // P 的位置：拖动中用的是**预览那个角**（见 Engine.VertexPreviewFocusU），
        // 没在拖 P 时（NaN）就照模型里那个（可能是 NaN = 自动摆，交给模型自己判）。
        g.FocusPointU = float.IsNaN(app.VertexPreviewFocusU) ? src.FocusPointU : app.VertexPreviewFocusU;
        // **网格**（2026-09-24）：开关跟着，格距也要跟着——拖那颗格点手柄改的正是格距，
        // 只在松手后才变的话，拖动全程屏幕上的格子纹丝不动，手感就是"这一拖什么也没发生"。
        g.Grid = src.Grid;
        g.AxisGridStep = app.VertexPreviewGridStep > 0f ? app.VertexPreviewGridStep : src.AxisGridStep;
        g.Transform = src.Transform;
        g.SetPoints(pts);           // 局部坐标：变换那一层仍由 g.Transform 负责
        DrawStroke(g);
    }

    /// <summary>
    /// 单选整体拖动时，给**被拖对象自己**画一个淡橙中心十字。
    ///
    /// 它只负责告诉老师"我这个框的中心在哪"，不画目标中心、不画目标框。
    /// 十字中间留空，圆心的永久墨点/坐标原点能在中间露出来；固定屏幕大小，不参与命中。
    /// </summary>
    private void DrawSelectionCenterCross(InkEngine app)
    {
        if (!app.SelectionCenterCrossVisible) return;

        var frame = app.LiveSelectionFrame;
        if (frame.IsEmpty) return;

        var box = frame.CanvasAabb;
        var c = new Vector2((box.MinX + box.MaxX) * 0.5f,
                            (box.MinY + box.MaxY) * 0.5f);
        float dpi = app.UiUnit;
        float arm = 7f * dpi;
        float gap = 2.5f * dpi;
        float width = 1.1f * dpi;
        var white = new Color4(1f, 1f, 1f, 0.42f);
        var accent = new Color4(1f, 0.62f, 0.22f, 0.55f);   // 淡橙：只做瞄准，不做确认

        _scratch.Color = white;
        _ctx.DrawLine(new Vector2(c.X - arm, c.Y), new Vector2(c.X - gap, c.Y), _scratch, width + 0.8f * dpi);
        _ctx.DrawLine(new Vector2(c.X + gap, c.Y), new Vector2(c.X + arm, c.Y), _scratch, width + 0.8f * dpi);
        _ctx.DrawLine(new Vector2(c.X, c.Y - arm), new Vector2(c.X, c.Y - gap), _scratch, width + 0.8f * dpi);
        _ctx.DrawLine(new Vector2(c.X, c.Y + gap), new Vector2(c.X, c.Y + arm), _scratch, width + 0.8f * dpi);

        _scratch.Color = accent;
        _ctx.DrawLine(new Vector2(c.X - arm, c.Y), new Vector2(c.X - gap, c.Y), _scratch, width);
        _ctx.DrawLine(new Vector2(c.X + gap, c.Y), new Vector2(c.X + arm, c.Y), _scratch, width);
        _ctx.DrawLine(new Vector2(c.X, c.Y - arm), new Vector2(c.X, c.Y - gap), _scratch, width);
        _ctx.DrawLine(new Vector2(c.X, c.Y + gap), new Vector2(c.X, c.Y + arm), _scratch, width);
    }

    /// <summary>
    /// 对象吸附的“短亮一下”：不写字、不画全屏辅助线，只在吸附目标处闪 220ms 后淡出。
    /// 中心/双轴＝外扩圆环＋十字；横向/纵向＝目标线的一小段高亮。
    /// 用暖橙色，和蓝色选中框区分开。
    /// </summary>
    private void DrawAlignSnapPulse(InkEngine app)
    {
        if (app.PassThrough || !app.AlignSnapPulseVisible) return;
        if (app.AlignSnapPulseKind == InkEngine.AlignSnapKind.None) return;

        float alpha = app.AlignSnapPulseAlpha;
        if (alpha <= 0.01f) return;

        float dpi = app.UiUnit;
        var p = app.AlignSnapPulsePoint;
        // 暖橙色：短亮要在白板、PPT、选中框旁边都跳出来；蓝色会和选中框混在一起。
        var accent = new Color4(1f, 0.55f, 0.08f, alpha);
        var white = new Color4(1f, 1f, 1f, 0.78f * alpha);

        if (app.AlignSnapPulseKind == InkEngine.AlignSnapKind.Point)
        {
            // 吸附瞬间小环从 10px 外扩一点，比静止细环更容易被看见。
            float grow = 1f + (1f - alpha) * 0.9f;
            float r = 10f * dpi * grow;
            _scratch.Color = white;
            _ctx.DrawEllipse(new Ellipse(p, r + 1.4f * dpi, r + 1.4f * dpi), _scratch, 2.6f * dpi);
            _scratch.Color = accent;
            _ctx.DrawEllipse(new Ellipse(p, r, r), _scratch, 2.2f * dpi);
            return;
        }

        float half = app.AlignSnapPulseHalfLen;
        Vector2 a, b;
        Vector2 ta, tb;
        if (app.AlignSnapPulseKind == InkEngine.AlignSnapKind.Horizontal)
        {
            a = new Vector2(p.X - half, p.Y);
            b = new Vector2(p.X + half, p.Y);
            ta = new Vector2(a.X, a.Y - 5f * dpi);
            tb = new Vector2(a.X, a.Y + 5f * dpi);
            _scratch.Color = white;
            _ctx.DrawLine(a, b, _scratch, 3.6f * dpi);
            _scratch.Color = accent;
            _ctx.DrawLine(a, b, _scratch, 1.8f * dpi);
            _ctx.DrawLine(ta, tb, _scratch, 1.8f * dpi);
            ta = new Vector2(b.X, b.Y - 5f * dpi);
            tb = new Vector2(b.X, b.Y + 5f * dpi);
            _ctx.DrawLine(ta, tb, _scratch, 1.8f * dpi);
        }
        else
        {
            a = new Vector2(p.X, p.Y - half);
            b = new Vector2(p.X, p.Y + half);
            ta = new Vector2(a.X - 5f * dpi, a.Y);
            tb = new Vector2(a.X + 5f * dpi, a.Y);
            _scratch.Color = white;
            _ctx.DrawLine(a, b, _scratch, 3.6f * dpi);
            _scratch.Color = accent;
            _ctx.DrawLine(a, b, _scratch, 1.8f * dpi);
            _ctx.DrawLine(ta, tb, _scratch, 1.8f * dpi);
            ta = new Vector2(b.X - 5f * dpi, b.Y);
            tb = new Vector2(b.X + 5f * dpi, b.Y);
            _ctx.DrawLine(ta, tb, _scratch, 1.8f * dpi);
        }
    }

    /// <summary>
    /// 画选中框和手柄。规格见 design/选中与操作条-设计稿.png（方案 B）。
    ///
    /// 三条设计约束，都跟教室场景有关：
    ///   · **画在浮动层上，不进内容层**。否则每改一次选区就要重画整块内容层，
    ///     而选中是高频操作（每拖一下框选都要变）。
    ///   · **光晕是必须的，不是装饰**。一盏投影打上去，深色 PPT 上一条纯蓝线
    ///     会糊掉；描边外侧那圈半透明蓝让它在浅色和深色背景上都立得住。
    ///   · 手柄的**位置和命中判定都在 SelectionHandles 里算**（那一层只有数学）。
    ///     这里只负责画——换观感改这里，改交互规则改那边，互不牵连。
    ///
    /// 操作条（复制/删除/翻转/旋转那排按钮）还没画，等图标设计定稿。
    /// </summary>
    private void DrawSelection(InkEngine app)
    {
        // **穿透 = 全让开（2026-10-17 用户定）**：选中框 / 手柄 / 操作条也一起藏——
        // 屏幕只剩下层程序 + 我们的面板（墨迹在 RasterizeTile 里同一条规矩）。
        if (app.PassThrough) return;

        // **编辑文字时选中框、手柄、操作条全部藏起来**（2026-11-01 用户照 ClassIn 定）：
        // 此刻屏幕上只该有"文字 + 编辑光标 + 很淡的框"，再多一圈蓝框会让人分不清
        // 自己在编辑还是在选对象。提交后 `TextBoxEditing` 落回 false，框自己回来。
        if (app.TextBoxEditing) return;

        var sel = app.Doc.Selected;
        if (sel.Count == 0) return;

        // 选区坐标系：**一律轴对齐**——不管选了一条还是多条，框都是屏幕上那个正矩形
        // （见 SelectionFrame 的注释）。拖动预览期间取"实时框"：模型还没动，
        // 但框必须跟着内容走，而且是每帧重新贴合当前内容（见 LiveSelectionFrame）。
        var frame = app.LiveSelectionFrame;
        if (frame.IsEmpty) return;
        var b = frame.CanvasAabb;      // 操作条和脏区用它的轴对齐范围

        float dpi = app.UiUnit;
        var accent = new Color4(0f, 0.47f, 0.83f, 1f);      // #0078D4

        // 单选一个"有定义元素的图形"（直线 / 箭头 / 圆 / 椭圆）时，手柄按定义元素给
        // （见下面第 3、4 步）：两个或五个，而不是通用 8 个。
        bool shapeLike = SelectionHandles.ShapeEditable(sel, out var shapeStroke);
        // AI 题卡走"文字对象"那一档：只给选中框 + 框内拖动 + 右边缘改宽，
        // 不给通用八手柄（会把字拉变形），也不给旋转柄。
        bool cardLike = sel.Count == 1 && sel[0].Kind == StrokeKind.TextBox;
        // 普通文本框：四个角 + 左右两条整高竖线；没有上下边/旋转。
        bool plainTextBox = SelectionHandles.PlainTextBox(sel, out _);

        // **这一帧要画几个"定义元素手柄"**（0 = 一个都没有）。
        //
        // 判据是"**这个对象有没有特殊手柄**"，不是"它是不是图形" —— 后者（`shapeLike`）
        // 对**四种曲线**（抛物线 / 双曲线 / 正弦 / 余弦）和**四个立体图形**
        //（圆柱 / 圆锥 / 长方体 / 四面体）也成立，而它们一个特殊手柄都没有，
        // 于是两头空：既画不出特殊手柄，又拿不到下面 else 里的**通用八手柄**
        //（缩放 / 对角拉伸）。用户 2026-09-20 问的就是这件事：
        // "没有特殊点的图形选中以后那八个拉伸对角拉伸旋转这些是不是应该都给？"——**该给**。
        Span<ShapeHandle> shapeHandles = stackalloc ShapeHandle[8];
        int nh = shapeLike ? SelectionHandles.ShapeHandlesOf(shapeStroke, shapeHandles) : 0;

        // 拖动 / 旋转期间的**装饰收敛**（方案 A）：手柄与操作条此刻点不中
        // （指针已被拖拽接管），留着就是"看得见、点不到"，还跟着内容一起晃。
        //
        // 注意三件事：
        //   · 缩放手势不收（拖手柄时别的柄是参照，见 SelChromeCollapsed）；
        //   · 旋转时**留**旋转柄与度数标签：柄标着"我抓的是这个"，度数是读数；
        //   · 不画不等于不算：这些矩形的脏区照旧由 ComputeTransientBounds 给出，
        //     靠两帧回溯把刚消失的那一块擦干净（这里踩过坑，见那段注释）。
        bool collapsed = app.SelChromeCollapsed;

        // 框的四个角。**按四个角连成四边形画**，而不是 DrawRectangle：
        // 现在框一律轴对齐（ToCanvas 是单位阵），两种画法结果一样，
        // 但这样将来真要加"整组的朝向"时，这里一行都不用改。
        var c0 = SelectionHandles.CanvasPosition(SelHandle.TopLeft, frame, dpi);
        var c1 = SelectionHandles.CanvasPosition(SelHandle.TopRight, frame, dpi);
        var c2 = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, dpi);
        var c3 = SelectionHandles.CanvasPosition(SelHandle.BottomLeft, frame, dpi);

        // **旋转中不画这个框**（用户 2026-09-25 试过 ClassIn 之后定）：ClassIn 转的时候没有矩形框。
        // 两个理由：
        //   · 框是**轴对齐**的，一转就每帧重新贴合内容，边角跟着内容一起跳 —— 看着像"框在抖"
        //     （用户："我们的外接矩形框在转的时候总有点小 bug"）；而转的当下没人看框，
        //     都在看内容转到哪个角度了（那个角度有专门的度数标签，见第 6 步）。
        //   · 少画一层，就少一处"框和内容不同步"的机会。
        // 松手（`SelRotating` 落回 false）之后框自己就回来了 —— 什么都不用补。
        //
        // 只收"框"这两步（光晕 ＋ 描边）：手柄那一圈本来就随 `collapsed` 收起来了（见上面），
        // 旋转柄和度数标签在第 3 / 6 步里单独判据，**照旧留着**（此刻它就是"我抓着的那个东西"）。
        if (!app.SelRotating)
        {
            // 1) 描边。**不再画 6.5px 光晕**：大屏上那圈半透明蓝看起来像"线的虚影"，
            // 选中框要干净利落；对比度靠 2.5px 实线和白底手柄就够。
            if (app.SelFlashing)
            {
                _scratch.Color = new Color4(accent.R, accent.G, accent.B, 1f);
                DrawQuad(c0, c1, c2, c3, 5.5f * dpi);
            }
            else
            {
                _scratch.Color = accent;
                DrawQuad(c0, c1, c2, c3, 2.5f * dpi);
            }
        }

        // 3) 旋转手柄（在图形的上方外侧，先画连线再画圆）
        //    拖动中收起来，但**旋转中要留**（此刻它就是"正在抓的那个东西"）。
        //    **圆不给旋转柄**（用户定："圆转了看不出来"）——画都不画，命中也一样。
        var white = Brush(new Color4(1f, 1f, 1f, 1f));
        if (!collapsed || app.SelRotating)
        {
            if (!cardLike && (!shapeLike || SelectionHandles.RotateHandleVisible(shapeStroke)))
            {
                float rotR = SelectionHandles.RotateGripLogical * 0.5f * dpi;
                // 旋转柄的位置：直线/箭头/椭圆三种"定义元素图形"也用**通用框**的算法
                // （框上边中点外再抬一段）——它只是个抓手，位置跟着框走最稳，不用另算一套。
                var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
                // 连线的那一头，和"给不给特殊手柄"**同一条判据**：
                //   · **有**定义元素手柄的（直线 / 圆 / 椭圆 / 三角形 / 平行四边形…）
                //     → 连到**它自己的中心**（一条斜线的包围盒上边中点根本不在线上，
                //       连线看着像连到别的东西上去了）；
                //   · **没有**的（矩形 / 墨迹 / 图像 / 四种曲线 / 四个立体图形）
                //     → 和通用框一样，连到**框上边中点**。
                //     四种曲线和立体图形原来落进的是"图形"那一支，于是那根线连到了
                //     `Points[0]` 与 `Points[1]` 的中点——对抛物线就是"顶点和经过点的中点"，
                //     一个跟图形看起来毫无关系的点（用户 2026-09-20："抛物线的旋转手柄线
                //     连的地方很奇怪"）。判据换过来之后它们和矩形一个样子。
                var topCenter = nh > 0
                    ? RotationGripAnchor(shapeStroke)
                    : SelectionHandles.CanvasPosition(SelHandle.Top, frame, dpi);
                _ctx.DrawLine(topCenter, rot, _scratch, 1.4f * dpi);
                _ctx.FillEllipse(new Ellipse(rot, rotR, rotR), white);
                _ctx.DrawEllipse(new Ellipse(rot, rotR, rotR), _scratch, 1.6f * dpi);
                // 圆里放**官方图标**，不是手画一段弧。
                // 手画那版在投影上看像个"©"——旋转图标的识别特征就是那个箭头，
                // 少一笔就不成形。这是"图标别自己画"的又一个实例。
                float glyph = SelectionHandles.RotateGlyphLogical * dpi;
                DrawIcon(IconPaths.rotate, rot.X - glyph * 0.5f, rot.Y - glyph * 0.5f, glyph, _scratch);
            }
        }

        // 4) 手柄。**判据只有一条：这个对象有没有"定义元素手柄"**（`nh`，见上面那段）：
        //    · **有**（单选直线 / 箭头 / 圆 / 椭圆 / 三角形 / 平行四边形 / 坐标系 / 数轴）：
        //      按**定义元素**给——直线/箭头/圆两个、椭圆两个（右端点管 a、上端点管 b）、
        //      三角形/平行四边形三个顶点、坐标系三个。8 个缩放柄对它们是多余甚至是错的
        //      （左右拉伸一条线会顺手改掉倾斜角；拉一个圆会把它拉成椭圆）。
        //    · **没有**（矩形 / 图像 / 自由笔迹 / 多选 / **四种曲线** / **四个立体图形**）：
        //      **通用 8 手柄**（四角 ＋ 四边中点）。矩形保留它们是因为它是唯一
        //      "拉了还是矩形"的图形；曲线和立体图形是 2026-09-20 补上的
        //      ——在那之前它们两头空（见上面 `nh` 那段）。
        //    白底 + 蓝边：深色背景上是白方块显眼，浅色背景上靠蓝边立住，一套画法两边都成立。
        //    拖动 / 旋转 / 拖元素中收起来（此刻点不中，而且是最"晃眼"的一圈家具）。
        if (!collapsed)
        {
            // 视觉柄**跟着对象大小缩**（小对象别被手柄盖住），命中半径不缩——
            // 判据与理由都在 SelectionHandles.VisualHandleSize。
            float hs = SelectionHandles.VisualHandleSize(frame.CanvasAabb, dpi);
            float radius = hs * 0.28f;
            if (nh > 0)
            {
                for (int i = 0; i < nh; i++)
                    DrawHandleSquare(SelectionHandles.ShapeHandleCanvasPosition(shapeStroke, shapeHandles[i]),
                                     hs, radius, white);
            }
            else if (plainTextBox)
            {
                // 四个角：等比放大/缩小字号。
                Span<SelHandle> corners = stackalloc SelHandle[]
                {
                    SelHandle.TopLeft, SelHandle.TopRight,
                    SelHandle.BottomLeft, SelHandle.BottomRight,
                };
                foreach (var h in corners)
                    DrawHandleSquare(SelectionHandles.CanvasPosition(h, frame, dpi), hs, radius, white);

                // 左右两条整高竖线：整条都能拖宽，超长文本框也不用找中点。
                var aabb = frame.CanvasAabb;
                for (int side = 0; side < 2; side++)
                {
                    var bar = SelectionHandles.TextBoxBarRect(side == 0, aabb, dpi);
                    var rr = new RoundedRectangle(
                        new Vortice.RawRectF(bar.MinX, bar.MinY, bar.MaxX, bar.MaxY),
                        3f * dpi, 3f * dpi);
                    _ctx.FillRoundedRectangle(rr, white);
                    _ctx.DrawRoundedRectangle(rr, _scratch, 1.6f * dpi);
                }
            }
            else
            {
                Span<SelHandle> all = stackalloc SelHandle[]
                {
                    SelHandle.TopLeft, SelHandle.Top, SelHandle.TopRight, SelHandle.Right,
                    SelHandle.BottomRight, SelHandle.Bottom, SelHandle.BottomLeft, SelHandle.Left,
                };
                // **和命中判定同一把尺子**（SelectionHandles.ThinEdges，判据只有那一份）：
                // 对象在某方向比手柄命中直径还窄时，那个方向的边中点柄**画也不画**。
                // 命中既然已经不收它，画出来就只是"看得见点不到"；而且小对象上八个方块
                // 会挤成一团（2026-10-05 用户："墨迹特别小的时候这个框会挤在一起"）。
                var (thinV, thinH) = SelectionHandles.ThinEdges(frame.CanvasAabb, dpi);
                foreach (var h in all)
                {
                    if (thinV && h is SelHandle.Top or SelHandle.Bottom) continue;
                    if (thinH && h is SelHandle.Left or SelHandle.Right) continue;
                    DrawHandleSquare(SelectionHandles.CanvasPosition(h, frame, dpi), hs, radius, white);
                }
            }
        }

        // 4.5) 交互矢量图：参数滑杆（只画在 overlay，不进内容层）。
        if (!collapsed && sel.Count == 1 && sel[0].Kind == StrokeKind.Figure
            && !sel[0].Locked)
        {
            DrawFigureParamBar(app, sel[0], frame, dpi);
        }

        // 5) 操作条（九格）/ 收起态的圆钮。放在下方，理由见 DrawSelectionBar 的注释。        //    拖动 / 旋转中收起来：此刻点不中；而且贴着屏幕下边时它会**停在原地**，
        //    内容继续走、条不跟——那是最像卡死的一幕（见 SelectionHandles.BarRect）。
        //
        // ⚠ 那一块画不画由 `SelectionBarShown` 定；**画整条还是画那颗圆钮**由
        // `BarDrawnCollapsed` 定（两种收起：用户自己收的、画完自动选中那个框——见那里的注释）。
        if (!collapsed && app.SelectionBarShown)
        {
            if (app.BarDrawnCollapsed) DrawCollapsedDot(app, b);
            else DrawSelectionBar(app, b);
        }

        // 5.5) 浮动面板（颜色/粗细、层级）。画在条之后，盖在内容之上。
        //     和条同一条判据：整条不在的时候，它下面挂的面板当然也不在。
        if (!collapsed && app.SelectionBarShown && !app.BarDrawnCollapsed)
        {
            if (app.SelPanelOpen == SelPanel.Ink) DrawInkPanel(app, b);
            else if (app.SelPanelOpen == SelPanel.Layer) DrawLayerPanel(app, b);
            else if (app.SelPanelOpen == SelPanel.FontSize && app.SingleTextBoxSelection)
            {
                DrawFontPanel(app, b);
                if (app.FontListOpen) DrawFontList(app, dpi);
            }
            // 自定义取色板挂在墨迹面板旁边（画在面板之后 = 压在它上面）。
            // 它只在墨迹面板开着时才有意义（面板一关，子面板由引擎一起清掉）。
            if (app.CustomColorOpen && app.SelPanelOpen == SelPanel.Ink) DrawPickPanel(app, b);
        }

        // 6) 浮出的读数标签。**两个数分开显示**（见 调研-图形工具.md 2.3）：
        //    · Δ = 我这一次**转了多少**（逆时针为正、不设上限）→ 拖旋转手柄时出现，
        //      **单选直线/箭头时例外**：那时读的是 `α₀ + Δ`（从这条线原有的倾斜角接着转、
        //      同样不设上限，用户 2026-09-18 澄清），写成纯数字（见 CurrentRotationReadout）；
        //    · α = 这条线**本身**的倾斜角（[0°,180°)，永远非负）→ 画线中、拖端点时出现。
        //    两件事都必须说清楚"这个角度是不是吸出来的"：靠颜色——吸住时整块变强调色
        //    （Figma / Office 也是这个语言）。没有这层提示，用户分不清"我自己拖到的"
        //    和"它替我吸上的"。
        if (app.SelRotating)
        {
            // 同一个文案：盒子按它量宽，字也画它。**改一处就得改两处**的地方收成一个函数
            // （CurrentRotationReadout 也被 ComputeTransientBounds 用来算脏区）。
            string readout = CurrentRotationReadout(app);
            DrawReadoutPill(RotationReadoutRect(frame, dpi, readout), readout, app.SelRotationSnapped);
        }
        else if (app.VertexDragging || app.ShapeSnapKind != ShapeSnapKind.None)
        {
            // 拖的是**定义元素 / 四角**：直线/箭头读 α、圆读 r+d、椭圆读 a 或 b，
            // 吸住特殊形状时改读"吸到了什么"（见 VertexReadoutLabel）。
            // 三角形 / 平行四边形没吸住时**什么都不画**（它们的读数是下面的角标那一组）。
            var pill = VertexPillRect(app);
            if (!pill.IsEmpty)
                DrawReadoutPill(pill, VertexReadoutLabel(app), VertexPillSnapped(app));
        }
        else if (app.StepSnap != ShapeSnapKind.None)
        {
            // **画多笔图形时吸住的**（现在只有棱柱第 2 笔吸到「直棱柱」）。
            // 和拖顶点吸住"等边 / 正方形"是**同一颗胶囊、同一套语言**：
            // 吸住了就整块用强调色（`snapped: true`）——"这个结果是它替我吸上的"。
            var pill = StepSnapPillRect(app);
            if (!pill.IsEmpty)
                DrawReadoutPill(pill, SelectionHandles.ShapeSnapLabel(app.StepSnap), true);
        }

        // 7) 多边形读数（规格 9.7）：三角形的**三个内角**、平行四边形的**两个夹角**。
        //    它不在上面那两个分支里，因为这一组是**好几颗**角标（最多三颗），有一套自己的
        //    "这一帧有哪些"（见 FillAnglePills）。**两个图形都只在拖顶点时出现**
        //    （用户 2026-09-20 定："三角形应该在拖动的时候再显示角度，要不然看起来也乱"）。
        DrawAnglePills(app);
    }

    /// <summary>交互矢量图的参数滑杆（选中时浮在图卡上方；拖动只改 Params）。</summary>
    private void DrawFigureParamBar(InkEngine app, Stroke s, SelectionFrame frame, float dpi)
    {
        var keys = FigureParamBar.Keys(s);
        if (keys.Count == 0) return;
        var panel = FigureParamBar.PanelRect(frame.CanvasAabb, dpi, app.FurnitureViewport,
                                             keys.Count);
        if (panel.IsEmpty) return;

        var accent = new Color4(0f, 0.47f, 0.83f, 1f);
        var border = new Color4(0.82f, 0.85f, 0.89f, 1f);
        var track = new Color4(0.84f, 0.86f, 0.90f, 1f);
        var ink = new Color4(0.20f, 0.22f, 0.26f, 1f);
        var white = Brush(new Color4(1f, 1f, 1f, 1f));

        var rr = new RoundedRectangle(
            new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY),
            10f * dpi, 10f * dpi);
        _ctx.FillRoundedRectangle(rr, Brush(new Color4(1f, 1f, 1f, 0.97f)));
        _ctx.DrawRoundedRectangle(rr, Brush(border), 1.2f * dpi);

        var labelFmt = AiTextFormat(13f * dpi, false, false);
        var valueFmt = AiTextFormat(13f * dpi, true, false);
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            if (!FigureParamBar.TryGet(s, key, out double value)) continue;
            var row = FigureParamBar.RowRect(panel, i, dpi);
            bool hot = app.FigureParamDragging
                       && ReferenceEquals(app.FigureParamStroke, s)
                       && app.FigureParamKey == key;
            if (hot)
            {
                var hr = new RoundedRectangle(
                    new Vortice.RawRectF(row.MinX, row.MinY, row.MaxX, row.MaxY),
                    7f * dpi, 7f * dpi);
                _ctx.FillRoundedRectangle(hr, Brush(new Color4(0.90f, 0.95f, 1f, 1f)));
            }

            _ctx.DrawText(key, labelFmt,
                new Rect(row.MinX + 8f * dpi, row.MinY + 7f * dpi,
                         24f * dpi, 20f * dpi), Brush(ink));

            FigureParamBar.Range(value, out float min, out float max);
            if (hot) { min = app.FigureParamMin; max = app.FigureParamMax; }
            var tr = FigureParamBar.TrackRect(row, dpi);
            float cy = (tr.MinY + tr.MaxY) * 0.5f;
            float tx0 = tr.MinX, tx1 = tr.MaxX;
            float t = Math.Clamp((float)((value - min) / MathF.Max(1e-4f, max - min)), 0f, 1f);
            float knobX = tx0 + (tx1 - tx0) * t;

            _ctx.DrawLine(new Vector2(tx0, cy), new Vector2(tx1, cy),
                          Brush(track), 3f * dpi);
            _ctx.DrawLine(new Vector2(tx0, cy), new Vector2(knobX, cy),
                          Brush(accent), 3f * dpi);
            _ctx.FillEllipse(new Ellipse(new Vector2(knobX, cy), 6.5f * dpi, 6.5f * dpi), white);
            _ctx.DrawEllipse(new Ellipse(new Vector2(knobX, cy), 6.5f * dpi, 6.5f * dpi),
                             Brush(accent), 2f * dpi);

            string text = Fmt((float)value);
            float tw = MeasureTextWidth(text, valueFmt);
            _ctx.DrawText(text, valueFmt,
                new Rect(row.MaxX - tw - 8f * dpi, row.MinY + 7f * dpi,
                         tw + 4f * dpi, 20f * dpi), Brush(ink));
        }
    }

    /// <summary>
    /// 旋转中该显示哪个数：
    ///   · **单选直线/箭头** → `按下时的倾斜角 α₀ + 转过的角 Δ`，**纯数字、一位小数、
    ///     不折角**（`405.0°` / `-135.0°` / `45.0°`）。写成纯数字而不是 `α = …` 是有意的：
    ///     405° 已经超出倾斜角的定义域（[0°,180°)），标成 α 反而错；而且用户说
    ///     "其他和原来的逻辑一样"，原来那套读数就是纯数字。
    ///   · **单选一个图形**（矩形/椭圆/三角形/平行四边形）→ `姿态 = 0.0°`：这个图形
    ///     **相对水平的姿态角**（折在 [0°,180°)，0° = 正的、90° = 竖的）。用户要的用途是
    ///     "歪的椭圆/矩形，看着读数拖到 0° 就转正了"——所以它和 α 只是名字不同、长相一样。
    ///   · **其它情况** → Δ（`FormatDegrees`，整度）：一行都没改。
    /// **绘制与脏区都走它**，免得两边文案不一致
    /// （文案不一致 → 盒子宽度不一致 → 脏区盖不住标签 → 屏幕上留残影）。
    /// </summary>
    private static string CurrentRotationReadout(InkEngine app)
        => app.SelRotationReadsPose
            ? SelectionHandles.FormatPose(app.SelRotationPose)
            : app.SelRotationReadsInclination
                ? SelectionHandles.FormatSignedDegrees(app.SelRotationInclination)
                : RotationLabel(app);

    /// <summary>
    /// **画线过程中的 α 读数**：用户在拉这条线的时候就要看到它现在是多少度
    /// （2026-09-18 四条之一）。贴在被拖动的那一端外侧，和拖端点时是同一颗胶囊。
    ///
    /// 为什么单独一个方法、不挂在 DrawSelection 里：画线的时候**没有选中对象**
    /// （画完自动选中（见 Engine.EndStroke）发生在**松手之后**，拉线的这一路上选区还是空的），
    /// DrawSelection 第一行就返回了。
    /// </summary>
    private void DrawShapeInclination(InkEngine app)
    {
        if (!app.ShapeInclinationActive) return;
        float dpi = app.DpiScale;
        string readout = ShapeLineReadout(app);
        DrawReadoutPill(InclinationReadoutRect(app.ShapeInclinationAnchor, dpi, readout),
                        readout, app.ShapeInclinationSnapped);
    }

    /// <summary>画线中的读数文案：**α ＋ 长度**（同一颗胶囊，和 α 同一个"边画边看"逻辑；
    /// 长度是 2026-10-05 用户要加的）。绘制与脏区必须共用这一份——分开写就会留下擦不掉的边。</summary>
    private static string ShapeLineReadout(InkEngine app)
        => $"{InclinationLabel(app.ShapeInclinationDegrees)}  长 {app.ShapeLength:F0}";

    /// <summary>
    /// 旋转柄那根连线在**图形这一头**挂哪儿：
    ///   · 直线 / 箭头：两个端点的中点（斜线的包围盒上边中点根本不在线上）；
    ///   · 椭圆：它的**中心**（那是它自己的"中心"，框的中心在旋转后不是它）；
    ///   · 三角形 / 平行四边形：三个（四个）顶点的**形心**——"多边形自己的中心"就是它，
    ///     拿某一个顶点当挂点会让那根线看着像挂歪了。
    /// 圆不给旋转柄，走不到这里。
    /// </summary>
    private static Vector2 RotationGripAnchor(Stroke s)
    {
        if (s.Kind == StrokeKind.Ellipse || Stroke.IsSemiAxisEllipse(s.Kind)) return s.ShapeCenterLocal is var c
            ? Vector2.Transform(c, s.Transform) : Vector2.Zero;
        if (s.Kind is StrokeKind.Triangle or StrokeKind.Parallelogram && s.Points.Count >= 3)
        {
            var sum = new Vector2(s.Points[0].X, s.Points[0].Y)
                    + new Vector2(s.Points[1].X, s.Points[1].Y)
                    + new Vector2(s.Points[2].X, s.Points[2].Y);
            if (s.Kind == StrokeKind.Parallelogram) sum += s.ParallelogramFourthLocal();
            float n = s.Kind == StrokeKind.Parallelogram ? 4f : 3f;
            return Vector2.Transform(sum / n, s.Transform);
        }
        var a = SelectionHandles.ShapeHandleCanvasPosition(s, ShapeHandle.Anchor);
        var r = SelectionHandles.ShapeHandleCanvasPosition(s, ShapeHandle.Rim);
        return (a + r) * 0.5f;
    }

    /// <summary>
    /// 拖定义元素 / 拖四角时那颗胶囊的**文案**。五种量（α / r+d / a / b / 吸到了什么）
    /// 都在这里成型，而且**绘制与脏区都调它**——文案一变宽窄就变，
    /// 脏区按哪个文案算就得画哪个文案。**没有可显示的返回 null**（这一帧不画胶囊）。
    ///
    /// 优先级：**特殊形状吸附**（规格 9.6）> 老读数。理由是"吸到了什么"是**这一刻最有用的
    /// 消息**——它回答"我这一下拖出了个什么形状"，而 α / a / b 只是当下这个数。
    /// </summary>
    private static string VertexReadoutLabel(InkEngine app)
    {
        if (app.ShapeSnapKind != ShapeSnapKind.None)
        {
            string snap = app.ShapeSnapKind is ShapeSnapKind.FrameAnchor or ShapeSnapKind.CanvasCenter
                                                   or ShapeSnapKind.CanvasEdge or ShapeSnapKind.GridStep
                                                   or ShapeSnapKind.AxisAngle
                          && !string.IsNullOrEmpty(app.ShapeSnapText)
                ? app.ShapeSnapText
                : SelectionHandles.ShapeSnapLabel(app.ShapeSnapKind);
            // 轴端吸住时**两个数一起给**："吸到了什么" ＋ "这一截有多长"。
            return app.VertexReadout switch
            {
                InkEngine.VertexReadoutKind.AxisXLength => $"{snap} · x 半轴 = {app.VertexReadoutValue:F0}",
                InkEngine.VertexReadoutKind.AxisYLength => $"{snap} · y 半轴 = {app.VertexReadoutValue:F0}",
                _ => snap,
            };
        }
        return app.VertexReadout switch
        {
            // 圆：两个数一起给（半径是"拉多大"，直径是老师嘴里常说的那个数）
            InkEngine.VertexReadoutKind.Radius => $"r = {app.VertexReadoutValue:F1}",
            InkEngine.VertexReadoutKind.AxisA => $"a = {app.VertexReadoutValue:F1}",
            InkEngine.VertexReadoutKind.AxisB => $"b = {app.VertexReadoutValue:F1}",
            // 抛物线：报课本里那个 p（焦点、准线都从它来，见 VertexReadoutKind 的注释）。
            InkEngine.VertexReadoutKind.ParabolaP => $"p = {app.VertexReadoutValue:F1}",
            // 双曲线：**说清是实半轴还是虚半轴**——方向一换，这两个名字会互换，
            // 光写个 a / b 老师会在另一个朝向下看错（这是本族唯一一处"名字会漂"的地方）。
            InkEngine.VertexReadoutKind.HyperbolaReal => $"a = {app.VertexReadoutValue:F1}（实半轴）",
            InkEngine.VertexReadoutKind.HyperbolaImag => $"b = {app.VertexReadoutValue:F1}（虚半轴）",
            // 正弦 / 余弦：峰点 / 谷点那两个手柄是**两个量一起动**的
            //（纵向 = 振幅、横向 = 周期），所以两个数都报出来。
            InkEngine.VertexReadoutKind.WavePeriod =>
                $"T = {app.VertexReadoutValue:F1}  A = {app.VertexReadoutSecondary:F1}",
            InkEngine.VertexReadoutKind.SectorAngle => $"{app.VertexReadoutValue:F0}°",
            // 坐标系：四个轴端各自只改自己那一截，所以读数就是"这一截有多长"。
            InkEngine.VertexReadoutKind.AxisXLength => $"x 半轴 = {app.VertexReadoutValue:F0}",
            InkEngine.VertexReadoutKind.AxisYLength => $"y 半轴 = {app.VertexReadoutValue:F0}",
            InkEngine.VertexReadoutKind.GridStep => $"格距 = {app.VertexReadoutValue:F1}",
            InkEngine.VertexReadoutKind.SpaceAngle => $"x–y 夹角 = {app.VertexReadoutValue:F0}°",
            // 直线/箭头：倾斜角 α（[0°,180°)），和旋转读数 Δ 是两个数；
            // 长度是 2026-10-05 加的第二个数（同 α 一颗胶囊）。
            InkEngine.VertexReadoutKind.Inclination =>
                app.VertexReadoutSecondary > 0.01f
                    ? $"{InclinationLabel(app.VertexReadoutValue)}  长 {app.VertexReadoutSecondary:F0}"
                    : InclinationLabel(app.VertexReadoutValue),
            // 三角形 / 平行四边形没吸住：这一颗胶囊没有可显示的量——它们要显示的是
            // 内角 / 夹角，那是**另一组**角标（见 DrawAnglePills，第③轮）。
            _ => null,
        };
    }

    /// <summary>
    /// 拖定义元素 / 拖四角时那颗胶囊**占的那块矩形**（空 = 这一帧就没有胶囊）。
    ///
    /// 绘制与脏区都走它：文案、锚点、宽度三处**必须同源**——分开算的话，
    /// 脏区按一个宽度算、画按另一个画，屏幕上就会剩下一条擦不掉的边。
    /// 锚点也在这里定：吸住时贴"吸住的那个点"，否则贴被拖的元素。
    /// </summary>
    private RectF VertexPillRect(InkEngine app)
    {
        string text = VertexReadoutLabel(app);
        if (string.IsNullOrEmpty(text)) return RectF.Empty;
        var at = app.ShapeSnapKind != ShapeSnapKind.None
            ? app.ShapeSnapAnchor
            : app.VertexPreviewCanvasPoint;
        return InclinationReadoutRect(at, app.DpiScale, text);
    }

    /// <summary>这颗胶囊是不是"吸出来的"（吸住时整块变强调色）。</summary>
    private static bool VertexPillSnapped(InkEngine app)
        => app.ShapeSnapKind != ShapeSnapKind.None || app.VertexInclinationSnapped;

    /// <summary>
    /// **多笔图形"这一笔吸到了什么"**那颗胶囊占的那块矩形（空 = 这一帧没有胶囊）。
    ///
    /// 现在只有棱柱第 2 笔吸到「直棱柱」这一种（见 `Engine.StepSnap`）。
    /// 和 <see cref="VertexPillRect"/> 同一套规矩：**绘制与脏区都走它**，
    /// 锚点取"吸完之后那个顶面中心"（`Engine.StepSnapAnchor`）——不能这里另算一份，
    /// 两边差一帧的话屏幕上就会留一条擦不掉的边。
    /// </summary>
    private RectF StepSnapPillRect(InkEngine app)
    {
        if (app.StepSnap == ShapeSnapKind.None) return RectF.Empty;
        string text = SelectionHandles.ShapeSnapLabel(app.StepSnap);
        if (string.IsNullOrEmpty(text)) return RectF.Empty;
        return InclinationReadoutRect(app.StepSnapAnchor, app.DpiScale, text);
    }

}
