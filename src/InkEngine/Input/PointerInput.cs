using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 一个**非笔**指针（鼠标 / 触摸）的采样点。
///
/// 和 <see cref="PenSample"/> 的区别只有一个：`POINTER_INFO` 里没有 `penMask`，
/// 所以既没有压感也没有倾角。为了让下游（渲染、存档）拿到的结构与笔那条路一致，
/// 这里给压力填 **0.5 占位**——这正是 WPF 对"无压感设备"的口径
/// （`StylusPoint.PressureFactor` 默认 0.5，见 调研-压感数据与墨迹预测.md 第八节），
/// 而 <see cref="HasPressure"/> 恒为 false，把"设备不报压感"这件事说清楚。
/// </summary>
internal struct PointerSample
{
    public float X, Y;        // 屏幕像素（虚桌面坐标）
    public float Pressure;    // 恒为 0.5（占位，不是真实压力）
    public bool HasPressure;  // 非笔设备恒为 false
    public bool InContact;    // POINTER_FLAG_INCONTACT
    public double TimeMs;     // 换算到引擎时钟的毫秒（用于速度和预测）
}

/// <summary>
/// 读一条**鼠标 / 触摸**消息里的**全部**采样点。
///
/// 为什么需要它：系统会把来不及投递的移动**合并**成一条消息，这条规则对鼠标和触摸
/// 一样成立。以前只有笔那条路读了合并点（见 `Input/PenInput.cs`），鼠标和触摸
/// 一条消息只取最新那一个——而"手写板没开 Windows Ink"时设备正是以 `PT_MOUSE`
/// 上报的，于是快写时轨迹被静默抽稀，预测也因为"每帧只有一个点"算不准速度。
///
/// 官方给非笔指针的接口是 <c>GetPointerInfoHistory</c>，语义与 `GetPointerPenInfoHistory`
/// 完全一致（都是"取当前这条消息里被合并的输入"、都**倒序**返回、缓冲不够时
/// **成功但只给最近的几条**），所以这里的读法照抄笔那一份。
/// </summary>
internal sealed class PointerSampleBuffer
{
    public const int MaxSamples = 64;

    private readonly PointerSample[] _items = new PointerSample[MaxSamples];
    private readonly Native.POINTER_INFO[] _info = new Native.POINTER_INFO[MaxSamples];

    /// <summary>本次读到的采样点数（时间序）。</summary>
    public int Count { get; private set; }

    /// <summary>系统报的合并点数（≥ Count；等于"本来该有几条消息"）。</summary>
    public int HistoryCount { get; private set; }

    /// <summary>相邻采样点的平均间隔（毫秒）；只有一个点时是 0。</summary>
    public double MeanIntervalMs { get; private set; }

    /// <summary>失败原因（诊断用）。</summary>
    public string Note { get; private set; } = "";

    public PointerSample this[int i] => _items[i];

    /// <summary>
    /// 读一条非笔指针消息的全部采样点。
    /// </summary>
    /// <param name="pointerId">WM_POINTER 消息里的 pointer id</param>
    /// <param name="nowMs">引擎时钟里"当前这一刻"（最新一点就锚在这里）</param>
    /// <param name="msgQpc">收到这条消息时的 QPC（用来判断硬件时标可不可信）</param>
    /// <returns>读到的点数；0 表示这次没法用（调用方退回"一个消息一个点"的老路）</returns>
    public int Read(uint pointerId, double nowMs, ulong msgQpc)
    {
        Count = 0;
        HistoryCount = 0;
        MeanIntervalMs = 0;
        Note = "";

        uint entries = MaxSamples;
        if (!Native.GetPointerInfoHistory(pointerId, ref entries, _info))
        {
            Note = "GetPointerInfoHistory 失败 err=" + Marshal.GetLastWin32Error();
            return 0;
        }

        HistoryCount = (int)entries;
        // 缓冲开小了会"成功但只给最近的几条"（官方语义），所以这里照 64 取，
        // 并且把"系统其实给了更多"这件事留在 HistoryCount 里，供调用方判断合并率。
        int n = (int)Math.Min(entries, (uint)MaxSamples);
        if (n <= 0) { Note = "没有采样点"; return 0; }
        if (entries > MaxSamples) Note = $"合并点被截断（系统报 {entries}，只取了 {MaxSamples}）";

        // 倒序 → 时间序：数组里 [0] 是最近的，我们要 [0] 是最早的。
        for (int i = 0; i < n; i++)
        {
            ref readonly var src = ref _info[n - 1 - i];
            ref var dst = ref _items[i];

            // D1 亚像素：鼠标/触摸多数不报 himetric，映射失败就退回整数像素。
            int pixX = src.ptPixelLocationX;
            int pixY = src.ptPixelLocationY;
            if (!InputPrecision.TryMap(src.sourceDevice,
                                       src.ptHimetricLocationX, src.ptHimetricLocationY,
                                       pixX, pixY, out float mappedX, out float mappedY))
            {
                mappedX = pixX;
                mappedY = pixY;
            }
            dst.X = mappedX;
            dst.Y = mappedY;
            dst.HasPressure = false;      // 非笔设备没有 penMask，也就没有压感
            dst.Pressure = 0.5f;          // 占位中值（与 WPF 的无压感设备口径一致）
            dst.InContact = (src.pointerFlags & Native.POINTER_FLAG_INCONTACT) != 0;
            dst.TimeMs = nowMs;
        }

        // 时间：尽量用每一点自带的时标算相对间隔，最新一点锚在 nowMs。
        // 时标不可信时（合成输入、部分驱动）所有点用同一个时间——同一时刻的两个点
        // 算不出速度，但**下一条消息的第一个点**与上一次之间仍有真实间隔，
        // 所以预测器照样能拿到"帧与帧之间"的速度，只是精度差一些。
        ulong newest = _info[0].PerformanceCount;
        if (StampUsable(newest, msgQpc))
        {
            for (int i = 0; i < n; i++)
            {
                ulong stamp = _info[n - 1 - i].PerformanceCount;
                _items[i].TimeMs = StampUsable(stamp, msgQpc)
                    ? nowMs - Qpc.Ms(stamp, newest)
                    : nowMs;
            }
            if (n > 1)
            {
                double span = _items[n - 1].TimeMs - _items[0].TimeMs;
                MeanIntervalMs = span > 0 ? span / (n - 1) : 0;
            }
        }
        else if (Note.Length == 0)
        {
            Note = "硬件时标不可用（间隔未知）";
        }

        Count = n;
        return n;
    }

    /// <summary>
    /// 和笔那条路（<see cref="PenSampleBuffer"/>）同一个口径：非零、不晚于收到时刻、
    /// 且在 2 秒以内。合成输入上它可能是 0，直接减会得到荒谬的数。
    /// </summary>
    private static bool StampUsable(ulong stamp, ulong msgQpc)
        => stamp != 0 && msgQpc != 0 && stamp <= msgQpc && (msgQpc - stamp) * Qpc.TicksToMs < 2000.0;
}

/// <summary>
/// 一条**原始鼠标报**的位移（相对位移，或绝对报换算出的差值），带自己的时刻。
///
/// 它进的是 <see cref="RawInputSegmentBuffer"/>：raw 报**不能**收到就立刻变成点，
/// 必须先攒着，等指针消息给出这一段的真值两端（起点锚点 + 终点）再校准发射。
/// </summary>
internal struct RawDeltaSample
{
    public float Dx, Dy;
    public double TimeMs;
}

/// <summary>
/// 原始输入（`WM_INPUT`）的**分段缓冲 + 单调几何校准**。
///
/// 为什么要有它（2026-11-22，关 ink 快写乱画的根因）：
/// `WM_INPUT`（约 200Hz）经常**超前**于合并后的 `WM_POINTERUPDATE`（约 100Hz）——
/// 先到的 raw 报把笔迹画到了 N+1，随后指针消息报出真值 N，锚点回到 N 并把笔迹往回拉，
/// 于是出现折返乱画、偶发跳点，且 raw 点用的 NowMs 会让 `Points[i].T` 倒退。
///
/// 做法（照搬"按段校准"这一类实现：光标插值都要先有区间两端才插值）：
///   1. raw 报只进 `_pending`，**不进笔画**；
///   2. 一条指针消息到达时，我们拿到区间 `[锚点, 终点]`，把 `_pending` 里
///      **落在这一段上**的那些报按线上投影发射，剩下的留给下一段；
///   3. 发射点必须沿线段严格单调前进（`0 < proj < 0.95`）、垂直偏移不超护栏、
///      与首末点保持最小距离，时间戳按投影插值且不倒退。
///
/// 这样"超前"那一包要么被压回本段内、要么留给下一段，笔迹永远只会往前走。
/// </summary>
internal sealed class RawInputSegmentBuffer
{
    /// <summary>最多攒多少条（超出丢最老的）：一段指针间隔内的 raw 报远少于这个数。</summary>
    public const int MaxPending = 64;

    private readonly List<RawDeltaSample> _pending = new(MaxPending);
    // 前缀累计位移（复用缓冲，零堆分配）。
    private readonly float[] _cumX = new float[MaxPending];
    private readonly float[] _cumY = new float[MaxPending];

    public int Count => _pending.Count;

    public void Clear() => _pending.Clear();

    public void Add(float dx, float dy, double timeMs)
    {
        if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
        _pending.Add(new RawDeltaSample { Dx = dx, Dy = dy, TimeMs = timeMs });
    }

    /// <summary>
    /// 把 `_pending` 里属于 `[start, end]` 这一段的报**校准后**发射出来。
    /// </summary>
    /// <param name="startX">段起点（屏幕坐标，来自上一条指针消息的锚点）</param>
    /// <param name="startY">同上</param>
    /// <param name="startTimeMs">段起点时刻（锚点时刻）</param>
    /// <param name="endX">段终点（屏幕坐标，本条指针消息）</param>
    /// <param name="endY">同上</param>
    /// <param name="endTimeMs">段终点时刻（本条指针消息最早那一采样点的时刻）</param>
    /// <param name="minDistPx">相邻发射点最小间距（屏幕像素）</param>
    /// <param name="output">输出缓冲（调用方复用，方法内先 Clear）</param>
    /// <returns>发射了几个点</returns>
    public int EmitBetween(float startX, float startY, double startTimeMs,
                           float endX, float endY, double endTimeMs,
                           float minDistPx, List<(float X, float Y, double TimeMs)> output)
    {
        output.Clear();
        int n = _pending.Count;
        if (n == 0) return 0;

        float vx = endX - startX, vy = endY - startY;
        float lenSq = vx * vx + vy * vy;

        // ① 前缀累计位移
        float cx = 0f, cy = 0f;
        for (int i = 0; i < n; i++)
        {
            cx += _pending[i].Dx;
            cy += _pending[i].Dy;
            _cumX[i] = cx;
            _cumY[i] = cy;
        }

        // ② 指针几乎没动：raw 攒着也没意义，攒太多就当噪声清掉。
        if (lenSq < 1f)
        {
            if (n > 8) _pending.Clear();
            return 0;
        }

        int consumeCount;
        int emitEnd;
        int splitIdx = FindSplit(n, vx, vy, lenSq);
        if (splitIdx >= 0)
        {
            float proj = (_cumX[splitIdx] * vx + _cumY[splitIdx] * vy) / lenSq;
            emitEnd = splitIdx;                       // 发射 0 .. splitIdx-1
            if (proj <= 1.35f)
            {
                // 正好这一段内到达终点：那一包本身消耗掉，其余留给下一段。
                consumeCount = splitIdx + 1;
            }
            else if (proj <= 2.5f)
            {
                // 单个大步跨过终点：把余量改写回那一包，下一段接着用。
                _pending[splitIdx] = new RawDeltaSample
                {
                    Dx = _cumX[splitIdx] - vx,
                    Dy = _cumY[splitIdx] - vy,
                    TimeMs = _pending[splitIdx].TimeMs,
                };
                consumeCount = splitIdx;
            }
            else
            {
                consumeCount = splitIdx + 1;          // 异常大跳：整包丢掉
            }
        }
        else
        {
            // 所有点累计都没到 90%：整段还在前面 → 这一段消费完，但不发射（等下一段）。
            consumeCount = n;
            float dot = _cumX[n - 1] * vx + _cumY[n - 1] * vy;
            if (dot <= 0f) return Consume(consumeCount);   // 整段反向噪声
            emitEnd = n;
        }

        EmitCalibrated(n, emitEnd, startX, startY, startTimeMs, endX, endY, endTimeMs,
                       vx, vy, lenSq, minDistPx, output);
        Consume(consumeCount);
        return output.Count;
    }

    /// <summary>按投影找"首次到达线段 90%"的那一包；没有则 -1。</summary>
    private int FindSplit(int n, float vx, float vy, float lenSq)
    {
        for (int i = 0; i < n; i++)
        {
            float proj = (_cumX[i] * vx + _cumY[i] * vy) / lenSq;
            if (proj >= 0.90f) return i;
        }
        return -1;
    }

    /// <summary>按投影插值 + 四道护栏，把段内的点发射出去。</summary>
    private void EmitCalibrated(int n, int emitEnd,
                                float startX, float startY, double startTimeMs,
                                float endX, float endY, double endTimeMs,
                                float vx, float vy, float lenSq, float minDistPx,
                                List<(float X, float Y, double TimeMs)> output)
    {
        if (emitEnd <= 0) return;
        float len = MathF.Sqrt(lenSq);
        if (!(len >= minDistPx) || len <= 0f) return;    // 段本身太短：不插值，等下一段
        float maxPerp = MathF.Max(16f, len * 0.35f);
        float minDistSq = minDistPx * minDistPx;
        float tailGuard = minDistPx * 0.5f;
        float tailGuardSq = tailGuard * tailGuard;

        float lastProj = 0f, lastX = startX, lastY = startY;
        double lastT = startTimeMs;
        for (int k = 0; k < emitEnd && k < n; k++)
        {
            float proj = (_cumX[k] * vx + _cumY[k] * vy) / lenSq;
            // 严格单调前进：不重复、不倒退，也不越过终点。
            if (proj <= lastProj + 0.05f || proj >= 0.95f) continue;
            // 垂直偏移护栏：折返/横跳造出来的噪点离这条线段很远，直接丢。
            float perp = MathF.Abs(_cumX[k] * vy - _cumY[k] * vx) / len;
            if (perp > maxPerp) continue;

            float px = startX + _cumX[k], py = startY + _cumY[k];
            // 距离护栏：离上一个发射点、离终点都别太近（零长段/贴终点会造退化段）。
            float dx = px - lastX, dy = py - lastY;
            if (dx * dx + dy * dy < minDistSq) continue;
            dx = px - endX; dy = py - endY;
            if (dx * dx + dy * dy < tailGuardSq) continue;

            // 时间戳按投影插值，并保证单调不倒退。
            double t = endTimeMs > startTimeMs
                ? startTimeMs + (endTimeMs - startTimeMs) * proj
                : endTimeMs;
            if (t < lastT) t = lastT;

            output.Add((px, py, t));
            lastProj = proj; lastX = px; lastY = py; lastT = t;
        }
    }

    /// <summary>从头部移除已消费的报，返回 0（让调用点统一 `return Consume(..)`）。</summary>
    private int Consume(int consumeCount)
    {
        if (consumeCount > 0) _pending.RemoveRange(0, Math.Min(consumeCount, _pending.Count));
        return 0;
    }
}
