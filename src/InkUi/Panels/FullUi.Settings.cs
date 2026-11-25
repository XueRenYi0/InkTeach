// [职责] 设置页：工具条/通用/书写/墨迹/AI/备份。
// [测试] --settingsaudit

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
    // ---- 上带右端的"动作"按钮（照假面板）------------------------------------
    //
    // 假面板里：**清空**原来挂在橡皮那条的右端（按住 0.8 秒防误触）；
    // 2026-11-15 用户定：橡皮带不再放清空，清屏统一走主条垃圾桶（单击 + 可撤销）。
    // **全选**仍挂在选择那条的右端，点一下就执行。
    //
    // **关闭白板**（2026-09-27 用户定的）挂白板那条的右端，点一下就执行。
    // 它是"白板格点第一下不再关板"补上的那一半：关板从此**只有这一个手动入口**
    //（另一条是"开穿透会自动关板"的互斥，那是自动的、不是入口）。
    //
    // 位置：上带**最右端**。2026-11-15 起橡皮带不再挂「清空」，
    // 清屏统一走主条垃圾桶；这里的动作只剩白板关闭、选择三连、截图粘贴、文本完成。
    private enum BandAction { None = 0, SelectAll, Copy, Paste, CloseBoard, PasteImage, TextDone, AiMenu }

    /// <summary>
    /// 「选择」上带右端的三连（2026-11-03 用户定）：全选 / 复制 / 粘贴。
    /// 触摸屏没有快捷键，复制粘贴必须有一个不用键盘的入口；
    /// 放这里而不是选中操作条，是因为"先选中、再操作"的顺序就是这个带子。
    ///
    /// **极简档只留复制 / 粘贴**：5 格时带子只有两百来像素，三个动作会把
    /// 「矩形 / 套索」挤到 5px（实测算式），那一档全选仍可双击选择格。
    /// </summary>
    private static readonly BandAction[] SelectBandActions =
        { BandAction.SelectAll, BandAction.Copy, BandAction.Paste };

    private static readonly BandAction[] SelectBandActionsNarrow =
        { BandAction.Copy, BandAction.Paste };

    private BandAction[] SelectActionsNow =>
        Density <= BandDensity.Minimal ? SelectBandActionsNarrow : SelectBandActions;

    private BandAction ActionOf(int bandCell) => bandCell switch
    {
        2 => BandAction.CloseBoard,   // 白板那条：最右端一个"关闭白板"
        // 橡皮带不再放「清空墨迹」：2026-11-15 起清屏统一走主条垃圾桶（单击 + 可撤销）。
        7 => _host?.State.TextBoxEditing == true ? BandAction.TextDone : BandAction.None,
        8 => BandAction.SelectAll,    // 单动作口径的兜底；实际按 ActionAt 取三连
        // 2026-11-03：**截图格是 9、图形格是 10**（文本插到 7 之后整体后移了一格）。
        // 以前这里写的是 10，结果「粘贴图片」挂到了图形那条上，截图条反而没有按钮。
        9 => BandAction.PasteImage,   // 截图那条：把剪贴板里的东西粘进来（8.3.0 从分段里拆出来）
        _ => BandAction.None,
    };

    /// <summary>这一条带上动作按钮有几个（选择那条是三个，其余最多一个）。</summary>
    private int ActionCount => _bandCell == 8
        ? SelectActionsNow.Length
        : ActionOf(_bandCell) == BandAction.None ? 0 : 1;

    private BandAction ActionAt(int i)
    {
        if (_bandCell == 8)
        {
            var actions = SelectActionsNow;
            return i >= 0 && i < actions.Length ? actions[i] : BandAction.None;
        }
        return i == 0 ? ActionOf(_bandCell) : BandAction.None;
    }
    private BandAction CurAction => ActionOf(_bandCell);

    private const float ActionW = 92f;
    /// <summary>
    /// **窄的那个动作按钮**（白板的"关闭白板"用它，只有图标、没有文字）。
    ///
    /// 为什么单独给一个宽度：白板那条带子本来就满——8 段（翻页器 ＋ 三色 ＋ 底纹 ＋ 间距）
    /// 要 420，加上 146 的透明度滑条只剩 25 的余量；再塞一个 92 宽的动作按钮，
    /// 段会被挤到 89% 缩放（`SegmentRect` 里的等比缩）。"关闭"这件事一个 ✕ 就够，
    /// 46 宽刚好（图标 18 ＋ 左右各 14），段就不用缩那么狠。
    /// </summary>
    private const float ActionWNarrow = 46f;

    /// <summary>窄动作按钮的宽（62 = 图标 16 + 两字 + 留白；选择三连是纯文字）。</summary>
    private const float ActionWTight = 62f;

    private float ActionWidthOf(BandAction a) => a switch
    {
        BandAction.CloseBoard => ActionWNarrow,
        // 全选 / 复制 / 粘贴：两字 + 留白
        BandAction.SelectAll or BandAction.Copy or BandAction.Paste => ActionWTight,
        BandAction.AiMenu => AiCellW,
        _ => ActionW,
    };

    /// <summary>三个动作按钮之间的缝（选择那条带才有三个）。</summary>
    private const float ActionGap = 6f;

    private float ActionWidthAt(int i) =>
        ActionAt(i) == BandAction.AiMenu ? AiCellW
        : _bandCell == 8 ? ActionWTight
        : ActionWidthOf(ActionAt(i));

    /// <summary>动作区整块：贴在带子最右端，三个按钮从左到右排。</summary>
    private RectF ActionGroupRect()
    {
        var band = BandRect();
        float right = band.MaxX - BarInset();
        float cy = (band.MinY + band.MaxY) * 0.5f;
        float w = 0f;
        for (int i = 0; i < ActionCount; i++)
            w += ActionWidthAt(i) + (i > 0 ? ActionGap : 0f);
        return new RectF
        {
            MinX = right - w, MinY = cy - Tokens.SegmentHeight * 0.5f,
            MaxX = right, MaxY = cy + Tokens.SegmentHeight * 0.5f,
        };
    }

    private RectF ActionRectAt(int i)
    {
        var g = ActionGroupRect();
        float x = g.MinX;
        for (int k = 0; k < i; k++) x += ActionWidthAt(k) + ActionGap;
        return new RectF
        {
            MinX = x, MinY = g.MinY,
            MaxX = x + ActionWidthAt(i), MaxY = g.MaxY,
        };
    }

    /// <summary>单动作那条带用的旧口径（等于第 0 个）。</summary>
    private RectF ActionRect() => ActionRectAt(0);

    /// <summary>指针落在第几个动作按钮上（-1 = 不在）。</summary>
    private int ActionHitAt(float x, float y)
    {
        for (int i = 0; i < ActionCount; i++)
            if (ActionRectAt(i).Contains(x, y)) return i;
        return -1;
    }

    /// <summary>动作按钮要占的横向空间（色片 / 分段 / 滑条都得让位）。</summary>
    private float ActionReserve
    {
        get
        {
            if (ActionCount == 0) return 0f;
            float w = 0f;
            for (int i = 0; i < ActionCount; i++)
                w += ActionWidthAt(i) + (i > 0 ? ActionGap : 0f);
            return w + 10f;
        }
    }

    /// <summary>"刚按完闪一下"的时刻（动作按钮的点击反馈）。</summary>
    private double _actionFlashUntil = double.NegativeInfinity;

    /// <summary>
    /// **双击选择格 = 全选**（用户 2026-09-27 定）。照的是 InkClass 那个"经典交互"：
    /// 它那边是 500ms 内双击选择图标 = 全选（`MW_FloatBar.cs` 的 500ms 双击检测）。
    ///
    /// ⚠ **双击能跟"单击换档"共存，全靠"只有两档"**：连点两下正好转两格、回到原档，
    ///   所以双击的净效果就是"全选，模式没动"（见 `Activate` 里 case 7 那一支）。
    ///   哪天选择方式加到**三档**，这条就不成立了（连点两下会净退一格）——
    ///   那时候要么去掉双击全选，要么加判定延迟（那时单击换档会迟钝，不划算）。
    /// </summary>
    private const double SelectDoubleClickMs = 500;
    private double _lastSelCellClickMs = double.NegativeInfinity;
    /// <summary>
    /// 双击的**第一下之前**那一档选择方式。
    /// 第二下用它把档位抵回去（第一下若已经切过档，这里正好抵掉）——
    /// 这样"从别的工具双击进来"和"已经在选择工具上双击"两种情况的结果一致：
    /// **全选 + 模式回到双击前**。
    /// </summary>
    private SelectMode _selModeBeforeDoubleClick = SelectMode.Rect;

    /// <summary>
    /// 动作按钮被点：按语义分派。选择那条的三连（全选 / 复制 / 粘贴）走同一条路。
    /// </summary>
    private void HandleBandAction(int i)
    {
        if (_host == null) return;
        switch (ActionAt(i))
        {
            case BandAction.CloseBoard:
                // 关闭白板：**点一下就执行**（和全选一样）——关板可逆，不必按住。
                // ⚠ 板已经关着时这一下什么都不做（按钮也是压暗的，见 DrawOneBandAction）。
                if (_host.State.Board)
                {
                    _host.Commands.SetBoard(false);
                    _actionFlashUntil = _host.NowMs + 260;
                }
                return;
            case BandAction.PasteImage:
                // 粘贴图片：点一下就执行——就是批注内的 Ctrl+V。
                _host.Commands.Paste();
                break;
            case BandAction.TextDone:
                // 文本工具的「完成」：触摸屏没有 Ctrl+Enter，必须有一颗明确的结束键。
                _host.Commands.CommitTextEdit();
                break;
            case BandAction.Copy:
                _host.Commands.Copy();
                break;
            case BandAction.Paste:
                _host.Commands.Paste();
                break;
            case BandAction.AiMenu:
                ToggleAiMenu();
                return;
            default:
                _host.Commands.SelectAll();
                break;
        }
        _actionFlashUntil = _host.NowMs + 260;
    }

    /// <summary>
    /// 动作按钮：图标 ＋ 文字；点击后闪一下。
    ///
    /// 白板那条的「关闭白板」只有**一个 ✕**（窄版，没有文字，见 <see cref="ActionWNarrow"/>），
    /// 而且**板已经关着时整块压暗、点了也不动**——那时候关板没有意义
    ///（压暗的写法沿用"到顶时上一屏"那一套：同色降到 30% 不透明度）。
    ///
    /// 2026-11-03：「选择」那条右端变成**三连**（全选 / 复制 / 粘贴），纯文字、无图标——
    /// 三个都是两个字，加图标会挤成小方块；其余带子仍是一个图标 + 文字。
    /// </summary>
    private void DrawBandAction(ID2D1DeviceContext ctx)
    {
        if (!RailOpen) return;
        int n = ActionCount;
        for (int i = 0; i < n; i++)
            DrawOneBandAction(ctx, ActionAt(i), ActionRectAt(i), textOnly: _bandCell == 8);
    }

    private void DrawOneBandAction(ID2D1DeviceContext ctx, BandAction a, in RectF r, bool textOnly)
    {
        if (a == BandAction.None) return;
        if (a == BandAction.AiMenu) { DrawAiCell(ctx, r); return; }
        bool closeBoard = a == BandAction.CloseBoard;
        bool paste = a == BandAction.PasteImage;
        bool textDone = a == BandAction.TextDone;
        bool dim = closeBoard && (_host == null || !_host.State.Board);
        var ink = dim ? new Color4(InkCol.R, InkCol.G, InkCol.B, 0.30f) : InkCol;
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 7f, 7f);
        var tint = Tokens.Accent;

        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(tint.R, tint.G, tint.B, (dim ? 0.03f : 0.08f))));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);

        // 窄版（关闭白板）：图标居中占满，没有文字那一栏
        if (closeBoard)
        {
            IconAtlas.DrawCentered(ctx, "dismiss", r, 16f, Brush(ctx, ink));
            if (_host != null && _host.NowMs < _actionFlashUntil)
                ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 2f);
            return;
        }

        // 三连（全选 / 复制 / 粘贴）：纯文字居中，不画图标
        if (textOnly)
        {
            string text = a switch
            {
                BandAction.SelectAll => "全选",
                BandAction.Copy => "复制",
                BandAction.Paste => "粘贴",
                _ => "",
            };
            _widgets.Text(ctx, text, r, 12.5f, Brush(ctx, InkCol));
            if (_host != null && _host.NowMs < _actionFlashUntil)
                ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 2f);
            return;
        }

        var iconBox = new RectF { MinX = r.MinX + 6f, MinY = r.MinY, MaxX = r.MinX + 28f, MaxY = r.MaxY };
        IconAtlas.DrawCentered(ctx, textDone ? "check" : paste ? "image" : "selectAll",
                                iconBox, 16f, Brush(ctx, ink));
        var labelBox = new RectF { MinX = r.MinX + 28f, MinY = r.MinY, MaxX = r.MaxX - 6f, MaxY = r.MaxY };
        bool tight = Density <= BandDensity.Compact;
        _widgets.Text(ctx, textDone ? "完成"
                              : paste ? (tight ? "粘贴" : "粘贴图片")
                              : "全选",
                      labelBox, 12.5f, Brush(ctx, InkCol));

        if (_host != null && _host.NowMs < _actionFlashUntil)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 2f);
    }

    // ---- 粗细预览：数字 ＋ **真实大小** --------------------------------------
    //
    // 用户 2026-09-17 的三句话：
    //   ①"笔和激光笔调节大小的时候可不可以显示笔号"（要一个读数）；
    //   ②"调节大小的时候那个点和实际大小是不是应该一样大，但是太大了装不下，
    //      我又不希望改动界面怎么办"；
    //   ③"面积橡皮擦的大小调整的时候也能预览实际大小"。
    //
    // 矛盾在于：滑条右端那个预览点最多只能占带子高（34 像素），而笔能到 40、
    // 整笔橡皮的圈能到 96、面积橡皮的块高能到 259 —— 装不下。
    //
    // 解法：**预览画到带子外面去**（带子在上就往上画，在下就往下画），
    // 并且把它算进 `QueryBounds()`——引擎是按那份矩形裁剪界面画的，
    // 不这么做画出去的部分会被裁掉。面板尺寸、布局一个像素都不用改，
    // 而且它只在"拖滑条 / 指针停在滑条上"时出现，平时那份矩形一点都不变。

    // **橡皮不给预览**（2026-10-09 晚用户："橡皮擦滑动的时候会显示'几百乘几百'的预览，
    // 那个是不是也不需要"）——橡皮的块头由落点自己说话，滑条上不再弹尺寸气泡；
    // 笔 / 荧光笔 / 激光 / 白板（板色片）照旧。
    private bool SizePreviewVisible =>
        BandOpen() && BandHasSlider && !BandHasTextSize
        && (_bandCell == 2 || _host == null
            || _host.State.Tool is not (Tool.Eraser or Tool.PixelEraser))
        && (_sliderDragging || _hover == 300);

    /// <summary>真实落点的宽高（逻辑像素）。**和引擎里那套落点同源**（都读 st.Width）。</summary>
    private (float W, float H) TrueSize(in UiState st)
    {
        // 白板那一格：预览的不是"落点"，而是**板色在这个不透明度下的样子**（一块小色片）
        if (_bandCell == 2) return (56f, 34f);
        return st.Tool switch
        {
            Tool.Eraser => (st.Width * 2f, st.Width * 2f),      // 引擎给的是半径 → 画出来是直径
            Tool.PixelEraser => (st.Width, st.Width * 1.618f),  // 黄金比例矩形（和落点一模一样）
            _ => (st.Width, st.Width),                          // 笔 / 荧光笔 / 激光 = 线宽
        };
    }

    private RectF SizePreviewRect(in UiState st)
    {
        var (w, h) = TrueSize(st);
        var band = BandRect();
        var (left, right) = SliderTrackRange();
        float kx = left + (right - left) * SliderT(st);
        float bw = MathF.Max(w, 58f) + 20f;      // 形状 + 左右留白
        float bh = h + 36f;                      // 上：形状；下：数字
        float top = BandAbove() ? band.MinY - 8f - bh : band.MaxY + 8f;
        // 横向夹在屏幕里：滑钮在最左/最右时，气泡会被推到屏幕外看不见
        float x0 = kx - bw * 0.5f, x1 = kx + bw * 0.5f;
        float lo = _screen.MinX + 4f, hi = _screen.MaxX - 4f;
        if (x0 < lo) { x1 += lo - x0; x0 = lo; }
        if (x1 > hi) { x0 -= x1 - hi; x1 = hi; }
        return new RectF
        {
            MinX = x0, MinY = top,
            MaxX = x1, MaxY = top + bh,
        };
    }

    /// <summary>读数。全是**逻辑像素**，和引擎里那个值同源（面积橡皮报"宽×高"）。</summary>
    private string SizeLabel(in UiState st)
    {
        if (_bandCell == 2) return $"{st.BoardOpacity * 100f:F0}%";
        return st.Tool switch
        {
            Tool.PixelEraser => $"{st.Width:F0}×{st.Width * 1.618f:F0}",
            Tool.Eraser => $"{st.Width * 2f:F0}",
            _ => $"{st.Width:F0}",
        };
    }

    private void DrawSizePreview(ID2D1DeviceContext ctx, in UiState st)
    {
        if (!SizePreviewVisible) return;

        var box = SizePreviewRect(st);
        var (w, h) = TrueSize(st);
        var bg = new RoundedRectangle(new Vortice.RawRectF(box.MinX, box.MinY, box.MaxX, box.MaxY), 10f, 10f);
        ctx.FillRoundedRectangle(bg, Brush(ctx, _dark ? Tokens.PanelDark : Tokens.PanelLight));
        ctx.DrawRoundedRectangle(bg, Brush(ctx, BorderCol), 1f);

        float cx = (box.MinX + box.MaxX) * 0.5f;
        float cy = box.MinY + 6f + h * 0.5f;
        DrawTrueSizeShape(ctx, st, new Vector2(cx, cy), w, h);

        var textBox = new RectF
        {
            MinX = box.MinX + 4f, MinY = box.MinY + 6f + h,
            MaxX = box.MaxX - 4f, MaxY = box.MaxY - 3f,
        };
        _widgets.Text(ctx, SizeLabel(st), textBox, 12f, Brush(ctx, InkCol));
    }

    /// <summary>按真实尺寸画一个"这一笔/这一块有多大"的样子。配色和落点光标一致。</summary>
    private void DrawTrueSizeShape(ID2D1DeviceContext ctx, in UiState st, Vector2 c, float w, float h)
    {
        var white = new Color4(1f, 1f, 1f, 0.85f);
        var dark = new Color4(0.22f, 0.28f, 0.38f, 0.9f);

        // 白板那一格：一块**板色在这个不透明度下的色片**——所见即所得
        if (_bandCell == 2)
        {
            var r = new Vortice.RawRectF(c.X - w * 0.5f, c.Y - h * 0.5f, c.X + w * 0.5f, c.Y + h * 0.5f);
            var b = st.BoardColor;
            ctx.FillRoundedRectangle(new RoundedRectangle(r, 6f, 6f),
                                     Brush(ctx, new Color4(b.R, b.G, b.B, b.A * st.BoardOpacity)));
            ctx.DrawRoundedRectangle(new RoundedRectangle(r, 6f, 6f), Brush(ctx, dark), 1.4f);
            return;
        }
        switch (st.Tool)
        {
            case Tool.Eraser:                       // 和整笔橡皮的落点圆环同一套
                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f),
                                Brush(ctx, new Color4(0.35f, 0.55f, 0.95f, 0.10f)));
                ctx.DrawEllipse(new Ellipse(c, w * 0.5f + 0.9f, w * 0.5f + 0.9f), Brush(ctx, white), 1.8f);
                ctx.DrawEllipse(new Ellipse(c, w * 0.5f, w * 0.5f), Brush(ctx, dark), 1.8f);
                break;

            case Tool.PixelEraser:                  // 和面积橡皮的落点矩形同一套
            {
                var r = new Vortice.RawRectF(c.X - w * 0.5f, c.Y - h * 0.5f, c.X + w * 0.5f, c.Y + h * 0.5f);
                ctx.FillRectangle(r, Brush(ctx, new Color4(0.35f, 0.55f, 0.95f, 0.22f)));
                ctx.DrawRectangle(r, Brush(ctx, dark), 1.8f);
                break;
            }

            case Tool.Laser:                        // 和激光落点同一个红点
                ctx.FillEllipse(new Ellipse(c, w * 0.5f + 1f, w * 0.5f + 1f), Brush(ctx, white));
                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f),
                                Brush(ctx, new Color4(1f, 0.16f, 0.16f, 0.95f)));
                break;

            case Tool.Highlighter:
            {
                // **圆盘**，和屏幕上的落点一模一样（`Overlay.DrawHighlighterDisc`：
                // 半径 = 半个笔宽，填充用荧光笔本色，外面套白／深两层描边）。
                //
                // 我第一版在这儿画了"一根那么粗的短条"，理由是"荧光笔画出来就是一大条"——
                // 那是**想当然**：点一下的落点就是个圆盘，短条反而和真实落点对不上，
                // 用户一眼就看出来了（"荧光笔的预览怎么不是圆的"）。
                // 规矩只有一条：**预览 = 那个工具在屏幕上的落点**。
                float r = w * 0.5f;
                ctx.FillEllipse(new Ellipse(c, r, r),
                                Brush(ctx, new Color4(st.HighlighterColor.R, st.HighlighterColor.G,
                                                      st.HighlighterColor.B, 0.22f)));
                ctx.DrawEllipse(new Ellipse(c, r + 0.75f, r + 0.75f), Brush(ctx, white), 1.5f);
                ctx.DrawEllipse(new Ellipse(c, r - 0.75f, r - 0.75f),
                                Brush(ctx, new Color4(0.22f, 0.28f, 0.38f, 0.55f)), 1.5f);
                break;
            }

            default:                                // 笔：一个实心圆 = 这一笔有多粗（用**当前笔色**）
                ctx.FillEllipse(new Ellipse(c, w * 0.5f, w * 0.5f), Brush(ctx, st.Color));
                break;
        }
    }

    private int HitSwatch(float x, float y)
    {
        if (!BandHasSwatches) return -1;
        if (!RailOpen) return -1;      // 还是一条色线时不吃点击（指针一靠近它就会张开）
        for (int i = 0; i < SwatchCount; i++)
            if (SwatchRect(i).Contains(x, y)) return i;
        return -1;
    }

    private int HitSegment(float x, float y)
    {
        int n = BandSegmentCount;
        if (!RailOpen) return -1;
        for (int i = 0; i < n; i++)
        {
            // 白板那一格中间那块「第 N 屏」是**只读**的（不是按钮）：它夹在两个翻页箭头
            // 中间、和它们贴在一起组成一个"翻页器"，点它什么都不该发生。
            if (_bandCell == 2 && BoardSegAt(i).Kind == BoardSegKind.PageLabel) continue;
            if (SegmentRect(i, n).Contains(x, y)) return i;
        }
        return -1;
    }

    /// <summary>色线 / 设置条该不该张开。判据：指针碰到它、或者正在拖滑条。</summary>
    private void UpdateRail()
    {
        if (!BandVisible())
        {
            _penPickerOpen = false;
            _penPickDragging = false;
            CloseTextFontMenu();
            _rail.To(0f, 0);
            _railHover = false;
            return;
        }
        if (_penPickerOpen || _textFontMenuOpen)
        {
            _railHover = true;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        // ---- 写字与色带的边界（2026-10-09 二轮定稿，用户原话：
        //      "只要收起来，它就一定是色带，不能是那种格子状的；只有退出来以后才能变成卡片"）----
        // 三条硬规则：
        //   ① 落笔那一刻：**没收干净的一律收回色线**——包括"正在张开的半路"
        //      （只冻结不拉回的话，张开动画会自己走完，写着写着就变成卡片了——这正是
        //       用户二轮里说的"偶尔还是会退化成卡片"）。已经完全展开（≥0.999）的保持不动。
        //   ② 整笔期间：冻结（不张不收）。
        //   ③ 抬手之后：进"余温"，且**必须让指针离开判定区一次**，再停回来（并距抬手 >
        //      RailRestMs），才允许重新张开——写字间歇的停顿不再把卡片弹出来。
        bool drawingNow = _host.State.IsDrawing;
        double now = _host.NowMs;
        if (drawingNow && !_railWasDrawing)
        {
            _railRequireLeave = true;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            if (_rail.Value < 0.999f) _rail.To(0f, Tokens.RailMs);
        }
        if (!drawingNow && _railWasDrawing) _railStrokeEndMs = now;
        _railWasDrawing = drawingNow;
        if (drawingNow)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            return;
        }

        // 穿透：上带**永远保持收起的那条色线**，不许张成设置条
        //（2026-10-02 用户口径：点穿透只是把它"收起来"，不是整条消失——面板高度不变，
        //  贴边隐藏露出来的还是它；但也不再像别的格子那样张着设置条）。
        // 进穿透那一下已经在 OnStateChanged 里启动折叠动画；这里兜住"之后又被谁强行张开"
        //（测试钩子、以及悬停意图残留），并让那两个计时器失效。
        if (_host.State.PassThrough)
        {
            _railHover = false;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            if (_rail.Value >= 0.5f) _rail.To(0f, Tokens.RailMs);
            return;
        }

        // 正在拖滑条：一直开着，不参与悬停那套计时
        // （手滑到轨道外面一点点不该让设置条收掉——拖到一半收掉是最气人的一种）
        if (_sliderDragging)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 色带常开（2026-10-05 用户点名的开关）：一直摊着，不参与悬停那套收放。
        if (_railPinned)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 触摸没有悬停：手指点在**面板任意处**（点工具格也算）就把设置条张开，并保持一段
        // （松手后 2.5s 内不收）——不然触摸用户只能去点那条色线（10 像素），很难点中
        // （2026-10-05 用户实测："点击图标色带不会展开，必须点色带位置"，触摸屏上太麻烦）。
        if (now < _railTouchHoldUntilMs)
        {
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
            return;
        }

        if (_railHover)
        {
            // 写字余温 + "离开一次"硬规则（见上面 ③）：抬手后马上停回来也不弹。
            if (_railRequireLeave || now - _railStrokeEndMs < Tokens.RailRestMs)
            {
                _railEnterAtMs = double.NegativeInfinity;
                return;
            }
            _railExitAtMs = double.NegativeInfinity;
            // **正在收时指针回来：立刻反着张开**（2026-10-09 沉稳档的对偶）。
            // 以前要等它收到 0.5 以下才肯重开（收尾 + 240ms 延迟 + 230ms 动画，最坏 ~0.7s）——
            // 手感是"我都把指针放回去了它还在收"；自检采样也会抓在动画中间（layout 判据全乱）。
            if (_rail.Running && _rail.Target < 0.5f)
            {
                _rail.To(1f, Tokens.RailMs);
                _railEnterAtMs = double.NegativeInfinity;
                return;
            }
            if (_rail.Value >= 0.5f) { _railEnterAtMs = double.NegativeInfinity; return; }
            if (double.IsNegativeInfinity(_railEnterAtMs)) _railEnterAtMs = now;
            else if (now - _railEnterAtMs >= Tokens.RailShowDelayMs) _rail.To(1f, Tokens.RailMs);
            return;
        }

        // 指针离开判定区——**只有"新的位置更新"才算数**（见 _railHoverStamp 注释）；
        // 这条同时兑现写字后的"离开一次"。
        if (!_railHover && _railHoverStamp > _railStrokeEndMs) _railRequireLeave = false;
        _railEnterAtMs = double.NegativeInfinity;
        if (_rail.Value <= 0.001f) { _railExitAtMs = double.NegativeInfinity; return; }
        if (double.IsNegativeInfinity(_railExitAtMs)) _railExitAtMs = now;
        else if (now - _railExitAtMs >= Tokens.RailHideDelayMs) _rail.To(0f, Tokens.RailMs);
    }

    /// <summary>
    /// 色线的"碰到"判定区：**按张开后的高度**算。
    ///
    /// 不这么做的话会来回抖：线一张开，它的矩形就往上长了 28 像素，
    /// 指针（还停在原来那条线的位置）立刻落到判定区外面 → 又收回去 → 再张开……
    /// </summary>
    private RectF RailZone()
    {
        var bar = BarRect();
        // ⚠ 这里以前是 `Tokens.BandHeight`（**一行**的高），图形那一格改成多行之后就短了一截：
        // 指针挪到上面那几行会被判成"离开面板"、带子当场收（用户 2026-09-20 报的 bug）。
        float h = BandBlockFull() + Tokens.RailHoverPad * 2f;
        return BandAbove()
            ? new RectF
            {
                MinX = bar.MinX, MinY = bar.MinY - BandGap - h,
                MaxX = bar.MaxX, MaxY = bar.MinY - BandGap + Tokens.RailHoverPad,
            }
            : new RectF
            {
                MinX = bar.MinX, MinY = bar.MaxY + BandGap - Tokens.RailHoverPad,
                MaxX = bar.MaxX, MaxY = bar.MaxY + BandGap + h,
            };
    }

    /// <summary>
    /// "焦点在悬浮框上"的判定区 = **主条 ∪ 设置条**（用户 2026-09-17 定的语义）。
    ///
    /// 以前只有**设置条自己那一条**算，于是老师想把鼠标从主条挪到色片上有两段路：
    /// 先碰到色线、等 120 毫秒张开、再点工具钉住，钉住了才敢把指针挪上去。
    /// 现在整块面板都算"焦点在面板上"：**指针在面板上它就张开，指针离开就收成色线**，
    /// 中间不用再点一次、也不会走两步就收回去。
    ///
    /// 注意判据用的是**主条 ∪ 色带**而不是"整个 UnionRect"：贴边隐藏时 UnionRect
    /// 会被夹到只剩 8 像素的露头，用它当判定区会让"贴边的面板"在指针没靠近时也展开。
    /// </summary>
    private RectF RailHoverZone()
    {
        var bar = BarRect();
        var rail = RailZone();
        return new RectF
        {
            MinX = MathF.Min(bar.MinX, rail.MinX),
            MinY = MathF.Min(bar.MinY, rail.MinY),
            MaxX = MathF.Max(bar.MaxX, rail.MaxX),
            MaxY = MathF.Max(bar.MaxY, rail.MaxY),
        };
    }

    /// <summary>
    /// 这一档显示哪几个色片（返回**色位编号**）。
    ///
    /// 2026-10-28 起笔的 8 个色位都是同一种东西：位置固定、默认色固定、每个都能被用户换色；
    /// 不再分“固定色片 / 自定义槽”。密度只决定显示哪几个色位：
    ///   3 格 = 红 / 黑 / 白；4–7 格 = 红 / 黑 / 蓝 / 白；8–9 格 = 红黑蓝绿黄白；10+ = 全部 8 个。
    /// 荧光笔仍用自己那张固定表（5 色）。
    /// </summary>
    private const int PenLastSlot = 7;
    private static readonly int[] NanoSwatchIdx = { 1, 0, PenLastSlot };
    private static readonly int[] MiniSwatchIdx = { 1, 0, 2, PenLastSlot };
    private static readonly int[] CompactSwatchIdx = { 1, 0, 2, 3, 4, PenLastSlot };
    private static readonly int[] FullSwatchIdx = { 0, 1, 2, 3, 4, 5, 6, PenLastSlot };
    private static readonly int[] HlSwatchIdx = CreateAllIdx(Tokens.HighlighterPalette.Length);
    private static readonly int[] HlSwatchIdx4 = { 0, 1, 2, 3 };
    private static readonly int[] HlSwatchIdx3 = { 0, 1, 2 };

    private static int[] CreateAllIdx(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    /// <summary>这一刻用的是荧光笔那张表吗（色片表 / 切色循环都看它）。</summary>
    private bool UsingHighlighterSwatches => _host != null && _host.State.Tool == Tool.Highlighter;
    private bool UsingTextSwatches => _bandCell == 7;

    /// <summary>这一刻生效的色片表（笔那张 / 荧光笔那张 / 文本那张）。</summary>
    private (string Name, Color4 Color)[] SwatchTable =>
        UsingHighlighterSwatches ? Tokens.HighlighterPalette : Tokens.Palette;

    /// <summary>
    /// 文字色带显示几格：**看当前主条实际剩多宽**（用户 2026-11-01 定的
    /// "几格取决于当前主操作条的长度"）。至少 3 格（红/黑/白），够宽才给第 4 格自定义。
    /// </summary>
    private int TextSwatchCountNow
    {
        get
        {
            // 用**展开后的整条宽**判断，不用动画中的宽度：否则色带张开到一半
            // 会从 3 格跳成 4 格，看着像闪了一下（和 BoardCompactBand 同一条经验）。
            float avail = ExpandedWidth() - BarInset() * 2f - TextSizeReserve - ActionReserve;
            const float minCell = 34f;
            return avail >= minCell * 4 + 4f * 3
                ? 4 : 3;
        }
    }

    private static readonly int[] TextSwatchIdx3 = { 0, 1, 2 };
    private static readonly int[] TextSwatchIdx4 = { 0, 1, 2, 3 };

    private int[] SwatchIdx
    {
        get
        {
            int n = VisibleCount();
            if (UsingHighlighterSwatches)
                return n <= 3 ? HlSwatchIdx3 : n <= 5 ? HlSwatchIdx4 : HlSwatchIdx;
            if (UsingTextSwatches)
                return TextSwatchCountNow >= 4 ? TextSwatchIdx4 : TextSwatchIdx3;
            if (n <= 3) return NanoSwatchIdx;
            return Density switch
            {
                BandDensity.Minimal => MiniSwatchIdx,
                BandDensity.Compact => CompactSwatchIdx,
                _ => FullSwatchIdx,
            };
        }
    }
    private int SwatchCount => SwatchIdx.Length;
    private Color4 SwatchColor(int i)
    {
        int idx = SwatchIdx[i];
        if (UsingTextSwatches)
        {
            var textColors = _host?.State.TextSwatchColors;
            return textColors != null && idx >= 0 && idx < textColors.Length
                ? textColors[idx] : SwatchTable[idx].Color;
        }
        if (UsingHighlighterSwatches)
        {
            var hl = _host?.State.HighlighterSwatchColors;
            return hl != null && idx >= 0 && idx < hl.Length
                ? hl[idx] : SwatchTable[idx].Color;
        }
        var colors = _host?.State.PenSwatchColors;
        return colors != null && idx >= 0 && idx < colors.Length
            ? colors[idx] : SwatchTable[idx].Color;
    }

    private void ActivateSwatch(int i)
    {
        int slot = SwatchIdx[i];
        if (UsingTextSwatches)
        {
            // 和画笔同一套：已经是这个颜色 → 再点打开取色板改这一格；
            // 否则只选这一格（只影响下一次输入，不改已经选中的文字）。
            if (SameColor(_host.State.TextColor, SwatchColor(i)))
            {
                _host.Commands.BeginTextColorPick(slot);
                OpenTextColorPicker(slot);
            }
            else _host.Commands.SelectTextSwatch(slot);
            return;
        }
        if (UsingHighlighterSwatches)
        {
            // 荧光笔也是：已经是这一格 → 再点打开它的取色板；否则只选色。
            if (_host.State.HighlighterSwatchSelected == slot) OpenHighlighterColorPicker(slot);
            else _host.Commands.SelectHighlighterSwatch(slot);
            return;
        }

        // 已经是这一格 → 再点一次 = 换这个色位的颜色；否则只选色。
        if (_host.State.PenSwatchSelected == slot) OpenPenColorPicker(slot);
        else _host.Commands.SelectPenSwatch(slot);
    }

    /// <summary>
    /// **已经是它了，再点一下 = 换下一个颜色**（用户 2026-09-27 定的）。
    ///
    /// 老师用着笔想换个色，原来是"把指针挪到色带上、瞄准某个色片"（还得先等色带张开）；
    /// 现在直接在工具格上连点就行——**手不用离开那一格**。荧光笔同理。
    ///
    /// 三条规矩：
    ///   · 循环范围 = **色带上看得见的那排色片**（`SwatchIdx`）：所见即所得，不会切到一个
    ///     色带上没有的颜色；色片的高亮圈就是"我现在是哪个色"的指示器；
    ///   · 顺序 = 色片表顺序（常用的排前面，见 `Tokens.Palette` 的注释）；
    ///   · 当前色不在表里（比如从选中面板改过色）→ 落到**第 1 个**。
    ///
    /// ⚠ **只在"色带本来就在这一格"时才切色**（调用处的判据）：老师从图形面板点回笔那一格，
    ///   意思是"把笔拿回来"，那时候悄悄换个颜色是最气人的（同图形那一格"不动工具"的规矩）。
    /// </summary>
    private void CycleColor()
    {
        int n = SwatchCount;
        if (n == 0) return;
        var st = _host.State;

        if (UsingTextSwatches)
        {
            // 文本格连点第二下 = 换下一个文字色（只管下一次输入）。
            int atText = -1;
            for (int i = 0; i < n; i++)
                if (SameColor(SwatchColor(i), st.TextColor)) { atText = i; break; }
            ActivateSwatch((atText + 1) % n);
            Invalidate();
            return;
        }

        if (!UsingHighlighterSwatches)
        {
            int at = -1;
            for (int i = 0; i < n; i++)
                if (st.PenSwatchSelected == SwatchIdx[i]) { at = i; break; }
            // 外部改色（选中面板等）没有色位编号：按颜色值找回当前位置，
            // 这样“点笔格换下一个色”仍然从眼前这个颜色往后走，不会先跳回第一个。
            if (at < 0)
            {
                for (int i = 0; i < n; i++)
                    if (SameColor(SwatchColor(i), st.PaletteBase)) { at = i; break; }
            }
            int next = at >= 0 ? (at + 1) % n : 0;
            _host.Commands.SelectPenSwatch(SwatchIdx[next]);
            Invalidate();
            return;
        }

        int atHl = -1;
        for (int i = 0; i < n; i++)
            if (IsSwatchActive(st, i)) { atHl = i; break; }
        if (atHl < 0)
        {
            for (int i = 0; i < n; i++)
                if (SameColor(SwatchColor(i), st.PaletteBase)) { atHl = i; break; }
        }
        ActivateSwatch((atHl + 1) % n);
        Invalidate();
    }

    /// <summary>
    /// 两个颜色算不算同一个（阈值 0.02）。
    /// **色片高亮和切色找位置共用这一把尺子**——各写一份的话迟早不一致：
    /// 会出现"高亮看着在这个色上，点一下却从下一个色开始切"这种事。
    /// </summary>
    private static bool SameColor(in Color4 a, in Color4 b) =>
        MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f && MathF.Abs(a.B - b.B) < 0.02f;

    /// <summary>
    // [2026-10-05 用户定] `CycleSelectMode`（点选择格/再按 Ctrl+M 切矩形↔套索）已删除：
    // 爱用矩形的一直用矩形、爱用套索的一直用套索；子类型在面板上带那两段里选（见 Activate case 7）。

    /// <summary>
    /// 点了一下虚实线那一格：**三档轮流**（实线 → 虚线 → 点线 → 实线）。
    ///
    /// 和橡皮那一格"点一下换一次"是同一个约定——上带里摆不下下拉框，**点就是切**；
    /// 三档而不是两档，是因为引擎那边线型本来就是三档（见 StrokeDash），
    /// 少给一档等于把"点线"藏起来。
    ///
    /// 顺手起一次换挡动画（见 <see cref="_dashFade"/>）：**旧档要先当场记下来**，
    /// 不然命令一发、状态就变了，动画开始之后再也问不出"从哪一档来的"。
    /// </summary>
    // ---- 色位取色板（左边 HSV / 吸管，右边常用色 + 最近；荧光笔多一段浓度）----
    private const float PenPickSv = 120f;
    private const float PenPickHue = 18f;
    private const float PenPickPad = 10f;
    private const float PenPickGap = 8f;
    private const float PenPickLeftW = PenPickSv + PenPickGap + PenPickHue; // 146：吸管右沿和彩虹带右沿对齐
    private const float PenPickColGap = 12f;
    private const float PenPickCell = 18f;
    private const float PenPickCellGap = 4f;
    private const int PenPickCols = 8;
    private const float PenPickRightW = PenPickCols * PenPickCell + (PenPickCols - 1) * PenPickCellGap; // 172
    private const float PenPickCardW = PenPickPad * 2 + PenPickLeftW + PenPickColGap + PenPickRightW;    // 350
    private const float PenPickRightX = PenPickPad + PenPickLeftW + PenPickColGap;
    private const float PenPickGridY = PenPickPad + 18f;

    /// <summary>笔 5 行 40 色；荧光笔 3 行 24 色（只要亮色）。</summary>
    private int PickerCommonRows => _pickerHighlighter ? 3 : 5;
    private float PickerCardH => _pickerHighlighter ? 204f : 196f;
    private float PickerRecentHeaderY
        => PenPickGridY + PickerCommonRows * (PenPickCell + PenPickCellGap) + 10f;
    private float PickerRecentY => PickerRecentHeaderY + 16f;
    /// <summary>荧光笔：浓度滑条在左栏（预览/吸管下面）；预览条在右栏底部。</summary>
    private const float PenPickOpacityHeaderY = 168f;
    private const float PenPickOpacityTrackY = 182f;
    private float PickerPreviewY => PickerCardH - PenPickPad - 20f;

    private static Color4 C(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1f);

    /// <summary>
    /// 取色板右栏的 40 个固定常用色（5 行 × 8 列）。**第一行 = 现在色带上的 8 个默认色**，
    /// 既能当常用色，也能当“误改后手动恢复”的入口；后 4 行按课堂用途分组。
    /// </summary>
    private static readonly (string Name, Color4 Color)[] PenPickerCommon =
    {
        Tokens.Palette[0], Tokens.Palette[1], Tokens.Palette[2], Tokens.Palette[3],
        Tokens.Palette[4], Tokens.Palette[5], Tokens.Palette[6], Tokens.Palette[7],

        // 批改 / 强调
        ("亮红", C(0xFF, 0x3B, 0x30)), ("朱红", C(0xFF, 0x45, 0x00)),
        ("珊瑚", C(0xFF, 0x6B, 0x6B)), ("玫红", C(0xFF, 0x2D, 0x55)),
        ("品红", C(0xE9, 0x1E, 0x63)), ("亮橙", C(0xFF, 0x7A, 0x00)),
        ("琥珀", C(0xFF, 0xB3, 0x00)), ("金", C(0xFF, 0xD5, 0x4F)),

        // 公式 / 理科
        ("纯蓝", C(0x00, 0x66, 0xFF)), ("天蓝", C(0x38, 0xBD, 0xF8)),
        ("青", C(0x06, 0xB6, 0xD4)), ("蓝绿", C(0x0D, 0x94, 0x88)),
        ("亮绿", C(0x22, 0xC5, 0x5E)), ("薄荷", C(0x34, 0xD3, 0x99)),
        ("靛蓝", C(0x4F, 0x46, 0xE5)), ("紫罗兰", C(0x7C, 0x3A, 0xED)),

        // 结构 / 中性
        ("深灰", C(0x37, 0x41, 0x51)), ("中灰", C(0x6B, 0x72, 0x80)),
        ("浅灰", C(0x9C, 0xA3, 0xAF)), ("银灰", C(0xD1, 0xD5, 0xDB)),
        ("深蓝", C(0x1E, 0x3A, 0x8A)), ("石板", C(0x47, 0x55, 0x69)),
        ("棕", C(0x92, 0x40, 0x0E)), ("橄榄", C(0x4D, 0x7C, 0x0F)),

        // 暗板 / 浅色
        ("奶油", C(0xFF, 0xF8, 0xE7)), ("浅黄", C(0xFF, 0xF5, 0x9D)),
        ("浅橙", C(0xFD, 0xBA, 0x74)), ("浅绿", C(0x86, 0xEF, 0xAC)),
        ("浅青", C(0x67, 0xE8, 0xF9)), ("浅蓝", C(0x93, 0xC5, 0xFD)),
        ("薰衣草", C(0xC4, 0xB5, 0xFD)), ("浅粉", C(0xF9, 0xA8, 0xD4)),
    };

    /// <summary>
    /// 荧光笔取色板的 24 个固定亮色（3 行 × 8 列）。第一行 = 现有 5 个荧光色 + 3 个常用亮色；
    /// 不放黑/深灰/深棕/藏青——它们加 alpha 后会变成脏灰/脏棕。
    /// </summary>
    private static readonly (string Name, Color4 Color)[] HighlighterPickerCommon =
    {
        Tokens.HighlighterPalette[0], Tokens.HighlighterPalette[1], Tokens.HighlighterPalette[2],
        Tokens.HighlighterPalette[3], Tokens.HighlighterPalette[4],
        ("荧光紫", C(0xC7, 0x7D, 0xFF)), ("荧光蓝", C(0x7C, 0xC7, 0xFF)),
        ("浅黄", C(0xFF, 0xF5, 0x9D)),

        ("浅绿", C(0xB9, 0xF6, 0xCA)), ("浅青", C(0xB2, 0xEB, 0xF2)),
        ("浅蓝", C(0xBB, 0xDE, 0xFB)), ("浅紫", C(0xD1, 0xC4, 0xE9)),
        ("浅粉", C(0xF8, 0xBB, 0xD0)), ("桃", C(0xFF, 0xCC, 0xBC)),
        ("金", C(0xFF, 0xE0, 0x82)), ("柠檬", C(0xF0, 0xF4, 0xC3)),

        ("珊瑚", C(0xFF, 0xAB, 0x91)), ("玫红", C(0xF4, 0x8F, 0xB1)),
        ("青绿", C(0x80, 0xCB, 0xC4)), ("湖蓝", C(0x81, 0xD4, 0xFA)),
        ("薰衣草", C(0xB3, 0x9D, 0xDB)), ("薄荷", C(0xA7, 0xFF, 0xEB)),
        ("杏", C(0xFF, 0xE0, 0xB2)), ("草绿", C(0xC5, 0xE1, 0xA5)),
    };

    private (string Name, Color4 Color)[] PickerCommon =>
        _pickerHighlighter ? HighlighterPickerCommon : PenPickerCommon;
    private int PickerCommonCount => PickerCommon.Length;
    private Color4 PickerCommonColor(int i) => PickerCommon[i].Color;

    private int PenPickerBandPos()
    {
        var idx = SwatchIdx;
        for (int i = 0; i < idx.Length; i++) if (idx[i] == _penPickerSlot) return i;
        return -1;
    }

    private void OpenPenColorPicker(int slot) => OpenColorPicker(slot, highlighter: false);
    private void OpenHighlighterColorPicker(int slot) => OpenColorPicker(slot, highlighter: true);
    private void OpenTextColorPicker(int slot) => OpenColorPicker(slot, highlighter: false, text: true);

    private void OpenColorPicker(int slot, bool highlighter, bool text = false)
    {
        if (_hexEditing) CancelHexEdit();
        if (_textFontMenuOpen) CloseTextFontMenu();
        _pickerReturnAfterPick = false;
        _pickerReturnArmed = false;
        var st = _host.State;
        Color4 c;
        if (text)
        {
            c = st.TextColor;
        }
        else if (highlighter)
        {
            var colors = st.HighlighterSwatchColors;
            c = colors != null && slot >= 0 && slot < colors.Length
                ? colors[slot] : Tokens.HighlighterPalette[0].Color;
        }
        else
        {
            var colors = st.PenSwatchColors;
            c = colors != null && slot >= 0 && slot < colors.Length
                ? colors[slot] : Tokens.Palette[0].Color;
        }
        _pickerHighlighter = highlighter;
        _pickerText = text;
        _penPickerSlot = slot;
        (_penPickH, _penPickS, _penPickV) = Hsv.FromRgb(c);
        _penPickDragging = false;
        _penPickOpacityDragging = false;
        _hexEditing = false;
        _hexBuffer = "";
        _hexSelectAll = false;
        _penPickerDismissUp = false;
        _penPickerOpen = true;
        // 打开 = 新的一次交互：上一次按下的残留一律清掉，免得抬起时被当成"点某格"。
        _press = -1;
        _pressId = 0;
        _dragging = false;
        _sliderDragging = false;
        CancelTipHold();
        HideTip();
        // 锁住贴边隐藏（见 UpdatePeek）：**原地冻结**在这一刻的露头进度，
        // 工具条和色板都不再动。不钉到 1 是怕老师正好在半收合时点开，
        // 面板"啪"地弹回全展开——那也是看得见的跳。
        _peek.Jump(_peek.Value);
        _peekPendingCollapse = false;
        _leftAtMs = _host.NowMs;
        _frameShiftValid = false;
        Invalidate();
    }

    private void ClosePenColorPicker(bool consumeUp = false)
    {
        if (!_penPickerOpen) return;
        if (_hexEditing) CancelHexEdit();
        _penPickOpacityDragging = false;
        _penPickerOpen = false;
        _penPickDragging = false;
        _penPickerDismissUp = consumeUp;   // 关掉它的这一下，抬起也归它
        _press = -1;
        _dragging = false;
        _leftAtMs = _host.NowMs;
        _frameShiftValid = false;
        Invalidate();
    }

    private RectF PenPickerRect()
    {
        var band = BandRect();
        int pos = PenPickerBandPos();
        var anchor = pos >= 0 ? SwatchRect(pos) : band;
        // **局部坐标**（RenderShifted 会整体平移 Shift）。夹取也要按"局部坐标系里的
        // 屏幕"来夹：色板打开时贴边位移被冻结，Shift 可能不是 0；直接拿 _screen
        // 夹局部坐标会错一块（画出来跑到屏幕外/脏区对不上）。
        var shift = Shift();
        float minX = _screen.MinX - shift.X, maxX = _screen.MaxX - shift.X;
        float minY = _screen.MinY - shift.Y, maxY = _screen.MaxY - shift.Y;
        float x = (anchor.MinX + anchor.MaxX - PenPickCardW) * 0.5f;
        float y = BandAbove()
            ? band.MinY - PenPickGap - PickerCardH
            : band.MaxY + PenPickGap;
        x = Math.Clamp(x, minX + 8f, MathF.Max(minX + 8f, maxX - 8f - PenPickCardW));
        y = Math.Clamp(y, minY + 8f, MathF.Max(minY + 8f, maxY - 8f - PickerCardH));
        return new RectF { MinX = x, MinY = y, MaxX = x + PenPickCardW, MaxY = y + PickerCardH };
    }

    private RectF PenPickerSvRect()
    {
        var p = PenPickerRect();
        return new RectF
        {
            MinX = p.MinX + PenPickPad, MinY = p.MinY + PenPickPad,
            MaxX = p.MinX + PenPickPad + PenPickSv, MaxY = p.MinY + PenPickPad + PenPickSv,
        };
    }

    private RectF PenPickerHueRect()
    {
        var sv = PenPickerSvRect();
        return new RectF
        {
            MinX = sv.MaxX + PenPickGap, MinY = sv.MinY,
            MaxX = sv.MaxX + PenPickGap + PenPickHue, MaxY = sv.MaxY,
        };
    }

    private RectF PenPickerPreviewRect()
    {
        var p = PenPickerRect();
        float y = PenPickerSvRect().MaxY + PenPickGap;
        return new RectF { MinX = p.MinX + PenPickPad, MinY = y, MaxX = p.MinX + PenPickPad + 26f, MaxY = y + 26f };
    }

    private RectF PenPickerEyeRect()
    {
        var p = PenPickerRect();
        float y = PenPickerPreviewRect().MinY;
        float right = p.MinX + PenPickPad + PenPickLeftW;
        return new RectF { MinX = right - 44f, MinY = y, MaxX = right, MaxY = y + 26f };
    }

    private RectF PenPickerCommonRect(int index)
    {
        var p = PenPickerRect();
        int row = index / PenPickCols, col = index % PenPickCols;
        float x = p.MinX + PenPickRightX + col * (PenPickCell + PenPickCellGap);
        float y = p.MinY + PenPickGridY + row * (PenPickCell + PenPickCellGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + PenPickCell, MaxY = y + PenPickCell };
    }

    private int PenPickerCommonHit(float x, float y)
    {
        for (int i = 0; i < PickerCommonCount; i++)
            if (PenPickerCommonRect(i).Contains(x, y)) return i;
        return -1;
    }

    private RectF PenPickerRecentRect(int i)
    {
        var p = PenPickerRect();
        float x = p.MinX + PenPickRightX + i * (PenPickCell + PenPickCellGap);
        float y = p.MinY + PickerRecentY;
        return new RectF { MinX = x, MinY = y, MaxX = x + PenPickCell, MaxY = y + PenPickCell };
    }

    /// <summary>色值文本的可点/可编辑区域（预览色块和吸管之间）。</summary>
    private RectF PenPickerHexRect()
    {
        var prev = PenPickerPreviewRect();
        var eye = PenPickerEyeRect();
        return new RectF
        {
            MinX = prev.MaxX + 4f, MinY = prev.MinY + 2f,
            MaxX = eye.MinX - 4f, MaxY = prev.MaxY - 2f,
        };
    }

    /// <summary>荧光笔浓度滑条（左栏，预览/吸管下面）。</summary>
    private RectF PenPickerOpacityTrackRect()
    {
        var p = PenPickerRect();
        float x = p.MinX + PenPickPad;
        float y = p.MinY + PenPickOpacityTrackY;
        return new RectF { MinX = x, MinY = y, MaxX = x + PenPickLeftW, MaxY = y + 16f };
    }

    private int PenPickerOpacityLevelAt(float x)
    {
        var r = PenPickerOpacityTrackRect();
        float left = r.MinX + 6f, right = r.MaxX - 6f;
        float t = Math.Clamp((x - left) / MathF.Max(1f, right - left), 0f, 1f);
        return (int)MathF.Round(t * 4f);
    }

    private void SetPenPickFromPoint(float x, float y, bool hue)
    {
        var sv = PenPickerSvRect();
        var hr = PenPickerHueRect();
        if (hue)
        {
            _penPickH = Math.Clamp((y - hr.MinY) / MathF.Max(1f, hr.MaxY - hr.MinY), 0f, 1f);
        }
        else
        {
            _penPickS = Math.Clamp((x - sv.MinX) / MathF.Max(1f, sv.MaxX - sv.MinX), 0f, 1f);
            _penPickV = 1f - Math.Clamp((y - sv.MinY) / MathF.Max(1f, sv.MaxY - sv.MinY), 0f, 1f);
        }
        Invalidate();
    }

    /// <summary>取色板打开时，按一下先于工具带处理；返回 true 表示这一下已消费。</summary>
    private bool PenPickerPointerDown(float x, float y)
    {
        if (!_penPickerOpen) return false;
        int slot = _penPickerSlot;
        bool inHex = PenPickerHexRect().Contains(x, y);
        if (_hexEditing && !inHex) CommitHexEdit();
        if (inHex)
        {
            if (_hexEditing) CommitHexEdit();
            else BeginHexEdit();
            return true;
        }

        if (!_pickerText && PenPickerEyeRect().Contains(x, y))
        {
            // 从取色板里点吸管：取完（或 Esc 取消）把取色板放回来，老师可以接着调浓淡/换色。
            _pickerReturnAfterPick = true;
            _pickerReturnArmed = true;
            _pickerReturnSlot = slot;
            _pickerReturnHighlighter = _pickerHighlighter;
            ClosePenColorPicker();
            _host.Commands.BeginScreenColorPickAt(slot);
            return true;
        }

        var recent = _pickerHighlighter
            ? _host.State.RecentHighlighterColors
            : _host.State.RecentPenColors;
        int n = Math.Min(recent?.Length ?? 0, 8);
        for (int i = 0; i < n; i++)
        {
            if (!PenPickerRecentRect(i).Contains(x, y)) continue;
            if (_pickerText)
                _host.Commands.SetTextColor(recent[i]);
            else if (_pickerHighlighter)
                _host.Commands.SetHighlighterSwatchColor(slot, recent[i], rememberRecent: true, normalize: false);
            else
                _host.Commands.SetPenSwatchColor(slot, recent[i], rememberRecent: true);
            ClosePenColorPicker(consumeUp: true);
            return true;
        }

        int common = PenPickerCommonHit(x, y);
        if (common >= 0)
        {
            // 固定常用色：只改色位，不写“最近使用”（它本来就在常用色里）。
            if (_pickerText)
                _host.Commands.SetTextColor(PickerCommonColor(common));
            else if (_pickerHighlighter)
                _host.Commands.SetHighlighterSwatchColor(
                    slot, PickerCommonColor(common), rememberRecent: false, normalize: false);
            else
                _host.Commands.SetPenSwatchColor(slot, PickerCommonColor(common), rememberRecent: false);
            ClosePenColorPicker(consumeUp: true);
            return true;
        }

        if (_pickerHighlighter)
        {
            var track = PenPickerOpacityTrackRect();
            var hit = new RectF
            {
                MinX = track.MinX - 6f, MinY = track.MinY - 8f,
                MaxX = track.MaxX + 6f, MaxY = track.MaxY + 8f,
            };
            if (hit.Contains(x, y))
            {
                _penPickOpacityDragging = true;
                _host.Commands.SetHighlighterOpacity(slot, PenPickerOpacityLevelAt(x));
                return true;
            }
        }

        if (PenPickerSvRect().Contains(x, y))
        {
            _penPickDragging = true;
            _penPickOnHue = false;
            SetPenPickFromPoint(x, y, false);
            return true;
        }
        if (PenPickerHueRect().Contains(x, y))
        {
            _penPickDragging = true;
            _penPickOnHue = true;
            SetPenPickFromPoint(x, y, true);
            return true;
        }
        if (PenPickerRect().Contains(x, y)) return true;   // 卡片空白：吃掉，不关

        // ---- 卡片外：先看是不是色片，再关 ----
        //
        // 点当前色片 = 只关；点别的色片 = 选中那个色位并关。两种都吃掉这一次按下，
        // `ClosePenColorPicker` 会把抬起也记成"已消费"。
        int sw = HitSwatch(x, y);
        if (sw >= 0)
        {
            if (_pickerText)
            {
                if (!SameColor(_host.State.TextColor, SwatchColor(sw)))
                    _host.Commands.SetTextColor(SwatchColor(sw));
            }
            else
            {
                int hitSlot = SwatchIdx[sw];
                if (hitSlot != slot)
                {
                    if (_pickerHighlighter) _host.Commands.SelectHighlighterSwatch(hitSlot);
                    else _host.Commands.SelectPenSwatch(hitSlot);
                }
            }
        }
        ClosePenColorPicker(consumeUp: true);
        return true;
    }

    private bool PenPickerPointerMove(float x, float y)
    {
        if (!_penPickerOpen) return false;
        if (_penPickOpacityDragging)
        {
            _host.Commands.SetHighlighterOpacity(_penPickerSlot, PenPickerOpacityLevelAt(x));
            return true;
        }
        if (!_penPickDragging) return false;
        SetPenPickFromPoint(x, y, _penPickOnHue);
        return true;
    }

    private bool PenPickerPointerUp()
    {
        if (!_penPickerOpen) return false;
        if (_penPickOpacityDragging)
        {
            _penPickOpacityDragging = false;
            return true;
        }
        if (!_penPickDragging) return false;
        _penPickDragging = false;
        var c = Hsv.ToRgb(_penPickH, _penPickS, _penPickV);
        if (_pickerText)
            _host.Commands.SetTextColor(c);
        else if (_pickerHighlighter)
            _host.Commands.SetHighlighterSwatchColor(_penPickerSlot, c, rememberRecent: true, normalize: true);
        else
            _host.Commands.SetPenSwatchColor(_penPickerSlot, c, rememberRecent: true);
        return true;
    }

    private Color4 PickerShownColor()
    {
        var picked = Hsv.ToRgb(_penPickH, _penPickS, _penPickV);
        return _pickerHighlighter ? InkPalette.HighlighterBase(picked) : picked;
    }

    /// <summary>进入色值编辑：当前值全选，第一次输入直接替换。</summary>
    private void BeginHexEdit()
    {
        _hexEditing = true;
        _hexBuffer = Hex(PickerShownColor()).TrimStart('#');
        _hexSelectAll = true;
        _hexKeyboardWasOn = _host.State.KeyboardMode;
        if (!_hexKeyboardWasOn) _host.Commands.SetKeyboardMode(true);
        Invalidate();
    }

    private void CommitHexEdit() => FinishHexEdit(apply: true);
    private void CancelHexEdit() => FinishHexEdit(apply: false);

    private void FinishHexEdit(bool apply)
    {
        if (!_hexEditing) return;
        string buffer = _hexBuffer;
        _hexEditing = false;
        _hexBuffer = "";
        _hexSelectAll = false;
        bool restoreKeyboard = !_hexKeyboardWasOn;
        _hexKeyboardWasOn = false;
        if (restoreKeyboard) _host.Commands.SetKeyboardMode(false);

        if (apply)
        {
            var c = ParseHex(buffer);
            if (c.HasValue)
            {
                var baseColor = _pickerHighlighter ? InkPalette.HighlighterBase(c.Value) : c.Value;
                if (_pickerText)
                    _host.Commands.SetTextColor(baseColor);
                else if (_pickerHighlighter)
                    _host.Commands.SetHighlighterSwatchColor(
                        _penPickerSlot, baseColor, rememberRecent: true, normalize: false);
                else
                    _host.Commands.SetPenSwatchColor(_penPickerSlot, baseColor, rememberRecent: true);
                (_penPickH, _penPickS, _penPickV) = Hsv.FromRgb(baseColor);
            }
        }
        Invalidate();
    }

    private static Color4? ParseHex(string text)
    {
        string s = (text ?? "").Trim().TrimStart('#').ToUpperInvariant();
        if (s.Length == 3)
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        if (s.Length != 6
            || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out int v))
            return null;
        return new Color4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
    }

    /// <summary>取色板色值输入框：只吃十六进制需要的键；编辑期间按键不落到画布/热键。</summary>
    public bool HandlePickerKey(int vk, bool ctrl)
    {
        if (_textFontMenuOpen)
        {
            if (vk == 0x1B /*Esc*/) CloseTextFontMenu();
            return true;
        }
        if (!_penPickerOpen) return false;

        // Ctrl+V：色值可以直接粘进来（剪贴板里带不带 # 都行）。
        if (ctrl && vk == 0x56 /*V*/)
        {
            string text = _host.GetClipboardText();
            string hex = FilterHex(text);
            if (hex.Length >= 3)
            {
                if (!_hexEditing) BeginHexEdit();
                _hexBuffer = hex.Length > 6 ? hex[..6] : hex;
                _hexSelectAll = false;
                Invalidate();
                return true;
            }
            return true;
        }

        if (!_hexEditing)
        {
            if (vk == 0x1B /*Esc*/) { ClosePenColorPicker(); return true; }
            return false;
        }

        if (vk == 0x1B /*Esc*/) { CancelHexEdit(); return true; }
        if (vk == 0x0D || vk == 0x0A /*Enter*/) { CommitHexEdit(); return true; }
        if (vk == 0x08 /*Backspace*/)
        {
            if (_hexSelectAll) { _hexBuffer = ""; _hexSelectAll = false; }
            else if (_hexBuffer.Length > 0) _hexBuffer = _hexBuffer[..^1];
            Invalidate();
            return true;
        }

        int digit = vk switch
        {
            >= 0x30 and <= 0x39 => vk - 0x30,          // 0-9
            >= 0x41 and <= 0x46 => vk - 0x41 + 10,     // A-F
            >= 0x60 and <= 0x69 => vk - 0x60,          // 小键盘 0-9
            _ => -1,
        };
        if (digit >= 0)
        {
            if (_hexSelectAll) { _hexBuffer = ""; _hexSelectAll = false; }
            if (_hexBuffer.Length < 6) _hexBuffer += digit.ToString("X");
            Invalidate();
            return true;
        }
        return true;   // 编辑期间吞掉其它键，避免 Ctrl+P 之类把工具切走
    }

    private static string FilterHex(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        Span<char> buf = stackalloc char[Math.Min(6, text.Length)];
        int n = 0;
        foreach (char ch in text)
        {
            int d = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => ch - 'a' + 10,
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => -1,
            };
            if (d < 0) continue;
            if (n >= 6) break;
            buf[n++] = "0123456789ABCDEF"[d];
        }
        return new string(buf[..n]);
    }

    private static string Hex(Color4 c)
    {
        int r = Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
        int b = Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    // ---- 设置页键盘焦点（A0，2026-11-08）------------------------------------
    // 只接管“页面打开时的 Tab/方向键/Enter/Space”，不碰全局快捷键。
    // 键位/焦点语义照 WAI-ARIA APG 的 Dialog/Menu 与 Unity FocusRing：
    // Tab 循环、跳过禁用、Enter/Space 执行、焦点进视口。

    public bool HandleUiKey(int vk, bool ctrl, bool shift, bool alt, bool repeat)
    {
        if (!_moreOpen || _morePage != MorePage.Settings || ctrl || alt) return false;
        EnsureUiActions();
        BuildSettingsFocusTargets();

        switch (vk)
        {
            case 0x09 /*Tab*/:
                if (shift) _focusRing.MovePrev(); else _focusRing.MoveNext();
                break;
            case 0x26 /*Up*/: _focusRing.MoveUp(); break;
            case 0x28 /*Down*/: _focusRing.MoveDown(); break;
            case 0x25 /*Left*/: _focusRing.MoveLeft(); break;
            case 0x27 /*Right*/: _focusRing.MoveRight(); break;
            case 0x0D /*Enter*/:
            case 0x0A /*LF*/:
            case 0x20 /*Space*/:
                if (!repeat) ActivateFocusedSetting();
                break;
            default:
                return false;
        }

        _settingsFocusId = _focusRing.FocusedId;
        EnsureSettingsFocusVisible();
        _settingsFocusVisible = true;
        Invalidate();
        return true;
    }

    private void ActivateFocusedSetting()
    {
        string id = _focusRing.FocusedId;
        if (string.IsNullOrEmpty(id)) return;
        _actions.Execute(id);
        _settingsFocusId = id;
    }

    private void BuildSettingsFocusTargets()
    {
        var targets = new List<FocusTarget>(SettingsCatNames.Length + Rows.Length);
        for (int c = 0; c < SettingsCatNames.Length; c++)
            targets.Add(new FocusTarget(SettingsNavActionId(c), SettingsNavItemRect(c), true));
        for (int i = 0; i < Rows.Length; i++)
            targets.Add(new FocusTarget(RowActionId(Rows[i].Kind), MoreRowRect(i), !Rows[i].Gray));
        _focusRing.SetTargets(targets, _settingsFocusId);
    }

    private RectF RectForFocusId(string id)
    {
        if (string.IsNullOrEmpty(id)) return RectF.Empty;
        for (int c = 0; c < SettingsCatNames.Length; c++)
            if (string.Equals(SettingsNavActionId(c), id, StringComparison.Ordinal))
                return SettingsNavItemRect(c);
        for (int i = 0; i < Rows.Length; i++)
            if (string.Equals(RowActionId(Rows[i].Kind), id, StringComparison.Ordinal))
                return MoreRowRect(i);
        return RectF.Empty;
    }

    private void EnsureSettingsFocusVisible()
    {
        string id = _focusRing.FocusedId;
        if (string.IsNullOrEmpty(id)) return;
        // 左栏分类始终在视口外/固定区域，不要拿它去滚右栏。
        if (id.StartsWith("more.settings.nav.", StringComparison.Ordinal)) return;

        var r = RectForFocusId(id);
        if (r.IsEmpty) return;
        var vp = SettingsViewportRect();
        const float margin = 8f;
        if (r.MinY < vp.MinY + margin)
            ScrollSettingsTo(_settingsScroll - (vp.MinY + margin - r.MinY));
        else if (r.MaxY > vp.MaxY - margin)
            ScrollSettingsTo(_settingsScroll + (r.MaxY - (vp.MaxY - margin)));
    }

    private void DrawSettingsFocusRing(ID2D1DeviceContext ctx)
    {
        var r = RectForFocusId(_focusRing.FocusedId);
        if (r.IsEmpty) return;
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(r.MinX - 1f, r.MinY - 1f, r.MaxX + 1f, r.MaxY + 1f), 5f, 5f);
        ctx.DrawRoundedRectangle(rr, Brush(ctx, Tokens.Accent), 1.5f);
    }

    private void ResetSettingsFocus()
    {
        _settingsFocusId = null;
        _settingsFocusVisible = false;
        _focusRing.Clear();
    }

    private void EnsurePenPickerBitmaps(ID2D1DeviceContext ctx)
    {
        float dpi = _host?.DpiScale ?? 1f;
        int svPix = Math.Max(2, (int)MathF.Round(PenPickSv * dpi));
        int hueW = Math.Max(2, (int)MathF.Round(PenPickHue * dpi));
        int hueH = svPix;
        if (_penPickSvBmp != null && _penPickSvPix == svPix
            && MathF.Abs(_penPickBmpHue - _penPickH) < 0.002f
            && _penPickHueBmp != null && _penPickHuePix == hueW) return;

        _penPickSvBmp?.Dispose(); _penPickSvBmp = null;
        _penPickHueBmp?.Dispose(); _penPickHueBmp = null;
        var props = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96f, 96f, BitmapOptions.None);

        var svBuf = new byte[svPix * svPix * 4];
        for (int y = 0; y < svPix; y++)
            for (int x = 0; x < svPix; x++)
            {
                var c = Hsv.ToRgb(_penPickH, x / (svPix - 1f), 1f - y / (svPix - 1f));
                int o = (y * svPix + x) * 4;
                svBuf[o] = (byte)(c.B * 255f); svBuf[o + 1] = (byte)(c.G * 255f);
                svBuf[o + 2] = (byte)(c.R * 255f); svBuf[o + 3] = 255;
            }
        var hSv = GCHandle.Alloc(svBuf, GCHandleType.Pinned);
        try { _penPickSvBmp = ctx.CreateBitmap(new SizeI(svPix, svPix), hSv.AddrOfPinnedObject(), (uint)(svPix * 4), props); }
        finally { hSv.Free(); }

        var hueBuf = new byte[hueW * hueH * 4];
        for (int y = 0; y < hueH; y++)
        {
            var c = Hsv.ToRgb(y / (hueH - 1f), 1f, 1f);
            for (int x = 0; x < hueW; x++)
            {
                int o = (y * hueW + x) * 4;
                hueBuf[o] = (byte)(c.B * 255f); hueBuf[o + 1] = (byte)(c.G * 255f);
                hueBuf[o + 2] = (byte)(c.R * 255f); hueBuf[o + 3] = 255;
            }
        }
        var hHue = GCHandle.Alloc(hueBuf, GCHandleType.Pinned);
        try { _penPickHueBmp = ctx.CreateBitmap(new SizeI(hueW, hueH), hHue.AddrOfPinnedObject(), (uint)(hueW * 4), props); }
        finally { hHue.Free(); }

        _penPickSvPix = svPix; _penPickHuePix = hueW; _penPickBmpHue = _penPickH;
    }

    private void DrawPenColorPicker(ID2D1DeviceContext ctx, in UiState st)
    {
        var p = PenPickerRect();
        DrawCard(ctx, p, 10f);
        EnsurePenPickerBitmaps(ctx);

        var sv = PenPickerSvRect();
        var hue = PenPickerHueRect();
        if (_penPickSvBmp != null)
            ctx.DrawBitmap(_penPickSvBmp, new Vortice.RawRectF(sv.MinX, sv.MinY, sv.MaxX, sv.MaxY),
                           1f, InterpolationMode.Linear, null, null);
        if (_penPickHueBmp != null)
            ctx.DrawBitmap(_penPickHueBmp, new Vortice.RawRectF(hue.MinX, hue.MinY, hue.MaxX, hue.MaxY),
                           1f, InterpolationMode.Linear, null, null);
        ctx.DrawRectangle(new Vortice.RawRectF(sv.MinX, sv.MinY, sv.MaxX, sv.MaxY), Brush(ctx, BorderCol), 1f);
        ctx.DrawRectangle(new Vortice.RawRectF(hue.MinX, hue.MinY, hue.MaxX, hue.MaxY), Brush(ctx, BorderCol), 1f);

        // SV 游标 + 色相线。
        float sx = sv.MinX + _penPickS * (sv.MaxX - sv.MinX);
        float sy = sv.MinY + (1f - _penPickV) * (sv.MaxY - sv.MinY);
        ctx.DrawEllipse(new Ellipse(new Vector2(sx, sy), 5.5f, 5.5f), Brush(ctx, new Color4(1f, 1f, 1f, 1f)), 2f);
        ctx.DrawEllipse(new Ellipse(new Vector2(sx, sy), 7f, 7f), Brush(ctx, new Color4(0f, 0f, 0f, 0.55f)), 1f);
        float hy = hue.MinY + _penPickH * (hue.MaxY - hue.MinY);
        ctx.DrawLine(new Vector2(hue.MinX + 1f, hy), new Vector2(hue.MaxX - 1f, hy),
                     Brush(ctx, new Color4(1f, 1f, 1f, 1f)), 2f);

        // 当前色预览 + 十六进制。荧光笔先做一次“高亮化”，预览里看到的就是会画出来的基色。
        var picked = Hsv.ToRgb(_penPickH, _penPickS, _penPickV);
        var shown = _pickerHighlighter ? InkPalette.HighlighterBase(picked) : picked;
        var prev = PenPickerPreviewRect();
        var prevR = new RoundedRectangle(new Vortice.RawRectF(prev.MinX, prev.MinY, prev.MaxX, prev.MaxY), 6f, 6f);
        ctx.FillRoundedRectangle(prevR, Brush(ctx, shown));
        ctx.DrawRoundedRectangle(prevR, Brush(ctx, BorderCol), 1f);
        var hexRect = PenPickerHexRect();
        var hb = new RoundedRectangle(
            new Vortice.RawRectF(hexRect.MinX, hexRect.MinY, hexRect.MaxX, hexRect.MaxY), 5f, 5f);
        if (_hexEditing)
        {
            ctx.FillRoundedRectangle(hb, Brush(ctx, new Color4(
                Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, _hexSelectAll ? 0.18f : 0.10f)));
            ctx.DrawRoundedRectangle(hb, Brush(ctx, Tokens.Accent), 1.2f);
        }
        else
        {
            // 平时也带一圈细边：让老师一眼看出这是可点的输入框，不是静态读数。
            ctx.DrawRoundedRectangle(hb, Brush(ctx, BorderCol), 1f);
        }
        _widgets.Text(ctx, _hexEditing ? "#" + _hexBuffer : Hex(shown),
                      hexRect, 11f, Brush(ctx, InkCol), center: true);

        // 吸管按钮。文字取色时不做吸管（吸管写回的是笔的色位），改成一句“文字色”。
        var eye = PenPickerEyeRect();
        var eyeR = new RoundedRectangle(new Vortice.RawRectF(eye.MinX, eye.MinY, eye.MaxX, eye.MaxY), 7f, 7f);
        if (!_pickerText)
        {
            ctx.FillRoundedRectangle(eyeR, Brush(ctx, _dark ? Tokens.HoverDark : Tokens.HoverLight));
            ctx.DrawRoundedRectangle(eyeR, Brush(ctx, BorderCol), 1f);
            _widgets.Text(ctx, "吸管", eye, 12f, Brush(ctx, InkCol));
        }
        else
        {
            ctx.DrawRoundedRectangle(eyeR, Brush(ctx, BorderCol), 1f);
            _widgets.Text(ctx, "文字色", eye, 11.5f, Brush(ctx, MutedCol));
        }

        // ---- 右栏：常用色 + 最近（荧光笔再多一段 5 档浓度）-------------------
        float rx = p.MinX + PenPickRightX;
        float rw = PenPickRightW;
        _widgets.Text(ctx, "常用",
                      new RectF { MinX = rx, MinY = p.MinY + PenPickPad, MaxX = rx + 60f, MaxY = p.MinY + PenPickPad + 15f },
                      11f, Brush(ctx, InkCol), center: false);

        for (int i = 0; i < PickerCommonCount; i++)
        {
            var r = PenPickerCommonRect(i);
            var color = PickerCommonColor(i);
            var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 4f, 4f);
            ctx.FillRoundedRectangle(rr, Brush(ctx, color));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
            if (SameColor(color, shown))
            {
                var ring = new Vortice.RawRectF(r.MinX - 2f, r.MinY - 2f, r.MaxX + 2f, r.MaxY + 2f);
                ctx.DrawRoundedRectangle(new RoundedRectangle(ring, 6f, 6f), Brush(ctx, Tokens.Accent), 1.5f);
            }
        }

        float recentHeaderY = p.MinY + PickerRecentHeaderY;
        float recentY = p.MinY + PickerRecentY;
        float divY = recentHeaderY - 6f;
        ctx.DrawLine(new Vector2(rx, divY), new Vector2(rx + rw, divY), Brush(ctx, BorderCol), 1f);

        _widgets.Text(ctx, "最近",
                      new RectF { MinX = rx, MinY = recentHeaderY, MaxX = rx + 60f, MaxY = recentHeaderY + 15f },
                      11f, Brush(ctx, InkCol), center: false);

        var recent = _pickerHighlighter ? st.RecentHighlighterColors : st.RecentPenColors;
        int n = Math.Min(recent?.Length ?? 0, 8);
        if (n == 0)
        {
            _widgets.Text(ctx, "暂无",
                          new RectF { MinX = rx, MinY = recentY, MaxX = rx + 60f, MaxY = recentY + PenPickCell },
                          9f, Brush(ctx, MutedCol), center: false);
        }
        for (int i = 0; i < n; i++)
        {
            var r = PenPickerRecentRect(i);
            var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 4f, 4f);
            ctx.FillRoundedRectangle(rr, Brush(ctx, recent[i]));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, BorderCol), 1f);
            if (SameColor(recent[i], shown))
            {
                var ring = new Vortice.RawRectF(r.MinX - 2f, r.MinY - 2f, r.MaxX + 2f, r.MaxY + 2f);
                ctx.DrawRoundedRectangle(new RoundedRectangle(ring, 6f, 6f), Brush(ctx, Tokens.Accent), 1.5f);
            }
        }

        if (_pickerHighlighter)
        {
            int selectedSlot = _penPickerSlot;
            int selectedLevel = st.HighlighterSwatchOpacity != null
                && selectedSlot >= 0 && selectedSlot < st.HighlighterSwatchOpacity.Length
                ? st.HighlighterSwatchOpacity[selectedSlot] : 2;

            // 左栏底部：透明度滑条（5 个吸附档）。**不是 5 个色块**，一眼就是滑条。
            float opHeaderY = p.MinY + PenPickOpacityHeaderY;
            _widgets.Text(ctx, "透明度",
                          new RectF
                          {
                              MinX = p.MinX + PenPickPad, MinY = opHeaderY,
                              MaxX = p.MinX + PenPickPad + 80f, MaxY = opHeaderY + 12f,
                          }, 9f, Brush(ctx, MutedCol), center: false);

            var track = PenPickerOpacityTrackRect();
            float left = track.MinX + 6f, right = track.MaxX - 6f;
            float cy = (track.MinY + track.MaxY) * 0.5f;
            ctx.DrawLine(new Vector2(left, cy), new Vector2(right, cy), Brush(ctx, BorderCol), 4f);

            float kx = left + (right - left) * (selectedLevel / 4f);
            ctx.DrawLine(new Vector2(left, cy), new Vector2(kx, cy), Brush(ctx, Tokens.Accent), 3f);

            for (int lv = 0; lv < 5; lv++)
            {
                float tx = left + (right - left) * (lv / 4f);
                ctx.FillEllipse(new Ellipse(new Vector2(tx, cy), lv == selectedLevel ? 2.4f : 1.6f,
                                                                  lv == selectedLevel ? 2.4f : 1.6f),
                                Brush(ctx, lv == selectedLevel ? Tokens.Accent : MutedCol));
            }

            float alpha = InkEngine.InkPalette.HighlighterOpacityLevels[selectedLevel];
            ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 6.5f, 6.5f),
                            Brush(ctx, new Color4(1f, 1f, 1f, 1f)));
            ctx.FillEllipse(new Ellipse(new Vector2(kx, cy), 6.5f, 6.5f),
                            Brush(ctx, new Color4(shown.R, shown.G, shown.B, alpha)));
            ctx.DrawEllipse(new Ellipse(new Vector2(kx, cy), 6.5f, 6.5f), Brush(ctx, BorderCol), 1.5f);

            // 右栏底部：文字预览——白底上先写“高亮预览”，再把当前色按这一档 alpha 盖上去。
            // 比左右两半色条直观：老师看到的是“字被高亮后”的样子。
            float py = p.MinY + PickerPreviewY;
            var pv = new Vortice.RawRectF(rx, py, rx + rw, py + 20f);
            ctx.FillRectangle(pv, Brush(ctx, new Color4(0.99f, 0.99f, 0.98f, 1f)));
            _widgets.Text(ctx, "高亮预览",
                          new RectF { MinX = rx + 6f, MinY = py, MaxX = rx + rw - 6f, MaxY = py + 20f },
                          11f, Brush(ctx, new Color4(0.12f, 0.15f, 0.20f, 1f)), center: true);
            var translucent = new Color4(shown.R, shown.G, shown.B, alpha);
            ctx.FillRectangle(pv, Brush(ctx, translucent));
            ctx.DrawRectangle(pv, Brush(ctx, BorderCol), 1f);
        }
    }

    private void CycleDash()
    {
        var cur = _host.State.Dash;
        var next = cur switch
        {
            StrokeDash.Solid => StrokeDash.Dashed,
            StrokeDash.Dashed => StrokeDash.Dotted,
            _ => StrokeDash.Solid,
        };
        _dashFadeFrom = cur;
        _dashFade.Jump(0f);
        _dashFade.To(1f, Tokens.RailMs);
        _host.Commands.SetDash(next);
        Invalidate();
    }

    private void ActivateSegment(int i)
    {
        // 色带上的动静也算"离开了那一格"：双击序列断掉（同 `Activate` 里那一句）。
        _lastSelCellClickMs = double.NegativeInfinity;
        switch (_bandCell)
        {
            case 2:                       // 白板：[‹] 第 N 屏 [›] │ [白][绿][黑] │ [底纹][间距]
            {
                // 这一格**只管白板的事**：翻页永远是白板的"上一屏 / 下一屏"。
                //
                // ⚠ 2026-09-26 改回来过一次：中间曾让它"放映时变成 PPT 的上一页/下一页"，
                // 用户的结论是**不要**——原话"取消掉白板区的翻页，白板区不需要 ppt 翻页"。
                // 语义上也是对的：这一格是"这块板怎么摆"，不该管幻灯片翻到第几张。
                // PPT 的翻页入口只有**底部那两条**（`PptBar`）＋ PPT 自己的遥控器/键盘。
                //
                // 段序**不写死下标**，一律问 `BoardSegAt`（段表只有 BoardBand 那一份；
                // 2026-09-27 重排之前这里写的是 `i == 0` / `i == 4`，段序一动就会静默点错）。
                var seg = BoardSegAt(i);
                switch (seg.Kind)
                {
                    // 这一格只属于白板。板没开时不能再落到 FlipPage 的文档分支上，
                    // 否则白板色带会把底下的 PDF / 图片翻走。
                    case BoardSegKind.PageUp:
                        if (_host.State.Board) _host.Commands.FlipPage(false);
                        break;
                    case BoardSegKind.PageDown:
                        if (_host.State.Board) _host.Commands.FlipPage(true);
                        break;
                    case BoardSegKind.PageLabel: break;    // 只读的一块，点它什么都不做

                    // 板色 / 底纹 / 间距：都是"要用板"的意思，所以都顺手把板打开
                    //（底纹只在板面上画得出来，不打开就等于改了看不见——这一条原来是靠
                    //  "白板没开就把那两行压暗"来守的，搬进色带之后改成"顺手打开"更好用）。
                    case BoardSegKind.Color:
                        _host.Commands.SetBoardColor(InkPalette.BoardPresets[seg.Idx].Color);
                        _host.Commands.SetBoard(true);
                        break;
                    case BoardSegKind.Pattern:
                    {
                        var st = _host.State;
                        int next = (st.BoardPattern + 1) % PatternNames.Length;
                        _host.Commands.SetBoardPattern(next, st.BoardPatternStep);
                        _host.Commands.SetBoard(true);
                        SavePrefs();
                        Invalidate();
                        break;
                    }
                    case BoardSegKind.Step:
                    {
                        var st = _host.State;
                        int idx = Array.FindIndex(PatternSteps, v => MathF.Abs(v - st.BoardPatternStep) < 0.5f);
                        float next = PatternSteps[(idx + 1 + PatternSteps.Length) % PatternSteps.Length];
                        _host.Commands.SetBoardPattern(st.BoardPattern, next);
                        _host.Commands.SetBoard(true);
                        SavePrefs();
                        Invalidate();
                        break;
                    }
                }
                break;
            }
            case 6:                       // 面积擦 / 整笔擦 —— 引擎里是**两个工具**
                _host.Commands.SetTool(i == 0 ? Tool.PixelEraser : Tool.Eraser);
                break;
            case 8:                       // 矩形框选 / 自由套索
                _host.Commands.SetSelectMode(i == 0 ? SelectMode.Rect : SelectMode.Lasso);
                break;
            case 10:                      // 图形那一格：三行拉平编号，顺序见 ShapeRows
                {
                    var picked = ShapeToolAt(i);
                    // **同一个图形格再点一次 = 换一档**。只对三种图形这样，别的照旧"再点=选中它"：
                    //   · 抛物线：换开口方向（用户 2026-09-20 定："选中框的抛物线按钮取消，
                    //     我不打算从这个转开口" → 朝向改到**画之前**定）；
                    //   · 直线：换线型（用户 2026-09-20 定："点击直线的图标，它会变成虚线，
                    //     再点击变成点虚线，再点击又变成直线……这样就省了好几个空间格"）；
                    //   · 棱柱 / 棱锥 / 棱台：换底面几边形（用户 2026-09-20 定："我想想能不能做成
                    //     像直线切换那样切换三四五六"）。**三格各记各的档**（换谁只动谁），
                    //     判据只有 `ShapeSpec.HasSideCount` 一处——加棱锥 / 棱台时这里差点漏掉。
                    if (picked == Tool.Parabola && _host.State.Tool == Tool.Parabola)
                        _host.Commands.CycleParabolaAxis();
                    else if (picked == Tool.Line && _host.State.Tool == Tool.Line)
                        _host.Commands.CycleLineDash();
                    // 双曲线：换"画不画渐近线"（2026-09-22，用户："增加两挡，有渐近线和无渐近线
                    // ……图标就按照有渐近线和无渐近线"）。
                    else if (picked == Tool.Hyperbola && _host.State.Tool == Tool.Hyperbola)
                        _host.Commands.CycleHyperbolaAsymptotes();
                    // 椭圆（带焦点）：换"画不画焦点三角形"（2026-09-22，用户："椭圆也有两档"）。
                    else if (picked == Tool.ConicEllipse && _host.State.Tool == Tool.ConicEllipse)
                        _host.Commands.CycleEllipseFocusTriangle();
                    // **坐标系：换"要不要网格"**（用户 2026-09-24："我打算把它挪到图形里面的那个
                    // 坐标系……点一下切换成网格，点一下网格没了"）。
                    // 原来这一档在「更多」抽屉里（"坐标系网格"那一行），挪到这一格之后
                    // **和抛物线/直线/双曲线一样是"同一个格再点一次换一档"**——一致，而且
                    // 画之前手指就在这一格上，不用再去抽屉里找。
                    // 落盘只看"改的是不是新画的默认值"（返回值 0 = 是；改已画的对象不算偏好）。
                    else if (picked == Tool.Coordinate && _host.State.Tool == Tool.Coordinate)
                    {
                        if (_host.Commands.ToggleCoordGrid() == 0) SavePrefs();
                    }
                    else if (ShapeSpec.HasSideCount(picked) && _host.State.Tool == picked)
                        _host.Commands.CycleSolidSides();
                    else
                        _host.Commands.SetTool(picked);
                }
                break;
            case 9:                       // 截图：[截图][隐藏窗口截图]（"粘贴图片"已挪到右端的动作按钮）
                // 名字照微信那套（8.3.1）；**点哪一段就用哪种截法进屋**——
                // 段本身是"入口"：`EnterCapture` 一进去就整屏灰下来（详见 IEngineCommands）。
                // 以前点格子就只把工具选中、什么都不发生，"仿微信仿得不像"就是这条。
                _host.Commands.SetCaptureHideInk(i == 1);
                _host.Commands.EnterCapture();
                break;
        }
    }

    // ---- 档位与钉住（「更多」面板「工具条」那一组在用）--------------------

    private int ProfileIndex() => (int)_profile;

    /// <summary>主条 ＋ 上带。</summary>
    private RectF PanelRect()
    {
        var bar = BarRect();
        var band = BandRect();
        if (band.MaxY - band.MinY < 2f) return bar;
        return new RectF
        {
            MinX = bar.MinX, MinY = Math.Min(bar.MinY, band.MinY),
            MaxX = bar.MaxX, MaxY = Math.Max(bar.MaxY, band.MaxY),
        };
    }

    private static bool IsToggleRow(Row kind)
        => kind is Row.DarkTheme or Row.AutoHide or Row.RailPin or Row.DwellShape
           or Row.Pressure or Row.Taper or Row.Predict or Row.Privacy or Row.RestoreInk or Row.PptAutoSave
           or Row.AutoStart or Row.ClearOnArrow;

    private bool IsToggleRow(int i) => IsToggleRow(Rows[i].Kind);

    /// <summary>
    /// **自检用**：这一行到底有没有画开关。
    ///
    /// 为什么专门开一个出口：2026-10-07 加「精细笔迹」那一行时，
    /// 我把行、位置、状态、点击、落盘全接好了，**唯独漏了把它加进 <see cref="IsToggleRow"/>**
    /// —— 于是**标签画出来了、开关没画**，用户看到的是"这一行怎么没有开关"。
    ///
    /// 而当时的自检只做"点一下 → 状态翻转"，**点击判定看的是整行矩形**，
    /// 所以开关画没画它都能过 —— **测试没盖住真正错的地方**。
    /// 现在把"有没有开关"变成一条可断言的事实：以后加开关行漏掉这一处，自检就会红。
    /// </summary>
    internal bool IsToggleRowForTest(int i) => IsToggleRow(i);

    /// <summary>自检用：按标签问"这一行有没有开关"（找不到标签返回 false）。</summary>
    internal bool IsToggleRowByLabelForTest(string label)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Label == label) return IsToggleRow(i);
        return false;
    }

    /// <summary>
    /// 这一行现在是不是压暗（点了没反应）。
    /// 目前只有一种来源：表里写死的（检查更新还没做）。
    ///
    /// ⚠ 2026-09-27 之前还有一条"白板没开时底纹两行压暗"——那两行已经搬进白板色带
    /// （见 <see cref="BoardSegKind"/>），所以这条特例跟着删了。
    /// </summary>
    private bool IsGrayRow(int i) => Rows[i].Gray;

    /// <summary>底纹三档的名字（0/1/2），和引擎那边的取值一一对应。</summary>
    private static readonly string[] PatternNames = { "无", "方格", "横线" };
    /// <summary>
    /// 底纹间距的档位（逻辑像素）。**五档**（2026-09-27 用户："间距可以档位，但需要多几档"）：
    /// 20 最细（密集格子当坐标纸）、40 是默认（＝引擎默认值，出厂就在正中间）、96 最粗（当横线纸）。
    /// ⚠ 引擎那边夹在 8～240 之间（`Engine.SetBoardPatternFromUi`），这几个值都在里面。
    /// ⚠ **最多六档**：档位点是竖排的（点距 5、段高只有 26），第七个就漏出段外了。
    /// </summary>
    private static readonly float[] PatternSteps = { 20f, 30f, 40f, 64f, 96f };

    private static string PatternName(int p) =>
        PatternNames[Math.Clamp(p, 0, PatternNames.Length - 1)];

    // ---- UI 动作表：设置行（A0）--------------------------------------------

    /// <summary>把设置行和左侧分类注册成稳定 Action。重复调用安全。</summary>
    private void EnsureUiActions()
    {
        if (_actionsReady) return;
        _actionsReady = true;

        foreach (var row in Rows)
        {
            var kind = row.Kind;
            _actions.Register(new UiAction
            {
                Id = RowActionId(kind),
                Label = row.Label,
                Hint = row.Hint,
                Kind = RowActionKind(kind),
                IsEnabled = () =>
                {
                    int i = RowIndex(kind);
                    return i >= 0 && !Rows[i].Gray;
                },
                IsChecked = IsToggleRow(kind)
                    ? () =>
                    {
                        int i = RowIndex(kind);
                        return i >= 0 && IsOn(i);
                    }
                    : null,
                Execute = () =>
                {
                    int i = RowIndex(kind);
                    if (i >= 0) LegacyActivateRow(i);
                },
            });
        }

        for (int c = 0; c < SettingsCatNames.Length; c++)
        {
            int cat = c;
            _actions.Register(new UiAction
            {
                Id = SettingsNavActionId(cat),
                Label = SettingsCatNames[cat],
                Kind = UiActionKind.Navigate,
                Execute = () => ScrollSettingsToCategory((SettingsCategory)cat),
            });
        }

        for (int code = 0; code <= 7; code++)
        {
            int c = code;
            var info = HubTileInfo(c);
            _actions.Register(new UiAction
            {
                Id = HubActionId(c),
                Label = info.Label,
                Hint = info.Hint,
                Kind = UiActionKind.Command,
                IsEnabled = () => HubTileInfo(c).Enabled,
                Execute = () => ActivateHub(c),
            });
        }
        foreach (int code in new[] { AiTileCode, HelpTileCode })
        {
            int c = code;
            var info = HubTileInfo(c);
            _actions.Register(new UiAction
            {
                Id = HubActionId(c),
                Label = info.Label,
                Hint = info.Hint,
                Kind = UiActionKind.Command,
                IsEnabled = () => HubTileInfo(c).Enabled,
                Execute = () => ActivateHub(c),
            });
        }

        for (int i = 0; i < MoreBottomTiles.Length; i++)
        {
            int idx = i;
            var info = MoreBottomTiles[idx];
            _actions.Register(new UiAction
            {
                Id = BottomActionId(idx),
                Label = info.Label,
                Hint = info.Hint,
                Kind = UiActionKind.Command,
                Execute = () => ActivateBottom(idx),
            });
        }

        RegisterLogAction(MoreHitLogNote, "问题描述", "写一句遇到的问题；上传/导出会带上它");
        RegisterLogAction(MoreHitLogUpload, "上传日志", "把运行日志传到云端，方便开发排查");
        RegisterLogAction(MoreHitLogExport, "导出 ZIP", "只打包日志，不联网");
        RegisterLogAction(MoreHitLogOlder, "包含更早日志", "连同上一次启动的日志一起打包");
        RegisterLogAction(MoreHitLogGroup, "复制群号", "复制反馈 QQ 群号");
        RegisterLogAction(MoreHitLogGroupUrl, "复制加群链接", "复制能直接唤起 QQ 的加群短链");
        RegisterLogAction(MoreHitLogQr, "群二维码", "整屏显示二维码，手机扫码加群");
    }

    private void RegisterLogAction(int hit, string label, string hint)
    {
        _actions.Register(new UiAction
        {
            Id = LogActionId(hit),
            Label = label,
            Hint = hint,
            Kind = UiActionKind.Command,
            Execute = () => ActivateLog(hit),
        });
    }

    private static int RowIndex(Row kind)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Kind == kind) return i;
        return -1;
    }

    private static string RowActionId(Row kind) => kind switch
    {
        Row.DarkTheme => "settings.general.darkTheme",
        Row.AutoHide => "settings.general.autoHide",
        Row.RailPin => "settings.general.railPin",
        Row.UiSize => "settings.general.uiSize",
        Row.WindowOpacity => "settings.general.windowOpacity",
        Row.Language => "settings.general.language",
        Row.AutoStart => "settings.general.autoStart",
        Row.DwellShape => "settings.writing.dwellShape",
        Row.Pressure => "settings.writing.pressure",
        Row.Taper => "settings.writing.taper",
        Row.Predict => "settings.writing.predict",
        Row.TouchMode => "settings.writing.touchMode",
        Row.RestoreInk => "settings.ink.restoreInk",
        Row.PptAutoSave => "settings.ink.pptAutoSave",
        Row.HistoryDays => "settings.ink.historyDays",
        Row.AiApi => "settings.ai.api",
        Row.BackupAll => "settings.backup.backupAll",
        Row.RestoreAll => "settings.backup.restoreAll",
        Row.Privacy => "settings.about.privacy",
        _ => "settings.unknown." + kind,
    };

    private static UiActionKind RowActionKind(Row kind) => kind switch
    {
        Row.UiSize or Row.WindowOpacity or Row.HistoryDays or Row.Language => UiActionKind.Choice,
        Row.AiApi => UiActionKind.Navigate,
        _ when IsToggleRow(kind) => UiActionKind.Toggle,
        _ => UiActionKind.Command,
    };

    /// <summary>
    /// 窗口透明度点一下就换一档（1 → .85 → .70 → .55 → .40 → 1，到顶回头）。
    /// 引擎立刻生效（合成期属性，下一帧刷到根视觉上）；落盘也由引擎做
    ///（"windowOpacity"，默认档不写项），界面这边不重复存。
    /// </summary>
    private void CycleWindowOpacity() =>
        _host.Commands.SetWindowOpacity(NextWindowOpacity(_host.State.WindowOpacity));

    /// <summary>
    /// 窗口透明度的下一档：1 → .85 → .70 → .55 → .40 → 1（到顶回头）。
    /// 档表必须和引擎 <c>Engine.WindowOpacityPresets</c> 一致（界面拿不到引擎的 internal 常量；
    /// 同 BoardOpacityMin/Max 的做法，不一致由 --settingsaudit / 实测兜住）。
    /// </summary>
    private static float NextWindowOpacity(float cur)
    {
        if (cur > 0.92f) return 0.85f;
        if (cur > 0.77f) return 0.70f;
        if (cur > 0.62f) return 0.55f;
        if (cur > 0.47f) return 0.40f;
        return 1f;
    }

    private void ActivateRow(int i)
    {
        EnsureUiActions();
        int idx = Math.Clamp(i, 0, Rows.Length - 1);
        _actions.Execute(RowActionId(Rows[idx].Kind));
    }

    private void LegacyActivateRow(int i)
    {
        switch (Rows[i].Kind)
        {
            case Row.DarkTheme:
                _dark = !_dark;
                SavePrefs();
                PushFloatingTheme();       // 浮层（操作条/小面板）也得跟着换
                break;
            case Row.AutoHide:
                _hideEnabled = !_hideEnabled;
                _peek.Jump(1f);          // 刚打开时先给个完整的，别一开就缩起来
                SavePrefs();
                break;

            // 色带常开（2026-10-05）：打开时立刻把它摊开；关掉后交回悬停那套（触摸仍有自适应）。
            case Row.RailPin:
                _railPinned = !_railPinned;
                if (_railPinned) _rail.To(1f, Tokens.RailMs);
                SavePrefs();
                break;

            // 悬停提示（2026-10-02 的「功能提示」开关）：**2026-10-16 撤除、改常开**——
            // 逻辑变成"第一次慢、之后快"（Tokens.TipDelayMs / TipFastDelayMs），不再有开关。

            // 界面大小（2026-10-10）：五档循环、到顶回头。引擎立刻生效（重排 + 重画），
            // 界面落盘 "uiscale"——**标准档不写项**（只写和默认不一样的，老规矩）。
            case Row.UiSize:
            {
                float next = UiScalePresets.Next(_host.UiScale);
                _host.SetPref("uiscale", UiScalePresets.StoreValue(next));
                _host.Commands.SetUiScale(next);
                break;
            }

            case Row.WindowOpacity: CycleWindowOpacity(); break;

            // 语言（2026-11-13）：**不再点一下就直接重启**，改成常规下拉。
            // 选中只落盘 `ui.lang`；底部“重启以应用”才重启。不在当前进程里热切换：
            // 文字格式/布局缓存、帮助页、系统对话框都按启动时语言建，重启换取确定性。
            case Row.Language:
                OpenLangMenu();
                break;

            // 停顿成型：翻转开关 → 推给引擎 → 落盘（**只写"关过的"那一份**：
            // 配置里没有这一项就是默认开，以后默认值改了老配置不会把新默认顶掉）。
            case Row.DwellShape:
                _host.Commands.SetDwellShape(!(_host.State.DwellShapeOn));
                SavePrefs();
                break;

            // 压感粗细（2026-10-01）：同一条规矩——引擎是权威，界面翻转后落盘。
            // 关掉是**渲染期**的：整块板立刻等宽，文档里的压力数据不动。
            case Row.Pressure:
                _host.Commands.SetPressure(!_host.State.PressureOn);
                SavePrefs();
                break;

            // 笔锋（2026-11-18）：同一条规矩——引擎是权威，界面翻转后落盘。
            // 只改**显示**：收笔变尖是渲染期的，文档里的墨迹数据不动。
            case Row.Taper:
                _host.Commands.SetTaper(!_host.State.TaperOn);
                SavePrefs();
                break;

            // 墨迹预测（2026-10-09 加；2026-10-16 起默认关）：
            // 引擎是权威，界面翻转后落盘 "predict2"（开过才写 "1"）。
            case Row.Predict:
                _host.Commands.SetPredict(!_host.State.PredictTailOn);
                SavePrefs();
                break;

            // 触摸模式（2026-10-24）：引擎持有 true=手势 / false=单指；
            // 界面不另存偏好，引擎会落 `touch.mode`。
            case Row.TouchMode:
                _host.Commands.SetTouchGestures(!_host.State.TouchGesturesOn);
                break;

            // 匿名设备统计：引擎负责开关状态、设备卡上传/删除；界面只翻转开关。
            case Row.Privacy:
                _host.Commands.SetPrivacyConsent(!_host.State.PrivacyConsentOn);
                break;

            case Row.AiApi:
                OpenApiSettingsPage();
                break;

            // 墨迹三条偏好（原来在「墨迹」页）：只写 `ui.*`，
            // 默认值不落盘（restoreInk 默认关只写 "1"、pptAutoSave 默认开只写 "0"、
            // historyDays 默认永久写成 null = 删项）。
            case Row.RestoreInk:
                _host.SetPref("restoreInk", _host.GetPref("restoreInk") == "1" ? null : "1");
                break;
            case Row.PptAutoSave:
                _host.SetPref("pptAutoSave", _host.GetPref("pptAutoSave") == "0" ? null : "0");
                break;
            case Row.HistoryDays:
                _host.SetPref("historyDays", _host.GetPref("historyDays") switch
                {
                    "90" => "30", "30" => "7", "7" => null, _ => "90",
                });
                break;
            case Row.BackupAll:
                _host.Commands.BackupAllData();
                break;
            case Row.RestoreAll:
                _host.Commands.RestoreFromBackup();
                break;
            case Row.AutoStart:
                _host.Commands.SetAutoStart(!_host.State.AutoStartOn);
                break;
            // 「箭头清屏」：引擎持有并落盘（clearOnArrow）。
            case Row.ClearOnArrow:
                _host.Commands.SetClearOnArrow(!_host.State.ClearOnArrow);
                break;
        }
        Invalidate();
    }

    /// <summary>切档。切完要检查"当前工具还在不在这一档里"——不在就落到笔。</summary>
    private void SetProfile(Profile p)
    {
        _profile = p;
        ClampSettingsScroll();          // 自定义档会多出宫格，内容高度变了
        if (PosOf(CellForTool(_host.State.Tool)) < 0)
            _host.Commands.SetTool(Tool.Pen);
        _hover = -1;
        _press = -1;
        SavePrefs();
        Invalidate();
    }

    /// <summary>钉住 / 取消钉住。只有鼠标和「更多」是安全项；其余工具都开放取消。</summary>
    private void TogglePin(int cell)
    {
        // 下标越界在这里直接挡住：命中编号一旦串段（历史上真发生过一次），
        // 这里是最后一道防线——界面组件不该因为一个下标把小命丢给引擎的异常阶梯。
        if (cell <= 0 || cell >= Cells.Length) return;
        if (!CanUnpin(cell)) return;
        _pinned[cell] = !_pinned[cell];
        SetProfile(Profile.Custom);
    }

    private bool _moreOpen;
    private readonly Anim _more;          // 0 = 关、1 = 全开（兼遮罩透明度）
    private readonly Anim _moreH;         // 面板高度：换页时动画到目标高（不跳）
    private MorePage _morePage => _pageHost?.Current != null
        ? MorePageFromId(_pageHost.Current.Id)
        : MorePage.Home;
    private SettingsCategory _settingsCat = SettingsCategory.Toolbar;

    private enum SettingsCategory { Toolbar = 0, General = 1, Writing = 2, Ink = 3, Ai = 4, Backup = 5, About = 6 }

    /// <summary>设置页右侧是**一整页连续内容**（不是一分类一页）：滚轮/拖动上下滑，
    /// 左侧分类只是"跳到哪一段"。所以滚动位置是页面级状态，不是分类级。</summary>
    private float _settingsScroll;
    private bool _settingsScrollDrag;
    private float _settingsFlingVy;
    private bool _settingsFlingActive;
    private double _settingsFlingLastMs;

    // 设置页双指纵滑（2026-11-07）：设置页是 UI 模态，触摸按下先被 UI 吃掉，
    // 引擎的 TouchGestures 收不到；所以在这里用 PointerId 自己记两根手指。
    private readonly Dictionary<uint, Vector2> _settingsTouches = new();
    private bool _settingsTouchGesture;
    private bool _settingsTouchAxisLocked;
    private bool _settingsTouchScrolling;
    private Vector2 _settingsTouchStartMid;
    private Vector2 _settingsTouchLastMid;
    private double _settingsTouchLastMs;
    private float _settingsTouchVy;

    // ---- UI 动作表（A0，2026-11-08）----------------------------------------
    // 先把设置行的“标题/说明/开关/执行”收成一份；旧 switch 保留为 LegacyActivateRow，
    // 双跑通过后再逐行删。不是第二套命令系统：Execute 仍调原来的业务方法。
    private readonly UiActionTable _actions = new();
    private bool _actionsReady;
    private readonly FocusRing _focusRing = new();
    private string _settingsFocusId;
    private bool _settingsFocusVisible;

    // A1：页面栈由 PageHost 管；现有绘制/命中仍读 _morePage，两者通过 SetMorePage 同步。
    private PageHost _pageHost;

    /// <summary>设置页右侧可视高度（逻辑像素）。面板高稳定，不随分类跳。</summary>
    private const float SettingsBodyH = 476f;

    private static readonly string[] SettingsCatNames =
        { "工具栏", "常规", "书写", "墨迹", "AI 服务", "备份", "关于" };

    private static readonly Row[] SettingsGeneralRows =
        { Row.DarkTheme, Row.AutoHide, Row.RailPin, Row.UiSize, Row.WindowOpacity, Row.Language, Row.AutoStart };
    private static readonly Row[] SettingsWritingRows =
        { Row.DwellShape, Row.Pressure, Row.Taper, Row.Predict, Row.TouchMode, Row.ClearOnArrow };
    private static readonly Row[] SettingsInkRows =
        { Row.RestoreInk, Row.PptAutoSave, Row.HistoryDays };
    private static readonly Row[] SettingsAiRows = { Row.AiApi };
    private static readonly Row[] SettingsBackupRows = { Row.BackupAll, Row.RestoreAll };
    private static readonly Row[] SettingsAboutRows = { Row.Privacy };

    private static Row[] SettingsRows(SettingsCategory cat) => cat switch
    {
        SettingsCategory.General => SettingsGeneralRows,
        SettingsCategory.Writing => SettingsWritingRows,
        SettingsCategory.Ink => SettingsInkRows,
        SettingsCategory.Ai => SettingsAiRows,
        SettingsCategory.Backup => SettingsBackupRows,
        SettingsCategory.About => SettingsAboutRows,
        _ => Array.Empty<Row>(),
    };
    private int _moreHover = MoreHitNone;
    private int _morePress = -1;
    private string _hubHint = "";         // 启动器状态行的临时提示（点置灰格/预留格时写）

    // ---- 语言下拉（2026-11-13）-----------------------------------------------
    // 参考 WinUI 3 ComboBox 的 flyout：点行展开、当前项打勾、点外面只收起、
    // 选中只改偏好，底部“重启以应用”才真正重启。不再点一下直接重启。
    private bool _langMenuOpen;
    private int _langMenuHover = -1;      // 绝对项下标 / MoreHitLangApply
    private int _langMenuPress = -1;      // 按下时的命中码（抬起比对同一个）
    private int _langMenuScroll;          // 第一项可见下标（可见行数不够时滚动）
    private Vector2 _langMenuPressPos;
    private int _langMenuScrollAtPress;
    private bool _langMenuDragged;

    /// <summary>面板两页：启动器（主页）/ 设置子页。**没有页签**——底栏与返回箭头导航。</summary>
    private enum MorePage { Home = 0, Settings = 1, Help = 2, Log = 3, Api = 4, Recent = 5 }

    // 尺度（逻辑像素）。tile 76、底栏同款；面板宽 = min(640, 55% 工作宽)。
    // 规矩见《规范-功能卡.md》§二/§3.4。
    private const float MorePad = 20f;
    private const float MoreHeaderH = 44f;
    private const float MoreGroupGap = 14f;
    private const float MoreGroupHeadH = 24f;
    private const float MoreTile = 76f;
    private const float MoreTileGap = 10f;
    private const float MoreStatusH = 20f;
    private const float MoreProfileH = 34f;
    private const float MoreChipH = 40f;
    private const float MoreChipGap = 8f;
    private const int MoreChipCols = 6;
    private const float MoreRowH = 44f;
    private const float MoreSwitchW = 44f;
    private const float MoreSwitchH = 26f;
    private const float MoreCloseSize = 44f;
    private const float MoreProfileGap = 8f;
    private const float MoreChipTopGap = 10f;

    // 「最近文档」页（2026-11-16 合并入口）：三组各最多 4 条。行高/行距归 MorePanel.cs 的
    // 页面几何常量管（RecentPageRowH / RecentPageRowGap），这里不再有主页那一条的尺寸。
    /// <summary>钉住宫格有几行（按 Cells 数量算；加新格不会再把后面的组头顶穿）。</summary>
    private static int MoreChipRows
        => Math.Max(1, (Cells.Length - 1 + MoreChipCols - 1) / MoreChipCols);
    private static float MoreChipBlockH
        => MoreChipRows * MoreChipH + (MoreChipRows - 1) * MoreChipGap;

    // 命中编号：一整块用**一个整数**编码；绘制/命中/执行都读同一个号。
    private const int MoreHitNone = -1;
    private const int MoreHitInside = 0;      // 面板空白：吃掉，但不关面板
    private const int MoreHitClose = 1;
    private const int MoreHitBack = 2;        // 设置子页返回启动器
    private const int MoreHitProfile = 20;    // 20..22（档位）
    private const int MoreHitChip = 30;       // 30+cell（钉住宫格；只有自定义档显示）
    private const int MoreHitRow = 60;        // 60+Rows 下标（设置子页的行）
    private const int MoreHitTile = 140;      // 140+启动器格子（0..5：计时/点名/分组 保存/打开/回放）
    private const int MoreHitBottom = 200;    // 200+底栏（0..4：设置/日志/检查更新/重启/退出）
    private const int MoreHitRecentPageRow = 220;  // 220..231「最近文档」页：section*4 + row（避开 200..204 与 300+）
    private const int MoreHitLogNote = 300;   // 「日志与反馈」：问题输入框
    private const int MoreHitLogUpload = 301; // 上传日志
    private const int MoreHitLogExport = 302; // 只导出 zip
    private const int MoreHitLogOlder = 303;  // 包含更早日志
    private const int MoreHitLogGroup = 304;  // 复制反馈 QQ 群号（2026-11-03）
    private const int MoreHitLogGroupUrl = 305; // 复制加群链接（2026-11-03）
    private const int MoreHitLogQr = 306;       // 整屏显示群二维码（2026-11-03）
    private const int MoreHitSettingsNav = 320; // 320+设置页左侧分类
    private const int MoreHitSettingsScroll = 340; // 设置页右侧滚动条（拖动）
    // 语言下拉：500+项下标、520 底部“重启以应用”。段位避开 Api 页的 400..459。
    private const int MoreHitLangItem = 500;
    private const int MoreHitLangApply = 520;

    // 语言下拉的尺度（逻辑像素）：行高固定，可见行数按屏幕高度算（见 LangMenuRows）。
    private const float LangMenuW = 248f;
    private const float LangMenuPad = 6f;
    private const float LangMenuHeadH = 30f;
    private const float LangMenuItemH = 32f;
    private const float LangMenuFootH = 42f;
    private static int LangMenuCount => Loc.PrefCodes.Count;

    /// <summary>
    /// 可见行数按屏幕高度算（1.3 倍界面 / 小屏时自动少露几行，改成滚动），
    /// 不再写死 8 行——否则 1366×768 + 1.3 倍下拉会盖住触发它的那一行。
    /// </summary>
    private int LangMenuRows
    {
        get
        {
            var row = MoreRowRect(RowIndex(Row.Language));
            float overhead = LangMenuPad * 2 + LangMenuHeadH + LangMenuFootH;
            float below = _screen.MaxY - 8f - (row.MaxY + 6f);
            float above = row.MinY - 6f - (_screen.MinY + 8f);
            float avail = MathF.Max(below, above);
            int n = (int)MathF.Floor((avail - overhead) / LangMenuItemH);
            return Math.Clamp(n, 4, Math.Min(LangMenuCount, 11));
        }
    }

    private int LangMenuScrollMax => Math.Max(0, LangMenuCount - LangMenuRows);
    private int LangMenuScrollTop => Math.Clamp(_langMenuScroll, 0, LangMenuScrollMax);
    private float LangMenuH
        => LangMenuPad * 2 + LangMenuHeadH + LangMenuRows * LangMenuItemH + LangMenuFootH;

    /// <summary>
    /// 面板宽：**所有 More 页共用**工作区的 55%，夹在 560～980 逻辑像素。
    /// 2026-10-20：说明页不再单独吃接近全屏的宽度；同一面板轮廓/字体/边距，
    /// 不同分辨率下屏幕占比保持相对稳定。宽度是 UI 逻辑单位，界面大小档位
    /// 只改变内部字号/控件，不把面板轮廓无限放大。
    /// </summary>
    private float MoreWidth()
    {
        float workW = _work.MaxX - _work.MinX;
        return Math.Clamp(workW * 0.55f, 560f, 980f);
    }

    private float MoreTargetH()
    {
        // A1：页面高度优先由 PageHost 当前页的 PreferredSize 决定；
        // 旧 _morePage switch 只在 PageHost 还没建立时兜底（测试/初始化顺序）。
        var page = _pageHost?.Current;
        if (page != null && page.PreferredSize.Height > 0f)
            return page.PreferredSize.Height;
        return _morePage switch
        {
            MorePage.Home => MoreHomeH(),
            MorePage.Settings => MoreSettingsH(),
            MorePage.Log => MoreLogH(),
            MorePage.Recent => MoreRecentPageH(),
            _ => MoreHelpH(),
        };
    }

    // ---- A1：PageHost 适配（现有绘制/命中仍读 _morePage）-------------------

    private void EnsurePageHost()
    {
        if (_pageHost != null) return;
        _pageHost = new PageHost(new PageHostMetrics
        {
            WidthRatio = 0.55f,
            MinWidth = 560f,
            MaxWidth = 980f,
            DefaultHeight = 570f,
            MaxHeightMargin = 24f,
        }, () => _host?.NowMs ?? 0d);
        if (_host != null) _pageHost.Bind(_host);
        _pageHost.SetWorkArea(_work);
    }

    private static string LegacyPageId(MorePage page) => page switch
    {
        MorePage.Settings => "more.settings",
        MorePage.Help => "more.help",
        MorePage.Log => "more.log",
        MorePage.Api => "more.api",
        MorePage.Recent => "more.recent",
        _ => "more.home",
    };

    /// <summary>
    /// 切页：先同步 PageHost 栈，再让现有 _morePage 读同一页。
    /// Home 重置栈；从 Home 进子页用 Push；子页之间切换用 Replace。
    /// </summary>
    private void SetMorePage(MorePage page, bool push)
    {
        CloseLangMenu();          // 换页/返回一律收起下拉（它属于“语言”行）
        EnsurePageHost();
        if (_pageHost == null) return;

        string id = LegacyPageId(page);
        string current = _pageHost.Current?.Id;
        if (page == MorePage.Home)
        {
            if (current != id) _pageHost.Open(CreatePage(page), animate: false);
            else _pageHost.Layout();
            return;
        }

        if (current == id)
        {
            _pageHost.Layout();
            return;
        }

        IPage next = CreatePage(page);
        if (current == null) _pageHost.Open(next, animate: false);
        else if (push) _pageHost.Push(next);
        else _pageHost.Replace(next);
    }

    private static MorePage MorePageFromId(string id) => id switch
    {
        "more.settings" => MorePage.Settings,
        "more.help" => MorePage.Help,
        "more.log" => MorePage.Log,
        "more.api" => MorePage.Api,
        "more.recent" => MorePage.Recent,
        _ => MorePage.Home,
    };

    private void GoBackMorePage()
    {
        CloseLangMenu();
        EnsurePageHost();
        _host?.TextInput?.Commit();       // 返回前先把正在编辑的字段提交
        if (_pageHost != null && _pageHost.Count > 1)
            _pageHost.Pop();
        else
            _pageHost?.Open(CreatePage(MorePage.Home), animate: false);
    }

    // ---- 日志与反馈页 --------------------------------------------------------
    private const float LogNoteH = 58f;
    private const float LogButtonH = 44f;
    private const float LogToggleH = 36f;
    /// <summary>日志页底部"加群反馈"那一行（2026-11-03）。</summary>
    private const float LogGroupH = 42f;

    private float MoreLogH()
        => MorePad * 2 + MoreHeaderH + 10
           + 20 + LogNoteH + 12
           + 36 + LogToggleH + 14
           + LogButtonH + 12 + MoreStatusH
           + 14 + LogGroupH;

    private bool _logIncludeOlder;

    /// <summary>日志页右侧的二维码卡片（2026-11-03）：左边表单，右边一张能扫的图。</summary>
    private const float LogQrW = 190f;
    private const float LogQrGap = 16f;
    private float LogLeftRight() => MoreLeftX() + MoreContentW() - LogQrW - LogQrGap;

    private RectF LogNoteRect() => new()
    {
        MinX = MoreLeftX(), MinY = MoreHeaderRect().MaxY + 30f,
        MaxX = LogLeftRight(), MaxY = MoreHeaderRect().MaxY + 30f + LogNoteH,
    };

    private RectF LogScopeRect() => new()
    {
        MinX = MoreLeftX(), MinY = LogNoteRect().MaxY + 8f,
        MaxX = LogLeftRight(), MaxY = LogNoteRect().MaxY + 8f + 34f,
    };

    private RectF LogToggleRect() => new()
    {
        MinX = MoreLeftX(), MinY = LogScopeRect().MaxY + 8f,
        MaxX = LogLeftRight(), MaxY = LogScopeRect().MaxY + 8f + LogToggleH,
    };

    private RectF LogUploadRect()
    {
        var t = LogToggleRect();
        float w = MathF.Min(190f, (MoreContentW() - 10f) * 0.58f);
        return new RectF
        {
            MinX = t.MaxX - w, MinY = t.MaxY + 14f,
            MaxX = t.MaxX, MaxY = t.MaxY + 14f + LogButtonH,
        };
    }

    private RectF LogExportRect()
    {
        var u = LogUploadRect();
        float w = MathF.Min(150f, MoreContentW() - (u.MaxX - u.MinX) - 20f);
        return new RectF
        {
            MinX = u.MinX - 10f - w, MinY = u.MinY,
            MaxX = u.MinX - 10f, MaxY = u.MaxY,
        };
    }

    /// <summary>日志页最底下一行：加 QQ 群反馈（2026-11-03）。</summary>
    private RectF LogGroupRect()
    {
        float top = LogUploadRect().MaxY + 10f + MoreStatusH + 14f;
        return new RectF
        {
            MinX = MoreLeftX(), MinY = top,
            MaxX = MoreLeftX() + MoreContentW(), MaxY = top + LogGroupH,
        };
    }

    /// <summary>日志页右侧的二维码卡片：从备注框顶端到状态行底端。</summary>
    private RectF LogQrCardRect()
    {
        float top = LogNoteRect().MinY;
        float bottom = LogUploadRect().MaxY + 10f + MoreStatusH;
        return new RectF
        {
            MinX = MoreLeftX() + MoreContentW() - LogQrW,
            MinY = top,
            MaxX = MoreLeftX() + MoreContentW(),
            MaxY = bottom,
        };
    }

    /// <summary>那一行右端的小动作：复制群号 / 复制链接（最右）。</summary>
    private const float LogGroupBtnW = 72f;
    private const float LogGroupBtnGap = 8f;
    private RectF LogGroupUrlRect()
    {
        var g = LogGroupRect();
        return new RectF
        {
            MinX = g.MaxX - 12f - LogGroupBtnW, MinY = g.MinY + 8f,
            MaxX = g.MaxX - 12f, MaxY = g.MaxY - 8f,
        };
    }
    private RectF LogGroupCopyRect()
    {
        var u = LogGroupUrlRect();
        return new RectF
        {
            MinX = u.MinX - LogGroupBtnGap - LogGroupBtnW, MinY = u.MinY,
            MaxX = u.MinX - LogGroupBtnGap, MaxY = u.MaxY,
        };
    }

    /// <summary>启动器高：两组（课堂/墨迹）各若干行 tile ＋ 状态行 ＋ 底栏一行。
    /// ⚠ 每组的行数要用 <see cref="HubRows"/>（宽度决定）真实算——旧式子按
    /// `(头高+格高+组距)` 估的、漏了每行的**格距**（MoreTileGap），面板比内容矮
    /// 2×格距，底行正好贴死在面板底边（2026-10-09 用户报"最下面一行贴着底边、
    /// 下边框被切"，出图量出下边距=0）。
    ///
    /// 2026-11-16：「最近文档」不再挂在主页上（改到第 7 格的独立页），
    /// 主页高度回到**固定值**——有没有最近文档都一样高。</summary>
    private float MoreHomeH() => MoreHomeFixedH();

    /// <summary>启动器的固定部分（不含「最近文档」）：0 条最近文档时面板就是它。</summary>
    private float MoreHomeFixedH()
        => MorePad * 2 + MoreHeaderH + 8
           + MoreGroupHeadH + HubRows(0) * (MoreTile + MoreTileGap) + MoreGroupGap
           + MoreGroupHeadH + HubRows(1) * (MoreTile + MoreTileGap) + MoreGroupGap
           + MoreStatusH + 8 + MoreTile;

    private const float SettingsNavW = 168f;
    private const float SettingsNavItemH = 44f;
    private const float SettingsNavGap = 5f;
    private const float SettingsNavTop = 4f;
    private const float SettingsNavTextInset = 14f;

    private float SettingsContentTop()
        => MoreRect().MinY + MorePad + MoreHeaderH + 8f;

    private float SettingsViewportTop() => SettingsContentTop();
    private float SettingsViewportBottom() => MoreRect().MaxY - MorePad;
    private float SettingsViewportH()
        => MathF.Max(80f, SettingsViewportBottom() - SettingsViewportTop());
    private RectF SettingsViewportRect() => new()
    {
        MinX = SettingsContentLeft(),
        MinY = SettingsViewportTop(),
        MaxX = SettingsContentLeft() + SettingsContentW(),
        MaxY = SettingsViewportBottom(),
    };

    private float SettingsNavLeft() => MoreLeftX();
    private float SettingsContentLeft() => MoreLeftX() + SettingsNavW + 16f;
    private float SettingsContentW() => MathF.Max(120f, MoreContentW() - SettingsNavW - 16f);

    /// <summary>每个分类在**连续内容**里的高度（含组头、行、组后距）。</summary>
    private float SettingsSectionH(SettingsCategory cat)
    {
        if (cat == SettingsCategory.Toolbar)
            return MoreGroupHeadH + MoreProfileH + MoreChipTopGap
                   + (ChipsShown ? MoreChipBlockH : 42f) + MoreGroupGap;
        return MoreGroupHeadH + SettingsRows(cat).Length * MoreRowH + MoreGroupGap;
    }

    /// <summary>分类在内容坐标系里的起点（0 = 内容顶部）。</summary>
    private float SettingsSectionTop(SettingsCategory cat)
    {
        float y = 0f;
        for (int c = 0; c < (int)cat; c++)
            y += SettingsSectionH((SettingsCategory)c);
        return y;
    }

    private float SettingsContentH()
    {
        float y = 0f;
        for (int c = 0; c < SettingsCatNames.Length; c++)
            y += SettingsSectionH((SettingsCategory)c);
        return y;
    }

    private float SettingsScrollMax()
        => MathF.Max(0f, SettingsContentH() - SettingsViewportH());

    private void ClampSettingsScroll()
        => _settingsScroll = Math.Clamp(_settingsScroll, 0f, SettingsScrollMax());

    /// <summary>内容坐标 → 屏幕坐标（右侧视口顶部 + 内容 y − 滚动）。</summary>
    private float SettingsY(float contentY)
        => SettingsViewportTop() + contentY - _settingsScroll;

    private RectF SettingsScrollbarRect() => new()
    {
        MinX = SettingsContentLeft() + SettingsContentW() + 4f,
        MinY = SettingsViewportTop(),
        MaxX = SettingsContentLeft() + SettingsContentW() + 10f,
        MaxY = SettingsViewportBottom(),
    };

    /// <summary>命中区比视觉宽一圈：触摸时 6px 的细条很难抓。</summary>
    private RectF SettingsScrollbarHitRect()
    {
        var r = SettingsScrollbarRect();
        return new RectF { MinX = r.MinX - 4f, MinY = r.MinY, MaxX = r.MaxX + 2f, MaxY = r.MaxY };
    }

    private RectF SettingsScrollThumbRect()
    {
        float max = SettingsScrollMax();
        if (max <= 0.5f) return RectF.Empty;
        var bar = SettingsScrollbarRect();
        float track = bar.MaxY - bar.MinY;
        float thumb = MathF.Max(32f, track * SettingsViewportH() / SettingsContentH());
        float t = _settingsScroll / max;
        float y = bar.MinY + t * (track - thumb);
        return new RectF { MinX = bar.MinX, MinY = y, MaxX = bar.MaxX, MaxY = y + thumb };
    }

    /// <summary>滚到哪一段 → 左侧分类高亮跟着走（不是反过来）。
    /// 滚到最底时高亮最后一段——否则"关于"永远高亮不到。</summary>
    private void UpdateSettingsActiveFromScroll()
    {
        var max = SettingsScrollMax();
        SettingsCategory active;
        if (max > 0.5f && _settingsScroll >= max - 1f)
        {
            active = (SettingsCategory)(SettingsCatNames.Length - 1);
        }
        else
        {
            float y = _settingsScroll + 1f;
            active = SettingsCategory.Toolbar;
            for (int c = 0; c < SettingsCatNames.Length; c++)
            {
                if (SettingsSectionTop((SettingsCategory)c) > y) break;
                active = (SettingsCategory)c;
            }
        }
        if (active != _settingsCat)
        {
            _settingsCat = active;
            Invalidate();
        }
    }

    private void ScrollSettingsTo(float scroll)
    {
        CloseLangMenu();                    // 设置页一滚，下拉就离开它那一行
        _settingsFocusVisible = false;      // 滚动后焦点环位置会变；下次键盘再显示
        _settingsScroll = Math.Clamp(scroll, 0f, SettingsScrollMax());
        UpdateSettingsActiveFromScroll();
        Invalidate();
    }

    /// <summary>点左侧分类：**高亮必须停在被点的那个**。
    /// 最后一段很短时，滚到它也只能滚到 max，按"视口顶部"算会跳回上一段（用户点了"关于"却亮"书写"）。</summary>
    private void ScrollSettingsToCategory(SettingsCategory cat)
    {
        CloseLangMenu();
        StopSettingsFling();
        _settingsCat = cat;
        _settingsScroll = Math.Clamp(SettingsSectionTop(cat), 0f, SettingsScrollMax());
        Invalidate();
    }

    /// <summary>拖动滚动条：按滑块中心映射到滚动位置。</summary>
    private void DragSettingsScrollbar(float y)
    {
        float max = SettingsScrollMax();
        if (max <= 0.5f) return;
        var bar = SettingsScrollbarRect();
        var thumb = SettingsScrollThumbRect();
        float track = bar.MaxY - bar.MinY;
        float thumbH = thumb.MaxY - thumb.MinY;
        float t = (y - bar.MinY - thumbH * 0.5f) / MathF.Max(1f, track - thumbH);
        ScrollSettingsTo(Math.Clamp(t, 0f, 1f) * max);
    }

    /// <summary>
    /// 双指纵滑（2026-11-07）：设置页是"整页连续滚动"，交给它接住；
    /// 其它界面不消费，继续走画布的双指漫游。
    /// </summary>
    public bool TouchScroll(in UiTouchScrollEvent e)
    {
        if (!_moreOpen) return false;
        if (e.Phase == UiTouchScrollPhase.Cancel)
        {
            StopSettingsFling();
            return true;
        }

        if (_morePage == MorePage.Settings)
        {
            var vp = SettingsViewportRect();
            if (e.Phase == UiTouchScrollPhase.Begin)
            {
                StopSettingsFling();
                return true;          // 设置页是模态：消费掉，别让底下的画布跟着滚
            }
            if (e.Phase == UiTouchScrollPhase.Move)
            {
                if (vp.Contains(e.X, e.Y))
                    ScrollSettingsTo(_settingsScroll - e.Dy);
                return true;
            }
            if (e.Phase == UiTouchScrollPhase.End)
            {
                if (vp.Contains(e.X, e.Y)) StartSettingsFling(e.Vy);
                return true;
            }
            return true;
        }

        // 其它“更多”页也是模态：能滚的帮助页滚自己的内容，其余吞掉，别滚底下的画布。
        if (_morePage == MorePage.Help && e.Phase == UiTouchScrollPhase.Move)
            ScrollHelp(-e.Dy);
        return true;
    }

    private void StartSettingsFling(float vy)
    {
        if (SettingsScrollMax() <= 0.5f) { StopSettingsFling(); return; }
        if (MathF.Abs(vy) < UiTouchFling.StopVelocity) { StopSettingsFling(); return; }
        _settingsFlingVy = Math.Clamp(vy, -UiTouchFling.MaxVelocity, UiTouchFling.MaxVelocity);
        _settingsFlingActive = true;
        _settingsFlingLastMs = _host?.NowMs ?? 0;
        Invalidate();
    }

    private void StepSettingsFling()
    {
        if (!_settingsFlingActive) return;
        double now = _host?.NowMs ?? 0;
        double dt = Math.Clamp(now - _settingsFlingLastMs, 0, UiTouchFling.MaxStepMs);
        _settingsFlingLastMs = now;
        if (dt <= 0) return;
        _settingsFlingVy *= MathF.Exp(-UiTouchFling.DecayPerSecond * (float)(dt / 1000.0));
        if (MathF.Abs(_settingsFlingVy) < UiTouchFling.StopVelocity)
        {
            StopSettingsFling();
            return;
        }
        float before = _settingsScroll;
        ScrollSettingsTo(_settingsScroll - _settingsFlingVy * (float)dt);
        if (MathF.Abs(_settingsScroll - before) < 0.01f)
            StopSettingsFling();
    }

    private void StopSettingsFling()
    {
        if (!_settingsFlingActive) { _settingsFlingVy = 0f; return; }
        _settingsFlingActive = false;
        _settingsFlingVy = 0f;
        Invalidate();
    }

    // ---- 设置页双指纵滑（UI 自己记两根手指；见字段注释）----------------------

    private void ResetSettingsTouch()
    {
        _settingsTouches.Clear();
        _settingsTouchGesture = false;
        _settingsTouchAxisLocked = false;
        _settingsTouchScrolling = false;
        _settingsTouchVy = 0f;
        StopSettingsFling();
    }

    /// <summary>返回 true = 这一次按下已经归双指手势，不再走行点击。</summary>
    private bool SettingsTouchDown(in UiPointerEvent e, Vector2 p)
    {
        if (!_moreOpen || !e.FromTouch) return false;
        _settingsTouches[e.PointerId] = new Vector2(p.X, p.Y);
        if (_settingsTouches.Count < 2) return false;

        StopSettingsFling();
        _settingsTouchGesture = true;
        _settingsTouchAxisLocked = false;
        _settingsTouchScrolling = false;
        _settingsTouchVy = 0f;
        _settingsTouchStartMid = _settingsTouchLastMid = SettingsTouchMid();
        _settingsTouchLastMs = _host?.NowMs ?? 0;
        _morePress = -1;
        _moreHover = MoreHitNone;
        Invalidate();
        return true;
    }

    /// <summary>返回 true = 这一次移动归双指手势，不再做悬停/行拖动。</summary>
    private bool SettingsTouchMove(in UiPointerEvent e, Vector2 p)
    {
        if (!_settingsTouchGesture || !e.FromTouch) return false;
        if (!_settingsTouches.ContainsKey(e.PointerId)) return true;
        _settingsTouches[e.PointerId] = new Vector2(p.X, p.Y);

        var mid = SettingsTouchMid();
        if (!_settingsTouchAxisLocked)
        {
            var total = mid - _settingsTouchStartMid;
            if (total.Length() < 10f) return true;
            if (MathF.Abs(total.X) >= MathF.Abs(total.Y) * 1.25f)
            {
                _settingsTouchAxisLocked = true;
                _settingsTouchScrolling = false;   // 横滑：不滚、也不翻页
                return true;
            }
            if (MathF.Abs(total.Y) >= MathF.Abs(total.X) * 1.25f)
            {
                _settingsTouchAxisLocked = true;
                _settingsTouchScrolling = true;
            }
            else return true;
        }

        if (!_settingsTouchScrolling)
        {
            _settingsTouchLastMid = mid;
            return true;
        }

        float dy = mid.Y - _settingsTouchLastMid.Y;
        _settingsTouchLastMid = mid;
        double dt = Math.Clamp((_host?.NowMs ?? 0) - _settingsTouchLastMs, 1, 40);
        _settingsTouchLastMs = _host?.NowMs ?? 0;
        if (MathF.Abs(dy) > 0.01f)
        {
            // 手指往下 = 内容往下 = scroll 减小；和白板 ScrollCanvasBy 的符号一致。
            if (_morePage == MorePage.Settings)
                ScrollSettingsTo(_settingsScroll - dy);
            else if (_morePage == MorePage.Help)
                ScrollHelp(-dy);
            _settingsTouchVy = Math.Clamp(dy / (float)dt, -UiTouchFling.MaxVelocity, UiTouchFling.MaxVelocity);
        }
        return true;
    }

    /// <summary>返回 true = 这一次抬起归双指手势，不再执行行点击。</summary>
    private bool SettingsTouchUp(in UiPointerEvent e)
    {
        if (!e.FromTouch) return false;
        bool tracked = _settingsTouches.Remove(e.PointerId);
        if (!_settingsTouchGesture) return false;   // 单指：让正常点击/长按收尾
        if (tracked && _settingsTouchScrolling && _settingsTouches.Count < 2)
        {
            if (_morePage == MorePage.Settings) StartSettingsFling(_settingsTouchVy);
            _settingsTouchScrolling = false;
        }
        if (_settingsTouches.Count == 0)
        {
            _settingsTouchGesture = false;
            _settingsTouchAxisLocked = false;
            _settingsTouchScrolling = false;
            _settingsTouchVy = 0f;
        }
        return true;
    }

    private Vector2 SettingsTouchMid()
    {
        Vector2 sum = Vector2.Zero;
        int n = 0;
        foreach (var p in _settingsTouches.Values) { sum += p; n++; }
        return n > 0 ? sum / n : Vector2.Zero;
    }

    private RectF SettingsNavItemRect(int i)
    {
        float x = SettingsNavLeft();
        float y = SettingsContentTop() + SettingsNavTop + i * (SettingsNavItemH + SettingsNavGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + SettingsNavW, MaxY = y + SettingsNavItemH };
    }

    private int SettingsNavAt(float x, float y)
    {
        for (int i = 0; i < SettingsCatNames.Length; i++)
            if (SettingsNavItemRect(i).Contains(x, y)) return i;
        return -1;
    }

    /// <summary>设置子页高：**固定**（右侧连续滚动），不随当前分类跳。</summary>
    private float MoreSettingsH()
        => MorePad * 2 + MoreHeaderH + 8f + SettingsBodyH;

    /// <summary>钉住宫格**只在自定义档**显示（用户 2026-10-02："老是占地方"）。</summary>
    private bool ChipsShown => _profile == Profile.Custom;

    /// <summary>面板矩形：工作区正中、夹进屏幕；高度用 `_moreH` 动画（换页不跳）。</summary>
    private RectF MoreRect()
    {
        float h = MathF.Min(_moreH.Value <= 0f ? MoreTargetH() : _moreH.Value,
                            MathF.Max(200f, (_work.MaxY - _work.MinY) - 16f));
        float w = MathF.Min(MoreWidth(), MathF.Max(240f, (_work.MaxX - _work.MinX) - 16f));
        float cx = (_work.MinX + _work.MaxX) * 0.5f;
        float cy = (_work.MinY + _work.MaxY) * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };
        const float m = 8f;
        if (r.MinX < _screen.MinX + m) { r.MaxX += _screen.MinX + m - r.MinX; r.MinX = _screen.MinX + m; }
        if (r.MaxX > _screen.MaxX - m) { r.MinX -= r.MaxX - (_screen.MaxX - m); r.MaxX = _screen.MaxX - m; }
        if (r.MinY < _screen.MinY + m) { r.MaxY += _screen.MinY + m - r.MinY; r.MinY = _screen.MinY + m; }
        if (r.MaxY > _screen.MaxY - m) { r.MinY -= r.MaxY - (_screen.MaxY - m); r.MaxY = _screen.MaxY - m; }
        return r;
    }

    private float MoreContentW() => MoreRect().MaxX - MoreRect().MinX - MorePad * 2f;
    private float MoreLeftX() => MoreRect().MinX + MorePad;

    private RectF MoreHeaderRect() => new()
    {
        MinX = MoreLeftX(), MinY = MoreRect().MinY + MorePad,
        MaxX = MoreLeftX() + MoreContentW(), MaxY = MoreRect().MinY + MorePad + MoreHeaderH,
    };

    private RectF MoreCloseRect() => new()
    {
        MinX = MoreHeaderRect().MaxX - MoreCloseSize, MinY = MoreHeaderRect().MinY,
        MaxX = MoreHeaderRect().MaxX, MaxY = MoreHeaderRect().MinY + MoreCloseSize,
    };

    private RectF MoreBackRect() => new()
    {
        MinX = MoreHeaderRect().MinX, MinY = MoreHeaderRect().MinY,
        MaxX = MoreHeaderRect().MinX + MoreCloseSize, MaxY = MoreHeaderRect().MinY + MoreCloseSize,
    };

}
