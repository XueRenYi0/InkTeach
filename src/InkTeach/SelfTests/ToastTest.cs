using System.Diagnostics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：轻提示显示 / 到点消失 / 出图。
    /// 用法：--toastshow [out.bmp]（默认 reports/toast.bmp）
    ///
    /// 时长按用户 2026-11-08 定的 2 秒；这里只验"确实显示了、确实自己消失了"，
    /// 不验像素细节（外观由截图人工看）。
    /// </summary>
    private void ToastShow(string outPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 轻提示 ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        ShowHud = false;

        ShowToast("已识别为文本框 · 双击编辑 · 可撤销", 2000);
        SettleFrames(200);
        bool visible = ToastVisible;
        bool shot = ScreenProbe.SaveBmp(outPath, (int)_virtualX, (int)_virtualY,
                                       (int)_virtualW, (int)_virtualH);

        // 截图那 200ms 已经走掉，剩约 1.8s；给调度留余量，1.4–3.2s 都算正常。
        var sw = Stopwatch.StartNew();
        while (ToastVisible && sw.ElapsedMilliseconds < 5000) SettleFrames(50);
        long ms = sw.ElapsedMilliseconds;
        bool gone = !ToastVisible;
        bool timing = ms >= 1400 && ms <= 3200;

        Console.WriteLine($"  显示={visible} 消失={gone} 用时={ms}ms 截图={(shot ? "OK" : "FAIL")}");
        Console.WriteLine($"  写出：{outPath}");
        ExitCode = visible && gone && timing && shot ? 0 : 1;
    }
}
