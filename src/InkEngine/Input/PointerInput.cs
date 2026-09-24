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

            dst.X = src.ptPixelLocationX;
            dst.Y = src.ptPixelLocationY;
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
