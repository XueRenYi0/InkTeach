using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesignSheet;

/// <summary>
/// v12：**「一支帅气的笔」** —— 面向 "Ink Teacher" 这个身份的图标第二轮。
///
/// 用户 2026-10-01 第二轮的原话：「其实我想要的就是一支比较帅气的、符合教师身份的笔。
/// 斜着、竖着都可以，要帅气、正式、简洁、美观、大气，因为我的名字叫 Ink Teacher。」
/// （第一轮选的马克笔已经落地进 src/InkTeach/AppIconUi.cs —— 这一轮是来换掉它的。）
///
/// 六个候选分成三条线：
///   · **笔尖特写**（A 斜 / B 竖＋一滴红墨）—— 最「墨」、最像招牌；
///   · **整支钢笔、露笔尖**（C 斜 / D 竖）—— 笔尖的仪式感 ＋ 一眼认得出是「一支笔」；
///   · **带帽钢笔**（E 斜，帽上那道笔夹）与 **红钢笔**（F，教师的那支红笔）。
///
/// 共用语言：白砖底（沿用已落地那张的底色）、近黑笔身、批注红做唯一的强调色，
/// 笔尖的中缝与呼吸孔用红色 —— 「Ink」和「批注」两件事都落在这一处。
/// 形状全部是矢量，和落地那条路（AppIconUi）一一对应；小尺寸照旧按真实像素渲染再放大。
/// </summary>
internal static partial class Program
{
    enum PenIcon
    {
        NibDiag,          // A 笔头特写 · 斜（笔尖 ＋ 一小截握位）
        NibVert,          // B 笔头特写 · 竖（＋红墨点）
        FountainDiag,     // C 整支钢笔 · 斜（露尖）
        FountainVert,     // D 整支钢笔 · 竖（露尖）
        CappedDiag,       // E 带帽钢笔 · 斜
        RedFountainDiag,  // F 红钢笔 · 斜（教师红笔）
    }

    /// <summary>笔尖的金属色：和近黑的笔身分开一档，「笔尖」才立得出来。</summary>
    static readonly Color IcGraphite = C(0x4B, 0x51, 0x5A);

    static readonly (PenIcon Kind, string Tag, string Title, string Note)[] PenCandidates =
    {
        (PenIcon.NibDiag, "A", "笔头特写 · 斜",
         "一支石墨色笔尖、红中缝 ＋ 红呼吸孔，斜 45°。最「墨」的一版 —— 一个笔头就是一块招牌，正式、没有多余的东西。"),
        (PenIcon.NibVert, "B", "笔头特写 · 竖",
         "同 A，立正，尖下多一滴红墨。竖版更像徽章、更正式；那滴墨是全身唯一的温度。"),
        (PenIcon.FountainDiag, "C", "整支钢笔 · 斜",
         "露尖的钢笔：石墨笔尖 ＋ 握位 ＋ 红环 ＋ 黑笔杆，斜 45°。既有笔尖的仪式感，又是一眼认得出的「一整支笔」——六支里最平衡的一支。"),
        (PenIcon.FountainVert, "D", "整支钢笔 · 竖",
         "同 C，立正朝下，像一支立在讲台上的笔，最贴「教师」这个姿势；代价是任务栏上它和「竖线类」图标有点像。"),
        (PenIcon.CappedDiag, "E", "带帽钢笔 · 斜",
         "合上帽的钢笔：圆头笔帽 ＋ 帽上那道笔夹，六个里最「正式」。看不见笔尖，但笔夹本身就是「钢笔」的招牌。"),
        (PenIcon.RedFountainDiag, "F", "红钢笔 · 斜",
         "把整支换成批注红、笔尖留石墨、白环 —— 教师的那支红笔。任务栏上最跳；气质更「批改」，少一点「墨」的沉稳。"),
    };

    /// <summary>单文件 ico / png 的文件名（跑这张稿时写进 design/图标候选v2/）。</summary>
    static readonly (PenIcon Kind, string File)[] PenFiles =
    {
        (PenIcon.NibDiag,         "A-钢笔尖-斜"),
        (PenIcon.NibVert,         "B-钢笔尖-竖"),
        (PenIcon.FountainDiag,    "C-整支钢笔-斜"),
        (PenIcon.FountainVert,    "D-整支钢笔-竖"),
        (PenIcon.CappedDiag,      "E-带帽钢笔-斜"),
        (PenIcon.RedFountainDiag, "F-红钢笔-斜"),
    };

    // =====================================================================
    //  出一张稿
    // =====================================================================

    static double DrawCoolPenSheet(DrawingContext c)
    {
        c.DrawRectangle(Brushes.White, null, new Rect(0, 0, SheetW, 3200));

        double y = 36;
        Text(c, "程序图标 · 设计稿 v2（一支帅气的笔 · Ink Teacher）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "用户 2026-10-01：「其实我想要的就是一支比较帅气的、符合教师身份的笔。斜着、竖着都可以 —— 因为我的名字叫 Ink Teacher。」",
             40, y + 44, 14, BodyBrush);
        Text(c, "沿用已落地那张的白砖底与批注红；六个候选都是矢量，小尺寸按真实像素渲染再最近邻放大。第一轮那支马克笔放在末尾的对照行里。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        y = Section(c, y, "① 六个候选（浅底 / 深底 / 真实尺寸 / 放大）");
        const double cw = 477, gap = 25, cardH = 520;
        double rowY = y + 8;
        for (int i = 0; i < PenCandidates.Length; i++)
        {
            double cx = 40 + (i % 3) * (cw + gap);
            double cy = rowY + (i / 3) * (cardH + 18);
            var cand = PenCandidates[i];
            IcCard(c, cx, cy, cw, s => PnRender(cand.Kind, s), cand.Tag, cand.Title, cand.Note);
        }
        y = rowY + 2 * (cardH + 18) + 6;

        y = Section(c, y, "② 小尺寸真容：16px（×6）与 32px（×4）—— 任务栏上比的这一行");
        var ladder = new (PenIcon? Kind, string Label)[]
        {
            (PenIcon.NibDiag, "A 尖·斜"),
            (PenIcon.NibVert, "B 尖·竖"),
            (PenIcon.FountainDiag, "C 钢·斜"),
            (PenIcon.FountainVert, "D 钢·竖"),
            (PenIcon.CappedDiag, "E 带帽"),
            (PenIcon.RedFountainDiag, "F 红笔"),
            (null, "现状 马克笔"),
        };

        double rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is PenIcon k ? PnRender(k, 16) : IcRender(AppIconKind.MarkerTile, 16), 6);
            c.DrawImage(b, new Rect(rx, y, b.PixelWidth, b.PixelHeight));
            Caption(c, it.Label, rx - 8, y + 100, 112);
            rx += 124;
        }
        double ry2 = y + 132;
        rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is PenIcon k2 ? PnRender(k2, 32) : IcRender(AppIconKind.MarkerTile, 32), 4);
            c.DrawImage(b, new Rect(rx, ry2, b.PixelWidth, b.PixelHeight));
            rx += 124;
        }
        Caption(c, "32px ×4（桌面 48 差不多也在这一档）", 44, ry2 + 134, 860);

        double vx = 1030, vy = y;
        string[] verdicts =
        {
            "16px 上最结实的是 C（整支钢笔·斜）与 E（带帽）：都是一条斜黑条 ＋ 红点，形状不靠细节撑着。",
            "A/B（笔尖特写）在 16px 上偏「针尖」，要靠真机看 —— 好处是它最独特，任务栏上一眼不是别人。",
            "D（竖）在 16px 上像一根竖线；F（红笔）在 16px 上是一块红 —— 都活得下来，气质各偏一边。",
            "斜着比竖着「帅气」（有动势），竖着比斜着「正式」（像徽章）—— 这两个字只能你自己拍。",
        };
        foreach (var s in verdicts) vy += Paragraph(c, s, vx, vy, SheetW - 60 - vx, 12.5, BodyBrush) + 8;

        y = Math.Max(ry2 + 160, vy) + 26;

        var box = new Rect(40, y, SheetW - 80, 196);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "③ 怎么选、怎么落地", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. 我的排序：C（整支钢笔·斜）＞ A（笔尖·斜）＞ E（带帽）—— C 兼顾「笔尖的仪式感」和「一眼是支笔」；A 最独特但要赌 16px。",
            "2. 要「正式」胜过「帅气」：D 或 E（竖 / 带帽），更像一枚校徽；要「帅气」：A / C / F 的斜向更带劲。",
            "3. 红只落在两处：笔尖的中缝与呼吸孔 / 一道笔环 —— 再多一分就花，再少一分就冷。",
            "4. 选定后我改 src/InkTeach/AppIconUi.cs 的画法（这张稿里的形状是一条条对得上的），再跑一次 --makeicon 出七档 ico；",
            "　 在这之前，design/图标候选v2/ 里每个候选都有多尺寸 .ico 和 256 的 .png，给快捷方式换图标就能真机比。",
            "5. 配色、笔身粗细、倾角（45°→35° 更飘逸）都是参数，选定方向后还能再精修一轮。",
        };
        double ty = y + 42;
        foreach (var line in concl) { Text(c, line, 60, ty, 13, BodyBrush); ty += 24; }
        return y + 210;
    }

    // =====================================================================
    //  渲染与文件
    // =====================================================================

    static BitmapSource PnRender(PenIcon kind, int size) => IcRender(size, c => PnDraw64(kind, c));

    static void PnDraw64(PenIcon kind, DrawingContext c)
    {
        switch (kind)
        {
            case PenIcon.NibDiag:
                IcTile(c);
                PnRot(c, 32, 32, -45, dc => PnNibAt(dc, -27, 21, 21, IcGraphite, IcRed, 1.35));
                break;

            case PenIcon.NibVert:
                IcTile(c);
                PnRot(c, 32, 32, -90, dc =>
                {
                    PnNibAt(dc, -24, 22, 21, IcGraphite, IcRed, 1.35);
                    dc.DrawEllipse(new SolidColorBrush(IcRed), null, new Point(-28.4, 0), 2.4, 2.4);   // 一滴红墨
                });
                break;

            case PenIcon.FountainDiag:
                IcTile(c);
                PnRot(c, 32, 32, -45, dc => PnFountain(dc, 58, 10.4, IcInk, IcGraphite, IcRed));
                break;

            case PenIcon.FountainVert:
                IcTile(c);
                PnRot(c, 32, 32, -90, dc => PnFountain(dc, 55, 10.4, IcInk, IcGraphite, IcRed));
                break;

            case PenIcon.CappedDiag:
                IcTile(c);
                PnRot(c, 32, 32, -45, dc => PnCapped(dc, 58, 11.4, IcInk, IcRed));
                break;

            case PenIcon.RedFountainDiag:
                IcTile(c);
                PnRot(c, 32, 32, -45, dc => PnFountain(dc, 58, 10.4, IcRed, IcGraphite, IcWhite));
                break;
        }
    }

    static void WritePenCandidates(string outDir)
    {
        string dir = Path.Combine(outDir, "图标候选v2");
        Directory.CreateDirectory(dir);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        foreach (var it in PenFiles)
        {
            var frames = new BitmapSource[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) frames[i] = PnRender(it.Kind, sizes[i]);
            WriteIcIco(Path.Combine(dir, it.File + ".ico"), frames);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(PnRender(it.Kind, 256)));
            using (var fs = File.Create(Path.Combine(dir, it.File + ".png"))) enc.Save(fs);
            Console.WriteLine($"  图标候选v2：{it.File}.ico（{string.Join("/", sizes)}）＋ .png（256）");
        }
    }

    // =====================================================================
    //  笔的零件（局部坐标：笔尖在 -X 端，宽在 Y 方向）
    // =====================================================================

    /// <summary>把局部坐标摆到 64 格里的某个位置和角度（斜 = −45°，竖 = −90°，笔尖朝下）。</summary>
    static void PnRot(DrawingContext c, double cx, double cy, double deg, Action<DrawingContext> draw)
    {
        c.PushTransform(new TranslateTransform(cx, cy));
        c.PushTransform(new RotateTransform(deg));
        draw(c);
        c.Pop();
        c.Pop();
    }

    /// <summary>
    /// 钢笔尖：肩 → 尖（带一小段圆头），最宽处在从尖数 44% 的地方，往底座收一点
    /// （真笔尖就是「中间最宽、底部略收」）；底座是半圆。
    /// 中缝从尖拉到呼吸孔，孔在 58% 处、是个小红圆。
    /// </summary>
    static void PnNibAt(DrawingContext c, double tipX, double baseX, double wid, Color nibColor, Color slitColor, double slitW)
    {
        double L = baseX - tipX, hw = wid / 2, hb = hw * 0.88;
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(tipX, 0), true, true);
            s.LineTo(new Point(tipX + L * 0.12, -hw * 0.24), true, false);
            s.LineTo(new Point(tipX + L * 0.30, -hw * 0.86), true, false);
            s.LineTo(new Point(tipX + L * 0.44, -hw), true, false);
            s.LineTo(new Point(tipX + L * 0.72, -hb), true, false);
            s.LineTo(new Point(baseX, -hb), true, false);
            s.ArcTo(new Point(baseX, hb), new Size(hb, hb), 0, false, SweepDirection.Clockwise, true, false);
            s.LineTo(new Point(tipX + L * 0.72, hb), true, false);
            s.LineTo(new Point(tipX + L * 0.44, hw), true, false);
            s.LineTo(new Point(tipX + L * 0.30, hw * 0.86), true, false);
            s.LineTo(new Point(tipX + L * 0.12, hw * 0.24), true, false);
        }
        g.Freeze();
        var nb = new SolidColorBrush(nibColor);
        // 同色描一遍、圆角接头：把尖和肩都磨圆一点，小尺寸下不扎像素
        c.DrawGeometry(nb, new Pen(nb, 2.0) { LineJoin = PenLineJoin.Round }, g);

        // 中缝 ＋ 呼吸孔（红）：孔比缝略大一点点，缩到 32px 时并成一个红点，正好
        var sp = new Pen(new SolidColorBrush(slitColor), slitW)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double holeX = tipX + L * 0.58;
        c.DrawLine(sp, new Point(tipX + slitW * 1.0, 0), new Point(holeX - slitW * 0.7, 0));
        c.DrawEllipse(new SolidColorBrush(slitColor), null, new Point(holeX, 0), slitW * 1.35, slitW * 1.35);

        // 高光：底座那一段的上侧一道很淡的白线 —— 「抛光过」的意思，只在 48px 以上看得见
        var hl = new Pen(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x22)), Math.Max(0.9, hw * 0.15))
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(hl, new Point(tipX + L * 0.62, -hw * 0.58), new Point(baseX - L * 0.14, -hw * 0.58));
    }

    /// <summary>
    /// 整支钢笔（露尖）：笔尖 ＋ 握位（略细）＋ 一道强调色笔环 ＋ 笔杆（尾端圆头）。
    /// <paramref name="accent"/> 同时用在笔环和笔尖的中缝/呼吸孔上 —— 全身只有这一处彩色。
    /// </summary>
    static void PnFountain(DrawingContext c, double len, double wid, Color body, Color nib, Color accent)
    {
        double x0 = -len / 2, x1 = len / 2, hw = wid / 2;
        double nibLen = len * 0.30;
        double gripX = x0 + nibLen * 0.86, gripW = len * 0.10, bandW = len * 0.035;

        PnNibAt(c, x0, x0 + nibLen, wid * 0.97, nib, accent, Math.Max(1.35, wid * 0.12));

        var b = new SolidColorBrush(body);
        c.DrawRoundedRectangle(b, null, new Rect(gripX, -hw * 0.82, gripW, hw * 1.64), hw * 0.30, hw * 0.30);
        c.DrawRoundedRectangle(new SolidColorBrush(accent), null,
            new Rect(gripX + gripW - 0.6, -hw * 0.90, bandW, hw * 1.80), bandW * 0.40, bandW * 0.40);

        double bL = gripX + gripW + bandW - 1.0;
        c.DrawRoundedRectangle(b, null, new Rect(bL, -hw, x1 - bL, wid), hw * 0.80, hw * 0.80);

        // 高光：沿上侧一道很淡的白线 —— 只在 48px 以上看得见，是「抛光过」的那点意思
        var hl = new Pen(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x22)), Math.Max(0.9, wid * 0.085))
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(hl, new Point(bL + wid * 0.8, -hw * 0.48), new Point(x1 - wid * 0.9, -hw * 0.48));
    }

    /// <summary>
    /// 带帽钢笔：圆头笔帽（＋帽上那道凸出来的笔夹）＋ 接缝处一道强调色笔环 ＋ 笔杆。
    /// 笔夹**凸出轮廓**而不是叠一条同色的线 —— 同色的线在黑笔帽上根本看不见。
    /// </summary>
    static void PnCapped(DrawingContext c, double len, double wid, Color body, Color accent)
    {
        double x0 = -len / 2, x1 = len / 2, hw = wid / 2;
        double capLen = len * 0.46, capW = hw * 1.18;
        var b = new SolidColorBrush(body);

        c.DrawRoundedRectangle(b, null, new Rect(x0, -capW, capLen, capW * 2), capW * 0.60, capW * 0.60);
        // 笔夹：凸出轮廓的一整条（同色的线在黑帽上根本看不见，所以要"长出来"）
        c.DrawRoundedRectangle(b, null, new Rect(x0 + capLen * 0.10, -capW - 2.6, capLen * 0.66, 4.6), 2.1, 2.1);

        double joint = x0 + capLen - 0.3, ringW = Math.Max(1.6, len * 0.032);
        c.DrawRoundedRectangle(new SolidColorBrush(accent), null,
            new Rect(joint, -hw * 1.06, ringW, hw * 2.12), ringW * 0.45, ringW * 0.45);
        c.DrawRoundedRectangle(b, null, new Rect(joint + ringW - 0.8, -hw, x1 - joint - ringW + 0.8, wid), hw * 0.72, hw * 0.72);

        var hl = new Pen(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, 0x22)), Math.Max(0.9, wid * 0.085))
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(hl, new Point(joint + ringW + wid * 0.6, -hw * 0.48), new Point(x1 - wid * 0.9, -hw * 0.48));
    }
}
