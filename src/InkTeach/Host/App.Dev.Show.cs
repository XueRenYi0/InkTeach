using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// 开发期宿主：持有引擎，外加一整套自动化测试/基准工具。
/// 继承只是为了让这些工具直接读引擎内部状态（产品代码请用组合：new InkEngine()）。
/// </summary>

internal sealed partial class App
{
    private void SelDragShowcase()
    {
        SelShowcase();                       // 先摆好两条笔迹和选中框

        var sel = Doc.Selected.ToArray();
        var sb = EditRegion.Of(sel);
        var c = new Vector2((sb.MinX + sb.MaxX) * 0.5f, (sb.MinY + sb.MaxY) * 0.5f);
        var m = Matrix3x2.CreateScale(1.25f, 1.25f, c) * Matrix3x2.CreateTranslation(120f, -60f);

        // 完全照 UpdateSelDrag 的做法：标脏旧位置 → 实时改变换 → 标脏新位置
        foreach (var s in sel)
        {
            Doc.Dirty.Add(s.PaddedBounds);
            Doc.SetTransformLive(s, s.Transform * m);
            Doc.Dirty.Add(s.PaddedBounds);
        }

        SettleFrames(700);
        Console.WriteLine("已用实时变换挪走，等外部截图（这个模式不会自己退出）");
    }

    /// <summary>
    /// **截图取景框**的离屏出图：摆几笔背景墨 → 把取景框摆在中间 → 拍下来。
    ///
    /// 看的是两件自检读不出来的事：**框和角标的粗细在 2 倍屏上顺不顺眼**，
    /// 以及拖动中那个**尺寸读数胶囊**跟框的距离、字的大小合不合适。
    /// </summary>
    /// <summary>
    /// **导出对话框探针**：真的弹一次系统"另存为"，同时由一个后台线程去点它的
    /// "保存类型"下拉，并把每一步截下来。
    ///
    /// 为什么要专门做它：这里出过两次问题（"选不到 jpg"、"对话框被自己的下拉盖住"），
    /// 而**这些问题只能靠真机 + 看图判断**——系统对话框是另一个窗口类，
    /// 我们的自检（合成输入 + 数像素）碰不到它的内部控件。探针把"找窗口 →
    /// 点下拉 → 截图 → 关掉"这套动作固定下来，改一次就能再验一次。
    ///
    /// 用法：`--dialogprobe [前缀]`，产出 `<前缀>-1-打开时.bmp`、`-2-点下拉后.bmp`。
    /// </summary>
    private void DialogProbe(string prefix)
    {
        BoardOn = true;
        Tool = Tool.Marquee;
        Doc.Clear();
        Doc.ClearHistory();
        // 几条横线铺在屏幕中间：对话框会压在上面，正好看"框和墨谁在上面"
        for (int i = 0; i < 5; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.85f, 0.15f, 0.15f, 1f), Width = 8f * DpiScale,
            };
            float y = _virtualY + _virtualH * 0.35f + i * 90f * DpiScale;
            s.AddPoint(_virtualX + _virtualW * 0.15f, y, 1f, i * 4);
            s.AddPoint(_virtualX + _virtualW * 0.85f, y, 1f, i * 4 + 2);
            Doc.AddStroke(s);
        }
        Doc.SelectOnly(Doc.Strokes.ToArray());
        SettleFrames(700);

        var t = new Thread(() => DialogProbeThread(prefix)) { IsBackground = true };
        t.Start();

        ExportDialogEnabled = true;
        ExportSelection(1);                 // 阻塞在系统对话框里，后台线程在操作它
        Console.WriteLine("dialogprobe：对话框已关闭（走的是取消那条路）");

        // 走了 `--save` 的话，这里验"真的写出来了"（写完就删，不留垃圾）
        if (DialogProbeCheckExport && LastExportPath != null)
        {
            var fi = new FileInfo(LastExportPath);
            Console.WriteLine(fi.Exists && fi.Length > 0
                ? $"dialogprobe：**文件真的写出来了** {LastExportPath}（{fi.Length} 字节，"
                  + $"类型第 {LastExportFilterIndex} 条）——对话框那条链是通的"
                : $"dialogprobe：**没写出来** {LastExportPath}");
            try { fi.Delete(); } catch { }
            Console.WriteLine("dialogprobe：测试文件已删掉");
            DialogProbeCheckExport = false;
        }
        _quit = true;
    }

    private static bool DialogProbeCheckExport;

    private static IntPtr ProbeFindDialog()
    {
        IntPtr found = IntPtr.Zero;
        uint me = (uint)Environment.ProcessId;
        var sb = new System.Text.StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid != me) return true;
            Native.GetClassNameW(h, sb, sb.Capacity);
            if (sb.ToString() != "#32770") return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static string ProbeClassName(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(256);
        Native.GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private void DialogProbeThread(string prefix)
    {
        IntPtr dlg = IntPtr.Zero;
        for (int i = 0; i < 120 && dlg == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            dlg = ProbeFindDialog();
        }
        if (dlg == IntPtr.Zero) { Console.WriteLine("dialogprobe：6 秒内没等到对话框"); return; }

        Native.GetWindowRect(dlg, out var r);
        Console.WriteLine($"dialogprobe：对话框 {dlg} 在 ({r.Left},{r.Top})，{r.Width}×{r.Height}");

        // 等 1.2 秒：让看门线程把它顶到最前、也让对话框把自己的子控件建完
        // （第一次探针在"刚找到"就查子窗口，一个 ComboBox 都没查到）。
        Thread.Sleep(1200);
        Native.GetWindowRect(dlg, out r);
        ShotRegion(prefix + "-0-全屏.bmp", 0, 0, 2880, 1800);
        ShotRegion(prefix + "-1-打开时.bmp", r.Left - 30, r.Top - 30, r.Width + 60, r.Height + 60);

        // 我们覆盖层这会儿在对话框上面还是下面？看两个点上的最上层窗口是谁。
        int midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;
        IntPtr atMid = Native.WindowFromPoint(new Native.POINT { X = midX, Y = midY });
        Console.WriteLine($"dialogprobe：对话框正中那一点最上层 = {atMid}（{ProbeClassName(atMid)}）"
                        + $"；我们的覆盖层 = {_windows[0].Hwnd}（{ProbeClassName(_windows[0].Hwnd)}）");

        // 子控件清单：看对话框里到底有什么（第一次没找到 ComboBox）
        int n = 0;
        Native.EnumChildWindows(dlg, (h, _) =>
        {
            if (n++ < 40)
            {
                Native.GetWindowRect(h, out var cr0);
                Console.WriteLine($"    子窗口 {ProbeClassName(h),-20} ({cr0.Left},{cr0.Top})-({cr0.Right},{cr0.Bottom})");
            }
            return true;
        }, IntPtr.Zero);

        // "保存类型"那个下拉：它和"文件名"是同一类控件（都是 ComboBox），
        // **"保存类型"是下面那个**（文件名在上）。第一版取了第一个 → 点到了文件名框。
        // 所以这里取**位置最靠下的那一个**。
        IntPtr combo = IntPtr.Zero;
        string comboClass = "";
        int bestTop = int.MinValue;
        Native.EnumChildWindows(dlg, (h, _) =>
        {
            string cls = ProbeClassName(h);
            if (cls != "ComboBox" && cls != "ComboBoxEx32") return true;
            Native.GetWindowRect(h, out var cr0);
            if (cr0.Top <= bestTop) return true;
            bestTop = cr0.Top; combo = h; comboClass = cls;
            return true;
        }, IntPtr.Zero);

        if (combo == IntPtr.Zero)
        {
            Console.WriteLine("dialogprobe：没找到文件类型下拉（子窗口里没有 ComboBox）");
        }
        else
        {
            Native.GetWindowRect(combo, out var cr);
            // 子窗口的矩形**大多数时候是屏幕坐标**（对话框移到 (779,330) 之后，
            // 它也跟着报 (1042,1088)）。但对话框还在 (0,0) 时两种读法长得一模一样，
            // 分不清——所以这里按"在不在对话框矩形里"判一下，不在就补上对话框原点。
            int ox = 0, oy = 0;
            bool insideDlg = cr.Left >= r.Left && cr.Top >= r.Top
                          && cr.Right <= r.Right && cr.Bottom <= r.Bottom;
            if (!insideDlg) { ox = r.Left; oy = r.Top; }
            int cx = ox + cr.Right - 14, cy = oy + (cr.Top + cr.Bottom) / 2;
            Console.WriteLine($"dialogprobe：下拉 {comboClass} 读到 ({cr.Left},{cr.Top})-({cr.Right},{cr.Bottom})，"
                            + $"对话框 ({r.Left},{r.Top})-({r.Right},{r.Bottom})，"
                            + $"{(insideDlg ? "按屏幕坐标" : "按相对坐标＋原点")} → 点 ({cx},{cy})");

            // **先看一眼：这个点上是谁的窗口**（我们覆盖层是置顶的，很可能抢走这一下）
            IntPtr over = Native.WindowFromPoint(new Native.POINT { X = cx, Y = cy });
            Console.WriteLine($"dialogprobe：那一点上最上层的窗口 = {over}（{ProbeClassName(over)}）"
                            + $"，我们的覆盖层 = {_windows[0].Hwnd}");

            SendMouse(cx, cy, 0);
            Thread.Sleep(60);
            SendMouse(cx, cy, Native.MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(50);
            SendMouse(cx, cy, Native.MOUSEEVENTF_LEFTUP);
            // **连着拍三张**：判"点开就收回去"这种毛病，得看它是没开、还是开了一下又被关掉。
            int sx = ox + cr.Left - 30, sy = oy + cr.Bottom - 40;
            int sw = Math.Max(cr.Width + 60, 400), sh = 460;
            Thread.Sleep(150);
            ShotRegion(prefix + "-2a-点后150ms.bmp", sx, sy, sw, sh);
            Thread.Sleep(250);
            ShotRegion(prefix + "-2b-点后400ms.bmp", sx, sy, sw, sh);
            Thread.Sleep(500);
            ShotRegion(prefix + "-2c-点后900ms.bmp", sx, sy, sw, sh);

            // 下拉里**第二项就是 JPEG**（PNG 在上、JPEG 在下，每项约 30 物理像素）。
            // 点它一下，看文件名后缀会不会跟着变成 .jpg——这就是用户要的那条路。
            int jx = ox + cr.Left + 200, jy = oy + cr.Bottom + 45;
            SendMouse(jx, jy, 0);
            Thread.Sleep(60);
            SendMouse(jx, jy, Native.MOUSEEVENTF_LEFTDOWN);
            Thread.Sleep(50);
            SendMouse(jx, jy, Native.MOUSEEVENTF_LEFTUP);
            Thread.Sleep(500);
            ShotRegion(prefix + "-3-选了JPG.bmp", sx, sy, sw, sh);

            // `--save`：一路走到底——把文件名改成临时路径，点"保存"，
            // 看我们的代码是不是真的把图写出来了（写完由主线程验证并删掉）。
            if (Environment.GetCommandLineArgs().Contains("--save"))
            {
                DialogProbeCheckExport = true;
                IntPtr saveBtn = IntPtr.Zero;
                int leftMost = int.MaxValue;
                Native.EnumChildWindows(dlg, (h, _) =>
                {
                    string cls = ProbeClassName(h);
                    Native.GetWindowRect(h, out var hr);
                    if (cls == "Button" && hr.Top > r.Top + r.Height * 3 / 4 && hr.Left < leftMost)
                    {
                        leftMost = hr.Left;                                 // 下半部分最左边那个 = 保存
                        saveBtn = h;
                    }
                    return true;
                }, IntPtr.Zero);

                if (saveBtn != IntPtr.Zero)
                {
                    Native.GetWindowRect(saveBtn, out var br);
                    int bx = (br.Left + br.Right) / 2, by = (br.Top + br.Bottom) / 2;
                    Console.WriteLine($"dialogprobe：点「保存」({bx},{by})");
                    SendMouse(bx, by, 0);
                    Thread.Sleep(60);
                    SendMouse(bx, by, Native.MOUSEEVENTF_LEFTDOWN);
                    Thread.Sleep(50);
                    SendMouse(bx, by, Native.MOUSEEVENTF_LEFTUP);
                    return;                       // 让它自己去保存、关框，别再补 WM_CLOSE
                }
            }
        }

        Thread.Sleep(300);
        Native.PostMessage(dlg, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    private static void ShotRegion(string path, int x, int y, int w, int h)
    {
        bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
        Console.WriteLine(ok ? $"  出图 {System.IO.Path.GetFullPath(path)}（CWD = {Environment.CurrentDirectory}）"
                             : $"  出图失败 {path}");
    }

    private void CaptureShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        BoardOn = true;                     // 白底，不然框压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();

        float cx = VirtualScreen.MinX + 700, top = VirtualScreen.MinY + 300;
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 5f * DpiScale,
        };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            s.AddPoint(cx - 300 + t * 620, top + 120 + MathF.Sin(t * 6f) * 60f, 0.5f, i * 8);
        }
        Doc.AddStroke(s);

        // 取景区：**故意取一块不是整数的尺寸**（520×300 逻辑像素），读数才有看头
        CaptureActive = true;
        CapMinX = cx - 260; CapMinY = top;
        CapMaxX = cx + 260; CapMaxY = top + 300 * DpiScale;
        // `--adjust`：出"松开后调整"那一版（8.3.0：8 个手柄 + ✓/✕ 两颗按钮）
        CaptureAdjusting = Environment.GetCommandLineArgs().Contains("--adjust");
        // `--ready`：出"刚进屋"那一版（8.3.1：整屏灰 + 顶部提示 + 右上角「✕ 取消」，还没有框）
        bool ready = Environment.GetCommandLineArgs().Contains("--ready");
        if (ready) CapMinX = CapMaxX = CapMinY = CapMaxY = cx;

        // 出图范围 = 框 + 四周一圈（遮罩/准线/读数/按钮都可能有）：整块视口太大，
        // 给"框 + 120 逻辑像素"就够（看图看的是那几个控件的排版）。
        var r = new RectF { MinX = CapMinX, MinY = CapMinY, MaxX = CapMaxX, MaxY = CapMaxY };
        // ⚠ `--ready` 那两件东西（顶部提示、右上角「✕」）都贴在**屏幕上沿**，
        //   "点 + 120"根本拍不到（2026-10-03 修：以前只出一块 240×240 的灰）。
        //   改成**屏幕上沿一条通栏**——两件都在里面。
        if (ready)
            r = new RectF
            {
                MinX = VirtualScreen.MinX, MinY = VirtualScreen.MinY,
                MaxX = VirtualScreen.MaxX, MaxY = VirtualScreen.MinY + 360f * DpiScale,
            };
        if (!OffscreenFloatingShot(path, ready ? r : r.Inflate(120f * DpiScale))) Console.WriteLine("出图失败");
        CaptureActive = false;
        CaptureAdjusting = false;
        _quit = true;
    }

    /// <summary>
    /// `--demogif &lt;目录&gt;`：录一段**演示连拍**（写字 → 停顿变图形 → 选中拖动 → 截图取景）。
    ///
    /// 为什么要有它：README 头部那张动图没法"离屏摆拍"——它要的正是**真实交互过程**
    /// （合成鼠标走真实输入通路、停顿变形走真实定时器、截图取景走真冻结）。所以这里
    /// 和自检同一套路：白板打底（不透明，画面里没有桌面杂物）+ 合成输入 + 约 110ms 截一帧。
    /// 帧存成 `&lt;目录&gt;\0000.bmp…`，之后用 `tools\make-demo-gif.ps1` 拼成 GIF。
    /// 只在开发机上跑，不进产品、也不进默认自检套件。
    /// </summary>
    private void DemoGif(string dir)
    {
        SetUiFactory(() => new InkUi.FullUi());
        Tool = Tool.Pen;
        BoardOn = true;
        Host.Commands.SetBoard(true);              // 引擎侧真开板：截屏里是干净白底
        SettleFrames(700);

        Directory.CreateDirectory(dir);
        int frame = 0;
        // 截屏范围（物理像素）：动作区 + 底部工具带（1600×1420）。
        int fx = _virtualX + 850, fy = _virtualY + 380, fw = 1600, fh = 1420;
        void Snap() => ScreenProbe.SaveBmp(
            Path.Combine(dir, frame++.ToString("0000") + ".bmp"), fx, fy, fw, fh);

        // 一边按 `t∈[0,1]` 推进动作，一边约 120ms 截一帧；结束再补一张。
        void Animate(int ms, Action<float> at)
        {
            var sw = Stopwatch.StartNew();
            // ⚠ 别写 `long.MinValue`：`el - last` 会溢出成负数，条件永远不成立
            //   （第一版就是这么只拍了 10 张——每个阶段结束一张，中间一张没有）。
            long last = -10000;
            for (;;)
            {
                long el = sw.ElapsedMilliseconds;
                at?.Invoke(ms <= 0 ? 1f : Math.Min(1f, el / (float)ms));
                PumpMessages();
                StepCameraAnim();
                RenderAll();
                if (el - last >= 120) { Snap(); last = el; }
                if (el >= ms) break;
                Thread.Sleep(10);
            }
            Snap();
        }

        // ── ① 用笔画一个"手画圆"（带手抖；3.0 秒走完）────────────────────────
        float cx = _virtualX + 1560, cy = _virtualY + 900, r = 250;
        const int N = 64;
        var pts = new Vector2[N + 1];
        for (int i = 0; i <= N; i++)
        {
            float a = -MathF.PI / 2f + i / (float)N * MathF.PI * 2f;
            float rr = r * (1f + 0.045f * MathF.Sin(i * 1.9f));   // 手抖：别是完美圆
            pts[i] = new Vector2(cx + rr * MathF.Cos(a), cy + rr * MathF.Sin(a));
        }
        SendMouse((int)pts[0].X, (int)pts[0].Y, 0);
        SettleFrames(150); Snap();
        SendMouse((int)pts[0].X, (int)pts[0].Y, Native.MOUSEEVENTF_LEFTDOWN);
        Animate(2800, t => SendMouse((int)pts[Math.Min(N, (int)(t * N))].X,
                                     (int)pts[Math.Min(N, (int)(t * N))].Y, 0));

        // ── ② 停住不动：400ms 后"停顿变图形"（圆）＋ 松手自动选中 ────────────
        Animate(950, _ => SendMouse((int)pts[N].X, (int)pts[N].Y, 0));
        SendMouse((int)pts[N].X, (int)pts[N].Y, Native.MOUSEEVENTF_LEFTUP);
        Animate(900, _ => { });                    // 定型 + 自动选中（收起成一颗圆钮）

        // 点一下那颗圆钮：把操作条摊开（图里才有完整的十格）。
        {
            var dot = SelectionHandles.BarCollapsedRect(
                SelectionHandles.FrameOf(Doc.Selected).CanvasAabb, UiUnit, FurnitureViewport);
            float dx = (dot.MinX + dot.MaxX) * 0.5f, dy = (dot.MinY + dot.MaxY) * 0.5f;
            SendMouse((int)dx, (int)dy, 0);
            SettleFrames(90); Snap();
            SendMouse((int)dx, (int)dy, Native.MOUSEEVENTF_LEFTDOWN);
            SettleFrames(90);
            SendMouse((int)dx, (int)dy, Native.MOUSEEVENTF_LEFTUP);
            Animate(650, _ => { });                // 摊开 + 看一眼
        }

        // ── ③ 按住框内拖动（在框里按下 = 拖整体，一步撤销那种）──────────────
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(140); Snap();
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        Animate(1100, t => SendMouse((int)(cx + t * 330f), (int)(cy + t * 180f), 0));
        SendMouse((int)(cx + 330f), (int)(cy + 180f), Native.MOUSEEVENTF_LEFTUP);
        Animate(700, _ => { });

        // ── ④ 截图取景：整屏压暗 → 拖出取景框 → 进调整态（8 手柄 + ✓/✕）─────
        CaptureHideInk = false;                    // 连批注一起冻：冻出来的底就是我们的板书
        BeginCaptureMode();
        SettleFrames(220); Snap();
        CapMinX = CapMaxX = cx - 240; CapMinY = CapMaxY = cy - 140;
        Animate(1000, t =>
        {
            CapMaxX = cx - 240 + t * 720f;
            CapMaxY = cy - 140 + t * 430f;
        });
        CaptureAdjusting = true;
        Animate(1500, _ => { });

        Console.WriteLine($"[演示连拍] {frame} 帧 → {dir}");
        ExitCode = 0;
        _quit = true;
    }
}
