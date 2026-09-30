using System.Numerics;

namespace InkEngine;

/// <summary>触摸手势这一刻在干什么（引擎按它路由移动/抬手）。</summary>
internal enum TouchMode
{
    None = 0,       // 没有触摸手势（鼠标 / 笔，或触摸已经交回普通书写）
    Write = 1,      // 单指写字（同时在等长按）
    Erase = 2,      // ≥3 指擦（松手回原工具）
    Gesture2 = 3,   // 双指：漫游 / 翻页 / 选中变换
    Marquee = 4,    // 长按成立后的框选
    SelDrag = 5,    // 长按 / 两指点选之后的"移动选中对象"
    Roam = 6,       // 单指漫游（给只报一个触点的屏）
}

/// <summary>触点落下那一刻的判定（"干净开始"的 150ms 窗口里定角色）。</summary>
internal enum TouchVerdict
{
    Write,      // 单指 → 写字（落到原来的写字那条路）
    Erase,      // **≥3 指一起落下** → 擦（不依赖接触面积）
    Gesture2,   // 双指 → 漫游 / 翻页 / 选中变换
    Roam,       // 单指漫游开关打开
    Ignore,     // 忽略这个触点（不抢正在写的那一笔）
}

/// <summary>
/// 触摸手势层：**多触点跟踪 + "干净开始"的角色判定 + 长按计时**。
///
/// 分工：这一层只回答"这一下是什么"（<see cref="TouchVerdict"/>）和"长按够了吗"；
/// 具体动作（写字 / 擦 / 漫游 / 翻页 / 选中变换）在 Engine 里按 <see cref="TouchMode"/> 路由。
///
/// ## 面积（`rcContact`）**不参与判定**（2026-09-30 再收敛）
///
/// 原方案里有"手掌（大面积）= 擦"，后来用户定了**动态橡皮**（按速度定大小）：
/// "什么时候擦"由**三指按住**回答（任何多点屏都能用）、"擦多大"由动态橡皮回答——
/// 于是 手掌擦 / 灵敏度 / 标定 / 四边红外 / 触摸倍数 / 橡皮绑定 **整条链都不要了**。
/// 接触面积只在**触点诊断浮层**（`--touchhud`）里当读数显示，不进这里。
/// （`PalmErase` / `PalmFactor` / `ThreeFingerErase` 这几个字段留着只为让上层先编过，
///   下一批把设置与界面收敛成"一个三档控件"时一起删。）
///
/// ## 两条纪律
/// ① **一次手势只能"干净地开始"**：触点要在 <see cref="CleanMs"/> 里一起落下、
///   而且笔画还没真画出去（路径 ≤ <see cref="CleanMoveLogical"/>）——写字中途蹭到的
///   手指一律 <see cref="TouchVerdict.Ignore"/>（"第二根手指不许抢笔"那条守卫继续保底）。
/// ② **"档位"= 两个布尔**（全开 / 单指拖动 / 只写字）：
///   `<see cref="Enabled"/>` = 手势总闸、`<see cref="SingleFingerRoam"/>` = 单指拖动。
///   下一批把它做成一个三档控件（设置里只留这一个）。
/// </summary>
internal sealed class TouchGestures
{
    // ---- 旋钮 ----
    /// <summary>手势总闸（"只写字"档 = false）。</summary>
    public bool Enabled = true;
    /// <summary>单指拖动档（手指只用来移动画布；给只报一个触点的屏用）。</summary>
    public bool SingleFingerRoam = false;
    /// <summary>长按 = 框选 / 点选（默认开，不提供关）。</summary>
    public bool LongPressSelect = true;
    /// <summary>两指轻点 = 点选（默认开，不提供关）。</summary>
    public bool TwoFingerTapSelect = true;

    // ---- 已废弃（面积链删掉后不再使用；留着只为上层编译过渡，下一批清掉）----
    public bool PalmErase = false;
    public float PalmFactor = 3f;
    public bool ThreeFingerErase = true;

    // ---- 常量 ----
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
        public float Size;          // 接触面积（长边，物理像素；0 = 屏不报）——**只给诊断用**
        public double DownMs;
        public bool InWindow;       // 落下时在"一起落下"窗口里
        public bool Moved;          // 路径超过 CleanMoveLogical
        public float Path;          // 走过的路程
    }

    private readonly List<Contact> _c = new();
    private double _gestureStartMs;  // 这一轮手势的起点（全部抬起时清）

    public readonly record struct ContactView(uint Id, Vector2 Pos, Vector2 Down,
                                              float Size, float Path, bool Moved);
    public readonly record struct PairView(uint IdA, Vector2 A, uint IdB, Vector2 B);

    /// <summary>当前触点（只读；Engine 拿它算双指增量）。</summary>
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

    public int Count => _c.Count;
    public bool Any => _c.Count > 0;
    public int MaxSeen { get; private set; }      // 这块屏最多同时报过几个触点（诊断用）
    public bool SawArea { get; private set; }     // 这块屏报过非零面积吗（**只给诊断**）
    public bool LongPressFired { get; private set; }
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
    /// 触点落下 → 定角色（"干净开始"规则）。`sizePx` 只用来给诊断记数，**不参与判定**。
    /// </summary>
    public TouchVerdict Down(uint id, float x, float y, float sizePx, double nowMs, float dpi)
    {
        _c.RemoveAll(k => k.Id == id);          // 防"TouchUp 丢了 → 永远多一根手指"
        if (_c.Count == 0) _gestureStartMs = nowMs;

        bool inWindow = nowMs - _gestureStartMs <= CleanMs;
        var c = new Contact
        {
            Id = id, Pos = new Vector2(x, y), Down = new Vector2(x, y),
            Size = sizePx, DownMs = nowMs, InWindow = inWindow,
        };
        _c.Add(c);
        if (_c.Count > MaxSeen) MaxSeen = _c.Count;
        if (sizePx > 0f) SawArea = true;        // 诊断读数

        // ---- 角色判定 ----
        // "干净开始"= 时间（150ms 窗口）+ 位移（第一根手指还没画出去）**两条一起**。
        // 少了位移那条，"写字写到一半、第二根手指很快蹭上来"会被当成手势、把字撤掉。
        bool clean = inWindow && (_c.Count == 1 || !_c[0].Moved);
        if (!clean) return _c.Count == 1 ? SingleVerdict() : TouchVerdict.Ignore;

        if (_c.Count >= 3) return TouchVerdict.Erase;      // ≥3 指一起落下 = 擦（不依赖面积）
        if (_c.Count == 2) return Enabled ? TouchVerdict.Gesture2 : TouchVerdict.Ignore;
        return SingleVerdict();
    }

    private TouchVerdict SingleVerdict()
        => SingleFingerRoam ? TouchVerdict.Roam : TouchVerdict.Write;

    /// <summary>触点移动：更新路径 / 位置。</summary>
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
    /// 40ms 心跳（引擎在笔画进行中本来就有一颗定时器）：长按判定。
    /// 判据和"点一下不留墨"同一个数（路径 ≤8 逻辑像素），所以**和停顿成型互斥**：
    /// 停顿成型要"已经画出东西"（点数 ≥3 + 识别器最短长度），长按要"从头到尾没画出去"。
    /// </summary>
    public void Tick(double nowMs, float dpi)
    {
        if (_c.Count != 1) return;
        var c = _c[0];
        if (c.Moved || !LongPressSelect) return;
        if (nowMs - c.DownMs < LongPressMs) return;
        if (c.Path > TapSlopLogical * dpi) return;
        if (LongPressFired) return;
        LongPressFired = true;
        Console.WriteLine("触摸：长按成立 → 选择（框选 / 点选）");
    }

    /// <summary>两指点按（松手时问一次）：两个触点都没动、都刚落下。</summary>
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

    /// <summary>触点的包围盒（画布坐标），外扩一圈。</summary>
    public RectF ContactBounds(float inflate)
    {
        var r = RectF.Empty;
        foreach (var k in _c) r.Add(k.Pos.X, k.Pos.Y);
        if (r.IsEmpty) return r;
        return r.Inflate(inflate);
    }

    /// <summary>
    /// 三指擦的橡皮半宽（**固定尺寸**）：动态橡皮（P1，按速度定大小）上线前先给一个够用的固定值，
    /// 夹在调用方给的上下限里（面积不参与）。
    /// </summary>
    public float EraseHalfWidth(float dpi, float minLogical, float maxLogical)
        => Math.Clamp(20f * dpi, minLogical * dpi, maxLogical * dpi);
}
