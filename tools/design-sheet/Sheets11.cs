using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesignSheet;

/// <summary>
/// v11：**程序图标**（任务栏 / 开始菜单 / 安装包上那一个）的候选与现状对照。
///
/// 由头（用户 2026-10-01）：「软件图标有点小气，想用简单大气的 —— 比如戴眼镜的笔？
/// 或者参考 InkClass。」
///
/// 这张稿的三条规矩：
///   1. 所有候选都是**矢量**画的，和选定后 --makeicon 那条路对得上（画一份、出一档，
///      不再手拼图片）；
///   2. 小尺寸一律**按真实像素渲染、再最近邻放大** —— 16px 上还剩什么形，
///      只有这样才能看见（把大图缩小是自欺欺人）；
///   3. 每个候选都给浅底 / 深底 / 真实 64·48·32·24·16 / 16 与 32 的放大，
///      最后再把所有候选的 16px 并成一排 —— 任务栏（32px）和桌面（48px）上
///      能不能认，以"真实像素"那一行为准。
///
/// 另外：跑这张稿会**顺带写** design/图标候选/ 下每个候选的多尺寸 .ico 与 256 的 .png，
/// 可以给快捷方式换个图标、在桌面上先试（不动 exe 自带的那个）。
/// </summary>
internal static partial class Program
{
    // ---- 图标自己的色（和引擎的墨色、批注红一致）----
    static readonly Color IcInk = C(0x1B, 0x1B, 0x1F);
    static readonly Color IcRed = C(0xF2, 0x2E, 0x2E);
    static readonly Color IcWhite = C(0xFF, 0xFF, 0xFF);
    static readonly Color IcTileEdge = C(0xE3, 0xE6, 0xEB);
    static readonly Color IcLightBg = C(0xF4, 0xF5, 0xF7);
    static readonly Color IcDarkBg = C(0x1B, 0x1B, 0x1E);

    enum AppIconKind
    {
        Current,       // 现在：白球 ＋ 红圈 ＋ 细笔
        GlassesWhite,  // A 戴眼镜的笔 · 白镜
        GlassesRed,    // B 戴眼镜的笔 · 红镜
        GlassesTile,   // C 戴眼镜的笔 · 白砖
        MarkerTile,    // D 一支大笔 · 白砖（InkClass 式）
        RingPen,       // E 红圈满格（保守档）
        RedCheck,      // F 大红勾（口味项）
    }

    /// <summary>候选表：卡片、单文件 ico、末尾的小尺寸对照都用这一张表。</summary>
    static readonly (AppIconKind Kind, string Tag, string Title, string Note)[] IcCandidates =
    {
        (AppIconKind.GlassesWhite, "A", "戴眼镜的笔 · 白镜",
         "用户点名的梗：黑笔身、红笔尖，戴一副白色圆框眼镜（镜片留黑，不糊成一块）。深底上最亮；16px 时退成两个白点 —— 形还在。"),
        (AppIconKind.GlassesRed, "B", "戴眼镜的笔 · 红镜",
         "同 A，但镜圈换成批注红、镜片填白：跟笔尖同一个红。浅底比 A 抢眼；16px 上红圈和白片先糊成一团，剩一块红白光。"),
        (AppIconKind.GlassesTile, "C", "戴眼镜的笔 · 白砖",
         "眼镜接上 InkClass 的底：黑框、白镜片，一块白砖兜底。三个眼镜方案里小尺寸最稳，也最像「一个应用」而不是一片浮着的图形。"),
        (AppIconKind.MarkerTile, "D", "一支大笔 · 白砖 ★已落地",
         "InkClass 那条路线的最短版：一支大笔、一块白砖、一颗红笔尖。16px 也不用赌；代价是「像邻居」——想拉开距离就靠红笔尖与切角的笔尖。"
         + "　★ 2026-10-01 用户选定了这一支（「不要眼镜，就要简单大气的笔」）。"),
        (AppIconKind.RingPen, "E", "红圈满格",
         "保守档：构图不动（白球 ＋ 红圈 ＋ 笔），只把圈从 2.5 加粗到 4.6、笔从占球径 36% 放大到 60%。老用户零学习成本，本质还是那个球。"),
        (AppIconKind.RedCheck, "F", "大红勾",
         "老师的大红勾：最简单、最大气，16px 也活着。代价是读起来更像「批改 / 已完成」，不像「批注工具」——口味项。"),
    };

    /// <summary>单文件 ico / png 的文件名（跑这张稿时写进 design/图标候选/）。</summary>
    static readonly (AppIconKind Kind, string File)[] IcFiles =
    {
        (AppIconKind.GlassesWhite, "A-戴眼镜的笔-白镜"),
        (AppIconKind.GlassesRed,   "B-戴眼镜的笔-红镜"),
        (AppIconKind.GlassesTile,  "C-戴眼镜的笔-白砖"),
        (AppIconKind.MarkerTile,   "D-一支大笔-白砖"),
        (AppIconKind.RingPen,      "E-红圈满格"),
        (AppIconKind.RedCheck,     "F-大红勾"),
    };

    // =====================================================================
    //  出一张稿
    // =====================================================================

    static double DrawAppIconSheet(DrawingContext c)
    {
        // 保底：内容超过 RenderSheet 预铺的 2400 时，底下也别露透明
        c.DrawRectangle(Brushes.White, null, new Rect(0, 0, SheetW, 3200));

        double y = 36;
        Text(c, "程序图标 · 设计稿 v1（简单、大气）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "由头：用户 2026-10-01 ——「软件图标有点小气，想用简单大气的，比如戴眼镜的笔？或者参考 InkClass。」",
             40, y + 44, 14, BodyBrush);
        Text(c, "图标全部按真实像素出图（16…256），小尺寸用最近邻放大 —— 任务栏（32px）和桌面（48px）上认不认得出，以「真实尺寸」那一行为准。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        y = Section(c, y, "① 先摆在一起：「现在」那颗球 vs InkClass");
        double t1 = y;

        // 现在
        IcChecker(c, 40, t1, 180, 180);
        c.DrawImage(IcRender(AppIconKind.Current, 180), new Rect(40, t1, 180, 180));
        Text(c, "现在：白球 ＋ 红圈 ＋ 细笔", 40, t1 + 186, 13, BodyBrush, bold: true);
        Text(c, "（--makeicon 出的就是它：收起态那颗球）", 40, t1 + 204, 12, NoteBrush);
        IcSizeLadder(c, 40, t1 + 228, s => IcRender(AppIconKind.Current, s));
        IcZoomRow(c, 40, t1 + 228 + 92, s => IcRender(AppIconKind.Current, s));

        // InkClass（本机资源原图，缺了就画一张描摹版）
        var inkClass = IcTryLoad(Path.Combine("design", "参考-InkClass图标-原版.jpg"));
        if (inkClass != null) c.DrawImage(inkClass, new Rect(280, t1, 180, 180));
        else c.DrawImage(IcRender(AppIconKind.MarkerTile, 180), new Rect(280, t1, 180, 180));
        Text(c, "InkClass：白砖 ＋ 一支大笔", 280, t1 + 186, 13, BodyBrush, bold: true);
        Text(c, inkClass != null ? "（本机 InkClass 的图标资源，原图）" : "（描摹版）", 280, t1 + 204, 12, NoteBrush);

        // 诊断：为什么显小气
        double dx = 540, dy = t1;
        Text(c, "为什么「小气」：把现在这颗球拆开量", dx, dy, 13, new SolidColorBrush(Accent), bold: true);
        dy += 26;
        string[] diag =
        {
            "① 主角太小：笔只占球径的 36%（17.3 / 48）。256px 上笔长 92 还行；32px 上 22、16px 上只剩 11.5 —— 一根头发。",
            "② 圈太细：红圈线宽 2.5，占球径 5.2%。16px 上折成 0.83px —— 一档缩放就退化成「红点」，形就没了。",
            "③ 三层套娃：球包圈、圈包笔，球边还有一圈很淡的影；三层都在抢话，最后谁也没当上主角。",
            "④ 形状没边界：浅色任务栏里白球直接溶进背景，只剩细红圈和细黑笔；深色里白球又太抢戏。",
            "对照 InkClass：一支笔占满砖面（笔画粗、面积大）＋ 一块白砖（底色整、有边界）—— 它把所有的「形」都用在一件事上，这就是它看着大气的原因。",
        };
        double dw = SheetW - 60 - dx;
        foreach (var s in diag) dy += Paragraph(c, s, dx, dy, dw, 12.5, BodyBrush) + 8;
        y = Math.Max(t1 + 450, dy) + 24;

        // ---- 六个候选：两行三列 ----
        y = Section(c, y, "② 六个候选（浅底 / 深底 / 真实尺寸 / 放大）");
        const double cw = 477, gap = 25, cardH = 520;
        double rowY = y + 8;
        for (int i = 0; i < IcCandidates.Length; i++)
        {
            double cx = 40 + (i % 3) * (cw + gap);
            double cy = rowY + (i / 3) * (cardH + 18);
            IcCard(c, cx, cy, cw, IcCandidates[i].Kind, IcCandidates[i].Tag, IcCandidates[i].Title, IcCandidates[i].Note);
        }
        y = rowY + 2 * (cardH + 18) + 6;

        // ---- 小尺寸真容：全部候选并排 ----
        y = Section(c, y, "③ 小尺寸真容：16px（×6）与 32px（×4）—— 任务栏上比的这一行");
        var ladder = new (AppIconKind Kind, string Label)[]
        {
            (AppIconKind.Current, "现在"),
            (AppIconKind.GlassesWhite, "A 白镜"),
            (AppIconKind.GlassesRed, "B 红镜"),
            (AppIconKind.GlassesTile, "C 白砖"),
            (AppIconKind.MarkerTile, "D 大笔"),
            (AppIconKind.RingPen, "E 红圈"),
            (AppIconKind.RedCheck, "F 大勾"),
        };

        double rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(IcRender(it.Kind, 16), 6);
            c.DrawImage(b, new Rect(rx, y, b.PixelWidth, b.PixelHeight));
            Caption(c, it.Label, rx - 8, y + 100, 112);
            rx += 124;
        }
        double ry2 = y + 132;
        rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(IcRender(it.Kind, 32), 4);
            c.DrawImage(b, new Rect(rx, ry2, b.PixelWidth, b.PixelHeight));
            rx += 124;
        }
        Caption(c, "32px ×4（桌面 48 差不多也在这一档）", 44, ry2 + 134, 860);

        double vx = 1030, vy = y;
        string[] verdicts =
        {
            "16px 上还读得出来：D（一支大笔）和 F（大红勾）最结实，C（白砖眼镜）紧跟着；A/B 的眼镜会糊成一团（A 是白点、B 是红白光），E 和「现在」只剩一个红点。",
            "D 和 F 都缺一点「我们是谁」：D 像 InkClass、F 像批改工具 —— 想要个性，在 A / B / C 里挑。",
            "浅底 / 深底没有绝对好坏：A（白圈黑片）深底更好看，B（红圈白片）浅底更抢眼，C（白砖）两边都稳。",
            "最后拿真机定：把 design/图标候选/ 里的 .ico 换给快捷方式，眼一扫任务栏就知道选哪个。",
        };
        foreach (var s in verdicts) vy += Paragraph(c, s, vx, vy, SheetW - 60 - vx, 12.5, BodyBrush) + 8;

        y = Math.Max(ry2 + 160, vy) + 26;

        // ---- 结论 ----
        var box = new Rect(40, y, SheetW - 80, 196);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "④ 怎么选、怎么落地", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. 要个性：A / B —— 用户点名的「戴眼镜的笔」，把「教学」这件事直接画在笔上了；两版只差一副眼镜的配色，现场可切。",
            "2. 要最稳：D —— 和 InkClass 同一条路线，16px 上唯一不用赌的；接受「像邻居」就选它。",
            "3. 两头都要：C —— 白砖把眼镜的白镜片保住，小尺寸比 A/B 稳，个性也还在。",
            "4. E 是「不想换」的那条退路：还是那颗球，只是圈加粗、笔放大；F 更像批改工具，当口味项留着。",
            "5. 落地：选定一张，把它的矢量画进 --makeicon 的同一条渲染路径（保证和界面里的图标一致），出 16…256 七档 ico；",
            "　 在那之前，design/图标候选/ 里每个候选都有多尺寸 .ico 和 256 的 .png，先给快捷方式换图标就能真机试。",
        };
        double ty = y + 42;
        foreach (var line in concl) { Text(c, line, 60, ty, 13, BodyBrush); ty += 24; }
        return y + 210;
    }

    // =====================================================================
    //  卡片与元件
    // =====================================================================

    static void IcCard(DrawingContext c, double x, double y, double w, AppIconKind kind, string tag, string title, string note)
        => IcCard(c, x, y, w, s => IcRender(kind, s), tag, title, note);

    /// <summary>一张候选卡：标题 + 说明 + 浅底/深底 + 真实尺寸阶梯 + 放大。</summary>
    static void IcCard(DrawingContext c, double x, double y, double w, Func<int, BitmapSource> make,
                       string tag, string title, string note)
    {
        c.DrawRoundedRectangle(new SolidColorBrush(IcWhite),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1),
                               new Rect(x, y, w, 520), 10, 10);
        Text(c, tag + "　" + title, x + 16, y + 12, 16, TitleBrush, bold: true);
        Paragraph(c, note, x + 16, y + 40, w - 32, 12.5, BodyBrush);

        double px = x + 16, py = y + 96, pz = 180;
        c.DrawRoundedRectangle(new SolidColorBrush(IcLightBg), null, new Rect(px, py, pz, pz), 10, 10);
        c.DrawRoundedRectangle(new SolidColorBrush(IcDarkBg), null, new Rect(px + pz + 16, py, pz, pz), 10, 10);
        c.DrawImage(make((int)pz), new Rect(px, py, pz, pz));
        c.DrawImage(make((int)pz), new Rect(px + pz + 16, py, pz, pz));
        Caption(c, "浅底 / 深底（180px）", px, py + pz + 4, pz * 2 + 16);

        IcSizeLadder(c, x + 16, py + pz + 34, make);
        IcZoomRow(c, x + 16, py + pz + 34 + 92, make);
    }

    /// <summary>真实尺寸阶梯：同一个槽 72 宽、底对齐，下面带 px 标注。</summary>
    static double IcSizeLadder(DrawingContext c, double x, double y, Func<int, BitmapSource> make)
    {
        int[] sizes = { 64, 48, 32, 24, 16 };
        foreach (int s in sizes)
        {
            c.DrawImage(make(s), new Rect(x + 36 - s / 2.0, y + (64 - s), s, s));
            Caption(c, s + "px", x, y + 70, 72);
            x += 72;
        }
        return y + 92;
    }

    /// <summary>放大看：16×4 / 32×3 / 48×2，最近邻（不插值，看的就是真实像素块）。</summary>
    static void IcZoomRow(DrawingContext c, double x, double y, Func<int, BitmapSource> make)
    {
        foreach (var (s, k) in new[] { (16, 4), (32, 3), (48, 2) })
        {
            var b = IcZoom(make(s), k);
            c.DrawImage(b, new Rect(x, y, b.PixelWidth, b.PixelHeight));
            Caption(c, s + "px ×" + k, x - 4, y + b.PixelHeight + 2, b.PixelWidth + 8);
            x += b.PixelWidth + 18;
        }
    }

    static void IcChecker(DrawingContext c, double x, double y, double w, double h)
    {
        c.DrawRectangle(new SolidColorBrush(IcWhite), null, new Rect(x, y, w, h));
        var cell = new SolidColorBrush(C(0xEC, 0xEF, 0xF3));
        const double cs = 10;
        for (int r = 0; r * cs < h; r++)
            for (int q = 0; q * cs < w; q++)
                if (((r + q) & 1) == 1)
                    c.DrawRectangle(cell, null, new Rect(x + q * cs, y + r * cs,
                                  Math.Min(cs, w - q * cs), Math.Min(cs, h - r * cs)));
    }

    static BitmapSource IcTryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(Path.GetFullPath(path));
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    // =====================================================================
    //  渲染：64 格设计 → 真实像素 → 最近邻放大
    // =====================================================================

    static BitmapSource IcRender(AppIconKind kind, int size) => IcRender(size, c => IcDraw64(kind, c));

    /// <summary>按 size 像素**真实渲染**一张图标图（64 格设计 → 目标像素）。</summary>
    static BitmapSource IcRender(int size, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var c = dv.RenderOpen())
        {
            double k = size / 64.0;
            c.PushTransform(new TranslateTransform(size / 2.0, size / 2.0));
            c.PushTransform(new ScaleTransform(k, k));
            c.PushTransform(new TranslateTransform(-32, -32));
            draw(c);
            c.Pop(); c.Pop(); c.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    static BitmapSource IcZoom(BitmapSource src, int k)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        int W = w * k, H = h * k;
        var big = new byte[W * H * 4];
        for (int yy = 0; yy < H; yy++)
        {
            int sy = yy / k;
            for (int xx = 0; xx < W; xx++)
            {
                int sx = xx / k;
                int so = (sy * w + sx) * 4, d = (yy * W + xx) * 4;
                big[d] = px[so]; big[d + 1] = px[so + 1];
                big[d + 2] = px[so + 2]; big[d + 3] = px[so + 3];
            }
        }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, big, W * 4);
        bmp.Freeze();
        return bmp;
    }

    // =====================================================================
    //  图标本体（都画在 64×64 的设计格里）
    // =====================================================================

    static void IcDraw64(AppIconKind kind, DrawingContext c)
    {
        switch (kind)
        {
            case AppIconKind.Current:
                // 复刻现在那颗球：球 48、圈 0.76R、线宽 2.5、笔 0.72R（这里放大到 64 格：R = 30）
                c.DrawEllipse(new SolidColorBrush(IcWhite), null, new Point(32, 32), 30, 30);
                c.DrawEllipse(null, new Pen(new SolidColorBrush(IcRed), 2.5 * 30 / 24.0),
                              new Point(32, 32), 30 * 0.76, 30 * 0.76);
                Icon(c, "pen", 32, 32, 30 * 0.72, new SolidColorBrush(IcInk));
                break;

            case AppIconKind.GlassesWhite:
                IcPenWithGlasses(c, 32, 32, -45, IcRed, IcWhite, IcInk);
                break;

            case AppIconKind.GlassesRed:
                IcPenWithGlasses(c, 32, 32, -45, IcRed, IcRed, IcWhite);
                break;

            case AppIconKind.GlassesTile:
                IcTile(c);
                IcPenWithGlasses(c, 32, 32, -45, IcRed, IcInk, IcWhite);
                break;

            case AppIconKind.MarkerTile:
                IcTile(c);
                c.PushTransform(new TranslateTransform(32, 32));
                c.PushTransform(new RotateTransform(-45));
                IcPenLocal(c, 44, 13.5, IcInk, IcRed, chisel: true);
                c.Pop(); c.Pop();
                break;

            case AppIconKind.RingPen:
                c.DrawEllipse(new SolidColorBrush(IcWhite), null, new Point(32, 32), 30, 30);
                c.DrawEllipse(null, new Pen(new SolidColorBrush(IcRed), 4.6),
                              new Point(32, 32), 25.4, 25.4);
                c.PushTransform(new TranslateTransform(32, 32));
                c.PushTransform(new RotateTransform(-45));
                IcPenLocal(c, 36, 10.5, IcInk, IcRed, chisel: false);
                c.Pop(); c.Pop();
                break;

            case AppIconKind.RedCheck:
            {
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(new Point(10, 33), false, false);
                    gc.LineTo(new Point(25, 48), true, false);
                    gc.LineTo(new Point(54, 15), true, false);
                }
                g.Freeze();
                c.DrawGeometry(null, new Pen(new SolidColorBrush(IcRed), 9.5)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                }, g);
                break;
            }
        }
    }

    /// <summary>InkClass 式白砖：圆角方 ＋ 一圈极淡的边（浅底上也要有边界）。</summary>
    static void IcTile(DrawingContext c)
    {
        c.DrawRoundedRectangle(new SolidColorBrush(IcWhite),
                               new Pen(new SolidColorBrush(IcTileEdge), 1),
                               new Rect(1.5, 1.5, 61, 61), 14, 14);
    }

    /// <summary>一支斜 45° 的笔（笔尖朝左下、笔帽朝右上）＋ 一副圆眼镜。</summary>
    static void IcPenWithGlasses(DrawingContext c, double cx, double cy, double angleDeg,
                                 Color nib, Color rim, Color lens)
    {
        c.PushTransform(new TranslateTransform(cx, cy));
        c.PushTransform(new RotateTransform(angleDeg));
        IcPenLocal(c, 48, 12, IcInk, nib, chisel: false);
        // 眼镜画在**笔的局部坐标**里：两片镜片沿笔身排 —— 正面看的样子。
        // 镜圈粗、镜片白：缩到 16px 时它是黑条上的两个白点，比细眼镜活得久。
        // 两片之间留出 3 的镜桥（缩到 32px 以下会先并起来，这是已知代价）。
        IcGlassesLocal(c, -6.5, 8.5, 6.2, 2.8, rim, lens);
        c.Pop();
        c.Pop();
    }

    /// <summary>
    /// 笔（局部坐标：X 轴沿笔身，笔尖在 -X 端）：
    /// 笔尖（写字笔＝三角 / 马克笔＝斜切梯形）＋ 笔身 ＋ 略宽的笔帽 ＋ 笔夹。
    /// </summary>
    static void IcPenLocal(DrawingContext c, double len, double wid, Color body, Color nib, bool chisel)
    {
        double hw = wid / 2, x0 = -len / 2, x1 = len / 2;
        double tipLen = len * 0.20, capLen = len * 0.30, capW = hw * 1.08;

        var nibGeo = new StreamGeometry();
        using (var g = nibGeo.Open())
        {
            if (chisel)
            {
                g.BeginFigure(new Point(x0, -hw * 0.30), true, true);
                g.LineTo(new Point(x0 + tipLen + 2, -hw * 0.80), true, false);
                g.LineTo(new Point(x0 + tipLen + 2, hw * 0.80), true, false);
                g.LineTo(new Point(x0, hw * 0.30), true, false);
            }
            else
            {
                g.BeginFigure(new Point(x0, 0), true, true);
                g.LineTo(new Point(x0 + tipLen, -hw * 0.70), true, false);
                g.LineTo(new Point(x0 + tipLen + 1.5, -hw * 0.70), true, false);
                g.LineTo(new Point(x0 + tipLen + 1.5, hw * 0.70), true, false);
                g.LineTo(new Point(x0 + tipLen, hw * 0.70), true, false);
            }
        }
        nibGeo.Freeze();
        var nb = new SolidColorBrush(nib);
        c.DrawGeometry(nb, new Pen(nb, 2.2) { LineJoin = PenLineJoin.Round }, nibGeo);

        double bodyL = x0 + tipLen * 0.7, bodyR = x1 - capLen * 0.55;
        var brush = new SolidColorBrush(body);
        c.DrawRoundedRectangle(brush, null, new Rect(bodyL, -hw, bodyR - bodyL, wid), hw * 0.5, hw * 0.5);
        c.DrawRoundedRectangle(brush, null, new Rect(x1 - capLen, -capW, capLen, capW * 2), capW * 0.55, capW * 0.55);
        c.DrawRoundedRectangle(brush, null, new Rect(x1 - capLen * 0.78, -capW - 1.0, capLen * 0.34, 3.6), 1.3, 1.3);
    }

    /// <summary>一副圆眼镜（局部坐标）：镜桥、两片镜片、镜圈。镜桥先画，两端会被镜片压住。</summary>
    static void IcGlassesLocal(DrawingContext c, double cx1, double cx2, double r, double rimW, Color rim, Color lens)
    {
        var bridge = new Pen(new SolidColorBrush(rim), rimW)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        c.DrawLine(bridge, new Point(cx1, 0), new Point(cx2, 0));
        foreach (double x in new[] { cx1, cx2 })
        {
            c.DrawEllipse(new SolidColorBrush(lens), null, new Point(x, 0), r, r);
            c.DrawEllipse(null, new Pen(new SolidColorBrush(rim), rimW), new Point(x, 0), r - rimW / 2, r - rimW / 2);
        }
    }

    // =====================================================================
    //  顺带：每个候选出一份多尺寸 .ico 与 256 的 .png（给快捷方式换图标用）
    // =====================================================================

    static void WriteAppIconCandidates(string outDir)
    {
        string dir = Path.Combine(outDir, "图标候选");
        Directory.CreateDirectory(dir);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        foreach (var it in IcFiles)
        {
            var frames = new BitmapSource[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) frames[i] = IcRender(it.Kind, sizes[i]);
            WriteIcIco(Path.Combine(dir, it.File + ".ico"), frames);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(IcRender(it.Kind, 256)));
            using (var fs = File.Create(Path.Combine(dir, it.File + ".png"))) enc.Save(fs);
            Console.WriteLine($"  图标候选：{it.File}.ico（{string.Join("/", sizes)}）＋ .png（256）");
        }
    }

    /// <summary>写一个多尺寸 ICO：每档都是 PNG 帧（Vista 起支持），尺寸 256 用 0 表示。</summary>
    static void WriteIcIco(string path, BitmapSource[] frames)
    {
        var png = new byte[frames.Length][];
        for (int i = 0; i < frames.Length; i++)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frames[i]));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                png[i] = ms.ToArray();
            }
        }

        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)frames.Length);
            int offset = 6 + 16 * frames.Length;
            for (int i = 0; i < frames.Length; i++)
            {
                int s = frames[i].PixelWidth;
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(png[i].Length);
                w.Write(offset);
                offset += png[i].Length;
            }
            foreach (var p in png) w.Write(p);
        }
    }
}
