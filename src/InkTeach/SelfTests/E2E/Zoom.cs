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
    ///   ② **放大 200%**：视口中心压着的画布点不动、页矩形不变、页位图仍 1×（高清靠补丁）；
    ///   ③ **页和墨一起缩**：页角 → 墨点的屏幕距离 = 画布距离 × ViewScale；
    ///   ④ 六个档位都能来回切、不崩、不越界（含 X/Y 夹取）；
    ///   ⑤ **回到 100%** 一切复位（横向偏移 0、页位图倍率 1）。
    ///
    /// 另外验两件事：**高清补丁**（区域渲染逐像素一致 / 一块屏 / 平移不灰闪 / 翻页回收），
    /// 以及 worker 在 200% 下**不再重渲整页**（底图恒 1×、补丁由后台线程渲）。
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
            ZoomTestTiles(Check);           // I：分块倍率 / 防抖 / 预取环字节上限
            ZoomTestPatches(Check);         // J：高清补丁（区域渲染一致 / 一块屏 / 平移不灰闪 / 翻页回收）
            ZoomTestBarAndPan(Check);       // G：文档条缩放组 / 档位浮层 / 横向拖页 / Ctrl+滚轮
            ZoomTestSelectionFurniture(Check, pdf);   // H：选中家具在 200% 下保持屏幕尺寸
            ZoomTestWorker(Check, pdf);     // F：worker 代次

            CloseDocument();
            Check("关文档：高清补丁全部释放（DocPatch 置空）", DocPatch == null,
                  DocPatch == null ? "已释放" : $"还挂着 {DocPatch.PatchCount} 块");
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

        // 100% 底图先落地：后面拿它的字节数证明"放大后底图没重渲"（2026-11-16 补丁方案）。
        SettleFrames(400);
        check("100%：底图已生成并驻留", DocView.ResidentPages >= 1,
              $"驻留={DocView.ResidentPages} 生成={DocView.GenerateCount}");
        long bytes100 = DocView.ResidentBytes;
        int imgW100 = DocView.At(0).Image != null ? DocView.At(0).Image.Width : 0;

        float centerScreenX = _virtualX + _virtualW * 0.5f;
        float centerScreenY = _virtualY + _virtualH * 0.5f;
        float anchorCx = ScreenToCanvasX(centerScreenX);
        float anchorCy = ScreenToCanvasY(centerScreenY);

        // ------------------------------------------------------------------
        Console.WriteLine("  -- B 200%：中心不动 / 页矩形不变 / 底图仍 1× / 补丁盖可见区 / 页+墨一起缩 --");
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

        ZoomTestBaseAndPatch(check, page0, baseW, baseH, bytes100, imgW100);

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

        ZoomTestSteps(check, baseW, baseH);
    }

    /// <summary>
    /// 200% 的两条硬判据：**底图仍是 1× 整页**（字节数不变、不重渲）+ **高清补丁就绪**
    /// （盖住「可见区 ∩ 页」、面积只是一块屏）。拆出来只为让 <see cref="ZoomTestScale"/>
    /// 不超 120 行（结构基线要求方法 ≤120 行）。
    /// </summary>
    private void ZoomTestBaseAndPatch(Action<string, bool, string> check, DocPages.Page page0,
                                      int baseW, int baseH, long bytes100, int imgW100)
    {
        var spec200 = DocView.At(0).Spec;
        long px200 = (long)spec200.BitmapW * spec200.BitmapH;
        check("2.0：页位图仍是 1× 基准（不再整页重渲）",
              MathF.Abs(spec200.RenderScale - 1f) < 1e-4f && spec200.BitmapW == baseW
              && spec200.BitmapH == baseH,
              $"倍率={spec200.RenderScale:F2} {spec200.BitmapW}x{spec200.BitmapH}（基准 {baseW}x{baseH}）");
        check("2.0：位图没有变大（原来的 143MB 整页重渲已去掉）",
              spec200.BitmapW == baseW && px200 == (long)baseW * baseH,
              $"{baseW}×{baseH} = {px200 / 1e6:F1}MP");
        check("2.0：DocView 常驻字节和 100% 完全一样",
              DocView.ResidentBytes == bytes100
              && DocView.At(0).Image != null && DocView.At(0).Image.Width == imgW100,
              $"{bytes100 / 1048576.0:F1}MB → {DocView.ResidentBytes / 1048576.0:F1}MB"
              + $"（图宽 {(DocView.At(0).Image != null ? DocView.At(0).Image.Width : 0)}）");

        ZoomTestWaitPatch(0, 4000);
        bool hasPatch = false;
        ImageData patchImg = null;
        RectF patchRect = default;
        if (DocPatch != null) hasPatch = DocPatch.TryGetPatch(0, out patchImg, out patchRect);
        var visPage = DocView.At(0).Rect;
        check("2.0：高清补丁已就绪（DocPatch.PatchCount ≥ 1）",
              DocPatch != null && DocPatch.PatchCount >= 1 && hasPatch,
              $"补丁={DocPatch?.PatchCount ?? 0} / 字节 {DocPatch?.PatchBytes / 1048576.0 ?? 0:F1}MB");
        check("2.0：补丁倍率 = 相机倍率",
              DocPatch != null && MathF.Abs(DocPatch.Scale - 2f) < 1e-3f,
              $"scale={DocPatch?.Scale ?? 0:F2}");
        check("2.0：补丁矩形盖住「可见区 ∩ 页」",
              hasPatch && CoversRect(patchRect, Intersect(visPage, Expand(ViewportCanvas, 2f))),
              hasPatch
                  ? $"[{patchRect.MinX:F0},{patchRect.MinY:F0}..{patchRect.MaxX:F0},{patchRect.MaxY:F0}]"
                  : "没有补丁");
        float pagePx2 = (visPage.MaxX - visPage.MinX) * (visPage.MaxY - visPage.MinY) * 4f;
        check("2.0：补丁只是「一块屏」（远小于整页 2× 的面积）",
              hasPatch && patchImg.Width * (long)patchImg.Height < pagePx2 * 0.5,
              hasPatch
                  ? $"{patchImg.Width}×{patchImg.Height} = {patchImg.Width * (long)patchImg.Height / 1e6:F1}MP"
                    + $" / 整页 2× {pagePx2 / 1e6:F1}MP"
                  : "n/a");
        check("2.0：底图没因为缩放被放掉（旧逻辑会整页作废）",
              page0.Image != null, page0.Image == null ? "旧图被放了" : "原图还在");
    }

    /// <summary>C–D 组：六个档位来回切 + 回到 100% 的复位。</summary>
    private void ZoomTestSteps(Action<string, bool, string> check, int baseW, int baseH)
    {
        // ------------------------------------------------------------------
        Console.WriteLine("  -- C 六档来回切：不崩、不越界、底图恒 1×（缩放只换相机）--");
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
            ok &= MathF.Abs(DocView.PageRenderScale - 1f) < 1e-3f;   // 页位图恒 1×（补丁负责高清）
            ok &= DocView.At(0).Spec.BitmapW == baseW && DocView.At(0).Spec.BitmapH == baseH;
            ok &= MathF.Abs(DocView.At(0).Spec.RenderScale - 1f) < 1e-4f;
            stepsOk &= ok;
            stepDetail.Append($"{z * 100:F0}%:{(ok ? "ok" : "BAD")} ");
        }
        check("六档全部通过（50/75/100/125/150/200）", stepsOk, stepDetail.ToString().Trim());

        SetDocZoomFromUi(0.5f);
        check("50%：页位图仍是 1× 基准（只缩不合，不重渲）",
              MathF.Abs(DocView.At(0).Spec.RenderScale - 1f) < 1e-4f
              && DocView.At(0).Spec.BitmapW == baseW,
              $"倍率={DocView.At(0).Spec.RenderScale:F2} 宽={DocView.At(0).Spec.BitmapW}");

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
        check("100%：页位图恒 1×、宽 = 基准（可见路径不变）",
              MathF.Abs(DocView.At(0).Spec.RenderScale - 1f) < 1e-4f
              && DocView.At(0).Spec.BitmapW == baseW,
              $"倍率={DocView.At(0).Spec.RenderScale:F2} 宽={DocView.At(0).Spec.BitmapW}");
        check("100%：界面状态快照 DocZoom 回 1",
              Host != null && MathF.Abs(Host.State.DocZoom - 1f) < 1e-4f,
              $"UiState.DocZoom={(Host != null ? Host.State.DocZoom : float.NaN):F2}");
    }


    /// <summary>
    /// G 组：**文档两条（翻页条 + 缩放条）＋ 档位浮层 ＋ 横向拖页 ＋ Ctrl+滚轮**。
    ///
    /// 判据全部走真实入口：条用 `PptBarPointerDown/Move/Up`，缩放用 `SetDocZoomFromUi` /
    /// `RunDocZoomPreset`，拖页用 `TryBeginDocPanPress` / `DocPanDragForTest`。
    /// 几何用 `PptBar` 自己算（和绘制 / 命中同一份），不另写一套坐标。
    /// </summary>
    private void ZoomTestBarAndPan(Action<string, bool, string> check)
    {
        ZoomTestBarZones(check);
        ZoomTestZoomMenu(check);
        ZoomTestPresets(check);
        ZoomTestPan(check);
        ZoomTestWheel(check);
        ZoomTestBarShots(check);
    }

    /// <summary>点一下缩放条的百分比格（按下 + 抬起 = 开 / 收档位浮层）。</summary>
    private void ClickZoomPct()
    {
        var c = PptBar.ZoomPctCell(DocZoomBarRect(), DpiScale);
        float x = (c.MinX + c.MaxX) * 0.5f, y = (c.MinY + c.MaxY) * 0.5f;
        PptBarPointerDown(x, y);
        PptBarPointerUp(x, y);
    }

    /// <summary>G1：两条的尺寸 / 默认位置 / 热区 / 互不重叠 / 拖动落盘。</summary>
    private void ZoomTestBarZones(Action<string, bool, string> check)
    {
        Console.WriteLine("  -- G 文档两条：翻页条 206 / 缩放条 ≈150 / 浮层 / 横向拖页 / Ctrl+滚轮 --");

        // 干净起点：100%、无菜单、无手掌、无穿透
        SetDocZoomFromUi(1f);
        PptBarPointerDown(_virtualX + 4f, _virtualY + 4f);   // 点别处：收掉任何浮层
        if (DocPanMode) ToggleDocPanFromUi();
        // 两条的位置清回默认（上一次真机跑可能把位置存进偏好；不清理这条会飘）
        SetUiPref("pptBarX", null); SetUiPref("pptBarY", null);
        SetUiPref("docZoomBarX", null); SetUiPref("docZoomBarY", null);
        ResetPptBarPosForTest(); ResetDocZoomBarPosForTest();
        SettleFrames(120);

        // ------------------------------------------------------------------
        Console.WriteLine("  -- G1 两条：翻页条 206 ＋ 右下缩放条 ≈150，各自热区、互不重叠 --");
        // ------------------------------------------------------------------
        var bar = PptBarRect();
        var zb = DocZoomBarRect();
        check("条宽常量：翻页条 = 206（三种模式一致）、缩放条 ≈150",
              MathF.Abs(PptBar.BarW - 206f) < 0.6f
              && MathF.Abs(PptBar.ZoomBarW - 152f) < 0.6f
              && MathF.Abs(PptBar.ZoomBarW - (PptBar.ZoomBtnW * 2f + PptBar.ZoomPctW)) < 0.01f,
              $"BarW={PptBar.BarW:F0}，ZoomBarW={PptBar.ZoomBarW:F0}");
        check("文档模式：DocZoomBar = true，两条都看得见", DocZoomBar && PageBarVisible,
              $"DocZoomBar={DocZoomBar} 可见={PageBarVisible}");
        check("实测宽：翻页条 206、缩放条 152（都按 DPI 缩放）",
              MathF.Abs((bar.MaxX - bar.MinX) - PptBar.BarW * DpiScale) < 1f
              && MathF.Abs((zb.MaxX - zb.MinX) - PptBar.ZoomBarW * DpiScale) < 1f,
              $"{bar.MaxX - bar.MinX:F1}px / {zb.MaxX - zb.MinX:F1}px");
        check("默认位置：左条贴左底、右条贴右底（边距镜像对称、同一条底边）",
              MathF.Abs(bar.MinX - (_virtualX + PptBar.EdgeMargin * DpiScale)) < 1f
              && MathF.Abs(zb.MaxX - (_virtualX + _virtualW - PptBar.EdgeMargin * DpiScale)) < 1f
              && MathF.Abs(bar.MaxY - zb.MaxY) < 1f,
              $"左 x{bar.MinX:F0}，右条 x{zb.MinX:F0}..{zb.MaxX:F0}（屏右 {_virtualX + _virtualW:F0}）");

        float midY = (bar.MinY + bar.MaxY) * 0.5f;
        var zo = PptBar.ZoomOutCell(zb, DpiScale);
        var zp = PptBar.ZoomPctCell(zb, DpiScale);
        var zi = PptBar.ZoomInCell(zb, DpiScale);
        PptBarZone Left(float x) => PptBar.ZoneAt(bar, x, midY, DpiScale);
        PptBarZone Zoom(float x) => PptBar.ZoomBarZoneAt(zb, x, midY, DpiScale);
        PptBarZone ZL(RectF r) => Left((r.MinX + r.MaxX) * 0.5f);
        PptBarZone ZZ(RectF r) => Zoom((r.MinX + r.MaxX) * 0.5f);
        check("热区顺序：翻页条 ◀ 页码 ▶ ☰；缩放条 − 百分比 ＋",
              Left(bar.MinX + 2f) == PptBarZone.LeftArrow
              && ZL(PptBar.MidCell(bar, DpiScale)) == PptBarZone.Page
              && ZL(PptBar.ArrowCell(bar, left: false, DpiScale)) == PptBarZone.RightArrow
              && Left(bar.MaxX - 2f) == PptBarZone.Menu
              && ZZ(zo) == PptBarZone.ZoomOut && ZZ(zp) == PptBarZone.ZoomPct
              && ZZ(zi) == PptBarZone.ZoomIn,
              $"翻页 {Left(bar.MinX + 2f)}/{ZL(PptBar.MidCell(bar, DpiScale))}/"
              + $"{ZL(PptBar.ArrowCell(bar, false, DpiScale))}/{Left(bar.MaxX - 2f)}；"
              + $"缩放 {ZZ(zo)}/{ZZ(zp)}/{ZZ(zi)}");
        check("缩放三格首尾相接、整条 ≈150",
              MathF.Abs(zo.MaxX - zp.MinX) < 0.01f && MathF.Abs(zp.MaxX - zi.MinX) < 0.01f
              && MathF.Abs(zi.MaxX - zo.MinX - PptBar.ZoomBarW * DpiScale) < 0.5f,
              $"− {zo.MinX:F0}..{zi.MaxX:F0}");

        bool apart = !(bar.MinX < zb.MaxX && zb.MinX < bar.MaxX
                       && bar.MinY < zb.MaxY && zb.MinY < bar.MaxY);
        var uiB = CurrentUi.QueryBounds();
        bool uiOk = uiB.IsEmpty
            || (!(bar.MinX < uiB.MaxX * DpiScale && uiB.MinX * DpiScale < bar.MaxX
                  && bar.MinY < uiB.MaxY * DpiScale && uiB.MinY * DpiScale < bar.MaxY)
                && !(zb.MinX < uiB.MaxX * DpiScale && uiB.MinX * DpiScale < zb.MaxX
                  && zb.MinY < uiB.MaxY * DpiScale && uiB.MinY * DpiScale < zb.MaxY));
        float midX = _virtualX + _virtualW * 0.5f;
        bool centerFree = _virtualW < 900f * DpiScale
            || (bar.MaxX < midX - 100f * DpiScale && zb.MinX > midX + 100f * DpiScale);
        check("两条互不重叠、也不压主工具条（中间留空）", apart && uiOk && centerFree,
              $"互不重叠={apart} 工具条={uiOk} 中间留空={centerFree}");

        // 分隔带不是热区、但必须吃掉；拖动不能从分隔带开始
        float dr = PptBar.DividerX(bar, DpiScale);
        check("翻页条分隔带不是热区", Left(dr) == PptBarZone.None, $"{Left(dr)}");
        bool ate = PptBarPointerDown(dr, midY);
        check("点在分隔带上：归条吃掉（不漏成笔画）、也不开始拖条",
              ate && !PptBarDragging && !PptMenuOpen && !PptZoomMenuOpen,
              $"吃掉={ate} 拖动={PptBarDragging}");

        // ☰ 在文档模式下仍然可用
        var mc = PptBar.MenuCell(bar, DpiScale);
        PptBarPointerDown((mc.MinX + mc.MaxX) * 0.5f, midY);
        check("文档模式 ☰ 仍开菜单", PptMenuOpen && !PptZoomMenuOpen,
              $"菜单={PptMenuOpen} 浮层={PptZoomMenuOpen}");
        PptBarPointerDown((mc.MinX + mc.MaxX) * 0.5f, midY);
        check("再点 ☰ 收起", !PptMenuOpen, $"菜单={PptMenuOpen}");

        // 缩放条拖动 + 落盘 + "重启后读回"
        float px = (zp.MinX + zp.MaxX) * 0.5f;
        PptBarPointerDown(px, midY);
        PptBarPointerMove(px - 120f * DpiScale, midY - 80f * DpiScale);
        check("缩放条：按住百分比格拖 = 进入拖动", DocZoomBarDragging, $"拖动={DocZoomBarDragging}");
        PptBarPointerUp(px - 120f * DpiScale, midY - 80f * DpiScale);
        var movedZb = DocZoomBarRect();
        check("缩放条拖动落盘：docZoomBarX/Y 有值、位置真的变了",
              GetUiPref("docZoomBarX") != null && GetUiPref("docZoomBarY") != null
              && movedZb.MinX < zb.MinX - 50f,
              $"x {zb.MinX:F0} → {movedZb.MinX:F0}，docZoomBarX={GetUiPref("docZoomBarX")}");
        float keptX = movedZb.MinX, keptY = movedZb.MinY;
        ResetDocZoomBarPosForTest();
        LoadDocZoomBarPos();
        var back = DocZoomBarRect();
        check("缩放条：重启（清内存 + 读偏好）后位置还在",
              MathF.Abs(back.MinX - keptX) < 2f && MathF.Abs(back.MinY - keptY) < 2f,
              $"记的是 ({keptX:F0},{keptY:F0})，读回来 ({back.MinX:F0},{back.MinY:F0})");

        // 收尾：清偏好、回默认（后面的组和下一次真机跑都不受这次拖动影响）
        SetUiPref("docZoomBarX", null); SetUiPref("docZoomBarY", null);
        ResetDocZoomBarPosForTest();
        DocZoomBarRect();
    }

    /// <summary>G2：百分比格弹出档位浮层（8 项、无 300%、锚在缩放条上）。</summary>
    private void ZoomTestZoomMenu(Action<string, bool, string> check)
    {
        var bar = DocZoomBarRect();
        var zp = PptBar.ZoomPctCell(bar, DpiScale);
        // ------------------------------------------------------------------
        Console.WriteLine("  -- G2 档位浮层：8 项、无 300%、锚在缩放条百分比格上方、点外面关 --");
        // ------------------------------------------------------------------
        check("档位表：8 项、没有 300%",
              DocZoomPresetCount == 8
              && DocZoomPresetText(0) == "适合宽度（100%）"
              && DocZoomPresetText(1) == "适合整页"
              && DocZoomPresetText(7) == "200%",
              $"共 {DocZoomPresetCount} 项；首项「{DocZoomPresetText(0)}」末项「{DocZoomPresetText(7)}」");
        bool has300 = false;
        for (int i = 0; i < DocZoomPresetCount; i++)
            if (DocZoomPresetText(i).Contains("300")) has300 = true;
        check("档位表里没有 300%", !has300, has300 ? "出现 300%" : "50/75/100/125/150/200 + 适合×2");

        ClickZoomPct();
        check("点百分比：浮层打开（菜单 / 面板不开）",
              PptZoomMenuOpen && !PptMenuOpen && !PptPagePanelOpen,
              $"浮层={PptZoomMenuOpen} 菜单={PptMenuOpen} 面板={PptPagePanelOpen}");
        PptZoomMenuRect(out var zm);
        float zmCx = (zm.MinX + zm.MaxX) * 0.5f, zpCx = (zp.MinX + zp.MaxX) * 0.5f;
        check("浮层 ≈140 宽、锚在百分比格正上方、整块在屏幕里",
              MathF.Abs((zm.MaxX - zm.MinX) - PptBar.ZoomMenuW * DpiScale) < 1f
              && MathF.Abs(zmCx - zpCx) < 1f
              && zm.MaxY <= bar.MinY + 0.5f
              && zm.MinX >= _virtualX && zm.MaxX <= _virtualX + _virtualW
              && zm.MinY >= _virtualY,
              $"宽 {(zm.MaxX - zm.MinX):F0}，中心差 {MathF.Abs(zmCx - zpCx):F1}，"
              + $"y {zm.MinY:F0}..{zm.MaxY:F0}，条顶 {bar.MinY:F0}");
        PptZoomMenuItemRectAt(0, out var item0);
        PptZoomMenuItemRectAt(7, out var item7);
        check("浮层 8 行都在浮层矩形里",
              zm.Contains(item0.MinX, item0.MinY) && zm.Contains(item7.MaxX, item7.MaxY),
              $"首行 y {item0.MinY:F0}，末行 y {item7.MaxY:F0}，浮层底 {zm.MaxY:F0}");

        // 悬停：第 3 项（50%）→ hover = 300+2
        PptZoomMenuItemRectAt(2, out var item2);
        PptBarPointerMove((item2.MinX + item2.MaxX) * 0.5f, (item2.MinY + item2.MaxY) * 0.5f);
        check("浮层悬停命中它自己（300+i）", PptBarHover == 300 + 2, $"hover={PptBarHover}");

        // 再点百分比 = 收起（不鬼打墙）
        ClickZoomPct();
        check("再点百分比：浮层收起", !PptZoomMenuOpen, $"浮层={PptZoomMenuOpen}");

        // 点浮层外面：收起
        ClickZoomPct();
        PptBarPointerDown(_virtualX + 4f, _virtualY + 4f);
        check("点浮层外面：收起", !PptZoomMenuOpen, $"浮层={PptZoomMenuOpen}");
    }

    /// <summary>G3：档位动作与对勾（适合宽度 / 适合整页 / 六档）。</summary>
    private void ZoomTestPresets(Action<string, bool, string> check)
    {
        // ------------------------------------------------------------------
        Console.WriteLine("  -- G3 档位动作：适合宽度 / 适合整页（可到 40%）/ 200% + 对勾 --");
        // ------------------------------------------------------------------
        SetDocZoomFromUi(1f);
        check("100%：对勾在「适合宽度」、百分比读数 100%",
              DocZoomPresetChecked(0) && !DocZoomPresetChecked(4) && DocZoomPercentText == "100%",
              $"读数={DocZoomPercentText} 勾0={DocZoomPresetChecked(0)} 勾100%={DocZoomPresetChecked(4)}");

        SetDocZoomFromUi(1.5f);
        check("150%：对勾在 150%、读数 150%",
              DocZoomPresetChecked(6) && !DocZoomPresetChecked(0) && DocZoomPercentText == "150%",
              $"读数={DocZoomPercentText} 勾150%={DocZoomPresetChecked(6)}");

        SetDocZoomFromUi(1f);
        float fit = DocFitPageZoom();
        RunDocZoomPreset(1);                       // 适合整页
        check("适合整页：装得下整页（≤100%）、且确实可能 <50%",
              MathF.Abs(DocZoom - fit) < 0.001f && fit <= 1f + 0.0001f && fit >= DocZoomFitMin - 0.0001f,
              $"适合整页={fit * 100f:F0}%，实际={DocZoom * 100f:F0}%");
        var pg = DocView.At(Math.Clamp(BarPageNow - 1, 0, DocView.Count - 1)).Rect;
        bool flooredFit = MathF.Abs(fit - DocZoomFitMin) < 1e-4f;
        check("适合整页：页在屏幕上完整可见（宽高都装进视口；或落在 40% 硬下限）",
              ((pg.MaxX - pg.MinX) * ViewScale <= _virtualW + 1f
               && (pg.MaxY - pg.MinY) * ViewScale <= _virtualH + 1f)
              || flooredFit,
              $"页 {(pg.MaxX - pg.MinX) * ViewScale:F0}×{(pg.MaxY - pg.MinY) * ViewScale:F0}，"
              + $"视口 {_virtualW:F0}×{_virtualH:F0}" + (flooredFit ? "（已到 40% 下限）" : ""));
        check("适合整页：读数如实显示实际百分比（不当成 50% 静默夹住）",
              DocZoomPercentText == $"{MathF.Round(DocZoom * 100f):F0}%"
              && (DocZoom >= DocZoomMin - 0.0001f || DocZoomPercentText != "50%"),
              $"读数={DocZoomPercentText}（DocZoom={DocZoom:F3}）");
        if (fit < 1f - 0.006f)
        {
            float fitCx = CanvasToScreenX((pg.MinX + pg.MaxX) * 0.5f);
            check("适合整页（<100%）：页水平居中（±1px）",
                  MathF.Abs(fitCx - (_virtualX + _virtualW * 0.5f)) < 1f,
                  $"页中心 x={fitCx:F1}，视口中心 {_virtualX + _virtualW * 0.5f:F1}");
        }
        if (fit < DocZoomMin - 0.0001f)
        {
            float beforeOut = DocZoom;
            DocZoomOut();
            check("适合整页的 40% 档：− 不再把它放大回 50%",
                  MathF.Abs(DocZoom - beforeOut) < 0.0001f,
                  $"{beforeOut:F2} → {DocZoom:F2}");
            check("低于 50% 时：− 置灰、＋ 仍可点",
                  !DocZoomOutEnabled && DocZoomInEnabled,
                  $"−={DocZoomOutEnabled} ＋={DocZoomInEnabled}");
        }
        else
        {
            check("本机适合整页 ≥50%（跳过 40% 档断言）", true, $"fit={fit * 100f:F0}%");
        }
        if (fit < 1f - 0.006f)
            check("适合整页之后：对勾在「适合整页」", DocZoomPresetChecked(1),
                  $"勾={DocZoomPresetChecked(1)}（fit={fit:F3}）");

        RunDocZoomPreset(7);                       // 200%
        check("档位 200%：生效、读数 200%、＋ 置灰",
              MathF.Abs(DocZoom - 2f) < 1e-4f && DocZoomPercentText == "200%" && !DocZoomInEnabled,
              $"DocZoom={DocZoom:F2} 读数={DocZoomPercentText} ＋={DocZoomInEnabled}");
        RunDocZoomPreset(2);                       // 50%
        check("档位 50%：生效、读数 50%、− 置灰",
              MathF.Abs(DocZoom - 0.5f) < 1e-4f && DocZoomPercentText == "50%" && !DocZoomOutEnabled,
              $"DocZoom={DocZoom:F2} 读数={DocZoomPercentText} −={DocZoomOutEnabled}");
        RunDocZoomPreset(0);                       // 适合宽度 = 100%
        check("适合宽度（100%）：回到 100%",
              MathF.Abs(DocZoom - 1f) < 1e-4f && MathF.Abs(ViewOffsetX) < 0.5f,
              $"DocZoom={DocZoom:F2} offX={ViewOffsetX:F2}");

        SetDocZoomFromUi(1f);
        var fit0 = DocFitPageZoom();
        ClickZoomPct();                            // 开浮层
        PptZoomMenuItemRectAt(1, out var fitItem);
        PptBarPointerDown((fitItem.MinX + fitItem.MaxX) * 0.5f, (fitItem.MinY + fitItem.MaxY) * 0.5f);
        check("点浮层「适合整页」：执行 + 浮层收起",
              MathF.Abs(DocZoom - fit0) < 0.001f && !PptZoomMenuOpen,
              $"DocZoom={DocZoom * 100f:F0}%（应 {fit0 * 100f:F0}%），浮层={PptZoomMenuOpen}");
    }

    /// <summary>G4：手掌横向拖页（100% 只纵、200% 两轴）。</summary>
    private void ZoomTestPan(Action<string, bool, string> check)
    {
        // ------------------------------------------------------------------
        Console.WriteLine("  -- G4 手掌横向拖页：100% 只纵、200% 两轴 --");
        // ------------------------------------------------------------------
        if (!DocPanMode) ToggleDocPanFromUi();
        check("（准备）手掌已开", DocPanMode, $"手掌={DocPanMode}");
        int strokesBeforePan = Doc.Strokes.Count;

        SetDocZoomFromUi(1f);
        ZoomTestResetCamera();
        float sx = _virtualX + _virtualW * 0.5f, sy = _virtualY + _virtualH * 0.5f;
        bool grabbed1 = TryBeginDocPanPress(sx, sy);
        DocPanDragForTest(sx + 140f, sy - 130f);
        float offX100 = ViewOffsetX, offY100 = ViewOffsetY;
        DocPanReleaseForTest();
        check("100%：横向拖动不动画布（ViewOffsetX 严格为 0）",
              grabbed1 && MathF.Abs(offX100) < 0.001f,
              $"offX={offX100:F3}");
        check("100%：纵向照旧能拖", MathF.Abs(offY100) > 40f, $"offY={offY100:F1}");

        SetDocZoomFromUi(2f);
        float baseX = ViewOffsetX, baseY = ViewOffsetY;
        bool grabbed2 = TryBeginDocPanPress(sx, sy);
        DocPanDragForTest(sx + 160f, sy - 140f);
        float offX200 = ViewOffsetX, offY200 = ViewOffsetY;
        DocPanReleaseForTest();
        check("200%：横向拖动改 ViewOffsetX（页比视口宽，左右能看全）",
              grabbed2 && MathF.Abs(offX200 - baseX) > 20f,
              $"offX {baseX:F0} → {offX200:F0}");
        check("200%：纵向同时跟着走", MathF.Abs(offY200 - baseY) > 20f,
              $"offY {baseY:F0} → {offY200:F0}");
        check("200%：横向被夹在合法范围里（不拖出页边）",
              offX200 <= 0.5f && offX200 >= -(_virtualW * 3f),
              $"offX={offX200:F0}（视口 {(_virtualX + _virtualW):F0}）");
        check("拖页不落墨（笔画数没变）", Doc.Strokes.Count == strokesBeforePan,
              $"笔画 {strokesBeforePan} → {Doc.Strokes.Count}");

        // 200% 横向拖过之后缩回去：100% 严格 0、50% 自动摆回视口中心（2026-11-16 用户要求）
        var panPage = DocView.At(Math.Clamp(BarPageNow - 1, 0, DocView.Count - 1)).Rect;
        float pageCx = (panPage.MinX + panPage.MaxX) * 0.5f;
        SetDocZoomFromUi(1f);
        float c100 = CanvasToScreenX(pageCx);
        check("200% 拖页后回 100%：横向偏移严格 0（100% 路径不变）",
              MathF.Abs(ViewOffsetX) < 1e-4f, $"offX={ViewOffsetX:F3}");
        check("200% 拖页后回 100%：页中心在视口中心（±1px）",
              MathF.Abs(c100 - sx) < 1f, $"页中心 {c100:F1} / 视口中心 {sx:F1}");
        SetDocZoomFromUi(0.5f);
        float c50 = CanvasToScreenX(pageCx);
        check("50%：页自动水平居中（±1px）",
              MathF.Abs(c50 - sx) < 1f, $"页中心 {c50:F1} / 视口中心 {sx:F1}");

        // 光标仍是手型（横向拖页后不变成别的）
        PointerX = 200f; PointerY = 200f + ViewOffsetY;
        LastPointerType = Native.PT_MOUSE;
        check("横向拖页后光标仍是手型", ComputeCursorKind() == CursorKind.Hand,
              Cursors.Name(ComputeCursorKind()));
        if (DocPanMode) ToggleDocPanFromUi();
    }

    /// <summary>G5：Ctrl+滚轮围绕指针缩放，普通滚轮不缩放。</summary>
    private void ZoomTestWheel(Action<string, bool, string> check)
    {
        // ------------------------------------------------------------------
        Console.WriteLine("  -- G5 Ctrl+滚轮：围绕指针缩放、普通滚轮不动 --");
        // ------------------------------------------------------------------
        SetDocZoomFromUi(1f);
        float anchorScreenX = _virtualX + _virtualW * 0.37f;
        float anchorScreenY = _virtualY + _virtualH * 0.41f;
        float anchorCanvasX = ScreenToCanvasX(anchorScreenX);
        float anchorCanvasY = ScreenToCanvasY(anchorScreenY);
        bool wheelOk = DocZoomWheelFromUi(120, anchorScreenX, anchorScreenY);
        float afterX = ScreenToCanvasX(anchorScreenX);
        float afterY = ScreenToCanvasY(anchorScreenY);
        check("Ctrl+滚轮上：放大一档（100→125%）", wheelOk && MathF.Abs(DocZoom - 1.25f) < 1e-4f,
              $"DocZoom={DocZoom:F2} 吃掉={wheelOk}");
        check("Ctrl+滚轮：指针压着的画布点不动（围绕指针缩放）",
              MathF.Abs(afterX - anchorCanvasX) < 1.5f && MathF.Abs(afterY - anchorCanvasY) < 1.5f,
              $"({anchorCanvasX:F1},{anchorCanvasY:F1}) → ({afterX:F1},{afterY:F1})");
        DocZoomWheelFromUi(-120, anchorScreenX, anchorScreenY);
        check("Ctrl+滚轮下：缩回 100%", MathF.Abs(DocZoom - 1f) < 1e-4f, $"{DocZoom:F2}");

        // 普通滚轮：**不得触发缩放**（Ctrl 分支只看 Ctrl，不能误伤滚画布）。
        ZoomTestResetCamera();
        float zoomBeforeWheel = DocZoom;
        int wheelDown = -120;
        HandleWheel(new IntPtr((long)(ushort)(short)wheelDown << 16));
        check("普通滚轮（无 Ctrl）：不触发缩放（Ctrl 分支没误伤）",
              MathF.Abs(DocZoom - zoomBeforeWheel) < 1e-4f,
              $"DocZoom {zoomBeforeWheel:F2} → {DocZoom:F2}");
        ZoomTestResetCamera();
        SetDocZoomFromUi(1f);
    }

    /// <summary>G6：文档条出图（100% / 200% / 档位浮层）。</summary>
    private void ZoomTestBarShots(Action<string, bool, string> check)
    {
        // ------------------------------------------------------------------
        Console.WriteLine("  -- G6 出图：左翻页条 / 右缩放条（100%·200%）/ 档位浮层 --");
        // ------------------------------------------------------------------
        string shotDir = System.IO.Path.GetFullPath("reports");
        try { Directory.CreateDirectory(shotDir); } catch { /* 有意忽略：出图失败不影响判据 */ }

        void Shot(string name, float zoom, in RectF area)
        {
            SetDocZoomFromUi(zoom);
            SettleFrames(260);
            float pad = 18f * DpiScale;
            int x0 = (int)MathF.Max(_virtualX, area.MinX - pad);
            int y0 = (int)MathF.Max(_virtualY, area.MinY - pad);
            int w = (int)MathF.Min(_virtualX + _virtualW - x0, area.MaxX - area.MinX + pad * 2f);
            int h = (int)MathF.Min(_virtualY + _virtualH - y0, area.MaxY - area.MinY + pad * 2f);
            string path = System.IO.Path.Combine(shotDir, name + ".png");
            bool ok = SaveScreenPng(path, x0, y0, Math.Max(1, w), Math.Max(1, h));
            Console.WriteLine($"    {name,-22} {(ok ? "已出图 " + path : "出图失败（不影响判据）")}");
        }

        Shot("docbar-left", 1f, PptBarRect());
        Shot("docbar-right", 1f, DocZoomBarRect());
        Shot("docbar-right-200", 2f, DocZoomBarRect());

        SetDocZoomFromUi(1f);
        SettleFrames(200);
        ClickZoomPct();                            // 开档位浮层
        SettleFrames(220);
        PptZoomMenuRect(out var zm);
        var withMenu = DocZoomBarRect();
        withMenu.Add(zm.MinX, zm.MinY); withMenu.Add(zm.MaxX, zm.MaxY);
        Shot("docbar-right-menu", 1f, withMenu);
        PptBarPointerDown(_virtualX + 4f, _virtualY + 4f);   // 收起

        SetDocZoomFromUi(1f);
        SettleFrames(120);
        check("出图后回到 100%（不影响后续 worker 组）",
              MathF.Abs(DocZoom - 1f) < 1e-4f && !PptZoomMenuOpen,
              $"DocZoom={DocZoom:F2} 浮层={PptZoomMenuOpen}");
    }

    /// <summary>F 组：worker——底图恒 1×，放大后的高清由**补丁线程**补上。</summary>
    private void ZoomTestWorker(Action<string, bool, string> check, string pdf)
    {
        Console.WriteLine("  -- F worker：底图不因缩放重渲 / 200% 的补丁由后台线程渲好 --");
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
        // 先排一页 1×（后台开始渲），立刻放大到 200%：
        // 新逻辑下底图**不换倍率**（配方没变、代次没变），在途的 1× 结果照样能采纳；
        // 高清由补丁线程另外渲一块。
        DocView.SyncWindow(ViewportCanvas.MinY, ViewportCanvas.MaxY, 1, 0f);
        SetDocZoomFromUi(2f);

        var swW = Stopwatch.StartNew();
        while (swW.ElapsedMilliseconds < 4000
               && (DocView.ResidentPages < 1 || DocPatch == null || DocPatch.PatchCount < 1))
            SettleFrames(120);

        var pgW = DocView.At(0);
        check("worker：底图仍是 1×（缩放不重渲整页）",
              pgW.Image != null && pgW.Image.Width == baseW && pgW.Spec.BitmapW == baseW,
              $"Image={pgW.Image?.Width}x{pgW.Image?.Height} Spec={pgW.Spec.BitmapW}x{pgW.Spec.BitmapH}"
              + $"（等了 {swW.ElapsedMilliseconds:F0}ms）");
        check("worker：200% 的高清补丁已由后台线程渲好",
              DocPatch != null && DocPatch.HasPatch(0),
              $"补丁={DocPatch?.PatchCount ?? 0} / {DocPatch?.PatchBytes / 1048576.0 ?? 0:F1}MB");
    }
}

