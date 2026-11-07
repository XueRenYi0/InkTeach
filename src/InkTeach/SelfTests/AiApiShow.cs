using System.IO;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>自检：设置页里的「AI 识别 API」入口 + 原生配置窗。
    ///   --aiapishow &lt;out.bmp&gt;
    /// </summary>
    private void AiApiShow(string outPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== 设置：AI 识别 API ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        ShowHud = false;

        if (CurrentUi is not InkUi.FullUi ui)
        {
            Console.WriteLine("FAIL：UI 不是 FullUi");
            ExitCode = 1;
            return;
        }

        ui.OpenSettingsPageForTest();
        SettleFrames(300);
        string pagePath = Path.ChangeExtension(outPath, null) + "-settings.bmp";
        bool page = ScreenProbe.SaveBmp(pagePath, (int)_virtualX, (int)_virtualY,
                                        (int)_virtualW, (int)_virtualH);

        bool opened = ui.ActivateRowByLabelForTest("AI 识别 API");
        SettleFrames(400);
        bool dialog = ScreenProbe.SaveBmp(outPath, (int)_virtualX, (int)_virtualY,
                                          (int)_virtualW, (int)_virtualH);

        Console.WriteLine($"入口={(opened ? "OK" : "FAIL")}，设置页截图={(page ? "OK" : "FAIL")}，" +
                          $"配置窗截图={(dialog ? "OK" : "FAIL")}");
        Console.WriteLine($"配置：{OcrClient.ConfigPath}");
        Console.WriteLine($"写出：{pagePath} / {outPath}");
        ExitCode = opened && page && dialog ? 0 : 1;
    }
}
