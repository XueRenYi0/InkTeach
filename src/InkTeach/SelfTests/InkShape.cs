// 本文件由 App.cs 拆出（2026-10-07）：InkShape 这一组。
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

    private void SelfCrossTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 自交叠色自检（一笔自己穿过自己，颜色不能变深）===");

        bool boardBefore = BoardOn;
        var boardColorBefore = BoardColor;
        BoardOn = true;
        BoardColor = new Color4(0.98f, 0.98f, 0.98f, 1f);
        ViewOffsetY = 0f;
        foreach (var win in _windows) { win.ViewOffsetX = 0f; win.ViewOffsetY = 0f; }
        Doc.Clear();
        Doc.ClearHistory();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        float w = 40f;                 // 物理像素
        float cx = _virtualX + 500f, cy = _virtualY + 500f;

        // 造一条"自己穿过自己"的笔迹：先往右，再绕回来从第一段上方穿过去。
        // **真正的交点在相对 (161.7, 20.9)**（两段中心线联立解出来的，推导见
        // EraseSelfCrossProbe），不是 (200, 0)。
        Stroke MakeLoop(float dx)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = HighlighterCurrent, Width = w,
            };
            var pts = new List<Vector2>
            {
                new(cx + dx, cy), new(cx + dx + 120, cy), new(cx + dx + 240, cy + 60),
                new(cx + dx + 240, cy + 220), new(cx + dx + 60, cy + 260),
                new(cx + dx - 20, cy + 120), new(cx + dx + 200, cy),   // ← 穿过第一段
                new(cx + dx + 330, cy - 150),
            };
            // 点之间插值，模拟真实采样密度
            for (int i = 0; i + 1 < pts.Count; i++)
                for (int k = 0; k < 20; k++)
                {
                    float t = k / 20f;
                    s.AddPoint(pts[i].X + (pts[i + 1].X - pts[i].X) * t,
                               pts[i].Y + (pts[i + 1].Y - pts[i].Y) * t, 0.5f, i * 20 + k);
                }
            s.AddPoint(pts[^1].X, pts[^1].Y, 0.5f, 999);
            return s;
        }

        Doc.AddStroke(MakeLoop(0f));
        Doc.InvalidateAll();
        SettleFrames(700);

        (int r, int g, int b) Avg(byte[] buf, int ww)
        {
            if (buf.Length < ww * ww * 4) return (0, 0, 0);
            long r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i < buf.Length; i += 4) { b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; n++; }
            return ((int)(r / n), (int)(g / n), (int)(b / n));
        }
        // 交叠区（两段墨重叠）与普通区（只有一段墨）各取一个小方块的平均色
        //
        // 探针位置 2026-09-15 修过一次：以前探的是相对 (200,0)，离真交点 40 像素——
        // 那儿只有一股墨，两边颜色当然一样，于是这条判据**永远是绿的、什么都没验**。
        void Probe(string label, float dx)
        {
            var co = Avg(ScreenProbe.CaptureRegion(
                (int)(cx + dx + 161.7f) - 8, (int)(cy + 20.9f) - 8, 17, 17), 17);
            var cs = Avg(ScreenProbe.CaptureRegion((int)(cx + dx + 60) - 8, (int)cy - 8, 17, 17), 17);
            int diff = Math.Abs(co.r - cs.r) + Math.Abs(co.g - cs.g) + Math.Abs(co.b - cs.b);
            Check($"自交处没有叠色（{label}）", diff <= 6,
                  $"交叠处 ({co.r},{co.g},{co.b}) vs 普通处 ({cs.r},{cs.g},{cs.b})，差 {diff}");
        }

        // 先确认"能看见墨"：抓屏在锁屏 / 远程会话 / 被别的窗口盖住时拿到的根本不是
        // 我们这一层，那时任何颜色比对都是假的（实测踩到：两处都拍到桌面壁纸，
        // 于是"差 0 = 通过"）。看不见就明确跳过，不能给一个假绿。
        var sanity = Avg(ScreenProbe.CaptureRegion((int)(cx + 60) - 8, (int)cy - 8, 17, 17), 17);
        // 荧光笔是黄色（r > g > b，都很亮）。拿"是不是黄的"当判据，比"和板色不一样"
        // 硬得多：桌面壁纸、任何别的窗口都能和板色不一样，但不该是黄的。
        bool canSeeInk = sanity.r > 180 && sanity.r > sanity.g && sanity.g > sanity.b;
        if (!canSeeInk)
        {
            Console.WriteLine($"  环境：抓屏看不到我们的墨（拍到的大概是桌面或别的窗口）"
                            + $" → SKIP: 自交叠色那一条跳过（这里应该是一块黄荧光笔，"
                            + $"实际拍到 ({sanity.r},{sanity.g},{sanity.b})）");
            BoardColor = boardColorBefore;
            BoardOn = boardBefore;
            _quit = true;
            return;
        }
        Probe("一笔自交", 0f);

        string shot = Path.Combine("reports", "自交叠色.bmp");
        ScreenProbe.SaveBmp(shot, (int)(cx - 120), (int)(cy - 260), 700, 700);
        Console.WriteLine($"  （已导出 {shot}：一笔自交，看交叉处有没有更深）");
        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();

        BoardOn = boardBefore;
        BoardColor = boardColorBefore;
        Doc.Clear();
        Doc.InvalidateAll();
        _quit = true;
    }



    /// <summary>
    /// **找洞自检**：笔是不透明的，所以"墨里出现深色"只可能是**墨真的缺了一块**
    /// （露出后面的背景），不是叠色。缺口的成因是轮廓自交时**绕向翻转**，
    /// 在 Winding 规则下正负抵消 → 那一块变成"外面"。
    ///
    /// 判据不靠肉眼：把笔画所在的区域截下来，从边界做一次**背景的连通填充**，
    /// 凡是"填不到、又被墨围住"的背景像素就是洞。这比"看谁变深"硬得多，
    /// 也不会把正常凹处（比如 V 字两臂之间）误判成洞。
    ///
    /// 造四种最容易出洞的笔迹：急折（内角 10°/20°）、自交（λ）、
    /// 以及"长斜线 + 顶端折返 + 穿过去"这种一笔画出来的交叉。
    /// </summary>
    private void HoleTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 找洞自检（不透明笔迹里不能露出背景）===");

        bool boardBefore = BoardOn;
        var boardColorBefore = BoardColor;
        BoardOn = true;
        BoardColor = new Color4(0.98f, 0.98f, 0.98f, 1f);
        ViewOffsetY = 0f;
        foreach (var win in _windows) { win.ViewOffsetX = 0f; win.ViewOffsetY = 0f; }
        Doc.Clear();
        Doc.ClearHistory();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        float w = 48f;                       // 粗笔：问题在粗笔上才显形
        float arm = 220f;
        var ink = new Color4(0.95f, 0.18f, 0.18f, 1f);   // 不透明红

        // 一笔画出来的形状；返回包围盒（画布坐标）
        RectF Make(string kind, float cx, float cy)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = ink, Width = w,
            };
            var pts = new List<Vector2>();
            if (kind == "折170")
            {
                float rad = 170f * MathF.PI / 180f;
                var d = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + arm * i / 30f, cy));
                for (int i = 1; i <= 30; i++)
                    pts.Add(new(cx + arm + d.X * arm * i / 30f, cy + d.Y * arm * i / 30f));
            }
            else if (kind == "折160")
            {
                float rad = 160f * MathF.PI / 180f;
                var d = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + arm * i / 30f, cy));
                for (int i = 1; i <= 30; i++)
                    pts.Add(new(cx + arm + d.X * arm * i / 30f, cy + d.Y * arm * i / 30f));
            }
            else if (kind == "自交λ")
            {
                // 上、折、再穿回来：经典的"λ"（自己的尾巴穿过自己的身子）
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + i * 6f, cy + 180f - i * 6f));
                for (int i = 1; i <= 30; i++) pts.Add(new(cx + 180f + i * 6f, cy + i * 6f));
            }
            else // "∧ 加穿线"：用户的形状（长斜线 + 顶端折返 + 一笔穿过去）
            {
                for (int i = 0; i <= 30; i++) pts.Add(new(cx + i * 7f, cy + 200f - i * 7f));
                for (int i = 1; i <= 20; i++) pts.Add(new(cx + 210f + i * 3f, cy - 10f + i * 9f));
                for (int i = 1; i <= 40; i++) pts.Add(new(cx + 270f - i * 8f, cy + 170f - i * 9f));
            }
            if (kind == "原路折返")
            {
                // 画出去、再从原路画回来（同一笔）：两段完全重合
                for (int i = 0; i <= 40; i++) pts.Add(new(cx + i * 6f, cy));
                for (int i = 39; i >= 0; i--) pts.Add(new(cx + i * 6f, cy));
            }
            else if (kind == "浅角自交")
            {
                // 两条腿以很小的夹角交叉（"X" 的浅角版本）
                for (int i = 0; i <= 60; i++) pts.Add(new(cx + i * 7f, cy + 200f - i * 4f));
                for (int i = 1; i <= 60; i++) pts.Add(new(cx + 420f - i * 7f, cy - 40f + i * 5f));
            }

            // 点之间插值，贴近真实采样密度（5px 一个点）
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                int steps = Math.Max(1, (int)(Vector2.Distance(a, b) / 5f));
                for (int k = 0; k < steps; k++)
                {
                    float t = k / (float)steps;
                    s.AddPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, 0.5f, i * 100 + k);
                }
            }
            s.AddPoint(pts[^1].X, pts[^1].Y, 0.5f, 9999);
            Doc.AddStroke(s);
            return s.PaddedBounds;
        }

        var kinds = new (string kind, string name, float cx)[]
        {
            ("折170", "急折·内角10°", _virtualX + 260f),
            ("折160", "急折·内角20°", _virtualX + 820f),
            ("自交λ", "自交（λ）", _virtualX + 1420f),
            ("∧穿线", "∧ + 穿线", _virtualX + 2000f),
            ("原路折返", "原路折返", _virtualX + 2600f),
            ("浅角自交", "浅角自交", _virtualX + 900f),
        };
        // 3 列 × 2 行摆开（现在只有一种画法了：D2D 原生描边）
        var boxes = new List<(string name, RectF box)>();
        float[] colX = { _virtualX + 260f, _virtualX + 900f, _virtualX + 1560f };
        for (int i = 0; i < kinds.Length; i++)
        {
            int row = i < 3 ? 0 : 1;
            float cy = _virtualY + 380f + row * 460f;
            boxes.Add((kinds[i].name, Make(kinds[i].kind, colX[i % 3], cy)));
        }
        Doc.InvalidateAll();
        SettleFrames(700);

        // 逐个形状找洞：把区域截下来，从边界对"背景色"做连通填充，
        // 填不到又被墨围住的背景像素 = 洞。
        foreach (var (name, box) in boxes)
        {
            int x0 = (int)box.MinX - 6, y0 = (int)box.MinY - 6;
            int ww = (int)(box.MaxX - box.MinX) + 12, hh = (int)(box.MaxY - box.MinY) + 12;
            var px = ScreenProbe.CaptureRegion(x0, y0, ww, hh);
            if (px.Length < ww * hh * 4) { Check(name, false, "截屏失败"); continue; }

            bool IsBackground(int x, int y)
            {
                int i = (y * ww + x) * 4;
                int b = px[i], g = px[i + 1], r = px[i + 2];
                return r > 225 && g > 225 && b > 225;      // 白板底色
            }

            var seen = new bool[ww * hh];
            var stack = new Stack<int>();
            void Push(int x, int y)
            {
                if (x < 0 || y < 0 || x >= ww || y >= hh) return;
                int k = y * ww + x;
                if (seen[k] || !IsBackground(x, y)) return;
                seen[k] = true; stack.Push(k);
            }
            for (int x = 0; x < ww; x++) { Push(x, 0); Push(x, hh - 1); }
            for (int y = 0; y < hh; y++) { Push(0, y); Push(ww - 1, y); }
            while (stack.Count > 0)
            {
                int k = stack.Pop(); int x = k % ww, y = k / ww;
                Push(x - 1, y); Push(x + 1, y); Push(x, y - 1); Push(x, y + 1);
            }

            int holes = 0; int hx = 0, hy = 0;
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < ww; x++)
                    if (!seen[y * ww + x] && IsBackground(x, y))
                    { holes++; if (holes == 1) { hx = x0 + x; hy = y0 + y; } }

            Check(name, holes == 0,
                  holes == 0 ? "没有露底" : $"露出背景 {holes} 像素（第一处在 {hx},{hy}）");
        }

        string shot = Path.Combine("reports", "洞检.bmp");
        ScreenProbe.SaveBmp(shot, (int)(_virtualX + 100f), (int)(_virtualY + 200f), 2400, 1000);
        Console.WriteLine($"  （已导出 {shot}）");
        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();

        BoardOn = boardBefore;
        BoardColor = boardColorBefore;
        Doc.Clear();
        Doc.InvalidateAll();
        _quit = true;
    }


    private void CornerTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 折角自检（拐角会不会缺墨）===");

        float w = 20f * DpiScale;            // 粗笔，问题在粗笔上最明显
        float cx = VirtualScreen.MinX + 700, cy = VirtualScreen.MinY + 600;
        float arm = 400f;

        // 相机归零：下面的兜底判据要数"屏幕上"的像素，靠的是"画布坐标 = 屏幕坐标"
        // 这个前提。别的用例滚动过之后相机不为 0，不归零就会量到错的地方。
        ViewOffsetY = 0f;
        foreach (var win in _windows) { win.ViewOffsetX = 0f; win.ViewOffsetY = 0f; }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        // 白板：兜底那一处要数屏幕上的黑像素，背景必须是干净的。
        bool boardBefore = BoardOn;
        var boardColorBefore = BoardColor;
        BoardOn = true;
        BoardColor = new Color4(0.98f, 0.98f, 0.98f, 1f);

        // 造一条"先水平走、再按 turnDeg 折过去"的笔迹，中心线上的采样点一并返回。
        (Stroke s, List<Vector2> pts) MakeElbow(float turnDeg)
        {
            var stroke = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1), Width = w,
            };
            var samples = new List<Vector2>();
            float rad = turnDeg * MathF.PI / 180f;
            var dir = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
            for (int i = 0; i <= 30; i++)
            {
                var p = new Vector2(cx + arm * i / 30f, cy);
                stroke.AddPoint(p.X, p.Y, 0.5f, i);
                samples.Add(p);
            }
            for (int i = 1; i <= 30; i++)
            {
                var p = new Vector2(cx + arm + dir.X * arm * i / 30f, cy + dir.Y * arm * i / 30f);
                stroke.AddPoint(p.X, p.Y, 0.5f, 30 + i);
                samples.Add(p);
            }
            return (stroke, samples);
        }

        foreach (float turnDeg in new[] { 45f, 90f, 135f, 170f })
        {
            var (s, pts) = MakeElbow(turnDeg);

            // 判据：中心线上的采样点必须都落在墨里。几何现在是"中心线"，
            // 所以用描边判定（和画出来用的是同一条几何、同一个描边样式）。
            var geo = s.BuildGeometry(Gfx.D2DFactory);
            int dry = 0;
            Vector2 dryAt = default;
            foreach (var p in pts)
            {
                if (Vector2.Distance(p, new Vector2(cx + arm, cy)) > arm * 0.6f) continue;
                if (!geo.StrokeContainsPoint(p, MathF.Max(1f, s.Width), Gfx.Round))
                { dry++; dryAt = p; }
            }

            Check($"{turnDeg:F0}° 折角", dry == 0,
                  dry == 0 ? "中心线上每个采样点都有墨"
                           : $"有 {dry} 个采样点是空的（例如 "
                             + $"({dryAt.X - cx - arm:F0},{dryAt.Y - cy:F0})）");
        }

        // ---- 墨不能超出脏区（残影防线）----
        // 脏区（PaddedBounds）算小了不会报错、只会留残影，所以必须自己盯着。
        // 现在的墨 = 中心线 ± 半个笔宽（圆头圆角），用 D2D 的"加宽边界"对账。
        {
            var (s, _) = MakeElbow(90f);
            var geo = s.BuildGeometry(Gfx.D2DFactory);
            var wb = geo.GetWidenedBounds(MathF.Max(1f, s.Width), Gfx.Round, 0.25f);
            var pb = s.PaddedBounds;
            bool inside = wb.Left >= pb.MinX - 0.5f && wb.Top >= pb.MinY - 0.5f
                       && wb.Right <= pb.MaxX + 0.5f && wb.Bottom <= pb.MaxY + 0.5f;
            Check("墨不超出脏区（残影防线）", inside,
                  $"墨 {wb.Left:F0},{wb.Top:F0}→{wb.Right:F0},{wb.Bottom:F0}；"
                  + $"脏区 {pb.MinX:F0},{pb.MinY:F0}→{pb.MaxX:F0},{pb.MaxY:F0}");
        }

        // ---- 兜底：90° 折角在屏幕上真的画出来了（白板 + 拐角窗口）----
        {
            var (s, pts) = MakeElbow(90f);
            var ideal = new Vector2(cx + arm, cy);

            Doc.Clear();
            Doc.InvalidateAll();
            Doc.AddStroke(s);
            SettleFrames(500);

            var onPathPts = new List<Vector2>();
            foreach (var p in pts)
                if (Vector2.Distance(p, ideal) <= w) onPathPts.Add(p);
            int small = 5, darkest = int.MaxValue;
            foreach (var p in onPathPts)
            {
                int h = ScreenProbe.CountNear((int)(p.X - small * 0.5f), (int)(p.Y - small * 0.5f),
                                              small, small, 0, 0, 0, 60);
                if (h < darkest) darkest = h;
            }
            float cover = onPathPts.Count > 0 ? darkest / (float)(small * small) : 0f;
            Check("拐角上屏（白板）", onPathPts.Count > 0 && cover >= 0.8f,
                  $"拐角附近 {onPathPts.Count} 个中心线点，最暗的一个 {cover:P0} 是黑的");

            string shot = Path.Combine("reports", "拐角-放大.bmp");
            ScreenProbe.SaveBmp(shot, (int)(cx + arm - 200f), (int)(cy - 200f), 400, 400);
            Console.WriteLine($"  （拐角已导出：{shot}，白底黑字）");
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        BoardOn = boardBefore;
        BoardColor = boardColorBefore;
        Doc.Clear();
        Doc.InvalidateAll();
        _quit = true;
    }


    /// <summary>
    /// 笔迹两端自检：**圆头端帽**。
    ///
    /// 这一层以前把带子两侧的端点直接用直线连起来，于是两端是被切平的方块：
    /// 宽笔拖出来像一根长条尺子，单击一下也不是圆点、而是一小段扁条。
    /// 现在两端各补一段半圆（半径 = 端点处的半宽），收尾自然收成半圆。
    ///
    /// 判定分两层，缺一层都说明不了问题：
    ///   ① **几何精确判定**：直接问 Direct2D"端帽里那个点有没有被墨盖住"
    ///      （FillContainsPoint，和命中测试用的是同一条几何）；
    ///   ② **屏幕上数像素**：端帽那一小块到底有多少墨在真正的屏幕上。
    /// 只看①会漏掉"几何对了但没画出来"，只看②会分不清形状错了还是没上屏。
    /// 最后再导一张图，人能一眼看形状。
    /// </summary>
    private void CapTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 笔迹两端自检（单击＝圆点，宽笔＝圆头收尾）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        float w = 40f * DpiScale;                       // 宽笔：端帽形状在粗笔上最显眼
        float r = w * 0.5f;
        float x0 = _virtualX + 380f, y0 = _virtualY + 320f;
        var ink = new Color4(1f, 0f, 1f, 1f);           // 品红：便于在屏幕上数像素

        // ---------- ① 单击：一个采样点 → 一个圆 ----------
        var click = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        click.AddPoint(x0, y0, 0.5f, NowMs);
        var gClick = click.BuildGeometry(Gfx.D2DFactory);
        // 单点笔迹的几何本身就是那个圆 → 用填充判定
        Check("单击 · 圆心有墨", gClick.FillContainsPoint(new Vector2(x0, y0)), "");
        Check("单击 · 半径内 0.95r 有墨",
            gClick.FillContainsPoint(new Vector2(x0 + r * 0.95f, y0)), $"r = {r:F0}px");
        Check("单击 · 半径外 1.1r 没墨",
            !gClick.FillContainsPoint(new Vector2(x0 + r * 1.1f, y0)), "");

        // ---------- ② 短拖（2 个采样点）：两端都是半圆 ----------
        var drag = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        float dx = x0 + 240f, dy = y0, len = 140f;
        drag.AddPoint(dx, dy, 0.5f, NowMs);
        drag.AddPoint(dx + len, dy, 0.5f, NowMs);
        var gDrag = drag.BuildGeometry(Gfx.D2DFactory);
        // 两点以上的笔迹，几何是**中心线**，墨是 D2D 描出来的 →
        // 判定要用描边判定（和画出来用的是同一条几何、同一个描边样式）。
        bool InkedAt(ID2D1Geometry geo, Stroke s, float x, float y)
            => geo.StrokeContainsPoint(new Vector2(x, y), MathF.Max(1f, s.Width), Gfx.Round);

        // 端点"正前方"只有圆头端帽盖得到：平头的话这里一定是空的。
        Check("短拖 · 收笔正前方 0.8r 有墨（圆头）",
            InkedAt(gDrag, drag, dx + len + r * 0.8f, dy), "");
        Check("短拖 · 起笔正后方 0.8r 有墨（圆头）",
            InkedAt(gDrag, drag, dx - r * 0.8f, dy), "");
        Check("短拖 · 端帽外 1.1r 没墨（半圆，不是超出去的方块）",
            !InkedAt(gDrag, drag, dx + len + r * 1.1f, dy), "");
        Check("短拖 · 侧面外 1.1r 没墨",
            !InkedAt(gDrag, drag, dx + len * 0.5f, dy + r * 1.1f), "");

        // ---------- ③ 起笔连报重复坐标：方向不能翻车 ----------
        // 鼠标刚按下的一瞬间经常连报好几个相同坐标。方向要取"前后各一个真的
        // 不一样的点"，否则会被强行当成水平，起笔处的轮廓就歪了。
        var dup = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        float ex = x0, ey = y0 + 200f;
        dup.AddPoint(ex, ey, 0.5f, NowMs);
        dup.AddPoint(ex, ey, 0.5f, NowMs);
        dup.AddPoint(ex, ey, 0.5f, NowMs);
        dup.AddPoint(ex + 200f, ey + 200f, 0.5f, NowMs);   // 斜着走
        var gDup = dup.BuildGeometry(Gfx.D2DFactory);
        var d = Vector2.Normalize(new Vector2(1f, 1f));    // 真实前进方向
        Check("重复点起笔 · 正后方 0.95r 有墨（端帽朝反方向鼓）",
            InkedAt(gDup, dup, ex - d.X * r * 0.95f, ey - d.Y * r * 0.95f), "");
        Check("重复点起笔 · 正后方 1.1r 没墨",
            !InkedAt(gDup, dup, ex - d.X * r * 1.1f, ey - d.Y * r * 1.1f), "");

        // ---------- ④ 真的画到屏幕上再数一遍像素 ----------
        Doc.Clear();
        Doc.AddStroke(click);
        Doc.AddStroke(drag);
        Doc.AddStroke(dup);

        float hlW = 32f * DpiScale;
        var hl = new Stroke
        {
            Tool = Tool.Highlighter, Color = HighlighterCurrent, Width = hlW,
        };
        hl.AddPoint(x0, y0 + 400f, 0.5f, NowMs);
        hl.AddPoint(x0 + 420f, y0 + 400f, 0.5f, NowMs);
        Doc.AddStroke(hl);

        // 竖着拖一笔：端帽是"斜着/竖着"的时候也得成立（横线和竖线走的
        // 是同一条几何路径，但画到屏幕上的方向完全不同）。
        var vert = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        float vx = x0 + 560f, vy = y0 - 40f;
        vert.AddPoint(vx, vy, 0.5f, NowMs);
        vert.AddPoint(vx, vy + 320f, 0.5f, NowMs);
        Doc.AddStroke(vert);
        Doc.InvalidateAll();
        SettleFrames(700);

        int dotIn = ScreenProbe.CountMagenta((int)(x0 - r), (int)(y0 - r), (int)(2 * r), (int)(2 * r));
        int dotCorner = ScreenProbe.CountMagenta((int)(x0 + r * 0.75f), (int)(y0 + r * 0.75f),
                                                 (int)(r * 0.6f), (int)(r * 0.6f));
        float expect = MathF.PI * r * r;
        Check("单击上屏 · 圆点墨量对得上面积",
            Math.Abs(dotIn - expect) < expect * 0.12f,
            $"实测 {dotIn} / 理论 {expect:F0}（{dotIn / expect:P0}）");
        Check("单击上屏 · 圆的外角没有墨（不是方块）", dotCorner == 0, $"角上 {dotCorner} 像素");

        int capPx = ScreenProbe.CountMagenta((int)(dx + len + r * 0.1f), (int)(dy - r * 0.3f),
                                            (int)(r * 0.8f), (int)(r * 0.6f));
        int capFlat = ScreenProbe.CountMagenta((int)(dx + len + r * 1.15f), (int)(dy - r * 0.3f),
                                              (int)(r * 0.4f), (int)(r * 0.6f));
        Check("宽笔上屏 · 端帽那一段有墨", capPx > 200, $"端帽区 {capPx} 像素");
        Check("宽笔上屏 · 端帽外是空的", capFlat == 0, $"外侧 {capFlat} 像素");

        int vertBody = ScreenProbe.CountMagenta((int)(vx - r * 0.9f), (int)(vy + 60f),
                                                (int)(r * 1.8f), (int)(200f * DpiScale));
        int vertCap = ScreenProbe.CountMagenta((int)(vx - r * 0.3f), (int)(vy + 320f + r * 0.1f),
                                               (int)(r * 0.6f), (int)(r * 0.8f));
        Check("竖笔上屏 · 笔身有墨", vertBody > 20000, $"笔身区 {vertBody} 像素");
        Check("竖笔上屏 · 收笔端帽有墨", vertCap > 200, $"端帽区 {vertCap} 像素");

        string shot = Path.Combine("reports", "端帽-圆头.bmp");
        bool saved = ScreenProbe.SaveBmp(shot, (int)(x0 - 120f), (int)(y0 - 160f), 900, 700);
        Console.WriteLine(saved ? $"  （已导出 {shot}：单击圆点 / 短拖 / 斜拖 / 荧光笔 / 竖拖）"
                                : "  导出失败");

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }


    private void WidthTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 笔迹粗细渲染测试（填充带子会不会在粗笔画上出洞）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");
        Console.WriteLine();
        Console.WriteLine("  逻辑宽度 | 物理宽度 | 理论墨量 | 实测墨量 | 覆盖率");
        Console.WriteLine("  ---------|----------|----------|----------|--------");

        Doc.Clear();
        int i = 0;
        foreach (float wLogical in WidthPresets)
        {
            float wPhys = wLogical * DpiScale;
            float y = _virtualY + 140 + i * 150;
            float x0 = _virtualX + 200;
            float x1 = _virtualX + 1600;

            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = wPhys };
            for (int k = 0; k <= 120; k++)
            {
                float t = k / 120f;
                s.AddPoint(x0 + (x1 - x0) * t, y + MathF.Sin(t * 9f) * 40f, 1f, NowMs);
            }
            Doc.AddStroke(s);
            i++;
        }
        Doc.InvalidateAll();
        SettleFrames(700);

        i = 0;
        int bad = 0;
        foreach (float wLogical in WidthPresets)
        {
            float wPhys = wLogical * DpiScale;
            float y = _virtualY + 140 + i * 150;
            // The wavy path is ~1480 px of x plus the wiggle.
            float pathLen = 1560f;
            // 墨是"中心线 + 等宽描边"：宽度就是名义笔宽（压感不再影响粗细）。
            float expected = pathLen * wPhys;
            int actual = ScreenProbe.CountMagenta((int)(_virtualX + 190), (int)(y - 90), 1430, 180);
            Console.WriteLine($"  {wLogical,8:F1} | {wPhys,8:F0} | {expected,8:F0} | {actual,8} | {(actual / expected):F2}");
            // 判据：填充带子的墨量要落在理论值的合理区间里。明显偏小 = 自交处
            // 被挖空了（洞），明显偏大 = 重复填充。这条以前只有数字没有结论，
            // 于是"填充出洞"这种事必须靠人看图，现在它自己会红。
            float ratio = actual / expected;
            if (ratio < 0.75f || ratio > 1.15f) bad++;
            i++;
        }

        Console.WriteLine();
        Console.WriteLine(bad == 0
            ? "  PASS: 各档粗细的墨量都在理论值的 0.75~1.15 倍之间（填充没有出洞）"
            : $"  FAIL: 有 {bad} 档墨量偏离理论值（<0.75 或 >1.15）");
        _quit = true;
    }


    private void DashTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 线型自检（实线 / 虚线 / 点线）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        var ink = new Color4(1f, 0f, 1f, 1f);        // 品红：便于在屏幕上数像素
        float w = 6f * DpiScale;
        float len = 1000f;
        float rowGap = 150f;
        float x0 = _virtualX + 240f;
        float yTop = _virtualY + 240f;

        Stroke MakeLine(float y, StrokeDash dash)
        {
            var s = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line, Color = ink, Width = w, Dash = dash,
            };
            s.AddPoint(x0, y, 1f, NowMs);
            s.AddPoint(x0 + len, y, 1f, NowMs);
            return s;
        }

        bool SameBox(Stroke p, Stroke q)
        {
            var a = p.PaddedBounds; var b = q.PaddedBounds;
            // 只比**大小**不比位置：三条线故意画在不同的行上（同一行会互相盖住，
            // 屏幕上就数不出各自的墨量了），位置本来就该不一样。
            return MathF.Abs((a.MaxX - a.MinX) - (b.MaxX - b.MinX)) < 0.5f
                && MathF.Abs((a.MaxY - a.MinY) - (b.MaxY - b.MinY)) < 0.5f;
        }

        // ================= ① 屏幕层：三种线型上屏后的墨量 =================
        Doc.Clear();
        var solid = MakeLine(yTop, StrokeDash.Solid);
        var dashed = MakeLine(yTop + rowGap, StrokeDash.Dashed);
        var dotted = MakeLine(yTop + rowGap * 2f, StrokeDash.Dotted);
        Doc.AddStroke(solid);
        Doc.AddStroke(dashed);
        Doc.AddStroke(dotted);
        Doc.InvalidateAll();
        SettleFrames(700);

        float full = len * w;                        // 实线的"满墨量"基准
        int bandH = (int)MathF.Max(6f, w * 3f);
        int pxSolid = ScreenProbe.CountMagenta((int)x0, (int)(yTop - bandH * 0.5f), (int)len, bandH);
        int pxDashed = ScreenProbe.CountMagenta((int)x0, (int)(yTop + rowGap - bandH * 0.5f), (int)len, bandH);
        int pxDotted = ScreenProbe.CountMagenta((int)x0, (int)(yTop + rowGap * 2f - bandH * 0.5f), (int)len, bandH);

        Console.WriteLine();
        Console.WriteLine("  线型 | 屏幕墨量 | 占实线比例");
        Console.WriteLine("  -----|----------|-----------");
        Console.WriteLine($"  实线 | {pxSolid,8} | {pxSolid / full,9:P0}");
        Console.WriteLine($"  虚线 | {pxDashed,8} | {pxDashed / full,9:P0}");
        Console.WriteLine($"  点线 | {pxDotted,8} | {pxDotted / full,9:P0}");
        Console.WriteLine();

        Check("实线上屏：墨量接近满（≥85%）", pxSolid >= full * 0.85f,
              $"{pxSolid} / 理论 {full:F0}");
        Check("虚线上屏：出墨四到八成", pxDashed >= full * 0.40f && pxDashed <= full * 0.80f,
              $"{pxDashed / full:P0}（图案设计值 ~60%）");
        Check("点线上屏：出墨一成二到六成，且明显少于虚线",
              pxDotted >= full * 0.12f && pxDotted <= full * 0.60f && pxDotted < pxDashed * 0.8f,
              $"{pxDotted / full:P0} vs 虚线 {pxDashed / full:P0}");
        Check("三种线型的包围盒一模一样（线型是样式，不改形状）",
              SameBox(solid, dashed) && SameBox(solid, dotted),
              $"{solid.PaddedBounds.MaxX - solid.PaddedBounds.MinX:F0}×"
              + $"{solid.PaddedBounds.MaxY - solid.PaddedBounds.MinY:F0}");

        string shot = Path.Combine("reports", "线型-三种.bmp");
        bool saved = ScreenProbe.SaveBmp(shot, (int)(x0 - 60f), (int)(yTop - 60f), (int)(len + 120f), (int)(rowGap * 2f + 120f));
        Console.WriteLine(saved ? $"  （已导出 {shot}：实线 / 虚线 / 点线）" : "  导出失败");
        Console.WriteLine();

        // ================= ② 交互层：面板那一行真的能改、该跳过的会跳过 =================
        Doc.Clear();
        var shape = MakeLine(_virtualY + 400f, StrokeDash.Solid);
        var hand = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
        hand.AddPoint(x0, _virtualY + 560f, 0.5f, NowMs);
        hand.AddPoint(x0 + 300f, _virtualY + 560f, 0.5f, NowMs);
        var pic = new Stroke
        {
            Tool = Tool.Capture, Kind = StrokeKind.Image, Color = ink, Width = w,
            Image = ImageData.Adopt(8, 8, new byte[8 * 8 * 4], hasAlpha: true),
        };
        pic.AddPoint(x0, _virtualY + 700f, 1f, NowMs);
        pic.AddPoint(x0 + 80f, _virtualY + 780f, 1f, NowMs);
        Doc.AddStroke(shape);
        Doc.AddStroke(hand);
        Doc.AddStroke(pic);
        Doc.SelectOnly(new[] { shape, hand, pic });
        // ⚠ **必须切到框选工具**：操作条（和它下面那个线型面板）只在
        // `SelectionBarShown` 时画、也只在它时吃点击，而那个条件是
        // `有选中 && (框选工具 || 图形工具 || 刚停顿成型)`。
        // 这里以前是直接 `SelectOnly` 造出的选中、工具还停在笔上 → 面板画都不画，
        // 后面几次"打格子中心"当然全部接不住（2026-09-28 查"点线型那三格没反应"查到的）。
        // 真实用户的手法是"先框选"，所以测试也得先摆成那个状态。
        Tool = Tool.Marquee;
        Doc.InvalidateAll();
        SettleFrames(300);

        var aabb = LiveSelectionFrame.CanvasAabb;
        int sc = SelectionHandles.SwatchCount;
        var c0 = SelectionHandles.StyleCellRect(0, aabb, DpiScale, ViewportCanvas, sc);
        var c1 = SelectionHandles.StyleCellRect(1, aabb, DpiScale, ViewportCanvas, sc);
        var c2 = SelectionHandles.StyleCellRect(2, aabb, DpiScale, ViewportCanvas, sc);
        var panel = SelectionHandles.PanelRect(aabb, DpiScale, ViewportCanvas, sc);

        Check("线型那一行是 3 格", SelectionHandles.StyleCellCount == 3,
              $"格数 = {SelectionHandles.StyleCellCount}");
        Check("3 格从左到右排、互不重叠",
              c0.MaxX <= c1.MinX && c1.MaxX <= c2.MinX,
              $"{c0.MinX:F0}..{c0.MaxX:F0} | {c1.MinX:F0}..{c1.MaxX:F0} | {c2.MinX:F0}..{c2.MaxX:F0}");
        Check("3 格都落在面板里（四边都留了内边距）",
              c0.MinX >= panel.MinX && c2.MaxX <= panel.MaxX
              && c0.MinY >= panel.MinY && c0.MaxY <= panel.MaxY,
              $"面板 {panel.MinX:F0}..{panel.MaxX:F0}，格子 {c0.MinX:F0}..{c2.MaxX:F0}");

        // ---- 点那一行：**全程走真路** ----
        //
        // 早先这里直接调 `RunStyleClickForTest`（引擎里的测试钩子），**绕过了两层**：
        //   ① 「这一点算不算落在面板上」（`PanelContains`）——不在的话点击会被当成
        //      "点面板外面"，反手把面板关掉，用户看到的就是"点了没反应"；
        //   ② 「操作条那一格点了会不会开面板」。
        // 用户报"实线和虚线好像不能选中"时，这两层正是嫌疑最大的地方（引擎里那一层
        // 是连通的，所以只能靠真路把它钉死或抓出来）。现在按用户的手法走一遍：
        // 点操作条「颜色」→ 面板开 → 手势打格子中心。
        RunBarActionForTest((int)SelBarButton.Color);
        SettleFrames(200);
        Check("操作条「颜色」那一格能把线型面板打开",
              SelPanelOpen == SelPanel.Ink, $"面板 = {SelPanelOpen}");

        for (int i = 0; i < SelectionHandles.StyleCellCount; i++)
        {
            var cellI = SelectionHandles.StyleCellRect(i, aabb, DpiScale, ViewportCanvas, sc);
            float mx = (cellI.MinX + cellI.MaxX) * 0.5f, my = (cellI.MinY + cellI.MaxY) * 0.5f;
            Check($"第 {i + 1} 格的中心算「落在面板上」（命中那一层过得去）",
                  SelectionHandles.PanelContains(mx, my, aabb, DpiScale, ViewportCanvas,
                                                 SelPanelOpen, sc),
                  $"({mx:F0},{my:F0})，面板 {panel.MinX:F0}..{panel.MaxX:F0}"
                  + $" × {panel.MinY:F0}..{panel.MaxY:F0}");
        }

        bool StyleClick(int i)
        {
            var cell = SelectionHandles.StyleCellRect(i, aabb, DpiScale, ViewportCanvas, sc);
            bool took = SelectionGestureForTest((cell.MinX + cell.MaxX) * 0.5f,
                                                (cell.MinY + cell.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(160);
            return took;
        }

        var boxBefore = shape.PaddedBounds;
        bool tookDashed = StyleClick(1);
        Check("点第 2 格（虚线）：这一下被吞掉了（面板没被关掉）",
              tookDashed && SelPanelOpen == SelPanel.Ink,
              $"接住 = {tookDashed}，面板 = {SelPanelOpen}");
        Check("点第 2 格 → 直线变虚线", shape.Dash == StrokeDash.Dashed, $"实际 = {shape.Dash}");
        Check("同一次里：**自由笔迹也跟着改**（用户 2026-09-19 改的口径）",
              hand.Dash == StrokeDash.Dashed, $"实际 = {hand.Dash}");
        Check("同一次里：图像**没有**被改", pic.Dash == StrokeDash.Solid, $"实际 = {pic.Dash}");

        bool tookDotted = StyleClick(2);
        Check("点第 3 格（点线）：被吞掉且生效",
              tookDotted && shape.Dash == StrokeDash.Dotted,
              $"接住 = {tookDotted}，线型 = {shape.Dash}");

        var boxAfter = shape.PaddedBounds;
        Check("改线型不动包围盒",
              MathF.Abs(boxBefore.MinX - boxAfter.MinX) < 0.01f
              && MathF.Abs(boxBefore.MinY - boxAfter.MinY) < 0.01f
              && MathF.Abs(boxBefore.MaxX - boxAfter.MaxX) < 0.01f
              && MathF.Abs(boxBefore.MaxY - boxAfter.MaxY) < 0.01f,
              $"{boxBefore.MinX:F1},{boxBefore.MinY:F1} .. {boxBefore.MaxX:F1},{boxBefore.MaxY:F1} 未变");

        // 回到实线：**这一格最容易被当成"点了没反应"**——对象本来就是实线时点它是空操作，
        // 所以下面除了"线型真的回到 Solid"，还额外断言"格子的当前档真的跟着回去了"
        // （面板上要看得出来，不然用户只会觉得这一格坏了）。
        bool tookSolid = StyleClick(0);
        Check("点第 1 格（实线）：被吞掉、线型回到实线",
              tookSolid && shape.Dash == StrokeDash.Solid,
              $"接住 = {tookSolid}，线型 = {shape.Dash}");
        Check("面板认得出「现在是实线」（当前档读的是选中的那一条）",
              SelectionHandles.DashOfSelection(Doc.Selected) == StrokeDash.Solid,
              $"当前档 = {SelectionHandles.DashOfSelection(Doc.Selected)}");

        StyleClick(1);                                 // 再变虚线，留给下一条撤销用
        bool undoOk = Doc.Undo();
        Check("撤销一步 → 回到上一步的线型（改线型一步可撤）",
              undoOk && shape.Dash == StrokeDash.Solid, $"撤销 = {undoOk}，线型 = {shape.Dash}");
        Console.WriteLine();

        // ---- ②b 画布上点一条线能不能选中（实线 / 虚线都试）----
        //
        // 这一条**以前没有任何自检覆盖**（别的自检都是用 `Doc.SelectOnly` 直接给选中的）。
        // 用户说的"不能选中"如果是"画布上点不中"，就会在这里露出来。
        foreach (var (dash, name) in new[] { (StrokeDash.Solid, "实线"), (StrokeDash.Dashed, "虚线") })
        {
            Doc.Selected.Clear();
            SelPanelOpen = SelPanel.None;
            var ln = MakeLine(_virtualY + 400f, dash);
            Doc.Clear();
            Doc.AddStroke(ln);
            Doc.InvalidateAll();
            Tool = Tool.Marquee;
            SettleFrames(300);

            float midX = x0 + len * 0.5f, midY = _virtualY + 400f;
            bool tookPick = SelectionGestureForTest(midX, midY);
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check($"画布上点一条{name}：能选中它",
                  tookPick && Doc.Selected.Count == 1 && Doc.Selected[0] == ln,
                  $"接住 = {tookPick}，选中 {Doc.Selected.Count} 个");
        }
        Console.WriteLine();

        // ============ ②c 笔的线型开关：画之前选 vs 画之后改 ============
        //
        // 用户 2026-09-19 第 2 件要的那个"笔的色带条上的虚实线切换"。引擎这一头只验一件事：
        // **开关切到哪一档，新画出来的笔迹就是哪一档**，而且**不碰已经画好的**。
        // 界面那一格本身（画在哪、点得到、三档轮回、极简档没有）在 `--paneltest` 里验——
        // "引擎吃不吃这个值"和"界面上有没有这个入口"是两件事，混在一起验的话
        // 坏了一处也看不出是哪一处。
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Pen);
            SetDashFromUi(StrokeDash.Dashed);

            // 真机画一笔（合成鼠标按下 → 移动 → 抬起），返回刚画出来的那一条。
            Stroke DrawOne(float y)
            {
                float px = _virtualX + 600f;
                SendMouse((int)px, (int)y, 0);                                     SettleFrames(40);
                SendMouse((int)px, (int)y, Native.MOUSEEVENTF_LEFTDOWN);           SettleFrames(40);
                for (int i = 1; i <= 4; i++)
                {
                    SendMouse((int)(px + 60f * i), (int)y, 0);
                    SettleFrames(20);
                }
                SendMouse((int)(px + 240f), (int)y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(200);
                return Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            }

            var first = DrawOne(_virtualY + 560f);
            Check("笔的线型：切到虚线之后，新画的一笔就是虚线",
                  first != null && first.Kind == StrokeKind.Freehand
                  && first.Dash == StrokeDash.Dashed,
                  first == null ? "没有对象" : $"{first.Kind}，线型 = {first.Dash}");

            SetDashFromUi(StrokeDash.Solid);
            var second = DrawOne(_virtualY + 700f);
            Check("笔的线型：开关只管以后——切回实线之后，**先画的那一笔还是虚线**",
                  first != null && first.Dash == StrokeDash.Dashed
                  && second != null && second.Dash == StrokeDash.Solid,
                  $"第一笔 {first?.Dash}，第二笔 {second?.Dash}");

            SetDashFromUi(StrokeDash.Solid);       // 收尾：别把开关留在非默认值上
        }
        Console.WriteLine();

        // ================= ③ 存档层：写文件读回来线型还在 =================
        Doc.Clear();
        var sa = MakeLine(_virtualY + 300f, StrokeDash.Dashed);
        var sb = MakeLine(_virtualY + 400f, StrokeDash.Dotted);
        var scLine = MakeLine(_virtualY + 500f, StrokeDash.Solid);
        Doc.AddStroke(sa);
        Doc.AddStroke(sb);
        Doc.AddStroke(scLine);

        byte[] blob = InkSerializer.Save(Doc);
        var back = new InkDocument();
        InkSerializer.LoadInto(back, blob);

        Check("存档读回 3 条都在", back.Strokes.Count == 3, $"读回 {back.Strokes.Count} 条");
        Check("虚线读回来还是虚线",
              back.Strokes.Count > 0 && back.Strokes[0].Dash == StrokeDash.Dashed,
              back.Strokes.Count > 0 ? $"实际 = {back.Strokes[0].Dash}" : "条数不对");
        Check("点线读回来还是点线",
              back.Strokes.Count > 1 && back.Strokes[1].Dash == StrokeDash.Dotted,
              back.Strokes.Count > 1 ? $"实际 = {back.Strokes[1].Dash}" : "条数不对");
        Check("**第二条**的几何也对（读端少读一位会在这里整体错位）",
              back.Strokes.Count > 1 && back.Strokes[1].Points.Count == 2
              && MathF.Abs(back.Strokes[1].Points[1].X - (x0 + len)) < 0.5f,
              back.Strokes.Count > 1
                  ? $"第二条第 2 点 X = {back.Strokes[1].Points[1].X:F1}（应 {(x0 + len):F1}）"
                  : "条数不对");

        // 真·v7 老文件（见 MakeLegacyFile）：这一条同时钉住两件事——
        // 老文件读得进、且读进来是实线（老文件本来的样子）。
        // 砍 5 位 = v8 线型 ＋ v9 网格 ＋ v10 压感 ＋ v11 朝向 ＋ v12 渐近线。
        var one = new InkDocument();
        one.AddStroke(MakeLine(_virtualY + 300f, StrokeDash.Dotted));
        byte[] v7 = MakeLegacyFile(one, 7, 5);
        var old = new InkDocument();
        bool oldOk = true;
        try { InkSerializer.LoadInto(old, v7); } catch (Exception ex) { oldOk = false; Console.WriteLine("    " + ex.Message); }
        Check("真·v7 老文件读得进，线型是实线",
              oldOk && old.Strokes.Count == 1 && old.Strokes[0].Dash == StrokeDash.Solid,
              oldOk ? $"{old.Strokes.Count} 条，线型 = {(old.Strokes.Count > 0 ? old.Strokes[0].Dash.ToString() : "?")}"
                    : "读取抛异常");

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }


    /// <summary>
    /// 线型自检（2026-09-19）：实线 / 虚线 / 点线。
    ///
    /// 三层判据，缺哪一层都可能"看着绿其实没做"：
    ///   ① **屏幕层**：同一条水平线，三种线型在屏幕上数出来的墨量依次明显递减
    ///      （实线 ≈ 满、虚线 ≈ 六成、点线 ≈ 三成），**而包围盒一模一样**——
    ///      这一条专抓"字段存了但渲染没接上"（只看字段的话这一层会是绿的）。
    ///   ② **交互层**：改线型走的是面板那条真路（命中格子 → HandlePanelClick），
    ///      **图像不被改**（自由笔迹 2026-09-19 起**也改**——用户报的"选中以后虚线面板
    ///      还没有实现"就是它：引擎里 `Freehand` 那一行 `continue`），
    ///      改完包围盒一分不动，一步撤销能回到原样；
    ///   ②c **笔的线型开关**：色带条上切一档 → 新画出来的笔迹就是那一档，
    ///      而**已经画好的不被动**（"画之前选"和"画之后改"两条路各管各的）。
    ///   ③ **存档层**：写文件读回来线型还在；再造一个**真·v7 老文件**（见 MakeLegacyFile），
    ///      验"老文件读进来是实线"——这是版本闸写错时唯一会炸的地方。
    /// 最后导一张图，人能一眼看出三种样子。
    /// </summary>
    /// <summary>
    /// 四种曲线（抛物线 / 双曲线 / 正弦 / 余弦）自检。
    ///
    /// 盯的六件事：
    ///   ① **画法**：真机拖那一下，定义元素得落在该落的地方（顶点 / 中心 / 起点 = **按下的点**）；
    ///   ② **紧框**：必须等于"曲线自己的那个矩形 ＋ 半笔宽"，**不能是控制点的外接**——
    ///      双曲线会小掉一大圈、正弦会漏掉起点那一侧（椭圆当年就是这么翻的车）；
    ///   ③ **手柄**：精简之后抛物线 1 个、其余 2 个，而且每个手柄**只改一个量**；
    ///   ④ **朝向**：换一档几何真的换了、一步撤销回得去、四次一循环；
    ///   ⑤ **双曲线两支之间没有"幽灵线段"**（折线那份的抬笔约定，见 ShapeOutline）；
    ///   ⑥ **存档往返** ＋ **真 v10 老文件**读得进来（朝向位缺一位就是整体错位）。
    /// </summary>
    // =====================================================================
    //  棱柱自检（2026-09-20 第十一批，用户提的，见 计划-图形工具.md §32）
    // =====================================================================

    /// <summary>
    /// **棱柱 / 棱锥 / 棱台自检**：三兄弟的底面 / 棱 / 隐藏棱 ＋ 直/斜吸附 ＋ 档位 ＋ "长方体没被碰"。
    /// 用法：`--prismtest`
    ///
    /// 验六件事：
    ///   ① **底面是正 n 边形**：顶点数 = n，而且**参数角均匀**——不是"边长相等"：
    ///      底面是"俯视压扁"画的，边长方差被压过，只有参数角才是那个不变量；
    ///   ② **棱柱的三族边各 n 条**（顶面 / 底面 / 侧棱 = 3n），其中侧棱那 n 条的向量
    ///      就是"侧棱向量"（逐条对得上）；
    ///   ③ **隐藏棱条数** = §32.4 那条通用判据算出来的定值（见下面的小表）；
    ///   ④ **棱锥 / 棱台**只换"顶面怎么来"：棱锥顶面退化成一个点（棱只有 2n 条）、
    ///      棱台的上底按下底同形缩一个固定比例（棱仍是 3n 条），
    ///      被挡住的条数与棱柱**同一张表**（判据是同一条）；
    ///   ⑤ **真机直 / 斜**：往上拖偏一点点 → 吸住（胶囊「直棱柱 / 直棱锥 / 直棱台」）
    ///      且顶上那个中心严格在底心正上方；拖歪 → 不吸、方向就是那一拖的方向；
    ///   ⑥ **档位**：3→4→5→6→3、四张图标两两不同、**三格共用同一档**；**长方体一个字没动**。
    /// </summary>
    private void PrismTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 棱柱自检（3/4/5/6 棱柱 ＋ 直/斜）===");
        Console.WriteLine($"  本机 DPI 缩放 {DpiScale:F2}");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-46} {detail}");
        }
        bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        var ink = new Color4(1f, 0f, 1f, 1f);
        Host.Commands.SetColor(ink);
        // 界面那一层（问"这一段现在画的是哪张图标"要用它）——和 `--shapebandtest` 同一套起手：
        // 挂产品界面、等它铺开。**不挂的话 `CurrentUi` 不是 FullUi**，下面会 NRE。
        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine($"  界面没挂上：当前 = {CurrentUi.Name}");
            ExitCode = 1;
            _quit = true;
            return;
        }

        // **被挡住的棱有几条**：§32.4 那条通用判据手算的结果，判据本体在 `ShapeSpec.PrismFaceVisible`
        //（"侧面的外法向朝向观察者吗"——观察者是 `(错切比, 1)` 方向，**错切那一项不能漏**）。
        // 起始角见 `ShapeSpec.PrismBaseOffsetDegrees`（统一 `90° + 180°/n`，正前方永远是一条边）——
        // 这个表跟着那个起始角走：改起始角，四个数都要重算。
        int HiddenWant(int nsides) => nsides switch { 3 => 3, 4 => 3, 5 => 3, 6 => 5, _ => -1 };

        // 把**某个立体工具**那一档拧到指定值（走的就是面板上"再点一次"那个命令）。
        // ⚠ 必须先切到那个工具——引擎换的是"**当前工具**那一档"（三格各记各的，
        // 见 `UiState.SidesOf`），不切工具就会一直拧棱柱那一档。
        void SetSides(Tool tool, int want)
        {
            SetToolFromUi(tool);
            for (int k = 0; k < 8 && Host.State.SidesOf(tool) != want; k++)
                Host.Commands.CycleSolidSides();
            SettleFrames(60);
        }

        Stroke NewSolid(Tool tool, StrokeKind kind, int sides)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 4f * DpiScale,
                PrismSides = sides,
            };
            s.AddPoint(400f, 300f, 1f, 0);
            return s;
        }

        Stroke NewPrism(int sides) => NewSolid(Tool.Prism, StrokeKind.Prism, sides);

        // ================= ①②③ 数学层：四个 n 各造一个 =================
        Console.WriteLine("  -- 数学层：底面 / 三族边 / 隐藏棱 --");
        foreach (int sides in new[] { 3, 4, 5, 6 })
        {
            var s = NewPrism(sides);
            s.SetPrismBase(400f, 300f, 700f, 420f);      // 外接框 300×120
            s.SetPrismApex(550f, 120f);                  // 顶心：往上拖 180

            var b = s.PrismBaseLocal();
            var lat = s.PrismLateralLocal();
            Check($"底面顶点数 = {sides}", b.Length == sides, $"{b.Length} 个");

            // 参数角均匀：把顶点**按画布上那套投影反算回单位圆**，相邻夹角差该是 360/n。
            // 反算必须把**错切**一起减掉（`ShapeSpec`：x = rxUse·cos − skew·sin, y = ry·sin），
            // 不然后面那步"除以 rx"减不掉错切，算出来的角就不均匀（这一条当场抓到过）。
            var c = s.PrismBaseCenterLocal();
            const float rx = 150f, ry = 60f;
            ShapeSpec.PrismFrame(rx, ry, out float rxUse, out float skew);
            float maxDev = 0f;
            for (int k = 0; k < sides; k++)
            {
                var a = b[k];
                var nx = b[(k + 1) % sides];
                float v0 = (a.Y - c.Y) / ry;
                float v1 = (nx.Y - c.Y) / ry;
                float u0 = (a.X - c.X + skew * v0) / rxUse;
                float u1 = (nx.X - c.X + skew * v1) / rxUse;
                float d = MathF.Atan2(v1, u1) - MathF.Atan2(v0, u0);
                while (d <= 0f) d += MathF.Tau;
                maxDev = MathF.Max(maxDev, MathF.Abs(d - MathF.Tau / sides));
            }
            Check($"{sides} 棱柱·底面是**正** {sides} 边形（参数角均匀，±0.5°）",
                  maxDev <= .5f * MathF.PI / 180f, $"最大偏差 {maxDev * 180f / MathF.PI:F4}°");

            // 三族边各 n 条；侧棱那 n 条的两端点之差 = 侧棱向量。
            // （`InkPiece` 是 Stroke 的**嵌套内部类型**，这里用 `var` 免得写全名。）
            var vis = s.PrismFamilyEdges(hidden: false);
            var hid = s.PrismFamilyEdges(hidden: true);
            var visLat = new List<float>();      // 看得见的竖棱的 x
            var hidLat = new List<float>();      // 被挡住的竖棱的 x
            int latCount = 0;
            foreach (var pc in vis)
                if (pc.Pts.Count == 2 && Vector2.Distance(pc.Pts[0] + lat, pc.Pts[1]) < 0.01f)
                { latCount++; visLat.Add(pc.Pts[0].X); }
            foreach (var pc in hid)
                if (pc.Pts.Count == 2 && Vector2.Distance(pc.Pts[0] + lat, pc.Pts[1]) < 0.01f)
                { latCount++; hidLat.Add(pc.Pts[0].X); }
            Check($"{sides} 棱柱·三族边各 {sides} 条（顶面 ＋ 底面 ＋ 侧棱 = {3 * sides}）",
                  vis.Count + hid.Count == 3 * sides && latCount == sides,
                  $"共 {vis.Count + hid.Count} 条（实 {vis.Count} / 虚 {hid.Count}），侧棱 {latCount} 条");

            Check($"{sides} 棱柱·被挡住 {HiddenWant(sides)} 条（§32.4 判据的定值）",
                  hid.Count == HiddenWant(sides), $"{hid.Count} 条（期望 {HiddenWant(sides)}）");

            // ---- 用户 2026-09-20 要求的三条（每一条都是一个"看着不对"的现场）----

            // ① **最靠前的那一条边必须是水平的**（四棱柱原来"最前面那条棱是斜的"）。
            //    判据：落在最前那一排（y 最大）的顶点得**不止一个**——两个以上才谈得上一条边。
            float maxY = float.MinValue;
            foreach (var v in b) maxY = MathF.Max(maxY, v.Y);
            int frontVerts = 0;
            foreach (var v in b) if (MathF.Abs(v.Y - maxY) < 0.5f) frontVerts++;
            Check($"{sides} 棱柱·最靠前那一条边是**水平的**（两个顶点同高）",
                  frontVerts >= 2, $"最前一排有 {frontVerts} 个顶点");

            // ② **被挡住的竖棱不能躲在前面的实线背后**（六棱柱原来后面两条虚线完全看不见）。
            //    判据用**模型自己分出来的那两组**（上面 visLat / hidLat 就是 PrismFamilyEdges 的产物）——
            //    不在这里另写一份"哪个面看得见"，那份判据只有 `ShapeSpec.PrismFaceVisible` 一条。
            float minGap = float.MaxValue;
            foreach (var hx in hidLat)
                foreach (var vx in visLat) minGap = MathF.Min(minGap, MathF.Abs(hx - vx));
            Check($"{sides} 棱柱·被挡住的竖棱**错开**了可见的竖棱（x 上拉开 ≥ 0.1×半宽）",
                  hidLat.Count == 0 || minGap >= 0.1f * rx,
                  $"虚 {hidLat.Count} 条 / 实 {visLat.Count} 条，最小间距 "
                  + $"{(float.IsPositiveInfinity(minGap) ? 0f : minGap):F1}（半宽 {rx:F0}）");

            // ③ **紧框上沿要贴住最高的那个顶点**（原来"上面会漏一块"：
            //    包围盒按"底面外接框"算，而正 n 边形并不填满那个框）。
            float topmost = float.MaxValue;
            foreach (var v in b)
            {
                topmost = MathF.Min(topmost, v.Y);
                topmost = MathF.Min(topmost, v.Y + lat.Y);
            }
            float frameGap = topmost - s.WorldInkBounds.MinY;      // 期望 = 半个笔宽
            Check($"{sides} 棱柱·紧框上沿贴着最高顶点（差 ≈ 半笔宽，不漏块）",
                  Near(frameGap, s.Width * 0.5f, 1.5f),
                  $"差 {frameGap:F2}（半笔宽 {s.Width * 0.5f:F2}）");
        }

        // ================= ④ 棱锥 / 棱台：和棱柱**同族**，只换"顶面怎么来" =================
        //
        // 这三兄弟的控制点完全一样（底面外接框两角 ＋ 顶上那个中心），所以几何上的差别
        // 就只有**顶面那一份点**（见 `Stroke.PrismTopLocal`）。这一节就把那一点钉住：
        //   · 棱锥：n 个顶面点**全等**（退化成一点）→ 于是棱只有 2n 条（没有顶面那一圈）；
        //   · 棱台：顶面点 = 顶心 ＋（底点 − 底心）× 比例 → 棱仍是 3n 条。
        // 另外**被挡住的棱条数沿用棱柱那张定值表**——这不是偷懒：三个图形的可见性判据
        // 是同一条（侧面法向朝不朝观察者），所以"几虚几实"本来就该一样。
        // 哪天判据动了，这里跟着一起动；要是**只有一家变了**，这条立刻红。
        Console.WriteLine("  -- 棱锥 / 棱台：控制点同棱柱，差别只在顶面 --");
        var familyRun = new (Tool tool, StrokeKind kind, string name)[]
        {
            (Tool.Pyramid, StrokeKind.Pyramid, "棱锥"),
            (Tool.Frustum, StrokeKind.Frustum, "棱台"),
        };
        foreach (var (tool, kind, fname) in familyRun)
        {
            foreach (int sides in new[] { 3, 4, 5, 6 })
            {
                var s = NewSolid(tool, kind, sides);
                s.SetPrismBase(400f, 300f, 700f, 420f);      // 和棱柱那一段同一个外接框
                s.SetPrismApex(550f, 120f);                  // 顶心：往上拖 180（直的）

                // ---- 顶面那一份点 ----
                var tp = s.PrismTopLocal();
                if (kind == StrokeKind.Pyramid)
                {
                    // 棱锥：n 个点**全等**（就是顶点）。容差给 0.01——它们是同一份算式出来的，
                    // 只可能有浮点末位差。
                    float maxSpread = 0f;
                    for (int k = 1; k < tp.Length; k++)
                        maxSpread = MathF.Max(maxSpread, Vector2.Distance(tp[0], tp[k]));
                    Check($"{sides} {fname}·顶面**退化成一个点**（{sides} 个顶面点全等）",
                          maxSpread < 0.01f, $"最大间距 {maxSpread:F4}");
                }
                else
                {
                    // 棱台：顶面点 = 顶心 ＋（底点 − 底心）× 比例，逐点比（把比例也钉住）。
                    var b0 = s.PrismBaseLocal();
                    var c0 = s.PrismBaseCenterLocal();
                    var apex0 = s.PrismApexLocal();
                    float maxErr = 0f;
                    for (int k = 0; k < tp.Length; k++)
                    {
                        var want = apex0 + (b0[k] - c0) * ShapeSpec.FrustumTopScale;
                        maxErr = MathF.Max(maxErr, Vector2.Distance(tp[k], want));
                    }
                    Check($"{sides} {fname}·上底 = 下底同形缩 {ShapeSpec.FrustumTopScale:F2}（逐点对）",
                          maxErr < 0.01f, $"最大偏差 {maxErr:F4}");
                }

                // ---- 棱的条数：棱锥少一圈顶面，所以是 2n 而不是 3n ----
                var vis = s.PrismFamilyEdges(hidden: false);
                var hid = s.PrismFamilyEdges(hidden: true);
                int wantEdges = kind == StrokeKind.Pyramid ? 2 * sides : 3 * sides;
                Check($"{sides} {fname}·棱共 {wantEdges} 条"
                      + (kind == StrokeKind.Pyramid ? "（**没有顶面那一圈**）" : "（底面 ＋ 顶面 ＋ 侧棱）"),
                      vis.Count + hid.Count == wantEdges,
                      $"共 {vis.Count + hid.Count} 条（实 {vis.Count} / 虚 {hid.Count}）");

                // ---- 被挡住的条数：和棱柱**同一张定值表**（判据是同一条）----
                Check($"{sides} {fname}·被挡住 {HiddenWant(sides)} 条（和棱柱同一张表）",
                      hid.Count == HiddenWant(sides), $"{hid.Count} 条（期望 {HiddenWant(sides)}）");
            }
        }

        // ================= ⑤ 真机：第 2 笔的直 / 斜 =================
        Console.WriteLine("  -- 真机：第 2 笔往上拖 = 直棱柱（吸附）／拖歪 = 斜棱柱 --");

        // 第 1 笔（两处都要用）：拖一个底面外接框。
        void DragBase(float bx, float by)
        {
            SendMouse((int)bx, (int)by, 0);                                          SettleFrames(50);
            SendMouse((int)bx, (int)by, Native.MOUSEEVENTF_LEFTDOWN);                 SettleFrames(50);
            SendMouse((int)(bx + 300f), (int)(by + 120f), 0);                         SettleFrames(80);
            SendMouse((int)(bx + 300f), (int)(by + 120f), Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);
        }

        Doc.Clear();
        Doc.ClearHistory();
        SetSides(Tool.Prism, 4);
        float bx0 = _virtualX + 900f, by0 = _virtualY + 900f;
        DragBase(bx0, by0);
        Check("真机：第 1 笔松手后**还没提交**（两笔图形，半成品不进文档）",
              Doc.Strokes.Count == 0 && ActiveStroke != null,
              $"文档 {Doc.Strokes.Count} 条");
        Check("真机：第 1 笔的预览就是底面那一圈（侧棱还没拖，几何是扁的）",
              ActiveStroke != null && ActiveStroke.PrismLateralLocal().LengthSquared() < 1e-6f,
              $"侧棱 {ActiveStroke?.PrismLateralLocal()}");

        // 第 2 笔：**故意偏 8 像素**（容差 12）→ 该吸住
        float ax = bx0 + 150f + 8f, ay = by0 + 60f - 180f;
        SendMouse((int)ax, (int)ay, 0);                                    SettleFrames(50);
        SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTDOWN);           SettleFrames(50);
        SendMouse((int)ax, (int)ay, 0);                                    SettleFrames(180);
        Check("真机·直棱柱：第 2 笔偏 8 像素 → **吸住**", StepSnap == ShapeSnapKind.RightPrism,
              $"StepSnap = {StepSnap}（期望 RightPrism）");
        Check("真机·直棱柱：那颗胶囊写的是「直棱柱」（和「等边/正方形」同一套语言）",
              SelectionHandles.ShapeSnapLabel(StepSnap) == "直棱柱",
              $"「{SelectionHandles.ShapeSnapLabel(StepSnap)}」");
        Check("真机·直棱柱：吸住那一刻侧棱**已经竖直**（吸附写在模型上，不只是显示）",
              ActiveStroke != null && MathF.Abs(ActiveStroke.PrismLateralLocal().X) < 0.5f,
              $"侧棱 {ActiveStroke?.PrismLateralLocal()}");
        SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTUP);             SettleFrames(240);
        Check("真机·直棱柱：松手后胶囊收回去", StepSnap == ShapeSnapKind.None,
              $"StepSnap = {StepSnap}");
        Check("真机·直棱柱：提交之后**侧棱严格竖直**（横向差 < 0.5）",
              Doc.Strokes.Count == 1 && MathF.Abs(Doc.Strokes[0].PrismLateralLocal().X) < 0.5f,
              $"侧棱 {(Doc.Strokes.Count == 1 ? Doc.Strokes[0].PrismLateralLocal().ToString() : "（没提交）")}");
        Check("真机·直棱柱：底面边数 = 当前那一档（4）",
              Doc.Strokes.Count == 1 && Doc.Strokes[0].PrismSidesClamped == 4,
              $"{(Doc.Strokes.Count == 1 ? Doc.Strokes[0].PrismSidesClamped : 0)} 边形");

        // 拖歪 → 不吸，方向就是那一拖的方向
        Doc.Clear();
        Doc.ClearHistory();
        SetToolFromUi(Tool.Prism);
        DragBase(bx0, by0);
        float sx = bx0 + 150f + 220f, sy = by0 + 60f - 140f;      // 明显偏右 → 斜棱柱
        SendMouse((int)sx, (int)sy, 0);                                    SettleFrames(50);
        SendMouse((int)sx, (int)sy, Native.MOUSEEVENTF_LEFTDOWN);           SettleFrames(50);
        SendMouse((int)sx, (int)sy, 0);                                    SettleFrames(160);
        Check("真机·斜棱柱：拖歪 → **不吸**（没有胶囊）", StepSnap == ShapeSnapKind.None,
              $"StepSnap = {StepSnap}");
        SendMouse((int)sx, (int)sy, Native.MOUSEEVENTF_LEFTUP);             SettleFrames(240);
        var slat = Doc.Strokes.Count == 1 ? Doc.Strokes[0].PrismLateralLocal() : Vector2.Zero;
        Check("真机·斜棱柱：侧棱方向 = 那一拖的方向（横向差 ≈ 220−8，不被动过）",
              Doc.Strokes.Count == 1 && Near(slat.X, 220f, 2f) && Near(slat.Y, -140f, 2f),
              $"侧棱 {slat}（期望 ≈ (220, −140)）");

        // ---- 棱锥 / 棱台：**同一套动作、同一个吸附**，只是胶囊上的字和"顶上那点"不同 ----
        //
        // 这几条一起看的就是"**族内一致**"：容差同一个（12）、判据同一条（顶上那个中心在
        // 底心正上方）、只是报出来的名字按种类分。所以这里最要紧的两条断言是：
        //   · **胶囊上的字**——它错了用户就会看到"画棱锥却写着直棱柱"；
        //   · **底面边数 = 这一格自己的档**——2026-09-20 第一版把"写档"那句写成
        //     `kind == StrokeKind.Prism`，于是棱锥 / 棱台**永远画成四棱**（用户："锥体和台体
        //     只能画四棱，不能画其他的"）。所以这里**故意给两家不同的档**（3 / 5）：
        //     要是又有人漏了那一句，两条都会红。
        var familyReal = new (Tool tool, StrokeKind kind, string name, ShapeSnapKind snap, string label, int sides)[]
        {
            (Tool.Pyramid, StrokeKind.Pyramid, "棱锥", ShapeSnapKind.RightPyramid, "直棱锥", 3),
            (Tool.Frustum, StrokeKind.Frustum, "棱台", ShapeSnapKind.RightFrustum, "直棱台", 5),
        };
        foreach (var (tool, kind, fname, snap, label, wantSides) in familyReal)
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetSides(tool, wantSides);
            DragBase(bx0, by0);

            float px = bx0 + 150f + 8f, py = by0 + 60f - 180f;      // 故意偏 8 像素（容差 12）
            SendMouse((int)px, (int)py, 0);                                    SettleFrames(50);
            SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN);           SettleFrames(50);
            SendMouse((int)px, (int)py, 0);                                    SettleFrames(180);
            Check($"真机·{fname}：第 2 笔偏 8 像素 → **吸住**，胶囊「{label}」",
                  StepSnap == snap && SelectionHandles.ShapeSnapLabel(StepSnap) == label,
                  $"StepSnap = {StepSnap}「{SelectionHandles.ShapeSnapLabel(StepSnap)}」");
            SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTUP);             SettleFrames(240);

            var made = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            float tilt = made == null ? -1f
                : MathF.Abs(made.PrismApexLocal().X - made.PrismBaseCenterLocal().X);
            Check($"真机·{fname}：提交之后顶上那个中心**严格在底心正上方**",
                  made != null && made.Kind == kind && tilt < 0.5f,
                  made == null ? "（没画出来）" : $"侧倾 {tilt:F2}（期望 < 0.5）");
            Check($"真机·{fname}：底面是**这一格自己的档**（{wantSides} 棱）——不是永远四棱",
                  made != null && made.PrismSidesClamped == wantSides
                  && made.PrismBaseLocal().Length == wantSides,
                  made == null ? "（没画出来）"
                               : $"存档里的档 {made.PrismSidesClamped}、底面 {made.PrismBaseLocal().Length} 个顶点");
        }

        // ================= ⑥ 档位 ＋ 长方体没被碰 =================
        Console.WriteLine("  -- 档位：点那一格再点一次 3→4→5→6→3 --");
        SetSides(Tool.Prism, 3);
        var iconNames = new List<string>();
        for (int k = 0; k < 4; k++)
        {
            iconNames.Add(ui.ShapeIconNameForTest(InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Prism)));
            Host.Commands.CycleSolidSides();
            SettleFrames(60);
        }
        Check("档位：一轮四档，回到三棱柱（3→4→5→6→3）",
              Host.State.SidesOf(Tool.Prism) == 3, $"现在是 {Host.State.SidesOf(Tool.Prism)}");
        Check("档位：四张图标两两不同，而且都是 prism 那四张",
              iconNames.Distinct().Count() == 4 && iconNames.All(nm => nm.StartsWith("prism")),
              string.Join(" / ", iconNames));
        Check("档位：范围**随状态推上界面**（3 / 6）——界面靠它决定画几个档位点",
              Host.State.SolidMinSides == 3 && Host.State.SolidMaxSides == 6,
              $"{Host.State.SolidMinSides} ~ {Host.State.SolidMaxSides}");

        // **三格各记各的档**（用户 2026-09-20 上手就报的 bug："切一个另外两个也动"）。
        //
        // 第一版是"三格共用一个 `PrismSides`"——写的时候觉得"它们本来就是同一族，
        // 跟着走正好"，实际用起来完全不是：老师完全可能"四棱柱 ＋ 三棱锥"混着画。
        // 所以现在是引擎里**三个字段**，换档命令换的是"**当前工具**那一档"。
        // 这里就钉住那件事：先把三格拧成**三个不同的档**，再单独换一格，看另外两格动没动。
        //
        // ⚠ 段号一律**按工具名问**（`ShapeSegmentIndexForTest`），不写死：
        // 写死的话，第二行一旦插 / 删图形就会静默点错段（这一轮加棱锥 / 棱台时就栽了一下
        // ——写死的"16 = 棱柱"其实已经是"棱台"）。
        SetSides(Tool.Prism, 3);
        SetSides(Tool.Pyramid, 4);
        SetSides(Tool.Frustum, 5);
        string Sides3() => $"{Host.State.SidesOf(Tool.Prism)}/"
                         + $"{Host.State.SidesOf(Tool.Pyramid)}/{Host.State.SidesOf(Tool.Frustum)}";
        Check("档位：三格**各记各的档**（四棱柱 ＋ 三棱锥 ＋ 五棱台 这种配法要能存在）",
              Host.State.SidesOf(Tool.Prism) == 3 && Host.State.SidesOf(Tool.Pyramid) == 4
              && Host.State.SidesOf(Tool.Frustum) == 5,
              Sides3() + "（期望 3/4/5）");
        SetToolFromUi(Tool.Pyramid);                 // 换档换的是"当前工具那一档"
        Host.Commands.CycleSolidSides();
        SettleFrames(60);
        Check("档位：只换棱锥那一格 → **棱柱和棱台的档一点没动**",
              Host.State.SidesOf(Tool.Prism) == 3 && Host.State.SidesOf(Tool.Pyramid) == 5
              && Host.State.SidesOf(Tool.Frustum) == 5,
              Sides3() + "（期望 3/5/5）");
        string IconOf(Tool t) => ui.ShapeIconNameForTest(InkUi.FullUi.ShapeSegmentIndexForTest(t));
        Check("档位：三格的图标各按**自己那一档**画（棱柱 3 / 棱锥 5 / 棱台 5）",
              IconOf(Tool.Prism) == "prism3" && IconOf(Tool.Pyramid) == "pyramid5"
              && IconOf(Tool.Frustum) == "frustum5",
              $"{IconOf(Tool.Prism)} / {IconOf(Tool.Pyramid)} / {IconOf(Tool.Frustum)}");
        SetSides(Tool.Prism, 3);

        Doc.Clear();
        Doc.ClearHistory();
        DragBase(bx0, by0);
        SendMouse((int)(bx0 + 150f), (int)(by0 + 60f - 200f), 0);             SettleFrames(50);
        SendMouse((int)(bx0 + 150f), (int)(by0 + 60f - 200f), Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
        SendMouse((int)(bx0 + 150f), (int)(by0 + 60f - 200f), Native.MOUSEEVENTF_LEFTUP);   SettleFrames(240);
        Check("档位：三棱柱真的画得出来（底面 3 个顶点、侧棱 3 条）",
              Doc.Strokes.Count == 1 && Doc.Strokes[0].PrismBaseLocal().Length == 3
              && Doc.Strokes[0].PrismFamilyEdges(hidden: false).Count
                 + Doc.Strokes[0].PrismFamilyEdges(hidden: true).Count == 9,
              Doc.Strokes.Count == 1
                ? $"底面 {Doc.Strokes[0].PrismBaseLocal().Length} 个顶点、"
                  + $"棱 {Doc.Strokes[0].PrismFamilyEdges(false).Count + Doc.Strokes[0].PrismFamilyEdges(true).Count} 条"
                : "（没画出来）");

        // 长方体：一个数都不许变（它是另一族，见 §32.2 第 2 条）。
        var cu = new Stroke { Tool = Tool.Cuboid, Kind = StrokeKind.Cuboid, Color = ink, Width = 4f * DpiScale };
        cu.AddPoint(200f, 500f, 1f, 0);
        cu.SetCuboidFront(200f, 500f, 600f, 900f);
        cu.SetCuboidDepth(500f, 650f);
        Check("长方体不受影响：深度 150、第三个控制点还是背面右下角 (750, 750)",
              Near(cu.CuboidDepthLocal(), 150f, .5f)
              && Near(cu.CurvePointLocal(2).X, 750f, .5f) && Near(cu.CurvePointLocal(2).Y, 750f, .5f),
              $"深度 {cu.CuboidDepthLocal():F1}，第 3 点 "
              + $"({cu.CurvePointLocal(2).X:F0},{cu.CurvePointLocal(2).Y:F0})");
        Check("长方体不受影响：它给的还是**通用八手柄**那一套（棱柱没有碰它）",
              !SelectionHandles.HasShapeHandles(cu)
              && SelectionHandles.HasShapeHandles(NewPrism(4)) == false,
              "长方体 / 棱柱 都没有定义元素手柄（走通用框）");

        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(120);

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        _quit = true;
    }

    // =====================================================================
    //  棱柱出图（2026-09-20 第十一批）：`--prismshow <路径>`
    // =====================================================================

    /// <summary>
    /// 出图：**三 / 四 / 五 / 六** × **棱柱 / 棱锥 / 棱台**各一行（都是"直"的）
    /// ＋ 一行"**直 vs 斜**"。
    ///
    /// 为什么必须出图：这一族"立着画得对不对、虚线配得对不对"**只能看**——
    /// 几何全对（顶点数、边长、侧棱向量）也一样可能看着别扭。
    /// 尤其三点：① 四棱柱那条（底面是扁的正方形，以前左右两条边与视线平行、
    /// 整块塌成平板，见 §33.2 / §33.6）；② 五/六两张的底面看得出分别吗；
    /// ③ **棱锥的顶是不是收敛成一点、棱台的上底是不是明显小一圈**——
    /// 这两件事自检只能数出"点的个数 / 比例对"，"看着像不像"只有图能说。
    /// </summary>


    /// <summary>
    /// 脏区渲染特有的风险测试：双缓冲里躺着的是两帧前的画面，如果脏区没覆盖到
    /// 上一帧临时图元的位置，快速移动的橡皮光标/激光就会留下"残影"。
    /// 这里用大跨度快速移动橡皮光标，最后停在远处，然后数屏幕上还剩下多少
    /// 光标颜色的像素——只剩停住那一圈才算通过。
    /// </summary>
    /// <summary>
    /// 验证微软的委托墨迹轨迹是否真的在画：把自己那一笔关掉，
    /// 拖动过程中按住不放截图——如果还能看到笔迹，那就是系统合成器画的。
    /// </summary>
    /// <summary>
    /// 长跑测试：模拟"上一节课"的连续使用，看内存是否稳定、帧耗时是否劣化。
    /// 每轮画一批笔画，再做擦除/撤销/清空，并周期性记录状态。
    /// </summary>
    /// <summary>
    /// A/B 实测：几何实现缓存到底能省多少。
    /// A = 每次都让 Direct2D 重新细分几何（现状）
    /// B = 先把细分结果缓存下来，之后只提交三角形
    /// </summary>
    /// <summary>
    /// 分辨率实测：同样的笔画工作量，放到不同尺寸的离屏内容层上跑，
    /// 看哪些开销随像素数增长、哪些不增长。用来回答"4K 教室里够不够用"。
    /// </summary>
    private void ResolutionTest(int strokeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 分辨率实测（{strokeCount} 笔）===");
        Console.WriteLine("  内容层尺寸 | 图层内存 | 整层清屏+重画 | 20 次脏区补丁 | 每次补丁");
        Console.WriteLine("  -----------|----------|---------------|----------------|----------");

        GenerateStrokes(strokeCount);
        var ctx = _windows[0].Context;
        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);

        (int w, int h, string label)[] sizes =
        {
            (1920, 1080, "1920x1080"),
            (2560, 1440, "2560x1440"),
            (2880, 1800, "2880x1800 当前屏"),
            (3840, 2160, "3840x2160 4K"),
        };

        foreach (var (w, h, label) in sizes)
        {
            double before = Mem.Priv();
            var bmp = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
                new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));

            // 先画一遍让缓存/显存真正落地
            RenderInto(bmp, strokeCount);
            double after = Mem.Priv();

            // 整层：清屏 + 画全部笔画
            var sw = Stopwatch.StartNew();
            RenderInto(bmp, strokeCount);
            sw.Stop();
            double full = sw.Elapsed.TotalMilliseconds;

            // 脏区补丁：取 20 个 300x300 的方块，清掉并重画落在里面的笔画
            var rnd = new Random(7);
            var swPatch = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
            {
                var r = new RectF
                {
                    MinX = _virtualX + (float)rnd.NextDouble() * (w - 400),
                    MinY = _virtualY + (float)rnd.NextDouble() * (h - 400),
                    MaxX = 0, MaxY = 0,
                };
                r.MaxX = r.MinX + 300; r.MaxY = r.MinY + 300;
                PatchInto(bmp, r);
            }
            swPatch.Stop();
            double patch = swPatch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"  {label,-12} | {after - before,6:F1}M | {full,11:F1}ms | {patch,12:F1}ms | {patch / 20,6:F2}ms");

            bmp.Dispose();
            Mem.TrimWorkingSet();
        }
        _quit = true;
    }

}
