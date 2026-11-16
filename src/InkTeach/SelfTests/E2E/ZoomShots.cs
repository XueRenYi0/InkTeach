// [职责] 文档缩放 E2E：出图（50/100/200 + 黑墨清晰度）+ 分块倍率 / 防抖 / 预取环字节上限。
// [测试] --zoomtest

// 判据和 Zoom.cs 一致：能数出来的东西说话（像素、块字节、应用次数）。
// 这里只放"真正上屏 / 出图 / 分块纹理"这一组，避免 E2E 入口文件超过 800 行。

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
        Save("zoom-ink-200");
        int inkDark200 = ScreenProbe.CountDark(_virtualX, (int)(_virtualY + _virtualH * 0.20f),
                                               (int)_virtualW, (int)(_virtualH * 0.22f));
        check("200%：墨迹真的画在页上（分块变换 = (画布−原点)×倍率）", inkDark200 > 2000,
              $"{inkDark200} 个暗像素");
        SetDocZoomFromUi(1f);
        SettleFrames(520);
        Save("zoom-ink-100");           // 同一条墨在 100% 下的样子（对照）
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
}
