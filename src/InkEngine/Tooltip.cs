using System.Numerics;

namespace InkEngine;

/// <summary>
/// 引擎侧的悬停提示（Tooltip；2026-10-02）。
///
/// 界面层（InkUi）有自己的那一套（主条/上带/更多面板）；这一份服务**引擎自己画的浮层**：
///   · 选中操作条十格（图标-only，最需要说明）；
///   · PPT 控件条（页码格"长按出菜单"、◀▶）与长按菜单四项。
///
/// 规矩和界面层完全一致（规格见《调研-悬停提示-Tooltip.md》）：
///   · 停留 <see cref="TipDelayMs"/> 才出；**出过提示后的 <see cref="TipBetweenWindowMs"/> 窗口内
///     下一个提示只等 <see cref="TipFastDelayMs"/>**（WPF 的 InitialShowDelay/BetweenShowDelay，
///     2026-10-16 加）；换块重新计时；移开 / 按下 / 拖动 / 穿透立刻收；
///   · 内容 = 名称 + 键位 + 一句说明；**键位从 <see cref="KeyMap"/> 查**，引擎里不抄第二份；
///   · 画在锚点（物理像素、和浮层同一套屏幕坐标）的上方，顶到屏幕就翻到下方。
///   · **常开**（原「功能提示」设置 2026-10-16 撤除）。
///
/// 谁在什么时机调：
///   · 指针移动的悬停路径里，各浮层的 hover 状态更新完之后调 <see cref="UpdateEngineTooltip"/>；
///   · 主循环每帧调 <see cref="StepEngineTooltip"/> 推延迟（两档）；
///   · 指针按下/离开、穿透、写字中 → <see cref="ClearEngineTooltip"/>。
/// 绘制与脏区在 Overlay.cs（那里才有 DirectWrite 量文字）。
/// </summary>
public partial class InkEngine
{
    /// <summary>停留多久才出提示（毫秒）——**第一次 / 隔久了**走它。出处：WPF
    /// `ToolTipService.InitialShowDelay`（默认 400）；我们取 500 给"扫过一排"留余量。</summary>
    private const double TipDelayMs = 500;

    /// <summary>"刚出过提示"之后，下一个提示的**快延迟**（毫秒）。出处：WPF
    /// `ToolTipService.BetweenShowDelay` 的语义——**出现过提示之后的一段时间内，
    /// 下一个提示不再等首次延迟**。WPF 在窗口内是零延迟；我们的浮层格子排得密，
    /// 零延迟会在扫过时连续闪提示，取 150（仍远小于 500）。
    /// （2026-10-16 用户定："第一次提示时间长一些，后面再提示就会快一些"。）</summary>
    private const double TipFastDelayMs = 150;

    /// <summary>"刚出过提示"的窗口（毫秒）。出处：WPF 官方 `BetweenShowDelay` 示例值 2000。</summary>
    private const double TipBetweenWindowMs = 2000;

    private int _tipId;                         // 0 = 没有；其余由各浮层给（只需唯一）
    private string _tipTitle, _tipKey, _tipNote;
    private RectF _tipAnchor;                    // 物理像素、和浮层同一套屏幕坐标
    private double _tipSinceMs;
    private bool _tipShown;
    /// <summary>上次**真的出过提示**的时刻（"刚看过提示"窗口用，见 <see cref="TipFastDelayMs"/>）。
    /// 亮着期间由 <see cref="StepEngineTooltip"/> 持续刷新——"刚收起"也算"刚看过"。</summary>
    private double _lastTipShownAtMs = double.NegativeInfinity;

    /// <summary>这一刻提示真的亮着（Overlay 用来画卡 + 算脏区）。</summary>
    internal bool TooltipShown => _tipId != 0 && _tipShown;

    /// <summary>还在等延迟（主循环据此要帧，否则"到点了"没人去点亮它）。</summary>
    internal bool TooltipPending => _tipId != 0 && !_tipShown;

    internal string TooltipTitle => _tipTitle;
    internal string TooltipKey => _tipKey;
    internal string TooltipNote => _tipNote;
    internal RectF TooltipAnchorNow => _tipAnchor;

    /// <summary>虚拟屏幕矩形（物理像素）——给 Overlay 把提示卡夹在屏幕里用。</summary>
    internal RectF TipScreenNow => ScreenRectPhysical();

    /// <summary>某一刻指针停在哪一块（由各浮层的 hover 状态算出来）。</summary>
    internal void UpdateEngineTooltip()
    {
        if (PassThrough || CaptureActive
            || _drawing || PptBarDragging)
        {
            ClearEngineTooltip();
            return;
        }

        // ---- ① PPT 条 / 长按菜单 / 页号面板 ----
        if (PptMode && PptBarHover >= 0)
        {
            if (PptBarHover >= 100 && PptBarHover < 100 + PptMenuItemCount)
            {
                int i = PptBarHover - 100;
                var (t, n) = PptMenuTip(i);
                PptMenuItemRectAt(i, out var item);
                EngineTooltipHover(100 + i, t, null, n, item);
                return;
            }
            if (PptBarHover >= 200)
            {
                // 页号面板里的格子：数字本身就是说明，不出提示
                ClearEngineTooltip();
                return;
            }
            var bar = PptBarRect();
            var mid = new RectF
            {
                MinX = bar.MinX + PptBar.ArrowW * DpiScale, MinY = bar.MinY,
                MaxX = bar.MaxX - PptBar.ArrowW * DpiScale, MaxY = bar.MaxY,
            };
            // ◀ ▶ 不配提示：箭头一看就知道，而且放映时它们**按下即翻页**（收窄后的判据：
            // 点一下就知道结果、没有代价 → 不配）。只有页码格需要（点它出来的是菜单）。
            if ((PptBarZone)PptBarHover == PptBarZone.Page)
            {
                // 2026-10-02 第五轮：长按入口取消、改成"点页码 = 菜单"。这句话把
                // "点一下会出什么"说清楚；触屏用户看进放映那张 1.5 秒引导卡，两条路一致。
                EngineTooltipHover(12, "页码", null,
                    "点 = 页码跳转菜单（含结束放映）", mid);
                return;
            }
        }

        // ---- ② 选中操作条十格 ----
        if (SelectionBarShown && SelBarHover >= 0)
        {
            var aabb = LiveSelectionFrame.CanvasAabb;
            var btn = BarDrawnCollapsed
                ? SelectionHandles.BarCollapsedRect(aabb, DpiScale, ViewportCanvas)
                : SelectionHandles.BarButtonRect(SelBarHover, aabb, DpiScale, ViewportCanvas);
            var (t, k, n) = BarButtonTip((SelBarButton)SelBarHover);
            if (t != null)
            {
                EngineTooltipHover(200 + SelBarHover, t, k, n, btn);
                return;
            }
        }

        ClearEngineTooltip();
    }

    /// <summary>记录"指针停在这块上"。同一块不重新计时；换块清掉旧的显示状态。</summary>
    internal void EngineTooltipHover(int id, string title, string key, string note, in RectF anchor)
    {
        _tipAnchor = anchor;
        if (id == _tipId && title == _tipTitle) return;   // 同一块：不重新计时
        _tipId = id;
        _tipTitle = title; _tipKey = key; _tipNote = note;
        _tipSinceMs = NowMs;
        _tipShown = false;
        _dirty = true;
    }

    internal void ClearEngineTooltip()
    {
        if (_tipId == 0 && !_tipShown) return;
        _tipId = 0;
        _tipShown = false;
        _tipTitle = _tipKey = _tipNote = null;
        _dirty = true;
    }

    /// <summary>每帧推进：到点就点亮（主循环里紧跟 StepPptBar 调一次）。
    /// 延迟分两档（2026-10-16）：**刚出过提示**（<see cref="TipBetweenWindowMs"/> 窗口内）
    /// 走 <see cref="TipFastDelayMs"/>，否则走完整的 <see cref="TipDelayMs"/>。</summary>
    internal void StepEngineTooltip()
    {
        if (_tipId == 0) return;
        if (_tipShown) { _lastTipShownAtMs = NowMs; return; }   // 亮着：持续刷新"刚看过"
        double delay = (NowMs - _lastTipShownAtMs) <= TipBetweenWindowMs ? TipFastDelayMs : TipDelayMs;
        if (NowMs - _tipSinceMs < delay) return;
        _tipShown = true;
        _lastTipShownAtMs = NowMs;
        _dirty = true;
    }

    /// <summary>菜单每一项的说明（六字标题之外，把"到底动了什么"说清）。</summary>
    private (string Title, string Note) PptMenuTip(int i)
    {
        // 第 4 项（清除全部墨迹）的说明要**按模式说**：PPT 是整副演示，文档是整份文档
        //（2026-10-14 改名时一起修的；别的几项文案本来就通用）。
        if (i == 3)
            return PptMode
                ? ("清除全部墨迹", "这份演示文稿的全部页面，点两次才清（可撤销）")
                : ("清除全部墨迹", "整份文档的全部页面，点两次才清（可撤销）");
        return i switch
        {
            0 => ("指定页码跳转", "打开页号格子，点哪一页跳哪一页"),
            1 => ("自动保存墨迹", "这次放映翻页 / 退出时自动存到本机"),
            2 => ("回放本页墨迹", "把这一页的板书重演一遍"),
            _ => ("结束本次放映", "退出放映，PPT 本身不动"),
        };
    }

    /// <summary>选中操作条每一格的说明（图标-only，这里是唯一的文字出口）。</summary>
    private (string Title, string Key, string Note) BarButtonTip(SelBarButton b) => b switch
    {
        SelBarButton.Collapse => ("收起操作条", null, "之后只剩一颗圆钮，点它再展开"),
        SelBarButton.Color => ("颜色与粗细", null, "改选中对象的颜色 / 粗细 / 线型"),
        SelBarButton.Lock => ("锁定", null, "锁定后拖不动，仍然能选中"),
        SelBarButton.Layer => ("层级", null, "置顶 / 置底"),
        SelBarButton.Export => ("导出", null, "存成图片文件（png / jpg）"),
        SelBarButton.Library => ("存入图库", null, "之后从图形面板的「图库」里取用"),
        SelBarButton.Copy => ("复制拖拽", Keys.KeyText(KeyAction.Copy),
                              "点一下进入模式，按住选中内容拖 = 拖出副本"),
        SelBarButton.FlipH => ("左右翻转", null, null),
        SelBarButton.FlipV => ("上下翻转", null, null),
        SelBarButton.Delete => ("删除", Keys.KeyText(KeyAction.DeleteSelected), null),
        _ => default,
    };
}
