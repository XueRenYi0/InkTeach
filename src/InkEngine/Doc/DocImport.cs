using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 文档导入的"门厅"：文件 → 页配方 → <see cref="DocPages"/>。
/// 图片这一条走 GDI+（<see cref="DocImageSource"/>）；PDF 走 PDFium（S5 接入）。
///
/// **规矩（10-03 调研第十一节，三条硬规矩）**：
///   · 不调用 = 零开销：没打开文档时这里一行都不跑；
///   · 用完释放：关文档 → 页位图全放 + 解码缓存清掉；
///   · 失败当没有：文件读不了 = 一句提示，**不动当前状态**（绝不影响别的功能）。
/// </summary>
public partial class InkEngine
{
    /// <summary>
    /// 打开文档。返回 null = 成功；否则是给用户看的一句失败原因。
    /// 分工：都是图片 → 一叠图页；就一份 PDF → PDF 文档；混选/多份 PDF → 提示重选。
    /// </summary>
    internal string OpenDocuments(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return "没有选中文件";

        var images = new List<string>();
        var pdfs = new List<string>();
        foreach (var p in paths)
        {
            if (DocImageSource.IsImage(p)) images.Add(p);
            else if (string.Equals(System.IO.Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase))
                pdfs.Add(p);
        }

        if (pdfs.Count == 0 && images.Count > 0) return OpenImageDocument(images);
        if (images.Count == 0 && pdfs.Count == 1) return OpenPdfDocument(pdfs[0]);
        if (pdfs.Count > 0) return "PDF 一次打开一份（图片可以多选）";
        return "没有能打开的文件（支持图片和 PDF）";
    }

    /// <summary>
    /// 打开一批图片：每张按屏宽铺页（超长图按 1 屏高切片），纵向一叠。
    /// 只有"读尺寸"这一步是现在做的（很快）；页位图到进视口才会生成（惰性）。
    /// </summary>
    internal string OpenImageDocument(IReadOnlyList<string> files)
    {
        var specs = new List<DocPages.Spec>();
        int ok = 0, bad = 0;
        foreach (var f in files)
        {
            if (!DocImageSource.TryReadInfo(f, out int w, out int h)) { bad++; continue; }
            specs.AddRange(DocImageSource.PlanSpecs(f, w, h, _virtualW, _virtualH));
            ok++;
        }
        if (specs.Count == 0 || ok == 0) return "这些图片都读不了（格式不支持或文件损坏）";

        string title = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{ok} 张图片";
        StartDocView(specs, title);
        if (bad > 0) Console.WriteLine($"    [文档] 有 {bad} 个文件读不了，已跳过");
        return null;
    }

    /// <summary>PDF（S5 接 PDFium）。</summary>
    internal string OpenPdfDocument(string path)
        => "PDF 支持还没接上（下一步 S5）";

    /// <summary>
    /// 排好页、装好生成器、锚在当前视口顶——打开之后**立刻能看到第一页**，
    /// 相机的"文档范围"由 <see cref="CanvasExtent"/> 并入页层保证（否则滚不到最后一页）。
    /// </summary>
    private void StartDocView(List<DocPages.Spec> specs, string title)
    {
        DocView.Generator = DocRenderSpec;
        var vp = ViewportCanvas;
        float gap = 24f * DpiScale;                       // 页缝 = 24 逻辑像素（和白板页界线同语言）
        float anchorTop = vp.MinY + 16f * DpiScale;       // 离视口顶留一点边
        DocView.Open(specs, title, CenterXOfCursorMonitor(), anchorTop, gap);
        ClampViewOffset();
        SetInkStatus($"文档：{title}（{DocView.Count} 页）");
        Console.WriteLine($"[文档] 已打开 {title}：{DocView.Count} 页（页图惰性生成）");
    }

    /// <summary>页生成的分派：图片走 GDI+；PDF 走 PDFium（S5）。</summary>
    private byte[] DocRenderSpec(DocPages.Spec spec)
        => spec.Kind == 0 ? DocImageSource.RenderSpec(spec) : null;

    /// <summary>关掉文档：页位图全放、解码缓存也清（"用完释放"，10-03 文档 7.4）。</summary>
    internal void CloseDocument()
    {
        if (!DocView.IsOpen) return;
        string title = DocView.Title;
        DocView.Close();
        DocImageSource.TrimCache();
        SetInkStatus($"已关闭文档：{title}");
        Console.WriteLine($"[文档] 已关闭：{title}（页位图与解码缓存已释放）");
    }

    /// <summary>
    /// 页往哪块屏上铺：**光标所在那块显示器**的横向中心。
    /// 单屏就是屏幕中心；教室里笔电 + 投影时，文档会铺在老师正在操作的那块屏上。
    /// </summary>
    private float CenterXOfCursorMonitor()
    {
        try
        {
            if (Native.GetCursorPos(out var pt))
            {
                var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
                var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                if (Native.GetMonitorInfo(mon, ref mi))
                    return (mi.rcWork.Left + mi.rcWork.Right) * 0.5f;
            }
        }
        catch { }
        return _virtualX + _virtualW * 0.5f;
    }
}
