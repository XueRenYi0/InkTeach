using InkEngine;

namespace InkTeach;

/// <summary>
/// 覆盖窗口的坐标换算（OverlayCoords）：**副屏原点**下的画布 / 屏幕 / 窗口三套坐标。
///
/// 为什么要单测（2026-11-16 残影评审 P0-2）：本机只有一块屏，主屏原点恒为 (0,0)，
/// 屏幕坐标和窗口坐标混着用也全部抵消；副屏（扩展模式投影仪，OriginX = 1920）上界面
/// "只画不擦"、内容层贴不上。这里不起窗口、不碰 GPU，把 OriginX = 1920 下的换算钉住。
/// </summary>
internal static class UnitTestsOverlay
{
    private const int W = 1920, H = 1080, OX = 1920, OY = 0;

    private static void RectNear(RectF expected, RectF actual, string what)
    {
        Check.Near(expected.MinX, actual.MinX, 0.001f, file: what + ".MinX");
        Check.Near(expected.MinY, actual.MinY, 0.001f, file: what + ".MinY");
        Check.Near(expected.MaxX, actual.MaxX, 0.001f, file: what + ".MaxX");
        Check.Near(expected.MaxY, actual.MaxY, 0.001f, file: what + ".MaxY");
    }

    private static RectF R(float l, float t, float r, float b) => new() { MinX = l, MinY = t, MaxX = r, MaxY = b };

    [UnitTest]
    internal static void OverlayCoords_SecondaryScreen_CanvasWindow_RoundTrip()
    {
        // 相机 0：副屏左缘（画布 x = 1920）就是窗口 x = 0
        Check.Near(0f, OverlayCoords.CanvasToWindow(1920f, 1f, 0f, OX), file: "canvas→window 左缘");
        Check.Near(1920f, OverlayCoords.CanvasToWindow(3840f, 1f, 0f, OX), file: "canvas→window 右缘");
        Check.Near(1920f, OverlayCoords.WindowToCanvas(0f, 1f, 0f, OX), file: "window→canvas 左缘");

        // 缩放 + 平移：往返一致
        foreach (float c in new[] { -500f, 0f, 1920f, 2500.5f, 9999f })
        {
            float w = OverlayCoords.CanvasToWindow(c, 1.25f, -100f, OX);
            Check.Near(c * 1.25f - 100f - OX, w, 0.001f, file: "canvas→window 公式");
            Check.Near(c, OverlayCoords.WindowToCanvas(w, 1.25f, -100f, OX), 0.001f, file: "往返");
        }
    }

    [UnitTest]
    internal static void OverlayCoords_SecondaryScreen_VisibleCanvas_NoDoubleOrigin()
    {
        // 以前把 OriginX 当窗口坐标喂进"窗口 → 画布"，原点被加两次：可见区会是 [3840, 5760]
        RectNear(R(1920, 0, 3840, 1080),
                 OverlayCoords.VisibleCanvas(W, H, OX, OY, 1f, 0f, 0f), "副屏相机 0");
        RectNear(R(2020, 1800, 3940, 2880),
                 OverlayCoords.VisibleCanvas(W, H, OX, OY, 1f, -100f, -1800f), "副屏滚动后");
        RectNear(R(960, 0, 1920, 540),
                 OverlayCoords.VisibleCanvas(W, H, OX, OY, 2f, 0f, 0f), "副屏放大 2×");
        // 主屏（原点 0）不受影响
        RectNear(R(0, 0, 1920, 1080),
                 OverlayCoords.VisibleCanvas(W, H, 0, 0, 1f, 0f, 0f), "主屏");
    }

    [UnitTest]
    internal static void OverlayCoords_SecondaryScreen_Clip_And_ScreenToWindow()
    {
        // 界面矩形是屏幕坐标：副屏上 (2000..2500) → 窗口 (80..580)，再裁剪不变
        var ui = OverlayCoords.ScreenToWindow(R(2000, 900, 2500, 1000), OX, OY);
        RectNear(R(80, 900, 580, 1000), ui, "界面 屏幕→窗口");
        RectNear(ui, OverlayCoords.ClipToWindow(ui, W, H), "界面裁剪");
        // 没换算的屏幕坐标会被判"越出窗口"（Present 里的 Trace 校验靠它）
        Check.True(!OverlayCoords.InsideWindow(R(2000, 900, 2500, 1000), W, H), "屏幕坐标应判越界");
        Check.True(OverlayCoords.InsideWindow(ui, W, H), "窗口坐标应判在窗内");

        // 画布矩形跨副屏左缘：画布 (1900..1950) → 窗口 (-20..30) → 裁成 (0..30)
        var cw = R(OverlayCoords.CanvasToWindow(1900f, 1f, 0f, OX), 100f,
                   OverlayCoords.CanvasToWindow(1950f, 1f, 0f, OX), 200f);
        RectNear(R(0, 100, 30, 200), OverlayCoords.ClipToWindow(cw, W, H), "画布矩形跨左缘");

        // 整个落在主屏那边的画布矩形：副屏窗口里是空的
        var off = R(OverlayCoords.CanvasToWindow(100f, 1f, 0f, OX), 0f,
                    OverlayCoords.CanvasToWindow(500f, 1f, 0f, OX), 50f);
        Check.True(OverlayCoords.ClipToWindow(off, W, H).IsEmpty, "主屏那边的矩形在副屏应裁空");

        // 整屏矩形 = (0,0)..(W,H)，裁剪后不变；超出部分被夹掉
        RectNear(R(0, 0, W, H), OverlayCoords.ClipToWindow(OverlayCoords.FullWindow(W, H), W, H), "整屏");
        RectNear(R(0, 5, 100, H), OverlayCoords.ClipToWindow(R(-10, 5, 100, 2000), W, H), "越界夹取");
        Check.True(OverlayCoords.ScreenToWindow(RectF.Empty, OX, OY).IsEmpty, "空矩形换算后仍为空");
    }
}
