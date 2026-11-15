using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// 开发期宿主：持有引擎，外加一整套自动化测试/基准工具。
/// 继承只是为了让这些工具直接读引擎内部状态（产品代码请用组合：new InkEngine()）。
/// </summary>

internal sealed partial class App
{
    private void ShapeToolShowcase(string path, string kind, bool drag, bool drawing,
                                   bool rotate, bool rotated, bool snap,
                                   bool angleRead, bool poseRead, bool autoSel)
    {
        BoardOn = true;                       // 白底：手柄压在桌面上看不清
        SetUiFactory(() => new InkUi.FullUi());   // 浮层配色/投影由界面推上来
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        // ---- `--autosel`：**画完自动选中那一帧**（2026-09-22）----
        //
        // 真机拖一个矩形、松手，拍"刚画完"的样子。要看的是两件事：
        //   ① 框在（框 ＋ 八个手柄 ＋ 旋转柄）；
        //   ② **下面没有操作条**——图形工具下那一条不画（见 Engine.SelectionBarShown），
        //      因为它正好挂在图形正下方，会吃掉"接着画下一个"的那一下。
        // 出图范围因此刻意**往下多留一段**：不往下留的话，"有没有条"这件事看不出来。
        if (autoSel)
        {
            SetToolFromUi(Tool.Rectangle);
            var aFrom = new Vector2(cx - 260f, cy - 170f);
            var aTo = new Vector2(cx + 260f, cy + 170f);
            SendMouse((int)aFrom.X, (int)aFrom.Y, 0);                            SettleFrames(60);
            SendMouse((int)aFrom.X, (int)aFrom.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(aFrom.X + (aTo.X - aFrom.X) * i / 4f),
                          (int)(aFrom.Y + (aTo.Y - aFrom.Y) * i / 4f), 0);
                SettleFrames(40);
            }
            SendMouse((int)aTo.X, (int)aTo.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(400);                       // 松手 → 自动选中，等它画完

            Console.WriteLine($"画完自动选中：对象 {Doc.Strokes.Count} 个，"
                            + $"选中 {Doc.Selected.Count} 个，工具还是 {Host.State.Tool}，"
                            + $"操作条画不画 = {SelectionBarShown}");

            var sel = SelectionHandles.FrameOf(Doc.Selected);
            var shot = sel.CanvasAabb;
            // 旋转柄在框上方外侧，单独并进来（它不在 aabb 里）。
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, sel, DpiScale);
            shot.Add(grip.X - 40f, grip.Y - 40f);
            shot.Add(grip.X + 40f, grip.Y + 40f);
            // **往下多留 120 逻辑像素**：那正是"操作条本该出现的地方"
            //（`SelectionHandles.BarOffsetLogical` 起算），留出来才看得出它不在。
            shot.Add(shot.MinX, shot.MaxY + 120f * DpiScale);
            if (!OffscreenFloatingShot(path, shot.Inflate(30f))) Console.WriteLine("出图失败");
            _quit = true;
            return;
        }

        // ---- 第③轮（读数）那几张 ----
        // `--pose`：拖旋转柄 → 姿态角读数（矩形停在吸住的 **0°**、椭圆停在 **90°**）。
        if (poseRead)
        {
            bool isRect = kind != "ellipse";
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 起手都是"歪的"：矩形 8°（差一点点就正）、椭圆 52°（很明显是歪的）。
            float startDeg = isRect ? 8f : 52f;
            var center = new Vector2(cx, cy);
            if (isRect)
            {
                sh.Tool = Tool.Rectangle; sh.Kind = StrokeKind.Rectangle;
                sh.AddPoint(center.X - 320f, center.Y - 200f, 1f, 0);
                sh.AddPoint(center.X + 320f, center.Y + 200f, 1f, 0);
            }
            else
            {
                sh.Tool = Tool.Ellipse; sh.Kind = StrokeKind.Ellipse;
                sh.AddPoint(center.X, center.Y, 1f, 0);              // 中心
                sh.AddPoint(center.X + 330f, center.Y + 150f, 1f, 0); // 外角点（a=330, b=150）
            }
            sh.Transform = SelectionHandles.RotateMatrix(startDeg, center);
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var f0 = SelectionHandles.FrameOf(Doc.Selected);
            var pv = new Vector2((f0.CanvasAabb.MinX + f0.CanvasAabb.MaxX) * 0.5f,
                                 (f0.CanvasAabb.MinY + f0.CanvasAabb.MaxY) * 0.5f);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f0, DpiScale);
            float arm = Vector2.Distance(grip, pv);
            float a0 = MathF.Atan2(grip.Y - pv.Y, grip.X - pv.X);
            // 要转到哪儿：矩形顺时针 8.4°（8 → -0.4，±1° 内 → 吸到 0）；
            //          椭圆逆时针 38.4°（52 → 90.4，±1° 内 → 吸到 90）。
            float turn = isRect ? -8.4f : 38.4f;

            SendMouse((int)grip.X, (int)grip.Y, 0);                          SettleFrames(60);
            SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            var last = grip;
            for (int i = 1; i <= 4; i++)
            {
                // 屏幕坐标 y 朝下：方向角**减小**才是视觉上的逆时针，所以 rad = a0 − 转过的角。
                float rad = a0 - turn * i / 4f * MathF.PI / 180f;
                last = new Vector2(pv.X + arm * MathF.Cos(rad), pv.Y + arm * MathF.Sin(rad));
                SendMouse((int)last.X, (int)last.Y, 0);
                SettleFrames(50);
            }
            SettleFrames(220);

            var live = LiveSelectionFrame;
            var gripNow = SelectionHandles.CanvasPosition(SelHandle.Rotate, live, DpiScale);
            var region = live.CanvasAabb;
            region.Add(gripNow.X - 70f * DpiScale, gripNow.Y - 80f * DpiScale);
            region.Add(gripNow.X + 70f * DpiScale, gripNow.Y + 20f * DpiScale);
            Console.WriteLine($"姿态角那一帧（{(isRect ? "矩形" : "椭圆")}）：读数 {SelRotationPose:F1}°"
                            + $"（吸住={SelRotationSnapped}），起手 {startDeg:F0}°，拖了 {turn:F1}°"
                            + $"，标签文案「{SelectionHandles.FormatPose(SelRotationPose)}」");
            if (!OffscreenFloatingShot(path, region.Inflate(40f))) Console.WriteLine("出图失败");
            SendMouse((int)last.X, (int)last.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(150);
            _quit = true;
            return;
        }

        // `--angles`：三角形和平行四边形**都要拖着一个顶点不松手**才出角度
        //（用户 2026-09-20 定：三角形也改成"拖动时才显示"，和平行四边形一条判据）。
        if (angleRead)
        {
            bool para = kind == "parallelogram";
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            if (para)
            {
                sh.Tool = Tool.Parallelogram; sh.Kind = StrokeKind.Parallelogram;
                sh.AddPoint(cx - 300f, cy + 170f, 1f, 0);      // 底左
                sh.AddPoint(cx + 300f, cy + 170f, 1f, 0);      // 底右
                sh.AddPoint(cx - 110f, cy - 170f, 1f, 0);      // 顶左
            }
            else
            {
                // 刻意**不等边**：三个角三个不同的数，出图时一眼能看出"每个角各自独立"
                sh.Tool = Tool.Triangle; sh.Kind = StrokeKind.Triangle;
                sh.AddPoint(cx - 70f, cy - 250f, 1f, 0);       // 上顶点
                sh.AddPoint(cx - 320f, cy + 170f, 1f, 0);      // 下左
                sh.AddPoint(cx + 320f, cy + 170f, 1f, 0);      // 下右
            }
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            // 拖一个顶点、**停住不松手**：三角形和平行四边形**都只在拖顶点时**才出角度
            // （用户 2026-09-20 定："三角形应该在拖动的时候再显示角度，要不然看起来也乱"），
            // 所以拍这一张必须按住不放——松手拍出来是空的。
            // 三角形拖**上顶点**（Vertex0）、平行四边形拖**底右顶点**（Vertex1）。
            var dragHandle = para ? ShapeHandle.Vertex1 : ShapeHandle.Vertex0;
            var dragBy = para ? new Vector2(70f, 40f) : new Vector2(90f, 70f);
            var v = SelectionHandles.ShapeHandleCanvasPosition(sh, dragHandle);
            var toP = new Vector2(v.X + dragBy.X, v.Y + dragBy.Y);
            SendMouse((int)v.X, (int)v.Y, 0);                            SettleFrames(60);
            SendMouse((int)v.X, (int)v.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(v.X + (toP.X - v.X) * i / 4f),
                          (int)(v.Y + (toP.Y - v.Y) * i / 4f), 0);
                SettleFrames(50);
            }
            SettleFrames(220);

            Span<Vector2> vs = stackalloc Vector2[4];
            Span<float> ds = stackalloc float[3];
            int n = FillAngleReadout(vs, ds);
            // **没有"内角和"那一行**（用户 2026-09-19 定），所以只报那几个角自己。
            Console.WriteLine(para
                ? $"夹角那一帧（平行四边形拖顶点中）：{n} 个角 = "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[0])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[1])}，顶点 {vs[0]} / {vs[1]}"
                : $"内角那一帧（三角形拖顶点中）：{n} 个角 = "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[0])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[1])} / "
                  + $"{SelectionHandles.FormatAngleDegrees(ds[2])}");
            var region = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            region = region.Inflate(220f * DpiScale);            // 角标挂在顶点外侧，框要放宽
            if (!OffscreenFloatingShot(path, region)) Console.WriteLine("出图失败");
            SendMouse((int)toP.X, (int)toP.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(150);
            _quit = true;
            return;
        }

        // ---- 圆 / 椭圆 / 三角形 / 平行四边形 / 抛物线 / 长方体：静止选中态 ----
        //（后两种是"**没有特殊手柄**"的那一类：拍的是通用八手柄 ＋ 旋转柄那根连线）
        if (kind != "line")
        {
            var sh = new Stroke
            {
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            switch (kind)
            {
                case "circle":
                    sh.Tool = Tool.Circle; sh.Kind = StrokeKind.Circle;
                    sh.AddPoint(cx - 40f, cy - 40f, 1f, 0);        // 圆心
                    sh.AddPoint(cx + 200f, cy - 40f, 1f, 0);       // 圆周点（半径 240）
                    break;
                case "ellipse":
                    sh.Tool = Tool.Ellipse; sh.Kind = StrokeKind.Ellipse;
                    sh.AddPoint(cx - 40f, cy, 1f, 0);              // 中心
                    sh.AddPoint(cx + 260f, cy + 180f, 1f, 0);      // 外角点（a=300, b=180）
                    break;
                case "triangle":
                    sh.Tool = Tool.Triangle; sh.Kind = StrokeKind.Triangle;
                    sh.AddPoint(cx, cy - 220f, 1f, 0);             // 上中
                    sh.AddPoint(cx - 260f, cy + 160f, 1f, 0);      // 下左
                    sh.AddPoint(cx + 260f, cy + 160f, 1f, 0);      // 下右
                    break;
                case "parabola":
                    // 抛物线：顶点在左下、经过点在右上 → 开口向上（四个朝向里最好认的那个）。
                    // 它是"没有特殊手柄"那一类的代表：选中后该看到**通用八手柄**，
                    // 旋转柄那根线该连到**框上边中点**（不是顶点与经过点的中点）。
                    sh.Tool = Tool.Parabola; sh.Kind = StrokeKind.Parabola;
                    sh.CurveAxis = CurveAxis.OpenUp;
                    sh.AddPoint(cx - 240f, cy + 150f, 1f, 0);      // 顶点（按下那个点）
                    sh.SetParabolaVertex(cx - 240f, cy + 150f);
                    sh.SetParabolaThroughPoint(cx + 200f, cy - 170f);
                    break;
                case "cuboid":
                    // 长方体：正面矩形 ＋ 深度 150（照画法：深度往右上退）。
                    sh.Tool = Tool.Cuboid; sh.Kind = StrokeKind.Cuboid;
                    sh.AddPoint(cx - 260f, cy - 40f, 1f, 0);
                    sh.SetCuboidFront(cx - 260f, cy - 40f, cx + 180f, cy + 200f);
                    sh.SetCuboidDepth(cy - 40f, cy - 40f + 150f);  // 深 = |正面上边 − 这个 y| = 150
                    break;
                case "spacecoordinate":
                    sh.Tool = Tool.SpaceCoordinate; sh.Kind = StrokeKind.SpaceCoordinate;
                    sh.AddPoint(cx - 260f, cy + 180f, 1f, 0);
                    sh.SetSpaceAxisBox(cx - 260f, cy + 180f, cx + 240f, cy - 180f);
                    break;
                default: // parallelogram
                    sh.Tool = Tool.Parallelogram; sh.Kind = StrokeKind.Parallelogram;
                    sh.AddPoint(cx - 250f, cy + 160f, 1f, 0);      // 底左
                    sh.AddPoint(cx + 250f, cy + 160f, 1f, 0);      // 底右
                    sh.AddPoint(cx - 125f, cy - 160f, 1f, 0);      // 顶左
                    break;
            }
            Doc.AddStroke(sh);
            Doc.SelectOnly(new[] { sh });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            // 吸附那一帧：真拖一次顶点，**停住不松手**再拍（松手就没有胶囊了）。
            // 三角形摆到"等边"的容差里（高 = √3/2 × 底边长 520 ≈ 450.3）。
            if (snap && kind == "triangle")
            {
                var apex = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.Vertex0);
                var apexTo = new Vector2(cx + 1f, cy + 160f - 260f * MathF.Sqrt(3f));
                SendMouse((int)apex.X, (int)apex.Y, 0);                          SettleFrames(60);
                SendMouse((int)apex.X, (int)apex.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
                for (int i = 1; i <= 4; i++)
                {
                    SendMouse((int)(apex.X + (apexTo.X - apex.X) * i / 4f),
                              (int)(apex.Y + (apexTo.Y - apex.Y) * i / 4f), 0);
                    SettleFrames(40);
                }
                SettleFrames(200);
                Console.WriteLine($"吸附帧：吸到={ShapeSnapKind}"
                                + $"（「{SelectionHandles.ShapeSnapLabel(ShapeSnapKind)}」），"
                                + $"被拖的顶点 {apex} → {apexTo}");
            }

            // `--drag` + 空间坐标系：真拖 X / Z 轴端缩短，松手后看有没有残留的旧选中框。
            if (drag && kind == "spacecoordinate")
            {
                void DragHandle(ShapeHandle h, float dx, float dy)
                {
                    var from = SelectionHandles.ShapeHandleCanvasPosition(sh, h);
                    var to = new Vector2(from.X + dx, from.Y + dy);
                    VertexDragForTest(sh, h, from.X, from.Y, to.X, to.Y);
                    SettleFrames(250);
                }
                DragHandle(ShapeHandle.SpaceX, -100f * DpiScale, -80f * DpiScale);
                DragHandle(ShapeHandle.SpaceZ, 0f, 120f * DpiScale);
                // 再把 Y 拖过原点：这是最容易留下旧框的一幕（旧框在右上方，新框跑到左下方）。
                {
                    var yh = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.SpaceY);
                    var oo = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.SpaceOrigin);
                    var yTo = oo - (yh - oo) * 0.8f;
                    VertexDragForTest(sh, ShapeHandle.SpaceY, yh.X, yh.Y, yTo.X, yTo.Y);
                    SettleFrames(350);
                }
                {
                    var oo = SelectionHandles.ShapeHandleCanvasPosition(sh, ShapeHandle.SpaceOrigin);
                    VertexDragForTest(sh, ShapeHandle.SpaceOrigin, oo.X, oo.Y, oo.X + 80f, oo.Y + 40f);
                    SettleFrames(350);
                }
            }

            var sFrame = SelectionHandles.FrameOf(Doc.Selected);
            var sAabb = sFrame.CanvasAabb;
            var sBar = SelectionHandles.BarRect(sAabb, UiUnit, FurnitureViewport);
            var region = sAabb;
            region.Add(sBar);
            if (SelectionHandles.RotateHandleVisible(sh))       // 圆没有旋转柄，别白算一块
            {
                var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, sFrame, DpiScale);
                region.Add(grip.X - 40f * DpiScale, grip.Y - 40f * DpiScale);
                region.Add(grip.X + 40f * DpiScale, grip.Y + 40f * DpiScale);
            }
            // 拖元素时的胶囊挂在元素外侧：`--snap` 那张必须把它一起圈进来
            if (snap && VertexDragging)
            {
                region.Add(VertexPreviewCanvasPoint.X - 90f * DpiScale,
                           VertexPreviewCanvasPoint.Y - 60f * DpiScale);
                region.Add(VertexPreviewCanvasPoint.X + 90f * DpiScale,
                           VertexPreviewCanvasPoint.Y + 10f * DpiScale);
            }
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");
            if (snap && VertexDragging)
            {
                SendMouse((int)ShapeSnapAnchor.X, (int)ShapeSnapAnchor.Y, Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(150);
            }
            _quit = true;
            return;
        }

        // ---- 一条"已经转过 60°"的直线、静止选中（紧框改口径的第二个现场）----
        if (rotated)
        {
            var rl = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 和另外几张**同一条线**（α₀ ≈ 27.65°），绕它自己的中点视觉逆时针转 32.35°
            // → 转完 α ≈ 60°，正是 --rotate 松手之后那个状态。
            rl.AddPoint(cx - 420f, cy + 220f, 1f, 0);
            rl.AddPoint(cx + 420f, cy - 220f, 1f, 0);
            rl.Transform = SelectionHandles.RotateMatrix(32.35f, new Vector2(cx, cy));
            Doc.AddStroke(rl);
            Doc.SelectOnly(new[] { rl });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var fr = SelectionHandles.FrameOf(Doc.Selected);
            var aabbR = fr.CanvasAabb;
            var barR = SelectionHandles.BarRect(aabbR, UiUnit, FurnitureViewport);
            var gripR = SelectionHandles.CanvasPosition(SelHandle.Rotate, fr, DpiScale);
            var regionR = aabbR;
            regionR.Add(barR);
            regionR.Add(gripR.X - 40f * DpiScale, gripR.Y - 40f * DpiScale);
            regionR.Add(gripR.X + 40f * DpiScale, gripR.Y + 40f * DpiScale);
            Console.WriteLine($"转过 60° 的线、静止选中：框 {aabbR.MaxX - aabbR.MinX:F0}×{aabbR.MaxY - aabbR.MinY:F0}"
                            + $"，线自己的墨迹 {rl.WorldInkBounds.MaxX - rl.WorldInkBounds.MinX:F0}"
                            + $"×{rl.WorldInkBounds.MaxY - rl.WorldInkBounds.MinY:F0}"
                            + $"，PaddedBounds {rl.PaddedBounds.MaxX - rl.PaddedBounds.MinX:F0}"
                            + $"×{rl.PaddedBounds.MaxY - rl.PaddedBounds.MinY:F0}");
            if (!OffscreenFloatingShot(path, regionR.Inflate(30f))) Console.WriteLine("出图失败");
            _quit = true;
            return;
        }

        // ---- 旋转拖动中：真按真转（用户要看的就是这一帧）----
        if (rotate)
        {
            var spinLine = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
            };
            // 和"静止选中"那张**同一条线**（α ≈ 27.65°），三张图才好对比
            spinLine.AddPoint(cx - 420f, cy + 220f, 1f, 0);
            spinLine.AddPoint(cx + 420f, cy - 220f, 1f, 0);
            Doc.AddStroke(spinLine);
            Doc.SelectOnly(new[] { spinLine });
            Tool = Tool.Marquee;
            Doc.InvalidateAll();
            SettleFrames(700);

            var f0 = SelectionHandles.FrameOf(Doc.Selected);
            var pv = new Vector2((f0.CanvasAabb.MinX + f0.CanvasAabb.MaxX) * 0.5f,
                                 (f0.CanvasAabb.MinY + f0.CanvasAabb.MaxY) * 0.5f);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f0, DpiScale);
            float arm = Vector2.Distance(grip, pv);
            float a0 = MathF.Atan2(grip.Y - pv.Y, grip.X - pv.X);

            SendMouse((int)grip.X, (int)grip.Y, 0);                          SettleFrames(60);
            SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            // 逆时针 31.5°：这条线 α₀ ≈ 27.65° → 59.15°（±1° 内）→ 吸到 60°，读数好看也吸住了
            const float Turn = 31.5f;
            var last = grip;
            for (int i = 1; i <= 4; i++)
            {
                float rad = a0 - Turn * i / 4f * MathF.PI / 180f;
                last = new Vector2(pv.X + arm * MathF.Cos(rad), pv.Y + arm * MathF.Sin(rad));
                SendMouse((int)last.X, (int)last.Y, 0);
                SettleFrames(40);
            }
            SettleFrames(200);

            // 范围：**实时框**（旋转中框按预览几何算，见 LiveSelectionFrame）∪ 旋转柄 ∪ 读数标签
            var live = LiveSelectionFrame;
            var aabb = live.CanvasAabb;
            var gripNow = SelectionHandles.CanvasPosition(SelHandle.Rotate, live, DpiScale);
            var region = aabb;
            region.Add(gripNow.X - 60f * DpiScale, gripNow.Y - 60f * DpiScale);
            region.Add(gripNow.X + 60f * DpiScale, gripNow.Y + 60f * DpiScale);
            region.Add(gripNow.X - 90f * DpiScale, gripNow.Y - 90f * DpiScale);
            region.Add(gripNow.X + 90f * DpiScale, gripNow.Y);
            // 顺带量一个"线**真实**占多大"（把两个端点过实时矩阵再外扩半笔宽）——
            // 用来和上面那个框比：相等就是贴合，明显小就是框虚胖（只打印，不改行为）。
            var liveM = spinLine.Transform * DragPreviewMatrix;
            var t0 = Vector2.Transform(new Vector2(spinLine.Points[0].X, spinLine.Points[0].Y), liveM);
            var t1 = Vector2.Transform(new Vector2(spinLine.Points[^1].X, spinLine.Points[^1].Y), liveM);
            var tight = RectF.Empty;
            tight.Add(t0.X, t0.Y);
            tight.Add(t1.X, t1.Y);
            tight = tight.Inflate(spinLine.Width * 0.5f + 2f);
            Console.WriteLine($"旋转中：读数 {SelRotationInclination:F1}°（吸住={SelRotationSnapped}），"
                            + $"实时框 {aabb.MaxX - aabb.MinX:F0}×{aabb.MaxY - aabb.MinY:F0}"
                            + $" ({(int)aabb.MinX},{(int)aabb.MinY})-({(int)aabb.MaxX},{(int)aabb.MaxY})，"
                            + $"线自己真实占 {tight.MaxX - tight.MinX:F0}×{tight.MaxY - tight.MinY:F0}"
                            + $" ({(int)tight.MinX},{(int)tight.MinY})-({(int)tight.MaxX},{(int)tight.MaxY})"
                            + $"，模型里（没动）的墨迹范围 "
                            + $"{(int)spinLine.WorldInkBounds.MinX},{(int)spinLine.WorldInkBounds.MinY}"
                            + $"-{(int)spinLine.WorldInkBounds.MaxX},{(int)spinLine.WorldInkBounds.MaxY}");
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");

            SendMouse((int)last.X, (int)last.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(250);
            // 松手后框会重新按**模型**算（FrameOf）——和拖动中那个"旧框转过去的外接矩形"
            // 差多少，这里给出数字（只打印，不改行为）。
            var afterAabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            Console.WriteLine($"旋转松手后：框 {afterAabb.MaxX - afterAabb.MinX:F0}×{afterAabb.MaxY - afterAabb.MinY:F0}"
                            + $"（拖动中是 {aabb.MaxX - aabb.MinX:F0}×{aabb.MaxY - aabb.MinY:F0}），"
                            + $"线的实时姿态 "
                            + $"{SelectionHandles.InclinationDegrees(SelectionHandles.EndpointCanvasPosition(spinLine, 0), SelectionHandles.EndpointCanvasPosition(spinLine, 1)):F1}°");
            _quit = true;
            return;
        }

        // ---- 正在画一条直线：按住不放，拖到**原始 45.5°**（±1° 容差内 → 吸到 45°）----
        if (drawing)
        {
            SetToolFromUi(Tool.Line);
            var from = new Vector2(cx - 380f, cy + 200f);
            float rad = -45.5f * MathF.PI / 180f;
            var tipTarget = new Vector2(from.X + 620f * MathF.Cos(rad), from.Y + 620f * MathF.Sin(rad));

            SendMouse((int)from.X, (int)from.Y, 0);                            SettleFrames(60);
            SendMouse((int)from.X, (int)from.Y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(from.X + (tipTarget.X - from.X) * i / 4f),
                          (int)(from.Y + (tipTarget.Y - from.Y) * i / 4f), 0);
                SettleFrames(40);
            }
            SettleFrames(200);

            var tip = ShapeInclinationAnchor;
            // 起手必须是 RectF.Empty（默认构造的 RectF 是 (0,0)-(0,0)，会把画面原点算进来）
            var drawBox = RectF.Empty;
            drawBox.Add(from.X, from.Y);
            drawBox.Add(tip.X, tip.Y);
            drawBox = drawBox.Inflate(30f * DpiScale);
            // 读数标签挂在"正在拖的那一端"上方，高度 30 逻辑 + 一段间距：一起圈进画面
            drawBox.Add(tip.X - 80f * DpiScale, tip.Y - 60f * DpiScale);
            drawBox.Add(tip.X + 80f * DpiScale, tip.Y);
            Console.WriteLine($"画线中：α = {ShapeInclinationDegrees:F1}°（吸住={ShapeInclinationSnapped}）"
                            + $"，起点 {from}，笔尖 {tip}"
                            + $"，出图范围 ({drawBox.MinX:F0},{drawBox.MinY:F0})-({drawBox.MaxX:F0},{drawBox.MaxY:F0})");
            if (!OffscreenFloatingShot(path, drawBox)) Console.WriteLine("出图失败");

            SendMouse((int)tipTarget.X, (int)tipTarget.Y, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(120);
            _quit = true;
            return;
        }

        var line = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f * DpiScale,
        };
        line.AddPoint(cx - 420f, cy + 220f, 1f, 0);
        line.AddPoint(cx + 420f, cy - 220f, 1f, 0);
        Doc.AddStroke(line);
        Doc.SelectOnly(new[] { line });
        Tool = Tool.Marquee;
        Doc.InvalidateAll();
        SettleFrames(700);

        if (!drag)
        {
            // 范围 = 选中框 ∪ 旋转柄 ∪ 操作条（操作条也由 DrawSelection 画，
            // 不并进来会被裁掉一半），再留一圈给投影。
            var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var bar = SelectionHandles.BarRect(aabb, UiUnit, FurnitureViewport);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate,
                                                       SelectionHandles.FrameOf(Doc.Selected), DpiScale);
            var region = aabb;
            region.Add(bar);
            region.Add(grip.X - 40f, grip.Y - 40f);
            region.Add(grip.X + 40f, grip.Y + 40f);
            if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");
            _quit = true;
            return;
        }

        // 真拖一次端点：原始方向 30.5°（±1° 容差内）→ 读数该显示 α = 30.0°。
        // 用真输入（而不是直接摆内部状态），出的图才是产品里那一帧。
        var eFixed = SelectionHandles.EndpointCanvasPosition(line, 1);
        var eDrag = SelectionHandles.EndpointCanvasPosition(line, 0);
        float theta = 149.5f * MathF.PI / 180f;
        var to = new Vector2(eFixed.X + 700f * MathF.Cos(theta), eFixed.Y + 700f * MathF.Sin(theta));

        SendMouse((int)eDrag.X, (int)eDrag.Y, 0);                          SettleFrames(60);
        SendMouse((int)eDrag.X, (int)eDrag.Y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
        for (int i = 1; i <= 4; i++)
        {
            SendMouse((int)(eDrag.X + (to.X - eDrag.X) * i / 4f),
                      (int)(eDrag.Y + (to.Y - eDrag.Y) * i / 4f), 0);
            SettleFrames(40);
        }
        SettleFrames(200);

        var preview = VertexPreviewCanvasPoint;
        // 起手必须是 RectF.Empty：默认构造的 RectF 是 (0,0)-(0,0)，
        // 直接 Add 进去会把画面原点也算进范围（出图一半是空白就是它）。
        var box = RectF.Empty;
        box.Add(eFixed.X, eFixed.Y);
        box.Add(preview.X, preview.Y);
        box = box.Inflate(40f * DpiScale);
        // 读数标签挂在被拖端点上方，高度 30 逻辑 + 一段间距：把它一起圈进画面
        box.Add(preview.X - 70f * DpiScale, preview.Y - 60f * DpiScale);
        box.Add(preview.X + 70f * DpiScale, preview.Y);
        Console.WriteLine($"拖端点中：α = {VertexReadoutValue:F1}°（吸住={VertexInclinationSnapped}），"
                        + $"固定端 {eFixed}，拖动端 {preview}"
                        + $"（模型里这条线还没动：墨迹范围 {(int)line.WorldInkBounds.MinX},{(int)line.WorldInkBounds.MinY}"
                        + $"-{(int)line.WorldInkBounds.MaxX},{(int)line.WorldInkBounds.MaxY}），"
                        + $"出图范围 ({box.MinX:F0},{box.MinY:F0})-({box.MaxX:F0},{box.MaxY:F0})");
        if (!OffscreenFloatingShot(path, box)) Console.WriteLine("出图失败");

        SendMouse((int)to.X, (int)to.Y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(120);
        _quit = true;
    }

    /// <summary>
    /// 折角填充自检：**拐角处不能掉色**（写"L""V""7""∠"这类折笔时，填充在
    /// 自交处被挖空的那个老 bug）。
    ///
    /// 判据在 2026-09-14 重写过，因为**旧判据自己有问题**：它拿"拐角窗口的墨量"
    /// 比"直段窗口的墨量"，要求前者 ≥ 后者的 95%。可是拐角窗口里本来就有 3/4 的
    /// 地方没墨（两个臂分别从窗口的左边和下边出去），所以这条自检从写下来那天起
    /// 就是红的——一条永远红的自检等于没有自检，它把真正的信号淹掉了。
    ///
    /// 现在的判据直接对着**要防的 bug**：沿笔迹中心线取一串采样点（含拐角本身），
    /// 每个点在填充里都必须有墨（FillContainsPoint）。拐角被挖空 → 拐角那个点
    /// 就是空的 → 报红。另加一条"上屏兜底"（白板 + 拐角窗口数黑像素），
    /// 因为"几何对但没画到屏幕上"也是一种可能。
    /// </summary>
    /// <summary>
    /// 自交叠色自检：**一笔自己穿过自己的地方，颜色不能变深**。
    ///
    /// 为什么单独拎出来验：我们画的是"一条闭合轮廓 + 一次填充"，理论上自交处只会让
    /// Winding 计数变成 2，仍然只合成一次；但中间有一道工序可能破坏这个前提——
    /// **几何细分缓存**（`CreateFilledGeometryRealization`）如果按三角形逐个合成，
    /// 重叠区就会被合成两次。用户报的"画角时内部变深"正是这个形状，所以要专门盯。
    ///
    /// 判据：在同一笔的"自交重叠区"和"普通区"各取一个像素，颜色必须相同
    /// （半透明荧光笔最敏感——叠两次一眼就能看出来）。
    /// </summary>
}
