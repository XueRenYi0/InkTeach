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
}
