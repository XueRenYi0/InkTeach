using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;


public partial class InkEngine
{
    /// <summary>把相机滚到"这个对象看得见"。只做纵向——横向没有滚动这回事。</summary>
    private void EnsureVisible(Stroke s)
    {
        if (s == null) return;
        var vp = ViewportCanvas;
        var b = s.PaddedBounds;
        if (b.MaxY > vp.MaxY) ViewOffsetY += b.MaxY - vp.MaxY;
        else if (b.MinY < vp.MinY) ViewOffsetY += b.MinY - vp.MinY;
        ClampViewOffset();
    }

    /// <summary>上一次「复制」的整块范围：判断原件还在不在当前这一屏。</summary>
    private RectF? _lastCopyBox;

    /// <summary>上一次「复制」写进剪贴板的原始字节：粘贴时比对，确认中间没被别的程序换过。</summary>
    private byte[] _lastCopiedBytes;
    private int _pasteStep;

    // ---- 反馈群二维码（2026-11-03）-----------------------------------------
    // 图在 assets/qq-group.jpg，跟着输出走；点「二维码」时整屏展示，点一下 / Esc 关闭。
    // 只读一次，失败也不重试（缺图是发版问题，不是运行时问题）。
    internal ImageData FeedbackQrImage;
    internal bool FeedbackQrVisible;
    private bool _feedbackQrTried;

    /// <summary>显示 / 隐藏反馈群二维码。</summary>
    internal void ToggleFeedbackQr()
    {
        if (FeedbackQrVisible) { HideFeedbackQr(); return; }

        if (!_feedbackQrTried)
        {
            _feedbackQrTried = true;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "assets", "qq-group-qr.png");
                var r = ImageFile.Load(path);
                if (r.Success)
                    FeedbackQrImage = ImageData.AdoptPremultiplied(r.Width, r.Height, r.Bgra);
                else
                    Console.WriteLine("二维码：读不出来 —— " + r.Error + $"（{path}）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("二维码：读取失败 " + ex.Message);
            }
        }

        if (FeedbackQrImage == null)
        {
            SetInkStatus("二维码图缺失：assets/qq-group.jpg");
            return;
        }

        FeedbackQrVisible = true;
        _dirty = true;
        RequestFullFrames();
        SetInkStatus("扫一扫加群；点一下关闭");
        Console.WriteLine("二维码：整屏展示");
    }

    internal void HideFeedbackQr()
    {
        if (!FeedbackQrVisible) return;
        FeedbackQrVisible = false;
        _dirty = true;
        RequestFullFrames();
        Console.WriteLine("二维码：关闭");
    }

    /// <summary>整屏重画两帧：二维码是覆盖层，出现/消失时脏区不能只算局部。</summary>
    private void RequestFullFrames()
    {
        foreach (var w in _windows) w.RequestFullFrames();
    }

    /// <summary>
    /// 复制选中对象到剪贴板：**对象字节 + 一张图**。
    ///
    /// 对象字节让"粘回来仍是可编辑对象"（跨页、跨窗口、跨程序实例都行）；
    /// 图是给外部程序的兜底（Word / PPT / 微信粘到的是图，至少能用）。
    /// 只放对象不放图也行，但那样"复制一段板书贴到微信"就没反应——两头都要顾。
    /// </summary>
    internal bool CopySelectionToClipboard()
    {
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("复制：没有选中任何东西（先用框选或点选选中）");
            return false;
        }

        byte[] objects = ClipboardInk.Serialize(sel);
        _lastCopiedBytes = objects;

        // 同时渲染一张图：范围 = 这批对象的画布范围 + 4 逻辑像素留白
        byte[] dib = null; int w = 0, h = 0;
        var box = EditRegion.Of(sel);
        if (!box.IsEmpty)
        {
            // 记住"原件在哪 + 多大"：粘贴时判断它还在不在当前屏，并落在旁边 24px
            _lastCopyBox = box;
            _pasteStep = 0;
        }
        if (!box.IsEmpty && _windows.Count > 0)
        {
            box = box.Inflate(4f * DpiScale);
            w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            // 防呆：图太大就不放了（对象格式照放）
            if ((long)w * h <= 32_000_000) dib = _windows[0].RenderStrokesToBgra(sel, box, w, h);
            else { w = h = 0; }
        }

        bool ok = ClipboardInk.Set(objects, dib, w, h);
        Console.WriteLine(ok
            ? $"复制 {sel.Count} 个对象（同时放了一张 {w}×{h} 的图：外部程序也粘得上）"
            : "复制失败（剪贴板正被别的程序占着？）");

        // 屏幕上的反馈只有**选区闪一下**（用户反馈的"复制以后看不出来"）。
        // 提示条 2026-09-16 按用户要求去掉了：屏幕上一个字都不留。
        if (ok) SelFlashUntilMs = NowMs + SelFlashMs;
        return ok;
    }

    /// <summary>
    /// 操作条「复制一份」：**原件原位保留，副本错位 24px 并选中**。
    ///
    /// 2026-11-03（用户）：**不再顺手写系统剪贴板**——"复制到剪贴板"是「选择」上带
    /// 右端那颗「复制」的事；条上这颗只管再制，避免只想复制图形却把剪贴板冲掉。
    /// </summary>
    internal bool CopySelectionAndDuplicate()
    {
        int n = Doc.Selected.Count;
        if (n == 0)
        {
            SetInkStatus("复制：先选中要复制的对象");
            return false;
        }

        bool duplicated = Doc.DuplicateSelected();
        SelFlashUntilMs = NowMs + SelFlashMs;
        if (duplicated)
        {
            SetInkStatus(Loc.T("已复制 {0} 个对象：原件保留，新副本已选中", n));
            return true;
        }

        string why = string.IsNullOrEmpty(Doc.LastRejectReason) ? "副本创建失败" : Doc.LastRejectReason;
        SetInkStatus(Loc.T("复制失败：{0}", why));
        return false;
    }

    // ---- 导出（选中 → 透明底 PNG）------------------------------------------
    //
    // 用户 2026-09-17 定：**只导选中的**、**透明底 PNG**、**弹系统"另存为"**，
    // 而且**同时放进剪贴板**（两样都要）。落点与命名按
    // [调研-选中框-反馈-导出-层级-属性.md] 第二节。
    //
    // 屏幕上**不留字**（用户 2026-09-16 明确要求把提示条去掉），只让选区闪一下——
    // 文件对话框本身就是最清楚的反馈。

    /// <summary>
    /// 把选中的内容离屏渲染成一张**透明底** BGRA（和"复制到剪贴板"那条路同一个渲染器）。
    /// 返回 null = 不能导（没选中 / 太大 / 渲染失败），每种情况都有日志。
    /// </summary>
    private byte[] RenderSelectionForExport(out int w, out int h)
    {
        w = h = 0;
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("导出：没有选中任何东西（先用框选或点选选中）");
            return null;
        }
        var box = EditRegion.Of(sel);
        if (box.IsEmpty) return null;
        box = box.Inflate(4f * DpiScale);                     // 和"复制"同一个留白
        w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 32_000_000)
        {
            Console.WriteLine($"导出：选中的内容太大（{w}×{h}）");
            w = h = 0;
            return null;
        }
        if (_windows.Count == 0) { w = h = 0; return null; }

        var bgra = _windows[0].RenderStrokesToBgra(sel, box, w, h);
        if (bgra == null) { Console.WriteLine("导出：离屏渲染失败"); w = h = 0; }
        return bgra;
    }

    /// <summary>
    /// 写 PNG ＋ **同时放进剪贴板**（对象 ＋ 这张图，和 Ctrl+C 同一个剪贴板合同），
    /// 再让选区闪一下。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path)
        => WriteExport(bgra, w, h, path, 1);

    /// <summary>
    /// 写盘。<paramref name="filterIndex"/> = 老师在类型栏选的那一条（决定"透明底 / 白底"），
    /// 编码器按扩展名决定——两者的分工见 `ExportFormats.Encode` 的注释。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path, int filterIndex)
    {
        LastExportPath = null;
        LastExportFilterIndex = 0;
        try
        {
            var bytes = ExportFormats.Encode(path, bgra, w, h, filterIndex, out string tag);
            if (bytes == null) { Console.WriteLine($"导出：{tag} 编码失败"); return false; }
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"导出成功：{path}（{w}×{h}，{tag}，{bytes.Length / 1024.0:F0} KB）");
            LastExportPath = Path.GetFullPath(path);
            LastExportFilterIndex = filterIndex;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"导出失败：{ex.Message}");
            return false;
        }

        // 用户 2026-09-17 更正：**导出不再顺手放进剪贴板**——"复制"那条路已经有了
        // （选中 → Ctrl+C / 操作条上的复制），导出就只管落盘，职责单一、也少一次
        // 剪贴板写入（剪贴板是全局资源，能少碰就少碰）。
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **导出按钮做的事**：离屏渲染 → 弹"另存为" → 写盘。取消 = 什么都不做。
    ///
    /// 格式**就在那个对话框里选**（见 <see cref="ExportFormats"/>：透明底 / 白底 /
    /// 文件大小 / 老软件兼容四种，每种把优势写在类型名里）。我们这边**不再先弹一层
    /// 自己的格式面板**——用户 2026-09-17："那下面这两个图标就没有用了吧，用户都可以
    /// 自己保存图片了"：说得对，两处选格式反而绕，点导出直接出对话框还少一步。
    /// （触摸屏上那个下拉现在也点得开了：真因是覆盖层每秒抢层，见 ReassertTopmost。）
    /// </summary>
    /// <param name="defaultFilterIndex">
    /// 默认选中第几条（1 起）。界面把"上次用的那条"记在偏好里传进来，
    /// 所以常用 JPG 的老师下次不用再选。
    /// </param>
    /// <returns>用户最终选的那一条（1 起）；取消或没导成返回 0。</returns>
    internal int ExportSelection(int defaultFilterIndex = 1)
    {
        ExportAttempts++;
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return 0;

        // 自检模式**绝不弹对话框**：系统对话框会阻塞在那里等消息，
        // 而自检是靠"抽消息 + 渲染"自己推进的——一弹就把整条自检卡死到超时。
        // （自检要验的是"渲染 → 编码 → 写盘 → 剪贴板"这条链，那条由 --iotest 走
        //  ExportSelectionToPathForTest，不经过这里。）
        if (!ExportDialogEnabled)
        {
            Console.WriteLine("导出：自检模式不弹另存为对话框（见 --iotest）");
            return 0;
        }

        // 默认扩展名跟着选的那一条走（第 1 条 PNG 透明底是老师最常要的：
        // "贴到别处不带走白底"）。
        int want = Math.Clamp(defaultFilterIndex, 1, ExportFormats.Count);
        string suggested = $"选中-{DateTime.Now:yyyyMMdd-HHmm}" + ExportFormats.ExtensionFor(want);
        string path = null;
        int chosen = want;
        BorrowFocusForDialog();                 // 覆盖层平时不抢焦点，弹框前临时放开
        ExportDialogOpen = true;                // 弹框期间不许再抬覆盖层，见 ReassertTopmost
        try { path = ExportFileDialog.AskForImage(OwnerHwnd(), suggested, want, "导出选中的内容", out chosen); }
        catch (Exception ex)
        {
            // **弹框这一步出错绝不允许打死软件**。真踩过：.NET 7 起结构体字段不能用
            // StringBuilder，那一版一点"导出"整个进程就没了（异常从 P/Invoke 冒到 Main）。
            // 现在最坏也只是"这次导不出去"，并且把原因说清楚。
            Console.WriteLine($"导出：弹另存为失败（{ex.GetType().Name}: {ex.Message}）");
            return 0;
        }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }

        if (path == null) { Console.WriteLine("导出：取消"); return 0; }
        return WriteExport(bgra, w, h, path, chosen) ? chosen : 0;
    }

    /// <summary>
    /// 导出按钮真正调的那一层：**记住老师上次选的那一条格式**（`ui.exportFormat`），
    /// 下次直接默认它——常用 JPG 的老师不用每次都去下拉里找。
    ///
    /// 这一条偏好由**引擎自己解释**（别的 `ui.*` 都是界面说了算）。理由：操作条
    /// 是引擎那边画和命中测试的（`Selection.cs`），导出按钮压根不经过 `InkUi`，
    /// 所以"记住上次的选择"只能落在这里。键名照旧放在 `ui` 段，不另开一节。
    ///
    /// 注意：自检模式下这条偏好**读不到也写不进**（自检不碰用户配置），
    /// 所以自检里走的是默认第 1 条，判据是确定的。
    /// </summary>
    private void ExportSelectionPref()
    {
        int last = ExportDefaultFilterIndex();
        int chosen = ExportSelection(last);
        if (chosen >= 1) SetUiPref("exportFormat", chosen.ToString());
    }

    /// <summary>
    /// 默认选中第几条格式：读偏好 `ui.exportFormat`，读不出来/越界就回到第 1 条（PNG 透明底）。
    /// 单独抽出来是为了能自检（"记住上次的选择"这条逻辑不依赖对话框）。
    /// </summary>
    internal int ExportDefaultFilterIndex()
        => int.TryParse(GetUiPref("exportFormat"), out int v)
           && v >= 1 && v <= ExportFormats.Count ? v : 1;

    /// <summary>自检用：不弹对话框，直接写到指定路径（其余流程一模一样）。</summary>
    internal bool ExportSelectionToPathForTest(string path, int filterIndex = 1)
    {
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return false;
        return WriteExport(bgra, w, h, path, filterIndex);
    }

    private IntPtr OwnerHwnd() => _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

    /// <summary>
    /// 导出时要不要弹系统的"另存为"。**自检模式必须关掉**（见 <see cref="ExportSelection"/>）：
    /// 系统对话框会阻塞等消息，而自检是自己抽消息推进的，一弹就卡到超时。
    /// 产品里保持 true。
    /// </summary>
    internal bool ExportDialogEnabled = true;

    /// <summary>
    /// "系统对话框正开着"。**只用来按住 <see cref="ReassertTopmost"/>**：
    /// 那一下每秒一次的置顶重抬会去跟对话框抢层，顺手把它的下拉列表关掉
    /// （用户报的"选不到 jpg"就是它）。见 <see cref="ReassertTopmost"/> 的注释。
    /// </summary>
    internal bool ExportDialogOpen;

    /// <summary>
    /// 上一次**真的写出去**的那个文件（全路径；没成功过就是 null），以及它是不是 JPEG。
    ///
    /// `--dialogprobe --save` 靠它确认"对话框 → 路径 → 编码 → 落盘"整条链真的通了，
    /// 而不是只看对话框弹没弹出来。
    /// </summary>
    internal string LastExportPath;
    /// <summary>上一次成功导出用的那一条格式（1 起，见 <see cref="ExportFormats"/>）。</summary>
    internal int LastExportFilterIndex;

    /// <summary>
    /// 试过几次导出（**包括没有选中、被自检挡下、用户取消**）。
    ///
    /// 自检要的"点一下"和"真的弹框"分开：`--edittest` 在自检模式下点导出，
    /// 对话框是关着的，只能靠这个计数器证明那一下真的走到了导出这条路
    /// （而不是"什么都没发生"）。
    /// </summary>
    internal int ExportAttempts;

    // 弹系统对话框期间借一下焦点：把 WS_EX_NOACTIVATE 摘掉、弹完装回去。
    // 和"批注键盘模式"同一个手法（那边是长期摘掉，这里是临时的）。
    private bool _focusBorrowed;

    private void BorrowFocusForDialog()
    {
        if (_focusBorrowed) return;
        _focusBorrowed = true;
        // **同时把"置顶"摘掉**：我们的覆盖层是 WS_EX_TOPMOST + 铺满整屏，
        // 而系统对话框自己的弹层（**文件类型那个下拉列表**）是普通弹窗——
        // 它会被置顶的覆盖层盖住，用户看到的就是"点开一下就收回去、选不到 JPG"
        // （用户 2026-09-17 报的）。弹框期间我们不需要浮在最上面，摘掉就好。
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
            // ⚠ **不要 SWP_FRAMECHANGED**：覆盖层是 DComp 合成的全屏层，强制重算框架
            // 会在换样式那一帧闪一下白（2026-10-05 用户报"保存图片先闪白屏"的来源之一）。
            // WS_EX_NOACTIVATE 属激活类样式，去掉它不需要 FRAMECHANGED。
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            Native.SetWindowPos(w.Hwnd, new IntPtr(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        if (_windows.Count > 0) Native.SetForegroundWindow(_windows[0].Hwnd);
    }

    private void ReturnFocusAfterDialog()
    {
        if (!_focusBorrowed) return;
        _focusBorrowed = false;
        if (KeyboardMode) return;               // 键盘模式本来就要求能激活，别把它的样式改回去
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            // 同 BorrowFocusForDialog：这里也不带 SWP_FRAMECHANGED（闪白来源）。
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            // 把"置顶"装回去（覆盖层平时必须浮在所有程序上面）
            Native.SetWindowPos(w.Hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 粘贴：**优先粘回可编辑对象**——剪贴板里有我们的对象格式就还原成对象，
    /// 没有就退回"当图片粘贴"（原有行为）。有原件位置就落在它旁边 24px，
    /// 否则落在视口左上角；粘完自动选中，直接能拖走。
    /// </summary>
    internal bool PasteFromClipboard()
    {
        ExitReplayForEdit("粘贴");
        if (ClipboardInk.TryGetObjectsWithRaw(out var objs, out var rawBytes) && objs.Count > 0)
        {
            var vp = ViewportCanvas;
            float m = CaptureMarginLogical * DpiScale;
            var box = EditRegion.Of(objs);
            float bw = box.MaxX - box.MinX, bh = box.MaxY - box.MinY;
            // 落点（2026-11-03）：
            //   · 原件**还在当前这一屏** → 挨着它放 24px（和 Ctrl+D 同一个手感）；
            //   · 翻了页 / 滚远了（原件不在眼前）→ 落在**当前屏中央**，不把人拽回去。
            //     这条照 Figma：视图离原件太远时，粘到当前视图中间。
            //   · 剪贴板不是我们刚复制的那批（另一个实例 / 外部程序）→ 老规矩，视口左上角。
            // 只认"剪贴板里还是我们刚复制的那批"（比字节）：中间被别的程序换过就不算。
            bool sameAsCopied = rawBytes != null && _lastCopiedBytes != null
                                && rawBytes.AsSpan().SequenceEqual(_lastCopiedBytes);
            bool sourceVisible = sameAsCopied && _lastCopyBox is RectF src
                                 && src.MaxY > vp.MinY + m && src.MinY < vp.MaxY - m
                                 && src.MaxX > vp.MinX + m && src.MinX < vp.MaxX - m;

            float tx, ty;
            if (sameAsCopied)
            {
                _pasteStep++;
                if (sourceVisible && _lastCopyBox is RectF srcBox)
                {
                    tx = srcBox.MinX + 24f * _pasteStep;
                    ty = srcBox.MinY + 24f * _pasteStep;
                }
                else
                {
                    tx = vp.MinX + (vp.MaxX - vp.MinX - bw) * 0.5f + 24f * (_pasteStep - 1);
                    ty = vp.MinY + (vp.MaxY - vp.MinY - bh) * 0.5f + 24f * (_pasteStep - 1);
                }
            }
            else
            {
                tx = vp.MinX + m;
                ty = vp.MinY + m;
            }

            // 夹进当前屏：连续粘贴、原件贴边、或图形比屏还大时都不会跑出去
            float maxX = MathF.Max(vp.MinX + m, vp.MaxX - m - bw);
            float maxY = MathF.Max(vp.MinY + m, vp.MaxY - m - bh);
            tx = Math.Clamp(tx, vp.MinX + m, maxX);
            ty = Math.Clamp(ty, vp.MinY + m, maxY);

            var move = Matrix3x2.CreateTranslation(tx - box.MinX, ty - box.MinY);
            foreach (var s in objs) s.Transform = s.Transform * move;   // 插入前平移（画布坐标）

            Doc.AddStrokes(objs);          // 一步撤销
            Doc.SelectOnly(objs);
            Tool = Tool.Marquee;
            NotifyUiStateChanged();
            _dirty = true;
            SetInkStatus(Loc.T("已粘贴 {0} 个对象（仍是可编辑对象）", objs.Count));
            Console.WriteLine($"粘贴 {objs.Count} 个对象（仍是可编辑对象）");
            return true;
        }
        return PasteImageFromClipboard();
    }

    internal bool PasteImageFromClipboard()
    {
        if (!ClipboardImage.TryGetImage(out var pixels, out int w, out int h, out bool hasAlpha))
        {
            Console.WriteLine("粘贴：剪贴板里没有可用的图像（只认 CF_DIB / CF_DIBV5）");
            return false;
        }
        var img = ImageData.Adopt(w, h, pixels, hasAlpha);
        if (img == null) return false;

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);
        EnsureVisible(placed);
        Doc.SelectOnly(new[] { placed });
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
        _dirty = true;
        Console.WriteLine($"粘贴图片 {w}×{h}");
        return true;
    }

}
