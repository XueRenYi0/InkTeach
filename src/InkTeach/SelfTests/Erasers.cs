// 本文件由 App.cs 拆出（2026-10-07）：Erasers 这一组。
// **纯搬家，逻辑一字未改** —— 靠 partial class 共享 App 的私有成员。
// 拆开的目的：产品代码与自检代码互不干扰，人和 AI 读代码时不必互相穿插。

using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{

    private void EraserTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 橡皮擦测试（用稀疏采样快速划过，看会不会漏）===");

        if (SkipIfNoSyntheticInput("稀疏采样快划那一组")) { _quit = true; return; }

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        for (int k = -1; k <= 1; k++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = new Color4(1f, 0f, 1f, 1f),
                Width = 10f * DpiScale,
            };
            for (int i = 0; i <= 80; i++)
                s.AddPoint(cx - 500 + i * 12, cy + k * 60, 0.9f, NowMs);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(400);

        int before = Doc.Strokes.Count;
        int undoBefore = Doc.UndoDepth;
        Console.WriteLine($"  先画了 {before} 条横线（间隔 60 px），橡皮擦半径 {EraserRadius:F0} px");

        Tool = Tool.Eraser;
        float y0 = cy - 200, y1 = cy + 200;
        SendMouse((int)cx, (int)y0, 0);
        SettleFrames(60);
        SendMouse((int)cx, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        // Only four samples over 400 px: a fast flick.
        const int coarse = 4;
        for (int i = 1; i <= coarse; i++)
        {
            SendMouse((int)cx, (int)(y0 + (y1 - y0) * i / coarse), 0);
            SettleFrames(10);
        }
        SendMouse((int)cx, (int)y1, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(400);

        int after = Doc.Strokes.Count;
        int undoAfter = Doc.UndoDepth;
        int leftover = ScreenProbe.CountMagenta((int)(cx - 520), (int)(cy - 260), 1040, 520);

        Console.WriteLine($"  笔画数 {before} -> {after}   期望 0");
        Console.WriteLine($"  撤销步数增加 {undoAfter - undoBefore}   期望 1（整段拖拽算一步）");
        Console.WriteLine($"  屏幕上残留品红像素 {leftover}   期望接近 0");

        bool ok = after == 0 && undoAfter - undoBefore == 1 && leftover < 200;
        Console.WriteLine(ok ? "  PASS" : "  FAIL");
        _quit = true;
    }


    /// <summary>
    /// 像素橡皮自检（--pixelerasetest）。
    ///
    /// 判据全是"看得见的行为"，不是内部状态：
    ///   ① 一条横线被竖着抹一下 → 变成**两段**，切口正好停在橡皮边界外（框里不留墨）；
    ///   ② 整条落在橡皮里的 → 消失，撤销回来还是原来那一条；
    ///   ③ 没碰到的 → 原样不动（**引用相等**：重建一遍看不出来，但白扔几何缓存）；
    ///   ④ 图形要"轮廓真的碰到"才删——橡皮从一个大图形正中间划过不能把整个图形吃掉；
    ///   ⑤ 一次拖拽扫过多个位置 = **一步**撤销，撤销后文档逐条回到拖之前；
    ///   ⑥ 上屏像素：框里的墨归零、框外还在。
    /// 外加两条结构判据：默认落点是**竖着的黄金比矩形**；这个工具的落点反馈画的是矩形。
    /// </summary>
    private void PixelEraserTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 像素橡皮自检（切段 / 框里无墨 / 一步撤销）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-36} {detail}");
        }

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        const float halfW = 30f, halfH = 60f;     // 测试用的小块（默认那块 93×150 太大）
        const float penW = 16f;
        const float reach = penW * 0.5f;          // 半笔宽：切口要停在框外这么远

        // --- 0. 形状与落点反馈 -------------------------------------------------
        Check("落点是竖着的黄金比例矩形",
              PixelEraserWidthLogical > 0f && PixelEraserHeightLogical > PixelEraserWidthLogical
              && MathF.Abs(PixelEraserHeightLogical / PixelEraserWidthLogical - GoldenRatio) < 0.002f,
              $"{PixelEraserWidthLogical:F0}×{PixelEraserHeightLogical:F0} 逻辑像素，"
              + $"高:宽 = {PixelEraserHeightLogical / PixelEraserWidthLogical:F3}");

        var savedTool = Tool;
        bool savedInside = PointerInside;
        var savedDevice = LastPointerType;
        Tool = Tool.PixelEraser;
        PointerInside = true;                  // 落点反馈只在"指针在窗口里"时才画
        LastPointerType = Native.PT_MOUSE;
        Check("落点反馈是矩形 + 系统光标藏起来",
              DrawnCursor == ToolCursorShape.Rect && ComputeCursorKind() == CursorKind.Hidden,
              $"{DrawnCursor} / {ComputeCursorKind()}");
        Tool = savedTool;
        PointerInside = savedInside;
        LastPointerType = savedDevice;

        // --- 1. 一条横线抹一下：应该变成两段，切口停在框外 ----------------------
        Doc.Clear();
        Doc.ClearHistory();

        var line = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 80; i++) line.AddPoint(cx - 400 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(line);

        int undoBefore = Doc.UndoDepth;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();

        var pieces = Doc.Strokes.ToArray();
        Check("擦断 → **两条独立对象**（原件不再在文档里）",
              pieces.Length == 2 && !Doc.Strokes.Contains(line),
              $"对象数 {pieces.Length}，原件 {(Doc.Strokes.Contains(line) ? "还在" : "已换成两段")}");
        Check("一次擦除只算一步撤销", Doc.UndoDepth == undoBefore + 1,
              $"撤销栈 {undoBefore} → {Doc.UndoDepth}");
        Check("两段各自是一个完整对象（没有残留区间表）",
              pieces.Length == 2 && pieces[0].Erased.Count == 0 && pieces[1].Erased.Count == 0
              && pieces[0].Points.Count + pieces[1].Points.Count >= 60,
              pieces.Length == 2
                ? $"点数 {pieces[0].Points.Count}/{pieces[1].Points.Count}（原 81 点）"
                : "没拆成两段");

        float leftEnd = pieces.Length == 2 ? pieces[0].Bounds.MaxX : float.NaN;
        float rightStart = pieces.Length == 2 ? pieces[1].Bounds.MinX : float.NaN;
        float bandLeft = cx - halfW - reach, bandRight = cx + halfW + reach;
        Check("框里不留墨（切口在边界外）",
              pieces.Length == 2 && leftEnd <= bandLeft + 0.5f && rightStart >= bandRight - 0.5f,
              $"左段到 {leftEnd:F1}（该 ≤ {bandLeft:F1}），右段从 {rightStart:F1}（该 ≥ {bandRight:F1}）");

        bool anyInside = false;
        foreach (var p in pieces)
            foreach (var q in p.Points)
                if (MathF.Abs(q.Y - cy) < halfH && MathF.Abs(q.X - cx) < halfW + reach - 0.5f)
                    anyInside = true;
        Check("剩下的墨没有一点伸进框里", !anyInside,
              anyInside ? "有采样点落在框内" : "两段的采样点都在框外");

        // --- 2. 撤销 / 重做：同一个对象，区间表清空 / 回来 ----------------------
        bool undone = Doc.Undo();
        Check("撤销：同一个对象、擦除区间清空",
              undone && Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], line)
              && line.Erased.Count == 0 && line.RemainingRuns().Count == 1 && line.Points.Count == 81,
              $"笔画数 {Doc.Strokes.Count}，区间 {line.Erased.Count} 段，点数 {line.Points.Count}");

        bool redone = Doc.Redo();
        Check("重做：又变回两条独立对象",
              redone && Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(line)
              && Doc.Strokes[0].Erased.Count == 0,
              $"对象数 {Doc.Strokes.Count}");

        // --- 2b. 同一笔擦两刀 / 重复擦已经擦过的地方 ---------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var longLine = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 120; i++) longLine.AddPoint(cx - 600 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(longLine);
        int undo2 = Doc.UndoDepth;

        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx - 200, cy, 20f, 30f);
        Doc.EraseRectAt(cx + 200, cy, 20f, 30f);
        Doc.EndErase();
        Check("同一笔擦两刀 → **三段独立对象**、一步撤销",
              Doc.Strokes.Count == 3 && !Doc.Strokes.Contains(longLine)
              && Doc.UndoDepth == undo2 + 1,
              $"对象 {Doc.Strokes.Count}、撤销栈 +{Doc.UndoDepth - undo2}");

        // 再擦一次"落在已经擦掉的地方"：不能多出撤销步，也不能把表搞乱
        int undo3 = Doc.UndoDepth;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx - 200, cy, 20f, 30f);
        Doc.EndErase();
        Check("重复擦已经擦过的地方 → 不多出撤销步",
              Doc.UndoDepth == undo3 && Doc.Strokes.Count == 3,
              $"撤销栈 +{Doc.UndoDepth - undo3}，对象还是 {Doc.Strokes.Count} 条");

        // 撤销回到"擦之前那一条"（而不是把两刀拆成两步）
        Doc.Undo();
        Check("一步撤销回到擦之前（原来那一条、点数不变）",
              Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], longLine)
              && longLine.Erased.Count == 0 && longLine.Points.Count == 121,
              $"对象 {Doc.Strokes.Count}，区间 {longLine.Erased.Count} 段，点数 {longLine.Points.Count}");

        // --- 3. 整条在框里 / 完全没碰到 ----------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var inside = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 10; i++) inside.AddPoint(cx - 10 + i * 2, cy - 10 + i * 2, 0.9f, i);
        var faraway = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
        for (int i = 0; i <= 40; i++) faraway.AddPoint(cx - 700 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(inside);
        Doc.AddStroke(faraway);

        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();
        Check("框里的整条被擦掉、框外的不动",
              Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], faraway),
              $"剩下 {Doc.Strokes.Count} 条"
              + (Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], faraway)
                 ? "（框外那条，引用没变）" : "（不是框外那条！）"));

        Doc.Undo();
        Check("撤销把整条放回来（含原顺序）",
              Doc.Strokes.Count == 2 && ReferenceEquals(Doc.Strokes[0], inside)
              && ReferenceEquals(Doc.Strokes[1], faraway),
              $"笔画数 {Doc.Strokes.Count}");

        // --- 4. 图形：碰到就**熔成笔迹再切**（用户 2026-09-15 定：断开也要单独算）---------
        Doc.Clear();
        Doc.ClearHistory();
        var crossed = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 4f,
        };
        crossed.AddPoint(cx - 10, cy - 200, 1f, 0);      // 两条竖边正好穿过橡皮
        crossed.AddPoint(cx + 10, cy + 200, 1f, 0);

        var bigShape = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 4f,
        };
        bigShape.AddPoint(cx - 400, cy - 400, 1f, 0);    // 外框很大，轮廓离橡皮很远
        bigShape.AddPoint(cx + 400, cy + 400, 1f, 0);

        Doc.AddStroke(crossed);
        Doc.AddStroke(bigShape);
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();
        bool allFreehand = true;
        foreach (var s in Doc.Strokes)
            if (!ReferenceEquals(s, bigShape) && s.Kind != StrokeKind.Freehand) allFreehand = false;
        Check("图形被擦到 → 熔成笔迹并切开（剩下的段各自独立）",
              !Doc.Strokes.Contains(crossed) && allFreehand && Doc.Strokes.Count >= 3,
              $"剩下 {Doc.Strokes.Count} 条，全是普通笔迹={allFreehand}（原图形已不在）");
        Check("橡皮从大图形正中间划过 → 不删", Doc.Strokes.Contains(bigShape),
              "外框和橡皮相交，但轮廓离橡皮 370 像素（按外框判就会误删）");

        // 图形的撤销：**一步回到原来那个图形**（熔是内部的，不该让用户多撤一步）
        Doc.Undo();
        Check("图形熔成笔迹后，一步撤销回到原图形",
              Doc.Strokes.Count == 2 && Doc.Strokes.Contains(crossed)
              && crossed.Kind == StrokeKind.Rectangle,
              $"对象 {Doc.Strokes.Count}，原图形 {(Doc.Strokes.Contains(crossed) ? "回来了" : "没回来")}");

        // --- 4b. 图形的"熔"必须是**一整组、外形逐笔不变** --------------------------
        // 用户 2026-09-20 反馈："面积橡皮擦图形，擦掉一部分剩下的会发生很奇怪的变化，
        // 比如虚线变实线，或者多出来线"。根因：熔的时候把轮廓（含抬笔标记）压成一条、
        // 辅助线（渐近线 / 被挡的棱）又不在轮廓里。下面逐条钉住修好之后的样子。
        Doc.Clear();
        Doc.ClearHistory();

        // 长方体（两笔画的）：正面 (cx−300, cy−200)..(cx+100, cy+100)，深度 = 80
        var cube = new Stroke
        {
            Tool = Tool.Cuboid, Kind = StrokeKind.Cuboid,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 4f,
        };
        cube.SetCuboidFront(cx - 300, cy - 200, cx + 100, cy + 100);
        cube.SetCuboidDepth(cx - 220, cy - 120);          // d = |cy−200 − (cy−120)| = 80
        Doc.AddStroke(cube);

        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx + 100, cy - 50, halfW, halfH);  // 只切正面右边那条竖棱
        Doc.EndErase();

        int cubeParts = 0, cubeDashed = 0, cubeMaxPts = 0;
        bool cubeThinDash = true;
        foreach (var s in Doc.Strokes)
        {
            if (s.Kind != StrokeKind.Freehand) continue;
            cubeParts++;
            if (s.Dash == StrokeDash.Dashed)
            {
                cubeDashed++;
                if (s.Width > 3f) cubeThinDash = false;   // 辅助线是 0.6 倍细（4 × 0.6 = 2.4）
            }
            cubeMaxPts = Math.Max(cubeMaxPts, s.Points.Count);
        }
        Check("长方体被擦到 → 熔成**一条棱一段**（不再是压成一条的 24 点折线）",
              cubeParts >= 12 && cubeMaxPts <= 4,
              $"熔出 {cubeParts} 条、最长 {cubeMaxPts} 点（压成一条会是 1 条 24 点＋横穿的假线）");
        Check("长方体被挡住的三条棱熔完**还是虚线、还是细的**",
              cubeDashed == 3 && cubeThinDash,
              $"虚线 {cubeDashed} 条（期望 3），细的={cubeThinDash}");

        Doc.Undo();
        Check("长方体熔成墨之后，一步撤销回到原图形",
              Doc.Strokes.Count == 1 && Doc.Strokes.Contains(cube) && cube.Kind == StrokeKind.Cuboid,
              $"对象 {Doc.Strokes.Count}（期望 1 个长方体）");

        // --- 4c. 双曲线：两支之间**不许连出假线**，渐近线熔完还是虚线 -----------------
        Doc.Clear();
        Doc.ClearHistory();
        var hyp = new Stroke
        {
            Tool = Tool.Hyperbola, Kind = StrokeKind.Hyperbola,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 4f,
        };
        hyp.SetHyperbolaFromAsymptote(cx, cy, cx + 300, cy + 200, 8f);
        // 经过点挑得离顶点远一点，右支才有足够长（曲线**只画到老师拖到的那个点为止**）
        hyp.SetHyperbolaThroughPoint(cx + 250, cy + 130);
        Doc.AddStroke(hyp);

        Doc.BeginEraseRect();
        // 落点挑在**右支的顶点附近**（x = a ≈ 156 处，该支的 y = 0）：那里离渐近线最远
        //（渐近线在 x = 126..186 这一段是 y = 84..124，全在橡皮的 [−60,60] 之外），
        // 于是"只切曲线、不碰渐近线"，两条渐近线正好整条保留。
        Doc.EraseRectAt(cx + 156, cy, halfW, halfH);
        Doc.EndErase();

        int hyParts = 0, hyDashed = 0;
        bool noBridge = true;
        foreach (var s in Doc.Strokes)
        {
            if (s.Kind != StrokeKind.Freehand) continue;
            hyParts++;
            if (s.Dash == StrokeDash.Dashed) hyDashed++;
            // 一支曲线整个在中心的一侧（实轴沿 x 时：右支 x ≥ cx，左支 x ≤ cx）。
            // 熔成一条的话，这个对象会**同时**跨到两侧——那正是"多出来一条横穿的长线"。
            if (s.Points.Count > 4)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var p in s.Points) { lo = MathF.Min(lo, p.X); hi = MathF.Max(hi, p.X); }
                if (lo < cx - 10 && hi > cx + 10) noBridge = false;
            }
        }
        Check("双曲线被擦到 → 熔成好几笔，两支之间**没有横穿的假线**",
              hyParts >= 4 && noBridge,
              $"熔出 {hyParts} 笔（两支＋两条渐近线，被切的会再拆），跨中心的长笔={(noBridge ? "没有" : "有！")}");
        Check("双曲线熔完，两条渐近线**还是虚线**", hyDashed == 2, $"虚线 {hyDashed} 条（期望 2）");

        // --- 4d. 圆柱：熔出来是 5 笔（顶圈 / 下半圈 / 两条母线 / 虚线半圈）------------
        Doc.Clear();
        Doc.ClearHistory();
        var cyl = new Stroke
        {
            Tool = Tool.Cylinder, Kind = StrokeKind.Cylinder,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 4f,
        };
        cyl.SetSolidBox(cx - 200, cy - 250, cx + 200, cy + 250);
        Doc.AddStroke(cyl);

        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx - 200, cy, halfW, halfH);       // 擦左边那条母线
        Doc.EndErase();

        int cylParts = 0, cylDashed = 0;
        foreach (var s in Doc.Strokes)
        {
            if (s.Kind != StrokeKind.Freehand) continue;
            cylParts++;
            if (s.Dash == StrokeDash.Dashed) cylDashed++;
        }
        Check("圆柱被擦到 → 熔成 5 笔以上（母线被切会再拆），其中虚线半圈仍是虚线",
              cylParts >= 5 && cylDashed == 1,
              $"熔出 {cylParts} 笔、虚线 {cylDashed} 条（顶圈＋下半圈＋两母线＋被挡的半圈）");

        // --- 4e. 擦在**空白处**（轮廓够不着）→ 图形原封不动，连熔都不熔 ---------------
        Doc.Clear();
        Doc.ClearHistory();
        var co = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = new Color4(0.2f, 0.2f, 0.2f, 1f), Width = 3f,
        };
        co.SetAxisBox(cx - 400, cy - 300, cx + 400, cy + 300);
        Doc.AddStroke(co);

        Doc.BeginEraseRect();
        // 落点在一根**网格线**上（离两条轴各一步远）：网格不在"碰到没有"那份轮廓里，
        // 所以这里够不着这个坐标系 —— 结论应是"什么都没发生"（而不是整个坐标系被熔）。
        int touched = Doc.EraseRectAt(cx + 200, cy + 150, halfW, halfH);
        Doc.EndErase();
        Check("橡皮落在坐标系的网格线上 → 不碰它（网格不是可擦对象，也不该把坐标系熔掉）",
              touched == 0 && Doc.Strokes.Count == 1 && Doc.Strokes.Contains(co),
              $"受影响 {touched} 条，对象 {Doc.Strokes.Count}（期望 0 / 1）");

        // --- 4b. **双曲线的渐近线：橡皮必须擦得到**（用户 2026-09-21 报的 bug）----------
        // 症状："双曲线的渐近线好像擦不掉"——判定"碰到没有"的那一份里**没有辅助线**
        //（轮廓只含两支曲线），所以橡皮从虚线上掠过时判定"没碰上"、那一刀什么也没擦。
        //
        // 判据要挑得准：**沿渐近线找一个"离曲线足够远（> 10 像素）"的点**再擦 ——
        // 只有这样才排除"其实是擦到曲线了"，老写法在这儿必然一条都碰不到。
        Doc.Clear();
        Doc.ClearHistory();
        var hb = new Stroke
        {
            Tool = Tool.Hyperbola, Kind = StrokeKind.Hyperbola,
            Color = new Color4(0f, 0f, 1f, 1f), Width = 4f * DpiScale,
            ShowAsymptotes = true,
        };
        hb.AddPoint(cx, cy, 1f, 0);                  // 中心
        hb.AddPoint(cx + 150f, cy + 100f, 1f, 0);    // 渐近线框的角点
        hb.AddPoint(cx + 200f, cy + 60f, 1f, 0);     // 曲线经过的一点
        Doc.AddStroke(hb);

        Vector2 pickOnAsym = default;
        bool foundAsym = false;
        var hbOutline = hb.ShapeOutline();
        for (int side = -1; side <= 1 && !foundAsym; side += 2)
        {
            var (from, to) = hb.HyperbolaAsymptoteLocal(side);
            for (int k = 1; k < 20 && !foundAsym; k++)
            {
                var q = Vector2.Lerp(from, to, k / 20f);
                float d = float.MaxValue;
                for (int m = 0; m < hbOutline.Count; m++)
                    if (!Stroke.IsOutlineBreak(hbOutline[m])) d = MathF.Min(d, Vector2.Distance(q, hbOutline[m]));
                if (d > 10f) { pickOnAsym = q; foundAsym = true; }
            }
        }
        Doc.BeginEraseRect();
        int asymHit = foundAsym ? Doc.EraseRectAt(pickOnAsym.X, pickOnAsym.Y, 10f, 10f) : -1;
        Doc.EndErase();
        Check("双曲线的**渐近线**：橡皮从虚线上擦过去必须算碰到（老写法：这一刀什么也没擦）",
              foundAsym && asymHit >= 1,
              foundAsym
                  ? $"在离曲线 {10f:F0} 像素以上的虚线上擦 → 受影响 {asymHit} 条"
                  : "没找到「离曲线足够远」的渐近线取样点（几何变了？）");

        // --- 5. 一次拖拽扫过 5 条 = 一步撤销 -----------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        for (int k = 0; k < 5; k++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW };
            for (int i = 0; i <= 80; i++) s.AddPoint(cx - 400 + i * 10, cy - 200 + k * 100, 0.9f, i);
            Doc.AddStroke(s);
        }
        var before = Doc.Strokes.ToArray();
        int undo0 = Doc.UndoDepth;

        Doc.BeginEraseRect();
        for (int k = 0; k < 5; k++) Doc.EraseRectAt(cx, cy - 200 + k * 100, halfW, halfH);
        Doc.EndErase();
        int cutCount = 0;
        foreach (var s in Doc.Strokes) if (s.Kind == StrokeKind.Freehand) cutCount++;
        Check("拖拽扫过 5 条 = 一步撤销，每条都断成两截",
              Doc.UndoDepth == undo0 + 1 && Doc.Strokes.Count == 10 && cutCount == 10,
              $"撤销栈 +{Doc.UndoDepth - undo0}，笔画数 {before.Length} → {Doc.Strokes.Count}");

        Doc.Undo();
        bool restored = Doc.Strokes.Count == before.Length;
        for (int i = 0; restored && i < before.Length; i++)
            restored = ReferenceEquals(Doc.Strokes[i], before[i]) && Doc.Strokes[i].Erased.Count == 0;
        Check("撤销后逐条回到拖之前（对象 + 顺序 + 区间清空）", restored,
              $"笔画数 {Doc.Strokes.Count}，逐条比对 {(restored ? "全部一致" : "有出入")}");

        // --- 6. 上屏像素 -------------------------------------------------------
        // --- 6. 擦断之后是两条独立对象（**已知取舍：自交处会叠色**）--------------
        //
        // 用户 2026-09-15 定的语义：擦断了就要"结构上分开、单独算"。
        // 代价是：半透明荧光笔的两截互相穿过时，会**各画一次** → 交叠处变深
        // （这条笔迹没被擦到的地方颜色也变了）。这一条不再判红绿，只把数字量出来，
        // 免得以后有人以为是新 bug——取舍写在 调研-橡皮擦.md 第八节。
        Doc.Clear();
        Doc.ClearHistory();
        // 自交图小，单独摆在位图左上角那一块（800×800 的离屏位图装不下屏幕中心）
        float lx = _virtualX + 500f, ly = _virtualY + 500f;
        var loop = MakeSelfCrossingHighlight(lx, ly, 40f);
        Doc.AddStroke(loop);

        var (offTarget, offCpu) = MakeOffscreen(800, 800);
        var bufA = RenderAndRead(offTarget, offCpu, 800, 800, _virtualX, _virtualY);
        int crossPx = (int)(lx + 161.7f - _virtualX) - 8, crossPy = (int)(ly + 20.9f - _virtualY) - 8;
        int plainPx = (int)(lx + 60f - _virtualX) - 8, plainPy = (int)(ly - _virtualY) - 8;
        int diffBefore = ColorDiff(AvgPatch(bufA, 800, crossPx, crossPy, 17),
                                   AvgPatch(bufA, 800, plainPx, plainPy, 17));

        Doc.BeginEraseRect();
        Doc.EraseRectAt(lx + 240, ly + 140, 20f, 30f);      // 离交点 240 像素以上
        Doc.EndErase();

        var bufB = RenderAndRead(offTarget, offCpu, 800, 800, _virtualX, _virtualY);
        var crossB = AvgPatch(bufB, 800, crossPx, crossPy, 17);
        var plainB = AvgPatch(bufB, 800, plainPx, plainPy, 17);
        int diffAfter = ColorDiff(crossB, plainB);
        offTarget.Dispose();
        offCpu.Dispose();

        Check("半透明荧光笔擦断 → 两条独立对象（代价：交叠处会叠色）",
              Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(loop),
              $"切之前差 {diffBefore}，切之后差 {diffAfter}（已知取舍，不判红绿）；"
              + $"对象数 {Doc.Strokes.Count}，"
              + $"交叠处 ({crossB.r},{crossB.g},{crossB.b}) vs 普通处 ({plainB.r},{plainB.g},{plainB.b})");

        // --- 7. 上屏像素（抓屏拍不到我们这层时会跳过）---------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var onScreen = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW * DpiScale,
        };
        for (int i = 0; i <= 80; i++) onScreen.AddPoint(cx - 400 + i * 10, cy, 0.9f, i);
        Doc.AddStroke(onScreen);
        Doc.InvalidateAll();
        PointerInside = false;                 // 别让自绘光标进像素统计
        SettleFrames(500);

        int inkBefore = ScreenProbe.CountMagenta((int)(cx - 500), (int)(cy - 40), 1000, 80);
        // 先确认抓屏真的能看到我们的墨：锁屏 / 远程会话 / 被别的窗口盖住时，
        // 拍到的是桌面壁纸，那时"框里的墨归零"会假红、"框外的还在"会假绿。
        // 看不见就明确跳过这一条（前面十几条模型判据照样算数）。
        if (inkBefore < 500)
        {
            Console.WriteLine($"  环境：抓屏看不到我们的墨（拍到的大概是桌面或别的窗口）"
                            + $" → SKIP: 上屏那一条跳过（该有一千多品红像素，拍到 {inkBefore}）");
            Console.WriteLine($"  合计：通过 {pass}，失败 {fail}（上屏一条未验）");
            _quit = true;
            return;
        }
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, halfW, halfH);
        Doc.EndErase();
        SettleFrames(500);

        int inBox = ScreenProbe.CountMagenta((int)(cx - halfW + 3), (int)(cy - 30), (int)(halfW * 2 - 6), 60);
        int outside = ScreenProbe.CountMagenta((int)(cx - 500), (int)(cy - 30), 300, 60);
        int inkAfter = ScreenProbe.CountMagenta((int)(cx - 500), (int)(cy - 40), 1000, 80);
        Check("上屏：框里的墨归零、框外的还在",
              inkBefore > 500 && inBox < 20 && outside > 300 && inkAfter > inkBefore * 0.8,
              $"画上 {inkBefore} 像素 → 框里 {inBox}、框外 {outside}，全带 {inkAfter}"
              + $"（应 ≈ {inkBefore} 减去被擦的那一段，不掉远处的墨）");

        // --- 8. 手测台（--eraserlab）的记录链路：写进去的必须是"能算的数" -------------
        // 这条不是在验橡皮，是在验"我事后拿到的数据靠得住"——列数、单位、落盘时机。
        string labCsv = Path.Combine(Path.GetTempPath(), "inklab-smoke.csv");
        string labTxt = Path.Combine(Path.GetTempPath(), "inklab-smoke.txt");
        try { File.Delete(labCsv); File.Delete(labTxt); } catch { /* 删不掉就覆盖 */ }
        var lab = new EraserTelemetry(labCsv, labTxt, NowMs);
        lab.BeginDrag(Tool.PixelEraser, cx - 200, cy, NowMs);
        lab.Step(3, Doc.TotalIntervals, Doc.Strokes.Count, 0.42, cx - 200, cy, NowMs);
        lab.Step(2, Doc.TotalIntervals, Doc.Strokes.Count, 0.31, cx - 150, cy + 10, NowMs + 20);
        lab.Frame(1.5, 8.0);
        lab.Note("撤销", NowMs + 30);
        lab.EndDrag(Doc, Doc.UndoDepth, NowMs + 40);
        lab.Close(Doc, NowMs + 50);
        var labLines = File.Exists(labCsv) ? File.ReadAllLines(labCsv) : Array.Empty<string>();
        var labCols = labLines.Length >= 2 ? labLines[1].Split(',') : Array.Empty<string>();
        Check("手测台：CSV 落盘、列数与数值都对",
              labLines.Length == 2 && labCols.Length == 26 && labCols[1] == "像素橡皮"
              && Math.Abs(double.Parse(labCols[3]) - 40) < 0.6 && labCols[4] == "2",
              labLines.Length >= 2
                ? $"{labLines.Length - 1} 条拖拽、{labCols.Length} 列；工具 {labCols[1]}、"
                  + $"时长 {labCols[3]}ms、采样 {labCols[4]}、擦到 {labCols[8]} 笔"
                : "没有写出 CSV");
        Check("手测台：退出时的汇总也写了", File.Exists(labTxt),
              File.Exists(labTxt) ? Path.GetFileName(labTxt) : "缺汇总文件");

        // --- 9. 擦断之后"两截各自独立"（用户 2026-09-15 定的语义）------------------
        //
        // "橡皮擦中墨迹或者图形，如果断开了结构，选中分开以后单独算"：擦完就是两条**独立对象**，
        // 能分别选中、分别搬。带变换的笔迹拆出来的段要**继承原变换**（点仍是局部坐标）。
        Doc.Clear();
        Doc.ClearHistory();

        var longLine2 = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW,
            // 带一点旋转 + 非等比缩放：拆段时**变换必须原样继承**（点还是局部坐标）
            // 注意变换要在 AddStroke **之前**设好：空间网格按添加时的包围盒索引，
            // 加进去之后再改变换，网格就过期了（擦除会擦不到——这里踩过一次）。
            Transform = Matrix3x2.CreateRotation(0.3f)
                      * Matrix3x2.CreateScale(1.4f, 0.8f)
                      * Matrix3x2.CreateTranslation(cx - 600, cy),
        };
        for (int i = 0; i <= 120; i++) longLine2.AddPoint(60 + i * 10, 200, 0.9f, i);
        Doc.AddStroke(longLine2);
        Doc.InvalidateAll();

        var wp = longLine2.PointAtParam(60);                 // 参数 60 处的画布坐标
        Vector2 canvasMid = Vector2.Transform(wp, longLine2.Transform);
        Doc.BeginEraseRect();
        Doc.EraseRectAt(canvasMid.X, canvasMid.Y, 30f, 60f);
        Doc.EndErase();
        var cut2 = Doc.Strokes.ToArray();
        bool gotTwo = cut2.Length == 2 && !Doc.Strokes.Contains(longLine2);
        bool transformKept = gotTwo
                          && cut2[0].Transform.Equals(longLine2.Transform)
                          && cut2[1].Transform.Equals(longLine2.Transform)
                          && cut2[0].Erased.Count == 0 && cut2[1].Erased.Count == 0;
        Check("带变换的长笔擦断 → 两条独立对象、变换原样继承",
              gotTwo && transformKept,
              gotTwo ? $"对象 {cut2.Length}，变换继承 {(transformKept ? "是" : "否")}，"
                     + $"点数 {cut2[0].Points.Count}/{cut2[1].Points.Count}"
                     : $"对象 {cut2.Length}（应 2）");

        // **每一截能单独选中**（这就是"分开以后单独算"）：框住其中一段，只该选中那一段
        Doc.Selected.Clear();
        var onePiece = cut2[0].WorldBounds.Inflate(10f);
        Doc.ApplyMarquee(onePiece);
        Check("框住其中一段 → 只选中那一段（不再整条一起选）",
              Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], cut2[0]),
              $"选中 {Doc.Selected.Count} 条");

        // SplitErasedSelection（Ctrl+Alt+8）现在只服务"老存档里带区间的笔迹"，本轮该返回 0
        Doc.SelectOnly(new[] { cut2[0] });
        Check("已经拆断的笔迹再按'拆开' → 无事可做",
              Doc.SplitErasedSelection() == 0, "返回 0");

        Doc.Undo();
        Check("撤销擦断：回到原来那一条（同一个对象、变换不变）",
              Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], longLine2)
              && longLine2.Erased.Count == 0,
              $"对象 {Doc.Strokes.Count}，区间 {longLine2.Erased.Count} 段");

        Doc.Redo();
        Check("重做：又变回两条独立对象",
              Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(longLine2),
              $"对象 {Doc.Strokes.Count}");

        // --- 10. 图像：两种橡皮都不碰（原则：图像是"内容"，不是笔画）-----------------
        Doc.Clear();
        Doc.ClearHistory();
        var pic = Doc.AddImage(MakeTestImage(400, 300), cx - 200, cy - 150, 1f);
        Doc.EraseRectAt(cx, cy, 40f, 40f);          // 像素橡皮的调用
        bool picIntact = Doc.Strokes.Contains(pic) && Doc.Strokes.Count == 1;
        Doc.EraseAt(cx, cy, 40f);                   // 整笔橡皮的调用
        picIntact &= Doc.Strokes.Contains(pic) && Doc.Strokes.Count == 1;
        Check("图像：两种橡皮都不碰（要删它用框选 + Delete）", picIntact,
              $"试了像素橡皮和整笔橡皮各一下，对象数 {Doc.Strokes.Count}，"
              + $"图 {(Doc.Strokes.Contains(pic) ? "还在" : "**被删掉了**")}");

        Console.WriteLine($"  合计：通过 {pass}，失败 {fail}");
        Console.WriteLine(fail == 0 ? "PASS" : "FAIL");
        _quit = true;
    }


    /// <summary>
    /// 动态橡皮专项自检（--dynerasertest）。
    ///
    /// 8.3.4 的行为："**面积擦**的尺寸跟着移动速度走"（照隔壁 Inkeys「笔速橡皮」的口径起手）：
    ///   ① 曲线：`factor = clamp(0.6 + 速度(px/ms)×0.6, 0.6, 2.5)`——慢≈0.72、中=1.2、快封顶 2.5；
    ///   ② 后门 `--eraserfixed`（`DynamicEraser=false`）：多快都恒 ×1；
    ///   ③ 真的用合成鼠标拖一遍（**走产品代码那条路**：指针 → `EraseRectAlongPath`）：
    ///      快扫擦掉的墨必须明显多于慢扫（放大真的生效，不是只有那个函数对）；
    ///   ④ 关掉动态后再快扫 → 擦除量回到基准档（不放大）；
    ///   ⑤ **整笔擦不受影响**：它的"大小"是命中半径（碰到哪条删哪条），快扫也不许把半径
    ///      外那条吃掉——半径随速度变 = "点到哪条全看手速"，不可预期。
    ///
    /// 抓屏看不见我们的墨（锁屏 / 远程 / 被别的窗口盖住）时，③④⑤ 明确跳过，曲线判据照样算数。
    /// </summary>
    private void DynEraserTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 动态橡皮自检（面积擦尺寸随速度；整笔擦不受影响）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-38} {detail}");
        }

        // --- ① 曲线（下限 0.7 / 死区到 0.8 / 斜坡 / 封顶 2.5）--------------------
        bool savedDyn = DynamicEraserForTest;
        DynamicEraserForTest = true;
        float fDead1 = EraserTargetFactorForSpeed(0.05f);   // 死区里头
        float fDead2 = EraserTargetFactorForSpeed(0.70f);   // 回差带（0.6~0.8）
        float fMid = EraserTargetFactorForSpeed(1.5f);      // 斜坡中段
        float fFast = EraserTargetFactorForSpeed(5.0f);     // 封顶
        Check("曲线：静止/慢=基准 1.0（死区到 0.8）/ 中 1.5→1.7 / 快封顶 2.5",
              MathF.Abs(fDead1 - 1f) < 0.001f && MathF.Abs(fDead2 - 1f) < 0.001f
              && MathF.Abs(fMid - 1.7f) < 0.02f && MathF.Abs(fFast - 2.5f) < 0.001f,
              $"0.05 → ×{fDead1:F2}、0.70 → ×{fDead2:F2}、1.5 → ×{fMid:F2}、5.0 → ×{fFast:F2}");

        // --- ② 后门：--eraserfixed 关掉动态 → 系数恒 1 -------------------------
        DynamicEraserForTest = false;
        float fFixed = DynamicEraserFactorForTest(5.0f);
        Check("后门（--eraserfixed）：多快都恒 ×1.0",
              MathF.Abs(fFixed - 1f) < 0.001f, $"5.0px/ms → ×{fFixed:F2}");
        DynamicEraserForTest = true;

        // --- ②b 慢速/常规速度不忽大忽小（用户两轮反馈）---------------------------
        // 手本来就是抖的：速度在 0.05~0.55 px/ms 之间来回（**全落在死区 0.8 以下**）。
        // 合格的样子：系数**单调**朝下限走、绝不回升，而且每步只挪一丁点。
        ResetDynamicEraserForTest();
        float prevF = 1f, worstStep = 0f, lastF = 1f;
        bool rose = false;
        for (int i = 0; i < 40; i++)
        {
            float v = (i % 3 == 0) ? 0.05f : (i % 3 == 1 ? 0.55f : 0.30f);
            lastF = DynamicEraserAdvanceForTest(v, 8.0);
            if (lastF > prevF + 0.0005f) rose = true;
            worstStep = MathF.Max(worstStep, MathF.Abs(lastF - prevF));
            prevF = lastF;
        }
        Check("点击后按住不动/慢速：尺寸停在基准 1.0、一点不缩（8.3.8）",
              !rose && MathF.Abs(lastF - 1f) < 0.001f && worstStep <= 0.001f,
              $"40 步后 ×{lastF:F3}（基准 1.00），单步最大变化 {worstStep:F3}（按住不动时手指的微抖也算速度，0 变化）");

        // --- ②c 速度窗口：逐次估法很抖，窗口算出来要稳 ---------------------------
        // 交替喂 (4px, 3ms)=1.33 与 (2px, 9ms)=0.22——真值始终 0.5 px/ms。
        // 窗口（100ms / 40px 先到先算）出来的速度必须**几乎不动**。
        ResetDynamicEraserForTest();
        float wMin = float.MaxValue, wMax = 0f, wLast = 0f;
        for (int i = 0; i < 90; i++)
        {
            bool big = (i % 2 == 0);
            wLast = DynamicEraserFeedForTest(big ? 4f : 2f, big ? 3.0 : 9.0);
            if (i >= 24)                                  // 头一个窗口攒满之前不算
            {
                wMin = MathF.Min(wMin, wLast);
                wMax = MathF.Max(wMax, wLast);
            }
        }
        Check("速度窗口：逐次 dist÷dt 在 1.33/0.22 之间跳，窗口稳在真值 0.5",
              MathF.Abs(wMax - wMin) < 0.05f && MathF.Abs(wLast - 0.5f) < 0.06f,
              $"窗口速度 {wMin:F3}~{wMax:F3}（真值 0.50）、最后 {wLast:F3}；"
              + $"逐次估法会跳在 {4f / 3f:F2} 与 {2f / 9f:F2} 之间");

        // --- ②d 真拖那三条的"逻辑版"（不依赖鼠标/抓屏，跑哪儿都验）--------------
        ResetDynamicEraserForTest();
        float slowFactor = 1f, fastFactor = 1f;
        for (int i = 0; i < 40; i++)                       // 慢扫：(20px, 50ms) → 0.4 px/ms，跑 2 秒
        {
            float v = DynamicEraserFeedForTest(20f, 50.0);
            slowFactor = DynamicEraserAdvanceForTest(v, 50.0);
        }
        ResetDynamicEraserForTest();
        for (int i = 0; i < 20; i++)                       // 快扫：(43px, 8ms) → 5.4 px/ms
        {
            float v = DynamicEraserFeedForTest(43f, 8.0);
            fastFactor = DynamicEraserAdvanceForTest(v, 8.0);
        }
        Check("慢扫停在 ×1.0（基准）、快扫明显更大（真拖那三条的逻辑版）",
              MathF.Abs(slowFactor - 1f) < 0.001f && fastFactor > slowFactor * 1.5f,
              $"慢扫 ×{slowFactor:F3}（应 1.00 = 基准）、快扫 ×{fastFactor:F3}");

        // --- ②e 停住 → 缓释（用户报的"停住不回落、再轻动一下猛变小"）----------------
        ResetDynamicEraserForTest();
        PixelEraseDragging = true;                     // 模拟"按住擦"
        float peak = 1f;
        for (int i = 0; i < 30; i++)                   // 快扫：(43px, 8ms) → 5.4 px/ms
        {
            float v = DynamicEraserFeedForTest(43f, 8.0);
            peak = DynamicEraserAdvanceForTest(v, 8.0);
        }
        float at100 = peak, at600 = peak, decayF = peak;
        bool decayRose = false;
        for (int k = 1; k <= 36; k++)                  // 停住 1.8 秒（每次推进 50ms）
        {
            float f = DynamicEraserIdleForTest(50f);
            if (f > decayF + 0.0005f) decayRose = true;   // 空闲里**不许涨**
            decayF = f;
            if (k == 2) at100 = f;                     // 100ms：保持期内，该纹丝不动
            if (k == 12) at600 = f;                    // 600ms：该开始收了
        }
        Check("停住：先保持、之后顺着回落、1.8s 回到基准（不回升）",
              MathF.Abs(at100 - peak) < 0.002f && at600 < peak - 0.1f
              && !decayRose && MathF.Abs(decayF - 1f) < 0.08f,
              $"峰值 ×{peak:F2} → 100ms ×{at100:F2}（该不动）、600ms ×{at600:F2}、1.8s ×{decayF:F2}（该 ≈1.00）");

        // 回落途中"轻动一下"：不许向上跳（用户原话："接着鼠标稍微再动一下…"）
        float before = decayF;
        float lightV = DynamicEraserFeedForTest(3f, 40.0);   // 轻动：3px / 40ms ≈ 0.075 px/ms
        float after = DynamicEraserAdvanceForTest(lightV, 40.0);
        Check("回落途中轻动一下：不许向上跳变",
              after <= before + 0.002f,
              $"轻动前 ×{before:F3} → 轻动后 ×{after:F3}");
        PixelEraseDragging = false;

        // --- ③ 看的框 = 擦的范围（同一份尺寸：拖动中跟速度、悬停回基准）----------
        DynamicEraserFactorForTest(5f);                 // → ×2.5
        PixelEraseDragging = true;
        float growW = PixelEraserCursorHalfWidthPx, growH = PixelEraserCursorHalfHeightPx;
        bool grows = MathF.Abs(growW - PixelEraserHalfWidthPx * 2.5f) < 0.01f
                  && MathF.Abs(growH - PixelEraserHalfHeightPx * 2.5f) < 0.01f;
        PixelEraseDragging = false;
        bool idleBase = MathF.Abs(PixelEraserCursorHalfWidthPx - PixelEraserHalfWidthPx) < 0.01f
                     && MathF.Abs(PixelEraserCursorHalfHeightPx - PixelEraserHalfHeightPx) < 0.01f;
        Check("看的框 = 擦的范围：拖动中跟速度 ×2.5、悬停回基准",
              grows && idleBase,
              $"拖动中半宽 {growW:F0}px（基准 {PixelEraserHalfWidthPx:F0}、×2.5 应为 "
              + $"{PixelEraserHalfWidthPx * 2.5f:F0}），悬停 {PixelEraserCursorHalfWidthPx:F0}px");

        // --- ③④⑤ 真机拖动（合成鼠标，走产品的指针路径）------------------------
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        float savedW = PixelEraserWidthLogical;
        var savedTool = Tool;
        // 用小块：默认那 93×150 逻辑像素（物理更大）会盖满整张测试图，量不出比例。
        PixelEraserWidthLogical = 12f;
        float hh0 = PixelEraserHalfHeightPx;          // 基准半高（物理像素）
        float spacing = hh0 * 1.6f;                   // 行距
        const int lines = 10;
        float blockH = (lines - 1) * spacing;
        float lineW = spacing * 1.5f;                 // 行够粗 → 竖着连成一片
        float sweepTop = cy - blockH * 0.5f - hh0 * 6f;
        float sweepBot = cy + blockH * 0.5f + hh0 * 6f;
        int boxX = (int)(cx - 170), boxW = 340;
        int boxY = (int)(cy - blockH * 0.5f - hh0 * 9f);
        int boxH = (int)(blockH + hh0 * 18f);

        void PaintBlock()
        {
            Doc.Clear();
            Doc.ClearHistory();
            for (int k = 0; k < lines; k++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = lineW };
                float y = cy - blockH * 0.5f + k * spacing;
                for (int i = 0; i <= 40; i++) s.AddPoint(cx - 240 + i * 12, y, 0.9f, i);
                Doc.AddStroke(s);
            }
            Doc.InvalidateAll();
            SettleFrames(450);
        }

        // 数墨之前把指针挪出统计框（落点反馈那个矩形也是我们画的，别让它进品红计数）。
        int Ink()
        {
            SendMouse((int)(cx + 320), boxY - 120, 0);
            SettleFrames(250);
            return ScreenProbe.CountMagenta(boxX, boxY, boxW, boxH);
        }

        void Sweep(bool fast)
        {
            SendMouse((int)cx, (int)sweepTop, 0);
            SettleFrames(80);
            SendMouse((int)cx, (int)sweepTop, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(fast ? 30 : 80);
            int n = fast ? 12 : 26;
            for (int i = 1; i <= n; i++)
            {
                SendMouse((int)cx, (int)(sweepTop + (sweepBot - sweepTop) * i / n), 0);
                // 慢扫：每步等一等（速度才真的低）。快扫也留 8ms——不是"瞬间一步"而是
                // "快扫"：新参数按时间常数平滑，尺寸要几帧才长起来（那才是真实手感）。
                SettleFrames(fast ? 8 : 50);
            }
            SendMouse((int)cx, (int)sweepBot, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(450);
        }

        bool canDrag = !SkipIfNoSyntheticInput("合成鼠标拖动那三条");
        if (canDrag)
        {
            Tool = Tool.PixelEraser;
            PaintBlock();
            int ink0 = Ink();
            if (ink0 < 500)
            {
                Console.WriteLine($"  环境：抓屏看不到我们的墨（拍到 {ink0} 像素）"
                                + " → SKIP: ③④⑤ 跳过");
            }
            else
            {
                Sweep(fast: false);
                int slowErased = ink0 - Ink();
                Check("慢扫（基准档）真的擦掉一片", slowErased > 300,
                      $"擦掉 {slowErased} 像素（先画了 {ink0}）");

                PaintBlock();
                Sweep(fast: true);
                int fastErased = ink0 - Ink();
                Check("快扫擦掉的明显多于慢扫（速度→尺寸 真的生效）",
                      fastErased > slowErased * 1.2f,
                      $"快扫 {fastErased} vs 慢扫 {slowErased} 像素"
                      + $"（×{fastErased / (float)Math.Max(1, slowErased):F2}）");

                PaintBlock();
                DynamicEraserForTest = false;
                Sweep(fast: true);
                DynamicEraserForTest = true;
                int fixedErased = ink0 - Ink();
                Check("后门：关掉动态后快扫回到基准档",
                      fixedErased < fastErased * 0.85f && fixedErased > slowErased * 0.6f,
                      $"关掉动态快扫 {fixedErased}（动态快扫 {fastErased}、慢扫 {slowErased}）");

                // --- ⑤ 整笔擦不受速度影响 ------------------------------------
                Doc.Clear();
                Doc.ClearHistory();
                Tool = Tool.Eraser;
                float r = EraserRadius;
                float dFar = r * 2f + 10f;            // 动态若误伤到这里，×2.5 的半径会够着它
                var near = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 8f };
                for (int i = 0; i <= 40; i++) near.AddPoint(cx - 240 + i * 12, cy, 0.9f, i);
                var far = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 8f };
                for (int i = 0; i <= 40; i++) far.AddPoint(cx - dFar, cy - 150 + i * 7.5f, 0.9f, i);
                Doc.AddStroke(near);
                Doc.AddStroke(far);
                Doc.InvalidateAll();
                SettleFrames(350);

                SendMouse((int)cx, (int)(cy - 150), 0);
                SettleFrames(60);
                SendMouse((int)cx, (int)(cy - 150), Native.MOUSEEVENTF_LEFTDOWN);
                SendMouse((int)cx, (int)(cy + 150), 0);   // 快扫：一步跨 300 像素
                SendMouse((int)cx, (int)(cy + 150), Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(350);

                Check("整笔擦：快扫也不许碰半径外那条（半径不随速度变）",
                      Doc.Strokes.Contains(far) && !Doc.Strokes.Contains(near),
                      $"半径 {r:F0}px：近的（过路径）"
                      + $"{(Doc.Strokes.Contains(near) ? "还在 ✗" : "被擦 ✓")}，"
                      + $"远的（{dFar:F0}px 外）{(Doc.Strokes.Contains(far) ? "没动 ✓" : "**被吃了 ✗**")}");

                // --- ⑥ 严丝合缝：框有多大，墨就擦到哪儿（系数钉成 2.5，不受手速影响）--
                // 判据用"墨的可见边缘到框边的缝"：0 附近 = 正好触到框；负 = 擦过头了；
                // 明显正 = 框和擦对不上（"看见没擦到、其实擦掉了"或反过来）。
                Doc.Clear();
                Doc.ClearHistory();
                Tool = Tool.PixelEraser;
                const float penW2 = 16f;
                float reach2 = penW2 * 0.5f;              // 半笔宽：墨那条"身体"的半径
                float fPin = 2.5f;
                EraserFactorOverrideForTest = fPin;
                var oneLine = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = penW2 };
                for (int i = 0; i <= 60; i++) oneLine.AddPoint(cx - 300 + i * 10, cy, 0.9f, i);
                Doc.AddStroke(oneLine);
                Doc.InvalidateAll();
                SettleFrames(350);

                SendMouse((int)cx, (int)(cy - 200), 0);
                SettleFrames(80);
                SendMouse((int)cx, (int)(cy - 200), Native.MOUSEEVENTF_LEFTDOWN);
                SettleFrames(60);
                SendMouse((int)cx, (int)(cy + 200), 0);   // 扫过这条线（框按 ×2.5 算）
                SendMouse((int)cx, (int)(cy + 200), Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(350);
                EraserFactorOverrideForTest = 0f;

                float halfFrame = PixelEraserHalfWidthPx * fPin;
                // 左段的右端 = 所有段里**最小**的 MaxX；右段的左端 = 所有段里**最大**的 MinX。
                float leftEnd2 = float.MaxValue, rightStart2 = float.MinValue;
                foreach (var s in Doc.Strokes)
                {
                    leftEnd2 = MathF.Min(leftEnd2, s.Bounds.MaxX);
                    rightStart2 = MathF.Max(rightStart2, s.Bounds.MinX);
                }
                float gapL = (cx - halfFrame) - (leftEnd2 + reach2);   // 墨够到框边了吗
                float gapR = (rightStart2 - reach2) - (cx + halfFrame);
                Check("严丝合缝：框有多大，墨就擦到框边（缝 0~4 像素，不擦过头）",
                      Doc.Strokes.Count == 2 && !Doc.Strokes.Contains(oneLine)
                      && gapL >= -1f && gapL <= 4f && gapR >= -1f && gapR <= 4f,
                      $"框半宽 {halfFrame:F1}px（基准 {PixelEraserHalfWidthPx:F0}×2.5）→ "
                      + $"左缝 {gapL:F1}px / 右缝 {gapR:F1}px（0 = 正好触到，负 = 擦过头）"
                      + $"，对象 {Doc.Strokes.Count} 条");
            }
        }

        PixelEraserWidthLogical = savedW;
        Tool = savedTool;
        DynamicEraserForTest = savedDyn;
        Doc.Clear();
        Console.WriteLine($"  合计：通过 {pass}，失败 {fail}");
        Console.WriteLine(fail == 0 ? "PASS" : "FAIL");
        _quit = true;
    }

}
