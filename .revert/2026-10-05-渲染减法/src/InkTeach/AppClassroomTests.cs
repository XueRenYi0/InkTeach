using System;
using System.IO;
using System.Linq;
using System.Threading;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 课堂窗自检（2026-10-02 起：计时/点名改为 InkClass 式独立窗口）。
/// 计时：三模式 / 改时长 / 开始暂停 / 到点超时 / 最小化 / 全屏 / 真拖动 / 接输入小窗。
/// 点名：人数 / 抽奖定格 / 去重池 / 三列排布 / 名单 / 快捷态自动关 / 真拖动 / 接输入小窗。
/// </summary>
internal sealed partial class App
{
    /// <summary>`--timertest`：计时窗（独立窗：三模式 / 改时长 / 到点 / 最小化 / 全屏 / 拖动）。</summary>
    private void TimerTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 课堂计时器自检（独立窗：三模式 / 改时长 / 到点 / 最小化 / 全屏 / 接输入小窗）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-44} {detail}");
        }
        void ClickTimer(float x, float y) { TimerPointerDownForTest(x, y); TimerPointerUpForTest(x, y); }
        void ClickRect(in RectF r) => ClickTimer((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
        void ClickMouse(float x, float y, int delay)
        {
            SendMouse((int)x, (int)y, 0);                              SettleFrames(60 + delay);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);    SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);      SettleFrames(250);
        }
        float U() => DpiScale;                                    // 自检机适配缩放 k=1
        RectF Card() => TimerWin.CardRect(TimerCardRect(), U(), TimerMinimized);

        StopTimerFromUi();
        SetPassThroughFromUi(false);
        SettleFrames(120);

        // ① 打开 = 待机窗；尺寸 1100×700；居中
        OpenTimerCardFromUi();
        var win = TimerCardRect();
        Check("打开：窗口开着、待机（未编辑）", TimerCardOpen && !TimerActive && !TimerSettingsOpen);
        Check("尺寸：1100×700 逻辑（卡片内缩 60）",
              MathF.Abs(win.MaxX - win.MinX - TimerWin.DW * DpiScale) < 1.5f
              && MathF.Abs(win.MaxY - win.MinY - TimerWin.DH * DpiScale) < 1.5f,
              $"{win.MaxX - win.MinX:F0}×{win.MaxY - win.MinY:F0}");
        Check("居中：窗口中心 = 屏中心",
              MathF.Abs((win.MinX + win.MaxX) * 0.5f - (_virtualX + _virtualW * 0.5f)) < 1.5f
              && MathF.Abs((win.MinY + win.MaxY) * 0.5f - (_virtualY + _virtualH * 0.5f)) < 1.5f,
              $"({(win.MinX + win.MaxX) * 0.5f:F0},{(win.MinY + win.MaxY) * 0.5f:F0})");

        var c2 = Card();
        float lu = TimerWin.Layout(c2, U());

        // ② 命中：页签/开始/重置/最小化/全屏/关闭/数字
        TimerZone Z(in RectF r) => TimerWin.ZoneAt(c2, lu, (r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f,
                                                   TimerSettingsOpen, TimerKind == TimerMode.Countdown);
        Check("命中：三页签 / 开始 / 重置 / 最小化 / 全屏 / 关闭 / 数字 都判对",
              Z(TimerWin.TabRect(c2, lu, 0)) == TimerZone.Tab0
              && Z(TimerWin.TabRect(c2, lu, 1)) == TimerZone.Tab1
              && Z(TimerWin.TabRect(c2, lu, 2)) == TimerZone.Tab2
              && Z(TimerWin.BtnRect(c2, lu, TimerZone.Start)) == TimerZone.Start
              && Z(TimerWin.BtnRect(c2, lu, TimerZone.Reset)) == TimerZone.Reset
              && Z(TimerWin.BtnRect(c2, lu, TimerZone.Minimize)) == TimerZone.Minimize
              && Z(TimerWin.BtnRect(c2, lu, TimerZone.Fullscreen)) == TimerZone.Fullscreen
              && Z(TimerWin.BtnRect(c2, lu, TimerZone.Close)) == TimerZone.Close
              && Z(TimerWin.ValueRect(c2, lu)) == TimerZone.Value);

        // ③ 三模式页签（待机可切）
        ClickRect(TimerWin.TabRect(c2, lu, 1));
        Check("页签：切「正计时」", TimerKind == TimerMode.CountUp, $"{TimerKind}");
        ClickRect(TimerWin.TabRect(c2, lu, 2));
        Check("页签：切「秒表」", TimerKind == TimerMode.Stopwatch, $"{TimerKind}");
        ClickRect(TimerWin.TabRect(c2, lu, 0));
        Check("页签：切回「倒计时」", TimerKind == TimerMode.Countdown, $"{TimerKind}");

        // ④ 改时长：点数字进入；±5/±1；✓ 返回
        ClickRect(TimerWin.ValueRect(c2, lu));
        Check("待机：点数字 → 改时长态", TimerSettingsOpen);
        StartTimerFromUi(TimerMode.Countdown, 300f);              // 钉一个已知设定（5:00）并跑起来
        ClickRect(TimerWin.BtnRect(c2, lu, TimerZone.Reset));     // 重置回待机（保留设定）
        ClickRect(TimerWin.ValueRect(c2, lu));                    // 再进改时长
        int m0 = TimerSetM;
        ClickRect(TimerWin.StepRect(c2, lu, 1, 0, 1));
        Check("改时长：分 +1", TimerSetM == (m0 + 1) % 60, $"{m0} → {TimerSetM}");
        ClickRect(TimerWin.StepRect(c2, lu, 1, 0, 0));
        ClickRect(TimerWin.StepRect(c2, lu, 1, 1, 0));
        ClickRect(TimerWin.StepRect(c2, lu, 1, 1, 1));
        Check("改时长：+5 / −1 / −5 后回到原值", TimerSetM == m0, $"{TimerSetM}");
        ClickRect(TimerWin.OkRect(c2, lu));
        Check("改时长：点 ✓ 返回", !TimerSettingsOpen);

        // ⑤ 开始 / 暂停 / 继续 / 重置
        ClickRect(TimerWin.BtnRect(c2, lu, TimerZone.Start));
        Check("开始：跑起来、设定 5:00 生效",
              TimerActive && !TimerSettingsOpen && TimerValueMs > 290_000f, $"{TimerValueMs / 1000f:F0}s");
        ClickRect(TimerWin.ValueRect(c2, lu));
        Check("跑着点数字：暂停", TimerPaused);
        ClickRect(TimerWin.ValueRect(c2, lu));
        Check("再点数字：继续", !TimerPaused);
        ClickRect(TimerWin.BtnRect(c2, lu, TimerZone.Start));
        Check("点大圆钮：暂停", TimerPaused);
        ClickRect(TimerWin.BtnRect(c2, lu, TimerZone.Start));
        Check("再点大圆钮：继续", !TimerPaused);
        ClickRect(TimerWin.BtnRect(c2, lu, TimerZone.Reset));
        Check("重置：回待机、保留设定",
              !TimerActive && !TimerFinished && TimerValueMs > 290_000f, $"{TimerValueMs / 1000f:F0}s");

        // ⑥ 到点：只响一次、继续显示 +MM:SS；大圆钮重开
        StartTimerFromUi(TimerMode.Countdown, 1f);
        double t0 = NowMs;
        while (!TimerFinished && NowMs - t0 < 3000) { DrainMessages(); Thread.Sleep(10); TimerTickForTest(); }
        int beeps = TimerBeepCountForTest;
        TimerTickForTest(); TimerTickForTest();
        Check("到点：只响一次 ＋ 跑着要帧",
              TimerFinished && beeps == 1 && TimerBeepCountForTest == 1 && TimerWantsFrame, $"beeps {beeps}");
        t0 = NowMs;
        while (NowMs - t0 < 350) { DrainMessages(); Thread.Sleep(10); TimerTickForTest(); }
        Check("到点后：继续正计时显示 +MM:SS",
              TimerOvertimeForTest > 100f && TimerDisplayText().StartsWith("+"), TimerDisplayText());
        ClickRect(TimerWin.BtnRect(c2, lu, TimerZone.Start));
        Check("到点后点开始：从头重开",
              TimerActive && !TimerFinished && TimerValueMs > 900f, $"{TimerValueMs:F0}ms");
        StopTimerFromUi();

        // ⑦ 帧口径：跑着要帧、暂停/停止不要
        OpenTimerCardFromUi();
        StartTimerFromUi(TimerMode.Stopwatch, 0f);
        TimerTickForTest();
        Check("秒表：跑着要连续帧", TimerWantsFrame);
        Check("秒表：显示百分秒（真秒表手感）", TimerDisplayText().Contains('.'), TimerDisplayText());
        PauseTimerFromUi(); TimerTickForTest();
        Check("暂停：不要帧", !TimerWantsFrame);
        StopTimerFromUi(); TimerTickForTest();
        Check("停止：不要帧", !TimerWantsFrame);

        // ⑧ 最小化（320×150）/ 还原；全屏往返
        OpenTimerCardFromUi();
        var fullWin = TimerCardRect();
        var fullCard = TimerWin.CardRect(fullWin, U(), false);
        float fullLu = TimerWin.Layout(fullCard, U());
        ClickRect(TimerWin.BtnRect(fullCard, fullLu, TimerZone.Minimize));
        var minWin = TimerCardRect();
        Check("最小化：320×150 逻辑",
              TimerMinimized
              && MathF.Abs(minWin.MaxX - minWin.MinX - TimerWin.MinDW * DpiScale) < 1.5f
              && MathF.Abs(minWin.MaxY - minWin.MinY - TimerWin.MinDH * DpiScale) < 1.5f,
              $"{minWin.MaxX - minWin.MinX:F0}×{minWin.MaxY - minWin.MinY:F0}");
        ClickRect(TimerWin.MinimalDigitsRect(minWin, U()));
        var backWin = TimerCardRect();
        Check("最小化：点数字还原 1100×700",
              !TimerMinimized
              && MathF.Abs(backWin.MaxX - backWin.MinX - TimerWin.DW * DpiScale) < 1.5f,
              $"{backWin.MaxX - backWin.MinX:F0}×{backWin.MaxY - backWin.MinY:F0}");
        var backCard = TimerWin.CardRect(backWin, U(), false);
        float backLu = TimerWin.Layout(backCard, U());
        ClickRect(TimerWin.BtnRect(backCard, backLu, TimerZone.Fullscreen));
        var fsWin = TimerCardRect();
        Check("全屏：窗口铺满工作区",
              TimerExpanded
              && MathF.Abs(fsWin.MaxX - fsWin.MinX - _virtualW) < 2f
              && MathF.Abs(fsWin.MaxY - fsWin.MinY - _virtualH) < 2f,
              $"{fsWin.MaxX - fsWin.MinX:F0}×{fsWin.MaxY - fsWin.MinY:F0}");
        Check("全屏：卡片铺满整屏（真全屏，不再内缩 60）",
              MathF.Abs(TimerWin.CardRect(fsWin, U(), false, true).MinX - fsWin.MinX) < 0.5f
              && MathF.Abs(TimerWin.CardRect(fsWin, U(), false, true).MaxY - fsWin.MaxY) < 0.5f);
        var fsCard = TimerWin.CardRect(fsWin, U(), false, true);
        float fsLu = TimerWin.Layout(fsCard, U());
        ClickRect(TimerWin.BtnRect(fsCard, fsLu, TimerZone.Fullscreen));
        Check("全屏：再点还原 1100×700",
              !TimerExpanded
              && MathF.Abs(TimerCardRect().MaxX - TimerCardRect().MinX - TimerWin.DW * DpiScale) < 1.5f);

        // 双击空白 = 全屏开关（用户 2026-10-02 加）
        {
            var dc = TimerWin.CardRect(TimerCardRect(), U(), false);
            float dlu = TimerWin.Layout(dc, U());
            float dbx = dc.MinX + 60f * dlu, dby = dc.MinY + 300f * dlu;
            ClickTimer(dbx, dby); ClickTimer(dbx, dby);
            Check("双击空白：进全屏", TimerExpanded);
            ClickTimer(dbx, dby); ClickTimer(dbx, dby);
            Check("再双击空白：退全屏", !TimerExpanded);
        }

        // 回归（用户 2026-10-02 报）：全屏后点「开始」不许退出全屏
        {
            var fc = TimerWin.CardRect(TimerCardRect(), U(), false);
            float flu = TimerWin.Layout(fc, U());
            ClickRect(TimerWin.BtnRect(fc, flu, TimerZone.Fullscreen));
            var fc2 = TimerWin.CardRect(TimerCardRect(), U(), false, true);
            float flu2 = TimerWin.Layout(fc2, U());
            ClickRect(TimerWin.BtnRect(fc2, flu2, TimerZone.Start));
            Check("全屏后点开始：保持全屏（回归）", TimerActive && TimerExpanded,
                  $"active={TimerActive} full={TimerExpanded}");
            StopTimerFromUi();
        }

        // ⑧.7 三模式独立：切走继续跑 + 页签圆点 + 关窗全停（用户 2026-10-03 定）
        {
            OpenTimerCardFromUi();
            StartTimerFromUi(TimerMode.Countdown, 300f);
            var mc = TimerWin.CardRect(TimerCardRect(), U(), false);
            float ml = TimerWin.Layout(mc, U());
            ClickRect(TimerWin.TabRect(mc, ml, 1));
            Check("切到正计时：倒计时在后台继续跑（页签圆点）",
                  TimerKind == TimerMode.CountUp && TimerTabRunning(0) && !TimerTabRunning(1),
                  $"kind={TimerKind} cd={TimerTabRunning(0)} up={TimerTabRunning(1)}");
            ClickRect(TimerWin.BtnRect(mc, ml, TimerZone.Start));
            Check("正计时开跑", TimerActive && TimerTabRunning(1));
            ClickRect(TimerWin.TabRect(mc, ml, 0));
            Check("切回倒计时：两个都在跑",
                  TimerKind == TimerMode.Countdown && TimerTabRunning(0) && TimerTabRunning(1),
                  $"cd={TimerTabRunning(0)} up={TimerTabRunning(1)}");

            // 后台倒计时到点：照样响一次
            int beeps0 = TimerBeepCountForTest;
            StartTimerFromUi(TimerMode.Countdown, 1f);
            ClickRect(TimerWin.TabRect(mc, ml, 2));          // 切去看秒表
            double tb = NowMs;
            while (TimerBeepCountForTest == beeps0 && NowMs - tb < 3000)
            { DrainMessages(); Thread.Sleep(10); TimerTickForTest(); }
            Check("后台倒计时到点：照样响一次", TimerBeepCountForTest == beeps0 + 1,
                  $"beeps {beeps0} → {TimerBeepCountForTest}");
            ClickRect(TimerWin.TabRect(mc, ml, 0));
            Check("切回倒计时：显示超时 +MM:SS",
                  TimerFinished && TimerDisplayText().StartsWith("+"), TimerDisplayText());

            // 关窗 = 全部停止（含后台在跑的）
            ClickRect(TimerWin.TabRect(mc, ml, 2));
            ClickRect(TimerWin.BtnRect(mc, ml, TimerZone.Start));   // 秒表也开跑
            Check("关窗前：倒计时（超时中）与秒表都在跑",
                  TimerTabRunning(0) && TimerTabRunning(2),
                  $"cd={TimerTabRunning(0)} sw={TimerTabRunning(2)}");
            ClickRect(TimerWin.BtnRect(mc, ml, TimerZone.Close));
            OpenTimerCardFromUi();
            Check("关窗 = 全部停止（后台也停）",
                  !TimerTabRunning(0) && !TimerTabRunning(1) && !TimerTabRunning(2) && !TimerActive);
            StopTimerFromUi();
        }

        // ⑨ 真拖动：按住空白才跟手；松手 / 单击后移动一步都不许动
        {
            OpenTimerCardFromUi();
            SettleFrames(200);
            TimerTickForTest();          // 先把穿透接输入小窗同步出来，再动真鼠标
            SettleFrames(200);
            var w0 = TimerCardRect();
            var k0 = TimerWin.CardRect(w0, U(), false);
            float klu = TimerWin.Layout(k0, U());
            float bx = k0.MinX + 60f * klu;
            float by = k0.MinY + 300f * klu;
            float tx2 = bx + 140f * klu, ty2 = by + 90f * klu;
            SendMouse((int)bx, (int)by, 0);                               SettleFrames(60);
            SendMouse((int)bx, (int)by, Native.MOUSEEVENTF_LEFTDOWN);     SettleFrames(60);
            for (int i = 1; i <= 8; i++)
            {
                SendMouse((int)(bx + (tx2 - bx) * i / 8f), (int)(by + (ty2 - by) * i / 8f), 0);
                SettleFrames(20);
            }
            SendMouse((int)tx2, (int)ty2, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(250);
            var moved = TimerCardRect();
            Check("真拖动：按住空白跟手",
                  moved.MinX > w0.MinX + 60f * DpiScale && moved.MinY > w0.MinY + 30f * DpiScale,
                  $"({w0.MinX:F0},{w0.MinY:F0}) → ({moved.MinX:F0},{moved.MinY:F0})");
            for (int i = 1; i <= 8; i++)
            {
                SendMouse(_virtualX + _virtualW * i / 9, _virtualY + _virtualH * i / 9, 0);
                SettleFrames(20);
            }
            SettleFrames(150);
            var after = TimerCardRect();
            Check("松手后移动：窗口不动",
                  MathF.Abs(after.MinX - moved.MinX) < 1.5f && MathF.Abs(after.MinY - moved.MinY) < 1.5f,
                  $"({moved.MinX:F0},{moved.MinY:F0}) → ({after.MinX:F0},{after.MinY:F0})");

            var k1 = TimerWin.CardRect(TimerCardRect(), U(), false);
            float klu1 = TimerWin.Layout(k1, U());
            ClickMouse(k1.MinX + 60f * klu1, k1.MinY + 300f * klu1, 0);
            var posB = TimerCardRect();
            for (int i = 1; i <= 8; i++)
            {
                SendMouse(_virtualX + _virtualW * i / 9, _virtualY + _virtualH * i / 9, 0);
                SettleFrames(20);
            }
            SettleFrames(150);
            var posA = TimerCardRect();
            Check("单击后移动：同样不动（没按住不进入拖动）",
                  MathF.Abs(posA.MinX - posB.MinX) < 1.5f && MathF.Abs(posA.MinY - posB.MinY) < 1.5f,
                  $"({posB.MinX:F0},{posB.MinY:F0}) → ({posA.MinX:F0},{posA.MinY:F0})");
            StopTimerFromUi();
            SettleFrames(120);
        }

        // ⑩ 格式（纯函数）
        Check("格式：00:00 / 01:01 / 1:01:01 / .cc / 超时 +",
              TimerWin.Format(0f, TimerMode.Countdown) == "00:00"
              && TimerWin.Format(61000f, TimerMode.Countdown) == "01:01"
              && TimerWin.Format(3661000f, TimerMode.CountUp) == "1:01:01"
              && TimerWin.Format(12340f, TimerMode.Stopwatch) == "00:12.34"
              && TimerWin.FormatOvertime(27000f) == "+00:27");

        // ⑪ 穿透：接输入小窗 = 窗口矩形；停止即收
        OpenTimerCardFromUi();
        StartTimerFromUi(TimerMode.CountUp, 0f);
        SetPassThroughFromUi(true);
        TimerTickForTest();
        TimerInputWindowRect(out var iwr);
        var w2 = TimerCardRect();
        Check("穿透：窗口那块铺了接输入小窗（矩形 = 窗）",
              TimerInputWindowShown
              && MathF.Abs(iwr.MinX - w2.MinX) < 1.5f && MathF.Abs(iwr.MinY - w2.MinY) < 1.5f
              && MathF.Abs(iwr.MaxX - w2.MaxX) < 1.5f && MathF.Abs(iwr.MaxY - w2.MaxY) < 1.5f,
              $"窗 {iwr.MinX:F0},{iwr.MinY:F0}-{iwr.MaxX:F0},{iwr.MaxY:F0}");
        StopTimerFromUi();
        TimerTickForTest();
        Check("停止：接输入小窗立刻收掉", !TimerInputWindowShown);
        SetPassThroughFromUi(false);

        // ⑫ 课堂窗自动穿透（记事本 / 课件可交互），关窗恢复
        SetPassThroughFromUi(false);
        OpenTimerCardFromUi();
        Check("课堂窗：打开自动切穿透", PassThrough);
        StopTimerFromUi();
        Check("课堂窗：关掉自动恢复非穿透", !PassThrough);

        // ⑬ UiState 与引擎一致
        OpenTimerCardFromUi();
        var st = Host.State;
        Check("UiState：Timer* 与引擎一致",
              st.TimerCardOpen == TimerCardOpen && st.TimerSettingsOpen == TimerSettingsOpen
              && st.TimerMode == TimerKind);
        StopTimerFromUi(); SettleFrames(100);

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        if (fail > 0) ExitCode = 1;
        _quit = true;
    }

    /// <summary>`--rolltest`：点名窗（人数 / 抽奖定格 / 去重池 / 三列 / 名单 / 快捷态 / 拖动）。</summary>
    private void RollTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 课堂点名自检（独立窗：人数 / 抽奖定格 / 去重池 / 三列 / 名单 / 快捷态 / 接输入小窗）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-44} {detail}");
        }
        void ClickRoll(float x, float y) { RollPointerDownForTest(x, y); RollPointerUpForTest(x, y); }
        void ClickRect(in RectF r) => ClickRoll((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
        void RunRollToEnd()
        {
            double t0 = NowMs;
            while (RollingNow && NowMs - t0 < 3000) { DrainMessages(); Thread.Sleep(10); RollTickForTest(); }
        }
        float U() => DpiScale;

        CloseRollCardFromUi();
        SetPassThroughFromUi(false);
        SetRollSeedForTest(12345);
        string noNames = Path.Combine(Path.GetTempPath(), "inkteach-no-names.txt");
        try { File.Delete(noNames); } catch { }
        InkEngine.InkEngine.NamesPathOverride = noNames;
        ReloadNamesFromUi();
        SettleFrames(100);

        // ① 打开：900×500；待抽态；无名单 = 1..60
        OpenRollCardFromUi();
        var win = RollCardRect();
        Check("打开：窗口开着、待抽态", RollCardOpen && RollSettingsOpen && !RollingNow);
        Check("尺寸：900×500 逻辑",
              MathF.Abs(win.MaxX - win.MinX - RollWin.DW * DpiScale) < 1.5f
              && MathF.Abs(win.MaxY - win.MinY - RollWin.DH * DpiScale) < 1.5f,
              $"{win.MaxX - win.MinX:F0}×{win.MaxY - win.MinY:F0}");
        Check("无名单：候选 = 1..60", !RollHasNames && RollTotalNow == 60, $"{RollTotalNow}");

        // ② 命中：−/＋/抽奖/池重置/名单/重读/关闭
        var card = RollCardRect();
        float u = U();
        float MCX(in RectF r) => (r.MinX + r.MaxX) * 0.5f;
        float MCY(in RectF r) => (r.MinY + r.MaxY) * 0.5f;
        Check("命中：− / ＋ / 抽奖 / 池重置 / 重读 / 关闭 都判对",
              RollWin.ZoneAt(card, u, MCX(RollWin.MinusRect(card, u)), MCY(RollWin.MinusRect(card, u))) == RollZone.CountMinus
              && RollWin.ZoneAt(card, u, MCX(RollWin.PlusRect(card, u)), MCY(RollWin.PlusRect(card, u))) == RollZone.CountPlus
              && RollWin.ZoneAt(card, u, MCX(RollWin.DrawRect(card, u)), MCY(RollWin.DrawRect(card, u))) == RollZone.Draw
              && RollWin.ZoneAt(card, u, MCX(RollWin.PoolResetRect(card, u)), MCY(RollWin.PoolResetRect(card, u))) == RollZone.PoolReset

              && RollWin.ZoneAt(card, u, MCX(RollWin.ReloadRect(card, u)), MCY(RollWin.ReloadRect(card, u))) == RollZone.Reload
              && RollWin.ZoneAt(card, u, MCX(RollWin.CloseRect(card, u)), MCY(RollWin.CloseRect(card, u))) == RollZone.Close);

        // ③ 人数夹取：1..60
        for (int i = 0; i < 8; i++) ClickRect(RollWin.MinusRect(card, u));
        Check("人数：夹到 1", RollCountNow == 1, $"{RollCountNow}");
        ClickRect(RollWin.PlusRect(card, u));
        Check("人数：＋ → 2", RollCountNow == 2, $"{RollCountNow}");

        // ④ 抽奖：滚动要帧 → 750ms 定格；池 60−2
        ClickRect(RollWin.DrawRect(card, u));
        Check("抽奖：进入滚动、要连续帧", RollingNow && RollWantsFrame);
        RunRollToEnd();
        Check("定格：结果 2 条、不重复、池剩 58",
              !RollingNow && RollResultNow.Length == 2 && RollResultNow.Distinct().Count() == 2 && RollPoolNow == 58,
              $"结果 {RollResultNow.Length}、池 {RollPoolNow}");
        Check("定格后：不要帧", !RollWantsFrame);

        // ⑤ 再抽：两轮不重叠；池重置回满
        var first = RollResultNow.ToArray();
        ClickRect(RollWin.DrawRect(card, u));
        RunRollToEnd();
        bool noOverlap = !first.Intersect(RollResultNow).Any();
        Check("再抽：去重生效（池 58→56）", noOverlap && RollPoolNow == 56, $"池 {RollPoolNow}");
        ClickRect(RollWin.PoolResetRect(card, u));
        Check("池重置：回满 60、已抽归零", RollPoolNow == 60 && RollDrawnNow == 0, $"池 {RollPoolNow}");

        // ⑥ 三列排布：一次 12 人
        for (int i = 0; i < 10; i++) ClickRect(RollWin.PlusRect(card, u));
        Check("人数：拉到 12", RollCountNow == 12, $"{RollCountNow}");
        ClickRect(RollWin.DrawRect(card, u));
        RunRollToEnd();
        Check("12 人：结果 12 条、不重复、按三列排布",
              RollResultNow.Length == 12 && RollResultNow.Distinct().Count() == 12 && RollWin.Cols(12) == 3,
              $"结果 {RollResultNow.Length} 条 / {RollWin.Cols(12)} 列");

        // ⑥.5 学号范围可调（无名单）：1–N
        {
            SetRollMaxForTest(45);
            Check("学号范围：设成 45 → 候选 45", RollTotalNow == 45, $"{RollTotalNow}");
            card = RollCardRect();
            ClickRect(RollWin.NumMinusRect(card, u));
            Check("学号范围：− → 44", RollMaxNumNow == 44, $"{RollMaxNumNow}");
            ClickRect(RollWin.NumPlusRect(card, u));
            ClickRect(RollWin.NumPlusRect(card, u));
            Check("学号范围：＋＋ → 46", RollMaxNumNow == 46, $"{RollMaxNumNow}");
            ClickRect(RollWin.DrawRect(card, u));
            RunRollToEnd();
            Check("范围抽取：结果都在 1–46",
                  RollResultNow.Length == RollCountNow
                  && RollResultNow.All(x => int.TryParse(x, out int v) && v >= 1 && v <= 46),
                  string.Join("、", RollResultNow));
        }

        // ⑦ 名单：临时 Names.txt → 重读 → 抽出来的是名字
        string namesPath = Path.Combine(Path.GetTempPath(), "inkteach-names-test.txt");
        File.WriteAllLines(namesPath, new[] { "# 注释行", "张三", "李四", "王五" });
        InkEngine.InkEngine.NamesPathOverride = namesPath;
        ReloadNamesFromUi();
        Check("名单：读入 3 人（注释不算）、人数夹到 3",
              Names.Length == 3 && RollHasNames && RollCountNow == 3, $"{Names.Length} 人 / 一次 {RollCountNow}");
        ClickRect(RollWin.DrawRect(card, u));
        RunRollToEnd();
        Check("有名单：抽出来的是名字且不重复",
              RollResultNow.Length == 3 && RollResultNow.All(n => Names.Contains(n))
              && RollResultNow.Distinct().Count() == 3,
              string.Join("、", RollResultNow));

        int maxBeforeNames = RollMaxNumNow;
        card = RollCardRect();
        ClickRect(RollWin.NumMinusRect(card, u));
        Check("有名单：学号加减不生效", RollMaxNumNow == maxBeforeNames, $"{RollMaxNumNow}");

        // ⑧ 快捷态：自动抽 1 人、出结果 1.5 秒后自动关
        CloseRollCardFromUi();
        OpenRollOneForTest();
        Check("快捷态：自动开抽、一次 1 人", RollCardOpen && RollingNow && RollCountNow == 1,
              $"rolling={RollingNow} count={RollCountNow}");
        RunRollToEnd();
        Check("快捷态：出结果 1 条（池已抽空 → 自动重置后再抽）",
              !RollingNow && RollResultNow.Length == 1, $"结果 {RollResultNow.Length}");
        double t1 = NowMs;
        while (RollCardOpen && NowMs - t1 < 3000) { DrainMessages(); Thread.Sleep(10); RollTickForTest(); }
        Check("快捷态：1.5 秒后自动关窗", !RollCardOpen);

        // ⑨ 关闭按钮
        OpenRollCardFromUi();
        card = RollCardRect();
        ClickRect(RollWin.CloseRect(card, u));
        Check("✕：关窗", !RollCardOpen);

        // ⑩ 真拖动：按住空白才跟手；松手后移动一步都不许动
        {
            OpenRollCardFromUi();
            SettleFrames(200);
            RollTickForTest();           // 同步穿透接输入小窗
            SettleFrames(200);
            card = RollCardRect();
            float bx = card.MinX + 120f * u;
            float by = card.MaxY - 40f * u;
            float tx2 = bx + 120f * u, ty2 = by - 80f * u;
            SendMouse((int)bx, (int)by, 0);                               SettleFrames(60);
            SendMouse((int)bx, (int)by, Native.MOUSEEVENTF_LEFTDOWN);     SettleFrames(60);
            for (int i = 1; i <= 8; i++)
            {
                SendMouse((int)(bx + (tx2 - bx) * i / 8f), (int)(by + (ty2 - by) * i / 8f), 0);
                SettleFrames(20);
            }
            SendMouse((int)tx2, (int)ty2, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(250);
            var moved = RollCardRect();
            Check("真拖动：按住空白跟手",
                  moved.MinX > card.MinX + 40f * DpiScale || moved.MinY < card.MinY - 20f * DpiScale,
                  $"({card.MinX:F0},{card.MinY:F0}) → ({moved.MinX:F0},{moved.MinY:F0})");
            for (int i = 1; i <= 8; i++)
            {
                SendMouse(_virtualX + _virtualW * i / 9, _virtualY + _virtualH * i / 9, 0);
                SettleFrames(20);
            }
            SettleFrames(150);
            var after = RollCardRect();
            Check("松手后移动：窗口不动",
                  MathF.Abs(after.MinX - moved.MinX) < 1.5f && MathF.Abs(after.MinY - moved.MinY) < 1.5f);
            CloseRollCardFromUi();
            SettleFrames(120);
        }

        // ⑪ 穿透：接输入小窗 = 窗口矩形；关窗即收
        OpenRollCardFromUi();
        SetPassThroughFromUi(true);
        RollTickForTest();
        RollInputWindowRect(out var iwr);
        var w3 = RollCardRect();
        Check("穿透：窗口那块铺了接输入小窗（矩形 = 窗）",
              RollInputWindowShown
              && MathF.Abs(iwr.MinX - w3.MinX) < 1.5f && MathF.Abs(iwr.MinY - w3.MinY) < 1.5f
              && MathF.Abs(iwr.MaxX - w3.MaxX) < 1.5f && MathF.Abs(iwr.MaxY - w3.MaxY) < 1.5f,
              $"窗 {iwr.MinX:F0},{iwr.MinY:F0}-{iwr.MaxX:F0},{iwr.MaxY:F0}");
        CloseRollCardFromUi();
        RollTickForTest();
        Check("✕：关窗、接输入小窗收掉", !RollCardOpen && !RollInputWindowShown);
        SetPassThroughFromUi(false);

        // ⑫ 课堂窗自动穿透 + 恢复
        SetPassThroughFromUi(false);
        OpenRollCardFromUi();
        Check("点名窗：打开自动切穿透", PassThrough);
        CloseRollCardFromUi();
        Check("点名窗：关掉自动恢复非穿透", !PassThrough);

        // ⑬ UiState 一致
        OpenRollCardFromUi();
        Check("UiState：RollCardOpen / RollSettingsOpen 与引擎一致",
              Host.State.RollCardOpen == RollCardOpen && Host.State.RollSettingsOpen == RollSettingsOpen);
        CloseRollCardFromUi();

        // 收尾：清掉测试名单与覆盖
        InkEngine.InkEngine.NamesPathOverride = null;
        ReloadNamesFromUi();
        try { File.Delete(namesPath); } catch { }
        try { File.Delete(noNames); } catch { }
        SettleFrames(100);

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        if (fail > 0) ExitCode = 1;
        _quit = true;
    }
}
