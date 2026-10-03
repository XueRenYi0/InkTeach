using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 一个指针采样点。**压感与倾角都可能是"设备根本不报"**，所以这里用
/// <see cref="HasPressure"/> 把"没有压感"和"压力为 0"分开——这两件事以前是混着的
/// （老写法 `pressure &lt;= 0.01 → 0.5`）。
/// </summary>
internal struct PenSample
{
    public float X, Y;        // 屏幕像素（虚桌面坐标）
    public float Pressure;    // 0..1；HasPressure=false 时是占位中值，不代表真实压力
    public int TiltX, TiltY;  // -90..+90（设备不报时是 0）
    public uint Rotation;     // 0..359（笔身扭转）
    public bool HasPressure;  // penMask & PEN_MASK_PRESSURE
    public bool InContact;    // POINTER_FLAG_INCONTACT
    public double TimeMs;     // 换算到引擎时钟的毫秒（用于速度和预测）
}

/// <summary>
/// 读一条指针消息里的**全部**采样点。
///
/// 为什么需要它：系统会把来不及投递的移动**合并**成一条消息，只取最新那一点
/// 等于把采样率砍半（我们自己实测过：注入 140 Hz，应用只收到约 60 条消息）。
/// 官方给的接口是 <c>GetPointerPenInfoHistory</c>，它一次返回这条消息里所有被合并的输入。
///
/// 三个必须照做的细节（都来自官方 Remarks）：
/// 1. **倒序**返回（最新的在第一行）→ 这里反转成时间序；
/// 2. 必须在**当前这条消息**还"新鲜"的时候取（我们的 WndProc 里同步取，天然满足）；
/// 3. 缓冲不够时函数**成功**但只给最近的几条 → 先按 historyCount 开缓冲，这里固定 64 条够用。
/// </summary>
internal sealed class PenSampleBuffer
{
    public const int MaxSamples = 64;

    private readonly PenSample[] _items = new PenSample[MaxSamples];
    private readonly Native.POINTER_PEN_INFO[] _penInfo = new Native.POINTER_PEN_INFO[MaxSamples];

    /// <summary>本次读到的采样点数（时间序）。</summary>
    public int Count { get; private set; }

    /// <summary>系统报的合并点数（≥ Count；等于"本来该有几条消息"）。</summary>
    public int HistoryCount { get; private set; }

    /// <summary>这批点里有没有任何一点带**有效**压感（设备级判断，不是看数值）。</summary>
    public bool AnyPressure { get; private set; }

    /// <summary>最近一点的倾角/旋转有没有有效值。</summary>
    public bool HasTilt { get; private set; }

    /// <summary>
    /// **缺压回填**的基准：个别事件不带 `PEN_MASK_PRESSURE` 时沿用上一次有效压力
    /// （Xournal++ 压力管线口径：*"Use the last recorded pressure value then"*）。
    /// 起笔时由 <see cref="BeginStroke"/> 复位成 0.5——设备从不报压力时行为与以前逐字一致。
    /// </summary>
    private float _lastValidPressure = 0.5f;

    /// <summary>起笔时调用：把缺压回填的基准复位（新一笔不该继承上一笔的压力）。</summary>
    public void BeginStroke() => _lastValidPressure = 0.5f;

    /// <summary>相邻采样点的平均间隔（毫秒）；只有一个点时是 0。</summary>
    public double MeanIntervalMs { get; private set; }

    /// <summary>失败原因（诊断用）。</summary>
    public string Note { get; private set; } = "";

    public PenSample this[int i] => _items[i];
    public PenSample Last => _items[Count - 1];

    /// <summary>
    /// 读一条 PT_PEN 消息的全部采样点。
    /// </summary>
    /// <param name="pointerId">WM_POINTER 消息里的 pointer id</param>
    /// <param name="nowMs">引擎时钟里"当前这一刻"（最新一点就锚在这里）</param>
    /// <param name="msgQpc">收到这条消息时的 QPC（用来判断硬件时标可不可信）</param>
    /// <returns>读到的点数；0 表示这次没法用（调用方退回逐点的老路）</returns>
    public int Read(uint pointerId, double nowMs, ulong msgQpc)
    {
        Count = 0;
        HistoryCount = 0;
        AnyPressure = false;
        HasTilt = false;
        MeanIntervalMs = 0;
        Note = "";

        uint entries = MaxSamples;
        if (!Native.GetPointerPenInfoHistory(pointerId, ref entries, _penInfo))
        {
            Note = "GetPointerPenInfoHistory 失败 err=" + Marshal.GetLastWin32Error();
            return 0;
        }

        HistoryCount = (int)entries;
        int n = (int)Math.Min(entries, (uint)MaxSamples);
        if (n <= 0) { Note = "没有采样点"; return 0; }

        // 倒序 → 时间序：数组里 [0] 是最近的，我们要 [0] 是最早的。
        for (int i = 0; i < n; i++)
        {
            ref readonly var src = ref _penInfo[n - 1 - i];
            ref var dst = ref _items[i];

            // D1 亚像素（`--himetric`）：优先用 himetric 映射出小数像素；
            // 设备不报/拿不到设备矩形时逐点退回整数像素（行为与以前一致）。
            int pixX = src.pointerInfo.ptPixelLocationX;
            int pixY = src.pointerInfo.ptPixelLocationY;
            if (!InputPrecision.TryMap(src.pointerInfo.sourceDevice,
                                       src.pointerInfo.ptHimetricLocationX,
                                       src.pointerInfo.ptHimetricLocationY,
                                       pixX, pixY, out float mappedX, out float mappedY))
            {
                mappedX = pixX;
                mappedY = pixY;
            }
            dst.X = mappedX;
            dst.Y = mappedY;
            dst.HasPressure = (src.penMask & Native.PEN_MASK_PRESSURE) != 0;
            dst.InContact = (src.pointerInfo.pointerFlags & Native.POINTER_FLAG_INCONTACT) != 0;
            if (dst.HasPressure)
            {
                dst.Pressure = Math.Clamp(src.pressure / 1024f, 0f, 1f);
                _lastValidPressure = dst.Pressure;
            }
            else
            {
                // 缺压回填（Xournal++ 口径）：沿用上一次有效压力，而不是给 0.5 让笔画中间凹一下。
                dst.Pressure = _lastValidPressure;
            }
            dst.Rotation = (src.penMask & Native.PEN_MASK_ROTATION) != 0 ? src.rotation : 0;
            dst.TiltX = (src.penMask & Native.PEN_MASK_TILT_X) != 0 ? src.tiltX : 0;
            dst.TiltY = (src.penMask & Native.PEN_MASK_TILT_Y) != 0 ? src.tiltY : 0;
            dst.TimeMs = nowMs;

            AnyPressure |= dst.HasPressure;
            HasTilt |= dst.Rotation != 0 || dst.TiltX != 0 || dst.TiltY != 0;
        }

        // 时间：用每一点自带的硬件时标（QPC）算相对间隔，最新一点锚在 nowMs。
        // 时标不可信（合成输入常见）时全部给同一个时间——预测器会因为"间隔为 0"自动放弃预测。
        ulong newest = _penInfo[0].pointerInfo.PerformanceCount;
        bool stampsOk = StampUsable(newest, msgQpc);
        if (stampsOk)
        {
            for (int i = 0; i < n; i++)
            {
                ulong stamp = _penInfo[n - 1 - i].pointerInfo.PerformanceCount;
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
        else
        {
            Note = "硬件时标不可用（间隔未知）";
        }

        Count = n;
        return n;
    }

    /// <summary>
    /// 和引擎里那条延时探针同一个口径：非零、不晚于收到时刻、且在 2 秒以内。
    /// （合成输入上它可能是 0，直接减会得到荒谬的数。）
    /// </summary>
    private static bool StampUsable(ulong stamp, ulong msgQpc)
        => stamp != 0 && msgQpc != 0 && stamp <= msgQpc && (msgQpc - stamp) * Qpc.TicksToMs < 2000.0;
}
