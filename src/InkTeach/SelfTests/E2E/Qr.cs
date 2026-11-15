using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 反馈群二维码（2026-11-03）：图读得出来、能开、点一下能关。
    /// 不验像素——整屏盖一层图，颜色判据在投影/缩放下不稳；这里只钉"能进能出"。
    /// </summary>
    private void QrTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 反馈群二维码 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"    {(ok ? "PASS" : "FAIL")}  {name,-24} {detail}");
        }

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(150);

        ToggleFeedbackQr();
        Check("图能读出来 + 整屏显示",
              FeedbackQrVisible && FeedbackQrImage != null,
              FeedbackQrImage == null ? "图片 null" : $"{FeedbackQrImage.Width}×{FeedbackQrImage.Height}");
        SettleFrames(150);

        float cx = _virtualX + _virtualW * 0.5f, cy = _virtualY + _virtualH * 0.5f;
        SendMouse((int)cx, (int)cy, 0);                              SettleFrames(60);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTDOWN);    SettleFrames(100);
        SendMouse((int)cx, (int)cy, Native.MOUSEEVENTF_LEFTUP);      SettleFrames(100);
        Check("点一下 → 关闭", !FeedbackQrVisible,
              $"可见 = {FeedbackQrVisible}");

        ToggleFeedbackQr();
        SettleFrames(120);
        Check("再点一次 → 又能打开", FeedbackQrVisible, $"可见 = {FeedbackQrVisible}");
        HideFeedbackQr();
        SettleFrames(80);

        Console.WriteLine($"反馈群二维码：通过 {pass}，失败 {fail}");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
