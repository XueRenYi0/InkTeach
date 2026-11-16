// [职责] 文档导入与文档页空间：打开图片 / PDF / PPT、批注读回、关文档回收。
// [测试] --doctest

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
    /// PDF 页的**视口高清补丁**（2026-11-16）：1× 整页底图之上的设备分辨率小图。
    /// 只有 PDF 文档才建（`_pdfDoc != null`）；关文档 / 换文档时必须先 Dispose 它，
    /// 再 Dispose `_pdfDoc`——它的后台线程正拿着 PDFium 文档渲。
    /// </summary>
    internal DocPatchCache DocPatch;

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

    /// <summary>底部页码条现在活跃吗（PPT 放映中 / 打开了文档 / 白板）。条的所有交互（命中/悬停/菜单/面板）都看它。</summary>
    internal bool PageBarActive => PptMode || DocView.IsOpen || BoardOn;

    /// <summary>
    /// 条现在**看得见 / 点得到**吗。与 <see cref="PageBarActive"/> 的差别 = 穿透：
    ///   · 文档 / 白板：穿透 = 全让开（S2，用户 2026-10-07 定）→ 条跟着收 ✗；
    ///   · PPT 模式：穿透时条**保留**（它管的是下层放映的东西，还有用 ✓，有接输入小窗撑着）。
    /// 白板现在有自己的页数，**不再**因为盖住文档就把条收掉。
    /// </summary>
    internal bool PageBarVisible => PageBarActive && (PptMode || !PassThrough);

    /// <summary>条上的"第几页"（1 起）。白板 = 白板自己的屏页；文档 = 视口中心所在页。</summary>
    internal int BarPageNow
    {
        get
        {
            if (BoardOn) return ScreenIndex;
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

    /// <summary>条上的"共几页"。白板 = 白板自己的内容页数。</summary>
    internal int BarTotal => BoardOn ? BoardPageCount
                           : !PptMode && DocView.IsOpen ? DocView.Count
                           : PptTotal;

    /// <summary>翻上/下一页（箭头）。白板 = 白板自己的屏页；文档 = 跳页顶。</summary>
    internal void BarPrev()
    {
        if (BoardOn) FlipPage(false);
        else if (!PptMode && DocView.IsOpen) FlipPage(false);
        else PptPrevFromUi();
    }

    internal void BarNext()
    {
        if (BoardOn) FlipPage(true);
        else if (!PptMode && DocView.IsOpen) FlipPage(true);
        else PptNextFromUi();
    }

    /// <summary>跳到第 <paramref name="page"/> 页（1 起；页号面板用）。文档模式 = 页顶对齐视口顶。</summary>
    internal void BarGoto(int page)
    {
        // 同 FlipPage：文本编辑中跳页，先把文本框提交。
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        if (BoardOn)
        {
            int idx = Math.Clamp(page - 1, 0, Math.Max(0, BoardPageCount - 1));
            float want = ClampOffset(_virtualY - idx * PageHeightCanvas * ViewScaleSafe);
            if (Math.Abs(want - ViewOffsetY) < 1f) return;
            if (!ClientAreaAnimationOn)
            {
                ViewOffsetY = want;
                _camAnimating = false;
                _dirty = true;
            }
            else
            {
                _camFrom = ViewOffsetY;
                _camTo = want;
                _camStartMs = NowMs;
                _camAnimMs = CamAnimMs;
                _camAnimating = true;
                _dirty = true;
            }
            Console.WriteLine($"[白板] 跳到第 {idx + 1} 页");
            return;
        }
        if (!PptMode && DocView.IsOpen)
        {
            DismissDocResume("跳页");
            int idx = Math.Clamp(page - 1, 0, Math.Max(0, DocView.Count - 1));
            float want = ClampOffset(_virtualY - DocView.TopOf(idx) * ViewScaleSafe);
            if (Math.Abs(want - ViewOffsetY) >= 1f)
            {
                DocView.PinPage(idx);
                if (!ClientAreaAnimationOn)
                {
                    ViewOffsetY = want;
                    _camAnimating = false;
                    DocView.UnpinPage();
                    _dirty = true;
                    NoteDocResumeChange();
                }
                else
                {
                    _camFrom = ViewOffsetY; _camTo = want; _camStartMs = NowMs;
                    _camAnimMs = DocMemory.FlipMs;
                    _camAnimating = true;
                    _dirty = true;
                    NoteDocResumeChange();
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
    /// `force=true`：不管自动保存开关都写——「清空当前页」要立刻把剩下的页落盘，
    /// 否则关掉自动保存时，重开又回到清空前。
    /// </summary>
    internal void SaveDocInk(bool force = false)
    {
        if (DocStoreKey.Length == 0) return;
        if (!force && !PptAutoSaveOn) return;
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
    /// 「打开文档…」（更多 → 墨迹）：多选对话框 → 图片铺页 / PDF 渲染 / PPT 直映（调 Office/WPS 放）。
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
        if (err != null) { SetInkStatus("打开文档失败：" + err); return; }
        PrepareAnnotateAfterOpen();
    }

    /// <summary>
    /// **「打开…」**（2026-10-16 合并入口：原「打开墨迹」＋「打开文档」，用户定 7→5）。
    /// 一条"打开"收全——板书 .inkb / 图片 / PDF / PPT，**按扩展名分派**：
    /// 单独一个 .inkb → 替换板书（先备份）；其余 → 铺页 / 放映（图片仍可多选）。
    /// 放映中不响应；结果写进 <see cref="UiState.InkStatus"/>。
    /// </summary>
    internal void OpenBoardAnyFromUi()
    {
        if (PptMode) { SetInkStatus("放映中不打开（先退出放映）"); return; }
        if (!ExportDialogEnabled) { SetInkStatus("自检模式不弹对话框"); return; }

        string dir = GetUiPref("docDir");
        BorrowFocusForDialog();
        ExportDialogOpen = true;
        string[] files;
        try
        {
            files = ExportFileDialog.AskForOpenDocuments(OwnerHwnd(), dir,
                        ExportFileDialog.OpenAnyFilter, "打开（板书 / 图片 / PDF / PPT）");
        }
        catch (Exception ex) { SetInkStatus("弹打开对话框失败：" + ex.Message); return; }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }
        if (files == null || files.Length == 0) { SetInkStatus("已取消"); return; }
        SetUiPref("docDir", System.IO.Path.GetDirectoryName(files[0]) ?? "");
        OpenBoardAnyCore(files);
    }

    /// <summary>「打开…」的分派核心（自检直接调它，绕开对话框）。</summary>
    internal void OpenBoardAnyCore(string[] files)
    {
        if (files == null || files.Length == 0) { SetInkStatus("没有选中文件"); return; }
        // 板书文件 = "替换整块板书"的重动作：只认单独一份（混选里不做，把原因说清）。
        bool anyInk = false;
        foreach (var f in files)
            if (string.Equals(System.IO.Path.GetExtension(f), ".inkb", StringComparison.OrdinalIgnoreCase))
            { anyInk = true; break; }
        if (anyInk && files.Length > 1)
        {
            SetInkStatus("板书文件（.inkb）请单独打开（它会替换整块板书）");
            return;
        }
        if (anyInk) { OpenInkFile(files[0]); return; }
        var err = OpenDocuments(files);
        if (err != null) { SetInkStatus("打开失败：" + err); return; }
        PrepareAnnotateAfterOpen();
    }

    /// <summary>
    /// 「更多 → 最近文档」点某一行：**不弹对话框**，直接按上次那批路径重开。
    /// 文件没了就跳过那一份（全没了只回一句提示，不抛、不关别的东西）。
    /// 返回 true = 已经发起打开（面板可以关掉了）。
    /// </summary>
    internal bool OpenRecentDocumentFromUi(string[] paths)
    {
        if (PptMode) { SetInkStatus("放映中不开文档（先退出放映）"); return false; }
        if (paths == null || paths.Length == 0) return false;

        var live = new List<string>(paths.Length);
        foreach (var p in paths)
            if (!string.IsNullOrWhiteSpace(p) && System.IO.File.Exists(p)) live.Add(p);

        if (live.Count == 0)
        {
            string name = paths.Length > 0 ? System.IO.Path.GetFileName(paths[0]) : "";
            SetInkStatus(name.Length > 0 ? $"文件已经不在了：{name}" : "文件已经不在了");
            Console.WriteLine("[最近文档] 文件已经不在了：" + string.Join("；", paths));
            return false;
        }

        var err = OpenDocuments(live);
        if (err != null) { SetInkStatus("打开失败：" + err); return false; }
        PrepareAnnotateAfterOpen();

        // 这一条里有文件已经没了：实际打开的那几条刚记成新的一条，
        // 把原来那条（含已删文件）删掉——列表里同一份文档不会留两行。
        if (live.Count != paths.Length)
        {
            try
            {
                var list = RecentDocs.Parse(GetUiPref(RecentDocs.PrefKey));
                if (RecentDocs.RemoveSet(list, paths))
                {
                    SetUiPref(RecentDocs.PrefKey, RecentDocs.Format(list));
                    FlushSettings();
                }
            }
            catch (Exception ex) { Console.WriteLine("[最近文档] 清理旧条目失败（不影响打开）：" + ex.Message); }
        }
        return true;
    }

    /// <summary>
    /// 打开成功后记一条「最近文档」（界面「更多 → 最近文档」读它）。
    ///
    /// 只认**文档**（图片 / PDF / PPT）：板书 .inkb 走的是"替换整块板书"，
    /// 不在这个列表里重复出现（它有「历史板书」那条自己的入口）。
    /// </summary>
    private void RecordRecentDoc(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return;
        try
        {
            bool any = false;
            foreach (var p in paths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var ext = System.IO.Path.GetExtension(p);
                if (DocImageSource.IsImage(p) || PptLaunch.IsPpt(p)
                    || string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
                { any = true; break; }
            }
            if (!any) return;

            var list = RecentDocs.Parse(GetUiPref(RecentDocs.PrefKey));
            RecentDocs.Push(list, paths, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            SetUiPref(RecentDocs.PrefKey, RecentDocs.Format(list));
            // 打开文档是低频动作：直接落盘，别等退出时才写（崩溃也不丢）。
            FlushSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[文档] 记「最近文档」失败（不影响打开）：" + ex.Message);
        }
    }

    /// <summary>
    /// 打开文档**成功之后**的"准备批注"组合（2026-10-16 用户提议：
    /// "穿透模式里打开 PDF，屏幕上什么都不显示，这样才知道 PDF 打开了？"）：
    ///   · **在穿透里 → 自动退出穿透**：穿透下页位图是**全放掉**的（省内存）——
    ///     PDF 打开了屏幕上却没有东西、也没法写；退出来页面立刻上屏，就知道"已就绪"；
    ///   · **总是把工具切回画笔、手掌关掉**（2026-11-15 用户定：文档模式像放映——
    ///     "打开就是笔"是这一模式的默认态；不这样的话上次用橡皮，一打开文档就是橡皮，
    ///     看起来像"PDF 不能写字"）。
    /// </summary>
    private void PrepareAnnotateAfterOpen()
    {
        bool wasPass = PassThrough;
        ExitDocPan("打开文档");
        if (wasPass) SetPassThrough(false, restoreBoard: false);   // 画布保持眼前所见（文档），不弹回白板
        SetToolFromUi(Tool.Pen);
        if (wasPass) SetInkStatus(InkStatus + "；已在穿透里自动退出穿透并切回「笔」");
        NotifyUiStateChanged();
    }

    /// <summary>自检用：绕开对话框，直接按扩展名分派。</summary>
    internal void OpenBoardAnyForTest(string[] files) => OpenBoardAnyCore(files);

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
    /// 分工：都是图片 → 一叠图页；就一份 PDF → PDF 文档；就一份 PPT → 直映（调 Office/WPS 放）；混选 → 提示重选。
    /// </summary>
    internal string OpenDocuments(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return "没有选中文件";

        var images = new List<string>();
        var pdfs = new List<string>();
        var ppts = new List<string>();
        foreach (var p in paths)
        {
            if (DocImageSource.IsImage(p)) images.Add(p);
            else if (string.Equals(System.IO.Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase))
                pdfs.Add(p);
            else if (PptLaunch.IsPpt(p)) ppts.Add(p);
        }

        // PPT 走"直映"：不铺页、不进文档层——交给系统里现成的 Office/WPS 放起来
        // （放映检测 / 批注联动由现成的 Ppt.cs 接管，见 LaunchPptShow）。
        if (ppts.Count > 0)
        {
            if (images.Count > 0 || pdfs.Count > 0) return "PPT 和图片/PDF 不能一起选（一次选一种）";
            if (ppts.Count > 1) return $"PPT 一次放一个（这次选了 {ppts.Count} 个）";
            string msg = LaunchPptShow(ppts[0]);
            // 放映喊起来了（含"退回普通打开"的兜底）才算打开成功 → 记「最近文档」。
            if (msg == null) RecordRecentDoc(ppts);
            return msg;
        }

        if (pdfs.Count == 0 && images.Count > 0) return OpenImageDocument(images);
        if (images.Count == 0 && pdfs.Count == 1) return OpenPdfDocument(pdfs[0]);
        if (pdfs.Count > 0) return "PDF 一次打开一份（图片可以多选）";
        return "没有能打开的文件（支持图片、PDF 和 PPT）";
    }

    /// <summary>
    /// 打开一批图片：每张按屏宽铺页（超长图按 1 屏高切片），纵向一叠。
    /// 只有"读尺寸"这一步是现在做的（很快）；页位图到进视口才会生成（惰性）。
    /// </summary>
    internal string OpenImageDocument(IReadOnlyList<string> files)
    {
        var specs = new List<DocPages.Spec>();
        var opened = new List<string>(files.Count);       // 真的读出来的那些（最近文档只记它们）
        int ok = 0, bad = 0;
        int primaryW = Native.GetSystemMetrics(0);       // 页锚在主屏中心 → 按主屏宽铺页
        int primaryH = Native.GetSystemMetrics(1);
        int layoutW = DocMemory.LayoutWidth(primaryW);
        foreach (var f in files)
        {
            if (!DocImageSource.TryReadInfo(f, out int w, out int h)) { bad++; continue; }
            specs.AddRange(DocImageSource.PlanSpecs(f, w, h, layoutW, primaryH));
            opened.Add(f);
            ok++;
        }
        if (specs.Count == 0 || ok == 0) return "这些图片都读不了（格式不支持或文件损坏）";

        DisposeDocPatch();        // 补丁线程先停：它正拿着上一份 PdfiumDoc 渲
        _pdfDoc?.Dispose();       // 换文档：上一份 PDF 放掉（图片文档不用它）
        _pdfDoc = null;
        DiagLog.PdfNote = $"image/{layoutW}px/cache1";

        string title = files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{ok} 张图片";
        StartDocView(specs, title, DocStore.KeyOf(files));
        RecordRecentDoc(opened);           // 最近文档：一组图片算一份（读不了的别记）
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
            int primaryW = Native.GetSystemMetrics(0);       // 页锚在主屏中心 → 按主屏宽渲染
            if (primaryW <= 0) primaryW = _virtualW;
            int layoutW = DocMemory.LayoutWidth(primaryW);
            int maxPx = DocMemory.MaxPagePixelsBase;
            for (int i = 0; i < doc.PageCount; i++)
            {
                var (pw, ph) = doc.Size(i);
                // 显示宽 = 主屏宽（页仍铺满屏）；渲染宽 = 档位上限（省内存靠它，不缩页面）。
                int displayW = Math.Max(1, primaryW);
                int displayH = Math.Max(1, (int)MathF.Round(ph * (displayW / pw)));
                int renderW = Math.Max(1, layoutW);
                int renderH = Math.Max(1, (int)MathF.Round(ph * (renderW / pw)));
                long px = (long)renderW * renderH;
                if (px > maxPx)                                     // 极端长页/超大幅面：按像素预算缩
                {
                    float k = MathF.Sqrt((float)maxPx / px);
                    renderW = Math.Max(1, (int)MathF.Round(renderW * k));
                    renderH = Math.Max(1, (int)MathF.Round(renderH * k));
                    Console.WriteLine($"    [文档] 第 {i + 1} 页很大，渲染已缩到约 {maxPx / 1_000_000}MP");
                }
                specs.Add(new DocPages.Spec
                {
                    Kind = 1,
                    Source = path,
                    SourceIndex = i,
                    SrcX = 0f, SrcY = 0f, SrcW = pw, SrcH = ph,
                    OutW = displayW, OutH = displayH,
                    RenderW = renderW, RenderH = renderH,
                });
            }
            Console.WriteLine($"[文档] PDF 渲染：主屏 {primaryW}px → 显示 {primaryW}px / 位图 ≤{layoutW}px"
                + $"（{DocMemory.Name}档；静止缓存 1 页；翻页 {DocMemory.FlipMs:F0}ms）");
            DiagLog.PdfNote = DocMemory.Note;
        }
        catch (Exception ex)
        {
            doc.Dispose();
            return "读取 PDF 页面尺寸失败：" + ex.Message;
        }

        DisposeDocPatch();           // 补丁线程先停：它正拿着上一份 PdfiumDoc 渲
        _pdfDoc?.Dispose();
        _pdfDoc = doc;

        StartDocView(specs, System.IO.Path.GetFileName(path), DocStore.KeyOf(new[] { path }));
        RecordRecentDoc(new[] { path });   // 最近文档：PDF 一份
        return null;
    }

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
        // 新文档 = 100%：缩放是**这一份文档的临时视图状态**，不跨文档继承
        // （上一份放到 200%，打开下一份还是 200% 会让人以为"文件变大了"）。
        // 必须在 EnterDocPageSpace / ClampViewOffset 之前复位：夹取要按新缩放算。
        DocZoom = 1f;
        ViewScale = 1f;
        ViewOffsetX = 0f;

        DocView.Generator = DocRenderSpec;
        DocView.UseWorker = DocPageWorker;      // 后台渲染（产品默认开；见那行注释）
        DocView.OnResultReady = WakeForDocPage; // 后台渲完一页叫醒主循环（不叫就不上屏）

        // **文档的"纸"**（2026-10-09"两张纸"模型）：文档开着时自动铺一层白底
        //（Overlay.BoardBrush 里按 DocView.IsOpen 现判，不需要开关）——
        //   ① 页缝 / 页边有纸感（不再透出桌面）；② 页不满屏时四周也是纸。
        // ⚠ **和白板无关**：纸不是白板，改板色/关白板都动不了它；关文档它自己收。
        // ⚠ 但"白板"是**另一张纸、盖在文档上面**——打开文档先把白板收掉，
        //    不然新打开的文档会被盖在白板底下看不见（用户要的是"先看见文档"）。
        if (BoardOn) SetBoardFromUi(false);

        // 先进页空间（切槽 + 相机就位），再排页——页的锚点是固定的，不依赖当时视口。
        DocStoreKey = storeKey;
        EnterDocPageSpace(storeKey);
        LoadDocZoomBarPos();              // 缩放条位置：这份/上次的文档模式记下的位置

        float gap = 24f * DpiScale;                       // 页缝 = 24 逻辑像素（和白板页界线同语言）
        DocView.Open(specs, title, PrimaryScreenCenterX(), 0f, gap);
        DocView.SetViewScale(1f);

        // 高清补丁：**只有 PDF**（图片页源像素就那么多，放大重渲只是白花内存）。
        // 它在后台按"可见区 + 256 设备像素"渲一块设备分辨率小图，底图永远 1×。
        DisposeDocPatch();
        if (_pdfDoc != null)
        {
            DocPatch = new DocPatchCache(_pdfDoc,
                i => DocView.At(i)?.Rect,
                () => DocView.Count,
                WakeForDocPage);
            DocPatch.SetScale(ViewScale);
        }

        LoadDocInk(storeKey);
        ClampViewOffset();
        LoadDocResumePrompt(storeKey);   // 只在跨会话、且位置不是"第 1 页页顶"时弹卡（不自动跳）
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
            1 => _pdfDoc?.RenderPage(spec.SourceIndex, spec.BitmapW, spec.BitmapH),
            _ => null,
        };

    /// <summary>
    /// 停掉高清补丁线程（并放掉补丁位图）。**必须在 `_pdfDoc.Dispose()` 之前调**：
    /// 补丁 worker 正拿着 <see cref="PdfiumDoc"/> 在 PDFium 里渲，反过来的话原生代码会踩到已释放的文档。
    /// </summary>
    private void DisposeDocPatch()
    {
        DocPatch?.Dispose();
        DocPatch = null;
    }

    /// <summary>
    /// 关掉文档：**先存批注**（自动保存开着才写）→ 释放页位图 / 解码缓存 / PDF 文档
    /// （"用完释放"，10-03 文档 7.4）→ 切回桌面页空间（相机回到老师原来的位置）。
    /// </summary>
    internal void CloseDocument()
    {
        if (!DocView.IsOpen && _pdfDoc == null) return;
        // 退出文档也先提交文本框：关掉页图/切回桌面后，编辑宿主不能还挂着。
        if (_textBoxEditing) EndTextBoxEditFromUi(true);
        // 手掌是"文档模式"的一部分：关文档就关掉（退出文档 = 回到桌面 + 那格恢复成穿透箭头）。
        ExitDocPan("关文档");
        PptZoomMenuOpen = false;            // 缩放浮层同理：文档都没了，别留一个孤儿浮层吃点击
        CloseDocZoomBarUi();                // 缩放条的按下 / 拖动状态一起收（无孤儿状态）
        string title = DocView.Title;

        // 白板是独立槽：关文档前先退出白板，回到文档页，避免出现
        // “BoardOn 还是 true、但 Doc.PageKey 已经不是 BoardPageKey”的混合状态。
        if (BoardOn) SetBoardFromUi(false);

        SaveDocInk();                       // 关闭时兜底存一次（节流之外的那一下）
        SaveDocResumeOnClose();             // 阅读位置也兜底存一次（卡片还开着时保留旧位置）
        DismissDocResume("关文档");         // 卡不能跟到桌面上去
        ClearBoardFreeze();                 // 文档都关了，白板下面没有页可冻结

        DocView.Close();
        DocImageSource.TrimCache();
        DisposeDocPatch();                  // 补丁线程先停，PDF 文档随后才安全释放
        _pdfDoc?.Dispose();
        _pdfDoc = null;
        DiagLog.PdfNote = "";

        // 缩放在文档关闭时复位（相机先回 100%，再回桌面槽——桌面槽的偏移是
        // 按 1:1 记的，带着 200% 的 ViewOffsetY 回去会落到别的屏上）。
        ViewScale = 1f;
        DocZoom = 1f;
        ViewOffsetX = 0f;
        DocView.SetViewScale(1f);

        LeaveDocPageSpace();                // 回桌面（含相机位置恢复）

        // **纸随文档一起收**：文档的"纸"是按 DocView.IsOpen 现判的，关文档自然就收了。
        // 白板是**另一张纸**：文档的开关不动它——打开时替你把它收过一次（好让你看见文档），
        // 这里不回补（想要白板再点一下那一格就行）。
        DocStoreKey = "";

        SetInkStatus($"已关闭文档：{title}");
        Console.WriteLine($"[文档] 已关闭：{title}（批注已存；页位图 / 解码缓存 / PDF 文档都已释放）");
    }

}
