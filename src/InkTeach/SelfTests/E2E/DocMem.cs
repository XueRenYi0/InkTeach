// [职责] PDF 缩放内存归因诊断（--docmem）：WS/私有/托管/LOH/GPU/页位图/分块，含 100ms 峰值与回收后快照。
// [测试] --docmem

// 只测量、不改渲染行为：所有缩放都走真实入口 SetDocZoomFromUi，页位图 / 分块 /
// 页缓存的生成与淘汰全部照产品路径（worker 开、缓存余量按 DocMemory.CacheMarginScreens）。
// 每步打印"瞬时 / 稳态 / 回收后"三个点，另外挂一个 100ms 后台采样器记峰值——
// 这样 1GB 到底是稳态占用还是过渡期的 LOH / 驱动缓存一眼能分开。

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    // ======================================================================
    //  数据结构
    // ======================================================================

    /// <summary>一次快照（全部是"读出来的数"，不做任何换算）。</summary>
    private sealed class DocMemSample
    {
        public string Step = "";
        public float Zoom = 1f;
        public double WsMb, PrivMb;
        public double ManagedMb, HeapMb, FragMb, LohMb;
        public double GpuLocalMb, GpuNonLocalMb;
        public double ImageMb;
        public int Pages;
        public double PageMb;
        public double CpuPageMb;
        public float RenderScale = 1f;
        public int BmpW, BmpH;
        public int PatchCount;
        public double PatchMb;
        public float PatchScale = 1f;
        public int PatchReuse;
        public string Spec = "n/a";
        public int TileCount, TileVisible, TileBudget, TileTexture;
        public double TileMb;
        public float TileScale = 1f;
        public float CamScale = 1f;
        // 这一拍（上一行快照到现在）的峰值，来自 100ms 采样器。
        public double PeakWsMb, PeakPrivMb, PeakGpuLocalMb;
    }

    /// <summary>
    /// 100ms 后台采样器：只读计数，不碰渲染。峰值分两套——全程 + 当前拍，
    /// 当前拍在每次 <see cref="DocMemAdd"/> 之后清零，所以每一行的"峰值"就是
    /// "从上一行到现在"这段过渡里真实出现过的最大值。
    /// </summary>
    private sealed class DocMemPeakSampler : IDisposable
    {
        private readonly System.Threading.Timer _timer;
        private readonly Process _process = Process.GetCurrentProcess();
        private int _busy;

        private long _ws, _priv, _gpuL, _gpuN;
        private long _pWs, _pPriv, _pGpuL, _pGpuN;

        public int Samples;

        public DocMemPeakSampler()
            => _timer = new System.Threading.Timer(_ => Sample(), null, 100, 100);

        private void Sample()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                _process.Refresh();
                long ws = _process.WorkingSet64;
                long priv = _process.PrivateMemorySize64;
                long gl = 0, gn = 0;
                var gpu = DocMemGpuBytes();
                if (!double.IsNaN(gpu.local)) gl = (long)gpu.local;
                if (!double.IsNaN(gpu.nonLocal)) gn = (long)gpu.nonLocal;

                if (ws > _ws) _ws = ws;
                if (priv > _priv) _priv = priv;
                if (gl > _gpuL) _gpuL = gl;
                if (gn > _gpuN) _gpuN = gn;
                if (ws > _pWs) _pWs = ws;
                if (priv > _pPriv) _pPriv = priv;
                if (gl > _pGpuL) _pGpuL = gl;
                if (gn > _pGpuN) _pGpuN = gn;
                Samples++;
            }
            catch { /* 有意忽略：采样失败不能影响被测进程 */ }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        public void ResetPhase()
        {
            Interlocked.Exchange(ref _pWs, 0);
            Interlocked.Exchange(ref _pPriv, 0);
            Interlocked.Exchange(ref _pGpuL, 0);
            Interlocked.Exchange(ref _pGpuN, 0);
        }

        public double PhaseWsMb => _pWs / 1048576.0;
        public double PhasePrivMb => _pPriv / 1048576.0;
        public double PhaseGpuLocalMb => _pGpuL / 1048576.0;
        public double TotalWsMb => _ws / 1048576.0;
        public double TotalPrivMb => _priv / 1048576.0;
        public double TotalGpuLocalMb => _gpuL / 1048576.0;
        public double TotalGpuNonLocalMb => _gpuN / 1048576.0;

        public void Stop() => _timer.Dispose();
        public void Dispose() { Stop(); _process.Dispose(); }
    }

    // ======================================================================
    //  主流程
    // ======================================================================

    /// <summary>
    /// `--docmem`：打开合成 3 页 PDF → 3 轮 100%↔200% → 1 次 50%→200%，
    /// 每拍打印瞬时 / 稳态 / 回收后，最后写 reports/docmem-&lt;时间戳&gt;.md。
    /// 任何指标拿不到都只打印 n/a，最终退出码仍是 0（纯测量模式）。
    /// </summary>
    private void DocMem()
    {
        Console.WriteLine();
        Console.WriteLine("=== 文档缩放内存归因（--docmem）===");
        var rows = new List<DocMemSample>();
        using var sampler = new DocMemPeakSampler();

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-docmem");
        Directory.CreateDirectory(dir);
        string pdf = System.IO.Path.Combine(dir, "内存归因测试.pdf");
        WriteTestPdf(pdf);

        bool pdfOk = Pdfium.EnsureLoaded(out string pdfErr);
        if (!pdfOk)
            Console.WriteLine("  n/a：pdfium.dll 加载失败（" + pdfErr + "），只写环境与空报告");

        // 干净起点 + 产品路径（worker 开；页缓存余量按产品常量）。
        CloseDocument();
        DocView.Close();
        Doc.Clear();
        Doc.ClearHistory();
        if (BoardOn) SetBoardFromUi(false);
        if (PassThrough) SetPassThroughFromUi(false);
        ZoomTestResetCamera();
        DocPageWorker = true;
        DocView.UseWorker = true;

        if (pdfOk)
        {
            var err = OpenDocuments(new[] { pdf });
            if (err != null)
            {
                Console.WriteLine("  n/a：打开合成 PDF 失败（" + err + "）");
                pdfOk = false;
            }
        }

        if (pdfOk)
        {
            try
            {
                DocMemSettle(1);                                   // 打开后先落稳
                DocMemAdd(rows, sampler, "open 100%");

                for (int c = 1; c <= 3; c++)
                {
                    DocMemZoom(rows, sampler, $"c{c} 200%", 2f);
                    DocMemZoom(rows, sampler, $"c{c} 100%", 1f);
                }

                // 单独一趟 50% → 200%：从"页只缩不合"的档位直接跳到最大档。
                DocMemZoom(rows, sampler, "50%", 0.5f);
                DocMemZoom(rows, sampler, "50->200%", 2f);

                // 1GB 假设里最值得验的一刀：200% 且视口跨页缝 → **两页同时驻留**。
                // 真实阅读就常常停在这个位置（一页底 + 下一页顶）。
                DocMemSeamProbe(rows, sampler);
            }
            catch (Exception ex)
            {
                // 测量中断也要留下已经采到的数：报告照写，退出码仍是 0。
                Console.WriteLine("  n/a：测量中断（" + ex.Message + "），报告只含中断前的样本");
            }
        }

        sampler.Stop();
        DocMemWriteReport(rows, sampler, pdfOk ? pdf : "");

        // 收尾：把页面 / 文档 / 相机拨回基线（进程随后退出，但保持和 --zoomtest 一致）。
        CloseDocument();
        Doc.Clear();
        Doc.ClearHistory();
        ZoomTestResetCamera();
        try { Directory.Delete(dir, true); } catch { /* 有意忽略：清理失败不影响测量结果 */ }
        _quit = true;
    }

    /// <summary>一轮缩放：改倍率 → 瞬时读数 → 落稳 → 稳态读数 → 强制回收 → 回收后读数。</summary>
    private void DocMemZoom(List<DocMemSample> rows, DocMemPeakSampler sampler, string step, float zoom)
    {
        SetDocZoomFromUi(zoom);
        SettleFrames(40);                       // 让"换倍率 / 放旧图 / 重建配方"这一拍发生
        DocMemAdd(rows, sampler, step + " +40ms");
        DocMemSettle(1);                        // 等到页位图重新生成、分块重新烘好
        DocMemAdd(rows, sampler, step + " settle");
        DocMemTrim();
        DocMemAdd(rows, sampler, step + " trim");
    }

    /// <summary>
    /// 把相机移到第 1/2 页的页缝：200% 下视口同时压住两页，逼页缓存驻留两张位图。
    /// 只移动相机，不改页缓存 / 分块 / 渲染规则。
    /// </summary>
    private void DocMemSeamProbe(List<DocMemSample> rows, DocMemPeakSampler sampler)
    {
        if (DocView.Count < 2) return;
        SetDocZoomFromUi(2f);
        DocMemSettle(1);
        float seam = (DocView.At(0).Rect.MaxY + DocView.At(1).Rect.MinY) * 0.5f;
        ViewOffsetY = _virtualY + _virtualH * 0.5f - seam * ViewScale;
        foreach (var w in _windows) w.ViewOffsetY = MathF.Round(ViewOffsetY);
        SettleFrames(60);
        DocMemAdd(rows, sampler, "seam 200% +60ms");
        DocMemSettle(2, 8000, 2);                   // 跨页缝：两页各一块补丁
        DocMemAdd(rows, sampler, "seam 200% settle");
        DocMemTrim();
        DocMemAdd(rows, sampler, "seam 200% trim");
    }

    /// <summary>
    /// 落稳：每 150ms 出一帧，驻留页数连续 2 拍不变且达到下限就算稳。
    /// <paramref name="expectedPatches"/> = 期望的高清补丁块数（&gt;100% 时才等；补丁是本次的内存主角）。
    /// </summary>
    private int DocMemSettle(int expectedMin, int maxMs = 8000, int expectedPatches = 1)
    {
        int wantPatches = ViewScale > 1.0001f ? Math.Max(1, expectedPatches) : 0;
        var sw = Stopwatch.StartNew();
        int last = -1, stable = 0;
        while (sw.ElapsedMilliseconds < maxMs)
        {
            SettleFrames(150);
            int now = DocView.ResidentPages;
            stable = now == last ? stable + 1 : 0;
            last = now;
            bool patchesOk = wantPatches <= 0 || (DocPatch != null && DocPatch.PatchCount >= wantPatches);
            if (now >= expectedMin && stable >= 2 && patchesOk) break;
        }
        return DocView.ResidentPages;
    }

    /// <summary>`GC.Collect(2, Aggressive)` + 工作集 Trim + DXGI Trim：分开"还活着"和"缓存/垃圾"。</summary>
    private static void DocMemTrim()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        Mem.TrimWorkingSet();
        Gfx.TrimVideoMemory();
        Mem.TrimWorkingSet();
    }

    // ======================================================================
    //  读数
    // ======================================================================

    /// <summary>取一拍全部指标、写进表、并打一行到控制台；随后把峰值清零。</summary>
    private void DocMemAdd(List<DocMemSample> rows, DocMemPeakSampler sampler, string step)
    {
        var s = DocMemRead(step);
        // 40ms 的窗口可能一个采样点都没有：峰值至少取当前值，否则会误报 0。
        s.PeakWsMb = DocMemMax(sampler.PhaseWsMb, s.WsMb);
        s.PeakPrivMb = DocMemMax(sampler.PhasePrivMb, s.PrivMb);
        s.PeakGpuLocalMb = DocMemMax(sampler.PhaseGpuLocalMb, s.GpuLocalMb);
        rows.Add(s);
        sampler.ResetPhase();

        Console.WriteLine($"  {step,-18} Zoom={s.Zoom * 100,3:F0}%(相机{s.CamScale:F2})  WS={DocMemMb(s.WsMb),7}  私有={DocMemMb(s.PrivMb),7}"
            + $"  托管={DocMemMb(s.ManagedMb),7}  堆={DocMemMb(s.HeapMb),7}  碎片={DocMemMb(s.FragMb),5}  LOH={DocMemMb(s.LohMb),7}"
            + $"  GPU={DocMemMb(s.GpuLocalMb),7}+{DocMemMb(s.GpuNonLocalMb),6}"
            + $"  Image={DocMemMb(s.ImageMb),7}  页={s.Pages}/{DocMemMb(s.PageMb)}MB(CPU{DocMemMb(s.CpuPageMb)})"
            + $"  补丁={s.PatchCount}块/{DocMemMb(s.PatchMb)}MB@{s.PatchScale:F2}"
            + $"  缓冲复用={s.PatchReuse}"
            + $"  位图={s.BmpW}×{s.BmpH}"
            + $"  分块={s.TileCount}(可见{s.TileVisible})×{s.TileTexture}²@{s.TileScale:F2}={DocMemMb(s.TileMb)}MB"
            + $"  峰值 WS={DocMemMb(s.PeakWsMb)}/私有={DocMemMb(s.PeakPrivMb)}");
    }

    /// <summary>读一次进程 / 托管 / GPU / 页位图 / 分块计数。失败项一律 NaN，打印成 n/a。</summary>
    private DocMemSample DocMemRead(string step)
    {
        var s = new DocMemSample { Step = step, Zoom = DocZoom };
        try
        {
            var p = Process.GetCurrentProcess();
            p.Refresh();
            s.WsMb = p.WorkingSet64 / 1048576.0;
            s.PrivMb = p.PrivateMemorySize64 / 1048576.0;
        }
        catch { s.WsMb = double.NaN; s.PrivMb = double.NaN; }

        try
        {
            s.ManagedMb = GC.GetTotalMemory(false) / 1048576.0;
            var gi = GC.GetGCMemoryInfo();
            s.HeapMb = gi.HeapSizeBytes / 1048576.0;
            s.FragMb = gi.FragmentedBytes / 1048576.0;
            s.LohMb = gi.GenerationInfo.Length > 3
                ? gi.GenerationInfo[3].SizeAfterBytes / 1048576.0
                : double.NaN;
        }
        catch { s.ManagedMb = s.HeapMb = s.FragMb = s.LohMb = double.NaN; }

        var gpu = DocMemGpuBytes();
        s.GpuLocalMb = gpu.local / 1048576.0;
        s.GpuNonLocalMb = gpu.nonLocal / 1048576.0;

        s.ImageMb = ImageData.LiveBytes / 1048576.0;
        s.Pages = DocView.ResidentPages;
        s.PageMb = DocView.ResidentBytes / 1048576.0;    // 底图（1× 整页）单独报
        s.CpuPageMb = DocMemCpuPageBytes() / 1048576.0;
        s.RenderScale = DocView.PageRenderScale;
        s.PatchCount = DocPatch?.PatchCount ?? 0;
        s.PatchMb = (DocPatch?.ResidentBytes ?? 0) / 1048576.0;   // 高清补丁单独报
        s.PatchScale = DocPatch?.Scale ?? 1f;
        s.PatchReuse = DocPatch?.PatchBufferReuseCount ?? 0;

        int idx = Math.Clamp(BarPageNow - 1, 0, Math.Max(0, DocView.Count - 1));
        var page = DocView.At(idx);
        if (page != null)
        {
            s.BmpW = page.Image != null ? page.Image.Width : page.Spec.BitmapW;
            s.BmpH = page.Image != null ? page.Image.Height : page.Spec.BitmapH;
            var sp = page.Spec;
            s.Spec = $"p{idx + 1} kind={sp.Kind} out={sp.OutW}x{sp.OutH}"
                + $" bmp={sp.BitmapW}x{sp.BitmapH} scale={sp.RenderScale:F2}";
        }

        int tc = 0, tv = 0, tb = 0, tt = 0;
        long tbytes = 0;
        float tscale = 1f;
        foreach (var w in _windows)
        {
            tc += w.LastTileCount;
            tv += w.LastTileVisible;
            tb += w.LastTileBudget;
            tbytes += (long)w.LastTileCount * w.LastTileBytes;
            if (w.LastTileTextureSize > 0) tt = w.LastTileTextureSize;
            tscale = w.LastTileRenderScale;
        }
        s.TileCount = tc;
        s.TileVisible = tv;
        s.TileBudget = tb;
        s.TileTexture = tt;
        s.TileMb = tbytes / 1048576.0;
        s.TileScale = tscale;
        s.CamScale = _windows.Count > 0 ? _windows[0].ViewScale : ViewScale;
        return s;
    }

    /// <summary>驻留页里**还留着 CPU 像素**的字节数（页上传 GPU 后 Bgra 置 null，见 ImageData.DropCpuBytes）。</summary>
    private long DocMemCpuPageBytes()
    {
        long bytes = 0;
        for (int i = 0; i < DocView.Count; i++)
        {
            var img = DocView.At(i)?.Image;
            if (img?.Bgra != null) bytes += img.ByteSize;
        }
        return bytes;
    }

    /// <summary>DXGI 视频内存（专用 / 共享）。返回字节；不可用时 NaN。</summary>
    private static (double local, double nonLocal) DocMemGpuBytes()
    {
        try
        {
            var adapter = Gfx.Adapter3;
            if (adapter == null) return (double.NaN, double.NaN);
            var l = adapter.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            var n = adapter.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.NonLocal);
            return (l.CurrentUsage, n.CurrentUsage);
        }
        catch { return (double.NaN, double.NaN); }
    }

    // ======================================================================
    //  报告
    // ======================================================================

    /// <summary>写 reports/docmem-&lt;时间戳&gt;.md，并在控制台打紧凑摘要。</summary>
    private void DocMemWriteReport(List<DocMemSample> rows, DocMemPeakSampler sampler, string pdf)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PDF 缩放内存归因（--docmem）");
        sb.AppendLine();
        sb.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 机器：{DiagLog.MachineModel} · {DiagLog.CpuName} · RAM {DiagLog.RamGb}G");
        sb.AppendLine($"- 屏幕：{_virtualW}×{_virtualH}（DPI 缩放 {DpiScale:F2}） · 覆盖窗口 {_windows.Count} 个");
        sb.AppendLine($"- 显卡：{Gfx.AdapterInfo}");
        sb.AppendLine($"- PDF 档位：{DocMemory.Name}（{DocMemory.Note}） · MaxPagePixels {DocMemory.MaxPagePixels / 1e6:F0}MP"
            + $" · 页位图恒 1×（不再按缩放整页重渲）· 高清补丁 = 可见区 + {DocPatchCache.MarginDevicePx:F0} 设备像素");
        sb.AppendLine($"- 样本：合成 3 页 PDF（{System.IO.Path.GetFileName(pdf)}） · 后台采样间隔 100ms"
            + $" · 全程峰值 WS {DocMemMb(sampler.TotalWsMb)} / 私有 {DocMemMb(sampler.TotalPrivMb)}"
            + $" / GPU(L) {DocMemMb(sampler.TotalGpuLocalMb)} MB");
        sb.AppendLine();
        sb.AppendLine("单位 MB。GPU = DXGI QueryVideoMemoryInfo(0, Local=专用, NonLocal=共享)。"
            + "「峰值」= 从上一条快照到这一条之间 100ms 采样器见到的最大值；"
            + "「trim」= GC.Collect(2, Aggressive, blocking, compacting) + LOH CompactOnce + EmptyWorkingSet + IDXGIDevice3::Trim。");
        sb.AppendLine();

        sb.AppendLine("## 逐步读数");
        sb.AppendLine();
        sb.AppendLine("| 步骤 | 缩放 | 相机 | WS | 私有 | GPU L | GPU NL | 托管 | Heap | 碎片 | LOH | ImageData | 驻留页 | 页字节 | 页CPU | 页倍率 | 页位图 | 补丁 | 补丁字节 | 补丁倍率 | Spec | 分块 | 可见 | 每块 | 分块字节 | 峰值WS | 峰值私有 | 峰值GPU L |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var s in rows) DocMemAppendRow(sb, s);
        sb.AppendLine();

        DocMemAppendSummary(sb, rows, sampler);
        DocMemAppendReadingGuide(sb, rows);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = System.IO.Path.Combine(System.IO.Path.GetFullPath("reports"), $"docmem-{stamp}.md");
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 仓库目录不可写时退到临时目录（仍然不写用户 AppData）。
            path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"docmem-{stamp}.md");
            try { File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
            catch { path = "(写报告失败)"; }
        }

        DocMemPrintSummary(rows, sampler, path);
    }

    /// <summary>一行 markdown。</summary>
    private static void DocMemAppendRow(StringBuilder sb, DocMemSample s)
    {
        sb.Append("| ").Append(s.Step)
          .Append(" | ").Append((s.Zoom * 100f).ToString("F0", CultureInfo.InvariantCulture)).Append('%')
          .Append(" | ").Append(s.CamScale.ToString("F2", CultureInfo.InvariantCulture))
          .Append(" | ").Append(DocMemMb(s.WsMb))
          .Append(" | ").Append(DocMemMb(s.PrivMb))
          .Append(" | ").Append(DocMemMb(s.GpuLocalMb))
          .Append(" | ").Append(DocMemMb(s.GpuNonLocalMb))
          .Append(" | ").Append(DocMemMb(s.ManagedMb))
          .Append(" | ").Append(DocMemMb(s.HeapMb))
          .Append(" | ").Append(DocMemMb(s.FragMb))
          .Append(" | ").Append(DocMemMb(s.LohMb))
          .Append(" | ").Append(DocMemMb(s.ImageMb))
          .Append(" | ").Append(s.Pages)
          .Append(" | ").Append(DocMemMb(s.PageMb))
          .Append(" | ").Append(DocMemMb(s.CpuPageMb))
          .Append(" | ").Append(s.RenderScale.ToString("F2", CultureInfo.InvariantCulture))
          .Append(" | ").Append(s.BmpW > 0 ? $"{s.BmpW}×{s.BmpH}" : "n/a")
          .Append(" | ").Append(s.PatchCount)
          .Append(" | ").Append(DocMemMb(s.PatchMb))
          .Append(" | ").Append(s.PatchScale.ToString("F2", CultureInfo.InvariantCulture))
          .Append(" | ").Append(s.Spec)
          .Append(" | ").Append(s.TileCount)
          .Append(" | ").Append(s.TileVisible)
          .Append(" | ").Append(s.TileTexture > 0 ? $"{s.TileTexture}²×4@{s.TileScale:F2}x" : "n/a")
          .Append(" | ").Append(DocMemMb(s.TileMb))
          .Append(" | ").Append(DocMemMb(s.PeakWsMb))
          .Append(" | ").Append(DocMemMb(s.PeakPrivMb))
          .Append(" | ").Append(DocMemMb(s.PeakGpuLocalMb))
          .AppendLine(" |");
    }

    /// <summary>四行对比：100% 基线 / 200% 稳态 / 过渡峰值 / 回收后。</summary>
    private static void DocMemAppendSummary(StringBuilder sb, List<DocMemSample> rows, DocMemPeakSampler sampler)
    {
        var baseline = rows.Count > 0 ? rows[0] : null;
        var steady100 = DocMemFind(rows, "c3 100% settle") ?? DocMemFind(rows, "100% settle");
        var steady200 = DocMemFind(rows, "c3 200% settle") ?? DocMemFind(rows, "200% settle");
        var trim200 = DocMemFind(rows, "c3 200% trim") ?? DocMemFind(rows, "200% trim");
        var seam200 = DocMemFind(rows, "seam 200% settle");
        double peakWs = sampler.TotalWsMb, peakPriv = sampler.TotalPrivMb, peakGpu = sampler.TotalGpuLocalMb;
        foreach (var r in rows)
        {
            if (r.PeakWsMb > peakWs) peakWs = r.PeakWsMb;
            if (r.PeakGpuLocalMb > peakGpu) peakGpu = r.PeakGpuLocalMb;
        }

        sb.AppendLine("## 关键对比");
        sb.AppendLine();
        sb.AppendLine("| 场景 | WS | 私有 | GPU L | LOH | 托管 | ImageData | 驻留页 | 页字节（底图 1×） | 补丁字节 | 分块字节 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        DocMemAppendSummaryRow(sb, "100% 基线（打开后）", baseline);
        DocMemAppendSummaryRow(sb, "200% 稳态", steady200);
        DocMemAppendSummaryRow(sb, "200% 跨页缝（2 页驻留）", seam200);
        DocMemAppendSummaryRow(sb, "100% 稳态（第 3 轮回落）", steady100);
        sb.Append("| 全程峰值（100ms 采样） | ").Append(DocMemMb(peakWs))
          .Append(" | ").Append(DocMemMb(peakPriv))
          .Append(" | ").Append(DocMemMb(peakGpu))
          .AppendLine(" | n/a | n/a | n/a | n/a | n/a | n/a | n/a |");
        DocMemAppendSummaryRow(sb, "200% 回收后（GC+Trim）", trim200);
        sb.AppendLine();
        DocMemAppendCycleReturn(sb, rows);
    }

    /// <summary>
    /// 回落判据（2026-11-16 内存抖动修复的验收口径）：第 1/2/3 轮回 100% 之后的**私有字节**
    /// 不许单调上涨。以前每轮新分配一块补丁缓冲（~25MB 的 LOH 不回收），一轮私有字节 +40~50MB；
    /// 现在单槽复用，末轮 100% 应该和首轮基本一样（允许 ≤20MB 的碎片 / 驱动缓存）。
    /// </summary>
    private static void DocMemAppendCycleReturn(StringBuilder sb, List<DocMemSample> rows)
    {
        var c1 = DocMemFind(rows, "c1 100% settle");
        var c2 = DocMemFind(rows, "c2 100% settle");
        var c3 = DocMemFind(rows, "c3 100% settle");
        if (c1 == null || c3 == null) return;

        double growth = c3.PrivMb - c1.PrivMb;
        bool pass = !double.IsNaN(growth) && growth <= 20.0;

        sb.AppendLine("### 每轮回 100% 的私有字节（不许单调上涨）");
        sb.AppendLine();
        sb.AppendLine("| 轮次 | 私有 | WS | LOH | ImageData | 补丁 | 补丁缓冲复用 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        DocMemAppendCycleRow(sb, "c1", c1);
        DocMemAppendCycleRow(sb, "c2", c2);
        DocMemAppendCycleRow(sb, "c3", c3);
        sb.AppendLine();
        sb.AppendLine(pass
            ? $"- **PASS**：末轮比首轮 {growth:+0.0;-0.0}MB（≤20MB），补丁缓冲复用 {c3.PatchReuse} 次"
              + " —— 池子生效，没有每轮新增常驻。"
            : $"- **FAIL**：末轮比首轮 {growth:+0.0;-0.0}MB（>20MB），仍有每轮新增常驻；"
              + $"先看 `补丁缓冲复用`（{c3.PatchReuse}）是否在涨，再看 LOH / GPU 各自涨了多少。");
        sb.AppendLine();
    }

    private static void DocMemAppendCycleRow(StringBuilder sb, string name, DocMemSample s)
    {
        if (s == null)
        {
            sb.AppendLine($"| {name} | n/a | n/a | n/a | n/a | n/a | n/a |");
            return;
        }
        sb.Append("| ").Append(name)
          .Append(" | ").Append(DocMemMb(s.PrivMb))
          .Append(" | ").Append(DocMemMb(s.WsMb))
          .Append(" | ").Append(DocMemMb(s.LohMb))
          .Append(" | ").Append(DocMemMb(s.ImageMb))
          .Append(" | ").Append(s.PatchCount + "块/" + DocMemMb(s.PatchMb) + "MB")
          .Append(" | ").Append(s.PatchReuse)
          .AppendLine(" |");
    }

    private static void DocMemAppendSummaryRow(StringBuilder sb, string name, DocMemSample s)
    {
        sb.Append("| ").Append(name);
        if (s == null)
        {
            sb.AppendLine(" | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a | n/a |");
            return;
        }
        sb.Append(" | ").Append(DocMemMb(s.WsMb))
          .Append(" | ").Append(DocMemMb(s.PrivMb))
          .Append(" | ").Append(DocMemMb(s.GpuLocalMb))
          .Append(" | ").Append(DocMemMb(s.LohMb))
          .Append(" | ").Append(DocMemMb(s.ManagedMb))
          .Append(" | ").Append(DocMemMb(s.ImageMb))
          .Append(" | ").Append(s.Pages)
          .Append(" | ").Append(DocMemMb(s.PageMb))
          .Append(" | ").Append(s.PatchCount + "块/" + DocMemMb(s.PatchMb) + "MB")
          .Append(" | ").Append(DocMemMb(s.TileMb))
          .AppendLine(" |");
    }

    /// <summary>报告的"怎么读"：避免只看工作集就下结论。</summary>
    private static void DocMemAppendReadingGuide(StringBuilder sb, List<DocMemSample> rows)
    {
        sb.AppendLine("## 怎么读");
        sb.AppendLine();
        sb.AppendLine("- **页字节 / ImageData**：`DocView.ResidentBytes` 是当前驻留页位图的账；"
            + "`ImageData.LiveBytes` 还包含文档里的图片对象。两者接近时，页位图就是 CPU 侧大头。");
        sb.AppendLine("- **GPU L**：DXGI 的专用显存当前用量。页位图会被 D2D 复制成纹理；"
            + "GPU 与 ImageData 同时按面积涨，说明 4 字节/像素在 CPU 和显存各有一份。");
        sb.AppendLine("- **补丁 / 补丁字节**：2026-11-16 起 PDF 放大后的清晰度由「可见区补丁」提供："
            + "底图恒 1×（`页字节`），补丁是一块设备分辨率小图（≈ 一屏，与倍率无关）。"
            + "200% 时应该是「底图不变 + 补丁 ≈ 30MB 级」，不再是整页 2×（本机约 143MB）。"
            + "100% 时补丁为 0——绘制路径与以前一致。");
        sb.AppendLine("- **缓冲复用**：补丁的 `byte[]`（一块屏大小）在**上传 GPU 后**会还进 `DocPatchCache` 的"
            + "**单槽**，下一次渲补丁原地复用（`缓冲复用` 计数涨 = 命中，没有新分配）。"
            + "所以 100% 稳态的 LOH 里常驻**一块**是有意的：一块常驻换掉「每轮新分配一块 LOH」"
            + "（2026-11-16 内存抖动修复；判据看下面「每轮回 100%」那张表）。");
        sb.AppendLine("- **页倍率**：现在恒为 1.00（只作诊断口径）。看到 2.00 说明有别的路径又去整页重渲了。");
        sb.AppendLine("- **LOH**：页位图是 `byte[]`（大对象）。LOH 跟着页面积走 = 活对象；"
            + "回收后 LOH 明显下降 = 之前是垃圾/待回收；不降 = 仍被引用（缓存 / 双缓冲）。");
        sb.AppendLine("- ⚠ `Heap` / `碎片` / `LOH` 来自 `GC.GetGCMemoryInfo()`，反映的是**最近一次 GC** 的快照；"
            + "刚分配完还没触发 GC 时会滞后（例如 200% settle 行仍显示 100% 的 34MB）。"
            + "所以判断活对象要看紧跟的 `trim` 行，不要只看 settle 行。");
        sb.AppendLine("- **峰值 vs 稳态**：峰值远高于稳态，说明 1GB 是过渡期（旧倍率位图未放、"
            + "新页正在 PDFium 渲染、落地成纹理的那一帧）；两者接近则是稳态常驻。");
        sb.AppendLine("- **trim 之后仍高**：GC + EmptyWorkingSet + DXGI Trim 都做过还高，"
            + "就是真实活对象或驱动/D2D 内部缓存（只能靠换倍率/关文档验证）。");

        // 本次读数暴露的既存前提：分块倍率没跟着缩放走（不是本模式改出来的）。
        bool anyZoomed = false, tileStuckAt1 = true;
        foreach (var r in rows)
        {
            if (r.Zoom <= 1.5f) continue;
            anyZoomed = true;
            if (MathF.Abs(r.TileScale - 1f) > 0.01f) tileStuckAt1 = false;
        }
        if (anyZoomed && tileStuckAt1)
        {
            sb.AppendLine("- ⚠ **本次读数**：200% 时 `CanvasTileCache.RenderScale` 仍是 1.00，"
                + "`LastTileTextureSize` 仍是 256²。对应代码在 `Overlay.Content.cs`："
                + "`_lastViewScale` 初值为 NaN，`MathF.Abs(ViewScaleSafe - NaN) > eps` 恒为 false，"
                + "`SetRenderScale` 从未被调用（**既有行为，本次测量未修改**）。"
                + "因此分块字节固定在 17.5MB 量级，不是 1GB 的来源；"
                + "但 200% 下墨迹/页图是由 1x 纹理放大显示的（观感偏糊），换页管线时要一并确认。");
        }

        // trim 后还活着的 LOH：如果页对象的 CPU 像素都已放掉，说明它由别处持有。
        var trim200 = DocMemFind(rows, "c3 200% trim");
        if (trim200 != null && trim200.LohMb > 1 && anyZoomed && trim200.CpuPageMb < 0.5)
        {
            sb.AppendLine($"- ⚠ **本次读数**：200% 回收后 LOH 仍有 {DocMemMb(trim200.LohMb)}MB，"
                + "但所有驻留页的 CPU 像素都已放掉（`页CPU` = 0）。这一块里通常有一份是"
                + "**补丁单槽**（一块屏大小，有意保留复用，见上一条「缓冲复用」）；"
                + "剩下若是页渲染 worker 线程最后一次 PDFium 渲染的缓冲（线程停在循环里，"
                + "栈引用让一整页活过一次 GC），也可能是 D2D 上传暂存。它随当前页面积走、有界（一页 1×，"
                + "本机约 36MB；补丁阴影下不再有 137MB 那份）；要坐实需在 worker 退出后再 trim 一次比对。");
        }
        sb.AppendLine();
    }

    /// <summary>控制台摘要（5 行以内）。</summary>
    private void DocMemPrintSummary(List<DocMemSample> rows, DocMemPeakSampler sampler, string path)
    {
        var baseline = rows.Count > 0 ? rows[0] : null;
        var steady200 = DocMemFind(rows, "c3 200% settle") ?? DocMemFind(rows, "200% settle");
        var trim200 = DocMemFind(rows, "c3 200% trim") ?? DocMemFind(rows, "200% trim");
        var seam200 = DocMemFind(rows, "seam 200% settle");

        Console.WriteLine();
        Console.WriteLine($"  [docmem] 档位={DocMemory.Name}（{DocMemory.Note}） 窗口={_windows.Count} 采样={sampler.Samples}");
        if (baseline != null)
            Console.WriteLine($"    100% 基线 : WS {DocMemMb(baseline.WsMb)} / 私有 {DocMemMb(baseline.PrivMb)}"
                + $" / GPU {DocMemMb(baseline.GpuLocalMb)} / LOH {DocMemMb(baseline.LohMb)}"
                + $" / ImageData {DocMemMb(baseline.ImageMb)} / 底图 {baseline.Pages}页/{DocMemMb(baseline.PageMb)}MB"
                + $" / 补丁 {baseline.PatchCount}块/{DocMemMb(baseline.PatchMb)}MB");
        if (steady200 != null)
            Console.WriteLine($"    200% 稳态 : WS {DocMemMb(steady200.WsMb)} / 私有 {DocMemMb(steady200.PrivMb)}"
                + $" / GPU {DocMemMb(steady200.GpuLocalMb)} / LOH {DocMemMb(steady200.LohMb)}"
                + $" / ImageData {DocMemMb(steady200.ImageMb)} / 底图 {steady200.Pages}页/{DocMemMb(steady200.PageMb)}MB"
                + $" / 补丁 {steady200.PatchCount}块/{DocMemMb(steady200.PatchMb)}MB"
                + $" / 分块 {steady200.TileCount}×{steady200.TileTexture}²={DocMemMb(steady200.TileMb)}MB");
        Console.WriteLine($"    全程峰值 : WS {DocMemMb(sampler.TotalWsMb)} / 私有 {DocMemMb(sampler.TotalPrivMb)}"
            + $" / GPU {DocMemMb(sampler.TotalGpuLocalMb)}");
        if (seam200 != null)
            Console.WriteLine($"    跨页缝200%: WS {DocMemMb(seam200.WsMb)} / 私有 {DocMemMb(seam200.PrivMb)}"
                + $" / GPU {DocMemMb(seam200.GpuLocalMb)} / ImageData {DocMemMb(seam200.ImageMb)}"
                + $" / 底图 {seam200.Pages}页/{DocMemMb(seam200.PageMb)}MB"
                + $" / 补丁 {seam200.PatchCount}块/{DocMemMb(seam200.PatchMb)}MB");
        if (trim200 != null)
            Console.WriteLine($"    200% 回收后: WS {DocMemMb(trim200.WsMb)} / 私有 {DocMemMb(trim200.PrivMb)}"
                + $" / GPU {DocMemMb(trim200.GpuLocalMb)} / LOH {DocMemMb(trim200.LohMb)}"
                + $" / 托管 {DocMemMb(trim200.ManagedMb)}");
        var c1 = DocMemFind(rows, "c1 100% settle");
        var c3 = DocMemFind(rows, "c3 100% settle");
        if (c1 != null && c3 != null)
            Console.WriteLine($"    每轮100% : c1 私有 {DocMemMb(c1.PrivMb)}MB → c3 {DocMemMb(c3.PrivMb)}MB"
                + $"（Δ {DocMemMb(c3.PrivMb - c1.PrivMb)}MB；补丁缓冲复用 {c3.PatchReuse} 次）");
        Console.WriteLine($"  报告：{path}");
    }

    // ======================================================================
    //  小工具
    // ======================================================================

    private static DocMemSample DocMemFind(List<DocMemSample> rows, string step)
        => rows.Find(r => string.Equals(r.Step, step, StringComparison.Ordinal));

    private static string DocMemMb(double mb)
        => double.IsNaN(mb) ? "n/a" : mb.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>NaN 安全的 max（拿不到 GPU 时不能把已有峰值污染成 NaN）。</summary>
    private static double DocMemMax(double a, double b)
    {
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        return Math.Max(a, b);
    }
}
