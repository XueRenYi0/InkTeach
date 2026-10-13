// 本文件由 App.cs 拆出（2026-10-07）：Navigation 这一组。
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

    // =====================================================================
    //  直角掉色自检
    // =====================================================================


    /// <summary>
    /// 相机自检 + 滚动步代价。
    ///
    /// 验两件事：
    ///   ① **正确性**：偏移之后，同一块画布内容必须出现在屏幕上偏移后的位置，
    ///      而**文档里的坐标一个都不变**——这是"滚动只改一个数、不动对象数据"的直接证据。
    ///   ② **代价**：滚动一步 = 整屏重画（第一步的已知边界）。按"一屏量级"与
    ///      "累积量级"分别量，就知道现在能扛到多少。
    /// </summary>
    private void CameraTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 相机自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-20}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float cx = _virtualX + 900, cy = _virtualY + 700;
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 20f * DpiScale,   // 品红，好数像素
        };
        s.AddPoint(cx - 200, cy, 1f, 0);
        s.AddPoint(cx + 200, cy, 1f, 0);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(400);

        int before = ScreenProbe.CountMagenta((int)cx - 60, (int)cy - 60, 120, 120);
        Check("偏移前在画布坐标处", before > 200, $"{before} 像素");

        float shift = 600f;
        ViewOffsetY = -shift;                        // 往下滚：内容上移
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }
        Doc.InvalidateAll();
        SettleFrames(400);

        int afterOld = ScreenProbe.CountMagenta((int)cx - 60, (int)cy - 60, 120, 120);
        int afterNew = ScreenProbe.CountMagenta((int)cx - 60, (int)(cy - shift) - 60, 120, 120);
        Check("原屏幕位置已清空", afterOld < 40, $"{afterOld} 像素");
        Check("墨到了偏移后的位置", afterNew > 200, $"{afterNew} 像素");
        Check("文档坐标未变", Math.Abs(s.Bounds.MinY - cy) < 0.01f,
              $"Bounds.MinY={s.Bounds.MinY:F1}");

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        SettleFrames(400);
        int back = ScreenProbe.CountMagenta((int)cx - 60, (int)cy - 60, 120, 120);
        Check("滚回去仍在原位", back > 200, $"{back} 像素");
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：相机只改一个数，对象数据不动");

        ScrollBench();

        ViewOffsetY = 0f;
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 滚动代价：**走真实的滚轮路径**（HandleWheel），一格渲染一帧。
    ///
    /// 两条纪律：
    ///   · 不直接给 ViewOffsetY 赋值——那样绕开了 HandleWheel 的算术，上一轮
    ///     就是因此漏掉了"滚轮符号写反"这个 bug（还是用户报的）。
    ///   · 内容**铺开 8 屏**，不是塞在一屏里。真实板书是纵向累积的，塞一屏
    ///     是"满屏"的极端形状，衡量不了分块缓存要解决的"累积量"问题。
    ///
    /// 看三个数：每格平均帧耗时、最慢一格、以及这一格光栅化了几块。
    /// 分块缓存成立的标志是：**帧耗时与文档总量基本无关**——新露出来的
    /// 只有那么一小条，和文档里已经有多少笔没关系。
    /// </summary>


    /// <summary>
    /// 滚轮**算术**的自检。
    ///
    /// 这条是补课的：之前所有滚动测试都直接给 ViewOffsetY 赋值，
    /// **从没走过 HandleWheel 里那段加减**，于是"符号写反、往下滚永远滚不动"
    /// 这个 bug 一直没被测出来——还是用户报的。
    ///
    /// 直接构造 WM_MOUSEWHEEL 的 wParam（高 16 位是 delta）喂进去：
    ///   往下滚 delta = -120，ViewOffsetY 必须**变负**（内容上移）
    ///   往上滚到顶，必须夹在 0，不能越过
    /// </summary>
    private void WheelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 滚轮算术自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        // delta 放在 wParam 的高 16 位（低 16 位是按键状态，这里给 0）
        IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
        float step = 72f * DpiScale;

        HandleWheel(Wheel(-120));
        Check("往下滚一格应向下", ViewOffsetY < -1f, $"ViewOffsetY={ViewOffsetY:F0}（应为 -{step:F0}）");
        Check("一格正好一个步长", Math.Abs(ViewOffsetY + step) < 0.6f, $"{ViewOffsetY:F0}");

        HandleWheel(Wheel(-120));
        HandleWheel(Wheel(-120));
        Check("连滚三格累加", Math.Abs(ViewOffsetY + step * 3f) < 1.2f, $"{ViewOffsetY:F0}");

        HandleWheel(Wheel(120));
        Check("往上滚一格回升", Math.Abs(ViewOffsetY + step * 2f) < 1.2f, $"{ViewOffsetY:F0}");

        for (int i = 0; i < 8; i++) HandleWheel(Wheel(120));
        Check("滚到顶夹在 0，不越过", Math.Abs(ViewOffsetY) < 0.01f, $"{ViewOffsetY:F0}");

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：滚轮方向与夹取都正确");
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }


    /// <summary>
    /// **整屏翻页自检（一屏 = 一页）**。取向与出处见 [调研-白板翻页.md]。
    ///
    /// 关键是盯着"**没有新概念**"这件事：桌面/白板这一侧**不做** OneNote 那种
    /// "每页一个文档"——它还是**同一张连续的长纸**，只让相机按整屏跳。
    /// 所以这一套用例全在验：
    ///   · 页高 = 视口高，翻一屏 = 相机正好走一屏（翻完不留半行字）；
    ///   · **翻页前后，文档里没有一个坐标发生变化**（只有相机动）——这是"没做分页"的证据；
    ///   · 到顶就停、往下永远还有一屏（无限纸的红利）；
    ///   · 滚轮仍然是细粒度（翻页不该把滚轮改成"一格一屏"）；
    ///   · 翻完屏幕上真的换了内容（屏幕取点，和 --cameratest 同一套手法）。
    ///
    /// ⚠ 2026-09-26 起要分清**两条路**：这里是**桌面/白板这一侧**（连续长纸、永不隔离）；
    /// PPT 放映那一侧的"每页一套、完全隔离"是另一条路，由 `--ppttest` 钉
    /// （同一份 `FlipPage`/相机代码，但切的是"页槽"，见 Model.cs 的 PageSlot）。
    /// </summary>
    private void PageTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 整屏翻页自检（一屏 = 一页）===");

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
        Doc.InvalidateAll();
        SettleFrames(250);

        float pageH = _virtualH;                       // 物理像素：页高 = 视口高

        // ---- ① 页高 ----
        Check("页高 = 视口高", Math.Abs(PageHeightCanvas - _virtualH) < 0.01f,
              $"页高 {PageHeightCanvas:F0}，视口高 {_virtualH}");
        Check("第 1 屏的页顶 = 虚拟桌面顶",
              Math.Abs(PageTopCanvas - _virtualY) < 0.01f,
              $"页顶 {PageTopCanvas:F0}，桌面顶 {_virtualY}");

        // ---- ② 翻一屏 = 相机正好走一屏 ----
        Check("起手在第 1 屏、且不能再往上翻",
              ScreenIndex == 1 && !CanFlipPageUp,
              $"屏号 {ScreenIndex}，能上翻 = {CanFlipPageUp}");

        bool went = FlipPage(true);
        SettleFrames(400);                             // 等完那 167ms 的缓动
        Check("往下翻返回 true", went, $"返回 {went}");
        Check("翻一屏 = 相机正好走一屏", Math.Abs(ViewOffsetY + pageH) < 0.6f,
              $"相机 {ViewOffsetY:F1}（应为 {-pageH:F0}）");
        Check("翻完在第 2 屏", ScreenIndex == 2, $"屏号 {ScreenIndex}");
        Check("到第 2 屏后就能往上翻了", CanFlipPageUp, $"能上翻 = {CanFlipPageUp}");

        // ---- ③ 往下永远还有一屏（连翻 6 次都成）----
        int flipped = 1;
        for (int i = 0; i < 6; i++)
        {
            if (!FlipPage(true)) break;
            flipped++;
            SettleFrames(250);
        }
        Check("下一屏永远可用（连翻 6 次都成）", flipped == 7,
              $"成功 {flipped} 次，屏号 {ScreenIndex}");

        // ---- ④ 回来；到顶后再上翻必须"什么都不做" ----
        for (int i = 0; i < 10 && CanFlipPageUp; i++) { FlipPage(false); SettleFrames(250); }
        Check("连翻回顶：相机夹在 0，不越过", Math.Abs(ViewOffsetY) < 0.01f,
              $"相机 {ViewOffsetY:F2}");
        bool upAtTop = FlipPage(false);
        SettleFrames(200);
        Check("到顶了再上翻：返回 false 且相机不动",
              !upAtTop && Math.Abs(ViewOffsetY) < 0.01f,
              $"返回 {upAtTop}，相机 {ViewOffsetY:F2}");

        // ---- ⑤ 翻页前后，文档里一个坐标都不变（证明"没做分页"）----
        // 铺开三屏、每屏三行——量级对齐一节真实板书。
        Doc.Clear();
        Doc.ClearHistory();
        for (int screen = 0; screen < 3; screen++)
            for (int row = 0; row < 3; row++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.12f, 0.13f, 0.16f, 1f), Width = 6f,
                };
                float y = _virtualY + screen * _virtualH + 200f + row * 260f;
                for (int i = 0; i <= 20; i++)
                    s.AddPoint(_virtualX + 240f + i * 55f, y + (i % 4) * 8f, 1f, i * 8);
                Doc.AddStroke(s);
            }
        Doc.InvalidateAll();
        SettleFrames(200);

        string Dump()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Doc.Strokes)
            {
                sb.Append((int)s.Tool).Append('/').Append((int)s.Kind).Append(':');
                foreach (var p in s.Points)
                    sb.Append(p.X.ToString("F3")).Append(',').Append(p.Y.ToString("F3")).Append(';');
                sb.Append('|');
            }
            return sb.ToString();
        }

        string coords0 = Dump();
        int strokes0 = Doc.Strokes.Count;
        FlipPage(true); SettleFrames(300);
        FlipPage(true); SettleFrames(300);
        FlipPage(false); SettleFrames(300);
        FlipPage(true); SettleFrames(300);
        string coords1 = Dump();
        Check("翻来翻去：对象数没变", Doc.Strokes.Count == strokes0,
              $"{strokes0} → {Doc.Strokes.Count} 条");
        Check("翻来翻去：**每一个坐标都没变**", coords1 == coords0,
              coords1 == coords0 ? $"{coords0.Length} 字符逐字符一致（只有相机动）"
                                 : "有坐标被改动了 —— 翻页不该碰文档");

        // ---- ⑥ 滚轮仍是细粒度（一格 72 逻辑像素，不是一格一屏）----
        IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
        for (int i = 0; i < 12 && CanFlipPageUp; i++) { FlipPage(false); SettleFrames(250); }
        SettleFrames(150);
        float camBefore = ViewOffsetY;
        HandleWheel(Wheel(-120));
        SettleFrames(120);
        float wheelStep = 72f * DpiScale;
        Check("滚轮还是细粒度（一格 72 逻辑像素）",
              Math.Abs(ViewOffsetY - camBefore + wheelStep) < 1.2f,
              $"走了一格 = {camBefore - ViewOffsetY:F0} 物理像素（应为 {wheelStep:F0}，一屏是 {pageH:F0}）");
        HandleWheel(Wheel(120));                       // 滚回去
        SettleFrames(120);

        // ---- ⑥.5 ←→：**没选中时也是翻页**（2026-10-05 用户定）----
        // 有选中 → 微调（那一半在 --seltest 里验）；这里只钉"空选区 = 上一屏 / 下一屏"。
        {
            Doc.Selected.Clear();
            SettleFrames(80);
            float cam0 = ViewOffsetY;
            RunActionForTest(KeyAction.NudgeRight);
            SettleFrames(420);                         // 等完 167ms 缓动
            Check("空选区按 → = 下一屏（和 PageDown 同一条路）",
                  Math.Abs(ViewOffsetY - (cam0 - pageH)) < 1.5f,
                  $"相机 {cam0:F0} → {ViewOffsetY:F0}（应走 {-pageH:F0}）");
            RunActionForTest(KeyAction.NudgeLeft);
            SettleFrames(420);
            Check("空选区按 ← = 上一屏", Math.Abs(ViewOffsetY - cam0) < 1.5f,
                  $"相机 {ViewOffsetY:F0}");
        }

        // ---- ⑦ 翻完屏幕上真的换了内容（屏幕取点）----
        // 先放一笔**第 1 屏**的洋红墨：它必须看得见——既当判据，也当"取屏可用"的探针。
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        // 两笔**故意放在不同的屏幕高度**，这样"没翻动"和"翻动了"才有区别：
        //   第 1 屏那笔 → 相机为 0 时在屏幕 y=400；翻下去之后跑到屏幕外（负号），
        //   第 2 屏那笔 → 相机为 0 时在屏幕外，翻下去之后出现在屏幕 y=1000。
        // （第一版把两笔放在**同一个屏幕位置**，结果"翻没翻"取到的像素数一模一样，
        //   即使相机根本没动也会通过——这种"测不出区别"的用例比没有还坏。）
        float px = _virtualX + 900f;
        float y1 = _virtualY + 400f;                 // 第 1 屏
        float y2 = _virtualY + _virtualH + 1000f;    // 第 2 屏，翻下去之后落在屏幕 y=1000
        foreach (float cy in new[] { y1, y2 })
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 26f * DpiScale,
            };
            s.AddPoint(px - 220f, cy, 1f, 0);
            s.AddPoint(px + 220f, cy, 1f, 1);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(400);

        int BandAt(float screenY) => ScreenProbe.CountMagenta((int)px - 260, (int)screenY - 40, 520, 80);
        float homeProbeY = _virtualY + 400f, secondProbeY = _virtualY + 1000f;

        int probe = BandAt(homeProbeY);
        if (probe < 300)
        {
            // 锁屏 / 远程桌面 / 别的窗口盖住时取不到像素：这条只能跳过，不能算失败。
            Console.WriteLine($"  [跳过] 第一屏的墨自己都没取到（{probe} 像素）——"
                            + "屏幕取点这时候不可用（锁屏 / 远程 / 被盖住），这一条不判红绿");
        }
        else
        {
            int secondBefore = BandAt(secondProbeY);
            Check("相机为 0 时：第 2 屏那笔不该在屏幕上", secondBefore < 40,
                  $"{secondBefore} 像素（第 2 屏那笔此时在屏幕外）");

            FlipPage(true);
            SettleFrames(400);
            int firstAfter = BandAt(homeProbeY);
            int secondAfter = BandAt(secondProbeY);
            Check("翻下去：第 1 屏那笔离开屏幕", firstAfter < 40,
                  $"{firstAfter} 像素（原来 400 处那笔现在在屏幕外）");
            Check("翻下去：第 2 屏那笔出现在屏幕上", secondAfter > 300,
                  $"{secondAfter} 像素（落在屏幕 1000 处）");

            FlipPage(false);
            SettleFrames(400);
            int backHome = BandAt(homeProbeY);
            Check("翻回来：第 1 屏那笔还在原处", backHome > 300, $"{backHome} 像素");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：整屏翻页正确（页高 = 视口高、只动相机、到顶就停、往下无限、滚轮仍是细粒度）"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");

        // ---- ⑧ 清空之后**留在原地**（用户 2026-09-17 问："如果我的焦点在第 12 页，
        //        点击清空以后要不要回到第一页？"）----
        //
        // 结论：**不回**。清空是"这一屏重新来"，不是"回到开头"：
        // 老师在第 12 屏写完一题、点清空，就是要在**这一屏**接着写下一题；
        // 把相机弹回去等于让他再翻十几次，而且下一次落笔的位置也错了
        // （我们一直守"点下去的东西别动"，锚点、按钮位置都按它办）。
        // 真想从头看，点"上一屏"或拖右缘滚动条就行。
        //
        // 这一条要**钉在自检里**，因为"清空之后镜子一照全变"这种事很容易被以后某次
        // 改动带歪（比如"顺手把相机归零"）。
        {
            Doc.Clear();
            Doc.ClearHistory();
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
            Doc.InvalidateAll();
            SettleFrames(250);

            FlipPage(true); SettleFrames(300);
            FlipPage(true); SettleFrames(300);
            int pageKept = ScreenIndex;
            float camKept = ViewOffsetY;

            for (int i = 0; i < 3; i++)
            {
                var st = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0f, 0f, 0f, 1f), Width = 8f,
                };
                st.AddPoint(600 + i * 60, 300 + i * 40, 1f, 0);
                st.AddPoint(900 + i * 60, 340 + i * 40, 1f, 1);
                Doc.AddStroke(st);
            }
            SettleFrames(250);
            ClearFromUi();
            SettleFrames(400);

            Check("清空之后：**还停在第 3 屏**，相机一动不动",
                  ScreenIndex == pageKept && MathF.Abs(ViewOffsetY - camKept) < 0.5f,
                  $"屏号 {pageKept} → {ScreenIndex}，相机 {camKept:F0} → {ViewOffsetY:F0}"
                  + $"（文档 {Doc.Strokes.Count} 笔）");
            Check("清空之后：还能接着在这一屏写",
                  Doc.Strokes.Count == 0,
                  $"笔画 {Doc.Strokes.Count}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        _quit = true;
    }


    /// <summary>
    /// **坐标不变量测试**——随机相机偏移下反复验同一句话：
    ///
    ///   在画布 P 处写一笔 → 屏幕上 P+相机 处必须有墨。
    ///
    /// 为什么要有它：前面三个 bug（滚下去写的看不见 / 滚完写不了 / 闪一下就没了）
    /// 都是"画布坐标和窗口坐标混用"，而且每个都是**用户碰出来的**。
    /// 一个一个追太慢，这条测试把它们一次性网住：
    /// 它走的是**真实交互路径**（AddStroke 不调 InvalidateAll → DrawOnlyPatch），
    /// 并且随机换相机偏移——偏移一大，任何忘了换算的地方都会露馅。
    ///
    /// 这一类测试行业里叫"不变量测试"（invariant／属性测试）：不写死具体场景，
    /// 只断言"无论参数取什么值，这条性质都必须成立"。
    /// </summary>
    private void CoordTest(int rounds)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 坐标不变量测试（{rounds} 个随机相机偏移）===");
        Console.WriteLine("    偏移 |     期望处 |   未偏移处 | 结果");
        Console.WriteLine("  -------|------------|------------|------");

        int pass = 0, fail = 0;
        var detail = new List<string>();
        var rnd = new Random(20260913);

        for (int k = 0; k < rounds; k++)
        {
            Doc.Clear();
            Doc.ClearHistory();

            float shift = -(float)(rnd.NextDouble() * 3000.0 + 150.0);   // 往下滚 150~3150
            ViewOffsetY = shift;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = shift; }
            Doc.InvalidateAll();
            SettleFrames(150);

            // 挑一个**屏幕可见**的位置，反推它对应的画布坐标（就是输入路径做的事）
            float sx = _virtualX + 600f + (float)rnd.NextDouble() * 700f;
            float sy = _virtualY + 300f + (float)rnd.NextDouble() * 700f;
            float cx = sx, cy = sy;
            ScreenToCanvas(ref cx, ref cy);

            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 24f * DpiScale,
            };
            s.AddPoint(cx - 150f, cy, 1f, 0);
            s.AddPoint(cx + 150f, cy, 1f, 0);
            Doc.AddStroke(s);              // 真实路径，别加 InvalidateAll
            SettleFrames(200);

            int atExpected = ScreenProbe.CountMagenta((int)sx - 90, (int)sy - 90, 180, 180);
            int atRaw = ScreenProbe.CountMagenta((int)sx - 90, (int)(sy - shift) - 90, 180, 180);
            bool ok = atExpected > 300;

            if (!ok && detail.Count < 3)
                Console.WriteLine($"      [诊断] 补画块数 {_windows[0].LastAppendedTiles}"
                                + $" 累计补画 {_windows[0].TotalAppendedTiles}"
                                + $" 未命中补画 {_windows[0].LastAppendMissed}"
                                + $" err={_windows[0].LastError}"
                                + $" 文档 {Doc.Strokes.Count} 条 画布 y={cy:F0}");

            if (ok) pass++; else { fail++; detail.Add($"偏移{shift:F0}：期望处 {atExpected} 像素（应 >300），画布 y={cy:F0}"); }
            if (k < 12 || !ok)
                Console.WriteLine($"  {shift,6:F0} | {atExpected,10} | {atRaw,10} | {(ok ? "PASS" : "FAIL")}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {pass}/{rounds} 通过");
        foreach (var d in detail) Console.WriteLine("    " + d);
        Console.WriteLine(fail == 0
            ? "  PASS：随机偏移下，画布上写在哪、屏幕上就该出现在哪 —— 全都成立"
            : $"  FAIL：{fail} 个偏移下不成立 —— 坐标换算有遗漏");

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }


    /// <summary>
    /// 滚下去还能不能写。
    ///
    /// 做法：在三个滚动位置（顶部 / 往下三屏）各在**当下的屏幕位置**写一笔，
    /// 然后回到顶部，验证只有第一笔在视野里、后两笔确实留在了下面。
    ///
    /// 这同时回答"滚动要不要下限"：数据上**不设限**才是对的（老师往下写不完），
    /// 真正要解决的是"怎么回来"——无限往下滚而没有回顶部的办法，老师会迷路。
    /// </summary>
    private void ScrollWriteTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 滚下去还能不能写 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-24}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();

        float px = _virtualX + 900, py = _virtualY + 700;      // 屏幕上的固定位置
        float[] offsets = { 0f, -1800f, -5400f };              // 顶部 / 一屏 / 三屏
        var writtenCanvasY = new List<float>();

        for (int i = 0; i < offsets.Length; i++)
        {
            ViewOffsetY = offsets[i];
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }

            // 输入路径做的事：屏幕坐标 -> 画布坐标
            float cx = px, cy = py;
            ScreenToCanvas(ref cx, ref cy);
            writtenCanvasY.Add(cy);

            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 20f * DpiScale,
            };
            s.AddPoint(cx - 200, cy, 1f, 0);
            s.AddPoint(cx + 200, cy, 1f, 0);
            Doc.AddStroke(s);      // 真实交互路径：不调 InvalidateAll，
                                    // 走"脏区 → 标脏分块 → 只重画碰到的那几块"
            SettleFrames(350);

            int seen = ScreenProbe.CountMagenta((int)px - 60, (int)py - 60, 120, 120);
            Check($"滚动 {offsets[i],7:F0} 处能写", seen > 200,
                  $"画布 y={cy:F0}，屏幕上有 {seen} 像素");
            if (seen <= 200)
            {
                // 诊断：墨到底画到哪去了？沿屏幕竖着扫几条带子。
                Console.Write("      竖扫结果：");
                for (int band = 0; band < 6; band++)
                {
                    int yy = _virtualY + band * 300;
                    int n2 = ScreenProbe.CountMagenta((int)px - 200, yy, 400, 300);
                    Console.Write($"y={band * 300,4}→{n2,5}  ");
                }
                Console.WriteLine();
            }
        }

        Check("三笔记录在三个不同的画布位置",
              writtenCanvasY.Distinct().Count() == 3, string.Join(", ", writtenCanvasY.Select(v => v.ToString("F0"))));

        // 回到顶部：只有第一笔应该在视野里
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        SettleFrames(400);

        int first = ScreenProbe.CountMagenta((int)px - 60, (int)py - 60, 120, 120);
        Check("回顶部：第一笔仍在原位", first > 200, $"{first} 像素");

        int below = ScreenProbe.CountMagenta((int)px - 60, (int)py + 900, 120, 300);
        Check("回顶部：后两笔在视野外", below < 40, $"屏幕下方 {below} 像素（应为 0）");

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：滚到哪儿都能写，写下的内容留在那个画布位置");
        Console.WriteLine();

        // 死锁回归：**空文档也必须能往下滚一屏**。
        // 之前只取"内容 ∪ 视口"时，空文档画布恰好一屏、下边界为 0，
        // 往下滚立刻被夹回去；而滚不动就写不到下面去 —— 死锁。
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        var empty = CanvasExtent;
        Check("空文档也能往下滚一屏",
              empty.MaxY - ViewportCanvas.MinY >= _virtualH * 2f,
              $"画布高 {empty.MaxY - empty.MinY:F0}px（一屏 {_virtualH}px）");
        Check("空文档不许往上滚过头", CanvasExtent.MinY <= ViewportCanvas.MinY + 0.5f,
              $"画布顶 {empty.MinY:F0}");
        Console.WriteLine();
        Console.WriteLine("  关于下限：数据层不设限才是对的（往下写不完）。");
        Console.WriteLine("  缺的不是下限，是**回顶部的办法**（滚动条 / 一键回顶）。");
        Console.WriteLine("  现在的实现只夹住了上边界（不许滚过内容顶部），下方不设限。");

        ViewOffsetY = 0f;
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }


    /// <summary>
    /// `--scrollflash`：复现"**滚轮滚动之后、一按鼠标就闪 / 错位**"（用户 2026-10-04 报，
    /// 关键线索：滚动之后按下才闪、松手就不闪；"有时候错位、有时候不错位"；
    /// 之前竖写时也在左侧遇到过）。
    ///
    /// 事故链（怀疑）：相机变化只强制了**一帧**整屏重画（`_forceFullFrame` 用完即清）。
    /// 滚动停下时，两个后缓冲里只有最后画的那个在**新位置**，另一个还停在上一格；
    /// 这时按下去画，第一帧只重画"笔迹附近的一条"（部分脏区），目标缓冲偏偏是落后
    /// 一格的旧画面 → 贴上去就是"从某条线隔开、一侧错位"，帧间交替 → 闪；松手后
    /// 进入空闲不再出帧，screen 停在哪一帧看运气（所以"有时错位有时不错位"）。
    ///
    /// **出帧节奏必须和真机一致**：主循环是"有需求才出一帧"（`NeedsFrame()`），
    /// 而不是 `SettleFrames` 那样一直出——空闲多出的整屏帧会把两个缓冲都修好，
    /// 所以以前的探针抓不到。本探针全程手动出帧（滚两格 → 停 → 按下 → 移动，
    /// 每步只 `PumpMessages + RenderAll` 一次），再和"滚完那一帧"比：
    /// 若某一帧整体错位一个滚动步长（S = 72×DPI），就是复现。
    /// </summary>
    private void ScrollFlashTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 滚动后按下就闪/错位 专项检测 ===");
        if (SkipIfNoSyntheticInput("滚动后按下（需要合成鼠标）")) { _quit = true; return; }
        InkEngine.OverlayWindow.Trace = true;

        BoardOn = true;
        BoardPattern = 2;                    // 横线底纹（用户白板那种）
        BoardPatternStepLogical = 40f;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        PassThrough = false;
        Doc.InvalidateAll();

        // 垫内容：相机要滚得动（画布范围要超过一屏）
        for (int k = 0; k < 6; k++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = PenColor, Width = 4f * DpiScale,
            };
            float yy = _virtualY + 2200f + k * 260f;
            for (int j = 0; j < 20; j++)
                s.AddPoint(_virtualX + 260f + j * 70f, yy + MathF.Sin(j * 0.7f) * 60f, 0.5f, j * 8f);
            Doc.AddStroke(s);
        }

        // 每出一帧拍一张，并量"左列（x=100）第一条线 vs 右区（x=700）第一条线"的相位差：
        // 0 = 对齐；非 0 = 左侧那一列分块错位（用户看到的"从某条线隔开"）。
        const int w = 1400, h = 1500;
        var seq = new List<(string Label, byte[] Cap, float Cam)>();
        void Frame(string label)
        {
            PumpMessages();
            RenderAll();
            Thread.Sleep(40);
            seq.Add((label, ScreenProbe.CaptureRegion(0, 0, w, h), ViewOffsetY));
        }

        Frame("起手1");
        Frame("起手2");

        float px = _virtualX + 260f, py = _virtualY + 420f;
        SendMouse((int)px, (int)py, 0);       // 鼠标挪到左侧、悬停
        Frame("悬停");

        for (int k = 0; k < 2; k++)
        {
            Native.PostMessage(_windows[0].Hwnd, 0x020A /*WM_MOUSEWHEEL*/,
                               new IntPtr(-120 << 16), IntPtr.Zero);
            Frame($"滚{k + 1}");
        }

        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN);
        Frame("按下");
        for (int k = 0; k < 2; k++)
        {
            SendMouse((int)px, (int)(py + 40 * (k + 1)), 0);
            Frame($"移动{k + 1}");
        }
        SendMouse((int)px, (int)(py + 120), Native.MOUSEEVENTF_LEFTUP);
        Frame("抬起");

        // 判定：左列第一条线的中心 − 右区第一条线的中心。
        int fail = 0;
        foreach (var (label, cap, cam) in seq)
        {
            if (cap == null) continue;
            int l = FirstLineCenter(cap, w, h, 100);
            int r = FirstLineCenter(cap, w, h, 700);
            int off = l - r;
            bool bad = off != 0;
            if (bad) fail++;
            Console.WriteLine($"  {label,-6} 相机 {cam,7:F0}  左线 {l,4} / 右线 {r,4}  左−右 = {off,4}"
                              + (bad ? "  ← 左列错位（闪）" : ""));
        }
        Console.WriteLine(fail > 0
            ? $"  FAIL: {fail} 帧左列与右区错位——就是用户看到的“从某条线隔开”的闪"
            : "  PASS: 全程左列与右区对齐");
        InkEngine.OverlayWindow.Trace = false;
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>竖着扫，返回第一条"灰线"的中心 y（没有就 -1）。截屏是 BGRA。</summary>


    /// <summary>
    /// `--smoothflashtest [--off]`：**"画的时候一直在闪"的专项检测**（2026-09-28 用户实测）。
    ///
    /// 判据不是"看"，而是"**已经画过去的地方不许再变**"：
    ///   · 合成鼠标按住不放、一路画过去（路径固定：每步 8~24px、每步拐 ±25° 以内）；
    ///   · 笔尖跑过"检查点" 100px 之后，每帧拍一次检查点那一小块（30×30）；
    ///   · 那一块里的墨应该**冻住**了——它要是还在变，画面上就是残影 / 发闪。
    ///
    /// 对照组 `--off` 跑同一条路径的折线：折线一定冻得住，所以这条测试**自证有效**
    /// （不是"永远通过"的摆设）。它也是"曲线不改脏区假设"这句话的机器证明。
    /// </summary>
    private void SmoothFlashTest(bool smoothing, bool fast = false)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 曲线化“画的时候闪不闪”专项检测（{(smoothing ? "曲线" : "折线对照")}）===");

        if (SkipIfNoSyntheticInput("曲线化闪不闪（需要合成鼠标移动光标）")) { _quit = true; return; }

        BoardOn = true;                     // 白底，判据干净
        bool wasEnabled = StrokeSmoothing.Enabled;
        StrokeSmoothing.SetEnabled(smoothing);
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        PassThrough = false;
        Doc.InvalidateAll();
        SettleFrames(400);

        // 固定路径（确定性）：一条整体往右、但不老实的抖动路径
        var rnd = new Random(20260928);
        var path = new List<Vector2>();
        float x = _virtualX + 300f, y = _virtualY + 700f;
        float ang = 0f;
        path.Add(new Vector2(x, y));
        for (int i = 0; i < 60; i++)
        {
            ang += (float)(rnd.NextDouble() * 2 - 1) * 25f * (MathF.PI / 180f);
            ang = Math.Clamp(ang, -0.6f, 0.6f);
            float step = fast ? 30f + (float)rnd.NextDouble() * 40f : 8f + (float)rnd.NextDouble() * 16f;
            x += MathF.Cos(ang) * step;
            y += MathF.Sin(ang) * step;
            path.Add(new Vector2(x, y));
        }

        // 整条路径所在的带：每次移动都在这里拍一张，和上一张比。
        // **离笔尖 60px 以外的像素不该变**——那里早就画过去了，变了就是残影 / 发闪。
        const int bandW = 1100, bandH = 320;
        int bx = (int)(_virtualX + 240f), by = (int)(_virtualY + 560f);

        SendMouse((int)path[0].X, (int)path[0].Y, 0);
        SettleFrames(200);
        SendMouse((int)path[0].X, (int)path[0].Y, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(80);

        byte[] prev = null;
        int samples = 0, far20 = 0, far60 = 0;
        float far20Dist = 0f, far60Dist = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            SendMouse((int)path[i].X, (int)path[i].Y, 0);
            SettleFrames(fast ? 10 : 20);
            var cap = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
            if (cap == null) continue;
            if (prev != null)
            {
                // 两圈分别量：20px 以外（笔尖后面那一小段"回头看"）与 60px 以外（真正的残影）
                var d20 = DiffFarFromTip(prev, cap, bandW, bandH, bx, by, path[i].X, path[i].Y, 20f);
                var d60 = DiffFarFromTip(prev, cap, bandW, bandH, bx, by, path[i].X, path[i].Y, 60f);
                far20 = Math.Max(far20, d20.count);
                far60 = Math.Max(far60, d60.count);
                far20Dist = Math.Max(far20Dist, d20.maxDist);
                far60Dist = Math.Max(far60Dist, d60.maxDist);
            }
            prev = cap;
            samples++;
        }

        SendMouse((int)path[^1].X, (int)path[^1].Y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(300);
        StrokeSmoothing.SetEnabled(wasEnabled);       // 恢复原状态，别把后面的路径带偏

        Console.WriteLine($"  画了 {samples} 步；带 {bandW}×{bandH} @ ({bx},{by})");
        Console.WriteLine($"  离笔尖 >20px 的变化：最多 {far20} 个像素（最远 {far20Dist:F0}px）");
        Console.WriteLine($"  离笔尖 >60px 的变化：最多 {far60} 个像素（最远 {far60Dist:F0}px）");
        bool ok = samples >= 8 && far60 <= 0.5f;
        Console.WriteLine(ok
            ? "  PASS: 已经画过去的地方是冻住的（离笔尖远处一个像素都没变）"
            : samples < 8
                ? "  FAIL: 没采到几步——测量无效（别当成通过）"
                : "  FAIL: 已经画过去的地方还在变——画面上就是残影 / 发闪");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 两张同尺寸 BGRA 图的差异，只看**离笔尖 &gt; minDist 的那些像素**。
    /// 返回（这样的像素数、其中最远那个到笔尖的距离、它的屏幕坐标）。
    /// </summary>


    /// <summary>
    /// `--prevflash`：**"写下一笔时，上一笔闪不闪"**专项检测（用户 2026-10-04 报：
    /// 正在写下一笔、笔尖滑动时，上一笔某个位置闪一下）。
    ///
    /// 和 <see cref="SmoothFlashTest"/> 的区别：那条测的是**同一笔**已经画过去的地方；
    /// 这一条测的是**已经落定的上一笔**。两条判据：
    ///   ① **上一笔本身**：以 A 落定后为基准，B 画完后，离 B 整条路径 &gt;40px 的像素必须
    ///      一个都没变（A 被 B 盖住的那一段允许变）；
    ///   ② **笔尖后面全都不许动**（更严）：每一步和上一步比，离当前笔尖 &gt;60px 的像素
    ///      必须冻住——这条连 B 自己画过的部分、以及 A 被盖住的部分都管；
    ///      抬笔（提交/补画那一帧）也单独拍一张比。
    /// 两条都过 = 用户看到的那种"上一笔闪"在这个输入路径下不存在。
    /// </summary>
    private void PrevFlashTest(bool usePen = false, bool left = false, bool withUi = false, bool fast = false)
    {
        Console.WriteLine();
        Console.WriteLine($"=== 上一笔闪不闪（{(left ? "左侧竖写＋横线底纹" : "写下一笔")}；{(usePen ? "合成笔 PT_PEN" : "合成鼠标")}"
                          + $"{(withUi ? "＋产品界面" : "")}{(fast ? "，快写" : "")}） ===");
        if (usePen)
        {
            if (!EnsureSyntheticPen())
            {
                Console.WriteLine("  SKIP: 拿不到合成笔设备（CreateSyntheticPointerDevice 失败）");
                _quit = true;
                return;
            }
        }
        else if (SkipIfNoSyntheticInput("上一笔闪不闪（需要合成鼠标移动光标）")) { _quit = true; return; }

        BoardOn = true;                     // 白底，判据干净
        if (left)
        {
            // 用户报的场景：白板 + **横线底纹**，竖写在**屏幕左侧**。
            BoardPattern = 2;               // 2 = 横线（1 = 方格，0 = 无）
            BoardPatternStepLogical = 40f;
        }
        if (withUi)
        {
            // 产品界面（工具条）：用户就是在这个状态下写的；测试模式默认是空界面。
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
        }
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        PassThrough = false;
        Doc.InvalidateAll();
        SettleFrames(400);

        // 输入注入：鼠标 / 合成笔（压感）各一条路。
        void Move(float x, float y, float p)
        {
            if (usePen) SendPenPoint(x, y, (uint)Math.Clamp(p, 0f, 1024f), contact: true, first: false);
            else SendMouse((int)x, (int)y, 0);
        }
        void Down(float x, float y)
        {
            if (usePen) SendPenPoint(x, y, 300, contact: true, first: true);
            else SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);
        }
        void Up(float x, float y)
        {
            if (usePen) SendPenPoint(x, y, 0, contact: false, first: false);
            else SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);
        }
        void Hover(float x, float y)
        {
            if (usePen) SendPenPoint(x, y, 0, contact: false, first: false);
            else SendMouse((int)x, (int)y, 0);
        }

        float x0 = left ? _virtualX + 300f : _virtualX + 400f;
        float y0 = left ? _virtualY + 420f : _virtualY + 720f;

        // ---- 上一笔 A：左侧场景是"一竖"（跨过第一列分块边界 x=256，带小摆动），
        //      默认场景是一条正弦弧 ----
        var pathA = new List<Vector2>();
        for (int i = 0; i <= 40; i++)
        {
            float t = i / 40f;
            pathA.Add(left
                ? new Vector2(x0 + MathF.Sin(t * 5f) * 36f, y0 + t * 560f)
                : new Vector2(x0 + t * 520f, y0 - MathF.Sin(t * MathF.PI) * 90f + t * 30f));
        }
        Hover(pathA[0].X, pathA[0].Y); SettleFrames(150);
        Down(pathA[0].X, pathA[0].Y); SettleFrames(80);
        for (int i = 1; i < pathA.Count; i++)
        {
            Move(pathA[i].X, pathA[i].Y, 200f + 600f * MathF.Abs(MathF.Sin(i / 40f * 2f * MathF.PI)));
            SettleFrames(fast ? 2 : 8);
        }
        Up(pathA[^1].X, pathA[^1].Y);
        SettleFrames(400);

        // 拍基准之前，先把笔**挪出取景带**：真笔/合成笔悬停时会画一圈落点反馈（Ring），
        // 笔停在 A 尾（抬笔点）上，圆环就在带里——B 一开始圆环被擦掉，会被误判成
        // "上一笔在闪"（第一次跑就踩到了：固定 31×31 的方块，正是圆环的包围盒）。
        Hover(_virtualX + 2400f, _virtualY + 1600f);
        SettleFrames(200);

        // ---- 基准：A 落定之后的整块区域 ----
        int bandW = left ? 900 : 1040;
        int bandH = left ? 1250 : 620;
        int bx = left ? (int)_virtualX : (int)(x0 - 140f);
        int by = left ? (int)(_virtualY + 140f) : (int)(y0 - 340f);
        var reference = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
        if (reference == null)
        {
            Console.WriteLine("  取不到屏（CaptureRegion 失败）——测量无效");
            _quit = true;
            return;
        }
        Console.WriteLine($"  上一笔 A 已落定；带 {bandW}×{bandH} @ ({bx},{by})");
        ScreenProbe.SaveBuffer("reports/prevflash-ref.bmp", reference, bandW, bandH);

        // ---- 【关键】两缓冲比对：同一块**静止**内容，强制渲染两帧（连续两次 Present
        //      会落在两个不同的后缓冲上），两张截图必须逐像素相同。
        //      不同 = 有一个后缓冲在那个位置是旧的 → 静止时不呈现、一写字/滚动
        //      （连续出帧）就"新旧交替"——正是用户报的"从某一条线开始闪"。
        int bufferDiff = 0; string bufferDiffWhere = "";
        for (int k = 0; k < 3; k++)
        {
            _dirty = true; SettleFrames(1);
            var f0 = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
            _dirty = true; SettleFrames(1);
            var f1 = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
            if (f0 == null || f1 == null) continue;
            var d = DiffFarFromPath(f0, f1, bandW, bandH, bx, by, pathA, pathA.Count, -1f);
            if (d.count > bufferDiff)
            {
                bufferDiff = d.count;
                bufferDiffWhere = $"({d.minX + bx},{d.minY + by})..({d.maxX + bx},{d.maxY + by})";
            }
        }
        Console.WriteLine($"  两缓冲比对（静止内容，强制出两帧）：差异 {bufferDiff} 像素"
                          + (bufferDiff > 0 ? $" @ {bufferDiffWhere}" : ""));

        // ---- 下一笔 B：默认从 A 的左上斜穿到右下；左侧场景在 A 左边再竖写一条 ----
        var pathB = new List<Vector2>();
        for (int i = 0; i <= 36; i++)
        {
            float t = i / 36f;
            pathB.Add(left
                ? new Vector2(x0 - 150f + MathF.Sin(t * 4f) * 26f, y0 - 140f + t * 700f)
                : new Vector2(x0 + 60f + t * 380f, y0 - 270f + t * 540f));
        }
        Hover(pathB[0].X, pathB[0].Y); SettleFrames(150);
        Down(pathB[0].X, pathB[0].Y); SettleFrames(80);

        int worstA = 0, samples = 0;            // 判据①：上一笔（离 B 整条路径远）
        float worstADist = 0f, worstAX = 0f, worstAY = 0f;
        int worstAMinX = 0, worstAMinY = 0, worstAMaxX = 0, worstAMaxY = 0;
        int worstTail = 0;                       // 判据②：离当前笔尖远的任何变化
        float worstTailDist = 0f, worstTailX = 0f, worstTailY = 0f;
        byte[] prev = null;
        for (int i = 1; i < pathB.Count; i++)
        {
            Move(pathB[i].X, pathB[i].Y, 200f + 600f * MathF.Abs(MathF.Sin(i / 36f * 2f * MathF.PI)));
            SettleFrames(fast ? 3 : 12);
            var cap = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
            if (cap == null) continue;
            samples++;

            var dA = DiffFarFromPath(reference, cap, bandW, bandH, bx, by, pathB, i + 1, 40f);
            if (dA.count > 0)
                Console.WriteLine($"    step {i,2}（笔尖 {pathB[i].X:F0},{pathB[i].Y:F0}）：上一笔被改 {dA.count} 像素，"
                                  + $"范围 ({dA.minX + bx},{dA.minY + by})..({dA.maxX + bx},{dA.maxY + by})");
            if (dA.count > worstA)
            {
                worstA = dA.count; worstADist = dA.maxDist; worstAX = dA.x; worstAY = dA.y;
                worstAMinX = dA.minX; worstAMinY = dA.minY; worstAMaxX = dA.maxX; worstAMaxY = dA.maxY;
                ScreenProbe.SaveBuffer("reports/prevflash-worst.bmp", cap, bandW, bandH);
            }

            if (prev != null)
            {
                var dT = DiffFarFromTip(prev, cap, bandW, bandH, bx, by, pathB[i].X, pathB[i].Y, 60f);
                if (dT.count > worstTail) { worstTail = dT.count; worstTailDist = dT.maxDist; worstTailX = dT.x; worstTailY = dT.y; }
            }
            prev = cap;
        }

        // 抬笔：**提交/补画那一帧**也要算（上一笔闪的一个高发点就是这里）
        Up(pathB[^1].X, pathB[^1].Y);
        SettleFrames(400);
        var capEnd = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
        if (capEnd != null)
        {
            var dA = DiffFarFromPath(reference, capEnd, bandW, bandH, bx, by, pathB, pathB.Count, 40f);
            if (dA.count > 0)
                Console.WriteLine($"    抬笔后：上一笔被改 {dA.count} 像素，"
                                  + $"范围 ({dA.minX + bx},{dA.minY + by})..({dA.maxX + bx},{dA.maxY + by})");
            if (dA.count > worstA)
            {
                worstA = dA.count; worstADist = dA.maxDist; worstAX = dA.x; worstAY = dA.y;
                worstAMinX = dA.minX; worstAMinY = dA.minY; worstAMaxX = dA.maxX; worstAMaxY = dA.maxY;
                ScreenProbe.SaveBuffer("reports/prevflash-worst.bmp", capEnd, bandW, bandH);
            }
            if (prev != null)
            {
                var dT = DiffFarFromTip(prev, capEnd, bandW, bandH, bx, by, pathB[^1].X, pathB[^1].Y, 60f);
                if (dT.count > worstTail) { worstTail = dT.count; worstTailDist = dT.maxDist; worstTailX = dT.x; worstTailY = dT.y; }
            }
        }

        // B 落定后再做一次两缓冲比对（提交/补画是否只进了其中一个缓冲）。
        // **先把笔挪出取景带**：笔悬停会画落点圆环，圆环在合成笔抬笔后会"进/出范围"抖动，
        // 把圆环当噪声误判成"上一笔在闪"（踩过一次：差异恰好是 32×32 的圆环）。
        {
            Hover(_virtualX + 2400f, _virtualY + 1600f);
            SettleFrames(200);
            int bd2 = 0; string w2 = "";
            for (int k = 0; k < 3; k++)
            {
                _dirty = true; SettleFrames(1);
                var f0 = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
                _dirty = true; SettleFrames(1);
                var f1 = ScreenProbe.CaptureRegion(bx, by, bandW, bandH);
                if (f0 == null || f1 == null) continue;
                var d = DiffFarFromPath(f0, f1, bandW, bandH, bx, by, pathA, pathA.Count, -1f);
                if (d.count > bd2)
                {
                    bd2 = d.count;
                    w2 = $"({d.minX + bx},{d.minY + by})..({d.maxX + bx},{d.maxY + by})";
                    Console.WriteLine($"    [bufdiff] 环={DrawnCursor} inside={PointerInside} "
                                      + $"pt=({PointerX:F0},{PointerY:F0}) type={LastPointerType}");
                    ScreenProbe.SaveBuffer("reports/prevflash-buf-f0.bmp", f0, bandW, bandH);
                    ScreenProbe.SaveBuffer("reports/prevflash-buf-f1.bmp", f1, bandW, bandH);
                }
            }
            if (bd2 > 0) Console.WriteLine($"  B 落定后两缓冲比对：差异 {bd2} 像素 @ {w2}");
            bufferDiff = Math.Max(bufferDiff, bd2);
        }

        Console.WriteLine($"  B 画了 {samples} 步，抬笔后再拍一张；判据阈值 40px（A）/ 60px（笔尖后）");
        Console.WriteLine($"  ① 上一笔被改动（离 B 整条路径 >40px）：{worstA} 个像素"
                          + $"（最远 {worstADist:F0}px @ {worstAX:F0},{worstAY:F0}，"
                          + $"范围 ({worstAMinX + bx},{worstAMinY + by})..({worstAMaxX + bx},{worstAMaxY + by})）");
        Console.WriteLine($"  ② 笔尖后面还在动（离笔尖 >60px，含 B 自己画过的）：{worstTail} 个像素"
                          + $"（最远 {worstTailDist:F0}px @ {worstTailX:F0},{worstTailY:F0}）");

        // ---- 滚动阶段（--left）：内容垫够两屏 → 滚 5 格，按"S 像素整体平移"逐行对；
        //      再原地连拍两张看有没有像素在闪（用户报的正是"滚动时左侧闪"）----
        int scrollBad = 0;
        if (left)
        {
            for (int k = 0; k < 6; k++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = PenColor, Width = 4f * DpiScale,
                };
                float yy = _virtualY + 2200f + k * 260f;
                for (int j = 0; j < 20; j++)
                    s.AddPoint(_virtualX + 260f + j * 70f, yy + MathF.Sin(j * 0.7f) * 60f, 0.5f, j * 8f);
                Doc.AddStroke(s);
            }
            _dirty = true;
            RenderAll();
            SettleFrames(200);

            Hover(_virtualX + 2400f, _virtualY + 1600f);   // 笔挪出取景区
            SettleFrames(150);

            const int sw = 1400, sh = 1500;
            var capBefore = ScreenProbe.CaptureRegion(0, 0, sw, sh);
            int steps = 0;
            for (int step = 0; step < 5 && capBefore != null; step++)
            {
                float cam0 = ViewOffsetY;
                Native.PostMessage(_windows[0].Hwnd, 0x020A /*WM_MOUSEWHEEL*/,
                                   new IntPtr(-120 << 16), IntPtr.Zero);
                SettleFrames(10);
                float cam1 = ViewOffsetY;
                float moved = cam0 - cam1;                   // 内容整体上移这么多
                int S = (int)MathF.Round(moved);
                var capAfter = ScreenProbe.CaptureRegion(0, 0, sw, sh);
                if (capAfter == null) break;
                if (S <= 0 || MathF.Abs(moved - S) > 0.3f)
                {
                    Console.WriteLine($"    滚动第 {step + 1} 格：相机实际平移 {moved:F2}px（到边界/非整），跳过");
                    capBefore = capAfter;
                    continue;
                }
                steps++;
                int up = CountShiftMismatch(capAfter, capBefore, sw, sh, S);
                int down = CountShiftMismatch(capBefore, capAfter, sw, sh, S);
                int bad = Math.Min(up, down);
                scrollBad = Math.Max(scrollBad, bad);

                var capAgain = ScreenProbe.CaptureRegion(0, 0, sw, sh);
                // 原地连拍要把**下一帧真的画出来**（强制出帧），否则两张拍的是同一帧、永远为 0。
                _dirty = true; SettleFrames(1);
                var capAgain2 = ScreenProbe.CaptureRegion(0, 0, sw, sh);
                int flick = (capAgain == null || capAgain2 == null) ? 0
                    : DiffFarFromTip(capAgain, capAgain2, sw, sh, 0, 0, -1e6f, -1e6f, 0f).count;
                scrollBad = Math.Max(scrollBad, flick);
                Console.WriteLine($"    滚动第 {step + 1} 格：平移 {S}px 不匹配 {bad} 像素，原地连拍差异 {flick} 像素"
                                  + (bad + flick == 0 ? "（干净）" : " ← 就是闪"));
                capBefore = capAfter;
            }
            if (steps > 0)
                Console.WriteLine($"  ③ 滚动：{steps} 格，最大不匹配/闪动 {scrollBad} 像素");
        }

        // ② 允许 ≤2 个像素的噪声：**笔迹自身**的起点在头几帧曲线稳定过程中
        //    可能变一个像素（离笔尖 61px 正好压线抓到过），那不是"上一笔闪"。
        bool ok = samples >= 8 && worstA == 0 && worstTail <= 2 && scrollBad == 0 && bufferDiff == 0;
        Console.WriteLine(ok
            ? "  PASS: 写下一笔时，上一笔 / 笔尖后面都是冻住的"
            : samples < 8 ? "  FAIL: 没采到几步——测量无效（别当成通过）"
                          : "  FAIL: 有东西在变——就是用户看到的“闪”");
        Console.WriteLine();
        _quit = true;
    }

}
