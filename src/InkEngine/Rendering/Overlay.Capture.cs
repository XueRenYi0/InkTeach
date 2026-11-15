// [职责] 截图取景框绘制：遮罩/准线/读数/手柄。
// [测试] --capturetest

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
    // =====================================================================
    //  截图模式（8.3.0）：遮罩/挖洞（洞里是冻结帧）/取景框/准线/读数/调整手柄
    //
    //  全部画在**画布坐标**里（传进来的 canvasToTarget 决定落到哪儿）：
    //    · 实机渲染：canvasToTarget = CanvasToWindow（窗口/屏幕）；
    //    · 离屏出图（--captureshow）：canvasToTarget = 出图那条路的平移矩阵。
    //  同一份画法两条路都对（出图那条路以前只能出一圈框，现在遮罩也能拍出来）。
    // =====================================================================

    private void DrawCaptureOverlay(InkEngine app, Matrix3x2 canvasToTarget)
    {
        // 抓屏那一瞬不画我们自己的任何东西（冻结帧就抓在这一瞬，见 Engine.CaptureFrameHidden）
        if (!app.CaptureActive || app.CaptureFrameHidden) return;

        float s = Dpi / 96f;
        var amber = new Color4(1f, 0.68f, 0.10f, 1f);
        var white = new Color4(1f, 1f, 1f, 0.92f);
        var saved = _ctx.Transform;
        _ctx.Transform = canvasToTarget;

        var fr = new RectF
        {
            MinX = MathF.Min(app.CapMinX, app.CapMaxX), MinY = MathF.Min(app.CapMinY, app.CapMaxY),
            MaxX = MathF.Max(app.CapMinX, app.CapMaxX), MaxY = MathF.Max(app.CapMinY, app.CapMaxY),
        };
        var screen = app.ViewportCanvas;      // 可见画布 = 屏幕
        bool hasHole = fr.MaxX - fr.MinX >= 2f && fr.MaxY - fr.MinY >= 2f;
        string modeName = app.CaptureHideInk ? "隐藏窗口截图" : "截图";

        // ---- ⓪ 底：**冻结帧铺满整屏**（8.3.1）。
        //   取景期间整块屏幕都用"进入那一刻抓的那一张"当底：
        //     · 隐藏窗口截图：那张是**藏起我们整个覆盖层之后**抓的 → 屏幕上真的一丁点
        //       我们的东西都没有（"隐藏窗口截图"这个名字看到的样子）；
        //     · 截图：那张带着板书（我们的取景层仍然不进去）。
        //   遮罩压在它上面——"框外压暗、框里原样"来自同一张图，不会出现
        //   "框外实时、框里冻结"的错位感（8.3.0 的问题）。
        //   没有冻结帧时（出图 / 兜底）就是下面的实时内容。----
        if (app.CaptureFrozen && app.CaptureFrozenBgra != null)
        {
            var bmp = EnsureFrozenBitmap(app);
            if (bmp != null)
                _ctx.DrawBitmap(bmp,
                    new Vortice.RawRectF(app.CaptureFrozenX, app.CaptureFrozenY - app.ViewOffsetY,
                                         app.CaptureFrozenX + app.CaptureFrozenW,
                                         app.CaptureFrozenY - app.ViewOffsetY + app.CaptureFrozenH),
                    1f, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
        }

        // ---- ① 遮罩：42% 黑铺满整个屏幕，框里挖洞（EvenOdd）----
        _scratch.Color = new Color4(0f, 0f, 0f, 0.42f);
        if (!hasHole)
        {
            _ctx.FillRectangle(new Vortice.RawRectF(screen.MinX, screen.MinY, screen.MaxX, screen.MaxY), _scratch);
        }
        else
        {
            using var geo = Gfx.D2DFactory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                sink.SetFillMode(Vortice.Direct2D1.FillMode.Alternate);
                sink.BeginFigure(new Vector2(screen.MinX, screen.MinY), FigureBegin.Filled);
                sink.AddLine(new Vector2(screen.MaxX, screen.MinY));
                sink.AddLine(new Vector2(screen.MaxX, screen.MaxY));
                sink.AddLine(new Vector2(screen.MinX, screen.MaxY));
                sink.EndFigure(FigureEnd.Closed);
                sink.BeginFigure(new Vector2(fr.MinX, fr.MinY), FigureBegin.Filled);
                sink.AddLine(new Vector2(fr.MaxX, fr.MinY));
                sink.AddLine(new Vector2(fr.MaxX, fr.MaxY));
                sink.AddLine(new Vector2(fr.MinX, fr.MaxY));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            _ctx.FillGeometry(geo, _scratch);
        }

        // ---- ② 全屏准线（8.3.1：**不再自绘小十字**——系统十字在外面跟着，
        //      这两条线只回答"我现在对齐到哪一行/哪一列"）。待机/拖框/调整全程都画。----
        {
            float gx = app.PointerX, gy = app.PointerY;
            _scratch.Color = new Color4(1f, 1f, 1f, 0.14f);
            _ctx.DrawLine(new Vector2(gx, screen.MinY), new Vector2(gx, screen.MaxY), _scratch, 3f * s);
            _ctx.DrawLine(new Vector2(screen.MinX, gy), new Vector2(screen.MaxX, gy), _scratch, 3f * s);
            _scratch.Color = new Color4(amber.R, amber.G, amber.B, 0.55f);
            _ctx.DrawLine(new Vector2(gx, screen.MinY), new Vector2(gx, screen.MaxY), _scratch, 1f * s);
            _ctx.DrawLine(new Vector2(screen.MinX, gy), new Vector2(screen.MaxX, gy), _scratch, 1f * s);
        }

        // 通用胶囊按钮（调整阶段那两颗 + 右上角的「✕ 取消」共用一套画法）
        void PillButton(in RectF rc, string label, bool primary)
        {
            var r3 = new Vortice.RawRectF(rc.MinX, rc.MinY, rc.MaxX, rc.MaxY);
            _scratch.Color = primary ? amber : new Color4(0.16f, 0.17f, 0.20f, 0.86f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(r3, 8f * s, 8f * s), _scratch);
            _ctx.DrawRoundedRectangle(new RoundedRectangle(r3, 8f * s, 8f * s), Brush(white), 1.2f * s);
            _ctx.DrawText(label, CaptureInfoFormat(),
                          new Rect(r3.Left, r3.Top, r3.Right - r3.Left, r3.Bottom - r3.Top),
                          Brush(primary ? new Color4(0.12f, 0.12f, 0.14f, 1f)
                                        : new Color4(1f, 1f, 1f, 1f)));
        }

        // ---- ③ 取景框：白垫 + 琥珀线 + 四角小方块（有框才画）----
        if (hasHole)
        {
            var box = new Vortice.RawRectF(fr.MinX, fr.MinY, fr.MaxX, fr.MaxY);
            _ctx.DrawRectangle(box, Brush(white), 3.4f * s);
            _scratch.Color = amber;
            _ctx.DrawRectangle(box, _scratch, 1.8f * s);
            void CornerSq(float x, float y)
            {
                float q = 5f * s;
                var r2 = new Vortice.RawRectF(x - q * 0.5f, y - q * 0.5f, x + q * 0.5f, y + q * 0.5f);
                _ctx.FillRectangle(r2, Brush(white));
                _scratch.Color = amber;
                _ctx.DrawRectangle(r2, _scratch, 1.2f * s);
            }
            CornerSq(fr.MinX, fr.MinY); CornerSq(fr.MaxX, fr.MinY);
            CornerSq(fr.MaxX, fr.MaxY); CornerSq(fr.MinX, fr.MaxY);

            // ---- ④ 尺寸读数：框右下角外侧（贴边翻到上方/夹进屏幕）；
            //      调整阶段画在框内右下角——框下面让给「✓ ✕」两颗按钮了。----
            float rw = fr.MaxX - fr.MinX, rh = fr.MaxY - fr.MinY;
            if (rw >= 24f * s && rh >= 16f * s)
            {
                string text = $"{rw / s:F0} × {rh / s:F0}";
                float boxW = 104f * s, boxH = 26f * s, gap = 6f * s;
                float bx, by;
                if (app.CaptureAdjusting)
                {
                    bx = fr.MaxX - boxW - 8f * s;
                    by = fr.MaxY - boxH - 8f * s;
                }
                else
                {
                    bx = Math.Clamp(fr.MaxX - boxW, screen.MinX + 4f * s, screen.MaxX - boxW - 4f * s);
                    by = fr.MaxY + gap;
                    if (by + boxH > screen.MaxY - 4f * s) by = fr.MinY - gap - boxH;
                    by = Math.Clamp(by, screen.MinY + 4f * s, screen.MaxY - boxH - 4f * s);
                }
                var pill = new Vortice.RawRectF(bx, by, bx + boxW, by + boxH);
                _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.82f);
                _ctx.FillRoundedRectangle(new RoundedRectangle(pill, boxH * 0.5f, boxH * 0.5f), _scratch);
                _ctx.DrawText(text, CaptureInfoFormat(),
                              new Rect(pill.Left, pill.Top, pill.Right - pill.Left, pill.Bottom - pill.Top),
                              Brush(new Color4(1f, 1f, 1f, 1f)));
            }
        }

        // ---- ⑤ 顶部提示（模式名 + 怎么操作）----
        {
            string hint = app.CaptureAdjusting
                ? $"{modeName} · 拖动边角调整 · Enter / 双击 = 完成 · Esc / 右键 取消"
                : $"{modeName} · 拖动框选截图区域 · Ctrl+Shift+A 换模式 · Esc / 右键 取消";
            float cx = (screen.MinX + screen.MaxX) * 0.5f;
            var pill = new Vortice.RawRectF(cx - 280f * s, screen.MinY + 40f * s,
                                            cx + 280f * s, screen.MinY + 74f * s);
            _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.72f);
            _ctx.FillRoundedRectangle(new RoundedRectangle(pill, 17f * s, 17f * s), _scratch);
            _ctx.DrawText(hint, CaptureInfoFormat(),
                          new Rect(pill.Left, pill.Top, pill.Right - pill.Left, pill.Bottom - pill.Top),
                          Brush(new Color4(1f, 1f, 1f, 0.95f)));
        }

        // ---- ⑥ 调整阶段：8 个手柄 + 「✓ 完成 / ✕ 取消」；待机/拖框中：右上角「✕ 取消」----
        if (app.CaptureAdjusting)
        {
            app.CaptureHandles(out var tl, out var t, out var tr, out var r,
                               out var br, out var b, out var bl, out var l);
            void Handle(Vector2 p)
            {
                float q = 3.5f * s;
                var r2 = new Vortice.RawRectF(p.X - q, p.Y - q, p.X + q, p.Y + q);
                _ctx.FillRectangle(r2, Brush(white));
                _scratch.Color = amber;
                _ctx.DrawRectangle(r2, _scratch, 1.2f * s);
            }
            Handle(tl); Handle(t); Handle(tr); Handle(r);
            Handle(br); Handle(b); Handle(bl); Handle(l);

            app.CaptureButtons(out var okBtn, out var cancelBtn);
            PillButton(okBtn, "✓ 完成", primary: true);
            PillButton(cancelBtn, "✕ 取消", primary: false);
        }
        else
        {
            // 取景期间工具条是收起的（见 Overlay.PrepareUi）：触摸屏没有 Esc / 右键，
            // 得有一颗看得见的出口。放在右上角，不和顶部的提示打架。
            PillButton(app.CaptureCancelRect(), "✕ 取消", primary: false);
        }

        _ctx.Transform = saved;
    }

    /// <summary>
    /// 屏幕吸管（2026-10-28）：冻结帧铺满屏幕 + **跟着鼠标**的放大预览。
    ///
    /// 和微信截图取色一样：鼠标箭头可见，箭头尖就是取样点；预览贴在箭头**右下方**，
    /// 里面是 11×11 像素放大格 + 色块/HEX。靠近屏幕右边/下边时自动翻到左上，
    /// 不画全屏十字线，也不把预览钉在屏幕角落。
    /// </summary>
    internal RectF ColorPickPanelRect(InkEngine app)
    {
        float s = Dpi / 96f;
        var screen = app.ViewportCanvas;
        float w = 120f * s, h = 136f * s;
        float px = app.PointerX, py = app.PointerY;

        // 默认：箭头右下方；右边/下边放不下就翻到另一侧。
        float x = px + 18f * s;
        float y = py + 18f * s;
        if (x + w > screen.MaxX - 8f * s) x = px - 18f * s - w;
        if (y + h > screen.MaxY - 8f * s) y = py - 18f * s - h;
        x = Math.Clamp(x, screen.MinX + 8f * s,
                        MathF.Max(screen.MinX + 8f * s, screen.MaxX - 8f * s - w));
        y = Math.Clamp(y, screen.MinY + 8f * s,
                        MathF.Max(screen.MinY + 8f * s, screen.MaxY - 8f * s - h));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>预览里的“色块 + 色值”那一行；点它 = 复制当前色值（Canvas 坐标）。</summary>
    internal RectF ColorPickCopyRect(InkEngine app)
    {
        var p = ColorPickPanelRect(app);
        float s = Dpi / 96f;
        return new RectF
        {
            MinX = p.MinX + 10f * s, MinY = p.MinY + 106f * s,
            MaxX = p.MaxX - 10f * s, MaxY = p.MinY + 130f * s,
        };
    }

    /// <summary>顶部提示条（Canvas 坐标）；点它不取色，免得点到我们自己的字。</summary>
    internal RectF ColorPickHintRect(InkEngine app)
    {
        float s = Dpi / 96f;
        var screen = app.ViewportCanvas;
        float w = 440f * s, h = 34f * s;
        float x = (screen.MinX + screen.MaxX - w) * 0.5f;
        return new RectF
        {
            MinX = x, MinY = screen.MinY + 30f * s,
            MaxX = x + w, MaxY = screen.MinY + 30f * s + h,
        };
    }

    private void DrawColorPickOverlay(InkEngine app, Matrix3x2 canvasToTarget)
    {
        if (!app.ColorPickActive || app.CaptureFrameHidden) return;
        float s = Dpi / 96f;
        var screen = app.ViewportCanvas;
        var saved = _ctx.Transform;
        _ctx.Transform = canvasToTarget;

        if (app.CaptureFrozen && app.CaptureFrozenBgra != null)
        {
            var bmp = EnsureFrozenBitmap(app);
            if (bmp != null)
                _ctx.DrawBitmap(bmp,
                    new Vortice.RawRectF(app.CaptureFrozenX, app.CaptureFrozenY - app.ViewOffsetY,
                                         app.CaptureFrozenX + app.CaptureFrozenW,
                                         app.CaptureFrozenY - app.ViewOffsetY + app.CaptureFrozenH),
                    1f, Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
        }

        float px = app.PointerX, py = app.PointerY;
        var c = app.ColorPickHoverValid ? app.ColorPickHover : new Color4(1f, 1f, 1f, 1f);
        string hex = app.ColorPickHexText();

        // ---- 跟着鼠标的预览：放大格在上，色块/HEX 在下 ----
        var panel = ColorPickPanelRect(app);
        var panelR = new Vortice.RawRectF(panel.MinX, panel.MinY, panel.MaxX, panel.MaxY);
        _scratch.Color = new Color4(0.08f, 0.09f, 0.12f, 0.94f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(panelR, 10f * s, 10f * s), _scratch);

        const int cells = 11;
        const int half = cells / 2;
        float cell = 8f * s;
        float lens = cell * cells;
        float pad = 10f * s;
        float lensX = panel.MinX + pad;
        float lensY = panel.MinY + pad;

        var lensBg = new Vortice.RawRectF(lensX - 3f * s, lensY - 3f * s,
                                           lensX + lens + 3f * s, lensY + lens + 3f * s);
        _scratch.Color = new Color4(0.04f, 0.05f, 0.07f, 0.98f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(lensBg, 6f * s, 6f * s), _scratch);

        for (int yy = 0; yy < cells; yy++)
            for (int xx = 0; xx < cells; xx++)
            {
                var q = app.SampleFrozenColorAt(px + (xx - half), py + (yy - half));
                if (!q.HasValue) continue;
                var r = new Vortice.RawRectF(lensX + xx * cell, lensY + yy * cell,
                                              lensX + (xx + 1) * cell, lensY + (yy + 1) * cell);
                _scratch.Color = q.Value;
                _ctx.FillRectangle(r, _scratch);
            }

        // 中心格：取色点
        float mx = lensX + half * cell, my = lensY + half * cell;
        var center = new Vortice.RawRectF(mx - 1f * s, my - 1f * s,
                                           mx + cell + 1f * s, my + cell + 1f * s);
        _scratch.Color = new Color4(0f, 0f, 0f, 0.80f);
        _ctx.DrawRectangle(center, _scratch, 2.4f * s);
        _scratch.Color = new Color4(1f, 1f, 1f, 0.95f);
        _ctx.DrawRectangle(center, _scratch, 1f * s);

        _scratch.Color = new Color4(1f, 1f, 1f, 0.22f);
        _ctx.DrawRoundedRectangle(new RoundedRectangle(lensBg, 6f * s, 6f * s), _scratch, 1f * s);

        // 下方：色块 + HEX（点这一行 = 复制）
        float infoY = panel.MinY + 106f * s;
        var swatch = new Vortice.RawRectF(panel.MinX + pad, infoY,
                                            panel.MinX + pad + 18f * s, infoY + 20f * s);
        _ctx.FillRoundedRectangle(new RoundedRectangle(swatch, 3f * s, 3f * s), Brush(c));
        _ctx.DrawRoundedRectangle(new RoundedRectangle(swatch, 3f * s, 3f * s),
                                  Brush(new Color4(1f, 1f, 1f, 0.35f)), 1f * s);
        _ctx.DrawText(hex, CaptureInfoFormat(),
            new Rect(panel.MinX + pad + 24f * s, infoY - 2f * s,
                     panel.MaxX - pad - (panel.MinX + pad + 24f * s), 24f * s),
            Brush(new Color4(1f, 1f, 1f, 1f)));

        // 顶部提示
        var pill = ColorPickHintRect(app);
        var pillR = new Vortice.RawRectF(pill.MinX, pill.MinY, pill.MaxX, pill.MaxY);
        _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.80f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(pillR, 17f * s, 17f * s), _scratch);
        string hint = "吸管 · 点一下取色 · Ctrl+C / 点色值复制 · Esc 取消";
        _ctx.DrawText(hint, CaptureInfoFormat(),
            new Rect(pill.MinX, pill.MinY, pill.MaxX - pill.MinX, pill.MaxY - pill.MinY),
            Brush(new Color4(1f, 1f, 1f, 0.95f)));

        _ctx.Transform = saved;
    }


    // ---- 冻结帧位图缓存（每个窗口一份，按 buffer 引用判"换没换"）----

    private ID2D1Bitmap _frozenBmp;
    private byte[] _frozenSrc;
    private int _frozenW, _frozenH;

    /// <summary>放掉冻结帧位图（截图收场后调用）。**连着那份像素数组一起松开**——
    /// 只清位图不清引用的话，那张 20MB 的整屏像素会被这块缓存一直拽着不放。</summary>
    private void ReleaseFrozenBitmap()
    {
        if (_frozenBmp == null && _frozenSrc == null) return;
        _frozenBmp?.Dispose();
        _frozenBmp = null; _frozenSrc = null; _frozenW = _frozenH = 0;
    }

    private ID2D1Bitmap EnsureFrozenBitmap(InkEngine app)
    {
        if (app.CaptureFrozenBgra == null) return null;
        if (_frozenBmp != null && ReferenceEquals(_frozenSrc, app.CaptureFrozenBgra)
            && _frozenW == app.CaptureFrozenW && _frozenH == app.CaptureFrozenH)
            return _frozenBmp;
        _frozenBmp?.Dispose(); _frozenBmp = null; _frozenSrc = null;
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            96f, 96f, BitmapOptions.None);
        var handle = GCHandle.Alloc(app.CaptureFrozenBgra, GCHandleType.Pinned);
        try
        {
            _frozenBmp = _ctx.CreateBitmap(new SizeI(app.CaptureFrozenW, app.CaptureFrozenH),
                                           handle.AddrOfPinnedObject(), (uint)(app.CaptureFrozenW * 4), props);
        }
        catch { _frozenBmp = null; }
        finally { handle.Free(); }
        if (_frozenBmp != null)
        { _frozenSrc = app.CaptureFrozenBgra; _frozenW = app.CaptureFrozenW; _frozenH = app.CaptureFrozenH; }
        return _frozenBmp;
    }

    /// <summary>取景框旁边的读数/提示/按钮的文字格式（14 逻辑像素、居中、按 DPI 生成一次）。</summary>
    private IDWriteTextFormat _capInfoFmt;
    private IDWriteTextFormat CaptureInfoFormat()
    {
        if (_capInfoFmt != null) return _capInfoFmt;
        _capInfoFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 14f * (Dpi / 96f), Loc.LocaleName);
        _capInfoFmt.TextAlignment = TextAlignment.Center;
        _capInfoFmt.ParagraphAlignment = ParagraphAlignment.Center;
        return _capInfoFmt;
    }

    private float HudScale => Dpi / 96f;
    public float HudWidthPx => HudWidthLogical * HudScale;
    public float HudHeightPx => HudHeightLogical * HudScale;

    /// <summary>
    /// 性能面板的文字格式，**按 DPI 生成并缓存**。
    ///
    /// 这是"看不清"的真正原因：渲染时的变换只有平移、没有缩放，字号写死 15
    /// 就等于 15 物理像素——200% 缩放的屏上只有 7.5 逻辑像素高，投影上糊成一团。
    /// </summary>
    private IDWriteTextFormat HudFormat()
    {
        float px = MathF.Max(11f, MathF.Round(HudFontLogical * HudScale));
        if (_hudFormat == null || _hudFormatPx != px)
        {
            _hudFormat?.Dispose();
            _hudFormat = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
                FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, Loc.LocaleName);
            _hudFormatPx = px;
        }
        return _hudFormat;
    }

    /// <summary>
    /// 面板进缓存位图。**必须在帧的 BeginDraw 之前调用**——Direct2D 不允许在
    /// BeginDraw/EndDraw 中间换渲染目标（换了就把设备搞进错误状态，这条踩过）。
    ///
    /// 为什么要缓存：老实现是每帧直接排 7 行文字，实测面板自己吃掉 4.3 ms/帧，
    /// 比它显示的所有东西加起来还贵（笔迹才 2 ms）。文字是 4 Hz 才变的，
    /// 每帧重排纯属浪费。现在只有文字变了才重排一次，平时只是一次贴图。
    /// </summary>
    private void PrepareHud(InkEngine app)
    {
        if (!app.ShowHud) { _hudCacheText = null; return; }

        float scale = HudScale;
        int w = (int)MathF.Ceiling(HudWidthPx), h = (int)MathF.Ceiling(HudHeightPx);
        EnsureHudBitmap(w, h);

        string text = app.HudText ?? "";
        _hudRedrewThisFrame = false;
        if (_hudCacheText == text) return;

        _ctx.Target = _hudTarget;
        _ctx.BeginDraw();
        _ctx.Transform = Matrix3x2.Identity;
        var box = new Vortice.RawRectF(0, 0, w, h);
        _ctx.FillRectangle(box, Brush(new Color4(0.05f, 0.06f, 0.09f, 0.86f)));
        _scratch.Color = new Color4(0.35f, 0.8f, 1f, 0.9f);
        _ctx.DrawRectangle(new Vortice.RawRectF(0.5f, 0.5f, w - 0.5f, h - 0.5f), _scratch, 1.5f);
        _scratch.Color = new Color4(0.94f, 0.97f, 1f, 1f);
        // 注意 Rect 是 (x, y, width, height)，不是 (left, top, right, bottom)。
        float pad = HudPadLogical * scale;
        _ctx.DrawText(text, HudFormat(), new Rect(pad, pad * 0.8f, w - pad * 2f, h - pad * 1.6f), _scratch);
        _ctx.Transform = Matrix3x2.Identity;
        _ctx.Target = null;
        var hr = _ctx.EndDraw();
        if (hr.Failure) LastError = "hud EndDraw: " + hr.Description;
        _hudCacheText = text;
        _hudRedrewThisFrame = true;
        HudRedraws++;
    }

    private bool _hudRedrewThisFrame;

    private void EnsureHudBitmap(int w, int h)
    {
        if (_hudTarget != null && _hudBmpW == w && _hudBmpH == h) return;
        _hudSource?.Dispose(); _hudTarget?.Dispose(); _hudBmpTex?.Dispose();
        _hudSource = null; _hudTarget = null; _hudBmpTex = null;

        var desc = new Texture2DDescription
        {
            Width = (uint)Math.Max(1, w), Height = (uint)Math.Max(1, h),
            MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        _hudBmpTex = Gfx.Device.CreateTexture2D(desc);
        var pf = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                                                 Vortice.DCommon.AlphaMode.Premultiplied);
        using (var surface = _hudBmpTex.QueryInterface<IDXGISurface>())
        {
            // 一张纹理两个视图：当渲染目标的那张不能同时当绘制源（Direct2D 的规定）。
            _hudTarget = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
            _hudSource = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.None));
        }
        _hudBmpW = w; _hudBmpH = h;
        _hudCacheText = null;      // 尺寸变了必须重排
    }

    /// <summary>把缓存好的面板贴到后缓冲上（帧内调用，代价就是一次位图拷贝）。</summary>
    private void DrawHud()
    {
        if (_hudSource == null) return;
        float m = HudMarginLogical * HudScale;
        var dst = new Vortice.RawRectF(m, m, m + _hudBmpW, m + _hudBmpH);
        _ctx.DrawBitmap(_hudSource, dst, 1f,
                        Vortice.Direct2D1.InterpolationMode.NearestNeighbor, null, null);
    }

    // ---- 触点诊断浮层（8.3.3，默认关）------------------------------------

    private const float TouchHudWidthLogical = 470f;
    private const float TouchHudHeightLogical = 86f;

    /// <summary>触点诊断浮层的**屏幕坐标**矩形（脏区用；绘制那份是它的窗口局部版）。</summary>
    private RectF TouchHudRect(InkEngine app)
    {
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = TouchHudWidthLogical * s, h = TouchHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return RectF.Empty;
        return new RectF
        {
            MinX = OriginX + m, MinY = OriginY + Height - m - h,
            MaxX = OriginX + m + w, MaxY = OriginY + Height - m,
        };
    }

    private IDWriteTextFormat _touchHudFmt;
    private IDWriteTextFormat TouchHudFormat()
    {
        if (_touchHudFmt != null) return _touchHudFmt;
        _touchHudFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13.5f * (Dpi / 96f), Loc.LocaleName);
        _touchHudFmt.TextAlignment = TextAlignment.Leading;
        _touchHudFmt.ParagraphAlignment = ParagraphAlignment.Near;
        _touchHudFmt.WordWrapping = WordWrapping.NoWrap;
        return _touchHudFmt;
    }

    private void DrawTouchHud(InkEngine app)
    {
        if (!app.TouchHud) return;
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = TouchHudWidthLogical * s, h = TouchHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return;

        // 画在**窗口局部**坐标（Origin 那一份只用在脏区上，和性能面板同一个套路）。
        var box = new Vortice.RawRectF(m, Height - m - h, m + w, Height - m);
        _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.80f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s), _scratch);
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s),
                                  Brush(new Color4(1f, 1f, 1f, 0.25f)), 1f * s);
        _ctx.DrawText(app.TouchHudText ?? "", TouchHudFormat(),
                      new Rect(box.Left + 10f * s, box.Top + 6f * s,
                               box.Right - box.Left - 20f * s, box.Bottom - box.Top - 12f * s),
                      Brush(new Color4(1f, 1f, 1f, 0.95f)));
    }

    // ---- 橡皮读数浮层（8.3.7，默认关；调参用）--------------------------------

    private const float EraserHudWidthLogical = 760f;
    private const float EraserHudHeightLogical = 62f;

    /// <summary>橡皮读数浮层的**屏幕坐标**矩形（脏区用）。放在左下角（触点浮层开着就让到它上面）。</summary>
    private RectF EraserHudRect(InkEngine app)
    {
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = EraserHudWidthLogical * s, h = EraserHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return RectF.Empty;
        float bottom = Height - m - (app.TouchHud ? (TouchHudHeightLogical + 8f) * s : 0f);
        return new RectF
        {
            MinX = OriginX + m, MinY = OriginY + bottom - h,
            MaxX = OriginX + m + w, MaxY = OriginY + bottom,
        };
    }

    private IDWriteTextFormat _eraserHudFmt;
    private IDWriteTextFormat EraserHudFormat()
    {
        if (_eraserHudFmt != null) return _eraserHudFmt;
        _eraserHudFmt = Gfx.WriteFactory.CreateTextFormat(Loc.FontFamily, null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13.5f * (Dpi / 96f), Loc.LocaleName);
        _eraserHudFmt.TextAlignment = TextAlignment.Leading;
        _eraserHudFmt.ParagraphAlignment = ParagraphAlignment.Near;
        _eraserHudFmt.WordWrapping = WordWrapping.NoWrap;
        return _eraserHudFmt;
    }

    private void DrawEraserHud(InkEngine app)
    {
        if (!app.EraserHud) return;
        float s = Dpi / 96f;
        float m = HudMarginLogical * s;
        float w = EraserHudWidthLogical * s, h = EraserHudHeightLogical * s;
        if (Width <= 0 || Height <= 0) return;

        float bottom = Height - m - (app.TouchHud ? (TouchHudHeightLogical + 8f) * s : 0f);
        var box = new Vortice.RawRectF(m, bottom - h, m + w, bottom);
        _scratch.Color = new Color4(0.10f, 0.11f, 0.14f, 0.80f);
        _ctx.FillRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s), _scratch);
        _ctx.DrawRoundedRectangle(new RoundedRectangle(box, 8f * s, 8f * s),
                                  Brush(new Color4(1f, 1f, 1f, 0.25f)), 1f * s);
        _ctx.DrawText(app.EraserHudText ?? "", EraserHudFormat(),
                      new Rect(box.Left + 10f * s, box.Top + 6f * s,
                               box.Right - box.Left - 20f * s, box.Bottom - box.Top - 12f * s),
                      Brush(new Color4(1f, 1f, 1f, 0.95f)));
    }

    public void Present()
    {
        _presentRects.Clear();
        foreach (var r in _frameDirty)
        {
            int l = (int)MathF.Floor(MathF.Max(0, r.MinX - OriginX));
            int t = (int)MathF.Floor(MathF.Max(0, r.MinY - OriginY));
            int rr = (int)MathF.Ceiling(MathF.Min(Width, r.MaxX - OriginX));
            int b = (int)MathF.Ceiling(MathF.Min(Height, r.MaxY - OriginY));
            if (rr <= l || b <= t) continue;
            _presentRects.Add(new Vortice.RawRect(l, t, rr, b));
        }
        MergePresentRects();

        double area = 0;
        foreach (var r in _presentRects)
            area += (long)(r.Right - r.Left) * (r.Bottom - r.Top);

        LastPresentRectCount = _presentRects.Count;
        LastPresentAreaPercent = Width > 0 && Height > 0
            ? Math.Min(100.0, area / (Width * (double)Height) * 100.0)
            : 100.0;
        LastPresentFull = Width > 0 && Height > 0
                          && _presentRects.Count == 1
                          && _presentRects[0].Left <= 0 && _presentRects[0].Top <= 0
                          && _presentRects[0].Right >= Width && _presentRects[0].Bottom >= Height;

        if (Trace)
        {
            int buf = -1;
            try { if (_swapChain3 != null) buf = (int)_swapChain3.CurrentBackBufferIndex; } catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
            var b = RectF.Empty;
            foreach (var r in _frameDirty) { b.Add(r.MinX, r.MinY); b.Add(r.MaxX, r.MaxY); }
            int l0 = int.MaxValue, t0 = int.MaxValue, r0 = int.MinValue, bo0 = int.MinValue;
            foreach (var r in _presentRects)
            {
                if (r.Left < l0) l0 = r.Left;
                if (r.Top < t0) t0 = r.Top;
                if (r.Right > r0) r0 = r.Right;
                if (r.Bottom > bo0) bo0 = r.Bottom;
            }
            Console.WriteLine($"    [present] rects={_presentRects.Count} area={LastPresentAreaPercent:F0}% "
                              + (l0 <= r0 ? $"rectsBox=({l0},{t0})..({r0},{bo0}) " : "")
                              + $"dirtyBox=({b.MinX:F0},{b.MinY:F0})..({b.MaxX:F0},{b.MaxY:F0}) "
                              + $"camY={ViewOffsetY:F0} backBuf={buf}");
        }

        SharpGen.Runtime.Result hr;

        // 试验D（2026-10-11，A/B）：每帧 DComp 提交（Chromium 的做法；我们以前只提交过一次）。
        if (TrailCommitEveryFrame && _inkTrail != null)
        {
            try { _dcomp.Commit(); } catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
        }

        // 用等待对象先"排队"：它发信号时才是提交下一帧的正确时机。
        // 这样 Present 用 syncInterval=0 立即返回，不必让驱动在垂直同步里忙等。
        // 这一段是**配速等待**，不是绘制/Present 代价，所以单独计时。
        bool useWaitable = _latencyWait != IntPtr.Zero;
        var swWait = Stopwatch.StartNew();
        if (useWaitable)
            Native.WaitForSingleObjectEx(_latencyWait, 100, true);
        swWait.Stop();
        LastPresentWaitMs = swWait.Elapsed.TotalMilliseconds;

        LastPresentStartQpc = Qpc.Now;
        var sw = Stopwatch.StartNew();

        // 脏区太少或太大都不划算：太大不如直接整屏上屏，太少说明这一帧
        // 没什么变化（例如只等垂直同步）。--fullpresent = 永远整屏上屏（诊断）。
        if (FullPresent
            || _presentRects.Count == 0 || _presentRects.Count > 16 || LastPresentAreaPercent > 80.0)
        {
            hr = _swapChain.Present(useWaitable ? 0u : 1u, PresentFlags.None);
            LastPresentAreaPercent = 100.0;
            LastPresentRectCount = 1;       // 整屏 = 一个整屏矩形，不是“0 块”
            LastPresentFull = true;
        }
        else
        {
            hr = _swapChain.Present1(useWaitable ? 0u : 1u, PresentFlags.None,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_presentRects), null, null);
        }

        sw.Stop();
        LastPresentEndQpc = Qpc.Now;
        LastPresentMs = sw.Elapsed.TotalMilliseconds;
        // `--syswet` 收笔挂起的轨迹撤除：干墨这一帧已经提交，现在撤不会露出空档。
        if (_trailEndPending) EndInkTrailNow();
        // 轨迹"结账"（2026-10-11）：这一笔我们自己已经画上屏了，把轨迹里同样这一段删掉。
        // ⚠ 只删"自绘过"的情况；`--syswet` / 抑制自绘时不删（那时湿墨只有轨迹这一份）。
        if (!hr.Failure && _drewWetStrokeThisFrame) TrimInkTrailAsDrawn();
        _drewWetStrokeThisFrame = false;
        if (hr.Failure)
        {
            LastError = "Present: " + hr.Description;
            // 失败 = 这一帧没翻上去，双缓冲"另一块是上一帧"的前提可能不再成立。
            // 下一帧强制连续两帧整屏，把状态重新对齐
            //（对齐 Windows Terminal 的规矩：失效被消费后提交失败 → 下一帧全量重来）。
            _fullFramesLeft = 2;
        }

    }

    /// <summary>
    /// 把上屏矩形合并到最少。重叠的矩形取外接框；数量少、代价可忽略。
    ///
    /// 为什么需要：`_frameDirty` 里“整屏 + 临时图元 + UI”会同时存在，直接交给 Present1
    /// 时面积会算成 100% 以上、矩形数也会虚高，既影响“要不要整屏”的判断，也让
    /// `LastPresentAreaPercent` 这类性能数字不可信。合并后这些数才是屏幕像素占比。
    /// 只合并**有正面积重叠**的矩形；只是边/角相接的不合并，避免把中间空隙算进面积。
    /// </summary>
    private void MergePresentRects()
    {
        if (_presentRects.Count <= 1) return;
        bool again = true;
        while (again)
        {
            again = false;
            for (int i = 0; i < _presentRects.Count && !again; i++)
            {
                var a = _presentRects[i];
                for (int j = i + 1; j < _presentRects.Count; j++)
                {
                    var b = _presentRects[j];
                    bool overlap = a.Left < b.Right && b.Left < a.Right
                                && a.Top < b.Bottom && b.Top < a.Bottom;
                    if (!overlap) continue;
                    _presentRects[i] = new Vortice.RawRect(
                        Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
                        Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
                    _presentRects.RemoveAt(j);
                    again = true;
                    break;
                }
            }
        }
    }


    public void Dispose()
    {
        _brushes.Clear();
        _scratch?.Dispose();
        _readoutFormat?.Dispose();
        _readoutFormatSmall?.Dispose();
        _hudFormat?.Dispose();
        _hudSource?.Dispose();
        _hudTarget?.Dispose();
        _hudBmpTex?.Dispose();
        _pickSvBmp?.Dispose();           // 自定义取色板的缓存位图（8.2.0）
        _pickHueBmp?.Dispose();
        _frozenBmp?.Dispose();           // 截图的冻结帧位图（8.3.0）
        _tiles?.Dispose();
        _backBuffer?.Dispose();
        _inkStyle?.Dispose();          // 墨迹笔尖样式（压感变宽那条路）
        _ctx?.Dispose();
        _inkVisual?.Dispose();
        _swapVisual?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _dcomp?.Dispose();
        _dcomp2?.Dispose();
        _swapChain?.Dispose();
        _swapChain2?.Dispose();
        if (_latencyWait != IntPtr.Zero) { Native.CloseHandle(_latencyWait); _latencyWait = IntPtr.Zero; }
        if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }
}
