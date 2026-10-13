// 本文件由 App.cs 拆出（2026-10-07）：BoardAndLaser 这一组。
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

    /// <summary>
    /// **白板底纹（方格 / 横线）自检**——用户 2026-09-17 要的，参考 InkClass。
    ///
    /// 判据是**数屏幕上的线**（不是看代码里那个开关）：
    ///   · 取一条横排像素 → 数"非板色"的段数 = 横着穿过几条**竖线**；
    ///   · 取一条竖排像素 → 段数 = 穿过几条**横线**。
    /// 于是：无底纹两个都是 0；横线只有横的；方格两个都有；间距变小线会变多。
    /// 最后再量一次**代价**：底纹是烘进分块缓存的，所以只有"换底纹那一下"要整层重铺，
    /// 之后每帧都不花钱——两个数都要报出来。
    /// </summary>
    private void PatternTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 白板底纹自检（方格 / 横线）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        BoardOn = true;
        BoardColor = InkPalette.BoardPresets[0].Color;     // 白板（浅色）
        SetBoardPatternFromUi(0, 40f);
        SettleFrames(400);

        // 取屏幕正中偏右下的一块（避开 HUD 和面板），看里面的线
        int px = (int)(_virtualX + _virtualW * 0.35f), py = (int)(_virtualY + _virtualH * 0.35f);
        const int W = 400, H = 400;

        // 一条横排 / 一条竖排：数"和板色不一样"的连续段数
        (int vLines, int hLines) CountLines()
        {
            var buf = ScreenProbe.CaptureRegion(px, py, W, H);
            if (buf.Length < W * H * 4) return (-1, -1);
            int Row(int y)                      // 固定 y 扫一行 → 竖线
            {
                int runs = 0, o = (y * W) * 4;
                bool inLine = false;
                for (int x = 0; x < W; x++)
                {
                    int i = o + x * 4;
                    bool lit = buf[i] < 240 || buf[i + 1] < 240 || buf[i + 2] < 240;
                    if (lit && !inLine) runs++;
                    inLine = lit;
                }
                return runs;
            }
            int Col(int x)                      // 固定 x 扫一列 → 横线
            {
                int runs = 0;
                bool inLine = false;
                for (int y = 0; y < H; y++)
                {
                    int i = (y * W + x) * 4;
                    bool lit = buf[i] < 240 || buf[i + 1] < 240 || buf[i + 2] < 240;
                    if (lit && !inLine) runs++;
                    inLine = lit;
                }
                return runs;
            }
            // 多取几条取中位数（怕某一条正好压在线上）
            var vs = new List<int>(); var hs = new List<int>();
            for (int k = 1; k <= 5; k++) { vs.Add(Row(H * k / 6)); hs.Add(Col(W * k / 6)); }
            vs.Sort(); hs.Sort();
            return (vs[vs.Count / 2], hs[hs.Count / 2]);
        }

        var (v0, h0) = CountLines();
        Check("无底纹：屏幕上一根线都没有", v0 == 0 && h0 == 0, $"竖线 {v0} 条 / 横线 {h0} 条");

        SetBoardPatternFromUi(2, 40f);                     // 横线
        SettleFrames(400);
        var (v2, h2) = CountLines();
        Check("横线：只有横的、没有竖的", v2 == 0 && h2 >= 2, $"竖线 {v2} 条 / 横线 {h2} 条");

        SetBoardPatternFromUi(1, 40f);                     // 方格
        SettleFrames(400);
        var (v1, h1) = CountLines();
        Check("方格：横竖都有", v1 >= 2 && h1 >= 2, $"竖线 {v1} 条 / 横线 {h1} 条");

        SetBoardPatternFromUi(2, 64f);                     // 粗间距
        SettleFrames(400);
        var (_, h64) = CountLines();
        SetBoardPatternFromUi(2, 24f);                     // 细间距
        SettleFrames(400);
        var (_, h24) = CountLines();
        Check("间距生效：细间距的线明显更多", h24 > h64 && h64 >= 1,
              $"24 逻辑像素 → {h24} 条，64 → {h64} 条");

        // ---- 代价：只有"换底纹那一下"要整层重铺，之后每帧都不花钱 ----
        double Measure(bool patternOn, int rounds = 8)
        {
            double total = 0;
            for (int i = 0; i < rounds; i++)
            {
                Doc.InvalidateAll();                        // 逼一次整层重铺
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                total += _windows[0].LastRebuildMs;
            }
            return total / rounds;
        }

        SetBoardPatternFromUi(1, 40f); SettleFrames(300);
        double withPattern = Measure(true);
        // 再量一次"什么都不用重铺"的那一帧：底纹烘在缓存里，这应该是 0
        RenderAll();
        double idleRebuild = _windows[0].LastRebuildMs;

        SetBoardPatternFromUi(0, 40f); SettleFrames(300);
        double withoutPattern = Measure(false);

        Console.WriteLine($"    [性能] 整层重铺一格：有底纹 {withPattern:F2} ms，无底纹 {withoutPattern:F2} ms"
                        + $"（差 {withPattern - withoutPattern:F2} ms）");
        Console.WriteLine($"    [性能] 底纹画好之后的空闲帧：分块光栅 {idleRebuild:F2} ms（应为 0）");
        Check("底纹画好之后，空闲帧不再重铺分块", idleRebuild < 0.05,
              $"空闲帧分块光栅 {idleRebuild:F2} ms");
        Check("整层重铺的代价在可接受范围（< 8 ms）", withPattern < 8.0,
              $"有底纹 {withPattern:F2} ms（这是**换底纹那一下**的一次性代价，不是每帧）");

        // ---- 白板透明度（用户 2026-09-17："隐约看见下面的题目，但是书写有干净"）----
        {
            (byte R, byte G, byte B) ScreenPixel(int x, int y)
            {
                var buf = ScreenProbe.CaptureRegion(x, y, 1, 1);
                return buf.Length < 4 ? ((byte)0, (byte)0, (byte)0) : (buf[2], buf[1], buf[0]);
            }

            int ox = (int)(_virtualX + _virtualW * 0.55f), oy = (int)(_virtualY + _virtualH * 0.75f);
            BoardColor = InkPalette.BoardPresets[2].Color;      // 黑板（深色）：和桌面（浅色）差得远，好判
            SetBoardPatternFromUi(0, 40f);

            SetBoardOpacityFromUi(1f);
            SettleFrames(400);
            var solid = ScreenPixel(ox, oy);
            var wantBoard = InkPalette.BoardPresets[2].Color;
            Check("不透明度 1.0：屏幕上就是板色本身（实心板）",
                  Math.Abs(solid.R - wantBoard.R * 255) < 6 && Math.Abs(solid.G - wantBoard.G * 255) < 6
                  && Math.Abs(solid.B - wantBoard.B * 255) < 6,
                  $"实测 ({solid.R},{solid.G},{solid.B})，板色 ({(int)(wantBoard.R * 255)},{(int)(wantBoard.G * 255)},{(int)(wantBoard.B * 255)})");

            SetBoardOpacityFromUi(0.5f);
            SettleFrames(400);
            var half = ScreenPixel(ox, oy);
            Check("不透明度 0.5：板面变浅（下面的东西透出来了）",
                  half.R > solid.R + 20 || half.G > solid.G + 20 || half.B > solid.B + 20,
                  $"实心 ({solid.R},{solid.G},{solid.B}) → 半透 ({half.R},{half.G},{half.B})");

            // **书写仍然干净**：板面半透明时，笔迹像素还是纯的（不跟着变淡）
            var mag = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 30f * DpiScale,
            };
            mag.AddPoint(ox - 100, oy + 160, 1f, 0);
            mag.AddPoint(ox + 100, oy + 160, 1f, 1);
            Doc.AddStroke(mag);
            SettleFrames(400);
            var inkPixel = ScreenPixel(ox, oy + 160);
            Check("板面半透明时，写上去的墨**仍然是实的**（不跟着变淡）",
                  inkPixel.R > 240 && inkPixel.G < 20 && inkPixel.B > 240,
                  $"墨色 ({inkPixel.R},{inkPixel.G},{inkPixel.B})（应为纯品红 255,0,255）");

            // 收尾：回到白板 + 实心 + 无底纹
            Doc.Clear();
            Doc.ClearHistory();
            BoardColor = InkPalette.BoardPresets[0].Color;
            SetBoardOpacityFromUi(1f);
            SettleFrames(250);
        }

        // 底纹不是笔迹：切底纹不该动文档
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
        };
        s.AddPoint(_virtualX + 500, _virtualY + 500, 1f, 0);
        s.AddPoint(_virtualX + 900, _virtualY + 560, 1f, 1);
        Doc.AddStroke(s);
        SettleFrames(200);
        int before = Doc.Strokes.Count;
        SetBoardPatternFromUi(2, 40f);
        Check("换底纹不动文档（底纹不是笔迹）",
              Doc.Strokes.Count == before && Doc.UndoDepth == 1,
              $"笔画 {before} → {Doc.Strokes.Count}，撤销栈 {Doc.UndoDepth}（还是 1，底纹不进撤销）");

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：方格/横线/间距都对，而且底纹是烘进缓存的（只在换的时候花一次钱）"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");

        Doc.Clear();
        Doc.ClearHistory();
        BoardOn = false;
        SetBoardPatternFromUi(0, 40f);
        _quit = true;
    }


    /// <summary>
    /// **激光笔自检**（2026-09-27）。用户那天要的是 ClassIn 那个叫「**拖拽激光笔**」的工具：
    /// "只要我一直写，它就不会消失；等我停止写，过几秒以后它就会消失"。
    ///
    /// 盯五件事（前三件正是**和旧行为不一样**的地方）：
    ///   ① **写过整条不缩**：一直写，最早那个点必须还在
    ///      （旧行为是 600ms 的滑动窗口，写着写着开头就一直在丢）；
    ///   ② **松手后停留 2 秒**再整体淡出（旧行为是每个点自己 600ms 过期）；
    ///   ③ **多条并存**：抬手再写一条，前一条还在淡出，不能被顶掉；
    ///   ④ **粗细滑条真的管用**：量屏幕上那条红带有多宽
    ///      （旧的那个滑条只管落点那个圆点，轨迹宽度是写死的 13 / 6）；
    ///   ⑤ 老规矩：它**不是笔迹**（不进文档、不进撤销）。
    ///
    /// ⚠ 底一定要用**白板**量：这正是把加法混合换成 SourceOver 的原因——
    ///   加法只加亮，"红 + 白 = 白"，在浅底上整条轨迹会看不见（量出来就是 0 个像素）。
    /// </summary>
    private void LaserTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 激光笔自检（拖尾寿命 / 多条并存 / 粗细 / 不进文档）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-36} {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        // **不挂界面**：这一条量的、拍的都只有激光（内容层），挂上面板反而会让
        // 抓屏图里多出一颗面板球（见下面出图那一段）。
        BoardOn = true;
        BoardColor = InkPalette.BoardPresets[0].Color;     // 白板（浅底）
        SettleFrames(400);

        Tool = Tool.Laser;
        Laser.Clear();
        // 把落点藏起来（别让它混进"数红像素"那一条里）
        PointerInside = false;

        // ---- ① 一直写：整条留着，最早那个点不许被淘汰 ----
        float bx = _virtualX + _virtualW * 0.30f, by = _virtualY + _virtualH * 0.40f;
        double t0 = NowMs;
        Laser.Begin(bx, by, t0, LaserWidthLogical);
        float firstX = Laser.Strokes[0].Points[0].X;
        for (int i = 1; i <= 200; i++)
            // 每一步横着走 6 像素（远大于采样门槛 2.5），所以 200 个点应该一个不少
            Laser.Add(bx + i * 6f, by + MathF.Sin(i * 0.12f) * 40f, t0 + i * 16.0);

        int pts = Laser.Strokes.Count > 0 ? Laser.Strokes[0].Points.Count : 0;
        Check("一直写：整条都留着（201 个点一个不少）", pts == 201, $"点数 {pts}（期望 201）");
        Check("最老那个点还在（旧行为是 600ms 窗口，开头一直在丢）",
              Laser.Strokes.Count > 0
              && MathF.Abs(Laser.Strokes[0].Points[0].X - firstX) < 0.01f
              && Laser.Strokes[0].Points.Count > 1,
              $"首点 x {Laser.Strokes[0].Points[0].X:F1}（写下时 {firstX:F1}）");

        // ---- ② 松手：先停 2 秒（一点不淡），之后才开始淡，再淡完就没 ----
        double rel = t0 + 200 * 16.0;
        Laser.Release(rel);
        Laser.Prune(rel + 1000);
        Check("松手 1 秒：还在，而且**一点没淡**",
              Laser.Strokes.Count == 1 && Laser.Strokes[0].Fade == 0f,
              $"条数 {Laser.Strokes.Count}，Fade {(Laser.Strokes.Count > 0 ? Laser.Strokes[0].Fade : -1f):F2}");

        Laser.Prune(rel + LaserTrail.HoldMs + LaserTrail.FadeMs * 0.5);
        Check("过了停留期：开始淡（Fade 在 0 和 1 之间）",
              Laser.Strokes.Count == 1 && Laser.Strokes[0].Fade > 0.2f && Laser.Strokes[0].Fade < 0.95f,
              $"Fade {(Laser.Strokes.Count > 0 ? Laser.Strokes[0].Fade : -1f):F2}（期望 0.5 上下）");

        Laser.Prune(rel + LaserTrail.HoldMs + LaserTrail.FadeMs + 50);
        Check($"停留 {LaserTrail.HoldMs / 1000:F0} 秒 ＋ 淡出 {LaserTrail.FadeMs / 1000:F1} 秒之后：没了",
              Laser.Strokes.Count == 0, $"条数 {Laser.Strokes.Count}");

        // ---- ③ 多条并存 ＋ **整批共用一个计时**（用户 2026-09-27 报的那个 bug）----
        //
        // 用户原话："我第一笔写完写第二笔，第二笔写完写第三笔，只要它还在写，
        // 第一笔就不会消失。等最后写完以后，它们才会一起消失，是这个逻辑。"
        // 以前是**每条各算各的**（`Stroke.ReleasedAtMs`），第 1 条抬手早，
        // 于是第 2 条还在写的时候它就已经到点淡掉了——下面第二条断言量的就是它。
        Laser.Clear();
        double t3 = NowMs;
        Laser.Begin(bx, by, t3, LaserWidthLogical);
        Laser.Add(bx + 120f, by, t3 + 16);
        Laser.Release(t3 + 32);                        // 第 1 条：抬手了，正在停留
        Laser.Begin(bx, by + 240f, t3 + 120, LaserWidthLogical);   // 第 2 条：新起一条（还没抬手）
        Laser.Add(bx + 120f, by + 240f, t3 + 136);
        Check("抬手再写一条：**两条并存**（前一条还在停留，没被顶掉）",
              Laser.Strokes.Count == 2, $"条数 {Laser.Strokes.Count}（期望 2）");

        double t3b = t3 + 120;                          // 第 2 条按下那一刻
        Laser.Prune(t3b + LaserTrail.HoldMs * 3);       // 时间推到"第 1 条抬手后 6 秒"
        Check("第 2 条还在写：第 1 条**一点都没淡**（整批共用一个计时）",
              Laser.Strokes.Count == 2 && Laser.Strokes[0].Fade == 0f,
              $"条数 {Laser.Strokes.Count}，第 1 条 Fade {(Laser.Strokes.Count > 0 ? Laser.Strokes[0].Fade : -1f):F2}（期望 0）");

        Laser.Release(t3b + LaserTrail.HoldMs * 3);     // 最后一条也抬手 → 整批开始计时
        Laser.Prune(t3b + LaserTrail.HoldMs * 3 + LaserTrail.HoldMs + LaserTrail.FadeMs * 0.5);
        Check("最后一条抬手后：两条**一起**淡（Fade 一模一样）",
              Laser.Strokes.Count == 2
              && MathF.Abs(Laser.Strokes[0].Fade - Laser.Strokes[1].Fade) < 0.01f
              && Laser.Strokes[0].Fade > 0.2f && Laser.Strokes[0].Fade < 0.95f,
              $"两条 Fade {(Laser.Strokes.Count == 2 ? $"{Laser.Strokes[0].Fade:F2} / {Laser.Strokes[1].Fade:F2}" : "—")}（期望 0.5 上下且相等）");

        Laser.Prune(t3b + LaserTrail.HoldMs * 4 + LaserTrail.FadeMs + 50);
        Check("再等一下：两条**一起**没", Laser.Strokes.Count == 0, $"条数 {Laser.Strokes.Count}");

        // ---- ④ 粗细滑条真的管用：量屏幕上那条红带多宽 ----
        float lx0 = _virtualX + _virtualW * 0.28f, lx1 = _virtualX + _virtualW * 0.72f;
        float ly = _virtualY + _virtualH * 0.62f;
        int BandPixels(float logicalWidth)
        {
            SetWidthFromUi(logicalWidth);              // 走的就是拖那个滑条那条路
            Laser.Clear();
            double tn = NowMs;
            Laser.Begin(lx0, ly, tn, logicalWidth);
            for (float x = lx0 + 4f; x <= lx1; x += 4f) Laser.Add(x, ly, tn);
            Laser.Release(tn);
            SettleFrames(220);                         // 画上去（还在停留期，不会淡）
            // 在带子正中间竖着数"偏红"的像素（红边、柔光都算；中间那道白芯不算）
            int cxp = (int)((lx0 + lx1) * 0.5f);
            var buf = ScreenProbe.CaptureRegion(cxp, (int)ly - 90, 1, 180);
            int cnt = 0;
            for (int i = 0; i + 3 < buf.Length; i += 4)
            {
                int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                if (b < 250 && r > g + 30 && r > b + 30) cnt++;
            }
            return cnt;
        }

        int thin = BandPixels(8f);                     // 现在这 3 档：8 / 14 / 22
        int thick = BandPixels(22f);
        Check("白板上看得见（加法混合会量出 0）", thin > 2, $"粗 8 时红带 {thin} 像素高");
        Check("粗细滑条**真的管用**（22 明显比 8 宽）",
              thick > thin * 1.8, $"粗 8 → {thin} 像素，粗 22 → {thick} 像素");

        // ---- ⑤ 不是笔迹 ----
        int strokesBefore = Doc.Strokes.Count, undoBefore = Doc.UndoDepth;
        Laser.Clear();
        double t5 = NowMs;
        Laser.Begin(lx0, ly, t5, LaserWidthLogical);
        Laser.Add(lx0 + 200f, ly + 60f, t5 + 16);
        Laser.Release(t5 + 32);
        SettleFrames(150);
        Check("激光轨迹**不是笔迹**（不进文档、不进撤销）",
              Doc.Strokes.Count == strokesBefore && Doc.UndoDepth == undoBefore,
              $"笔画 {strokesBefore} → {Doc.Strokes.Count}，撤销栈 {undoBefore} → {Doc.UndoDepth}");

        // ---- ⑥ 真按一遍鼠标：按下 → 拖 → 抬手（走引擎那条真路）----
        //
        // ⚠ 上面五条都是**直接调 `Laser` 那几个方法**，等于"我自己造了个对象、字段自己填"
        //   ——接线（`OnPointerDown → Begin`、`OnPointerUp → EndStroke → Release`）根本没测到。
        //   而这一轮最容易错的恰恰是接线：漏一个 `Release`，轨迹就永远"还在写"、永不淡出。
        Tool = Tool.Laser;
        SettleFrames(150);
        Laser.Clear();
        int mx = (int)(_virtualX + _virtualW * 0.30f), my = (int)(_virtualY + _virtualH * 0.28f);
        SendMouse(mx, my, 0);                              SettleFrames(60);
        SendMouse(mx, my, Native.MOUSEEVENTF_LEFTDOWN);    SettleFrames(60);
        for (int i = 1; i <= 20; i++)
        {
            SendMouse(mx + i * 12, my + i * 3, 0);
            SettleFrames(20);
        }
        SettleFrames(100);
        int livePts = Laser.Writing != null ? Laser.Writing.Points.Count : 0;
        Check("真按着鼠标拖：轨迹在长（说明按下那条接线通了）",
              livePts > 5, $"点数 {livePts}（期望 > 5）");

        SendMouse(mx + 240, my + 60, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(250);
        Check("真抬手：不再\"还在写\"了，进入停留期（说明抬手那条接线通了）",
              Laser.Writing == null && Laser.Strokes.Count >= 1 && Laser.Strokes[^1].Fade == 0f,
              $"正在写 = {(Laser.Writing == null ? "无（对）" : "还在写（错）")}，条数 {Laser.Strokes.Count}");

        // ---- 出图：摆样好不好看，自检判不了（摆三笔不同粗细，给人看）----
        Laser.Clear();
        double tg = NowMs;
        for (int k = 0; k < 3; k++)
        {
            float gw = k == 0 ? 8f : k == 1 ? 15f : 22f;   // 细 / 中 / 粗三档，给人看粗细范围
            SetWidthFromUi(gw);
            float sy = _virtualY + _virtualH * (0.30f + k * 0.18f);
            Laser.Begin(_virtualX + _virtualW * 0.16f, sy + 40f, tg, gw);
            for (int i = 1; i <= 60; i++)
                Laser.Add(_virtualX + _virtualW * (0.16f + i * 0.011f), sy - i * 1.1f, tg + i * 8.0);
            Laser.Release(tg + 16);
        }
        Laser.Prune(NowMs);            // 都在停留期，一个都不会掉
        SettleFrames(300);
        // ⚠ **不能用 `OffscreenShot`**：那条路只画**界面层**（面板），
        //   而激光画在**内容层**上（`Overlay.DrawLaser`）——出出来的图里只有面板、
        //   一条轨迹都没有（第一版就是这么白出一张的，看图才发现）。
        //   所以这里走"自己抓屏"，和 `--pixeleraseshow` 同一条路。
        int capX = (int)_virtualX, capY = (int)_virtualY;
        int capW = (int)_virtualW, capH = (int)_virtualH;
        bool shotOk = ScreenProbe.SaveBmp("reports/laser-trail.png", capX, capY, capW, capH);
        Console.WriteLine(shotOk
            ? "  已出图 reports/laser-trail.png（三笔不同粗细，白色底，自己抓屏）"
            : "  出图失败");

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：拖尾一直写不缩、松手停 2 秒再整体淡出（整批一起）、多条并存、粗细真的管用、而且不是笔迹"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");

        Laser.Clear();
        Doc.Clear();
        Doc.ClearHistory();
        BoardOn = false;
        SetWidthFromUi(8f);            // 激光的默认档（见 Engine.LaserWidthPresets）
        Tool = Tool.Pen;
        _quit = true;
    }


    /// <summary>
    /// Three long strokes, then a deliberately coarse vertical swipe with the
    /// eraser. Without path interpolation the sample spacing leaves gaps and
    /// strokes in between survive, which is the "eraser is not sensitive"
    /// symptom. Also checks the whole swipe collapses into one undo step.
    /// </summary>
    /// <summary>
    /// Draws the same wavy stroke at every width preset and measures how much
    /// ink actually lands. A filled ribbon can collapse if the winding rule
    /// cancels where the shape folds over itself, which shows up as a coverage
    /// ratio far below 1 - and it only appears once strokes get fat.
    /// </summary>
    /// <summary>
    /// 指针形状自检：把"设备 × 工具 × 悬停目标 × 是否拖拽"这张表逐条算出来，
    /// 跟期望值比对。
    ///
    /// 为什么值得写成自检：光标这类东西**漏一个状态很难靠肉眼发现**——
    /// 典型的是"拖拽中突然变回箭头"，只有真去拖一遍才会看到。这里把状态
    /// 直接构造出来，一条命令跑完全部组合。
    ///
    /// 只验"该显示什么"（纯计算，不碰系统）。"系统真的显示出来了"要另开
    /// 一个进程读 GetCursorInfo，那是端到端测试的事。
    /// </summary>
    private void CursorTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 指针形状自检（状态 → 光标）===");
        Console.WriteLine($"  系统指针尺寸 {CursorSizePx}px   DPI 缩放 {DpiScale:F2}");
        Console.WriteLine();

        int pass = 0, fail = 0;

        void Check(string name, CursorKind want)
        {
            var got = ComputeCursorKind();
            bool ok = got == want;
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} 期望 {Cursors.Name(want),-24} 实际 {Cursors.Name(got)}");
        }

        void CheckBool(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        // 准备一份真实的选区：三笔横线，全选。
        Doc.Clear();
        for (int i = 0; i < 3; i++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f * DpiScale };
            float y = 500 + i * 140;
            for (int k = 0; k <= 60; k++) s.AddPoint(600 + k * 14, y, 1f, NowMs);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();

        PassThrough = false;
        PointerInside = true;
        LastPointerType = Native.PT_MOUSE;
        Doc.Selected.Clear();
        Tool = Tool.Pen;
        PointerX = 100; PointerY = 100;

        // ---- 输入设备 × 工具 ----
        // 规则（2026-09-27 更新，用户报"手写板书写时光标消失"之后定的）：
        //   · 鼠标 / 手写板 → **一路给斜笔**（自绘彩笔：热点在笔尖、跟墨色/笔宽）；
        //   · 触摸屏自带的笔（笔尖就压在屏幕上）→ 藏系统光标，落点由自绘环表达，
        //     落笔后连环也不画（笔尖本身就是最准的落点）；
        //   · 触摸 → 什么都不显示。
        // 判据是 PenDeviceOnScreen（设备出身：INTEGRATED_PEN vs EXTERNAL_PEN，见引擎注释）。
        Check("画笔 · 鼠标悬停（斜笔）", CursorKind.Pen);
        CheckBool("画笔 · 鼠标下不叠自绘环（斜笔本身就是落点）",
            DrawnCursor == ToolCursorShape.None, $"DrawnCursor={DrawnCursor}");

        LastPointerType = Native.PT_PEN;
        PenDeviceOnScreen = false;      // 手写板：笔尖在板子上，不在屏幕里
        Check("画笔 · 手写板悬停（斜笔）", CursorKind.Pen);
        CheckBool("画笔 · 手写板悬停不叠自绘环",
            DrawnCursor == ToolCursorShape.None, $"DrawnCursor={DrawnCursor}");

        PenDeviceOnScreen = true;       // 触摸屏自带笔：笔尖即落点
        Check("画笔 · 触屏笔悬停（藏系统光标，落点自己画）", CursorKind.Hidden);
        CheckBool("画笔 · 触屏笔悬停画圆环",
            DrawnCursor == ToolCursorShape.Ring, $"DrawnCursor={DrawnCursor}");

        PenDeviceOnScreen = false;      // 之后默认按手写板走（谁要测触屏笔谁自己设 true）
        LastPointerType = Native.PT_TOUCH;
        Check("触摸（不显示指针）", CursorKind.Hidden);

        LastPointerType = Native.PT_MOUSE;
        Tool = Tool.Highlighter;
        Check("荧光笔 · 悬停", CursorKind.Hidden);
        CheckBool("荧光笔 · 落点是圆盘（直径 = 笔宽）",
            DrawnCursor == ToolCursorShape.Disc,
            $"DrawnCursor={DrawnCursor}");

        Tool = Tool.Eraser;
        Check("橡皮 · 悬停", CursorKind.Hidden);
        CheckBool("橡皮 · 落点是圆环（半径跟着橡皮半径走）",
            DrawnCursor == ToolCursorShape.Ring && DrawnCursorRadius > 0f,
            $"DrawnCursor={DrawnCursor} 半径={DrawnCursorRadius:F0}px");

        // ---- 笔尾橡皮（倒持的笔）：悬停就要按橡皮显示（用户 2026-09-27 报的）----
        //
        // 落笔分派早就按"倒持 = 橡皮"走了（OnPointerDown / OnPointerMove 里的
        // `inverted ? Tool.Eraser : Tool`），光标当时没跟上——笔尾悬停在画布上，
        // 屏幕上还是斜笔 / 笔尖环，看不出"这一头是橡皮、会擦掉多大一块"。
        // 现在光标这一族问的是 EffectiveTool（判据只写一处）。
        Tool = Tool.Pen;
        LastPointerType = Native.PT_PEN;
        PenDeviceOnScreen = false;                 // 手写板的笔
        LastPointerInverted = true;                // 倒过来拿：笔尾（橡皮头）朝下
        Check("笔尾（倒持）· 悬停 = 橡皮光标（藏起来，落点自己画）", CursorKind.Hidden);
        CheckBool("笔尾 · 落点是圆环（不是斜笔）",
            DrawnCursor == ToolCursorShape.Ring, $"DrawnCursor={DrawnCursor}");
        CheckBool("笔尾 · 圆环半径 = 橡皮半径（和真的会擦掉的范围一致）",
            Math.Abs(CursorOuterRadius - EraserRadius) < 0.01f,
            $"外圈 {CursorOuterRadius:F1}px，橡皮半径 {EraserRadius:F1}px");
        _drawing = true;                           // 正用笔尾擦着
        CheckBool("笔尾 · 擦除中圆环一直画", DrawnCursor == ToolCursorShape.Ring, $"{DrawnCursor}");
        _drawing = false;
        LastPointerInverted = false;               // 笔正过来拿：立刻回到斜笔
        Check("笔正过来 · 悬停回到斜笔", CursorKind.Pen);
        CheckBool("笔正过来 · 不再画圆环", DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");

        LastPointerType = Native.PT_PEN;
        Tool = Tool.Pen;
        PenDeviceOnScreen = true;
        CheckBool("触屏笔 · 悬停时笔画环", DrawnCursor == ToolCursorShape.Ring, $"DrawnCursor={DrawnCursor}");
        PenDeviceOnScreen = false;
        CheckBool("手写板 · 悬停时给斜笔、不画环",
            DrawnCursor == ToolCursorShape.None, $"DrawnCursor={DrawnCursor}");
        LastPointerType = Native.PT_MOUSE;

        Tool = Tool.Laser;
        Check("激光笔 · 悬停", CursorKind.Hidden);
        CheckBool("激光笔 · 落点是实心点",
            DrawnCursor == ToolCursorShape.Dot, $"DrawnCursor={DrawnCursor}");
        Tool = Tool.Marquee;
        Check("框选 · 空白处", CursorKind.Cross);

        // ---- 截图 vs 框选/图形：光标怎么分 ----
        //
        // 2026-09-17 那位用户要的是"截图和框选用不同的光标"（当时给了取景框角括号）。
        // **8.3.1 起照微信改了**：截图 = **系统十字**，不再自绘角括号
        // （用户 2026-09-30 真机反馈："微信截图之后鼠标一直是那个样子，
        //   没有切换成我们现在那个比较难用的光标"）。
        // 和框选分开的任务交给**取景时那两条全屏准线**（见 Overlay.DrawCaptureOverlay），
        // 光标本身不动——"从哪儿开始"也就永远看得见。
        Tool = Tool.Capture;
        Check("截图 · 系统十字（不再自绘角括号）", CursorKind.Cross);
        CheckBool("截图 · 不画自绘落点",
            DrawnCursor == ToolCursorShape.None, $"DrawnCursor={DrawnCursor}");
        _drawing = true;
        CheckBool("截图 · 拖动中也不自绘（准线画在截图那一层）",
            DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        _drawing = false;
        Tool = Tool.Rectangle;
        Check("图形 · 仍是十字准星", CursorKind.Cross);
        CheckBool("图形 · 不画自绘落点", DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        Tool = Tool.Marquee;

        // ---- 书写中：**手写板的光标不能消失**（用户 2026-09-27 报的手感 bug）----
        _drawing = true;
        LastPointerType = Native.PT_MOUSE;
        Tool = Tool.Pen;         CheckBool("鼠标书写中 · 斜笔一路跟着（不叠自绘环）", DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        Tool = Tool.Highlighter; CheckBool("鼠标书写中 · 圆盘跟着走", DrawnCursor == ToolCursorShape.Disc, $"{DrawnCursor}");
        Tool = Tool.Laser;       CheckBool("鼠标书写中 · 点跟着走", DrawnCursor == ToolCursorShape.Dot, $"{DrawnCursor}");

        // 手写板：笔尖在板子上、不在屏幕里，**屏幕上必须留着指示**——
        // 这一组就是这次用户报的 bug（"光标消失、不流畅"）的回归钉子。
        LastPointerType = Native.PT_PEN;
        PenDeviceOnScreen = false;
        Tool = Tool.Pen;
        Check("手写板书写中 · 斜笔光标留着（不消失）", CursorKind.Pen);
        CheckBool("手写板书写中 · 斜笔就是指示、不叠自绘环",
            DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        Tool = Tool.Highlighter;
        CheckBool("手写板书写中 · 荧光笔圆盘留着（宽度要看得见）",
            DrawnCursor == ToolCursorShape.Disc, $"{DrawnCursor}");
        Tool = Tool.Laser;
        CheckBool("手写板书写中 · 激光点留着", DrawnCursor == ToolCursorShape.Dot, $"{DrawnCursor}");

        // 触屏笔：笔尖就压在屏幕上，落笔后不画（保持 2026-09 的定案：笔尖即落点）
        PenDeviceOnScreen = true;
        Tool = Tool.Pen;         CheckBool("触屏笔落笔后 · 不画（笔尖即落点）", DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        Tool = Tool.Highlighter; CheckBool("触屏笔落笔后 · 荧光笔也不画", DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
        PenDeviceOnScreen = false;
        Tool = Tool.Eraser;      CheckBool("橡皮擦除中 · 一直画（范围要看得见）", DrawnCursor == ToolCursorShape.Ring, $"{DrawnCursor}");
        _drawing = false;
        LastPointerType = Native.PT_MOUSE;

        // ---- 粗细 → 落点反馈 ----
        // ⚠ 2026-09-27 起：鼠标 / 手写板改用系统斜笔，**自绘环这条路只剩触屏笔**——
        // 所以这一段显式按"触屏笔"测（环的内圈 / 外圈 / 脏区仍然要跟着笔宽走）。
        LastPointerType = Native.PT_PEN;
        PenDeviceOnScreen = true;
        Tool = Tool.Pen;
        // 取**档位表里的**最细 / 最粗两档，不写死数字：写死的话表一改（2026-09-27
        // 最细档就从 1.5 降到了 1）这里会静默量错值（同"写死常量会随功能移位而静默失效"那条教训）。
        PenWidthLogical = WidthPresets[0];
        float thinTrue = CursorRingTrueRadius, thinOuter = CursorOuterRadius;
        float thinDirty = DrawnCursorRadius;
        PenWidthLogical = WidthPresets[^1];
        float fatTrue = CursorRingTrueRadius, fatOuter = CursorOuterRadius;
        CheckBool("笔 · 内圈跟着真实笔宽变",
            fatTrue > thinTrue * 10f,
            $"{WidthPresets[0]} → {thinTrue:F1}px，{WidthPresets[^1]} → {fatTrue:F1}px（半径）");
        CheckBool("笔 · 细笔有外圈兜底（看得见落点）",
            thinOuter > thinTrue && Math.Abs(thinOuter - 6f * DpiScale) < 0.01f,
            $"真半径 {thinTrue:F1}px，外圈 {thinOuter:F1}px（下限 {6f * DpiScale:F1}px）");
        CheckBool("笔 · 粗笔时内外圈合一",
            fatOuter <= fatTrue + 0.01f, $"外圈 {fatOuter:F1}px，真半径 {fatTrue:F1}px");
        CheckBool("笔 · 脏区跟着落点反馈放大",
            DrawnCursorRadius > thinDirty, $"{thinDirty:F0}px → {DrawnCursorRadius:F0}px");
        LastPointerType = Native.PT_MOUSE;
        PenDeviceOnScreen = false;

        float penBefore = PenWidthLogical, hlBefore = HighlighterWidthLogical;
        Tool = Tool.Highlighter;
        HighlighterWidthLogical = 8f;
        float thinDisc = CursorRingTrueRadius;
        float thinDiscDirty = DrawnCursorRadius;
        HighlighterWidthLogical = 32f;
        CheckBool("荧光笔 · 圆盘半径就是半个笔宽",
            Math.Abs(thinDisc - 8f * DpiScale * 0.5f) < 0.01f
            && Math.Abs(CursorRingTrueRadius - 32f * DpiScale * 0.5f) < 0.01f,
            $"8 → 半径 {thinDisc:F1}px，32 → {CursorRingTrueRadius:F1}px");
        CheckBool("荧光笔 · 脏区盖得住圆盘（含描边）",
            DrawnCursorRadius > CursorRingTrueRadius,
            $"脏区 {DrawnCursorRadius:F0}px > 圆盘 {CursorRingTrueRadius:F0}px");
        CheckBool("荧光笔 · 脏区跟着荧光笔宽变",
            DrawnCursorRadius > thinDiscDirty,
            $"{thinDiscDirty:F0}px → {DrawnCursorRadius:F0}px（脏区半径）");
        HighlighterWidthLogical = hlBefore;

        Tool = Tool.Laser;
        // ⚠ 设值要**连索引一起设**：切粗细（CycleWidth）是按索引走的，只改
        // LaserWidthLogical 不改 LaserWidthIndex 的话，后面"切粗细"那条用例会撞上
        // 同一个值直接判红（2026-09-27 把档位改成 8/14/22 时就这么红过一次）。
        LaserWidthIndex = 0;
        LaserWidthLogical = LaserWidthPresets[0];            // 最细档
        float thinDot = DrawnCursorRadius;
        LaserWidthIndex = LaserWidthPresets.Length - 1;
        LaserWidthLogical = LaserWidthPresets[^1];           // 最粗档
        CheckBool("激光笔 · 点跟着激光宽变",
            DrawnCursorRadius > thinDot, $"{thinDot:F0}px → {DrawnCursorRadius:F0}px");

        // ---- 切粗细改的是**当前工具**那一档（这条是 bug 回归）----
        Tool = Tool.Laser;
        float laserBefore = LaserWidthLogical;
        CycleWidth();
        CheckBool("切粗细 · 激光改的是激光宽", LaserWidthLogical != laserBefore
                  && Math.Abs(PenWidthLogical - penBefore) < 1e-4f,
                  $"激光 {laserBefore} → {LaserWidthLogical}，笔宽保持 {PenWidthLogical}");
        Tool = Tool.Highlighter;
        float hlBefore2 = HighlighterWidthLogical;
        CycleWidth();
        CheckBool("切粗细 · 荧光笔改的是荧光笔宽", HighlighterWidthLogical != hlBefore2
                  && Math.Abs(PenWidthLogical - penBefore) < 1e-4f,
                  $"荧光笔 {hlBefore2} → {HighlighterWidthLogical}，笔宽保持 {PenWidthLogical}");
        Tool = Tool.Pen;
        float penBefore2 = PenWidthLogical;
        float hlAfterCycle = HighlighterWidthLogical;
        CycleWidth();
        CheckBool("切粗细 · 笔改的是笔宽", Math.Abs(PenWidthLogical - penBefore2) > 1e-4f
                  && Math.Abs(HighlighterWidthLogical - hlAfterCycle) < 1e-4f,
                  $"笔 {penBefore2} → {PenWidthLogical}");
        // 复位成常用值，后面的用例不受影响（激光要连索引一起复位——见上面那条警告；
        // 写死的 4f 已经不是合法档位了，会留下"值不在档位表里"的隐性不一致）
        PenWidthLogical = 3f; HighlighterWidthLogical = 18f;
        LaserWidthIndex = 0; LaserWidthLogical = LaserWidthPresets[0];

        // ---- 选中框：八个手柄 + 旋转 + 整体拖动 + 操作条 ----
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;

        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;
        var aabb = frame.CanvasAabb;

        void At(SelHandle h)
        {
            var p = SelectionHandles.CanvasPosition(h, frame, dpi);
            PointerX = p.X; PointerY = p.Y;
        }

        At(SelHandle.Left);        Check("选中框 · 左边中点（左右拉伸）", CursorKind.ResizeWE);
        At(SelHandle.Right);       Check("选中框 · 右边中点（左右拉伸）", CursorKind.ResizeWE);
        At(SelHandle.Top);         Check("选中框 · 上边中点（上下拉伸）", CursorKind.ResizeNS);
        At(SelHandle.Bottom);      Check("选中框 · 下边中点（上下拉伸）", CursorKind.ResizeNS);
        At(SelHandle.TopLeft);     Check("选中框 · 左上角（对角）", CursorKind.ResizeNWSE);
        At(SelHandle.BottomRight); Check("选中框 · 右下角（对角）", CursorKind.ResizeNWSE);
        At(SelHandle.TopRight);    Check("选中框 · 右上角（对角）", CursorKind.ResizeNESW);
        At(SelHandle.BottomLeft);  Check("选中框 · 左下角（对角）", CursorKind.ResizeNESW);
        At(SelHandle.Rotate);      Check("选中框 · 旋转手柄", CursorKind.Rotate);

        PointerX = (aabb.MinX + aabb.MaxX) * 0.5f;
        PointerY = (aabb.MinY + aabb.MaxY) * 0.5f;
        Check("选中框 · 框内（整体移动）", CursorKind.Move);

        var b0 = SelectionHandles.BarButtonRect(0, aabb, dpi, ViewportCanvas);
        PointerX = (b0.MinX + b0.MaxX) * 0.5f;
        PointerY = (b0.MinY + b0.MaxY) * 0.5f;
        Check("操作条按钮（不换光标）", CursorKind.Default);

        // **按下中（_drawing=true）也要保持箭头**——用户 2026-09-27 报的
        // "悬浮过去是鼠标、点一下它又变成十字了"。
        // 根因：`OnPointerDown` 在"这一下归谁"分流**之前**就设了 `_drawing = true` 并调了
        // ApplyCursor，按下的那一帧光标落到 ToolCursorKind（框选 = 十字）。
        // 这里把那个瞬间的状态直接构造出来钉住：框选工具 + 按下中 + 指针在条上。
        _drawing = true;
        Check("操作条 · 按下中仍是箭头（不是十字）", CursorKind.Default);
        _drawing = false;

        // ---- 操作条**整块**都是"界面"（用户 2026-09-17 报的）----
        //
        // "操作对应图标的时候一会是十字光标，一会是箭头图标"——原来只认按钮本体，
        // 按钮之间的分隔线、两头的留白都退回工具光标（框选是十字），
        // 鼠标在条上横着滑过去就是 箭头／十字／箭头／十字。
        {
            var bar = SelectionHandles.BarRect(aabb, dpi, ViewportCanvas);
            // 条**尾部那段留白**（最后一格右边到条右端）：一定不在任何按钮上。
            // 注：九格是**紧挨着**排的（BarGapLogical = 0，靠分隔线分区），所以
            // "两格之间"其实是共用一条边，那里算前一个按钮；真正露在外面的
            // 只有条两端的 6 逻辑像素留白，也正是原来会漏出十字的地方。
            var last = SelectionHandles.BarButtonRect(SelectionHandles.BarButtonCount - 1,
                                                      aabb, dpi, ViewportCanvas);
            PointerX = (last.MaxX + bar.MaxX) * 0.5f;
            PointerY = (bar.MinY + bar.MaxY) * 0.5f;
            CheckBool("（先确认那个点真的不在按钮上）",
                SelectionHandles.BarButtonAt(PointerX, PointerY, aabb, dpi, ViewportCanvas) == -1
                && PointerX < bar.MaxX, "不在按钮上、但在条里");
            Check("操作条 · 条内留白（箭头，不是十字）", CursorKind.Default);

            // 挂在下头的小面板：整块也是界面，面板的空白处同样是箭头
            SelPanelOpen = SelPanel.Layer;
            var pnl = SelectionHandles.LayerPanelRect(aabb, dpi, ViewportCanvas);
            PointerX = pnl.MinX + 3f; PointerY = pnl.MaxY - 3f;      // 面板的内边距
            Check("层级面板 · 面板空白处（箭头）", CursorKind.Default);
            var cell0 = SelectionHandles.LayerCellRect(0, aabb, dpi, ViewportCanvas);
            PointerX = (cell0.MinX + cell0.MaxX) * 0.5f;
            PointerY = (cell0.MinY + cell0.MaxY) * 0.5f;
            Check("层级面板 · 置顶那格（箭头）", CursorKind.Default);
            SelPanelOpen = SelPanel.None;

            // 收起态那颗圆钮也一样
            SelBarCollapsed = true;
            var dot = SelectionHandles.BarCollapsedRect(aabb, dpi, ViewportCanvas);
            PointerX = (dot.MinX + dot.MaxX) * 0.5f;
            PointerY = (dot.MinY + dot.MaxY) * 0.5f;
            Check("操作条收起态的圆钮（箭头）", CursorKind.Default);
            SelBarCollapsed = false;
        }

        PointerX = aabb.MinX - 120f * dpi; PointerY = aabb.MinY - 120f * dpi;
        Check("选中框外的空白（重新框选）", CursorKind.Cross);

        // ---- 图库面板（自绘界面块）：光标**不许取决于"来路"**（2026-09-27 修）----
        //
        // 病根：图库面板不在"接输入小窗"里，它的悬停分支以前只调 ApplyCursor、
        // 没有任何地方更新"指针在界面上"这个状态，于是同一块面板上的光标取决于**从哪边进来**：
        //   从工具条过来（_uiHover=true）→ 箭头 ✔；从画布直入（_uiHover=false）→ 工具光标（十字）✘。
        // 现在判据收成一条 PointerOnDrawnChrome（纯几何），两种情况必须一样：箭头 + 不画落点。
        {
            LibraryPanelOpen = true;
            var panel = LibraryPanelRectNow();
            PointerX = panel.MinX + 12f; PointerY = panel.MinY + 12f;    // 面板里（左上内边距处）

            // ① 框选工具：画布上是十字，最容易暴露"来路差异"——面板里必须是箭头
            Tool = Tool.Marquee;
            _uiHover = false;                          // 从画布直入（bug 现场）
            Check("图库面板 · 框选工具直入也是箭头", CursorKind.Default);
            _uiHover = true;                           // 从工具条进入（老路，钉住"两条路一致"）
            Check("图库面板 · 从工具条进入还是箭头", CursorKind.Default);

            // ② 橡皮：画布上**恒画圆环**，最能验"落点反馈在面板上被收起来"
            Tool = Tool.Eraser;
            _uiHover = false;
            CheckBool("图库面板 · 从画布直入不画圆环",
                DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");
            _uiHover = true;
            CheckBool("图库面板 · 从工具条进入也不画圆环",
                DrawnCursor == ToolCursorShape.None, $"{DrawnCursor}");

            _uiHover = false;
            Tool = Tool.Marquee;                       // 还原（后面的段落都按框选工具写）
            CloseLibraryPanel();                       // 走真实关闭路径（顺带复位 _onDrawnChrome）
        }

        // ---- 拖拽中：用按下那一刻的语义，不重新命中测试 ----
        PointerX = 100; PointerY = 100;         // 指针早就离开手柄了
        _drawing = true;
        SelDragging = true;
        _dragIsMove = false;
        _dragHandle = SelHandle.Left;
        Check("拖拽中 · 左右拉伸（指针已离开手柄）", CursorKind.ResizeWE);
        _dragHandle = SelHandle.Rotate;
        Check("拖拽中 · 旋转", CursorKind.Rotate);
        _dragIsMove = true;
        _dragHandle = SelHandle.None;
        Check("拖拽中 · 整体移动", CursorKind.Move);
        _drawing = false; SelDragging = false; _dragIsMove = false; _dragHandle = SelHandle.None;

        // ---- 滚动条 / 穿透 ----
        ScrollBarHover = true;
        Check("滚动条悬停（滑块自己变粗，不换光标）", CursorKind.Default);
        ScrollBarHover = false;

        PassThrough = true;
        Check("穿透模式（交给下层窗口）", CursorKind.Leave);
        PassThrough = false;

        // ---- 自绘光标能不能真的建出来 ----
        var hRot = Cursors.HandleFor(CursorKind.Rotate, CursorSizePx);
        var hHide = Cursors.HandleFor(CursorKind.Hidden, CursorSizePx);
        CheckBool("旋转光标建立成功（位图/热点没写错）", hRot != IntPtr.Zero,
            $"句柄 0x{hRot:X}  {CursorSizePx}px");
        CheckBool("隐藏光标建立成功", hHide != IntPtr.Zero, $"句柄 0x{hHide:X}");

        // ---- 斜笔（系统 IDC_PEN）：句柄能取到、热点真在笔尖 ----
        //
        // "不挡视线"全靠热点在笔尖——取错光标序号（比如落到箭头/十字上）时，
        // 屏幕上会变成"笔杆压在落点上"，肉眼第一时间很难发现，所以钉两个数：
        // 句柄非零 + 热点 (0,0)。完整形状的验证在 tmp/penprobe（导出大图看）。
        // ⚠ 2026-09-27 晚试过"自绘彩笔版"（跟墨色/跟笔宽），用户看过真机后否掉了
        //   （"填充颜色有点难看，原来那支就挺好"）——**已撤回**，这里保住"用的是系统那支"。
        var hPen = Cursors.HandleFor(CursorKind.Pen, CursorSizePx);
        CheckBool("斜笔建立成功（IDC_PEN 系统光标）", hPen != IntPtr.Zero, $"句柄 0x{hPen:X}");
        var (penHotX, penHotY) = Cursors.HotspotOf(hPen);
        CheckBool("斜笔热点在笔尖（左上角 0,0；别处会挡视线）",
            penHotX == 0 && penHotY == 0, $"热点=({penHotX},{penHotY})");
        CheckBool("斜笔用的是**系统那支**（不是自绘的彩笔）",
            hPen == Cursors.System(Native.IDC_PEN), $"句柄 0x{hPen:X}");

        // ---- 光标"设过就不重设"的缓存**必须会被作废**（用户 2026-09-27 报的 bug）----
        //
        // 病根：穿透（`CursorKind.Leave`，指针归下层窗口）和**改窗口扩展样式**这两处
        // 会让屏幕上的光标变成箭头，而我们那句"同一个句柄不重复设"只认自己记的数——
        // 于是之后所有"设成同一个"的请求全被跳过，**光标卡在箭头上**。
        // 用户的原话："切成穿透再切回画笔，还是三角形；切荧光笔更明显（箭头 + 圆盘一起出现）；
        // 多切几遍有时候又能切回来"。
        // 这两条就是那个 bug 的回归钉子：放手之后缓存必须归零。
        {
            LastPointerType = Native.PT_MOUSE;
            Tool = Tool.Pen;
            PassThrough = false;
            ApplyCursor(force: true);
            CheckBool("（准备）画笔下光标已设成斜笔",
                CursorAppliedForTest == hPen, $"已设 0x{CursorAppliedForTest:X}");

            PassThrough = true;
            ApplyCursor();                       // 穿透：把指针交给下层窗口
            CheckBool("穿透时：放手 + 缓存作废（下次才会真设）",
                CursorAppliedForTest == IntPtr.Zero, $"缓存 0x{CursorAppliedForTest:X}");

            PassThrough = false;
            ApplyCursor();                       // 退出穿透：非 force 也必须真设回去
            CheckBool("退出穿透后：光标真的设回斜笔（不再卡在箭头上）",
                CursorAppliedForTest == hPen, $"已设 0x{CursorAppliedForTest:X}");

            // 换穿透（真实入口：它会**改窗口扩展样式**，系统顺手把光标恢复成箭头）
            // ——缓存同样必须作废，否则用户看到的还是那个箭头。
            SetPassThroughFromUi(true);
            CheckBool("开穿透（改样式）后：缓存作废",
                CursorAppliedForTest == IntPtr.Zero, $"缓存 0x{CursorAppliedForTest:X}");
            SetPassThroughFromUi(false);
            CheckBool("关穿透后：光标真设回斜笔（一条路走通，不再卡箭头）",
                CursorAppliedForTest == hPen, $"已设 0x{CursorAppliedForTest:X}");
        }

        // 自绘光标导出成图，供人眼检查形状与抗锯齿。
        try
        {
            Directory.CreateDirectory("reports");
            string dump = Path.Combine("reports", "cursor-rotate.bmp");
            Cursors.DumpRotate(dump, CursorSizePx);
            // 再导一张大的：实际尺寸只有 64px，放大看才能判断形状和抗锯齿。
            Cursors.DumpRotate(Path.Combine("reports", "cursor-rotate-big.bmp"), 128, 3);
            Console.WriteLine($"  （旋转光标已导出：{dump}，放大 4 倍）");
            Console.WriteLine("  旋转光标圆周采样（0°=右 90°=下 180°=左 270°=上，缺口应在右上）：");
            Console.WriteLine("    " + Cursors.RotateRingReport(CursorSizePx));
            int stray = Cursors.RotateStrayPixels(CursorSizePx);
            CheckBool("旋转光标 · 圆环外没有多余墨点（箭头没跑位）", stray == 0, $"环外墨点 {stray} 个");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  旋转光标导出失败：" + ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        _quit = true;
    }


    /// <summary>
    /// **分块缓存自检**：接缝、内容正确性、滚动复用、内存上界。
    ///
    /// 分块缓存有一类特别难查的 bug——**接缝**：笔画跨在块边界上时，如果
    /// 有一边的块没把这条笔画画进去，就会缺一条边；而"平时看着好好的、
    /// 只有粗笔画压在边界上才露出来"，靠肉眼很难碰到。
    ///
    /// 这里用"同一支笔，摆在块边界上 vs 摆在块正中间"做对照：墨量必须一样。
    /// 边界上少墨 = 接缝，多墨 = 重复绘制。
    /// </summary>
    private void TileTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 分块缓存自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        const int T = 256;                                  // 块边长，和 CanvasTileCache 一致
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        // ---- 1) 粗横线压在横向块边界上 ----------------------------------
        // 边界取 y = 3T = 768（画布坐标 = 屏幕坐标，因为相机为 0、原点为 0）。
        float w2 = 40f;
        int onSeam = DrawAndCountHorizontal(_virtualX + 300, 3 * T, 1200, w2);
        int midTile = DrawAndCountHorizontal(_virtualX + 300, 3 * T + T / 2, 1200, w2);
        int diffY = Math.Abs(onSeam - midTile);
        Check("横线压在块边界上不缺墨", onSeam > 40000 && diffY < midTile * 0.03,
              $"边界 {onSeam} vs 块中间 {midTile}（差 {diffY}，允许 {midTile * 0.03:F0}）");

        // ---- 2) 粗竖线压在纵向块边界上 ----------------------------------
        int onSeamX = DrawAndCountVertical(4 * T, _virtualY + 300, 1200, w2);
        int midTileX = DrawAndCountVertical(4 * T + T / 2, _virtualY + 300, 1200, w2);
        int diffX = Math.Abs(onSeamX - midTileX);
        Check("竖线压在块边界上不缺墨", onSeamX > 40000 && diffX < midTileX * 0.03,
              $"边界 {onSeamX} vs 块中间 {midTileX}（差 {diffX}，允许 {midTileX * 0.03:F0}）");

        // ---- 3) 跨块的长笔画：一整条都得在 ------------------------------
        // 从画面左上角一路斜到右下角，横跨十几个块。
        Doc.Clear();
        Doc.ClearHistory();
        var longStroke = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 2f * DpiScale,
        };
        for (int i = 0; i <= 40; i++)
        {
            float t = i / 40f;
            longStroke.AddPoint(_virtualX + 200 + t * 2400, _virtualY + 200 + t * 1400, 1f, i);
        }
        Doc.AddStroke(longStroke);
        SettleFrames(500);
        // 沿线取 6 段采样，每段都得有墨（中间任何一块漏画，就会有一段是空的）
        int emptySpots = 0;
        for (int k = 0; k < 6; k++)
        {
            float t = (k + 0.5f) / 6f;
            int n = ScreenProbe.CountMagenta(
                (int)(_virtualX + 200 + t * 2400) - 60, (int)(_virtualY + 200 + t * 1400) - 60, 120, 120);
            if (n < 50) emptySpots++;
        }
        Check("跨十几个块的长笔画不断线", emptySpots == 0, $"6 段采样里有 {emptySpots} 段是空的");

        // ---- 4) 滚下去再滚回来：墨量必须一模一样，而且回程不重画 -------
        int before = ScreenProbe.CountMagenta(_virtualX, _virtualY, _virtualW, _virtualH);
        var w0 = _windows[0];
        int rasterDown = 0, rasterUp = 0;
        for (int k = 0; k < 4; k++) { HandleWheel(Wheel(-120)); RenderAll(); rasterDown += w0.LastPatchCount; }
        for (int k = 0; k < 4; k++) { HandleWheel(Wheel(120)); RenderAll(); rasterUp += w0.LastPatchCount; }
        SettleFrames(300);
        int after = ScreenProbe.CountMagenta(_virtualX, _virtualY, _virtualW, _virtualH);

        Check("滚下去再滚回来画面一致", Math.Abs(after - before) <= before * 0.005 && before > 5000,
              $"{before} -> {after}");
        Check("回程复用了块缓存（没有重画）", rasterUp == 0,
              $"去程光栅 {rasterDown} 块，回程 {rasterUp} 块");

        // ---- 5) 常驻块数不超过预算 --------------------------------------
        Check("常驻分块不超预算", w0.LastTileCount <= Math.Max(w0.LastTileBudget, 1),
              $"{w0.LastTileCount} 块 / 预算 {w0.LastTileBudget}（每块 256KB）");

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：块边界无缝、跨块笔画完整、回程免重画、内存有上界");
        Console.WriteLine();
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    // 分块测试的滚轮构造（和 WheelTest 一致：delta 在高 16 位）


    /// <summary>
    /// **空闲预取自检**（2026-10-04，对应上游 Xournal++ 的页面预载 / Rnote 的视口余量预渲染）。
    ///
    /// 判据：滚到之前，视口外那一圈已经烘好——所以"再滚一格"这一帧**一块都不用光栅**
    /// （`LastPatchCount == 0`）。还有关掉预取的对照（同一动作必须重画），
    /// 自证这条测试有效，不是"永远通过"的摆设。
    /// </summary>
    private void PrefetchTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 分块空闲预取自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        bool saved = CanvasTileCache.PrefetchEnabled;
        CanvasTileCache.PrefetchEnabled = true;
        try
        {
            // 垫内容：画布要超过一屏，滚得动；笔迹铺到 y≈3000。
            Doc.Clear();
            Doc.ClearHistory();
            for (int k = 0; k < 8; k++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = PenColor, Width = 3f * DpiScale,
                };
                float yy = _virtualY + 300f + k * 380f;
                for (int j = 0; j < 24; j++)
                    s.AddPoint(_virtualX + 320f + j * 90f, yy + MathF.Sin(j * 0.5f) * 60f, 0.5f, j * 8f);
                Doc.AddStroke(s);
            }
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
            RenderAll();
            SettleFrames(300);

            var w0 = _windows[0];

            // 滚两格（正常同步光栅），停。
            for (int k = 0; k < 2; k++) { HandleWheel(Wheel(-120)); RenderAll(); }
            SettleFrames(120);

            // ① 环里确实有欠着的块（不成立说明测试前提没了，别当成通过）
            bool needBefore = w0.PrefetchNeeded();
            Check("滚完停下：视口外一圈有欠着的块", needBefore, $"PrefetchNeeded={needBefore}");

            // ② 跑预取拍直到没有活
            int steps = 0, prefetched = 0;
            while (w0.PrefetchNeeded() && steps < 400)
            {
                w0.PrefetchStep(this);
                prefetched += w0.LastPrefetch;
                steps++;
            }
            Check("预取把环烘完（拍数有界）", steps < 400, $"{steps} 拍、共 {prefetched} 块");
            Check("预取确实烘了块（不是空转）", prefetched > 0, $"{prefetched} 块");

            // ③ 再滚一格：新露出来的那一行应该已经热了，这一帧一块都不用光栅。
            HandleWheel(Wheel(-120));
            RenderAll();
            Check("再滚一格：不需要重新光栅（预取命中）", w0.LastPatchCount == 0,
                  $"本帧光栅 {w0.LastPatchCount} 块");

            // ④ 对照：关掉预取，继续往下滚——**滚出预取环之后必须重画**（证明这条测试真的在测东西）。
            CanvasTileCache.PrefetchEnabled = false;
            int controlRaster = 0;
            for (int k = 0; k < 8 && controlRaster == 0; k++)
            {
                HandleWheel(Wheel(-120));
                RenderAll();
                controlRaster += w0.LastPatchCount;
            }
            Check("对照（关预取）：滚出预取环后要重画", controlRaster > 0, $"共光栅 {controlRaster} 块");

            // ⑤ 关预取后不再产生空闲拍
            bool needOff = w0.PrefetchNeeded();
            Check("关预取后不再产生空闲拍", !needOff, $"PrefetchNeeded={needOff}");

            // ⑥ 预算把环算进去了：常驻 ≤ 预算（否则预取的块会被 Trim 淘汰）
            Check("常驻块数含预取环后仍不超预算", w0.LastTileCount <= Math.Max(w0.LastTileBudget, 1),
                  $"{w0.LastTileCount} 块 / 预算 {w0.LastTileBudget}");
        }
        finally
        {
            CanvasTileCache.PrefetchEnabled = saved;
        }

        Doc.Clear();
        Doc.ClearHistory();
        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：空闲预取把新露出的一行提前烘好、不超预算");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>画一条粗横线并数它的墨像素。横线的**中心线**画在 y 上。</summary>


    private void GhostTest(bool scrolled = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 残影测试（大跨度快速移动橡皮光标，看会不会拖尾）===");

        if (SkipIfNoSyntheticInput("残影检测（需要合成鼠标移动光标）")) { _quit = true; return; }

        Doc.Clear();
        Doc.InvalidateAll();
        Tool = Tool.Eraser;
        if (scrolled)
        {
            ViewOffsetY = -_virtualH * 0.5f;      // 把相机挪开：画布坐标 ≠ 屏幕坐标
            Console.WriteLine($"  （滚动过：ViewOffsetY = {ViewOffsetY:F0}，画布坐标与屏幕坐标不再相等）");
        }
        SettleFrames(300);

        // 先把光标停在远处（在测试带之外），拍一张"干净"的基准图
        float parkX = _virtualX + 150, parkY = _virtualY + _virtualH - 200;
        SendMouse((int)parkX, (int)parkY, 0);
        SettleFrames(500);

        int bandX = _virtualX + 400, bandY = _virtualY + 780;
        int bandW = 2000, bandH = 200;

        // 先量一遍"环境噪声"：光标停着不动，连拍两张。
        // 桌面上的别的程序（聊天窗口、光标闪烁）本来就会自己变化，
        // 不先量这个底噪，就会把它当成我们的残影。这一步让测试有自证能力。
        byte[] noise1 = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);
        SettleFrames(500);
        byte[] noise2 = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);
        int noise = ScreenProbe.DiffCount(noise1, noise2);

        byte[] before = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);

        float y = _virtualY + _virtualH * 0.5f;
        float x0 = _virtualX + 450;
        SendMouse((int)x0, (int)y, 0);
        SettleFrames(200);

        // 每次跳 300 像素、间隔很短：模拟快速划动
        const int hops = 6;
        byte[] during = null;
        double dirtyPercent = -1;
        var perHop = new List<string>();
        for (int i = 1; i <= hops; i++)
        {
            SendMouse((int)(x0 + i * 300f), (int)y, 0);
            SettleFrames(20);
            if (_windows.Count > 0)
                perHop.Add($"{i}:{_windows[0].LastPresentRectCount}块/{_windows[0].LastPresentAreaPercent:F1}%");
            if (i == 3)
            {
                // 扫到一半时停下拍一张：用来证明"光标确实画出来了"。
                // 少了这一步，光标因为脏区算错而**根本没画**的情况也会被判成
                // "没有残影"——测试就成了永远通过摆设。
                SettleFrames(300);
                during = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);
                dirtyPercent = _windows.Count > 0 ? _windows[0].LastPresentAreaPercent : -1;
            }
        }

        // 光标回到原处，等脏区排空，再拍一张
        SendMouse((int)parkX, (int)parkY, 0);
        SettleFrames(500);
        byte[] after = ScreenProbe.CaptureRegion(bandX, bandY, bandW, bandH);

        int diff = ScreenProbe.DiffCount(before, after);
        int visible = during != null ? ScreenProbe.DiffCount(before, during) : -1;
        Console.WriteLine($"  光标扫过区域前后逐像素差异: {diff} 像素（共 {bandW * bandH} 像素）");
        Console.WriteLine($"  对照：同一区域静置两帧的差异: {noise} 像素（环境噪声）");
        Console.WriteLine($"  扫过途中光标是否真的画出来了: {visible} 像素（应当 > 1000）");
        // 顺带记一下上屏面积：光标只是一小块，这里却是"光标 ∪ 滚动条那条竖带"的
        // 并集，所以数值偏大是正常的——只作为观察，不作判据。
        Console.WriteLine($"  扫过途中的上屏脏区: {dirtyPercent:F1}%（含滚动条竖带的并集，仅作观察）");
        Console.WriteLine("  每跳一格的脏区: " + string.Join("  ", perHop));

        string shot = Path.Combine("reports", "shots", "ghosttest.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shot));
        ScreenProbe.SaveBmp(shot, _virtualX, _virtualY, _virtualW, _virtualH);
        Console.WriteLine($"  截图: {Path.GetFullPath(shot)}");

        // 光标已经离开这块区域，像素应当复原。
        //
        // 判据用"我们造成的差异"而不是绝对差异：这块区域里别的程序自己也会变
        // （实测聊天窗口刷文字能刷出上千个像素），所以要先减掉环境噪声。
        // 容差 200 像素留给抗锯齿和噪声本身的抖动。
        int ours = Math.Max(0, diff - noise);
        bool drew = visible > 1000;
        bool ok = ours < 200 && drew;
        Console.WriteLine(ok
            ? $"  PASS: 光标画出来了，且没有残影（扣掉环境噪声后仅剩 {ours} 像素）"
            : !drew
                ? $"  FAIL: 光标压根没画出来（只差 {visible} 像素）——脏区算错了"
                : $"  FAIL: 出现拖尾残影（扣掉环境噪声后仍有 {ours} 像素）");
        _quit = true;
    }


    // =====================================================================
    //  Self test
    // =====================================================================

    private void SelfTest(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== self test ===");
        Console.WriteLine("drawing a synthetic stroke across the primary monitor...");

        var probe = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 14f };
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.3f;
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            probe.AddPoint(cx - 400 + t * 800, cy + MathF.Sin(t * 6.28f) * 120, 0.8f, NowMs);
        }
        Doc.AddStroke(probe);
        _dirty = true;

        RenderAll();

        // Let DirectComposition settle, then verify the pixels really reached the screen.
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 700)
        {
            PumpMessages();
            RenderAll();
        }

        bool found = ScreenProbe.TryFindMagenta((int)(cx - 420), (int)(cy - 160), 840, 320, out int hits);
        Console.WriteLine(found
            ? $"PASS: overlay pixels verified on screen ({hits} matching pixels)"
            : $"FAIL: no overlay pixels found on screen ({hits} matches)");
        LastCaptureReport = found ? "rendering verified" : "rendering NOT verified";

        Console.WriteLine();
        Console.WriteLine("running the 10k-stroke benchmark...");
        Benchmark(10_000);

        Console.WriteLine("laser stress: forcing a continuous 120 Hz trail...");
        // 一条**一直写**的长轨迹（2026-09-27 起激光笔"手写期间整条都留着"，
        // 所以这里量的正是"点最多、最贵"的那一种：它会一直涨到 3 秒长）。
        Laser.Begin(cx - 300, cy + 400, _clock.Elapsed.TotalMilliseconds, LaserWidthLogical);
        var t0 = _clock.Elapsed.TotalMilliseconds;
        while (_clock.Elapsed.TotalMilliseconds - t0 < 3000)
        {
            double t = _clock.Elapsed.TotalMilliseconds;
            float px = cx - 300 + (float)(Math.Sin(t / 300.0) * 300);
            float py = cy + 400 + (float)(Math.Cos(t / 220.0) * 120);
            Laser.Add(px, py, t);
            NowMs = t;
            Laser.Prune(NowMs);
            PumpMessages();
            RenderAll();
        }
        Laser.Clear();

        Console.WriteLine();
        Console.WriteLine("--- summary ---");
        Console.WriteLine($"  rendering verified : {LastCaptureReport}");
        Console.WriteLine($"  fps                : {_fps:F1}");
        Console.WriteLine($"  record per frame   : {_lastRecordMs:F2} ms");
        Console.WriteLine($"  present per frame  : {_lastPresentMs:F2} ms");
        Console.WriteLine($"  full rebuild       : {_lastRebuildMs:F1} ms");
        Console.WriteLine($"  working set        : {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1} MB");
        Console.WriteLine($"  peak working set   : {_peakWorkingSetMb:F1} MB");
        Console.WriteLine();
    }

}
