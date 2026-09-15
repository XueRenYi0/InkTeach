using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UiMock;

/// <summary>
/// 假面板：不接引擎的三带面板，用来在真 PPT 上看观感、试手感。
///
///   dotnet run --project tools/ui-mock -c Release              # 透明悬浮面板，贴到你的 PPT 上看
///   dotnet run --project tools/ui-mock -c Release -- --demo     # 自带一张假 PPT，抢戏测试
///   dotnet run --project tools/ui-mock -c Release -- --shot design/mock   # 出静态图
///
/// 键盘（在控制台窗口里按）：Esc 退出 / D 切深色 / C 收起展开 / R 腰线开合 /
/// G 凹槽开合 / 1-9 选色 / +- 调粗细 / L 换激光笔图标
/// </summary>
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            // 双击运行时，出事不能一闪而过 —— 把话说清楚再等一个按键
            Log.Exception("启动/主循环", ex);
            Console.WriteLine();
            Console.WriteLine("启动失败：" + ex.GetType().Name);
            Console.WriteLine(ex.Message);
            Console.WriteLine();
            Console.WriteLine("按任意键关闭…");
            try { Console.ReadKey(true); } catch { }
        }
    }

    static void Run(string[] args)
    {
        // 控制台里要打中文说明：把代码页设成 UTF-8，免得变成一堆问号
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        Log.Session(string.Join(" ", args));

        // 启动计时：进程入口 → 窗口出现，顺便量一下"构建全部图标几何"要多久
        if (Array.IndexOf(args, "--startup") >= 0)
        {
            StartupProbe();
            return;
        }

        if (Array.IndexOf(args, "--uitest") >= 0)
        {
            Environment.ExitCode = UiTests.Run();
            return;
        }

        // 剩下的几种模式（出图 / 总览图 / 冒烟 / 交互）都在这里
        AfterStartupPlaceholder(args);
    }

    /// <summary>启动分解：建图标几何 ＋ 进程入口到窗口出现（数字见 调研-界面-性能账.md 第九节）。</summary>
    static void StartupProbe()
    {
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            foreach (string name in new[]
                     {
                         "mouse", "pen", "highlighter", "laser", "eraser", "select", "shapes", "capture",
                         "undo", "redo", "more", "settings", "broom", "selectAll", "arrowSync", "arrowClockwise",
                         "power", "darkTheme", "dockRow", "ruler", "mathFormula", "grid", "color", "pin",
                         "msStylusLaser", "msStylusLaserFill",
                     })
            {
                try
                {
                    var geo = Geometry.Parse(DesignSheet.IconPaths.Get(name));
                    geo.Freeze();      // 冻结 = 以后可以跨帧复用，和 D2D 里"建一次几何"是同一件事
                    n++;
                }
                catch { }
            }
            double geometryMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"  构建 {n} 个图标几何：{geometryMs:F1} ms（一次性，之后缓存复用）");

            var app1 = new Application();
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            MockWindow w1 = new MockWindow();
            w1.Loaded += (_, __) => Console.WriteLine($"  进程入口 → 窗口出现：{t0.Elapsed.TotalMilliseconds:F0} ms（这一段才是假面板特有的）");
            var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t1.Tick += (_, __) => { Console.WriteLine($"  合计 {t0.Elapsed.TotalMilliseconds:F0} ms，退出"); app1.Shutdown(); };
            t1.Start();
            app1.Run(w1);
            return;
        }
    }



    static void AfterStartupPlaceholder(string[] args)
    {
        if (args.Length > 0 && args[0] == "--shot")
        {
            Shots(args.Length > 1 ? args[1] : "design/mock");
            return;
        }

        if (args.Length > 0 && args[0] == "--sheet")
        {
            ContactSheet(args.Length > 1 ? args[1] : "design/mock/工具设置条总览.png");
            return;
        }

        // 冒烟：开窗 → 渲染一拍 → 自己关掉。用来验证"窗口这条路"也是通的，不给人添麻烦。
        if (Array.IndexOf(args, "--smoke") >= 0)
        {
            var app0 = new Application();
            MockWindow w0 = Array.IndexOf(args, "--demo") >= 0 ? new DemoWindow() : new MockWindow();
            w0.Loaded += (_, __) => Console.WriteLine($"  窗口已开（{(w0 is DemoWindow ? "自带假 PPT" : "透明悬浮")}）：{w0.ActualWidth:F0} × {w0.ActualHeight:F0}");
            Console.WriteLine(CheckHoverStability()
                ? "  自检：上带的命中区在开/合前后是稳定的（不会再「反复横跳」）"
                : "  自检失败：上带命中区不稳定！");
            Console.WriteLine(CheckConstantHeight()
                ? "  自检：各工具的面板高度一致（切工具不会再「突然下降」）"
                : "  自检失败：有的工具高度不一样！");
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            t.Tick += (_, __) => { Console.WriteLine("  冒烟通过，关闭"); app0.Shutdown(); };
            t.Start();
            app0.Run(w0);
            return;
        }

        bool demo = Array.IndexOf(args, "--demo") >= 0;
        var app = new Application();
        MockWindow win = demo ? new DemoWindow() : new MockWindow();
        win.Panel.Status += s => Console.WriteLine("  · " + s);

        Console.WriteLine(demo
            ? "假面板（自带假 PPT）—— 这个窗口就是\"抢戏测试\"：看工具条会不会把板书压下去。"
            : "假面板已浮在屏幕上。把它拖到你的 PPT 上，试这几件事：");
        Console.WriteLine("  1) 鼠标移到最上面那条色线 → 它会长成「当前工具的设置条」");
        Console.WriteLine("     笔＝12 色片；橡皮＝整笔擦/面积擦；截屏＝直接截/隐藏批注截；选择＝框选/套索；图形＝六种图形；设置＝三个开关");
        Console.WriteLine("  2) 鼠标移到最下面那道凹槽 → 拖块浮出，拖动改大小（笔＝笔宽，橡皮＝橡皮大小，右边有预览）");
        Console.WriteLine("  3) 按 D 切深色主题：看白块/黑块还分不分得出来");
        Console.WriteLine("  4) 按住面板空白处可以拖动它；点最左边那一格收起成球");
        Console.WriteLine("  控制台热键：Esc 退出 · D 深浅 · C 收起/展开 · M 极简/完整 · H 瘦身档 · B 白板开合 · R 上带开合 · G 滑条 · S 图标档位 · T 换工具 · 1-9 选色 · +/- 大小 · L 激光笔图标");
        Console.WriteLine();

        var keys = new Thread(() =>
        {
            while (true)
            {
                ConsoleKeyInfo k;
                try { k = Console.ReadKey(true); }
                catch { return; }
                win.Dispatcher.Invoke(() => HandleKey(win, k));
            }
        }) { IsBackground = true };
        keys.Start();

        app.Run(win);
    }

    /// <summary>
    /// 自检 1：上带命中区锚在"按钮带上沿"，而那个位置在屏幕上是固定的 ——
    /// 所以条子开合时，同一个屏幕点算出来的结果必须一样（否则就会反复横跳）。
    /// </summary>
    static bool CheckHoverStability()
    {
        foreach (bool slim in new[] { false, true })
        {
            var closed = PanelDraw.Compute(new PanelState { E = 1, Rail = 0, Tool = 1, Slim = slim });
            var open = PanelDraw.Compute(new PanelState { E = 1, Rail = 1, Tool = 1, Slim = slim });
            double shift = open.Band - closed.Band;      // 窗口底边固定 → 局部坐标会多出这一段
            for (double y = 0; y < 60; y += 0.5)
            {
                bool hitClosed = y <= closed.Band + 8;
                bool hitOpen = (y + shift) <= open.Band + 8;
                if (hitClosed != hitOpen) return false;
            }
        }
        return true;
    }

    /// <summary>自检 2：每个工具的面板高度必须一样（下带永远占位，没有滑条的画装饰线）。</summary>
    static bool CheckConstantHeight()
    {
        // 注意：开与合本来就该不一样（80/108 或 60/84）；要比的是"同一个开合状态下，各工具是否一样高"
        foreach (bool slim in new[] { false, true })
        foreach (double rail in new[] { 0.0, 1.0 })
        {
            double? h = null;
            for (int tool = 0; tool < PanelDraw.Tools.Length; tool++)
            {
                if (PanelDraw.IsAction(tool)) continue;
                var cur = PanelDraw.Compute(new PanelState { E = 1, Rail = rail, Tool = tool, Slim = slim });
                h ??= cur.H;
                if (Math.Abs(cur.H - h.Value) > 0.01) return false;
            }
        }
        return true;
    }

    static void HandleKey(MockWindow win, ConsoleKeyInfo k)
    {
        var p = win.Panel;
        switch (k.Key)
        {
            case ConsoleKey.Escape: case ConsoleKey.Q: Application.Current.Shutdown(); break;
            case ConsoleKey.D: p.ToggleDark(); break;
            case ConsoleKey.C: case ConsoleKey.Spacebar: p.ToggleExpand(); break;
            case ConsoleKey.R: p.ToggleRail(); break;
            case ConsoleKey.G: p.ToggleGroove(); break;
            case ConsoleKey.L: p.CycleLaser(); break;
            case ConsoleKey.S: p.CycleIconScale(); break;
            case ConsoleKey.H: p.ToggleSlim(); break;
            case ConsoleKey.M: p.ToggleMini(); break;
            case ConsoleKey.B: p.SetTool(PanelDraw.BoardTool); break;
            case ConsoleKey.T: p.SetTool((p.State.Tool + 1) % PanelDraw.Tools.Length); break;
            case ConsoleKey.OemPlus: case ConsoleKey.Add: p.NudgeSlider(0.06); break;
            case ConsoleKey.OemMinus: case ConsoleKey.Subtract: p.NudgeSlider(-0.06); break;
            default:
                if (k.Key >= ConsoleKey.D1 && k.Key <= ConsoleKey.D9) p.SetColor(k.Key - ConsoleKey.D1);
                else if (k.Key >= ConsoleKey.NumPad1 && k.Key <= ConsoleKey.NumPad9) p.SetColor(k.Key - ConsoleKey.NumPad1);
                break;
        }
    }

    // =====================================================================
    //  出静态图（用同一份绘制代码，所以图 = 屏）
    // =====================================================================

    static void Shots(string dir)
    {
        Directory.CreateDirectory(dir);

        (string Name, PanelState St, bool Slide, int W, int H)[] list =
        {
            ("01-收起成球",            St(e: 0),                                    false, 0, 0),
            ("02-平时只有一条色线",    St(),                                        false, 0, 0),
            ("03-笔-12色片",           St(rail: 1, tool: 2, color: 0),              false, 0, 0),
            ("04-橡皮-整笔与面积",     St(rail: 1, tool: 5, eraser: 1, groove: 1),  false, 0, 0),
            ("05-截屏-直接与隐藏批注", St(rail: 1, tool: 7),                        false, 0, 0),
            ("06-选择-框选与套索",     St(rail: 1, tool: 5),                        false, 0, 0),
            ("07-图形-六种",           St(rail: 1, tool: 6, color: 5),              false, 0, 0),
            ("08-设置-三个开关",       St(rail: 1, tool: 10, dark: false),          false, 0, 0),
            ("09-鼠标-直接与穿透",     St(rail: 1, tool: 0),                        false, 0, 0),
            ("10-图标档-40-20",        St(rail: 1, tool: 1, scale: 0),              false, 0, 0),
            ("11-图标档-44-24",        St(rail: 1, tool: 1, scale: 1),              false, 0, 0),
            ("12-图标档-48-28",        St(rail: 1, tool: 1, scale: 2),              false, 0, 0),
            ("13-深色-12色片",         St(rail: 1, tool: 2, color: 10, dark: true), false, 0, 0),
            ("14-贴在假PPT上",         St(tool: 2, color: 0),                       true, 900, 420),
            ("15-深色贴PPT",           St(rail: 1, tool: 2, color: 10, dark: true), true, 900, 420),
            ("16-清空-按住进行中",     St(rail: 1, tool: 5, eraser: 1, hold: 0.55, groove: 1), false, 0, 0),
            ("17-全选-执行闪一下",     St(rail: 1, tool: 6, select: 1, flash: 1),   false, 0, 0),
            ("18-瘦身档-平时",         St(tool: 1, color: 0, slim: true),            false, 0, 0),
            ("19-瘦身档-展开",         St(rail: 1, tool: 1, color: 0, slim: true),   false, 0, 0),
            ("20-瘦身档-橡皮",         St(rail: 1, tool: 4, eraser: 1, groove: 1, slim: true), false, 0, 0),
            ("21-瘦身档-贴PPT",        St(tool: 1, color: 0, slim: true),            true, 900, 420),
            ("22-更多抽屉",            St(tool: 1, color: 0, more: true),            false, 0, 0),
            ("23-极简档-笔",           St(tool: 2, color: 0, mini: true, rail: 1),   false, 0, 0),
            ("24-极简档-橡皮",         St(tool: 5, color: 0, mini: true, rail: 1, eraser: 1, groove: 1), false, 0, 0),
            ("25-极简档-更多",         St(tool: 2, color: 0, mini: true, more: true), false, 0, 0),
            ("26-白板-开着（盖住 PPT）", St(tool: 1, color: 0, board: true, boardColor: 0), true, 900, 420),
            ("27-白板-板色三选",       St(tool: 1, color: 11, board: true, boardColor: 1, rail: 1), true, 900, 420),
        };

        foreach (var it in list)
        {
            var L = PanelDraw.Compute(it.St);
            double pad = it.Slide ? 0 : 40;
            double w = it.Slide ? it.W : L.ContentW + pad * 2;
            double h = it.Slide ? it.H : L.OriginY + L.H + pad * 2;   // 抽屉在面板上方，要一起算进来

            var dv = new DrawingVisual();
            using (var c = dv.RenderOpen())
            {
                if (it.Slide)
                {
                    if (it.St.BoardOn)
                        c.DrawRectangle(new SolidColorBrush(PanelDraw.BoardColors[it.St.BoardColor].Color), null, new Rect(0, 0, w, h));
                    else
                        SlideDraw.Draw(c, new Rect(0, 0, w, h));
                    c.PushTransform(new TranslateTransform((w - L.W) / 2, h - L.H - 20));
                }
                else
                {
                    c.DrawRectangle(new SolidColorBrush(PanelDraw.C(0xF0, 0xF1, 0xF3)), null, new Rect(0, 0, w, h));
                    c.PushTransform(new TranslateTransform(pad, pad));
                }
                PanelDraw.Draw(c, it.St);
                c.Pop();
            }

            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string path = Path.Combine(dir, it.Name + ".png");
            using var fs = File.Create(path);
            enc.Save(fs);
            Console.WriteLine($"已生成 {path}（{(int)w} × {(int)h}）");
        }
    }

    static PanelState St(double e = 1, double rail = 0, double groove = 0, int color = 0,
                         bool dark = false, int tool = 2, int laser = 0, int eraser = 0,
                         bool hideInk = false, int select = 0, int shape = 0, int scale = 0,
                         double hold = 0, double flash = 0, bool slim = false, bool more = false,
                         bool mini = false, bool board = false, int boardColor = 0)
        => new PanelState
        {
            E = e, Rail = rail, Groove = groove, Color = color, Dark = dark, Tool = tool,
            LaserStyle = laser, EraserMode = eraser, CaptureHideInk = hideInk,
            SelectMode = select, ShapeKind = shape, IconScale = scale,
            ClearHold = hold, ActionFlash = flash,
            Slim = slim,
            MoreOpen = more,
            Mini = mini,
            BoardOn = board, BoardColor = boardColor,
        };

    // =====================================================================
    //  总览图：把每个工具的设置条排在一张图上（同一份绘制代码）
    // =====================================================================

    static void ContactSheet(string path)
    {
        var rows = new (string Title, PanelState St)[]
        {
            ("笔：12 色片（满饱和、带间隙、圆角）", St(rail: 1, tool: 2, color: 0)),
            ("荧光笔：同一组色片（画出来是半透明）", St(rail: 1, tool: 3, color: 2)),
            ("激光笔：小 / 中 / 大（下带＝光点大小）", St(rail: 1, tool: 4, laser: 0)),
            ("橡皮擦：整笔擦 / 面积擦（下带＝橡皮大小）", St(rail: 1, tool: 5, eraser: 1, groove: 1)),
            ("选择：矩形框选 / 自由套索", St(rail: 1, tool: 6, select: 1)),
            ("图形：直线 箭头 矩形 椭圆 三角 平行四边形", St(rail: 1, tool: 7, shape: 2)),
            ("截屏：直接截取 / 隐藏批注截取", St(rail: 1, tool: 8, hideInk: true)),
            ("鼠标：直接操作 / 穿透点击", St(rail: 1, tool: 0)),
            ("设置：装饰带 / 贴边隐藏 / 深色主题", St(rail: 1, tool: 11)),
            ("平时：上带退成一条色线（4 像素）", St(tool: 2, color: 0)),
            ("任务栏档：平时 52（原 80）", St(tool: 2, color: 0)),
            ("任务栏档：展开 76（原 108）", St(rail: 1, tool: 2, color: 0)),
            ("深色主题：同一套（黑块已提亮）", St(rail: 1, tool: 2, color: 10, dark: true)),
        };

        const double W = 760, X = 84;
        double y = 34;
        // 先把总高精确算出来（以前这里少算了图标档位那一段，图被截掉过）
        double total = 34 + 84;
        foreach (var r in rows) total += PanelDraw.Compute(r.St).H + 52;
        total += 10 + 30;
        foreach (int sc in new[] { 0, 1, 2, 3 })
            total += PanelDraw.Compute(St(rail: 1, tool: 2, scale: sc, slim: true)).H + 44;
        total += 40;

        var dv = new DrawingVisual();
        using (var c = dv.RenderOpen())
        {
            c.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, total));
            PanelDraw.Text(c, "批注工具条 · 每个工具的「上带」长什么样", X, y, 24, new SolidColorBrush(PanelDraw.C(0x14, 0x16, 0x1A)), true);
            PanelDraw.Text(c, "上带＝当前工具的设置条（平时退成一条色线），下带＝这个工具的滑条。真实尺寸 1:1。",
                           X, y + 36, 13, new SolidColorBrush(PanelDraw.C(0x5A, 0x5E, 0x66)));
            y += 84;

            foreach (var r in rows)
            {
                var L = PanelDraw.Compute(r.St);
                PanelDraw.Text(c, r.Title, X, y, 13.5, new SolidColorBrush(PanelDraw.C(0x2A, 0x2E, 0x36)), true);
                c.PushTransform(new TranslateTransform(X, y + 22));
                PanelDraw.Draw(c, r.St);
                c.Pop();
                y += L.H + 52;
            }

            // 图标档位对照
            y += 10;
            PanelDraw.Text(c, "图标与按钮的三档搭配（按 S 在假面板里切换）", X, y, 16, new SolidColorBrush(PanelDraw.C(0x14, 0x16, 0x1A)), true);
            y += 30;
            foreach (int sc in new[] { 0, 1, 2, 3 })
            {
                var st = St(rail: 1, tool: 2, scale: sc, slim: true);
                var L = PanelDraw.Compute(st);
                var sca = PanelDraw.Scales[sc];
                PanelDraw.Text(c, $"按钮 {sca.Btn:F0} ／ 图标 {sca.Icon:F0}　→　面板总高 {L.H:F0}", X, y, 12.5,
                               new SolidColorBrush(sc == 1 ? PanelDraw.Accent : PanelDraw.C(0x3A, 0x3E, 0x46)), sc == 1);
                c.PushTransform(new TranslateTransform(X, y + 20));
                PanelDraw.Draw(c, st);
                c.Pop();
                y += L.H + 44;
            }
        }

        var bmp = new RenderTargetBitmap((int)W, (int)total, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
        Console.WriteLine($"已生成 {path}（{(int)W} × {(int)total}）");
    }
}

/// <summary>假 PPT 的一页：标题、正文、柱状图、红色手写批注、蓝色圈选。</summary>
internal sealed class SlideElement : FrameworkElement
{
    public PanelState St;
    protected override void OnRender(DrawingContext c)
    {
        var r = new Rect(0, 0, ActualWidth, ActualHeight);
        if (St != null && St.BoardOn)
        {
            // 白板：把下面那张假 PPT 整块盖住 —— 这就是"白板模式"在真环境里的样子
            c.DrawRectangle(new SolidColorBrush(PanelDraw.BoardColors[St.BoardColor].Color), null, r);
            return;
        }
        SlideDraw.Draw(c, r);
    }
}

internal static class SlideDraw
{
    public static void Draw(DrawingContext c, Rect r)
    {
        c.DrawRectangle(new SolidColorBrush(PanelDraw.C(0xFA, 0xFA, 0xFB)), null, r);

        double x = r.X + 60, y = r.Y + 46;
        // 标题
        c.DrawRoundedRectangle(new SolidColorBrush(PanelDraw.C(0x1F, 0x2A, 0x3A)), null, new Rect(x, y, 320, 18), 4, 4);
        // 正文
        for (int i = 0; i < 4; i++)
            c.DrawRoundedRectangle(new SolidColorBrush(PanelDraw.C(0xE0, 0xE4, 0xEA)), null,
                                   new Rect(x, y + 52 + i * 22, 380 - i * 46, 10), 5, 5);
        // 柱状图
        for (int i = 0; i < 5; i++)
            c.DrawRoundedRectangle(new SolidColorBrush(PanelDraw.Pen[i % 2 == 0 ? 5 : 3]), null,
                                   new Rect(r.X + 540 + i * 62, r.Bottom - 90 - (26 + i * 16), 40, 26 + i * 16), 3, 3);
        // 红色手写批注
        Scribble(c, x + 40, y + 150, 300, PanelDraw.C(0xE0, 0x2B, 0x2B));
        Scribble(c, x + 420, y + 210, 340, PanelDraw.C(0xE0, 0x2B, 0x2B));
        // 蓝色圈选
        c.DrawEllipse(null, new Pen(new SolidColorBrush(PanelDraw.C(0x21, 0x73, 0xE6)), 3),
                      new Point(r.X + 700, r.Bottom - 120), 62, 34);
    }

    static void Scribble(DrawingContext c, double x, double y, double w, Color color)
    {
        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(new Point(x, y), false, false);
            gc.BezierTo(new Point(x + w * 0.22, y - 22), new Point(x + w * 0.34, y + 18), new Point(x + w * 0.52, y - 4), true, false);
            gc.BezierTo(new Point(x + w * 0.72, y - 26), new Point(x + w * 0.82, y + 14), new Point(x + w, y - 8), true, false);
        }
        g.Freeze();
        c.DrawGeometry(null, new Pen(new SolidColorBrush(color), 3)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        }, g);
    }
}
