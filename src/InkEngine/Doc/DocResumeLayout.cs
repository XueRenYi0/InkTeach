// [职责] 文档恢复提示卡的几何：卡片 / 两行字 / 两个按钮的矩形（画与命中同一份）。
// [测试] --doctest（F 组）/ --panelshow --docresume

namespace InkEngine;

/// <summary>恢复提示卡的各块矩形（**屏幕物理像素**）。</summary>
internal readonly struct DocResumeCardRects
{
    public readonly RectF Card;
    public readonly RectF Line1;
    public readonly RectF Line2;
    public readonly RectF Continue;
    public readonly RectF StartOver;

    public DocResumeCardRects(RectF card, RectF line1, RectF line2, RectF cont, RectF over)
    {
        Card = card; Line1 = line1; Line2 = line2; Continue = cont; StartOver = over;
    }
}

/// <summary>
/// 恢复卡排版。**画和命中都走这里**：分开写迟早差几个像素，表现就是
/// "看得见按钮却点不中"（操作条那一批已经踩过）。
///
/// 尺寸：约 420×64 逻辑像素（文案更长时自动加宽），贴工作区顶边、
/// 水平居中，两个按钮右对齐。整套沿用界面推上来的 `UiUnit`（界面大小档）。
/// </summary>
internal static class DocResumeLayout
{
    public const float MinWidthLogical = 420f;
    public const float HeightLogical = 64f;

    /// <summary>按屏幕矩形（物理像素）、当前界面单位、两行文案算各块矩形。</summary>
    public static DocResumeCardRects Compute(in RectF screen, float unit, string line1, string line2)
    {
        if (unit <= 0f) unit = 1f;
        float pad = 16f * unit;
        float btnGap = 8f * unit;
        float textGap = 16f * unit;

        float contW = ButtonWidth("继续", unit);
        float overW = ButtonWidth("从头", unit);
        float textW = MathF.Max(EstimateTextWidth(line1, 14f) * unit,
                                EstimateTextWidth(line2, 11f) * unit);
        float width = MathF.Max(MinWidthLogical * unit, pad * 2f + textW + textGap + contW + btnGap + overW);

        float screenW = MathF.Max(1f, screen.MaxX - screen.MinX);
        if (width > screenW - 24f * unit) width = MathF.Max(240f * unit, screenW - 24f * unit);

        float height = HeightLogical * unit;
        float x = screen.MinX + (screenW - width) * 0.5f;
        float y = screen.MinY + 12f * unit;

        var card = new RectF { MinX = x, MinY = y, MaxX = x + width, MaxY = y + height };
        float btnH = 32f * unit;
        float btnY = y + (height - btnH) * 0.5f;
        float overX = card.MaxX - pad - overW;
        float contX = overX - btnGap - contW;
        var cont = new RectF { MinX = contX, MinY = btnY, MaxX = contX + contW, MaxY = btnY + btnH };
        var over = new RectF { MinX = overX, MinY = btnY, MaxX = overX + overW, MaxY = btnY + btnH };

        float textLeft = x + pad;
        float textWAvail = MathF.Max(10f, cont.MinX - 12f * unit - textLeft);
        var line1Rect = new RectF { MinX = textLeft, MinY = y + 6f * unit, MaxX = textLeft + textWAvail, MaxY = y + 32f * unit };
        var line2Rect = new RectF { MinX = textLeft, MinY = y + 30f * unit, MaxX = textLeft + textWAvail, MaxY = y + 52f * unit };
        return new DocResumeCardRects(card, line1Rect, line2Rect, cont, over);
    }

    /// <summary>把整张卡上下挪 <paramref name="dy"/> 物理像素（给"避开界面 / 页码条"用）。</summary>
    public static DocResumeCardRects Shift(in DocResumeCardRects r, float dy)
    {
        RectF Mv(in RectF a) => new() { MinX = a.MinX, MinY = a.MinY + dy, MaxX = a.MaxX, MaxY = a.MaxY + dy };
        return new DocResumeCardRects(Mv(r.Card), Mv(r.Line1), Mv(r.Line2), Mv(r.Continue), Mv(r.StartOver));
    }

    /// <summary>
    /// 按钮宽：文案宽 + 左右留白。用**估算**而不是 DirectWrite 量——引擎这层
    /// 拿不到文字格式；中文按 1 个字号、西文按 0.56 个字号估，留白足够兜住误差。
    /// </summary>
    private static float ButtonWidth(string label, float unit)
        => EstimateTextWidth(label, 14f) * unit + 36f * unit;

    /// <summary>粗估文字宽度（逻辑像素）。只给布局用，不追求逐像素精确。</summary>
    public static float EstimateTextWidth(string text, float logicalPx)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float w = 0f;
        foreach (char c in text)
        {
            if (c >= 0x2E80) w += logicalPx;                 // CJK / 全角
            else if (c == ' ') w += logicalPx * 0.30f;
            else if (c is >= '0' and <= '9') w += logicalPx * 0.58f;
            else w += logicalPx * 0.56f;
        }
        return w;
    }
}
