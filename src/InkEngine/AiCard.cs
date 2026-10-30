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

    public TextBoxQuestion Clone() => new()
    {
        Stem = Stem, Answer = Answer, Analysis = Analysis,
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
internal sealed class TextBoxData
{
    public TextBoxRole Role = TextBoxRole.Normal;
    public string Title = "";
    public string Meta = "";
    public string Text = "";
    public bool ShowAnswers;

    /// <summary>文本框宽度（画布像素）。</summary>
    public float Width = 560f;

    /// <summary>文本框高度（画布像素）。AI 题卡自动高度时由排版维护。</summary>
    public float Height = 120f;

    /// <summary>高度是否随内容自动增长（AI 题卡默认开；普通文本框默认关）。</summary>
    public bool AutoHeight = true;

    /// <summary>正文字号（逻辑像素，乘 <see cref="Unit"/> 后是画布像素）。</summary>
    public float FontSize = 14.5f;

    /// <summary>建卡时的 DPI 单位（1 = 100%）。卡片是自包含的，换机器后仍按这个比例排版。</summary>
    public float Unit = 1f;

    public readonly List<TextBoxQuestion> Questions = new();

    /// <summary>当前排版后的内容高度（画布像素）。</summary>
    public float LayoutHeight => Height;

    private List<TextBoxLine> _lines;

    public void InvalidateLayout()
    {
        _lines = null;
    }

    public IReadOnlyList<TextBoxLine> EnsureLayout()
    {
        if (_lines != null) return _lines;
        _lines = TextBoxLayout.Build(this, out float h);
        if (AutoHeight) Height = MathF.Max(1f, h);
        return _lines;
    }

    public void SetWidth(float width)
    {
        Width = MathF.Max(1f, width);
        InvalidateLayout();
    }

    public void SetHeight(float height)
    {
        Height = MathF.Max(1f, height);
        AutoHeight = false;
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
            AutoHeight = AutoHeight, FontSize = FontSize, Unit = Unit,
        };
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
        };
        c.Questions.Add(new TextBoxQuestion
        {
            Stem = "1. 已知二次函数 y = x² − 4x + 3。\n（1）求它的对称轴和顶点坐标；\n（2）当 x 取何值时，y 随 x 增大而减小？",
            Answer = "对称轴 x = 2，顶点 (2, −1)；当 x < 2 时，y 随 x 增大而减小。",
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

    /// <summary>普通文本框：固定宽高、默认不自动高度；内容超出时先裁切（滚动后续）。</summary>
    public static TextBoxData CreatePlain(string text, float unit, float width, float height)
    {
        var c = new TextBoxData
        {
            Role = TextBoxRole.Normal,
            Text = text ?? "",
            Unit = MathF.Max(0.5f, unit),
            Width = MathF.Max(120f, width),
            Height = MathF.Max(60f, height),
            AutoHeight = false,
            FontSize = 15f,
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
    public bool Bold;
    public int QuestionIndex = -1;

    /// <summary>Toggle 的按钮矩形（局部坐标）；Text/Divider 不用。</summary>
    public RectF Box;
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
            : BuildPlain(card, out height);

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

            foreach (string line in Wrap(q.Stem, textW, baseSize))
            {
                AddText(lines, line, pad, y, baseSize, 22f * u, TextBoxInk.Black);
                y += 22f * u;
            }

            y += 4f * u;
            if (card.ShowAnswers)
            {
                AddText(lines, "答案", pad, y, baseSize * 0.76f, 15f * u, TextBoxInk.Red, bold: true);
                y += 15f * u;
                float answerSize = baseSize * 0.93f;
                foreach (string line in Wrap(q.Answer, textW, answerSize))
                {
                    AddText(lines, line, pad, y, answerSize, 20f * u, TextBoxInk.Red);
                    y += 20f * u;
                }

                AddText(lines, "解析", pad, y, baseSize * 0.76f, 15f * u, TextBoxInk.Red, bold: true);
                y += 15f * u;
                foreach (string line in Wrap(q.Analysis, textW, answerSize))
                {
                    AddText(lines, line, pad, y, answerSize, 20f * u, TextBoxInk.Red);
                    y += 20f * u;
                }
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

    private static List<TextBoxLine> BuildPlain(TextBoxData card, out float height)
    {
        float u = MathF.Max(0.5f, card.Unit);
        float pad = 18f * u;
        float textW = MathF.Max(40f * u, card.Width - pad * 2f);
        float size = Math.Clamp(card.FontSize, 8f, 72f) * u;
        float lineH = size * 1.55f;
        var lines = new List<TextBoxLine>();
        float y = pad;
        foreach (string line in Wrap(card.Text ?? "", textW, size))
        {
            AddText(lines, line, pad, y, size, lineH, TextBoxInk.Black);
            y += lineH;
        }
        height = MathF.Max(y + pad, pad * 2f + lineH);
        return lines;
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

    private static void AddDivider(List<TextBoxLine> lines, float x0, float x1, float y)
    {
        lines.Add(new TextBoxLine
        {
            Kind = TextBoxLineKind.Divider,
            X = x0, Y = y, Size = 1f, Height = 1f,
            Box = new RectF { MinX = x0, MinY = y, MaxX = x1, MaxY = y + 1f },
        });
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
            Tool = Tool,
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
        if (s.TextBox.Role != TextBoxRole.AiQuestion)
        {
            MaybeEditTextBoxOnDoubleClick(s);
            return _textBoxEditing;
        }

        Vector2 local = new(cx, cy);
        if (!s.Transform.IsIdentity && Matrix3x2.Invert(s.Transform, out var inv))
            local = Vector2.Transform(local, inv);
        // 题卡内容以 Points[0] 为左上角（局部坐标），排版行/按钮都是相对它的；
        // 这里把"对象局部坐标"换成"卡片内容坐标"，否则原点不在 (0,0) 时按钮点不中。
        if (s.Points.Count > 0) local -= new Vector2(s.Points[0].X, s.Points[0].Y);

        var card = s.TextBox;
        var lines = card.EnsureLayout();

        // 右上角 ✕：删整卡，一步撤销。
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

        // 每道题的「显示答案 / 收起答案」。
        foreach (var line in lines)
        {
            if (line.Kind != TextBoxLineKind.Toggle) continue;
            if (!line.Box.Contains(local.X, local.Y)) continue;
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

    internal void TextBoxPointerUp()
    {
        _aiCardCapturing = false;
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
        // 底部留出工具条那一条（120 逻辑像素）：题卡展开后不能压住工具条。
        float bottom = screen.MaxY - ViewOffsetY - 120f * u;
        var b = s.PaddedBounds;
        float dy = 0f;
        if (b.MaxY > bottom) dy = bottom - b.MaxY;
        if (b.MinY + dy < top) dy = top - b.MinY;
        if (MathF.Abs(dy) > 0.5f)
            s.Transform = Matrix3x2.CreateTranslation(0f, dy) * s.Transform;
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
