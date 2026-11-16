// [职责] 计时器 / 点名 × 文档 E2E（PDF 不被穿透吞掉 / 关文档补穿透）。
// [测试] --timertest

using System;
using System.Collections.Generic;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 计时器 × 文档（2026-11-16 用户报：PDF 开着点「更多 → 计时器」，PDF 整个消失）。
    ///
    /// 根因：手动开窗无条件强制穿透，文档模式下穿透会让 `DrawDocPages` 整层让开。
    /// 判据：状态位 + 浮层命中 + 截图像素（页色真的上屏）；截图 `reports/timer-over-pdf.png`。
    /// </summary>
    private void TimerOverDocGroup(Action<string, bool, string> Check)
    {
        // 假文档：页色 BGRA(60,180,220) 和桌面 / 卡片都分得开；页顶那一小条留给像素探针。
        void OpenFakeDoc(string key)
        {
            CloseDocument();
            var specs = new List<DocPages.Spec>();
            for (int i = 0; i < 2; i++)
                specs.Add(new DocPages.Spec
                {
                    Kind = 0, Source = "timer-doc.png", SourceIndex = i,
                    OutW = 800, OutH = 1120,
                });
            DocPageWorker = false;
            StartDocViewForTest(specs, "计时器-文档.pdf", key);
            DocView.Generator = sp => FakeDocPageBgra(sp.OutW, sp.OutH, 60, 180, 220);
            PrepareAnnotateAfterOpenForTest();     // 产品"打开成功之后"的准备批（退穿透 / 切笔）
            SettleFrames(200);
        }

        // 卡片中心（浮层命中探测用；窗口 1100×700，卡片内缩 60）。
        (float X, float Y) CardCenter()
        {
            var c = TimerWin.CardRect(TimerCardRect(), TimerUnit(), TimerMinimized);
            return ((c.MinX + c.MaxX) * 0.5f, (c.MinY + c.MaxY) * 0.5f);
        }

        string HitAt(float x, float y) => OverlayLayers.PeekHit(x, y)?.Id ?? "无";

        // 页顶 120×24 一条（卡片在屏幕中间，够不着这里）：有页色 = PDF 真的上屏了。
        int PagePixels()
        {
            var p0 = DocView.At(0);
            if (p0 == null) return 0;
            int x = (int)(p0.Rect.MinX + ViewOffsetX) + 40;
            int y = (int)(p0.Rect.MinY + ViewOffsetY) + 8;
            return ScreenProbe.CountNear(x, y, 120, 24, 220, 180, 60, 24);
        }

        // 干净起点
        StopTimerFromUi();
        CloseDocument();
        SetPassThroughFromUi(false);
        SettleFrames(80);

        // ① 桌面（没文档）：照旧强制穿透（行为不变）
        OpenTimerCardFromUi();
        Check("桌面：无文档打开计时器仍强制穿透", TimerCardOpen && PassThrough, $"pass={PassThrough}");

        // ② 桌面计时器开着 → 开文档：退穿透、卡片留在 PDF 上、计时层接输入
        OpenFakeDoc("timer-over-doc-1");
        var cc = CardCenter();
        Check("开文档：PDF 与卡片同时在、已退穿透",
              DocView.IsOpen && TimerCardOpen && !PassThrough,
              $"doc={DocView.IsOpen} card={TimerCardOpen} pass={PassThrough}");
        Check("开文档：计时浮层吃到卡片那块、不铺接输入小窗",
              HitAt(cc.X, cc.Y) == "timer" && !TimerInputWindowShown,
              $"hit={HitAt(cc.X, cc.Y)} 小窗={TimerInputWindowShown}");
        int pxTimerFirst = PagePixels();
        Check("开文档：PDF 页面上屏（不是空白页）", pxTimerFirst > 2000, $"{pxTimerFirst} 像素");

        // ③ 关文档（卡片还开着）→ 回桌面穿透 + 接输入小窗，桌面可用
        CloseDocument();
        TimerTickForTest();
        Check("关文档：计时器还开着 → 补回穿透", TimerCardOpen && PassThrough, $"pass={PassThrough}");
        Check("关文档：穿透接输入小窗挂上", TimerInputWindowShown, $"小窗={TimerInputWindowShown}");
        StopTimerFromUi();
        Check("关窗：恢复非穿透", !PassThrough, $"pass={PassThrough}");

        // ④ 用户报的那条路：文档先开，再点「更多 → 计时器」
        OpenFakeDoc("timer-over-doc-2");
        Check("文档里打开前：非穿透、PDF 在", DocView.IsOpen && !PassThrough,
              $"doc={DocView.IsOpen} pass={PassThrough}");
        OpenTimerCardFromUi();                 // 产品入口（UiHost.Commands.OpenTimerCard 同一句）
        SettleFrames(250);                     // 时间档不要连续帧：至少等一帧把卡片真画上去
        TimerTickForTest();
        var c2 = CardCenter();
        Check("文档里手动打开计时器：文档还在、不穿透、卡片开着",
              DocView.IsOpen && TimerCardOpen && !PassThrough,
              $"doc={DocView.IsOpen} card={TimerCardOpen} pass={PassThrough}");
        Check("文档里手动打开计时器：计时浮层接输入、不铺接输入小窗",
              HitAt(c2.X, c2.Y) == "timer" && !TimerInputWindowShown,
              $"hit={HitAt(c2.X, c2.Y)} 小窗={TimerInputWindowShown}");
        int pxUserPath = PagePixels();
        bool shot = SaveScreenPng("reports/timer-over-pdf.png",
            (int)MathF.Floor(_virtualX), (int)MathF.Floor(_virtualY),
            Math.Max(1, (int)MathF.Ceiling(_virtualW)), Math.Max(1, (int)MathF.Ceiling(_virtualH)));
        Check("截图：reports/timer-over-pdf.png 里 PDF 页色还在",
              shot && pxUserPath > 2000, $"{(shot ? "已存" : "存图失败")} 页色 {pxUserPath} 像素");

        // ⑤ 收尾：关文档 → 自动补穿透；关窗 → 回非穿透、无残留
        CloseDocument();
        Check("收尾：关文档后回到穿透（桌面可点）", PassThrough, $"pass={PassThrough}");
        StopTimerFromUi();
        SetPassThroughFromUi(false);
        SettleFrames(80);
        Check("收尾：无文档 / 无计时窗 / 非穿透",
              !DocView.IsOpen && !TimerCardOpen && !PassThrough,
              $"doc={DocView.IsOpen} card={TimerCardOpen} pass={PassThrough}");

        // ⑥ 点名卡 × 文档（同一条根因：手动开窗无条件强制穿透 → PDF 整层让开）
        RollOverDocGroup(Check);
    }

    /// <summary>
    /// 点名 / 随机一人 × 文档（2026-11-16 同一条根因的孪生面）：
    /// 文档里开点名卡不再强制穿透——PDF 留在屏幕上，卡片那块由 "roll" 浮层接输入；
    /// 桌面 / 放映照旧强制穿透 + 接输入小窗；关文档时按"卡片还开着"把穿透补回来。
    /// </summary>
    private void RollOverDocGroup(Action<string, bool, string> Check)
    {
        // 干净起点
        CloseRollCardFromUi();
        CloseDocument();
        SetPassThroughFromUi(false);
        SettleFrames(80);

        // ① 桌面（没文档）：照旧强制穿透（行为不变）
        OpenRollCardFromUi();
        Check("桌面：无文档打开点名仍强制穿透", RollCardOpen && PassThrough, $"pass={PassThrough}");
        CloseRollCardFromUi();
        SettleFrames(80);
        OpenRollOneFromUi();
        Check("桌面：「随机一人」仍强制穿透", RollCardOpen && RollQuick && PassThrough,
              $"quick={RollQuick} pass={PassThrough}");
        CloseRollCardFromUi();
        SettleFrames(80);

        // ② 桌面点名开着 → 开文档：退穿透、卡片留在 PDF 上、roll 层接输入
        OpenRollCardFromUi();
        RollFakeDocOpen("roll-over-doc-1");
        var c1 = RollCardCenterNow();
        Check("开文档：PDF 与点名卡同时在、已退穿透",
              DocView.IsOpen && RollCardOpen && !PassThrough,
              $"doc={DocView.IsOpen} card={RollCardOpen} pass={PassThrough}");
        Check("开文档：点名浮层吃到卡片那块、不铺接输入小窗",
              RollDocHitAt(c1.X, c1.Y) == "roll" && !RollInputWindowShown,
              $"hit={RollDocHitAt(c1.X, c1.Y)} 小窗={RollInputWindowShown}");
        int pxFirst = RollDocPagePixels();
        Check("开文档：PDF 页面上屏（不是空白页）", pxFirst > 2000, $"{pxFirst} 像素");

        // ③ 关文档（点名卡还开着）→ 回桌面穿透 + 接输入小窗
        CloseDocument();
        RollTickForTest();
        Check("关文档：点名卡还开着 → 补回穿透", RollCardOpen && PassThrough, $"pass={PassThrough}");
        Check("关文档：穿透接输入小窗挂上", RollInputWindowShown, $"小窗={RollInputWindowShown}");
        CloseRollCardFromUi();
        Check("关窗：恢复非穿透", !PassThrough, $"pass={PassThrough}");

        RollOverDocOpenGroup(Check);
    }

    /// <summary>点名 × 文档的第二段：文档先开再点入口（用户报的那条路）。</summary>
    private void RollOverDocOpenGroup(Action<string, bool, string> Check)
    {
        // ④ 用户报的那条路：文档先开，再点「更多 → 点名」
        RollFakeDocOpen("roll-over-doc-2");
        Check("文档里打开前：非穿透、PDF 在", DocView.IsOpen && !PassThrough,
              $"doc={DocView.IsOpen} pass={PassThrough}");
        OpenRollCardFromUi();                  // 产品入口（UiHost.Commands.OpenRollCard 同一句）
        SettleFrames(250);
        RollTickForTest();
        var c2 = RollCardCenterNow();
        Check("文档里手动打开点名：文档还在、不穿透、卡片开着",
              DocView.IsOpen && RollCardOpen && !PassThrough,
              $"doc={DocView.IsOpen} card={RollCardOpen} pass={PassThrough}");
        Check("文档里手动打开点名：roll 浮层接输入、不铺接输入小窗",
              RollDocHitAt(c2.X, c2.Y) == "roll" && !RollInputWindowShown,
              $"hit={RollDocHitAt(c2.X, c2.Y)} 小窗={RollInputWindowShown}");
        int pxUser = RollDocPagePixels();
        bool shot = SaveScreenPng("reports/roll-over-pdf.png",
            (int)MathF.Floor(_virtualX), (int)MathF.Floor(_virtualY),
            Math.Max(1, (int)MathF.Ceiling(_virtualW)), Math.Max(1, (int)MathF.Ceiling(_virtualH)));
        Check("截图：reports/roll-over-pdf.png 里 PDF 页色还在",
              shot && pxUser > 2000, $"{(shot ? "已存" : "存图失败")} 页色 {pxUser} 像素");

        // ⑤ 文档里「随机一人」也不吃 PDF
        CloseRollCardFromUi();
        OpenRollOneFromUi();
        SettleFrames(120);
        var c3 = RollCardCenterNow();
        Check("文档里「随机一人」：文档还在、不穿透、卡片在",
              DocView.IsOpen && RollCardOpen && RollQuick && !PassThrough,
              $"doc={DocView.IsOpen} quick={RollQuick} pass={PassThrough}");
        Check("文档里「随机一人」：roll 浮层接输入",
              RollDocHitAt(c3.X, c3.Y) == "roll" && !RollInputWindowShown,
              $"hit={RollDocHitAt(c3.X, c3.Y)} 小窗={RollInputWindowShown}");

        // ⑥ 收尾：关文档 → 自动补穿透；关窗 → 回非穿透、无残留
        CloseDocument();
        Check("收尾：关文档后回到穿透（桌面可点）", PassThrough, $"pass={PassThrough}");
        CloseRollCardFromUi();
        SetPassThroughFromUi(false);
        SettleFrames(80);
        Check("收尾：无文档 / 无点名窗 / 非穿透",
              !DocView.IsOpen && !RollCardOpen && !PassThrough,
              $"doc={DocView.IsOpen} card={RollCardOpen} pass={PassThrough}");
    }

    /// <summary>假文档：页色 BGRA(60,180,220)，和桌面 / 卡片都分得开。</summary>
    private void RollFakeDocOpen(string key)
    {
        CloseDocument();
        var specs = new List<DocPages.Spec>();
        for (int i = 0; i < 2; i++)
            specs.Add(new DocPages.Spec
            {
                Kind = 0, Source = "roll-doc.png", SourceIndex = i,
                OutW = 800, OutH = 1120,
            });
        DocPageWorker = false;
        StartDocViewForTest(specs, "点名-文档.pdf", key);
        DocView.Generator = sp => FakeDocPageBgra(sp.OutW, sp.OutH, 60, 180, 220);
        PrepareAnnotateAfterOpenForTest();     // 产品"打开成功之后"的准备批（退穿透 / 切笔）
        SettleFrames(200);
    }

    /// <summary>点名卡中心（"roll" 浮层的命中矩形就是窗口矩形）。</summary>
    private (float X, float Y) RollCardCenterNow()
    {
        var r = RollCardRect();
        return ((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
    }

    private string RollDocHitAt(float x, float y) => OverlayLayers.PeekHit(x, y)?.Id ?? "无";

    /// <summary>页面上找一条 120×24 的页色探针；卡片（位置是记忆的）压住就往下让。</summary>
    private int RollDocPagePixels()
    {
        var p0 = DocView.At(0);
        if (p0 == null) return 0;
        var card = RollCardRect();
        float x = p0.Rect.MinX + ViewOffsetX + 40;
        float top = p0.Rect.MinY + ViewOffsetY;
        for (float y = top + 8; y + 24 <= p0.Rect.MaxY + ViewOffsetY; y += 40)
        {
            var band = new RectF { MinX = x, MaxX = x + 120, MinY = y, MaxY = y + 24 };
            if (band.MinY + 24 > _virtualY + _virtualH) break;      // 出屏了就别探
            if (band.Intersects(card)) continue;                     // 卡片挡着这条
            int n = ScreenProbe.CountNear((int)band.MinX, (int)band.MinY, 120, 24, 220, 180, 60, 24);
            if (n > 0) return n;
        }
        return 0;
    }
}
