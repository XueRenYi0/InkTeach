using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesignSheet;

/// <summary>
/// v13：**「一支批注软件的笔」** —— 图标第三轮（v2 的钢笔全部作废）。
///
/// 用户 2026-10-01 第三轮的原话：「不想用钢笔，因为我们这个软件里面没有钢笔这个功能。
/// 它就是常用的批注，我想展示出来，它是一个批注软件的笔就可以了。」
///
/// 所以这一轮回到软件里真正有的那几支笔：**马克笔**（笔/荧光笔那一类）、
/// **中性笔**（最常见的写字笔）、**触控笔**（学校大屏上就是它），
/// 另加两个"正在批注"的说法（笔尖拉出红墨 / 笔下一道红下划线）。
///
/// 美学口径沿用上一轮磨出来的四条（见 调研-图标-应用图标.md 第六节）：
///   · 笔尖/金属件用石墨色，和近黑笔身分开一档；
///   · 红只落在两处：笔头（或笔珠）＋ 一道环；
///   · 笔身细长（宽 ≈ 长的 20%）；
///   · 笔夹凸出轮廓（同色的线在黑帽上根本看不见）。
///
/// 小尺寸照旧：按真实像素渲染、最近邻放大；末尾把六支和"现状那支马克笔"并排。
/// </summary>
internal static partial class Program
{
    enum InkPenKind
    {
        MarkerDiag,       // A 马克笔 · 斜（现在这支的精修版）
        MarkerVert,       // B 马克笔 · 竖
        RollerDiag,       // C 中性笔 · 斜（锥形金属笔头＋笔珠）
        StylusDiag,       // D 触控笔 · 斜（细长、圆头、侧键）
        MarkerTrail,      // E 马克笔 · 斜 ＋ 笔尖拉出一条红墨
        MarkerUnderline,  // F 马克笔 · 斜 ＋ 笔下一道红下划线
    }

    static readonly (InkPenKind Kind, string Tag, string Title, string Note)[] InkPenCandidates =
    {
        (InkPenKind.MarkerDiag, "A", "马克笔 · 斜",
         "最贴近软件里那支：细长笔身、锥形过渡、红切角笔头，笔帽 ＋ 凸出的笔夹 ＋ 一道红环。它就是已经落地那支的精修版 —— 把「胖子」磨成「长的」。"),
        (InkPenKind.MarkerVert, "B", "马克笔 · 竖",
         "同 A 立正。竖着更像「立在讲台上的笔」，正式；任务栏上它就是一根竖条，最安静。"),
        (InkPenKind.RollerDiag, "C", "中性笔 · 斜",
         "石墨锥形笔头 ＋ 笔珠：最「常见」的一支笔，不用猜就知道是笔，也最正式。笔珠缩到 16px 会消失，靠锥形撑着。"),
        (InkPenKind.StylusDiag, "D", "触控笔 · 斜",
         "细长、圆头、笔身一道红键 —— 学校大屏上就是这支。它说的是「在屏幕上写」，比马克笔更贴设备。"),
        (InkPenKind.MarkerTrail, "E", "马克笔 ＋ 红墨迹 · 斜",
         "笔尖正拉出一条红墨迹：不说「这是一支笔」，而是说「它正在批注」。任务栏上多出那一点红，辨识度反而更高。"),
        (InkPenKind.MarkerUnderline, "F", "马克笔 ＋ 红下划线 · 斜",
         "笔下一道红下划线：批注的经典动作（划线、圈重点），比 E 安静一档，也更像「老师的红笔」。"),
    };

    static readonly (InkPenKind Kind, string File)[] InkPenFiles =
    {
        (InkPenKind.MarkerDiag,      "A-马克笔-斜"),
        (InkPenKind.MarkerVert,      "B-马克笔-竖"),
        (InkPenKind.RollerDiag,      "C-中性笔-斜"),
        (InkPenKind.StylusDiag,      "D-触控笔-斜"),
        (InkPenKind.MarkerTrail,     "E-马克笔-红墨迹"),
        (InkPenKind.MarkerUnderline, "F-马克笔-红下划线"),
    };

    // =====================================================================
    //  出一张稿
    // =====================================================================

    static double DrawInkPenSheet(DrawingContext c)
    {
        c.DrawRectangle(Brushes.White, null, new Rect(0, 0, SheetW, 3200));

        double y = 36;
        Text(c, "程序图标 · 设计稿 v3（一支批注软件的笔）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "用户 2026-10-01：「不想用钢笔 —— 软件里没有钢笔这个功能。它就是常用的批注，我想展示出来，它是一个批注软件的笔就可以了。」",
             40, y + 44, 14, BodyBrush);
        Text(c, "所以六支都回到软件里真有的笔：马克笔 / 中性笔 / 触控笔，另加两个「正在批注」的说法。美学口径沿用上一轮：石墨色笔头、细长笔身、红只在笔头与一道环。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        y = Section(c, y, "① 六个候选（浅底 / 深底 / 真实尺寸 / 放大）");
        const double cw = 477, gap = 25, cardH = 520;
        double rowY = y + 8;
        for (int i = 0; i < InkPenCandidates.Length; i++)
        {
            double cx = 40 + (i % 3) * (cw + gap);
            double cy = rowY + (i / 3) * (cardH + 18);
            var cand = InkPenCandidates[i];
            IcCard(c, cx, cy, cw, s => IkRender(cand.Kind, s), cand.Tag, cand.Title, cand.Note);
        }
        y = rowY + 2 * (cardH + 18) + 6;

        y = Section(c, y, "② 小尺寸真容：16px（×6）与 32px（×4）—— 任务栏上比的这一行");
        var ladder = new (InkPenKind? Kind, string Label)[]
        {
            (InkPenKind.MarkerDiag, "A 马克·斜"),
            (InkPenKind.MarkerVert, "B 马克·竖"),
            (InkPenKind.RollerDiag, "C 中性笔"),
            (InkPenKind.StylusDiag, "D 触控笔"),
            (InkPenKind.MarkerTrail, "E 带墨迹"),
            (InkPenKind.MarkerUnderline, "F 带下划线"),
            (null, "现状 马克笔"),
        };

        double rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is InkPenKind k ? IkRender(k, 16) : IcRender(AppIconKind.MarkerTile, 16), 6);
            c.DrawImage(b, new Rect(rx, y, b.PixelWidth, b.PixelHeight));
            Caption(c, it.Label, rx - 8, y + 100, 112);
            rx += 124;
        }
        double ry2 = y + 132;
        rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is InkPenKind k2 ? IkRender(k2, 32) : IcRender(AppIconKind.MarkerTile, 32), 4);
            c.DrawImage(b, new Rect(rx, ry2, b.PixelWidth, b.PixelHeight));
            rx += 124;
        }
        Caption(c, "32px ×4（桌面 48 差不多也在这一档）", 44, ry2 + 134, 860);

        double vx = 1030, vy = y;
        string[] verdicts =
        {
            "16px 上 A / B / C / D 都是一条（斜或竖的）黑条：A/C/D 带一点灰头、B 最安静。",
            "E / F 多一条红线 —— 在 16px 上并成一点 / 一条，但「批注」这个意思活到了最后。",
            "C（中性笔）与现状那支马克笔在 16px 上几乎一模一样，差别只在笔头：一个是锥、一个是切角。",
            "要不要「正在批注」的那一笔红（E/F），是这一轮唯一的风格分叉：不加最干净，加了最会说故事。",
        };
        foreach (var s in verdicts) vy += Paragraph(c, s, vx, vy, SheetW - 60 - vx, 12.5, BodyBrush) + 8;

        y = Math.Max(ry2 + 160, vy) + 26;

        var box = new Rect(40, y, SheetW - 80, 196);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "③ 怎么选、怎么落地", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. 我的排序：A（马克笔·斜）＞ C（中性笔）＞ D（触控笔）—— A 就是「我们这支笔」的精修版，C 最通用、D 最贴设备。",
            "2. 要最安静：B（竖）；要最会说话：E（笔尖带墨）是「正在批注」最直接的说法，F 是它的安静版。",
            "3. 上一轮磨出来的四条（石墨笔头、细长、红只用两处、笔夹凸出）这一轮全部保留 —— 它们才是「帅气」的来源，和是不是钢笔无关。",
            "4. 选定后我把对应形状写进 src/InkTeach/AppIconUi.cs（这张稿里的数一条条对得上），再跑 --makeicon 出七档 ico；",
            "　 现在也可以先试：design/图标候选v3/ 里每支都有多尺寸 .ico 和 256 的 .png，给快捷方式换图标就能真机比。",
            "5. 笔头形状（切角/锥/圆头）、倾角、笔身粗细都还是参数，选定方向后还能再精修一轮。",
        };
        double ty = y + 42;
        foreach (var line in concl) { Text(c, line, 60, ty, 13, BodyBrush); ty += 24; }
        return y + 210;
    }

    // =====================================================================
    //  渲染与文件
    // =====================================================================

    static BitmapSource IkRender(InkPenKind kind, int size) => IcRender(size, c => IkDraw64(kind, c));

    static void IkDraw64(InkPenKind kind, DrawingContext c)
    {
        switch (kind)
        {
            case InkPenKind.MarkerDiag:
                IcTile(c);
                PnRot(c, 32, 32, -45, dc => IkMarker(dc, 52, 11, IcInk, IcRed, IcRed));
                break;

            case InkPenKind.MarkerVert:
                IcTile(c);
                PnRot(c, 32, 32, -90, dc => IkMarker(dc, 50, 11, IcInk, IcRed, IcRed));
                break;

            case InkPenKind.RollerDiag:
                IcTile(c);
                PnRot(c, 32, 32, -45, dc => IkRoller(dc, 54, 10.5, IcInk, IcGraphite, IcRed));
                break;

            case InkPenKind.StylusDiag:
                IcTile(c);
                PnRot(c, 32, 32, -45, dc => IkStylus(dc, 54, 8.5, IcInk, IcGraphite, IcRed));
                break;

            case InkPenKind.MarkerTrail:
                IcTile(c);
                // 笔整体往右上挪一点，给笔尖那条红墨留出地方
                PnRot(c, 35, 29, -45, dc =>
                {
                    IkTrail(dc, -26);
                    IkMarker(dc, 52, 11, IcInk, IcRed, IcRed);
                });
                break;

            case InkPenKind.MarkerUnderline:
                IcTile(c);
                PnRot(c, 33, 27, -45, dc => IkMarker(dc, 49, 10.5, IcInk, IcRed, IcRed));
                c.DrawRoundedRectangle(new SolidColorBrush(IcRed), null, new Rect(12, 51, 40, 4.6), 2.3, 2.3);
                break;
        }
    }

    static void WriteInkPenCandidates(string outDir)
    {
        string dir = Path.Combine(outDir, "图标候选v3");
        Directory.CreateDirectory(dir);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        foreach (var it in InkPenFiles)
        {
            var frames = new BitmapSource[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) frames[i] = IkRender(it.Kind, sizes[i]);
            WriteIcIco(Path.Combine(dir, it.File + ".ico"), frames);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(IkRender(it.Kind, 256)));
            using (var fs = File.Create(Path.Combine(dir, it.File + ".png"))) enc.Save(fs);
            Console.WriteLine($"  图标候选v3：{it.File}.ico（{string.Join("/", sizes)}）＋ .png（256）");
        }
    }

    // =====================================================================
    //  三支笔（局部坐标：笔尖在 -X，宽在 Y；都带笔帽与凸出的笔夹）
    // =====================================================================

    /// <summary>
    /// 马克笔：红色切角笔头 → 锥形过渡 → 笔身 → 红环 → 笔帽（圆头）＋ 笔夹。
    /// 和已经落地那支的区别：笔身从"胖胶囊"改成细长，笔头前面多一段锥形的过渡
    /// （原来是一个直角台阶，放大看很生硬）。
    /// </summary>
    static void IkMarker(DrawingContext c, double len, double wid, Color body, Color tip, Color accent)
    {
        double x0 = -len / 2, x1 = len / 2, hw = wid / 2;
        double tipLen = len * 0.20, chisel = len * 0.09;
        double capLen = len * 0.30, capW = hw * 1.12;
        var b = new SolidColorBrush(body);

        // ① 红切角笔头
        var tg = new StreamGeometry();
        using (var s = tg.Open())
        {
            s.BeginFigure(new Point(x0, -hw * 0.34), true, true);
            s.LineTo(new Point(x0 + chisel + 2, -hw * 0.66), true, false);
            s.LineTo(new Point(x0 + chisel + 2, hw * 0.66), true, false);
            s.LineTo(new Point(x0, hw * 0.34), true, false);
        }
        tg.Freeze();
        var tb = new SolidColorBrush(tip);
        c.DrawGeometry(tb, new Pen(tb, 1.8) { LineJoin = PenLineJoin.Round }, tg);

        // ② 锥形过渡（笔身宽 → 笔头宽）
        double bodyL = x0 + tipLen;
        var cg = new StreamGeometry();
        using (var s = cg.Open())
        {
            s.BeginFigure(new Point(bodyL + 0.5, -hw), true, true);
            s.LineTo(new Point(x0 + chisel + 1.0, -hw * 0.60), true, false);
            s.LineTo(new Point(x0 + chisel + 1.0, hw * 0.60), true, false);
            s.LineTo(new Point(bodyL + 0.5, hw), true, false);
        }
        cg.Freeze();
        c.DrawGeometry(b, new Pen(b, 1.6) { LineJoin = PenLineJoin.Round }, cg);

        // ③ 笔身
        double bodyR = x1 - capLen * 0.45;
        c.DrawRoundedRectangle(b, null, new Rect(bodyL, -hw, bodyR - bodyL, wid), hw * 0.5, hw * 0.5);

        // ④ 红环 ＋ 笔帽 ＋ 笔夹
        double ringW = Math.Max(1.4, len * 0.030);
        c.DrawRoundedRectangle(new SolidColorBrush(accent), null,
            new Rect(bodyR - ringW * 0.4, -hw * 1.02, ringW, hw * 2.04), ringW * 0.45, ringW * 0.45);
        c.DrawRoundedRectangle(b, null, new Rect(bodyR + ringW * 0.4, -capW, x1 - bodyR - ringW * 0.4, capW * 2), capW * 0.55, capW * 0.55);
        c.DrawRoundedRectangle(b, null, new Rect(x1 - capLen * 0.94, -capW - 1.8, capLen * 0.62, 3.6), 1.7, 1.7);

        // ⑤ 高光
        var hl = new Pen(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x22)), Math.Max(0.9, hw * 0.14))
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(hl, new Point(bodyL + wid * 0.9, -hw * 0.50), new Point(bodyR - wid * 0.6, -hw * 0.50));
    }

    /// <summary>中性笔：石墨锥形笔头 ＋ 笔珠，其余（笔身/红环/笔帽/笔夹/高光）和马克笔同一套。</summary>
    static void IkRoller(DrawingContext c, double len, double wid, Color body, Color metal, Color accent)
    {
        double x0 = -len / 2, x1 = len / 2, hw = wid / 2;
        double coneLen = len * 0.17, capLen = len * 0.30, capW = hw * 1.12;
        var b = new SolidColorBrush(body);
        var mb = new SolidColorBrush(metal);

        var cg = new StreamGeometry();
        using (var s = cg.Open())
        {
            s.BeginFigure(new Point(x0 + coneLen, -hw), true, true);
            s.LineTo(new Point(x0 + 2.2, -hw * 0.30), true, false);
            s.LineTo(new Point(x0 + 2.2, hw * 0.30), true, false);
            s.LineTo(new Point(x0 + coneLen, hw), true, false);
        }
        cg.Freeze();
        c.DrawGeometry(mb, new Pen(mb, 1.5) { LineJoin = PenLineJoin.Round }, cg);
        c.DrawEllipse(mb, null, new Point(x0 + 2.0, 0), hw * 0.30, hw * 0.30);   // 笔珠

        double bodyL = x0 + coneLen - 1.0, bodyR = x1 - capLen * 0.45;
        c.DrawRoundedRectangle(b, null, new Rect(bodyL, -hw, bodyR - bodyL, wid), hw * 0.5, hw * 0.5);

        double ringW = Math.Max(1.4, len * 0.030);
        c.DrawRoundedRectangle(new SolidColorBrush(accent), null,
            new Rect(bodyR - ringW * 0.4, -hw * 1.02, ringW, hw * 2.04), ringW * 0.45, ringW * 0.45);
        c.DrawRoundedRectangle(b, null, new Rect(bodyR + ringW * 0.4, -capW, x1 - bodyR - ringW * 0.4, capW * 2), capW * 0.55, capW * 0.55);
        c.DrawRoundedRectangle(b, null, new Rect(x1 - capLen * 0.94, -capW - 1.8, capLen * 0.62, 3.6), 1.7, 1.7);

        var hl = new Pen(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x22)), Math.Max(0.9, hw * 0.14))
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(hl, new Point(bodyL + wid * 0.9, -hw * 0.50), new Point(bodyR - wid * 0.6, -hw * 0.50));
    }

    /// <summary>触控笔：整根细长胶囊 ＋ 石墨圆头，笔身上一道红键（侧键）。</summary>
    static void IkStylus(DrawingContext c, double len, double wid, Color body, Color metal, Color accent)
    {
        double x0 = -len / 2, x1 = len / 2, hw = wid / 2;
        var b = new SolidColorBrush(body);
        var mb = new SolidColorBrush(metal);

        c.DrawRoundedRectangle(b, null, new Rect(x0 + len * 0.13, -hw, len * 0.87, wid), hw * 0.9, hw * 0.9);

        var tg = new StreamGeometry();
        using (var s = tg.Open())
        {
            s.BeginFigure(new Point(x0 + len * 0.16, -hw), true, true);
            s.LineTo(new Point(x0 + 3.2, -hw * 0.62), true, false);
            s.LineTo(new Point(x0, -hw * 0.28), true, false);
            s.LineTo(new Point(x0, hw * 0.28), true, false);
            s.LineTo(new Point(x0 + 3.2, hw * 0.62), true, false);
            s.LineTo(new Point(x0 + len * 0.16, hw), true, false);
        }
        tg.Freeze();
        c.DrawGeometry(mb, new Pen(mb, 1.4) { LineJoin = PenLineJoin.Round }, tg);

        // 侧键
        c.DrawRoundedRectangle(new SolidColorBrush(accent), null,
            new Rect(x0 + len * 0.30, -hw * 0.34, len * 0.10, hw * 0.68), hw * 0.25, hw * 0.25);

        var hl = new Pen(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x22)), Math.Max(0.9, hw * 0.14))
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(hl, new Point(x0 + len * 0.24, -hw * 0.48), new Point(x1 - len * 0.12, -hw * 0.48));
    }

    /// <summary>笔尖那条"正在写"的红墨：从笔尖顺着笔的方向拉一小段（略带手写的斜度）。</summary>
    static void IkTrail(DrawingContext c, double tipX)
    {
        var pen = new Pen(new SolidColorBrush(IcRed), 2.6)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(pen, new Point(tipX + 1.5, -0.4), new Point(tipX - 10.5, 2.6));
    }
}
