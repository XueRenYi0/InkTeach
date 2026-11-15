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
    //  Click target (second process) + real pass-through test
    // ------------------------------------------------------------------

    /// <summary>Runs a plain window in its own process that records every time
    /// it is clicked. Used as the window underneath the overlay.</summary>
    private void RunClickTarget(string logFile)
    {
        _clickLogFile = logFile;
        _clickTargetHwnd = Native.CreateWindowEx(
            0, _className, "InkTeach 点击目标",
            0x00CF0000L /*WS_OVERLAPPEDWINDOW*/, 120, 120, 420, 320,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_clickTargetHwnd == IntPtr.Zero)
        {
            File.WriteAllText(logFile, "error\n");
            return;
        }
        Native.ShowWindow(_clickTargetHwnd, 5 /*SW_SHOW*/);
        File.WriteAllText(logFile, "ready\n");

        while (!_quit)
        {
            if (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            else break;
        }
    }

    private static int CountClicks(string file)
    {
        try
        {
            if (!File.Exists(file)) return 0;
            var text = ReadTextShared(file);
            int n = 0, i = 0;
            while ((i = text.IndexOf("clicked", i, StringComparison.Ordinal)) >= 0) { n++; i += 7; }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 读一个"另一个进程正在写"的小文件，**带重试**。
    ///
    /// 为什么需要：点击目标进程每隔一会儿就往这个文件里追加一行，父进程同时在读；
    /// 撞上共享冲突时 `File.ReadAllText` 直接抛 IOException，整条用例会以"异常"收场
    /// 而不是判据失败——套件里就报成 FATAL，看起来像功能坏了。
    /// 这类"测试自己的时序问题"必须和被测的东西分开，不然会浪费很多时间去查一个不存在的 bug。
    /// </summary>
    private static string ReadTextShared(string path, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) when (sw.ElapsedMilliseconds < timeoutMs) { Thread.Sleep(20); }
            catch (Exception) { return ""; }   // 到点了还读不到就当空的，别把用例打成异常
        }
    }
}
