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
    private enum HandOcrMode { Handwriting, TextBox }

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

    private void StartHandOcr(HandOcrMode mode, bool includeFormulas)
    {
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
            SetHandOcrStatus("先框选要优化的文字笔迹");
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

        // 「识别到文本框」只需要文字：把图形长线从 OCR 图里剔掉（公式分式线一般 <350）。
        if (mode == HandOcrMode.TextBox)
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
        if (string.IsNullOrEmpty(font))
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
            : includeFormulas ? "识别中…（含公式）" : "识别中…（约 2–5 秒）");

        _handOcrCts?.Cancel();
        _handOcrCts = new CancellationTokenSource();
        int gen = ++_handOcrGeneration;
        var ct = _handOcrCts.Token;

        Task.Run(async () =>
        {
            var result = await OcrClient.RecognizeAsync(png, cfg,
                                        mode == HandOcrMode.TextBox
                                            ? OcrClient.TextBoxPrompt
                                            : OcrClient.BoardPrompt, ct)
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

        if (done.Mode == HandOcrMode.TextBox)
        {
            string text = string.Join("\n", done.Result.Lines);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetHandOcrStatus("识别结果为空");
                return;
            }
            var box = CreateRecognizedTextBox(text, done.InkColor);
            if (box == null)
            {
                SetHandOcrStatus("创建文本框失败");
                return;
            }
            Doc.SelectOnly(new[] { box });
            Console.WriteLine($"[识别到文本框] {done.Result.Lines.Length} 行，{done.Result.Seconds:F1}s");
            SetHandOcrStatus($"已识别 {done.Result.Lines.Length} 行 → 文本框");
            return;
        }

        // 先做一次切行对齐检查：OCR 行数和几何行数差太多，说明这一页是
        // 双栏 / 公式碎片 / 图形混排，硬替换会把整行搬到错误位置。
        var detected = HandwritingReplace.DetectLines(done.Selected, done.Region,
                                                      done.Result.Lines.Length);
        if (detected.Count != done.Result.Lines.Length)
        {
            Console.WriteLine($"[手写优化] 拒绝对齐：几何 {detected.Count} 行 / OCR {done.Result.Lines.Length} 行");
            SetHandOcrStatus($"排版复杂（检测 {detected.Count} / 识别 {done.Result.Lines.Length} 行），请只框选文字");
            return;
        }

        string font = GlyphText.BundledFontPath;
        if (string.IsNullOrEmpty(font))
        {
            SetHandOcrStatus("缺少手写字体文件");
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

    /// <summary>把识别结果变成一个普通文本框；纸面背景 + 原墨迹主色。</summary>
    private Stroke CreateRecognizedTextBox(string text, Color4 ink)
    {
        var s = CreatePlainTextBox(text);
        if (s?.TextBox == null) return null;
        s.TextBox.Paper = true;
        s.Color = ink;
        s.TextBox.Ink = ink;
        return s;
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

    private static string Clip(string s, int n)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
