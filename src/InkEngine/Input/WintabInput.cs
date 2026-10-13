using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// **Wintab 输入源**（第一阶段：**只借"压力"这一样，不借坐标**）。
///
/// <para>## 为什么需要它</para>
/// 手写板**关掉 Windows Ink** 之后，笔会被系统当成鼠标报（`PT_MOUSE`），
/// 而鼠标这条路上**永远没有压力**（非笔设备没有 `penMask`）——
/// 所以关 ink 时所有笔迹都是等宽的，和开 ink 的手感是两样东西。
///
/// Wintab 是**驱动自己的通道**，与 Windows Ink 开关无关。实测（高漫表）：
/// <code>
///   包率     168 Hz
///   压力     0 ~ 16383（14 位）
///   倾斜     有（方位角/高低角，本阶段先不用）
/// </code>
/// 对比 Windows Ink 那条路：压力官方文档明确"normalized to 0~1024" → **差 16 倍**。
///
/// <para>## 为什么第一阶段只借压力</para>
/// 坐标仍旧走既有的指针/鼠标那条路（含 Raw Input 补点）。
/// 这样几何一个字都不动——万一 Wintab 有问题，**症状只会是"压力不对"，
/// 不会把笔画画歪**，随时一行开关退回。
/// 以后再单独做"连坐标也交给 Wintab"（那样还能顺带绕开 PHRC 吞墨，见下）。
///
/// <para>## 三个必须照做的坑（都是查同行资料查到的，不是我试出来的）</para>
/// 1. **不要 `WTOpen(..., fEnable:true)`**：某个 Python Wintab 库的记录——
///    *"Certain Wacom driver versions corrupt the Windows process heap when WTOpenA
///    is called with fEnable=1 ... after a heavily-initialised UI"*。
///    我们正好是重度 D2D 初始化过的进程，所以照它的做法：
///    **用 `fEnable:false` 打开，再单独 `WTEnable(true)`**。
/// 2. **不要 `CXO_MESSAGES`，改成主动轮询**：同样来自那个库
///    （`lcOptions &amp;= ~CXO_MESSAGES` + 自己 `WTPacketsGet`）。
///    这条对我们尤其重要——我们在 Raw Input 上吃过一模一样的亏：
///    按消息收会让**空闲时也持续被唤醒**（实测空闲 46fps、单核 26%）。
///    轮询就没有这个问题：不写一笔时我们一次都不去取。
/// 3. **`CXO_SYSTEM` 要清掉**：默认上下文带着它（实测 `lcOptions=0x0003`），
///    含义是"让驱动去移动系统光标"。我们不需要——笔移动光标是 Windows
///    通过"笔当鼠标"那条路已经做好的事，再让 Wintab 插一脚只会打架。
///
/// <para>## 包体布局不能按规范硬编</para>
/// 实测这个高漫驱动**与规范差 4 字节**：规范说 时间戳@8、X@24、Y@28、压力@32，
/// 实测是 **时间戳@12、X@28、Y@32、压力@36**——驱动在 offset 8 多占了一个字，
/// 从它往后所有字段整体挪了 4。
/// （教训：**规范 ≠ 驱动的实现**，所以这里不写死，用下面的自校准现场钉正。）
/// </summary>
internal sealed class WintabInput
{
    /// <summary>缓冲给足——`WTInfo` 实测会写 172 字节（规范常说 160）。
    /// 宁可多给，**绝不能让驱动写坏我们的内存**。</summary>
    private const int BufBytes = 256;

    /// <summary>`DVC_NPRESSURE`——wintab.h 明确写着 15（已用 Wine 的 wintab.h、
    /// Wacom 官方 WINTAB.H、Rust wintab_lite 三处独立印证；实测枚举出来的 [15]
    /// 正是 0~16383，与驱动自报一致）。</summary>
    private const uint DVC_NPRESSURE = 15;

    private IntPtr _ctx = IntPtr.Zero;
    private IntPtr _buf = IntPtr.Zero;

    /// <summary>规范里 `pkSerialNumber` 的字节偏移——自校准的锚。</summary>
    private const int SpecSerialOffset = 12;

    // 字段偏移：先用规范值，开起来之后马上用"包序号每包 +1"这条铁律钉正。
    private int _offX = 24;
    private int _offY = 28;
    private int _offBtn = 20;
    private int _offP = 32;
    private int _layoutShift;
    private bool _layoutPinned;

    /// <summary>驱动的压力上限（`DVC_NPRESSURE.axMax`）。</summary>
    public int MaxPressure { get; private set; } = 1023;

    /// <summary>本次轮询取到的压力（0..1）；**-1 = 这一次没有有效压力**。</summary>
    public float Pressure01 { get; private set; } = -1f;

    /// <summary>本次轮询取到的**原始**压力值（0..<see cref="MaxPressure"/>）。
    /// 报日志用——只报 0..1 看不出"力度用到了量程的哪一段"。</summary>
    public int RawPressure { get; private set; }

    /// <summary>本次轮询取到的**平板坐标**（还没映射到屏幕）。
    /// 2026-10-07 加：要做"整笔都走 Wintab"就得知道平板坐标↔屏幕坐标的映射关系，
    /// 而这个关系**不能猜**（今天已经因为猜布局栽过好几次）——
    /// 所以先把两个坐标同时录下来，用实测数据把映射算出来。</summary>
    public int LatestX { get; private set; }
    public int LatestY { get; private set; }

    /// <summary>笔尖是否按下（`pkButtons` 的最低位）。</summary>
    public bool PenDown { get; private set; }

    public int PacketsRead { get; private set; }

    /// <summary>压力越界的包数——**这个数不为 0 就说明偏移钉错了**，见 <see cref="Poll"/>。</summary>
    public int BadPackets { get; private set; }

    public string Note { get; private set; } = "未尝试";

    public bool IsOpen => _ctx != IntPtr.Zero;

    /// <summary>布局是不是被现场钉正过（自检口径，会在 `[笔画]` 里报出来）。</summary>
    public bool LayoutPinned => _layoutPinned;

    public string LayoutText => _layoutPinned
        ? $"实测钉正 压力@{_offP}（偏规范 {_layoutShift:+#;-#;0} 字节）"
        : $"**按规范值 压力@{_offP}（没能现场钉正）**";

    /// <summary>
    /// 打开 Wintab 上下文。**只在第一次写笔时调用**（不是启动时）：
    /// ① 那个堆破坏问题只在"重度初始化过的进程"上出现，晚点开更安全；
    /// ② 没笔的时候开着它纯属浪费。
    /// </summary>
    public bool Open(IntPtr hwnd)
    {
        if (_ctx != IntPtr.Zero) return true;
        if (hwnd == IntPtr.Zero) { Note = "窗口句柄为空"; return false; }

        _buf = Marshal.AllocHGlobal(BufBytes);
        try
        {
            // ---- ① 压力上限：直接问驱动（Wacom 官方文档推荐的做法）----
            for (int i = 0; i < BufBytes; i++) Marshal.WriteByte(_buf, i, 0);
            if (Native.WTInfo(Native.WTI_DEVICES, DVC_NPRESSURE, _buf) > 0)
            {
                var ax = Marshal.PtrToStructure<Native.AXIS>(_buf);
                if (ax.axMax > 0) MaxPressure = ax.axMax;
            }
            if (MaxPressure <= 0) MaxPressure = 1023;

            // ---- ② 取默认上下文 ----
            for (int i = 0; i < BufBytes; i++) Marshal.WriteByte(_buf, i, 0);
            uint bytes = Native.WTInfo(Native.WTI_DEFCONTEXT, 0, _buf);
            if (bytes == 0) { Note = "WTInfo(WTI_DEFCONTEXT) 返回 0（没装驱动/驱动没在跑）"; FreeBuf(); return false; }

            int opts = Marshal.ReadInt32(_buf, Native.LC_OPTIONS_OFFSET);
            // 清 CXO_SYSTEM（别让驱动跟着动系统光标）、**不设** CXO_MESSAGES（用轮询）
            int newOpts = opts & ~(int)Native.CXO_SYSTEM & ~(int)Native.CXO_MESSAGES;
            Marshal.WriteInt32(_buf, Native.LC_OPTIONS_OFFSET, newOpts);
            int pktData = Marshal.ReadInt32(_buf, Native.LC_PKTDATA_OFFSET);

            // ---- ③ 打开（**先禁用打开**，见类注释第 1 条）----
            IntPtr ctx = Native.WTOpen(hwnd, _buf, false);
            if (ctx == IntPtr.Zero)
            {
                Note = $"WTOpen 失败 err={Marshal.GetLastWin32Error()}";
                Marshal.WriteInt32(_buf, Native.LC_OPTIONS_OFFSET, opts);   // 还原，免得下次拿脏的
                FreeBuf();
                return false;
            }

            if (!Native.WTEnable(ctx, true))
            {
                Note = "WTOpen 成功但 WTEnable 失败";
                Native.WTClose(ctx);
                FreeBuf();
                return false;
            }

            _ctx = ctx;
            _ = pktData;
            Note = $"已连通（压力上限 {MaxPressure}）";
            return true;
        }
        catch (Exception ex)
        {
            Note = "异常：" + ex.Message;
            FreeBuf();
            return false;
        }
    }

    /// <summary>关掉上下文（空闲时调，别让驱动白干活）。</summary>
    public void Close()
    {
        if (_ctx != IntPtr.Zero)
        {
            try { Native.WTClose(_ctx); } catch { /* 关不上也不能影响主流程 */ }
            _ctx = IntPtr.Zero;
        }
        FreeBuf();
        Pressure01 = -1f;
        PenDown = false;
    }

    private void FreeBuf()
    {
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
    }

    /// <summary>
    /// 把队列**倒空并丢弃**。起笔时先来一次——空闲期间驱动仍可能在排队，
    /// 不清的话第一笔会吃到一堆过期样本（和 Raw Input 那个"绝对报锚点"是同一类问题）。
    /// </summary>
    public void Flush()
    {
        if (_ctx == IntPtr.Zero) return;
        int guard = 0;
        while (Native.WTPacketsGet(_ctx, 1, _buf) > 0 && ++guard < 100000) { }
        Pressure01 = -1f;
        PenDown = false;
    }

    /// <summary>
    /// 取一次队列里的**全部**包，把最新的压力留在 <see cref="Pressure01"/>。
    ///
    /// **不靠"包长"跨包读**：一次只要 1 个包，每包都写回缓冲的 0 偏移，
    /// 于是**跨包错位不可能发生**（这是探针阶段用真机数据换来的教训：
    /// 按自算的包长批量读，包长一错就从第 2 个包起全错位，读出 7000 万这种鬼数）。
    /// </summary>
    /// <returns>本次有没有取到有效压力</returns>
    public bool Poll()
    {
        Pressure01 = -1f;
        PenDown = false;
        if (_ctx == IntPtr.Zero) return false;

        bool any = false;
        _samples.Clear();
        int n;
        while ((n = Native.WTPacketsGet(_ctx, 1, _buf)) > 0)
        {
            PacketsRead++;

            // **先钉布局**（攒够几个包就能认出"包序号"那个字）。
            if (!_layoutPinned && !TryPinLayout()) { /* 还没攒够，这次先不当结论 */ }

            int p = Marshal.ReadInt32(_buf, _offP);
            if (p < 0 || p > MaxPressure)
            {
                // **合理性断言**：压力不可能超出驱动自报的上限。
                // 超了 = 偏移钉错了 → **这次的数一个都不信**，只记账。
                BadPackets++;
                continue;
            }

            Pressure01 = p / (float)MaxPressure;
            RawPressure = p;
            LatestX = Marshal.ReadInt32(_buf, _offX);
            LatestY = Marshal.ReadInt32(_buf, _offY);
            int btn = Marshal.ReadInt32(_buf, _offBtn);
            PenDown = (btn & 0x01) != 0;
            // **每个包的压力都留下**（见 Samples 那段注释）——
            // 以前只留最后一个，等于把这一批里其余的压力全扔了。
            if (_samples.Count < MaxSamples) _samples.Add(p / (float)MaxPressure);
            any = true;
        }
        return any;
    }

    /// <summary>一次轮询最多留几个压力样本（够这一批点用就行）。</summary>
    private const int MaxSamples = 64;
    private readonly System.Collections.Generic.List<float> _samples = new();

    /// <summary>
    /// **这次轮询取到的全部压力值**（按时间序，0..1）。空表示这次没取到。
    ///
    /// 为什么要有它（2026-10-07，用户一句"关了 wintab 开 ink 就正常、开了 wintab 关 ink 就很脏"
    /// 把病因钉死了）：一条输入消息里常常合并了 1~5 个点，而设备的包率（192Hz）
    /// 比消息率（60~80Hz）高得多 —— 一批点对应着**好几个包**。
    /// 以前只取最后一个包的压力、套给这一批所有点 → **宽度是台阶**（真机录音实测：
    /// 28 个点里压力只变 12 次），而开 ink 那条路是**逐点自带压力**的，所以那边平滑。
    /// **两条路的差别只在压力分辨率** —— 位置是同一份（我们只借压力）。
    ///
    /// 现在把整批样本交给调用方，由它按"点在这批里的位置"摊开 —— 这才用上了本该有的分辨率。
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<float> Samples => _samples;

    /// <summary>自校准用的样本（最多留几个包就够认出包序号）。</summary>
    private readonly int[][] _calibSamples = new int[6][];
    private int _calibCount;

    /// <summary>
    /// **现场钉正字段偏移**——用"包序号（`pkSerialNumber`）每包 +1"这条铁律。
    ///
    /// 这是全类里最关键的一段：驱动的真实布局和规范可能不一样（我们这个就差 4 字节），
    /// 而**偏移错一位，读出来的"压力"就是别的字段**——探针阶段真的把 Y 坐标
    /// 当成压力报出去过（"压力 593~1162"那个结论就是错的）。
    /// 光靠"值在合理范围内"查不出来（坐标也在 0~65535 里），
    /// 但**"每包 +1"是骗不了人的**。
    /// </summary>
    private bool TryPinLayout()
    {
        var words = new int[64 / 4];
        for (int w = 0; w < words.Length; w++) words[w] = Marshal.ReadInt32(_buf, w * 4);
        _calibSamples[_calibCount++ % _calibSamples.Length] = words;
        if (_calibCount < 3) return false;

        int have = Math.Min(_calibCount, _calibSamples.Length);
        int first = (_calibCount - have) % _calibSamples.Length;

        // 找"连续几个包都恰好 +1"的那个字 = 包序号
        for (int w = 0; w < words.Length; w++)
        {
            bool ok = true;
            for (int k = 1; k < have && ok; k++)
            {
                int a = _calibSamples[(first + k - 1) % _calibSamples.Length][w];
                int b = _calibSamples[(first + k) % _calibSamples.Length][w];
                if (b - a != 1) ok = false;
            }
            if (!ok) continue;

            // 找到包序号 → 由它推其它字段（规范里它前面 3 个字：上下文/状态/时间）
            int shift = w * 4 - SpecSerialOffset;
            if (shift < -8 || shift > 16) continue;
            _layoutShift = shift;
            _offBtn = 20 + shift;
            _offX = 24 + shift;
            _offY = 28 + shift;
            _offP = 32 + shift;
            if (_offP < 0 || _offP + 4 > BufBytes) continue;
            _layoutPinned = true;
            Note = $"已连通（压力上限 {MaxPressure}，布局实测钉正：偏规范 {shift:+#;-#;0} 字节）";
            return true;
        }
        return false;
    }
}
