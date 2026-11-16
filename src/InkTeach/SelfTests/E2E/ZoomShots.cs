// [职责] 文档缩放 E2E：出图（50/100/200 + 黑墨清晰度）+ 分块倍率 / 防抖 / 预取环字节上限。
// [测试] --zoomtest

// 判据和 Zoom.cs 一致：能数出来的东西说话（像素、块字节、应用次数）。
// 这里只放"真正上屏 / 出图 / 分块纹理"这一组，避免 E2E 入口文件超过 800 行。

using System.Diagnostics;
using System.Numerics;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>E 组：真正上屏 + 50/100/200 四张出图（判据仍然是"数像素"）。</summary>
    private void ZoomTestShots(Action<string, bool, string> check)
    {
        Console.WriteLine("  -- E 上屏 + 出图：50 / 100 / 200 三张（含一条黑墨看清晰度）--");
        int baseW = DocView.At(0).Spec.RenderW > 0 ? DocView.At(0).Spec.RenderW
                                                   : DocView.At(0).Spec.OutW;
        string shotDir = System.IO.Path.GetFullPath("reports");
        try { Directory.CreateDirectory(shotDir); } catch { /* 有意忽略：出图失败不算自检失败 */ }

        void Save(string name)
        {
            string p = System.IO.Path.Combine(shotDir, name + ".bmp");
            bool ok = ScreenProbe.SaveBmp(p, _virtualX, _virtualY, _virtualW, _virtualH);
            Console.WriteLine($"    {name,-24} {(ok ? "已出图 " + p : "出图失败（不影响判据）")}");
        }

        void Shot(float zoom, string name)
        {
            SetDocZoomFromUi(zoom);
            SettleFrames(500);
            Save(name);
        }

        SetDocZoomFromUi(1f);
        SettleFrames(500);
        check("100%：页位图已生成并驻留", DocView.ResidentPages >= 1,
              $"驻留={DocView.ResidentPages} 生成={DocView.GenerateCount}");

        Shot(1f, "zoom-100");
        Shot(2f, "zoom-200");

        // 页上压一条**黑墨**：200% 的"糊/清晰"只有在墨迹上才看得出来
        // （红页本身是大色块，1× 拉伸也还是红的）。落墨位置按 **200% 的屏幕带**算
        // ——相机围绕视口中心放大，按 100% 的屏幕位置落墨会放大到屏幕外（第一版就这么错了）。
        AddZoomShotInk();
        SettleFrames(420);
        ZoomTestWaitPatch(0, 4000);
        Save("zoom-patch-200");         // 200% + 黑墨 + 高清补丁（对齐/接缝看这张）
        int inkDark200 = ScreenProbe.CountDark(_virtualX, (int)(_virtualY + _virtualH * 0.20f),
                                               (int)_virtualW, (int)(_virtualH * 0.22f));
        check("200%：墨迹真的画在页上（分块变换 = (画布−原点)×倍率）", inkDark200 > 2000,
              $"{inkDark200} 个暗像素");
        check("200%：屏幕上这块由高清补丁提供",
              DocPatch != null && DocPatch.HasPatch(0),
              $"补丁={DocPatch?.PatchCount ?? 0} / {DocPatch?.PatchBytes / 1048576.0 ?? 0:F1}MB");
        SetDocZoomFromUi(1f);
        SettleFrames(520);
        Save("zoom-ink-100");           // 同一条墨在 100% 下的样子（对照）
        Save("zoom-patch-100");         // 100% 基线（同一张墨、没有补丁）
        SetDocZoomFromUi(2f);
        SettleFrames(520);

        // 200%：视口中心压着的画布点落在页里 → 中心一块必须是红的（页真的被放大了）。
        // （不取"页中心"：放大后页中心可能在屏幕外，视口中心一定在可见范围内。）
        float centerScreenX = _virtualX + _virtualW * 0.5f;
        float centerScreenY = _virtualY + _virtualH * 0.5f;
        int red200 = ScreenProbe.CountNear((int)centerScreenX - 50, (int)centerScreenY - 50,
                                           100, 100, 255, 0, 0, 24);
        check("200%：页在屏幕上仍是红的（页图重渲成功）", red200 > 8000,
              $"{red200}/10000 像素");

        Shot(0.5f, "zoom-50");
        int red50 = CountVisiblePageRed();
        check("50%：还看得见页（缩小时不空白）", red50 > 3000, $"{red50} 像素");

        SetDocZoomFromUi(1f);
        SettleFrames(300);
        check("出图后回 100%：已生成页的位图宽 = 基准",
              DocView.At(0).Spec.BitmapW == baseW, $"{DocView.At(0).Spec.BitmapW}");
        check("100%：补丁已全部撤掉（绘制路径回到原样）",
              DocPatch != null && DocPatch.PatchCount == 0,
              $"补丁={DocPatch?.PatchCount ?? -1}");
    }

    /// <summary>
    /// 往页上写一条黑色折线（出图用）：位置按**当前相机**（E 组调用时是 200%）取
    /// 屏幕 0.30 高的一条横带，夹在首页矩形里。选 0.30 高有两个原因：
    /// 放大到 200% 时它还在屏幕内（围绕视口中心放大的点会往外跑），
    /// 缩回 100% 时它落在约 0.40 高，也不会压到 200% 判据的中心取样块。
    /// </summary>
    private void AddZoomShotInk()
    {
        var page = DocView.At(0).Rect;
        float pad = 40f;
        float x0 = MathF.Max(page.MinX + pad, ScreenToCanvasX(_virtualX + _virtualW * 0.18f));
        float x1 = MathF.Min(page.MaxX - pad, ScreenToCanvasX(_virtualX + _virtualW * 0.82f));
        float y = Math.Clamp(ScreenToCanvasY(_virtualY + _virtualH * 0.30f),
                             page.MinY + pad, page.MaxY - pad);
        if (x1 <= x0 + 40f) { x0 = page.MinX + pad; x1 = page.MaxX - pad; }

        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0f, 0f, 1f), Width = 4f,
        };
        for (int i = 0; i <= 48; i++)
        {
            float t = i / 48f;
            s.AddPoint(x0 + (x1 - x0) * t, y + MathF.Sin(t * 9f) * 12f, 1f, i * 4f);
        }
        Doc.AddStroke(s);
        Doc.InvalidateAll();
    }

    /// <summary>
    /// I 组：**分块纹理的倍率**（200% 必须 2×/512、100% 回 1×/256）、
    /// 连滚多档只换一次（防抖计数）、预取环的字节上限。
    /// </summary>
    private void ZoomTestTiles(Action<string, bool, string> check)
    {
        Console.WriteLine("  -- I 分块倍率：200% 烘 2×/512、100% 回 1×/256、连滚只换一次、环 ≤16MB --");
        var w0 = _windows[0];

        SetDocZoomFromUi(2f);
        SettleFrames(520);
        check("200%：分块 RenderScale = 2、纹理边长 512",
              MathF.Abs(w0.LastTileRenderScale - 2f) < 1e-3f && w0.LastTileTextureSize == 512,
              $"scale={w0.LastTileRenderScale:F2} tex={w0.LastTileTextureSize} 块/字节={w0.LastTileBytes}"
              + $" 块数={w0.LastTileCount}/{w0.LastTileBudget} 总={w0.LastTileCount * (long)w0.LastTileBytes / 1048576.0:F1}MB");
        check("200%：每块字节 = 512²×4（1MB）",
              w0.LastTileBytes == 512 * 512 * 4, $"{w0.LastTileBytes}");
        int ring200 = w0.LastTileRing;
        long ringB200 = w0.LastTileRingBytes;
        check("200%：预取环 ≤16MB（块数 × 块字节自洽）",
              ringB200 <= 16L * 1024 * 1024 && ringB200 == (long)ring200 * w0.LastTileBytes,
              $"{ring200} 块 / {ringB200 / 1048576.0:F1} MB");

        SetDocZoomFromUi(1f);
        SettleFrames(520);
        check("100%：分块回 1×/256（100% 像素路径不变）",
              MathF.Abs(w0.LastTileRenderScale - 1f) < 1e-3f && w0.LastTileTextureSize == 256
              && w0.LastTileBytes == 256 * 256 * 4,
              $"scale={w0.LastTileRenderScale:F2} tex={w0.LastTileTextureSize} 块/字节={w0.LastTileBytes}"
              + $" 块数={w0.LastTileCount}/{w0.LastTileBudget} 总={w0.LastTileCount * (long)w0.LastTileBytes / 1048576.0:F1}MB");
        int ring100 = w0.LastTileRing;
        long ringB100 = w0.LastTileRingBytes;
        check("100%：预取环 ≤16MB", ringB100 <= 16L * 1024 * 1024,
              $"{ring100} 块 / {ringB100 / 1048576.0:F1} MB");
        check("200% 的环块数明显少于 100%（按字节夹住，不是按块数）",
              ring200 < ring100, $"{ring100} 块 → {ring200} 块");

        SetDocZoomFromUi(0.5f);
        SettleFrames(520);
        check("50%：分块 0.5×/128（看得更多但每块更小）",
              MathF.Abs(w0.LastTileRenderScale - 0.5f) < 1e-3f && w0.LastTileTextureSize == 128
              && w0.LastTileBytes == 128 * 128 * 4,
              $"scale={w0.LastTileRenderScale:F2} tex={w0.LastTileTextureSize} 块/字节={w0.LastTileBytes}");
        check("50%：预取环 ≤16MB", w0.LastTileRingBytes <= 16L * 1024 * 1024,
              $"{w0.LastTileRing} 块 / {w0.LastTileRingBytes / 1048576.0:F1} MB");

        // 连滚 6 档（40ms 内）→ 手势期间一次都不换，停手 120ms 后只换一次。
        // NowMs 是自检可写的（和 DwellProbe 同一套），这里**显式推时钟**，
        // 免得真机一帧的耗时把 50ms 窗口撑破、测试变成看运气。
        SetDocZoomFromUi(1f);
        SettleFrames(320);
        int applies0 = w0.TileScaleApplyCount;
        double t0 = NowMs;
        float[] ramp = { 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f };
        for (int i = 0; i < ramp.Length; i++)
        {
            SetDocZoomFromUi(ramp[i]);
            NowMs = t0 + i * 8;             // 6 档 ≈ 40ms
            RenderAll();
        }
        check("连滚 6 档（≈40ms）：手势期间不换分块倍率（不每档 Reset）",
              w0.TileScaleApplyCount == applies0,
              $"应用 {w0.TileScaleApplyCount - applies0} 次，scale={w0.LastTileRenderScale:F2}");
        check("连滚期间：TileScalePending = true（主循环据此继续出帧，不会睡在旧倍率上）",
              w0.TileScalePending, $"pending={w0.TileScalePending}");

        SettleFrames(400);
        check("停手 120ms 后：只应用一次、落在 200%（2×/512）",
              w0.TileScaleApplyCount == applies0 + 1
              && MathF.Abs(w0.LastTileRenderScale - 2f) < 1e-3f && w0.LastTileTextureSize == 512,
              $"应用 {w0.TileScaleApplyCount - applies0} 次，scale={w0.LastTileRenderScale:F2}"
              + $"，tex={w0.LastTileTextureSize}");

        SetDocZoomFromUi(1f);
        SettleFrames(400);
        check("回 100%：再应用一次（合计 2 次）且回 1×/256",
              w0.TileScaleApplyCount == applies0 + 2
              && MathF.Abs(w0.LastTileRenderScale - 1f) < 1e-3f && w0.LastTileTextureSize == 256,
              $"应用 {w0.TileScaleApplyCount - applies0} 次，scale={w0.LastTileRenderScale:F2}");

        check("分块常驻不超预算（环被夹住后 Trim 仍然收得住）",
              w0.LastTileCount <= Math.Max(w0.LastTileBudget, 1),
              $"{w0.LastTileCount} 块 / 预算 {w0.LastTileBudget}");
    }

    /// <summary>把当前可见的页（按相机换算是屏幕矩形）里"红的像素"数出来。</summary>
    private int CountVisiblePageRed()
    {
        int total = 0;
        for (int i = 0; i < DocView.Count; i++)
        {
            var pp = DocView.At(i);
            if (pp.Image == null) continue;
            int x = (int)MathF.Max(_virtualX, CanvasToScreenX(pp.Rect.MinX) + 8);
            int y = (int)MathF.Max(_virtualY, CanvasToScreenY(pp.Rect.MinY) + 8);
            int w = (int)MathF.Min(_virtualX + _virtualW, CanvasToScreenX(pp.Rect.MaxX) - 8) - x;
            int h = (int)MathF.Min(_virtualY + _virtualH, CanvasToScreenY(pp.Rect.MaxY) - 8) - y;
            if (w > 4 && h > 4) total += ScreenProbe.CountNear(x, y, w, h, 255, 0, 0, 24);
        }
        return total;
    }

    // ======================================================================
    //  J 组：高清补丁（PDF >100% 的可见区设备分辨率小图）
    // ======================================================================

    /// <summary>
    /// J 组：**区域渲染对不对 / 补丁是不是一块屏 / 100% 有没有补丁 / 平移会不会灰闪 / 翻页回收**。
    ///
    /// 判据全走产品路径（SetDocZoomFromUi + 真实渲染循环）；只有"区域渲染 vs 整页渲染"
    /// 这一条直接调 PDFium 包装——它是补丁方案成立的前提（负起点裁剪 + 无接缝），
    /// 必须逐像素证明，不能只看观感。
    /// </summary>
    private void ZoomTestPatches(Action<string, bool, string> check)
    {
        Console.WriteLine("  -- J 高清补丁：区域渲染逐像素一致 / 一块屏 / 100% 无补丁 / 平移不灰闪 / 翻页回收 --");

        ZoomTestPatchRegionRender(check);

        // 100%：不建补丁。
        SetDocZoomFromUi(1f);
        SettleFrames(360);
        check("100%：没有任何补丁（绘制路径与以前逐字节一致）",
              DocPatch != null && DocPatch.PatchCount == 0 && !DocPatch.HasPatch(0),
              $"补丁={DocPatch?.PatchCount ?? -1}");

        // 200%：一块屏（不是 143MB 的整页 2×）。
        ZoomTestResetCamera();
        SetDocZoomFromUi(2f);
        SettleFrames(240);
        bool ok = ZoomTestWaitPatch(0, 4000);
        var p0 = DocView.At(0).Rect;
        float pageW = p0.MaxX - p0.MinX;
        float pageH = p0.MaxY - p0.MinY;
        long pagePx2 = (long)MathF.Round(pageW * 2f) * (long)MathF.Round(pageH * 2f);
        long patchPx = 0;
        if (DocPatch != null && DocPatch.TryGetPatch(0, out var pi, out _))
            patchPx = (long)pi.Width * pi.Height;
        check("200%：补丁就绪（后台线程），面积只是「一块屏」",
              ok && patchPx > 0 && patchPx * 4 <= 48L * 1024 * 1024 && patchPx < pagePx2 / 2,
              $"{patchPx / 1e6:F1}MP / 整页 2× {pagePx2 / 1e6:F1}MP"
              + $"（{DocPatch?.PatchBytes / 1048576.0 ?? 0:F1}MB vs {pagePx2 * 4 / 1048576.0:F0}MB）");

        // 平移 300 画布像素（= 600 设备像素，超出 256 余量）：排新补丁期间旧补丁必须一直在。
        int ver0 = DocPatch?.Version ?? 0;
        ViewOffsetY -= 300f;
        bool neverEmpty = true;
        var swp = Stopwatch.StartNew();
        while (swp.ElapsedMilliseconds < 4000)
        {
            SettleFrames(16);
            bool has = DocPatch != null && DocPatch.HasPatch(0);
            if (!has) neverEmpty = false;
            if (has && DocPatch.Version > ver0) break;
        }
        check("200% 平移：新补丁就绪前旧补丁一直在（没有「两样都没有」的帧）",
              neverEmpty && DocPatch != null && DocPatch.HasPatch(0),
              $"补丁={DocPatch?.PatchCount ?? 0} 版本 {ver0}→{DocPatch?.Version ?? 0}");

        // 翻页：离开视口的页，补丁被回收；新页的补丁随后就绪。
        float page1Top = DocView.At(1).Rect.MinY;
        ViewOffsetY = _virtualY - page1Top * ViewScale;
        SettleFrames(220);
        check("翻页：离开视口的页补丁被放掉",
              DocPatch != null && !DocPatch.HasPatch(0),
              $"第 1 页补丁={(DocPatch?.HasPatch(0) ?? false ? "还在" : "已放")}");
        bool ok1 = ZoomTestWaitPatch(1, 4000);
        check("翻页：新页补丁就绪（不是整本重渲）", ok1,
              $"补丁={DocPatch?.PatchCount ?? 0} / {DocPatch?.PatchBytes / 1048576.0 ?? 0:F1}MB");

        // 收尾：回 100%，后续组不受影响。
        SetDocZoomFromUi(1f);
        ZoomTestResetCamera();
        SettleFrames(120);
    }

    /// <summary>
    /// 区域渲染 vs 整页渲染：重叠区必须逐像素一致（允许抗锯齿的极小差异）。
    /// 这一条直接证明 `FPDF_RenderPageBitmap(startX = -rx, startY = -ry)` 在本仓的
    /// PDFium 版本上**确实按负起点裁剪**，也证明补丁和底图不会错位/出接缝。
    /// 用带文字和细线的合成 PDF（纯色块看不出接缝）。
    /// </summary>
    private void ZoomTestPatchRegionRender(Action<string, bool, string> check)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-zoomtest");
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "区域文字.pdf");
        WriteTestTextPdf(path);

        if (!Pdfium.Open(path, out var doc, out string err))
        {
            check("区域渲染：打开文字 PDF", false, err);
            return;
        }
        try
        {
            var (pw, ph) = doc.Size(0);
            int fullW = Math.Max(1, Native.GetSystemMetrics(0));
            int fullH = Math.Max(1, (int)MathF.Round(ph * (fullW / pw)));
            byte[] full = doc.RenderPage(0, fullW, fullH);
            int rx = fullW / 8, ry = fullH / 20;
            int rw = Math.Max(1, fullW / 2), rh = Math.Max(1, fullH / 2);
            byte[] part = doc.RenderRegion(0, fullW, fullH, rx, ry, rw, rh);

            long bad = 0, dark = 0, compared = 0;
            if (full != null && part != null
                && full.Length >= (long)fullW * fullH * 4
                && part.Length >= (long)rw * rh * 4)
            {
                for (int y = 2; y < rh - 2; y++)
                {
                    int fo = ((y + ry) * fullW + rx) * 4;
                    int po = y * rw * 4;
                    for (int x = 2; x < rw - 2; x++, fo += 4, po += 4)
                    {
                        compared++;
                        int d = Math.Abs(full[fo] - part[po])
                              + Math.Abs(full[fo + 1] - part[po + 1])
                              + Math.Abs(full[fo + 2] - part[po + 2]);
                        if (d > 6) bad++;
                        if (part[po] < 60 && part[po + 1] < 60 && part[po + 2] < 60) dark++;
                    }
                }
            }
            check("区域渲染：与整页渲染在重叠区逐像素一致（负起点裁剪正确、无接缝）",
                  compared > 0 && bad * 10000 < compared,
                  $"{bad}/{compared} 像素有差异（{rw}×{rh} @ {rx},{ry}）");
            check("区域渲染：这块里确实有文字/线条（不是空白）", dark > 200,
                  $"{dark} 个暗像素");
        }
        finally
        {
            doc.Dispose();
            try { System.IO.File.Delete(path); } catch { /* 有意忽略：临时文件删不掉不影响判据 */ }
        }
    }

    /// <summary>等这一页的补丁就绪（最多 <paramref name="maxMs"/> 毫秒；期间照常出帧）。</summary>
    private bool ZoomTestWaitPatch(int page, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (DocPatch != null && DocPatch.HasPatch(page)) return true;
            SettleFrames(16);
        }
        return DocPatch != null && DocPatch.HasPatch(page);
    }

    private static RectF Intersect(in RectF a, in RectF b) => new()
    {
        MinX = MathF.Max(a.MinX, b.MinX),
        MinY = MathF.Max(a.MinY, b.MinY),
        MaxX = MathF.Min(a.MaxX, b.MaxX),
        MaxY = MathF.Min(a.MaxY, b.MaxY),
    };

    private static RectF Expand(in RectF a, float d) => new()
    {
        MinX = a.MinX - d,
        MinY = a.MinY - d,
        MaxX = a.MaxX + d,
        MaxY = a.MaxY + d,
    };

    /// <summary>outer 是否完整盖住 inner（半像素容差：相机渲染偏移取整会差 &lt;1px）。</summary>
    private static bool CoversRect(in RectF outer, in RectF inner)
        => outer.MinX <= inner.MinX + 0.5f && outer.MinY <= inner.MinY + 0.5f
        && outer.MaxX >= inner.MaxX - 0.5f && outer.MaxY >= inner.MaxY - 0.5f;
}
