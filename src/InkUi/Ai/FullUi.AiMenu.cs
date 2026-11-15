using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 选中操作条末尾的「AI」彩色格 + 下拉菜单。
///
/// 2026-11-05 用户定：
///   · 跟着操作条一起出现，放在「全选 / 复制 / 粘贴」后面；
///   · 彩色、丰富、梦幻 —— 青 → 紫 → 粉 的横向渐变；
///   · 下拉只放已经做好的功能，不展示灰项。
///   · 2026-11-06 加第二项：公式默认保留原笔迹（安全），要连公式换需明确点第二项。
/// </summary>
public sealed partial class FullUi
{
    private enum AiMenuItem { None = -1, Handwriting = 0 }

    private bool _aiMenuOpen;
    private int _aiMenuHover = -1;
    private string _lastAiStatus = "";
    private bool _lastAiBusy;

    private const float AiCellW = 62f;
    private const float AiMenuW = 236f;
    private const float AiMenuRowH = 40f;
    private const float AiMenuStatusH = 30f;
    private const float AiMenuPad = 6f;
    private const int AiMenuItems = 2;

    private static readonly Color4 AiCyan = new(0.13f, 0.83f, 0.93f, 1f);
    private static readonly Color4 AiPurple = new(0.63f, 0.36f, 0.98f, 1f);
    private static readonly Color4 AiPink = new(0.96f, 0.45f, 0.71f, 1f);

    /// <summary>AI 格是动作组里的最后一格（由 ActionRectAt 统一排布）。</summary>
    private RectF AiCellRect() => ActionRectAt(ActionCount - 1);

    private RectF AiMenuRect()
    {
        var c = AiCellRect();
        float w = AiMenuW;
        float h = AiMenuPad * 2 + AiMenuRowH * AiMenuItems
                  + (_lastAiStatus.Length > 0 ? AiMenuStatusH : 0f);
        float x = c.MinX;
        float y = c.MaxY + 8f;                 // 带子在屏幕下方时往上翻
        if (y + h > _screen.MaxY - 8f) y = c.MinY - 8f - h;
        x = Math.Clamp(x, _screen.MinX + 8f, MathF.Max(_screen.MinX + 8f, _screen.MaxX - 8f - w));
        y = Math.Clamp(y, _screen.MinY + 8f, MathF.Max(_screen.MinY + 8f, _screen.MaxY - 8f - h));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    private RectF AiMenuStatusRect()
    {
        var r = AiMenuRect();
        float y = r.MinY + AiMenuPad + AiMenuRowH * AiMenuItems;
        return new RectF { MinX = r.MinX + 10f, MinY = y, MaxX = r.MaxX - 10f, MaxY = y + AiMenuStatusH };
    }

    private RectF AiMenuItemRect(int i)
    {
        var r = AiMenuRect();
        float y = r.MinY + AiMenuPad + i * AiMenuRowH;
        return new RectF { MinX = r.MinX + AiMenuPad, MinY = y, MaxX = r.MaxX - AiMenuPad, MaxY = y + AiMenuRowH };
    }

    private void ToggleAiMenu()
    {
        _aiMenuOpen = !_aiMenuOpen;
        _aiMenuHover = -1;
        Invalidate();
    }

    private void CloseAiMenu()
    {
        if (!_aiMenuOpen) return;
        _aiMenuOpen = false;
        _aiMenuHover = -1;
        Invalidate();
    }

    /// <summary>点中菜单项返回 true（调用方不要再把这一下派给别处）。</summary>
    private bool AiMenuClick(float x, float y)
    {
        if (!_aiMenuOpen) return false;
        if (AiMenuItemRect(0).Contains(x, y))
        {
            // 默认安全模式：公式行保留原笔迹。
            _host?.Commands.OptimizeHandwriting(false);
            _aiMenuHover = -1;
            Invalidate();
            return true;
        }
        if (AiMenuItemRect(1).Contains(x, y))
        {
            // 用户明确选择“含公式”：OCR 对下标敏感，结果会有识别误差。
            _host?.Commands.OptimizeHandwriting(true);
            _aiMenuHover = -1;
            Invalidate();
            return true;
        }
        if (!AiMenuRect().Contains(x, y)) CloseAiMenu();
        return true;
    }

    private void DrawAiCell(ID2D1DeviceContext ctx, in RectF r)
    {
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);

        // 青 → 紫 → 粉：横向线性渐变
        var stops = new[]
        {
            new GradientStop(0f, AiCyan),
            new GradientStop(0.55f, AiPurple),
            new GradientStop(1f, AiPink),
        };
        using var coll = ctx.CreateGradientStopCollection(stops, ExtendMode.Clamp);
        using var grad = ctx.CreateLinearGradientBrush(
            new LinearGradientBrushProperties
            {
                StartPoint = new Vector2(r.MinX, r.MinY),
                EndPoint = new Vector2(r.MaxX, r.MaxY),
            }, new BrushProperties { Opacity = 1f }, coll);

        ctx.FillRoundedRectangle(rr, grad);

        _widgets.Text(ctx, "AI", r, 12.5f, Brush(ctx, new Color4(1f, 1f, 1f, 1f)));
        var tri = new RectF { MinX = r.MaxX - 16f, MinY = r.MinY, MaxX = r.MaxX - 6f, MaxY = r.MaxY };
        _widgets.Text(ctx, "▾", tri, 10f, Brush(ctx, new Color4(1f, 1f, 1f, 0.9f)));

        if (_host != null && _host.NowMs < _actionFlashUntil)
            ctx.DrawRoundedRectangle(rr, Brush(ctx, new Color4(1f, 1f, 1f, 0.9f)), 2f);
    }

    private void DrawAiMenu(ID2D1DeviceContext ctx)
    {
        var r = AiMenuRect();
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 10f, 10f);
        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(1f, 1f, 1f, 0.98f)));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, new Color4(0f, 0f, 0f, 0.12f)), 1f);

        string[] labels = { "✨ 手写优化（保留公式）", "✨ 手写优化（含公式）" };
        for (int i = 0; i < labels.Length; i++)
        {
            var item = AiMenuItemRect(i);
            if (_aiMenuHover == i)
                ctx.FillRoundedRectangle(
                    new RoundedRectangle(new Vortice.RawRectF(item.MinX, item.MinY, item.MaxX, item.MaxY), 7f, 7f),
                    Brush(ctx, new Color4(0.63f, 0.36f, 0.98f, 0.12f)));
            _widgets.Text(ctx, labels[i], item, 12.5f, Brush(ctx, new Color4(0.15f, 0.15f, 0.18f, 1f)));
        }

        string status = _lastAiStatus;
        if (status.Length > 0)
        {
            var sr = AiMenuStatusRect();
            var col = _lastAiBusy
                ? new Color4(0.55f, 0.30f, 0.85f, 1f)
                : status.Contains("公式可能")
                    ? new Color4(0.85f, 0.50f, 0.10f, 1f)
                    : status.StartsWith("已优化", StringComparison.Ordinal)
                        ? new Color4(0.10f, 0.55f, 0.30f, 1f)
                        : new Color4(0.60f, 0.20f, 0.20f, 1f);
            _widgets.Text(ctx, status, sr, 10.5f, Brush(ctx, col));
        }
    }
}
