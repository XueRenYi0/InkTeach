// 本文件由 App.cs 拆出（2026-10-07）：ShapeLibrary 这一组。
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

    private void ShapeTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图形命中测试（描边轮廓，中间是空的）===");

        float cx = _virtualX + 900, cy = _virtualY + 700;
        float halfW = 8f * DpiScale;          // 半笔宽（物理像素）
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(1f, 0f, 1f, 1f), Width = halfW * 2f,
        };
        rect.AddPoint(cx - 200, cy - 150, 1f, NowMs);
        rect.AddPoint(cx + 200, cy + 150, 1f, NowMs);

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Check("边上命中", rect.HitTestExact(cx, cy - 150), "");
        Check("正中央不命中", !rect.HitTestExact(cx, cy), "轮廓中间是空的");
        Check("远处不命中", !rect.HitTestExact(cx + 900, cy + 900), "");

        float off = halfW + 20f;   // 离中心线比半笔宽还远
        Check("容差外不命中", !rect.HitTestExact(cx, cy - 150 + off), $"偏移 {off:F0}px");
        Check("容差内命中", rect.HitTestExact(cx, cy - 150 + off, off + 5f), $"容差 {off + 5f:F0}px");

        var saved = rect.Transform;
        rect.Transform = rect.Transform * Matrix3x2.CreateTranslation(400, 0);
        Check("平移后原位置不命中", !rect.HitTestExact(cx, cy - 150), "");
        Check("平移后新位置命中", rect.HitTestExact(cx + 400, cy - 150), "变换必须参与命中");
        rect.Transform = saved;

        Doc.Clear();
        Doc.AddStroke(rect);
        int removed = Doc.EraseAt(cx, cy, 10f * DpiScale);
        Check("擦正中央不误删", removed == 0, $"删了 {removed} 个");
        removed = Doc.EraseAt(cx, cy - 150, 10f * DpiScale);
        Check("擦边上应删除", removed == 1, $"删了 {removed} 个");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 图形命中判定正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }


    /// <summary>
    /// 图形工具第一轮（直线先行）自检。六段，前两段走**真机输入**，中间一段是纯数学，
    /// 后面三段走"选择手势"的内部入口（自检里一直是这么驱动手柄的）。
    ///
    ///   A. 接线画图入口：四种工具各拖一次 → Kind 对、两端正好落在按下点 / 松手点、
    ///      一次拖拽 = 一步撤销、撤销一次就干净；
    ///   B. 拖动太短不产生对象（点一下、挪两个像素各一条）；
    ///   C. 画线吸附（数学层）：软吸附 0/30/45/60/90 ±3°、Shift 15° 网格、Alt 自由，
    ///      以及"吸附时绕起点转、保持长度"；顺带把 α 的五个读数钉死；
    ///   D. 拖端点（真机层）：模型在拖动中一个字不改、内容层一帧不重画、
    ///      长度与倾斜角按预期变、Revision 真的变了、一步撤销、α 标签真的上屏；
    ///   E. 旋转过的直线拖端点：端点仍落在指针位置（走 Transform⁻¹ 那条路）。
    /// </summary>
    /// <summary>
    /// 自检 `--librarytest`：**图库（我的图形）**——"选中 → 存入 → 面板 → 落笔插入 → 整理删除"
    /// 整条链真机跑一遍（用户 2026-09-22 要的"图像收藏"，口径见 计划-图形工具.md §41）。
    ///
    /// 为什么每一段都走真机而不是直接调 API：这条链上有四处"看得见的东西"——
    ///   ① 操作条上那颗图标**画在哪儿、点得中吗**（几何与命中同源，`--handletest` 的教训）；
    ///   ② 上带最后那一段「图库」**点得开面板吗**（它是一段"动作"，不进工具表）；
    ///   ③ 面板里的缩略图**真的画出来了吗**——只断言条目数的话，
    ///      "存进去了、格子却是空的"照样绿，所以这里数屏幕像素；
    ///   ④ 落笔插入：**落在按下的地方**、插入后是**选中的**、**一步撤销**。
    ///
    /// ⚠ 图库目录用 <see cref="ShapeLibrary.DirOverride"/> 指到临时目录——
    /// **绝不动用户真正的收藏**（同 `InkSettings.PathOverride` 那条规矩）。
    /// </summary>
    private void LibraryTest(string shotPath)
    {
        Console.WriteLine("===== 图库（我的图形）：存入 → 面板 → 落笔插入 → 整理删除 =====");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-library-test");
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
        ShapeLibrary.DirOverride = dir;

        // 界面要挂上：图库的两个入口（操作条那颗图标要靠界面推上来的主题画、
        // 上带最后那一段是界面画并命中）都在界面上。
        SetUiFactory(() => new InkUi.FullUi());
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine("  没有界面，跳过（图库两个入口都在界面上）");
            Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
            ShapeLibrary.DirOverride = null;
            _quit = true;
            return;
        }

        // 面板刚挂上是**收起态（一个球）**：先点一下球展开，不然后面点"图形"那一格
        // 点的是球（点球＝展开），段一个都点不到 —— ShapeBandTest 第一次跑全红就是这个原因。
        if (!ui.ExpandedForTest)
        {
            var bb = ui.QueryBounds();
            ClickPhys((bb.MinX + bb.MaxX) * 0.5f * DpiScale,
                      (bb.MinY + bb.MaxY) * 0.5f * DpiScale);
            SettleFrames(300);
        }
        Check("面板点球展开了（不然上带那一段点不到）", ui.ExpandedForTest,
              $"ExpandedForTest={ui.ExpandedForTest}");

        void DragPhys(float x0, float y0, float x1, float y1, int holdFrames = 200)
        {
            SendMouse((int)x0, (int)y0, 0);                                 SettleFrames(60);
            SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);       SettleFrames(60);
            SendMouse((int)((x0 + x1) * 0.5f), (int)((y0 + y1) * 0.5f), 0); SettleFrames(40);
            SendMouse((int)x1, (int)y1, 0);                                 SettleFrames(40);
            SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);         SettleFrames(holdFrames);
        }
        void ClickPhys(float x, float y)
        {
            SendMouse((int)x, (int)y, 0);                            SettleFrames(80);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(320);
        }

        Doc.Clear();
        Doc.ClearHistory();
        CurrentColor = new Color4(1f, 0f, 1f, 1f);      // 品红：屏幕判据靠它数
        SetToolFromUi(Tool.Rectangle);

        // ---- A. 画一个矩形，把它存进图库 ----
        float ax = _virtualX + 600, ay = _virtualY + 900;
        DragPhys(ax, ay, ax + 320f, ay + 220f);
        Check("A：矩形画出来了，而且是自动选中状态",
              Doc.Strokes.Count == 1 && Doc.Selected.Count == 1,
              $"对象 {Doc.Strokes.Count} 个，选中 {Doc.Selected.Count} 个");

        // 刚画完是"收起来"那一态（见 §40），要先把条摊开才点得到图标。
        {
            var f = SelectionHandles.FrameOf(Doc.Selected);
            var dot = SelectionHandles.BarCollapsedRect(f.CanvasAabb, DpiScale, ViewportCanvas);
            ClickPhys((dot.MinX + dot.MaxX) * 0.5f, (dot.MinY + dot.MaxY) * 0.5f);
            Check("A：点圆钮把操作条摊开了", !BarDrawnCollapsed,
                  $"BarDrawnCollapsed={BarDrawnCollapsed}");
        }

        // 点「图库」那一格：**走真机的命中**（几何和绘制同源，不是直接调动作）。
        var frame0 = SelectionHandles.FrameOf(Doc.Selected);
        var libCell = SelectionHandles.BarButtonRect((int)SelBarButton.Library, frame0.CanvasAabb,
                                                     DpiScale, ViewportCanvas);
        Check("A：操作条上那颗「图库」在屏幕里（没被夹出去）",
              libCell.MinX > 0 && libCell.MaxY < _virtualY + _virtualH,
              $"格子 ({libCell.MinX:F0},{libCell.MinY:F0})-({libCell.MaxX:F0},{libCell.MaxY:F0})");
        ClickPhys((libCell.MinX + libCell.MaxX) * 0.5f, (libCell.MinY + libCell.MaxY) * 0.5f);
        var entries = ShapeLibrary.List();
        Check("A：存进图库了（目录里多出一个条目，里面就是刚画的那个矩形）",
              entries.Count == 1 && entries[0].Strokes.Count == 1
              && entries[0].Strokes[0].Kind == StrokeKind.Rectangle,
              $"条目 {entries.Count} 个" + (entries.Count > 0
                  ? $"，第一个里有 {entries[0].Strokes.Count} 个对象"
                  : ""));

        // ---- B. 从上带最后那一段「图库」打开面板（真机点段）----
        {
            var cell = ui.CellRectForTest(8);
            ClickPhys((cell.MinX + cell.MaxX) * 0.5f * DpiScale, (cell.MinY + cell.MaxY) * 0.5f * DpiScale);
            // 上带只在指针停在面板上时张开：合成鼠标偶尔丢移动，给三次机会（同 ShapeBandTest）。
            for (int attempt = 0; attempt < 3 && !ui.RailOpenForTest; attempt++)
            {
                var bar = ui.BarRectForTest;
                SendMouse((int)((bar.MinX + bar.MaxX) * 0.5f * DpiScale),
                          (int)((bar.MinY + bar.MaxY) * 0.5f * DpiScale) - attempt, 0);
                SettleFrames(350);
            }
            int segCount = ui.BandSegmentCountForTest;
            Check("B：图形那一格多了最后一段（工具段 ＋ 图库）",
                  segCount == 23, $"现在 {segCount} 段（22 个图形 ＋ 1 段图库）");
            var seg = ui.SegmentRectForTest(segCount - 1);
            ClickPhys((seg.MinX + seg.MaxX) * 0.5f * DpiScale,
                      (seg.MinY + seg.MaxY) * 0.5f * DpiScale);
            Check("B：点那一段 → 图库面板开了，而且读到了那个条目",
                  LibraryPanelOpen && LibraryEntries.Count == 1,
                  $"面板 {LibraryPanelOpen}，条目 {LibraryEntries.Count} 个");
        }

        // ---- C. 面板里的缩略图真的画出来了吗（数屏幕像素）----
        {
            var panel = LibraryPanelRectNow();
            var cell = LibraryLayout.CellRect(panel, DpiScale, 0);
            int pix = ScreenProbe.CountMagenta((int)cell.MinX, (int)cell.MinY,
                                               (int)(cell.MaxX - cell.MinX),
                                               (int)(cell.MaxY - cell.MinY));
            Check("C：格子里真的画着那个矩形的缩略图（品红像素）", pix > 30, $"{pix} 像素");

            // 出图（给了路径才出）：面板长什么样只能看——标题行、格子、悬停、
            // 空态文案、整理角标这些"排版对不对"的判据不在断言里。
            if (!string.IsNullOrEmpty(shotPath))
            {
                if (OffscreenFloatingShot(shotPath, panel.Inflate(16f)))
                    Console.WriteLine($"  已出图：{shotPath}");
                else Console.WriteLine("  出图失败");
            }
        }

        // ---- D. 点第一格 → 进"落笔插入"态；再到画布上按一下 → 落在那儿 ----
        {
            var panel = LibraryPanelRectNow();
            var cell = LibraryLayout.CellRect(panel, DpiScale, 0);
            ClickPhys((cell.MinX + cell.MaxX) * 0.5f, (cell.MinY + cell.MaxY) * 0.5f);
            Check("D：点格子 = 准备插入，面板自己收起来了",
                  LibraryInsertArmed && !LibraryPanelOpen,
                  $"待插入 {LibraryInsertArmed}，面板 {LibraryPanelOpen}");

            // 落点：画布右侧空地。**按下的地方 = 插入内容的包围盒左上角**。
            float px = ax + 520f, py = ay - 380f;
            ClickPhys(px, py);
            Check("D：按下就插进来了（对象数 +1）", Doc.Strokes.Count == 2,
                  $"对象 {Doc.Strokes.Count} 个（期望 2）");
            var placed = Doc.Strokes[1];
            var box = EditRegion.Of(new[] { placed });
            var srcBox = EditRegion.Of(new[] { Doc.Strokes[0] });
            Check("D：包围盒左上角正好落在按下的地方、大小和原件一样",
                  MathF.Abs(box.MinX - px) < 3f && MathF.Abs(box.MinY - py) < 3f
                  && MathF.Abs((box.MaxX - box.MinX) - (srcBox.MaxX - srcBox.MinX)) < 0.5f
                  && MathF.Abs((box.MaxY - box.MinY) - (srcBox.MaxY - srcBox.MinY)) < 0.5f,
                  $"落点 ({px:F0},{py:F0})，实际 ({box.MinX:F0},{box.MinY:F0})，"
                  + $"尺寸 {box.MaxX - box.MinX:F1}×{box.MaxY - box.MinY:F1}"
                  + $"（原件 {srcBox.MaxX - srcBox.MinX:F1}×{srcBox.MaxY - srcBox.MinY:F1}）");
            Check("D：插入之后是选中的（能直接拖走）",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == placed,
                  $"选中 {Doc.Selected.Count} 个");
        }

        // ---- E. 一步撤销 + 整理模式删条目 ----
        Doc.Undo();
        SettleFrames(150);
        Check("E：插入是**一步**撤销（退回插入前）", Doc.Strokes.Count == 1,
              $"撤销后对象 {Doc.Strokes.Count} 个（期望 1）");

        {
            Doc.Selected.Clear();
            OpenLibraryPanel();
            SettleFrames(120);
            var panel = LibraryPanelRectNow();
            ClickPhys((LibraryLayout.EditRect(panel, DpiScale).MinX
                       + LibraryLayout.EditRect(panel, DpiScale).MaxX) * 0.5f,
                      (LibraryLayout.EditRect(panel, DpiScale).MinY
                       + LibraryLayout.EditRect(panel, DpiScale).MaxY) * 0.5f);
            Check("E：点「整理」进了整理模式（触摸屏删格子的入口）", LibraryEditMode,
                  $"LibraryEditMode={LibraryEditMode}");

            var cell = LibraryLayout.CellRect(panel, DpiScale, 0);
            var badge = LibraryLayout.BadgeRect(cell, DpiScale);
            ClickPhys((badge.MinX + badge.MaxX) * 0.5f, (badge.MinY + badge.MaxY) * 0.5f);
            Check("E：点红 ✕ 把条目删了（目录里也真没了）",
                  LibraryEntries.Count == 0 && ShapeLibrary.List().Count == 0,
                  $"面板里 {LibraryEntries.Count} 个，目录里 {ShapeLibrary.List().Count} 个");
        }

        CloseLibraryPanel();
        ShapeLibrary.DirOverride = null;      // 收尾：还回真实目录
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch { }
        Console.WriteLine(fail == 0
            ? $"  PASS: 图库（存入 / 面板 / 落笔插入 / 整理删除）都对（{pass} 项）"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");
        _quit = true;
    }


    private void ShapeToolTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图形工具自检（画图入口 / 吸附 / 端点编辑）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-34}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        // **白板打底**：后面有几条判据要数屏幕上的强调色像素，透明批注下桌面上
        // 什么颜色都可能出现（这一条在旋转自检里踩过）。板色取深灰，和强调色分得开。
        BoardOn = true;
        BoardColor = new Color4(0.10f, 0.10f, 0.12f, 1f);
        ViewOffsetY = 0f;                    // 屏幕坐标 == 画布坐标，抓屏和算位置都好对
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        CurrentColor = new Color4(1f, 0f, 1f, 1f);   // 品红：几条像素判据要靠它数
        Doc.Clear();
        Doc.ClearHistory();

        Vector2 Pt(InkEngine.InkPoint p) => new Vector2(p.X, p.Y);

        // ================= A. 四种工具各拖一次 =================
        Console.WriteLine("  -- A. 画图入口（四种工具，真机输入）--");
        var tools = new (Tool tool, StrokeKind kind)[]
        {
            (Tool.Line, StrokeKind.Line), (Tool.Rectangle, StrokeKind.Rectangle),
            (Tool.Ellipse, StrokeKind.Ellipse), (Tool.Arrow, StrokeKind.Arrow),
        };
        foreach (var (tool, kind) in tools)
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(tool);

            // 方向刻意取 α ≈ 22.8°：离软吸附那几条（0/30/45/60/90）都超过 3°，
            // 于是"松手点"就是最终端点，判据才干净。想验吸附在 C 段。
            float ax = _virtualX + 600, ay = _virtualY + 900;
            float bx = ax + 380, by = ay - 160;

            SendMouse((int)ax, (int)ay, 0);                             SettleFrames(60);
            SendMouse((int)ax, (int)ay, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(60);
            for (int i = 1; i <= 5; i++)
            {
                SendMouse((int)(ax + (bx - ax) * i / 5f), (int)(ay + (by - ay) * i / 5f), 0);
                SettleFrames(25);
            }
            SendMouse((int)bx, (int)by, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(220);

            string tag = tool.ToString();
            var s = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check($"{tag}：拖出一个对象", s != null, $"对象数 {Doc.Strokes.Count}");
            if (s == null) continue;

            Check($"{tag}：Kind 正确", s.Kind == kind, $"Kind={s.Kind}（期望 {kind}）");
            Check($"{tag}：控制点只有两个（图形由两端定义）", s.Points.Count == 2, $"点数 {s.Points.Count}");
            Check($"{tag}：起点落在按下点",
                  Vector2.Distance(Pt(s.Points[0]), new Vector2(ax, ay)) < 2f,
                  $"起点 ({Pt(s.Points[0]).X:F0},{Pt(s.Points[0]).Y:F0}) 期望 ({ax:F0},{ay:F0})");
            Check($"{tag}：终点落在松手点",
                  Vector2.Distance(Pt(s.Points[^1]), new Vector2(bx, by)) < 2f,
                  $"终点 ({Pt(s.Points[^1]).X:F0},{Pt(s.Points[^1]).Y:F0}) 期望 ({bx:F0},{by:F0})");
            Check($"{tag}：一次拖拽 = 一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");
            if (tool == Tool.Line)
            {
                // 画出来还得真的**上屏**（预览与提交后的几何是同一条，不该有跳变）
                var mid = new Vector2((ax + bx) * 0.5f, (ay + by) * 0.5f);
                int onScreen = ScreenProbe.CountMagenta((int)mid.X - 30, (int)mid.Y - 30, 60, 60);
                Check($"{tag}：画出来的线真的上了屏", onScreen > 100, $"线上取一块 {onScreen} 像素");
            }
            Doc.Undo();
            SettleFrames(150);
            Check($"{tag}：撤销一次就干净", Doc.Strokes.Count == 0 && Doc.UndoDepth == 0,
                  $"撤销后对象 {Doc.Strokes.Count} 个，撤销栈 {Doc.UndoDepth} 步");
        }

        // ================= A2. 画完自动选中 ＋ 那个框只"收起来"，别的都不变 =================
        //
        // 用户 2026-09-22 定的三条：
        //   ① 图形一成型就**自动出现选中框**（"方便调整和拖动"）；
        //   ② "点击了其他地方，这个选中框就取消"——取消完这一笔还得**照常画得出来**
        //     （计划-图形工具.md 第 8 条："紧接着落笔不被遮罩吃掉"）；
        //   ③ 上手之后补的：**刚画完把操作条收起来（一颗圆钮）**，点一下才摊开成整条——
        //     但"**只要收缩，其他的都不变**"（拖动 / 拉手柄 / 点条一律和框选工具下一样）。
        //     原话后半句是他抓出来的 bug："它好像不能拖动位置，只能拉伸缩放"。
        //
        // 下面按顺序真拖一遍（走真机输入，不直接调内部函数）。
        Console.WriteLine("  -- A2. 画完自动选中：收起那颗圆钮 / 框里照常能拖 / 框外接着画 --");
        Doc.Clear();
        Doc.ClearHistory();
        CurrentColor = new Color4(1f, 0f, 1f, 1f);      // 品红：屏幕判据靠它数
        SetToolFromUi(Tool.Rectangle);

        void DragPhys(float x0, float y0, float x1, float y1, int holdFrames = 200)
        {
            SendMouse((int)x0, (int)y0, 0);                                 SettleFrames(60);
            SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);       SettleFrames(60);
            SendMouse((int)((x0 + x1) * 0.5f), (int)((y0 + y1) * 0.5f), 0); SettleFrames(40);
            SendMouse((int)x1, (int)y1, 0);                                 SettleFrames(40);
            SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);         SettleFrames(holdFrames);
        }

        void ClickPhys(float x, float y)
        {
            SendMouse((int)x, (int)y, 0);                            SettleFrames(80);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(320);
        }

        float a2x = _virtualX + 500, a2y = _virtualY + 700;      // 第 1 个矩形的左上角
        const float a2w = 420f, a2h = 300f;
        DragPhys(a2x, a2y, a2x + a2w, a2y + a2h);

        var r1 = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
        Check("A2：拖出矩形成型", r1 != null && r1.Kind == StrokeKind.Rectangle,
              $"对象 {Doc.Strokes.Count} 个，Kind {(r1 == null ? "（一个都没有）" : r1.Kind.ToString())}");
        Check("A2：成型那一刻它就**自动选中**了",
              r1 != null && Doc.Selected.Count == 1 && Doc.Selected[0] == r1,
              $"选中 {Doc.Selected.Count} 个");
        // 工具**不换成框选**：手里还是矩形，"接着画下一个"才是常态（换工具会把上带也掰走）。
        Check("A2：工具还是矩形（没被换成框选）",
              Host.State.Tool == Tool.Rectangle, $"当前工具 {Host.State.Tool}");
        // **刚画完是"收起来"那一档**：只挂一颗圆钮，不是一整条九格（用户上手之后定的）。
        Check("A2：刚画完那个框是**收起来**的形态（一颗圆钮，不是一整条）",
              AutoSelCollapsed && BarDrawnCollapsed, $"AutoSelCollapsed={AutoSelCollapsed}");

        // 框＋手柄真的画到屏幕上了：数选中框上边那条线（强调色）的像素。
        // 只判模型的话，"选中了、但框没上屏"这种毛病（脏区没盖住）看不出来。
        var r1Box = r1.WorldInkBounds;
        {
            float probeX = (r1Box.MinX + r1Box.MaxX) * 0.5f - 30f;
            float probeY = r1Box.MinY - 10f;
            int boxPix = ScreenProbe.CountNear((int)probeX, (int)probeY, 60, 26, 0, 120, 212, 60);
            Check("A2：选中框真的上了屏（框上边那一段的强调色像素）", boxPix > 150,
                  $"框上边取一块 {boxPix} 像素");
        }

        // ② **按在框里那块空白**（不在墨上）：该是"拖它走"——和框选工具下**一模一样**。
        //    用户 2026-09-22 上手报的就是这一条："它好像不能拖动位置，只能拉伸缩放"。
        //    从框的正中间开始拖：那儿离八个手柄都远（这个框 420×300，离最近的手柄也有 150 像素
        //    以上，而手柄命中半径是 14 逻辑像素），而且**不在墨上**（矩形正中间是空的）——
        //    所以除了"框里能拖"这一条，没有别的路能走通。
        float inX = (r1Box.MinX + r1Box.MaxX) * 0.5f, inY = (r1Box.MinY + r1Box.MaxY) * 0.5f;
        DragPhys(inX, inY, inX + 140f, inY + 60f);
        Check("A2：按在框里＝拖它走（不是在框里又画一个）", Doc.Strokes.Count == 1,
              $"对象 {Doc.Strokes.Count} 个（期望 1）");
        var moved1 = r1.WorldInkBounds;
        float dx1 = (moved1.MinX + moved1.MaxX) * 0.5f - (r1Box.MinX + r1Box.MaxX) * 0.5f;
        Check("A2：它真的跟着挪了（整体平移，形状没被拉变形）",
              MathF.Abs(dx1 - 140f) < 8f
              && MathF.Abs((moved1.MaxX - moved1.MinX) - (r1Box.MaxX - r1Box.MinX)) < 2f,
              $"框心横移 {dx1:F0}（期望 140），宽 {r1Box.MaxX - r1Box.MinX:F0} → {moved1.MaxX - moved1.MinX:F0}");
        Doc.Undo();
        SettleFrames(150);
        Check("A2：这一次拖动是一步撤销",
              Doc.Strokes.Count == 1 && MathF.Abs(r1.WorldInkBounds.MinX - r1Box.MinX) < 1f,
              $"撤销后对象 {Doc.Strokes.Count} 个，框左上角 {r1.WorldInkBounds.MinX:F0}（期望 {r1Box.MinX:F0}）");

        // ③ 按在**框外**（这里正好是"原来那条操作条的地盘"）：该是"收起这个框、这一笔照常画"
        //    ——用户 2026-09-22 定的口径："点击了其他地方，这个选中框就取消"。
        //    ⚠ 这一条同时守着 `--shapebandtest` A3 段踩出来的那个坑：整条操作条原来就挂在
        //    图形正下方，老师想在下面接着画时会被它吃掉（跑出来的是"点了条上的某一格"）。
        //    现在刚画完只挂一颗圆钮，整条要**点开圆钮**才出来，所以那片地方是空的。
        {
            var bar2 = SelectionHandles.BarRect(r1Box, DpiScale, ViewportCanvas);
            // 圆钮在**框的正下方、居中**（`BarCollapsedRect`），所以从条的**左端**按下去
            // ——那头离圆钮 150 像素以上，测的就是"整条不在"这件事本身。
            float bx2 = bar2.MinX + 30f, by2 = (bar2.MinY + bar2.MaxY) * 0.5f;
            Check("A2：那一刻那颗圆钮确实离这儿很远（不然这条测的是圆钮）",
                  Math.Abs(bx2 - (bar2.MinX + bar2.MaxX) * 0.5f) > 100f,
                  $"按点 x={bx2:F0}，圆钮中心 x={(bar2.MinX + bar2.MaxX) * 0.5f:F0}");
            DragPhys(bx2, by2, bx2 + 300f, by2 + 200f);
            Check("A2：按在框外＝收起框、接着画（整条没摊开、也没被它吃掉）",
                  Doc.Strokes.Count == 2,
                  $"对象 {Doc.Strokes.Count} 个（期望 2：第 1 个 ＋ 框外新画的）");
            var r1BoxNow = Doc.Strokes[0].WorldInkBounds;
            Check("A2：第 1 个矩形**没被拖走**",
                  MathF.Abs(r1BoxNow.MinX - r1Box.MinX) < 1f && MathF.Abs(r1BoxNow.MinY - r1Box.MinY) < 1f,
                  $"左上角 ({r1Box.MinX:F0},{r1Box.MinY:F0}) → ({r1BoxNow.MinX:F0},{r1BoxNow.MinY:F0})");
            Check("A2：新画的这个又被自动选中、又是收起那一态",
                  Doc.Selected.Count == 1 && Doc.Selected[0] == Doc.Strokes[1] && AutoSelCollapsed,
                  $"选中 {Doc.Selected.Count} 个，AutoSelCollapsed={AutoSelCollapsed}");
        }

        // ④ **点一下那颗圆钮** → 摊开成整条操作条（行为一个字都不变，只是"条"长得完整了）。
        {
            var f = SelectionHandles.FrameOf(Doc.Selected);
            var dot = SelectionHandles.BarCollapsedRect(f.CanvasAabb, DpiScale, ViewportCanvas);
            ClickPhys((dot.MinX + dot.MaxX) * 0.5f, (dot.MinY + dot.MaxY) * 0.5f);
            Check("A2：点一下圆钮 → 摊开成整条（不再是收起的）",
                  !AutoSelCollapsed && !BarDrawnCollapsed && SelectionBarShown,
                  $"AutoSelCollapsed={AutoSelCollapsed}，SelectionBarShown={SelectionBarShown}");
        }

        // ⑤ 每个新框都从**收起**开始；不点圆钮、直接点「选择」工具也一样摊开
        //    （`SwitchTool` 对"换到框选"本来就不清选区，那儿顺手把"刚画完"这一档也清掉）。
        {
            Doc.Selected.Clear();                            // 清干净，下一步那一笔落点要确定
            SettleFrames(60);
            // 落在**第 1 个矩形右边那块空地**上：离前面两个框都远，按下会不会被谁挡住没有歧义。
            DragPhys(a2x + 520f, a2y + 40f, a2x + 780f, a2y + 220f);   // 再画一个，让它自动选中
            Check("A2：新画的这个又是「收起来」那一态（每个新框都从收起开始）",
                  Doc.Strokes.Count == 3 && AutoSelCollapsed && Doc.Selected.Count == 1,
                  $"对象 {Doc.Strokes.Count} 个，AutoSelCollapsed={AutoSelCollapsed}");
            SetToolFromUi(Tool.Marquee);
            SettleFrames(120);
            Check("A2：点一下「选择」工具 → 选区还在、整条操作条出来（不点圆钮也行）",
                  Doc.Selected.Count == 1 && SelectionBarShown && !BarDrawnCollapsed,
                  $"选中 {Doc.Selected.Count} 个，SelectionBarShown={SelectionBarShown}");
        }

        // ================= B. 拖动太短不产生对象 =================
        Console.WriteLine("  -- B. 拖动太短（误点）不产生对象 --");
        Doc.Clear();
        Doc.ClearHistory();
        SetToolFromUi(Tool.Line);
        CurrentColor = new Color4(1f, 0f, 1f, 1f);      // 品红：屏幕上好不好数
        float px = _virtualX + 700, py = _virtualY + 1100;

        SendMouse((int)px, (int)py, 0);                             SettleFrames(60);
        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(60);
        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTUP);     SettleFrames(200);
        Check("点一下不产生对象", Doc.Strokes.Count == 0 && Doc.UndoDepth == 0,
              $"对象 {Doc.Strokes.Count} 个，撤销 {Doc.UndoDepth} 步（都不该有）");

        // 2 物理像素：100% 缩放下是 2 逻辑像素，200% 下是 1 —— 两种情况都短于 4
        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(60);
        SendMouse((int)px + 2, (int)py + 2, 0);                     SettleFrames(100);
        SendMouse((int)px + 2, (int)py + 2, Native.MOUSEEVENTF_LEFTUP); SettleFrames(200);
        Check("挪 2 物理像素也不产生对象（短于 4 逻辑像素）",
              Doc.Strokes.Count == 0 && Doc.UndoDepth == 0,
              $"对象 {Doc.Strokes.Count} 个，撤销 {Doc.UndoDepth} 步（都不该有）");

        // 连"屏幕上有没有留下杂物"一起验：拖动预览是每帧画在浮动层上的，
        // 不提交的时候必须连预览一起消失（这里数的是屏幕像素，不是模型）。
        int residue = ScreenProbe.CountMagenta((int)px - 60, (int)py - 60, 120, 120);
        Check("拖动太短：屏幕上也没留下墨（不许留杂物）", residue == 0, $"{residue} 像素");

        // ================= C. 吸附（数学层）=================
        Console.WriteLine("  -- C. 画线吸附与倾斜角（数学层）--");
        var origin = new Vector2(1000f, 1000f);
        // 目标点：从 origin 出发、方向角 α = deg、长度 len。
        // （α 的定义就是本段要验的东西，所以下面另有一组**写死坐标**的读数判据。）
        Vector2 At(float deg, float len)
            => new Vector2(origin.X + len * MathF.Cos(-deg * MathF.PI / 180f),
                           origin.Y + len * MathF.Sin(-deg * MathF.PI / 180f));

        void SnapCase(string name, float target, float want, bool wantSnap,
                      bool shift = false, bool alt = false)
        {
            var e = SelectionHandles.SnapEndPoint(origin, At(target, 300f), shift, alt, out bool snapped);
            float got = SelectionHandles.InclinationDegrees(origin, e);
            float len = Vector2.Distance(origin, e);
            bool ok = Math.Abs(got - want) < 0.05f && snapped == wantSnap && Math.Abs(len - 300f) < 0.05f;
            Check(name, ok, $"{target:F1}° → {got:F1}°{(snapped ? "（吸附）" : "（自由）")}"
                          + $"，长度 {len:F1}（期望 300）"
                          + $"，期望 {want:F1}°{(wantSnap ? "（吸附）" : "（自由）")}");
        }

        // 容差 2026-09-18 从 ±3° 收紧到 ±1°，所以"31.5° 吸到 30°"这类旧判据已经不对了：
        // 现在要 30.5° 才吸、31.5° 必须**不吸**（用户就是要能画出 26.5°/31.5° 这种角）。
        SnapCase("软吸附：正对 45°", 45f, 45f, true);
        SnapCase("软吸附：容差内吸到 30°（30.5°）", 30.5f, 30f, true);
        SnapCase("软吸附：容差边界内（30.9°）", 30.9f, 30f, true);
        SnapCase("软吸附：容差边界外（31.1° 不吸）", 31.1f, 31.1f, false);
        SnapCase("软吸附：旧容差下会吸、现在不吸（31.5°）", 31.5f, 31.5f, false);
        SnapCase("软吸附：容差内吸到 0°（水平）", 0.5f, 0f, true);
        // 0° 与 180° 是同一条线：判距离必须是**环形**的（这条以前是错的，179.5° 吸不到 0°）
        SnapCase("软吸附：179.5° 吸到 180≡0°（环形距离）", 179.5f, 0f, true);
        SnapCase("软吸附：容差内吸到 60°", 60.5f, 60f, true);
        SnapCase("软吸附：容差内吸到 90°（竖直）", 89.5f, 90f, true);
        SnapCase("软吸附：容差内吸到 120°（新增）", 120.5f, 120f, true);
        SnapCase("软吸附：容差内吸到 135°（新增）", 135.5f, 135f, true);
        SnapCase("软吸附：容差内吸到 150°（新增）", 150.5f, 150f, true);
        SnapCase("软吸附：容差外保持自由（25°）", 25f, 25f, false);
        SnapCase("软吸附：容差外保持自由（38°）", 38f, 38f, false);
        SnapCase("Shift：15° 硬网格（37° → 30°）", 37f, 30f, true, shift: true);
        SnapCase("Shift：吸到 60°", 58f, 60f, true, shift: true);
        SnapCase("Shift：15° 网格照旧覆盖特殊角（92° → 90°）", 92f, 90f, true, shift: true);
        SnapCase("Alt：完全自由（44° 不吸 45°）", 44f, 44f, false, alt: true);
        SnapCase("Shift+Alt：Shift 优先", 37f, 30f, true, shift: true, alt: true);

        // 环形距离/最短角差是这一轮新加的，单独钉两条（旋转吸附要用它把"吸到的角"
        // 换算成"这一拖还得转多少度"）。
        Check("环形距离：179.5° 离 0° 是 0.5°（不是 179.5°）",
              Math.Abs(SelectionHandles.InclinationDistance(179.5f, 0f) - 0.5f) < 1e-3f,
              $"算出 {SelectionHandles.InclinationDistance(179.5f, 0f):F3}°");
        Check("最短角差：从 179.5° 到 0° 是 +0.5°（不是 -179.5°）",
              Math.Abs(SelectionHandles.InclinationShortestDelta(179.5f, 0f) - 0.5f) < 1e-3f,
              $"算出 {SelectionHandles.InclinationShortestDelta(179.5f, 0f):F3}°");

        // ---- 展开角（可以超过 180°、也可以是负数）的吸附：特殊角按 180° 周期 ----
        // 单选直线拖旋转柄读的就是这种数（用户 2026-09-18 澄清："可以无限转下去"）。
        // 这里的判据只看"吸完落在哪个数上"——不能折回 [0,180)，否则 405 会被印成 45。
        void ExpSnapCase(string name, float deg, float want, bool wantSnap,
                         bool shift = false, bool alt = false)
        {
            float got = SelectionHandles.SnapExpandedInclinationDegrees(deg, shift, alt, out bool snapped);
            Check(name, Math.Abs(got - want) < 0.01f && snapped == wantSnap,
                  $"{deg:F1}° → {got:F1}°{(snapped ? "（吸附）" : "（自由）")}"
                  + $"，期望 {want:F1}°{(wantSnap ? "（吸附）" : "（自由）")}");
        }
        ExpSnapCase("展开吸附：405° 就是特殊角（45° + 一整圈）", 405f, 405f, true);
        ExpSnapCase("展开吸附：404.5° 吸到 405°（±1° 内）", 404.5f, 405f, true);
        ExpSnapCase("展开吸附：406.5° 不吸（±1° 外）", 406.5f, 406.5f, false);
        ExpSnapCase("展开吸附：765.4° 吸到 765°（45° + 两圈）", 765.4f, 765f, true);
        ExpSnapCase("展开吸附：−135.4° 吸到 −135°（负方向那一圈）", -135.4f, -135f, true);
        ExpSnapCase("展开吸附：−180.6° 吸到 −180°（0° 的负方向）", -180.6f, -180f, true);
        ExpSnapCase("展开吸附：−355° 保持自由（45° − 400°，离特殊角 5°）", -355f, -355f, false);
        ExpSnapCase("展开吸附：Shift 的 15° 网格也按 180° 周期（407° → 405°）", 407f, 405f, true, shift: true);
        ExpSnapCase("展开吸附：Alt 完全自由（405.5° 不吸）", 405.5f, 405.5f, false, alt: true);
        Check("展开角文案：405° / −135° 都是纯数字一位小数（不带 α =）",
              SelectionHandles.FormatSignedDegrees(405f) == "405.0°"
              && SelectionHandles.FormatSignedDegrees(-135.4f) == "-135.4°"
              && SelectionHandles.FormatSignedDegrees(45f) == "45.0°"
              && SelectionHandles.FormatSignedDegrees(-0.02f) == "0.0°",
              $"405° → \"{SelectionHandles.FormatSignedDegrees(405f)}\"，"
              + $"−135.4° → \"{SelectionHandles.FormatSignedDegrees(-135.4f)}\"，"
              + $"−0.02° → \"{SelectionHandles.FormatSignedDegrees(-0.02f)}\"");

        // α 的五条读数：**写死坐标**，不借上面的 At（否则等于自己证自己）。
        void InclCase(string name, Vector2 a, Vector2 b, float want)
        {
            float got = SelectionHandles.InclinationDegrees(a, b);
            Check(name, Math.Abs(got - want) < 0.05f, $"{got:F2}°（期望 {want:F2}°）");
        }
        InclCase("α：水平 = 0°", new Vector2(0, 0), new Vector2(300, 0), 0f);
        InclCase("α：斜率 0.5 上升 = 26.57°", new Vector2(0, 0), new Vector2(200, -100), 26.565f);
        InclCase("α：竖直 = 90°", new Vector2(0, 0), new Vector2(0, 300), 90f);
        InclCase("α：斜率 -1 下降 = 135°", new Vector2(0, 0), new Vector2(300, 300), 135f);
        InclCase("α：差 1° 到水平 = 179°", new Vector2(0, 0), new Vector2(-300, -5.2365f), 179f);
        Check("α：反向量是同一条线（读数不变）",
              Math.Abs(SelectionHandles.InclinationDegrees(new Vector2(300, 300), new Vector2(0, 0)) - 135f) < 0.05f,
              "180° 与 0° 是同一条水平线，读数必须折在 [0,180)");
        Check("α：文案格式", SelectionHandles.FormatInclination(30.04f) == "α = 30.0°",
              $"30.04° → \"{SelectionHandles.FormatInclination(30.04f)}\"");
        Check("α：水平线不会印成 -0.0（α 按定义非负）",
              SelectionHandles.FormatInclination(
                  SelectionHandles.InclinationDegrees(new Vector2(0, 0), new Vector2(300, 0))) == "α = 0.0°",
              $"水平线 → \"{SelectionHandles.FormatInclination(SelectionHandles.InclinationDegrees(new Vector2(0, 0), new Vector2(300, 0)))}\"");

        // ================= D. 拖端点（真机层）=================
        Console.WriteLine("  -- D. 拖端点（快路 / 读数 / 撤销）--");
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        float lx = _virtualX + 700, ly = _virtualY + 1000;
        var line = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
        };
        line.AddPoint(lx, ly, 1f, 0);             // 水平：α = 0°
        line.AddPoint(lx + 600f, ly, 1f, 0);
        Doc.AddStroke(line);
        Doc.SelectOnly(new[] { line });
        SettleFrames(300);

        var pA = SelectionHandles.EndpointCanvasPosition(line, 0);
        var pB = SelectionHandles.EndpointCanvasPosition(line, 1);
        int revBefore = line.Revision;
        int undoBefore = Doc.UndoDepth;          // 加这条线本身也算一步，所以比"净增 1"

        // 目标：绕 pB 转、长度 620、**原始**方向 30.5°（容差 ±1° 内 → 该吸到 30°）。
        // 屏幕方向角 θ = 149.5°（左偏下）——拖的是左边那个端点，不能把它甩到右边去。
        const float wantLen = 620f;
        float theta = 149.5f * MathF.PI / 180f;
        var raw = new Vector2(pB.X + wantLen * MathF.Cos(theta), pB.Y + wantLen * MathF.Sin(theta));

        bool took = SelectionGestureForTest(pA.X, pA.Y);
        Check("按在端点手柄上被接住（不是整体拖动）",
              took && VertexDragging, $"接住={took}，VertexDragging={VertexDragging}");

        SettleFrames(60);
        int patchAtBegin = _windows[0].LastPatchCount;   // 起手那一下要重画一次（把这条从内容层摘掉）
        UpdateSelectionGestureForTest(raw.X, raw.Y);
        SettleFrames(90);

        Check("拖动中：模型一个字没改（走的是快路）",
              Math.Abs(line.Points[0].X - lx) < 0.01f && Math.Abs(line.Points[1].X - (lx + 600f)) < 0.01f
              && line.Revision == revBefore,
              $"端点 ({line.Points[0].X:F1},{line.Points[0].Y:F1})，Revision {revBefore} → {line.Revision}");
        Check("拖动中：内容层一帧都不重画（方案 B）",
              _windows[0].LastPatchCount == 0,
              $"上一帧光栅化分块 {_windows[0].LastPatchCount} 块（起手那一下是 {patchAtBegin} 块）");
        // 旧位置那一段（离固定端很远，ghost 根本没走到那儿）**不许还有墨**：
        // 拖端点复用了"把这条从内容层摘出去"的机制，于是"拖动预览"这条路也可能顺手把它
        // 按单位矩阵再画一遍——屏幕上就成了"旧线不动 + 新线跟着指针"两条线（2026-09-18 抓到）。
        int staleInk = ScreenProbe.CountMagenta((int)(lx - 30f), (int)(ly - 30f), 230, 60);
        Check("拖动中：旧位置不许还留着那条线（预览与临时几何只能画一个）",
              staleInk == 0, $"{staleInk} 像素（这一段在临时几何之外，正确时应为 0）");
        Check("拖动中：α 被吸到 30°（软吸附）",
              VertexInclinationSnapped && VertexReadout == VertexReadoutKind.Inclination
              && Math.Abs(VertexReadoutValue - 30f) < 0.5f,
              $"α = {VertexReadoutValue:F1}°，吸住={VertexInclinationSnapped}");

        var anchor = VertexPreviewCanvasPoint;
        int labelPixels = ScreenProbe.CountNear(
            (int)(anchor.X - 70f * DpiScale), (int)(anchor.Y - 60f * DpiScale),
            (int)(140f * DpiScale), (int)(60f * DpiScale), 0, 120, 212, 40);
        // 两条边界一起判：太少 = 标签没画出来；太多 = 画得比胶囊还大
        // （字号用错单位就会这样：Dpi=192 拿去当缩放倍数，字会糊满整个画面）。
        Check("拖动中：倾斜角读数真的上屏（吸住 → 强调色胶囊）",
              labelPixels > 1200 && labelPixels < 20000,
              $"{labelPixels} 像素（探针窗一半面积是 {140 * DpiScale * 60 * DpiScale / 2:F0}）");

        EndSelectionGestureForTest();
        SettleFrames(250);

        Check("松手后：读数标签消失（不再拖端点）", !VertexDragging, $"VertexDragging={VertexDragging}");
        var pA2 = SelectionHandles.EndpointCanvasPosition(line, 0);
        float len2 = Vector2.Distance(pB, pA2);
        Check("松手后：长度保持 620（吸附只改方向、不改长短）",
              Math.Abs(len2 - wantLen) < 2f, $"长度 {len2:F1}（期望 {wantLen:F0}）");
        Check("松手后：倾斜角 ≈ 30°",
              Math.Abs(SelectionHandles.InclinationDegrees(pB, pA2) - 30f) < 0.5f,
              $"α = {SelectionHandles.InclinationDegrees(pB, pA2):F2}°");
        Check("松手后：另一个端点没动",
              Vector2.Distance(SelectionHandles.EndpointCanvasPosition(line, 1), pB) < 0.5f,
              $"另一端点 {Vector2.Distance(SelectionHandles.EndpointCanvasPosition(line, 1), pB):F2}px");
        Check("松手后：Revision 变了（几何缓存真的失效）",
              line.Revision > revBefore, $"Revision {revBefore} → {line.Revision}");
        Check("松手后：一次拖拽只多了**一步**撤销（净增 1）",
              Doc.UndoDepth == undoBefore + 1,
              $"撤销栈 {undoBefore} → {Doc.UndoDepth} 步（拖动中的预览不该进撤销栈）");

        Doc.Undo();
        SettleFrames(200);
        Check("撤销后：端点回到原位",
              Vector2.Distance(SelectionHandles.EndpointCanvasPosition(line, 0), pA) < 0.5f,
              $"端点 {SelectionHandles.EndpointCanvasPosition(line, 0)} 期望 {pA}");

        // ---- D2. 只点一下端点手柄（不移动）：线不许从屏幕上消失 ----
        //
        // 这条防的是快路的一个副作用：起手那一下这条线已经从内容层摘出去了，
        // 一点没动的时候如果不再标一次脏，屏幕上那块就永远是"没有这条线"。
        Doc.Clear();
        Doc.ClearHistory();
        var tap = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
        };
        tap.AddPoint(lx, ly, 1f, 0);
        tap.AddPoint(lx + 600f, ly, 1f, 0);
        Doc.AddStroke(tap);
        Doc.SelectOnly(new[] { tap });
        SettleFrames(300);
        var tapBox = tap.PaddedBounds.Inflate(6f);
        int inkBefore = ScreenProbe.CountMagenta((int)tapBox.MinX, (int)tapBox.MinY,
                                                 (int)(tapBox.MaxX - tapBox.MinX),
                                                 (int)(tapBox.MaxY - tapBox.MinY));
        var tapA = SelectionHandles.EndpointCanvasPosition(tap, 0);
        bool tookTap = SelectionGestureForTest(tapA.X, tapA.Y);
        SettleFrames(120);
        EndSelectionGestureForTest();
        SettleFrames(250);
        int inkAfter = ScreenProbe.CountMagenta((int)tapBox.MinX, (int)tapBox.MinY,
                                                (int)(tapBox.MaxX - tapBox.MinX),
                                                (int)(tapBox.MaxY - tapBox.MinY));
        Check("点一下端点手柄（不移动）：线还在屏幕上、也没多一条撤销",
              tookTap && !VertexDragging && inkAfter > inkBefore / 2 && Doc.UndoDepth == 1,
              $"接住={tookTap}，墨 {inkBefore} → {inkAfter} 像素，撤销栈 {Doc.UndoDepth} 步");

        // ---- D3. 拖端点中的框：与静止态**同一口径**（每边不再多 2 像素）----
        //
        // 这一处只差 2 像素，肉眼看不出来，所以**只看数字**（用户 2026-09-19 要求）。
        // 用一条**转过 30° 的斜线**：旧口径（把整体框矩形转过去再取外接）会虚胖一大圈，
        // 两种口径的差别在这里才看得清（用水平长线的话差 6 像素，等于没验到）。
        Console.WriteLine("  -- D3. 拖端点中的框：与静止态同一口径 --");
        {
            bool SameBox(in RectF a, in RectF b, float tol = 0.01f)
                => MathF.Abs(a.MinX - b.MinX) < tol && MathF.Abs(a.MinY - b.MinY) < tol
                && MathF.Abs(a.MaxX - b.MaxX) < tol && MathF.Abs(a.MaxY - b.MaxY) < tol;
            string Size(in RectF r) => $"{r.MaxX - r.MinX:F0}×{r.MaxY - r.MinY:F0}";

            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var vb = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            vb.AddPoint(lx, ly, 1f, 0);                        // 45° 斜线
            vb.AddPoint(lx + 600f, ly - 600f, 1f, 0);
            vb.Transform = SelectionHandles.RotateMatrix(30f, new Vector2(lx + 300f, ly - 300f));
            Doc.AddStroke(vb);
            Doc.SelectOnly(new[] { vb });
            SettleFrames(300);

            var vb0 = SelectionHandles.EndpointCanvasPosition(vb, 0);
            var vb1 = SelectionHandles.EndpointCanvasPosition(vb, 1);
            var stillBox = SelectionHandles.FrameOf(Doc.Selected).Local;
            Check("D3：这条线是转过角度的（α = 45° + 30° = 75°）",
                  MathF.Abs(SelectionHandles.InclinationDegrees(vb0, vb1) - 75f) < 0.5f,
                  $"α = {SelectionHandles.InclinationDegrees(vb0, vb1):F2}°，静止框 {Size(stillBox)}");

            // ① 按在端点上但**不移动**：框必须和静止态逐边相等（改前这里每边多 2 像素）
            bool took3 = SelectionGestureForTest(vb0.X, vb0.Y);
            SettleFrames(60);
            var pressBox = LiveSelectionFrame.Local;
            Check("D3：按下不移动时，框与静止态逐边相等（每边不再多 2 像素）",
                  took3 && VertexDragging && SameBox(pressBox, stillBox),
                  $"按下 {Size(pressBox)} / 静止 {Size(stillBox)}"
                  + $"（旧口径会是 {Size(stillBox)} + 4 那种）");

            // ② 拖到某处：框 = 按预览端点现算的墨迹框（端点外接 + 半笔宽），≤0.5 像素
            var target3 = new Vector2(vb1.X - 320f, vb1.Y + 430f);
            UpdateSelectionGestureForTest(target3.X, target3.Y);
            SettleFrames(80);
            var previewEnd = VertexPreviewCanvasPoint;
            var wantInk = RectF.Empty;
            wantInk.Add(previewEnd.X, previewEnd.Y);
            wantInk.Add(vb1.X, vb1.Y);
            wantInk = wantInk.Inflate(vb.Width * 0.5f);
            var dragBox = LiveSelectionFrame.Local;
            Check("D3：拖动中的框 = 按预览端点现算的墨迹框（≤0.5 像素）",
                  SameBox(dragBox, wantInk, 0.5f),
                  $"拖动 {Size(dragBox)} / 现算 {Size(wantInk)}"
                  + $"（脏区口径 PaddedBoundsOf 会是 {Size(Stroke.PaddedBoundsOf(VertexPreviewPoints, vb.Transform, vb.Width))}）");
            Check("D3：拖动中模型仍然没动（快路）",
                  Math.Abs(vb.Points[0].X - lx) < 0.01f && Math.Abs(vb.Points[1].Y - (ly - 600f)) < 0.01f,
                  $"模型端点 ({vb.Points[0].X:F0},{vb.Points[0].Y:F0})-({vb.Points[1].X:F0},{vb.Points[1].Y:F0})");

            // ③ 松手：框前后逐边相等（不再"缩一下"），提交的几何就是刚才预览的那个端点
            EndSelectionGestureForTest();
            SettleFrames(200);
            var afterBox = SelectionHandles.FrameOf(Doc.Selected).Local;
            Check("D3：松手前后框逐边相等（不再缩一下）",
                  SameBox(dragBox, afterBox, 0.5f),
                  $"松手前 {Size(dragBox)} / 松手后 {Size(afterBox)}");
            Check("D3：松手后提交的几何 = 刚才预览的端点（≤1 像素）",
                  Vector2.Distance(SelectionHandles.EndpointCanvasPosition(vb, 0), previewEnd) < 1f,
                  $"提交 {SelectionHandles.EndpointCanvasPosition(vb, 0)} / 预览 {previewEnd}");
        }

        // ================= E. 旋转过的直线：端点仍落在指针位置 =================
        Console.WriteLine("  -- E. 旋转过的直线拖端点（Transform⁻¹ 那条路）--");
        Doc.Clear();
        Doc.ClearHistory();
        var rot = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
        };
        rot.AddPoint(lx, ly, 1f, 0);
        rot.AddPoint(lx + 600f, ly + 300f, 1f, 0);
        var rotCenter = new Vector2(lx + 300f, ly + 150f);
        rot.Transform = Matrix3x2.CreateRotation(-37f * MathF.PI / 180f, rotCenter);
        Doc.AddStroke(rot);
        Doc.SelectOnly(new[] { rot });
        SettleFrames(300);

        var eA = SelectionHandles.EndpointCanvasPosition(rot, 0);
        var eB = SelectionHandles.EndpointCanvasPosition(rot, 1);
        // 原始方向 17°：离最近的吸附方向（0° / 30°）都超过 1°（现在的容差），
        // 所以预览 = 指针，判据干净。
        float eTheta = -17f * MathF.PI / 180f;
        var rawE = new Vector2(eB.X + 520f * MathF.Cos(eTheta), eB.Y + 520f * MathF.Sin(eTheta));

        bool tookE = SelectionGestureForTest(eA.X, eA.Y);
        UpdateSelectionGestureForTest(rawE.X, rawE.Y);
        SettleFrames(90);
        Check("旋转过的直线：拖动中也就地预览（模型没动）",
              tookE && VertexDragging && Math.Abs(rot.Points[0].X - lx) < 0.01f,
              $"接住={tookE}，模型起点 ({rot.Points[0].X:F1},{rot.Points[0].Y:F1})");
        EndSelectionGestureForTest();
        SettleFrames(250);

        var eA2 = SelectionHandles.EndpointCanvasPosition(rot, 0);
        Check("旋转过的直线：拖完端点落在指针位置",
              Vector2.Distance(eA2, rawE) < 1.5f,
              $"端点 {eA2}，指针 {rawE}，差 {Vector2.Distance(eA2, rawE):F2}px");
        Check("旋转过的直线：另一端不动",
              Vector2.Distance(SelectionHandles.EndpointCanvasPosition(rot, 1), eB) < 0.5f,
              $"差 {Vector2.Distance(SelectionHandles.EndpointCanvasPosition(rot, 1), eB):F2}px");
        Doc.Undo();
        SettleFrames(150);
        Check("旋转过的直线：撤销回原位",
              Vector2.Distance(SelectionHandles.EndpointCanvasPosition(rot, 0), eA) < 0.5f,
              $"端点 {SelectionHandles.EndpointCanvasPosition(rot, 0)} 期望 {eA}");

        // ================= F. 画线过程中也显示 α（真机层）=================
        //
        // 用户 2026-09-18 四条之一："画直线的时候也要显示倾斜角 α"。
        // 这条要**数屏幕像素**（不能只判状态）：标签是画在浮动层上的，
        // 进没进每帧脏区、会不会被裁掉，只有看像素才知道。
        Console.WriteLine("  -- F. 画线过程中的 α 读数（真机层）--");
        Doc.Clear();
        Doc.ClearHistory();
        SetToolFromUi(Tool.Line);
        float dx0 = _virtualX + 700, dy0 = _virtualY + 1200;
        // 原始方向 45.5°（容差 ±1° 内 → 该吸到 45°）、长度 500
        float dTheta = -45.5f * MathF.PI / 180f;
        var dEnd = new Vector2(dx0 + 500f * MathF.Cos(dTheta), dy0 + 500f * MathF.Sin(dTheta));

        // 读数标签贴在"正在拖的那一端"上方（盒子高 30 逻辑 + 一段间距）：
        // 探针窗按它取，取小一点，别把线本身的像素算进来。
        int ShapeLabelPixels(Vector2 anchor)
            => ScreenProbe.CountNear((int)(anchor.X - 70f * DpiScale), (int)(anchor.Y - 60f * DpiScale),
                                     (int)(140f * DpiScale), (int)(60f * DpiScale), 0, 120, 212, 40);

        SendMouse((int)dx0, (int)dy0, 0);                             SettleFrames(60);
        SendMouse((int)dx0, (int)dy0, Native.MOUSEEVENTF_LEFTDOWN);   SettleFrames(60);
        SendMouse((int)dEnd.X, (int)dEnd.Y, 0);                       SettleFrames(200);

        Check("画线中：引擎报出「正在画有倾斜角的图形」并给了 α",
              ShapeInclinationActive && ShapeInclinationSnapped
              && Math.Abs(ShapeInclinationDegrees - 45f) < 0.5f,
              $"active={ShapeInclinationActive}，α = {ShapeInclinationDegrees:F1}°，吸住={ShapeInclinationSnapped}");
        Check("画线中：长度和 α 同源同报（2026-10-05 用户要的「长度」）",
              Math.Abs(ShapeLength - 500f) < 2f, $"长 {ShapeLength:F1}（期望 500）");
        int drawPixels = ShapeLabelPixels(ShapeInclinationAnchor);
        Check("画线中：α 读数真的上了屏（吸住 → 强调色胶囊）",
              drawPixels > 1200 && drawPixels < 20000,
              $"{drawPixels} 像素（探针窗一半面积是 {140 * DpiScale * 60 * DpiScale / 2:F0}）");

        // 再拖到 26.5°（离特殊角都超过 1° → 自由态）：读数该跟着变、胶囊该变回白底
        var dFree = new Vector2(dx0 + 500f * MathF.Cos(-26.565f * MathF.PI / 180f),
                                dy0 + 500f * MathF.Sin(-26.565f * MathF.PI / 180f));
        SendMouse((int)dFree.X, (int)dFree.Y, 0);                     SettleFrames(200);
        Check("画线中：拖到 26.5° 就是 26.5°（不再被吸到 30°）",
              !ShapeInclinationSnapped && Math.Abs(ShapeInclinationDegrees - 26.565f) < 0.5f,
              $"α = {ShapeInclinationDegrees:F2}°，吸住={ShapeInclinationSnapped}");
        int freeLabelPixels = ShapeLabelPixels(ShapeInclinationAnchor);
        Check("画线中：没吸住时胶囊是白底（强调色像素变少）",
              freeLabelPixels < drawPixels / 2, $"{drawPixels} → {freeLabelPixels} 像素");

        SendMouse((int)dFree.X, (int)dFree.Y, Native.MOUSEEVENTF_LEFTUP); SettleFrames(250);
        Check("松手后：画线读数消失、也没留下杂物（那条线还在）",
              !ShapeInclinationActive && ShapeLabelPixels(ShapeInclinationAnchor) < 800,
              $"active={ShapeInclinationActive}，探针里强调色 {ShapeLabelPixels(ShapeInclinationAnchor)} 像素");

        // ================= G. 单选直线拖旋转柄：读数是 α（不是 Δ）=================
        //
        // 用户 2026-09-18 的原话："那个旋转手柄使用直线水平为 0° 为依据，如果是这个直线初始
        // 是倾斜的，那它就要有对应的角度。按照高中数学，在 0~180° 里面，然后在这个度数上
        // 接着开始旋转。" —— 所以要判两件事：读数**接着它原有的 α** 变，并且**不是** Δ。
        Console.WriteLine("  -- G. 单选直线旋转读 α / 其它对象读 Δ（真机层）--");
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        // 一条初始就倾斜的直线：α = atan2(218, 600) ≈ 19.96°
        var spinLine = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
        };
        spinLine.AddPoint(lx, ly, 1f, 0);
        spinLine.AddPoint(lx + 600f, ly - 218f, 1f, 0);
        Doc.AddStroke(spinLine);
        Doc.SelectOnly(new[] { spinLine });
        SettleFrames(300);
        float a0 = SelectionHandles.InclinationDegrees(
            SelectionHandles.EndpointCanvasPosition(spinLine, 0),
            SelectionHandles.EndpointCanvasPosition(spinLine, 1));

        // 绕选区中心把旋转柄的指针**屏幕逆时针**拖若干步（每一步都是"从这个位置直接到那个位置"，
        // 所以累积角是精确的步进和；分步是为了能走出"一整圈"这种大角度）。
        void SpinSteps(float[] ccwSteps, out bool took)
        {
            var f = SelectionHandles.FrameOf(Doc.Selected);
            var pv = new Vector2((f.CanvasAabb.MinX + f.CanvasAabb.MaxX) * 0.5f,
                                 (f.CanvasAabb.MinY + f.CanvasAabb.MaxY) * 0.5f);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f, DpiScale);
            float arm = Vector2.Distance(grip, pv);
            float a = MathF.Atan2(grip.Y - pv.Y, grip.X - pv.X);
            took = SelectionGestureForTest(grip.X, grip.Y);
            float travel = 0f;
            foreach (float step in ccwSteps)
            {
                // 传进来的是**增量**：一路加着走，才能真的"转了一整圈"。
                travel += step;
                // 屏幕坐标里 y 朝下：角度**减小**才是视觉上的逆时针（和 --rotatetest 的 Spin 同一套）
                float rad = a - travel * MathF.PI / 180f;
                UpdateSelectionGestureForTest(pv.X + arm * MathF.Cos(rad), pv.Y + arm * MathF.Sin(rad));
                SettleFrames(70);
            }
        }

        void GrabRotate(float ccwDeg, out bool took) => SpinSteps(new[] { ccwDeg }, out took);

        GrabRotate(24.5f, out bool tookSpin);
        Check("单选直线：旋转中标记为「读 α₀+Δ」而不是 Δ",
              tookSpin && SelRotating && SelRotationReadsInclination,
              $"接住={tookSpin}，SelRotating={SelRotating}，读数走 α₀+Δ={SelRotationReadsInclination}");
        Check("单选直线：读数 = 这条线按下时的 α 接着转（19.96° + 24.5° → 吸到 45°）",
              Math.Abs(SelRotationInclination - 45f) < 0.1f,
              $"读数 {SelRotationInclination:F2}°（按下时 α={a0:F2}°），Δ 是 {SelRotationDegrees:F2}°");
        Check("单选直线：读数 = 它原有的 α 接着变（≈ 按下时 α + 转过的角），而且不是 Δ",
              Math.Abs(SelRotationInclination - (a0 + SelRotationDegrees)) < 0.2f
              && Math.Abs(SelRotationInclination - SelRotationDegrees) > a0 - 5f,
              $"按下时 α={a0:F2}°，Δ={SelRotationDegrees:F2}° → 读数 {SelRotationInclination:F2}°"
              + $"（要是读 Δ 就该是 {SelRotationDegrees:F2}°）");
        Check("单选直线：吸到特殊角（45°）", SelRotationSnapped, $"吸住={SelRotationSnapped}");

        // 屏幕上真的画了那颗胶囊（文案是 α 那一句，宽度和 Δ 那句不同）
        {
            var f = SelectionHandles.FrameOf(Doc.Selected);
            var grip = SelectionHandles.CanvasPosition(SelHandle.Rotate, f, DpiScale);
            int rotLabel = ScreenProbe.CountNear((int)(grip.X - 80f * DpiScale), (int)(grip.Y - 90f * DpiScale),
                                                 (int)(160f * DpiScale), (int)(80f * DpiScale), 0, 120, 212, 40);
            Check("单选直线：旋转标签真的上屏（吸住 → 强调色胶囊）",
                  rotLabel > 1200 && rotLabel < 20000, $"{rotLabel} 像素");
        }
        EndSelectionGestureForTest();
        SettleFrames(200);
        Check("松手：这次旋转提交成一步撤销，且直线真的转到 45° 附近",
              Doc.UndoDepth == 2 && Math.Abs(SelectionHandles.InclinationDegrees(
                  SelectionHandles.EndpointCanvasPosition(spinLine, 0),
                  SelectionHandles.EndpointCanvasPosition(spinLine, 1)) - 45f) < 0.5f,
              $"撤销栈 {Doc.UndoDepth} 步（1=加线 2=旋转），"
              + $"线现在是 {SelectionHandles.InclinationDegrees(SelectionHandles.EndpointCanvasPosition(spinLine, 0), SelectionHandles.EndpointCanvasPosition(spinLine, 1)):F2}°");
        Doc.Undo();
        SettleFrames(150);

        // ---- 「可以无限转下去」那一组（用户 2026-09-18 澄清）----
        //
        // 从一条 α₀ = 45° 的线起手：
        //   · 逆时针转一整圈 → 读数 405.0°（不是折回 45°）；
        //   · 顺时针转 400°  → 读数 −355.0°。
        // 中间还插一条"读数变化量 = 视觉转角"的核对（读数 135° 时线本身也真的在 135°，
        // 说明吸完的角同时用在矩阵和标签上）。
        float LineInclination(Stroke s2) => SelectionHandles.InclinationDegrees(
            SelectionHandles.EndpointCanvasPosition(s2, 0),
            SelectionHandles.EndpointCanvasPosition(s2, 1));

        // 拖动中"这条线现在是什么姿态"要问**合成后**的矩阵：方案 B 里模型到松手才动，
        // 实时姿态活在预览矩阵里（和 --rotatetest 的 Live() 同一个式子）。
        float LiveInclination(Stroke s2)
        {
            var m = DragPreviewActive ? s2.Transform * DragPreviewMatrix : s2.Transform;
            var a = Vector2.Transform(new Vector2(s2.Points[0].X, s2.Points[0].Y), m);
            var b = Vector2.Transform(new Vector2(s2.Points[^1].X, s2.Points[^1].Y), m);
            return SelectionHandles.InclinationDegrees(a, b);
        }

        Stroke MakeFortyFiveLine()
        {
            var s2 = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            s2.AddPoint(lx, ly, 1f, 0);
            s2.AddPoint(lx + 520f, ly - 520f, 1f, 0);     // α = 45°
            return s2;
        }

        Doc.Clear();
        Doc.ClearHistory();
        var fullTurn = MakeFortyFiveLine();
        Doc.AddStroke(fullTurn);
        Doc.SelectOnly(new[] { fullTurn });
        SettleFrames(300);
        Check("无上限组：起手这条线是 45°", Math.Abs(LineInclination(fullTurn) - 45f) < 0.01f,
              $"α₀ = {LineInclination(fullTurn):F2}°");

        var fTurn = SelectionHandles.FrameOf(Doc.Selected);
        var pvTurn = new Vector2((fTurn.CanvasAabb.MinX + fTurn.CanvasAabb.MaxX) * 0.5f,
                                 (fTurn.CanvasAabb.MinY + fTurn.CanvasAabb.MaxY) * 0.5f);
        var gripTurn = SelectionHandles.CanvasPosition(SelHandle.Rotate, fTurn, DpiScale);
        float armTurn = Vector2.Distance(gripTurn, pvTurn);
        float aTurn0 = MathF.Atan2(gripTurn.Y - pvTurn.Y, gripTurn.X - pvTurn.X);
        bool tookTurn = SelectionGestureForTest(gripTurn.X, gripTurn.Y);
        float travelTurn = 0f;
        void TurnStep(float ccw)
        {
            travelTurn += ccw;               // 正数 = 屏幕逆时针（rad = a0 − 转过的角）
            float rad = aTurn0 - travelTurn * MathF.PI / 180f;
            UpdateSelectionGestureForTest(pvTurn.X + armTurn * MathF.Cos(rad),
                                          pvTurn.Y + armTurn * MathF.Sin(rad));
            SettleFrames(70);
        }

        TurnStep(90f);
        Check("无上限组：逆时针 90° → 读数 45+90 = 135.0°（读数变化量 = 视觉转角）",
              tookTurn && Math.Abs(SelRotationInclination - 135f) < 0.5f
              && Math.Abs(LiveInclination(fullTurn) - 135f) < 0.5f,
              $"读数 {SelRotationInclination:F2}°，线的实时姿态 {LiveInclination(fullTurn):F2}°"
              + $"（模型此刻还没动，仍是 {LineInclination(fullTurn):F2}°）");
        TurnStep(90f);
        TurnStep(90f);
        TurnStep(90f);
        Check("无上限组：再转三步共**一整圈** → 读数 405.0°（不是 45.0°）",
              Math.Abs(SelRotationInclination - 405f) < 0.5f,
              $"读数 {SelRotationInclination:F2}°（折回的话会是 {SelRotationInclination - 360f:F2}° 那种小数字）");
        Check("无上限组：转到 405° 时仍然吸得住特殊角（180° 周期）",
              SelRotationSnapped, $"吸住={SelRotationSnapped}，读数 {SelRotationInclination:F2}°");
        EndSelectionGestureForTest();
        SettleFrames(200);
        Check("无上限组：松手后线真的转了一整圈（视觉上回到 45°）",
              Math.Abs(LineInclination(fullTurn) - 45f) < 0.5f,
              $"线现在 {LineInclination(fullTurn):F2}°");
        Doc.Undo();
        SettleFrames(150);

        // 顺时针转 400°：读数 −355.0°（负方向同样无下限）
        Doc.Clear();
        Doc.ClearHistory();
        var negTurn = MakeFortyFiveLine();
        Doc.AddStroke(negTurn);
        Doc.SelectOnly(new[] { negTurn });
        SettleFrames(300);
        var fNeg = SelectionHandles.FrameOf(Doc.Selected);
        var pvNeg = new Vector2((fNeg.CanvasAabb.MinX + fNeg.CanvasAabb.MaxX) * 0.5f,
                                (fNeg.CanvasAabb.MinY + fNeg.CanvasAabb.MaxY) * 0.5f);
        var gripNeg = SelectionHandles.CanvasPosition(SelHandle.Rotate, fNeg, DpiScale);
        float armNeg = Vector2.Distance(gripNeg, pvNeg);
        float aNeg0 = MathF.Atan2(gripNeg.Y - pvNeg.Y, gripNeg.X - pvNeg.X);
        float travelNeg = 0f;
        void NegStep(float cw)
        {
            travelNeg += cw;                 // 顺时针量：屏幕上的方向角**增大**
            float rad = aNeg0 + travelNeg * MathF.PI / 180f;
            UpdateSelectionGestureForTest(pvNeg.X + armNeg * MathF.Cos(rad),
                                          pvNeg.Y + armNeg * MathF.Sin(rad));
            SettleFrames(70);
        }
        bool tookNeg = SelectionGestureForTest(gripNeg.X, gripNeg.Y);   // 先按下旋转柄
        for (int i = 0; i < 4; i++) NegStep(90f);      // 顺时针 360°
        NegStep(40f);                                   // 再 40° → 共 400°
        Check("无上限组：顺时针转 400° → 读数 −355.0°（负方向也没有下限）",
              tookNeg && Math.Abs(SelRotationInclination + 355f) < 0.5f,
              $"接住={tookNeg}，读数 {SelRotationInclination:F2}°（期望 −355.0°）");
        EndSelectionGestureForTest();
        SettleFrames(200);
        Doc.Undo();
        SettleFrames(150);

        // —— 单选图形（矩形）：读数走**姿态角**（规格 9.7；在这之前它读 Δ）——
        // 吸 0/90、矩阵与标签同角这些真机细节在下面的 G2 段，这里只钉"走哪一档"。
        Doc.Clear();
        Doc.ClearHistory();
        var spinRect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
        };
        spinRect.AddPoint(lx, ly, 1f, 0);
        spinRect.AddPoint(lx + 600f, ly - 400f, 1f, 0);
        Doc.AddStroke(spinRect);
        Doc.SelectOnly(new[] { spinRect });
        SettleFrames(300);
        GrabRotate(24.5f, out bool tookRect);
        Check("矩形：旋转读数走**姿态角**（不读 α、也不读 Δ）",
              tookRect && SelRotating && SelRotationReadsPose && !SelRotationReadsInclination,
              $"接住={tookRect}，读姿态={SelRotationReadsPose}，读α={SelRotationReadsInclination}，"
              + $"读数 {SelRotationPose:F2}°（Δ 是 {SelRotationDegrees:F2}°）");
        Check("矩形：姿态角读数 = 0 + 24.5 → 24.5°（离 0/90 都远，不吸）",
              !SelRotationSnapped && MathF.Abs(SelRotationPose - 24.5f) < 1.5f,
              $"读数 {SelRotationPose:F2}°，吸住={SelRotationSnapped}");
        // 多选（两条线）：也应该退回 Δ（"单选一个图形 / 单选直线"才读那两个角）
        EndSelectionGestureForTest();
        SettleFrames(150);
        Doc.Clear();
        Doc.ClearHistory();
        var m1 = new Stroke { Tool = Tool.Line, Kind = StrokeKind.Line,
                              Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale };
        m1.AddPoint(lx, ly, 1f, 0);
        m1.AddPoint(lx + 600f, ly - 218f, 1f, 0);
        var m2 = new Stroke { Tool = Tool.Line, Kind = StrokeKind.Line,
                              Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale };
        m2.AddPoint(lx, ly + 300f, 1f, 0);
        m2.AddPoint(lx + 600f, ly + 82f, 1f, 0);
        Doc.AddStroke(m1);
        Doc.AddStroke(m2);
        Doc.SelectOnly(new[] { m1, m2 });
        SettleFrames(300);
        GrabRotate(24.5f, out bool tookMulti);
        Check("多选两条直线：旋转读数也退回 Δ（只有单选直线才读 α）",
              tookMulti && SelRotating && !SelRotationReadsInclination && !SelRotationReadsPose
              && Math.Abs(SelRotationDegrees - 24.5f) < 1.5f,
              $"接住={tookMulti}，读α={SelRotationReadsInclination}，读数 {SelRotationDegrees:F2}°");
        EndSelectionGestureForTest();
        SettleFrames(150);

        // ================= G2. 姿态角（图形）：提取 / 吸附 0-90 / 矩阵与标签同角 =================
        //
        // 规格 9.7：单选一个图形（矩形 / 椭圆 / 三角形 / 平行四边形）拖旋转柄时，
        // 读数从"转了多少 Δ"改成"**图形相对水平的姿态角**"（0° = 正的、90° = 竖的），
        // 软吸附 0/90（±1°）。用户要的用途写在规格里：将来手写识别出来的椭圆 / 矩形可能是
        // 歪的，**看着读数拖到 0° 就转正了**。
        Console.WriteLine("  -- G2. 姿态角（提取 / 吸附 / 矩阵与标签同角）--");
        {
            // 独立量一遍姿态角：把局部 x 轴过变换、自己 atan2、自己折进 [0,180)。
            // **不走被测的那条路**（PoseAngleDegrees），判据才有意义。
            float IndependentPose(Matrix3x2 m)
            {
                var o = Vector2.Transform(Vector2.Zero, m);
                var v = Vector2.Transform(Vector2.UnitX, m) - o;
                float a = -MathF.Atan2(v.Y, v.X) * 180f / MathF.PI;
                a %= 180f;
                if (a < 0f) a += 180f;
                return a;
            }
            // 拖动中"这个图形现在什么姿态"要问**合成后**的矩阵（方案 B：模型到松手才动）。
            float LivePose(Stroke s) => IndependentPose(
                DragPreviewActive ? s.Transform * DragPreviewMatrix : s.Transform);

            var pc = new Vector2(lx + 300f, ly - 150f);
            foreach (float deg in new[] { 0f, 12.5f, 137.4f, 271f })
            {
                var m = SelectionHandles.RotateMatrix(deg, pc);
                float got = SelectionHandles.PoseAngleDegrees(m);
                Check($"姿态角·提取：转 {deg:F1}° 的图形",
                      MathF.Abs(got - IndependentPose(m)) < 0.1f,
                      $"读数 {got:F2}°，独立算 {IndependentPose(m):F2}°（折在 [0,180)）");
            }
            // 镜像 / 翻转：折进 [0,180) 之后与"正反"无关（图形没有正反）。
            // 左右翻转一个正着的矩形，它照样是"水平的" → 读 0.0°。
            var flipH = SelectionHandles.RotateMatrix(0f, pc) * Matrix3x2.CreateScale(-1f, 1f, pc);
            Check("姿态角·提取：左右翻转过的矩形照样读 0.0°",
                  SelectionHandles.IsMirrored(flipH)
                  && SelectionHandles.InclinationDistance(
                         SelectionHandles.PoseAngleDegrees(flipH), 0f) < 0.01f,
                  $"读 {SelectionHandles.PoseAngleDegrees(flipH):F2}°（行列式为负 = 已镜像，"
                  + $"独立算 {IndependentPose(flipH):F2}°）");
            foreach (float deg in new[] { 0f, 30f, 95f })
            {
                var mm = SelectionHandles.RotateMatrix(deg, pc)
                       * Matrix3x2.CreateScale(1f, -1f, pc);        // 上下翻转
                float got = SelectionHandles.PoseAngleDegrees(mm);
                Check($"姿态角·提取：上下翻转 + 转 {deg:F1}°",
                      SelectionHandles.IsMirrored(mm)
                      && got >= 0f && got < 180f
                      && MathF.Abs(got - IndependentPose(mm)) < 0.1f,
                      $"读 {got:F2}°，独立算 {IndependentPose(mm):F2}°，镜像={SelectionHandles.IsMirrored(mm)}");
            }

            // 吸附口径：**只有 0/90 两条，±1°**（和直线同一套容差；直线是八个特殊角）。
            float F0(float d, bool shift, bool alt, out bool sn)
                => SelectionHandles.SnapPoseDegrees(d, shift, alt, out sn);
            Check("姿态角·吸附：0.6° 吸到 0°（±1° 内）",
                  MathF.Abs(F0(0.6f, false, false, out bool p1) - 0f) < 0.01f && p1, "0.6° → 0.0°");
            Check("姿态角·吸附：1.3° **不吸**（容差外，和直线同一个 ±1°）",
                  MathF.Abs(F0(1.3f, false, false, out bool p2) - 1.3f) < 0.01f && !p2, "1.3° → 留自由");
            Check("姿态角·吸附：89.4° 吸到 90°",
                  MathF.Abs(F0(89.4f, false, false, out bool p3) - 90f) < 0.01f && p3, "89.4° → 90.0°");
            Check("姿态角·吸附：91.4° 不吸（容差外）",
                  MathF.Abs(F0(91.4f, false, false, out bool p4) - 91.4f) < 0.01f && !p4, "91.4° → 留自由");
            // 折过的那一版在环上算：179.5° 离 0° 只有 0.5°，所以吸到的是 **0°**（同一条水平线）。
            Check("姿态角·吸附：179.5° 吸到 0°（环形：离 0° 只有 0.5°）",
                  MathF.Abs(F0(179.5f, false, false, out bool p5) - 0f) < 0.01f && p5, "179.5° → 0.0°");
            Check("姿态角·吸附：45° 不吸（它不在 0/90 这两条上）",
                  MathF.Abs(F0(45f, false, false, out bool p6) - 45f) < 0.01f && !p6, "45° → 留自由");
            Check("姿态角·吸附：Shift = 15° 硬网格",
                  MathF.Abs(F0(12.6f, true, false, out bool p7) - 15f) < 0.01f && p7, "12.6° → 15°");
            Check("姿态角·吸附：Alt = 完全自由",
                  MathF.Abs(F0(0.2f, false, true, out bool p8) - 0.2f) < 0.01f && !p8, "0.2° → 留自由");
            // 引擎里用的是**展开值**那一版（"按下时的角 + 累积角"）：连续角上吸才不会翻 180°。
            Check("姿态角·吸附（展开值）：179.6° → 180.0°（不是翻成 0°）",
                  MathF.Abs(SelectionHandles.SnapExpandedPoseDegrees(179.6f, false, false, out bool p9)
                            - 180f) < 0.01f && p9,
                  "179.6° → 180.0°（拿折过的值去吸会得到 0.0° —— 那等于把图形翻过来 180°）");
            Check("姿态角·吸附（展开值）：-0.4° → 0.0°（顺时针转过来的那一点点）",
                  MathF.Abs(SelectionHandles.SnapExpandedPoseDegrees(-0.4f, false, false, out bool p10)
                            - 0f) < 0.01f && p10,
                  "-0.4° → 0.0°");

            // ---- 真机①：一个**转过 8° 的矩形**拖旋转柄 → 吸到 0°（水平）----
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var poseRect = new Stroke
            {
                Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            poseRect.AddPoint(lx, ly, 1f, 0);
            poseRect.AddPoint(lx + 600f, ly + 400f, 1f, 0);
            poseRect.Transform = SelectionHandles.RotateMatrix(8f, new Vector2(lx + 300f, ly + 200f));
            Doc.AddStroke(poseRect);
            Doc.SelectOnly(new[] { poseRect });
            SettleFrames(300);
            Check("姿态角·真机：这个矩形一开始是歪的（姿态 8.0°）",
                  MathF.Abs(SelectionHandles.PoseAngleDegrees(poseRect.Transform) - 8f) < 0.05f,
                  $"姿态 {SelectionHandles.PoseAngleDegrees(poseRect.Transform):F3}°");
            GrabRotate(-8.4f, out bool tookPose);     // 屏幕上顺时针 8.4°：姿态 8° → -0.4（±1° 内 → 吸到 0）
            Check("姿态角·真机：拖旋转柄 → 走姿态角这一档",
                  tookPose && SelRotating && SelRotationReadsPose && !SelRotationReadsInclination,
                  $"接住={tookPose}，读姿态={SelRotationReadsPose}，读α={SelRotationReadsInclination}");
            Check("姿态角·真机：吸到 0°（水平）", SelRotationSnapped,
                  $"吸住={SelRotationSnapped}，读数 {SelRotationPose:F3}°");
            Check("姿态角·真机：读数就是 0.0°（吸住时写的是吸到的那条线，不是 180.0°）",
                  MathF.Abs(SelRotationPose - 0f) < 0.01f,
                  $"读数 {SelRotationPose:F4}°");
            Check("姿态角·真机：**矩阵与标签是同一个角**（拿实时变换反推）",
                  SelectionHandles.InclinationDistance(SelRotationPose, LivePose(poseRect)) < 0.01f
                  && SelectionHandles.InclinationDistance(LivePose(poseRect), 0f) < 0.01f,
                  $"标签 {SelRotationPose:F4}°，实时矩阵反推 {LivePose(poseRect):F4}°");
            EndSelectionGestureForTest();
            SettleFrames(220);
            Check("姿态角·真机：松手真的落到水平（对象姿态 ≈ 0°，一步撤销）",
                  SelectionHandles.InclinationDistance(
                      SelectionHandles.PoseAngleDegrees(poseRect.Transform), 0f) < 0.05f,
                  $"落定姿态 {SelectionHandles.PoseAngleDegrees(poseRect.Transform):F3}°，撤销栈 {Doc.UndoDepth} 步");
            Doc.Undo();
            SettleFrames(150);

            // ---- 真机②：一个**转过 52° 的椭圆**拖旋转柄 → 吸到 90°（竖的）----
            // 这正是用户提这个需求的初衷：识别出来的椭圆是歪的，想把它"转正"。
            Doc.Clear();
            Doc.ClearHistory();
            var poseEll = new Stroke
            {
                Tool = Tool.Ellipse, Kind = StrokeKind.Ellipse,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            var ellCenter = new Vector2(lx + 300f, ly);
            poseEll.AddPoint(ellCenter.X, ellCenter.Y, 1f, 0);          // 中心
            poseEll.AddPoint(ellCenter.X + 320f, ellCenter.Y + 140f, 1f, 0);   // 外角点（a=320, b=140）
            poseEll.Transform = SelectionHandles.RotateMatrix(52f, ellCenter);
            Doc.AddStroke(poseEll);
            Doc.SelectOnly(new[] { poseEll });
            SettleFrames(300);
            Check("姿态角·真机：歪椭圆一开始的姿态 ≈ 52.0°",
                  MathF.Abs(SelectionHandles.PoseAngleDegrees(poseEll.Transform) - 52f) < 0.05f,
                  $"姿态 {SelectionHandles.PoseAngleDegrees(poseEll.Transform):F3}°");
            GrabRotate(38.4f, out bool tookEll);      // 屏幕上逆时针 38.4°：52 → 90.4（±1° 内 → 吸到 90）
            Check("姿态角·真机：歪椭圆拖到 90° 附近吸住（竖的）",
                  tookEll && SelRotating && SelRotationReadsPose && SelRotationSnapped
                  && MathF.Abs(SelRotationPose - 90f) < 0.01f,
                  $"读数 {SelRotationPose:F4}°，吸住={SelRotationSnapped}");
            EndSelectionGestureForTest();
            SettleFrames(220);
            Check("姿态角·真机：松手后椭圆真的竖起来了（姿态 ≈ 90°）",
                  SelectionHandles.InclinationDistance(
                      SelectionHandles.PoseAngleDegrees(poseEll.Transform), 90f) < 0.05f,
                  $"落定姿态 {SelectionHandles.PoseAngleDegrees(poseEll.Transform):F3}°");
            Doc.Undo();
            SettleFrames(150);

            // ---- 镜像过的图形：读数仍然以**眼睛看到的**为准（和矩阵同一个角）----
            Doc.Clear();
            Doc.ClearHistory();
            var mirRect = new Stroke
            {
                Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            mirRect.AddPoint(lx, ly, 1f, 0);
            mirRect.AddPoint(lx + 500f, ly - 300f, 1f, 0);
            Doc.AddStroke(mirRect);
            Doc.SelectOnly(new[] { mirRect });
            SettleFrames(200);
            Doc.ApplyTransform(SelectionHandles.MirrorMatrix(mirRect.WorldInkBounds, horizontal: true));
            SettleFrames(250);
            bool mirrored = SelectionHandles.IsMirrored(mirRect.Transform);
            // 镜像过的图形"本地转 +1°"在屏幕上是 -1°：要转到 90° 得**顺时针**拖 90.4°
            // （这正是引擎里那个 sgn 符号存在的理由，自检把这条走一遍）。
            GrabRotate(-90.4f, out bool tookMir);
            Check("姿态角·真机：镜像过的图形也能吸（0/90 那一档照样生效）",
                  tookMir && mirrored && SelRotating && SelRotationReadsPose && SelRotationSnapped
                  && SelectionHandles.InclinationDistance(SelRotationPose, 90f) < 0.01f,
                  $"镜像={mirrored}，吸住={SelRotationSnapped}，读数 {SelRotationPose:F3}°");
            Check("姿态角·真机：镜像 + 拖过 → 标签与实时矩阵**仍是同一个角**",
                  SelectionHandles.InclinationDistance(SelRotationPose, LivePose(mirRect)) < 0.01f,
                  $"标签 {SelRotationPose:F4}°，实时矩阵反推 {LivePose(mirRect):F4}°");
            EndSelectionGestureForTest();
            SettleFrames(150);

            // ---- 直线仍然读 α（不是姿态角）；圆连姿态角这一档都进不去 ----
            var sl = new Stroke
            {
                Tool = Tool.Line, Kind = StrokeKind.Line,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            sl.AddPoint(lx, ly, 1f, 0);
            sl.AddPoint(lx + 600f, ly - 218f, 1f, 0);        // α ≈ 19.96°
            Doc.Clear();
            Doc.ClearHistory();
            Doc.AddStroke(sl);
            Doc.SelectOnly(new[] { sl });
            SettleFrames(300);
            GrabRotate(10.4f, out bool tookLine);
            Check("直线：仍然读倾斜角 α（不读姿态角）",
                  tookLine && SelRotating && SelRotationReadsInclination && !SelRotationReadsPose
                  && MathF.Abs(SelRotationInclination - 30f) < 0.2f,
                  $"读α={SelRotationReadsInclination}，读姿态={SelRotationReadsPose}，"
                  + $"读数 {SelRotationInclination:F2}°（α₀≈19.96 + 10.4 → 吸到 30）");
            EndSelectionGestureForTest();
            SettleFrames(150);

            var circ = new Stroke
            {
                Tool = Tool.Circle, Kind = StrokeKind.Circle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            circ.AddPoint(lx + 200f, ly, 1f, 0);
            circ.AddPoint(lx + 420f, ly, 1f, 0);            // 半径 220
            Doc.Clear();
            Doc.ClearHistory();
            Doc.AddStroke(circ);
            Doc.SelectOnly(new[] { circ });
            SettleFrames(250);
            var cFrame = SelectionHandles.FrameOf(Doc.Selected);
            var cGrip = SelectionHandles.CanvasPosition(SelHandle.Rotate, cFrame, DpiScale);
            bool circlePoseOk = !SelectionHandles.PoseEditable(Doc.Selected, out _)
                                && !SelectionHandles.RotateHandleVisible(circ)
                                && SelectionHandles.HitTest(cGrip.X, cGrip.Y, Doc.Selected, cFrame, DpiScale)
                                   != SelHandle.Rotate;
            bool circleCaught = SelectionGestureForTest(cGrip.X, cGrip.Y);
            bool circleRotating = SelRotating;
            EndSelectionGestureForTest();
            SettleFrames(120);
            Check("圆：没有姿态角读数（连旋转柄都不给，那个位置按下去也接不住旋转）",
                  circlePoseOk && !circleRotating,
                  $"PoseEditable={SelectionHandles.PoseEditable(Doc.Selected, out _)}，"
                  + $"旋转柄可见={SelectionHandles.RotateHandleVisible(circ)}，"
                  + $"在那个位置按下接住旋转={circleCaught && circleRotating}");
            SettleFrames(120);

            // ---- 图像 / 笔迹 / 多选：仍然读转过的 Δ，而且**通用旋转的 ±3° 口径一个字没改** ----
            Doc.Clear();
            Doc.ClearHistory();
            var scribble = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 12f * DpiScale,
            };
            for (int i = 0; i <= 40; i++)
                scribble.AddPoint(lx + i * 12f, ly + MathF.Sin(i * 0.3f) * 60f, 0.9f, i * 8);
            Doc.AddStroke(scribble);
            Doc.SelectOnly(new[] { scribble });
            SettleFrames(250);
            GrabRotate(24.5f, out bool tookFree);
            Check("笔迹：旋转读数仍然是 Δ（不是姿态角）",
                  tookFree && SelRotating && !SelRotationReadsPose && !SelRotationReadsInclination
                  && Math.Abs(SelRotationDegrees - 24.5f) < 1.5f,
                  $"读姿态={SelRotationReadsPose}，缺口 {SelRotationDegrees:F2}°");
            EndSelectionGestureForTest();
            SettleFrames(150);

            Doc.Clear();
            Doc.ClearHistory();
            var pic = Doc.AddImage(MakeTestImage(300, 200), lx, ly - 100f, 1f);
            Doc.SelectOnly(new[] { pic });
            SettleFrames(250);
            GrabRotate(24.5f, out bool tookImg);
            Check("图像：旋转读数仍然是 Δ（不是姿态角）",
                  tookImg && SelRotating && !SelRotationReadsPose && !SelRotationReadsInclination
                  && Math.Abs(SelRotationDegrees - 24.5f) < 1.5f,
                  $"读姿态={SelRotationReadsPose}，读数 {SelRotationDegrees:F2}°"
                  + $"（PoseEditable={SelectionHandles.PoseEditable(Doc.Selected, out _)}）");
            EndSelectionGestureForTest();
            SettleFrames(150);

            // 多选两个**图形**（都是"会读姿态角"的种类）：单选才读姿态，多选一律退回 Δ。
            Doc.Clear();
            Doc.ClearHistory();
            var mr1 = new Stroke { Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                                   Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale };
            mr1.AddPoint(lx, ly, 1f, 0);
            mr1.AddPoint(lx + 300f, ly - 200f, 1f, 0);
            var mr2 = new Stroke { Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                                   Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale };
            mr2.AddPoint(lx + 400f, ly, 1f, 0);
            mr2.AddPoint(lx + 700f, ly - 200f, 1f, 0);
            Doc.AddStroke(mr1);
            Doc.AddStroke(mr2);
            Doc.SelectOnly(new[] { mr1, mr2 });
            SettleFrames(300);
            GrabRotate(24.5f, out bool tookTwoShapes);
            Check("多选两个矩形：读数退回 Δ（姿态角只认单选）",
                  tookTwoShapes && SelRotating && !SelRotationReadsPose && !SelRotationReadsInclination
                  && Math.Abs(SelRotationDegrees - 24.5f) < 1.5f,
                  $"读姿态={SelRotationReadsPose}，读数 {SelRotationDegrees:F2}°");
            EndSelectionGestureForTest();
            SettleFrames(150);

            // 通用旋转那一档的容差**必须一个字没改**：±3°，且只吸 90° 的整数倍。
            Check("通用旋转：92° 吸到 90°（±3° 内，保持现在的行为）",
                  MathF.Abs(SelectionHandles.SnapRotationDegrees(92f, false, false, out bool g1) - 90f) < 0.01f
                  && g1, "92° → 90°");
            Check("通用旋转：94° **不吸**（±3° 外，口径没被这一轮改窄）",
                  MathF.Abs(SelectionHandles.SnapRotationDegrees(94f, false, false, out bool g2) - 94f) < 0.01f
                  && !g2, $"94° → 留自由（容差常量 = {SelectionHandles.RotationSoftSnapToleranceDegrees}）");
            Check("通用旋转：45° 不吸（它只吸 90° 的整数倍，不是直线的八个特殊角）",
                  MathF.Abs(SelectionHandles.SnapRotationDegrees(45f, false, false, out bool g3) - 45f) < 0.01f
                  && !g3, "45° → 留自由");
        }

        // ================= H. 直线/箭头的选中框：紧框（端点口径）=================
        //
        // 旧口径 `TransformRect(InkBounds, Transform)` = "先取局部 AABB、整体转过去、
        // 再取外接矩形"。直线的局部 AABB 四个角**根本不在线上**，所以一转就虚胖
        // （2026-09-18 实测：转过 31.5° 的直线框 962×838，线自己只有 490×837）。
        // 新口径 = 端点（箭头还要加两个翅膀尖）过变换后取外接，再外扩半笔宽。
        //
        // 这里的"旧口径"是**自检自己现算一遍**的（不调被测实现）——判据才有意义。
        Console.WriteLine("  -- H. 直线/箭头选中框：紧框（端点口径）--");
        {
            RectF CornersBox(in RectF r, in Matrix3x2 m)
            {
                var box = RectF.Empty;
                foreach (var c in new[]
                {
                    new Vector2(r.MinX, r.MinY), new Vector2(r.MaxX, r.MinY),
                    new Vector2(r.MaxX, r.MaxY), new Vector2(r.MinX, r.MaxY),
                })
                {
                    var q = Vector2.Transform(c, m);
                    box.Add(q.X, q.Y);
                }
                return box;
            }
            RectF LocalInkBox(Stroke s2)
            {
                var r = RectF.Empty;
                float hw = s2.Width * 0.5f;
                foreach (var p in s2.Points) { r.Add(p.X - hw, p.Y - hw); r.Add(p.X + hw, p.Y + hw); }
                return r;
            }
            string Size(in RectF r) => $"{r.MaxX - r.MinX:F0}×{r.MaxY - r.MinY:F0}";
            bool Same(in RectF a, in RectF b, float tol = 0.01f)
                => MathF.Abs(a.MinX - b.MinX) < tol && MathF.Abs(a.MinY - b.MinY) < tol
                && MathF.Abs(a.MaxX - b.MaxX) < tol && MathF.Abs(a.MaxY - b.MaxY) < tol;

            // ---- 一条 **45° 斜线**，四个姿态：0°（没转过）/ 30° / 60° / 90° ----
            //
            // 为什么用斜线而不是水平线：虚胖的幅度取决于"局部 AABB 的长短边之比 + 转了多大"。
            // 一条水平长线（900×16）转 30° 时旧口径只胖 6 像素——判据会"看着过了但其实没验到"。
            // 斜线的局部框是 608×608，一转就是 830×830 这种，虚胖一眼可见。
            // 注：**不可能**要求四个姿态"都比旧口径小一大截"——90° 这种角度旧口径本来就接近
            // （正方形框转 90° 还是它自己），所以硬判据是"≈ 按定义现算的端点外接"，
            // 旧口径的值打印出来作对照，另有一条专门判"明显小"（45° 斜线转 30° 那一档）。
            var slant = new Func<Stroke>(() =>
            {
                var s2 = new Stroke
                {
                    Tool = Tool.Line, Kind = StrokeKind.Line,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
                };
                s2.AddPoint(lx, ly, 1f, 0);
                s2.AddPoint(lx + 600f, ly - 600f, 1f, 0);      // 45° 斜线
                return s2;
            });
            foreach (float deg in new[] { 0f, 30f, 60f, 90f })
            {
                var s2 = slant();
                if (deg != 0f)
                    s2.Transform = SelectionHandles.RotateMatrix(deg, new Vector2(lx + 300f, ly - 300f));

                var oldBox = CornersBox(LocalInkBox(s2), s2.Transform);   // 旧口径（自检自己算）
                var got = s2.WorldInkBounds;
                var e0 = SelectionHandles.EndpointCanvasPosition(s2, 0);
                var e1 = SelectionHandles.EndpointCanvasPosition(s2, 1);
                var want = RectF.Empty;
                want.Add(e0.X, e0.Y);
                want.Add(e1.X, e1.Y);
                want = want.Inflate(s2.Width * 0.5f);

                if (deg == 0f)
                {
                    // 没转过：新口径与旧口径**一字不差**（这条是"不改坏"的保险）
                    Check("没转过的直线：框值与旧口径一字不差", Same(got, oldBox),
                          $"新 {Size(got)} / 旧 {Size(oldBox)}");
                }
                else
                {
                    Check($"转 {deg:F0}° 的斜线：框 ≈ 端点外接 + 半笔宽（≤1 像素）",
                          Same(got, want, 1f),
                          $"框 {Size(got)}，按定义现算 {Size(want)}"
                          + $"（旧口径 {Size(oldBox)} 作对照）");
                }
            }
            {
                // 专门判"明显小"：45° 斜线转 30° —— 旧口径约 830×830，新口径约 230×830
                var s2 = slant();
                s2.Transform = SelectionHandles.RotateMatrix(30f, new Vector2(lx + 300f, ly - 300f));
                var oldBox = CornersBox(LocalInkBox(s2), s2.Transform);
                var got = s2.WorldInkBounds;
                Check("转 30° 的斜线：框比旧口径小了 500 像素以上（虚胖被去掉）",
                      (got.MaxX - got.MinX) < (oldBox.MaxX - oldBox.MinX) - 500f,
                      $"新 {Size(got)} ／ 旧 {Size(oldBox)}");
            }

            // ---- 上一轮那个现场那组数（同一条线、同一个角）----
            {
                var s2 = new Stroke
                {
                    Tool = Tool.Line, Kind = StrokeKind.Line,
                    Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f,
                };
                s2.AddPoint(1020f, 1120f, 1f, 0);
                s2.AddPoint(1860f, 680f, 1f, 0);
                var center2 = new Vector2(1440f, 900f);
                s2.Transform = SelectionHandles.RotateMatrix(31.5f, center2);   // → α ≈ 59.15°
                var oldBox = CornersBox(LocalInkBox(s2), s2.Transform);
                var got = s2.WorldInkBounds;
                var e0 = SelectionHandles.EndpointCanvasPosition(s2, 0);
                var e1 = SelectionHandles.EndpointCanvasPosition(s2, 1);
                var want = RectF.Empty;
                want.Add(e0.X, e0.Y);
                want.Add(e1.X, e1.Y);
                want = want.Inflate(s2.Width * 0.5f);
                Check("上一轮那个现场（转 31.5° 的直线）：框从 962×838 收到 ≈ 490×838",
                      Same(got, want, 1f) && (got.MaxX - got.MinX) < 550f,
                      $"新 {Size(got)} ／ 旧 {Size(oldBox)}（上一轮实测：962×838 vs 线自己 490×837）");
                // 导出 / 剪贴板给外部那张图的裁切范围就是 PaddedBounds —— 它跟着这条口径一起收紧
                var pb = s2.PaddedBounds;
                var pbOld = CornersBox(LocalInkBox(s2), s2.Transform).Inflate(s2.Width * 0.5f + 2f);
                Check("导出/剪贴板的裁切范围（PaddedBounds）也跟着收紧",
                      (pb.MaxX - pb.MinX) < 550f,
                      $"新 {Size(pb)} ／ 旧 {Size(pbOld)}");
            }

            // ---- 箭头：框必须包住头部的两个翅膀尖（它们不在两端之间）----
            {
                var ar = new Stroke
                {
                    Tool = Tool.Arrow, Kind = StrokeKind.Arrow,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 6f * DpiScale,
                };
                ar.AddPoint(lx, ly, 1f, 0);
                ar.AddPoint(lx + 900f, ly, 1f, 0);
                ar.Transform = SelectionHandles.RotateMatrix(45f, new Vector2(lx + 450f, ly));
                var ea = SelectionHandles.EndpointCanvasPosition(ar, 0);
                var eb = SelectionHandles.EndpointCanvasPosition(ar, 1);
                // 头部几何按"渲染那一份的公式"独立复算：head = clamp(len*0.28,10,48)、spread = head*0.45
                float dx = eb.X - ea.X, dy = eb.Y - ea.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                dx /= len; dy /= len;
                float head = Math.Clamp(len * 0.28f, 10f, 48f);
                var root = new Vector2(eb.X - dx * head, eb.Y - dy * head);
                var nrm = new Vector2(-dy, dx);
                float spread = head * 0.45f;
                var want = RectF.Empty;
                foreach (var p in new[] { ea, eb, root + nrm * spread, root - nrm * spread })
                    want.Add(p.X, p.Y);
                want = want.Inflate(ar.Width * 0.5f);
                var got = ar.WorldInkBounds;
                Check("转 45° 的箭头：框包住两端点 + 两个翅膀尖（≤1 像素）",
                      Same(got, want, 1f), $"框 {Size(got)}，含翅膀现算 {Size(want)}");
            }

            // ---- 不变的：矩形 / 图像（各转 30°）----
            // 注：**椭圆不在这一组**了——2026-09-19 起它和圆一样走"参数化紧框"
            // （见下面 J 段：椭圆局部是参数曲线，把它的外框矩形转过去再取外接会虚胖）。
            foreach (var (tool, kind, tag) in new[]
            {
                (Tool.Rectangle, StrokeKind.Rectangle, "矩形"),
            })
            {
                var s2 = new Stroke
                {
                    Tool = tool, Kind = kind,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
                };
                s2.AddPoint(lx, ly, 1f, 0);
                s2.AddPoint(lx + 600f, ly - 400f, 1f, 0);
                s2.Transform = SelectionHandles.RotateMatrix(30f, new Vector2(lx + 300f, ly - 200f));
                var oldBox = CornersBox(LocalInkBox(s2), s2.Transform);
                Check($"{tag}（转过 30°）：框值与旧口径一字不差",
                      Same(s2.WorldInkBounds, oldBox),
                      $"新 {Size(s2.WorldInkBounds)} / 旧 {Size(oldBox)}");
            }
            var img = Doc.AddImage(MakeTestImage(400, 300), lx, ly, 1f / DpiScale);
            img.Transform = SelectionHandles.RotateMatrix(30f,
                new Vector2(lx + 200f, ly + 150f));
            var imgOld = CornersBox(LocalInkBox(img), img.Transform);
            Check("图像（转过 30°）：框值与旧口径一字不差",
                  Same(img.WorldInkBounds, imgOld),
                  $"新 {Size(img.WorldInkBounds)} / 旧 {Size(imgOld)}");
            Doc.RemoveStroke(img);
        }

        // ================= I. 圆：画法 / 手柄 / 紧框 / 命中 =================
        //
        // 规格是 计划-图形工具.md 9.2：存"圆心 + 圆周点"、按下=圆心拖=半径、
        // 手柄只有圆心（平移）与圆周点（改半径）、**不给旋转柄**、紧框 = 圆心 ± r。
        Console.WriteLine("  -- I. 圆 --");
        {
            bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

            // ---- 画一个圆（真机：按下拖出半径）----
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Circle);
            float ccx = _virtualX + 800, ccy = _virtualY + 800;
            float cRadius = 180f;                      // 物理像素：拖这么远就是半径
            SendMouse((int)ccx, (int)ccy, 0);                            SettleFrames(60);
            SendMouse((int)ccx, (int)ccy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(ccx + cRadius * i / 4f), (int)ccy, 0);
                SettleFrames(30);
            }
            SendMouse((int)(ccx + cRadius), (int)ccy, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);

            var circle = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("圆：拖出一个圆对象", circle != null && circle.Kind == StrokeKind.Circle,
                  circle == null ? "没有对象" : $"Kind={circle.Kind}");
            if (circle != null)
            {
                Check("圆：圆心 = 按下的点（±1 像素）",
                      Near(circle.ShapeCenterLocal.X, ccx, 1f) && Near(circle.ShapeCenterLocal.Y, ccy, 1f),
                      $"圆心 ({circle.ShapeCenterLocal.X:F1},{circle.ShapeCenterLocal.Y:F1})"
                      + $" 期望 ({ccx:F0},{ccy:F0})");
                Check("圆：半径 = 拖动的距离（±1 像素）",
                      Near(circle.CircleRadiusLocal, cRadius, 1f),
                      $"半径 {circle.CircleRadiusLocal:F1} 期望 {cRadius:F0}");
                Check("圆：一次拖拽 = 一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");
                Doc.Undo();
                SettleFrames(120);
                Check("圆：撤销一次就干净", Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 个");
            }

            // ---- 造一个已知的圆，验手柄 / 紧框 / 命中 ----
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var c2 = new Stroke
            {
                Tool = Tool.Circle, Kind = StrokeKind.Circle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            c2.AddPoint(ccx, ccy, 1f, 0);            // 圆心
            c2.AddPoint(ccx + 240f, ccy, 1f, 0);     // 圆周点（半径 240）
            Doc.AddStroke(c2);
            Doc.SelectOnly(new[] { c2 });
            SettleFrames(250);

            // 紧框 = 圆心 ± r（＋半笔宽）
            var wantCircle = new RectF
            {
                MinX = ccx - 240f - c2.Width * 0.5f, MinY = ccy - 240f - c2.Width * 0.5f,
                MaxX = ccx + 240f + c2.Width * 0.5f, MaxY = ccy + 240f + c2.Width * 0.5f,
            };
            var gotCircle = c2.WorldInkBounds;
            Check("圆：紧框 = 圆心 ± r（＋半笔宽，±1 像素）",
                  MathF.Abs(gotCircle.MinX - wantCircle.MinX) < 1f
                  && MathF.Abs(gotCircle.MinY - wantCircle.MinY) < 1f
                  && MathF.Abs(gotCircle.MaxX - wantCircle.MaxX) < 1f
                  && MathF.Abs(gotCircle.MaxY - wantCircle.MaxY) < 1f,
                  $"框 {gotCircle.MaxX - gotCircle.MinX:F0}×{gotCircle.MaxY - gotCircle.MinY:F0}"
                  + $"（直径 480 + 笔宽 {c2.Width:F0}）");

            // 命中：圆周上算命中、**圆心处不算**（只认描边，不认内部）
            Check("圆：圆周上命中、圆心处不命中（只认描边）",
                  c2.HitTestExact(ccx + 240f, ccy) && !c2.HitTestExact(ccx, ccy),
                  $"圈上 {c2.HitTestExact(ccx + 240f, ccy)}，圆心 {c2.HitTestExact(ccx, ccy)}");

            // 手柄：两个（圆心 + 圆周点）、**没有旋转柄**
            Span<ShapeHandle> cHandles = stackalloc ShapeHandle[5];
            int cn = SelectionHandles.ShapeHandlesOf(c2, cHandles);
            Check("圆：手柄是两个（圆心 + 圆周点）",
                  cn == 2 && cHandles[0] == ShapeHandle.Anchor && cHandles[1] == ShapeHandle.Rim,
                  $"手柄数 {cn}");
            // 旋转柄的位置上按一下：**必须是 None**（圆的旋转柄连命中都不做）
            var cFrame = SelectionHandles.FrameOf(Doc.Selected);
            var cRotGrip = SelectionHandles.CanvasPosition(SelHandle.Rotate, cFrame, DpiScale);
            var circleRotateHit = SelectionHandles.HitTest(cRotGrip.X, cRotGrip.Y, Doc.Selected, cFrame, DpiScale);
            Check("圆：不给旋转柄（画也不画、点也点不到）",
                  !SelectionHandles.RotateHandleVisible(c2) && circleRotateHit == SelHandle.None,
                  $"可见={SelectionHandles.RotateHandleVisible(c2)}，命中={circleRotateHit}");

            // ---- 拖圆心 = 平移（半径逐位不变；几何一个点都不动）----
            float rBefore = c2.CircleRadiusLocal;
            // 圆心位移在**画布坐标**上比：几何点（局部坐标）本来就不动，动的是变换矩阵。
            var cx0 = SelectionHandles.ShapeHandleCanvasPosition(c2, ShapeHandle.Anchor);
            string ptsBefore = $"{c2.Points[0].X},{c2.Points[0].Y}|{c2.Points[1].X},{c2.Points[1].Y}";
            bool tookCenter = SelectionGestureForTest(cx0.X, cx0.Y);
            UpdateSelectionGestureForTest(cx0.X + 300f, cx0.Y + 150f);
            SettleFrames(80);
            bool geomUntouched = ptsBefore == $"{c2.Points[0].X},{c2.Points[0].Y}|{c2.Points[1].X},{c2.Points[1].Y}";
            Check("圆：拖圆心 = 整体平移（走的是变换，几何一个点不动）",
                  tookCenter && geomUntouched,
                  $"接住={tookCenter}，几何原样={geomUntouched}（若走改几何这条路这里就会变）");
            EndSelectionGestureForTest();
            SettleFrames(180);
            var cx1 = SelectionHandles.ShapeHandleCanvasPosition(c2, ShapeHandle.Anchor);
            Check("圆：平移后半径逐位不变、圆心在画布上正好挪了 (300,150)",
                  c2.CircleRadiusLocal == rBefore
                  && Near(cx1.X - cx0.X, 300f, 1f) && Near(cx1.Y - cx0.Y, 150f, 1f),
                  $"半径 {rBefore:F3} → {c2.CircleRadiusLocal:F3}，"
                  + $"圆心画布位移 ({cx1.X - cx0.X:F1},{cx1.Y - cx0.Y:F1})");

            // ---- 拖圆周点 = 只改半径（圆心逐位不变）----
            Doc.Undo();   // 把平移撤掉，回到干净的圆
            SettleFrames(150);
            int cRevBefore = c2.Revision;
            var rimPoint = SelectionHandles.ShapeHandleCanvasPosition(c2, ShapeHandle.Rim);
            // 圆心在局部坐标里的值（拖圆周点不该动它）；下一段那个"圆心逐位不变"要拿它比。
            float cxBefore = c2.Points[0].X;
            float cyBefore = c2.Points[0].Y;
            bool tookRim = SelectionGestureForTest(rimPoint.X, rimPoint.Y);
            UpdateSelectionGestureForTest(rimPoint.X + 120f, rimPoint.Y);
            SettleFrames(80);
            Check("圆：拖圆周点时模型没动（Revision 不变、快路）",
                  c2.Revision == cRevBefore && VertexDragging,
                  $"Revision {cRevBefore} → {c2.Revision}，拖元素中={VertexDragging}");
            Check("圆：拖动中内容层一帧都不重画", _windows[0].LastPatchCount == 0,
                  $"上一帧光栅化分块 {_windows[0].LastPatchCount} 块");
            Check("圆：读数给的是 r 和 d = 2r",
                  VertexReadout == VertexReadoutKind.Radius
                  && Near(VertexReadoutSecondary, VertexReadoutValue * 2f, 0.01f),
                  $"读数 r={VertexReadoutValue:F1} d={VertexReadoutSecondary:F1}");
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check("圆：拖圆周点 = 只改半径（圆心逐位不变）",
                  c2.Points[0].X == cxBefore && c2.Points[0].Y == cyBefore
                  && Near(c2.CircleRadiusLocal, 360f, 1.5f) && c2.Revision > cRevBefore,
                  $"圆心 ({c2.Points[0].X:F1},{c2.Points[0].Y:F1})，半径 {c2.CircleRadiusLocal:F1}（期望 ≈360）");
            Doc.Undo();
            SettleFrames(150);
        }

        // ================= J. 椭圆：画法变更 / 四个轴端点 / 紧框公式 =================
        //
        // 规格是 9.3：存"中心 + 外角点"、**按下=中心拖=同时定两条半轴**（2026-09-19 改，
        // 以前是"拖外框对角"）、手柄 = 中心 + 四个轴端点 + 旋转柄（**不给四个角**）、
        // 紧框 = 旋转后的参数化外接、a/b 各有最小值。
        Console.WriteLine("  -- J. 椭圆 --");
        {
            bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

            // ---- 画一个椭圆（真机：按下=中心，横纵一起定半轴）----
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Ellipse);
            float ecx = _virtualX + 800, ecy = _virtualY + 700;
            float wa = 220f, wb = 140f;
            SendMouse((int)ecx, (int)ecy, 0);                            SettleFrames(60);
            SendMouse((int)ecx, (int)ecy, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(ecx + wa * i / 4f), (int)(ecy + wb * i / 4f), 0);
                SettleFrames(30);
            }
            SendMouse((int)(ecx + wa), (int)(ecy + wb), Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);

            var ell = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("椭圆：拖出一个椭圆对象", ell != null && ell.Kind == StrokeKind.Ellipse,
                  ell == null ? "没有对象" : $"Kind={ell.Kind}");
            if (ell != null)
            {
                Check("椭圆：中心 = 按下的点（±1 像素）",
                      Near(ell.ShapeCenterLocal.X, ecx, 1f) && Near(ell.ShapeCenterLocal.Y, ecy, 1f),
                      $"中心 ({ell.ShapeCenterLocal.X:F1},{ell.ShapeCenterLocal.Y:F1})"
                      + $" 期望 ({ecx:F0},{ecy:F0})");
                Check("椭圆：a / b = 拖动的横向 / 纵向距离（±1 像素）",
                      Near(ell.SemiAxisALocal, wa, 1f) && Near(ell.SemiAxisBLocal, wb, 1f),
                      $"a={ell.SemiAxisALocal:F1} b={ell.SemiAxisBLocal:F1} 期望 {wa:F0}/{wb:F0}");
                Check("椭圆：一次拖拽 = 一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");
                Doc.Undo();
                SettleFrames(120);
            }

            // ---- 造一个已知的椭圆：手柄 / 只改一条半轴 / 最小值 / 紧框 ----
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var e2 = new Stroke
            {
                Tool = Tool.Ellipse, Kind = StrokeKind.Ellipse,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            e2.AddPoint(lx, ly, 1f, 0);              // 中心
            e2.AddPoint(lx + 240f, ly + 120f, 1f, 0); // 外角点 → a=240, b=120
            Doc.AddStroke(e2);
            Doc.SelectOnly(new[] { e2 });
            SettleFrames(250);

            Span<ShapeHandle> eHandles = stackalloc ShapeHandle[5];
            int en = SelectionHandles.ShapeHandlesOf(e2, eHandles);
            Check("椭圆：手柄是**两个**（右端点管 a、上端点管 b）",
                  en == 2 && eHandles[0] == ShapeHandle.AxisRight && eHandles[1] == ShapeHandle.AxisTop
                  && SelectionHandles.HandleOf(e2, SelHandle.EndpointB) == ShapeHandle.None,
                  $"手柄数 {en}，外角点手柄={SelectionHandles.HandleOf(e2, SelHandle.EndpointB)}");
            Check("椭圆：精简掉的三个（中心 + 左端点 + 下端点）**一个都不发**",
                  SelectionHandles.HandleOf(e2, SelHandle.Left) == ShapeHandle.None
                  && SelectionHandles.HandleOf(e2, SelHandle.Bottom) == ShapeHandle.None
                  && !SelectionHandles.IsAnchorMove(e2, ShapeHandle.Anchor),
                  $"左={SelectionHandles.HandleOf(e2, SelHandle.Left)}"
                  + $"，下={SelectionHandles.HandleOf(e2, SelHandle.Bottom)}"
                  + $"，中心算平移={SelectionHandles.IsAnchorMove(e2, ShapeHandle.Anchor)}");
            Check("椭圆：给旋转柄（和圆不同）", SelectionHandles.RotateHandleVisible(e2),
                  $"可见={SelectionHandles.RotateHandleVisible(e2)}");

            // 拖右轴端点：只改 a，b 逐位不变
            float bBefore = e2.SemiAxisBLocal;
            var rightPt = SelectionHandles.ShapeHandleCanvasPosition(e2, ShapeHandle.AxisRight);
            bool tookAxis = SelectionGestureForTest(rightPt.X, rightPt.Y);
            UpdateSelectionGestureForTest(rightPt.X + 160f, rightPt.Y);
            SettleFrames(80);
            Check("椭圆：拖轴端点时的读数是 a（不是 α）",
                  VertexReadout == VertexReadoutKind.AxisA,
                  $"读数类型={VertexReadout}，值 {VertexReadoutValue:F1}");
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check("椭圆：拖右轴端点只改 a（b 逐位不变）",
                  tookAxis && Near(e2.SemiAxisALocal, 400f, 1.5f) && e2.SemiAxisBLocal == bBefore,
                  $"a={e2.SemiAxisALocal:F1}（期望 ≈400），b {bBefore:F3} → {e2.SemiAxisBLocal:F3}");
            Doc.Undo();
            SettleFrames(150);

            // 拖上轴端点：只改 b，a 逐位不变
            float aBefore = e2.SemiAxisALocal;
            var topPt = SelectionHandles.ShapeHandleCanvasPosition(e2, ShapeHandle.AxisTop);
            SelectionGestureForTest(topPt.X, topPt.Y);
            UpdateSelectionGestureForTest(topPt.X, topPt.Y - 90f);
            SettleFrames(80);
            Check("椭圆：拖上轴端点时的读数是 b",
                  VertexReadout == VertexReadoutKind.AxisB, $"读数类型={VertexReadout}");
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check("椭圆：拖上轴端点只改 b（a 逐位不变）",
                  Near(e2.SemiAxisBLocal, 210f, 1.5f) && e2.SemiAxisALocal == aBefore,
                  $"b={e2.SemiAxisBLocal:F1}（期望 ≈210），a {aBefore:F3} → {e2.SemiAxisALocal:F3}");
            Doc.Undo();
            SettleFrames(150);

            // 最小值边界：把右轴端点一路拖过中心，a 不许小于 4 逻辑像素
            var rightPt2 = SelectionHandles.ShapeHandleCanvasPosition(e2, ShapeHandle.AxisRight);
            SelectionGestureForTest(rightPt2.X, rightPt2.Y);
            UpdateSelectionGestureForTest(lx - 500f, rightPt2.Y);   // 拖到中心左侧老远
            SettleFrames(80);
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check("椭圆：a 有最小值（拖过中心也不许退化成线段）",
                  Near(e2.SemiAxisALocal, ShapeMinAxisLogical * DpiScale, 0.01f),
                  $"a={e2.SemiAxisALocal:F2} 期望 {ShapeMinAxisLogical * DpiScale:F2}"
                  + $"（{ShapeMinAxisLogical:F0} 逻辑像素 × dpi {DpiScale}）");
            Doc.Undo();
            SettleFrames(150);

            // ---- 旋转 30° 后的紧框 = 参数化外接公式 ----
            var eRot = new Stroke
            {
                Tool = Tool.Ellipse, Kind = StrokeKind.Ellipse,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            eRot.AddPoint(lx, ly, 1f, 0);
            eRot.AddPoint(lx + 240f, ly + 120f, 1f, 0);
            eRot.Transform = SelectionHandles.RotateMatrix(30f, new Vector2(lx, ly));
            float th = 30f * MathF.PI / 180f;
            float ha = 240f, hb = 120f;
            // 规格 9.3 给的公式（自检自己算，不调被测实现）
            float ex = MathF.Sqrt(MathF.Pow(ha * MathF.Cos(th), 2) + MathF.Pow(hb * MathF.Sin(th), 2));
            float ey = MathF.Sqrt(MathF.Pow(ha * MathF.Sin(th), 2) + MathF.Pow(hb * MathF.Cos(th), 2));
            var gotE = eRot.WorldInkBounds;
            Check("椭圆：转过 30° 的紧框 = √((a·cosθ)²+(b·sinθ)²) 那两条公式（±1 像素）",
                  Near(gotE.MaxX - gotE.MinX, 2f * ex + eRot.Width, 1f)
                  && Near(gotE.MaxY - gotE.MinY, 2f * ey + eRot.Width, 1f),
                  $"框 {gotE.MaxX - gotE.MinX:F1}×{gotE.MaxY - gotE.MinY:F1}"
                  + $"，公式算 {2f * ex + eRot.Width:F1}×{2f * ey + eRot.Width:F1}");

            // 拖中心 = 平移（a / b 逐位不变）
            Doc.AddStroke(eRot);
            Doc.SelectOnly(new[] { eRot });
            SettleFrames(200);
            float ea = eRot.SemiAxisALocal, eb = eRot.SemiAxisBLocal;
            // 位移要在**画布坐标**上比：这条椭圆转过 30°，局部位移和屏幕位移不是一个方向。
            var eCenterBefore = SelectionHandles.ShapeHandleCanvasPosition(eRot, ShapeHandle.Anchor);
            var eCenter = eCenterBefore;
            SelectionGestureForTest(eCenter.X, eCenter.Y);
            UpdateSelectionGestureForTest(eCenter.X + 200f, eCenter.Y - 120f);
            SettleFrames(80);
            EndSelectionGestureForTest();
            SettleFrames(200);
            var eCenterAfter = SelectionHandles.ShapeHandleCanvasPosition(eRot, ShapeHandle.Anchor);
            Check("椭圆：拖中心 = 平移（a / b 逐位不变，而且真的挪了）",
                  eRot.SemiAxisALocal == ea && eRot.SemiAxisBLocal == eb
                  && Near(eCenterAfter.X - eCenterBefore.X, 200f, 1f)
                  && Near(eCenterAfter.Y - eCenterBefore.Y, -120f, 1f),
                  $"a {ea:F3} → {eRot.SemiAxisALocal:F3}，b {eb:F3} → {eRot.SemiAxisBLocal:F3}，"
                  + $"中心画布位移 ({eCenterAfter.X - eCenterBefore.X:F1},"
                  + $"{eCenterAfter.Y - eCenterBefore.Y:F1})");
        }

        // ================= K. 三角形：画法 / 三个顶点手柄 / 紧框 / 命中 =================
        //
        // 规格 9.4：`Points` = 三个顶点（**上中 / 下左 / 下右**）、一按一拖出来的是
        // "底边水平、左右对称"的三角形、手柄 = 三个顶点 + 旋转柄、紧框 = 三顶点外接、
        // 命中只认边（和矩形 / 椭圆一致）。
        Console.WriteLine("  -- K. 三角形 --");
        {
            bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

            // ---- 画一个三角形（真机：按下外框左上角 → 拖到右下角）----
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Triangle);
            float tax = _virtualX + 700, tay = _virtualY + 500;
            float tbx = tax + 400, tby = tay + 300;
            SendMouse((int)tax, (int)tay, 0);                            SettleFrames(60);
            SendMouse((int)tax, (int)tay, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(tax + (tbx - tax) * i / 4f), (int)(tay + (tby - tay) * i / 4f), 0);
                SettleFrames(30);
            }
            SendMouse((int)tbx, (int)tby, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(220);

            var tri = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("三角形：拖出一个三角形对象", tri != null && tri.Kind == StrokeKind.Triangle,
                  tri == null ? "没有对象" : $"Kind={tri.Kind}");
            if (tri != null)
            {
                float mid = (tax + tbx) * 0.5f;
                Check("三角形：控制点是**三个**（顶点，不是外框对角）",
                      tri.Points.Count == 3, $"点数 {tri.Points.Count}");
                Check("三角形：三个顶点 = 上中 / 下左 / 下右（±1 像素）",
                      Near(Pt(tri.Points[0]).X, mid, 1f) && Near(Pt(tri.Points[0]).Y, tay, 1f)
                      && Near(Pt(tri.Points[1]).X, tax, 1f) && Near(Pt(tri.Points[1]).Y, tby, 1f)
                      && Near(Pt(tri.Points[2]).X, tbx, 1f) && Near(Pt(tri.Points[2]).Y, tby, 1f),
                      $"上中 ({Pt(tri.Points[0]).X:F1},{Pt(tri.Points[0]).Y:F1})"
                      + $" 下左 ({Pt(tri.Points[1]).X:F1},{Pt(tri.Points[1]).Y:F1})"
                      + $" 下右 ({Pt(tri.Points[2]).X:F1},{Pt(tri.Points[2]).Y:F1})"
                      + $" 期望 ({mid:F0},{tay:F0})/({tax:F0},{tby:F0})/({tbx:F0},{tby:F0})");
                Check("三角形：一次拖拽 = 一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");
                Doc.Undo();
                SettleFrames(120);
                Check("三角形：撤销一次就干净", Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 个");
            }

            // ---- 造一个已知的三角形：手柄 / 紧框 / 命中 / 拖顶点 ----
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var t2 = new Stroke
            {
                Tool = Tool.Triangle, Kind = StrokeKind.Triangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            t2.AddPoint(lx + 300f, ly - 240f, 1f, 0);     // 上中
            t2.AddPoint(lx, ly, 1f, 0);                   // 下左
            t2.AddPoint(lx + 600f, ly, 1f, 0);            // 下右
            Doc.AddStroke(t2);
            Doc.SelectOnly(new[] { t2 });
            SettleFrames(250);

            Span<ShapeHandle> tHandles = stackalloc ShapeHandle[5];
            int tn = SelectionHandles.ShapeHandlesOf(t2, tHandles);
            Check("三角形：手柄是三个顶点（+ 旋转柄），没有八向缩放柄",
                  tn == 3 && tHandles[0] == ShapeHandle.Vertex0
                  && tHandles[1] == ShapeHandle.Vertex1 && tHandles[2] == ShapeHandle.Vertex2
                  && SelectionHandles.RotateHandleVisible(t2)
                  && SelectionHandles.HandleOf(t2, SelHandle.EndpointB) == ShapeHandle.None,
                  $"手柄数 {tn}，旋转柄={SelectionHandles.RotateHandleVisible(t2)}，"
                  + $"EndpointB→{SelectionHandles.HandleOf(t2, SelHandle.EndpointB)}");

            var tFrame = SelectionHandles.FrameOf(Doc.Selected);
            var tRot = SelectionHandles.CanvasPosition(SelHandle.Rotate, tFrame, DpiScale);
            Check("三角形：三个顶点都点得到、旋转柄也点得到",
                  SelectionHandles.HitTest(lx + 300f, ly - 240f, Doc.Selected, tFrame, DpiScale)
                      == SelHandle.VertexA
                  && SelectionHandles.HitTest(lx, ly, Doc.Selected, tFrame, DpiScale) == SelHandle.VertexB
                  && SelectionHandles.HitTest(lx + 600f, ly, Doc.Selected, tFrame, DpiScale)
                      == SelHandle.VertexC
                  && SelectionHandles.HitTest(tRot.X, tRot.Y, Doc.Selected, tFrame, DpiScale)
                      == SelHandle.Rotate,
                  "上中→VertexA，下左→VertexB，下右→VertexC");

            // 紧框 = 三个顶点的外接（＋半笔宽），**不是**"外框转过去再取外接"
            float hw = t2.Width * 0.5f;
            var wantTri = new RectF
            {
                MinX = lx - hw, MinY = ly - 240f - hw,
                MaxX = lx + 600f + hw, MaxY = ly + hw,
            };
            var gotTri = t2.WorldInkBounds;
            Check("三角形：紧框 = 三个顶点的外接（＋半笔宽，±0.5 像素）",
                  Near(gotTri.MinX, wantTri.MinX, 0.5f) && Near(gotTri.MinY, wantTri.MinY, 0.5f)
                  && Near(gotTri.MaxX, wantTri.MaxX, 0.5f) && Near(gotTri.MaxY, wantTri.MaxY, 0.5f),
                  $"框 ({gotTri.MinX:F1},{gotTri.MinY:F1})-({gotTri.MaxX:F1},{gotTri.MaxY:F1})"
                  + $" 期望 ({wantTri.MinX:F1},{wantTri.MinY:F1})-({wantTri.MaxX:F1},{wantTri.MaxY:F1})");

            // 命中：边上命中、**内部不命中**（只认描边，和矩形 / 椭圆一致）
            var edgeMid = new Vector2((lx + 300f + lx) * 0.5f, (ly - 240f + ly) * 0.5f);   // 左上那条边的中点
            var centroid = new Vector2((lx + 300f + lx + lx + 600f) / 3f,
                                       (ly - 240f + ly + ly) / 3f);
            Check("三角形：边上命中、内部不命中（只认描边）",
                  t2.HitTestExact(edgeMid.X, edgeMid.Y) && !t2.HitTestExact(centroid.X, centroid.Y),
                  $"边上 {t2.HitTestExact(edgeMid.X, edgeMid.Y)}，"
                  + $"重心 {t2.HitTestExact(centroid.X, centroid.Y)}");

            // ---- 拖一个顶点：只动它、走快路、一步撤销 ----
            var tApex = SelectionHandles.ShapeHandleCanvasPosition(t2, ShapeHandle.Vertex0);
            float p1x = t2.Points[1].X, p1y = t2.Points[1].Y;
            float p2x = t2.Points[2].X, p2y = t2.Points[2].Y;
            float p0x = t2.Points[0].X, p0y = t2.Points[0].Y;
            int tRev = t2.Revision, tUndo = Doc.UndoDepth;
            // 目标位置刻意挑成"离等腰 / 等边 / 直角都很远"：这一条只验拖动本身，
            // 吸附留给 M 段（两个行为混在一起，红了也不知道是谁的错）。
            var tApexTo = new Vector2(lx + 140f, ly - 620f);
            bool tookApex = SelectionGestureForTest(tApex.X, tApex.Y);
            UpdateSelectionGestureForTest(tApexTo.X, tApexTo.Y);
            SettleFrames(80);
            Check("三角形：拖顶点时模型没动（Revision 不变、走快路）",
                  t2.Revision == tRev && VertexDragging,
                  $"Revision {tRev} → {t2.Revision}，拖元素中={VertexDragging}");
            Check("三角形：拖动中内容层一帧都不重画", _windows[0].LastPatchCount == 0,
                  $"上一帧光栅化分块 {_windows[0].LastPatchCount} 块");
            Check("三角形：这一拖没吸（目标离三种约束都远）",
                  ShapeSnapKind == ShapeSnapKind.None, $"吸到={ShapeSnapKind}");
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check("三角形：拖一个顶点只动它（另两个顶点**逐位**不变）",
                  tookApex
                  && t2.Points[1].X == p1x && t2.Points[1].Y == p1y
                  && t2.Points[2].X == p2x && t2.Points[2].Y == p2y
                  && Near(Pt(t2.Points[0]).X, tApexTo.X, 1f)
                  && Near(Pt(t2.Points[0]).Y, tApexTo.Y, 1f),
                  $"顶点0 ({Pt(t2.Points[0]).X:F1},{Pt(t2.Points[0]).Y:F1}) 期望 ({tApexTo.X:F0},{tApexTo.Y:F0})，"
                  + $"顶点1 ({t2.Points[1].X:F1},{t2.Points[1].Y:F1})，"
                  + $"顶点2 ({t2.Points[2].X:F1},{t2.Points[2].Y:F1})");
            Check("三角形：一次拖顶点 = 一步撤销",
                  Doc.UndoDepth == tUndo + 1, $"撤销栈 {tUndo} → {Doc.UndoDepth} 步");
            Doc.Undo();
            SettleFrames(150);
            Check("三角形：撤销把那个顶点放回原位（逐位）",
                  t2.Points[0].X == p0x && t2.Points[0].Y == p0y,
                  $"顶点0 ({t2.Points[0].X:F3},{t2.Points[0].Y:F3}) "
                  + $"期望 ({p0x:F3},{p0y:F3})");
        }

        // ================= L. 平行四边形：三个顶点 / 第四个现推 / 紧框 / 命中 =================
        //
        // 规格 9.5：`Points` = 三个顶点，**第四个 = 第2 + 第3 − 第1**（自动推导、不存）、
        // 一按一拖出来的是"底边水平、上边右移 1/4 宽"、手柄只有三个（第四个角不给）、
        // 紧框 = **四个顶点**的外接。
        Console.WriteLine("  -- L. 平行四边形 --");
        {
            bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

            // ---- 画一个平行四边形（真机）----
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Parallelogram);
            float pax = _virtualX + 700, pay = _virtualY + 500;
            float pbx = pax + 400, pby = pay + 300;
            float pw = pbx - pax;
            SendMouse((int)pax, (int)pay, 0);                            SettleFrames(60);
            SendMouse((int)pax, (int)pay, Native.MOUSEEVENTF_LEFTDOWN);  SettleFrames(60);
            for (int i = 1; i <= 4; i++)
            {
                SendMouse((int)(pax + (pbx - pax) * i / 4f), (int)(pay + (pby - pay) * i / 4f), 0);
                SettleFrames(30);
            }
            SendMouse((int)pbx, (int)pby, Native.MOUSEEVENTF_LEFTUP);    SettleFrames(220);

            var par = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            Check("平行四边形：拖出一个平行四边形对象",
                  par != null && par.Kind == StrokeKind.Parallelogram,
                  par == null ? "没有对象" : $"Kind={par.Kind}");
            if (par != null)
            {
                Check("平行四边形：控制点是**三个**（第四个不存）",
                      par.Points.Count == 3, $"点数 {par.Points.Count}");
                Check("平行四边形：三个顶点 = 底左 / 底右 / 顶左（上边右移 1/4 宽，±1 像素）",
                      Near(Pt(par.Points[0]).X, pax, 1f) && Near(Pt(par.Points[0]).Y, pby, 1f)
                      && Near(Pt(par.Points[1]).X, pbx - pw * 0.25f, 1f)
                      && Near(Pt(par.Points[1]).Y, pby, 1f)
                      && Near(Pt(par.Points[2]).X, pax + pw * 0.25f, 1f)
                      && Near(Pt(par.Points[2]).Y, pay, 1f),
                      $"底左 ({Pt(par.Points[0]).X:F1},{Pt(par.Points[0]).Y:F1})"
                      + $" 底右 ({Pt(par.Points[1]).X:F1},{Pt(par.Points[1]).Y:F1})"
                      + $" 顶左 ({Pt(par.Points[2]).X:F1},{Pt(par.Points[2]).Y:F1})"
                      + $" 期望 ({pax:F0},{pby:F0})/({pbx - pw * 0.25f:F0},{pby:F0})/({pax + pw * 0.25f:F0},{pay:F0})");
                // 第四个顶点：**两条路各算一遍**（公式 / 外框右上角），两条都得对上
                var f4 = par.ParallelogramFourthLocal();
                Check("平行四边形：第四点逐位 = 第2 + 第3 − 第1（并且正好是外框右上角）",
                      f4.X == par.Points[1].X + par.Points[2].X - par.Points[0].X
                      && f4.Y == par.Points[1].Y + par.Points[2].Y - par.Points[0].Y
                      && Near(f4.X, pbx, 1f) && Near(f4.Y, pay, 1f),
                      $"第四点 ({f4.X:F1},{f4.Y:F1})，外框右上角 ({pbx:F0},{pay:F0})");
                Check("平行四边形：一次拖拽 = 一步撤销", Doc.UndoDepth == 1, $"撤销栈 {Doc.UndoDepth} 步");
                Doc.Undo();
                SettleFrames(120);
                Check("平行四边形：撤销一次就干净", Doc.Strokes.Count == 0, $"对象 {Doc.Strokes.Count} 个");
            }

            // ---- 造一个已知的平行四边形：手柄 / 第四个角没有手柄 / 紧框 / 命中 ----
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var q2 = new Stroke
            {
                Tool = Tool.Parallelogram, Kind = StrokeKind.Parallelogram,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            q2.AddPoint(lx, ly, 1f, 0);                 // 底左
            q2.AddPoint(lx + 300f, ly, 1f, 0);          // 底右
            q2.AddPoint(lx + 75f, ly - 200f, 1f, 0);    // 顶左
            Doc.AddStroke(q2);
            Doc.SelectOnly(new[] { q2 });
            SettleFrames(250);

            Span<ShapeHandle> qHandles = stackalloc ShapeHandle[5];
            int qn = SelectionHandles.ShapeHandlesOf(q2, qHandles);
            var q4 = q2.ParallelogramFourthLocal();
            var qFrame = SelectionHandles.FrameOf(Doc.Selected);
            var qRot = SelectionHandles.CanvasPosition(SelHandle.Rotate, qFrame, DpiScale);
            Check("平行四边形：手柄只有三个（第四个角不给手柄，用户定）",
                  qn == 3 && qHandles[0] == ShapeHandle.Vertex0
                  && qHandles[1] == ShapeHandle.Vertex1 && qHandles[2] == ShapeHandle.Vertex2
                  && SelectionHandles.HitTest(q4.X, q4.Y, Doc.Selected, qFrame, DpiScale)
                      == SelHandle.None,
                  $"手柄数 {qn}，在第四点 ({q4.X:F0},{q4.Y:F0}) 上按一下 → "
                  + $"{SelectionHandles.HitTest(q4.X, q4.Y, Doc.Selected, qFrame, DpiScale)}（期望 None）");
            Check("平行四边形：给旋转柄", SelectionHandles.RotateHandleVisible(q2)
                  && SelectionHandles.HitTest(qRot.X, qRot.Y, Doc.Selected, qFrame, DpiScale)
                      == SelHandle.Rotate,
                  $"旋转柄={SelectionHandles.RotateHandleVisible(q2)}");

            // 紧框 = **四个顶点**的外接（＋半笔宽）：第四个点在右上，少了它框会小一截
            float qhw = q2.Width * 0.5f;
            var wantQ = new RectF
            {
                MinX = lx - qhw, MinY = ly - 200f - qhw,
                MaxX = lx + 375f + qhw, MaxY = ly + qhw,
            };
            var gotQ = q2.WorldInkBounds;
            Check("平行四边形：紧框 = **四个顶点**的外接（含推导出来的那个，±0.5 像素）",
                  Near(gotQ.MinX, wantQ.MinX, 0.5f) && Near(gotQ.MinY, wantQ.MinY, 0.5f)
                  && Near(gotQ.MaxX, wantQ.MaxX, 0.5f) && Near(gotQ.MaxY, wantQ.MaxY, 0.5f),
                  $"框 ({gotQ.MinX:F1},{gotQ.MinY:F1})-({gotQ.MaxX:F1},{gotQ.MaxY:F1})"
                  + $" 期望 ({wantQ.MinX:F1},{wantQ.MinY:F1})-({wantQ.MaxX:F1},{wantQ.MaxY:F1})"
                  + $"（右上那个角就是第四点 {q4.X:F0},{q4.Y:F0}）");

            var qEdgeMid = new Vector2(lx + 150f, ly);                 // 底边中点
            var qInside = new Vector2((lx + lx + 300f + lx + 75f + q4.X) / 4f,
                                      (ly + ly + ly - 200f + q4.Y) / 4f);
            Check("平行四边形：边上命中、内部不命中（只认描边）",
                  q2.HitTestExact(qEdgeMid.X, qEdgeMid.Y) && !q2.HitTestExact(qInside.X, qInside.Y),
                  $"底边中点 {q2.HitTestExact(qEdgeMid.X, qEdgeMid.Y)}，"
                  + $"内部 {q2.HitTestExact(qInside.X, qInside.Y)}");

            // ---- 拖一个顶点：只动它，第四个角**仍逐位等于公式** ----
            var qV1 = SelectionHandles.ShapeHandleCanvasPosition(q2, ShapeHandle.Vertex1);
            float q0x = q2.Points[0].X, q0y = q2.Points[0].Y;
            float q2x = q2.Points[2].X, q2y = q2.Points[2].Y;
            int qRev = q2.Revision, qUndo = Doc.UndoDepth;
            var qV1To = new Vector2(lx + 380f, ly + 30f);   // 离菱形 / 矩形都远（见 M 段的摆位账）
            bool tookQ = SelectionGestureForTest(qV1.X, qV1.Y);
            UpdateSelectionGestureForTest(qV1To.X, qV1To.Y);
            SettleFrames(80);
            Check("平行四边形：拖顶点时模型没动（Revision 不变、走快路）",
                  q2.Revision == qRev && VertexDragging,
                  $"Revision {qRev} → {q2.Revision}，拖元素中={VertexDragging}");
            Check("平行四边形：拖动中内容层一帧都不重画", _windows[0].LastPatchCount == 0,
                  $"上一帧光栅化分块 {_windows[0].LastPatchCount} 块");
            EndSelectionGestureForTest();
            SettleFrames(200);
            var q4After = q2.ParallelogramFourthLocal();
            Check("平行四边形：拖任一顶点后，第四个顶点仍**逐位**等于公式",
                  tookQ && q2.Points.Count == 3
                  && q4After.X == q2.Points[1].X + q2.Points[2].X - q2.Points[0].X
                  && q4After.Y == q2.Points[1].Y + q2.Points[2].Y - q2.Points[0].Y
                  && q4After.X == q4.X + (q2.Points[1].X - (lx + 300f))
                  && q4After.Y == q4.Y + (q2.Points[1].Y - ly),
                  $"第四个点 ({q4.X:F1},{q4.Y:F1}) → ({q4After.X:F1},{q4After.Y:F1})"
                  + $"（右下角挪了 ({q2.Points[1].X - (lx + 300f):F1},{q2.Points[1].Y - ly:F1})，"
                  + $"第四点跟着平移了同样的量）");
            Check("平行四边形：拖一个顶点只动它（另两个逐位不变）",
                  q2.Points[0].X == q0x && q2.Points[0].Y == q0y
                  && q2.Points[2].X == q2x && q2.Points[2].Y == q2y
                  && Near(q2.Points[1].X, qV1To.X, 1f) && Near(q2.Points[1].Y, qV1To.Y, 1f),
                  $"底右 ({q2.Points[1].X:F1},{q2.Points[1].Y:F1}) 期望 ({qV1To.X:F0},{qV1To.Y:F0})，"
                  + $"底左 ({q2.Points[0].X:F1},{q2.Points[0].Y:F1})，"
                  + $"顶左 ({q2.Points[2].X:F1},{q2.Points[2].Y:F1})");
            Check("平行四边形：一次拖顶点 = 一步撤销", Doc.UndoDepth == qUndo + 1,
                  $"撤销栈 {qUndo} → {Doc.UndoDepth} 步");
            Doc.Undo();
            SettleFrames(150);
        }

        // ================= L2. 读数：三角形三个内角 / 平行四边形两个夹角（规格 9.7）=================
        //
        // **两个图形都只在"拖顶点时"显示**（三角形原来"选中就显示"，用户 2026-09-20 定：
        // "应该在拖动的时候再显示角度，要不然看起来也乱"），拖的时候实时更新。
        // **不再显示"内角和"那一行**（用户 2026-09-19 定：显示和太乱），所以为它服务的
        // 0.1° 配平也撤了——三个角**各自独立**取值，没有谁为了凑 180.0 被动过 0.1°。
        //
        // 这一节顺带把"**绘制与脏区同源**"验掉：浮动层是**按脏区裁剪**画的，所以
        //   · 标签上屏了 ⟹ 那块脏区确实算上了它；
        //   · 松手之后同一块探针窗必须干净 ⟹ 消失那一帧也被算到了（不留残影）。
        // 探针数的是角标胶囊的**底色**（默认主题 Panel 盖在深色白板上 ≈ (236,236,239)）。
        Console.WriteLine("  -- L2. 三角形内角 / 平行四边形夹角（读数）--");
        {
            bool Near(float a, float b, float t) => MathF.Abs(a - b) <= t;
            Span<Vector2> av = stackalloc Vector2[4];
            Span<float> ad = stackalloc float[3];
            int PanelPixels(int x, int y, int w, int h)
                => ScreenProbe.CountNear(x, y, w, h, 236, 236, 239, 26);
            // 两颗角标的探针窗：左顶点取它左边一截、右顶点取它右边一截（角标就挂在那儿）
            int LeftPill(float vx, float vy) => PanelPixels((int)(vx - 90f * DpiScale),
                (int)(vy - 14f * DpiScale), (int)(60f * DpiScale), (int)(28f * DpiScale));
            int RightPill(float vx, float vy) => PanelPixels((int)(vx + 30f * DpiScale),
                (int)(vy - 14f * DpiScale), (int)(60f * DpiScale), (int)(28f * DpiScale));

            // ---- 数学层：一个**已知角度**的 30/60/90 三角形 ----
            // 顶点0 = 直角，顶点1 处 30°，顶点2 处 60°（高 = 600·tan30° ≈ 346.41）。
            var T0 = new Vector2(lx, ly);
            var T1 = new Vector2(lx + 600f, ly);
            var T2 = new Vector2(lx, ly - 346.4102f);
            Span<Vector2> tri3 = stackalloc Vector2[3];
            tri3[0] = T0; tri3[1] = T1; tri3[2] = T2;
            Span<float> triDeg = stackalloc float[3];
            int triN = SelectionHandles.PolygonAngles(StrokeKind.Triangle, tri3, triDeg);
            Check("内角·数学层：30/60/90 的三角形 → 读数 90 / 30 / 60（±0.1°）",
                  triN == 3 && Near(triDeg[0], 90f, 0.1f) && Near(triDeg[1], 30f, 0.1f)
                  && Near(triDeg[2], 60f, 0.1f),
                  $"读数 {triDeg[0]:F2}° / {triDeg[1]:F2}° / {triDeg[2]:F2}°");
            // **配平撤掉了**（用户 2026-09-19 定：不再显示那一行"内角和"）：每个角都必须是
            // "那个角本身"，不许为了凑一行 180.0 被动过 0.1°。用一个**真值不在 0.1° 网格上**
            // 的三角形盯住它——配平过的话，三个数会变成 0.1° 的整数倍，这里当场不等。
            var S0 = new Vector2(0f, 0f);
            var S1 = new Vector2(300f, 0f);
            var S2 = new Vector2(-120f, 211.36f);          // 故意摆歪：三个角都不是整齐数
            Span<Vector2> triV = stackalloc Vector2[3];
            triV[0] = S0; triV[1] = S1; triV[2] = S2;
            Span<float> triRaw = stackalloc float[3];
            int triRawN = SelectionHandles.PolygonAngles(StrokeKind.Triangle, triV, triRaw);
            float e0 = SelectionHandles.AngleBetweenDegrees(S1 - S0, S2 - S0);
            float e1 = SelectionHandles.AngleBetweenDegrees(S2 - S1, S0 - S1);
            float e2 = SelectionHandles.AngleBetweenDegrees(S0 - S2, S1 - S2);
            Check("内角·数学层：每个角都取**它自己**（独立算一遍，逐位一致 = 没配平）",
                  triRawN == 3 && triRaw[0] == e0 && triRaw[1] == e1 && triRaw[2] == e2,
                  $"{triRaw[0]:F4} / {triRaw[1]:F4} / {triRaw[2]:F4}"
                  + $" vs 独立算 {e0:F4} / {e1:F4} / {e2:F4}");
            Check("内角·数学层：三个角之和 = 180（三角形的性质，不是凑出来的，±0.01°）",
                  Near(triRaw[0] + triRaw[1] + triRaw[2], 180f, 0.01f),
                  $"和 = {triRaw[0] + triRaw[1] + triRaw[2]:F4}°");

            // ---- 数学层：平行四边形两个夹角（互补）----
            Span<Vector2> par4 = stackalloc Vector2[4];
            par4[0] = new Vector2(lx, ly);
            par4[1] = new Vector2(lx + 400f, ly);
            par4[2] = new Vector2(lx + 100f, ly - 260f);
            par4[3] = Stroke.ParallelogramFourth(par4[0], par4[1], par4[2]);
            Span<float> parDeg = stackalloc float[2];
            int parN = SelectionHandles.PolygonAngles(StrokeKind.Parallelogram, par4, parDeg);
            float w0 = SelectionHandles.AngleBetweenDegrees(par4[1] - par4[0], par4[2] - par4[0]);
            float w1 = SelectionHandles.AngleBetweenDegrees(par4[0] - par4[1], par4[3] - par4[1]);
            Check("夹角·数学层：两个角 = 那两条边张成的角（独立算一遍，±0.01°）",
                  parN == 2 && Near(parDeg[0], w0, 0.01f) && Near(parDeg[1], w1, 0.01f),
                  $"读数 {parDeg[0]:F2}° / {parDeg[1]:F2}°，独立算 {w0:F2}° / {w1:F2}°");
            Check("夹角·数学层：两个数互补（和 = 180 ±0.1°）",
                  Near(parDeg[0] + parDeg[1], 180f, 0.1f),
                  $"{parDeg[0]:F2} + {parDeg[1]:F2} = {parDeg[0] + parDeg[1]:F3}");

            // ---- 真机：三角形**也改成"只在拖顶点时显示"**（用户 2026-09-20 定）----
            //
            // 用户原话："三角形应该在拖动的时候再显示角度，要不然看起来也乱。"
            // 在那之前它是"一选中就挂三颗角标"，静止摆着也一直显示。
            // 所以这一节四条按顺序钉住：
            //   ① 静止选中 → 一个角都不显示、屏幕上也没有角标；
            //   ② **按住顶点**（还没移动）→ 三个角立刻出来，而且是这个三角形的真值 90/30/60；
            //   ③ 继续拖 → 读数实时变；④ 松手 → 立刻收回去、不留残影；
            //   ⑤ 一次拖顶点仍然只是一步撤销。
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            var rt = new Stroke
            {
                Tool = Tool.Triangle, Kind = StrokeKind.Triangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            rt.AddPoint(T0.X, T0.Y, 1f, 0);
            rt.AddPoint(T1.X, T1.Y, 1f, 0);
            rt.AddPoint(T2.X, T2.Y, 1f, 0);
            Doc.AddStroke(rt);
            Doc.SelectOnly(new[] { rt });
            SettleFrames(360);

            // ① 静止选中：读数一个都没有，三颗角标也都不在屏幕上
            Check("内角·真机：**选中静止时一个角都不显示**（用户 2026-09-20 定：拖动时才显示）",
                  FillAngleReadout(av, ad) == 0, $"读数个数 {FillAngleReadout(av, ad)}");
            int idleLeft0 = LeftPill(T0.X, T0.Y);
            int idleRight = RightPill(T1.X, T1.Y);
            int idleLeft2 = LeftPill(T2.X, T2.Y);
            Check("内角·真机：静止选中时三颗角标都不在屏幕上（不是\"画了但看不见\"）",
                  idleLeft0 < 30 && idleRight < 30 && idleLeft2 < 30,
                  $"左下 {idleLeft0} 像素、右 {idleRight} 像素、上 {idleLeft2} 像素");

            // ② 按住上顶点、**先别挪**：按住那一刻三个角就该出来
            var triApex = SelectionHandles.ShapeHandleCanvasPosition(rt, ShapeHandle.Vertex2);
            bool tookTri = SelectionGestureForTest(triApex.X, triApex.Y);
            SettleFrames(160);
            int rn = FillAngleReadout(av, ad);
            Check("内角·真机：**按住顶点就显示**三个内角（90 / 30 / 60，±0.1°）",
                  tookTri && VertexDragging && rn == 3
                  && Near(ad[0], 90f, 0.1f) && Near(ad[1], 30f, 0.1f) && Near(ad[2], 60f, 0.1f),
                  $"接住={tookTri}，拖元素中={VertexDragging}，"
                  + $"读数 {ad[0]:F2} / {ad[1]:F2} / {ad[2]:F2}");
            // 三颗角标：左下的角挂在它左边、右边的角挂在它右边、上面的角也挂左边
            //（左右由"顶点在形心的哪一侧"决定，见 FillAnglePills）。
            int triLeft0 = LeftPill(T0.X, T0.Y);
            int triRight = RightPill(T1.X, T1.Y);
            int triLeft2 = LeftPill(T2.X, T2.Y);
            // **形心那一块正是以前印"内角和"那一行的地方**（用户 2026-09-19 定：不显示了）。
            // 所以这里反过来量：它必须是干净的——这一条盯住"那行真的没了"。
            var triCentroid = new Vector2((T0.X + T1.X + T2.X) / 3f, (T0.Y + T1.Y + T2.Y) / 3f);
            int triCenter = PanelPixels((int)(triCentroid.X - 60f * DpiScale),
                (int)(triCentroid.Y - 14f * DpiScale), (int)(120f * DpiScale), (int)(28f * DpiScale));
            Check("内角·真机：三颗角标都上了屏（脏区确实把每一颗都算上了）",
                  triLeft0 > 150 && triRight > 150 && triLeft2 > 150,
                  $"左下 {triLeft0} 像素、右 {triRight} 像素、上 {triLeft2} 像素");
            Check("内角·真机：形心那一块**没有**\"内角和\"那一行（用户 2026-09-19 定）",
                  triCenter < 30, $"形心窗口 {triCenter} 像素（期望 <30）");

            // ③ 继续拖 → 读数实时变（每个角各自独立，谁都不为凑和被动过）
            float b0 = ad[0], b1 = ad[1], b2 = ad[2];
            var triApexTo = new Vector2(T2.X + 260f, T2.Y - 140f);
            UpdateSelectionGestureForTest(triApexTo.X, triApexTo.Y);
            SettleFrames(140);
            int dn = FillAngleReadout(av, ad);
            Check("内角·真机：拖顶点时读数**实时变**（还是三个）",
                  VertexDragging && dn == 3
                  && (MathF.Abs(ad[0] - b0) > 1f || MathF.Abs(ad[1] - b1) > 1f
                      || MathF.Abs(ad[2] - b2) > 1f),
                  $"{b0:F1}/{b1:F1}/{b2:F1} → {ad[0]:F1}/{ad[1]:F1}/{ad[2]:F1}");
            Check("内角·真机：拖完三个角仍是各自独立的值（和 = 180 是三角形的性质，±0.02°）",
                  Near(ad[0] + ad[1] + ad[2], 180f, 0.02f), $"和 = {ad[0] + ad[1] + ad[2]:F4}°");
            // 拖动中把整块都探一遍：角标跟着顶点走，走过的路上不许有残留
            var dragBox = RectF.Empty;
            dragBox.Add(T0.X, T0.Y);
            dragBox.Add(T2.X - 60f, T2.Y - 60f);
            dragBox.Add(triApexTo.X + 160f, triApexTo.Y + 60f);
            dragBox.Add(T1.X + 160f, T1.Y + 60f);
            int dragResidue = PanelPixels((int)dragBox.MinX, (int)dragBox.MinY,
                                          (int)(dragBox.MaxX - dragBox.MinX),
                                          (int)(dragBox.MaxY - dragBox.MinY));
            Check("内角·真机：拖动中整块探针里的胶囊像素 = 那几颗角标（没有额外的残影）",
                  dragResidue > 300, $"整块里 {dragResidue} 像素（三颗角标）");

            // ④ 松手 = 角标立刻收回去（这一条正是这次改动的核心）
            EndSelectionGestureForTest();
            SettleFrames(260);
            Check("内角·真机：**松手就不显示**（读数个数 = 0）",
                  !VertexDragging && FillAngleReadout(av, ad) == 0,
                  $"拖元素中={VertexDragging}，读数个数 {FillAngleReadout(av, ad)}");
            int goneLeft0 = LeftPill(T0.X, T0.Y);      // 这两个角没被拖，还在原位
            int goneRight = RightPill(T1.X, T1.Y);
            Check("内角·真机：松手后角标真的从屏幕消失（不留残影）",
                  goneLeft0 < 30 && goneRight < 30,
                  $"还剩 左下 {goneLeft0} 像素、右 {goneRight} 像素");

            // ⑤ 一次拖顶点 = 一步撤销
            Check("内角·真机：一次拖顶点 = 一步撤销",
                  Doc.UndoDepth >= 2, $"撤销栈 {Doc.UndoDepth} 步");
            Doc.Undo();
            SettleFrames(150);

            // ---- 真机：平行四边形只**拖顶点时**显示两个夹角 ----
            Doc.Clear();
            Doc.ClearHistory();
            var rp = new Stroke
            {
                Tool = Tool.Parallelogram, Kind = StrokeKind.Parallelogram,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            rp.AddPoint(par4[0].X, par4[0].Y, 1f, 0);
            rp.AddPoint(par4[1].X, par4[1].Y, 1f, 0);
            rp.AddPoint(par4[2].X, par4[2].Y, 1f, 0);
            Doc.AddStroke(rp);
            Doc.SelectOnly(new[] { rp });
            SettleFrames(320);
            Check("夹角·真机：**选中静止时两个角都不显示**（规格只要求拖顶点时显示）",
                  FillAngleReadout(av, ad) == 0, $"读数个数 {FillAngleReadout(av, ad)}");
            var qv1 = SelectionHandles.ShapeHandleCanvasPosition(rp, ShapeHandle.Vertex1);
            bool tookQ1 = SelectionGestureForTest(qv1.X, qv1.Y);
            UpdateSelectionGestureForTest(qv1.X + 45f, qv1.Y + 30f);
            SettleFrames(120);
            int qn = FillAngleReadout(av, ad);
            float q0 = SelectionHandles.AngleBetweenDegrees(av[1] - av[0], av[2] - av[0]);
            float q1 = SelectionHandles.AngleBetweenDegrees(av[0] - av[1], av[3] - av[1]);
            Check("夹角·真机：拖顶点时出现**两个**角，且 = 那两条边张成的角（±0.01°）",
                  tookQ1 && VertexDragging && qn == 2
                  && Near(ad[0], q0, 0.01f) && Near(ad[1], q1, 0.01f),
                  $"读数 {ad[0]:F2}° / {ad[1]:F2}°，独立算 {q0:F2}° / {q1:F2}°");
            Check("夹角·真机：两个数互补（和 = 180 ±0.1°）",
                  Near(ad[0] + ad[1], 180f, 0.1f), $"{ad[0]:F2} + {ad[1]:F2} = {ad[0] + ad[1]:F3}");
            int qLeft = LeftPill(av[0].X, av[0].Y);
            int qRight = RightPill(av[1].X, av[1].Y);
            Check("夹角·真机：两颗角标都上了屏（贴在被拖顶点之外的左右两侧）",
                  qLeft > 150 && qRight > 150, $"左 {qLeft} 像素，右 {qRight} 像素");
            EndSelectionGestureForTest();
            SettleFrames(360);
            int qLeftGone = LeftPill(par4[0].X, par4[0].Y);
            int qRightGone = RightPill(par4[1].X, par4[1].Y);
            Check("夹角·真机：松手后两颗角标都消失（不留残影）",
                  FillAngleReadout(av, ad) == 0 && qLeftGone < 30 && qRightGone < 30,
                  $"读数个数 {FillAngleReadout(av, ad)}，左 {qLeftGone} 像素，右 {qRightGone} 像素");
            Doc.Undo();
            SettleFrames(150);
        }

        // ================= M. 一族"特殊形状"吸附（规格 9.6）=================
        //
        // 三角形 → 等腰 / 等边 / 直角；矩形 → 正方形；椭圆 → 正圆；平行四边形 → 菱形 / 矩形。
        // 每种约束都验三档：**正例**、**容差边界（刚好在外面的不吸）**、**Alt 自由**；
        // 外加"整体移动 / 旋转时不会吸"。
        //
        // 两条腿都验：数学层直接调权威函数（容差边界与 Alt 只能在这一层验——Alt 要真按
        // 键盘，注入不了），真机层真拖一次手柄、证明它确实接在那条路上。
        // 摆位一律按**容差的比例**（tol = 2 逻辑像素 × dpi），这样在任何 DPI 的机器上语义一样。
        Console.WriteLine("  -- M. 一族\"特殊形状\"吸附 --");
        {
            bool Near(float a, float b, float t) => MathF.Abs(a - b) <= t;
            float tol = SelectionHandles.ShapeSnapLengthToleranceLogical * DpiScale;
            float angTol = SelectionHandles.ShapeSnapAngleToleranceDegrees;

            // 三角形的两个固定顶点：底边水平、长 200（被拖的第三个点另行给）
            var A = new Vector2(lx, ly);
            var B = new Vector2(lx + 200f, ly);
            float midX = lx + 100f;
            float D(Vector2 p, Vector2 q) => Vector2.Distance(p, q);

            // ---- 三角形 · 等腰 ----
            var isoV = new Vector2(midX + tol * 0.25f, ly - 150f);
            float isoDiff = MathF.Abs(D(isoV, A) - D(isoV, B));
            var isoGot = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, isoV, A, B,
                                                            isoV, tol, false, out var isoSnap);
            Check("三角形·等腰：两边长差在容差内 → 修正到中垂线上（两腰**精确**相等）",
                  isoSnap == ShapeSnapKind.Isosceles && Near(isoGot.X, midX, 0.01f)
                  && Near(isoGot.Y, isoV.Y, 0.01f) && Near(D(isoGot, A), D(isoGot, B), 1e-3f),
                  $"摆位时两边差 {isoDiff:F2}（容差 {tol:F1}）→ 吸到「{SelectionHandles.ShapeSnapLabel(isoSnap)}」，"
                  + $"顶点 ({isoGot.X:F2},{isoGot.Y:F2}) 期望 x={midX:F2}，"
                  + $"两腰 {D(isoGot, A):F4} / {D(isoGot, B):F4}");

            var isoV2 = new Vector2(midX + tol * 1.5f, ly - 150f);
            float isoDiff2 = MathF.Abs(D(isoV2, A) - D(isoV2, B));
            var isoGot2 = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, isoV2, A, B,
                                                             isoV2, tol, false, out var isoSnap2);
            Check("三角形·等腰：差得**刚好在容差外** → 不吸（手柄不粘手）",
                  isoDiff2 > tol && isoDiff2 < tol * 2f
                  && isoSnap2 == ShapeSnapKind.None && isoGot2 == isoV2,
                  $"两边差 {isoDiff2:F2} > 容差 {tol:F1} → 吸到={isoSnap2}，"
                  + $"顶点原样 ({isoGot2.X:F2},{isoGot2.Y:F2})");

            var isoAlt = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, isoV, A, B,
                                                            isoV, tol, true, out var isoSnap3);
            Check("三角形·等腰：**Alt 一律自由**（同样的位置，不吸）",
                  isoSnap3 == ShapeSnapKind.None && isoAlt == isoV,
                  $"Alt 下吸到={isoSnap3}，顶点原样 ({isoAlt.X:F2},{isoAlt.Y:F2})");

            // ---- 三角形 · 等边 ----
            var eqH = 100f * MathF.Sqrt(3f);                       // 边长 200 的正三角形的高
            var eqV = new Vector2(midX + tol * 0.25f, ly - eqH);
            var eqGot = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, eqV, A, B,
                                                           eqV, tol, false, out var eqSnap);
            Check("三角形·等边：三边都在容差内 → 修正成**精确**正三角形",
                  eqSnap == ShapeSnapKind.Equilateral
                  && Near(eqGot.X, midX, 0.01f) && Near(eqGot.Y, ly - eqH, 0.01f)
                  && Near(D(eqGot, A), 200f, 1e-3f) && Near(D(eqGot, B), 200f, 1e-3f)
                  && Near(D(A, B), 200f, 1e-3f),
                  $"摆位：三边 {D(eqV, A):F2} / {D(eqV, B):F2} / {D(A, B):F2}（容差 {tol:F1}）"
                  + $"→ 吸到「{SelectionHandles.ShapeSnapLabel(eqSnap)}」，"
                  + $"修正后三边 {D(eqGot, A):F4} / {D(eqGot, B):F4} / {D(A, B):F4}");
            // 等边一定也满足"两腰相等"：优先级必须是等边（反了会把好形状改成只等腰）
            Check("三角形·等边 优先于 等腰（同一个位置不会报成等腰）",
                  eqSnap == ShapeSnapKind.Equilateral, $"吸到={eqSnap}");

            // 等边的容差边界：把顶点沿底边挪出去，让两条斜边一条超差、一条欠差
            // （等边要求**三条边**都在容差内，所以挪 2 倍容差就出界了）
            var eqV2 = new Vector2(midX + tol * 2f, ly - eqH);
            float eqDev = MathF.Max(MathF.Abs(D(eqV2, A) - 200f), MathF.Abs(D(eqV2, B) - 200f));
            var eqGot2 = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, eqV2, A, B,
                                                            eqV2, tol, false, out var eqSnap2);
            Check("三角形·等边：三边差得**刚好在容差外** → 不吸",
                  eqDev > tol && eqDev < tol * 2f && eqSnap2 == ShapeSnapKind.None && eqGot2 == eqV2,
                  $"摆位：离 200 最远的那条边差 {eqDev:F2}（容差 {tol:F1}）→ 吸到={eqSnap2}，顶点原样");
            var eqAlt = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, eqV, A, B,
                                                           eqV, tol, true, out var eqSnap3);
            Check("三角形·等边：**Alt 一律自由**",
                  eqSnap3 == ShapeSnapKind.None && eqAlt == eqV,
                  $"Alt 下吸到={eqSnap3}，顶点原样");

            // ---- 三角形 · 直角（被拖的那个角）----
            // 直角顶点在以 AB 为直径的圆上（泰勒斯）：取 60° 那个位置，
            // 它**不在**中垂线上（所以不会先被等腰抢走）
            var rtV = new Vector2(midX + 50f, ly - 100f * MathF.Sin(60f * MathF.PI / 180f));
            float rtAng = SelectionHandles.AngleBetweenDegrees(A - rtV, B - rtV);
            var rtGot = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, rtV, A, B,
                                                           rtV, tol, false, out var rtSnap);
            float rtAngAfter = SelectionHandles.AngleBetweenDegrees(A - rtGot, B - rtGot);
            Check("三角形·直角：内角在 1° 内 → 修正到**恰好 90°**（被拖的那个角）",
                  rtSnap == ShapeSnapKind.RightAngle && MathF.Abs(rtAngAfter - 90f) < 1e-3f,
                  $"摆位时内角 {rtAng:F4}°（容差 {angTol:F0}°）→ 吸到「"
                  + $"{SelectionHandles.ShapeSnapLabel(rtSnap)}」，修正后 {rtAngAfter:F4}°，"
                  + $"顶点挪了 {D(rtGot, rtV):F4} 像素");
            // 容差边界：沿半径缩到 98%（同一条射线上），内角变成 ~91.3°（刚好在 1° 外）
            var rtV2 = new Vector2(midX + 49f, ly - 98f * MathF.Sin(60f * MathF.PI / 180f));
            float rtAng2 = SelectionHandles.AngleBetweenDegrees(A - rtV2, B - rtV2);
            float rtDiff2 = MathF.Abs(D(rtV2, A) - D(rtV2, B));
            var rtGot2 = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, rtV2, A, B,
                                                            rtV2, tol, false, out var rtSnap2);
            Check("三角形·直角：内角差得**刚好在容差外**（约 91.3°）→ 不吸",
                  MathF.Abs(rtAng2 - 90f) > angTol && MathF.Abs(rtAng2 - 90f) < angTol * 2f
                  && rtDiff2 > tol        // 顺带确认：也没被等腰抢走
                  && rtSnap2 == ShapeSnapKind.None && rtGot2 == rtV2,
                  $"摆位时内角 {rtAng2:F4}°（离 90° 差 {MathF.Abs(rtAng2 - 90f):F3}° > {angTol:F0}°），"
                  + $"两边差 {rtDiff2:F2} > 容差 {tol:F1} → 吸到={rtSnap2}，顶点原样");
            var rtAlt = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, rtV, A, B,
                                                           rtV, tol, true, out var rtSnap3);
            Check("三角形·直角：**Alt 一律自由**",
                  rtSnap3 == ShapeSnapKind.None && rtAlt == rtV,
                  $"Alt 下吸到={rtSnap3}，顶点原样");

            // ---- 三角形 · 直角（**固定的**那个角）----
            // 角在底左（A）上：被拖的点要落在"过 A 且垂直于 AB"的直线上
            var rtF = new Vector2(lx + tol * 0.1f, ly - 100f);
            float rtAngA = SelectionHandles.AngleBetweenDegrees(rtF - A, B - A);
            var rtFGot = SelectionHandles.SnapPolygonVertex(StrokeKind.Triangle, 0, rtF, A, B,
                                                            rtF, tol, false, out var rtFSnap);
            float rtAngA2 = SelectionHandles.AngleBetweenDegrees(rtFGot - A, B - A);
            Check("三角形·直角：直角在**另一个（固定的）顶点**上也能修（修正只动被拖的点）",
                  rtFSnap == ShapeSnapKind.RightAngle && Near(rtFGot.X, A.X, 0.01f)
                  && MathF.Abs(rtAngA2 - 90f) < 1e-3f,
                  $"摆位时 A 处内角 {rtAngA:F4}° → 吸到「{SelectionHandles.ShapeSnapLabel(rtFSnap)}」，"
                  + $"修正后 {rtAngA2:F4}°，被拖的点 ({rtFGot.X:F2},{rtFGot.Y:F2}) 期望 x={A.X:F2}");

            // ---- 椭圆 · 正圆 ----
            bool eSnap;
            float eIn = SelectionHandles.SnapEllipseAxis(200f + tol * 0.5f, 200f, tol, false, out eSnap);
            Check("椭圆·正圆：|a − b| 在容差内 → 被拖的那条半轴取成另一条（a、b **逐位**相等）",
                  eSnap && eIn == 200f,
                  $"a = 200 + {tol * 0.5f:F1} → 修正成 {eIn:F1}（期望正好 200），吸住={eSnap}");
            bool eSnap2;
            float eOut = SelectionHandles.SnapEllipseAxis(200f + tol * 1.5f, 200f, tol, false, out eSnap2);
            Check("椭圆·正圆：差得刚好在容差外 → 不吸",
                  !eSnap2 && eOut == 200f + tol * 1.5f,
                  $"a = 200 + {tol * 1.5f:F1}（容差 {tol:F1}）→ 不吸，a 保持 {eOut:F2}");
            bool eSnap3;
            float eAlt = SelectionHandles.SnapEllipseAxis(200f + tol * 0.25f, 200f, tol, true, out eSnap3);
            Check("椭圆·正圆：**Alt 一律自由**",
                  !eSnap3 && eAlt == 200f + tol * 0.25f,
                  $"Alt 下 a 保持 {eAlt:F2}，吸住={eSnap3}");

            // ---- 平行四边形 · 菱形 ----
            var P0 = new Vector2(lx, ly);
            var P2 = new Vector2(lx + 60f, ly - 200f);
            float lv = D(P2, P0);                                   // 另一条邻边的长
            var rhV = new Vector2(P0.X + lv + tol * 0.2f, ly);
            var rhGot = SelectionHandles.SnapPolygonVertex(StrokeKind.Parallelogram, 1, P0, rhV, P2,
                                                           rhV, tol, false, out var rhSnap);
            Check("平行四边形·菱形：邻边差在容差内 → 修正到**精确**相等",
                  rhSnap == ShapeSnapKind.Rhombus && Near(rhGot.X, P0.X + lv, 0.01f)
                  && rhGot.Y == ly && Near(D(rhGot, P0), lv, 1e-3f),
                  $"另一条邻边长 {lv:F3}，摆位差 {MathF.Abs(D(rhV, P0) - lv):F2}（容差 {tol:F1}）"
                  + $"→ 吸到「{SelectionHandles.ShapeSnapLabel(rhSnap)}」，"
                  + $"修正后 ({rhGot.X:F3},{rhGot.Y:F3})，邻边 {D(rhGot, P0):F4} / {lv:F4}");

            var rhV2 = new Vector2(P0.X + lv + tol * 1.5f, ly);
            float rhDiff2 = MathF.Abs(D(rhV2, P0) - lv);
            var rhGot2 = SelectionHandles.SnapPolygonVertex(StrokeKind.Parallelogram, 1, P0, rhV2, P2,
                                                            rhV2, tol, false, out var rhSnap2);
            Check("平行四边形·菱形：差得刚好在容差外 → 不吸",
                  rhDiff2 > tol && rhDiff2 < tol * 2f && rhSnap2 == ShapeSnapKind.None && rhGot2 == rhV2,
                  $"邻边差 {rhDiff2:F2} > 容差 {tol:F1} → 吸到={rhSnap2}，顶点原样");
            var rhAlt = SelectionHandles.SnapPolygonVertex(StrokeKind.Parallelogram, 1, P0, rhV, P2,
                                                           rhV, tol, true, out var rhSnap3);
            Check("平行四边形·菱形：**Alt 一律自由**",
                  rhSnap3 == ShapeSnapKind.None && rhAlt == rhV,
                  $"Alt 下吸到={rhSnap3}，顶点原样");

            // ---- 平行四边形 · 矩形（邻边垂直）----
            var raP1 = new Vector2(lx + 200f, ly);
            var raV = new Vector2(lx + tol * 0.1f, ly - 150f);
            float raAng = SelectionHandles.AngleBetweenDegrees(raV - P0, raP1 - P0);
            var raGot = SelectionHandles.SnapPolygonVertex(StrokeKind.Parallelogram, 2, P0, raP1, raV,
                                                           raV, tol, false, out var raSnap);
            float raAngAfter = SelectionHandles.AngleBetweenDegrees(raGot - P0, raP1 - P0);
            Check("平行四边形·矩形：夹角在 1° 内 → 修正到**恰好 90°**",
                  raSnap == ShapeSnapKind.Rectangle && Near(raGot.X, P0.X, 0.01f)
                  && MathF.Abs(raAngAfter - 90f) < 1e-3f,
                  $"摆位时夹角 {raAng:F4}°（容差 {angTol:F0}°）→ 吸到「"
                  + $"{SelectionHandles.ShapeSnapLabel(raSnap)}」，修正后 {raAngAfter:F4}°");

            var raV2 = new Vector2(lx + 3f, ly - 150f);      // 3 像素 → 夹角离 90° 约 1.15°（容差外）
            float raAng2 = SelectionHandles.AngleBetweenDegrees(raV2 - P0, raP1 - P0);
            var raGot2 = SelectionHandles.SnapPolygonVertex(StrokeKind.Parallelogram, 2, P0, raP1, raV2,
                                                            raV2, tol, false, out var raSnap2);
            Check("平行四边形·矩形：夹角刚好在容差外（约 88.85°）→ 不吸",
                  MathF.Abs(raAng2 - 90f) > angTol && raSnap2 == ShapeSnapKind.None && raGot2 == raV2,
                  $"夹角 {raAng2:F4}°，离 90° 差 {MathF.Abs(raAng2 - 90f):F3}° > {angTol:F0}° → "
                  + $"吸到={raSnap2}，顶点原样");
            var raAlt = SelectionHandles.SnapPolygonVertex(StrokeKind.Parallelogram, 2, P0, raP1, raV,
                                                           raV, tol, true, out var raSnap3);
            Check("平行四边形·矩形：**Alt 一律自由**",
                  raSnap3 == ShapeSnapKind.None && raAlt == raV,
                  $"Alt 下吸到={raSnap3}，顶点原样");

            // ---- 矩形 · 正方形（拖四角：改的是矩阵，不是点）----
            {
                var rSq = new Stroke
                {
                    Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 4f * DpiScale,
                };
                rSq.AddPoint(lx, ly, 1f, 0);
                rSq.AddPoint(lx + 300f, ly + 300f + tol * 0.5f, 1f, 0);   // 两边差 = tol/2
                var sqSel = new[] { rSq };
                var sqFrame = SelectionHandles.FrameOf(sqSel);
                float sqW = sqFrame.Local.MaxX - sqFrame.Local.MinX;
                float sqH = sqFrame.Local.MaxY - sqFrame.Local.MinY;
                var sqAnchor = SelectionHandles.CanvasPosition(SelHandle.TopLeft, sqFrame, DpiScale);
                var sqCorner = SelectionHandles.CanvasPosition(SelHandle.BottomRight, sqFrame, DpiScale);
                var sqTo = sqAnchor + (sqCorner - sqAnchor) * 2f;          // 拖到两倍大

                bool sqOk = SelectionHandles.TrySnapSquareCorner(sqSel, SelHandle.BottomRight, sqFrame,
                                                                 sqTo, DpiScale, false, out var sqM);
                var s0 = Vector2.Transform(new Vector2(sqFrame.Local.MinX, sqFrame.Local.MinY), sqM);
                var s1 = Vector2.Transform(new Vector2(sqFrame.Local.MaxX, sqFrame.Local.MaxY), sqM);
                Check("矩形·正方形：两边差在容差内 → 拖四角时修正成**精确**正方形",
                      sqOk && Near(s1.X - s0.X, s1.Y - s0.Y, 0.01f) && (s1.X - s0.X) > 500f,
                      $"原框 {sqW:F2}×{sqH:F2}（差 {MathF.Abs(sqW - sqH):F2}，容差 {tol:F1}）→ "
                      + $"结果 {s1.X - s0.X:F3}×{s1.Y - s0.Y:F2}，吸住={sqOk}");

                // 对照：**不吸**的那条路（通用 DragMatrix）会保持原来的**比值**
                // （四角是等比缩放，两条边同比放大，所以"差"会跟着变大、"比值"不变）
                var plainM = SelectionHandles.DragMatrix(SelHandle.BottomRight, sqFrame,
                                                         sqTo, sqTo, DpiScale, false, false, true);
                var p0 = Vector2.Transform(new Vector2(sqFrame.Local.MinX, sqFrame.Local.MinY), plainM);
                var p1 = Vector2.Transform(new Vector2(sqFrame.Local.MaxX, sqFrame.Local.MaxY), plainM);
                Check("矩形·正方形：不吸的时候**比值**一个数都不改（对照）",
                      Near((p1.X - p0.X) / (p1.Y - p0.Y), sqW / sqH, 1e-4f),
                      $"通用换算的结果 {p1.X - p0.X:F2}×{p1.Y - p0.Y:F2}"
                      + $"（比值 {(p1.X - p0.X) / (p1.Y - p0.Y):F5} / 原比值 {sqW / sqH:F5}，"
                      + $"两边差被同比放大成 {MathF.Abs((p1.X - p0.X) - (p1.Y - p0.Y)):F2}"
                      + $"= 原差值 {MathF.Abs(sqW - sqH):F2} × 缩放倍数）");

                var rOut = new Stroke
                {
                    Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 4f * DpiScale,
                };
                rOut.AddPoint(lx, ly, 1f, 0);
                rOut.AddPoint(lx + 300f, ly + 300f + tol * 1.5f, 1f, 0);
                var outSel = new[] { rOut };
                var outFrame = SelectionHandles.FrameOf(outSel);
                var outAnchor = SelectionHandles.CanvasPosition(SelHandle.TopLeft, outFrame, DpiScale);
                var outCorner = SelectionHandles.CanvasPosition(SelHandle.BottomRight, outFrame, DpiScale);
                var outTo = outAnchor + (outCorner - outAnchor) * 2f;
                Check("矩形·正方形：两边差**刚好在容差外** → 不吸",
                      !SelectionHandles.TrySnapSquareCorner(outSel, SelHandle.BottomRight, outFrame,
                                                            outTo, DpiScale, false, out _),
                      $"原框 {outFrame.Local.MaxX - outFrame.Local.MinX:F2}"
                      + $"×{outFrame.Local.MaxY - outFrame.Local.MinY:F2}"
                      + $"（差 {outFrame.Local.MaxY - outFrame.Local.MinY - (outFrame.Local.MaxX - outFrame.Local.MinX):F2}"
                      + $" > 容差 {tol:F1}）");
                Check("矩形·正方形：**Alt 一律自由**",
                      !SelectionHandles.TrySnapSquareCorner(sqSel, SelHandle.BottomRight, sqFrame,
                                                            sqTo, DpiScale, true, out _),
                      "Alt 下同样的摆位不吸");
                // 转过的矩形：选中框是它的外接正矩形（虚胖），沿屏幕轴缩放和它自己的边长
                // 没有简单关系 —— 明确不吸（宁可不给，也不给一个解释不清的行为）
                var rRot = new Stroke
                {
                    Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 4f * DpiScale,
                };
                rRot.AddPoint(lx, ly, 1f, 0);
                rRot.AddPoint(lx + 300f, ly + 300f + tol * 0.5f, 1f, 0);
                rRot.Transform = SelectionHandles.RotateMatrix(30f, new Vector2(lx, ly));
                var rotSel = new[] { rRot };
                var rotFrame = SelectionHandles.FrameOf(rotSel);
                var rotAnchor = SelectionHandles.CanvasPosition(SelHandle.TopLeft, rotFrame, DpiScale);
                var rotCorner = SelectionHandles.CanvasPosition(SelHandle.BottomRight, rotFrame, DpiScale);
                Check("矩形·正方形：**转过的**矩形不吸（范围写清楚）",
                      !SelectionHandles.TrySnapSquareCorner(rotSel, SelHandle.BottomRight, rotFrame,
                                                            rotAnchor + (rotCorner - rotAnchor) * 2f,
                                                            DpiScale, false, out _),
                      "转过 30°：沿屏幕轴缩放 ≠ 沿它自己两条边缩放，所以不吸");
            }

            // ============ 真机层：吸住时接在那条路上，而且提交的几何**精确**满足约束 ============
            // ---- 三角形拖顶点（真机）：等腰 ----
            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            float mA = lx, mB = lx + 200f, mY = ly;
            var mTri = new Stroke
            {
                Tool = Tool.Triangle, Kind = StrokeKind.Triangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 6f * DpiScale,
            };
            mTri.AddPoint(midX + tol * 0.25f, mY - 150f, 1f, 0);
            mTri.AddPoint(mA, mY, 1f, 0);
            mTri.AddPoint(mB, mY, 1f, 0);
            Doc.AddStroke(mTri);
            Doc.SelectOnly(new[] { mTri });
            SettleFrames(250);
            var mTriApex = SelectionHandles.ShapeHandleCanvasPosition(mTri, ShapeHandle.Vertex0);
            var mTriTo = new Vector2(midX + tol * 0.25f, mY - 400f);   // 还在容差内 → 会吸
            SelectionGestureForTest(mTriApex.X, mTriApex.Y);
            UpdateSelectionGestureForTest(mTriTo.X, mTriTo.Y);
            SettleFrames(80);
            Check("吸附（真机）：拖三角形的顶点、吸住时胶囊说得出「等腰」",
                  ShapeSnapKind == ShapeSnapKind.Isosceles
                  && SelectionHandles.ShapeSnapLabel(ShapeSnapKind) == "等腰"
                  && VertexDragging,
                  $"吸到={ShapeSnapKind}（「{SelectionHandles.ShapeSnapLabel(ShapeSnapKind)}」），"
                  + $"拖元素中={VertexDragging}");
            EndSelectionGestureForTest();
            SettleFrames(200);
            var mA2 = new Vector2(mTri.Points[1].X, mTri.Points[1].Y);
            var mB2 = new Vector2(mTri.Points[2].X, mTri.Points[2].Y);
            var mApex = new Vector2(mTri.Points[0].X, mTri.Points[0].Y);
            Check("吸附（真机）：提交的几何**精确**满足约束（两腰逐位相等）",
                  mApex.X == midX && D(mApex, mA2) == D(mApex, mB2),
                  $"顶点 ({mApex.X:F3},{mApex.Y:F3}) 期望 x={midX:F3}（指针本来在 "
                  + $"{mTriTo.X:F3}，被吸回来了），两腰 {D(mApex, mA2):F6} / {D(mApex, mB2):F6}");

            // ---- 椭圆拖轴端点（真机）：正圆 ----
            Doc.Clear();
            Doc.ClearHistory();
            float eCx = lx + 300f, eCy = ly - 300f;
            float eB0 = 200f + tol * 0.5f;             // b 比 a 大半个容差
            var mEll = new Stroke
            {
                Tool = Tool.Ellipse, Kind = StrokeKind.Ellipse,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 6f * DpiScale,
            };
            mEll.AddPoint(eCx, eCy, 1f, 0);
            // a 一开始**故意离 b 很远**（差了整整 100）：那样这一次拖动才有足够长的行程
            // （短于"点选容差"的拖动会被当成"点了一下"，不提交几何）。
            // 拖到最后一步时 a 落在 b 的容差里 → 才吸。
            mEll.AddPoint(eCx + 100f, eCy + eB0, 1f, 0);      // a = 100、b = eB0
            Doc.AddStroke(mEll);
            Doc.SelectOnly(new[] { mEll });
            SettleFrames(250);
            var mRim = SelectionHandles.ShapeHandleCanvasPosition(mEll, ShapeHandle.AxisRight);
            SelectionGestureForTest(mRim.X, mRim.Y);
            UpdateSelectionGestureForTest(eCx + eB0 + tol * 0.5f, eCy);   // 拖到 |a − b| = tol/2
            SettleFrames(80);
            Check("吸附（真机）：拖椭圆的轴端点、吸住时胶囊说得出「正圆」",
                  ShapeSnapKind == ShapeSnapKind.Circle
                  && SelectionHandles.ShapeSnapLabel(ShapeSnapKind) == "正圆",
                  $"吸到={ShapeSnapKind}（「{SelectionHandles.ShapeSnapLabel(ShapeSnapKind)}」）");
            EndSelectionGestureForTest();
            SettleFrames(200);
            Check("吸附（真机）：提交的几何是**正圆**（a、b 逐位相等）",
                  mEll.SemiAxisALocal == mEll.SemiAxisBLocal && mEll.SemiAxisALocal == eB0,
                  $"a={mEll.SemiAxisALocal:F6} b={mEll.SemiAxisBLocal:F6}（期望都等于 {eB0:F6}）");

            // ---- 矩形拖四角（真机）：正方形 ----
            Doc.Clear();
            Doc.ClearHistory();
            var mRect = new Stroke
            {
                Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 4f * DpiScale,
            };
            mRect.AddPoint(lx, ly, 1f, 0);
            mRect.AddPoint(lx + 300f, ly + 300f + tol * 0.5f, 1f, 0);
            Doc.AddStroke(mRect);
            Doc.SelectOnly(new[] { mRect });
            SettleFrames(250);
            var mFrame = SelectionHandles.FrameOf(Doc.Selected);
            var mCorner = SelectionHandles.CanvasPosition(SelHandle.BottomRight, mFrame, DpiScale);
            bool tookCorner = SelectionGestureForTest(mCorner.X, mCorner.Y);
            UpdateSelectionGestureForTest(mCorner.X + 200f, mCorner.Y + 200f);
            SettleFrames(80);
            Check("吸附（真机）：拖矩形的角、吸住时胶囊说得出「正方形」",
                  tookCorner && ShapeSnapKind == ShapeSnapKind.Square
                  && SelectionHandles.ShapeSnapLabel(ShapeSnapKind) == "正方形",
                  $"接住={tookCorner}，吸到={ShapeSnapKind}"
                  + $"（「{SelectionHandles.ShapeSnapLabel(ShapeSnapKind)}」）");
            EndSelectionGestureForTest();
            SettleFrames(200);
            var mRectBox = mRect.WorldInkBounds;
            Check("吸附（真机）：提交的几何是**精确**正方形",
                  Near(mRectBox.MaxX - mRectBox.MinX, mRectBox.MaxY - mRectBox.MinY, 0.01f),
                  $"框 {(mRectBox.MaxX - mRectBox.MinX):F3}×{(mRectBox.MaxY - mRectBox.MinY):F3}");

            // ---- 整体移动 / 旋转**不吸**（规格 9.6 第一条硬要求）----
            Doc.Clear();
            Doc.ClearHistory();
            var moveTri = new Stroke
            {
                Tool = Tool.Triangle, Kind = StrokeKind.Triangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 6f * DpiScale,
            };
            // 这个三角形**本来就在等腰的容差里**：如果整体移动 / 旋转也做形状吸附，
            // 三个顶点会被"修正"一个像素——自检就是盯这一条。
            moveTri.AddPoint(midX + tol * 0.25f, mY - 150f, 1f, 0);
            moveTri.AddPoint(mA, mY, 1f, 0);
            moveTri.AddPoint(mB, mY, 1f, 0);
            Doc.AddStroke(moveTri);
            Doc.SelectOnly(new[] { moveTri });
            SettleFrames(250);
            string movePts0 = $"{moveTri.Points[0].X},{moveTri.Points[0].Y}"
                            + $"|{moveTri.Points[1].X},{moveTri.Points[1].Y}"
                            + $"|{moveTri.Points[2].X},{moveTri.Points[2].Y}";
            var mvFrame = SelectionHandles.FrameOf(Doc.Selected);
            var mvCenter = new Vector2((mvFrame.CanvasAabb.MinX + mvFrame.CanvasAabb.MaxX) * 0.5f,
                                       (mvFrame.CanvasAabb.MinY + mvFrame.CanvasAabb.MaxY) * 0.5f);
            bool tookMove = SelectionGestureForTest(mvCenter.X, mvCenter.Y);
            UpdateSelectionGestureForTest(mvCenter.X + 150f, mvCenter.Y + 80f);
            SettleFrames(80);
            Check("整体移动不吸：拖的是位置，顶点一个都不动、也没有吸附",
                  tookMove && ShapeSnapKind == ShapeSnapKind.None
                  && movePts0 == $"{moveTri.Points[0].X},{moveTri.Points[0].Y}"
                             + $"|{moveTri.Points[1].X},{moveTri.Points[1].Y}"
                             + $"|{moveTri.Points[2].X},{moveTri.Points[2].Y}",
                  $"接住={tookMove}，吸到={ShapeSnapKind}，顶点原样（逐位）");
            EndSelectionGestureForTest();
            SettleFrames(200);
            var mvApex = SelectionHandles.ShapeHandleCanvasPosition(moveTri, ShapeHandle.Vertex0);
            Check("整体移动：真的挪了 (150,80)、顶点仍然逐位不变",
                  movePts0 == $"{moveTri.Points[0].X},{moveTri.Points[0].Y}"
                             + $"|{moveTri.Points[1].X},{moveTri.Points[1].Y}"
                             + $"|{moveTri.Points[2].X},{moveTri.Points[2].Y}"
                  && Near(mvApex.X - (midX + tol * 0.25f), 150f, 1f)
                  && Near(mvApex.Y - (mY - 150f), 80f, 1f),
                  $"顶点画布位移 ({mvApex.X - (midX + tol * 0.25f):F1},"
                  + $"{mvApex.Y - (mY - 150f):F1})，局部点逐位不变");
            Doc.Undo();
            SettleFrames(150);

            // 旋转：同样不该改顶点
            Doc.SelectOnly(new[] { moveTri });
            SettleFrames(200);
            var rotFrame2 = SelectionHandles.FrameOf(Doc.Selected);
            var rotPivot = new Vector2((rotFrame2.CanvasAabb.MinX + rotFrame2.CanvasAabb.MaxX) * 0.5f,
                                       (rotFrame2.CanvasAabb.MinY + rotFrame2.CanvasAabb.MaxY) * 0.5f);
            var rotGrip = SelectionHandles.CanvasPosition(SelHandle.Rotate, rotFrame2, DpiScale);
            float rotArm = Vector2.Distance(rotGrip, rotPivot);
            float rotA0 = MathF.Atan2(rotGrip.Y - rotPivot.Y, rotGrip.X - rotPivot.X);
            bool tookRot = SelectionGestureForTest(rotGrip.X, rotGrip.Y);
            // **旋转中不画框**（用户 2026-09-25 照 ClassIn 定）：框是轴对齐的，一转就每帧重贴
            // 内容、边角看着像在抖；转的当下也没人看框 —— 干脆收起来，松手再出现。
            // 量法：在**框上边那一段**数强调色（#0078D4）像素。
            // ⚠ 窗口必须**每次从"当前框"重算**（写成局部函数、两次调用各算一次）：
            //   转起来框会被重新贴合，窗口固定不动就量不到转后的边 —— 变异抽查时只差 16 像素，
            //   余量小到不够硬（2026-09-25 实测）。跟着当前框走，两边都压在"今天这个框的上边"，
            //   对比才有意义。
            int FrameTopPixels()
            {
                var bx = LiveSelectionFrame.CanvasAabb;
                return ScreenProbe.CountNear(
                    (int)bx.MinX, (int)(bx.MinY - 3f * DpiScale),
                    Math.Max(8, (int)((bx.MaxX - bx.MinX) * 0.6f)), Math.Max(4, (int)(8f * DpiScale)),
                    0, 120, 212, 60);
            }
            int framePixStatic = FrameTopPixels();

            var rp = new Vector2(rotPivot.X + rotArm * MathF.Cos(rotA0 - 0.35f),
                                 rotPivot.Y + rotArm * MathF.Sin(rotA0 - 0.35f));    // 逆时针 20°
            UpdateSelectionGestureForTest(rp.X, rp.Y);
            SettleFrames(80);
            int framePixRotating = FrameTopPixels();
            // 判据：静止时那一段**必须有**框的强调色（太少说明窗口没压住边、这条就白验了）；
            //       旋转中必须**一个都没有**。
            Check("旋转中**不画**选中框（ClassIn 的手感：转的时候没有矩形框）",
                  framePixStatic > 100 && framePixRotating == 0,
                  $"框上边那一段：静止 {framePixStatic} 像素 → 旋转中 {framePixRotating} 像素（期望 0）");
            Check("整体旋转不吸：转的时候顶点一个都不动、也没有形状吸附",
                  tookRot && SelRotating && ShapeSnapKind == ShapeSnapKind.None
                  && movePts0 == $"{moveTri.Points[0].X},{moveTri.Points[0].Y}"
                             + $"|{moveTri.Points[1].X},{moveTri.Points[1].Y}"
                             + $"|{moveTri.Points[2].X},{moveTri.Points[2].Y}",
                  $"接住={tookRot}，旋转中={SelRotating}，吸到={ShapeSnapKind}，顶点原样（逐位）");
            EndSelectionGestureForTest();
            SettleFrames(200);
            var rotM = moveTri.Transform;
            Check("整体旋转：变换是**纯旋转**（顶点逐位不变，只换了姿态）",
                  movePts0 == $"{moveTri.Points[0].X},{moveTri.Points[0].Y}"
                             + $"|{moveTri.Points[1].X},{moveTri.Points[1].Y}"
                             + $"|{moveTri.Points[2].X},{moveTri.Points[2].Y}"
                  && Near(rotM.M11 * rotM.M11 + rotM.M12 * rotM.M12, 1f, 1e-3f)
                  && Near(rotM.M21, -rotM.M12, 1e-3f) && Near(rotM.M22, rotM.M11, 1e-3f),
                  $"矩阵 ({rotM.M11:F4},{rotM.M12:F4},{rotM.M21:F4},{rotM.M22:F4})，顶点逐位不变");
        }

        // ================= N. 存档：圆 / 椭圆 / 三角形 / 平行四边形读回来逐位一致 =================
        //
        // 格式**没有加字段**（三角形的三个顶点、平行四边形的三个顶点都是普通控制点），
        // 只是多了两个 `Kind` 取值，所以升版本的理由和前几版一样：
        // 让不认识新 Kind 的老程序直接说"请升级"，而不是画出个残缺的形状；
        // 反方向（新程序读老文件）不受影响。
        Console.WriteLine("  -- N. 存档（圆 / 椭圆 / 三角形 / 平行四边形往返）--");
        {
            Doc.Clear();
            Doc.ClearHistory();
            var kCircle = new Stroke
            {
                Tool = Tool.Circle, Kind = StrokeKind.Circle,
                Color = new Color4(0.1f, 0.2f, 0.3f, 1f), Width = 6f,
            };
            kCircle.AddPoint(1000f, 500f, 1f, 0);
            kCircle.AddPoint(1180f, 500f, 1f, 0);
            var kEllipse = new Stroke
            {
                Tool = Tool.Ellipse, Kind = StrokeKind.Ellipse,
                Color = new Color4(0.3f, 0.2f, 0.1f, 1f), Width = 5f,
            };
            kEllipse.AddPoint(400f, 300f, 1f, 0);
            kEllipse.AddPoint(620f, 420f, 1f, 0);
            kEllipse.Transform = SelectionHandles.RotateMatrix(25f, new Vector2(400f, 300f));
            // 三角形 / 平行四边形也一起过一遍（第二批第②步新增的两个 Kind）
            var kTriangle = new Stroke
            {
                Tool = Tool.Triangle, Kind = StrokeKind.Triangle,
                Color = new Color4(0.2f, 0.5f, 0.3f, 1f), Width = 7f,
            };
            kTriangle.AddPoint(1500f, 400f, 1f, 0);
            kTriangle.AddPoint(1300f, 700f, 1f, 0);
            kTriangle.AddPoint(1750f, 700f, 1f, 0);
            kTriangle.Transform = SelectionHandles.RotateMatrix(-18f, new Vector2(1500f, 600f));
            var kPara = new Stroke
            {
                Tool = Tool.Parallelogram, Kind = StrokeKind.Parallelogram,
                Color = new Color4(0.5f, 0.2f, 0.4f, 1f), Width = 4f,
            };
            kPara.AddPoint(200f, 900f, 1f, 0);          // 底左
            kPara.AddPoint(520f, 900f, 1f, 0);          // 底右
            kPara.AddPoint(280f, 620f, 1f, 0);          // 顶左
            Doc.AddStroke(kCircle);
            Doc.AddStroke(kEllipse);
            Doc.AddStroke(kTriangle);
            Doc.AddStroke(kPara);

            var blob = InkSerializer.Save(Doc);
            var back = new InkDocument();
            InkSerializer.LoadInto(back, blob);

            // 这里**不再写死版本号**：这一条要验的是"文件头里写的 = 常量"，
            // 而不是"版本正好是 7"。写死数字的话，以后每次抬版本都要回来改一次
            // （2026-09-19 加坐标系 / 数轴抬到 9，这里就是当场红的那一条）。
            // `>= 7` 那一半仍然钉住"这一批的两个 Kind 存在 ⇒ 版本至少 7"。
            Check("存档：格式版本抬过 7、且文件头里写的就是常量",
                  InkSerializer.FormatVersion >= 7
                  && BitConverter.ToInt32(blob, 4) == InkSerializer.FormatVersion,
                  $"常量 {InkSerializer.FormatVersion}，文件头里的版本 {BitConverter.ToInt32(blob, 4)}");
            Check("存档：四个对象都读回来了（Kind 也对）",
                  back.Strokes.Count == 4
                  && back.Strokes[0].Kind == StrokeKind.Circle
                  && back.Strokes[1].Kind == StrokeKind.Ellipse
                  && back.Strokes[2].Kind == StrokeKind.Triangle
                  && back.Strokes[3].Kind == StrokeKind.Parallelogram,
                  $"对象 {back.Strokes.Count} 个，Kind = {back.Strokes[0].Kind} / {back.Strokes[1].Kind}"
                  + $" / {back.Strokes[2].Kind} / {back.Strokes[3].Kind}");

            var bc = back.Strokes[0];
            var be = back.Strokes[1];
            var bt = back.Strokes[2];
            var bp = back.Strokes[3];
            Check("存档：圆的圆心 / 半径逐位一致",
                  bc.Points[0].X == kCircle.Points[0].X && bc.Points[0].Y == kCircle.Points[0].Y
                  && bc.Points[1].X == kCircle.Points[1].X && bc.Points[1].Y == kCircle.Points[1].Y
                  && bc.CircleRadiusLocal == kCircle.CircleRadiusLocal,
                  $"圆心 ({bc.Points[0].X:F1},{bc.Points[0].Y:F1})，半径 {bc.CircleRadiusLocal:F3}");
            Check("存档：椭圆的中心 / 半轴 / 变换逐位一致",
                  be.Points[0].X == kEllipse.Points[0].X && be.Points[0].Y == kEllipse.Points[0].Y
                  && be.SemiAxisALocal == kEllipse.SemiAxisALocal
                  && be.SemiAxisBLocal == kEllipse.SemiAxisBLocal
                  && be.Transform.Equals(kEllipse.Transform),
                  $"中心 ({be.Points[0].X:F1},{be.Points[0].Y:F1})，"
                  + $"a={be.SemiAxisALocal:F1} b={be.SemiAxisBLocal:F1}");
            Check("存档：紧框与原件一致（口径也跟着存下来的点走）",
                  bc.WorldInkBounds.MaxX == kCircle.WorldInkBounds.MaxX
                  && be.WorldInkBounds.MaxY == kEllipse.WorldInkBounds.MaxY,
                  $"圆框 {bc.WorldInkBounds.MaxX - bc.WorldInkBounds.MinX:F1}"
                  + $"/{kCircle.WorldInkBounds.MaxX - kCircle.WorldInkBounds.MinX:F1}，"
                  + $"椭圆框 {be.WorldInkBounds.MaxY - be.WorldInkBounds.MinY:F1}"
                  + $"/{kEllipse.WorldInkBounds.MaxY - kEllipse.WorldInkBounds.MinY:F1}");
            Check("存档：三角形的三个顶点 / 变换逐位一致",
                  bt.Points.Count == 3
                  && bt.Points[0].X == kTriangle.Points[0].X && bt.Points[0].Y == kTriangle.Points[0].Y
                  && bt.Points[1].X == kTriangle.Points[1].X && bt.Points[1].Y == kTriangle.Points[1].Y
                  && bt.Points[2].X == kTriangle.Points[2].X && bt.Points[2].Y == kTriangle.Points[2].Y
                  && bt.Transform.Equals(kTriangle.Transform),
                  $"顶点0 ({bt.Points[0].X:F1},{bt.Points[0].Y:F1})，变换 {bt.Transform.M11:F4}");
            Check("存档：平行四边形的三个顶点逐位一致（第四个读回来仍是公式推出来的）",
                  bp.Points.Count == 3
                  && bp.Points[0].X == kPara.Points[0].X && bp.Points[0].Y == kPara.Points[0].Y
                  && bp.Points[1].X == kPara.Points[1].X && bp.Points[1].Y == kPara.Points[1].Y
                  && bp.Points[2].X == kPara.Points[2].X && bp.Points[2].Y == kPara.Points[2].Y
                  && bp.ParallelogramFourthLocal() == kPara.ParallelogramFourthLocal(),
                  $"第四个顶点 ({bp.ParallelogramFourthLocal().X:F1},"
                  + $"{bp.ParallelogramFourthLocal().Y:F1})");
            Check("存档：两个多边形的紧框与原件一致",
                  bt.WorldInkBounds.MinX == kTriangle.WorldInkBounds.MinX
                  && bt.WorldInkBounds.MaxY == kTriangle.WorldInkBounds.MaxY
                  && bp.WorldInkBounds.MinY == kPara.WorldInkBounds.MinY
                  && bp.WorldInkBounds.MaxX == kPara.WorldInkBounds.MaxX,
                  $"三角框 {bt.WorldInkBounds.MaxX - bt.WorldInkBounds.MinX:F1}"
                  + $"/{kTriangle.WorldInkBounds.MaxX - kTriangle.WorldInkBounds.MinX:F1}，"
                  + $"平四框 {bp.WorldInkBounds.MaxX - bp.WorldInkBounds.MinX:F1}"
                  + $"/{kPara.WorldInkBounds.MaxX - kPara.WorldInkBounds.MinX:F1}");
        }

        // ================= O. 拉伸中的形体完整性（缺块 / 残影）=================
        //
        // 用户真机反馈（2026-09-19）："椭圆 / 圆在拉伸的时候有时候会消失一部分"。
        //
        // **触发条件**（"有时候"的那个"有时候"）：拉伸让形体跨过**分块边界**
        // （块边长 256 画布像素）。根因在内容层这一头，两步：
        //   ① 起手那一刻形体被摘出内容层（DetachForDrag），它压过的分块整块重画一次
        //      ——那次是**不带它**画的，于是那些块上留着一个"洞"；
        //   ② 松手提交几何时，新位置的脏区由 Stroke.PaddedBoundsOf 算。它以前对圆 / 椭圆
        //      退化成"圆心 + 圆周点这两个点的外接"——那只是形体的一角（椭圆的四分之一），
        //      于是**左边的分块没被标脏**，洞永远补不上：屏幕上就是"形体缺一大块"，
        //      而缺到哪一刀正好落在分块边界上（所以是"有时候"）。
        // 拖动过程本身是好的（浮动层按"实时框"画临时几何，框是完整的），
        // 所以症状总在**手一松的那一瞬**露出来。这一节把它钉死：
        //   · 放大（未松手）：被拖手柄那一侧的远端、对面那个极端点、以及另一条半轴的
        //     两个极端点，四处都必须有墨——缺一块就会被抓到；
        //   · 缩小（未松手）：旧形体外侧不许留墨（残影会被抓到），新形体外侧必须有墨；
        //   · 松手后：整块墨量必须等于"整层作废重画"的墨量（= 静止态直接画出来）。
        Console.WriteLine("  -- O. 拉伸中的形体完整性（缺块 / 残影）--");
        {
            ViewOffsetY = 0f;                       // 屏幕坐标 == 画布坐标，探针才好对位
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

            // 探针窗口半径（物理像素）。**别开太大**：拖元素时的读数胶囊挂在被拖的那个点
            // **上方**（间距 ≈ 21 逻辑像素），开大了会把胶囊的底色算进来。
            const int ProbeHalf = 12;
            // "这一处有墨"的门槛：8 逻辑像素宽的笔画在 24×24 的窗里约 190 个核心像素，
            // 门槛给 60 足够宽裕，又能把"整条弧少了一截"抓出来。
            const int HasInk = 60, NoInk = 20;

            int Ink(float x, float y)
                => ScreenProbe.CountMagenta((int)x - ProbeHalf, (int)y - ProbeHalf,
                                            ProbeHalf * 2, ProbeHalf * 2);

            // 形体整块的墨量（按墨迹框四边各放 20 物理像素量）。缺一块会当场少一大截。
            int WholeInk(Stroke s)
            {
                var b = s.WorldInkBounds;
                return ScreenProbe.CountMagenta((int)b.MinX - 20, (int)b.MinY - 20,
                                                (int)(b.MaxX - b.MinX) + 40, (int)(b.MaxY - b.MinY) + 40);
            }

            // 拖一次尺寸手柄：放大 → 缩小 → 松手，全程盯"缺块 / 残影"。
            //   · `dir`     = 从中心出发、被拖那个手柄的单位方向（右轴端点 = (+1,0)…）；
            //   · `r0`      = 起手时沿着 `dir` 的那条半轴（圆的半径）长度；
            //   · `other0`  = 另一条半轴的长度（圆与 `r0` 相同）；
            //   · `rGrow` / `rShrink` = 放大 / 缩小到多少（两次拖的是同一个手柄）。
            void ResizeCase(string name, StrokeKind kind, float cx, float cy,
                            ShapeHandle handle, Vector2 dir, float r0, float other0,
                            float rGrow, float rShrink)
            {
                Doc.Clear();
                Doc.ClearHistory();
                Tool = Tool.Marquee;
                var s = new Stroke
                {
                    Tool = kind == StrokeKind.Circle ? Tool.Circle : Tool.Ellipse,
                    Kind = kind, Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
                };
                s.AddPoint(cx, cy, 1f, 0);                       // 中心 / 圆心
                if (kind == StrokeKind.Circle)                   // 圆周点：沿着 dir，半径 r0
                    s.AddPoint(cx + dir.X * r0, cy + dir.Y * r0, 1f, 0);
                else
                {
                    // 外角点：**横着的那条半轴总是 a、竖着的那条总是 b**（和拖的哪个手柄无关）。
                    // 所以"被拖的那条半轴是 r0、另一条是 other0"要按 dir 落到 a / b 上。
                    float a = MathF.Abs(dir.X) > 0.5f ? r0 : other0;
                    float b = MathF.Abs(dir.Y) > 0.5f ? r0 : other0;
                    s.AddPoint(cx + a, cy + b, 1f, 0);
                }
                Doc.AddStroke(s);
                Doc.SelectOnly(new[] { s });
                SettleFrames(300);

                var perp = new Vector2(-dir.Y, dir.X);           // 另一条半轴的方向
                // 四个极端点：沿被拖那条半轴的远端 / 近端，以及另一条半轴的两端。
                Vector2 Far(float along) => new(cx + dir.X * along, cy + dir.Y * along);
                Vector2 Near(float along) => new(cx - dir.X * along, cy - dir.Y * along);
                Vector2 Side(float across, float sign)
                    => new(cx + perp.X * across * sign, cy + perp.Y * across * sign);

                // 按下手柄（真走一次选择手势的分流，和用户手拖是同一条路）。
                // 顺手对一次"手柄在哪"：这里算的 Far(r0) 必须就是引擎给的那个手柄位置，
                // 否则探针盯的地方和用户拖的地方不是一回事（判据自己会骗自己）。
                var handlePos = SelectionHandles.ShapeHandleCanvasPosition(s, handle);
                bool took = SelectionGestureForTest(handlePos.X, handlePos.Y);
                Check($"{name}：{handle} 手柄就在被拖那条半轴的端点上",
                      MathF.Abs(handlePos.X - Far(r0).X) < 0.5f && MathF.Abs(handlePos.Y - Far(r0).Y) < 0.5f,
                      $"手柄 ({handlePos.X:F1},{handlePos.Y:F1}) 期望 ({Far(r0).X:F1},{Far(r0).Y:F1})");
                // **一帧跳到位**：这一步正是真机上"拖快了"的那一帧。
                var growTo = Far(rGrow);
                UpdateSelectionGestureForTest(growTo.X, growTo.Y);
                SettleFrames(120);
                int farGrow = Ink(growTo.X, growTo.Y);
                int nearGrow = Ink(Near(rGrow).X, Near(rGrow).Y);
                var sideA = Side(kind == StrokeKind.Circle ? rGrow : other0, +1f);
                var sideB = Side(kind == StrokeKind.Circle ? rGrow : other0, -1f);
                int sideAGrow = Ink(sideA.X, sideA.Y);
                int sideBGrow = Ink(sideB.X, sideB.Y);
                Check($"{name}：放大中被拖那一侧的远端有墨（缺一块会在这儿露）",
                      took && VertexDragging && farGrow > HasInk,
                      $"远端 ({growTo.X:F0},{growTo.Y:F0}) {farGrow} 像素（期望 >{HasInk}）");
                Check($"{name}：放大中对面那个极端点有墨",
                      nearGrow > HasInk, $"对面 {nearGrow} 像素（期望 >{HasInk}）");
                Check($"{name}：放大中另一条半轴的两个极端点都有墨",
                      sideAGrow > HasInk && sideBGrow > HasInk,
                      $"两个极端点 {sideAGrow} / {sideBGrow} 像素（期望都 >{HasInk}）");

                // 缩小：同一个手柄往回收（rGrow → rShrink，且 rShrink < r0）。
                var shrinkTo = Far(rShrink);
                UpdateSelectionGestureForTest(shrinkTo.X, shrinkTo.Y);
                SettleFrames(120);
                int farShrink = Ink(shrinkTo.X, shrinkTo.Y);
                int nearShrink = Ink(Near(rShrink).X, Near(rShrink).Y);
                int oldResidue = Ink(growTo.X, growTo.Y);      // 放大时待过的地方：必须干净
                Check($"{name}：缩小后新形体外侧有墨",
                      farShrink > HasInk, $"新远端 {farShrink} 像素（期望 >{HasInk}）");
                Check($"{name}：缩小后对面那个极端点也有墨",
                      nearShrink > HasInk, $"对面 {nearShrink} 像素（期望 >{HasInk}）");
                Check($"{name}：缩小后旧形体外侧不留墨（残影）",
                      oldResidue < NoInk, $"放大时待过的 ({growTo.X:F0},{growTo.Y:F0}) 还剩 {oldResidue} 像素（期望 <{NoInk}）");

                // 松手：模型这才改，屏幕改由内容层那一份来画。
                EndSelectionGestureForTest();
                SettleFrames(250);
                // **先取消选中再量**：手柄方块正好压在被拖的那个端点上，带着选中量会把
                // "手柄盖住了弧"当成"缺了一块"（这条自检第一版就是这么误报的）。
                Doc.Selected.Clear();
                SettleFrames(250);
                int afterFar = Ink(shrinkTo.X, shrinkTo.Y);
                int afterOld = Ink(growTo.X, growTo.Y);
                int commitInk = WholeInk(s);
                Check($"{name}：松手后探针与拖动中一致（还是那条几何）",
                      afterFar > HasInk && afterOld < NoInk,
                      $"远端 {farShrink} → {afterFar} 像素；旧位 {afterOld} 像素");
                // **最硬的一条**：松手后的墨量 == 把内容层整层作废重画的墨量。
                // 后者就是"静止态直接画出来"，只差一点抗锯齿接缝（实测 <1%）。
                // 脏区算窄了 → 有分块从没重画过 → 这一条当场掉下去（修之前 6936 / 12636）。
                Doc.InvalidateAll();
                SettleFrames(300);
                int fullInk = WholeInk(s);
                Check($"{name}：松手后整块墨量 = 整层重画（脏区算窄了就会缺一块）",
                      commitInk >= (int)(fullInk * 0.97f),
                      $"松手 {commitInk} vs 整层重画 {fullInk} 像素");
                Doc.Undo();
                SettleFrames(150);
            }

            // 圆拖圆周点（半径 240 → 520 → 140）。另一条半轴 = 半径，跟着一起变。
            ResizeCase("圆·拖圆周点", StrokeKind.Circle, lx + 900f, ly - 300f,
                       ShapeHandle.Rim, new Vector2(1f, 0f), 240f, 240f, 520f, 140f);

            // 椭圆拖两个轴端点（a/b 起手 240/120）：横着、竖着各放大缩小一次。
            // **只有右端点和上端点两条**（用户 2026-09-20 精简：原来左右都管 a、上下都管 b，
            // 各留一个）。"缩小"这一半由 rShrink 覆盖，不依赖另一个手柄。
            ResizeCase("椭圆·拖右端点（管 a）", StrokeKind.Ellipse, lx + 900f, ly - 300f,
                       ShapeHandle.AxisRight, new Vector2(1f, 0f), 240f, 120f, 520f, 140f);
            ResizeCase("椭圆·拖上端点（管 b）", StrokeKind.Ellipse, lx + 900f, ly - 300f,
                       ShapeHandle.AxisTop, new Vector2(0f, -1f), 120f, 240f, 360f, 70f);

            // ---- 顺手检查：三角形 / 平行四边形拖顶点、矩形拖角，有没有同类问题 ----
            //
            // 它们和圆 / 椭圆走的是**同一条"提交时算新几何包围盒"的路**
            // （SetStrokeGeometryAction → Stroke.PaddedBoundsOf），所以一起验。
            // 判据只有一条但很硬：松手后的整块墨量必须等于"整层作废重画"的墨量。
            // （按道理它们是绿的：三角形的三个顶点、平行四边形的四个顶点、矩形的两个对角点，
            //   "点的外接"本来就等于形体自己的外接——但验过才算数。）
            void DragReleaseCase(string name, Stroke s, Vector2 from, Vector2 to, Vector2 oldSpot)
            {
                Doc.Clear();
                Doc.ClearHistory();
                Tool = Tool.Marquee;
                Doc.AddStroke(s);
                Doc.SelectOnly(new[] { s });
                SettleFrames(300);

                var before = s.WorldInkBounds;
                bool took = SelectionGestureForTest(from.X, from.Y);
                UpdateSelectionGestureForTest(to.X, to.Y);
                SettleFrames(120);
                EndSelectionGestureForTest();
                SettleFrames(250);
                Doc.Selected.Clear();                  // 手柄 / 框会盖住形体，先取消选中再量
                SettleFrames(250);

                int commitInk = WholeInk(s);
                int oldInk = Ink(oldSpot.X, oldSpot.Y);
                Check($"{name}：拖到手了（几何真的变了）",
                      took && s.WorldInkBounds.MaxX > before.MaxX + 1f,
                      $"接住={took}，框右边界 {before.MaxX:F0} → {s.WorldInkBounds.MaxX:F0}");
                Check($"{name}：搬走的那一头不留墨（残影）",
                      oldInk < NoInk, $"旧位 ({oldSpot.X:F0},{oldSpot.Y:F0}) 还剩 {oldInk} 像素（期望 <{NoInk}）");
                Doc.InvalidateAll();
                SettleFrames(300);
                int fullInk = WholeInk(s);
                Check($"{name}：松手后整块墨量 = 整层重画（脏区算窄了就会缺一块）",
                      commitInk >= (int)(fullInk * 0.97f),
                      $"松手 {commitInk} vs 整层重画 {fullInk} 像素");
            }

            {   // 三角形：拖那个上顶点，往右搬一大段（跨分块边界的拖法）
                var t = new Stroke
                {
                    Tool = Tool.Triangle, Kind = StrokeKind.Triangle,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
                };
                t.AddPoint(lx + 900f, ly - 700f, 1f, 0);        // 上中
                t.AddPoint(lx + 600f, ly - 260f, 1f, 0);        // 下左
                t.AddPoint(lx + 1200f, ly - 260f, 1f, 0);       // 下右
                var from = SelectionHandles.ShapeHandleCanvasPosition(t, ShapeHandle.Vertex0);
                DragReleaseCase("三角形·拖顶点", t, from, new Vector2(from.X + 460f, from.Y - 120f), from);
            }

            {   // 平行四边形：拖右下那个顶点（它一动，现推的第四个顶点跟着动）
                var q = new Stroke
                {
                    Tool = Tool.Parallelogram, Kind = StrokeKind.Parallelogram,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
                };
                q.AddPoint(lx + 600f, ly - 260f, 1f, 0);        // 底左
                q.AddPoint(lx + 1200f, ly - 260f, 1f, 0);       // 底右
                q.AddPoint(lx + 750f, ly - 700f, 1f, 0);        // 顶左
                var from = SelectionHandles.ShapeHandleCanvasPosition(q, ShapeHandle.Vertex1);
                DragReleaseCase("平行四边形·拖顶点", q, from, new Vector2(from.X + 420f, from.Y - 90f), from);
            }

            {   // 箭头：拖端点。箭头的翅膀尖伸到轴外 ~22 像素（比端点外接宽），
                // 所以它是最像"脏区算窄"的那一个——验下来**没有**这条毛病：
                // 那 22 像素远小于块边长 256，漏掉的那一小条永远和端点带同处一块，
                // 块照样被标脏、照样重画（下面这条判据绿着就是证据）。
                var ar = new Stroke
                {
                    Tool = Tool.Arrow, Kind = StrokeKind.Arrow,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
                };
                ar.AddPoint(lx + 600f, ly - 700f, 1f, 0);       // 尾
                ar.AddPoint(lx + 1250f, ly - 700f, 1f, 0);      // 头（水平 → 翅膀在轴上下各 ~22 像素）
                var from = SelectionHandles.ShapeHandleCanvasPosition(ar, ShapeHandle.Rim);
                DragReleaseCase("箭头·拖端点", ar, from, new Vector2(from.X + 320f, from.Y - 300f), from);
            }

            {   // 矩形：拖右下角。这条走的是**另一条路**（缩放矩阵，不是改几何），
                // 所以单独写：提交的是 TransformObjectsAction（脏区按 PaddedBounds 算）。
                var r = new Stroke
                {
                    Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                    Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
                };
                r.AddPoint(lx + 600f, ly - 700f, 1f, 0);        // 左上
                r.AddPoint(lx + 1100f, ly - 300f, 1f, 0);       // 右下
                Doc.Clear();
                Doc.ClearHistory();
                Tool = Tool.Marquee;
                Doc.AddStroke(r);
                Doc.SelectOnly(new[] { r });
                SettleFrames(300);

                var rectFrame = SelectionHandles.FrameOf(Doc.Selected);
                var corner = SelectionHandles.CanvasPosition(SelHandle.BottomRight, rectFrame, DpiScale);
                var rectBefore = r.WorldInkBounds;
                bool rectTook = SelectionGestureForTest(corner.X, corner.Y);
                // 角往右下拖：矩形被放大（对角那个角钉住不动）
                UpdateSelectionGestureForTest(corner.X + 380f, corner.Y + 220f);
                SettleFrames(120);
                EndSelectionGestureForTest();
                SettleFrames(250);
                Doc.Selected.Clear();
                SettleFrames(250);
                int commitInk = WholeInk(r);
                Doc.InvalidateAll();
                SettleFrames(300);
                int fullInk = WholeInk(r);
                Check("矩形·拖角放大：墨量 = 整层重画 + 右下角确实出去了",
                      rectTook && r.WorldInkBounds.MaxX > rectBefore.MaxX + 1f && commitInk >= (int)(fullInk * 0.97f),
                      $"接住={rectTook}，框右边界 {rectBefore.MaxX:F0} → {r.WorldInkBounds.MaxX:F0}，"
                      + $"松手 {commitInk} vs 整层重画 {fullInk} 像素");
            }
        }

        // 线宽**不跟着缩放**（用户 2026-09-13 拍板、2026-09-20 落地）：
        // 横着把一个矩形拉 3 倍，四条边该多粗还是多粗。
        // 判据用"数墨"——数出来的是屏幕上真看到的东西，比查矩阵可靠。
        Console.WriteLine("  -- P. 拉伸 / 旋转时线宽不变（图形）--");
        {
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

            var rs = new Stroke
            {
                Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 8f * DpiScale,
            };
            rs.AddPoint(700f, 320f, 1f, 0);
            rs.AddPoint(1000f, 520f, 1f, 0);

            Doc.Clear();
            Doc.ClearHistory();
            Tool = Tool.Marquee;
            Doc.AddStroke(rs);
            Doc.Selected.Clear();
            Doc.InvalidateAll();
            SettleFrames(300);

            // 沿一条扫描线数**连续的墨**，返回最长的一段 = 那条边看起来有多粗（物理像素）
            int ThickAcrossY(float x, float yFrom, float yTo)      // 竖着扫：量横边
            {
                int run = 0, best = 0;
                for (int y = (int)yFrom; y <= (int)yTo; y++)
                {
                    if (ScreenProbe.CountMagenta((int)x, y, 2, 1) > 0) { run++; best = Math.Max(best, run); }
                    else run = 0;
                }
                return best;
            }
            int ThickAcrossX(float y, float xFrom, float xTo)      // 横着扫：量竖边
            {
                int run = 0, best = 0;
                for (int x = (int)xFrom; x <= (int)xTo; x++)
                {
                    if (ScreenProbe.CountMagenta(x, (int)y, 1, 2) > 0) { run++; best = Math.Max(best, run); }
                    else run = 0;
                }
                return best;
            }

            int top0 = ThickAcrossY(850f, 300f, 345f);         // 上边（y = 320 那条）
            int right0 = ThickAcrossX(420f, 975f, 1025f);      // 右边（x = 1000 那条）

            // 用**真手势**拖右中柄（横向拉长、纵向不动）——和用户的手一模一样，
            // 也顺便验了"缩放手势提交之后屏幕上的线宽"。
            Doc.SelectOnly(new[] { rs });
            SettleFrames(200);
            var rFrame = SelectionHandles.FrameOf(Doc.Selected);
            var rMid = SelectionHandles.CanvasPosition(SelHandle.Right, rFrame, DpiScale);
            bool rTook = SelectionGestureForTest(rMid.X, rMid.Y);
            UpdateSelectionGestureForTest(rMid.X + 700f, rMid.Y);     // 右边往右拖 700 → 横向拉长
            SettleFrames(150);
            EndSelectionGestureForTest();
            SettleFrames(250);
            Doc.Selected.Clear();
            SettleFrames(250);

            int top1 = ThickAcrossY(1200f, 300f, 345f);        // 上边中段（现在伸到 x = 1700）
            int right1 = ThickAcrossX(420f, 1655f, 1710f);     // 右边（被拖到 x ≈ 1682）
            Check("拖右中柄横拉：**横边**粗细不变（拉伸只改形状，不改线宽）",
                  rTook && top0 >= 6 && Math.Abs(top1 - top0) <= 2,
                  $"接住={rTook}，上边 {top0} → {top1} 物理像素");
            Check("拖右中柄横拉：**竖边**粗细也不变（老做法这里会粗 3 倍，看着像书法笔）",
                  rTook && rs.WorldBounds.MaxX > 1400f && right0 >= 6 && Math.Abs(right1 - right0) <= 2,
                  $"框右边界 {rs.WorldBounds.MaxX:F0}，右边 {right0} → {right1} 物理像素"
                  + $"（老做法约 {right0 * 3}）");

            // 规则本身也钉一条：**图形**不跟着缩放、**手写墨迹**跟着缩放（像图片一样）。
            // 免得以后有人把"线宽不变"顺手套到笔迹上，或者反过来把图形漏掉。
            var inkProbe = new Stroke { Tool = Tool.Pen };          // 自由笔迹
            var shapeProbe = new Stroke { Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle };
            shapeProbe.AddPoint(0f, 0f, 1f, 0);
            shapeProbe.AddPoint(10f, 10f, 1f, 0);
            Check("规则：图形 KeepsWidth=true、手写墨迹=false、图像=false（三种各归各位）",
                  shapeProbe.KeepsWidth && !inkProbe.KeepsWidth
                  && !new Stroke { Kind = StrokeKind.Image }.KeepsWidth,
                  $"矩形 {shapeProbe.KeepsWidth}、笔迹 {inkProbe.KeepsWidth}");
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? $"  PASS: 图形工具（画图入口 / 吸附 / 端点编辑）都正确（{pass} 项）"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// 图形工具摆样出图（离屏，锁屏 / 远程也能出）。
    ///
    ///   · `--shapetoolshow &lt;图&gt;`：**选中一条直线**——两个端点手柄 + 旋转柄；
    ///   · `--shapetoolshow &lt;图&gt; --drag`：**拖端点中**——临时几何 + 倾斜角读数 α；
    ///   · `--shapetoolshow &lt;图&gt; --draw`：**正在画一条直线**——实时几何 + α 读数
    ///     （2026-09-18 用户要的"画线时也要显示 α"）；
    ///   · `--shapetoolshow &lt;图&gt; --rotate`：**旋转拖动中**——给用户核对
    ///     "转的时候那个框看着不对劲"（2026-09-18 的第三条）；
    ///   · `--shapetoolshow &lt;图&gt; --rotated`：**已经转过 60° 的线**、静止选中
    ///     （紧框改口径前后的对照：旧口径下这张同样是虚胖的）；
    ///   · `--shapetoolshow &lt;图&gt; --circle / --ellipse / --triangle / --parallelogram`：
    ///     **那个图形静止选中**——给第二批①（圆 / 椭圆）补出图、并新增②（三角形 /
    ///     平行四边形）那两张，看的是"手柄是不是只有定义元素那几个、第四个角有没有手柄"；
    ///   · 再加 `--snap`：把被拖的顶点拖到"会在容差里"的位置停住，
    ///     拍下**吸附生效的那一帧**（胶囊上写「等边」这类字，强调色）。
    ///   · `--rectangle` + `--pose`：**拖旋转柄**、拍姿态角读数那一帧（矩形停在吸住的 0°）；
    ///   · `--ellipse` + `--pose`：**歪椭圆拖到吸住的 90°**（用户提这个需求的初衷）；
    ///   · `--triangle` + `--angles`：**拖着一个顶点、不松手**，拍三个内角那一帧
    ///     （**没有**"内角和"那一行，用户 2026-09-19 定："显示和太乱"；
    ///      2026-09-20 又定"三角形改成**拖动时才显示**"，所以这张也必须拖着拍）；
    ///   · `--parallelogram` + `--angles`：拖一个顶点、拍两个夹角那一帧。
    ///
    /// 为什么这几张必须出图而不是靠自检：手柄的样子、标签的位置与排版**只能看**，
    /// 几何全对也一样难看（这是仓库里"出图"这一组的由来）。
    ///
    ///   · 再加 `--autosel`（2026-09-22）：**画完自动选中那个框**那一帧——真机拖一个矩形、
    ///     松手之后**工具还是矩形**，框只有"框 ＋ 手柄 ＋ 旋转柄"、**没有操作条**
    ///    （那一条只在框选工具下出现，理由见 `Engine.SelectionBarShown`）。
    ///     这张图看的就是"没有一个条挂在下面"这件事本身。
    /// </summary>

}
