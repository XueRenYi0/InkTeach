using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesignSheet;

/// <summary>
/// v14：**「书与笔迹」** —— 图标第四轮。
///
/// 由头（用户 2026-10-01 给了两张参考图）：
///   ① 一本打开的书 ＋ 一支白笔 ＋ 一道蓝笔迹（墨绿底、圆角方框里套着书）；
///   ② 一支笔 ＋ 一道蓝色弧线（深蓝底、笔正落在弧线的一端）。
/// 用户：「可不可以把这两种图标做成符合我们风格的软件图标」。
///
/// 这一轮的翻译原则：
///   · **拿**：构图（书当舞台、笔当主角）与动势（笔斜着、压在笔迹的一端 ——
///     "正在批注"靠方向感说，不靠细节）；
///   · **不拿**：参考图的墨绿 / 深蓝新底色（我们只有白砖与墨砖两档，加第三个品牌色就散架）、
///     3D 高光与渐变（16px 上最先糊掉）、参考图①里那圈"白相框"（它就是我们的砖边）。
///
/// 所以六个候选共用同一套零件：我们的马克笔（近黑 / 白，红笔尖与红环）、
/// 一道红或蓝的笔迹、一本用墨色或纸白画的书 —— 全部是已落地那套语言的零件。
/// 小尺寸照旧按真实像素渲染、最近邻放大。
/// </summary>
internal static partial class Program
{
    /// <summary>参考图那道笔迹的蓝：我们色板里的蓝（不是参考图的品牌蓝）。</summary>
    static readonly Color IcBlue = C(0x21, 0x73, 0xE6);

    enum MxKind
    {
        BookPenPaper,    // A 书（纸白）＋ 黑笔 ＋ 红笔迹
        BookPenInk,      // B 书（墨底）＋ 白笔 ＋ 红笔迹
        BookMarkPaper,   // C 书（纸白）＋ 一道红划线
        PenRedSwash,     // D 黑笔 ＋ 红笔迹（参考图②的骨）
        PenBlueSwash,    // E 黑笔 ＋ 蓝笔迹（参考图②的色）
        PenRedSwashDark, // F 白笔 ＋ 红笔迹 · 墨砖（深底对照）
    }

    static readonly (MxKind Kind, string Tag, string Title, string Note)[] MxCandidates =
    {
        (MxKind.BookPenPaper, "A", "书 ＋ 笔 · 纸白",
         "打开的书＝白页＋石墨描边，我们的马克笔斜压在书页上，笔尖拖出一道红笔迹。纸、墨、红三样都是老配角：书只当舞台，笔还是主角。"),

        (MxKind.BookPenInk, "B", "书 ＋ 笔 · 墨底",
         "明暗对调：书换成墨色实心（一道白中缝＝书脊）、笔换成白杆。参考图①的那点明暗关系（深底浅物）在这一版里最足；代价是白笔不是我们平时那支。"),

        (MxKind.BookMarkPaper, "C", "书 ＋ 红划线 · 纸白",
         "书上横着一道红：划线是批注最经典的动作，比 A/B 少一支笔、小尺寸也最稳；读起来更像「已经批注过了」。"),

        (MxKind.PenRedSwash, "D", "笔 ＋ 红笔迹",
         "参考图②的骨：我们的笔斜着，笔尖拉出一道红色的长笔迹。红是我们的批注色，比参考图的蓝更「我们」。"),

        (MxKind.PenBlueSwash, "E", "笔 ＋ 蓝笔迹",
         "同 D，笔迹换成参考图那种蓝（取我们色板里的蓝 #2173E6）。蓝更安静、更像「写字」；代价是少了一点批注的热度。"),

        (MxKind.PenRedSwashDark, "F", "笔 ＋ 红笔迹 · 墨砖",
         "把底换成墨砖、笔换成白杆：参考图②的深底方向。但深底在深色任务栏上会与任务栏同色、丢掉边界 —— 这一版只作对照。"),
    };

    static readonly (MxKind Kind, string File)[] MxFiles =
    {
        (MxKind.BookPenPaper,    "A-书与笔-纸白"),
        (MxKind.BookPenInk,      "B-书与笔-墨底"),
        (MxKind.BookMarkPaper,   "C-书与红划线-纸白"),
        (MxKind.PenRedSwash,     "D-笔与红笔迹"),
        (MxKind.PenBlueSwash,    "E-笔与蓝笔迹"),
        (MxKind.PenRedSwashDark, "F-笔与红笔迹-墨砖"),
    };

    // =====================================================================
    //  出一张稿
    // =====================================================================

    static double DrawMixSheet(DrawingContext c)
    {
        c.DrawRectangle(Brushes.White, null, new Rect(0, 0, SheetW, 3200));

        double y = 36;
        Text(c, "程序图标 · 设计稿 v4（书与笔迹）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "用户 2026-10-01 给的两张参考图：① 一本打开的书 ＋ 一支白笔 ＋ 一道蓝笔迹（墨绿底）　② 一支笔 ＋ 一道蓝色弧线（深蓝底）。",
             40, y + 44, 14, BodyBrush);
        Text(c, "这一轮把它们翻成我们的语言：白砖 / 墨砖、近黑笔身与批注红、平涂矢量 —— 参考图里只拿构图与动势，不拿新的品牌色和 3D 光。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        y = Section(c, y, "① 参考图里拿什么、不拿什么");
        double ny = y + 4;
        double colW = (SheetW - 100) / 2;
        Text(c, "拿：", 60, ny, 13, new SolidColorBrush(Accent), bold: true);
        ny += 24;
        foreach (string s in new[]
        {
            "· 构图：书当舞台、笔当主角（参考图①）——「在什么上面批注」和「用哪支笔」各占一件事。",
            "· 动势：笔都斜着、笔尖压在笔迹的一端（参考图②）——「正在批注」靠方向感说，不靠细节。",
            "· 笔迹只画一道：一道弧线就够说「在写」；参考图里那圈小波浪（∿）缩到 32px 就没了。",
        }) ny += Paragraph(c, s, 60, ny, colW, 12.5, BodyBrush) + 8;

        double ny2 = y + 4;
        Text(c, "不拿：", 60 + colW + 20, ny2, 13, new SolidColorBrush(Accent), bold: true);
        ny2 += 24;
        foreach (string s in new[]
        {
            "· 墨绿 / 深蓝的新底色：我们只有白砖（浅底不糊、深底不丢边界）与墨砖两档；加第三个品牌色，图标就散架了。",
            "· 3D 高光、渐变、玻璃质感：16px 上它们最先糊成一团；我们一路用平涂矢量，就是为了任务栏那 32px。",
            "· 参考图①里那圈「白相框」：它就是我们的砖边（1px 极淡的那一圈），不用再套一层。",
        }) ny2 += Paragraph(c, s, 60 + colW + 20, ny2, colW, 12.5, BodyBrush) + 8;

        y = Math.Max(ny, ny2) + 18;

        y = Section(c, y, "② 六个候选（浅底 / 深底 / 真实尺寸 / 放大）");
        const double cw = 477, gap = 25, cardH = 520;
        double rowY = y + 8;
        for (int i = 0; i < MxCandidates.Length; i++)
        {
            double cx = 40 + (i % 3) * (cw + gap);
            double cy = rowY + (i / 3) * (cardH + 18);
            var cand = MxCandidates[i];
            IcCard(c, cx, cy, cw, s => MxRender(cand.Kind, s), cand.Tag, cand.Title, cand.Note);
        }
        y = rowY + 2 * (cardH + 18) + 6;

        y = Section(c, y, "③ 小尺寸真容：16px（×7）与 32px（×4）—— 任务栏上比的这一行");
        var ladder = new (MxKind? Kind, string Label)[]
        {
            (MxKind.BookPenPaper, "A 书+笔·纸"),
            (MxKind.BookPenInk, "B 书+笔·墨"),
            (MxKind.BookMarkPaper, "C 书+红划"),
            (MxKind.PenRedSwash, "D 笔+红迹"),
            (MxKind.PenBlueSwash, "E 笔+蓝迹"),
            (MxKind.PenRedSwashDark, "F 笔+红·墨砖"),
            (null, "现状 马克笔"),
        };

        double rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is MxKind k ? MxRender(k, 16) : IcRender(AppIconKind.MarkerTile, 16), 6);
            c.DrawImage(b, new Rect(rx, y, b.PixelWidth, b.PixelHeight));
            Caption(c, it.Label, rx - 8, y + 100, 112);
            rx += 124;
        }
        double ry2 = y + 132;
        rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is MxKind k2 ? MxRender(k2, 32) : IcRender(AppIconKind.MarkerTile, 32), 4);
            c.DrawImage(b, new Rect(rx, ry2, b.PixelWidth, b.PixelHeight));
            rx += 124;
        }
        Caption(c, "32px ×4（桌面 48 差不多也在这一档）", 44, ry2 + 134, 860);

        double vx = 1030, vy = y;
        string[] verdicts =
        {
            "16px 上：C（书＋红划线）与 D（笔＋红笔迹）最稳 —— 都是一团深色 ＋ 一道红。A 的书描边会退成一圈浅灰，书散在砖里。",
            "B（墨书＋白笔）在 16px 上是一块墨 ＋ 一道白斜杠：最安静也最特别。F（墨砖）在深色任务栏上会与任务栏同色 —— 边界没了。",
            "D 与 E 只差笔迹的颜色：红＝批注、蓝＝书写。A/B/C 与 D/E/F 的分叉是「要不要把书搬进来」：书说的是「在什么上面批注」，笔迹说的是「正在批注」。",
            "我的排序：D（笔＋红笔迹，最像我们、也最稳）＞ A（书＋笔，最贴参考图①）＞ C（书＋红划线）。",
        };
        foreach (var s in verdicts) vy += Paragraph(c, s, vx, vy, SheetW - 60 - vx, 12.5, BodyBrush) + 8;

        y = Math.Max(ry2 + 160, vy) + 26;

        var box = new Rect(40, y, SheetW - 80, 196);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "④ 怎么选、怎么落地", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. 想最像「我们」：D —— 就是已落地那支马克笔 ＋ 一道红笔迹，同一套零件，任务栏上一眼认得出，16px 也不用赌。",
            "2. 想一眼说出「批注的是资料」：A / B / C 这条「书」线；书会占掉一半砖面，笔就得让位，气势比 D 小一档。",
            "3. 参考图的两块底色没有跟过来：我们的底＝白砖（主）或墨砖（对照）。若一定要一块彩色底，用批注红，不要引入墨绿 / 深蓝。",
            "4. 选定后：把对应形状写进 src/InkTeach/AppIconUi.cs（这张稿里的数一条条对得上），再跑 --makeicon 出七档 ico。",
            "5. 先试也行：design/图标候选v4/ 里每个候选都有多尺寸 .ico 与 256 的 .png，给快捷方式换图标就能在真任务栏上比。",
        };
        double ty = y + 42;
        foreach (var line in concl) { Text(c, line, 60, ty, 13, BodyBrush); ty += 24; }
        return y + 210;
    }

    // =====================================================================
    //  渲染与文件
    // =====================================================================

    static BitmapSource MxRender(MxKind kind, int size) => IcRender(size, c => MxDraw64(kind, c));

    static void MxDraw64(MxKind kind, DrawingContext c)
    {
        switch (kind)
        {
            case MxKind.BookPenPaper:
                IcTile(c);
                MxBook(c, fill: IcWhite, edge: IcInk, stack: C(0xE9, 0xEC, 0xF1));
                MxPen(c, IcInk);
                MxSquiggle(c, IcRed);
                break;

            case MxKind.BookPenInk:
                IcTile(c);
                MxBook(c, fill: IcInk, edge: null, stack: C(0x39, 0x3D, 0x45));
                MxPenOutlined(c);
                MxSquiggle(c, IcRed);
                break;

            case MxKind.BookMarkPaper:
                IcTile(c);
                MxBook(c, fill: IcWhite, edge: IcInk, stack: C(0xE9, 0xEC, 0xF1));
                MxUnderline(c, IcRed);
                break;

            case MxKind.PenRedSwash:
                IcTile(c);
                MxPen(c, IcInk);
                MxSwash(c, IcRed);
                break;

            case MxKind.PenBlueSwash:
                IcTile(c);
                MxPen(c, IcInk);
                MxSwash(c, IcBlue);
                break;

            case MxKind.PenRedSwashDark:
                MxTileDark(c);
                MxPenOutlined(c);
                MxSwash(c, IcRed);
                break;
        }
    }

    static void WriteMixCandidates(string outDir)
    {
        string dir = Path.Combine(outDir, "图标候选v4");
        Directory.CreateDirectory(dir);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        foreach (var it in MxFiles)
        {
            var frames = new BitmapSource[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) frames[i] = MxRender(it.Kind, sizes[i]);
            WriteIcIco(Path.Combine(dir, it.File + ".ico"), frames);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(MxRender(it.Kind, 256)));
            using (var fs = File.Create(Path.Combine(dir, it.File + ".png"))) enc.Save(fs);
            Console.WriteLine($"  图标候选v4：{it.File}.ico（{string.Join("/", sizes)}）＋ .png（256）");
        }
    }

    // =====================================================================
    //  零件一：书（64 格：两片页 x 9.5…54.5、y 21.5…45，外带一条页垛）
    // =====================================================================

    /// <summary>
    /// 打开的书 = 两片对开的页：左边一片、右边一片，各带一点点向外的倾角，
    /// 中间留 3 的书脊缝（透出砖面）—— 两块并排的方在这道缝 + 倾角里就变成了一本摊开的书。
    /// fill=白 → 纸白页（edge=墨，那圈描边就是纸的边界）；fill=墨 → 实心墨底（edge 传 null）。
    /// </summary>
    static void MxBook(DrawingContext c, Color? fill, Color? edge, Color stack)
    {
        var edgeBrush = edge is Color ec ? new SolidColorBrush(ec) : null;
        var fillBrush = new SolidColorBrush(fill ?? IcWhite);

        // 页垛：两片页底下那一层略窄、平底的边（书能坐在桌面上的那一条）——
        // 不给它描边：它只是"很多页"的一点暗示，纸白页上它就是一块很淡的灰。
        c.DrawRoundedRectangle(new SolidColorBrush(stack), null, new Rect(11, 36, 42, 12), 6, 6);

        // 左页、右页：各绕自己底角往外让 3.2°，中间就是书脊的那道缝
        MxPage(c, fillBrush, edgeBrush, new Rect(9.5, 21.5, 21.5, 23.5), new Point(10, 45.5), -3.2);
        MxPage(c, fillBrush, edgeBrush, new Rect(33, 21.5, 21.5, 23.5), new Point(54, 45.5), 3.2);
    }

    /// <summary>一页纸：21.5 × 23.5 的圆角方，绕某个点转一个小角度（页向外摊开的那点意思）。</summary>
    static void MxPage(DrawingContext c, Brush fill, Brush edge, Rect r, Point pivot, double deg)
    {
        c.PushTransform(new RotateTransform(deg, pivot.X, pivot.Y));
        if (edge != null) c.DrawRoundedRectangle(fill, new Pen(edge, 2.6), r, 3.4, 3.4);
        else c.DrawRoundedRectangle(fill, null, r, 3.4, 3.4);
        c.Pop();
    }

    // =====================================================================
    //  零件二：笔（就是已落地那支马克笔）与它的位置
    // =====================================================================

    /// <summary>所有候选共用同一个笔位：斜 −45°、笔心 (41, 24)、长 37 —— 笔尖落在 (27.9, 37.1)。</summary>
    static void MxPen(DrawingContext c, Color body)
        => PnRot(c, 41, 24, -45, dc => IkMarker(dc, 37, 12, body, IcRed, IcRed));

    /// <summary>
    /// 白笔（墨底上用）：先垫一圈比笔大 2.8 的墨色轮廓，再画白笔 —— 白笔画到砖面上时
    /// 还剩一根极淡的边（和砖边同一条规矩：浅底上的白物必须有一圈边界）。
    /// </summary>
    static void MxPenOutlined(DrawingContext c)
        => PnRot(c, 41, 24, -45, dc =>
        {
            IkMarker(dc, 39.8, 14.8, IcInk, IcInk, IcInk);
            IkMarker(dc, 37, 12, IcWhite, IcRed, IcRed);
        });

    // =====================================================================
    //  零件三：一道笔迹（从笔尖拖出去的）与那个大红勾
    // =====================================================================

    /// <summary>
    /// 笔迹：起笔就在笔尖 (27.9, 37.1)，先往下压、再往右扫、最后抬起来 ——
    /// 一笔成形的"正在写"，始终走在笔身下缘以外（不穿过笔）。
    /// </summary>
    static void MxSwash(DrawingContext c, Color color, double w = 3.4)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(27.9, 37.1), false, false);
            s.BezierTo(new Point(30.5, 42.8), new Point(33.5, 45.0), new Point(37.5, 44.6), true, false);
            s.BezierTo(new Point(46.0, 43.6), new Point(52.0, 38.0), new Point(55.0, 26.5), true, false);
        }
        g.Freeze();

        c.DrawGeometry(null, new Pen(new SolidColorBrush(color), w)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, g);
    }

    /// <summary>书上那一小截笔迹（只留在左页里）：从笔尖往左兜一个小弧，收笔往上翘一点。</summary>
    static void MxSquiggle(DrawingContext c, Color color, double w = 3.4)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(27.9, 37.1), false, false);
            s.BezierTo(new Point(24.0, 41.5), new Point(18.5, 42.8), new Point(14.8, 40.6), true, false);
            s.BezierTo(new Point(12.8, 39.2), new Point(12.6, 37.8), new Point(13.6, 36.8), true, false);
        }
        g.Freeze();

        c.DrawGeometry(null, new Pen(new SolidColorBrush(color), w)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, g);
    }

    /// <summary>书页上的一道红划线（比勾安静一档的"批注过"）：从 (13.5,38.5) 微弧着划到 (50.5,35.5)。</summary>
    static void MxUnderline(DrawingContext c, Color color, double w = 5.2)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(13.5, 38.5), false, false);
            s.BezierTo(new Point(24.0, 40.5), new Point(40.0, 39.0), new Point(50.5, 35.5), true, false);
        }
        g.Freeze();

        c.DrawGeometry(null, new Pen(new SolidColorBrush(color), w)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, g);
    }

    /// <summary>墨砖：深底对照用（白 12% 的边 —— 和深色工具条同一条规矩）。</summary>
    static void MxTileDark(DrawingContext c)
    {
        c.DrawRoundedRectangle(new SolidColorBrush(IcDarkBg),
                               new Pen(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x24)), 1),
                               new Rect(1.5, 1.5, 61, 61), 14, 14);
    }
}
