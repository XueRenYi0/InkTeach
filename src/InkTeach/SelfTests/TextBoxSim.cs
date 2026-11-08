// 文本框仿真自检（--textboxsim）
//
// 只测文本对象：真实鼠标/键盘消息 + 每个关键阶段截图。
// 目标不是"读内部状态自己骗自己"，而是把用户实际会做的动作按 ClassIn 流程走一遍：
//   点文本格 → 点空白出窄框 → 逐字输入（每字变宽）→ 点空白出新框
//   → 悬停/选中/拖动/双击编辑 → 操作条（收起/字号/颜色）
//   → 左右竖线改宽、四角缩放、无上下手柄 → 色带与操作条分离
//   → 清空/垃圾桶删除/撤销 → 橡皮 → 滚动时操作条 → 存读。
using System.Numerics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    private void TextBoxSimTest()
    {
        if (SkipIfNoSyntheticInput("文本框仿真自检")) { _quit = true; return; }

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-36} {detail}");
        }

        string dir = System.IO.Path.Combine("reports", "textbox-sim");
        System.IO.Directory.CreateDirectory(dir);
        void Shot(string name)
        {
            // 截图先存 BMP（ScreenProbe 的现成路径），再压成 PNG 并删 BMP：
            // 2520×1680 的 BMP 每张 ~17MB，22 张会把磁盘塞满。
            string bmp = System.IO.Path.Combine(dir, name + ".bmp");
            string png = System.IO.Path.Combine(dir, name + ".png");
            if (!ScreenProbe.SaveBmp(bmp, (int)_virtualX, (int)_virtualY,
                                     (int)_virtualW, (int)_virtualH)) return;
            try
            {
                using var input = SkiaSharp.SKBitmap.Decode(bmp);
                if (input != null)
                {
                    using var image = SkiaSharp.SKImage.FromBitmap(input);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                    using var fs = System.IO.File.Create(png);
                    data.SaveTo(fs);
                }
                System.IO.File.Delete(bmp);
            }
            catch
            {
                // 转换失败就保留 BMP，至少截图还在。
            }
        }

        void ClickCanvas(float x, float y)
        {
            SendMouse((int)x, (int)y, 0); SettleFrames(60);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(80);
            SendMouse((int)x, (int)y, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
        }
        void ClickCanvasPoint(float x, float y)
            => ClickCanvas(_virtualX + x, _virtualY + y + ViewOffsetY);
        void ClickLogical(in RectF r)
            => ClickCanvas(_virtualX + (r.MinX + r.MaxX) * 0.5f * DpiScale,
                           _virtualY + (r.MinY + r.MaxY) * 0.5f * DpiScale);
        void DragCanvasPoint(float x0, float y0, float x1, float y1)
        {
            int sx = (int)(_virtualX + x0), sy = (int)(_virtualY + y0 + ViewOffsetY);
            int ex = (int)(_virtualX + x1), ey = (int)(_virtualY + y1 + ViewOffsetY);
            SendMouse(sx, sy, 0); SettleFrames(60);
            SendMouse(sx, sy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(70);
            SendMouse(ex, ey, 0); SettleFrames(150);
            SendMouse(ex, ey, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
        }

        // ---- 起手：完整界面、文本格 ----
        PassMode = PassThroughMode.LayeredTransparent;
        PassThrough = false;
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        SetUiFactory(() => new InkUi.FullUi());
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        SettleFrames(320);

        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null)
        {
            Console.WriteLine("  界面没挂上，停止");
            ExitCode = 1;
            _quit = true;
            return;
        }
        ui.SetProfileForTest(2);
        ui.SetPinForTest(7, true);
        SettleFrames(180);
        var textCell = ui.CellRectForTest(7);
        ClickLogical(textCell);
        Check("主栏点「文本」= 文本工具", Tool == Tool.Text && ui.CellActiveForTest(7),
              $"Tool={Tool} active={ui.CellActiveForTest(7)}");

        // ---- 1. 新框：窄、逐字变宽 ----
        float cx = _virtualX + _virtualW * 0.34f;
        float cy = _virtualY + _virtualH * 0.30f;
        ClickCanvas(cx, cy);
        var box = Doc.Selected.Count == 1 ? Doc.Selected[0] : null;
        Check("点空白：同一下新建并进入编辑",
              TextBoxEditing && box?.TextBox is { Role: TextBoxRole.Normal },
              $"editing={TextBoxEditing} 选择={Doc.Selected.Count}");
        if (box?.TextBox == null)
        {
            Console.WriteLine("  没建出文本框，停止");
            ExitCode = 1;
            _quit = true;
            return;
        }

        var tb = box.TextBox;
        float initialW = tb.Width;
        float left0 = box.Points[0].X;
        Check("新框起始宽只够约一个字", initialW > 0f && initialW <= 72f * tb.Unit,
              $"宽={initialW:F0} unit={tb.Unit:F2}");
        Shot("01-create-narrow");

        float wPrev = initialW;
        for (int i = 1; i <= 12; i++)
        {
            TypeTextBoxEditCharForTest('你');
            SettleFrames(150);
            float w = tb.Width;
            if (i >= 2)
                Check($"输入第 {i} 个汉字：框变宽", w > wPrev + 1f,
                      $"{wPrev:F0} → {w:F0}");
            wPrev = w;
            if (i is 1 or 2 or 12) Shot($"02-typing-{i:00}");
        }
        Check("逐字输入左沿不动", MathF.Abs(box.Points[0].X - left0) < 1f,
              $"{left0:F0} → {box.Points[0].X:F0}");
        Check("输入后仍自动宽、自动高",
              tb.AutoWidth && tb.AutoHeight && MathF.Abs(tb.ContentHeight - tb.Height) < 2f,
              $"autoW={tb.AutoWidth} autoH={tb.AutoHeight} 高={tb.Height:F0}");
        Shot("03-typed-12");

        // ---- 2. 点空白：旧框普通显示，新框在落点 ----
        var oldBox = box;
        int beforeNew = TextBoxCountForTest;
        ClickCanvas(_virtualX + 70f, _virtualY + 70f);
        Check("编辑中点空白：同一下出新框，旧框取消选中",
              TextBoxEditing && TextBoxCountForTest == beforeNew + 1
              && !Doc.Selected.Contains(oldBox),
              $"editing={TextBoxEditing} 框 {beforeNew}→{TextBoxCountForTest} "
              + $"旧选中={Doc.Selected.Contains(oldBox)}");
        Shot("04-click-away-new");
        EndTextBoxEditFromUi(true);        // 这个新框没输入 → 自动清理
        SettleFrames(120);

        // ---- 3. 靠右沿：不超出屏幕，到边换行/最小左让 ----
        // 必须是**真的在右侧点一下**新建；不能拿上面左上角那个框来测。
        float edgeClickX = _virtualX + _virtualW - 40f * DpiScale;
        float edgeClickY = _virtualY + _virtualH * 0.34f;
        ClickCanvas(edgeClickX, edgeClickY);
        for (int i = 0; i < 10; i++)
        {
            TypeTextBoxEditCharForTest('边');
            SettleFrames(130);
        }
        var edgeBox = Doc.Selected.Count == 1 ? Doc.Selected[0] : null;
        var edgeB = edgeBox?.PaddedBounds ?? default;
        float screenRight = _virtualX + _virtualW;
        Check("靠右沿输入：右沿不出屏",
              edgeBox != null && edgeB.MaxX <= screenRight - 16f * edgeBox.TextBox.Unit + 2f,
              $"右沿={edgeB.MaxX:F0} 屏右={screenRight:F0} 宽={edgeBox?.TextBox?.Width:F0}");
        Shot("05-right-edge");
        EndTextBoxEditFromUi(true);
        SettleFrames(120);

        // ---- 4. 悬停 / 选中 / 拖动 / 双击 ----
        var ob = oldBox.PaddedBounds;
        float ocx = (ob.MinX + ob.MaxX) * 0.5f;
        float ocy = (ob.MinY + ob.MaxY) * 0.5f;
        SendMouse((int)(_virtualX + ocx), (int)(_virtualY + ocy + ViewOffsetY), 0);
        SettleFrames(120);
        Check("未选中文字：悬停 I 字", ComputeCursorKind() == CursorKind.IBeam,
              $"cursor={ComputeCursorKind()}");
        ClickCanvasPoint(ocx, ocy);
        Check("第一下：只选中、不编辑",
              !TextBoxEditing && Doc.Selected.Count == 1 && Doc.Selected.Contains(oldBox),
              $"editing={TextBoxEditing} 选中={Doc.Selected.Count}");
        SendMouse((int)(_virtualX + ocx), (int)(_virtualY + ocy + ViewOffsetY), 0);
        SettleFrames(120);
        Check("选中文字：悬停移动十字", ComputeCursorKind() == CursorKind.Move,
              $"cursor={ComputeCursorKind()}");
        Shot("06-selected-bar");

        var op = oldBox.Points.Count > 0 ? oldBox.Points[0] : default;
        float ox0 = op.X, oy0 = op.Y;
        DragCanvasPoint(ocx, ocy, ocx - 140f, ocy + 90f);
        var op1 = oldBox.Points.Count > 0 ? oldBox.Points[0] : default;
        Check("选中后拖动：真的移动", MathF.Abs(op1.X - ox0) > 40f && MathF.Abs(op1.Y - oy0) > 30f,
              $"Δ=({op1.X - ox0:F0},{op1.Y - oy0:F0})");
        Shot("07-moved");

        var movedB = oldBox.PaddedBounds;
        float mcx = (movedB.MinX + movedB.MaxX) * 0.5f;
        float mcy = (movedB.MinY + movedB.MaxY) * 0.5f;
        ClickCanvasPoint(mcx, mcy);
        ClickCanvasPoint(mcx, mcy);
        Check("双击：进入编辑", TextBoxEditing, $"editing={TextBoxEditing}");
        Check("编辑时：选中框 / 操作条隐藏", !SelectionBarShown,
              $"barShown={SelectionBarShown}");
        Shot("08-edit");
        float beforeAppend = oldBox.TextBox?.Width ?? 0f;
        TypeTextBoxEditCharForTest('你');
        TypeTextBoxEditCharForTest('好');
        SettleFrames(220);
        Check("再次编辑：AutoWidth 仍开，继续变宽",
              oldBox.TextBox != null && oldBox.TextBox.AutoWidth
              && oldBox.TextBox.Width > beforeAppend + 1f,
              $"{beforeAppend:F0} → {oldBox.TextBox?.Width:F0}");
        Shot("09-reedit-width");
        EndTextBoxEditFromUi(true);
        SettleFrames(120);

        // ---- 5. 操作条：七格 / 收起 / 字号 / 颜色 ----
        ClickCanvasPoint(mcx, mcy);
        Check("选中文字：七格操作条出现",
              SelectionBarShown && SingleTextBoxSelection
              && SelectionHandles.BarButtonCountFor(true) == 7,
              $"bar={SelectionBarShown} 七格={SelectionHandles.BarButtonCountFor(true)}");
        var bar = SelectionBarCanvasRect;
        Check("操作条在下方、贴底不翻面",
              bar.MinY > oldBox.PaddedBounds.MaxY - 2f,
              $"框底={oldBox.PaddedBounds.MaxY:F0} 条={bar.MinY:F0}");
        var vp = FurnitureViewport;
        var lowSel = new RectF
        {
            MinX = cx, MinY = vp.MaxY - 90f, MaxX = cx + 220f, MaxY = vp.MaxY - 60f,
        };
        var lowBar = SelectionHandles.BarRect(lowSel, UiUnit, vp,
            SelectionHandles.BarButtonCountFor(true));
        Check("操作条贴底：不翻面、卡在下限",
              lowBar.MaxY <= vp.MaxY - SelectionHandles.BarMinBottomMarginLogical * UiUnit + 1f
              && lowBar.MaxY > lowSel.MaxY,
              $"条底={lowBar.MaxY:F0} 下限={vp.MaxY - SelectionHandles.BarMinBottomMarginLogical * UiUnit:F0}");
        Shot("10-operation-bar");

        var collapse = SelectionHandles.BarButtonRectIn(bar, 0, UiUnit);
        ClickCanvasPoint((collapse.MinX + collapse.MaxX) * 0.5f,
                         (collapse.MinY + collapse.MaxY) * 0.5f);
        Check("点 ×：收成圆钮", BarDrawnCollapsed, $"collapsed={BarDrawnCollapsed}");
        Shot("11-collapsed-dot");
        var dot = SelectionDotCanvasRect;
        ClickCanvasPoint((dot.MinX + dot.MaxX) * 0.5f, (dot.MinY + dot.MaxY) * 0.5f);
        Check("点圆钮：重新展开", !BarDrawnCollapsed, $"collapsed={BarDrawnCollapsed}");

        bar = SelectionBarCanvasRect;
        var fontBtn = SelectionHandles.BarButtonRectIn(bar, 1, UiUnit);
        ClickCanvasPoint((fontBtn.MinX + fontBtn.MaxX) * 0.5f,
                         (fontBtn.MinY + fontBtn.MaxY) * 0.5f);
        Check("点字号：打开字号下拉", SelPanelOpen == SelPanel.FontSize,
              $"panel={SelPanelOpen}");
        Shot("12-font-menu");
        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        var fc = SelectionHandles.FontPanelCellRect(5, aabb, UiUnit, FurnitureViewport);
        float fontBefore = oldBox.TextBox?.FontSize ?? 0f;
        ClickCanvasPoint((fc.MinX + fc.MaxX) * 0.5f, (fc.MinY + fc.MaxY) * 0.5f);
        Check("字号下拉选 64：只改当前文字",
              oldBox.TextBox != null && MathF.Abs(oldBox.TextBox.FontSize - 64f) < 0.2f
              && MathF.Abs(Host.State.TextFontSize - 18f) < 0.2f,
              $"文字={oldBox.TextBox?.FontSize:F0} 默认={Host.State.TextFontSize:F0}");
        Shot("13-font-64");
        Host.Commands.Undo();
        SettleFrames(120);

        bar = SelectionBarCanvasRect;
        var colorBtn = SelectionHandles.BarButtonRectIn(bar, 2, UiUnit);
        ClickCanvasPoint((colorBtn.MinX + colorBtn.MaxX) * 0.5f,
                         (colorBtn.MinY + colorBtn.MaxY) * 0.5f);
        Check("点颜色：打开色板（文字面板没有粗细/线型）",
              SelPanelOpen == SelPanel.Ink
              && SelectionHandles.SliderRect(aabb, UiUnit, FurnitureViewport,
                                             SelectionHandles.SwatchCount, true).IsEmpty,
              $"panel={SelPanelOpen}");
        Shot("14-color-panel");
        var redSw = SelectionHandles.SwatchRect(4, aabb, UiUnit, FurnitureViewport,
                                                SelectionHandles.SwatchCount, true);
        var bandBefore = Host.State.TextColor;
        ClickCanvasPoint((redSw.MinX + redSw.MaxX) * 0.5f,
                         (redSw.MinY + redSw.MaxY) * 0.5f);
        Check("操作条改字色：只改当前文字",
              oldBox.TextBox != null && oldBox.TextBox.Ink.R > 0.8f
              && MathF.Abs(Host.State.TextColor.R - bandBefore.R) < 0.01f,
              $"文字=({oldBox.TextBox?.Ink.R:F2}) 默认=({Host.State.TextColor.R:F2})");
        Shot("15-color-red");
        Host.Commands.Undo();
        SettleFrames(120);

        // ---- 6. 色带只管下一次输入 ----
        ui.SelectBandCellForTest(7);
        SettleFrames(120);
        var bandRed = ui.SwatchRectForTest(0);
        var inkBefore = oldBox.TextBox?.Ink ?? default;
        ClickLogical(bandRed);
        Check("色带点红：只改默认色，不改已选文字",
              Host.State.TextColor.R > 0.8f
              && MathF.Abs(oldBox.TextBox.Ink.R - inkBefore.R) < 0.01f,
              $"默认=({Host.State.TextColor.R:F2}) 文字=({oldBox.TextBox?.Ink.R:F2})");
        Shot("16-band-red");

        // ---- 7. 改宽 / 角缩放 / 无上下手柄 ----
        ClickCanvasPoint(mcx, mcy);
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var rh = SelectionHandles.CanvasPosition(SelHandle.Right, frame, UiUnit);
        float width0 = oldBox.TextBox?.Width ?? 0f;
        DragCanvasPoint(rh.X, rh.Y, rh.X + 120f, rh.Y);
        Check("拖右竖线：宽变宽、高仍自动",
              oldBox.TextBox != null && oldBox.TextBox.Width > width0 + 50f
              && oldBox.TextBox.AutoHeight,
              $"{width0:F0} → {oldBox.TextBox?.Width:F0} autoH={oldBox.TextBox?.AutoHeight}");
        Shot("17-resize-wide");

        frame = SelectionHandles.FrameOf(Doc.Selected);
        var lh = SelectionHandles.CanvasPosition(SelHandle.Left, frame, UiUnit);
        DragCanvasPoint(lh.X, lh.Y, lh.X + 500f, lh.Y);
        Check("拖左竖线：最窄不小于一个字",
              oldBox.TextBox != null && oldBox.TextBox.Width >= oldBox.TextBox.MinWidth - 1f,
              $"宽={oldBox.TextBox?.Width:F0} 最小={oldBox.TextBox?.MinWidth:F0}");
        Shot("18-resize-min");

        frame = SelectionHandles.FrameOf(Doc.Selected);
        var br = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, UiUnit);
        float font0 = oldBox.TextBox?.FontSize ?? 0f;
        DragCanvasPoint(br.X, br.Y, br.X + 70f, br.Y + 70f);
        Check("拖四角：字号变大、宽跟着变",
              oldBox.TextBox != null && oldBox.TextBox.FontSize > font0 + 0.2f,
              $"字号 {font0:F1} → {oldBox.TextBox?.FontSize:F1}");
        Shot("19-corner-font");

        frame = SelectionHandles.FrameOf(Doc.Selected);
        var bh = SelectionHandles.CanvasPosition(SelHandle.Bottom, frame, UiUnit);
        var bhHit = SelectionHandles.HitTest(bh.X, bh.Y, Doc.Selected, frame, UiUnit);
        Check("普通文本框没有下边手柄", bhHit != SelHandle.Bottom, $"hit={bhHit}");

        // ---- 8. 清空删除 + 撤销 ----
        int beforeEmpty = TextBoxCountForTest;
        BeginTextBoxEdit(oldBox);
        SetTextBoxEditTextForTest("   ");
        EndTextBoxEditFromUi(true);
        SettleFrames(160);
        Check("老框清空：整框删除", TextBoxCountForTest == beforeEmpty - 1,
              $"{beforeEmpty} → {TextBoxCountForTest}");
        Host.Commands.Undo();
        SettleFrames(160);
        Check("撤销清空：原文回来",
              TextBoxCountForTest == beforeEmpty && oldBox.TextBox?.Text?.Trim().Length > 0,
              $"框={TextBoxCountForTest} 字={oldBox.TextBox?.Text?.Length ?? 0}");

        // ---- 9. 垃圾桶删除 + 撤销 ----
        // 上面的改宽/角缩放把 mcx/mcy 变成旧坐标了；这里直接按对象选中，
        // 不靠旧坐标点选（模拟自检要验证的是删除/撤销，不是再测一遍点选）。
        if (TextBoxEditing) EndTextBoxEditFromUi(true);
        Doc.SelectOnly(new[] { oldBox });
        SettleFrames(120);
        bar = SelectionBarCanvasRect;
        var del = SelectionHandles.BarButtonRectIn(bar, 6, UiUnit);
        int beforeTrash = TextBoxCountForTest;
        ClickCanvasPoint((del.MinX + del.MaxX) * 0.5f, (del.MinY + del.MaxY) * 0.5f);
        Check("操作条垃圾桶：删除选中文字", TextBoxCountForTest == beforeTrash - 1,
              $"{beforeTrash} → {TextBoxCountForTest}");
        Host.Commands.Undo();
        SettleFrames(160);
        Check("撤销垃圾桶：文字回来", TextBoxCountForTest == beforeTrash,
              $"框={TextBoxCountForTest}");

        // ---- 10. 橡皮：面积擦不碰文字 / 整笔擦删框 ----
        var eb = oldBox.PaddedBounds;
        float ecx = (eb.MinX + eb.MaxX) * 0.5f, ecy = (eb.MinY + eb.MaxY) * 0.5f;
        int beforeArea = TextBoxCountForTest;
        int areaHit = Doc.EraseRectAt(ecx, ecy, 24f, 24f);
        Check("面积擦：不碰文字", TextBoxCountForTest == beforeArea && areaHit == 0,
              $"框={TextBoxCountForTest} 命中={areaHit}");
        int beforeWhole = TextBoxCountForTest;
        Doc.EraseAt(ecx, ecy, EraserRadius);
        Check("整笔擦：点中文字 = 删整框", TextBoxCountForTest == beforeWhole - 1,
              $"{beforeWhole} → {TextBoxCountForTest}");
        Host.Commands.Undo();
        SettleFrames(160);
        Check("撤销整笔擦：文字回来", TextBoxCountForTest == beforeWhole,
              $"框={TextBoxCountForTest}");
        Shot("20-eraser-undo");

        // ---- 11. 滚动时操作条跟着对象走，选中不丢 ----
        ClickCanvasPoint(mcx, mcy);
        var barCanvas = SelectionBarCanvasRect;
        float savedOffset = ViewOffsetY;
        ViewOffsetY += 320f;
        SettleFrames(80);
        var barAfterScroll = SelectionBarCanvasRect;
        Check("滚动：操作条画布位置不重算、选中不丢",
              Doc.Selected.Count == 1
              && MathF.Abs(barAfterScroll.MinY - barCanvas.MinY) < 0.5f,
              $"条 y {barCanvas.MinY:F0}→{barAfterScroll.MinY:F0} 选中={Doc.Selected.Count}");
        Shot("21-scroll");
        ViewOffsetY = savedOffset;
        SettleFrames(80);

        // ---- 12. 存读：文字 / AutoWidth / 宽 ----
        var bytes = InkSerializer.SaveStrokes(new[] { oldBox });
        var loaded = InkSerializer.LoadStrokes(bytes, out _);
        var lb = loaded.Count > 0 ? loaded[0].TextBox : null;
        Check("存读：文字、AutoWidth、宽度都保持",
              lb != null && lb.Text == oldBox.TextBox?.Text
              && lb.AutoWidth == oldBox.TextBox?.AutoWidth
              && MathF.Abs(lb.Width - oldBox.TextBox.Width) < 0.5f,
              $"字={lb?.Text?.Length ?? 0} autoW={lb?.AutoWidth} 宽={lb?.Width:F0}");

        // ---- 13. 编辑底截图（当前页；人工看白板/文档对比）----
        BeginTextBoxEdit(oldBox);
        SettleFrames(200);
        Shot("22-edit-background");
        EndTextBoxEditFromUi(true);

        Console.WriteLine();
        Console.WriteLine($"文本框仿真自检：通过 {pass} 项，失败 {fail} 项");
        Console.WriteLine($"截图目录：{System.IO.Path.GetFullPath(dir)}");
        ExitCode = fail == 0 ? 0 : 1;
    }
}
