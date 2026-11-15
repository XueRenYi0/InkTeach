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
    //  离屏渲染小工具
    //
    //  量"颜色会不会变深"这类事不能靠抓屏：抓屏在锁屏 / 远程会话 / 被别的窗口盖住时
    //  拍到的是桌面壁纸，两处都是壁纸 → "差 0" 看着像通过，其实什么都没验
    //  （2026-09-15 就被骗过一次）。离屏渲染什么时候跑都一样。
    // ------------------------------------------------------------------

    /// <summary>建一对离屏位图：一个当渲染目标，一个 CPU 可读（读回像素用）。</summary>
    private (ID2D1Bitmap1 target, ID2D1Bitmap1 cpu) MakeOffscreen(int w, int h)
    {
        var ctx = _windows[0].Context;
        var pf = new Vortice.DCommon.PixelFormat(
            Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        var target = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        var cpu = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        return (target, cpu);
    }

    /// <summary>把文档里的笔画画进离屏位图（画布坐标 → 位图坐标，按 -origin 平移）。</summary>
    private void RenderDocToBitmap(ID2D1Bitmap1 target, float originX, float originY)
    {
        var ctx = _windows[0].Context;
        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0.98f, 0.98f, 0.98f, 1f));      // 当作白板底色
        ctx.Transform = System.Numerics.Matrix3x2.CreateTranslation(-originX, -originY);
        foreach (var s in Doc.Strokes) _windows[0].DrawStrokeForTest(s);
        ctx.Transform = System.Numerics.Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        if (hr.Failure) Console.WriteLine("  离屏 EndDraw 失败: " + hr.Description);
        ctx.Target = null;
    }

    /// <summary>把离屏位图读回内存（BGRA）。</summary>
    private static byte[] ReadPixels(ID2D1Bitmap1 target, ID2D1Bitmap1 cpu, int w, int h)
    {
        cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
        var m = cpu.Map(MapOptions.Read);
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Marshal.Copy(IntPtr.Add(m.Bits, (int)(y * m.Pitch)), buf, y * w * 4, w * 4);
        cpu.Unmap();
        return buf;
    }

    private byte[] RenderAndRead(ID2D1Bitmap1 target, ID2D1Bitmap1 cpu, int w, int h,
                                 float originX, float originY)
    {
        RenderDocToBitmap(target, originX, originY);
        return ReadPixels(target, cpu, w, h);
    }

    /// <summary>取一小块的平均色（参数是 BGRA 缓冲、行宽像素数、左上角、边长）。</summary>
    private static (int r, int g, int b) AvgPatch(byte[] buf, int width, int px, int py, int size)
    {
        // 探针落在缓冲外面时**夹回来**，而不是抛异常：这是量颜色的工具，
        // 量歪了应该报一个可疑的数字让人去看，不该把整个自检进程打断。
        int height = buf.Length / (width * 4);
        px = Math.Clamp(px, 0, Math.Max(0, width - size));
        py = Math.Clamp(py, 0, Math.Max(0, height - size));
        long r = 0, g = 0, b = 0; int n = 0;
        for (int y = py; y < py + size; y++)
            for (int x = px; x < px + size; x++)
            {
                int i = y * width * 4 + x * 4;
                b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; n++;
            }
        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>两个颜色的差（三通道绝对值之和）。</summary>
    private static int ColorDiff((int r, int g, int b) a, (int r, int g, int b) b)
        => Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);

    /// <summary>
    /// 造一条"自己穿过自己"的荧光笔笔迹（`--selfcross`、`--eraseselfcross`、像素橡皮自检
    /// 共用同一条）。
    ///
    /// **真正的自交点**：两段中心线联立解出来是相对 (161.7, 20.9)，不是 (200, 0)——
    /// 后者离交点 40 像素，那儿只有一股墨，拿它做判据等于什么都没验（老用例就是这么错的）。
    /// </summary>
    private static Stroke MakeSelfCrossingHighlight(float cx, float cy, float width)
    {
        var s = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = InkPalette.HighlighterDefault, Width = width,
        };
        var raw = new List<Vector2>
        {
            new(cx, cy), new(cx + 120, cy), new(cx + 240, cy + 60),
            new(cx + 240, cy + 220), new(cx + 60, cy + 260),
            new(cx - 20, cy + 120), new(cx + 200, cy),          // ← 穿过第一段
            new(cx + 330, cy - 150),
        };
        for (int i = 0; i + 1 < raw.Count; i++)
            for (int k = 0; k < 20; k++)
            {
                float t = k / 20f;
                s.AddPoint(raw[i].X + (raw[i + 1].X - raw[i].X) * t,
                           raw[i].Y + (raw[i + 1].Y - raw[i].Y) * t, 0.5f, i * 20 + k);
            }
        s.AddPoint(raw[^1].X, raw[^1].Y, 0.5f, 999);
        return s;
    }

    /// <summary>
    /// 橡皮手测台（--eraserlab [日志前缀]）。
    ///
    /// **这是给人用的，不是自检**：铺一屏"可擦的东西"，打开橡皮遥测，然后把控制权交回
    /// 引擎的正常消息循环，用户拿鼠标自己擦。每条拖拽结束立刻落一行 CSV，退出时写汇总。
    ///
    /// 为什么要有它：手感、习惯、误擦这些事只有真人用真手才知道。自检能证明"切得对、
    /// 撤销一步、上屏像素对"，证明不了"这个尺寸顺手""这一擦我其实不想擦"。
    ///
    /// 样例里**故意放了三类对象**——笔迹（会被切开）、荧光笔（自交，看颜色会不会变深）、
    /// 图形（碰到轮廓整条删），一屏就能把三种行为都试到。
    /// </summary>
    /// <summary>
    /// 实验（--eraseimage）：**画面里有一张大图时，擦除一步要多久**。
    ///
    /// 查的是用户报的"擦图像不流畅"。分块缓存的重画是"整块清掉再画一遍块内的所有对象"，
    /// 而图像对象画的是**一张位图**——块里有图，这一块每次重画都要把图重新贴一遍。
    /// 所以图越大、橡皮跨过的块越多，一步擦除就越贵。这里做 A/B：同一串擦除动作，
    /// 一次在"有图 + 图上有墨"的版面上跑，一次在"没有图"的版面上跑。
    /// </summary>
    /// <summary>
    /// 探针（--imageerase）：**两种橡皮碰到图像对象时分别会怎样**。
    ///
    /// 用户报"擦图像不流畅"，先把"到底会发生什么"钉死：
    ///   · 像素橡皮（Ctrl+Alt+7）：按设计**不碰**图像（代码里 `if (s.IsImage) continue`）；
    ///   · 整笔橡皮（Ctrl+Alt+4）：走 `IsShape` 分支 → `HitTestExact` → 图像**整条删掉**。
    /// 这两个不一样，是历史遗留（图像是后来加的，整笔橡皮那条分支没跟着改）。
    /// </summary>
    private void ImageEraseProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 探针：两种橡皮碰到图像对象时会怎样 ===");

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;

        Doc.Clear();
        Doc.ClearHistory();
        var img = Doc.AddImage(MakeTestImage(400, 300), cx - 200, cy - 150, 1f);
        Console.WriteLine($"  先放一张 400×300 的图（对象数 {Doc.Strokes.Count}），"
                        + "橡皮落点打在图像正中");

        int hit = Doc.EraseRectAt(cx, cy, 40f, 40f);          // 像素橡皮的调用
        Console.WriteLine($"  像素橡皮擦一下：受影响 {hit} 条，图还在吗 "
                        + $"{(Doc.Strokes.Contains(img) ? "在" : "**没了**")}"
                        + $"（对象数 {Doc.Strokes.Count}）");

        int removed = Doc.EraseAt(cx, cy, 40f);               // 整笔橡皮的调用
        Console.WriteLine($"  整笔橡皮擦一下：删掉 {removed} 条，图还在吗 "
                        + $"{(Doc.Strokes.Contains(img) ? "在" : "**没了**")}"
                        + $"（对象数 {Doc.Strokes.Count}）");

        Console.WriteLine("image-erase probe done");
        _quit = true;
    }

    private void EraseImageProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 实验：画面里有大图时，擦除一步的代价 ===");

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        const int Steps = 25;

        double Run(bool withImage, out double patchMax, out int tiles, out int drawn, out double selfMs)
        {
            Doc.Clear();
            Doc.ClearHistory();
            // 按**截图**的实际尺寸来：一张全屏截图就是虚拟桌面那么大（2880×1800 = 20MB）。
            int iw = (int)_virtualW, ih = (int)_virtualH;
            if (withImage) Doc.AddImage(MakeTestImage(iw, ih), _virtualX, _virtualY, 1f);

            // 图上画几行笔迹（老师在图/PPT 上圈画），橡皮就擦这些
            var rnd = new Random(5);
            for (int k = 0; k < 6; k++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0.9f, 0.15f, 0.15f, 1f), Width = 8f * DpiScale };
                float y = cy - 400 + k * 130;
                for (int i = 0; i <= 60; i++)
                {
                    float t = i / 60f;
                    s.AddPoint(cx - 700 + t * 1400, y + MathF.Sin(t * 7f + k) * 12f, 0.9f, i);
                }
                Doc.AddStroke(s);
            }
            Doc.InvalidateAll();
            SettleFrames(500);

            double sum = 0, mx = 0, self = 0;
            int tl = 0, dr = 0;
            float bx = cx - 600, by = cy - 400;
            for (int i = 0; i < Steps; i++)
            {
                bx += 48; by += 32;
                var sw = Stopwatch.StartNew();
                Doc.EraseRectAt(bx, by, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
                sw.Stop();
                self += sw.Elapsed.TotalMilliseconds;
                NowMs = _clock.Elapsed.TotalMilliseconds;
                RenderAll();
                sum += _windows[0].LastPatchMs;
                if (_windows[0].LastPatchMs > mx) mx = _windows[0].LastPatchMs;
                tl += _windows[0].LastPatchCount;
                dr += _windows[0].LastDrawnStrokes;
            }
            patchMax = mx; tiles = tl; drawn = dr; selfMs = self;
            return sum / Steps;
        }

        double noImg = Run(false, out var mxA, out var tlA, out var drA, out var selfA);
        double withImg = Run(true, out var mxB, out var tlB, out var drB, out var selfB);

        Console.WriteLine($"  没有图：重画 {noImg,6:F2} ms/步（峰值 {mxA,5:F2}）"
                        + $"　{TL(tlA)} 块/步　{DR(drA)} 笔/步　擦除本身 {selfA / Steps,5:F2} ms/步");
        Console.WriteLine($"  有  图：重画 {withImg,6:F2} ms/步（峰值 {mxB,5:F2}）"
                        + $"　{TL(tlB)} 块/步　{DR(drB)} 笔/步　擦除本身 {selfB / Steps,5:F2} ms/步");
        Console.WriteLine($"  结论：带图时一步慢 {withImg - noImg:F2} ms（重画部分）");
        string TL(int t) => (t / (double)Steps).ToString("F1");
        string DR(int d) => (d / (double)Steps).ToString("F0");
        Console.WriteLine("erase-image probe done");
        _quit = true;
    }

    private void EraserLab(string prefix, bool keepSystemCursor = false)
    {
        string stamp = DateTime.Now.ToString("MMdd-HHmmss");
        string root = string.IsNullOrWhiteSpace(prefix) ? $"reports/橡皮-手测-{stamp}" : prefix;

        Doc.Clear();
        Doc.ClearHistory();

        float L = DpiScale;                       // 逻辑 → 物理
        float X(float logical) => _virtualX + logical * L;
        float Y(float logical) => _virtualY + logical * L;
        var ink = new Color4(0.08f, 0.09f, 0.12f, 1f);

        // ① 两行"板书"：每行三条长笔迹（模拟一行连着写下来）
        for (int row = 0; row < 2; row++)
        {
            float yy = Y(320f + row * 80f);
            for (int seg = 0; seg < 3; seg++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = 5f * L };
                float x0 = X(90f + seg * 420f), x1 = X(90f + seg * 420f + 395f);
                for (int i = 0; i <= 40; i++)
                {
                    float t = i / 40f;
                    s.AddPoint(x0 + (x1 - x0) * t,
                               yy + MathF.Sin(t * 9f + seg * 2f) * 7f * L, 0.9f, i);
                }
                Doc.AddStroke(s);
            }
        }

        // ② 三个"字"：三横一竖——"改一个字要重写一整行"那种场景（整笔擦会连带整行）
        for (int ch = 0; ch < 3; ch++)
        {
            float x0 = X(140f + ch * 220f), y0 = Y(470f);
            for (int k = 0; k < 3; k++)
            {
                var s = new Stroke { Tool = Tool.Pen, Color = ink, Width = 6f * L };
                s.AddPoint(x0, y0 + k * 26f * L, 1f, 0);
                s.AddPoint(x0 + 120f * L, y0 + k * 26f * L, 1f, 1);
                Doc.AddStroke(s);
            }
            var v = new Stroke { Tool = Tool.Pen, Color = ink, Width = 6f * L };
            v.AddPoint(x0 + 60f * L, y0 - 12f * L, 1f, 0);
            v.AddPoint(x0 + 60f * L, y0 + 64f * L, 1f, 1);
            Doc.AddStroke(v);
        }

        // ③ 一条自交的荧光笔：擦一刀，看交叠处会不会变深（这正是这版改动的核心收益）
        Doc.AddStroke(MakeSelfCrossingHighlight(X(880f), Y(360f), 26f * L));

        // ④ 图形：碰到轮廓整条删（橡皮从正中间划过不该删）
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.85f, 0.2f, 0.2f, 1f), Width = 5f * L,
        };
        rect.AddPoint(X(790f), Y(520f), 1f, 0);
        rect.AddPoint(X(1090f), Y(620f), 1f, 1);
        Doc.AddStroke(rect);

        var arrow = new Stroke
        {
            Tool = Tool.Arrow, Kind = StrokeKind.Arrow,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * L,
        };
        arrow.AddPoint(X(1160f), Y(600f), 1f, 0);
        arrow.AddPoint(X(1360f), Y(530f), 1f, 1);
        Doc.AddStroke(arrow);

        // ⑤ 一块"截图区"：模拟老师截了 PPT/图，然后在图上圈画。
        //    用它试两件事——**图上的墨能正常擦掉**，而**图本身不该被橡皮吃掉**
        //    （图像对象按设计不参与擦除，要删它用框选 + Delete）。
        Doc.AddImage(MakeTestImage(640, 220), X(90f), Y(660f), DpiScale);
        for (int k = 0; k < 3; k++)
        {
            var s = new Stroke { Tool = Tool.Pen, Color = new Color4(0.9f, 0.15f, 0.15f, 1f), Width = 4f * L };
            float y0 = Y(690f + k * 60f);
            for (int i = 0; i <= 30; i++)
            {
                float t = i / 30f;
                s.AddPoint(X(120f + t * 560f), y0 + MathF.Sin(t * 8f + k) * 6f * L, 0.9f, i);
            }
            Doc.AddStroke(s);
        }

        // 默认就站在像素橡皮上——要测的就是它。整笔橡皮 Ctrl+Alt+4，一键换回来。
        Tool = Tool.PixelEraser;
        ShowHud = true;                 // 面板上有一行"记录中 N 条"，用户看得见确实在记
        PassThrough = false;
        BoardOn = false;                // 透明批注：能看见底下的东西，和上课一样
        Doc.InvalidateAll();
        _dirty = true;

        EraserTelemetry = new EraserTelemetry(root + ".csv", root + ".txt", NowMs);
        if (keepSystemCursor)
        {
            EraserKeepsSystemCursor = true;
            Console.WriteLine("  已打开对照：系统箭头也会显示（和自绘方块对比跟手程度）");
        }

        Console.WriteLine();
        Console.WriteLine($"=== 橡皮手测台 ===  日志：{Path.GetFullPath(root)}.csv / .txt");
        Console.WriteLine("  已铺样例：两行板书、三个字、一条自交荧光笔、一个矩形一个箭头");
        Console.WriteLine("  左下角还有一块「截图区」：图上有三条批注，试试图上的墨能不能正常擦、图本身会不会被吃掉");
        Console.WriteLine("  当前工具：像素橡皮 Ctrl+Alt+7（整笔橡皮 4、笔 1、撤销 Z、"
                        + "换橡皮大小 6、退出 X）");
        SettleFrames(300);
    }

    /// <summary>
    /// 实验 / 回归（--eraseselfcross）：**切一刀之后，荧光笔自交处会不会变深**。
    ///
    /// 2026-09-15：像素橡皮改成"在一条笔迹上记擦除区间"之后，这个实验从"报数字"变成
    /// 了**回归判据**（差必须是 0）——它正是那一版改动存在的理由。同一件事在
    /// `--pixelerasetest` 里也会验一次。
    ///
    /// 为什么值得单独量：`--selfcross` 守着"一笔自己穿过自己时颜色不能变深"这条不变量，
    /// 而它是**一次 DrawGeometry** 的性质——D2D 把一条描边当成一个整体填充一次。
    /// 像素橡皮把一条笔迹**拆成两个对象**之后就变成两次填充，两段墨在空间上重叠的地方
    /// 会各画一次：半透明的荧光笔叠两次，交叠处就深了。
    ///
    /// 这不是"看着差不多"的事，能被像素证伪。所以这里在**离交点很远的地方**切一刀，
    /// 让互相穿过的那两股墨落在不同的碎片里，再量交点色和普通处色的差。
    /// 只报数字、不判红绿（它是在验证一个设计取舍，不是回归用例）。
    /// </summary>
    private void EraseSelfCrossProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 实验：切段之后自交处会不会变深（半透明荧光笔）===");

        Doc.Clear();
        Doc.ClearHistory();

        const int W = 800, H = 800;
        var (target, cpu) = MakeOffscreen(W, H);
        byte[] Render() => RenderAndRead(target, cpu, W, H, _virtualX, _virtualY);

        float cx = _virtualX + 500f, cy = _virtualY + 500f;
        Doc.AddStroke(MakeSelfCrossingHighlight(cx, cy, 40f));
        (int r, int g, int b) Avg(byte[] buf, int px, int py, int size)
            => AvgPatch(buf, W, px, py, size);
        // **真正的自交点在哪儿**：把两段中心线解出来才知道。
        //   A: P1(120,0) → P2(240,60)（第一段那条横线的斜尾巴）
        //   B: P5(-20,120) → P6(200,0)（绕回来那一股）
        //   A(u) = (120+120u, 60u)、B(t) = (-20+220t, 120-120t)
        //   60u = 120-120t → u = 2-2t；代进 x：-20+220t = 360-240t → t = 0.826, u = 0.348
        //   → 交点 ≈ (161.7, 20.9)
        //
        // 顺带一提：原来那条 --selfcross 用例探的是相对 (200, 0)，离这里 40 像素——
        // 那儿其实只有一股墨，两边颜色当然一样，那条判据是**空的**（见文档）。
        int crossX = (int)(cx + 161.7f - _virtualX) - 8;
        int crossY = (int)(cy + 20.9f - _virtualY) - 8;
        int plainX = (int)(cx + 60f - _virtualX) - 8;
        int plainY = (int)(cy - _virtualY) - 8;

        var buf0 = Render();
        var cross0 = Avg(buf0, crossX, crossY, 17);
        var plain0 = Avg(buf0, plainX, plainY, 17);
        int Diff((int r, int g, int b) a, (int r, int g, int b) b) => ColorDiff(a, b);
        int diff0 = Diff(cross0, plain0);
        Console.WriteLine($"  一刀没切（{Doc.Strokes.Count} 个对象）：交叠处 ({cross0.r},{cross0.g},{cross0.b})"
                        + $" vs 普通处 ({plain0.r},{plain0.g},{plain0.b})  差 {diff0}");

        // 在右侧那条竖边上切一刀（离交点 240 像素以上）：
        // 擦除区间记在**同一条笔迹**上，所以互相穿过的那两股墨还在同一次 DrawGeometry 里。
        int before = Doc.Strokes.Count;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx + 240, cy + 140, 20f, 30f);
        Doc.EndErase();

        var buf1 = Render();
        var cross1 = Avg(buf1, crossX, crossY, 17);
        var plain1 = Avg(buf1, plainX, plainY, 17);
        int diff1 = Diff(cross1, plain1);
        Console.WriteLine($"  切一刀后（{Doc.Strokes.Count} 个对象）：交叠处 ({cross1.r},{cross1.g},{cross1.b})"
                        + $" vs 普通处 ({plain1.r},{plain1.g},{plain1.b})  差 {diff1}");
        Console.WriteLine($"  对象数 {before} → {Doc.Strokes.Count}"
                        + $"；交叠处比切之前深了 {diff1 - diff0}（0 = 没变深）");

        target.Dispose();
        cpu.Dispose();
        Console.WriteLine("erase-selfcross probe done");
        _quit = true;
    }
    private bool _benchPressure;

    /// <summary>`--benchpts N`：基准里每笔几个点（0 = 老行为：随机 8~31）。
    ///
    /// 为什么要有它：老的"一万笔"每笔平均 19.5 个点，
    /// 而**用户真机写字是 139~211 个点**（raw 补点之后）—— 老基准比真实用法轻了近 9 倍。
    /// 要答"一万笔到底吃多少"，得按真实点密度量。</summary>
    private int _benchPts;

    /// <summary>`--benchspread N`：一万笔**分散到 N 屏**（0 = 老行为：全挤在一屏）。
    /// 真实板书是一节课往下写十几屏，每屏只有那么多字；全挤一屏是最极端的形状，
    /// 量出来的重铺代价会大得多，拿它当"一节课的消耗"会吓人。</summary>
    private int _benchSpread;

    /// <summary>`--selftest` 里 10k 基准实测的整层重铺耗时；给最后汇总用，别拿激光压测的最后一帧冒充。</summary>
    private double _lastBenchRebuildMs;

    private void Benchmark(int strokeCount)
    {
        Console.WriteLine();
        Console.WriteLine($"=== benchmark: {strokeCount} strokes"
                          + (_benchSpread > 0 ? $" spread over {_benchSpread} screens" : " on one screen")
                          + (_benchPressure ? " (with pressure)" : "")
                          + (_benchPts > 0 ? $" ({_benchPts} pts/stroke)" : "") + " ===");

        var gen = Stopwatch.StartNew();
        // 默认那一屏是**最极端的形状**（一万笔全挤在一屏）；真实板书是一节课分散十几屏。
        // `--benchspread N` 走分散版，量的才是"低配机上一节课"那个形状。
        if (_benchSpread > 0) GenerateStrokesSpread(strokeCount, _benchSpread);
        else GenerateStrokes(strokeCount);
        gen.Stop();

        var buildSw = Stopwatch.StartNew();
        foreach (var s in Doc.Strokes) s.BuildGeometry(Gfx.D2DFactory);
        buildSw.Stop();

        // Force a full content rebuild so the next frames re-rasterise everything.
        Doc.InvalidateAll();
        _dirty = true;

        RenderAll();
        var w0 = _windows[0];
        double rebuildMs = w0.LastRebuildMs;
        _lastBenchRebuildMs = rebuildMs;

        Console.WriteLine($"  generate      : {gen.Elapsed.TotalMilliseconds,8:F1} ms");
        Console.WriteLine($"  build geometry: {buildSw.Elapsed.TotalMilliseconds,8:F1} ms  ({buildSw.Elapsed.TotalMilliseconds / strokeCount:F3} ms/stroke)");
        Console.WriteLine($"  full rebuild  : {rebuildMs,8:F1} ms  (rasterise {strokeCount} strokes onto the content layer)");
        Console.WriteLine($"  points total  : {Doc.TotalPoints}");
        Console.WriteLine($"  working set   : {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1} MB");
        Console.WriteLine();
    }

}
