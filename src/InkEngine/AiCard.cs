using System.Numerics;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// AI 题卡里的一道题。**第一版只存纯文本 + Unicode 线性公式**（`x²`、`√2`、`≤`），
/// 公式不显示 LaTeX 源码；以后接 MicroTeX/KaTeX 时，这里再加 `runs` 和图片片段，
/// 题卡对象、存档、交互都不用推倒重来。
/// </summary>
internal sealed class TextBoxQuestion
{
    public string Stem = "";
    public string Answer = "";
    public string Analysis = "";
    /// <summary>题目配图（可为空）。图片由卡片持有，跟着卡片移动/缩放。</summary>
    public ImageData Figure;

    public TextBoxQuestion Clone() => new()
    {
        Stem = Stem, Answer = Answer, Analysis = Analysis, Figure = Figure,
    };
}

internal enum TextBoxRole
{
    /// <summary>普通文本框：一段可编辑文字。</summary>
    Normal = 0,
    /// <summary>AI 题卡：结构化题目、答案折叠、黑题红解析。</summary>
    AiQuestion = 1,
}

/// <summary>
/// 一个文本框的内容。它是 <see cref="StrokeKind.TextBox"/> 对象的数据，
/// 不是 UI 浮窗、也不是整张截图：宽高、字号、折叠状态、每道题都在这里，
/// 所以能保存、复制、撤销、翻页隔离，也能在换宽/换字号时重新排版。
///
/// **AI 题卡只是 Role = AiQuestion 的文本框**；普通文本框用 Role = Normal 和
/// <see cref="Text"/>。两条路共用同一套选中、改宽高、存档和操作条。
/// </summary>
/// <summary>文本框里一段显式墨色（编辑时选中改色）。Start/Length 对 <see cref="TextBoxData.Text"/>。</summary>
internal sealed class TextBoxColorSpan
{
    public int Start;
    public int Length;
    public Color4 Color;
}

internal sealed class TextBoxData
{
    public TextBoxRole Role = TextBoxRole.Normal;
    /// <summary>
    /// 只读富文本卡（OCR 结果）：用 Markdown/公式排版，但**不能进入文字编辑**。
    /// 普通文字标签 Rich=false，只排纯文本。
    /// </summary>
    public bool Rich;
    /// <summary>只读：双击/点选不进入编辑；选中、移动、缩放、删除照常。</summary>
    public bool ReadOnly;
    /// <summary>纸面背景：识别结果放到文本框时打开，避免叠在原板书上看不清。</summary>
    public bool Paper;
    /// <summary>背景填充（含 alpha，0 = 透明）。</summary>
    public Color4 Fill = new(0f, 0f, 0f, 0f);
    /// <summary>边框（含 alpha，0 = 无边框）。</summary>
    public Color4 Border = new(0f, 0f, 0f, 0f);
    /// <summary>圆角（逻辑像素，乘 Unit 后是画布像素）。</summary>
    public float Radius = 12f;
    public string Title = "";
    public string Meta = "";
    public string Text = "";
    public bool ShowAnswers;

    /// <summary>OCR/生成结果卡的整体配图（可为空）。题目卡一般用每题 Figure。</summary>
    public ImageData Figure;

    /// <summary>文本框宽度（画布像素）。</summary>
    public float Width = 560f;

    /// <summary>文本框高度（画布像素）。AI 题卡自动高度时由排版维护。</summary>
    public float Height = 120f;

    /// <summary>高度是否随内容自动增长（AI 题卡默认开；普通文本框默认关）。</summary>
    public bool AutoHeight = true;

    /// <summary>
    /// **运行时**标记：这一个文本框还在“第一次输入”里，宽度跟着文字长。
    /// 不进存档（读回来一律 false）——落盘之后尺寸就是用户看到的那个，
    /// 再打开不会因为内容量一遍又跳宽。
    /// </summary>
    public bool AutoWidth;

    /// <summary>正文字号（逻辑像素，乘 <see cref="Unit"/> 后是画布像素）。</summary>
    public float FontSize = 14.5f;

    /// <summary>普通文本框的字体档（雅黑 / 楷体 / 宋体 / 黑体）。</summary>
    public TextBoxFace Face = TextBoxFace.YaHei;

    /// <summary>行距倍数（1.0 = 紧凑，1.55 = 现有默认）。只影响普通文本框。</summary>
    public float LineSpacing = TextBoxFaces.DefaultLineSpacing;

    /// <summary>
    /// 逐行墨色（对应 <see cref="Text"/> 的硬换行行号；空 = 全部用 <see cref="Ink"/>）。
    /// 识别到文本框时按 OCR 行写入，红蓝批注能按行保留。
    /// </summary>
    public readonly List<Color4> LineColors = new();

    /// <summary>
    /// 逐字/逐段的显式墨色（编辑时选中一段改色）。Start/Length 是 <see cref="Text"/>
    /// 的字符下标；同一位置以 ColorSpans 优先于 <see cref="LineColors"/>。
    /// </summary>
    public readonly List<TextBoxColorSpan> ColorSpans = new();

    /// <summary>第 <paramref name="hardLine"/> 行的墨色；没有逐行色就退回整框主色。</summary>
    public Color4 InkForLine(int hardLine)
        => hardLine >= 0 && hardLine < LineColors.Count ? LineColors[hardLine] : Ink;

    /// <summary>某个字符的墨色：显式 span &gt; 整框主色。</summary>
    public Color4 ColorAt(int index, Color4 fallback)
    {
        for (int i = ColorSpans.Count - 1; i >= 0; i--)
        {
            var s = ColorSpans[i];
            if (index >= s.Start && index < s.Start + s.Length) return s.Color;
        }
        return fallback;
    }

    public bool HasColorSpanIn(int start, int length)
    {
        int end = start + Math.Max(0, length);
        foreach (var s in ColorSpans)
            if (s.Start < end && s.Start + s.Length > start) return true;
        return false;
    }

    /// <summary>手动改整框颜色：逐行色和逐字色都清掉，统一听 <see cref="Ink"/>。</summary>
    public void ClearCustomColors()
    {
        LineColors.Clear();
        ColorSpans.Clear();
    }

    /// <summary>写入一段显式墨色；和已有 span 重叠时先切掉旧的重叠部分。</summary>
    public void SetColorSpan(int start, int length, Color4 color)
    {
        if (length <= 0) return;
        start = Math.Max(0, start);
        int end = start + length;
        var next = new List<TextBoxColorSpan>();
        foreach (var old in ColorSpans)
        {
            int oe = old.Start + old.Length;
            if (oe <= start || old.Start >= end) { next.Add(old); continue; }
            if (old.Start < start)
                next.Add(new TextBoxColorSpan { Start = old.Start, Length = start - old.Start, Color = old.Color });
            if (oe > end)
                next.Add(new TextBoxColorSpan { Start = end, Length = oe - end, Color = old.Color });
        }
        next.Add(new TextBoxColorSpan { Start = start, Length = length, Color = color });
        next.Sort((a, b) => a.Start.CompareTo(b.Start));

        ColorSpans.Clear();
        foreach (var s in next)
        {
            if (ColorSpans.Count > 0)
            {
                var last = ColorSpans[^1];
                if (last.Start + last.Length == s.Start && SameColor(last.Color, s.Color))
                {
                    last.Length += s.Length;
                    continue;
                }
            }
            ColorSpans.Add(s);
        }
    }

    private static bool SameColor(Color4 a, Color4 b)
        => MathF.Abs(a.R - b.R) < 0.004f
           && MathF.Abs(a.G - b.G) < 0.004f
           && MathF.Abs(a.B - b.B) < 0.004f;

    /// <summary>
    /// 普通文本框的文字/公式颜色（= Stroke.Color 的副本）。
    /// 公式是位图，颜色必须在渲染时定；改颜色时同步这里并让排版重烘。
    /// AI 题卡不用它（题干黑、答案红由 Role 决定）。
    /// </summary>
    public Color4 Ink = new(0.12f, 0.14f, 0.18f, 1f);

    /// <summary>建卡时的 DPI 单位（1 = 100%）。卡片是自包含的，换机器后仍按这个比例排版。</summary>
    public float Unit = 1f;

    public readonly List<TextBoxQuestion> Questions = new();

    /// <summary>
    /// 固定高度时，内容向上滚了多少（画布像素）。0 = 顶部；只影响绘制/命中，
    /// 不改排版行本身。自动高度时恒 0。
    /// </summary>
    public float ScrollY;

    /// <summary>最近一次排版的内容总高度（可能大于 Height）。</summary>
    public float ContentHeight { get; private set; }

    public float MaxScroll => MathF.Max(0f, ContentHeight - Height);
    public bool CanScroll => !AutoHeight && MaxScroll > 0.5f;

    /// <summary>滚轮滚内容；返回是否真的动了（没溢出/到顶到底 = false，交回画布滚动）。</summary>
    public bool ScrollBy(float dy)
    {
        float max = MaxScroll;
        if (max <= 0.5f) return false;
        float next = Math.Clamp(ScrollY + dy, 0f, max);
        if (MathF.Abs(next - ScrollY) < 0.01f) return false;
        ScrollY = next;
        return true;
    }

    private void ClampScroll()
    {
        if (AutoHeight) ScrollY = 0f;
        else ScrollY = Math.Clamp(ScrollY, 0f, MaxScroll);
    }

    /// <summary>当前排版后的内容高度（画布像素）。</summary>
    public float LayoutHeight => Height;

    private List<TextBoxLine> _lines;
    private float _minWidth = -1f;

    public void InvalidateLayout()
    {
        _lines = null;
        _minWidth = -1f;
    }

    public IReadOnlyList<TextBoxLine> EnsureLayout()
    {
        if (_lines != null) return _lines;
        _lines = TextBoxLayout.Build(this, out float h);
        ContentHeight = h;
        if (AutoHeight)
        {
            // 高度 = 内容高度（空框也有一行）；不再强制 60 逻辑像素，
            // 否则小字号会凭空多出一条空白。
            Height = MathF.Max(1f, h);
            ScrollY = 0f;
        }
        else
        {
            ClampScroll();
        }
        return _lines;
    }

    /// <summary>
    /// 当前文字允许拖到的最窄宽度：取"最长不可断的 token"＋左右内边距。
    /// 中文按单字、英文按空格分词、公式按可缩小的宽度算；再小就会把字/公式切掉。
    /// 结果缓存，改文字/字号/单位时随 <see cref="InvalidateLayout"/> 失效。
    /// </summary>
    public float MinWidth
    {
        get
        {
            if (Role != TextBoxRole.Normal) return 120f * MathF.Max(0.5f, Unit);
            if (_minWidth < 0f)
            {
                var (_, min) = MeasureWidths(MathF.Max(4000f * Unit, Width * 4f));
                _minWidth = min;
            }
            return _minWidth;
        }
    }

    /// <summary>
    /// 量两件事（画布像素）：
    ///   · Natural = 完全不换行时的自然宽度（最宽那一行 + 两边内边距）；
    ///   · Min     = 最长不可断 token + 两边内边距（再窄就会切字）。
    /// 用"超宽排版"复用现成的 Markdown/公式/换行逻辑，量完恢复原状。
    /// </summary>
    internal (float Natural, float Min) MeasureWidths(float probeWidth)
    {
        if (Role != TextBoxRole.Normal)
            return (Width, Width);

        float oldW = Width, oldH = Height;
        bool oldAuto = AutoHeight;
        Width = MathF.Max(1200f, probeWidth);
        InvalidateLayout();
        var lines = TextBoxLayout.Build(this, out _);

        float u = MathF.Max(0.5f, Unit);
        float pad = 18f * u;
        float natural = 0f, minToken = MathF.Max(8f * u, FontSize * u);
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case TextBoxLineKind.Math:
                    natural = MathF.Max(natural, line.MathW + pad * 2f);
                    minToken = MathF.Max(minToken, line.MathW * 0.55f);
                    break;

                case TextBoxLineKind.Text when line.Runs != null:
                    foreach (var run in line.Runs)
                    {
                        natural = MathF.Max(natural, line.X + run.X + run.Width + pad);
                        if (run.IsMath)
                            minToken = MathF.Max(minToken, run.Width * 0.55f);
                        else
                            minToken = MathF.Max(minToken, LongestToken(run.Text, run.Size));
                    }
                    break;

                case TextBoxLineKind.Text:
                    natural = MathF.Max(natural,
                        line.X + TextBoxLayout.MeasureText(line.Text, line.Size) + pad);
                    minToken = MathF.Max(minToken, LongestToken(line.Text, line.Size));
                    break;
            }
        }

        Width = oldW;
        Height = oldH;
        AutoHeight = oldAuto;
        InvalidateLayout();

        // 最小宽 = **一个最长 token + 左右内边距**，不再固定 80：
        // 这样起始框可以窄到一个字，之后每多一个字就真的往右长一格。
        float min = MathF.Max(24f * u, minToken + pad * 2f + 4f * u);
        return (natural, min);
    }

    /// <summary>
    /// 第一次自适应：宽度按自然宽度（夹在"最长 token"和上限之间）；
    /// 高度按内容，且**一直保持自动**——普通文本框没有固定高这一态。
    /// </summary>
    internal void FitToContent(float maxWidth, float maxHeight, bool fitWidth)
    {
        if (Role != TextBoxRole.Normal) return;
        float u = MathF.Max(0.5f, Unit);
        maxWidth = MathF.Max(80f * u, maxWidth);

        var (natural, min) = MeasureWidths(MathF.Max(maxWidth * 3f, 2400f * u));
        if (fitWidth && !string.IsNullOrWhiteSpace(Text))
        {
            float lo = MathF.Min(min, maxWidth);
            Width = Math.Clamp(natural, lo, maxWidth);
        }
        else
        {
            Width = MathF.Min(Width, maxWidth);
        }

        AutoHeight = true;
        ScrollY = 0f;
        InvalidateLayout();
        _ = EnsureLayout();
        // 高度不再封顶：内容多高，框就多高；超过屏幕时滚画布，不滚框内。
        _ = maxHeight;
    }

    /// <summary>
    /// 输入中的实时自适应：宽度按自然宽长（夹在 token 最小宽和屏幕可用宽之间），
    /// 到右沿后由排版自动换行，高度继续长。高度**永远自动**，不转内部滚动。
    /// </summary>
    internal void GrowToContent(float maxWidth, float maxHeight)
    {
        if (Role != TextBoxRole.Normal) return;
        float u = MathF.Max(0.5f, Unit);
        maxWidth = MathF.Max(80f * u, maxWidth);

        var (natural, min) = MeasureWidths(MathF.Max(maxWidth * 3f, 2400f * u));
        float lo = MathF.Min(min, maxWidth);
        Width = Math.Clamp(natural, lo, maxWidth);

        AutoHeight = true;
        ScrollY = 0f;
        InvalidateLayout();
        _ = EnsureLayout();
        _ = maxHeight;
    }

    private static float LongestToken(string text, float size)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float best = 0f, word = 0f;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                best = MathF.Max(best, word);
                word = 0f;
                continue;
            }
            if (IsCjk(ch))
            {
                best = MathF.Max(best, word);
                word = 0f;
                best = MathF.Max(best, TextBoxLayout.MeasureText(ch.ToString(), size));
            }
            else
            {
                word += TextBoxLayout.MeasureText(ch.ToString(), size);
            }
        }
        return MathF.Max(best, word);
    }

    private static bool IsCjk(char ch)
        => (ch >= 0x2E80 && ch <= 0x9FFF)
           || (ch >= 0xF900 && ch <= 0xFAFF)
           || (ch >= 0xFF00 && ch <= 0xFFEF);

    public void SetWidth(float width)
    {
        Width = MathF.Max(1f, width);
        InvalidateLayout();
    }

    public void SetHeight(float height)
    {
        Height = MathF.Max(1f, height);
        AutoHeight = false;
        ScrollY = Math.Clamp(ScrollY, 0f, MathF.Max(0f, ContentHeight - Height));
        InvalidateLayout();
    }

    public void ToggleAnswers()
    {
        ShowAnswers = !ShowAnswers;
        InvalidateLayout();
    }

    public TextBoxData Clone()
    {
        var c = new TextBoxData
        {
            Role = Role, Title = Title, Meta = Meta, Text = Text,
            ShowAnswers = ShowAnswers, Width = Width, Height = Height,
            AutoHeight = AutoHeight, AutoWidth = AutoWidth, FontSize = FontSize,
            Face = Face, LineSpacing = LineSpacing, Paper = Paper,
            Rich = Rich, ReadOnly = ReadOnly,
            Fill = Fill, Border = Border, Radius = Radius,
            Unit = Unit, Ink = Ink, ScrollY = ScrollY, Figure = Figure,
        };
        foreach (var color in LineColors) c.LineColors.Add(color);
        foreach (var span in ColorSpans)
            c.ColorSpans.Add(new TextBoxColorSpan { Start = span.Start, Length = span.Length, Color = span.Color });
        foreach (var q in Questions) c.Questions.Add(q.Clone());
        return c;
    }

    /// <summary>
    /// 1A 的 mock 题卡：固定三题，题干黑、答案/解析红，答案默认折叠。
    /// 接模型以后只换这个工厂，题卡和排版一行都不用改。
    /// </summary>
    public static TextBoxData CreateMock(string prompt, float unit)
    {
        string topic = "二次函数";
        if (!string.IsNullOrEmpty(prompt))
        {
            if (prompt.Contains("一元二次方程", StringComparison.Ordinal)) topic = "一元二次方程";
            else if (prompt.Contains("函数单调性", StringComparison.Ordinal)) topic = "函数单调性";
            else if (prompt.Contains("二次函数", StringComparison.Ordinal)) topic = "二次函数";
            else if (prompt.Contains("函数", StringComparison.Ordinal)) topic = "函数";
        }

        var c = new TextBoxData
        {
            Role = TextBoxRole.AiQuestion,
            Title = $"{topic} · 中等 · 3 道",
            Meta = "AI 生成 · 示例数据，请老师核对",
            Unit = MathF.Max(0.5f, unit),
            Width = 560f * MathF.Max(0.5f, unit),
            AutoHeight = true,
            FontSize = 14.5f,
            Fill = new Color4(1f, 1f, 1f, 0.98f),
            Border = new Color4(0.86f, 0.88f, 0.91f, 1f),
        };
        c.Questions.Add(new TextBoxQuestion
        {
            Stem = "1. 已知二次函数 \\(y=x^2-4x+3\\)。\n（1）求它的对称轴和顶点坐标；\n（2）当 x 取何值时，y 随 x 增大而减小？",
            Answer = "对称轴 \\(x=2\\)，顶点 \\((2,-1)\\)；当 \\(x<2\\) 时，y 随 x 增大而减小。",
            Analysis = "配方得 y = (x − 2)² − 1，所以对称轴 x = 2、顶点 (2, −1)；开口向上，对称轴左侧递减。",
        });
        c.Questions.Add(new TextBoxQuestion
        {
            Stem = "2. 二次函数 y = −2(x + 1)² + 8 的图象与 x 轴交于 A、B 两点，求线段 AB 的长度。",
            Answer = "AB = 4。",
            Analysis = "令 y = 0，得 (x + 1)² = 4，所以 x₁ = −3、x₂ = 1，AB = 1 − (−3) = 4。",
        });
        c.Questions.Add(new TextBoxQuestion
        {
            Stem = "3. 某商店销售一种商品，每件进价 40 元。售价 60 元时每天可卖出 100 件；售价每降低 1 元，每天多卖出 10 件。求每天利润的最大值。",
            Answer = "每天利润最大为 2250 元。",
            Analysis = "设售价降低 x 元，利润 W = (20 − x)(100 + 10x) = −10(x − 5)² + 2250，所以 x = 5 时 W 最大，为 2250 元。",
        });
        c.InvalidateLayout();
        return c;
    }

    /// <summary>
    /// 普通文本框：**宽度手动、高度永远自动**（2026-11-01 定稿）。
    /// 默认字号 18（与 ClassIn 的常用档一致）；内容多高框就多高，没有内部滚动。
    /// </summary>
    public static TextBoxData CreatePlain(string text, float unit, float width, float height)
    {
        var c = new TextBoxData
        {
            Role = TextBoxRole.Normal,
            Text = text ?? "",
            Unit = MathF.Max(0.5f, unit),
            Width = MathF.Max(120f, width),
            Height = MathF.Max(60f, height),
            AutoHeight = true,
            FontSize = 18f,
        };
        c.InvalidateLayout();
        return c;
    }
}

internal enum TextBoxLineKind
{
    Text = 0,
    Toggle = 1,
    Divider = 2,
    /// <summary>独立公式行（整行 LaTeX），画的是透明位图。</summary>
    Math = 3,
    /// <summary>题目配图：整块位图，按内容宽度等比缩放。</summary>
    Image = 4,
}

internal enum TextBoxInk
{
    Black = 0,
    Red = 1,
    Muted = 2,
    Accent = 3,
}

/// <summary>排版后的一行。坐标是**卡片局部坐标**，由 Overlay 逐行画。</summary>
internal sealed class TextBoxLine
{
    public TextBoxLineKind Kind;
    public string Text = "";
    public float X, Y, Size, Height;
    public TextBoxInk Ink;
    /// <summary>逐行墨色（识别结果的红/蓝行）；null = 按 <see cref="Ink"/> / 笔迹颜色。</summary>
    public Color4? Tint;
    public bool Bold;
    public int QuestionIndex = -1;

    /// <summary>Toggle 的按钮矩形（局部坐标）；Text/Divider 不用。</summary>
    public RectF Box;
    /// <summary>独立公式行的透明位图；Math 行才有。</summary>
    public ImageData MathImage;
    public float MathW, MathH;
    /// <summary>题目配图；Image 行才有。</summary>
    public ImageData Image;
    public float ImageW, ImageH;
    /// <summary>行内混排（文字 + 公式片段）；普通文本框含 `$...$` 时才有。</summary>
    public List<TextBoxRun> Runs;
}

/// <summary>行内的一段：普通文字或公式图片。X/Y 是相对卡片内容左上角。</summary>
internal sealed class TextBoxRun
{
    public string Text = "";
    public ImageData MathImage;
    public float X, Y, Width, Height, Ascent, Descent;
    public float Size;
    public bool Bold;
    public bool Italic;
    /// <summary>这一段自己的墨色（逐字改色）；null = 跟随整行 / 文本框。</summary>
    public Color4? Tint;
    public bool IsMath => MathImage != null;
}

/// <summary>
/// 题卡排版。**第一版自己做贪心换行**，不依赖 DirectWrite 的段落排版：
/// 这样"算高度"和"画出来"用的是同一份断行结果，折叠/变宽后高度不会和内容对不上。
///
/// 公式目前按 Unicode 线性文本排；以后换成图片片段时，只需要在 <see cref="Build"/>
/// 里把某一段换成 inline 占位，行高与基线仍由这一层算。
/// </summary>
internal static class TextBoxLayout
{
    public static List<TextBoxLine> Build(TextBoxData card, out float height)
        => card.Role == TextBoxRole.AiQuestion
            ? BuildAi(card, out height)
            : card.Rich
                ? BuildRich(card, out height)
                : BuildPlainText(card, out height);

    private static List<TextBoxLine> BuildAi(TextBoxData card, out float height)
    {
        float u = MathF.Max(0.5f, card.Unit);
        float baseSize = Math.Clamp(card.FontSize, 8f, 72f) * u;
        float pad = 18f * u;
        float right = MathF.Max(pad + 40f * u, card.Width - pad);
        float textW = MathF.Max(40f * u, right - pad);
        var lines = new List<TextBoxLine>();

        float y = 14f * u;
        AddText(lines, card.Title, pad, y, baseSize * 1.1f, 24f * u, TextBoxInk.Black, bold: true);
        y += 24f * u;

        AddText(lines, card.Meta, pad, y, baseSize * 0.76f, 16f * u, TextBoxInk.Muted);
        y += 18f * u;

        AddDivider(lines, pad, right, y);
        y += 10f * u;

        for (int qi = 0; qi < card.Questions.Count; qi++)
        {
            var q = card.Questions[qi];
            AddText(lines, $"第 {qi + 1} 题", pad, y, baseSize * 0.76f, 15f * u, TextBoxInk.Muted);
            y += 17f * u;

            AddRichText(lines, q.Stem, pad, textW, baseSize, 22f * u, u, TextBoxInk.Black, ref y);
            if (q.Figure != null)
                AddFigure(lines, q.Figure, pad, textW, 360f * u, 8f * u, ref y);

            y += 4f * u;
            if (card.ShowAnswers)
            {
                AddText(lines, "答案", pad, y, baseSize * 0.76f, 15f * u, TextBoxInk.Red, bold: true);
                y += 15f * u;
                float answerSize = baseSize * 0.93f;
                AddRichText(lines, q.Answer, pad, textW, answerSize, 20f * u, u, TextBoxInk.Red, ref y);

                AddText(lines, "解析", pad, y, baseSize * 0.76f, 15f * u, TextBoxInk.Red, bold: true);
                y += 15f * u;
                AddRichText(lines, q.Analysis, pad, textW, answerSize, 20f * u, u, TextBoxInk.Red, ref y);
                y += 2f * u;
                var box = new RectF { MinX = pad, MinY = y, MaxX = pad + 96f * u, MaxY = y + 26f * u };
                lines.Add(new TextBoxLine
                {
                    Kind = TextBoxLineKind.Toggle,
                    Text = "收起答案",
                    X = box.MinX, Y = box.MinY, Size = baseSize * 0.83f, Height = 26f * u,
                    Ink = TextBoxInk.Muted, QuestionIndex = qi, Box = box,
                });
                y += 30f * u;
            }
            else
            {
                var box = new RectF { MinX = pad, MinY = y, MaxX = pad + 96f * u, MaxY = y + 26f * u };
                lines.Add(new TextBoxLine
                {
                    Kind = TextBoxLineKind.Toggle,
                    Text = "显示答案",
                    X = box.MinX, Y = box.MinY, Size = baseSize * 0.83f, Height = 26f * u,
                    Ink = TextBoxInk.Red, QuestionIndex = qi, Box = box,
                });
                y += 30f * u;
            }

            AddDivider(lines, pad, right, y);
            y += 12f * u;
        }

        y += 12f * u;
        height = y;
        return lines;
    }

    /// <summary>
    /// 普通文本框：**纯文本**。不解析 Markdown、不做行内公式。
    /// 所见即所得：编辑态看到什么，提交后就是什么。
    /// </summary>
    private static List<TextBoxLine> BuildPlainText(TextBoxData card, out float height)
    {
        float u = MathF.Max(0.5f, card.Unit);
        float pad = 18f * u;
        float size = Math.Clamp(card.FontSize, 8f, 72f) * u;
        // 给 GDI 编辑态留半个字的右边余量：RichEdit 的字面宽比 DirectWrite
        // 略宽，恰好顶到边时会把最后一个字挤到下一行。
        float textW = MathF.Max(8f * u, card.Width - pad * 2f + size * 0.5f);
        float spacing = TextBoxFaces.ClampSpacing(card.LineSpacing);
        float lineH = size * spacing;
        var lines = new List<TextBoxLine>();
        float y = pad;

        string[] hardLines = (card.Text ?? "").Replace("\r\n", "\n").Split('\n');
        int textOffset = 0;
        for (int hi = 0; hi < hardLines.Length; hi++)
        {
            string hard = hardLines[hi];
            int lineStart = textOffset;
            textOffset += hard.Length + 1;
            Color4 ink = card.InkForLine(hi);

            // 逐字颜色是纯文本唯一的“富”格式；不解析任何语法。
            if (card.HasColorSpanIn(lineStart, hard.Length))
            {
                AddColoredPlain(lines, hard, lineStart, size, u, pad, textW, lineH,
                                ink, card, ref y);
                continue;
            }

            foreach (string line in Wrap(hard, textW, size))
            {
                AddText(lines, line, pad, y, size, lineH, ink);
                y += lineH;
            }
        }

        height = MathF.Max(y + pad, pad * 2f + lineH);
        return lines;
    }

    /// <summary>纯文本的逐字颜色排版：按 ColorSpans 切 run，不碰 `$...$`。</summary>
    private static void AddColoredPlain(List<TextBoxLine> lines, string text, int textStart,
                                        float size, float u, float pad, float textW,
                                        float lineH, Color4 fallback, TextBoxData card,
                                        ref float y)
    {
        var runs = new List<TextBoxRun>();
        int i = 0;
        while (i < text.Length)
        {
            Color4 c = card.ColorAt(textStart + i, fallback);
            int j = i + 1;
            while (j < text.Length && SameInk(card.ColorAt(textStart + j, fallback), c)) j++;
            runs.Add(new TextBoxRun
            {
                Text = text[i..j], Size = size, Tint = c,
                Height = size * 1.55f, Ascent = size * 0.82f, Descent = size * 0.30f,
            });
            i = j;
        }
        if (runs.Count > 0)
            LayoutInlineRuns(lines, runs, pad, textW, size, lineH, u, ref y);
    }

    private static bool SameInk(Color4 a, Color4 b)
        => MathF.Abs(a.R - b.R) < 0.004f
           && MathF.Abs(a.G - b.G) < 0.004f
           && MathF.Abs(a.B - b.B) < 0.004f;

    private static List<TextBoxLine> BuildRich(TextBoxData card, out float height)
    {
        float u = MathF.Max(0.5f, card.Unit);
        float pad = 18f * u;
        float textW = MathF.Max(8f * u, card.Width - pad * 2f);
        float size = Math.Clamp(card.FontSize, 8f, 72f) * u;
        float spacing = TextBoxFaces.ClampSpacing(card.LineSpacing);
        float lineH = size * spacing;
        var lines = new List<TextBoxLine>();
        float y = pad;
        string[] hardLines = (card.Text ?? "").Replace("\r\n", "\n").Split('\n');
        int textOffset = 0;
        for (int hi = 0; hi < hardLines.Length; hi++)
        {
            string hard = hardLines[hi];
            int lineStart = textOffset;
            textOffset += hard.Length + 1;
            Color4 ink = card.InkForLine(hi);
            string display = hard;
            float lineSize = size;
            float lineH2 = lineH;
            bool boldAll = false;

            // Markdown 分隔线
            string t = hard.Trim();
            if (t == "---" || t == "***" || t == "___")
            {
                AddDivider(lines, pad, pad + textW, y);
                y += 12f * u;
                continue;
            }

            // Markdown 代码块：AI 常把 LaTeX 包在 ```latex ... ``` 里
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                string lang = t[3..].Trim().ToLowerInvariant();
                var body = new System.Text.StringBuilder();
                int j = hi + 1;
                while (j < hardLines.Length
                       && !hardLines[j].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    body.AppendLine(hardLines[j]);
                    j++;
                }
                string code = body.ToString().Trim();
                bool math = lang is "latex" or "tex" or "math" or "katex"
                            || (lang.Length == 0 && LooksLikeLatex(code));
                if (math)
                {
                    AddMathBlock(lines, code, size, pad, textW, ink, u, ref y);
                }
                else
                {
                    foreach (string row in code.Replace("\r\n", "\n").Split('\n'))
                    {
                        foreach (string part in Wrap(row, textW, size))
                        {
                            AddText(lines, part, pad, y, size, lineH, ink);
                            y += lineH;
                        }
                    }
                }
                hi = j;
                continue;
            }

            // 跨行块公式：\[ ... \] 或 $$ ... $$；开符号可以和公式同一行。
            string opener = null, closer = null;
            if (t.StartsWith("\\[", StringComparison.Ordinal)) { opener = "\\["; closer = "\\]"; }
            else if (t.StartsWith("$$", StringComparison.Ordinal)) { opener = "$$"; closer = "$$"; }
            if (opener != null && !TryBlockMath(t, out _))
            {
                string first = t[opener.Length..];
                int closeAt = first.IndexOf(closer, StringComparison.Ordinal);
                var sb = new System.Text.StringBuilder();
                int consumedTo = hi;
                if (closeAt >= 0)
                {
                    sb.Append(first[..closeAt]);
                }
                else
                {
                    sb.AppendLine(first);
                    int j = hi + 1;
                    bool closed = false;
                    for (; j < hardLines.Length; j++)
                    {
                        string row = hardLines[j];
                        int at = row.IndexOf(closer, StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            sb.AppendLine(row[..at]);
                            consumedTo = j;
                            closed = true;
                            break;
                        }
                        sb.AppendLine(row);
                        consumedTo = j;
                    }
                    if (!closed) { sb.Clear(); consumedTo = hi; }
                }

                if (sb.Length > 0)
                {
                    AddMathBlock(lines, sb.ToString().Trim(), size, pad, textW, ink, u, ref y);
                    hi = consumedTo;
                    continue;
                }
            }

            // Markdown 标题：### / ## / #
            int hash = 0;
            while (hash < display.Length && hash < 3 && display[hash] == '#') hash++;
            if (hash > 0 && hash < display.Length && display[hash] == ' ')
            {
                display = display[(hash + 1)..].Trim();
                float scale = hash == 1 ? 1.45f : hash == 2 ? 1.25f : 1.12f;
                lineSize = size * scale;
                lineH2 = lineSize * spacing;
                boldAll = true;
            }
            else if (display.StartsWith("- ", StringComparison.Ordinal)
                     || display.StartsWith("* ", StringComparison.Ordinal))
            {
                display = "• " + display[2..];
            }

            string trimmed = display.Trim();

            // 逐字改色：这一行有显式 span 时走彩色 run 路径（忽略 Markdown 粗体）。
            if (display == hard && card.HasColorSpanIn(lineStart, hard.Length)
                && !trimmed.StartsWith("$$", StringComparison.Ordinal)
                && !trimmed.StartsWith("\\[", StringComparison.Ordinal))
            {
                AddColoredInline(lines, display, lineStart, lineSize, u, pad, textW, lineH2,
                                 ink, card, ref y);
                continue;
            }

            if (TryBlockMath(trimmed, out string latex))
            {
                AddMathBlock(lines, latex, lineSize, pad, textW, ink, u, ref y);
                continue;
            }

            if (display.Contains('$') || display.Contains("\\(") || display.Contains("\\)")
                || display.Contains('*') || display.Contains('`') || boldAll)
            {
                var runs = ParseInline(display, lineSize, u, boldAll, ink);
                if (runs.Count > 0)
                {
                    LayoutInlineRuns(lines, runs, pad, textW, lineSize, lineH2, u, ref y, ink);
                    continue;
                }
            }

            foreach (string line in Wrap(display, textW, lineSize))
            {
                AddText(lines, line, pad, y, lineSize, lineH2, ink, boldAll);
                y += lineH2;
            }
        }
        if (card.Figure != null)
            AddFigure(lines, card.Figure, pad, card.Width - pad * 2f, 360f * u, 10f * u, ref y);
        height = MathF.Max(y + pad, pad * 2f + lineH);
        return lines;
    }

    private static List<TextBoxRun> ParseInline(string text, float size, float u, bool boldAll,
                                                Color4 ink)
    {
        // DeepSeek / ChatGPT 常输出 \(...\) 行内公式；内部统一成 $...$ 再解析。
        text = text.Replace("\\(", "$").Replace("\\)", "$");
        var runs = new List<TextBoxRun>();
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('$', i);
            if (open < 0)
            {
                AddStyledTextRuns(runs, text[i..], size, boldAll);
                break;
            }
            if (open > i) AddStyledTextRuns(runs, text[i..open], size, boldAll);

            int close = text.IndexOf('$', open + 1);
            if (close < 0 || close == open + 1)
            {
                AddStyledTextRuns(runs, text[open..], size, boldAll);
                break;
            }
            string latex = text[(open + 1)..close].Trim();
            AddMathRuns(runs, latex, size * 0.95f, ink);
            i = close + 1;
        }
        return runs;
    }

    /// <summary>
    /// 带逐字颜色的行内排版：按 `$...$` 切公式，文字段再按 ColorSpans 切成彩色 run。
    /// 只用于“这一行有显式 span”的情况，普通行仍走 <see cref="ParseInline"/>。
    /// </summary>
    private static void AddColoredInline(List<TextBoxLine> lines, string text, int textStart,
                                         float size, float u, float pad, float textW, float lineH,
                                         Color4 fallback, TextBoxData card, ref float y)
    {
        var runs = new List<TextBoxRun>();
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('$', i);
            if (open < 0)
            {
                AddColoredTextRuns(runs, text[i..], textStart + i, size, card, fallback);
                break;
            }
            if (open > i)
                AddColoredTextRuns(runs, text[i..open], textStart + i, size, card, fallback);

            int close = text.IndexOf('$', open + 1);
            if (close < 0 || close == open + 1)
            {
                AddColoredTextRuns(runs, text[open..], textStart + open, size, card, fallback);
                break;
            }
            string latex = text[(open + 1)..close].Trim();
            int colorAt = textStart + Math.Min(open + 1, text.Length - 1);
            AddMathRuns(runs, latex, size * 0.95f, card.ColorAt(colorAt, fallback));
            i = close + 1;
        }
        if (runs.Count > 0)
            LayoutInlineRuns(lines, runs, pad, textW, size, lineH, u, ref y);
    }

    private static void AddColoredTextRuns(List<TextBoxRun> runs, string text, int absStart,
                                           float size, TextBoxData card, Color4 fallback)
    {
        if (string.IsNullOrEmpty(text)) return;
        int segStart = 0;
        Color4 cur = card.ColorAt(absStart, fallback);
        for (int i = 1; i < text.Length; i++)
        {
            Color4 c = card.ColorAt(absStart + i, fallback);
            if (MathF.Abs(c.R - cur.R) < 0.004f
                && MathF.Abs(c.G - cur.G) < 0.004f
                && MathF.Abs(c.B - cur.B) < 0.004f) continue;
            AddRun(text[segStart..i], cur);
            segStart = i;
            cur = c;
        }
        AddRun(text[segStart..], cur);

        void AddRun(string value, Color4 color)
        {
            if (value.Length == 0) return;
            float w = MeasureText(value, size);
            runs.Add(new TextBoxRun
            {
                Text = value, Size = size, Width = w,
                Height = size * 1.55f, Ascent = size * 0.82f, Descent = size * 0.30f,
                Tint = color,
            });
        }
    }

    /// <summary>
    /// 公式 → 若干 run。含中文的 `\text{...}` 拆成普通文字 run，
    /// 其余按公式图；这样中文不会画成方框，又能和公式排在同一基线。
    /// </summary>
    private static void AddMathRuns(List<TextBoxRun> runs, string latex, float size, Color4 ink)
    {
        latex = LatexCompat.Normalize(latex);
        if (string.IsNullOrWhiteSpace(latex)) return;
        var segments = LatexCompat.SplitTextSegments(latex);
        if (segments.Count == 0) return;
        foreach (var (isText, value) in segments)
        {
            if (isText)
            {
                if (value.Length == 0) continue;
                float w = MeasureText(value, size);
                runs.Add(new TextBoxRun
                {
                    Text = value, Size = size, Width = w,
                    Height = size * 1.55f, Ascent = size * 0.82f, Descent = size * 0.30f,
                    Tint = ink,
                });
            }
            else
            {
                AddMathImageRun(runs, value.Trim(), size, ink);
            }
        }
    }

    private static void AddMathImageRun(List<TextBoxRun> runs, string latex, float size, Color4 ink)
    {
        if (string.IsNullOrEmpty(latex)) return;
        var math = MathRenderer.RenderImage(latex, size, ink, 2f);
        if (math == null)
        {
            AddStyledTextRuns(runs, "（公式）", size, false);
            return;
        }
        runs.Add(new TextBoxRun
        {
            MathImage = math.Image,
            Width = math.LogicalWidth,
            Height = math.LogicalHeight,
            Ascent = math.LogicalAscent,
            Descent = math.LogicalDescent,
            Size = size,
        });
    }

    /// <summary>解析 `**粗体**` / `*斜体*`；标记本身不进显示。</summary>
    private static void AddStyledTextRuns(List<TextBoxRun> runs, string text, float size, bool boldAll)
    {
        if (string.IsNullOrEmpty(text)) return;
        var sb = new System.Text.StringBuilder();
        bool bold = boldAll, italic = false;
        bool boldMark = false, italicMark = false;
        void Flush()
        {
            if (sb.Length == 0) return;
            string t = sb.ToString();
            runs.Add(new TextBoxRun
            {
                Text = t, Size = size, Bold = bold, Italic = italic,
                Width = MeasureText(t, size),
                Height = size * 1.55f, Ascent = size * 0.82f, Descent = size * 0.30f,
            });
            sb.Clear();
        }
        for (int i = 0; i < text.Length; i++)
        {
            // 行内代码/公式的 ` 号不显示；内容照常排版。
            if (text[i] == '`') { Flush(); continue; }
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                // 成对的 ** 才当粗体；落单的 ** 原样显示，避免把 2**3 吃掉。
                if (boldMark || text.IndexOf("**", i + 2, StringComparison.Ordinal) >= 0)
                {
                    Flush();
                    boldMark = !boldMark;
                    bold = boldAll ^ boldMark;
                    i++;
                    continue;
                }
                sb.Append('*');
                continue;
            }
            if (text[i] == '*'
                && !(i > 0 && char.IsDigit(text[i - 1])
                     && i + 1 < text.Length && char.IsDigit(text[i + 1]))
                && (italicMark || text.IndexOf('*', i + 1) >= 0))
            {
                Flush();
                italicMark = !italicMark;
                italic = italicMark;
                continue;
            }
            sb.Append(text[i]);
        }
        Flush();
    }

    private static void LayoutInlineRuns(List<TextBoxLine> lines, List<TextBoxRun> source,
                                         float pad, float textW, float size, float lineH,
                                         float u, ref float y, Color4? tint = null)
    {
        var current = new List<TextBoxRun>();
        float x = 0f;
        float cursorY = y;

        void Flush()
        {
            if (current.Count == 0) return;
            float asc = size * 0.82f, desc = size * 0.30f;
            foreach (var r in current)
            {
                asc = MathF.Max(asc, r.Ascent);
                desc = MathF.Max(desc, r.Descent);
            }
            float h = MathF.Max(lineH, asc + desc + 2f * u);
            foreach (var r in current) r.Y = asc - r.Ascent;
            lines.Add(new TextBoxLine
            {
                Kind = TextBoxLineKind.Text,
                X = pad, Y = cursorY, Size = size, Height = h,
                Tint = tint,
                Runs = current,
            });
            cursorY += h;
            current = new List<TextBoxRun>();
            x = 0f;
        }

        void AddRun(string text, bool bold, bool italic, Color4? tint)
        {
            if (text.Length == 0) return;
            float w = MeasureText(text, size);
            current.Add(new TextBoxRun
            {
                Text = text, Size = size, X = x - w, Width = w,
                Height = size * 1.55f, Ascent = size * 0.82f, Descent = size * 0.30f,
                Bold = bold, Italic = italic, Tint = tint,
            });
        }

        foreach (var token in source)
        {
            if (token.IsMath)
            {
                // 单个公式比整行还宽：等比缩到行宽，宁可小一点也不能漏到卡片外。
                if (token.Width > textW)
                {
                    float k = textW / token.Width;
                    token.Width = textW;
                    token.Height *= k;
                    token.Ascent *= k;
                    token.Descent *= k;
                }
                if (current.Count > 0 && x + token.Width > textW) Flush();
                token.X = x;
                current.Add(token);
                x += token.Width + 2f * u;
                continue;
            }

            // 上一行被超宽公式撑满：文字另起一行，别挤在公式后面。
            if (current.Count > 0 && x >= textW) Flush();

            int start = 0;
            for (int i = 0; i < token.Text.Length; i++)
            {
                char ch = token.Text[i];
                float cw = CharWidth(ch, size);
                bool closing = IsClosingPunct(ch);
                // 中文收尾标点不顶到下一行行首：允许它超出右边界一点点。
                bool overflow = x + cw > textW
                                && !(closing && x < textW + size * 1.2f);
                if (overflow && current.Count > 0)
                {
                    if (i > start) AddRun(token.Text[start..i], token.Bold, token.Italic, token.Tint);
                    Flush();
                    start = i;
                }
                x += cw;
            }
            if (start < token.Text.Length)
                AddRun(token.Text[start..], token.Bold, token.Italic, token.Tint);
        }
        Flush();
        y = cursorY;
    }

    /// <summary>中文/英文的收尾标点：换行时让它跟着上一行，不要孤零零出现在行首。</summary>
    private static bool IsClosingPunct(char ch)
        => "。，、；：！？）》」』】”’%,.;:!?)]}".IndexOf(ch) >= 0;

    private static bool LooksLikeLatex(string s)
        => !string.IsNullOrWhiteSpace(s)
           && (s.Contains('\\') || s.Contains("^{") || s.Contains("_{"));

    private static bool TryBlockMath(string text, out string latex)
    {
        latex = "";
        if (text.Length < 4) return false;
        if (text.StartsWith("$$", StringComparison.Ordinal) && text.EndsWith("$$", StringComparison.Ordinal))
        {
            latex = text[2..^2].Trim();
            return latex.Length > 0;
        }
        if (text.StartsWith("\\[", StringComparison.Ordinal) && text.EndsWith("\\]", StringComparison.Ordinal))
        {
            latex = text[2..^2].Trim();
            return latex.Length > 0;
        }
        return false;
    }

    /// <summary>按同一套字宽估算一段文字的宽度（行内 run 排版用）。</summary>
    public static float MeasureText(string text, float size)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float w = 0f;
        foreach (char ch in text) w += CharWidth(ch, size);
        return w;
    }

    /// <summary>AI 题卡的墨色；必须和 Overlay.AiColor 保持同一组值，公式才能和文字同色。</summary>
    private static Color4 AiInk(TextBoxInk ink) => ink switch
    {
        TextBoxInk.Red => new Color4(0.72f, 0.16f, 0.16f, 1f),
        TextBoxInk.Muted => new Color4(0.42f, 0.45f, 0.50f, 1f),
        TextBoxInk.Accent => new Color4(0.72f, 0.16f, 0.16f, 1f),
        _ => new Color4(0.12f, 0.14f, 0.18f, 1f),
    };

    /// <summary>
    /// 题卡的富文本行：支持 `$...$` / `\(...\)` 公式、`**粗体**`、`*斜体*`，
    /// 没有公式时退回普通换行。题干预设黑色，答案/解析传红色。
    /// </summary>
    private static void AddRichText(List<TextBoxLine> lines, string text, float x, float textW,
                                    float size, float lineH, float u, TextBoxInk ink, ref float y)
    {
        string[] hard = (text ?? "").Replace("\r\n", "\n").Split('\n');
        foreach (string raw in hard)
        {
            string line = raw;
            if (line.Contains('$') || line.Contains("\\(") || line.Contains("\\)")
                || line.Contains('*') || line.Contains('`'))
            {
                var runs = ParseInline(line, size, u, false, AiInk(ink));
                if (runs.Count > 0)
                {
                    int before = lines.Count;
                    LayoutInlineRuns(lines, runs, x, textW, size, lineH, u, ref y);
                    for (int i = before; i < lines.Count; i++) lines[i].Ink = ink;
                    continue;
                }
            }
            foreach (string part in Wrap(line, textW, size))
            {
                AddText(lines, part, x, y, size, lineH, ink);
                y += lineH;
            }
        }
    }

    private static void AddText(List<TextBoxLine> lines, string text, float x, float y,
                                float size, float h, TextBoxInk ink, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        lines.Add(new TextBoxLine
        {
            Kind = TextBoxLineKind.Text, Text = text, X = x, Y = y,
            Size = size, Height = h, Ink = ink, Bold = bold,
        });
    }

    /// <summary>普通文本框的逐行墨色版本：Tint 优先于 <see cref="TextBoxLine.Ink"/>。</summary>
    private static void AddText(List<TextBoxLine> lines, string text, float x, float y,
                                float size, float h, Color4 tint, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        lines.Add(new TextBoxLine
        {
            Kind = TextBoxLineKind.Text, Text = text, X = x, Y = y,
            Size = size, Height = h, Ink = TextBoxInk.Black, Tint = tint, Bold = bold,
        });
    }

    /// <summary>把一整行公式排成居中的公式图；失败时给一句提示，不露 LaTeX 源码。</summary>
    private static void AddMathBlock(List<TextBoxLine> lines, string latex, float size,
                                     float pad, float textW, Color4 ink, float u, ref float y)
    {
        latex = LatexCompat.Normalize(latex);
        if (string.IsNullOrWhiteSpace(latex)) return;
        // 纯公式：仍走单张公式图，居中。
        var segments = LatexCompat.SplitTextSegments(latex);
        bool hasText = false;
        foreach (var (isText, _) in segments) hasText |= isText;
        if (!hasText)
        {
            var math = MathRenderer.RenderImage(latex, size, ink, 2f);
            if (math == null)
            {
                Console.WriteLine("[math] 未能渲染：" + latex);
            AddText(lines, "（公式）", pad, y, size, size * 1.55f, TextBoxInk.Muted);
                y += size * 1.55f;
                return;
            }
            float mw = math.LogicalWidth, mh = math.LogicalHeight;
            if (mw > textW)
            {
                float k = textW / mw;
                mw = textW;
                mh *= k;
            }
            float mx = pad + MathF.Max(0f, (textW - mw) * 0.5f);
            lines.Add(new TextBoxLine
            {
                Kind = TextBoxLineKind.Math,
                X = mx, Y = y, Size = size, Height = mh,
                MathImage = math.Image,
                MathW = mw,
                MathH = mh,
            });
            y += mh + 8f * u;
            return;
        }

        // 公式里混了中文（\text{当 }x>0\text{ 时}）：拆成文字 + 公式 run，居中排一行。
        var runs = new List<TextBoxRun>();
        AddMathRuns(runs, latex, size, ink);
        if (runs.Count == 0)
        {
            Console.WriteLine("[math] 未能渲染：" + latex);
            AddText(lines, "（公式）", pad, y, size, size * 1.55f, TextBoxInk.Muted);
            y += size * 1.55f;
            return;
        }

        float total = 0f, asc = size * 0.82f, desc = size * 0.30f;
        foreach (var r in runs)
        {
            total += r.Width + (r.IsMath ? 2f * u : 0f);
            asc = MathF.Max(asc, r.Ascent);
            desc = MathF.Max(desc, r.Descent);
        }
        if (total > textW)
        {
            LayoutInlineRuns(lines, runs, pad, textW, size, size * 1.55f, u, ref y);
            y += 8f * u;
            return;
        }

        float h = MathF.Max(size * 1.55f, asc + desc + 2f * u);
        float x0 = pad + (textW - total) * 0.5f;
        float x = 0f;
        foreach (var r in runs)
        {
            r.X = x;
            r.Y = asc - r.Ascent;
            x += r.Width + (r.IsMath ? 2f * u : 0f);
        }
        lines.Add(new TextBoxLine
        {
            Kind = TextBoxLineKind.Text,
            X = x0, Y = y, Size = size, Height = h,
            Runs = runs,
        });
        y += h + 8f * u;
    }

    private static void AddDivider(List<TextBoxLine> lines, float x0, float x1, float y)
    {
        lines.Add(new TextBoxLine
        {
            Kind = TextBoxLineKind.Divider,
            X = x0, Y = y, Size = 1f, Height = 1f,
            Box = new RectF { MinX = x0, MinY = y, MaxX = x1, MaxY = y + 1f },
        });
    }

    /// <summary>题目配图：按内容宽度等比缩放、居中；图片高度计入卡片自动高度。</summary>
    private static void AddFigure(List<TextBoxLine> lines, ImageData img, float x, float maxW,
                                  float maxH, float gap, ref float y)
    {
        if (img == null || img.Width <= 0 || img.Height <= 0) return;
        float scale = MathF.Min(1f, maxW / img.Width);
        if (maxH > 0f) scale = MathF.Min(scale, maxH / img.Height);
        if (scale <= 0.001f) return;
        float w = MathF.Max(1f, img.Width * scale);
        float h = MathF.Max(1f, img.Height * scale);
        lines.Add(new TextBoxLine
        {
            Kind = TextBoxLineKind.Image,
            X = x + MathF.Max(0f, (maxW - w) * 0.5f), Y = y,
            Size = 1f, Height = h,
            Image = img, ImageW = w, ImageH = h,
        });
        y += h + gap;
    }

    /// <summary>
    /// 贪心换行。宽度按"常用中文字宽 1em、ASCII 约 0.56em"估算——
    /// 1A 只是为了让高度和内容对得上；接 DirectWrite 精确测量以后再替换这一层。
    /// </summary>
    public static List<string> Wrap(string text, float maxWidth, float fontSize)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }

        foreach (string hard in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (hard.Length == 0) { result.Add(""); continue; }
            var sb = new System.Text.StringBuilder();
            float w = 0f;
            foreach (char ch in hard)
            {
                float cw = CharWidth(ch, fontSize);
                if (w + cw > maxWidth && sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    w = 0f;
                }
                sb.Append(ch);
                w += cw;
            }
            result.Add(sb.ToString());
        }
        return result;
    }

    private static float CharWidth(char ch, float size)
    {
        if (ch == '\t') return size * 1.2f;
        if (ch == ' ') return size * 0.32f;
        // 全角/CJK/中文标点：按一个字宽算；ASCII 按 0.56 字宽，宁宽勿窄（避免画出去）。
        if (ch >= 0x2E80 || (ch >= 0x3000 && ch <= 0x303F) || (ch >= 0xFF00 && ch <= 0xFFEF))
            return size;
        return size * 0.56f;
    }
}

/// <summary>
/// 题卡交互：在卡片上按下时优先于画布（拖动、改宽、折叠答案、删除），
/// 但卡片本身仍是文档对象——选中、对象橡皮、撤销、保存、翻页隔离都走文档那一套。
/// </summary>
public partial class InkEngine
{
    internal string AiPromptText = "给初二出三道二次函数题，中等难度，带答案";

    private IntPtr _aiPromptHwnd;
    private Native.WndProcDelegate _aiPromptWndProc;
    private IntPtr _aiPromptOldProc;
    private bool _aiPromptEditing;
    private bool _aiPromptHadNoActivate;

    private bool _aiCardCapturing;

    internal bool AiPromptEditing => _aiPromptEditing;

    // ---- 插入 ---------------------------------------------------------------

    internal void InsertMockTextBoxFromUi()
    {
        HideAiPromptEdit();
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.75f, DpiScale);
        var card = TextBoxData.CreateMock(AiPromptText, u);
        float screenW = screen.MaxX - screen.MinX;
        float maxW = MathF.Max(240f * u, MathF.Min(screenW * 0.8f, screenW - 32f * u));
        float minW = MathF.Min(360f * u, maxW);
        card.SetWidth(Math.Clamp(560f * u, minW, maxW));
        _ = card.EnsureLayout();

        float cx = (screen.MinX + screen.MaxX) * 0.5f;
        float cy = (screen.MinY + screen.MaxY) * 0.5f - ViewOffsetY;
        float x = cx - card.Width * 0.5f;
        float y = cy - card.LayoutHeight * 0.5f;
        x = Math.Clamp(x, screen.MinX + 16f * u,
            MathF.Max(screen.MinX + 16f * u, screen.MaxX - card.Width - 16f * u));
        y = Math.Clamp(y, screen.MinY - ViewOffsetY + 16f * u,
            MathF.Max(screen.MinY - ViewOffsetY + 16f * u,
                       screen.MaxY - ViewOffsetY - card.LayoutHeight - 16f * u));

        var s = new Stroke
        {
            // 文本框/题卡的对象身份看 Kind=TextBox，不看当前手里拿什么工具；
            // 写 Marquee 避免把新加的 Tool.Text 值带进存档（旧程序不认识 32）。
            Tool = Tool.Marquee,
            Kind = StrokeKind.TextBox,
            TextBox = card,
            Color = new Color4(0.12f, 0.14f, 0.18f, 1f),
            Width = 0f,
        };
        s.SetPoints(new[]
        {
            new Vector2(x, y),
            new Vector2(x + card.Width, y + card.LayoutHeight),
        });
        Doc.AddStroke(s);
        SetInkStatus("已插入一张 AI 示例题卡（1A）");
        _dirty = true;
        NotifyUiStateChanged();
    }

    private sealed class AiPlotDone
    {
        public bool Ok;
        public string Error = "";
        public ImageData Image;
        public PlotSpec Spec;
        public double Seconds;
    }

    private volatile AiPlotDone _aiPlotDone;
    private volatile bool _aiPlotBusy;

    /// <summary>
    /// AI 作图（单解析式 v1）：后台调用模型解析表达式 + 离屏画图，
    /// 完成后再在主线程插入 Image 对象；不在笔迹路径上做任何工作。
    /// </summary>
    internal void InsertAiPlotFromUi(string prompt)
    {
        HideAiPromptEdit();
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        if (_aiPlotBusy)
        {
            ShowToast("AI 正在作图，请稍等");
            return;
        }
        var cfg = OcrClient.LoadConfig();
        if (!cfg.IsConfigured)
        {
            ShowToast("AI 未配置：先设置 API Key/模型");
            return;
        }
        _aiPlotBusy = true;
        ShowToast("AI 作图中…", 3000);
        _ = Task.Run(async () =>
        {
            var r = await AiPlotClient.ParseAsync(prompt, cfg, CancellationToken.None)
                                 .ConfigureAwait(false);
            bool vector = r.Ok && r.Spec != null
                          && r.Spec.Kind is "functions" or "conic";
            ImageData img = null;
            if (r.Ok && r.Spec != null && !vector) img = PlotRenderer.Render(r.Spec);
            var done = new AiPlotDone
            {
                Ok = r.Ok && (vector || img != null),
                Error = r.Ok
                    ? (vector || img != null ? "" : "没有可渲染的数据")
                    : r.Error,
                Image = img,
                Spec = r.Spec,
                Seconds = r.Seconds,
            };
            Interlocked.Exchange(ref _aiPlotDone, done);
        });
    }

    /// <summary>主循环调用：把后台作好的图插到画布（只在主线程做一次 AddStroke）。</summary>
    internal void StepAiPlot()
    {
        var done = Interlocked.Exchange(ref _aiPlotDone, null);
        if (done == null) return;
        _aiPlotBusy = false;
        bool vector = done.Ok && done.Spec != null
                      && done.Spec.Kind is "functions" or "conic";
        if (!done.Ok || (!vector && done.Image == null))
        {
            ShowToast("AI 作图失败：" + ClipAiPlot(done.Error, 40), 3500);
            return;
        }

        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.75f, DpiScale);
        float maxW = MathF.Max(320f * u, (screen.MaxX - screen.MinX) * 0.82f);
        float maxH = MathF.Max(240f * u, (screen.MaxY - screen.MinY) * 0.82f);
        float localW = vector ? 1100f : done.Image.Width;
        float localH = vector ? 760f : done.Image.Height;
        float scale = MathF.Min(1f, MathF.Min(maxW / localW, maxH / localH));
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = vector ? StrokeKind.Figure : StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = vector ? null : done.Image,
            Figure = vector ? done.Spec : null,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(localW, localH),
        });
        float w = localW * scale, h = localH * scale;
        float x = screen.MinX + (screen.MaxX - screen.MinX - w) * 0.5f;
        float y = screen.MinY + (screen.MaxY - screen.MinY - h) * 0.5f - ViewOffsetY;
        s.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(s);
        Doc.SelectOnly(new[] { s });
        SetInkStatus($"AI 作图完成：{done.Spec?.Title ?? ""}");
        ShowToast($"AI 作图完成（{done.Seconds:F1}s）");
        _dirty = true;
        NotifyUiStateChanged();
    }

    private static string ClipAiPlot(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    // ---- 命中 / 拖动 --------------------------------------------------------

    private Stroke TextBoxAt(float screenX, float screenY)
    {
        if (!Doc.HasTextBoxs) return null;
        float cx = screenX, cy = screenY - ViewOffsetY;
        for (int i = Doc.Strokes.Count - 1; i >= 0; i--)
        {
            var s = Doc.Strokes[i];
            if (s.Kind != StrokeKind.TextBox || s.TextBox == null) continue;
            if (s.HitTestExact(cx, cy)) return s;
        }
        return null;
    }

    internal bool TextBoxContains(float screenX, float screenY) => TextBoxAt(screenX, screenY) != null;

    /// <summary>
    /// 「选择」工具下的题卡命中。返回 true = 这一下归题卡（调用方负责 SetCapture）；
    /// 返回 false = 交给标准选择手势（框内拖动 / 点选）。
    ///
    /// **卡片不再自己拖整卡**：移动、删除、撤销都走现有选中框那一套，只有
    /// 三个卡片专属动作留在这里——右上角 ✕、显示/收起答案、右边缘改宽。
    /// </summary>
    internal bool TextBoxPointerDown(float screenX, float screenY)
    {
        var s = TextBoxAt(screenX, screenY);
        if (s == null) return false;

        float cx = screenX, cy = screenY - ViewOffsetY;

        // **先按 z 序问一句**：卡片是填充矩形，但它上面的墨迹/图形/另一张卡才是
        // 这一下真正点到的东西。这里如果不让位，就会出现"墨迹明明在卡上面，
        // 却永远选不中墨迹"——2026-10-30 用户实测报的就是这个。
        var top = Doc.HitObjectAt(cx, cy, ClickToleranceLogical * DpiScale);
        if (top != null && !ReferenceEquals(top, s)) return false;

        // 没选中 = 先选中，再把这一下交回标准选择手势（单击选中、按住可顺势拖动）。
        bool single = Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], s);
        if (!single)
        {
            Doc.SelectOnly(new[] { s });
            _dirty = true;
            NotifyUiStateChanged();
            return false;
        }

        // 锁定语义和普通对象一致：能选中、能看，但文本框专属动作（✕/折叠）不动它。
        if (s.Locked) return false;

        // 普通文本框只有文字，没有 ✕/显示答案这些卡片 chrome。
        // 先把指针换成卡片内容坐标：滚动条和题卡按钮都用它。
        Vector2 local = new(cx, cy);
        if (!s.Transform.IsIdentity && Matrix3x2.Invert(s.Transform, out var inv))
            local = Vector2.Transform(local, inv);
        if (s.Points.Count > 0) local -= new Vector2(s.Points[0].X, s.Points[0].Y);

        var card = s.TextBox;
        var lines = card.EnsureLayout();

        // 右上角 ✕：删整卡，一步撤销（先于滚动条，避免抢掉关闭）。
        if (card.Role == TextBoxRole.AiQuestion)
        {
            var close = new RectF
            {
                MinX = card.Width - 42f * card.Unit, MinY = 6f * card.Unit,
                MaxX = card.Width - 8f * card.Unit, MaxY = 40f * card.Unit,
            };
            if (close.Contains(local.X, local.Y))
            {
                Doc.RemoveStrokeWithUndo(s);
                _dirty = true;
                NotifyUiStateChanged();
                return true;
            }
        }

        // 右侧滚动条：按下即进入拖动（不用滚轮也能滚）。
        if (TryStartTextBoxScrollDrag(s, card, local, cy))
            return true;

        if (card.Role != TextBoxRole.AiQuestion)
        {
            // 选择工具下也遵守"第一下选中/可拖，双击松手才编辑"：
            // 这里只 arm，返回 false 让标准选择手势接住这一下。
            ArmTextBoxEditOnDoubleClick(s, screenX, screenY);
            return false;
        }

        // 每道题的「显示答案 / 收起答案」。
        foreach (var line in lines)
        {
            if (line.Kind != TextBoxLineKind.Toggle) continue;
            if (!line.Box.Contains(local.X, local.Y + card.ScrollY)) continue;
            var before = s.PaddedBounds;
            card.ToggleAnswers();
            s.SyncTextBoxBounds();
            ClampTextBoxIntoView(s);
            Doc.RefreshStrokeGrid(s);
            Doc.Dirty.Add(before);
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            _dirty = true;
            NotifyUiStateChanged();
            return true;
        }

        // 卡片正文：不抢，交给标准选中框的"框内拖动 / 8 手柄改宽高"。
        return false;
    }

    private bool _textBoxScrollDragging;
    private Stroke _textBoxScrollStroke;
    private float _textBoxScrollStartY, _textBoxScrollStartScroll;
    private float _textBoxScrollTrackH, _textBoxScrollThumbH;

    private bool TryStartTextBoxScrollDrag(Stroke s, TextBoxData card, Vector2 local, float canvasY)
    {
        if (card == null || card.MaxScroll <= 0.5f) return false;
        float u = MathF.Max(0.5f, card.Unit);
        // ?????????????????????
        if (local.X < card.Width - 30f * u || local.X > card.Width - 12f * u) return false;
        float top = 18f * u;
        float bottom = card.Height - 18f * u;
        if (local.Y < top || local.Y > bottom) return false;
        if (card.Role == TextBoxRole.AiQuestion && local.Y < 44f * u) return false;

        float trackH = MathF.Max(8f, bottom - top);
        float thumbH = MathF.Max(20f * u, trackH * card.Height / MathF.Max(1f, card.ContentHeight));
        _textBoxScrollDragging = true;
        _textBoxScrollStroke = s;
        _textBoxScrollStartY = canvasY;
        _textBoxScrollStartScroll = card.ScrollY;
        _textBoxScrollTrackH = trackH;
        _textBoxScrollThumbH = MathF.Min(thumbH, trackH);
        return true;
    }

    private void UpdateTextBoxScrollDrag(float canvasY)
    {
        var s = _textBoxScrollStroke;
        var card = s?.TextBox;
        if (card == null) return;
        float range = MathF.Max(1f, _textBoxScrollTrackH - _textBoxScrollThumbH);
        float target = _textBoxScrollStartScroll
                     + (canvasY - _textBoxScrollStartY) / range * card.MaxScroll;
        target = Math.Clamp(target, 0f, card.MaxScroll);
        if (MathF.Abs(target - card.ScrollY) < 0.1f) return;
        card.ScrollY = target;
        Doc.Dirty.Add(s.PaddedBounds);
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
    }

    internal void TextBoxPointerUp()
    {
        _aiCardCapturing = false;
        _textBoxScrollDragging = false;
        _textBoxScrollStroke = null;
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>自检 / 摆样：展开或收起所有答案。</summary>
    internal void ToggleTextBoxAnswersForTest()
    {
        foreach (var s in Doc.Strokes)
        {
            if (s.Kind != StrokeKind.TextBox || s.TextBox == null) continue;
            var old = s.PaddedBounds;
            s.TextBox.ToggleAnswers();
            s.SyncTextBoxBounds();
            ClampTextBoxIntoView(s);
            Doc.RefreshStrokeGrid(s);
            Doc.Dirty.Add(old);
            Doc.Dirty.Add(s.PaddedBounds);
        }
        Doc.StructureChangedSinceRender = true;
        Doc.Version++;
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>卡片变高/变宽后如果超出当前视野，把它整体挪回来（设计：答案展开不能顶到工具条外）。</summary>
    private void ClampTextBoxIntoView(Stroke s)
    {
        if (s.Kind != StrokeKind.TextBox || s.TextBox == null) return;
        var screen = ScreenRectPhysical();
        float u = MathF.Max(0.75f, DpiScale);
        float top = screen.MinY - ViewOffsetY + 16f * u;
        bool normal = s.TextBox.Role == TextBoxRole.Normal;
        // 通用移动手势会把平移记在 Transform 上；普通文本先并进 Points，
        // 下面按画布坐标夹取才是准的（不并的话移动过的框一夹就漂）。
        if (normal) BakeTextBoxTranslation(s);
        // 底部留出工具条那一条（120 逻辑像素）：题卡展开后不能压住工具条。
        // **普通文本框不封底**：长文本高了就高，画布自己滚（用户 2026-11-01 定）。
        float bottom = screen.MaxY - ViewOffsetY - 120f * u;
        var b = s.PaddedBounds;
        float dy = 0f, dx = 0f;
        if (!normal && b.MaxY > bottom) dy = bottom - b.MaxY;
        if (b.MinY + dy < top) dy = top - b.MinY;

        float left = screen.MinX + 16f * u;
        float right = screen.MaxX - 16f * u;
        // 宽度不能超过屏幕左右沿；普通文本框在输入/改宽那边已经收过一次，
        // 这里兜底处理老存档和载入的宽框。
        if (normal && s.TextBox.Width > right - left)
        {
            s.TextBox.Width = right - left;
            s.TextBox.InvalidateLayout();
            _ = s.TextBox.EnsureLayout();
            s.SetTextBoxRect(s.Points.Count > 0
                ? new Vector2(s.Points[0].X, s.Points[0].Y) : Vector2.Zero,
                s.TextBox.Width, s.TextBox.Height);
            b = s.PaddedBounds;
        }
        if (b.MaxX > right) dx = right - b.MaxX;
        if (b.MinX + dx < left) dx = left - b.MinX;
        if (MathF.Abs(dx) > 0.5f || MathF.Abs(dy) > 0.5f)
        {
            if (normal && s.Points.Count > 0)
            {
                var p0 = s.Points[0];
                s.SetTextBoxRect(new Vector2(p0.X + dx, p0.Y + dy),
                                 s.TextBox.Width, s.TextBox.Height);
            }
            else
            {
                s.Transform = Matrix3x2.CreateTranslation(dx, dy) * s.Transform;
            }
        }
    }

    internal int TextBoxCountForTest
    {
        get
        {
            int n = 0;
            foreach (var s in Doc.Strokes)
                if (s.Kind == StrokeKind.TextBox) n++;
            return n;
        }
    }

    // ---- 文字输入（原生 EDIT，中文/输入法交给系统）-------------------------

    internal void BeginAiPromptEditFromUi(float x, float y, float w, float h)
    {
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        EnsureAiPromptWindow();
        if (_aiPromptHwnd == IntPtr.Zero) return;

        Native.SetWindowTextW(_aiPromptHwnd, AiPromptText ?? "");
        var font = EnsureLibraryRenameFont(UiUnit);
        Native.SendMessage(_aiPromptHwnd, Native.WM_SETFONT, font, new IntPtr(1));
        int margin = Math.Max(2, (int)MathF.Round(6f * UiUnit));
        Native.SendMessage(_aiPromptHwnd, Native.EM_SETMARGINS,
                           new IntPtr(Native.EC_LEFTMARGIN | Native.EC_RIGHTMARGIN),
                           new IntPtr(margin | (margin << 16)));
        Native.SendMessage(_aiPromptHwnd, Native.EM_SETLIMITTEXT, new IntPtr(300), IntPtr.Zero);
        Native.SendMessage(_aiPromptHwnd, Native.EM_SETSEL, IntPtr.Zero, new IntPtr(-1));

        int px = (int)MathF.Round(x * UiUnit);
        int py = (int)MathF.Round(y * UiUnit);
        int pw = Math.Max(80, (int)MathF.Round(w * UiUnit));
        int ph = Math.Max(24, (int)MathF.Round(h * UiUnit));
        Native.SetWindowPos(_aiPromptHwnd, Native.HWND_TOPMOST, px, py, pw, ph,
                            Native.SWP_SHOWWINDOW);
        FocusAiPromptWindow();
        _aiPromptEditing = true;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void EndAiPromptEditFromUi(bool accept)
    {
        if (!_aiPromptEditing) return;
        string text = AiPromptText ?? "";
        if (accept && _aiPromptHwnd != IntPtr.Zero)
        {
            var sb = new System.Text.StringBuilder(512);
            Native.GetWindowTextW(_aiPromptHwnd, sb, sb.Capacity);
            string t = sb.ToString().Trim();
            if (t.Length > 0) text = t;
        }
        AiPromptText = text;
        HideAiPromptEdit();
        _dirty = true;
        NotifyUiStateChanged();
    }

    private void HideAiPromptEdit()
    {
        bool was = _aiPromptEditing;
        _aiPromptEditing = false;
        if (_aiPromptHwnd != IntPtr.Zero)
            Native.ShowWindow(_aiPromptHwnd, Native.SW_HIDE);
        RestoreAiPromptFocus();
        if (was) { _dirty = true; NotifyUiStateChanged(); }
    }

    private void EnsureAiPromptWindow()
    {
        if (_aiPromptHwnd != IntPtr.Zero) return;
        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW;
        _aiPromptHwnd = Native.CreateWindowEx(exStyle, "EDIT", "InkTeachAiPrompt",
            0x80000000L /*WS_POPUP*/ | 0x00800000L /*WS_BORDER*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_aiPromptHwnd == IntPtr.Zero)
        {
            Console.WriteLine("AI 输入框创建失败: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return;
        }
        _aiPromptWndProc = AiPromptWndProc;
        _aiPromptOldProc = Native.SetWindowLongPtr(
            _aiPromptHwnd, Native.GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_aiPromptWndProc));
    }

    private IntPtr AiPromptWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_KEYDOWN:
            {
                int vk = (int)wParam.ToInt64();
                if (vk == 0x0D) { EndAiPromptEditFromUi(true); return IntPtr.Zero; }
                if (vk == 0x1B) { EndAiPromptEditFromUi(false); return IntPtr.Zero; }
                break;
            }
            case Native.WM_CHAR:
                if ((int)wParam.ToInt64() == 0x0D || (int)wParam.ToInt64() == 0x1B)
                    return IntPtr.Zero;
                break;
            case Native.WM_KILLFOCUS:
                if (_aiPromptEditing) EndAiPromptEditFromUi(true);
                return IntPtr.Zero;
            case Native.WM_GETDLGCODE:
                return new IntPtr(Native.DLGC_WANTALLKEYS);
        }
        return Native.CallWindowProc(_aiPromptOldProc, hWnd, msg, wParam, lParam);
    }

    private void FocusAiPromptWindow()
    {
        IntPtr main = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        _aiPromptHadNoActivate = false;
        if (main != IntPtr.Zero)
        {
            long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_NOACTIVATE) != 0)
            {
                _aiPromptHadNoActivate = true;
                Native.SetWindowLongPtr(main, Native.GWL_EXSTYLE,
                    new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
                Native.SetWindowPos(main, IntPtr.Zero, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            }
        }
        IntPtr fg = Native.GetForegroundWindow();
        uint fgTid = Native.GetWindowThreadProcessId(fg, out _);
        uint myTid = Native.GetCurrentThreadId();
        if (fgTid != 0 && fgTid != myTid)
        {
            Native.AttachThreadInput(myTid, fgTid, true);
            Native.SetForegroundWindow(_aiPromptHwnd);
            Native.SetFocus(_aiPromptHwnd);
            Native.AttachThreadInput(myTid, fgTid, false);
        }
        else
        {
            Native.SetForegroundWindow(_aiPromptHwnd);
            Native.SetFocus(_aiPromptHwnd);
        }
        Native.SetWindowPos(_aiPromptHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void RestoreAiPromptFocus()
    {
        bool had = _aiPromptHadNoActivate;
        _aiPromptHadNoActivate = false;
        if (had && _windows.Count > 0)
        {
            IntPtr main = _windows[0].Hwnd;
            long ex = Native.GetWindowLongPtr(main, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(main, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            Native.SetWindowPos(main, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        if (KeyboardMode && _windows.Count > 0) Native.SetFocus(_windows[0].Hwnd);
    }
}
