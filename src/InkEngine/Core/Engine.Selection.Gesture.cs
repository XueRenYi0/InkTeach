// [职责] 框选/套索/拖动/对齐吸附/美化入口。
// [测试] --seltest / --lassotest

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;


public partial class InkEngine
{
    /// <summary>被拖端点"走过的一小段"的脏矩形（两个端点位置取并集，再按半笔宽外扩）。</summary>
    private static RectF EndpointDirtyRect(Vector2 a, Vector2 b, float inflate)
    {
        var r = RectF.Empty;
        r.Add(a.X, a.Y);
        r.Add(b.X, b.Y);
        return r.Inflate(inflate);
    }

    /// <summary>
    /// 这一批对象在矩阵 <paramref name="m"/> 下占的画布范围（轴对齐）。
    ///
    /// 脏区必须是正矩形，而对象和选区框都可能是斜的，所以取四个角变换后的包围盒。
    /// 往外多留 2 逻辑像素：抗锯齿的边缘会跑出精确包围盒一点点，漏了就会在屏幕上
    /// 留一条发丝一样的残影。
    /// </summary>
    private RectF PreviewBounds(in Matrix3x2 m)
    {
        var r = RectF.Empty;
        foreach (var s in _dragTargets)
        {
            var b = s.PaddedBounds;
            if (b.IsEmpty) continue;
            if (m.IsIdentity) { r.Add(b); continue; }
            r.Add(TransformBounds(b, m));
        }
        return r.IsEmpty ? r : r.Inflate(2f * DpiScale);
    }

    /// <summary>把一个轴对齐矩形过一遍矩阵，取四个角变换后的新包围盒。</summary>
    private static RectF TransformBounds(in RectF b, in Matrix3x2 m)
    {
        if (b.IsEmpty) return RectF.Empty;
        if (m.IsIdentity) return b;
        var p0 = Vector2.Transform(new Vector2(b.MinX, b.MinY), m);
        var p1 = Vector2.Transform(new Vector2(b.MaxX, b.MinY), m);
        var p2 = Vector2.Transform(new Vector2(b.MaxX, b.MaxY), m);
        var p3 = Vector2.Transform(new Vector2(b.MinX, b.MaxY), m);
        var r = RectF.Empty;
        r.Add(p0.X, p0.Y); r.Add(p1.X, p1.Y);
        r.Add(p2.X, p2.Y); r.Add(p3.X, p3.Y);
        return r;
    }

    /// <summary>
    /// **这一帧该画的那个选中框**：拖动预览期间要用实时变换，不能再用模型里的。
    ///
    /// 为什么必须分开：方案 B 里模型到松手才动（那样内容层才能一帧都不重画），
    /// 于是 <see cref="SelectionHandles.FrameOf"/> 拿到的是**按下那一刻**的框 ——
    /// 直接拿它画，拖动中框和手柄会留在原地不动、内容和框分家（松手才"啪"地跳回来）。
    ///
    /// 算法和 <see cref="SelectionHandles.FrameOf"/> 是**同一条**：每个对象的**世界**
    /// 包围盒先过一遍实时矩阵，再并成轴对齐的框。所以一条和多条没有分支——
    /// 框永远正着，旋转中每帧重新贴合当前内容（会"呼吸"，这是"框永远正着"的代价，
    /// 见 SelectionFrame 的注释）。
    /// 不在拖动中就原样返回（绝大多数帧走这条，零开销）。
    /// </summary>
    internal SelectionFrame LiveSelectionFrame
    {
        get
        {
            // 拖端点 / 拖顶点：模型到松手才动，所以框必须按**临时几何**算——
            // 照模型算的话框会留在旧位置上，和屏幕上那条线当场分家。
            // （和下面"拖动预览用实时矩阵"是同一件事的两种形态：一个改几何、一个改变换。）
            //
            // 口径必须和静止态**一模一样**（2026-09-19）：走 PreviewInkBounds
            // （端点外接 / 圆的参数化外接 / 顶点外接 + 半笔宽），它和 WorldInkBounds 一张表。
            // 以前这里写死了 LineLikeInkBounds —— 那是**直线专用**的一条式子，
            // 圆/椭圆/三角形拖元素时框会退化成"两个控制点的外接"（圆的紧框一度算成 256×16）。
            // 另：这儿以前用的是 PaddedBoundsOf —— 那是**脏区**的口径（墨迹 + 2 像素），
            // 于是拖动中每边比对象多 2 像素，松手那一瞬框会缩一下。
            if (_vertexDragging && _vertexTarget != null && _vertexPreviewLocal != null)
            {
                var pv = _vertexPreviewLocal;
                var rv = _vertexTarget.PreviewInkBounds(pv, Matrix3x2.Identity);
                return new SelectionFrame { Local = rv, ToCanvas = Matrix3x2.Identity };
            }

            var f = SelectionHandles.FrameOf(Doc.Selected);
            if (!DragPreviewActive || f.IsEmpty) return f;
            var r = RectF.Empty;
            foreach (var s in Doc.Selected)
            {
                // 锁定的对象**不跟着动**（用户 2026-09-16 定的 B 语义），
                // 所以它们那块按"当前位置"算——不然框会跟着它们一起漂。
                if (s.Locked) { r.Add(s.WorldInkBounds); continue; }
                // 直线 / 箭头要按**端点**过（对象变换 × 实时矩阵）算，不能拿"当前墨迹框"再乘矩阵：
                // 后者等于"把一个矩形整体转过去再取外接"，一条转 30° 的直线框会撑到接近两倍宽
                // （2026-09-18 实测：962 vs 线自己 490）。
                r.Add(s.IsLineLike
                    ? s.LineLikeInkBounds(_selDragMatrix)
                    : TransformBounds(s.WorldInkBounds, _selDragMatrix));
            }
            return new SelectionFrame { Local = r, ToCanvas = Matrix3x2.Identity };
        }
    }

    /// <summary>
    /// **画完自动选中那个框**（见 <see cref="EndStroke"/>）：指针落在哪一档。
    ///
    /// **和框选工具下那一套完全一样**，只有"按到框外"这一种情况不同：
    ///   · <see cref="AutoSelZone.Furniture"/> ＝ 操作条那一块（圆钮或整条）/ 面板 /
    ///     手柄 / 旋转柄——交给 <see cref="TryBeginSelectionGesture"/> 那一套分流；
    ///   · <see cref="AutoSelZone.Grab"/> ＝ **框内任意一点**（含图形自己那条墨）
    ///     → 整体拖动，和框选工具下同一条路（按住框里哪儿都能拖）；
    ///   · <see cref="AutoSelZone.None"/> ＝ 框**外**：收起这个框，**这一笔照常画**
    ///     ——用户 2026-09-22 的口径："点击了其他地方，这个选中框就取消"。
    ///
    /// 用户上手之后的原话是"**我现在是只要收缩，其他的都不变**"——所以这一档判据
    /// **不认识 `_autoSelCollapsed`**：收起 / 摊开只影响"条"画成圆钮还是一整条
    ///（见 <see cref="BarDrawnCollapsed"/>），拖动、拉手柄、点条这些行为一个字都不变。
    /// ⚠ 第一版把"框里"判成"接着画一笔"，结果就是用户报的那句
    /// "它好像不能拖动位置，只能拉伸缩放"——**框里那块地方必须能拖**。
    ///
    /// ⚠ **只有这一份判据**：按下时往哪条路走（`OnPointerDown` → <see cref="AutoSelectionPress"/>）
    /// 和光标形状（<see cref="ComputeCursorKind"/>）都问它，两处各写一遍的话就会出现
    /// "光标看着能拖、按下去却在画图"这种一半对一半错的状态
    ///（"同一个名单写在多处必漏一处"的教训见 架构-分层与规则.md 五-7）。
    /// </summary>
    private AutoSelZone AutoSelectionZoneAt(float x, float y)
    {
        // 家具尺度（屏幕恒定）：手柄 / 条 / 面板的大小与命中在缩放时不能跟着变。
        float dpi = FurnitureDpi;
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var aabb = frame.CanvasAabb;

        // ① 操作条那一块——**收起来就是那一颗圆钮，摊开了就是一整条 ＋ 面板**。
        //    范围和 SelectionCursor 里那几块**同一个**（"看得见的一块"和"点得到的一块"
        //    必须是同一个）。
        if (BarDrawnCollapsed)
        {
            if (SelectionDotCanvasRect.Contains(x, y))
                return AutoSelZone.Furniture;
        }
        else
        {
            if (SelectionBarCanvasRect.Contains(x, y))
                return AutoSelZone.Furniture;
            if (CustomColorOpen
                && SelectionHandles.PickContains(x, y, aabb, dpi, FurnitureViewport,
                                                 SelectionHandles.SwatchCount, SingleTextBoxSelection))
                return AutoSelZone.Furniture;
            if (SelPanelOpen != SelPanel.None
                && SelectionHandles.PanelContains(x, y, aabb, dpi, FurnitureViewport,
                                                  SelPanelOpen, SelectionHandles.SwatchCount,
                                                  SingleTextBoxSelection))
                return AutoSelZone.Furniture;
            // 字体下拉浮在面板右边：点它不能当成“拖画布/拖对象”。
            if (FontListOpen && FontListRectNow().Contains(x, y))
                return AutoSelZone.Furniture;
        }

        // ② 手柄（八个缩放柄 / 旋转柄 / 定义元素手柄）。**必须排在"整体拖动"那条前面**：
        //    直线的两个端点手柄正好压在框边上，先判框内的话那一按会被当成"拖整条"。
        if (SelectionHandles.HitTest(x, y, Doc.Selected, frame, dpi) != SelHandle.None)
            return AutoSelZone.Furniture;

        // ③ **框内** → 整体拖动（`TryBeginSelectionGesture` 里第 3 / 4 步那一套：
        //    框里空白处是"拖动"、按在某条墨上是"收窄成只选它"）。
        //    判据就是框选工具下那一条（`frame.ToLocalPoint` 落在 `frame.Local` 里），
        //    不另写一份"离轮廓多远算按上了"。
        if (SelectionHandles.InsideUiFrame(frame, new Vector2(x, y), dpi))
            return AutoSelZone.Grab;

        // ④ 框**外**：不算动它——收起这个框，这一笔照常画。
        return AutoSelZone.None;
    }

    /// <summary>图形工具下、那个自动出现的框：这一次按下算不算"在动它"。</summary>
    private bool AutoSelectionPress(float x, float y, bool shift, bool alt)
        => AutoSelectionZoneAt(x, y) != AutoSelZone.None
           && TryBeginSelectionGesture(x, y, shift, alt);

    /// <summary>
    /// 此刻指针所在这一带，"选中框那一套"（手柄 / 框内拖动）是不是可用。
    ///
    /// 框选工具下**整个框**都可用；图形工具下按 <see cref="AutoSelectionZoneAt"/> 那两档走。
    /// 这一条也**只有这一份**——光标都问它。
    /// </summary>
    private bool SelectionInteractiveAt(float canvasX, float canvasY)
        => Doc.Selected.Count > 0
           && !_textBoxEditing
           && (Tool == Tool.Marquee
               // 文本工具下，**已选中的文字框**也要给"移动十字"：未选中的文字仍是 I 字，
               // 选中之后拖它走、双击编辑（2026-11-01 用户照 ClassIn 定的）。
               || (Tool == Tool.Text && SingleTextBoxSelection)
               || ((IsShapeTool(Tool) || _dwellSelected || _touchSelected)
                   && AutoSelectionZoneAt(canvasX, canvasY) != AutoSelZone.None));

    /// <summary>
    /// **操作条那一块（整条 / 圆钮 / 它下面挂的面板）画不画、点不点**。
    ///
    /// 框选工具下当然有；**图形工具下也有**——画完自动选中那个框会挂一**颗圆钮**
    /// （见 <see cref="_autoSelCollapsed"/>）：不画的话老师不知道该去哪儿把它摊开，
    /// 而那只是一颗 26 逻辑像素的圆钮，不像整条九格那样会把"接着画"的地方占掉。
    /// 摊开之后行为**一个字都不变**（只是"条"长得完整了）。
    ///
    /// ⚠ 这是**唯一**判据：绘制（Overlay）/ 按下分流（TryBeginSelectionGesture）/ 悬停高亮
    /// （UpdateBarHover）/ 光标（SelectionCursor）四处都问它，任一处漏了都会变成
    /// "画了但点不到"或者反过来"点得到但看不见"。具体画整条还是画圆钮，问 `BarDrawnCollapsed`。
    /// </summary>
    internal bool SelectionBarShown
        => Doc.Selected.Count > 0
           && !_textBoxEditing
           && (Tool == Tool.Marquee || (Tool == Tool.Text && SingleTextBoxSelection)
               || IsShapeTool(Tool) || _dwellSelected || _touchSelected);

    /// <summary>图形工具下那个自动选中的框：指针落在"家具 / 算动它 / 都不是"哪一档。</summary>
    private enum AutoSelZone
    {
        /// <summary>都不算——按下去是接着画一笔。</summary>
        None,
        /// <summary>框的家具：圆钮 / 操作条 / 面板 / 手柄 / 旋转柄（交给选择手势那一套分流）。</summary>
        Furniture,
        /// <summary>框内任意一点（含图形自己那条墨）：拖它走。</summary>
        Grab,
    }

    /// <summary>
    /// 框选工具按下时的分流。返回 true 表示这次按下已经被选择手势接掉。
    ///
    /// 顺序有讲究（从 1 到 5，**不能换**）：
    ///   1. 操作条按钮——它和手柄、框都紧挨着，放后面就点不中；
    ///   2. 手柄（缩放 / 旋转）；
    ///   3. 在**当前选区框内** → 整体拖动；
    ///   4. **点选**：按在一条墨迹上就选中它（没有选中时也走这条）；
    ///   5. 都没有 → 返回 false，交给外面的框选。
    ///
    /// 第 3 步必须在第 4 步之前：否则"拖动已选中的一块"会被判成"点选其中一条"，
    /// 多选就再也搬不动了。（2026-09-15 实现点选时定的顺序。）
    /// </summary>
    /// <summary>按下参数滑杆：进入"拖参数"手势（一次拖动 = 一步撤销）。</summary>
    private bool TryBeginFigureParamDrag(float x, float y, in SelectionFrame frame)
    {
        if (Doc.Selected.Count != 1) return false;
        var s = Doc.Selected[0];
        if (s.Kind != StrokeKind.Figure || s.Figure == null || s.Locked) return false;
        var keys = FigureParamBar.Keys(s);
        if (keys.Count == 0) return false;
        float ui = FurnitureDpi;          // 参数滑杆也是悬浮家具：屏幕恒定
        var panel = FigureParamBar.PanelRect(frame.CanvasAabb, ui, FurnitureViewport, keys.Count);
        int row = FigureParamBar.RowAt(panel, keys.Count, x, y, ui);
        if (row < 0) return false;
        string key = keys[row];
        if (!FigureParamBar.TryGet(s, key, out double value)) return false;

        _figureParamDragging = true;
        _figureParamStroke = s;
        _figureParamKey = key;
        FigureParamBar.Range(value, out _figureParamMin, out _figureParamMax);
        _figureParamOldJson = PlotSpecJson.Write(s.Figure);
        SelDragging = true;
        _dragIsMove = false;
        _dragHandle = SelHandle.None;
        _selDragMoved = true;
        ApplyFigureParamAt(x);
        return true;
    }

    private void ApplyFigureParamAt(float x)
    {
        if (!_figureParamDragging || _figureParamStroke?.Figure == null) return;
        var keys = FigureParamBar.Keys(_figureParamStroke);
        int row = keys.IndexOf(_figureParamKey);
        if (row < 0) return;
        float ui = FurnitureDpi;
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var panel = FigureParamBar.PanelRect(frame.CanvasAabb, ui, FurnitureViewport, keys.Count);
        var track = FigureParamBar.TrackRect(FigureParamBar.RowRect(panel, row, ui), ui);
        float v = FigureParamBar.ValueAt(track, x, _figureParamMin, _figureParamMax);
        FigureParamBar.Set(_figureParamStroke, _figureParamKey, v);
        _figureParamStroke.InvalidateMetrics();
        Doc.Dirty.Add(_figureParamStroke.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
    }

    private void FinishFigureParamDrag()
    {
        var s = _figureParamStroke;
        if (s?.Figure != null)
        {
            string json = PlotSpecJson.Write(s.Figure);
            if (json != _figureParamOldJson)
            {
                var act = new SetFigureParamsAction(s, _figureParamOldJson, json);
                Doc.CommitInteractive(act);
            }
            Doc.Dirty.Add(s.PaddedBounds);
        }
        _figureParamDragging = false;
        _figureParamStroke = null;
        _figureParamKey = "";
        _figureParamOldJson = "";
        _dragIsMove = false;
        _dragHandle = SelHandle.None;
        SelDragging = false;
        _dirty = true;
    }

    private bool TryBeginSelectionGesture(float x, float y, bool shift, bool alt)
    {
        _dragHitStroke = null;
        ResetAlignSnapLatch();
        ClearAlignTargets();
        float dpi = DpiScale;      // 内容命中容差 / 粗细换算（"纸上的尺"，不跟界面档）
        float ui = FurnitureDpi;   // 选中家具（条 / 面板 / 手柄）的尺寸与命中：屏幕恒定

        bool move = false;
        var h = SelHandle.None;
        var frame = SelectionHandles.FrameOf(Doc.Selected);

        // ⓪.5 交互矢量图：参数滑杆优先于操作条/手柄（它浮在框上方）。
        if (TryBeginFigureParamDrag(x, y, frame)) return true;

        // —— ⓪ 收起态圆钮 / 浮动面板 / 操作条：优先级最高（它们盖在别的东西上面）——
        //
        // 顺序：**面板内部 → 操作条 → 面板外部**。为什么把"条"放在"面板外部"之前：
        // 面板开着时去点"层级"那一格，应该直接换成层级面板，而不是"先关掉再被这一格
        // 又打开"。而点面板和条以外的任何地方，才把面板收起来（并且这一次点击**继续**
        // 往下走，于是"点空白取消选中"的习惯不会因为面板开着就失灵）。
        //
        // ⚠ 整段由 `SelectionBarShown` 把守：只有"框选工具 / 图形工具下那个自动选中的框"
        // 才有点得到的那一块（见那里的注释）——不把守的话，操作条会隔空吃掉
        // "接着画下一个"的那一下。
        if (SelectionBarShown)
        {
            var aabb0 = LiveSelectionFrame.CanvasAabb;

            if (BarDrawnCollapsed)
            {
                // 收起态：只有一个圆钮，点它展开（框和手柄照旧，收起的只是"条"）。
                if (SelectionDotCanvasRect.Contains(x, y))
                {
                    SelBarCollapsed = false;
                    // **画完自动选中那个框**：点开这颗圆钮之后就成"常规那一套"了
                    //（整条操作条出来、框内任意一点都能拖）——用户 2026-09-22 定的就是这一步。
                    _autoSelCollapsed = false;
                    _dirty = true;
                    return true;
                }
            }
            // **自定义取色板优先**：它挂在墨迹面板旁边，可能压着别的控件，而且
            // "点它自己 = 不算点在外面"（点空白不取消，只有点卡片以外才取消）。
            else if (CustomColorOpen
                     && SelectionHandles.PickContains(x, y, aabb0, ui, FurnitureViewport,
                                                      SelectionHandles.SwatchCount,
                                                      SingleTextBoxSelection))
            {
                HandlePickClick(x, y);
                return true;                        // 色板上的点击一律吃掉（包括空白处）
            }
            // 字体下拉「更多…」：列表浮在面板右侧，优先于面板本身命中。
            // 按下只“武装”拖动；抬起时没拖动才应用那一行（鼠标/笔/触摸共用）。
            else if (FontListOpen
                     && !FontListRectNow().IsEmpty
                     && FontListRectNow().Contains(x, y))
            {
                return true;                        // 列表上的按下一律吃掉（含空白）
            }
            else if (SelPanelOpen != SelPanel.None
                     && SelectionHandles.PanelContains(x, y, aabb0, ui, FurnitureViewport,
                                                       SelPanelOpen, SelectionHandles.SwatchCount,
                                                       SingleTextBoxSelection))
            {
                HandlePanelClick(x, y);
                return true;                        // 面板上的点击一律吃掉（包括空白处）
            }
            else if (!SelectionMenuRectNow().IsEmpty
                     && SelectionMenuRectNow().Contains(x, y))
            {
                var menu = SelectionMenuRectNow();
                int row = SelectionHandles.BarMenuRowAt(x, y, menu, ui, SelectionMenuRowsNow);
                if (SelAiMenuOpen)
                {
                    // 选了行就收起弹层：以前 AI 菜单点完不关，投影上看着“没反应”，
                    // 再点一次又只会提示“正在作图”（2026-11-11 用户反馈）。
                    if (row >= 0) CloseSelectionMenu();
                    switch (AiMenuKindNow)
                    {
                        case AiMenuKind.Hand:
                            if (row == 0) RecognizeToTextBoxFromUi();
                            else if (row == 1) RecognizePlotFromUi();
                            break;
                        case AiMenuKind.Image:
                            if (row == 0) RecognizeImageCardFromUi();
                            else if (row == 1) RecognizePlotFromUi();
                            break;
                        default:
                            if (row == 0) RecognizePlotFromUi();
                            break;
                    }
                }
                else if (SelMoreMenuOpen)
                {
                    var order = SelectionHandles.MoreBarOrder(BarSurfaceNow);
                    if (row >= 0 && row < order.Length)
                    {
                        var action = order[row];
                        CloseSelectionMenu();
                        RunBarButton(action, frame, aabb0);
                    }
                }
                return true;
            }
            else
            {
                int btn0 = SelectionHandles.BarButtonAtIn(x, y, SelectionBarRect(aabb0), ui,
                    SelectionHandles.BarButtonCountFor(BarSurfaceNow));
                if (btn0 >= 0) { RunBarAction(btn0, frame, aabb0); return true; }
                if (SelPanelOpen != SelPanel.None || CustomColorOpen)
                {
                    CloseCustomColor();
                    SelPanelOpen = SelPanel.None;
                    _dirty = true;
                }
                CloseSelectionMenu();
            }
        }

        // —— 1~3：有选中时才谈得上（没选中就直接跳到第 4 步的点选）——
        if (Doc.Selected.Count > 0)
        {
            // 操作条定位用框的**画布轴对齐范围**：框本身可能是斜的，但"它占了屏幕上
            // 哪一块"永远是个正矩形，操作条贴在那个矩形的下面才对。
            var aabb = frame.CanvasAabb;

            // （操作条与面板已经在 ⓪ 里处理过了，这里只剩手柄 / 框内拖动 / 点选）
            h = SelectionHandles.HitTest(x, y, Doc.Selected, frame, ui);

            // 定义元素手柄（直线 / 箭头 / 圆 / 椭圆）：**两种语义分派**
            //   · **圆心 / 中心**（Anchor）：拖它 = 整体平移（改变换矩阵、几何一个点不动，
            //     半径 / 半轴因此逐位不变）。
            //   · **端点 / 圆周点 / 轴端点**：走"改几何"那条快路（手势期不碰模型、松手一步撤销）。
            // 锁定语义照旧：锁定的拖不动，落回"框内拖动"让下面那段锁定分支把它吃掉。
            bool anchorMove = false;
            // "按下的是不是定义元素手柄"——顶点那几格**走 IsVertexHandle 一条判据**，
            // 不在这里再列一遍名字（列名字的写法 2026-09-19 漏过一次 VertexD，
            // 症状是"拖单位长度点什么都没发生"，见 SelectionHandles.IsVertexHandle）。
            bool handleLike = h is SelHandle.EndpointA or SelHandle.EndpointB
                                 or SelHandle.Left or SelHandle.Right or SelHandle.Top or SelHandle.Bottom
                              || SelectionHandles.IsVertexHandle(h)
                              // 焦点三角形的顶点 P（2026-09-22）：**它也是一格定义元素手柄**
                              // ——少写这一格，屏幕上会画出一个"看得见、按不动"的圆点。
                              || h == SelHandle.FocusPoint;
            if (handleLike && SelectionHandles.ShapeEditable(Doc.Selected, out var shape))
            {
                var sh = SelectionHandles.HandleOf(shape, h);
                if (SelectionHandles.IsAnchorMove(shape, sh))
                {
                    anchorMove = true;
                    _dragHitStroke = shape;    // 松手若没动 → 收窄成"只选中它"（和点选同一条路）
                    h = SelHandle.None;
                }
                else if (sh != ShapeHandle.None && !shape.Locked)
                {
                    BeginVertexDrag(shape, sh, x, y);
                    return true;
                }
                // **别急着把 h 抹掉**：`Top / Bottom / Left / Right` 这四个名字是**两用**的——
                //   · 对椭圆 / 坐标系 / 数轴：它们是"定义元素"（走上面那条改几何的路）；
                //   · 对**没有定义元素手柄**的对象（矩形 / 墨迹 / 图像 / 四种曲线 / 四个立体图形）：
                //     它们就是**通用框的四边中点缩放柄**。
                // 原来这里无条件 `h = SelHandle.None`，于是那类对象"四角能拉、四边中点只能整体拖"
                //（用户 2026-09-20："那八个点出现了，但是我看着只有对角线能拖动放缩？
                //  我觉得左右拉伸也可以给"）。
                // 判据还是那一条：**它到底有没有定义元素手柄**——有，才说明这个 h 真的"没有"，
                // 该抹掉（"画都不画就别点得到"，见 HitTest 那段）；没有，就留给下面的通用框。
                else if (SelectionHandles.HasShapeHandles(shape))
                    h = SelHandle.None;
            }

            if (h == SelHandle.None)
            {
                if (anchorMove)
                {
                    // 圆心 / 中心：**直接算作整体拖动**，不走下面"指针底下有没有东西"那一关。
                    // 理由是几何事实：圆心离描边一整个半径那么远，而点选只认描边
                    // （见 HitObjectAt）——靠那一段的话"拖圆心"根本拖不动（2026-09-19 自检抓到）。
                    move = true;
                }
                else
                {
                    // 没点在手柄上：看指针是不是落在**操作框**里（整体拖动）。
                    // 小对象那个框撑到最小尺寸（见 SelectionHandles.UiBox），
                    // "看得见的那一圈"里面都能拖——判据和光标、自动选中框同一份。
                    move = SelectionHandles.InsideUiFrame(frame, new Vector2(x, y), ui);

                    // **框里有别的对象压在上面时，不能拖当前选区**：先让位给点选。
                    // 典型场景：文本框上面写了墨迹，当前文本框是选中的，老师点那条墨迹，
                    // 期望是"选中墨迹"，不是"把文本框拖走"。
                    if (move)
                    {
                        var under = Doc.HitObjectAt(x, y, ClickToleranceLogical * dpi);
                        // 2026-11-03：**只在"选中的是文本框 / 题卡"时让位给压在上面的墨迹**。
                        // 形状不能这么判——椭圆的中心、矩形的内部本来就是空的，
                        // 点在那儿仍然应该拖动整个形状（snaptest 里坐标轴的端点正好穿过
                        // 椭圆中心，旧写法会拒绝拖动，表现为"选框完全拖不动"）。
                        bool selectedTextBox = Doc.Selected.Count > 0;
                        foreach (var sel in Doc.Selected)
                            if (sel.Kind != StrokeKind.TextBox) { selectedTextBox = false; break; }
                        if (selectedTextBox && under != null && !Doc.Selected.Contains(under)) move = false;
                        else _dragHitStroke = under;
                    }
                }
            }
        }

        // —— 4：点选（2026-09-15 新增）——
        if (h == SelHandle.None && !move)
        {
            var hit = Doc.SelectAt(x, y, ClickToleranceLogical * dpi,
                                   additive: shift, subtractive: alt);
            if (hit == null) return false;                // 5：空白 → 交给框选

            _dragHitStroke = hit;
            _dirty = true;
            // 加选/减选时**不进拖动**：方便连着点几条攒出一个选择。
            if (shift || alt) return true;

            // 无修饰键：选中它并**直接进入整体拖动**（点住就能拖，和 PPT 一样）。
            frame = SelectionHandles.FrameOf(Doc.Selected);
            move = true;
        }

        // 文本框：8 手柄改的是"框的宽高"，不是缩放内容。走专用手势，
        // 不进下面的 Transform 缩放（否则字和公式会被拉变形）。
        if (h != SelHandle.None && Doc.Selected.Count == 1
            && Doc.Selected[0].Kind == StrokeKind.TextBox)
        {
            BeginTextBoxResize(Doc.Selected[0], h, x, y);
            return true;
        }

        _dragHandle = h;
        _dragIsMove = move;
        _dragFrame = frame;
        _dragStartPoint = new Vector2(x, y);
        _selDragMatrix = Matrix3x2.Identity;
        _selDragMoved = false;
        _dragTargets = Doc.Selected.ToArray();

        // 锁定（用户 2026-09-16 定的 B 语义）：**能选中，但拖不动**。
        // 做法是把锁定的从"这一次手势要动的那批"里摘掉——选区框照旧画（框算的是
        // Doc.Selected），只是它们不跟着动。全锁时提示一句、并且什么都不动。
        {
            int lockedCount = 0;
            foreach (var s in _dragTargets) if (s.Locked) lockedCount++;
            if (lockedCount > 0)
            {
                if (lockedCount == _dragTargets.Length)
                {
                    // 锁定的对象拖不动——这一句只在控制台留痕（屏幕反馈按用户要求去掉了提示条；
                    // 锁图标本身就是状态指示）。
                    Console.WriteLine($"拖动：这 {lockedCount} 个对象已锁定，拖不动");
                    SelDragging = true;                 // 吃掉这次按下：别退化成重新框选
                    _dragTargets = Array.Empty<Stroke>();
                    _dragStartXform = Array.Empty<Matrix3x2>();
                    _dragHandle = SelHandle.None;
                    _dragIsMove = false;
                    _dirty = true;
                    return true;
                }
                var movable = new List<Stroke>(_dragTargets.Length - lockedCount);
                foreach (var s in _dragTargets) if (!s.Locked) movable.Add(s);
                _dragTargets = movable.ToArray();
                Console.WriteLine($"拖动：{lockedCount} 个对象已锁定，不跟着动");
            }
        }

        _dragStartXform = new Matrix3x2[_dragTargets.Length];
        for (int i = 0; i < _dragTargets.Length; i++)
            _dragStartXform[i] = _dragTargets[i].Transform;

        // 对象吸附只服务“单选、整体拖动”；目标框在这里一次性收集。
        PrepareAlignTargets(move && _dragTargets.Length == 1 ? _dragTargets[0] : null);

        // 方案 B：把这一批从**内容层**摘出去，改由浮动层画实时预览。
        // 模型在整个手势里一个字都不改，所以内容层一帧都不用重画。
        DetachForDrag();

        // 旋转：累积角从 0 起，指针的"上一帧位置"就是按下这一点。
        // 必须在这里归零——上一次拖拽攒下来的角度绝不能带进这一次。
        _rotAccumDeg = 0f;
        _rotPrevPoint = frame.ToLocalPoint(_dragStartPoint);

        // 这一次旋转读 α 还是 Δ，**按下这一刻判定一次**（用户 2026-09-18 的四条之一）。
        // 判定复用"单选一条直线/箭头"那个现成判据（终点手柄用的是同一个），
        // 于是"何时给端点手柄、何时给 α 读数"永远是同一个答案——不会两处走岔。
        _rotLineLike = SelectionHandles.EndpointEditable(Doc.Selected, out var rotLine);
        _rotStartInclination = _rotLineLike
            ? SelectionHandles.InclinationDegrees(SelectionHandles.EndpointCanvasPosition(rotLine, 0),
                                                  SelectionHandles.EndpointCanvasPosition(rotLine, 1))
            : 0f;
        SelRotationInclination = _rotStartInclination;
        SelRotationReadsInclination = false;   // 松手前标签不显示，这个标志只在拖动中为真

        // 单选一个图形（矩形 / 椭圆 / 三角形 / 平行四边形）→ 读数走**姿态角**（规格 9.7）。
        // 圆不在内（转了看不出来），图像 / 笔迹改不进来（PoseEditable 只认那四种）。
        // 镜像状态也在这里记一次：手势里它不会变（乘上去的是纯旋转，行列式恒正）。
        _rotPoseLike = SelectionHandles.PoseEditable(Doc.Selected, out var rotPose);
        _rotMirrored = _rotPoseLike && SelectionHandles.IsMirrored(rotPose.Transform);
        _rotStartPose = _rotPoseLike ? SelectionHandles.PoseAngleDegrees(rotPose.Transform) : 0f;
        SelRotationPose = _rotStartPose;
        SelRotationReadsPose = false;          // 同上：只在拖动中为真

        SelDragging = true;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 自检用：直接走一次"选择手势分流"（操作条 / 手柄 / 框内拖动 / 点选都从这儿进）。
    /// 返回 false = 这次按下没被选择手势接掉（调用方会起框选）。
    ///
    /// 为什么把修饰键当参数：点选里的 Shift/Alt 分支要看真键盘，自检没法按着 Shift 跑，
    /// 于是"读键盘"这一步留在调用方（鼠标按下那处），判定函数保持纯逻辑。
    /// </summary>
    internal bool SelectionGestureForTest(float x, float y, bool shift = false, bool alt = false)
        => TryBeginSelectionGesture(x, y, shift, alt);

    /// <summary>自检用：结束一次选择手势（松手）。</summary>
    internal void EndSelectionGestureForTest() => EndSelDrag();

    /// <summary>
    /// 自检用：走一遍"指针移动"里**浮层拖动**那一段（粗细滑条 / 自定义取色板）。
    /// 真机上这段在 `OnPointerMove` 里排在工具 switch 之前（和工具无关）；
    /// 自检没法合成系统指针消息，所以把同一小段逻辑暴露出来（和上面那条同一个套路）。
    /// </summary>
    internal void PanelDragMoveForTest(float x, float y)
    {
        if (_sliderDragging) { DragWidthSliderTo(x); return; }
        if (_colorDragging) DragColorPickerTo(x, y);
    }

    /// <summary>自检用：结束浮层拖动（松手）——滑条提交、取色板按指针在不在板内决定应用/取消。</summary>
    internal void EndPanelDragForTest(float x, float y)
    {
        if (_sliderDragging) EndWidthSliderDrag();
        else if (_colorDragging) EndColorPickerDrag(x, y);
    }

    /// <summary>自检用：自定义取色板开着吗。</summary>
    internal bool CustomColorOpenForTest => CustomColorOpen;

    /// <summary>自检/出图用：按真实入口打开自定义取色板（起点色 = 选中墨迹/手里那支笔）。</summary>
    internal void OpenCustomColorForTest() => OpenCustomColor();

    private const float AlignSnapEnterLogical = 5f;
    private const float AlignSnapReleaseLogical = 8f;
    private const float AlignSnapProximityLogical = 24f;
    private const float AlignSnapPulseMs = 220f;

    /// <summary>清掉这一手势的“吸住了谁”（短亮不跟着清，它自己淡出）。</summary>
    private void ResetAlignSnapLatch()
    {
        _alignSnapLatched = false;
        _alignSnapTarget = null;
        _alignSnapTargetFrame = RectF.Empty;
        _alignSnapTargetCurveFrame = RectF.Empty;
        _alignSnapTargetWave = false;
        _alignSnapTargetCenterline = false;
        _alignSnapTargetAxisCenterOnlyX = false;
        _alignSnapTargetAxisCenterOnlyY = false;
        _alignSnapX = null;
        _alignSnapY = null;
    }

    private void ClearAlignTargets()
    {
        _alignTargets = null;
        _alignTargetFrames = null;
        _alignTargetCurveFrames = null;
        _alignTargetWaves = null;
        _alignTargetCenterlines = null;
        _alignTargetAxisCenterOnlyX = null;
        _alignTargetAxisCenterOnlyY = null;
        _alignSourceCenterline = false;
        _alignSourceWave = false;
        _alignSourceCurveFrame = RectF.Empty;
    }

    /// <summary>
    /// 手势开始时收集一次“图形 + 图片”的目标框。自由笔迹不参与，
    /// 所以几百个汉字的墨迹不会进入每帧的候选循环。
    /// </summary>
    private void PrepareAlignTargets(Stroke selected)
    {
        ClearAlignTargets();
        if (selected == null || !selected.IsShape) return;

        _alignSourceCenterline = IsAxisAlignedTransform(selected.Transform);
        _alignSourceWave = IsWaveLike(selected.Kind);
        _alignSourceCurveFrame = _alignSourceWave ? CurveSnapFrame(selected) : RectF.Empty;
        var targets = new List<Stroke>();
        var frames = new List<RectF>();
        var curveFrames = new List<RectF>();
        var waves = new List<bool>();
        var centerlines = new List<bool>();
        var centerOnlyX = new List<bool>();
        var centerOnlyY = new List<bool>();
        for (int i = 0; i < Doc.Strokes.Count; i++)
        {
            var s = Doc.Strokes[i];
            if (ReferenceEquals(s, selected) || !s.IsShape) continue;
            var b = s.WorldInkBounds;
            if (b.IsEmpty) continue;
            targets.Add(s);
            frames.Add(b);
            bool wave = IsWaveLike(s.Kind);
            waves.Add(wave);
            curveFrames.Add(wave ? CurveSnapFrame(s) : RectF.Empty);
            bool axisAligned = IsAxisAlignedTransform(s.Transform);
            centerlines.Add(axisAligned);

            // 轴类目标只把**真正的轴线**留给吸附。直线 / 箭头横着放时，
            // 它自己的笔宽会让 3×3 框多出“上沿 / 下沿”两条假轴线；数轴同理。
            // 只在这条轴与屏幕轴平行时启用：斜线 / 已经旋转过的图形仍走原来的
            // 3×3，避免把端点当成厚度。坐标系保留 3×3——它的外框（网格边界）
            // 本身也是用户可能要对齐的对象，不能一起砍掉。
            bool horizontal = axisAligned && IsHorizontalAxisTarget(s);
            bool vertical = axisAligned && IsVerticalAxisTarget(s);
            centerOnlyX.Add(axisAligned && s.IsLineLike && vertical);
            centerOnlyY.Add((axisAligned && s.Kind == StrokeKind.NumberLine)
                            || (axisAligned && s.IsLineLike && horizontal));
        }
        _alignTargets = targets.ToArray();
        _alignTargetFrames = frames.ToArray();
        _alignTargetCurveFrames = curveFrames.ToArray();
        _alignTargetWaves = waves.ToArray();
        _alignTargetCenterlines = centerlines.ToArray();
        _alignTargetAxisCenterOnlyX = centerOnlyX.ToArray();
        _alignTargetAxisCenterOnlyY = centerOnlyY.ToArray();
    }

    /// <summary>目标是一条水平直线 / 箭头（按画布里的两个端点判，容差 5%）。</summary>
    private static bool IsHorizontalAxisTarget(Stroke s)
    {
        if (!s.IsLineLike || s.Points.Count < 2) return false;
        var p0 = Vector2.Transform(new Vector2(s.Points[0].X, s.Points[0].Y), s.Transform);
        var p1 = Vector2.Transform(new Vector2(s.Points[^1].X, s.Points[^1].Y), s.Transform);
        float dx = MathF.Abs(p1.X - p0.X), dy = MathF.Abs(p1.Y - p0.Y);
        return dx > 1e-3f && dy <= MathF.Max(1f, dx * 0.05f);
    }

    /// <summary>目标是一条竖直直线 / 箭头（按画布里的两个端点判，容差 5%）。</summary>
    private static bool IsVerticalAxisTarget(Stroke s)
    {
        if (!s.IsLineLike || s.Points.Count < 2) return false;
        var p0 = Vector2.Transform(new Vector2(s.Points[0].X, s.Points[0].Y), s.Transform);
        var p1 = Vector2.Transform(new Vector2(s.Points[^1].X, s.Points[^1].Y), s.Transform);
        float dx = MathF.Abs(p1.X - p0.X), dy = MathF.Abs(p1.Y - p0.Y);
        return dy > 1e-3f && dx <= MathF.Max(1f, dy * 0.05f);
    }

    /// <summary>
    /// 正弦 / 余弦 / 波浪线是“有中心线的开放曲线”。两块这类图形拼接时，
    /// 要用**曲线中心线框**，不能用含笔宽的墨迹外框；否则外沿碰上了，中心线
    /// 还差一个笔宽（用户 2026-10-19 报的两个正弦接缝）。
    /// 这里只开这三类，闭合图形 / 图片仍走墨迹外框。
    /// </summary>
    private static bool IsWaveLike(StrokeKind kind)
        => kind is StrokeKind.Sine or StrokeKind.Cosine or StrokeKind.Wave;

    /// <summary>曲线中心线框（画布坐标，不含笔宽）。</summary>
    private static RectF CurveSnapFrame(Stroke s)
    {
        var b = s.CurveBoxLocal();
        if (b.IsEmpty) return b;
        var m = s.Transform;
        if (m.IsIdentity) return b;

        var r = RectF.Empty;
        void Add(Vector2 p) => r.Add(p.X, p.Y);
        Add(Vector2.Transform(new Vector2(b.MinX, b.MinY), m));
        Add(Vector2.Transform(new Vector2(b.MaxX, b.MinY), m));
        Add(Vector2.Transform(new Vector2(b.MaxX, b.MaxY), m));
        Add(Vector2.Transform(new Vector2(b.MinX, b.MaxY), m));
        return r;
    }

    /// <summary>中心线只对未旋转的图形有意义；旋转过的框不是图形自身的对称轴。</summary>
    private static bool IsAxisAlignedTransform(in Matrix3x2 m)
    {
        float s = MathF.Max(MathF.Abs(m.M11), MathF.Abs(m.M22));
        if (s < 1e-6f) return false;
        return MathF.Abs(m.M12) <= s * 1e-3f && MathF.Abs(m.M21) <= s * 1e-3f;
    }

    private static float AlignCenterX(in RectF r) => (r.MinX + r.MaxX) * 0.5f;
    private static float AlignCenterY(in RectF r) => (r.MinY + r.MaxY) * 0.5f;

    private static float AlignLineX(in RectF r, AlignLineRole role) => role switch
    {
        AlignLineRole.Start => r.MinX,
        AlignLineRole.Center => AlignCenterX(r),
        _ => r.MaxX,
    };

    private static float AlignLineY(in RectF r, AlignLineRole role) => role switch
    {
        AlignLineRole.Start => r.MinY,
        AlignLineRole.Center => AlignCenterY(r),
        _ => r.MaxY,
    };

    /// <summary>两个一维区间之间的空隙（重叠 = 0；分离时是正数）。</summary>
    private static float IntervalGap(float a0, float a1, float b0, float b1)
    {
        if (a1 < b0) return b0 - a1;
        if (b1 < a0) return a0 - b1;
        return 0f;
    }

    /// <summary>
    /// 软相交：两条竖线要比较时，上下范围必须重叠或相距不超过 24px；
    /// 两条横线要比较时，左右范围同理。避免屏幕两角也触发对齐。
    /// </summary>
    private static bool AlignNearX(in RectF a, in RectF b, float proximity)
        => IntervalGap(a.MinX, a.MaxX, b.MinX, b.MaxX) <= proximity;

    private static bool AlignNearY(in RectF a, in RectF b, float proximity)
        => IntervalGap(a.MinY, a.MaxY, b.MinY, b.MaxY) <= proximity;

    /// <summary>中心对中心优先级最高；同边其次；交叉边最后；距离永远优先。</summary>
    private static int AlignPriority(AlignLineRole source, AlignLineRole target)
        => source == AlignLineRole.Center && target == AlignLineRole.Center ? 0
         : source == target ? 1
         : 2;

    private static void ConsiderAlignCandidate(float delta, AlignLineRole sourceRole,
                                               AlignLineRole targetRole, float targetLine,
                                               float tolerance, ref AlignAxisCandidate? best)
    {
        if (MathF.Abs(delta) > tolerance) return;
        if (best.HasValue)
        {
            var old = best.Value;
            float oldD = MathF.Abs(old.Delta), newD = MathF.Abs(delta);
            if (newD > oldD + 1e-4f) return;
            if (MathF.Abs(newD - oldD) <= 1e-4f
                && AlignPriority(sourceRole, targetRole)
                   > AlignPriority(old.SourceRole, old.TargetRole)) return;
        }
        best = new AlignAxisCandidate
        {
            SourceRole = sourceRole,
            TargetRole = targetRole,
            Delta = delta,
            TargetLine = targetLine,
        };
    }

    /// <summary>
    /// 一根轴上的候选：源框三条线 × 目标框三条线（3×3），
    /// 但先用另一轴的软相交门槛把远处的框排掉。只保留最近的一个。
    /// </summary>
    private static AlignAxisCandidate? BestX(in RectF a, in RectF b, float tolerance,
                                             float proximity, bool allowCenterline,
                                             bool targetCenterOnly)
    {
        if (!AlignNearY(a, b, proximity)) return null;
        AlignAxisCandidate? best = null;
        for (int si = 0; si < 3; si++)
        {
            var sourceRole = (AlignLineRole)si;
            if (!allowCenterline && sourceRole == AlignLineRole.Center) continue;
            float sourceLine = AlignLineX(a, sourceRole);
            for (int ti = 0; ti < 3; ti++)
            {
                var targetRole = (AlignLineRole)ti;
                if (targetCenterOnly && targetRole != AlignLineRole.Center) continue;
                if (!allowCenterline && targetRole == AlignLineRole.Center) continue;
                float targetLine = AlignLineX(b, targetRole);
                ConsiderAlignCandidate(targetLine - sourceLine, sourceRole, targetRole,
                                       targetLine, tolerance, ref best);
            }
        }
        return best;
    }

    private static AlignAxisCandidate? BestY(in RectF a, in RectF b, float tolerance,
                                             float proximity, bool allowCenterline,
                                             bool targetCenterOnly)
    {
        if (!AlignNearX(a, b, proximity)) return null;
        AlignAxisCandidate? best = null;
        for (int si = 0; si < 3; si++)
        {
            var sourceRole = (AlignLineRole)si;
            if (!allowCenterline && sourceRole == AlignLineRole.Center) continue;
            float sourceLine = AlignLineY(a, sourceRole);
            for (int ti = 0; ti < 3; ti++)
            {
                var targetRole = (AlignLineRole)ti;
                if (targetCenterOnly && targetRole != AlignLineRole.Center) continue;
                if (!allowCenterline && targetRole == AlignLineRole.Center) continue;
                float targetLine = AlignLineY(b, targetRole);
                ConsiderAlignCandidate(targetLine - sourceLine, sourceRole, targetRole,
                                       targetLine, tolerance, ref best);
            }
        }
        return best;
    }

    /// <summary>按已经吸住的那一对线重算；吸住期间不换线。</summary>
    private static AlignAxisCandidate? CandidateForRoles(AlignLineRole sourceRole,
                                                        AlignLineRole targetRole,
                                                        bool xAxis,
                                                        in RectF a, in RectF b,
                                                        float tolerance, float proximity,
                                                        bool allowCenterline,
                                                        bool targetCenterOnly)
    {
        if (targetCenterOnly && targetRole != AlignLineRole.Center) return null;
        if (!allowCenterline
            && (sourceRole == AlignLineRole.Center || targetRole == AlignLineRole.Center))
            return null;
        if (xAxis)
        {
            if (!AlignNearY(a, b, proximity)) return null;
            float targetLine = AlignLineX(b, targetRole);
            float delta = targetLine - AlignLineX(a, sourceRole);
            if (MathF.Abs(delta) > tolerance) return null;
            return new AlignAxisCandidate
            {
                SourceRole = sourceRole, TargetRole = targetRole,
                Delta = delta, TargetLine = targetLine,
            };
        }
        else
        {
            if (!AlignNearX(a, b, proximity)) return null;
            float targetLine = AlignLineY(b, targetRole);
            float delta = targetLine - AlignLineY(a, sourceRole);
            if (MathF.Abs(delta) > tolerance) return null;
            return new AlignAxisCandidate
            {
                SourceRole = sourceRole, TargetRole = targetRole,
                Delta = delta, TargetLine = targetLine,
            };
        }
    }

    /// <summary>找最近的、在 5px 内有候选的目标框；一个手势只跟一个目标框。</summary>
    private bool TryFindAlignTarget(in RectF a, in RectF aCurve, float tolerance, float proximity,
                                    bool sourceCenterline,
                                    out Stroke target, out RectF targetFrame,
                                    out RectF targetCurveFrame, out bool targetWave,
                                    out bool targetCenterline,
                                    out bool targetCenterOnlyX, out bool targetCenterOnlyY,
                                    out AlignAxisCandidate? x, out AlignAxisCandidate? y)
    {
        target = null;
        targetFrame = RectF.Empty;
        targetCurveFrame = RectF.Empty;
        targetWave = false;
        targetCenterline = false;
        targetCenterOnlyX = false;
        targetCenterOnlyY = false;
        x = null;
        y = null;
        float bestScore = float.MaxValue;
        int bestPriority = int.MaxValue;

        for (int i = 0; i < _alignTargets.Length; i++)
        {
            bool useCurveFrame = _alignSourceWave && _alignTargetWaves[i] && !aCurve.IsEmpty
                                 && !_alignTargetCurveFrames[i].IsEmpty;
            var aa = useCurveFrame ? aCurve : a;
            var bb = useCurveFrame ? _alignTargetCurveFrames[i] : _alignTargetFrames[i];
            bool allowCenterline = sourceCenterline && _alignTargetCenterlines[i];
            bool centerOnlyX = allowCenterline && _alignTargetAxisCenterOnlyX[i];
            bool centerOnlyY = allowCenterline && _alignTargetAxisCenterOnlyY[i];
            var bx = BestX(aa, bb, tolerance, proximity, allowCenterline, centerOnlyX);
            var by = BestY(aa, bb, tolerance, proximity, allowCenterline, centerOnlyY);
            if (!bx.HasValue && !by.HasValue) continue;

            float sxc = bx.HasValue ? MathF.Abs(bx.Value.Delta) : float.MaxValue;
            float syc = by.HasValue ? MathF.Abs(by.Value.Delta) : float.MaxValue;
            float score = MathF.Min(sxc, syc);
            int prio = Math.Min(
                bx.HasValue ? AlignPriority(bx.Value.SourceRole, bx.Value.TargetRole) : int.MaxValue,
                by.HasValue ? AlignPriority(by.Value.SourceRole, by.Value.TargetRole) : int.MaxValue);

            if (score > bestScore + 1e-4f) continue;
            if (MathF.Abs(score - bestScore) <= 1e-4f && prio >= bestPriority) continue;

            bestScore = score;
            bestPriority = prio;
            target = _alignTargets[i];
            targetFrame = _alignTargetFrames[i];
            targetCurveFrame = _alignTargetCurveFrames[i];
            targetWave = useCurveFrame;
            targetCenterline = allowCenterline;
            targetCenterOnlyX = centerOnlyX;
            targetCenterOnlyY = centerOnlyY;
            x = bx;
            y = by;
        }
        return target != null;
    }

    private void BeginAlignPulse(AlignSnapKind kind, Vector2 point, float halfLen)
    {
        _alignPulseKind = kind;
        _alignPulsePoint = point;
        _alignPulseHalfLen = halfLen;
        _alignPulseStartMs = NowMs;
        _alignPulseEndMs = NowMs + AlignSnapPulseMs;
        _dirty = true;
    }

    /// <summary>吸附刚发生 / 某一轴刚吸住时，短亮一次；持续吸住不重复闪。</summary>
    private void MaybeBeginAlignPulse(in RectF a, bool hadX, bool hadY, Stroke oldTarget)
    {
        if (!_alignSnapX.HasValue && !_alignSnapY.HasValue) return;
        bool targetChanged = !ReferenceEquals(oldTarget, _alignSnapTarget);
        bool newX = !hadX && _alignSnapX.HasValue;
        bool newY = !hadY && _alignSnapY.HasValue;
        if (!targetChanged && !newX && !newY) return;

        float dpi = UiUnit;
        bool xMoved = _alignSnapX.HasValue && MathF.Abs(_alignSnapX.Value.Delta) > 0.01f;
        bool yMoved = _alignSnapY.HasValue && MathF.Abs(_alignSnapY.Value.Delta) > 0.01f;

        // 两个方向都真的动了（或本来就对齐）才画点；
        // 其中一个方向只是“本来就已经对齐”（delta≈0），就只画另一个方向的线，
        // 这样两个正弦周期拼接时看到的是竖缝，而不是一个笼统的圆点。
        if (_alignSnapX.HasValue && _alignSnapY.HasValue && xMoved && yMoved)
        {
            var p = new Vector2(_alignSnapX.Value.TargetLine, _alignSnapY.Value.TargetLine);
            BeginAlignPulse(AlignSnapKind.Point, p, 0f);
        }
        else if (xMoved)
        {
            var p = new Vector2(_alignSnapX.Value.TargetLine, AlignCenterY(a));
            BeginAlignPulse(AlignSnapKind.Vertical, p, 44f * dpi);
        }
        else if (yMoved)
        {
            var p = new Vector2(AlignCenterX(a), _alignSnapY.Value.TargetLine);
            BeginAlignPulse(AlignSnapKind.Horizontal, p, 44f * dpi);
        }
        else if (_alignSnapX.HasValue && _alignSnapY.HasValue)
        {
            var p = new Vector2(_alignSnapX.Value.TargetLine, _alignSnapY.Value.TargetLine);
            BeginAlignPulse(AlignSnapKind.Point, p, 0f);
        }
    }

    /// <summary>
    /// 整体移动矩阵：先按指针算纯平移，再在单选时叠一层选中框吸附。
    /// 只改平移量；模型 / 撤销仍然走原来的那一步（见 EndSelDrag）。
    /// </summary>
    private Matrix3x2 MoveMatrixWithAlignSnap(Vector2 cur, bool alt)
    {
        var raw = cur - _dragStartPoint;

        if (_dragTargets == null || _dragTargets.Length != 1 || alt
            || _alignTargets == null || _alignTargets.Length == 0)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }

        var s = _dragTargets[0];
        if (s == null || s.Locked || !s.IsShape)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }

        float dpi = DpiScale;
        float enterTol = AlignSnapEnterLogical * dpi;
        float releaseTol = AlignSnapReleaseLogical * dpi;
        float proximity = AlignSnapProximityLogical * dpi;
        bool movedEnough = Vector2.Distance(cur, _dragStartPoint) > ClickToleranceLogical * dpi;
        if (!movedEnough && !_alignSnapLatched)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }

        var aInk = s.WorldInkBounds;
        if (aInk.IsEmpty)
        {
            ResetAlignSnapLatch();
            return Matrix3x2.CreateTranslation(raw);
        }
        aInk.MinX += raw.X; aInk.MaxX += raw.X;
        aInk.MinY += raw.Y; aInk.MaxY += raw.Y;

        var aCurve = _alignSourceWave ? _alignSourceCurveFrame : RectF.Empty;
        if (!aCurve.IsEmpty)
        {
            aCurve.MinX += raw.X; aCurve.MaxX += raw.X;
            aCurve.MinY += raw.Y; aCurve.MaxY += raw.Y;
        }
        var a = aInk;

        var oldTarget = _alignSnapTarget;
        bool hadX = _alignSnapX.HasValue;
        bool hadY = _alignSnapY.HasValue;
        AlignAxisCandidate? x = null;
        AlignAxisCandidate? y = null;

        // 已吸住同一个目标：先按释放距离验证；目标被删掉也立刻松开。
        if (_alignSnapLatched)
        {
            bool alive = _alignSnapTarget != null && Doc.Strokes.Contains(_alignSnapTarget);
            if (alive)
            {
                // 吸住期间只沿原来那一对线走：不因为另一条线更近就突然换线。
                bool allowCenterline = _alignSourceCenterline && _alignSnapTargetCenterline;
                bool centerOnlyX = allowCenterline && _alignSnapTargetAxisCenterOnlyX;
                bool centerOnlyY = allowCenterline && _alignSnapTargetAxisCenterOnlyY;
                bool curvePair = _alignSnapTargetWave && _alignSourceWave && !aCurve.IsEmpty
                                 && !_alignSnapTargetCurveFrame.IsEmpty;
                var af = curvePair ? aCurve : aInk;
                var bf = curvePair ? _alignSnapTargetCurveFrame : _alignSnapTargetFrame;
                a = af;
                x = _alignSnapX.HasValue
                    ? CandidateForRoles(_alignSnapX.Value.SourceRole,
                                        _alignSnapX.Value.TargetRole,
                                        xAxis: true, af, bf,
                                        releaseTol, proximity, allowCenterline, centerOnlyX)
                    : null;
                y = _alignSnapY.HasValue
                    ? CandidateForRoles(_alignSnapY.Value.SourceRole,
                                        _alignSnapY.Value.TargetRole,
                                        xAxis: false, af, bf,
                                        releaseTol, proximity, allowCenterline, centerOnlyY)
                    : null;

                // 原来只有一条轴吸住时，另一条轴后来进入范围也可以补上。
                // 仍然只在同一个目标框上补，不换目标。
                if (!x.HasValue) x = BestX(af, bf, enterTol, proximity, allowCenterline, centerOnlyX);
                if (!y.HasValue) y = BestY(af, bf, enterTol, proximity, allowCenterline, centerOnlyY);
            }
            if (!alive || (!x.HasValue && !y.HasValue))
            {
                ResetAlignSnapLatch();
                x = null;
                y = null;
            }
            else
            {
                _alignSnapX = x;
                _alignSnapY = y;
            }
        }

        // 没吸住：找最近目标框（5px 内）。
        if (!_alignSnapLatched
            && TryFindAlignTarget(aInk, aCurve, enterTol, proximity, _alignSourceCenterline,
                                  out var target, out var targetFrame,
                                  out var targetCurveFrame, out var targetWave,
                                  out var targetCenterline,
                                  out var targetCenterOnlyX, out var targetCenterOnlyY,
                                  out x, out y))
        {
            _alignSnapTarget = target;
            _alignSnapTargetFrame = targetFrame;
            _alignSnapTargetCurveFrame = targetCurveFrame;
            _alignSnapTargetWave = targetWave;
            _alignSnapTargetCenterline = targetCenterline;
            _alignSnapTargetAxisCenterOnlyX = targetCenterOnlyX;
            _alignSnapTargetAxisCenterOnlyY = targetCenterOnlyY;
            _alignSnapX = x;
            _alignSnapY = y;
            _alignSnapLatched = true;
            if (targetWave && !aCurve.IsEmpty) a = aCurve;
        }

        if (!_alignSnapLatched) return Matrix3x2.CreateTranslation(raw);

        if (_alignSnapX.HasValue) raw.X += _alignSnapX.Value.Delta;
        if (_alignSnapY.HasValue) raw.Y += _alignSnapY.Value.Delta;

        MaybeBeginAlignPulse(a, hadX, hadY, oldTarget);
        return Matrix3x2.CreateTranslation(raw);
    }

    /// <summary>
    /// 拖动中：每帧都从**按下那一刻的变换**重算，而不是在上一帧结果上继续乘。
    /// 后者会累积浮点误差，拖得越久偏得越多，撤销也回不到原样。
    /// </summary>
    private void UpdateSelDrag(float x, float y)
    {
        // 参数滑杆：每帧只改一个参数并重采样，不碰变换。
        if (_figureParamDragging) { ApplyFigureParamAt(x); return; }

        // 文本框：8 手柄改宽高（不是 Transform 缩放）。
        if (_textBoxResizing) { UpdateTextBoxResize(x, y); return; }

        // 拖端点：另一条路（改几何，模型不动），和"拖对象"分开走。
        if (_vertexDragging) { UpdateVertexDrag(x, y); return; }

        // 缩放 / 旋转不参与对象吸附：只清掉上一次可能留下的“吸住”状态。
        if (!_dragIsMove) ResetAlignSnapLatch();

        var cur = new Vector2(x, y);
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        // 拖手柄的换算用**家具尺度**：手柄画在 UiBox（最小操作框）上，那个框的大小是屏幕恒定的，
        // 换算的锚点 / 最小尺寸下限必须和"画出来的那个框"同一把尺子，否则缩放下按下第一帧会跳。
        float furniture = FurnitureDpi;
        // Alt = 临时关掉吸附。Shift 是"硬网格 15°"，Alt 是"完全自由"，两个修饰键
        // 各管一头，中间那档（默认的 90° 软吸附）不用按键。
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;

        Matrix3x2 m, localM;
        // 特殊形状吸附（规格 9.6）**只在拖四角时可能出现**：整体移动、旋转一律不吸
        // （那两件事改的是位置和姿态，改不了"这是不是个正方形"）。每帧先清空，
        // 下面那条分支吸住了再填。
        _shapeSnap = ShapeSnapKind.None;
        if (_dragIsMove)
        {
            // 整体移动：指针在画布上走多少，对象就走多少。**不能**在框坐标里算
            // 再共轭回来——框是斜的时候那样会走偏方向。
            // 单选拖动时这里再叠一层选中框吸附；每帧从按下点重算，
            // 所以吸住后再拖开也不会累积误差。
            m = MoveMatrixWithAlignSnap(cur, alt);
        }
        else if (_dragHandle == SelHandle.Rotate)
        {
            // —— 旋转：**一帧一步地累积**（角度不设上限，见 SelRotationDegrees）——
            //
            // 角度在**框坐标**里量（和真正施加的旋转矩阵同一套输入）。
            // 选中框现在一律轴对齐（ToCanvas 是单位阵），所以框坐标 == 画布坐标，
            // 量出来的角度就是屏幕上看到的角度——这也正是"读数按眼睛看的方向"那条规则。
            var c = new Vector2((_dragFrame.Local.MinX + _dragFrame.Local.MaxX) * 0.5f,
                                (_dragFrame.Local.MinY + _dragFrame.Local.MaxY) * 0.5f);
            var p = _dragFrame.ToLocalPoint(cur);
            _rotAccumDeg += SelectionHandles.RotationStepDegrees(c, _rotPrevPoint, p);
            _rotPrevPoint = p;

            float localDeg;
            bool snapped;
            if (_rotLineLike)
            {
                // —— 单选直线 / 箭头：读数与吸附都走 **α₀ + Δ**（用户 2026-09-18 澄清）——
                //
                // 用户原话："按照高中数学这个直线的倾斜角，现在是 45 度，那么我那个旋转手柄
                // 刚开始转的时候就是 45 度。那么逆时针是为正，顺时针为负，它可以无限转下去，
                // 也就说可以转到 360、720、1000 多度，或者说负的 2000 多度……除了初始角度
                // 是要调一下以外，其他和原来的最开始的逻辑是一样的。"
                //
                // 于是这里**不折角、不取模**：读数就是 `按下时的 α₀ + 这一拖累积转过的角 Δ`
                // （Δ 本来就无上限，见 _rotAccumDeg）。45° 的线逆时针转一圈 = 405°、
                // 再转 = 765°；顺时针 400° = −355°。
                // 吸附在**展开值**上做，特殊角按 180° 周期（405.5° 吸到 405°），
                // 所以"要不要吸"和折回那一版完全一样，只是吸完的数仍是展开的。
                float expanded = _rotStartInclination + _rotAccumDeg;
                float snappedDeg = SelectionHandles.SnapExpandedInclinationDegrees(expanded, shift, alt,
                                                                                  out snapped);
                if (snapped)
                {
                    // "要落到那个角还得多转多少度"——把它加进累积角，
                    // 于是**矩阵用的是吸过之后的角**，标签上的数和屏幕上看到的一致。
                    _rotAccumDeg += snappedDeg - expanded;
                }
                localDeg = _rotAccumDeg;
                SelRotationInclination = _rotStartInclination + localDeg;   // 展开值，故意不折
                SelRotationReadsInclination = true;
            }
            else if (_rotPoseLike && _dragTargets.Length == 1)
            {
                // —— 单选一个图形：读数与吸附都走**姿态角**（规格 9.7）——
                //
                // 和直线那一支是同一套账，只是目标角不同（那边是八个特殊角，这边是 0/90）：
                //   "按下时的姿态角 ＋ 累积角" → 在这个**连续**的角上吸 → 把最小修正
                //   加回累积角。必须在连续角上吸：折过的 [0,180) 里 "179.6°" 离 0° 只有 0.4°，
                //   但它们差整整 180°——直接吸会把只偏 0.4° 的三角形**翻过来**。
                //
                // 镜像过的图形"本地转 +1°"在屏幕上是 -1°（行列式为负），所以两个方向都要乘
                // _rotMirrored 的符号；同一个手势里镜像状态不会变（乘上去的是纯旋转）。
                float sgn = _rotMirrored ? -1f : 1f;
                float expanded = _rotStartPose + sgn * _rotAccumDeg;
                float snappedExpanded = SelectionHandles.SnapExpandedPoseDegrees(expanded, shift, alt,
                                                                                out snapped);
                if (snapped)
                {
                    _rotAccumDeg += sgn * (snappedExpanded - expanded);
                    // 吸住时标签直接写"**吸到的那条线**"（0 / 90，或 Shift 网格上的角）：
                    // 从矩阵里解出来的值在浮点噪声下可能印成 `180.0°`（数学上和 0° 是同一条线，
                    // 但用户要的就是"拖到读数 0° 就转正了"）。两者差在 0.001° 以内，
                    // 所以下面那条"矩阵与标签同一个角"的自检照样成立。
                    // 先按 0.001° 抹一遍浮点噪声再折：`-0.0000001` 折进 [0,180) 会变成 179.9999999，
                    // 印出来还是 "180.0°"——把噪声抹掉才拿得到干净的 0.0（远细于一位小数的显示精度）。
                    SelRotationPose = SelectionHandles.FoldDegrees(MathF.Round(snappedExpanded, 3));
                }
                localDeg = _rotAccumDeg;
                SelRotationReadsPose = true;    // 没吸住时数值在下面 m 算出来之后再填
            }
            else
            {
                // 其它情况（多选 / 图像 / 自由笔迹 / 圆）：照旧读 Δ、照旧吸 90°。
                // 吸附作用在**累积角**上：90° / 15° 的整数倍在负角度、超过一圈的角度上照样对得上。
                localDeg = SelectionHandles.SnapRotationDegrees(_rotAccumDeg, shift, alt, out snapped);
                SelRotationReadsInclination = false;
            }

            // 这一段是"框坐标和屏幕反手时，把读数翻回眼睛看到的方向"的通用保险：
            // 镜像过的对象 + 斜框的组合下，本地角度的正负和屏幕是反的。
            // 选中框改成**一律轴对齐**之后 ToCanvas 恒为单位阵，这里恒不触发；
            // 留着是因为公式本来就要覆盖"框有自己的朝向"那一天（见 SelectionFrame 的注释）。
            // 注：它只管 Δ 那个读数；α 是从**画布坐标**量出来的，天生就是眼睛看到的方向。
            SelRotationDegrees = SelectionHandles.IsMirrored(_dragFrame.ToCanvas) ? -localDeg : localDeg;
            SelRotationSnapped = snapped;
            SelRotating = true;

            // 矩阵用**吸附后的那个数**（没镜像时它和标签上的数一模一样）。
            localM = SelectionHandles.RotateMatrix(localDeg, c);
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }
        else
        {
            // 手柄换算在**框坐标**里做（缩放的锚点是"对角那个手柄"，只有在框坐标里
            // 才是"沿着框的两条边"），再共轭回画布坐标：M = F⁻¹ · M_local · F
            //
            // 拖**矩形四角**时先问一句"要不要吸成正方形"（规格 9.6）。顺序是先吸附、
            // 再回退到通用换算：吸住了就用吸附给的那个矩阵，没吸住一个字都不改。
            // 注意它只改 `localM`（框坐标里的矩阵），共轭那一步照旧——这样"转过/镜像过的
            // 框"那种情形（今天框恒轴对齐，但公式留着）不会被绕过。
            if (SelectionHandles.TrySnapSquareCorner(_dragTargets, _dragHandle, _dragFrame,
                                                     cur, furniture, alt, out var squareM))
            {
                _shapeSnap = ShapeSnapKind.Square;
                _shapeSnapAnchor = cur;             // 胶囊贴在指针（= 被拖的那个角）上
                localM = squareM;
            }
            else
            {
                localM = SelectionHandles.DragMatrix(_dragHandle, _dragFrame,
                                                     _dragStartPoint, cur, furniture, shift, shift, alt);
            }
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }

        // 姿态角读数**从最终矩阵里解出来**：`对象变换 × 预览矩阵` 正是浮动层画那一帧用的式子，
        // 所以标签上的数就是屏幕上那个姿态——"矩阵与标签同一个角"是构造出来的，不是凑出来的
        // （直线那一支当年就是在这里对不上：标签用一个角、矩阵用另一个角）。
        // 吸住那一档已经在上面填成"吸到的那个角"了（0/90 更好看，见那里的注释），跳过。
        if (_rotPoseLike && _dragTargets.Length == 1 && !SelRotationSnapped)
            SelRotationPose = SelectionHandles.PoseAngleDegrees(_dragTargets[0].Transform * m);

        // 方案 B：**不动模型**，实时位移只活在 _selDragMatrix 里，由浮动层画预览。
        //
        // 以前这里是"每帧 SetTransformLive"，它会把渲染版本号抬起来，于是内容层
        // 每一帧都要把"走过的面积"里的所有笔迹重画一遍——实测散布全屏的一小撮
        // 是 19ms/帧，而其中被拖的只有那几条。现在内容层只在**手势开始**被标脏一次
        // （把这一批从块里摘出去那一下），中途一帧都不碰。
        //
        // 脏区 = 上一帧预览的位置 ∪ 这一帧预览的位置：后缓冲里躺着的是两帧前的画面，
        // 而这两块并起来正好覆盖"两帧之间差了哪些像素"（和以前"旧位 ∪ 新位"同一套账）。
        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);
        _selDragMatrix = m;
        _dragPrevBounds = PreviewBounds(m);
        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);

        // 方案 A：真的动过才收装饰（单击一下不该闪）。
        if (!_selDragMoved
            && Vector2.Distance(cur, _dragStartPoint) > ClickToleranceLogical * DpiScale)
            _selDragMoved = true;

        _dirty = true;
    }

    /// <summary>
    /// 松手：先把模型恢复到按下那一刻，再提交**一条**变换命令。
    ///
    /// 这样拖动过程中的实时预览不产生撤销记录，而撤销一步就精确回到拖动前
    /// ——不会出现"拖的时候动了三十次，要按三十次撤销"那种事。
    /// </summary>
    private void EndSelDrag()
    {
        // 参数滑杆：提交一步撤销，结束。
        if (_figureParamDragging) { FinishFigureParamDrag(); return; }

        // 文本框：改宽高手势收尾（模型已经实时改好，这里只清状态）。
        if (_textBoxResizing) { FinishTextBoxResize(); return; }

        SelDragging = false;
        SelRotating = false;            // 度数标签只在拖动中出现
        ResetAlignSnapLatch();          // 吸附只活在这一次拖动里；短亮自己淡出
        ClearAlignTargets();
        _rotAccumDeg = 0f;              // 下一次拖拽从 0 开始数（标签也不显示了）
        SelRotationReadsInclination = false;   // 读数归位：下一次按下时重新判定读 α 还是 Δ
        SelRotationReadsPose = false;          // 姿态角那一档同样归位
        _shapeSnap = ShapeSnapKind.None;       // "正方形"那颗胶囊也只在拖动中出现

        // 拖端点：收尾走"改几何"那条路（一次拖拽 = 一步改几何的撤销）。
        if (_vertexDragging) { CommitVertexDrag(); return; }

        if (_dragTargets == null) return;

        // 一批都没得动（例如选中的全锁着）：只把状态收干净，**不要**提交空动作
        // （提交了就是"点一下多一条空撤销记录"）。
        if (_dragTargets.Length == 0)
        {
            _dragTargets = null;
            _dragStartXform = null;
            _dragHandle = SelHandle.None;
            _dragIsMove = false;
            _dragHitStroke = null;
            _selDragMatrix = Matrix3x2.Identity;
            ReattachAfterDrag();
            _dirty = true;
            return;
        }

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            Doc.SetTransformLive(_dragTargets[i], _dragStartXform[i]);
            Doc.Dirty.Add(_dragTargets[i].PaddedBounds);
        }

        Doc.Selected.Clear();
        foreach (var t in _dragTargets) Doc.Selected.Add(t);
        // **一点没移动就别进撤销栈**：按一下选中框（或者点选一条）原本会多出一条
        // "原样"的撤销记录——点选一多，撤销栈里全是这种空记录。
        if (!_selDragMatrix.IsIdentity)
            Doc.ApplyTransform(_selDragMatrix);

        // "点一下把多选收窄成单选"：按下时命中了一条、而这一次**一点没移动** → 只留它
        // （PPT/Figma 的行为）。移动过就正常整体搬，不收窄。
        if (_dragIsMove && _dragHitStroke != null && _selDragMatrix.IsIdentity
            && Doc.Selected.Count > 1 && Doc.Selected.Contains(_dragHitStroke))
            Doc.SelectOnly(new[] { _dragHitStroke });

        _dragTargets = null;
        _dragStartXform = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _dragHitStroke = null;
        _selDragMatrix = Matrix3x2.Identity;

        // 方案 B：这一批回到内容层（下一次重画就把它们画进块里）。
        // 顺序放在最后：上面那条"提交变换"已经把旧位与新位都标脏了，
        // 所以这一帧的重画一定会带上它们（而不是继续画预览）。
        ReattachAfterDrag();

        _dirty = true;
    }

}
