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
    Radial = 7,     // 两指长按 / 轻点呼出的轮盘（方向由触点平均位喂，松手确认）
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
/// ## 面积（`rcContact`）：**只用于手掌擦，而且是相对的**（2026-10-05 定）
///
/// 手掌 = 接触尺寸 > **这台机器自己的手指基线** × 倍数（默认 3；灵敏度三档 4/3/2）。
/// 基线是运行中学的、只喂"判定为手指"的样本——所以跨屏 / 跨机器都**不用标定**：
/// 比的是同一块屏上"手掌 vs 手指"的相对大小（用户实测：学校大屏在隔壁 Ink-Canvas 里
/// 要乘 0.25 倍，量纲完全不通——相对判定就不怕这个）。
/// 屏不报面积（`touchMask` 没置位）→ 手掌擦自动失效，其它手势照常。
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
    /// <summary>手势**总开关**（"保险丝"；关掉只剩单指书写）。界面在「设置 → 书写 → 触摸手势」，
    /// 命令行后门 `--notouch`。默认为开；每条判定都各自读它——见 <see cref="Down"/> / <see cref="Tick"/> 等。</summary>
    public bool Enabled = true;
    /// <summary>单指拖动档（手指只用来移动画布；给只报一个触点的屏用）。</summary>
    public bool SingleFingerRoam = false;
    /// <summary>长按 = 框选 / 点选（默认开，不提供关）。</summary>
    public bool LongPressSelect = true;
    /// <summary>两指轻点 = 点选（默认开，不提供关）。</summary>
    public bool TwoFingerTapSelect = true;
    /// <summary>两指长按 = 呼出盘（2026-10-05；默认开，不提供关——轮盘的触摸入口）。</summary>
    public bool TwoFingerHold = true;

    // ---- 手掌擦（2026-10-05 恢复启用；判据=**相对基线**，绝不写死绝对值）----
    /// <summary>手掌（大面积）擦：默认开。判据是"明显大于手指基线"，量纲跨屏不通也不影响。</summary>
    public bool PalmErase = true;
    /// <summary>手掌判定倍数：接触尺寸 > 手指基线 × 它 = 手掌（4=保守 / 3=标准 / 2=灵敏）。</summary>
    public float PalmFactor = 3f;
    /// <summary>三指擦（默认开）。</summary>
    public bool ThreeFingerErase = true;

    // ---- 常量 ----
    public const double CleanMs = 150;          // "一起落下"的时间窗
    public const float CleanMoveLogical = 10f;  // 还没真画出去（逻辑像素）
    public const double LongPressMs = 500;      // > 停顿成型的 400ms
    public const double TwoFingerHoldMs = 500;  // 两指长按 = 呼出盘（和单指长按同一个时长，触点数天然分开）
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

    // ---- 手掌擦的"相对指纹"（2026-10-05）----
    //
    // 为什么不能用绝对阈值：不同屏上报的"接触尺寸"量纲不通、跨机器不可移植
    // （用户实测：学校大屏在隔壁 Ink-Canvas 里要乘 **0.25**；上游 issue #112 里还有要乘 **>1** 的框）。
    // 做法：**这台机器自己的手指基线** = 最近"非手掌"单指尺寸的 EMA；手掌 = 明显大于基线。
    // 基线只喂"判定为手指"的样本，所以手掌不会把它越带越大；换机器自然重新学。
    private float _fingerBaseline;        // 物理像素（0 = 还没学到）
    public bool LastDownWasPalm { get; private set; }   // 这一次 Down 是手掌吗（引擎据此"按下先不擦"）

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
    public bool TwoFingerHoldFired { get; private set; }
    public void ClearLongPress() { LongPressFired = false; TwoFingerHoldFired = false; }

    public void Reset()
    {
        _c.Clear();
        LongPressFired = false;
        TwoFingerHoldFired = false;
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

        // ---- 手掌擦：**相对基线**判定（放在角色判定之前；手掌不参与"干净开始"那套）----
        LastDownWasPalm = false;
        if (Enabled && PalmErase && sizePx > 0f)
        {
            if (_fingerBaseline <= 0f)
            {
                // 还没基线：这一下当手指（并记下基线）。第一下就是手掌的概率低；
                // 就算误判也只是多写一笔，一撤就回去。
                _fingerBaseline = sizePx;
            }
            else if (sizePx > _fingerBaseline * PalmFactor)
            {
                LastDownWasPalm = true;
                // **单掌落下 = 擦**（"按下先不擦、移动才擦"由引擎做，见 TouchDownDispatch 的 Erase 分支）。
                // 已经有别的触点在按（写字中途蹭上来的手掌 / 掌根）→ **忽略**：不许抢笔、也不许乱擦。
                return _c.Count == 1 ? TouchVerdict.Erase : TouchVerdict.Ignore;
            }
            else
            {
                // 只喂"不是手掌"的样本（手掌不会把基线越带越大）。
                _fingerBaseline += (sizePx - _fingerBaseline) * 0.25f;
            }
        }

        // ---- 角色判定 ----
        // "干净开始"= 时间（150ms 窗口）+ 位移（第一根手指还没画出去）**两条一起**。
        // 少了位移那条，"写字写到一半、第二根手指很快蹭上来"会被当成手势、把字撤掉。
        bool clean = inWindow && (_c.Count == 1 || !_c[0].Moved);
        if (!clean) return _c.Count == 1 ? SingleVerdict() : TouchVerdict.Ignore;

        if (_c.Count >= 3) return Enabled ? TouchVerdict.Erase : TouchVerdict.Ignore;
        if (_c.Count == 2) return Enabled ? TouchVerdict.Gesture2 : TouchVerdict.Ignore;
        return SingleVerdict();
    }

    private TouchVerdict SingleVerdict()
        => (Enabled && SingleFingerRoam) ? TouchVerdict.Roam : TouchVerdict.Write;

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
        if (!Enabled) return;                    // 总开关关掉：只剩单指书写，长按也不判

        // **两指长按 = 呼出盘**（2026-10-05）：两根手指都"从头到尾没动"才算。
        // 和单指长按各判各的（触点数不同，天然分开）；判据复用 TapSlop（和"点一下不留墨"同一个数）。
        if (_c.Count == 2)
        {
            if (!TwoFingerHold || TwoFingerHoldFired) return;
            if (_c[0].Moved || _c[1].Moved) return;
            if (_c[0].Path > TapSlopLogical * dpi || _c[1].Path > TapSlopLogical * dpi) return;
            if (nowMs - _c[0].DownMs < TwoFingerHoldMs || nowMs - _c[1].DownMs < TwoFingerHoldMs) return;
            TwoFingerHoldFired = true;
            Console.WriteLine("触摸：两指长按成立 → 呼出盘");
            return;
        }

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
        if (!Enabled || !TwoFingerTapSelect || _c.Count != 2) return false;
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
