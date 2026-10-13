using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 出图用：**截屏工具图标**的候选并排画出来。
///
/// 缘起（用户 2026-09-17）："截图图标和选中图标一样的，是不是不大好？"
/// ——确实像：截屏用的是 Fluent `Screenshot`（方框 + 四角括号），
/// 选择用的是 `Select Object`（虚线方框 + 角点），缩到 24 像素在一条带子里几乎分不出。
///
/// 所以把候选（连同"和它像的那一个"）并排画出来，真实尺寸 + 放大一倍各画一遍——
/// 图标这种事**眼睛说了算**，光看名字判断不了。
/// </summary>
internal sealed class CaptureIconSheet : IOverlayUi
{
    private IUiHost _host;
    private RectF _bounds;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    private static readonly (string Icon, string Name)[] Items =
    {
        ("capture",   "① Fluent Screenshot（现在用这个）"),
        ("camera",    "② Fluent Camera（相机）"),
        ("shotRecord","③ Fluent Screenshot Record（带点）"),
        ("crop",      "④ Fluent Crop（裁切）"),
        ("scan",      "⑤ Fluent Scan（扫描）"),
        ("select",    "⑥ Fluent Select Object（选择工具在用，拿来对照）"),
    };

    public string Name => "截屏图标对照";
    public bool Visible => true;
    public bool IsAnimating => false;

    public void Attach(IUiHost host)
    {
        _host = host;
        InkUi.IconAtlas.Init(host.PathFactory);
        Layout(host.Screen, host.DpiScale);
    }

    public RectF Layout(RectF screen, float dpiScale)
    {
        float w = 620f, h = Items.Length * 64f + 24f;
        _bounds = new RectF
        {
            MinX = screen.MinX + (screen.MaxX - screen.MinX - w) * 0.5f,
            MinY = screen.MinY + 120f, MaxX = 0, MaxY = 0,
        };
        _bounds.MaxX = _bounds.MinX + w;
        _bounds.MaxY = _bounds.MinY + h;
        return _bounds;
    }

    public RectF QueryBounds() => _bounds;

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_host == null || _bounds.IsEmpty) return;

        var box = new Vortice.RawRectF(_bounds.MinX, _bounds.MinY, _bounds.MaxX, _bounds.MaxY);
        ctx.FillRoundedRectangle(new RoundedRectangle(box, 12f, 12f), Brush(ctx, new Color4(1f, 1f, 1f, 0.92f)));
        ctx.DrawRoundedRectangle(new RoundedRectangle(box, 12f, 12f), Brush(ctx, new Color4(0f, 0f, 0f, 0.12f)), 1f);

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);
        var fmt = _host.TextFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13f, "zh-CN");

        for (int i = 0; i < Items.Length; i++)
        {
            float y = _bounds.MinY + 12f + i * 64f;
            float cy = y + 26f;
            ctx.DrawText(Items[i].Name, fmt,
                         new Rect(_bounds.MinX + 16f, cy - 10f, 260f, 20f), Brush(ctx, ink));

            var cell = new RectF { MinX = _bounds.MinX + 290f, MinY = y + 4f, MaxX = _bounds.MinX + 330f, MaxY = y + 44f };
            var cbox = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
            ctx.FillRoundedRectangle(new RoundedRectangle(cbox, 8f, 8f), Brush(ctx, new Color4(0f, 0f, 0f, 0.07f)));
            InkUi.IconAtlas.DrawCentered(ctx, Items[i].Icon, cell, 24f, Brush(ctx, ink));

            var big = new RectF { MinX = _bounds.MinX + 360f, MinY = y - 8f, MaxX = _bounds.MinX + 440f, MaxY = y + 72f };
            InkUi.IconAtlas.DrawCentered(ctx, Items[i].Icon, big, 48f, Brush(ctx, ink));
        }
    }

    private ID2D1SolidColorBrush Brush(ID2D1DeviceContext ctx, Color4 c)
    {
        uint key = ((uint)(c.R * 255) << 24) | ((uint)(c.G * 255) << 16)
                 | ((uint)(c.B * 255) << 8) | (uint)(c.A * 255);
        if (_brushes.TryGetValue(key, out var b)) return b;
        b = ctx.CreateSolidColorBrush(c, null);
        _brushes[key] = b;
        return b;
    }

    public bool PointerDown(in UiPointerEvent e) => false;
    public bool PointerMove(in UiPointerEvent e) => false;
    public void PointerLeave() { }
    public bool PointerUp(in UiPointerEvent e) => false;
    public void OnStateChanged(in UiState state) { }
}
