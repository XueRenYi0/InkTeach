using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 界面用得着的几类元件：文字、色片、分段选择框、滑条。
///
/// 为什么只有这几类：界面是被"按钮带 ＋ 上带"两件事撑起来的，
/// 需要的元件就这几种。**不做通用控件库**——通用控件库会把复杂度吃光，
/// 而这里每一件都是照着设计稿 1:1 落下来的。
///
/// 文字格式**缓存**（同一档字号只建一次）：DirectWrite 建格式不便宜，
/// 而这里每帧都在画字。
/// </summary>
internal sealed class Widgets
{
    private readonly IUiHost _host;
    private readonly Dictionary<(float Size, bool Center), IDWriteTextFormat> _formats = new();

    public Widgets(IUiHost host) => _host = host;

    // ---- 文字 ---------------------------------------------------------------

    private IDWriteTextFormat Format(float sizeLogical, bool center)
    {
        if (_formats.TryGetValue((sizeLogical, center), out var f)) return f;

        // **字号就是逻辑像素，不要再乘 DPI**：引擎画界面时已经把整个上下文按 DPI 缩放过
        // 一次（Overlay.DrawUi 里的 CreateScale(dpiScale)），再乘一次等于放大两遍——
        // 症状是"标签大得离谱、三个字的按钮标题被挤成两行"（这一版就是这么被看出来的）。
        float px = MathF.Max(9f, sizeLogical);
        f = _host.TextFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px, "zh-CN");
        if (center)
        {
            f.TextAlignment = TextAlignment.Center;
            f.ParagraphAlignment = ParagraphAlignment.Center;
        }
        _formats[(sizeLogical, center)] = f;
        return f;
    }

    /// <summary>
    /// 在矩形里画一段文字（默认居中）。
    /// 注意 Vortice 的 `Rect(x, y, w, h)` 是"位置 ＋ 尺寸"，不是
    /// (左, 上, 右, 下)——写错的话文字会被排到很远的屏幕外，看起来像"没画出来"。
    /// </summary>
    public void Text(ID2D1DeviceContext ctx, string text, RectF box, float size,
                     ID2D1Brush brush, bool center = true)
    {
        ctx.DrawText(text, Format(size, center),
                     new Rect(box.MinX, box.MinY, box.MaxX - box.MinX, box.MaxY - box.MinY),
                     brush);
    }

    /// <summary>
    /// 量一段文字有多宽（逻辑像素）。**量和画用同一个 Format**——悬停提示的卡片
    /// 要"按文字量宽"（照引擎侧呼出盘提示卡的做法），量盒子和画字不同源就会裁字或留白。
    /// 量不出来返回 0（调用方退回一个最小宽），绝不因为量个宽度把渲染搞挂。
    /// </summary>
    public float Measure(string text, float size)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        try
        {
            using var layout = _host.TextFactory.CreateTextLayout(text, Format(size, false), 4096f, 1024f);
            return layout.Metrics.Width;
        }
        catch { return 0f; }
    }

    /// <summary>
    /// 左对齐 ＋ **垂直居中**。
    ///
    /// DWrite 的默认段落对齐是"顶部"（`ParagraphAlignment.Near`），照原样画会贴行的上沿；
    /// 设置页那些行是"左边标签 ＋ 右边开关"，而开关是垂直居中的——标签贴顶就和开关
    /// 差了半行（用户 2026-10-10 报的"标签和开关不在一条线上"就是这个）。
    /// 行高按字号实测一次、缓存住（同一字号的行高恒定）。
    /// </summary>
    public void TextLeftMiddle(ID2D1DeviceContext ctx, string text, RectF box, float size, ID2D1Brush brush)
    {
        float h = LineHeight(size);
        float y = box.MinY + MathF.Max(0f, ((box.MaxY - box.MinY) - h) * 0.5f);
        ctx.DrawText(text, Format(size, false),
                     new Rect(box.MinX, y, box.MaxX - box.MinX, h), brush);
    }

    private readonly Dictionary<float, float> _lineHeights = new();

    private float LineHeight(float size)
    {
        if (_lineHeights.TryGetValue(size, out float h)) return h;
        try
        {
            using var layout = _host.TextFactory.CreateTextLayout("汉", Format(size, false), 4096f, 4096f);
            h = layout.Metrics.Height;
        }
        catch { h = size * 1.4f; }
        _lineHeights[size] = h;
        return h;
    }

    // ---- 滑条 ---------------------------------------------------------------

    /// <summary>轨道 + 已走过去的那一段 + 滑钮。返回滑钮的圆心（画预览用得上）。</summary>
    public void Slider(ID2D1DeviceContext ctx, RectF box, float t01,
                       ID2D1Brush track, ID2D1Brush fill, ID2D1Brush knob)
    {
        float cy = (box.MinY + box.MaxY) * 0.5f;
        float left = box.MinX + Tokens.SliderKnob * 0.5f;
        float right = box.MaxX - Tokens.SliderKnob * 0.5f;
        var a = new System.Numerics.Vector2(left, cy);
        var b = new System.Numerics.Vector2(right, cy);

        ctx.DrawLine(a, b, track, Tokens.SliderTrack);
        var k = new System.Numerics.Vector2(left + (right - left) * Math.Clamp(t01, 0f, 1f), cy);
        ctx.DrawLine(a, k, fill, Tokens.SliderTrack);
        ctx.FillEllipse(new Ellipse(k, Tokens.SliderKnob * 0.5f, Tokens.SliderKnob * 0.5f), knob);
    }

    /// <summary>滑条的可拖区域（比视觉大一圈，手指才抓得住）。</summary>
    public static RectF SliderHit(RectF box) => new()
    {
        MinX = box.MinX - 6, MinY = box.MinY - 6,
        MaxX = box.MaxX + 6, MaxY = box.MaxY + 6,
    };
}
