// [职责] 文档缩放 E2E（阶段 2：渲染核心 + 引擎缩放 API）。
// [测试] --zoomtest

// 判据原则与 --doctest 一致：**能数出来的东西**说话（相机换算 / 页矩形 / 位图倍率 /
// 像素），不靠观感。所有缩放都走引擎的真实入口 `SetDocZoomFromUi`，
// 自检不另写一套坐标换算——否则"测的是另一条路"。

using System.Diagnostics;
using System.Numerics;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>`--zoomtest` 的判据出口（各分组共用一份计数）。</summary>
    private void ZoomCheck(string name, bool ok, string detail, ref int pass, ref int fail)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"    {name,-34}{(ok ? "PASS" : "FAIL")}  {detail}");
    }

    /// <summary>
    /// `--zoomtest`：文档缩放 50/75/100/125/150/200。
    ///
    /// 验五件事：
    ///   ① **100% 的原行为不变**（页位图 = 档位宽、倍率 1、页矩形不动）；
    ///   ② **放大 200%**：视口中心压着的画布点不动、页矩形不变、页位图按倍率重渲；
    ///   ③ **页和墨一起缩**：页角 → 墨点的屏幕距离 = 画布距离 × ViewScale；
    ///   ④ 六个档位都能来回切、不崩、不越界（含 X/Y 夹取）；
    ///   ⑤ **回到 100%** 一切复位（横向偏移 0、页位图倍率 1）。
    ///
    /// 另外验 worker 的**代次**：倍率变化时 1x 的在途结果必须被丢掉。
    /// </summary>
    private void ZoomTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 文档缩放自检（--zoomtest）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
            => ZoomCheck(name, ok, detail, ref pass, ref fail);

        // 确定性：前面几组用同步生成（关掉后台线程），最后一组专门验 worker。
        DocPageWorker = false;
        DocView.UseWorker = false;
        DocView.Generator = null;

        // 干净起点
        CloseDocument();
        DocView.Close();
        Doc.Clear();
        Doc.ClearHistory();
        if (BoardOn) SetBoardFromUi(false);
        if (PassThrough) SetPassThroughFromUi(false);
        ZoomTestResetCamera();

        // 合成一份三页 PDF（和 --doctest 同一份；不依赖外部素材）
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-zoomtest");
        Directory.CreateDirectory(dir);
        string pdf = System.IO.Path.Combine(dir, "缩放测试.pdf");
        WriteTestPdf(pdf);

        bool pdfOk = Pdfium.EnsureLoaded(out string pdfErr);
        Check("pdfium.dll 能加载", pdfOk, pdfOk ? "ok" : pdfErr);
        if (pdfOk)
        {
            var errOpen = OpenDocuments(new[] { pdf });
            Check("打开合成 PDF：3 页、100%",
                  errOpen == null && DocView.Count == 3
                  && MathF.Abs(DocZoom - 1f) < 1e-4f && MathF.Abs(ViewScale - 1f) < 1e-4f
                  && MathF.Abs(ViewOffsetX) < 1e-4f,
                  errOpen ?? $"页={DocView.Count} DocZoom={DocZoom:F2} ViewScale={ViewScale:F2} offX={ViewOffsetX:F1}");

            ZoomTestScale(Check);           // A–D：倍率 / 页矩形 / 坐标 / 六档 / 复位
            ZoomTestShots(Check);           // E：上屏 + 50/100/200 三张图
            ZoomTestWorker(Check, pdf);     // F：worker 代次
        }

        // 收尾：关文档、回基线
        CloseDocument();
        DocPageWorker = false;
        DocView.UseWorker = false;
        Doc.Clear();
        Doc.ClearHistory();
        ZoomTestResetCamera();
        try { Directory.Delete(dir, true); } catch { /* 有意忽略：清理失败不影响主流程 */ }

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：文档缩放 {pass} 项通过 / {fail} 项失败");
        Console.WriteLine($"合计：通过 {pass} 项，失败 {fail} 项");
        _quit = true;
    }

    /// <summary>把相机 / 缩放 / 各覆盖窗口都拨回 100% 起点。</summary>
    private void ZoomTestResetCamera()
    {
        ViewOffsetY = 0f;
        ViewOffsetX = 0f;
        ViewScale = 1f;
        DocZoom = 1f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; w.ViewScale = 1f; }
    }

    /// <summary>A–D 组：100% 基线 / 200% 的中心与倍率 / 六档 / 回 100%。</summary>
    private void ZoomTestScale(Action<string, bool, string> check)
    {
        // ------------------------------------------------------------------
        Console.WriteLine("  -- A 100% 基线：页位图 = 档位宽、倍率 1、页矩形不动 --");
        // ------------------------------------------------------------------
        var page0 = DocView.At(0);
        var rect100 = page0.Rect;
        int baseW = page0.Spec.RenderW > 0 ? page0.Spec.RenderW : page0.Spec.OutW;
        int baseH = page0.Spec.RenderH > 0 ? page0.Spec.RenderH : page0.Spec.OutH;
        check("100%：页位图宽 = 档位渲染宽",
              page0.Spec.BitmapW == baseW && MathF.Abs(page0.Spec.RenderScale - 1f) < 1e-4f,
              $"BitmapW={page0.Spec.BitmapW}（基准 {baseW}）倍率={page0.Spec.RenderScale:F2}");
        check("100%：页位图宽 = DocMemory.LayoutWidth",
              page0.Spec.BitmapW == DocMemory.LayoutWidth(Native.GetSystemMetrics(0)),
              $"{page0.Spec.BitmapW}（应 {DocMemory.LayoutWidth(Native.GetSystemMetrics(0))}）");

        // 一条**很宽**的墨：让横向夹取有活动余地，否则"中心不动"会被页边夹掉
        // （页比视口窄时"不越过页边"优先，那是夹取的正常行为，见 ClampOffsetX）。
        var ink = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 0f, 1f), Width = 6f,
        };
        ink.AddPoint(-3000f, page0.Rect.MinY + 400f, 1f, 0);
        ink.AddPoint(6000f, page0.Rect.MinY + 400f, 1f, 0);
        Doc.AddStroke(ink);
        Doc.InvalidateAll();

        float centerScreenX = _virtualX + _virtualW * 0.5f;
        float centerScreenY = _virtualY + _virtualH * 0.5f;
        float anchorCx = ScreenToCanvasX(centerScreenX);
        float anchorCy = ScreenToCanvasY(centerScreenY);

        // ------------------------------------------------------------------
        Console.WriteLine("  -- B 200%：中心不动 / 页矩形不变 / 页位图翻倍 / 页+墨一起缩 --");
        // ------------------------------------------------------------------
        SetDocZoomFromUi(2f);
        check("2.0：ViewScale = DocZoom = 2",
              MathF.Abs(ViewScale - 2f) < 1e-4f && MathF.Abs(DocZoom - 2f) < 1e-4f,
              $"ViewScale={ViewScale:F2} DocZoom={DocZoom:F2}");
        check("2.0：界面状态快照带出 DocZoom",
              Host != null && MathF.Abs(Host.State.DocZoom - 2f) < 1e-4f,
              $"UiState.DocZoom={(Host != null ? Host.State.DocZoom : float.NaN):F2}");

        float cxNow = ScreenToCanvasX(centerScreenX);
        float cyNow = ScreenToCanvasY(centerScreenY);
        check("2.0：视口中心压着的画布点没动",
              MathF.Abs(cxNow - anchorCx) < 1.5f && MathF.Abs(cyNow - anchorCy) < 1.5f,
              $"中心画布点 ({anchorCx:F1},{anchorCy:F1}) → ({cxNow:F1},{cyNow:F1})");

        check("2.0：页矩形一个数都没变（不重排版）",
              MathF.Abs(DocView.At(0).Rect.MinX - rect100.MinX) < 1e-3f
              && MathF.Abs(DocView.At(0).Rect.MinY - rect100.MinY) < 1e-3f
              && MathF.Abs(DocView.At(0).Rect.MaxX - rect100.MaxX) < 1e-3f
              && MathF.Abs(DocView.At(0).Rect.MaxY - rect100.MaxY) < 1e-3f,
              $"[{rect100.MinX:F0},{rect100.MinY:F0}..{rect100.MaxX:F0},{rect100.MaxY:F0}]");

        var spec200 = DocView.At(0).Spec;
        float capNow = DocMemory.PageRenderScaleCap;
        long px200 = (long)spec200.BitmapW * spec200.BitmapH;
        check("2.0：页位图倍率 = min(2, 档位上限)",
              MathF.Abs(spec200.RenderScale - MathF.Min(2f, capNow)) < 1e-3f,
              $"倍率={spec200.RenderScale:F2}（上限 {capNow:F2}）");
        check("2.0：BitmapW = round(基准 × 倍率)",
              spec200.BitmapW == (int)MathF.Round(baseW * spec200.RenderScale)
              && spec200.BitmapH == (int)MathF.Round(baseH * spec200.RenderScale),
              $"{spec200.BitmapW}x{spec200.BitmapH}（基准 {baseW}x{baseH}）");
        check("2.0：位图真的变大了（重渲，不是拉伸）",
              spec200.BitmapW > baseW && spec200.BitmapH > baseH,
              $"{baseW}→{spec200.BitmapW}");
        check("2.0：单页像素不超预算",
              px200 <= DocMemory.MaxPagePixels * 1.001 + 10000,
              $"{px200 / 1e6:F1}MP（预算 {DocMemory.MaxPagePixels / 1e6:F0}MP）");
        check("2.0：页位图生成代次 +1（旧图作废）",
              DocView.RenderGeneration > 0 && page0.Image == null,
              $"代次={DocView.RenderGeneration} Image={(page0.Image == null ? "已放" : "还在")}");

        // 页 + 墨一起缩：屏幕上"页角 → 墨点"的距离 = 画布距离 × 倍率。
        float inkX = ink.Points[1].X;
        float pageCornerX = rect100.MinX;
        float wantScreenDx = (inkX - pageCornerX) * ViewScale;
        float gotScreenDx = CanvasToScreenX(inkX) - CanvasToScreenX(pageCornerX);
        check("2.0：页和墨在屏幕上一起放大",
              MathF.Abs(gotScreenDx - wantScreenDx) < 0.5f,
              $"{gotScreenDx:F1}（应 {wantScreenDx:F1}）");

        // 坐标往返：同一个画布点，屏幕↔画布必须可逆（页角 + 墨点各验一次）。
        float cornerScreenX = CanvasToScreenX(pageCornerX);
        float inkScreenY = CanvasToScreenY(ink.Points[1].Y);
        check("2.0：页角 / 墨点屏幕↔画布往返一致",
              MathF.Abs(ScreenToCanvasX(cornerScreenX) - pageCornerX) < 0.01f
              && MathF.Abs(ScreenToCanvasY(inkScreenY) - ink.Points[1].Y) < 0.01f,
              $"页角 {pageCornerX:F1} → {ScreenToCanvasX(cornerScreenX):F1}；"
              + $"墨点 y {ink.Points[1].Y:F1} → {ScreenToCanvasY(inkScreenY):F1}");

        ZoomTestSteps(check, baseW, capNow);
    }

    /// <summary>C–D 组：六个档位来回切 + 回到 100% 的复位。</summary>
    private void ZoomTestSteps(Action<string, bool, string> check, int baseW, float capNow)
    {
        // ------------------------------------------------------------------
        Console.WriteLine("  -- C 六档来回切：不崩、不越界、倍率跟着走 --");
        // ------------------------------------------------------------------
        float[] steps = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };
        bool stepsOk = true;
        var stepDetail = new System.Text.StringBuilder();
        foreach (var z in steps)
        {
            SetDocZoomFromUi(z);
            bool ok = MathF.Abs(ViewScale - z) < 1e-4f && MathF.Abs(DocZoom - z) < 1e-4f;
            ok &= DocZoom >= 0.5f - 1e-4f && DocZoom <= 2f + 1e-4f;
            ok &= ViewportCanvas.MaxY > ViewportCanvas.MinY;   // 没被夹成空视口
            float want = z <= 1.0001f ? 1f : MathF.Min(z, capNow);
            ok &= MathF.Abs(DocView.PageRenderScale - want) < 1e-3f;
            ok &= DocView.At(0).Spec.BitmapW == (int)MathF.Round(baseW * DocView.At(0).Spec.RenderScale);
            stepsOk &= ok;
            stepDetail.Append($"{z * 100:F0}%:{(ok ? "ok" : "BAD")} ");
        }
        check("六档全部通过（50/75/100/125/150/200）", stepsOk, stepDetail.ToString().Trim());

        SetDocZoomFromUi(0.5f);
        check("50%：页位图倍率回 1（只缩不合，不重渲更大的图）",
              MathF.Abs(DocView.At(0).Spec.RenderScale - 1f) < 1e-4f,
              $"倍率={DocView.At(0).Spec.RenderScale:F2}");

        // 越界：传 5 和 0.1 都要被夹进 [0.5, 2]。
        SetDocZoomFromUi(5f);
        check("传 5.0：夹到 200%", MathF.Abs(DocZoom - 2f) < 1e-4f, $"{DocZoom:F2}");
        SetDocZoomFromUi(0.1f);
        check("传 0.1：夹到 50%", MathF.Abs(DocZoom - 0.5f) < 1e-4f, $"{DocZoom:F2}");
        SetDocZoomFromUi(1f);

        // 倍率不变 → 不重做（代次不涨）：这是"不每帧重建缓存"的同一套幂等要求。
        int genIdle = DocView.RenderGeneration;
        SetDocZoomFromUi(1f);
        check("重复设同一个倍率：不重做（代次不涨）",
              DocView.RenderGeneration == genIdle, $"{genIdle} → {DocView.RenderGeneration}");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- D 回到 100%：相机与页位图全部复位 --");
        // ------------------------------------------------------------------
        check("100%：ViewScale = DocZoom = 1、横向偏移 = 0",
              MathF.Abs(ViewScale - 1f) < 1e-4f && MathF.Abs(DocZoom - 1f) < 1e-4f
              && MathF.Abs(ViewOffsetX) < 0.5f,
              $"ViewScale={ViewScale:F2} offX={ViewOffsetX:F2}");
        check("100%：页位图倍率 = 1、宽回基准",
              MathF.Abs(DocView.At(0).Spec.RenderScale - 1f) < 1e-4f
              && DocView.At(0).Spec.BitmapW == baseW,
              $"倍率={DocView.At(0).Spec.RenderScale:F2} 宽={DocView.At(0).Spec.BitmapW}");
        check("100%：界面状态快照 DocZoom 回 1",
              Host != null && MathF.Abs(Host.State.DocZoom - 1f) < 1e-4f,
              $"UiState.DocZoom={(Host != null ? Host.State.DocZoom : float.NaN):F2}");
    }

    /// <summary>E 组：真正上屏 + 50/100/200 三张出图（判据仍然是"数像素"）。</summary>
    private void ZoomTestShots(Action<string, bool, string> check)
    {
        Console.WriteLine("  -- E 上屏 + 出图：50 / 100 / 200 三张 --");
        int baseW = DocView.At(0).Spec.RenderW > 0 ? DocView.At(0).Spec.RenderW
                                                   : DocView.At(0).Spec.OutW;
        string shotDir = System.IO.Path.GetFullPath("reports");
        try { Directory.CreateDirectory(shotDir); } catch { /* 有意忽略：出图失败不算自检失败 */ }

        void Shot(float zoom, string name)
        {
            SetDocZoomFromUi(zoom);
            SettleFrames(500);
            string p = System.IO.Path.Combine(shotDir, name + ".bmp");
            bool ok = ScreenProbe.SaveBmp(p, _virtualX, _virtualY, _virtualW, _virtualH);
            Console.WriteLine($"    {name,-24} {(ok ? "已出图 " + p : "出图失败（不影响判据）")}");
        }

        SetDocZoomFromUi(1f);
        SettleFrames(500);
        check("100%：页位图已生成并驻留", DocView.ResidentPages >= 1,
              $"驻留={DocView.ResidentPages} 生成={DocView.GenerateCount}");

        Shot(1f, "zoom-100");
        Shot(2f, "zoom-200");

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

    /// <summary>F 组：worker 的代次——倍率换了，1x 在途结果必须丢掉。</summary>
    private void ZoomTestWorker(Action<string, bool, string> check, string pdf)
    {
        Console.WriteLine("  -- F worker 代次：倍率换了，1x 在途结果必须丢掉 --");
        CloseDocument();
        Doc.Clear();
        Doc.ClearHistory();
        ZoomTestResetCamera();

        DocPageWorker = true;
        DocView.UseWorker = true;
        var errW = OpenDocuments(new[] { pdf });
        if (errW != null)
        {
            check("worker：重开 PDF", false, errW);
            return;
        }

        int baseW = DocView.At(0).Spec.RenderW > 0 ? DocView.At(0).Spec.RenderW
                                                   : DocView.At(0).Spec.OutW;
        // 先按 1x 排一页（此时后台刚开始渲），立刻放大到 200%：
        // 在途的 1x 结果代次对不上，必须被丢掉，最终采纳的是 200% 那张。
        DocView.SyncWindow(ViewportCanvas.MinY, ViewportCanvas.MaxY, 1, 0f);
        SetDocZoomFromUi(2f);

        var swW = Stopwatch.StartNew();
        while (swW.ElapsedMilliseconds < 4000 && DocView.ResidentPages < 1)
            SettleFrames(120);

        var pgW = DocView.At(0);
        check("worker：换倍率后在途 1x 结果被丢掉",
              pgW.Image != null && pgW.Image.Width == pgW.Spec.BitmapW
              && pgW.Image.Height == pgW.Spec.BitmapH,
              $"Image={pgW.Image?.Width}x{pgW.Image?.Height} Spec={pgW.Spec.BitmapW}x{pgW.Spec.BitmapH}"
              + $"（等了 {swW.ElapsedMilliseconds:F0}ms）");
        check("worker：采纳的确实是放大后的位图", pgW.Image != null && pgW.Spec.BitmapW > baseW,
              $"{baseW} → {pgW.Spec.BitmapW}");
    }
}
