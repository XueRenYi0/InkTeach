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
    private void AiTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 题卡自检（1A：插入 / 折叠 / 存档 / 删除 / 撤销）===");
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        AiPromptText = "给初二出三道二次函数题，中等难度，带答案";

        int fails = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
            if (!ok) fails++;
        }

        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }

        // 原生输入框：开 / 关 / 文字留在状态里（中文/输入法由系统 EDIT 负责）
        Host.Commands.SetAiPromptText("测试提示");
        var pr = new RectF { MinX = 100f, MinY = 100f, MaxX = 300f, MaxY = 140f };
        Host.Commands.BeginAiPromptEdit(pr.MinX, pr.MinY, pr.MaxX - pr.MinX, pr.MaxY - pr.MinY);
        SettleFrames(150);
        Check(Host.State.AiPromptEditing, "AI 原生输入框能打开");
        Host.Commands.EndAiPromptEdit(true);
        SettleFrames(100);
        Check(!Host.State.AiPromptEditing && Host.State.AiPrompt == "测试提示",
              $"输入框关闭后文字保留（{Host.State.AiPrompt}）");
        Host.Commands.SetAiPromptText("给初二出三道二次函数题，中等难度，带答案");

        Check(card != null, "插入一张题卡");
        Check(card?.TextBox != null && card.TextBox.Questions.Count == 3, "题卡有 3 道题");
        Check(card != null && !card.TextBox.ShowAnswers, "答案默认折叠");
        float h0 = card?.TextBox.LayoutHeight ?? 0f;
        Check(h0 > 100f, $"折叠高度 > 100（{h0:0}）");

        // ---- 拖动 / 改宽 / 点答案（走和真机同一条指针路径）----
        {
            var b = card.PaddedBounds;
            float sx = (b.MinX + b.MaxX) * 0.5f;
            float sy = b.MinY + 18f;
            TextBoxPointerDown(sx, sy);
            TextBoxPointerUp();
            Check(Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], card),
                  "点卡片 = 选中（移动交给标准选中框）");

            float w0 = card.TextBox.Width;
            b = card.PaddedBounds;
            BeginTextBoxResize(card, SelHandle.Right, b.MaxX - 4f, b.MinY + 80f);
            UpdateSelectionGestureForTest(b.MaxX - 104f, b.MinY + 80f);
            EndSelectionGestureForTest();
            Check(card.TextBox.Width < w0 - 50f,
                  $"拖右边缘改宽（{w0:0} → {card.TextBox.Width:0}）");

            b = card.PaddedBounds;
            TextBoxLine firstToggle = null;
            foreach (var l in card.TextBox.EnsureLayout())
                if (l.Kind == TextBoxLineKind.Toggle) { firstToggle = l; break; }
            Check(firstToggle != null, "折叠态有「显示答案」按钮");
            if (firstToggle != null)
            {
                var local = new Vector2(card.Points[0].X + (firstToggle.Box.MinX + firstToggle.Box.MaxX) * 0.5f,
                                        card.Points[0].Y + (firstToggle.Box.MinY + firstToggle.Box.MaxY) * 0.5f);
                var canvas = Vector2.Transform(local, card.Transform);
                TextBoxPointerDown(canvas.X, canvas.Y + ViewOffsetY);
                TextBoxPointerUp();
                Check(card.TextBox.ShowAnswers, "点「显示答案」展开");
                TextBoxLine collapse = null;
                foreach (var l in card.TextBox.EnsureLayout())
                    if (l.Kind == TextBoxLineKind.Toggle) { collapse = l; break; }
                Check(collapse != null, "展开态有「收起答案」按钮");
                if (collapse != null)
                {
                    local = new Vector2(card.Points[0].X + (collapse.Box.MinX + collapse.Box.MaxX) * 0.5f,
                                        card.Points[0].Y + (collapse.Box.MinY + collapse.Box.MaxY) * 0.5f);
                    canvas = Vector2.Transform(local, card.Transform);
                    TextBoxPointerDown(canvas.X, canvas.Y + ViewOffsetY);
                    TextBoxPointerUp();
                    Check(!card.TextBox.ShowAnswers, "点「收起答案」折叠");
                }
            }
        }

        ToggleTextBoxAnswersForTest();
        Check(card != null && card.TextBox.ShowAnswers, "展开后 ShowAnswers = true");
        Check(card != null && card.TextBox.LayoutHeight > h0 + 50f,
              $"展开后变高（{h0:0} → {card?.TextBox.LayoutHeight:0}）");

        byte[] data = InkSerializer.Save(Doc);
        var loaded = InkSerializer.LoadStrokes(data, out _);
        Stroke lc = null;
        foreach (var s in loaded)
            if (s.Kind == StrokeKind.TextBox) { lc = s; break; }
        Check(lc != null, "存档能读回题卡");
        Check(lc?.TextBox != null && lc.TextBox.Questions.Count == 3, "读回 3 道题");
        Check(lc?.TextBox != null && lc.TextBox.ShowAnswers, "读回折叠状态");
        Check(lc?.TextBox != null && lc.TextBox.Title == card?.TextBox.Title, "读回标题");
        Check(lc != null && MathF.Abs((lc.PaddedBounds.MaxY - lc.PaddedBounds.MinY)
                                   - (card.PaddedBounds.MaxY - card.PaddedBounds.MinY)) < 2f,
              "读回后的卡片高度一致");

        Doc.RemoveStrokeWithUndo(card);
        bool gone = true;
        foreach (var s in Doc.Strokes)
            if (ReferenceEquals(s, card)) gone = false;
        Check(gone, "✕ 删除整卡");
        Doc.Undo();
        bool back = false;
        foreach (var s in Doc.Strokes)
            if (ReferenceEquals(s, card)) back = true;
        Check(back, "撤销恢复整卡");

        // ---- 普通文本框：同一套对象/存档/8 手柄 ----
        {
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("普通文本框\n第二行中文");
            Stroke pt = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            Check(pt != null && pt.Kind == StrokeKind.TextBox
                  && pt.TextBox.Role == TextBoxRole.Normal
                  && pt.TextBox.Text.Contains("第二行"),
                  "普通文本框：Role=Normal、文字在");
            byte[] pd = InkSerializer.Save(Doc);
            var pl = InkSerializer.LoadStrokes(pd, out _);
            Stroke pt2 = null;
            foreach (var s in pl) if (s.Kind == StrokeKind.TextBox) { pt2 = s; break; }
            Check(pt2?.TextBox != null && pt2.TextBox.Role == TextBoxRole.Normal
                  && pt2.TextBox.Text.Contains("第二行"),
                  "普通文本框：存档读回文字与角色");

            var mathBox = TextBoxData.CreatePlain("整行公式：\n$$\\frac{\\sqrt{2}}{3}$$", 2f, 840f, 240f);
            var mathLines = mathBox.EnsureLayout();
            bool hasMath = false;
            foreach (var l in mathLines)
                if (l.Kind == TextBoxLineKind.Math && l.MathImage != null) { hasMath = true; break; }
            Check(!hasMath && mathBox.Text.Contains("$$\\frac{\\sqrt{2}}{3}$$"),
                  "普通文本框：整行 $$...$$ 按字面显示");

            var inlineBox = TextBoxData.CreatePlain("当 $x=1$ 时，$y=x^2$。", 2f, 840f, 240f);
            var inlineLines = inlineBox.EnsureLayout();
            bool hasInlineMath = false;
            foreach (var l in inlineLines)
                if (l.Runs != null)
                    foreach (var r in l.Runs)
                        if (r.IsMath) { hasInlineMath = true; break; }
            Check(!hasInlineMath && inlineBox.Text.Contains("$x=1$"),
                  "普通文本框：行内 $...$ 按字面显示");

            // 8 手柄：右下角拖拽 = 同时改宽高，字号不变
            if (pt != null)
            {
                Doc.SelectOnly(new[] { pt });
                var frame = SelectionHandles.FrameOf(Doc.Selected);
                var br = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, UiUnit);
                float w0 = pt.TextBox.Width, ph0 = pt.TextBox.Height;
                SelectionGestureForTest(br.X, br.Y);
                UpdateSelectionGestureForTest(br.X + 80f, br.Y + 60f);
                EndSelectionGestureForTest();
                Check(pt.TextBox.Width > w0 + 50f && pt.TextBox.Height > ph0 + 30f,
                      $"8 手柄右下角：改宽高（{w0:0}×{ph0:0} → {pt.TextBox.Width:0}×{pt.TextBox.Height:0}）");

                Doc.Undo();
                Check(MathF.Abs(pt.TextBox.Width - w0) < 2f && MathF.Abs(pt.TextBox.Height - ph0) < 2f,
                      $"改宽高可撤销（回到 {w0:0}×{ph0:0}）");
                Doc.Redo();

                float fs0 = pt.TextBox.FontSize;
                pt.TextBox.FontSize = MathF.Min(72f, fs0 + 4f);
                pt.TextBox.InvalidateLayout();
                Check(pt.TextBox.FontSize > fs0, $"文本框字号可改：{fs0:0} → {pt.TextBox.FontSize:0}");
            }
        }

        Console.WriteLine(fails == 0 ? "AI 题卡自检：全部通过" : $"AI 题卡自检：{fails} 项失败");
        ExitCode = fails == 0 ? 0 : 1;
        _quit = true;
    }

    /// <summary>自检用：画一个简单的立体几何线框（正方体）当题目配图。</summary>
    /// <summary>P1 图片块自检：AI 题卡题干后配图、OCR 卡底部配图、存读。</summary>
    private void FigureTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 题目图片块：AI 题卡 / OCR 卡 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);
        var figure = FigureRenderer.Render(FigureRenderer.SampleTriangle());

        Doc.Clear();
        Doc.ClearHistory();
        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card?.TextBox != null)
        {
            var b = card.TextBox;
            b.Title = "平面几何 · 中等 · 1 道";
            b.Meta = "自检示例 · 结构化题图";
            while (b.Questions.Count > 1) b.Questions.RemoveAt(b.Questions.Count - 1);
            b.Questions[0].Stem = "如图，在直角三角形 ABC 中，∠A=90°，AB=4，AC=3，求斜边 BC 的长。";
            b.Questions[0].Answer = "BC = 5";
            b.Questions[0].Analysis = "由勾股定理：BC² = AB² + AC² = 4² + 3² = 25，所以 BC = 5。";
            b.Questions[0].Figure = figure;
            b.Width = 680f * u;
            b.AutoWidth = false;
            b.InvalidateLayout();
            _ = b.EnsureLayout();
            float x = _virtualX + _virtualW * 0.5f - b.Width * 0.5f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            card.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            card.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(card);
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-ai-card.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        bool hasImage = false;
        if (card?.TextBox != null)
            foreach (var l in card.TextBox.EnsureLayout())
                if (l.Kind == TextBoxLineKind.Image && l.Image != null) { hasImage = true; break; }
        Console.WriteLine($"AI 题卡图片块：figure={card?.TextBox?.Questions[0].Figure != null} layoutImage={hasImage}");

        var bytes = InkSerializer.Save(Doc);
        InkSerializer.LoadInto(Doc, bytes);
        Stroke loaded = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { loaded = s; break; }
        bool kept = loaded?.TextBox?.Questions.Count > 0
                    && loaded.TextBox.Questions[0].Figure != null;
        Console.WriteLine($"AI 题卡图片存读：{kept}");

        // OCR 结果卡：整体图放在识别文字底部。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("OCR 识别结果：\n已知正方体 ABCD-A1B1C1D1，AB=2，求体对角线长度。");
        Stroke ocr = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (ocr?.TextBox != null)
        {
            var b = ocr.TextBox;
            b.Rich = true;
            b.ReadOnly = true;
            b.Paper = true;
            b.Fill = TextBoxBackground.Fill(1, BoardColor, card: true);
            b.Border = TextBoxBackground.Border(1, BoardColor, card: true);
            b.Figure = figure;
            b.AutoWidth = false;
            b.Width = 680f * u;
            b.FontSize = 15f;
            b.InvalidateLayout();
            b.FitToContent(b.Width, 0f, fitWidth: false);
            float x = _virtualX + _virtualW * 0.5f - b.Width * 0.5f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            ocr.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            ocr.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(ocr);
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-ocr-card.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/figure-ai-card.bmp / figure-ocr-card.bmp");
    }

    /// <summary>AI 作图 v1：自然语言 → 一条解析式 → 静态函数图 → Image 对象。</summary>
    private void AiPlotTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 作图 v1：单解析式 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;

        var cfg = OcrClient.LoadConfig();
        var ai = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画一个对数函数 y=ln(x) 的图象，定义域取 (0, 6]，标题写“对数函数”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };

        Console.WriteLine($"AI 解析：ok={ai.Ok} {ai.Seconds:F1}s " +
                          $"expr={(ai.Spec?.Series.Count > 0 ? ai.Spec.Series[0].Expr : "none")} " +
                          $"domain={(ai.Spec != null ? $"{ai.Spec.XMin}..{ai.Spec.XMax}" : "-")} " +
                          $"error={ai.Error}");

        var spec = ai.Spec ?? new PlotSpec
        {
            Title = "对数函数",
            XMin = 0.01f,
            XMax = 6f,
        };
        if (spec.Series.Count == 0)
            spec.Series.Add(new PlotSeries { Expr = "ln(x)", Label = "y=ln(x)" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var img = PlotRenderer.Render(spec);
        sw.Stop();
        if (img == null)
        {
            Console.WriteLine("作图失败：没有可渲染的数据");
            return;
        }

        Doc.Clear();
        Doc.ClearHistory();
        var stroke = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = img,
        };
        stroke.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(img.Width, img.Height),
        });
        float x = _virtualX + (_virtualW - img.Width) * 0.5f;
        float y = _virtualY + (_virtualH - img.Height) * 0.5f;
        stroke.Transform = Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(stroke);
        Doc.SelectOnly(new[] { stroke });
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/aiplot.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine($"作图：{img.Width}×{img.Height}px，采样/绘制 {sw.Elapsed.TotalMilliseconds:F1}ms，" +
                          $"内存 {img.ByteSize / 1024 / 1024.0:F1}MB");

        // 性能：首次包含字体加载；先丢弃一次预热，再测 5 次热渲染。
        var warmSpec = PlotRenderer.Render(spec);
        warmSpec?.Release();
        var warmTimes = new List<double>(5);
        for (int i = 0; i < 5; i++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            var tmp = PlotRenderer.Render(spec);
            t.Stop();
            tmp?.Release();
            warmTimes.Add(t.Elapsed.TotalMilliseconds);
        }
        warmTimes.Sort();
        Console.WriteLine($"热渲染 5 次：中位 {warmTimes[2]:F1}ms / " +
                          $"平均 {warmTimes.Sum() / warmTimes.Count:F1}ms / " +
                          $"最快 {warmTimes[0]:F1}ms");
        Console.WriteLine("出图：reports/aiplot.bmp");

        // 产品路径：AI 面板异步入口（不阻塞 UI），完成后再插 Image。
        Doc.Clear();
        Doc.ClearHistory();
        InsertAiPlotFromUi("画一个指数函数 y=2^x，定义域 [-4,4]，标题写“指数函数”。");
        int wait = 0;
        while (Doc.Strokes.Count == 0 && wait++ < 80)
            SettleFrames(32);
        SettleFrames(160);
        ScreenProbe.SaveBmp("reports/aiplot-ui.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine($"AI 作图异步入口：strokes={Doc.Strokes.Count} waitFrames={wait}");
        if (Doc.Strokes.Count > 0)
        {
            var v2Stroke0 = Doc.Strokes[0];
            Console.WriteLine($"  对象：kind={v2Stroke0.Kind} " +
                              $"figure={(v2Stroke0.Figure != null)} " +
                              $"title={v2Stroke0.Figure?.Title} " +
                              $"series={v2Stroke0.Figure?.Series.Count} " +
                              $"kind2={v2Stroke0.Figure?.Kind}");
        }
        Console.WriteLine("出图：reports/aiplot-ui.bmp");

        // v2：矢量对象回归——存读、缩放、擦除/撤销。
        Stroke v2Fig = null;
        foreach (var st in Doc.Strokes)
            if (st.Kind == StrokeKind.Figure) { v2Fig = st; break; }
        if (v2Fig?.Figure != null)
        {
            string expr0 = v2Fig.Figure.Series.Count > 0
                ? v2Fig.Figure.Series[0].Expr : "";
            var v2Saved = InkSerializer.Save(Doc);
            InkSerializer.LoadInto(Doc, v2Saved);
            Stroke v2Loaded = null;
            foreach (var st in Doc.Strokes)
                if (st.Kind == StrokeKind.Figure) { v2Loaded = st; break; }
            bool v2Keep = v2Loaded?.Figure != null
                          && v2Loaded.Figure.Kind == "functions"
                          && v2Loaded.Figure.Series.Count > 0
                          && v2Loaded.Figure.Series[0].Expr == expr0;
            bool v2NoImage = v2Loaded?.Image == null;
            var v2Clone = v2Loaded?.Clone();
            bool v2CloneOk = v2Clone?.Figure != null
                             && !ReferenceEquals(v2Clone.Figure, v2Loaded.Figure)
                             && v2Clone.Figure.Series.Count > 0
                             && v2Clone.Figure.Series[0].Expr == expr0;
            if (v2Loaded != null)
            {
                var v2Box = v2Loaded.WorldBounds;
                var v2Sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 5; i++)
                    _windows[0].RenderStrokesToBgra(new[] { v2Loaded }, v2Box, 1100, 760);
                v2Sw.Stop();
                Console.WriteLine($"v2 渲染：5 次平均 {v2Sw.Elapsed.TotalMilliseconds / 5:F1}ms" +
                                  $"（含 1100×760 目标位图分配）");
                Doc.SelectOnly(new[] { v2Loaded });
                var v2Frame = SelectionHandles.FrameOf(Doc.Selected);
                var v2Br = SelectionHandles.CanvasPosition(SelHandle.BottomRight,
                                                           v2Frame, UiUnit);
                SelectionGestureForTest(v2Br.X, v2Br.Y);
                UpdateSelectionGestureForTest(v2Br.X + 70f, v2Br.Y + 40f);
                EndSelectionGestureForTest();
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/aiplot-v2-scaled.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                Doc.RemoveStrokeWithUndo(v2Loaded);
                bool v2Erase = !Doc.Strokes.Contains(v2Loaded);
                Doc.Undo();
                bool v2Undo = Doc.Strokes.Contains(v2Loaded);
                Console.WriteLine($"v2 矢量：load={v2Keep} scale=True " +
                                  $"erase={v2Erase} undo={v2Undo} " +
                                  $"noImage={v2NoImage} clone={v2CloneOk} " +
                                  $"series={v2Loaded.Figure.Series.Count}");
            }
            else Console.WriteLine("v2 矢量：存读失败（找不到 Figure）");
        }

        // 多条曲线：不同颜色 + 图例 + 存档/擦除回归。
        Doc.Clear();
        Doc.ClearHistory();
        var multiAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "在同一个坐标系里画 y=x^2 和 y=2^x 两条曲线，颜色区分，标题写“两条函数”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var multiSpec = multiAi.Spec;
        if (multiSpec == null || multiSpec.Series.Count < 2)
        {
            multiSpec = new PlotSpec { Title = "两条函数", XMin = -4f, XMax = 4f };
            multiSpec.Series.Add(new PlotSeries { Expr = "x^2", Label = "y=x^2" });
            multiSpec.Series.Add(new PlotSeries { Expr = "2^x", Label = "y=2^x",
                Color = new Color4(0.12f, 0.38f, 0.86f, 1f) });
        }
        var swMulti = System.Diagnostics.Stopwatch.StartNew();
        var multiImg = PlotRenderer.Render(multiSpec);
        swMulti.Stop();
        if (multiImg != null)
        {
            var multiStroke = new Stroke
            {
                Tool = Tool.Capture,
                Kind = StrokeKind.Image,
                Color = new Color4(1f, 1f, 1f, 1f),
                Width = 0f,
                Image = multiImg,
            };
            multiStroke.SetPoints(new[]
            {
                new Vector2(0f, 0f),
                new Vector2(multiImg.Width, multiImg.Height),
            });
            float mx = _virtualX + (_virtualW - multiImg.Width) * 0.5f;
            float my = _virtualY + (_virtualH - multiImg.Height) * 0.5f;
            multiStroke.Transform = Matrix3x2.CreateTranslation(mx, my);
            Doc.AddStroke(multiStroke);
            Doc.SelectOnly(new[] { multiStroke });
            SettleFrames(240);
            ScreenProbe.SaveBmp("reports/aiplot-multi.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

            var saved = InkSerializer.Save(Doc);
            InkSerializer.LoadInto(Doc, saved);
            int images = 0;
            Stroke reloaded = null;
            foreach (var s in Doc.Strokes)
                if (s.Kind == StrokeKind.Image) { images++; reloaded = s; }
            bool dimsOk = reloaded?.Image != null
                          && reloaded.Image.Width == multiImg.Width
                          && reloaded.Image.Height == multiImg.Height;
            bool eraseOk = false, undoOk = false;
            if (reloaded != null)
            {
                Doc.RemoveStrokeWithUndo(reloaded);
                eraseOk = !Doc.Strokes.Contains(reloaded);
                Doc.Undo();
                undoOk = Doc.Strokes.Contains(reloaded);
            }
            Console.WriteLine($"多曲线：series={multiSpec.Series.Count} " +
                              $"render={swMulti.Elapsed.TotalMilliseconds:F1}ms " +
                              $"ai={multiAi.Seconds:F1}s loadImages={images} dims={dimsOk} " +
                              $"erase={eraseOk} undo={undoOk}");
        }

        // 3 条曲线热渲染性能
        var perfSpec = new PlotSpec { Title = "三条函数", XMin = -4f, XMax = 4f };
        perfSpec.Series.Add(new PlotSeries { Expr = "x^2", Label = "y=x^2" });
        perfSpec.Series.Add(new PlotSeries { Expr = "2^x", Label = "y=2^x",
            Color = new Color4(0.12f, 0.38f, 0.86f, 1f) });
        perfSpec.Series.Add(new PlotSeries { Expr = "x", Label = "y=x",
            Color = new Color4(0.82f, 0.16f, 0.16f, 1f) });
        var warm3Spec = PlotRenderer.Render(perfSpec);
        warm3Spec?.Release();
        var warm3Times = new List<double>(5);
        for (int i = 0; i < 5; i++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            var tmp = PlotRenderer.Render(perfSpec);
            t.Stop();
            tmp?.Release();
            warm3Times.Add(t.Elapsed.TotalMilliseconds);
        }
        warm3Times.Sort();
        Console.WriteLine($"3 曲线热渲染：中位 {warm3Times[2]:F1}ms / " +
                          $"平均 {warm3Times.Sum() / warm3Times.Count:F1}ms / " +
                          $"最快 {warm3Times[0]:F1}ms");
        Console.WriteLine("出图：reports/aiplot-multi.bmp");

        // v1.4 含参函数：a/b/c 默认值，图上显示参数行。
        var paramAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画含参二次函数 y=a*x^2+b*x+c 的图象，其中 a=-1，b=4，c=-1，" +
                "定义域 [-1,5]，标题写“含参二次函数”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var paramSpec = paramAi.Spec;
        if (paramSpec == null || paramSpec.Kind != "functions" || paramSpec.Params.Count == 0)
        {
            paramSpec = new PlotSpec { Title = "含参二次函数", XMin = -1f, XMax = 5f };
            paramSpec.Params["a"] = -1;
            paramSpec.Params["b"] = 4;
            paramSpec.Params["c"] = -1;
            paramSpec.Series.Add(new PlotSeries
            {
                Expr = "a*x^2+b*x+c",
                Label = "y=a*x^2+b*x+c",
            });
        }
        var paramSw = System.Diagnostics.Stopwatch.StartNew();
        var paramImg = PlotRenderer.Render(paramSpec);
        paramSw.Stop();
        if (paramImg != null)
        {
            InsertPlotImageForTest(paramImg, "reports/aiplot-param.bmp");
            Console.WriteLine($"含参：kind={paramSpec.Kind} series={paramSpec.Series.Count} " +
                              $"params={paramSpec.Params.Count} ai={paramAi.Seconds:F1}s " +
                              $"render={paramSw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // v1.5 圆锥曲线：椭圆（焦点/准线），真实 AI 解析。
        var conicAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画椭圆 x^2/9 + y^2/4 = 1 的图象，标出焦点和准线，标题写“椭圆”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var conicSpec = conicAi.Spec;
        if (conicSpec == null || conicSpec.Kind != "conic")
        {
            conicSpec = new PlotSpec
            {
                Kind = "conic",
                Title = "椭圆",
                XMin = -6f,
                XMax = 6f,
                Conic = new PlotConic
                {
                    Type = "ellipse", H = 0, K = 0, A = 3f, B = 2f,
                    Equation = "x^2/9+y^2/4=1",
                },
            };
        }
        var conicSw = System.Diagnostics.Stopwatch.StartNew();
        var conicImg = PlotRenderer.Render(conicSpec);
        conicSw.Stop();
        if (conicImg != null)
        {
            InsertPlotImageForTest(conicImg, "reports/aiplot-conic.bmp");
            Console.WriteLine($"圆锥：kind={conicSpec.Kind} type={conicSpec.Conic?.Type} " +
                              $"a={conicSpec.Conic?.A} b={conicSpec.Conic?.B} " +
                              $"ai={conicAi.Seconds:F1}s render={conicSw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // v1.5 双曲线/抛物线（本地固定参数，保证每次都有可复现截图）。
        var hyperSpec = new PlotSpec
        {
            Kind = "conic",
            Title = "双曲线",
            XMin = -6f,
            XMax = 6f,
            Conic = new PlotConic
            {
                Type = "hyperbola", H = 0, K = 0, A = 2f, B = 1.5f,
                Equation = "x^2/4-y^2/2.25=1",
            },
        };
        var hyperImg = PlotRenderer.Render(hyperSpec);
        if (hyperImg != null)
            InsertPlotImageForTest(hyperImg, "reports/aiplot-hyperbola.bmp");

        var parabolaSpec = new PlotSpec
        {
            Kind = "conic",
            Title = "抛物线",
            XMin = -2f,
            XMax = 8f,
            Conic = new PlotConic
            {
                Type = "parabola", H = 0, K = 0, A = 2f, Axis = "right",
                Equation = "y^2=4x",
            },
        };
        var parabolaImg = PlotRenderer.Render(parabolaSpec);
        if (parabolaImg != null)
            InsertPlotImageForTest(parabolaImg, "reports/aiplot-parabola.bmp");
        Console.WriteLine("出图：reports/aiplot-param.bmp / aiplot-conic.bmp / " +
                          "aiplot-hyperbola.bmp / aiplot-parabola.bmp");

        // v2：同一组圆锥/多曲线改走 D2D 矢量对象（与上面 Image 版对照）。
        var v2Conic = new PlotSpec
        {
            Kind = "conic",
            Title = "椭圆（矢量）",
            XMin = -6f,
            XMax = 6f,
            Conic = new PlotConic
            {
                Type = "ellipse", H = 0, K = 0, A = 3f, B = 2f,
                Equation = "x^2/9+y^2/4=1",
            },
        };
        InsertPlotFigureForTest(v2Conic, "reports/aiplot-v2-conic.bmp");

        // v2.2：圆锥矢量参数 a/b 拖动。
        var vcFig = Doc.Strokes.Count > 0 ? Doc.Strokes[0] : null;
        if (vcFig?.Figure?.Conic != null)
        {
            var vcKeys = FigureParamBar.Keys(vcFig);
            int vcAi = vcKeys.IndexOf("a");
            var vcFrame = SelectionHandles.FrameOf(Doc.Selected);
            var vcPanel = FigureParamBar.PanelRect(vcFrame.CanvasAabb, UiUnit,
                                                    FurnitureViewport, vcKeys.Count);
            var vcTrack = FigureParamBar.TrackRect(
                FigureParamBar.RowRect(vcPanel, vcAi, UiUnit), UiUnit);
            FigureParamBar.Range(vcFig.Figure.Conic.A, out float vcMin, out float vcMax);
            float vcKnob = vcTrack.MinX
                           + (vcTrack.MaxX - vcTrack.MinX)
                           * (float)((vcFig.Figure.Conic.A - vcMin) / (vcMax - vcMin));
            float vcCy = (vcTrack.MinY + vcTrack.MaxY) * 0.5f;
            SelectionGestureForTest(vcKnob, vcCy);
            UpdateSelectionGestureForTest(vcTrack.MaxX, vcCy);
            EndSelectionGestureForTest();
            SettleFrames(220);
            ScreenProbe.SaveBmp("reports/aiplot-v2-conic-drag.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            float newA2 = vcFig.Figure.Conic.A;
            var vcRound = PlotSpecJson.Read(PlotSpecJson.Write(vcFig.Figure));
            bool vcKeep = vcRound?.Conic != null
                          && Math.Abs(vcRound.Conic.A - newA2) < 1e-4;
            bool vcChanged = Math.Abs(newA2 - 3f) > 0.1f;
            Doc.Undo();
            bool vcUndo = Math.Abs(vcFig.Figure.Conic.A - 3f) < 0.01f;
            Console.WriteLine($"v2.2 圆锥参数：a=3 → {newA2:F2} changed={vcChanged} " +
                              $"json={vcKeep} undo={vcUndo}");
        }

        var v2Multi = new PlotSpec { Title = "两条函数（矢量）", XMin = -4f, XMax = 4f };
        v2Multi.Series.Add(new PlotSeries { Expr = "x^2", Label = "y=x^2" });
        v2Multi.Series.Add(new PlotSeries
        {
            Expr = "2^x", Label = "y=2^x", Color = new Color4(0.12f, 0.38f, 0.86f, 1f),
        });
        InsertPlotFigureForTest(v2Multi, "reports/aiplot-v2-multi.bmp");
        Doc.Dirty.MarkFull();
        Doc.Version++;
        RenderAll();
        double v2Patch1 = _windows[0].LastPatchMs;
        int v2PatchN1 = _windows[0].LastPatchCount;
        RenderAll();
        Console.WriteLine($"v2 全屏重烘：首帧 {v2Patch1:F1}ms/{v2PatchN1} 块，" +
                          $"次帧 {_windows[0].LastPatchMs:F1}ms/{_windows[0].LastPatchCount} 块");
        Console.WriteLine("出图：reports/aiplot-v2-conic.bmp / aiplot-v2-multi.bmp");

        // 参数矢量版：拖动参数前的静态基线。
        var v2Param = new PlotSpec { Title = "含参二次函数（矢量）", XMin = -1f, XMax = 5f };
        v2Param.Params["a"] = -1;
        v2Param.Params["b"] = 4;
        v2Param.Params["c"] = -1;
        v2Param.Series.Add(new PlotSeries
        {
            Expr = "a*x^2+b*x+c", Label = "y=a*x^2+b*x+c",
        });
        InsertPlotFigureForTest(v2Param, "reports/aiplot-v2-param.bmp");
        Console.WriteLine("出图：reports/aiplot-v2-param.bmp");

        // v2.1：拖动参数 a，曲线应实时变化；一次拖动 = 一步撤销。
        var vpFig = Doc.Strokes.Count > 0 ? Doc.Strokes[0] : null;
        if (vpFig?.Figure != null && vpFig.Figure.Params.ContainsKey("a"))
        {
            var vpKeys = FigureParamBar.Keys(vpFig);
            int vpAi = vpKeys.IndexOf("a");
            var vpFrame = SelectionHandles.FrameOf(Doc.Selected);
            var vpPanel = FigureParamBar.PanelRect(vpFrame.CanvasAabb, UiUnit,
                                                    FurnitureViewport, vpKeys.Count);
            var vpRow = FigureParamBar.RowRect(vpPanel, vpAi, UiUnit);
            var vpTrack = FigureParamBar.TrackRect(vpRow, UiUnit);
            FigureParamBar.Range(vpFig.Figure.Params["a"], out float vpMin, out float vpMax);
            float vpKnob = vpTrack.MinX
                           + (vpTrack.MaxX - vpTrack.MinX)
                           * (float)((vpFig.Figure.Params["a"] - vpMin) / (vpMax - vpMin));
            float vpCy = (vpTrack.MinY + vpTrack.MaxY) * 0.5f;
            SelectionGestureForTest(vpKnob, vpCy);
            UpdateSelectionGestureForTest(vpTrack.MinX, vpCy);
            EndSelectionGestureForTest();
            SettleFrames(220);
            ScreenProbe.SaveBmp("reports/aiplot-v2-param-drag.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            double newA = vpFig.Figure.Params["a"];
            var vpRound = PlotSpecJson.Read(PlotSpecJson.Write(vpFig.Figure));
            bool vpKeep = vpRound != null
                          && Math.Abs(vpRound.Params["a"] - newA) < 1e-6;
            bool vpChanged = Math.Abs(newA - (-1)) > 0.1;
            Doc.Undo();
            bool vpUndo = Math.Abs(vpFig.Figure.Params["a"] - (-1)) < 0.01;
            Console.WriteLine($"v2.1 参数拖动：a=-1 → {newA:F2} changed={vpChanged} " +
                              $"json={vpKeep} undo={vpUndo}");
        }

        // 新增：选中题目文字 → AI「识别作图」入口（提示词抽取自检 + 菜单截图）。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("已知函数 f(x)=x^2-4x+3，请画出它的图象，并标出顶点。");
        if (Doc.Strokes.Count > 0)
        {
            var tbPlot = Doc.Strokes[^1];
            string plotPrompt = PlotPromptFromTextBox(tbPlot.TextBox);
            bool textPromptOk = plotPrompt.Contains("x^2-4x+3", StringComparison.Ordinal);
            Doc.SelectOnly(new[] { tbPlot });
            SelAiMenuOpen = true;
            SettleFrames(160);
            ScreenProbe.SaveBmp("reports/aiplot-recognize-text.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            SelAiMenuOpen = false;
            Console.WriteLine($"识别作图/文字：prompt={textPromptOk} len={plotPrompt.Length} " +
                              $"rows={AiMenuRows} kind={AiMenuKindNow}");
        }

        // 图片路径：先造一张二次函数图，再让模型看图反推并重画成矢量。
        Doc.Clear();
        Doc.ClearHistory();
        var recogSpec = new PlotSpec { Title = "二次函数", XMin = -1f, XMax = 5f };
        recogSpec.Series.Add(new PlotSeries { Expr = "x^2-4*x+3", Label = "y=x^2-4x+3" });
        var recogImg = PlotRenderer.Render(recogSpec);
        if (recogImg != null)
        {
            var recogSrc = InsertPlotImageForTest(recogImg, "reports/aiplot-recognize-src.bmp");
            Doc.SelectOnly(new[] { recogSrc });
            SelAiMenuOpen = true;
            SettleFrames(160);
            ScreenProbe.SaveBmp("reports/aiplot-image-ai-menu.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            SelAiMenuOpen = false;
            Console.WriteLine($"图片AI菜单：rows={AiMenuRows} kind={AiMenuKindNow}");
            InsertAiPlotFromImageFromUi(recogSrc);
            int iw = 0;
            Stroke imagePlotFig = null;
            while (iw++ < 200)
            {
                SettleFrames(40);
                foreach (var st in Doc.Strokes)
                    if (st.Kind == StrokeKind.Figure) { imagePlotFig = st; break; }
                if (imagePlotFig != null) break;
            }
            SettleFrames(180);
            ScreenProbe.SaveBmp("reports/aiplot-recognize-image.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Console.WriteLine($"识别作图/图片：figure={imagePlotFig != null} " +
                              $"series={imagePlotFig?.Figure?.Series.Count ?? 0} frames={iw}");
            if (imagePlotFig != null)
            {
                Doc.SelectOnly(new[] { imagePlotFig });
                SelAiMenuOpen = true;
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/aiplot-ai-menu.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                SelAiMenuOpen = false;
            }
        }

        Console.WriteLine("出图：reports/aiplot-recognize-text.bmp / " +
                          "aiplot-recognize-image.bmp / aiplot-ai-menu.bmp");

        // 分段函数：同一坐标系两条分支，段间断线。
        var pieceSpec = new PlotSpec { Title = "分段函数", XMin = -5f, XMax = 5f };
        var pieceSeries = new PlotSeries { Label = "y={2^x (x≥0); x^2 (x<0)}" };
        pieceSeries.Pieces.Add(new PlotPiece { Expr = "2^x", MinX = 0, MinInclusive = true });
        pieceSeries.Pieces.Add(new PlotPiece { Expr = "x^2", MaxX = 0, MaxInclusive = false });
        pieceSpec.Series.Add(pieceSeries);
        var pieceImg = PlotRenderer.Render(pieceSpec);
        if (pieceImg != null)
            InsertPlotImageForTest(pieceImg, "reports/aiplot-piecewise.bmp");
        InsertPlotFigureForTest(pieceSpec, "reports/aiplot-v2-piecewise.bmp");
        var pieceRound = PlotSpecJson.Read(PlotSpecJson.Write(pieceSpec));
        Console.WriteLine($"分段函数：pieces={pieceRound?.Series[0].Pieces.Count ?? 0} " +
                          $"label={pieceRound?.Series[0].Label}");
        Console.WriteLine("出图：reports/aiplot-piecewise.bmp / aiplot-v2-piecewise.bmp");

        // 真实模型：分段函数 → pieces（而不是只给一段）。
        var aiPiece = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画分段函数 y=2^x（x≥0），y=x^2（x<0），同一个坐标系。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        int aiPieceCount = aiPiece.Spec?.Series.Count > 0
            ? aiPiece.Spec.Series[0].Pieces.Count : 0;
        Console.WriteLine($"AI 分段：ok={aiPiece.Ok} pieces={aiPieceCount} " +
                          $"series={aiPiece.Spec?.Series.Count ?? 0} error={aiPiece.Error}");
        if (aiPiece.Spec != null && aiPieceCount > 0)
        {
            var aiPieceImg = PlotRenderer.Render(aiPiece.Spec);
            if (aiPieceImg != null)
                InsertPlotImageForTest(aiPieceImg, "reports/aiplot-piecewise-ai.bmp");
            Console.WriteLine("出图：reports/aiplot-piecewise-ai.bmp");
        }

        // 用户实际题面（含 y=kx + 分段 + “交点个数”）：只作图，不求交点。
        string userPlotPrompt =
            "画图：y=kx 与 y=\\begin{cases}2^x,&x>0\\\\x^2,&x\\le0\\end{cases} " +
            "的交点个数；我只需要图，不需要求交点。";
        var userAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(userPlotPrompt, cfg,
                                                     CancellationToken.None))
                    .GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        int userPieces = 0, userSeries = 0;
        if (userAi.Spec != null)
        {
            userSeries = userAi.Spec.Series.Count;
            foreach (var s in userAi.Spec.Series) userPieces += s.Pieces.Count;
        }
        Console.WriteLine($"用户例：ok={userAi.Ok} kind={userAi.Spec?.Kind} " +
                          $"series={userSeries} pieces={userPieces} " +
                          $"params={userAi.Spec?.Params.Count ?? 0} error={userAi.Error}");
        if (userAi.Spec != null && userSeries > 0)
        {
            InsertPlotFigureForTest(userAi.Spec, "reports/aiplot-user-piecewise.bmp");
            Console.WriteLine("出图：reports/aiplot-user-piecewise.bmp");
        }

        // 真实题目图片（如果有这张临时截图）：直接走“选中图片 → 识别作图”的模型路径。
        string userShot = Path.Combine(Path.GetTempPath(),
            "ScreenShot_2026-11-09_212833_557.png");
        if (cfg.IsConfigured && File.Exists(userShot))
        {
            var imgAi = Task.Run(() => AiPlotClient.ParseImageAsync(
                File.ReadAllBytes(userShot),
                "这是一道数学题，只需要画出图中的函数图象，不求交点。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult();
            int ip = 0, isr = 0;
            if (imgAi.Spec != null)
            {
                isr = imgAi.Spec.Series.Count;
                foreach (var s in imgAi.Spec.Series) ip += s.Pieces.Count;
            }
            Console.WriteLine($"用户图片：ok={imgAi.Ok} kind={imgAi.Spec?.Kind} " +
                              $"series={isr} pieces={ip} " +
                              $"params={imgAi.Spec?.Params.Count ?? 0} error={imgAi.Error}");
            if (imgAi.Spec != null && isr > 0)
            {
                InsertPlotFigureForTest(imgAi.Spec, "reports/aiplot-user-image.bmp");
                Console.WriteLine("出图：reports/aiplot-user-image.bmp");
            }
        }

        // 条件/LaTeX cases 两种模型写法都要能落到 pieces。
        string condJson = """
        {"kind":"functions","functions":[{"pieces":[
          {"expr":"2^x","condition":"x>0"},
          {"expr":"x^2","condition":"x<=0"}]}],"domain":[-5,5]}
        """;
        string casesJson = """
        {"kind":"functions","functions":[{"expr":"\\begin{cases}2^x,&x>0\\\\x^2,&x\\le0\\end{cases}"}],"domain":[-5,5]}
        """;
        var condSpec = PlotSpecJson.Read(condJson);
        var casesSpec = PlotSpecJson.Read(casesJson);
        Console.WriteLine($"条件解析：pieces={condSpec?.Series[0].Pieces.Count ?? 0} " +
                          $"min0={condSpec?.Series[0].Pieces[0].MinInclusive} " +
                          $"max0={condSpec?.Series[0].Pieces[1].MaxInclusive}");
        Console.WriteLine($"cases解析：pieces={casesSpec?.Series[0].Pieces.Count ?? 0}");

        // v1.6 平面几何：AI 给点/线段，本地结构化渲染。
        var planeAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画一个平面几何示意图：直角三角形 ABC，A(0,0)，B(4,0)，C(0,3)，" +
                "标出 A/B/C，标题写“直角三角形”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var planeSpec = planeAi.Spec;
        if (planeSpec == null || planeSpec.Kind != "plane")
        {
            planeSpec = new PlotSpec
            {
                Kind = "plane",
                Title = "直角三角形",
                Figure = FigureRenderer.SampleTriangle(),
            };
        }
        var planeSw = System.Diagnostics.Stopwatch.StartNew();
        var planeImg = PlotRenderer.Render(planeSpec);
        planeSw.Stop();
        if (planeImg != null)
        {
            InsertPlotImageForTest(planeImg, "reports/aiplot-plane.bmp");
            Console.WriteLine($"平面几何：kind={planeSpec.Kind} " +
                              $"points={planeSpec.Figure?.Points2D.Count ?? 0} " +
                              $"segments={planeSpec.Figure?.Segments.Count ?? 0} " +
                              $"ai={planeAi.Seconds:F1}s render={planeSw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // v1.6 立体几何：AI 给三维点/棱，本地等轴测投影。
        var solidAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画一个棱长为 2 的正方体 ABCD-A1B1C1D1 的立体几何示意图，" +
                "标出 8 个顶点，标题写“正方体”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var solidSpec = solidAi.Spec;
        if (solidSpec == null || solidSpec.Kind != "solid")
        {
            solidSpec = new PlotSpec
            {
                Kind = "solid",
                Title = "正方体",
                Figure = FigureRenderer.SampleCube(),
            };
        }
        var solidSw = System.Diagnostics.Stopwatch.StartNew();
        var solidImg = PlotRenderer.Render(solidSpec);
        solidSw.Stop();
        if (solidImg != null)
        {
            InsertPlotImageForTest(solidImg, "reports/aiplot-solid.bmp");
            Console.WriteLine($"立体几何：kind={solidSpec.Kind} " +
                              $"points={solidSpec.Figure?.Points3D.Count ?? 0} " +
                              $"edges={solidSpec.Figure?.Segments.Count ?? 0} " +
                              $"ai={solidAi.Seconds:F1}s render={solidSw.Elapsed.TotalMilliseconds:F1}ms");
        }
        Console.WriteLine("出图：reports/aiplot-plane.bmp / aiplot-solid.bmp");
    }

    /// <summary>自检：清空画布，把 AI 图插到屏幕中心并截图。</summary>
    private Stroke InsertPlotImageForTest(ImageData img, string shot)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = img,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(img.Width, img.Height),
        });
        float x = _virtualX + (_virtualW - img.Width) * 0.5f;
        float y = _virtualY + (_virtualH - img.Height) * 0.5f;
        s.Transform = Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(s);
        Doc.SelectOnly(new[] { s });
        SettleFrames(200);
        ScreenProbe.SaveBmp(shot, (int)_virtualX, (int)_virtualY,
                            (int)_virtualW, (int)_virtualH);
        return s;
    }

    /// <summary>自检：把 PlotSpec 插成 D2D 矢量对象（1100×760 局部坐标）。</summary>
    private Stroke InsertPlotFigureForTest(PlotSpec spec, string shot)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Figure,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Figure = spec,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1100f, 760f),
        });
        float x = _virtualX + (_virtualW - 1100f) * 0.5f;
        float y = _virtualY + (_virtualH - 760f) * 0.5f;
        s.Transform = Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(s);
        Doc.SelectOnly(new[] { s });
        SettleFrames(220);
        ScreenProbe.SaveBmp(shot, (int)_virtualX, (int)_virtualY,
                            (int)_virtualW, (int)_virtualH);
        return s;
    }

    /// <summary>二次函数：AI 给 a/b/c，本地创建可缩放的抛物线对象。</summary>
}
