// [职责] 覆盖窗口的坐标换算（画布 ↔ 窗口 ↔ 屏幕）与窗口裁剪：纯数学、无状态，OverlayWindow 与单测共用。
// [自检] --unittest OverlayCoords

namespace InkEngine;

/// <summary>
/// 覆盖窗口用到的三套坐标，以及它们之间的换算（2026-11-16 残影评审 P0-2）：
///
///   · **画布坐标**：文档 / 笔迹 / 选中框；相机（缩放 + 平移）作用在它上面。
///   · **屏幕坐标**：虚拟桌面物理像素；界面（工具条）的矩形、窗口原点 OriginX/OriginY 都在这里。
///   · **窗口坐标**：0..Width × 0..Height；后缓冲、`PushAxisAlignedClip`、`Present1` 的脏矩形吃的都是它。
///
/// **`_frameDirty` 一律是窗口坐标**。以前一半是屏幕坐标（整屏矩形、界面、HUD、滚动条）、
/// 一半是窗口坐标（画布矩形换算来的），裁剪又按屏幕范围夹——主屏原点是 (0,0) 看不出来，
/// 副屏（扩展模式投影仪，OriginX = 1920）上界面"只画不擦"、内容层基本贴不上。
/// 抽成纯函数是为了能在不起窗口、不碰 GPU 的单测里把副屏原点下的换算钉住。
/// </summary>
internal static class OverlayCoords
{
    /// <summary>画布 → 窗口（先缩放、再平移、再减窗口原点）。</summary>
    internal static float CanvasToWindow(float c, float scale, float viewOffset, int origin)
        => c * scale + viewOffset - origin;

    /// <summary>窗口 → 画布（<see cref="CanvasToWindow"/> 的逆）。</summary>
    internal static float WindowToCanvas(float w, float scale, float viewOffset, int origin)
        => (w + origin - viewOffset) / scale;

    /// <summary>屏幕矩形 → 窗口矩形（减窗口原点）。空矩形原样返回。</summary>
    internal static RectF ScreenToWindow(in RectF r, int originX, int originY)
        => r.IsEmpty ? RectF.Empty : new RectF
        {
            MinX = r.MinX - originX, MinY = r.MinY - originY,
            MaxX = r.MaxX - originX, MaxY = r.MaxY - originY,
        };

    /// <summary>整个窗口（窗口坐标）。</summary>
    internal static RectF FullWindow(int width, int height)
        => new() { MinX = 0, MinY = 0, MaxX = width, MaxY = height };

    /// <summary>窗口矩形夹进 [0,Width]×[0,Height]；完全在外面就是空。</summary>
    internal static RectF ClipToWindow(in RectF r, int width, int height)
    {
        float l = MathF.Max(r.MinX, 0f);
        float t = MathF.Max(r.MinY, 0f);
        float rr = MathF.Min(r.MaxX, width);
        float b = MathF.Min(r.MaxY, height);
        if (rr < l || b < t) return RectF.Empty;
        return new RectF { MinX = l, MinY = t, MaxX = rr, MaxY = b };
    }

    /// <summary>矩形是不是整个落在窗口里（Present 里校验脏区坐标系用，容 1 像素取整误差）。</summary>
    internal static bool InsideWindow(in RectF r, int width, int height)
        => !r.IsEmpty && r.MinX >= -1f && r.MinY >= -1f && r.MaxX <= width + 1f && r.MaxY <= height + 1f;

    /// <summary>这个窗口看得见的那块画布（画布坐标）= 窗口 (0,0)..(W,H) 反算回画布。</summary>
    internal static RectF VisibleCanvas(int width, int height, int originX, int originY,
                                        float scale, float viewOffsetX, float viewOffsetY)
        => new()
        {
            MinX = WindowToCanvas(0f, scale, viewOffsetX, originX),
            MinY = WindowToCanvas(0f, scale, viewOffsetY, originY),
            MaxX = WindowToCanvas(width, scale, viewOffsetX, originX),
            MaxY = WindowToCanvas(height, scale, viewOffsetY, originY),
        };
}
