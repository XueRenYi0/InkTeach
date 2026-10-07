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
        // ⚠ 探针读的是**屏幕合成结果**：必须开白板（不透明底），否则透明处会把桌面读进来。
        BoardOn = true;
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

        Console.WriteLine();
        Console.WriteLine($"  {(fail == 0 ? "PASS" : "FAIL")}：文档页底层 {pass} 项通过 / {fail} 项失败");
        Console.WriteLine($"合计：通过 {pass} 项，失败 {fail} 项");

        _quit = true;
    }
}
