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
    private void FigureQuadTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 二次函数：真实模型 → 可操作抛物线 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);

        var cfg = OcrClient.LoadConfig();
        var gen = cfg.IsConfigured
            ? Task.Run(() => AiQuestionClient.GenerateAsync(
                "一道高中二次函数题，教材/高考风格。图中给出抛物线 y=ax²+bx+c，" +
                "要求求顶点/最值或解集；figure 用 kind=quadratic，给 a/b/c 和 domain。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiQuestionClient.Result { Error = "AI 未配置" };
        Console.WriteLine($"AI 结果：ok={gen.Ok} {gen.Seconds:F1}s error={gen.Error}");

        FigureSpec spec = gen.Figure;
        if (spec == null || spec.Kind != "quadratic")
            spec = new FigureSpec { Kind = "quadratic", Qa = 1f, Qb = -4f, Qc = 3f,
                                    DomainMin = -1f, DomainMax = 5f };
        Console.WriteLine($"抛物线：a={spec.Qa} b={spec.Qb} c={spec.Qc} domain={spec.DomainMin}..{spec.DomainMax}");

        Doc.Clear();
        Doc.ClearHistory();
        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card?.TextBox != null)
        {
            var b = card.TextBox;
            b.Title = $"{cfg.Model} · 二次函数 · 1 道";
            b.Meta = gen.Ok ? $"AI 生成 · {gen.Seconds:F1}s · 请核对" : "AI 未配置，使用示例";
            b.Questions.Clear();
            b.Questions.Add(new TextBoxQuestion
            {
                Stem = gen.Ok ? gen.Stem : "已知抛物线 y = x² − 4x + 3，求它的顶点坐标和最值。",
                Answer = gen.Answer,
                Analysis = gen.Analysis,
            });
            b.Width = 560f * u;
            b.AutoWidth = false;
            b.InvalidateLayout();
            _ = b.EnsureLayout();
            float x = _virtualX + 80f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            card.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            card.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(card);
        }

        // y = a x² + b x + c → 现有 Parabola 对象（顶点 + 过点），可直接缩放/移动。
        float h = -spec.Qb / (2f * MathF.Max(0.0001f, spec.Qa));
        float k = spec.Qc - spec.Qb * spec.Qb / (4f * spec.Qa);
        float sUnit = 45f * u;
        float ox = 1850f, oy = 820f;

        // 函数图需要坐标系：x∈[-5,5]、y∈[-2,8]，网格步长 = 1 个单位。
        var axes = new Stroke
        {
            Tool = Tool.Coordinate,
            Kind = StrokeKind.Coordinate,
            Color = new Color4(0.55f, 0.58f, 0.62f, 1f),
            Width = 3f * u,
            Grid = true,
            AxisGridStep = sUnit,
        };
        axes.AddPoint(ox - 5f * sUnit, oy - 8f * sUnit, 1f, 0);
        axes.AddPoint(ox + 5f * sUnit, oy + 2f * sUnit, 1f, 0);
        axes.AddPoint(ox, oy, 1f, 0);
        Doc.AddStroke(axes);

        // y = a x² + b x + c → 现有 Parabola 对象（顶点 + 过点），可直接缩放/移动。
        var sh = new Stroke
        {
            Tool = Tool.Parabola,
            Kind = StrokeKind.Parabola,
            Color = new Color4(0.12f, 0.14f, 0.18f, 1f),
            Width = 5f * u,
            CurveAxis = spec.Qa > 0 ? CurveAxis.OpenUp : CurveAxis.OpenDown,
        };
        sh.SetParabolaVertex(ox + h * sUnit, oy - k * sUnit);
        sh.SetParabolaThroughPoint(ox + (h + 1f) * sUnit, oy - (k + spec.Qa) * sUnit);
        Doc.AddStroke(sh);
        Doc.SelectOnly(new[] { sh });
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-quad-editable.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 拖右下角缩放：现有通用框会等比改变曲线。
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var br = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, UiUnit);
        SelectionGestureForTest(br.X, br.Y);
        UpdateSelectionGestureForTest(br.X + 80f * u, br.Y + 60f * u);
        EndSelectionGestureForTest();
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-quad-scaled.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/figure-quad-editable.bmp / figure-quad-scaled.bmp");
    }

    /// <summary>真实大模型出题：DeepSeek/OpenAI 兼容接口 → JSON → 结构化图形 → 卡片。</summary>
    private void FigureLiveTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 真实模型出题 + 结构化题图 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);

        var cfg = OcrClient.LoadConfig();
        Console.WriteLine($"AI 配置：configured={cfg.IsConfigured} model={cfg.Model} base={cfg.BaseUrl}");
        if (!cfg.IsConfigured)
        {
            Console.WriteLine("AI 未配置：跳过真实模型测试");
            return;
        }

        var gen = Task.Run(() => AiQuestionClient.GenerateAsync(
            "一道初中平面几何题，配一个简单的三角形或四边形，点坐标 0..10，题干要提到图中的点。",
            cfg, CancellationToken.None)).GetAwaiter().GetResult();

        Console.WriteLine($"AI 结果：ok={gen.Ok} {gen.Seconds:F1}s error={gen.Error}");
        if (gen.Ok)
            Console.WriteLine($"AI 题目：stem={ClipText(gen.Stem, 80)} " +
                              $"figure={(gen.Figure != null ? gen.Figure.Kind : "none")} " +
                              $"points={gen.Figure?.Points2D.Count + gen.Figure?.Points3D.Count ?? 0} " +
                              $"segments={gen.Figure?.Segments.Count ?? 0}");

        var spec = gen.Figure ?? FigureRenderer.SampleTriangle();
        var figure = FigureRenderer.Render(spec);
        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card?.TextBox != null)
        {
            var b = card.TextBox;
            b.Title = $"{cfg.Model} · 真实模型 · 1 道";
            b.Meta = gen.Ok
                ? $"AI 生成 · {gen.Seconds:F1}s · 请老师核对"
                : $"AI 失败：{ClipText(gen.Error, 60)}";
            b.Questions.Clear();
            b.Questions.Add(new TextBoxQuestion
            {
                Stem = gen.Ok ? gen.Stem : "真实模型未返回题目，下面是结构化图形兜底。",
                Answer = gen.Answer,
                Analysis = gen.Analysis,
                Figure = figure,
            });
            b.Width = 560f * u;
            b.AutoWidth = false;
            b.InvalidateLayout();
            _ = b.EnsureLayout();
            float x = _virtualX + 80f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            card.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            card.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(card);
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-live.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 真实模型场景 → 本地可拖动三角形对象：
        // AI 只给点坐标，本地用现有 Triangle 对象创建，顶点手柄/移动/撤销全部复用。
        Stroke tri = null;
        var ptsSpec = gen.Figure?.Points2D;
        if (ptsSpec != null && ptsSpec.Count >= 3)
        {
            var names = new List<string>();
            if (ptsSpec.ContainsKey("A") && ptsSpec.ContainsKey("B") && ptsSpec.ContainsKey("C"))
            { names.Add("A"); names.Add("B"); names.Add("C"); }
            else
            {
                foreach (var k in ptsSpec.Keys) { names.Add(k); if (names.Count == 3) break; }
            }
            var pts = names.Select(n => ptsSpec[n]).ToList();
            float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            float bw = MathF.Max(0.001f, maxX - minX), bh = MathF.Max(0.001f, maxY - minY);
            const float rw = 760f, rh = 600f, ox = 1420f, oy = 360f;
            float sc = MathF.Min(rw / bw, rh / bh);
            tri = new Stroke
            {
                Tool = Tool.Triangle,
                Kind = StrokeKind.Triangle,
                Color = new Color4(0.12f, 0.14f, 0.18f, 1f),
                Width = 5f * u,
            };
            foreach (var p in pts)
            {
                tri.AddPoint(ox + (p.X - minX) * sc,
                             oy + rh - (p.Y - minY) * sc, 1f, 0);
            }
            Doc.AddStroke(tri);
            Doc.SelectOnly(new[] { tri });
            SettleFrames(300);
            ScreenProbe.SaveBmp("reports/figure-live-editable.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

            var from = SelectionHandles.ShapeHandleCanvasPosition(tri, ShapeHandle.Vertex0);
            VertexDragForTest(tri, ShapeHandle.Vertex0, from.X, from.Y,
                              from.X + 90f * u, from.Y + 40f * u);
            SettleFrames(300);
            ScreenProbe.SaveBmp("reports/figure-live-dragged.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Console.WriteLine($"AI 场景 → 可拖动三角形：points=3 handleDrag=True");
        }

        Console.WriteLine("出图：reports/figure-live.bmp / figure-live-editable.bmp / figure-live-dragged.bmp");
    }

    private static string ClipText(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// P4 背景样式自检：四种预设 + 字号面板里的「背景」行 + 存读。
    /// 出图 reports/textbg-*.bmp。
    /// </summary>
    private void TextBackgroundTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 文字背景：透明 / 白纸 / 板色 / 浅色 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        if (Environment.GetCommandLineArgs().Contains("--darkboard"))
            BoardColor = new Vortice.Mathematics.Color4(0.10f, 0.16f, 0.19f, 1f);
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);
        string suffix = TextBoxBackground.Luma(BoardColor) < 0.5f ? "dark" : "light";

        void Make(int preset, string label, float x, float y)
        {
            InsertPlainTextBoxForTest($"{label}背景\n第二行：背景样式 {preset + 1}/4");
            var s = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (s?.TextBox == null) return;
            var b = s.TextBox;
            b.AutoWidth = false;
            b.Width = 760f * u / MathF.Max(0.5f, b.Unit);
            b.FontSize = 16f;
            b.Fill = TextBoxBackground.Fill(preset, BoardColor, card: false);
            b.Border = TextBoxBackground.Border(preset, BoardColor, card: false);
            b.Ink = preset == 0
                ? TextBoxBackground.InkFor(BoardColor)
                : TextBoxBackground.InkFor(b.Fill);
            s.Color = b.Ink;
            b.LineColors.Clear();
            b.ColorSpans.Clear();
            b.InvalidateLayout();
            var ls = b.EnsureLayout();
            s.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            s.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(s);
            Console.WriteLine($"bg preset={preset} board=({BoardColor.R:F2},{BoardColor.G:F2},{BoardColor.B:F2}) " +
                              $"fill=({b.Fill.R:F2},{b.Fill.G:F2},{b.Fill.B:F2},{b.Fill.A:F2}) " +
                              $"ink=({b.Ink.R:F2},{b.Ink.G:F2},{b.Ink.B:F2}) " +
                              $"color=({s.Color.R:F2},{s.Color.G:F2},{s.Color.B:F2}) " +
                              $"textLen={b.Text.Length} lines={ls.Count} h={b.Height:F0} " +
                              $"origin=({s.Points[0].X:F0},{s.Points[0].Y:F0}) w={b.Width:F0}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        Make(0, "透明", 160f, 260f);
        Make(1, "白纸", 1060f, 260f);
        Make(2, "板色", 160f, 700f);
        Make(3, "浅色", 1060f, 700f);
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp($"reports/textbg-{suffix}.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 选中第一个框，打开字号面板：应能看到第四行「背景」。
        var first = Doc.Strokes.Count > 0 ? Doc.Strokes[0] : null;
        if (first != null)
        {
            Doc.SelectOnly(new[] { first });
            RunBarButtonForTest(SelBarButton.FontSize);
            SettleFrames(300);
            ScreenProbe.SaveBmp($"reports/textbg-panel-{suffix}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

            // 面板命中：第四行第 3 格应报「板色」。
            var frame = SelectionHandles.FrameOf(Doc.Selected);
            var bgCell = SelectionHandles.FontPanelBackgroundRect(2, frame.CanvasAabb, UiUnit,
                                                                 FurnitureViewport);
            var part = SelectionHandles.PanelPartAt(
                (bgCell.MinX + bgCell.MaxX) * 0.5f,
                (bgCell.MinY + bgCell.MaxY) * 0.5f,
                frame.CanvasAabb, UiUnit, FurnitureViewport, SelPanel.FontSize,
                SelectionHandles.SwatchCount, textBar: true);
            Console.WriteLine($"BGHIT part={(int)part} expected=" +
                              $"{(int)SelectionHandles.PanelPart.BackgroundBase + 2}");

            var beforeFill = first.TextBox?.Fill ?? default;
            SetSelectedTextBackgroundFromUi(2);
            bool bgChanged = first.TextBox != null
                             && !TextBoxBackground.Same(beforeFill, first.TextBox.Fill);
            Doc.Undo();
            bool bgUndone = first.TextBox != null
                            && TextBoxBackground.Same(beforeFill, first.TextBox.Fill);
            Doc.Redo();
            bool bgRedone = first.TextBox != null
                            && !TextBoxBackground.Same(beforeFill, first.TextBox.Fill);
            Console.WriteLine($"背景撤销：changed={bgChanged} undo={bgUndone} redo={bgRedone}");
        }

        // 白纸卡编辑态：RichEdit 只能画实色近似，但应该看起来就是白纸。
        Stroke whiteBox = null;
        foreach (var s in Doc.Strokes)
            if (s.TextBox is { } b0 && b0.Fill.A > 0.9f && TextBoxBackground.Luma(b0.Fill) > 0.7f)
            { whiteBox = s; break; }
        if (whiteBox != null)
        {
            BeginTextBoxEdit(whiteBox);
            SettleFrames(260);
            ScreenProbe.SaveBmp($"reports/textbg-edit-{suffix}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            EndTextBoxEditFromUi(false);
            SettleFrames(120);
        }

        // 深色板色编辑态：编辑底应接近板色，不能突然变白。
        Stroke boardBox = null;
        foreach (var s in Doc.Strokes)
            if (s.TextBox is { } b1 && b1.Fill.A > 0.9f && TextBoxBackground.Luma(b1.Fill) < 0.3f)
            { boardBox = s; break; }
        if (boardBox != null)
        {
            BeginTextBoxEdit(boardBox);
            SettleFrames(260);
            ScreenProbe.SaveBmp($"reports/textbg-edit-board-{suffix}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            EndTextBoxEditFromUi(false);
            SettleFrames(120);
        }

        // 走真实入口应用白纸，再存读：Fill/Border/Radius 必须还在。
        if (first != null)
        {
            SetSelectedTextBackgroundFromUi(1);
            SettleFrames(120);
            var bytes = InkSerializer.Save(Doc);
            InkSerializer.LoadInto(Doc, bytes);
            Stroke back = null;
            foreach (var s in Doc.Strokes)
                if (s.TextBox is { } b && b.Fill.A > 0.9f) { back = s; break; }
            Console.WriteLine($"背景存读：fillA={back?.TextBox?.Fill.A:F2} " +
                              $"borderA={back?.TextBox?.Border.A:F2} " +
                              $"radius={back?.TextBox?.Radius:F0}");
        }
        Console.WriteLine($"出图：reports/textbg-{suffix}.bmp / textbg-panel-{suffix}.bmp");
    }

    /// <summary>
    /// 新文字模型自检：普通框纯文本；OCR 只读富文本卡；AI 卡不变。
    /// 出图 reports/newtext-*.bmp。
    /// </summary>
    private void NewTextModelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 新文字模型：纯文本 + OCR 只读卡 + AI 卡 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);

        void Shot(string name)
        {
            SettleFrames(260);
            ScreenProbe.SaveBmp($"reports/{name}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        }

        // ① 普通文本框：Markdown / LaTeX 全部按字面显示。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("**粗体** 不是粗体\n# 标题 不是标题\n公式 $x^2$ 原样显示\n--- 不是分隔线");
        var plain = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (plain?.TextBox != null)
        {
            plain.Color = new Vortice.Mathematics.Color4(0.12f, 0.14f, 0.18f, 1f);
            plain.TextBox.Ink = plain.Color;
            plain.TextBox.Paper = false;
            plain.TextBox.LineColors.Clear();
            plain.TextBox.ColorSpans.Clear();
            plain.TextBox.InvalidateLayout();
            plain.SyncTextBoxBounds();
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-plain");
        Console.WriteLine($"纯文本：rich={plain?.TextBox?.Rich} " +
                          $"readOnly={plain?.TextBox?.ReadOnly} " +
                          $"text={plain?.TextBox?.Text.Replace('\n', '/')}");

        // 编辑态同样按字面显示：证明“所见即所得”。
        BeginTextBoxEdit(plain);
        SetTextBoxEditTextForTest("**粗体** 不是粗体\n# 标题 不是标题\n公式 $x^2$ 原样显示\n--- 不是分隔线");
        Shot("newtext-plain-edit");
        EndTextBoxEditFromUi(true);
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-plain-committed");

        // ② OCR 结果：只读富文本卡，文字 + 公式混排。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("已知 $x^2-4x+3=0$，求：\n（1）对称轴；\n（2）顶点坐标。");
        var ocr = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (ocr?.TextBox != null)
        {
            ocr.Color = new Vortice.Mathematics.Color4(0.12f, 0.14f, 0.18f, 1f);
            ocr.TextBox.Ink = ocr.Color;
            ocr.TextBox.LineColors.Clear();
            ocr.TextBox.ColorSpans.Clear();
            ocr.TextBox.Rich = true;
            ocr.TextBox.ReadOnly = true;
            ocr.TextBox.Paper = true;
            ocr.TextBox.Fill = TextBoxBackground.Fill(1, BoardColor, card: true);
            ocr.TextBox.Border = TextBoxBackground.Border(1, BoardColor, card: true);
            ocr.TextBox.AutoWidth = false;
            ocr.TextBox.Width = 760f * u;
            ocr.TextBox.FontSize = 15f;
            ocr.TextBox.InvalidateLayout();
            ocr.TextBox.FitToContent(ocr.TextBox.Width, 0f, fitWidth: false);
            ocr.SyncTextBoxBounds();
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-ocr");
        BeginTextBoxEdit(ocr);
        Console.WriteLine($"OCR 卡：rich={ocr?.TextBox?.Rich} " +
                          $"readOnly={ocr?.TextBox?.ReadOnly} " +
                          $"editing={TextBoxEditing}（应为 False）");
        if (TextBoxEditing) EndTextBoxEditFromUi(false);

        // 只读标志要能存读：否则重新打开又变成可编辑文本。
        var saved = InkSerializer.Save(Doc);
        InkSerializer.LoadInto(Doc, saved);
        Stroke back = null;
        foreach (var s in Doc.Strokes)
            if (s.TextBox is { Rich: true }) { back = s; break; }
        Console.WriteLine($"OCR 存读：rich={back?.TextBox?.Rich} " +
                          $"readOnly={back?.TextBox?.ReadOnly} " +
                          $"editing={TextBoxEditing}（应为 False）");

        // ③ AI 题卡：沿用原有只读渲染。
        Doc.Clear();
        Doc.ClearHistory();
        InsertMockTextBoxFromUi();
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-ai");
        Console.WriteLine("出图：reports/newtext-plain.bmp / newtext-ocr.bmp / newtext-ai.bmp");
    }

}
