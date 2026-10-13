using System;

namespace InkEngine;

/// <summary>
/// **图库面板的布局**（几何只有这一份：绘制、命中、自检都问它）。
///
/// 为什么单独一个类、不写在绘制里：命中判定和绘制必须用同一套尺寸，分开写迟早差几个像素，
/// 表现就是"看得见格子却点不中"——这条教训在 <see cref="SelectionHandles"/> 的注释里写过。
///
/// 位置：**贴着工具条上沿往上长**（工具条那一块由界面报给引擎，见
/// <see cref="InkEngine.UiQueryBoundsNow"/>）。为什么不做成"跟着鼠标/居中弹出"：
/// 老师点的是**图形面板最后那一段**，面板从那儿往上长，视线不用跳（参考实现的
/// 图形面板也是贴着工具条）。单位一律是**画布坐标（＝屏幕物理像素）**。
/// </summary>
internal static class LibraryLayout
{
    /// <summary>内边距。</summary>
    public const float PadLogical = 12f;
    /// <summary>格子大小。比工具条的图标格（38×30）大一圈：格子里的内容是"一张图"，不是图标。</summary>
    public const float CellWLogical = 64f;
    public const float CellHLogical = 56f;
    public const float CellGapLogical = 8f;
    /// <summary>一行几格。4 列 × 64 ＋ 3 × 8 ＋ 两边 12 = 328 逻辑像素宽，和主条一个量级。</summary>
    public const int Cols = 4;
    /// <summary>标题行高度（"图库 · N 个" ＋ 右边的「整理」「✕」都在这一行里）。</summary>
    public const float HeaderHLogical = 26f;
    /// <summary>面板底边离工具条上沿的空隙。</summary>
    public const float GapAboveUiLogical = 16f;
    /// <summary>最多画几行（再多就翻不下）。超过的条目这一版不显示，标题上会写出来。</summary>
    public const int MaxRows = 4;

    /// <summary>按条目数算出来的面板矩形（画布坐标）。<paramref name="uiTop"/> = 工具条上沿。</summary>
    public static RectF PanelRect(float screenMinX, float screenMaxX, float uiTop,
                                 float dpi, int count)
    {
        int rows = Math.Clamp((count + Cols - 1) / Cols, 1, MaxRows);
        float pad = PadLogical * dpi, gap = CellGapLogical * dpi;
        float w = pad * 2 + Cols * CellWLogical * dpi + (Cols - 1) * gap;
        float h = pad * 2 + HeaderHLogical * dpi + rows * CellHLogical * dpi + (rows - 1) * gap;

        float maxX = MathF.Min(screenMaxX, screenMinX + w);
        float minX = maxX - w;
        // 水平居中（相对整块虚拟桌面），底边贴着工具条上沿往上长
        float cx = (screenMinX + screenMaxX) * 0.5f;
        minX = MathF.Max(screenMinX, cx - w * 0.5f);
        maxX = minX + w;

        float maxY = uiTop - GapAboveUiLogical * dpi;
        float minY = maxY - h;
        return new RectF { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
    }

    public static bool Contains(in RectF panel, float x, float y)
        => x >= panel.MinX && x <= panel.MaxX && y >= panel.MinY && y <= panel.MaxY;

    /// <summary>标题行。</summary>
    public static RectF HeaderRect(in RectF panel, float dpi)
        => new()
        {
            MinX = panel.MinX + PadLogical * dpi,
            MinY = panel.MinY + PadLogical * dpi,
            MaxX = panel.MaxX - PadLogical * dpi,
            MaxY = panel.MinY + PadLogical * dpi + HeaderHLogical * dpi,
        };

    /// <summary>「整理」按钮（触摸屏删格子的入口，参考实现同款）。</summary>
    public static RectF EditRect(in RectF panel, float dpi)
    {
        float h = 20f * dpi, w = 46f * dpi;
        var head = HeaderRect(panel, dpi);
        return new RectF
        {
            MinX = head.MaxX - (w + 6f * dpi) - 22f * dpi,
            MinY = (head.MinY + head.MaxY) * 0.5f - h * 0.5f,
            MaxX = head.MaxX - 22f * dpi,
            MaxY = (head.MinY + head.MaxY) * 0.5f + h * 0.5f,
        };
    }

    /// <summary>关闭按钮「✕」，在最右边。</summary>
    public static RectF CloseRect(in RectF panel, float dpi)
    {
        float h = 20f * dpi;
        var head = HeaderRect(panel, dpi);
        return new RectF
        {
            MinX = head.MaxX - h,
            MinY = (head.MinY + head.MaxY) * 0.5f - h * 0.5f,
            MaxX = head.MaxX,
            MaxY = (head.MinY + head.MaxY) * 0.5f + h * 0.5f,
        };
    }

    /// <summary>第 <paramref name="index"/> 个格子（从左到右、从上到下）。</summary>
    public static RectF CellRect(in RectF panel, float dpi, int index)
    {
        int col = index % Cols, row = index / Cols;
        float pad = PadLogical * dpi, gap = CellGapLogical * dpi;
        float w = CellWLogical * dpi, h = CellHLogical * dpi;
        float x = panel.MinX + pad + col * (w + gap);
        float y = panel.MinY + pad + HeaderHLogical * dpi + gap + row * (h + gap);
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>命中了第几个格子（-1 = 没中）。</summary>
    public static int CellAt(in RectF panel, float dpi, int count, float x, float y)
    {
        int rows = Math.Clamp((count + Cols - 1) / Cols, 1, MaxRows);
        int max = Math.Min(count, rows * Cols);
        for (int i = 0; i < max; i++)
        {
            // 格子之间留了 8 逻辑像素的空隙，命中就按格子本身算（空隙不算任何一格）
            var c = CellRect(panel, dpi, i);
            if (x >= c.MinX && x <= c.MaxX && y >= c.MinY && y <= c.MaxY) return i;
        }
        return -1;
    }

    /// <summary>整理模式下那一颗红 ✕ 的矩形（叠在格子右上角）。</summary>
    public static RectF BadgeRect(in RectF cell, float dpi)
    {
        float s = 16f * dpi;
        return new RectF { MinX = cell.MaxX - s, MinY = cell.MinY, MaxX = cell.MaxX, MaxY = cell.MinY + s };
    }
}
