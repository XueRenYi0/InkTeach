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

internal sealed partial class App
{
    // ------------------------------------------------------------------
    //  Click target (second process) + real pass-through test
    // ------------------------------------------------------------------

    /// <summary>Runs a plain window in its own process that records every time
    /// it is clicked. Used as the window underneath the overlay.</summary>
    private void RunClickTarget(string logFile)
    {
        _clickLogFile = logFile;
        _clickTargetHwnd = Native.CreateWindowEx(
            0, _className, "InkTeach 点击目标",
            0x00CF0000L /*WS_OVERLAPPEDWINDOW*/, 120, 120, 420, 320,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_clickTargetHwnd == IntPtr.Zero)
        {
            File.WriteAllText(logFile, "error\n");
            return;
        }
        Native.ShowWindow(_clickTargetHwnd, 5 /*SW_SHOW*/);
        File.WriteAllText(logFile, "ready\n");

        while (!_quit)
        {
            if (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            else break;
        }
    }

    private static int CountClicks(string file)
    {
        try
        {
            if (!File.Exists(file)) return 0;
            var text = ReadTextShared(file);
            int n = 0, i = 0;
            while ((i = text.IndexOf("clicked", i, StringComparison.Ordinal)) >= 0) { n++; i += 7; }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 读一个"另一个进程正在写"的小文件，**带重试**。
    ///
    /// 为什么需要：点击目标进程每隔一会儿就往这个文件里追加一行，父进程同时在读；
    /// 撞上共享冲突时 `File.ReadAllText` 直接抛 IOException，整条用例会以"异常"收场
    /// 而不是判据失败——套件里就报成 FATAL，看起来像功能坏了。
    /// 这类"测试自己的时序问题"必须和被测的东西分开，不然会浪费很多时间去查一个不存在的 bug。
    /// </summary>
    private static string ReadTextShared(string path, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (Exception) { return ""; }   // 到点了还读不到就当空的，别把用例打成异常
        }
    }
    private void CaptureIconShow(string path) => SheetShow(new CaptureIconSheet(), path);

    /// <summary>出图：**激光笔图标候选**（开发期比图用，见 <see cref="LaserIconSheet"/>）。</summary>
    private void IconShow(string path) => SheetShow(new LaserIconSheet(), path);

    /// <summary>出图：图形面板那些自绘图标的对照表（见 <see cref="ShapeIconSheet"/>）。</summary>
    private void ShapeIconShow(string path) => SheetShow(new ShapeIconSheet(), path);

    /// <summary>出图：**白板 / 激光笔的图标候选**（2026-09-26，见 <see cref="ToolIconSheet"/>）。</summary>
    private void ToolIconShow(string path) => SheetShow(new ToolIconSheet(), path);

    /// <summary>
    /// **把一张"图标对照表"的图截下来**——`--iconshow` / `--captureicons` / `--shapeiconshow`
    /// 三处共用这一份（原来只有前面两个、各写了一遍，加第三个时收过来的）。
    /// </summary>
    private void SheetShow(IOverlayUi sheet, string path)
    {
        SetUi(sheet);
        BoardOn = true;                      // 白板打底：图里没有桌面上的杂东西
        SettleFrames(400);

        // 优先走**离屏出图**：锁屏 / 远程 / 没显示器时照样出得来，图里也不会混进桌面。
        if (OffscreenShot(path)) return;

        var b = CurrentUi.QueryBounds();
        int x = (int)MathF.Floor(b.MinX * DpiScale) - 20;
        int y = (int)MathF.Floor(b.MinY * DpiScale) - 20;
        int w = (int)MathF.Ceiling((b.MaxX - b.MinX) * DpiScale) + 40;
        int h = (int)MathF.Ceiling((b.MaxY - b.MinY) * DpiScale) + 40;
        bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
        Console.WriteLine(ok ? $"已出图 {path}" : "出图失败");
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }

    /// <summary>
    /// 真实面板的每帧代价：**同一份内容**，挂面板与不挂面板各渲染一遍，比时间。
    ///
    /// 为什么要有这个：设计阶段那句"面板每帧 0.5～1.5 毫秒"是**估**的
    /// （拿 160×60 的小界面按面积外推的），而面板现在有 636×82、
    /// 还在写字这条最敏感的路径上。教室机器比开发机慢，估的数不能当结论。
    ///
    /// 量三档：不挂界面（基准）／收起成球／展开成带子（含设置条，最贵的一档）。
    /// 每档都强制 `_dirty = true` 再渲染，避免"空闲不出帧"把代价量成 0。
    /// </summary>
    private void PanelPerf()
    {
        Console.WriteLine();
        Console.WriteLine("=== 真实面板的每帧代价（同一份内容，挂 vs 不挂）===");

        // 一屏板书：200 条各 40 个点
        Doc.Clear();
        Doc.ClearHistory();
        var rnd = new Random(7);
        for (int s = 0; s < 200; s++)
        {
            var st = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0.1f, 0.1f, 0.1f, 1f),
                Width = 6f * DpiScale,
            };
            float x0 = _virtualX + rnd.Next(100, Math.Max(200, _virtualW - 400));
            float y0 = _virtualY + rnd.Next(100, Math.Max(200, _virtualH - 300));
            for (int k = 0; k < 40; k++) st.AddPoint(x0 + k * 6, y0 + MathF.Sin(k * 0.3f) * 20, 1f, k);
            Doc.AddStroke(st);
        }
        Doc.InvalidateAll();
        SettleFrames(400);

        double Measure(int frames)
        {
            // **跑 3 轮取最小值**：这台机器上同时跑着远程控制和几个吃 GPU 的客户端，
            // 平均值被"别人抢走的那几毫秒"抬高得离谱（同一档实测在 1.1～3.0 之间跳）。
            // 最小值 = 最"干净"的那一轮，用它比才比得出我们自己代码的差别。
            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                SettleFrames(150);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++) { _dirty = true; RenderAll(); }
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds / frames);
            }
            return best;
        }

        SetUi(new HeadlessUi());
        SettleFrames(200);
        double none = Measure(200);

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        double ball = Measure(200);                       // 收起态：一个球

        if (CurrentUi is InkUi.FullUi ui)
        {
            ui.SetExpandForTest(1f);                     // 展开（显式跳 1；别用会"翻面"的旧 SnapForTest）
            ui.OpenRailForTest();                         // 色带也张开（最贵的一档）
        }
        SettleFrames(300);
        double full = Measure(200);

        Console.WriteLine($"  不挂界面            {none,6:F2} ms/帧");
        Console.WriteLine($"  收起成一个球        {ball,6:F2} ms/帧   （比不挂多 {ball - none,5:F2}）");
        Console.WriteLine($"  展开＋色带张开      {full,6:F2} ms/帧   （比不挂多 {full - none,5:F2}）");

        // ---- 归因：把几块分别关掉再量，看钱花在哪 ----
        Console.WriteLine();
        Console.WriteLine("  归因（从「展开＋色带」这一档里省了多少）：");
        void Attrib(string name, Action set)
        {
            set();
            SettleFrames(200);
            double t = Measure(200);
            Console.WriteLine($"    {name,-22} {t,6:F2} ms/帧   （省 {full - t,5:F2}）");
        }
        Attrib("不算阴影", () => InkUi.FullUi.PerfSkipShadow = true);
        InkUi.FullUi.PerfSkipShadow = false;
        Attrib("不算凹槽与顶光", () => InkUi.FullUi.PerfSkipChrome = true);
        InkUi.FullUi.PerfSkipChrome = false;
        Attrib("不算色片细节", () => InkUi.FullUi.PerfSkipSwatchDetail = true);
        InkUi.FullUi.PerfSkipSwatchDetail = false;
        Attrib("不算图标", () => InkUi.FullUi.PerfSkipIcons = true);
        InkUi.FullUi.PerfSkipIcons = false;

        bool ok = (full - none) <= 1.5;                   // 计划里定的红线：≤1.5ms/帧
        Console.WriteLine(ok
            ? "  PASS: 完整展开态的额外代价在 1.5 毫秒以内"
            : $"  FAIL: 完整展开态比不挂界面贵了 {full - none:F2} 毫秒（红线 1.5）");
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }
    private bool OffscreenShot(string path)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderUiToBgra(this, 24, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>同上，但**画哪一块自己指定**（逻辑像素）——见 `RenderUiToBgra` 的重载。</summary>
    private bool OffscreenShot(string path, in RectF bounds)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderUiToBgra(this, 24, bounds, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>同上，但画的是**浮动层**（选中框 / 操作条 / 小面板）——坐标给画布坐标。</summary>
    private bool OffscreenFloatingShot(string path, in RectF canvasBounds)
    {
        if (_windows.Count == 0) return false;
        var bytes = _windows[0].RenderFloatingToBgra(this, 24, canvasBounds, out int w, out int h);
        return SaveBgraAsBmp(path, bytes, w, h);
    }

    /// <summary>
    /// `--makeicon &lt;out.ico&gt;`：**用界面自己的渲染**生成程序图标。
    ///
    /// 2026-10-01 第五轮定：图标 = 设计稿 v5 的 **B 档「Fluent 笔 ＋ 带笔锋的红笔迹」**
    /// （用户："你笔锋弧线这一版做得挺好的，我想使用这个"；稿子见
    /// design/图标-设计稿v5-线条型的笔.png 与 v5b-笔迹四选.png，画在 <see cref="AppIconUi"/> 里）。
    /// 上一版是"实心的大笔 · 白砖"，再上一版是"收起态那颗球"，都留在 git 历史里；这条命令的机制没变：
    /// 把窗口 DPI **临时放大**再离屏出图 —— 界面是按"逻辑坐标 × DPI/96"画的，
    /// 所以放大 DPI 等于**按矢量重画一张大的**，而不是把 96 的小图拉大（拉大会糊）。
    /// 再把那一张按各档尺寸缩下去、装成一个多尺寸 .ico。
    /// </summary>
    private void MakeIcon(string path)
    {
        SetUi(new AppIconUi());
        SettleFrames(120);
        if (CurrentUi is not AppIconUi ui || _windows.Count == 0)
        {
            Console.WriteLine("  出图标失败：图标界面没挂上 / 没有窗口");
            _quit = true;
            return;
        }

        var box = ui.QueryBounds();
        if (box.IsEmpty) { Console.WriteLine("  出图标失败：占用矩形是空的"); _quit = true; return; }

        // 目标边长 256：图标界面是 64 逻辑像素的方框，DPI 抬到 384 正好出 256×256。
        var win = _windows[0];
        uint want = (uint)Math.Clamp(MathF.Round(96f * 256f / (box.MaxX - box.MinX)), 96, 96 * 12);
        uint dpi0 = win.Dpi;
        win.Dpi = want;
        byte[] px = win.RenderUiToBgraTransparent(this, 0, box, out int bw, out int bh);
        win.Dpi = dpi0;
        if (px == null || bw <= 0)
        {
            Console.WriteLine("  出图标失败：" + (win.LastError ?? "没画出来"));
            _quit = true;
            return;
        }
        Console.WriteLine($"  渲染尺寸 {bw}×{bh}（为它把窗口 DPI 临时抬到 {want}）");

        // **不做圆形蒙版**：形状就是白砖自己的圆角，砖外本来就是透明
        // （旧版那颗球才需要把方形柔光清成圆；这条注释留着，免得下次改回去时忘了）。
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            WriteMultiSizeIco(path, px, bw, bh, sizes);
            // 顺带出一张 PNG 预览：笔对不对、红不红、砖的圆角顺不顺，**只能看图**。
            string png = Path.ChangeExtension(path, ".png");
            using (var b = BgraToBitmap(px, bw, bh)) b.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  已写出 {path}（{sizes.Length} 档：{string.Join('/', sizes)}）");
            Console.WriteLine($"  预览图 {png}（人眼核对用）");
        }
        catch (Exception ex) { Console.WriteLine("  出图标失败：" + ex.Message); }
        _quit = true;
    }

    /// <summary>
    /// 量出"球"的半径（像素）：沿中心行/列找 α≥96 的最远点，取两者的较小值。
    /// 为什么要量而不是算：真实半径由界面渲染（含 DPI 缩放与投影）决定，硬算容易差几像素。
    /// 投影的 α 很小（≤30 上下），进不了 96 这道门，所以量到的是球本身。
    ///
    /// ⚠ 2026-10-01 起**没有调用**（图标换成了方角的「白砖＋大笔」，不再需要圆形蒙版）。
    ///   留着是给"以后真要做圆形图标"的那一刻：连同下面那个 <see cref="MaskIconToCircle"/>，
    ///   两条都在"球"的版本上实测过（踩过的坑写在注释里，比代码值钱）。
    /// </summary>
    private static int IconRadiusPx(byte[] bgra, int w, int h)
    {
        int cx = w / 2, cy = h / 2, rx = 0, ry = 0;
        for (int x = 0; x < w; x++) if (bgra[(cy * w + x) * 4 + 3] >= 96) rx = Math.Max(rx, Math.Abs(x - cx));
        for (int y = 0; y < h; y++) if (bgra[(y * w + cx) * 4 + 3] >= 96) ry = Math.Max(ry, Math.Abs(y - cy));
        return Math.Max(1, Math.Min(rx, ry) - 1);      // 再收 1px：把抗锯齿那圈也收进去
    }

    /// <summary>
    /// 圆形 alpha 蒙版：圆外全透明，边缘 1px 线性过渡（抗锯齿）。
    /// ⚠ 数据是**预乘** BGRA，四个分量要一起乘——只把 α 清零会留下黑边（预乘的经典坑）。
    /// </summary>
    private static void MaskIconToCircle(byte[] bgra, int w, int h, int radius)
    {
        float cx = w / 2f, cy = h / 2f;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float k = Math.Clamp(radius - d + 0.5f, 0f, 1f);      // 圆内 1、圆外 0、边缘过渡
                if (k >= 1f) continue;
                int o = (y * w + x) * 4;
                bgra[o]     = (byte)(bgra[o]     * k);
                bgra[o + 1] = (byte)(bgra[o + 1] * k);
                bgra[o + 2] = (byte)(bgra[o + 2] * k);
                bgra[o + 3] = (byte)(bgra[o + 3] * k);
            }
        }
    }

    /// <summary>BGRA（D2D 的**预乘**格式）→ GDI+ 位图。像素行是自上而下的。</summary>
    private static System.Drawing.Bitmap BgraToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                             System.Drawing.Imaging.ImageLockMode.WriteOnly,
                             System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(bgra, y * w * 4, row, 0, w * 4);
                Marshal.Copy(row, 0, IntPtr.Add(d.Scan0, y * d.Stride), w * 4);
            }
        }
        finally { bmp.UnlockBits(d); }
        return bmp;
    }

    /// <summary>
    /// 写一个**多尺寸 ICO**（每档一张 PNG——Vista 以后系统就认这种）。
    ///
    /// 格式很小，手写比引第三方库省事：6 字节目录头 ＋ 每档 16 字节目录项 ＋ 各档 PNG 数据。
    /// 注意目录项里宽高各只占**一个字节**：256 要写成 0（这是 ICO 格式的约定，不是笔误）。
    /// </summary>
    private static void WriteMultiSizeIco(string path, byte[] bgra, int bw, int bh, int[] sizes)
    {
        using var src = BgraToBitmap(bgra, bw, bh);
        var blobs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var one = new System.Drawing.Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(one))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, s, s));
            }
            using var ms = new MemoryStream();
            one.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            blobs.Add(ms.ToArray());
        }

        using var fs = File.Create(path);
        using var wr = new BinaryWriter(fs);
        wr.Write((ushort)0); wr.Write((ushort)1); wr.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            byte wh = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            wr.Write(wh); wr.Write(wh);
            wr.Write((byte)0);            // 调色板颜色数（真彩不用）
            wr.Write((byte)0);            // 保留
            wr.Write((ushort)1);          // 平面数
            wr.Write((ushort)32);         // 位深
            wr.Write(blobs[i].Length);
            wr.Write(offset);
            offset += blobs[i].Length;
        }
        foreach (var b in blobs) wr.Write(b);
    }

    private bool SaveBgraAsBmp(string path, byte[] bytes, int w, int h)
    {
        if (bytes == null)
        {
            Console.WriteLine("离屏出图失败：" + (_windows[0].LastError ?? "没有占用矩形"));
            return false;
        }

        // BMP：文件头 14 ＋ 信息头 40，像素按 BGRA、行从下往上（行宽 4 字节对齐）。
        int stride = w * 4;
        int size = 54 + stride * h;
        var outBytes = new byte[size];
        void W16(int at, int v) { outBytes[at] = (byte)v; outBytes[at + 1] = (byte)(v >> 8); }
        void W32(int at, int v)
        {
            outBytes[at] = (byte)v; outBytes[at + 1] = (byte)(v >> 8);
            outBytes[at + 2] = (byte)(v >> 16); outBytes[at + 3] = (byte)(v >> 24);
        }
        outBytes[0] = (byte)'B'; outBytes[1] = (byte)'M';
        W32(2, size); W32(10, 54); W32(14, 40);
        W32(18, w); W32(22, h);
        W16(26, 1); W16(28, 32);
        W32(34, stride * h);
        for (int y = 0; y < h; y++)
            Array.Copy(bytes, (h - 1 - y) * w * 4, outBytes, 54 + y * stride, w * 4);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllBytes(path, outBytes);
            Console.WriteLine($"已离屏出图 {path}（{w}×{h}）");
            ExitCode = 0;
            _quit = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("写出图失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// **页界线摆样**：白板打底、铺三屏板书，出两张图——
    ///   · `<path>`：相机停在两屏之间（页界线落在屏幕中间，最好看细节）；
    ///   · `<path>-对齐.bmp`：相机正好停在第 2 屏（页界线压在屏幕上下沿）。
    /// 为什么要两张：一眼看出"一屏一页"的分界到底顺不顺眼、会不会被当成画面上的脏点。
    /// </summary>
    private void PageShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        BoardOn = true;
        BoardColor = InkPalette.BoardPresets[0].Color;
        // --pattern N：出图时带上白板底纹（0 无 / 1 方格 / 2 横线；--step 给间距）
        {
            var cli0 = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(cli0, "--pattern");
            int si = Array.IndexOf(cli0, "--step");
            if (pi >= 0 && pi + 1 < cli0.Length && int.TryParse(cli0[pi + 1], out int pat))
            {
                float step = si >= 0 && si + 1 < cli0.Length && float.TryParse(cli0[si + 1], out float sv)
                             ? sv : 40f;
                SetBoardPatternFromUi(pat, step);
            }
        }

        for (int screen = 0; screen < 3; screen++)
            for (int row = 0; row < 4; row++)
            {
                var s = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                    Color = new Color4(0.10f, 0.11f, 0.14f, 1f), Width = 5f * DpiScale,
                };
                float y = _virtualY + screen * _virtualH + 240f + row * 380f;
                for (int i = 0; i <= 36; i++)
                    s.AddPoint(_virtualX + 420f + i * 52f, y + MathF.Sin(i * 0.4f) * 26f, 1f, i * 8);
                Doc.AddStroke(s);
            }

        // 第 2 屏右边标一句"这里是第 2 屏"，看图时好对号
        var tag = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.13f, 0.36f, 0.24f, 1f), Width = 9f * DpiScale,
        };
        float ty = _virtualY + _virtualH + 150f;
        for (int i = 0; i <= 60; i++) tag.AddPoint(_virtualX + 2200f + i * 4f, ty + (i % 7) * 6f, 1f, i * 8);
        Doc.AddStroke(tag);

        void Shot(float offset, string outPath)
        {
            ViewOffsetY = offset;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = offset; }
            Doc.InvalidateAll();
            SettleFrames(600);
            bool ok = ScreenProbe.SaveBmp(outPath, (int)_virtualX, (int)_virtualY,
                                          (int)_virtualW, (int)_virtualH);
            Console.WriteLine(ok ? $"已出图 {outPath}" : $"出图失败 {outPath}");
        }

        Shot(-_virtualH * 0.55f, path);                       // 界线落在屏幕中间
        string aligned = System.IO.Path.ChangeExtension(path, null) + "-对齐.bmp";
        Shot(-_virtualH * 1.0f, aligned);                     // 正好第 2 屏

        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        _quit = true;
    }

    private void PanelShow(string path)
    {
        _panelShowMore = Environment.GetCommandLineArgs().Contains("--more");
        bool showHelp = Environment.GetCommandLineArgs().Contains("--helpui");
        _panelShowMini = Environment.GetCommandLineArgs().Contains("--mini");
        _panelShowBand = Environment.GetCommandLineArgs().Contains("--band");
        SetUiFactory(() => new InkUi.FullUi());
        // 出图专用：把“有新版本”状态摆出来，查看红点位置（产品里由静默检查设置）。
        if (Environment.GetCommandLineArgs().Contains("--update"))
        {
            UpdateState = UpdateStage.Available;
            UpdateText = "有新版本 9.2.2";
        }
        Tool = Tool.Pen;
        BoardOn = true;                      // 白板打底：截出来的图里没有桌面上的杂东西
        // --pixel：把工具切成面积橡皮再出图（看"橡皮按钮的图标跟着换"那一版）
        if (Environment.GetCommandLineArgs().Contains("--pixel")) Tool = Tool.PixelEraser;
        if (Environment.GetCommandLineArgs().Contains("--eraser")) Tool = Tool.Eraser;
        if (Environment.GetCommandLineArgs().Contains("--laser")) Tool = Tool.Laser;
        if (Environment.GetCommandLineArgs().Contains("--hl")) Tool = Tool.Highlighter;
        // --select：把工具切成选择（看"选择那一格的图标"）；配 --lasso 就是套索那一档
        //（2026-09-26 加：选择那格现在"选中什么显示什么"，两种框选各一个图标）。
        if (Environment.GetCommandLineArgs().Contains("--select")) Tool = Tool.Marquee;
        // --board-green / --board-black：换板色再出图（看"白板那格填的是哪块板的颜色"）
        {
            var argvB = Environment.GetCommandLineArgs();
            if (argvB.Contains("--board-green")) Host.Commands.SetBoardColor(InkPalette.BoardPresets[1].Color);
            if (argvB.Contains("--board-black")) Host.Commands.SetBoardColor(InkPalette.BoardPresets[2].Color);
        }
        // --pattern N / --step N：把白板底纹摆成某一档再出图（看色带上那两格文字和档位点）。
        // 和 `--cell` / `--zoom` / `--dashline` 同一类：产品里没有这个入口，只服务"图上看细节"。
        {
            var argvP = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(argvP, "--pattern");
            if (pi >= 0 && pi + 1 < argvP.Length && int.TryParse(argvP[pi + 1], out int patArg))
            {
                int si = Array.IndexOf(argvP, "--step");
                float stepArg = si >= 0 && si + 1 < argvP.Length
                                && float.TryParse(argvP[si + 1], out float sv) ? sv : 40f;
                Host.Commands.SetBoardPattern(patArg, stepArg);
            }
        }
        // --board-on：把白板真的打开再出图。**只影响图**：`BoardOn = true` 那句设的是 App
        // 自己的字段，引擎那边是关着的——而"白板色带最右端那个 ✕"在板关着时是压暗的
        //（见 FullUi.DrawBandAction），所以要看它正常的样子必须真开一下板。
        if (Environment.GetCommandLineArgs().Contains("--board-on")) Host.Commands.SetBoard(true);
        // --shape <名字>：把工具切成某一种图形再出图（看"主条那一格的图标跟着种类变"）。
        // 名字用命令行里那套小写写法，和 --shapetoolshow 的 --circle/--triangle 一致。
        {
            var argv = Environment.GetCommandLineArgs();
            int si = Array.IndexOf(argv, "--shape");
            if (si >= 0 && si + 1 < argv.Length)
            {
                Tool = argv[si + 1].ToLowerInvariant() switch
                {
                    "line" => Tool.Line,
                    "rect" or "rectangle" => Tool.Rectangle,
                    "ellipse" => Tool.Ellipse,
                    "circle" => Tool.Circle,
                    "triangle" => Tool.Triangle,
                    "parallelogram" => Tool.Parallelogram,
                    "arrow" => Tool.Arrow,
                    // 2026-09-20：四种曲线（出图看"图形格两行 + 新图标"时要用）
                    "parabola" => Tool.Parabola,
                    "hyperbola" => Tool.Hyperbola,
                    "sine" => Tool.Sine,
                    "cosine" => Tool.Cosine,
                    "wave" => Tool.Wave,
                    "tangent" => Tool.Tangent,
                    "cylinder" => Tool.Cylinder,
                    "cone" => Tool.Cone,
                    "conefrustum" => Tool.ConeFrustum,
                    "sphere" => Tool.Sphere,
                    // 立体那一族（出图看"图形格第二行 + 档位点"时要用）：三个的名字就是
                    // 它们各自的工具名小写（棱柱 / 棱锥 / 棱台）。
                    "prism" => Tool.Prism,
                    "pyramid" => Tool.Pyramid,
                    "frustum" => Tool.Frustum,
                    _ => Tool.Line,
                };
            }
        }
        // 上面那几行改的是 **App 自己的 Tool 字段**，界面不会因此重算"当前是哪一格"
        // （`Tool` 是个裸字段，没有通知）。而出图要看的恰恰是"图形那一格的两行"，
        // 所以这里**走一次真的换工具**（引擎 → 通知界面 → 上带跟着切到图形格）。
        Host.Commands.SetTool(Tool);
        // 套索那一档要单独设（它和"选的是哪个工具"是两回事，见 SetSelectMode）
        if (Environment.GetCommandLineArgs().Contains("--lasso"))
            Host.Commands.SetSelectMode(SelectMode.Lasso);
        SettleFrames(400);

        // --hide：贴边隐藏**要先写进偏好再挂界面**（界面是在 Attach 里读偏好的），
        // 挂完再把露头一把按到底。配合 --ball 出"收起态露头"，不配就是"展开态露头"。
        bool wantHide = Environment.GetCommandLineArgs().Contains("--hide");
        if (wantHide)
        {
            SetUiPref("hide", "1");
            SetUiFactory(() => new InkUi.FullUi());
        }

        if (CurrentUi is InkUi.FullUi ui)
        {
            // --ball：**出收起态（那个球）**——启动默认就是展开态，所以要显式跳 0；
            // 不传就保持展开（收起/贴边这一类毛病只在球上看得见，所以要能单拍它）。
            // ⚠ 2026-10-03 修：原来写的是 `if (!wantBall) ui.SnapForTest()`，而那一刻的
            //   `SnapForTest` 是**跳到另一端**的开关、不是"展开"——启动时本来就展开，
            //   那一跳反而把默认态收成了球：**不带 --ball 出球、带 --ball 出展开**，
            //   和注释正好相反（出 README 主图时实测到）。现在显式跳 0 / 跳 1。
            bool wantBall = Environment.GetCommandLineArgs().Contains("--ball");
            ui.SetExpandForTest(wantBall ? 0f : 1f);
            if (wantHide) ui.ForcePeekForTest(0f);
            // --expand <0..1>：把"球 → 带子"的展开进度钉在中间某一帧（核对动画用）
            {
                var argv = Environment.GetCommandLineArgs();
                int ei = Array.IndexOf(argv, "--expand");
                if (ei >= 0 && ei + 1 < argv.Length && float.TryParse(argv[ei + 1], out float ex))
                    ui.SetExpandForTest(ex);
            }
            if (_panelShowMini) ui.SetProfileForTest(0);     // --mini：极简档（短胶囊）
            if (_panelShowBand) ui.OpenRailForTest();        // --band：把色线张开成设置条
            // --picker：把笔的取色板打开（设计验收用；默认落在第 3 个色位，蓝色好核对）。
            // 顺便塞几条“最近使用”，让截图能看到右栏下半段（产品里由用户实际取色产生）。
            if (Environment.GetCommandLineArgs().Contains("--picker"))
            {
                var seed = new[]
                {
                    new Color4(0.05f, 0.58f, 0.53f, 1f), new Color4(0.88f, 0.11f, 0.28f, 1f),
                    new Color4(0.49f, 0.23f, 0.93f, 1f), new Color4(0.92f, 0.35f, 0.05f, 1f),
                    new Color4(0.40f, 0.64f, 0.05f, 1f), new Color4(0.03f, 0.57f, 0.70f, 1f),
                    new Color4(0.86f, 0.15f, 0.47f, 1f), new Color4(0.29f, 0.33f, 0.39f, 1f),
                };
                foreach (var c in seed) Host.Commands.SetPenSwatchColor(7, c, rememberRecent: true);
                Host.Commands.SetPenSwatchColor(7, InkUi.Tokens.Palette[7].Color, rememberRecent: false);
                Host.Commands.SelectPenSwatch(2);
                ui.OpenPenPickerForTest();
            }

            // --hlpicker：荧光笔取色板（设计验收用；顺便塞几条最近色，看看右栏下半段）。
            if (Environment.GetCommandLineArgs().Contains("--hlpicker"))
            {
                Host.Commands.SetTool(Tool.Highlighter);
                var hlSeed = new[]
                {
                    new Color4(0.05f, 0.58f, 0.53f, 1f), new Color4(0.88f, 0.11f, 0.28f, 1f),
                    new Color4(0.49f, 0.23f, 0.93f, 1f), new Color4(0.92f, 0.35f, 0.05f, 1f),
                    new Color4(0.40f, 0.64f, 0.05f, 1f), new Color4(0.03f, 0.57f, 0.70f, 1f),
                };
                foreach (var c in hlSeed)
                    Host.Commands.SetHighlighterSwatchColor(4, c, rememberRecent: true, normalize: true);
                var orange = InkUi.Tokens.HighlighterPalette[4].Color;
                Host.Commands.SetHighlighterSwatchColor(4, orange, rememberRecent: false, normalize: false);
                Host.Commands.SetHighlighterOpacity(0, 2);
                Host.Commands.SelectHighlighterSwatch(0);
                ui.OpenHighlighterPickerForTest(0);
            }
            // --cell N：把上带掰到第 N 格再出图（N=2 就是白板那条
            // `[上一屏] [白][绿][黑] [下一屏]` ＋ "第 N 屏"）
            var cli = Environment.GetCommandLineArgs();
            int ci = Array.IndexOf(cli, "--cell");
            if (ci >= 0 && ci + 1 < cli.Length && int.TryParse(cli[ci + 1], out int cellArg))
                ui.SelectBandCellForTest(cellArg);
            // --width N：先把粗细调成 N 再出图（看"真实大小预览"用）
            int wi = Array.IndexOf(cli, "--width");
            if (wi >= 0 && wi + 1 < cli.Length && float.TryParse(cli[wi + 1], out float widthArg))
            {
                Host.Commands.SetWidth(widthArg);
                SettleFrames(120);
            }
            if (cli.Contains("--preview")) ui.ShowSizePreviewForTest();
            // --dashline N：把「直线」那一格的档位往后切 N 下（看"图标跟着线型换"那三档：
            // N=0 实线、1 虚线、2 点线；配 `--shape line` 用）。产品里没有这个入口，
            // 它只服务"图上看细节"（和 --zoom / --cell 同一类）。
            {
                int dli = Array.IndexOf(cli, "--dashline");
                if (dli >= 0 && dli + 1 < cli.Length && int.TryParse(cli[dli + 1], out int dashArg))
                {
                    for (int k = 0; k < dashArg; k++) Host.Commands.CycleLineDash();
                    SettleFrames(120);
                }
            }
            // --paraaxis N：把「抛物线」那一格的档位往后切 N 下（N=1 就是"左右抛物"那一档）。
            // 和 --dashline 同一类，只服务"图上看细节"。
            {
                int pai = Array.IndexOf(cli, "--paraaxis");
                if (pai >= 0 && pai + 1 < cli.Length && int.TryParse(cli[pai + 1], out int paArg))
                {
                    for (int k = 0; k < paArg; k++) Host.Commands.CycleParabolaAxis();
                    SettleFrames(120);
                }
            }
            // --dark：出图前直接切深色主题（**不落盘**；和点「深色主题」那一行同一条路）
            if (Environment.GetCommandLineArgs().Contains("--dark")) ui.SetDarkForTest(true);
            // --uisize N：出图前切界面缩放档（0.8 / 1.0 / 1.3…；和设置里同一命令）。
            {
                var argvUi = Environment.GetCommandLineArgs();
                int uiAt = Array.IndexOf(argvUi, "--uisize");
                if (uiAt >= 0 && uiAt + 1 < argvUi.Length
                    && float.TryParse(argvUi[uiAt + 1], System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out float uiArg))
                {
                    ui.SetUiScaleForTest(uiArg);
                    Console.WriteLine($"  [panelshow] uisize={uiArg}");
                }
            }
            // --more [--page N]：出**中央「更多」面板**（0 课堂 / 1 墨迹 / 2 设置）。
            // 面板打开时 QueryBounds 是整块屏幕，截图范围在下面单独裁（见 shot 那一段）。
            if (_panelShowMore)
            {
                ui.OpenMoreForTest();
                var argvMore = Environment.GetCommandLineArgs();
                int pi2 = Array.IndexOf(argvMore, "--page");
                if (pi2 >= 0 && pi2 + 1 < argvMore.Length && int.TryParse(argvMore[pi2 + 1], out int pageArg))
                    ui.SetMorePageForTest(pageArg);
                // --settingscat N：先把设置页滚到第 N 个分类（0 工具条 / 1 常规 / 2 书写 …）。
                int si2 = Array.IndexOf(argvMore, "--settingscat");
                if (si2 >= 0 && si2 + 1 < argvMore.Length && int.TryParse(argvMore[si2 + 1], out int catArg))
                {
                    ui.SettingsScrollToCategoryForTest(catArg);
                    Console.WriteLine($"  [panelshow] settingscat={catArg}");
                }
                // --clickrow N：出图前点一下设置页第 N 行（语言下拉这类“点开才出现”的层）。
                int ri2 = Array.IndexOf(argvMore, "--clickrow");
                if (ri2 >= 0 && ri2 + 1 < argvMore.Length && int.TryParse(argvMore[ri2 + 1], out int rowArg))
                {
                    ui.ClickSettingsRowForTest(rowArg);
                    Console.WriteLine($"  [panelshow] clickrow={rowArg} langMenu={ui.LangMenuOpenForTest}");
                }
                int lp2 = Array.IndexOf(argvMore, "--langpick");
                if (lp2 >= 0 && lp2 + 1 < argvMore.Length && int.TryParse(argvMore[lp2 + 1], out int pickArg))
                {
                    ui.PickLangForTest(pickArg);
                    Console.WriteLine($"  [panelshow] langpick={pickArg}");
                }
            }
            // --tip N：把第 N 个悬停提示直接摆出来（橡皮提示等；不用真悬停等 500ms）。
            // 它不依赖 --more：主条工具、色片、设置行都能单独出图。
            {
                var argvTip = Environment.GetCommandLineArgs();
                int ti2 = Array.IndexOf(argvTip, "--tip");
                if (ti2 >= 0 && ti2 + 1 < argvTip.Length && int.TryParse(argvTip[ti2 + 1], out int tipArg))
                {
                    ui.ShowTipForTest(tipArg);
                    Console.WriteLine($"  [panelshow] tip={tipArg}");
                }
            }
            if (showHelp)
            {
                // 走真实入口：先开「更多」，再点「使用说明」格；顺便把“格子真的能打开”
                // 这条路径也测到。找不到格子时才退回直开，出图不静默失败。
                ui.OpenMoreForTest();
                var tile = ui.HubTileRectForTest(8);
                if (!tile.IsEmpty)
                {
                    var ev = new InkEngine.UiPointerEvent(
                        (tile.MinX + tile.MaxX) * 0.5f, (tile.MinY + tile.MaxY) * 0.5f,
                        1f, false, false);
                    ui.PointerDown(ev);
                    ui.PointerUp(ev);
                }
                if (!ui.HelpOpenForTest) ui.OpenHelpForTest();
                var argvHelp = Environment.GetCommandLineArgs();
                int ciHelp = Array.IndexOf(argvHelp, "--clicktab");
                if (ciHelp >= 0 && ciHelp + 1 < argvHelp.Length
                    && int.TryParse(argvHelp[ciHelp + 1], out int clickTab))
                {
                    var tr = ui.HelpTabRectForTest(clickTab);
                    var cev = new InkEngine.UiPointerEvent(
                        (tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f,
                        1f, false, false);
                    ui.PointerDown(cev);
                    ui.PointerUp(cev);
                    Console.WriteLine($"  [helpui] clicktab={clickTab} open={ui.HelpOpenForTest} now={ui.HelpTabForTest}");
                }
                int hi = Array.IndexOf(argvHelp, "--tab");
                if (hi >= 0 && hi + 1 < argvHelp.Length && int.TryParse(argvHelp[hi + 1], out int tabArg))
                    ui.SetHelpTabForTest(tabArg);
            }
            SettleFrames(500);

            // --timerwin [--run|--edit|--min|--full] / --rollwin [--n 3] [--names]：
            // 课堂窗画在**引擎浮层**里，离屏那条路（只画界面层）拍不到它，
            // 所以等它真画出来，直接截屏幕上的窗口矩形（1:1）。
            if (Environment.GetCommandLineArgs().Contains("--timerwin")
                || Environment.GetCommandLineArgs().Contains("--rollwin"))
            {
                var argvW = Environment.GetCommandLineArgs();
                if (argvW.Contains("--timerwin"))
                {
                    OpenTimerCardFromUi();
                    SettleFrames(200);
                    var tw = TimerCardRect();
                    float tu = TimerUnit();
                    var cin = TimerWin.CardRect(tw, tu, false);
                    float lu = TimerWin.Layout(cin, tu);
                    void ClickAxis(in RectF r)
                    {
                        TimerPointerDownForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                        TimerPointerUpForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
                    }
                    if (argvW.Contains("--stopwatch")) { StartTimerFromUi(TimerMode.Stopwatch, 0f); SettleFrames(900); }
                    else if (argvW.Contains("--run")) StartTimerFromUi(TimerMode.Countdown, 300f);
                    if (TimerKind == TimerMode.Stopwatch && argvW.Contains("--laps"))
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            SettleFrames(420);
                            ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Lap));
                        }
                        SettleFrames(220);
                    }
                    if (TimerKind == TimerMode.Stopwatch && argvW.Contains("--pause"))
                    {
                        PauseTimerFromUi();
                        SettleFrames(220);
                    }
                    int tbi = Array.IndexOf(argvW, "--tab");
                    if (tbi >= 0 && tbi + 1 < argvW.Length && int.TryParse(argvW[tbi + 1], out int tabArg))
                    {
                        float tcu = TimerUnit();
                        var tcin = TimerWin.CardRect(TimerCardRect(), tcu, false);
                        float tlu = TimerWin.Layout(tcin, tcu);
                        var tr = TimerWin.TabRect(tcin, tlu, Math.Clamp(tabArg, 0, 2));
                        TimerPointerDownForTest((tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f);
                        TimerPointerUpForTest((tr.MinX + tr.MaxX) * 0.5f, (tr.MinY + tr.MaxY) * 0.5f);
                    }
                    if (argvW.Contains("--full")) ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Fullscreen));
                    else if (argvW.Contains("--edit")) ClickAxis(TimerWin.ValueRect(cin, lu));
                    else if (argvW.Contains("--min")) ClickAxis(TimerWin.BtnRect(cin, lu, TimerZone.Minimize));
                }
                if (argvW.Contains("--rollwin"))
                {
                    if (argvW.Contains("--names"))
                    {
                        string tmpNames = Path.Combine(Path.GetTempPath(), "inkteach-shot-names.txt");
                        File.WriteAllLines(tmpNames, new[] { "张伟", "李娜", "王强", "刘洋", "陈晨" });
                        InkEngine.InkEngine.NamesPathOverride = tmpNames;
                        ReloadNamesFromUi();
                    }
                    OpenRollCardFromUi();
                    int ri = Array.IndexOf(argvW, "--n");
                    if (ri >= 0 && ri + 1 < argvW.Length && int.TryParse(argvW[ri + 1], out int nArg) && nArg > 1)
                    {
                        var rw = RollCardRect();
                        float ru = RollUnit();
                        var pr = RollWin.PlusRect(rw, ru);
                        for (int k = 1; k < Math.Min(nArg, 60); k++)
                        {
                            RollPointerDownForTest((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
                            RollPointerUpForTest((pr.MinX + pr.MaxX) * 0.5f, (pr.MinY + pr.MaxY) * 0.5f);
                        }
                    }
                    StartRollFromUi();
                    double rt0 = NowMs;
                    while (RollingNow && NowMs - rt0 < 3000) { DrainMessages(); Thread.Sleep(10); RollTickForTest(); }
                }
                SettleFrames(400);
                // --board [--boardblack]：出图前铺一层白板，让背景是干净的。
                // 课堂窗是**截屏**出来的（离屏那条路只画界面层，拍不到它），不加这层的话
                // 图里会混着桌面图标和当时的窗口——拿去当宣传图很难看。
                if (argvW.Contains("--board"))
                {
                    Host.Commands.SetBoard(true);
                    if (argvW.Contains("--boardblack"))
                        Host.Commands.SetBoardColor(new Color4(0.13f, 0.15f, 0.17f, 1f));
                    SettleFrames(300);
                }
                if (argvW.Contains("--keepboard")) SetPassThroughFromUi(false);
                SettleFrames(300);
                var wr = argvW.Contains("--timerwin") ? TimerCardRect() : RollCardRect();
                bool okW = ScreenProbe.SaveBmp(path,
                    (int)MathF.Floor(wr.MinX), (int)MathF.Floor(wr.MinY),
                    Math.Max(1, (int)MathF.Ceiling(wr.MaxX - wr.MinX)),
                    Math.Max(1, (int)MathF.Ceiling(wr.MaxY - wr.MinY)));
                Console.WriteLine(okW
                    ? $"已出图 {path}（截屏 {wr.MaxX - wr.MinX:F0}×{wr.MaxY - wr.MinY:F0}）"
                    : "出图失败");
                ExitCode = okW ? 0 : 1;
                _quit = true;
                return;
            }

            // 优先离屏出图（锁屏 / 远程也能出，图里不混桌面）。
            // --pickshow：直接进吸管冻结帧，截一张全屏看左下角面板/抓屏是否正确。
            if (Environment.GetCommandLineArgs().Contains("--pickshow"))
            {
                Host.Commands.BeginScreenColorPick();
                SettleFrames(900);
                bool okPick = ScreenProbe.SaveBmp(path,
                    (int)MathF.Floor(_virtualX), (int)MathF.Floor(_virtualY),
                    Math.Max(1, (int)MathF.Ceiling(_virtualW)), Math.Max(1, (int)MathF.Ceiling(_virtualH)));
                Console.WriteLine(okPick ? $"已出图 {path}（吸管全屏）" : "出图失败");
                ExitCode = okPick ? 0 : 1;
                _quit = true;
                return;
            }

            // **范围要连"画到外面那一圈"一起给**（投影，见 IOverlayUi.PaintMargin）：
            // 这一条的边上留白只有 24 **物理**像素，200% 缩放下就是 12 逻辑像素，
            // 而投影最远胀出去 16+8 逻辑像素——不给就会被切掉一半，图看着像"没有投影"。
            var b = ui.QueryBounds();
            // 悬停提示画在面板外：无论开不开「更多」，出图都要把它框进来。
            {
                var tr0 = ui.TipRectForTest;
                if (!tr0.IsEmpty)
                    b = new RectF
                    {
                        MinX = MathF.Min(b.MinX, tr0.MinX - 12f),
                        MinY = MathF.Min(b.MinY, tr0.MinY - 12f),
                        MaxX = MathF.Max(b.MaxX, tr0.MaxX + 12f),
                        MaxY = MathF.Max(b.MaxY, tr0.MaxY + 12f),
                    };
            }
            // --more：面板打开时占用 = 整块屏幕，照它出图会得到一整屏（20MB 级）。
            // 设计验收要的是那块卡片 ＋ 周围一圈遮罩，所以裁到"面板 ＋ 64"。
            if (_panelShowMore || showHelp)
            {
                var mr = ui.MoreRectForTest;
                b = new RectF
                {
                    MinX = mr.MinX - 64f, MinY = mr.MinY - 64f,
                    MaxX = mr.MaxX + 64f, MaxY = mr.MaxY + 64f,
                };
                // 语言下拉会伸出设置面板，出图范围要连它一起框进来。
                if (ui.LangMenuOpenForTest)
                {
                    var lr = ui.LangMenuRectForTest;
                    b = new RectF
                    {
                        MinX = MathF.Min(b.MinX, lr.MinX - 16f),
                        MinY = MathF.Min(b.MinY, lr.MinY - 16f),
                        MaxX = MathF.Max(b.MaxX, lr.MaxX + 16f),
                        MaxY = MathF.Max(b.MaxY, lr.MaxY + 16f),
                    };
                }
                // 悬停提示画在面板外，出图也要框进来。
                var tipRect = ui.TipRectForTest;
                if (!tipRect.IsEmpty)
                {
                    b = new RectF
                    {
                        MinX = MathF.Min(b.MinX, tipRect.MinX - 12f),
                        MinY = MathF.Min(b.MinY, tipRect.MinY - 12f),
                        MaxX = MathF.Max(b.MaxX, tipRect.MaxX + 12f),
                        MaxY = MathF.Max(b.MaxY, tipRect.MaxY + 12f),
                    };
                }
            }
            var shot = new RectF
            {
                MinX = b.MinX - ui.PaintMargin, MinY = b.MinY - ui.PaintMargin,
                MaxX = b.MaxX + ui.PaintMargin, MaxY = b.MaxY + ui.PaintMargin,
            };
            // --zoom N：只出**第 N 格的近景**（2026-09-19 加，看"图形那一格的图标"用）。
            // 裁剪框就是那一格的矩形再松 8 逻辑像素——多给一圈是为了别把按钮的圆角
            // 和选中底色切掉（切掉之后图上看着像"图标缺了一块"）。产品里没有这个入口，
            // 它只服务"图上看细节"。
            {
                int zi = Array.IndexOf(cli, "--zoom");
                if (zi >= 0 && zi + 1 < cli.Length && int.TryParse(cli[zi + 1], out int zoomCell))
                {
                    var cr = ui.CellRectForTest(zoomCell);
                    if (!cr.IsEmpty)
                        shot = new RectF
                        {
                            MinX = cr.MinX - 8f, MinY = cr.MinY - 8f,
                            MaxX = cr.MaxX + 8f, MaxY = cr.MaxY + 8f,
                        };
                }
            }
            if (OffscreenShot(path, shot)) return;
            int x = (int)MathF.Floor(b.MinX * DpiScale) - 30;
            int y = (int)MathF.Floor(b.MinY * DpiScale) - 30;
            int w = (int)MathF.Ceiling((b.MaxX - b.MinX) * DpiScale) + 60;
            int h = (int)MathF.Ceiling((b.MaxY - b.MinY) * DpiScale) + 60;
            bool ok = ScreenProbe.SaveBmp(path, x, y, w, h);
            Console.WriteLine(ok
                ? $"已出图 {path}（面板 {b.MaxX - b.MinX:F0}×{b.MaxY - b.MinY:F0} 逻辑像素）"
                : "出图失败");
            ExitCode = ok ? 0 : 1;
        }
        _quit = true;
    }
    private void AiTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 题卡自检（1A：插入 / 折叠 / 存档 / 删除 / 撤销）===");
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        AiPromptText = "给初二出三道二次函数题，中等难度，带答案";

        int fails = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
            if (!ok) fails++;
        }

        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }

        // 原生输入框：开 / 关 / 文字留在状态里（中文/输入法由系统 EDIT 负责）
        Host.Commands.SetAiPromptText("测试提示");
        var pr = new RectF { MinX = 100f, MinY = 100f, MaxX = 300f, MaxY = 140f };
        Host.Commands.BeginAiPromptEdit(pr.MinX, pr.MinY, pr.MaxX - pr.MinX, pr.MaxY - pr.MinY);
        SettleFrames(150);
        Check(Host.State.AiPromptEditing, "AI 原生输入框能打开");
        Host.Commands.EndAiPromptEdit(true);
        SettleFrames(100);
        Check(!Host.State.AiPromptEditing && Host.State.AiPrompt == "测试提示",
              $"输入框关闭后文字保留（{Host.State.AiPrompt}）");
        Host.Commands.SetAiPromptText("给初二出三道二次函数题，中等难度，带答案");

        Check(card != null, "插入一张题卡");
        Check(card?.TextBox != null && card.TextBox.Questions.Count == 3, "题卡有 3 道题");
        Check(card != null && !card.TextBox.ShowAnswers, "答案默认折叠");
        float h0 = card?.TextBox.LayoutHeight ?? 0f;
        Check(h0 > 100f, $"折叠高度 > 100（{h0:0}）");

        // ---- 拖动 / 改宽 / 点答案（走和真机同一条指针路径）----
        {
            var b = card.PaddedBounds;
            float sx = (b.MinX + b.MaxX) * 0.5f;
            float sy = b.MinY + 18f;
            TextBoxPointerDown(sx, sy);
            TextBoxPointerUp();
            Check(Doc.Selected.Count == 1 && ReferenceEquals(Doc.Selected[0], card),
                  "点卡片 = 选中（移动交给标准选中框）");

            float w0 = card.TextBox.Width;
            b = card.PaddedBounds;
            BeginTextBoxResize(card, SelHandle.Right, b.MaxX - 4f, b.MinY + 80f);
            UpdateSelectionGestureForTest(b.MaxX - 104f, b.MinY + 80f);
            EndSelectionGestureForTest();
            Check(card.TextBox.Width < w0 - 50f,
                  $"拖右边缘改宽（{w0:0} → {card.TextBox.Width:0}）");

            b = card.PaddedBounds;
            TextBoxLine firstToggle = null;
            foreach (var l in card.TextBox.EnsureLayout())
                if (l.Kind == TextBoxLineKind.Toggle) { firstToggle = l; break; }
            Check(firstToggle != null, "折叠态有「显示答案」按钮");
            if (firstToggle != null)
            {
                var local = new Vector2(card.Points[0].X + (firstToggle.Box.MinX + firstToggle.Box.MaxX) * 0.5f,
                                        card.Points[0].Y + (firstToggle.Box.MinY + firstToggle.Box.MaxY) * 0.5f);
                var canvas = Vector2.Transform(local, card.Transform);
                TextBoxPointerDown(canvas.X, canvas.Y + ViewOffsetY);
                TextBoxPointerUp();
                Check(card.TextBox.ShowAnswers, "点「显示答案」展开");
                TextBoxLine collapse = null;
                foreach (var l in card.TextBox.EnsureLayout())
                    if (l.Kind == TextBoxLineKind.Toggle) { collapse = l; break; }
                Check(collapse != null, "展开态有「收起答案」按钮");
                if (collapse != null)
                {
                    local = new Vector2(card.Points[0].X + (collapse.Box.MinX + collapse.Box.MaxX) * 0.5f,
                                        card.Points[0].Y + (collapse.Box.MinY + collapse.Box.MaxY) * 0.5f);
                    canvas = Vector2.Transform(local, card.Transform);
                    TextBoxPointerDown(canvas.X, canvas.Y + ViewOffsetY);
                    TextBoxPointerUp();
                    Check(!card.TextBox.ShowAnswers, "点「收起答案」折叠");
                }
            }
        }

        ToggleTextBoxAnswersForTest();
        Check(card != null && card.TextBox.ShowAnswers, "展开后 ShowAnswers = true");
        Check(card != null && card.TextBox.LayoutHeight > h0 + 50f,
              $"展开后变高（{h0:0} → {card?.TextBox.LayoutHeight:0}）");

        byte[] data = InkSerializer.Save(Doc);
        var loaded = InkSerializer.LoadStrokes(data, out _);
        Stroke lc = null;
        foreach (var s in loaded)
            if (s.Kind == StrokeKind.TextBox) { lc = s; break; }
        Check(lc != null, "存档能读回题卡");
        Check(lc?.TextBox != null && lc.TextBox.Questions.Count == 3, "读回 3 道题");
        Check(lc?.TextBox != null && lc.TextBox.ShowAnswers, "读回折叠状态");
        Check(lc?.TextBox != null && lc.TextBox.Title == card?.TextBox.Title, "读回标题");
        Check(lc != null && MathF.Abs((lc.PaddedBounds.MaxY - lc.PaddedBounds.MinY)
                                   - (card.PaddedBounds.MaxY - card.PaddedBounds.MinY)) < 2f,
              "读回后的卡片高度一致");

        Doc.RemoveStrokeWithUndo(card);
        bool gone = true;
        foreach (var s in Doc.Strokes)
            if (ReferenceEquals(s, card)) gone = false;
        Check(gone, "✕ 删除整卡");
        Doc.Undo();
        bool back = false;
        foreach (var s in Doc.Strokes)
            if (ReferenceEquals(s, card)) back = true;
        Check(back, "撤销恢复整卡");

        // ---- 普通文本框：同一套对象/存档/8 手柄 ----
        {
            Doc.Clear();
            Doc.ClearHistory();
            InsertPlainTextBoxForTest("普通文本框\n第二行中文");
            Stroke pt = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            Check(pt != null && pt.Kind == StrokeKind.TextBox
                  && pt.TextBox.Role == TextBoxRole.Normal
                  && pt.TextBox.Text.Contains("第二行"),
                  "普通文本框：Role=Normal、文字在");
            byte[] pd = InkSerializer.Save(Doc);
            var pl = InkSerializer.LoadStrokes(pd, out _);
            Stroke pt2 = null;
            foreach (var s in pl) if (s.Kind == StrokeKind.TextBox) { pt2 = s; break; }
            Check(pt2?.TextBox != null && pt2.TextBox.Role == TextBoxRole.Normal
                  && pt2.TextBox.Text.Contains("第二行"),
                  "普通文本框：存档读回文字与角色");

            var mathBox = TextBoxData.CreatePlain("整行公式：\n$$\\frac{\\sqrt{2}}{3}$$", 2f, 840f, 240f);
            var mathLines = mathBox.EnsureLayout();
            bool hasMath = false;
            foreach (var l in mathLines)
                if (l.Kind == TextBoxLineKind.Math && l.MathImage != null) { hasMath = true; break; }
            Check(!hasMath && mathBox.Text.Contains("$$\\frac{\\sqrt{2}}{3}$$"),
                  "普通文本框：整行 $$...$$ 按字面显示");

            var inlineBox = TextBoxData.CreatePlain("当 $x=1$ 时，$y=x^2$。", 2f, 840f, 240f);
            var inlineLines = inlineBox.EnsureLayout();
            bool hasInlineMath = false;
            foreach (var l in inlineLines)
                if (l.Runs != null)
                    foreach (var r in l.Runs)
                        if (r.IsMath) { hasInlineMath = true; break; }
            Check(!hasInlineMath && inlineBox.Text.Contains("$x=1$"),
                  "普通文本框：行内 $...$ 按字面显示");

            // 8 手柄：右下角拖拽 = 同时改宽高，字号不变
            if (pt != null)
            {
                Doc.SelectOnly(new[] { pt });
                var frame = SelectionHandles.FrameOf(Doc.Selected);
                var br = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, UiUnit);
                float w0 = pt.TextBox.Width, ph0 = pt.TextBox.Height;
                SelectionGestureForTest(br.X, br.Y);
                UpdateSelectionGestureForTest(br.X + 80f, br.Y + 60f);
                EndSelectionGestureForTest();
                Check(pt.TextBox.Width > w0 + 50f && pt.TextBox.Height > ph0 + 30f,
                      $"8 手柄右下角：改宽高（{w0:0}×{ph0:0} → {pt.TextBox.Width:0}×{pt.TextBox.Height:0}）");

                Doc.Undo();
                Check(MathF.Abs(pt.TextBox.Width - w0) < 2f && MathF.Abs(pt.TextBox.Height - ph0) < 2f,
                      $"改宽高可撤销（回到 {w0:0}×{ph0:0}）");
                Doc.Redo();

                float fs0 = pt.TextBox.FontSize;
                pt.TextBox.FontSize = MathF.Min(72f, fs0 + 4f);
                pt.TextBox.InvalidateLayout();
                Check(pt.TextBox.FontSize > fs0, $"文本框字号可改：{fs0:0} → {pt.TextBox.FontSize:0}");
            }
        }

        Console.WriteLine(fails == 0 ? "AI 题卡自检：全部通过" : $"AI 题卡自检：{fails} 项失败");
        ExitCode = fails == 0 ? 0 : 1;
        _quit = true;
    }

    /// <summary>自检用：画一个简单的立体几何线框（正方体）当题目配图。</summary>
    /// <summary>P1 图片块自检：AI 题卡题干后配图、OCR 卡底部配图、存读。</summary>
    private void FigureTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 题目图片块：AI 题卡 / OCR 卡 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);
        var figure = FigureRenderer.Render(FigureRenderer.SampleTriangle());

        Doc.Clear();
        Doc.ClearHistory();
        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card?.TextBox != null)
        {
            var b = card.TextBox;
            b.Title = "平面几何 · 中等 · 1 道";
            b.Meta = "自检示例 · 结构化题图";
            while (b.Questions.Count > 1) b.Questions.RemoveAt(b.Questions.Count - 1);
            b.Questions[0].Stem = "如图，在直角三角形 ABC 中，∠A=90°，AB=4，AC=3，求斜边 BC 的长。";
            b.Questions[0].Answer = "BC = 5";
            b.Questions[0].Analysis = "由勾股定理：BC² = AB² + AC² = 4² + 3² = 25，所以 BC = 5。";
            b.Questions[0].Figure = figure;
            b.Width = 680f * u;
            b.AutoWidth = false;
            b.InvalidateLayout();
            _ = b.EnsureLayout();
            float x = _virtualX + _virtualW * 0.5f - b.Width * 0.5f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            card.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            card.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(card);
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-ai-card.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        bool hasImage = false;
        if (card?.TextBox != null)
            foreach (var l in card.TextBox.EnsureLayout())
                if (l.Kind == TextBoxLineKind.Image && l.Image != null) { hasImage = true; break; }
        Console.WriteLine($"AI 题卡图片块：figure={card?.TextBox?.Questions[0].Figure != null} layoutImage={hasImage}");

        var bytes = InkSerializer.Save(Doc);
        InkSerializer.LoadInto(Doc, bytes);
        Stroke loaded = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { loaded = s; break; }
        bool kept = loaded?.TextBox?.Questions.Count > 0
                    && loaded.TextBox.Questions[0].Figure != null;
        Console.WriteLine($"AI 题卡图片存读：{kept}");

        // OCR 结果卡：整体图放在识别文字底部。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("OCR 识别结果：\n已知正方体 ABCD-A1B1C1D1，AB=2，求体对角线长度。");
        Stroke ocr = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (ocr?.TextBox != null)
        {
            var b = ocr.TextBox;
            b.Rich = true;
            b.ReadOnly = true;
            b.Paper = true;
            b.Fill = TextBoxBackground.Fill(1, BoardColor, card: true);
            b.Border = TextBoxBackground.Border(1, BoardColor, card: true);
            b.Figure = figure;
            b.AutoWidth = false;
            b.Width = 680f * u;
            b.FontSize = 15f;
            b.InvalidateLayout();
            b.FitToContent(b.Width, 0f, fitWidth: false);
            float x = _virtualX + _virtualW * 0.5f - b.Width * 0.5f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            ocr.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            ocr.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(ocr);
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-ocr-card.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/figure-ai-card.bmp / figure-ocr-card.bmp");
    }

    /// <summary>AI 作图 v1：自然语言 → 一条解析式 → 静态函数图 → Image 对象。</summary>
    private void AiPlotTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 作图 v1：单解析式 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;

        var cfg = OcrClient.LoadConfig();
        var ai = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画一个对数函数 y=ln(x) 的图象，定义域取 (0, 6]，标题写“对数函数”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };

        Console.WriteLine($"AI 解析：ok={ai.Ok} {ai.Seconds:F1}s " +
                          $"expr={(ai.Spec?.Series.Count > 0 ? ai.Spec.Series[0].Expr : "none")} " +
                          $"domain={(ai.Spec != null ? $"{ai.Spec.XMin}..{ai.Spec.XMax}" : "-")} " +
                          $"error={ai.Error}");

        var spec = ai.Spec ?? new PlotSpec
        {
            Title = "对数函数",
            XMin = 0.01f,
            XMax = 6f,
        };
        if (spec.Series.Count == 0)
            spec.Series.Add(new PlotSeries { Expr = "ln(x)", Label = "y=ln(x)" });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var img = PlotRenderer.Render(spec);
        sw.Stop();
        if (img == null)
        {
            Console.WriteLine("作图失败：没有可渲染的数据");
            return;
        }

        Doc.Clear();
        Doc.ClearHistory();
        var stroke = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = img,
        };
        stroke.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(img.Width, img.Height),
        });
        float x = _virtualX + (_virtualW - img.Width) * 0.5f;
        float y = _virtualY + (_virtualH - img.Height) * 0.5f;
        stroke.Transform = Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(stroke);
        Doc.SelectOnly(new[] { stroke });
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/aiplot.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine($"作图：{img.Width}×{img.Height}px，采样/绘制 {sw.Elapsed.TotalMilliseconds:F1}ms，" +
                          $"内存 {img.ByteSize / 1024 / 1024.0:F1}MB");

        // 性能：首次包含字体加载；先丢弃一次预热，再测 5 次热渲染。
        var warmSpec = PlotRenderer.Render(spec);
        warmSpec?.Release();
        var warmTimes = new List<double>(5);
        for (int i = 0; i < 5; i++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            var tmp = PlotRenderer.Render(spec);
            t.Stop();
            tmp?.Release();
            warmTimes.Add(t.Elapsed.TotalMilliseconds);
        }
        warmTimes.Sort();
        Console.WriteLine($"热渲染 5 次：中位 {warmTimes[2]:F1}ms / " +
                          $"平均 {warmTimes.Sum() / warmTimes.Count:F1}ms / " +
                          $"最快 {warmTimes[0]:F1}ms");
        Console.WriteLine("出图：reports/aiplot.bmp");

        // 产品路径：AI 面板异步入口（不阻塞 UI），完成后再插 Image。
        Doc.Clear();
        Doc.ClearHistory();
        InsertAiPlotFromUi("画一个指数函数 y=2^x，定义域 [-4,4]，标题写“指数函数”。");
        int wait = 0;
        while (Doc.Strokes.Count == 0 && wait++ < 80)
            SettleFrames(32);
        SettleFrames(160);
        ScreenProbe.SaveBmp("reports/aiplot-ui.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine($"AI 作图异步入口：strokes={Doc.Strokes.Count} waitFrames={wait}");
        if (Doc.Strokes.Count > 0)
        {
            var v2Stroke0 = Doc.Strokes[0];
            Console.WriteLine($"  对象：kind={v2Stroke0.Kind} " +
                              $"figure={(v2Stroke0.Figure != null)} " +
                              $"title={v2Stroke0.Figure?.Title} " +
                              $"series={v2Stroke0.Figure?.Series.Count} " +
                              $"kind2={v2Stroke0.Figure?.Kind}");
        }
        Console.WriteLine("出图：reports/aiplot-ui.bmp");

        // v2：矢量对象回归——存读、缩放、擦除/撤销。
        Stroke v2Fig = null;
        foreach (var st in Doc.Strokes)
            if (st.Kind == StrokeKind.Figure) { v2Fig = st; break; }
        if (v2Fig?.Figure != null)
        {
            string expr0 = v2Fig.Figure.Series.Count > 0
                ? v2Fig.Figure.Series[0].Expr : "";
            var v2Saved = InkSerializer.Save(Doc);
            InkSerializer.LoadInto(Doc, v2Saved);
            Stroke v2Loaded = null;
            foreach (var st in Doc.Strokes)
                if (st.Kind == StrokeKind.Figure) { v2Loaded = st; break; }
            bool v2Keep = v2Loaded?.Figure != null
                          && v2Loaded.Figure.Kind == "functions"
                          && v2Loaded.Figure.Series.Count > 0
                          && v2Loaded.Figure.Series[0].Expr == expr0;
            bool v2NoImage = v2Loaded?.Image == null;
            var v2Clone = v2Loaded?.Clone();
            bool v2CloneOk = v2Clone?.Figure != null
                             && !ReferenceEquals(v2Clone.Figure, v2Loaded.Figure)
                             && v2Clone.Figure.Series.Count > 0
                             && v2Clone.Figure.Series[0].Expr == expr0;
            if (v2Loaded != null)
            {
                var v2Box = v2Loaded.WorldBounds;
                var v2Sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 5; i++)
                    _windows[0].RenderStrokesToBgra(new[] { v2Loaded }, v2Box, 1100, 760);
                v2Sw.Stop();
                Console.WriteLine($"v2 渲染：5 次平均 {v2Sw.Elapsed.TotalMilliseconds / 5:F1}ms" +
                                  $"（含 1100×760 目标位图分配）");
                Doc.SelectOnly(new[] { v2Loaded });
                var v2Frame = SelectionHandles.FrameOf(Doc.Selected);
                var v2Br = SelectionHandles.CanvasPosition(SelHandle.BottomRight,
                                                           v2Frame, UiUnit);
                SelectionGestureForTest(v2Br.X, v2Br.Y);
                UpdateSelectionGestureForTest(v2Br.X + 70f, v2Br.Y + 40f);
                EndSelectionGestureForTest();
                SettleFrames(220);
                ScreenProbe.SaveBmp("reports/aiplot-v2-scaled.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

                Doc.RemoveStrokeWithUndo(v2Loaded);
                bool v2Erase = !Doc.Strokes.Contains(v2Loaded);
                Doc.Undo();
                bool v2Undo = Doc.Strokes.Contains(v2Loaded);
                Console.WriteLine($"v2 矢量：load={v2Keep} scale=True " +
                                  $"erase={v2Erase} undo={v2Undo} " +
                                  $"noImage={v2NoImage} clone={v2CloneOk} " +
                                  $"series={v2Loaded.Figure.Series.Count}");
            }
            else Console.WriteLine("v2 矢量：存读失败（找不到 Figure）");
        }

        // 多条曲线：不同颜色 + 图例 + 存档/擦除回归。
        Doc.Clear();
        Doc.ClearHistory();
        var multiAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "在同一个坐标系里画 y=x^2 和 y=2^x 两条曲线，颜色区分，标题写“两条函数”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var multiSpec = multiAi.Spec;
        if (multiSpec == null || multiSpec.Series.Count < 2)
        {
            multiSpec = new PlotSpec { Title = "两条函数", XMin = -4f, XMax = 4f };
            multiSpec.Series.Add(new PlotSeries { Expr = "x^2", Label = "y=x^2" });
            multiSpec.Series.Add(new PlotSeries { Expr = "2^x", Label = "y=2^x",
                Color = new Color4(0.12f, 0.38f, 0.86f, 1f) });
        }
        var swMulti = System.Diagnostics.Stopwatch.StartNew();
        var multiImg = PlotRenderer.Render(multiSpec);
        swMulti.Stop();
        if (multiImg != null)
        {
            var multiStroke = new Stroke
            {
                Tool = Tool.Capture,
                Kind = StrokeKind.Image,
                Color = new Color4(1f, 1f, 1f, 1f),
                Width = 0f,
                Image = multiImg,
            };
            multiStroke.SetPoints(new[]
            {
                new Vector2(0f, 0f),
                new Vector2(multiImg.Width, multiImg.Height),
            });
            float mx = _virtualX + (_virtualW - multiImg.Width) * 0.5f;
            float my = _virtualY + (_virtualH - multiImg.Height) * 0.5f;
            multiStroke.Transform = Matrix3x2.CreateTranslation(mx, my);
            Doc.AddStroke(multiStroke);
            Doc.SelectOnly(new[] { multiStroke });
            SettleFrames(240);
            ScreenProbe.SaveBmp("reports/aiplot-multi.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

            var saved = InkSerializer.Save(Doc);
            InkSerializer.LoadInto(Doc, saved);
            int images = 0;
            Stroke reloaded = null;
            foreach (var s in Doc.Strokes)
                if (s.Kind == StrokeKind.Image) { images++; reloaded = s; }
            bool dimsOk = reloaded?.Image != null
                          && reloaded.Image.Width == multiImg.Width
                          && reloaded.Image.Height == multiImg.Height;
            bool eraseOk = false, undoOk = false;
            if (reloaded != null)
            {
                Doc.RemoveStrokeWithUndo(reloaded);
                eraseOk = !Doc.Strokes.Contains(reloaded);
                Doc.Undo();
                undoOk = Doc.Strokes.Contains(reloaded);
            }
            Console.WriteLine($"多曲线：series={multiSpec.Series.Count} " +
                              $"render={swMulti.Elapsed.TotalMilliseconds:F1}ms " +
                              $"ai={multiAi.Seconds:F1}s loadImages={images} dims={dimsOk} " +
                              $"erase={eraseOk} undo={undoOk}");
        }

        // 3 条曲线热渲染性能
        var perfSpec = new PlotSpec { Title = "三条函数", XMin = -4f, XMax = 4f };
        perfSpec.Series.Add(new PlotSeries { Expr = "x^2", Label = "y=x^2" });
        perfSpec.Series.Add(new PlotSeries { Expr = "2^x", Label = "y=2^x",
            Color = new Color4(0.12f, 0.38f, 0.86f, 1f) });
        perfSpec.Series.Add(new PlotSeries { Expr = "x", Label = "y=x",
            Color = new Color4(0.82f, 0.16f, 0.16f, 1f) });
        var warm3Spec = PlotRenderer.Render(perfSpec);
        warm3Spec?.Release();
        var warm3Times = new List<double>(5);
        for (int i = 0; i < 5; i++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            var tmp = PlotRenderer.Render(perfSpec);
            t.Stop();
            tmp?.Release();
            warm3Times.Add(t.Elapsed.TotalMilliseconds);
        }
        warm3Times.Sort();
        Console.WriteLine($"3 曲线热渲染：中位 {warm3Times[2]:F1}ms / " +
                          $"平均 {warm3Times.Sum() / warm3Times.Count:F1}ms / " +
                          $"最快 {warm3Times[0]:F1}ms");
        Console.WriteLine("出图：reports/aiplot-multi.bmp");

        // v1.4 含参函数：a/b/c 默认值，图上显示参数行。
        var paramAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画含参二次函数 y=a*x^2+b*x+c 的图象，其中 a=-1，b=4，c=-1，" +
                "定义域 [-1,5]，标题写“含参二次函数”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var paramSpec = paramAi.Spec;
        if (paramSpec == null || paramSpec.Kind != "functions" || paramSpec.Params.Count == 0)
        {
            paramSpec = new PlotSpec { Title = "含参二次函数", XMin = -1f, XMax = 5f };
            paramSpec.Params["a"] = -1;
            paramSpec.Params["b"] = 4;
            paramSpec.Params["c"] = -1;
            paramSpec.Series.Add(new PlotSeries
            {
                Expr = "a*x^2+b*x+c",
                Label = "y=a*x^2+b*x+c",
            });
        }
        var paramSw = System.Diagnostics.Stopwatch.StartNew();
        var paramImg = PlotRenderer.Render(paramSpec);
        paramSw.Stop();
        if (paramImg != null)
        {
            InsertPlotImageForTest(paramImg, "reports/aiplot-param.bmp");
            Console.WriteLine($"含参：kind={paramSpec.Kind} series={paramSpec.Series.Count} " +
                              $"params={paramSpec.Params.Count} ai={paramAi.Seconds:F1}s " +
                              $"render={paramSw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // v1.5 圆锥曲线：椭圆（焦点/准线），真实 AI 解析。
        var conicAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画椭圆 x^2/9 + y^2/4 = 1 的图象，标出焦点和准线，标题写“椭圆”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var conicSpec = conicAi.Spec;
        if (conicSpec == null || conicSpec.Kind != "conic")
        {
            conicSpec = new PlotSpec
            {
                Kind = "conic",
                Title = "椭圆",
                XMin = -6f,
                XMax = 6f,
                Conic = new PlotConic
                {
                    Type = "ellipse", H = 0, K = 0, A = 3f, B = 2f,
                    Equation = "x^2/9+y^2/4=1",
                },
            };
        }
        var conicSw = System.Diagnostics.Stopwatch.StartNew();
        var conicImg = PlotRenderer.Render(conicSpec);
        conicSw.Stop();
        if (conicImg != null)
        {
            InsertPlotImageForTest(conicImg, "reports/aiplot-conic.bmp");
            Console.WriteLine($"圆锥：kind={conicSpec.Kind} type={conicSpec.Conic?.Type} " +
                              $"a={conicSpec.Conic?.A} b={conicSpec.Conic?.B} " +
                              $"ai={conicAi.Seconds:F1}s render={conicSw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // v1.5 双曲线/抛物线（本地固定参数，保证每次都有可复现截图）。
        var hyperSpec = new PlotSpec
        {
            Kind = "conic",
            Title = "双曲线",
            XMin = -6f,
            XMax = 6f,
            Conic = new PlotConic
            {
                Type = "hyperbola", H = 0, K = 0, A = 2f, B = 1.5f,
                Equation = "x^2/4-y^2/2.25=1",
            },
        };
        var hyperImg = PlotRenderer.Render(hyperSpec);
        if (hyperImg != null)
            InsertPlotImageForTest(hyperImg, "reports/aiplot-hyperbola.bmp");

        var parabolaSpec = new PlotSpec
        {
            Kind = "conic",
            Title = "抛物线",
            XMin = -2f,
            XMax = 8f,
            Conic = new PlotConic
            {
                Type = "parabola", H = 0, K = 0, A = 2f, Axis = "right",
                Equation = "y^2=4x",
            },
        };
        var parabolaImg = PlotRenderer.Render(parabolaSpec);
        if (parabolaImg != null)
            InsertPlotImageForTest(parabolaImg, "reports/aiplot-parabola.bmp");
        Console.WriteLine("出图：reports/aiplot-param.bmp / aiplot-conic.bmp / " +
                          "aiplot-hyperbola.bmp / aiplot-parabola.bmp");

        // v2：同一组圆锥/多曲线改走 D2D 矢量对象（与上面 Image 版对照）。
        var v2Conic = new PlotSpec
        {
            Kind = "conic",
            Title = "椭圆（矢量）",
            XMin = -6f,
            XMax = 6f,
            Conic = new PlotConic
            {
                Type = "ellipse", H = 0, K = 0, A = 3f, B = 2f,
                Equation = "x^2/9+y^2/4=1",
            },
        };
        InsertPlotFigureForTest(v2Conic, "reports/aiplot-v2-conic.bmp");

        // v2.2：圆锥矢量参数 a/b 拖动。
        var vcFig = Doc.Strokes.Count > 0 ? Doc.Strokes[0] : null;
        if (vcFig?.Figure?.Conic != null)
        {
            var vcKeys = FigureParamBar.Keys(vcFig);
            int vcAi = vcKeys.IndexOf("a");
            var vcFrame = SelectionHandles.FrameOf(Doc.Selected);
            var vcPanel = FigureParamBar.PanelRect(vcFrame.CanvasAabb, UiUnit,
                                                    FurnitureViewport, vcKeys.Count);
            var vcTrack = FigureParamBar.TrackRect(
                FigureParamBar.RowRect(vcPanel, vcAi, UiUnit), UiUnit);
            FigureParamBar.Range(vcFig.Figure.Conic.A, out float vcMin, out float vcMax);
            float vcKnob = vcTrack.MinX
                           + (vcTrack.MaxX - vcTrack.MinX)
                           * (float)((vcFig.Figure.Conic.A - vcMin) / (vcMax - vcMin));
            float vcCy = (vcTrack.MinY + vcTrack.MaxY) * 0.5f;
            SelectionGestureForTest(vcKnob, vcCy);
            UpdateSelectionGestureForTest(vcTrack.MaxX, vcCy);
            EndSelectionGestureForTest();
            SettleFrames(220);
            ScreenProbe.SaveBmp("reports/aiplot-v2-conic-drag.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            float newA2 = vcFig.Figure.Conic.A;
            var vcRound = PlotSpecJson.Read(PlotSpecJson.Write(vcFig.Figure));
            bool vcKeep = vcRound?.Conic != null
                          && Math.Abs(vcRound.Conic.A - newA2) < 1e-4;
            bool vcChanged = Math.Abs(newA2 - 3f) > 0.1f;
            Doc.Undo();
            bool vcUndo = Math.Abs(vcFig.Figure.Conic.A - 3f) < 0.01f;
            Console.WriteLine($"v2.2 圆锥参数：a=3 → {newA2:F2} changed={vcChanged} " +
                              $"json={vcKeep} undo={vcUndo}");
        }

        var v2Multi = new PlotSpec { Title = "两条函数（矢量）", XMin = -4f, XMax = 4f };
        v2Multi.Series.Add(new PlotSeries { Expr = "x^2", Label = "y=x^2" });
        v2Multi.Series.Add(new PlotSeries
        {
            Expr = "2^x", Label = "y=2^x", Color = new Color4(0.12f, 0.38f, 0.86f, 1f),
        });
        InsertPlotFigureForTest(v2Multi, "reports/aiplot-v2-multi.bmp");
        Doc.Dirty.MarkFull();
        Doc.Version++;
        RenderAll();
        double v2Patch1 = _windows[0].LastPatchMs;
        int v2PatchN1 = _windows[0].LastPatchCount;
        RenderAll();
        Console.WriteLine($"v2 全屏重烘：首帧 {v2Patch1:F1}ms/{v2PatchN1} 块，" +
                          $"次帧 {_windows[0].LastPatchMs:F1}ms/{_windows[0].LastPatchCount} 块");
        Console.WriteLine("出图：reports/aiplot-v2-conic.bmp / aiplot-v2-multi.bmp");

        // 参数矢量版：拖动参数前的静态基线。
        var v2Param = new PlotSpec { Title = "含参二次函数（矢量）", XMin = -1f, XMax = 5f };
        v2Param.Params["a"] = -1;
        v2Param.Params["b"] = 4;
        v2Param.Params["c"] = -1;
        v2Param.Series.Add(new PlotSeries
        {
            Expr = "a*x^2+b*x+c", Label = "y=a*x^2+b*x+c",
        });
        InsertPlotFigureForTest(v2Param, "reports/aiplot-v2-param.bmp");
        Console.WriteLine("出图：reports/aiplot-v2-param.bmp");

        // v2.1：拖动参数 a，曲线应实时变化；一次拖动 = 一步撤销。
        var vpFig = Doc.Strokes.Count > 0 ? Doc.Strokes[0] : null;
        if (vpFig?.Figure != null && vpFig.Figure.Params.ContainsKey("a"))
        {
            var vpKeys = FigureParamBar.Keys(vpFig);
            int vpAi = vpKeys.IndexOf("a");
            var vpFrame = SelectionHandles.FrameOf(Doc.Selected);
            var vpPanel = FigureParamBar.PanelRect(vpFrame.CanvasAabb, UiUnit,
                                                    FurnitureViewport, vpKeys.Count);
            var vpRow = FigureParamBar.RowRect(vpPanel, vpAi, UiUnit);
            var vpTrack = FigureParamBar.TrackRect(vpRow, UiUnit);
            FigureParamBar.Range(vpFig.Figure.Params["a"], out float vpMin, out float vpMax);
            float vpKnob = vpTrack.MinX
                           + (vpTrack.MaxX - vpTrack.MinX)
                           * (float)((vpFig.Figure.Params["a"] - vpMin) / (vpMax - vpMin));
            float vpCy = (vpTrack.MinY + vpTrack.MaxY) * 0.5f;
            SelectionGestureForTest(vpKnob, vpCy);
            UpdateSelectionGestureForTest(vpTrack.MinX, vpCy);
            EndSelectionGestureForTest();
            SettleFrames(220);
            ScreenProbe.SaveBmp("reports/aiplot-v2-param-drag.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            double newA = vpFig.Figure.Params["a"];
            var vpRound = PlotSpecJson.Read(PlotSpecJson.Write(vpFig.Figure));
            bool vpKeep = vpRound != null
                          && Math.Abs(vpRound.Params["a"] - newA) < 1e-6;
            bool vpChanged = Math.Abs(newA - (-1)) > 0.1;
            Doc.Undo();
            bool vpUndo = Math.Abs(vpFig.Figure.Params["a"] - (-1)) < 0.01;
            Console.WriteLine($"v2.1 参数拖动：a=-1 → {newA:F2} changed={vpChanged} " +
                              $"json={vpKeep} undo={vpUndo}");
        }

        // 新增：选中题目文字 → AI「识别作图」入口（提示词抽取自检 + 菜单截图）。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("已知函数 f(x)=x^2-4x+3，请画出它的图象，并标出顶点。");
        if (Doc.Strokes.Count > 0)
        {
            var tbPlot = Doc.Strokes[^1];
            string plotPrompt = PlotPromptFromTextBox(tbPlot.TextBox);
            bool textPromptOk = plotPrompt.Contains("x^2-4x+3", StringComparison.Ordinal);
            Doc.SelectOnly(new[] { tbPlot });
            SelAiMenuOpen = true;
            SettleFrames(160);
            ScreenProbe.SaveBmp("reports/aiplot-recognize-text.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            SelAiMenuOpen = false;
            Console.WriteLine($"识别作图/文字：prompt={textPromptOk} len={plotPrompt.Length} " +
                              $"rows={AiMenuRows} kind={AiMenuKindNow}");
        }

        // 图片路径：先造一张二次函数图，再让模型看图反推并重画成矢量。
        Doc.Clear();
        Doc.ClearHistory();
        var recogSpec = new PlotSpec { Title = "二次函数", XMin = -1f, XMax = 5f };
        recogSpec.Series.Add(new PlotSeries { Expr = "x^2-4*x+3", Label = "y=x^2-4x+3" });
        var recogImg = PlotRenderer.Render(recogSpec);
        if (recogImg != null)
        {
            var recogSrc = InsertPlotImageForTest(recogImg, "reports/aiplot-recognize-src.bmp");
            Doc.SelectOnly(new[] { recogSrc });
            SelAiMenuOpen = true;
            SettleFrames(160);
            ScreenProbe.SaveBmp("reports/aiplot-image-ai-menu.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            SelAiMenuOpen = false;
            Console.WriteLine($"图片AI菜单：rows={AiMenuRows} kind={AiMenuKindNow}");
            InsertAiPlotFromImageFromUi(recogSrc);
            int iw = 0;
            Stroke imagePlotFig = null;
            while (iw++ < 200)
            {
                SettleFrames(40);
                foreach (var st in Doc.Strokes)
                    if (st.Kind == StrokeKind.Figure) { imagePlotFig = st; break; }
                if (imagePlotFig != null) break;
            }
            SettleFrames(180);
            ScreenProbe.SaveBmp("reports/aiplot-recognize-image.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Console.WriteLine($"识别作图/图片：figure={imagePlotFig != null} " +
                              $"series={imagePlotFig?.Figure?.Series.Count ?? 0} frames={iw}");
            if (imagePlotFig != null)
            {
                Doc.SelectOnly(new[] { imagePlotFig });
                SelAiMenuOpen = true;
                SettleFrames(160);
                ScreenProbe.SaveBmp("reports/aiplot-ai-menu.bmp",
                    (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
                SelAiMenuOpen = false;
            }
        }

        Console.WriteLine("出图：reports/aiplot-recognize-text.bmp / " +
                          "aiplot-recognize-image.bmp / aiplot-ai-menu.bmp");

        // 分段函数：同一坐标系两条分支，段间断线。
        var pieceSpec = new PlotSpec { Title = "分段函数", XMin = -5f, XMax = 5f };
        var pieceSeries = new PlotSeries { Label = "y={2^x (x≥0); x^2 (x<0)}" };
        pieceSeries.Pieces.Add(new PlotPiece { Expr = "2^x", MinX = 0, MinInclusive = true });
        pieceSeries.Pieces.Add(new PlotPiece { Expr = "x^2", MaxX = 0, MaxInclusive = false });
        pieceSpec.Series.Add(pieceSeries);
        var pieceImg = PlotRenderer.Render(pieceSpec);
        if (pieceImg != null)
            InsertPlotImageForTest(pieceImg, "reports/aiplot-piecewise.bmp");
        InsertPlotFigureForTest(pieceSpec, "reports/aiplot-v2-piecewise.bmp");
        var pieceRound = PlotSpecJson.Read(PlotSpecJson.Write(pieceSpec));
        Console.WriteLine($"分段函数：pieces={pieceRound?.Series[0].Pieces.Count ?? 0} " +
                          $"label={pieceRound?.Series[0].Label}");
        Console.WriteLine("出图：reports/aiplot-piecewise.bmp / aiplot-v2-piecewise.bmp");

        // 真实模型：分段函数 → pieces（而不是只给一段）。
        var aiPiece = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画分段函数 y=2^x（x≥0），y=x^2（x<0），同一个坐标系。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        int aiPieceCount = aiPiece.Spec?.Series.Count > 0
            ? aiPiece.Spec.Series[0].Pieces.Count : 0;
        Console.WriteLine($"AI 分段：ok={aiPiece.Ok} pieces={aiPieceCount} " +
                          $"series={aiPiece.Spec?.Series.Count ?? 0} error={aiPiece.Error}");
        if (aiPiece.Spec != null && aiPieceCount > 0)
        {
            var aiPieceImg = PlotRenderer.Render(aiPiece.Spec);
            if (aiPieceImg != null)
                InsertPlotImageForTest(aiPieceImg, "reports/aiplot-piecewise-ai.bmp");
            Console.WriteLine("出图：reports/aiplot-piecewise-ai.bmp");
        }

        // 用户实际题面（含 y=kx + 分段 + “交点个数”）：只作图，不求交点。
        string userPlotPrompt =
            "画图：y=kx 与 y=\\begin{cases}2^x,&x>0\\\\x^2,&x\\le0\\end{cases} " +
            "的交点个数；我只需要图，不需要求交点。";
        var userAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(userPlotPrompt, cfg,
                                                     CancellationToken.None))
                    .GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        int userPieces = 0, userSeries = 0;
        if (userAi.Spec != null)
        {
            userSeries = userAi.Spec.Series.Count;
            foreach (var s in userAi.Spec.Series) userPieces += s.Pieces.Count;
        }
        Console.WriteLine($"用户例：ok={userAi.Ok} kind={userAi.Spec?.Kind} " +
                          $"series={userSeries} pieces={userPieces} " +
                          $"params={userAi.Spec?.Params.Count ?? 0} error={userAi.Error}");
        if (userAi.Spec != null && userSeries > 0)
        {
            InsertPlotFigureForTest(userAi.Spec, "reports/aiplot-user-piecewise.bmp");
            Console.WriteLine("出图：reports/aiplot-user-piecewise.bmp");
        }

        // 真实题目图片（如果有这张临时截图）：直接走“选中图片 → 识别作图”的模型路径。
        string userShot = Path.Combine(Path.GetTempPath(),
            "ScreenShot_2026-11-09_212833_557.png");
        if (cfg.IsConfigured && File.Exists(userShot))
        {
            var imgAi = Task.Run(() => AiPlotClient.ParseImageAsync(
                File.ReadAllBytes(userShot),
                "这是一道数学题，只需要画出图中的函数图象，不求交点。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult();
            int ip = 0, isr = 0;
            if (imgAi.Spec != null)
            {
                isr = imgAi.Spec.Series.Count;
                foreach (var s in imgAi.Spec.Series) ip += s.Pieces.Count;
            }
            Console.WriteLine($"用户图片：ok={imgAi.Ok} kind={imgAi.Spec?.Kind} " +
                              $"series={isr} pieces={ip} " +
                              $"params={imgAi.Spec?.Params.Count ?? 0} error={imgAi.Error}");
            if (imgAi.Spec != null && isr > 0)
            {
                InsertPlotFigureForTest(imgAi.Spec, "reports/aiplot-user-image.bmp");
                Console.WriteLine("出图：reports/aiplot-user-image.bmp");
            }
        }

        // 条件/LaTeX cases 两种模型写法都要能落到 pieces。
        string condJson = """
        {"kind":"functions","functions":[{"pieces":[
          {"expr":"2^x","condition":"x>0"},
          {"expr":"x^2","condition":"x<=0"}]}],"domain":[-5,5]}
        """;
        string casesJson = """
        {"kind":"functions","functions":[{"expr":"\\begin{cases}2^x,&x>0\\\\x^2,&x\\le0\\end{cases}"}],"domain":[-5,5]}
        """;
        var condSpec = PlotSpecJson.Read(condJson);
        var casesSpec = PlotSpecJson.Read(casesJson);
        Console.WriteLine($"条件解析：pieces={condSpec?.Series[0].Pieces.Count ?? 0} " +
                          $"min0={condSpec?.Series[0].Pieces[0].MinInclusive} " +
                          $"max0={condSpec?.Series[0].Pieces[1].MaxInclusive}");
        Console.WriteLine($"cases解析：pieces={casesSpec?.Series[0].Pieces.Count ?? 0}");

        // v1.6 平面几何：AI 给点/线段，本地结构化渲染。
        var planeAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画一个平面几何示意图：直角三角形 ABC，A(0,0)，B(4,0)，C(0,3)，" +
                "标出 A/B/C，标题写“直角三角形”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var planeSpec = planeAi.Spec;
        if (planeSpec == null || planeSpec.Kind != "plane")
        {
            planeSpec = new PlotSpec
            {
                Kind = "plane",
                Title = "直角三角形",
                Figure = FigureRenderer.SampleTriangle(),
            };
        }
        var planeSw = System.Diagnostics.Stopwatch.StartNew();
        var planeImg = PlotRenderer.Render(planeSpec);
        planeSw.Stop();
        if (planeImg != null)
        {
            InsertPlotImageForTest(planeImg, "reports/aiplot-plane.bmp");
            Console.WriteLine($"平面几何：kind={planeSpec.Kind} " +
                              $"points={planeSpec.Figure?.Points2D.Count ?? 0} " +
                              $"segments={planeSpec.Figure?.Segments.Count ?? 0} " +
                              $"ai={planeAi.Seconds:F1}s render={planeSw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // v1.6 立体几何：AI 给三维点/棱，本地等轴测投影。
        var solidAi = cfg.IsConfigured
            ? Task.Run(() => AiPlotClient.ParseAsync(
                "画一个棱长为 2 的正方体 ABCD-A1B1C1D1 的立体几何示意图，" +
                "标出 8 个顶点，标题写“正方体”。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiPlotClient.Result { Error = "AI 未配置" };
        var solidSpec = solidAi.Spec;
        if (solidSpec == null || solidSpec.Kind != "solid")
        {
            solidSpec = new PlotSpec
            {
                Kind = "solid",
                Title = "正方体",
                Figure = FigureRenderer.SampleCube(),
            };
        }
        var solidSw = System.Diagnostics.Stopwatch.StartNew();
        var solidImg = PlotRenderer.Render(solidSpec);
        solidSw.Stop();
        if (solidImg != null)
        {
            InsertPlotImageForTest(solidImg, "reports/aiplot-solid.bmp");
            Console.WriteLine($"立体几何：kind={solidSpec.Kind} " +
                              $"points={solidSpec.Figure?.Points3D.Count ?? 0} " +
                              $"edges={solidSpec.Figure?.Segments.Count ?? 0} " +
                              $"ai={solidAi.Seconds:F1}s render={solidSw.Elapsed.TotalMilliseconds:F1}ms");
        }
        Console.WriteLine("出图：reports/aiplot-plane.bmp / aiplot-solid.bmp");
    }

    /// <summary>自检：清空画布，把 AI 图插到屏幕中心并截图。</summary>
    private Stroke InsertPlotImageForTest(ImageData img, string shot)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = img,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(img.Width, img.Height),
        });
        float x = _virtualX + (_virtualW - img.Width) * 0.5f;
        float y = _virtualY + (_virtualH - img.Height) * 0.5f;
        s.Transform = Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(s);
        Doc.SelectOnly(new[] { s });
        SettleFrames(200);
        ScreenProbe.SaveBmp(shot, (int)_virtualX, (int)_virtualY,
                            (int)_virtualW, (int)_virtualH);
        return s;
    }

    /// <summary>自检：把 PlotSpec 插成 D2D 矢量对象（1100×760 局部坐标）。</summary>
    private Stroke InsertPlotFigureForTest(PlotSpec spec, string shot)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Figure,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Figure = spec,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1100f, 760f),
        });
        float x = _virtualX + (_virtualW - 1100f) * 0.5f;
        float y = _virtualY + (_virtualH - 760f) * 0.5f;
        s.Transform = Matrix3x2.CreateTranslation(x, y);
        Doc.AddStroke(s);
        Doc.SelectOnly(new[] { s });
        SettleFrames(220);
        ScreenProbe.SaveBmp(shot, (int)_virtualX, (int)_virtualY,
                            (int)_virtualW, (int)_virtualH);
        return s;
    }

    /// <summary>二次函数：AI 给 a/b/c，本地创建可缩放的抛物线对象。</summary>
    private void FigureQuadTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 二次函数：真实模型 → 可操作抛物线 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);

        var cfg = OcrClient.LoadConfig();
        var gen = cfg.IsConfigured
            ? Task.Run(() => AiQuestionClient.GenerateAsync(
                "一道高中二次函数题，教材/高考风格。图中给出抛物线 y=ax²+bx+c，" +
                "要求求顶点/最值或解集；figure 用 kind=quadratic，给 a/b/c 和 domain。",
                cfg, CancellationToken.None)).GetAwaiter().GetResult()
            : new AiQuestionClient.Result { Error = "AI 未配置" };
        Console.WriteLine($"AI 结果：ok={gen.Ok} {gen.Seconds:F1}s error={gen.Error}");

        FigureSpec spec = gen.Figure;
        if (spec == null || spec.Kind != "quadratic")
            spec = new FigureSpec { Kind = "quadratic", Qa = 1f, Qb = -4f, Qc = 3f,
                                    DomainMin = -1f, DomainMax = 5f };
        Console.WriteLine($"抛物线：a={spec.Qa} b={spec.Qb} c={spec.Qc} domain={spec.DomainMin}..{spec.DomainMax}");

        Doc.Clear();
        Doc.ClearHistory();
        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card?.TextBox != null)
        {
            var b = card.TextBox;
            b.Title = $"{cfg.Model} · 二次函数 · 1 道";
            b.Meta = gen.Ok ? $"AI 生成 · {gen.Seconds:F1}s · 请核对" : "AI 未配置，使用示例";
            b.Questions.Clear();
            b.Questions.Add(new TextBoxQuestion
            {
                Stem = gen.Ok ? gen.Stem : "已知抛物线 y = x² − 4x + 3，求它的顶点坐标和最值。",
                Answer = gen.Answer,
                Analysis = gen.Analysis,
            });
            b.Width = 560f * u;
            b.AutoWidth = false;
            b.InvalidateLayout();
            _ = b.EnsureLayout();
            float x = _virtualX + 80f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            card.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            card.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(card);
        }

        // y = a x² + b x + c → 现有 Parabola 对象（顶点 + 过点），可直接缩放/移动。
        float h = -spec.Qb / (2f * MathF.Max(0.0001f, spec.Qa));
        float k = spec.Qc - spec.Qb * spec.Qb / (4f * spec.Qa);
        float sUnit = 45f * u;
        float ox = 1850f, oy = 820f;

        // 函数图需要坐标系：x∈[-5,5]、y∈[-2,8]，网格步长 = 1 个单位。
        var axes = new Stroke
        {
            Tool = Tool.Coordinate,
            Kind = StrokeKind.Coordinate,
            Color = new Color4(0.55f, 0.58f, 0.62f, 1f),
            Width = 3f * u,
            Grid = true,
            AxisGridStep = sUnit,
        };
        axes.AddPoint(ox - 5f * sUnit, oy - 8f * sUnit, 1f, 0);
        axes.AddPoint(ox + 5f * sUnit, oy + 2f * sUnit, 1f, 0);
        axes.AddPoint(ox, oy, 1f, 0);
        Doc.AddStroke(axes);

        // y = a x² + b x + c → 现有 Parabola 对象（顶点 + 过点），可直接缩放/移动。
        var sh = new Stroke
        {
            Tool = Tool.Parabola,
            Kind = StrokeKind.Parabola,
            Color = new Color4(0.12f, 0.14f, 0.18f, 1f),
            Width = 5f * u,
            CurveAxis = spec.Qa > 0 ? CurveAxis.OpenUp : CurveAxis.OpenDown,
        };
        sh.SetParabolaVertex(ox + h * sUnit, oy - k * sUnit);
        sh.SetParabolaThroughPoint(ox + (h + 1f) * sUnit, oy - (k + spec.Qa) * sUnit);
        Doc.AddStroke(sh);
        Doc.SelectOnly(new[] { sh });
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-quad-editable.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 拖右下角缩放：现有通用框会等比改变曲线。
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        var br = SelectionHandles.CanvasPosition(SelHandle.BottomRight, frame, UiUnit);
        SelectionGestureForTest(br.X, br.Y);
        UpdateSelectionGestureForTest(br.X + 80f * u, br.Y + 60f * u);
        EndSelectionGestureForTest();
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-quad-scaled.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine("出图：reports/figure-quad-editable.bmp / figure-quad-scaled.bmp");
    }

    /// <summary>真实大模型出题：DeepSeek/OpenAI 兼容接口 → JSON → 结构化图形 → 卡片。</summary>
    private void FigureLiveTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 真实模型出题 + 结构化题图 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);

        var cfg = OcrClient.LoadConfig();
        Console.WriteLine($"AI 配置：configured={cfg.IsConfigured} model={cfg.Model} base={cfg.BaseUrl}");
        if (!cfg.IsConfigured)
        {
            Console.WriteLine("AI 未配置：跳过真实模型测试");
            return;
        }

        var gen = Task.Run(() => AiQuestionClient.GenerateAsync(
            "一道初中平面几何题，配一个简单的三角形或四边形，点坐标 0..10，题干要提到图中的点。",
            cfg, CancellationToken.None)).GetAwaiter().GetResult();

        Console.WriteLine($"AI 结果：ok={gen.Ok} {gen.Seconds:F1}s error={gen.Error}");
        if (gen.Ok)
            Console.WriteLine($"AI 题目：stem={ClipText(gen.Stem, 80)} " +
                              $"figure={(gen.Figure != null ? gen.Figure.Kind : "none")} " +
                              $"points={gen.Figure?.Points2D.Count + gen.Figure?.Points3D.Count ?? 0} " +
                              $"segments={gen.Figure?.Segments.Count ?? 0}");

        var spec = gen.Figure ?? FigureRenderer.SampleTriangle();
        var figure = FigureRenderer.Render(spec);
        InsertMockTextBoxFromUi();
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card?.TextBox != null)
        {
            var b = card.TextBox;
            b.Title = $"{cfg.Model} · 真实模型 · 1 道";
            b.Meta = gen.Ok
                ? $"AI 生成 · {gen.Seconds:F1}s · 请老师核对"
                : $"AI 失败：{ClipText(gen.Error, 60)}";
            b.Questions.Clear();
            b.Questions.Add(new TextBoxQuestion
            {
                Stem = gen.Ok ? gen.Stem : "真实模型未返回题目，下面是结构化图形兜底。",
                Answer = gen.Answer,
                Analysis = gen.Analysis,
                Figure = figure,
            });
            b.Width = 560f * u;
            b.AutoWidth = false;
            b.InvalidateLayout();
            _ = b.EnsureLayout();
            float x = _virtualX + 80f;
            float y = MathF.Max(_virtualY - ViewOffsetY + 16f * u,
                                (_virtualY + _virtualH) * 0.5f - ViewOffsetY - b.Height * 0.5f);
            card.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            card.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(card);
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp("reports/figure-live.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 真实模型场景 → 本地可拖动三角形对象：
        // AI 只给点坐标，本地用现有 Triangle 对象创建，顶点手柄/移动/撤销全部复用。
        Stroke tri = null;
        var ptsSpec = gen.Figure?.Points2D;
        if (ptsSpec != null && ptsSpec.Count >= 3)
        {
            var names = new List<string>();
            if (ptsSpec.ContainsKey("A") && ptsSpec.ContainsKey("B") && ptsSpec.ContainsKey("C"))
            { names.Add("A"); names.Add("B"); names.Add("C"); }
            else
            {
                foreach (var k in ptsSpec.Keys) { names.Add(k); if (names.Count == 3) break; }
            }
            var pts = names.Select(n => ptsSpec[n]).ToList();
            float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            float bw = MathF.Max(0.001f, maxX - minX), bh = MathF.Max(0.001f, maxY - minY);
            const float rw = 760f, rh = 600f, ox = 1420f, oy = 360f;
            float sc = MathF.Min(rw / bw, rh / bh);
            tri = new Stroke
            {
                Tool = Tool.Triangle,
                Kind = StrokeKind.Triangle,
                Color = new Color4(0.12f, 0.14f, 0.18f, 1f),
                Width = 5f * u,
            };
            foreach (var p in pts)
            {
                tri.AddPoint(ox + (p.X - minX) * sc,
                             oy + rh - (p.Y - minY) * sc, 1f, 0);
            }
            Doc.AddStroke(tri);
            Doc.SelectOnly(new[] { tri });
            SettleFrames(300);
            ScreenProbe.SaveBmp("reports/figure-live-editable.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

            var from = SelectionHandles.ShapeHandleCanvasPosition(tri, ShapeHandle.Vertex0);
            VertexDragForTest(tri, ShapeHandle.Vertex0, from.X, from.Y,
                              from.X + 90f * u, from.Y + 40f * u);
            SettleFrames(300);
            ScreenProbe.SaveBmp("reports/figure-live-dragged.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Console.WriteLine($"AI 场景 → 可拖动三角形：points=3 handleDrag=True");
        }

        Console.WriteLine("出图：reports/figure-live.bmp / figure-live-editable.bmp / figure-live-dragged.bmp");
    }

    private static string ClipText(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// P4 背景样式自检：四种预设 + 字号面板里的「背景」行 + 存读。
    /// 出图 reports/textbg-*.bmp。
    /// </summary>
    private void TextBackgroundTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 文字背景：透明 / 白纸 / 板色 / 浅色 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        if (Environment.GetCommandLineArgs().Contains("--darkboard"))
            BoardColor = new Vortice.Mathematics.Color4(0.10f, 0.16f, 0.19f, 1f);
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);
        string suffix = TextBoxBackground.Luma(BoardColor) < 0.5f ? "dark" : "light";

        void Make(int preset, string label, float x, float y)
        {
            InsertPlainTextBoxForTest($"{label}背景\n第二行：背景样式 {preset + 1}/4");
            var s = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
            if (s?.TextBox == null) return;
            var b = s.TextBox;
            b.AutoWidth = false;
            b.Width = 760f * u / MathF.Max(0.5f, b.Unit);
            b.FontSize = 16f;
            b.Fill = TextBoxBackground.Fill(preset, BoardColor, card: false);
            b.Border = TextBoxBackground.Border(preset, BoardColor, card: false);
            b.Ink = preset == 0
                ? TextBoxBackground.InkFor(BoardColor)
                : TextBoxBackground.InkFor(b.Fill);
            s.Color = b.Ink;
            b.LineColors.Clear();
            b.ColorSpans.Clear();
            b.InvalidateLayout();
            var ls = b.EnsureLayout();
            s.SetTextBoxRect(new Vector2(x, y), b.Width, b.Height);
            s.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(s);
            Console.WriteLine($"bg preset={preset} board=({BoardColor.R:F2},{BoardColor.G:F2},{BoardColor.B:F2}) " +
                              $"fill=({b.Fill.R:F2},{b.Fill.G:F2},{b.Fill.B:F2},{b.Fill.A:F2}) " +
                              $"ink=({b.Ink.R:F2},{b.Ink.G:F2},{b.Ink.B:F2}) " +
                              $"color=({s.Color.R:F2},{s.Color.G:F2},{s.Color.B:F2}) " +
                              $"textLen={b.Text.Length} lines={ls.Count} h={b.Height:F0} " +
                              $"origin=({s.Points[0].X:F0},{s.Points[0].Y:F0}) w={b.Width:F0}");
        }

        Doc.Clear();
        Doc.ClearHistory();
        Make(0, "透明", 160f, 260f);
        Make(1, "白纸", 1060f, 260f);
        Make(2, "板色", 160f, 700f);
        Make(3, "浅色", 1060f, 700f);
        Doc.SelectOnly(Array.Empty<Stroke>());
        SettleFrames(300);
        ScreenProbe.SaveBmp($"reports/textbg-{suffix}.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 选中第一个框，打开字号面板：应能看到第四行「背景」。
        var first = Doc.Strokes.Count > 0 ? Doc.Strokes[0] : null;
        if (first != null)
        {
            Doc.SelectOnly(new[] { first });
            RunBarButtonForTest(SelBarButton.FontSize);
            SettleFrames(300);
            ScreenProbe.SaveBmp($"reports/textbg-panel-{suffix}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

            // 面板命中：第四行第 3 格应报「板色」。
            var frame = SelectionHandles.FrameOf(Doc.Selected);
            var bgCell = SelectionHandles.FontPanelBackgroundRect(2, frame.CanvasAabb, UiUnit,
                                                                 FurnitureViewport);
            var part = SelectionHandles.PanelPartAt(
                (bgCell.MinX + bgCell.MaxX) * 0.5f,
                (bgCell.MinY + bgCell.MaxY) * 0.5f,
                frame.CanvasAabb, UiUnit, FurnitureViewport, SelPanel.FontSize,
                SelectionHandles.SwatchCount, textBar: true);
            Console.WriteLine($"BGHIT part={(int)part} expected=" +
                              $"{(int)SelectionHandles.PanelPart.BackgroundBase + 2}");

            var beforeFill = first.TextBox?.Fill ?? default;
            SetSelectedTextBackgroundFromUi(2);
            bool bgChanged = first.TextBox != null
                             && !TextBoxBackground.Same(beforeFill, first.TextBox.Fill);
            Doc.Undo();
            bool bgUndone = first.TextBox != null
                            && TextBoxBackground.Same(beforeFill, first.TextBox.Fill);
            Doc.Redo();
            bool bgRedone = first.TextBox != null
                            && !TextBoxBackground.Same(beforeFill, first.TextBox.Fill);
            Console.WriteLine($"背景撤销：changed={bgChanged} undo={bgUndone} redo={bgRedone}");
        }

        // 白纸卡编辑态：RichEdit 只能画实色近似，但应该看起来就是白纸。
        Stroke whiteBox = null;
        foreach (var s in Doc.Strokes)
            if (s.TextBox is { } b0 && b0.Fill.A > 0.9f && TextBoxBackground.Luma(b0.Fill) > 0.7f)
            { whiteBox = s; break; }
        if (whiteBox != null)
        {
            BeginTextBoxEdit(whiteBox);
            SettleFrames(260);
            ScreenProbe.SaveBmp($"reports/textbg-edit-{suffix}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            EndTextBoxEditFromUi(false);
            SettleFrames(120);
        }

        // 深色板色编辑态：编辑底应接近板色，不能突然变白。
        Stroke boardBox = null;
        foreach (var s in Doc.Strokes)
            if (s.TextBox is { } b1 && b1.Fill.A > 0.9f && TextBoxBackground.Luma(b1.Fill) < 0.3f)
            { boardBox = s; break; }
        if (boardBox != null)
        {
            BeginTextBoxEdit(boardBox);
            SettleFrames(260);
            ScreenProbe.SaveBmp($"reports/textbg-edit-board-{suffix}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            EndTextBoxEditFromUi(false);
            SettleFrames(120);
        }

        // 走真实入口应用白纸，再存读：Fill/Border/Radius 必须还在。
        if (first != null)
        {
            SetSelectedTextBackgroundFromUi(1);
            SettleFrames(120);
            var bytes = InkSerializer.Save(Doc);
            InkSerializer.LoadInto(Doc, bytes);
            Stroke back = null;
            foreach (var s in Doc.Strokes)
                if (s.TextBox is { } b && b.Fill.A > 0.9f) { back = s; break; }
            Console.WriteLine($"背景存读：fillA={back?.TextBox?.Fill.A:F2} " +
                              $"borderA={back?.TextBox?.Border.A:F2} " +
                              $"radius={back?.TextBox?.Radius:F0}");
        }
        Console.WriteLine($"出图：reports/textbg-{suffix}.bmp / textbg-panel-{suffix}.bmp");
    }

    /// <summary>
    /// 新文字模型自检：普通框纯文本；OCR 只读富文本卡；AI 卡不变。
    /// 出图 reports/newtext-*.bmp。
    /// </summary>
    private void NewTextModelTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 新文字模型：纯文本 + OCR 只读卡 + AI 卡 ===");
        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        SetBoardFromUi(true);
        Tool = Tool.Marquee;
        ShowHud = false;
        float u = MathF.Max(0.75f, DpiScale);

        void Shot(string name)
        {
            SettleFrames(260);
            ScreenProbe.SaveBmp($"reports/{name}.bmp",
                (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        }

        // ① 普通文本框：Markdown / LaTeX 全部按字面显示。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("**粗体** 不是粗体\n# 标题 不是标题\n公式 $x^2$ 原样显示\n--- 不是分隔线");
        var plain = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (plain?.TextBox != null)
        {
            plain.Color = new Vortice.Mathematics.Color4(0.12f, 0.14f, 0.18f, 1f);
            plain.TextBox.Ink = plain.Color;
            plain.TextBox.Paper = false;
            plain.TextBox.LineColors.Clear();
            plain.TextBox.ColorSpans.Clear();
            plain.TextBox.InvalidateLayout();
            plain.SyncTextBoxBounds();
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-plain");
        Console.WriteLine($"纯文本：rich={plain?.TextBox?.Rich} " +
                          $"readOnly={plain?.TextBox?.ReadOnly} " +
                          $"text={plain?.TextBox?.Text.Replace('\n', '/')}");

        // 编辑态同样按字面显示：证明“所见即所得”。
        BeginTextBoxEdit(plain);
        SetTextBoxEditTextForTest("**粗体** 不是粗体\n# 标题 不是标题\n公式 $x^2$ 原样显示\n--- 不是分隔线");
        Shot("newtext-plain-edit");
        EndTextBoxEditFromUi(true);
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-plain-committed");

        // ② OCR 结果：只读富文本卡，文字 + 公式混排。
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("已知 $x^2-4x+3=0$，求：\n（1）对称轴；\n（2）顶点坐标。");
        var ocr = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (ocr?.TextBox != null)
        {
            ocr.Color = new Vortice.Mathematics.Color4(0.12f, 0.14f, 0.18f, 1f);
            ocr.TextBox.Ink = ocr.Color;
            ocr.TextBox.LineColors.Clear();
            ocr.TextBox.ColorSpans.Clear();
            ocr.TextBox.Rich = true;
            ocr.TextBox.ReadOnly = true;
            ocr.TextBox.Paper = true;
            ocr.TextBox.Fill = TextBoxBackground.Fill(1, BoardColor, card: true);
            ocr.TextBox.Border = TextBoxBackground.Border(1, BoardColor, card: true);
            ocr.TextBox.AutoWidth = false;
            ocr.TextBox.Width = 760f * u;
            ocr.TextBox.FontSize = 15f;
            ocr.TextBox.InvalidateLayout();
            ocr.TextBox.FitToContent(ocr.TextBox.Width, 0f, fitWidth: false);
            ocr.SyncTextBoxBounds();
        }
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-ocr");
        BeginTextBoxEdit(ocr);
        Console.WriteLine($"OCR 卡：rich={ocr?.TextBox?.Rich} " +
                          $"readOnly={ocr?.TextBox?.ReadOnly} " +
                          $"editing={TextBoxEditing}（应为 False）");
        if (TextBoxEditing) EndTextBoxEditFromUi(false);

        // 只读标志要能存读：否则重新打开又变成可编辑文本。
        var saved = InkSerializer.Save(Doc);
        InkSerializer.LoadInto(Doc, saved);
        Stroke back = null;
        foreach (var s in Doc.Strokes)
            if (s.TextBox is { Rich: true }) { back = s; break; }
        Console.WriteLine($"OCR 存读：rich={back?.TextBox?.Rich} " +
                          $"readOnly={back?.TextBox?.ReadOnly} " +
                          $"editing={TextBoxEditing}（应为 False）");

        // ③ AI 题卡：沿用原有只读渲染。
        Doc.Clear();
        Doc.ClearHistory();
        InsertMockTextBoxFromUi();
        Doc.SelectOnly(Array.Empty<Stroke>());
        Shot("newtext-ai");
        Console.WriteLine("出图：reports/newtext-plain.bmp / newtext-ocr.bmp / newtext-ai.bmp");
    }

    private void AiShow(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 面板 + 题卡摆样（1A：mock，不联网）===");

        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ShowHud = false;

        if (Ui is not InkUi.FullUi ui)
        {
            Console.WriteLine("界面不是 FullUi，出图取消。");
            _quit = true;
            return;
        }

        // ① 面板
        ui.OpenAiPanelForTest();
        SettleFrames(400);
        string p1 = path;
        bool ok1 = ScreenProbe.SaveBmp(p1, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ② 生成 mock 题卡（答案默认折叠）
        ui.GenerateMockForTest();
        SettleFrames(400);
        string p2 = System.IO.Path.ChangeExtension(path, null) + "-card.bmp";
        bool ok2 = ScreenProbe.SaveBmp(p2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ③ 选中框（选择工具 + 点选卡片）
        Stroke card = null;
        foreach (var s in Doc.Strokes)
            if (s.Kind == StrokeKind.TextBox) { card = s; break; }
        if (card != null)
        {
            Host.Commands.SetTool(Tool.Marquee);
            Doc.SelectOnly(new[] { card });
            SettleFrames(300);
        }
        string ps = System.IO.Path.ChangeExtension(path, null) + "-selected.bmp";
        bool oks = card != null && ScreenProbe.SaveBmp(ps, (int)_virtualX, (int)_virtualY,
                                                       (int)_virtualW, (int)_virtualH);

        // ④ 展开答案
        ToggleTextBoxAnswersForTest();
        SettleFrames(300);
        string p3 = System.IO.Path.ChangeExtension(path, null) + "-answer.bmp";
        bool ok3 = ScreenProbe.SaveBmp(p3, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ⑤ 普通文本框：同样 8 手柄、可改字号
        Doc.Clear();
        Doc.ClearHistory();
        string sample = "# 例题\n\n"
            + "已知数列 \\(\\{a_n\\}\\) 满足：\n"
            + "\\[\na_1=2,\\qquad a_{n+1}=3a_n+2\\quad(n\\ge 1)\n\\]\n"
            + "求数列 \\(\\{a_n\\}\\) 的通项公式。\n\n"
            + "### 分析\n\n"
            + "形式：\\(a_{n+1}=pa_n+q\\)，用待定系数构造等比数列。\n"
            + "设 \\(a_{n+1}+c=3(a_n+c)\\)，展开得 \\(a_{n+1}=3a_n+2c\\)，\n"
            + "与原式比较得 \\(2c=2\\Rightarrow c=1\\)。\n\n"
            + "行内中文：\\(\\text{当 }n\\ge 1\\text{ 时，}a_n>0\\)。\n"
            + "中文混排：\n"
            + "\\[\n\\text{当 }a_n>0\\text{ 时，}a_{n+1}=3a_n+2\n\\]\n"
            + "括号：\n"
            + "\\[\na_{n+1}+An+B=2\\bigl(a_n+A(n-1)+B\\bigr)\n\\]\n"
            + "```latex\n\\boxed{a_n=7\\cdot 2^{n-1}-3n-3}\n```\n"
            + "所以：\n"
            + "\\[\na_n+1=3\\cdot 3^{n-1}=3^n\n\\]\n"
            + "**答案：** \\(\\boxed{a_n=3^n-1}\\)";
        // 走真实路径：插入空框 → 原生 EDIT 粘贴长文 → 结束编辑（会重新排版长高）
        InsertPlainTextBoxForTest("");
        var editTarget = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (editTarget != null)
        {
            BeginTextBoxEdit(editTarget);
            SetTextBoxEditTextForTest(sample);
            EndTextBoxEditFromUi(true);
        }
        Doc.SelectOnly(editTarget != null ? new[] { editTarget } : Array.Empty<Stroke>());
        Host.Commands.SetTool(Tool.Marquee);
        SettleFrames(300);
        string p4 = System.IO.Path.ChangeExtension(path, null) + "-textbox.bmp";
        bool ok4 = ScreenProbe.SaveBmp(p4, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ⑥ 改字色：文字和公式图都应该跟着变，不能只变文字
        SetSelectionColor(new Color4(0.80f, 0.18f, 0.18f, 1f));
        SettleFrames(200);
        string p5 = System.IO.Path.ChangeExtension(path, null) + "-red.bmp";
        bool ok5 = ScreenProbe.SaveBmp(p5, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ⑦ 删除：删完不能有残字；再框选/点删除也不能命中已删对象
        // 先做一次真实的"改宽 → 长高 → 重登记索引"，再删。
        if (editTarget?.TextBox != null)
        {
            float hBeforeWidth = editTarget.TextBox.Height;
            var oldB = editTarget.PaddedBounds;
            editTarget.TextBox.Width *= 0.85f;
            editTarget.TextBox.InvalidateLayout();
            editTarget.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(editTarget);
            Doc.Dirty.Add(oldB);
            Doc.Dirty.Add(editTarget.PaddedBounds);
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            SettleFrames(150);
            bool manualKept = !editTarget.TextBox.AutoHeight
                              && MathF.Abs(editTarget.TextBox.Height - hBeforeWidth) < 1f;
            Console.WriteLine(manualKept
                ? $"改宽后保持手动高度：{hBeforeWidth:0} → {editTarget.TextBox.Height:0}"
                : $"改宽后高度跳变：{hBeforeWidth:0} → {editTarget.TextBox.Height:0} auto={editTarget.TextBox.AutoHeight}");
        }
        // 改窄后拖下边：高度应跟指针平滑变化，不能跳回"自动全高"
        if (editTarget?.TextBox != null)
        {
            var frameW = SelectionHandles.FrameOf(Doc.Selected);
            var bh = SelectionHandles.CanvasPosition(SelHandle.Bottom, frameW, UiUnit);
            float hPreDrag = editTarget.TextBox.Height;
            SelectionGestureForTest(bh.X, bh.Y);
            UpdateSelectionGestureForTest(bh.X, bh.Y - 90f);
            EndSelectionGestureForTest();
            SettleFrames(150);
            float hPostDrag = editTarget.TextBox.Height;
            bool smooth = !editTarget.TextBox.AutoHeight
                          && hPostDrag < hPreDrag - 20f && hPostDrag > hPreDrag - 180f;
            Console.WriteLine(smooth
                ? $"手动拖下边平滑：{hPreDrag:0} → {hPostDrag:0}"
                : $"手动拖下边异常：{hPreDrag:0} → {hPostDrag:0} auto={editTarget.TextBox.AutoHeight}");
        }
        // 内部滚动：固定高度小于内容时，滚轮/ScrollBy 只动内容，不动画布
        if (editTarget?.TextBox != null)
        {
            var tbCard = editTarget.TextBox;
            var oldB = editTarget.PaddedBounds;
            tbCard.Height = MathF.Max(200f, MathF.Min(400f, tbCard.ContentHeight * 0.5f));
            tbCard.AutoHeight = false;
            tbCard.InvalidateLayout();
            editTarget.SyncTextBoxBounds();
            Doc.RefreshStrokeGrid(editTarget);
            Doc.Dirty.Add(oldB);
            Doc.Dirty.Add(editTarget.PaddedBounds);
            bool scrollOk = tbCard.MaxScroll > 1f && tbCard.ScrollBy(90f) && tbCard.ScrollY > 0.5f;
            // ?????????????????????????????
            tbCard.ScrollY = 0f;
            var bb = editTarget.PaddedBounds;
            PointerX = (bb.MinX + bb.MaxX) * 0.5f;
            PointerY = (bb.MinY + bb.MaxY) * 0.5f + ViewOffsetY;
            PointerInside = true;
            HandleWheel(new IntPtr((long)(((-120) & 0xFFFF) << 16)));
            bool wheelOk = tbCard.ScrollY > 0.5f;
            scrollOk &= wheelOk;
            Doc.StructureChangedSinceRender = true;
            Doc.Version++;
            SettleFrames(150);
            string p7 = System.IO.Path.ChangeExtension(path, null) + "-scroll.bmp";
            ScreenProbe.SaveBmp(p7, (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Console.WriteLine(scrollOk
                ? $"内部滚动通过：内容 {tbCard.ContentHeight:0} 高，框 {tbCard.Height:0} 高，ScrollY={tbCard.ScrollY:0}"
                : $"内部滚动失败：content={tbCard.ContentHeight:0} height={tbCard.Height:0} scroll={tbCard.ScrollY:0}");
        }
        var victim = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        var vb = victim?.PaddedBounds ?? default;
        RunBarButtonForTest(SelBarButton.Delete);   // 走真实操作条删除路径
        SettleFrames(300);
        string p6 = System.IO.Path.ChangeExtension(path, null) + "-deleted.bmp";
        bool ok6 = ScreenProbe.SaveBmp(p6, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);
        bool clean = Doc.Strokes.Count == 0 && Doc.Selected.Count == 0;
        bool ghost = false;
        if (victim != null)
        {
            var candidates = new List<Stroke>();
            Doc.QueryGrid(vb, candidates);
            ghost |= candidates.Count > 0;
            for (int gy = 0; gy <= 2 && !ghost; gy++)
                for (int gx = 0; gx <= 2 && !ghost; gx++)
                {
                    float px = vb.MinX + (vb.MaxX - vb.MinX) * (0.15f + 0.35f * gx);
                    float py = vb.MinY + (vb.MaxY - vb.MinY) * (0.15f + 0.35f * gy);
                    if (Doc.HitObjectAt(px, py, 8f) != null) ghost = true;
                }
        }
        clean &= !ghost;
        // 操作条统一"给下限、不翻面"：删除格也必须点得到（画/命中同源）。
        bool textBar = Doc.Selected.Count == 1 && Doc.Selected[0].Kind == StrokeKind.TextBox;
        int barCount = SelectionHandles.BarButtonCountFor(textBar);
        int delIndex = SelectionHandles.BarIndexOf(SelBarButton.Delete, textBar);
        var barRect = SelectionHandles.BarRect(vb, UiUnit, FurnitureViewport, barCount);
        var delBtn = SelectionHandles.BarButtonRectIn(barRect, delIndex, UiUnit);
        bool delHit = SelectionHandles.BarButtonAtIn((delBtn.MinX + delBtn.MaxX) * 0.5f,
                                                     (delBtn.MinY + delBtn.MaxY) * 0.5f,
                                                     barRect, UiUnit, barCount) == delIndex;
        clean &= delHit;
        Console.WriteLine(clean
            ? $"删除自检通过：strokes={Doc.Strokes.Count} selected={Doc.Selected.Count}"
            : $"删除自检失败：strokes={Doc.Strokes.Count} selected={Doc.Selected.Count} ghost={ghost} delHit={delHit}");

        // 删除 → 撤销 → 重做：反复进出不能让框/字残留
        bool undoOk = true, redoOk = true;
        if (victim != null)
        {
            Doc.Undo();
            SettleFrames(300);
            undoOk = Doc.Strokes.Count == 1 && ReferenceEquals(Doc.Strokes[0], victim);
            string pu = System.IO.Path.ChangeExtension(path, null) + "-undo.bmp";
            ScreenProbe.SaveBmp(pu, (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
            Doc.Redo();
            SettleFrames(300);
            redoOk = Doc.Strokes.Count == 0;
            string pr = System.IO.Path.ChangeExtension(path, null) + "-redo.bmp";
            ScreenProbe.SaveBmp(pr, (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        }
        Console.WriteLine(undoOk && redoOk ? "删除撤销/重做通过" : $"删除撤销/重做失败：undo={undoOk} redo={redoOk}");

        // 编辑中直接删：先收原生 EDIT，不能留一个还在显示文字的窗口
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("编辑中删除测试");
        var st2 = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (st2 != null)
        {
            BeginTextBoxEdit(st2);
            SetTextBoxEditTextForTest("编辑中删除测试\n第二行文字");
            RunBarButtonForTest(SelBarButton.Delete);
            SettleFrames(250);
        }
        bool editDelOk = !TextBoxEditing && Doc.Strokes.Count == 0 && Doc.Selected.Count == 0;
        Console.WriteLine(editDelOk ? "编辑中删除通过" : $"编辑中删除失败：editing={TextBoxEditing} strokes={Doc.Strokes.Count}");

        // 第一次粘贴的尺寸也要能随文字一起撤销/重做
        Doc.Clear();
        Doc.ClearHistory();
        InsertPlainTextBoxForTest("");
        var st3 = Doc.Strokes.Count > 0 ? Doc.Strokes[^1] : null;
        if (st3 != null)
        {
            float emptyW = st3.TextBox.Width, emptyH = st3.TextBox.Height;
            var emptyPos = new Vector2(st3.Points[0].X, st3.Points[0].Y);
            BeginTextBoxEdit(st3);
            SetTextBoxEditTextForTest("短文本");
            EndTextBoxEditFromUi(true);
            SettleFrames(150);
            float fitW = st3.TextBox.Width, fitH = st3.TextBox.Height;
            var fitPos = new Vector2(st3.Points[0].X, st3.Points[0].Y);
            Doc.Undo();
            SettleFrames(100);
            bool undoOk2 = st3.TextBox.Text.Length == 0
                           && MathF.Abs(st3.TextBox.Width - emptyW) < 1f
                           && MathF.Abs(st3.TextBox.Height - emptyH) < 1f
                           && st3.TextBox.AutoHeight
                           && Vector2.Distance(emptyPos, new Vector2(st3.Points[0].X, st3.Points[0].Y)) < 1f;
            Doc.Redo();
            SettleFrames(100);
            bool redoOk2 = st3.TextBox.Text == "短文本"
                           && MathF.Abs(st3.TextBox.Width - fitW) < 1f
                           && MathF.Abs(st3.TextBox.Height - fitH) < 1f
                           && !st3.TextBox.AutoHeight;
            Console.WriteLine(undoOk2 && redoOk2
                ? $"首贴尺寸撤销/重做通过：{emptyW:0}×{emptyH:0} ↔ {fitW:0}×{fitH:0}"
                : $"首贴尺寸撤销/重做失败：undo={undoOk2} redo={redoOk2} 当前 {st3.TextBox.Width:0}×{st3.TextBox.Height:0} auto={st3.TextBox.AutoHeight}");
        }

        Console.WriteLine(ok1 && ok2 && ok3 && oks && ok4 && ok5 && ok6
            ? $"出图：{p1} / {p2} / {ps} / {p3} / {p4} / {p5} / {p6}"
            : "抓屏失败（看上一行）");
        Console.WriteLine($"题卡/文本框数：{TextBoxCountForTest}（删除后应为 0）");
        _quit = true;
    }

    private void RadialShow(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 呼出盘摆样（Ctrl+Alt+Shift+Q）===");

        // --dark：深色那档也出一张（呼出盘跟着 FloatingTheme 走，这里顺手验观感）。
        // 自检模式用临时配置（`_selfCheckMode`），不会碰用户的 settings.json。
        if (Environment.GetCommandLineArgs().Contains("--dark")) SetUiPref("dark", "1");

        SetUiFactory(() => new InkUi.FullUi());
        PassThrough = false;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        ShowHud = false;
        RadialTestHold = true;

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // ① 打开（还没划）
        RadialOpenForTest(cx, cy);
        SettleFrames(400);
        bool ok1 = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ② 划向「红笔」（正北；v3 起北格固定红，东西才是翻色）
        RadialMoveForTest(cx, cy - 120f);
        SettleFrames(300);
        string path2 = System.IO.Path.ChangeExtension(path, null) + "-red.bmp";
        bool ok2 = ScreenProbe.SaveBmp(path2, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // ③ 划向「橡皮」（正南）：验证中央图标跟着扇区变、色环压暗
        RadialMoveForTest(cx, cy + 120f);
        SettleFrames(300);
        string path3 = System.IO.Path.ChangeExtension(path, null) + "-eraser.bmp";
        bool ok3 = ScreenProbe.SaveBmp(path3, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        RadialCancelForTest("摆样结束");
        RadialTestHold = false;
        Console.WriteLine(ok1 && ok2 && ok3 ? $"出图：{path} / {path2} / {path3}" : "抓屏失败（看上一行）");
        _quit = true;
    }
    private static byte[] MakeLegacyFile(InkDocument doc, int version, int droppedBytes)
    {
        byte[] cur = InkSerializer.Save(doc);
        byte[] old = droppedBytes > 0 ? new byte[cur.Length - droppedBytes] : cur;
        if (droppedBytes > 0) Array.Copy(cur, old, old.Length);
        // 魔数（8 字节）后面那个小端 int32 就是版本号。
        old[4] = (byte)version; old[5] = 0; old[6] = 0; old[7] = 0;
        return old;
    }
    private void AxisShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        float x0 = _virtualX + 460f, y0 = _virtualY + 400f;
        // 外框（两张坐标系共用）：x0..x0+760 × y0..y0+620，所以半宽半高 = (380, 310)。
        // **画法改成了"按下 = 原点、拖出去 = 展开"**（见 Stroke.SetAxisBox），
        // 所以这里的第一参数是原点、第二参数是"往右下拖多远"，不再是外框的两个角。
        const float hw = 380f, hh = 310f;

        // ① 坐标系 · 带网格，原点摊在框的左下（老师最常用的"只留第一象限"）
        var g = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = true,
        };
        for (int i = 0; i < 3; i++) g.AddPoint(x0 + 130f, y0 + 470f, 1f, 0);
        g.SetAxisBox(x0 + 130f, y0 + 470f, x0 + 130f + hw, y0 + 470f + hh);
        Doc.AddStroke(g);

        // ② 坐标系 · 不带网格，原点在框正中间（和左边对照）
        var c = new Stroke
        {
            Tool = Tool.Coordinate, Kind = StrokeKind.Coordinate,
            Color = ink, Width = 4f * DpiScale, Grid = false,
        };
        for (int i = 0; i < 3; i++) c.AddPoint(x0 + 900f + hw, y0 + hh, 1f, 0);
        c.SetAxisBox(x0 + 900f + hw, y0 + hh, x0 + 900f + hw + hw, y0 + hh + hh);
        Doc.AddStroke(c);

        // ③ 数轴：一条水平线 + 右端箭头（没有刻度）
        var n = new Stroke
        {
            Tool = Tool.NumberLine, Kind = StrokeKind.NumberLine,
            Color = ink, Width = 4f * DpiScale,
        };
        for (int i = 0; i < 2; i++) n.AddPoint(x0, y0 + 760f, 1f, 0);
        n.SetAxisBox(x0, y0 + 760f, x0 + 1660f, y0 + 760f);
        Doc.AddStroke(n);

        // ④ 一条虚线直线（顺带把线型也看进去：虚线在粗笔上是不是像样）
        var d = new Stroke
        {
            Tool = Tool.Line, Kind = StrokeKind.Line,
            Color = ink, Width = 7f * DpiScale, Dash = StrokeDash.Dashed,
        };
        d.AddPoint(x0, y0 + 960f, 1f, 0);
        d.AddPoint(x0 + 1660f, y0 + 960f, 1f, 0);
        Doc.AddStroke(d);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)(x0 - 60f), (int)(y0 - 60f), 1900, 1150);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 出图：函数曲线各来一份——**抛物线四种开口、双曲线两个方向、正弦 / 余弦各一个周期、
    /// 波浪线三个周期、正切一支**。
    /// 用法 `--curveshow [路径]`，默认 `reports/四种曲线.bmp`。
    ///
    /// 自检盯的是"数值对不对"，这一张是给**眼睛**看的（这条教训见 计划-图形工具.md 10.7）：
    ///   · 曲线胖不胖、够不够顺（段数按尺寸定，太少了看着是折线）；
    ///   · 双曲线**截断到哪**（`Stroke.HyperbolaTMaxOf`：出框就停笔、最多画到 `2a`：
    ///     太短看着像两根短线、太长就把包围盒撑得很大）；
    ///   · 四种开口是不是真的四个方向（左右开口是"换自变量"，最容易写反）；
    ///   · 正弦 / 余弦的**起点**对不对（正弦从轴起、余弦从峰起），而且**都是一个周期**；
    ///   · **波浪线**是不是"很多个周期"（和正弦那一张摆在一起看，一眼就该分得开）；
    ///   · 正切**两侧的渐近线**在不在、曲线有没有贴着它上去（这条靠 `TangentMinAspect` 兜着，
    ///     所以这一张**故意只拖 60 高**：画出来仍该是贴着渐近线的那一支）。
    /// 摆放：4 列 × 3 行，第一行四种开口、第二行"双曲线两个方向 ＋ 正弦 / 余弦 / 波浪线 / 正切"、
    /// 第三行四种立体。第二行挤了 6 个图形，所以位置是按"不碰上邻居"逐个摆的（见各段注释）。
    /// </summary>
    /// <summary>
    /// 出图（`--smoothshow [路径]`）：**曲线化开/关的对照**（2026-09-28）。
    ///
    /// 同一组"手写样本"画两遍——先按现在的折线出 `-off`，再把曲线化打开、整层重画出 `-on`：
    ///   ① 快弧：采样故意稀（模拟"关掉 Windows Ink 的笔 / 鼠标"）；
    ///   ② 直角：必须原样保留（用户唯一的硬约束）；
    ///   ③ 抛物线：手画曲线不许被改形（上次砍"保形平滑"就是因为这个）；
    ///   ④ 锯齿：连续尖角，一个都不许被磨圆。
    /// 好不好看只能靠眼睛，所以单开一条出图命令（判据交给下一轮真人对比）。
    /// </summary>
    private void SmoothShowcase(string path)
    {
        Console.WriteLine($"=== 出图：曲线化对照（{path}）===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);
        var dotInk = new Color4(0.62f, 0.66f, 0.72f, 1f);   // 采样点的小灰点
        float penW = 6f * DpiScale;
        float ox = _virtualX + 120f, oy = _virtualY + 160f;

        void Add(IEnumerable<Vector2> pts, float dx, float dy, bool dots = true)
        {
            var list = new List<Vector2>();
            foreach (var p in pts) list.Add(new Vector2(p.X + dx, p.Y + dy));

            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = ink, Width = penW };
            foreach (var p in list) s.AddPoint(p.X, p.Y, 0.5f, 0);
            Doc.AddStroke(s);

            if (!dots) return;
            // 每个采样点打一个小灰点：曲线过不过点、直角动没动，看这个最直观
            foreach (var p in list)
            {
                var d = new Stroke
                {
                    Tool = Tool.Pen, Kind = StrokeKind.Freehand, Color = dotInk, Width = 2.4f * DpiScale,
                };
                d.AddPoint(p.X, p.Y, 0.5f, 0);
                Doc.AddStroke(d);
            }
        }

        // ① 快弧：中心 (ox+300, oy+280)、半径 280、每 13° 一个点（≈65px 一段，折角肉眼可见）
        var arc = new List<Vector2>();
        for (int i = 0; i <= 14; i++)
        {
            float a = MathF.PI * i / 14f;
            arc.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(arc, ox + 300f, oy + 280f);

        // ② 直角：两条臂各 6 个点（步长 56）
        var corner = new List<Vector2>();
        for (int i = 0; i <= 6; i++) corner.Add(new Vector2(i * 56f, 0f));
        for (int i = 1; i <= 6; i++) corner.Add(new Vector2(336f, i * 56f));
        Add(corner, ox + 760f, oy + 100f);

        // ③ 抛物线：y = x²/500，x ∈ [−260, 260]（手画曲线不许被改形）
        var parab = new List<Vector2>();
        for (int i = 0; i <= 13; i++)
        {
            float x = -260f + i * 40f;
            parab.Add(new Vector2(x, x * x / 500f));
        }
        Add(parab, ox + 1400f, oy + 120f);

        // ④ 锯齿：周期 2 的连续尖角（每个峰谷都必须留住）
        var zig = new List<Vector2>();
        for (int i = 0; i <= 12; i++) zig.Add(new Vector2(i * 55f, (i % 2 == 0) ? 0f : 110f));
        Add(zig, ox + 1860f, oy + 100f);

        // ⑤ 慢画直角：90° 摊在 5 小段上、**每段只有 20°**（比角点阈值 35° 小）——
        //    只有 ±2 点窗口判据兜得住；局部判据看到每段 20° 是判不出来的。
        //
        //    ⚠ 2026-10-06 修正：原来这条用的是 **120px 一段**（"90° 摊在三小段上"）。
        //    那个点距对应的速度是 130Hz × 120px ≈ 15600px/s，是"飞笔"不是"慢画"；
        //    而且"90° 摊在 3 个 120px 点上"与"半径 ~229px 的圆弧"在数学上无法区分
        //    （只有 4 个点），逼着窗口判据去误伤圆弧——正是"快速画圆变折线"的根因。
        //    改成 11px 一段（真·慢画的采样密度）之后，它才真的在测它声称要测的东西。
        var slow = new List<Vector2>();
        {
            const float stepPx = 11f;            // 点距（慢画：11px/点）
            int nPer = 6;                        // 先直走 6 段
            float ang5 = 0f;
            var p = new Vector2(0f, 0f);
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            for (int i = 0; i < 5; i++)          // 转 5 段 × 20° = 100°
            {
                ang5 += 20f * MathF.PI / 180f;
                slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx;
            }
            for (int i = 0; i < nPer; i++) { slow.Add(p); p += new Vector2(MathF.Cos(ang5), MathF.Sin(ang5)) * stepPx; }
            slow.Add(p);
        }
        Add(slow, ox + 760f, oy + 520f);

        // ⑥ 密采样弧：**和真笔（Windows Ink 开）一个量级**（每 1° 一个点 ≈ 4.9px 一段）。
        // 它回答的是"拿手写板试为什么看不出区别"：点密到一定程度，折线和曲线就是同一根线
        //（弦长 c、半径 R 时中点偏差 ≈ c²/8R，c=5、R=280 → 0.011px，比一个像素小两个数量级）。
        var dense = new List<Vector2>();
        for (int i = 0; i <= 180; i++)
        {
            float a = MathF.PI * i / 180f;
            dense.Add(new Vector2(MathF.Cos(a) * 280f, -MathF.Sin(a) * 280f));
        }
        Add(dense, ox + 1560f, oy + 700f, dots: false);

        // ⑦ **稀疏快弧**（用户 2026-10-06 报的那个）：半径 150、每 22.5° 一个点
        //（弦长 = 2×150×sin(11.25°) ≈ 58.5px 一段）。这是"快速画圆 / 手写板关掉
        //  Windows Ink"的采样密度。
        //
        // 为什么单列这一条：窗口判据量到的转角 ≈ 2×(点距/半径)，
        // 这里 = 2×58.5/150 = 44.7° > 阈值 35° → **每个点都被判成角点** →
        // 半圆退化成 8 边形。修前修后一眼能看出来（对照用 `--smoothcornerpx 0`）。
        var sparse = new List<Vector2>();
        for (int i = 0; i <= 8; i++)
        {
            float a = MathF.PI * i / 8f;
            sparse.Add(new Vector2(MathF.Cos(a) * 150f, -MathF.Sin(a) * 150f));
        }
        Add(sparse, ox + 1120f, oy + 380f);

        // ⑧ **真机复现**（用户 2026-10-07 的实测数据）：「ink 关、快速画圆」那一档
        //（`压感=False 活笔=False 点 11 均距 38.9px 总长 389px`）。
        //
        // 关键算术：**圆上每个顶点的转角 = 360°/段数**，与半径无关。
        //   11 点 → 10 段 → **36°/顶点**，刚好越过 35° 阈值；
        //   ink 开的 36 点 → 35 段 → 10.3°/顶点，差得远。
        // 所以这一条要复现的是"**局部判据①在稀采样下把光滑圆弧判成角点**"，
        // 和用例⑦（那条是窗口判据③越界）是**两个不同的毛病**。
        var realcase = new List<Vector2>();
        {
            const int nSeg = 10;                 // 11 个点 = 10 段
            float R = 389f / (2f * MathF.PI);    // 总长 389px → 半径 ≈ 61.9px
            // **必须绕成接近闭合的一圈**：11 点绕 355° → 每段 35.5°，刚好越过
            // 35° 阈值（局部判据①）。绕得少一点（比如半圈）每段只有 17°，
            // 复现不出来——第一版就是这么做的，出图是光滑的，白测一轮。
            for (int i = 0; i <= nSeg; i++)
            {
                float a = 355f * MathF.PI / 180f * i / nSeg - 355f * MathF.PI / 180f * 0.5f;
                realcase.Add(new Vector2(MathF.Cos(a) * R, -MathF.Sin(a) * R));
            }
        }
        Add(realcase, ox + 420f, oy + 760f);

        // ⑨ **真机复现·椭圆**（用户 2026-10-07 第二轮数据）：他的形状不是圆，是
        //    周长 515~564px 的**椭圆**（≈200×150），10~14 个点、点距 40~60px。
        //
        //    为什么必须单列：椭圆上**转角是变化的**——端部曲率半径 = b²/a ≈ 56px、
        //    侧面 = a²/b ≈ 133px，所以端部每顶点转角 ≈48°、侧面只有 ≈20°，
        //    中间连续过渡。任何"相邻差"式的判据如果容差取小了，端部照样被切成角
        //    ——第一版连续性容差 12° 就是这么栽的（圆的转角处处相等，能过；
        //    椭圆过不了）。
        var ellipse = new List<Vector2>();
        {
            const float a = 100f, b = 75f;       // ≈ 周长 553px，和用户实测一致
            const int n = 12;                    // 12 个点
            // 按**弧长均匀**取样：等弧长行走（手画时点的分布接近这个，
            // 不是按参数角均匀——按角均匀会让端部点变稀，测不出真实分布）
            float perimeter = 0f;
            for (int k = 0; k < 720; k++)
            {
                float t0 = k * MathF.PI / 360f, t1 = (k + 1) * MathF.PI / 360f;
                float s0 = MathF.Sqrt(a * a * MathF.Sin(t0) * MathF.Sin(t0) + b * b * MathF.Cos(t0) * MathF.Cos(t0));
                float s1 = MathF.Sqrt(a * a * MathF.Sin(t1) * MathF.Sin(t1) + b * b * MathF.Cos(t1) * MathF.Cos(t1));
                perimeter += (s0 + s1) * 0.5f * (MathF.PI / 360f);
            }
            float step = perimeter / n;
            ellipse.Add(new Vector2(a, 0f));
            float theta = 0f;
            for (int i = 1; i <= n; i++)
            {
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    float d = 0.0005f;
                    acc += s * d;
                    theta += d;
                }
                float ang = theta - MathF.PI * 0.5f;   // 让起笔落在端部
                ellipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(ellipse, ox + 800f, oy + 760f);

        // ⑩ **真机复现·不均匀点距**（用户 2026-10-07 第三轮数据）：
        //    他实测的顶点转角序列是 `51 18 17 24 30 31 37 22 41 35 27` /
        //    `37 42 35 70 27 13 23 69 60 16` —— **同一个光滑形状上，转角在 13°~70° 之间跳**。
        //
        //    为什么：转角 ≈ **点距 / 局部曲率半径**。手快速画时点距不均匀
        //    （60Hz 采样 + 手抖），于是转角也跟着跳。**这不代表形状有角。**
        //    任何"转角 ≥ 固定绝对值就算角"的判据都会被它骗到 —— 这就是前几版反复栽的地方。
        //
        //    这条用例就是用"椭圆 + 均匀弧长点 + 沿路径抖动 ±50% 点距"复现那个分布。
        var jitterEllipse = new List<Vector2>();
        {
            const float a = 110f, b = 78f;
            const int n = 12;
            var rnd = new Random(20261007);
            float theta = 0f;
            jitterEllipse.Add(new Vector2(a, 0f));
            for (int i = 1; i <= n; i++)
            {
                // 基准步长按周长均分，再乘 0.5~1.6 的抖动（复现 60Hz 快速手画的点距分布）
                float baseStep = 553f / n;
                float step = (float)(baseStep * (0.5 + rnd.NextDouble() * 1.1));
                float acc = 0f;
                while (acc < step && theta < MathF.PI * 2f)
                {
                    float s = MathF.Sqrt(a * a * MathF.Sin(theta) * MathF.Sin(theta)
                                       + b * b * MathF.Cos(theta) * MathF.Cos(theta));
                    acc += s * 0.0005f;
                    theta += 0.0005f;
                }
                float ang = theta - MathF.PI * 0.5f;
                jitterEllipse.Add(new Vector2(MathF.Cos(ang) * a, -MathF.Sin(ang) * b));
            }
        }
        Add(jitterEllipse, ox + 1300f, oy + 760f);

        // ⑪ **点密度对照**（用户 2026-10-07 问"raw input 值不值"）：
        //    同一个椭圆、同一条弧长路径，按两种点距各画一条：
        //      · 左边 13 点 = 关 ink 时的真实点距（~68Hz × 2900px/s ≈ 43px 一点）
        //      · 右边 33 点 = 接上 raw input 之后的点距（~170Hz ≈ 17px 一点）
        //    左边是"我们现在能拿到的"，右边是"raw input 能拿到的"。
        //    两条都过同一套曲线代码，差别**只在采样密度**——所以这张图直接回答
        //    "多出来的那 60% 输入到底买到了什么"。
        void DensePair(float dx, int n, bool dots)
        {
            var pts = new List<Vector2>();
            const float a2 = 120f, b2 = 88f;
            for (int i = 0; i <= n; i++)
            {
                float ang = MathF.PI * 1.9f * i / n - MathF.PI * 0.95f;
                pts.Add(new Vector2(MathF.Cos(ang) * a2, -MathF.Sin(ang) * b2));
            }
            Add(pts, dx, oy + 1180f, dots);
        }
        DensePair(ox + 260f, 13, dots: true);    // 现在（关 ink 的真实密度）
        DensePair(ox + 760f, 33, dots: true);    // raw input 之后

        string dir = Path.GetDirectoryName(Path.GetFullPath(path));
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string off = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-off" + ext);
        string on = Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, name + "-on" + ext);

        // 折线那张：先关掉开关，整层重画（版本号会顶掉每条笔的几何缓存）
        bool wasEnabled = StrokeSmoothing.Enabled;
        StrokeSmoothing.SetEnabled(false);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok1 = ScreenProbe.SaveBmp(off, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);

        // 曲线那张：打开开关，同一批笔重画一遍
        StrokeSmoothing.SetEnabled(true);
        Doc.InvalidateAll();
        SettleFrames(300);
        bool ok2 = ScreenProbe.SaveBmp(on, (int)_virtualX, (int)_virtualY, _virtualW, _virtualH);
        StrokeSmoothing.SetEnabled(wasEnabled);     // 出完图恢复原状态，别把后面的路径带偏

        Console.WriteLine(ok1 ? $"  折线：{off}" : "  折线图保存失败");
        Console.WriteLine(ok2 ? $"  曲线：{on}" : "  曲线图保存失败");
        Console.WriteLine();
        _quit = true;
    }
    private static (int count, float maxDist, float x, float y) DiffFarFromTip(
        byte[] a, byte[] b, int w, int h, int ox, int oy, float tipX, float tipY, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = MathF.Sqrt((px - tipX) * (px - tipX) + (py - tipY) * (py - tipY));
                if (dist <= minDist) continue;      // 笔尖附近本来就是新墨，允许变
                count++;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy);
    }

    /// <summary>
    /// 滚动比对：两张同尺寸图按"内容整体上移 <paramref name="shift"/> 像素"逐像素对
    /// （a[y] 应等于 b[y+shift]）。返回不匹配的像素数（亮度差 &gt; 24）。
    /// 用来找"滚动时某条横带没更新"——那会表现成一整行不匹配。
    /// </summary>
    private static int CountShiftMismatch(byte[] a, byte[] b, int w, int h, int shift)
    {
        if (shift <= 0 || shift >= h) return int.MaxValue;
        int bad = 0;
        for (int y = 0; y + shift < h; y++)
        {
            int ra = y * w * 4, rb = (y + shift) * w * 4;
            for (int x = 0; x < w; x++)
            {
                int oa = ra + x * 4, ob = rb + x * 4;
                int d = Math.Abs(a[oa] - b[ob]) + Math.Abs(a[oa + 1] - b[ob + 1]) + Math.Abs(a[oa + 2] - b[ob + 2]);
                if (d > 24) bad++;
            }
        }
        return bad;
    }
    private static int FirstLineCenter(byte[] cap, int w, int h, int x)
    {
        if (cap == null) return -1;
        int run = -1;
        for (int y = 0; y < h; y++)
        {
            int o = (y * w + x) * 4;
            bool dark = cap[o] < 225 && cap[o + 1] < 225 && cap[o + 2] < 225;
            if (dark) { if (run < 0) run = y; }
            else if (run >= 0) return (run + y - 1) / 2;
        }
        return -1;
    }

    /// <summary>两张同尺寸 BGRA 图的差异像素数（亮度差 &gt; 24）。</summary>
    private static int DiffCount(byte[] a, byte[] b, int w, int h)
    {
        int n = 0;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
            if (d > 24) n++;
        }
        return n;
    }

    /// <summary>
    /// 两张同尺寸 BGRA 图的差异，只看**离折线 <paramref name="path"/> 的前
    /// <paramref name="pathCount"/> 个点 &gt; minDist 的那些像素**。
    /// 返回（这样的像素数、其中最远那个到路径的距离、坐标）。
    /// </summary>
    private static (int count, float maxDist, float x, float y,
                    int minX, int minY, int maxX, int maxY) DiffFarFromPath(
        byte[] a, byte[] b, int w, int h, int ox, int oy,
        List<Vector2> path, int pathCount, float minDist)
    {
        int count = 0;
        float maxDist = 0f, wx = 0f, wy = 0f;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int j = 0; j < h; j++)
        {
            int row = j * w * 4;
            for (int i = 0; i < w; i++)
            {
                int o = row + i * 4;
                int d = Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]);
                if (d <= 24) continue;
                float px = ox + i, py = oy + j;
                float dist = DistToPath(path, pathCount, px, py);
                if (dist <= minDist) continue;      // B 自己画的 / 盖住的，允许变
                count++;
                if (i < minX) minX = i;
                if (i > maxX) maxX = i;
                if (j < minY) minY = j;
                if (j > maxY) maxY = j;
                if (dist > maxDist) { maxDist = dist; wx = px; wy = py; }
            }
        }
        return (count, maxDist, wx, wy, minX, minY, maxX, maxY);
    }

    /// <summary>点到折线（前 count 个点）的最短距离。</summary>
    private static float DistToPath(List<Vector2> path, int count, float x, float y)
    {
        int n = Math.Min(count, path.Count);
        if (n <= 0) return float.MaxValue;
        if (n == 1) return Vector2.Distance(path[0], new Vector2(x, y));
        float best = float.MaxValue;
        for (int i = 1; i < n; i++)
        {
            float d = DistToSegment(path[i - 1].X, path[i - 1].Y, path[i].X, path[i].Y, x, y);
            if (d < best) best = d;
        }
        return best;
    }

    private static float DistToSegment(float ax, float ay, float bx, float by, float px, float py)
    {
        float vx = bx - ax, vy = by - ay;
        float wx = px - ax, wy = py - ay;
        float len2 = vx * vx + vy * vy;
        float t = len2 <= 1e-6f ? 0f : Math.Clamp((wx * vx + wy * vy) / len2, 0f, 1f);
        float dx = wx - vx * t, dy = wy - vy * t;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
    private static (float[] width, float[] center) InkProfile(byte[] bgra, int w, int h)
    {
        var width = new float[w];
        var center = new float[w];
        for (int i = 0; i < w; i++)
        {
            int min = int.MaxValue, max = -1;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                if (bgra[o] + bgra[o + 1] + bgra[o + 2] < 600)
                {
                    if (j < min) min = j;
                    if (j > max) max = j;
                }
            }
            width[i] = max >= 0 ? max - min + 1 : -1f;
            center[i] = max >= 0 ? (min + max) * 0.5f : -1f;
        }
        return (width, center);
    }

    /// <summary>
    /// `--tailjumptest`：**预测尾"突突突往外跳"的专项检测**（2026-09-28 用户实测：
    /// "鼠标模式下手写板写字，末端总会突突突往外跳，Ink 开了就没有了"）。
    ///
    /// 症状的机理：鼠标模式下没有委托轨迹，预测尾由我们自己画；而鼠标/兼容模式的输入
    /// 是**突发**的（报几个点、停一下、再报几个点）。来一阵好数据 → 末速度算得大 →
    /// 尾巴甩到前面；间隔一超 `MaxGapMs`(20ms) → 速度清零 → 尾巴没了；再来数据又甩出来。
    ///
    /// 判据是**笔尖的逐帧轨迹**（品红墨的最右列）：
    ///   · `往前跳的最大幅度`：一帧里笔尖突然前进多少；
    ///   · `往后退的次数 / 最大后退`：尾巴消失时笔尖会**缩回去**——这就是"突突"的正身。
    /// 对照组 `--off` 应当明显更稳（这就是这条测试的自证）。
    /// 测试**自己摆状态**（不依赖启动默认值）：默认测"开预测"，`--off` 测对照。
    /// </summary>
    // [重启 2026-10-08] B4 预测尾"突突跳"检测（老系统 2026-10-05 停用，本次接回新尾）：
    // 新尾的纪律（长度限速 + 断笔后缓慢回收）就是对着下面这段"机理"修的。
    private void TailJumpTest(bool noisy = false, bool predictOff = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== 预测尾“突突跳”检测（合成鼠标，突发节奏）===");
        // 测试自己摆状态（不依赖启动默认值）：默认测"开预测"，`--off` 测对照
        bool predOn = !predictOff;
        global::InkEngine.InkEngine.PredictTailEnabled = predOn;
        Console.WriteLine($"  预测={(predOn ? "开" : "关（对照）")}；节奏={(noisy ? "忽大忽小 + 间隔不齐（像真鼠标）" : "固定步长 + 每 4 点一停")}");

        // ── B4.2（2026-10-08 真机反馈）：**急转**时不许留"旧方向"的尾（**纯引擎，不走屏幕/合成鼠标**，
        //    所以放在 SkipIfNoSyntheticInput 之前——鼠标忙的时候这项也照样能跑）───────────
        //   用户（学校大屏 + 手写板）："写快的时候急转/急拐弯，偶尔会跳一下"。
        //   机理：旧方向的尾留在屏幕上、再按慢速率缩回 = 那一"跳"。
        //   现在两道防线：预测器自己的**方向自适应弧长收缩**（2026-10-15 线性预测内置；
        //   急转那一帧的尾长按方向不稳度自动缩）+ 失效快收（n=0 时 0.72px/ms）。
        //   快写向右 → 急转 75° → 两帧内"沿旧方向伸出"要 ≤4px。
        if (predOn)
        {
            Doc.Clear();
            Doc.ClearHistory();
            float bx = 400f, by = 300f;
            DwellBeginForTest(bx, by);
            double t0 = NowMs;
            for (int i = 1; i <= 20; i++)          // 快写：4ms 一点、每点 12px（=3px/ms，正高速）
            {
                NowMs = t0 + i * 4;
                DwellMoveForTest(bx + i * 12f, by);
            }
            UpdatePredictTail(); UpdatePredictTail(); UpdatePredictTail();
            bool hadTail = PredictTailActive;
            float rad = 75f * MathF.PI / 180f;     // 急转 75°：向下
            for (int k = 1; k <= 2; k++)
            {
                NowMs = t0 + (20 + k) * 4;
                DwellMoveForTest(bx + 240f + 12f * k * MathF.Cos(rad),
                                       by + 12f * k * MathF.Sin(rad));
            }
            UpdatePredictTail(); UpdatePredictTail();
            float lastX = bx + 240f + 24f * MathF.Cos(rad);
            float lead = float.MinValue;
            foreach (var pt in PredictTailPoints) lead = MathF.Max(lead, pt.X - lastX);
            if (lead == float.MinValue) lead = 0f; // 尾收干净了：更谈不上"旧方向残留"
            bool turnOk = hadTail && lead <= 4f;
            Console.WriteLine($"  急转 75°：转前尾={(hadTail ? "有" : "无")}；"
                              + $"转后沿旧方向最远伸出 {lead:F1}px（要求 ≤4）");
            Console.WriteLine(turnOk
                ? "  PASS: 急转处没有'旧方向'的尾残留"
                : $"  FAIL: 急转处残留旧方向的尾 {lead:F1}px（或转前尾就没长出来）");
            DwellEndForTest();
            Doc.Clear();
            Doc.ClearHistory();
        }

        if (SkipIfNoSyntheticInput("预测尾跳跃检测（需要合成鼠标）")) { _quit = true; return; }

        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Pen;
        PassThrough = false;
        SetColorFromUi(new Color4(1f, 0f, 1f, 1f));      // 品红：和光标/白底分得开
        Doc.InvalidateAll();
        SettleFrames(200);

        float y0 = _virtualY + _virtualH * 0.5f;
        float x0 = _virtualX + 400f;
        const int N = 60;

        // 先把这一步的"步长 + 间隔"定下来（确定性）：忽大忽小的步长是**鼠标加速**的样子，
        // 不齐的间隔是**报点节奏**的样子，两者一起才逼得出"尾巴一出一进"。
        var rnd = new Random(20260928);
        var steps = new float[N];
        var gaps = new int[N];
        float total = 0f;
        for (int i = 0; i < N; i++)
        {
            steps[i] = noisy ? 2f + (float)rnd.NextDouble() * 32f : 30f;
            gaps[i] = noisy ? 6 + rnd.Next(36) : (i % 4 == 3 ? 60 : 8);
            total += steps[i];
        }

        int bandW = (int)(total + 220f), bandH = 120;
        int bandX = (int)(x0 - 60f), bandY = (int)(y0 - bandH * 0.5f);
        var buf = new byte[bandW * bandH * 4];

        SendMouse((int)x0, (int)y0, 0);
        SettleFrames(120);
        SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);

        var tip = new List<(double t, float x)>();
        var sentAt = new List<(double t, float x)>();   // 每个采样时刻"最后报的坐标"（算前沿-报点差）
        int sent = 0;
        float cx = x0;
        double nextMove = NowMs;
        int tailFrames = 0;                      // 诊断：预测尾处于激活状态的帧数
        while (!_quit && sent < N)
        {
            if (NowMs >= nextMove)
            {
                cx += steps[sent];
                sent++;
                SendMouse((int)cx, (int)y0, 0);
                nextMove = NowMs + gaps[sent - 1];
            }
            SettleFrames(1);                     // 一拍 ≈ 一帧：保证"报点间隔"真的落在
                                                 // MaxGapMs(40ms) 以内——不然预测器每拍都被
                                                 // 断笔重置，尾巴永远长不出来，测试什么都测不到
            if (PredictTailActive) tailFrames++;
            if (ScreenProbe.CaptureRegionInto(buf, bandX, bandY, bandW, bandH))
            {
                int col = MaxInkColumn(buf, bandW, bandH, minRun: 5);
                tip.Add((NowMs, col >= 0 ? bandX + col : float.NaN));
                sentAt.Add((NowMs, cx));
            }
        }
        SendMouse((int)cx, (int)y0, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        float maxFwd = 0f, maxBack = 0f;
        int backCount = 0, solidBack = 0, measured = 0;
        for (int i = 1; i < tip.Count; i++)
        {
            if (float.IsNaN(tip[i].x) || float.IsNaN(tip[i - 1].x)) continue;
            float d = tip[i].x - tip[i - 1].x;
            measured++;
            if (d > maxFwd) maxFwd = d;
            if (d < 0f) { backCount++; if (-d > maxBack) maxBack = -d; }
            if (d < -3f) solidBack++;          // 明确回退：>3px（物理像素）= 尾整段消失再回来的量级
        }
        float lastSent = cx;
        float lastTip = 0f;
        for (int i = tip.Count - 1; i >= 0; i--) if (!float.IsNaN(tip[i].x)) { lastTip = tip[i].x; break; }

        Console.WriteLine($"  采到 {tip.Count} 帧（有效 {measured} 对），画到 x={lastSent:F0}，"
                          + $"最后笔尖 x={lastTip:F0}（落后 {lastSent - lastTip:F0}px）");
        Console.WriteLine($"  往前跳：最大 {maxFwd:F0}px/帧");
        Console.WriteLine($"  往后退：{backCount} 次，最大 {maxBack:F0}px（其中 >3px 的明确回退 {solidBack} 次）  ← “突突”的正身");
        // [B4 新判据] 墨前沿 − 最后报点：量"尾有没有真的伸出去"（恒定的一截在差分里看不见）。
        double leadSum = 0; int leadN = 0; float leadMax = float.MinValue;
        for (int i = 0; i < tip.Count && i < sentAt.Count; i++)
        {
            if (float.IsNaN(tip[i].x)) continue;
            float lead = tip[i].x - sentAt[i].x;
            leadSum += lead; leadN++;
            if (lead > leadMax) leadMax = lead;
        }
        if (leadN > 0)
            Console.WriteLine($"  墨前沿−报点：平均 {leadSum / leadN:F1}px / 最大 {leadMax:F1}px"
                              + "（开预测应明显偏正；它就是‘尾’在屏幕上的样子）");
        Console.WriteLine($"  预测尾：{tailFrames}/{tip.Count} 帧处于激活状态");
        // 判据（2026-10-15 复核，含 v6 基线对照）：
        //  · 默认节奏（固定 30px 步长、每 4 点一停）：回缩次数是"突突跳"的正身，必须 ≤2
        //    （v6 与线性预测实测都是 **0**）；单帧前跳上限 = 步长 30 + 弧长顶 6 + 4px 余量 = 40
        //    （实测 36.0px，正好顶设计上限）。
        //  · `--noisy` 节奏（间隔 6~41ms、步长 2~34px 随机）：小回退（≤3px）是**尾长跟着
        //    速度抖动调整**的固有现象，不是"突突"——v6 基线 8 次、线性预测 6 次，最大都只有
        //    3px。所以这一档只数 **>3px 的明确回退**（尾整段消失再回来的量级，应 ≤2）；
        //    单帧前跳放宽到 42（步长上限 34 + 弧长顶 6 = 40 理论值，实测 39）。
        bool ok = noisy
            ? (solidBack <= 2 && maxFwd <= 42f)
            : (backCount <= 2 && maxFwd <= 40f);
        Console.WriteLine(ok
            ? "  PASS: 笔尖轨迹是稳的（没有反复回缩）"
            : $"  FAIL: 笔尖轨迹不稳（回缩 {backCount} 次（明确 {solidBack}）/ 单帧最大 {maxFwd:F0}px）");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 一块 BGRA 图里**最右边**那一列"够粗的品红墨"（≥ <paramref name="minRun"/> 个连续像素）。
    /// 要求"够粗"是为了把落点光标环排除掉（那是细线，竖着切只有两三个像素）。
    /// </summary>
    private static int MaxInkColumn(byte[] bgra, int w, int h, int minRun)
    {
        for (int i = w - 1; i >= 0; i--)
        {
            int run = 0;
            for (int j = 0; j < h; j++)
            {
                int o = (j * w + i) * 4;
                bool magenta = bgra[o] > 200 && bgra[o + 1] < 90 && bgra[o + 2] > 200;
                if (magenta) { if (++run >= minRun) return i; }
                else run = 0;
            }
        }
        return -1;
    }

    private void CurveShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        float x0 = _virtualX + 200f, y0 = _virtualY + 180f;
        const float pitch = 620f;                            // 格子间距（横竖一样，看着是网格）

        // 造一条曲线：只有 BeginShapeAt 铺的那一个占位点，之后交给 Set*Box
        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }
        // 每格的左上角
        float CellX(int col) => x0 + col * pitch;
        float CellY(int row) => y0 + row * pitch;

        // ---- 第一行：抛物线的四种开口（**先选朝向，再用"顶点 + 经过点"两点画**） ----
        // 这就是用户 2026-09-20 定的口径：方向是"选"出来的（CurveAxis），大小由"经过那个点"反解。
        var dirs = new[] { CurveAxis.OpenUp, CurveAxis.OpenDown, CurveAxis.OpenRight, CurveAxis.OpenLeft };
        for (int col = 0; col < dirs.Length; col++)
        {
            float cx = CellX(col), cy = CellY(0);
            var v = new Vector2(cx + 280f, cy + 280f);
            var s = Make(Tool.Parabola, StrokeKind.Parabola, v.X, v.Y);
            s.CurveAxis = dirs[col];                              // ① 先选开口方向
            s.SetParabolaVertex(v.X, v.Y);                        // ② 定顶点
            // ③ 定"曲线经过的点"：取 `u = 1` 处的曲线点（`s = p/2、t = p`），
            //    反解出来正好是 p = 140（见 Stroke.ParabolaPThroughPoint），一个格子装得下。
            var (dir, perp) = Stroke.ParabolaBasis(dirs[col]);
            var q = v + perp * 140f + dir * 70f;
            s.SetParabolaThroughPoint(q.X, q.Y);
            Doc.AddStroke(s);
        }

        // ---- 第二行：双曲线两个方向 ＋ 正弦 / 余弦各一个周期 ----
        // 双曲线按真实的**两步**出图（第一步拖出**渐近线框**、第二步定"曲线过哪个点"），
        // 这样图上这两条跟老师真画出来的完全一致（朝向也是那一步定的）。
        // 渐近线框是**拖到哪就是哪**：`A = |dx|、B = |dy|`（2026-09-20 改成不再打对折）。
        var hX = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(0) + 280f, CellY(1) + 280f);
        hX.SetHyperbolaFromAsymptote(CellX(0) + 280f, CellY(1) + 280f,
                                     CellX(0) + 280f + 300f, CellY(1) + 280f + 120f, 8f);   // 斜率 0.4
        hX.SetHyperbolaThroughPoint(CellX(0) + 280f + 300f, CellY(1) + 280f + 104f);
        Doc.AddStroke(hX);          // 点更横 → 焦点在 x 轴（左右双曲线）

        var hY = Make(Tool.Hyperbola, StrokeKind.Hyperbola, CellX(1) + 280f, CellY(1) + 280f);
        hY.SetHyperbolaFromAsymptote(CellX(1) + 280f, CellY(1) + 280f,
                                     CellX(1) + 280f + 120f, CellY(1) + 280f + 300f, 8f);   // 斜率 2.5
        hY.SetHyperbolaThroughPoint(CellX(1) + 280f + 104f, CellY(1) + 280f + 300f);
        Doc.AddStroke(hY);          // 点更竖 → 焦点在 y 轴（上下双曲线）

        // 正弦：起点在轴上（第一个零点），**往上拖 = 先上后下**（就是课本的 y = sin x）。
        // ⚠ 2026-09-20 第十六批：**框宽就是一个周期**（用户："正弦和余弦用一个周期的图"）——
        // 所以这一张拖出来的是**一个周期**（300 宽、A = 75）。多周期那件事在下一格「波浪线」。
        // ⚠ 这一行的**纵向位置都写成 `CellY(1) + 本行内偏移`**：格子间距是 620，
        // 第三行（立体）从 `CellY(1) + 600` 就开始了 —— 偏移超过 600 就压在立体上了
        //（2026-09-20 第十六批把波浪线摆到 +760、当场压住圆台和球，出图才发现）。
        float sinX = x0 + 1250f;
        var sin = Make(Tool.Sine, StrokeKind.Sine, sinX, CellY(1) + 300f);
        sin.SetWaveBox(sinX, CellY(1) + 300f, sinX + 300f, CellY(1) + 225f, 8f);   // dy = −75 → A = 75
        Doc.AddStroke(sin);

        // 余弦：起点在**峰顶**，**往下拖 = 从峰顶往下**（就是课本的 y = cos x）。
        // 同样是**一个周期**（纵拖 = 峰 → 谷 = 2A，所以同样 75 的振幅要拖 150 高）。
        float cosX = x0 + 1600f;
        var cos = Make(Tool.Cosine, StrokeKind.Cosine, cosX, CellY(1) + 225f);
        cos.SetWaveBox(cosX, CellY(1) + 225f, cosX + 300f, CellY(1) + 375f, 8f);
        Doc.AddStroke(cos);

        // 波浪线（第十六批）：**多周期的正弦波**——横向拖的是"要画多长"、周期由振幅定
        //（`T = 1 × A`，用户定的"振幅 = 一个周期"）。这里 A = 70、画 210 宽 → **3 个整周期**，
        // 正是用户要的"很多个周期的波浪线"。
        // 摆在正弦 / 余弦下面一点（同一行里错开，三张并排看"一个周期 vs 多周期"最直观）。
        float wavX = x0 + 1950f;
        var wav = Make(Tool.Wave, StrokeKind.Wave, wavX, CellY(1) + 400f);
        wav.SetWaveBox(wavX, CellY(1) + 400f, wavX + 210f, CellY(1) + 330f, 8f);
        Doc.AddStroke(wav);

        // 正切（2026-09-20 第十五批）：**一支**，按下 = 原点、拖出以它为中心的框
        //（横向 = 半支长、纵向 = 可视半高，两条渐近线就落在框的左右两边）。
        // ⚠ 视觉半高有**下限比例 3:1**（见 Stroke.TangentMinAspect）：这里故意只拖 60 高，
        // 画出来会被顶到 240 —— 图上要看的正是"随手一拖就贴着渐近线"这件事。
        float tanX = x0 + 2560f, tanY = CellY(1) + 330f;
        var tan = Make(Tool.Tangent, StrokeKind.Tangent, tanX, tanY);
        tan.SetTangentBox(tanX, tanY, tanX + 80f, tanY + 60f, 8f);
        Doc.AddStroke(tan);

        // ---- 第三行：立体图形（2026-09-20 第五批，照 InkClass 的 case 6/7）----
        // **旋转体那一族**：一次拖出**外接矩形**就成（椭圆由矩形派生，
        // 扁率 = `Stroke.SolidEllipseRatio`），被挡住的那半圈是**细虚线**（辅助几何槽）。
        // 位置贴着第三行的格线上沿：再往上抬就会和第二行（双曲线 / 正弦）叠在一起，
        // 再往下压出图（1800 高）就会被裁掉——两头都试过，这个位置刚好。
        //
        // ⚠ 这一行原来放的是**圆柱 / 圆锥 / 长方体 / 四面体**——后两个 2026-09-20 第十二批
        // 撤了面板入口（见 ShapeRows），所以这里换成**用户现在真能拖出来的四个**：
        // 圆柱 / 圆锥 / 圆台 / 球。（长方体 / 四面体的画法没删，`--curvetest` 里那些
        // 几何断言照旧在管着它们，旧板书也照样打开。）
        float sy0 = CellY(2) - 20f;
        var cyl = Make(Tool.Cylinder, StrokeKind.Cylinder, CellX(0) + 130f, sy0);
        cyl.SetSolidBox(CellX(0) + 130f, sy0, CellX(0) + 430f, sy0 + 350f);
        Doc.AddStroke(cyl);

        var cone = Make(Tool.Cone, StrokeKind.Cone, CellX(1) + 130f, sy0);
        cone.SetSolidBox(CellX(1) + 130f, sy0, CellX(1) + 430f, sy0 + 350f);
        Doc.AddStroke(cone);

        // 圆台（2026-09-20 第十三批）：同一个外接矩形，上底是一圈**小一圈**的椭圆。
        var cfz = Make(Tool.ConeFrustum, StrokeKind.ConeFrustum, CellX(2) + 130f, sy0);
        cfz.SetSolidBox(CellX(2) + 130f, sy0, CellX(2) + 430f, sy0 + 350f);
        Doc.AddStroke(cfz);

        // 球（第十四批）：半径 = min(半宽, 半高) 的内切正圆 ＋ 赤道椭圆（近侧实线）。
        // 这里**故意拖一个正方**（350×350）——球本来就该是个圆。
        var sph = Make(Tool.Sphere, StrokeKind.Sphere, CellX(3) + 130f, sy0);
        sph.SetSolidBox(CellX(3) + 130f, sy0, CellX(3) + 480f, sy0 + 350f);
        Doc.AddStroke(sph);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }

    /// <summary>
    /// 出图（2026-09-22）：**双曲线两档 ＋ 椭圆（带焦点）两档**摆成一张四格的图。
    /// 用法 `--conicshow [路径]`，默认 `reports/圆锥曲线-两档.bmp`。
    ///
    /// 为什么单开一张、不塞进 `--curveshow`：那一张的三行已经满了（第二行末尾离立体
    /// 只有 600 像素），塞进去会叠在一起（第十六批就这么出过一次错）。而这一批要看的
    /// 正是"**两档之间的差别**"——四格并排、同一套尺寸，一眼就能对比：
    ///   上排：双曲线**有 / 无**渐近线；下排：椭圆**有焦点三角形 / 只有焦点**。
    /// 椭圆的 P 用**默认位置**（椭圆左上方那个点，见 `Stroke.DefaultFocusPointU`）——
    /// 图上要看的正是"顶点**在椭圆上**、三角形在上半边"，和面板图标同一个位置。
    /// </summary>
    private void ConicShowcase(string path)
    {
        Console.WriteLine($"=== 出图：{path} ===");
        BoardOn = true;                          // 白底：不然桌面背景会混进画面
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);      // 板书的近黑色
        const float colPitch = 1440f, rowPitch = 720f;
        float x0 = _virtualX + 720f, y0 = _virtualY + 300f;

        Stroke Make(Tool tool, StrokeKind kind, float x, float y)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink, Width = 5f * DpiScale,
            };
            s.AddPoint(x, y, 1f, 0);
            return s;
        }

        // ---- 上排：双曲线两档（**同一个几何**，只差那两条虚线在不在）----
        // 画法按真实的两步来：第一步拖出**渐近线框**（+300, +120，斜率 0.4），
        // 第二步定"曲线经过哪个点"（更横 → 焦点在 x 轴）。
        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0;
            var h = Make(Tool.Hyperbola, StrokeKind.Hyperbola, cx, cy);
            h.SetHyperbolaFromAsymptote(cx, cy, cx + 300f, cy + 120f, 8f);
            h.SetHyperbolaThroughPoint(cx + 300f, cy + 104f);
            h.ShowAsymptotes = col == 0;                 // 左：有渐近线；右：无渐近线
            Doc.AddStroke(h);
        }

        // ---- 下排：椭圆（带焦点）两档（**同一个椭圆**，只差那两条边在不在）----
        // 中心 ＋ 外角点拖 300 × 160 → 宽椭圆，焦点在横轴上、离中心 ±254。
        // 左：有焦点三角形（**P 用默认位置**，也就是"顶点在椭圆上"的那个样子）；
        // 右：只有两个焦点。
        // ⚠ P 的默认角是**左上方**（120°，见 `Stroke.DefaultFocusPointU`）——图上看的就是
        // "三角形的顶点贴着椭圆那条线、在上半边"，和面板图标上画的是同一个位置。
        for (int col = 0; col < 2; col++)
        {
            float cx = x0 + col * colPitch, cy = y0 + rowPitch;
            var e = Make(Tool.ConicEllipse, StrokeKind.ConicEllipse, cx, cy);
            // ⚠ **不要用 `SetShapeBox`**：那是"三角形 / 平行四边形"的外框写法（会写三个顶点），
            // 而椭圆的定义是"**中心 ＋ 外角点**"两个点 —— 用错的话画出来是个又细又高的怪东西
            //（第一版出图时就是这么翻的：a 和 b 全错了）。正确写法是"起手点当中心、再拖出外角点"。
            e.SetEnd(cx + 300f, cy + 160f);
            e.FocusTriangle = col == 0;
            Doc.AddStroke(e);
        }

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok ? $"  已保存 {path}" : "  保存失败");
        Console.WriteLine();
        _quit = true;
    }
    private void PrismShowcase(string path)
    {
        BoardOn = true;                       // 白底：虚线压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        var ink = new Color4(0.11f, 0.12f, 0.15f, 1f);

        void Put(Tool tool, StrokeKind kind, int sides, float bx, float by, float dx, float dy)
        {
            var s = new Stroke
            {
                Tool = tool, Kind = kind, Color = ink,
                Width = 6f * DpiScale, PrismSides = sides,
            };
            s.AddPoint(bx, by, 1f, 0);
            s.SetPrismBase(bx, by, bx + 340f, by + 140f);        // 底面外接框 340×140（压扁比 ≈ 0.41）
            s.SetPrismApex(bx + 170f + dx, by + 70f + dy);       // 顶上那个中心（dx/dy 决定直还是斜）
            Doc.AddStroke(s);
        }

        // 三行：棱柱 / 棱锥 / 棱台，每行都是**三/四/五/六**、都是直的（顶上那个中心正对底心上方 240）
        var rows = new (Tool tool, StrokeKind kind)[]
        {
            (Tool.Prism, StrokeKind.Prism),
            (Tool.Pyramid, StrokeKind.Pyramid),
            (Tool.Frustum, StrokeKind.Frustum),
        };
        float[] colX = { 200f, 820f, 1440f, 2060f };
        int[] colSides = { 3, 4, 5, 6 };
        for (int r = 0; r < rows.Length; r++)
        {
            float by = _virtualY + 420f + r * 400f;
            for (int c = 0; c < colX.Length; c++)
                Put(rows[r].tool, rows[r].kind, colSides[c], _virtualX + colX[c], by, 0f, -240f);
        }

        // 最后一行：**直 vs 斜**，三族各一对（同一个四棱，斜的那个顶心往右挪 200）
        float lastY = _virtualY + 420f + rows.Length * 400f;
        var pairs = new (Tool tool, StrokeKind kind, float x, bool slant)[]
        {
            (Tool.Prism, StrokeKind.Prism, 200f, false),
            (Tool.Prism, StrokeKind.Prism, 760f, true),
            (Tool.Pyramid, StrokeKind.Pyramid, 1320f, false),
            (Tool.Pyramid, StrokeKind.Pyramid, 1880f, true),
            (Tool.Frustum, StrokeKind.Frustum, 2440f, false),
        };
        foreach (var (tool, kind, x, slant) in pairs)
            Put(tool, kind, 4, _virtualX + x, lastY,
                slant ? 200f : 0f, slant ? -200f : -240f);

        Doc.InvalidateAll();
        SettleFrames(800);

        bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY, 2880, 1800);
        Console.WriteLine(ok
            ? $"  已保存 {path}（上三行：三/四/五/六 的 棱柱 / 棱锥 / 棱台，都是直的；末行：直 vs 斜）"
            : "  保存失败");
        _quit = true;
    }

    private void RenderInto(ID2D1Bitmap1 target, int strokeCount)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (n++ >= strokeCount) break;
            _windows[0].DrawStrokeForTest(s);
        }
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        if (hr.Failure) Console.WriteLine("  RenderInto EndDraw 失败: " + hr.Description);
        try { ctx.Flush(out _, out _); } catch { /* 见报告说明：CreateBitmap 目标上 Flush 会报状态错误，四种分辨率一致，不影响横向对比 */ }
        ctx.Target = null;
    }

    private void PatchInto(ID2D1Bitmap1 target, RectF r)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-_virtualX, -_virtualY);
        ctx.PushAxisAlignedClip(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY),
            Vortice.Direct2D1.AntialiasMode.Aliased);
        _windows[0].ClearRectForTest(r);
        int n = 0;
        foreach (var s in Doc.Strokes)
        {
            if (!s.IntersectsRect(r)) continue;
            _windows[0].DrawStrokeForTest(s);
            if (++n > 400) break;
        }
        ctx.PopAxisAlignedClip();
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr2 = ctx.EndDraw();
        if (hr2.Failure) Console.WriteLine("  PatchInto EndDraw 失败: " + hr2.Description);
        try { ctx.Flush(out _, out _); } catch { }
        ctx.Target = null;
    }
    private IntPtr _syntheticPen;

    // ---- 合成触摸（自检注入用）------------------------------------------
    //
    // 为什么触摸需要单独一份：**POINTER_TYPE_INFO 真实定义里中间是一个 union，
    // 最大成员是 POINTER_TOUCH_INFO（144 字节）**，而我们那个结构体只声明了 pen 分支
    // （120 字节）加 24 字节填充 → 152 字节，与真实步长（4 + 4 对齐 + 144）一致；
    // 而且两个分支的**开头都是同一份 POINTER_INFO**，
    // 所以按 pen 的字段名填、把 type 设成 PT_TOUCH 就能造出一个触摸触点。
    private IntPtr _syntheticTouch;

    private bool EnsureSyntheticTouch()
    {
        if (_syntheticTouch != IntPtr.Zero) return true;
        // maxCount = 10：触摸**可以同时有多个触点**——"第二根手指守卫"、≥3 指擦、
        // 双指手势的自检都要一次注入 2~5 个触点。
        _syntheticTouch = Native.CreateSyntheticPointerDevice(
            Native.PT_TOUCH, 10, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticTouch != IntPtr.Zero;
    }

    /// <summary>
    /// 注入合成触摸（**不带面积**）。`points` 是"这一刻**所有**按在屏上的触点"，
    /// 下标就是触点序号（系统按 pointerId 跟踪，所以每次调用都要把还按着的触点一起带上）。
    /// `contact=false` 表示全部抬起。
    /// </summary>
    private void SendTouches(bool contact, params (float x, float y)[] points)
    {
        var sized = new (float x, float y, float size)[points.Length];
        for (int i = 0; i < points.Length; i++) sized[i] = (points[i].x, points[i].y, 0f);
        SendTouchesSized(contact, sized);
    }

    /// <summary>
    /// 注入合成触摸（**可带接触面积**）：`size` = 接触矩形边长（物理像素），0 = 不报面积。
    /// 手掌擦的判据靠它（见 Touch.cs 的自适应基线），所以"手掌"用例必须能造出大面积。
    ///
    /// ⚠ 步长 = **托管结构体大小**（= 原生步长 152，见 Native 的注释）；
    /// `rcContact` 等偏移**运行时用 `Marshal.OffsetOf` 算**——不写魔法数字。
    /// （历史坑：漏掉 `touchFlags/touchMask` 时按 144 排、第二个触点整体错位丢失。）
    /// </summary>
    private unsafe void SendTouchesSized(bool contact, params (float x, float y, float size)[] points)
    {
        int stride = Marshal.SizeOf<Native.POINTER_TYPE_INFO>();
        int unionOff = (int)Marshal.OffsetOf<Native.POINTER_TYPE_INFO>(nameof(Native.POINTER_TYPE_INFO.pen));
        int maskOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.touchMask));
        int rcOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContact));
        int rcRawOff = unionOff + (int)Marshal.OffsetOf<Native.POINTER_TOUCH_INFO>(nameof(Native.POINTER_TOUCH_INFO.rcContactRaw));

        int n = Math.Max(1, points.Length);
        IntPtr buf = Marshal.AllocHGlobal(stride * n + 16);
        try
        {
            for (int i = 0; i < points.Length; i++)
            {
                var one = new Native.POINTER_TYPE_INFO();
                one.type = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerType = Native.PT_TOUCH;
                one.pen.pointerInfo.pointerId = (uint)(i + 1);
                one.pen.pointerInfo.pointerFlags =
                    Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE
                    | (contact ? Native.POINTER_FLAG_INCONTACT : 0u)
                    | (i == 0 ? Native.POINTER_FLAG_PRIMARY : 0u);
                one.pen.pointerInfo.ptPixelLocationX = (int)points[i].x;
                one.pen.pointerInfo.ptPixelLocationY = (int)points[i].y;
                one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

                IntPtr dst = buf + i * stride;
                Marshal.StructureToPtr(one, dst, false);
                if (points[i].size > 0f)
                {
                    // 报面积：touchMask 置位 + rcContact / rcContactRaw 都填矩形。
                    float half = points[i].size * 0.5f;
                    int l = (int)(points[i].x - half), t = (int)(points[i].y - half);
                    int r = (int)(points[i].x + half), b = (int)(points[i].y + half);
                    Marshal.WriteInt32(dst + maskOff, (int)Native.TOUCH_MASK_CONTACTAREA);
                    Marshal.WriteInt32(dst + rcOff + 0, l);
                    Marshal.WriteInt32(dst + rcOff + 4, t);
                    Marshal.WriteInt32(dst + rcOff + 8, r);
                    Marshal.WriteInt32(dst + rcOff + 12, b);
                    Marshal.WriteInt32(dst + rcRawOff + 0, l);
                    Marshal.WriteInt32(dst + rcRawOff + 4, t);
                    Marshal.WriteInt32(dst + rcRawOff + 8, r);
                    Marshal.WriteInt32(dst + rcRawOff + 12, b);
                }
            }
            Native.InjectSyntheticPointerInput(_syntheticTouch, buf, (uint)points.Length);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool EnsureSyntheticPen()
    {
        if (_syntheticPen != IntPtr.Zero) return true;
        _syntheticPen = Native.CreateSyntheticPointerDevice(Native.PT_PEN, 1, Native.POINTER_FEEDBACK_DEFAULT);
        return _syntheticPen != IntPtr.Zero;
    }

    /// <summary>注入一个合成笔采样点，走的是和真笔同一条 WM_POINTER 路径。</summary>
    private unsafe void SendPenPoint(float x, float y, uint pressure, bool contact, bool first)
    {
        uint flags = Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_CONFIDENCE;
        if (contact) flags |= Native.POINTER_FLAG_INCONTACT;
        if (first) flags |= Native.POINTER_FLAG_NEW | Native.POINTER_FLAG_PRIMARY | Native.POINTER_FLAG_FIRSTBUTTON;

        var one = new Native.POINTER_TYPE_INFO();
        one.type = Native.PT_PEN;
        one.pen.pointerInfo.pointerType = Native.PT_PEN;
        one.pen.pointerInfo.pointerFlags = flags;
        one.pen.pointerInfo.ptPixelLocationX = (int)x;
        one.pen.pointerInfo.ptPixelLocationY = (int)y;
        one.pen.pointerInfo.hwndTarget = _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;
        one.pen.penFlags = 0;
        one.pen.penMask = Native.PEN_MASK_PRESSURE;
        one.pen.pressure = pressure;

        // 单点：按**托管结构体步长**排（= 原生步长 152；单点其实无所谓，但别再写死旧数字）。
        IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.POINTER_TYPE_INFO>() + 16);
        try
        {
            Marshal.StructureToPtr(one, buf, false);
            Native.InjectSyntheticPointerInput(_syntheticPen, buf, 1);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
    private void BallProbe(string prefix)
    {
        SetUiPref("hide", "1");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        var ui = CurrentUi as InkUi.FullUi;
        if (ui == null) { Console.WriteLine("  界面没挂上"); ExitCode = 1; _quit = true; return; }

        ui.SetExpandForTest(0f);            // 收起态（球）
        var scr = ui.ScreenForTest;
        const float gap = 2f, ball = 48f;
        var spots = new (string Name, float X, float Y)[]
        {
            ("left-mid",    scr.MinX + gap,               (scr.MinY + scr.MaxY) * 0.5f),
            ("right-mid",   scr.MaxX - gap - ball,        (scr.MinY + scr.MaxY) * 0.5f),
            ("bottom-left", scr.MinX + gap,               scr.MaxY - gap - ball),
            ("bottom-mid",  (scr.MinX + scr.MaxX) * 0.5f - ball * 0.5f, scr.MaxY - gap - ball),
            ("bottom-right",scr.MaxX - gap - ball,        scr.MaxY - gap - ball),
            ("left-low",    scr.MinX + gap,               scr.MaxY - 80f),
        };

        foreach (var (name, x, y) in spots)
        {
            foreach (int peek in new[] { 1, 0 })
            {
                ui.SetAnchorForTest(new System.Numerics.Vector2(x, y));
                ui.ForcePeekForTest(peek);
                SettleFrames(250);
                var b = ui.QueryBounds();
                var shot = new RectF
                {
                    MinX = b.MinX - 10f, MinY = b.MinY - 10f,
                    MaxX = b.MaxX + 10f, MaxY = b.MaxY + 10f,
                };
                string path = $"{prefix}-{name}-{(peek == 1 ? "show" : "hide")}.bmp";
                OffscreenShot(path, shot);
                Console.WriteLine($"  {name,-13} peek={peek} 锚=({x:F0},{y:F0}) "
                                  + $"占用=({b.MinX:F0},{b.MinY:F0})-({b.MaxX:F0},{b.MaxY:F0}) "
                                  + $"球高={b.MaxY - b.MinY:F0} {path}");
            }
        }
        _quit = true;
    }

    /// <summary>
    /// `--replayshow &lt;图&gt;`：回放控制条的摆样（排版好不好看自检判不了，只能看图）。
    /// 铺几笔 → 开始回放 → 拖到快一半 → 截控制条那一块（带一点周围）。
    /// 看图用：按钮间距、倍速高亮、进度滑钮、读数、✕。
    /// </summary>
    private void ReplayShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        Doc.ResetToSinglePage();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Tool = Tool.Pen;
        SettleFrames(150);

        void AddInk(float x, float y, float dx, float t0, float t1)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.90f, 0.20f, 0.20f, 1f), Width = 8f,
            };
            for (int k = 0; k <= 4; k++)
                s.AddPoint(x + dx * k / 4f, y + k * 8f, 0.3f + 0.15f * k, t0 + (t1 - t0) * k / 4f);
            Doc.AddStroke(s);
        }
        for (int i = 0; i < 6; i++) AddInk(300f + i * 40f, 260f + i * 70f, 420f, i * 700f, i * 700f + 600f);
        SettleFrames(200);

        if (!StartReplayForTest())
        {
            Console.WriteLine("  摆样失败：回放没起来（没有可见笔迹？）");
            ExitCode = 1; _quit = true; return;
        }
        ReplaySeekForTest(ReplayTotalForTest * 0.45f);   // 停在"正在长"那儿
        ReplaySetSpeedForTest(2f);
        SettleFrames(300);

        var bar = ReplayBarRectForTest;
        int sx, sy, sw, sh;
        if (Environment.GetCommandLineArgs().Contains("--full"))
        {
            // 整屏：看"已出完的（烘进内容层）＋ 正在长的那一条（前缀）"对不对
            sx = _virtualX; sy = _virtualY; sw = _virtualW; sh = _virtualH;
        }
        else
        {
            int pad = (int)(18 * DpiScale);
            sx = (int)bar.MinX - pad; sy = (int)bar.MinY - pad;
            sw = (int)(bar.MaxX - bar.MinX) + pad * 2;
            sh = (int)(bar.MaxY - bar.MinY) + pad * 2;
        }
        bool ok = ScreenProbe.SaveBmp(path, sx, sy, sw, sh);
        Console.WriteLine(ok ? $"  已保存 {path}（{sw}×{sh}）" : "  截屏失败");
        StopReplay("摆样收尾");
        ExitCode = ok ? 0 : 1;
        _quit = true;
    }
    private static IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
    private int DrawAndCountHorizontal(float x, float y, float len, float width)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = width,
        };
        s.AddPoint(x, y, 1f, 0);
        s.AddPoint(x + len, y, 1f, 1);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(350);
        return ScreenProbe.CountMagenta((int)x, (int)(y - width), (int)len, (int)(width * 2f));
    }

    /// <summary>画一条粗竖线并数它的墨像素。竖线的**中心线**画在 x 上。</summary>
    private int DrawAndCountVertical(float x, float y, float len, float width)
    {
        Doc.Clear();
        Doc.ClearHistory();
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 1f, 1f), Width = width,
        };
        s.AddPoint(x, y, 1f, 0);
        s.AddPoint(x, y + len, 1f, 1);
        Doc.AddStroke(s);
        Doc.InvalidateAll();
        SettleFrames(350);
        return ScreenProbe.CountMagenta((int)(x - width), (int)y, (int)(width * 2f), (int)len);
    }
    private bool SkipIfNoSyntheticInput(string what)
    {
        if (ProbeSyntheticInput(_virtualX + _virtualW * 0.5f, _virtualY + _virtualH * 0.4f)) return false;
        Console.WriteLine($"  环境：合成输入不可用 → SKIP: {what}跳过（多半是别的程序正占着鼠标；松开后重跑即可）");
        return true;
    }

    private bool ProbeSyntheticInput(float cx, float cy)
    {
        Tool = Tool.Pen;
        int before = Doc.Strokes.Count;
        SendMouse((int)cx, (int)cy, 0);
        SettleFrames(120);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);
        SettleFrames(60);
        SendMouse((int)(cx + 60), (int)(cy + 30), 0);
        SettleFrames(80);
        SendMouse((int)(cx + 60), (int)(cy + 30), Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(200);

        // **先数再清**：下面那个 Clear() 会把刚画出来的这一笔也清掉，
        // 以前在这里直接 `return Doc.Strokes.Count > before`，于是永远返回 false
        // ——所有靠它把关的用例（残影、橡皮、截图……）其实一直在静默 SKIP，
        // 而屏幕上的日志却写着"合成输入不可用"，把锅推给了环境。
        // 判据必须取 Clear 之前的值。
        int after = Doc.Strokes.Count;
        Doc.Clear();
        Doc.ClearHistory();
        return after > before;
    }
    private Thread StartInjector(float x0, float y0, float x1, float y1, double hz, double ms)
    {
        var th = new Thread(() =>
        {
            SendMouse((int)x0, (int)y0, 0);
            Thread.Sleep(80);
            SendMouse((int)x0, (int)y0, Native.MOUSEEVENTF_LEFTDOWN);
            var sw = Stopwatch.StartNew();
            double period = 1000.0 / hz;
            int i = 1;
            while (true)
            {
                double target = i * period;
                if (target > ms) break;
                // 睡到接近、再自旋到点：Sleep 的抖动是 1~15ms，直接用来配速
                // 会让"注入时刻"本身变得比被测量的延时还抖。
                while (true)
                {
                    double remain = target - sw.Elapsed.TotalMilliseconds;
                    if (remain <= 0) break;
                    if (remain > 2) Thread.Sleep((int)(remain - 1));
                    else Thread.SpinWait(200);
                }
                float u = (float)(target / ms);
                float px = x0 + (x1 - x0) * u;
                float py = y0 + (y1 - y0) * u + MathF.Sin(u * 9f) * 70f;
                SendMouse((int)px, (int)py, 0);
                i++;
            }
            SendMouse((int)x1, (int)y1, Native.MOUSEEVENTF_LEFTUP);
        })
        { IsBackground = true };
        th.Start();
        return th;
    }

    /// <summary>
    /// 边抽消息边渲染，直到注入线程收笔。
    ///
    /// **顺序必须和主循环 Loop() 完全一致**：先抽、等到合成边界（配速模式）、
    /// 再抽一次、然后画。第一版把 DwmFlush 放在抽消息之后，量出来的
    /// "收到 → 调 Present" 是 16.2ms——那不是渲染慢，那是测量循环自己排的队。
    /// 测量代码和被测代码的时序不一致，量出来的就是测量方法的问题。
    /// </summary>
    private void PumpUntilDone(Thread injector)
    {
        while (injector.IsAlive)
        {
            DrainMessages();
            if (_dirty || _drawing)
            {
                if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
                RenderAll();
                _dirty = false;
            }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, Native.QS_ALLINPUT, 0);
        }
        DrainMessages();
        RenderAll();
    }

    private string PresentModeName()
    {
        if (OverlayWindow.VBlankPaced) return "合成边界配速 + Present(0)";
        if (OverlayWindow.LatencyWaitEnabled) return "帧延迟等待对象 + Present(0)（不限速）";
        return "Present(1) 阻塞在垂直同步";
    }

    /// <summary>把一个场景跑完：清空采样 → 注入 → 收数据 → 打印 + 落 CSV。</summary>
    private void RunLatencyScenario(string tag, string csvPath, string note,
                                    float x0, float y0, float x1, float y1, double hz, double ms)
    {
        Console.WriteLine();
        Console.WriteLine($"--- 场景 {tag}：{note}");
        Console.WriteLine($"    注入 {hz:F0} Hz，{ms / 1000.0:F1} 秒，路径 ({x0:F0},{y0:F0}) → ({x1:F0},{y1:F0})");

        Latency.Scenario = tag;
        Latency.Clear();
        ResetLatencyProbe();
        LatencyRecording = true;

        long frames0 = FrameCounter;
        double wall0 = _clock.Elapsed.TotalMilliseconds;
        var th = StartInjector(x0, y0, x1, y1, hz, ms);
        PumpUntilDone(th);
        double wall = _clock.Elapsed.TotalMilliseconds - wall0;
        long frames = FrameCounter - frames0;

        LatencyRecording = false;
        Console.WriteLine($"    采到 {Latency.Count} 条样本，渲染 {frames} 帧 / {wall / 1000.0:F1} 秒"
                          + $" = {frames * 1000.0 / wall:F1} fps（笔画数 {Doc.Strokes.Count}）");
        Console.Write(Latency.Report(spikeThresholdMs: 2 * (_windows.Count > 0 ? _windows[0].RefreshPeriodMs : 1000.0 / 60.0)));
        Latency.WriteCsv(csvPath, tag);
    }

    /// <summary>把页面写满，用来量"同一页墨迹很多时"的延时。</summary>
    private void FillDensePage(int strokes, float cx, float cy)
    {
        Doc.Clear();
        var rnd = new Random(7);
        for (int i = 0; i < strokes; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = new Color4(0.2f, 0.3f, 0.9f, 1f),
                Width = 3f * DpiScale,
            };
            float bx = cx - 420 + (float)rnd.NextDouble() * 840;
            float by = cy - 260 + (float)rnd.NextDouble() * 520;
            for (int k = 0; k <= 14; k++)
                s.AddPoint(bx + k * 3.5f, by + MathF.Sin(k * 0.7f) * 5f, 0.6f, NowMs);
            Doc.AddStroke(s);
        }
        Doc.InvalidateAll();
        SettleFrames(700);
    }

    /// <summary>
    /// 测"Present 返回 → 屏幕真的变色"这一段——DWM 合成 + 扫描输出 + 面板。
    ///
    /// 为什么必须盯着屏幕看：进程内的 API 拿不到这一段的准确时刻。前面四段
    /// 我们都能自己打时标，唯独"像素什么时候亮"只有屏幕自己知道。
    ///
    /// 做法：白板底色按 8 帧一个周期黑白交替（4 帧白、4 帧黑），后台线程用 GDI
    /// 一直读屏幕上固定的一个点，记下每次变色的时刻；主线程每帧记下
    /// (Present 返回时刻, 这一帧的颜色)。周期 8 帧 ≈133ms，远大于任何可能的
    /// 管线延时，所以每次变色都能唯一对上"是哪一帧先变的"。
    /// delta = 变色时刻 − 那一帧 Present 返回的时刻。
    ///
    /// 这个尺子自身的偏差要说清楚：GDI 抓的是 DWM 合成后的桌面，读取本身也可能
    /// 按垂直同步对齐，所以绝对值可能整体偏大几毫秒。**两种呈现方式用同一把
    /// 尺子量出来的差值（差几个刷新周期）才是这条数据真正的价值。**
    /// </summary>
    private void RunPhotonProbe(string csvPath, int cycles)
    {
        Console.WriteLine();
        Console.WriteLine("--- 场景 photon：Present 返回 → 屏幕真的变色（DWM 合成 + 扫描输出）");
        Console.WriteLine("    白板按 8 帧黑白交替，另起一个线程盯着屏幕上一个点看什么时候变");

        Doc.Clear();
        BoardOn = true;
        _dirty = true;
        RenderAll();
        _dirty = false;

        int probeX = _virtualX + 40, probeY = _virtualY + 40;
        var flips = new List<(ulong qpc, int color)>();
        bool stop = false;

        var th = new Thread(() =>
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            var bi = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = 1, biHeight = -1, biPlanes = 1, biBitCount = 32,
                    biCompression = (int)Native.BI_RGB,
                }
            };
            IntPtr dib = Native.CreateDIBSection(memDc, ref bi, Native.DIB_RGB_COLORS,
                                                 out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(memDc, dib);
            int last = -1;
            while (!stop)
            {
                Native.BitBlt(memDc, 0, 0, 1, 1, screenDc, probeX, probeY, Native.SRCCOPY);
                int c = Marshal.ReadByte(bits, 0) > 128 ? 1 : 0;   // B 通道
                if (last >= 0 && c != last) flips.Add((Qpc.Now, c));
                last = c;
                Thread.SpinWait(150);
            }
            Native.SelectObject(memDc, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        })
        { IsBackground = true };
        th.Start();
        Thread.Sleep(200);

        var frames = new List<(ulong qpc, int color)>();
        int total = cycles * 8;
        for (int i = 0; i < total; i++)
        {
            int color = (i / 4) % 2 == 0 ? 1 : 0;      // 1 = 白，0 = 黑
            BoardColor = color == 1 ? new Color4(1f, 1f, 1f, 1f) : new Color4(0f, 0f, 0f, 1f);
            _dirty = true;
            if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
            RenderAll();
            _dirty = false;
            frames.Add((_windows[0].LastPresentEndQpc, color));
        }

        stop = true;
        th.Join(2000);
        BoardOn = false;
        BoardColor = new Color4(0.99f, 0.99f, 0.98f, 1f);
        _dirty = true;
        RenderAll();
        _dirty = false;

        // 把每次变色对上"这一段的头一帧"。同一颜色的帧是连续 4 帧一段，
        // 屏幕上变色对应的是这一段的第 1 帧上屏。
        var deltas = new List<double>();
        int searchFrom = 0;
        foreach (var (qpc, color) in flips)
        {
            int match = -1;
            for (int k = searchFrom; k < frames.Count; k++)
            {
                if (frames[k].color != color) continue;
                if (k > 0 && frames[k - 1].color == color) continue;   // 只要段首
                if (frames[k].qpc <= qpc) { match = k; searchFrom = k + 1; }
                else break;
            }
            if (match < 0) continue;
            deltas.Add(Qpc.Ms(frames[match].qpc, qpc));
        }

        double refresh = _windows[0].RefreshPeriodMs;
        if (deltas.Count == 0)
        {
            Console.WriteLine("    没对上（屏幕抓取可能被别的窗口挡住，或抓取本身失败）");
            return;
        }
        var d = new Dist(deltas);
        Console.WriteLine($"    抓到 {deltas.Count} 次变色，本机交换链报告的刷新周期 {refresh:F2} ms");
        Console.WriteLine($"    Present 返回 → 屏幕变色：{d}");
        Console.WriteLine($"    折合 {(d.Mean / refresh):F2} 个刷新周期（均值）、"
                          + $"{(d.P50 / refresh):F2} 个（中位）");
        _photonDeltaMs = d.Mean;
        _photonDeltaFrames = d.Mean / refresh;
    }

    /// <summary>上机测得的上屏段（毫秒 / 刷新周期数），给报告拼"端到端"用。</summary>
    private double _photonDeltaMs;
    private double _photonDeltaFrames;

    private void LatencyBench(string csvPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 延时实测（笔尖动 → 像素亮）===");
        Console.WriteLine($"  呈现方式      ：{PresentModeName()}");
        Console.WriteLine($"  交换链后缓冲  ：{OverlayWindow.BufferCount}");
        Console.WriteLine($"  性能面板 HUD  ：{(ShowHud ? "开" : "关")}");
        Console.WriteLine($"  委托墨迹轨迹  ：{OverlayWindow.InkTrailNote}"
                          + (OverlayWindow.InkTrailEnabled ? "，已启用" : "，未启用"));
        Console.WriteLine($"  说明          ：注入的是合成鼠标，走的是和真笔同一条 WM_POINTER 路径；"
                          + "“输入 → 收到消息”那一段在合成输入上可能不可得（报告里会标）。");

        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.45f;

        // 1) 空白页，正常书写速度。
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("empty-slow", csvPath, "空白页，正常书写速度",
            cx - 380, cy, cx + 380, cy, 140, 3000);

        // 2) 空白页，快速划线。点的间距大、单位时间覆盖面积大，
        //    量的是"同样一帧要光栅化的墨更多"时的表现。
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("empty-fast", csvPath, "空白页，快速划线",
            cx - 600, cy, cx + 600, cy, 200, 2500);

        // 3) 密集页：同一小块里已经有 3000 笔。
        FillDensePage(3000, cx, cy);
        RunLatencyScenario("dense", csvPath, "密集页（同区域已有 3000 笔）",
            cx - 380, cy, cx + 380, cy, 140, 3000);

        // 4) 长跑：连续写 12 秒，专门看"越写越慢"和抖动分布。
        Doc.Clear();
        Doc.InvalidateAll();
        SettleFrames(300);
        RunLatencyScenario("long-12s", csvPath, "连续书写 12 秒（稳定性）",
            cx - 380, cy, cx + 380, cy, 140, 12000);

        // 5) 上屏段：唯一必须"看屏幕"才知道的一段。
        RunPhotonProbe(csvPath, 20);
        if (_photonDeltaMs > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  端到端估计（最新一笔 → 像素亮）：");
            Console.WriteLine($"    阻塞式 : 16.5（到 Present 返回）+ {_photonDeltaMs:F1}（上屏）"
                              + $" ≈ {16.5 + _photonDeltaMs:F0} ms");
        }

        Console.WriteLine();
        Console.WriteLine($"CSV 已写入 {csvPath}");
        _quit = true;
    }

    /// <summary>
    /// 真笔实测：把探针挂上，请用户用手写笔在屏幕上写，结束后出报告。
    ///
    /// 这个模式同时回答一个关键问题：**手写板到底是以"笔"报进来的，还是以
    /// "鼠标"报进来的**。前者能拿到硬件时标和压感，后者连"笔尖什么时候动"
    /// 都只能靠轮询猜——很多手写板的"迟滞感"就出在这里，跟应用代码无关。
    /// </summary>
    private void LatencyLive(double seconds)
    {
        Console.WriteLine();
        Console.WriteLine("=== 真笔延时实测 ===");
        Console.WriteLine($"  呈现方式：{PresentModeName()}，交换链后缓冲 {OverlayWindow.BufferCount}");
        Console.WriteLine($"  请用手写笔画线，持续 {seconds:F0} 秒（或按 Ctrl+Alt+Shift+X 提前结束）……");
        Console.WriteLine();

        Latency.Scenario = "真笔手写";
        Latency.Clear();
        ResetLatencyProbe();
        LatencyRecording = true;

        var seen = new Dictionary<uint, int>();
        double t0 = NowMs;
        double nextTick = t0 + 2000;
        while (!_quit && NowMs - t0 < seconds * 1000)
        {
            DrainMessages();
            seen[LastPointerType] = seen.GetValueOrDefault(LastPointerType) + 1;
            if (_dirty || _drawing)
            {
                if (OverlayWindow.VBlankPaced) { Native.DwmFlush(); DrainMessages(); }
                RenderAll();
                _dirty = false;
            }
            else Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, Native.QS_ALLINPUT, 0);
            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= nextTick)
            {
                nextTick = NowMs + 2000;
                Console.WriteLine($"  [{NowMs / 1000:F0}s] 已采 {Latency.Count} 帧，"
                                  + $"当前设备 {DeviceName(LastPointerType)}，"
                                  + $"笔画 {Doc.Strokes.Count}");
            }
        }

        LatencyRecording = false;
        Console.WriteLine();
        Console.WriteLine("设备使用统计（每帧采到的最新型号）："
                          + string.Join("，", seen.OrderByDescending(kv => kv.Value)
                                                  .Select(kv => $"{DeviceName(kv.Key)} {kv.Value} 帧")));
        Console.WriteLine("  笔 = PT_PEN 才是真笔通道（有压感、有硬件时标）；"
                          + "鼠标 = 手写板工作在兼容模式，单这一项就够造成迟滞感。");

        // 真笔的"数据质量"汇总：这次要不要相信压感、合并点收全了没有、
        // 以及预测在什么档位（这些在 调研-压感与预测-原理.md 与 测试-压感与预测.md 里都有判据）。
        Console.WriteLine();
        Console.WriteLine("=== 真笔数据质量汇总 ===");
        Console.WriteLine($"  合并点：{PenMessages} 条消息 → {PenSamples} 个采样点"
                          + (PenMessages > 0 ? $"（每条 {PenSamples / (double)PenMessages:F2} 个）" : ""));
        // 非笔指针（鼠标 / 触摸）也报一份：手写板没开 Windows Ink、以及触摸屏，
        // 走的就是这条路——"合并率"这一项以前只有笔有数，现在两边都有。
        if (PtrMessages > 0)
            Console.WriteLine($"  非笔指针（鼠标/触摸）：{PtrMessages} 条消息 → {PtrSamples} 个采样点"
                              + $"（每条 {PtrSamples / (double)PtrMessages:F2} 个）"
                              + $"，收下 {PtrTotalPoints} 个点");
        Console.WriteLine($"  压感：{PenPressurePoints}/{PenTotalPoints} 个点带有效压感；"
                          + $"设备报 pressure 位={Yes(PenSawPressureMask)}，"
                          + $"倾角位={Yes(PenSawTiltMask)}，旋转位={Yes(PenSawRotationMask)}");
        // [删除 2026-10-05] 预测统计：随老预测系统移除。
        Console.WriteLine($"  湿墨轨迹：{(OverlayWindow.InkTrailEnabled ? "开" : "关")}"
                          + $"（{OverlayWindow.InkTrailNote}）");

        // 书写期间的分配与 GC：低配机排查"偶发卡顿"的依据。
        // 每笔分配越大越容易触发回收；**第 2 代回收出现在书写期间 = 那一下就卡了几十毫秒**。
        if (StrokesMeasured > 0)
            Console.WriteLine($"  书写期间：{StrokesMeasured} 笔，每笔整笔会话分配（累计，非驻留） 平均 {AllocKbSum / StrokesMeasured:F1} KB"
                              + $" / 最大 {AllocBytesMax / 1024.0:F1} KB；"
                              + (StrokesWithGc2 == 0
                                  ? "第 2 代 GC 一次都没出现在书写期间（好）"
                                  : $"⚠ 第 2 代 GC 出现在 {StrokesWithGc2} 笔里（低配机上就是可见卡顿，要查）"));
        Console.WriteLine($"  GC 低延迟档：{GcLatency.Describe()}");

        Console.WriteLine(PenTotalPoints == 0
            ? "  注意：这一轮一个真笔采样点都没有——写的时候用的是鼠标/触摸，或者笔工作在兼容模式"
            : PenSawPressureMask
                ? "  压感可用：这台机器/这支笔确实在报压力"
                : "  压感不可用：设备没报 pressure 位（先查驱动的 Windows Ink 开关）");

        Console.Write(Latency.Report(spikeThresholdMs: 2 * (_windows.Count > 0 ? _windows[0].RefreshPeriodMs : 1000.0 / 60.0)));
        string csv = "reports/latency-live.csv";
        Latency.WriteCsv(csv, "real-pen");
        Console.WriteLine($"CSV 已写入 {csv}");
        _quit = true;
    }

    /// <summary>汇总行里用的小写法。</summary>
    private static string Yes(bool v) => v ? "有" : "无";

    private static string DeviceName(uint t) => t switch
    {
        Native.PT_PEN => "笔",
        Native.PT_TOUCH => "触摸",
        Native.PT_MOUSE => "鼠标",
        _ => $"未知({t})",
    };

    /// <summary>
    /// Draws one long stroke, checks it is on screen, erases its middle, then
    /// checks the middle went away and the ends survived. Correctness of the
    /// region patch is not something you can eyeball from inside the process.
    /// </summary>
    private void EraseCheck(out bool correct, out string detail)
    {
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;

        // Magenta horizontal stroke, wiped at its left third.
        var target = new Stroke { Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 16f };
        for (int i = 0; i <= 80; i++)
        {
            float t = i / 80f;
            target.AddPoint(cx - 400 + t * 800, cy, 0.9f, NowMs);
        }

        // Green vertical stroke crossing the magenta one, far from the erase
        // point. It must survive untouched *including* where it passes through
        // the rectangle that gets cleared and re-rasterised.
        float vx = cx + 200;
        var bystander = new Stroke { Tool = Tool.Pen, Color = new Color4(0f, 1f, 0f, 1f), Width = 16f };
        for (int i = 0; i <= 60; i++)
        {
            float t = i / 60f;
            bystander.AddPoint(vx, cy - 300 + t * 600, 0.9f, NowMs);
        }

        Doc.Clear();
        Doc.AddStroke(target);
        Doc.AddStroke(bystander);
        Doc.InvalidateAll();
        SettleFrames(500);

        int targetBefore = ScreenProbe.CountMagenta((int)(cx - 360), (int)(cy - 30), 120, 60);
        int bystanderBefore = ScreenProbe.CountGreen((int)(vx - 30), (int)(cy - 30), 60, 60);

        int removed = Doc.EraseAt(cx - 300, cy, 30f);
        SettleFrames(500);

        int targetAfter = ScreenProbe.CountMagenta((int)(cx - 360), (int)(cy - 30), 120, 60);
        // Sample the bystander *inside* the patched band, not above it.
        int bystanderAfter = ScreenProbe.CountGreen((int)(vx - 30), (int)(cy - 30), 60, 60);

        correct = targetBefore > 200 && targetAfter < 40 && removed == 1
                  && bystanderBefore > 200 && bystanderAfter > bystanderBefore * 0.8;
        detail = $"被擦笔画 {targetBefore}→{targetAfter} 像素（应归零），"
               + $"同区域另一笔 {bystanderBefore}→{bystanderAfter} 像素（应保留），删除 {removed} 笔";
    }

    // =====================================================================
    //  Teardown
    // =====================================================================

}
