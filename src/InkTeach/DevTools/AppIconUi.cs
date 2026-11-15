using System;
using System.Collections.Generic;
using System.Numerics;
using InkEngine;
using InkUi;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 出图用：**程序图标**（`--makeicon` 画的就是这一张；开发期专用，不是产品界面）。
///
/// 用户 2026-10-01 第五轮定：「线条型的笔，就用 B 方案（Fluent 笔 ＋ 带笔锋的红笔迹）」——
/// 形状和稿子 [design/图标-设计稿v5-线条型的笔.png] 与 [design/图标-设计稿v5b-笔迹四选.png]
/// 里的 B 一条条对得上，稿子代码在 `tools/design-sheet/Sheets15.cs`。
/// ⚠ 两边**是两份代码**（那边是 WPF 的 DrawingContext，这边是 Direct2D）：
///   改形状必须两边一起改，改完各出一张图对着看 —— 这是"同一个图形写两份"的已知代价。
///
/// 三件事（从下往上）：
///   ① **白砖**：圆角方 ＋ 一圈 1px 的极淡边。那圈边不是装饰：浅色任务栏 / 白底上，
///      纯白砖会直接糊进背景，"有边界"是它立得住的原因（见设计稿 v1 第 ① 节）。
///   ② **笔**：就用界面里那支 —— `IconAtlas` 的 "pen"（Fluent UI System Icons · Pen 24 regular，
///      MIT），走**界面同一条图标渲染路径**，所以任务栏上这支笔和工具条里那支是同一份路径数据。
///   ③ **笔迹**：带笔锋的一道红弧。它不是"描一条等宽的线"（那样两头一样粗，像根水管），
///      而是按脊线铺出来的**填充图形**：脊线两段贝塞尔，宽度「细—粗—细」，两端补圆头。
/// </summary>
internal sealed class AppIconUi : IOverlayUi
{
    /// <summary>图标设计格边长（逻辑像素）：砖 61、四周各留 1.5。</summary>
    public const float Grid = 64f;

    /// <summary>笔就是界面里那支 Fluent Pen（24 网格、regular）。</summary>
    private const string PenName = "pen";

    /// <summary>24 网格缩到 64 格里的 42 见方（稿子 v5 的数）。</summary>
    private const float PenSize = 42f;

    /// <summary>
    /// 笔尖在 **24 网格**里的位置（2.36, 21.66）：从 Fluent Pen 几何的包围盒左下角量出来的
    /// （斜 45° 的笔，尖就在左下角）。换笔的图形时这里要跟着量。
    /// </summary>
    private static readonly Vector2 PenTip24 = new(2.36f, 21.66f);

    // 笔迹：脊线采样点数、起笔 / 最粗的半宽（64 格里的数，和 Sheets15.P5BrushTrail 一字不差）
    private const int TrailSamples = 48;
    private const float TrailWStart = 0.8f;
    private const float TrailWPeak = 3.6f;

    private static readonly Color4 Ink = new(0x1B / 255f, 0x1B / 255f, 0x1F / 255f, 1f);
    private static readonly Color4 Red = new(0xF2 / 255f, 0x2E / 255f, 0x2E / 255f, 1f);
    private static readonly Color4 White = new(1f, 1f, 1f, 1f);
    private static readonly Color4 TileEdge = new(0xE3 / 255f, 0xE6 / 255f, 0xEB / 255f, 1f);

    private IUiHost _host;
    private RectF _bounds;
    private ID2D1PathGeometry _trail;
    private Vector2 _trailStart, _trailEnd;
    private float _trailStartR, _trailEndR;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    public string Name => "程序图标";
    public bool Visible => true;
    public bool IsAnimating => false;

    public void Attach(IUiHost host)
    {
        _host = host;
        Layout(host.Screen, host.DpiScale);
    }

    public RectF Layout(RectF screen, float dpiScale)
    {
        _bounds = new RectF
        {
            MinX = screen.MinX, MinY = screen.MinY,
            MaxX = screen.MinX + Grid, MaxY = screen.MinY + Grid,
        };
        return _bounds;
    }

    public RectF QueryBounds() => _bounds;

    public void Render(ID2D1DeviceContext ctx, UiTheme theme)
    {
        if (_bounds.IsEmpty) return;
        var saved = ctx.Transform;

        // ① 白砖
        var rounded = new RoundedRectangle(new Vortice.RawRectF(1.5f, 1.5f, Grid - 1.5f, Grid - 1.5f), 14f, 14f);
        ctx.FillRoundedRectangle(rounded, Brush(ctx, White));
        ctx.DrawRoundedRectangle(rounded, Brush(ctx, TileEdge), 1f);

        // ② 笔（界面同一条路：同一份路径数据、同一个 DrawCentered）
        var iconBox = new RectF { MinX = 0f, MinY = 0f, MaxX = Grid, MaxY = Grid };
        IconAtlas.DrawCentered(ctx, PenName, iconBox, PenSize, Brush(ctx, Ink));

        // ③ 笔迹（脊线铺出来的形状 ＋ 两端的圆头）
        var red = Brush(ctx, Red);
        ctx.FillGeometry(Trail(), red);
        ctx.FillEllipse(new Ellipse(_trailStart, _trailStartR, _trailStartR), red);
        ctx.FillEllipse(new Ellipse(_trailEnd, _trailEndR, _trailEndR), red);

        ctx.Transform = saved;
    }

    /// <summary>
    /// 笔迹（建一次）：脊线两段贝塞尔 —— 从笔尖起笔，先压下、再横扫向右上；
    /// 宽度沿脊线走 sin 曲线「细—粗—细」；左右两条边铺成一个闭合图形。
    /// </summary>
    private ID2D1PathGeometry Trail()
    {
        if (_trail != null) return _trail;

        var start = PenTip();
        const int n = TrailSamples;
        var spine = new Vector2[n + 1];
        var half = new float[n + 1];

        var c1 = new Vector2(start.X + 0.8f, start.Y + 3.2f);
        var c2 = new Vector2(start.X + 5.0f, start.Y + 5.0f);
        var mid = new Vector2(start.X + 13.0f, start.Y + 4.6f);
        var c3 = new Vector2(start.X + 24.0f, start.Y + 4.0f);
        var c4 = new Vector2(start.X + 33.0f, start.Y - 0.5f);
        var end = new Vector2(start.X + 41.0f, start.Y - 7.5f);

        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            spine[i] = t < 0.5f
                ? Bez(start, c1, c2, mid, t * 2f)
                : Bez(mid, c3, c4, end, (t - 0.5f) * 2f);
            half[i] = TrailWStart + (TrailWPeak - TrailWStart) * MathF.Sin(MathF.PI * MathF.Pow(t, 0.7f));
        }

        var left = new Vector2[n + 1];
        var right = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            var a = spine[Math.Max(0, i - 1)];
            var b = spine[Math.Min(n, i + 1)];
            var d = b - a;
            float len = MathF.Max(0.0001f, d.Length());
            var nrm = new Vector2(-d.Y / len, d.X / len);
            left[i] = spine[i] + nrm * half[i];
            right[i] = spine[i] - nrm * half[i];
        }

        var g = _host.PathFactory.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.BeginFigure(left[0], FigureBegin.Filled);
            for (int i = 1; i <= n; i++) sink.AddLine(left[i]);
            for (int i = n; i >= 0; i--) sink.AddLine(right[i]);
            sink.EndFigure(FigureEnd.Closed);
            // ⚠ 这一下不能省：不收尾，几何就是"没建完"的状态，拿去 Fill 会 D2DERR_WRONG_STATE
            //   （2026-10-01 落地第五轮图标时踩到，症状是 EndDraw 报"对象未处于正确的状态"）。
            sink.Close();
        }

        _trail = g;
        _trailStart = spine[0];
        _trailStartR = half[0];
        _trailEnd = spine[n];
        _trailEndR = half[n];
        return _trail;
    }

    /// <summary>笔尖落在 64 格里的哪儿：和稿子同一个算法（把 24 网格的笔尖按同一套映射搬过来）。</summary>
    private static Vector2 PenTip()
    {
        float s = PenSize / 24f;
        return new Vector2(32f + (PenTip24.X - 12f) * s, 32f + (PenTip24.Y - 12f) * s);
    }

    private static Vector2 Bez(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        float a = u * u * u, b = 3f * u * u * t, c = 3f * u * t * t, d = t * t * t;
        return new Vector2(a * p0.X + b * p1.X + c * p2.X + d * p3.X,
                           a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
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
