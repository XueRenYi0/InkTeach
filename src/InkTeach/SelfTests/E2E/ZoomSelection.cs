// [职责] 文档缩放 E2E（选中家具：手柄 / 操作条 / 面板 / 旋转柄在缩放下的屏幕恒定性）。
// [测试] --zoomtest

// 从 Zoom.cs 拆出来（2026-11-16）：文档缩放那一组本来就长，选中家具这一组
// 又是"100% 与 200% 各量一遍"的对照实验，混在一起会过 800 行。
// 判据原则同上：**能量出来的数说话**，全部走真实入口
// （`FurnitureDpi` / `SelectionHandles` / `SelectionBarCanvasRect`），不另写换算。

using System.Numerics;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 选中家具的一次"屏幕尺寸快照"：同一份选区在当前缩放下的家具尺度、手柄位置、
    /// 条 / 面板 / 最小操作框的矩形。比较 100% 与 200% 两份快照就是这一组的全部判据。
    /// </summary>
    private readonly record struct SelFurnitureProbe(
        float Unit, float Handle, Vector2 Corner, Vector2 RotateGrip, Vector2 TopEdge,
        RectF Bar, RectF Panel, RectF MinBox, float ViewScale);

    /// <summary>当前缩放下的家具快照（`tiny` 是一个用来量最小操作框的小矩形）。</summary>
    private SelFurnitureProbe ProbeSelectionFurniture(in RectF tiny)
    {
        var f = SelectionHandles.FrameOf(Doc.Selected);
        float u = FurnitureDpi;
        return new SelFurnitureProbe(
            Unit: u,
            Handle: SelectionHandles.VisualHandleSize(f.CanvasAabb, u),
            Corner: SelectionHandles.CanvasPosition(SelHandle.BottomRight, f, u),
            RotateGrip: SelectionHandles.CanvasPosition(SelHandle.Rotate, f, u),
            TopEdge: SelectionHandles.CanvasPosition(SelHandle.Top, f, u),
            Bar: SelectionBarCanvasRect,
            Panel: SelectionHandles.LayerPanelRect(f.CanvasAabb, u, FurnitureViewport),
            MinBox: SelectionHandles.UiBox(tiny, u),
            ViewScale: ViewScale);
    }

    /// <summary>
    /// 右手柄外侧 "屏幕 <paramref name="screenDx"/> 像素" 处点一下，返回命中的手柄。
    /// 屏幕偏移 ÷ 相机缩放 = 画布偏移——这是"点在屏幕上看到的地方"那句口径的算式。
    /// </summary>
    private SelHandle SelectionHandleHitAt(float screenDx)
    {
        var f = SelectionHandles.FrameOf(Doc.Selected);
        float u = FurnitureDpi;
        var p = SelectionHandles.CanvasPosition(SelHandle.BottomRight, f, u);
        return SelectionHandles.HitTest(p.X + screenDx / ViewScaleSafe, p.Y, Doc.Selected, f, u);
    }

    /// <summary>
    /// H 组（2026-11-16）：**选中家具在文档缩放下屏幕恒定**。
    ///
    /// 用户口径：放大到 200% 时，手柄 / 操作条 / 面板 / 旋转柄 / 读数标签
    /// **不许跟着变两倍大**——内容放大，家具还是屏幕上那点大小。
    ///
    /// 判据（同一份选区，100% 与 200% 各量一遍）：
    ///   ① 手柄视觉尺寸 = 10 逻辑像素；命中半径 = 14 逻辑像素；
    ///   ② 角手柄的**画布坐标一个数都不变**（位置跟着内容走，只是大小不跟）；
    ///   ③ 旋转柄到上边的屏幕距离 = 30 逻辑像素；
    ///   ④ 操作条 / 层级面板 / 最小操作框的屏幕尺寸不变。
    ///
    /// 命中用"半径内 0.75 处必须中 / 1.6 倍半径处必须不中"两针：后者专门盯旧行为
    /// （200% 时命中区被撑成两倍，1.6 倍半径那个点当时也会"中"）。
    /// </summary>
    private void ZoomTestSelectionFurniture(Action<string, bool, string> check, string pdf)
    {
        Console.WriteLine("  -- H 选中家具：200% 下仍是屏幕尺寸（手柄 / 条 / 面板 / 旋转柄） --");

        // **必须有文档**：桌面上 ViewScale 恒为 1，"相机缩放"这件事只存在于文档态
        // （前面几组可能已经关掉文档，这里重新开一份合成 PDF）。
        CloseDocument();
        Doc.Clear();
        Doc.ClearHistory();
        ZoomTestResetCamera();
        var errOpen = OpenDocuments(new[] { pdf });
        check("（准备）打开合成 PDF 以便放大到 200%",
              errOpen == null && DocView.Count > 0 && MathF.Abs(ViewScale - 1f) < 1e-4f,
              errOpen ?? $"页={DocView.Count} ViewScale={ViewScale:F2}");
        SetDocZoomFromUi(1f);

        // 大对象（短边 400 画布像素）：视觉柄拿满 10 逻辑像素、最小操作框不介入。
        // 放在**可见画布中心**：放大到 200% 时相机绕视口中心缩放，它仍在屏幕里。
        var vis = ViewportCanvas;
        float cx = (vis.MinX + vis.MaxX) * 0.5f, cy = (vis.MinY + vis.MaxY) * 0.5f;
        var big = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0f, 1f, 1f), Width = 6f,
        };
        big.AddPoint(cx - 200f, cy - 200f, 1f, 0);
        big.AddPoint(cx + 200f, cy - 200f, 1f, 0);
        big.AddPoint(cx + 200f, cy + 200f, 1f, 0);
        big.AddPoint(cx - 200f, cy + 200f, 1f, 0);
        Doc.AddStroke(big);
        Doc.SelectOnly(new[] { big });
        Doc.InvalidateAll();
        SettleFrames(120);

        float ui = UiUnit;                                             // 一个逻辑像素 = 多少屏幕像素
        float wantHandle = SelectionHandles.VisualSizeLogical * ui;    // 10 逻辑像素
        float wantRadius = SelectionHandles.HitRadiusLogical * ui;     // 14 逻辑像素
        var tiny = new RectF { MinX = cx, MinY = cy, MaxX = cx + 10f, MaxY = cy + 10f };

        // ---- 100% 基线（= 改动前的原行为，必须一个数都不变）----
        var p100 = ProbeSelectionFurniture(tiny);
        check("100%：家具单位 = UiUnit（和改动前同一个数）",
              MathF.Abs(p100.Unit - ui) < 1e-5f,
              $"家具单位={p100.Unit:F4} UiUnit={ui:F4}");
        check("100%：手柄屏幕尺寸 = 10 逻辑像素",
              MathF.Abs(p100.Handle - wantHandle) < 0.01f,
              $"{p100.Handle:F2}px（应 {wantHandle:F2}px）");
        check("100%：半径内 0.75 处命中右下手柄；1.6 倍半径处不命中",
              SelectionHandleHitAt(wantRadius * 0.75f) == SelHandle.BottomRight
              && SelectionHandleHitAt(wantRadius * 1.6f) == SelHandle.None,
              $"0.75r={SelectionHandleHitAt(wantRadius * 0.75f)} "
              + $"1.6r={SelectionHandleHitAt(wantRadius * 1.6f)}");

        // ---- 200%：同一份选区再量一遍 ----
        SetDocZoomFromUi(2f);
        SettleFrames(120);
        var p200 = ProbeSelectionFurniture(tiny);

        check("200%：家具单位 = UiUnit ÷ 2（相机那一层被抵消）",
              MathF.Abs(p200.Unit - ui / 2f) < 1e-5f,
              $"家具单位={p200.Unit:F4}（UiUnit÷2={ui / 2f:F4}）");
        check("200%：手柄屏幕尺寸不变（仍是 10 逻辑像素）",
              MathF.Abs(p200.Handle * ViewScale - wantHandle) < 0.01f,
              $"{p200.Handle * ViewScale:F2}px（100% 时 {p100.Handle:F2}px）");
        // 角手柄的画布坐标必须一个数都不变（位置跟着内容走）；
        // 旋转柄是例外：它离上边 30 逻辑像素的**屏幕**距离不变 → 画布坐标随缩放成反比，
        // 那一条由下面"旋转柄到上边的屏幕距离"单独验。
        check("200%：角手柄画布坐标不变（位置跟着内容走）",
              Vector2.Distance(p200.Corner, p100.Corner) < 1e-3f,
              $"右下 ({p100.Corner.X:F2},{p100.Corner.Y:F2}) → "
              + $"({p200.Corner.X:F2},{p200.Corner.Y:F2})");
        check("200%：命中半径在屏幕上不变（0.75r 中、1.6r 不中）",
              SelectionHandleHitAt(wantRadius * 0.75f) == SelHandle.BottomRight
              && SelectionHandleHitAt(wantRadius * 1.6f) == SelHandle.None,
              $"0.75r={SelectionHandleHitAt(wantRadius * 0.75f)} "
              + $"1.6r={SelectionHandleHitAt(wantRadius * 1.6f)}");
        check("200%：旋转柄到上边的屏幕距离不变（30 逻辑像素）",
              MathF.Abs((p100.TopEdge.Y - p100.RotateGrip.Y)
                        - SelectionHandles.RotateOffsetLogical * ui) < 0.01f
              && MathF.Abs((p200.TopEdge.Y - p200.RotateGrip.Y) * ViewScale
                           - (p100.TopEdge.Y - p100.RotateGrip.Y)) < 0.01f,
              $"{(p200.TopEdge.Y - p200.RotateGrip.Y) * ViewScale:F2}px"
              + $"（应 {SelectionHandles.RotateOffsetLogical * ui:F2}px）");
        check("200%：操作条屏幕高度不变",
              MathF.Abs((p200.Bar.MaxY - p200.Bar.MinY) * ViewScale
                        - (p100.Bar.MaxY - p100.Bar.MinY)) < 0.01f,
              $"{(p200.Bar.MaxY - p200.Bar.MinY) * ViewScale:F2}px"
              + $"（100% 时 {p100.Bar.MaxY - p100.Bar.MinY:F2}px）");
        check("200%：层级面板屏幕尺寸不变",
              MathF.Abs((p200.Panel.MaxX - p200.Panel.MinX) * ViewScale
                        - (p100.Panel.MaxX - p100.Panel.MinX)) < 0.01f
              && MathF.Abs((p200.Panel.MaxY - p200.Panel.MinY) * ViewScale
                           - (p100.Panel.MaxY - p100.Panel.MinY)) < 0.01f,
              $"{(p200.Panel.MaxX - p200.Panel.MinX) * ViewScale:F1}"
              + $"×{(p200.Panel.MaxY - p200.Panel.MinY) * ViewScale:F1}px");
        check("200%：小对象的最小操作框屏幕尺寸不变（28 逻辑像素）",
              MathF.Abs((p200.MinBox.MaxX - p200.MinBox.MinX) * ViewScale
                        - SelectionHandles.MinUiFrameLogical * ui) < 0.01f
              && MathF.Abs((p100.MinBox.MaxX - p100.MinBox.MinX)
                           - (p200.MinBox.MaxX - p200.MinBox.MinX) * ViewScale) < 0.01f,
              $"{(p200.MinBox.MaxX - p200.MinBox.MinX) * ViewScale:F2}px"
              + $"（应 {SelectionHandles.MinUiFrameLogical * ui:F2}px）");

        ZoomTestSelectionShot(ui);

        // 收尾：回 100%、关文档（合成 PDF），别影响后面的 worker 组
        SetDocZoomFromUi(1f);
        Doc.Selected.Clear();
        CloseDocument();
        Doc.Clear();
        Doc.ClearHistory();
        ZoomTestResetCamera();
        SettleFrames(80);
    }

    /// <summary>
    /// H 组出图：200% + 选中框 + 手柄 + 操作条（`reports/zoom-sel-200.bmp`）。
    /// 切到框选工具只是因为"操作条只在框选 / 图形工具下出现"，判据不依赖工具。
    /// </summary>
    private void ZoomTestSelectionShot(float ui)
    {
        SelBarCollapsed = false;
        SetToolFromUi(Tool.Marquee);
        SettleFrames(320);
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var bar = SelectionBarCanvasRect;
        try
        {
            string shotDir = System.IO.Path.GetFullPath("reports");
            Directory.CreateDirectory(shotDir);
            float pad = 40f * ui;
            float x0 = MathF.Max(_virtualX, CanvasToScreenX(MathF.Min(frame.CanvasAabb.MinX, bar.MinX)) - pad);
            float y0 = MathF.Max(_virtualY, CanvasToScreenY(frame.CanvasAabb.MinY) - pad);
            float x1 = MathF.Min(_virtualX + _virtualW,
                                 CanvasToScreenX(MathF.Max(frame.CanvasAabb.MaxX, bar.MaxX)) + pad);
            float y1 = MathF.Min(_virtualY + _virtualH, CanvasToScreenY(bar.MaxY) + pad);
            string p = System.IO.Path.Combine(shotDir, "zoom-sel-200.bmp");
            bool ok = ScreenProbe.SaveBmp(p, (int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0));
            Console.WriteLine($"    zoom-sel-200            {(ok ? "已出图 " + p : "出图失败（不影响判据）")}");
        }
        catch { /* 有意忽略：出图失败不算自检失败 */ }
    }
}
