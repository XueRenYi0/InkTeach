using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace DesignSheet;

/// <summary>
/// 把「批注工具条」的形态画成一张设计稿 PNG（design/界面-设计稿v0-形态.png）。
///
/// 为什么单独做一个出图工具、而不是直接写在引擎里：
/// 这一版要讨论的是**形态与尺寸**（球多大、条多高、色带怎么弹、透明度几档），
/// 还没到"接进引擎"那一步。稿子能一改一跑、随时对比，比在真程序里边改边看快。
/// 所有尺寸都是**逻辑像素 1:1**，和引擎的坐标空间一致，所以稿上的数字能直接抄进代码。
///
/// 图标路径由 tools/gen-ui-icons.ps1 从 Fluent 图标库生成（见 IconPaths.g.cs）。
/// 这个工具不进产品，只用来出图。
/// </summary>
internal static partial class Program
{
    // ---- 设计令牌（这一版要拍的数都在这里） ------------------------------

    const double BarH = 56;            // 工具条高度
    const double Btn = 40;             // 按钮命中区
    const double IconSize = 20;        // 图标绘制尺寸（24 网格缩到 20）
    const double Gap = 4;              // 组内按钮间隙
    const double Pad = 8;              // 条两端内边距
    const double SepGap = 8;           // 分隔线两侧留白
    const double BallD = 48;           // 收起态大圆直径
    const byte PanelAlpha = 0xCC;      // 0xCC = 0.80：微微能看到下面

    static readonly Color Ink = C(0x1B, 0x1B, 0x1F);
    static readonly Color Accent = C(0x00, 0x67, 0xC0);
    static readonly Color PenRed = C(0xF2, 0x2E, 0x2E);
    static readonly Color BallBlue = C(0x21, 0x73, 0xE6);

    /// <summary>和引擎 InkPalette.Presets 同一组色（红橙黄绿青蓝紫黑白）。</summary>
    static readonly Color[] Palette =
    {
        C(0xF2, 0x2E, 0x2E), C(0xFA, 0x8C, 0x17), C(0xFA, 0xD1, 0x1F),
        C(0x21, 0xB3, 0x54), C(0x1A, 0xB8, 0xC7), C(0x21, 0x73, 0xE6),
        C(0x8C, 0x47, 0xDB), C(0x1C, 0x1F, 0x26), C(0xFF, 0xFF, 0xFF),
    };

    static readonly (string Icon, string Name)[] Tools =
    {
        ("mouse", "鼠标"), ("pen", "笔"), ("highlighter", "荧光笔"), ("laser", "激光笔"),
        ("eraser", "橡皮擦"), ("select", "选择"), ("shapes", "图形"), ("capture", "截屏"),
        ("undo", "后撤"), ("redo", "重做"), ("settings", "设置"),
    };

    /// <summary>这些索引之后插一条分隔线：鼠标之后、橡皮擦之后、截屏之后。</summary>
    static readonly int[] GroupEnds = { 0, 4, 7 };

    const int SheetW = 1560;
    const int SheetMaxH = 2400;
    static readonly Brush TitleBrush = new SolidColorBrush(C(0x14, 0x16, 0x1A));
    static readonly Brush BodyBrush = new SolidColorBrush(C(0x3A, 0x3E, 0x46));
    static readonly Brush NoteBrush = new SolidColorBrush(C(0x6B, 0x70, 0x78));

    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "design";
        string sheet = args.Length > 1 ? args[1] : "all";
        Directory.CreateDirectory(outDir);

        if (sheet is "all" or "v0")
            RenderSheet(outDir, "界面-设计稿v0-形态.png", DrawV0Sheet);
        if (sheet is "all" or "more")
            RenderSheet(outDir, "界面-设计稿v1-更多与收纳.png", DrawMoreSheet);
        if (sheet is "all" or "color")
            RenderSheet(outDir, "界面-设计稿v1-色带三方案.png", DrawColorSheet);
        if (sheet is "all" or "layout")
            RenderSheet(outDir, "界面-设计稿v1-面板位置与粗细.png", DrawLayoutSheet);
        if (sheet is "all" or "fuse")
            RenderSheet(outDir, "界面-设计稿v2-一体三带-形态.png", DrawFuseFormSheet);
        if (sheet is "all" or "fuseux")
            RenderSheet(outDir, "界面-设计稿v2-一体三带-交互.png", DrawFuseUxSheet);
        if (sheet is "all" or "icons")
            RenderSheet(outDir, "界面-设计稿v3-图标与色块.png", DrawIconSheet);
        if (sheet is "all" or "width")
            RenderSheet(outDir, "界面-设计稿v4-宽度调研.png", DrawWidthSheet);
        if (sheet is "all" or "height")
            RenderSheet(outDir, "界面-设计稿v5-高度调研.png", DrawHeightSheet);
    }

    /// <summary>把每个绘制函数画成一张 PNG：高度由绘制函数自己算出来。</summary>
    static void RenderSheet(string outDir, string fileName, Func<DrawingContext, double> draw)
    {
        var visual = new DrawingVisual();
        double bottom;
        using (var c = visual.RenderOpen())
        {
            c.DrawRectangle(Brushes.White, null, new Rect(0, 0, SheetW, SheetMaxH));
            bottom = draw(c) + 28;
        }

        // 画完才知道稿子多高，所以位图按实测高度开（不要先开一张大的再裁）。
        var bmp = new RenderTargetBitmap(SheetW, (int)bottom, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        string path = Path.Combine(outDir, fileName);
        using (var fs = File.Create(path)) enc.Save(fs);
        Console.WriteLine($"已生成 {path}（{SheetW} × {(int)bottom}）");
    }

    static double DrawV0Sheet(DrawingContext c)
    {
        double y = 36;
        y = DrawHeader(c, y);
        y = DrawBall(c, y);
        y = DrawFullBar(c, y);
        y = DrawPopover(c, y);
        y = DrawMinimal(c, y);
        y = DrawAlpha(c, y);
        y = DrawStates(c, y);
        y = DrawDecisions(c, y);
        return y;
    }

    // =====================================================================
    //  分区
    // =====================================================================

    static double DrawHeader(DrawingContext c, double y)
    {
        Text(c, "批注工具条 · 形态设计稿 v0", 40, y, 30, TitleBrush, bold: true);
        Text(c, "逻辑像素 1:1（192 DPI 屏上乘 2）。收起态是一个大圆；展开后极简版走环形，完整版走直线胶囊。",
             40, y + 44, 14, BodyBrush);
        Text(c, "配色取自引擎已有的 InkPalette，图标取自微软 Fluent 图标库（MIT）。稿上的尺寸可以直接抄进代码。",
             40, y + 66, 14, NoteBrush);
        return y + 104;
    }

    static double DrawBall(DrawingContext c, double y)
    {
        y = Section(c, y, "① 收起态：一个大圆（直径 48）");

        double cy = y + 56, x = 60;
        Ball(c, x + 30, cy, BallD, hovered: false, pressed: false, ring: PenRed);
        Caption(c, "常态", x, cy + 44, 60);

        x += 110;
        Ball(c, x + 30, cy, BallD, hovered: true, pressed: false, ring: BallBlue);
        Caption(c, "悬停：圆长大到 52，底色加深一档", x - 30, cy + 44, 120);

        x += 200;
        Ball(c, x + 30, cy, BallD, hovered: true, pressed: true, ring: C(0x21, 0xB3, 0x54));
        Caption(c, "按下：缩到 46", x, cy + 44, 60);

        // 贴边半隐：只露 8 像素
        x += 130;
        c.DrawRectangle(new SolidColorBrush(C(0xEC, 0xEE, 0xF3)), null, new Rect(x + 10, cy - 40, 120, 80));
        Text(c, "屏幕边", x + 22, cy - 32, 12, NoteBrush);
        c.PushClip(new RectangleGeometry(new Rect(x + 6, cy - 40, 124, 80)));
        Ball(c, x + 8 - BallD / 2 + 8, cy, BallD, false, false, C(0x8C, 0x47, 0xDB));
        c.Pop();
        c.DrawRectangle(new SolidColorBrush(C(0x30, 0x34, 0x3C)), null, new Rect(x + 6, cy - 40, 2, 80));
        Caption(c, "贴边：只露 8 像素，碰到才浮出", x - 40, cy + 44, 160);

        // 3 倍放大
        double zx = 840, zy = cy + 26;
        Text(c, "3×：圆里那圈颜色 = 当前笔色 —— 不用点开就知道现在拿的是哪支笔。",
             zx, zy - 100, 13, BodyBrush);
        Ball(c, zx + 72, zy, BallD * 3, false, false, PenRed);
        Caption(c, "常态", zx + 42, zy + 84, 60);
        Ball(c, zx + 72 + 220, zy, BallD * 3, hovered: true, pressed: false, ring: BallBlue);
        Caption(c, "悬停", zx + 72 + 220 - 30, zy + 84, 60);

        return y + 214;
    }

    static double DrawFullBar(DrawingContext c, double y)
    {
        double bw = BarTotalWidth(Tools.Length);
        y = Section(c, y, $"② 完整界面：展开成一条胶囊（{bw:F0} × {BarH:F0}）");

        double lightTop = y + 8;
        Backdrop(c, 60, lightTop, 660, 96, light: true, label: "浅色底（白底 PPT / 白板）");
        Bar(c, 60 + 34, lightTop + 20, dark: false, active: 1, hover: -1);
        Text(c, $"实算宽度 {bw:F0} = 两端内边距 {Pad:F0}×2 ＋「收起格」{Btn:F0} ＋ 3 组工具 ＋ 3 条 1px 分隔线（两侧各 {SepGap:F0} 留白）",
             60, lightTop + 104, 12, NoteBrush);

        double darkTop = y + 124;
        Backdrop(c, 60, darkTop, 660, 96, light: false, label: "深色底（深色 PPT / 视频）");
        Bar(c, 60 + 34, darkTop + 20, dark: true, active: 1, hover: -1);

        double rx = 780;
        Text(c, "2×：分组与命中区（按钮 40×40，间隙 4，内边距 8，分隔线 1px）", rx, y + 12, 13, BodyBrush);
        Bar(c, rx, y + 40, dark: false, active: 1, hover: -1, scale: 2, maxButtons: 6);

        double ly = y + 40 + BarH * 2 + 10;
        double dimW = (Btn + Gap + Btn) * 2;
        Line(c, rx, ly, rx + dimW, ly, C(0x9A, 0xA0, 0xAA));
        Line(c, rx, ly - 5, rx, ly + 5, C(0x9A, 0xA0, 0xAA));
        Line(c, rx + dimW, ly - 5, rx + dimW, ly + 5, C(0x9A, 0xA0, 0xAA));
        Caption(c, "40 ＋ 4 ＋ 40（再乘 2 倍）", rx, ly + 6, dimW);

        Text(c, "· 组与组之间是 17 像素（8 ＋ 1px 分隔线 ＋ 8），比组内的 4 明显，眼睛能一眼分块。",
             rx, ly + 30, 12, NoteBrush);
        Text(c, "· 「收起格」永远是条子最左边的一格，位置恒定 —— 来回都不用找（费茨定律：固定位置 + 边缘最快）。",
             rx, ly + 48, 12, NoteBrush);

        return darkTop + 116;
    }

    static double DrawPopover(DrawingContext c, double y)
    {
        y = Section(c, y, "③ 点「笔」之后：直线色带 ＋ 粗细滑块（都是平时不出现的）");

        double top = y + 12;
        BarLayout layout = Bar(c, 60, top, dark: false, active: 1, hover: -1);

        double penX = layout.Centers[1 + 1];                 // 「收起格」之后是鼠标，再下一个才是笔
        double bandW = 9 * 26 + 8 * 6 + 2 * 8;
        double bandX = Math.Max(60, Math.Min(penX - bandW / 2, 60 + layout.Width - bandW));

        double bandY = top + BarH + 8;
        Strip(c, bandX, bandY, bandW, 42, dark: false);
        for (int i = 0; i < Palette.Length; i++)
        {
            double cx = bandX + 21 + i * 32, cyy = bandY + 21;
            c.DrawEllipse(new SolidColorBrush(Palette[i]), null, new Point(cx, cyy), 13, 13);
            if (i == 0) Ring2(c, cx, cyy, 13.5, 9.5);
        }

        double sliderY = bandY + 50;
        Strip(c, bandX, sliderY, bandW, 40, dark: false);
        double trackX = bandX + 16, trackW = bandW - 32 - 40;
        c.DrawRoundedRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x24)), null,
                               new Rect(trackX, sliderY + 18, trackW, 4), 2, 2);
        c.DrawRoundedRectangle(new SolidColorBrush(Accent), null, new Rect(trackX, sliderY + 18, trackW * 0.45, 4), 2, 2);
        c.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(C(0x30, 0x34, 0x3C)), 1),
                      new Point(trackX + trackW * 0.45, sliderY + 20), 8, 8);
        c.DrawEllipse(new SolidColorBrush(C(0x1C, 0x1F, 0x26)), null, new Point(bandX + bandW - 26, sliderY + 20), 7.5, 7.5);
        Caption(c, "笔尖预览", bandX + bandW - 44, sliderY + 26, 48);

        // 右列：2× 放大
        double rx = 780, s = 2;
        Text(c, "2×：色带的选中态", rx, y + 16, 13, BodyBrush);
        Strip(c, rx, y + 44, bandW * s, 42 * s, dark: false);
        for (int i = 0; i < Palette.Length; i++)
        {
            double cx = rx + 21 * s + i * 32 * s, cyy = y + 44 + 21 * s;
            c.DrawEllipse(new SolidColorBrush(Palette[i]), null, new Point(cx, cyy), 13 * s, 13 * s);
            if (i == 5) Ring2(c, cx, cyy, 13.5 * s, 9.5 * s);
        }

        double ny = y + 44 + 42 * s + 18;
        foreach (string line in new[]
        {
            "色带就是引擎 InkPalette 的 9 个色（红橙黄绿青蓝紫黑白）—— 白笔和黑笔都要有，投影上白笔是刚需。",
            "选中态有两重标记：外圈 1.5px 深色描边 ＋ 内圈 2px 白环。只靠颜色区分，色弱用户就分不出。",
            "色带以「笔」按钮为中心，越出屏幕时夹回可视区（左右都夹）；它跟着笔的位置走，不单独开窗。",
            "粗细滑块默认不占地方：点开笔、或悬停 400 毫秒才浮出；拖动时右边那个笔尖预览圆跟着变粗。",
        })
        {
            Text(c, "· " + line, rx, ny, 12.5, BodyBrush);
            ny += 22;
        }

        return Math.Max(sliderY + 60, ny) + 12;
    }

    static double DrawMinimal(DrawingContext c, double y)
    {
        y = Section(c, y, "④ 极简界面：环形（4 项）与胶囊小条（备选形态）");

        double cy = y + 124;
        Text(c, "环形（推荐）", 60, y + 8, 13, BodyBrush);
        Ring(c, 220, cy, ringR: 66, showColors: false, withBall: true);
        Caption(c, "笔固定在 3 点钟方向", 120, cy + 108, 200);

        Text(c, "点「笔」→ 环形色带", 400, y + 8, 13, BodyBrush);
        Ring(c, 600, cy, ringR: 66, showColors: true, withBall: true);
        Caption(c, "工具环 66、色环 118，两环之间留 12 像素不重叠", 440, cy + 150, 320);

        double bx = 900;
        Text(c, "胶囊小条（4 项，备选）", bx, y + 8, 13, BodyBrush);
        MinimalBar(c, bx, cy - 24, active: 1);
        Caption(c, "180 × 48，占地最小；代价是笔和橡皮固定在同一行，手感不如环形快", bx, cy + 40, 380);

        double my = cy + 86;
        foreach (string line in new[]
        {
            "环形为什么值得做：径向布局里每个方向的距离都一样，靠肌肉记忆就能点到、不用看屏幕；",
            "直线条每多一个工具，两端的项就要多瞄准一次。极简版只有 4 项，正好落在环形的甜点区。",
            "环形项数上限 8。再多，人眼就分不清角度了；超过 8 项就该换回直线条。",
            "环形只在展开时存在，收起后还是一个球 —— 平时的屏幕占用为零。",
        })
        {
            Text(c, "· " + line, bx, my, 12.5, BodyBrush);
            my += 22;
        }

        return Math.Max(cy + 170, my) + 14;
    }

    static double DrawAlpha(DrawingContext c, double y)
    {
        y = Section(c, y, "⑤ 透明度：到底让下面透过多少（同一张彩色背景，三档对比）");

        (byte A, string Label, string Note)[] rows =
        {
            (0xB8, "0.72", "下面看得太清楚，图标开始被背景的花色吃掉"),
            (0xCC, "0.80（推荐）", "微微能看到下面，图标永远是主角"),
            (0xE6, "0.90", "基本不透，显得厚、像弹窗，不像贴在桌面上的工具"),
        };

        double top = y + 8;
        for (int i = 0; i < rows.Length; i++)
        {
            double ry = top + i * 104;
            bool rec = rows[i].A == PanelAlpha;
            Text(c, rec ? rows[i].Label + "  ★" : rows[i].Label, 60, ry + 42, 15,
                 rec ? new SolidColorBrush(Accent) : BodyBrush, bold: rec);
            PhotoBackdrop(c, 200, ry, 660, 92);
            Bar(c, 234, ry + 18, dark: false, active: 1, hover: -1, alpha: rows[i].A, maxButtons: 11);
            Text(c, rows[i].Note, 890, ry + 42, 12.5, NoteBrush);
        }

        double ny = top + rows.Length * 104 + 4;
        foreach (string line in new[]
        {
            "底：白 80% ＋ 1px 黑 10% 描边。那圈描边是「立得住」的关键，没有它圆角会糊在背景里。",
            "图标用近黑 #1B1B1F：在白 80% 的底上，压到彩色背景最差也守得住 3:1；换成中灰就守不住了。",
            "当前工具用不透明的强调色 #0067C0 填充 —— 微软的规范明确不建议在毛玻璃上放强调色的文字。",
            "系统「透明效果」被关掉时（老机器、节能模式常见），整块底直接退回 100% 不透明，不做半透明糊弄。",
        })
        {
            Text(c, "· " + line, 60, ny, 12.5, BodyBrush);
            ny += 22;
        }

        return ny + 10;
    }

    static double DrawStates(DrawingContext c, double y)
    {
        y = Section(c, y, "⑥ 按钮状态与图标");

        (string Icon, string Label, int State)[] tiles =
        {
            ("pen", "常态", 0), ("pen", "悬停", 1), ("pen", "激活", 2),
            ("pen", "按下", 3), ("undo", "禁用（撤销栈空）", 4),
        };

        double x = 60, cy = y + 44;
        foreach (var t in tiles)
        {
            Strip(c, x, cy - 20, 40, 40, dark: false);
            TileRaw(c, x + 20, cy, 40, IconSize, t.Icon, dark: false,
                    active: t.State == 2, hovered: t.State == 1, pressed: t.State == 3, disabled: t.State == 4);
            Caption(c, t.Label, x - 24, cy + 28, 88);
            x += 104;
        }

        x = 660;
        Text(c, "3× 图标（Fluent 24 regular，绘到 20，填充式路径）", x, y + 4, 13, BodyBrush);
        string[] names = { "mouse", "pen", "highlighter", "laser", "eraser", "shapes", "capture" };
        string[] labels = { "鼠标", "笔", "荧光笔", "激光笔", "橡皮擦", "图形", "截屏" };
        for (int i = 0; i < names.Length; i++)
        {
            Icon(c, names[i], x + 40 + i * 92, cy, 60, new SolidColorBrush(Ink));
            Caption(c, labels[i], x + 10 + i * 92, cy + 46, 60);
        }

        double ny = y + 104;
        foreach (string line in new[]
        {
            "激活 = 不透明强调色底 ＋ 图标转白；悬停 = 黑 7% 底（深色主题换成白 8%）；按下 = 再深一档并缩到 0.96。",
            "禁用只降图标、不降底：底还在，用户才知道这个位置有东西、只是现在不能用（后撤栈空时）。",
            "图标沿用你们已经定过的观感：20 逻辑像素、视觉线宽约 1.7 —— 投影上看得清，又不会糊成一块。",
            "一件事只有一种图标语义：笔永远是 Pen，不和「编辑」混用；禁用态不换图标、只降透明度。",
        })
        {
            Text(c, "· " + line, 60, ny, 12.5, BodyBrush);
            ny += 22;
        }

        return ny + 10;
    }

    static double DrawDecisions(DrawingContext c, double y)
    {
        y = Section(c, y, "⑦ 这一版要拍板的事");

        var box = new Rect(40, y, SheetW - 80, 172);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);

        string[] lines =
        {
            "1. 收起态就是那个球；展开后它不消失，变成条子最左边的一格（点它收起）。位置恒定，来回都最好点。",
            "2. 完整条 592×56 里放 11 项；极简版（环形 4 项 / 胶囊 180×48）后面做，两套共用同一个引擎接入口。",
            "3. 圆角一律用「高度 ÷ 2」的胶囊形：球展开成条是一次连续变形，不会出现「先变圆角再变长」的怪相。",
            "4. 透明度定 0.80 ＋ 1px 描边；不做真毛玻璃（blur 要抓屏、每帧算，核显上不划算，一帧都省不得）。",
            "5. 色带（9 色）与粗细滑块都做成「按需浮出」，不常驻；常驻的面积越大，遮挡板书的机会越多。",
            "6. 贴边与自动隐藏做成开关，默认「离边 12px 停住」；课堂里找不到工具，比挡一点更糟。",
        };

        double ly = y + 18;
        foreach (string line in lines)
        {
            Text(c, line, 60, ly, 13, BodyBrush);
            ly += 24;
        }

        return ly + 8;
    }

    // =====================================================================
    //  元件
    // =====================================================================

    sealed class BarLayout
    {
        public double Width;
        public readonly List<double> Centers = new List<double>();
    }

    /// <summary>工具条的总宽（纯计算，先算宽再画标题用得上）。</summary>
    static double BarTotalWidth(int shown)
    {
        double w = Pad * 2 + Btn + (SepGap * 2 + 1);
        for (int i = 0; i < shown; i++)
        {
            w += Btn;
            if (i < shown - 1)
                w += Array.IndexOf(GroupEnds, i) >= 0 ? SepGap * 2 + 1 : Gap;
        }
        return w;
    }

    static BarLayout Bar(DrawingContext c, double x, double y, bool dark, int active, int hover,
                         byte alpha = PanelAlpha, double scale = 1, int maxButtons = 99)
    {
        var layout = new BarLayout();
        double h = BarH * scale, pad = Pad * scale, btn = Btn * scale,
               gap = Gap * scale, sep = SepGap * scale;

        int shown = Math.Min(Tools.Length, maxButtons);
        double w = pad * 2 + btn + (sep * 2 + 1);
        for (int i = 0; i < shown; i++)
        {
            w += btn;
            if (i < shown - 1)
                w += Array.IndexOf(GroupEnds, i) >= 0 ? sep * 2 + 1 : gap;
        }
        layout.Width = w;

        Strip(c, x, y, w, h, dark, alpha);

        // 最左边：收起格（就是那个球）
        double cx = x + pad + btn / 2;
        c.DrawEllipse(new SolidColorBrush(dark ? C(0x3A, 0x3A, 0x3E) : C(0xEC, 0xEE, 0xF2)),
                      null, new Point(cx, y + h / 2), btn / 2 - 2 * scale, btn / 2 - 2 * scale);
        Icon(c, "chevronDown", cx, y + h / 2, IconSize * scale * 0.85, dark ? Brushes.White : new SolidColorBrush(Ink));
        layout.Centers.Add(cx);
        cx += btn / 2 + sep * 2 + 1;

        for (int i = 0; i < shown; i++)
        {
            TileRaw(c, cx + btn / 2, y + h / 2, btn, IconSize * scale, Tools[i].Icon, dark,
                    active: i == active, hovered: i == hover, pressed: false, disabled: false);
            layout.Centers.Add(cx + btn / 2);
            cx += btn;
            if (i < shown - 1)
            {
                if (Array.IndexOf(GroupEnds, i) >= 0)
                {
                    c.DrawRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x28) : C(0x00, 0x00, 0x00, 0x1E)),
                                    null, new Rect(cx + sep, y + h / 2 - 10 * scale, scale, 20 * scale));
                    cx += sep * 2 + 1;
                }
                else cx += gap;
            }
        }
        return layout;
    }

    static void MinimalBar(DrawingContext c, double x, double y, int active)
    {
        const double h = 48, btn = 36, pad = 6;
        double w = pad * 2 + btn * 4 + 4 * 3;
        Strip(c, x, y, w, h, dark: false);
        string[] icons = { "mouse", "pen", "eraser", "settings" };
        for (int i = 0; i < 4; i++)
            TileRaw(c, x + pad + btn / 2 + i * (btn + 4), y + h / 2, btn, 19, icons[i], false,
                    active: i == active, hovered: false, pressed: false, disabled: false);
    }

    static void Ring(DrawingContext c, double cx, double cy, double ringR, bool showColors, bool withBall)
    {
        var items = new (string Icon, double Angle)[]
        {
            ("mouse", -90), ("pen", 0), ("eraser", 90), ("settings", 180),
        };
        foreach (var it in items)
        {
            double rad = it.Angle * Math.PI / 180.0;
            double bx = cx + Math.Cos(rad) * ringR, by = cy + Math.Sin(rad) * ringR;
            Strip(c, bx - 20, by - 20, 40, 40, dark: false);
            TileRaw(c, bx, by, 40, IconSize, it.Icon, false,
                    active: it.Icon == "pen", hovered: false, pressed: false, disabled: false);
        }

        if (withBall) Ball(c, cx, cy, BallD, false, false, PenRed);

        if (!showColors) return;
        for (int i = 0; i < Palette.Length; i++)
        {
            double rad = (-90 + i * (360.0 / Palette.Length)) * Math.PI / 180.0;
            double bx = cx + Math.Cos(rad) * 118, by = cy + Math.Sin(rad) * 118;
            c.DrawEllipse(new SolidColorBrush(Palette[i]), null, new Point(bx, by), 15, 15);
            if (i == 0) Ring2(c, bx, by, 15.5, 11);
        }
    }

    static void Ball(DrawingContext c, double cx, double cy, double d, bool hovered, bool pressed, Color ring)
    {
        double r = d / 2;
        if (hovered && !pressed) r += 2;
        if (pressed) r -= 1;
        Color fill = hovered ? C(0xF7, 0xFA, 0xFF) : C(0xFF, 0xFF, 0xFF);
        // 影：同一形状往下叠三层，逐层变淡
        for (int i = 3; i >= 1; i--)
        {
            c.DrawEllipse(new SolidColorBrush(C(0x00, 0x00, 0x00, (byte)(0x04 + i * 3))), null,
                          new Point(cx, cy + i * 1.6), r, r);
        }
        c.DrawEllipse(new SolidColorBrush(fill), null, new Point(cx, cy), r, r);
        c.DrawEllipse(null, new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x1A)), 1), new Point(cx, cy), r - 0.5, r - 0.5);
        c.DrawEllipse(null, new Pen(new SolidColorBrush(ring), Math.Max(2.5, r * 0.09)), new Point(cx, cy), r * 0.78, r * 0.78);
        Icon(c, "pen", cx, cy, r * 0.72, new SolidColorBrush(Ink));
    }

    static void TileRaw(DrawingContext c, double cx, double cy, double size, double iconSize, string icon,
                        bool dark, bool active, bool hovered, bool pressed, bool disabled)
    {
        double r = size / 2;
        Brush bg = null;
        if (active) bg = new SolidColorBrush(Accent);
        else if (pressed) bg = new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x24) : C(0x00, 0x00, 0x00, 0x14));
        else if (hovered) bg = new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x18) : C(0x00, 0x00, 0x00, 0x0E));

        if (bg != null)
        {
            double s = pressed ? r * 0.94 : r;
            double rad = 8 * (s / r);
            c.DrawRoundedRectangle(bg, null, new Rect(cx - s, cy - s, s * 2, s * 2), rad, rad);
        }

        Color ic = active ? Colors.White : (dark ? C(0xF2, 0xF2, 0xF2) : Ink);
        if (disabled) ic = C(0x9A, 0xA0, 0xAA);
        Icon(c, icon, cx, cy, iconSize, new SolidColorBrush(ic));
    }

    static void Strip(DrawingContext c, double x, double y, double w, double h, bool dark,
                      byte alpha = PanelAlpha, bool shadow = true)
    {
        Color fill = dark ? C(0x22, 0x22, 0x24, alpha) : C(0xFF, 0xFF, 0xFF, alpha);
        Color border = dark ? C(0xFF, 0xFF, 0xFF, 0x1F) : C(0x00, 0x00, 0x00, 0x1A);
        double r = h / 2;
        if (shadow)
        {
            for (int i = 3; i >= 1; i--)
            {
                c.DrawRoundedRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, (byte)(0x04 + i * 3))), null,
                                       new Rect(x + 0.5, y + 0.5 + i * 1.6, w - 1, h - 1), r, r);
            }
        }
        c.DrawRoundedRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(border), 1),
                               new Rect(x + 0.5, y + 0.5, w - 1, h - 1), r, r);
    }

    /// <summary>选中态：外圈深色描边 + 内圈白环。</summary>
    static void Ring2(DrawingContext c, double cx, double cy, double outer, double inner)
    {
        c.DrawEllipse(null, new Pen(new SolidColorBrush(C(0x2A, 0x2E, 0x36)), 1.5), new Point(cx, cy), outer, outer);
        c.DrawEllipse(null, new Pen(Brushes.White, 2), new Point(cx, cy), inner, inner);
    }

    static void Backdrop(DrawingContext c, double x, double y, double w, double h, bool light, string label)
    {
        Color bg = light ? C(0xF4, 0xF5, 0xF7) : C(0x1B, 0x1B, 0x1E);
        c.DrawRoundedRectangle(new SolidColorBrush(bg), new Pen(new SolidColorBrush(C(0xDA, 0xDE, 0xE4)), 1),
                               new Rect(x, y, w, h), 8, 8);
        Color l1 = light ? C(0xD8, 0xDC, 0xE2) : C(0x33, 0x38, 0x40);
        Color l2 = light ? C(0xE4, 0xE7, 0xEC) : C(0x2A, 0x2E, 0x36);
        c.DrawRoundedRectangle(new SolidColorBrush(l1), null, new Rect(x + 20, y + 20, 300, 9), 4, 4);
        c.DrawRoundedRectangle(new SolidColorBrush(l2), null, new Rect(x + 20, y + 38, 200, 9), 4, 4);
        Text(c, label, x + 20, y + h - 24, 12, NoteBrush);
    }

    static void PhotoBackdrop(DrawingContext c, double x, double y, double w, double h)
    {
        var g = new LinearGradientBrush(C(0x2E, 0x4A, 0x7A), C(0x9A, 0x4B, 0x3C), new Point(0, 0), new Point(1, 1));
        c.DrawRoundedRectangle(g, null, new Rect(x, y, w, h), 8, 8);
        c.PushClip(new RectangleGeometry(new Rect(x, y, w, h), 8, 8));
        c.DrawEllipse(new SolidColorBrush(C(0xE8, 0xC4, 0x3A, 0xB0)), null, new Point(x + 90, y + 30), 46, 46);
        c.DrawEllipse(new SolidColorBrush(C(0x2F, 0x8F, 0x5A, 0xB0)), null, new Point(x + 250, y + 66), 54, 54);
        c.DrawEllipse(new SolidColorBrush(C(0xD8, 0x5A, 0x8A, 0xA0)), null, new Point(x + 430, y + 26), 40, 40);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0xC0)), null, new Rect(x + 340, y + 54, 260, 10), 5, 5);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x90)), null, new Rect(x + 340, y + 72, 170, 10), 5, 5);
        c.Pop();
    }

    // =====================================================================
    //  小工具
    // =====================================================================

    static double Section(DrawingContext c, double y, string title)
    {
        Text(c, title, 40, y, 19, TitleBrush, bold: true);
        Line(c, 40, y + 30, SheetW - 40, y + 30, C(0xE6, 0xE9, 0xEE));
        return y + 44;
    }

    static void Caption(DrawingContext c, string s, double x, double y, double w)
    {
        var ft = Fmt(s, 11.5, NoteBrush);
        ft.MaxTextWidth = Math.Max(40, w);
        ft.TextAlignment = TextAlignment.Center;
        c.DrawText(ft, new Point(x, y));
    }

    static void Text(DrawingContext c, string s, double x, double y, double size, Brush b, bool bold = false)
        => c.DrawText(Fmt(s, size, b, bold), new Point(x, y));

    static FormattedText Fmt(string s, double size, Brush b, bool bold = false)
    {
        var tf = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                              FontStyles.Normal,
                              bold ? FontWeights.SemiBold : FontWeights.Normal,
                              FontStretches.Normal);
        return new FormattedText(s, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, tf, size, b, 1.0);
    }

    static void Line(DrawingContext c, double x1, double y1, double x2, double y2, Color color)
        => c.DrawLine(new Pen(new SolidColorBrush(color), 1), new Point(x1, y1), new Point(x2, y2));

    static void Icon(DrawingContext c, string name, double cx, double cy, double size, Brush brush)
    {
        var geo = Geometry.Parse(IconPaths.Get(name));
        double s = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(s, s));
        c.DrawGeometry(brush, null, geo);
        c.Pop();
        c.Pop();
    }

    static Color C(byte r, byte g, byte b, byte a = 0xFF) => Color.FromArgb(a, r, g, b);
}
