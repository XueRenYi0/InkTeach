// [职责] PDF 页的**视口高清补丁**：1× 整页底图之上，只为可见区域渲一块设备分辨率小图（单 worker、单在飞、落稳 120ms）。
// [测试] --zoomtest / --docmem

using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// PDF 页的**视口高清补丁缓存**（2026-11-16 架构评审定）。
///
/// ## 为什么不再"整页按倍率重渲"
/// 200% 时整页重渲是 2520×3566×2²×4 ≈ **143MB**，且和页数、缩放绑死；
/// 而屏幕上真正需要的只是**视口那一块**。所以：
///   · 底图永远是 100% 的整页（<see cref="DocPages.Spec.RenderScale"/> 恒为 1，约 34MB，缩放时不再重渲）；
///   · 每个可见页再叠**一块补丁**：可见区 + 256 设备像素余量，按设备分辨率渲。
///     一块补丁 ≈ 一块屏（2520×1680 ≈ 34MB 上限），**与缩放倍率无关**。
///
/// ## 状态机（和文档页 worker 同一套语言）
///   · 后台线程一个（BelowNormal）：UI 线程只排"想要什么"，绝不在这里等 PDFium；
///   · **单在飞 + 单就绪**：任何时刻最多一个请求在渲、一个结果等采纳；
///   · **代次**：换倍率 / 收补丁 / 关文档都 +1，在途结果对不上就当场丢掉；
///   · **落稳 120ms**：滚动/缩放过程中不排队（否则每帧一个请求）；
///   · **旧补丁不撤**：新补丁就绪前一直画着旧的——否则会有一帧"既没补丁也没高清"的灰闪。
///     换倍率也一样：旧倍率的补丁**继续拉伸着画**，只把"覆盖"判据作废（会在新倍率重渲一块），
///     直到新补丁被采纳为止（2026-11-16 评审 P1-3）。
///
/// ## 坐标
/// 页矩形是画布坐标（100% 基准）；补丁的位图是**设备像素**：
///   `fullW = round(页宽 × scale)`、`rx = round((补丁.MinX − 页.MinX) × scale)`。
/// 画的时候用的 `CanvasRect` 是**同一组取整后的设备矩形反算**回来的
/// （`canvas = 页.Min + device / scale`），所以补丁和底图、墨迹严格对齐。
/// `CanvasRect` 与倍率无关，所以换倍率时它仍然可以直接拿来回测算脏区。
///
/// ## 归谁管
/// - 只有 PDF 文档（`_pdfDoc != null`）才建；图片页源像素就那么多，放大重渲只是白花内存。
/// - 穿透 / 白板不透明（页让位）时 <see cref="ReleaseAll"/>：补丁也收起来，别再问。
/// - 多显示器：**每帧只由引擎挑的那一个窗口驱动**（各窗口共用同一套相机/画布坐标；
///   每个窗口各排一份会互相踩掉对方的请求）。挑谁见 `InkEngine.ChooseDocPatchDriver`。
/// </summary>
internal sealed class DocPatchCache : IDisposable
{
    /// <summary>停手多久后才排请求（毫秒）。和分块倍率同一套防抖口径。</summary>
    public const float SettleMs = 120f;

    /// <summary>可见区外再留多少**设备像素**（按 scale 折回画布）。</summary>
    public const float MarginDevicePx = 256f;

    /// <summary>一块已就绪的补丁（位图 + 它盖住的画布矩形）。</summary>
    internal sealed class Patch
    {
        public int Page;
        public float Scale;
        public RectF CanvasRect;
        public ImageData Image;
        public bool Failed;

        public override string ToString()
            => $"补丁 第 {Page + 1} 页 {CanvasRect.MinX:F0},{CanvasRect.MinY:F0}"
             + $"..{CanvasRect.MaxX:F0},{CanvasRect.MaxY:F0} @{Scale:F2}x";
    }

    /// <summary>一个待渲请求（UI 线程填、worker 取）。</summary>
    private sealed class Request
    {
        public int Page;
        public float Scale;
        public RectF CanvasRect;
        public int FullW, FullH, Rx, Ry, Rw, Rh;
        public int Generation;
    }

    private readonly PdfiumDoc _pdf;
    private readonly Func<int, RectF?> _pageRect;
    private readonly Func<int> _pageCount;
    private readonly Action _wake;

    private readonly Dictionary<int, Patch> _patches = new();
    private readonly Dictionary<int, RectF> _failed = new();
    private readonly List<int> _scratchKeys = new();

    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private Thread _worker;

    private Request _queued;        // UI → worker（单槽）
    private Request _inFlight;      // worker 正在渲
    private Patch _ready;           // worker 渲完、等 UI 采纳（Image 为 null = 失败）
    private volatile int _generation;
    private volatile bool _stop;
    private bool _disposed;

    private float _scale = 1f;
    private bool _suspended;        // 穿透/板不透明：别问、别渲
    private bool _needsMore;        // 上一拍结论：还有页缺补丁（含"等落稳"）
    private RectF _lastVisible;
    private bool _hasLastVisible;
    private double _settleAtMs;
    private bool _settled;
    /// <summary>白板冻结页（-1 = 没有）：那一页只画 1× 底图，别为它排队/留补丁。</summary>
    private int _frozenPage = -1;

    /// <summary>
    /// 补丁位图的**单槽复用缓冲**（2026-11-16 内存抖动修复）。
    ///
    /// 为什么：每块补丁是一块 ~25MB 的 `byte[]`（2520×1680×4），走 LOH；缩放来回一轮就
    /// 新分配一块，而 LOH 空闲时不回收 → 私有字节每轮 +40~50MB。补丁的 CPU 像素只活到
    /// "上传 GPU"那一刻（`Overlay.Content` 的 `DropCpuBytes`），之后什么都不读它，
    /// 所以**一块就够**：worker 取走 → 渲 → 采纳/丢弃时原地还回来。
    ///
    /// 只在 <see cref="_gate"/> 里读写（worker 与 UI 的释放路径都碰它）。
    /// </summary>
    private byte[] _spare;

    private int _reuseCount;

    /// <summary>
    /// 补丁变了哪几块（画布矩形）。渲染层照单 MarkDirty，**由引擎在所有窗口都渲染完之后清**
    /// （每个窗口都要铺一遍自己那块分块；哪个窗口先清，后面的窗口就漏铺）。
    /// </summary>
    internal readonly List<RectF> DirtyRects = new();

    /// <summary>提交/撤销计数（自检口径：同一倍率不该反复重做）。</summary>
    public int Version { get; private set; }
    public int RequestCount { get; private set; }
    public int RenderCount { get; private set; }
    public int FailCount { get; private set; }

    /// <summary>
    /// 诊断口径：`TakeBuffer` **命中单槽**的次数（= 没有新分配那块 ~25MB）。
    /// 第二次 100→200 起就该一直涨；不涨说明池子没接上（`--zoomtest` 会断言）。
    /// </summary>
    public int PatchBufferReuseCount => Volatile.Read(ref _reuseCount);

    public DocPatchCache(PdfiumDoc pdf, Func<int, RectF?> pageRect, Func<int> pageCount, Action wake)
    {
        _pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
        _pageRect = pageRect ?? (_ => null);
        _pageCount = pageCount ?? (() => 0);
        _wake = wake;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "InkTeach-DocPatchRender",
            Priority = ThreadPriority.BelowNormal,   // 绝不给书写路径添堵
        };
        _worker.Start();
        Console.WriteLine("[文档] 高清补丁线程已启动（后台）");
    }

    // ======================================================================
    //  查询
    // ======================================================================

    /// <summary>当前补丁倍率（1 = 100%，没有补丁）。</summary>
    public float Scale => _scale;

    public int PatchCount => _patches.Count;

    /// <summary>已驻留补丁的字节数（含 CPU 那半份；上传 GPU 后 CPU 会放掉，但账仍按位图算）。</summary>
    public long PatchBytes
    {
        get
        {
            long sum = 0;
            foreach (var p in _patches.Values) sum += p.Image?.ByteSize ?? 0;
            return sum;
        }
    }

    /// <summary>`--docmem` 口径：补丁常驻字节（和 <see cref="DocPages.ResidentBytes"/> 分开报）。</summary>
    public long ResidentBytes => PatchBytes;

    /// <summary>这一页现在有补丁吗（自检/诊断用）。</summary>
    public bool HasPatch(int page) => _patches.ContainsKey(page);

    /// <summary>取这一页的补丁（**只在 UI 线程调**；画它的时候用）。</summary>
    public bool TryGetPatch(int page, out ImageData image, out RectF canvasRect)
    {
        if (_patches.TryGetValue(page, out var p) && p.Image != null)
        {
            image = p.Image;
            canvasRect = p.CanvasRect;
            return true;
        }
        image = null;
        canvasRect = RectF.Empty;
        return false;
    }

    /// <summary>
    /// 主循环"还需要出帧吗"。
    ///
    /// **只在三种情况下要**（2026-11-16 评审 P2-4）：
    ///   ① 补丁已经渲好、等这一拍采纳（`_ready`）；
    ///   ② 相机还在动、落稳窗没到（`_settled == false`）——不继续出帧，落稳计时就永远不推进，
    ///      补丁也永远排不出来；
    ///   ③ 上一拍的结论是"还缺补丁"，而这一拍还没把请求排出去（同一件事的收尾）。
    /// 请求在排队 / 在渲（`_queued` / `_inFlight`）时**不返回 true**：worker 渲完会自己叫醒主循环
    /// （见 <c>_wake</c>），那期间空转出帧只是白烧 CPU。
    ///
    /// **为什么放这里**（而不是让覆盖窗口再存一个字段）：ADR-005 禁止给 Overlay 加私有字段，
    /// 新状态归组合对象——这个类就是那个组合对象（和分块缓存的 `TileScalePending` 同一个用途：
    /// 界面不再置脏时主循环会睡在 WaitMessage 上，补丁就永远排不出来）。
    /// </summary>
    public bool WantsFrame
    {
        get
        {
            if (_disposed || _suspended || _scale <= 1.0001f) return false;
            lock (_gate) if (_ready != null) return true;
            if (HasWork()) return false;
            return !_settled && _needsMore;
        }
    }

    // ======================================================================
    //  倍率 / 让开 / 关闭
    // ======================================================================

    /// <summary>
    /// 换倍率。
    ///
    /// **不立刻放掉当前补丁**（2026-11-16 评审 P1-3）：代次 +1（排队/在飞/等采纳的旧倍率结果
    /// 全部作废），但已经采纳的补丁**继续画着**——`CanvasRect` 与倍率无关，旧像素被拉伸一点，
    /// 至少不会出现"既没补丁也没高清"的灰闪。它只是**不再算作覆盖**（`Covered` 认倍率），
    /// 所以落稳后会按新倍率另渲一块，采纳那一刻才把旧的换掉。
    ///
    /// 100%（及以下）仍然**全部放掉**：补丁没有意义，底图就是目标分辨率，
    /// 绘制路径和以前逐字节一致。
    /// </summary>
    public void SetScale(float scale)
    {
        if (_disposed) return;
        if (!(scale > 0f)) scale = 1f;
        if (MathF.Abs(scale - _scale) < 0.0005f) return;
        _scale = scale;
        _hasLastVisible = false;               // 新倍率 = 新落稳窗（别拿旧窗口排请求）
        CancelWork();                          // 代次 +1：旧倍率的在途结果落地即丢
        _needsMore = scale > 1.0001f;
        if (scale > 1.0001f) _failed.Clear();  // 换倍率后允许重试一次（失败过的块这次也许渲得出来）
        else DropPatches();                    // ≤100%：补丁全放（脏区照记，渲染层重铺底图）
    }

    /// <summary>
    /// 页让位（穿透 / 白板不透明）或关文档前：补丁全放、请求全撤，**别再问**。
    /// 记录的脏区由渲染层下一拍重铺（否则屏幕上还留着上一张高清补丁）。
    /// </summary>
    public void ReleaseAll()
    {
        if (_disposed) return;
        _suspended = true;
        _needsMore = false;
        _hasLastVisible = false;
        if (_patches.Count > 0 || HasWork()) InvalidateAll();
    }

    /// <summary>
    /// 关文档：**先停线程并等它从 PDFium 里出来**，再放位图——PDF 文档随后才 Dispose。
    ///
    /// 等 5 秒是"尽量等干净"（省得 worker 还拿着一块内存），但**安全性不靠它**：
    /// 到点没停下也没关系——PDFium 的句柄在 <see cref="Pdfium.Gate"/> 里关，
    /// 迟到的 worker 在锁里查到句柄为 0 就直接返回 null，碰不到已释放的文档
    /// （2026-11-16 评审 P0-1）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _signal.Set();

        bool joined = true;
        try
        {
            var w = _worker;
            _worker = null;
            if (w != null) joined = w.Join(5000);
        }
        catch { /* 有意忽略：线程状态失败不能挡住关闭流程（ADR-005 D5） */ }

        lock (_gate)
        {
            _queued = null;
            _inFlight = null;
            _ready?.Image?.Release();
            _ready = null;
            _spare = null;              // 池里那块也放掉（关文档后补丁不再有）
        }
        foreach (var p in _patches.Values) p.Image?.Release();
        _patches.Clear();
        _failed.Clear();
        DirtyRects.Clear();
        _generation++;
        // 线程没停下时别 Dispose 这个句柄：它可能正睡在 WaitOne 上（会抛在后台线程）。
        if (joined) _signal.Dispose();
        Console.WriteLine("[文档] 高清补丁线程已停止");
    }

    /// <summary>作废全部补丁 + 请求；脏区照记（渲染层据此重铺）。</summary>
    private void InvalidateAll()
    {
        CancelWork();
        DropPatches();
    }

    /// <summary>
    /// 撤掉排队 / 在飞 / 等采纳的请求（代次 +1，在途结果落地即丢）。
    /// **不动已经采纳的补丁**——换倍率走这条路，旧的继续画着（P1-3）。
    /// in-flight 那个不再需要额外处理：worker 落地时会拿代次对不上而直接放掉。
    /// </summary>
    private void CancelWork()
    {
        _generation++;
        lock (_gate)
        {
            _queued = null;
            if (_ready != null)
            {
                _ready.Image?.Release();
                _ready = null;
            }
        }
    }

    /// <summary>放掉所有**已采纳**的补丁（脏区照记）。不动代次、不碰排队（见 <see cref="CancelWork"/>）。</summary>
    private void DropPatches()
    {
        foreach (var p in _patches.Values)
        {
            DirtyRects.Add(p.CanvasRect);
            p.Image?.Release();
        }
        _patches.Clear();
        _failed.Clear();
        Version++;
    }

    // ======================================================================
    //  每帧同步（UI 线程）
    // ======================================================================

    /// <summary>
    /// 视口同步（每帧渲染前由**引擎**调一次，见 <c>InkEngine.SyncDocPatch</c>）。
    ///
    /// 顺序固定：**先采纳**（ready → 装/丢）→ 收掉离视口的补丁 → 判断还缺什么 →
    /// 落稳了才排一个请求。返回 true = 下一拍还需要出帧（见 <see cref="WantsFrame"/>）。
    ///
    /// ⚠ `settled` 由本类自己算（可见矩形/倍率连续 120ms 没变），不是由覆盖窗口传：
    /// ADR-005 不允许给 Overlay 加私有字段，相机落稳属于补丁这个组合对象的状态。
    ///
    /// <paramref name="frozenPage"/>：白板冻结的那一页（-1 = 没有）。它只画 1× 底图，
    /// 补丁按"当前视口"算完再硬贴会留接缝，所以有冻结页时整层都不建补丁。
    /// </summary>
    public bool Sync(RectF visibleCanvas, double nowMs, int frozenPage = -1)
    {
        if (_disposed) return false;
        _suspended = false;
        _frozenPage = frozenPage;

        AdoptReady(visibleCanvas);

        if (_scale <= 1.0001f || frozenPage >= 0)
        {
            // ≤100%：补丁没有意义（底图就是目标分辨率）。
            // 冻结页：整层只画那一页的 1× 底图（DrawDocPages 的 frozen 分支），补丁没有用武之地。
            if (_patches.Count > 0 || HasWork()) InvalidateAll();
            _needsMore = false;
            return false;
        }

        UpdateSettle(visibleCanvas, nowMs);
        TrimPatches(visibleCanvas);

        _needsMore = AnyPageNeeds(visibleCanvas);
        if (_settled && _needsMore && !HasWork())
            QueueBestRequest(visibleCanvas);

        return _needsMore || HasWork();
    }

    /// <summary>排队 / 在飞 / 等采纳：三者任一为真就还有活。</summary>
    private bool HasWork()
    {
        lock (_gate) return _queued != null || _inFlight != null || _ready != null;
    }

    /// <summary>相机（可见矩形）连续 120ms 没变 = 落稳；变了就重新计时。</summary>
    private void UpdateSettle(RectF visible, double nowMs)
    {
        bool moved = !_hasLastVisible
            || MathF.Abs(visible.MinX - _lastVisible.MinX) > 0.5f
            || MathF.Abs(visible.MinY - _lastVisible.MinY) > 0.5f
            || MathF.Abs(visible.MaxX - _lastVisible.MaxX) > 0.5f
            || MathF.Abs(visible.MaxY - _lastVisible.MaxY) > 0.5f;
        if (moved)
        {
            _lastVisible = visible;
            _hasLastVisible = true;
            _settleAtMs = nowMs + SettleMs;
        }
        _settled = nowMs >= _settleAtMs;
    }

    /// <summary>
    /// 补丁已经盖不住视口了（页翻走 / 页被换文档换掉 / 这一页现在只画底图）：放掉，别再画。
    ///
    /// **只按"页还在不在、还看不看得见"收，不按倍率收**（2026-11-16 评审 P1-3）：
    /// 换倍率时旧补丁要一直画着（拉伸一点没关系），直到新倍率的补丁被采纳——
    /// 这里要是按 `Scale != _scale` 收掉，换倍率那一刻就会闪一帧糊的。
    /// </summary>
    private void TrimPatches(RectF visible)
    {
        if (_patches.Count == 0) return;
        _scratchKeys.Clear();
        foreach (var kv in _patches)
        {
            if (kv.Key == _frozenPage
                || _pageRect(kv.Key) == null
                || !Intersects(visible, kv.Value.CanvasRect))
                _scratchKeys.Add(kv.Key);
        }
        foreach (int key in _scratchKeys)
        {
            if (!_patches.TryGetValue(key, out var p)) continue;
            // 只有"还在视口里但页没了 / 改成只画底图"才需要重铺；翻走的页本来就不该占脏区。
            if (Intersects(visible, p.CanvasRect)) DirtyRects.Add(p.CanvasRect);
            p.Image?.Release();
            _patches.Remove(key);
            Version++;
        }
        _scratchKeys.Clear();
    }

    /// <summary>还有没有哪一页缺"可见区 + 256 设备像素"这块补丁。</summary>
    private bool AnyPageNeeds(RectF visible)
    {
        int n = _pageCount();
        for (int i = 0; i < n; i++)
        {
            if (i == _frozenPage) continue;       // 冻结页只画 1× 底图，别为它排队
            var page = _pageRect(i);
            if (page is not { } pr || !Intersects(visible, pr)) continue;
            var need = NeededRect(pr, visible, _scale);
            if (need is not { } nr) continue;
            if (Covered(i, nr)) continue;
            return true;
        }
        return false;
    }

    private bool Covered(int page, in RectF need)
    {
        if (_patches.TryGetValue(page, out var p) && p.Image != null
            && MathF.Abs(p.Scale - _scale) < 0.0005f)
            return ContainsRect(p.CanvasRect, need);
        // 渲染失败过的块：别再每帧重试（"失败当没有"，底图兜着）。
        return _failed.TryGetValue(page, out var bad) && ContainsRect(bad, need);
    }

    /// <summary>挑最该先补的一页（可见面积最大），排一个请求。</summary>
    private void QueueBestRequest(RectF visible)
    {
        int bestPage = -1;
        float bestArea = -1f;
        var bestNeed = RectF.Empty;

        int n = _pageCount();
        for (int i = 0; i < n; i++)
        {
            if (i == _frozenPage) continue;       // 冻结页只画 1× 底图，别为它排队
            var page = _pageRect(i);
            if (page is not { } pr || !Intersects(visible, pr)) continue;
            var need = NeededRect(pr, visible, _scale);
            if (need is not { } nr || Covered(i, nr)) continue;
            float area = OverlapArea(nr, visible);
            if (area <= bestArea) continue;
            bestArea = area;
            bestPage = i;
            bestNeed = nr;
        }
        if (bestPage < 0) return;

        var pr2 = _pageRect(bestPage);
        if (pr2 is not { } pageRect) return;
        var req = BuildRequest(bestPage, pageRect, bestNeed);

        lock (_gate)
        {
            // 单槽：同倍率的旧排队请求让位给"更该补"的这一页。
            _queued = req;
        }
        RequestCount++;
        _signal.Set();
    }

    private Request BuildRequest(int page, in RectF pageRect, in RectF need)
    {
        float s = _scale;
        int fullW = Math.Max(1, (int)MathF.Round((pageRect.MaxX - pageRect.MinX) * s));
        int fullH = Math.Max(1, (int)MathF.Round((pageRect.MaxY - pageRect.MinY) * s));

        int rx = (int)MathF.Round((need.MinX - pageRect.MinX) * s);
        int ry = (int)MathF.Round((need.MinY - pageRect.MinY) * s);
        int rw = (int)MathF.Round((need.MaxX - need.MinX) * s);
        int rh = (int)MathF.Round((need.MaxY - need.MinY) * s);

        rx = Math.Clamp(rx, 0, fullW - 1);
        ry = Math.Clamp(ry, 0, fullH - 1);
        rw = Math.Clamp(rw, 1, fullW - rx);
        rh = Math.Clamp(rh, 1, fullH - ry);

        // 画布矩形 = **取整后的设备矩形反算**：补丁和底图/墨迹用同一组边。
        var canvas = new RectF
        {
            MinX = pageRect.MinX + rx / s,
            MinY = pageRect.MinY + ry / s,
            MaxX = pageRect.MinX + (rx + rw) / s,
            MaxY = pageRect.MinY + (ry + rh) / s,
        };

        return new Request
        {
            Page = page,
            Scale = s,
            CanvasRect = canvas,
            FullW = fullW,
            FullH = fullH,
            Rx = rx,
            Ry = ry,
            Rw = rw,
            Rh = rh,
            Generation = _generation,
        };
    }

    /// <summary>采纳就绪的补丁：倍率/页/视口都对得上才装，否则丢掉（旧补丁原框图保持不动）。</summary>
    private void AdoptReady(RectF visible)
    {
        Patch ready;
        lock (_gate)
        {
            ready = _ready;
            _ready = null;
        }
        if (ready == null) return;

        if (ready.Failed || ready.Image == null)
        {
            FailCount++;
            // 记住"这块渲不出来"，别每帧再试一次。
            if (_pageRect(ready.Page) is not null) _failed[ready.Page] = ready.CanvasRect;
            return;
        }
        if (_disposed
            || MathF.Abs(ready.Scale - _scale) > 0.0005f
            || ready.Page < 0 || ready.Page >= _pageCount()
            || _pageRect(ready.Page) is null
            || !Intersects(visible, ready.CanvasRect))
        {
            ready.Image.Release();          // 过期 / 已经不在视口：不要了
            return;
        }

        if (_patches.TryGetValue(ready.Page, out var old))
        {
            old.Image?.Release();
            DirtyRects.Add(old.CanvasRect); // 旧补丁盖过、新补丁没盖到的边也要落回底图
            _patches.Remove(ready.Page);
        }
        _patches[ready.Page] = ready;
        _failed.Remove(ready.Page);
        DirtyRects.Add(ready.CanvasRect);
        Version++;
    }

    // ======================================================================
    //  后台渲染线程
    // ======================================================================

    /// <summary>
    /// 取一块**至少** <paramref name="bytes"/> 字节的补丁缓冲：单槽够大就用它（不再分配），
    /// 否则新分配。命中时 `PatchBufferReuseCount` +1（诊断口径）。
    /// 命中/不命中都把槽清空——这块从现在起只归 worker 用。
    /// </summary>
    private byte[] TakeBuffer(int bytes)
    {
        byte[] spare;
        lock (_gate)
        {
            spare = _spare;
            _spare = null;
        }
        if (spare != null && spare.Length >= bytes)
        {
            Interlocked.Increment(ref _reuseCount);
            return spare;
        }
        return new byte[bytes];             // 槽里那块太小（换了屏 / 不同取整）→ 丢，新分配一块
    }

    /// <summary>
    /// 把补丁缓冲还回单槽：**只留最大的一块**（更小的直接丢）。
    /// 线程安全：worker 渲完的失败路径、`ImageData` 回调（UI 上传那一拍 / 采纳前作废）都会进来，
    /// 而且有些路径是在 `_gate` 里调的（`Release` 多在锁内），所以这里用同一把锁（可重入）。
    /// </summary>
    private void ReturnBuffer(byte[] buffer)
    {
        if (buffer == null || buffer.Length == 0) return;
        lock (_gate)
        {
            if (_disposed) return;          // 关文档后不再留：留着就是一块没人用的 LOH
            if (_spare == null || buffer.Length > _spare.Length) _spare = buffer;
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            Request req;
            lock (_gate)
            {
                if (_stop) break;
                req = _queued;
                _queued = null;
                _inFlight = req;
            }
            if (req == null)
            {
                // 没活就**睡死等**：排请求（QueueBestRequest）和 Dispose 都会 Set 这个信号，
                // 轮询（老写法 50ms）只会在空闲时白烧 CPU（2026-11-16 评审 P2-4）。
                _signal.WaitOne();
                continue;
            }

            int need = req.Rw * req.Rh * 4;      // 一块屏 ≈ 25MB，int 安全
            byte[] buf = TakeBuffer(need);
            byte[] bgra = null;
            try
            {
                bgra = _pdf.RenderRegion(req.Page, req.FullW, req.FullH,
                                         req.Rx, req.Ry, req.Rw, req.Rh, buf);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [文档] 补丁渲染失败（第 {req.Page + 1} 页）：{ex.Message}");
            }

            Patch done = null;
            bool adopted = false;
            if (bgra != null && bgra.Length >= (long)need)
            {
                // 回调 = 这一块 CPU 像素被丢掉的那一刻（上传后 / 没采纳就作废）还回单槽。
                var img = ImageData.Adopt(req.Rw, req.Rh, bgra, false, ReturnBuffer);
                if (img != null)
                {
                    adopted = true;
                    done = new Patch
                    {
                        Page = req.Page,
                        Scale = req.Scale,
                        CanvasRect = req.CanvasRect,
                        Image = img,
                    };
                }
            }
            if (!adopted)
            {
                // 渲失败 / 没采纳：缓冲当场还回去，别等到下一次 GC 才收（这就是以前每轮 +25MB 的来源）。
                if (bgra != null) ReturnBuffer(bgra);
                if (!ReferenceEquals(bgra, buf)) ReturnBuffer(buf);
            }
            if (done == null)
                done = new Patch
                {
                    Page = req.Page,
                    Scale = req.Scale,
                    CanvasRect = req.CanvasRect,
                    Failed = true,
                };

            bool drop = false;
            lock (_gate)
            {
                _inFlight = null;
                if (_stop || req.Generation != _generation) drop = true;
                else
                {
                    _ready?.Image?.Release();   // 上一个没被取走 → 丢掉（不排队、不堆内存）
                    _ready = done;
                }
            }
            if (drop) done.Image?.Release();
            else RenderCount++;

            // 叫醒主循环：空闲时引擎不出帧，不叫就是"补丁渲好了、屏幕上还是糊的"。
            try { _wake?.Invoke(); } catch { /* 有意忽略：叫醒失败下一拍还会再排（ADR-005 D5） */ }
        }
    }

    // ======================================================================
    //  几何小工具
    // ======================================================================

    /// <summary>可见区 ∩ 页，再往四周外扩 256 设备像素（按 scale 折回画布），最后夹回页里。</summary>
    private static RectF? NeededRect(in RectF page, in RectF visible, float scale)
    {
        float m = MarginDevicePx / (scale > 0.0001f ? scale : 1f);
        float minX = MathF.Max(page.MinX, visible.MinX - m);
        float minY = MathF.Max(page.MinY, visible.MinY - m);
        float maxX = MathF.Min(page.MaxX, visible.MaxX + m);
        float maxY = MathF.Min(page.MaxY, visible.MaxY + m);
        if (maxX <= minX + 0.001f || maxY <= minY + 0.001f) return null;
        return new RectF { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
    }

    private static bool Intersects(in RectF a, in RectF b)
        => a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinY < b.MaxY && b.MinY < a.MaxY;

    private static bool ContainsRect(in RectF outer, in RectF inner)
        => outer.MinX <= inner.MinX + 0.5f && outer.MinY <= inner.MinY + 0.5f
        && outer.MaxX >= inner.MaxX - 0.5f && outer.MaxY >= inner.MaxY - 0.5f;

    private static float OverlapArea(in RectF a, in RectF b)
    {
        float w = MathF.Min(a.MaxX, b.MaxX) - MathF.Max(a.MinX, b.MinX);
        float h = MathF.Min(a.MaxY, b.MaxY) - MathF.Max(a.MinY, b.MinY);
        return w > 0f && h > 0f ? w * h : 0f;
    }
}
