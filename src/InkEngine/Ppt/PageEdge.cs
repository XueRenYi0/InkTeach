// [职责] 屏幕左右边缘的整页翻页条：三格几何（上翻 / 页码 / 下翻），绘制与命中共用。
// [测试] --ppttest（几何 + 上/下翻各点一次，看 PPT 命令计数）

using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// **左右边缘**的整页翻页条（2026-11-25 定版；PPT / 白板 / 文档三种模式显示）：
///
/// 每边三个圆角方格竖着排、整体垂直居中——
///   · 上格 ∧ = 上一页；
///   · 中格 = 当前页码（**只读**，点了不动作）；
///   · 下格 ∨ = 下一页。
/// 颜色/透明度跟工具栏面板同一套（全局窗口透明度会带着一起淡）。
/// 显不显由 <c>InkEngine.PageEdgeVisible</c> 管；分派在 <c>PageEdgeFlip</c>（白板/文档 = 相机翻页，PPT = COM）。
///
/// ⚠ 几何函数**同时**给绘制和命中使用（同 PptBar 的规矩）：不许另写一套坐标。
/// </summary>
internal static class PageEdge
{
    // ---- 尺度（逻辑像素）----
    /// <summary>方格边长。</summary>
    public const float Cell = 48f;
    /// <summary>方格之间的缝。</summary>
    public const float Gap = 6f;
    /// <summary>圆角半径。</summary>
    public const float Radius = 12f;
    /// <summary>离屏幕左/右边缘的距离（圆角要看得见，不能贴死）。</summary>
    public const float EdgeMargin = 8f;
    /// <summary>整组高度：三格 + 两条缝。</summary>
    public const float GroupH = Cell * 3f + Gap * 2f;

    /// <summary>整组矩形（物理像素：垂直居中、贴屏幕左/右边）。</summary>
    public static RectF GroupRect(in RectF screen, bool left, float dpi)
    {
        float c = Cell * dpi, h = GroupH * dpi, m = EdgeMargin * dpi;
        float y = (screen.MinY + screen.MaxY) * 0.5f - h * 0.5f;
        float x = left ? screen.MinX + m : screen.MaxX - m - c;
        return new RectF { MinX = x, MinY = y, MaxX = x + c, MaxY = y + h };
    }

    /// <summary>第 <paramref name="index"/> 格（0 = 上翻、1 = 页码、2 = 下翻）。</summary>
    public static RectF CellRect(in RectF screen, bool left, int index, float dpi)
    {
        var g = GroupRect(screen, left, dpi);
        float c = Cell * dpi, gap = Gap * dpi;
        float y = g.MinY + index * (c + gap);
        return new RectF { MinX = g.MinX, MinY = y, MaxX = g.MaxX, MaxY = y + c };
    }

    /// <summary>点在任意一边的整组上吗。</summary>
    public static bool Contains(float x, float y, in RectF screen, float dpi)
        => GroupRect(screen, true, dpi).Contains(x, y) || GroupRect(screen, false, dpi).Contains(x, y);

    /// <summary>点在哪一边：-1 左、+1 右、0 都没点中。</summary>
    public static int SideAt(float x, float y, in RectF screen, float dpi)
    {
        if (GroupRect(screen, true, dpi).Contains(x, y)) return -1;
        if (GroupRect(screen, false, dpi).Contains(x, y)) return 1;
        return 0;
    }

    /// <summary>某一格里点的第几格：0 上翻 / 1 页码 / 2 下翻 / -1 落在缝里或组外。</summary>
    public static int CellAt(float x, float y, in RectF screen, bool left, float dpi)
    {
        for (int i = 0; i < 3; i++)
            if (CellRect(screen, left, i, dpi).Contains(x, y)) return i;
        return -1;
    }
}
