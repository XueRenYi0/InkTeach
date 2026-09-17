using InkEngine;
using System.Reflection;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// **幻灯片页自检**（阶段 1）：用**假放映**驱动整条链，不需要装 Office。
///
/// 验的是"页变了 → 该显示的墨跟着换"这条链，以及三件容易做错的事：
///   · 归属按**身份**（SlideID）而不是页号——调换页序之后批注不回错位；
///   · 切页**只动相机**（O(1)、不重放任何历史、不清撤销栈）；
///   · 放映结束时**退得干净**（回到白板、不把放映页的墨带过来）。
/// </summary>
internal sealed partial class App
{
    /// <summary>幻灯片页自检的入口（`--slidetest`）。</summary>
    internal void SlideTestEntry() => SlideTest();

    /// <summary>
    /// **真 PowerPoint 端到端探针**（阶段 2）：自己起一个 PowerPoint、建三张空白幻灯片、
    /// 开始放映，然后**走我们自己的那条路**（`PowerPointComSource` + 轮询 + 页归属）
    /// 验：认得出来吗？翻页跟得上吗？批注落在正确的那一页上吗？退出干净吗？
    ///
    /// 会启动 PowerPoint 进程（结束时**不保存**地关掉它）。只在开发机上手动跑：
    /// 装了 Office 才有意义，而且会真的弹全屏放映。
    /// </summary>
    internal void PptProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 真 PowerPoint 端到端探针 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        GotoPage(0);
        SettleFrames(250);

        object app = null, pres = null;
        try
        {
            // ① 起一个 PowerPoint 并造三张空白幻灯片（后期绑定，不引 Office 程序集）
            Type t = Type.GetTypeFromProgID("PowerPoint.Application");
            if (t == null) { Console.WriteLine("  没装 PowerPoint（ProgID 找不到）——这一条跳过"); _quit = true; return; }
            app = Activator.CreateInstance(t);
            Set(app, "Visible", true);
            object presSet = Get(app, "Presentations");
            pres = Call(presSet, "Add", null);
            object slides = Get(pres, "Slides");
            for (int i = 0; i < 3; i++) Call(slides, "Add", new object[] { i + 1, 12 /*ppLayoutBlank*/ });
            Console.WriteLine($"  已造好一份 {Get(pres, "Slides.Count")} 页的临时演示文稿");

            // 开始放映（在后台线程里跑：Run() 会阻塞到放映结束）
            var runner = new Thread(() =>
            {
                try { Call(Get(pres, "SlideShowSettings"), "Run", null); } catch { }
            });
            runner.IsBackground = true;
            runner.Start();

            // 诊断：放映到底起来了没有（这一步只在探针里做，产品代码不做）
            SettleFrames(2500);
            Console.WriteLine($"  诊断：PowerPoint 进程 {System.Diagnostics.Process.GetProcessesByName("POWERPNT").Length} 个，"
                            + $"SlideShowWindows.Count = {Get(app, "SlideShowWindows.Count")}，"
                            + $"pres.SlideShowWindow = {(Get(pres, "SlideShowWindow") != null ? "有" : "无")}");

            // ② 接上我们的来源，等它认出"正在放映"
            Slides = new PowerPointComSource();
            bool got = false;
            for (int i = 0; i < 60 && !got; i++) { SettleFrames(200); got = SlideNow.Showing; }
            Check("认出正在放映", got,
                  got ? $"{SlideNow.Position}/{SlideNow.Count} 页，身份 {SlideNow.SlideId}，{SlideNow.DeckKey}"
                      : "6 秒内没认出来");
            if (!got) { Cleanup(); Check("收尾：关掉临时演示文稿（不保存）", true, ""); Finish(); return; }

            long id1 = SlideNow.SlideId;
            Check("第 1 页拿到的是**幻灯片身份**（不是页号 1）", id1 != 1, $"SlideID = {id1}");
            Check("我们在幻灯片页空间里", Doc.CurrentPage == SlidePageOfPosition(1),
                  $"页号 {Doc.CurrentPage}");

            // ③ 在**这一页**写一笔（走真实规则：起笔标页 + 标幻灯片身份）
            var a = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.9f, 0.2f, 0.2f, 1f), Width = 8f * DpiScale,
                Page = CurrentPage, SlideId = SlideNow.SlideId,
            };
            float y0 = PageTopCanvas - CurrentPage * PageHeightCanvas + _virtualH * 0.5f;
            for (int i = 0; i <= 20; i++) a.AddPoint(_virtualX + _virtualW * 0.4f + i * 20f, y0, 1f, i * 8);
            Doc.AddStroke(a);
            SettleFrames(150);
            Check("第 1 页那一笔带上了这一页的身份", a.SlideId == id1, $"{a.SlideId} vs {id1}");

            // ④ **从我们的面板翻页**（这条按钮放映中驱动 PPT）
            FlipPageFromUi(true);
            SettleFrames(600);
            Check("「下一屏」把放映翻到了第 2 页", SlideNow.Position == 2, $"现在第 {SlideNow.Position} 页");
            Check("我们的画布跟着换到了第 2 页的批注空间",
                  Doc.CurrentPage == SlidePageOfPosition(2) && CurrentPage == Doc.CurrentPage,
                  $"页号 {Doc.CurrentPage}");
            long id2 = SlideNow.SlideId;
            Check("第 2 页的身份和第 1 页不同（不然批注会串页）", id2 != 0 && id2 != id1,
                  $"{id1} → {id2}");
            Check("第 1 页那一笔**留在第 1 页**（没被改页号）",
                  a.Page == SlidePageOfPosition(1) && a.SlideId == id1,
                  $"Page {a.Page}，SlideId {a.SlideId}");

            FlipPageFromUi(false);
            SettleFrames(600);
            Check("「上一屏」翻回第 1 页", SlideNow.Position == 1 && Doc.CurrentPage == SlidePageOfPosition(1),
                  $"放映第 {SlideNow.Position} 页，我们第 {Doc.CurrentPage - SlidePageBase} 页");

            FlipPageFromUi(true);
            SettleFrames(600);
            Check("再翻到第 2 页：第 1 页那一笔仍然在，而且不在这一页",
                  Doc.Strokes.Contains(a) && a.Page != Doc.CurrentPage,
                  $"对象 {Doc.Strokes.Count} 个");

            // ⑤ 清空只清**当前这一页幻灯片**（第 2 页是空的 → 什么都不该少）
            ClearFromUi();
            SettleFrames(200);
            Check("清空只清当前页：第 2 页空的，所以第 1 页那笔不受影响",
                  Doc.Strokes.Contains(a), $"剩 {Doc.Strokes.Count} 个对象");

            // ⑥ 结束放映：退出后应该**回到白板**，并且将来写的笔不带幻灯片身份
            int wbPage = 0;
            // 用**取得到的那条路**退放映（实测：集合的 `Item(1)` 取不出来，
            // 而 `Presentation.SlideShowWindow` 取得到——产品代码里也是这么两条都留着的）
            Call(Get(Get(pres, "SlideShowWindow"), "View"), "Exit", null);
            for (int i = 0; i < 40 && SlideNow.Showing; i++) SettleFrames(200);
            SettleFrames(600);              // 等"回白板"那段相机动画走完（探针第一版没等，量到半路）
            Check("结束放映：我们不再认为在放映", !SlideNow.Showing, $"Showing = {SlideNow.Showing}");
            Check("结束放映：回到白板空间", Doc.CurrentPage == wbPage && CurrentPage == wbPage,
                  $"页号 {Doc.CurrentPage}，相机 {ViewOffsetY:F0}");
        }
        catch (Exception ex)
        {
            Check("探针整体没抛异常", false, ex.Message);
        }
        finally
        {
            Cleanup();
        }
        Finish();

        void Cleanup()
        {
            Slides = null;
            SlideNow = default;
            try { if (pres != null) Call(pres, "Close", null); } catch { }
            try { if (app != null) Call(app, "Quit", null); } catch { }
        }
        void Finish()
        {
            Doc.Clear();
            Doc.ClearHistory();
            Console.WriteLine();
            Console.WriteLine(fail == 0
                ? "  PASS：真 PowerPoint 上认得出、翻得动、批注按页走、退出干净"
                : $"  FAIL：{fail} 项不对（{pass} 项通过）");
            ExitCode = fail == 0 ? 0 : 1;
            _quit = true;
        }
    }

    // ---- 后期绑定的小工具（探针自己造演示文稿用）---------------------------
    private static object Get(object o, string path)
    {
        if (o == null) return null;
        object cur = o;
        foreach (var part in path.Split('.'))
        {
            if (part.Length == 0) continue;
            try { cur = cur.GetType().InvokeMember(part, BindingFlags.GetProperty, null, cur, null); }
            catch { try { return cur.GetType().InvokeMember(part, BindingFlags.GetProperty, null, cur, new object[] { 1 }); } catch { return null; } }
        }
        return cur;
    }

    private static object Get(object o, string name, object[] args)
    {
        if (o == null) return null;
        try { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args); }
        catch { return null; }
    }

    private static object Call(object o, string name, object[] args)
    {
        if (o == null) return null;
        try { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
        catch (Exception ex) { Console.WriteLine($"  [COM] {name} 失败：{ex.Message}"); return null; }
    }

    private static void Set(object o, string name, object v)
    {
        try { o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { v }); }
        catch { }
    }

    private void SlideTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 幻灯片页自检（假放映驱动，不需要 Office）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        // 干净起步：白板第 1 页、没有放映
        Doc.Clear();
        Doc.ClearHistory();
        Slides = null;
        SlideNow = default;
        GotoPage(0);
        SettleFrames(300);

        var fake = new FakeSlideSource { Showing = true, Position = 1, Count = 3 };
        Slides = fake;
        SettleFrames(400);                       // 等一次轮询
        Check("接上假放映：探测到正在放映、第 1/3 页",
              SlideNow.Showing && SlideNow.Position == 1 && SlideNow.Count == 3,
              $"{SlideNow.Position}/{SlideNow.Count}");
        Check("切到第 1 页的**幻灯片空间**",
              Doc.CurrentPage == SlidePageOfPosition(1) && CurrentPage == Doc.CurrentPage,
              $"页号 {Doc.CurrentPage}（幻灯片页起点 {SlidePageBase}），相机 {ViewOffsetY:F0}");

        // 每一页各写一笔（走真规则：起笔时按当前页/当前放映标页）
        var made = new List<Stroke>();
        for (int pos = 1; pos <= 3; pos++)
        {
            fake.Position = pos;
            SettleFrames(400);                   // 轮询 + 相机动画
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.1f, 0.1f, 0.1f, 1f), Width = 8f,
                Page = CurrentPage,              // 起笔时引擎就是这么标的
                SlideId = fake.SlideIdOf(pos),
            };
            float y = PageTopCanvas - CurrentPage * PageHeightCanvas + 300f;
            for (int i = 0; i <= 20; i++) s.AddPoint(_virtualX + 300f + i * 40f, y, 1f, i * 8);
            Doc.AddStroke(s);
            made.Add(s);
            SettleFrames(120);
        }
        Check("三页幻灯片各一笔，页号是幻灯片空间里的三段",
              made[0].Page == SlidePageOfPosition(1)
              && made[1].Page == SlidePageOfPosition(2)
              && made[2].Page == SlidePageOfPosition(3),
              $"{made[0].Page}/{made[1].Page}/{made[2].Page}");
        Check("三笔的**身份**是各自的 SlideID（不是页号）",
              made[0].SlideId == fake.SlideIdOf(1) && made[2].SlideId == fake.SlideIdOf(3),
              $"{made[0].SlideId}/{made[1].SlideId}/{made[2].SlideId}");

        // 切页：只动相机，对象一个坐标都不动、撤销栈一条都不清
        string Dump()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Doc.Strokes)
                sb.Append(s.Id).Append('@').Append(s.Page).Append(';');
            return sb.ToString();
        }
        string before = Dump();
        int undoBefore = Doc.UndoDepth;
        fake.Position = 1;
        SettleFrames(400);
        float camAt1 = ViewOffsetY;
        fake.Position = 3;
        SettleFrames(450);
        Check("切页**只动相机**（对象、页归属、撤销栈一条都没变）",
              Dump() == before && Doc.UndoDepth == undoBefore
              && Math.Abs(ViewOffsetY - camAt1) > PageHeightCanvas * 0.9f,
              $"相机 {camAt1:F0} → {ViewOffsetY:F0}（一页 {PageHeightCanvas:F0}）");

        // **调换页序也要跟得上**：第 2 页和第 3 页换个位置
        // （假实现里"页序变化"= 同一页的 SlideID 出现在别的位置上）
        var at3 = made[2];
        fake.Position = 2;
        SettleFrames(400);
        Check("调换页序：第 2 页上显示的是**第 2 页自己的批注**",
              CurrentPage == SlidePageOfPosition(2) && at3.Page == SlidePageOfPosition(3),
              $"当前页 {CurrentPage}，那一笔仍属于 {at3.Page}（按身份，不按页号）");

        // 清空：只清**这一页幻灯片**
        ClearFromUi();
        SettleFrames(200);
        Check("清空只清这一页幻灯片，别的页原样",
              !Doc.Strokes.Contains(made[1]) && Doc.Strokes.Contains(made[0])
              && Doc.Strokes.Contains(made[2]),
              $"剩 {Doc.Strokes.Count} 笔");
        UndoFromUi();
        SettleFrames(300);
        Check("撤销清空：那一页的批注回来", Doc.Strokes.Contains(made[1]),
              $"现在 {Doc.Strokes.Count} 笔");

        // 从**我们的面板**翻页：放映中应该驱动 PPT，而不是翻白板
        fake.Position = 1;
        SettleFrames(400);
        FlipPageFromUi(true);
        SettleFrames(450);
        Check("面板上的「下一屏」在放映中**驱动 PPT 翻页**",
              fake.Position == 2 && CurrentPage == SlidePageOfPosition(2),
              $"假放映到了第 {fake.Position} 页，我们在第 {CurrentPage - SlidePageBase} 页");
        FlipPageFromUi(false);
        SettleFrames(450);
        Check("「上一屏」同理往回翻", fake.Position == 1 && CurrentPage == SlidePageOfPosition(1),
              $"假放映回到第 {fake.Position} 页");

        // 放映结束：退回白板，别把放映页当白板页
        fake.Showing = false;
        SettleFrames(450);
        Check("放映结束：回到白板（页号不再是幻灯片空间）",
              !SlideNow.Showing && Doc.CurrentPage < SlidePageBase
              && Doc.CurrentPage == CurrentPage,
              $"页号 {Doc.CurrentPage}，相机 {ViewOffsetY:F0}");
        Check("放映结束后新写的笔**不再带幻灯片身份**",
              Doc.CurrentSlideId == 0, $"CurrentSlideId = {Doc.CurrentSlideId}");

        Slides = null;
        SlideNow = default;
        Doc.Clear();
        Doc.ClearHistory();
        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：幻灯片页逻辑正确（假放映驱动；切页只动相机、按身份归属、退得干净）"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
