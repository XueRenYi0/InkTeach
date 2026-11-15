using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace InkEngine;

// =====================================================================================
//  PPT 放映联动：连接层
//
//  参考出处（用户 2026-09-26 定的"站在巨人的肩膀上"最高原则，先查后写）：
//    · **Inkeys / 智绘教**（GPLv3，https://github.com/Alan-CRL/Inkeys，本机
//      `画布测试\Inkeys-20260713a\智绘教\PptCOM\PptCOM.cs`）：本文件的连接骨架
//      ——ROT（运行对象表）扫描、多 PPT 实例按优先级挑、WPS 的 ProgID 特判、
//      "忙"重试、COM 对象逐个释放，全部照它改写；它的形态是"独立 COM 服务进程 +
//      共享内存指针"，我们这里是**进程内模块**（省掉进程间那层，逻辑不变）。
//    · **Ink Canvas Ultra**（GPLv3，`MW_PPT.cs`）：进程名探测（没开 PPT 就别去碰
//      COM）、读失败一律静默——避免"没装 Office 的机器上每秒抛一个 COMException"。
//    · **InkCanvasForClass 社区版**（GPLv3，GitHub 上能找到的最新实现，`PPTManager`
//      / `PPTROTConnectionHelper`）：把"连接"抽成接口、换实现不改调用方（策略模式）
//      ——我们用它的接口形状，自检时注入假源。
//
//  与它们的最大不同（我们的适配，写在 Ppt.cs 里）：**页的所有者永远是我们**。
//  这里只回答"现在是不是在放映、第几页、那一页的 SlideID 是多少"，绝不碰文档。
// =====================================================================================

/// <summary>
/// PPT 放映状态的一份快照。**纯托管值**（不含 COM 对象），可以安全地跨线程传——
/// 这是刻意的：COM 对象有线程亲和性，跨线程用会抛 RPC_E_WRONG_THREAD
/// （Ultra 的注释里记着这个坑，我们不让 COM 对象离开轮询线程）。
/// </summary>
internal readonly struct PptSnapshot
{
    /// <summary>连上了 PowerPoint / WPS（有打开的演示文稿）。</summary>
    public bool Connected { get; init; }
    /// <summary>正在放映（全屏放映窗口存在）。</summary>
    public bool Showing { get; init; }
    /// <summary>当前是第几页（1 起）。</summary>
    public int Slide { get; init; }
    /// <summary>一共几页。</summary>
    public int Total { get; init; }
    /// <summary>当前页的 SlideID（PowerPoint 给每张幻灯片的一个稳定编号）。</summary>
    public int SlideId { get; init; }
    /// <summary>演示文稿的稳定标识（名字 + 完整路径的哈希），用来当存盘的目录名。</summary>
    public string Key { get; init; }

    /// <summary>两个快照在"对我们的状态机有意义"的维度上相等吗。</summary>
    public bool SameAs(in PptSnapshot o) =>
        Connected == o.Connected && Showing == o.Showing && Slide == o.Slide &&
        Total == o.Total && SlideId == o.SlideId && Key == o.Key;
}

/// <summary>
/// 页码源：只回答"放映状态是什么"，外加三个操作 PPT 的命令。
/// **自检注入假源**（<see cref="PptFakeSource"/>），产品用 COM 源
/// （<see cref="PptComSource"/>）——测量用的通路和产品用的通路是同一条。
/// </summary>
internal interface IPptSource
{
    PptSnapshot Poll();
    void Next();
    void Prev();
    /// <summary>跳到第 <paramref name="slide"/> 页（1 起）——底部那条进度条松手时用。</summary>
    void GotoSlide(int slide);
    void ExitShow();
}

/// <summary>
/// 轮询线程：**所有 COM 调用都在这一条线程上**（包括 Next/Prev/Exit 命令——
/// 它们从主线程入队、由这里执行）。为什么：COM 对象有公寓（Apartment）亲和性，
/// 让同一条后台线程包办"读状态"和"发命令"，就不存在跨线程 marshalling 的问题。
///
/// 状态一变就 `PostThreadMessage` 唤醒主循环（消息泵本来就在主线程队列上），
/// 主循环 `StepPpt()` 读快照、切页。**轮询间隔 200ms**：翻页的延迟上限就是它。
/// </summary>
internal sealed class PptWatcher
{
    /// <summary>自定义线程消息：PPT 状态变了（主循环在 DrainMessages 里认它）。</summary>
    public const uint WakeMessage = 0x8000 + 0x51;     // WM_APP + 0x51

    private readonly IPptSource _src;
    private readonly uint _wakeThreadId;
    private readonly int _intervalMs;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<(byte cmd, int arg)> _cmds = new();   // 0=下一页 1=上一页 2=结束放映 3=跳到第 n 页
    private readonly object _sync = new();
    private PptSnapshot _snap;
    private bool _dirty;
    private volatile bool _stop;

    /// <summary>这轮轮询真的跑了几次（自检用：证明线程活着、不是"设了就忘了"）。</summary>
    public int PollCount;

    public PptWatcher(IPptSource src, uint wakeThreadId, int intervalMs = 200)
    {
        _src = src;
        _wakeThreadId = wakeThreadId;
        _intervalMs = intervalMs;
        _thread = new Thread(Run) { IsBackground = true, Name = "PptWatch" };
    }

    public void Start() => _thread.Start();
    public void Stop() { _stop = true; _wake.Set(); }

    public PptSnapshot Snapshot() { lock (_sync) return _snap; }

    /// <summary>有新快照吗（取走就清）。主循环每帧问一次，**没变化时一个锁都不拿**。</summary>
    public bool TakeDirty() { lock (_sync) { if (!_dirty) return false; _dirty = false; return true; } }

    /// <summary>主线程排一条命令，由轮询线程执行（见类注释）。</summary>
    public void Post(byte cmd, int arg = 0) { _cmds.Enqueue((cmd, arg)); _wake.Set(); }

    private void Run()
    {
        while (!_stop)
        {
            // ① 先执行命令（按钮点下去最多等一个醒来周期，而不是一个轮询周期）
            while (_cmds.TryDequeue(out var c))
            {
                try
                {
                    if (c.cmd == 0) _src.Next();
                    else if (c.cmd == 1) _src.Prev();
                    else if (c.cmd == 2) _src.ExitShow();
                    else if (c.cmd == 3) _src.GotoSlide(c.arg);
                }
                catch { /* 命令失败不许把轮询线程带走 */ }
            }

            // ② 读一次状态；变了就存快照 + 唤醒主循环
            try
            {
                var s = _src.Poll();
                PollCount++;
                bool changed;
                lock (_sync)
                {
                    changed = !s.SameAs(_snap);
                    _snap = s;
                    if (changed) _dirty = true;
                }
                if (changed && _wakeThreadId != 0)
                    Native.PostThreadMessage(_wakeThreadId, WakeMessage, IntPtr.Zero, IntPtr.Zero);
            }
            catch { /* 读失败当"没连上"处理：下一次轮询再试 */ }

            _wake.WaitOne(_intervalMs);
        }
    }
}

/// <summary>
/// 自检用的假源：状态由测试直接写，命令只改自己的页码，不碰任何外部程序。
/// `--ppttest` 走的就是这条（我这边没有 PowerPoint 环境，产品那条路要用户在
/// 装了 PPT / WPS 的机器上用 `--pptprobe` 确认）。
/// </summary>
internal sealed class PptFakeSource : IPptSource
{
    public bool Connected = true;
    public bool Showing;
    public int Slide = 1;
    public int Total = 3;
    public int SlideId = 256;
    public string Key = "假演示文稿";

    /// <summary>自检用：翻页/跳页命令真的被调到了几次（证明按钮那条路通了）。</summary>
    public int NextCalls, PrevCalls, ExitCalls, GotoCalls;
    /// <summary>自检用：最近一次跳页跳到了第几页。</summary>
    public int GotoTarget;

    public PptSnapshot Poll() => new()
    {
        Connected = Connected, Showing = Showing, Slide = Slide, Total = Total,
        SlideId = SlideId, Key = Key,
    };

    public void Next() { NextCalls++; if (Slide < Total) { Slide++; SlideId += 1; } }
    public void Prev() { PrevCalls++; if (Slide > 1) { Slide--; SlideId -= 1; } }
    public void ExitShow() { ExitCalls++; Showing = false; }

    public void GotoSlide(int slide)
    {
        GotoCalls++;
        GotoTarget = slide;
        int want = Math.Clamp(slide, 1, Math.Max(1, Total));
        SlideId += want - Slide;      // 保持"SlideId 跟着页码走"（和 Next/Prev 同一条规则）
        Slide = want;
    }
}

// 真实的 COM 源（PptComSource）已移到 PptComSource.cs：2026-10-05 为 NativeAOT 改造
// ——去掉 dynamic / 内置 COM / 反射，改用 PptComLate.cs 的原始 vtable 函数指针。
