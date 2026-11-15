using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    // ------------------------------------------------------------------
    //  Synthetic input test
    // ------------------------------------------------------------------

    private static Native.INPUT MouseInput(int x, int y, uint extraFlags, int vx, int vy, int vw, int vh)
    {
        var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
        inp.mi.dx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        inp.mi.dy = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        inp.mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE
                       | Native.MOUSEEVENTF_VIRTUALDESK | extraFlags;
        return inp;
    }

    private void SendMouse(int x, int y, uint extraFlags)
    {
        var inp = new[] { MouseInput(x, y, extraFlags, _virtualX, _virtualY, _virtualW, _virtualH) };
        Native.SendInput(1, inp, Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT_KBD KeyInput(ushort vk, bool up)
    {
        var inp = new Native.INPUT_KBD { type = Native.INPUT_KEYBOARD };
        inp.ki.wVk = vk;
        inp.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0u;
        return inp;
    }

    /// <summary>
    /// 合成键盘：把一串虚拟键**按顺序按下、再倒序抬起**（Ctrl+Alt+O 这种组合必须这样发，
    /// 只按主键是触发不了全局热键的）。
    ///
    /// 为什么要它（而不是像批注内按键那样直接 `PostMessage`）：全局热键是**系统**在
    /// 键盘输入流上判定的，`RegisterHotKey` 收到的是真实的按键；要验"按下去真的切了工具"，
    /// 就得走真实的键盘通路。批注内那些键不用它是因为它们由我们自己的窗口收，
    /// 直接投递窗口消息就能到。
    /// </summary>
    private uint SendKeyChord(params ushort[] vks)
    {
        var seq = new List<Native.INPUT_KBD>();
        foreach (var vk in vks) seq.Add(KeyInput(vk, false));
        for (int i = vks.Length - 1; i >= 0; i--) seq.Add(KeyInput(vks[i], true));
        // 返回值＝**真的塞进系统输入流的事件个数**。自检要把它打出来：
        // 它比"工具没切"多一层信息——分得清"按键没进系统"和"进了但热键没响应"。
        return Native.SendInput((uint)seq.Count, seq.ToArray(), Marshal.SizeOf<Native.INPUT_KBD>());
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;        // Alt
    private const ushort VK_SHIFT = 0x10;

    /// <summary>按一次 Ctrl+Alt+&lt;主键&gt;（现在只给"退役图形键不响应"那条测试用；
    /// 2026-10-04 起全局热键都是 `Ctrl+Alt+Shift+…` 的形状）。返回塞进去的事件数。</summary>
    private uint SendCtrlAlt(ushort vk) => SendKeyChord(VK_CONTROL, VK_MENU, vk);

    /// <summary>
    /// Drives a real drag through SendInput so the whole input path is exercised
    /// end to end - pointer messages, stroke accumulation, redraw, and finally
    /// pixels on screen. Synthetic input is consumed by this overlay, which is
    /// topmost, so nothing underneath is clicked.
    /// </summary>
}
