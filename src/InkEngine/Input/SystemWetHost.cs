// SystemWetHost.cs —— 系统托管湿墨后端（CoreInkPresenterHost + CoreWetStrokeUpdateSource 旁听）
//
// 用在**委托墨迹轨迹不可用**的环境（Win10 1709+）或 `--syswethost` 强制时。后端优先级：
// 委托墨迹轨迹 → **系统托管湿墨（本文件）** → 自绘湿墨。系统墨迹线程收笔、画湿墨（低延迟），
// 我们在 CoreWetStrokeUpdateSource 上**旁听**逐点喂自己的笔迹模型（压力映射、去抖、停顿成型
// 全部复用不动），收笔后干墨自己画、再把系统那一份清掉（重叠交接，见 AfterPresent）。
//
// 关键接法（2026-10-12 微探针 %TEMP%\wetprobe 实测钉死的四件事）：
//   ① 同一个 HWND 挂不了两个合成目标——DesktopWindowTarget 会占掉窗口的合成根
//      （见 调研/调研-压感数据与墨迹预测.md §7.4"与我们自己的渲染管线怎么共存"）。
//      所以宿主模式下**合成根换成 DesktopWindowTarget**，我们自己的合成交换链经
//      ICompositorInterop::CreateCompositionSurfaceForSwapChain（IID 25297D5C-…，**vtable slot 4**）
//      变成 CompositionSurfaceBrush + SpriteVisual 挂进同一棵树：交换链在下、宿主墨迹层在上。
//      实测交换链内容整屏上屏（800×600 纯红 480000 像素全在）——"两层共存"成立。
//   ② CoreWetStrokeUpdateSource 的事件**在墨迹后台线程**即时到达（0~16ms），
//      逐点带 x/y/pressure/Timestamp；Timestamp 是 **µs 口径**（与 QPC 同源）。
//      转发到 UI 线程一律走 PostMessage（合成 WM_POINTER*），引擎原路不动。
//   ③ GetPointerInfo(e.PointerId) 在事件里拿得到设备类型（笔/触摸）；
//      InkUnprocessedInput 与"受理为墨"互斥（画墨时 0 点），**不用**。
//   ④ Disposition=Canceled 只取消当前那一段，系统会**立刻重开一笔**继续受理
//      （点流只剩 Starting、而且分不出"抬笔"）——所以 UI / 自绘模式**不用 Cancel**，
//      改为临时藏起墨迹层（IsVisible=false）：系统照常画但看不见，收笔再清。
//
// 2026-10-13 扩到荧光笔 + 触摸，三件实测结论先钉住（数字都在 --highlightertest 里）：
//   ⑤ 荧光笔的"半透明一致性"：系统那层默认是**按基色不透明画**的（预补偿色 #FFF3BA
//      上屏正好 B=186、和底不做任何混合）；设 IsHighlighter 则是**乘性（multiply）**
//      （中灰 128 底：(128,128,99)，经典荧光观感）；两者都和我们干墨的 α 铺色对不上。
//      正解：**不设 IsHighlighter + 墨迹层整层按 α 合成**（ContainerVisual.Opacity=α）
//      + 基色原样——合成 = 底×(1−α) + 色×α，和我们干墨逐像素同一个公式（含抗锯齿边缘）。
//      实测：白板 湿墨 B=182 / 干墨 B=182（Δ=0）；中灰底交叉色 (169,157,99) vs
//      α 理论 (169,156,99)（Δ=(0,1,0)）。IsHighlighter 那条是对照实验（Δ=(41,28,0)），
//      一行可切回（见 ApplyAttrs 注释）。
//   ⑥ 触摸**多指状态在宿主里拿不到**（2026-10-13 合成触摸实测）：宿主收下触摸后
//      WM_POINTER 被吞，两指注入只有一指进墨迹管线（第二指既无湿墨事件、
//      InkUnprocessedInput 也一个点不报、按下消息也到不了引擎）→ 双指漫游/三指擦/
//      长按选会整个断掉。所以触摸**只在"触摸手势总开关关掉"时**才给宿主
//      （`--notouch` 那条偏好；见 Engine.UpdateHostInputEnabled）——手势开时触摸
//      照旧走 WM_POINTER 自绘，多指手势一条不掉。
//   ⑦ 触摸的 InkPoint.Pressure 实测是 0（合成触摸同）：系统按压感画会把触摸湿墨
//      画成细线，所以起笔按指针种类套 IgnorePressure（见 ApplyPointerKind）。
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;

namespace InkEngine;

/// <summary>系统托管湿墨下"这一笔的湿墨谁画"。判定见 <see cref="InkEngine.DecideHostStroke"/>。</summary>
internal enum HostStrokeMode
{
    /// <summary>系统画湿墨（实线笔 / 荧光笔）：我们不自绘，收笔后重叠交接。</summary>
    Ink,
    /// <summary>系统那份藏起来，我们自绘湿墨（虚线笔）。</summary>
    Self,
    /// <summary>点在界面上：系统那份藏起来，整条转发给原有 UI 路由。</summary>
    Ui,
}

/// <summary>
/// 一个"合成指针"（宿主旁听转发）的会话状态。
///
/// ⚠ 2026-10-12 真笔实测教训：点批**跟着动作走**（见 <see cref="HostPointFeed.PointerAction"/>），
/// 不再走"每 id 一条 Pending 队列 + 每条消息取一批"的对齐法——那种对齐在
/// "上一笔的抬笔还没被 UI 消费、下一笔（同一个笔 id）就起了"时**整个错位**：
/// 下一笔的 KindDown 变 KindMove、旧批被新批换掉，症状是"整笔没进文档、
/// 只剩系统那一份，下一次交接一清就'上一笔消失'"。
/// </summary>
internal sealed class HostPointerState
{
    /// <summary>这一代手势的编号（每 <see cref="HostPointFeed.BeginGesture"/> +1）。
    /// 抬笔/收尾的迟到回调拿它认"是不是自己那一代"，串不了台。</summary>
    public uint Gen;
    public uint PType;
    public bool Inverted;
    public HostStrokeMode Mode;
    /// <summary>手势进行中（Starting 开、抬笔关）。</summary>
    public bool Active;
    /// <summary>UI 线程已处理完抬笔消息。</summary>
    public bool UpProcessed;
    /// <summary>湿墨事件侧已收尾（Completed/Canceled）。两边都齐了才从表里摘掉。</summary>
    public bool Ended;
    /// <summary>UI 线程"当前这条动作"的点批（<see cref="BeginMessage"/> 从动作缓冲
    /// 拷贝进来的**自有稳定缓冲**——动作缓冲用完要还池，不能长期持有）。</summary>
    public PenSample[] Current = Array.Empty<PenSample>();
    /// <summary>当前点批的有效长度（<see cref="Current"/> 容量可能比它大）。</summary>
    public int CurrentCount;
    public PenSample Last;
}

    /// <summary>
    /// 一条点批：池化缓冲 + 有效长度（R0 2026-10-13：不再每事件 new PenSample[n]——
    /// 书写期 ~240 事件/s，那曾是 40~50KB/s 的 gen0 churn）。
    /// 命名空间级：墨迹线程（SystemWetHost）与主循环（Engine）两边都要用。
    /// </summary>
    internal struct PointBatch
    {
        public PenSample[] Buf;
        public int Count;
    }

    /// <summary>
    /// 合成指针（宿主旁听 → 引擎指针路由）的全局登记表。
/// 引擎的 <c>ReadPointer</c> / <c>AppendStrokeSamples</c> 靠它认出"这条消息是转发来的"，
/// 数据源换成旁听点（同一套净化/去抖/建模入口 <c>AddPointCleaned</c> 不变）。
///
/// ⚠ 转发**不能用 PostMessage(WM_POINTER\*)**：实测（2026-10-12）这类消息由系统按指针状态
/// 派发、不接受应用合成投递，PostMessage 直接失败（err 1002 "The window cannot act on
/// the sent message"）。所以走**线程消息 + 动作队列**：后台线程把（窗口，指针 id，动作）
/// 排进队列再 PostThreadMessage 叫醒主循环，DrainMessages 认这条自定义消息派发下去
/// （和 PptWatcher 的唤醒消息同一个套路）。
/// </summary>
internal static class HostPointFeed
{
    /// <summary>唤醒主循环的自定义线程消息（WM_USER 范围；参数没人用）。</summary>
    public const uint WakeMessage = 0x0400 + 777;
    public const int KindDown = 0, KindMove = 1, KindUp = 2;

    /// <summary>主循环线程 id（PostThreadMessage 的收件人；引擎在 UI 线程上报一次）。</summary>
    public static uint MainThreadId;

    /// <summary>
    /// 把动作队列派给引擎的回调（见 Engine.DispatchHostPointer——它自己带线程守卫，
    /// 不在主循环线程跑就改投 WakeMessage，绝不跨线程改引擎状态）。
    /// 挂载方：Engine.UpdateHostInputEnabled。
    /// </summary>
    public static Action Drain;

    /// <summary>UI 线程的 DispatcherQueue（EnsureProbed 里拿）；用它把回调送回 UI 线程。</summary>
    internal static DispatcherQueue UiQueue;

    /// <summary>一条转发动作：投给哪个窗口、哪个指针、按/动/抬，**点批跟着动作走**。</summary>
    public struct PointerAction
    {
        public IntPtr Hwnd;
        public uint Id;
        public int Kind;
        /// <summary>发起这一代手势的编号（认"迟到的抬笔/收尾是不是自己的"）。</summary>
        public uint Gen;
        public PointBatch Batch;
    }

    static readonly object Lock = new();
    static readonly Dictionary<uint, HostPointerState> Map = new();
    static readonly LinkedList<PointerAction> Actions = new();
    /// <summary>动作队列上限（UI 线程卡死时的兜底）。丢点可以、丢整笔不行——满了只丢 Move。</summary>
    const int MaxActions = 2048;
    static uint s_nextGen;

    // ---- R0：点批缓冲池（墨迹后台线程租、主循环派发完归还）--------------------
    //
    // 安全规则：**一个缓冲同一时刻只被一个持有者租用**——墨迹线程租出后只有两种命运：
    // ① 被主循环派发完由 <see cref="ReturnBuf"/> 归还；② 被合并进新缓冲后当场归还。
    // BeginMessage 会把内容**拷贝**进每指针自有的稳定缓冲，所以归还后 st.Current 不悬空。
    // 只增不减（池空才分配）；池子设上限，防极端场景无界增长。
    static readonly Stack<PenSample[]> s_bufPool = new();
    const int BufPoolCap = 64;

    /// <summary>租一个容量 ≥ n 的点批缓冲（不够大就分配新的，旧的小缓冲自然淘汰）。</summary>
    public static PenSample[] RentBuf(int n)
    {
        lock (Lock)
        {
            while (s_bufPool.Count > 0)
            {
                var b = s_bufPool.Pop();
                if (b.Length >= n) return b;
            }
        }
        return new PenSample[Math.Max(n, 64)];
    }

    /// <summary>归还点批缓冲（null / 空缓冲忽略）。</summary>
    public static void ReturnBuf(PenSample[] b)
    {
        if (b == null || b.Length == 0) return;
        lock (Lock)
        {
            if (s_bufPool.Count < BufPoolCap) s_bufPool.Push(b);
        }
    }

    // ---- R0：单次合批唤醒（每批事件最多唤醒主循环一次）------------------------
    //
    // 旧写法每事件一次 DispatcherQueue.TryEnqueue(新闭包)：~240 次/s 的闭包分配 +
    // dispatcher 往返。新写法：静态缓存委托 + s_wakePending 挂起标志——
    // 只有把标志从 0 翻成 1 的那个生产者负责投递；派发完清标志时若队列又有新货
    // （派发期间到达、它的 Exchange 看到 1 没有投递），由清标志方补投，**不丢唤醒**。
    // ⚠ 类型是 DispatcherQueueHandler（WinRT 委托）不是 System.Action——
    // TryEnqueue 只认前者；缓存一个静态实例，消灭每事件的闭包分配。
    static readonly DispatcherQueueHandler s_drainCached = static () => Drain?.Invoke();
    static int s_wakePending;

    /// <summary>投递一次唤醒（DispatcherQueue 优先，失败退回 PostThreadMessage）。</summary>
    static void PostWake()
    {
        var q = UiQueue;
        try { if (q != null && q.TryEnqueue(s_drainCached)) return; } catch { }
        uint tid = MainThreadId;
        if (tid != 0) Native.PostThreadMessage(tid, WakeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 主循环派发完一轮后调：清唤醒挂起标志。判空与清标志在同一把锁内——
    /// 要么"看到空并清标"，要么"看到非空补投一次唤醒"，不存在丢唤醒的交错。
    /// </summary>
    public static void NoteDrained()
    {
        bool more;
        lock (Lock) { more = Actions.Count > 0; s_wakePending = 0; }
        if (more) PostWake();
    }

    /// <summary>Map 的键按 WM_POINTER 口径取低 16 位（合成指针 id 与 wParam 掩码对齐）。</summary>
    static uint Key(uint id) => id & 0xFFFF;

    /// <summary>
    /// 后台线程：排一条转发动作（点批随行），把回调送回主循环线程执行。
    /// 用 DispatcherQueue.TryEnqueue / PostThreadMessage，**不能用 PostMessage(WM_POINTER\*)**
    /// ——后者系统不收（实测 err 1002，见类注释）。
    /// ⚠ 队列满时**只丢 Move**：丢一条 Down/Up = 整笔没了（2026-10-12 真笔 bug 的一类根因）。
    ///
    /// R0（2026-10-13）：点批缓冲由调用方租用（<see cref="RentBuf"/>）、本方法入队后
    /// 归队列/合并结果所有，最终由主循环 <see cref="ReturnBuf"/> 归还；唤醒改单次合批
    /// （s_wakePending 协议，见上面 NoteDrained 的交错分析）——**所有路径都要过标志位**，
    /// 合并/丢弃的早退也一样，否则那次事件可能永远唤不醒主循环。
    /// </summary>
    public static void PostAction(IntPtr hwnd, uint id, int kind, PenSample[] buf, int count, uint gen)
    {
        buf ??= Array.Empty<PenSample>();
        lock (Lock)
        {
            if (Actions.Count >= MaxActions && kind == KindMove)
            {
                // 尾巴是同一指针的 Move 就并进去（点不丢，只合成一批）；并不进才丢。
                var last = Actions.Last;
                if (last != null && last.Value.Kind == KindMove && last.Value.Id == id && last.Value.Gen == gen)
                {
                    // R0：合并也走池化缓冲（租一个够大的，拷两份，两个旧缓冲当场归还）。
                    int na = last.Value.Batch.Count, nb = count;
                    var merged = RentBuf(na + nb);
                    Array.Copy(last.Value.Batch.Buf, merged, na);
                    if (nb > 0) Array.Copy(buf, 0, merged, na, nb);
                    ReturnBuf(last.Value.Batch.Buf);
                    ReturnBuf(buf);
                    Actions.Last.Value = new PointerAction
                    {
                        Hwnd = hwnd, Id = id, Kind = kind, Gen = gen,
                        Batch = new PointBatch { Buf = merged, Count = na + nb },
                    };
                    goto wake;   // 合并也是"入队"：照过唤醒协议
                }
            }
            if (Actions.Count >= MaxActions && kind != KindMove)
            {
                // 按/抬必须送到：挤掉一条最旧的 Move 腾位子。
                for (var node = Actions.First; node != null; node = node.Next)
                {
                    if (node.Value.Kind == KindMove)
                    {
                        ReturnBuf(node.Value.Batch.Buf);   // R0：挤掉的缓冲也归还
                        Actions.Remove(node);
                        break;
                    }
                }
                if (Actions.Count >= MaxActions)
                {
                    ReturnBuf(buf);   // R0：丢弃路径也要归还（否则白租一个缓冲）
                    goto wake;        // 全是按/抬（极端）：只好丢这一条
                }
            }
            Actions.AddLast(new PointerAction
            {
                Hwnd = hwnd, Id = id, Kind = kind, Gen = gen,
                Batch = new PointBatch { Buf = buf, Count = count },
            });
        }
    wake:
        if (Interlocked.Exchange(ref s_wakePending, 1) == 0) PostWake();
    }

    /// <summary>主循环：取一条转发动作（DispatchHostPointer 逐条派发）。</summary>
    public static bool TryDequeueAction(out PointerAction a)
    {
        lock (Lock)
        {
            if (Actions.Count > 0) { a = Actions.First.Value; Actions.RemoveFirst(); return true; }
        }
        a = default;
        return false;
    }

    /// <summary>
    /// UI 线程：这条动作开始处理——把点批**拷贝**进每指针自有的稳定缓冲
    /// （R0：动作缓冲派发完要还池，不能长期持有——所以这里拷一份，st.Current 不悬空）。
    /// ReadPointer / AppendStrokeSamples 用。
    /// </summary>
    public static void BeginMessage(uint id, uint gen, PointBatch batch)
    {
        lock (Lock)
        {
            if (!Map.TryGetValue(Key(id), out var st) || st.Gen != gen) return;
            int n = batch.Count;
            if (st.Current.Length < n) st.Current = new PenSample[Math.Max(n, 64)];
            if (n > 0)
            {
                Array.Copy(batch.Buf, st.Current, n);
                st.Last = st.Current[n - 1];
            }
            st.CurrentCount = n;
        }
    }

    /// <summary>这个 id 是不是宿主转发的合成指针（消息处理入口判一次）。</summary>
    public static bool IsHostId(uint id) { lock (Lock) return Map.ContainsKey(Key(id)); }

    /// <summary>这一笔的湿墨是不是"系统在画"（起笔时判，收笔交接也问它）。</summary>
    public static bool IsInkWet(uint id)
    {
        lock (Lock) return Map.TryGetValue(Key(id), out var st) && st.Mode == HostStrokeMode.Ink;
    }

    /// <summary>
    /// 起一个手势（湿墨 Starting）。返回这一代的编号。
    /// ⚠ **旧的死会话直接换掉**（判"fresh"要看 <c>Active</c>、不能只看在不在表里）——
    /// 上一代的抬笔还没被 UI 消费时，表里留着的是个"已收尾未摘除"的死条目，
    /// 拿它当"重启段"会让新一笔的 KindDown 变 KindMove、整笔进不了文档。
    /// </summary>
    public static uint BeginGesture(uint id, uint ptype, bool inverted, HostStrokeMode mode)
    {
        lock (Lock)
        {
            var k = Key(id);
            s_nextGen++;
            Map[k] = new HostPointerState
            { Gen = s_nextGen, PType = ptype, Inverted = inverted, Mode = mode, Active = true };
            return s_nextGen;
        }
    }

    /// <summary>同一手势被系统"重开一段"（Cancel 后续段）：沿用原代号、原模式。</summary>
    public static bool TryGetGesture(uint id, out uint gen, out HostStrokeMode mode)
    {
        lock (Lock)
        {
            if (Map.TryGetValue(Key(id), out var st))
            {
                gen = st.Gen; mode = st.Mode;
                return true;
            }
        }
        gen = 0; mode = HostStrokeMode.Ink;
        return false;
    }

    /// <summary>UI 线程：这条消息的当前点（ReadPointer 用；多次读同一消息拿到的是同一个）。</summary>
    public static bool TryReadCurrent(uint id, out float x, out float y, out float pressure,
                                      out bool inverted, out uint pointerType)
    {
        lock (Lock)
        {
            if (Map.TryGetValue(Key(id), out var st))
            {
                var last = st.CurrentCount > 0 ? st.Current[st.CurrentCount - 1] : st.Last;
                x = last.X; y = last.Y; pressure = last.Pressure;
                inverted = st.Inverted; pointerType = st.PType;
                return true;
            }
        }
        x = y = 0; pressure = 0.5f; inverted = false; pointerType = 0;
        return false;
    }

    /// <summary>UI 线程：这条消息的全部采样点（AppendStrokeSamples 的数据源）。
    /// R0：出参带有效长度——缓冲是池化复用的，容量可能大于点数。</summary>
    public static bool TryGetCurrent(uint id, out PenSample[] batch, out int count)
    {
        lock (Lock)
        {
            if (Map.TryGetValue(Key(id), out var st) && st.CurrentCount > 0)
            {
                batch = st.Current;
                count = st.CurrentCount;
                return true;
            }
        }
        batch = Array.Empty<PenSample>();
        count = 0;
        return false;
    }

    /// <summary>取一个手势的状态（后台事件线程按 PointerId 查；没有返回 null）。</summary>
    public static HostPointerState StateOf(uint id)
    {
        lock (Lock) return Map.TryGetValue(Key(id), out var st) ? st : null;
    }

    /// <summary>UI 线程：抬笔消息处理完（会话两边都齐了才摘，见 <see cref="MarkEnded"/>）。</summary>
    public static void MarkUpProcessed(uint id, uint gen)
    {
        lock (Lock)
        {
            if (!Map.TryGetValue(Key(id), out var st) || st.Gen != gen) return;
            st.UpProcessed = true;
            if (st.Ended) Map.Remove(Key(id));
        }
    }

    /// <summary>湿墨事件侧：这一笔收尾（Completed/Canceled）。</summary>
    public static void MarkEnded(uint id, uint gen)
    {
        lock (Lock)
        {
            if (!Map.TryGetValue(Key(id), out var st) || st.Gen != gen) return;
            st.Ended = true;
            st.Active = false;
            if (st.UpProcessed) Map.Remove(Key(id));
        }
    }
}

/// <summary>
/// 一个覆盖层窗口的系统墨迹宿主：Windows.UI.Composition 合成根 + CoreInkPresenterHost。
/// 每屏一个（多显示器各建各的，和委托轨迹"按窗口探测"同一个口径）。
/// </summary>
internal sealed class SystemWetHost
{
    // ---- 进程级探测（风格对齐 OverlayWindow.InkTrailAvailable / InkTrailNote）----
    /// <summary>这台机器上建不建得起系统墨迹宿主（Win10 1709+）。</summary>
    public static bool Available;
    /// <summary>启动日志里那一行（可不可用、为什么）。</summary>
    public static string Note = "未尝试";
    /// <summary>本进程有没有窗口真的开了宿主（诊断/降级判据）。</summary>
    public static bool Active;

    static bool s_probed;
    static DispatcherQueueController s_dispatcher;   // 保活：合成与墨迹都要本线程有 DispatcherQueue
    static Compositor s_compositor;                  // 每屏的合成目标共用一个合成器

    /// <summary>
    /// 探测一次：线程 DispatcherQueue + Compositor + CoreInkPresenterHost 能不能建起来。
    /// 建不起来（老系统）就安静记一行，各窗口照旧走自绘湿墨。
    /// ⚠ threadType=2 apt=1 是实测出来的组合（threadType=1 时 GetForCurrentThread 返回 null）。
    /// </summary>
    public static void EnsureProbed()
    {
        if (s_probed) return;
        s_probed = true;
        try
        {
            if (DispatcherQueue.GetForCurrentThread() == null)
            {
                var opt = new DispatcherQueueOptions { dwSize = 12, threadType = 2, apartmentType = 1 };
                int hr = CreateDispatcherQueueController(opt, out IntPtr pCtrl);
                if (hr < 0) throw new InvalidOperationException($"CreateDispatcherQueueController hr=0x{hr:X8}");
                s_dispatcher = DispatcherQueueController.FromAbi(pCtrl);
            }
            HostPointFeed.UiQueue = DispatcherQueue.GetForCurrentThread();
            s_compositor = new Compositor();
            // 1709 以下连类型都没有；激活失败就当不可用
            _ = new CoreInkPresenterHost();
            Available = true;
            Note = "可用（CoreInkPresenterHost，Win10 1709+）";
        }
        catch (Exception ex)
        {
            Available = false;
            Note = "不可用：" + ex.GetType().Name + " " + ex.Message;
        }
    }

    // ---- 一个窗口的宿主 ----
    OverlayWindow _win;
    InkEngine _app;
    DesktopWindowTarget _target;
    ContainerVisual _root, _inkLayer;
    SpriteVisual _swapVisual;
    CoreInkPresenterHost _host;
    InkPresenter _presenter;
    CoreWetStrokeUpdateSource _wet;

    // 收笔交接（重叠交接）：湿墨结束 + 干墨那一帧上屏 → 才清系统那一份。
    volatile bool _handoffPending, _dryReady;
    int _presentsSinceDry;
    /// <summary>系统那份干墨还挂着没清（主循环为它兜一帧，见 Overlay.NeedsFrame）。</summary>
    public bool HandoffPending => _handoffPending;
    // 自绘/UI 模式把墨迹层藏起来的手势数（归零才亮回来）
    int _hiddenGestures;
    // 本宿主自己记的"每个指针这一笔归谁"（墨迹后台线程写、OnWetEnding 读）：
    // 不用全局会话表判模式——那张表的摘除时机在 UI 线程，晚一步收尾就查不到（见 OnWetEnding）。
    // 连"是不是半透明墨（荧光笔）"一起记：交接清法不同（见 TryCompleteHandoff）。
    readonly object _gestures = new();
    readonly Dictionary<uint, (HostStrokeMode Mode, bool Hl)> _gestureModes = new();

    // ---- R1 自定义干燥（2026-10-13 --customdry 探针钉死）----------------------
    /// <summary>
    /// InkSynchronizer（自定义干燥）。非空 = 系统**只画湿墨**、干墨永不落系统层：
    /// 收笔交接从 ClearStrokes 一族（迟到入库 10~120ms/补枪/延迟亮层/荧光双层）
    /// 简化成 <see cref="DismissWetInk"/> 一对 BeginDry/EndDry。
    /// 接法（探针实测）：必须在 <c>RootVisual 挂上之前</c>调 ActivateCustomDrying——
    /// RootVisual 之后 = E_INVALIDSTATE(0x8000000E)；拿到 presenter 立刻 = 成功。
    /// null = 老系统/异常，自动回退现行交接，功能不受影响。
    /// </summary>
    InkSynchronizer _synchronizer;
    /// <summary>R1 开关（Engine 解析 `--nocustomdrying`；默认开）。</summary>
    public static bool CustomDryingWanted = true;
    /// <summary>R1 结果说明（启动横幅用）：开 / 不可用回退 / 关。</summary>
    public static string CustomDryingNote = "未尝试";
    /// <summary>本窗口是不是自定义干燥模式。</summary>
    bool CustomDrying => _synchronizer != null;

    /// <summary>
    /// R1：收笔撤湿墨——BeginDry/EndDry **就地配对**（探针钉死的协议）：
    /// · 湿墨在**收笔后不会自动消失**、要等 EndDry 才撤（探针：EndDry 后 ≤1 帧撤净）；
    /// · BeginDry 无视觉副作用（湿墨仍在），单独调 EndDry 或漏配对 = E_INVALIDSTATE；
    /// · 调用点必须在"我们干墨那一帧已上屏"之后（见 <see cref="TryCompleteHandoff"/> 的重叠口径），
    ///   否则会露出一帧空白。
    /// 失败按"这个窗口的自定义干燥废了"处理：退回 ClearStrokes 交接，不再冒险。
    /// </summary>
    void DismissWetInk()
    {
        try { _synchronizer.BeginDry(); _synchronizer.EndDry(); }
        catch (Exception ex)
        {
            _synchronizer = null;
            CustomDryingNote = "运行期异常，本窗口回退 ClearStrokes：" + ex.Message;
            Console.WriteLine("[系统层] 自定义干燥异常 → 回退 ClearStrokes：" + ex.Message);
        }
    }

    /// <summary>系统层绘制属性的同步状态（进程级日志用；最后一次同步的结果）。</summary>
    public static string ColorSyncNote = "未同步";

    /// <summary>未受理输入事件计数（自检取证用：宿主收下触摸后第二指连这里都不报，见文件头⑥）。</summary>
    public static int UnprocDown, UnprocMove, UnprocUp;

    /// <summary>湿墨起笔计数（自检取证用：几条湿墨进了管线，按指针种类分开记）。</summary>
    public static int WetStarts, WetStartsPen, WetStartsTouch;

    /// <summary>指针种类的短名（日志用）。</summary>
    internal static string PointerKindName(uint ptype)
        => ptype == Native.PT_PEN ? "笔" : ptype == Native.PT_TOUCH ? "触摸" : "鼠标";

    // 最近一次同步的参数（按指针种类重套 IgnorePressure 用，见 ApplyPointerKind）。
    readonly object _attrs = new();
    Vector4 _syncColor = new(1, 0, 0, 1);
    float _syncWidth = 3f;
    bool _syncHl, _syncNoPressure;

    /// <summary>
    /// 把 InkTeach 当前工具的**颜色 / 线宽 / 压感口径**同步给系统墨迹层。
    /// 不同步的话系统用它自己的默认属性画湿墨（默认黑、2 DIP）——一笔两种颜色
    /// （2026-10-12 真笔"黑+红同笔双画"的一半根因）。
    ///
    /// 口径对照（见 Model.PressureWidth）：我们的宽 = 档位宽 × clamp(2.0×p, 0.22, 2.0)，
    /// 系统（WPF 同源）= 2 × Size × p —— 把 Size 设成**档位宽**（DIP）两条曲线就重合
    /// （p ≥ 0.11 段完全一致；更轻的压我们有 0.22 地板、系统没有，湿墨期最细处略细）。
    ///
    /// 荧光笔：IsHighlighter + IgnorePressure（和 InkTeach"荧光笔不吃压感"同口径），
    /// 外加**预补偿色**（见文件头⑤）：系统荧光是乘性混合，直接喂基色会比我们
    /// α=0.32 的一层深一大截；设成"一层铺白底的颜色" K=(1−α)·255+α·c 才对得上。
    /// </summary>
    public string SyncDrawingAttributes(System.Numerics.Vector4 color, float widthDip, bool highlighter)
    {
        lock (_attrs) { _syncColor = color; _syncWidth = widthDip; _syncHl = highlighter; }
        return ApplyAttrs();
    }

    /// <summary>
    /// 按**指针种类**套压感口径（起笔时调，见 OnWetStarting）：笔吃压感、触摸不吃——
    /// 触摸的 InkPoint.Pressure 实测是 0（合成触摸与真手指同一条路），
    /// 不压掉压感的话系统按 2×Size×p 把触摸湿墨画成一条细线，和我们干墨
    /// （缺压回填后等宽）对不上。
    /// </summary>
    public void ApplyPointerKind(bool isPen)
    {
        lock (_attrs) { _syncNoPressure = !isPen; }
        ApplyAttrs();
    }

    /// <summary>按当前同步状态重建系统层默认绘制属性（颜色/宽/荧光/压感一起套）。</summary>
    string ApplyAttrs()
    {
        try
        {
            Vector4 color; float widthDip; bool hl, noPressure;
            lock (_attrs)
            {
                color = _syncColor; widthDip = _syncWidth; hl = _syncHl; noPressure = _syncNoPressure;
            }
            var attrs = _presenter.CopyDefaultDrawingAttributes();
            byte R = (byte)Math.Clamp(color.X * 255f + 0.5f, 0f, 255f);
            byte G = (byte)Math.Clamp(color.Y * 255f + 0.5f, 0f, 255f);
            byte B = (byte)Math.Clamp(color.Z * 255f + 0.5f, 0f, 255f);
            attrs.Color = new Windows.UI.Color { A = 255, R = R, G = G, B = B };
            attrs.Size = new Windows.Foundation.Size { Width = widthDip, Height = widthDip };
            // ★ IsHighlighter **故意不设**（2026-10-13 --highlightertest D 段对照试验钉死）：
            //   设了它系统那层是**乘性（multiply）**合成，和我们干墨的 α 铺色只有白底上同值，
            //   压着深色内容当场分家（中灰 128 底实测：乘性 (128,128,99) vs 我们的 (169,156,99)，
            //   Δ=(41,28,0)——"画的时候把底下的字压深、收笔又变浅"）。不设它、改用
            //   **整层 α 合成**（下面 Opacity），系统那层和我们的一层荧光**逐像素同一个公式**
            //   （同一实测 Δ=(0,1,0)）。半透明一致性是验收核心，所以走这条；
            //   想切回经典 multiply 观感就把它改回 hl（两条实测数据都在 D 段注释里）。
            attrs.DrawAsHighlighter = false;
            attrs.IgnorePressure = hl || noPressure;
            attrs.FitToCurve = false;
            attrs.PenTip = Windows.UI.Input.Inking.PenTipShape.Circle;
            _presenter.UpdateDefaultDrawingAttributes(attrs);
            // ★ 荧光笔的半透明一致性（2026-10-13 --highlightertest 像素取证，见文件头⑤）：
            //   **墨迹层整层按 α 合成**（ContainerVisual.Opacity=α）+ 基色原样，
            //   合成结果 = 底×(1−α) + 色×α，和我们干墨的 α 铺色**逐像素同一个公式**
            //   （含抗锯齿边缘：覆盖率一起乘进 α 里）。实测白板 182 对 182、
            //   中灰底交叉色 Δ=(0,1,0)。Opacity 是逐帧合成属性、没有"起笔快照"时序问题
            //   （IgnorePressure 有，见 ApplyPointerKind），所以这里随手就套。
            float a = Math.Clamp(color.W, 0f, 1f);
            try { _inkLayer.Opacity = hl ? a : 1f; } catch { }
            ColorSyncNote = $"已同步 #{R:X2}{G:X2}{B:X2} 宽{widthDip:F1}DIP"
                          + (hl
                              ? $"（荧光笔·不压感·整层α={a:F2}×基色 #{R:X2}{G:X2}{B:X2}）"
                              : noPressure ? "（触摸·不压感 等宽）" : "（笔·压感 2×宽×p）");
        }
        catch (Exception ex) { ColorSyncNote = "同步失败: " + ex.Message; }
        return ColorSyncNote;
    }

    /// <summary>
    /// 这一笔中途改成"我们自绘"（停顿成型把 ActiveStroke 换成了图形对象）：
    /// 系统那份藏起来、收尾按 Self 清掉（见 Engine.ArmDwellShape）。不改的话
    /// 换对象之后自绘抑制失效，系统那条和幽灵图形**两条一起画**（同笔双画）。
    /// </summary>
    public void DowngradeToSelf()
    {
        bool any = false;
        lock (_gestures)
        {
            foreach (var k in _gestureModes.Keys)
            {
                var g = _gestureModes[k];
                if (g.Mode == HostStrokeMode.Ink) { _gestureModes[k] = (HostStrokeMode.Self, g.Hl); any = true; }
            }
        }
        // ★ 只有**真的降级了某条"系统在画"的手势**才藏层（2026-10-12 修）：
        //   原来无条件 `HideInkLayer()` → 藏层计数 +1，却等不到对应的收尾把它减回去
        //   （鼠标画的停顿成型在宿主模式下根本没有宿主手势；或恰逢这一笔已经收尾）。
        //   计数一旦卡在 >0，墨迹层就再也亮不回来——之后"系统画"的笔全看不见（掉墨）。
        if (any) HideInkLayer();
    }

    /// <summary>
    /// 建宿主：把合成根换成 DesktopWindowTarget，我们的交换链作为表面视觉挂进同一棵树
    /// （交换链在下、墨迹层在上）。失败返回错误文字，调用方安静退回自绘路线。
    /// </summary>
    public static string Create(OverlayWindow win, Vortice.DXGI.IDXGISwapChain1 swapChain, out SystemWetHost host)
    {
        host = null;
        try
        {
            EnsureProbed();
            if (!Available) return Note;

            var h = new SystemWetHost { _win = win };
            Vector2 size = new Vector2(win.Width / (win.Dpi / 96f), win.Height / (win.Dpi / 96f));

            // ① 合成根：DesktopWindowTarget（IID 29E691FA-…，slot 3 = CreateDesktopWindowTarget）。
            //    本窗口**不再建 DComp 目标**（一个 HWND 一个合成根，见文件头①）。
            h._target = ComposeInterop.CreateDesktopWindowTarget(s_compositor, win.Hwnd, true);
            h._root = s_compositor.CreateContainerVisual();
            h._target.Root = h._root;

            // ② 我们的合成交换链 → Composition 表面视觉（叠在最底下）
            var surface = ComposeInterop.CreateCompositionSurfaceForSwapChain(s_compositor, swapChain);
            h._swapVisual = s_compositor.CreateSpriteVisual();
            h._swapVisual.Brush = s_compositor.CreateSurfaceBrush(surface);
            h._swapVisual.Size = size;
            h._root.Children.InsertAtTop(h._swapVisual);

            // ③ 系统墨迹层（湿墨 + 它那份干墨都画在这里面），叠在交换链之上
            h._inkLayer = s_compositor.CreateContainerVisual();
            h._inkLayer.Size = size;
            h._root.Children.InsertAtTop(h._inkLayer);

            // ④ CoreInkPresenterHost（1709+）+ InkPresenter
            h._host = new CoreInkPresenterHost();
            h._presenter = h._host.InkPresenter;
            // ★ R1 自定义干燥（2026-10-13 --customdry 探针钉死的接法）：
            //   **必须在 RootVisual 挂上之前调**——Win11 26200 实测：
            //   RootVisual 之后调 = E_INVALIDSTATE(0x8000000E)；拿到 presenter 立刻调 = 成功。
            //   探针同时钉死：旁听通道照常送点、系统只画湿墨不画干墨（收笔后全 0）、
            //   BeginDry 每笔返回 1 条（不累积）、EndDry 撤湿墨 ≤1 帧、顺序 A 无空白帧。
            //   失败自动回退现行交接（老系统/异常都走这条路），功能不受影响。
            if (CustomDryingWanted)
            {
                try
                {
                    h._synchronizer = h._presenter.ActivateCustomDrying();
                    CustomDryingNote = "开（InkSynchronizer 已拿到）";
                }
                catch (Exception cdEx)
                {
                    CustomDryingNote = $"不可用，回退 ClearStrokes 交接（hr=0x{cdEx.HResult:X8} {cdEx.Message}）";
                }
            }
            else CustomDryingNote = "关（--nocustomdrying）";
            h._host.RootVisual = h._inkLayer;
            // 输入先不开：等引擎把回调接好（Attach）再开，防止"事件先到、路由未接"。
            h._presenter.InputDeviceTypes = Windows.UI.Core.CoreInputDeviceTypes.None;
            h._wet = CoreWetStrokeUpdateSource.Create(h._presenter);
            h._wet.WetStrokeStarting += h.OnWetStarting;
            h._wet.WetStrokeContinuing += h.OnWetContinuing;
            h._wet.WetStrokeStopping += h.OnWetStopping;
            h._wet.WetStrokeCompleted += h.OnWetEnding;
            h._wet.WetStrokeCanceled += h.OnWetEnding;

            // 未受理输入计数（2026-10-13 触摸多指探针的结论留在文件头⑥）：
            // 宿主收下触摸后第二指**连这条口都不报**（合成两指实测 0 事件），
            // 只留计数给自检取证（--touchhosttest 报"未受理 N 条"），平时不打日志。
            h._presenter.UnprocessedInput.PointerPressed += (s, e) => Interlocked.Increment(ref UnprocDown);
            h._presenter.UnprocessedInput.PointerMoved += (s, e) => Interlocked.Increment(ref UnprocMove);
            h._presenter.UnprocessedInput.PointerReleased += (s, e) => Interlocked.Increment(ref UnprocUp);

            host = h;
            Active = true;
            return null;
        }
        catch (Exception ex)
        {
            host = null;
            return ex.GetType().Name + "：" + ex.Message;
        }
    }

    /// <summary>引擎把"自己"接上来（回调都走引擎内部方法），随后才能开输入。</summary>
    public void Attach(InkEngine app)
    {
        _app = app;
    }

    /// <summary>
    /// 开/关宿主受理的输入（自由笔工具 + 非穿透才开；其余工具全走原 WM_POINTER 路）。
    /// `touch` = 这一段把**触摸**也交给宿主——只在"触摸手势总开关关掉"时为真
    ///（手势开时触摸必须留在 WM_POINTER 那条路上，多指手势才不掉，见文件头⑥）。
    /// </summary>
    public void SetInputEnabled(bool on, bool touch)
    {
        try
        {
            var types = Windows.UI.Core.CoreInputDeviceTypes.None;
            if (on)
            {
                types |= Windows.UI.Core.CoreInputDeviceTypes.Pen;
                if (touch) types |= Windows.UI.Core.CoreInputDeviceTypes.Touch;
            }
            _presenter.InputDeviceTypes = types;
        }
        catch { }
    }

    // ---- 湿墨旁听（墨迹后台线程！只做：转点、入队、投消息、藏层）-----------------

    /// <summary>把一条事件里的 InkPoint（客户区 DIP、Timestamp=µs）换成屏幕物理像素采样点。
    /// R0：填进租用的池化缓冲（调用方随 PostAction 入队，主循环派发完归还）。</summary>
    PointBatch Convert(CoreWetStrokeUpdateEventArgs e, HostPointerState st, double anchorMs)
    {
        float dpiScale = _win.Dpi / 96f;
        var list = e.NewInkPoints;
        int n = list.Count;
        var arr = n > 0 ? HostPointFeed.RentBuf(n) : Array.Empty<PenSample>();
        ulong newest = 0;
        for (int i = 0; i < n; i++) if (list[i].Timestamp > newest) newest = list[i].Timestamp;
        for (int i = 0; i < n; i++)
        {
            var p = list[i];
            arr[i] = new PenSample
            {
                X = _win.OriginX + (float)(p.Position.X * dpiScale),
                Y = _win.OriginY + (float)(p.Position.Y * dpiScale),
                Pressure = Math.Clamp(p.Pressure, 0f, 1f),
                // 压感只作用于笔这一支（和 WM_POINTER 路同一口径，见 AppendStrokeSamples）：
                // 触摸没有真实压力，不给这一笔打 HasPressure。
                HasPressure = st.PType == Native.PT_PEN && p.Pressure > 0f,
                InContact = true,
                TiltX = (int)p.TiltX,
                TiltY = (int)p.TiltY,
                Rotation = 0,
                // Timestamp 是 µs（实测与 QPC 同源）：最新点锚在"事件到达时刻"，早的点往前推。
                TimeMs = anchorMs - (newest - p.Timestamp) / 1000.0,
            };
        }
        return new PointBatch { Buf = arr, Count = n };
    }

    void OnWetStarting(CoreWetStrokeUpdateSource s, CoreWetStrokeUpdateEventArgs e)
    {
        try
        {
            uint pid = e.PointerId;
            // fresh 的判据是"**有没有活着的手势**"，不是"在不在表里"——表里可能留着
            // 上一笔"已收尾、抬笔还没被 UI 消费"的死条目，拿它当重启段会让新一笔
            // 的 KindDown 变 KindMove、整笔进不了文档（2026-10-12 真笔"上一笔消失"根因）。
            // 抬笔已经被 UI 处理过的（UpProcessed）也算死条目：那一笔已经收了。
            var st0 = HostPointFeed.StateOf(pid);
            bool fresh = st0 == null || !st0.Active || st0.UpProcessed;
            uint ptype = Native.PT_PEN;
            bool inverted = false;
            if (Native.GetPointerInfo(pid, out var pi)) ptype = pi.pointerType;
            if (ptype == Native.PT_PEN && Native.GetPointerPenInfo(pid, out var pen))
                inverted = (pen.penFlags & (Native.PEN_FLAG_INVERTED | Native.PEN_FLAG_ERASER)) != 0;

            HostStrokeMode mode;
            uint gen;
            if (fresh)
            {
                float dpiScale = _win.Dpi / 96f;
                double sx = _win.OriginX + (e.NewInkPoints.Count > 0 ? e.NewInkPoints[0].Position.X * dpiScale : 0);
                double sy = _win.OriginY + (e.NewInkPoints.Count > 0 ? e.NewInkPoints[0].Position.Y * dpiScale : 0);
                mode = _app.DecideHostStroke((float)sx, (float)sy);
                // 这一笔是不是半透明墨（荧光笔）——交接清法按它分（见 TryCompleteHandoff）。
                bool hl = _app.HostStrokeTranslucent;
                gen = HostPointFeed.BeginGesture(pid, ptype, inverted, mode);
                lock (_gestures) { _gestureModes[pid] = (mode, hl); }
                // 压感口径跟指针种类走（触摸的 InkPoint.Pressure 实测 0，见 ApplyPointerKind）。
                ApplyPointerKind(ptype == Native.PT_PEN);
                Interlocked.Increment(ref WetStarts);
                if (ptype == Native.PT_PEN) Interlocked.Increment(ref WetStartsPen);
                else if (ptype == Native.PT_TOUCH) Interlocked.Increment(ref WetStartsTouch);
                Console.WriteLine($"[系统层] 起笔 pid={pid} gen={gen} mode={mode}"
                                  + $"（{(hl ? "荧光" : "实线")}，{PointerKindName(ptype)}，交接挂起={_handoffPending}）");
                if (mode != HostStrokeMode.Ink) HideInkLayer();
                else
                {
                    // 新笔归系统画：墨迹层必须**现在就亮**——上一笔（自绘/UI）的
                    // "延迟亮层"没走完的话在这儿收掉，不然这一笔的湿墨看不见（掉墨）。
                    _showAfterClear = false;
                    if (_hiddenGestures == 0) ShowInkLayer();
                }
                // 新一笔（系统画）：上一笔遗留的交接状态没挂起就归零——不归零的话
                // "上一笔的 _dryReady/_presents"会让这一笔**一收笔就同帧 Clear**
                //（干墨还没上屏），一闪或直接掉墨。
                if (!_handoffPending)
                {
                    _dryReady = false; _presentsSinceDry = 0;
                    if (!CustomDrying)
                    {
                        // 起笔补枪：交接清屏/延迟亮层没逮住的"迟到入库"残留在这里收掉——
                        // 起笔这一刻容器里只可能是上一笔的残留（本笔湿墨还没入库），清了无害。
                        // ⚠ **每一笔都清**（2026-10-12）：迟到入库晚到 10~120ms，任何固定拍数
                        //   的补枪都可能没轮到它；起笔这一枪是最后一个确定性收口。
                        // R1：自定义干燥下系统**根本没有干墨**（探针：收笔后 20 帧全 0、
                        //   容器内容只在 BeginDry 里出现），无残留可清——整条跳过。
                        _clearRetries = 0;
                        Console.WriteLine("[系统层] 起笔补枪 → ClearStrokes（迟到残留）");
                        ClearStrokes();
                    }
                }
                var down = Convert(e, HostPointFeed.StateOf(pid), _app.NowMs);
                HostPointFeed.PostAction(_win.Hwnd, pid, HostPointFeed.KindDown,
                                         down.Buf, down.Count, gen);
                return;
            }
            // 同一指针的"重启段"（被 Cancel 才有；兜底沿用原模式与原代号）
            HostPointFeed.TryGetGesture(pid, out gen, out mode);
            var move = Convert(e, HostPointFeed.StateOf(pid), _app.NowMs);
            HostPointFeed.PostAction(_win.Hwnd, pid, HostPointFeed.KindMove,
                                     move.Buf, move.Count, gen);
        }
        catch (Exception ex) { Note = "WetStrokeStarting 异常: " + ex.Message; }
    }

    void OnWetContinuing(CoreWetStrokeUpdateSource s, CoreWetStrokeUpdateEventArgs e)
    {
        try
        {
            uint pid = e.PointerId;
            var st = HostPointFeedState(pid);
            if (st == null) return;
            var cont = Convert(e, st, _app.NowMs);
            HostPointFeed.PostAction(_win.Hwnd, pid, HostPointFeed.KindMove,
                                     cont.Buf, cont.Count, st.Gen);
        }
        catch (Exception ex) { Note = "WetStrokeContinuing 异常: " + ex.Message; }
    }

    void OnWetStopping(CoreWetStrokeUpdateSource s, CoreWetStrokeUpdateEventArgs e)
    {
        try
        {
            uint pid = e.PointerId;
            var st = HostPointFeedState(pid);
            if (st == null) return;
            var up = e.NewInkPoints.Count > 0 ? Convert(e, st, _app.NowMs) : default;
            HostPointFeed.PostAction(_win.Hwnd, pid, HostPointFeed.KindUp,
                                     up.Buf, up.Count, st.Gen);
        }
        catch (Exception ex) { Note = "WetStrokeStopping 异常: " + ex.Message; }
    }

    void OnWetEnding(CoreWetStrokeUpdateSource s, CoreWetStrokeUpdateEventArgs e)
    {
        try
        {
            uint pid = e.PointerId;
            // 这一笔"归谁"由**本宿主自己记**（起笔时落的 _gestureModes），不再依赖
            // 全局会话表的存活——那张表的摘除时机在 UI 线程，晚一步就查不到模式，
            // 交接/清理整个漏掉、系统那份干墨永远挂着（"上一笔黑墨不消失/乱清"一族）。
            HostStrokeMode mode;
            bool hl;
            lock (_gestures)
            {
                if (!_gestureModes.Remove(pid, out var g)) return;   // 同一笔的重复收尾
                mode = g.Mode; hl = g.Hl;
            }
            var st = HostPointFeedState(pid);
            Console.WriteLine($"[系统层] 湿墨收尾 pid={pid} mode={mode}{(hl ? "·荧光" : "")} gen={st?.Gen}"
                              + $"（交接挂起={_handoffPending} 干墨就绪={_dryReady} 已上屏拍={_presentsSinceDry}）");
            // Completed/Canceled：湿墨到此为止。系统那一份干墨的清法按模式分：
            //   Ink  → 重叠交接（等干墨那一帧上屏再清，见 AfterPresent）；
            //   Self/Ui → 系统那份全程藏着，随手清掉、亮回墨迹层。
            if (mode == HostStrokeMode.Ink)
            {
                _handoffPending = true;
                _handoffHl = hl;
                TryCompleteHandoff();
            }
            else
            {
                if (--_hiddenGestures <= 0)
                {
                    _hiddenGestures = 0;
                    if (CustomDrying)
                    {
                        // R1：系统不画干墨——被藏起来的那条湿墨用 BeginDry/EndDry 撤掉即可，
                        // 没有残留可清。亮层仍走"延迟亮层"兜底（探针：EndDry 撤湿墨 ≤1 帧，
                        // 但保守等几帧再亮，防极端帧序下闪一下幽灵）。
                        DismissWetInk();
                        _showAfterClear = true;
                        _selfWaitStartMs = Environment.TickCount64;
                        _selfEmptyFrames = 0;
                        Console.WriteLine("[系统层] 自绘/UI 笔收尾 → BeginDry/EndDry 撤湿墨（自定义干燥）");
                    }
                    else
                    {
                        ClearStrokes();
                        // ★ 延迟亮层（2026-10-12 自检 --highlightertest 像素取证钉死）：
                        // 系统那份干墨**在 WetStrokeCompleted 之后 10~120ms 才落进
                        // StrokeContainer**（实测：固定拍数的补枪在自检的紧凑帧循环里
                        // 几毫秒就烧完、逮不住它），这一枪清的多半是空容器；紧接着
                        // ShowInkLayer 就把它亮了出来——荧光笔（半透明）的屏幕上就是
                        // "两层墨"（实测：收笔后整条笔 99.9% 像素变成系统 multiply 强黄
                        // B=38、压着自绘那层浅黄；下一笔起笔藏层又消失、收笔再现）。
                        // 所以收尾改成**确定性**的：墨迹层继续藏着，逐帧看 StrokeContainer
                        // ——有残留就清，**连续几帧空 + 距收尾 ≥ 300ms** 才亮层
                        //（见 AfterPresent / SelfClearWaitMs）。亮出来的只剩一层。
                        _showAfterClear = true;
                        _selfWaitStartMs = Environment.TickCount64;
                        _selfEmptyFrames = 0;
                        Console.WriteLine("[系统层] 自绘/UI 笔收尾 → 藏层清残留，清干净才亮（≥300ms）");
                    }
                }
            }
            if (st != null) HostPointFeed.MarkEnded(pid, st.Gen);
        }
        catch (Exception ex) { Note = "WetStroke 收尾异常: " + ex.Message; }
    }

    HostPointerState HostPointFeedState(uint pid) => HostPointFeed.StateOf(pid);

    void HideInkLayer()
    {
        _hiddenGestures++;
        try { _inkLayer.IsVisible = false; } catch { }
    }

    void ShowInkLayer()
    {
        try { _inkLayer.IsVisible = true; } catch { }
    }

    void ClearStrokes()
    {
        // R1：自定义干燥下 StrokeContainer 整个不可用（探针 v3 实测：GetStrokes/Clear
        // 都抛 NullReferenceException）——直接跳过，不让无谓异常走进来。
        if (CustomDrying) return;
        try { _presenter.StrokeContainer.Clear(); } catch { }
    }

    // ---- 干墨交接（重叠交接：干墨上屏 → 泵一帧 → 清系统那份）--------------------
    //
    // 实测（InkBystanderTest T6）：收笔后"同帧 Clear"必闪一帧空白（两种顺序都闪），
    // 必须"我们的干墨先上屏、下一拍再 Clear"。这里：
    //   · 抬笔消息处理完（干墨已进文档）→ NoteUpProcessed 置 _dryReady；
    //   · 每次 Present 之后调 AfterPresent：干墨那一帧已经上屏 → 现在清不闪。
    public void NoteUpProcessed()
    {
        Console.WriteLine($"[系统层] 干墨已进文档（交接挂起={_handoffPending} 已上屏拍={_presentsSinceDry}）");
        _dryReady = true;
        TryCompleteHandoff();
    }

    void TryCompleteHandoff()
    {
        if (!_handoffPending || !_dryReady) return;
        if (_presentsSinceDry < 1) return;   // 干墨那一帧还没上屏：等 AfterPresent
        if (CustomDrying)
        {
            // ★ R1：干墨那一帧已上屏 → BeginDry/EndDry 撤湿墨（重叠交接）。
            //   探针钉死：EndDry 后 ≤1 帧撤净、之前与自绘干墨同址同色重叠、无空白帧。
            //   没有迟到入库/补枪/延迟亮层——系统根本没画过干墨。
            Console.WriteLine($"[系统层] 交接完成 → BeginDry/EndDry（自定义干燥，已上屏拍={_presentsSinceDry}）");
            DismissWetInk();
            _handoffPending = false;
            _handoffHl = false;
            _dryReady = false;
            _presentsSinceDry = 0;
            return;
        }
        Console.WriteLine($"[系统层] 交接完成 → ClearStrokes（已上屏拍={_presentsSinceDry}，"
                          + $"{(_handoffHl ? "荧光·交接后延迟亮层" : "实线·重叠交接")}）");
        ClearStrokes();
        // ★ 补枪（2026-10-12 自检 --syswethosttest 钉死）：系统的干墨**可能在
        // WetStrokeCompleted 之后才落进 StrokeContainer**——这一枪清的是空容器，
        // 那份干墨随后才到、一直挂到**下一笔的交接**才被清掉，屏幕上就是
        // "一起新笔，上一笔就掉墨"（用户真笔症状②）。所以之后几帧各补一枪，
        // 外加下一笔起笔时再补一枪（见 OnWetStarting）：迟到的那份跑不掉。
        _clearRetries = 3;
        // ★ 荧光笔（半透明墨）比实线多一道**延迟亮层**（2026-10-13）：实线的迟到残留
        //   和我们干墨长得一模一样，露头也看不出来；荧光的迟到残留是系统乘性那一层，
        //   压在我们干墨上就是"两层墨"（上一轮 --highlightertest 的老 bug）。
        //   所以交接清屏后**把墨迹层藏起来**，逐帧清到容器空 + ≥300ms 才亮
        //（复用 Self 模式那套延迟亮层，见 AfterPresent）：亮出来的只剩一层。
        if (_handoffHl)
        {
            try { _inkLayer.IsVisible = false; } catch { }
            _showAfterClear = true;
            _selfWaitStartMs = Environment.TickCount64;
            _selfEmptyFrames = 0;
        }
        _handoffPending = false;
        _handoffHl = false;
        _dryReady = false;
        _presentsSinceDry = 0;
    }

    /// <summary>这一轮交接是不是荧光笔（半透明墨）：清法不同（见 TryCompleteHandoff）。</summary>
    bool _handoffHl;

    /// <summary>交接清屏的"补枪"余量（每帧 AfterPresent 消耗一次）。</summary>
    int _clearRetries;

    /// <summary>
    /// 自绘/UI 笔收尾后的"延迟亮层"（见 OnWetEnding）：墨迹层继续藏着，
    /// 逐帧清掉迟到入库的系统干墨，**容器连续几帧空 + 距收尾 ≥ <see cref="SelfClearWaitMs"/>**
    /// 才亮回来——亮出来的只剩一层。
    /// </summary>
    bool _showAfterClear;
    long _selfWaitStartMs;
    int _selfEmptyFrames;

    /// <summary>延迟亮层的最短藏层时长（实测迟到入库 ≤120ms，取 300ms 留一倍余量）。</summary>
    const int SelfClearWaitMs = 300;

    /// <summary>补枪还没放完（主循环为它兜几帧，见 Engine.NeedsFrame）。</summary>
    public int ClearRetries => _clearRetries;

    /// <summary>延迟亮层还挂着（主循环为它兜帧，见 Overlay.HostClearPending）。</summary>
    public bool ShowAfterClearPending => _showAfterClear;

    /// <summary>每帧 Present 之后调（挂进 Overlay 的收尾钩子，和轨迹结账同一个位置）。</summary>
    public void AfterPresent()
    {
        if (_dryReady) _presentsSinceDry++;
        TryCompleteHandoff();
        if (_clearRetries > 0) { _clearRetries--; ClearStrokes(); }
        // 自绘/UI 笔的延迟亮层：容器里有残留就清、连续几帧都空 + 过了最短窗口才亮。
        if (_showAfterClear && _hiddenGestures == 0)
        {
            if (CustomDrying)
            {
                // R1：没有系统干墨可清（也**不能碰容器**——探针实测自定义干燥下
                // GetStrokes 在收笔后会抛异常）。纯时间+帧数兜底：≥300ms 且 3 帧后亮层。
                if (Environment.TickCount64 - _selfWaitStartMs >= SelfClearWaitMs && ++_selfEmptyFrames >= 3)
                {
                    _showAfterClear = false;
                    ShowInkLayer();
                    Console.WriteLine("[系统层] 延迟亮层到点 → 亮回墨迹层（自定义干燥）");
                }
                return;
            }
            int n = 0;
            try { n = _presenter.StrokeContainer.GetStrokes().Count; } catch { }
            if (n > 0)
            {
                _selfEmptyFrames = 0;
                ClearStrokes();
            }
            else if (Environment.TickCount64 - _selfWaitStartMs >= SelfClearWaitMs && ++_selfEmptyFrames >= 3)
            {
                _showAfterClear = false;
                ShowInkLayer();
                Console.WriteLine("[系统层] 残留已清干净 → 亮回墨迹层");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _wet.WetStrokeStarting -= OnWetStarting;
            _wet.WetStrokeContinuing -= OnWetContinuing;
            _wet.WetStrokeStopping -= OnWetStopping;
            _wet.WetStrokeCompleted -= OnWetEnding;
            _wet.WetStrokeCanceled -= OnWetEnding;
        }
        catch { }
        _wet = null; _presenter = null; _host = null;
        _inkLayer = null; _swapVisual = null; _root = null; _target = null;
    }

    // ---- WinRT 互操作（CsWinRT 投影对象不能直接 cast，照探针走 QI + vtable）----

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public uint dwSize; public int threadType, apartmentType; }

    [DllImport("coremessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}

/// <summary>
/// Compositor 的两个互操作接口（windows.ui.composition.interop.h，SDK 10.0.16299 起）：
///   ICompositorDesktopInterop（IID 29E691FA-…，slot 3 = CreateDesktopWindowTarget）；
///   ICompositorInterop（IID 25297D5C-…，slot 3 = CreateCompositionSurfaceForHandle、
///   **slot 4 = CreateCompositionSurfaceForSwapChain**、slot 5 = CreateGraphicsDevice）。
/// CsWinRT 投影对象 cast 不出这两个接口（InvalidCastException），只能拿原生指针走 vtable
///（InkBystanderTest 探针同款接法）。
/// </summary>
internal static class ComposeInterop
{
    static readonly Guid IID_ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
    static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateDesktopWindowTargetFn(IntPtr self, IntPtr hwnd, int isTopmost, out IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateCompositionSurfaceForSwapChainFn(IntPtr self, IntPtr swapChain, out IntPtr surface);

    public static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, IntPtr hwnd, bool topmost)
    {
        var marshaler = WinRT.MarshalInspectable<Compositor>.CreateMarshaler(compositor, true);
        IntPtr pUnk = WinRT.MarshalInspectable<Compositor>.GetAbi(marshaler);
        Guid iid = IID_ICompositorDesktopInterop;
        int hr = Marshal.QueryInterface(pUnk, iid, out IntPtr pItf);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(pItf);
            var fn = Marshal.GetDelegateForFunctionPointer<CreateDesktopWindowTargetFn>(
                Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            hr = fn(pItf, hwnd, topmost ? 1 : 0, out IntPtr pTarget);
            Marshal.ThrowExceptionForHR(hr);
            return DesktopWindowTarget.FromAbi(pTarget);
        }
        finally { Marshal.Release(pItf); }
    }

    public static Windows.UI.Composition.ICompositionSurface CreateCompositionSurfaceForSwapChain(
        Compositor compositor, object swapChain)
    {
        IntPtr pSwap = NativePointerOf(swapChain);
        var marshaler = WinRT.MarshalInspectable<Compositor>.CreateMarshaler(compositor, true);
        IntPtr pUnk = WinRT.MarshalInspectable<Compositor>.GetAbi(marshaler);
        Guid iid = IID_ICompositorInterop;
        int hr = Marshal.QueryInterface(pUnk, iid, out IntPtr pItf);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(pItf);
            // ★ slot 4：第二个方法才是 CreateCompositionSurfaceForSwapChain（slot 3 是 ForHandle）
            var fn = Marshal.GetDelegateForFunctionPointer<CreateCompositionSurfaceForSwapChainFn>(
                Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));
            hr = fn(pItf, pSwap, out IntPtr pSurface);
            Marshal.ThrowExceptionForHR(hr);
            return WinRT.MarshalInterface<Windows.UI.Composition.ICompositionSurface>.FromAbi(pSurface);
        }
        finally { Marshal.Release(pItf); }
    }

    /// <summary>SharpGen ComObject → 原生 IUnknown 指针（不同版本属性名不同，逐个试）。</summary>
    static IntPtr NativePointerOf(object o)
    {
        foreach (var name in new[] { "NativePointer", "Handle" })
        {
            var p = o.GetType().GetProperty(name);
            if (p != null && p.PropertyType == typeof(IntPtr)) return (IntPtr)p.GetValue(o);
        }
        foreach (var name in new[] { "DangerousGetHandle" })
        {
            var m = o.GetType().GetMethod(name, Type.EmptyTypes);
            if (m != null && m.ReturnType == typeof(IntPtr)) return (IntPtr)m.Invoke(o, null);
        }
        throw new InvalidOperationException("拿不到交换链的原生指针（NativePointer/Handle 都没有）");
    }
}

