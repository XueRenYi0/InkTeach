using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 自检取消（2026-10-18 用户："自检停不下来，怎么都关不掉"）。
///
/// 为什么不能在主线程上等热键：
///   · 现有 `Ctrl+Alt+Shift+X` 是 RegisterHotKey 到覆盖层 HWND 的，WM_HOTKEY 由
///     UI 线程处理；自检一旦把 UI 线程占住（长序列化、同步探测、等子进程），
///     这个键就只能在队列里排队，按了等于没按。
///   · 自检窗口是 WS_EX_TOOLWINDOW（不进任务栏）、WS_EX_NOACTIVATE（没有焦点），
///     所以 Alt+F4、点任务栏、关窗口都指望不上。
///
/// 做法：**独立看门狗线程**，不碰 UI 消息循环：
///   · 每 25ms 直接读 `GetAsyncKeyState`，检测 Ctrl+Alt+Shift+C；
///   · 按住不放只在“按下那一下”触发（边沿），不会连发；
///   · 触发后先尽量记日志，再杀掉自检自己启动的子进程，最后 `Environment.Exit(130)`。
///   · 终端里也可以 Ctrl+C（Console.CancelKeyPress）。
///
/// 只在自检/测试模式启动，产品模式没有这条线程。
/// </summary>
internal static class SelfTestCancel
{
    static volatile bool _started;

    public const int ExitCodeCancelled = 130;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            Console.WriteLine("[自检] 取消键：Ctrl+Alt+Shift+C（全局；UI 卡住也能停）");
            Console.WriteLine("[自检] 终端里也可以 Ctrl+C。取消会把自检子进程一起结束。");
        }
        catch { }

        try
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;                 // 不让默认 Ctrl+C 先杀进程，走统一收尾
                Cancel("终端 Ctrl+C");
            };
        }
        catch { }

        var t = new Thread(Watch)
        {
            IsBackground = true,
            Name = "SelfTestCancel",
            Priority = ThreadPriority.AboveNormal,   // 只读键，几乎不占 CPU；UI 被占时也能跑
        };
        t.Start();
    }

    static void Watch()
    {
        // 起跑时如果这几个键本来就按着，先等松开，避免“一启动就取消”。
        while (Down(VK_CONTROL) && Down(VK_MENU) && Down(VK_SHIFT) && Down(VK_C))
        {
            try { Thread.Sleep(50); } catch { }
        }

        bool was = false;
        while (true)
        {
            bool now = Down(VK_CONTROL) && Down(VK_MENU) && Down(VK_SHIFT) && Down(VK_C);
            if (now && !was)
            {
                Cancel("Ctrl+Alt+Shift+C");
                return;
            }
            was = now;
            try { Thread.Sleep(25); } catch { }
        }
    }

    static void Cancel(string reason)
    {
        try { Console.WriteLine("[自检] 已取消（" + reason + "），正在退出…"); } catch { }

        try { SelfTestTracker.KillChildren(); } catch { }

        // 直接结束进程：UI 线程可能正卡在同步探测/子进程等待里，不能等它自己退。
        // 但 Environment.Exit 会跑 ProcessExit（日志 flush/写盘），极端情况下也可能被
        // 慢盘/锁拖住——所以 1.5 秒还没退就强制 Kill 自己。取消键必须“一定能停”。
        var exit = new Thread(() =>
        {
            try { Environment.Exit(ExitCodeCancelled); } catch { }
        })
        {
            IsBackground = true,
            Name = "SelfTestCancel-Exit",
        };
        exit.Start();
        try { exit.Join(1500); } catch { }
        try { Process.GetCurrentProcess().Kill(); } catch { }
    }

    // ---- 键盘状态 ----
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_C = 0x43;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}

/// <summary>
/// 自检启动的子进程（`--clicktarget` 等）。取消时一起杀掉，免得留下一个置顶小窗、
/// 下一次自检或 DLL 被它们挡住。
/// </summary>
internal static class SelfTestTracker
{
    static readonly object _lk = new();
    static readonly List<Process> _children = new();

    public static void Track(Process p)
    {
        if (p == null) return;
        lock (_lk) _children.Add(p);
    }

    public static void KillChildren()
    {
        lock (_lk)
        {
            foreach (var p in _children)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
            _children.Clear();
        }
    }
}
