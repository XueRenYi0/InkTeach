using System.Numerics;

namespace InkEngine;

/// <summary>PPT 控件条上"点到了哪一块"。</summary>
internal enum PptBarZone
{
    None = 0,
    LeftArrow,      // ◀ 上一页
    RightArrow,     // ▶ 下一页
    Page,           // 页码格：**点它直接开页号面板**；按下后移动 = 拖条
    Menu,           // 最右 ☰：**点它开 / 收菜单**
    // ---- 下面三档只在文档模式（PDF / 图片）里有：缩放组插在页码组和 ☰ 之间 ----
    ZoomOut,        // −：缩小一档
    ZoomPct,        // 百分比：**点它开缩放档位浮层**
    ZoomIn,         // ＋：放大一档
}

/// <summary>
/// PPT 放映时贴在屏幕上的那**一条**控件：`[◀] [页码] [▶] │ [☰]`。
///
/// 沿革（用户 2026-09-26 / 09-27 / 10-02 / 11-15 五次拍板，设计过程见 计划-PPT条-单条与拖动.md）：
///   · 第一版参考 Inkeys：底部左、右**各一条** ＋ 可拖的进度条 ＋ 长按菜单；
///   · 第二版收成现在这样：**只留一条**、**删掉进度条**（改成"点页码弹面板跳页"）、
///     **加拖动位置**（默认左下角，重启还记得），长按仍然是菜单；
///   · 第三版（2026-09-27 晚）**删掉「⋮」**：它本来夹在页码和 ▶ 之间，把一对箭头
///     从中间劈开了（`◀ 3/12` 和 `▶` 看着不像一套），而且它是从页码格里**抠**出来的
///     ——页码数字按整格居中，悬停高亮只画在被抠小的那块上，两者差 14 逻辑像素。
///     删掉之后页码格恢复**黄金矩形**、数字与高亮对齐，三个热区各 ≈50~78 宽；
///   · 第四版（2026-10-02 第五轮）**取消长按入口**：单击页码 = 打开菜单，
///     原页号面板降为菜单第一项「指定页码跳转」——"跳页"和"退出"点一下都看得见，
///     长按整段空出来给"功能提示"的触摸长按（见 调研-悬停提示-Tooltip.md 10.3）。
///   · 第五版（2026-11-15，本轮）**菜单从页码格里拆出来**：页码格回归"点一下 = 页号面板"
///     （跳页是最高频动作，不该再借道菜单）；菜单有了自己的**三横线「☰」**按钮，长在
///     最右，三种模式（PPT / 白板 / 文档）共用。菜单**不再跟条同宽**（固定 180）：它是
///     一个独立浮层，和条同宽反而把四字条目撑得很空。条**不镜像**——不管拖到屏幕哪边，
///     永远是"页码在左、☰ 在最右"，老师的手感才固定。
///
/// 三条硬约束（改尺寸时别碰）：
///   · **高 = 主界面那条的高**（`InkUi.Tokens.BarHeight`）。引擎看不见界面工程的令牌
///     （分层纪律：依赖只能从上往下），所以这个数只能写两遍——靠 `--ppttest` 里
///     "两个数相等"那条断言兜住，不然改一个忘另一个；
///   · **页码格是黄金矩形**（`BarH × 1.618` = 78）——它是视觉主体，两端箭头是配角；
///   · 热区高度 ≥48（教室里是手指/笔，不是鼠标）。
///
/// 这个类**只有几何**：位置怎么算、点到了哪一块、面板摆在哪。
/// 状态（悬停 / 拖动 / 菜单与面板开合）在 `InkEngine`（见 Ppt.cs），绘制在 `Overlay`。
/// 尺度和命中都乘 DPI（物理像素）——浮层和选中操作条同一套规矩。
/// </summary>
internal static class PptBar
{
    // ---- 尺度（逻辑像素）----
    public const float BarH = 48f;
    public const float ArrowW = 40f;
    public const float PageCellW = BarH * 1.618f;            // ≈ 78（黄金矩形）
    public const float MenuBtnW = 40f;                       // 最右 ☰ 的点击格
    /// <summary>页码组与 ☰ 之间的分隔带：**1px 竖线画在正中间**，两边各留 3.5 的呼吸。
    /// 这一带不算热区（见 <see cref="ZoneAt"/>）——点在线上不动作，也不会漏成画布笔画。</summary>
    public const float MenuGapW = 8f;
    /// <summary>条宽（PPT / 白板）。文档模式另加一组缩放控件（见 <see cref="DocBarW"/>）。
    /// 现在的排布：`[◀] [页码] [▶] │ [☰]`。</summary>
    public const float BarW = ArrowW * 2f + PageCellW + MenuGapW + MenuBtnW;   // ≈ 206
    public const float EdgeMargin = 16f;                     // 默认位置离屏幕左/下边

    // ---- 文档模式的缩放组（2026-11-16）：`│ [−] [100% ▾] [+] │`
    // 只插在页码组和 ☰ **之间**（顺序固定、不镜像），PPT / 白板一个像素都不变。
    public const float ZoomBtnW = 40f;                       // − / ＋ 的点击格
    public const float ZoomPctW = 72f;                       // 百分比格（含 ▾）
    public const float ZoomGroupW = ZoomBtnW * 2f + ZoomPctW; // 152
    /// <summary>文档模式的条宽 = 206 + 152 + 8 = 366（多出来的一条分隔带把缩放组和 ☰ 分开）。</summary>
    public const float DocBarW = BarW + ZoomGroupW + MenuGapW;

    /// <summary>拖动的判定阈值（逻辑像素）：移动超过它 = 拖条；
    /// 没动过再松手 = 单击（开面板）。8 比界面的 `DragThreshold`(4) 松一点：
    /// 手指按住不动时多少会抖，卡太紧会把"想点页码"拖成"挪条"。</summary>
    public const float DragSlop = 8f;

    // ---- 页号面板（点页码格直接进来）----
    public const int GridCols = 10;          // 每行 10 个页号
    public const float GridCell = 44f;       // 格子 44×44（触摸点得中）
    public const float GridGap = 6f;
    public const float PanelPad = 8f;
    public const float PanelGap = 8f;        // 面板与条之间的缝

    // ---- 菜单（**点最右 ☰** 弹出来的那张）----
    // **宽度固定 180**（2026-11-15 用户定）：菜单是独立浮层，不跟条宽走。
    // 跟条同宽时条目左右各空一大截，"小巧精致"就没了；固定宽之后三种模式（PPT /
    // 白板 / 文档）的菜单是同一个盒子，换模式不跳尺寸。
    public const float MenuW = 180f;
    public const float MenuItemH = 40f;
    public const float MenuGap = 8f;
    public const float MenuPad = 6f;
    /// <summary>菜单离屏幕边至少留这么多（夹紧用；比条的 <see cref="EdgeMargin"/> 小一档，
    /// 因为菜单是"临时摊开的一张纸"，贴边一点无妨）。</summary>
    public const float MenuMargin = 12f;

    // ---- 缩放档位浮层（点百分比弹出来的那张）----
    /// <summary>浮层宽（**独立浮层**，不跟条同宽；和菜单同一套哲学）。</summary>
    public const float ZoomMenuW = 140f;
    public const float ZoomMenuItemH = 32f;
    public const float ZoomMenuGap = 8f;
    public const float ZoomMenuPad = 6f;
    /// <summary>锚在百分比格上：横向以它为中心，纵向**优先开在条上方**（和菜单 / 页号面板同方向）。</summary>
    public static RectF ZoomMenuRect(in RectF bar, int itemCount, float dpi, in RectF screen)
    {
        float w = ZoomMenuW * dpi;
        float h = (itemCount * ZoomMenuItemH + ZoomMenuPad * 2f) * dpi;
        float gap = ZoomMenuGap * dpi;
        float m = MenuMargin * dpi;

        var pct = ZoomPctCell(bar, dpi);
        float cx = (pct.MinX + pct.MaxX) * 0.5f;
        float x = cx - w * 0.5f;
        float minX = screen.MinX + m, maxX = screen.MaxX - m - w;
        if (maxX < minX) maxX = minX;
        x = Math.Clamp(x, minX, maxX);

        float minY = screen.MinY + m, maxY = screen.MaxY - m - h;
        if (maxY < minY) maxY = minY;
        float top = bar.MinY - gap - h;
        if (top < minY) top = bar.MaxY + gap;
        top = Math.Clamp(top, minY, maxY);
        return new RectF { MinX = x, MinY = top, MaxX = x + w, MaxY = top + h };
    }

    public static RectF ZoomMenuItemRect(in RectF menu, int index, float dpi)
    {
        float pad = ZoomMenuPad * dpi, h = ZoomMenuItemH * dpi;
        return new RectF
        {
            MinX = menu.MinX + pad, MinY = menu.MinY + pad + index * h,
            MaxX = menu.MaxX - pad, MaxY = menu.MinY + pad + (index + 1) * h,
        };
    }

    /// <summary>条宽：文档模式多一组缩放控件。</summary>
    public static float WidthFor(bool docZoom) => docZoom ? DocBarW : BarW;

    /// <summary>由左上角位置算矩形（位置是物理像素；<paramref name="docZoom"/> = 文档模式的宽条）。</summary>
    public static RectF RectAt(float x, float y, float dpi, bool docZoom = false) => new()
    {
        MinX = x, MinY = y,
        MaxX = x + WidthFor(docZoom) * dpi, MaxY = y + BarH * dpi,
    };

    /// <summary>默认位置：**屏幕左下角**（用户 2026-09-26 定的："因为 PPT 本来的工具
    /// 就在这里"——PPT 放映时自己的控制条也在左下角，老师的视线本来就在那儿）。</summary>
    public static Vector2 DefaultTopLeft(in RectF screen, float dpi)
        => new(screen.MinX + EdgeMargin * dpi, screen.MaxY - EdgeMargin * dpi - BarH * dpi);

    /// <summary>把位置夹进屏幕（离边至少 <see cref="EdgeMargin"/>）——
    /// **拖出去找不回来**是这个功能最坏的失败模式。</summary>
    public static Vector2 ClampTopLeft(float x, float y, in RectF screen, float dpi, bool docZoom = false)
    {
        float w = WidthFor(docZoom) * dpi, h = BarH * dpi, m = EdgeMargin * dpi;
        float minX = screen.MinX + m, maxX = screen.MaxX - m - w;
        if (maxX < minX) maxX = minX;                  // 屏幕比条还窄：钉在左边
        float minY = screen.MinY + m, maxY = screen.MaxY - m - h;
        if (maxY < minY) maxY = minY;
        return new Vector2(Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }

    /// <summary>◀ / ▶ 格（40 宽）。**从左边数**：翻页组永远在最左、不镜像；
    /// 右边那格落在页码格右边界，和文档模式有没有缩放组无关。</summary>
    public static RectF ArrowCell(in RectF bar, bool left, float dpi)
    {
        float a = ArrowW * dpi;
        float x = left ? bar.MinX : bar.MinX + (ArrowW + PageCellW) * dpi;
        return new RectF { MinX = x, MinY = bar.MinY, MaxX = x + a, MaxY = bar.MaxY };
    }

    /// <summary>页码格（中格，黄金矩形）——**点它开页号面板**，页码文字也在这块里居中。</summary>
    public static RectF MidCell(in RectF bar, float dpi)
    {
        float a = ArrowW * dpi;
        return new RectF
        {
            MinX = bar.MinX + a, MinY = bar.MinY,
            MaxX = bar.MinX + a + PageCellW * dpi, MaxY = bar.MaxY,
        };
    }

    /// <summary>最右的 ☰ 格（40 宽，热区整高）。</summary>
    public static RectF MenuCell(in RectF bar, float dpi)
    {
        float m = MenuBtnW * dpi;
        return new RectF { MinX = bar.MaxX - m, MinY = bar.MinY, MaxX = bar.MaxX, MaxY = bar.MaxY };
    }

    /// <summary>页码组与 ☰ 之间那条 1px 竖线的中心 x（绘制用；命中把整条分隔带留空）。</summary>
    public static float DividerX(in RectF bar, float dpi)
        => bar.MaxX - (MenuBtnW + MenuGapW * 0.5f) * dpi;

    /// <summary>页码组与缩放组之间那条 1px 竖线的中心 x。只有文档模式的宽条才有它。</summary>
    public static float DividerLeftX(in RectF bar, float dpi)
        => bar.MinX + (ArrowW * 2f + PageCellW + MenuGapW * 0.5f) * dpi;

    /// <summary>缩放组的起点（减号左边界）。</summary>
    private static float ZoomStartX(in RectF bar, float dpi)
        => bar.MinX + (ArrowW * 2f + PageCellW + MenuGapW) * dpi;

    /// <summary>− 格（缩放组最左 40）。</summary>
    public static RectF ZoomOutCell(in RectF bar, float dpi)
    {
        float x = ZoomStartX(bar, dpi);
        return new RectF { MinX = x, MinY = bar.MinY, MaxX = x + ZoomBtnW * dpi, MaxY = bar.MaxY };
    }

    /// <summary>百分比格（缩放组中间 72；百分比文字和 ▾ 都在这块里）。</summary>
    public static RectF ZoomPctCell(in RectF bar, float dpi)
    {
        float x = ZoomStartX(bar, dpi) + ZoomBtnW * dpi;
        return new RectF { MinX = x, MinY = bar.MinY, MaxX = x + ZoomPctW * dpi, MaxY = bar.MaxY };
    }

    /// <summary>＋ 格（缩放组最右 40）。</summary>
    public static RectF ZoomInCell(in RectF bar, float dpi)
    {
        float x = ZoomStartX(bar, dpi) + (ZoomBtnW + ZoomPctW) * dpi;
        return new RectF { MinX = x, MinY = bar.MinY, MaxX = x + ZoomBtnW * dpi, MaxY = bar.MaxY };
    }

    /// <summary>
    /// 点到了哪一块（不重叠也不留缝）。
    /// <paramref name="docZoom"/> = 文档模式：多出 `[−][百分比][+]` 三档；
    /// 两条分隔带（8 宽）都**不是热区**——调用方仍然要把它们吃掉。
    /// </summary>
    public static PptBarZone ZoneAt(in RectF bar, float x, float y, float dpi, bool docZoom = false)
    {
        if (!bar.Contains(x, y)) return PptBarZone.None;

        float mb = MenuBtnW * dpi;
        if (x >= bar.MaxX - mb) return PptBarZone.Menu;      // 最右 ☰

        // 分隔带（8 宽）不是热区：它只是两个组之间的呼吸。指针落在这儿什么也不做
        // （调用方仍然把它吃掉，绝不能让这一下漏成画布上的笔画）。
        float groupEnd = bar.MaxX - (MenuBtnW + MenuGapW) * dpi;
        if (x >= groupEnd) return PptBarZone.None;

        float a = ArrowW * dpi;
        if (x < bar.MinX + a) return PptBarZone.LeftArrow;

        // 页码组右边界（文档模式里它后面还有分隔带 + 缩放组）。
        float pageEnd = bar.MinX + (ArrowW * 2f + PageCellW) * dpi;
        if (x >= pageEnd)
        {
            if (!docZoom) return PptBarZone.RightArrow;      // PPT / 白板：▶ 就在这儿
            // 文档模式：先过一条分隔带，再是 [−][%][+]。
            float zx = ZoomStartX(bar, dpi);
            if (x < zx) return PptBarZone.None;              // 页码组 ↔ 缩放组的分隔带
            float zoomEnd = zx + ZoomGroupW * dpi;
            if (x >= zoomEnd) return PptBarZone.None;        // 缩放组 ↔ ☰ 的分隔带
            if (x < zx + ZoomBtnW * dpi) return PptBarZone.ZoomOut;
            if (x >= zoomEnd - ZoomBtnW * dpi) return PptBarZone.ZoomIn;
            return PptBarZone.ZoomPct;
        }

        if (x >= pageEnd - a) return PptBarZone.RightArrow;
        return PptBarZone.Page;
    }

    // ---- 菜单（**点 ☰** 弹出来的那张）----

    /// <summary>
    /// 菜单矩形（2026-11-15 定的锚法：**外缘对齐**，条本身不镜像）。
    ///   · 条在屏幕左半边 → 菜单**左缘**贴条左缘（往右摊开）；
    ///   · 条在屏幕右半边 → 菜单**右缘**贴条右缘（往左摊开）；
    ///   · 两边都夹进屏幕（离边 <see cref="MenuMargin"/>）；
    ///   · 优先开在条的上方；上方放不下（条贴着屏幕顶）才改开在下方。
    /// 为什么按"条中心在哪半边"而不是"右边放不放得下"：后者的判定会随菜单宽度漂，
    /// 条拖到某条线上菜单就突然换边；按屏幕中线分，拖到哪儿都是同一个手感。
    /// </summary>
    public static RectF MenuRect(in RectF bar, int itemCount, float dpi, in RectF screen)
    {
        float w = MenuW * dpi;
        float h = (itemCount * MenuItemH + MenuPad * 2f) * dpi;
        float gap = MenuGap * dpi;
        float m = MenuMargin * dpi;

        float barCx = (bar.MinX + bar.MaxX) * 0.5f;
        float scrCx = (screen.MinX + screen.MaxX) * 0.5f;
        float x = barCx <= scrCx ? bar.MinX : bar.MaxX - w;      // 外缘对齐

        float minX = screen.MinX + m, maxX = screen.MaxX - m - w;
        if (maxX < minX) maxX = minX;                            // 屏幕比菜单还窄：钉在左边
        x = Math.Clamp(x, minX, maxX);

        float minY = screen.MinY + m, maxY = screen.MaxY - m - h;
        if (maxY < minY) maxY = minY;
        float top = bar.MinY - gap - h;                          // 先试上方
        if (top < minY) top = bar.MaxY + gap;                    // 上方没地方 → 开在下方
        top = Math.Clamp(top, minY, maxY);
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
