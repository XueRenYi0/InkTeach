// [职责] 键盘模式与键盘手势：工具键、色带键、滚轮/↑↓ 视野平移。
// [测试] --keytest

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
    // =====================================================================
    //  批注键盘模式
    // =====================================================================

    /// <summary>
    /// 开关批注键盘模式。
    ///
    /// 开：去掉 WS_EX_NOACTIVATE 并把窗口提到前台，键盘归批注层，编辑类
    ///     快捷键（Ctrl+Z / Ctrl+D / Delete / 方向键…）才有地方落地。
    /// 关：加回 WS_EX_NOACTIVATE，覆盖层回到"永不抢焦点"，键盘还给下层程序
    ///     ——那时候只有全局热键（Ctrl+Alt+Shift+…）可用。
    ///
    /// 取舍说清楚：开着的时候，放映中的 PPT 收不到键盘。
    /// </summary>
    internal void SetKeyboardMode(bool on)
    {
        KeyboardMode = on;
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            if (on) ex &= ~Native.WS_EX_NOACTIVATE;
            else ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            // ⚠ 放映期间**不许抢前台**（2026-11-23）：PPT/WPS 一丢前台就停掉翻页动画
            // （真机症状：动画第一帧被卡住/直接跳页）。放映期的工具键与方向键由
            // `RegisterPptHotkeys` 的临时全局热键接管，不需要键盘焦点；这里只落样式。
            // 退出放映时 `ExitPptMode` 已先把 `PptMode` 置 false 再调本方法，正常要回焦点。
            if (on && !PptMode)
            {
                // 覆盖层平时是 WS_EX_NOACTIVATE，直接 SetForegroundWindow 可能被系统拒绝；
                // 借前台线程的输入队列（和分类改名输入框同一套）才能保证键盘焦点真的过来。
                IntPtr fg = Native.GetForegroundWindow();
                uint fgTid = Native.GetWindowThreadProcessId(fg, out _);
                uint myTid = Native.GetCurrentThreadId();
                if (fgTid != 0 && fgTid != myTid)
                {
                    Native.AttachThreadInput(myTid, fgTid, true);
                    Native.SetForegroundWindow(w.Hwnd);
                    Native.SetFocus(w.Hwnd);
                    Native.AttachThreadInput(myTid, fgTid, false);
                }
                else
                {
                    Native.SetForegroundWindow(w.Hwnd);
                    Native.SetFocus(w.Hwnd);
                }
            }
        }
        Console.WriteLine($"批注键盘模式 = {on}"
            + (on ? "（键盘归批注层；此时下层程序收不到键盘）" : "（键盘还给下层程序）"));
        _dirty = true;
    }

    /// <summary>
    /// **把前台/键盘要回来**——"从别的状态切回批注态"的统一补丁。
    ///
    /// 为什么必须有这一句：藏窗口（`ScreenCapture.HiddenOverlay`）/ 摘置顶 / 换窗口样式
    /// 之后，前台会被系统转给别人，而恢复显示用的是 `SW_SHOWNOACTIVATE`（不许抢焦点），
    /// **它不会自己回来**。于是 `Ctrl+P` 这些应用内快捷键全被别的窗口收走；更隐蔽的是
    /// **截图取景中的 `Esc`/`Enter` 也收不到**（它们同样靠我们的窗口收键），
    /// 而我们的窗口 `WM_MOUSEACTIVATE → MA_NOACTIVATE`，**点它也不会激活**，
    /// 用户自己救不回来——只能重启软件。
    ///
    /// 已经踩过三次，**全是同一个坑**：
    ///   ① 穿透开开关关（见 SetPassThrough 那段注释）；
    ///   ② 退出放映（见 Ppt.ExitPptMode）；
    ///   ③ **截完图**（用户 2026-09-30 报的"截完图以后笔的快捷键没有用了"）。
    /// 以后凡是"从别的状态切回批注态"的地方都要调它。幂等。
    /// </summary>
    private void RegainKeyboardFocus()
    {
        // 放映期间前台必须留给 PPT/WPS（抢一次就打断翻页动画，见 SetKeyboardMode）。
        if (PptMode) return;
        if (!KeyboardMode || _windows.Count == 0) return;
        foreach (var w in _windows)
        {
            Native.SetForegroundWindow(w.Hwnd);
            Native.SetFocus(w.Hwnd);
        }
    }

    /// <summary>
    /// 批注键盘模式下的按键。**只在 <see cref="KeyboardMode"/> 打开时收到。**
    ///
    /// 键位一律照 Windows 的通用习惯，不自己发明：
    ///   Ctrl+Z 撤销 / Ctrl+Y 重做 / Ctrl+A 全选 / Delete 删除 / Esc 取消选择
    ///   Ctrl+D 复制一份（系统剪贴板还没接，先用 D）
    ///   方向键移动 1 像素，按住 Shift 是 10 像素
    ///
    /// 已知待改：方向键按住会重复触发，每次都是一条撤销记录。要接"连续微调
    /// 合并成一步"，得等编辑命令支持合并（撤销栈里相邻同类动作合并）。
    /// </summary>
    // ================= 工具键的"手势"状态机 =================
    //
    // 手写板的笔上只有两个按钮，所以工具键要"一键多用"（用户 2026-09-29 定）：
    //   · 单击             = 切到它；**已经是它** → 连按换色/换档（和点面板那一格一个规矩）
    //   · 快速双击(≤350ms) = 主工具往后轮一格：笔 → 荧光笔 → 激光笔 → 橡皮 → 选中 → 笔
    //   · 按住(≥0.6s)      = 回第一个颜色 / 第一档
    //
    // 为什么要"延迟 250ms 结算连按换色"：双击和"同键连按"只能靠时间分开。
    // 延迟只落在**最后一击**上：连按时下一击一到就立刻结算，手感是连续的。
    private const double ToolKeyDoubleMs = 350;   // 两击间隔 ≤ 它 = 双击
    private const double ToolKeyHoldMs = 600;     // 按住 ≥ 它 = 长按
    // 连按动作（换色/换档）在**松手后**再等这么久才结算。
    // ⚠ 必须 > ToolKeyDoubleMs：双击是"第二次按下落在上次松手后 350ms 内"，
    //   结算若比它早，双击就会被"先换了色、再切工具"（2026-09-29 自己踩过）。
    private const double ToolKeyRepeatMs = 400;

    private sealed class KeyGesture
    {
        public double DownAt = -1;      // 本次按下时刻；-1 = 没按着
        public double UpAt = -1;        // 上次松开时刻；-1 = 不参与双击判定（刚长按完）
        public bool HoldFired;          // 本次按住已经触发过长按
        public bool WantsRepeat;        // 这一击落在"已经在这个工具上" → 松手后要换色/换档
        public double PendingAt = -1;   // 待结算的"连按动作"时刻
        public KeyAction PendingWhat;
    }

    private readonly Dictionary<KeyAction, KeyGesture> _gestures = new();

    private KeyGesture Gest(KeyAction a)
    {
        if (!_gestures.TryGetValue(a, out var g)) _gestures[a] = g = new KeyGesture();
        return g;
    }

    /// <summary>这些键走手势状态机（单击/双击/长按），其余键照旧一按一动。</summary>
    private static bool IsToolKey(KeyAction a) => a
        is KeyAction.ToolPen or KeyAction.ToolHighlighter or KeyAction.ToolLaser
        or KeyAction.ToolEraser or KeyAction.ToolPixelEraser or KeyAction.ToolMarquee;

    /// <summary>主工具的轮换顺序（双击往后一格）。</summary>
    private static readonly Tool[] MainToolCycle =
        { Tool.Pen, Tool.Highlighter, Tool.Laser, Tool.Eraser, Tool.Marquee };

    private static Tool ToolOf(KeyAction a) => a switch
    {
        KeyAction.ToolPen => Tool.Pen,
        KeyAction.ToolHighlighter => Tool.Highlighter,
        KeyAction.ToolLaser => Tool.Laser,
        KeyAction.ToolEraser => Tool.Eraser,
        KeyAction.ToolPixelEraser => Tool.PixelEraser,
        _ => Tool.Marquee,
    };

    /// <summary>双击：主工具往后轮一格（图形/截图这些不在循环里 → 从笔开始）。</summary>
    private void NextMainTool()
    {
        int i = Array.IndexOf(MainToolCycle, Tool);
        var next = MainToolCycle[(i + 1) % MainToolCycle.Length];
        SwitchTool(next);
        Console.WriteLine($"工具键双击 → {ToolName(next)}");
    }

    private bool ToolKeyDown(KeyAction a, double now)
    {
        var g = Gest(a);
        if (g.DownAt >= 0) return true;                            // 自动重复的按下：忽略，等松手
        bool dbl = g.UpAt >= 0 && now - g.UpAt <= ToolKeyDoubleMs;
        g.DownAt = now;
        g.HoldFired = false;
        g.WantsRepeat = false;
        if (dbl)
        {
            CancelPending(g);
            g.UpAt = -1;                                            // 双击后不再连着判"第三击"
            NextMainTool();
            return true;
        }
        var target = ToolOf(a);
        if (Tool != target || DocPanMode) { SwitchTool(target); return true; }     // 单击：立即切（要快）
        // 已经在这个工具上 → 松手后 400ms 执行"连按动作"（等一等看是不是双击）
        g.WantsRepeat = true;
        return true;
    }

    private bool ToolKeyUp(KeyAction a, double now)
    {
        var g = Gest(a);
        if (g.DownAt < 0) return false;
        double held = now - g.DownAt;
        g.DownAt = -1;
        if (held >= ToolKeyHoldMs || g.HoldFired) { g.UpAt = -1; g.WantsRepeat = false; return true; }
        g.UpAt = now;                                               // 短按：记下来给双击判定用
        if (g.WantsRepeat)
        {
            g.WantsRepeat = false;
            g.PendingAt = now + ToolKeyRepeatMs;
            g.PendingWhat = a;
        }
        return true;
    }

    /// <summary>每帧一次：长按判定 + 连按动作的延迟结算。</summary>
    private void PumpKeyGestures()
    {
        double now = NowMs;
        foreach (var (a, g) in _gestures)
        {
            if (g.DownAt >= 0 && !g.HoldFired && now - g.DownAt >= ToolKeyHoldMs)
            {
                g.HoldFired = true;
                CancelPending(g);
                ResetToolToFirst(a);
                continue;
            }
            if (g.PendingAt >= 0 && now >= g.PendingAt)
            {
                var what = g.PendingWhat;
                CancelPending(g);
                DoToolKeyRepeat(what);
            }
        }
    }

    private static void CancelPending(KeyGesture g) => g.PendingAt = -1;

    /// <summary>连按要干的事：**只有笔/荧光笔换色**。
    /// [2026-10-05 用户定] 橡皮"整笔/面积"、框选"矩形/套索"不再同键切换——爱用哪种就一直用哪种，
    /// 子类型去面板的上带里选（"再点同一格换档"也随之取消，见 FullUi.Activate）。</summary>
    private void DoToolKeyRepeat(KeyAction a)
    {
        switch (a)
        {
            case KeyAction.ToolPen: CycleBandColor(highlighter: false); break;
            case KeyAction.ToolHighlighter: CycleBandColor(highlighter: true); break;
            // 橡皮 / 框选：连按不做事（子类型固定，面板上带里选）。
        }
    }

    /// <summary>长按：回第一个颜色（只对笔 / 荧光笔；子类型不再由键盘改）。</summary>
    private void ResetToolToFirst(KeyAction a)
    {
        switch (a)
        {
            case KeyAction.ToolPen:
                SelectPenSwatchFromUi(0);
                Console.WriteLine($"长按 → 笔回到「{InkPalette.PenBand[0].Name}」");
                break;
            case KeyAction.ToolHighlighter:
                SelectHighlighterSwatchFromUi(0);
                Console.WriteLine($"长按 → 荧光笔回到「{InkPalette.HighlighterBand[0].Name}」");
                break;
            // [2026-10-05 用户定] 橡皮 / 框选的子类型不再由键盘（长按）改；面板上带里选。
        }
    }

    /// <summary>
    /// 连按换色：在色带里往后走一格（转圈）。
    ///
    /// ⚠ **用记下来的序号走，不靠"按颜色值找当前位置"**——2026-09-30 修的真 bug：
    /// 以前每按一下都拿当前颜色去色带里匹配，容差 0.10 把「深蓝 / 藏青」认成同一个，
    /// 于是在 墨绿→酒红→藏青→(被当成深蓝)→墨绿 之间来回蹦，用户实测"只有后面三种在切"。
    /// 现在序号只在 SetColorFromUi（别人改了色）时同步，循环本身永远 +1。
    /// </summary>
    private void CycleBandColor(bool highlighter)
    {
        if (!highlighter)
        {
            // 8 个色位一起循环（含最后一格）；当前色不是从色带选的就从第 1 格开始。
            int penN = PenSwatchColors.Length;
            int next = PenSwatchSelected >= 0 ? (PenSwatchSelected + 1) % penN : 0;
            SelectPenSwatchFromUi(next);
            Console.WriteLine($"连按 → 色位 {next + 1}");
            return;
        }

        // 5 个荧光色位一起循环；当前色不是从色带选的就从第 1 格开始。
        int n = HighlighterSwatchColors.Length;
        int nextHl = HighlighterSwatchSelected >= 0 ? (HighlighterSwatchSelected + 1) % n : 0;
        SelectHighlighterSwatchFromUi(nextHl);
        Console.WriteLine($"连按 → 荧光色位 {nextHl + 1}");
    }

    private static bool CloseColor(Color4 a, Color4 b) =>
        Math.Abs(a.R - b.R) < 0.10f && Math.Abs(a.G - b.G) < 0.10f && Math.Abs(a.B - b.B) < 0.10f;

    // ---- 荧光笔的 5 个色位 / 5 档浓度（2026-10-28）----------------------------

    internal void SelectHighlighterSwatchFromUi(int slot)
    {
        if (slot < 0 || slot >= HighlighterSwatchColors.Length || Tool != Tool.Highlighter) return;
        HighlighterSwatchSelected = slot;
        HighlighterCurrent = InkPalette.ToHighlighter(
            HighlighterSwatchColors[slot], InkPalette.HighlighterOpacityLevels[HighlighterSwatchOpacity[slot]]);
        PersistHighlighterSwatches();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 把颜色写进荧光笔的第 <paramref name="slot"/> 个色位，并套这个色位自己的浓度。
    /// <paramref name="normalize"/> = true 时先做“高亮化”（HSV/吸管取色走这条），
    /// 固定常用色已经挑好，传 false。
    /// </summary>
    internal void SetHighlighterSwatchColorFromUi(int slot, Color4 color, bool rememberRecent, bool normalize)
    {
        if (slot < 0 || slot >= HighlighterSwatchColors.Length || Tool != Tool.Highlighter) return;
        if (normalize) color = InkPalette.HighlighterBase(color);
        var baseColor = new Color4(color.R, color.G, color.B, 1f);
        HighlighterSwatchColors[slot] = baseColor;
        HighlighterSwatchSelected = slot;
        HighlighterCurrent = InkPalette.ToHighlighter(
            baseColor, InkPalette.HighlighterOpacityLevels[HighlighterSwatchOpacity[slot]]);
        if (rememberRecent) RememberRecentHighlighterColor(baseColor);
        PersistHighlighterSwatches();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>只改当前荧光色位的浓度档（0..4）；已经画好的笔迹不动。</summary>
    internal void SetHighlighterOpacityFromUi(int slot, int level)
    {
        if (slot < 0 || slot >= HighlighterSwatchColors.Length || Tool != Tool.Highlighter) return;
        HighlighterSwatchOpacity[slot] = Math.Clamp(level, 0, InkPalette.HighlighterOpacityLevels.Length - 1);
        if (HighlighterSwatchSelected == slot)
            HighlighterCurrent = InkPalette.ToHighlighter(
                HighlighterSwatchColors[slot], InkPalette.HighlighterOpacityLevels[HighlighterSwatchOpacity[slot]]);
        PersistHighlighterSwatches();
        _dirty = true;
        NotifyUiStateChanged();
    }

    private void PersistHighlighterSwatches()
    {
        var swatches = new string[HighlighterSwatchColors.Length];
        for (int i = 0; i < swatches.Length; i++) swatches[i] = FormatColorPref(HighlighterSwatchColors[i]);
        SetUiPref("hlSwatches", string.Join(';', swatches));
        SetUiPref("hlOpacities", string.Join(';', HighlighterSwatchOpacity));
        SetUiPref("hlSwatchSelected", HighlighterSwatchSelected.ToString());
        SetUiPref("hlColorValue", FormatColorPref(
            new Color4(HighlighterCurrent.R, HighlighterCurrent.G, HighlighterCurrent.B, 1f)));
        SetUiPref("hlColorOpacity", HighlighterCurrent.A.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (HighlighterSwatchSelected >= 0 && HighlighterSwatchSelected < HighlighterSwatchColors.Length)
            SetUiPref("hlColor", HighlighterSwatchSelected.ToString());
    }

    private void RememberRecentHighlighterColor(Color4 color)
    {
        for (int i = _recentHighlighterColors.Count - 1; i >= 0; i--)
            if (ColorNear(_recentHighlighterColors[i], color, 0.02f)) _recentHighlighterColors.RemoveAt(i);
        _recentHighlighterColors.Insert(0, color);
        while (_recentHighlighterColors.Count > RecentHighlighterColorMax)
            _recentHighlighterColors.RemoveAt(_recentHighlighterColors.Count - 1);

        var parts = new string[_recentHighlighterColors.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = FormatColorPref(_recentHighlighterColors[i]);
        SetUiPref("hlRecent", string.Join(';', parts));
    }

    // ---- 笔的 8 个色位 / 最近颜色（2026-10-23 起；2026-10-28 扩成可改色位）----

    /// <summary>选中笔的第 <paramref name="slot"/> 个色位，不改色位里的颜色。</summary>
    internal void SelectPenSwatchFromUi(int slot)
    {
        if (slot < 0 || slot >= PenSwatchColors.Length || Tool != Tool.Pen) return;
        PenSwatchSelected = slot;
        CurrentColor = PenSwatchColors[slot];
        PersistPenSwatches();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 把颜色写进笔的第 <paramref name="slot"/> 个色位并立即选中。
    /// <paramref name="rememberRecent"/> = false：固定常用色只改色位，不写“最近使用”。
    /// </summary>
    internal void SetPenSwatchColorFromUi(int slot, Color4 color, bool rememberRecent)
    {
        if (slot < 0 || slot >= PenSwatchColors.Length || Tool != Tool.Pen) return;
        PenSwatchColors[slot] = color;
        PenSwatchSelected = slot;
        CurrentColor = color;
        if (rememberRecent) RememberRecentPenColor(color);
        PersistPenSwatches();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>选中工具带里的最后一个色位（兼容旧“自定义槽”入口）。</summary>
    internal void SelectCustomPenColorFromUi()
    {
        SelectPenSwatchFromUi(PenSwatchColors.Length - 1);
    }

    /// <summary>把新颜色写进最后一个色位并立即使用；旧自定义槽入口。</summary>
    internal void SetCustomPenColorFromUi(Color4 color)
    {
        SetPenSwatchColorFromUi(PenSwatchColors.Length - 1, color, rememberRecent: true);
    }

    /// <summary>把 8 个色位 + 当前选中 + 当前 RGB 一起落盘；旧键也同步写，方便回退上一版。</summary>
    private void PersistPenSwatches()
    {
        var parts = new string[PenSwatchColors.Length];
        for (int i = 0; i < parts.Length; i++) parts[i] = FormatColorPref(PenSwatchColors[i]);
        SetUiPref("penSwatches", string.Join(';', parts));
        SetUiPref("penSwatchSelected", PenSwatchSelected.ToString());
        SetUiPref("penColorValue", FormatColorPref(CurrentColor));
        SetUiPref("penCustom", FormatColorPref(PenSwatchColors[^1]));
        SetUiPref("penCustomSelected", PenSwatchSelected == PenSwatchColors.Length - 1 ? "1" : "0");
    }

    private void RememberRecentPenColor(Color4 color)
    {
        for (int i = _recentPenColors.Count - 1; i >= 0; i--)
            if (ColorNear(_recentPenColors[i], color, 0.02f)) _recentPenColors.RemoveAt(i);
        _recentPenColors.Insert(0, color);
        while (_recentPenColors.Count > RecentPenColorMax)
            _recentPenColors.RemoveAt(_recentPenColors.Count - 1);

        var parts = new string[_recentPenColors.Count];
        for (int i = 0; i < parts.Length; i++) parts[i] = FormatColorPref(_recentPenColors[i]);
        SetUiPref("penRecent", string.Join(';', parts));
    }

    private static bool ColorNear(Color4 a, Color4 b, float eps) =>
        MathF.Abs(a.R - b.R) <= eps && MathF.Abs(a.G - b.G) <= eps && MathF.Abs(a.B - b.B) <= eps;

    private static string FormatColorPref(Color4 c) =>
        $"{Math.Clamp(c.R, 0f, 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},"
        + $"{Math.Clamp(c.G, 0f, 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},"
        + $"{Math.Clamp(c.B, 0f, 1f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";

    private static Color4 ParseColorPref(string text, Color4 fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var p = text.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 3
            || !float.TryParse(p[0], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float r)
            || !float.TryParse(p[1], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float g)
            || !float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float b))
            return fallback;
        return new Color4(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), 1f);
    }

    /// <summary>上次用的橡皮形态（整笔擦 / 面积擦）——按 Ctrl+E 时切回它，见 SetUiPref("eraserKind")。</summary>
    private Tool _eraserKind = Tool.PixelEraser;

    /// <summary>自检用：设置"记住的橡皮种类"（三指 / 手掌擦跟随它，和鼠标同源）。</summary>
    internal Tool EraserKindForTest { get => _eraserKind; set => _eraserKind = value; }

    /// <summary>
    /// 工具键的**单击**逻辑（2026-09-30 收口：双击/长按那套手势全部取消，只留单击；
    /// 2026-10-05 再收：**只有笔/荧光笔"已经是它 → 换色"**，橡皮/框选连按不再换子类型）：
    ///   · 不是这个工具 → 切过去
    ///   · 已经是它    → 笔/荧光笔换颜色；橡皮/框选什么都不做
    ///
    /// **穿透模式下整个失效**（用户 2026-09-30 拍板："开了穿透以后，笔、橡皮这些快捷键
    /// 应该就没有用了，等退出穿透才有用"）：穿透 = "不能画"，这时换工具/换色都没有着落，
    /// 而且"已经不是笔了、颜色却还在变"正是用户报的那个怪状态。想画画先退出穿透
    /// （全局 `Ctrl+Alt+Shift+T` / 点穿透格），工具键随即恢复。
    /// 只挡**键盘**这两条路（应用内键 + 放映临时全局键，都汇到这里）；
    /// 面板上那一格不在此列——点它仍然"顺手关穿透 + 换工具"（没键盘的教室靠它）。
    /// </summary>
    private void ToolKeyPress(KeyAction a)
    {
        if (PassThrough)
        {
            Console.WriteLine("穿透模式下：工具键不响应（先退出穿透）");
            return;
        }
        var target = ToolOf(a);
        // 橡皮：回到"上次用的那一种形态"（整笔/面积），不是永远回整笔擦
        if (target == Tool.Eraser) target = _eraserKind;
        if (Tool != target || DocPanMode)
        {
            SwitchTool(target);
            ApplyCursor();
            NotifyUiStateChanged();
            return;
        }
        DoToolKeyRepeat(a);
    }

    // ---- 滚轮 / ↑↓：同一类"阅读视野"输入（放在键盘这一份，避免再开 partial）----

    /// <summary>
    /// 一"格"的滑动距离 = **屏幕高度的 1/10**（用户 2026-09-30 定，照 ClassIn 的手感）。
    /// 上下方向键、以及放映期的同名键都用它。
    /// </summary>
    private float ScrollStepLogical => _windows.Count > 0 ? _windows[0].Height / DpiScale / 10f : 60f;

    /// <summary>
    /// ↑↓（任何模式）上下挪视野——和滚轮**同一条"相机偏移"**（只动视野，不动墨迹数据）。
    /// 符号照滚轮那条：往"下"看 = <see cref="ViewOffsetY"/> 变小，所以 <paramref name="logicalDelta"/>
    /// 为正 = 看上面的内容。
    /// </summary>
    private void ScrollCanvasBy(float logicalDelta)
    {
        if (_windows.Count == 0) return;
        ViewOffsetY += logicalDelta * DpiScale;
        ClampViewOffset();
        ScrollBarActiveAtMs = NowMs;
        _dirty = true;
    }

    internal IntPtr HandleWheel(IntPtr wParam)
    {
        StopTouchFling();                      // 滚轮/触控板接管：先刹住双指惯性
        StopDocPanFling();
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        Native.GetCursorPos(out var cursor);
        // Ctrl + 滚轮 = **文档缩放**（2026-11-16；参考 Acrobat / Edge 的阅读器）。
        // 围绕**指针**缩放：指针压着的那个画布点不动。只缩文档，白板 / 桌面批注不缩；
        // 普通滚轮（没有 Ctrl）仍然往下走，滚画布 —— 老行为一个字节都不变。
        if (!_textBoxEditing
            && (Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0
            && DocZoomWheelFromUi(delta, cursor.X, cursor.Y))
            return IntPtr.Zero;
        // 字体下拉：滚轮翻字体列表。
        if (FontListOpen && FontListRectNow().Contains(PointerX, PointerY))
        {
            FontListScrollByPx(-delta / 120f * 3f * FontListRowHeightNow);
            return IntPtr.Zero;
        }
        // 选中一个内容溢出的文本框时，滚轮先滚它自己的内容，不滚画布。
        if (Doc.Selected.Count == 1 && Doc.Selected[0].Kind == StrokeKind.TextBox)
        {
            var ts = Doc.Selected[0];
            var card = ts.TextBox;
            if (card != null && card.MaxScroll > 0.5f)
            {
                float cx = PointerX, cy = PointerY;
                ScreenToCanvas(ref cx, ref cy);
                if (ts.PaddedBounds.Contains(cx, cy))
                {
                    float step = 80f * MathF.Max(0.5f, card.Unit);
                    if (card.ScrollBy(-delta / 120f * step))
                    {
                        Doc.Dirty.Add(ts.PaddedBounds);
                        Doc.StructureChangedSinceRender = true;
                        Doc.Version++;
                        _dirty = true;
                        return IntPtr.Zero;
                    }
                }
            }
        }
        // 收藏分类抽屉：滚轮滚分类。
        if (LibraryPanelVisibleNow && LibraryCategoriesOpen)
        {
            var lm = LibraryPanelInfoNow();
            if (LibraryDrawerRectNow().Contains(PointerX, PointerY))
            {
                int cap = Math.Max(1, LibraryLayout.CategoryDrawerCapacity(lm) - 1);
                if (_libraryCategoryInfos.Length > cap)
                {
                    int step = delta > 0 ? -1 : 1;
                    LibraryCategoryScroll = Math.Clamp(LibraryCategoryScroll + step, 0,
                        Math.Max(0, _libraryCategoryInfos.Length - cap));
                    _dirty = true;
                    return IntPtr.Zero;
                }
            }
        }
        // 有面板/图库等界面时先给界面（说明页要滚内部内容）；没消费才滚画布。
        // ????? / ?????????????????????
        if (LibraryPanelVisibleNow)
        {
            var lm = LibraryPanelInfoNow();
            if (LibraryLayout.Contains(lm.Panel, PointerX, PointerY))
            {
                if (lm.Tabbed && LibraryLayout.Contains(lm.Tabs, PointerX, PointerY))
                {
                    var cats = LibraryTabCategories();
                    var widths = new float[cats.Length];
                    for (int i = 0; i < cats.Length; i++)
                        widths[i] = LibraryLayout.TabWidthLogical(cats[i].Name) * UiUnit;
                    float view = lm.Tabs.MaxX - lm.Tabs.MinX;
                    float max = MathF.Max(0f, LibraryLayout.TabsWidth(widths, UiUnit) - view);
                    if (max > 0.5f)
                    {
                        float step = delta > 0 ? -36f : 36f;
                        LibraryTabScroll = Math.Clamp(LibraryTabScroll + step * UiUnit, 0f, max);
                        _dirty = true;
                        return IntPtr.Zero;
                    }
                }
                if (LibraryLayout.Contains(lm.Grid, PointerX, PointerY))
                {
                    LibraryScrollBy(-delta / 120f * 80f * UiUnit);
                    return IntPtr.Zero;
                }
            }
        }

        if (UiVisibleNow && Ui != null)
        {
            // **用系统光标的真实位置**，不用 PointerX/Y：那两个是**画布坐标**
            // （`PointerY = 屏幕Y - ViewOffsetY`）。设置页在滚动过的白板/文档上打开时，
            // 拿画布坐标去判界面视口会永远判不进——表现就是"滚轮在设置里滚不动"。
            bool consumed = UiGuard("Wheel",
                () => Ui.Wheel(cursor.X / UiUnit, cursor.Y / UiUnit, delta), false);
            if (consumed)
            {
                _dirty = true;
                return IntPtr.Zero;
            }
        }
        // 往下滚时 delta = -120，要先变负（内容上移）——所以这里是 +=。
        // 写成 -= 会把符号翻过来：滚轮往下 = 视图往上，正好抵掉，
        // 再被"不许滚过顶部"夹回 0，表现就是**完全滚不动**。
        ViewOffsetY += delta / 120f * 72f * DpiScale;   // 一格 = 72 逻辑像素
        // 上下都夹住画布范围（内容边界 ∪ 一屏）。
        // 不夹的话会滚进无尽的空白，而且比例滚动条拿不到有意义的范围。
        // 下边界是"视口底边贴住内容底边"——接着写，内容长出去，范围自己长出来，
        // 所以不会把人卡在底边。
        ClampViewOffset();

        ScrollBarActiveAtMs = NowMs;                     // 滚动时让滚动条露面
        // 注意：**不调 Doc.InvalidateAll()**。那会把整个文档标脏、让所有分块
        // 重画——正是这次要拔掉的病根。分块缓存自己会发现相机变了，只做重合成。
        _dirty = true;
        return IntPtr.Zero;
    }
}

