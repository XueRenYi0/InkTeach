// 文档导入（图片 / PDF）自检：`--doctest`。
//
// 这是新功能（2026-10-07 起）的落点。分组：
//   A 组（本文件当前）：页底层模型——布局 / 切片 / 视口窗口与回收 / 惰性生成 / 失败当没有。
//   B 组：图片导入（GDI+ 解码、EXIF、长图切片）——S4 接。
//   C 组：PDF（PDFium 渲染）——S5 接。
//
// 判据原则与其它自检一致：**不弹对话框**（`ExportDialogEnabled=false` 下走内部 API），
// 用"能数出来的东西"说话（页数 / 坐标 / 驻留页数 / 生成次数 / 字节数），不靠观感。

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
    /// <summary>造一张假页位图：纯色（BGRA）。判据不靠图案，所以只要能数、能上屏就够。</summary>
    private static byte[] FakeDocPageBgra(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[(long)w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i + 0] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return px;
    }

    private static DocPages.Spec DocSpec(int w, int h, string src, int idx = 0) => new()
    {
        Kind = 0,
        Source = src,
        SourceIndex = idx,
        OutW = w,
        OutH = h,
    };

    /// <summary>
    /// ===== 文档页底层自检（假源）=====
    ///
    /// 这一组**不碰真文件**：页层的正确性（布局、窗口、回收、惰性、失败）全部
    /// 用"假配方 + 假生成器"验——真文件那条路（图片解码 / PDF 渲染）在 B/C 组另测。
    /// 分开的理由：页层是"所有来源共用的骨架"，它错了，上面接着什么都白搭。
    /// </summary>
    private void DocumentTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 文档导入自检（--doctest）===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {name,-30}{(ok ? "PASS" : "FAIL")}  {detail}");
        }

        // 前面几组验"模型 + 同步生成"，判据要确定性：关掉后台线程（D 组专门验它）
        DocPageWorker = false;
        DocView.UseWorker = false;

        // 干净起点
        DocView.Close();
        DocView.Generator = sp => FakeDocPageBgra(sp.OutW, sp.OutH, 60, 180, 220);

        // ------------------------------------------------------------------
        Console.WriteLine("  -- A1 布局：居中 / 挨着铺 / 页缝 --");
        // ------------------------------------------------------------------
        float cx = 2000f, top = -1000f, gap = 48f;
        var specs = new List<DocPages.Spec>
        {
            DocSpec(600, 900, "A.png"),
            DocSpec(800, 400, "B.png"),
            DocSpec(600, 900, "C.png"),
        };
        DocView.Open(specs, "假文档", cx, top, gap);

        Check("页数 = 3", DocView.Count == 3, $"Count={DocView.Count}");
        var p0 = DocView.At(0); var p1 = DocView.At(1); var p2 = DocView.At(2);
        Check("第 1 页水平居中", Math.Abs((p0.Rect.MinX + p0.Rect.MaxX) * 0.5f - cx) < 0.01f,
              $"中心={(p0.Rect.MinX + p0.Rect.MaxX) * 0.5f:F1}（应 {cx}）");
        Check("第 1 页顶边 = 锚点", Math.Abs(p0.Rect.MinY - top) < 0.01f, $"{p0.Rect.MinY:F1}");
        Check("第 2 页顶 = 第 1 页底 + 缝", Math.Abs(p1.Rect.MinY - (p0.Rect.MaxY + gap)) < 0.01f,
              $"{p1.Rect.MinY:F1} vs {p0.Rect.MaxY + gap:F1}");
        Check("第 3 页接第 2 页", Math.Abs(p2.Rect.MinY - (p1.Rect.MaxY + gap)) < 0.01f,
              $"{p2.Rect.MinY:F1} vs {p1.Rect.MaxY + gap:F1}");

        var ext = DocView.Extent();
        Check("Extent 并入整叠页",
              Math.Abs(ext.MinX - 1600f) < 0.01f && Math.Abs(ext.MaxX - 2400f) < 0.01f &&
              Math.Abs(ext.MinY - top) < 0.01f && Math.Abs(ext.MaxY - p2.Rect.MaxY) < 0.01f,
              $"[{ext.MinX:F0},{ext.MinY:F0} .. {ext.MaxX:F0},{ext.MaxY:F0}]");

        Check("IndexAt：页内命中", DocView.IndexAt(top + 10f) == 0 && DocView.IndexAt(p1.Rect.MinY + 5f) == 1
              && DocView.IndexAt(p2.Rect.MinY + 5f) == 2,
              $"{DocView.IndexAt(top + 10f)}/{DocView.IndexAt(p1.Rect.MinY + 5f)}/{DocView.IndexAt(p2.Rect.MinY + 5f)}");
        Check("IndexAt：页缝里 = -1", DocView.IndexAt(p0.Rect.MaxY + gap * 0.5f) == -1,
              $"{DocView.IndexAt(p0.Rect.MaxY + gap * 0.5f)}");
        Check("TopOf(2) 正确", Math.Abs(DocView.TopOf(2) - p2.Rect.MinY) < 0.01f, $"{DocView.TopOf(2):F1}");
        Check("CurrentIndex 用视口中心", DocView.CurrentIndex(p2.Rect.MinY - 100f, p2.Rect.MinY + 100f) == 2,
              $"{DocView.CurrentIndex(p2.Rect.MinY - 100f, p2.Rect.MinY + 100f)}");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- A2 惰性生成：一帧最多一页（预算） --");
        // ------------------------------------------------------------------
        Check("打开 ≠ 生成（惰性）", DocView.GenerateCount == 0 && DocView.ResidentPages == 0,
              $"生成={DocView.GenerateCount} 驻留={DocView.ResidentPages}");

        float vTop = top, vBot = top + 1680f;      // 视口一屏高（假想 1680 物理）
        int made = DocView.SyncWindow(vTop, vBot, maxGenerate: 1, marginScreens: 1f);
        Check("第一拍：只生成 1 页", made == 1 && DocView.ResidentPages == 1 && DocView.GenerateCount == 1,
              $"made={made} 驻留={DocView.ResidentPages}");
        made = DocView.SyncWindow(vTop, vBot, 1, 1f);
        made += DocView.SyncWindow(vTop, vBot, 1, 1f);
        Check("再两拍：3 页全就绪", made == 2 && DocView.ResidentPages == 3 && DocView.GenerateCount == 3,
              $"made={made} 驻留={DocView.ResidentPages}");
        made = DocView.SyncWindow(vTop, vBot, 1, 1f);
        Check("全就绪后不再生成", made == 0 && DocView.GenerateCount == 3, $"made={made}");

        float pageBytes = 600f * 900f * 4f;
        Check("驻留字节 = 面积×4×页数",
              Math.Abs(DocView.ResidentBytes - (pageBytes * 2 + 800f * 400f * 4f)) < 1,
              $"{DocView.ResidentBytes / 1024 / 1024:F2} MB");

        Check("生成会把该页标脏给渲染层", DocView.DirtyRects.Count > 0 || DocView.Version > 0,
              $"DirtyRects={DocView.DirtyRects.Count} Version={DocView.Version}");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- A3 窗口回收：滚远的放、滚回来的重生 --");
        // ------------------------------------------------------------------
        float far = 100000f;
        DocView.SyncWindow(far, far + 1680f, 1, 1f);
        Check("滚远：3 页全回收", DocView.ResidentPages == 0 && DocView.ReleaseCount == 3,
              $"驻留={DocView.ResidentPages} 回收={DocView.ReleaseCount}");
        Check("回收后字节归零", DocView.ResidentBytes == 0, $"{DocView.ResidentBytes}");

        DocView.SyncWindow(vTop, vBot, 1, 1f);
        Check("滚回来能重生", DocView.ResidentPages == 1 && DocView.GenerateCount == 4,
              $"驻留={DocView.ResidentPages} 生成={DocView.GenerateCount}");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- A4 长文档：驻留不随页数涨 --");
        // ------------------------------------------------------------------
        var many = new List<DocPages.Spec>();
        for (int i = 0; i < 20; i++) many.Add(DocSpec(600, 900, "long.png", i));
        DocView.Open(many, "长文档", 2000f, 0f, 48f);      // 每页 stride = 948

        float stride = 900f + 48f;
        float pageTop = 10 * stride;                       // 视口落在第 11 页
        float vt = pageTop, vb = pageTop + 1680f;
        for (int i = 0; i < 20; i++) DocView.SyncWindow(vt, vb, 1, 1f);   // 20 拍，足够把窗口内全部生成
        Check("窗口内全部就绪", DocView.ResidentPages >= 5 && DocView.ResidentPages <= 8,
              $"驻留={DocView.ResidentPages} 页（窗口 ≈ 视口 ±1 屏）");
        Check("上方远了的不驻留", DocView.At(0).Image == null && DocView.At(3).Image == null,
              $"第 1/4 页 Image={(DocView.At(0).Image == null ? "放掉" : "还在")}");
        Check("下方还没到的不预生成", DocView.At(19).Image == null && DocView.At(19).Failed == false,
              $"第 20 页 Image={(DocView.At(19).Image == null ? "未生成 ✓" : "生成了 ✗")}");

        long cap = DocView.ResidentBytes;
        // 连续滚 20 页：每滚 2 页同步一拍；驻留页数**始终**不随位置增长
        int maxResident = 0;
        for (int step = 0; step < 10; step++)
        {
            float t = (step * 2) * stride;
            for (int k = 0; k < 8; k++) DocView.SyncWindow(t, t + 1680f, 1, 1f);
            maxResident = Math.Max(maxResident, DocView.ResidentPages);
        }
        Check("滚 20 页：驻留始终 ≤ 8", maxResident <= 8, $"峰值驻留={maxResident}");
        Check("滚 20 页：字节也不涨", DocView.ResidentBytes <= 8 * 600L * 900 * 4,
              $"峰值 {Math.Max(cap, DocView.ResidentBytes) / 1024 / 1024:F1} MB");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- A5 失败当没有：坏页不重试、不阻塞其它页 --");
        // ------------------------------------------------------------------
        int genCalls = 0;
        DocView.Open(new List<DocPages.Spec> { DocSpec(200, 200, "bad.png"), DocSpec(200, 200, "good.png") },
                     "失败测试", 0f, 0f, 10f);
        DocView.Generator = sp =>
        {
            genCalls++;
            return sp.Source.EndsWith("bad.png") ? null : FakeDocPageBgra(sp.OutW, sp.OutH, 0, 0, 0);
        };
        for (int i = 0; i < 5; i++) DocView.SyncWindow(0f, 1680f, 1, 1f);
        Check("坏页标记失败且只试一次", DocView.At(0).Failed && DocView.FailCount == 1,
              $"Failed={DocView.At(0).Failed} FailCount={DocView.FailCount}");
        Check("好页不受坏页影响", DocView.At(1).Image != null, $"Image={(DocView.At(1).Image != null)}");
        Check("失败的重试次数不再增长", genCalls == 2, $"genCalls={genCalls}（1 坏 + 1 好）");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- A7 上屏：页真的画出来 / 失败占位 / 滚走再回来 --");
        // ------------------------------------------------------------------
        // ⚠ 探针读的是**屏幕合成结果**。老版这里靠"开白板当不透明底"——
        //    "两张纸"模型下白板一开就盖住文档了 ✗；现在**文档自带纸**（白底），
        //    页外面不是桌面而是纸，白板关着照样能稳探（这是新模型顺手带来的好处）。
        if (BoardOn) SetBoardFromUi(false);
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        byte cb = 40, cg = 80, cr = 230;               // BGRA：橙红
        float halfW = _virtualW * 0.5f;                // 页水平居中（和 DocPages 的规矩一致）
        DocView.Close();
        DocView.Generator = sp =>
            sp.Source.EndsWith("bad.png") ? null
            : FakeDocPageBgra(sp.OutW, sp.OutH, cb, cg, cr);
        // 三页都在第一屏内/缘，采样点全部落在屏上：
        //   p1  y 100..800   → 采样 (1000,300)
        //   p2  y 848..1548  → 采样 (1000,900)
        //   bad y 1596..2196 → 屏上只露 1596..1680 → 采样 (1000,1620) 小方块
        DocView.Open(new List<DocPages.Spec>
        {
            DocSpec(600, 700, "p1.png"),
            DocSpec(600, 700, "p2.png"),
            DocSpec(600, 600, "bad.png", 1),
        }, "上屏测试", halfW, 100f, 48f);

        SettleFrames(300);      // 每帧最多生成 1 页：300ms 里有几十帧，3 页绰绰有余
        int onP1 = ScreenProbe.CountNear(1000, 300, 100, 100, cr, cg, cb, 10);
        Check("第 1 页画上屏", onP1 > 9000, $"{onP1}/10000 像素");
        int onP2 = ScreenProbe.CountNear(1000, 900, 100, 100, cr, cg, cb, 10);
        Check("第 2 页画上屏", onP2 > 9000, $"{onP2}/10000 像素");
        // 失败页（bad.png）→ 浅红占位 (252,230,230)
        int onBad = ScreenProbe.CountNear(1000, 1620, 40, 40, 252, 230, 230, 8);
        Check("失败页是浅红占位", onBad > 1400, $"{onBad}/1600 像素");

        // 滚下去：第 2 页到视口顶仍在（屏幕 y = 画布 y + ViewOffsetY）
        ViewOffsetY = -1000f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }
        SettleFrames(200);
        int onP2b = ScreenProbe.CountNear(1000, 100, 100, 100, cr, cg, cb, 10);
        Check("滚动后第 2 页跟着走", onP2b > 9000, $"{onP2b}/10000 像素");
        int onBad2 = ScreenProbe.CountNear(1000, 700, 60, 60, 252, 230, 230, 8);
        Check("滚动后失败占位也还在", onBad2 > 3000, $"{onBad2}/3600 像素");

        // 滚得远远的：页全回收、那一片屏幕上再也不该有页色
        ViewOffsetY = -20000f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }
        SettleFrames(200);
        Check("滚远：页全回收", DocView.ResidentPages == 0, $"驻留={DocView.ResidentPages}");
        int stale = ScreenProbe.CountNear(1000, 300, 100, 100, cr, cg, cb, 30);
        Check("滚远：屏幕上没有残留页", stale == 0, $"{stale} 像素（应为 0）");

        // 滚回来：页重生、重新上屏
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        SettleFrames(300);
        int back = ScreenProbe.CountNear(1000, 300, 100, 100, cr, cg, cb, 10);
        Check("滚回来：页重生上屏", back > 9000, $"{back}/10000 像素");

        // 收拾现场
        DocView.Close();
        BoardOn = false;
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
        SettleFrames(120);

        // ------------------------------------------------------------------
        Console.WriteLine("  -- A6 关闭：全放、回到空 --");
        // ------------------------------------------------------------------
        DocView.Close();
        Check("关闭后为空", !DocView.IsOpen && DocView.Count == 0 && DocView.ResidentPages == 0
              && DocView.ResidentBytes == 0, $"Count={DocView.Count} 驻留={DocView.ResidentPages}");
        Check("关闭后 Generator 留着但没用（零开销）", DocView.Generator != null, "ok");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- B1 图片：EXIF 方向映射（直接验映射表，不依赖 EXIF 往返） --");
        // ------------------------------------------------------------------
        // 造一张 100×50 的"左红右蓝"旗子：好认旋转方向。
        System.Drawing.Bitmap MakeFlag()
        {
            var b = new System.Drawing.Bitmap(100, 50, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(b);
            g.Clear(System.Drawing.Color.Blue);
            g.FillRectangle(System.Drawing.Brushes.Red, 0, 0, 50, 50);
            return b;
        }
        bool IsRed(System.Drawing.Bitmap b, int x, int y)
        {
            var c = b.GetPixel(x, y);
            return c.R > 200 && c.G < 60 && c.B < 60;
        }

        using (var b6 = MakeFlag())
        {
            DocImageSource.ApplyOrientation(b6, 6);      // 90° 顺时针：左红 → 上红
            Check("EXIF 6 = 90°CW（左红→上红）", b6.Width == 50 && b6.Height == 100 && IsRed(b6, 25, 5),
                  $"{b6.Width}x{b6.Height} 顶部={(IsRed(b6, 25, 5) ? "红" : "蓝")}");
        }
        using (var b3 = MakeFlag())
        {
            DocImageSource.ApplyOrientation(b3, 3);      // 180°：左红 → 右红
            Check("EXIF 3 = 180°（左红→右红）", IsRed(b3, 90, 25) && !IsRed(b3, 10, 25),
                  $"右={(IsRed(b3, 90, 25) ? "红" : "蓝")} 左={(IsRed(b3, 10, 25) ? "红" : "蓝")}");
        }
        using (var b8 = MakeFlag())
        {
            DocImageSource.ApplyOrientation(b8, 8);      // 270°CW = 90°CCW：左红 → 下红
            Check("EXIF 8 = 270°CW（左红→下红）", IsRed(b8, 25, 95),
                  $"底部={(IsRed(b8, 25, 95) ? "红" : "蓝")}");
        }
        using (var b1 = MakeFlag())
        {
            DocImageSource.ApplyOrientation(b1, 1);
            Check("EXIF 1 = 不动", IsRed(b1, 10, 25) && b1.Width == 100, $"{b1.Width}x{b1.Height}");
        }

        // ------------------------------------------------------------------
        Console.WriteLine("  -- B2 布局：fit width / 不超 2×放大 / 长图切片算术 --");
        // ------------------------------------------------------------------
        var s1 = DocImageSource.PlanSpecs("x.png", 800, 600, 1000f, 700f);
        Check("普通图：一页、撑宽", s1.Count == 1 && s1[0].OutW == 1000 && s1[0].OutH == 750,
              $"{s1.Count} 页 {s1[0].OutW}x{s1[0].OutH}（应 1000x750）");

        var s2 = DocImageSource.PlanSpecs("x.png", 200, 100, 1000f, 700f);
        Check("小图：最多放大 2×（不无限撑）", s2.Count == 1 && s2[0].OutW == 400 && s2[0].OutH == 200,
              $"{s2[0].OutW}x{s2[0].OutH}（应 400x200）");

        var s3 = DocImageSource.PlanSpecs("x.png", 300, 2800, 1000f, 700f);
        bool s3ok = s3.Count == 8;
        float expectY = 0f;
        foreach (var sp in s3)
        {
            if (Math.Abs(sp.SrcY - expectY) > 0.01f || Math.Abs(sp.SrcH - 350f) > 0.01f) s3ok = false;
            if (sp.OutH != 700 || sp.OutW != 600) s3ok = false;
            expectY += sp.SrcH;
        }
        Check("长图：8 片、首尾相接不重不漏", s3ok && Math.Abs(expectY - 2800f) < 0.01f,
              $"{s3.Count} 片，末尾到 {expectY:F0}（应 2800）");

        var s4 = DocImageSource.PlanSpecs("x.png", 300, 2900, 1000f, 700f);
        Check("长图：最后一片是短的", s4.Count == 9 && s4[^1].OutH == 200,
              $"{s4.Count} 片，末片 {s4[^1].OutH}px（应 200）");

        var s5 = DocImageSource.PlanSpecs("x.png", 300, 1401, 1000f, 700f);
        Check(">2 屏就切片（1401 → 并薄片后 4 片）", s5.Count == 4 && s5[^1].OutH == 702,
              $"{s5.Count} 片，末片 {s5[^1].OutH}px（应 702，2px 薄片已并进前一片）");

        // ------------------------------------------------------------------
        Console.WriteLine("  -- B3 真解码：颜色 / 白底合成 / 切片内容 --");
        // ------------------------------------------------------------------
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-doctest-images");
        Directory.CreateDirectory(dir);

        string pFlag = System.IO.Path.Combine(dir, "flag.png");
        using (var b = new System.Drawing.Bitmap(800, 600, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using var g = System.Drawing.Graphics.FromImage(b);
            g.Clear(System.Drawing.Color.Blue);
            g.FillRectangle(System.Drawing.Brushes.Red, 0, 0, 400, 600);
            b.Save(pFlag, System.Drawing.Imaging.ImageFormat.Png);
        }
        var f1 = DocImageSource.PlanSpecs(pFlag, 800, 600, 1000f, 700f)[0];
        var px1 = DocImageSource.RenderSpec(f1);
        bool pxl = px1 != null && px1[(375 * 1000 + 100) * 4 + 2] > 200 && px1[(375 * 1000 + 100) * 4 + 0] < 60;
        bool pxr = px1 != null && px1[(375 * 1000 + 900) * 4 + 0] > 200 && px1[(375 * 1000 + 900) * 4 + 2] < 60;
        Check("解码+缩放：左红右蓝", pxl && pxr, $"左={(pxl ? "红" : "?")} 右={(pxr ? "蓝" : "?")}");

        string pClear = System.IO.Path.Combine(dir, "clear.png");
        using (var b = new System.Drawing.Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using var g = System.Drawing.Graphics.FromImage(b);
            g.Clear(System.Drawing.Color.Transparent);
            b.Save(pClear, System.Drawing.Imaging.ImageFormat.Png);
        }
        var f2 = DocImageSource.PlanSpecs(pClear, 400, 300, 1000f, 700f)[0];
        var px2 = DocImageSource.RenderSpec(f2);
        bool white = px2 != null;
        if (white)
            for (int c = 0; c < 3; c++)
                if (px2[((150 * f2.OutW + 200) * 4) + c] < 240) white = false;
        Check("透明 PNG：合成到白底（不透桌面）", white, white ? "中心 RGB=(255,255,255)" : "中心不是白的");

        string pLong = System.IO.Path.Combine(dir, "long.png");
        var band = new[]
        {
            System.Drawing.Color.FromArgb(255, 0, 0), System.Drawing.Color.FromArgb(0, 255, 0),
            System.Drawing.Color.FromArgb(0, 0, 255), System.Drawing.Color.FromArgb(255, 255, 0),
            System.Drawing.Color.FromArgb(255, 0, 255), System.Drawing.Color.FromArgb(0, 255, 255),
            System.Drawing.Color.FromArgb(128, 128, 128), System.Drawing.Color.FromArgb(255, 128, 0),
        };
        using (var b = new System.Drawing.Bitmap(300, 2800, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using var g = System.Drawing.Graphics.FromImage(b);
            for (int i = 0; i < 8; i++)
                using (var br = new System.Drawing.SolidBrush(band[i]))
                    g.FillRectangle(br, 0, i * 350, 300, 350);
            b.Save(pLong, System.Drawing.Imaging.ImageFormat.Png);
        }
        var f3 = DocImageSource.PlanSpecs(pLong, 300, 2800, 1000f, 700f);
        var px3 = DocImageSource.RenderSpec(f3[3]);      // 第 4 片 = 第 4 条色带（黄）
        bool bandOK = px3 != null && px3[(350 * f3[3].OutW + 300) * 4 + 1] > 200
                   && px3[(350 * f3[3].OutW + 300) * 4 + 2] > 200
                   && px3[(350 * f3[3].OutW + 300) * 4 + 0] < 60;
        Check("切片内容对位（第 4 片 = 黄带）", bandOK, bandOK ? "BGR=(0,255,255)" : "颜色不对");

        // EXIF 文件往返：GDI+ 不一定保留（保留不了就只标注，不判红）
        string pExif = System.IO.Path.Combine(dir, "exif.jpg");
        try
        {
            using var b = new System.Drawing.Bitmap(300, 200, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(b))
            {
                g.Clear(System.Drawing.Color.Blue);
                g.FillRectangle(System.Drawing.Brushes.Red, 0, 0, 150, 200);
            }
            var item = (System.Drawing.Imaging.PropertyItem)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(System.Drawing.Imaging.PropertyItem));
            item.Id = 0x0112; item.Type = 3; item.Len = 2; item.Value = new byte[] { 6, 0 };
            b.SetPropertyItem(item);
            b.Save(pExif, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
        catch { }
        if (System.IO.File.Exists(pExif) && DocImageSource.TryReadInfo(pExif, out int ew, out int eh))
        {
            if (ew == 200 && eh == 300)
            {
                var f4 = DocImageSource.PlanSpecs(pExif, ew, eh, 1000f, 700f)[0];
                var px4 = DocImageSource.RenderSpec(f4);
                bool rotated = px4 != null && px4[(10 * f4.OutW + f4.OutW / 2) * 4 + 2] > 180;
                Check("EXIF 文件：尺寸交换 + 渲染已转正", rotated,
                      rotated ? "顶部=红（已转正）" : "顶部不是红的");
            }
            else
            {
                Console.WriteLine($"      （GDI+ 没保留 EXIF：读到 {ew}x{eh}；这条留待真机手机照片验）");
            }
        }

        // ------------------------------------------------------------------
        Console.WriteLine("  -- B4 端到端：OpenDocuments → 生成 → 关闭回收 --");
        // ------------------------------------------------------------------
        int imgBefore = ImageData.LiveImages;
        long bytesBefore = ImageData.LiveBytes;
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        var errA = OpenDocuments(new[] { pLong });
        int expectPages = DocImageSource.PlanSpecs(pLong, 300, 2800, _virtualW, _virtualH).Count;
        Check("打开长图不报错", errA == null, errA ?? "ok");
        Check($"页数 = 按本机屏切片的 {expectPages} 片", DocView.Count == expectPages, $"Count={DocView.Count}");
        Check("标题 = 文件名", DocView.Title == "long.png", DocView.Title);
        Check("状态行报页数", InkStatus.Contains($"{expectPages} 页"), InkStatus);

        SettleFrames(300);
        Check("推进视口后页位图真的生成了", DocView.ResidentPages >= 1, $"驻留={DocView.ResidentPages}");
        Check("页位图记进了 ImageData 账", ImageData.LiveImages > imgBefore,
              $"LiveImages {imgBefore} → {ImageData.LiveImages}");

        var errB = OpenDocuments(new[] { System.IO.Path.Combine(dir, "no-such-file.png"), pFlag });
        Check("坏文件跳过、好文件照开", errB == null && DocView.Count == 1, errB ?? $"Count={DocView.Count}");

        // 文件不锁：打开状态下还能对那张图做**独占打开**（GDI+ "new Bitmap(路径) 锁文件"那条坑的回归判据）
        bool fileFree = true;
        try
        {
            using var exclusive = new System.IO.FileStream(pFlag, System.IO.FileMode.Open,
                System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
        }
        catch { fileFree = false; }
        Check("打开文档不锁住图片文件", fileFree, fileFree ? "文件还归老师（可改可删）" : "被锁住了 ✗");

        var errC = OpenDocuments(new[] { System.IO.Path.Combine(dir, "no-such-file.png") });
        Check("全读不了 ⇒ 一句提示、状态不动", errC != null && DocView.Count == 1, errC ?? "(没报错)");

        var errD = OpenDocuments(new[] { "a.pdf", pFlag });
        Check("图片和 PDF 混选 ⇒ 提示重选", errD != null, errD ?? "(没报错)");

        CloseDocument();
        Check("关闭后页位图全放（回基线）",
              DocView.Count == 0 && ImageData.LiveImages == imgBefore && ImageData.LiveBytes == bytesBefore,
              $"LiveImages {imgBefore} ⇐ {ImageData.LiveImages}");

        try { Directory.Delete(dir, true); } catch { }

        // ------------------------------------------------------------------
        Console.WriteLine("  -- C1 PDFium：加载 / 打开 / 页尺寸 / 渲染 --");
        // ------------------------------------------------------------------
        string pdfDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-doctest-pdf");
        Directory.CreateDirectory(pdfDir);
        string pPdf = System.IO.Path.Combine(pdfDir, "三色三页.pdf");
        WriteTestPdf(pPdf);

        bool pdfOk = Pdfium.EnsureLoaded(out string pdfErr);
        Check("pdfium.dll 能加载", pdfOk,
              pdfOk ? "ok" : pdfErr + "（跑 tools\\fetch-pdfium.ps1 取回）");
        if (pdfOk)
        {
            bool opened = Pdfium.Open(pPdf, out var pdfDoc, out string openErr);
            Check("打开 3 页 PDF", opened && pdfDoc.PageCount == 3,
                  opened ? $"PageCount={pdfDoc.PageCount}" : openErr);
            if (opened)
            {
                var (pw0, ph0) = pdfDoc.Size(0);
                Check("页尺寸 = A4（595×842pt）",
                      Math.Abs(pw0 - 595f) < 1f && Math.Abs(ph0 - 842f) < 1f,
                      $"{pw0:F0}x{ph0:F0}pt");

                int rw = (int)MathF.Round(_virtualW);
                int rh = (int)MathF.Round(_virtualW * ph0 / pw0);
                var swR = Stopwatch.StartNew();
                var pxR = pdfDoc.RenderPage(0, rw, rh);
                swR.Stop();
                bool red = pxR != null;
                if (red)
                {
                    int o = (rh / 2) * rw * 4 + (rw / 2) * 4;
                    red = pxR[o + 2] > 180 && pxR[o + 0] < 80 && pxR[o + 1] < 80;
                }
                Check("渲染第 1 页：整页红", red,
                      red ? $"{rw}x{rh}，{swR.Elapsed.TotalMilliseconds:F0} ms" : "中心不是红的");

                var pxG = pdfDoc.RenderPage(1, rw, rh);
                bool green = pxG != null;
                if (green) green = pxG[((rh / 2) * rw + (rw / 2)) * 4 + 1] > 180 && pxG[((rh / 2) * rw + (rw / 2)) * 4 + 2] < 80;
                Check("渲染第 2 页：整页绿", green, green ? "ok" : "中心不是绿的");
                pdfDoc.Dispose();
            }
        }

        // ------------------------------------------------------------------
        Console.WriteLine("  -- C2 PDF 端到端：打开 → fit width 铺页 → 上屏 → 关闭 --");
        // ------------------------------------------------------------------
        if (pdfOk)
        {
            int imgBefore2 = ImageData.LiveImages;
            Doc.Clear();
            Doc.ClearHistory();
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

            var errPdf = OpenDocuments(new[] { pPdf });
            Check("打开 PDF：不报错、3 页", errPdf == null && DocView.Count == 3,
                  errPdf ?? $"Count={DocView.Count}");
            Check("标题 = 文件名", DocView.Title == "三色三页.pdf", DocView.Title);
            int expectW = (int)MathF.Round(_virtualW);
            Check("每页宽度 = 屏宽（fit width）", DocView.At(0).Spec.OutW == expectW,
                  $"{DocView.At(0).Spec.OutW}（应 {expectW}）");

            SettleFrames(400);
            Check("页位图生成并上屏（账在涨）",
                  DocView.ResidentPages >= 1 && ImageData.LiveImages > imgBefore2,
                  $"驻留={DocView.ResidentPages} LiveImages {imgBefore2}→{ImageData.LiveImages}");

            var rb = DocView.At(0).Rect;
            int hitRed = ScreenProbe.CountNear((int)rb.MinX + 200, (int)rb.MinY + 200, 50, 50, 255, 0, 0, 24);
            Check("屏幕上第 1 页是红的", hitRed > 2000, $"{hitRed}/2500 像素");

            CloseDocument();
            Check("关闭后回到基线、PDF 已释放",
                  DocView.Count == 0 && ImageData.LiveImages == imgBefore2,
                  $"LiveImages {imgBefore2} ⇐ {ImageData.LiveImages}");

            var errPdf2 = OpenDocuments(new[] { pPdf });
            Check("关闭后能重新打开（显式释放干净）", errPdf2 == null && DocView.Count == 3,
                  errPdf2 ?? "ok");
            CloseDocument();

            string pBad = System.IO.Path.Combine(pdfDir, "坏文件.pdf");
            System.IO.File.WriteAllText(pBad, "this is not a pdf at all");
            var errBad = OpenDocuments(new[] { pBad });
            Check("非 PDF 文件：一句提示、状态不动", errBad != null && DocView.Count == 0,
                  errBad ?? "(没报错)");
        }

        try { Directory.Delete(pdfDir, true); } catch { }

        // ------------------------------------------------------------------
        Console.WriteLine("  -- C3 页码条 + 文档批注独立页空间 + 自动保存/读回 --");
        // ------------------------------------------------------------------
        if (pdfOk)
        {
            string pdfDir2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-doctest-pdf2");
            Directory.CreateDirectory(pdfDir2);
            string pPdf2 = System.IO.Path.Combine(pdfDir2, "状态测试.pdf");
            WriteTestPdf(pPdf2);

            DocView.Close();
            if (Doc.PageKey != 0) Doc.SwitchPage(0);
            Doc.Clear();
            Doc.ClearHistory();
            if (BoardOn) SetBoardFromUi(false);          // 干净起点：板关（好验"开文档不碰白板"）
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

            var errOpen = OpenDocuments(new[] { pPdf2 });
            int docSlot = DocStore.SlotOf(DocStore.KeyOf(new[] { pPdf2 }));
            Check("打开后：条活跃 + 进了文档页空间",
                  errOpen == null && PageBarActive && Doc.PageKey == docSlot,
                  errOpen ?? $"slot={Doc.PageKey}（应 {docSlot}）");

            Check("打开后：白板**没被碰**（纸由文档自带，与板无关）", !BoardOn, $"BoardOn={BoardOn}");

            Check("条上页码 1/3", BarPageNow == 1 && BarTotal == 3, $"{BarPageNow}/{BarTotal}");

            BarNext();
            SettleFrames(260);
            Check("下一页：视口到第 2 页顶 + 页码跟着变",
                  Math.Abs(ViewOffsetY + DocView.TopOf(1)) < 2f && BarPageNow == 2,
                  $"camY={ViewOffsetY:F0}（应 {-DocView.TopOf(1):F0}）页码={BarPageNow}");

            BarGoto(3);
            SettleFrames(260);
            Check("跳页（面板同一入口）：到第 3 页", BarPageNow == 3, $"{BarPageNow}");
            BarGoto(1);
            SettleFrames(260);

            // ★ 文档批注独立：写一笔进当前槽（= 文档槽），桌面板书一个字不动
            var mark = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                                    Color = new Color4(1f, 0f, 0f, 1f), Width = 6f };
            mark.AddPoint(100f, 100f, 1f, 0);
            mark.AddPoint(300f, 200f, 1f, 0);
            Doc.AddStroke(mark);
            Check("文档槽里有了 1 笔（桌面槽不受影响）", Doc.Strokes.Count == 1, $"{Doc.Strokes.Count}");

            // 动作同菜单第 2 项（自动保存开关默认开）+ 节流走一拍
            SetAutoSaveIntervalForTest(1);
            StepDocAutoSave();
            Check("自动保存：盘上有了这份文档的批注", DocStore.Load(DocStoreKey) != null, DocStoreKey);

            CloseDocument();
            SetAutoSaveIntervalForTest(15000);
            Check("关闭：回桌面页空间 + 条收走", !PageBarActive && Doc.PageKey == 0, $"slot={Doc.PageKey}");
            Check("关闭：桌面板书没被污染", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 个对象");

            var errRe = OpenDocuments(new[] { pPdf2 });
            Check("重开同一份：批注读回、槽还原",
                  errRe == null && Doc.Strokes.Count == 1 && Doc.PageKey == docSlot,
                  errRe ?? $"{Doc.Strokes.Count} 个对象 slot={Doc.PageKey}");

            // ⭐ "两张纸"模型（2026-10-09 用户定）：**文档自带一张纸**（白底，独立于白板）——
            //   页缝/页边不露桌面；白板是**另一张盖在上面的纸**（开板=页让位、板色盖满）。
            var pg1 = DocView.At(0); var pg2 = DocView.At(1);
            float gapMid = (pg1.Rect.MaxY + pg2.Rect.MinY) * 0.5f;   // 第 1/2 页之间那条缝的中心（画布 y）
            ViewOffsetY = -gapMid + 840f;                            // 把它对到屏幕 y≈840
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }
            SettleFrames(350);
            int sx = (int)pg1.Rect.MinX + 600;                       // 缝横贯整幅页宽，x 取页内任意
            int sy = 840 - 8;                                        // 采样块 16 高，落在缝（48 高）里
            int pr = (int)MathF.Round(DocPaperColor.R * 255);
            int pgc = (int)MathF.Round(DocPaperColor.G * 255);
            int pbc = (int)MathF.Round(DocPaperColor.B * 255);
            int gapWhite = ScreenProbe.CountNear(sx, sy, 100, 16, pr, pgc, pbc, 3);
            Check($"文档开着、白板关着：页缝里是**文档的纸** rgb({pr},{pgc},{pbc})", gapWhite > 1580, $"{gapWhite}/1600");

            // 对照：把**板色**临时换成绿色——纸**不受影响**（纸是文档的、不是板）。
            // 这条把"两张纸互不干涉"钉死：旧版纸底借的是板开关，换个板色纸就跟着变 ✗。
            var savedBoardColor = BoardColor;
            SetBoardColorFromUi(new Color4(0f, 0.8f, 0f, 1f));
            SettleFrames(300);
            int gapWhite2 = ScreenProbe.CountNear(sx, sy, 100, 16, pr, pgc, pbc, 3);
            int gapGreen2 = ScreenProbe.CountNear(sx, sy, 100, 16, 0, 204, 0, 3);
            Check("换绿板色：纸**不变**（纸与板互不干涉）", gapWhite2 > 1580 && gapGreen2 < 40,
                  $"纸白 {gapWhite2}/1600、绿 {gapGreen2}（应≈0）");

            // 白板铺上来（"上面那张纸"）：页让位、条收起、板色盖满；关板回到文档。
            SetBoardFromUi(true);
            SettleFrames(420);
            Check("开白板（文档开着）：盖住文档（页位图让位 + 条收起）",
                  BoardOn && DocView.ResidentPages == 0 && !PageBarVisible,
                  $"板开={BoardOn} 驻留={DocView.ResidentPages} 条可见={PageBarVisible}");
            int pagePx = (int)pg1.Rect.MinX + 40;
            int pagePy = 840 - 470;                                  // 屏幕上页 0 的可见段里
            int pageRedBoardOn = ScreenProbe.CountNear(pagePx, pagePy, 80, 80, 255, 0, 0, 40);
            int boardGreenOn = ScreenProbe.CountNear(pagePx, pagePy, 80, 80, 0, 204, 0, 6);
            Check("开白板：页区整块是板色（不漏页面、不漏桌面；当前板色绿）",
                  pageRedBoardOn < 60 && boardGreenOn > 5000,
                  $"页红残留 {pageRedBoardOn}、板绿 {boardGreenOn}/6400");
            SetBoardFromUi(false);
            SettleFrames(420);
            int pageRedBack2 = ScreenProbe.CountNear(pagePx, pagePy, 80, 80, 255, 0, 0, 40);
            Check("关白板：文档回来（页/条回来 + 页重新上屏）",
                  !BoardOn && DocView.ResidentPages >= 1 && PageBarVisible && pageRedBack2 > 5000,
                  $"板开={BoardOn} 驻留={DocView.ResidentPages} 条可见={PageBarVisible} 页红={pageRedBack2}");
            SetBoardColorFromUi(savedBoardColor);
            SettleFrames(300);
            ViewOffsetY = 0f;
            foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
            SettleFrames(200);

            ClearDocMarks();
            Check("清空文档墨迹：内存与盘都空了",
                  Doc.Strokes.Count == 0 && DocStore.Load(DocStoreKey) == null, "ok");

            // ⭐ 穿透 = 全让开（S2，用户 2026-10-07 定）：页收、条收；退出自动回来
            var barRect = PptBarRect();
            float bcx = (barRect.MinX + barRect.MaxX) * 0.5f;
            float bcy = (barRect.MinY + barRect.MaxY) * 0.5f;
            int rb0 = DocView.At(0) != null ? (int)DocView.At(0).Rect.MinX : 0;
            int ry0 = DocView.At(0) != null ? (int)DocView.At(0).Rect.MinY : 0;

            SetPassThroughFromUi(true);
            SettleFrames(260);
            Check("穿透：页位图全放 + 条可见性关闭",
                  DocView.ResidentPages == 0 && !PageBarVisible && !PptBarContains(bcx, bcy),
                  $"驻留={DocView.ResidentPages} 条可见={PageBarVisible} 条命中={PptBarContains(bcx, bcy)}");
            int redPass = ScreenProbe.CountNear(rb0 + 300, ry0 + 300, 80, 80, 255, 0, 0, 40);
            Check("穿透：屏幕上页也收走（让开）", redPass < 50, $"{redPass} 红像素（应≈0）");

            SetPassThroughFromUi(false);
            SettleFrames(400);
            Check("退出穿透：页回来 + 条回来",
                  DocView.ResidentPages >= 1 && PageBarVisible,
                  $"驻留={DocView.ResidentPages} 条可见={PageBarVisible}");
            int redBack = ScreenProbe.CountNear(rb0 + 300, ry0 + 300, 80, 80, 255, 0, 0, 40);
            Check("退出穿透：页重新上屏", redBack > 4000, $"{redBack}/6400 像素");

            // ⭐ 白板盖住文档时：**墨与截图都留在白板上**（用户用法："截图 PDF 里的题 → 摆到白板上"，
            //   那些截图/批注就是要摆在白板上的内容，所以不隐）。
            {
                var coverMark = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                                             Color = new Color4(0f, 0f, 0f, 1f), Width = 10f };
                coverMark.AddPoint(rb0 + 240f, ry0 + 240f, 1f, 0);
                coverMark.AddPoint(rb0 + 360f, ry0 + 320f, 1f, 0);
                Doc.AddStroke(coverMark);
                SettleFrames(300);
                int inkBefore = ScreenProbe.CountNear(rb0 + 270, ry0 + 260, 100, 80, 0, 0, 0, 40);

                SetBoardFromUi(true);
                SettleFrames(420);
                int inkOnBoard = ScreenProbe.CountNear(rb0 + 270, ry0 + 260, 100, 80, 0, 0, 0, 40);
                Check("白板盖住文档：墨/截图留在白板上（不隐）",
                      BoardOn && inkBefore > 100 && inkOnBoard > 100,
                      $"板开={BoardOn}，黑像素（盖前/盖后）={inkBefore}/{inkOnBoard}");
                SetBoardFromUi(false);
                SettleFrames(420);
                int inkBack = ScreenProbe.CountNear(rb0 + 270, ry0 + 260, 100, 80, 0, 0, 0, 40);
                Check("关白板：墨还在（回到文档上面）", !BoardOn && inkBack > 100, $"黑像素={inkBack}");
            }

            CloseDocument();
            Check("关文档：白板状态不受影响（文档只收自己那张纸）", !BoardOn, $"BoardOn={BoardOn}");
            if (Doc.PageKey != 0) Doc.SwitchPage(0);
            Doc.Clear();
            Doc.ClearHistory();
            try { Directory.Delete(pdfDir2, true); } catch { }
        }

        // ------------------------------------------------------------------
        Console.WriteLine("  -- D 后台渲染线程：一拍不卡 / 陆续到货 / 在途关档不崩 --");
        // ------------------------------------------------------------------
        // 真机数据（122MB 扫描型 PDF）：一页要 270~900ms。这条线程就是为那种页存在的。
        int dBaseImages = ImageData.LiveImages;
        DocView.UseWorker = true;
        DocView.Generator = sp =>
        {
            Thread.Sleep(60);              // 装成"扫描型 PDF 一页 60ms"
            return FakeDocPageBgra(sp.OutW, sp.OutH, 30, 30, 30);
        };
        var dSpecs = new List<DocPages.Spec>();
        for (int i = 0; i < 6; i++) dSpecs.Add(DocSpec(600, 900, "slow.png", i));
        DocView.Open(dSpecs, "后台测试", 1260f, 0f, 48f);

        var swD = Stopwatch.StartNew();
        DocView.SyncWindow(0f, 1680f, 1, 1f);           // 一拍：只排队，不等渲染
        swD.Stop();
        Check("一拍不阻塞（< 30ms）", swD.Elapsed.TotalMilliseconds < 30,
              $"{swD.Elapsed.TotalMilliseconds:F1} ms（渲染在后台线程）");
        Check("第一拍还没成品（正在后台渲）", DocView.ResidentPages == 0, $"驻留={DocView.ResidentPages}");

        var swWait = Stopwatch.StartNew();
        while (swWait.Elapsed.TotalSeconds < 3 && DocView.ResidentPages < 3)
        {
            Thread.Sleep(20);
            DocView.SyncWindow(0f, 1680f, 1, 1f);
        }
        Check("后台陆续到货（≥3 页）", DocView.ResidentPages >= 3,
              $"驻留={DocView.ResidentPages}，等了 {swWait.Elapsed.TotalMilliseconds:F0} ms");

        // 在途渲染时关档：Close 会等它收手（PDF 文档随后才能安全释放），不崩、也清干净
        Thread.Sleep(5);
        DocView.SyncWindow(0f, 1680f, 1, 1f);           // 保证有活在途
        var swClose = Stopwatch.StartNew();
        DocView.Close();
        swClose.Stop();
        Check("在途关档：不崩、清干净", DocView.Count == 0 && DocView.ResidentPages == 0
              && ImageData.LiveImages == dBaseImages,
              $"驻留={DocView.ResidentPages} LiveImages={ImageData.LiveImages}（基 {dBaseImages}），" +
              $"关档用时 {swClose.Elapsed.TotalMilliseconds:F0} ms");

        DocView.UseWorker = false;
        DocView.Generator = null;

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：文档页底层 {pass} 项通过 / {fail} 项失败");
        Console.WriteLine($"合计：通过 {pass} 项，失败 {fail} 项");

        _quit = true;
    }

    /// <summary>
    /// `--docstates <文件>`：把"文档 × 白板 × 穿透"的状态组合逐一摆出来 + 抓屏。
    /// 给"文档到底归谁管"这件事**提供事实而不是猜**（2026-10-07 用户实测反馈后加的探针）。
    /// 出图在 `%TEMP%\inkteach-docstates\`（BMP，物理像素）。
    /// </summary>
    private void DocStatesProbe(string path)
    {
        Console.WriteLine();
        Console.WriteLine("=== 文档状态探针（--docstates）===");
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            Console.WriteLine($"  FAIL：给的路径读不了：{path ?? "(没给)"}");
            _quit = true;
            return;
        }
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-docstates");
        Directory.CreateDirectory(dir);

        void Shot(string name)
        {
            SettleFrames(300);
            string p = System.IO.Path.Combine(dir, name + ".bmp");
            bool ok = ScreenProbe.SaveBmp(p, 0, 0, _virtualW, _virtualH);
            Console.WriteLine($"  {name,-14} 板={BoardOn,-5} 穿透={PassThrough,-5} " +
                              $"文档={DocView.IsOpen}({DocView.Count}页/驻留{DocView.ResidentPages}) " +
                              $"{(ok ? "已存图" : "存图失败")}");
        }

        // 干净起点：穿透关、板关
        if (PassThrough) SetPassThroughFromUi(false);
        if (BoardOn) SetBoardFromUi(false);

        var err = OpenDocuments(new[] { path });
        if (err != null) { Console.WriteLine("  FAIL：" + err); _quit = true; return; }

        Shot("1-打开文档");
        SetBoardFromUi(true);       Shot("2-开白板");
        SetPassThroughFromUi(true); Shot("3-开穿透");
        SetPassThroughFromUi(false); Shot("4-关穿透");
        SetBoardFromUi(false);      Shot("5-关白板");
        SetBoardFromUi(true);       Shot("6-再开白板");
        CloseDocument();            Shot("7-关闭文档");

        Console.WriteLine("  PASS：状态探针跑完，图在 " + dir);
        _quit = true;
    }

    /// <summary>
    /// 合成一份**最小三页 PDF**（红/绿/蓝满页、不用字体、不用压缩）：自检不依赖外部素材，
    /// 改颜色/页数一眼能改。手写 PDF 的老规矩——xref 偏移在循环里现算，不会写歪。
    /// </summary>
    private static string WriteTestPdf(string path)
    {
        static string StreamObj(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 7 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 8 0 R >>",
            StreamObj("1 0 0 rg 0 0 595 842 re f"),
            StreamObj("0 1 0 rg 0 0 595 842 re f"),
            StreamObj("0 0 1 rg 0 0 595 842 re f"),
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new int[objs.Count];
        for (int i = 0; i < objs.Count; i++)
        {
            offsets[i] = sb.Length;                 // ASCII ⇒ 字符数就是字节数
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.ASCII);
        return path;
    }

    /// <summary>
    /// `--pdfprobe <文件.pdf> [页数]`：把前几页按屏宽渲成 PNG 放到
    /// `%TEMP%\inkteach-pdfprobe\`，打出尺寸与每页耗时。
    /// （照 Wintab 探针的先例：先能看见真东西、拿到真数字，再谈接界面。）
    /// </summary>
    private void PdfProbe(string path, int pages)
    {
        Console.WriteLine();
        Console.WriteLine("=== PDF 探针（--pdfprobe）===");
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            Console.WriteLine($"  FAIL：给的路径读不了：{path ?? "(没给)"}");
            Console.WriteLine("  用法：--pdfprobe <文件.pdf> [页数=3]");
            _quit = true;
            return;
        }
        if (!Pdfium.EnsureLoaded(out var loadErr)) { Console.WriteLine("  FAIL：" + loadErr); _quit = true; return; }
        if (!Pdfium.Open(path, out var doc, out var err)) { Console.WriteLine("  FAIL：" + err); _quit = true; return; }

        Console.WriteLine($"  文件：{path}");
        Console.WriteLine($"  页数：{doc.PageCount}");
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-pdfprobe");
        Directory.CreateDirectory(dir);

        int n = Math.Clamp(pages, 1, doc.PageCount);
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            var (pw, ph) = doc.Size(i);
            float scale = _virtualW / pw;
            int w = Math.Max(1, (int)MathF.Round(pw * scale));
            int h = Math.Max(1, (int)MathF.Round(ph * scale));
            var sw = Stopwatch.StartNew();
            var px = doc.RenderPage(i, w, h);
            sw.Stop();
            sum += sw.Elapsed.TotalMilliseconds;
            if (px == null) { Console.WriteLine($"  第 {i + 1} 页：渲染失败"); continue; }
            string outPng = System.IO.Path.Combine(dir, $"P{i + 1}.png");
            using (var b = BgraToBitmap(px, w, h))
                b.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  第 {i + 1} 页：{pw:F0}x{ph:F0}pt → {w}x{h}px  {sw.Elapsed.TotalMilliseconds:F0} ms  → {outPng}");
        }
        Console.WriteLine($"  平均 {sum / n:F0} ms/页（含渲染进缓冲）");
        Console.WriteLine($"  PASS：PDFium 出图成功（{n} 页）");
        doc.Dispose();
        _quit = true;
    }
}
