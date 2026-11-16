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


internal sealed partial class App
{
    private void AiShow(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 面板 + 题卡摆样（1A：mock，不联网）===");

        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ShowHud = false;

        if (Ui is not InkUi.FullUi ui)
        {
            Console.WriteLine("界面不是 FullUi，出图取消。");
            _quit = true;
            return;
        }

        // ① 面板
        ui.OpenAiPanelForTest();
        SettleFrames(400);
        string p1 = path;
        bool ok1 = ScreenProbe.SaveBmp(p1, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ② 生成 mock 题卡（答案默认折叠）
        ui.GenerateMockForTest();
        SettleFrames(400);
        string p2 = System.IO.Path.ChangeExtension(path, null) + "-card.bmp";
        bool ok2 = ScreenProbe.SaveBmp(p2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ③ 选中框（选择工具 + 点选卡片）
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card != null)
        {
            Host.Commands.SetTool(Tool.Marquee);
            Doc.SelectOnly(new[] { card });
            SettleFrames(300);
        }
        string ps = System.IO.Path.ChangeExtension(path, null) + "-selected.bmp";
        bool oks = card != null && ScreenProbe.SaveBmp(ps, (int)_virtualX, (int)_virtualY,
                                                       (int)_virtualW, (int)_virtualH);

        // ④ 展开答案
        ToggleTextBoxAnswersForTest();
        SettleFrames(300);
        string p3 = System.IO.Path.ChangeExtension(path, null) + "-answer.bmp";
        bool ok3 = ScreenProbe.SaveBmp(p3, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ⑤ 普通文本框：同样 8 手柄、可改字号
        Doc.Clear();
        Doc.ClearHistory();
        string sample = "# 例题\n\n"
            + "已知数列 \\(\\{a_n\\}\\) 满足：\n"
            + "\\[\na_1=2,\\qquad a_{n+1}=3a_n+2\\quad(n\\ge 1)\n\\]\n"
            + "求数列 \\(\\{a_n\\}\\) 的通项公式。\n\n"
            + "### 分析\n\n"
            + "形式：\\(a_{n+1}=pa_n+q\\)，用待定系数构造等比数列。\n"
            + "设 \\(a_{n+1}+c=3(a_n+c)\\)，展开得 \\(a_{n+1}=3a_n+2c\\)，\n"
            + "与原式比较得 \\(2c=2\\Rightarrow c=1\\)。\n\n"
            + "行内中文：\\(\\text{当 }n\\ge 1\\text{ 时，}a_n>0\\)。\n"
            + "中文混排：\n"
            + "\\[\n\\text{当 }a_n>0\\text{ 时，}a_{n+1}=3a_n+2\n\\]\n"
            + "括号：\n"
            + "\\[\na_{n+1}+An+B=2\\bigl(a_n+A(n-1)+B\\bigr)\n\\]\n"
            + "```latex\n\\boxed{a_n=7\\cdot 2^{n-1}-3n-3}\n```\n"
            + "所以：\n"
            + "\\[\na_n+1=3\\cdot 3^{n-1}=3^n\n\\]\n"
            + "**答案：** \\(\\boxed{a_n=3^n-1}\\)";
        // 走真实路径：插入空框 → 原生 EDIT 粘贴长文 → 结束编辑（会重新排版长高）
        InsertPlainTextBoxForTest("");
        var editTarget = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (editTarget != null)
        {
            BeginTextBoxEdit(editTarget);
            SetTextBoxEditTextForTest(sample);
            EndTextBoxEditFromUi(true);
        }
        Doc.SelectOnly(editTarget != null ? new[] { editTarget } : Array.Empty<Stroke>());
        Host.Commands.SetTool(Tool.Marquee);
        SettleFrames(300);
        string p4 = System.IO.Path.ChangeExtension(path, null) + "-textbox.bmp";
        bool ok4 = ScreenProbe.SaveBmp(p4, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ⑥ 改字色：文字和公式图都应该跟着变，不能只变文字
        SetSelectionColor(new Color4(0.80f, 0.18f, 0.18f, 1f));
        SettleFrames(200);
        string p5 = System.IO.Path.ChangeExtension(path, null) + "-red.bmp";
        bool ok5 = ScreenProbe.SaveBmp(p5, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ⑦ 删除：删完不能有残字；再框选/点删除也不能命中已删对象
        // 先做一次真实的"改宽 → 长高 → 重登记索引"，再删。
        if (editTarget?.TextBox != null)
        {
            float hBeforeWidth = editTarget.TextBox.Height;
            var oldB = editTarget.PaddedBounds;
            editTarget.TextBox.Width *= 0.85f;
            editTarget.TextBox.InvalidateLayout();
            editTarget.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(editTarget);
            Doc.Dirty.Add(oldB);
            Doc.Dirty.Add(editTarget.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            SettleFrames(150);
            bool manualKept = !editTarget.TextBox.AutoHeight
                              && MathF.Abs(editTarget.TextBox.Height - hBeforeWidth) < 1f;
            Console.WriteLine(manualKept
                ? $"改宽后保持手动高度：{hBeforeWidth:0} → {editTarget.TextBox.Height:0}"
                : $"改宽后高度跳变：{hBeforeWidth:0} → {editTarget.TextBox.Height:0} auto={editTarget.TextBox.AutoHeight}");
        }
        // 改窄后拖下边：高度应跟指针平滑变化，不能跳回"自动全高"
        if (editTarget?.TextBox != null)
        {
            var frameW = SelectionHandles.FrameOf(Doc.Selected);
            var bh = SelectionHandles.CanvasPosition(SelHandle.Bottom, frameW, UiUnit);
            float hPreDrag = editTarget.TextBox.Height;
            SelectionGestureForTest(bh.X, bh.Y);
            UpdateSelectionGestureForTest(bh.X, bh.Y - 90f);
            EndSelectionGestureForTest();
            SettleFrames(150);
            float hPostDrag = editTarget.TextBox.Height;
            bool smooth = !editTarget.TextBox.AutoHeight
                          && hPostDrag < hPreDrag - 20f && hPostDrag > hPreDrag - 180f;
            Console.WriteLine(smooth
                ? $"手动拖下边平滑：{hPreDrag:0} → {hPostDrag:0}"
                : $"手动拖下边异常：{hPreDrag:0} → {hPostDrag:0} auto={editTarget.TextBox.AutoHeight}");
        }
        // 内部滚动：固定高度小于内容时，滚轮/ScrollBy 只动内容，不动画布
        if (editTarget?.TextBox != null)
        {
            var tbCard = editTarget.TextBox;
            var oldB = editTarget.PaddedBounds;
            tbCard.Height = MathF.Max(200f, MathF.Min(400f, tbCard.ContentHeight * 0.5f));
            tbCard.AutoHeight = false;
            tbCard.InvalidateLayout();
            editTarget.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(editTarget);
            Doc.Dirty.Add(oldB);
            Doc.Dirty.Add(editTarget.PaddedBounds);
            bool scrollOk = tbCard.MaxScroll > 1f && tbCard.ScrollBy(90f) && tbCard.ScrollY > 0.5f;
            // ?????????????????????????????
            tbCard.ScrollY = 0f;
            var bb = editTarget.PaddedBounds;
            PointerX = (bb.MinX + bb.MaxX) * 0.5f;
            PointerY = (bb.MinY + bb.MaxY) * 0.5f + ViewOffsetY;
            PointerInside = true;
            HandleWheel(new IntPtr((long)(((-120) & 0xFFFF) << 16)));
            bool wheelOk = tbCard.ScrollY > 0.5f;
            scrollOk &= wheelOk;
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            SettleFrames(150);
            string p7 = System.IO.Path.ChangeExtension(path, null) + "-scroll.bmp";
            ScreenProbe.SaveBmp(p7, (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Console.WriteLine(scrollOk
                ? $"内部滚动通过：内容 {tbCard.ContentHeight:0} 高，框 {tbCard.Height:0} 高，ScrollY={tbCard.ScrollY:0}"
                : $"内部滚动失败：content={tbCard.ContentHeight:0} height={tbCard.Height:0} scroll={tbCard.ScrollY:0}");
        }
        var victim = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        var vb = victim?.PaddedBounds ?? default;
        RunBarButtonForTest(SelBarButton.Delete);   // 走真实操作条删除路径
        SettleFrames(300);
        string p6 = System.IO.Path.ChangeExtension(path, null) + "-deleted.bmp";
        bool ok6 = ScreenProbe.SaveBmp(p6, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);
        bool clean = Doc.Strokes.Count == 0 && Doc.Selected.Count == 0;
        bool ghost = false;
        if (victim != null)
        {
            var candidates = new List<Stroke>();
            Doc.QueryGrid(vb, candidates);
            ghost |= candidates.Count > 0;
            for (int gy = 0; gy <= 2 && !ghost; gy++)
                for (int gx = 0; gx <= 2 && !ghost; gx++)
                {
                    float px = vb.MinX + (vb.MaxX - vb.MinX) * (0.15f + 0.35f * gx);
                    float py = vb.MinY + (vb.MaxY - vb.MinY) * (0.15f + 0.35f * gy);
                    if (Doc.HitObjectAt(px, py, 8f) != null) ghost = true;
                }
        }
        clean &= !ghost;
        // 操作条统一"给下限、不翻面"：删除格也必须点得到（画/命中同源）。
        bool textBar = Doc.Selected.Count == 1 && Doc.Selected[0].Kind == StrokeKind.TextBox;
        int barCount = SelectionHandles.BarButtonCountFor(textBar);
        int delIndex = SelectionHandles.BarIndexOf(SelBarButton.Delete, textBar);
        var barRect = SelectionHandles.BarRect(vb, UiUnit, FurnitureViewport, barCount);
        var delBtn = SelectionHandles.BarButtonRectIn(barRect, delIndex, UiUnit);
        bool delHit = SelectionHandles.BarButtonAtIn((delBtn.MinX + delBtn.MaxX) * 0.5f,
                                                     (delBtn.MinY + delBtn.MaxY) * 0.5f,
                                                     barRect, UiUnit, barCount) == delIndex;
        clean &= delHit;
        Console.WriteLine(clean
            ? $"删除自检通过：strokes={Doc.Strokes.Count} selected={Doc.Selected.Count}"
            : $"删除自检失败：strokes={Doc.Strokes.Count} selected={Doc.Selected.Count} ghost={ghost} delHit={delHit}");

        // 删除 → 撤销 → 重做：反复进出不能让框/字残留
        bool undoOk = true, redoOk = true;
        if (victim != null)
        {
            Doc.Undo();
            SettleFrames(300);
            undoOk = Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], victim);
            string pu = System.IO.Path.ChangeExtension(path, null) + "-undo.bmp";
            ScreenProbe.SaveBmp(pu, (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Doc.Redo();
            SettleFrames(300);
            redoOk = Doc.Strokes.Count == 0;
            string pr = System.IO.Path.ChangeExtension(path, null) + "-redo.bmp";
            ScreenProbe.SaveBmp(pr, (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        }
        Console.WriteLine(undoOk && redoOk ? "删除撤销/重做通过" : $"删除撤销/重做失败：undo={undoOk} redo={redoOk}");

        // 编辑中直接删：先收原生 EDIT，不能留一个还在显示文字的窗口
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("编辑中删除测试");
        var st2 = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (st2 != null)
        {
            BeginTextBoxEdit(st2);
            SetTextBoxEditTextForTest("编辑中删除测试\n第二行文字");
            RunBarButtonForTest(SelBarButton.Delete);
            SettleFrames(250);
        }
        bool editDelOk = !TextBoxEditing && Doc.Strokes.Count == 0 && Doc.Selected.Count == 0;
        Console.WriteLine(editDelOk ? "编辑中删除通过" : $"编辑中删除失败：editing={TextBoxEditing} strokes={Doc.Strokes.Count}");

        // 第一次粘贴的尺寸也要能随文字一起撤销/重做
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("");
        var st3 = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (st3 != null)
        {
            float emptyW = st3.TextBox.Width, emptyH = st3.TextBox.Height;
            var emptyPos = new Vector2(st3.Points[0].X, st3.Points[0].Y);
            BeginTextBoxEdit(st3);
            SetTextBoxEditTextForTest("短文本");
            EndTextBoxEditFromUi(true);
            SettleFrames(150);
            float fitW = st3.TextBox.Width, fitH = st3.TextBox.Height;
            var fitPos = new Vector2(st3.Points[0].X, st3.Points[0].Y);
            Doc.Undo();
            SettleFrames(100);
            bool undoOk2 = st3.TextBox.Text.Length == 0
                           && MathF.Abs(st3.TextBox.Width - emptyW) < 1f
                           && MathF.Abs(st3.TextBox.Height - emptyH) < 1f
                           && st3.TextBox.AutoHeight
                           && Vector2.Distance(emptyPos, new Vector2(st3.Points[0].X, st3.Points[0].Y)) < 1f;
            Doc.Redo();
            SettleFrames(100);
            bool redoOk2 = st3.TextBox.Text == "短文本"
                           && MathF.Abs(st3.TextBox.Width - fitW) < 1f
                           && MathF.Abs(st3.TextBox.Height - fitH) < 1f
                           && !st3.TextBox.AutoHeight;
            Console.WriteLine(undoOk2 && redoOk2
                ? $"首贴尺寸撤销/重做通过：{emptyW:0}×{emptyH:0} ↔ {fitW:0}×{fitH:0}"
                : $"首贴尺寸撤销/重做失败：undo={undoOk2} redo={redoOk2} 当前 {st3.TextBox.Width:0}×{st3.TextBox.Height:0} auto={st3.TextBox.AutoHeight}");
        }

        Console.WriteLine(ok1 && ok2 && ok3 && oks && ok4 && ok5 && ok6
            ? $"出图：{p1} / {p2} / {ps} / {p3} / {p4} / {p5} / {p6}"
            : "抓屏失败（看上一行）");
        Console.WriteLine($"题卡/文本框数：{TextBoxCountForTest}（删除后应为 0）");
        _quit = true;
    }

    private void RadialShow(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 呼出盘摆样（Ctrl+Alt+Shift+Q）===");

        // --dark：深色那档也出一张（呼出盘跟着 FloatingTheme 走，这里顺手验观感）。
        // 自检模式用临时配置（`_selfCheckMode`），不会碰用户的 settings.json。
        if (Environment.GetCommandLineArgs().Contains("--dark")) SetUiPref("dark", "1");

        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ShowHud = false;
        RadialTestHold = true;

        // --customband：把主条色带改成**非默认**（band[1] 绿、band[2] 橙），
        // 用来验"呼出盘的北格/东西格跟着活色带走"（2026-11-16）。
        // 顺手开白板：出图背景干净，色块/垃圾桶看得清（和 --axisshow 同一套做法）。
        if (Environment.GetCommandLineArgs().Contains("--customband"))
        {
            BoardOn = true;
            PenSwatchColors[1] = new Color4(0.20f, 0.78f, 0.35f, 1f);
            PenSwatchColors[2] = new Color4(0.98f, 0.52f, 0.10f, 1f);
            CurrentColor = PenSwatchColors[0];       // 当前 = 第一格：下一色才是改过的绿
        }

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // ① 打开（还没划）
        RadialOpenForTest(cx, cy);
        SettleFrames(400);
        bool ok1 = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ② 划向「红笔」（正北；v3 起北格固定红，东西才是翻色）
        RadialMoveForTest(cx, cy - 120f);
        SettleFrames(300);
        string path2 = System.IO.Path.ChangeExtension(path, null) + "-red.bmp";
        bool ok2 = ScreenProbe.SaveBmp(path2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ③ 划向「橡皮」（正南）：验证中央图标跟着扇区变、色环压暗
        RadialMoveForTest(cx, cy + 120f);
        SettleFrames(300);
        string path3 = System.IO.Path.ChangeExtension(path, null) + "-eraser.bmp";
        bool ok3 = ScreenProbe.SaveBmp(path3, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        RadialCancelForTest("摆样结束");
        RadialTestHold = false;
        Console.WriteLine(ok1 && ok2 && ok3 ? $"出图：{path} / {path2} / {path3}" : "抓屏失败（看上一行）");
        _quit = true;
    }
    private static byte[] MakeLegacyFile(InkDocument doc, int version, int droppedBytes)
    {
        byte[] cur = InkSerializer.Save(doc);
        byte[] old = droppedBytes > 0 ? new byte[cur.Length - droppedBytes] : cur;
        if (droppedBytes > 0) Array.Copy(cur, old, old.Length);
        // 魔数（8 字节）后面那个小端 int32 就是版本号。
        old[4] = (byte)version; old[5] = 0; old[6] = 0; old[7] = 0;
        return old;
    }
    private void AxisShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        float x0 = _virtualX + 460f, y0 = _virtualY + 400f;
        // 外框（两张坐标系共用）：x0..x0+760 × y0..y0+620，所以半宽半高 = (380, 310)。
        // **画法改成了"按下 = 原点、拖出去 = 展开"**（见 Stroke.SetAxisBox），
        // 所以这里的第一参数是原点、第二参数是"往右下拖多远"，不再是外框的两个角。
        const float hw = 380f, hh = 310f;

        // ① 坐标系 · 带网格，原点摊在框的左下（老师最常用的"只留第一象限"）
        var g = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = true,
        };
        for (int i = 0; i < 3; i++) g.AddPoint(x0 + 130f, y0 + 470f, 1f, 0);
        g.SetAxisBox(x0 + 130f, y0 + 470f, x0 + 130f + hw, y0 + 470f + hh);
        Doc.AddStroke(g);

        // ② 坐标系 · 不带网格，原点在框正中间（和左边对照）
        var c = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = false,
        };
        for (int i = 0; i < 3; i++) c.AddPoint(x0 + 900f + hw, y0 + hh, 1f, 0);
        c.SetAxisBox(x0 + 900f + hw, y0 + hh, x0 + 900f + hw + hw, y0 + hh + hh);
        Doc.AddStroke(c);

        // ③ 数轴：一条水平线 + 右端箭头（没有刻度）
        var n = new Stroke
        {
            Tool = Tool.NumberLine, Kind = StrokeKind.NumberLine,
            Color = ink, Width = 4f * DpiScale,
        };
        for (int i = 0; i < 2; i++) n.AddPoint(x0, y0 + 760f, 1f, 0);
        n.SetAxisBox(x0, y0 + 760f, x0 + 1660f, y0 + 760f);
        Doc.AddStroke(n);

        // ④ 一条虚线直线（顺带把线型也看进去：虚线在粗笔上是不是像样）
        var d = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = ink, Width = 7f * DpiScale, Dash = StrokeDash.Dashed,
        };
        d.AddPoint(x0, y0 + 960f, 1f, 0);
        d.AddPoint(x0 + 1660f, y0 + 960f, 1f, 0);
        Doc.AddStroke(d);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)(x0 - 60f), (int)(y0 - 60f), 1900, 1150);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 出图：函数曲线各来一份——**抛物线四种开口、双曲线两个方向、正弦 / 余弦各一个周期、
    /// 波浪线三个周期、正切一支**。
    /// 用法 `--curveshow [路径]`，默认 `reports/四种曲线.bmp`。
    ///
    /// 自检盯的是"数值对不对"，这一张是给**眼睛**看的（这条教训见 计划-图形工具.md 10.7）：
    ///   · 曲线胖不胖、够不够顺（段数按尺寸定，太少了看着是折线）；
    ///   · 双曲线**截断到哪**（`Stroke.HyperbolaTMaxOf`：出框就停笔、最多画到 `2a`：
    ///     太短看着像两根短线、太长就把包围盒撑得很大）；
    ///   · 四种开口是不是真的四个方向（左右开口是"换自变量"，最容易写反）；
    ///   · 正弦 / 余弦的**起点**对不对（正弦从轴起、余弦从峰起），而且**都是一个周期**；
    ///   · **波浪线**是不是"很多个周期"（和正弦那一张摆在一起看，一眼就该分得开）；
    ///   · 正切**两侧的渐近线**在不在、曲线有没有贴着它上去（这条靠 `TangentMinAspect` 兜着，
    ///     所以这一张**故意只拖 60 高**：画出来仍该是贴着渐近线的那一支）。
    /// 摆放：4 列 × 3 行，第一行四种开口、第二行"双曲线两个方向 ＋ 正弦 / 余弦 / 波浪线 / 正切"、
    /// 第三行四种立体。第二行挤了 6 个图形，所以位置是按"不碰上邻居"逐个摆的（见各段注释）。
    /// </summary>
    /// <summary>
    /// 出图（`--smoothshow [路径]`）：**曲线化开/关的对照**（2026-09-28）。
    ///
    /// 同一组"手写样本"画两遍——先按现在的折线出 `-off`，再把曲线化打开、整层重画出 `-on`：
    ///   ① 快弧：采样故意稀（模拟"关掉 Windows Ink 的笔 / 鼠标"）；
    ///   ② 直角：必须原样保留（用户唯一的硬约束）；
    ///   ③ 抛物线：手画曲线不许被改形（上次砍"保形平滑"就是因为这个）；
    ///   ④ 锯齿：连续尖角，一个都不许被磨圆。
    /// 好不好看只能靠眼睛，所以单开一条出图命令（判据交给下一轮真人对比）。
    /// </summary>
}
