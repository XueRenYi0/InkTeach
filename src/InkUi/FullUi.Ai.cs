using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 「AI 出题」面板（1A）。
///
/// 刻意**不放进「更多」的子页**：语音/文字出题是高频动作，面板要和更多一样能
/// 直接开、直接关；但它也不动主工具条格表（1A 先验证交互）。
///
/// 1A 只做 UI 闭环：输入要求（原生 EDIT，中文/输入法交给系统）→ 生成一张
/// **mock 题卡** → 题卡黑题红解析、答案折叠、拖动、改宽、删除、撤销、保存。
/// 不联网、不录音、不调模型；以后接 ASR/模型只换数据源。
/// </summary>
public sealed partial class FullUi
{
    /// <summary>「更多 → 课堂」里的 AI 格编号。不占 3..7（墨迹格）和 8（使用说明）。</summary>
    private const int AiTileCode = 9;

    private const int AiHitOutside = -1;
    private const int AiHitInside = 0;
    private const int AiHitClose = 1;
    private const int AiHitVoice = 2;
    private const int AiHitPrompt = 3;
    private const int AiHitGenerate = 4;
    private const int AiHitChip = 10;       // 10+i（i = 预设 0..2）

    private bool _aiOpen;
    private readonly Anim _ai = new(0f);
    private int _aiHover = AiHitOutside;
    private string _aiPrompt = "给初二出三道二次函数题，中等难度，带答案";
    private bool _aiPromptEditing;
    private string _aiHint = "";

    private static readonly string[] AiChips =
    {
        "初二 · 二次函数 · 3 道 · 中等",
        "初三 · 一元二次方程 · 3 道 · 中等",
        "高一 · 二次函数图象 · 3 道 · 中等",
    };

    // ---- 开 / 关 ------------------------------------------------------------

    private void OpenAiPanel()
    {
        if (_moreOpen) CloseMore();
        _aiOpen = true;
        _aiHover = AiHitOutside;
        _aiHint = "";
        _aiPrompt = _host?.State.AiPrompt ?? _aiPrompt;
        _aiPromptEditing = _host?.State.AiPromptEditing ?? false;
        _ai.Jump(0f);
        _ai.To(1f, Tokens.MoreOpenMs);
        Invalidate();
    }

    private void CloseAiPanel()
    {
        if (!_aiOpen) return;
        if (_aiPromptEditing) _host?.Commands.EndAiPromptEdit(true);
        _aiOpen = false;
        _aiHover = AiHitOutside;
        _ai.To(0f, Tokens.MoreCloseMs);
        Invalidate();
    }

    /// <summary>Esc：AI → 更多 → 色板，从最上面那层往下关。</summary>
    public bool HandleEscape()
    {
        if (_aiOpen) { CloseAiPanel(); return true; }
        if (_moreOpen) { CloseMore(); return true; }
        if (_penPickerOpen) { ClosePenColorPicker(); return true; }
        return false;
    }

    // ---- 几何 ---------------------------------------------------------------

    private RectF AiRect()
    {
        float workW = _work.MaxX - _work.MinX;
        float workH = _work.MaxY - _work.MinY;
        float w = Math.Clamp(workW * 0.46f, 500f, 620f);
        float h = 330f;
        if (h > workH - 16f) h = MathF.Max(300f, workH - 16f);
        float cx = (_work.MinX + _work.MaxX) * 0.5f;
        float cy = (_work.MinY + _work.MaxY) * 0.5f;
        var r = new RectF
        {
            MinX = cx - w * 0.5f, MinY = cy - h * 0.5f,
            MaxX = cx + w * 0.5f, MaxY = cy + h * 0.5f,
        };
        const float m = 8f;
        if (r.MinX < _screen.MinX + m) { r.MaxX += _screen.MinX + m - r.MinX; r.MinX = _screen.MinX + m; }
        if (r.MaxX > _screen.MaxX - m) { r.MinX -= r.MaxX - (_screen.MaxX - m); r.MaxX = _screen.MaxX - m; }
        if (r.MinY < _screen.MinY + m) { r.MaxY += _screen.MinY + m - r.MinY; r.MinY = _screen.MinY + m; }
        if (r.MaxY > _screen.MaxY - m) { r.MinY -= r.MaxY - (_screen.MaxY - m); r.MaxY = _screen.MaxY - m; }
        return r;
    }

    private const float AiPad = 22f;
    private const float AiHeaderH = 40f;
    private const float AiVoiceH = 58f;
    private const float AiPromptH = 44f;
    private const float AiChipH = 34f;
    private const float AiGenerateH = 44f;

    private float AiContentW() => AiRect().MaxX - AiRect().MinX - AiPad * 2f;

    private RectF AiHeaderRect() => new()
    {
        MinX = AiRect().MinX + AiPad, MinY = AiRect().MinY + AiPad,
        MaxX = AiRect().MinX + AiPad + AiContentW(), MaxY = AiRect().MinY + AiPad + AiHeaderH,
    };

    private RectF AiCloseRect()
    {
        var h = AiHeaderRect();
        return new RectF { MinX = h.MaxX - 32f, MinY = h.MinY, MaxX = h.MaxX, MaxY = h.MinY + 32f };
    }

    private RectF AiVoiceRect()
    {
        var h = AiHeaderRect();
        return new RectF
        {
            MinX = h.MinX, MinY = h.MaxY + 6f,
            MaxX = h.MaxX, MaxY = h.MaxY + 6f + AiVoiceH,
        };
    }

    private RectF AiPromptLabelRect()
    {
        var v = AiVoiceRect();
        return new RectF { MinX = v.MinX, MinY = v.MaxY + 12f, MaxX = v.MaxX, MaxY = v.MaxY + 12f + 16f };
    }

    private RectF AiPromptRect()
    {
        var l = AiPromptLabelRect();
        return new RectF { MinX = l.MinX, MinY = l.MaxY + 2f, MaxX = l.MaxX, MaxY = l.MaxY + 2f + AiPromptH };
    }

    private RectF AiChipRect(int i)
    {
        var p = AiPromptRect();
        float gap = 8f;
        float w = (p.MaxX - p.MinX - gap * 2f) / 3f;
        float x = p.MinX + i * (w + gap);
        return new RectF { MinX = x, MinY = p.MaxY + 10f, MaxX = x + w, MaxY = p.MaxY + 10f + AiChipH };
    }

    private RectF AiGenerateRect()
    {
        var c = AiChipRect(0);
        var p = AiPromptRect();
        return new RectF
        {
            MinX = p.MaxX - 156f, MinY = c.MaxY + 12f,
            MaxX = p.MaxX, MaxY = c.MaxY + 12f + AiGenerateH,
        };
    }

    private RectF AiHintRect()
    {
        var g = AiGenerateRect();
        return new RectF
        {
            MinX = AiRect().MinX + AiPad, MinY = g.MinY + 4f,
            MaxX = g.MinX - 12f, MaxY = g.MaxY,
        };
    }

    private int AiHitAt(float x, float y)
    {
        if (!AiRect().Contains(x, y)) return AiHitOutside;
        if (AiCloseRect().Contains(x, y)) return AiHitClose;
        if (AiVoiceRect().Contains(x, y)) return AiHitVoice;
        if (AiPromptRect().Contains(x, y)) return AiHitPrompt;
        for (int i = 0; i < AiChips.Length; i++)
            if (AiChipRect(i).Contains(x, y)) return AiHitChip + i;
        if (AiGenerateRect().Contains(x, y)) return AiHitGenerate;
        return AiHitInside;
    }

    // ---- 输入 ---------------------------------------------------------------

    private void AiPointerDown(Vector2 p)
    {
        int hit = AiHitAt(p.X, p.Y);
        if (hit == AiHitOutside || hit == AiHitClose)
        {
            CloseAiPanel();
            return;
        }

        if (_aiPromptEditing && hit != AiHitPrompt)
            _host.Commands.EndAiPromptEdit(true);

        if (hit == AiHitVoice)
        {
            _aiHint = "语音出题在 1D 接入；现在可以用文字或下面的预设。";
            Invalidate();
            return;
        }

        if (hit == AiHitPrompt)
        {
            if (!_aiPromptEditing)
            {
                var r = AiPromptRect();
                _host.Commands.BeginAiPromptEdit(r.MinX, r.MinY, r.MaxX - r.MinX, r.MaxY - r.MinY);
                _aiPromptEditing = true;
            }
            Invalidate();
            return;
        }

        if (hit >= AiHitChip && hit < AiHitChip + AiChips.Length)
        {
            string text = AiChips[hit - AiHitChip];
            _host.Commands.SetAiPromptText(text);
            _aiPrompt = text;
            _aiHint = "";
            Invalidate();
            return;
        }

        if (hit == AiHitGenerate)
        {
            _host.Commands.EndAiPromptEdit(true);
            _host.Commands.InsertMockAiCard();
            CloseAiPanel();
            return;
        }

        Invalidate();
    }

    // ---- 绘制 ---------------------------------------------------------------

    private void DrawAiPanel(ID2D1DeviceContext ctx)
    {
        float a = Math.Clamp(_ai.Value, 0f, 1f);
        if (a <= 0.003f) return;

        var scrim = _dark ? Tokens.ScrimDark : Tokens.ScrimLight;
        ctx.FillRectangle(
            new Vortice.RawRectF(_screen.MinX, _screen.MinY, _screen.MaxX, _screen.MaxY),
            Brush(ctx, new Color4(scrim.R, scrim.G, scrim.B, QA(scrim.A * a))));

        var r = AiRect();
        var saved = ctx.Transform;
        float k = 0.97f + 0.03f * a;
        var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
        ctx.Transform = Matrix3x2.CreateTranslation(-c.X, -c.Y)
                      * Matrix3x2.CreateScale(k, k)
                      * Matrix3x2.CreateTranslation(c.X, c.Y)
                      * saved;
        try { DrawAiPanelCore(ctx); }
        finally { ctx.Transform = saved; }
    }

    private void DrawAiPanelCore(ID2D1DeviceContext ctx)
    {
        var r = AiRect();
        var card = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 20f, 20f);
        ctx.FillRoundedRectangle(card, Brush(ctx, _dark ? Tokens.PopoverPanelDark : Tokens.PopoverPanelLight));
        ctx.DrawRoundedRectangle(card, Brush(ctx, BorderCol), 1f);

        // 标题 + 关闭
        _widgets.Text(ctx, "AI 出题", AiHeaderRect(), 17f, Brush(ctx, InkCol), center: false);
        _widgets.Text(ctx, "✕", AiCloseRect(), 15f,
                      Brush(ctx, _aiHover == AiHitClose ? InkCol : MutedCol));

        // 语音主按钮（1A 先占位，点它给提示；1D 换成按住说话）
        var v = AiVoiceRect();
        var vr = new RoundedRectangle(new Vortice.RawRectF(v.MinX, v.MinY, v.MaxX, v.MaxY), 12f, 12f);
        ctx.FillRoundedRectangle(vr, Brush(ctx, new Color4(
            Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, _dark ? 0.20f : 0.10f)));
        ctx.DrawRoundedRectangle(vr, Brush(ctx, _aiHover == AiHitVoice ? Tokens.Accent : BorderCol), 1.2f);
        _widgets.Text(ctx, "按住说话", new RectF { MinX = v.MinX, MinY = v.MinY + 2f, MaxX = v.MaxX, MaxY = v.MinY + 34f },
                      16f, Brush(ctx, InkCol));
        _widgets.Text(ctx, "1A 暂未接入语音 · 可直接输入或点预设", new RectF { MinX = v.MinX, MinY = v.MinY + 30f, MaxX = v.MaxX, MaxY = v.MaxY },
                      10.5f, Brush(ctx, MutedCol));

        // 请求文字 / 输入框
        _widgets.Text(ctx, "题目要求", AiPromptLabelRect(), 11.5f, Brush(ctx, MutedCol), center: false);
        var p = AiPromptRect();
        var pr = new RoundedRectangle(new Vortice.RawRectF(p.MinX, p.MinY, p.MaxX, p.MaxY), 9f, 9f);
        ctx.FillRoundedRectangle(pr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(pr, Brush(ctx, _aiPromptEditing || _aiHover == AiHitPrompt ? Tokens.Accent : BorderCol),
                                 _aiPromptEditing ? 1.5f : 1f);
        string prompt = _aiPromptEditing ? "正在输入…（回车确认 / Esc 取消）" : _aiPrompt;
        if (string.IsNullOrWhiteSpace(prompt)) prompt = "例如：给初二出三道二次函数题，中等难度，带答案";
        _widgets.TextLeftMiddle(ctx, prompt,
            new RectF { MinX = p.MinX + 10f, MinY = p.MinY, MaxX = p.MaxX - 10f, MaxY = p.MaxY },
            12.5f, Brush(ctx, _aiPromptEditing ? MutedCol : InkCol));

        // 三个预设
        for (int i = 0; i < AiChips.Length; i++)
        {
            var cr = AiChipRect(i);
            bool on = string.Equals(_aiPrompt, AiChips[i], StringComparison.Ordinal);
            var rr = new RoundedRectangle(new Vortice.RawRectF(cr.MinX, cr.MinY, cr.MaxX, cr.MaxY), 8f, 8f);
            if (on) ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(
                Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, _dark ? 0.24f : 0.12f)));
            else if (_aiHover == AiHitChip + i) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, on ? Tokens.Accent : BorderCol), on ? 1.5f : 1f);
            _widgets.Text(ctx, AiChips[i], cr, 11f, Brush(ctx, on ? Tokens.Accent : InkCol));
        }

        // 生成按钮
        var g = AiGenerateRect();
        var gr = new RoundedRectangle(new Vortice.RawRectF(g.MinX, g.MinY, g.MaxX, g.MaxY), 10f, 10f);
        ctx.FillRoundedRectangle(gr, Brush(ctx, Tokens.Accent));
        _widgets.Text(ctx, "生成题卡", g, 14f, Brush(ctx, Tokens.AccentInk));

        // 提示行
        string hint = string.IsNullOrEmpty(_aiHint)
            ? "1A · 生成后用「选择」工具点卡片管理"
            : _aiHint;
        _widgets.TextLeftMiddle(ctx, hint, AiHintRect(), 10.5f, Brush(ctx, MutedCol));
    }

    // ---- 自检 / 摆样 --------------------------------------------------------

    internal void OpenAiPanelForTest()
    {
        OpenAiPanel();
        _ai.Jump(1f);
    }

    internal bool AiOpenForTest => _aiOpen;
    internal RectF AiRectForTest => AiRect();
    internal RectF AiGenerateRectForTest => AiGenerateRect();
    internal RectF AiPromptRectForTest => AiPromptRect();

    internal void GenerateMockForTest()
    {
        _host?.Commands.InsertMockAiCard();
        CloseAiPanel();
        _ai.Jump(0f);
    }
}
