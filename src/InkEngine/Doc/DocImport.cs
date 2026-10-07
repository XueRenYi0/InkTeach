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
    /// <summary>当前打开的 PDF（渲染要用它；换文档/关文档时释放）。图片文档时是 null。</summary>
    private PdfiumDoc _pdfDoc;

    /// <summary>
    /// 当前文档的落盘键（<see cref="DocStore.KeyOf"/>；空 = 没文档）。
    /// 文档批注的"自动保存/读回"都认它。
    /// </summary>
    internal string DocStoreKey = "";

    /// <summary>文档批注节流保存的下一次时刻 / 上次存过的版本（"没变就不写"）。</summary>
    private double _nextDocAutoSaveAtMs;
    private long _docSavedVersion = -1;

    // ======================================================================
    //  底部页码条（"像打开 PPT 一样"——2026-10-07 用户定，见 计划-文档模式-状态模型.md）
    // ======================================================================

    /// <summary>底部页码条现在活跃吗（PPT 放映中 / 打开了文档）。条的所有交互（命中/悬停/菜单/面板）都看它。</summary>
    internal bool PageBarActive => PptMode || DocView.IsOpen;

    /// <summary>
    /// 条现在**看得见 / 点得到**吗。与 <see cref="PageBarActive"/> 的差别 = 穿透：
    ///   · 文档模式：穿透 = 全让开（S2，用户 2026-10-07 定）→ 条跟着收 ✗；
    ///   · PPT 模式：穿透时条**保留**（它管的是下层放映的东西，还有用 ✓，有接输入小窗撑着）。
    /// </summary>
    internal bool PageBarVisible => PageBarActive && (PptMode || !PassThrough);

    /// <summary>条上的"第几页"（1 起）。文档模式 = 视口中心所在的页（没页时给 1）。</summary>
    internal int BarPageNow
    {
        get
        {
            if (!PptMode && DocView.IsOpen)
            {
                var vp = ViewportCanvas;
                int i = DocView.CurrentIndex(vp.MinY, vp.MaxY);
                if (i < 0) i = DocView.FirstAtOrAfter(vp.MinY);
                if (i >= DocView.Count) i = DocView.Count - 1;
                return 1 + Math.Max(0, i);
            }
            return PptSlide;
        }
    }

    /// <summary>条上的"共几页"。</summary>
    internal int BarTotal => !PptMode && DocView.IsOpen ? DocView.Count : PptTotal;

    /// <summary>翻上/下一页（箭头）。文档模式 = 跳页顶（连续滚仍用滚轮）。</summary>
    internal void BarPrev()
    {
        if (!PptMode && DocView.IsOpen) FlipPage(false);
        else PptPrevFromUi();
    }

    internal void BarNext()
    {
        if (!PptMode && DocView.IsOpen) FlipPage(true);
        else PptNextFromUi();
    }

    /// <summary>跳到第 <paramref name="page"/> 页（1 起；页号面板用）。文档模式 = 页顶对齐视口顶。</summary>
    internal void BarGoto(int page)
    {
        if (!PptMode && DocView.IsOpen)
        {
            int idx = Math.Clamp(page - 1, 0, Math.Max(0, DocView.Count - 1));
            float want = ClampOffset(_virtualY - DocView.TopOf(idx));
            if (Math.Abs(want - ViewOffsetY) >= 1f)
            {
                if (!ClientAreaAnimationOn) { ViewOffsetY = want; _dirty = true; }
                else
                {
                    _camFrom = ViewOffsetY; _camTo = want; _camStartMs = NowMs; _camAnimating = true;
                    _dirty = true;
                }
            }
            Console.WriteLine($"[文档] 跳到第 {idx + 1} 页");
            return;
        }
        PostPptCommand(3, page);                    // 3 = 跳到第 n 页
        Console.WriteLine($"[PPT] 跳到第 {page} 页");
    }

    // ======================================================================
    //  文档页空间（墨迹和桌面板书 / PPT 批注互不污染；相机各自记忆）
    // ======================================================================

    /// <summary>
    /// 进文档页空间：记住当前（桌面）槽的相机位置 → 切到文档槽 → 恢复文档槽的位置。
    /// 每槽一个相机位置是 PPT 模式早就用的机制（<c>_pageScroll</c>，见 Ppt.GotoPage）。
    /// </summary>
    private void EnterDocPageSpace(string storeKey)
    {
        _pageScroll[Doc.PageKey] = ViewOffsetY;
        if (Doc.SwitchPage(DocStore.SlotOf(storeKey)))
        {
            ViewOffsetY = _pageScroll.TryGetValue(Doc.PageKey, out var v) ? v : 0f;
            ClampViewOffset();
            _camAnimating = false;
            Doc.InvalidateAll();        // 内容全换：分块缓存整层作废（同 GotoPage）
        }
    }

    /// <summary>回桌面页空间（关文档时）。</summary>
    private void LeaveDocPageSpace()
    {
        _pageScroll[Doc.PageKey] = ViewOffsetY;
        if (Doc.SwitchPage(0))
        {
            ViewOffsetY = _pageScroll.TryGetValue(0, out var v) ? v : 0f;
            ClampViewOffset();
            _camAnimating = false;
            Doc.InvalidateAll();
        }
    }

    /// <summary>读回这份文档的批注（自动保存关着就不读，同 PPT 的语义）。读坏了当没有。</summary>
    private void LoadDocInk(string storeKey)
    {
        if (storeKey.Length == 0) return;
        if (!PptAutoSaveOn)
        {
            Console.WriteLine("[文档] 自动保存关着：这次不读盘上的批注（盘上原样留着）");
            return;
        }
        try
        {
            var blob = DocStore.Load(storeKey);
            if (blob == null) return;
            var strokes = InkSerializer.LoadStrokes(blob, out int maxId);
            Doc.LoadPageContent(Doc.PageKey, strokes, maxId);
            Console.WriteLine($"[文档] 读回批注 {strokes.Count} 个对象");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[文档] 读批注失败（当作没有）：" + ex.Message);
        }
    }

    /// <summary>
    /// 写文档批注（自动保存开着才写；**空文档不留空文件**——把已有文件删掉就完了，同 PPT 的规矩）。
    /// 关闭文档、节流（<see cref="StepDocAutoSave"/>）都走这里。
    /// </summary>
    internal void SaveDocInk()
    {
        if (DocStoreKey.Length == 0) return;
        if (!PptAutoSaveOn) return;
        try
        {
            var strokes = Doc.Strokes;
            if (strokes.Count > 0) DocStore.Save(DocStoreKey, InkSerializer.SaveStrokes(strokes));
            else DocStore.Delete(DocStoreKey);
            _docSavedVersion = Doc.Version;
        }
        catch (Exception ex) { Console.WriteLine("[文档] 批注写盘失败：" + ex.Message); }
    }

    /// <summary>节流自动保存（主循环每拍调）：文档开着 + 自动保存开 + 真的变了 + 到间隔。</summary>
    internal void StepDocAutoSave()
    {
        if (DocStoreKey.Length == 0 || !PptAutoSaveOn) return;
        if (NowMs < _nextDocAutoSaveAtMs) return;
        _nextDocAutoSaveAtMs = NowMs + _autoSaveEveryMs;
        if (Doc.Version == _docSavedVersion) return;
        SaveDocInk();
    }

    /// <summary>「清空文档墨迹」（菜单第 4 项）：清当前槽 + **连盘一起删**（同 PPT 的理由）。</summary>
    internal void ClearDocMarks()
    {
        int n = Doc.Strokes.Count;
        Doc.Clear();
        if (DocStoreKey.Length > 0) DocStore.Delete(DocStoreKey);
        _docSavedVersion = Doc.Version;
        SetInkStatus($"已清空文档墨迹（{n} 个对象）");
        Console.WriteLine($"[文档] 清空文档墨迹：{n} 个对象（连盘）");
    }

    /// <summary>
    /// 「打开文档…」（更多 → 墨迹）：多选对话框 → 图片铺页 / PDF 渲染。
    /// 与「打开墨迹」同一套对话框纪律：看门线程、焦点借用、自检不弹框。
    /// </summary>
    internal void OpenDocumentFromUi()
    {
        if (PptMode) { SetInkStatus("放映中不开文档（先退出放映）"); return; }
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return; }

        string dir = GetUiPref("docDir");
        BorrowFocusForDialog();
        ExportDialogOpen = true;
        string[] files;
        try { files = ExportFileDialog.AskForOpenDocuments(OwnerHwnd(), dir); }
        catch (Exception ex) { SetInkStatus("弹打开对话框失败：" + ex.Message); return; }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
        if (files == null || files.Length == 0) { SetInkStatus("已取消"); return; }
        SetUiPref("docDir", System.IO.Path.GetDirectoryName(files[0]) ?? "");

        var err = OpenDocuments(files);
        if (err != null) SetInkStatus("打开文档失败：" + err);
    }

    /// <summary>「关闭文档」：页位图 / 解码缓存 / PDF 全放；**批注留在画布上**。</summary>
    internal void CloseDocumentFromUi()
    {
        if (!DocView.IsOpen && _pdfDoc == null) { SetInkStatus("当前没有打开的文档"); return; }
        CloseDocument();
    }

    /// <summary>
    /// 文档页是否交给**后台线程**渲染（产品 = true）。自检里要"确定性的一拍一页"时关掉它
    /// （关掉就回到"同步当场渲"，判据稳定、不掺时序）。
    /// </summary>
    internal bool DocPageWorker = true;

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

        _pdfDoc?.Dispose();       // 换文档：上一份 PDF 放掉（图片文档不用它）
        _pdfDoc = null;

        string title = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{ok} 张图片";
        StartDocView(specs, title, DocStore.KeyOf(files));
        if (bad > 0) Console.WriteLine($"    [文档] 有 {bad} 个文件读不了，已跳过");
        return null;
    }

    /// <summary>
    /// 打开一份 PDF：每页按**屏宽**（fit width，用户 2026-10-07 定）排页，纵向一叠。
    /// 页位图惰性生成——打开只做"页数 + 每页尺寸"（读头，很快）。
    /// </summary>
    internal string OpenPdfDocument(string path)
    {
        if (!Pdfium.Open(path, out var doc, out string err)) return err;

        var specs = new List<DocPages.Spec>();
        try
        {
            for (int i = 0; i < doc.PageCount; i++)
            {
                var (pw, ph) = doc.Size(i);
                float scale = _virtualW / pw;                     // fit width（矢量页不存在"放大糊"）
                long px = (long)(pw * scale) * (long)(ph * scale);
                if (px > Pdfium.MaxPagePixels)                    // 极端长页/超大幅面：按像素预算缩
                {
                    scale *= MathF.Sqrt((float)Pdfium.MaxPagePixels / px);
                    Console.WriteLine($"    [文档] 第 {i + 1} 页很大，已缩到约 {Pdfium.MaxPagePixels / 1_000_000}MP");
                }
                int outW = Math.Max(1, (int)MathF.Round(pw * scale));
                int outH = Math.Max(1, (int)MathF.Round(ph * scale));
                specs.Add(new DocPages.Spec
                {
                    Kind = 1,
                    Source = path,
                    SourceIndex = i,
                    SrcX = 0f, SrcY = 0f, SrcW = pw, SrcH = ph,
                    OutW = outW, OutH = outH,
                });
            }
        }
        catch (Exception ex)
        {
            doc.Dispose();
            return "读取 PDF 页面尺寸失败：" + ex.Message;
        }

        _pdfDoc?.Dispose();
        _pdfDoc = doc;

        StartDocView(specs, System.IO.Path.GetFileName(path), DocStore.KeyOf(new[] { path }));
        return null;
    }

    /// <summary>
    /// 打开文档前板底是不是开着的（关文档时按它恢复）。
    /// 打开文档会自动开纸底（默认白），但那张纸是"文档的纸"——文档关了纸也收，
    /// 老师原来什么样就什么样（同"进穿透关板、退出恢复"的语义）。
    /// </summary>
    private bool _boardWasOnBeforeDoc = true;

    /// <summary>
    /// 排好页、进文档页空间、读回批注。
    ///
    /// **页锚在固定画布位置**（2026-10-07 定）：横向 = 主屏中心、纵向 = 画布 y=0 起。
    /// 为什么必须固定：批注是按画布坐标存的，页一挪位置批注就对不上了 ✗——
    /// 而"关掉再打开、切走再回来"都要能对上（自动保存/读回的前提）。
    /// 相机：进空间时恢复"上次在这份文档里滚到哪"（没有就停在页顶 = y 0）。
    /// </summary>
    private void StartDocView(List<DocPages.Spec> specs, string title, string storeKey)
    {
        DocView.Generator = DocRenderSpec;
        DocView.UseWorker = DocPageWorker;      // 后台渲染（产品默认开；见那行注释）
        DocView.OnResultReady = WakeForDocPage; // 后台渲完一页叫醒主循环（不叫就不上屏）

        // **文档的"纸底"**：打开文档时确保板底开着（默认白）——
        //   ① 页缝 / 页边有纸感（不再透出桌面）；② 将来"自适应撑满"、页不满屏时四周也是纸。
        // 关文档时恢复老师原来的板态（见 CloseDocument）。
        _boardWasOnBeforeDoc = BoardOn;
        if (!BoardOn) SetBoardFromUi(true);

        // 先进页空间（切槽 + 相机就位），再排页——页的锚点是固定的，不依赖当时视口。
        DocStoreKey = storeKey;
        EnterDocPageSpace(storeKey);

        float gap = 24f * DpiScale;                       // 页缝 = 24 逻辑像素（和白板页界线同语言）
        DocView.Open(specs, title, PrimaryScreenCenterX(), 0f, gap);
        LoadDocInk(storeKey);
        ClampViewOffset();
        _docSavedVersion = Doc.Version;
        SetInkStatus($"文档：{title}（{DocView.Count} 页）");
        Console.WriteLine($"[文档] 已打开 {title}：{DocView.Count} 页（页图惰性生成）");
    }

    /// <summary>
    /// 页往哪块屏上铺：**主屏的横向中心**。固定值（不是"光标所在屏"）——
    /// 锚点必须跨会话稳定，否则批注对不上（见 StartDocView 的说明）。
    /// </summary>
    private static float PrimaryScreenCenterX()
        => Native.GetSystemMetrics(0 /*SM_CXSCREEN*/) * 0.5f;

    /// <summary>页生成的分派：图片走 GDI+（解码+裁切+缩放），PDF 走 PDFium（渲染进缓冲）。</summary>
    private byte[] DocRenderSpec(DocPages.Spec spec)
        => spec.Kind switch
        {
            0 => DocImageSource.RenderSpec(spec),
            1 => _pdfDoc?.RenderPage(spec.SourceIndex, spec.OutW, spec.OutH),
            _ => null,
        };

    /// <summary>
    /// 关掉文档：**先存批注**（自动保存开着才写）→ 释放页位图 / 解码缓存 / PDF 文档
    /// （"用完释放"，10-03 文档 7.4）→ 切回桌面页空间（相机回到老师原来的位置）。
    /// </summary>
    internal void CloseDocument()
    {
        if (!DocView.IsOpen && _pdfDoc == null) return;
        string title = DocView.Title;

        SaveDocInk();                       // 关闭时兜底存一次（节流之外的那一下）

        DocView.Close();
        DocImageSource.TrimCache();
        _pdfDoc?.Dispose();
        _pdfDoc = null;

        LeaveDocPageSpace();                // 回桌面（含相机位置恢复）

        // **纸底随文档一起收**：打开文档时自动开的那个白板，关文档时恢复原样
        //（老师本来就开着 → 不动；本来就关着 → 收掉，别把"文档的纸"留在桌面上）。
        if (!_boardWasOnBeforeDoc)
        {
            if (BoardOn) SetBoardFromUi(false);
            // 别让之后"退出穿透"再把这张纸变回来（它是文档的纸，文档没了）
            _boardBeforePassThrough = false;
        }
        _boardWasOnBeforeDoc = true;

        DocStoreKey = "";

        SetInkStatus($"已关闭文档：{title}");
        Console.WriteLine($"[文档] 已关闭：{title}（批注已存；页位图 / 解码缓存 / PDF 文档都已释放）");
    }
}
