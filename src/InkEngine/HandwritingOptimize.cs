using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// AI 菜单「手写优化」的运行时：
///   选中文字笔迹 → 渲染成白底 PNG → 调 <see cref="OcrClient"/> → 逐行 Recognize
///   → 回到主线程 <see cref="HandwritingReplace.Apply"/>（一步撤销）。
///
/// 网络在后台线程跑，主循环只做“取结果 + 改文档”，界面不会卡。
/// </summary>
public partial class InkEngine
{
    private enum HandOcrMode { Handwriting, TextBox, Plot }

    private sealed class HandOcrDone
    {
        public List<Stroke> Selected;
        public RectF Region;
        public OcrClient.OcrResult Result;
        public bool IncludeFormulas;
        public HandOcrMode Mode;
        public Color4 InkColor;
    }

    private bool _handOcrBusy;
    private string _handOcrStatus = "";
    private HandOcrDone _handOcrDone;
    private CancellationTokenSource _handOcrCts;
    private int _handOcrGeneration;

    internal bool HandOcrBusy => _handOcrBusy;
    internal string HandOcrStatus => _handOcrStatus;

    private void SetHandOcrStatus(string text)
    {
        _handOcrStatus = text ?? "";
        NotifyUiStateChanged();
        InvalidateUi();
    }

    /// <summary>AI 菜单「手写优化」：开始识别，立即返回。</summary>
    internal void OptimizeHandwritingFromUi(bool includeFormulas = false)
        => StartHandOcr(HandOcrMode.Handwriting, includeFormulas);

    /// <summary>AI 菜单「识别到文本框」：识别后放进普通文本框（公式用 $...$ 渲染）。</summary>
    internal void RecognizeToTextBoxFromUi()
        => StartHandOcr(HandOcrMode.TextBox, true);

    /// <summary>
    /// AI 菜单「识别作图」：
    ///   · 选中图片 → 模型直接看图，反推函数/圆锥/几何；
    ///   · 选中文本框/题卡 → 用现有文字直接解析；
    ///   · 选中手写 → OCR 成文字后走同一套作图解析。
    /// </summary>
    internal void RecognizePlotFromUi()
    {
        if (Doc.Selected.Count == 1)
        {
            var only = Doc.Selected[0];
            if (only.Kind == StrokeKind.Image && only.Image != null)
            {
                InsertAiPlotFromImageFromUi(only);
                return;
            }
            if (only.Kind == StrokeKind.Figure)
            {
                SetHandOcrStatus("已经是矢量图：选中后拖参数滑杆即可");
                return;
            }
            if (only.Kind == StrokeKind.TextBox && only.TextBox != null)
            {
                string text = PlotPromptFromTextBox(only.TextBox);
                if (text.Length > 0)
                {
                    SetHandOcrStatus("已读取题目，正在解析作图…");
                    InsertAiPlotFromUi(text);
                }
                else SetHandOcrStatus("文本框里没有可作图的题目");
                return;
            }
        }
        StartHandOcr(HandOcrMode.Plot, true);
    }

    /// <summary>文本框/题卡 → 作图提示词（普通文本 + 所有题干）。</summary>
    internal static string PlotPromptFromTextBox(TextBoxData b)
    {
        if (b == null) return "";
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(b.Text)) sb.AppendLine(b.Text);
        foreach (var q in b.Questions)
            if (!string.IsNullOrWhiteSpace(q.Stem)) sb.AppendLine(q.Stem);
        return sb.ToString().Trim();
    }

    private void StartHandOcr(HandOcrMode mode, bool includeFormulas)    {
        if (_handOcrBusy)
        {
            SetHandOcrStatus("正在识别，请稍候…");
            return;
        }

        var selected = new List<Stroke>();
        foreach (var s in Doc.Selected)
        {
            if (s == null) continue;
            if (s.Kind != StrokeKind.Freehand) continue;
            if (s.Tool == Tool.Highlighter) continue;
            if (s.Locked || s.IsImage || s.IsShape) continue;
            selected.Add(s);
        }
        if (selected.Count == 0)
        {
            SetHandOcrStatus(mode == HandOcrMode.Plot
                ? "请选中题目文字/手写题，或单张题目图片"
                : "先框选要优化的文字笔迹");
            return;
        }

        int graphicLike = 0;
        foreach (var s in selected)
        {
            var b = s.WorldInkBounds;
            if (b.IsEmpty) continue;
            if (b.MaxX - b.MinX > 350f || b.MaxY - b.MinY > 180f) graphicLike++;
        }
        if (mode != HandOcrMode.TextBox && graphicLike > 3)
        {
            SetHandOcrStatus($"选区里图形较多（{graphicLike} 条长线），请只框选文字");
            return;
        }

        // 「识别到文本框」/「识别作图」只需要题目文字：把图形长线从 OCR 图里剔掉（公式分式线一般 <350）。
        if (mode is HandOcrMode.TextBox or HandOcrMode.Plot)
        {
            var textOnly = new List<Stroke>();
            foreach (var s in selected)
            {
                var b = s.WorldInkBounds;
                if (b.IsEmpty) continue;
                if (b.MaxX - b.MinX <= 360f && b.MaxY - b.MinY <= 190f) textOnly.Add(s);
            }
            if (textOnly.Count >= 3) selected = textOnly;
        }

        var cfg = OcrClient.LoadConfig();
        if (!cfg.IsConfigured)
        {
            SetHandOcrStatus("未配置识别服务（ai.json）");
            return;
        }

        string font = GlyphText.BundledFontPath;
        if (mode != HandOcrMode.Plot && string.IsNullOrEmpty(font))
        {
            SetHandOcrStatus("缺少手写字体文件");
            return;
        }

        var region = EditRegion.Of(selected);
        if (region.IsEmpty)
        {
            SetHandOcrStatus("选区没有墨迹");
            return;
        }

        var inkColor = DominantInkColor(selected);

        byte[] png = RenderSelectionPng(selected, region);
        if (png == null)
        {
            SetHandOcrStatus("选区太大，请只框选一块文字");
            return;
        }

        _handOcrBusy = true;
        SetHandOcrStatus(mode == HandOcrMode.TextBox
            ? "识别中…（文本框）"
            : mode == HandOcrMode.Plot
                ? "识别作图中…（先认题，再解析）"
                : includeFormulas ? "识别中…（含公式）" : "识别中…（约 2–5 秒）");

        _handOcrCts?.Cancel();
        _handOcrCts = new CancellationTokenSource();
        int gen = ++_handOcrGeneration;
        var ct = _handOcrCts.Token;

        Task.Run(async () =>
        {
            string prompt = mode == HandOcrMode.TextBox
                ? OcrClient.BoxPrompt
                : mode == HandOcrMode.Plot
                    ? OcrClient.BoardPrompt
                    : includeFormulas ? OcrClient.BoxPrompt : OcrClient.BoardPrompt;
            var result = await OcrClient.RecognizeAsync(png, cfg, prompt, ct,
                                        withBoxes: mode == HandOcrMode.TextBox)
                                        .ConfigureAwait(false);
            if (gen != Volatile.Read(ref _handOcrGeneration)) return;
            var done = new HandOcrDone { Selected = selected, Region = region, Result = result,
                                          IncludeFormulas = includeFormulas, Mode = mode,
                                          InkColor = inkColor };
            Interlocked.Exchange(ref _handOcrDone, done);
        });
    }

    /// <summary>主循环每帧调一次：把后台识别结果搬回文档（只在主线程改 Doc）。</summary>
    internal void StepHandwritingOcr()
    {
        var done = Interlocked.Exchange(ref _handOcrDone, null);
        if (done == null) return;

        _handOcrBusy = false;
        if (done.Result == null)
        {
            SetHandOcrStatus("识别失败：没有返回结果");
            return;
        }
        if (!done.Result.Ok)
        {
            string why = string.IsNullOrWhiteSpace(done.Result.Error) ? "未知错误" : done.Result.Error;
            Console.WriteLine($"[手写优化] 识别失败：{why}（{done.Result.Seconds:F1}s）");
            SetHandOcrStatus("识别失败：" + Clip(why, 26));
            return;
        }
        if (SelfCheckMode)
        {
            Console.WriteLine($"[手写优化] OCR 返回 {done.Result.Lines.Length} 行：");
            for (int i = 0; i < done.Result.Lines.Length; i++)
            {
                string t = done.Result.Lines[i];
                if (t.Length > 70) t = t[..70] + "…";
                Console.WriteLine($"  {i + 1,2}. math={OcrText.HasMath(t)}  {t}");
            }
        }

        // 识别期间选区可能被改掉 / 撤销；对象不在了就放弃，绝不误删。
        var alive = new HashSet<Stroke>(Doc.Strokes);
        foreach (var s in done.Selected)
            if (!alive.Contains(s))
            {
                SetHandOcrStatus("选区已变化，已取消");
                return;
            }

        if (done.Mode == HandOcrMode.Plot)
        {
            var plotLines = new List<string>();
            foreach (var line in done.Result.Lines)
                if (!IsEmptyOcrLine(line)) plotLines.Add(line);
            string text = string.Join("\n", plotLines);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetHandOcrStatus("没认出题目文字，请只框选题目");
                return;
            }
            SetHandOcrStatus("已认出题目，正在解析作图…");
            Console.WriteLine($"[识别作图] OCR {done.Result.Lines.Length} 行，{done.Result.Seconds:F1}s");
            InsertAiPlotFromUi(text);
            return;
        }

        if (done.Mode == HandOcrMode.TextBox)
        {
            string text = string.Join("\n", done.Result.Lines);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetHandOcrStatus("识别结果为空");
                return;
            }
            var boxes = CreateRecognizedTextBoxes(done);
            if (boxes.Count == 0)
            {
                SetHandOcrStatus("创建文本框失败");
                return;
            }
            // 识别成功：原墨迹删除 + 新文本框加入，**一步撤销**。
            // 之前只加框不删墨：98% 白纸底下会透出原笔迹（2026-11-09 实测“鬼影”）。
            var remove = new RemoveStrokesAction();
            var seen = new HashSet<Stroke>();
            foreach (var s in done.Selected)
            {
                if (!seen.Add(s)) continue;
                int idx = Doc.Strokes.IndexOf(s);
                if (idx >= 0) remove.Items.Add((idx, s));
            }
            var add = new AddStrokesAction();
            add.Strokes.AddRange(boxes);
            remove.Redo(Doc);
            add.Redo(Doc);
            Doc.CommitCompound(remove, add);
            // 2026-11-08 用户定：识别完**默认不选中**新文本框。
            // 选中会立刻弹出操作条 + 手柄，把刚识别出来的字盖住，编辑反而更别扭；
            // 不选中时它就是页面上的普通文字，单击选中、双击编辑。
            Doc.Selected.Clear();
            _dirty = true;
            ShowToast(boxes.Count > 1
                ? $"已识别为文本（{boxes.Count} 栏）· 双击编辑 · 可撤销"
                : "已识别为文本 · 双击编辑 · 可撤销");
            Console.WriteLine($"[识别到文本框] {done.Result.Lines.Length} 行，{done.Result.Seconds:F1}s，" +
                              $"{boxes.Count} 个文本框（默认不选中）");
            SetHandOcrStatus(boxes.Count > 1
                ? $"已识别 {done.Result.Lines.Length} 行 → {boxes.Count} 栏文本"
                : $"已识别 {done.Result.Lines.Length} 行 → 文本");
            return;
        }

        string font = GlyphText.BundledFontPath;
        if (string.IsNullOrEmpty(font))
        {
            SetHandOcrStatus("缺少手写字体文件");
            return;
        }

        int boxed = 0;
        foreach (var item in done.Result.Items) if (item.HasBox) boxed++;

        // 先做一次切行对齐检查：OCR 行数和几何行数差太多，说明这一页是
        // 双栏 / 公式碎片 / 图形混排，硬替换会把整行搬到错误位置。
        var detected = HandwritingReplace.DetectLines(done.Selected, done.Region,
                                                      done.Result.Lines.Length);
        if (detected.Count != done.Result.Lines.Length)
        {
            // 几何对不上时再走 OCR 行框；单栏能对齐的板书仍用几何切行，
            // 它比 bbox 更贴原笔迹（bbox 容易漏掉框边的批注/下划线）。
            // bbox 路径目前仍是实验：b2 实测会留下大量框外残笔，默认不开。
            if (Environment.GetEnvironmentVariable("INKTEACH_HAND_BBOX") == "1"
                && done.Result.Items.Count == done.Result.Lines.Length
                && boxed >= done.Result.Lines.Length * 0.7f)
            {
                int bboxLines = ApplyBboxHandwriting(done, font, out var bboxGlyphs);
                if (bboxLines > 0)
                {
                    Doc.Selected.Clear();
                    Doc.Selected.AddRange(bboxGlyphs);
                    Console.WriteLine($"[手写优化/bbox] {bboxLines} 行，{done.Result.Seconds:F1}s");
                    SetHandOcrStatus($"已优化 {bboxLines} 行（按识别框）");
                    return;
                }
            }
            Console.WriteLine($"[手写优化] 拒绝对齐：几何 {detected.Count} 行 / OCR {done.Result.Lines.Length} 行");
            SetHandOcrStatus($"排版复杂（检测 {detected.Count} / 识别 {done.Result.Lines.Length} 行），请只框选文字");
            return;
        }

        int lines = HandwritingReplace.Apply(Doc, done.Selected, done.Region,
                                             done.Result.Lines, font, done.IncludeFormulas,
                                             out var glyphs, out int skipped);
        if (lines == 0)
        {
            SetHandOcrStatus(skipped > 0
                ? $"只识别到公式行（{skipped} 行已保留原笔迹）"
                : "没有匹配到可替换的文字行");
            return;
        }

        // 替换完选中新对象：操作条还在，AI 菜单还能继续显示结果。
        Doc.Selected.Clear();
        Doc.Selected.AddRange(glyphs);
        Console.WriteLine($"[手写优化] {lines} 行，{done.Result.Seconds:F1}s，" +
                          $"tokens {done.Result.PromptTokens}/{done.Result.CompletionTokens}");
        if (skipped > 0)
            SetHandOcrStatus($"已优化 {lines} 行；{skipped} 个公式行保留原笔迹");
        else if (done.IncludeFormulas)
        {
            int math = 0;
            foreach (var line in done.Result.Lines)
                if (OcrText.HasMath(line)) math++;
            SetHandOcrStatus(math > 0
                ? $"已优化 {lines} 行；公式可能有识别误差"
                : $"已优化 {lines} 行（Ctrl+Z 可撤销）");
        }
        else
            SetHandOcrStatus($"已优化 {lines} 行（Ctrl+Z 可撤销）");
    }

    /// <summary>
    /// 识别结果 → 普通文本框。尽量贴近原选区：
    ///   · 框宽 = 选区宽 + 左右内边距，位置从原选区左上角开始；
    ///   · 字号 / 行距由“贴近墨迹的几何行”反推；
    ///   · 每一行自己的墨色（红蓝批注分得开），公式也在对应行色里烘；
    ///   · 双栏板书按 OCR bbox 的 x 空隙拆成两个框，避免把两栏叠成一长条。
    /// </summary>
    private List<Stroke> CreateRecognizedTextBoxes(HandOcrDone done)
    {
        var items = done.Result.Items;
        if (items.Count == done.Result.Lines.Length && items.Count > 3)
        {
            var groups = SplitColumns(items);
            if (groups.Count > 1)
            {
                var multi = new List<Stroke>();
                foreach (var group in groups)
                {
                    var s = CreateRecognizedTextBoxForGroup(done, group);
                    if (s != null) multi.Add(s);
                }
                if (multi.Count > 0) return multi;
            }
        }

        var one = CreateRecognizedTextBoxForGroup(done,
            items.Count == done.Result.Lines.Length ? items : null);
        return one != null ? new List<Stroke> { one } : new List<Stroke>();
    }

    private Stroke CreateRecognizedTextBoxForGroup(HandOcrDone done, List<OcrClient.OcrLine> items)
    {
        bool useItems = items != null && items.Count > 0;
        var lines = new List<string>();
        if (useItems) foreach (var item in items) lines.Add(item.Text);
        else lines.AddRange(done.Result.Lines);
        lines.RemoveAll(IsEmptyOcrLine);
        string text = string.Join("\n", lines);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var region = useItems ? ItemsRegion(items, done.Region) : done.Region;
        region = region.Inflate(10f);
        var pngBox = done.Region.Inflate(12f);
        float u = MathF.Max(0.75f, DpiScale);
        float pad = 18f * u;

        var colors = new List<Color4>();
        var heights = new List<float>();
        var centers = new List<float>();

        // 几何切行始终算一份：它比 OCR 的 bbox 更贴**墨迹本身**，用来定字号和行距；
        // OCR bbox 主要负责“这一行是什么颜色”和双栏拆分。
        var detected = HandwritingReplace.DetectLines(done.Selected, region, lines.Count);

        if (useItems)
        {
            foreach (var item in items)
            {
                if (item.HasBox)
                {
                    var box = ItemBox(item, pngBox);
                    colors.Add(DominantInkColor(done.Selected, box));
                }
                else colors.Add(done.InkColor);
            }
        }
        else if (detected.Count == lines.Count)
        {
            foreach (var line in detected) colors.Add(line.Color);
        }

        // 字号 / 行距：优先用几何行（紧贴墨迹）；几何行切多了/切少了也用它的
        // **低位数**，因为中位数容易被分式、上下标撑高的行带带偏。
        bool useDetectedMetrics = detected.Count >= 2
                                  && detected.Count >= lines.Count * 0.65f;
        if (useDetectedMetrics)
        {
            foreach (var line in detected)
            {
                heights.Add(line.Box.MaxY - line.Box.MinY);
                centers.Add((line.Box.MinY + line.Box.MaxY) * 0.5f);
            }
        }
        else if (useItems)
        {
            foreach (var item in items)
            {
                if (!item.HasBox) continue;
                var box = ItemBox(item, pngBox);
                heights.Add(box.MaxY - box.MinY);
                centers.Add((box.MinY + box.MaxY) * 0.5f);
            }
        }

        float medianH = LowerMedian(heights,
            detected.Count >= lines.Count ? 0.45f : 0.5f);
        if (medianH <= 0f)
            medianH = (region.MaxY - region.MinY) / MathF.Max(1f, lines.Count);

        centers.Sort();
        var gaps = new List<float>();
        for (int i = 1; i < centers.Count; i++)
        {
            float gap = centers[i] - centers[i - 1];
            if (gap > 1f) gaps.Add(gap);
        }
        float pitch = Median(gaps);

        // 两个估计取大：紧贴墨迹的行高，和“行中心距 / 1.75”。
        // 只用行高会把小字行估小，再用大行距补高度，结果字小、行空。
        float fontPx = Math.Clamp(MathF.Max(medianH * 0.76f,
                                            pitch > 0f && detected.Count >= lines.Count
                                                ? pitch / 1.75f : 0f),
                                  9f * u, 72f * u);

        float spacing = detected.Count >= lines.Count && pitch > 0f
            ? Math.Clamp(pitch / fontPx, 1.0f, 2.2f)
            : TextBoxFaces.DefaultLineSpacing;   // 几何行被合并（双栏/整页）：不拿合并行距当行距

        float boxW = MathF.Max(120f * u, (region.MaxX - region.MinX) + pad * 2f);
        var target = new RectF
        {
            MinX = region.MinX - pad,
            MinY = region.MinY - pad,
            MaxX = region.MinX - pad + boxW,
            MaxY = region.MaxY + pad,
        };
        if (SelfCheckMode)
        {
            int boxes = 0;
            if (useItems) foreach (var item in items) if (item.HasBox) boxes++;
            Console.WriteLine($"[布局] 选区 {(region.MaxX - region.MinX):F0}×{(region.MaxY - region.MinY):F0}，" +
                              $"OCR {items?.Count ?? 0} 行（bbox {boxes}），几何 {detected.Count} 行，" +
                              $"中位行高 {medianH:F1} → 字号 {fontPx / u:F1}px，行距 {spacing:F2}×");
        }
        var card = CreatePlainTextBoxAt(text, target, done.InkColor, TextBoxFace.YaHei,
                                        spacing, fontPx / u, colors, addToDoc: false);
        if (card?.TextBox != null)
        {
            // OCR 结果是**只读富文本卡**：文字 + 公式混排，但不进文字编辑。
            card.TextBox.Rich = true;
            card.TextBox.ReadOnly = true;
            card.TextBox.FitToContent(card.TextBox.Width, 0f, fitWidth: false);
            card.SyncTextBoxBounds();
        }
        return card;
    }

    private static RectF ItemBox(OcrClient.OcrLine item, RectF pngBox)
        => new()
        {
            MinX = pngBox.MinX + item.X0 * (pngBox.MaxX - pngBox.MinX),
            MinY = pngBox.MinY + item.Y0 * (pngBox.MaxY - pngBox.MinY),
            MaxX = pngBox.MinX + item.X1 * (pngBox.MaxX - pngBox.MinX),
            MaxY = pngBox.MinY + item.Y1 * (pngBox.MaxY - pngBox.MinY),
        };

    private static RectF ItemsRegion(List<OcrClient.OcrLine> items, RectF fallback)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var item in items)
        {
            if (!item.HasBox) continue;
            x0 = MathF.Min(x0, item.X0);
            y0 = MathF.Min(y0, item.Y0);
            x1 = MathF.Max(x1, item.X1);
            y1 = MathF.Max(y1, item.Y1);
        }
        if (x1 <= x0 || y1 <= y0) return fallback;
        var pngBox = fallback.Inflate(12f);
        return new RectF
        {
            MinX = pngBox.MinX + x0 * (pngBox.MaxX - pngBox.MinX),
            MinY = pngBox.MinY + y0 * (pngBox.MaxY - pngBox.MinY),
            MaxX = pngBox.MinX + x1 * (pngBox.MaxX - pngBox.MinX),
            MaxY = pngBox.MinY + y1 * (pngBox.MaxY - pngBox.MinY),
        };
    }

    /// <summary>
    /// 按 OCR bbox 的 x0 最大空隙拆栏。只在“空隙 &gt; 0.2 图片宽、两边都有 4 行以上”时拆，
    /// 单栏板书（b1/b2）不会被误拆；双栏（b3）左右各成一块。
    /// </summary>
    private static List<List<OcrClient.OcrLine>> SplitColumns(List<OcrClient.OcrLine> items)
    {
        var groups = new List<List<OcrClient.OcrLine>>();
        SplitColumns(items, groups, 0);
        groups.Sort((a, b) => MinX(a).CompareTo(MinX(b)));
        return groups;

        static float MinX(List<OcrClient.OcrLine> list)
        {
            float m = float.MaxValue;
            foreach (var i in list) if (i.HasBox) m = MathF.Min(m, i.X0);
            return m;
        }
    }

    private static void SplitColumns(List<OcrClient.OcrLine> items,
                                     List<List<OcrClient.OcrLine>> groups, int depth)
    {
        if (depth >= 3 || items.Count < 8)
        {
            groups.Add(items);
            return;
        }

        var xs = new List<float>();
        foreach (var item in items) if (item.HasBox) xs.Add((item.X0 + item.X1) * 0.5f);
        if (xs.Count < items.Count * 0.75f)
        {
            groups.Add(items);
            return;
        }
        xs.Sort();

        float bestGap = 0f, cut = 0f;
        for (int i = 1; i < xs.Count; i++)
        {
            float gap = xs[i] - xs[i - 1];
            if (gap > bestGap) { bestGap = gap; cut = (xs[i] + xs[i - 1]) * 0.5f; }
        }
        if (bestGap < 0.2f)
        {
            groups.Add(items);
            return;
        }

        var left = new List<OcrClient.OcrLine>();
        var right = new List<OcrClient.OcrLine>();
        bool seenRight = false;
        foreach (var item in items)
        {
            float cx = (item.X0 + item.X1) * 0.5f;
            bool isRight = item.HasBox ? cx >= cut : seenRight;
            if (item.HasBox && cx >= cut) seenRight = true;
            (isRight ? right : left).Add(item);
        }
        if (left.Count < 4 || right.Count < 4)
        {
            groups.Add(items);
            return;
        }

        // 两栏的横向范围必须基本不重叠；交叠说明是“宽左栏 + 右侧批注”，
        // 硬拆会让两个纸面互相盖住（b3 实测），这种宁可保持一个长框。
        float leftMax = float.MinValue, rightMin = float.MaxValue;
        foreach (var item in left) if (item.HasBox) leftMax = MathF.Max(leftMax, item.X1);
        foreach (var item in right) if (item.HasBox) rightMin = MathF.Min(rightMin, item.X0);
        if (leftMax > rightMin + 0.08f)
        {
            groups.Add(items);
            return;
        }

        SplitColumns(left, groups, depth + 1);
        SplitColumns(right, groups, depth + 1);
    }

    /// <summary>选中笔迹在指定框内的主色（按框内点数分 红 / 蓝 / 其它三桶）。</summary>
    private static Color4 DominantInkColor(IReadOnlyList<Stroke> strokes, RectF area)
    {
        int rw = 0, bw = 0, ow = 0;
        float rr = 0, rg = 0, rb = 0, br = 0, bg = 0, bb = 0, or_ = 0, og = 0, ob = 0;
        foreach (var s in strokes)
        {
            int inside = 0;
            foreach (var p in s.Points)
                if (p.X >= area.MinX && p.X <= area.MaxX && p.Y >= area.MinY && p.Y <= area.MaxY)
                    inside++;
            if (inside == 0) continue;

            var c = s.Color;
            int w = inside;
            bool red = c.R > c.B * 1.25f && c.R > 0.35f;
            bool blue = c.B > c.R * 1.25f && c.B > 0.35f;
            if (red) { rw += w; rr += c.R * w; rg += c.G * w; rb += c.B * w; }
            else if (blue) { bw += w; br += c.R * w; bg += c.G * w; bb += c.B * w; }
            else { ow += w; or_ += c.R * w; og += c.G * w; ob += c.B * w; }
        }
        if (rw == 0 && bw == 0 && ow == 0) return new Color4(0.86f, 0.16f, 0.12f, 1f);
        if (rw >= bw && rw >= ow && rw > 0) return new Color4(rr / rw, rg / rw, rb / rw, 1f);
        if (bw >= rw && bw >= ow && bw > 0) return new Color4(br / bw, bg / bw, bb / bw, 1f);
        return new Color4(or_ / ow, og / ow, ob / ow, 1f);
    }

    /// <summary>
    /// 按 OCR 行框就地替换：复杂板书（双栏、行数对不上）不再整块拒绝。
    /// 只删“落在该行框里”的笔迹 + 行内短下划线，图形和框外内容保留。
    /// </summary>
    private int ApplyBboxHandwriting(HandOcrDone done, string font, out List<Stroke> glyphs)
    {
        glyphs = new List<Stroke>();
        var items = done.Result.Items;
        if (items == null || items.Count == 0) return 0;

        var pngBox = done.Region.Inflate(12f);
        var lines = new List<HandwritingReplace.TextLine>();
        var texts = new List<string>();
        int skipped = 0;

        foreach (var item in items)
        {
            if (!item.HasBox) continue;
            string raw = item.Text?.Trim();
            if (string.IsNullOrEmpty(raw)) continue;
            if (!done.IncludeFormulas && OcrText.HasMath(raw)) { skipped++; continue; }

            var box = ItemBox(item, pngBox);
            if (box.MaxX - box.MinX < 4f || box.MaxY - box.MinY < 4f) continue;

            var line = new HandwritingReplace.TextLine
            {
                Box = box,
                Color = DominantInkColor(done.Selected, box),
            };

            foreach (var s in done.Selected)
            {
                var b = s.WorldInkBounds;
                if (b.IsEmpty) continue;
                float iw = MathF.Min(b.MaxX, box.MaxX) - MathF.Max(b.MinX, box.MinX);
                float ih = MathF.Min(b.MaxY, box.MaxY) - MathF.Max(b.MinY, box.MinY);
                float area = MathF.Max(1f, (b.MaxX - b.MinX) * (b.MaxY - b.MinY));
                bool overlap = iw > 0 && ih > 0 && iw * ih / area > 0.25f;
                float cx = (b.MinX + b.MaxX) * 0.5f, cy = (b.MinY + b.MaxY) * 0.5f;
                bool center = cx >= box.MinX && cx <= box.MaxX
                              && cy >= box.MinY && cy <= box.MaxY
                              && (b.MaxX - b.MinX) <= (box.MaxX - box.MinX) * 1.8f
                              && (b.MaxY - b.MinY) <= (box.MaxY - box.MinY) * 1.8f;
                if (overlap || center) line.Strokes.Add(s);
            }

            HandwritingReplace.AddUnderlineStrokes(line, done.Selected);
            if (line.Strokes.Count == 0) continue;
            lines.Add(line);
            texts.Add(raw);
        }

        if (lines.Count == 0) return 0;
        int n = HandwritingReplace.ApplyLines(Doc, done.Selected, lines, texts, font,
                                               done.IncludeFormulas, out glyphs, out int skipped2);
        skipped += skipped2;
        if (SelfCheckMode)
            Console.WriteLine($"[手写优化/bbox] 行框 {lines.Count}，跳过公式 {skipped}，替换 {n}");
        return n;
    }

    private static float Median(List<float> values)
    {
        if (values == null || values.Count == 0) return 0f;
        var copy = new List<float>(values);
        copy.Sort();
        return copy[copy.Count / 2];
    }

    /// <summary>低位数：按比例取排序后的值，避免一行分式/上下标把字号估大。</summary>
    private static float LowerMedian(List<float> values, float percentile)
    {
        if (values == null || values.Count == 0) return 0f;
        var copy = new List<float>(values);
        copy.Sort();
        int i = (int)MathF.Round((copy.Count - 1) * Math.Clamp(percentile, 0f, 1f));
        return copy[Math.Clamp(i, 0, copy.Count - 1)];
    }

    /// <summary>选中笔迹的主色（按点数分 红 / 蓝 / 其它三桶，取占比最大的那桶）。</summary>
    private static Color4 DominantInkColor(IReadOnlyList<Stroke> strokes)
    {
        int rw = 0, bw = 0, ow = 0;
        float rr = 0, rg = 0, rb = 0, br = 0, bg = 0, bb = 0, or_ = 0, og = 0, ob = 0;
        foreach (var s in strokes)
        {
            var c = s.Color;
            int w = Math.Max(1, s.Points.Count);
            bool red = c.R > c.B * 1.25f && c.R > 0.35f;
            bool blue = c.B > c.R * 1.25f && c.B > 0.35f;
            if (red) { rw += w; rr += c.R * w; rg += c.G * w; rb += c.B * w; }
            else if (blue) { bw += w; br += c.R * w; bg += c.G * w; bb += c.B * w; }
            else { ow += w; or_ += c.R * w; og += c.G * w; ob += c.B * w; }
        }
        if (rw >= bw && rw >= ow && rw > 0) return new Color4(rr / rw, rg / rw, rb / rw, 1f);
        if (bw >= rw && bw >= ow && bw > 0) return new Color4(br / bw, bg / bw, bb / bw, 1f);
        if (ow > 0) return new Color4(or_ / ow, og / ow, ob / ow, 1f);
        return new Color4(0.86f, 0.16f, 0.12f, 1f);
    }

    /// <summary>把选中笔迹渲染成白底 PNG（OCR 接口只吃图片）。</summary>
    private byte[] RenderSelectionPng(IReadOnlyList<Stroke> selected, RectF region)
    {
        if (_windows.Count == 0) return null;
        var box = region.Inflate(12f);
        int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if (w > 4096 || h > 4096 || (long)w * h > 16_000_000) return null;

        var bgra = _windows[0].RenderStrokesToBgra(selected, box, w, h);
        if (bgra == null) return null;
        return ExportFormats.Encode("ocr.png", bgra, w, h, 0, out _);
    }

    /// <summary>模型有时把"没识别到"回成 [] / {}，不能当文字建框。</summary>
    private static bool IsEmptyOcrLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        string t = s.Trim();
        return t is "[]" or "{}" or "null" or "None";
    }

    private static string Clip(string s, int n)    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
