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
    /// J3：**补丁缓冲复用**（2026-11-16 内存抖动修复的判据）。
    ///
    /// 100→200→100 三轮：每轮回 100% 后 `ImageData.LiveBytes` 必须回到基线（±5MB），
    /// 且第二、三轮渲新补丁时命中单槽（`PatchBufferReuseCount` 涨）——
    /// 也就是不再每轮新分配一块 ~25MB 的 LOH（那正是私有字节每轮 +40~50MB 的来源）。
    /// </summary>
    private void ZoomTestPatchBufferPool(Action<string, bool, string> check)
    {
        Console.WriteLine("  -- J3 补丁缓冲复用：100→200→100 ×3（回基线 / 第二轮起命中单槽）--");

        SetDocZoomFromUi(1f);
        ZoomTestResetCamera();
        SettleFrames(240);
        long baseline = ImageData.LiveBytes;

        bool reused2 = false, reused3 = false;
        long maxAt200 = 0, last100 = baseline;
        var detail = new System.Text.StringBuilder();
        for (int c = 1; c <= 3; c++)
        {
            int reuseBefore = DocPatch?.PatchBufferReuseCount ?? 0;
            SetDocZoomFromUi(2f);
            SettleFrames(220);
            ZoomTestWaitPatch(0, 4000);
            long at200 = ImageData.LiveBytes;
            if (at200 > maxAt200) maxAt200 = at200;

            SetDocZoomFromUi(1f);
            bool back = ZoomTestWaitLiveBytes(baseline, 5L << 20, 4000);
            last100 = ImageData.LiveBytes;
            int reuseAfter = DocPatch?.PatchBufferReuseCount ?? 0;
            if (c == 2) reused2 = reuseAfter > reuseBefore;
            if (c == 3) reused3 = reuseAfter > reuseBefore;
            detail.Append($"c{c} {at200 / 1048576.0:F1}→{last100 / 1048576.0:F1}MB"
                        + $"{(back ? "" : "（未回落）")} ");
        }

        long delta = last100 - baseline;
        check("补丁 CPU 像素账回 100% 基线（±5MB，三轮后）",
              Math.Abs(delta) <= 5L * 1024 * 1024,
              $"基线 {baseline / 1048576.0:F1}MB → 末轮 100% {last100 / 1048576.0:F1}MB"
              + $"（Δ {delta / 1048576.0:+0.0;-0.0}MB；200% 峰值 {maxAt200 / 1048576.0:F1}MB）");
        check("第二轮起：新补丁命中单槽（不再每轮新分配一块）",
              reused2 && reused3,
              $"复用 {DocPatch?.PatchBufferReuseCount ?? 0} 次（c2={reused2} c3={reused3}）"
              + $" | {detail.ToString().Trim()}");
    }

    /// <summary>等到 `ImageData.LiveBytes` 落进 target±tol（期间照常出帧）；超时返回最后一次判断。</summary>
    private bool ZoomTestWaitLiveBytes(long target, long tol, int maxMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (Math.Abs(ImageData.LiveBytes - target) <= tol) return true;
            SettleFrames(16);
        }
        return Math.Abs(ImageData.LiveBytes - target) <= tol;
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

            // 复用缓冲（2026-11-16 内存修复的前提）：**渲进调用方给的脏数组**，
            // 像素必须和"每次新分配"逐字节相同——池子不许改变输出。
            // 数组故意比需要的大一圈（真实单槽就是这样：够长就用），尾巴不读。
            int need = rw * rh * 4;
            byte[] reuse = new byte[need + 4096];
            for (int i = 0; i < reuse.Length; i++) reuse[i] = 0x5A;
            byte[] again = doc.RenderRegion(0, fullW, fullH, rx, ry, rw, rh, reuse);
            long diff = -1;
            if (again != null && part != null && again.Length >= need)
            {
                diff = 0;
                for (int i = 0; i < need; i++) if (again[i] != part[i]) diff++;
            }
            check("复用缓冲：渲进调用方数组，像素与每次新分配逐字节相同",
                  ReferenceEquals(again, reuse) && diff == 0,
                  diff < 0 ? "复用渲染失败" : $"{diff}/{need} 字节不同（同一块数组，长 {reuse.Length}）");
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

    // ======================================================================
    //  J2 组：驱动窗口 / 换倍率不闪 / 渲到一半关文档（2026-11-16 评审 P0/P1）
    // ======================================================================

    /// <summary>
    /// J2：**多窗口驱动**（投影仪排第一也不影响主屏）、**换倍率不闪**（150→200 补丁不归零）、
    /// **渲到一半关文档**（×20 不崩）、以及"打开失败不动当前文档"。
    ///
    /// 这一组会反复开关文档，所以排在 `--zoomtest` 的最后（`ZoomTestWorker` 之后）：
    /// 前面的组都还在用同一个打开的 PDF。
    /// </summary>
    private void ZoomTestPatchStress(Action<string, bool, string> check, string pdf)
    {
        Console.WriteLine("  -- J2 驱动窗口 / 150→200 不闪 / 渲到一半关文档 ×20 / 打开失败不动当前文档 --");
        ZoomTestPatchDriver(check);
        ZoomTestPatchOpenFailure(check);
        ZoomTestPatchScaleSwitch(check);
        ZoomTestPatchCloseDuringRender(check, pdf);
    }

    /// <summary>
    /// 驱动窗口怎么挑：**压在页上的那块屏**，不是"枚举到的第一个窗口"。
    /// 用伪造的屏幕矩形验（真机上把投影仪插到第一位才会踩到这个 bug），
    /// 再用真实窗口验一次"挑中的那块确实压在页上"。
    /// </summary>
    private void ZoomTestPatchDriver(Action<string, bool, string> check)
    {
        var page = DocView.At(0).Rect;
        float halfW = (page.MaxX - page.MinX) * 0.5f;
        int primW = Native.GetSystemMetrics(0);
        int primH = Native.GetSystemMetrics(1);
        if (primW <= 0) primW = _virtualW;
        if (primH <= 0) primH = _virtualH;

        // 投影仪排在 _windows[0]：它的可见矩形整块在页右边（页锚在主屏中心，投影仪上什么都没有）
        var projCanvas = new RectF
        {
            MinX = page.MaxX + 200f, MinY = page.MinY,
            MaxX = page.MaxX + 200f + 800f, MaxY = page.MinY + 800f,
        };
        var projScreen = new RectF
        {
            MinX = primW + 200f, MinY = 0f,
            MaxX = primW + 200f + _virtualW, MaxY = _virtualH,
        };
        var mainScreen = new RectF { MinX = 0f, MinY = 0f, MaxX = primW, MaxY = primH };

        int pick = ChooseDocPatchDriver(
            new[] { projCanvas, page }, new[] { projScreen, mainScreen });
        check("驱动窗口：投影仪排在第一（不压页）时选压着页的主屏", pick == 1, $"选中 #{pick}");

        // 面积相同 → 取包含主屏中心的那块（页锚在主屏中心）
        var leftHalf = new RectF { MinX = page.MinX, MinY = page.MinY, MaxX = page.MinX + halfW, MaxY = page.MaxY };
        var rightHalf = new RectF { MinX = page.MinX + halfW, MinY = page.MinY, MaxX = page.MaxX, MaxY = page.MaxY };
        int tie = ChooseDocPatchDriver(
            new[] { leftHalf, rightHalf }, new[] { projScreen, mainScreen });
        check("驱动窗口：重叠面积相同时取包含主屏中心的那个", tie == 1, $"选中 #{tie}");

        // 真实窗口：挑中的那块必须真的压在页上（不然补丁永远排不出来）
        int n = _windows.Count;
        var canvases = new RectF[n];
        var screens = new RectF[n];
        for (int i = 0; i < n; i++)
        {
            var w = _windows[i];
            canvases[i] = w.VisibleCanvasRect;
            screens[i] = new RectF
            {
                MinX = w.OriginX, MinY = w.OriginY,
                MaxX = w.OriginX + w.Width, MaxY = w.OriginY + w.Height,
            };
        }
        int real = ChooseDocPatchDriver(canvases, screens);
        var hit = real >= 0 ? Intersect(canvases[real], page) : RectF.Empty;
        check("驱动窗口：本机真实窗口里挑中的那块确实压在页上",
              real >= 0 && !hit.IsEmpty && hit.MaxX > hit.MinX && hit.MaxY > hit.MinY,
              real < 0 ? "没有候选" : $"#{real}，与页重叠 {hit.MaxX - hit.MinX:F0}×{hit.MaxY - hit.MinY:F0}");
    }

    /// <summary>
    /// 打开新文档失败时**不能动当前文档**（评审 P0-1 的失败回滚）：
    /// 打开前会先停掉旧补丁 worker，失败必须把补丁缓存补回给还开着的旧文档。
    /// </summary>
    private void ZoomTestPatchOpenFailure(Action<string, bool, string> check)
    {
        int pagesBefore = DocView.Count;
        string missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "inkteach-zoomtest", "这份文件不存在.pdf");
        try { System.IO.File.Delete(missing); } catch { /* 有意忽略：删不掉就是本来没有 */ }

        string err = OpenDocuments(new[] { missing });
        SettleFrames(120);
        check("打开失败：仍开着原来那份（页数不变）、补丁缓存还在",
              err != null && DocView.IsOpen && DocView.Count == pagesBefore && DocPatch != null,
              err == null ? "竟然打开了" : $"{err}；页={DocView.Count} 补丁={(DocPatch != null)}");
        check("打开失败：旧文档的高清补丁能被重新排出来（缓存是新建的、空的）",
              DocPatch != null && DocPatch.PatchCount == 0 && ZoomTestWaitPatch(0, 4000),
              $"补丁={DocPatch?.PatchCount ?? -1} / {DocPatch?.PatchBytes / 1048576.0 ?? 0:F1}MB");
    }

    /// <summary>
    /// 换倍率**不许把当前补丁放掉**（评审 P1-3）：150% → 200% 全程 `PatchCount` 一直是 1，
    /// 拉伸着画旧补丁，直到新倍率那块被采纳。
    /// </summary>
    private void ZoomTestPatchScaleSwitch(Action<string, bool, string> check)
    {
        SetDocZoomFromUi(1.5f);
        SettleFrames(320);
        bool ok150 = ZoomTestWaitPatch(0, 4000);
        int before = DocPatch?.PatchCount ?? 0;

        SetDocZoomFromUi(2f);
        bool neverZero = true;
        int minSeen = int.MaxValue;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            SettleFrames(16);
            int c = DocPatch?.PatchCount ?? 0;
            if (c < minSeen) minSeen = c;
            if (c == 0) neverZero = false;
            if (DocPatch != null && MathF.Abs(DocPatch.Scale - 2f) < 1e-3f && DocPatch.HasPatch(0)) break;
        }
        check("150%→200%：过渡期旧补丁一直在（PatchCount 没归零）",
              ok150 && before >= 1 && neverZero && minSeen >= 1,
              $"150% 补丁={before}，过渡最少 {minSeen} 块");
        check("150%→200%：结束时补丁已换成新倍率",
              DocPatch != null && MathF.Abs(DocPatch.Scale - 2f) < 1e-3f && DocPatch.HasPatch(0),
              $"倍率={DocPatch?.Scale ?? 0:F2}，补丁={DocPatch?.PatchCount ?? 0}");
    }

    /// <summary>
    /// **渲到一半关文档**（评审 P0-1）：请求刚排出去（worker 正拿着 PdfiumDoc 在 PDFium 里渲）
    /// 就关文档，反复 20 次。判据是"进程没死、每次都能干净收场"——
    /// 原生踩到已释放的文档会是访问违例，自检会当场崩掉，不会悄悄通过。
    /// </summary>
    private void ZoomTestPatchCloseDuringRender(Action<string, bool, string> check, string pdf)
    {
        DocPageWorker = true;
        DocView.UseWorker = true;
        const int cycles = 20;
        int done = 0, queued = 0;
        string why = "";

        for (int i = 0; i < cycles; i++)
        {
            var err = OpenDocuments(new[] { pdf });
            if (err != null) { why = $"第 {i + 1} 轮打开失败：{err}"; break; }
            SetDocZoomFromUi(2f);

            // 固定起点：视口贴第 1 页页顶（不依赖上一轮把相机丢在哪）
            float top = DocView.At(0).Rect.MinY;
            ViewOffsetY = _virtualY - top * ViewScale;

            // 先落稳、拿到这一块补丁（之后 _settled 保持 true，下一帧就能立刻排新请求）
            var swP = Stopwatch.StartNew();
            while (swP.ElapsedMilliseconds < 3000 && (DocPatch == null || DocPatch.PatchCount == 0))
                SettleFrames(16);
            if (DocPatch == null) { why = $"第 {i + 1} 轮没有补丁缓存"; break; }

            // 往页里挪 400 画布像素（= 800 设备像素，超出 256 余量）：旧补丁盖不住，
            // 落稳窗一过**必然**排一个新请求，worker 随即拿着 PdfiumDoc 进 PDFium。
            ViewOffsetY -= 400f;
            int req0 = DocPatch.RequestCount;
            var swR = Stopwatch.StartNew();
            while (swR.ElapsedMilliseconds < 1500 && DocPatch.RequestCount == req0)
                SettleFrames(16);
            if (DocPatch.RequestCount > req0) queued++;

            done++;
            CloseDocument();          // 请求刚排出去 / 正在渲：关文档必须不崩、不留尾巴
        }

        check($"渲染在飞时关文档 ×{cycles}：不崩、补丁与页都收干净",
              done == cycles && queued == cycles && DocPatch == null && !DocView.IsOpen,
              why.Length > 0 ? why
                  : $"完成 {done} 轮（{queued} 轮确实排出了新请求），"
                    + $"DocPatch={(DocPatch == null ? "已释放" : "还挂着")}");
    }
}
