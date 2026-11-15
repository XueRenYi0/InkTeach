// [职责] 自检模式注册表：Ink 域（ADR-005 D2）。新增模式只改本文件。
// [测试] --list-modes / --unittest

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
    private ModeEntry[] BuildModeTableInk() => new ModeEntry[]
    {
        new("--b0textbox", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            Doc.Clear();
            Doc.ClearHistory();
            SetBoardFromUi(true);
            SetBoardColorFromUi(new Vortice.Mathematics.Color4(0.10f, 0.18f, 0.22f, 1f));
            // 在文本框下面铺一条亮绿粗线：编辑背景真透明时，线应从文字后面透出来。
            var band = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Vortice.Mathematics.Color4(0.20f, 0.85f, 0.55f, 1f),
                Width = 44f,
            };
            float midY = _virtualY + _virtualH * 0.5f;
            for (int i = 0; i <= 20; i++)
                band.AddPoint(_virtualX + _virtualW * 0.30f + i * (_virtualW * 0.40f / 20f),
                              midY, 0.5f, i * 8);
            Doc.AddStroke(band);
            Doc.InvalidateAll();
            SettleFrames(180);

            InsertPlainTextBoxForTest("");
            var tr = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (tr?.TextBox != null)
            {
                tr.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                tr.TextBox.Ink = tr.Color;
                tr.TextBox.Paper = false;
                BeginTextBoxEdit(tr);
                Console.WriteLine("B0 IME context=" + TextBoxEditHasImmContextForTest);
                SetTextBoxEditTextForTest("透明文本框：白字压在深色板上\n第二行 ABC 123\n第三行：光标、选区、IME 都由 RichEdit 负责。");
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/b0-textbox-dark.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                SetTextBoxEditSelectionForTest(0, 6);
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-selection.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) TypeTextBoxEditCharForTest('字');
                sw.Stop();
                Console.WriteLine($"B0 输入 100 字耗时：{sw.ElapsedMilliseconds} ms");
                EndTextBoxEditFromUi(true);
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            }

            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("纸面文本框：Paper=true\n第二行：圆角、底色和画布一致");
            var paper = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (paper?.TextBox != null)
            {
                paper.TextBox.Paper = true;
                paper.TextBox.Fill = TextBoxBackground.Fill(1, BoardColor, card: false);
                paper.TextBox.Border = TextBoxBackground.Border(1, BoardColor, card: false);
                paper.Color = new Vortice.Mathematics.Color4(0.1f, 0.1f, 0.12f, 1f);
                paper.TextBox.Ink = paper.Color;
                BeginTextBoxEdit(paper);
                SetTextBoxEditTextForTest("纸面文本框：Paper=true\n第二行：圆角、底色和画布一致");
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/b0-textbox-paper.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
            }

            // 真实逐字输入（不是 SetText）：验证光标位置、字号和字是否对得上。
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("");
            var typed = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (typed?.TextBox != null)
            {
                typed.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                typed.TextBox.Ink = typed.Color;
                typed.TextBox.Paper = false;
                typed.TextBox.AutoWidth = false;
                typed.TextBox.Width = 600f;
                typed.TextBox.InvalidateLayout();
                BeginTextBoxEdit(typed);
                SettleFrames(220);          // 等快照/宿主显示
                TypeTextBoxEditCharForTest('你');
                TypeTextBoxEditCharForTest('怎');
                TypeTextBoxEditCharForTest('么');
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/b0-textbox-realtype.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                Doc.SelectOnly(Array.Empty<Stroke>());
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-realtype-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                // 小字号（18px）：验证顶部留白按字号缩放，提交不跳。
                typed.TextBox.FontSize = 9f;
                typed.TextBox.AutoHeight = true;
                typed.TextBox.Width = 300f;
                typed.TextBox.InvalidateLayout();
                BeginTextBoxEdit(typed);
                SettleFrames(200);
                TypeTextBoxEditCharForTest('你');
                TypeTextBoxEditCharForTest('怎');
                SettleFrames(200);
                ScreenProbe.SaveBmp("reports/b0-textbox-smallfont.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                Doc.SelectOnly(Array.Empty<Stroke>());
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-smallfont-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                // 27px：第三点校准 RichEdit / DirectWrite 的首行留白差。
                typed.TextBox.FontSize = 13.5f;
                typed.TextBox.AutoHeight = true;
                typed.TextBox.Width = 300f;
                typed.TextBox.InvalidateLayout();
                BeginTextBoxEdit(typed);
                SettleFrames(200);
                TypeTextBoxEditCharForTest('你');
                SettleFrames(200);
                ScreenProbe.SaveBmp("reports/b0-textbox-midfont.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                Doc.SelectOnly(Array.Empty<Stroke>());
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/b0-textbox-midfont-committed.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            }

            // 点击文字中间：caret 应落在点击处附近，而不是偏一截。
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("你怎么又来了 ABC");
            var clickBox = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (clickBox?.TextBox != null)
            {
                clickBox.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                clickBox.TextBox.Ink = clickBox.Color;
                clickBox.TextBox.Paper = false;
                clickBox.TextBox.AutoWidth = false;
                clickBox.TextBox.Width = 600f;
                BeginTextBoxEdit(clickBox);
                SettleFrames(220);
                var cb = clickBox.PaddedBounds;
                float cpad = 18f * MathF.Max(0.5f, clickBox.TextBox.Unit);
                int clickX = (int)MathF.Round(cb.MinX + 2f + cpad + 90f);
                int clickY = (int)MathF.Round(cb.MinY + 2f + ViewOffsetY + cpad + 24f);
                SendMouse(clickX, clickY, 0);
                SendMouse(clickX, clickY, Native.MOUSEEVENTF_LEFTDOWN);
                SendMouse(clickX, clickY, Native.MOUSEEVENTF_LEFTUP);
                SettleFrames(180);
                var cp = TextBoxEditCaretPointForTest;
                Console.WriteLine($"B0 点击 ({clickX},{clickY}) caret={TextBoxEditCaretForTest} point={cp.X},{cp.Y} host={clickX - cp.X},{clickY - cp.Y}");
                ScreenProbe.SaveBmp("reports/b0-textbox-click.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
            }

            // 已有文字的重新编辑：快照里不能残留旧字（否则会出现双份/重影）。
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("旧文字：重新编辑不应有残影\n第二行 ABC");
            var again = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (again?.TextBox != null)
            {
                again.Color = new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f);
                again.TextBox.Ink = again.Color;
                again.TextBox.Paper = false;
                BeginTextBoxEdit(again);
                SetTextBoxEditTextForTest("旧文字：重新编辑不应有残影\n第二行 ABC\n第三行：快照里不能有旧字");
                SettleFrames(240);
                ScreenProbe.SaveBmp("reports/b0-textbox-reedit.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
            }
            Console.WriteLine("B0 出图：reports/b0-textbox-*.bmp");
            _quit = true;
            return null;
        }),
        new("--glyphfill", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphFillPng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                         args.Length > 2 && args[2].Length > 0 ? args[2][0] : '函',
                         args.Length > 3 ? args[3] : "reports/glyphfill.png");
            _quit = true;
            return null;
        }),
        new("--glyphink", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphInkPng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                        args.Length > 2 ? args[2] : "函数向量",
                        args.Length > 3 ? args[3] : "reports/glyphink.png",
                        args.Length > 4 && float.TryParse(args[4], out float gh) ? gh : 96f);
            _quit = true;
            return null;
        }),
        new("--glyphline", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphLinePng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                         args.Length > 2 ? args[2] : "同学们，上课！",
                         args.Length > 3 ? args[3] : "reports/glyphline.png",
                         args.Length > 4 && float.TryParse(args[4], out float lh) ? lh : 72f,
                         args.Length > 5 && float.TryParse(args[5], out float lw) ? lw : 1200f);
            _quit = true;
            return null;
        }),
        new("--glyphobj", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphObjPng(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                        args.Length > 2 ? args[2] : "函数向量",
                        args.Length > 3 ? args[3] : "reports/glyphobj.png",
                        args.Length > 4 && float.TryParse(args[4], out float gs) ? gs : 120f);
            _quit = true;
            return null;
        }),
        new("--glyphpath", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            GlyphPathDump(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                          args.Length > 2 ? args[2] : "函数向量");
            _quit = true;
            return null;
        }),
        new("--glyphsave", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphSaveTest(args.Length > 1 ? args[1] : @"C:\Windows\Fonts\simkai.ttf",
                          args.Length > 2 ? args[2] : "函数向量",
                          args.Length > 3 ? args[3] : "reports/glyphsave.png",
                          args.Length > 4 && float.TryParse(args[4], out float gs2) ? gs2 : 120f);
            _quit = true;
            return null;
        }),
        new("--handbbox", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandBboxPng(args.Length > 1 ? args[1] : "",
                        args.Length > 2 ? args[2] : "",
                        args.Length > 3 ? args[3] : "reports/handbbox.png",
                        !args.Contains("--safe"));
            _quit = true;
            return null;
        }),
        new("--handblocks", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HandBlocksDump(args.Length > 1 ? args[1] : "");
            _quit = true;
            return null;
        }),
        new("--handletest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HandleTest();
            return null;
        }),
        new("--handreplace", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandReplacePng(args.Length > 1 ? args[1] : "",
                           args.Length > 2 ? args[2] : "",
                           args.Length > 3 ? args[3] : "reports/handreplace.png",
                           args.Length > 4 ? args[4] : @"C:\Windows\Fonts\simkai.ttf",
                           args.Length > 5 && float.TryParse(args[5], out float tl) ? tl : 0f);
            _quit = true;
            return null;
        }),
        new("--handtextbox", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandTextBoxPng(args.Length > 1 ? args[1] : "",
                           args.Length > 2 ? args[2] : "reports/handtextbox.png",
                           args.Length > 3 && float.TryParse(args[3], out float htl) ? htl : 480f);
            _quit = true;
            return null;
        }),
        new("--hotkeytest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            HotkeyTest();
            return null;
        }),
        new("--inkbeautify", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            InkBeautify(args.Length > 1 ? args[1] : "",
                        args.Length > 2 ? args[2] : "reports/board-beautified.png",
                        args.Length > 3 && int.TryParse(args[3], out int n) ? n : 2);
            _quit = true;
            return null;
        }),
        new("--inkboardpng", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            InkBoardPng(args.Length > 1 ? args[1] : "",
                        args.Length > 2 ? args[2] : "reports/board.png");
            _quit = true;
            return null;
        }),
        new("--inkfile", "", args =>
        {
            // **读用户手画的样本**（`--recink` 录出来的那份）：见 `RecoProbe.RunInkFile`。
            // 纯离线：不建窗口、不碰界面。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = RecoProbe.RunInkFile(args.Length > 1 ? args[1] : "reports/我的双曲线.txt");
            _quit = true;
            return null;
        }),
        new("--inkfiletest", "墨迹文件自检（保存/打开往返 / 打开前备份与轮转 / 坏文件不动文档 / 放映中置灰）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InkFileTest();
            return null;
        }),
        new("--inktest", "**墨迹识别的单独测试**（日常改识别只跑这一条：准确率 / 误报 / 难例 / 写回）", args =>
        {
            // **纯几何，不建窗口、不碰界面**：识别器是离线慢路径，
            // 它该验的是"判据对不对"，不该混进界面/输入那套。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = RecoProbe.Run();
            _quit = true;
            return null;
        }),
        new("--keytest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            KeyTest();
            return null;
        }),
        new("--newtext", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            NewTextModelTest();
            _quit = true;
            return null;
        }),
        new("--tapdotprobe", "", args =>
        {
            // [2026-10-08] "起笔冒圆"复现器（合成笔）：见 TapDotProbe。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TapDotProbe();
            return null;
        }),
        new("--textbg", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TextBackgroundTest();
            _quit = true;
            return null;
        }),
        new("--textboxedit", "文本框编辑截图（reports/textbox-edit.bmp）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("文本框编辑对齐检查\n第二行：中文混排，没有公式。");
            var st = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (st != null)
            {
                BeginTextBoxEdit(st);
                SetTextBoxEditTextForTest("文本框编辑对齐检查\n第二行：中文混排，没有公式。\n第三行：看看光标和文字是否对齐。");
                SettleFrames(300);
                ScreenProbe.SaveBmp("reports/textbox-edit.bmp", (int)_virtualX, (int)_virtualY,
                                    (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(false);
            }
            Console.WriteLine("出图：reports/textbox-edit.bmp");

            // 文本工具专项：在屏幕右侧贴近边缘处逐字输入，框必须向左长/换行，
            // 不能把前面的汉字挤出可视区；然后点画布空白，应当同一下出新输入框。
            Doc.Clear();
            Doc.ClearHistory();
            SetToolFromUi(Tool.Text);
            float edgeX = _virtualX + _virtualW - 40f * DpiScale;
            float edgeY = _virtualY + _virtualH * 0.35f;
            if (TextToolPointerDown(edgeX, edgeY))
            {
                // 逐字追加，模拟真实键盘：每次插入都会触发"边打边长"和 RichEdit 重排。
                for (int n = 1; n <= 12; n++)
                {
                    TypeTextBoxEditCharForTest('你');
                    SettleFrames(160);
                    var s = Doc.Selected.Count > 0 ? Doc.Selected[0] : null;
                    var b = s?.PaddedBounds ?? default;
                    Console.WriteLine($"右侧输入 {n} 字：x={b.MinX:F0}..{b.MaxX:F0} "
                                    + $"宽={s?.TextBox?.Width:F0} 屏右={_virtualX + _virtualW:F0}");
                    if (n is 2 or 6 or 12)
                        ScreenProbe.SaveBmp($"reports/textbox-edit-right-{n}.bmp",
                            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                }
                int beforeClick = TextBoxCountForTest;
                var old = Doc.Selected.Count > 0 ? Doc.Selected[0] : null;
                int cxp = (int)(_virtualX + 40f), cyp = (int)(_virtualY + 40f);
                SendMouse(cxp, cyp, 0); SettleFrames(60);
                SendMouse(cxp, cyp, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(80);
                SendMouse(cxp, cyp, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
                Console.WriteLine($"点空白：editing={TextBoxEditing} 文本框 {beforeClick}"
                                + $"→{TextBoxCountForTest} 旧框仍选中="
                                + (old != null && Doc.Selected.Contains(old)));
                ScreenProbe.SaveBmp("reports/textbox-edit-after-click.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                EndTextBoxEditFromUi(true);
                SettleFrames(160);

                // 旧框：未选中时悬停应是 I 字；单击选中后应是移动十字；按住拖要真的能移动。
                if (old?.TextBox is { Role: TextBoxRole.Normal } && Doc.Strokes.Contains(old))
                {
                    var ob = old.PaddedBounds;
                    int ocx = (int)((ob.MinX + ob.MaxX) * 0.5f);
                    int ocy = (int)((ob.MinY + ob.MaxY) * 0.5f + ViewOffsetY);
                    SendMouse(ocx, ocy, 0); SettleFrames(140);
                    var hoverCursor = ComputeCursorKind();
                    SendMouse(ocx, ocy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(70);
                    SendMouse(ocx, ocy, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
                    bool selected = Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], old);
                    SendMouse(ocx, ocy, 0); SettleFrames(140);
                    var selCursor = ComputeCursorKind();
                    var p0 = old.Points.Count > 0 ? old.Points[0] : default;
                    float beforeX = p0.X, beforeY = p0.Y;
                    SendMouse(ocx, ocy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(70);
                    SendMouse(ocx - 140, ocy + 90, 0); SettleFrames(160);
                    SendMouse(ocx - 140, ocy + 90, Native.MOUSEEVENTF_LEFTUP); SettleFrames(220);
                    var p1 = old.Points.Count > 0 ? old.Points[0] : default;
                    Console.WriteLine($"旧框悬停：光标={hoverCursor} 选中={selected} "
                                    + $"选中后光标={selCursor} 移动 Δ=({p1.X - beforeX:F0},{p1.Y - beforeY:F0})");
                    ScreenProbe.SaveBmp("reports/textbox-edit-moved.bmp",
                        (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                    // 双击进入编辑：真双击（松手才兑现）；然后继续打字，宽度仍应自动长。
                    if (selected)
                    {
                        var ob2 = old.PaddedBounds;
                        int ox2 = (int)((ob2.MinX + ob2.MaxX) * 0.5f);
                        int oy2 = (int)((ob2.MinY + ob2.MaxY) * 0.5f + ViewOffsetY);
                        for (int k = 0; k < 2; k++)
                        {
                            SendMouse(ox2, oy2, 0); SettleFrames(60);
                            SendMouse(ox2, oy2, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(70);
                            SendMouse(ox2, oy2, Native.MOUSEEVENTF_LEFTUP); SettleFrames(160);
                        }
                        bool dblOk = TextBoxEditing;
                        if (!dblOk) BeginTextBoxEdit(old);   // 兜底，保证后面的宽高检查仍能跑
                        float wBefore = old.TextBox?.Width ?? 0f;
                        TypeTextBoxEditCharForTest('你');
                        TypeTextBoxEditCharForTest('好');
                        SettleFrames(220);
                        float wAfter = old.TextBox?.Width ?? 0f;
                        Console.WriteLine($"双击={dblOk} 重新编辑追加 2 字：宽 {wBefore:F0}→{wAfter:F0} "
                                        + $"autoW={old.TextBox?.AutoWidth}");
                        ScreenProbe.SaveBmp("reports/textbox-edit-reedit.bmp",
                            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                        EndTextBoxEditFromUi(true);

                        // 文本专项：存读一圈，确认 v27 的 AutoWidth 字节和文字都没丢。
                        var bytes = InkSerializer.SaveStrokes(new[] { old });
                        var loaded = InkSerializer.LoadStrokes(bytes, out _);
                        var lb = loaded.Count > 0 ? loaded[0].TextBox : null;
                        Console.WriteLine($"存读：文本={lb?.Text?.Length ?? 0} 字 "
                                        + $"autoW={lb?.AutoWidth} 宽={lb?.Width:F0}");
                    }

                    // 靠近底部主状态栏的文本框：确认选中后从上半部分也能往上拖。
                    Doc.Clear();
                    Doc.ClearHistory();
                    SetToolFromUi(Tool.Text);
                    float bottomX = _virtualX + _virtualW * 0.45f;
                    float bottomY = _virtualY + _virtualH - 210f * DpiScale;
                    if (TextToolPointerDown(bottomX, bottomY))
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            AppendTextBoxEditTextForTest("底部文字测试\n");
                        }
                        SettleFrames(200);
                        EndTextBoxEditFromUi(true);
                        if (Doc.Strokes.Count > 0)
                        {
                            var bottom = Doc.Strokes[^1];
                            Doc.SelectOnly(new[] { bottom });
                            var bb = bottom.PaddedBounds;
                            int bcx = (int)((bb.MinX + bb.MaxX) * 0.5f);
                            int bcy = (int)(bb.MinY + 24f + ViewOffsetY);
                            var q0 = bottom.Points.Count > 0 ? bottom.Points[0] : default;
                            float qx = q0.X, qy = q0.Y;
                            SendMouse(bcx, bcy, 0); SettleFrames(80);
                            SendMouse(bcx, bcy, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(80);
                            SendMouse(bcx, bcy - 120, 0); SettleFrames(160);
                            SendMouse(bcx, bcy - 120, Native.MOUSEEVENTF_LEFTUP); SettleFrames(180);
                            var q1 = bottom.Points.Count > 0 ? bottom.Points[0] : default;
                            Console.WriteLine($"底部文本框：框={bb.MinY:F0}..{bb.MaxY:F0} "
                                            + $"拖动 Δ=({q1.X - qx:F0},{q1.Y - qy:F0})");
                            ScreenProbe.SaveBmp("reports/textbox-edit-bottom.bmp",
                                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                        }
                    }
                }
            }
            _quit = true;
            return null;
        }),
        new("--textboxsim", "文本框仿真自检（真实鼠标/键盘 + 截图 reports/textbox-sim/）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TextBoxSimTest();
            _quit = true;
            return null;
        }),
        new("--textcolor", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            TextColorShow(args.Length > 1 ? args[1] : "reports/textcolor.bmp");
            _quit = true;
            return null;
        }),
        new("--textinputaudit", "文字输入宿主自检（隐藏窗口；Begin/Commit/Cancel/校验/多行）", args =>
        {
            // A2：文字宿主 Begin/Commit/Cancel/校验/多行；隐藏窗口、不抢前台。
            return TextInputAudit.Run();
        }),
        new("--textstyle", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            TextStyleShow(args.Length > 1 ? args[1] : "reports/textstyle.bmp");
            _quit = true;
            return null;
        }),
        new("--wetinktest", "湿墨轨迹实测（只让系统画，数上屏像素：这条通道到底画不画）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 15;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--live" && double.TryParse(args[i + 1], out var s)) secs = s;
            if (args.Contains("--live")) WetInkLiveTest(secs);
            else WetInkTest(args.Contains("--activate"));
            return null;
        }),
    };
}
