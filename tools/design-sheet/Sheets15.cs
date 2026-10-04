using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesignSheet;

/// <summary>
/// v15：**线条型的笔** —— 图标第五轮。
///
/// 用户 2026-10-01 第二轮回话：
/// 「不行，太难看了。你看看隔壁 InkClass 的那个笔的图标，我觉得挺好的，
/// 不过那个笔太实心了。我们采用那种 Fluent 风格的线条型图标；
/// 或者你去找一些开源、可商用的笔的图标给我，我们直接来参考一下，找适合当桌面图标的。」
///
/// 所以这一轮分两件事：
///   ① **找**：从七家开源图标库拉 12 支笔（`tools/gen-pen-refs.ps1` 生成 `PenRefs.g.cs`，
///      原图另存 `design/参考-笔图标/`），全部白砖上、真实像素排一遍 —— 让「挑哪一支」这件事
///      先在真实尺寸上发生；
///   ② **做**：照着 Fluent 那支做四版「我们的」（原样 / ＋红笔迹 / 线描马克笔 / 无砖对照）。
///
/// 口径（这一轮只此一条）：**要线条、不要实心**。Fluent 那种「填充式轮廓图标」直接当线条用
/// （它本来就是用轮廓线画出来的），Tabler / Lucide / Iconoir 那种描边型则要用 Pen 描。
/// </summary>
internal static partial class Program
{
    // =====================================================================
    //  参考图：把 PenRefs 里的路径按目标大小画进 64 格
    // =====================================================================

    /// <summary>把一支参考笔的 viewBox 映射到 64 格：返回 (缩放, 平移)。</summary>
    static (double S, double Ox, double Oy) P5Map(PenRef r, double target)
    {
        double bx = 0, by = 0, bw = 24, bh = 24;
        var parts = r.Box.Split(' ');
        if (parts.Length == 4)
        {
            bx = double.Parse(parts[0], CultureInfo.InvariantCulture);
            by = double.Parse(parts[1], CultureInfo.InvariantCulture);
            bw = double.Parse(parts[2], CultureInfo.InvariantCulture);
            bh = double.Parse(parts[3], CultureInfo.InvariantCulture);
        }
        double s = target / Math.Max(bw, bh);
        return (s, 32 - (bx + bw / 2) * s, 32 - (by + bh / 2) * s);
    }

    /// <summary>画一支参考笔（填充型用填、描边型用描 —— 混了就不是那支笔了）。</summary>
    static void P5DrawRef(DrawingContext c, PenRef r, double target, Brush brush)
    {
        var (s, ox, oy) = P5Map(r, target);
        c.PushTransform(new TranslateTransform(ox, oy));
        c.PushTransform(new ScaleTransform(s, s));
        var geo = Geometry.Parse(r.Path);
        if (r.Stroked)
            c.DrawGeometry(null, new Pen(brush, r.StrokeW)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            }, geo);
        else
            c.DrawGeometry(brush, null, geo);
        c.Pop();
        c.Pop();
    }

    static BitmapSource P5RefIcon(PenRef r, int size, bool tile, double target = 40)
        => IcRender(size, c =>
        {
            if (tile) IcTile(c);
            P5DrawRef(c, r, target, new SolidColorBrush(IcInk));
        });

    /// <summary>参考笔的笔尖在 64 格里的位置（取图形包围盒的左下角 —— 斜 45° 的笔，尖就在那儿）。</summary>
    static Point P5TipOf(PenRef r, double target)
    {
        var (s, ox, oy) = P5Map(r, target);
        var b = Geometry.Parse(r.Path).Bounds;
        return new Point(ox + (b.Left + 0.4) * s, oy + (b.Bottom - 0.4) * s);
    }

    // =====================================================================
    //  我们的四版
    // =====================================================================

    enum P5Kind
    {
        FluentPen,           // A Fluent 的 Pen 原样 ＋ 白砖
        FluentPenTrail,      // B 同 A，笔尖多一道带笔锋的红弧（仿参考图那道 swoosh）
        FluentPenShort,      // B2 笔尖拉出一小段直墨迹（v3 的 E 那一支的做法）
        FluentPenUnderline,  // B3 笔下一道红下划线（v3 的 F 那一支的做法）
        LineMarker,          // C 照着 InkClass 那支的形状、改画成线条
        NakedTrail,          // D 同 B，但没有白砖（对照：看看「裸着」上任务栏）
    }

    static readonly PenRef P5Fluent = PenRefs.All[0];   // Fluent · Pen

    static readonly (P5Kind Kind, string Tag, string Title, string Note)[] P5Candidates =
    {
        (P5Kind.FluentPen, "A", "Fluent 笔 · 素",
         "Fluent 自家的 Pen 原样放进白砖，一点红都不加：最干净、最像「官方图标」，也最不像「批注」—— 一支安静的笔。"),

        (P5Kind.FluentPenTrail, "B", "Fluent 笔 ＋ 红笔迹 ★已落地",
         "同一支笔，笔尖下拖出一道带笔锋的红弧。红只落在笔迹这一处，「正在批注」由方向感说出来。"
         + "　★ 2026-10-01 用户选定：「你笔锋弧线这一版做得挺好的，我想使用这个」——" +
           "已写进 src/InkTeach/AppIconUi.cs 并用 --makeicon 出了七档 ico。"),

        (P5Kind.LineMarker, "C", "线描马克笔",
         "照着 InkClass 那支的形状、改画成线条：笔身空心，只有笔尖和笔环是红的。你说「笔挺好的，就是太实心」—— 这一版就是那句话的答案。"),

        (P5Kind.NakedTrail, "D", "无砖 · 对照",
         "把 B 的白砖去掉，看看「裸着」上任务栏：浅色任务栏还立得住，深色任务栏上整支墨笔会消失。放这儿只为对照，不建议选。"),
    };

    static readonly (P5Kind Kind, string File)[] P5Files =
    {
        (P5Kind.FluentPen,      "A-Fluent笔-素"),
        (P5Kind.FluentPenTrail, "B-Fluent笔-红笔迹"),
        (P5Kind.FluentPenShort, "B2-Fluent笔-短墨迹"),
        (P5Kind.FluentPenUnderline, "B3-Fluent笔-红下划线"),
        (P5Kind.LineMarker,     "C-线描马克笔"),
        (P5Kind.NakedTrail,     "D-Fluent笔-红笔迹-无砖"),
    };

    static readonly Dictionary<string, string> P5Notes = new()
    {
        ["fluentPen"]         = "Fluent 自家的钢笔：斜 45°、笔尖朝左下，一支最「正」的线条笔；放到 48px 以上轮廓还是干净的。",
        ["fluentInkStroke"]   = "一条手写出来的墨迹（不是笔）：最「批注」的一笔，但没有「笔」这个主角，当程序图标少一口气。",
        ["fluentEdit"]        = "铅笔：笔尖是斜切的一刀，比 Pen 更「编辑」、少一点「书写」；16px 上两截还分得开。",
        ["msInkPen"]          = "钢笔尖＋笔杆，笔画比 Fluent 硬；16px 上笔杆和笔尖并成一团墨点，只剩个尖。",
        ["msDraw"]            = "笔＋两道手写弧：故事最完整（正在写字），也正因为元素多，缩到 16px 就糊了。",
        ["tablerBallpen"]     = "圆珠笔：2px 描边、圆头圆角，最「线条」的一支；笔尖那颗小圆点在 16px 上会丢，笔杆还在。",
        ["lucidePenLine"]     = "笔＋底下一道横线：批注的经典动作，24px 上最好认的一支；线细，16px 上横线先没。",
        ["lucideHighlighter"] = "荧光笔＋一条画出来的线：最「老师」的一支；斜切笔头缩到 32px 以下就认不出了。",
        ["phPencilLine"]      = "铅笔＋底下一道线，头尾都圆、气质温和；线宽 2 放到 48px 以上会显得有点胖。",
        ["phPenNib"]          = "钢笔尖特写：最「墨」的一支，像一枚徽章；没有笔杆，16px 上是一块小三角。",
        ["remixMarkPen"]      = "马克笔＋底下一道线：和我们的场景最贴（笔正在批注），笔身一笔带过、利落。",
        ["mdiDrawPen"]        = "笔＋两道手写弧，细节最多；放大最好看，16px 上基本只剩一团。",
    };

    static void P5Draw64(P5Kind kind, DrawingContext c)
    {
        switch (kind)
        {
            case P5Kind.FluentPen:
                IcTile(c);
                P5DrawRef(c, P5Fluent, 42, new SolidColorBrush(IcInk));
                break;

            case P5Kind.FluentPenTrail:
                IcTile(c);
                P5DrawRef(c, P5Fluent, 42, new SolidColorBrush(IcInk));
                P5BrushTrail(c, P5TipOf(P5Fluent, 42), IcRed);
                break;

            case P5Kind.FluentPenShort:
                IcTile(c);
                P5DrawRef(c, P5Fluent, 42, new SolidColorBrush(IcInk));
                P5ShortTrail(c, P5TipOf(P5Fluent, 42), IcRed);
                break;

            case P5Kind.FluentPenUnderline:
                IcTile(c);
                P5DrawRef(c, P5Fluent, 42, new SolidColorBrush(IcInk));
                P5Underline(c, P5TipOf(P5Fluent, 42), IcRed);
                break;

            case P5Kind.LineMarker:
                IcTile(c);
                P5LineMarker(c);
                break;

            case P5Kind.NakedTrail:
                P5DrawRef(c, P5Fluent, 42, new SolidColorBrush(IcInk));
                P5BrushTrail(c, P5TipOf(P5Fluent, 42), IcRed);
                break;
        }
    }

    /// <summary>
    /// 带笔锋的红弧（仿参考图里那道 swoosh）：脊线是两段贝塞尔，
    /// 宽度走「细—粗—细」（起笔 0.7、最粗 3.4、收笔 0.7），两端圆头 ——
    /// 所以它是一条真正的"笔迹"，不是一根一样粗的水管。
    /// 起笔就落在笔尖上：墨是从尖上流出来的，不是旁边贴上去的。
    /// </summary>
    static void P5BrushTrail(DrawingContext c, Point start, Color color)
    {
        const int n = 48;
        var spine = new Point[n + 1];
        var half = new double[n + 1];

        var c1 = new Point(start.X + 0.8, start.Y + 3.2);
        var c2 = new Point(start.X + 5.0, start.Y + 5.0);
        var m = new Point(start.X + 13.0, start.Y + 4.6);
        var c3 = new Point(start.X + 24.0, start.Y + 4.0);
        var c4 = new Point(start.X + 33.0, start.Y - 0.5);
        var e = new Point(start.X + 41.0, start.Y - 7.5);

        for (int i = 0; i <= n; i++)
        {
            double t = i / (double)n;
            spine[i] = t < 0.5 ? Bez(start, c1, c2, m, t * 2) : Bez(m, c3, c4, e, (t - 0.5) * 2);
            half[i] = 0.8 + 2.8 * Math.Sin(Math.PI * Math.Pow(t, 0.7));
        }

        P5Brush(c, spine, half, color);
    }

    /// <summary>v3 的 E 那一支的做法：从笔尖顺着笔的方向拉一小段直墨迹（略带一点手写的弯）。</summary>
    static void P5ShortTrail(DrawingContext c, Point start, Color color)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(start, false, false);
            s.BezierTo(new Point(start.X - 3.4, start.Y + 3.9), new Point(start.X - 6.2, start.Y + 5.6),
                       new Point(start.X - 9.2, start.Y + 6.2), true, false);
        }
        g.Freeze();

        c.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2.8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, g);
    }

    /// <summary>v3 的 F 那一支的做法：笔下一道红下划线（略斜、圆头），说的是「划重点」。</summary>
    static void P5Underline(DrawingContext c, Point tip, Color color)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(tip.X - 4.5, tip.Y + 5.0), false, false);
            s.BezierTo(new Point(tip.X + 8.0, tip.Y + 6.4), new Point(tip.X + 22.0, tip.Y + 3.2),
                       new Point(tip.X + 33.5, tip.Y - 3.0), true, false);
        }
        g.Freeze();

        c.DrawGeometry(null, new Pen(new SolidColorBrush(color), 4.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, g);
    }

    /// <summary>把一条"脊线 ＋ 每点半宽"铺成一个填充图形（左右两条边 ＋ 两端圆头）。</summary>
    static void P5Brush(DrawingContext c, Point[] spine, double[] half, Color color)
    {
        int n = spine.Length;
        var left = new Point[n];
        var right = new Point[n];
        for (int i = 0; i < n; i++)
        {
            Point a = spine[Math.Max(0, i - 1)], b = spine[Math.Min(n - 1, i + 1)];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Max(0.0001, Math.Sqrt(dx * dx + dy * dy));
            double nx = -dy / len, ny = dx / len;
            left[i] = new Point(spine[i].X + nx * half[i], spine[i].Y + ny * half[i]);
            right[i] = new Point(spine[i].X - nx * half[i], spine[i].Y - ny * half[i]);
        }

        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(left[0], true, true);
            for (int i = 1; i < n; i++) s.LineTo(left[i], true, false);
            for (int i = n - 1; i >= 0; i--) s.LineTo(right[i], true, false);
        }
        g.Freeze();

        var brush = new SolidColorBrush(color);
        c.DrawGeometry(brush, null, g);
        c.DrawEllipse(brush, null, spine[0], half[0], half[0]);
        c.DrawEllipse(brush, null, spine[n - 1], half[n - 1], half[n - 1]);
    }

    static Point Bez(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, cc = 3 * u * t * t, d = t * t * t;
        return new Point(a * p0.X + b * p1.X + cc * p2.X + d * p3.X,
                         a * p0.Y + b * p1.Y + cc * p2.Y + d * p3.Y);
    }

    /// <summary>
    /// 线描马克笔：整支笔只画一圈轮廓（空心），只有笔尖和笔环是红的 ——
    /// 就是 InkClass 那支的形状（切角笔尖、锥形过渡、略宽的圆头笔帽），改成线条。
    /// 局部坐标同 IkMarker：笔尖在 -X 端；绕 (32,32) 转 -45°。
    /// </summary>
    static void P5LineMarker(DrawingContext c)
    {
        PnRot(c, 32, 32, -45, dc =>
        {
            const double x0 = -19.5, xTip = -12.5, xBody = 11.6, xCap = 12.0, hw = 5.0, hc = 5.7;

            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(x0, -1.8), false, false);
                s.LineTo(new Point(xTip, -hw), true, false);
                s.LineTo(new Point(xBody, -hw), true, false);
                s.LineTo(new Point(xBody, -hc), true, false);
                s.LineTo(new Point(xCap, -hc), true, false);
                s.ArcTo(new Point(xCap, hc), new Size(hc, hc), 0, false, SweepDirection.Clockwise, true, false);
                s.LineTo(new Point(xBody, hc), true, false);
                s.LineTo(new Point(xBody, hw), true, false);
                s.LineTo(new Point(xTip, hw), true, false);
                s.LineTo(new Point(x0, 1.8), true, false);
            }
            g.Freeze();
            c.DrawGeometry(null, new Pen(new SolidColorBrush(IcInk), 2.4)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            }, g);

            // （不加笔夹：空心笔身里那点小结构一多就成了杂音 —— 笔帽那道「台阶」就已经够说"这是一支马克笔"了）

            // 切角笔尖（红、实心）：把轮廓在尖上那一小段接过去，也定了「这是支马克笔」
            var tip = new StreamGeometry();
            using (var s = tip.Open())
            {
                s.BeginFigure(new Point(x0, -1.8), true, true);
                s.LineTo(new Point(xTip + 1.2, -4.2), true, false);
                s.LineTo(new Point(xTip + 1.2, 4.2), true, false);
                s.LineTo(new Point(x0, 1.8), true, false);
            }
            tip.Freeze();
            c.DrawGeometry(new SolidColorBrush(IcRed), null, tip);

            // 笔环（红）：横过笔身的一道，比笔身略窄一点，读起来才像「环」
            c.DrawLine(new Pen(new SolidColorBrush(IcRed), 2.2)
                       { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                       new Point(xBody - 1.6, -hc + 0.4), new Point(xBody - 1.6, hc - 0.4));
        });
    }

    static BitmapSource P5Render(P5Kind kind, int size) => IcRender(size, c => P5Draw64(kind, c));

    static void WriteLinePenCandidates(string outDir)
    {
        string dir = Path.Combine(outDir, "图标候选v5");
        Directory.CreateDirectory(dir);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        foreach (var it in P5Files)
        {
            var frames = new BitmapSource[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) frames[i] = P5Render(it.Kind, sizes[i]);
            WriteIcIco(Path.Combine(dir, it.File + ".ico"), frames);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(P5Render(it.Kind, 256)));
            using (var fs = File.Create(Path.Combine(dir, it.File + ".png"))) enc.Save(fs);
            Console.WriteLine($"  图标候选v5：{it.File}.ico（{string.Join("/", sizes)}）＋ .png（256）");
        }
    }

    // =====================================================================
    //  出一张稿
    // =====================================================================

    static double DrawPenLibSheet(DrawingContext c)
    {
        c.DrawRectangle(Brushes.White, null, new Rect(0, 0, SheetW, 3600));

        double y = 36;
        Text(c, "程序图标 · 设计稿 v5（线条型的笔 · 七家开源库 12 支）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "用户 2026-10-01：「上一轮不行，太难看了。InkClass 那个笔的图标我觉得挺好的，不过那个笔太实心了 —— " +
                "我们采用那种 Fluent 风格的线条型图标；或者你去找一些开源、可商用的笔的图标给我，我们直接来参考一下。」",
             40, y + 44, 14, BodyBrush);
        Text(c, "所以这一轮只做一件事：线条型的笔。12 支参考笔全部来自 MIT / ISC / Apache-2.0 的库（可商用），" +
                "原图在 design/参考-笔图标/；每支都放在我们的白砖上、按真实像素排给你看。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        // ---- ① 三张对照 ----
        y = Section(c, y, "① 先看三张：实心 vs 线条");
        double ty = y + 6;
        c.DrawImage(IcRender(AppIconKind.MarkerTile, 160), new Rect(40, ty, 160, 160));
        Caption(c, "我们已落地那支（实心）", 30, ty + 164, 180);
        var ink = IcTryLoad(Path.Combine("design", "参考-InkClass图标-原版.jpg"));
        if (ink != null) c.DrawImage(ink, new Rect(240, ty, 160, 160));
        else c.DrawImage(IcRender(AppIconKind.MarkerTile, 160), new Rect(240, ty, 160, 160));
        Caption(c, "InkClass（实心）—— 你说的「挺好的，就是太实心」", 220, ty + 164, 200);
        c.DrawImage(P5RefIcon(P5Fluent, 160, tile: true), new Rect(440, ty, 160, 160));
        Caption(c, "Fluent 的 Pen（线条）", 430, ty + 164, 180);

        double dx = 660, dy = ty - 4;
        Text(c, "这一轮的口径", dx, dy, 13, new SolidColorBrush(Accent), bold: true);
        dy += 26;
        foreach (string s in new[]
        {
            "· 只要线条：笔身是空心的（轮廓线），不是一整块墨 —— 小尺寸会吃点亏，这是线条的代价，认了。",
            "· 只挑开源、可商用：这一页 12 支分别来自 Fluent(MIT)、Material Symbols(Apache-2.0)、Tabler(MIT)、",
            "　 Lucide(ISC)、Phosphor(MIT)、Remix Icon(Apache-2.0)、Material Design Icons(Apache-2.0)。",
            "· 全部放在我们的白砖上看（96px），再按真实像素（32 / 16）与放大排 —— 任务栏认不认，以后两行为准。",
        }) dy += Paragraph(c, s, dx, dy, SheetW - 60 - dx, 12.5, BodyBrush) + 6;
        y = Math.Max(ty + 200, dy) + 26;

        // ---- ② 12 支参考笔 ----
        y = Section(c, y, "② 十二支参考笔（白砖 96 / 真实 32 · 16 / 放大）");
        const double cellW = 358, cellH = 316, gapX = 14, gapY = 14;
        for (int i = 0; i < PenRefs.All.Length; i++)
        {
            double cx = 40 + (i % 4) * (cellW + gapX);
            double cy = y + (i / 4) * (cellH + gapY);
            P5RefCell(c, cx, cy, PenRefs.All[i]);
        }
        y += 3 * (cellH + gapY) + 10;

        // ---- ③ 我们照着做的四版 ----
        y = Section(c, y, "③ 我们照着做的四版（浅底 / 深底 / 真实尺寸 / 放大）");
        const double cw = 477, gap = 25, cardH = 520;
        double rowY = y + 8;
        for (int i = 0; i < P5Candidates.Length; i++)
        {
            double cx = 40 + (i % 2) * (cw + gap);
            double cy = rowY + (i / 2) * (cardH + 18);
            var cand = P5Candidates[i];
            IcCard(c, cx, cy, cw, s => P5Render(cand.Kind, s), cand.Tag, cand.Title, cand.Note);
        }
        y = rowY + 2 * (cardH + 18) + 6;

        y = Section(c, y, "④ 小尺寸真容：16px（×6）与 32px（×4）");
        var ladder = new (P5Kind? Kind, string Label)[]
        {
            (P5Kind.FluentPen, "A Fluent·素"),
            (P5Kind.FluentPenTrail, "B ＋红笔迹"),
            (P5Kind.LineMarker, "C 线描马克"),
            (P5Kind.NakedTrail, "D 无砖"),
            (null, "现状 马克笔"),
        };

        double rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is P5Kind k ? P5Render(k, 16) : IcRender(AppIconKind.MarkerTile, 16), 6);
            c.DrawImage(b, new Rect(rx, y, b.PixelWidth, b.PixelHeight));
            Caption(c, it.Label, rx - 8, y + 100, 112);
            rx += 156;
        }
        double ry2 = y + 132;
        rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is P5Kind k2 ? P5Render(k2, 32) : IcRender(AppIconKind.MarkerTile, 32), 4);
            c.DrawImage(b, new Rect(rx, ry2, b.PixelWidth, b.PixelHeight));
            rx += 156;
        }
        Caption(c, "32px ×4（桌面 48 差不多也在这一档）", 44, ry2 + 134, 860);

        double vx = 900, vy = y;
        string[] verdicts =
        {
            "16px 上线条型普遍吃亏：A / B 的笔身退成一条斜线 ＋ 一个红点，C 只剩一个红笔尖，D 只剩那道红。",
            "32px 是分水岭：A / B / C 都还认得出是一支笔；12 支参考里能撑到 32px 的是 Fluent Pen、Tabler ballpen、Remix mark-pen。",
            "线条型的优势在 48px 以上：笔尖、笔环、笔帽这些细节全在，比实心那支透气，也更配我们界面里那排 Fluent 图标。",
            "红只留一处（笔迹或笔尖）：线条细，红色一多就抢戏 —— 这是 A 与 B 的唯一差别，也是 B 更好用的原因。",
        };
        foreach (var s in verdicts) vy += Paragraph(c, s, vx, vy, SheetW - 60 - vx, 12.5, BodyBrush) + 8;

        y = Math.Max(ry2 + 160, vy) + 26;

        var box = new Rect(40, y, SheetW - 80, 172);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "⑤ 怎么选、怎么落地", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. 我的排序：B（Fluent 笔 ＋ 红笔迹）＞ A（Fluent 笔）＞ C（线描马克笔）。B 既有线条的干净，又留着「批注」这一口气。",
            "2. 想要更「我们」的形状：C —— InkClass 那支马克笔、改成空心线描；代价是 16px 上只剩一个红尖，得赌。",
            "3. 你从 12 支里点一支也行：点哪支，我就用它做 B 那一版（笔 ＋ 红笔迹 ＋ 白砖），许可证已写在稿上。",
            "4. 落地：这几家都是允许商用的开源库，进代码时在注释里带上库名与许可证；形状写进 src/InkTeach/AppIconUi.cs，再跑 --makeicon 出七档 ico。",
            "5. 先试：design/图标候选v5/ 里 A–D 各有多尺寸 .ico 与 256 的 .png，给快捷方式换图标就能在真任务栏上比。",
        };
        double ly = y + 42;
        foreach (var line in concl) { Text(c, line, 60, ly, 13, BodyBrush); ly += 24; }
        return y + 186;
    }

    /// <summary>一格参考笔：名字 / 库与许可证 / 白砖 96 / 真实 32 / 真实 16 与放大 / 一句话。</summary>
    static void P5RefCell(DrawingContext c, double x, double y, PenRef r)
    {
        const double w = 358;
        c.DrawRoundedRectangle(new SolidColorBrush(IcWhite),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1),
                               new Rect(x, y, w, 316), 10, 10);

        Text(c, r.Name, x + 14, y + 10, 15, TitleBrush, bold: true);
        Text(c, r.Lib + " · " + r.License + (r.Stroked ? " · 描边型" : " · 填充型"), x + 14, y + 30, 11, NoteBrush);

        c.DrawImage(P5RefIcon(r, 96, tile: true), new Rect(x + 14, y + 50, 96, 96));
        Caption(c, "白砖 96", x + 14, y + 148, 96);

        c.DrawImage(P5RefIcon(r, 32, tile: true), new Rect(x + 126, y + 50, 32, 32));
        c.DrawImage(IcZoom(P5RefIcon(r, 32, tile: true), 3), new Rect(x + 170, y + 50, 96, 96));
        Caption(c, "32 真实 / ×3", x + 126, y + 148, 140);

        c.DrawImage(P5RefIcon(r, 16, tile: true), new Rect(x + 126, y + 196, 16, 16));
        c.DrawImage(IcZoom(P5RefIcon(r, 16, tile: true), 4), new Rect(x + 170, y + 172, 64, 64));
        Caption(c, "16 真实 / ×4", x + 126, y + 240, 140);

        string note = P5Notes.TryGetValue(r.Key, out var s) ? s : "";
        Paragraph(c, note, x + 14, y + 262, w - 28, 11.5, BodyBrush);
    }

    // =====================================================================
    //  v5b：只比笔迹（B 方案的轨迹）
    // =====================================================================

    static readonly (P5Kind Kind, string Tag, string Title, string Note)[] P5TrailVariants =
    {
        (P5Kind.FluentPen, "①", "不画笔迹（就是 A）",
         "最安静：一支笔、一块白砖，什么故事也不讲。缺点是任务栏上和一堆「编辑类」图标撞脸。"),

        (P5Kind.FluentPenTrail, "②", "笔锋弧线 ★已选定",
         "照着参考图那道 swoosh 重画：脊线走两段弧、宽度「细—粗—细」（起笔 0.8 → 最粗 3.6 → 收笔 0.8），" +
         "起笔就落在笔尖上 —— 墨是从尖上流出来的。有动势，像刚写完一划。"),

        (P5Kind.FluentPenShort, "③", "短墨迹（第三轮 E 那一支）",
         "从笔尖顺着笔的方向拉出一小段直墨迹：最克制的一版，「正在写」这件事只露一个头，不抢笔的戏。"),

        (P5Kind.FluentPenUnderline, "④", "红下划线（第三轮 F 那一支）",
         "笔下一道红：说的是「划重点 / 批注过」，最像老师那支红笔；代价是它更像「动作」，不像「正在写」。"),
    };

    static double DrawTrailSheet(DrawingContext c)
    {
        c.DrawRectangle(Brushes.White, null, new Rect(0, 0, SheetW, 3200));

        double y = 36;
        Text(c, "程序图标 · 设计稿 v5b（笔迹四选）", 40, y, 30, TitleBrush, bold: true);
        Text(c, "用户 2026-10-01：「你照着做的那一版，就是 B 方案那个红色笔的轨迹，我看着好像有点奇怪呀。你参阅一下之前人家比的轨迹。」",
             40, y + 44, 14, BodyBrush);
        Text(c, "所以这一页只比笔迹：笔都是 Fluent 的 Pen ＋ 我们的白砖，只换笔尖那一笔 —— " +
                "② 是照参考图重画的（带笔锋），③ ④ 是第三轮比过的那两种做法，① 是干脆不画。",
             40, y + 66, 14, NoteBrush);
        y += 104;

        y = Section(c, y, "① 四种笔迹（浅底 / 深底 / 真实尺寸 / 放大）");
        const double cw = 477, gap = 25, cardH = 520;
        double rowY = y + 8;
        for (int i = 0; i < P5TrailVariants.Length; i++)
        {
            double cx = 40 + (i % 2) * (cw + gap);
            double cy = rowY + (i / 2) * (cardH + 18);
            var v = P5TrailVariants[i];
            IcCard(c, cx, cy, cw, s => P5Render(v.Kind, s), v.Tag, v.Title, v.Note);
        }
        y = rowY + 2 * (cardH + 18) + 6;

        y = Section(c, y, "② 小尺寸真容：16px（×6）与 32px（×4）");
        var ladder = new (P5Kind? Kind, string Label)[]
        {
            (P5Kind.FluentPen, "① 不画"),
            (P5Kind.FluentPenTrail, "② 笔锋弧"),
            (P5Kind.FluentPenShort, "③ 短墨迹"),
            (P5Kind.FluentPenUnderline, "④ 红下划"),
            (null, "现状 马克笔"),
        };

        double rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is P5Kind k ? P5Render(k, 16) : IcRender(AppIconKind.MarkerTile, 16), 6);
            c.DrawImage(b, new Rect(rx, y, b.PixelWidth, b.PixelHeight));
            Caption(c, it.Label, rx - 8, y + 100, 112);
            rx += 156;
        }
        rx = 44;
        foreach (var it in ladder)
        {
            var b = IcZoom(it.Kind is P5Kind k2 ? P5Render(k2, 32) : IcRender(AppIconKind.MarkerTile, 32), 4);
            c.DrawImage(b, new Rect(rx, y + 132, b.PixelWidth, b.PixelHeight));
            rx += 156;
        }
        Caption(c, "32px ×4（桌面 48 差不多也在这一档）", 44, y + 132 + 134, 860);

        double vx = 900, vy = y;
        string[] verdicts =
        {
            "① ② 在 16px 上几乎一样（都只剩一条斜线）：笔迹是 32px 以上才讲得出故事的东西。",
            "② 的笔锋在 32px 上还剩一点粗细变化（起细、中粗、收细），这是它比原来那根「水管」耐看的地方。",
            "③ 在 16px 上最干净：一条斜线 ＋ 尖上一点红，谁也不糊；④ 在 16px 上是一条红杠，最容易被看成「编辑」类图标。",
            "「谁在写」还是「写完过」：② ③ 说的是正在写，④ 说的是刚批改完 —— 这一条只有你能拍。",
        };
        foreach (var s in verdicts) vy += Paragraph(c, s, vx, vy, SheetW - 60 - vx, 12.5, BodyBrush) + 8;

        y = Math.Max(y + 132 + 160, vy) + 26;

        var box = new Rect(40, y, SheetW - 80, 148);
        c.DrawRoundedRectangle(new SolidColorBrush(C(0xF6, 0xF7, 0xF9)),
                               new Pen(new SolidColorBrush(C(0xE2, 0xE5, 0xEA)), 1), box, 10, 10);
        Text(c, "③ 怎么选", 60, y + 14, 15, TitleBrush, bold: true);
        string[] concl =
        {
            "1. 我的排序：②（笔锋弧线）＞ ③（短墨迹）＞ ①（不画）＞ ④（红下划线）。② 是唯一一个「像笔迹」的：有起笔、有收笔。",
            "2. 想要更安静：③ —— 就是第三轮 E 那一支的做法，把「正在写」只露一个头。",
            "3. ④ 更像「批改 / 划重点」这个动作，任务栏上也最跳；要不要它，看你更想秀「正在写」还是「改过了」。",
            "4. ★ 2026-10-01 定：②（笔锋弧线）—— 同一天写进 src/InkTeach/AppIconUi.cs，--makeicon 出了七档 ico。",
        };
        double ly = y + 42;
        foreach (var line in concl) { Text(c, line, 60, ly, 13, BodyBrush); ly += 24; }
        return y + 162;
    }
}
