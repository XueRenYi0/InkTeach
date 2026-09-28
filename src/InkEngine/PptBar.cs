using System.Numerics;

namespace InkEngine;

/// <summary>PPT 控件条上"点到了哪一块"。</summary>
internal enum PptBarZone
{
    None = 0,
    LeftArrow,      // ◀ 上一页
    RightArrow,     // ▶ 下一页
    Page,           // 页码格：**点它弹页号面板**（快速跳页）、**长按它开菜单**
}

/// <summary>
/// PPT 放映时贴在屏幕上的那**一条**控件：`[◀] [页码] [▶]`。
///
/// 沿革（用户 2026-09-26 / 2026-09-27 三次拍板，设计过程见 计划-PPT条-单条与拖动.md）：
///   · 第一版参考 Inkeys：底部左、右**各一条** ＋ 可拖的进度条 ＋ 长按菜单；
///   · 第二版收成现在这样：**只留一条**、**删掉进度条**（改成"点页码弹面板跳页"）、
///     **加拖动位置**（默认左下角，重启还记得），长按仍然是菜单；
///   · 第三版（2026-09-27 晚）**删掉「⋮」**：它本来夹在页码和 ▶ 之间，把一对箭头
///     从中间劈开了（`◀ 3/12` 和 `▶` 看着不像一套），而且它是从页码格里**抠**出来的
///     ——页码数字按整格居中，悬停高亮只画在被抠小的那块上，两者差 14 逻辑像素。
///     删掉之后页码格恢复**黄金矩形**、数字与高亮对齐，三个热区各 ≈50~78 宽。
///     代价：菜单只剩**长按页码**一条路（`⋮` 走的是"点一下"）——用**每次放映都提示**
///     的短引导兜住（见 `InkEngine.PptHintVisible`，用户 2026-09-27 定）。
///
/// 三条硬约束（改尺寸时别碰）：
///   · **高 = 主界面那条的高**（`InkUi.Tokens.BarHeight`）。引擎看不见界面工程的令牌
///     （分层纪律：依赖只能从上往下），所以这个数只能写两遍——靠 `--ppttest` 里
///     "两个数相等"那条断言兜住，不然改一个忘另一个；
///   · **页码格是黄金矩形**（`BarH × 1.618` = 78）——它是视觉主体，两端箭头是配角；
///   · 热区高度 ≥48（教室里是手指/笔，不是鼠标）。
///
/// 这个类**只有几何**：位置怎么算、点到了哪一块、面板摆在哪。
/// 状态（悬停 / 拖动 / 长按 / 面板开合）在 `InkEngine`（见 Ppt.cs），绘制在 `Overlay`。
/// 尺度和命中都乘 DPI（物理像素）——浮层和选中操作条同一套规矩。
/// </summary>
internal static class PptBar
{
    // ---- 尺度（逻辑像素）----
    public const float BarH = 48f;
    public const float ArrowW = 40f;
    public const float BarW = ArrowW * 2f + BarH * 1.618f;   // ≈ 158（= 40 + 78 + 40）
    public const float EdgeMargin = 16f;                     // 默认位置离屏幕左/下边

    /// <summary>长按多久算数（毫秒）。600 是 Windows 触摸"长按"那一档的量级，
    /// 也和系统笔设置里"长按判定等待 300ms"同一条手感线（我们更保守，免得误触）。</summary>
    public const double LongPressMs = 600;

    /// <summary>拖动的判定阈值（逻辑像素）：移动超过它才算"拖动"，
    /// 否则松手时算"长按没动过"→ 弹菜单。8 比界面的 `DragThreshold`(4) 松一点：
    /// 手指按住不动时多少会抖，卡太紧会变成"想弹菜单却拖走了条"。</summary>
    public const float DragSlop = 8f;

    // ---- 页号面板（点页码弹出来的那张）----
    public const int GridCols = 10;          // 每行 10 个页号
    public const float GridCell = 44f;       // 格子 44×44（触摸点得中）
    public const float GridGap = 6f;
    public const float PanelPad = 8f;
    public const float PanelGap = 8f;        // 面板与条之间的缝

    // ---- 长按菜单（**长按页码格**弹出来的那张）----
    // **宽度 = 条宽**：菜单是从条上长出来的，一样宽看着才像一套（用户要的"小巧精致"）。
    public const float MenuW = BarW;
    public const float MenuItemH = 40f;
    public const float MenuGap = 8f;
    public const float MenuPad = 6f;

    /// <summary>由左上角位置算矩形（位置是物理像素）。</summary>
    public static RectF RectAt(float x, float y, float dpi) => new()
    {
        MinX = x, MinY = y,
        MaxX = x + BarW * dpi, MaxY = y + BarH * dpi,
    };

    /// <summary>默认位置：**屏幕左下角**（用户 2026-09-26 定的："因为 PPT 本来的工具
    /// 就在这里"——PPT 放映时自己的控制条也在左下角，老师的视线本来就在那儿）。</summary>
    public static Vector2 DefaultTopLeft(in RectF screen, float dpi)
        => new(screen.MinX + EdgeMargin * dpi, screen.MaxY - EdgeMargin * dpi - BarH * dpi);

    /// <summary>把位置夹进屏幕（离边至少 <see cref="EdgeMargin"/>）——
    /// **拖出去找不回来**是这个功能最坏的失败模式。</summary>
    public static Vector2 ClampTopLeft(float x, float y, in RectF screen, float dpi)
    {
        float w = BarW * dpi, h = BarH * dpi, m = EdgeMargin * dpi;
        float minX = screen.MinX + m, maxX = screen.MaxX - m - w;
        if (maxX < minX) maxX = minX;                  // 屏幕比条还窄：钉在左边
        float minY = screen.MinY + m, maxY = screen.MaxY - m - h;
        if (maxY < minY) maxY = minY;
        return new Vector2(Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }

    /// <summary>页码格（中格）——**点它弹页号面板、长按它开菜单**，页码文字也在这块里居中。</summary>
    public static RectF MidCell(in RectF bar, float dpi)
    {
        float a = ArrowW * dpi;
        return new RectF { MinX = bar.MinX + a, MinY = bar.MinY, MaxX = bar.MaxX - a, MaxY = bar.MaxY };
    }

    /// <summary>点到了哪一块（三块一人一半，不重叠也不留缝）。</summary>
    public static PptBarZone ZoneAt(in RectF bar, float x, float y, float dpi)
    {
        if (!bar.Contains(x, y)) return PptBarZone.None;
        float a = ArrowW * dpi;
        if (x < bar.MinX + a) return PptBarZone.LeftArrow;
        if (x > bar.MaxX - a) return PptBarZone.RightArrow;
        return PptBarZone.Page;
    }

    // ---- 长按菜单（**长按页码格**弹出来的那张，2026-09-27 起没有别的入口）----

    public static RectF MenuRect(in RectF bar, int itemCount, float dpi, in RectF screen)
    {
        float w = MenuW * dpi;
        float h = (itemCount * MenuItemH + MenuPad * 2f) * dpi;
        float gap = MenuGap * dpi;
        float slack = 4f * dpi;
        float x = Math.Clamp(bar.MinX, screen.MinX + slack, Math.Max(screen.MinX + slack, screen.MaxX - slack - w));
        float top = bar.MinY - gap - h;
        float minTop = screen.MinY + slack;
        if (top < minTop) top = minTop;                // 条太靠上就往回收，别出屏幕
        return new RectF { MinX = x, MinY = top, MaxX = x + w, MaxY = top + h };
    }

    public static RectF MenuItemRect(in RectF menu, int index, float dpi)
    {
        float pad = MenuPad * dpi, h = MenuItemH * dpi;
        return new RectF
        {
            MinX = menu.MinX + pad, MinY = menu.MinY + pad + index * h,
            MaxX = menu.MaxX - pad, MaxY = menu.MinY + pad + (index + 1) * h,
        };
    }

    // ---- 页号面板 ---------------------------------------------------------------

    /// <summary>面板几行（每行 <see cref="GridCols"/> 个页号）。</summary>
    public static int PanelRows(int pageCount) => Math.Max(1, (pageCount + GridCols - 1) / GridCols);

    /// <summary>面板实际用几列（页数不够一行时不留空列）。</summary>
    public static int PanelCols(int pageCount) => Math.Min(GridCols, Math.Max(1, pageCount));

    /// <summary>
    /// 页号面板的位置：**从条的上方往上长**（和"更多"抽屉、图库面板同一套方向）。
    /// 页数多时面板会很高，顶到屏幕顶就贴住（不再往上）；
    /// 横向夹在屏幕内（条在右下角时面板会往左让）。
    /// </summary>
    public static RectF PanelRect(in RectF bar, int pageCount, float dpi, in RectF screen)
    {
        int rows = PanelRows(pageCount), cols = PanelCols(pageCount);
        float w = (cols * GridCell + (cols - 1) * GridGap + PanelPad * 2f) * dpi;
        float h = (rows * GridCell + (rows - 1) * GridGap + PanelPad * 2f) * dpi;
        float gap = PanelGap * dpi;
        float slack = 4f * dpi;
        float x = Math.Clamp(bar.MinX, screen.MinX + slack, Math.Max(screen.MinX + slack, screen.MaxX - slack - w));
        float top = bar.MinY - gap - h;
        float minTop = screen.MinY + slack;
        if (top < minTop) top = minTop;
        return new RectF { MinX = x, MinY = top, MaxX = x + w, MaxY = top + h };
    }

    /// <summary>面板里第 <paramref name="index"/> 个格子（0 起，对应页号 index+1）。
    /// **绘制和命中读同一个**——各算一遍迟早差几个像素，那表现就是"看得见却点不中"。</summary>
    public static RectF CellRect(in RectF panel, int index, float dpi)
    {
        int row = index / GridCols, col = index % GridCols;
        float pad = PanelPad * dpi, c = GridCell * dpi, gap = GridGap * dpi;
        float x = panel.MinX + pad + col * (c + gap);
        float y = panel.MinY + pad + row * (c + gap);
        return new RectF { MinX = x, MinY = y, MaxX = x + c, MaxY = y + c };
    }

    /// <summary>面板里有没有第 <paramref name="index"/> 个格子（页数不是 <see cref="GridCols"/>
    /// 的整数倍时，最后一行是缺的——不判的话会画出一排空格子）。</summary>
    public static bool HasCell(int index, int pageCount) => index >= 0 && index < pageCount;
}
