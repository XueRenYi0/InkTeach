// 本文件由 App.cs 拆出（2026-10-07）：PanelAndKeys 这一组。
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

    private void PanelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 产品界面自检（球 → 按钮带）===");

        if (SkipIfNoSyntheticInput("产品界面自检")) { _quit = true; return; }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        void ClickPhysical(float x, float y)
        {
            SendMouse((int)x, (int)y, 0);                            SettleFrames(80);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(320);
        }

        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(300);

        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine($"  界面没挂上：当前 = {CurrentUi.Name}");
            ExitCode = 1;
            _quit = true;
            return;
        }

        // ---- 投影曲线：**没有"袋状"硬边**（用户 2026-10-01 报的"菜单栏周围半透明黑影袋子"）----
        // 每层都是硬边填充，所以**最外那一层的 α 就是"袋子"的外沿轮廓**。从现象反推的判据：
        // 层数够多把台阶磨平、最外层淡到看不见、逐层向外只淡不变深。
        {
            var sh = FloatingTheme.Shadow;
            bool mono = true; string note = "";
            for (int i = 1; i < sh.Length; i++)
            {
                if (sh[i].Inflate <= sh[i - 1].Inflate || sh[i].Color.A > sh[i - 1].Color.A + 1e-4f)
                { mono = false; note = $"第 {i} 层 胀 {sh[i].Inflate:F1} α {sh[i].Color.A:F4}"; break; }
            }
            Check("投影：层数 ≥ 8、逐层向外只淡不变深（台阶磨平）",
                  sh.Length >= 8 && mono, $"{sh.Length} 层；{note}");
            Check("投影：最外层 α ≤ 0.01（浅底上看不见硬边，没有'袋子'）",
                  sh.Length > 0 && sh[sh.Length - 1].Color.A <= 0.01f,
                  $"最外层 α={sh[sh.Length - 1].Color.A:F4}（≈{sh[sh.Length - 1].Color.A * 255f:F1} 个色阶）");
            Check("投影：最远够得着的一圈 ≤ PaintMargin（不被界面裁剪切掉）",
                  FloatingTheme.ShadowReachLogical <= InkUi.Tokens.PaintMargin + 0.01f,
                  $"reach {FloatingTheme.ShadowReachLogical:F0} ≤ margin {InkUi.Tokens.PaintMargin:F0}");
            var shd = InkUi.Tokens.ShadowDark;
            bool monoD = shd.Length >= 8;
            string noteD = $"{shd.Length} 层";
            for (int i = 1; i < shd.Length; i++)
            {
                if (shd[i].Inflate <= shd[i - 1].Inflate || shd[i].Color.A > shd[i - 1].Color.A + 1e-4f)
                { monoD = false; noteD = $"第 {i} 层 胀 {shd[i].Inflate:F1} α {shd[i].Color.A:F4}"; break; }
            }
            Check("投影（深色）：与浅色同一套判据（≥8 层、只淡不变深、外层 ≤0.01）",
                  monoD && shd[shd.Length - 1].Color.A <= 0.01f,
                  $"{noteD}，最外层 α={shd[shd.Length - 1].Color.A:F3}");
        }

        // ---- ⓪ 贴边隐藏 + **刚启动**：不许一上来就收（用户 2026-09-17）----
        //
        // 原来 `_leftAtMs` 初值是负无穷，第一帧就满足"离开够久了"——一开机面板立刻收成
        // 屏幕底边那条 8 像素的露头（而且露头正好压在任务栏上），老师根本找不到它。
        // 现在：**先露着**，等指针碰过面板一次才允许自动收。
        {
            // 先把指针挪到画布上（确保没碰过面板），再把"贴边隐藏"写进偏好、重新挂一个界面
            // ——重新挂就等于"刚启动"那一刻。
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.3f), 0);
            SettleFrames(200);
            SetUiPref("hide", "1");
            SetUiFactory(() => new InkUi.FullUi());
            ui = CurrentUi as InkUi.FullUi;
            SettleFrames(1800);                 // 等过"离开 700 毫秒才收"那一段
            var b0 = ui.QueryBounds();
            Check("贴边隐藏开着＋刚启动：面板还是完整的（不许一上来就收）",
                  (b0.MaxY - b0.MinY) > 40f && !ui.PeekArmedForTest,
                  $"占用 {b0.MaxX - b0.MinX:F0}×{b0.MaxY - b0.MinY:F0}，允许自动收 = {ui.PeekArmedForTest}");

            // 先碰一下面板（把 `_peekArmed` 置真 = 允许自动收），再把它**拖到屏幕最底下**。
            //
            // ⚠ 2026-09-30 第二轮收紧后，触发条件是"**离屏幕底边 ≤10**"（`DockHideDistance`）：
            // 默认位置离屏幕底 52（任务栏 48 + 离任务栏 4），**不再触发隐藏**——
            // 要收起来就得把它拖到屏幕最底下（这一段压过任务栏）。
            var b0c = ui.QueryBounds();
            float m0x = (b0c.MinX + b0c.MaxX) * 0.5f * DpiScale;
            float m0y = (b0c.MinY + b0c.MaxY) * 0.5f * DpiScale;
            SendMouse((int)m0x, (int)m0y, 0);                              SettleFrames(250);
            SendMouse((int)m0x, (int)m0y, Native.MOUSEEVENTF_LEFTDOWN);     SettleFrames(50);
            float toBottomY = _virtualY + _virtualH + 60 * (float)DpiScale; // 拖过屏幕底，靠夹取兜住
            for (int i = 1; i <= 8; i++)
            {
                SendMouse((int)m0x, (int)(m0y + (toBottomY - m0y) * i / 8f), 0);
                SettleFrames(20);
            }
            SendMouse((int)m0x, (int)toBottomY, Native.MOUSEEVENTF_LEFTUP);  SettleFrames(200);
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.3f), 0);
            SettleFrames(2000);
            var b1 = ui.QueryBounds();
            Check("碰过 + 拖到屏幕底边附近再离开：这时才收成露头",
                  (b1.MaxY - b1.MinY) < 12f || (b1.MaxX - b1.MinX) < 12f,
                  $"占用 {b1.MaxX - b1.MinX:F0}×{b1.MaxY - b1.MinY:F0}，允许自动收 = {ui.PeekArmedForTest}");

            // ---- 露头状态下"写一笔"：面板**不许自己蹦回来**（用户 2026-09-18 报的）----
            //
            // 起因是 `UpdatePeek` 里 `keepOpen` 那条把 `IsDrawing` 和"指针在面板上"混在了一起：
            // 规则的本意是**写字中不许收**，写成了"写字中强制展开"，于是藏好的面板一落笔就被拽出来。
            // 判据：在**画布中间**（离那条露头足够远，排除"是碰到面板才展开的"）画一笔，
            // 画完占用矩形必须还是露头那一条。
            {
                float inkX = _virtualX + _virtualW * 0.5f;
                float inkY = _virtualY + _virtualH * 0.4f;
                SendMouse((int)inkX, (int)inkY, 0);                          SettleFrames(60);
                SendMouse((int)inkX, (int)inkY, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
                for (int i = 1; i <= 5; i++)
                { SendMouse((int)(inkX + 20 * i * DpiScale), (int)inkY, 0); SettleFrames(20); }
                SendMouse((int)(inkX + 100 * DpiScale), (int)inkY, Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(250);

                var b2 = ui.QueryBounds();
                Check("露头时在画布上写一笔：面板不许被拽出来",
                      (b2.MaxY - b2.MinY) < 12f || (b2.MaxX - b2.MinX) < 12f,
                      $"占用 {b2.MaxX - b2.MinX:F0}×{b2.MaxY - b2.MinY:F0}"
                      + $"，露头值 peek={ui.PeekForTest:F2}（0 = 收着），"
                      + $"允许自动收 = {ui.PeekArmedForTest}，"
                      + $"画完真出了笔 = {Doc.Strokes.Count > 0}");

                // ---- 反过来那一半也要钉住：**面板开着的时候写字，不许它自己收** ----
                //
                // 这是那条规则原本的用途（"老师写到屏幕边上，工具条不能自己缩回去"）。
                // 修上面那个 bug 时很容易顺手把这条一起弄丢，所以单独钉一条。
                SendMouse((int)((b1.MinX + b1.MaxX) * 0.5f * DpiScale),
                          (int)((b1.MinY + b1.MaxY) * 0.5f * DpiScale), 0);   // 碰露头，把它叫回来
                SettleFrames(400);
                float ix = _virtualX + _virtualW * 0.5f;
                float iy = _virtualY + _virtualH * 0.4f;
                SendMouse((int)ix, (int)iy, 0);                           SettleFrames(60);
                SendMouse((int)ix, (int)iy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
                for (int i = 1; i <= 8; i++)
                { SendMouse((int)(ix + 18 * i * DpiScale), (int)iy, 0); SettleFrames(120); }  // 按住写约 1 秒
                var mid = ui.QueryBounds();
                SendMouse((int)(ix + 144 * DpiScale), (int)iy, Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(200);
                Check("面板开着时按着写约 1 秒：不许它自己收",
                      (mid.MaxY - mid.MinY) > 40f,
                      $"写字中占用 {mid.MaxX - mid.MinX:F0}×{mid.MaxY - mid.MinY:F0}");
            }

            // ---- 贴边隐藏**只认底边**（用户 2026-09-30："只要拖动到底边或者离底边很近才贴边隐"）----
            //
            // 沿革：这一格原来验的是"收起态四边都能藏、展开态只在上下藏"（2026-09-18 定的）。
            // 现在规则收紧成一条：**只有贴底才藏**。所以三件事都要钉：
            //   · 球停在左边中段（**离底边远**）→ **不许藏**（旧规则会藏成竖的 8×48）；
            //   · 球拖到底边附近 → **必须藏**（横的那条露头）；
            //   · 展开态停在左边中段 → 也不藏（只有底边才贴边隐）。
            // ⚠ 位置必须"左**且**不贴底"才算数：停在左下角时它当然该藏（那本来就是站在底边上）。
            {
                // 此刻面板是展开的、贴在屏幕下边（可能正在收着露头）——先碰一下叫回来，
                // 再收成球。
                var side0 = ui.QueryBounds();
                SendMouse((int)((side0.MinX + side0.MaxX) * 0.5f * DpiScale),
                          (int)((side0.MinY + side0.MaxY) * 0.5f * DpiScale), 0);
                SettleFrames(400);
                if (ui.ExpandedForTest)
                {
                    var c0 = ui.CellRectForTest(0);
                    ClickPhysical((c0.MinX + c0.MaxX) * 0.5f * DpiScale,
                                  (c0.MinY + c0.MaxY) * 0.5f * DpiScale);
                    SettleFrames(350);
                }

                // 把球拖到**左边中段**：球左落在离边 20、纵向在屏幕 40% 处（离底边远着呢）。
                var side1 = ui.QueryBounds();
                float bx = (side1.MinX + side1.MaxX) * 0.5f * DpiScale;
                float by = (side1.MinY + side1.MaxY) * 0.5f * DpiScale;
                int toX = _virtualX + (int)((20f + 24f) * DpiScale);        // 球宽 48：中心 = 左 + 24
                int toY = (int)(_virtualY + _virtualH * 0.4f);              // 纵向中段
                SendMouse((int)bx, (int)by, 0);                            SettleFrames(60);
                SendMouse((int)bx, (int)by, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(50);
                for (int i = 1; i <= 8; i++)
                {
                    SendMouse((int)(bx + (toX - bx) * i / 8f), (int)(by + (toY - by) * i / 8f), 0);
                    SettleFrames(20);
                }
                SendMouse(toX, toY, Native.MOUSEEVENTF_LEFTUP);             SettleFrames(200);

                // 看一眼就走：球停在左边中段——**不许藏**
                SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.7f), 0);
                SettleFrames(1400);
                var atLeft = ui.QueryBounds();
                Check("收起态停在左边中段：**不藏**（贴边隐藏只认底边）",
                      (atLeft.MaxX - atLeft.MinX) > 30f && (atLeft.MaxY - atLeft.MinY) > 30f,
                      $"占用 {atLeft.MaxX - atLeft.MinX:F0}×{atLeft.MaxY - atLeft.MinY:F0}，"
                      + $"展开 = {ui.ExpandedForTest}");

                // 再把它拖到**屏幕底边附近**（球底离屏幕底 5 逻辑像素）→ 走开 → 这一次才该藏。
                //
                // ⚠ 触发条件按**屏幕**底边算（2026-09-30 第二批收紧：离屏幕底 ≤10）；
                // 工作区底（任务栏上沿）那条线**不算**——默认位置离屏幕底 52，根本不触发。
                var side2 = ui.QueryBounds();
                float c2x = (side2.MinX + side2.MaxX) * 0.5f * DpiScale;
                float c2y = (side2.MinY + side2.MaxY) * 0.5f * DpiScale;
                // 球高 48：中心放在"屏幕底 − 5 − 24"处，球底就落在离屏幕底 5 的位置。
                float toY2 = (float)(_virtualY + _virtualH) - 29f * (float)DpiScale;
                SendMouse((int)c2x, (int)c2y, 0);                          SettleFrames(60);
                SendMouse((int)c2x, (int)c2y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
                for (int i = 1; i <= 8; i++) { SendMouse((int)c2x, (int)(c2y + (toY2 - c2y) * i / 8f), 0); SettleFrames(20); }
                SendMouse((int)c2x, (int)toY2, Native.MOUSEEVENTF_LEFTUP);  SettleFrames(200);
                SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.35f), 0);
                SettleFrames(1400);
                var tucked = ui.QueryBounds();
                Check("拖到屏幕底边附近：这时才藏（横的那条露头）",
                      (tucked.MaxY - tucked.MinY) < 12f && (tucked.MaxX - tucked.MinX) > 30f,
                      $"占用 {tucked.MaxX - tucked.MinX:F0}×{tucked.MaxY - tucked.MinY:F0}，"
                      + $"展开 = {ui.ExpandedForTest}，露头值 peek={ui.PeekForTest:F2}");

                // 碰回来 → 点开成条 → 再拖到**左边中段**、走开：展开态同样**不藏**
                SendMouse((int)((tucked.MinX + tucked.MaxX) * 0.5f * DpiScale),
                          (int)((tucked.MinY + tucked.MaxY) * 0.5f * DpiScale), 0);
                SettleFrames(450);
                var side3 = ui.QueryBounds();
                ClickPhysical((side3.MinX + side3.MaxX) * 0.5f * DpiScale,
                              (side3.MinY + side3.MaxY) * 0.5f * DpiScale);   // 展开
                SettleFrames(400);
                var bar0 = ui.QueryBounds();
                float d3x = (bar0.MinX + bar0.MaxX) * 0.5f * DpiScale;
                float d3y = (bar0.MinY + bar0.MaxY) * 0.5f * DpiScale;
                float d3w = bar0.MaxX - bar0.MinX;                             // 逻辑宽（636）
                float to3x = (float)((20f + d3w * 0.5f) * DpiScale);           // 条左落在离边 20
                SendMouse((int)d3x, (int)d3y, 0);                          SettleFrames(60);
                SendMouse((int)d3x, (int)d3y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
                for (int i = 1; i <= 8; i++)
                {
                    SendMouse((int)(d3x + (to3x - d3x) * i / 8f), (int)(d3y + (toY - d3y) * i / 8f), 0);
                    SettleFrames(20);
                }
                SendMouse((int)to3x, (int)toY, Native.MOUSEEVENTF_LEFTUP);  SettleFrames(200);
                SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.7f), 0);
                SettleFrames(1400);
                var barLeft = ui.QueryBounds();
                Check("展开态停在左边中段：也不藏（只有底边才贴边隐）",
                      (barLeft.MaxX - barLeft.MinX) > 400f && (barLeft.MaxY - barLeft.MinY) > 40f,
                      $"占用 {barLeft.MaxX - barLeft.MinX:F0}×{barLeft.MaxY - barLeft.MinY:F0}，"
                      + $"展开 = {ui.ExpandedForTest}，露头值 peek={ui.PeekForTest:F2}");
            }

            // 收尾：关掉贴边隐藏、重新挂回默认界面（后面几段用例都按"没开贴边隐藏"写）
            SetUiPref("hide", null);
            Doc.Clear();                    // 上面为了验贴边隐藏画了两笔，后面的用例从空画布起
            Doc.ClearHistory();
            SetUiFactory(() => new InkUi.FullUi());
            ui = CurrentUi as InkUi.FullUi;
            SettleFrames(400);
        }

        // 把面板弄到"展开 ＋ 「更多」面板开着"这个已知状态。
        // 每一步都先问**当前**状态再动手——面板可能正收着、可能刚被换成新实例，
        // 硬按上一次算好的坐标去点，点空了都不知道（这一版用例被坑过一次）。
        void NormalizePanel()
        {
            if (!ui.ExpandedForTest)
            {
                var b = ui.QueryBounds();
                ClickPhysical((b.MinX + b.MaxX) * 0.5f * DpiScale,
                              (b.MinY + b.MaxY) * 0.5f * DpiScale);
                SettleFrames(300);
            }
            if (!ui.MoreOpenForTest)
            {
                var m = ui.CellRectForTest(12);
                ClickPhysical((m.MinX + m.MaxX) * 0.5f * DpiScale,
                              (m.MinY + m.MaxY) * 0.5f * DpiScale);
                SettleFrames(250);
            }
            // 面板现在**默认停在启动器主页**；要碰设置行的用例都先切到设置子页
            if (ui.MoreOpenForTest && ui.MorePageForTest != 1)
            {
                var s = ui.HubBottomRectForTest(0);
                ClickPhysical((s.MinX + s.MaxX) * 0.5f * DpiScale,
                              (s.MinY + s.MaxY) * 0.5f * DpiScale);
                SettleFrames(250);
            }
        }

        // ---- ① 启动默认态：**展开 ＋ 贴在工作区底边、水平居中**（用户 2026-09-27 定）----
        //
        // 用户原话："打开后默认居中，高度大概在 Win11 系统任务栏的上面"，随后补了一句
        // "和任务栏不重叠，在它上方一点点"。落成三条判据：
        //   ① 启动就是**展开**的（不是一颗球等人去点）；
        //   ② 主条底边 = **工作区**底边 − 4（工作区 = 屏幕减掉任务栏）；
        //   ③ 水平在**工作区**里居中（不是虚拟桌面——多屏时那样会落在两块屏的缝上）。
        Check("启动即展开（不是一颗球等人去点）",
              ui.ExpandedForTest, $"展开 = {ui.ExpandedForTest}");

        var work = ui.WorkAreaForTest;
        {
            var b0 = ui.BarRectForTest;
            float screenBottom = VirtualScreen.MaxY / DpiScale;
            Check("默认位置：主条底边 = 工作区底边 − 4（紧贴任务栏上方）",
                  MathF.Abs(b0.MaxY - (work.MaxY - InkUi.Tokens.EdgeMargin)) < 1.5f,
                  $"条底 {b0.MaxY:F0}，工作区底 {work.MaxY:F0}，屏幕底 {screenBottom:F0}"
                  + $"（差 {screenBottom - work.MaxY:F0} = 任务栏那一条）");
            Check("默认位置：**不压任务栏**（条底 ≤ 工作区底）",
                  b0.MaxY <= work.MaxY + 0.5f,
                  $"条底 {b0.MaxY:F0} ≤ 工作区底 {work.MaxY:F0}");
            float wantCx = (work.MinX + work.MaxX) * 0.5f;
            Check("默认位置：整条带子在**工作区**里水平居中",
                  MathF.Abs((b0.MinX + b0.MaxX) * 0.5f - wantCx) < 2f,
                  $"带子中心 {(b0.MinX + b0.MaxX) * 0.5f:F0}，工作区中心 {wantCx:F0}，"
                  + $"虚拟桌面中心 {(_virtualX + _virtualW * 0.5f) / DpiScale:F0}"
                  + "（单屏时这两个数相同，多屏才会分开——那正是这条要挡的）");
        }

        // 底下几段验的是**收起态**（球的几何、点球展开），所以这里先手动收起来。
        // 产品里"收起"是点一下主条的结果（那条路在 ⑦ 的收起/展开用例里），
        // 这里用 `SetExpandForTest` 一步到位——它和动画终态是同一个值。
        ui.SetExpandForTest(0f);
        SettleFrames(250);

        // ---- ①b 收起态：球真的在屏幕上，而且是个 48 的方（圆） ----
        var ball = ui.QueryBounds();
        float ballW = ball.MaxX - ball.MinX, ballH = ball.MaxY - ball.MinY;
        Check("收起态是一个 48 的球",
              MathF.Abs(ballW - 48f) < 1f && MathF.Abs(ballH - 48f) < 1f,
              $"占用 {ballW:F0}×{ballH:F0}，位置 ({ball.MinX:F0},{ball.MinY:F0})");

        // ---- 收起球里那两个比例：**笔图标不许碰到色圈** ----
        //
        // 用户 2026-09-18 报过一次"笔碰到色圈"：产品把假面板的 0.70R 抄成了 1.40R，
        // 半边长 16.8 正好顶到色圈内沿 16.99。这条不靠眼睛——**碰没碰到是算得出来的**：
        //   色圈内沿 = BallRing × R − 线宽/2；  图标半边长 = BallIcon × R ÷ 2
        // 要求留 ≥ 1 像素的空（0.19 那种"看着压上去了"的余量不算）。
        {
            float R = InkUi.Tokens.Ball * 0.5f;
            float ringInner = InkUi.Tokens.BallRing * R - 1.25f;      // 线宽 2.5 的一半
            float iconHalf = InkUi.Tokens.BallIcon * R * 0.5f;
            Check("收起球：笔图标不碰色圈（半边长 < 色圈内沿 − 1）",
                  iconHalf < ringInner - 1f,
                  $"图标半边长 {iconHalf:F2} vs 色圈内沿 {ringInner:F2}"
                  + $"（BallIcon = {InkUi.Tokens.BallIcon:F2}R，留 {ringInner - iconHalf:F2} 的空）");
        }

        // 球也贴**工作区**底边（和展开态同一个锚点：锚的是"展开后带子的左端"，
        // 所以收起/展开之间球一动不动，Y 自然也一样）。
        // ⚠ 这里 2026-09-27 之前是按**屏幕**底边 + 离边 12 写的，还专门注了
        //   "允许盖住任务栏"——用户改主意了：不盖、也不重叠，紧贴任务栏上方。
        Check("球贴在**工作区**底边、离边 4（同样不压任务栏）",
              MathF.Abs(ball.MaxY - (work.MaxY - InkUi.Tokens.EdgeMargin)) < 1.5f,
              $"球底 {ball.MaxY:F0}，工作区底 {work.MaxY:F0}，屏幕底 {VirtualScreen.MaxY / DpiScale:F0}");

        // 没拖过时的横向位置：锚点是**展开后那条带子的左端**，而带子在**工作区**里居中，
        // 所以球停在"工作区中心 − 带子宽/2"（偏左半个带子）。
        // 用户 2026-09-17 要的就是这个：**点一下往右展开、展开完全局居中**。
        {
            float wantBallLeft = work.MinX + ((work.MaxX - work.MinX) - ui.ExpandedWidthForTest) * 0.5f;
            Check("没拖过：球停在「展开后带子居中」的左端",
                  MathF.Abs(ball.MinX - wantBallLeft) < 1.5f,
                  $"球左 {ball.MinX:F0}（应为 {wantBallLeft:F0} = 工作区中心 − 带子宽/2），"
                  + $"带子宽 {ui.ExpandedWidthForTest:F0}");
        }

        // ---- ② 点球展开：动画期间必须连续出帧 ----
        int strokes0 = Doc.Strokes.Count;
        float ballCx = (ball.MinX + ball.MaxX) * 0.5f * DpiScale;
        float ballCy = (ball.MinY + ball.MaxY) * 0.5f * DpiScale;
        SendMouse((int)ballCx, (int)ballCy, 0);                            SettleFrames(70);
        SendMouse((int)ballCx, (int)ballCy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(50);
        SendMouse((int)ballCx, (int)ballCy, Native.MOUSEEVENTF_LEFTUP);

        // 松手就开始数帧——**别先 settle**：动画只有 200 毫秒，
        // 先 settle 一遍等于等它跑完再来数"动画期间的帧"，那永远数不出东西。
        int frames = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 260)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; frames++; }
            else Thread.Sleep(1);
        }
        SettleFrames(120);

        Check("点球能展开", ui.ExpandedForTest, $"展开状态 = {ui.ExpandedForTest}");
        Check("展开动画拿到了连续帧", frames >= 5, $"260 毫秒里出了 {frames} 帧");
        Check("点球不落墨", Doc.Strokes.Count == strokes0, $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ③ 展开后的尺寸：算出来的宽，不是一个拍脑袋的数 ----
        var bar = ui.QueryBounds();
        float barW = bar.MaxX - bar.MinX, barH = bar.MaxY - bar.MinY;
        var barOnly = ui.BarRectForTest;
        // 总高看这一刻上带是色线还是设置条（58 / 86），所以这里只卡"主条 48、宽 > 600、
        // 总高在这两档之间"；两条精确的高度由上面那两条"色线/设置条"专测。
        Check("展开成一条带子（主条高 48、宽 > 600）",
              MathF.Abs((barOnly.MaxY - barOnly.MinY) - 48f) < 1f
              && barH >= 52f && barH <= 88f      // 色线时 54、设置条时 82
              && barW > 600f,
              $"占用 {barW:F0}×{barH:F0}（主条高 {barOnly.MaxY - barOnly.MinY:F0}）");

        // 展开之后**整条带子在工作区里居中**（没拖过时）。球停在带子左端，从头到尾没动过。
        {
            float workCx = (work.MinX + work.MaxX) * 0.5f;
            Check("展开后整条带子在**工作区**里居中",
                  MathF.Abs((barOnly.MinX + barOnly.MaxX) * 0.5f - workCx) < 2f,
                  $"带子中心 {(barOnly.MinX + barOnly.MaxX) * 0.5f:F0}，工作区中心 {workCx:F0}"
                  + $"（带子 {barOnly.MinX:F0}..{barOnly.MaxX:F0}）");
        }

        // 带子长在**上面**（贴底时朝屏幕中心）：主条位置不许动——
        // 你刚点的那个按钮要是往上跳 38 像素，下一次点它就得重新瞄（费茨定律）。
        Check("展开时主条不动（只有带子长出来）",
              MathF.Abs(barOnly.MinY - ball.MinY) < 1.5f
              && MathF.Abs(barOnly.MaxY - ball.MaxY) < 1.5f,
              $"收起时 y {ball.MinY:F0}..{ball.MaxY:F0}，展开后主条 y {barOnly.MinY:F0}..{barOnly.MaxY:F0}");

        // ---- ④ 点"笔"那一格：引擎状态真的变了（走的是命令通道）----
        Tool = Tool.Eraser;
        var penCell = ui.CellRectForTest(3);
        ClickPhysical((penCell.MinX + penCell.MaxX) * 0.5f * DpiScale,
                      (penCell.MinY + penCell.MaxY) * 0.5f * DpiScale);
        Check("点「笔」切到笔", Tool == Tool.Pen, $"工具 = {Tool}");

        // ---- ⑤ 输入拦截：面板内不落墨，面板外照常落墨 ----
        strokes0 = Doc.Strokes.Count;
        var boardCell = ui.CellRectForTest(2);
        ClickPhysical((boardCell.MinX + boardCell.MaxX) * 0.5f * DpiScale,
                      (boardCell.MinY + boardCell.MaxY) * 0.5f * DpiScale);
        Check("点「白板」不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}，白板 = {BoardOn}");
        if (BoardOn) { Host.Commands.SetBoard(false); SettleFrames(120); }

        strokes0 = Doc.Strokes.Count;
        Tool = Tool.Pen;
        ClickPhysical(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.45f);
        Check("面板外照常落墨", Doc.Strokes.Count > strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑥ 上带：色片 / 滑条 / 分段（都走命令通道）----
        //
        // **张不张开只看焦点在不在面板上**（2026-09-17 用户定的新语义）：
        //   指针落在面板（主条 ∪ 设置条）上 → 240ms 后张开（2026-10-09 沉稳档，原 120）；
        //   离开 → 800ms 后收成一条 10 像素色线（原 450 / 6 像素）。
        // 旧的那套"点一次钉住、再点同一个工具收起"已经删掉了——两套规则会打架：
        // 指针还停在面板上，收下去会立刻又张开。
        var penCellAgain = ui.CellRectForTest(3);
        ClickPhysical((penCellAgain.MinX + penCellAgain.MaxX) * 0.5f * DpiScale,
                      (penCellAgain.MinY + penCellAgain.MaxY) * 0.5f * DpiScale);
        SettleFrames(560);                              // 240ms 张开延迟 + 230ms 动画，留足
        Check("焦点在面板上：设置条张开",
              ui.RailHoverForTest && ui.RailOpenForTest
              && MathF.Abs(ui.BandHeightForTest - InkUi.Tokens.BandHeight) < 1.5f,
              $"焦点在面板 = {ui.RailHoverForTest}，张开 = {ui.RailOpenForTest}，高 {ui.BandHeightForTest:F0}");

        // 指针移到画布上（离开面板）→ 收成一条色线
        SendMouse((int)(_virtualX + _virtualW * 0.6f), (int)(_virtualY + _virtualH * 0.3f), 0);
        SettleFrames(1200);                             // 800ms 慢隐 + 230ms 动画，留足
        Check("指针离开面板：收成一条色线",
              !ui.RailOpenForTest
              && MathF.Abs(ui.BandHeightForTest - InkUi.Tokens.BandLine) < 1.5f,
              $"张开 = {ui.RailOpenForTest}，高 {ui.BandHeightForTest:F0}（色线应为 {InkUi.Tokens.BandLine:F0}）");

        // 再把指针挪回**主条**（不是色带本身）→ 不用点，它自己就该张开。
        // 这一条就是用户说的"焦点在悬浮框的时候就展开"。
        var barHover = ui.BarRectForTest;
        int hoverPx = (int)((barHover.MinX + barHover.MaxX) * 0.5f * DpiScale);
        int hoverPy = (int)((barHover.MinY + barHover.MaxY) * 0.5f * DpiScale);
        // 合成鼠标**偶尔会丢一次移动**（自检里见过：同一份代码，一次跑红一次跑绿）——
        // 每次把坐标挪 1 像素再发，保证真的产生一次 WM_MOUSEMOVE，最多给三次机会。
        for (int attempt = 0; attempt < 3 && !ui.RailOpenForTest; attempt++)
        {
            SendMouse(hoverPx, hoverPy - attempt, 0);
            SettleFrames(560);
        }
        var hb = ui.QueryBounds();
        Console.WriteLine($"    [诊断] 指针物理 ({hoverPx},{hoverPy})，主条 {barHover.MinX:F0}..{barHover.MaxX:F0}"
                        + $" × {barHover.MinY:F0}..{barHover.MaxY:F0}，面板 {hb.MinX:F0}..{hb.MaxX:F0}"
                        + $" × {hb.MinY:F0}..{hb.MaxY:F0}，railHover={ui.RailHoverForTest}");
        Check("指针回到面板（主条）上就自己张开",
              ui.RailOpenForTest && ui.RailHoverForTest,
              $"张开 = {ui.RailOpenForTest}，高 {ui.BandHeightForTest:F0}");

        var bandRect = ui.BandRectForTest;
        Check("展开后有上带", bandRect.MaxY - bandRect.MinY > 20f,
              $"上带 {bandRect.MaxX - bandRect.MinX:F0}×{bandRect.MaxY - bandRect.MinY:F0}");

        Check("上带是笔的设置条（色片行）", ui.RailOpenForTest,
              $"张开 = {ui.RailOpenForTest}");

        // 挑一个**不是默认色**的色片（默认笔色就是红，用红当期望值会假通过——
        // 这一条自检第一版就是这么假通过的，被"换色成功"蒙了一次）。
        // 期望值跟着 `Tokens.Palette` 走、这里不写死颜色名：2026-09-27 换过色表
        //（第 7 个从"绿"变成了"紫"），当时这条自检**没红**——因为两边都读同一个下标。
        var swatch = ui.SwatchRectForTest(6);                 // 第 7 个色片
        float swx = (swatch.MinX + swatch.MaxX) * 0.5f * DpiScale;
        float swy = (swatch.MinY + swatch.MaxY) * 0.5f * DpiScale;
        ClickPhysical(swx, swy);
        var wantColor = InkUi.Tokens.Palette[6].Color;
        var gotColor = Host.State.PaletteBase;
        var defaultColor = InkPalette.PenDefault;
        bool isDefault = MathF.Abs(gotColor.R - defaultColor.R) < 0.02f
                      && MathF.Abs(gotColor.G - defaultColor.G) < 0.02f
                      && MathF.Abs(gotColor.B - defaultColor.B) < 0.02f;
        Check("点色片换笔色",
              MathF.Abs(wantColor.R - gotColor.R) < 0.02f
              && MathF.Abs(wantColor.G - gotColor.G) < 0.02f
              && MathF.Abs(wantColor.B - gotColor.B) < 0.02f
              && !isDefault,
              $"期望 ({wantColor.R:F2},{wantColor.G:F2},{wantColor.B:F2})，实际 ({gotColor.R:F2},{gotColor.G:F2},{gotColor.B:F2})");

        // ---- ⑥.1 色带条上的**虚实线切换**（用户 2026-09-19 第 2 件）----
        //
        // 位置是用户点的："加在'调按钮大小'和'颜色带'中间"。所以这里除了"点得到、
        // 三档轮回"，还断言**它真的夹在色片和滑条之间**（两边都不压着）——
        // 位置是需求的一部分，光验"有个按钮"验不出来。
        {
            Check("笔的设置条上有虚实线那一格", ui.DashToggleVisibleForTest, "");
            var dashR = ui.DashToggleRectForTest;
            var lastSw = ui.SwatchRectForTest(ui.SwatchCountForTest - 1);
            var sliderR = ui.SliderRectForTest;
            Check("虚实线那一格夹在色片和粗细滑条中间（两边都不压着）",
                  dashR.MinX >= lastSw.MaxX - 0.5f && dashR.MaxX <= sliderR.MinX + 0.5f,
                  $"色片到 {lastSw.MaxX:F0}，虚实线 {dashR.MinX:F0}..{dashR.MaxX:F0}，滑条从 {sliderR.MinX:F0}");

            // 加了这一格之后色片会变窄——"挤到看不清"等于这个入口没有（和极简档 4 色那条同一个判据）。
            var swFirst = ui.SwatchRectForTest(0);
            Check("完整档：加了这一格之后色片仍然够宽（≥ 30 逻辑像素）",
                  swFirst.MaxX - swFirst.MinX >= 30f, $"色片宽 {swFirst.MaxX - swFirst.MinX:F0}");

            float dx = (dashR.MinX + dashR.MaxX) * 0.5f * DpiScale;
            float dy = (dashR.MinY + dashR.MaxY) * 0.5f * DpiScale;
            var dash0 = Host.State.Dash;
            ClickPhysical(dx, dy); SettleFrames(120);
            var dash1 = Host.State.Dash;
            ClickPhysical(dx, dy); SettleFrames(120);
            var dash2 = Host.State.Dash;
            ClickPhysical(dx, dy); SettleFrames(150);
            Check("点虚实线那一格：三档轮回（实线 → 虚线 → 点线 → 实线）",
                  dash0 == StrokeDash.Solid && dash1 == StrokeDash.Dashed
                  && dash2 == StrokeDash.Dotted && Host.State.Dash == StrokeDash.Solid,
                  $"{dash0} → {dash1} → {dash2} → {Host.State.Dash}");
            Check("换挡只动线型，不许顺手换工具",
                  Tool == Tool.Pen, $"工具 = {Tool}");
        }

        float widthBefore = Host.State.Width;
        var slider = ui.SliderRectForTest;
        float sliderY = (slider.MinY + slider.MaxY) * 0.5f * DpiScale;
        SendMouse((int)(slider.MinX * DpiScale), (int)sliderY, 0);                          SettleFrames(60);
        SendMouse((int)(slider.MinX * DpiScale), (int)sliderY, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
        for (int i = 1; i <= 6; i++)
        {
            SendMouse((int)((slider.MinX + (slider.MaxX - slider.MinX) * i / 6f) * DpiScale),
                      (int)sliderY, 0);
            SettleFrames(25);
        }
        SendMouse((int)(slider.MaxX * DpiScale), (int)sliderY, Native.MOUSEEVENTF_LEFTUP);  SettleFrames(150);
        Check("拖滑条改粗细", Host.State.Width > widthBefore + 5f,
              $"粗细 {widthBefore:F1} → {Host.State.Width:F1}");

        // 档位点（2026-10-09 用户："点击到档位、滑动连续"）：
        // ① 界面那份档位表和引擎那张表逐项对得上（两边各留一份的老规矩，靠自检卡）
        // ② 点中「4」那一档的小点 → 宽度精确 = 4（不是"差不多"）
        {
            var gradesUi = ui.WidthGradesForTest(Tool.Pen);
            bool sameGrades = gradesUi.Length == WidthPresets.Length;
            for (int gi = 0; gi < gradesUi.Length && sameGrades; gi++)
                sameGrades = MathF.Abs(gradesUi[gi] - WidthPresets[gi]) < 0.01f;
            Check("界面档位表 == 引擎档位表（笔）", sameGrades,
                  $"界面 {string.Join("/", gradesUi)} vs 引擎 {string.Join("/", WidthPresets)}");

            float pipX4 = ui.WidthGradeXForTest(4f);
            SendMouse((int)(pipX4 * DpiScale), (int)sliderY, 0);                          SettleFrames(60);
            SendMouse((int)(pipX4 * DpiScale), (int)sliderY, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            SendMouse((int)(pipX4 * DpiScale), (int)sliderY, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(120);
            Check("点滑条上的档位点：精确落到那一档（4 逻辑像素）",
                  MathF.Abs(Host.State.Width - 4f) < 0.01f, $"粗细 = {Host.State.Width:F2}");
        }

        // ---- ⑥.2 滑条**按工具路由**：橡皮终于能调大小了 ----
        //
        // 这一条是被用户点出来的："橡皮擦现在没有调节大小功能"。
        // 真相是界面上**有**滑条（`BandHasSlider` 一直包含橡皮那一格），
        // 但引擎的 `SetWidthFromUi` 只分了"荧光笔 / 激光 / 其它"三支，
        // 两种橡皮全落进"其它"——拖橡皮的滑条，**改的是笔宽**，橡皮一点没动。
        // 所以这三条要一起看：橡皮变了 **且** 笔宽没变（不然还会退回旧 bug）。
        {
            Host.Commands.SetTool(Tool.Eraser);
            SettleFrames(200);
            float penW0 = PenWidthLogical;
            // 先把指针放回面板（不然设置条是收着的，滑条不在）
            var barE = ui.BarRectForTest;
            SendMouse((int)((barE.MinX + barE.MaxX) * 0.5f * DpiScale),
                      (int)((barE.MinY + barE.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(560);
            var slE = ui.SliderRectForTest;
            float slEy = (slE.MinY + slE.MaxY) * 0.5f * DpiScale;

            var (emin, emax) = (8f, 48f);              // 界面那边 WidthRange(Tool.Eraser)
            var (trackL, trackR) = ui.SliderTrackRangeForTest;
            SendMouse((int)(trackL * DpiScale), (int)slEy, 0);                          SettleFrames(60);
            SendMouse((int)(trackL * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
            SendMouse((int)(trackR * DpiScale), (int)slEy, 0);                          SettleFrames(120);
            SendMouse((int)(trackR * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(150);
            Check("橡皮的滑条拖到最右＝最大落点",
                  MathF.Abs(EraserRadiusLogical - emax) < 1.5f,
                  $"橡皮半径 {EraserRadiusLogical:F1}（应到 {emax}）");
            Check("拖橡皮的滑条**不动笔宽**",
                  MathF.Abs(PenWidthLogical - penW0) < 0.01f,
                  $"笔宽 {penW0:F2} → {PenWidthLogical:F2}（旧 bug 就是这里被改掉的）");

            var (trackL2, trackR2) = ui.SliderTrackRangeForTest;
            SendMouse((int)(trackL2 * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(60);
            SendMouse((int)(trackL2 * DpiScale), (int)slEy, 0);                          SettleFrames(120);
            SendMouse((int)(trackL2 * DpiScale), (int)slEy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(150);
            Check("橡皮的滑条拖到最左＝最小落点",
                  MathF.Abs(EraserRadiusLogical - emin) < 1.5f,
                  $"橡皮半径 {EraserRadiusLogical:F1}（应到 {emin}）");

            // **真实大小预览**（用户 2026-09-17："那个点和实际大小是不是应该一样大，
            // 但是太大了装不下，我又不希望改动界面"）。
            // 验的是"预览整个落在 QueryBounds() 里"——引擎按那份矩形裁剪界面，
            // 只要不包含它，画出去的部分就会被裁掉（这才是"预览看不见"的真因）。
            // 拖完滑条指针还停在滑条上 → 预览应该在。
            var pv = ui.SizePreviewRectForTest;
            var qb = ui.QueryBounds();
            Check("粗细预览画在面板外、且算进可见范围（不会被裁掉）",
                  pv.MaxY - pv.MinY > InkUi.Tokens.BandHeight
                  && pv.MinX >= qb.MinX - 0.5f && pv.MaxX <= qb.MaxX + 0.5f
                  && pv.MinY >= qb.MinY - 0.5f && pv.MaxY <= qb.MaxY + 0.5f,
                  $"预览 {pv.MaxX - pv.MinX:F0}×{pv.MaxY - pv.MinY:F0}"
                  + $"（{pv.MinX:F0}..{pv.MaxX:F0} × {pv.MinY:F0}..{pv.MaxY:F0}），"
                  + $"可见范围 {qb.MaxX - qb.MinX:F0}×{qb.MaxY - qb.MinY:F0}");

            // 面积橡皮同理，而且它的范围比笔宽大得多（30～160）
            Host.Commands.SetTool(Tool.PixelEraser);
            SettleFrames(200);
            float penW1 = PenWidthLogical;
            var slP = ui.SliderRectForTest;
            float slPy = (slP.MinY + slP.MaxY) * 0.5f * DpiScale;
            var (pl, pr) = ui.SliderTrackRangeForTest;
            SendMouse((int)(pr * DpiScale), (int)slPy, 0);                          SettleFrames(60);
            SendMouse((int)(pr * DpiScale), (int)slPy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
            SendMouse((int)(pr * DpiScale), (int)slPy, 0);                          SettleFrames(120);
            SendMouse((int)(pr * DpiScale), (int)slPy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(150);
            Check("面积橡皮的滑条能放到 160（比笔宽的上限 40 大）",
                  MathF.Abs(PixelEraserWidthLogical - 160f) < 2f,
                  $"面积橡皮宽 {PixelEraserWidthLogical:F1}（应到 160）");
            Check("拖面积橡皮的滑条**不动笔宽**",
                  MathF.Abs(PenWidthLogical - penW1) < 0.01f,
                  $"笔宽 {penW1:F2} → {PenWidthLogical:F2}");
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(200);
        }

        // ---- ⑥.3 穿透与工具**互斥** ----
        {
            Host.Commands.SetPassThrough(true);
            SettleFrames(250);
            Check("穿透开着时，工具格一律不高亮（免得`穿透＋笔`同时亮）",
                  ui.CellActiveForTest(1) && !ui.CellActiveForTest(3) && !ui.CellActiveForTest(4)
                  && !ui.CellActiveForTest(6) && !ui.CellActiveForTest(9),
                  $"鼠标 = {ui.CellActiveForTest(1)}，笔 = {ui.CellActiveForTest(3)}，工具 = {Tool}");

            var penCellPt = ui.CellRectForTest(3);
            ClickPhysical((penCellPt.MinX + penCellPt.MaxX) * 0.5f * DpiScale,
                          (penCellPt.MinY + penCellPt.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点工具格＝顺手关掉穿透（不用先去点鼠标格）",
                  !Host.State.PassThrough && Tool == Tool.Pen,
                  $"穿透 = {Host.State.PassThrough}，工具 = {Tool}");
            Check("关掉穿透之后工具格亮回来", ui.CellActiveForTest(3),
                  $"笔格高亮 = {ui.CellActiveForTest(3)}");
        }

        // ---- ⑥.3c 穿透：色带只收成那条线，不许张开（2026-10-02 用户口径）----
        //
        // 用户原话："点击穿透以后，色带是横起来的……不是说我点了个穿透，色带就完全没有了。
        // 我说的色带消失，就是把它折叠起来，而不是像其他一样，点过来以后还是展开的。"
        // 所以规则是：进穿透**只把设置条收回那条 6 像素色线**（面板高度不变——贴边隐藏
        // 露出来的还是它，不会难看），而且穿透期间不许再张开（没有设置可放）。
        // 以前那个毛病照旧要防：`_bandCell` 还停在上一个工具那格，穿透开着、指针在面板上时
        // 旧设置条照常张开、色片还能点。
        // 这里钉五件事：① 点格子进穿透 → 收成线；② 指针在面板上也不许张开；
        // ③ 退出穿透 → 还能重新张开（功能没被收坏）；④ 穿透里点工具格照样能出来；
        // ⑤ 全局开关（Ctrl+Alt+Shift+T 同一条路）进穿透同样只收成线。
        {
            // 起手：笔、色带在笔格并张开（走真实路径：点笔格后指针留在面板上）
            Host.Commands.SetTool(Tool.Pen);
            Host.Commands.SetPassThrough(false);
            SettleFrames(200);
            var penCellP = ui.CellRectForTest(3);
            ClickPhysical((penCellP.MinX + penCellP.MaxX) * 0.5f * DpiScale,
                          (penCellP.MinY + penCellP.MaxY) * 0.5f * DpiScale);
            SettleFrames(560);
            Check("（准备）色带在笔格且已经张开",
                  ui.BandCellForTest == 3 && ui.RailValueForTest > 0.99f,
                  $"格 {ui.BandCellForTest}，张开度 {ui.RailValueForTest:F2}");

            // 点穿透格 → 穿透开、设置条收回那条 10 像素色线（不是消失）
            var mouseCellP = ui.CellRectForTest(1);
            ClickPhysical((mouseCellP.MinX + mouseCellP.MaxX) * 0.5f * DpiScale,
                          (mouseCellP.MinY + mouseCellP.MaxY) * 0.5f * DpiScale);
            SettleFrames(600);
            var lineRect = ui.BandRectForTest;
            Check("点穿透格：色带只收成那条 10 像素线（不是消失）",
                  Host.State.PassThrough && ui.RailValueForTest < 0.01f
                  && !lineRect.IsEmpty && lineRect.MaxY - lineRect.MinY is >= 5f and <= 12f,
                  $"穿透 = {Host.State.PassThrough}，张开度 {ui.RailValueForTest:F2}，"
                  + $"带高 {(lineRect.IsEmpty ? 0f : lineRect.MaxY - lineRect.MinY):F1}");

            // ② 指针就停在面板上（刚点的穿透格）：旧的悬停意图不许把设置条重新弹开
            ui.OpenRailForTest();                 // 强行模拟"指针碰到带子区"
            SettleFrames(300);
            Check("穿透中：指针在面板上也不张开旧设置条",
                  ui.RailValueForTest < 0.01f
                  && (ui.BandRectForTest.MaxY - ui.BandRectForTest.MinY) <= 12f,
                  $"张开度 {ui.RailValueForTest:F2}");

            // ③ 退出穿透（走命令，和全局 Ctrl+Alt+Shift+T 同一条路）→ 悬停还能重新张开
            Host.Commands.SetPassThrough(false);
            SettleFrames(200);
            ui.OpenRailForTest();
            SettleFrames(300);
            Check("退出穿透：色带可以重新张开（功能没被收坏）",
                  !Host.State.PassThrough && ui.RailValueForTest > 0.99f,
                  $"张开度 {ui.RailValueForTest:F2}");

            // ④ 穿透里点笔格：照样顺手关穿透（没键盘的教室靠它）＋ 带子回到笔格，
            //    而且**这一次不许顺手换色**（2026-10-05 用户报的 bug：穿透中点笔格直接切了颜色）。
            var colorBeforeP4 = Host.State.PaletteBase;
            ClickPhysical((mouseCellP.MinX + mouseCellP.MaxX) * 0.5f * DpiScale,
                          (mouseCellP.MinY + mouseCellP.MaxY) * 0.5f * DpiScale);
            SettleFrames(400);
            var penCellP2 = ui.CellRectForTest(3);
            ClickPhysical((penCellP2.MinX + penCellP2.MaxX) * 0.5f * DpiScale,
                          (penCellP2.MinY + penCellP2.MaxY) * 0.5f * DpiScale);
            SettleFrames(500);
            bool sameColorP4 = MathF.Abs(Host.State.PaletteBase.R - colorBeforeP4.R) < 0.02f
                            && MathF.Abs(Host.State.PaletteBase.G - colorBeforeP4.G) < 0.02f
                            && MathF.Abs(Host.State.PaletteBase.B - colorBeforeP4.B) < 0.02f;
            Check("穿透里点笔格：关穿透、上带回到笔格、**颜色不动**",
                  !Host.State.PassThrough && Tool == Tool.Pen && ui.BandCellForTest == 3 && sameColorP4,
                  $"穿透 = {Host.State.PassThrough}，工具 = {Tool}，色带格 = {ui.BandCellForTest}，颜色不动 = {sameColorP4}");

            // ⑤ 全局开关那条路（不经面板）：同样只收成线
            Host.Commands.SetPassThrough(true);
            SettleFrames(500);
            Check("全局开关进穿透：同样只收成那条线（不是只有点格子才收）",
                  Host.State.PassThrough && ui.RailValueForTest < 0.01f,
                  $"张开度 {ui.RailValueForTest:F2}");
            Host.Commands.SetPassThrough(false);
            SettleFrames(300);
        }

        // ---- ⑥.3d 写字期间：色带**冻结**（2026-10-09 用户："写到附近该保持不动"）----
        // 旧行为：落笔后引擎不再转发悬停（OnPointerMove 的 !_drawing 闸），railHover 定格在
        // 落笔前的值——若色带已张开、而落笔点不在面板上（hover=false），450ms 后**写到
        // 一半就开始收**；反之攒着的张开意图也会在整笔期间被每帧执行。现在：整笔不动。
        {
            // 先把色带张开（指针停到主条上，等新的 240ms 延迟 + 230ms 动画全部走完）
            var barF = ui.BarRectForTest;
            int fx = (int)((barF.MinX + barF.MaxX) * 0.5f * DpiScale);
            int fy = (int)((barF.MinY + barF.MaxY) * 0.5f * DpiScale);
            SendMouse(fx, fy, 0);
            SettleFrames(560);
            bool openBefore = ui.RailOpenForTest;

            // 落笔写一条（按住不松）、指针留在画布上，边写边小幅挪动（防"停顿变图形"）
            int cxF = (int)(_virtualX + _virtualW * 0.45f);
            int cyF = (int)(_virtualY + _virtualH * 0.5f);
            SendMouse(cxF, cyF, 0);
            SendMouse(cxF, cyF, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(60);
            for (int i = 1; i <= 6; i++) { SendMouse(cxF + i * 40, cyF, 0); SettleFrames(30); }
            for (int k = 0; k < 18; k++) { SendMouse(cxF + 240 + (k % 2), cyF, 0); SettleFrames(50); }
            Check("写字期间：色带保持不动（写到一半不许自己收）",
                  openBefore && ui.RailOpenForTest,
                  $"落笔前张开 = {openBefore}，写中张开 = {ui.RailOpenForTest}");

            SendMouse(cxF + 240, cyF, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(150);
            Host.Commands.Undo();                      // 这一笔撤掉，别影响后面的笔画计数
            SettleFrames(150);

            // 指针挪开：按新的"慢隐"（800ms 延迟 + 230ms 动画）慢慢收
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.62f), 0);
            SettleFrames(1300);
            Check("写完挪开：色带慢隐（不再一点就收）", !ui.RailOpenForTest, $"张开 = {ui.RailOpenForTest}");
        }

        // ---- ⑥.3e 硬规则（2026-10-09 二轮）：收起时必须是色带；抬手后要"离开一次"才许变卡片 ----
        // 用户二轮反馈："色带收起来以后，在附近写字，偶尔还是会退化成卡片"。
        // 根因：落笔那一瞬间它可能正张到一半——旧的"冻结"只是不再改目标，**进行中的张开
        // 动画会自己走完**。现在：落笔时没收干净的一律拉回色线；整笔冻结；抬手后进"余温"
        // （600ms 内不许开始张开），且必须先让指针离开判定区一次、再停回来才重开。
        {
            // 先收干净（挪到画布中下 + 等慢隐走完）
            int cxE = (int)(_virtualX + _virtualW * 0.45f);
            int cyE = (int)(_virtualY + _virtualH * 0.45f);
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.68f), 0);
            SettleFrames(1300);
            bool closedE = ui.RailValueForTest < 0.02f;

            var barE = ui.BarRectForTest;
            int gx = (int)((barE.MinX + barE.MaxX) * 0.5f * DpiScale);
            int gy = (int)((barE.MinY + barE.MaxY) * 0.5f * DpiScale);

            // 悬停 300ms：它已经"开始张、还没张完"（240ms 延迟 + 230ms 动画的半路）
            SendMouse(gx, gy, 0);
            SettleFrames(300);

            // 移到画布落笔写——半开状态必须被**收回色线**，整笔保持色线
            SendMouse(cxE, cyE, 0);
            SendMouse(cxE, cyE, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(60);
            SendMouse(cxE + 120, cyE, 0);
            SettleFrames(500);
            Check("落笔时没收干净的色带：整笔被收回色线（半路也不许变卡片）",
                  closedE && ui.RailValueForTest < 0.05f,
                  $"写前收干净 = {closedE}，写中张开度 = {ui.RailValueForTest:F2}");

            // 按住挪到面板上、就在那里抬手——"写完笔还停在色带附近"
            SendMouse(gx, gy, 0);
            SettleFrames(120);
            SendMouse(gx, gy, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(120);
            // 抬手后在原地轻动一下（让悬停值是"新鲜"的）：仍不许弹
            SendMouse(gx - 1, gy, 0);
            SettleFrames(1100);
            Check("抬手后笔还停在面板上：仍不弹（要等'离开一次'）",
                  ui.RailValueForTest < 0.05f, $"张开度 = {ui.RailValueForTest:F2}");
            Host.Commands.Undo();                      // 这一笔撤掉，别影响后面的笔画计数
            SettleFrames(150);

            // 离开一次 → 再停回来：这次按正常节奏张开
            SendMouse(cxE, cyE, 0);
            SettleFrames(150);
            SendMouse(gx, gy, 0);
            SettleFrames(900);
            Check("离开一次再停回来：色带恢复张开", ui.RailValueForTest > 0.99f,
                  $"张开度 = {ui.RailValueForTest:F2}");

            // 收尾：挪开 + 等它慢隐收干净（给下一段一个清爽的状态）
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.68f), 0);
            SettleFrames(1300);
        }

        // ---- ⑥.3b 主条那几格"选中显示选中什么"（2026-09-26）----
        //
        // 用户那天说的那条逻辑：**没选中画一个固定的，选中之后就画"手里到底是什么"**。
        // 图形那一格早就这样了（见 ShapeBandTest 的 D 段），这一轮补了三处：
        //   · 白板那一格：板开着 → 图标就是**这块板的颜色**（白 / 绿 / 黑）；
        //   · 选择那一格：切到套索 → 画套索（不再是那个通用的选择框）；
        //   · 激光笔那一格：未选中/选中各一张（上游那个专名）。
        // 断言问的是 `CellIconForTest`——**和绘制同一条判据**（这正是这条自检要盯的：
        // 自检说换了、屏幕上没换，等于白测）。
        {
            Host.Commands.SetPassThrough(false);
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(200);

            // 穿透那一格（2026-09-26 用户："把'穿透'的图标换成鼠标，在'更多'里面也要改"）：
            // 两态都得是**鼠标设备**（MDI 那张：常态描边、选中实心）。原来那个是 Fluent `Cursor`
            // ＝ 一根箭头指针，和"选择"工具那根容易混。
            // ⚠ "更多里面也要改"这一半**不用另测**：面板里的钉住宫格和主条走的是同一个
            // `DrawCellIcon` / `CellIconName`（见那两处的说明），所以这一条断言同时管着两边——
            // 面板要是哪天又自己写一份图标名单，这条就会在出图验收时露馅（面板那张图见 --panelshow --more）。
            Check("穿透那一格：没选中 → 指针箭头（Tabler 描边版）",
                  ui.CellIconForTest(1) == "cursorArrow",
                  $"画的是 {ui.CellIconForTest(1)}（期望 cursorArrow）");
            Host.Commands.SetPassThrough(true);
            SettleFrames(150);
            Check("穿透那一格：穿透开着 → 同一支箭头的实心版",
                  ui.CellIconForTest(1) == "cursorArrowFilled",
                  $"画的是 {ui.CellIconForTest(1)}（期望 cursorArrowFilled）");
            Host.Commands.SetPassThrough(false);
            Host.Commands.SetBoard(true);
            SettleFrames(150);

            foreach (var (idx, want) in new[] { (0, "白板"), (1, "绿板"), (2, "黑板") })
            {
                Host.Commands.SetBoardColor(InkPalette.BoardPresets[idx].Color);
                SettleFrames(120);
                Check($"白板那一格：板色换成「{want}」→ 图标就是这块板",
                      ui.CellIconForTest(2) == "board:" + want,
                      $"画的是 {ui.CellIconForTest(2)}（期望 board:{want}）");
            }
            Host.Commands.SetBoard(false);
            SettleFrames(120);
            Check("白板那一格：板关掉 → 回到固定的那张（不填颜色）",
                  ui.CellIconForTest(2) == "board",
                  $"画的是 {ui.CellIconForTest(2)}（期望 board）");
            Host.Commands.SetBoardColor(InkPalette.BoardPresets[0].Color);
            Host.Commands.SetBoard(true);
            SettleFrames(120);

            // 橡皮那一格（2026-09-26 用户挑的图标）：未选中＝**纯橡皮**（Radix 那张，
            // 15 网格，路径里一根横线都没有——用户："就像一个纯粹的橡皮擦，不要带那种横线"）；
            // 选中＝跟着当前是哪种橡皮换
            //（整笔擦＝Fluent 原版带横线、面积擦＝Eraser Medium 那个大圈）。
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
            Check("橡皮那一格：没选中 → Radix 纯橡皮（无横线）",
                  ui.CellIconForTest(6) == "eraserPure",
                  $"画的是 {ui.CellIconForTest(6)}（期望 eraserPure）");
            Host.Commands.SetTool(Tool.Eraser);
            SettleFrames(150);
            Check("橡皮那一格：选中·整笔擦 → Fluent 那张（橡皮＋底下那道线）",
                  ui.CellIconForTest(6) == "eraser",
                  $"画的是 {ui.CellIconForTest(6)}（期望 eraser）");
            Host.Commands.SetTool(Tool.PixelEraser);
            SettleFrames(150);
            Check("橡皮那一格：切到面积擦 → Eraser Medium（橡皮＋大圈）",
                  ui.CellIconForTest(6) == "eraserMedium",
                  $"画的是 {ui.CellIconForTest(6)}（期望 eraserMedium）");

            // 选择那一格：未选中＝Lucide 虚线框＋指针；选中＝框选（MDI）/ 套索（Fluent）
            Host.Commands.SetTool(Tool.Marquee);
            Host.Commands.SetSelectMode(SelectMode.Rect);
            SettleFrames(150);
            Check("选择那一格：选中·框选 → MDI 那张（四角括号＋断续边）",
                  ui.CellIconForTest(7) == "mdiSelection",
                  $"画的是 {ui.CellIconForTest(7)}（期望 mdiSelection）");
            Host.Commands.SetSelectMode(SelectMode.Lasso);
            SettleFrames(150);
            Check("选择那一格：切到套索 → 图标跟着换成套索",
                  ui.CellIconForTest(7) == "lasso",
                  $"画的是 {ui.CellIconForTest(7)}（期望 lasso）");
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
            Check("选择那一格：没选中 → 固定的那支（Lucide 虚线框＋指针）",
                  ui.CellIconForTest(7) == "lucideSelectPointer",
                  $"画的是 {ui.CellIconForTest(7)}（期望 lucideSelectPointer）");

            Host.Commands.SetTool(Tool.Laser);
            SettleFrames(150);
            string laserOn = ui.CellIconForTest(5);
            Check("激光笔那一格：选中 →「笔射出一道光」那张（现在是哪一支见 IconAtlas）",
                  laserOn.StartsWith("laser") || laserOn.StartsWith("msStylusLaser"),
                  $"画的是 {laserOn}");
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
            string laserOff = ui.CellIconForTest(5);
            // 这条是**真断言**：两态必须是**两支不同的画法**——判据和绘制同源，
            // 所以"未选中那一档忘了换"这类漏法会当场红（不是"自己跟自己比"）。
            Check("激光笔那一格：未选中 != 选中（两态是两支画法，不许同一张）",
                  laserOff != laserOn, $"未选中 = {laserOff}，选中 = {laserOn}");
        }

        // ---- ⑥.4 截图那一格真的接上了（用户 2026-09-17："截图功能还没有接进来"）----
        //
        // 引擎里截图工具（Tool.Capture）和"拖框抓图"这条链路是有的（--capturetest 全绿），
        // 面板上第 9 格也在走 SetTool(Capture)。这一条把它**钉在自检里**：
        // 点一下必须真的切到截图工具、而且那一格要亮——不然老师点了没反应，
        // 只能得出"还没接进来"这个结论（这正是用户看到的）。
        //
        // 8.3.1 又补了两条（用户 2026-09-30："我点了这个按钮以后屏幕没有灰下来"）：
        //   · 点格子 = **只出模式条**（还不进取景）；
        //   · 点模式段 = **进入取景**（遮罩铺上，屏幕灰下来）；Esc 退出并**还给上一个工具**。
        {
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
            var cap = ui.CellRectForTest(9);
            ClickPhysical((cap.MinX + cap.MaxX) * 0.5f * DpiScale,
                          (cap.MinY + cap.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点「截屏」真的切到截图工具", Tool == Tool.Capture, $"工具 = {Tool}");
            Check("截屏那一格亮起来（点了有反馈）", ui.CellActiveForTest(9),
                  $"截屏格高亮 = {ui.CellActiveForTest(9)}");
            Check("点格子**先不进取景**（8.3.1：出模式条，屏幕还没灰）",
                  !CaptureActive, $"取景 = {CaptureActive}");

            // 截图那一格的上带：**[截图][隐藏窗口截图]**（8.3.1 照微信改的名；
            // 8.3.0 叫"连批注拍/只拍下层"）+ 右端一颗**动作按钮「粘贴图片」**。
            var capBar = ui.BarRectForTest;
            SendMouse((int)((capBar.MinX + capBar.MaxX) * 0.5f * DpiScale),
                      (int)((capBar.MinY + capBar.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
            var segDirect = ui.SegmentRectForTest(0);
            var segHidden = ui.SegmentRectForTest(1);
            Check("截屏那一格有两条截法（0 宽 = 没接上）",
                  ui.BandSegmentCountForTest == 2
                  && segDirect.MaxX - segDirect.MinX > 20f && segHidden.MinX > segDirect.MaxX - 1f,
                  $"段数 {ui.BandSegmentCountForTest}，段宽 {segDirect.MaxX - segDirect.MinX:F0}，"
                  + $"第二段起点 {segHidden.MinX:F0}");

            void EscKey()
            {
                Native.PostMessage(_windows[0].Hwnd, (uint)Native.WM_KEYDOWN,
                                   new IntPtr(0x1B), IntPtr.Zero);
                SettleFrames(400);
            }

            // 第一段「截图」= 进屋（整屏灰下来）
            ClickPhysical((segDirect.MinX + segDirect.MaxX) * 0.5f * DpiScale,
                          (segDirect.MinY + segDirect.MaxY) * 0.5f * DpiScale);
            SettleFrames(450);
            Check("点「截图」：**进入取景**（遮罩铺上）",
                  CaptureActive && !CaptureAdjusting && Tool == Tool.Capture,
                  $"取景={CaptureActive} 调整={CaptureAdjusting} 工具={Tool}");
            Check("点「截图」→ hideInk = false",
                  !Host.State.CaptureHideInk, $"hideInk = {Host.State.CaptureHideInk}");
            EscKey();
            Check("Esc 退出取景：**还给进来之前的工具**",
                  !CaptureActive && Tool == Tool.Pen,
                  $"取景={CaptureActive}，工具={Tool}（期望 Pen）");

            // 第二段「隐藏窗口截图」= 进屋 + hideInk = true
            ClickPhysical((cap.MinX + cap.MaxX) * 0.5f * DpiScale,
                          (cap.MinY + cap.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            SendMouse((int)((capBar.MinX + capBar.MaxX) * 0.5f * DpiScale),
                      (int)((capBar.MinY + capBar.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
            segHidden = ui.SegmentRectForTest(1);
            ClickPhysical((segHidden.MinX + segHidden.MaxX) * 0.5f * DpiScale,
                          (segHidden.MinY + segHidden.MaxY) * 0.5f * DpiScale);
            SettleFrames(450);
            Check("点「隐藏窗口截图」：进入取景 + hideInk = true",
                  CaptureActive && Host.State.CaptureHideInk,
                  $"取景={CaptureActive}，hideInk = {Host.State.CaptureHideInk}");
            EscKey();
            Check("Esc 再退一次（截图模式不粘手）", !CaptureActive && Tool == Tool.Pen,
                  $"取景={CaptureActive}，工具={Tool}");

            // 「粘贴图片」：右端的动作按钮（没有键盘的触摸屏 / 手写板也能用）
            ClickPhysical((cap.MinX + cap.MaxX) * 0.5f * DpiScale,
                          (cap.MinY + cap.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            SendMouse((int)((capBar.MinX + capBar.MaxX) * 0.5f * DpiScale),
                      (int)((capBar.MinY + capBar.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
            {
                // 先在剪贴板上放一张 60×40 的图（绿底），再点动作按钮
                var buf = new byte[60 * 40 * 4];
                for (int i = 0; i < buf.Length; i += 4)
                { buf[i] = 0; buf[i + 1] = 200; buf[i + 2] = 0; buf[i + 3] = 255; }
                ClipboardImage.SetImage(buf, 60, 40);
                int imgBefore = Doc.Strokes.Count(s => s.IsImage);

                var actPaste = ui.ActionRectForTest;
                Check("「粘贴图片」是右端的动作按钮（不再是第三段）",
                      actPaste.MaxX - actPaste.MinX > 40f,
                      $"动作按钮宽 {actPaste.MaxX - actPaste.MinX:F0}（0 = 没有）");
                ClickPhysical((actPaste.MinX + actPaste.MaxX) * 0.5f * DpiScale,
                              (actPaste.MinY + actPaste.MaxY) * 0.5f * DpiScale);
                SettleFrames(400);
                int imgAfter = Doc.Strokes.Count(s => s.IsImage);
                Check("点「粘贴图片」：剪贴板里的图进了画布",
                      imgAfter == imgBefore + 1,
                      $"图像对象 {imgBefore} → {imgAfter}");

                // 收尾：把刚粘的那张删掉
                Doc.DeleteSelected();
                SettleFrames(150);
            }

            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
        }

        // ---- ⑥.5 上带右端的两个动作：清空（按住 0.8 秒）、全选（点一下）----
        //
        // 出自假面板：清空挂在**橡皮**那条（擦一点/擦一块/全擦掉是一路的事），
        // 按住 0.8 秒才算数；全选挂在**选择**那条。清空可撤销，但代价大，所以防误触。
        {
            Doc.Clear();
            Doc.ClearHistory();
            for (int i = 0; i < 3; i++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
                };
                s.AddPoint(_virtualX + 300 + i * 40, _virtualY + 300, 1f, 0);
                s.AddPoint(_virtualX + 380 + i * 40, _virtualY + 340, 1f, 1);
                Doc.AddStroke(s);
            }
            // 再补一笔**洋红**的：专供"屏幕像素"核对。本轮那个 bug（清空之后墨还留在
            // 屏幕上、连橡皮都擦不掉）只有量屏幕才抓得到——当时只验了 Doc.Strokes.Count。
            float magX = _virtualX + 900f, magY = _virtualY + 620f;
            var magStroke = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 26f * DpiScale,
            };
            magStroke.AddPoint(magX - 200f, magY, 1f, 0);
            magStroke.AddPoint(magX + 200f, magY, 1f, 1);
            Doc.AddStroke(magStroke);
            SettleFrames(150);
            SettleFrames(300);
            int inkBefore = ScreenProbe.CountMagenta((int)magX - 240, (int)magY - 40, 480, 80);
            Check("清空前：洋红那笔在屏幕上", inkBefore > 300, $"{inkBefore} 像素");

            Host.Commands.SetTool(Tool.Eraser);
            SettleFrames(200);
            var barA = ui.BarRectForTest;          // 指针挪回面板：设置条才开着
            SendMouse((int)((barA.MinX + barA.MaxX) * 0.5f * DpiScale),
                      (int)((barA.MinY + barA.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);

            var act = ui.ActionRectForTest;
            Check("橡皮那条右端有「清空」", act.MaxX - act.MinX > 40f,
                  $"动作按钮宽 {act.MaxX - act.MinX:F0}");

            float ax = (act.MinX + act.MaxX) * 0.5f * DpiScale;
            float ay = (act.MinY + act.MaxY) * 0.5f * DpiScale;
            int before = Doc.Strokes.Count;

            // 按住 0.3 秒就松手：**不清空**，而且那一刻进度条该走到一半
            SendMouse((int)ax, (int)ay, 0);                            SettleFrames(60);
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(300);
            bool midHold = ui.ActionHoldingForTest
                        && ui.HoldProgressForTest > 0.1f && ui.HoldProgressForTest < 0.9f;
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(250);
            Check("按住 0.3 秒松手：不清空（进度条走到一半）",
                  midHold && Doc.Strokes.Count == before,
                  $"按住中 = {midHold}，笔画 {before} → {Doc.Strokes.Count}");

            // 按住够 0.8 秒：清空
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(1100);
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(250);
            Check("按住 0.8 秒：清空生效", Doc.Strokes.Count == 0,
                  $"笔画 {before} → {Doc.Strokes.Count}");

            // **屏幕上也得干净**。这一条是本轮真 bug 的靶子：清空是在界面的 Render 里
            // 触发的，引擎渲染完无条件把脏区清了 → 文档空了、屏幕上那层墨还留着，
            // 表现就是"清空没用，而且常规橡皮也擦不掉"（文档里已经没东西可擦）。
            int inkAfter = ScreenProbe.CountMagenta((int)magX - 240, (int)magY - 40, 480, 80);
            Check("清空之后**屏幕上**也干净了（不然就是'墨迹卡住'）", inkAfter < 40,
                  $"{inkBefore} → {inkAfter} 像素");

            Host.Commands.Undo();
            SettleFrames(250);
            Check("清空能撤销回来（不是不可逆的破坏）", Doc.Strokes.Count == before,
                  $"撤销后 {Doc.Strokes.Count} 笔");
            int inkUndo = ScreenProbe.CountMagenta((int)magX - 240, (int)magY - 40, 480, 80);
            Check("撤销之后墨回到屏幕上", inkUndo > 300, $"{inkAfter} → {inkUndo} 像素");

            // 全选：挂在选择那条
            Host.Commands.SetTool(Tool.Marquee);
            SettleFrames(200);
            var barS = ui.BarRectForTest;
            SendMouse((int)((barS.MinX + barS.MaxX) * 0.5f * DpiScale),
                      (int)((barS.MinY + barS.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
            var act2 = ui.ActionRectForTest;
            Check("选择那条右端有「全选」", act2.MaxX - act2.MinX > 40f,
                  $"动作按钮宽 {act2.MaxX - act2.MinX:F0}");

            Doc.Selected.Clear();
            ClickPhysical((act2.MinX + act2.MaxX) * 0.5f * DpiScale,
                          (act2.MinY + act2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点「全选」：选中的条数 = 笔画数",
                  Doc.Selected.Count == Doc.Strokes.Count && Doc.Selected.Count > 0,
                  $"选中 {Doc.Selected.Count} / 笔画 {Doc.Strokes.Count}");
        }

        // ---- ⑥.6 撤销/重做的**灰度**（用户 2026-09-17："撤销重做灰度"）----
        {
            Doc.Clear();
            Doc.ClearHistory();
            SettleFrames(250);
            Check("栈空：撤销和重做都压暗",
                  ui.CellUnavailableForTest(10) && ui.CellUnavailableForTest(11),
                  $"撤销压暗 = {ui.CellUnavailableForTest(10)}，重做压暗 = {ui.CellUnavailableForTest(11)}");

            var s1 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0f, 0f, 1f), Width = 6f,
            };
            s1.AddPoint(_virtualX + 400, _virtualY + 400, 1f, 0);
            s1.AddPoint(_virtualX + 500, _virtualY + 420, 1f, 1);
            Doc.AddStroke(s1);
            SettleFrames(250);
            Check("画一笔之后：撤销亮、重做仍暗",
                  !ui.CellUnavailableForTest(10) && ui.CellUnavailableForTest(11),
                  $"撤销压暗 = {ui.CellUnavailableForTest(10)}，重做压暗 = {ui.CellUnavailableForTest(11)}");

            Host.Commands.Undo();
            SettleFrames(250);
            Check("撤销之后：重做亮起来",
                  ui.CellUnavailableForTest(10) && !ui.CellUnavailableForTest(11),
                  $"撤销压暗 = {ui.CellUnavailableForTest(10)}，重做压暗 = {ui.CellUnavailableForTest(11)}");
            Doc.Clear();
            Doc.ClearHistory();
            SettleFrames(150);
        }

        // ---- ⑥.8 白板色带：翻页器 ＋ 板色 ＋ 底纹 ＋ 间距（2026-09-27 重排）----
        //
        // 两件事一起做的：
        //   · 用户 2026-09-27："我现在要把更多里面的白板底纹，底纹间距这些设置移动到白板的
        //     展开色带里面" → 抽屉那两行删掉、搬到这里（**引擎接口与偏好键没动，存档格式不变**）；
        //   · 同一天还说了"上下翻页和页码应该挨着" → 两件翻页和「第 N 屏」合成一个翻页器，
        //     摆在带子最左，不再被三个板色劈成两端。
        // ⚠ 这一段**按段种找段、不写死下标**：段序刚被重排过，写死下标会静默点到别的段上
        //   （那正是抽屉那边当年踩过的坑，见 `RowRectByLabelForTest` 的注释）。
        {
            Host.Commands.SetBoard(false);                 // 先关板，等下"点那一格"把它打开
            Host.Commands.SetBoardPattern(0, 40f);
            SettleFrames(250);

            var bCell = ui.CellRectForTest(2);
            ClickPhysical((bCell.MinX + bCell.MaxX) * 0.5f * DpiScale,
                          (bCell.MinY + bCell.MaxY) * 0.5f * DpiScale);
            SettleFrames(300);

            int patSeg = ui.BoardSegIndexOfForTest("Pattern");
            int stepSeg = ui.BoardSegIndexOfForTest("Step");
            Check("白板色带上有「底纹」「间距」两段（从「更多」抽屉搬过来的）",
                  patSeg >= 0 && stepSeg >= 0 && BoardOn,
                  $"底纹第 {patSeg} 段、间距第 {stepSeg} 段（共 {ui.BandSegmentCountForTest} 段），板开 = {BoardOn}");

            // 翻页器：三件"挨着"——段序上连着，几何上也贴着
            int pgUpSeg = ui.BoardSegIndexOfForTest("PageUp");
            int pgLblSeg = ui.BoardSegIndexOfForTest("PageLabel");
            int pgDownSeg = ui.BoardSegIndexOfForTest("PageDown");
            var rUp = ui.SegmentRectForTest(pgUpSeg);
            var rLbl = ui.SegmentRectForTest(pgLblSeg);
            var rDown = ui.SegmentRectForTest(pgDownSeg);
            Check("翻页两件和「第 N 屏」挨着（‹ / 页码 / › 三件连着）",
                  pgLblSeg == pgUpSeg + 1 && pgDownSeg == pgLblSeg + 1
                  && rUp.MaxX <= rLbl.MinX + 0.6f && rLbl.MaxX <= rDown.MinX + 0.6f,
                  $"段序 {pgUpSeg}/{pgLblSeg}/{pgDownSeg}，两处缝 {rLbl.MinX - rUp.MaxX:F1} / {rDown.MinX - rLbl.MaxX:F1}");

            // 底纹：三档循环（无 → 方格 → 横线 → 无）
            var pr = ui.SegmentRectForTest(patSeg);
            ClickPhysical((pr.MinX + pr.MaxX) * 0.5f * DpiScale, (pr.MinY + pr.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点色带上的「底纹」→ 方格",
                  Host.State.BoardPattern == 1, $"底纹 = {Host.State.BoardPattern}（1 = 方格）");

            pr = ui.SegmentRectForTest(patSeg);
            ClickPhysical((pr.MinX + pr.MaxX) * 0.5f * DpiScale, (pr.MinY + pr.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("再点一下 → 横线",
                  Host.State.BoardPattern == 2, $"底纹 = {Host.State.BoardPattern}（2 = 横线）");

            // 间距：**五档循环**（20 / 30 / 40 / 64 / 96）——用户 2026-09-27："需要多几档"
            float step0 = Host.State.BoardPatternStep;
            var sr = ui.SegmentRectForTest(stepSeg);
            ClickPhysical((sr.MinX + sr.MaxX) * 0.5f * DpiScale, (sr.MinY + sr.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("点色带上的「间距」→ 换下一档（五档循环）",
                  MathF.Abs(Host.State.BoardPatternStep - step0) > 1f,
                  $"{step0:F0} → {Host.State.BoardPatternStep:F0}");

            // 板没开时点这两格：改了**顺手把板打开**——底纹只画在板面上，不打开就等于改了看不见
            //（这条原来是靠"白板没开就把那两行压暗"来守的，搬进色带之后换成更好用的做法）。
            Host.Commands.SetBoard(false);
            SettleFrames(250);
            int patBefore = Host.State.BoardPattern;
            pr = ui.SegmentRectForTest(patSeg);
            ClickPhysical((pr.MinX + pr.MaxX) * 0.5f * DpiScale, (pr.MinY + pr.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("板没开时点「底纹」：改了顺带把板打开",
                  Host.State.BoardPattern != patBefore && BoardOn,
                  $"底纹 {patBefore} → {Host.State.BoardPattern}，板开 = {BoardOn}");

            // **每一段都不许越出带子**：本轮顺带修掉的老 bug 就是"5 段写死 70 宽把色带撑爆"
            //（极简档那条带子只有 315 宽）。这条断言就是那个 bug 的哨兵。
            bool fit = true; string why = "";
            {
                var bd = ui.BandRectForTest;
                for (int k = 0; k < ui.BandSegmentCountForTest; k++)
                {
                    var segR = ui.SegmentRectForTest(k);
                    if (segR.MaxX > bd.MaxX - 15f)
                    {
                        fit = false;
                        why = $"第 {k} 段的右沿 {segR.MaxX:F0} 越过了带子右沿 {bd.MaxX - 15f:F0}";
                        break;
                    }
                }
            }
            Check("完整档：白板色带每一段都在带子里", fit, why.Length == 0 ? "最右一段也没越界" : why);

            // 「更多」抽屉里**不该再有**那两行——防"挪了没删"（那样两处都能改，用户会以为是两个开关）。
            // 口径和「坐标系网格」那次一样：**按标签找，找不到 = 空矩形**。
            var moreMoved = ui.CellRectForTest(12);
            ClickPhysical((moreMoved.MinX + moreMoved.MaxX) * 0.5f * DpiScale,
                          (moreMoved.MinY + moreMoved.MaxY) * 0.5f * DpiScale);
            SettleFrames(300);
            Check("「更多」面板里已经没有「白板底纹」这一行",
                  ui.RowRectByLabelForTest("白板底纹").IsEmpty, "按标签找不到 = 已经删干净");
            Check("「更多」面板里已经没有「底纹间距」这一行",
                  ui.RowRectByLabelForTest("底纹间距").IsEmpty, "按标签找不到 = 已经删干净");
            var moreMoved2 = ui.CellRectForTest(12);
            ClickPhysical((moreMoved2.MinX + moreMoved2.MaxX) * 0.5f * DpiScale,
                          (moreMoved2.MinY + moreMoved2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
        }

        // ---- ⑥.9 极简档：白板色带自动退到**紧凑布局** ----
        //
        // 用户 2026-09-27 定的口径："翻页不要，保留黑白色，底纹 间距"。
        // 极简档那条带子只有 315 宽（完整布局要 420 ＋ 滑条），所以这一格会退到紧凑布局。
        // 判据是 BoardCompactBand —— **真的量一下放不放得下**，不是写死"极简档"；
        // 所以"自定义档里把格子取消钉得只剩几格"也一样不会撑爆（那正是原来坏掉的根因）。
        {
            ui.SetProfileForTest(0);                        // 极简档（短胶囊）
            SettleFrames(350);
            var bCellMini = ui.CellRectForTest(2);
            ClickPhysical((bCellMini.MinX + bCellMini.MaxX) * 0.5f * DpiScale,
                          (bCellMini.MinY + bCellMini.MaxY) * 0.5f * DpiScale);
            SettleFrames(300);

            Check("极简档：白板色带退到紧凑布局（翻页那两件不放）",
                  ui.BoardCompactBandForTest && ui.BoardSegIndexOfForTest("PageUp") < 0
                  && ui.BoardSegIndexOfForTest("PageLabel") < 0,
                  $"紧凑 = {ui.BoardCompactBandForTest}，段数 {ui.BandSegmentCountForTest}");
            Check("极简档：板色三个 ＋ 底纹 ＋ 间距都还在",
                  ui.BoardSegIndexOfForTest("Color0") >= 0 && ui.BoardSegIndexOfForTest("Color1") >= 0
                  && ui.BoardSegIndexOfForTest("Color2") >= 0
                  && ui.BoardSegIndexOfForTest("Pattern") >= 0 && ui.BoardSegIndexOfForTest("Step") >= 0,
                  $"段数 {ui.BandSegmentCountForTest}");

            bool fitMini = true; string whyMini = "";
            {
                var bd = ui.BandRectForTest;
                for (int k = 0; k < ui.BandSegmentCountForTest; k++)
                {
                    var segR = ui.SegmentRectForTest(k);
                    if (segR.MaxX > bd.MaxX - 15f)
                    {
                        fitMini = false;
                        whyMini = $"第 {k} 段的右沿 {segR.MaxX:F0} 越过了带子右沿 {bd.MaxX - 15f:F0}";
                        break;
                    }
                }
            }
            Check("极简档：白板色带每一段都在带子里（原来就是这儿撑爆的）",
                  fitMini, whyMini.Length == 0 ? "最右一段也没越界" : whyMini);

            // 关板入口**不许随档位消失**：极简档也得有那个 ✕（否则这一档就关不掉白板了）
            var arMini = ui.ActionRectForTest;
            Check("极简档：最右端那个「关闭白板」✕ 也在",
                  !arMini.IsEmpty, $"按钮矩形 {arMini.MinX:F0},{arMini.MinY:F0}-{arMini.MaxX:F0},{arMini.MaxY:F0}");

            // 布局换了、行为不能变：极简档里点底纹照样换档
            int patSegMini = ui.BoardSegIndexOfForTest("Pattern");
            var prM = ui.SegmentRectForTest(patSegMini);
            int patBeforeM = Host.State.BoardPattern;
            ClickPhysical((prM.MinX + prM.MaxX) * 0.5f * DpiScale, (prM.MinY + prM.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("极简档：点色带上的「底纹」照样换档",
                  Host.State.BoardPattern != patBeforeM,
                  $"{patBeforeM} → {Host.State.BoardPattern}");

            ui.SetProfileForTest(2);                        // 回完整档
            SettleFrames(350);
            Host.Commands.SetBoardPattern(0, 40f);          // 收尾：回到"无底纹"
            // **收尾要把指针挪回主条**：色带只在指针落在面板上时才张开，指针不动就永远不张开，
            // 后面那几条"点分段"的用例就会全部点空（当年就是这么红的）。
            var barBack = ui.BarRectForTest;
            SendMouse((int)((barBack.MinX + barBack.MaxX) * 0.5f * DpiScale),
                      (int)((barBack.MinY + barBack.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(400);
        }

        // ---- ⑥.10 白板格：**点三下是一个来回**（用户 2026-09-27 第三次定）----
        //
        // 用户的三条原话就是规则本身：
        //   "1. 如果第一遍打开，白板是开启的；2. 开启后，如果此时色带不在白板上，
        //    再点击一下，色带就会来到白板；3. 再点击一下，白板就关掉。"
        // 还加了一句"单击切换上面的白板颜色，我感觉不需要了"——所以原来"第三下换板色"
        // 那条**删掉了**，第三下改成关板（`CycleBoardColor` 整个函数已删）。
        //
        // 下面按 ①②③④ 逐下点过去，验的就是这个闭环；**板色一下都不许变**。
        {
            // 起手先把色带落到「笔」那一格（产品里就是点一下笔格）
            var penCellA = ui.CellRectForTest(3);
            ClickPhysical((penCellA.MinX + penCellA.MaxX) * 0.5f * DpiScale,
                          (penCellA.MinY + penCellA.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Host.Commands.SetBoard(false);
            Host.Commands.SetBoardColor(InkPalette.BoardPresets[0].Color);   // 白板：好看出"这一下没换色"
            SettleFrames(300);

            bool SameBoard(Color4 a, Color4 b) =>
                MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f
                && MathF.Abs(a.B - b.B) < 0.02f;
            void ClickBoardCell()
            {
                var c = ui.CellRectForTest(2);
                ClickPhysical((c.MinX + c.MaxX) * 0.5f * DpiScale,
                              (c.MinY + c.MaxY) * 0.5f * DpiScale);
                SettleFrames(300);
            }

            // ① 板关着 → 开板 ＋ 色带切过来（**不换色**）
            ClickBoardCell();
            Check("① 板关着时点白板格：开板 ＋ 色带切到白板（不换色）",
                  BoardOn && ui.BandCellForTest == 2
                  && SameBoard(Host.State.BoardColor, InkPalette.BoardPresets[0].Color),
                  $"板开 = {BoardOn}，色带在第 {ui.BandCellForTest} 格（2 = 白板），板色 = {Host.State.BoardColor}");

            // 回到"用笔写字"这个常态：点笔格把色带拿回去（板**保持开着**）
            var penCell2 = ui.CellRectForTest(3);
            ClickPhysical((penCell2.MinX + penCell2.MaxX) * 0.5f * DpiScale,
                          (penCell2.MinY + penCell2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("（前提）点笔格：色带回到笔那一格，板还是开着的",
                  ui.BandCellForTest == 3 && BoardOn,
                  $"色带在第 {ui.BandCellForTest} 格（3 = 笔），板开 = {BoardOn}");

            // ② 板开着、色带在笔 → 只把色带拿过来，板一动不动
            ClickBoardCell();
            Check("② 板开着时点白板格：只把色带切过来，**板一动不动**",
                  BoardOn && ui.BandCellForTest == 2,
                  $"板开 = {BoardOn}（必须还是 True），色带在第 {ui.BandCellForTest} 格");

            // ③ 色带已经在白板格 → **关板**（这一下原来是"换个板色"，用户不要了）
            ClickBoardCell();
            Check("③ 色带就在这一格时再点：**白板关掉**（不再是换板色）",
                  !BoardOn && ui.BandCellForTest == 2,
                  $"板开 = {BoardOn}（期望 False），色带在第 {ui.BandCellForTest} 格");

            // ④ 再点一下 → 又开板（三下之后循环，不会卡在"关着点不动"）
            ClickBoardCell();
            Check("④ 关着再点：又开板（这三下是循环的）", BoardOn, $"板开 = {BoardOn}");

            // 全程**板色一点没变**（用户明确不要"单击换板色"了）
            Check("全程板色没被动过（单击不再换色）",
                  SameBoard(Host.State.BoardColor, InkPalette.BoardPresets[0].Color),
                  $"板色 = {Host.State.BoardColor}");

            // ---- 设置条最右端那个 ✕ 仍然在（一眼看得见的关板键）----
            var ar = ui.ActionRectForTest;
            Check("白板设置条上有关闭按钮（最右端，和橡皮「清空」同一个位置）",
                  !ar.IsEmpty, $"按钮矩形 {ar.MinX:F0},{ar.MinY:F0}-{ar.MaxX:F0},{ar.MaxY:F0}");

            ClickPhysical((ar.MinX + ar.MaxX) * 0.5f * DpiScale,
                          (ar.MinY + ar.MaxY) * 0.5f * DpiScale);
            SettleFrames(300);
            Check("点色带上那个 ✕ → 白板关掉", !BoardOn, $"板开 = {BoardOn}");

            // 板已经关着时，那个 ✕ 是压暗的、点了也不动（关了没意义）
            var ar2 = ui.ActionRectForTest;
            ClickPhysical((ar2.MinX + ar2.MaxX) * 0.5f * DpiScale,
                          (ar2.MinY + ar2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("板已经关着时点 ✕：什么都不发生（还是关着）",
                  !BoardOn, $"板开 = {BoardOn}");

            Host.Commands.SetBoard(false);
            SettleFrames(200);
        }

        // ---- ⑥.11 笔 / 荧光笔：**已经是它了，再点一下换下一个颜色**（用户 2026-09-27 定）----
        //
        // 同一天还定了另一件事，在这里一起验：**荧光笔用自己的一张亮色表**
        //（不再和笔共用一套色相——那样能选到"黑/灰"，画出来只是一道灰道道）。
        {
            // "两个颜色算同一个"——口径和 FullUi.SameColor 一致（阈值 0.02）
            bool SameCol(Color4 a, Color4 b) =>
                MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f
                && MathF.Abs(a.B - b.B) < 0.02f;

            var penPal = InkUi.Tokens.Palette;                 // 笔那张（12 个）
            var hlPal = InkUi.Tokens.HighlighterPalette;       // 荧光笔那张（5 个）

            // 起手：把色带挪到别格（点橡皮），再把笔色设成新表第 1 个（黑）
            var offCell = ui.CellRectForTest(6);
            ClickPhysical((offCell.MinX + offCell.MaxX) * 0.5f * DpiScale,
                          (offCell.MinY + offCell.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Host.Commands.SetColor(penPal[0].Color);
            SettleFrames(200);

            // 这一下**只把色带拿过来，不换色**（"把笔拿回来"不能被悄悄换色）
            var penC1 = ui.CellRectForTest(3);
            ClickPhysical((penC1.MinX + penC1.MaxX) * 0.5f * DpiScale,
                          (penC1.MinY + penC1.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("色带不在笔格时点笔格：色带过来、**颜色不动**",
                  ui.BandCellForTest == 3 && SameCol(Host.State.PaletteBase, penPal[0].Color),
                  $"色带在第 {ui.BandCellForTest} 格（应是 3），颜色 {Host.State.PaletteBase}");

            // 现在色带已经在笔格了 —— 再点一下 = 换下一个颜色（黑 → 红）
            var penC2 = ui.CellRectForTest(3);
            ClickPhysical((penC2.MinX + penC2.MaxX) * 0.5f * DpiScale,
                          (penC2.MinY + penC2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("已经是笔、再点一下：换到下一个颜色（黑 → 红）",
                  SameCol(Host.State.PaletteBase, penPal[1].Color),
                  $"期望 {penPal[1].Name}，实际 {Host.State.PaletteBase}");

            // 再点一下 → 蓝（第 3 个）：连点就是顺着色片表往下走
            var penC3 = ui.CellRectForTest(3);
            ClickPhysical((penC3.MinX + penC3.MaxX) * 0.5f * DpiScale,
                          (penC3.MinY + penC3.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("再点一下（红 → 蓝）：顺序 = 色片表的顺序",
                  SameCol(Host.State.PaletteBase, penPal[2].Color),
                  $"期望 {penPal[2].Name}，实际 {Host.State.PaletteBase}");

            // 环回：先把色设成**最后一个**（藏青），点一下应该绕回第 1 个（黑）
            Host.Commands.SetColor(penPal[penPal.Length - 1].Color);
            SettleFrames(200);
            var penC4 = ui.CellRectForTest(3);
            ClickPhysical((penC4.MinX + penC4.MaxX) * 0.5f * DpiScale,
                          (penC4.MinY + penC4.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("走到最后一个再点：环回第 1 个（藏青 → 黑）",
                  SameCol(Host.State.PaletteBase, penPal[0].Color),
                  $"期望 {penPal[0].Name}，实际 {Host.State.PaletteBase}");

            // ---- 荧光笔：独立的那张亮色表 ----
            var keepPen = Host.State.PaletteBase;              // 记下笔色，等下要比

            var hlC1 = ui.CellRectForTest(4);
            ClickPhysical((hlC1.MinX + hlC1.MaxX) * 0.5f * DpiScale,
                          (hlC1.MinY + hlC1.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("切到荧光笔：色片换成那张亮色表（5 个）",
                  ui.BandCellForTest == 4 && ui.SwatchCountForTest == hlPal.Length,
                  $"色片数 {ui.SwatchCountForTest}（笔那张是 {penPal.Length}）");
            Check("荧光笔当前色 = 第一个色片「荧光黄」（和引擎默认色对得上）",
                  SameCol(Host.State.PaletteBase, hlPal[0].Color),
                  $"期望 {hlPal[0].Name}，实际 {Host.State.PaletteBase}");

            // 荧光笔也支持"再点一下换色"
            var hlC2 = ui.CellRectForTest(4);
            ClickPhysical((hlC2.MinX + hlC2.MaxX) * 0.5f * DpiScale,
                          (hlC2.MinY + hlC2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("荧光笔再点一下：换到下一个（荧光黄 → 荧光绿）",
                  SameCol(Host.State.PaletteBase, hlPal[1].Color),
                  $"期望 {hlPal[1].Name}，实际 {Host.State.PaletteBase}");

            // 换荧光笔的颜色**不许带着笔的颜色一起变**（两套色各记各的）
            var backPen = ui.CellRectForTest(3);
            ClickPhysical((backPen.MinX + backPen.MaxX) * 0.5f * DpiScale,
                          (backPen.MinY + backPen.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("换荧光笔的颜色**不影响笔的颜色**（两套各记各的）",
                  ui.BandCellForTest == 3 && SameCol(Host.State.PaletteBase, keepPen),
                  $"笔色应还是 {Host.State.PaletteBase}，色带在第 {ui.BandCellForTest} 格");

            Host.Commands.SetTool(Tool.Pen);
            Host.Commands.SetColor(InkPalette.PenDefault);     // 收尾：回到默认红
            SettleFrames(200);
        }

        // ---- ⑥.12 选择格：**已经是它了，再点一下换下一档** ＋ **双击 = 全选**（用户 2026-09-27 定）----
        //
        // 和笔 / 荧光笔"再点一下换个颜色"是同一条规矩，只是这里换的是**选择方式**
        //（矩形框选 ←→ 自由套索，见 FullUi.CycleSelectMode）。
        //
        // 双击全选照的是 InkClass 那个"经典交互"（它那边 500ms 内双击选择图标 = 全选）。
        // 能跟"单击换档"共存**全靠只有两档**：连点两下正好转两圈回到原档，
        // 所以双击的净效果就是"全选、模式没动"——这一条也断言了（不然以后加第三档会静默出错）。
        {
            // "快击"：两次之间的间隔必须**真的**小于 500ms（`NowMs` 是真实秒表）。
            // 而 `ClickPhysical` 一次就要 460ms（80+60+320），两次相距 920ms —— 判不出双击。
            // 所以双击这件事得用这个 45ms 一发的版本（单发之间照旧用 ClickPhysical ＋ 补时间）。
            void ClickFast(float px, float py)
            {
                SendMouse((int)px, (int)py, 0);                           SettleFrames(15);
                SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(15);
                SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(15);
            }

            // 摆两笔进去：全选才有的可"全"（断言写成"选中的条数 = 总条数"，
            // 不去假设这一刻文档里到底有几条——前面的用例画过东西）。
            Doc.Clear();
            Doc.ClearHistory();
            for (int k = 0; k < 2; k++)
            {
                var line = new Stroke
                {
                    Tool = Tool.Pen, Color = new Color4(0, 0, 0, 1), Width = 6f * DpiScale,
                };
                float ly = VirtualScreen.MinY + 300f + k * 120f;
                for (int i = 0; i <= 60; i++)
                    line.AddPoint(VirtualScreen.MinX + 300f + i * 4f, ly, 0.5f, i);
                Doc.AddStroke(line);
            }
            Doc.Selected.Clear();
            SettleFrames(250);

            var selCell = ui.CellRectForTest(7);
            float sxSel = (selCell.MinX + selCell.MaxX) * 0.5f * DpiScale;
            float sySel = (selCell.MinY + selCell.MaxY) * 0.5f * DpiScale;

            // 起手：色带在**笔**那一格（"从别的工具进来"这一种）
            ui.SelectBandCellForTest(3);
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(200);
            Host.Commands.SetSelectMode(SelectMode.Rect);
            SettleFrames(200);

            // 第一下：从别的工具进来 → 只切工具，**不换档**
            ClickPhysical(sxSel, sySel);
            SettleFrames(400);                       // 把和下一击的间隔拉过 500ms（别被当成双击）
            Check("选择格第一下（从别的工具进来）：只切到选择工具，**档位不动**",
                  Host.State.Tool == Tool.Marquee && Host.State.SelectMode == SelectMode.Rect,
                  $"工具 {Host.State.Tool}，档 {Host.State.SelectMode}（期望 Rect）");

            // [2026-10-05 用户定] 已经是选择工具、再点一下（或按 Ctrl+M）**不再换档**：
            // 爱用矩形的一直用矩形、爱用套索的一直用套索；子类型去上带那两段里选。
            ClickPhysical(sxSel, sySel);
            SettleFrames(400);
            Check("已经是选择工具、再点一下：**档位不动**（不再矩形↔套索）",
                  Host.State.SelectMode == SelectMode.Rect,
                  $"档 {Host.State.SelectMode}（期望 Rect）");

            // 正路：上带里那一段才是切子类型的地方（用命令通道模拟上带点击，
            // 上带分段本身的点击自检见下面的 ⑥.13）。
            Host.Commands.SetSelectMode(SelectMode.Lasso);
            SettleFrames(200);
            Check("上带切套索：档位跟着变", Host.State.SelectMode == SelectMode.Lasso,
                  $"档 {Host.State.SelectMode}（期望 Lasso）");
            Host.Commands.SetSelectMode(SelectMode.Rect);
            SettleFrames(200);

            // ---- 双击 = 全选 ----
            var modeBeforeDbl = Host.State.SelectMode;
            int strokeCount = Doc.Strokes.Count;
            ClickFast(sxSel, sySel);
            ClickFast(sxSel, sySel);
            SettleFrames(300);
            Check("双击选择格 = **全选**",
                  strokeCount > 0 && Doc.Selected.Count == strokeCount,
                  $"选中 {Doc.Selected.Count} / 共 {strokeCount} 条");

            Check("双击全选不动档位", Host.State.SelectMode == modeBeforeDbl,
                  $"档 {modeBeforeDbl} → {Host.State.SelectMode}（期望没变）");

            // 边界：**中间点了别的格子，双击序列要断掉**。
            // 三下挨得再近也不能算"对选择格的双击"——不这么守的话，
            // 老师只是在"选择格 / 笔格"之间来回看一眼，就被全选了。
            Doc.Selected.Clear();
            SettleFrames(150);
            ClickFast(sxSel, sySel);                                   // 选择格（第 1 下）
            var penCellDbl = ui.CellRectForTest(3);
            ClickFast((penCellDbl.MinX + penCellDbl.MaxX) * 0.5f * DpiScale,
                      (penCellDbl.MinY + penCellDbl.MaxY) * 0.5f * DpiScale);   // 摸一下笔格
            ClickFast(sxSel, sySel);                                   // 又回选择格
            SettleFrames(250);
            Check("中间点过别的格子 → 双击序列断掉（不会误全选）",
                  Doc.Selected.Count == 0, $"选中 {Doc.Selected.Count} 条（期望 0）");

            // 收尾：清掉选中、回到笔（选中态会叫出选中操作条，别带给后面的用例）
            Doc.Selected.Clear();
            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(250);
        }

        // ---- ⑥.7 白板和穿透**互斥**（用户 2026-09-17 问："鼠标和白板是不是也应该互斥"）----
        //
        // 该互斥：白板是不透明的一层，穿透是"点击落到下层程序"——两个一起开着，
        // 老师看到的是白板、点到的却是白板下面那个看不见的窗口。
        {
            Host.Commands.SetBoard(true);
            SettleFrames(200);
            Host.Commands.SetPassThrough(true);
            SettleFrames(250);
            Check("开穿透 → 白板自动关掉",
                  !BoardOn && Host.State.PassThrough,
                  $"板开 = {BoardOn}，穿透 = {Host.State.PassThrough}");

            Host.Commands.SetBoard(true);
            SettleFrames(300);
            Check("开白板 → 穿透自动关掉",
                  BoardOn && !Host.State.PassThrough,
                  $"板开 = {BoardOn}，穿透 = {Host.State.PassThrough}");

            // ---- ⑥.7b 退出穿透时的板态恢复（2026-09-30 用户拍板）----
            // 规格：**开关退出**（面板那一格 / Ctrl+Alt+Shift+T）= "回到之前"，板开就恢复；
            //       **换工具退出**（Ctrl+P 等）= "我现在就要写"，板保持关，不能突然盖回来。
            //       （"墨迹不隐藏"是同一批拍板的结果——那一半没有代码改动，无需断言。）
            Host.Commands.SetBoard(true);
            SettleFrames(200);
            Host.Commands.SetPassThrough(true);      // 进穿透：板被自动关，快照"板开"
            SettleFrames(250);
            Host.Commands.SetPassThrough(false);     // 开关退出（和面板格、全局热键同一条路）
            SettleFrames(250);
            Check("开关退出穿透：白板恢复到进穿透之前（开着）",
                  BoardOn && !Host.State.PassThrough,
                  $"板开 = {BoardOn}，穿透 = {Host.State.PassThrough}");

            Host.Commands.SetPassThrough(true);
            SettleFrames(250);
            Host.Commands.SetTool(Tool.Pen);         // 换工具退出（和按 Ctrl+P 同一条路）
            SettleFrames(250);
            Check("换工具退出穿透：白板保持关（不自动盖回来）",
                  !BoardOn && !Host.State.PassThrough && Tool == Tool.Pen,
                  $"板开 = {BoardOn}，穿透 = {Host.State.PassThrough}，工具 = {Tool}");

            Host.Commands.SetBoard(false);
            Host.Commands.SetPassThrough(true);      // 板本来关着：进出穿透不该凭空开板
            SettleFrames(200);
            Host.Commands.SetPassThrough(false);
            SettleFrames(200);
            Check("板本来关着：进出穿透后仍是关着", !BoardOn,
                  $"板开 = {BoardOn}，穿透 = {Host.State.PassThrough}");

            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(200);
        }

        // ---- ⑥.7c 穿透模式下工具键一律不响应（2026-09-30 用户拍板）----
        //
        // 用户原话："开了穿透模式以后，快捷键还能调颜色，但是这个时候它又不是笔，
        // 我感觉这个算 bug。开了穿透模式以后，笔、橡皮这些快捷键应该就没有用了，
        // 等退出穿透模式以后才有用。"
        // 判据：穿透开着时按 Ctrl+P（走同一个命令入口）——不换色、不切工具、也不顺手退穿透；
        // 退出穿透后同一个键立刻恢复。**面板上的工具格不在此列**（点了仍会关穿透，照旧）。
        {
            bool SameCol(Color4 a, Color4 b) =>
                MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f
                && MathF.Abs(a.B - b.B) < 0.02f;

            // ① 是笔：按 Ctrl+P 不许换色
            Host.Commands.SetBoard(false);
            Host.Commands.SetTool(Tool.Pen);
            Host.Commands.SetColor(InkPalette.PenDefault);
            SettleFrames(200);
            var color0 = Host.State.PaletteBase;
            Host.Commands.SetPassThrough(true);
            SettleFrames(200);
            RunActionForTest(KeyAction.ToolPen);
            RunActionForTest(KeyAction.ToolPen);
            SettleFrames(200);
            Check("穿透开着：Ctrl+P 不换色（已经是笔也一样）",
                  Host.State.PassThrough && Tool == Tool.Pen && SameCol(Host.State.PaletteBase, color0),
                  $"穿透 = {Host.State.PassThrough}，工具 = {Tool}，色 {Host.State.PaletteBase}");

            // ② 不是笔：不许切工具、也不许顺手退穿透
            Host.Commands.SetPassThrough(false);
            Host.Commands.SetTool(Tool.Eraser);
            SettleFrames(200);
            Host.Commands.SetPassThrough(true);
            SettleFrames(200);
            RunActionForTest(KeyAction.ToolPen);
            SettleFrames(200);
            Check("穿透开着：Ctrl+P 不切工具、不顺手退穿透",
                  Host.State.PassThrough && Tool == Tool.Eraser,
                  $"穿透 = {Host.State.PassThrough}，工具 = {Tool}");

            // ③ 退出穿透：同一个键立刻恢复
            Host.Commands.SetPassThrough(false);
            SettleFrames(200);
            RunActionForTest(KeyAction.ToolPen);
            SettleFrames(200);
            Check("退出穿透后：Ctrl+P 恢复（切回笔）",
                  !Host.State.PassThrough && Tool == Tool.Pen,
                  $"穿透 = {Host.State.PassThrough}，工具 = {Tool}");

            Host.Commands.SetTool(Tool.Pen);
            SettleFrames(150);
        }

        // 换工具（走引擎那条路，等同按热键）：上带要跟着换成"选择"的设置条
        Host.Commands.SetTool(Tool.Marquee);
        SettleFrames(150);
        ui.OpenRailForTest();               // 确保上带张开：分段只有张开时才吃得到点击
        SettleFrames(200);
        var lasso = ui.SegmentRectForTest(1);
        ClickPhysical((lasso.MinX + lasso.MaxX) * 0.5f * DpiScale,
                      (lasso.MinY + lasso.MaxY) * 0.5f * DpiScale);
        Check("点分段切成套索", Host.State.SelectMode == SelectMode.Lasso,
              $"选择方式 = {Host.State.SelectMode}（上带跟着工具换成选择才点得到）");

        Host.Commands.SetTool(Tool.Pen);
        SettleFrames(150);
        var boardCell2 = ui.CellRectForTest(2);
        ClickPhysical((boardCell2.MinX + boardCell2.MaxX) * 0.5f * DpiScale,
                      (boardCell2.MinY + boardCell2.MaxY) * 0.5f * DpiScale);
        SettleFrames(150);
        // 白板那一格的段序**2026-09-27 重排过**（现在是 `[‹] 第 N 屏 [›] [白][绿][黑] [底纹][间距]`），
        // 所以这里**按段种找段**，不写死下标——写死的话下次重排又会静默点到别的段上
        //（这一条自检第一版就是按老的三段布局点下标 1，点到了"白板"，被判成失败）。
        var green = ui.SegmentRectForTest(ui.BoardSegIndexOfForTest("Color1"));
        ClickPhysical((green.MinX + green.MaxX) * 0.5f * DpiScale,
                      (green.MinY + green.MaxY) * 0.5f * DpiScale);
        var wantBoard = InkPalette.BoardPresets[1].Color;
        var gotBoard = Host.State.BoardColor;
        Check("点板色换成绿板",
              BoardOn && MathF.Abs(wantBoard.R - gotBoard.R) < 0.02f
              && MathF.Abs(wantBoard.G - gotBoard.G) < 0.02f
              && MathF.Abs(wantBoard.B - gotBoard.B) < 0.02f,
              $"板开 = {BoardOn}，板色 ({gotBoard.R:F2},{gotBoard.G:F2},{gotBoard.B:F2})");

        // ---- ⑥.1 白板那一格的滑条 = **板面不透明度**（用户 2026-09-17 要的）----
        {
            float penW = PenWidthLogical;
            var slB = ui.SliderRectForTest;
            float slBy = (slB.MinY + slB.MaxY) * 0.5f * DpiScale;
            var (bl, _) = ui.SliderTrackRangeForTest;

            SendMouse((int)(bl * DpiScale), (int)slBy, 0);                            SettleFrames(60);
            SendMouse((int)(bl * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(50);
            SendMouse((int)(bl * DpiScale), (int)slBy, 0);                            SettleFrames(120);
            SendMouse((int)(bl * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(150);
            Check("白板滑条拖到最左 = 最透（0.35）",
                  MathF.Abs(Host.State.BoardOpacity - 0.35f) < 0.02f,
                  $"不透明度 {Host.State.BoardOpacity:F2}");

            var (_, br2) = ui.SliderTrackRangeForTest;
            SendMouse((int)(br2 * DpiScale), (int)slBy, 0);                            SettleFrames(60);
            SendMouse((int)(br2 * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(50);
            SendMouse((int)(br2 * DpiScale), (int)slBy, 0);                            SettleFrames(120);
            SendMouse((int)(br2 * DpiScale), (int)slBy, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(150);
            Check("白板滑条拖到最右 = 实心（1.0）",
                  MathF.Abs(Host.State.BoardOpacity - 1f) < 0.02f,
                  $"不透明度 {Host.State.BoardOpacity:F2}");
            Check("拖白板滑条**不动笔宽**（和橡皮那条一个坑）",
                  MathF.Abs(PenWidthLogical - penW) < 0.01f,
                  $"笔宽 {penW:F2} → {PenWidthLogical:F2}");
        }

        // ---- ⑥.5 白板翻页：上带上的 [‹] / [›] ----
        // 屏幕高必须是"整数屏"的底数：跑到第一屏（相机偏移 = 0）再往下翻。
        // 段序**按段种找**（2026-09-27 重排过：现在是 `[‹] 第 N 屏 [›] [白][绿][黑] [底纹][间距]`）。
        var upSeg = ui.SegmentRectForTest(ui.BoardSegIndexOfForTest("PageUp"));
        float upX = (upSeg.MinX + upSeg.MaxX) * 0.5f * DpiScale;
        float upY = (upSeg.MinY + upSeg.MaxY) * 0.5f * DpiScale;
        var downSeg = ui.SegmentRectForTest(ui.BoardSegIndexOfForTest("PageDown"));
        float downX = (downSeg.MinX + downSeg.MaxX) * 0.5f * DpiScale;
        float downY = (downSeg.MinY + downSeg.MaxY) * 0.5f * DpiScale;
        float camHome = ViewOffsetY;

        // 已经在最上面：点"上一屏"不该动（按钮也该是压暗的）
        Check("到顶时「上一屏」不可用", !Host.State.CanFlipPageUp,
              $"相机 {camHome:F0}，还能上翻 = {Host.State.CanFlipPageUp}");
        ClickPhysical(upX, upY);
        SettleFrames(400);
        Check("到顶时点「上一屏」相机不动",
              MathF.Abs(ViewOffsetY - camHome) < 1f,
              $"相机 {camHome:F0} → {ViewOffsetY:F0}");

        ClickPhysical(downX, downY);
        SettleFrames(400);
        Check("点「下一屏」翻到第 2 屏",
              Host.State.ScreenIndex == 2 && ViewOffsetY < -1f,
              $"屏号 {Host.State.ScreenIndex}，相机 {ViewOffsetY:F0}");

        ClickPhysical(downX, downY);
        SettleFrames(400);
        Check("再点一次翻到第 3 屏（下面永远还有一屏）",
              Host.State.ScreenIndex == 3, $"屏号 {Host.State.ScreenIndex}");

        ClickPhysical(upX, upY);
        SettleFrames(400);
        Check("点「上一屏」回到第 2 屏",
              Host.State.ScreenIndex == 2 && Host.State.CanFlipPageUp,
              $"屏号 {Host.State.ScreenIndex}，还能上翻 = {Host.State.CanFlipPageUp}");

        // 收尾：把相机送回第一屏，别把后面的用例带偏（后面的用例都假设相机为 0）
        while (Host.State.CanFlipPageUp)
        {
            ClickPhysical(upX, upY);
            SettleFrames(300);
        }
        Check("翻回第一屏（相机归零）", Math.Abs(ViewOffsetY) < 1f,
              $"相机 {ViewOffsetY:F1}");

        // ---- ⑥.6 放映时白板那一格**仍然只管白板**（用户 2026-09-26 更正：
        //      "取消掉白板区的翻页，白板区不需要 ppt 翻页"）。
        // 这是一条**负向断言**：放映中点两端，命令一条都不许跑到 PPT 上去
        //（PPT 的翻页入口只有底部那两条，见 `--ppttest` ⑧.5）。----
        var pptFake = new PptFakeSource { Showing = true, Slide = 1, SlideId = 101, Total = 5 };

        // ---- ⑥.5 进放映那一刻：面板**自动展开 ＋ 回到默认位置**（用户 2026-09-27 定）----
        //
        // 用户原话："PPT 开始播放以后，把它打开放到居中靠底部。"
        // 先人为破坏一下前提（收成球 + 拖到左上角），再进放映——**两条都得被掰回来**。
        //
        // ⚠ 归位目标就是上面 ⑥ 那一段算坐标时所在的**默认位置**，所以 `downX/downY/upX/upY`
        //   （放映中按白板格翻页那几个点）在这之后仍然有效——不是巧合，是同一个 `RawAnchor`。
        ui.SetExpandForTest(0f);
        ui.SetAnchorForTest(new Vector2(work.MinX + 40f, work.MinY + 40f));
        SettleFrames(250);
        Check("（前提）面板已被收成球、并拖到左上角",
              !ui.ExpandedForTest && ui.QueryBounds().MinY < work.MinY + 80f,
              $"展开 = {ui.ExpandedForTest}，占用顶 {ui.QueryBounds().MinY:F0}");

        AttachPptSource(pptFake, watch: false);
        StepPpt();
        SettleFrames(300);
        Check("进放映：引擎进入 PPT 模式", PptMode && PptTotal == 5,
              $"PptMode={PptMode}，{PptSlide}/{PptTotal} 页");

        {
            var pb = ui.BarRectForTest;
            Check("进放映：面板**自动展开**（一放片笔就出来）",
                  ui.ExpandedForTest, $"展开 = {ui.ExpandedForTest}");
            Check("进放映：位置**归到工作区底边居中**（拖走过的也掰回来）",
                  MathF.Abs(pb.MaxY - (work.MaxY - InkUi.Tokens.EdgeMargin)) < 1.5f
                  && MathF.Abs((pb.MinX + pb.MaxX) * 0.5f - (work.MinX + work.MaxX) * 0.5f) < 2f,
                  $"条底 {pb.MaxY:F0}（应 {work.MaxY - InkUi.Tokens.EdgeMargin:F0}），"
                  + $"带子中心 {(pb.MinX + pb.MaxX) * 0.5f:F0}"
                  + $"（应 {(work.MinX + work.MaxX) * 0.5f:F0}）");
        }

        // ⚠ 上面那次"收成球再展开"会**把色带一起收掉**：收起时 `_railHover` 被清成 false
        //   （见 UpdateRail），而它**只在指针事件里**重算。不重新碰一下的话，下面 ⑥.6 那几刀
        //   （点「下一屏」/「上一屏」）会点在一条已经收起来的色带上——坐标是对的、东西不在那，
        //   表现是"点了没反应"（自检当场抓到：屏号还是 1、相机还是 0）。
        //   这一段就是"把指针放回面板、等色带重新张开"，和别处恢复悬停用同一个套路。
        {
            var panelBack = ui.BarRectForTest;
            SendMouse((int)((panelBack.MinX + panelBack.MaxX) * 0.5f * DpiScale),
                      (int)((panelBack.MinY + panelBack.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(500);          // 悬停意图 120ms + 色带张开 RailMs
            Check("（准备）指针回面板后色带重新张开（不然下面点空）",
                  !ui.BandRectForTest.IsEmpty, $"色带 {(ui.BandRectForTest.IsEmpty ? "没张开" : "已张开")}");
        }

        float camBeforePpt = ViewOffsetY;
        ClickPhysical(downX, downY);                   // 白板格的「下一屏」
        SettleFrames(400);
        Check("放映中点「下一屏」：翻的是白板、**一条命令都不给 PPT**",
              pptFake.NextCalls == 0 && Host.State.ScreenIndex == 2
              && MathF.Abs(ViewOffsetY - camBeforePpt) > 1f,
              $"NextCalls={pptFake.NextCalls}，屏号 {Host.State.ScreenIndex}，相机 {ViewOffsetY:F0}");

        ClickPhysical(upX, upY);                       // 白板格的「上一屏」
        SettleFrames(400);
        Check("放映中点「上一屏」：回到第 1 屏，也不给 PPT",
              pptFake.PrevCalls == 0 && Host.State.ScreenIndex == 1,
              $"PrevCalls={pptFake.PrevCalls}，屏号 {Host.State.ScreenIndex}");

        pptFake.Showing = false;                       // 退出放映
        StepPpt();
        SettleFrames(200);
        Check("退出放映：退出 PPT 模式", !PptMode, $"PptMode={PptMode}");

        Host.Commands.SetBoard(false);
        Host.Commands.SetBoardColor(InkPalette.BoardPresets[0].Color);
        SettleFrames(150);

        // ---- ⑦ 「更多」面板：开合、页签、深色主题、贴边隐藏 ----
        var moreCell = ui.CellRectForTest(12);
        ClickPhysical((moreCell.MinX + moreCell.MaxX) * 0.5f * DpiScale,
                      (moreCell.MinY + moreCell.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        var moreRect = ui.MoreRectForTest;
        Check("点「更多」开出中央面板", ui.MoreOpenForTest,
              $"面板 ({moreRect.MinX:F0},{moreRect.MinY:F0})-({moreRect.MaxX:F0},{moreRect.MaxY:F0})");

        var panelWork = ui.WorkAreaForTest;
        Check("面板在主屏工作区正中、且在屏幕内",
              ui.MoreOpenForTest
              && MathF.Abs((moreRect.MinX + moreRect.MaxX) * 0.5f - (panelWork.MinX + panelWork.MaxX) * 0.5f) < 2f
              && MathF.Abs((moreRect.MinY + moreRect.MaxY) * 0.5f - (panelWork.MinY + panelWork.MaxY) * 0.5f) < 2f
              && moreRect.MinX >= _virtualX / DpiScale && moreRect.MaxX <= (_virtualX + _virtualW) / DpiScale,
              $"中心 ({(moreRect.MinX + moreRect.MaxX) * 0.5f:F0},{(moreRect.MinY + moreRect.MaxY) * 0.5f:F0})，"
              + $"工作区中心 ({(panelWork.MinX + panelWork.MaxX) * 0.5f:F0},{(panelWork.MinY + panelWork.MaxY) * 0.5f:F0})");

        // 全屏模态的根：打开期间占用 = 整块屏幕（引擎的命中/裁剪/接输入小窗都按它走）
        var modalBounds = ui.QueryBounds();
        Check("面板打开时占用整块屏幕（全屏模态）",
              MathF.Abs(modalBounds.MinX - ui.ScreenForTest.MinX) < 1f
              && MathF.Abs(modalBounds.MaxX - ui.ScreenForTest.MaxX) < 1f
              && MathF.Abs(modalBounds.MaxY - ui.ScreenForTest.MaxY) < 1f,
              $"占用 {modalBounds.MaxX - modalBounds.MinX:F0}×{modalBounds.MaxY - modalBounds.MinY:F0}，"
              + $"屏幕 {ui.ScreenForTest.MaxX - ui.ScreenForTest.MinX:F0}×{ui.ScreenForTest.MaxY - ui.ScreenForTest.MinY:F0}");

        // 打开面板会把设置条收掉：模态期间它不该再冒出来跟面板抢注意力
        Check("面板打开时设置条已经收掉",
              !ui.RailOpenForTest && MathF.Abs(ui.BandHeightForTest - InkUi.Tokens.BandLine) < 1.5f,
              $"设置条高 {ui.BandHeightForTest:F0}（该是色线 {InkUi.Tokens.BandLine:F0}）");

        // ---- 启动器：格子/底栏逐格点得通（"加一格漏一处"用这条兜住）----
        {
            Check("启动器：打开面板默认停在主页", ui.MorePageForTest == 0, $"页 = {ui.MorePageForTest}");
            bool tilesOk = true;
            for (int code = 0; code < 7; code++)
            {
                var t = ui.HubTileRectForTest(code);
                if (t.IsEmpty || t.MinX < moreRect.MinX || t.MaxX > moreRect.MaxX
                    || t.MinY < moreRect.MinY || t.MaxY > moreRect.MaxY) tilesOk = false;
            }
            bool bottomOk = true;
            for (int i = 0; i < 4; i++)
            {
                var t = ui.HubBottomRectForTest(i);
                if (t.IsEmpty || t.MaxX > moreRect.MaxX || t.MaxY > moreRect.MaxY) bottomOk = false;
            }
            Check("启动器：七格（课堂 3 ＋ 墨迹 4）＋ 底栏四格都在面板内", tilesOk && bottomOk,
                  $"面板 {moreRect.MaxX - moreRect.MinX:F0}×{moreRect.MaxY - moreRect.MinY:F0}");

            // 分辨率规范化 B：面板宽 = min(640, 55% 工作宽)
            float wantW = Math.Clamp((panelWork.MaxX - panelWork.MinX) * 0.55f, 380f, 640f);
            Check("面板宽：min(640, 55% 工作宽)", moreRect.MaxX - moreRect.MinX <= wantW + 0.5f,
                  $"宽 {moreRect.MaxX - moreRect.MinX:F0} ≤ {wantW:F0}");

            // 「随机一人」格：关面板 ＋ 点名窗自动开抽，出结果 1.5 秒后自动关
            var oneTile = ui.HubTileRectForTest(2);
            ClickPhysical((oneTile.MinX + oneTile.MaxX) * 0.5f * DpiScale,
                          (oneTile.MinY + oneTile.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("启动器：「随机一人」→ 关面板、点名窗自动开抽",
                  !ui.MoreOpenForTest && Host.State.RollCardOpen && RollingNow,
                  $"panel={ui.MoreOpenForTest} card={Host.State.RollCardOpen} rolling={RollingNow}");
            {
                double t1 = NowMs;
                while (Host.State.RollCardOpen && NowMs - t1 < 3000)
                { DrainMessages(); Thread.Sleep(10); RollTickForTest(); }
                Check("「随机一人」：出结果后自动关窗", !Host.State.RollCardOpen);
            }

            // 「随机一人」会把面板关掉 —— 计时器格之前先把「更多」重新打开
            {
                var again = ui.CellRectForTest(12);
                ClickPhysical((again.MinX + again.MaxX) * 0.5f * DpiScale,
                              (again.MinY + again.MaxY) * 0.5f * DpiScale);
                SettleFrames(250);
            }

            // 计时器格：点它 = 关面板 ＋ 引擎侧计时窗以**待机态**出现
            var timTile = ui.HubTileRectForTest(0);
            ClickPhysical((timTile.MinX + timTile.MaxX) * 0.5f * DpiScale,
                          (timTile.MinY + timTile.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("启动器：「计时器」→ 关面板、计时窗开着（待机）",
                  !ui.MoreOpenForTest && Host.State.TimerCardOpen && !Host.State.TimerSettingsOpen,
                  $"panel={ui.MoreOpenForTest} card={Host.State.TimerCardOpen}");

            // 计时窗真路：点大圆钮 → 跑；点数字 → 暂停；✕ = 停止并关窗
            var tcard = TimerCardRect();
            var tcardIn = TimerWin.CardRect(tcard, DpiScale, false);
            float tlu = TimerWin.Layout(tcardIn, DpiScale);
            var tstart = TimerWin.BtnRect(tcardIn, tlu, TimerZone.Start);
            ClickPhysical((tstart.MinX + tstart.MaxX) * 0.5f, (tstart.MinY + tstart.MaxY) * 0.5f);
            SettleFrames(250);
            Check("计时窗：点大圆钮 → 跑起来", Host.State.TimerActive,
                  $"active={Host.State.TimerActive}");
            var tval = TimerWin.ValueRect(tcardIn, tlu);
            ClickPhysical((tval.MinX + tval.MaxX) * 0.5f, (tval.MinY + tval.MaxY) * 0.5f);
            SettleFrames(150);
            Check("计时窗：点数字 = 暂停", Host.State.TimerPaused);
            var tstop = TimerWin.BtnRect(tcardIn, tlu, TimerZone.Close);
            ClickPhysical((tstop.MinX + tstop.MaxX) * 0.5f, (tstop.MinY + tstop.MaxY) * 0.5f);
            SettleFrames(150);
            Check("计时窗：✕ = 停止并关窗",
                  !Host.State.TimerCardOpen && !Host.State.TimerActive);
            // 三模式/改时长/到点/最小化/全屏那些细节在 `--timertest` 里逐条验，这里只验"启动器→窗"的真路。

            // 点名格 → 点名窗（待抽态）；抽奖 → 滚动 → 定格；✕ 关窗
            var moreCellB = ui.CellRectForTest(12);
            ClickPhysical((moreCellB.MinX + moreCellB.MaxX) * 0.5f * DpiScale,
                          (moreCellB.MinY + moreCellB.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            var rollTile = ui.HubTileRectForTest(1);
            ClickPhysical((rollTile.MinX + rollTile.MaxX) * 0.5f * DpiScale,
                          (rollTile.MinY + rollTile.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("启动器：「点名」→ 关面板、点名窗开着（待抽）",
                  !ui.MoreOpenForTest && Host.State.RollCardOpen && Host.State.RollSettingsOpen,
                  $"card={Host.State.RollCardOpen} set={Host.State.RollSettingsOpen}");
            var rcard = RollCardRect();
            var rdraw = RollWin.DrawRect(rcard, DpiScale);
            ClickPhysical((rdraw.MinX + rdraw.MaxX) * 0.5f, (rdraw.MinY + rdraw.MaxY) * 0.5f);
            Check("点名窗：抽奖后进入滚动", RollingNow);
            {
                double t2 = NowMs;
                while (RollingNow && NowMs - t2 < 3000)
                { DrainMessages(); Thread.Sleep(10); RollTickForTest(); }
            }
            Check("点名窗：定格出结果", !RollingNow && RollResultNow.Length > 0,
                  $"结果 {RollResultNow.Length} 条");
            rcard = RollCardRect();
            var rclose = RollWin.CloseRect(rcard, DpiScale);
            ClickPhysical((rclose.MinX + rclose.MaxX) * 0.5f, (rclose.MinY + rclose.MaxY) * 0.5f);
            SettleFrames(150);
            Check("点名窗：✕ 关窗", !Host.State.RollCardOpen);

            // 命令格：空板书点「保存」，关面板 ＋ 引擎写"没有可保存"（点置灰也写状态）
            Host.Commands.Clear();
            SettleFrames(150);
            var moreCell3 = ui.CellRectForTest(12);
            ClickPhysical((moreCell3.MinX + moreCell3.MaxX) * 0.5f * DpiScale,
                          (moreCell3.MinY + moreCell3.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            var saveTile = ui.HubTileRectForTest(3);
            ClickPhysical((saveTile.MinX + saveTile.MaxX) * 0.5f * DpiScale,
                          (saveTile.MinY + saveTile.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("启动器：空板书点「保存墨迹」→ 关面板、引擎写「没有可保存」",
                  !ui.MoreOpenForTest && Host.State.InkStatus.Contains("没有可保存"),
                  $"status={Host.State.InkStatus}");

            // 「保存图片」（2026-10-02）：空板书同样 → 关面板 + 状态行说清
            var moreCellImg = ui.CellRectForTest(12);
            ClickPhysical((moreCellImg.MinX + moreCellImg.MaxX) * 0.5f * DpiScale,
                          (moreCellImg.MinY + moreCellImg.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            var imgTile = ui.HubTileRectForTest(6);
            ClickPhysical((imgTile.MinX + imgTile.MaxX) * 0.5f * DpiScale,
                          (imgTile.MinY + imgTile.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("启动器：空板书点「保存图片」→ 关面板、引擎写「没有可保存」",
                  !ui.MoreOpenForTest && Host.State.InkStatus.Contains("没有可保存"),
                  $"status={Host.State.InkStatus}");

            // 「设置」格 → 设置子页；返回箭头回启动器
            var moreCellD = ui.CellRectForTest(12);
            ClickPhysical((moreCellD.MinX + moreCellD.MaxX) * 0.5f * DpiScale,
                          (moreCellD.MinY + moreCellD.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            var setTile = ui.HubBottomRectForTest(0);
            Check("启动器：底栏「设置」格命中码 = 150（区间不与格子串段）",
                  ui.MoreHitForTest((setTile.MinX + setTile.MaxX) * 0.5f, (setTile.MinY + setTile.MaxY) * 0.5f)
                    == 150,
                  $"格 ({setTile.MinX:F0},{setTile.MinY:F0})-({setTile.MaxX:F0},{setTile.MaxY:F0})，"
                  + $"命中 {ui.MoreHitForTest((setTile.MinX + setTile.MaxX) * 0.5f, (setTile.MinY + setTile.MaxY) * 0.5f)}");
            ClickPhysical((setTile.MinX + setTile.MaxX) * 0.5f * DpiScale,
                          (setTile.MinY + setTile.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("启动器：「设置」→ 设置子页", ui.MoreOpenForTest && ui.MorePageForTest == 1,
                  $"page={ui.MorePageForTest}");
            var backBtn = ui.MoreBackRectForTest;
            ClickPhysical((backBtn.MinX + backBtn.MaxX) * 0.5f * DpiScale,
                          (backBtn.MinY + backBtn.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("设置子页：返回箭头 → 回启动器", ui.MorePageForTest == 0, $"page={ui.MorePageForTest}");

            // 再回设置子页，供下面几段"行"的用例用；面板换页高度会变，刷新 moreRect
            var setTile2 = ui.HubBottomRectForTest(0);
            ClickPhysical((setTile2.MinX + setTile2.MaxX) * 0.5f * DpiScale,
                          (setTile2.MinY + setTile2.MaxY) * 0.5f * DpiScale);
            SettleFrames(300);
            moreRect = ui.MoreRectForTest;
            Check("（准备）面板停在设置子页", ui.MoreOpenForTest && ui.MorePageForTest == 1,
                  $"page={ui.MorePageForTest}");
        }

        // 六行都在面板里、互不重叠（"加一行漏一处"的老毛病用这条兜住）
        {
            bool rowsOk = true;
            var seenRows = new List<RectF>();
            for (int i = 0; i < ui.MoreRowCountForTest; i++)
            {
                var r = ui.MoreRowRectForTest(i);
                bool inside = !r.IsEmpty && r.MinX >= moreRect.MinX && r.MaxX <= moreRect.MaxX
                           && r.MinY >= moreRect.MinY && r.MaxY <= moreRect.MaxY;
                if (!inside) { rowsOk = false; break; }
                foreach (var s in seenRows)
                    if (s.MinX < r.MaxX && r.MinX < s.MaxX && s.MinY < r.MaxY && r.MinY < s.MaxY)
                    { rowsOk = false; break; }
                seenRows.Add(r);
            }
            Check("每一行都在面板内、互不重叠", rowsOk, $"逐行取了 {seenRows.Count} 个矩形");
        }

        var darkRow = ui.RowRectForTest(0);
        ClickPhysical((darkRow.MinX + darkRow.MaxX) * 0.5f * DpiScale,
                      (darkRow.MinY + darkRow.MaxY) * 0.5f * DpiScale);
        Check("点「深色主题」切换", ui.DarkForTest, $"深色 = {ui.DarkForTest}");

        var hideRow = ui.RowRectForTest(1);
        ClickPhysical((hideRow.MinX + hideRow.MaxX) * 0.5f * DpiScale,
                      (hideRow.MinY + hideRow.MaxY) * 0.5f * DpiScale);
        Check("点「贴边隐藏」打开", ui.HideEnabledForTest, $"开关 = {ui.HideEnabledForTest}");

        // ---- ⑦.1 模态的收口：点面板外＝关、这一下**不落墨**；关掉后画布立刻能画 ----
        int strokesBefore = Doc.Strokes.Count;
        // 点位**自适应**：贴着面板上边缘之外 12px（逻辑像素）。
        // 写死"屏幕高度 10%"会被长高的面板吞掉——2026-10-09 加「墨迹预测」行时命中过一次：
        // 面板顶从 ~89px 升到 ~42-65px，84px 那个固定点落进了面板里，后面三连败全是它的连锁。
        var outPanel = ui.MoreRectForTest;
        float outClickY = MathF.Max(_virtualY + 8f, (outPanel.MinY - 12f) * (float)DpiScale);
        ClickPhysical(_virtualX + _virtualW * 0.5f, outClickY);   // 面板上方（面板居中）
        SettleFrames(400);
        Check("点面板外＝关闭面板", !ui.MoreOpenForTest, $"面板开着 = {ui.MoreOpenForTest}");
        Check("关闭那一下不落墨", Doc.Strokes.Count == strokesBefore,
              $"笔画 {strokesBefore} → {Doc.Strokes.Count}");
        var afterClose = ui.QueryBounds();
        Check("关闭后占用复原（不再是整块屏幕）",
              (afterClose.MaxY - afterClose.MinY) < (panelWork.MaxY - panelWork.MinY) * 0.9f,
              $"占用 {afterClose.MaxX - afterClose.MinX:F0}×{afterClose.MaxY - afterClose.MinY:F0}");

        ClickPhysical(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.5f);
        SettleFrames(200);
        Check("关闭后第一下照常落笔", Doc.Strokes.Count == strokesBefore + 1,
              $"笔画 {strokesBefore} → {Doc.Strokes.Count}");
        Host.Commands.Undo();
        SettleFrames(200);

        // 然后把面板**拖到屏幕最底下**：
        // 现行规则是"离屏幕底边 ≤10 才藏"（默认位置离屏幕底 52，不再触发隐藏）。
        var dragDown = ui.BarRectForTest;
        float ddx = (dragDown.MinX + dragDown.MaxX) * 0.5f * DpiScale;
        float ddy = (dragDown.MinY + dragDown.MaxY) * 0.5f * DpiScale;
        float ddyTo = _virtualY + _virtualH + 60 * (float)DpiScale;      // 拖过屏幕底，靠夹取兜住
        SendMouse((int)ddx, (int)ddy, 0);                              SettleFrames(60);
        SendMouse((int)ddx, (int)ddy, Native.MOUSEEVENTF_LEFTDOWN);     SettleFrames(50);
        for (int i = 1; i <= 8; i++) { SendMouse((int)ddx, (int)(ddy + (ddyTo - ddy) * i / 8f), 0); SettleFrames(20); }
        SendMouse((int)ddx, (int)ddyTo, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(200);
        // 指针移到画布上：贴边状态下应该收成一条 8 像素的"露头"，悬停露头再长回来。
        // 等过"离开 700 毫秒才收"＋收起动画那一段（167 毫秒）：留足余量，别把动画
        // 中间态当成终态来判——第一版就是这么误判成"露头 56 像素"的。
        SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.4f), 0);
        SettleFrames(2000);
        var peeked = ui.QueryBounds();
        Check("贴边隐藏收成露头（先拖到屏幕底边附近）",
              (peeked.MaxY - peeked.MinY) < 12f || (peeked.MaxX - peeked.MinX) < 12f,
              $"占用 {peeked.MaxX - peeked.MinX:F0}×{peeked.MaxY - peeked.MinY:F0}（应只剩露头那条）");

        SendMouse((int)((peeked.MinX + peeked.MaxX) * 0.5f * DpiScale),
                  (int)((peeked.MinY + peeked.MaxY) * 0.5f * DpiScale), 0);
        SettleFrames(500);
        var back = ui.QueryBounds();
        Check("碰一下露头就长回来", (back.MaxX - back.MinX) > 100f,
              $"占用 {back.MaxX - back.MinX:F0}×{back.MaxY - back.MinY:F0}");

        // ---- ⑦.15 球贴左/右边：那里**藏不动**，所以必须还是球（不是纯色把手）----
        //
        // 用户 2026-10-01 报的 bug："球吸附到左边或右边会变成一个纯颜色的小圆球、
        // 还吸不进去"——把手显示的判据以前只看"收没收"，没看"这个位置藏不藏得动"。
        // 判据：球摆到左边中段、peek 按到 0（模拟"已经收下去"），离屏渲染那块，
        // 数"卡片浅色像素"（纯色把手是整块笔色，一个浅色像素都没有）。
        {
            bool oldExpanded = ui.ExpandedForTest;
            ui.SetExpandForTest(0f);
            var scr2 = ui.ScreenForTest;
            ui.SetAnchorForTest(new System.Numerics.Vector2(
                scr2.MinX + 2f, (scr2.MinY + scr2.MaxY) * 0.5f));
            ui.ForcePeekForTest(0f);        // "已经藏下去"：左右边也不该变成把手
            SettleFrames(250);
            var ballBox = ui.QueryBounds();
            var shotBox = new RectF
            {
                MinX = ballBox.MinX - 4f, MinY = ballBox.MinY - 4f,
                MaxX = ballBox.MaxX + 4f, MaxY = ballBox.MaxY + 4f,
            };
            var shotPx = _windows.Count > 0
                ? _windows[0].RenderUiToBgraTransparent(this, 0, shotBox, out int _, out int _)
                : null;
            int lightPixels = 0;            // 近白 = 球卡片
            if (shotPx != null)
                for (int i = 0; i + 3 < shotPx.Length; i += 4)
                {
                    int a8 = shotPx[i + 3], r8 = shotPx[i + 2], g8 = shotPx[i + 1], b8 = shotPx[i];
                    if (a8 > 200 && r8 > 200 && g8 > 200 && b8 > 200) lightPixels++;
                }
            Check("球贴左边：还是球（卡片在），不是纯色把手",
                  lightPixels > 50, $"浅色像素 {lightPixels}");
            ui.SetAnchorForTest(null);
            ui.SetExpandForTest(oldExpanded ? 1f : 0f);
            ui.ForcePeekForTest(1f);
            SettleFrames(250);
        }

        // 关掉贴边隐藏，免得影响后面的用例（点「…」开面板 → 点行 → 点面板外关掉）
        var moreCell2 = ui.CellRectForTest(12);
        ClickPhysical((moreCell2.MinX + moreCell2.MaxX) * 0.5f * DpiScale,
                      (moreCell2.MinY + moreCell2.MaxY) * 0.5f * DpiScale);   // 开面板
        SettleFrames(250);
        var setTileNav = ui.HubBottomRectForTest(0);
        ClickPhysical((setTileNav.MinX + setTileNav.MaxX) * 0.5f * DpiScale,
                      (setTileNav.MinY + setTileNav.MaxY) * 0.5f * DpiScale);   // 启动器 → 设置子页
        SettleFrames(250);
        var hideRow2 = ui.RowRectForTest(1);
        ClickPhysical((hideRow2.MinX + hideRow2.MaxX) * 0.5f * DpiScale,
                      (hideRow2.MinY + hideRow2.MaxY) * 0.5f * DpiScale);
        ClickPhysical((moreCell2.MinX + moreCell2.MaxX) * 0.5f * DpiScale,
                      (moreCell2.MinY + moreCell2.MaxY) * 0.5f * DpiScale);   // 点外面 = 关
        SettleFrames(250);
        Check("关掉贴边隐藏", !ui.HideEnabledForTest, $"开关 = {ui.HideEnabledForTest}");

        // ---- ⑦.5 界面档位与钉住 ----
        var moreCell4 = ui.CellRectForTest(12);
        ClickPhysical((moreCell4.MinX + moreCell4.MaxX) * 0.5f * DpiScale,
                      (moreCell4.MinY + moreCell4.MaxY) * 0.5f * DpiScale);   // 开面板
        SettleFrames(200);
        var setTile45 = ui.HubBottomRectForTest(0);
        ClickPhysical((setTile45.MinX + setTile45.MaxX) * 0.5f * DpiScale,
                      (setTile45.MinY + setTile45.MaxY) * 0.5f * DpiScale);   // 设置子页（档位/宫格在这页）
        SettleFrames(250);

        Check("完整档是 13 格", ui.VisibleCountForTest == 13, $"显示 {ui.VisibleCountForTest} 格");

        // 极简档的色片行：**只给 4 个**（用户 2026-09-17："极简模式的色带展开栏里面的
        // 内容排布有点问题"——短胶囊里塞一整排色片，减掉滑条之后每个只有十几像素宽）。
        {
            var miniSeg0 = ui.ProfileRectForTest(0);
            ClickPhysical((miniSeg0.MinX + miniSeg0.MaxX) * 0.5f * DpiScale,
                          (miniSeg0.MinY + miniSeg0.MaxY) * 0.5f * DpiScale);   // 切极简
            SettleFrames(250);
            ui.SelectBandCellForTest(3);                                     // 笔的设置条
            SettleFrames(150);
            Check("极简档：色片只有 4 个", ui.SwatchCountForTest == 4,
                  $"色片数 {ui.SwatchCountForTest}");
            var sw0 = ui.SwatchRectForTest(0);
            Check("极简档：每个色片都够宽（≥ 30 逻辑像素，点得准）",
                  sw0.MaxX - sw0.MinX >= 30f, $"色片宽 {sw0.MaxX - sw0.MinX:F0}");
            // 极简档那条带子塞不下第三样东西（见 FullUi.BandHasDashToggle），
            // 所以那一格**不给**——色片优先。这条要验出来，不然以后谁把它加回去，
            // 色片会悄悄变窄、而"≥30"那条也会跟着红，两处却对不上因果。
            Check("极简档：没有虚实线那一格（挤不下，让位给色片）",
                  !ui.DashToggleVisibleForTest, $"可见 = {ui.DashToggleVisibleForTest}");
            // 切回完整档
            var fullSeg0 = ui.ProfileRectForTest(2);
            ClickPhysical((fullSeg0.MinX + fullSeg0.MaxX) * 0.5f * DpiScale,
                          (fullSeg0.MinY + fullSeg0.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            // **不与写死的数字比**：完整档的色片数 = 笔色带的长度（8.1.4 从 12 砍到 8，
            // 这条自检当时跟着红了一次——以后改色带只改引擎那一张表，这里自动跟上）。
            Check("完整档：色片回到一整排（= 笔色带长度）",
                  ui.SwatchCountForTest == InkPalette.PenBand.Length,
                  $"色片数 {ui.SwatchCountForTest}（笔色带 {InkPalette.PenBand.Length} 个）");
        }

        // 切到极简：只留六格 ＋ 收起格，整条带子明显变短
        var miniSeg = ui.ProfileRectForTest(0);
        ClickPhysical((miniSeg.MinX + miniSeg.MaxX) * 0.5f * DpiScale,
                      (miniSeg.MinY + miniSeg.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        var miniBar = ui.BarRectForTest;
        Check("极简档是七格（六格 ＋ 收起格）", ui.VisibleCountForTest == 7,
              $"显示 {ui.VisibleCountForTest} 格，档位 = {ui.ProfileForTest}");
        Check("极简档是一条短胶囊", (miniBar.MaxX - miniBar.MinX) < 360f,
              $"宽 {miniBar.MaxX - miniBar.MinX:F0}（完整档是 636）");

        // 切档时"当前工具不在这一档里"要落到笔：先把工具换成图形（极简档里没有它）
        Host.Commands.SetTool(Tool.Rectangle);
        SettleFrames(150);
        var miniSeg2 = ui.ProfileRectForTest(0);          // 已经在极简了，再点一次也走同一条路
        ClickPhysical((miniSeg2.MinX + miniSeg2.MaxX) * 0.5f * DpiScale,
                      (miniSeg2.MinY + miniSeg2.MaxY) * 0.5f * DpiScale);
        Check("切档时工具不在档内→落到笔", Tool == Tool.Pen, $"工具 = {Tool}");

        // 切回完整档
        var fullSeg = ui.ProfileRectForTest(2);
        ClickPhysical((fullSeg.MinX + fullSeg.MaxX) * 0.5f * DpiScale,
                      (fullSeg.MinY + fullSeg.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        Check("切回完整档", ui.VisibleCountForTest == 13, $"显示 {ui.VisibleCountForTest} 格");

        // 取消钉住"图形"（下标 8）：档位自动变成自定义，主条上少一格。
        // ⚠ 宫格**只在自定义档显示**（2026-10-02 拍板），所以先切到自定义档再点宫格。
        var customSeg = ui.ProfileRectForTest(1);
        ClickPhysical((customSeg.MinX + customSeg.MaxX) * 0.5f * DpiScale,
                      (customSeg.MinY + customSeg.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        var chip = ui.ChipRectForTest(8);
        ClickPhysical((chip.MinX + chip.MaxX) * 0.5f * DpiScale,
                      (chip.MinY + chip.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        Check("取消钉住→进自定义档、主条少一格",
              ui.ProfileForTest == 1 && !ui.PinnedForTest(8) && ui.VisibleCountForTest == 12,
              $"档位 = {ui.ProfileForTest}，钉着 = {ui.PinnedForTest(8)}，显示 {ui.VisibleCountForTest} 格");

        // 安全项：笔（下标 3）点一下不该被取消
        var penChip = ui.ChipRectForTest(3);
        ClickPhysical((penChip.MinX + penChip.MaxX) * 0.5f * DpiScale,
                      (penChip.MinY + penChip.MaxY) * 0.5f * DpiScale);
        SettleFrames(200);
        Check("安全项（笔）取消不掉", ui.PinnedForTest(3), $"笔钉着 = {ui.PinnedForTest(3)}");

        // 钉回去，回到完整档，别把后面的用例带偏
        // 注意：每次点之前**重新取一次矩形**——切档会让主条宽度变、档位段自己也跟着重排，
        // 复用之前算好的坐标就会点空（这一版自检当年就是这么把自己坑了一次；
        // 现在面板是居中的，档位段只跟面板宽度走，但"点之前重取"这条纪律留着）。
        var chipBack = ui.ChipRectForTest(8);
        ClickPhysical((chipBack.MinX + chipBack.MaxX) * 0.5f * DpiScale,
                      (chipBack.MinY + chipBack.MaxY) * 0.5f * DpiScale);
        SettleFrames(200);
        var fullSeg2 = ui.ProfileRectForTest(2);
        ClickPhysical((fullSeg2.MinX + fullSeg2.MaxX) * 0.5f * DpiScale,
                      (fullSeg2.MinY + fullSeg2.MaxY) * 0.5f * DpiScale);
        SettleFrames(200);
        var moreCell5 = ui.CellRectForTest(12);
        ClickPhysical((moreCell5.MinX + moreCell5.MaxX) * 0.5f * DpiScale,
                      (moreCell5.MinY + moreCell5.MaxY) * 0.5f * DpiScale);   // 点外面 = 关面板
        SettleFrames(250);
        Check("收尾：回到完整档、面板已关",
              ui.ProfileForTest == 2 && ui.VisibleCountForTest == 13 && !ui.MoreOpenForTest,
              $"档位 = {ui.ProfileForTest}，显示 {ui.VisibleCountForTest} 格，面板 = {ui.MoreOpenForTest}");

        // ---- ⑥ 收起来，然后空闲必须 0 帧 ----
        var ball2 = ui.CellRectForTest(0);
        ClickPhysical((ball2.MinX + ball2.MaxX) * 0.5f * DpiScale,
                      (ball2.MinY + ball2.MaxY) * 0.5f * DpiScale);
        SettleFrames(250);
        Check("点最左那格能收起", !ui.ExpandedForTest, $"展开状态 = {ui.ExpandedForTest}");

        PumpMessages();
        RenderAll();
        _dirty = false;
        int quiet = 0;
        var swQuiet = Stopwatch.StartNew();
        while (swQuiet.ElapsedMilliseconds < 150)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; quiet++; }
            else Thread.Sleep(2);
        }
        Check("空闲 0 帧", quiet == 0, $"安静 150 毫秒出了 {quiet} 帧");

        // ---- ⑥.6 悬停提示（Tooltip；2026-10-02）----
        //
        // 三条链路：真鼠标停在「笔」上 500ms → 出提示（键位**从键位表查**，界面不写死）；
        // 移开 → 立刻收；覆盖不变量：每一格、上带每一段/色片/动作都有文案
        //（"加一段漏一处"是这个仓库的老毛病，用逐个数兜住）。
        {
            ui.SetExpandForTest(1f);
            ui.SetTipEnabledForTest(true);
            // 先把指针放画布上，清掉上一段留下的悬停
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.3f), 0);
            SettleFrames(150);

            var penCell3 = ui.CellRectForTest(3);
            int tpx = (int)((penCell3.MinX + penCell3.MaxX) * 0.5f * DpiScale);
            int tpy = (int)((penCell3.MinY + penCell3.MaxY) * 0.5f * DpiScale);
            // 合成鼠标偶尔丢移动（前面那条经验）：挪 1 像素多给几次机会
            for (int attempt = 0; attempt < 3 && !ui.TipVisibleForTest; attempt++)
            {
                SendMouse(tpx, tpy - attempt, 0);
                SettleFrames(700);            // > 500ms 延迟 + 淡入
            }
            Check("停在「笔」上 0.5 秒：悬停提示出现", ui.TipVisibleForTest,
                  $"可见 = {ui.TipVisibleForTest}");
            var penTip = ui.TipContentForTest(3);
            string wantPenKey = Keys.KeyText(KeyAction.ToolPen);
            Check("提示里的键位来自键位表（界面不写死）",
                  !string.IsNullOrEmpty(penTip.Key) && penTip.Key == wantPenKey,
                  $"提示 = {penTip.Key ?? "（空）"}，键位表 = {wantPenKey ?? "（空）"}");
            var tipRect = ui.TipRectForTest;
            var barNow = ui.BarRectForTest;
            Check("提示卡在面板上方、和「笔」那一格横向对得上",
                  !tipRect.IsEmpty && tipRect.MaxY <= barNow.MinY + 0.5f
                  && tipRect.MinX < penCell3.MaxX && penCell3.MinX < tipRect.MaxX,
                  $"卡 {tipRect.MinX:F0}..{tipRect.MaxX:F0} × {tipRect.MinY:F0}..{tipRect.MaxY:F0}，"
                  + $"笔格 {penCell3.MinX:F0}..{penCell3.MaxX:F0}，主条顶 {barNow.MinY:F0}");

            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.3f), 0);
            SettleFrames(150);
            Check("鼠标移开：提示立刻收", !ui.TipVisibleForTest, $"可见 = {ui.TipVisibleForTest}");

            // --- 收窄后的清单不变量：该有的有、该没有的没有（2026-10-02 第二轮）---
            // 判据（《调研-悬停提示-Tooltip.md》10.8）：只有"图标-only / 带快捷键 /
            // 隐藏手势 / 认不出来"才配；色片、文字段、点一下就见结果的一律不配。
            bool cover = true; string missing = "";
            void Need(bool ok, string what) { if (!ok) { cover = false; missing += what + " "; } }

            for (int c = 1; c <= 12; c++)                       // 1..12 该有（0 号收起格不配）
                Need(ui.TipContentForTest(c).Title != null, $"缺格{c}");
            Need(ui.TipContentForTest(0).Title == null, "收起格不该有");
            Need(ui.TipContentForTest(-2).Title == null, "球不该有");
            Need(ui.TipContentForTest(300).Title == null, "滑条不该有（有粗细预览）");

            ui.SelectBandCellForTest(2);
            int up = ui.BoardSegIndexOfForTest("PageUp");
            int down = ui.BoardSegIndexOfForTest("PageDown");
            Need(up >= 0 && down >= 0, "白板翻页段找得到");
            for (int i = 0; i < ui.BandSegmentCountForTest; i++)
            {
                bool should = i == up || i == down;             // 页码 / 板色 / 底纹 / 间距不配
                Need(should == (ui.TipContentForTest(200 + i).Title != null), $"白板段{i}不符");
            }
            Need(ui.TipContentForTest(400).Title != null, "关闭白板应配");
            ui.SelectBandCellForTest(3);
            Need(ui.TipContentForTest(500).Title != null, "线型应配");
            for (int i = 0; i < ui.SwatchCountForTest; i++)
                Need(ui.TipContentForTest(100 + i).Title == null, $"色片{i}不该有");
            Host.Commands.SetTool(Tool.Highlighter);
            ui.SelectBandCellForTest(4);
            for (int i = 0; i < ui.SwatchCountForTest; i++)
                Need(ui.TipContentForTest(100 + i).Title == null, $"荧光色{i}不该有");
            Host.Commands.SetTool(Tool.Eraser);
            ui.SelectBandCellForTest(6);
            Need(ui.TipContentForTest(400).Title != null, "清空（按住）应配");
            for (int i = 0; i < 2; i++)
                Need(ui.TipContentForTest(200 + i).Title == null, $"橡皮段{i}不该有");
            Host.Commands.SetTool(Tool.Marquee);
            ui.SelectBandCellForTest(7);
            Need(ui.TipContentForTest(400).Title == null, "全选不该有");
            for (int i = 0; i < 2; i++)
                Need(ui.TipContentForTest(200 + i).Title == null, $"框选段{i}不该有");
            Host.Commands.SetTool(Tool.Rectangle);
            ui.SelectBandCellForTest(8);
            for (int i = 0; i < ui.BandSegmentCountForTest; i++)
                Need(ui.TipContentForTest(200 + i).Title != null, $"缺图形段{i}");
            Host.Commands.SetTool(Tool.Capture);
            ui.SelectBandCellForTest(9);
            Need(ui.TipContentForTest(400).Title == null, "粘贴图片不该有");
            for (int i = 0; i < 2; i++)
                Need(ui.TipContentForTest(200 + i).Title == null, $"截图段{i}不该有");
            Host.Commands.SetTool(Tool.Pen);
            Check("提示范围：该有的都有、该没有的都没有（收窄后的清单）", cover,
                  cover ? "13 格 + 各格上带逐个数过" : $"不符：{missing}");

            // --- 触摸长按（2026-10-02 第二轮）：主流四步手势 ---
            //   短按=执行 / 按住不动 0.6 秒=出提示且松手不执行 / 按住后滑走=不弹提示。
            //   鼠标不参与长按（上面那些悬停用例走的就是鼠标的路）。
            if (EnsureSyntheticTouch())
            {
                var cellP = ui.CellRectForTest(3);
                float tx = (cellP.MinX + cellP.MaxX) * 0.5f * DpiScale;
                float ty = (cellP.MinY + cellP.MaxY) * 0.5f * DpiScale;

                // ① 短按：照常换工具
                Host.Commands.SetTool(Tool.Eraser);
                SettleFrames(120);
                SendTouches(true, (tx, ty));   SettleFrames(120);
                SendTouches(false, (tx, ty));  SettleFrames(250);
                Check("触摸短按「笔」：照常换工具", Tool == Tool.Pen, $"工具 = {Tool}（期望 Pen）");

                // ② 长按：出提示、松手不执行、提示停留后自动收
                Host.Commands.SetTool(Tool.Eraser);
                SettleFrames(120);
                // 注入的触点是"快照帧"：静止保持要每 100ms 补一帧
                //（TouchGuardTest 里也是边走边补。真机没这回事，手指按着就一直按着）。
                for (int i = 0; i < 8 && !ui.TipHoldFiredForTest; i++)
                {
                    SendTouches(true, (tx, ty));
                    SettleFrames(100);
                }
                Check("触摸按住不动 0.8 秒：弹出提示",
                      ui.TipVisibleForTest && ui.TipHoldFiredForTest,
                      $"可见 = {ui.TipVisibleForTest}，长按已触发 = {ui.TipHoldFiredForTest}");
                SendTouches(false, (tx, ty));  SettleFrames(200);
                Check("长按后松手：**不执行**（工具还是橡皮）", Tool == Tool.Eraser,
                      $"工具 = {Tool}（期望 Eraser）");
                Check("提示松手后继续停留（2.5 秒）", ui.TipVisibleForTest,
                      $"可见 = {ui.TipVisibleForTest}");
                SettleFrames(2700);
                Check("停留结束：提示自己收掉", !ui.TipVisibleForTest,
                      $"可见 = {ui.TipVisibleForTest}");

                // ③ 按住后滑走：不弹提示、不执行（走拖动消歧）
                Host.Commands.SetTool(Tool.Eraser);
                SettleFrames(120);
                SendTouches(true, (tx, ty));   SettleFrames(150);
                SendTouches(true, (tx + 30f * DpiScale, ty));   // 超过拖动阈值
                SettleFrames(150);
                SendTouches(false, (tx + 30f * DpiScale, ty));
                SettleFrames(700);
                Check("触摸按住后滑走：不弹提示", !ui.TipVisibleForTest,
                      $"可见 = {ui.TipVisibleForTest}，触发 = {ui.TipHoldFiredForTest}");

                Host.Commands.SetTool(Tool.Pen);   // 收尾
                SettleFrames(120);
            }

            ui.SetExpandForTest(0f);       // 恢复收起态，别把后面的拖动用例带偏
            SettleFrames(150);
        }

        // ---- ⑦ 拖动**不吸附**（用户 2026-09-30："拖到任务栏下面自动靠底边这种不用了"）----
        //
        // 判据：把球拖到离左边 20 逻辑像素（原来的吸附范围 40 以内）松手——
        // **停在原地**，不许被吸到 DockGap(2) 去；夹取（不许拖出屏幕）照旧还在。
        var home = ui.QueryBounds();
        float hx = (home.MinX + home.MaxX) * 0.5f * DpiScale;
        float hy = (home.MinY + home.MaxY) * 0.5f * DpiScale;
        // 球宽 48：中心放在"离左边 20 + 24"处，球左就落在离边 20 的位置。
        int targetX = _virtualX + (int)((20f + 24f) * DpiScale);
        SendMouse((int)hx, (int)hy, 0);                          SettleFrames(60);
        SendMouse((int)hx, (int)hy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
        for (int i = 1; i <= 10; i++)
        {
            SendMouse((int)(hx + (targetX - hx) * i / 10f), (int)hy, 0);
            SettleFrames(20);
        }
        SendMouse(targetX, (int)hy, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        var dropped = ui.QueryBounds();
        Check("拖到左边缘附近松手：**不吸附**（停在拖到的位置）",
              MathF.Abs(dropped.MinX - 20f) < 3f,
              $"落下后左边缘 {dropped.MinX:F0}（应为 20 = 拖到的位置；"
              + $"旧行为会吸到 {InkUi.Tokens.DockGap:F0}），拖动前在 {home.MinX:F0}");

        // ---- ⑦.2 锚点：**球不动、带子往右长**；顶到右边就整条夹回屏幕内 ----
        //
        // 用户 2026-09-17 定的规则（也是假面板当年的做法）：
        //   球是锚点（它长在**展开后那条带子的左端**），展开＝从球往右铺；
        //   铺出去会出屏时，把整条带子夹回屏幕内——看着就是"贴住右边往左铺开"。
        //
        // 这里要验两件事：① 拖到中间之后展开，**球一动不动**（收起时不用重新瞄）；
        // ② 拖到右边缘再展开，**整条带子还在屏幕里**（不会有一截跑到屏幕外）。
        {
            // ① 拖到屏幕中左部（离两边都远）→ 展开 → 球的位置必须没变
            var d0 = ui.QueryBounds();                        // 此刻贴着左边、收起态
            float sx = (d0.MinX + d0.MaxX) * 0.5f * DpiScale;
            float sy = (d0.MinY + d0.MaxY) * 0.5f * DpiScale;
            int toX = _virtualX + (int)(500 * DpiScale);
            SendMouse((int)sx, (int)sy, 0);                           SettleFrames(60);
            SendMouse((int)sx, (int)sy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(50);
            for (int i = 1; i <= 8; i++) { SendMouse((int)(sx + (toX - sx) * i / 8f), (int)sy, 0); SettleFrames(20); }
            SendMouse(toX, (int)sy, Native.MOUSEEVENTF_LEFTUP);        SettleFrames(250);
            var parked = ui.QueryBounds();
            float parkedLeft = parked.MinX;

            ClickPhysical((parked.MinX + parked.MaxX) * 0.5f * DpiScale,
                          (parked.MinY + parked.MaxY) * 0.5f * DpiScale);   // 点球展开
            SettleFrames(400);
            var ballAfter = ui.CellRectForTest(0);                 // 展开后那一格就是球
            var anchoredBar = ui.BarRectForTest;
            Check("拖到中间后展开：球不动、只往右长",
                  // 比的是**锚点**（主条左端）：收起时它就是球的位置；
                  // 展开后球那一格还要往里让 BarPad（8 像素），拿格子比会差这 8 像素。
                  MathF.Abs(anchoredBar.MinX - parkedLeft) < 1.5f
                  && anchoredBar.MaxX > ballAfter.MaxX + 400f,
                  $"锚点（主条左端）{parkedLeft:F0} → {anchoredBar.MinX:F0}，"
                  + $"带子 {anchoredBar.MinX:F0}..{anchoredBar.MaxX:F0}");

            // 收回去（后面几段用例都假设"面板是收起的"）
            var ballCell = ui.CellRectForTest(0);
            ClickPhysical((ballCell.MinX + ballCell.MaxX) * 0.5f * DpiScale,
                          (ballCell.MinY + ballCell.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            Check("再点一次收起（回到球）", !ui.ExpandedForTest, $"展开状态 = {ui.ExpandedForTest}");

            // ② 拖到**右边缘**（贴住右边靠的是夹取，不是吸附）→ 展开 → 整条带子必须还在屏幕里
            var e0 = ui.QueryBounds();
            float ex = (e0.MinX + e0.MaxX) * 0.5f * DpiScale;
            float ey = (e0.MinY + e0.MaxY) * 0.5f * DpiScale;
            int farX = (int)((_virtualX + _virtualW) - 30 * DpiScale);
            SendMouse((int)ex, (int)ey, 0);                           SettleFrames(60);
            SendMouse((int)ex, (int)ey, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(50);
            for (int i = 1; i <= 10; i++) { SendMouse((int)(ex + (farX - ex) * i / 10f), (int)ey, 0); SettleFrames(20); }
            SendMouse(farX, (int)ey, Native.MOUSEEVENTF_LEFTUP);       SettleFrames(250);

            var atRight = ui.QueryBounds();
            ClickPhysical((atRight.MinX + atRight.MaxX) * 0.5f * DpiScale,
                          (atRight.MinY + atRight.MaxY) * 0.5f * DpiScale);  // 展开
            SettleFrames(500);
            var rightBar = ui.BarRectForTest;
            float screenL = _virtualX / DpiScale, screenR = (_virtualX + _virtualW) / DpiScale;
            Check("球贴右边时展开：整条带子夹回屏幕内（贴着右边、往左铺）",
                  rightBar.MinX >= screenL - 1f && rightBar.MaxX <= screenR + 1f
                  && MathF.Abs(rightBar.MaxX - (screenR - InkUi.Tokens.DockGap)) < 3f,
                  $"带子 {rightBar.MinX:F0}..{rightBar.MaxX:F0}（屏 {screenL:F0}..{screenR:F0}）");

            // 收拾干净：收回球、拖回屏幕中间，别把后面几段用例带偏
            var ballCell2 = ui.CellRectForTest(0);
            ClickPhysical((ballCell2.MinX + ballCell2.MaxX) * 0.5f * DpiScale,
                          (ballCell2.MinY + ballCell2.MaxY) * 0.5f * DpiScale);
            SettleFrames(250);
            var b2 = ui.QueryBounds();
            float b2x = (b2.MinX + b2.MaxX) * 0.5f * DpiScale, b2y = (b2.MinY + b2.MaxY) * 0.5f * DpiScale;
            SendMouse((int)b2x, (int)b2y, 0);                          SettleFrames(60);
            SendMouse((int)b2x, (int)b2y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);
            int homeX = _virtualX + (int)(720 * DpiScale);
            for (int i = 1; i <= 8; i++) { SendMouse((int)(b2x + (homeX - b2x) * i / 8f), (int)b2y, 0); SettleFrames(20); }
            SendMouse(homeX, (int)b2y, Native.MOUSEEVENTF_LEFTUP);      SettleFrames(250);
        }

        // ---- ⑧ 「更多」面板里的「重启软件」：先暂存板书，再拉起新进程（这里只记一笔，不真拉）----
        Recovery.ClearRestartCount();
        try { File.Delete(Recovery.SessionPath); } catch { }

        // ---- ⑨ 偏好落盘：改过的写进 settings.json，重开界面读得回来 ----
        // 把状态弄成一组"非默认"：深色开着（前面的用例已经开了）、取消钉住"图形"
        // （档位随之进"自定义"）。自检模式用的是临时配置文件，不碰用户真正的设置。
        //
        // 注意：上一条用例结束时面板是**收起**的，得先展开再去点「更多」面板里的东西——
        // 收起状态下那些格子的坐标算出来是"按球的位置铺开"，点过去全是空的
        // （这一版用例第一次跑就是这么点空的）。
        var ballFirst = ui.QueryBounds();
        ClickPhysical((ballFirst.MinX + ballFirst.MaxX) * 0.5f * DpiScale,
                      (ballFirst.MinY + ballFirst.MaxY) * 0.5f * DpiScale);
        SettleFrames(300);
        var moreCell6 = ui.CellRectForTest(12);
        ClickPhysical((moreCell6.MinX + moreCell6.MaxX) * 0.5f * DpiScale,
                      (moreCell6.MinY + moreCell6.MaxY) * 0.5f * DpiScale);       // 开面板
        SettleFrames(200);
        var setTile9 = ui.HubBottomRectForTest(0);
        ClickPhysical((setTile9.MinX + setTile9.MaxX) * 0.5f * DpiScale,
                      (setTile9.MinY + setTile9.MaxY) * 0.5f * DpiScale);         // 设置子页
        SettleFrames(250);
        var customSeg9 = ui.ProfileRectForTest(1);
        ClickPhysical((customSeg9.MinX + customSeg9.MaxX) * 0.5f * DpiScale,
                      (customSeg9.MinY + customSeg9.MaxY) * 0.5f * DpiScale);     // 自定义档（宫格才显示）
        SettleFrames(250);
        var chip6 = ui.ChipRectForTest(8);
        ClickPhysical((chip6.MinX + chip6.MaxX) * 0.5f * DpiScale,
                      (chip6.MinY + chip6.MaxY) * 0.5f * DpiScale);               // 取消钉住"图形"
        SettleFrames(250);

        // 「压感粗细」也在这里过一遍完整链路：点行 → 引擎状态立刻翻转 → 落盘（批次 0.2）
        {
            var pressureRow = ui.RowRectByLabelForTest("压感粗细");
            Check("「压感粗细」那一行找得到", pressureRow.MaxY > pressureRow.MinY,
                  $"行高 {pressureRow.MaxY - pressureRow.MinY:F0}");
            ClickPhysical((pressureRow.MinX + pressureRow.MaxX) * 0.5f * DpiScale,
                          (pressureRow.MinY + pressureRow.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("点「压感粗细」：引擎状态立刻翻转（默认开 → 关）",
                  !Host.State.PressureOn, $"PressureOn = {Host.State.PressureOn}");
        }

        // 「精细笔迹」（2026-10-07）：同一条链路过一遍 —— 点行 → 引擎状态翻转 → 落盘。
        // 它管的是"原始输入补点"那个开关（低配机的性能保险丝）。
        {
            var fineRow = ui.RowRectByLabelForTest("精细笔迹");
            Check("「精细笔迹」那一行找得到", fineRow.MaxY > fineRow.MinY,
                  $"行高 {fineRow.MaxY - fineRow.MinY:F0}");
            // **这一条是关键**（2026-10-07 真机抓到的）：光有行不够，**开关得真的画出来**。
            // 当时我把行/位置/状态/点击/落盘全接好了，只漏了把它加进 IsToggleRow，
            // 于是标签画了、开关没画；而"点一下状态翻转"那条判据看的是整行矩形，
            // **开关没画也照样通过** —— 测试没盖住真正错的地方。
            Check("「精细笔迹」是**开关行**（有开关，不是空白行）",
                  ui.IsToggleRowByLabelForTest("精细笔迹"),
                  "IsToggleRow = " + ui.IsToggleRowByLabelForTest("精细笔迹"));
            Check("精细笔迹默认是开的", Host.State.FineStrokeOn, $"FineStrokeOn = {Host.State.FineStrokeOn}");
            ClickPhysical((fineRow.MinX + fineRow.MaxX) * 0.5f * DpiScale,
                          (fineRow.MinY + fineRow.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("点「精细笔迹」：引擎状态立刻翻转（默认开 → 关）",
                  !Host.State.FineStrokeOn, $"FineStrokeOn = {Host.State.FineStrokeOn}");
        }

        // 「墨迹预测」（2026-10-09 回归：B4 自绘预测尾，**默认关**——用户二轮定：
        // "以后新装默认不开，等测试完善了再开"）：点行 → 引擎状态翻转 → 落盘。
        // （原 2026-10-05 停用块里顺带的「启动器/底栏提示文案」那段仍留停用——它跟本行无关，
        //   需要时从 git 历史恢复；这里只验"墨迹预测"行自己的链路。）
        {
            var predictRow = ui.RowRectByLabelForTest("墨迹预测");
            Check("「墨迹预测」那一行找得到", predictRow.MaxY > predictRow.MinY,
                  $"行高 {predictRow.MaxY - predictRow.MinY:F0}");
            Check("「墨迹预测」是**开关行**（有开关，不是空白行）",
                  ui.IsToggleRowByLabelForTest("墨迹预测"),
                  "IsToggleRow = " + ui.IsToggleRowByLabelForTest("墨迹预测"));
            Check("墨迹预测默认是关的（B4 待完善，2026-10-09 用户定）", !Host.State.PredictTailOn,
                  $"PredictTailOn = {Host.State.PredictTailOn}");

            // 鼠标悬停设置行 → 出提示（小字搬进提示）
            int rhx = (int)((predictRow.MinX + predictRow.MaxX) * 0.5f * DpiScale);
            int rhy = (int)((predictRow.MinY + predictRow.MaxY) * 0.5f * DpiScale);
            SendMouse(rhx, rhy, 0); SettleFrames(700);
            int rhit = ui.MoreHitForTest((predictRow.MinX + predictRow.MaxX) * 0.5f,
                                         (predictRow.MinY + predictRow.MaxY) * 0.5f);
            var rtip = ui.TipContentForTest(1000 + rhit);
            Check("悬停设置行：出提示（小字搬进提示）",
                  ui.TipVisibleForTest && rtip.Title == "墨迹预测" && rtip.Note.Contains("跟手"),
                  $"可见={ui.TipVisibleForTest}，标题={rtip.Title}，说明={rtip.Note}");
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.3f), 0);
            SettleFrames(150);
            Check("移开：提示收起", !ui.TipVisibleForTest, $"可见={ui.TipVisibleForTest}");

            // 触摸长按设置行 → 出提示、**这一次松手不执行**（开关保持"关"）
            if (EnsureSyntheticTouch())
            {
                for (int i = 0; i < 8 && !ui.TipHoldFiredForTest; i++)
                {
                    SendTouches(true, (rhx, rhy));
                    SettleFrames(100);
                }
                Check("长按设置行 0.6 秒：出提示", ui.TipVisibleForTest && ui.TipHoldFiredForTest,
                      $"可见={ui.TipVisibleForTest}，触发={ui.TipHoldFiredForTest}");
                SendTouches(false, (rhx, rhy)); SettleFrames(200);
                Check("长按后松手：这一次**不翻转**开关", !Host.State.PredictTailOn,
                      $"PredictTailOn = {Host.State.PredictTailOn}");
            }

            ClickPhysical((predictRow.MinX + predictRow.MaxX) * 0.5f * DpiScale,
                          (predictRow.MinY + predictRow.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("点「墨迹预测」：引擎状态立刻翻转（默认关 → 开）",
                  Host.State.PredictTailOn, $"PredictTailOn = {Host.State.PredictTailOn}");
        }

        // 「功能提示」开关也过一遍完整链路（2026-10-02 新增；原叫「悬停提示」）：
        // 点行 → 界面状态翻转 → 落盘
        {
            var tipRow = ui.RowRectByLabelForTest("功能提示");
            Check("「功能提示」那一行找得到", tipRow.MaxY > tipRow.MinY,
                  $"行高 {tipRow.MaxY - tipRow.MinY:F0}");
            ClickPhysical((tipRow.MinX + tipRow.MaxX) * 0.5f * DpiScale,
                          (tipRow.MinY + tipRow.MaxY) * 0.5f * DpiScale);
            SettleFrames(200);
            Check("点「功能提示」：开关翻到关", !ui.TipEnabledForTest,
                  $"开关 = {ui.TipEnabledForTest}");
        }
        SaveSettingsForTest();

        string prefsPath = InkSettings.PathOverride ?? "";
        string prefsText = File.Exists(prefsPath) ? File.ReadAllText(prefsPath) : "";
        Check("改动写进了配置文件",
              prefsText.Contains("\"ui\"") && prefsText.Contains("\"dark\"")
              && prefsText.Contains("\"profile\"") && prefsText.Contains("\"unpinned\"")
              && prefsText.Contains("\"pressure\"") && prefsText.Contains("\"finestroke\"")
              && prefsText.Contains("\"predict2\"")
              && prefsText.Contains("\"tooltip\"")
              ,
              $"{Path.GetFileName(prefsPath)}（{prefsText.Length} 字节）");

        // 把内存里那份清掉、从文件重读，再挂一个新界面——这才算"重开软件"那条链子
        ReloadUiPrefsForTest();
        ApplyPressurePrefForTest();       // 压感是引擎状态，要补"启动时应用偏好"那一步
        ApplyFineStrokePrefForTest();     // 精细笔迹同理（原始输入补点）
        ApplyPredictPrefForTest();        // 墨迹预测同理（B4 自绘预测尾）
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        ui = CurrentUi as InkUi.FullUi;
        Check("重开界面读回了偏好",
              ui != null && ui.DarkForTest && ui.ProfileForTest == 1 && !ui.PinnedForTest(8)
              && !ui.TipEnabledForTest,
              $"深色={ui?.DarkForTest}，档位={ui?.ProfileForTest}（1=自定义），图形钉着={ui?.PinnedForTest(8)}，"
              + $"功能提示={ui?.TipEnabledForTest}");
        Check("压感偏好也读回来了（重启后仍是关）",
              !Host.State.PressureOn, $"PressureOn = {Host.State.PressureOn}");
        Check("精细笔迹偏好也读回来了（重启后仍是关）",
              !Host.State.FineStrokeOn, $"FineStrokeOn = {Host.State.FineStrokeOn}");
        Check("墨迹预测偏好也读回来了（重启后仍是开）",
              Host.State.PredictTailOn, $"PredictTailOn = {Host.State.PredictTailOn}");

        // 关着开关时，"停在笔上 0.7 秒"必须**什么都不出**（开关真的在闸门上，不是装饰）
        {
            var penCell4 = ui.CellRectForTest(3);
            SendMouse((int)((penCell4.MinX + penCell4.MaxX) * 0.5f * DpiScale),
                      (int)((penCell4.MinY + penCell4.MaxY) * 0.5f * DpiScale), 0);
            SettleFrames(700);
            Check("关着开关：同样的悬停不出提示",
                  !ui.TipVisibleForTest && !ui.TipEnabledForTest,
                  $"可见 = {ui.TipVisibleForTest}，开关 = {ui.TipEnabledForTest}");
            SendMouse((int)(_virtualX + _virtualW * 0.5f), (int)(_virtualY + _virtualH * 0.4f), 0);
            SettleFrames(150);
        }

        try { File.Delete(prefsPath); } catch { }        // 临时配置用完就删

        Doc.Clear();
        Doc.ClearHistory();
        var keep2 = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        keep2.AddPoint(500, 500, 1f, 0);
        keep2.AddPoint(800, 500, 1f, 1);
        Doc.AddStroke(keep2);

        NormalizePanel();
        // **按标签找那一行，不写行下标**：行表会插来插去（2026-09-17 插了底纹两行、
        // 2026-09-19 把「学科工具」换成坐标系/数轴/网格三行），
        // 写死下标的话每插一次就要改一处引用，而且点错行时红的是那条断言本身
        // （"点重启没反应"）——2026-09-19 就是这么又红了一次。
        // 「重启软件」现在在启动器的底栏（固定四格之一，不再是一行——2026-10-02 改版）
        ui.SetMorePageForTest(0);       // 测试钩子：退回启动器主页
        SettleFrames(150);
        var restartTile = ui.HubBottomRectForTest(2);
        Check("自检能按格找到「重启软件」（底栏固定位）",
              restartTile.MaxY > restartTile.MinY, $"格高 {restartTile.MaxY - restartTile.MinY:F0}");
        // 造一个"上次崩溃抢救留下的残留"，并清掉自动存档从零开始：
        // **这一条是故意的**——新语义里光"不写会话暂存"是不够的，残留会被新进程读回去，
        // 那就成了"重启还带出旧板书"。所以必须被删掉。
        File.WriteAllBytes(Recovery.SessionPath, new byte[] { 1, 2, 3, 4 });
        Recovery.DeleteAuto();

        ClickPhysical((restartTile.MinX + restartTile.MaxX) * 0.5f * DpiScale,
                      (restartTile.MinY + restartTile.MaxY) * 0.5f * DpiScale);
        // 新语义（2026-09-22 用户定："重启就相当于电脑重启，墨迹不保存"）：
        // **判据和"界面崩了自己重建"那条路正好相反**——那条要求 SessionPath 存在
        //（见 `--uitest` 里"板书必须读得回来"那一条），因为那是 App 自己决定重启、
        // 丢了板书是事故。两处一起改就会把其中一条弄坏。
        Check("点「重启软件」＝ 像电脑重启：不写会话暂存，残留也被删掉",
              RestartRequested && !File.Exists(Recovery.SessionPath),
              $"重启已发起 = {RestartRequested}，会话暂存还在 = {File.Exists(Recovery.SessionPath)}");
        Check("但盘上留了一份自动存档（需要时能找回来）",
              Recovery.AutoSaveExists, $"autosave 存在 = {Recovery.AutoSaveExists}");

        // 留的那一份**要真的解得回来**：只断言"文件存在"等于没验（写个空文件也算存在）。
        int keptStrokes = -1;
        try
        {
            var keptBlob = Recovery.LoadAuto();
            Doc.Clear();
            InkSerializer.LoadInto(Doc, keptBlob);
            keptStrokes = Doc.Strokes.Count;
        }
        catch (Exception ex) { Console.WriteLine("  读回自动存档失败：" + ex.Message); }
        Check("留的那一份解得回来、笔数对得上", keptStrokes == 1,
              $"读回 {keptStrokes} 笔（应为 1 笔）");

        Recovery.DeleteAuto();
        Recovery.ClearRestartCount();

        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }


    /// <summary>
    /// **图形那格的界面入口自检**（2026-09-19：给圆 / 三角形 / 平行四边形补真机入口）。
    ///
    /// 为什么单独一支、不塞进 `--paneltest`：
    ///   · paneltest 是"球 → 按钮带"那条最小闭环，项数是**写进报告里的 100**；
    ///     往里加用例会把那个数字改掉，"这次和上次哪里不一样"就说不清了；
    ///   · 这一支要**按真键**（SendInput 发 Ctrl+Alt+O），比 paneltest 慢、也更依赖桌面环境，
    ///     单独一支好在"合成输入不可用"时整支 SKIP，不拖累别的用例。
    ///
    /// 四段：
    ///   A. 上带是**七段**，逐段点一遍 → 工具切成对应的那一种（"真机上点得到"的判据）；
    ///   B. 三个新热键（圆 / 三角形 / 平行四边形）**真按一次** → 工具跟着切；
    ///   C. 用点段选出来的那三种工具**在画布上画一笔** → StrokeKind 就是那一种
    ///      （工具切对了 ≠ 画得出来，最终答案在 StrokeKind 上）；
    ///   D. 主条"图形"那一格的**图标跟着当前种类变**（七种两两不同）。
    /// </summary>
    private void ShapeBandTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图形那格的界面入口自检（七段 / 三个新热键 / 主条图标）===");

        if (SkipIfNoSyntheticInput("图形界面入口自检")) { _quit = true; return; }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-32} {detail}");
        }

        void ClickPhysical(float x, float y)
        {
            SendMouse((int)x, (int)y, 0);                            SettleFrames(80);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(320);
        }

        // 起手和 paneltest 一样：关穿透、挂产品界面、清空文档（判据要确定）。
        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(300);

        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine($"  界面没挂上：当前 = {CurrentUi.Name}");
            ExitCode = 1;
            _quit = true;
            return;
        }

        // 图形那一格从左到右、从上到下分别是什么。**这里是期望值**
        // （界面那边读的是 FullUi.ShapeRows，一行一个数组）：
        // 自检要是也从界面那张表里读，就成了"自己和自己比"。
        var want = new (Tool tool, string name)[]
        {
            (Tool.Line, "直线"), (Tool.Rectangle, "矩形"), (Tool.Ellipse, "椭圆"),
            (Tool.Circle, "圆"), (Tool.Triangle, "三角形"),
            (Tool.Parallelogram, "平行四边形"), (Tool.Arrow, "箭头"),
            // 2026-09-19：坐标轴那一批**接在末尾**（用户要求"所有图形都从图形框那个入口进"）。
            // **数轴那一段当天晚些又撤掉了**（用户："把快捷栏最后一个图标删掉，我感觉用不到"），
            // 所以第一行到此为止是 8 段、坐标系是最后一段。撤的是**入口**：
            // `StrokeKind.NumberLine` 留在存档里，老板书里的数轴照样能打开、能选中、能删
            // （见 计划-图形工具.md 11.2）。
            (Tool.Coordinate, "坐标系"),

            // **第二行（2026-09-20 第五批）**：四种曲线。用户定的入口是
            // "图形框里加第二行，高频的只要一行"——所以第一行八段一个没动。
            //
            // ⚠ **2026-09-22 用户重排了行首三格**（原话："椭圆排在第二行的最前面，
            // 然后是椭圆的……图标前三个是椭圆，双曲线，抛物线"）：
            //   ① 椭圆（带焦点）② 双曲线 ③ 抛物线 —— 三个**圆锥曲线**连在一起，
            //   双曲线那一格当天还多了"有 / 无渐近线"两档、椭圆那一格多了"有 / 无焦点三角形"。
            //   注意段落号是按**工具名**找的（`ShapeSegmentIndexForTest`），所以这次重排
            //   不会让下面任何一条断言点错格子。
            (Tool.ConicEllipse, "椭圆（带焦点）"),
            (Tool.Hyperbola, "双曲线"), (Tool.Parabola, "抛物线"),
            (Tool.Sine, "正弦"), (Tool.Cosine, "余弦"),
            // 2026-09-20 第十六批：**波浪线**（用户："还有一个另外的**很多周期的波浪**的弦函数线"）。
            // 它和正弦是同一条曲线，差别只在**这一拖管什么**：
            // 正弦的框宽 = 一个周期（讲"一个周期的图象"）、波浪线的框宽 = 要画多长
            //（周期由振幅定，讲周期性用）。所以两格紧挨着排，曲线那一行七格。
            (Tool.Wave, "波浪线"),
            // 2026-09-20 第十五批：**正切**（用户："可以画正切"）。它和正弦 / 余弦同族
            // （都在第二行这一族曲线上），但**一笔**画完：按下是原点、拖出去是以它为中心的框
            //（见 StrokeKind.Tangent）。⚠ 它在余弦**后面**——同一族的曲线连着排。
            (Tool.Tangent, "正切"),
            // 同一天又搬进来两个**立体图形**（照 InkClass 的 case 6/7）：
            // 一次拖出外接矩形，底面被挡住的那半圈是虚线。
            (Tool.Cylinder, "圆柱"), (Tool.Cone, "圆锥"),
            // 2026-09-20 第十三批：**圆台**（用户："再加一个圆台"）。和圆柱 / 圆锥同族
            //（一笔拖出外接矩形），所以紧挨着它们放。
            (Tool.ConeFrustum, "圆台"),
            // 2026-09-20 第十四批：**球**（用户："在加入球"）。一笔拖外接矩形。
            // ⚠ 位置按用户同一天的口径改过："把球放在旋转体后面" —— 所以它在圆台之后、
            // 棱柱之前（旋转体那一组"柱 / 锥 / 台 / 球"连着排）。
            (Tool.Sphere, "球"),
            // 2026-09-20 第十一批：**棱柱**（3/4/5/6 棱柱 ＋ 直/斜）。两笔，
            // 而且那一格"再点一次换一档"（和直线的线型同构，见 §32）。
            (Tool.Prism, "棱柱"),
            // 2026-09-20 第十二批：**棱锥 / 棱台**（见 §34）。它们和棱柱是**一族**
            //（同样的两笔、同样的 3/4/5/6 档、同样的"直"吸附），所以紧挨着排。
            (Tool.Pyramid, "棱锥"), (Tool.Frustum, "棱台"),
            // ⚠ **长方体 / 四面体那两段撤掉了**（同一天，用户："那两格似乎可以删除掉了"）——
            // 所以这张期望表里也没有它们了，但**画法与存档都还在**（下面单列一条断言钉住
            // "能画、没入口"这第三种状态，正因为"有入口的那些"这张表管不到它们）。
            // 行结构见下面的 `wantRowLens`（2026-09-20 第十三批从两行改成三行）。
        };
        // 图形那一格的**行结构**（每行几段）——**期望值，独立于界面那张表**。
        // 加图形时必须一起改：下面先断一句"各行加起来 = want 的段数"，对不上就红
        //（这就是"自检表本身也要被卡住"那条规矩在这里的落点）。
        // 2026-09-20 第十三批：从"两行 8+10"改成"三行 8+4+6"——第二行 10 段时每格
        // 只有 55 宽，低于"每段 ≥ 60"那条量出来的门槛（见 计划-图形工具.md §35）；
        // 第十四批加球：第三行 6 → **7** 段（604 ÷ 7 ≈ 81，还在门槛之上）。
        // 第十五批加正切、第十六批加波浪线：**第二行 4 → 6** 段（604 ÷ 6 ≈ 101）。
        // 2026-09-22 加椭圆（带焦点）：**第二行 6 → 7** 段（604 ÷ 7 ≈ 86，和第三行一样宽，
        // 仍在"每段 ≥ 60"那条门槛之上）。
        // 2026-09-22 加「图库」：**第四行 1 段**——它是**动作**（点开图库面板），
        // 不是图形工具，所以 `want` 里没有它，但**行结构里要有它**（带子高了就得占一行）。
        int[] wantRowLens = { 8, 7, 7, 1 };
        /** 「图库」那一段：段表之外的**动作段**（不计入 `want`）。 */
        const int actionSegs = 1;

        // 上带只在"指针落在面板上"时张开（见 FullUi.RailHoverZone）。点完一格、画完一笔
        // 之后指针可能在画布上，所以每次要点段之前先把指针挪回主条等它张开。
        // 合成鼠标**偶尔会丢一次移动**（paneltest 里也见过），给三次机会。
        void EnsureRailOpen()
        {
            var bar = ui.BarRectForTest;
            int px = (int)((bar.MinX + bar.MaxX) * 0.5f * DpiScale);
            int py = (int)((bar.MinY + bar.MaxY) * 0.5f * DpiScale);
            // **等到"完全张开"（≥0.99），不是"过 0.5 就当开"**（2026-10-09）：
            // 动画中途（比如 0.63）时带子矮一截——后面量的是"完全张开"的 layout，
            // 采样抓在半路就会误报"第 4 行出了带子"。合成鼠标偶尔丢移动，给三次机会。
            for (int attempt = 0; attempt < 3 && ui.RailValueForTest < 0.99f; attempt++)
            {
                SendMouse(px, py - attempt, 0);
                SettleFrames(700);
            }
        }

        void ClickSegment(int i)
        {
            var seg = ui.SegmentRectForTest(i);
            ClickPhysical((seg.MinX + seg.MaxX) * 0.5f * DpiScale,
                          (seg.MinY + seg.MaxY) * 0.5f * DpiScale);
        }

        // 让上带回到"图形"那一格，再把指针挪回主条等它张开。
        // **每一步都要重新做**：上带是跟着当前工具走的（CellForTool），按过换工具的热键
        // 或换成笔之后，它显示的是**那个工具**的设置条——这时去点"第 3 段"会点在
        // 别的设置条上（笔那格是 12 个色片），点出来的结果和图形毫无关系。
        // C 段第一次跑就是这么点空的。
        void GotoShapeBand()
        {
            var cell = ui.CellRectForTest(8);
            ClickPhysical((cell.MinX + cell.MaxX) * 0.5f * DpiScale,
                          (cell.MinY + cell.MaxY) * 0.5f * DpiScale);
            EnsureRailOpen();
        }

        // 面板现在是**启动即展开**的（用户 2026-09-27 定的默认态），所以正常情况下这一段
        // 什么都不用做。留着兜底：万一哪天默认态又改回收起（或者这里的界面是中途换过的），
        // 点一下"收起格"旁边的球把它掰开，后面才不会"一段都点不到"。
        if (!ui.ExpandedForTest)
        {
            var bb = ui.QueryBounds();
            ClickPhysical((bb.MinX + bb.MaxX) * 0.5f * DpiScale,
                          (bb.MinY + bb.MaxY) * 0.5f * DpiScale);
            SettleFrames(300);
        }
        Check("面板是展开的（不然面板上的格子一个都点不到）",
              ui.ExpandedForTest, $"展开 = {ui.ExpandedForTest}");

        // ================= A. 每一段都点得到 =================
        //
        // ⚠ 段数**不写死在文案里**（"十七段"这种字眼一加图形就过期）：下面几条断言全用
        // `want.Length` / `n` 现算。
        Console.WriteLine($"  -- A. 上带图形格（{want.Length} 段）：逐段点一遍，工具真的切了 --");
        GotoShapeBand();

        int n = ui.BandSegmentCountForTest;
        Check($"自检表：行结构 {string.Join("＋", wantRowLens)} = {want.Length} 个图形 ＋ {actionSegs} 段动作",
              wantRowLens.Sum() == want.Length + actionSegs && wantRowLens.All(v => v > 0),
              $"{string.Join("＋", wantRowLens)} = {wantRowLens.Sum()}（期望 {want.Length + actionSegs}）");
        Check($"图形那格的上带是 {want.Length + actionSegs} 段（{wantRowLens.Length} 行：{string.Join("＋", wantRowLens)}）",
              n == want.Length + actionSegs && InkUi.FullUi.ShapeRowCountForTest == wantRowLens.Length,
              $"段数 {n}（期望 {want.Length + actionSegs}），行数 {InkUi.FullUi.ShapeRowCountForTest}"
              + $"（期望 {wantRowLens.Length}）");

        // 段宽和越界：段宽 = 可用宽 ÷ **本行**段数，所以各行各有各的宽度。
        // "挤到看不清" ＝ 这个入口等于没有，所以宽度本身就是判据（图标 18 逻辑像素，
        // 60 的门槛给的是"图标四周还留得下 20 像素空白"）——2026-09-20 第十三批
        // 就是这条把"第二行 10 段（每格 55 宽）"当场拦下来的，所以才改成三行。
        //
        // 行分组按**自检自己那份 `wantRowLens`** 走（不读界面的行表，那成了自己和自己比）；
        // 每行再从界面拿一次段矩形，验"行不重叠、都在带子里"。
        var bandRect = ui.BandRectForTest;
        float minW = float.MaxValue, maxRight = float.MinValue;
        var rowTop = new float[wantRowLens.Length];
        var rowBottom = new float[wantRowLens.Length];
        for (int r = 0; r < wantRowLens.Length; r++) { rowTop[r] = float.MaxValue; rowBottom[r] = float.MinValue; }
        int segIdx = 0;
        for (int r = 0; r < wantRowLens.Length; r++)
        {
            for (int c = 0; c < wantRowLens[r]; c++, segIdx++)
            {
                var rr = ui.SegmentRectForTest(segIdx);
                minW = MathF.Min(minW, rr.MaxX - rr.MinX);
                maxRight = MathF.Max(maxRight, rr.MaxX);
                rowTop[r] = MathF.Min(rowTop[r], rr.MinY);
                rowBottom[r] = MathF.Max(rowBottom[r], rr.MaxY);
            }
        }
        Check($"{want.Length + actionSegs} 段都画得下（每段 ≥ 60 宽、最右一段不越出上带）",
              n == want.Length + actionSegs && minW >= 60f && maxRight <= bandRect.MaxX + 0.5f,
              $"最窄 {minW:F0} 逻辑像素（门槛 60），上带宽 {bandRect.MaxX - bandRect.MinX:F0}");
        // 各行**不许叠在一起**，而且都要落在带子里——带子为多行长得更高
        // （见 FullUi.BandHeightLogical），这一条就是钉住"长得够高"。
        bool rowsOk = true;
        string rowNote = "";
        for (int r = 0; r < wantRowLens.Length; r++)
        {
            if (rowTop[r] < bandRect.MinY - 0.5f || rowBottom[r] > bandRect.MaxY + 0.5f)
            { rowsOk = false; rowNote = $"第 {r + 1} 行出了带子"; }
            if (r > 0 && rowTop[r] < rowBottom[r - 1] - 0.5f)
            { rowsOk = false; rowNote = $"第 {r + 1} 行和第 {r} 行叠了"; }
        }
        Check($"{wantRowLens.Length} 行不重叠、且都在带子里（带子为多行长得更高）",
              rowsOk,
              rowsOk
                  ? "各行 y " + string.Join(" / ", Enumerable.Range(0, wantRowLens.Length)
                        .Select(r => $"{rowTop[r]:F0}..{rowBottom[r]:F0}"))
                    + $"，带子 y {bandRect.MinY:F0}..{bandRect.MaxY:F0}"
                  : rowNote + $"[诊断] 带子 y {bandRect.MinY:F0}..{bandRect.MaxY:F0}，"
                    + $"行4 {rowTop[^1]:F0}..{rowBottom[^1]:F0}，rail={ui.RailValueForTest:F2}，"
                    + $"画中={Host.State.IsDrawing}，现在带子 y {ui.BandRectForTest.MinY:F0}..{ui.BandRectForTest.MaxY:F0}");

        // == 指针停在**任何一行**上，带子都得留得住（不许收） ==
        //
        // 用户 2026-09-20 报的 bug 就是这一条："**鼠标移动到第一行的任何图形位置，
        // 色带会收起来**"——根因是"焦点在面板上"的判定区用了**一行**的带子高
        //（`Tokens.BandHeight`），而这一格是三行：指针挪到上面那两行就落到判定区外面，
        // 220 毫秒后带子收回。
        //
        // 所以判据要**逐行**来，而且要看**两件事**：判定为悬停（`RailHoverForTest`）
        // 和真的没收（`RailOpenForTest`）—— 只看前者的话，"判定区对了但动画被别处打断"
        // 会溜过去。停 400 毫秒是故意的：慢隐要 800ms 才动，等短了测的就是"它没来得及收"，
        // 那正是这条要钉的"留得住"。
        {
            string badRow = "";
            for (int r = 0; r < wantRowLens.Length && badRow.Length == 0; r++)
            {
                EnsureRailOpen();
                // 段矩形 / 带子矩形都是**逻辑**坐标，合成鼠标要的是屏幕像素 → 乘 DpiScale
                //（和上面 `EnsureRailOpen` 同一个口径）。
                int cx = (int)((bandRect.MinX + bandRect.MaxX) * 0.5f * DpiScale);
                int cy = (int)((rowTop[r] + rowBottom[r]) * 0.5f * DpiScale);
                SendMouse(cx, cy, 0);
                SettleFrames(400);
                if (!ui.RailHoverForTest || !ui.RailOpenForTest)
                    badRow = $"第 {r + 1} 行（y {cy}）：悬停={ui.RailHoverForTest}，张开={ui.RailOpenForTest}"
                           + $"[诊断] rail={ui.RailValueForTest:F2}，画中={Host.State.IsDrawing}，"
                           + $"带子现在 y {ui.BandRectForTest.MinY:F0}..{ui.BandRectForTest.MaxY:F0}";
            }
            Check($"{wantRowLens.Length} 行的**每一行**都留得住带子（指针停上去不许收）",
                  badRow.Length == 0,
                  badRow.Length == 0
                      ? $"逐行停 400 毫秒，带子一直张开（带子 y {bandRect.MinY:F0}..{bandRect.MaxY:F0}）"
                      : badRow);
        }

        // **哪几格该有档位点、各有几档**——期望值独立写在下面这张表里。
        // 用户 2026-09-20 提的两条都落在这上面：
        //   · "你把档位点挪到图标右边"（位置）—— 那条只有出图看得见；
        //   · "抛物线页增加小圆点"（**这一格原来没有点**）—— 这条能断，而且该断：
        //     它有"再点一次换一档"却一直没给点，是**漏的**（和加图形忘了自检同一类）。
        // 所以这里逐段比：该有几档就几档，别的图形必须 0。
        {
            var pipWant = new Dictionary<Tool, int>
            {
                [Tool.Line] = 3,        // 实 / 虚 / 点
                [Tool.Parabola] = 2,    // 上下 / 左右
                [Tool.Prism] = 4, [Tool.Pyramid] = 4, [Tool.Frustum] = 4,   // 三 / 四 / 五 / 六
                // 2026-09-22 加的两格，各 2 档：双曲线（有 / 无渐近线）、
                // 椭圆（带焦点）（有 / 无焦点三角形）。
                [Tool.Hyperbola] = 2, [Tool.ConicEllipse] = 2,
                // 2026-09-24：「坐标系」也成了"再点一次换一档"的那一格（带网格 / 不带网格）
                // ——这一档是从「更多」抽屉挪过来的，**一样要给点**：
                // 用户当天紧接着就提了"它档位之间是有切换按钮的，你可以参照一下其他那个切换逻辑"。
                [Tool.Coordinate] = 2,
            };
            int pipOk = 0, pipNo = 0;
            var pipWrong = new List<string>();
            for (int i = 0; i < want.Length; i++)
            {
                int wantCount = pipWant.TryGetValue(want[i].tool, out int c) ? c : 0;
                var (gotCount, gotCur) = ui.ShapePipsForTest(i);
                bool ok = gotCount == wantCount && (gotCount == 0 || (gotCur >= 0 && gotCur < gotCount));
                if (ok && gotCount > 0) pipOk++; else if (ok) pipNo++;
                if (!ok)
                    pipWrong.Add($"{want[i].name}：{gotCount} 个（期望 {wantCount}）");
            }
            Check("档位点：**该有的那几格**有（直线 3 / 抛物线 2 / 双曲线 2 / 椭圆带焦点 2 / 坐标系 2 / 棱柱族 4），别的图形一个点都没有",
                  pipWrong.Count == 0,
                  pipWrong.Count > 0
                      ? string.Join("；", pipWrong)
                      : $"有档位的 {pipOk} 格、没档位的 {pipNo} 格，当前档也都在范围内");
        }

        for (int i = 0; i < want.Length; i++)
        {
            EnsureRailOpen();
            ClickSegment(i);
            Check($"第 {i + 1} 段「{want[i].name}」点得到、工具切了",
                  Host.State.Tool == want[i].tool,
                  $"工具 = {Host.State.Tool}（期望 {want[i].tool}）");
        }

        // ---- 名单一致性：三份"图形"判据必须一一对应 ----
        //
        // 2026-09-20 收敛之后，"哪些是图形"只剩三个入口，维度不同但**必须同步**：
        //   · `Engine.IsShapeTool(Tool)`  = 这个工具**画出来的是图形**（**含数轴**——
        //     它没有面板入口了，但画法还在，老存档里的数轴要能选中、能删）；
        //   · `FullUi` 的两张段表         = 面板上**有入口**的那些（**不含数轴**）；
        //   · `Stroke.IsShapeKind(Kind)`  = 按种类的"参数化图形"那一份
        //     （Model 与 Selection 共用；**矩形不在里面，是故意的**：它的局部 AABB 就是
        //      自己的边界、选中后走通用框那套八向柄，不参与"按参数算墨迹框 / 发定义元素手柄"）。
        //
        // 这里逐段对照"有入口的必须能画；除矩形外还必须算参数化图形"。
        // 加新图形时漏改一处，这一条立刻红——而不是等到"画出来点不中"才发现。
        bool namesAgree = true;
        string nameNote = "";
        for (int i = 0; i < want.Length; i++)
        {
            var kind = KindOfShapeTool(want[i].tool);
            if (!IsShapeTool(want[i].tool))
            {
                namesAgree = false;
                nameNote = $"「{want[i].name}」面板上有入口，但 Engine.IsShapeTool 说它不是图形工具";
                break;
            }
            if (kind != StrokeKind.Rectangle && !Stroke.IsShapeKind(kind))
            {
                namesAgree = false;
                nameNote = $"「{want[i].name}」的 Kind = {kind}，但 Stroke.IsShapeKind 说它不是参数化图形";
                break;
            }
        }
        Check("名单一致：面板十二段 ↔ Engine.IsShapeTool ↔ Stroke.IsShapeKind 三方对得上",
              namesAgree,
              namesAgree ? $"逐段核对 {want.Length} 段（矩形落在 ShapeKind 之外，故意的）" : nameNote);
        // 另一头单独验一句：**能画、但面板上没入口**的那些（2026-09-19 撤的数轴、
        // 2026-09-20 第十二批撤的长方体 / 四面体）——撤的是**入口**，画法与存档都留着。
        // 不单列的话，上面对照表只会看"有入口的那些"，这第三种状态就没人管了
        //（而它恰恰是最容易出事的一种：以为删干净了，结果旧板书打开少一条）。
        var noEntry = new (Tool tool, string name)[]
        {
            (Tool.NumberLine, "数轴"),
            (Tool.Cuboid, "长方体"),
            (Tool.Tetrahedron, "四面体"),
        };
        foreach (var (tool, name) in noEntry)
        {
            Check($"名单一致：「{name}」**能画、但面板没入口**（撤的是入口，不是画法）",
                  IsShapeTool(tool) && !InkUi.FullUi.HasShapeEntryForTest(tool),
                  $"能画 = {IsShapeTool(tool)}，"
                  + $"有入口 = {InkUi.FullUi.HasShapeEntryForTest(tool)}");
            Check($"名单一致：「{name}」的种类仍算参数化图形（旧板书要能选中、能移动）",
                  Stroke.IsShapeKind(KindOfShapeTool(tool)),
                  $"Kind = {KindOfShapeTool(tool)}");
        }

        // **自检表本身也要被卡住**——这一条是给"**以后每个新增图形都要自检**"立的规矩。
        //
        // 做法：把引擎里所有"能画的图形"**枚举出来**（`IsShapeTool` 说了算），要求它们
        // 一个不漏地出现在上面两张表里。以后新加一种图形、只要忘了写进自检表，
        // **这条当场红**——而不是像棱锥 / 棱台这一轮那样，靠用户上手才发现。
        //
        // （为什么要"枚举 + 集合比较"，而不是"再数一遍个数"：个数对不上说明不了**谁**漏了，
        //   而这里错了会直接把名字打出来。）
        var engineShapes = Enum.GetValues<Tool>().Where(IsShapeTool).ToArray();
        var listedTools = want.Select(w => w.tool).Concat(noEntry.Select(n => n.tool)).ToArray();
        var missingShapes = engineShapes.Where(t => !listedTools.Contains(t)).ToArray();
        var extraShapes = listedTools.Where(t => !engineShapes.Contains(t)).ToArray();
        Check("自检覆盖：引擎里**每一个**图形都在自检表里（新增图形忘了写自检 → 这里红）",
              missingShapes.Length == 0 && extraShapes.Length == 0,
              missingShapes.Length > 0
                  ? $"自检表漏了：{string.Join(" / ", missingShapes)}"
                  : extraShapes.Length > 0
                      ? $"自检表里多出（引擎说它不是图形）：{string.Join(" / ", extraShapes)}"
                      : $"引擎 {engineShapes.Length} 个图形 = 自检表 {listedTools.Length} 个"
                        + $"（有入口 {want.Length} ＋ 没入口 {noEntry.Length}），两边完全对上");

        // ================= A2. 抛物线那格：**再点一次 = 换一档（上下 / 左右）** =================
        //
        // 用户 2026-09-20 深夜定：**上下还是左右**画之前定，不从选中框改
        //（"选中框的抛物线按钮功能取消哦，我不打算从这个转抛物线开口"）；
        // 后来（同一天更晚）他看了手感又说"感觉不对，还是参考他的逻辑"——
        // 于是**具体朝哪边**改由那一拖的符号定（照 InkClass 的 case 20/21），
        // 面板这一格只剩"哪一对"（= 他那儿两个按钮：上下抛物 / 左右抛物）。
        // 这里验四件事：
        //   ① 点第一下 = 选中抛物线、还是"上下"那一档；
        //   ② 再点 = 换到"左右"（两档循环）；
        //   ③ 那一格的**图标名**跟着变（不换的话，老师看不出点第二下有没有生效）；
        //   ④ 别的图形**不参与**这条规则（再点还是它自己）。
        Console.WriteLine("  -- A2. 抛物线格：再点一次换一档（上下 / 左右，画之前定）--");
        GotoShapeBand();
        int paraSeg = InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Parabola);   // 按工具名找（不写死段号）
        ClickSegment(paraSeg);
        Check("抛物线：点第一下 = 选中它，还是「上下抛物」那一档",
              Host.State.Tool == Tool.Parabola && Host.State.ParabolaAxis == CurveAxis.OpenUp,
              $"工具 {Host.State.Tool}、档位 {Host.State.ParabolaAxis}");

        var axisRun = new (CurveAxis want, string icon)[]
        {
            (CurveAxis.OpenRight, "parabolaRight"),   // 第二下 = 换到"左右抛物"
            (CurveAxis.OpenUp, "parabola"),           // 第三下 = 转回"上下抛物"
        };
        for (int k = 0; k < axisRun.Length; k++)
        {
            EnsureRailOpen();
            ClickSegment(paraSeg);                    // 再点一次 = 换一档
            Check($"抛物线：第 {k + 2} 次点 = 换到 {axisRun[k].want}（工具没变）",
                  Host.State.Tool == Tool.Parabola && Host.State.ParabolaAxis == axisRun[k].want,
                  $"工具 {Host.State.Tool}、档位 {Host.State.ParabolaAxis}");
            Check($"抛物线：图标名跟着变成 {axisRun[k].icon}",
                  ui.ShapeIconNameForTest(paraSeg) == axisRun[k].icon,
                  ui.ShapeIconNameForTest(paraSeg));
        }

        // 别的图形**不许**被这条规则影响：连点两次三角形，还是三角形。
        EnsureRailOpen();
        ClickSegment(4);                              // 第一行第 5 段 = 三角形
        EnsureRailOpen();
        ClickSegment(4);
        Check("别的图形：再点一次仍然是它自己（这条规则只对抛物线）",
              Host.State.Tool == Tool.Triangle, $"工具 {Host.State.Tool}");

        // ================= A3. 直线那一段：**再点一次 = 换一档线型** =================
        //
        // 用户 2026-09-20 定（落地记在 计划-图形工具.md §27.1）：
        // "点击直线的图标，它会变成虚线，再点击变成点虚线，再点击又变成直线……
        //  这样就可以在虚实之间切换，而且又**省了好几个空间格**"。
        //
        // 验五件事（缺哪一件都可能"看着绿其实没做"）：
        //   ① **一轮三档**：实线 → 虚线 → 点线 → 实线（第四下回到起点，不是停在点线）；
        //   ② 那一格的**图标名**跟着换（不换的话，老师看不出点第二下有没有生效）；
        //   ③ 新画出来的直线**真的带那一档**（几何层，不只是界面显示）；
        //   ④ **只影响以后画的**：先画好的那条一个字节都不许动；
        //   ⑤ 别的图形**不吃这一档**（画出来一律实线）——这条钉住"只给直线"的口径。
        Console.WriteLine("  -- A3. 直线格：再点一次换一档线型（实线 → 虚线 → 点线）--");
        GotoShapeBand();

        // 先归零：不写死"进来时一定是实线、一定是直线工具"——前面任何一段点过它就会错位。
        // **两个条件都要判**：工具不是直线时，点它只是"选中它"（不换档）；
        // 工具已经是直线时，点它才换档。最多四下一。
        for (int k = 0; k < 4
             && (Host.State.Tool != Tool.Line || Host.State.LineDash != StrokeDash.Solid); k++)
        {
            EnsureRailOpen();
            ClickSegment(0);                          // 第一行第 1 段 = 直线
        }
        Check("直线：点它 = 选中直线，当前档是**实线**",
              Host.State.Tool == Tool.Line && Host.State.LineDash == StrokeDash.Solid,
              $"工具 {Host.State.Tool}、档位 {Host.State.LineDash}");
        Check("直线：实线那一档的图标名 = line",
              ui.ShapeIconNameForTest(0) == "line", ui.ShapeIconNameForTest(0));

        Doc.Clear();
        Doc.ClearHistory();
        // 落点和 C 段同一套：画布中上部，别落到屏幕下沿的面板上（那就变成点按钮了）。
        int ldX = (int)(_virtualX + _virtualW * 0.28f);
        int ldY = (int)(_virtualY + _virtualH * 0.42f);
        void DragLine(int x, int y)
        {
            SendMouse(x, y, 0);                                       SettleFrames(60);
            SendMouse(x, y, Native.MOUSEEVENTF_LEFTDOWN);             SettleFrames(60);
            SendMouse(x + 130, y + 60, 0);                            SettleFrames(40);
            SendMouse(x + 260, y + 120, 0);                           SettleFrames(40);
            SendMouse(x + 260, y + 120, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(220);
        }

        // 第一条：实线（用来验 ④"老的那条不受影响"）。
        DragLine(ldX, ldY);
        var oldLine = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("直线·实线档：新画的直线带**实线**",
              oldLine != null && oldLine.Kind == StrokeKind.Line && oldLine.Dash == StrokeDash.Solid,
              $"对象 {Doc.Strokes.Count} 个，线型 "
              + $"{(oldLine == null ? "（没画出来）" : oldLine.Dash.ToString())}（期望 Solid）");

        // 一轮三档：点一下换一档，图标名跟着换。
        var dashRun = new (StrokeDash want, string icon)[]
        {
            (StrokeDash.Dashed, "lineDash"),
            (StrokeDash.Dotted, "lineDot"),
            (StrokeDash.Solid, "line"),               // 第四下回到实线（一轮闭环）
        };
        for (int k = 0; k < dashRun.Length; k++)
        {
            EnsureRailOpen();
            ClickSegment(0);                          // 再点一次 = 换一档
            Check($"直线：第 {k + 2} 次点 = 换到 {dashRun[k].want}（工具没变）",
                  Host.State.Tool == Tool.Line && Host.State.LineDash == dashRun[k].want,
                  $"工具 {Host.State.Tool}、档位 {Host.State.LineDash}（期望 {dashRun[k].want}）");
            Check($"直线：图标名跟着变成 {dashRun[k].icon}",
                  ui.ShapeIconNameForTest(0) == dashRun[k].icon, ui.ShapeIconNameForTest(0));

            // 第三下（点线）那一档顺手验"画出来真的是点线"——三档各画一条就够说明问题，
            // 不必每档都拖一次（拖一次要等 400 毫秒，这段会白长三倍）。
            if (dashRun[k].want == StrokeDash.Dotted)
            {
                DragLine(ldX, ldY + 200);
                var dotLine = Doc.Strokes.Count == 2 ? Doc.Strokes[1] : null;
                Check("直线·点线档：新画的直线带**点线**",
                      dotLine != null && dotLine.Kind == StrokeKind.Line
                      && dotLine.Dash == StrokeDash.Dotted,
                      $"对象 {Doc.Strokes.Count} 个，线型 "
                      + $"{(dotLine == null ? "（没画出来）" : dotLine.Dash.ToString())}（期望 Dotted）");
                Check("直线：换档**只影响以后画的**，先前那条实线一个字节没动",
                      oldLine != null && oldLine.Dash == StrokeDash.Solid,
                      $"先前那条 = {(oldLine == null ? "（没了）" : oldLine.Dash.ToString())}（期望 Solid）");
            }
        }

        // ⑤ 别的图形不吃这一档：切到矩形再画一条，必须是实线（哪怕直线那格刚切过点线）。
        EnsureRailOpen();
        ClickSegment(1);                              // 第一行第 2 段 = 矩形
        DragLine(ldX, ldY + 400);
        var rectLine = Doc.Strokes.Count == 3 ? Doc.Strokes[2] : null;
        Check("别的图形不吃这一档：矩形画出来仍然是**实线**",
              Host.State.Tool == Tool.Rectangle
              && rectLine != null && rectLine.Kind == StrokeKind.Rectangle
              && rectLine.Dash == StrokeDash.Solid,
              $"对象 {Doc.Strokes.Count} 个，矩形线型 "
              + $"{(rectLine == null ? "（没画出来）" : rectLine.Dash.ToString())}（期望 Solid）");
        Doc.Clear();
        Doc.ClearHistory();

        // ================= A4. 双曲线 / 椭圆（带焦点）那两格：**再点一次换一档** =================
        //
        // 用户 2026-09-22 定的两条（原话）：
        //   · 双曲线"增加两挡，有渐近线和无渐近线？**就是化的时候是都有渐近线，但是最终显示没有**，
        //     图标就按照有渐近线和无渐近线"；
        //   · 椭圆（带焦点）"也有两档，有焦点三角形和没有焦点三角形，焦点三角形顶点在椭圆上，
        //     可以在椭圆上拖动"（"拖 P"那一条在 `--curvetest` 的 ⑤e 里验，这里只管格子）。
        //
        // 两格同构，所以一段里一起验，每格盯四件事：
        //   ① 一轮两档（点第二下换到另一档、第三下转回来）；② 图标名跟着换；
        //   ③ **真的画出来是按那一档**（几何层：`ShowAsymptotes` / `FocusTriangle`）；
        //   ④ 双曲线多一条"**画的过程中一律有渐近线**"——半成品那一位必须是 true，
        //      哪怕当前档是"无"（收口在松手那一刻，见 Engine.EndStroke）。
        Console.WriteLine("  -- A4. 双曲线 / 椭圆（带焦点）：再点一次换一档（各 2 档）--");
        GotoShapeBand();
        int hySeg = InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Hyperbola);          // 按工具名找（不写死段号）
        int ceSeg = InkUi.FullUi.ShapeSegmentIndexForTest(Tool.ConicEllipse);

        // 下面两条**屏幕像素**断言要数品红（照 --curvetest 那一套），所以这一段先换成品红，
        // 段落末尾再换回原来的颜色（后面的 B/C/D 段不该受这一段影响）。
        var colorBeforeA4 = Host.State.PaletteBase;
        Host.Commands.SetColor(new Color4(1f, 0f, 1f, 1f));

        // 渐近线上"离曲线足够远"的一个取样点（照 --pixelerasetest 4b 段那套）＋
        // "数它周围那一小块的品红像素"。**为什么要数像素**：见下面"无渐近线"那条断言
        // ——只查字段的话，"字段对了、屏幕上那两条虚线还留着"这种 bug 照不出来
        //（第一版就是这么漏的，用户一眼就看见了）。
        Vector2 AsymSample(Stroke s)
        {
            var outline = s.ShapeOutline();
            for (int side = -1; side <= 1; side += 2)
            {
                var (from, to) = s.HyperbolaAsymptoteLocal(side);
                for (int k = 1; k < 20; k++)
                {
                    var q = Vector2.Lerp(from, to, k / 20f);
                    float d = float.MaxValue;
                    for (int m = 0; m < outline.Count; m++)
                        if (!Stroke.IsOutlineBreak(outline[m]))
                            d = MathF.Min(d, Vector2.Distance(q, outline[m]));
                    if (d > 14f) return q;
                }
            }
            return new Vector2(float.NaN, float.NaN);
        }
        int InkAt(Vector2 q) => float.IsNaN(q.X)
            ? -1
            : ScreenProbe.CountMagenta((int)q.X - 18, (int)q.Y - 18, 36, 36);

        // 归零到"有渐近线"那一档（最多三下：选中它 / 换一档 / 再换回来）。
        for (int k = 0; k < 3
             && (Host.State.Tool != Tool.Hyperbola || !Host.State.HyperbolaAsymptotes); k++)
        {
            EnsureRailOpen();
            ClickSegment(hySeg);
        }
        Check("双曲线：点它 = 选中它，当前档 = **有渐近线**（默认档）",
              Host.State.Tool == Tool.Hyperbola && Host.State.HyperbolaAsymptotes,
              $"工具 {Host.State.Tool}、档 {Host.State.HyperbolaAsymptotes}");
        Check("双曲线：有渐近线那一档的图标名 = hyperbola（两支 ＋ 两条细斜线）",
              ui.ShapeIconNameForTest(hySeg) == "hyperbola", ui.ShapeIconNameForTest(hySeg));
        Check("双曲线：那一格右边点**2 个档位点**、当前是第 1 个",
              ui.ShapePipsForTest(hySeg) == (2, 0), $"{ui.ShapePipsForTest(hySeg)}");

        // 真机画一条（双曲线是**两笔**：渐近线框 → 曲线经过的点）。
        // `midCheck` 在**两笔之间**跑——那一刻半成品还在引擎手里（没进文档），
        // 正好用来验 ④"画的过程里渐近线一直在"。
        int hyX = (int)(_virtualX + _virtualW * 0.30f);
        int hyY = (int)(_virtualY + _virtualH * 0.34f);
        void DragHyperbola(int x, int y, Action midCheck)
        {
            SendMouse(x, y, 0);                                       SettleFrames(60);
            SendMouse(x, y, Native.MOUSEEVENTF_LEFTDOWN);             SettleFrames(60);
            SendMouse(x + 130, y + 60, 0);                            SettleFrames(40);
            SendMouse(x + 260, y + 120, 0);                           SettleFrames(40);
            SendMouse(x + 260, y + 120, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(160);   // 第 1 笔完
            midCheck();
            // 第 2 笔：**也是一次完整的按下-拖-松手**（照 --curvetest ⑧ 段那套；
            // 只发移动 + 松手是**不算一笔**的——自检第一版就这么写的，画出来 0 个对象）。
            SendMouse(x + 300, y + 80, 0);                            SettleFrames(50);
            SendMouse(x + 300, y + 80, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(60);
            SendMouse(x + 330, y + 60, 0);                            SettleFrames(50);
            SendMouse(x + 340, y + 50, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(240);   // 第 2 笔完 → 提交
        }

        Doc.Clear();
        Doc.ClearHistory();
        bool midAsymOn = false, midAsymOff = false;
        DragHyperbola(hyX, hyY, () => midAsymOn = ActiveStroke != null && ActiveStroke.ShowAsymptotes);
        var hyOn = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("双曲线·有渐近线档：画出来带那两条虚线（ShowAsymptotes = true）",
              hyOn != null && hyOn.Kind == StrokeKind.Hyperbola && hyOn.ShowAsymptotes,
              hyOn == null ? $"对象 {Doc.Strokes.Count} 个（没画出来）"
                           : $"Kind {hyOn.Kind}、渐近线 {hyOn.ShowAsymptotes}（期望 true）");
        Check("双曲线：画的过程中也画渐近线（有那一档，两笔之间为 true）",
              midAsymOn, $"两笔之间 ShowAsymptotes = {midAsymOn}");
        // **屏幕上也真有那两条虚线**（不是只在字段里）：在离曲线 > 14 像素的那段虚线上数品红。
        var qOnAsym = AsymSample(hyOn);
        Doc.InvalidateAll();
        SettleFrames(200);
        int asymInkOn = InkAt(qOnAsym);
        Check("双曲线·有渐近线档：屏幕上**真有**那两条虚线（离曲线 14 像素以外那块有墨）",
              asymInkOn > 20, $"{asymInkOn} 个品红像素（门槛 20）");

        EnsureRailOpen();
        ClickSegment(hySeg);                              // 再点一次 = 换到"无渐近线"
        Check("双曲线：第 2 次点 = 换到**无渐近线**（工具没变）",
              Host.State.Tool == Tool.Hyperbola && !Host.State.HyperbolaAsymptotes,
              $"工具 {Host.State.Tool}、档 {Host.State.HyperbolaAsymptotes}（期望 False）");
        Check("双曲线：图标名跟着变成 hyperbolaNoAsym",
              ui.ShapeIconNameForTest(hySeg) == "hyperbolaNoAsym", ui.ShapeIconNameForTest(hySeg));
        Check("双曲线：档位点跟着移到第 2 个",
              ui.ShapePipsForTest(hySeg) == (2, 1), $"{ui.ShapePipsForTest(hySeg)}");

        Doc.Clear();
        Doc.ClearHistory();
        DragHyperbola(hyX, hyY + 320, () => midAsymOff = ActiveStroke != null && ActiveStroke.ShowAsymptotes);
        var hyOff = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("双曲线：**无渐近线那一档，画的时候照样有**（向导不能少）",
              midAsymOff, $"两笔之间 ShowAsymptotes = {midAsymOff}（期望 true）");
        Check("双曲线·无渐近线档：画完只剩曲线（ShowAsymptotes = false）",
              hyOff != null && hyOff.Kind == StrokeKind.Hyperbola && !hyOff.ShowAsymptotes,
              hyOff == null ? $"对象 {Doc.Strokes.Count} 个（没画出来）"
                            : $"Kind {hyOff.Kind}、渐近线 {hyOff.ShowAsymptotes}（期望 false）");
        Check("双曲线：换档**只影响以后画的**，先前那条（有渐近线）一个字节没动",
              hyOn != null && hyOn.ShowAsymptotes,
              $"先前那条 = {(hyOn == null ? "（没了）" : hyOn.ShowAsymptotes.ToString())}（期望 true）");
        // ⚠ **这一条才是用户报的那个 bug 的判据**："我选择的不带渐近线的，但是画完以后还有渐近线？"
        // 根因是**改了字段、没让几何缓存失效**（见 Stroke.SetShowAsymptotes）——屏幕上那份
        // "带渐近线"的辅助几何原样留着。**只查字段是绿的**，所以这里必须数屏幕像素：
        // 同一个取样点（在渐近线上、离曲线 > 14 像素），"无"那一档必须**一个墨点都没有**。
        var qOffAsym = AsymSample(hyOff);
        Doc.InvalidateAll();
        SettleFrames(200);
        int asymInkOff = InkAt(qOffAsym);
        Check("双曲线·无渐近线档：画完之后屏幕上**一个虚线墨点都没有**（不是只在字段里）",
              asymInkOff == 0, $"{asymInkOff} 个品红像素（期望 0；非 0 就是缓存没重建）");

        EnsureRailOpen();
        ClickSegment(hySeg);                              // 第三下 = 转回来（一轮闭环）
        Check("双曲线：第 3 次点 = 转回**有渐近线**（两档一轮）",
              Host.State.Tool == Tool.Hyperbola && Host.State.HyperbolaAsymptotes,
              $"工具 {Host.State.Tool}、档 {Host.State.HyperbolaAsymptotes}（期望 true）");

        // ---- 椭圆（带焦点）：两档 = 有 / 无焦点三角形 ----
        for (int k = 0; k < 3
             && (Host.State.Tool != Tool.ConicEllipse || !Host.State.EllipseFocusTriangle); k++)
        {
            EnsureRailOpen();
            ClickSegment(ceSeg);
        }
        Check("椭圆（带焦点）：点它 = 选中它，当前档 = **有焦点三角形**（默认档）",
              Host.State.Tool == Tool.ConicEllipse && Host.State.EllipseFocusTriangle,
              $"工具 {Host.State.Tool}、档 {Host.State.EllipseFocusTriangle}");
        Check("椭圆（带焦点）：那一格的图标名 = ovalFocusTri",
              ui.ShapeIconNameForTest(ceSeg) == "ovalFocusTri", ui.ShapeIconNameForTest(ceSeg));
        Check("椭圆（带焦点）：那一格右边点**2 个档位点**、当前是第 1 个",
              ui.ShapePipsForTest(ceSeg) == (2, 0), $"{ui.ShapePipsForTest(ceSeg)}");
        Check("两格并存：第一行的「椭圆」和这一格是**两个格子、两张图标**（不是同一个）",
              InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Ellipse) != ceSeg
              && ui.ShapeIconNameForTest(InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Ellipse)) == "oval",
              $"「椭圆」段 {InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Ellipse)} 图标 "
              + $"{ui.ShapeIconNameForTest(InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Ellipse))}、"
              + $"「椭圆（带焦点）」段 {ceSeg} 图标 {ui.ShapeIconNameForTest(ceSeg)}");

        int ceX = (int)(_virtualX + _virtualW * 0.30f);
        int ceY = (int)(_virtualY + _virtualH * 0.62f);
        void DragConicEllipse(int x, int y)
        {
            SendMouse(x, y, 0);                                       SettleFrames(60);
            SendMouse(x, y, Native.MOUSEEVENTF_LEFTDOWN);             SettleFrames(60);
            SendMouse(x + 130, y + 60, 0);                            SettleFrames(40);
            SendMouse(x + 260, y + 120, 0);                           SettleFrames(40);
            SendMouse(x + 260, y + 120, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(240);
        }

        Doc.Clear();
        Doc.ClearHistory();
        DragConicEllipse(ceX, ceY);
        var ceOn = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("椭圆（带焦点）·有三角形档：画出来带焦点三角形（FocusTriangle = true）",
              ceOn != null && ceOn.Kind == StrokeKind.ConicEllipse && ceOn.FocusTriangle,
              ceOn == null ? $"对象 {Doc.Strokes.Count} 个（没画出来）"
                           : $"Kind {ceOn.Kind}、焦点三角形 {ceOn.FocusTriangle}（期望 true）");
        Check("椭圆（带焦点）：焦点 / 三角形走的是**主几何**（虚线辅助槽一笔都没有）",
              ceOn != null && ceOn.InkPieces().Count(pc => pc.Aux) == 0,
              ceOn == null ? "（没画出来）" : $"{ceOn.InkPieces().Count(pc => pc.Aux)} 笔 Aux（期望 0）");

        EnsureRailOpen();
        ClickSegment(ceSeg);                              // 再点一次 = 换到"无焦点三角形"
        Check("椭圆（带焦点）：第 2 次点 = 换到**无焦点三角形**（工具没变）",
              Host.State.Tool == Tool.ConicEllipse && !Host.State.EllipseFocusTriangle,
              $"工具 {Host.State.Tool}、档 {Host.State.EllipseFocusTriangle}（期望 False）");
        Check("椭圆（带焦点）：图标名跟着变成 ovalFocus",
              ui.ShapeIconNameForTest(ceSeg) == "ovalFocus", ui.ShapeIconNameForTest(ceSeg));
        Check("椭圆（带焦点）：档位点跟着移到第 2 个",
              ui.ShapePipsForTest(ceSeg) == (2, 1), $"{ui.ShapePipsForTest(ceSeg)}");

        Doc.Clear();
        Doc.ClearHistory();
        DragConicEllipse(ceX, ceY);
        var ceOff = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("椭圆（带焦点）·无三角形档：还是画两个焦点、只是不连那两条边（FocusTriangle = false）",
              ceOff != null && ceOff.Kind == StrokeKind.ConicEllipse && !ceOff.FocusTriangle,
              ceOff == null ? $"对象 {Doc.Strokes.Count} 个（没画出来）"
                            : $"Kind {ceOff.Kind}、焦点三角形 {ceOff.FocusTriangle}（期望 false）");
        EnsureRailOpen();
        ClickSegment(ceSeg);                              // 第三下 = 转回来（留给后面 C 段按默认档拖）
        Doc.Clear();
        Doc.ClearHistory();
        Host.Commands.SetColor(colorBeforeA4);            // 品红只借这一段用（见段首注释）

        // ================= A5. 坐标系那格：**再点一次 = 换一档（带网格 / 不带网格）** =========
        //
        // 用户 2026-09-24："那个更多里面现在有一个显示网格，我打算把它挪到图形里面的那个坐标系，
        // ……点一下切换成网格，点一下网格没了。" 于是这一档从「更多」抽屉那一行（**已经拿掉**）
        // 挪到图形面板，**和抛物线 / 直线 / 双曲线 / 椭圆同构**（同一个格再点一次换一档）。
        //
        // 盯四件事：
        //   ① 一轮两档（点第二下换过去，再点一下转回来）；② 图标名跟着换（`axes` / `axesGrid`）；
        //   ③ **档真的落到"下一笔画的坐标系"上**（真手势拖一个，看它的 `Grid`）；
        //   ④ 抽屉里**不再有**那一行（防"挪了没删"——那样两处都能改，用户会以为是两个开关）。
        // ⚠ ③ 之后那一下还要记一条引擎的**分派规则**（原来在抽屉那行上就有的）：
        //   **选中了坐标系 → 改的是选中的那些；没选中 → 才翻"新画的默认值"**。
        Console.WriteLine("  -- A5. 坐标系格：再点一次换一档（带网格 / 不带网格）--");
        GotoShapeBand();
        int axSeg = InkUi.FullUi.ShapeSegmentIndexForTest(Tool.Coordinate);   // 按工具名找（不写死段号）

        // 归零到"不带网格"那一档（最多三下：选中它 / 换一档 / 再换回来）
        for (int k = 0; k < 3
             && (Host.State.Tool != Tool.Coordinate || Host.State.CoordGridDefault); k++)
        {
            EnsureRailOpen();
            ClickSegment(axSeg);
        }
        Check("坐标系：点它 = 选中它，当前档 = **不带网格**（默认档）",
              Host.State.Tool == Tool.Coordinate && !Host.State.CoordGridDefault,
              $"工具 {Host.State.Tool}、档 {Host.State.CoordGridDefault}");
        Check("坐标系：不带网格那一档的图标名 = axes",
              ui.ShapeIconNameForTest(axSeg) == "axes", ui.ShapeIconNameForTest(axSeg));

        EnsureRailOpen();
        ClickSegment(axSeg);                              // 再点一次 = 换一档
        Check("坐标系：第 2 次点 = 换到**带网格**（工具没变）",
              Host.State.Tool == Tool.Coordinate && Host.State.CoordGridDefault,
              $"工具 {Host.State.Tool}、档 {Host.State.CoordGridDefault}（期望 True）");
        Check("坐标系：图标名跟着变成 axesGrid（同一张图多一层细格线）",
              ui.ShapeIconNameForTest(axSeg) == "axesGrid", ui.ShapeIconNameForTest(axSeg));

        // ③ 档落到"下一笔"：真手势拖一个坐标系出来（照 --axistest ① 那套鼠标动作）
        {
            Doc.Clear();
            Doc.ClearHistory();
            float ax0 = _virtualX + 700f, ay0 = _virtualY + 1500f, abw = 700f, abh = 500f;
            SendMouse((int)ax0, (int)ay0, 0);                              SettleFrames(60);
            SendMouse((int)ax0, (int)ay0, Native.MOUSEEVENTF_LEFTDOWN);    SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(ax0 + abw * i / 4f), (int)(ay0 + abh * i / 4f), 0);
                SettleFrames(30);
            }
            SendMouse((int)(ax0 + abw), (int)(ay0 + abh), Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
            var axOn = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("坐标系：带网格那一档画出来的坐标系**真的带网格**（Grid = true）",
                  axOn != null && axOn.Kind == StrokeKind.Coordinate && axOn.Grid,
                  axOn == null ? "没有对象" : $"Kind {axOn.Kind}、网格 {axOn.Grid}（期望 true）");
            // 网格比轴线细一半、淡一半：几何是**两段**（主几何只有轴线，网格走辅助槽）——
            // 这是 2026-09-24 "变细变淡" 的落点，也是"一个几何只能有一种描边"的直接后果。
            Check("坐标系：网格自成一笔身份、单独一段几何（细/淡就是这么落地的）",
                  axOn != null && axOn.InkPieces().Any(p => p.Grid)
                  && axOn.BuildAuxGeometry(Gfx.D2DFactory) != null,
                  axOn == null ? "没有对象"
                               : $"网格笔数 {axOn.InkPieces().Count(p => p.Grid)}、"
                                 + $"辅助几何 {(axOn.BuildAuxGeometry(Gfx.D2DFactory) != null ? "有" : "无")}");

            // 第 3 次点：⚠ 刚才画出来的那个**还选中着**（画完自动选中），于是按引擎的分派规则
            //（`ToggleSelectionGrid`：**选中了就改它们、没选中才翻"新画的默认值"**）
            // 这一下改的是**那个对象**，默认档不动。这正是老师的实际用法：
            // 画完发现"这个要格子" → 再点一下这一格就给它加上（日志会印"1 个对象 → 关"）。
            EnsureRailOpen();
            ClickSegment(axSeg);
            Check("坐标系：刚画完那个还选中着 → 这一点改的是**它**（默认档不动）",
                  axOn != null && !axOn.Grid && Host.State.CoordGridDefault,
                  $"对象网格 {axOn?.Grid}、默认档 {Host.State.CoordGridDefault}（期望 False / True）");

            // 第 4 下：**清掉选中**再点 → 才轮到"新画的默认值"（转回不带网格，留给后面的段）
            Doc.Clear();
            Doc.ClearHistory();
            Doc.SelectOnly(Array.Empty<Stroke>());
            SettleFrames(120);
            EnsureRailOpen();
            ClickSegment(axSeg);
            Check("坐标系：没选中任何东西时，这一点翻的是**新画的默认值**（转回不带网格）",
                  !Host.State.CoordGridDefault, $"档 {Host.State.CoordGridDefault}（期望 False）");
        }

        // ④ 「更多」抽屉里**不该再有**「坐标系网格」那一行（按标签找，找不到 = 空矩形）
        {
            var staleRow = ui.RowRectByLabelForTest("坐标系网格");
            Check("更多面板：**没有**「坐标系网格」那一行了（挪走了就删干净）",
                  staleRow.MaxY <= staleRow.MinY,
                  $"找到的行高 {staleRow.MaxY - staleRow.MinY:F0}（期望 0）");
        }

        // ================= B. 图形**一个热键都没有**（用户 2026-09-19 定）=================
        //
        // 用户原话："图形不需要加快捷键，通通取消掉"。原来给圆 / 三角形 / 平行四边形 /
        // 坐标系 / 数轴 配过 `Ctrl+Alt+O/T/G/F/N`，这一轮全撤——它们的入口就是上面那 8 段。
        //
        // 判据分两层，缺一层都可能"看着绿其实没撤干净"：
        //   ① **对表查**：那几个组合在任何作用域里都不许再绑着动作；
        //   ② **真按一次**：合成键盘发一个 `Ctrl+Alt+O`，工具**不许**变——
        //      这一层防的是"表里删了、注册那一路还留着"。
        //
        // ⚠ `Ctrl+Alt+T` 2026-09-30～10-04 曾借给穿透；穿透抬到 `Ctrl+Alt+Shift+T` 之后
        // 五个组合（O/T/G/F/N）全部退役，这里一起查。
        Console.WriteLine("  -- B. 图形一个热键都没有 --");
        var retired = new (string chord, ushort vk)[]
        {
            ("Ctrl+Alt+O", 'O'), ("Ctrl+Alt+T", 'T'), ("Ctrl+Alt+G", 'G'),
            ("Ctrl+Alt+F", 'F'), ("Ctrl+Alt+N", 'N'),
        };
        foreach (var (chord, vk) in retired)
        {
            var c = new KeyChord(KeyChord.ModCtrl | KeyChord.ModAlt, vk);
            var hit = Keys.Bindings.FirstOrDefault(b => b.Chord.Equals(c));
            Check($"退役键 {chord} 不再绑任何动作", hit == null,
                  hit == null ? "表里没有这一条"
                              : $"还绑着「{KeyMap.Describe(hit.Action)}」（{hit.Scope}）");
        }

        Host.Commands.SetTool(Tool.Line);
        SettleFrames(150);
        SendCtrlAlt('O');
        SettleFrames(400);
        Check("真按一次 Ctrl+Alt+O：工具一动不动（图形确实没有键了）",
              Host.State.Tool == Tool.Line,
              $"工具 = {Host.State.Tool}（期望 Line）");

        // ================= C. **每一个有入口的图形**都真拖一笔，画出来就是那一种 =================
        //
        // ⚠ 这一段原来是**抽查 6 个**（"每次拖拽要等几帧，省时间"），结果正好漏在
        // 棱锥 / 棱台身上（用户："锥体和台体只能画四棱"）。省下的那几秒，换来的是
        // **一个图形入口整整没走过一遍**——不值。现在**逐段全拖**：
        // 判据表就是上面那张 `want`（有入口的那些），一个都跑不掉。
        //
        // 两件事**不再由人写**（写了就会跟不上）：
        //   · **拖几笔** → 问引擎 `ShapeStepCount(tool)`（它是从那张三笔表算出来的）；
        //   · **段号** → 问界面 `ShapeSegmentIndexForTest(tool)`。
        Console.WriteLine($"  -- C. 逐个图形真拖一笔（{want.Length} 个）：StrokeKind 就是那一种 --");
        // 落点在画布中上部：面板在屏幕下沿，别画到面板上（那就变成点按钮了）。
        //
        // ⚠ 格子是"每行 4 个、行距 120"**从上面往下排**的，所以**图形越多、最后几个越靠下** ——
        // 2026-09-20 第十六批加到 21 个时，最后那格正好排进了面板（症状是"对象 0 个"，
        // 看着像功能坏了）。所以起点取在画布 30% 高处、行距收紧到 120：
        // 21 个图形最底下一格的落点是 `0.30 × 1800 ＋ 5 × 120 ＝ 1140`，
        // 再加拖出去的 120 也才 1260，离下沿的面板还有一大截。
        int dragX = (int)(_virtualX + _virtualW * 0.28f);
        int dragY = (int)(_virtualY + _virtualH * 0.30f);
        for (int i = 0; i < want.Length; i++)
        {
            var d = want[i];
            Doc.Clear();
            Doc.ClearHistory();
            GotoShapeBand();
            ClickSegment(InkUi.FullUi.ShapeSegmentIndexForTest(d.tool));

            // 一个图形一格（互相别叠上），按引擎说的**笔数**一笔一笔拖
            int x = dragX + (i % 4) * 260, y = dragY + (i / 4) * 120;
            int steps = ShapeStepCount(d.tool);
            for (int k = 0; k < steps; k++)
            {
                // 第 1 笔拖 260×120（比"太短不产生对象"的阈值大得多）；第 2 笔（多笔图形的
                // "方向 / 深度 / 顶上那个中心"）拖 160×40——和 --prismtest 那套最短两笔同一个口径。
                int dx = k == 0 ? 260 : 160, dy = k == 0 ? 120 : 40;
                SendMouse(x, y, 0);                                    SettleFrames(60);
                SendMouse(x, y, Native.MOUSEEVENTF_LEFTDOWN);           SettleFrames(60);
                SendMouse(x + dx / 2, y + dy / 2, 0);                   SettleFrames(40);
                SendMouse(x + dx, y + dy, 0);                           SettleFrames(40);
                SendMouse(x + dx, y + dy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(k + 1 < steps ? 120 : 220);
            }

            var s = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            var kindWant = KindOfShapeTool(d.tool);      // 期望值**从引擎问**，不再表里再抄一遍
            Check($"「{d.name}」：点段选的工具、拖 {steps} 笔，画出来就是它",
                  Host.State.Tool == d.tool && s != null && s.Kind == kindWant,
                  $"工具 = {Host.State.Tool}，对象 {Doc.Strokes.Count} 个，"
                  + $"Kind = {(s == null ? "（一个对象都没有）" : s.Kind.ToString())}"
                  + $"（期望 {kindWant}）");
        }
        Doc.Clear();
        Doc.ClearHistory();

        // ================= D. 主条那一格的图标跟着种类变 =================
        Console.WriteLine("  -- D. 主条「图形」那一格的图标跟着当前种类变 --");
        var icons = new List<string>();
        foreach (var (tool, name) in want)
        {
            Host.Commands.SetTool(tool);
            SettleFrames(120);
            string icon = ui.CellIconForTest(8);
            bool changed = icons.Count == 0 || icon != icons[icons.Count - 1];
            Check($"「{name}」那一格的图标 = {icon}", changed,
                  changed
                      ? $"（上一种是 {(icons.Count == 0 ? "还没切过" : icons[icons.Count - 1])}）"
                      : $"和上一种「{icons[icons.Count - 1]}」是同一张图——切了种类图标没跟着变");
            icons.Add(icon);
        }
        Check("十二种图形的图标两两不同",
              icons.Distinct().Count() == want.Length, string.Join(" → ", icons));

        // ---- D2. 没选中图形工具时，这一格画的是**通用图标** ----
        //
        // 用户 2026-09-22 定："图形图标在未选中时，默认使用最初始的圆框（Fluent 官方
        // 通用的图标）；当选中它并再选中一个具体图形时，图标就显示为矩形。"
        // 判据必须和绘制同一条（`FullUi` 里那句 `active ? ShapeIcon : Cells[8].Icon`），
        // 所以这里挑几个**不是图形**的工具，逐个问一遍——它们都不该画成某一种具体图形。
        Console.WriteLine("  -- D2. 没选中图形工具 → 这一格回到通用图标 --");
        foreach (var t in new[] { Tool.Pen, Tool.Eraser, Tool.Marquee, Tool.Capture })
        {
            Host.Commands.SetTool(t);
            SettleFrames(120);
            string icon = ui.CellIconForTest(8);
            Check($"手里是{t}时，图形那一格画通用图标",
                  icon == "shapes", $"画的是 {icon}（期望 shapes）");
        }
        // 再切回一种图形：必须**立刻**回到那一种（不是"切走了就再也不跟着变"）。
        Host.Commands.SetTool(Tool.Rectangle);
        SettleFrames(120);
        Check("切回矩形：图标立刻跟着回到矩形",
              ui.CellIconForTest(8) == "square", $"画的是 {ui.CellIconForTest(8)}（期望 square）");
        // 这一段是**真渲染过的**：图标名一旦拼错，`IconAtlas.Draw` 会当场抛
        // （那条"绝不画一个空图标了事"的规矩），所以它同时就是
        // "自绘的图标（oval / parallelogram / axes / parabola / hyperbola / sine / cosine
        // 那几张）画得出来"的判据——它们在图标库里不存在，
        // `ShapeIconFor` 里名字写错了只会在这里露出来。

        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }


    /// <summary>
    /// 界面输入通路的真机自检：把"面板内 / 面板外 × 穿透开 / 穿透关 × 界面吃不吃"
    /// 这几种组合各合成一次**真实点击**，然后看**到底谁收到了**。
    ///
    /// 四类判定（这就是引擎与界面之间那四条规矩）：
    ///
    ///   · 命中界面矩形、界面消费 → 归界面（画布不落墨，穿透也不交下层）
    ///   · 命中界面矩形、界面没消费 → 画布不落墨；穿透时交下层
    ///   · 没命中界面矩形 → 穿透时交下层，否则照常落墨
    ///   · 悬停同样要转发给界面（按钮高亮靠它），坐标必须是**逻辑屏幕**坐标
    ///
    /// 为什么非要真窗口 + 真点击：这一类 bug 全在"系统命中测试 → WM_NCHITTEST →
    /// WM_POINTER* → 引擎"这条路上，纯函数自检与不接引擎的假面板**都看不见**。
    /// 合成输入走的就是真实的那条路。
    /// </summary>
    private void UiInputTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 界面输入通路自检（合成真实点击，看谁收到）===");

        if (SkipIfNoSyntheticInput("界面输入通路自检")) { _quit = true; return; }

        // 目标窗口：另一个进程里的一块普通窗口，被我们的覆盖层压着。
        string dir = Path.Combine(Path.GetTempPath(), "inkprobe_uitest");
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, $"target-{Environment.ProcessId}.txt");   // 同 --passtest：按进程号分开
        File.WriteAllText(log, "");

        var psi = new ProcessStartInfo(Environment.ProcessPath, $"--clicktarget \"{log}\"")
        {
            UseShellExecute = false,
        };
        var target = Process.Start(psi);
        if (target == null)
        {
            Console.WriteLine("  无法启动点击目标进程");
            _quit = true;
            return;
        }

        for (int i = 0; i < 120 && !ReadTextShared(log, 200).Contains("ready"); i++)
            Thread.Sleep(50);
        if (!ReadTextShared(log, 200).Contains("ready"))
        {
            Console.WriteLine("  点击目标窗口没有就绪");
            try { target.Kill(); } catch { }
            _quit = true;
            return;
        }

        // 面板盖住目标窗口的中段：这样"面板内"和"面板外"两个落点都在同一个
        // 下层窗口里，唯一的差别就是有没有被界面接住。
        const int twX = 120, twY = 120, twW = 420, twH = 320;
        int cx = twX + twW / 2, cy = twY + twH / 2;
        int panelX = cx - 140, panelW = 280, panelH = 140;

        var probe = new UiProbe
        {
            BoundsPhysical = new RectF
            {
                MinX = panelX, MinY = cy - panelH / 2,
                MaxX = panelX + panelW, MaxY = cy + panelH / 2,
            },
        };
        SetUi(probe);
        Tool = Tool.Pen;
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(200);

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-26} {detail}");
        }

        // 浮层尺寸 token（8.2.0 验收①）——和 `--selftest` 共用同一份判据
        //（计划里点名"补在 --uitest 里"，那边跑到的就是这个）。
        PrintOverlayMetrics();
        CheckFloatOverlayTokens((n, ok, d) => Check(n, ok, d));

        void Click(int x, int y)
        {
            SendMouse(x, y, 0);                            SettleFrames(80);
            SendMouse(x, y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse(x, y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(320);
        }

        void SetPass(bool on)
        {
            PassMode = PassThroughMode.LayeredTransparent;
            PassThrough = on;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
            SettleFrames(150);
        }

        // 三个落点：都在目标窗口的**客户区**里（窗口有边框和标题栏，
        // 点在标题栏上系统不会发 WM_LBUTTONDOWN，下层就"收不到点击"了），
        // 差别只在面板/界面怎么处理。
        int inX = cx - 80, inY = cy;             // 面板左半：界面会消费
        int holeX = cx + 80, holeY = cy;         // 面板右半：界面返回 false
        int outX = cx, outY = cy + 140;          // 面板下沿之外、客户区内

        // ---- ① 悬停要能到界面 ----
        SetPass(false);
        probe.Move = 0;
        SendMouse(inX, inY, 0);
        SettleFrames(200);
        Check("悬停转发到界面", probe.Move > 0, $"界面收到 {probe.Move} 次移动");

        // ---- ② 悬停坐标必须是"逻辑屏幕"，不能是画布坐标 ----
        // 判法：同一个物理点，相机滚过之后再悬停一次，界面看到的坐标不该变。
        float y0 = probe.LastY;
        ViewOffsetY = -300f;
        SettleFrames(150);
        SendMouse(inX, inY + 6, 0);              // 错开一点，确保真的有 WM_POINTERUPDATE
        SettleFrames(200);
        float y1 = probe.LastY;
        bool coordsUsable = !float.IsNaN(y0) && !float.IsNaN(y1);
        bool drift = coordsUsable && MathF.Abs(y1 - y0) > 4f;
        Check("悬停坐标不受相机影响", coordsUsable && !drift,
              coordsUsable
                  ? $"滚前 y={y0:F0}，滚后 y={y1:F0}（差 {MathF.Abs(y1 - y0):F0} 像素）"
                  : "界面根本没收到悬停，这一项无从判定");
        ViewOffsetY = 0f;
        SettleFrames(150);

        // ---- ③ 不穿透：面板内点击归界面，且不许画出一笔 ----
        int strokes0 = Doc.Strokes.Count;
        int clicks0 = CountClicks(log);
        probe.Down = 0;
        Tool = Tool.Pen;
        Click(inX, inY);
        bool uiGot = probe.Down > 0;
        Check("不穿透·点面板归界面", uiGot, $"界面收到 {probe.Down} 次按下");
        Check("不穿透·点面板不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");
        Check("不穿透·点面板不传下层", CountClicks(log) == clicks0, "下层窗口没收到点击");
        Check("界面的命令真的到了引擎", probe.LastToolFromState == Tool.Eraser,
              $"界面按钮把工具切成了 {probe.LastToolFromState}");

        // ---- ④ 不穿透：界面"看见了但不吃"的那一半也不该落墨 ----
        Tool = Tool.Pen;
        strokes0 = Doc.Strokes.Count;
        Click(holeX, holeY);
        Check("不穿透·面板空洞不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑤ 不穿透：面板外照常画线 ----
        Tool = Tool.Pen;
        strokes0 = Doc.Strokes.Count;
        clicks0 = CountClicks(log);
        Click(outX, outY);
        Check("不穿透·面板外照常落墨", Doc.Strokes.Count > strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");
        Check("不穿透·面板外不传下层", CountClicks(log) == clicks0, "下层窗口没收到点击");

        // ---- ⑥ 穿透：面板内点击必须由界面收下（这就是这次要修的那条）----
        SetPass(true);

        // 穿透下悬停也要到界面：这是"看 PPT 时随手叫出笔"的同一组合。
        probe.Move = 0;
        SendMouse(inX, inY, 0);
        SettleFrames(220);
        Check("穿透·悬停到界面", probe.Move > 0, $"界面收到 {probe.Move} 次移动");

        clicks0 = CountClicks(log);
        strokes0 = Doc.Strokes.Count;
        probe.Down = 0;
        Tool = Tool.Pen;
        Click(inX, inY);
        Check("穿透·点面板归界面", probe.Down > 0, $"界面收到 {probe.Down} 次按下");
        Check("穿透·面板不许穿到下层", CountClicks(log) == clicks0,
              $"下层窗口收到 {CountClicks(log) - clicks0} 次点击（应为 0）");
        Check("穿透·点面板不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑦ 穿透：面板是界面的地盘，"看见但不吃"也不许漏给下层 ----
        // （面板要放行某个位置，得它自己别把那一块算进 QueryBounds；
        //   将来若真需要"面板中间挖个洞让点击穿过去"，再加一个逐点命中回调。）
        clicks0 = CountClicks(log);
        strokes0 = Doc.Strokes.Count;
        Click(holeX, holeY);
        Check("穿透·面板空洞不漏给下层", CountClicks(log) == clicks0,
              $"下层窗口收到 {CountClicks(log) - clicks0} 次点击（应为 0）");
        Check("穿透·面板空洞不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑧ 穿透：面板外交下层 ----
        clicks0 = CountClicks(log);
        strokes0 = Doc.Strokes.Count;
        Click(outX, outY);
        Check("穿透·面板外交下层", CountClicks(log) > clicks0,
              $"下层窗口收到 {CountClicks(log) - clicks0} 次点击（应 ≥ 1）");
        Check("穿透·面板外不落墨", Doc.Strokes.Count == strokes0,
              $"笔画 {strokes0} → {Doc.Strokes.Count}");

        // ---- ⑨ 界面驱动的动画必须拿到连续帧，停下之后必须回到零帧 ----
        // 判据用的是引擎自己的 NeedsFrame()：测的和跑的是同一个判据。
        SetPass(false);
        SettleFrames(150);
        probe.AnimateUntilMs = NowMs + 220;

        int framesDuring = 0;
        var swAnim = Stopwatch.StartNew();
        while (swAnim.ElapsedMilliseconds < 240)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; framesDuring++; }
            else Thread.Sleep(1);
        }
        Check("界面驱动的动画拿到连续帧", framesDuring >= 5,
              $"220 毫秒里出了 {framesDuring} 帧（60 fps 下约 13 帧）");

        // 先落地一帧把脏区清干净，再看安静期是不是真的 0 帧
        PumpMessages();
        RenderAll();
        _dirty = false;
        int framesQuiet = 0;
        var swQuiet = Stopwatch.StartNew();
        while (swQuiet.ElapsedMilliseconds < 150)
        {
            PumpMessages();
            if (NeedsFrame()) { RenderAll(); _dirty = false; framesQuiet++; }
            else Thread.Sleep(2);
        }
        Check("动画结束后回到零帧", framesQuiet == 0, $"安静 150 毫秒出了 {framesQuiet} 帧");

        // ---- ⑩ 命令通道：新加的三条命令走一遍，状态读得回来 ----
        // 这段走的是**界面能看到的那条路**（IUiHost.Commands → IEngineCommands → 引擎），
        // 不是直接改引擎字段——否则测的就不是"契约通不通"。
        var host = Host;
        host.Commands.SetSelectMode(SelectMode.Lasso);
        Check("命令·切套索", host.State.SelectMode == SelectMode.Lasso,
              $"读回 {host.State.SelectMode}");
        host.Commands.SetSelectMode(SelectMode.Rect);

        var green = InkPalette.BoardPresets[1].Color;
        host.Commands.SetBoardColor(green);
        Check("命令·换板色", host.State.BoardColor.Equals(green), "读回绿板");
        host.Commands.SetBoardColor(InkPalette.BoardPresets[0].Color);

        Doc.Clear();
        Doc.ClearHistory();
        var only = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        only.AddPoint(400, 400, 1f, 0);
        only.AddPoint(620, 400, 1f, 1);
        Doc.AddStroke(only);
        Tool = Tool.Pen;
        host.Commands.SelectAll();
        Check("命令·全选", Doc.Selected.Count == 1 && Tool == Tool.Marquee,
              $"选中 {Doc.Selected.Count} 条，工具={Tool}");
        Doc.Clear();
        Doc.ClearHistory();

        // ---- ⑪ 界面看到的"屏幕"必须和 IUiHost.Screen 是同一个 ----
        // 单屏上这两者本来就相等（覆盖窗口 == 虚拟桌面），所以这一条是**回归护栏**：
        // 一旦有人把 Layout 的实参改回"本窗口那一块显示器"，双屏上才会露馅，
        // 而在没有双屏的机器上，护栏能立刻发现它不等了。
        Check("Layout 与 Screen 同一套坐标",
              probe.LastLayoutScreen.Equals(host.Screen),
              $"Layout ({probe.LastLayoutScreen.MinX:F0},{probe.LastLayoutScreen.MinY:F0})-"
              + $"({probe.LastLayoutScreen.MaxX:F0},{probe.LastLayoutScreen.MaxY:F0})  vs  "
              + $"Screen ({host.Screen.MinX:F0},{host.Screen.MinY:F0})-"
              + $"({host.Screen.MaxX:F0},{host.Screen.MaxY:F0})");

        // ---- ⑫ 界面抛异常不许留死局：重建 → 重启 → 兜底，而且板书不许丢 ----
        // 教室大屏/手写板上可能根本没有键盘，界面是唯一的出口：界面没了又关不掉、
        // 重启不了，就是死局（用户 2026-09-16 提的）。
        Recovery.ClearRestartCount();          // 别让上一次自检留下的计数影响这一次
        try { File.Delete(Recovery.SessionPath); } catch { }

        int uiBuilt = 0;
        var panelRect = probe.BoundsPhysical;
        UiProbe MakeProbe()
        {
            uiBuilt++;
            return new UiProbe { BoundsPhysical = panelRect };
        }
        SetUiFactory(MakeProbe);               // 引擎从此有"再造一个回来"的办法
        SettleFrames(200);

        // ① 第一次崩：3 秒内 3 次 → 重建界面（不是停用）
        var firstUi = (UiProbe)CurrentUi;
        firstUi.ThrowOnDown = true;
        for (int i = 0; i < 3; i++) { probe.Down = 0; Click(inX, inY); }
        bool rebuilt = CurrentUi is UiProbe && !ReferenceEquals(CurrentUi, firstUi);
        Check("界面崩了先重建（不停用）", rebuilt,
              $"重建次数 {uiBuilt - 1}，当前界面 = {CurrentUi.Name}");

        // ② 重建之后继续崩（重建额度用完）→ 重启软件，且重启前把板书存下来
        Doc.Clear();
        Doc.ClearHistory();
        var keep = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        keep.AddPoint(400, 400, 1f, 0);
        keep.AddPoint(700, 400, 1f, 1);
        Doc.AddStroke(keep);
        int strokesBeforeRestart = Doc.Strokes.Count;

        for (int round = 0; round < 2; round++)
        {
            var ui = CurrentUi as UiProbe;
            if (ui == null) break;
            ui.ThrowOnDown = true;
            for (int i = 0; i < 3; i++) { probe.Down = 0; Click(inX, inY); }
            SettleFrames(120);
        }
        Check("重建救不回来就重启软件", RestartRequested, $"当前界面 = {CurrentUi.Name}");

        // 板书必须读得回来——"重启"的前提是不丢东西
        var blob = Recovery.TakeSession();
        bool restorable = false;
        string detail = "没有暂存文件";
        if (blob != null)
        {
            var fresh = new InkDocument();
            InkSerializer.LoadInto(fresh, blob);
            restorable = fresh.Strokes.Count == strokesBeforeRestart && strokesBeforeRestart > 0;
            detail = $"暂存 {blob.Length} 字节 → 读回 {fresh.Strokes.Count} 笔（重启前 {strokesBeforeRestart} 笔）";
        }
        Check("重启后板书读得回来", restorable, detail);
        try { File.Delete(Recovery.SessionPath); } catch { }
        Recovery.ClearRestartCount();

        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        ExitCode = fail == 0 ? 0 : 1;

        // ---- 诊断（不计红绿）：穿透的两种实现方式各自牺牲了什么 ----
        // "面板可点"和"点击透给下层"在现在的实现里是互斥的：
        //   · 只改 WM_NCHITTEST（HitTest 档）：面板能收到，但下层收不到
        //     —— HTTRANSPARENT 只在**同一个线程内**继续往下找窗口；
        //   · LAYERED+TRANSPARENT 档：下层收得到，但这个窗口在系统那一层
        //     就被排除在输入之外了，NCHITTEST 根本轮不到我们，面板点不动。
        // 这段打印就是为了把这条取舍钉成数字，选架构时不用再猜。
        // 上面的熔断自检留着"故意抛异常"的界面，诊断这几下点击会继续触发重建/重启
        // （还会往临时目录写会话文件）。先把开关关掉，让这段只回答它要回答的问题。
        var diagUi = CurrentUi as UiProbe;
        if (diagUi != null) diagUi.ThrowOnDown = false;

        foreach (var (modeName, mode) in new[]
                 {
                     ("只按点回答命中测试", PassThroughMode.HitTest),
                     ("LAYERED+TRANSPARENT", PassThroughMode.LayeredTransparent),
                 })
        {
            PassMode = mode;
            PassThrough = true;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
            SettleFrames(150);

            if (diagUi != null) diagUi.Down = 0;
            int c0 = CountClicks(log);
            int nc0 = _cntNcHitTest, ncc0 = _cntNcHitClient;
            int dn0 = _cntDown;
            Click(inX, inY);
            Console.WriteLine($"  [诊断] {modeName,-24} 面板收到按下 {(diagUi != null && diagUi.Down > 0 ? "是" : "否")}"
                              + $"   下层窗口收到 {(CountClicks(log) > c0 ? "是" : "否")}"
                              + $"   系统问了命中测试 {_cntNcHitTest - nc0} 次"
                              + $"（其中答「这块是我的」{_cntNcHitClient - ncc0} 次）"
                              + $"   引擎收到指针按下 {_cntDown - dn0} 次");
            PassThrough = false;
            foreach (var w in _windows) ApplyPassThroughStyle(w);
            SettleFrames(120);
        }
        try { File.Delete(Recovery.SessionPath); } catch { }
        Recovery.ClearRestartCount();

        SetPass(false);
        try { target.Kill(); } catch { }
        _quit = true;
    }

    /// <summary>
    /// 产品界面（`src/InkUi` 的 `FullUi`）自检：球 ↔ 按钮带这条最小闭环。
    ///
    /// 这一步故意只验四件事——**坐标 / DPI / 脏区 / 输入拦截**：
    /// 它们在假面板里验证不到（假面板自成一个窗口、自带坐标系），而恰恰是接引擎
    /// 最容易错的地方。这四件对了，后面搬色带、滑块、抽屉都只是堆代码。
    ///
    /// 挂法用的是 `SetUiFactory`——产品的挂法。界面崩了引擎要能自己再造一个，
    /// 没工厂就只能一路走到重启（见 计划-底层对接界面.md 4.5）。
    /// </summary>
    /// <summary>
    /// 把界面挂上、展开、出图（给人看的，不判红绿）。
    /// 出图这条链子是这个仓库一贯的验收方式：观感的事眼睛说了算，数字只负责证明没坏。
    /// </summary>
    /// <summary>出图：**截屏图标候选**（用户 2026-09-17："截图图标和选中图标一样的，是不是不大好？"）。</summary>


    /// <summary>
    /// 键位自检：默认表、解析、冲突检测、落盘读回、真机注册、批注内真按一次键。
    ///
    /// 为什么值得单独写一条：键位是"用户的设置"，出错的方式特别隐蔽——
    /// 键被别的程序占了（按下去没反应）、改键撞了车（按下去触发另一件事）、
    /// 配置文件写坏了（下次启动直接崩）。这三类都在这里验。
    /// </summary>
    private void KeyTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 键位自检（表 / 冲突 / 落盘 / 真机注册）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        // ---- 1. 默认表 ----
        var map = KeyMap.Default();
        var problems = map.Validate();
        Check("默认表内部没有矛盾", problems.Count == 0,
              problems.Count == 0 ? "无" : string.Join("；", problems));

        int nGlobal = map.For(KeyScope.Global).Count();
        int nAnno = map.For(KeyScope.Annotation).Count();
        // 条数**不再写死**：加一个工具就会多一条，写死只会让这条自检天天报假警
        // （"22 项通过、1 项不对"里那一项就是它）。真正的不变量是下面两条：
        // 全局一律带修饰键、编辑类动作不在全局。这里只保证两档都非空、
        // 且每一条都真的绑上了键。
        Check("作用域划分：两档都非空且每条都有键",
              nGlobal > 0 && nAnno > 0 && map.Bindings.All(b => b.Chord.IsValid),
              $"全局 {nGlobal} 条，批注内 {nAnno} 条");
        Check("全局键一律带修饰键（不会抢走正常打字）",
              map.For(KeyScope.Global).All(b => b.Chord.HasModifier), "");
        Check("编辑类动作只在批注内（没有全局 Ctrl+Z 这种）",
              !map.For(KeyScope.Global).Any(b => b.Action == KeyAction.Redo
                    || b.Action == KeyAction.SelectAll || b.Action == KeyAction.DeleteSelected
                    || b.Action == KeyAction.NudgeLeft), "");

        // ---- 1.5 2026-10-05 新增键位：白板开关 / 选中缩放 / 重做别名 ----
        bool eqOk = KeyChord.TryParse("Ctrl+=", out var eqKey, out _);
        bool mnOk = KeyChord.TryParse("Ctrl+-", out var mnKey, out _);
        Check("解析 `Ctrl+=` / `Ctrl+-` 并原样打印",
              eqOk && eqKey.ToString() == "Ctrl+=" && mnOk && mnKey.ToString() == "Ctrl+-",
              $"Ctrl+= → {eqKey}，Ctrl+- → {mnKey}");
        {
            var anno = KeyScope.Annotation;
            string ChordOf(KeyAction a) => map.Find(anno, a)?.Chord.ToString() ?? "×";
            bool newKeys = ChordOf(KeyAction.ToggleBoard) == "Ctrl+B"
                        && ChordOf(KeyAction.ScaleUp) == "Ctrl+="
                        && ChordOf(KeyAction.ScaleDown) == "Ctrl+-"
                        && (map.KeyText(KeyAction.Redo) ?? "").Contains("Ctrl+Shift+Z");
            Check("新键位：Ctrl+B 白板 / Ctrl+= 放大 / Ctrl+- 缩小 / Ctrl+Shift+Z 重做别名",
                  newKeys,
                  $"白板 {ChordOf(KeyAction.ToggleBoard)}，缩放 {ChordOf(KeyAction.ScaleUp)} / {ChordOf(KeyAction.ScaleDown)}，"
                  + $"重做 {map.KeyText(KeyAction.Redo)}");
        }

        // **全局只留最最常用的那几个**（用户 2026-09-19："除了最最最常用的功能需要全局热键以外，
        // 其他的通通换成应用内快捷键就行了"）。判据取自 `KeyBindings.GlobalAllowed`
        // ——那份名单是**产品的规则**，不是自检自己写的一份：两边各写一份早晚会分叉
        // （这条仓库里踩过三次）。这里只负责"表里不许出现名单之外的动作"。
        var allowedGlobal = new HashSet<KeyAction>(KeyMap.GlobalAllowedActions);
        var stray = map.For(KeyScope.Global).Select(b => b.Action).Where(a => !allowedGlobal.Contains(a)).ToList();
        Check($"全局里没有名单（{allowedGlobal.Count} 个）之外的动作",
              stray.Count == 0,
              stray.Count == 0
                  ? $"全局 {nGlobal} 条，全是：{string.Join(" / ", allowedGlobal.Select(KeyMap.Describe))}"
                  : "多出来：" + string.Join("、", stray.Select(KeyMap.Describe)));

        // 图形**一个键都没有**（用户 2026-09-19："图形不需要加快捷键，通通取消掉"）。
        // 查法是对着"退役的那五个组合"查，不是查动作名——动作枚举里已经没有图形那几个了，
        // 查名字等于什么都没查。
        //
        // ⚠ `Ctrl+Alt+T` 2026-09-30～10-04 曾借给穿透；2026-10-04 穿透整体抬到
        // `Ctrl+Alt+Shift+T` 之后它重新空出来，所以这次把五个组合一起放回来查。
        foreach (var (name, vk) in new[] { ("O", 'O'), ("T", 'T'), ("G", 'G'), ("F", 'F'), ("N", 'N') })
        {
            var c = new KeyChord(KeyChord.ModCtrl | KeyChord.ModAlt, vk);
            Check($"退役的图形键 Ctrl+Alt+{name} 不在任何作用域里",
                  map.Bindings.All(b => !b.Chord.Equals(c)), "");
        }
        // 顺手钉住 2026-10-04 用户定的新形状（哪天有人换回去，这里会给出说得清的红）：
        // ① 两个老全局键都加上了 Shift；② 呼出盘升到全局、批注内不再留 Ctrl+Q 副本。
        Check("穿透默认键 = Ctrl+Alt+Shift+T（2026-10-04 起统一加 Shift）",
              map.Find(KeyScope.Global, KeyAction.TogglePassThrough).Chord.ToString() == "Ctrl+Alt+Shift+T",
              map.Find(KeyScope.Global, KeyAction.TogglePassThrough).Chord.ToString());
        Check("呼出盘已升为全局键 Ctrl+Alt+Shift+Q，批注内不再留副本",
              map.Find(KeyScope.Global, KeyAction.RadialPalette)?.Chord.ToString() == "Ctrl+Alt+Shift+Q"
              && map.Find(KeyScope.Annotation, KeyAction.RadialPalette) == null,
              map.Find(KeyScope.Global, KeyAction.RadialPalette)?.Chord.ToString() ?? "全局里没有");

        // ---- 2. 按键解析 ----
        bool ok1 = KeyChord.TryParse("ctrl+alt+p", out var c1, out _);
        Check("解析 Ctrl+Alt+P（大小写不敏感）", ok1 && c1.ToString() == "Ctrl+Alt+P", c1.ToString());
        bool ok2 = KeyChord.TryParse("Delete", out var c2, out _);
        Check("解析 Delete", ok2 && c2.Vk == 0x2E, $"VK={c2.Vk:X2}");
        bool ok3 = KeyChord.TryParse("Shift+Left", out var c3, out _);
        Check("解析 Shift+Left", ok3 && c3.Vk == 0x25 && (c3.Modifiers & KeyChord.ModShift) != 0,
              c3.ToString());
        bool ok4 = KeyChord.TryParse("Ctrl+月亮", out _, out string err4);
        Check("认不出的键名要报错（不是静默忽略）", !ok4 && err4 != null, err4);
        bool ok5 = KeyChord.TryParse("Ctrl+", out _, out string err5);
        Check("只有修饰键要报错", !ok5 && err5 != null, err5);

        // ---- 3. 冲突检测 ----
        // 样本用**穿透模式开关**：它是全局里必留的那几条之一（现在全局只有
        // 穿透/呼出盘/退出，笔和橡皮都降到批注内了——拿 ToolPen 当样本会以
        // "这个作用域里没有这个动作"直接失败，冲突检测那一条就成了假通过）。
        KeyChord.TryParse("Ctrl+Alt+Shift+X", out var takenChord, out _);     // 被"退出"占着
        bool taken = map.TrySet(KeyScope.Global, KeyAction.TogglePassThrough, takenChord, out string errTaken);
        Check("撞了别人的键要拒绝并说清是谁", !taken && errTaken != null
              && errTaken.Contains("退出"), errTaken);

        KeyChord.TryParse("F5", out var noMod, out _);
        bool bare = map.TrySet(KeyScope.Global, KeyAction.TogglePassThrough, noMod, out string errBare);
        Check("全局热键没有修饰键要拒绝", !bare && errBare != null, errBare);

        KeyChord.TryParse("Ctrl+Alt+F5", out var free, out _);
        bool moved = map.TrySet(KeyScope.Global, KeyAction.TogglePassThrough, free, out string errMove);
        Check("没冲突就能改，并标记成脏",
              moved && map.Dirty && map.Find(KeyScope.Global, KeyAction.TogglePassThrough).Chord.Equals(free),
              $"穿透 → {map.Find(KeyScope.Global, KeyAction.TogglePassThrough).Chord}");

        map.ResetToDefault(KeyScope.Global, KeyAction.TogglePassThrough);
        Check("能恢复默认键",
              map.Find(KeyScope.Global, KeyAction.TogglePassThrough).Chord.ToString() == "Ctrl+Alt+Shift+T",
              map.Find(KeyScope.Global, KeyAction.TogglePassThrough).Chord.ToString());

        // ---- 4. 落盘 / 读回 / 坏文件 ----
        string cfg = Path.Combine(Path.GetTempPath(), "inkteach-keytest.json");
        InkSettings.PathOverride = cfg;
        try
        {
            KeyChord.TryParse("Ctrl+Alt+F12", out var f12, out _);
            map.TrySet(KeyScope.Global, KeyAction.Quit, f12, out _);
            InkSettings.Save(map);
            Check("写盘后文件真的存在", File.Exists(cfg), cfg);

            var reloaded = KeyMap.Default();
            var warns = InkSettings.Load(reloaded);
            Check("读回无警告，且改动生效",
                  warns.Count == 0 && reloaded.Find(KeyScope.Global, KeyAction.Quit).Chord.Equals(f12),
                  warns.Count == 0 ? $"退出键 → {reloaded.Find(KeyScope.Global, KeyAction.Quit).Chord}"
                                   : string.Join("；", warns));
            Check("没改过的项仍是默认值（只写差异）",
                  reloaded.Find(KeyScope.Global, KeyAction.TogglePassThrough).Chord.ToString() == "Ctrl+Alt+Shift+T", "");
            Check("只写差异：文件里应当只有 1 条", File.ReadAllText(cfg).Split('\n')
                  .Count(l => l.Contains("\"Global.")) == 1, "");

            File.WriteAllText(cfg, "{ \"keys\": { \"Global.Quit\": \"Ctrl+Alt+\" } }");
            var broken = KeyMap.Default();
            var warns2 = InkSettings.Load(broken);
            Check("键名写坏了：报警告 + 用默认值 + 不抛异常",
                  warns2.Count > 0 && broken.Find(KeyScope.Global, KeyAction.Quit).Chord.ToString() == "Ctrl+Alt+Shift+X",
                  warns2.Count > 0 ? warns2[0] : "没有警告（不该）");

            File.WriteAllText(cfg, "这不是 JSON，只是一段乱码");
            var garbage = KeyMap.Default();
            var warns3 = InkSettings.Load(garbage);
            Check("整个文件是垃圾：报警告 + 仍能启动",
                  warns3.Count > 0 && garbage.Validate().Count == 0,
                  warns3.Count > 0 ? warns3[0] : "没有警告（不该）");
        }
        finally
        {
            try { File.Delete(cfg); } catch { }
            InkSettings.PathOverride = null;
        }

        // ---- 5. 真机注册结果 ----
        Check("全局热键注册数量与表一致", HotkeysRegistered == nGlobal,
              $"{HotkeysRegistered}/{nGlobal} 注册成功"
              + (Keys.GlobalFailures.Count > 0 ? "；失败：" + string.Join("；", Keys.GlobalFailures) : ""));

        // ---- 6. 快捷键总表：文档不许落后于代码 ----
        //
        // 用户的要求是"同一个功能的不同快捷键都要记录全，以后每次修改都记录"。
        // 光靠自觉迟早会漏，所以这里把**代码里的每一条绑定**拿去文档里查：
        // 要求**键和动作名出现在同一行**——只要求"这两个字符串各自出现过"太弱了
        // （把键换了但忘了改文档，照样能通过）。
        // 拿**默认表**去核对，不是拿这一轮被测试改过的 map：文档记的是程序**出厂默认键**，
        // 用户自己改的键活在 settings.json 里（上面刚验过落盘读回）。用 map 去比，
        // 会把"测试里临时改成 Ctrl+Alt+F12"当成文档缺一条——这条假失败刚踩到。
        var docCheck = CheckHotkeyDoc(KeyMap.Default());
        Check("快捷键总表.md 覆盖了每一条绑定（键 + 动作名同一行）", docCheck.ok, docCheck.detail);

        // ---- 6. 动作路由：id ↔ 动作 ↔ 键 一一对应 ----
        bool routingOk = true;
        string routingDetail = "";
        int i2 = 0;
        foreach (var b in Keys.For(KeyScope.Global))
        {
            i2++;
            if (ActionForHotkeyId(i2) != b.Action)
            {
                routingOk = false;
                routingDetail = $"第 {i2} 个：期望 {b.Action}，实际 {ActionForHotkeyId(i2)}";
                break;
            }
        }
        Check("WM_HOTKEY 的 id 能正确反查到动作", routingOk,
              routingOk ? $"{i2} 条全部对上" : routingDetail);

        // ---- 7. 批注内真按一次键（走窗口消息 → 键盘模式 → 动作）----
        Doc.Clear();
        Doc.ClearHistory();
        for (int i = 0; i < 3; i++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f * DpiScale };
            for (int k = 0; k <= 20; k++) s.AddPoint(600 + k * 20, 400 + i * 120, 1f, NowMs);
            Doc.AddStroke(s);
        }
        bool wasKeyboardMode = KeyboardMode;
        if (!wasKeyboardMode) SetKeyboardMode(true);
        SelectAll();
        int before = Doc.Strokes.Count;

        // Delete 在默认表里是"删除选中"（批注内、无修饰键，所以能直接合成）
        Native.PostMessage(_windows[0].Hwnd, (uint)Native.WM_KEYDOWN, new IntPtr(0x2E), IntPtr.Zero);
        SettleFrames(200);
        int afterDelete = Doc.Strokes.Count;
        Check("批注内按 Delete 真的删掉了选中",
              before == 3 && afterDelete == 0, $"{before} 笔 → {afterDelete} 笔");

        Doc.Undo();
        SettleFrames(150);
        Check("这一步能撤销回来", Doc.Strokes.Count == 3, $"撤销后 {Doc.Strokes.Count} 笔");
        if (!wasKeyboardMode) SetKeyboardMode(false);

        // ---- 8. 白板开关 Ctrl+B：和点白板格同一条命令（含"开板顺手退穿透"）----
        {
            bool passWas = PassThrough;
            if (passWas) SetPassThroughFromUi(false);
            if (BoardOn) Host.Commands.SetBoard(false);
            SettleFrames(100);
            RunActionForTest(KeyAction.ToggleBoard);
            SettleFrames(100);
            bool opened = BoardOn;
            RunActionForTest(KeyAction.ToggleBoard);
            SettleFrames(100);
            Check("Ctrl+B：按一下开板、再按关板", opened && !BoardOn,
                  $"开 = {opened}，再按后 = {BoardOn}");

            if (BoardOn) Host.Commands.SetBoard(false);
            SetPassThroughFromUi(true);
            SettleFrames(100);
            RunActionForTest(KeyAction.ToggleBoard);
            SettleFrames(100);
            Check("Ctrl+B：穿透里按 = 开板 + 顺手退穿透", BoardOn && !PassThrough,
                  $"板 = {BoardOn}，穿透 = {PassThrough}");
            if (BoardOn) Host.Commands.SetBoard(false);
            if (passWas) SetPassThroughFromUi(true);
            SettleFrames(100);
        }

        Console.WriteLine();
        Console.WriteLine("  当前键位表：");
        Console.Write(Keys.ToText());
        if (Keys.GlobalFailures.Count > 0)
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    ！" + f);

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS: 键位表、冲突检测、落盘读回、真机注册与批注内按键都正确"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");

        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 核对"快捷键总表.md"有没有落后于代码。
    ///
    /// 规则（用户定的）：同一个功能的所有键都要列全，每次改键都要记录。
    /// 这里只机器可判的那一半：**代码里每一条绑定，都要能在文档里找到
    /// "键 + 动作名"同一行**。文档不在（比如在别的目录跑）就跳过，
    /// 只提示不判失败——那是环境问题，不是键位表的问题。
    /// </summary>


    /// <summary>
    /// **快捷键自检**（用户 2026-09-30 要的独立一条）：截图前后 + 焦点丢/恢复之后，
    /// 应用内快捷键（`Ctrl+P` 这些）还灵不灵。
    ///
    /// 为什么单开一条、而且**必须用真实键盘**（`SendKeyChord`，不是 `PostMessage`）：
    /// 这类故障是"**前台被系统转给了别人**"——按键发给了别的窗口，我们的 WndProc
    /// 根本收不到。`PostMessage` 是直接投给窗口的，焦点丢了也照样"通过"，**测不出来**。
    /// 所以这条自检的判据是两件事一起看：
    ///   ① `GetForegroundWindow()` 是不是我们的窗口；
    ///   ② 真按一次 `Ctrl+P`，工具是不是真的换成了笔。
    ///
    /// 场景（照用户的要求排的）：基线 → 隐藏窗口截图（藏窗口那一瞬最容易掉前台）→
    /// 取消 → 拖框 + 确认 → 截图（连批注）→ "焦点被抢走再要回来"。
    /// </summary>
    private void HotkeyTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 快捷键自检（截图前后的键盘前台）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        if (SkipIfNoSyntheticInput("快捷键全流程（需要合成键盘/鼠标）")) { _quit = true; return; }

        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(200);

        // "键盘在我们这儿"＝ 前台是我们的窗口 **且** 焦点也在它上面。
        // 只查前台会漏掉"前台是我们、焦点还留在别人那儿"这一种——屏幕上看着一切正常、
        // 按键却全收不到（8.3.2 的自检里最后一条红就是这个样子）。
        bool FgOurs()
        {
            if (_windows.Count == 0) return false;
            if (Native.GetForegroundWindow() != _windows[0].Hwnd) return false;
            return Native.GetFocus() == _windows[0].Hwnd;
        }

        // 真按一个组合键。**要按真实手的节奏来**：修饰键先按住、隔一会儿再按主键——
        // 一次 SendInput 把四个事件全灌进去的话，我们处理 KEYDOWN 时修饰键可能已经抬了
        //（`HandleKeyDown` 读的是 `GetAsyncKeyState`），会被误判成"没按 Ctrl"。
        // 这一条也让测试更接近老师真实的按键节奏。
        void RealChord(bool ctrl, ushort vk)
        {
            var downs = ctrl
                ? new[] { KeyInput(VK_CONTROL, false), KeyInput(vk, false) }
                : new[] { KeyInput(vk, false) };
            Native.SendInput((uint)downs.Length, downs, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(90);
            var ups = ctrl
                ? new[] { KeyInput(vk, true), KeyInput(VK_CONTROL, true) }
                : new[] { KeyInput(vk, true) };
            Native.SendInput((uint)ups.Length, ups, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(160);
        }

        // 真按 Ctrl+P：焦点在不在我们这儿，这一条说了算。
        bool RealPenKeyWorks(string what)
        {
            Host.Commands.SetTool(Tool.Eraser);        // 摆到"不按就看得出来"的位置
            SettleFrames(120);
            RealChord(ctrl: true, 'P');
            bool ok = Tool == Tool.Pen;
            Console.WriteLine($"      （{what}）真按 Ctrl+P → 工具 = {Tool}，前台是我们 = {FgOurs()}");
            return ok;
        }

        // ---- ① 基线：还没截图，键盘在我们这儿 ----
        Check("基线：批注窗口是前台", FgOurs(),
              $"前台 = {(FgOurs() ? "批注窗口" : "别的窗口")}");
        Check("基线：真按 Ctrl+P 切到笔", RealPenKeyWorks("基线"), $"工具 = {Tool}");

        // ---- ② 隐藏窗口截图：进屋那一瞬藏过窗口（漏要前台的正是这一步）----
        Host.Commands.SetCaptureHideInk(true);
        Host.Commands.EnterCapture();
        SettleFrames(400);
        Check("隐藏窗口截图 · 取景中：前台还是我们（Esc/Enter 才有地方落地）",
              FgOurs(), $"前台 = {(FgOurs() ? "批注窗口" : "别的窗口")}，取景 = {CaptureActive}");
        SendKeyChord(0x1B);                        // 真按 Esc
        SettleFrames(400);
        Check("隐藏窗口截图 · 真按 Esc 能退出取景", !CaptureActive && !CaptureAdjusting,
              $"取景 = {CaptureActive}，工具 = {Tool}");
        Check("隐藏窗口截图 · 退出后真按 Ctrl+P 能用", RealPenKeyWorks("Esc 退出后"), $"工具 = {Tool}");

        // ---- ③ 隐藏窗口截图：拖框 + 真按 Enter 确认 ----
        Host.Commands.EnterCapture();
        SettleFrames(300);
        int cx = (int)(_virtualX + _virtualW * 0.5f), cy = (int)(_virtualY + _virtualH * 0.5f);
        SendMouse(cx - 120, cy - 80, 0);                              SettleFrames(60);
        SendMouse(cx - 120, cy - 80, Native.MOUSEEVENTF_LEFTDOWN);     SettleFrames(60);
        SendMouse(cx, cy, 0);                                        SettleFrames(50);
        SendMouse(cx + 120, cy + 80, 0);                              SettleFrames(50);
        SendMouse(cx + 120, cy + 80, Native.MOUSEEVENTF_LEFTUP);       SettleFrames(300);
        SendKeyChord(0x0D);                        // 真按 Enter
        SettleFrames(500);
        Check("隐藏窗口截图 · 拖框 + 真按 Enter 确认落图",
              Doc.Strokes.Count(s => s.IsImage) == 1,
              $"图像对象 {Doc.Strokes.Count(s => s.IsImage)} 个");
        Check("隐藏窗口截图 · 落图后真按 Ctrl+P 能用", RealPenKeyWorks("确认后"), $"工具 = {Tool}");

        // ---- ④ 截图（连批注）：同一套 ----
        Host.Commands.SetCaptureHideInk(false);
        Host.Commands.EnterCapture();
        SettleFrames(300);
        Check("截图（连批注）· 取景中：前台还是我们", FgOurs(),
              $"前台 = {(FgOurs() ? "批注窗口" : "别的窗口")}");
        SendKeyChord(0x1B);
        SettleFrames(400);
        Check("截图（连批注）· 真按 Esc 退出 + Ctrl+P 能用",
              !CaptureActive && RealPenKeyWorks("连批注退出后"), $"工具 = {Tool}");

        // ---- ⑤ 焦点被抢走 → 回到批注态要得回来 ----
        // 模拟"前台被系统转给了别人"：藏窗口 + 用 SW_SHOWNOACTIVATE 显示
        //（截图藏窗口那一下就是这个效果）。这时真按键到不了批注层——
        // 这就是用户看到的"快捷键没用"。
        Native.ShowWindow(_windows[0].Hwnd, Native.SW_HIDE);
        Native.ShowWindow(_windows[0].Hwnd, Native.SW_SHOWNOACTIVATE);
        SettleFrames(250);
        bool lost = !FgOurs();
        bool worksWhileLost = RealPenKeyWorks("焦点丢了");
        Check("焦点丢了：此时按键到不了批注层（用户遇到的'快捷键没用'）",
              !lost || !worksWhileLost,
              $"焦点丢了 = {lost}，按键还能用 = {worksWhileLost}");

        // 产品里"回到批注态"都走这一句（截图收场 / 退出放映 / 关掉穿透）：
        SetKeyboardMode(KeyboardMode);
        SettleFrames(250);
        Check("回到批注态：焦点要得回来", FgOurs(),
              $"前台 = {(FgOurs() ? "批注窗口" : "别的窗口")}");
        Check("焦点恢复后：真按 Ctrl+P 立刻能用", RealPenKeyWorks("焦点恢复后"), $"工具 = {Tool}");

        // ---- ⑥ 真按 Ctrl+=：OEM 等号键 + 选中缩放（2026-10-05 新增）----
        // 为什么单列一条：`=` 是 **OEM 键**，合成键盘上和字母键不是一条路——键盘布局、
        // 修饰键状态读法、键名解析都可能在这里翻车，值得让真键盘走一遍。
        {
            Doc.Clear();
            Doc.ClearHistory();
            var kz = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f * DpiScale };
            kz.AddPoint(_virtualX + 400f, _virtualY + 400f, 1f, NowMs);
            kz.AddPoint(_virtualX + 600f, _virtualY + 400f, 1f, NowMs + 8);
            Doc.AddStroke(kz);
            Doc.SelectOnly(new[] { kz });
            SettleFrames(150);
            RealChord(ctrl: true, 0xBB);               // 真按 Ctrl+=
            float scaled = kz.Transform.M11;
            Check("真按 Ctrl+=：选中对象真的放大 1.1×",
                  Math.Abs(scaled - 1.1f) < 0.02f, $"M11 = {scaled:F3}");
            RunActionForTest(KeyAction.Undo);         // 连按合并那条在 --seltest 验
            SettleFrames(120);
            Check("真按 Ctrl+= 后可一步撤销", Math.Abs(kz.Transform.M11 - 1f) < 0.001f,
                  $"M11 = {kz.Transform.M11:F3}");
        }

        // 收尾：模式还原、画布清干净
        Host.Commands.SetCaptureHideInk(true);
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(150);

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 快捷键全通（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }


    /// <summary>
    /// The real question is not "does hit testing report another window" - that
    /// gave a false pass - but "does a click actually arrive at the window
    /// underneath". So this spawns a second process with a target window,
    /// synthesises real clicks, and counts how many it received.
    /// </summary>
    private void PassThroughTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 穿透真机测试（合成真实点击，看下层窗口收不收到）===");

        string dir = Path.Combine(Path.GetTempPath(), "inkprobe_passtest");
        Directory.CreateDirectory(dir);
        // 每次跑用**带进程号的文件名**：上一轮要是留了个没退干净的点击目标进程，
        // 它还在往老文件里写，两边就会抢同一个文件（套件里真撞到过一次）。
        string log = Path.Combine(dir, $"target-{Environment.ProcessId}.txt");
        File.WriteAllText(log, "");      // must write before spawning? no: spawn then wait for ready

        var psi = new ProcessStartInfo(Environment.ProcessPath, $"--clicktarget \"{log}\"")
        {
            UseShellExecute = false,
        };
        var target = Process.Start(psi);
        if (target == null) { Console.WriteLine("  无法启动点击目标进程"); _quit = true; return; }

        // 读的时候带重试：父进程读、子进程写，撞上共享冲突时直接抛会把整条用例打成异常
        for (int i = 0; i < 120 && !ReadTextShared(log, 200).Contains("ready"); i++)
            Thread.Sleep(50);
        if (!ReadTextShared(log, 200).Contains("ready"))
        {
            Console.WriteLine("  点击目标窗口没有就绪");
            target.Kill();
            _quit = true;
            return;
        }

        int tx = 120 + 210, ty = 120 + 160;   // centre of the target window

        // Expected outcomes come from the documented rules:
        //  - HTTRANSPARENT only hands off to windows of the same thread
        //  - WS_EX_TRANSPARENT only makes a *layered* window click-through
        (string name, bool pass, bool expectReach, PassThroughMode mode)[] cases =
        {
            ("对照：不穿透",              false, false, PassThroughMode.LayeredTransparent),
            ("只改命中测试返回值",        true,  false, PassThroughMode.HitTest),
            ("只加 WS_EX_TRANSPARENT",    true,  false, PassThroughMode.ExTransparent),
            ("加 LAYERED+TRANSPARENT",    true,  true,  PassThroughMode.LayeredTransparent),
        };

        int failures = 0;
        foreach (var (name, pass, expectReach, mode) in cases)
        {
            PassMode = mode;
            PassThrough = pass;
            foreach (var w in _windows) ApplyPassThroughStyle(w);

            SettleFrames(150);

            int before = CountClicks(log);
            SendMouse(tx, ty, 0);
            SettleFrames(80);
            SendMouse(tx, ty, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(60);
            SendMouse(tx, ty, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(400);
            int after = CountClicks(log);

            bool reached = after > before;
            bool good = reached == expectReach;
            if (!good) failures++;
            Console.WriteLine($"  {name,-26} 下层窗口收到点击: {(reached ? "是" : "否"),-2}"
                              + $"  文档预期: {(expectReach ? "是" : "否"),-2}  {(good ? "PASS" : "FAIL")}"
                              + (pass ? "" : "   （对照组）"));
        }
        Console.WriteLine(failures == 0 ? "  结论: 穿透行为与文档一致" : $"  结论: {failures} 项与文档不符");

        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);

        try { target.Kill(); } catch { }
        _quit = true;
    }


    /// <summary>
    /// 呼出盘自检（--radialtest）：全走引擎里真在用的那套状态机（开 / 划 / 松 / 取消）。
    ///
    /// 不依赖真键盘：按住/松手的"真实键路由"由发布前手测覆盖；这里钉住的是
    /// 其余全部行为——扇区几何、死区、滞回、和工具键同一条命令、穿透语义、
    /// 松键轮询、放映临时键表。对照文档见《调研-笔键方案.md》附录 C/D。
    /// </summary>
    private void RadialTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 呼出盘自检（Ctrl+Alt+Shift+Q：按住 → 划向扇区 → 松手）===");

        if (SkipIfNoSyntheticInput("呼出盘自检")) { _quit = true; return; }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-32} {detail}");
        }
        static bool SameCol(Color4 a, Color4 b) =>
            MathF.Abs(a.R - b.R) < 0.02f && MathF.Abs(a.G - b.G) < 0.02f
            && MathF.Abs(a.B - b.B) < 0.02f;

        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(300);

        // 自检期间**先按住"测试保持"**：不然一次 Settle 的泵就会把盘当成"已松手"提交掉。
        // ⑩ 单独把它放开来验轮询。
        RadialTestHold = true;

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // ---- ① 打开：盘心 = 按下那一刻的指针 ----
        RadialOpenForTest(cx, cy);
        Check("打开：进入呼出盘状态", RadialPaletteActive, $"active = {RadialPaletteActive}");
        Check("盘心 = 按下那一刻的指针位置",
              MathF.Abs(RadialCenterX - cx) < 0.5f && MathF.Abs(RadialCenterY - cy) < 0.5f,
              $"({RadialCenterX:F0},{RadialCenterY:F0}) vs ({cx:F0},{cy:F0})");

        // ---- ② 死区（没移动）松手 = 取消 ----
        Host.Commands.SetTool(Tool.Pen);
        Host.Commands.SetColor(InkPalette.PenBand[0].Color);
        SettleFrames(120);
        var tool0 = Host.State.Tool;
        RadialCommitForTest();
        Check("死区松手 = 取消：工具/颜色都不动",
              !RadialPaletteActive && Host.State.Tool == tool0,
              $"active = {RadialPaletteActive}，工具 = {Host.State.Tool}");

        // ---- ③ 划向正东 =「红」：执行后是红笔 ----
        RadialOpenForTest(cx, cy);
        RadialMoveForTest(cx + 120f, cy);
        Check("划向正东 = 选中「红」扇区", RadialPaletteSector == 2, $"扇区 {RadialPaletteSector}");
        RadialCommitForTest();
        Check("松手执行：切成红笔",
              !RadialPaletteActive && Host.State.Tool == Tool.Pen
              && SameCol(Host.State.PaletteBase, InkPalette.PenBand[1].Color),
              $"工具 = {Host.State.Tool}，色 = {Host.State.PaletteBase}");

        // ---- ④ 在别的工具上选颜色 = "给我这支颜色的笔"（切回笔） ----
        Host.Commands.SetTool(Tool.Eraser);
        SettleFrames(120);
        RadialOpenForTest(cx, cy);
        RadialMoveForTest(cx + 85f, cy - 85f);          // 东北 = 黑
        Check("划向东北 = 选中「黑」扇区", RadialPaletteSector == 1, $"扇区 {RadialPaletteSector}");
        RadialCommitForTest();
        Check("在橡皮上选颜色：会切回笔并给对应色",
              Host.State.Tool == Tool.Pen
              && SameCol(Host.State.PaletteBase, InkPalette.PenBand[0].Color),
              $"工具 = {Host.State.Tool}，色 = {Host.State.PaletteBase}");

        // ---- ⑤ 工具扇区 = 和按 Ctrl+P 同一条命令（已经是它 → 换色） ----
        Host.Commands.SetTool(Tool.Pen);
        Host.Commands.SetColor(InkPalette.PenBand[0].Color);
        SettleFrames(120);
        RadialOpenForTest(cx, cy);
        RadialMoveForTest(cx, cy - 120f);               // 北 = 笔
        Check("划向正北 = 选中「笔」扇区", RadialPaletteSector == 0, $"扇区 {RadialPaletteSector}");
        RadialCommitForTest();
        Check("已经是笔时选「笔」= 连按语义（换下一色）",
              Host.State.Tool == Tool.Pen
              && SameCol(Host.State.PaletteBase, InkPalette.PenBand[1].Color),
              $"色 = {Host.State.PaletteBase}");

        // ---- ⑥ 其余扇区各换一次工具（V-a 排序：南=橡皮、西南=框选、西=荧光笔） ----
        RadialOpenForTest(cx, cy);
        RadialMoveForTest(cx, cy + 120f);               // 南 = 橡皮
        RadialCommitForTest();
        Check("划向正南 = 橡皮", Host.State.Tool == Tool.Eraser, $"工具 = {Host.State.Tool}");

        RadialOpenForTest(cx, cy);
        RadialMoveForTest(cx - 85f, cy + 85f);          // 西南 = 框选
        RadialCommitForTest();
        Check("划向西南 = 框选", Host.State.Tool == Tool.Marquee, $"工具 = {Host.State.Tool}");

        RadialOpenForTest(cx, cy);
        RadialMoveForTest(cx - 120f, cy);               // 西 = 荧光笔
        RadialCommitForTest();
        Check("划向正西 = 荧光笔", Host.State.Tool == Tool.Highlighter, $"工具 = {Host.State.Tool}");

        RadialOpenForTest(cx, cy);
        RadialMoveForTest(cx - 85f, cy - 85f);          // 西北 = 激光
        RadialCommitForTest();
        Check("划向西北 = 激光", Host.State.Tool == Tool.Laser, $"工具 = {Host.State.Tool}");

        // ---- ⑥.5 排序 V-a 与扇面几何（2026-09-30 v4 定稿） ----
        // 正位（上下左右）＝前四高频：笔 / 红 / 橡皮 / 荧光笔；四角＝次频。
        Check("排序 V-a：上下左右＝笔/红/橡皮/荧光笔，四角＝黑/蓝/框选/激光",
              RadialSectorNames[0] == "笔" && RadialSectorNames[2] == "红"
              && RadialSectorNames[4] == "橡皮" && RadialSectorNames[6] == "荧光笔"
              && RadialSectorNames[1] == "黑" && RadialSectorNames[3] == "蓝"
              && RadialSectorNames[5] == "框选" && RadialSectorNames[7] == "激光",
              string.Join(" ", RadialSectorNames));
        // 扇面几何：图标环 68 ± 图标半径 12 要落在（锁定距离, 盘半径）里，也别压到中央读数。
        Check("扇面几何：图标环 68 与死区/锁定/中央读数不打架",
              OverlayWindow.RadialIconRingLogical - 12f > RadialLockLogical
              && OverlayWindow.RadialIconRingLogical + 12f < RadialRadiusLogical
              && OverlayWindow.RadialIconRingLogical - 12f > OverlayWindow.RadialInnerRadiusLogical
              && OverlayWindow.RadialInnerRadiusLogical > OverlayWindow.RadialCenterLogical,
              $"环带 {OverlayWindow.RadialInnerRadiusLogical}→{OverlayWindow.RadialPlateRadiusLogical}，"
              + $"图标环 {OverlayWindow.RadialIconRingLogical}，锁定 {RadialLockLogical}");

        // ---- ⑦ 跨扇区滞回：出界 9° 以内不跳扇区 ----
        RadialOpenForTest(cx, cy);
        float rr = 120f;
        RadialMoveForTest(cx + rr, cy);                 // 正东 = 红
        float a23 = 23f * MathF.PI / 180f;              // 刚过 22.5° 边界
        RadialMoveForTest(cx + rr * MathF.Cos(a23), cy + rr * MathF.Sin(a23));
        Check("越过扇区边界 9° 内：不跳扇区（滞回）", RadialPaletteSector == 2,
              $"扇区 {RadialPaletteSector}");
        float a40 = 40f * MathF.PI / 180f;
        RadialMoveForTest(cx + rr * MathF.Cos(a40), cy + rr * MathF.Sin(a40));
        Check("出界超过 9°：正常换到下一扇区", RadialPaletteSector == 3,
              $"扇区 {RadialPaletteSector}");
        RadialCancelForTest("自检收尾");

        // ---- ⑧ 穿透里照样能呼出：这是"把笔从下层抢回来"的入口（2026-10-04 用户定）----
        //
        // 刻意钉住两件不同的事，别混成一件：
        //   ① **能呼出**：穿透开着时按 Ctrl+Alt+Shift+Q，盘照样出（指针要走
        //      `GetCursorPos`，因为 WS_EX_TRANSPARENT 下我们收不到鼠标消息）；
        //   ② **选扇区 = 退出穿透 + 换工具**——工具键本身在穿透下不响应（8.5），
        //      所以提交时必须先退穿透，否则会"按下去悄无声息"。
        // 同时保留一条反向守卫：**取消不动穿透**（死区松手 / 划回中心）。
        Host.Commands.SetPassThrough(true);
        SettleFrames(150);
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(150);
        RadialTestHold = true;
        RadialOpenForTest(cx, cy);
        Check("穿透开着：呼出盘照样能呼出（2026-10-04 起）", RadialPaletteActive,
              $"active = {RadialPaletteActive}，穿透 = {PassThrough}");
        // ⚠ 这里**必须真移鼠标**、不能用 `RadialMoveForTest`：穿透时指针位置每帧从
        // `GetCursorPos` 刷（我们收不到鼠标消息），直接写 PointerX/Y 会被立刻覆盖掉。
        // 这也正是这段判据要钉的东西——穿透里扇区方向跟不跟得上系统光标。
        SendMouse((int)(cx + 85f), (int)(cy - 85f), 0);  // 东北 = 黑（颜色扇区）
        SettleFrames(150);
        RadialPumpForTest();
        Check("穿透里：扇区方向跟得上系统光标（GetCursorPos 那一路）", RadialPaletteSector == 1,
              $"扇区 {RadialPaletteSector}");
        RadialCommitForTest();
        Check("穿透里选扇区：退出穿透 + 换到那支笔",
              !PassThrough && Host.State.Tool == Tool.Pen
              && SameCol(Host.State.PaletteBase, InkPalette.PenBand[0].Color),
              $"穿透 = {PassThrough}，工具 = {Host.State.Tool}，色 = {Host.State.PaletteBase}");

        // 反向守卫：死区松手 = 取消，穿透必须原样留着。
        Host.Commands.SetPassThrough(true);
        SettleFrames(150);
        RadialOpenForTest(cx, cy);
        RadialCommitForTest();
        Check("穿透里死区松手：只是取消，穿透不动", PassThrough && !RadialPaletteActive,
              $"穿透 = {PassThrough}，active = {RadialPaletteActive}");
        Host.Commands.SetPassThrough(false);
        SettleFrames(150);
        RadialTestHold = true;

        // ---- ⑨ 写字中（笔尖在屏上）不响应 ----
        SendMouse((int)cx, (int)cy, 0);                          SettleFrames(60);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(120);
        RadialOpenForTest(cx, cy);
        Check("落笔中：呼出盘不响应（抬笔后再按）", !RadialPaletteActive, $"active = {RadialPaletteActive}");
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTUP);   SettleFrames(200);

        // ---- ⑩ 松键轮询：放映那条路没有 KeyUp，靠每帧 GetAsyncKeyState ----
        RadialTestHold = false;
        RadialOpenForTest(cx, cy);
        Check("按住状态：盘是活的", RadialPaletteActive, $"active = {RadialPaletteActive}");
        RadialPumpForTest();     // 物理 Q 没按着 → 应当按"已松手"提交（这里没位移 = 取消）
        Check("轮询发现松手：自动提交（没位移 = 取消）", !RadialPaletteActive,
              $"active = {RadialPaletteActive}");

        // ---- ⑪ 呼出盘现在是常驻全局键（Ctrl+Alt+Shift+Q），放映临时表里不重复挂 ----
        // 2026-10-04 用户定：呼出盘从批注内 Ctrl+Q 升级为全局 Ctrl+Alt+Shift+Q。
        // 这条同时钉住"升上去"和"临时表里那一条删干净"——两件事缺一个都会回到
        // "一个动作两把全局键"的糊状态。
        var radialGlobal = Keys.Find(KeyScope.Global, KeyAction.RadialPalette);
        var radialTemp = PptHotkeyEntryForTest(KeyAction.RadialPalette);
        Check("呼出盘：全局 Ctrl+Alt+Shift+Q，放映临时表里没有重复",
              radialGlobal != null && radialGlobal.Chord.ToString() == "Ctrl+Alt+Shift+Q"
              && radialTemp == null,
              radialGlobal == null
                  ? "全局表里没有"
                  : $"全局 {radialGlobal.Chord}；临时表 {(radialTemp == null ? "没有" : "有")}");

        // ---- ⑫ 真键盘 + 真鼠标：全局热键那条路（RegisterHotKey → WM_HOTKEY → 打开） ----
        // 前面都是引擎钩子；这一条和 --hotkeytest 同一套方法，走真实输入流。
        // 呼出盘 2026-10-04 起是全局键：按下由系统送 WM_HOTKEY，松手靠泵轮询物理键
        //（和放映时的临时全局键同一条路）。
        if (!SkipIfNoSyntheticInput("呼出盘真键盘"))
        {
            Host.Commands.SetTool(Tool.Pen);
            Host.Commands.SetColor(InkPalette.PenBand[0].Color);
            SettleFrames(200);
            SendMouse((int)cx, (int)cy, 0);
            SettleFrames(150);

            var qDown = new[]
            {
                KeyInput(VK_CONTROL, false), KeyInput(VK_MENU, false),
                KeyInput(VK_SHIFT, false), KeyInput(0x51, false),
            };
            Native.SendInput((uint)qDown.Length, qDown, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(250);
            Check("真键盘：按住 Ctrl+Alt+Shift+Q 能打开呼出盘", RadialPaletteActive,
                  $"active = {RadialPaletteActive}");

            SendMouse((int)(cx + 120f), (int)cy, 0);      // 正东 = 红
            SettleFrames(150);
            var qUp = new[]
            {
                KeyInput(0x51, true), KeyInput(VK_SHIFT, true),
                KeyInput(VK_MENU, true), KeyInput(VK_CONTROL, true),
            };
            Native.SendInput((uint)qUp.Length, qUp, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(250);
            Check("真键盘：松手确认为红笔",
                  !RadialPaletteActive && Host.State.Tool == Tool.Pen
                  && SameCol(Host.State.PaletteBase, InkPalette.PenBand[1].Color),
                  $"active = {RadialPaletteActive}，色 = {Host.State.PaletteBase}");

            // ---- ⑬ 真键盘：先松修饰键（盘还在）→ 松开 Alt/Shift 后按 Esc 取消 ----
            // 这条同时钉住两件事：提交只认 Q（松修饰键不提交）；Esc 要在没有修饰键压着时按
            // （Ctrl+Esc 是系统开始菜单、Alt+Esc 还会切窗口，都收不到——⑫ 那版就是这么
            // 发现问题的）。
            SendMouse((int)cx, (int)cy, 0);
            SettleFrames(120);
            Native.SendInput((uint)qDown.Length, qDown, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(250);
            var ctrlUp = new[] { KeyInput(VK_CONTROL, true) };
            Native.SendInput((uint)ctrlUp.Length, ctrlUp, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(150);
            Check("真键盘：先松 Ctrl，盘还在（提交只认 Q 松手）", RadialPaletteActive,
                  $"active = {RadialPaletteActive}");
            var modsUp = new[] { KeyInput(VK_SHIFT, true), KeyInput(VK_MENU, true) };
            Native.SendInput((uint)modsUp.Length, modsUp, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(150);
            SendKeyChord(0x1B);                            // 真按 Esc（此时没有修饰键压着）
            SettleFrames(200);
            Check("真键盘：Esc 取消（工具/颜色都不动）",
                  !RadialPaletteActive && Host.State.Tool == Tool.Pen,
                  $"active = {RadialPaletteActive}，工具 = {Host.State.Tool}");
            var qUpOnly = new[] { KeyInput(0x51, true) };
            Native.SendInput((uint)qUpOnly.Length, qUpOnly, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(150);

            // ---- ⑭ 真键盘 + 穿透：开着穿透按 Ctrl+Alt+Shift+Q 也能调出笔来 ----
            // 这一条钉的就是用户 2026-10-04 要的那件事：**穿透时也能用全局呼出盘
            // 把笔快速调出来**（松手 = 退出穿透 + 换工具）。走真实输入流，和 ⑫ 同一条路。
            Host.Commands.SetPassThrough(true);
            SettleFrames(300);
            SendMouse((int)cx, (int)cy, 0);
            SettleFrames(200);
            Native.SendInput((uint)qDown.Length, qDown, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(250);
            Check("真键盘 + 穿透：能呼出盘", RadialPaletteActive,
                  $"active = {RadialPaletteActive}，穿透 = {PassThrough}");
            SendMouse((int)(cx + 120f), (int)cy, 0);      // 正东 = 红
            SettleFrames(200);
            Native.SendInput((uint)qUp.Length, qUp, Marshal.SizeOf<Native.INPUT_KBD>());
            SettleFrames(300);
            Check("真键盘 + 穿透：松手 = 退出穿透 + 红笔",
                  !RadialPaletteActive && !PassThrough && Host.State.Tool == Tool.Pen
                  && SameCol(Host.State.PaletteBase, InkPalette.PenBand[1].Color),
                  $"穿透 = {PassThrough}，工具 = {Host.State.Tool}，色 = {Host.State.PaletteBase}");
            Host.Commands.SetPassThrough(false);
            SettleFrames(200);
        }

        // 收尾
        Host.Commands.SetTool(Tool.Pen);
        PassThrough = false;
        Doc.Clear();
        Doc.ClearHistory();
        SettleFrames(150);

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? $"  PASS: 呼出盘 {pass} 项全过"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");
        if (fail > 0) ExitCode = 1;
        _quit = true;
    }

    /// <summary>
    /// 呼出盘摆样（--radialshow）：把盘定格在屏幕中央、自己抓屏出图——
    /// 给"长什么样"留底稿（和 --cursorshow / --pixeleraseshow 同一套做法）。
    /// 出两张：没划的、划到「红」的。
    /// </summary>

}
