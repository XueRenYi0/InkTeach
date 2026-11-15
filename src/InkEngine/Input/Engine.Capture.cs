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
    //  截图
    //
    //  流程（每一步都有它非这么做不可的理由）：
    //
    //   1. 拖一个框（和框选同一个手势，用户不用学新动作）；
    //   2. **把自己的覆盖层藏起来**再抓屏——不然抓到的就是"批注盖在 PPT 上"
    //      那一坨，等于把老师写的字又贴回去一张；
    //   3. 抓到的是一比一的**物理像素**，按 DPI 折成画布尺寸；
    //   4. 放到**视口左上角**并立刻选中——用户看得见、拖得动、能接着复制/翻转；
    //   5. 同时写进剪贴板：老师要把它贴到别的地方（PPT、微信、Word）是常见动作。
    //  =====================================================================

    /// <summary>
    /// 覆盖层藏起来时用的句柄列表。**多屏也要一起藏**，否则副屏上抓到的还是
    /// 我们自己画的东西（多屏教室很常见）。
    /// </summary>
    private IntPtr[] OverlayHandles()
    {
        var list = new List<IntPtr>(_windows.Count);
        foreach (var w in _windows) list.Add(w.Hwnd);
        // 抓屏时要连"接输入小窗"一起藏：它是 1/255 的一层灰，肉眼看不见，
        // 但拍进图里就是一层脏（截图界面永远不该出现在截图里）。
        // PPT 条那块小窗同理（它也是我们的窗口）。
        if (_uiInputHwnd != IntPtr.Zero) list.Add(_uiInputHwnd);
        if (_pptInputHwnd != IntPtr.Zero) list.Add(_pptInputHwnd);
        if (_timerInputHwnd != IntPtr.Zero) list.Add(_timerInputHwnd);
        if (_rollInputHwnd != IntPtr.Zero) list.Add(_rollInputHwnd);
        return list.ToArray();
    }

    /// <summary>截图落地的位置：**视口左上角**往里缩一点（见 CaptureMargin 的注释）。</summary>
    internal const float CaptureMarginLogical = 24f;

    /// <summary>
    /// 松手：**先不定局**（8.3.0）——框太小**不退出取景**（8.3.1：单击一下只当"还没想好"，
    /// 回到等拖框的状态），否则进入"调整"阶段等确认。
    /// 抓屏已经在进入取景时做完了（冻结帧），这里只判尺寸、切状态。
    /// </summary>
    private void EndCapture()
    {
        _dirty = true;

        var s = CaptureFrameScreen();
        int w = (int)MathF.Round(s.MaxX - s.MinX);
        int h = (int)MathF.Round(s.MaxY - s.MinY);
        float minPx = CaptureMinSizeLogical * DpiScale;
        if (w < minPx || h < minPx)
        {
            // 单击（没拖出框）：**继续取景**，不要整个退掉——
            // 老师的动作常常是"点一下看看、再拖"，一下点掉整个模式太凶。
            CapMinX = CapMaxX = PointerX;
            CapMinY = CapMaxY = PointerY;
            _capDrag = CaptureDrag.None;
            Console.WriteLine("截图：没拖出框，继续取景");
            return;
        }

        CaptureAdjusting = true;
        _capDrag = CaptureDrag.None;
        Console.WriteLine($"截图：框 {w}×{h} 物理像素（松开后可调整：拖边/拖角/拖整体，"
                          + "Enter/双击 或点「✓」完成，Esc 取消）");
    }

    /// <summary>
    /// **确认截图**：裁冻结帧 → 剪贴板 → **原位落图** → 选中、切回框选。
    /// 返回是否真的落了图。`Enter` / 点「✓ 完成」都走它。
    /// </summary>
    internal bool ConfirmCapture()
    {
        if (!CaptureActive) return false;
        var s = CaptureFrameScreen();
        int x = (int)MathF.Round(s.MinX), y = (int)MathF.Round(s.MinY);
        int w = (int)MathF.Round(s.MaxX - s.MinX), h = (int)MathF.Round(s.MaxY - s.MinY);
        if (w <= 0 || h <= 0) { CancelCapture(); return false; }

        byte[] pixels = CaptureFrozen
            ? CropCaptureFrozen(x, y, w, h)
            : GrabCaptureNow(x, y, w, h);          // 兜底：冻结没抓到 → 老路径现抓

        // 状态先收干净：遮罩/框/冻结帧立刻消失（屏幕上"啪"地回到常态）
        CaptureActive = false;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        ReleaseCaptureFrozen();
        _dirty = true;

        if (pixels == null)
        {
            Console.WriteLine("截图失败：抓屏返回空");
            return false;
        }

        // **不再默认写系统剪贴板**（2026-11-03 用户："截图可能也会默认进剪贴板，
        // 这样就不用进剪贴板了"）：截图只落图、选中，要外发走「导出」或选中后 Ctrl+C。
        var img = ImageData.Adopt(w, h, pixels);
        if (img == null) return false;

        // **原位落**（8.3.0，用户定的）：画布坐标 = 屏幕坐标 − 相机纵向偏移（x 不滚）。
        // 夹进视口、留一圈边距——**边距是必须的**：贴着 (0,0) 的话选中框的旋转手柄会跑到屏幕外。
        //
        // ⚠ `scale = 1`：**1 物理像素 = 1 画布单位**（画布坐标就是物理像素，见 ViewportCanvas
        //   与 `--imagetest` 的上屏探针）。旧版写的是 `1f / DpiScale`，那是把图**缩小 DpiScale 倍**——
        //   200% 缩放的屏上贴出来只有一半大（用户 2026-09-30："截图感觉也不对"，这条是硬伤）。
        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        float px = Math.Clamp(x, vp.MinX + m, MathF.Max(vp.MinX + m, vp.MaxX - m - w));
        float py = Math.Clamp(y - ViewOffsetY, vp.MinY + m, MathF.Max(vp.MinY + m, vp.MaxY - m - h));
        var placed = Doc.AddImage(img, px, py, 1f);
        if (placed != null)
        {
            Doc.SelectOnly(new[] { placed });
            Tool = Tool.Marquee;             // 刚截完图，最可能的下一件事是摆放它
            SetInkStatus(Loc.T("截图 {0}×{1} 已落图并选中", w, h));
        }
        Console.WriteLine($"截图 {w}×{h} 物理像素 → 原位落在画布 ({px:F0},{py:F0})，已选中");
        RegainKeyboardFocus();          // 回到批注态：前台/键盘要回来（见那段注释）
        return placed != null;
    }

    /// <summary>
    /// 兜底：冻结帧不在（按下时抓失败）时按老路径**现抓**——
    /// 隐藏界面模式藏整个覆盖层（60ms 的闪就闪在这里），直接截取只藏我们的框/光标。
    /// </summary>
    private byte[] GrabCaptureNow(int x, int y, int w, int h)
    {
        if (CaptureHideInk)
        {
            using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                return ScreenCapture.Grab(x, y, w, h);
        }
        CaptureFrameHidden = true;
        _dirty = true;
        RenderAll();
        Native.DwmFlush();
        var p = ScreenCapture.Grab(x, y, w, h);
        CaptureFrameHidden = false;
        _dirty = true;
        return p;
    }

    /// <summary>
    /// 取消截图（Esc / 右键 / 「✕ 取消」/ 点框外）：什么都不落，把冻结帧和状态收干净，
    /// 并且**回到进截图之前用的工具**（8.3.1；不再是"留在截图工具上空等"）。
    /// </summary>
    internal void CancelCapture()
    {
        if (!CaptureActive) return;
        CaptureActive = false;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capturePressHandled = false;
        ReleaseCaptureFrozen();
        var back = _toolBeforeCapture;
        if (back == Tool.Capture) back = Tool.Pen;      // 兜底，正常走不到
        SwitchTool(back);
        _dirty = true;
        ApplyCursor();
        NotifyUiStateChanged();
        RegainKeyboardFocus();          // 回到批注态：前台/键盘要回来（见那段注释）
        Console.WriteLine("截图取消");
    }

    /// <summary>「✕ 取消」那颗按钮（**画布坐标**）：待机/拖框中一直挂在右上角。</summary>
    internal RectF CaptureCancelRect()
    {
        float s = DpiScale;
        float w = CaptureBtnWLogical * s, h = CaptureBtnHLogical * s;
        var vp = ViewportCanvas;
        float pad = 16f * s;
        return new RectF
        {
            MinX = vp.MaxX - pad - w, MinY = vp.MinY + pad,
            MaxX = vp.MaxX - pad, MaxY = vp.MinY + pad + h,
        };
    }

    internal bool CaptureCancelHit(float x, float y) => CaptureCancelRect().Contains(x, y);

    /// <summary>
    /// 调整阶段的两颗按钮：「✓ 完成」「✕ 取消」。
    /// AI 不放在这里（2026-11-11 用户定）：截完就是一张图片，选中后按 AI 即可，
    /// 截图条只留“完成/取消”，少两颗按钮、少一次误触。
    /// </summary>
    internal void CaptureButtons(out RectF ok, out RectF cancel)
    {
        float w = CaptureBtnWLogical * DpiScale, h = CaptureBtnHLogical * DpiScale;
        float gap = CaptureBtnGapLogical * DpiScale;
        var vp = ViewportCanvas;
        float total = w * 2 + gap;
        float x0 = CapMaxX - total;
        x0 = Math.Clamp(x0, vp.MinX + 4f, MathF.Max(vp.MinX + 4f, vp.MaxX - total - 4f));
        float y0 = CapMaxY + gap;
        if (y0 + h > vp.MaxY) y0 = CapMinY - gap - h;              // 下方放不下 → 放框上方
        y0 = Math.Clamp(y0, vp.MinY + 4f, MathF.Max(vp.MinY + 4f, vp.MaxY - h - 4f));
        cancel = new RectF { MinX = x0, MinY = y0, MaxX = x0 + w, MaxY = y0 + h };
        ok = new RectF { MinX = x0 + w + gap, MinY = y0, MaxX = x0 + w + gap + w, MaxY = y0 + h };
    }

    /// <summary>指针压在哪颗按钮上（✓ / ✕）——光标形状要用。</summary>
    internal bool CaptureButtonHit(float x, float y)
    {
        CaptureButtons(out var ok, out var cancel);
        return ok.Contains(x, y) || cancel.Contains(x, y);
    }

    /// <summary>调整阶段 8 个手柄里，指针压在哪个上（没有 = None）。拖动与光标共用同一套判据。</summary>
    internal CaptureDrag CaptureHandleAt(float x, float y)
    {
        CaptureHandles(out var tl, out var t, out var tr, out var r,
                       out var br, out var b, out var bl, out var l);
        float hit = CaptureHandleHitLogical * DpiScale;
        if (Near(x, y, tl, hit)) return CaptureDrag.TL;
        if (Near(x, y, tr, hit)) return CaptureDrag.TR;
        if (Near(x, y, bl, hit)) return CaptureDrag.BL;
        if (Near(x, y, br, hit)) return CaptureDrag.BR;
        if (Near(x, y, l, hit)) return CaptureDrag.L;
        if (Near(x, y, r, hit)) return CaptureDrag.R;
        if (Near(x, y, t, hit)) return CaptureDrag.T;
        if (Near(x, y, b, hit)) return CaptureDrag.B;
        return CaptureDrag.None;
    }

    /// <summary>
    /// 截图模式下的按下（8.3.1）：待机 / 拖框 / 调整三种状态先都过这里。
    /// 返回 true = 这一下被截图吃掉；返回 false = 照常走工具分流（也就是"从这里起框"）。
    /// </summary>
    private bool CapturePointerDown(float x, float y)
    {
        if (!CaptureAdjusting)
        {
            // 待机/拖框中：右上角那颗「✕ 取消」
            if (CaptureCancelHit(x, y)) { CancelCapture(); return true; }
            return false;                            // 其余交给 `case Tool.Capture` 起框
        }

        // ---- 调整阶段 ----
        CaptureButtons(out var ok, out var cancel);
        if (ok.Contains(x, y)) { ConfirmCapture(); return true; }
        if (cancel.Contains(x, y)) { CancelCapture(); return true; }

        _capDragX = x; _capDragY = y;
        _capD0MinX = CapMinX; _capD0MinY = CapMinY; _capD0MaxX = CapMaxX; _capD0MaxY = CapMaxY;
        _capDrag = CaptureHandleAt(x, y);
        if (_capDrag == CaptureDrag.None)
        {
            bool inside = x >= CapMinX && x <= CapMaxX && y >= CapMinY && y <= CapMaxY;
            if (!inside) { CancelCapture(); return true; }   // 点框外 = 取消（微信/QQ 的口径）
            // 双击框内 = 完成（微信的手感）：400ms、6 逻辑像素以内算同一处
            double now = NowMs;
            bool dbl = now - _capLastClickMs < 400
                       && MathF.Abs(x - _capLastClickX) < 6f * DpiScale
                       && MathF.Abs(y - _capLastClickY) < 6f * DpiScale;
            _capLastClickMs = now; _capLastClickX = x; _capLastClickY = y;
            if (dbl) { ConfirmCapture(); return true; }
            _capDrag = CaptureDrag.Move;
        }
        _dirty = true;
        return true;
    }

    /// <summary>调整阶段的 8 个手柄中心（左上/上中/右上/右中/右下/下中/左下/左中）。</summary>
    internal void CaptureHandles(out Vector2 tl, out Vector2 t, out Vector2 tr, out Vector2 r,
                                out Vector2 br, out Vector2 b, out Vector2 bl, out Vector2 l)
    {
        float cx = (CapMinX + CapMaxX) * 0.5f, cy = (CapMinY + CapMaxY) * 0.5f;
        tl = new Vector2(CapMinX, CapMinY); t = new Vector2(cx, CapMinY);
        tr = new Vector2(CapMaxX, CapMinY); r = new Vector2(CapMaxX, cy);
        br = new Vector2(CapMaxX, CapMaxY); b = new Vector2(cx, CapMaxY);
        bl = new Vector2(CapMinX, CapMaxY); l = new Vector2(CapMinX, cy);
    }

    private static bool Near(float x, float y, Vector2 p, float r)
        => MathF.Abs(x - p.X) <= r && MathF.Abs(y - p.Y) <= r;

    /// <summary>
    /// 调整阶段的**方向键微调**（8.3.1）：方向键 = 框整体挪 1 逻辑像素（投影上鼠标很难微调），
    /// `Shift` + 方向键 = 改大小（右下那两条边）。
    /// </summary>
    private void CaptureNudge(int vk)
    {
        if (!CaptureAdjusting) return;
        float s = 1f * DpiScale;
        float dx = vk == 0x25 ? -s : vk == 0x27 ? s : 0f;
        float dy = vk == 0x26 ? -s : vk == 0x28 ? s : 0f;
        bool shift = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
        var vs = VirtualScreen;
        float minYc = vs.MinY - ViewOffsetY, maxYc = vs.MaxY - ViewOffsetY;
        float minSz = CaptureMinSizeLogical * DpiScale;
        if (shift)
        {
            CapMaxX = Math.Clamp(CapMaxX + dx, CapMinX + minSz, vs.MaxX);
            CapMaxY = Math.Clamp(CapMaxY + dy, CapMinY + minSz, maxYc);
        }
        else
        {
            float w = CapMaxX - CapMinX, h = CapMaxY - CapMinY;
            float l = Math.Clamp(CapMinX + dx, vs.MinX, vs.MaxX - w);
            float t = Math.Clamp(CapMinY + dy, minYc, maxYc - h);
            CapMinX = l; CapMaxX = l + w;
            CapMinY = t; CapMaxY = t + h;
        }
        _dirty = true;
    }

    /// <summary>调整阶段拖动：整体移 / 拖某条边 / 拖某个角，夹在屏幕里、不小于最小尺寸。</summary>
    private void CaptureAdjustMove(float x, float y)
    {
        if (_capDrag == CaptureDrag.None) return;
        float dx = x - _capDragX, dy = y - _capDragY;
        float minSz = CaptureMinSizeLogical * DpiScale;
        var vs = VirtualScreen;
        float minYc = vs.MinY - ViewOffsetY, maxYc = vs.MaxY - ViewOffsetY;

        float l = _capD0MinX, r = _capD0MaxX, t = _capD0MinY, b = _capD0MaxY;
        switch (_capDrag)
        {
            case CaptureDrag.Move: l += dx; r += dx; t += dy; b += dy; break;
            case CaptureDrag.L: l = MathF.Min(l + dx, r - minSz); break;
            case CaptureDrag.R: r = MathF.Max(r + dx, l + minSz); break;
            case CaptureDrag.T: t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.B: b = MathF.Max(b + dy, t + minSz); break;
            case CaptureDrag.TL:
                l = MathF.Min(l + dx, r - minSz); t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.TR:
                r = MathF.Max(r + dx, l + minSz); t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.BL:
                l = MathF.Min(l + dx, r - minSz); b = MathF.Max(b + dy, t + minSz); break;
            case CaptureDrag.BR:
                r = MathF.Max(r + dx, l + minSz); b = MathF.Max(b + dy, t + minSz); break;
        }
        // 夹在屏幕里；夹完尺寸可能被压小 → 按"拖的是哪条边"把对边让回去，守住最小尺寸
        l = Math.Clamp(l, vs.MinX, vs.MaxX); r = Math.Clamp(r, vs.MinX, vs.MaxX);
        t = Math.Clamp(t, minYc, maxYc);   b = Math.Clamp(b, minYc, maxYc);
        if (r - l < minSz)
        {
            if (_capDrag is CaptureDrag.L or CaptureDrag.TL or CaptureDrag.BL) l = r - minSz;
            else r = l + minSz;
        }
        if (b - t < minSz)
        {
            if (_capDrag is CaptureDrag.T or CaptureDrag.TL or CaptureDrag.TR) t = b - minSz;
            else b = t + minSz;
        }
        CapMinX = l; CapMaxX = r; CapMinY = t; CapMaxY = b;
        _dirty = true;
    }

    /// <summary>调整拖动收尾（抬手）。</summary>
    private void EndCaptureAdjustDrag()
    {
        _capDrag = CaptureDrag.None;
        _dirty = true;
    }

}
