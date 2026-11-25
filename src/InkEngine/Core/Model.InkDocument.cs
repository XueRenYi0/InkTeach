// [职责] 文档/页/槽/撤销栈/空间索引。
// [测试] --edittest

using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// **一页的内容槽**：这一页的对象、空间索引、撤销栈、计数。
///
/// 为什么要有它（用户 2026-09-26 拍板"PPT 模式参考 InkClass 原味——完全隔离"）：
/// 参考的是它们的**语义**（每页一套笔迹、切页看不到别页、清空只清本页），
/// 但实现换掉——InkClass 切页要"清空 + 逐条重放撤销历史"，ICC 要换 MemoryStream
/// 再反序列化；我们是**换一个槽的引用**：
///   · 对象、网格、撤销栈、计数全在槽里，谁都不搬；
///   · 切页 = 换引用 + 标脏，**O(1)**（真正的代价只剩"整层重铺"那一次绘制）。
/// 这也是为什么 `_undo` / `_grid` 在下面写成了转发属性：调用处一个字都不用改。
/// </summary>
internal sealed class PageSlot
{
    public readonly List<Stroke> Strokes = new();
    public readonly List<EditAction> Undo = new();
    public readonly List<EditAction> Redo = new();
    public readonly SpatialGrid Grid = new();
    public long TotalPoints;
    public int TotalIntervals;
}

internal sealed class InkDocument
{
    // ---- 页（每页一套内容）------------------------------------------------
    //
    // 0 号页 = **桌面批注页**（没有 PPT 时唯一的那一页，也是退出放映后回来的地方）。
    // > 0    = PPT 的某一页，键取 PowerPoint 的 **SlideID**（参考 Ink Canvas Ultra：
    //          页码会随插入/删除页漂移，SlideID 不会）。
    //
    // 页槽只增不减（第一批：内存换简单）；上限与清理见 `--ppttest` 的待办。

    private PageSlot _cur;
    private readonly Dictionary<int, PageSlot> _pages = new();
    private int _pageKey;

    public InkDocument()
    {
        _cur = new PageSlot();
        _pages[0] = _cur;
    }

    /// <summary>
    /// **白板槽的页键**（2026-10-16 用户定："白板设置为独立槽"）。
    ///
    /// 键空间分配（见 DocStore.SlotOf / Ppt 的键注释）：
    ///   0 = 桌面批注页；正数 = PPT SlideID；-1..-9999 = PPT 无 SlideID 的兜底；
    ///   [int.MinValue, -1073741825] = 文档页槽（DocStore.SlotOf 的 int.MinValue 段）。
    /// 白板取 **-100000**：落在两段负空间之间的空档里，谁也撞不上。
    /// </summary>
    public const int BoardPageKey = -100000;

    /// <summary>当前页的键：0 = 桌面批注页；&gt; 0 = PPT 那一页的 SlideID；
    /// -100000 = 白板槽（见 <see cref="BoardPageKey"/>）。</summary>
    public int PageKey => _pageKey;

    public List<Stroke> Strokes => _cur.Strokes;
    public readonly List<Stroke> Selected = new();

    private int _aiCardScanVersion = -1;
    private bool _hasTextBoxs;

    /// <summary>
    /// 当前页有没有 AI 题卡。按文档版本缓存一次扫描——指针每次移动都要问
    /// "在不在题卡上"，没有缓存的话一万笔的板书上每次移动都要扫一遍全表。
    /// </summary>
    internal bool HasTextBoxs
    {
        get
        {
            if (_aiCardScanVersion != Version)
            {
                _hasTextBoxs = false;
                foreach (var s in Strokes)
                    if (s.Kind == StrokeKind.TextBox) { _hasTextBoxs = true; break; }
                _aiCardScanVersion = Version;
            }
            return _hasTextBoxs;
        }
    }
    public readonly DirtyRegion Dirty = new();

    /// <summary>
    /// 自上一帧渲染之后**新增**的笔画。
    ///
    /// 内容层缓存（CanvasTiles）用它走"只补画新笔画"的快路径：正在写字时
    /// 文档只增不减，往已经画好的块上补一笔就行，不必清空整块重画——重画一块的
    /// 代价与该块里的笔画总数成正比，而"再写一笔"的代价应当只有一笔。
    /// 由渲染循环在每个窗口都渲染完之后清空（和 <see cref="Dirty"/> 同一处）。
    /// </summary>
    public readonly List<Stroke> AppendedSinceRender = new();

    /// <summary>
    /// 自上一帧之后发生过**结构性**改动：删除、插入、移动、清空、整层作废。
    /// 一旦为真，"只补画"的前提（块内容只差几条新笔画）就不成立了，
    /// 相关块必须整块重画。
    /// </summary>
    public bool StructureChangedSinceRender;

    /// <summary>
    /// 画布块序列（纵向排列）。现在固定一块——冻结截图、白板、翻页都是它的不同内容，
    /// 见计划文档第十二节。序列化按"多块"写，所以以后加页不用改格式。
    /// </summary>
    public readonly List<CanvasBlock> Blocks = new() { CanvasBlock.Default };

    /// <summary>
    /// 对象身份分配器。**只增不减、永不复用**。
    /// 反序列化时要把用过的最大值写回来，否则新对象会和老对象撞 id，
    /// 撤销和多选就会指向错的对象。
    /// </summary>
    private int _nextId = 1;
    public int NextId() => _nextId++;

    /// <summary>反序列化之后调用：把 id 水位抬到至少这么高。</summary>
    public void ReserveIdsUpTo(int maxUsed)
    {
        if (maxUsed >= _nextId) _nextId = maxUsed + 1;
    }

    /// <summary>
    /// 画布范围 = **内容边界 ∪ 一屏**，随书写自动增长。
    ///
    /// "∪ 一屏"是刻意的：没有内容的空白也必须能写（老师总得有个地方起笔），
    /// 所以画布永远至少有一屏；内容长出去了，画布跟着长。
    ///
    /// 它是**比例滚动条的前提**——没有总高度就没有比例可算，滑块无从画起。
    ///
    /// 代价：每次调用 O(笔画数)（约 0.05ms/万笔），滚轮时算一遍可以接受。
    /// </summary>
    public RectF Extent(in RectF viewport)
    {
        var r = RectF.Empty;
        foreach (var s in Strokes) r.Add(s.PaddedBounds);
        if (r.IsEmpty) return viewport;   // 空文档：画布就是一屏
        r.Add(viewport);                  // ∪ 一屏
        return r;
    }

    // ---- 页（每页一套内容）--------------------------------------------------

    /// <summary>
    /// 切到另一页（不存在就新建一个空页）。返回是否真的换了页。
    ///
    /// 切页 = **换槽的引用**：对象、空间索引、撤销栈、点数计数都在槽里，一样都不搬，
    /// 所以是 O(1)。真正的开销只有调用方那一次"整层重铺"（分块缓存要作废）。
    ///
    /// 跨页要清的三样（它们不属于任何一页）：
    ///   · 选中集合（别让"上一页的选中"挂到这一页的对象上）；
    ///   · 本帧新增的笔画暂存（渲染快路径用的，换页后前提不成立）；
    ///   · 脏区改为"整层"（旧页的缓存像素必须全部作废，否则会在新页的屏上露出来）。
    /// </summary>
    internal bool SwitchPage(int key)
    {
        if (key == _pageKey) return false;
        _cur = GetOrCreateSlot(key);
        _pageKey = key;

        Selected.Clear();
        AppendedSinceRender.Clear();
        Dirty.MarkFull();
        StructureChangedSinceRender = true;
        Version++;
        return true;
    }

    private PageSlot GetOrCreateSlot(int key)
    {
        if (_pages.TryGetValue(key, out var slot)) return slot;
        slot = new PageSlot();
        _pages[key] = slot;
        return slot;
    }

    /// <summary>所有页的键（含 0 号桌面页）。PPT 模式退出时按它逐页写盘。</summary>
    internal List<int> PageKeys()
    {
        var keys = new List<int>(_pages.Count);
        foreach (var kv in _pages) keys.Add(kv.Key);
        return keys;
    }

    /// <summary>某一页的对象表（写盘用；调用方只读，不要改）。没有这一页就是 null。</summary>
    internal List<Stroke> StrokesOf(int key)
        => _pages.TryGetValue(key, out var s) ? s.Strokes : null;

    /// <summary>
    /// **空闲瘦身**：释放所有**非当前页槽**里可重建的 GPU 缓存，返回处理过的笔数。
    ///
    /// 为什么安全：这些页当前不在屏幕上（切页 = 换引用，见 <see cref="PageSlot"/>），
    /// 缓存放掉之后几何会按需重建（<c>Stroke.Release</c> 会把版本号打回 -1）。
    /// 唯一不能碰的是"已经丢掉 CPU 源字节的位图"——那种图放掉显卡资源就再也画不出来，
    /// 所以只释放 <see cref="ImageData.HasCpuBytes"/> 为 true 的图。
    ///
    /// 与 <see cref="LoadPageContent"/> / <see cref="ResetToSinglePage"/> 的区别：
    /// 那两个是"整页不要了"，这里只是"暂时看不见，先把 GPU 侧还回去"。
    /// </summary>
    internal int TrimInactivePageCaches()
    {
        int released = 0;
        foreach (var kv in _pages)
        {
            if (ReferenceEquals(kv.Value, _cur)) continue;
            foreach (var s in kv.Value.Strokes)
            {
                // 没有 CPU 源字节的图（文档页位图）不能放：放掉就重建不出来。
                if (s.IsImage && (s.Image == null || !s.Image.HasCpuBytes)) continue;
                s.Release();
                released++;
            }
        }
        return released;
    }

    /// <summary>
    /// 往指定页槽装一页的内容（PPT 模式从磁盘读回某一页时用）。
    ///
    /// **绕开 <see cref="AppendStroke"/>**：那个只服务"当前页"，还会记历史、标脏区；
    /// 这是一次批量装载（和 <see cref="ReplaceAll"/> 同类，不产生撤销动作），
    /// 所以计数、网格、几何释放在这里自己管一遍。
    /// 装完如果这一页正好是当前页，要整层作废重画。
    /// </summary>
    internal void LoadPageContent(int key, List<Stroke> strokes, int maxId)
    {
        var slot = GetOrCreateSlot(key);
        foreach (var s in slot.Strokes) s.Release();
        slot.Strokes.Clear();
        slot.Grid.Clear();

        long points = 0;
        int intervals = 0;
        foreach (var s in strokes)
        {
            if (s.Id == 0) s.Id = NextId();
            slot.Strokes.Add(s);
            slot.Grid.Insert(s);
            points += s.Points.Count;
            intervals += s.Erased.Count;
        }
        slot.TotalPoints = points;
        slot.TotalIntervals = intervals;
        slot.Undo.Clear();
        slot.Redo.Clear();
        ReserveIdsUpTo(maxId);

        if (key == _pageKey)
        {
            Dirty.MarkFull();
            StructureChangedSinceRender = true;
            Version++;
        }
    }

    /// <summary>
    /// 丢掉所有页、回到"只有 0 号页"（加载一份新文件时用：那是一份新文档，
    /// 不该带着上一份的 PPT 页）。**旧槽里的对象要逐个 Release**——它们缓存着
    /// Direct2D 几何，直接丢字典会泄漏 GPU 侧内存（同 RemoveStroke 那条注释）。
    /// </summary>
    internal void ResetToSinglePage()
    {
        foreach (var kv in _pages)
            foreach (var s in kv.Value.Strokes) s.Release();

        _cur = new PageSlot();
        _pages.Clear();
        _pages[0] = _cur;
        _pageKey = 0;

        Selected.Clear();
        AppendedSinceRender.Clear();
        Dirty.MarkFull();
        StructureChangedSinceRender = true;
        Version++;
    }

    // 撤销栈必须有上限。原来用无上限的 Stack，一节课下来会堆进十万条动作、
    // 每条还持有笔画对象——实测 3 分钟就多占约 80 MB。主流软件的撤销深度
    // 都在 100~200 步，超过就从最旧的开始丢。
    private const int MaxUndoDepth = 200;

    /// <summary>
    /// 撤销栈里最多挂多少条笔画。
    ///
    /// 只管步数是不够的：一步"清空"就能把一万条笔画挂在栈里等着还原，
    /// 步数还是 1。所以再加一道**条数**上限，超了从最老的开始丢。
    ///
    /// 实测（--memlife）：一万条笔画挂在栈里约 5MB 点数据；几何在删除时就
    /// 释放了，不会跟着栈走。所以这道上限不是为了省那点内存，而是防止
    /// "清空一万条 + 继续写"这类操作把内存顶上去——上限之内随便撤销，
    /// 超出的部分才丢。
    /// </summary>
    public const int MaxUndoStrokes = 30_000;

    // 撤销栈与重做栈**也是每页一套**（参考 InkClass 的 TimeMachineHistories[页]：
    // 切页后 Ctrl+Z 撤的是本页自己的动作，不会撤出"看不见的东西"）。
    // 写成转发属性而不是字段，是为了让下面所有 `_undo.Xxx()` 的调用一个字都不用改。
    private List<EditAction> _undo => _cur.Undo;
    private List<EditAction> _redo => _cur.Redo;

    /// <summary>Bumped on every change so windows know to repaint.</summary>
    public int Version;

    /// <summary>当前页的点数总数（切页跟着换页槽走）。</summary>
    public long TotalPoints { get => _cur.TotalPoints; set => _cur.TotalPoints = value; }

    /// <summary>
    /// 全文档的**擦除区间总段数**（像素橡皮擦了多少段）。
    ///
    /// 维护成 O(1) 的计数而不是每次遍历统计：手测台要按秒采样它（见 EraserTelemetry），
    /// 遍历一万笔只为了显示一个数，纯属浪费。增删改三处跟着维护。
    /// </summary>
    public int TotalIntervals { get => _cur.TotalIntervals; set => _cur.TotalIntervals = value; }
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    /// <summary>
    /// 撤销/重做栈里引用着的笔画总数。
    ///
    /// 这是"删了东西内存不降"的头号嫌疑：被删的笔画还在撤销栈里等着被还原。
    /// 步数有上限（<see cref="MaxUndoDepth"/>），所以真正要盯的是**条数**——
    /// 一步"清空"就能把一万条全挂在那儿。
    /// </summary>
    public int HistoryStrokes
    {
        get
        {
            int n = 0;
            foreach (var a in _undo) n += a.HeldStrokes;
            foreach (var a in _redo) n += a.HeldStrokes;
            return n;
        }
    }

    /// <summary>空间索引**每页一个**（切页跟着换，也是"切页 O(1)"的一半）。</summary>
    private SpatialGrid _grid => _cur.Grid;
    private readonly List<Stroke> _queryScratch = new();
    /// <summary>像素橡皮的候选集合（HashSet 是为了"扫一遍笔画列表时 O(1) 判断在不在候选里"）。</summary>
    private readonly HashSet<Stroke> _candidateSet = new();
    /// <summary>整笔橡皮的候选快照（移除会改网格，必须先拷出来；复用避免拖动时每步分配）。</summary>
    private readonly List<Stroke> _eraseCandidates = new();
    /// <summary>像素橡皮命中的 (下标, 前面的干净笔画数, 笔画)。复用，避免拖动时每步分配。</summary>
    private readonly List<(int index, int logical, Stroke s)> _hitScratch = new();
    public int GridCells => _grid.CellCount;

    /// <summary>Candidate lookup through the spatial index (for measurement).</summary>
    public int QueryGrid(RectF r, List<Stroke> results) => _grid.Query(r, results);

    /// <summary>视口内有没有笔迹（多屏休眠判断用）。走空间网格，**零分配**（复用查询缓冲）。</summary>
    public bool HasStrokesInRect(in RectF r)
    {
        if (r.IsEmpty) return false;
        _grid.Query(r, _contentProbeScratch);
        return _contentProbeScratch.Count > 0;
    }

    private readonly List<Stroke> _contentProbeScratch = new();

    // -- mutation primitives (no history; the actions below drive these) ---

    public void AppendStroke(Stroke s)
    {
        if (s.Id == 0) s.Id = NextId();
        Strokes.Add(s);
        AppendedSinceRender.Add(s);
        TotalPoints += s.Points.Count;
        TotalIntervals += s.Erased.Count;
        _grid.Insert(s);
        Dirty.Add(s.PaddedBounds);
        Version++;
    }

    public void InsertStroke(int index, Stroke s) => InsertStroke(index, s, null);

    /// <summary>
    /// 插一条笔画，并**只**把 <paramref name="dirty"/> 标成脏区（null = 按常规标整条的范围）。
    ///
    /// 只有像素橡皮走带脏区的那一版，理由很直接：它把一个对象换成两段，**框外的墨
    /// 一点没变**（同一批点、同一个变换折算后的画布坐标、同样的颜色和宽度），
    /// 所以缓存里框外那些块仍然是有效的。按"整条的范围"标脏会把这条墨跨过的每一块
    /// 都拖去重画——实测一万笔时每步重画 11 块，而橡皮明明只盖住 2 块。
    /// </summary>
    public void InsertStroke(int index, Stroke s, RectF? dirty)
    {
        // 插到中间（撤销"删除"走这里）：顺序变了，块必须整块重画。
        StructureChangedSinceRender = true;
        if (s.Id == 0) s.Id = NextId();
        Strokes.Insert(Math.Clamp(index, 0, Strokes.Count), s);
        TotalPoints += s.Points.Count;
        TotalIntervals += s.Erased.Count;
        _grid.Insert(s);
        Dirty.Add(dirty ?? s.PaddedBounds);
        Version++;
    }

    public void RemoveStroke(Stroke s) => RemoveStroke(s, null);

    /// <summary>
    /// 对象改了**包围盒**（AI 题卡折叠/改宽、或直接改 Points/Transform）之后，
    /// 让空间索引重新按新范围登记它。只 remove/insert 一条，O(格子数)，不整层作废。
    ///
    /// 漏掉这一句的表现是"卡片下半截不画"：分块查询按旧包围盒回答，
    /// 新长出来的那块里根本查不到这条对象（2026-10-30 实测）。
    /// </summary>
    internal void RefreshStrokeGrid(Stroke s)
    {
        if (s == null) return;
        _grid.Remove(s);
        _grid.Insert(s);
    }

    /// <summary>删一条笔画。脏区同 <see cref="InsertStroke(int, Stroke, RectF?)"/>（像素橡皮专用）。</summary>
    public void RemoveStroke(Stroke s, RectF? dirty)
    {
        if (!Strokes.Remove(s)) return;
        // **对象从文档里消失了，就不能还留在选中集合里**（否则选中框会挂着一个不存在的东西，
        // 拖它还会给它做变换）。擦除、删除、撤销"新增"都会走到这里——集中一处兜底。
        Selected.Remove(s);
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        // 关键：笔画被移除时必须释放缓存的 Direct2D 几何，否则每擦一次、
        // 每撤销一次都会泄漏一个几何对象（连同它占的 GPU 侧细分数据）。
        // 撤销/重做会重建几何，代价很小；不释放的话一节课能涨到 GB 级。
        s.Release();
        TotalPoints -= s.Points.Count;
        TotalIntervals -= s.Erased.Count;
        // 文本框按"实际画出来的卡片"擦：排版长高后包围盒可能还没同步，
        // 只擦 PaddedBounds 会留下最后几行字（用户实测）。
        var dirtyRect = dirty ?? s.PaddedBounds;
        if (s.Kind == StrokeKind.TextBox)
        {
            var card = s.TextBoxWorldRect();
            if (!card.IsEmpty)
            {
                card = card.Inflate(6f + MathF.Max(0f, s.Width) * 0.5f);
                dirtyRect = dirtyRect.IsEmpty ? card : new RectF
                {
                    MinX = MathF.Min(dirtyRect.MinX, card.MinX),
                    MinY = MathF.Min(dirtyRect.MinY, card.MinY),
                    MaxX = MathF.Max(dirtyRect.MaxX, card.MaxX),
                    MaxY = MathF.Max(dirtyRect.MaxY, card.MaxY),
                };
            }
        }
        Dirty.Add(dirtyRect);
        Version++;
    }

    public void ClearStrokes()
    {
        StructureChangedSinceRender = true;
        foreach (var s in Strokes) s.Release();
        _grid.Clear();
        Strokes.Clear();
        Selected.Clear();
        TotalPoints = 0;
        TotalIntervals = 0;
        Dirty.MarkFull();
        Version++;
    }

    // -- user operations ---------------------------------------------------

    private void Commit(EditAction action)
    {
        _undo.Add(action);
        TrimUndo();
        _redo.Clear();

        // 命令自己报告"我动了哪块区域"，脏区在这里统一合并。
        // 这样功能代码就不必各自记得去标脏——漏标是留残影的头号原因。
        // （自己管脏区的那一条除外，它在 Redo/Undo 里加的是"两个矩形"而不是并集。）
        if (!action.SelfManagesDirty) Dirty.Add(action.AffectedUnion);
    }

    /// <summary>
    /// **交互式拖动**的提交口（8.2.0：面板粗细滑条 / 自定义取色板）。
    ///
    /// 拖动期间那个动作已经反复 `Redo` 过（屏幕上是实时的），这里只是把它**挂上撤销栈**：
    /// 于是"一次拖拽 = 一步撤销"，而不是每移动一格记一步。
    /// 松手时传进来的动作如果其实没改变什么（`HasChange` = false），调用方**不要**提交它。
    /// </summary>
    internal void CommitInteractive(EditAction action) => Commit(action);

    /// <summary>
    /// 撤销栈两道闸：**步数**（不超过 <see cref="MaxUndoDepth"/>）和
    /// **条数**（不超过 <see cref="MaxUndoStrokes"/>）。任一条超了就从最老的丢。
    ///
    /// 为什么要有条数这道：一步"清空"能挂着一万条笔画。步数只有 1，
    /// 但内存是实打实的。
    /// </summary>
    private void TrimUndo()
    {
        if (_undo.Count > MaxUndoDepth)
            _undo.RemoveRange(0, _undo.Count - MaxUndoDepth);

        int held = 0;
        foreach (var a in _undo) held += a.HeldStrokes;
        if (held <= MaxUndoStrokes) return;

        int drop = 0;
        while (held > MaxUndoStrokes && drop < _undo.Count - 1)
        {
            held -= _undo[drop].HeldStrokes;
            drop++;
        }
        if (drop > 0) _undo.RemoveRange(0, drop);
    }

    /// <summary>
    /// 改一个对象的变换，并把空间索引跟着挪。
    ///
    /// **索引必须在改之前移除、改之后插入**：改完再移除就找不到它原来占的格子了，
    /// 那些格子里会永远留着一个幽灵条目。
    /// </summary>
    internal void ApplyTransformCore(Stroke s, in Matrix3x2 m)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);

        // **文本框的平移直接并进 Points，不记在 Transform 上**（2026-11-01）。
        // 原因：文本框的输入/改宽/边缘夹取都按 Points[0] 当画布坐标；如果移动只改
        // Transform，之后编辑还得把平移"烘焙"回去，而旧的移动撤销记录仍然按
        // Transform 算，撤销/重做会越走越偏。移动一开始就写进 Points，transform
        // 保持单位阵，两条路就统一了。
        bool textMove = s.Kind == StrokeKind.TextBox && s.TextBox != null
                     && IsPureTranslation(s.Transform) && IsPureTranslation(m);
        if (textMove)
        {
            var cur = s.Transform;
            var p0 = s.Points.Count > 0 ? s.Points[0] : default;
            s.Transform = Matrix3x2.Identity;
            s.SetTextBoxRect(
                new Vector2(p0.X + cur.M31 + m.M31, p0.Y + cur.M32 + m.M32),
                s.TextBox.Width, s.TextBox.Height);
        }
        else
        {
            s.Transform = s.Transform * m;
        }
        _grid.Insert(s);
    }

    /// <summary>只含平移（没有旋转/缩放）的矩阵。文本框移动可以用这个安全烘焙。</summary>
    internal static bool IsPureTranslation(in Matrix3x2 m)
        => MathF.Abs(m.M11 - 1f) < 1e-4f
        && MathF.Abs(m.M22 - 1f) < 1e-4f
        && MathF.Abs(m.M12) < 1e-4f
        && MathF.Abs(m.M21) < 1e-4f;

    /// <summary>
    /// 改一条图形的**几何**（控制点），并把空间索引重摆。
    ///
    /// 为什么必须先 `Remove` 再 `Insert`：索引是按"它占哪块"记的，端点挪走之后
    /// 原来的格子里会留下一个幽灵条目（和 <see cref="ApplyTransformCore"/> 同一条理由）。
    ///
    /// **调用方负责脏区**：这条只负责让模型和索引跟上，加哪个矩形由
    /// <see cref="SetStrokeGeometryAction"/> 决定（它是"两个矩形、不是并集"）。
    /// </summary>
    internal void ApplyGeometryCore(Stroke s, Vector2[] localPoints)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        // 一次换掉全部控制点：Revision++ → 几何缓存失效、Bounds 重算（见 Stroke.SetPoints）。
        s.SetPoints(localPoints);
        _grid.Insert(s);
        Version++;
    }

    /// <summary>
    /// 换掉一整组 Glyph 轮廓点（保留每段的 P=-1 起点标记）。橡皮布尔减用。
    /// 调用方负责 Dirty；这里只让模型和空间索引跟上。
    /// </summary>
    internal void ApplyGlyphPoints(Stroke s, IReadOnlyList<InkPoint> points)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        s.Points.Clear();
        if (points != null) s.Points.AddRange(points);
        s.InvalidateMetrics();
        s.RecomputeBounds();
        _grid.Insert(s);
        Version++;
    }

    /// <summary>
    /// 对一条图形提交一次**改几何**（拖端点松手时走这里）。一步撤销。
    ///
    /// 和 <see cref="ApplyTransform"/> 一样是"效果先应用、再记账"：
    /// 拖动期间模型一个字没动，全部位移只在浮动层的预览里（见 计划-图形工具.md 8.1①）。
    /// </summary>
    /// <param name="newFocusU">
    /// 拖**焦点三角形的顶点 P** 时那个新的参数角（别的动作传 `null`，默认）——
    /// P 不在控制点表里，所以它得单独当一路参数传进来（见 <see cref="ShapeHandle.FocusPoint"/>）。
    /// </param>
    public bool ApplyGeometry(Stroke s, IReadOnlyList<Vector2> newLocalPoints,
                              float? newFocusU = null, float? newGridStep = null)
    {
        if (s == null || newLocalPoints == null || newLocalPoints.Count == 0) return false;
        var act = new SetStrokeGeometryAction(s, newLocalPoints, newFocusU, newGridStep);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>
    /// 对当前选中的对象施加一个变换。移动 / 缩放 / 旋转 / 镜像都走这里。
    /// 返回 false 表示没有选中任何东西。
    /// </summary>
    public bool ApplyTransform(Matrix3x2 delta)
    {
        if (Selected.Count == 0) return false;
        var act = new TransformObjectsAction(Selected, delta);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>
    /// 把几步**合成一步**提交。调用方保证这些效果**已经应用过**了
    /// （副本已插入、变换在拖动中逐帧设过），这里只记账。
    /// </summary>
    public void CommitCompound(params EditAction[] steps)
    {
        var c = new CompoundAction();
        foreach (var a in steps) if (a != null) c.Steps.Add(a);
        if (c.Steps.Count > 0) Commit(c);
    }

    /// <summary>
    /// 复制选中的对象：克隆、换新身份、整体偏移一点，**并把选中切到副本**。
    /// 选中切到副本是主流软件的行为，也符合直觉：复制完接着拖，拖的该是副本。
    /// </summary>
    public bool DuplicateSelected(float dx = 24f, float dy = 24f)
    {
        if (Selected.Count == 0) return false;

        // 防呆护栏：Ctrl+A + Ctrl+D 是**指数增长**（选全部→复制→再全选→再复制，
        // 每按一次翻一倍）。按十次就是 1024 倍，二十次是一百万条——任何画布
        // 程序都扛不住，区别只在于"崩"还是"优雅地拒绝"。
        //
        // 到上限就**明确拒绝并说清楚**，而不是让程序卡到没响应。
        // 老师说"我按了几下就卡死了"的时候，至少能看懂发生了什么。
        // 两道护栏：
        //   ① 单次复制新增太多——"全选 + 复制"连按就是在走这条路。一次复制
        //      5000 条以上几乎一定是误操作，而且代价是实打实的：
        //      每条要重新光栅化、要进空间索引、要占点数据。
        //   ② 总数上限——见 MaxObjects。
        if (Selected.Count > MaxDuplicatePerOperation)
        {
            LastRejectReason = $"一次复制 {Selected.Count} 条超过上限 {MaxDuplicatePerOperation}。"
                             + "如果确实要复制这么多，请分几次选。";
            Console.WriteLine("[拒绝] " + LastRejectReason);
            return false;
        }

        if (Strokes.Count + Selected.Count > MaxObjects)
        {
            LastRejectReason = $"批注数量将达到 {Strokes.Count + Selected.Count}，"
                             + $"超过上限 {MaxObjects}。请先清空或删掉一些。";
            Console.WriteLine("[拒绝] " + LastRejectReason);
            return false;
        }

        var act = new AddStrokesAction();
        var copies = new List<Stroke>(Selected.Count);
        foreach (var s in Selected)
        {
            var c = s.Clone();
            c.Transform = c.Transform * Matrix3x2.CreateTranslation(dx, dy);
            copies.Add(c);
            act.Strokes.Add(c);
            AppendStroke(c);       // 这一步给它分配 Id
        }
        Commit(act);

        Selected.Clear();
        Selected.AddRange(copies);
        return true;
    }

    /// <summary>
    /// 对象数量上限。
    ///
    /// 原来取 10 万（按"每条约 10KB"估的）——实测每条约 16~20KB，
    /// 10 万条就是 **2GB**，还没算内容层缓存。教室里那台机器会直接卡死。
    ///
    /// 现在取 2 万：
    ///   · 够用：连续板书 20 分钟约 7200 笔，一节 40 分钟的课约 1.5 万笔；
    ///   · 安全：堆在同一个位置的极端情况（Ctrl+A + Ctrl+D）约 400MB 就停住，
    ///     而不是一路涨到 GB 级。
    /// 到上限是**明确拒绝并说明原因**，不是让程序卡到没响应。
    /// </summary>
    public const int MaxObjects = 20_000;

    /// <summary>
    /// 单次复制最多新增多少条。
    ///
    /// 实测：堆在同一个位置的 8192 条，渲染一帧要 162ms、占用 272MB；
    /// 16384 条是 289ms / 397MB（用真实笔迹还要更高）。而正常一屏板书大约
    /// 400 条，所以 5000 这个量级既拦得住"全选+复制连按"，又不挡正常复制。
    /// </summary>
    public const int MaxDuplicatePerOperation = 5_000;

    /// <summary>上一次被拒绝的原因（给界面显示用）。没有就是 null。</summary>
    public string LastRejectReason;

    /// <summary>
    /// 拖动预览：把变换**直接设成某个值**（不是乘增量）。
    ///
    /// 为什么不乘增量：拖动中每帧都从"按下那一刻的原始变换"重算，而不是在
    /// 上一帧结果上继续乘——后者会累积浮点误差，拖得越久偏得越多，松手后
    /// 撤销也回不到原样。这个过程**不产生撤销记录**；松手时才提交一条命令。
    /// 但空间索引必须每帧跟着挪，否则拖完擦除会擦不到。
    /// </summary>
    internal void SetTransformLive(Stroke s, in Matrix3x2 m)
    {
        StructureChangedSinceRender = true;
        _grid.Remove(s);
        s.Transform = m;
        _grid.Insert(s);

        // **必须 bump 版本号**：渲染层就是靠它判断"内容层该不该修补"的
        // （SyncTiles 里 `_tilesVersion != doc.Version` 那一句）。
        //
        // 当初漏了这一句，把"不产生撤销记录"和"文档没变"混成了一件事，
        // 结果拖动时蓝框跟着走、墨迹纹丝不动，松手才整层重画跳过去。
        // 版本号管的是"外观变了没有"，跟撤销栈无关。
        //
        // 代价是每帧把脏区碰到的**分块**重画一次（不是整层重建），
        // 所以跟画面里有多少笔无关。
        Version++;
    }

    /// <summary>
    /// 告诉渲染层"这些块要整块重来"，但**模型本身一个字没改**。
    ///
    /// 用途是拖动预览（见 <c>InkEngine._dragTargets</c> 与方案 B）：手势开始的这一刻，
    /// 被拖的那一批对象要从内容层里**摘出去**——它们原来压过的地方必须重画一次
    /// （这次不带它们）。之后整个手势期间这些块都有效，一帧都不用再碰。
    ///
    /// 为什么需要这个专用入口：块标脏只在"版本号变了"那一支里做
    /// （见 <c>Overlay.SyncTiles</c>），所以"只标脏、不动模型"这件事必须
    /// 同时把版本号往上抬一格，否则这一帧的标脏会被丢掉。
    /// </summary>
    public void InvalidateContent(in RectF r)
    {
        Dirty.Add(r);
        StructureChangedSinceRender = true;
        Version++;
    }

    /// <summary>
    /// 用读出来的内容整体替换文档（加载 / 粘贴）。
    /// **这是唯一一个不产生撤销动作的批量改动**——加载是一份新文档的开始；
    /// 粘贴要进撤销栈的话，由调用方自己包成一条动作。
    /// </summary>
    internal void ReplaceAll(List<CanvasBlock> blocks, List<Stroke> strokes, int maxId)
    {
        // 加载是一份新文档的开始：**只留 0 号页**（上一份的 PPT 页连同键一起丢掉）。
        // 它换的是全新的空槽，所以这里不再需要 ClearStrokes（旧对象的几何
        // 已经由 ResetToSinglePage 逐个 Release 掉了）。
        ResetToSinglePage();
        Blocks.Clear();
        Blocks.AddRange(blocks);
        if (Blocks.Count == 0) Blocks.Add(CanvasBlock.Default);
        foreach (var s in strokes) AppendStroke(s);
        ReserveIdsUpTo(maxId);
        ClearHistory();
        Dirty.MarkFull();
        Version++;
    }

    /// <summary>Drops the undo history without touching the strokes. Used by the
    /// synthetic benchmark, which must not record 10k undo entries.</summary>
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void AddStroke(Stroke s)
    {
        var act = new AddStrokesAction();
        act.Strokes.Add(s);
        AppendStroke(s);
        Commit(act);
    }

    /// <summary>
    /// “新建后没输入就取消”的收尾：把刚插入的那条连同它的 Add 撤销记录一起丢掉，
    /// 不在历史里留一个“撤销后复活空框”的怪步骤。
    /// </summary>
    internal void DropLastAddStrokeIf(Stroke s)
    {
        if (_undo.Count == 0) return;
        if (_undo[^1] is AddStrokesAction a && a.Strokes.Count == 1
            && ReferenceEquals(a.Strokes[0], s))
            _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>一次插入多条（粘贴用），**算一步撤销**。</summary>
    public void AddStrokes(IReadOnlyList<Stroke> items)
    {
        if (items == null || items.Count == 0) return;
        var act = new AddStrokesAction();
        for (int i = 0; i < items.Count; i++)
        {
            act.Strokes.Add(items[i]);
            AppendStroke(items[i]);
        }
        Commit(act);
    }

    /// <summary>
    /// 删一条对象并**记一步撤销**（AI 题卡右上角 ✕ 走这里；普通删除走选择框那条）。
    /// </summary>
    internal bool RemoveStrokeWithUndo(Stroke s)
    {
        int idx = Strokes.IndexOf(s);
        if (idx < 0) return false;
        var act = new RemoveStrokesAction();
        act.Items.Add((idx, s));
        RemoveStroke(s);
        Commit(act);
        return true;
    }

    /// <summary>
    /// **停顿成型**的提交：图形进文档，手绘原迹留给撤销栈（一步撤销能回手绘）。
    /// 细节与理由见 <see cref="DwellShapeAction"/>。
    /// </summary>
    public void AddDwellShape(Stroke shape, Stroke ink)
    {
        var act = new DwellShapeAction { Shape = shape, Ink = ink, Index = Strokes.Count };
        AppendStroke(shape);
        Commit(act);
    }

    /// <summary>
    /// **两笔成型**的提交（用户 2026-09-25 定的"**画两支就是双曲线**"）：
    /// 图形进文档，**两笔**手绘原迹都留给撤销栈 —— 按一次 Ctrl+Z 回到**两笔手绘**。
    ///
    /// ⚠ **调用方要先把第一笔从文档里拿掉**（`RemoveStroke(ink2)`）：这个方法只管
    /// "把图形放进去 + 记好账"，和上面那条一笔的约定完全一样。
    /// `index2` 传**第一笔被拿掉之前在文档里的下标**。
    /// </summary>
    public void AddDwellShape(Stroke shape, Stroke ink, Stroke ink2, int index2)
    {
        var act = new DwellShapeAction
        {
            Shape = shape, Ink = ink, Index = Strokes.Count,
            Ink2 = ink2, Index2 = index2,
        };
        AppendStroke(shape);
        Commit(act);
    }

    /// <summary>
    /// 造一个图像对象（截图落盘、粘贴图片走这里）。
    ///
    /// 两个坐标约定：
    ///   · **局部坐标**是 (0,0)-(W,H) 像素，图像自己的像素坐标；
    ///   · 摆到画布上靠 <see cref="Stroke.Transform"/>（缩放 + 平移），
    ///     和笔迹完全一样的机制——所以之后缩放、旋转、翻转都不需要另写代码。
    ///
    /// <paramref name="scale"/> 是"物理像素 → 画布像素"的换算（等于 1/DPI）。
    /// 截屏抓到的是一比一的物理像素，在 200% 缩放的屏上直接摆上去会大一倍。
    /// </summary>
    public Stroke AddImage(ImageData img, float canvasX, float canvasY, float scale)
    {
        if (img == null) return null;
        var s = new Stroke
        {
            Tool = Tool.Capture,
            Kind = StrokeKind.Image,
            Color = new Color4(1f, 1f, 1f, 1f),
            Width = 0f,
            Image = img,
        };
        s.SetPoints(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(img.Width, img.Height),
        });
        s.Transform = Matrix3x2.CreateScale(scale)
                    * Matrix3x2.CreateTranslation(canvasX, canvasY);
        AddStroke(s);
        return s;
    }

    /// <summary>把选区换成指定的这一批对象（截图之后自动选中新对象用）。</summary>
    public void SelectOnly(IEnumerable<Stroke> items)
    {
        Selected.Clear();
        foreach (var s in items) Selected.Add(s);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var a = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        // 撤销也要把自己动过的那块标脏——**和 Commit 用同一句话**，理由一样：
        // 命令自己报告"我动了哪块区域"，漏标就是屏幕上留着撤销前的画面
        // （改变换那类命令只 bump 版本号、不碰 Dirty，原来在这里断了链：
        //  模型回去了、块却还是旧图，自检里"撤销后原位置要有墨、拖过去的地方要干净"
        //  两条当场抓到）。
        //
        // **必须在改之前采样**：AffectedAfter 是"对象**现在**占哪块"（TransformObjectsAction
        // 是现算的），改完再算就变成"原位置"，于是"拖过去的那个位置"永远没被标脏、
        // 在屏幕上留一块幽灵（实测：撤销后新旧两处都有墨）。
        var affected = a.SelfManagesDirty ? RectF.Empty : a.AffectedUnion;
        a.Undo(this);
        Dirty.Add(affected);
        _redo.Add(a);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var a = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        a.Redo(this);
        // 同 Undo：重做同样要把那块重画（自己管脏区的那条已经在 Redo 里加过了）。
        if (!a.SelfManagesDirty) Dirty.Add(a.AffectedUnion);
        _undo.Add(a);
        TrimUndo();
        return true;
    }

    public void Clear()
    {
        if (Strokes.Count == 0) return;
        var act = new ClearAction();
        act.Removed.AddRange(Strokes);
        ClearStrokes();
        Commit(act);
    }

    /// <summary>
    /// 「清空墨迹」：只删与 <paramref name="rect"/> 相交的**整笔**（当前页 / 当前这一屏）。
    /// 不切笔、不碰矩形外的对象；一步撤销。返回删掉的对象数。
    /// </summary>
    public int ClearRect(RectF rect)
    {
        if (Strokes.Count == 0) return 0;
        if (rect.MaxX <= rect.MinX || rect.MaxY <= rect.MinY) return 0;

        var act = new RemoveStrokesAction();
        for (int i = 0; i < Strokes.Count; i++)
        {
            var s = Strokes[i];
            if (s == null) continue;
            var b = s.PaddedBounds;
            if (b.MaxX < rect.MinX || b.MinX > rect.MaxX ||
                b.MaxY < rect.MinY || b.MinY > rect.MaxY) continue;
            act.Items.Add((i, s));
        }
        if (act.Items.Count == 0) return 0;

        foreach (var it in act.Items) RemoveStroke(it.stroke);
        Commit(act);
        return act.Items.Count;
    }

    /// <summary>
    /// 「清除全部墨迹」（PPT）：清掉**除 0 号页（桌面/板书）以外**的所有页。
    /// **一步撤销**能整份回来（见 <see cref="ClearSlidesAction"/>，2026-10-14 用户定）。
    /// 返回（清掉的页数、清掉的对象数）；(0, 0) = 本来就没有。
    /// </summary>
    internal (int Pages, int Strokes) ClearAllSlides()
    {
        var act = new ClearSlidesAction();
        int strokes = 0;
        foreach (int key in PageKeys())
        {
            if (key == 0) continue;
            var list = StrokesOf(key);
            if (list == null || list.Count == 0) continue;
            act.Removed[key] = new List<Stroke>(list);
            strokes += list.Count;
        }
        if (act.Removed.Count == 0) return (0, 0);

        // 逐页清空：走 LoadPageContent（它会 Release 旧几何、清空间索引、当前页标脏）。
        foreach (var key in act.Removed.Keys) LoadPageContent(key, new List<Stroke>(), 0);
        Commit(act);
        Dirty.MarkFull();
        StructureChangedSinceRender = true;
        Version++;
        return (act.Removed.Count, strokes);
    }

    private static bool SameGlyphPoints(List<InkPoint> a, List<InkPoint> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (MathF.Abs(a[i].X - b[i].X) > 0.05f ||
                MathF.Abs(a[i].Y - b[i].Y) > 0.05f ||
                MathF.Abs(a[i].P - b[i].P) > 0.01f) return false;
        }
        return true;
    }

    public int EraseAt(float x, float y, float radius)
    {        // One drag = one undo step. Collecting the removals into a single action
        // also stops a fast drag from flooding the undo stack. A call made
        // outside BeginErase/EndErase is committed straight away.
        bool standalone = _eraseBatch == null;
        var act = _eraseBatch ?? new RemoveStrokesAction();
        int added = 0;

        var probe = new RectF { MinX = x - radius, MinY = y - radius, MaxX = x + radius, MaxY = y + radius };
        _grid.Query(probe, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        // Copy first: removing strokes mutates the grid we just queried.
        // 用复用缓冲而不是 ToArray：拖动时每一步都会调到这里，每步一次分配没必要。
        _eraseCandidates.Clear();
        for (int i = 0; i < _queryScratch.Count; i++) _eraseCandidates.Add(_queryScratch[i]);
        foreach (var s in _eraseCandidates)
        {
            // **图像对象不碰**：两种橡皮一致（像素橡皮那边也跳过）。
            // 图像是"老师截来的内容"，不是笔画——橡皮擦掉半张截图不是任何人想要的；
            // 要删它用框选 + Delete。改之前这里对图像会走 IsShape 分支整条删掉，
            // 两种橡皮行为不一致（实测见 调研-图形与选中框.md 第五节）。
            // 例外：手写优化生成的公式图（Tool=Pen）是这一行的一部分，整张删掉可撤销。
            if (s.IsImage && s.Tool != Tool.Pen) continue;
            if (s.IsImage)
            {
                int ii = Strokes.IndexOf(s);
                if (ii < 0) continue;
                act.Items.Add((ii, s));
                RemoveStroke(s);
                added++;
                continue;
            }
            // **锁定的也不碰**（2026-09-16）：橡皮改的是内容，不是位置——
            // "锁了还能被擦掉"等于没锁。图像那条先例的同一个形状。
            if (s.Locked) continue;
            if (s.Kind == StrokeKind.Glyph)
            {
                // Glyph 是填充轮廓：橡皮按**布尔减**擦局部，而不是整行删。
                var newPts = GlyphFill.SubtractCircle(s, x, y, radius);
                if (newPts == null)
                {
                    int gi = Strokes.IndexOf(s);
                    if (gi < 0) continue;
                    act.Items.Add((gi, s));
                    RemoveStroke(s);
                    added++;
                }
                else if (!SameGlyphPoints(newPts, s.Points))
                {
                    var ge = new GlyphEraseAction(s, newPts);
                    ge.Redo(this);
                    _glyphEraseBatch.Add(ge);
                    added++;
                }
                continue;
            }
            if (s.Kind == StrokeKind.TextBox)
            {
                // **整笔橡皮点中文本框 = 删掉整个文本框**（用户 2026-11-01 定）：
                // 文字不是墨，没有"擦掉半个字"这回事；要么整框消失（可撤销），
                // 要么完全不碰（面积擦走 EraseRectAt，那边显式跳过 TextBox）。
                if (!s.HitTestExact(x, y, radius)) continue;
            }
            else if (s.IsShape)
            {
                // 图形的轮廓不是两个端点之间的线段，近似会偏，交给 Direct2D 精确算。
                if (!s.HitTestExact(x, y, radius)) continue;
            }
            else
            {
                // 自由笔迹：墨就是中心线两侧各半个笔宽，点到中心线的距离已经够准，走快的那条。
                // 半宽用**最粗处**：有压感的笔迹重压的地方比标称宽 50%（否则那一截擦不掉）。
                float reach = radius + s.MaxHalfWidth;
                if (s.DistanceToCanvas(x, y) > reach) continue;
            }
            int index = Strokes.IndexOf(s);
            if (index < 0) continue;
            act.Items.Add((index, s));
            added++;
            RemoveStroke(s);
        }
        if (standalone)
        {
            if (_glyphEraseBatch.Count > 0 || act.Items.Count > 0)
            {
                var compound = new CompoundAction();
                compound.Steps.AddRange(_glyphEraseBatch);
                if (act.Items.Count > 0) compound.Steps.Add(act);
                if (compound.Steps.Count > 0) Commit(compound);
            }
            _glyphEraseBatch.Clear();
        }
        return added;
    }

    private RemoveStrokesAction _eraseBatch;
    private EraseIntervalsAction _intervalBatch;
    private readonly List<EditAction> _glyphEraseBatch = new();

    /// <summary>像素橡皮算出来的参数区间（复用一个表，拖动时不要每步分配）。</summary>
    private readonly List<(float a, float b)> _intervalScratch = new();

    public void BeginErase()
    {
        _eraseBatch = new RemoveStrokesAction();
        _glyphEraseBatch.Clear();
    }

    /// <summary>像素橡皮：开始一次拖拽。整段拖拽只算**一步**撤销。</summary>
    public void BeginEraseRect() => _intervalBatch = new EraseIntervalsAction();

    public void EndErase()
    {
        bool hasRemove = _eraseBatch != null && _eraseBatch.Items.Count > 0;
        if (hasRemove || _glyphEraseBatch.Count > 0)
        {
            var compound = new CompoundAction();
            compound.Steps.AddRange(_glyphEraseBatch);
            if (hasRemove) compound.Steps.Add(_eraseBatch);
            if (compound.Steps.Count > 0) Commit(compound);
        }
        _glyphEraseBatch.Clear();
        _eraseBatch = null;
        if (_intervalBatch != null && _intervalBatch.Items.Count > 0)
        {
            // 拖拽中用区间表（便宜、不 churn 对象），**松手时才落成独立对象**。
            MaterializeIntervalBatch(_intervalBatch);
            _intervalBatch = null;
        }
    }

    /// <summary>
    /// 像素橡皮的拖拽收尾：**把"擦除区间"落成"独立对象"**。
    ///
    /// 用户 2026-09-15 定的语义："橡皮擦中墨迹或者图形，如果断开了结构，选中分开以后单独算"
    /// ——所以擦完就是**两截各自独立的笔迹**，能分别选中、分别搬、分别删。
    /// 拖拽过程中先用区间表是图便宜（一次拖拽里同一笔可能被擦十几刀，每刀都新建对象会
    /// 又慢又乱），松手一次性结账。
    ///
    /// 三步都不能省：
    ///   ① 把"整条被擦没"的**临时放回列表**——Anchor 记的是"排在它前面的、没被本批拿走的
    ///      画笔有几条"，按它升序插回去，列表就恢复成**拖拽前**的样子；
    ///   ② 这时每个原对象的 `IndexOf` 就是拖拽前那一套下标（撤销记录要用同一套坐标系）；
    ///   ③ 从后往前把每个原对象换成它的碎片（擦光的换成空），下标不会互相干扰。
    /// </summary>
    private void MaterializeIntervalBatch(EraseIntervalsAction act)
    {
        // ① 把被擦光的临时放回去（只为算下标，马上又会被换掉）
        var gone = new List<EraseIntervalsAction.Item>();
        foreach (var it in act.Items) if (it.Removed) gone.Add(it);
        gone.Sort((a, b) => a.Anchor.CompareTo(b.Anchor));
        int inserted = 0;
        foreach (var it in gone) InsertStroke(it.Anchor + inserted++, it.S);

        // ② 记下"拖拽前"的下标；③ 从后往前替换成碎片
        var live = new List<(int index, EraseIntervalsAction.Item it)>();
        foreach (var it in act.Items)
        {
            int idx = Strokes.IndexOf(it.S);
            if (idx >= 0) live.Add((idx, it));
        }
        live.Sort((a, b) => b.index.CompareTo(a.index));

        var split = new SplitErasedAction { Paint = act.Paint };
        foreach (var (index, it) in live)
        {
            var s = it.S;
            var item = new SplitErasedAction.Item
            {
                Index = index,
                Original = it.UndoOriginal ?? s,   // 熔过图形的：还的是原图形
                Before = it.Before,
            };
            split.Items.Add(item);

            // 熔过图形的：一条原对象换来**一整组**墨（见 MeltToInkParts），每一笔再各自按
            // "剩下的段"分开；没熔过的（笔迹）就是它自己一条。
            if (it.Melted != null)
            {
                foreach (var src in it.Melted) item.Parts.AddRange(src.SplitIntoRuns());
                foreach (var src in it.Melted) RemoveStroke(src, it.Paint);
            }
            else
            {
                item.Parts.AddRange(s.SplitIntoRuns());     // 剩下的每一段 = 一个独立对象
                RemoveStroke(s, it.Paint);
            }
            for (int k = 0; k < item.Parts.Count; k++) InsertStroke(index + k, item.Parts[k], it.Paint);
        }
        if (split.Items.Count > 0) Commit(split);
    }

    /// <summary>
    /// 换掉一条笔画的擦除区间表（像素橡皮 / 撤销 / 重做都走这里）。
    ///
    /// 为什么要包一层：改区间**必须**同时做三件事——几何缓存失效（<c>Revision</c> 会涨，
    /// 见 Stroke.SetErased）、内容层的块标成要重画、脏区记上。少一件就是"数据改了屏幕
    /// 不动"或者"重画范围不对留残影"。
    ///
    /// <paramref name="paint"/> 是"这次真正变了的那一块"；不给就退回整条的包围盒
    /// （保守但很粗，见 EraseIntervalsAction.AffectedBefore 的注释）。
    /// </summary>
    public void SetErased(Stroke s, List<(float a, float b)> intervals, RectF? paint = null)
    {
        TotalIntervals += (intervals?.Count ?? 0) - s.Erased.Count;
        s.SetErased(intervals);
        StructureChangedSinceRender = true;
        Dirty.Add(paint ?? s.PaddedBounds);
        Version++;
    }

    /// <summary>
    /// 像素橡皮：把以 (cx,cy) 为中心、半宽 halfW、半高 halfH 的矩形范围内的墨擦掉。
    /// 返回**受影响的笔画条数**（被切开的、被整条删的，都算一条）。
    ///
    /// 和 <see cref="EraseAt"/>（整笔橡皮）的区别只有一条：自由笔迹**被切成两段**，
    /// 而不是碰到就整条消失。
    ///
    /// 切的时候按**墨迹轮廓**算——矩形往外扩半个笔宽，再和中心线求交。只按中心线
    /// 切的话，圆头端帽会戳进框里半个笔尖，"框里干干净净"就不成立（实测看得出来）。
    ///
    /// 图形（直线 / 矩形 / 椭圆 / 箭头 / 曲线 / 立体）碰到 **熔成墨再按区间切**：
    /// 切成碎线段没有意义、也没法再拖它的顶点，但"擦一下整个图形消失"更不合直觉，
    /// 所以先熔成一组笔迹（外形逐笔不变，见 <see cref="Stroke.MeltToInkParts"/>）、
    /// 再走和墨迹一模一样的区间擦除。图像对象**不碰**：那是老师截来的内容，要删它
    /// 应该用框选 + Delete——橡皮擦掉半张截图不是任何人想要的。
    /// </summary>
    public int EraseRectAt(float cx, float cy, float halfW, float halfH)
    {
        bool standalone = _intervalBatch == null;
        var act = _intervalBatch ?? new EraseIntervalsAction();

        var rect = new RectF
        {
            MinX = cx - halfW, MinY = cy - halfH,
            MaxX = cx + halfW, MaxY = cy + halfH,
        };

        // 粗筛走空间网格：它按格子回答，会多给几个候选，所以下面还要各自精确判一次。
        _grid.Query(rect, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        // 粗筛：几何上真的碰到的才有资格。
        _candidateSet.Clear();
        for (int i = 0; i < _queryScratch.Count; i++)
        {
            var s = _queryScratch[i];
            if (s.IsImage) continue;
            // AI 题卡和图像同一条规矩：像素橡皮不啃内容；要删整张用对象橡皮 / 选择框。
            if (s.Kind == StrokeKind.TextBox) continue;
            if (s.Locked) continue;        // 同整笔橡皮：锁定的不擦（见 Stroke.Locked）
            if (!s.PaddedBounds.Intersects(rect)) continue;
            if (s.Kind == StrokeKind.Glyph) continue;   // Glyph 用整笔橡皮做布尔减，像素橡皮不啃
            if (s.Kind != StrokeKind.Freehand && !ShapeTouchesRect(s, rect)) continue;
            _candidateSet.Add(s);
        }
        if (_candidateSet.Count == 0) return 0;

        // **一次线性扫描**同时算出两件事：候选在列表里的下标（只有"整条被擦没"才用得上）、
        // 以及它前面有几条"没被本批拿走的笔画"（Anchor）。
        //
        // 为什么不逐个 IndexOf + 逐个往前数：在一块 93×150 的地方，候选项可以有一两百条，
        // 每条都 O(笔画数) 扫一遍，一万笔的画面上就是"每步几百万次比较"——实测那正是
        // 一步 22ms 里的大头。扫一遍是 O(笔画数)，和候选多少无关。
        _hitScratch.Clear();
        int unaffected = 0;
        for (int i = 0; i < Strokes.Count; i++)
        {
            var t = Strokes[i];
            if (_candidateSet.Contains(t)) _hitScratch.Add((i, unaffected, t));
            unaffected++;
        }
        if (_hitScratch.Count == 0) return 0;

        int affected = 0;
        foreach (var (index, logical, s) in _hitScratch)
            ApplyErase(act, index, logical, s, rect, ref affected);

        // 单次调用（自检、性能测试、以及将来"按一下擦一下"的用法）也要落成独立对象——
        // 和整段拖拽松手时走的是同一个收尾，语义只有一处。
        if (standalone && act.Items.Count > 0) MaterializeIntervalBatch(act);
        return affected;
    }

    /// <summary>
    /// 擦到一笔：算出"被擦掉的参数区间"，并进它的区间表；如果整条都被擦没了，
    /// 就把它从文档里拿走（撤销时按 Anchor 放回来）。
    ///
    /// <paramref name="index"/> 是调用方线性扫描时记下的下标；<paramref name="anchor"/>
    /// 是"排在这一笔前面的、没被本批拿走的笔画有几条"——只有整条被拿走时才用得上。
    /// </summary>
    private void ApplyErase(EraseIntervalsAction act, int index, int anchor,
                            Stroke s, in RectF rect, ref int affected)
    {
        // 这一次真正变了的那一块 = 橡皮矩形 ⊕ 半个笔宽（切口的圆头刚好在这一圈上）
        // + 2 像素抗锯齿余量。框外的墨没变，所以只标这一块当脏区。
        var paint = rect.Inflate(MathF.Max(1f, s.Width) * 0.5f + 2f);
        act.Paint.Add(paint);

        bool isNew = act.FindItem(s) == null;
        var item = act.Touch(s);
        item.Paint.Add(paint);

        // **图形：先熔成墨（一整组！）再切**（用户 2026-09-15 定："橡皮擦中图形，断开了
        // 也要单独算"；2026-09-20 又定"擦完外形一个字不能变"——见 MeltToInkParts）。
        // 熔完在文档里就地替换成一整组笔迹，下面的区间擦除逻辑完全不用为图形另开一条路；
        // 撤销靠 item.UndoOriginal 记着原图形、item.Melted 记着熔出来的这一组。
        if (s.Kind != StrokeKind.Freehand)
        {
            int at = Strokes.IndexOf(s);
            if (at < 0) { if (isNew) act.Drop(item); return; }

            // ① 先在**还没进文档**的这一组副本上算好每一笔要擦掉的区间。
            //    一整组都没被切到就什么都不做：既不熔（熔了没切到 = 白白把图形降级成墨），
            //    也不打掉它的几何缓存。
            var pieces = s.MeltToInkParts();
            var tables = new List<List<(float a, float b)>>(pieces.Count);
            bool any = false;
            for (int k = 0; k < pieces.Count; k++)
            {
                var tbl = new List<(float a, float b)>();
                if (pieces[k].Points.Count >= 2)
                {
                    _intervalScratch.Clear();
                    if (ErasedIntervals(pieces[k], rect, _intervalScratch))
                    {
                        tbl.AddRange(_intervalScratch);
                        any = true;
                    }
                }
                tables.Add(tbl);
            }
            if (!any) { if (isNew) act.Drop(item); return; }

            // ② 真的切到了：把图形换成一整组墨，并把每一笔算好的区间表落上去。
            RemoveStroke(s, paint);
            for (int k = 0; k < pieces.Count; k++) InsertStroke(at + k, pieces[k], paint);
            item.UndoOriginal = s;
            item.Melted = pieces;
            item.S = pieces[0];
            item.After = tables[0];
            for (int k = 0; k < pieces.Count; k++)
                if (tables[k].Count > 0) SetErased(pieces[k], tables[k], paint);
            affected++;
            return;
        }

        if (s.Points.Count <= 1)
        {
            // 单点笔迹（一个圆点）：参数只有一个 0，没有长度可分——盖住就整条擦掉。
            if (!DotCovered(s, rect)) { if (isNew) act.Drop(item); return; }
            item.After.Clear();
            SetErased(s, item.After, paint);
            MarkRemoved(act, item, anchor, s, paint);
            affected++;
            return;
        }

        _intervalScratch.Clear();
        if (!ErasedIntervals(s, rect, _intervalScratch))
        {
            if (isNew) act.Drop(item);
            return;
        }

        // 先在副本上算好结果，再决定要不要动对象：没变化就别打掉几何缓存。
        var after = new List<(float a, float b)>(s.Erased);
        foreach (var iv in _intervalScratch) Stroke.MergeInterval(after, iv.a, iv.b);
        if (SameIntervals(after, s.Erased)) { if (isNew) act.Drop(item); return; }

        item.After = after;
        SetErased(s, item.After, paint);
        if (Stroke.RemainingRunsOf(after, s.LastParam).Count == 0)
            MarkRemoved(act, item, anchor, s, paint);
        affected++;
    }

    /// <summary>
    /// 整条被擦没了：从文档里拿走，并记下**放回去的位置**。
    ///
    /// Anchor 要用"没被本批拿走的"笔画来算：本批已经拿走的那几条不在列表里了，
    /// 但它们原本排在前面，所以得把它们补回去（按记录顺序扫一遍）。
    /// 松手时 <see cref="MaterializeIntervalBatch"/> 就靠它把列表恢复成拖拽前的样子。
    /// </summary>
    private void MarkRemoved(EraseIntervalsAction act, EraseIntervalsAction.Item item,
                             int anchor, Stroke s, in RectF paint)
    {
        int fix = 0;
        foreach (var other in act.Items)
            if (other.Removed && other.Anchor <= anchor + fix) fix++;
        item.Removed = true;
        item.Anchor = anchor + fix;
        RemoveStroke(s, paint);
    }

    /// <summary>两张区间表是否逐项相同（用来判断"这一刀其实什么都没改变"）。</summary>
    private static bool SameIntervals(List<(float a, float b)> x, List<(float a, float b)> y)
    {
        if (x.Count != y.Count) return false;
        for (int i = 0; i < x.Count; i++) if (x[i] != y[i]) return false;
        return true;
    }

    /// <summary>单点笔迹（圆点）是否被这块橡皮盖住（画布坐标判定）。</summary>
    private static bool DotCovered(Stroke s, in RectF rect)
    {
        var p = new Vector2(s.Points[0].X, s.Points[0].Y);
        if (!s.Transform.IsIdentity) p = Vector2.Transform(p, s.Transform);
        // 圆点的墨是以 p 为心、半径半个笔宽的圆；"墨被盖住"= 圆心到橡皮的距离 ≤ 半笔宽。
        return DistToRect(p, rect) <= MathF.Max(1f, s.Width) * 0.5f;
    }

    /// <summary>
    /// 图形和这块矩形碰上了没有（**轮廓 ＋ 辅助线**两趟，不按外框——
    /// 见 <see cref="Stroke.ShapeOutline"/> 与 <see cref="Stroke.InkPieces"/>）。
    ///
    /// ⚠ **2026-09-21 补上第二趟（辅助线）**，修的是用户报的
    /// "**双曲线的渐近线好像擦不掉**"：轮廓里**没有**渐近线（它只在辅助槽里），
    /// 所以橡皮从虚线上掠过时判定"没碰上"，那一刀**什么也没擦**（曲线能擦、虚线纹丝不动）。
    /// 被挡的那几条棱（立体图形）、正切的两条渐近线同理，现在都能擦到了。
    ///
    /// **为什么不干脆按 `InkPieces` 全量判**：坐标系 / 数轴的**网格**也在 pieces 里，
    /// 而网格是**有意做成"不可擦"**的装饰（见 `--shapetest` 那条"落在网格线上 → 不碰它"）
    /// —— 全量判会让"擦一下网格"把整个坐标系熔成一堆笔迹。
    /// 网格那一笔的 `Aux` 是 false，所以"轮廓 ＋ 辅助线"这个判据正好把它挡在外面。
    /// </summary>
    private static bool ShapeTouchesRect(Stroke s, in RectF rect)
    {
        // 往外扩半个笔宽（+1 余量）再判交：笔身擦到就算碰到。①②两趟共用这个扩过的框。
        var r = rect.Inflate(MathF.Max(1f, s.Width) * 0.5f + 1f);
        // ① 轮廓（曲线 / 边本身）。**跳过抬笔标记**（双曲线两支之间那一下）：
        //    这一条不能省 —— NaN 喂进 SegmentHitsRect 会"比较全为 false"、
        //    最后 return true，变成"任意一擦就整条删掉"。
        var pts = s.ShapeOutline();
        for (int i = 1; i < pts.Count; i++)
        {
            if (Stroke.IsOutlineBreak(pts[i - 1]) || Stroke.IsOutlineBreak(pts[i])) continue;
            var a = pts[i - 1];
            var b = pts[i];
            if (Hit(s, a, b, r)) return true;
        }
        // ② **辅助线**（渐近线 / 被挡住的棱）：它们**不在轮廓里**，所以得单独走一遍 ——
        //    这正是"双曲线的渐近线擦不掉"的修法。网格那一笔的 Aux 是 false，不会被误判
        //    （网格有意做成不可擦，见上面那条注释）。
        foreach (var piece in s.InkPieces())
        {
            if (!piece.Aux) continue;
            for (int k = 1; k < piece.Pts.Count; k++)
                if (Hit(s, piece.Pts[k - 1], piece.Pts[k], r)) return true;
        }
        return false;

        // 小工具：把一段（局部坐标）过变换再判交。
        static bool Hit(Stroke s, Vector2 a, Vector2 b, in RectF r)
        {
            if (!s.Transform.IsIdentity)
            {
                a = Vector2.Transform(a, s.Transform);
                b = Vector2.Transform(b, s.Transform);
            }
            return SegmentHitsRect(a, b, r);
        }
    }

    /// <summary>线段与轴对齐矩形相交（slab 法）。</summary>
    private static bool SegmentHitsRect(Vector2 a, Vector2 b, in RectF r)
    {
        if (PointInRect(a, r) || PointInRect(b, r)) return true;

        float t0 = 0f, t1 = 1f;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        if (!Slab(a.X, dx, r.MinX, r.MaxX, ref t0, ref t1)) return false;
        if (!Slab(a.Y, dy, r.MinY, r.MaxY, ref t0, ref t1)) return false;
        return true;
    }

    private static bool Slab(float origin, float dir, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(dir) < 1e-9f) return origin >= lo && origin <= hi;
        float inv = 1f / dir;
        float ta = (lo - origin) * inv, tb = (hi - origin) * inv;
        if (ta > tb) (ta, tb) = (tb, ta);
        if (ta > t0) t0 = ta;
        if (tb < t1) t1 = tb;
        return t0 <= t1;
    }

    private static bool PointInRect(Vector2 p, in RectF r)
        => p.X >= r.MinX && p.X <= r.MaxX && p.Y >= r.MinY && p.Y <= r.MaxY;

    /// <summary>点到矩形的距离（在框里就是 0）。转角是圆的——这就是"矩形 ⊕ 圆笔头"。</summary>
    private static float DistToRect(Vector2 p, in RectF r)
    {
        float dx = MathF.Max(MathF.Max(r.MinX - p.X, 0f), p.X - r.MaxX);
        float dy = MathF.Max(MathF.Max(r.MinY - p.Y, 0f), p.Y - r.MaxY);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 算出"这一笔被这块橡皮擦掉的**参数区间**"（参数 = 点序号，可以带小数）。
    /// 返回 false = 一点没碰到（调用方原样留着）；true = 至少有一段被擦了（写进 into）。
    ///
    /// 判据和上一版一样：墨在中心线两侧各半个笔宽，所以"墨被盖住"等价于
    /// "中心线上的点到橡皮矩形的距离 ≤ 半笔宽"。进出边界的那一段上二分求切点参数
    /// （距离沿线段是凸的，必收敛；<see cref="EdgeT"/> 与方向无关）。
    ///
    /// 画布坐标只用来**判定**，"擦掉哪一段"用的是笔画自己的参数——所以带变换的笔迹
    /// （被框选移动 / 缩放过）不用再把变换烘进点坐标，区间天生跟着变换走。
    /// </summary>
    private static bool ErasedIntervals(Stroke s, RectF rect, List<(float a, float b)> into)
    {
        int n = s.Points.Count;
        if (n < 2) return false;
        float reach = MathF.Max(1f, s.Width) * 0.5f;

        Vector2 Pt(int i)
        {
            var v = new Vector2(s.Points[i].X, s.Points[i].Y);
            return s.Transform.IsIdentity ? v : Vector2.Transform(v, s.Transform);
        }
        bool In(Vector2 p) => DistToRect(p, rect) <= reach;

        bool touched = false;
        bool inRun = false;
        float runStart = 0f;

        // 起手就在框里（第一段之前没有任何边界要算）
        if (In(Pt(0))) { inRun = true; runStart = 0f; touched = true; }

        // **逐段扫，而且段内要细分**：只看两个端点是不够的——橡皮完全可能整段落
        // 在两个采样点之间（稀疏采样、或者只有两个端点的长直线），只看端点会"擦了个寂寞"。
        // 段内按固定步长细分，跨界处在相邻两个子采样之间二分求切点（精度和整段二分一致）。
        // 代价：密集笔迹（采样点间隔 1~5 像素）每段只多 1~2 次距离计算；
        //       只有"两个端点拉得很开"的笔迹才多算，而那正是原来会漏的那种。
        const float SubStep = 4f;
        for (int i = 0; i + 1 < n; i++)
        {
            Vector2 a = Pt(i), b = Pt(i + 1);
            float len = Vector2.Distance(a, b);
            int sub = Math.Max(1, (int)MathF.Ceiling(len / SubStep));

            float prevT = 0f;
            bool prevIn = In(a);
            for (int k = 1; k <= sub; k++)
            {
                float t = k / (float)sub;
                bool cur = In(Vector2.Lerp(a, b, t));
                if (cur) touched = true;

                if (!inRun && cur)
                {
                    runStart = i + EdgeT(a, b, rect, reach, prevT, t);
                    inRun = true;
                }
                else if (inRun && !cur)
                {
                    into.Add((runStart, i + EdgeT(a, b, rect, reach, prevT, t)));
                    inRun = false;
                }
                prevT = t;
                prevIn = cur;
            }
        }
        if (inRun) into.Add((runStart, n - 1));      // 一直擦到最后一笔
        return touched;
    }

    /// <summary>
    /// 线段上"刚好压在橡皮边界上"那一点的参数 t：在 [<paramref name="lo"/>,
    /// <paramref name="hi"/>] 之间二分，两端一个是框内、一个是框外。
    ///
    /// **方向必须无关**：进框（前一点在外、后点在里面）和出框（反过来）都要用。
    /// 第一版只按"起点在外"写，于是出框那一刀收敛到了框内那个采样点——
    /// 结果是每条的右半边多留了一截墨，切口不在框边而在框里。自检抓到的就是这个。
    /// 框是凸的，距离沿线段先减后增，二分一定收敛（20 次 ≈ 百万分之一，远小于一个像素）。
    /// </summary>
    private static float EdgeT(Vector2 a, Vector2 b, in RectF r, float reach, float lo, float hi)
    {
        bool loInside = DistToRect(Vector2.Lerp(a, b, lo), r) <= reach;
        for (int i = 0; i < 20; i++)
        {
            float mid = (lo + hi) * 0.5f;
            bool midInside = DistToRect(Vector2.Lerp(a, b, mid), r) <= reach;
            if (midInside == loInside) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    /// <summary>
    /// 把**选区里被擦断的**笔迹拆成独立对象（剩下的每一段各成一个）。
    ///
    /// 为什么要有这一步：像素橡皮默认**不拆**（一条笔迹 + 擦除区间表）——那样半透明
    /// 荧光笔自交处不会混合两次、反复擦对象数也不涨、`Id` 也不变。但用户眼睛看到的是
    /// "这里明明断成两截了"，想单独搬动其中一截时，选中整条太反直觉。折中：
    /// **平时不拆，一旦被框选到就拆**——"擦"保持轻量，"单独摆弄某一段"也做得到。
    ///
    /// 拆出来的段继承样式与变换，点仍是**局部坐标**（缩放/旋转过的笔迹拆完也正确）；
    /// 一次框选拆多条的，**算一步撤销**。
    /// </summary>
    /// <returns>拆开了几条（0 = 选中的里面没有被擦断的）</returns>
    public int SplitErasedSelection()
    {
        var act = new SplitErasedAction();

        // **按下标降序**处理：每拆一条都会改变列表长度，从后往前走，前面那些还没处理的
        // 下标才是同一套坐标系（撤销/重做按它插回去才不会错位）。
        var targets = new List<(int index, Stroke s)>();
        foreach (var s in Selected)
        {
            if (!s.HasErased || s.Kind != StrokeKind.Freehand) continue;
            int i = Strokes.IndexOf(s);
            if (i >= 0) targets.Add((i, s));
        }
        if (targets.Count == 0) return 0;
        targets.Sort((a, b) => b.index.CompareTo(a.index));

        var newSelection = new List<Stroke>(Selected);
        foreach (var (index, s) in targets)
        {
            var parts = s.SplitIntoRuns();
            if (parts.Count <= 1) continue;            // 只剩一段（或者全被擦没了），不用拆

            var item = new SplitErasedAction.Item { Index = index, Original = s };
            item.Parts.AddRange(parts);
            act.Items.Add(item);
            newSelection.Remove(s);
            newSelection.AddRange(parts);

            RemoveStroke(s);
            for (int k = 0; k < parts.Count; k++) InsertStroke(index + k, parts[k]);
        }
        if (act.Items.Count == 0) return 0;

        Commit(act);
        Selected.Clear();
        Selected.AddRange(newSelection);
        return act.Items.Count;
    }

    public void DeleteSelected()
    {
        if (Selected.Count == 0) return;
        var act = new RemoveStrokesAction();
        foreach (var s in Selected)
        {
            if (s.Locked) continue;            // 锁定的不删（见 Stroke.Locked）
            int idx = Strokes.IndexOf(s);
            if (idx >= 0) act.Items.Add((idx, s));
        }
        foreach (var it in act.Items) RemoveStroke(it.stroke);
        Selected.Clear();
        if (act.Items.Count > 0) Commit(act);
    }

    /// <summary>Moves the current selection by a delta (drag-to-move).</summary>
    public void MoveSelected(float dx, float dy)
        => ApplyTransform(Matrix3x2.CreateTranslation(dx, dy));

    // ---- 属性编辑（颜色 / 粗细 / 锁定）与层级 -------------------------------

    /// <summary>
    /// 给一批对象改颜色（一步撤销）。<paramref name="newColors"/> 与 targets 一一对应
    /// （荧光笔那一条要用半透明版本，见 SetStrokePropAction）。
    /// </summary>
    public bool ApplyColors(IReadOnlyList<Stroke> targets, Color4[] newColors)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, newColors);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>给一批对象改粗细（**物理**像素，调用方自己乘 DPI）。一步撤销。</summary>
    public bool ApplyWidths(IReadOnlyList<Stroke> targets, float width)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, width);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>给一批对象加锁 / 解锁（一步撤销）。见 Stroke.Locked 的语义。</summary>
    public bool ApplyLock(IReadOnlyList<Stroke> targets, bool locked)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, locked);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>
    /// 给一批对象改线型（一步撤销）。见 <see cref="StrokeDash"/>。
    /// 它是**样式**不是形状：包围盒、几何缓存、命中判定都不受影响。
    /// </summary>
    public bool ApplyDash(IReadOnlyList<Stroke> targets, StrokeDash dash)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, dash);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>给一批**坐标系**改"要不要网格"（一步撤销）。见 <see cref="Stroke.Grid"/>。
    /// 和线型不同：网格**改的是几何**（多画那些线），所以缓存要重建。</summary>
    public bool ApplyGrid(IReadOnlyList<Stroke> targets, bool on)
    {
        if (targets == null || targets.Count == 0) return false;
        var act = new SetStrokePropAction(targets, on, SetStrokePropAction.Prop.Grid);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>层级：把选中的整体置顶 / 置底（一步撤销）。内部相对顺序不变。</summary>
    public bool ReorderSelected(bool toFront)
    {
        if (Selected.Count == 0 || Selected.Count >= Strokes.Count) return false;
        var act = new ReorderStrokesAction(Selected, toFront);
        act.Redo(this);
        Commit(act);
        return true;
    }

    /// <summary>
    /// 框选：**框碰到墨就选中那一条**（不是"整条都在框里才选中"）。
    ///
    /// 判据分两步：
    ///   ① 粗筛：<see cref="Stroke.PaddedBounds"/> 和框不相交的直接排除（便宜）；
    ///   ② 精确：<see cref="InkTouchesRect"/>——真的拿墨（中心线/轮廓按半个笔宽外扩、
    ///      擦掉的段不算）去和框判交。
    ///
    /// **为什么不能只看 PaddedBounds**（2026-10-04 用户实测报的 bug）：PaddedBounds 是
    /// 轴对齐**外接矩形**，斜线的外接矩形有两个巨大的空角——框放在空角里、离斜线还远，
    /// 旧判据照样"相交"、整条被选进来。图形（圆的外接矩形中心是空的）和旋转过的
    /// 图像同理。所以"碰到就选"必须让墨自己去碰，不能拿外接矩形代替。
    ///
    /// 保留"碰到就选"而不是"整条都在框里"：按后者屏幕上永远选不全——笔迹只要有一头
    /// 在屏幕外（框拖不到那儿），或者粗笔的笔身压出框外一点点，那条就永远选不上，
    /// 用户看到的就是"我明明全框住了，却没全选中"。这和 OneNote 的框选一致，也是老师
    /// 更需要的那个方向（选多了可以点空白重来，选少了会以为软件坏了）。
    /// </summary>
    public void ApplyMarquee(RectF r)
    {
        Selected.Clear();
        _grid.Query(r, _queryScratch);
        foreach (var s in _queryScratch)
        {
            if (!s.PaddedBounds.Intersects(r)) continue;   // ① 粗筛
            if (!InkTouchesRect(s, r)) continue;           // ② 精确判交
            Selected.Add(s);
        }
    }

    /// <summary>
    /// 框选的精确判据：**墨真的和框有交集**（见 <see cref="ApplyMarquee"/>）。
    ///
    /// 三种对象各走各的真实形状：
    ///   · 自由笔迹——中心线逐段判交，外扩半个笔宽；擦掉的参数区间不算墨；
    ///   · 图形——和像素橡皮同一条 <see cref="ShapeTouchesRect"/>（轮廓折线 ＋ 辅助线，
    ///     网格有意挡在外面）；
    ///   · 图像——"填满的一块"：四角变换到画布，和框做 OBB 相交（SAT）。
    ///     只按变换后的外接矩形判，旋转过的图像同样会在空角里被误选。
    /// </summary>
    private static bool InkTouchesRect(Stroke s, in RectF rect)
    {
        if (s.IsImage) return ImageTouchesRect(s, rect);
        if (s.Kind != StrokeKind.Freehand) return ShapeTouchesRect(s, rect);
        return FreehandTouchesRect(s, rect);
    }

    /// <summary>自由笔迹与框判交：中心线逐段 ＋ 半个笔宽（压感按最粗处），跳过擦掉的区间。</summary>
    private static bool FreehandTouchesRect(Stroke s, in RectF rect)
    {
        int n = s.Points.Count;
        if (n == 0) return false;

        // 笔身外扩：和 PaddedBounds / HitTestExact 同一口径（最粗处 ＋ 1 像素余量）。
        var r = rect.Inflate(MathF.Max(1f, s.MaxHalfWidth) + 1f);
        bool ident = s.Transform.IsIdentity;

        Vector2 P(float t)
        {
            var v = s.PointAtParam(t);
            return ident ? v : Vector2.Transform(v, s.Transform);
        }

        if (n == 1) return PointInRect(P(0), r);

        // 没有擦除（绝大多数）走直路，不做任何中间表。
        if (s.Erased.Count == 0)
        {
            for (int i = 1; i < n; i++)
                if (SegmentHitsRect(P(i - 1), P(i), r)) return true;
            return false;
        }

        // 擦掉的段不算墨（和点选 HitObjectAt / 套索同一口径）：按剩下的参数区间分段判交。
        foreach (var (a, b) in s.RemainingRuns())
        {
            float lo = MathF.Max(0f, a), hi = MathF.Min(n - 1f, b);
            if (hi < lo) continue;
            if (hi - lo < 1e-4f) { if (PointInRect(P(lo), r)) return true; continue; }

            int i0 = Math.Max(0, (int)MathF.Floor(lo));
            int i1 = Math.Min(n - 1, (int)MathF.Ceiling(hi));
            for (int i = i0; i < i1; i++)
            {
                float sa = MathF.Max(lo, i), sb = MathF.Min(hi, i + 1);
                if (sb - sa < 1e-5f) continue;
                if (SegmentHitsRect(P(sa), P(sb), r)) return true;
            }
        }
        return false;
    }

    /// <summary>图像（"填满的一块"）与框判交：四角变换到画布后做 OBB 相交。</summary>
    private static bool ImageTouchesRect(Stroke s, in RectF rect)
    {
        var b = s.Bounds;
        if (b.IsEmpty) return false;
        return ObbHitsRect(
            Vector2.Transform(new Vector2(b.MinX, b.MinY), s.Transform),
            Vector2.Transform(new Vector2(b.MaxX, b.MinY), s.Transform),
            Vector2.Transform(new Vector2(b.MaxX, b.MaxY), s.Transform),
            Vector2.Transform(new Vector2(b.MinX, b.MaxY), s.Transform),
            rect);
    }

    /// <summary>
    /// 凸四边形（顶点按序）和轴对齐矩形相交——分离轴定理（SAT）。
    /// 轴取"矩形的两条轴 ＋ 四边形的两条相邻边法线"；四个轴都分离不了才算相交。
    /// </summary>
    private static bool ObbHitsRect(Vector2 q0, Vector2 q1, Vector2 q2, Vector2 q3, in RectF r)
    {
        float cx = (r.MinX + r.MaxX) * 0.5f, cy = (r.MinY + r.MaxY) * 0.5f;
        float hx = (r.MaxX - r.MinX) * 0.5f, hy = (r.MaxY - r.MinY) * 0.5f;

        bool Separated(float ax, float ay)
        {
            if (ax * ax + ay * ay < 1e-12f) return false;   // 退化边不构成分离轴
            float p0 = ax * q0.X + ay * q0.Y;
            float p1 = ax * q1.X + ay * q1.Y;
            float p2 = ax * q2.X + ay * q2.Y;
            float p3 = ax * q3.X + ay * q3.Y;
            float lo = MathF.Min(MathF.Min(p0, p1), MathF.Min(p2, p3));
            float hi = MathF.Max(MathF.Max(p0, p1), MathF.Max(p2, p3));
            float c = ax * cx + ay * cy;
            float ext = MathF.Abs(ax) * hx + MathF.Abs(ay) * hy;
            return c + ext < lo - 1e-3f || c - ext > hi + 1e-3f;
        }

        return !Separated(1f, 0f) && !Separated(0f, 1f)
            && !Separated(-(q1.Y - q0.Y), q1.X - q0.X)
            && !Separated(-(q2.Y - q1.Y), q2.X - q1.X);
    }

    /// <summary>WPF 的 `_percentIntersectForInk`：代表点落进圈里的比例（百分数）。</summary>
    public const float LassoPercentInk = 80f;

    /// <summary>
    /// 套索的判据：**一个对象的代表点里有 80% 以上落在圈里**才算选中。
    ///
    /// 80 这个数是抄微软 WPF 的 `LassoHelper._percentIntersectForInk = 80`——它是
    /// 微软在真实手写板上反复调出来的：太高（100%＝"整条都在圈里"）会像框选那样
    /// 永远选不全（笔头伸出圈外一点点就白圈）；太低会把手滑划过的笔迹一股脑选进来。
    ///
    /// 注意它和**框选**的判据**故意不一样**：框选是"碰到就选"（对应 OneNote），
    /// 套索是"圈住了才算"（对应 WPF 与绝大多数白板）。两种手势的意图本来就不同：
    /// 拖矩形是"我框住这一片"，画一圈是"我把这一条圈起来了"。
    ///
    /// **贴边＝无限延伸**（<paramref name="visible"/> 是当前可见的画布矩形）：
    /// 圈到屏幕边的顶点会被推到"屏幕外很远"（见 ExtendToEdges），于是伸出屏幕的
    /// 那一截也算在圈里。没有这一条，一条横跨屏幕的长笔迹永远选不全——框选当初
    /// 就是为同一个问题才改成"碰到就选"的（见 ApplyMarquee 的注释）。
    ///
    /// 返回**被这一圈判中**的对象数（加减选之前），自检和日志用它。
    /// </summary>
    public int ApplyLasso(IReadOnlyList<Vector2> path, in RectF visible, float edgeSnap,
                          bool additive = false, bool subtractive = false)
    {
        if (path == null || path.Count < 3) return 0;

        // 候选粗筛用**没延伸过**的圈：延伸之后包围盒会到几十万像素外，等于全表扫描。
        // 这不影响正确性——能落在圈里的对象，它的包围盒一定和圈（原始范围）相交；
        // 不相交的，在屏幕上根本看不见（看不见的东西本来也不该被圈进来）。
        var bbox = RectF.Empty;
        foreach (var p in path) bbox.Add(p.X, p.Y);
        bbox = bbox.Inflate(1f);

        _grid.Query(bbox, _queryScratch);
        if (_queryScratch.Count == 0) return 0;

        var poly = ExtendToEdges(path, visible, edgeSnap);
        var scratch = new List<Vector2>();
        var hit = new List<Stroke>();
        foreach (var s in _queryScratch)
        {
            scratch.Clear();
            s.AppendRepresentativePoints(scratch);
            if (scratch.Count == 0) continue;

            int inside = 0;
            foreach (var p in scratch)
                if (PointInPolygon(poly, p.X, p.Y)) inside++;

            // 整数比较，避免浮点边界上的 79.99999：inside / Count ≥ 80%
            if (inside * 100 >= scratch.Count * LassoPercentInk) hit.Add(s);
        }

        if (subtractive) { foreach (var s in hit) Selected.Remove(s); }
        else if (additive) { foreach (var s in hit) if (!Selected.Contains(s)) Selected.Add(s); }
        else { Selected.Clear(); Selected.AddRange(hit); }
        return hit.Count;
    }

    /// <summary>"无限远"的替代值：10 万像素。见 ExtendToEdges。</summary>
    private const float EdgeRun = 100_000f;

    /// <summary>
    /// 把"贴着屏幕边"的顶点推到屏幕外很远（EdgeRun），这一步就是
    /// 把"圈到屏幕边"变成"圈到无限远"。
    ///
    /// 为什么推顶点、而不是"把那条边延长成射线"：射线版的数学更干净，但要给多边形
    /// 引入"无穷远边"的概念，后续的奇偶判定、包围盒、预览绘制全都得跟着改。
    /// 推到 10 万像素外，在**可见区域尺度**上和无穷远没有区别（屏幕上最长的一条
    /// 板书也就几千像素），而判据仍然是一个普通的多边形。
    ///
    /// 代价（已知）：被推出去的那个顶点会在屏幕外鼓出一个楔形，圈外、又恰好在
    /// 那个方向上的东西会被多选进来。屏幕上看不出来（那些东西本来就不在可见区域里）。
    /// </summary>
    private static List<Vector2> ExtendToEdges(
        IReadOnlyList<Vector2> path, in RectF visible, float edgeSnap)
    {
        var poly = new List<Vector2>(path.Count);
        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            if (!visible.IsEmpty)
            {
                if (p.X <= visible.MinX + edgeSnap) p.X = visible.MinX - EdgeRun;
                else if (p.X >= visible.MaxX - edgeSnap) p.X = visible.MaxX + EdgeRun;
                if (p.Y <= visible.MinY + edgeSnap) p.Y = visible.MinY - EdgeRun;
                else if (p.Y >= visible.MaxY - edgeSnap) p.Y = visible.MaxY + EdgeRun;
            }
            poly.Add(p);
        }
        return poly;
    }

    /// <summary>
    /// 点在多边形内（射线法 / 奇偶规则）。每点 O(n)，不需要三角化。
    /// </summary>
    public static bool PointInPolygon(IReadOnlyList<Vector2> poly, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            float xi = poly[i].X, yi = poly[i].Y;
            float xj = poly[j].X, yj = poly[j].Y;
            if ((yi > y) == (yj > y)) continue;                 // 这条边不跨过水平线
            float xCross = (xj - xi) * (y - yi) / (yj - yi) + xi;
            if (x < xCross) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 点选：返回 (x,y) 处**最上面**的那个对象（没点到就 null）。
    ///
    /// 判据和橡皮命中同源：自由笔迹看"点到中心线的距离 ≤ 半笔宽 + 容差"
    /// （**被像素橡皮擦掉的那段不算墨**，<see cref="Stroke.DistanceToCanvas"/> 已经跳过）；
    /// 图形 / 图像交给 Direct2D 的描边 / 填充命中。
    ///
    /// 多条叠在一起时取列表里**下标最大**的（最后画的＝最上面）——和 InkClass 一致
    /// （那边是 `hitTestStrokes[^1]`）。
    /// </summary>
    public Stroke HitObjectAt(float x, float y, float tolerance)
    {
        // 粗筛：网格是按"带笔宽的外扩包围盒"索引的，所以点周围一个小矩形就能捞到
        // 所有可能命中的对象（宽笔的笔身伸过来也算）。
        var probe = new RectF
        {
            MinX = x - tolerance, MinY = y - tolerance,
            MaxX = x + tolerance, MaxY = y + tolerance,
        };
        _grid.Query(probe, _queryScratch);
        if (_queryScratch.Count == 0) return null;

        Stroke best = null;
        int bestIndex = -1;
        foreach (var s in _queryScratch)
        {
            if (!s.PaddedBounds.Contains(x, y)) continue;

            bool hit = s.Kind == StrokeKind.Freehand
                ? s.DistanceToCanvas(x, y) <= s.MaxHalfWidth + tolerance    // 有压感时按最粗处（否则重压处点不中）
                : s.HitTestExact(x, y, tolerance);
            if (!hit) continue;

            int idx = Strokes.IndexOf(s);
            if (idx > bestIndex) { bestIndex = idx; best = s; }
        }
        return best;
    }

    /// <summary>
    /// 点选：把 (x,y) 处的对象选中，返回**被点到的那个**（没点到 = null，调用方继续框选）。
    ///
    /// 修饰键（和主流一致）：<paramref name="additive"/>（Shift）= 加选，已经在选区里就
    /// **移出**（切换）；<paramref name="subtractive"/>（Alt）= 移出。加/减选时不动其它选中，
    /// 方便连着点几条攒出一个选择。
    /// </summary>
    public Stroke SelectAt(float x, float y, float tolerance,
                           bool additive = false, bool subtractive = false)
    {
        var hit = HitObjectAt(x, y, tolerance);
        if (hit == null) return null;

        if (subtractive) Selected.Remove(hit);
        else if (additive) { if (!Selected.Remove(hit)) Selected.Add(hit); }
        else { Selected.Clear(); Selected.Add(hit); }
        return hit;
    }

    /// <summary>Marks the whole content layer stale (cheap to say, expensive to
    /// repaint, so only used when a change really touches the entire surface).</summary>
    public void InvalidateAll()
    {
        StructureChangedSinceRender = true;
        Dirty.MarkFull();
        Version++;
    }
}

