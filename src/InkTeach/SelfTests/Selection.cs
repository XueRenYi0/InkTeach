// 本文件由 App.cs 拆出（2026-10-07）：Selection 这一组。
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
    /// 框选自检：**框到的就该选中**。
    ///
    /// 判据（这是产品语义，不是实现细节）：
    ///   ① 框住全部墨 → 全部选中（老师最常做的动作）；
    ///   ② 框只压住笔身的一部分 → 这条也要选中（"碰到就选中"），
    ///      否则屏幕上永远选不全：笔迹只要有一头在屏幕外（或框拖不到的地方），
    ///      要求"整条都在框里"就永远选不上，用户看到的就是"框了但没全选中"；
    ///   ③ 框在空白处 → 一个都不选；
    ///   ④ 图像对象（截图/粘贴）和旋转过的对象，同一套规则。
    /// </summary>
    private void SelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 框选自检（框到的就该选中）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-32} {detail}");
        }

        float x0 = _virtualX + 300f, y0 = _virtualY + 300f;
        var ink = new Color4(1f, 0f, 1f, 1f);

        Stroke Line(float x, float y, float dx, float dy, float w)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = w };
            s.AddPoint(x, y, 0.5f, NowMs);
            s.AddPoint(x + dx, y + dy, 0.5f, NowMs);
            return s;
        }

        Doc.Clear();
        Doc.ClearHistory();

        var a = Line(x0, y0, 200f, 0f, 6f * DpiScale);            // 横线
        var b = Line(x0, y0 + 200f, 0f, 160f, 40f * DpiScale);    // 竖的粗笔（笔身很宽）
        var c = Line(x0 + 300f, y0, 300f, 300f, 8f * DpiScale);   // 斜线
        var d = Line(x0 - 500f, y0 + 500f, 3000f, 0f, 10f * DpiScale); // 长横线（一头在屏幕外）
        foreach (var s in new[] { a, b, c, d }) Doc.AddStroke(s);

        // 旋转 30° 的一条：包围盒是旋转后的 AABB
        var rot = Line(x0 + 200f, y0 + 500f, 200f, 0f, 10f * DpiScale);
        rot.Transform = Matrix3x2.CreateRotation(MathF.PI / 6f) * Matrix3x2.CreateTranslation(0, 0);
        Doc.AddStroke(rot);

        int n = Doc.Strokes.Count;
        Doc.Selected.Clear();

        // ① 框住全部
        var all = RectF.Empty;
        foreach (var s in Doc.Strokes) all.Add(s.PaddedBounds);
        Doc.ApplyMarquee(all.Inflate(20f));
        Check("框住全部 → 全部选中", Doc.Selected.Count == n, $"{Doc.Selected.Count}/{n} 条");

        // ② 只压住粗竖笔的笔身（中心线在框外）
        Doc.Selected.Clear();
        var body = RectF.Empty;
        body.Add(b.Points[0].X - 30f, b.Points[0].Y + 40f);
        body.Add(b.Points[0].X + 30f, b.Points[0].Y + 100f);
        Doc.ApplyMarquee(body);
        Check("框压住笔身的一角 → 这一条也选中",
            Doc.Selected.Contains(b), $"选中 {Doc.Selected.Count} 条（期望含这条粗竖笔）");

        // ③ 框住屏幕外那条长横线可见的一段：也该选中
        Doc.Selected.Clear();
        var edge = RectF.Empty;
        edge.Add(_virtualX + 10f, d.Points[0].Y - 4f);
        edge.Add(_virtualX + 200f, d.Points[0].Y + 4f);
        Doc.ApplyMarquee(edge);
        Check("框住长线露出来的一段 → 整条选中",
            Doc.Selected.Contains(d), $"选中 {Doc.Selected.Count} 条");

        // ④ 空白处 → 一个都不选
        Doc.Selected.Clear();
        var empty = RectF.Empty;
        empty.Add(_virtualX + 40f, _virtualY + 40f);
        empty.Add(_virtualX + 120f, _virtualY + 120f);
        Doc.ApplyMarquee(empty);
        Check("空白处 → 一个都不选", Doc.Selected.Count == 0, $"{Doc.Selected.Count} 条");

        // ⑤ 精确框一条：不能顺手把别的也选进来
        Doc.Selected.Clear();
        var onlyA = RectF.Empty;
        onlyA.Add(a.Points[0].X - 10f, a.Points[0].Y - 10f);
        onlyA.Add(a.Points[1].X + 10f, a.Points[1].Y + 10f);
        Doc.ApplyMarquee(onlyA);
        Check("框住横线 → 只有它", Doc.Selected.Count == 1 && Doc.Selected[0] == a,
              $"选中 {Doc.Selected.Count} 条");

        // ⑤b 斜线外接矩形的空角：框只和包围盒相交、和墨没有交集 → 不许选中
        //     （2026-10-04 用户实测："框明明和斜线没交集，它却被选中了"）
        Doc.Selected.Clear();
        var bboxCorner = RectF.Empty;
        bboxCorner.Add(x0 + 560f, y0 + 10f);
        bboxCorner.Add(x0 + 600f, y0 + 50f);
        Doc.ApplyMarquee(bboxCorner);
        Check("斜线包围盒的空角 → 不选", !Doc.Selected.Contains(c),
              $"选中 {Doc.Selected.Count} 条");

        // ⑥ 旋转过的对象：框住它的包围盒就该选中
        Doc.Selected.Clear();
        Doc.ApplyMarquee(rot.PaddedBounds.Inflate(6f));
        Check("旋转对象 → 框住包围盒即选中", Doc.Selected.Contains(rot),
              $"选中 {Doc.Selected.Count} 条");

        // ⑦ 截图/粘贴来的图像对象走同一套规则
        var img = ImageData.Adopt(40, 30, new byte[40 * 30 * 4], false);
        if (img != null)
        {
            var placed = Doc.AddImage(img, _virtualX + 300f, _virtualY + 700f, 1f);
            Doc.Selected.Clear();
            Doc.ApplyMarquee(placed.PaddedBounds.Inflate(6f));
            Check("图像对象 → 同样能被框到", Doc.Selected.Contains(placed),
                  $"选中 {Doc.Selected.Count} 条");
        }

        // ⑧ 宽墨迹：选中框必须把**墨**圈住（不是只圈中心线）
        //    64 像素宽的荧光笔，屏幕上是一条 64 像素宽的带子；框要是按中心线算，
        //    四条边全压在墨里面——用户实测就是嫌这个。
        {
            var wide = new Stroke
            {
                Tool = Tool.Highlighter, Color = HighlighterCurrent,
                Width = 32f * DpiScale,                 // 最粗的一档荧光笔
            };
            wide.AddPoint(x0 + 400f, y0 + 800f, 0.5f, NowMs);
            wide.AddPoint(x0 + 900f, y0 + 800f, 0.5f, NowMs);
            Doc.AddStroke(wide);
            Doc.SelectOnly(new[] { wide });

            var frame = SelectionHandles.FrameOf(Doc.Selected);
            var inkBox = wide.InkBounds;                // 无变换：局部 = 画布
            float paint = wide.Width * 0.5f;            // 鼠标压感 0.5 → 半宽 = 半个笔宽
            bool contains = frame.Local.MinX <= inkBox.MinX + 0.01f
                         && frame.Local.MinY <= inkBox.MinY + 0.01f
                         && frame.Local.MaxX >= inkBox.MaxX - 0.01f
                         && frame.Local.MaxY >= inkBox.MaxY - 0.01f;
            float slack = MathF.Max(
                MathF.Max(frame.Local.MinX - inkBox.MinX, frame.Local.MinY - inkBox.MinY),
                MathF.Max(inkBox.MaxX - frame.Local.MaxX, inkBox.MaxY - frame.Local.MaxY));
            Check("宽笔选中框把墨圈住", contains,
                  contains ? $"框 {frame.Local.MaxX - frame.Local.MinX:F0}×"
                             + $"{frame.Local.MaxY - frame.Local.MinY:F0}px，"
                             + $"墨是中心线外扩 {paint:F0}px"
                           : $"框 {frame.Local.MinX:F0},{frame.Local.MinY:F0}→"
                             + $"{frame.Local.MaxX:F0},{frame.Local.MaxY:F0}；"
                             + $"墨 {inkBox.MinX:F0},{inkBox.MinY:F0}→"
                             + $"{inkBox.MaxX:F0},{inkBox.MaxY:F0}");
            Check("宽笔选中框不虚胖", slack <= 2f, $"比墨大出 {slack:F1}px（要 ≤ 2）");
            Doc.Selected.Clear();
        }

        // ⑨ **点选**（2026-09-15 新增）：按在墨上就选中那一条
        {
            float tol = ClickToleranceLogical * DpiScale;

            Doc.Selected.Clear();
            var hitA = Doc.SelectAt(a.Points[0].X + 100f, a.Points[0].Y, tol);
            Check("点选：点在墨上 → 选中这一条",
                  ReferenceEquals(hitA, a) && Doc.Selected.Count == 1 && Doc.Selected[0] == a,
                  $"点到 {(hitA == null ? "空" : "一条")}，选中 {Doc.Selected.Count} 条");

            // 细笔（1.5 逻辑像素）：容差让"点得中"成为可能，但离太远仍然不该命中
            var thin = Line(x0 + 1200f, y0, 400f, 0f, 1.5f * DpiScale);
            Doc.AddStroke(thin);
            var near = Doc.SelectAt(x0 + 1300f, y0 + 3f, tol);
            var far = Doc.HitObjectAt(x0 + 1300f, y0 + 12f, tol);
            Check("点选：细笔有容差（3px 命中、12px 不命中）",
                  ReferenceEquals(near, thin) && far == null,
                  $"近处点到 {(near == null ? "空" : "细笔")}，远处 {(far == null ? "空" : "误命中")}");

            // 两条重叠：取**最上面**那条（后画的）
            var under = Line(x0 + 2000f, y0, 200f, 0f, 12f * DpiScale);
            var over = Line(x0 + 2000f, y0, 200f, 0f, 12f * DpiScale);
            Doc.AddStroke(under);
            Doc.AddStroke(over);
            var top = Doc.HitObjectAt(x0 + 2100f, y0, tol);
            Check("点选：重叠处取最上面那条", ReferenceEquals(top, over),
                  ReferenceEquals(top, over) ? "取到后画的那条" : "取错了（取到下面那条）");

            // Shift 加选 / 再点同一条移出 / Alt 减选
            Doc.Selected.Clear();
            Doc.SelectAt(a.Points[0].X + 100f, a.Points[0].Y, tol);
            Doc.SelectAt(c.Points[0].X + 150f, c.Points[0].Y + 150f, tol, additive: true);
            Check("点选：Shift 加选 → 两条",
                  Doc.Selected.Count == 2 && Doc.Selected.Contains(a) && Doc.Selected.Contains(c),
                  $"选中 {Doc.Selected.Count} 条");

            Doc.SelectAt(c.Points[0].X + 150f, c.Points[0].Y + 150f, tol, additive: true);
            Check("点选：再 Shift 点同一条 → 移出（切换）",
                  Doc.Selected.Count == 1 && !Doc.Selected.Contains(c),
                  $"选中 {Doc.Selected.Count} 条");

            Doc.SelectAt(a.Points[0].X + 100f, a.Points[0].Y, tol, subtractive: true);
            Check("点选：Alt 减选 → 空", Doc.Selected.Count == 0, $"选中 {Doc.Selected.Count} 条");

            // 点空白：返回空、**不动选中**（交给框选那一步去处理"单击空白＝取消"）
            Doc.SelectOnly(new[] { a });
            var miss = Doc.SelectAt(_virtualX + 40f, _virtualY + 40f, tol);
            Check("点选：点空白 → 返回空、选中不动（交给框选）",
                  miss == null && Doc.Selected.Count == 1 && Doc.Selected[0] == a,
                  miss == null ? "没命中，选中保持 1 条" : "空白处竟然命中了");

            // **被像素橡皮擦断之后，缺口里不算墨**（点缺口不该命中）
            var cut = Line(x0 + 2400f, y0, 600f, 0f, 10f * DpiScale);
            Doc.AddStroke(cut);
            float gapX = cut.Points[0].X + 300f;
            Doc.EraseRectAt(gapX, y0, 30f, 30f);
            var inGap = Doc.HitObjectAt(gapX, y0, tol);
            var onInk = Doc.HitObjectAt(cut.Points[0].X + 60f, y0, tol);
            Check("点选：擦断的缺口不算墨（点缺口不命中、点墨命中）",
                  inGap == null && onInk != null,
                  $"缺口 {(inGap == null ? "没命中" : "误命中")}，"
                  + $"墨上 {(onInk != null ? "命中某一段" : "没命中")}");
        }

        // ⑩ 操作条：**只给下限、不翻面**（用户 2026-09-15 定的）
        {
            var vp = ViewportCanvas;

            var low = Line(x0, _virtualY + _virtualH - 30f, 200f, 0f, 8f * DpiScale);
            Doc.AddStroke(low);
            Doc.SelectOnly(new[] { low });
            var lowAabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var barLow = SelectionHandles.BarRect(lowAabb, DpiScale, vp);
            float floor = vp.MaxY - SelectionHandles.BarMinBottomMarginLogical * DpiScale;
            Check("操作条：贴屏幕下边时停在下限（不越界、也不翻面）",
                  MathF.Abs(barLow.MaxY - floor) <= 0.5f,
                  $"条底 {barLow.MaxY:F0}，下限 {floor:F0}（可见下边 {vp.MaxY:F0}）");

            var leftLine = Line(vp.MinX + 5f, _virtualY + 200f, 0f, 200f, 8f * DpiScale);
            Doc.AddStroke(leftLine);
            Doc.SelectOnly(new[] { leftLine });
            var leftAabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var barLeft = SelectionHandles.BarRect(leftAabb, DpiScale, vp);
            bool inView = barLeft.MinX >= vp.MinX - 0.5f && barLeft.MaxX <= vp.MaxX + 0.5f;
            bool barsHit = true;
            for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
            {
                var r = SelectionHandles.BarButtonRect(i, leftAabb, DpiScale, vp);
                int got = SelectionHandles.BarButtonAt((r.MinX + r.MaxX) * 0.5f,
                                                       (r.MinY + r.MaxY) * 0.5f,
                                                       leftAabb, DpiScale, vp);
                if (got != i) barsHit = false;
            }
            Check("操作条：贴屏幕左边 → 整条在可见区内、按钮都能点中",
                  inView && barsHit,
                  $"条 x {barLeft.MinX:F0}..{barLeft.MaxX:F0}（可见 {vp.MinX:F0}..{vp.MaxX:F0}），"
                  + $"按钮命中 {(barsHit ? "全中" : "有点不中")}");
        }

        // ⑩.4 悬停提示：选中操作条的每一格（2026-10-02；图标-only 的唯一文字出口）
        {
            Tool = Tool.Marquee;                 // 让操作条处于"显示"状态（上面这些用例就是框选在跑）
            SettleFrames(120);
            SelBarHover = (int)SelBarButton.Delete;
            UpdateEngineTooltip();
            SettleFrames(650);
            StepEngineTooltip();                  // 自检里没有主循环，手动推一下"到点"
            var delKeys = Keys.KeyText(KeyAction.DeleteSelected);
            Check("操作条悬停 0.5 秒：提示出现（删除 + 键位）",
                  TooltipShown && TooltipTitle == "删除" && TooltipKey == delKeys,
                  $"亮={TooltipShown}，标题={TooltipTitle}，键={TooltipKey}（期望 {delKeys}）");

            // 覆盖不变量：十格逐个数，一格都不许漏文案
            bool barTipsAll = true; string barTipsMiss = "";
            for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
            {
                SelBarHover = i;
                UpdateEngineTooltip();
                if (string.IsNullOrEmpty(TooltipTitle)) { barTipsAll = false; barTipsMiss += i + " "; }
            }
            Check("操作条每一格都有提示文案（逐格数）", barTipsAll,
                  barTipsAll ? $"{SelectionHandles.BarButtonCount} 格" : $"缺：{barTipsMiss}");

            // 开关真的在闸门上：关掉之后同样的悬停不出提示
            SetTooltipsFromUi(false);
            SelBarHover = (int)SelBarButton.Color;
            UpdateEngineTooltip();
            SettleFrames(650);
            StepEngineTooltip();
            Check("关掉「悬停提示」：引擎侧提示不再出现", !TooltipShown, $"亮={TooltipShown}");
            SetTooltipsFromUi(true);
            SelBarHover = -1;
            UpdateEngineTooltip();
        }

        // ⑪ 点选的**手势接线**（引擎那一侧：无选中时点一条、点空白、Shift 加选、收窄成单选）
        {
            float tol = ClickToleranceLogical * DpiScale;

            Doc.Selected.Clear();
            Tool = Tool.Marquee;
            bool tookA = SelectionGestureForTest(a.Points[0].X + 100f, a.Points[0].Y);
            Check("手势：**没选中**时点一条 → 接住并选中它（这条最容易写漏）",
                  tookA && Doc.Selected.Count == 1 && Doc.Selected[0] == a && SelDragging,
                  $"接住={tookA}，选中 {Doc.Selected.Count} 条，拖动态={SelDragging}");
            EndSelectionGestureForTest();

            Doc.Selected.Clear();
            bool tookBlank = SelectionGestureForTest(_virtualX + 40f, _virtualY + 40f);
            Check("手势：点空白 → 不接（交给框选）", !tookBlank, $"接住={tookBlank}");

            Doc.Selected.Clear();
            SelectionGestureForTest(a.Points[0].X + 100f, a.Points[0].Y);
            EndSelectionGestureForTest();
            bool tookC = SelectionGestureForTest(c.Points[0].X + 150f, c.Points[0].Y + 150f, shift: true);
            Check("手势：Shift 点第二条 → 加选，且不进拖动",
                  tookC && Doc.Selected.Count == 2 && !SelDragging,
                  $"接住={tookC}，选中 {Doc.Selected.Count} 条，拖动态={SelDragging}");

            // "点一下把多选收窄成单选"：按在多选中的一条上、松手不移动
            Doc.Selected.Clear();
            Doc.SelectOnly(new[] { a, c });
            SelectionGestureForTest(a.Points[0].X + 100f, a.Points[0].Y);   // 落在 a 的框里 → 整体拖动
            EndSelectionGestureForTest();                                   // 没移动 → 收窄
            Check("手势：点多选中的一条、不移动 → 收窄成只选它",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == a,
                  $"选中 {Doc.Selected.Count} 条{(Doc.Selected.Count == 1 && Doc.Selected[0] == a ? "（就是那一条）" : "")}");

            Doc.Selected.Clear();
            SelDragging = false;
            _ = tol;
        }

        // ⑫ 框选矩形**咬着指针**（用户 2026-09-15 反馈："感觉有点不大跟手"）
        //
        // 以前按下之后每次移动都累积 min/max，于是框只会**变大**：指针往回走框不跟着缩，
        // 屏幕上看到的是"扫过的最大范围"，而不是"从起点拉到现在的这一个矩形"。
        // 现在按**锚点 ↔ 当前点**算（和同行一致），往回拖要能缩回去。
        // 截图取景框用的是同一套算法，这里一起验。
        {
            SelMode = SelectMode.Rect;
            Tool = Tool.Marquee;

            // ① 拖到 +400 再拖回 +100 松手：框应该是 [锚点, +100]，不是 [锚点, +400]
            Doc.Clear();
            Doc.ClearHistory();
            var sweptOnly = Line(x0 + 200f, y0 + 50f, 100f, 0f, 6f * DpiScale);  // 扫过、但不在最终框里
            var inside = Line(x0 + 20f, y0 + 50f, 60f, 0f, 6f * DpiScale);       // 在最终框里
            Doc.AddStroke(sweptOnly);
            Doc.AddStroke(inside);
            MarqueeDragForTest(new[]
            {
                new Vector2(x0, y0),
                new Vector2(x0 + 400f, y0 + 400f),      // 先拖远
                new Vector2(x0 + 100f, y0 + 100f),      // 再拖回来松手
            });
            bool sweptPicked = Doc.Selected.Contains(sweptOnly);
            Check("框选：拖远再拖回来 → 框跟着缩（不是扫过的最大范围）",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == inside,
                  $"选中 {Doc.Selected.Count} 条（扫过但已退回的那条"
                  + (sweptPicked ? "**被误选**" : "没被选") + "）");

            // ② 从锚点往左上拖：反向也要能拉出框
            Doc.Clear();
            Doc.ClearHistory();
            var upLeft = Line(x0 - 120f, y0 - 120f, 60f, 0f, 6f * DpiScale);
            Doc.AddStroke(upLeft);
            MarqueeDragForTest(new[]
            {
                new Vector2(x0, y0),
                new Vector2(x0 - 180f, y0 - 180f),
            });
            Check("框选：从锚点往左上拖 → 反向也能拉出框",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == upLeft,
                  $"选中 {Doc.Selected.Count} 条");

            // ③ 截图取景框：同一套锚点算法（只驱动"按下 → 拖"，不抓屏）
            CaptureFrameDragForTest(x0, y0, x0 + 400f, y0 + 400f, x0 + 100f, y0 + 100f);
            bool capShrunk = MathF.Abs(CapMinX - x0) < 0.01f && MathF.Abs(CapMaxX - (x0 + 100f)) < 0.01f
                          && MathF.Abs(CapMinY - y0) < 0.01f && MathF.Abs(CapMaxY - (y0 + 100f)) < 0.01f;
            Check("截图取景框：同一套锚点算法（拖远再拖回也缩）", capShrunk,
                  $"框 = ({CapMinX:F0},{CapMinY:F0})..({CapMaxX:F0},{CapMaxY:F0})，"
                  + $"期望 ({x0:F0},{y0:F0})..({x0 + 100f:F0},{y0 + 100f:F0})");
            CaptureActive = false;
        }

        // ⑬ "选中是临时上下文"（用户 2026-09-15 定的规则，也是 InkClass/PPT/Figma 的惯例）
        {
            // 换工具 → 收起；按"框选"（本来就是它）→ 保留
            Doc.SelectOnly(new[] { a });
            RunActionForTest(KeyAction.ToolPen);
            bool clearedOnSwitch = Doc.Selected.Count == 0 && Tool == Tool.Pen;
            Tool = Tool.Marquee;                       // 直接换回来（不经过 SwitchTool，免得又清）
            Doc.SelectOnly(new[] { a });
            RunActionForTest(KeyAction.ToolMarquee);
            Check("换工具收起选区；按框选（同一工具）保留",
                  clearedOnSwitch && Doc.Selected.Count == 1,
                  $"换笔后 {(clearedOnSwitch ? "已收起" : "没收起")}，再按框选后选中 {Doc.Selected.Count} 条");

            // 对象从文档里消失 → 自动从选中里去掉
            Doc.Clear();
            Doc.ClearHistory();
            var gone = Line(x0, y0, 200f, 0f, 8f * DpiScale);
            Doc.AddStroke(gone);
            Doc.SelectOnly(new[] { gone });
            Doc.RemoveStroke(gone);
            Check("对象被删/被擦掉 → 自动从选中里去掉", Doc.Selected.Count == 0,
                  $"选中 {Doc.Selected.Count} 条");

            // 滚动**不算**"操作选区"：滚轮只改相机，不该把选中弄没
            Doc.SelectOnly(new[] { a });
            float camBefore = ViewOffsetY;
            HandleWheel((IntPtr)(-120L << 16));        // 高 16 位 = 滚轮增量
            bool camMoved = MathF.Abs(ViewOffsetY - camBefore) > 0.5f;
            ViewOffsetY = camBefore;
            Check("滚动不清选中（滚动是「看」，不是「操作对象」）",
                  Doc.Selected.Count == 1 && camMoved,
                  $"选中 {Doc.Selected.Count} 条，相机 {(camMoved ? "动了" : "没动")}");

            // **复制拖拽模式**：点复制按钮进模式 → 按住选中内容拖 → 拖出副本（原件不动）→
            // 可以连着拖第二份；**克隆 + 位移算一步撤销**
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var src = Line(x0, y0, 200f, 0f, 8f * DpiScale);
            Doc.AddStroke(src);
            Doc.SelectOnly(new[] { src });
            var srcXform0 = src.Transform;

            RunBarActionForTest((int)SelBarButton.Copy);              // 复制按钮（第二轮从 0 挪到 5）
            bool armed = CopyDragArmed && Doc.Selected.Count == 1;

            bool took = SelectionGestureForTest(x0 + 100f, y0);        // 按在原件上（框内 → 拖动）
            bool cloneMade = Doc.Strokes.Count == 2;                   // 克隆发生在按下那一刻
            if (took)
            {
                UpdateSelectionGestureForTest(x0 + 300f, y0 + 60f);    // 拖出去
                EndSelectionGestureForTest();
            }
            var firstCopy = Doc.Selected.Count == 1 ? Doc.Selected[0] : null;
            bool firstOk = armed && took && cloneMade
                        && firstCopy != null && !ReferenceEquals(firstCopy, src)
                        && src.Transform.Equals(srcXform0);

            // 再拖一次 → 第三份（「可连续多份」）
            bool took2 = SelectionGestureForTest(x0 + 300f, y0 + 60f);
            if (took2)
            {
                UpdateSelectionGestureForTest(x0 + 500f, y0 + 120f);
                EndSelectionGestureForTest();
            }
            bool secondOk = Doc.Strokes.Count == 3 && Doc.Selected.Count == 1;

            Check("复制拖拽模式：拖出副本、原件不动、可连续拖第二份",
                  firstOk && secondOk,
                  $"进模式={armed}，第一次接住={took}（克隆重合={cloneMade}），"
                  + $"第二次接住={took2}；对象数 {Doc.Strokes.Count}（应 3），"
                  + $"原件 {(src.Transform.Equals(srcXform0) ? "没动" : "动了")}");

            // 每拖出一份 = 一步撤销（**克隆 + 位移是一步**，不是两步）
            Doc.Undo();
            bool backToTwo = Doc.Strokes.Count == 2;
            Doc.Undo();
            Check("复制拖拽：一次撤销回退一份（克隆+位移合成一步）",
                  backToTwo && Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], src)
                  && src.Transform.Equals(srcXform0),
                  $"撤一次后 {2} 条、再撤一次 {Doc.Strokes.Count} 条，"
                  + $"原件位置 {(src.Transform.Equals(srcXform0) ? "回到原位" : "没回来")}");

            RunBarActionForTest((int)SelBarButton.Copy);              // 退出模式（收尾）
            Check("再点一次复制按钮 → 退出复制拖拽模式", !CopyDragArmed, $"armed={CopyDragArmed}");
        }

        // ⑭ 拖动 / 旋转期间的画法（2026-09-16）：方案 A 收装饰 + 方案 B 拖动预览
        //
        // 这一段的判据全部落在**屏幕像素**上，因为要验的两件事都是"看得见"的性质：
        //   · 拖动中手柄与操作条到底还在不在屏幕上（方案 A）；
        //   · 被拖的那块到底有没有跟着指针走、并且**没被选中的墨一个像素都没动**（方案 B）。
        //
        // 前置条件是把板铺成**不透明**并选一个**没有别的程序参与**的颜色：
        // 桌面上"没画东西的地方"是别的程序，白像素到处都是，数不出"手柄的白在不在"。
        // 板色取深灰，于是四种东西互不撞色：板=深灰、墨=品红、手柄与操作条=白、选中框=蓝。
        {
            bool boardWas = BoardOn;
            var boardColorWas = BoardColor;
            float camWas = ViewOffsetY;
            BoardOn = true;
            BoardColor = new Color4(0.10f, 0.10f, 0.12f, 1f);
            ViewOffsetY = 0f;                        // 屏幕坐标 == 画布坐标，抓屏好算
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;

            float dpi = DpiScale;
            Stroke FatLine(float yy, Color4 col)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = col, Width = 10f * dpi };
                s.AddPoint(x0, yy, 0.5f, NowMs);
                s.AddPoint(x0 + 260f * dpi, yy, 0.5f, NowMs);
                return s;
            }
            var dragInk = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));    // 品红：被拖的这一条
            var refInk = FatLine(y0 + 1100f, new Color4(0f, 1f, 0f, 1f));    // 绿：什么都不做的参照物
            Doc.AddStroke(dragInk);
            Doc.AddStroke(refInk);
            Doc.SelectOnly(new[] { dragInk });
            SettleFrames(250);

            var frame0 = LiveSelectionFrame;
            var barRect = SelectionHandles.BarRect(frame0.CanvasAabb, dpi, ViewportCanvas);
            var handleTL = SelectionHandles.CanvasPosition(SelHandle.TopLeft, frame0, dpi);

            int BarWhite() => ScreenProbe.CountNear(
                (int)barRect.MinX, (int)barRect.MinY,
                (int)(barRect.MaxX - barRect.MinX), (int)(barRect.MaxY - barRect.MinY),
                255, 255, 255, 30);
            int HandleWhite() => ScreenProbe.CountNear(
                (int)handleTL.X - 20, (int)handleTL.Y - 20, 40, 40, 255, 255, 255, 30);

            var oldBox = dragInk.PaddedBounds.Inflate(8f);
            int BoxMagenta(in RectF r) => ScreenProbe.CountMagenta(
                (int)r.MinX, (int)r.MinY, (int)(r.MaxX - r.MinX), (int)(r.MaxY - r.MinY));
            var refBox = refInk.PaddedBounds.Inflate(10f);
            byte[] RefShot() => ScreenProbe.CaptureRegion(
                (int)refBox.MinX, (int)refBox.MinY,
                (int)(refBox.MaxX - refBox.MinX), (int)(refBox.MaxY - refBox.MinY));

            int barBefore = BarWhite(), handleBefore = HandleWhite();
            int oldBefore = BoxMagenta(oldBox);
            var refBefore = RefShot();

            // 按住选中内容中间拖走（中间离手柄最远，命中的一定是"整体拖动"）
            float px = x0 + 130f * dpi, py = y0 + 700f;
            float dx = 150f, dy = 210f;
            bool tookDrag = SelectionGestureForTest(px, py);
            UpdateSelectionGestureForTest(px + dx, py + dy);
            SettleFrames(90);

            var movedBox = new RectF
            {
                MinX = oldBox.MinX + dx, MinY = oldBox.MinY + dy,
                MaxX = oldBox.MaxX + dx, MaxY = oldBox.MaxY + dy,
            };
            // 墨量比对只在**中间那一段**做：两端压着缩放手柄，而手柄拖动中是收起来的，
            // 拿整条去比会把"手柄盖住了几块墨"当成"两种画法不一致"。
            var midStrip = new RectF
            {
                MinX = (movedBox.MinX + movedBox.MaxX) * 0.5f - 200f,
                MinY = (movedBox.MinY + movedBox.MaxY) * 0.5f - 30f,
                MaxX = (movedBox.MinX + movedBox.MaxX) * 0.5f - 100f,
                MaxY = (movedBox.MinY + movedBox.MaxY) * 0.5f + 30f,
            };
            int barDuring = BarWhite(), handleDuring = HandleWhite();
            int oldDuring = BoxMagenta(oldBox), newDuring = BoxMagenta(movedBox);
            int stripDuring = BoxMagenta(midStrip);
            var refDuring = RefShot();
            int patchDuring = _windows[0].LastPatchCount;

            Check("拖动中：手柄与操作条收起来（方案 A）",
                  tookDrag && SelChromeCollapsed
                  && barDuring * 10 < barBefore && handleDuring * 10 < handleBefore,
                  $"接住={tookDrag}，收起={SelChromeCollapsed}；"
                  + $"操作条的白 {barBefore}→{barDuring}，手柄的白 {handleBefore}→{handleDuring}");

            Check("拖动中：内容层一帧都不重画（方案 B）",
                  patchDuring == 0,
                  $"上一帧光栅化分块 {patchDuring} 块（老做法要把走过的面积整块重画一遍）");

            Check("拖动中：预览真的上屏（新位置有墨、原位置干净）",
                  newDuring > 200 && oldDuring == 0,
                  $"新位置 {newDuring} 像素，原位置 {oldDuring} 像素");

            Check("拖动中：没被选中的墨迹一个像素都不许变",
                  ScreenProbe.DiffCount(refBefore, refDuring) == 0,
                  $"参照物区域差异 {ScreenProbe.DiffCount(refBefore, refDuring)} 像素");

            EndSelectionGestureForTest();
            SettleFrames(250);

            // 松手后框跟着内容走到了新位置，所以手柄与操作条要**按现在的框**重新取位置
            var frameAfter = LiveSelectionFrame;
            var barRectAfter = SelectionHandles.BarRect(frameAfter.CanvasAabb, dpi, ViewportCanvas);
            var handleAfterPt = SelectionHandles.CanvasPosition(SelHandle.TopLeft, frameAfter, dpi);
            int barAfter = ScreenProbe.CountNear(
                (int)barRectAfter.MinX, (int)barRectAfter.MinY,
                (int)(barRectAfter.MaxX - barRectAfter.MinX), (int)(barRectAfter.MaxY - barRectAfter.MinY),
                255, 255, 255, 30);
            int handleAfter = ScreenProbe.CountNear(
                (int)handleAfterPt.X - 20, (int)handleAfterPt.Y - 20, 40, 40, 255, 255, 255, 30);
            int oldAfter = BoxMagenta(oldBox), newAfter = BoxMagenta(movedBox);
            int stripAfter = BoxMagenta(midStrip);
            int parity = Math.Abs(stripAfter - stripDuring);
            var refAfter = RefShot();

            Check("松手后：装饰回来、模型才动、原位置干净",
                  !SelChromeCollapsed && handleAfter > handleBefore / 2 && barAfter > barBefore / 2
                  && !dragInk.Transform.IsIdentity && oldAfter == 0,
                  $"手柄的白 {handleAfter}（拖动前 {handleBefore}），条的白 {barAfter}，"
                  + $"模型 {(dragInk.Transform.IsIdentity ? "还没动" : "动了")}，原位置 {oldAfter} 像素");

            Check("松手前后同一段墨的墨量一致（预览与内容层像素一致）",
                  stripDuring > 200 && parity <= Math.Max(20, stripDuring / 50),
                  $"中间那一段：拖动中 {stripDuring} 像素，松手后 {stripAfter} 像素（差 {parity}）");

            Check("松手后参照物仍然一个像素都没变",
                  ScreenProbe.DiffCount(refBefore, refAfter) == 0,
                  $"差异 {ScreenProbe.DiffCount(refBefore, refAfter)} 像素");

            Doc.Undo();
            SettleFrames(250);
            Check("一次撤销回到原位（方案 B 没改撤销语义）",
                  dragInk.Transform.IsIdentity && BoxMagenta(oldBox) > oldBefore / 2
                  && BoxMagenta(movedBox) == 0,
                  $"模型 {(dragInk.Transform.IsIdentity ? "回原位" : "没回来")}，"
                  + $"原位置品红 {BoxMagenta(oldBox)} 像素（拖着时 {oldBefore}），"
                  + $"拖过去的位置 {BoxMagenta(movedBox)} 像素（该是 0）");

            // ---- 方案 A 的另一半：旋转中**留**旋转柄与度数标签 ----
            Doc.Clear();
            Doc.ClearHistory();
            var spinInk = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(spinInk);
            Doc.SelectOnly(new[] { spinInk });
            SettleFrames(250);

            var f2 = LiveSelectionFrame;
            var grip2 = SelectionHandles.CanvasPosition(SelHandle.Rotate, f2, dpi);
            var pivot2 = new Vector2((f2.CanvasAabb.MinX + f2.CanvasAabb.MaxX) * 0.5f,
                                     (f2.CanvasAabb.MinY + f2.CanvasAabb.MaxY) * 0.5f);
            float arm2 = Vector2.Distance(grip2, pivot2);
            var barRect2 = SelectionHandles.BarRect(f2.CanvasAabb, dpi, ViewportCanvas);
            int SpinBarWhite() => ScreenProbe.CountNear(
                (int)barRect2.MinX, (int)barRect2.MinY,
                (int)(barRect2.MaxX - barRect2.MinX), (int)(barRect2.MaxY - barRect2.MinY),
                255, 255, 255, 30);
            // 度数标签就在**当前**旋转柄的上方（手柄跟着内容转，所以每步都要重算位置）
            int LabelAccent()
            {
                var h = SelectionHandles.CanvasPosition(SelHandle.Rotate, LiveSelectionFrame, dpi);
                int pw = (int)(120f * dpi), ph = (int)(70f * dpi);
                return ScreenProbe.CountNear((int)(h.X - pw * 0.5f), (int)(h.Y - ph),
                                             pw, ph, 0, 120, 212, 40);
            }

            int spinBarBefore = SpinBarWhite();
            bool tookSpin = SelectionGestureForTest(grip2.X, grip2.Y);
            UpdateSelectionGestureForTest(pivot2.X - arm2, pivot2.Y);     // 屏幕上逆时针 90°
            SettleFrames(90);

            int labelPixels = LabelAccent();
            int spinBarDuring = SpinBarWhite();
            Check("旋转中：柄与度数标签还在、操作条收起来（方案 A）",
                  tookSpin && SelRotating && SelChromeCollapsed
                  && spinBarDuring * 10 < spinBarBefore && labelPixels > 800,
                  $"接住={tookSpin}，旋转中={SelRotating}，收起={SelChromeCollapsed}；"
                  + $"度数标签强调色 {labelPixels} 像素，操作条的白 {spinBarBefore}→{spinBarDuring}");

            EndSelectionGestureForTest();
            SettleFrames(200);
            var f2After = LiveSelectionFrame;
            var barRect2After = SelectionHandles.BarRect(f2After.CanvasAabb, dpi, ViewportCanvas);
            int spinBarAfter = ScreenProbe.CountNear(
                (int)barRect2After.MinX, (int)barRect2After.MinY,
                (int)(barRect2After.MaxX - barRect2After.MinX), (int)(barRect2After.MaxY - barRect2After.MinY),
                255, 255, 255, 30);
            Check("旋转松手后：标签消失、操作条回来",
                  !SelRotating && spinBarAfter > spinBarBefore / 2,
                  $"条的白 {spinBarAfter}（旋转前 {spinBarBefore}）");

            // ---- 多选拖动：框是**轴对齐并集**，拖动中同样要跟着内容走 ----
            // （LiveSelectionFrame 的另一条分支：单选靠"框坐标系右乘"，多选要
            //   逐条过实时矩阵再并。少了这条，多选拖动时会只剩框留在原地。）
            Doc.Clear();
            Doc.ClearHistory();
            var m1 = FatLine(y0 + 600f, new Color4(1f, 0f, 1f, 1f));
            var m2 = FatLine(y0 + 980f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(m1);
            Doc.AddStroke(m2);
            Doc.SelectOnly(new[] { m1, m2 });
            SettleFrames(200);

            var multiBefore = LiveSelectionFrame.CanvasAabb;
            float mx = (multiBefore.MinX + multiBefore.MaxX) * 0.5f;
            float my = (multiBefore.MinY + multiBefore.MaxY) * 0.5f;
            bool tookMulti = SelectionGestureForTest(mx, my);
            UpdateSelectionGestureForTest(mx + 120f, my - 80f);
            SettleFrames(60);
            var multiDuring = LiveSelectionFrame.CanvasAabb;
            bool frameFollows = MathF.Abs(multiDuring.MinX - (multiBefore.MinX + 120f)) < 1.5f
                             && MathF.Abs(multiDuring.MinY - (multiBefore.MinY - 80f)) < 1.5f;
            Check("多选拖动：框（轴对齐并集）跟着内容走",
                  tookMulti && DragPreviewActive && frameFollows,
                  $"{multiBefore.MinX:F0},{multiBefore.MinY:F0} → {multiDuring.MinX:F0},{multiDuring.MinY:F0}"
                  + $"（期望 +120,-80）");
            EndSelectionGestureForTest();
            SettleFrames(120);

            // ---- 缩放手势也走同一条"摘出去"的路，但**装饰不收** ----
            // （方案 B 对三种手势一视同仁；方案 A 只收移动与旋转——
            //   拖某个手柄时，另外几个手柄是有用的参照。）
            Doc.Clear();
            Doc.ClearHistory();
            var scInk = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(scInk);
            Doc.SelectOnly(new[] { scInk });
            SettleFrames(200);

            var fScale = LiveSelectionFrame;
            var gripR = SelectionHandles.CanvasPosition(SelHandle.Right, fScale, dpi);
            float widthBefore = fScale.CanvasAabb.MaxX - fScale.CanvasAabb.MinX;
            bool tookScale = SelectionGestureForTest(gripR.X, gripR.Y);
            UpdateSelectionGestureForTest(gripR.X + 200f, gripR.Y);
            SettleFrames(60);
            var fScaled = LiveSelectionFrame;
            float widthDuring = fScaled.CanvasAabb.MaxX - fScaled.CanvasAabb.MinX;
            Check("缩放中：框跟着手柄变宽，且装饰不收（方案 A 只管移动与旋转）",
                  tookScale && widthDuring > widthBefore + 150f && !SelChromeCollapsed,
                  $"宽 {widthBefore:F0} → {widthDuring:F0}（期望 +200），收起={SelChromeCollapsed}");
            EndSelectionGestureForTest();
            SettleFrames(120);

            // ---- 单选一个**转过角度**的对象：框也必须是轴对齐的正矩形 ----
            // （用户 2026-09-16 定：单选也走多选那条量法。早先是"单选跟对象转"，
            //   判据就一条——框坐标系必须是单位阵，且框恰好是对象墨迹的外接正矩形。
            //   有人把它改回"跟对象转"，这里会红。）
            Doc.Clear();
            Doc.ClearHistory();
            var tilted = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(tilted);
            Doc.SelectOnly(new[] { tilted });
            var tCenter = new Vector2((tilted.PaddedBounds.MinX + tilted.PaddedBounds.MaxX) * 0.5f,
                                      (tilted.PaddedBounds.MinY + tilted.PaddedBounds.MaxY) * 0.5f);
            Doc.ApplyTransform(Matrix3x2.CreateRotation(-40f * MathF.PI / 180f, tCenter));
            SettleFrames(150);

            var tf = SelectionHandles.FrameOf(Doc.Selected);
            var tExpect = tilted.WorldInkBounds;
            bool tAligned = tf.ToCanvas.IsIdentity
                         && MathF.Abs(tf.Local.MinX - tExpect.MinX) < 0.01f
                         && MathF.Abs(tf.Local.MinY - tExpect.MinY) < 0.01f
                         && MathF.Abs(tf.Local.MaxX - tExpect.MaxX) < 0.01f
                         && MathF.Abs(tf.Local.MaxY - tExpect.MaxY) < 0.01f;
            var tGrip = SelectionHandles.CanvasPosition(SelHandle.Rotate, tf, dpi);
            bool tGripOnTop = MathF.Abs(tGrip.X - (tf.Local.MinX + tf.Local.MaxX) * 0.5f) < 0.01f
                           && tGrip.Y < tf.Local.MinY;
            Check("单选一个转过的对象：框是正矩形、旋转柄在正上方",
                  tAligned && tGripOnTop,
                  $"框 {tf.Local.MinX:F0},{tf.Local.MinY:F0}..{tf.Local.MaxX:F0},{tf.Local.MaxY:F0}"
                  + $"（墨迹 {tExpect.MinX:F0},{tExpect.MinY:F0}..{tExpect.MaxX:F0},{tExpect.MaxY:F0}）；"
                  + $"柄在 ({tGrip.X:F0},{tGrip.Y:F0})");

            // 再转一手：拖动中框仍然轴对齐，而且是**每帧重新贴合**当前内容
            var tPivot = new Vector2((tf.Local.MinX + tf.Local.MaxX) * 0.5f,
                                     (tf.Local.MinY + tf.Local.MaxY) * 0.5f);
            float tArm = Vector2.Distance(tGrip, tPivot);
            float tA0 = MathF.Atan2(tGrip.Y - tPivot.Y, tGrip.X - tPivot.X);
            float tA1 = tA0 - 30f * MathF.PI / 180f;              // 屏幕上逆时针 30°
            bool tookTilt = SelectionGestureForTest(tGrip.X, tGrip.Y);
            UpdateSelectionGestureForTest(tPivot.X + tArm * MathF.Cos(tA1),
                                          tPivot.Y + tArm * MathF.Sin(tA1));
            SettleFrames(60);

            var dFrame = LiveSelectionFrame;
            var wb = tilted.WorldInkBounds;
            var expBox = RectF.Empty;
            foreach (var corner in new[]
            {
                new Vector2(wb.MinX, wb.MinY), new Vector2(wb.MaxX, wb.MinY),
                new Vector2(wb.MaxX, wb.MaxY), new Vector2(wb.MinX, wb.MaxY),
            })
            {
                var q = Vector2.Transform(corner, DragPreviewMatrix);
                expBox.Add(q.X, q.Y);
            }
            bool tLiveAligned = dFrame.ToCanvas.IsIdentity
                             && MathF.Abs(dFrame.Local.MinX - expBox.MinX) < 0.5f
                             && MathF.Abs(dFrame.Local.MaxX - expBox.MaxX) < 0.5f;
            Check("旋转中：框仍正着，并每帧重新贴合当前内容",
                  tookTilt && tLiveAligned,
                  $"框宽 {dFrame.Local.MaxX - dFrame.Local.MinX:F0}"
                  + $"（内容外接 {(expBox.MaxX - expBox.MinX):F0}），轴对齐={dFrame.ToCanvas.IsIdentity}");
            EndSelectionGestureForTest();
            SettleFrames(120);

            // ---- 操作条第二轮（2026-09-16）：九格 / 收起 / 提示条 / 颜色面板 / 锁定 / 层级 ----
            Doc.Clear();
            Doc.ClearHistory();
            var bA = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
            var bB = FatLine(y0 + 900f, new Color4(1f, 0f, 1f, 1f));
            Doc.AddStroke(bA);
            Doc.AddStroke(bB);
            Doc.SelectOnly(new[] { bA });
            SettleFrames(220);

            var barFrame = LiveSelectionFrame;
            var barBoxR = SelectionHandles.BarRect(barFrame.CanvasAabb, dpi, ViewportCanvas);
            int Bar2White() => ScreenProbe.CountNear((int)barBoxR.MinX, (int)barBoxR.MinY,
                (int)(barBoxR.MaxX - barBoxR.MinX), (int)(barBoxR.MaxY - barBoxR.MinY), 255, 255, 255, 24);

            // ① 九格逐格命中（纯函数，便宜且能钉住"下标 ↔ 语义"不错位）
            bool allHit = true; string hitNote = "";
            for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
            {
                var br = SelectionHandles.BarButtonRect(i, barFrame.CanvasAabb, dpi, ViewportCanvas);
                int got = SelectionHandles.BarButtonAt((br.MinX + br.MaxX) * 0.5f,
                                                      (br.MinY + br.MaxY) * 0.5f,
                                                      barFrame.CanvasAabb, dpi, ViewportCanvas);
                if (got != i) { allHit = false; hitNote = $"第 {i} 格命中成了 {got}"; }
            }
            Check("操作条十格：每格都点得中自己（灰格也占一格——它只是不响应，见下）", allHit,
                  allHit ? $"{SelectionHandles.BarButtonCount} 格逐格核对" : hitNote);

            // ② 收起 / 展开（用户更正：条首那个 ✕ 是"收起工具条"，不是"取消选择"）
            int barWhiteBeforeCollapse = Bar2White();
            RunBarActionForTest((int)SelBarButton.Collapse);
            SettleFrames(140);
            var dotR = SelectionHandles.BarCollapsedRect(barFrame.CanvasAabb, dpi, ViewportCanvas);
            int barWhiteCollapsed = Bar2White();
            int dotWhite = ScreenProbe.CountNear((int)dotR.MinX, (int)dotR.MinY,
                (int)(dotR.MaxX - dotR.MinX), (int)(dotR.MaxY - dotR.MinY), 255, 255, 255, 24);
            bool tookDot = SelectionGestureForTest((dotR.MinX + dotR.MaxX) * 0.5f,
                                                   (dotR.MinY + dotR.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(140);
            Check("收起：整条没了、圆钮在；点圆钮又展开",
                  // 判据用"白底掉了 80% 以上"而不是"归零"：**圆钮自己就落在条的那块区域里**
                  // （它居中在框下方，和条的横纵位置一致），所以那块不会真的全黑。
                  tookDot && !SelBarCollapsed && barWhiteCollapsed * 5 < barWhiteBeforeCollapse
                  && dotWhite > 300 && Bar2White() > 3000,
                  $"条的白底 {barWhiteBeforeCollapse} → 收起后 {barWhiteCollapsed} → 展开后 {Bar2White()}，"
                  + $"圆钮的白 {dotWhite}（圆心 {dotR.MinX:F0},{dotR.MinY:F0}），"
                  + $"点中圆钮={tookDot}，收起态={SelBarCollapsed}");

            // ②.5（8.2.0 验收①）浮层尺寸在 token 容差内 + 量尺打表
            //
            // 量尺（PrintOverlayMetrics）先打一张表：收紧要收在哪儿、收了多少，前后两版
            // 用的是同一把尺子；然后是判据本身（见 CheckFloatOverlayTokens）。
            PrintOverlayMetrics();
            CheckFloatOverlayTokens((n, ok, d) => Check(n, ok, d));

            // ③ 颜色面板：点"颜色"开面板 → 点色片改色 → 一次撤销回原色
            RunBarActionForTest((int)SelBarButton.Color);
            SettleFrames(140);
            var panelR = SelectionHandles.PanelRect(barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                    SelectionHandles.SwatchCount);
            int panelWhite = ScreenProbe.CountNear((int)panelR.MinX, (int)panelR.MinY,
                (int)(panelR.MaxX - panelR.MinX), (int)(panelR.MaxY - panelR.MinY), 255, 255, 255, 24);
            int swatchPick = 5;                                   // 橙
            var swR = SelectionHandles.SwatchRect(swatchPick, barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                  SelectionHandles.SwatchCount);
            bool tookSwatch = SelectionGestureForTest((swR.MinX + swR.MaxX) * 0.5f,
                                                     (swR.MinY + swR.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(140);
            var wantColor = InkPalette.SelectionSwatches[swatchPick].Color;
            bool colorChanged = MathF.Abs(bA.Color.R - wantColor.R) < 0.02f
                             && MathF.Abs(bA.Color.G - wantColor.G) < 0.02f
                             && MathF.Abs(bA.Color.B - wantColor.B) < 0.02f;
            Doc.Undo();
            SettleFrames(140);
            bool colorBack = bA.Color.R > 0.9f && bA.Color.G < 0.1f && bA.Color.B > 0.9f;
            Check("颜色面板：点色片真的改色、一次撤销回原色",
                  SelPanelOpen == SelPanel.Ink && panelWhite > 5000 && tookSwatch
                  && colorChanged && colorBack,
                  $"面板白底 {panelWhite} 像素；改色 {(colorChanged ? "对" : "不对")}，"
                  + $"撤销后回原色 {(colorBack ? "是" : "否")}");

            // ⑤ 粗细滑条（8.2.0：连续拖动 + 实时数值）
            //
            // 判据（用户 2026-09-30："做滑动，记得滑动的时候显示值，参考色带"）：
            //   · 按下 → 拖到**中点** → 松手：选中对象的宽度 == 滑条中点的连续值
            //     （不再是"最近的那一档"）；
            //   · 拖动中引擎报的"显示值" == 同一个数（画在滑钮上方的那行字读的就是它）；
            //   · 这一次拖拽**只记一步撤销**（拖动中反复 Redo、松手才提交）；
            //   · 选中框跟着变（墨真的变粗了），一次撤销回原宽。
            var sliderT = SelectionHandles.SliderRect(barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                      SelectionHandles.SwatchCount);
            var (wMin, wMax) = WidthRangeForSelection();
            float wBefore = bA.Width / dpi;
            var frameWBeforeBox = LiveSelectionFrame.CanvasAabb;
            float frameWBefore = frameWBeforeBox.MaxX - frameWBeforeBox.MinX;
            int undoBefore = Doc.UndoDepth;
            float sx0 = SelectionHandles.SliderXOfT(0.25f, barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                    SelectionHandles.SwatchCount);
            float sx1 = SelectionHandles.SliderXOfT(0.5f, barFrame.CanvasAabb, dpi, ViewportCanvas,
                                                    SelectionHandles.SwatchCount);
            float sy = (sliderT.MinY + sliderT.MaxY) * 0.5f;
            bool tookSlider = SelectionGestureForTest(sx0, sy);
            PanelDragMoveForTest(sx1, sy);
            SettleFrames(60);
            float shownValue = WidthSliderDragValue;       // 拖动中"显示的值"（HUD 同口径）
            EndPanelDragForTest(sx1, sy);
            SettleFrames(140);
            float midWant = wMin + (wMax - wMin) * 0.5f;
            bool midApplied = MathF.Abs(bA.Width / dpi - midWant) < 0.15f;
            bool shownMatches = MathF.Abs(shownValue - midWant) < 0.15f;
            int depthAfterDrag = Doc.UndoDepth;
            bool oneUndoStep = depthAfterDrag == undoBefore + 1;
            float midAppliedValue = bA.Width / dpi;
            float frameWAfter = LiveSelectionFrame.CanvasAabb.MaxX - LiveSelectionFrame.CanvasAabb.MinX;
            bool widthChanged = midAppliedValue > wBefore + 1f;
            Doc.Undo();
            SettleFrames(140);
            bool widthBack = MathF.Abs(bA.Width / dpi - wBefore) < 0.02f;
            Check("粗细滑条：拖到中点 → 连续值生效、显示值一致、一步撤销、框跟着变",
                  tookSlider && midApplied && shownMatches && oneUndoStep && widthChanged
                  && frameWAfter > frameWBefore + 1f && widthBack,
                  $"宽 {wBefore:F1} → {midAppliedValue:F1}（拖动中显示 {shownValue:F1}，中点 {midWant:F1}）；"
                  + $"撤销栈 +{depthAfterDrag - undoBefore}（该是 1），撤销后回 {bA.Width / dpi:F1}；"
                  + $"框宽 {frameWBefore:F0} → {frameWAfter:F0}");

            // ⑤.2 工具宽那条路的记档（计划里点名的那半条）：
            // 面板滑条改的是**选中对象**；"改当前工具 + 落盘"仍走主条滑条那条
            // `SetWidthFromUi`——这里直接用那条真实入口点一下，确认引擎值和偏好都落上
            // （8.0.8/8.0.9 加的 wv.pen）。
            // **2026-10-09 晚起：滑条值吸附到最近一档**——11.5 离 10 最近（10 与 20 之间），
            // 吸附到 **10**；wv.pen 记的也是吸附后的值（界面不会出现"不存在的档"）。
            {
                float oldPen = PenWidthLogical;
                SetWidthFromUi(11.5f);
                bool engineValue = MathF.Abs(PenWidthLogical - 10f) < 0.01f;
                bool prefSaved = GetUiPref("wv.pen") == "10";
                Check("工具粗细：SetWidthFromUi 吸附到最近档 + 写 wv.pen（能落盘）",
                      engineValue && prefSaved,
                      $"笔宽 {PenWidthLogical:F1}（应吸附到 10），wv.pen={GetUiPref("wv.pen") ?? "(空)"}");
                SetWidthFromUi(oldPen);                    // 还原
            }

            // ⑤.5 自定义取色（8.2.0）：点末格开色板 → 拖色相条 → 直接改选中墨迹、一步撤销
            {
                int sc = SelectionHandles.SwatchCount;
                var customR = SelectionHandles.SwatchRect(sc - 1, barFrame.CanvasAabb, dpi, ViewportCanvas, sc);
                bool opened = SelectionGestureForTest((customR.MinX + customR.MaxX) * 0.5f,
                                                      (customR.MinY + customR.MaxY) * 0.5f);
                EndSelectionGestureForTest();
                SettleFrames(120);
                bool pickShown = CustomColorOpenForTest;

                var hueR = SelectionHandles.PickHueRect(barFrame.CanvasAabb, dpi, ViewportCanvas, sc);
                float hx = (hueR.MinX + hueR.MaxX) * 0.5f;
                float hy = hueR.MinY + (hueR.MaxY - hueR.MinY) * 0.5f;      // h ≈ 0.5 = 青
                int depth0 = Doc.UndoDepth;
                bool tookHue = SelectionGestureForTest(hx, hy);
                PanelDragMoveForTest(hx, hy + 1f);                          // 一点点位移 = 真的拖动
                EndPanelDragForTest(hx, hy + 1f);
                SettleFrames(140);
                var (ph, ps, pv) = PickHsv;
                bool hueTurned = MathF.Abs(ph - 0.5f) < 0.03f;
                // h=0.5、s=1、v=1 的青：G、B 接近 1、R 接近 0
                bool inkCyan = bA.Color.R < 0.08f && bA.Color.G > 0.9f && bA.Color.B > 0.9f;
                int depthAfterApply = Doc.UndoDepth;
                bool oneStep = depthAfterApply == depth0 + 1;
                Doc.Undo();
                SettleFrames(120);
                bool inkBack = bA.Color.R > 0.9f && bA.Color.B > 0.9f;      // 回品红
                Check("自定义取色：点末格开板、拖色相条直接改选中墨、一步撤销",
                      opened && pickShown && tookHue && hueTurned && inkCyan && oneStep && inkBack,
                      $"开板={opened}（开着 {pickShown}）；H {ph:F2}（期望 0.50）；"
                      + $"墨色 ({bA.Color.R:F2},{bA.Color.G:F2},{bA.Color.B:F2})；撤销栈 +{depthAfterApply - depth0}");

                // **松开在色板外 = 取消**（用户定的"点面板外 = 取消，保持原色"）
                var svR = SelectionHandles.PickSvRect(barFrame.CanvasAabb, dpi, ViewportCanvas, sc);
                int depth1 = Doc.UndoDepth;
                bool tookSv = SelectionGestureForTest(svR.MinX + (svR.MaxX - svR.MinX) * 0.25f,
                                                      svR.MinY + 8f);
                PanelDragMoveForTest(svR.MaxX - 2f, svR.MinY + 2f);
                EndPanelDragForTest(0f, 0f);                                // 松在屏幕外 = 板外
                SettleFrames(120);
                bool stillMagenta = bA.Color.R > 0.9f && bA.Color.B > 0.9f;
                bool noUndo = Doc.UndoDepth == depth1;
                Check("自定义取色：松开在板外 = 取消（预览撤回、保持原色、不记撤销）",
                      tookSv && stillMagenta && noUndo,
                      $"墨色 ({bA.Color.R:F2},{bA.Color.G:F2},{bA.Color.B:F2})，撤销栈 +{Doc.UndoDepth - depth1}");

                // 荧光笔套色必须保留半透明（`InkPalette.ForStroke` 是唯一那份规则）
                var hl = new Stroke { Color = InkPalette.ToHighlighter(new Color4(1f, 0.5f, 0.2f, 1f)) };
                var applied = InkPalette.ForStroke(new Color4(0f, 0f, 1f, 1f), hl);
                Check("自定义取色：荧光笔（半透明墨）套色时保留半透明",
                      MathF.Abs(applied.A - 0.32f) < 0.001f && applied.B > 0.99f && applied.R < 0.01f,
                      $"alpha {applied.A:F2}（该 0.32），RGB ({applied.R:F2},{applied.G:F2},{applied.B:F2})");
            }

            // ⑥ 锁定（用户定的 B 语义）：锁上以后能选中、但拖不动、也删不掉
            RunBarActionForTest((int)SelBarButton.Color);          // 先收面板（免得盖住条）
            RunBarActionForTest((int)SelBarButton.Lock);
            SettleFrames(120);
            bool lockedNow = bA.Locked;
            var lockC = new Vector2((barFrame.CanvasAabb.MinX + barFrame.CanvasAabb.MaxX) * 0.5f,
                                    (barFrame.CanvasAabb.MinY + barFrame.CanvasAabb.MaxY) * 0.5f);
            int depthBefore = Doc.UndoDepth;
            bool tookLockedDrag = SelectionGestureForTest(lockC.X, lockC.Y);
            UpdateSelectionGestureForTest(lockC.X + 160f, lockC.Y + 60f);
            EndSelectionGestureForTest();
            SettleFrames(120);
            bool stayed = bA.Transform.IsIdentity && Doc.UndoDepth == depthBefore;
            int nBefore = Doc.Strokes.Count;
            RunBarActionForTest((int)SelBarButton.Delete);
            SettleFrames(120);
            bool survivedDelete = Doc.Strokes.Contains(bA) && Doc.Strokes.Count == nBefore;
            Check("锁定：能选中，但拖不动、也删不掉（B 语义）",
                  lockedNow && tookLockedDrag && stayed && survivedDelete,
                  $"锁上={lockedNow}，拖了以后位置 {(bA.Transform.IsIdentity ? "没动" : "动了")}，"
                  + $"撤销栈 +{Doc.UndoDepth - depthBefore}（该是 0），删除后还在={Doc.Strokes.Contains(bA)}");

            // ⑦ 解锁之后能拖（B 语义的另一半：锁是可以随时解开的）
            Doc.SelectOnly(new[] { bA });
            RunBarActionForTest((int)SelBarButton.Lock);           // 再点一次 = 解锁
            SettleFrames(120);
            var unlockC = new Vector2((LiveSelectionFrame.CanvasAabb.MinX + LiveSelectionFrame.CanvasAabb.MaxX) * 0.5f,
                                      (LiveSelectionFrame.CanvasAabb.MinY + LiveSelectionFrame.CanvasAabb.MaxY) * 0.5f);
            SelectionGestureForTest(unlockC.X, unlockC.Y);
            UpdateSelectionGestureForTest(unlockC.X + 120f, unlockC.Y);
            EndSelectionGestureForTest();
            SettleFrames(140);
            Check("解锁之后又能拖了", !bA.Locked && !bA.Transform.IsIdentity,
                  $"锁={bA.Locked}，位置 {(bA.Transform.IsIdentity ? "没动" : "动了")}");
            Doc.Undo();
            SettleFrames(120);

            // ⑧ 层级：置顶（下标关系反过来）→ 一次撤销回原顺序
            Doc.SelectOnly(new[] { bA });
            int idxBefore = Doc.Strokes.IndexOf(bA);
            RunBarActionForTest((int)SelBarButton.Layer);          // 开层级面板
            SettleFrames(120);
            var frontCell = SelectionHandles.LayerCellRect(0, LiveSelectionFrame.CanvasAabb, dpi, ViewportCanvas);
            bool tookFront = SelectionGestureForTest((frontCell.MinX + frontCell.MaxX) * 0.5f,
                                                    (frontCell.MinY + frontCell.MaxY) * 0.5f);
            EndSelectionGestureForTest();
            SettleFrames(140);
            bool onTop = Doc.Strokes.IndexOf(bA) == Doc.Strokes.Count - 1;
            Doc.Undo();
            SettleFrames(140);
            Check("层级：置顶把这一条移到最上，一次撤销回原顺序",
                  tookFront && onTop && Doc.Strokes.IndexOf(bA) == idxBefore,
                  $"下标 {idxBefore} → 置顶后 {Doc.Strokes.Count - 1}（最上）→ 撤销后 {Doc.Strokes.IndexOf(bA)}");

            // ⑨ 导出那一格：**已接**（离屏渲染 → 系统"另存为" → 落盘；自检里对话框是关的）。
            // 判据两条：
            //   · 点了**真的走导出这条路**（`ExportAttempts` +1）——不自检这一步的话，
            //     "点了没反应"和"点了在跑导出"长得一模一样；
            //   · 但**不动文档、不进撤销栈、不改选中**——导出是只读操作，污染文档就是 bug。
            // 屏幕上不弹提示条（用户 2026-09-16 明确不要提示条），只写控制台。
            {
                int n0 = Doc.Strokes.Count, d0 = Doc.UndoDepth, sel0 = Doc.Selected.Count;
                int a0 = ExportAttempts;
                RunBarActionForTest((int)SelBarButton.Export);
                SettleFrames(80);
                Check("导出格子：点了真的走导出，而且不动文档、不进撤销栈",
                      ExportAttempts == a0 + 1
                      && Doc.Strokes.Count == n0 && Doc.UndoDepth == d0 && Doc.Selected.Count == sel0,
                      $"导出 {a0}→{ExportAttempts} 次；对象 {n0}→{Doc.Strokes.Count}，"
                      + $"撤销栈 +{Doc.UndoDepth - d0}，选中 {sel0}→{Doc.Selected.Count}");
            }

            // ---- 选中逻辑三连测（用户点名要的）：一条 / 多条 / 里面混着图片 ----
            {
                // (1) 单选一条：框 = 它自己的墨迹包围盒（不是中心线、也不虚胖）
                Doc.Clear();
                Doc.ClearHistory();
                var one = FatLine(y0 + 700f, new Color4(1f, 0f, 1f, 1f));
                Doc.AddStroke(one);
                Doc.SelectOnly(new[] { one });
                SettleFrames(160);
                var oneF = SelectionHandles.FrameOf(Doc.Selected);
                var oneInk = one.WorldInkBounds;
                bool oneFrameOk = MathF.Abs(oneF.Local.MinX - oneInk.MinX) < 0.01f
                               && MathF.Abs(oneF.Local.MaxY - oneInk.MaxY) < 0.01f;

                // (2) 多条：框 = 并集；**改一次颜色只记一步撤销**；置顶后内部相对顺序不变
                var two = FatLine(y0 + 1000f, new Color4(1f, 0f, 1f, 1f));
                Doc.AddStroke(two);
                Doc.SelectOnly(new[] { one, two });
                SettleFrames(160);
                var twoF = SelectionHandles.FrameOf(Doc.Selected);
                bool twoFrameOk = twoF.Local.MinY < oneInk.MinY + 1f
                               && twoF.Local.MaxY > two.WorldInkBounds.MaxY - 1f;
                int d0 = Doc.UndoDepth;
                SetSelectionColor(InkPalette.SelectionSwatches[9].Color);      // 蓝
                bool bothColored = one.Color.B > 0.8f && two.Color.B > 0.8f;
                bool oneUndo = Doc.UndoDepth == d0 + 1;
                Doc.Undo();
                SettleFrames(120);
                // 置顶要有"别人在下面"才谈得上：再加一条**不选中**的（否则"全选"是特例，
                // 引擎会拒绝并提示"整页都选中了，没有层级可调"——第一版自检就踩了这个）。
                var bystander = FatLine(y0 + 1150f, new Color4(1f, 0f, 1f, 1f));
                Doc.AddStroke(bystander);
                Doc.SelectOnly(new[] { one, two });
                SettleFrames(120);
                int iOne = Doc.Strokes.IndexOf(one), iTwo = Doc.Strokes.IndexOf(two);
                ReorderSelection(toFront: true);                               // 两条一起置顶
                SettleFrames(120);
                bool orderKept = Doc.Strokes.IndexOf(one) < Doc.Strokes.IndexOf(two)
                              && Doc.Strokes.IndexOf(two) == Doc.Strokes.Count - 1;
                Doc.Undo();
                SettleFrames(120);
                bool orderBack = Doc.Strokes.IndexOf(one) == iOne && Doc.Strokes.IndexOf(two) == iTwo;

                // (3) 里面混着图片：改颜色/粗细**跳过图片**，翻转**连图片一起翻**，
                //     删除/框选都算上它（它是普通对象）
                var img3 = ImageData.Adopt(32, 24, new byte[32 * 24 * 4], false);
                var placedImg = img3 != null ? Doc.AddImage(img3, x0 + 200f, y0 + 1300f, 1f) : null;
                if (placedImg != null)
                {
                    Doc.SelectOnly(new[] { one, placedImg });
                    SettleFrames(160);
                    var imgColor = placedImg.Color; var imgWidth = placedImg.Width;
                    int d1 = Doc.UndoDepth;
                    SetSelectionColor(InkPalette.SelectionSwatches[5].Color);  // 橙
                    bool imgColorKept = placedImg.Color.Equals(imgColor);
                    bool inkChanged = one.Color.R > 0.9f && one.Color.G > 0.4f && one.Color.B < 0.2f;
                    SetSelectionWidth(24f);
                    bool imgWidthKept = MathF.Abs(placedImg.Width - imgWidth) < 0.01f;
                    bool twoSteps = Doc.UndoDepth == d1 + 2;
                    Doc.Undo(); Doc.Undo();
                    SettleFrames(160);
                    // 翻转：图像也该跟着翻（M11 变负）
                    Doc.SelectOnly(new[] { placedImg });
                    Doc.ApplyTransform(SelectionHandles.MirrorMatrix(placedImg.WorldInkBounds, true));
                    SettleFrames(120);
                    bool imgFlipped = placedImg.Transform.M11 < -0.5f;
                    Check("含图片的选中：改色/改粗细跳过图片、翻转照翻、各记一步撤销",
                          imgColorKept && inkChanged && imgWidthKept && twoSteps && imgFlipped,
                          $"图片颜色 {(imgColorKept ? "没动" : "被改了")}、粗细 {(imgWidthKept ? "没动" : "被改了")}；"
                          + $"墨变色={(inkChanged ? "是" : "否")}；两步撤销={(twoSteps ? "对" : "错")}；"
                          + $"图片翻转={(imgFlipped ? "翻了" : "没翻")}");
                }
                Check("单选一条 / 多选多条：框按墨迹范围、多选按并集，改一次只记一步撤销",
                      oneFrameOk && twoFrameOk && bothColored && oneUndo && orderKept && orderBack,
                      $"单选框贴合墨迹={(oneFrameOk ? "是" : "否")}，多选并集={(twoFrameOk ? "是" : "否")}，"
                      + $"两条都变色={(bothColored ? "是" : "否")}，一步撤销={(oneUndo ? "对" : "错")}，"
                      + $"置顶保持内部顺序={(orderKept && orderBack ? "是" : "否")}");
            }

            BoardOn = boardWas;
            BoardColor = boardColorWas;
            ViewOffsetY = camWas;
            Doc.Clear();
            Doc.ClearHistory();
        }

        // ---- ⑩ 键盘微调 / 缩放（2026-10-05）：连续动作合并成一步撤销 ----
        //
        // 用户报过的那条"移歪了想撤销、要按十几次 Ctrl+Z"在这里收口：
        // 方向键 / `Ctrl+=` / `Ctrl+-` 连续触发 → 撤销栈只多 1 条；停手或做别的事才另起一条。
        {
            Doc.Clear();
            Doc.ClearHistory();
            var mk = Line(x0, y0, 200f, 0f, 6f * DpiScale);
            Doc.AddStroke(mk);
            Doc.SelectOnly(new[] { mk });
            SettleFrames(120);

            // ① 连按 5 下微调 → 位置右移 5px，撤销栈只多 1 条
            float b0 = mk.PaddedBounds.MinX;
            int dNudge = Doc.UndoDepth;
            for (int i = 0; i < 5; i++) RunActionForTest(KeyAction.NudgeRight);
            SettleFrames(120);
            Check("连按 5 下微调：位置真的右移 5px、撤销只多 1 条",
                  Math.Abs(mk.PaddedBounds.MinX - (b0 + 5f)) < 0.05f && Doc.UndoDepth == dNudge + 1,
                  $"左缘 {b0:F1} → {mk.PaddedBounds.MinX:F1}，撤销 {dNudge} → {Doc.UndoDepth}");

            // ② 中途做别的动作 = 封口 → 下一次微调另起一条
            RunActionForTest(KeyAction.ToggleHud);
            RunActionForTest(KeyAction.ToggleHud);
            RunActionForTest(KeyAction.NudgeRight);
            SettleFrames(120);
            Check("中间做了别的动作：微调另起一条撤销", Doc.UndoDepth == dNudge + 2,
                  $"撤销 {Doc.UndoDepth}（应 {dNudge + 2}）");

            RunActionForTest(KeyAction.Undo);
            RunActionForTest(KeyAction.Undo);
            SettleFrames(120);
            Check("两次撤销 → 位置完全回到原处（两条记录各撤一步）",
                  Math.Abs(mk.PaddedBounds.MinX - b0) < 0.05f,
                  $"左缘 {mk.PaddedBounds.MinX:F1}（应 {b0:F1}）");

            // ③ 键盘缩放：选区中心为锚、连按合并（放大一段 / 缩小一段各一条撤销）
            Doc.ClearHistory();
            Doc.SelectOnly(new[] { mk });
            SettleFrames(80);
            float cx0 = (mk.PaddedBounds.MinX + mk.PaddedBounds.MaxX) * 0.5f;
            float cy0 = (mk.PaddedBounds.MinY + mk.PaddedBounds.MaxY) * 0.5f;
            int dScale = Doc.UndoDepth;
            RunActionForTest(KeyAction.ScaleUp);
            RunActionForTest(KeyAction.ScaleUp);
            SettleFrames(80);
            float sc = mk.Transform.M11;
            float cx1 = (mk.PaddedBounds.MinX + mk.PaddedBounds.MaxX) * 0.5f;
            float cy1 = (mk.PaddedBounds.MinY + mk.PaddedBounds.MaxY) * 0.5f;
            Check("连按两下 Ctrl+=：缩放 ×1.21、中心不动、撤销只多 1 条",
                  Math.Abs(sc - 1.21f) < 0.015f
                  && Math.Abs(cx1 - cx0) < 0.6f && Math.Abs(cy1 - cy0) < 0.6f
                  && Doc.UndoDepth == dScale + 1,
                  $"M11={sc:F3}，中心 ({cx0:F1},{cy0:F1}) → ({cx1:F1},{cy1:F1})，撤销 +{Doc.UndoDepth - dScale}");

            RunActionForTest(KeyAction.ToggleHud);   // 封口 → 缩小另起一条
            RunActionForTest(KeyAction.ToggleHud);
            RunActionForTest(KeyAction.ScaleDown);
            RunActionForTest(KeyAction.ScaleDown);
            SettleFrames(80);
            Check("封口后连按两下 Ctrl+-：回到 ×1.0", Math.Abs(mk.Transform.M11 - 1f) < 0.02f,
                  $"M11={mk.Transform.M11:F3}");

            RunActionForTest(KeyAction.Undo);
            SettleFrames(80);
            Check("撤销缩小那段 → 回到 ×1.21（会话各撤各的）",
                  Math.Abs(mk.Transform.M11 - 1.21f) < 0.02f, $"M11={mk.Transform.M11:F3}");
            RunActionForTest(KeyAction.Undo);
            SettleFrames(80);
            Check("再撤销放大那段 → 回到 ×1.0", Math.Abs(mk.Transform.M11 - 1f) < 0.001f,
                  $"M11={mk.Transform.M11:F3}");

            Doc.Clear();
            Doc.ClearHistory();
        }

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 项：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }


    private void SelBenchTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 选中与变换性能 ===");
        Console.WriteLine();
        Console.WriteLine("  ① 老做法：每帧改模型（SetTransformLive）→ 内容层按脏区修补");
        Console.WriteLine("  画布里  | 变换 | 修补均 | 修补最长 | 每帧记录均 | 每帧最长");
        Console.WriteLine("  --------|------|--------|----------|------------|--------");

        foreach (int total in new[] { 0, 1000, 10000 })
        {
            Doc.Clear();
            Doc.ClearHistory();
            if (total > 0) GenerateStrokes(total);
            SettleFrames(150);

            // 模拟真实用法：用户框选**一小撮**（20 个），而不是把一万笔全选中
            Doc.Selected.Clear();
            int pick = Math.Min(20, Doc.Strokes.Count);
            for (int i = 0; i < pick; i++) Doc.Selected.Add(Doc.Strokes[i]);

            var targets = Doc.Selected.ToArray();
            const int steps = 30;
            double patchSum = 0, recordSum = 0, patchMax = 0, recordMax = 0;
            var w = _windows[0];

            for (int k = 0; k < steps; k++)
            {
                var m = Matrix3x2.CreateTranslation(6f, 3f);
                foreach (var s in targets)
                {
                    Doc.Dirty.Add(s.PaddedBounds);          // 旧位置
                    Doc.SetTransformLive(s, s.Transform * m);
                    Doc.Dirty.Add(s.PaddedBounds);          // 新位置
                }
                _dirty = true;
                SettleFrames(10);

                double p = w.LastPatchMs, r = w.LastRecordMs;
                patchSum += p; recordSum += r;
                if (p > patchMax) patchMax = p;
                if (r > recordMax) recordMax = r;
            }

            Console.WriteLine($"  {total,7} | {targets.Length,4} | {patchSum / steps,6:F2} | "
                            + $"{patchMax,8:F2} | {recordSum / steps,10:F2} | {recordMax,8:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("  判据：修补耗时**不该**随画布里已有笔画数明显增长——区域修补只处理");
        Console.WriteLine("  脏区内的对象，跟总数无关。若明显增长，说明退化成了整层重建。");

        // ---- ② 现在的拖动路径（方案 B：拖动预览）---------------------------
        //
        // 同一批对象、同样的拖动，走**真实的手势路径**：按住 → 每步挪一点 → 松手。
        // 这一条要盯的是两个数：
        //   · 内容层每帧重画的块数 —— 方案 B 之后应当是 **0**（模型不动、块全有效）；
        //   · 每帧记录耗时 —— 老做法那个"散布全屏 19ms"是否真的掉下来。
        Console.WriteLine();
        Console.WriteLine("  ② 现在的拖动（方案 B：拖动预览，内容层不动）");
        Console.WriteLine("  画布里  | 变换 | 起手一次重画 | 拖动中重画 | 拖动中补丁均 | 每帧记录均 | 每帧最长");
        Console.WriteLine("  --------|------|--------------|------------|--------------|------------|--------");
        foreach (int total in new[] { 1000, 10000 })
        {
            Doc.Clear();
            Doc.ClearHistory();
            if (total > 0) GenerateStrokes(total);
            SettleFrames(150);

            // 刻意挑**散布全屏**的 20 条（和老做法那张表同一个场景）
            Doc.Selected.Clear();
            int pick = Math.Min(20, Doc.Strokes.Count);
            int stride = Math.Max(1, Doc.Strokes.Count / Math.Max(1, pick));
            for (int i = 0; i < pick; i++)
                Doc.Selected.Add(Doc.Strokes[Math.Min(Doc.Strokes.Count - 1, i * stride)]);

            var fr = SelectionHandles.FrameOf(Doc.Selected);
            var aabb = fr.CanvasAabb;
            float px = (aabb.MinX + aabb.MaxX) * 0.5f, py = (aabb.MinY + aabb.MaxY) * 0.5f;
            bool took = SelectionGestureForTest(px, py);

            var w = _windows[0];
            const int steps = 30;
            double patchSum = 0, recordSum = 0, recordMax = 0, patchMax = 0;
            double tilesSum = 0; int tilesMax = 0;
            int startTiles = 0; double startPatchMs = 0;
            for (int k = 0; k < steps; k++)
            {
                UpdateSelectionGestureForTest(px + 6f * (k + 1), py + 3f * (k + 1));
                SettleFrames(10);
                recordMax = Math.Max(recordMax, w.LastRecordMs);
                if (k == 0)
                {
                    // 第 0 步 = **起手那一下**：把这一批从内容层摘出去，那些块要重画一次。
                    // 这一笔是一次性的，不能混进"拖动中每帧"的均值里（混进去会把它算成
                    // 每个拖动帧的代价，看着像没优化）。
                    startTiles = w.LastPatchCount;
                    startPatchMs = w.LastPatchMs;
                    continue;
                }
                patchSum += w.LastPatchMs; recordSum += w.LastRecordMs;
                patchMax = Math.Max(patchMax, w.LastPatchMs);
                tilesSum += w.LastPatchCount;
                tilesMax = Math.Max(tilesMax, w.LastPatchCount);
            }
            EndSelectionGestureForTest();
            SettleFrames(60);
            int steadySteps = steps - 1;

            Console.WriteLine($"  {total,7} | {pick,4} | {startTiles,7} 块/{startPatchMs,5:F1}ms | "
                            + $"{tilesSum / steadySteps,7:F1} 块 | {patchSum / steadySteps,9:F2} ms | "
                            + $"{recordSum / steadySteps,8:F2} ms | {recordMax,8:F2} ms"
                            + (took ? "" : "   （！手势没接住）"));
            Console.WriteLine($"          （拖动中内容层重画的块：均 {tilesSum / steadySteps:F1}、最多 {tilesMax}"
                            + $"，应当是 0；起手那一下 {startTiles} 块是一次性的）");
        }
        Console.WriteLine();
        Console.WriteLine("  判据：②的\"内容层重画块/帧\"应当是 0（模型不动，只画浮动层预览），");
        Console.WriteLine("        每帧耗时应当与\"选中的这几条\"成正比，而不是与\"走过的面积\"成正比。");
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 拖动预览的渲染核对：把选中对象用**实时变换**（SetTransformLive，
    /// 就是拖动中走的那条路径）挪走并挂着不动，由外部截图。
    ///
    /// 验的是"内容层会不会跟着修补"。之前漏了 bump 版本号，表现就是
    /// 蓝框跟着走、墨迹停在原地——截图上一眼能看出来：修好的话墨迹在框里，
    /// 没修的话墨迹还留在原来的地方，跟框分了家。
    /// </summary>


    private void LassoTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 套索自检（80% 判据 / 贴边无限延伸 / 加选减选）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        static List<Vector2> Box(float x0, float y0, float x1, float y1) => new()
        {
            new Vector2(x0, y0), new Vector2(x1, y0),
            new Vector2(x1, y1), new Vector2(x0, y1),
        };

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        var vp = ViewportCanvas;
        float snap = LassoEdgeSnapLogical * DpiScale;
        var realOut = Console.Out;                    // 掐掉 ApplyLasso 与手势里的控制台输出
        void Quiet(Action a) { Console.SetOut(TextWriter.Null); try { a(); } finally { Console.SetOut(realOut); } }

        // --- 0. 切换方式 --------------------------------------------------------
        Check("默认是矩形框", SelMode == SelectMode.Rect, SelMode.ToString());
        SelMode = SelectMode.Rect;
        ToggleSelectModeForTest();
        bool toLasso = SelMode == SelectMode.Lasso;
        ToggleSelectModeForTest();
        Check("Ctrl+Alt+9 在两种方式之间切", toLasso && SelMode == SelectMode.Rect,
              $"矩形 → {(toLasso ? "套索" : "?")} → {SelMode}");

        // --- 1. 80% 边界（79% 不选 / 81% 选）------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var boxPath = Box(cx - 150, cy - 150, cx + 150, cy + 150);

        Stroke MakeRow(int insideCount)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 3f };
            for (int i = 0; i < 100; i++)
            {
                if (i < insideCount)
                    s.AddPoint(cx - 100 + (i % 10) * 22, cy - 100 + (i / 10) * 25, 1f, i);   // 圈里
                else
                    s.AddPoint(cx + 400 + (i % 20) * 5, cy - 100 + (i / 20) * 25, 1f, i);    // 圈外
            }
            return s;
        }

        // 两条**分开测**：放一个文档里的话，81% 那条本来就会被选中，
        // "79% 没被选中"这件事就看不出来了（第一版就是这么写错的）。
        var s79 = MakeRow(79);
        Doc.AddStroke(s79);
        int n79 = Doc.ApplyLasso(boxPath, vp, snap);
        Check("79% 在圈里 → 不选", n79 == 0 && Doc.Selected.Count == 0,
              $"判中 {n79} 条（80% 是门槛，79/100 必须落空）");

        Doc.Clear();
        Doc.ClearHistory();
        var s81 = MakeRow(81);
        Doc.AddStroke(s81);
        int n81 = Doc.ApplyLasso(boxPath, vp, snap);
        Check("81% 在圈里 → 选中",
              n81 == 1 && Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], s81),
              $"判中 {n81} 条（81/100 ≥ 80%）");

        // --- 2. 贴边＝无限延伸 --------------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var longLine = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        for (int i = 0; i < 120; i++) longLine.AddPoint(vp.MinX - 800 + i * 10, cy, 1f, i);
        Doc.AddStroke(longLine);
        int total = longLine.Points.Count;                 // 120
        int visibleInside = 110;                           // x < vp.MinX + 300 的那些

        var awayFromEdge = Box(vp.MinX + 50, cy - 100, vp.MinX + 300, cy + 100);
        int nAway = Doc.ApplyLasso(awayFromEdge, vp, snap);
        Check("圈不贴边 → 屏幕外那半截不算（不选）", nAway == 0 && Doc.Selected.Count == 0,
              $"判中 {nAway} 条（圈里只有约 25/{total} 个点，{(25 * 100 / total)}%）");

        var touchEdge = Box(vp.MinX, cy - 100, vp.MinX + 300, cy + 100);
        int nEdge = Doc.ApplyLasso(touchEdge, vp, snap);
        Check("圈贴着屏幕左边 → 当作圈到无限远（选中）",
              nEdge == 1 && Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], longLine),
              $"判中 {nEdge} 条（延伸后圈里 {visibleInside}/{total} 个点 = "
              + $"{visibleInside * 100 / total}% ≥ 80%）");

        // --- 3. 图形按**轮廓**判，不按两个端点 ----------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var bigRect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.9f, 0.2f, 0.2f, 1f), Width = 4f,
        };
        bigRect.AddPoint(cx - 600, cy - 400, 1f, 0);
        bigRect.AddPoint(cx + 600, cy + 400, 1f, 0);        // 端点是那条斜对角线
        Doc.AddStroke(bigRect);

        // 只把右下角切掉一点：轮廓 5 个点里 4 个在圈里（= 80%），两个端点里只有 1 个
        var cutCorner = new List<Vector2>
        {
            new(cx - 700, cy - 500), new(cx + 700, cy - 500), new(cx + 700, cy + 300),
            new(cx + 560, cy + 420), new(cx - 700, cy + 420),
        };
        int nRect = Doc.ApplyLasso(cutCorner, vp, snap);
        Check("矩形：4/5 个轮廓点在圈里 → 选中", nRect == 1,
              $"判中 {nRect} 条（轮廓 5 点里 4 点在内 = 80%；按两个端点算只有 50%，会漏选）");

        var noBottom = Box(cx - 700, cy - 500, cx + 700, cy + 300);
        int nRect2 = Doc.ApplyLasso(noBottom, vp, snap);
        Check("矩形：只有 3/5 个轮廓点在圈里 → 不选", nRect2 == 0,
              $"判中 {nRect2} 条（60% < 80%）");

        // --- 4. 图像按四个角判 --------------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var pic = Doc.AddImage(MakeTestImage(300, 200), cx - 500, cy - 300, 1f);
        int nPicAll = Doc.ApplyLasso(Box(cx - 600, cy - 400, cx - 100, cy), vp, snap);
        Check("图像：四个角都在圈里 → 选中", nPicAll == 1,
              $"判中 {nPicAll} 条（角点 4/4 = 100%）");

        int nPic3 = Doc.ApplyLasso(Box(cx - 600, cy - 400, cx - 200, cy), vp, snap);
        Check("图像：只圈住三个角 → 不选", nPic3 == 0,
              $"判中 {nPic3} 条（3/4 = 75% < 80%）");

        // --- 5. 被擦掉的那一段不算墨 --------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var erasedMost = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
        for (int i = 0; i < 20; i++) erasedMost.AddPoint(cx, cy - 190 + i * 20, 1f, i);
        erasedMost.AddErased(0f, 16f);                      // 上面 17 个点那一段被擦掉
        Doc.AddStroke(erasedMost);
        int remaining = 0;
        for (int i = 0; i < erasedMost.Points.Count; i++)
            if (!erasedMost.IsParamErased(i)) remaining++;
        int nErased = Doc.ApplyLasso(Box(cx - 100, cy - 200, cx + 100, cy + 100), vp, snap);
        Check("圈住被擦掉的那半截 → 不选", nErased == 0 && Doc.Selected.Count == 0,
              $"判中 {nErased} 条（圈里还剩 {remaining} 个没被擦的点 = "
              + $"{remaining * 100 / erasedMost.Points.Count}%；把擦掉的也算上就是 85%，会误选）");

        // --- 6. Shift 加选 / Alt 减选 -------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var left = MakeRow(100);                            // 全在圈里
        var right = Doc.AddImage(MakeTestImage(80, 80), cx + 400, cy + 400, 1f);
        Doc.AddStroke(left);
        Doc.SelectOnly(new[] { right });
        int nAdd = Doc.ApplyLasso(boxPath, vp, snap, additive: true);
        bool added = Doc.Selected.Count == 2 && Doc.Selected.Contains(left) && Doc.Selected.Contains(right);
        Check("Shift 加选：原有的不丢", nAdd == 1 && added,
              $"判中 {nAdd} 条，选区 {Doc.Selected.Count} 条（图像 + 笔迹）");

        int nSub = Doc.ApplyLasso(boxPath, vp, snap, subtractive: true);
        bool removed = Doc.Selected.Count == 1 && Doc.Selected.Contains(right) && !Doc.Selected.Contains(left);
        Check("Alt 减选：只把它移出去", nSub == 1 && removed,
              $"判中 {nSub} 条，选区剩 {Doc.Selected.Count} 条（图像还在）");

        // --- 7. 引擎那条路（按下 → 拖 → 松手）-----------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var target = MakeRow(100);
        Doc.AddStroke(target);
        Doc.Selected.Clear();
        SelMode = SelectMode.Lasso;
        Quiet(() => MarqueeDragForTest(boxPath));
        Check("引擎：走一次完整套索手势 → 选中", Doc.Selected.Count == 1 && Doc.Selected.Contains(target),
              $"选中 {Doc.Selected.Count} 条，路径已清空={LassoPath.Count == 0}");

        Quiet(() => MarqueeDragForTest(new[] { new Vector2(cx, cy), new Vector2(cx + 1, cy + 1) }));
        Check("引擎：路径太短＝单击空白 → 取消选中", Doc.Selected.Count == 0,
              $"选中 {Doc.Selected.Count} 条（和框选那条规则一致）");

        // --- 8. 拽着不放时屏幕上真有那根线（差分判据，抓屏拍不到就跳过）----------
        // 预览画的是自由折线，和矩形框不是同一段代码；不验的话"套索拖起来什么都看不见"
        // 这种问题要等人肉测才发现。
        Doc.Clear();
        Doc.ClearHistory();
        SelMode = SelectMode.Lasso;
        MarqueeActive = false;
        LassoPath.Clear();
        Doc.InvalidateAll();
        SettleFrames(400);

        int bandX = (int)(cx - 220), bandY = (int)(cy - 190);
        int bandYpx = (int)(cy - 190 + ViewOffsetY);
        const int bandW = 440, bandH = 70;
        var sight = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 10f };
        for (int i = 0; i <= 20; i++) sight.AddPoint(cx - 200 + i * 20, cy - 150, 1f, i);
        Doc.AddStroke(sight);
        Doc.InvalidateAll();
        SettleFrames(400);
        int canSee = ScreenProbe.CountMagenta(bandX, bandYpx, bandW, bandH);
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(400);

        if (canSee < 200)
        {
            Console.WriteLine($"  环境：抓屏看不到我们的层（拍到的是桌面/别的窗口，{canSee} 像素）"
                            + " → SKIP: 预览那一条跳过");
        }
        else
        {
            var before = ScreenProbe.CaptureRegion(bandX, bandYpx, bandW, bandH);
            MarqueeActive = true;
            LassoPath.AddRange(boxPath);
            LassoLive = boxPath[boxPath.Count - 1];      // 线头画在指针位置（这里就是最后一个点）
            MqMinX = boxPath[0].X; MqMinY = boxPath[0].Y;
            MqMaxX = boxPath[2].X; MqMaxY = boxPath[2].Y;
            Doc.InvalidateAll();
            SettleFrames(400);
            var after = ScreenProbe.CaptureRegion(bandX, bandYpx, bandW, bandH);

            int changed = 0;
            for (int i = 0; i + 3 < Math.Min(before.Length, after.Length); i += 4)
            {
                int d = Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1])
                      + Math.Abs(before[i + 2] - after[i + 2]);
                if (d > 40) changed++;
            }
            // 圈的上边正好横穿这条带子：300 逻辑像素长 × 1.6 宽（DPI 2 → 约 3 像素）
            Check("拽着不放时屏幕上真画出了那条线", changed > 200,
                  $"这条带子里变了 {changed} 个像素（上边线横穿过去，应该上千）");

            MarqueeActive = false;
            LassoPath.Clear();
            Doc.InvalidateAll();
            SettleFrames(200);
        }

        // --- 9. 切回矩形：同一个手势变成"碰到就选" -----------------------------
        Doc.Clear();
        Doc.ClearHistory();
        var rectTarget = MakeRow(100);
        Doc.AddStroke(rectTarget);
        Doc.Selected.Clear();
        SelMode = SelectMode.Rect;
        // 矩形模式喂**两个点**（锚点 → 对角）就够：矩形是按"锚点 ↔ 当前点"算的，
        // 喂一圈闭合路径的话锚点和终点会重合，等于拉出一个零面积的框。
        Quiet(() => MarqueeDragForTest(new[]
        {
            new Vector2(cx - 150f, cy - 150f),
            new Vector2(cx + 150f, cy + 150f),
        }));
        Check("切回矩形：同一个手势变成'碰到就选'",
              SelMode == SelectMode.Rect && Doc.Selected.Count == 1 && Doc.Selected.Contains(rectTarget),
              $"矩形模式下选中 {Doc.Selected.Count} 条（同一个矩形范围，换了一套判据）");

        Doc.Clear();
        Console.WriteLine($"  合计：通过 {pass}，失败 {fail}");
        Console.WriteLine(fail == 0 ? "PASS" : "FAIL");
        _quit = true;
    }

    /// <summary>
    /// 图形与**顶点拖动**自检。
    ///
    /// 关键在最后两条：顶点手柄和选中框的角手柄在屏幕上重合，所以"到底拖到了
    /// 哪一个"必须验；以及**旋转过的图形，顶点必须拖动在它自己的坐标系里**
    /// （否则一转，矩形就不再是矩形了——这类 bug 肉眼一看才发现，且很晚）。
    /// </summary>
    /// <summary>
    /// 合成输入探针：往屏幕中间划一小段，看窗口到底收不收到消息。
    /// 返回 true = 输入通。**它只回答环境问题**，不判功能对错。
    /// </summary>
    /// <summary>
    /// 合成输入不可用时，把依赖真机拖动的自检标成"跳过"而不是"失败"。
    ///
    /// 为什么要这一层：覆盖层是置顶的，但这不代表消息一定送得到——
    /// 别的程序占着鼠标捕获时，我们的窗口一条消息都收不到（实测遇到过）。
    /// 那种情况下判 FAIL，会让人去查完全无关的功能代码。
    /// </summary>


    /// <summary>
    /// 编辑命令自检：复制 / 删除 / 翻转 / 旋转，外加操作条的命中判定。
    ///
    /// 这四个动作是操作条按钮直接调的，但按钮没法自动点（要合成鼠标事件），
    /// 所以这里直接验它们背后的命令，把**撤销语义**一并验掉——
    /// 一步操作必须正好对应一条撤销记录，多一条少一条都是 bug。
    /// </summary>
    private void EditTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 编辑命令自检（复制 / 删除 / 翻转 / 旋转）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-24}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Stroke Make(float x)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand, Width = 4f,
                Color = new Color4(0.9f, 0.2f, 0.2f, 1f),
            };
            for (int i = 0; i < 20; i++) s.AddPoint(x + i * 3f, 500 + i * 2f, 0.5f, i);
            return s;
        }
        float Width(Stroke s) => s.WorldBounds.MaxX - s.WorldBounds.MinX;
        float Height(Stroke s) => s.WorldBounds.MaxY - s.WorldBounds.MinY;

        Doc.Clear();
        Doc.ClearHistory();
        var a = Make(300); Doc.AddStroke(a);
        var b = Make(600); Doc.AddStroke(b);

        // ---- 复制 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a); Doc.Selected.Add(b);
        int n0 = Doc.Strokes.Count, d0 = Doc.UndoDepth;
        Doc.DuplicateSelected(30, 30);
        Check("复制新增对象", Doc.Strokes.Count == n0 + 2, $"{n0} -> {Doc.Strokes.Count}");
        Check("复制只记一步", Doc.UndoDepth == d0 + 1, $"{d0} -> {Doc.UndoDepth}");
        Check("选中切到副本", Doc.Selected.Count == 2 && !ReferenceEquals(Doc.Selected[0], a), "");
        Check("副本是新身份", Doc.Selected[0].Id != a.Id, $"{a.Id} -> {Doc.Selected[0].Id}");
        Doc.Undo();
        Check("撤销复制", Doc.Strokes.Count == n0, $"{Doc.Strokes.Count}");

        // ---- 翻转 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a);
        var wb = a.WorldBounds;
        Doc.ApplyTransform(SelectionHandles.MirrorMatrix(wb, horizontal: true));
        Check("左右翻转：包围盒不变",
              Math.Abs(a.WorldBounds.MinX - wb.MinX) < 0.5f && Math.Abs(Width(a) - (wb.MaxX - wb.MinX)) < 0.5f, "");
        Check("翻转后行列式为负", a.Transform.M11 * a.Transform.M22 - a.Transform.M12 * a.Transform.M21 < 0f, "");
        Doc.Undo();
        Check("撤销翻转回原位", Math.Abs(a.WorldBounds.MinX - wb.MinX) < 0.05f,
              $"MinX {a.WorldBounds.MinX:F2} vs {wb.MinX:F2}");

        // ---- 旋转 90°：宽高应该互换 ----
        var wb2 = a.WorldBounds;
        var c = new Vector2((wb2.MinX + wb2.MaxX) * 0.5f, (wb2.MinY + wb2.MaxY) * 0.5f);
        Doc.ApplyTransform(Matrix3x2.CreateRotation(MathF.PI / 2f, c));
        Check("旋转 90°：宽高互换",
              Math.Abs(Width(a) - (wb2.MaxY - wb2.MinY)) < 0.5f
              && Math.Abs(Height(a) - (wb2.MaxX - wb2.MinX)) < 0.5f,
              $"{Width(a):F1}×{Height(a):F1}");
        Doc.Undo();

        // ---- 删除 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a);
        int n1 = Doc.Strokes.Count;
        Doc.DeleteSelected();
        Check("删除选中", Doc.Strokes.Count == n1 - 1 && Doc.Selected.Count == 0, $"{n1} -> {Doc.Strokes.Count}");
        Doc.Undo();
        Check("撤销删除", Doc.Strokes.Count == n1, $"{Doc.Strokes.Count}");

        // ---- 操作条命中 ----
        Doc.Selected.Clear(); Doc.Selected.Add(a); Doc.Selected.Add(b);
        var sb = EditRegion.Of(Doc.Selected);
        float dpi = DpiScale;
        var bar = SelectionHandles.BarRect(sb, dpi, ViewportCanvas);
        Check("操作条在选中框下方", bar.MinY > sb.MaxY, $"间距 {bar.MinY - sb.MaxY:F0}px");

        bool allHit = true; string bad = "";
        for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
        {
            var r = SelectionHandles.BarButtonRect(i, sb, dpi, ViewportCanvas);
            int got = SelectionHandles.BarButtonAt((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f,
                                                   sb, dpi, ViewportCanvas);
            if (got != i) { allHit = false; bad = $"第{i}个按钮命中到 {got}"; }
        }
        Check("每个按钮都能点中", allHit, bad);
        Check("框外不误判",
              SelectionHandles.BarButtonAt(bar.MinX - 30, bar.MinY + 5, sb, dpi, ViewportCanvas) == -1, "");

        // ---- 导出：**点一下直接走导出，不再开自己的格式面板** ----
        //
        // 曾经这里点"导出"会先开一层我们自己的两格面板（PNG 透明底 / JPG 白底），
        // 那是为了绕开"系统对话框的下拉点不开"。真因（覆盖层每秒抢一次置顶）修掉之后，
        // 用户 2026-09-17 说"那下面这两个图标就没有用了吧，用户都可以自己保存图片了"——
        // 删掉。现在格式在**系统对话框**的类型栏里选（见 ExportFormats，四种都写明了优势）。
        //
        // 自检里对话框是关着的（`ExportDialogEnabled = false`），所以这里只能验
        // "那一下真的走到了导出这条路"（计数器）＋"没有把任何面板打开"。
        {
            int before = ExportAttempts;
            RunBarActionForTest((int)SelBarButton.Export);
            Check("点「导出」直接走导出（不再开格式面板）",
                  ExportAttempts == before + 1 && SelPanelOpen == SelPanel.None,
                  $"试了 {before} → {ExportAttempts} 次，面板 = {SelPanelOpen}");

            // 四种格式的"扩展名 → 编码器 + 底色"这套规则是纯逻辑，这里一并钉住：
            // 尤其是**有损/无透明通道的格式绝不给透明通道**（否则透明处会变黑块）。
            Check("格式表：四条，扩展名各就各位",
                  ExportFormats.Count == 4
                  && ExportFormats.ExtensionFor(1) == ".png"
                  && ExportFormats.ExtensionFor(2) == ".jpg"
                  && ExportFormats.ExtensionFor(3) == ".png"
                  && ExportFormats.ExtensionFor(4) == ".bmp",
                  string.Join(" / ", Enumerable.Range(1, ExportFormats.Count)
                                                .Select(i => ExportFormats.ExtensionFor(i))));
            Check("格式表：四种类型的名字里都写明了优势（不是光一个格式名）",
                  ExportFormats.Filter.Contains("贴课件首选")
                  && ExportFormats.Filter.Contains("好发微信邮件")
                  && ExportFormats.Filter.Contains("深色模板上不露底")
                  && ExportFormats.Filter.Contains("老软件也能打开"),
                  "四条都带说明");

            // **记住上次选的那一条**（常用 JPG 的老师不用每次去下拉里找）。
            // 存的是"类型栏的第几条"，读的时候越界/坏值一律回到第 1 条。
            var saved = GetUiPref("exportFormat");
            SetUiPref("exportFormat", null);
            int def0 = ExportDefaultFilterIndex();
            SetUiPref("exportFormat", "3");
            int def3 = ExportDefaultFilterIndex();
            SetUiPref("exportFormat", "99");
            int defBad = ExportDefaultFilterIndex();
            SetUiPref("exportFormat", saved);
            Check("记住上次的格式：没存过→第 1 条、存过→照存、坏值→回到第 1 条",
                  def0 == 1 && def3 == 3 && defBad == 1,
                  $"默认 {def0}，存 3 → {def3}，存 99 → {defBad}");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 编辑命令与操作条命中都正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 把选中框和手柄摆出来给人看。跟 --beautifyshowcase 一个路子：
    /// 画好挂着不动，由外部截图，用来肉眼核对观感（不是自动判定）。
    /// </summary>


    /// <summary>
    /// 选中手柄自检：位置、命中、以及每个手柄拖出来的是什么矩阵。
    ///
    /// 为什么要专门测：变换矩阵错了是那种"看着能动、但缩放之后再撤销
    /// 回不到原样"的问题，肉眼基本发现不了，而且会累积误差。
    /// </summary>
    private void HandleTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 选中手柄自检（位置 / 命中 / 变换矩阵）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float dpi = DpiScale;
        var b = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        float cx = (b.MinX + b.MaxX) * 0.5f;

        // ---- 位置 ----
        var tl = SelectionHandles.Position(SelHandle.TopLeft, b, dpi);
        var top = SelectionHandles.Position(SelHandle.Top, b, dpi);
        var rot = SelectionHandles.Position(SelHandle.Rotate, b, dpi);
        Check("左上角位置", Math.Abs(tl.X - b.MinX) < 0.01f && Math.Abs(tl.Y - b.MinY) < 0.01f,
              $"({tl.X:F0},{tl.Y:F0})");
        Check("上边中点位置", Math.Abs(top.X - cx) < 0.01f && Math.Abs(top.Y - b.MinY) < 0.01f, "");
        Check("旋转手柄在上方外侧",
              Math.Abs(rot.X - cx) < 0.01f && rot.Y < b.MinY - 20f,
              $"离上边 {b.MinY - rot.Y:F0}px");

        // ---- 命中 ----
        Check("点上四角命中", SelectionHandles.HitTest(tl.X, tl.Y, b, dpi) == SelHandle.TopLeft, "");
        Check("旋转手柄命中", SelectionHandles.HitTest(rot.X, rot.Y, b, dpi) == SelHandle.Rotate, "");
        float far = SelectionHandles.HitRadiusLogical * dpi * 1.6f;
        Check("偏离太远不命中",
              SelectionHandles.HitTest(tl.X - far, tl.Y, b, dpi) == SelHandle.None, $"偏 {far:F0}px");
        Check("极简模式不认边中点",
              SelectionHandles.HitTest(top.X, top.Y, b, dpi, includeEdgeHandles: false) == SelHandle.None, "");

        // ---- 小对象：最小操作框（2026-10-05 用户："太小还用这么大的点不合适"）----
        //    手柄摆到撑开的最小操作框上；真实包围盒只有 10×8 画布单位。
        var tiny = new RectF { MinX = 100, MinY = 100, MaxX = 110, MaxY = 108 };
        var ttl = SelectionHandles.Position(SelHandle.TopLeft, tiny, dpi);
        var tbr = SelectionHandles.Position(SelHandle.BottomRight, tiny, dpi);
        float tinySide = SelectionHandles.MinUiFrameLogical * dpi;
        Check("小对象：手柄撑到最小操作框",
              MathF.Abs((tbr.X - ttl.X) - tinySide) < 0.01f
              && MathF.Abs((tbr.Y - ttl.Y) - tinySide) < 0.01f,
              $"操作框 {tbr.X - ttl.X:F0}×{tbr.Y - ttl.Y:F0}（最小 {tinySide:F0}），真实框 10×8");
        Check("小对象：真实框外的空白也算拖动区",
              SelectionHandles.InsideUiFrame(
                  new SelectionFrame { Local = tiny, ToCanvas = Matrix3x2.Identity },
                  new Vector2(120f, 104f), dpi),
              "点 (120,104)：真实框右边界 110 之外、操作框之内");
        var mTiny = SelectionHandles.DragMatrix(SelHandle.TopLeft, tiny, ttl, ttl, dpi, false, false);
        Check("小对象：按下不动 = 缩放 1（不会跳）",
              MathF.Abs(mTiny.M11 - 1f) < 1e-3f && MathF.Abs(mTiny.M22 - 1f) < 1e-3f,
              $"sx={mTiny.M11:F3} sy={mTiny.M22:F3}");
        // 缩小：真实框的**对面角**必须钉住 —— 拿操作框的角当缩放中心的话，
        // 内容会绕着框外一个点漂（用户 2026-10-05："缩到最小以后鼠标乱动，
        // 它跟着乱移动"）。这条断言在旧实现下会红。
        var shrinkTo = new Vector2(ttl.X + 6f, ttl.Y + 6f);        // 往里拖一点
        var mShrink = SelectionHandles.DragMatrix(SelHandle.TopLeft, tiny, ttl, shrinkTo, dpi, false, false);
        var realBr = new Vector2(tiny.MaxX, tiny.MaxY);
        var brAfter = Vector2.Transform(realBr, mShrink);
        Check("小对象缩小：真实框的对面角钉住（不乱漂）",
              MathF.Abs(brAfter.X - realBr.X) < 0.05f && MathF.Abs(brAfter.Y - realBr.Y) < 0.05f,
              $"对面角 ({brAfter.X:F1},{brAfter.Y:F1})，原位 ({realBr.X:F1},{realBr.Y:F1})");
        Check("小对象缩小：内容真的变小了（不是纹丝不动）", mShrink.M11 < 0.95f && mShrink.M11 > 0f,
              $"sx={mShrink.M11:F3}");

        // ---- 四角拖动：锚点不动，被拖的角跟手 ----
        var br = SelectionHandles.Position(SelHandle.BottomRight, b, dpi);
        // 目标点取在"锚点 → 被拖的角"的延长线上：四角现在是**等比**缩放，
        // 只有沿对角线拖，被拖的角才会精确落在目标点上。
        // （不在对角线上时等比缩放也能用，只是角落不到指针那儿——这是等比的
        //   固有性质，不是 bug。）
        var target = new Vector2(b.MinX * 2f - b.MaxX, b.MinY * 2f - b.MaxY);
        var m = SelectionHandles.DragMatrix(SelHandle.TopLeft, b, tl, target, dpi, false, false);
        var anchorAfter = Vector2.Transform(br, m);
        Check("锚点（对角）不动",
              Math.Abs(anchorAfter.X - br.X) < 0.05f && Math.Abs(anchorAfter.Y - br.Y) < 0.05f,
              $"({anchorAfter.X:F1},{anchorAfter.Y:F1})");
        var cornerAfter = Vector2.Transform(tl, m);
        Check("被拖的角跟到目标点",
              Math.Abs(cornerAfter.X - target.X) < 0.05f && Math.Abs(cornerAfter.Y - target.Y) < 0.05f,
              $"({cornerAfter.X:F1},{cornerAfter.Y:F1}) 目标 ({target.X:F1},{target.Y:F1})");
        Check("四角是等比", MathF.Abs(m.M11 - m.M22) < 1e-4f, $"sx={m.M11:F3} sy={m.M22:F3}");

        // ---- 边中点：只动一个轴（这就是"左右拉伸 / 上下拉伸"）----
        var right = SelectionHandles.Position(SelHandle.Right, b, dpi);
        var mEdge = SelectionHandles.DragMatrix(SelHandle.Right, b, right,
                                                new Vector2(right.X + 200, right.Y + 999), dpi, false, false);
        Check("右边中点只拉伸 X", MathF.Abs(mEdge.M22 - 1f) < 1e-4f && mEdge.M11 > 1.49f,
              $"sx={mEdge.M11:F2} sy={mEdge.M22:F2}（Y 的拖动被忽略）");

        // ---- 拖过头：夹住，不翻转 ----
        var wayPast = new Vector2(br.X + 600, br.Y + 600);
        var mOver = SelectionHandles.DragMatrix(SelHandle.TopLeft, b, tl, wayPast, dpi, false, false);
        Check("拖过头不翻转（夹住）",
              mOver.M11 > 0f && mOver.M22 > 0f && MathF.Abs(mOver.M11 - SelectionHandles.MinScale) < 1e-3f,
              $"sx={mOver.M11:F3} sy={mOver.M22:F3}，下限 {SelectionHandles.MinScale}");

        // ---- Shift 等比 ----
        var mUni = SelectionHandles.DragMatrix(SelHandle.TopLeft, b, tl,
                                               new Vector2(b.MinX - 300, b.MinY - 80), dpi, true, false);
        Check("Shift 等比", MathF.Abs(mUni.M11 - mUni.M22) < 1e-4f, $"sx={mUni.M11:F3} sy={mUni.M22:F3}");

        // ---- 旋转：绕选区中心转，长度不变 ----
        var center = new Vector2(cx, (b.MinY + b.MaxY) * 0.5f);
        var mRot = SelectionHandles.DragMatrix(SelHandle.Rotate, b, top,
                                               new Vector2(center.X + 100, center.Y), dpi, false, false);
        var rotated = Vector2.Transform(tl, mRot);
        float r0 = Vector2.Distance(tl, center);
        float r1 = Vector2.Distance(rotated, center);
        Check("旋转保持半径", Math.Abs(r0 - r1) < 0.05f, $"{r0:F1} -> {r1:F1}");

        // ---- 旋转吸附 ----
        var mSnap = SelectionHandles.DragMatrix(SelHandle.Rotate, b, top,
                                                new Vector2(center.X + 100, center.Y + 7), dpi, false, true);
        var v0 = Vector2.Normalize(top - center);
        var v1 = Vector2.Normalize(Vector2.Transform(top, mSnap) - center);
        float deg = MathF.Acos(Math.Clamp(Vector2.Dot(v0, v1), -1f, 1f)) * 180f / MathF.PI;
        Check("Shift 旋转吸附到 15°", Math.Abs(deg % 15f) < 0.5f || Math.Abs(deg % 15f - 15f) < 0.5f,
              $"转了 {deg:F1}°");

        // ---- 镜像：只能从矩阵入口来 ----
        var mMir = SelectionHandles.MirrorMatrix(b, horizontal: true);
        var mirroredLeft = Vector2.Transform(tl, mMir);
        Check("左右镜像对称", Math.Abs(mirroredLeft.X - b.MaxX) < 0.01f
                           && Math.Abs(mirroredLeft.Y - b.MinY) < 0.01f,
              $"左上角 -> ({mirroredLeft.X:F1},{mirroredLeft.Y:F1})");
        Check("镜像矩阵行列式为负", mMir.M11 * mMir.M22 - mMir.M12 * mMir.M21 < 0f, "");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 手柄位置、命中与变换矩阵都正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }


    /// <summary>
    /// 旋转度数 + 吸附自检。
    ///
    /// 分两段：
    ///   A. **数学层**：读数、三种吸附模式（软吸附 90° / Shift 硬网格 15° / Alt 自由）、
    ///      边界值，以及"读数与矩阵自洽"（用独立的 atan2 再量一遍矩阵转过的角度）。
    ///   B. **真机层**：合成鼠标按在旋转手柄上拖，然后**数屏幕像素**确认度数标签
    ///      真的画出来了、"吸住"时真的变色、松手后真的消失。
    ///
    /// 为什么非要 B：标签是画在浮动层上的，进没进每帧脏区、双缓冲会不会把它留在
    /// 屏幕上，只有看像素才知道。选中框那一套 UI 就踩过"漏算脏区 → 残影"的坑。
    /// </summary>
    private void RotateTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 旋转度数 / 吸附自检 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-28}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        // ================= A. 数学层 =================
        var b = new RectF { MinX = 600, MinY = 500, MaxX = 1000, MaxY = 800 };
        var center = new Vector2((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f);
        const float radius = 260f;
        // 起点固定在正上方（屏幕上的 -90°），目标点 = 起点绕中心转 deg 度
        Vector2 At(float deg)
        {
            float rad = (-90f + deg) * MathF.PI / 180f;
            return new Vector2(center.X + radius * MathF.Cos(rad), center.Y + radius * MathF.Sin(rad));
        }
        var from = At(0f);

        (float deg, bool snapped) Probe(float deg, bool shift = false, bool alt = false)
            => (SelectionHandles.RotationDeltaDegrees(center, from, At(deg), shift, alt, out bool s), s);

        void Case(string name, float target, float want, bool wantSnap,
                  bool shift = false, bool alt = false)
        {
            var (got, snapped) = Probe(target, shift, alt);
            bool ok = Math.Abs(got - want) < 0.05f && snapped == wantSnap;
            Check(name, ok, $"转 {target:F1}° → {got:F1}°{(snapped ? "（吸附）" : "（自由）")}"
                          + $"，期望 {want:F1}°{(wantSnap ? "（吸附）" : "（自由）")}");
        }

        // 注意 `target` 是**指针在屏幕上顺时针转的角度**（见 At），`want` 是标签上的数。
        // 用户 2026-09-15 定：**逆时针为正、顺时针为负**——所以顺时针 88° 读数是 -90°。
        Case("正对网格：吸附", 0f, 0f, true);
        Case("容差内：吸到 0°", 2f, 0f, true);
        Case("容差外：保持自由（顺时针 → 负）", 5f, -5f, false);
        Case("普通角度：保持自由", 43f, -43f, false);
        Case("容差内：吸到 90°", 88f, -90f, true);
        Case("容差内：吸到 -90°", -89f, 90f, true);
        Case("容差外：保持自由（90 附近）", 95f, -95f, false);
        Case("半个整角：自由", 45f, -45f, false);
        Case("整角 180°：吸附", 180f, -180f, true);
        // 半圈这个点天生有歧义：屏幕 -180° 的最短增量既可以是 +180 也可以是 -180，
        // 实现取"归一化后再取负" → -180。真正的转圈由拖动中的**逐帧累积**决定（见 C 段）。
        Case("半圈的临界点：取 -180°", -180f, -180f, true);
        Case("Shift：硬网格 15°", 20f, -15f, true, shift: true);
        Case("Shift：吸到 0°", 7f, 0f, true, shift: true);
        Case("Alt：完全自由", 2f, -2f, false, alt: true);
        Case("Shift+Alt：Shift 优先", 20f, -15f, true, shift: true, alt: true);

        // **不设上限**（用户 2026-09-15 定）：吸附规则原样，但角度不绕回。
        // 转两圈多一点点 = 738°，就该原样写 738°，而不是 18°。
        Check("吸附：738° 不绕回", Math.Abs(SelectionHandles.SnapRotationDegrees(738f, false, false, out _) - 738f) < 0.01f,
              $"738° → {SelectionHandles.SnapRotationDegrees(738f, false, false, out _):F0}°");
        Check("吸附：722° 吸到 720°（整圈的整数倍照样认）",
              Math.Abs(SelectionHandles.SnapRotationDegrees(722f, false, false, out bool s722) - 720f) < 0.01f && s722,
              $"722° → {SelectionHandles.SnapRotationDegrees(722f, false, false, out _):F0}°");
        Check("吸附：-725° 保持自由（离 -720° 差 5°）",
              Math.Abs(SelectionHandles.SnapRotationDegrees(-725f, false, false, out bool s725) + 725f) < 0.01f && !s725,
              $"-725° → {SelectionHandles.SnapRotationDegrees(-725f, false, false, out _):F0}°");

        // 读数与矩阵必须自洽：矩阵转过的角度（独立用 atan2 量）要等于读数
        foreach (float target in new[] { 0f, 43f, 88f, -137f, 179f })
        {
            var (deg, _) = Probe(target);
            var m = SelectionHandles.DragMatrix(SelHandle.Rotate, b, from, At(target), 1f, false, false);
            var moved = Vector2.Transform(from, m);
            float measured = MathF.Atan2(moved.Y - center.Y, moved.X - center.X)
                           * 180f / MathF.PI + 90f;         // 起点在 -90°，所以加回来
            // 量出来的是**屏幕坐标**（顺时针为正）；读数那套是逆时针为正，取负号对齐
            measured = -SelectionHandles.NormalizeDegrees(measured);
            var pivotAfter = Vector2.Transform(center, m);
            Check($"拖动 {target,5:F0}°：读数与矩阵一致",
                  Math.Abs(measured - deg) < 0.2f
                  && Math.Abs(pivotAfter.X - center.X) < 0.05f
                  && Math.Abs(pivotAfter.Y - center.Y) < 0.05f,
                  $"读数 {deg:F1}°，矩阵量出 {measured:F1}°，轴心偏移 "
                  + $"{Vector2.Distance(pivotAfter, center):F3}px");
        }

        Check("标签文案不留 -0°", SelectionHandles.FormatDegrees(-0.4f) == "0°",
              $"{-0.4f:F1}° → {SelectionHandles.FormatDegrees(-0.4f)}");
        Check("标签文案取整", SelectionHandles.FormatDegrees(89.6f) == "90°",
              $"89.6° → {SelectionHandles.FormatDegrees(89.6f)}");
        Check("标签文案带符号", SelectionHandles.FormatDegrees(-45f) == "-45°",
              $"-45° → {SelectionHandles.FormatDegrees(-45f)}");
        Check("标签文案不绕回（转两圈就写 740°）", SelectionHandles.FormatDegrees(740f) == "740°",
              $"740° → {SelectionHandles.FormatDegrees(740f)}");
        Check("标签文案不绕回（倒着转就写 -1234°）", SelectionHandles.FormatDegrees(-1234f) == "-1234°",
              $"-1234° → {SelectionHandles.FormatDegrees(-1234f)}");

        // ================= B. 真机层 =================
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        // **白板打底**：这一节靠"数屏幕上的强调色像素"判断标签画没画、在哪儿，
        // 而批注模式下面板是全透明的——桌面上的东西会被算进来。2026-09-17 实测：
        // 桌面换了一批窗口之后，这三条一下全红（"标签位置是干净的"量到 3421 像素），
        // 而代码一个字没改。铺一层不透明白板，量到的就全是我们的墨。
        BoardOn = true;

        float sx = _virtualX + _virtualW * 0.5f, sy = _virtualY + _virtualH * 0.5f;
        var stroke = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 12f * DpiScale,
        };
        // 画个圈：选区足够大，手柄到轴心的半径才够长，角度才好量准
        for (int i = 0; i <= 48; i++)
        {
            float a = i / 48f * MathF.PI * 2f;
            stroke.AddPoint(sx + MathF.Cos(a) * 180f * DpiScale,
                            sy + MathF.Sin(a) * 180f * DpiScale, 0.9f, i * 8);
        }
        Doc.AddStroke(stroke);
        Doc.Selected.Clear();
        Doc.Selected.Add(stroke);
        Tool = Tool.Marquee;
        SettleFrames(400);

        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;
        var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, frame, dpi);
        var pivot = new Vector2((frame.CanvasAabb.MinX + frame.CanvasAabb.MaxX) * 0.5f,
                                (frame.CanvasAabb.MinY + frame.CanvasAabb.MaxY) * 0.5f);
        float arm = Vector2.Distance(grip, pivot);
        Vector2 GripAt(float deg)
        {
            float rad = (-90f + deg) * MathF.PI / 180f;
            return new Vector2(pivot.X + arm * MathF.Cos(rad), pivot.Y + arm * MathF.Sin(rad));
        }

        // 探针窗：**当前**旋转手柄上方那一块（标签就画在那儿），刻意不覆盖选中框本身。
        // 必须每一步都重算手柄位置——手柄是跟着框转的，拖动之后标签早就不在原来的地方了。
        int probeW = (int)(150f * dpi), probeH = (int)(120f * dpi);
        int AccentCount()
        {
            // 用**实时框**：拖动中模型到松手才动（方案 B），拿模型里的框去算手柄位置，
            // 探针窗会停在按下那一刻的老地方，而标签早跟着内容转走了。
            var f = LiveSelectionFrame;
            var h = SelectionHandles.CanvasPosition(SelHandle.Rotate, f, dpi);
            return ScreenProbe.CountNear((int)(h.X - probeW * 0.5f), (int)(h.Y - probeH),
                                         probeW, probeH, 0, 120, 212, 40);
        }

        int beforeDraw = AccentCount();
        Check("没拖之前，标签位置是干净的", beforeDraw < 800, $"{beforeDraw} 像素");

        // 按下旋转手柄，拖到 92°（容差内 → 应该吸到 90°）
        SendMouse((int)grip.X, (int)grip.Y, Native.MOUSEEVENTF_LEFTDOWN);
        var to92 = GripAt(92f);
        SendMouse((int)to92.X, (int)to92.Y, 0);
        SettleFrames(260);

        Check("拖动中标记为旋转", SelRotating, $"SelRotating={SelRotating}");
        // 指针**顺时针**拖了 92°（GripAt 的角度是屏幕坐标），标签该写 -90°——逆转为正、顺转为负
        Check("读数 ≈ -90°（顺时针为负）", Math.Abs(SelRotationDegrees + 90f) < 1f, $"{SelRotationDegrees:F1}°");
        Check("顺时针 92° 被吸到 -90°", SelRotationSnapped, $"snapped={SelRotationSnapped}");
        int snappedPixels = AccentCount();
        Check("吸住时标签上屏（强调色填充）", snappedPixels > 1500, $"{snappedPixels} 像素");

        // 再拖到 43°（容差外 → 不吸）
        var to43 = GripAt(43f);
        SendMouse((int)to43.X, (int)to43.Y, 0);
        SettleFrames(260);
        Check("顺时针 43° 保持自由（-43°）", !SelRotationSnapped && Math.Abs(SelRotationDegrees + 43f) < 1f,
              $"{SelRotationDegrees:F1}°，snapped={SelRotationSnapped}");
        int freePixels = AccentCount();
        Check("没吸住时标签是白底（强调色像素少）", freePixels < 800, $"{freePixels} 像素");

        // 松手：标签必须消失，且一次拖拽只留一条撤销记录
        SendMouse((int)to43.X, (int)to43.Y, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(320);
        Check("松手后不再标记旋转", !SelRotating, $"SelRotating={SelRotating}");
        int afterRelease = AccentCount();
        Check("松手后标签从屏幕上消失", afterRelease < 800, $"{afterRelease} 像素（拖动前 {beforeDraw}）");

        var rotated = Doc.Strokes[0].Transform;
        Check("对象真的被转了（不是只显示个标签）",
              Math.Abs(rotated.M11 - 1f) > 0.01f || Math.Abs(rotated.M12) > 0.01f,
              $"M11={rotated.M11:F3} M12={rotated.M12:F3}");

        Doc.Undo();
        SettleFrames(200);
        var back = Doc.Strokes.Count > 0 ? Doc.Strokes[0].Transform : Matrix3x2.Identity;
        Check("一次拖拽 = 一步撤销，撤销后回原位",
              Math.Abs(back.M11 - 1f) < 1e-4f && Math.Abs(back.M12) < 1e-4f
              && Math.Abs(back.M21) < 1e-4f && Math.Abs(back.M22 - 1f) < 1e-4f,
              $"撤销后 M11={back.M11:F4} M12={back.M12:F4}");

        // ================= C. 方向 + 不设上限（用户 2026-09-15 定的）=================
        //
        // 两条都要测：只有"逆时针为正"和"顺时针为负"**成对**出现，才证明符号是对的
        // （只测一条的话，符号写反了也看不出来）。
        // 再往同一方向一直转，看读数会不会在 ±180° 处**绕回**——绕回就说明还是在量
        // "起点到当前点的夹角"，而不是"这一拖一共转了多少"。
        {
            // 圆形选区：绕自己转不改变轴心和半径，角度才好量准
            Stroke NewRound()
            {
                Doc.Clear();
                Doc.ClearHistory();
                var s2 = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 12f * DpiScale,
                };
                for (int i = 0; i <= 48; i++)
                {
                    float a = i / 48f * MathF.PI * 2f;
                    s2.AddPoint(sx + MathF.Cos(a) * 180f * DpiScale,
                                sy + MathF.Sin(a) * 180f * DpiScale, 0.9f, i * 8);
                }
                Doc.AddStroke(s2);
                Doc.Selected.Clear();
                Doc.Selected.Add(s2);
                Tool = Tool.Marquee;
                SettleFrames(300);
                return s2;
            }

            // 从当前位置的旋转手柄开始，按一串"屏幕角度增量"拖过去（单位：度，顺时针为正）
            float Spin(Stroke s2, params float[] clockwiseSteps)
            {
                var f2 = SelectionHandles.FrameOf(Doc.Selected);
                var grip2 = SelectionHandles.CanvasPosition(SelHandle.Rotate, f2, DpiScale);
                var pivot2 = new Vector2((f2.CanvasAabb.MinX + f2.CanvasAabb.MaxX) * 0.5f,
                                         (f2.CanvasAabb.MinY + f2.CanvasAabb.MaxY) * 0.5f);
                float arm2 = Vector2.Distance(grip2, pivot2);
                float a0 = MathF.Atan2(grip2.Y - pivot2.Y, grip2.X - pivot2.X);

                SendMouse((int)grip2.X, (int)grip2.Y, Native.MOUSEEVENTF_LEFTDOWN);
                SettleFrames(90);
                float travel = 0f;
                foreach (float step in clockwiseSteps)
                {
                    // 传进来的是**增量**：一路加着走，才能真的"转了 200°"。
                    // （第一版写成 `a0 + step` 当绝对角用，两步都落在同一个点上，
                    //  指针没动就没有消息，读数自然只有最后一步——自检当场抓到。）
                    travel += step;
                    float rad = a0 + travel * MathF.PI / 180f;
                    SendMouse((int)(pivot2.X + arm2 * MathF.Cos(rad)),
                              (int)(pivot2.Y + arm2 * MathF.Sin(rad)), 0);
                    SettleFrames(90);
                }
                return SelRotationDegrees;
            }

            // 拖动中"对象现在是什么变换"要问**合成后**的那个矩阵：方案 B 里
            // 模型到松手才动，拖动中的实时位移活在预览矩阵里（对象自己的变换 × 预览矩阵，
            // 和渲染时左乘的完全是同一个式子）。松手后再问 s.Transform 就是它本身。
            Matrix3x2 Live(Stroke s2) => DragPreviewActive
                ? s2.Transform * DragPreviewMatrix : s2.Transform;

            // ① 逆时针 90°：读数为**正**，而且矩阵真的往逆时针转了
            //    （屏幕坐标里逆时针 90° → CreateRotation(-90°) → M12 = -1）
            var spinCcw = NewRound();
            float ccwRead = Spin(spinCcw, -90f);
            Check("逆时针拖 90° → 读数 +90°（逆转为正）",
                  Math.Abs(ccwRead - 90f) < 1.5f, $"读数 {ccwRead:F1}°");
            var liveCcw = Live(spinCcw);
            Check("读数的符号和几何一致（逆时针转出来 M12≈-1）",
                  Math.Abs(liveCcw.M12 + 1f) < 0.05f && Math.Abs(liveCcw.M11) < 0.05f,
                  $"M11={liveCcw.M11:F3} M12={liveCcw.M12:F3}");
            // 方案 B 的契约：拖动中模型**一个字都不动**（动静全在预览里，松手才提交）。
            // 少了这条，将来有人"顺手"把 SetTransformLive 加回去就没人拦得住 ——
            // 而那就是每帧重画走过的面积、19ms/帧 的那条老路。
            Check("拖动中模型不动（只在预览里位移，松手才提交）",
                  DragPreviewActive && spinCcw.Transform.Equals(Matrix3x2.Identity),
                  $"预览={DragPreviewActive}，模型 M11={spinCcw.Transform.M11:F3} M12={spinCcw.Transform.M12:F3}");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);

            // ② 顺时针 90°：读数为**负**
            var spinCw = NewRound();
            float cwRead = Spin(spinCw, 90f);
            Check("顺时针拖 90° → 读数 -90°（顺转为负）",
                  Math.Abs(cwRead + 90f) < 1.5f, $"读数 {cwRead:F1}°");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);

            // ③ 不设上限：同一拖里一路逆时针转 200°，读数必须 > 180°，不许绕回 -160°
            var spinLong = NewRound();
            float longRead = Spin(spinLong, -90f, -90f, -20f);
            Check("同一拖转 200° → 读数 ≈ +200°（不绕回）",
                  longRead > 190f && longRead < 210f,
                  $"读数 {longRead:F1}°（绕回的话会是 -160° 左右）");
            var liveLong = Live(spinLong);
            Check("转 200° 的对象真的转了 200°（矩阵能和读数对上）",
                  Math.Abs(liveLong.M11 - MathF.Cos(200f * MathF.PI / 180f)) < 0.05f
                  && Math.Abs(liveLong.M12 + MathF.Sin(200f * MathF.PI / 180f)) < 0.05f,
                  $"M11={liveLong.M11:F3}（期望 {MathF.Cos(200f * MathF.PI / 180f):F3}）");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);

            // ④ 翻转过的对象：框坐标和屏幕是"反手"的（行列式为负），
            //    读数仍然要以**眼睛看到的方向**为准——屏幕上逆时针拖，就该是正数。
            var spinMirror = NewRound();
            Doc.ApplyTransform(SelectionHandles.MirrorMatrix(spinMirror.WorldInkBounds, horizontal: true));
            SettleFrames(250);
            bool leftHanded = SelectionHandles.IsMirrored(spinMirror.Transform);
            float mirrorRead = Spin(spinMirror, -90f);      // 屏幕上逆时针 90°
            string mirrorNote = leftHanded ? "已镜像（行列式为负）" : "没镜像成（行列式为正）";
            Check("翻转过的对象：屏幕上逆时针拖 → 读数仍为正",
                  leftHanded && mirrorRead > 0f,
                  $"{mirrorNote}，读数 {mirrorRead:F1}°");
            SendMouse(0, 0, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(200);
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS: 度数读数、三种吸附模式、矩阵自洽与标签上屏都正确"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");

        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }


    /// <summary>
    /// 变换命令自检。
    ///
    /// 它要证明的是整个对象模型的**核心论断**：改变换不碰几何。
    /// 具体就是三件事——几何版本号不变（GPU 缓存不用重建）、
    /// 包围盒和空间索引跟着走、撤销能精确回到原样。
    /// </summary>
    private void TransformTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 变换命令自检（改矩阵，不碰几何）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();

        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 0f, 1f), Width = 4f,
        };
        for (int i = 0; i < 60; i++)
            s.AddPoint(200 + i * 5f, 300 + MathF.Sin(i * 0.2f) * 40f, 0.5f, i * 8);
        Doc.AddStroke(s);

        int revisionBefore = s.Revision;
        var boundsBefore = s.WorldBounds;
        float widthBefore = boundsBefore.MaxX - boundsBefore.MinX;

        Doc.Selected.Clear();
        Doc.Selected.Add(s);

        // 以画的起点为中心放大两倍——非等比也不影响这套机制
        var center = new Vector2(boundsBefore.MinX, boundsBefore.MinY);
        int depthBefore = Doc.UndoDepth;
        bool applied = Doc.ApplyTransform(Matrix3x2.CreateScale(2f, 2f, center));
        Check("命令已提交", applied && Doc.UndoDepth == depthBefore + 1,
              $"撤销深度 {depthBefore} -> {Doc.UndoDepth}");

        // ★ 这条是整个设计的要害：几何没有重建
        Check("几何版本号未变", s.Revision == revisionBefore,
              $"Revision {revisionBefore} -> {s.Revision}（GPU 缓存不用重建）");

        var boundsAfter = s.WorldBounds;
        float widthAfter = boundsAfter.MaxX - boundsAfter.MinX;
        Check("包围盒按倍数变大", Math.Abs(widthAfter - widthBefore * 2f) < 0.5f,
              $"{widthBefore:F0} -> {widthAfter:F0} px");

        // 空间索引必须跟着走：在新位置查得到
        var probe = new RectF
        {
            MinX = center.X - 1, MinY = center.Y - 1,
            MaxX = center.X + 1, MaxY = center.Y + 1,
        };
        var hits = new List<Stroke>();
        Doc.QueryGrid(probe, hits);
        Check("空间索引已更新", hits.Contains(s), $"新位置查到 {hits.Count} 个");

        Doc.Undo();
        var boundsUndone = s.WorldBounds;
        bool restored = Math.Abs(boundsUndone.MinX - boundsBefore.MinX) < 0.01f
                     && Math.Abs(boundsUndone.MaxX - boundsBefore.MaxX) < 0.01f
                     && Math.Abs(boundsUndone.MinY - boundsBefore.MinY) < 0.01f
                     && Math.Abs(boundsUndone.MaxY - boundsBefore.MaxY) < 0.01f;
        Check("撤销精确回原位", restored,
              $"({boundsUndone.MinX:F2},{boundsUndone.MinY:F2})-({boundsUndone.MaxX:F2},{boundsUndone.MaxY:F2})");

        Doc.Redo();
        Check("重做又回到放大后", Math.Abs((s.WorldBounds.MaxX - s.WorldBounds.MinX) - widthAfter) < 0.5f, "");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 变换只改矩阵，几何与缓存未受影响" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }


    /// <summary>
    /// 图像对象自检：造图 → 命中 → 上屏 → 走通用操作（复制/翻转/删除）
    /// → 存档往返 → 剪贴板往返。
    ///
    /// 最后两项是重点：图像像素**必须**进得了文件、进得了剪贴板，
    /// 否则"截图发给学生"这件事就是断的。
    /// </summary>
    private void ImageTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图像对象自检（截图 / 粘贴的图）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float cx = _virtualX + _virtualW * 0.4f, cy = _virtualY + _virtualH * 0.4f;
        const int W = 64, H = 64;
        byte[] pix = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool left = x < W / 2;
                pix[i + 0] = left ? (byte)255 : (byte)0;     // B
                pix[i + 1] = 0;                              // G
                pix[i + 2] = left ? (byte)255 : (byte)0;     // R（左半品红、右半黑）
                pix[i + 3] = 255;
            }

        Doc.Clear();
        Doc.ClearHistory();
        var img = ImageData.Adopt(W, H, pix, true);
        var placed = Doc.AddImage(img, cx, cy, 1f);
        Check("落地：对象是图像类型", placed != null && placed.IsImage, $"{placed?.Kind}");
        Check("落地：包围盒 = 像素尺寸 × 缩放",
              Math.Abs(placed.Bounds.MaxX - W) < 0.01f && Math.Abs(placed.Bounds.MaxY - H) < 0.01f,
              $"{placed.Bounds.MaxX:F0}×{placed.Bounds.MaxY:F0}");
        Check("落地：变换里带平移", Math.Abs(placed.Transform.M31 - cx) < 0.01f
                                  && Math.Abs(placed.Transform.M32 - cy) < 0.01f,
              $"({placed.Transform.M31:F0},{placed.Transform.M32:F0})");
        Check("命中：压在图上算命中", placed.HitTestExact(cx + 4, cy + 4), "");
        Check("命中：图外面不算", !placed.HitTestExact(cx - 40, cy - 40), "");

        // 真的画上去了吗：左边像素应该是品红
        Doc.InvalidateAll();
        SettleFrames(400);
        int leftPixels = ScreenProbe.CountMagenta((int)cx + 2, (int)cy + 2, W / 2 - 4, H - 4);
        Check("上屏：左半张画出来了（品红像素 > 500）", leftPixels > 500, $"{leftPixels} 像素");

        // 通用操作：复制 / 翻转 / 删除都走同一套（因为图像就是一个对象）
        Doc.Selected.Clear();
        Doc.Selected.Add(placed);
        Doc.DuplicateSelected(30f, 30f);
        Check("复制：图像也能复制（通用操作）", Doc.Strokes.Count == 2, $"{Doc.Strokes.Count} 个对象");
        var copy = Doc.Strokes[1];
        Check("复制：副本是新身份、像素共享",
              copy.Id != placed.Id && ReferenceEquals(copy.Image, placed.Image), $"id {copy.Id}");
        Doc.ApplyTransform(SelectionHandles.MirrorMatrix(placed.Bounds, horizontal: true));
        Check("翻转：图像也能翻（走同一条变换）", Math.Abs(copy.Transform.M11 + 1f) < 0.01f
                                              || Math.Abs(copy.Transform.M11 - 1f) > 0.5f,
              $"M11={copy.Transform.M11:F2}");

        // 存档往返
        Doc.Clear();
        Doc.ClearHistory();
        var img2 = ImageData.Adopt(W, H, (byte[])pix.Clone(), true);
        Doc.AddImage(img2, cx, cy, 1f);
        byte[] blob = InkSerializer.Save(Doc);
        var doc2 = new InkDocument();
        InkSerializer.LoadInto(doc2, blob);
        Check("存档：对象数对上", doc2.Strokes.Count == 1, $"{doc2.Strokes.Count} 个");
        bool samePixels = doc2.Strokes.Count == 1 && doc2.Strokes[0].Image != null
            && doc2.Strokes[0].Image.Width == W && doc2.Strokes[0].Image.Height == H;
        if (samePixels)
        {
            var a = doc2.Strokes[0].Image.Bgra;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != pix[i]) diff++;
            samePixels = diff == 0;
            Check("存档：像素逐字节一致", samePixels, $"差异 {diff} 字节");
        }
        else Check("存档：像素逐字节一致", false, "图像没读回来");
        Console.WriteLine($"    存档体积：{blob.Length / 1024.0:F1} KB（{W}×{H} 原始像素）");

        // 剪贴板往返（别的程序占着剪贴板时会失败，那是环境问题，不是 bug）
        var clipPix = (byte[])pix.Clone();
        bool wrote = ClipboardImage.SetImage(clipPix, W, H);
        if (!wrote)
        {
            Console.WriteLine("    剪贴板：写不进去（被别的程序占着？）——这项跳过");
        }
        else if (!ClipboardImage.TryGetImage(out var back, out int bw, out int bh, out _))
        {
            Check("剪贴板：读回来", false, "写成功但读不回来");
        }
        else
        {
            Check("剪贴板：尺寸一致", bw == W && bh == H, $"{bw}×{bh}");
            int diff = 0;
            for (int i = 0; i < Math.Min(back.Length, pix.Length); i++) if (back[i] != pix[i]) diff++;
            Check("剪贴板：像素一致", diff == 0, $"差异 {diff} 字节");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 图像对象全通（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }


    /// <summary>
    /// 截图自检（真机）：合成鼠标拖一个框，然后验三件事——
    ///   1. 生成了一个**图像对象**，尺寸等于拖出来的框（按 DPI 折成画布尺寸）；
    ///   2. 它落在**视口左上角**并自动选中（用户下一步就是拖它）；
    ///   3. **没有把自己的批注拍进去**（抓之前覆盖层藏起来了）——
    ///      这一条最重要：先在框里画一大片品红，抓到的图里品红必须≈0。
    /// </summary>
    private void CaptureTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 截图自检（模式段进屋 → 拖框 → 调整 → 原位落 → 剪贴板）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        float dpi = DpiScale;

        if (SkipIfNoSyntheticInput("截图全流程（需要拖框）")) { _quit = true; return; }

        // 先在要抓的这一块上涂满自己的墨（品红），用来验证"抓的时候墨不在图里"
        Doc.Clear();
        Doc.ClearHistory();
        for (int k = 0; k < 12; k++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 40f * dpi,
            };
            float y = cy - 110 + k * 20;
            for (int i = 0; i <= 20; i++) s.AddPoint(cx - 200 + i * 20, y, 0.9f, i);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(500);
        int onScreenInk = ScreenProbe.CountMagenta((int)(cx - 200), (int)(cy - 120), 400, 260);
        Check("准备：框里已经有一片墨", onScreenInk > 5000, $"{onScreenInk} 像素");

        // 换截图工具，拖一个 300×200 的框（两种模式各抓一次，所以抽成函数）。
        //
        // 8.3.0 起流程是"**拖框 → 松手进调整 → Enter/✓ 确认**"（不再松手即落图）：
        // 拖完直接量读数/遮罩，再确认；每段都用**真实键盘**（PostMessage 一个 Enter/Esc 到窗口，
        // 和 --keytest 那条一样）。
        bool boardWas = BoardOn;
        BoardOn = true;                    // 白底：亮度/像素判据才稳（桌面颜色不可控）
        Doc.InvalidateAll();
        SettleFrames(300);
        Tool = Tool.Capture;
        int x0 = (int)(cx - 150), y0 = (int)(cy - 100);
        int x1 = (int)(cx + 150), y1 = (int)(cy + 100);
        int midReadoutDark = 0;                 // 拖动中"尺寸读数"那块有多深（见下面的量法）
        bool midAdjusting = false;              // 松手之后进没进"调整"（不再立刻落图）
        float lumaBefore = (float)ScreenProbe.AvgLuma((int)(x0 - 220), (int)(cy - 15), 60, 30);

        void PostKey(ushort vk)
        {
            Native.PostMessage(_windows[0].Hwnd, (uint)Native.WM_KEYDOWN, new IntPtr(vk), IntPtr.Zero);
            Native.PostMessage(_windows[0].Hwnd, (uint)Native.WM_KEYUP, new IntPtr(vk), IntPtr.Zero);
        }

        void DragCapture()
        {
            Host.Commands.SetTool(Tool.Capture);      // 等价于"点了一下「截屏」格"（不进取景）
            SendMouse(x0, y0, 0);
            SettleFrames(40);
            SendMouse(x0, y0, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(60);
            SendMouse((x0 + x1) / 2, y0 + 20, 0);
            SettleFrames(40);
            SendMouse(x1, y1, 0);
            SettleFrames(60);
            // **拖动中要有尺寸读数**（用户 2026-09-17："截图使用不顺手，光标配合也感觉不好"）：
            // 框的**右下角**外侧 6 逻辑像素处会画一个深色胶囊写着 "宽 × 高"
            //（8.3.0 从"左下角"挪到"正在拖的那个角"）。
            {
                float s = dpi;
                int lx = (int)(x1 - 110 * s), ly = (int)(y1 + 4 * s);
                int lw = (int)(110 * s), lh = (int)(30 * s);
                midReadoutDark = ScreenProbe.CountDark(lx, ly, lw, lh);
            }
            SendMouse(x1, y1, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(300);
            midAdjusting = CaptureActive && CaptureAdjusting;
        }

        // ---- 0. 进入取景（8.3.1，照微信）：点格子只出模式条；点模式段 = 进屋 ----
        Host.Commands.SetTool(Tool.Capture);        // 等价于"点了一下「截屏」格"
        SettleFrames(200);
        Check("点「截屏」格：先不进取景（出模式条，屏幕还没灰）",
              !CaptureActive && Tool == Tool.Capture,
              $"取景={CaptureActive}，工具={Tool}");
        Host.Commands.EnterCapture();               // 等价于"点了一下模式段"
        SettleFrames(400);
        Check("点模式段：**立刻进入取景**（整屏灰下来、工具还是截图）",
              CaptureActive && !CaptureAdjusting && Tool == Tool.Capture,
              $"取景={CaptureActive}，调整={CaptureAdjusting}，工具={Tool}");
        {
            float lumaIn = (float)ScreenProbe.AvgLuma((int)(x0 - 220), (int)(cy - 15), 60, 30);
            Check("进入取景屏幕就压暗（同一处亮度下降）", lumaBefore - lumaIn > 30f,
                  $"进之前 {lumaBefore:F0} → 进之后 {lumaIn:F0}");
        }

        // 单击一下（没拖出框）＝ 不退出取景（8.3.1；以前这一下会把整个模式退掉）
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(60);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(300);
        Check("单击一下（没拖出框）：**继续取景**、不落图",
              CaptureActive && !CaptureAdjusting && Doc.Strokes.FindLast(s => s.IsImage) == null,
              $"取景={CaptureActive}，调整={CaptureAdjusting}");

        // ---- 0b 右上角「✕ 取消」：触摸屏没有 Esc / 右键，得有颗点得到的出口 ----
        {
            var cancelBtn = CaptureCancelRect();       // 画布坐标；鼠标给的是屏幕坐标
            Check("右上角挂着「✕ 取消」（触摸屏的出口）",
                  cancelBtn.MaxX - cancelBtn.MinX > 40f
                  && cancelBtn.MinY < ViewportCanvas.MinY + ViewportCanvas.MaxY / 2f,
                  $"按钮 ({cancelBtn.MinX:F0},{cancelBtn.MinY:F0})..({cancelBtn.MaxX:F0},{cancelBtn.MaxY:F0})");
            int bx = (int)((cancelBtn.MinX + cancelBtn.MaxX) * 0.5f);
            int by = (int)((cancelBtn.MinY + cancelBtn.MaxY) * 0.5f + ViewOffsetY);
            SendMouse(bx, by, 0);                              SettleFrames(80);
            SendMouse(bx, by, Native.MOUSEEVENTF_LEFTDOWN);     SettleFrames(80);
            SendMouse(bx, by, Native.MOUSEEVENTF_LEFTUP);       SettleFrames(300);
            Check("点右上角「✕」：退出取景、不落图、还给上一个工具",
                  !CaptureActive && !CaptureAdjusting
                  && Doc.Strokes.FindLast(s => s.IsImage) == null && Tool != Tool.Capture,
                  $"取景={CaptureActive}，调整={CaptureAdjusting}，工具={Tool}");
        }

        DragCapture();
        Check("拖框松手：**先进调整阶段**（不立刻落图，可以拖边/确认/取消）",
              midAdjusting && Doc.Strokes.FindLast(s => s.IsImage) == null,
              $"调整={CaptureAdjusting}，活动={CaptureActive}，"
              + $"图形对象 {(Doc.Strokes.Where(s => s.IsImage).Count())} 个（该 0）");

        // 遮罩（8.3.0）：同一处（框外的白板）在被遮罩压暗
        {
            float lumaMasked = (float)ScreenProbe.AvgLuma((int)(x0 - 220), (int)(cy - 15), 60, 30);
            Check("遮罩：框外被压暗（同一处亮度明显下降）", lumaBefore - lumaMasked > 30f,
                  $"遮罩前 {lumaBefore:F0} → 遮罩后 {lumaMasked:F0}");
        }

        PostKey(0x0D);                     // Enter = 完成
        SettleFrames(700);

        var shot = Doc.Strokes.FindLast(s => s.IsImage);
        Check("确认：生成了图像对象", shot != null, shot == null ? "没有" : $"{shot.Image.Width}×{shot.Image.Height} 物理像素");
        if (shot == null)
        {
            Console.WriteLine();
            Console.WriteLine("  FAIL: 截图没生成对象");
            _quit = true;
            return;
        }

        Check("截图：物理尺寸 = 拖出来的框", Math.Abs(shot.Image.Width - 300) <= 2
                                          && Math.Abs(shot.Image.Height - 200) <= 2,
              $"{shot.Image.Width}×{shot.Image.Height}，期望 300×200");
        Check("截图：画布尺寸 = 物理像素 1:1（修掉高 DPI 下「贴出来只有一半大」的老 bug）",
              Math.Abs((shot.Transform.M11 * shot.Image.Width) - 300f) < 3f,
              $"画布宽 {(shot.Transform.M11 * shot.Image.Width):F0}，期望 300（dpi={dpi:F2}）");

        Check("截图：**原位落**（屏幕上截哪儿、画布上就落在哪儿，夹进视口留边距）",
              Math.Abs(shot.WorldBounds.MinX - x0) < 3f
              && Math.Abs(shot.WorldBounds.MinY - (y0 - ViewOffsetY)) < 3f,
              $"落在 ({shot.WorldBounds.MinX:F0},{shot.WorldBounds.MinY:F0})，"
              + $"期望 ({x0},{y0 - ViewOffsetY:F0})");
        Check("截图：自动选中、并切回框选工具",
              Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], shot) && Tool == Tool.Marquee,
              $"选中 {Doc.Selected.Count} 个，工具 {Tool}");
        Check("截图：确认之后状态收干净（不在调整里）",
              !CaptureActive && !CaptureAdjusting && CaptureFrozenBgra == null,
              $"活动={CaptureActive}，调整={CaptureAdjusting}，冻结帧={(CaptureFrozenBgra == null ? "已放" : "还拿着")}");

        // 核心一条：抓到的图里不该有自己的墨
        int shotInk = 0;
        var px = shot.Image.Bgra;
        for (int i = 0; i + 3 < px.Length; i += 4)
            if (px[i + 2] > 200 && px[i + 1] < 90 && px[i] > 200) shotInk++;
        Check("截图：自己的批注没被拍进去（品红≈0）", shotInk < 200, $"{shotInk} 像素");

        // 剪贴板：截图必须同时进剪贴板
        if (ClipboardImage.TryGetImage(out var clip, out int cw, out int ch, out _))
        Check("截图：同时进了剪贴板", cw == shot.Image.Width && ch == shot.Image.Height, $"{cw}×{ch}");
        else
            Console.WriteLine("    剪贴板：读不出来（可能被别的程序占着）——这项跳过");

        Check("拖动中显示了尺寸读数（框右下角那块有深色胶囊）", midReadoutDark > 800,
              $"{midReadoutDark} 像素（读数是 {300 / dpi:F0}×{200 / dpi:F0} 的一个深色胶囊）");

        // 覆盖层藏过又显示：屏幕上不该留残影（墨应该还在原处）
        int afterInk = ScreenProbe.CountMagenta((int)(cx - 200), (int)(cy - 120), 400, 260);
        Check("截图后屏幕恢复正常（批注还在，没有残影）", afterInk > 5000,
              $"截前 {onScreenInk} 像素，截后 {afterInk} 像素");

        // ---- ①b Esc = 退出取景、**还给进来之前的工具**（不落图，8.3.1）----
        int imgsBeforeCancel = Doc.Strokes.Count(s => s.IsImage);
        DragCapture();
        PostKey(0x1B);
        SettleFrames(400);
        Check("Esc 退出取景：不落图、状态清干净、**工具还给上一个**",
              Doc.Strokes.Count(s => s.IsImage) == imgsBeforeCancel
              && !CaptureActive && !CaptureAdjusting && CaptureFrozenBgra == null
              && Tool != Tool.Capture,
              $"图像 {imgsBeforeCancel} → {Doc.Strokes.Count(s => s.IsImage)}，"
              + $"取景={CaptureActive}，工具={Tool}（进截图之前是 Marquee）");

        // ---- ①c 调整：拖右下角手柄 → 确认出来的图就是框最后的大小 ----
        DragCapture();
        {
            SendMouse(x1, y1, 0);                                   SettleFrames(60);
            SendMouse(x1, y1, Native.MOUSEEVENTF_LEFTDOWN);          SettleFrames(50);
            SendMouse(x1 - 40, y1 - 30, 0);                         SettleFrames(40);
            SendMouse(x1 - 80, y1 - 60, 0);                         SettleFrames(40);
            SendMouse(x1 - 80, y1 - 60, Native.MOUSEEVENTF_LEFTUP);  SettleFrames(200);
        }
        PostKey(0x0D);
        SettleFrames(700);
        var shot3 = Doc.Strokes.FindLast(s => s.IsImage);
        Check("调整：拖右下角手柄之后确认，图 = 框最后的大小（约 220×140）",
              shot3 != null && Math.Abs(shot3.Image.Width - 220) <= 6 && Math.Abs(shot3.Image.Height - 140) <= 6,
              shot3 == null ? "没有对象" : $"{shot3.Image.Width}×{shot3.Image.Height}，期望约 220×140");

        // ---- ①d 双击框内 = 完成（微信的手感，8.3.1）----
        DragCapture();
        {
            // 两下"按—放"**挨着发**：判据是 400ms / 6 逻辑像素以内算同一处，
            // 中间不能 SettleFrames（一帧 16ms 也可能把两次按下拉开）。
            SendMouse((int)cx, (int)cy, 0);
            SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
            SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTUP);
            SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
            SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTUP);
            SettleFrames(600);
        }
        var shot4 = Doc.Strokes.FindLast(s => s.IsImage);
        Check("双击框内 = 完成（落了一张新的、截图模式收场）",
              shot4 != null && !ReferenceEquals(shot4, shot3) && !CaptureActive,
              shot4 == null ? "没有对象" : $"{shot4.Image.Width}×{shot4.Image.Height}，取景={CaptureActive}");

        // ---- ①e 方向键微调框（8.3.1；投影上鼠标很难微调一格）----
        DragCapture();
        PostKey(0x27);                       // →：框整体右移 1 逻辑像素
        SettleFrames(150);
        PostKey(0x0D);
        SettleFrames(700);
        var shot5 = Doc.Strokes.FindLast(s => s.IsImage);
        Check("调整：方向键把框右移 1 逻辑像素，确认出来的图也跟着右移",
              shot5 != null && Math.Abs(shot5.WorldBounds.MinX - (x0 + DpiScale)) < 3f,
              shot5 == null ? "没有对象" : $"落在 {shot5.WorldBounds.MinX:F0}，期望 {x0 + DpiScale:F0}");

        // ---- ② 第二种模式：**截图**（连批注一起拍）----
        //
        // 用户 2026-09-17："截图功能是不是也应该对接了，也可以参考 inkclass"。
        // InkClass 给的是两项菜单（快速截图 / 隐藏界面截图），我们把这两项放进
        // **截图那一格的上带**：[截图][隐藏窗口截图]（8.3.1 照微信改的名；
        // 8.3.0 叫"连批注拍/只拍下层"，"直接截取/隐藏界面"是最早的名字）。
        //
        // ⚠ 先把前两次落下的图删掉：**原位落**之后它们正好盖在接下来要拍的那一块上，
        // 不删的话拍到的是那两张图（白板，没有墨），"板书在图里"这条必然假红。
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) if (s.IsImage) Doc.Selected.Add(s);
        Doc.DeleteSelected();
        Doc.ClearHistory();
        SettleFrames(200);

        Host.Commands.SetCaptureHideInk(false);
        SettleFrames(250);
        Check("模式切到「截图」", !Host.State.CaptureHideInk,
              $"hideInk = {Host.State.CaptureHideInk}");

        DragCapture();
        PostKey(0x0D);
        SettleFrames(700);
        var shot2 = Doc.Strokes.FindLast(s => s.IsImage);
        Check("截图：又生成了一个图像对象", shot2 != null && !ReferenceEquals(shot2, shot5),
              shot2 == null ? "没有" : $"{shot2.Image.Width}×{shot2.Image.Height}");
        if (shot2 != null)
        {
            var q = shot2.Image.Bgra;
            int ink2 = 0, frame = 0;
            for (int y = 0; y < shot2.Image.Height; y++)
                for (int x = 0; x < shot2.Image.Width; x++)
                {
                    int i = (y * shot2.Image.Width + x) * 4;
                    byte b = q[i], g = q[i + 1], r = q[i + 2];
                    if (r > 200 && g < 90 && b > 200) ink2++;                       // 品红 = 板书
                    // 取景框/准线是琥珀色（1, 0.68, 0.10）：只查图片最外 3 像素那一圈——
                    // 框就画在抓取矩形的边上，真被拍进去必然落在这里
                    if ((x < 3 || y < 3 || x >= shot2.Image.Width - 3 || y >= shot2.Image.Height - 3)
                        && r > 200 && g > 140 && g < 215 && b < 90) frame++;
                }
            Check("截图（连批注）：**板书在图里**（品红 > 2000）", ink2 > 2000, $"{ink2} 像素");
            Check("截图（连批注）：取景框/准线没被拍进去（最外一圈没有琥珀色）", frame == 0, $"{frame} 像素");
        }

        // 收尾：模式还原成默认的"隐藏窗口截图"；白板还原
        Host.Commands.SetCaptureHideInk(true);
        BoardOn = boardWas;
        Doc.InvalidateAll();
        SettleFrames(150);
        Check("收尾：模式还原成「隐藏批注截取」", Host.State.CaptureHideInk,
              $"hideInk = {Host.State.CaptureHideInk}");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? $"  PASS: 截图全通（{pass} 项）" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

}
