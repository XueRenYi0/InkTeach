using System.Numerics;

namespace InkEngine;

/// <summary>
/// **中心线曲线化**：过点的 centripetal Catmull-Rom ＋ **角点保护**（2026-09-28 新增）。
///
/// 它解决的事：我们画墨迹一直是"采样点 → 直线段"（<see cref="Stroke.BuildCenterline"/> 里
/// 逐点 `AddLine`），点稀的时候（手写板关掉 Windows Ink、鼠标、触摸）折角肉眼可见。
/// 这条把相邻点之间换成三次贝塞尔——**曲线严格经过每一个采样点**，所以形状没有被
/// "换成另一条"；急转处由角点保护切断切线，**直角、顿笔、尖角原样保留**。
///
/// 与 2026-09-25 被用户取消的"保形平滑"**不是一件事**，别混：
///   · 那个是 Schneider **近似拟合**（不过点），还会在"认不出图形"时悄悄替换曲线，
///     所以画抛物线会被改形（`CurveFit.cs` 文件头有完整记录）；
///   · 这个是**过点插值** ＋ 显式角点保护，存储的点一个都不动（纯渲染期加工）。
///
/// 参数（都从命令行来，见 Engine.Run 的 `--smooth` / `--smoothcorner`）：
///   · <see cref="Enabled"/>：默认**关**。这是一次观感实验，对比过再决定默认值；
///   · <see cref="CornerAngleDeg"/>：转角超过它就算角点、切线在那里切断；
///   · <see cref="CornerWindow"/>：角点判定看几个点（专治"慢画时直角被摊到好几个点上"）。
///
/// 调用方式（流式，避免中间分配）：
/// <code>
/// StrokeSmoothing.Begin();
/// foreach (点) StrokeSmoothing.Add(x, y, pressure);
/// int n = StrokeSmoothing.Finish();      // 段数；0 = 点不够，调用方退回折线
/// var segs = StrokeSmoothing.Out;        // 第 k 段连接 PointAt(k) → PointAt(k+1)
/// </code>
/// 缓冲是**静态复用**的：引擎的渲染是单线程、非重入（同一时刻只有一条笔在拼几何），
/// 所以不按笔分配。谁要是以后把渲染搬多线程，这里必须改成每线程一份。
/// </summary>
internal static class StrokeSmoothing
{
    // ---- 开关与参数（命令行）--------------------------------------------------

    /// <summary>
    /// 曲线化要不要生效。**2026-09-28 起默认开**（用户拍板："默认开也没关系"）；
    /// `--nosmooth` 退回折线，专门用来做"开 / 关"对照。
    /// </summary>
    public static bool Enabled = true;

    /// <summary>转角 ≥ 它 → 角点（切线切断，直角/尖角保住）。`--smoothcorner N` 调。</summary>
    public static float CornerAngleDeg = 35f;

    /// <summary>角点判定往两边各看几个点。2 是为了兜"慢画时一个直角被摊到四五个点上"。</summary>
    public static int CornerWindow = 2;

    /// <summary>
    /// **窗口判据的臂长上限**（画布像素；`--smoothcornerpx N` 调，`0` = 关闭本护栏）。
    ///
    /// 立这条是因为下面这条判据的**前提是"点密"**，而它的窗口却按**点数**算：
    ///
    ///   窗口转角 = ±<see cref="CornerWindow"/> 个点夹出来的两段弦的夹角。
    ///   圆弧上这个角 ≈ **2 × (点距 / 半径)** ——
    ///   所以**点距越大，量出来的转角越大**。点距超过 0.305×半径时它就超过 35°，
    ///   于是**正常圆弧的每个点都被判成角点**，切线处处切断 → 整条笔迹退化成折线。
    ///
    /// 这正是"快速画圆变折线"的成因：Windows Ink 关掉 / 快速挥笔 → 采样点稀 → 中招。
    /// （作者已在 `CornerMacroPx` 那条注释里记录过同一失效模式，但那条只覆盖
    ///   新加的宏观窗；基础这条 ±点数窗口一直没设上限。）
    ///
    /// 为什么"上限"就是对的修法：这条判据存在的理由是"**慢画**时一个直角被摊到
    /// 四五个点上，只看相邻两点每段只有十几度、判不出来"——那是点密的场景。
    /// 点稀的时候，相邻两点本身就是长臂（每段转角就很大），角点由局部判据①直接
    /// 得出，根本不需要②式的外推；此时再拿 ±2 点去跨一段长弧，只会把圆弧误判成角。
    ///
    /// 默认 24px：点距 ≤12px 时窗口判据照常生效（保持"慢画直角"的行为不变），
    /// 点距更大时让位给局部判据。
    /// </summary>
    public static float CornerWindowMaxPx = 24f;

    /// <summary>
    /// **像素宏观窗**（2026-10-04 追加；`--smoothmacropx N` 调，**默认 0 = 关**）。
    ///
    /// ⚠ 默认关的原因（实测踩过）：24px 臂在**小半径笔画**上会超过角点阈值（半径 20px 时
    /// 弧长约 69°），正常弯曲被误判成角点 → 过点曲线被切断 → 细笔放大后"很脏、不流畅"。
    /// 它是为"稀疏采样（鼠标）下的真直角"准备的实验口径（参考 Inkclass 的 24px，只参考思路）；
    /// 要试就用 `--smoothmacropx 24`，并配合 `--smoothtest` 与真笔一起看。
    /// </summary>
    public static float CornerMacroPx = 0f;

    /// <summary>自检用的反例开关：关掉角点保护，直角会被磨圆（用来证明保护真的在起作用）。</summary>
    public static bool CornerProtection = true;

    /// <summary>
    /// 设置变更计数。`Stroke.BuildGeometry` 的几何缓存把它算进缓存键——
    /// 不然运行时切换开关，已经缓存的笔迹不会重画（出对照图时会两张开成一样）。
    /// </summary>
    public static int Version { get; private set; }

    public static void SetEnabled(bool on)
    {
        if (Enabled == on) return;
        Enabled = on;
        Version++;
    }

    public static void BumpVersion() => Version++;

    // ---- 常量（都用真实笔迹与几何退化情形试过的取值）--------------------------

    /// <summary>两点近到这个距离（px）就当重复点扔掉（按下瞬间与慢速时会连着报同坐标）。</summary>
    private const float DedupPx = 0.25f;

    /// <summary>角点判定的两条"臂"都短于它就不算角点（抖出来的小折角不是角）。</summary>
    private const float MinArmPx = 1.0f;

    /// <summary>控制点到端点的最大长度 = 弦长 × 它（防过冲：宁可少弯一点，不许甩出去）。</summary>
    private const float MaxCtrlChordRatio = 0.5f;

    /// <summary>抽稀：只有"这一个点相对上一个保留点的转角小于它、且离得近"才允许丢。</summary>
    private const float DecimateTurnDeg = 1.5f;

    /// <summary>抽稀：离上一个保留点超过它就必须保留（保证长直线也有足够的控制点）。</summary>
    private const float DecimateStepPx = 6f;

    /// <summary>一段三次贝塞尔。P0/P1 **严格落在**输入点上（曲线过点）。</summary>
    internal struct Seg
    {
        public Vector2 P0, C1, C2, P1;
        /// <summary>两端点的压力（0..1）。常宽路不读它，压感路用它插值半径。</summary>
        public float R0, R1;
    }

    // ---- 缓冲（静态复用，见类头说明）-----------------------------------------

    private static Vector2[] _in = new Vector2[1024];
    private static float[] _inR = new float[1024];
    private static int _n;

    private static Vector2[] _p = new Vector2[1024];
    private static float[] _pr = new float[1024];
    private static float[] _t = new float[1024];
    private static bool[] _corner = new bool[1024];
    private static int _m;

    private static Seg[] _out = new Seg[1024];

    /// <summary>去重/抽稀之后还剩几个点。</summary>
    public static int PointCount => _m;
    public static Vector2 PointAt(int i) => _p[i];
    public static float PressureAt(int i) => _pr[i];
    /// <summary>输出段（Finish 之后有效）：第 k 段 = PointAt(k) → PointAt(k+1)。</summary>
    public static Seg[] Out => _out;

    public static void Begin() => _n = 0;

    public static void Add(float x, float y, float pressure)
    {
        if (_n == _in.Length)
        {
            Array.Resize(ref _in, _n * 2);
            Array.Resize(ref _inR, _n * 2);
        }
        _in[_n] = new Vector2(x, y);
        _inR[_n] = pressure;
        _n++;
    }

    /// <summary>
    /// 收尾并算出所有曲线段。返回段数；**0 = 别用曲线**（点不够），调用方退回折线。
    /// </summary>
    public static int Finish()
    {
        _m = 0;
        if (_n < 2) return 0;

        Dedup();
        if (_m < 2) return 0;

        MarkCorners();
        Decimate();                 // 只丢"近似直线且步长小"的点，角点一个不丢
        if (_m < 2) return 0;

        Knots();                    // 抽稀之后重算弦长（centripetal 参数化用它）
        Build();
        return _m - 1;
    }

    // ---- 各步 ----------------------------------------------------------------

    /// <summary>去重：相邻重复点合并（压力取大的那个——轻按的点不该吃掉重按的）。</summary>
    private static void Dedup()
    {
        for (int i = 0; i < _n; i++)
        {
            if (_m > 0 && Vector2.DistanceSquared(_in[i], _p[_m - 1]) < DedupPx * DedupPx)
            {
                if (_inR[i] > _pr[_m - 1]) _pr[_m - 1] = _inR[i];
                continue;
            }
            if (_m == _p.Length) GrowP(_m * 2);
            _p[_m] = _in[i];
            _pr[_m] = _inR[i];
            _m++;
        }
    }

    /// <summary>
    /// 角点判定，**两条判据取"或"**（2026-09-28 出图时发现只留窗口会漏掉锯齿）：
    ///
    ///   ① **局部**（相邻两点）：锯齿、方波、快速的直角——转角集中在一点上；
    ///   ② **窗口**（±<see cref="CornerWindow"/> 个点）：慢画时一个直角被摊到
    ///      四五个点上，只看相邻两点每个都只有十几度、判不出来，会把它磨圆——
    ///      这正是用户不愿意要的。
    ///
    /// 只用①会漏"慢画的直角"，只用②会把周期 2 的锯齿看成直线（±2 个点正好跨过一个
    /// 周期、方向又对上了），两个都要，所以取或。
    /// </summary>
    private static void MarkCorners()
    {
        for (int i = 0; i < _m; i++) _corner[i] = false;
        if (!CornerProtection) return;

        float cosThr = MathF.Cos(CornerAngleDeg * MathF.PI / 180f);
        int w = Math.Max(1, CornerWindow);

        // ③ 像素宏观窗：按全条平均点距把 CornerMacroPx 折成点数（1..16，防止病态输入）。
        int wMacro = 0;
        if (CornerMacroPx > 0 && _m >= 3)
        {
            double len = 0;
            for (int i = 1; i < _m; i++) len += Vector2.Distance(_p[i], _p[i - 1]);
            float spacing = (float)(len / (_m - 1));
            if (spacing > 1e-6f)
                wMacro = Math.Clamp((int)MathF.Round(CornerMacroPx / spacing), 1, 16);
        }

        for (int i = 1; i < _m - 1; i++)
        {
            // ① 局部转角
            if (TurnAt(i - 1, i, i + 1) >= CornerAngleDeg) { _corner[i] = true; continue; }

            // ② 像素宏观窗（新）：点密/点稀都落在同一个"实际距离"尺度上
            if (wMacro > 0)
            {
                int am = Math.Max(0, i - wMacro);
                int bm = Math.Min(_m - 1, i + wMacro);
                Vector2 vam = _p[i] - _p[am];
                Vector2 vbm = _p[bm] - _p[i];
                float lam = vam.Length(), lbm = vbm.Length();
                if (lam >= MinArmPx && lbm >= MinArmPx)
                {
                    float cosm = Vector2.Dot(vam, vbm) / (lam * lbm);
                    if (cosm <= cosThr) { _corner[i] = true; continue; }
                }
            }

            // ③ 窗口转角（原有的 ±CornerWindow 点）
            int a = Math.Max(0, i - w);
            int b = Math.Min(_m - 1, i + w);
            Vector2 va = _p[i] - _p[a];
            Vector2 vb = _p[b] - _p[i];
            float la = va.Length(), lb = vb.Length();
            if (la < MinArmPx || lb < MinArmPx) continue;
            // [护栏] 臂太长 = 点太稀：这条判据是给"点密、直角被摊开"用的（见
            // CornerWindowMaxPx 的说明）。点稀时相邻两点本身就是长臂，角点由 ①
            // 直接判出；再用 ±2 点跨一段长弧去量，会把正常圆弧的累计转角量成
            // 超阈值 → 每个点都判角 → 整条退化成折线（快速画圆就是这个）。
            if (CornerWindowMaxPx > 0f && (la > CornerWindowMaxPx || lb > CornerWindowMaxPx))
                continue;
            float cos = Vector2.Dot(va, vb) / (la * lb);
            if (cos <= cosThr) _corner[i] = true;
        }
    }

    /// <summary>在 <paramref name="i"/> 处的局部转角（度）；臂太短返回 0（不算角）。</summary>
    private static float TurnAt(int a, int i, int b)
    {
        Vector2 va = _p[i] - _p[a];
        Vector2 vb = _p[b] - _p[i];
        float la = va.Length(), lb = vb.Length();
        if (la < MinArmPx || lb < MinArmPx) return 0f;
        float cos = Math.Clamp(Vector2.Dot(va, vb) / (la * lb), -1f, 1f);
        return MathF.Acos(cos) * (180f / MathF.PI);
    }

    /// <summary>
    /// 抽稀：只在"几乎是直线"的地方丢点（转角 &lt; <see cref="DecimateTurnDeg"/> 且离
    /// 上一个保留点 &lt; <see cref="DecimateStepPx"/>）。目的是别让一条两千点的长笔
    /// 每帧都去建两千段贝塞尔——弯曲处一个点都不许少。
    /// </summary>
    private static void Decimate()
    {
        if (_m < 3) return;
        int w = 1;                                  // _p[0] 必留
        for (int i = 1; i < _m - 1; i++)
        {
            bool keep = _corner[i];
            if (!keep)
            {
                if (Vector2.Distance(_p[i], _p[w - 1]) >= DecimateStepPx) keep = true;
                else
                {
                    Vector2 a = _p[i] - _p[w - 1];
                    Vector2 b = _p[i + 1] - _p[i];
                    float la = a.Length(), lb = b.Length();
                    if (la > 1e-6f && lb > 1e-6f)
                    {
                        float cos = Vector2.Dot(a, b) / (la * lb);
                        float deg = MathF.Acos(Math.Clamp(cos, -1f, 1f)) * (180f / MathF.PI);
                        if (deg >= DecimateTurnDeg) keep = true;
                    }
                }
            }
            if (keep)
            {
                if (w != i)
                {
                    _p[w] = _p[i];
                    _pr[w] = _pr[i];
                    _corner[w] = _corner[i];
                }
                w++;
            }
        }
        // 末点必留（它是笔的"现在"，少了会短一截）
        if (w != _m - 1) { _p[w] = _p[_m - 1]; _pr[w] = _pr[_m - 1]; _corner[w] = false; }
        _m = w + 1;
    }

    /// <summary>centripetal 参数化的累积弦长（开方，见 Yüksel 那篇的参数化结论）。</summary>
    private static void Knots()
    {
        _t[0] = 0f;
        for (int i = 1; i < _m; i++)
            _t[i] = _t[i - 1] + MathF.Sqrt(Vector2.Distance(_p[i], _p[i - 1]));
    }

    /// <summary>逐段生成三次贝塞尔；角点处切线切断（两侧各自沿弦方向）。</summary>
    private static void Build()
    {
        int need = _m - 1;
        if (_out.Length < need) Array.Resize(ref _out, Math.Max(need, _out.Length * 2));

        for (int i = 0; i < need; i++)
        {
            Vector2 d = _p[i + 1] - _p[i];
            Vector2 c1 = _corner[i] ? _p[i] + d / 3f : _p[i] + TravelOffset(i, i - 1, i + 1);
            Vector2 c2 = _corner[i + 1] ? _p[i + 1] - d / 3f : _p[i + 1] - TravelOffset(i + 1, i, i + 2);
            _out[i].P0 = _p[i];
            _out[i].C1 = c1;
            _out[i].C2 = c2;
            _out[i].P1 = _p[i + 1];
            _out[i].R0 = _pr[i];
            _out[i].R1 = _pr[i + 1];
        }
    }

    /// <summary>
    /// 点 <paramref name="at"/> 处、沿行进方向的切线控制偏移（未除 3 的那一半由这里
    /// 乘上 <c>dtn / 3</c> 完成）。非均匀（centripetal）Catmull-Rom 的标准式：
    /// <code>
    /// m = (P - Pprev)/dtp - (Pnext - Pprev)/(dtp+dtn) + (Pnext - P)/dtn
    /// 偏移 = m × dtn / 3
    /// </code>
    /// 端点（没有 prev 或 next）与退化间隔一律退回"沿弦的 1/3"，不会甩出去。
    /// </summary>
    private static Vector2 TravelOffset(int at, int prev, int next)
    {
        if (prev < 0) return (_p[next] - _p[at]) / 3f;
        if (next >= _m) return (_p[at] - _p[prev]) / 3f;

        float dtp = _t[at] - _t[prev];
        float dtn = _t[next] - _t[at];
        if (dtp <= 1e-5f || dtn <= 1e-5f) return (_p[next] - _p[at]) / 3f;

        Vector2 m = (_p[at] - _p[prev]) / dtp
                  - (_p[next] - _p[prev]) / (dtp + dtn)
                  + (_p[next] - _p[at]) / dtn;
        Vector2 off = m * (dtn / 3f);

        // 限幅：控制点不许超过弦长的一半（急转 + 疏采样时 CR 会想伸很远）
        float chord = Vector2.Distance(_p[at], _p[next]);
        float len = off.Length();
        float max = chord * MaxCtrlChordRatio;
        if (len > max && len > 1e-6f) off *= max / len;
        return off;
    }

    private static void GrowP(int need)
    {
        Array.Resize(ref _p, need);
        Array.Resize(ref _pr, need);
        Array.Resize(ref _t, need);
        Array.Resize(ref _corner, need);
    }
}
