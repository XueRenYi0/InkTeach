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
    private void PptShow(string path)
    {
        if (path == null)
        {
            Console.WriteLine("  用法：--pptshow <图.bmp> [--panel] [--menu] [--ink] [--wide] [--pass]");
            _quit = true;
            return;
        }

        // 颜色与投影是界面推给引擎的（见 IUiHost.SetFloatingTheme）——
        // 不挂界面拿到的是 UiTheme.Default 那套兜底，出的图就不是产品里的样子。
        SetUiFactory(() => new InkUi.FullUi());

        var fake = new PptFakeSource { Showing = true, Slide = 3, SlideId = 258, Total = 12, Key = "摆样" };
        AttachPptSource(fake, watch: false);
        var argv = Environment.GetCommandLineArgs();

        ResetPptBarPosForTest();            // 从默认位置（左下角）起，出的图才是"第一眼"的样子
        StepPpt();
        SettleFrames(150);
        // 引导那一行字**每次进放映都会出现**（2026-09-27 起），而它和菜单长在同一处、
        // 会互相压着看不清——所以除了专门出它的 `--hint`，其余出图一律把它按下去。
        // ⚠ 必须压在 `StepPpt()` **之后**：进放映那一刻它才被点起来，压早了没用。
        if (!argv.Contains("--hint")) PptHintSuppressForTest();

        // `--ink`：给当前页铺三笔（一长一短一点）——「回放本页墨迹」那一项
        // 亮不亮、右侧"N 笔"对不对，出图时一眼就能看到。
        if (argv.Contains("--ink"))
        {
            var s1 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.90f, 0.20f, 0.20f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s1.AddPoint(300f + 320f * k / 4f, 300f + 40f * k, 0.3f + 0.1f * k, k * 200f);
            Doc.AddStroke(s1);
            var s2 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.13f, 0.45f, 0.90f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s2.AddPoint(320f + 280f * k / 4f, 560f - 30f * k, 0.4f + 0.1f * k, 1200f + k * 180f);
            Doc.AddStroke(s2);
            var dot = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.15f, 0.65f, 0.35f, 1f), Width = 8f,
            };
            dot.AddPoint(780f, 420f, 0.8f, 2200f);
            Doc.AddStroke(dot);
            SettleFrames(120);
        }

        // `--pass`：诊断用——打开穿透，让"接输入小窗"铺上（代码注释说它是 1/255 的灰、
        // 肉眼看不见；这个开关就是拿来核对"到底看不看得见"的）。
        if (argv.Contains("--pass"))
        {
            SetPassThroughFromUi(true);
            SettleFrames(150);
        }

        var bar = PptBarRect();
        float midX = (bar.MinX + bar.MaxX) * 0.5f;
        float midY = (bar.MinY + bar.MaxY) * 0.5f;

        if (argv.Contains("--menu"))
        {
            PptBarPointerDown(midX, midY);      // 2026-10-02 第五轮：点页码 = 菜单
            PptBarPointerUp(midX, midY);
        }
        else if (argv.Contains("--panel"))
        {
            PptBarPointerDown(midX, midY);      // 点页码 → 菜单
            PptBarPointerUp(midX, midY);
            PptMenuItemRectAt(0, out var miJump);
            PptBarPointerDown((miJump.MinX + miJump.MaxX) * 0.5f,   // 菜单第一项 → 页号面板
                              (miJump.MinY + miJump.MaxY) * 0.5f);
        }
        else
        {
            // 悬停在**页码格**上：它才是条上的主角（点它出菜单），顺便把悬停高亮出进图里
            // ——用户 2026-09-26 指出过"页码那里没有悬停指示"，这条图就是给那件事看的。
            PptBarPointerMove(midX, midY);
        }
        SettleFrames(200);

        // 截图范围 = 这一刻要拍的东西（条 ∪ 面板 ∪ 菜单 ∪ 首次引导）
        var box = PptBarRect();
        if (PptPagePanelOpen) { PptPanelRect(out var p); box.Add(p.MinX, p.MinY); box.Add(p.MaxX, p.MaxY); }
        if (PptMenuOpen || PptHintVisible) { PptMenuRect(out var m); box.Add(m.MinX, m.MinY); box.Add(m.MaxX, m.MaxY); }
        float pad = argv.Contains("--wide") ? 160f : 24f;   // --wide：出图带上一圈周围，查"条外面有什么"
        int sx = (int)Math.Max(_virtualX, box.MinX - pad);
        int sy = (int)Math.Max(_virtualY, box.MinY - pad);
        int sw = (int)Math.Min(_virtualX + _virtualW - sx, box.MaxX - box.MinX + pad * 2);
        int sh = (int)Math.Min(_virtualY + _virtualH - sy, box.MaxY - box.MinY + pad * 2);
        bool ok = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
        Console.WriteLine(ok ? $"  已保存 {path}（{sw}×{sh}）" : "  截屏失败");
        _quit = true;
    }

    /// <summary>
    /// `--pptprobe [秒]`：**真机连接探针**（用户在自己的机器上跑一次就知道通不通）。
    ///
    /// 为什么只能这样验：我这边没有 PowerPoint / WPS 环境，产品那条 COM 连接
    /// 没法在自检里跑（自检也不该去碰它——`StartPptLink` 在自检模式下直接返回）。
    /// 这条命令每秒问一次"现在连上谁了、在不在放映、第几页"，把答案打出来。
    ///
    /// 用法（老师的机器上）：
    ///   ① 跑 `InkTeach.exe --pptprobe 20`；
    ///   ② 趁这 20 秒打开一个 PPT，按 F5 放映、翻两页（WPS 演示也一样试一次）；
    ///   ③ 看输出：放映中应当从 False 变 True、页码跟着翻页变、SlideID 是个大整数。
    /// 三条都对 = 连接层在这台机器上没问题（WPS 也照这条走，它就是给国产办公套件铺的路）。
    /// </summary>
    private void PptProbe(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== PPT 连接探针 ===");
        Console.WriteLine($"  接下来 {seconds:F0} 秒里，请去打开一个 PPT 并按 F5 放映、翻两页；");
        Console.WriteLine("  WPS 演示也一样试一次（这条探针就是用来分清哪家的 Office 能连上的）。");
        Console.WriteLine();

        // `--pptcmd`：连接上之后真的按一下 Next / Prev / GotoSlide，看页码跟不跟着动
        //（这条路径走的是 IDispatch::Invoke，和只读属性不是同一条调用）。
        bool cmdTest = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--pptcmd");
        bool cmdDone = !cmdTest;

        var src = new PptComSource();
        int connectedSamples = 0, showingSamples = 0;
        double t0 = NowMs;
        string lastLine = "";
        while (NowMs - t0 < seconds * 1000)
        {
            var s = src.Poll();
            if (s.Connected) connectedSamples++;
            if (s.Showing) showingSamples++;
            string line = $"  连上={s.Connected}  放映中={s.Showing}  第 {s.Slide}/{s.Total} 页  "
                        + $"SlideID={s.SlideId}  标识={s.Key}";
            if (line != lastLine) { Console.WriteLine(line); lastLine = line; }

            if (!cmdDone && s.Showing && NowMs - t0 > 1500)
            {
                // 命令后 PowerPoint 会忙一下，属性可能短暂读失败——轮询重试到恢复为止。
                PptSnapshot WaitShow(int ms)
                {
                    var end = NowMs + ms; var last = default(PptSnapshot);
                    while (NowMs < end) { last = src.Poll(); if (last.Showing) return last; SettleFrames(100); }
                    return last;
                }
                PptSnapshot WaitSlide(int want, int ms)
                {
                    var end = NowMs + ms; var last = default(PptSnapshot);
                    while (NowMs < end) { last = src.Poll(); if (last.Showing && last.Slide == want) return last; SettleFrames(100); }
                    return last;
                }

                int before = s.Slide;

                // ① GotoSlide：动画页也不影响，立刻能验。
                src.GotoSlide(before + 1);
                var sG1 = WaitSlide(before + 1, 2500);
                src.GotoSlide(before);
                var sG0 = WaitSlide(before, 2500);
                bool okGoto = sG1.Showing && sG1.Slide == before + 1 && sG0.Showing && sG0.Slide == before;

                // ② Next：**动画页上要按好几下才翻页**（每一下是一个动画步），
                //    循环到页码变化为止，不是"按一下就必须翻"。
                int nextPresses = 0;
                var sNext = sG0;
                while (nextPresses < 6 && (!sNext.Showing || sNext.Slide <= before))
                { src.Next(); nextPresses++; sNext = WaitShow(1200); }
                bool okNext = sNext.Showing && sNext.Slide > before;

                // ③ Prev：同样循环回到原页；最后 Goto 对齐，防停在动画中间。
                int prevPresses = 0;
                var sPrev = sNext;
                while (prevPresses < 8 && (!sPrev.Showing || sPrev.Slide > before))
                { src.Prev(); prevPresses++; sPrev = WaitShow(1200); }
                src.GotoSlide(before); WaitShow(1500);
                bool okPrev = sPrev.Showing && sPrev.Slide <= before;

                Console.WriteLine($"  命令自检: Goto {before}->{before + 1}->{before} {(okGoto ? "PASS" : "FAIL")}; "
                                + $"Next x{nextPresses}->{sNext.Slide} {(okNext ? "PASS" : "FAIL")}; "
                                + $"Prev x{prevPresses}->{sPrev.Slide} {(okPrev ? "PASS" : "FAIL")}");
                cmdDone = true;
            }

            SettleFrames(200);
        }

        Console.WriteLine();
        if (cmdTest && !cmdDone)
            Console.WriteLine("  命令自检：没跑（探针期间没读到放映状态）");
        Console.WriteLine(showingSamples > 0
            ? $"  PASS：读到过放映状态（{showingSamples} 次采样在放映中）——连接层在这台机器上可用"
            : connectedSamples > 0
                ? $"  半通：连上了演示文稿（{connectedSamples} 次采样），但没读到放映状态——请确认按了 F5 全屏放映"
                : "  不通：一次都没连上（没装桌面 Office / WPS、没有打开的演示文稿，或 COM 被安全软件挡住）");
        _quit = true;
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }

    /// <summary>
    /// 显存已用量（MB）。
    ///
    /// 注意：**别去解析 GpuMb() 返回的字符串**——那是"154/7396 MB"这种给人看的
    /// 格式，TryParse 会失败并静默返回 0（第一版就是这么错的，整列显示 0）。
    /// 要数就直接问同一个 API 要数。
    ///
    /// 为什么这项重要：核显的显存**是从系统内存里分的**，在 4GB 教室机上它和
    /// 进程内存抢的是同一块。--memory 报告里一万笔时显存 154MB，比点数据
    /// （3.9MB）大两个数量级——真正的大头很可能在这里。
    /// </summary>
    private static double GpuUsedMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return 0;
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return info.CurrentUsage / 1048576.0;
        }
        catch { return 0; }
    }
}
