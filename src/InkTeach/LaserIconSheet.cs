using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 出图用：把激光笔图标的几个候选**并排画出来**（开发期专用，不是产品界面）。
///
/// 为什么要这张图：图标"单薄不单薄"是眼睛说了算的事，光看代码里的坐标判断不了。
/// 每个候选画两遍——**真实尺寸**（24 逻辑像素，和工具格里一模一样）和**放大一倍**，
/// 前者看它在界面里的分量，后者看线条细节。
/// </summary>
internal sealed class LaserIconSheet : IOverlayUi
{
    private IUiHost _host;
    private RectF _bounds;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    /// <summary>一行一个候选：自绘的用 Variant，上游 Fluent 的用 Icon 名字。</summary>
    private static readonly (int Variant, string Icon, string Name)[] Items =
    {
        (0, null, "① 细光束（自绘）"),
        (1, null, "② 锥形光束（自绘）"),
        (2, null, "③ 加重笔+光束（自绘 · 现在用这个）"),
        (3, null, "④ 锥形+落点光环（自绘）"),
        (4, null, "⑤ 红点（PowerPoint 那种）"),
        (0, "laserFlash", "⑥ Fluent Flash（闪电）"),
        (0, "laserFlashlight", "⑦ Fluent Flashlight（手电）"),
        (0, "laserRecord", "⑧ Fluent Record（圆点）"),
        (0, "laserTarget", "⑨ Fluent Target（靶心）"),
        (0, "laserWand", "⑩ Fluent Wand（魔杖）"),
        (0, "laserSparkle", "⑪ Fluent Sparkle（星芒）"),
        (0, "laserCircle", "⑫ Fluent Circle（空心圆）"),
    };

    public string Name => "激光图标对照";
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
        var soft = new Color4(ink.R, ink.G, ink.B, 0.3f);
        var fmt = _host.TextFactory.CreateTextFormat("Microsoft YaHei UI", null,
            FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13f, "zh-CN");

        for (int i = 0; i < Items.Length; i++)
        {
            float y = _bounds.MinY + 12f + i * 64f;
            float cy = y + 26f;

            // 名称
            ctx.DrawText(Items[i].Name, fmt,
                         new Rect(_bounds.MinX + 16f, cy - 10f, 180f, 20f), Brush(ctx, ink));

            // 真实尺寸（24，画在一个 40 的按钮格里）
            var cell = new RectF { MinX = _bounds.MinX + 210f, MinY = y + 4f, MaxX = _bounds.MinX + 250f, MaxY = y + 44f };
            DrawCellBg(ctx, cell);
            DrawOne(ctx, cell, 24f, ink, soft, Items[i]);

            // 放大一倍（48）
            var big = new RectF { MinX = _bounds.MinX + 270f, MinY = y - 8f, MaxX = _bounds.MinX + 350f, MaxY = y + 72f };
            DrawOne(ctx, big, 48f, ink, soft, Items[i]);
        }
    }

    private void DrawCellBg(ID2D1DeviceContext ctx, RectF cell)
    {
        var box = new Vortice.RawRectF(cell.MinX, cell.MinY, cell.MaxX, cell.MaxY);
        ctx.FillRoundedRectangle(new RoundedRectangle(box, 8f, 8f), Brush(ctx, new Color4(0f, 0f, 0f, 0.07f)));
    }

    private void DrawOne(ID2D1DeviceContext ctx, RectF box, float size, Color4 ink, Color4 soft,
                         (int Variant, string Icon, string Name) item)
    {
        // 上游图标按"图标色"画；自绘的激光那几个按当前笔色/红的来
        if (item.Icon != null)
        {
            InkUi.IconAtlas.DrawCentered(ctx, item.Icon, box, size, Brush(ctx, ink));
            return;
        }
        InkUi.IconAtlas.DrawLaser(ctx, box, size, Brush(ctx, ink), Brush(ctx, soft), item.Variant);
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
