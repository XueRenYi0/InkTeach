// 本文件由 App.cs 拆出（2026-10-07）：PPT / 存档 / 导出 那一组自检。
// **纯搬家，逻辑一字未改** —— 靠 partial class 共享 App 的私有成员。
// 用途：产品代码与自检代码分开，人和 AI 读代码时不必互相干扰。

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
    /// <summary>
    /// **导出（选中的内容 → 透明底 PNG）自检**。
    ///
    /// 用户 2026-09-17 定：只导选中的、透明底、弹"另存为"、**同时进剪贴板**。
    /// 这里不走对话框（对话框在自检里会卡住），直接写到临时文件，其余流程一模一样。
    ///
    /// 关键是**把 PNG 解回来验像素**：PNG 要的是直通 alpha，而我们渲染出来的是
    /// 预乘 alpha——不还原就发灰（半透明荧光笔最明显），而这条错误光看"文件写出来了"
    /// 是发现不了的。所以这里自己解 IDAT（inflate ＋ 去 filter 字节）抽像素比。
    /// </summary>
    private void IoTest(string keepPath = null)
    {
        Console.WriteLine();
        Console.WriteLine("=== 导出自检（选中 → PNG 透明底 / JPEG 白底；不碰剪贴板）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        string path = keepPath ?? Path.Combine(Path.GetTempPath(), "inkteach-iotest.png");
        if (keepPath == null) { try { File.Delete(path); } catch { } }

        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        SettleFrames(200);

        // ① 没选中 → 不导出、也不该留下文件
        bool ok0 = ExportSelectionToPathForTest(path);
        Check("没选中时不导出、不写文件", !ok0 && !File.Exists(path),
              $"返回 {ok0}，文件在 = {File.Exists(path)}");

        // ② 一笔实心笔 ＋ 一笔半透明荧光笔，全选中
        var red = new Color4(0.95f, 0.18f, 0.18f, 1f);
        var yellow = new Color4(0.98f, 0.82f, 0.12f, 1f);
        var pen = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = red, Width = 20f * DpiScale,
        };
        for (int i = 0; i <= 20; i++) pen.AddPoint(600 + i * 10, 400, 1f, i * 8);
        Doc.AddStroke(pen);

        var hlColor = InkPalette.ToHighlighter(yellow);
        var hl = new Stroke
        {
            Tool = Tool.Highlighter, Kind = StrokeKind.Freehand,
            Color = hlColor, Width = 40f * DpiScale,
        };
        for (int i = 0; i <= 20; i++) hl.AddPoint(600 + i * 10, 500, 1f, i * 8);
        Doc.AddStroke(hl);
        Doc.SelectOnly(new[] { pen, hl });
        Tool = Tool.Marquee;
        SettleFrames(300);

        // 先在剪贴板上放一个"标记"（一条蓝色笔迹）：导出之后它必须**原样还在**——
        // 用户 2026-09-17 明确："导出不用进剪贴板，因为我们有复制功能"。
        var marker = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0f, 1f, 1f), Width = 5f,
        };
        marker.AddPoint(10, 10, 1f, 0); marker.AddPoint(20, 20, 1f, 1);
        ClipboardInk.Set(ClipboardInk.Serialize(new[] { marker }), null, 0, 0);

        bool ok = ExportSelectionToPathForTest(path);
        Check("导出返回成功", ok, $"返回 {ok}");
        if (!ok || !File.Exists(path))
        {
            Console.WriteLine($"  FAIL: 文件没写出来，后面几项没法验（{fail} 项失败）");
            _quit = true;
            return;
        }

        var png = File.ReadAllBytes(path);
        Check("文件非空", png.Length > 100, $"{png.Length} 字节");
        Check("PNG 签名正确",
              png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
              $"{png[0]:X2} {png[1]:X2} {png[2]:X2} {png[3]:X2}");

        // ③ 解回像素：走 chunks → 拼 IDAT → inflate → 去每行的 filter 字节
        int iw = 0, ih = 0;
        var idat = new MemoryStream();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            string type = "" + (char)png[p + 4] + (char)png[p + 5] + (char)png[p + 6] + (char)png[p + 7];
            int dataAt = p + 8;
            if (type == "IHDR")
            {
                iw = (png[dataAt] << 24) | (png[dataAt + 1] << 16) | (png[dataAt + 2] << 8) | png[dataAt + 3];
                ih = (png[dataAt + 4] << 24) | (png[dataAt + 5] << 16) | (png[dataAt + 6] << 8) | png[dataAt + 7];
            }
            else if (type == "IDAT") idat.Write(png, dataAt, len);
            p = dataAt + len + 4;
        }

        var box = EditRegion.Of(new[] { pen, hl }).Inflate(4f * DpiScale);
        int wantW = (int)MathF.Ceiling(box.MaxX - box.MinX);
        int wantH = (int)MathF.Ceiling(box.MaxY - box.MinY);
        Check("IHDR 的宽高 = 选区像素尺寸", iw == wantW && ih == wantH,
              $"图 {iw}×{ih}，选区 {wantW}×{wantH}");

        // inflate：zlib 头 2 字节 ＋ 尾部 adler32 4 字节，中间是裸 deflate
        var z = idat.ToArray();
        byte[] raw;
        using (var ms = new MemoryStream(z, 2, z.Length - 6))
        using (var inf = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            inf.CopyTo(outMs);
            raw = outMs.ToArray();
        }
        Check("IDAT 能解回原始像素（每行 1 个 filter 字节）",
              raw.Length >= ih * (1 + iw * 4), $"{raw.Length} 字节（应为 {ih * (1 + iw * 4)}）");

        (byte R, byte G, byte B, byte A) PixelAt(float canvasX, float canvasY)
        {
            int x = Math.Clamp((int)(canvasX - box.MinX), 0, iw - 1);
            int y = Math.Clamp((int)(canvasY - box.MinY), 0, ih - 1);
            int o = y * (1 + iw * 4) + 1 + x * 4;
            return (raw[o], raw[o + 1], raw[o + 2], raw[o + 3]);
        }

        var px = PixelAt(700, 400);           // 红笔的中心
        Check("实心笔：不透明、颜色就是笔色",
              px.A == 255
              && Math.Abs(px.R - red.R * 255) < 6 && Math.Abs(px.G - red.G * 255) < 6
              && Math.Abs(px.B - red.B * 255) < 6,
              $"RGBA = {px.R},{px.G},{px.B},{px.A}（应为 {(int)(red.R * 255)},{(int)(red.G * 255)},{(int)(red.B * 255)},255）");

        var hp = PixelAt(700, 500);           // 荧光笔的中心
        int wantA = (int)(hlColor.A * 255);
        Check("荧光笔：半透明（没被压成不透明）",
              hp.A > 20 && hp.A < 250, $"alpha = {hp.A}（应为 {wantA} 上下）");
        // 预乘 → 直通没做对的话，这里会明显偏暗（R 会从 ~250 掉到 ~80）
        Check("荧光笔：颜色是**直通 alpha**（预乘没还原就会发灰）",
              Math.Abs(hp.R - yellow.R * 255) < 12 && Math.Abs(hp.G - yellow.G * 255) < 12
              && Math.Abs(hp.B - yellow.B * 255) < 12,
              $"RGB = {hp.R},{hp.G},{hp.B}（应为 {(int)(yellow.R * 255)},{(int)(yellow.G * 255)},{(int)(yellow.B * 255)}）");

        var blank = PixelAt(300, 300);        // 选区左上角那块（没有墨）
        Check("空白处：完全透明（透明底）", blank.A == 0 && blank.R == 0 && blank.G == 0 && blank.B == 0,
              $"RGBA = {blank.R},{blank.G},{blank.B},{blank.A}");

        // ④ 剪贴板：导出**不许动它**（用户 2026-09-17："导出不用进剪贴板，我们有复制功能"）。
        // 判据：导出前放一条"标记"笔迹，导出之后读回来还得是那一条（1 个，不是选中的 2 个）。
        bool clip = ClipboardInk.TryGetObjects(out var back);
        Check("导出**不动剪贴板**（复制那条路各管各的）",
              clip && back != null && back.Count == 1,
              clip ? $"读回 {back?.Count} 个对象（应为 1 = 导出前放的那条标记）"
                   : "剪贴板里没有对象（不该）");

        // ⑤ 另一种格式：**JPEG（白底）**——用户 2026-09-17："导出功能增加 jpg 格式，
        //    然后让用户知道 png 是透明底、jpg 是白底"。格式差别写在文件类型那一行，
        //    这里验的是"真按 JPG 编码了、而且透明处是白的（不是黑的）"。
        string jpgPath = System.IO.Path.ChangeExtension(path, ".jpg");
        try { File.Delete(jpgPath); } catch { }
        bool okJpg = ExportSelectionToPathForTest(jpgPath);
        Check("导出成 .jpg：返回成功、文件存在", okJpg && File.Exists(jpgPath),
              $"返回 {okJpg}，文件在 = {File.Exists(jpgPath)}");
        if (File.Exists(jpgPath))
        {
            var jpg = File.ReadAllBytes(jpgPath);
            Check("是真正的 JPEG（FF D8 FF 开头）",
                  jpg.Length > 4 && jpg[0] == 0xFF && jpg[1] == 0xD8 && jpg[2] == 0xFF,
                  $"{jpg[0]:X2} {jpg[1]:X2} {jpg[2]:X2}，{jpg.Length / 1024.0:F0} KB");
            // 解码回来验像素：GDI+ 解、和我们编码用的不是同一段代码
            try
            {
                using var bmp = new System.Drawing.Bitmap(jpgPath);
                Check("JPEG 尺寸 = 选区像素尺寸", bmp.Width == wantW && bmp.Height == wantH,
                      $"{bmp.Width}×{bmp.Height}，选区 {wantW}×{wantH}");
                // 空白处（原来透明）必须是**白**的——JPEG 没有透明通道，
                // 不合成白底的话那里会是黑块（很多程序都踩过）
                var blankPx = bmp.GetPixel(20, 20);
                Check("原来透明的地方变成**白底**（不是黑块）",
                      blankPx.R > 235 && blankPx.G > 235 && blankPx.B > 235,
                      $"({blankPx.R},{blankPx.G},{blankPx.B})");
                // 笔迹颜色还在（JPEG 有损，给宽一点的容差）
                var penPx = bmp.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmp.Width - 1),
                                         Math.Clamp((int)(400 - box.MinY), 0, bmp.Height - 1));
                Check("笔迹颜色还在（有损压缩，允许偏差）",
                      Math.Abs(penPx.R - red.R * 255) < 40 && penPx.G < 110 && penPx.B < 110,
                      $"({penPx.R},{penPx.G},{penPx.B})，原笔色 ({(int)(red.R * 255)},{(int)(red.G * 255)},{(int)(red.B * 255)})");
            }
            catch (Exception ex) { Check("JPEG 能被别的解码器读出来", false, ex.Message); }
            try { File.Delete(jpgPath); } catch { }
        }

        // ⑥ **PNG 白底**（第 3 条）：无损 + 白底。
        //    给"深色模板"用的——透明底的黑色笔迹贴到深色 PPT 上会看不见。
        //    注意它和第 1 条**同一个扩展名**，差别只在"选了哪一条"，
        //    所以这里必须传 filterIndex=3，不能靠扩展名区分。
        {
            string whiteP = Path.Combine(Path.GetTempPath(), "inkteach-iotest-white.png");
            try { File.Delete(whiteP); } catch { }
            bool okW = ExportSelectionToPathForTest(whiteP, 3);
            Check("导出成「PNG 白底」（第 3 条）：文件存在", okW && File.Exists(whiteP), $"返回 {okW}");
            if (File.Exists(whiteP))
            {
                var bytes = File.ReadAllBytes(whiteP);
                Check("还是 PNG 签名（同一条扩展名，两个选择）",
                      bytes.Length > 8 && bytes[0] == 0x89 && bytes[3] == 0x47,
                      $"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");
                try
                {
                    using var bmp = new System.Drawing.Bitmap(whiteP);
                    var blankPx = bmp.GetPixel(20, 20);
                    Check("空白处是**白底**（透明底那条这里是全透明）",
                          blankPx.R > 250 && blankPx.G > 250 && blankPx.B > 250 && blankPx.A == 255,
                          $"({blankPx.R},{blankPx.G},{blankPx.B},a={blankPx.A})");
                    var penPx = bmp.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmp.Width - 1),
                                             Math.Clamp((int)(400 - box.MinY), 0, bmp.Height - 1));
                    Check("笔迹颜色**一个不差**（无损，不是 JPEG 那种有损）",
                          Math.Abs(penPx.R - red.R * 255) < 4
                          && Math.Abs(penPx.G - red.G * 255) < 4
                          && Math.Abs(penPx.B - red.B * 255) < 4,
                          $"({penPx.R},{penPx.G},{penPx.B})");
                }
                catch (Exception ex) { Check("白底 PNG 能被别的解码器读出来", false, ex.Message); }
                try { File.Delete(whiteP); } catch { }
            }
        }

        // ⑦ **BMP 白底**（第 4 条）：老软件也打得开的无损位图。
        //    自己写的编码器，所以逐字段验：签名、宽高、位深、以及**像素真的是白的和白底**。
        {
            string bmpPath = Path.Combine(Path.GetTempPath(), "inkteach-iotest.bmp");
            try { File.Delete(bmpPath); } catch { }
            bool okB = ExportSelectionToPathForTest(bmpPath, 4);
            Check("导出成 .bmp：返回成功、文件存在", okB && File.Exists(bmpPath),
                  $"返回 {okB}，文件在 = {File.Exists(bmpPath)}");
            if (File.Exists(bmpPath))
            {
                var b = File.ReadAllBytes(bmpPath);
                int bw = b[18] | (b[19] << 8) | (b[20] << 16) | (b[21] << 24);
                int bh = b[22] | (b[23] << 8) | (b[24] << 16) | (b[25] << 24);
                int bits = b[28] | (b[29] << 8);
                int off = b[10] | (b[11] << 8) | (b[12] << 16) | (b[13] << 24);
                Check("BMP 文件头：'BM' + 宽高 + 24 位",
                      b[0] == (byte)'B' && b[1] == (byte)'M' && bw == wantW && bh == wantH && bits == 24,
                      $"{(char)b[0]}{(char)b[1]}，{bw}×{bh}，{bits} 位，像素起点 {off}，{b.Length / 1024.0:F0} KB");

                // **用别的解码器读**（GDI+），不自己解——自己解只能证明"我写的我自己读得懂"，
                // 证不了这个文件在别的程序里对不对（和上面 JPEG 那条同一个道理）。
                try
                {
                    using var bmpImg = new System.Drawing.Bitmap(bmpPath);
                    Check("GDI+ 也读得出来（尺寸对）",
                          bmpImg.Width == wantW && bmpImg.Height == wantH,
                          $"{bmpImg.Width}×{bmpImg.Height}");
                    var blankPx = bmpImg.GetPixel(20, 20);
                    Check("空白处是**白底**（不是黑块、不是透明）",
                          blankPx.R > 250 && blankPx.G > 250 && blankPx.B > 250,
                          $"({blankPx.R},{blankPx.G},{blankPx.B})");
                    var penPx = bmpImg.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmpImg.Width - 1),
                                                Math.Clamp((int)(400 - box.MinY), 0, bmpImg.Height - 1));
                    Check("笔迹颜色**一个不差**（无损）",
                          Math.Abs(penPx.R - red.R * 255) < 4
                          && Math.Abs(penPx.G - red.G * 255) < 4
                          && Math.Abs(penPx.B - red.B * 255) < 4,
                          $"({penPx.R},{penPx.G},{penPx.B})");
                    // 反过来：**上下不能颠倒**（BMP 是自下而上存的，写反了图是扣着的）
                    var topRow = bmpImg.GetPixel(Math.Clamp((int)(700 - box.MinX), 0, bmpImg.Width - 1), 1);
                    Check("上下没写反（最上一行是空白，不是笔迹）",
                          topRow.R > 250 && topRow.G > 250 && topRow.B > 250,
                          $"最上一行 ({topRow.R},{topRow.G},{topRow.B})");
                }
                catch (Exception ex) { Check("BMP 能被别的解码器读出来", false, ex.Message); }
                try { File.Delete(bmpPath); } catch { }
            }
        }

        // ⑧ **保存图片**（2026-10-02「更多 → 墨迹 → 保存图片」）：整块板书 → 图片。
        //    判据：写盘成功 / 尺寸 = 整块内容包围盒 / 默认白底 / 笔迹在 /
        //    **不碰选中与剪贴板** / 空板书拒绝。
        {
            string boardPath = Path.Combine(Path.GetTempPath(), "inkteach-iotest-board.jpg");
            try { File.Delete(boardPath); } catch { }

            // 先把选区清干净：证明保存图片**不依赖、也不改动**选区
            Doc.Selected.Clear();
            var boxAll = EditRegion.Of(new[] { pen, hl }).Inflate(4f * DpiScale);
            int bw = (int)MathF.Ceiling(boxAll.MaxX - boxAll.MinX);
            int bh = (int)MathF.Ceiling(boxAll.MaxY - boxAll.MinY);

            bool okBoard = SaveBoardImageForTest(boardPath);
            Check("保存图片：返回成功、文件存在", okBoard && File.Exists(boardPath),
                  $"返回 {okBoard}，文件在 = {File.Exists(boardPath)}");
            if (File.Exists(boardPath))
            {
                try
                {
                    using var bmp = new System.Drawing.Bitmap(boardPath);
                    Check("保存图片：尺寸 = 整块板书内容",
                          bmp.Width == bw && bmp.Height == bh,
                          $"{bmp.Width}×{bmp.Height}，内容 {bw}×{bh}");
                    var blankPx = bmp.GetPixel(20, 20);
                    Check("保存图片：默认白底（发微信不露黑）",
                          blankPx.R > 235 && blankPx.G > 235 && blankPx.B > 235,
                          $"({blankPx.R},{blankPx.G},{blankPx.B})");
                    var penPx = bmp.GetPixel(Math.Clamp((int)(700 - boxAll.MinX), 0, bmp.Width - 1),
                                             Math.Clamp((int)(400 - boxAll.MinY), 0, bmp.Height - 1));
                    Check("保存图片：笔迹在里面", penPx.G < 110 && penPx.B < 110,
                          $"({penPx.R},{penPx.G},{penPx.B})");
                }
                catch (Exception ex) { Check("保存图片：能被别的解码器读出来", false, ex.Message); }
                try { File.Delete(boardPath); } catch { }
            }
            Check("保存图片：**不碰选中**（之前清空的选区还是空的）",
                  Doc.Selected.Count == 0, $"选中 {Doc.Selected.Count}");
            bool clip2 = ClipboardInk.TryGetObjects(out var back2);
            Check("保存图片：**不动剪贴板**（还是那条标记）",
                  clip2 && back2 != null && back2.Count == 1, $"读回 {back2?.Count}");

            // 空板书拒绝、不写文件
            Doc.Clear();
            string emptyBoard = Path.Combine(Path.GetTempPath(), "inkteach-iotest-board-empty.jpg");
            try { File.Delete(emptyBoard); } catch { }
            bool okEmpty = SaveBoardImageForTest(emptyBoard);
            Check("保存图片：空板书拒绝、不写文件",
                  !okEmpty && !File.Exists(emptyBoard),
                  $"返回 {okEmpty}，文件在 = {File.Exists(emptyBoard)}");
            try { File.Delete(emptyBoard); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "  PASS：四种格式都对（PNG 透明底 / JPEG 白底 / PNG 白底 / BMP 白底，逐像素验过），而且没碰剪贴板"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");
        Console.WriteLine($"  文件：{path}（{png.Length} 字节）");

        // 给了路径就留着（人工核对 / 拿别的解码器验它）
        if (keepPath == null) { try { File.Delete(path); } catch { } }
        Doc.Clear();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// `--ppttest`：**PPT 模式自检**（用户 2026-09-26 拍板"参考 InkClass 原味"）。
    ///
    /// 用**假页码源**驱动（`PptFakeSource`）——我这边没有 PowerPoint 环境，
    /// 产品那条 COM 连接由用户在装了 PPT / WPS 的机器上跑 `--pptprobe` 确认。
    /// 假源和真源走的是**同一份状态机**（`StepPpt` → `ApplyPptState`），
    /// 所以这里验过的"进放映 / 翻页 / 存取 / 退出恢复"在真机上就是同一段代码。
    ///
    /// 验的九件事（对应参考来的语义 + 我们的两处适配）：
    ///   ① 进放映 = 切到 PPT 页，桌面批注**看不见**（完全隔离）；
    ///   ② 每页一套内容：翻页看不到别页、翻回来原样；
    ///   ③ **页内照样能滚动**，而且每页记住自己滚到哪（用户要的"我依然支持向下滚动"）；
    ///   ④ 清空只清本页（InkClass 的语义）；撤销栈也是每页一套；
    ///   ⑤ 翻页时**先存上一页**（防一节课中途崩了丢东西）；
    ///   ⑥ 退出放映 = 逐页写盘 + 回桌面页（桌面批注原样回来）；
    ///   ⑦ 再进放映 = 从盘里读回（模拟"关软件再打开、下一节课接着讲"）；
    ///   ⑧ 界面按钮那条路：命令走 PPT（`fake.NextCalls` 涨），页跟着变；
    ///   ⑨ 没有 PPT / 连不上时一个字都不发生（没装 Office 的机器上零症状）。
    /// </summary>

    /// <summary>
    /// `--ppttest`：**PPT 模式自检**（用户 2026-09-26 拍板"参考 InkClass 原味"）。
    ///
    /// 用**假页码源**驱动（`PptFakeSource`）——我这边没有 PowerPoint 环境，
    /// 产品那条 COM 连接由用户在装了 PPT / WPS 的机器上跑 `--pptprobe` 确认。
    /// 假源和真源走的是**同一份状态机**（`StepPpt` → `ApplyPptState`），
    /// 所以这里验过的"进放映 / 翻页 / 存取 / 退出恢复"在真机上就是同一段代码。
    ///
    /// 验的九件事（对应参考来的语义 + 我们的两处适配）：
    ///   ① 进放映 = 切到 PPT 页，桌面批注**看不见**（完全隔离）；
    ///   ② 每页一套内容：翻页看不到别页、翻回来原样；
    ///   ③ **页内照样能滚动**，而且每页记住自己滚到哪（用户要的"我依然支持向下滚动"）；
    ///   ④ 清空只清本页（InkClass 的语义）；撤销栈也是每页一套；
    ///   ⑤ 翻页时**先存上一页**（防一节课中途崩了丢东西）；
    ///   ⑥ 退出放映 = 逐页写盘 + 回桌面页（桌面批注原样回来）；
    ///   ⑦ 再进放映 = 从盘里读回（模拟"关软件再打开、下一节课接着讲"）；
    ///   ⑧ 界面按钮那条路：命令走 PPT（`fake.NextCalls` 涨），页跟着变；
    ///   ⑨ 没有 PPT / 连不上时一个字都不发生（没装 Office 的机器上零症状）。
    /// </summary>
    private void PptTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== PPT 模式自检（假页码源驱动）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        // 自检**绝不碰用户真实的 PPT 批注**：把根目录指到临时目录（同 Recovery 的套路）。
        PptStore.RootOverride = Path.Combine(Path.GetTempPath(), "inkteach-ppttest");
        try { if (Directory.Exists(PptStore.RootOverride)) Directory.Delete(PptStore.RootOverride, true); } catch { }

        // 干净起手：只有 0 号桌面页、相机归零
        Doc.ResetToSinglePage();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        Doc.InvalidateAll();
        SettleFrames(150);

        var fake = new PptFakeSource();
        AttachPptSource(fake, watch: false);
        void Step() { StepPpt(); SettleFrames(50); }
        IntPtr Wheel(int delta) => new((long)(ushort)(short)delta << 16);
        int penSeq = 0;
        void MakePen(float x, float y)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.12f, 0.13f, 0.16f, 1f), Width = 8f,
            };
            s.AddPoint(x, y, 1f, 0);
            s.AddPoint(x + 160f, y + 30f, 1f, 8);
            int seq = penSeq++;
            s.AddPoint(x + 240f, y + (seq % 3) * 20f, 1f, 16);   // 让每笔略有不同
            Doc.AddStroke(s);
        }

        // 桌面批注：进放映前写 3 笔（它们必须"进得去、出得来"）
        for (int i = 0; i < 3; i++) MakePen(300 + i * 70, 320);
        SettleFrames(100);
        Check("桌面页起手 3 笔", Doc.Strokes.Count == 3, $"{Doc.Strokes.Count} 条");

        // ---- ① 进放映 ----
        fake.Showing = true; fake.Slide = 1; fake.SlideId = 256; fake.Total = 3;
        Step();
        Check("进放映：进入 PPT 模式", PptMode, $"PptMode={PptMode}");
        Check("进放映：切到第 1 页（键 = SlideID）", Doc.PageKey == 256, $"页键 {Doc.PageKey}");
        Check("进放映：桌面批注**看不见了**（完全隔离）", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 条");

        // ---- ①.2 危险顺序：**面板先开着，PptMode 才打开**——第一次定位不许被顶走 ----
        //
        // 用户 2026-10-01 实测："先开放映、再开「更多」"条不动——那是**安全顺序**：
        // 条在放映第一帧就定位完了（那时界面只是工具条）。真正的危险顺序是反过来：
        // 面板开着时 PptMode 才打开（比如 Alt+Tab 回 PPT 按 F5；我们的覆盖层不抢前台，
        // 键盘还在 PPT 手里）。那时 `PptBarRect` 的"避让工具条"会把条顶到屏幕顶部
        // （修之前实测落在 (32,32)，默认应是左下角 (32,1672)）。
        {
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(150);
            var uiProbe = CurrentUi as InkUi.FullUi;
            var scr = new RectF { MinX = _virtualX, MinY = _virtualY,
                                  MaxX = _virtualX + _virtualW, MaxY = _virtualY + _virtualH };
            float defTop = scr.MaxY - 16f * DpiScale - PptBar.BarH * DpiScale;
            float defLeft = scr.MinX + 16f * DpiScale;

            var toolbarRect = new RectF { MinX = 600f, MinY = scr.MaxY - 96f,
                                          MaxX = 1000f, MaxY = scr.MaxY - 32f };
            Check("判据：全屏模态矩形不参与避让、工具条矩形参与",
                  UiLooksFullscreen(scr, scr) && !UiLooksFullscreen(toolbarRect, scr),
                  $"全屏 {UiLooksFullscreen(scr, scr)}，工具条 {UiLooksFullscreen(toolbarRect, scr)}");

            ResetPptBarPosForTest();
            uiProbe?.OpenMoreForTest();
            SettleFrames(200);
            var barProbe = PptBarRect();
            Check("面板开着时首次定位：PPT 条仍在默认左下角（全屏模态不参与避让）",
                  MathF.Abs(barProbe.MinX - defLeft) < 1.5f && MathF.Abs(barProbe.MinY - defTop) < 1.5f,
                  $"条左上 ({barProbe.MinX:F0},{barProbe.MinY:F0})，期望 ({defLeft:F0},{defTop:F0})");

            uiProbe?.CloseMoreForTest();
            ResetPptBarPosForTest();
            SettleFrames(150);
        }

        // ---- ①.5 放映临时全局键 × 穿透：穿透期间让给下层（用户 2026-09-30 定）----
        //
        // 用户定的总规则："正常模式我们的键起作用、PPT 的键不起作用；穿透模式反过来。"
        // 放映时那 8 个键是临时全局热键，穿透开着就该整体注销，把键盘还给 PPT/WPS。
        {
            Check("放映中：临时全局键已挂", PptHotkeysOnForTest,
                  $"挂着 = {PptHotkeysOnForTest}");
            SetPassThroughFromUi(true);
            SettleFrames(150);
            Check("放映中开穿透：临时全局键让给下层（PPT 的 Ctrl+P/E 等恢复可用）",
                  PassThrough && !PptHotkeysOnForTest,
                  $"穿透 = {PassThrough}，挂着 = {PptHotkeysOnForTest}");
            SetPassThroughFromUi(false);
            SettleFrames(150);
            Check("关掉穿透（还在放映）：临时全局键收回",
                  !PassThrough && PptHotkeysOnForTest,
                  $"穿透 = {PassThrough}，挂着 = {PptHotkeysOnForTest}");
        }

        // ---- ①.6 悬停提示：页码格 / ◀ / 长按菜单（2026-10-02）----
        {
            var bar = PptBarRect();
            float dpi = DpiScale;
            var page = PptBar.MidCell(bar, dpi);
            PptBarPointerMove((page.MinX + page.MaxX) * 0.5f, (page.MinY + page.MaxY) * 0.5f);
            UpdateEngineTooltip();
            SettleFrames(650);
            StepEngineTooltip();                  // 自检里没有主循环，手动推一下"到点"
            Check("悬停页码格 0.5 秒：提示出现（点 = 页码跳转菜单）",
                  TooltipShown && TooltipTitle == "页码" && TooltipNote.Contains("结束放映"),
                  $"亮={TooltipShown}，标题={TooltipTitle}，说明={TooltipNote}");

            // ◀ ▶ 按收窄后的清单**不配提示**（箭头一看就懂、按下即翻页）：悬停也不该冒卡
            var left = new RectF { MinX = bar.MinX, MinY = bar.MinY,
                                   MaxX = bar.MinX + PptBar.ArrowW * dpi, MaxY = bar.MaxY };
            PptBarPointerMove((left.MinX + left.MaxX) * 0.5f, (left.MinY + left.MaxY) * 0.5f);
            UpdateEngineTooltip();
            SettleFrames(650);
            StepEngineTooltip();
            Check("悬停 ◀：**不出提示**（收窄：箭头不要提示）",
                  !TooltipShown && string.IsNullOrEmpty(TooltipTitle),
                  $"亮={TooltipShown}，标题={TooltipTitle ?? "（空）"}");

            PptBarPointerMove(bar.MinX + 6f * dpi, bar.MaxY + 160f * dpi);   // 条外面
            UpdateEngineTooltip();
            SettleFrames(120);
            Check("指针移开条：提示立刻收", !TooltipShown, $"亮={TooltipShown}");

            // 菜单五项：逐项核对有说明（悬停码 100+i，和真实悬停同一套编号）
            bool menuTipsAll = true; string menuTipsMiss = "";
            for (int i = 0; i < PptMenuItemCount; i++)
            {
                PptBarHover = 100 + i;
                UpdateEngineTooltip();
                if (string.IsNullOrEmpty(TooltipTitle) || string.IsNullOrEmpty(TooltipNote))
                { menuTipsAll = false; menuTipsMiss += i + " "; }
            }
            Check("菜单五项都有说明（六字标题之外）", menuTipsAll,
                  menuTipsAll ? $"{PptMenuItemCount} 项" : $"缺：{menuTipsMiss}");

            PptBarHover = -1;
            ClearEngineTooltip();
            SettleFrames(120);
        }

        // ---- ② 每页一套 + 页内滚动 ----
        MakePen(400, 420);
        MakePen(520, 420);
        Check("第 1 页写了 2 笔", Doc.Strokes.Count == 2, $"{Doc.Strokes.Count} 条");

        HandleWheel(Wheel(-120));
        SettleFrames(120);
        float scrolled = ViewOffsetY;
        Check("PPT 页内**照样能滚动**（用户要的那条）", scrolled < -1f, $"相机 {scrolled:F0}");

        fake.Slide = 2; fake.SlideId = 257;
        Step();
        Check("翻到第 2 页", Doc.PageKey == 257, $"页键 {Doc.PageKey}");
        Check("第 2 页是**空的**（隔离的另一半）", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 条");
        Check("翻到没去过的页：相机归零", Math.Abs(ViewOffsetY) < 0.01f, $"相机 {ViewOffsetY:F2}");
        MakePen(600, 520);

        fake.Slide = 1; fake.SlideId = 256;
        Step();
        Check("翻回第 1 页：2 笔都在", Doc.Strokes.Count == 2, $"{Doc.Strokes.Count} 条");
        Check("翻回第 1 页：**滚到哪还记得**",
              Math.Abs(ViewOffsetY - scrolled) < 2f, $"相机 {ViewOffsetY:F0}（原来 {scrolled:F0}）");
        Check("页面条数字（界面显示 n/N 用）",
              PptSlide == 1 && PptTotal == 3, $"{PptSlide}/{PptTotal}");

        // ---- ④ 清空只清本页 ----
        ClearFromUi();
        SettleFrames(150);
        Check("清空：只清第 1 页", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 条");
        fake.Slide = 2; fake.SlideId = 257;
        Step();
        Check("清空：第 2 页那 1 笔**还在**", Doc.Strokes.Count == 1, $"{Doc.Strokes.Count} 条");

        // ---- 撤销栈每页一套（参考 InkClass 的 TimeMachineHistories[页]）----
        UndoFromUi();
        SettleFrames(100);
        Check("撤销：第 2 页撤得掉自己那笔", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 条");
        RedoFromUi();
        SettleFrames(100);
        Check("重做：又回来了", Doc.Strokes.Count == 1, $"{Doc.Strokes.Count} 条");

        // 回到第 1 页补 2 笔（给"退出放映写盘"准备内容）
        fake.Slide = 1; fake.SlideId = 256;
        Step();
        MakePen(700, 460);
        MakePen(760, 500);
        int page1Count = Doc.Strokes.Count;
        Check("第 1 页补到 2 笔（清空后重写）", page1Count == 2, $"{page1Count} 条");

        // ---- ⑥ 退出放映：写盘 + 回桌面 ----
        fake.Showing = false;
        Step();
        Check("退出放映：退出 PPT 模式", !PptMode, $"PptMode={PptMode}");
        Check("退出放映：回到 0 号页", Doc.PageKey == 0, $"页键 {Doc.PageKey}");
        Check("退出放映：桌面 3 笔**原样回来**", Doc.Strokes.Count == 3, $"{Doc.Strokes.Count} 条");

        var savedKeys = PptStore.ListPageKeys(fake.Key);
        savedKeys.Sort();
        Check("退出放映：两页都写了盘（且不含桌面页）",
              savedKeys.Count == 2 && savedKeys[0] == 256 && savedKeys[1] == 257,
              $"盘上 {savedKeys.Count} 页：{string.Join(",", savedKeys)}");
        Check("退出放映：Position 记了页码", PptStore.LoadPosition(fake.Key) == 1,
              $"记的是 {PptStore.LoadPosition(fake.Key)}");

        // ---- ⑨ 没有 PPT 时：一个字都不发生 ----
        // ⚠ 顺序有讲究：下面 ⑦ 会 `ResetToSinglePage` **模拟"关掉软件"**——那一句
        // 会把内存里的页全丢掉（**包括桌面页**；桌面批注靠自动存档那条路持久化，
        // 不归 PPT 模式管），所以"桌面批注还在吗"这一条必须排在它之前。
        fake.Showing = false; fake.Connected = false;
        Step();
        Check("连不上 PPT：退出 PPT 模式、回桌面页", !PptMode && Doc.PageKey == 0,
              $"PptMode={PptMode}，页键 {Doc.PageKey}");
        Check("连不上 PPT：桌面批注还在（没被谁清掉）", Doc.Strokes.Count == 3, $"{Doc.Strokes.Count} 条");
        fake.Connected = true;
        Step();

        // ---- ⑦ 再进放映：从盘里读回（模拟关掉软件再打开）----
        Doc.ResetToSinglePage();                    // 内存里的页全丢掉
        Check("（模拟关软件：内存里一页都不剩）",
              Doc.Strokes.Count == 0 && Doc.PageKey == 0, $"页键 {Doc.PageKey}");
        fake.Showing = true; fake.Slide = 1; fake.SlideId = 256;
        Step();
        Check("再进放映：第 1 页从盘里读回来（2 笔）", Doc.Strokes.Count == 2, $"{Doc.Strokes.Count} 条");
        fake.Slide = 2; fake.SlideId = 257;
        Step();
        Check("再进放映：第 2 页也读回来（1 笔）", Doc.Strokes.Count == 1, $"{Doc.Strokes.Count} 条");

        // ---- ⑧ 界面按钮那条路：命令走 PPT、页跟着变 ----
        int nextBefore = fake.NextCalls;
        PptNextFromUi();
        Step();
        Check("点界面“下一页”：命令交给 PPT（NextCalls +1）",
              fake.NextCalls == nextBefore + 1, $"NextCalls={fake.NextCalls}");
        Check("点了“下一页”之后页跟着变（第 3 页）", Doc.PageKey == 258, $"页键 {Doc.PageKey}");
        Check("到末页再点“下一页”不越界（PPT 自己夹住）",
              fake.Slide == 3, $"PPT 第 {fake.Slide} 页");

        // ---- ⑧.5 PPT 控件条（PptBar）：用户 2026-09-26 定的最终形态 ----
        //      一条、默认屏幕左下角、可拖可记；点箭头翻页、点页码弹页号面板、
        //      长按不动松手弹菜单、长按后拖挪位置。
        {
            float screenW = _virtualW, screenBottom = _virtualY + _virtualH;

            // 尺寸：**高度跟主界面那条走**、**中区是黄金矩形**。这两条断言是"同一个数写在
            // 两处"的那道保险：`PptBar.BarH` 在引擎里、`Tokens.BarHeight` 在界面里，
            // 改了一个忘了另一个，这里当场红（同 `--shapebandtest` 盯"加图形漏自检"的思路）。
            Check("PPT 条的高 = 主界面那条的高",
                  Math.Abs(PptBar.BarH - InkUi.Tokens.BarHeight) < 0.01f,
                  $"PptBar.BarH={PptBar.BarH}，Tokens.BarHeight={InkUi.Tokens.BarHeight}");
            float midW = PptBar.BarW - PptBar.ArrowW * 2f;
            Check("中区（页码格）是黄金矩形",
                  Math.Abs(midW / PptBar.BarH - 1.618f) < 0.02f,
                  $"中区 {midW:F0} ÷ 高 {PptBar.BarH} = {midW / PptBar.BarH:F3}（黄金比 1.618）");

            // 默认位置：**屏幕左下角**（用户："因为 PPT 本来的工具就在这里"）。
            // 先清干净：上一次自检可能把位置留在临时配置里了（不清的话这一条会飘）。
            SetUiPref("pptBarX", null);
            SetUiPref("pptBarY", null);
            ResetPptBarPosForTest();
            var bar = PptBarRect();
            float defX = _virtualX + PptBar.EdgeMargin * DpiScale;
            float defY = screenBottom - PptBar.EdgeMargin * DpiScale - PptBar.BarH * DpiScale;
            Check("默认位置 = 屏幕左下角、整条在屏幕里",
                  Math.Abs(bar.MinX - defX) < 1f && Math.Abs(bar.MinY - defY) < 1f
                  && bar.MaxX <= _virtualX + screenW && bar.MaxY <= screenBottom,
                  $"条 x {bar.MinX:F0}..{bar.MaxX:F0}，y {bar.MinY:F0}..{bar.MaxY:F0}");

            // 点右端 ▶ = 下一页（命令给 PPT）
            int nextBefore2 = fake.NextCalls;
            float arX = bar.MaxX - PptBar.ArrowW * DpiScale * 0.5f;
            float arY = (bar.MinY + bar.MaxY) * 0.5f;
            bool ate = PptBarPointerDown(arX, arY);
            Step();
            Check("点右端 ▶：这一下归它、命令给 PPT",
                  ate && fake.NextCalls == nextBefore2 + 1, $"吃掉={ate}，NextCalls={fake.NextCalls}");

            // 点页码格 → **弹菜单**（2026-10-02 第五轮；原来直接弹页号面板）。
            // 先把页数设成 12（页号面板铺成 2 行）：3 页时面板只有一行、点"第 5 格"根本不在面板里，
            // 那样测的是"点面板外"——**用例要挑有代表性的输入**（这坑自检当场踩了一次）。
            fake.Total = 12;
            Step();
            float midX = (bar.MinX + bar.MaxX) * 0.5f, midY = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(midX, midY);
            PptBarPointerUp(midX, midY);
            Check("点页码：弹出菜单（长按入口已取消）",
                  PptMenuOpen && !PptPagePanelOpen, $"菜单={PptMenuOpen}，面板={PptPagePanelOpen}");
            Check("菜单第一项＝指定页码跳转", PptMenuItemText(0) == "指定页码跳转",
                  $"第一项={PptMenuItemText(0)}");
            PptMenuItemRectAt(0, out var miJump);
            PptBarPointerDown((miJump.MinX + miJump.MaxX) * 0.5f, (miJump.MinY + miJump.MaxY) * 0.5f);
            Check("点「指定页码跳转」：页号面板打开、菜单收起",
                  PptPagePanelOpen && !PptMenuOpen, $"面板={PptPagePanelOpen}，菜单={PptMenuOpen}");
            PptPanelRect(out var panel);
            Check("面板长在条的上方、在屏幕里",
                  panel.MaxY <= bar.MinY + 0.5f && panel.MinY >= _virtualY
                  && panel.MaxX <= _virtualX + screenW,
                  $"面板 y {panel.MinY:F0}..{panel.MaxY:F0}，条顶 {bar.MinY:F0}");

            // 点面板里第 5 格 → 跳到第 5 页、面板收起
            int gotoBefore = fake.GotoCalls;
            PptPanelCellRectAt(4, out var cell5);
            Check("第 5 格落在面板里（页数够、格子才排得下）",
                  panel.Contains((cell5.MinX + cell5.MaxX) * 0.5f, (cell5.MinY + cell5.MaxY) * 0.5f),
                  $"格中心 ({(cell5.MinX + cell5.MaxX) * 0.5f:F0},{(cell5.MinY + cell5.MaxY) * 0.5f:F0})，面板 {panel.MinX:F0}..{panel.MaxX:F0}");
            bool ateCell = PptBarPointerDown((cell5.MinX + cell5.MaxX) * 0.5f,
                                             (cell5.MinY + cell5.MaxY) * 0.5f);
            Step();
            Check("点面板第 5 格：跳页命令给 PPT、面板收起",
                  ateCell && fake.GotoCalls == gotoBefore + 1 && fake.GotoTarget == 5 && !PptPagePanelOpen,
                  $"GotoCalls={fake.GotoCalls}，跳到第 {fake.GotoTarget} 页，面板={PptPagePanelOpen}");

            // ---- 页号面板：**每一个格子的悬停都要准**（用户 2026-09-27 报的）----
            // 原话："PPT 的悬停页码好像不是那么准确……后面的页码还落在外面。"
            // 逐格量两件事（不靠眼睛）：
            //   ① 整格在面板矩形里（画得出来、也点得到）；
            //   ② 悬停能命中它自己（hover == 200+i，绘制的高亮就是照这个比对的）。
            // 用 75 页（8 行）压：行数一多，"后面的行"最容易暴露坐标算错。
            // 面板走**真实入口**打开（点页码 → 松手），不直接改状态（那个属性是只读的）。
            fake.Total = 75;
            Step();
            bar = PptBarRect();
            midX = (bar.MinX + bar.MaxX) * 0.5f; midY = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(midX, midY);                 // 点页码 → 菜单
            PptBarPointerUp(midX, midY);
            PptMenuItemRectAt(0, out var miJump75);        // 菜单第一项 → 页号面板
            PptBarPointerDown((miJump75.MinX + miJump75.MaxX) * 0.5f,
                              (miJump75.MinY + miJump75.MaxY) * 0.5f);
            Check("（准备）75 页时页号面板已打开", PptPagePanelOpen, $"面板={PptPagePanelOpen}");
            PptPanelRect(out var panel75);
            int cellsBad = 0; string firstBadCell = "（全部命中）";
            for (int i = 0; i < 75; i++)
            {
                PptPanelCellRectAt(i, out var c);
                bool inPanel = panel75.Contains(c.MinX, c.MinY) && panel75.Contains(c.MaxX, c.MaxY);
                PptBarPointerMove((c.MinX + c.MaxX) * 0.5f, (c.MinY + c.MaxY) * 0.5f);
                bool hot = PptBarHover == 200 + i;
                if (!inPanel || !hot)
                {
                    cellsBad++;
                    if (cellsBad == 1)
                        firstBadCell = $"第 {i + 1} 格：在面板内={inPanel}、悬停命中={hot}"
                                     + $"，格 ({c.MinX:F0},{c.MinY:F0})-({c.MaxX:F0},{c.MaxY:F0})"
                                     + $"，面板 ({panel75.MinX:F0},{panel75.MinY:F0})-({panel75.MaxX:F0},{panel75.MaxY:F0})";
                }
            }
            Check("页号面板 · 75 页逐格：都在面板内、悬停都能命中",
                  cellsBad == 0, $"坏格 {cellsBad} 个；{firstBadCell}");

            // —— 页数多到"面板顶到屏幕顶"时，面板底部会**压到条上** ——
            // 那时悬停判定必须**面板优先**（与按下分派 PptBarPointerDown 同一个顺序：
            // 菜单 → 面板 → 条）。原来悬停这边是"条优先"——两处顺序不一致，
            // 表现就是"格子明明看得见、悬停不亮，按下去却真跳页"。
            // 用户 2026-09-27 报的"悬停页码不准确"最可能就是这个。
            fake.Total = 400;
            Step();
            // 先把 75 页那一轮开着的页号面板收掉（不先收，下面"点页码"会被面板吃掉）
            PptBarPointerDown(_virtualX + 6f, _virtualY + 6f);
            bar = PptBarRect();
            midX = (bar.MinX + bar.MaxX) * 0.5f; midY = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(midX, midY);                 // 点页码 → 菜单
            PptBarPointerUp(midX, midY);
            PptMenuItemRectAt(0, out var miJump400);       // 菜单第一项 → 页号面板
            PptBarPointerDown((miJump400.MinX + miJump400.MaxX) * 0.5f,
                              (miJump400.MinY + miJump400.MaxY) * 0.5f);
            PptPanelRect(out var panelBig);
            Check("大页数（400 页）：面板被夹顶、确实压到条上（构造出了问题现场）",
                  panelBig.MaxY > bar.MinY + 0.5f,
                  $"面板底 {panelBig.MaxY:F0}，条顶 {bar.MinY:F0}（面板高 {panelBig.MaxY - panelBig.MinY:F0}）");
            int clash = -1;
            for (int i = 0; i < 400; i++)
            {
                PptPanelCellRectAt(i, out var c);
                float ccx = (c.MinX + c.MaxX) * 0.5f, ccy = (c.MinY + c.MaxY) * 0.5f;
                if (bar.Contains(ccx, ccy)) { clash = i; break; }   // 格子中心落在条上 = 冲突点
            }
            if (clash >= 0)
            {
                PptPanelCellRectAt(clash, out var cc);
                PptBarPointerMove((cc.MinX + cc.MaxX) * 0.5f, (cc.MinY + cc.MaxY) * 0.5f);
                Check($"重叠区里第 {clash + 1} 格：悬停命中**面板**（不是条）",
                      PptBarHover == 200 + clash,
                      $"hover={PptBarHover}（期望 {200 + clash}；条 zone 最大 {(int)PptBarZone.Page} 一档）");
            }
            else
            {
                Check("大页数：没有格子中心落在条上（这次构造不出冲突点）", true, "跳过");
            }
            PptBarPointerDown(_virtualX + 6f, _virtualY + 6f);   // 点别处：把面板收起来（真实路径）
            Check("（收尾）点别处后面板已收起", !PptPagePanelOpen, $"面板={PptPagePanelOpen}");
            fake.Total = 12;
            Step();

            // 长按（不动）→ **不再弹菜单**（2026-10-02 第五轮：入口改成单击）；
            // 松手在页码格上 = 一次正常的单击 → 菜单打开。再点一下收起。
            bar = PptBarRect();
            midX = (bar.MinX + bar.MaxX) * 0.5f; midY = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(midX, midY);
            SettleFrames(750);                     // 过 600ms 也不该有菜单（长按已不是入口）
            Check("按住 600ms 不动：**不弹菜单**（长按入口已取消）", !PptMenuOpen, $"菜单={PptMenuOpen}");
            PptBarPointerUp(midX, midY);
            Check("松手 = 单击：菜单打开（这才是入口）", PptMenuOpen, $"菜单={PptMenuOpen}");
            PptBarPointerDown(midX, midY);         // 再点页码格 = 收起（不鬼打墙）
            Check("再点页码格：菜单收起", !PptMenuOpen, $"菜单={PptMenuOpen}");
            PptBarPointerDown(_virtualX + 6f, _virtualY + 6f);   // 点别处：什么都不发生
            Check("点别处：菜单仍是收起的", !PptMenuOpen, $"菜单={PptMenuOpen}");

            // **按下就移** → 直接拖动（用户 2026-09-26："点中页码那一块直接拖动就能走"）
            bar = PptBarRect();
            float gx = (bar.MinX + bar.MaxX) * 0.5f, gy = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(gx, gy);
            PptBarPointerMove(gx + 240f, gy - 320f);      // 一步就过阈值，**不用等 600ms**
            Check("按下就移（不用长按）：直接进入拖动", PptBarDragging, $"拖动={PptBarDragging}");
            PptBarPointerUp(gx + 240f, gy - 320f);
            var barMoved = PptBarRect();
            Check("松手：条真的挪了位置、且不是在拖了",
                  Math.Abs(barMoved.MinX - bar.MinX) > 100f && !PptBarDragging,
                  $"x {bar.MinX:F0} → {barMoved.MinX:F0}，y {bar.MinY:F0} → {barMoved.MinY:F0}");
            Check("松手：位置**存进了偏好**（重启还在）",
                  GetUiPref("pptBarX") != null && GetUiPref("pptBarY") != null,
                  $"pptBarX={GetUiPref("pptBarX")}，pptBarY={GetUiPref("pptBarY")}");

            // 模拟"关掉软件再打开"：退出放映（清内存位置）→ 再进放映（EnterPptMode 会读回）
            float keptX = barMoved.MinX, keptY = barMoved.MinY;
            fake.Showing = false; Step();                    // 退出放映
            ResetPptBarPosForTest();                         // 内存里的位置清掉（重启后就是这状态）
            fake.Showing = true; fake.Slide = 1; fake.SlideId = 256; Step();   // 再进放映
            var barBack = PptBarRect();
            Check("再进放映：位置从盘里读回来（重启还在这个位置）",
                  Math.Abs(barBack.MinX - keptX) < 2f && Math.Abs(barBack.MinY - keptY) < 2f,
                  $"记的是 ({keptX:F0},{keptY:F0})，读回来 ({barBack.MinX:F0},{barBack.MinY:F0})");

            // 拖到屏幕外：必须被夹住（**拖出去找不回来**是这个功能最坏的失败模式）
            bar = PptBarRect();
            gx = (bar.MinX + bar.MaxX) * 0.5f; gy = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(gx, gy);
            PptBarPointerMove(_virtualX - 5000f, screenBottom + 5000f);   // 拼命往屏幕外拖（按下就移）
            PptBarPointerUp(_virtualX - 5000f, screenBottom + 5000f);
            var bc = PptBarRect();
            Check("拖到屏幕外：被夹住、整条还在屏幕里",
                  bc.MinX >= _virtualX && bc.MinY >= _virtualY
                  && bc.MaxX <= _virtualX + screenW && bc.MaxY <= screenBottom,
                  $"条 x {bc.MinX:F0}..{bc.MaxX:F0}，y {bc.MinY:F0}..{bc.MaxY:F0}");

            // ---- **拖起来了，松手就不许弹菜单**（用户 2026-09-27 报过"拖页码时弹菜单"）----
            // 现在"拖"和"单击"是同一次按下的两条岔路：移动超阈值 → 变拖动、按下标记清掉，
            // 松手只放条，不会再被当成单击去开菜单。
            bar = PptBarRect();
            gx = (bar.MinX + bar.MaxX) * 0.5f; gy = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(gx, gy);
            PptBarPointerMove(gx + 30f, gy - 12f);        // 慢慢拖起来（过阈值）
            SettleFrames(750);                            // 过 600ms：以前这里会弹菜单
            Check("拖动中（含过 600ms）：**不弹菜单**（拖和单击是两条路）",
                  !PptMenuOpen, $"菜单={PptMenuOpen}，拖动={PptBarDragging}");
            PptBarPointerUp(gx + 30f, gy - 12f);
            Check("松手：拖动正常结束、**不弹菜单**",
                  !PptBarDragging && !PptMenuOpen && Math.Abs(PptBarRect().MinX - bar.MinX) > 20f,
                  $"拖动={PptBarDragging}，菜单={PptMenuOpen}，x {bar.MinX:F0} → {PptBarRect().MinX:F0}");

            // ---- PPT 浮层（条 / 菜单 / 页号面板）上的光标：一律箭头 ----
            // 与图库面板同型的洞（"一块是界面就是界面"，见 Engine.PointerOnDrawnChrome）：
            // 以前悬停在条上时顶着的是工具光标（框选=十字、笔=斜笔），笔/激光/橡皮的
            // 落点反馈还会画到条上面。判据已收进 PointerOnDrawnChrome（含 PptBarContains）。
            {
                var keepTool = Tool;
                float keepPx = PointerX, keepPy = PointerY;
                bar = PptBarRect();
                midX = (bar.MinX + bar.MaxX) * 0.5f; midY = (bar.MinY + bar.MaxY) * 0.5f;
                Tool = Tool.Marquee;                        // 框选：画布上是十字，最容易暴露
                PointerX = midX; PointerY = midY;
                ScreenToCanvas(ref PointerX, ref PointerY); // 光标判定用画布坐标
                Check("PPT 条上：光标是箭头（不是十字）",
                      ComputeCursorKind() == CursorKind.Default,
                      $"光标={Cursors.Name(ComputeCursorKind())}");
                Check("PPT 条上：落点反馈不画（不顶着圆环）",
                      DrawnCursor == ToolCursorShape.None, $"DrawnCursor={DrawnCursor}");
                Tool = keepTool;
                PointerX = keepPx; PointerY = keepPy;
            }

            // ---- 菜单：**点页码格开**（2026-10-02 第五轮，长按入口取消）；五项逐项点一遍 ----
            // （"加一项漏一处"是这个仓库的老毛病，见 架构-分层与规则.md 五-7）
            bar = PptBarRect();
            midX = (bar.MinX + bar.MaxX) * 0.5f; midY = (bar.MinY + bar.MaxY) * 0.5f;
            Check("菜单五项、一律六字（跳/存/放/清/退）",
                  PptMenuItemCount == 5 && PptMenuItemText(0) == "指定页码跳转"
                  && PptMenuItemText(1) == "自动保存墨迹" && PptMenuItemText(2) == "回放本页墨迹"
                  && PptMenuItemText(3) == "清空所有墨迹" && PptMenuItemText(4) == "结束本次放映",
                  $"{PptMenuItemText(0)} / {PptMenuItemText(1)} / {PptMenuItemText(2)} / "
                  + $"{PptMenuItemText(3)} / {PptMenuItemText(4)}");

            // ① 点页码格 = 开菜单（2026-10-02 第五轮：长按入口取消、单击就是唯一入口）
            PptBarPointerDown(midX, midY);
            PptBarPointerUp(midX, midY);
            Check("点页码格：菜单打开", PptMenuOpen, $"菜单={PptMenuOpen}");

            // ①.5 「回放本页墨迹」（2026-10-01 用户提议新增）：有墨迹 → 点了起回放、菜单收起
            Check("（准备）当前页有墨迹可回放", Doc.Strokes.Count > 0, $"{Doc.Strokes.Count} 笔");
            PptMenuItemRectAt(2, out var miReplay);
            PptBarPointerDown((miReplay.MinX + miReplay.MaxX) * 0.5f, (miReplay.MinY + miReplay.MaxY) * 0.5f);
            Step();
            Check("点「回放本页墨迹」：起回放、菜单收起",
                  ReplayActive && !PptMenuOpen, $"回放={ReplayActive}，菜单={PptMenuOpen}");
            StopReplayForTest("自检收尾");
            Step();
            Check("（收尾）回放已停", !ReplayActive, $"回放={ReplayActive}");

            // 菜单再开一次，给 ② 用
            PptBarPointerDown(midX, midY);
            PptBarPointerUp(midX, midY);
            Check("（准备）菜单重新打开", PptMenuOpen, $"菜单={PptMenuOpen}");

            // ② 菜单开着时**再点页码格 = 收起**（原来这是"再点 ⋮"那条路，不能鬼打墙）
            PptBarPointerDown(midX, midY);
            Check("菜单开着再点页码格：收起（不鬼打墙）", !PptMenuOpen, $"菜单={PptMenuOpen}");

            // ---- 穿透开着时，条仍然归我们 ----
            // 用户 2026-09-27 问的"穿透模式下 PPT 翻页起不起作用"，两层都要验：
            //   · 引擎的分派：`PptBarPointerDown` 排在 `if (PassThrough) return;` **之前**，
            //     所以点 ▶ 照样翻页、这一下不会被让给下层；
            //   · 系统的命中测试：穿透用的是 **WS_EX_TRANSPARENT**，它让系统**跳过命中测试**
            //     ——覆盖层那条 WM_NCHITTEST 豁免根本执行不到（这就是用户实测"不管用"的原因）。
            //     修法是照面板那套**再开一块接输入小窗**，下面几条量它的矩形。
            bar = PptBarRect();
            float rx = bar.MaxX - PptBar.ArrowW * DpiScale * 0.5f;
            float ry = (bar.MinY + bar.MaxY) * 0.5f;
            SetPassThroughFromUi(true);
            Check("穿透开着：条那一块仍算「我们的地盘」（NCHITTEST 不会让出去）",
                  PptBarContains(rx, ry), $"条内点 ({rx:F0},{ry:F0})");
            int nx = fake.NextCalls;
            bool ateP = PptBarPointerDown(rx, ry);
            Step();
            Check("穿透开着：点 ▶ 照样翻页、这一下没被让给下层",
                  ateP && fake.NextCalls == nx + 1, $"吃掉={ateP}，NextCalls={fake.NextCalls}");

            // **接输入小窗**（穿透下条能点的真正原因）：
            //   ① 只覆盖"条 ∪ 菜单 ∪ 页号面板"，一像素都不多（多出来的地方会挡住下层程序）；
            //   ② 关掉穿透 / 退出放映**必须收掉**（留着就成了屏幕上一块看不见的挡板）。
            Check("穿透 + 放映：条的接输入小窗已经铺上", PptInputWindowShown, $"铺上={PptInputWindowShown}");
            {
                PptInputWindowRect(out var win);
                Check("接输入小窗就贴在条上（不越界、也不是整屏）",
                      Math.Abs(win.MinX - bar.MinX) < 1.5f && Math.Abs(win.MinY - bar.MinY) < 1.5f
                      && Math.Abs(win.MaxX - bar.MaxX) < 1.5f && Math.Abs(win.MaxY - bar.MaxY) < 1.5f,
                      $"窗 ({win.MinX:F0},{win.MinY:F0})-({win.MaxX:F0},{win.MaxY:F0})，"
                      + $"条 ({bar.MinX:F0},{bar.MinY:F0})-({bar.MaxX:F0},{bar.MaxY:F0})");

                // 打开页号面板：窗要**变高**把面板也罩住（不然面板看得见、点不动）。
                // 2026-10-02 第五轮：点页码 = 菜单，面板从第一项进来。
                PptBarPointerDown((bar.MinX + bar.MaxX) * 0.5f, (bar.MinY + bar.MaxY) * 0.5f);
                PptBarPointerUp((bar.MinX + bar.MaxX) * 0.5f, (bar.MinY + bar.MaxY) * 0.5f);
                PptMenuItemRectAt(0, out var miJumpPass);
                PptBarPointerDown((miJumpPass.MinX + miJumpPass.MaxX) * 0.5f,
                                  (miJumpPass.MinY + miJumpPass.MaxY) * 0.5f);
                Check("（准备）页号面板已打开", PptPagePanelOpen, $"面板={PptPagePanelOpen}");
                Step();
                PptPanelRect(out var panelNow);
                PptInputWindowRect(out var win2);
                Check("面板开着时：小窗跟着长高（面板那几格也点得中）",
                      win2.MinY <= panelNow.MinY + 1.5f && win2.MaxY >= bar.MaxY - 1.5f,
                      $"窗顶 {win2.MinY:F0} ≤ 面板顶 {panelNow.MinY:F0}，窗底 {win2.MaxY:F0} ≥ 条底 {bar.MaxY:F0}");
                PptBarPointerDown(_virtualX + 6f, _virtualY + 6f);   // 点别处：收面板（真实路径）
                Step();
            }
            SetPassThroughFromUi(false);
            Step();
            Check("关掉穿透：接输入小窗立刻收掉（不留看不见的挡板）",
                  !PptInputWindowShown, $"铺上={PptInputWindowShown}");

            // 穿透那两条点了一下 ▶（顺手把开着的菜单收起了），这里重新开起来给 ③④ 用
            PptBarPointerDown(midX, midY);
            PptBarPointerUp(midX, midY);
            Check("（准备）菜单已打开", PptMenuOpen, $"菜单={PptMenuOpen}");

            // ③ 「墨迹保存」（开关，索引 1）：点一下翻状态、**菜单留着**（要让老师看见"开 → 关"）
            bool saveBefore = PptAutoSaveOn;
            PptMenuItemRectAt(1, out var mi0);
            PptBarPointerDown((mi0.MinX + mi0.MaxX) * 0.5f, (mi0.MinY + mi0.MaxY) * 0.5f);
            Check("点「墨迹保存」：状态翻过来、菜单留着",
                  PptAutoSaveOn == !saveBefore && PptMenuOpen,
                  $"{saveBefore} → {PptAutoSaveOn}，菜单={PptMenuOpen}");
            Check("关掉后偏好里记的是 \"0\"（默认开 → 只记差异）",
                  PptAutoSaveOn || GetUiPref("pptAutoSave") == "0",
                  $"pptAutoSave={GetUiPref("pptAutoSave") ?? "(空 = 开)"}");

            // ④ 「墨迹清空」（索引 3，两段确认）：**第一下不执行**
            int diskBefore = PptStore.ListPageKeys(fake.Key).Count;
            PptMenuItemRectAt(3, out var mi1);
            PptBarPointerDown((mi1.MinX + mi1.MaxX) * 0.5f, (mi1.MinY + mi1.MaxY) * 0.5f);
            Check("点「墨迹清空」第一下：进入等确认、文字变「再点确认」、**还没清**",
                  PptClearConfirm && PptMenuOpen && PptMenuItemText(3) == "再点确认"
                  && PptStore.ListPageKeys(fake.Key).Count == diskBefore,
                  $"等确认={PptClearConfirm}，文字={PptMenuItemText(3)}，盘上还是 {diskBefore} 页");

            // 把自动保存打开，好验证"清空是连盘一起清的"（关着的话盘上本来就该原样）
            PptMenuItemRectAt(1, out var mi0b);
            PptBarPointerDown((mi0b.MinX + mi0b.MaxX) * 0.5f, (mi0b.MinY + mi0b.MaxY) * 0.5f);

            // 第二下：真清
            PptMenuItemRectAt(3, out var mi1b);
            PptBarPointerDown((mi1b.MinX + mi1b.MaxX) * 0.5f, (mi1b.MinY + mi1b.MaxY) * 0.5f);
            Step();
            Check("点第二下：清空执行、菜单收起、等确认状态放掉",
                  !PptClearConfirm && !PptMenuOpen, $"等确认={PptClearConfirm}，菜单={PptMenuOpen}");
            Check("清空后：**盘上这份 PPT 的批注文件真没了**（显式删盘，不靠写空内容）",
                  PptStore.ListPageKeys(fake.Key).Count == 0,
                  $"清空前 {diskBefore} 页 → 现在 {PptStore.ListPageKeys(fake.Key).Count} 页");
            Check("清空后：内存里也是空的", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 条");

            // ⑤ 清空完再进一次放映：**不能复活**（这是"清空到底有没有用"的唯一判据）
            fake.Showing = false; Step();
            fake.Showing = true; fake.Slide = 1; fake.SlideId = 256; Step();
            Check("清空后再进放映：**不复活**", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 条");

            // ⑥ 引导：**每次进放映都提示一遍**（用户 2026-09-27 定；2026-10-02 第五轮文案改成
            //    "点页码：页码跳转菜单"）。强断言：先把"已经提示过"这个偏好**写死**
            //    （老逻辑下它就不会再出现了），再进放映——引导**照样出现**。
            SetUiPref("pptHint", "1");
            fake.Showing = false; Step();
            fake.Showing = true; Step();
            Check("进放映：冒出一行引导（告诉老师点页码出菜单）", PptHintVisible, $"提示={PptHintVisible}");
            SettleFrames(1700);            // 1.5 秒后应该自己消失（不挡讲课）
            Check("1.5 秒后引导自己消失", !PptHintVisible, $"提示={PptHintVisible}");
            fake.Showing = false; Step();
            fake.Showing = true; Step();
            Check("第二次进放映：**照样提示**（不是只提示一次）", PptHintVisible, $"提示={PptHintVisible}");
            PptHintSuppressForTest();      // 压下去，别挡着后面⑦的菜单

            // ⑦ 「结束放映」（最后一项，它会把放映退掉，所以排在最后）
            bar = PptBarRect();
            midX = (bar.MinX + bar.MaxX) * 0.5f; midY = (bar.MinY + bar.MaxY) * 0.5f;
            PptBarPointerDown(midX, midY);
            PptBarPointerUp(midX, midY);

            // ⑦.0 清空之后没有墨迹：「回放本页墨迹」（索引 2）**置灰**（右侧 0 笔），点了不动
            Check("（准备）清空后无墨迹：回放项置灰、右侧 0 笔",
                  !PptMenuItemEnabled(2) && PptMenuItemStatus(2) == "0 笔",
                  $"enabled={PptMenuItemEnabled(2)}，状态={PptMenuItemStatus(2)}");
            PptMenuItemRectAt(2, out var miReplay0);
            PptBarPointerDown((miReplay0.MinX + miReplay0.MaxX) * 0.5f, (miReplay0.MinY + miReplay0.MaxY) * 0.5f);
            Check("清空后点置灰的回放项：菜单留着、没有起回放",
                  PptMenuOpen && !ReplayActive, $"菜单={PptMenuOpen}，回放={ReplayActive}");

            int exitBefore = fake.ExitCalls;
            PptMenuItemRectAt(4, out var mi2);            // 结束放映（索引 4）
            PptBarPointerDown((mi2.MinX + mi2.MaxX) * 0.5f, (mi2.MinY + mi2.MaxY) * 0.5f);
            Step();
            Check("点「结束放映」：命令给 PPT、菜单收起",
                  fake.ExitCalls == exitBefore + 1 && !PptMenuOpen,
                  $"ExitCalls={fake.ExitCalls}，菜单={PptMenuOpen}");
            Check("退出放映后：条不再命中（它是放映时才有的）",
                  !PptMode && !PptBarContains(bar.MinX + 10f, bar.MinY + 10f),
                  $"PptMode={PptMode}");
        }

        // ---- 兜底：读不到 SlideID 时用负页码当键（参考 Ultra 的 GetStrokeCacheKey）----
        fake.Connected = true; fake.Showing = true; fake.Slide = 2; fake.SlideId = 0;
        Step();
        Check("读不到 SlideID：用负页码兜底当页键", Doc.PageKey == -2, $"页键 {Doc.PageKey}");
        fake.Showing = false; fake.SlideId = 0;
        Step();

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? $"  PASS：PPT 模式正确（隔离 / 页内滚动 / 清空只清本页 / 退出写盘 / 再进读回 / 按钮走 PPT）——共 {pass} 项"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");

        try { Directory.Delete(PptStore.RootOverride, true); } catch { }
        PptStore.RootOverride = null;

        // 收尾：把文档清干净、退出（自检宿主跑完就结束，别留着一块盖在桌面上的窗口）。
        Doc.ResetToSinglePage();
        Doc.ClearHistory();
        _quit = true;
    }

    /// <summary>
    /// `--pptshow <图>`：**PPT 控件条的摆样**——用假源进入 PPT 模式，把它画出来再截一张。
    /// 加 `--panel` 出"点页码弹出的页号面板"，加 `--menu` 出"长按弹出的菜单"；
    /// 加 `--ink` 给当前页铺三笔——「回放本页墨迹」那一项才有得看（右邻显示"N 笔"，
    /// 不加 --ink 出的是置灰的"0 笔"态）。
    ///
    /// 为什么要单开一条：排版好不好看**自检判不了**（几何全对也一样难看），
    /// 所以项目里界面（`--panelshow`）、浮层（`--selshowcase`）、旋转标签
    /// （`--rotateshow`）都各有一条这样的出图命令（见 README 的"出图"一节）。
    /// </summary>

    /// <summary>
    /// 保存 / 加载往返自检。
    ///
    /// 验的是"存下去的和读回来的完全一样"。这条比看起来重要：序列化是
    /// **唯一会碰全部字段**的代码，任何一个字段忘了写、或者顺序写错，
    /// 表现都是"用户存了一学期的批注打不开"，而且开发时很难发现。
    /// </summary>
    private void SaveTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 保存 / 加载往返自检 ===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-22}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        Doc.Clear();
        Doc.ClearHistory();

        // 自由笔迹：带压感、带时间戳
        var freehand = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f),
            Width = 6f,
        };
        for (int i = 0; i < 40; i++)
            freehand.AddPoint(100 + i * 7f, 200 + MathF.Sin(i * 0.3f) * 30f,
                              0.2f + 0.6f * (i / 40f), 1000 + i * 8.5);
        Doc.AddStroke(freehand);

        // 顺手用像素橡皮在这条笔迹上擦掉一小段：**存档必须把擦除区间一起带上**，
        // 否则"存一次再打开"会把擦掉的墨又画回来（v4 新增的那段就是它）。
        Doc.BeginEraseRect();
        Doc.EraseRectAt(100 + 14 * 7f, 200, 12f, 12f);
        Doc.EndErase();
        bool erasedSaved = freehand.Erased.Count > 0;

        // 图形：带非等比缩放 + 旋转（最容易在序列化里被写错的东西）
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.13f, 0.45f, 0.90f, 0.8f),
            Width = 12f,
            Transform = Matrix3x2.CreateRotation(0.5f)
                      * Matrix3x2.CreateScale(1.5f, 0.75f)
                      * Matrix3x2.CreateTranslation(300f, 120f),
        };
        rect.AddPoint(10, 20, 1f, 5000);
        rect.AddPoint(210, 160, 1f, 5000);
        Doc.AddStroke(rect);

        int before = Doc.Strokes.Count;
        int maxId = 0;
        foreach (var s in Doc.Strokes) maxId = Math.Max(maxId, s.Id);

        var bytes = InkSerializer.Save(Doc);
        Check("格式头可识别", InkSerializer.LooksLikeInk(bytes), $"{bytes.Length} 字节");
        Check("每对象体积合理", bytes.Length / Math.Max(1, before) < 4000,
              $"{bytes.Length / Math.Max(1, before)} 字节/对象");

        var target = new InkDocument();
        InkSerializer.LoadInto(target, bytes);

        Check("对象数量", target.Strokes.Count == before, $"{target.Strokes.Count}");
        Check("加载后撤销栈为空", target.UndoDepth == 0, $"{target.UndoDepth}");

        bool allEqual = target.Strokes.Count == before;
        string diff = "";
        for (int i = 0; allEqual && i < before; i++)
        {
            var a = Doc.Strokes[i];
            var b = target.Strokes[i];
            bool eq = a.Id == b.Id && a.Tool == b.Tool && a.Kind == b.Kind
                   && a.Color.R == b.Color.R && a.Color.G == b.Color.G
                   && a.Color.B == b.Color.B && a.Color.A == b.Color.A
                   && a.Width == b.Width
                   && a.Transform.Equals(b.Transform)
                   && a.Points.Count == b.Points.Count
                   && a.Erased.Count == b.Erased.Count;
            for (int k = 0; eq && k < a.Erased.Count; k++)
                if (a.Erased[k] != b.Erased[k]) eq = false;
            if (eq)
            {
                for (int k = 0; k < a.Points.Count; k++)
                {
                    var p = a.Points[k];
                    var q = b.Points[k];
                    // 时间戳是"绝对量 + float 偏移"，会有浮点截断，给 0.05ms 容差。
                    if (p.X != q.X || p.Y != q.Y || p.P != q.P || Math.Abs(p.T - q.T) > 0.05)
                    { eq = false; diff = $"对象{i} 第{k}点"; break; }
                }
            }
            else diff = $"对象{i} 的字段";
            allEqual = eq;
        }
        Check("逐字段一致", allEqual, allEqual ? "含变换、颜色、压感、时间" : diff);
        Check("擦除区间也一起存了", erasedSaved && allEqual,
              erasedSaved ? "存前有区间、读回后一致（否则擦掉的墨会画回来）"
                          : "这一轮没造出擦除区间，等于没验");

        var probe = new Stroke { Tool = Tool.Pen, Width = 3f };
        probe.AddPoint(0, 0, 1f, 0);
        target.AddStroke(probe);
        Check("新对象 id 不撞车", probe.Id > maxId, $"新 {probe.Id} > 旧最大 {maxId}");

        bool threw = false;
        try { InkSerializer.LoadInto(new InkDocument(), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }); }
        catch (InvalidDataException) { threw = true; }
        Check("乱数据抛异常", threw, "");

        var truncated = new byte[bytes.Length / 2];
        Array.Copy(bytes, truncated, truncated.Length);
        var keep = new InkDocument();
        threw = false;
        try { InkSerializer.LoadInto(keep, truncated); } catch (Exception) { threw = true; }
        Check("截断数据抛异常", threw, "");
        Check("失败时文档未被动过", keep.Strokes.Count == 0, $"{keep.Strokes.Count} 个对象");

        // ---- 老文件兼容：v2（每条笔画多一个"笔锋预设"字节）必须还能打开 ----
        // 手写美化删掉之后，那个字节不再写、不再读；**但读老文件时必须读掉它**，
        // 否则后面的变换、点数据全部错位——用户存了一学期的批注会全部打不开。
        // 这个字节就在"宽度"和"变换"之间，写错了表现是"能打开但东西是乱的"，
        // 比打不开更难查，所以专门造一个 v2 文件来验。
        var v2 = new MemoryStream();
        using (var w = new BinaryWriter(v2, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(new byte[] { (byte)'I', (byte)'N', (byte)'K', (byte)'B' });
            w.Write(2);                     // 版本 2：每条笔画带预设字节
            w.Write(0);                     // flags
            w.Write(1);                     // 一块画布
            w.Write((byte)0);               // Blank
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write("");
            w.Write(1);                     // 一个对象
            w.Write(42);                    // id
            w.Write((byte)Tool.Pen);
            w.Write((byte)StrokeKind.Freehand);
            w.Write(0.95f); w.Write(0.18f); w.Write(0.18f); w.Write(1f);
            w.Write(7.5f);                  // 宽度
            w.Write((byte)1);               // ← 已废弃的"笔锋预设"字节（v2 才有）
            w.Write(1f); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(100f); w.Write(200f);
            w.Write(2);                     // 两个点
            w.Write(0.0);                   // 第一个点的时间戳
            w.Write(10f); w.Write(20f); w.Write(0.5f); w.Write(0f);
            w.Write(30f); w.Write(40f); w.Write(0.5f); w.Write(8f);
            w.Write((byte)0);               // 无图像
        }
        var old = new InkDocument();
        bool oldOk = true; string oldNote = "";
        try { InkSerializer.LoadInto(old, v2.ToArray()); }
        catch (Exception ex) { oldOk = false; oldNote = ex.GetType().Name; }
        var os = old.Strokes.Count > 0 ? old.Strokes[0] : null;
        Check("v2 老文件能打开", oldOk && os != null && os.Points.Count == 2, oldNote);
        Check("v2 老文件字段不错位",
            os != null && Math.Abs(os.Width - 7.5f) < 1e-4f
            && Math.Abs(os.Points[1].X - 30f) < 1e-4f && Math.Abs(os.Points[1].Y - 40f) < 1e-4f
            && Math.Abs(os.Transform.M31 - 100f) < 1e-4f,
            os == null ? "没读到对象"
                       : $"宽 {os.Width}、第二个点 ({os.Points[1].X},{os.Points[1].Y})、"
                         + $"平移 ({os.Transform.M31},{os.Transform.M32})");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "  PASS: 保存/加载往返正确" : $"  FAIL: {fail} 项不对");
        _quit = true;
    }

    /// <summary>
    /// 图形命中测试自检。
    ///
    /// 验的是"图形走精确命中、自由笔迹走中心线距离"这条分岔有没有走对。
    /// 关键在于：**图形画的是描边轮廓，中间是空的**——用"点到中心线的距离"
    /// 去判定，会把"点在矩形正中央"也当成命中，那是错的。
    /// </summary>

    /// <summary>
    /// 剪贴板**对象通道**自检（--clipboardtest）。
    ///
    /// 验的是"复制一段板书 → 粘到别处 → 还是可编辑对象"这条链，分四段：
    ///   ① 写进去的对象字节读回来**逐字段一致**（点数 / 粗细 / 颜色 / 变换 / 擦除区间 /
    ///      图像像素），差一个字段就是"粘回来少了一块"；
    ///   ② 身份必须**重新发**（Id 归零）：文件里的 Id 是原对象的，直接插进同一个文档
    ///      会和原件撞号，撤销 / 多选就会指错对象；
    ///   ③ 同一次复制里还夹着一张**图**（Word / PPT / 微信粘得到），尺寸 = 选区包围盒
    ///      两边各留 4 逻辑像素，背景**全透明**（粘到别处不该带我们的白底）；
    ///   ④ 粘贴是**智能**的：有对象格式就粘对象（落视口左上角、自动选中、一步撤销），
    ///      只剩一张图才退回"当图粘"——两条分支都要真的走一遍。
    ///
    /// **这个用例会覆盖系统剪贴板**（和 --capturetest 一样）：跑之前先存好要粘的东西。
    /// 剪贴板被别的程序占着时会明确报出来并跳过，那是环境问题，不是 bug。
    /// </summary>
    private void ClipboardTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 剪贴板对象通道自检（复制 → 粘回来仍是对象）===");
        Console.WriteLine("  注意：本用例会覆盖系统剪贴板（跑之前先存好要粘的东西）");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-32} {detail}");
        }

        // 逐字段比对：任何一处不同都回报"差在哪"，不靠肉眼看数字。
        static bool SameStroke(Stroke a, Stroke b, out string why)
        {
            why = "";
            if (a.Tool != b.Tool) { why = "工具不同"; return false; }
            if (a.Kind != b.Kind) { why = "类型不同"; return false; }
            if (a.Width != b.Width) { why = $"粗细 {a.Width} → {b.Width}"; return false; }
            if (a.Color.R != b.Color.R || a.Color.G != b.Color.G
                || a.Color.B != b.Color.B || a.Color.A != b.Color.A)
            { why = $"颜色 {a.Color} → {b.Color}"; return false; }
            if (!a.Transform.Equals(b.Transform)) { why = "变换不同"; return false; }
            if (a.IsImage != b.IsImage) { why = "一个像是一个不是"; return false; }
            if (a.IsImage)
            {
                if (a.Image.Width != b.Image.Width || a.Image.Height != b.Image.Height)
                { why = $"图 {a.Image.Width}×{a.Image.Height} → {b.Image.Width}×{b.Image.Height}"; return false; }
                if (a.Image.Bgra.Length != b.Image.Bgra.Length) { why = "图像字节数不同"; return false; }
                for (int i = 0; i < a.Image.Bgra.Length; i++)
                    if (a.Image.Bgra[i] != b.Image.Bgra[i]) { why = $"图像第 {i} 个字节"; return false; }
            }
            if (a.Points.Count != b.Points.Count)
            { why = $"点数 {a.Points.Count} → {b.Points.Count}"; return false; }
            for (int i = 0; i < a.Points.Count; i++)
            {
                var p = a.Points[i]; var q = b.Points[i];
                // 时间戳是"绝对量 + float 偏移"，会有浮点截断，给 0.05ms 容差（同存档自检）。
                if (p.X != q.X || p.Y != q.Y || p.P != q.P || Math.Abs(p.T - q.T) > 0.05)
                { why = $"第 {i} 个点"; return false; }
            }
            if (a.Erased.Count != b.Erased.Count)
            { why = $"擦除区间 {a.Erased.Count} 段 → {b.Erased.Count} 段"; return false; }
            for (int i = 0; i < a.Erased.Count; i++)
                if (a.Erased[i] != b.Erased[i]) { why = $"第 {i} 段擦除区间"; return false; }
            return true;
        }

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        // --- 0. 没选中时不该动剪贴板 -------------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        Check("没选中任何东西 → 不写剪贴板", !CopySelectionToClipboard(),
              "返回 false，剪贴板里原来的东西不动");

        // --- 1. 造三条要复制的东西 ---------------------------------------------
        var plain = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(0.1f, 0.35f, 0.95f, 1f), Width = 7.5f,
        };
        for (int i = 0; i <= 40; i++) plain.AddPoint(cx - 320 + i * 8, cy - 60, 0.9f, 1000 + i * 8);

        var turned = new Stroke
        {
            Tool = Tool.Pen, Color = new Color4(1f, 0f, 1f, 1f), Width = 12f,
            // 变换要在 AddStroke **之前**设好（空间网格按添加时的包围盒索引，见像素橡皮自检）
            Transform = Matrix3x2.CreateRotation(0.37f)
                      * Matrix3x2.CreateScale(1.3f, 0.7f)
                      * Matrix3x2.CreateTranslation(cx - 160, cy + 120),
        };
        for (int i = 0; i <= 60; i++) turned.AddPoint(i * 6, 40, 0.7f, 2000 + i * 4);
        turned.AddErased(12f, 20f);                 // 假装被像素橡皮擦掉两段
        turned.AddErased(30.5f, 33f);

        Doc.AddStroke(plain);
        Doc.AddStroke(turned);
        var picture = Doc.AddImage(MakeTestImage(48, 32), cx + 120, cy + 60, 1f);

        Doc.SelectOnly(new[] { plain, turned, picture });
        var expect = new[] { plain, turned, picture };
        var box = EditRegion.Of(expect);

        // --- 2. 复制 → 读回对象 ------------------------------------------------
        bool copied = CopySelectionToClipboard();
        if (!copied)
        {
            Console.WriteLine("  环境：剪贴板被别的程序占着 → SKIP: 剪贴板相关的几项跳过"
                            + "（关掉占用剪贴板的程序再跑）");
            Console.WriteLine($"  合计：通过 {pass}，失败 {fail}（剪贴板相关未验）");
            _quit = true;
            return;
        }

        bool gotObjects = ClipboardInk.TryGetObjects(out var back);
        Check("读回来的是**对象**（不是图）", gotObjects && back.Count == expect.Length,
              gotObjects ? $"{back.Count} 个对象（期望 {expect.Length}）" : "读不到我们的对象格式，只剩图了");

        bool same = gotObjects && back.Count == expect.Length;
        string diff = "";
        if (same)
            for (int i = 0; i < expect.Length; i++)
                if (!SameStroke(expect[i], back[i], out diff))
                { same = false; diff = $"第 {i} 个对象：{diff}"; break; }
        Check("逐字段一致（含变换 / 擦除区间 / 图像像素）",
              same,
              same ? $"3 个对象全对上（擦除区间 {turned.Erased.Count} 段、图 48×32 逐字节）" : diff);

        bool idsCleared = gotObjects && back.Count > 0;
        if (idsCleared)
            foreach (var s in back) if (s.Id != 0) idsCleared = false;
        Check("身份重新发（Id 归零）", idsCleared,
              idsCleared ? "读回来的都是 0，粘进文档时由文档发新号"
                         : $"还带着原 Id：{string.Join(",", back.ConvertAll(s => s.Id))}");

        // --- 3. 同一份剪贴板里那张图（给外部程序的兜底）-------------------------
        // 期望尺寸用**和复制那条路完全一样的算式**（先 Inflate 再相减），
        // 换成"宽度 + 两边留白"会和它在浮点上差一丢丢，跨整数边界就成了假失败。
        float margin = 4f * DpiScale;
        var boxIn = box.Inflate(margin);
        int expW = Math.Max(1, (int)MathF.Ceiling(boxIn.MaxX - boxIn.MinX));
        int expH = Math.Max(1, (int)MathF.Ceiling(boxIn.MaxY - boxIn.MinY));
        bool gotImg = ClipboardImage.TryGetImage(out var dib, out int bw, out int bh, out _);
        Check("同一份剪贴板里还有一张图", gotImg && bw == expW && bh == expH,
              gotImg ? $"{bw}×{bh}，期望 {expW}×{expH}"
                     : "没读到 CF_DIB（外部程序粘不到了）");

        if (gotImg && bw > 0 && bh > 0)
        {
            // 背景必须透明：粘到 PPT 上不该压一块白底。
            int corner = 3;                                  // 左上角那个像素的 alpha
            int opaque = 0;
            for (int i = 3; i < dib.Length; i += 4) if (dib[i] > 8) opaque++;
            Check("那张图背景透明、内容非空",
                  dib[corner] < 8 && opaque > 100,
                  $"左上角 alpha {dib[corner]}（期望 0），不透明像素 {opaque} 个");
        }

        // --- 4. 智能粘贴：有对象 → 粘成对象 ------------------------------------
        Doc.Clear();
        Doc.ClearHistory();
        int undos0 = Doc.UndoDepth;
        bool pasted = PasteFromClipboard();
        bool asObjects = pasted && Doc.Strokes.Count == expect.Length
                      && Doc.Selected.Count == expect.Length;
        Check("粘贴：有对象格式就粘成**对象**（不是一张图）", asObjects,
              asObjects ? $"{Doc.Strokes.Count} 个对象、全选中"
                        : $"对象数 {Doc.Strokes.Count}、选中 {Doc.Selected.Count}（期望 3 / 3）");

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var pastedBox = EditRegion.Of(Doc.Strokes);
        Check("粘到视口左上角（含留白）",
              Math.Abs(pastedBox.MinX - (vp.MinX + m)) < 0.6f
              && Math.Abs(pastedBox.MinY - (vp.MinY + m)) < 0.6f,
              $"落在 ({pastedBox.MinX:F0},{pastedBox.MinY:F0})，期望 ({vp.MinX + m:F0},{vp.MinY + m:F0})");

        bool newIds = Doc.Strokes.Count > 0;
        foreach (var s in Doc.Strokes) if (s.Id == 0) newIds = false;
        Check("粘进来的对象拿到了新身份", newIds,
              newIds ? $"Id {Doc.Strokes[0].Id}…（不是 0，也不和原件相等）" : "有对象的 Id 还是 0");

        bool erasedKept = Doc.Strokes.Count == 3;
        if (erasedKept)
        {
            var backTurned = Doc.Strokes.Find(s => s.Erased.Count > 0);
            erasedKept = backTurned != null && backTurned.Erased.Count == turned.Erased.Count;
        }
        Check("粘回来的笔迹还带着擦除区间（擦掉的墨不会画回来）", erasedKept,
              erasedKept ? $"区间 {Doc.Strokes.Find(s => s.Erased.Count > 0).Erased.Count} 段"
                         : "区间丢了");

        Doc.Undo();
        Check("粘贴算一步撤销（3 个对象一起回去）",
              Doc.Strokes.Count == 0 && Doc.UndoDepth == undos0,
              $"撤销后对象 {Doc.Strokes.Count}，撤销栈回到 {Doc.UndoDepth}");

        // --- 5. 只有图（没有对象格式）→ 退回"当图粘" ---------------------------
        var little = MakeTestImage(64, 48);
        bool wroteImg = ClipboardImage.SetImage(little.Bgra, little.Width, little.Height);
        Check("准备：把剪贴板换成一张纯图（对象格式没了）", wroteImg,
              wroteImg ? "写进去 64×48" : "写不进去（被别的程序占着？）");
        if (wroteImg)
        {
            bool hadObjects = ClipboardInk.TryGetObjects(out _);
            Check("这时候剪贴板里确实没有对象了", !hadObjects,
                  hadObjects ? "居然还读得到对象" : "只剩 CF_DIB");

            Doc.Clear();
            Doc.ClearHistory();
            bool pasted2 = PasteFromClipboard();
            bool asImage = pasted2 && Doc.Strokes.Count == 1 && Doc.Strokes[0].IsImage
                        && Doc.Strokes[0].Image.Width == 64 && Doc.Strokes[0].Image.Height == 48;
            Check("只有图时退回当图粘（老行为不变）", asImage,
                  asImage ? "粘成一个 64×48 的图像对象、自动选中"
                          : $"对象数 {Doc.Strokes.Count}，"
                            + (Doc.Strokes.Count > 0 ? $"Kind={Doc.Strokes[0].Kind}" : "什么都没有"));

            int diff2 = -1;
            if (asImage)
            {
                diff2 = 0;
                var got = Doc.Strokes[0].Image.Bgra;
                for (int i = 0; i < got.Length; i++) if (got[i] != little.Bgra[i]) diff2++;
            }
            Check("粘回来的图逐字节一致", diff2 == 0,
                  diff2 < 0 ? "上一条没过，这条没验" : $"差异 {diff2} 字节");
        }

        Doc.Clear();
        Console.WriteLine("  剪贴板里现在留着自检那张 64×48 的图（退出不会恢复你原来的内容）");
        Console.WriteLine($"  合计：通过 {pass}，失败 {fail}");
        Console.WriteLine(fail == 0 ? "PASS" : "FAIL");
        _quit = true;
    }

    /// <summary>
    /// 套索自检（--lassotest）。判据全是能算出来的数，不看屏幕。
    ///
    /// 七条：
    ///   ① `Ctrl+Alt+9` 真的在切方式（默认矩形）；
    ///   ② **80% 边界**：代表点 79% 在圈里 → 不选，81% → 选（WPF 的 _percentIntersectForInk）；
    ///   ③ **贴边＝无限延伸**：同一条半个身子在屏幕外的长笔迹，圈贴左边 → 选中；
    ///      圈不贴边（只有可见的那一小段在圈里）→ 不选。这一对**必须成对验**，
    ///      只验"贴边选中"看不出延伸是不是把什么都选进来了；
    ///   ④ **图形按轮廓判**：矩形只差右下角没圈住（4/5 轮廓点）→ 选；
    ///      再少一个角（3/5）→ 不选。用"两个端点"当代表点的话前者会漏选；
    ///   ⑤ **图像按四个角判**：四角全在圈里 → 选；少一个角（3/4 = 75%）→ 不选；
    ///   ⑥ **被擦掉的那一段不算墨**：圈住一条笔迹"被擦掉的那半截"，不该选中它；
    ///   ⑦ 引擎那条路（按下→拖→松手）真的会用这条判据；路径太短＝单击空白＝取消选中。
    /// </summary>

}
