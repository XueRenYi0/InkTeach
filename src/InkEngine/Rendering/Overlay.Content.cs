// [职责] 内容层合成：分块光栅、相机变换、缩放合成、滚动条。
// [测试] --zoomtest

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
    //  Content layer
    // ------------------------------------------------------------------

    /// <summary>
    /// 文档页层：与这块画布相交的那些页。
    ///   · 已生成 → 画位图（顺带把 CPU 那半份放掉：页图不存档、不导出，重生成就是了）；
    ///   · 没生成 → 浅灰占位；失败 → 浅红占位；
    ///   · 一律描一条很淡的边（页缝的语言，和白板页界线同一族）。
    /// 页图**不参与笔迹的任何东西**（选中 / 橡皮 / 存档 / 导出 / 回放），这里只是"画出来"；
    /// 笔迹画在它上面（<see cref="RasterizeTile"/> 里这一句排在 QueryGrid 之前）。
    /// </summary>
    private void DrawDocPages(InkEngine app, in RectF canvas)
    {
        var pages = app.DocView;
        if (!pages.IsOpen) return;
        if (app.PassThrough) return;      // 穿透 = 全让开（S2）：页不画（位图也在 SyncDocPages 里放了）
        // 白板"不透明"盖上来（"另一张纸"）：页让位、板色盖满。
        // ⚠ 板**半透明**时不藏页——要"透过上面那张纸看到 PDF"（2026-10-09 用户问；见 RasterizeTile）。
        if (app.BoardOn && app.BoardOpacity >= 0.999f) return;

        // 白板半透明时，文档页只画**打开白板那一刻**冻结的那一页；页矩形按
        // (冻结时的文档相机 − 当前白板相机) 平移，所以在屏幕上位置不动。
        bool frozen = app.BoardFrozenDocActive;
        float shiftY = frozen ? app.BoardFrozenDocViewOffsetY - ViewOffsetY : 0f;
        int start = frozen ? app.BoardFrozenDocPage : pages.FirstAtOrAfter(canvas.MinY);
        int end = frozen ? start + 1 : pages.Count;
        if (start < 0 || start >= pages.Count) return;

        ID2D1SolidColorBrush placeholder = null, failed = null, border = null;
        for (int i = start; i < end; i++)
        {
            var p = pages.At(i);
            if (!frozen && p.Rect.MinY > canvas.MaxY) break;
            var r = frozen
                ? new RectF
                {
                    MinX = p.Rect.MinX, MaxX = p.Rect.MaxX,
                    MinY = p.Rect.MinY + shiftY, MaxY = p.Rect.MaxY + shiftY,
                }
                : p.Rect;
            if (!r.Intersects(canvas)) continue;

            if (p.Image != null)
            {
                var bmp = p.Image.GetBitmap(_ctx);
                if (bmp != null)
                {
                    _ctx.DrawBitmap(bmp,
                        new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
                        1f, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
                    p.Image.DropCpuBytes();
                }
                else
                {
                    placeholder ??= Brush(new Color4(0.94f, 0.94f, 0.94f, 1f));
                    _ctx.FillRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
                                       placeholder);
                }
            }
            else if (p.Failed)
            {
                failed ??= Brush(new Color4(0.99f, 0.90f, 0.90f, 1f));
                _ctx.FillRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), failed);
            }
            else
            {
                placeholder ??= Brush(new Color4(0.94f, 0.94f, 0.94f, 1f));
                _ctx.FillRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
                                   placeholder);
            }

            border ??= Brush(new Color4(0f, 0f, 0f, 0.22f));
            _ctx.DrawRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
                               border, 1f);
        }
    }

    /// <summary>
    /// 文档页层同步（每帧渲染前调一次）：
    ///   ① 按视口窗口生成/回收页位图（生成预算 = **一帧一页**——笔优先，10-03 文档 7.5）；
    ///   ② 页层报的脏区 → 分块脏（下次光栅）+ 上屏脏（Present1 的矩形）。
    /// "整层变了"（打开/关闭文档）走 MarkAllDirty + 两帧整屏——和换底色同一套。
    ///
    /// **这一步与笔迹完全无关**：页位图生成不碰 `Doc`，`Doc` 的变化也不碰这里；
    /// 两边各走各的版本号，靠"脏区"在分块缓存里会合。
    /// </summary>
    private void SyncDocPages(InkEngine app)
    {
        _pageDirtyNow.Clear();

        var pages = app.DocView;

        // 穿透 = **全让开**（S2，用户 2026-10-07 定）：页不画、不生成、位图全放（省内存）。
        // **白板且不透明**也走同一条链（"两张纸"上面那张盖住：页让位、位图全放）。
        // ⚠ 板**半透明**时不藏：要透过它看 PDF——这时页位图保持驻留
        //    （内存只在"文档开着＋板半透明"的组合下多花；不透明/无板时照旧省）。
        // 状态翻转那一帧要"整层作废"——分块里烘着页像素，不重铺的话穿透了屏幕还留着卷子。
        bool hidden = app.PassThrough || (app.BoardOn && app.BoardOpacity >= 0.999f);
        if (hidden != _docHidden)
        {
            _docHidden = hidden;
            _tiles.MarkAllDirty();
            _fullFramesLeft = 2;
        }

        if (pages.IsOpen)
        {
            // 白板打开后冻结页要一直留着（即使用户先把透明度拉满、或临时进穿透）：
            // 否则再回来时页图已被 ReleaseAll 放掉，只剩占位。
            bool keepFrozen = app.BoardFrozenDocPage >= 0;
            if (hidden && keepFrozen) pages.ReleaseAllExceptPinned();
            else if (hidden) { pages.ReleaseAll(); pages.StopWanting(); }
            else
            {
                int adopted = pages.SyncWindow(VisibleCanvasRect.MinY, VisibleCanvasRect.MaxY,
                                               1, DocMemory.CacheMarginScreens);
                if (adopted > 0)
                    Console.WriteLine($"[文档] 页缓存 {pages.ResidentPages} 页 / "
                        + $"{pages.ResidentBytes / 1048576.0:F1} MB");
            }
        }

        if (pages.FullDirty)
        {
            pages.FullDirty = false;
            _tiles.MarkAllDirty();
            _fullFramesLeft = 2;
        }
        if (pages.DirtyRects.Count > 0)
        {
            foreach (var r in pages.DirtyRects)
            {
                _tiles.MarkDirty(r);
                _pageDirtyNow.Add(CanvasRectToWindow(r));
            }
            pages.DirtyRects.Clear();
        }
    }

    /// <summary>穿透是否正在把文档页层藏着（上一帧的值；状态翻转要整层重铺）。</summary>
    private bool _docHidden;

    /// <summary>
    /// 把文档的最新状态同步进分块缓存，并把"这一帧要看的新块"光栅化出来。
    ///
    /// 三步，顺序固定：
    ///   ① 文档变了 → 把脏区碰到的块标脏（全是**画布坐标**，与相机无关）
    ///   ② 相机/底色变了 → 块内容一个像素都不用重画，只是合成位置或底变了
    ///   ③ 同步可见块：新露出来的（或脏的）就地光栅化，然后按预算淘汰旧块
    ///
    /// **滚动只走第 ② 步和第 ③ 步里的"没有新块"分支**，所以滚一格的代价
    /// 与文档总量无关——这是分块相对老做法的根本差别。
    /// </summary>
    private void SyncTiles(InkEngine app)
    {
        // 回放期间内容层渲染的是**影子文档**（已出完的笔画；见 InkEngine.RenderDoc）——
        // 这样分块缓存、多窗口、DPI、板色全部白拿，只有"正在长的那一条"走浮动层。
        var doc = app.RenderDoc;

        // 文档页层（图片 / PDF）先同步：按视口窗口生成/回收页位图，
        // 并把它报的脏区转成"分块脏 + 上屏脏"。页位图先就位，这一帧的光栅化才画得出来。
        SyncDocPages(app);

        if (_tilesVersion != doc.Version)
        {
            if (doc.Dirty.Full)
            {
                _tiles.MarkAllDirty();
                _fullFramesLeft = 2;
            }
            else if (doc.StructureChangedSinceRender)
            {
                // 结构变了（删 / 移 / 撤销 / 清空）：块里"只差几条新笔画"的前提没了，
                // 挂着的补画清单一律作废，改成整块重画。
                _tiles.FlushAppendsAsDirty();
                foreach (var r in doc.Dirty.Rects) _tiles.MarkDirty(r);
            }
            else
            {
                // 只增不减：交给"补画"路径——把新笔画记到它覆盖的块上，
                // 不清空整块。这是"同一页墨迹很多还在写"不卡的关键。
                // 注意：这时的脏区全部来自新笔画本身，所以不必再 MarkDirty。
                int missed = 0;
                foreach (var s in doc.AppendedSinceRender)
                    if (!_tiles.MarkAppend(s)) missed++;
                LastAppendMissed = missed;
            }
            _tilesVersion = doc.Version;
        }

        // 相机变了：**分块一律不动**，只是贴的位置变了。后缓冲里躺着的是
        // 滚动前的画面，整块都不可信，所以这一帧整屏重合成。
        if (ViewOffsetY != _lastCamY || ViewOffsetX != _lastCamX)
        {
            // 普通内容只换贴合位置；但白板冻结的 PDF 页是按"冻结相机 − 当前相机"
            // 补偿画进分块的，相机一动就必须重烘，否则页图会跟着白板一起滚走。
            if (app.BoardFrozenDocActive)
                _tiles.MarkAllDirty();
            _lastCamY = ViewOffsetY;
            _lastCamX = ViewOffsetX;
            _fullFramesLeft = 2;      // 相机变了：两个缓冲都要重画到新位置（见字段说明）
        }

        // 缩放变了：分块纹理的倍率要跟着换（SetRenderScale 内部幂等——只有倍率真的
        // 变了才整层重烘，不会每帧清一遍）。后缓冲里躺的是上一倍率下的画面，两个都作废。
        if (MathF.Abs(ViewScaleSafe - _lastViewScale) > 0.0005f)
        {
            _lastViewScale = ViewScaleSafe;
            _tiles.SetRenderScale(ViewScaleSafe);
            _fullFramesLeft = 2;
        }

        // 底色是**画进分块里**的（透明批注 = 擦成全透明；白板 = 板色；文档开着 = 文档垫白），
        // 所以换底色（含"文档开/关""穿透"导致的垫白进出）等于所有块都过期。
        bool paperNow = !app.BoardOn && app.DocView.IsOpen && !app.PassThrough;
        if (app.BoardOn != _lastBoardOn || !app.BoardColor.Equals(_lastBoardColor)
            || paperNow != _lastPaper)
        {
            _lastBoardOn = app.BoardOn;
            _lastBoardColor = app.BoardColor;
            _lastPaper = paperNow;
            _tiles.MarkAllDirty();
            _fullFramesLeft = 2;
        }

        _tiles.Sync(VisibleCanvasRect, RasterizeTile);

        if (Trace && (_tiles.RasterizedLastFrame > 0 || _tiles.AppendedLastFrame > 0))
            Console.WriteLine($"    [tiles] raster={_tiles.RasterizedLastFrame} append={_tiles.AppendedLastFrame} "
                              + $"camY={ViewOffsetY:F0}");

        RebuildCount += _tiles.RasterizedLastFrame;
        LastRebuildMs = _tiles.RasterMsLastFrame;
        LastAppendedTiles = _tiles.AppendedLastFrame;
        TotalAppendedTiles += _tiles.AppendedLastFrame;
        LastAppendMs = _tiles.AppendedMsLastFrame;
        LastPatchMs = _tiles.RasterMsLastFrame;
        LastPatchCount = _tiles.RasterizedLastFrame;
        LastDrawnStrokes = _tiles.StrokesLastFrame;
        LastTileCount = _tiles.Count;
        LastTileVisible = _tiles.VisibleCount;
        LastTileBudget = _tiles.BudgetNow;      // 可见 + 回滚余量 + 预取环（见 CanvasTileCache.BudgetNow）
        LastTileBytes = _tiles.TileBytes;       // 每块纹理字节（随倍率变；诊断用，不影响分块行为）
        LastTileTextureSize = _tiles.TextureSize;
        LastTileRenderScale = _tiles.RenderScale;
    }

    /// <summary>视口外一圈还有没有欠着的块（引擎据此排空闲预取拍）。</summary>
    public bool PrefetchNeeded() => _tiles.PrefetchNeeded(VisibleCanvasRect);

    /// <summary>
    /// 空闲预取一拍：只烘"视口外一圈"里欠着的块，**不合成、不上屏**（上游 Xournal++/Rnote 的
    /// 预渲染思路）。返回 true = 环里还有活，引擎再排下一拍。
    /// </summary>
    public bool PrefetchStep(InkEngine app)
    {
        _app = app;
        bool more = _tiles.Prefetch(VisibleCanvasRect, RasterizeTile);
        LastPrefetch = _tiles.LastPrefetch;
        LastPrefetchMs = _tiles.PrefetchMsLastFrame;
        LastPrefetchPending = more;
        if (Trace && _tiles.LastPrefetch > 0)
            Console.WriteLine($"    [prefetch] {_tiles.LastPrefetch} 块 {_tiles.PrefetchMsLastFrame:F2}ms "
                              + $"pending={more} camY={ViewOffsetY:F0}");
        return more;
    }

    /// <summary>
    /// 光栅化一块：把与这块画布矩形相交的笔画画进它的纹理。
    ///
    /// **这一层唯一的坐标换算就是"减块原点"**，相机不参与——相机只出现在
    /// <see cref="CompositeTiles"/> 那一步。老做法里"脏区、裁剪、快路径"
    /// 各要记得换算一次，漏一处就出残影；现在想漏也没地方漏。
    ///
    /// 块纹理正好是块的大小，所以画出界的部分会被渲染目标自己裁掉，
    /// 不需要额外的裁剪矩形（这也顺带避免了抗锯齿接缝）。
    /// </summary>
    private int RasterizeTile(ID2D1Bitmap1 target, RectF canvas, List<Stroke> onlyThese)
    {
        var app = _app;
        var doc = app.RenderDoc;      // 回放期间是影子文档（见 SyncTiles 的说明）

        _ctx.Target = target;
        _ctx.BeginDraw();
        // **先缩放、再减块原点**：块纹理是 (TileSize × RenderScale) 像素，
        // 画布 1 单位 = RenderScale 纹理像素；平移仍是整数画布单位，
        // 所以同一段几何在相邻块里的像素覆盖完全一致（接缝防线不变）。
        _ctx.Transform = Matrix3x2.CreateScale(_tiles.RenderScale)
                       * Matrix3x2.CreateTranslation(-canvas.MinX, -canvas.MinY);

        // 只补画新增的笔画：不清空、不遍历块内原有的笔画。
        // 内容层"只增不减"（正在写字）时才走这条路，见 SyncTiles 里的判断。
        if (onlyThese != null)
        {
            foreach (var s in onlyThese)
            {
                // 被摘出去的那一批（拖动预览）不画进内容层：它们此刻由浮动层画，
                // 位置是每帧都在变的实时变换。见 InkEngine.DetachForDrag。
                if (app.IsContentDetached(s)) continue;
                DrawStroke(s);
            }
            _ctx.Transform = Matrix3x2.Identity;
            var hrAppend = _ctx.EndDraw();
            _ctx.Target = null;
            if (hrAppend.Failure) LastError = "tile append EndDraw: " + hrAppend.Description;
            return onlyThese.Count;
        }

        // Clear() 无视裁剪，用 Copy 混合的填充来"擦"这一块（老规矩）。
        //
        // **矩形要写画布坐标，不能写 (0,0,TileSize,TileSize)**：当前的变换是
        // "画布 → 块内"，直接给块内坐标会被再减一次块原点，整块擦到画面外去，
        // 结果就是"内容确实重画了，但旧墨没被擦掉"——擦除后屏幕上留着鬼影。
        // （实测踩过：橡皮擦掉了数据，屏幕上三条线还在。）
        //
        // "两张纸"的底（2026-10-09）：**半透明白板盖文档**时，底要先铺"文档的纸"、
        // 页图再画、最后才把板色（带透明度）盖上去——透出来的是 **PDF**，不是桌面。
        bool boardOverDocTranslucent =
            app.BoardOn && app.DocView.IsOpen && app.BoardOpacity < 0.999f;

        var clearRect = new Vortice.RawRectF(
            canvas.MinX, canvas.MinY, canvas.MaxX, canvas.MaxY);
        _ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        _ctx.FillRectangle(clearRect,
            boardOverDocTranslucent ? Brush(app.DocPaperColor) : BoardBrush(app));
        _ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;

        // 白板模式：画**底纹**（方格/横线）。页界线的一屏一页逻辑仍保留在
        // PageHeightCanvas / 翻页 / 页码里，但**视觉上不再绘制**——它和底纹
        // 混在一起会让老师写字时分心；这里只去掉“看得见的那条线”。
        if (app.BoardOn && !boardOverDocTranslucent)
        {
            DrawBoardPattern(app, canvas);
        }

        // 文档页层：**在底纹之上、笔迹之下**。页图是内容层的一部分，
        // 所以它跟着分块缓存走——滚动、写字都不重画它。
        DrawDocPages(app, canvas);

        // 半透明盖文档：**板色（带透明度）盖在页图上**——"透过上面那张纸看到下面那张"。
        if (boardOverDocTranslucent)
        {
            _ctx.FillRectangle(clearRect, BoardBrush(app));
            DrawBoardPattern(app, canvas);
        }

        // 空间索引按**带笔宽外扩**的框返回候选，所以跨在块边界上的粗笔画
        // 两边都会被画到，不会出现"贴边被削掉一半"的缺口。
        //
        // **穿透 = 全让开（2026-10-17 用户定）：连墨迹也不画**——屏幕只剩下层程序
        // 和我们的面板，不再"各槽的墨一起浮着、分不清在哪一层"。退出穿透原样回来；
        // 翻转那一帧的"整层作废"走 SyncDocPages 的 `hidden`（它本来就含 PassThrough）。
        int drawn = 0;
        if (!app.PassThrough)
        {
            doc.QueryGrid(canvas, _strokeScratch);

            // **按文档列表的 z 序画**（2026-10-17 修"置顶/置底没用"）：网格查询返回的是
            // **格子顺序**，不是层级；而置顶/置底只改 `Doc.Strokes` 列表——不按列表重排的话，
            // 画面顺序根本不变（用户真机抓到：图片置底后仍压着墨迹）。
            // 缓存：列表一变（Version++）重建一次 下标表；之后每次查询只做 O(k log k) 排序。
            if (_zCacheVersion != doc.Version)
            {
                _zCache.Clear();
                var all = doc.Strokes;
                for (int i = 0; i < all.Count; i++) _zCache[all[i]] = i;
                _zCacheVersion = doc.Version;
            }
            _strokeScratch.Sort((a, b) =>
            {
                int ia = _zCache.TryGetValue(a, out int va) ? va : 0;
                int ib = _zCache.TryGetValue(b, out int vb) ? vb : 0;
                return ia.CompareTo(ib);
            });

            foreach (var s in _strokeScratch)
            {
                if (!s.PaddedBounds.Intersects(canvas)) continue;
                if (app.IsContentDetached(s)) continue;      // 同上：拖动预览那一批不进内容层
                DrawStroke(s);
                drawn++;
            }
        }

        _ctx.Transform = Matrix3x2.Identity;
        var hr = _ctx.EndDraw();
        _ctx.Target = null;

        if (hr.Failure) LastError = "tile EndDraw: " + hr.Description;
        return drawn;
    }

    /// <summary>
    /// 把可见分块贴到后缓冲上这一块区域里。
    ///
    /// **相机在整个内容层里只出现在这里**：窗口矩形 → 画布矩形，取每块相交的
    /// 子矩形，贴回窗口上对应的位置。块在画布空间里待着不动，所以"滚动"在
    /// 这里是一堆**子矩形拷贝**——和 Win32 的 ScrollWindowEx、浏览器合成器
    /// 滚动图层是同一个手法：搬的是已经画好的像素，不是重新画。
    ///
    /// 必须是子矩形而不是整块：写字时脏区只有一小块，整块贴就等于每帧多搬
    /// 几十万像素。
    /// </summary>
    private void CompositeTiles(in RectF windowRect)
    {
        // 窗口 → 画布（CanvasToWindow 的逆）：c = (w + Origin − ViewOffset) / ViewScale
        var canvasRect = new RectF
        {
            MinX = WindowToCanvasX(windowRect.MinX),
            MinY = WindowToCanvasY(windowRect.MinY),
            MaxX = WindowToCanvasX(windowRect.MaxX),
            MaxY = WindowToCanvasY(windowRect.MaxY),
        };

        // 块纹理 = 画布 × RenderScale；src 必须写在**纹理像素**里。
        float tex = _tiles.RenderScale;
        bool oneToOne = MathF.Abs(tex - ViewScaleSafe) < 0.0005f;
        // 纹理像素 = 屏幕像素时用 NearestNeighbor：线性过滤会在块边界上把邻居的
        // 像素混进来（半透明笔迹会糊出淡边）。只有页位图被内存档位压低、和相机
        // 倍率不一致时，才交给 Linear 做那一次放大。
        var interp = oneToOne
            ? Vortice.Direct2D1.InterpolationMode.NearestNeighbor
            : Vortice.Direct2D1.InterpolationMode.Linear;

        foreach (var tile in _tiles.Visible)
        {
            var tr = CanvasTileCache.RectOf(tile.Tx, tile.Ty);

            float l = MathF.Max(tr.MinX, canvasRect.MinX);
            float t = MathF.Max(tr.MinY, canvasRect.MinY);
            float r = MathF.Min(tr.MaxX, canvasRect.MaxX);
            float b = MathF.Min(tr.MaxY, canvasRect.MaxY);
            if (r <= l || b <= t) continue;

            // src = (画布子矩形 − 块原点) × RenderScale（纹理像素）；
            // dst 由 CanvasToWindow 换算：缩放时同一块在窗口上更大，由 DrawBitmap 拉伸。
            var src = new Vortice.RawRectF((l - tr.MinX) * tex, (t - tr.MinY) * tex,
                                           (r - tr.MinX) * tex, (b - tr.MinY) * tex);
            var dst = new Vortice.RawRectF(
                CanvasToWindowX(l), CanvasToWindowY(t),
                CanvasToWindowX(r), CanvasToWindowY(b));

            _ctx.DrawBitmap(tile.Source, dst, 1f, interp, src, null);
        }
    }

    /// <summary>
    /// 画布矩形 → 窗口矩形。
    ///
    /// **凡是来自文档的矩形（脏区、笔画包围盒）在送进 ClipToWindow /
    /// PushAxisAlignedClip 之前都必须过这一步**——那些函数吃的是窗口坐标。
    /// 相机为 0 时两者恰好相等，一滚动就全错，症状是"滚完写不了字"
    /// 或者"写了看不见"。
    ///
    /// 这个坑踩过两次：第一次修的是"整层重建"那条路（RebuildAll），
    /// 但真正写字走的是"追加一笔"的快路径（DrawOnlyPatch）——没修到。
    /// </summary>
    private RectF CanvasRectToWindow(in RectF r) => new()
    {
        MinX = CanvasToWindowX(r.MinX), MinY = CanvasToWindowY(r.MinY),
        MaxX = CanvasToWindowX(r.MaxX), MaxY = CanvasToWindowY(r.MaxY),
    };
    private RectF ClipToWindow(RectF r)
    {
        float l = MathF.Max(r.MinX, OriginX);
        float t = MathF.Max(r.MinY, OriginY);
        float rr = MathF.Min(r.MaxX, OriginX + Width);
        float b = MathF.Min(r.MaxY, OriginY + Height);
        if (rr < l || b < t) return RectF.Empty;
        return new RectF { MinX = l, MinY = t, MaxX = rr, MaxY = b };
    }

    /// <summary>
    /// 旋转度数标签的矩形（**画布坐标**）。抽成独立函数，是因为有两处必须
    /// 用**同一套几何**：画它，以及把它算进每帧脏区。少算一处，屏幕上就会
    /// 留一块擦不掉的残影（选中框那一套 UI 已经踩过这个坑）。
    ///
    /// 位置贴在旋转手柄外侧（跟着手柄转，和 Figma 一样），并且夹在当前可见
    /// 画布范围内——选区贴到屏幕边上的时候，标签不会跑到屏幕外面去。
    /// </summary>
    /// <summary>
    /// 度数标签的盒子**宽度跟着文案走**：角度不设上限之后会出现 "-1234°" 这种长数字，
    /// 写死 64 逻辑像素会把字裁掉（D2D 画在固定矩形里，超出部分直接不见）。
    /// 用和绘制同一个文字格式量一遍宽度——**量完要留着那点余量**，
    /// 所以宽度 = 文字宽 + 两侧内边距。
    /// </summary>
    private RectF RotationReadoutRect(in SelectionFrame frame, float dpi, string text)
    {
        const float minWidthLogical = 64f, heightLogical = 30f;
        float widthLogical = minWidthLogical;
        if (!string.IsNullOrEmpty(text))
        {
            // 量一次就够了：标签只在拖动中出现，每帧一次测量对帧率没有影响
            // （比"按字数估宽"可靠——中英文、正负号、度数符号宽度都不一样）。
            float textW = MeasureTextWidth(text, ReadoutFormat(dpi));
            widthLogical = MathF.Max(minWidthLogical, textW / dpi + 24f);
        }
        var rot = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
        float w = widthLogical * dpi, h = heightLogical * dpi;
        float gap = (SelectionHandles.RotateGripLogical * 0.5f + 9f) * dpi;

        float cx = rot.X, cy = rot.Y - gap - h * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };

        var vis = VisibleCanvasRect;
        float pad = 4f * dpi;
        if (r.MinX < vis.MinX + pad) { r.MaxX += vis.MinX + pad - r.MinX; r.MinX = vis.MinX + pad; }
        if (r.MaxX > vis.MaxX - pad) { r.MinX -= r.MaxX - (vis.MaxX - pad); r.MaxX = vis.MaxX - pad; }
        if (r.MinY < vis.MinY + pad) { r.MaxY += vis.MinY + pad - r.MinY; r.MinY = vis.MinY + pad; }
        if (r.MaxY > vis.MaxY - pad) { r.MinY -= r.MaxY - (vis.MaxY - pad); r.MaxY = vis.MaxY - pad; }
        return r;
    }

    /// <summary>
    /// 倾斜角标签的盒子：贴在**正在拖的那个端点**外侧（挂在它上方），按文案量宽，
    /// 最后夹进当前可见画布。
    ///
    /// 和旋转标签同一套做法（连"按文案量宽"和夹取都照抄），只有锚点不同：
    /// 旋转标签挂旋转柄，这个挂在**指针此刻所在的那个端点**上——拖到哪儿跟到哪儿，
    /// 眼睛不用在两个地方来回找。
    /// </summary>
    private RectF InclinationReadoutRect(Vector2 anchor, float dpi, string text)
        => ReadoutPillRect(anchor, dpi, text, 30f, 84f, ReadoutFormat(dpi), PillPlace.Above);

    /// <summary>读数胶囊贴在锚点的哪一侧。</summary>
    private enum PillPlace
    {
        /// <summary>锚点正上方（倾斜角 / 顶点读数那一套）。</summary>
        Above,
        /// <summary>锚点左边、竖直方向对齐（角标：贴在那个角的外侧）。</summary>
        LeftOf,
        /// <summary>锚点右边、竖直方向对齐（角标）。</summary>
        RightOf,
    }

    /// <summary>
    /// 一颗读数胶囊的盒子：**按文案量宽**、贴到锚点的一侧、再夹进当前可见画布。
    ///
    /// 全工程只有这一份"量宽 + 夹取"的实现（倾斜角 α、圆的 r/d、椭圆 a/b、旋转 Δ，
    /// 以及第③轮的那些角标都走它）：那边当年就是"量一次、画另一份"出的残影，
    /// 分开写迟早会在盒子的宽窄上再犯一次。
    /// </summary>
    private RectF ReadoutPillRect(Vector2 anchor, float dpi, string text,
                                  float heightLogical, float minWidthLogical,
                                  IDWriteTextFormat fmt, PillPlace place)
    {
        float widthLogical = minWidthLogical;
        if (!string.IsNullOrEmpty(text))
        {
            float textW = MeasureTextWidth(text, fmt);
            widthLogical = MathF.Max(minWidthLogical, textW / dpi + 24f);
        }

        float w = widthLogical * dpi, h = heightLogical * dpi;
        float gap = (SelectionHandles.VisualSizeLogical * 0.5f + 9f) * dpi;
        float cx, cy;
        switch (place)
        {
            case PillPlace.LeftOf: cx = anchor.X - gap - w * 0.5f; cy = anchor.Y; break;
            case PillPlace.RightOf: cx = anchor.X + gap + w * 0.5f; cy = anchor.Y; break;
            default: cx = anchor.X; cy = anchor.Y - gap - h * 0.5f; break;    // Above
        }
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };

        var vis = VisibleCanvasRect;
        float pad = 4f * dpi;
        if (r.MinX < vis.MinX + pad) { r.MaxX += vis.MinX + pad - r.MinX; r.MinX = vis.MinX + pad; }
        if (r.MaxX > vis.MaxX - pad) { r.MinX -= r.MaxX - (vis.MaxX - pad); r.MaxX = vis.MaxX - pad; }
        if (r.MinY < vis.MinY + pad) { r.MaxY += vis.MinY + pad - r.MinY; r.MinY = vis.MinY + pad; }
        if (r.MaxY > vis.MaxY - pad) { r.MinY -= r.MaxY - (vis.MaxY - pad); r.MaxY = vis.MaxY - pad; }
        return r;
    }

    /// <summary>
    /// 滚动条（样式 B：Win11 / Figma 那根细线）。
    ///
    /// 默认隐藏，滚动时浮现，**静止 3 秒淡出**（InkClass 实测 1.5 秒太快，
    /// 用户会找不到它）。画在浮动层上——它每帧都在变（淡出），进内容层就等于
    /// 每帧重画内容。
    ///
    /// 比例滑块靠"画布范围"（内容边界 ∪ 一屏，见 InkDocument.Extent）：
    /// 没有总高度就没有比例可算。画布只有一屏时干脆不画。
    ///
    /// 用 <c>_scratch</c> 而不是 Brush(颜色)：淡出时颜色每帧都在变，
    /// Brush() 是按颜色缓存的，会一帧往缓存里塞一个画刷。
    /// </summary>
    private void DrawScrollBar(InkEngine app)
    {
        if (!app.ScrollBarVisible) return;               // 2026-10-09 起停用（见 Engine.ScrollBarEnabled）
        if (!TryScrollBar(app, out var sb)) return;      // 画布只有一屏：没有滚动条

        // 悬停/拖动中：不淡出，而且加粗——"这根线能拖"靠它自己说，
        // 不靠换光标（手型按规范只能表示链接，四向箭头会被读成缩放）。
        bool hot = app.ScrollBarHover || app.ScrollBarDragging;
        float dpi = Dpi / 96f;
        double idle = (app.NowMs - app.ScrollBarActiveAtMs) / 1000.0;
        float alpha = hot ? 1f
                    : idle <= 3.0 ? 1f
                    : MathF.Max(0f, 1f - (float)((idle - 3.0) / 0.4));
        if (alpha <= 0.01f) return;

        float w = (hot ? 9f : 4f) * dpi;
        float x0 = sb.AxisX - w * 0.5f;
        float r = w * 0.5f;

        // 轨道：极淡，只用来"知道这儿有东西"，不抢视觉
        _scratch.Color = new Color4(0.50f, 0.52f, 0.55f, (hot ? 0.22f : 0.16f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.Top, x0 + w, sb.Bottom), r, r), _scratch);

        // 滑块：中灰带透明度，浅色深色背景上都立得住
        _scratch.Color = new Color4(0.47f, 0.49f, 0.53f, (hot ? 0.86f : 0.75f) * alpha);
        _ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x0, sb.ThumbTop, x0 + w, sb.ThumbTop + sb.ThumbLen), r, r),
            _scratch);
    }

    /// <summary>
    /// 滚动条的几何（**屏幕坐标**，和 OriginX/Width 同一套）。
    /// 画和拖动共用它——两处各算一份，改一处忘一处是这类代码的经典翻车点。
    /// 返回 false = 现在不该有滚动条（画布只有一屏）。
    /// </summary>
    internal bool TryScrollBar(InkEngine app, out ScrollBarLayout l)
    {
        l = default;
        float dpi = Dpi / 96f;
        var extent = app.CanvasExtent;
        float extentH = extent.MaxY - extent.MinY;
        if (extentH <= Height + 1f) return false;

        float top = OriginY + 40f * dpi;
        float bottom = OriginY + Height - 40f * dpi;
        float trackLen = bottom - top;
        if (trackLen < 40f * dpi) return false;

        // 滑块长度 = 一屏 / 画布总高；位置 = 视口顶在画布里的比例
        float thumbLen = MathF.Max(40f * dpi, trackLen * (Height / extentH));
        float maxTravel = trackLen - thumbLen;
        float viewTop = WindowToCanvasY(OriginY);       // 视口顶部对应的画布 Y
        float frac = extentH - Height > 1f
            ? (viewTop - extent.MinY) / (extentH - Height) : 0f;
        frac = Math.Clamp(frac, 0f, 1f);

        l = new ScrollBarLayout
        {
            AxisX = OriginX + Width - 8f * dpi,
            Top = top,
            Bottom = bottom,
            ThumbTop = top + maxTravel * frac,
            ThumbLen = thumbLen,
            MaxTravel = maxTravel,
            ExtentMinY = extent.MinY,
            ExtentH = extentH,
        };
        return true;
    }
}
