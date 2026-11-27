// [职责] 边缘翻页条的运行期状态：显隐动画 + 穿透下的"接输入小窗"（左右各一块）+ 悬停。
// [入口] 引擎每帧 StepPageEdge()；窗口过程转 WM_* 到 PageEdgeInputWndProc()；绘制读 Reveal / HoverCell。
// [读写状态] 只持有自己的（开关、两块输入小窗句柄、动画进度、悬停格、脏区记忆）。
// [落盘 Key] ui.pageEdge（"1" = 开；**默认关**，老用户升级后完全无感）。
// [测试] --ppttest（几何 + 上/下翻各点一次 + 穿透下的输入小窗）

using System;
using System.Runtime.InteropServices;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// **屏幕左右边缘整页翻页条**的运行期状态（2026-11-25，自研；几何见 <see cref="PageEdge"/>）。
///
/// 为什么是一个独立对象、而不是 Engine / Overlay 的私有字段（ADR-005 D3）：
/// 新功能不许再往那几个巨石里堆私有字段，需要状态就新建组合对象。
///
/// 三件事：
///   1. **显隐**：平时几乎不可见（<see cref="PageEdge.IdleAlpha"/>），指针靠近 /
///      按着才淡入到 1。它正压在画布左右中部——常驻不透明面板会被掌根误触、
///      也挡内容（用户 2026-11-25 的顾虑）。
///   2. **穿透下的接输入小窗**：穿透靠 `WS_EX_TRANSPARENT`，系统**跳过命中测试**
///      （连 WM_NCHITTEST 都不问），所以"看得见的一块就是点得到的"在穿透下不成立。
///      界面面板 / PPT 条都是靠一块 1/255 透明的独立小窗解决的，这里照抄同一套。
///      ⚠ **必须左右各一块**：两块整组隔着整块屏幕，合成一块外接矩形会把中间
///      一整条横带都吃掉（穿透下老师就点不到 PPT 了）。
///      ⚠ PR 那个实现最大的坑就在这里：只加了 NCHITTEST 豁免，穿透下整条是死的，
///      而且点击会漏给下层 PowerPoint。
///   3. **按下即翻页**：和 PPT 条的左右箭头一致（触屏没有 hover，按下就生效最跟手）。
/// </summary>
internal sealed class PageEdgeBar : IDisposable
{
    /// <summary>设置里开着吗（"ui.pageEdge" = "1"）。**默认关**。</summary>
    internal bool Enabled;

    /// <summary>这一次按下归翻页条（松手要由它收尾、放开捕获）。</summary>
    internal bool Capturing;

    /// <summary>指针落在哪一格（100＋格号 = 左，200＋格号 = 右；-1 = 没落）。</summary>
    internal int HoverCell = -1;

    // ---- 显隐动画 ----
    private float _reveal;
    private float _revealFrom;
    private double _revealStartMs = double.NegativeInfinity;
    private double _revealMs;

    /// <summary>当前显形进度（0 = 几乎不可见，1 = 完全显形）。绘制按它算 alpha。</summary>
    internal float Reveal => _reveal;

    /// <summary>这一刻是不是还要继续出帧（淡入淡出没走完）。</summary>
    internal bool Animating => MathF.Abs(_reveal - RevealTarget) > 0.002f;

    private float RevealTarget => _wanted ? 1f : 0f;
    private bool _wanted;

    // ---- 指针位置（鼠标靠它算"靠近"，手指靠按下）----
    private float _lastX, _lastY;
    private bool _lastValid;
    private bool _wasNear;

    /// <summary>指针进了"靠近就会显形"那一圈（自检 / 调试看）。</summary>
    internal bool NearForTest(in RectF screen, float dpi)
        => _lastValid && PageEdge.Near(_lastX, _lastY, screen, dpi);

    /// <summary>
    /// 记一个指针位置（覆盖层 / 接输入小窗的移动消息都走这里）。
    /// 返回 true = "靠近"的状态变了（调用方要出帧，让显隐动画动起来）。
    /// </summary>
    internal bool Track(float x, float y, in RectF screen, float dpi)
    {
        _lastX = x; _lastY = y; _lastValid = true;
        bool nearBefore = _wasNear;
        int hoverBefore = HoverCell;
        UpdateHover(screen, dpi);
        _wasNear = PageEdge.Near(x, y, screen, dpi);
        return _wasNear != nearBefore || HoverCell != hoverBefore;
    }

    /// <summary>指针走了（鼠标离开覆盖层）：不再算"靠近"。</summary>
    internal void PointerLeft()
    {
        _lastValid = false;
        _wasNear = false;
        HoverCell = -1;
    }

    private void UpdateHover(in RectF screen, float dpi)
    {
        (HoverCell, _wasNear) = HitTest(_lastX, _lastY, _lastValid, screen, dpi);
    }

    /// <summary>命中 + "靠近"一起算（两条消息都问同一份判据）。</summary>
    internal static (int Hover, bool Near) HitTest(float x, float y, bool valid, in RectF screen, float dpi)
    {
        if (!valid) return (-1, false);
        int cell = -1;
        int side = PageEdge.SideAt(x, y, screen, dpi);
        if (side != 0)
        {
            cell = PageEdge.CellAt(x, y, screen, left: side < 0, dpi);
            // 落在两格之间的缝里：CellAt 给 -1，整组仍然算"在这条上"，悬停收掉而已。
            cell = side < 0 ? 100 + cell : 200 + cell;
            if (cell is 99 or 199) cell = -1;
        }
        return (cell, PageEdge.Near(x, y, screen, dpi));
    }

    /// <summary>按下：这一下归条（记成"按着"，显形立即开始）。</summary>
    internal void Press(float x, float y, in RectF screen, float dpi)
    {
        Capturing = true;
        _lastX = x; _lastY = y; _lastValid = true;
        UpdateHover(screen, dpi);
    }
    /// <summary>抬手：只清"按着"（翻页在按下那一刻已经走了）。</summary>
    internal void Release() => Capturing = false;

    /// <summary>
    /// 推进一帧：算该不该显形、走到哪一步、悬停在哪一格。
    /// <paramref name="visible"/> = 开关开着且当前模式允许（PPT 放映 / 白板）。
    /// </summary>
    internal void Step(double nowMs, bool visible, in RectF screen, float dpi)
    {
        if (!visible)
        {
            HoverCell = -1;
            _wanted = false;
            _reveal = 0f;
            _revealStartMs = double.NegativeInfinity;
            _revealMs = 0;
            return;
        }

        // 鼠标：用系统光标位置算"靠近"（穿透下覆盖层收不到悬停消息，只能这么办）。
        // **按着的时候不覆盖**：手指 / 笔按下时系统光标可能停在别处，
        // 盖进来会让悬停格和"在附近"当场作废（松手前一瞬间条就淡掉了）。
        if (!Capturing && Native.GetCursorPos(out var p))
        {
            _lastX = p.X; _lastY = p.Y; _lastValid = true;
            UpdateHover(screen, dpi);
            _wasNear = PageEdge.Near(_lastX, _lastY, screen, dpi);
        }

        bool wanted = Capturing || (_lastValid && PageEdge.Near(_lastX, _lastY, screen, dpi));
        if (wanted != _wanted)
        {
            _wanted = wanted;
            _revealFrom = _reveal;
            _revealStartMs = nowMs;
            _revealMs = PageEdge.RevealMs;
        }
        if (_revealStartMs <= double.NegativeInfinity)
        {
            _reveal = RevealTarget;
            return;
        }
        double t = (nowMs - _revealStartMs) / Math.Max(1.0, _revealMs);
        if (t >= 1.0)
        {
            _reveal = RevealTarget;
            _revealStartMs = double.NegativeInfinity;
            _revealMs = 0;
            return;
        }
        // 快出缓停（同 Anim 的曲线）
        float u = 1f - (float)t;
        float k = 1f - u * u * u;
        _reveal = _revealFrom + (RevealTarget - _revealFrom) * k;
    }

    /// <summary>自检 / 出图用：把显形进度一把按到某个值（平时中间态只有 120ms）。</summary>
    internal void ForceRevealForTest(float v)
    {
        _reveal = v;
        _revealFrom = v;
        _revealStartMs = double.NegativeInfinity;
        _revealMs = 0;
        _wanted = v > 0.5f;
    }

    /// <summary>画出来的 alpha：几乎不可见的那一档 + 显形那一档插值。</summary>
    internal float Alpha => PageEdge.IdleAlpha + (1f - PageEdge.IdleAlpha) * _reveal;

    /// <summary>
    /// 上一帧画过的两块脏区（窗口坐标；由 Overlay 换算）。**左右分开存**：
    /// 合成一块外接矩形会变成"横跨整屏的一条带子"，每次悬停都要重画半个屏幕。
    /// 放组合对象里而不是 Overlay 的私有字段（ADR-005 D3）。
    /// </summary>
    internal RectF DirtyPrevL = RectF.Empty, DirtyPrevR = RectF.Empty;

    // ---- 穿透下的接输入小窗（1/255 透明，只收输入不画东西；左右各一块）----
    private readonly IntPtr[] _inputHwnd = new IntPtr[2];
    private readonly bool[] _inputShown = new bool[2];
    private readonly RectF[] _inputRect = { RectF.Empty, RectF.Empty };

    /// <summary>自检用：左 / 右输入小窗这一刻在不在（穿透下"点得到"的硬件前提）。</summary>
    internal bool InputShownForTest(int side)
        => side is >= 0 and < 2 && _inputShown[side] && _inputHwnd[side] != IntPtr.Zero;

    /// <summary>自检用：某一边小窗的位置（物理屏幕坐标）。</summary>
    internal RectF InputRectForTest(int side)
        => side is >= 0 and < 2 ? _inputRect[side] : RectF.Empty;

    /// <summary>截图时要一起藏起来的小窗（1/255 的一层灰拍进图里就是脏）。</summary>
    internal void AppendInputWindows(System.Collections.Generic.List<IntPtr> list)
    {
        for (int i = 0; i < 2; i++)
            if (_inputHwnd[i] != IntPtr.Zero) list.Add(_inputHwnd[i]);
    }

    /// <summary>这个句柄是不是我们的输入小窗（窗口过程分派 / "是不是自己的窗口"都要问）。</summary>
    internal bool IsInputWindow(IntPtr h)
        => h != IntPtr.Zero && (h == _inputHwnd[0] || h == _inputHwnd[1]);

    /// <summary>哪一边的小窗（0 = 左、1 = 右、-1 = 都不是）。</summary>
    internal int InputSideOf(IntPtr h)
        => h == _inputHwnd[0] ? 0 : h == _inputHwnd[1] ? 1 : -1;

    /// <summary>
    /// 把两块输入小窗各铺在自己那一边的整组上（没有就收掉）。
    /// 每帧调一次，位置没变不动窗口。**只在穿透下需要**：不穿透时覆盖层自己就能收消息。
    /// </summary>
    internal void SyncInputWindows(IntPtr instance, string className,
                                   in RectF screen, float dpi, bool needed)
    {
        for (int i = 0; i < 2; i++)
        {
            if (!needed) { HideInputWindow(i); continue; }
            EnsureInputWindow(i, instance, className);
            if (_inputHwnd[i] == IntPtr.Zero) continue;

            var box = PageEdge.GroupRect(screen, left: i == 0, dpi);
            if (_inputShown[i] && box.Equals(_inputRect[i])) continue;

            Native.SetWindowPos(_inputHwnd[i], Native.HWND_TOPMOST,
                (int)MathF.Floor(box.MinX), (int)MathF.Floor(box.MinY),
                Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX)),
                Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY)),
                Native.SWP_NOACTIVATE | (_inputShown[i] ? 0 : Native.SWP_SHOWWINDOW));
            _inputRect[i] = box;
            _inputShown[i] = true;
        }
    }

    private void EnsureInputWindow(int side, IntPtr instance, string className)
    {
        if (_inputHwnd[side] != IntPtr.Zero) return;
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _inputHwnd[side] = Native.CreateWindowEx(exStyle, className,
            side == 0 ? "InkTeachPageEdgeInputL" : "InkTeachPageEdgeInputR",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_inputHwnd[side] == IntPtr.Zero)
        {
            Console.WriteLine("边缘翻页条接输入小窗创建失败: " + Marshal.GetLastWin32Error()
                              + "（穿透模式下边缘翻页条会点不动）");
            return;
        }
        // 1/255 的不透明度：肉眼看不见，但对命中测试来说它**实实在在地在这**。
        Native.SetLayeredWindowAttributes(_inputHwnd[side], 0, 1, Native.LWA_ALPHA);
        Native.DisableSystemPressAndHold(_inputHwnd[side]);
    }

    private void HideInputWindow(int side)
    {
        if (_inputHwnd[side] != IntPtr.Zero && _inputShown[side])
            Native.ShowWindow(_inputHwnd[side], Native.SW_HIDE);
        _inputShown[side] = false;
        _inputRect[side] = RectF.Empty;
    }

    /// <summary>该收了：两块一起藏（悬停格也清掉，别留一个"亮着"的假状态）。</summary>
    internal void HideInputWindows()
    {
        HideInputWindow(0);
        HideInputWindow(1);
        HoverCell = -1;
        Capturing = false;
    }

    /// <summary>退出时把它收掉（窗口句柄要还给系统）。</summary>
    public void Dispose()
    {
        for (int i = 0; i < 2; i++)
        {
            if (_inputHwnd[i] == IntPtr.Zero) continue;
            Native.DestroyWindow(_inputHwnd[i]);
            _inputHwnd[i] = IntPtr.Zero;
            _inputShown[i] = false;
        }
        Capturing = false;
        HoverCell = -1;
    }

    /// <summary>周期性重申接输入小窗的置顶顺序（在覆盖层之上）。</summary>
    internal void ReassertTopmost()
    {
        for (int i = 0; i < 2; i++)
            if (_inputShown[i] && _inputHwnd[i] != IntPtr.Zero)
                Native.SetWindowPos(_inputHwnd[i], Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---- 多屏屏幕矩形缓存（避免每帧绘制/命中多次 EnumWindows）----
    private RectF _cachedScreen;
    private double _cachedScreenAtMs = double.NegativeInfinity;
    private bool _cachedScreenPpt;
    private int _cachedScreenWinCount;

    /// <summary>
    /// 多屏下解析翻页条应该贴哪一块屏幕的物理矩形（500ms 节流缓存，防热路径反复 EnumWindows）。
    /// 放映时优先跟放映窗中心所在屏；非放映或找不到放映窗时跟包含主屏原点 (0,0) 的窗。
    /// </summary>
    internal RectF ResolveScreenRect(double nowMs, bool pptMode, IReadOnlyList<OverlayWindow> windows,
                                     float primaryW, float primaryH, RectF fallback)
    {
        if (windows == null || windows.Count <= 1) return fallback;
        if (!_cachedScreen.IsEmpty && _cachedScreenPpt == pptMode
            && _cachedScreenWinCount == windows.Count && nowMs - _cachedScreenAtMs < 500.0)
            return _cachedScreen;

        var rect = ComputeScreenRect(pptMode, windows, primaryW, primaryH, fallback);
        _cachedScreen = rect;
        _cachedScreenAtMs = nowMs;
        _cachedScreenPpt = pptMode;
        _cachedScreenWinCount = windows.Count;
        return rect;
    }

    private static RectF ComputeScreenRect(bool pptMode, IReadOnlyList<OverlayWindow> windows,
                                           float primaryW, float primaryH, RectF fallback)
    {
        if (windows == null || windows.Count <= 1) return fallback;

        static RectF WindowRect(OverlayWindow w) => new()
        {
            MinX = w.OriginX, MinY = w.OriginY,
            MaxX = w.OriginX + w.Width, MaxY = w.OriginY + w.Height,
        };
        static bool Contains(in RectF r, float x, float y)
            => x >= r.MinX && x < r.MaxX && y >= r.MinY && y < r.MaxY;

        if (pptMode)
        {
            var show = PptLaunch.FindShowWindow(out _);
            if (show != IntPtr.Zero && Native.GetWindowRect(show, out var sr))
            {
                float cx = (sr.Left + sr.Right) * 0.5f;
                float cy = (sr.Top + sr.Bottom) * 0.5f;
                foreach (var w in windows)
                {
                    var wr = WindowRect(w);
                    if (Contains(wr, cx, cy)) return wr;
                }
            }
        }

        foreach (var w in windows)
        {
            var wr = WindowRect(w);
            if (Contains(wr, 0f, 0f)) return wr;
        }
        if (primaryW > 0f && primaryH > 0f)
            return new RectF { MinX = 0f, MinY = 0f, MaxX = primaryW, MaxY = primaryH };
        return fallback;
    }
}
