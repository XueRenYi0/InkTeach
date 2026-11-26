// [职责] 屏幕左右边缘的整页翻页条：三格几何（上翻 / 页码 / 下翻）。几何与命中共用一份。
// [入口] 引擎 PageEdge* 系列（DocImport.Pan.cs）；绘制在 Overlay.Ui.cs 的 DrawPageEdges。
// [读写状态] 无自己的状态——显隐读 PptMode/BoardOn/DocView，页码读 BarPageNow/BarTotal。
// [落盘 Key] ui.pageEdge（"1" = 开；默认关，老用户无感）。
// [测试] --ppttest（几何 + 上/下翻各点一次看命令计数 + 默认关）

using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// **左右边缘的整页翻页条**（2026-11-25，自研）。
///
/// 显示范围（和 PR 的差异，用户 2026-11-25 拍板）：
///   · **PPT 放映** ✓（放映本来就是"一页一屏、点左右翻"，左右边缘是肌肉记忆）
///   · **白板** ✓（连续画布、页 = 屏幕，边缘一般空着）
///   · **PDF / 图片文档 ✗**——文档底部已经有翻页条 + 右下缩放条，再加左右两条
///     就是三套翻页入口（违反 AGENTS.md「主入口可见时不在别处重复同一入口」）；
///     而且文档页码是按"视口中心所在页"算的（`BarPageNow`），连续滚动时
///     中格数字会在跨页缝那一刻突变，"当前第几页"在 PDF 里没有确定答案。
///
/// 形态：每边三个 48×48 圆角方格竖排、整体垂直居中——
///   · 上格 ∧ = 上一页；中格 = 当前页 / 总页数（**只读**）；下格 ∨ = 下一页。
/// 平时**几乎不可见**（只有极淡的轮廓），指针靠近 / 按下才显形——它正压在画布
/// 左右中部，常驻不透明面板会被掌根误触，也挡内容（用户 2026-11-25 的顾虑）。
///
/// 穿透下照样能点：引擎给它开了一块 1/255 透明的**接输入小窗**
/// （见 DocImport.Pan.cs 的 SyncPageEdgeInputWindow）——这是 PR 那个实现最大的坑
/// （WS_EX_TRANSPARENT 让系统跳过命中测试，NCHITTEST 豁免根本执行不到）。
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

    /// <summary>
    /// 显形判定：指针离整组多远以内算"靠近"（逻辑像素）。
    /// 比方格本身外扩一圈——手指/笔往那儿移动时先浮出来，等真按上时已经看得见。
    /// </summary>
    public const float RevealPad = 28f;

    /// <summary>指针没靠近时的不透明度（几乎只剩一点轮廓，知道那儿有东西但不挡内容）。</summary>
    public const float IdleAlpha = 0.10f;
    /// <summary>靠近时的目标不透明度。</summary>
    public const float HoverAlpha = 1f;
    /// <summary>淡入 / 淡出时长（毫秒）。</summary>
    public const float RevealMs = 120f;

    /// <summary>第 <paramref name="index"/> 格（0 = 上翻、1 = 页码、2 = 下翻）。</summary>
    public static RectF CellRect(in RectF screen, bool left, int index, float dpi)
    {
        float c = Cell * dpi, gap = Gap * dpi, m = EdgeMargin * dpi;
        float h = GroupH * dpi;
        float y0 = (screen.MinY + screen.MaxY) * 0.5f - h * 0.5f;
        float x = left ? screen.MinX + m : screen.MaxX - m - c;
        float y = y0 + index * (c + gap);
        return new RectF { MinX = x, MinY = y, MaxX = x + c, MaxY = y + c };
    }

    /// <summary>整组矩形（物理像素：垂直居中、贴屏幕左/右边）。</summary>
    public static RectF GroupRect(in RectF screen, bool left, float dpi)
    {
        var top = CellRect(screen, left, 0, dpi);
        var bottom = CellRect(screen, left, 2, dpi);
        return new RectF
        {
            MinX = top.MinX, MinY = top.MinY,
            MaxX = top.MaxX, MaxY = bottom.MaxY,
        };
    }

    /// <summary>两边整组的并集（脏区 / 输入小窗 / 避让界面都用它，**只算一次**）。</summary>
    public static RectF BothGroupRect(in RectF screen, float dpi)
    {
        var l = GroupRect(screen, true, dpi);
        var r = GroupRect(screen, false, dpi);
        return new RectF
        {
            MinX = l.MinX, MinY = l.MinY,
            MaxX = r.MaxX, MaxY = r.MaxY,
        };
    }

    /// <summary>点在任意一边的整组上吗（**不含** RevealPad：命中就是看得见的那一块）。</summary>
    public static bool Contains(float x, float y, in RectF screen, float dpi)
        => GroupRect(screen, true, dpi).Contains(x, y)
        || GroupRect(screen, false, dpi).Contains(x, y);

    /// <summary>点在"靠近就会显形"的那一圈里吗（外扩 RevealPad，只用于显隐，不用于命中）。</summary>
    public static bool Near(float x, float y, in RectF screen, float dpi)
    {
        float pad = RevealPad * dpi;
        return GroupRect(screen, true, dpi).Inflate(pad).Contains(x, y)
            || GroupRect(screen, false, dpi).Inflate(pad).Contains(x, y);
    }

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
