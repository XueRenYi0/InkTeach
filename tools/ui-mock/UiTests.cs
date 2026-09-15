using System;
using System.Collections.Generic;
using System.Windows;

namespace UiMock;

/// <summary>
/// 面板的全方位自检（`--uitest`）。
///
/// 为什么要有它：假面板是"手动点着看"的东西，出了问题只能靠回忆。
/// 2026-09-16 那次就是——点"更多"再点"收起"，抽屉的状态没清掉，再展开时抽屉自己冒出来。
/// 这类 bug 看一遍代码是看不出来的（要知道"哪些状态属于临时状态"），必须有个东西替你反复点。
///
/// 特点：**不开窗口**，直接驱动状态机（创建 PanelElement、调它的公开方法、
/// 用 SnapAll 把动画一步到位），所以它跑得飞快、能进 CI；
/// 检查的都是"几何自洽"与"状态不泄漏"这类能判定的东西，不靠肉眼。
/// </summary>
internal static class UiTests
{
    public static int Run()
    {
        int pass = 0, fail = 0;

        void Check(string name, Func<string> body)
        {
            string why;
            try { why = body(); }
            catch (Exception ex) { why = $"抛异常 {ex.GetType().Name}: {ex.Message}"; }
            if (string.IsNullOrEmpty(why)) { pass++; Console.WriteLine($"  PASS  {name}"); }
            else { fail++; Console.WriteLine($"  FAIL  {name}\n          原因：{why}"); }
        }

        Console.WriteLine("=== 面板自检（--uitest）===");

        // ---------------------------------------------------------------
        // A. 状态机：临时状态不许泄漏（这次的 bug 就在这一类）
        // ---------------------------------------------------------------

        Check("更多→收起→展开：抽屉不会自己冒出来", () =>
        {
            var el = New();
            el.SetTool(PanelDraw.ToolMore);
            el.SnapAll();
            if (!el.State.MoreOpen) return "点「更多」没把抽屉打开";
            el.ToggleExpand();
            el.SnapAll();
            if (el.State.MoreOpen) return "收起之后 MoreOpen 还是 true（状态泄漏）";
            el.ToggleExpand();
            el.SnapAll();
            if (el.State.MoreOpen) return "再展开时抽屉自己冒出来了";
            var L = PanelDraw.Compute(el.State);
            if (!L.DrawerRect.IsEmpty) return "抽屉矩形没清掉";
            if (Math.Abs(L.ContentW - L.W) > 0.01) return "内容宽度没回到面板宽度";
            return null;
        });

        Check("更多→收起：按下的按钮被清掉", () =>
        {
            var el = New();
            el.SetTool(PanelDraw.ToolMore);
            el.SnapAll();
            el.ToggleExpand();
            el.SnapAll();
            if (el.State.PressTile >= 0) return "PressTile 还留着";
            if (el.State.HoverTile >= 0) return "HoverTile 还留着";
            if (el.State.ActionHoverID >= 0) return "ActionHoverID 还留着";
            return null;
        });

        Check("钉住上带→收起→展开：上带不许还是钉住的", () =>
        {
            var el = New();
            el.ToggleRail();          // 键盘 = 钉住展开
            el.SnapAll();
            if (!el.RailPinned) return "按 R 之后没有钉住";
            el.ToggleExpand();
            el.SnapAll();
            if (el.RailPinned) return "收起之后还钉着（下一次展开会自带一条展开的上带）";
            return null;
        });

        Check("按住清空的进度：收起时归零", () =>
        {
            var el = New();
            el.State.Tool = PanelDraw.ToolEraser;
            el.State.ClearHold = 0.6;
            el.ToggleExpand();
            el.SnapAll();
            if (el.State.ClearHold != 0) return "ClearHold 没归零";
            return null;
        });

        Check("收起→展开：回到「干净的展开态」", () =>
        {
            var el = New();
            el.SetTool(PanelDraw.ToolMore);   // 开抽屉
            el.ToggleRail();                  // 钉住上带
            el.SnapAll();
            el.ToggleExpand();                // 收起
            el.SnapAll();
            el.ToggleExpand();                // 再展开
            el.SnapAll();
            var L = PanelDraw.Compute(el.State);
            if (!L.DrawerRect.IsEmpty) return "抽屉还在";
            if (el.RailPinned) return "上带还钉着";
            if (Math.Abs(el.State.E - 1) > 0.01) return "没回到完全展开";
            // 上带此时应当是"收起来的那条线"（Rail == 0），而不是展开的
            if (el.State.Rail > 0.5) return "上带还是展开的";
            return null;
        });

        // ---------------------------------------------------------------
        // B. 几何自洽：每一档 × 每一种工具，算出来的东西必须站得住
        // ---------------------------------------------------------------

        Check("所有档 × 所有工具：布局都能算出来、尺寸合理", () =>
        {
            for (int scale = 0; scale < PanelDraw.Scales.Length; scale++)
            for (int mini = 0; mini < 2; mini++)
            foreach (int tool in PanelDraw.VisibleTools(new PanelState { Mini = mini == 1 }))
            foreach (double rail in new[] { 0.0, 0.5, 1.0 })
            foreach (double e in new[] { 0.3, 0.6, 1.0 })
            {
                var L = PanelDraw.Compute(new PanelState
                { Tool = tool, IconScale = scale, Mini = mini == 1, Rail = rail, E = e, Slim = true });
                if (L.W <= 0 || L.H <= 0) return $"档{scale} 极简{mini} 工具{tool} rail{rail} e{e}：宽高非正";
                if (L.W > 900) return $"档{scale} 工具{tool}：宽度异常 {L.W:F0}";
            }
            return null;
        });

        Check("按钮矩形都在面板里（不越界）", () =>
        {
            foreach (int mini in new[] { 0, 1 })
            {
                var L = PanelDraw.Compute(new PanelState { Mini = mini == 1, Slim = true, E = 1 });
                var panel = new Rect(0, 0, L.W, L.H);
                foreach (var t in L.Tiles)
                    if (!panel.Contains(t)) return $"极简{mini}：有个按钮跑到面板外面了 {t}";
            }
            return null;
        });

        Check("上带的控件不与按钮带重叠", () =>
        {
            foreach (int mini in new[] { 0, 1 })
            foreach (int tool in PanelDraw.VisibleTools(new PanelState { Mini = mini == 1 }))
            {
                var L = PanelDraw.Compute(new PanelState { Tool = tool, Mini = mini == 1, Slim = true, E = 1, Rail = 1 });
                foreach (var it in L.Items)
                    if (it.Bottom > bandBottom(L) + 0.01)
                        return $"极简{mini} 工具{tool}：上带控件压到按钮带（控件底 {it.Bottom:F1} > {bandBottom(L):F1}）";
                if (!L.ActionRect.IsEmpty && L.ActionRect.Bottom > bandBottom(L) + 0.01)
                    return $"工具{tool}：动作按钮压到按钮带";
            }
            return null;

            static double bandBottom(PanelDraw.Layout L) => L.H - L.BtnRow - L.GrooveBandH;
        });

        Check("滑条的命中区不抢按钮的点击", () =>
        {
            foreach (int tool in PanelDraw.VisibleTools(new PanelState()))
            {
                var spec = PanelDraw.SpecOf(new PanelState { Tool = tool });
                if (!spec.HasSlider) continue;
                var L = PanelDraw.Compute(new PanelState { Tool = tool, Slim = true, E = 1 });
                foreach (var t in L.Tiles)
                    if (t.Bottom > L.SliderZoneTop + 0.01 && t.Top < L.SliderZoneBottom)
                        return $"工具{tool}：滑条命中区盖住了按钮（按钮底 {t.Bottom:F1}，命中区从 {L.SliderZoneTop:F1} 开始）";
            }
            return null;
        });

        Check("抽屉：项不重叠、且都在抽屉里；抽屉不压住面板", () =>
        {
            foreach (bool mini in new[] { false, true })
            {
                var L = PanelDraw.Compute(new PanelState { Mini = mini, Slim = true, E = 1, MoreOpen = true });
                if (L.DrawerRect.IsEmpty) return $"极简{mini}：抽屉开着却没有矩形";
                if (L.ContentW < L.DrawerRect.Width - 0.01) return $"极简{mini}：内容宽度装不下抽屉";
                foreach (var it in L.MoreItems)
                {
                    if (!L.DrawerRect.Contains(it.R)) return $"极简{mini}：有抽屉项跑到抽屉外面 {it.R}";
                    foreach (var other in L.MoreItems)
                        if (!other.R.Equals(it.R) && other.R.IntersectsWith(it.R))
                            return $"极简{mini}：两个抽屉项重叠了";
                }
                // 抽屉在面板上方：抽屉底 ≤ 面板顶
                if (L.DrawerRect.Bottom > L.OriginY + 0.01) return $"极简{mini}：抽屉压住了面板";
            }
            return null;
        });

        Check("抽屉比面板宽时：面板居中、内容宽度够", () =>
        {
            var L = PanelDraw.Compute(new PanelState { Mini = true, Slim = true, E = 1, MoreOpen = true });
            if (L.ContentW < PanelDraw.DrawerW) return "内容宽度小于抽屉宽度";
            if (Math.Abs(L.OriginX - (L.ContentW - L.W) / 2) > 0.01) return "面板没有居中";
            if (L.OriginX < 0) return "OriginX 为负";
            return null;
        });

        Check("动画中间态不崩、尺寸单调", () =>
        {
            double lastW = 0;
            foreach (double e in new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 })
            {
                var L = PanelDraw.Compute(new PanelState { Slim = true, E = e, Rail = e });
                if (L.W + 0.01 < lastW) return $"宽度不单调：e={e} 时 {L.W:F1} < 上一档 {lastW:F1}";
                lastW = L.W;
            }
            return null;
        });

        // ---------------------------------------------------------------
        // C. 语义：几件"点一下会发生什么"的约定
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // D. 钉住 / 取消钉住
        // ---------------------------------------------------------------

        Check("取消钉住：工具离开主条、进了未钉组、档位变成自定义", () =>
        {
            var el = New();
            el.SnapAll();
            el.PinTool(PanelDraw.ToolLaser, false);
            el.SnapAll();
            if (Array.IndexOf(PanelDraw.VisibleTools(el.State), PanelDraw.ToolLaser) >= 0)
                return "取消钉住之后激光笔还在主条上";
            if (Array.IndexOf(PanelDraw.UnpinnedTools(el.State), PanelDraw.ToolLaser) < 0)
                return "取消钉住之后工具没进未钉那一组";
            if (el.State.Profile != PanelDraw.ProfileCustom)
                return $"档位是「{PanelDraw.ProfileName(el.State.Profile)}」，应为自定义";
            return null;
        });

        Check("钉回去：回到主条、且按规范顺序排（不是按点击先后）", () =>
        {
            var el = New();
            el.PinTool(PanelDraw.ToolLaser, false);
            el.PinTool(PanelDraw.ToolShapes, false);
            el.PinTool(PanelDraw.ToolLaser, true);      // 先钉激光笔
            el.PinTool(PanelDraw.ToolShapes, true);     // 再钉图形
            el.SnapAll();
            var vis = PanelDraw.VisibleTools(el.State);
            if (Array.IndexOf(vis, PanelDraw.ToolLaser) < 0) return "激光笔没回来";
            if (Array.IndexOf(vis, PanelDraw.ToolShapes) < 0) return "图形没回来";
            for (int i = 1; i < vis.Length; i++)
                if (vis[i] < vis[i - 1]) return "顺序乱了（应按规范顺序排，不按点击先后）";
            if (vis.Length != PanelDraw.AllToolsIndex.Length) return $"钉回来之后是 {vis.Length} 项，应回到 {PanelDraw.AllToolsIndex.Length}";
            return null;
        });

        Check("安全项：笔 / 橡皮 / 更多 取消不了", () =>
        {
            var el = New();
            foreach (int t in new[] { PanelDraw.ToolPen, PanelDraw.ToolEraser, PanelDraw.ToolMore })
            {
                el.PinTool(t, false);
                if (Array.IndexOf(PanelDraw.UnpinnedTools(el.State), t) >= 0)
                    return $"{PanelDraw.Tools[t].Name} 被取消掉了（它是安全项）";
            }
            if (el.State.Profile == PanelDraw.ProfileCustom) return "安全项没法取消，却把档位改成了自定义";
            return null;
        });

        Check("钉住只动钉住集合，不动别的", () =>
        {
            var el = New();
            el.SetTool(PanelDraw.ToolPen);
            el.State.Color = 5;
            el.SnapAll();
            el.PinTool(PanelDraw.ToolLaser, false);
            el.SnapAll();
            if (el.State.Tool != PanelDraw.ToolPen) return "取消钉住把当前工具改了";
            if (el.State.Color != 5) return "取消钉住把颜色改了";
            return null;
        });

        Check("极简档 = 笔 / 橡皮 / 白板 / 更多", () =>
        {
            var vis = PanelDraw.VisibleTools(new PanelState { Mini = true });
            if (vis.Length != 4) return $"可见工具数是 {vis.Length}，应为 4";
            if (vis[0] != PanelDraw.ToolPen) return "第一格不是笔";
            if (vis[1] != PanelDraw.ToolEraser) return "第二格不是橡皮";
            if (vis[2] != PanelDraw.ToolBoard) return "第三格不是白板";
            if (vis[3] != PanelDraw.ToolMore) return "第四格不是更多";
            return null;
        });

        Check("三个界面档位：项数与宽度都对得上", () =>
        {
            var expect = new[] { 4, 12, 12 };          // 极简 / 自定义（默认=全部）/ 完整
            for (int p = 0; p < PanelDraw.Profiles.Length; p++)
            {
                var st = new PanelState { Profile = p, Slim = true, E = 1 };
                int n = PanelDraw.VisibleTools(st).Length;
                if (n != expect[p]) return $"{PanelDraw.ProfileName(p)} 档有 {n} 项，应为 {expect[p]}";
                var L = PanelDraw.Compute(st);
                if (L.W <= 0) return $"{PanelDraw.ProfileName(p)} 档宽度非正";
                if (PanelDraw.ProfileName(p).Length == 0) return "档位没有名字";
            }
            return null;
        });

        Check("循环切档：三下回到原来那一档", () =>
        {
            var el = New();
            int first = el.State.Profile;
            for (int i = 0; i < PanelDraw.Profiles.Length; i++) { el.CycleProfile(); el.SnapAll(); }
            if (el.State.Profile != first) return $"绕一圈之后是 {PanelDraw.ProfileName(el.State.Profile)}，应为 {PanelDraw.ProfileName(first)}";
            if (PanelDraw.Profiles.Length != 3) return "档位数不是 3";
            return null;
        });

        Check("极简档：每个工具的上带控件都放得进面板", () =>
        {
            double panelW = PanelDraw.Compute(new PanelState { Mini = true, Slim = true, E = 1 }).W;
            foreach (int tool in PanelDraw.VisibleTools(new PanelState { Mini = true }))
            {
                var L = PanelDraw.Compute(new PanelState { Tool = tool, Mini = true, Slim = true, E = 1, Rail = 1 });
                foreach (var it in L.Items)
                    if (it.X < -0.5 || it.Right > L.W + 0.5)
                        return $"工具「{PanelDraw.Tools[tool].Name}」的上带控件超出面板（{it.X:F1}..{it.Right:F1}，面板宽 {L.W:F0}）";
                if (!L.ActionRect.IsEmpty && L.ActionRect.Right > L.W + 0.5)
                    return $"工具「{PanelDraw.Tools[tool].Name}」的动作按钮超出面板";
            }
            return null;
        });

        Check("极简档里当前工具不在档内时，落到笔", () =>
        {
            var el = New();
            el.SetTool(PanelDraw.ToolShapes);   // 图形不在极简档里
            el.SnapAll();
            el.ToggleMini();
            el.SnapAll();
            if (el.State.Tool != PanelDraw.ToolPen)
                return $"切到极简档后当前工具是「{PanelDraw.Tools[el.State.Tool].Name}」，应落到笔";
            return null;
        });

        Check("白板：开板自动关穿透，关板不自动开", () =>
        {
            var el = New();
            el.State.PassThrough = true;
            el.SetTool(PanelDraw.BoardTool);    // 开板
            el.SnapAll();
            if (!el.State.BoardOn) return "没开成白板";
            if (el.State.PassThrough) return "开白板之后穿透还开着（会点到看不见的窗口）";
            el.SetTool(PanelDraw.BoardTool);    // 关板
            el.SnapAll();
            if (el.State.BoardOn) return "没关成白板";
            if (el.State.PassThrough) return "关白板之后穿透被自动打开了（不应该）";
            return null;
        });

        Check("白板：三种板色都取得到", () =>
        {
            for (int i = 0; i < PanelDraw.BoardColors.Length; i++)
            {
                var L = PanelDraw.Compute(new PanelState { Tool = PanelDraw.BoardTool, BoardOn = true, BoardColor = i, Rail = 1, E = 1 });
                if (L.Items.Length != PanelDraw.BoardColors.Length)
                    return $"板色 {i}：上带的格子数是 {L.Items.Length}，应为 {PanelDraw.BoardColors.Length}";
            }
            return null;
        });

        Check("后撤/重做：点了不改变当前工具", () =>
        {
            var el = New();
            el.SetTool(PanelDraw.ToolPen);
            el.SnapAll();
            el.SetTool(PanelDraw.ToolUndo);
            el.SnapAll();
            if (el.State.Tool != PanelDraw.ToolPen) return "点后撤把当前工具改掉了";
            el.SetTool(PanelDraw.ToolRedo);
            el.SnapAll();
            if (el.State.Tool != PanelDraw.ToolPen) return "点重做把当前工具改掉了";
            return null;
        });

        Check("「更多」：点了不改当前工具", () =>
        {
            var el = New();
            el.SetTool(PanelDraw.ToolEraser);
            el.SnapAll();
            el.SetTool(PanelDraw.ToolMore);
            el.SnapAll();
            if (el.State.Tool != PanelDraw.ToolEraser) return "点更多把当前工具改掉了";
            return null;
        });

        Check("每一格工具都有自己的上带（不会漏配）", () =>
        {
            foreach (int tool in PanelDraw.VisibleTools(new PanelState()))
            {
                var sp = PanelDraw.SpecOf(new PanelState { Tool = tool });
                if (sp.Kind == PanelDraw.StripKind.None) return $"工具{tool}（{PanelDraw.Tools[tool].Name}）没有上带";
                if (sp.Kind == PanelDraw.StripKind.Segments && sp.Labels.Length == 0) return $"工具{tool}：分段控件没有标签";
                if (!sp.HasSlider && sp.Kind == PanelDraw.StripKind.Colors && !sp.Decorative)
                    return $"工具{tool}：有色片却没有滑条（下带会变成装饰线，是有意的吗？）";
            }
            return null;
        });

        Console.WriteLine($"=== 自检结束：{pass} 通过 / {fail} 失败 ===");
        return fail == 0 ? 0 : 1;
    }

    static PanelElement New() => new PanelElement();
}
