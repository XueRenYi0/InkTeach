using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 文档页底层：图片 / PDF 导入之后，"一叠页"在画布上的那一层。
///
/// ## 它不是什么（这是 2026-10-03 调研里三个坑的直接对策）
/// **不是笔迹**——`Doc.Strokes` 里没有它的任何东西：
///   · 不污染存档（`.inkb` 不会把整页原图裸写进去，一页就是几十 MB）；
///   · 不进回放、不进导出、不进撤销栈（撤销栈里躺着整页位图＝一步撤销撑爆内存）；
///   · 橡皮 / 选中 / 清空都不认识它。墨迹照旧画在它上面，两者互不相识。
///
/// ## 它是什么
/// 一份"配方表"（<see cref="Spec"/>：每页怎么生成）＋ 已生成的位图 ＋
/// 一个**视口窗口**：视口附近的页留着、滚远的立即放掉——和分块缓存同一个思路。
/// 全量渲染整本 PDF 是 GB 级的账（10-03 文档 7.2/8.4 算过），窗口是这件事成立的前提。
///
/// ## 生成是惰性的
/// 打开文档只排配方、**一页都不生成**；生成发生在渲染或空闲预取时
/// （<see cref="SyncWindow"/>），每拍最多 <c>maxGenerate</c> 页——
/// 调用方用它卡"一帧最多一页"的节奏（笔优先，10-03 文档 7.5）。
///
/// ## 坐标系
/// 页矩形就是**画布坐标**（物理像素，和笔迹同一套），相机不动它——
/// 滚动时"页跟着走"是相机的事，这一层只回答"这一块画布上有没有页、画哪张位图"。
/// </summary>
internal sealed class DocPages
{
    /// <summary>
    /// 一页的生成配方（由导入方给；本类**不解释**它的具体含义，只转交给 <see cref="Generator"/>）。
    /// 图片和 PDF 的区别全在 Generator 里，窗口/回收逻辑两边共用。
    /// </summary>
    public sealed class Spec
    {
        /// <summary>来源类别：0 = 图片切片，1 = PDF 页。只用于诊断与自检。</summary>
        public int Kind;
        /// <summary>来源文件（完整路径）。</summary>
        public string Source = "";
        /// <summary>图片 = 切片序号；PDF = 页号（0 起）。</summary>
        public int SourceIndex;
        /// <summary>图片：要裁切的**源矩形**（原始像素）；PDF：不用。</summary>
        public float SrcX, SrcY, SrcW, SrcH;
        /// <summary>输出位图尺寸（物理像素）。</summary>
        public int OutW, OutH;

        public override string ToString() =>
            $"{(Kind == 0 ? "图片" : "PDF")} {System.IO.Path.GetFileName(Source)}#{SourceIndex} {OutW}x{OutH}";
    }

    public sealed class Page
    {
        public int Index;               // 0 起
        public Spec Spec;
        public RectF Rect;              // 画布矩形（物理像素）
        public ImageData Image;         // null = 还没生成（或已回收）
        public bool Failed;             // 生成失败过：别每帧重试（规矩三：失败当没有）
        public override string ToString() => $"第 {Index + 1} 页 {Rect.MinX:F0},{Rect.MinY:F0} {Spec}";
    }

    private readonly List<Page> _pages = new();

    /// <summary>
    /// 页位图生成器（宿主注入：图片 = GDI+ 解码＋裁切＋缩放；PDF = PDFium 渲进缓冲）。
    /// 返回 BGRA（OutW×OutH×4，长度不够算失败）；失败返回 null。
    /// </summary>
    public Func<Spec, byte[]> Generator;

    /// <summary>显示名（文件名；状态行与自检用）。</summary>
    public string Title = "";

    /// <summary>页缝（画布像素）。24 逻辑像素 × DPI——和白板页界线同一套语言。</summary>
    public float Gap;

    /// <summary>内容版本：页生成 / 回收 / 失败 / 换文档都 +1。渲染层据此决定要不要重铺分块。</summary>
    public int Version { get; private set; }

    /// <summary>"整层变了"（打开 / 关闭）：渲染层看到它就整层作废（MarkAllDirty）。</summary>
    public bool FullDirty;

    /// <summary>变了哪几块（画布矩形）。渲染层照单 MarkDirty——滚动中单页生成就靠它。</summary>
    internal readonly List<RectF> DirtyRects = new();

    public bool IsOpen => _pages.Count > 0;
    public int Count => _pages.Count;
    public Page At(int i) => (uint)i < (uint)_pages.Count ? _pages[i] : null;

    // ---- 诊断计数（自检判据；HUD 的补充口径）--------------------------------
    /// <summary>已驻留（生成好、还没回收）的页数。</summary>
    public int ResidentPages { get; private set; }
    /// <summary>已驻留位图的字节数（= 页数 × 面积 × 4）。</summary>
    public long ResidentBytes { get; private set; }
    /// <summary>至今生成过多少页（含回收后又重生的）——滚动的代价一眼可见。</summary>
    public int GenerateCount { get; private set; }
    public int ReleaseCount { get; private set; }
    public int FailCount { get; private set; }

    // ======================================================================
    //  打开 / 关闭
    // ======================================================================

    /// <summary>
    /// 排一叠页。布局规则：**所有页以 <paramref name="centerX"/> 水平居中**、
    /// 纵向挨着铺（页缝 <paramref name="gap"/>），第一页顶边 = <paramref name="anchorTop"/>。
    /// 只排配方、不生成位图（惰性）。
    /// </summary>
    public void Open(List<Spec> specs, string title, float centerX, float anchorTop, float gap)
    {
        Close();
        Title = title ?? "";
        Gap = MathF.Max(0f, gap);
        float y = anchorTop;
        if (specs != null)
        {
            foreach (var sp in specs)
            {
                if (sp == null || sp.OutW <= 0 || sp.OutH <= 0) continue;
                float x = centerX - sp.OutW * 0.5f;
                _pages.Add(new Page
                {
                    Index = _pages.Count,
                    Spec = sp,
                    Rect = new RectF { MinX = x, MinY = y, MaxX = x + sp.OutW, MaxY = y + sp.OutH },
                });
                y += sp.OutH + Gap;
            }
        }
        FullDirty = true;
        Version++;
    }

    /// <summary>关掉文档：位图全放、配方清空。墨迹**不动**（那是 `Doc` 的事）。</summary>
    public void Close()
    {
        foreach (var p in _pages)
        {
            if (p.Image != null) { p.Image.Release(); p.Image = null; }
        }
        _pages.Clear();
        DirtyRects.Clear();
        ResidentPages = 0;
        ResidentBytes = 0;
        Title = "";
        FullDirty = true;
        Version++;
    }

    // ======================================================================
    //  视口窗口：生成与回收
    // ======================================================================

    private long _frame;

    /// <summary>
    /// 视口窗口同步（每帧渲染前调一次，或空闲预取时调）：
    ///   · 窗口 = 视口上下各外扩 <paramref name="marginScreens"/> 屏；
    ///   · 窗口内的页：没生成的**生成**（每拍最多 <paramref name="maxGenerate"/> 页）；
    ///   · 窗口外的页：**立即回收**（GPU＋CPU 一起放）。
    /// 返回这一拍真的生成了几页。
    /// </summary>
    public int SyncWindow(float viewTop, float viewBottom, int maxGenerate = 1, float marginScreens = 1f)
    {
        if (_pages.Count == 0) return 0;
        _frame++;
        float h = MathF.Max(1f, viewBottom - viewTop);
        float m = h * MathF.Max(0f, marginScreens);
        float top = viewTop - m, bottom = viewBottom + m;

        // 页顶边有序 ⇒ 页底边也有序（下一块的顶边 = 上一块的底边 + 缝），
        // 所以"第一块可能相交的"[1] 可以二分：第一个底边 >= top 的页。
        int lo = 0, hi = _pages.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_pages[mid].Rect.MaxY < top) lo = mid + 1; else hi = mid;
        }

        // ① 窗口上方：全放
        for (int k = 0; k < lo; k++) ReleasePage(_pages[k]);

        // ② 窗口内：生成（预算内）
        int made = 0;
        int k2 = lo;
        for (; k2 < _pages.Count && _pages[k2].Rect.MinY <= bottom; k2++)
        {
            var p = _pages[k2];
            if (p.Image != null || p.Failed) continue;
            if (made >= maxGenerate || Generator == null) continue;
            byte[] bgra;
            try { bgra = Generator(p.Spec); }
            catch (Exception ex)
            {
                Console.WriteLine($"    [文档] 生成失败（{p.Spec}）：{ex.Message}");
                bgra = null;
            }
            if (bgra == null || bgra.Length < (long)p.Spec.OutW * p.Spec.OutH * 4)
            {
                p.Failed = true;
                FailCount++;
                DirtyRects.Add(p.Rect);
                Version++;
                continue;
            }
            p.Image = ImageData.Adopt(p.Spec.OutW, p.Spec.OutH, bgra, false);
            if (p.Image == null) { p.Failed = true; FailCount++; DirtyRects.Add(p.Rect); Version++; continue; }
            GenerateCount++;
            made++;
            ResidentPages++;
            ResidentBytes += (long)p.Spec.OutW * p.Spec.OutH * 4;
            DirtyRects.Add(p.Rect);
            Version++;
        }

        // ③ 窗口下方：全放
        for (; k2 < _pages.Count; k2++) ReleasePage(_pages[k2]);

        return made;
    }

    private void ReleasePage(Page p)
    {
        if (p.Image == null) return;
        p.Image.Release();
        p.Image = null;
        ResidentPages = Math.Max(0, ResidentPages - 1);
        ResidentBytes = Math.Max(0, ResidentBytes - (long)p.Spec.OutW * p.Spec.OutH * 4);
        ReleaseCount++;
        // **不标脏**：页被放掉之后重生成出来的是同一份像素（同源、确定性），
        // 屏幕上贴着的是旧块，内容没有区别；等块自己过期时才会重光栅。
    }

    // ======================================================================
    //  查询（翻页 / 相机范围 / 渲染）
    // ======================================================================

    /// <summary>画布 y 落在第几页（0 起；-1 = 没落在任何页上，例如页缝里）。二分。</summary>
    public int IndexAt(float canvasY)
    {
        int lo = 0, hi = _pages.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_pages[mid].Rect.MaxY < canvasY) lo = mid + 1; else hi = mid;
        }
        if (lo < _pages.Count && _pages[lo].Rect.MinY <= canvasY) return lo;
        return -1;
    }

    /// <summary>第 i 页的顶边画布 y（翻页"跳到下一页页顶"用）。越界夹回端点。</summary>
    public float TopOf(int i)
    {
        if (_pages.Count == 0) return 0f;
        if (i < 0) i = 0;
        if (i >= _pages.Count) i = _pages.Count - 1;
        return _pages[i].Rect.MinY;
    }

    /// <summary>视口中央现在在第几页（0 起；-1 = 缝里/没页）。状态行与翻页的"现在在哪"。</summary>
    public int CurrentIndex(float viewTop, float viewBottom)
        => IndexAt((viewTop + viewBottom) * 0.5f);

    /// <summary>整叠页的画布范围（并进 <c>CanvasExtent</c>，否则相机会被夹住滚不到最后一页）。</summary>
    public RectF Extent()
    {
        if (_pages.Count == 0) return RectF.Empty;
        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (var p in _pages)
        {
            if (p.Rect.MinX < minX) minX = p.Rect.MinX;
            if (p.Rect.MaxX > maxX) maxX = p.Rect.MaxX;
        }
        return new RectF { MinX = minX, MinY = _pages[0].Rect.MinY, MaxX = maxX, MaxY = _pages[^1].Rect.MaxY };
    }

    /// <summary>
    /// 第一个"底边 &gt;= y"的页下标（渲染层扫与块相交的页就从这里起）。
    /// 返回 _pages.Count = 没有。
    /// </summary>
    public int FirstAtOrAfter(float y)
    {
        int lo = 0, hi = _pages.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_pages[mid].Rect.MaxY < y) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
