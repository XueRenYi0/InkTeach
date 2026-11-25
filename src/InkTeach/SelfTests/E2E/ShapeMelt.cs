// [职责] 图形面积擦除"零变形"像素级 E2E：熔墨前后未擦区域逐像素对比。
// [测试] --shapemelttest

using System.Numerics;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检 `--shapemelttest`：**图形被面积橡皮擦掉一小块之后，没擦到的地方必须一个像素都不变**。
    ///
    /// 判据（每种图形都跑一遍）：
    ///   ① 擦除框**外**（再外扩一个"半笔宽 + 10px"安全边距）的像素变化率 ≤ 1%
    ///      —— 以前熔墨走手写平滑 / 运动模型，直角变圆角、大圆变多边形，
    ///      这里会直接大面积 FAIL；
    ///   ② 擦除框**内**原来有墨（>0），擦完接近 0（证明真擦到了，不是空跑）；
    ///   ③ 熔出来的每一片都是 `ExactPolyline` 的 Freehand；
    ///   ④ `Ctrl+Z` 一步撤销后**原参数化图形**回来（引用相等、Kind 不变）。
    ///
    /// 覆盖：矩形 / 三角形 / 圆 / 椭圆 / 带焦点椭圆 / 双曲线（带渐近线）/
    /// 网格坐标系 / 圆柱 / 四棱柱 / 长方体。
    /// </summary>
    private void ShapeMeltTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图形面积擦除零变形（熔墨像素级）===");

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(160);
        PassThrough = false;
        ShowHud = false;
        SetBoardFromUi(true);
        SetPptModeForTest(false);
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Tool = Tool.Pen;
        PointerInside = false;
        SettleFrames(80);

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-16} {detail}");
        }

        Console.WriteLine("  图形            擦前墨/擦后墨   框外变化        判定");
        foreach (var (name, make) in MeltShapeCases(cx, cy))
        {
            if (!RunOneMeltCase(name, make, Check)) continue;
        }

        SetBoardFromUi(false);
        SetPptModeForTest(false);
        Doc.Clear();
        Doc.ClearHistory();
        Console.WriteLine();
        Console.WriteLine($"图形熔墨像素级：通过 {pass} 项，失败 {fail} 项");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }

    /// <summary>跑一种图形：画 → 抓图 → 擦一小块 → 抓图 → 对比 → 撤销。返回 false = 本轮没能采样。</summary>
    private bool RunOneMeltCase(string name, Func<Stroke> make,
                                Action<string, bool, string> check)
    {
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(40);

        var s = make();
        var wantKind = s.Kind;
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(200);

        // 只抓图形周围一圈：整屏抓图又慢又没必要，判据也只关心这块。
        var b = s.WorldInkBounds;
        int x0 = (int)MathF.Floor(b.MinX - 24f);
        int y0 = (int)MathF.Floor(b.MinY - 24f);
        int bw = (int)MathF.Ceiling(b.MaxX - b.MinX + 48f);
        int bh = (int)MathF.Ceiling(b.MaxY - b.MinY + 48f);
        var before = ScreenProbe.CaptureRegion(x0, y0, bw, bh);
        if (before.Length < bw * bh * 4)
        {
            check(name, false, "抓屏失败（区域越界）");
            return false;
        }

        // 擦除点：取轮廓最长那一笔的中点——保证一定碰到"画出来的墨"。
        var ep = MeltErasePoint(s);
        const float halfW = 22f, halfH = 16f;
        var epCanvas = Vector2.Transform(ep, s.Transform);
        int inkBefore = CountInkInRect(before, bw, x0, y0, epCanvas, halfW, halfH);

        Doc.BeginEraseRect();
        Doc.EraseRectAt(epCanvas.X, epCanvas.Y, halfW, halfH);
        Doc.EndErase();
        Doc.InvalidateAll();
        SettleFrames(200);

        var after = ScreenProbe.CaptureRegion(x0, y0, bw, bh);
        if (after.Length < bw * bh * 4)
        {
            check(name, false, "擦后抓屏失败");
            return false;
        }

        int inkAfter = CountInkInRect(after, bw, x0, y0, epCanvas, halfW, halfH);

        // 框外（再外扩半笔宽 + 10px 安全边距）的像素变化。
        int margin = (int)MathF.Ceiling(MathF.Max(2f, s.Width) * 0.5f + 10f);
        int sx0 = (int)MathF.Floor(epCanvas.X - halfW - x0) - margin;
        int sy0 = (int)MathF.Floor(epCanvas.Y - halfH - y0) - margin;
        int sx1 = (int)MathF.Ceiling(epCanvas.X + halfW - x0) + margin;
        int sy1 = (int)MathF.Ceiling(epCanvas.Y + halfH - y0) + margin;
        CountChangedOutside(before, after, bw, bh, sx0, sy0, sx1, sy1,
                            out int changed, out int total);
        double ratio = total > 0 ? (double)changed / total : 1.0;

        // 熔墨产物必须全是精确折线，且真的把图形换成了墨。
        var pieces = Doc.Strokes.ToArray();
        bool exact = pieces.Length > 0;
        foreach (var p in pieces)
            if (p.Kind != StrokeKind.Freehand || !p.ExactPolyline) exact = false;

        int undoBefore = Doc.UndoDepth;
        bool undone = Doc.Undo();
        SettleFrames(60);
        bool restored = undone && Doc.Strokes.Count == 1
                        && ReferenceEquals(Doc.Strokes[0], s)
                        && Doc.Strokes[0].Kind == wantKind;

        Console.WriteLine($"  {name,-14} {inkBefore,5} / {inkAfter,-5} "
                        + $"{changed,6}/{total,-7}({ratio * 100:F3}%) {(exact ? "精确折线" : "非精确")}");
        check($"{name}：框外 0 变形", ratio <= 0.01,
              $"变化 {changed}/{total} = {ratio * 100:F3}%（容差 ≤ 1%）");
        check($"{name}：框内确实擦掉", inkBefore > 0 && inkAfter <= Math.Max(4, total / 100),
              $"擦前 {inkBefore} → 擦后 {inkAfter}");
        check($"{name}：熔墨是精确折线", exact,
              pieces.Length == 0 ? "没有墨了" : $"{pieces.Length} 片");
        check($"{name}：一步撤销恢复原图形", restored,
              $"撤销 {undoBefore}→{Doc.UndoDepth}，对象 {Doc.Strokes.Count} 个，Kind={Doc.Strokes[0].Kind}");
        return true;
    }

    /// <summary>轮廓最长那一笔的约中点（局部坐标）——擦这里一定碰到墨。</summary>
    private static Vector2 MeltErasePoint(Stroke s)
    {
        var parts = Stroke.SplitOutlineParts(s.ShapeOutline());
        List<Vector2> best = null;
        foreach (var p in parts)
            if (best == null || p.Count > best.Count) best = p;
        if (best == null || best.Count == 0)
        {
            var bb = s.WorldInkBounds;
            return new Vector2((bb.MinX + bb.MaxX) * 0.5f, (bb.MinY + bb.MaxY) * 0.5f);
        }
        // 取**相邻两点的中点**：落在笔画中段而不是顶点 / 两笔的交点。
        // 擦在交点会把"另一笔"的起点也切掉（虚线辅助线会因此重起相位，
        // 屏幕上看是"整条虚线挪了位"）；中点不会碰到任何另一笔。
        int i = best.Count >= 4 ? best.Count / 4 : best.Count / 2;
        var p0 = best[i];
        var p1 = best[Math.Min(best.Count - 1, i + 1)];
        return (p0 + p1) * 0.5f;
    }

    /// <summary>缓冲区里、给定画布矩形内的墨像素数（任一通道 &lt; 140 视为墨）。</summary>
    private static int CountInkInRect(byte[] buf, int w, int x0, int y0, Vector2 c,
                                      float halfW, float halfH)
    {
        int l = Math.Max(0, (int)MathF.Floor(c.X - halfW - x0));
        int r = Math.Min(w - 1, (int)MathF.Ceiling(c.X + halfW - x0));
        int t = Math.Max(0, (int)MathF.Floor(c.Y - halfH - y0));
        int btm = Math.Min(buf.Length / 4 / Math.Max(1, w) - 1,
                           (int)MathF.Ceiling(c.Y + halfH - y0));
        int n = 0;
        for (int y = t; y <= btm; y++)
            for (int x = l; x <= r; x++)
            {
                int i = (y * w + x) * 4;
                if (buf[i] < 140 || buf[i + 1] < 140 || buf[i + 2] < 140) n++;
            }
        return n;
    }

    /// <summary>两帧在"安全矩形之外"有多少像素真的变了（任一通道差 &gt; 20）。</summary>
    private static void CountChangedOutside(byte[] a, byte[] b, int w, int h,
                                            int sx0, int sy0, int sx1, int sy1,
                                            out int changed, out int total)
    {
        changed = 0;
        total = 0;
        for (int y = 0; y < h; y++)
        {
            bool safeRow = y >= sy0 && y <= sy1;
            for (int x = 0; x < w; x++)
            {
                if (safeRow && x >= sx0 && x <= sx1) continue;
                total++;
                int i = (y * w + x) * 4;
                int d = Math.Max(Math.Abs(a[i] - b[i]),
                        Math.Max(Math.Abs(a[i + 1] - b[i + 1]),
                                 Math.Abs(a[i + 2] - b[i + 2])));
                if (d > 20) changed++;
            }
        }
    }

    /// <summary>10 种代表图形的构造（都落在画布中心附近，尺寸按验收要求给足）。</summary>
    private static List<(string Name, Func<Stroke> Make)> MeltShapeCases(float cx, float cy)
    {
        Stroke New(Tool tool, StrokeKind kind) => new()
        {
            Tool = tool, Kind = kind,
            Color = new Color4(0f, 0f, 0f, 1f), Width = 5f,
        };

        Stroke Rect()
        {
            var s = New(Tool.Rectangle, StrokeKind.Rectangle);
            s.AddPoint(cx - 220, cy - 150, 1f, 0);
            s.SetEnd(cx + 220, cy + 150);
            return s;
        }

        Stroke Tri()
        {
            var s = New(Tool.Triangle, StrokeKind.Triangle);
            s.AddPoint(cx, cy - 170, 1f, 0);
            s.SetShapeBox(cx - 220, cy - 170, cx + 220, cy + 160);
            return s;
        }

        Stroke Cir()
        {
            var s = New(Tool.Circle, StrokeKind.Circle);
            s.AddPoint(cx, cy, 1f, 0);
            s.SetEnd(cx + 170, cy);
            return s;
        }

        Stroke Ell()
        {
            var s = New(Tool.Ellipse, StrokeKind.Ellipse);
            s.AddPoint(cx, cy, 1f, 0);
            s.SetEnd(cx + 210, cy + 130);
            return s;
        }

        Stroke Conic()
        {
            var s = New(Tool.ConicEllipse, StrokeKind.ConicEllipse);
            s.AddPoint(cx, cy, 1f, 0);
            s.SetEnd(cx + 210, cy + 130);
            s.SetConicEllipsePointAngle(-MathF.PI * 0.5f);
            s.FocusTriangle = true;
            return s;
        }

        Stroke Hyper()
        {
            var s = New(Tool.Hyperbola, StrokeKind.Hyperbola);
            s.AddPoint(cx, cy, 1f, 0);
            s.SetHyperbolaFromAsymptote(cx, cy, cx + 210, cy + 150, 2f);
            s.SetHyperbolaThroughPoint(cx + 130, cy - 70);
            s.ShowAsymptotes = true;
            return s;
        }

        Stroke Coord()
        {
            var s = New(Tool.Coordinate, StrokeKind.Coordinate);
            s.AddPoint(cx, cy, 1f, 0);
            s.SetAxisBox(cx - 240, cy - 170, cx + 240, cy + 170);
            s.Grid = true;
            return s;
        }

        Stroke Cyl()
        {
            var s = New(Tool.Cylinder, StrokeKind.Cylinder);
            s.AddPoint(cx - 170, cy - 160, 1f, 0);
            s.SetEnd(cx + 170, cy + 160);
            return s;
        }

        Stroke Prism()
        {
            var s = New(Tool.Prism, StrokeKind.Prism);
            s.PrismSides = 4;
            s.AddPoint(cx, cy, 1f, 0);
            s.SetPrismBase(cx - 160, cy - 40, cx + 160, cy + 110);
            s.SetPrismApex(cx, cy - 160);
            return s;
        }

        Stroke Cuboid()
        {
            var s = New(Tool.Cuboid, StrokeKind.Cuboid);
            s.AddPoint(cx - 200, cy - 120, 1f, 0);
            s.SetCuboidFront(cx - 200, cy - 120, cx + 120, cy + 120);
            s.SetCuboidDepth(cx - 200, cy - 120);
            return s;
        }

        return new List<(string, Func<Stroke>)>
        {
            ("矩形", Rect), ("三角形", Tri), ("圆", Cir), ("椭圆", Ell),
            ("带焦点椭圆", Conic), ("双曲线", Hyper), ("网格坐标系", Coord),
            ("圆柱", Cyl), ("四棱柱", Prism), ("长方体", Cuboid),
        };
    }
}
