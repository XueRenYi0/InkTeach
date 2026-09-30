using System.Numerics;

namespace InkEngine;

/// <summary>触摸手势这一刻在干什么（引擎按它路由移动/抬手）。</summary>
internal enum TouchMode
{
    None = 0,       // 没有触摸手势（鼠标 / 笔，或触摸已经交回普通书写）
    Write = 1,      // 单指写字（同时在等长按）
    Erase = 2,      // 手掌 / 三指擦
    Gesture2 = 3,   // 双指：漫游 / 翻页 / 选中变换
    Marquee = 4,    // 长按成立后的框选
    SelDrag = 5,    // 长按 / 两指点选之后的"移动选中对象"
    Roam = 6,       // 单指漫游（给只报一个触点的屏）
}

/// <summary>触点落下那一刻的判定（"干净开始"的 150ms 窗口里定角色）。</summary>
internal enum TouchVerdict
{
    Write,      // 单指小面积 → 写字（落到原来的写字那条路）
    Erase,      // 手掌（大面积）或 ≥3 指 → 擦
    Gesture2,   // 双指 → 漫游 / 翻页 / 选中变换
    Roam,       // 单指漫游开关打开
    Ignore,     // 忽略这个触点（不抢正在写的那一笔）
}

/// <summary>
/// 触摸手势层（8.4.0）：**多触点跟踪 + "干净开始"的角色判定 + 手掌/三指识别 + 长按计时**。
///
/// 分工：这一层只回答"这一下是什么"（<see cref="TouchVerdict"/>）和"长按够了吗"；
/// 具体动作（写字 / 擦 / 漫游 / 翻页 / 选中变换）在 Engine 里按 <see cref="TouchMode"/> 路由——
/// 那边才拿得到文档、相机和选区。规格与理由见 `调研-触摸手势-学校大屏.md`。
///
/// 两条从 InkClass 那边学来的纪律：
///   ① **一次手势只能"干净地开始"**：触点要在 <see cref="CleanMs"/> 里一起落下、
///      而且笔画还没真画出去（路径 ≤ <see cref="CleanMoveLogical"/>）——写字中途蹭到的
///      手指/掌根一律 <see cref="TouchVerdict.Ignore"/>（"第二根手指不许抢笔"那条守卫继续保底）。
///   ② **面积阈值自适应**（免校准）：不同屏报的接触面积能差一个数量级，
///      所以"手掌"= 面积 ≥ 见过的最小触点 × <see cref="PalmFactor"/>，不写死像素数。
/// </summary>
internal sealed class TouchGestures
{
    // ---- 旋钮（Engine 从设置里灌进来）----
    public bool Enabled = true;             // 双指手势总闸
    public bool PalmErase = true;           // 手掌擦
    public float PalmFactor = 3f;           // 手掌判定倍率（保守 4 / 标准 3 / 灵敏 2）
    public bool ThreeFingerErase = true;    // 三指擦（不依赖面积上报）
    public bool LongPressSelect = true;     // 长按 = 框选 / 点选
    public bool TwoFingerTapSelect = true;  // 两指点选
    public bool SingleFingerRoam = false;   // 单指漫游（给只报一个触点的屏）

    // ---- 常量（出处见调研文档 §3）----
    public const double CleanMs = 150;          // "一起落下"的时间窗
    public const float CleanMoveLogical = 10f;  // 还没真画出去（逻辑像素）
    public const double LongPressMs = 500;      // > 停顿成型的 400ms
    public const float TapSlopLogical = 8f;     // 和 DwellTapLeaveNoInk 同一个数
    public const float DirLockLogical = 15f;    // 两指方向锁
    public const float DirRatio = 1.5f;         // 主方向要占 1.5 倍
    public const float PageTurnLogical = 80f;   // 横滑翻页阈值

    private sealed class Contact
    {
        public uint Id;
        public Vector2 Pos, Down;
        public float Size;          // 接触面积（矩形长边，物理像素；0 = 屏不报）
        public double DownMs;
        public bool InWindow;       // 落下时在"一起落下"窗口里
        public bool Moved;          // 路径超过 CleanMoveLogical
        public float Path;          // 走过的路程
    }

    private readonly List<Contact> _c = new();
    private double _baseline;        // 见过的最小接触尺寸（自适应手掌基线）
    private bool _sawArea;           // 这块屏报过有效面积吗
    private double _gestureStartMs;  // 这一轮手势的起点（全部抬起时清）

    /// <summary>当前触点。只读给 Engine 算双指增量用。</summary>
    public IReadOnlyList<ContactView> Views
    {
        get
        {
            var list = new List<ContactView>(_c.Count);
            foreach (var k in _c)
                list.Add(new ContactView(k.Id, k.Pos, k.Down, k.Size, k.Path, k.Moved));
            return list;
        }
    }

    public readonly record struct ContactView(uint Id, Vector2 Pos, Vector2 Down,
                                              float Size, float Path, bool Moved);

    public readonly record struct PairView(uint IdA, Vector2 A, uint IdB, Vector2 B);

    public int Count => _c.Count;
    public bool Any => _c.Count > 0;
    public int MaxSeen { get; private set; }      // 这块屏最多同时报过几个触点（能力自检）
    public bool SawArea => _sawArea;
    public bool LongPressFired { get; private set; }   // 长按成立（Engine 消费一次后清）
    public void ClearLongPress() => LongPressFired = false;

    public void Reset()
    {
        _c.Clear();
        LongPressFired = false;
        _gestureStartMs = 0;
    }

    /// <summary>两指数据（不够两个返回 false）。</summary>
    public bool TryPair(out PairView pair)
    {
        pair = default;
        if (_c.Count < 2) return false;
        pair = new PairView(_c[0].Id, _c[0].Pos, _c[1].Id, _c[1].Pos);
        return true;
    }

    /// <summary>
    /// 触点落下 → 定角色（"干净开始"规则）。`sizePx` = 接触矩形长边（物理像素，0 = 不报）。
    /// </summary>
    public TouchVerdict Down(uint id, float x, float y, float sizePx, double nowMs, float dpi)
    {
        // 已经跟丢/重复的 id：先清掉旧的（防"TouchUp 丢了 → 永远多一根手指"）
        _c.RemoveAll(k => k.Id == id);
        if (_c.Count == 0) _gestureStartMs = nowMs;

        bool inWindow = nowMs - _gestureStartMs <= CleanMs;
        var c = new Contact
        {
            Id = id, Pos = new Vector2(x, y), Down = new Vector2(x, y),
            Size = sizePx, DownMs = nowMs, InWindow = inWindow,
        };
        _c.Add(c);
        if (_c.Count > MaxSeen) MaxSeen = _c.Count;

        // 面积基线：只用"干净开始"的单触点更新，免得把手指并拢/手掌混进去
        if (sizePx > 0f)
        {
            _sawArea = true;
            if (_c.Count == 1 && (inWindow || _baseline <= 0))
                _baseline = _baseline <= 0 ? sizePx : MathF.Min((float)_baseline, sizePx);
        }

        // ---- 角色判定（"干净开始"才认手势；后来的触点一律忽略）----
        // 判据是**时间 + 位移**两条一起：触点要在 150ms 里一起落下，**而且第一根手指
        // 还没真画出去**（路径 ≤ 10 逻辑像素）。少了位移这一条，"写字写到一半、
        // 第二根手指很快蹭上来"会被当成双指手势，正在写的那一笔会被撤掉——
        // `--touchguardtest` 里两条老断言当场变红（"点一个都没丢"那条）。
        bool clean = inWindow && (_c.Count == 1 || !_c[0].Moved);
        if (!clean) return _c.Count == 1 ? SingleVerdict(sizePx) : TouchVerdict.Ignore;
        if (_c.Count >= 3)
            return ThreeFingerErase ? TouchVerdict.Erase : TouchVerdict.Ignore;
        if (_c.Count == 2)
        {
            // 两指里只要有一个是"手掌面积"，就是擦（手掌常带一两根手指）
            if (PalmErase && (IsPalmSize(_c[0].Size) || IsPalmSize(_c[1].Size)))
                return TouchVerdict.Erase;
            return Enabled ? TouchVerdict.Gesture2 : TouchVerdict.Ignore;
        }
        return SingleVerdict(sizePx);
    }

    private TouchVerdict SingleVerdict(float sizePx)
    {
        if (SingleFingerRoam) return TouchVerdict.Roam;
        if (PalmErase && IsPalmSize(sizePx)) return TouchVerdict.Erase;
        return TouchVerdict.Write;
    }

    private bool IsPalmSize(float sizePx)
        => PalmErase && _sawArea && _baseline > 0 && sizePx > 0
           && sizePx >= _baseline * PalmFactor;

    /// <summary>触点移动：更新路径 / 位置；返回"这一下算不算真动了"（长按计时用）。</summary>
    public void Move(uint id, float x, float y, float dpi)
    {
        var c = _c.Find(k => k.Id == id);
        if (c == null) return;
        float d = Vector2.Distance(new Vector2(x, y), c.Pos);
        c.Path += d;
        c.Pos = new Vector2(x, y);
        if (!c.Moved && c.Path > CleanMoveLogical * dpi) c.Moved = true;
    }

    public void Up(uint id) => _c.RemoveAll(k => k.Id == id);

    /// <summary>
    /// 40ms 的心跳（引擎在笔画进行中本来就有一颗定时器）：**长按判定 + 两指点按收集**。
    /// 只对"刚好一个触点、还没动、还在窗口语义里"的那一下计时（判据和"点一下不留墨"同一个数）。
    /// </summary>
    public void Tick(double nowMs, float dpi)
    {
        if (_c.Count != 1) return;
        var c = _c[0];
        if (c.Moved) return;
        if (!LongPressSelect) return;
        if (nowMs - c.DownMs < LongPressMs) return;
        if (c.Path > TapSlopLogical * dpi) return;
        if (LongPressFired) return;
        LongPressFired = true;
        Console.WriteLine("触摸：长按成立 → 选择（框选 / 点选）");
    }

    /// <summary>两指点按（松手时问一次）：两个触点都在 TapSlop 内、都在 <see cref="CleanMs"/>+250ms 里落下过。</summary>
    public bool TwoFingerTap(double nowMs, float dpi, out Vector2 mid)
    {
        mid = default;
        if (!TwoFingerTapSelect || _c.Count != 2) return false;
        if (_c[0].Moved || _c[1].Moved) return false;
        if (_c[0].Path > TapSlopLogical * dpi || _c[1].Path > TapSlopLogical * dpi) return false;
        if (nowMs - _c[0].DownMs > 300 || nowMs - _c[1].DownMs > 300) return false;
        mid = (_c[0].Pos + _c[1].Pos) * 0.5f;
        return true;
    }

    /// <summary>三指/手掌擦的橡皮范围：触点的包围盒（画布坐标），外扩一圈。</summary>
    public RectF ContactBounds(float inflate)
    {
        var r = RectF.Empty;
        foreach (var k in _c) r.Add(k.Pos.X, k.Pos.Y);
        if (r.IsEmpty) return r;
        return r.Inflate(inflate);
    }

    /// <summary>手掌/单触点擦的橡皮半宽：接触面积换算（夹在给定范围里）。</summary>
    public float EraseHalfWidth(float dpi, float minLogical, float maxLogical)
    {
        float size = _c.Count > 0 ? _c[0].Size : 0f;
        // 面积不可用或太小 → 用下限（不然擦不动）
        float half = size > 0 ? size * 0.5f : 0f;
        float min = minLogical * dpi, max = maxLogical * dpi;
        if (!_sawArea || half < min) half = min;
        if (half > max) half = max;
        return half;
    }
}
