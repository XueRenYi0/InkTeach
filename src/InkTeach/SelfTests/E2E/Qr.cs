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
        bool savedBoard = BoardOn;
        BoardOn = true;                      // 白板打底：遮罩下面是确定的颜色（不受桌面影响）
        Doc.InvalidateAll();
        SettleFrames(150);

        // 遮罩外的一个角（卡片居中，左上角一定在遮罩里、卡片外）
        int qx = _virtualX + 30, qy = _virtualY + _virtualH / 2;
        byte[] under = ScreenProbe.CaptureRegion(qx, qy, 8, 8);

        ToggleFeedbackQr();
        Check("图能读出来 + 整屏显示",
              FeedbackQrVisible && FeedbackQrImage != null,
              FeedbackQrImage == null ? "图片 null" : $"{FeedbackQrImage.Width}×{FeedbackQrImage.Height}");
        SettleFrames(150);

        // 2026-11-16 残影评审 P2：遮罩画在脏区循环外、整屏 55% 黑。开着时每帧都要整屏重画，
        // 否则静止区域每帧再叠一层，一帧比一帧暗（几十帧后就成纯黑）。
        // 判据：停 400ms（几十帧）后，角上那块 ≈ 板色 × 0.45（只叠了一层），而不是接近 0。
        SettleFrames(400);
        byte[] masked = ScreenProbe.CaptureRegion(qx, qy, 8, 8);
        int worstDim = 0;
        if (under.Length == masked.Length && under.Length >= 4)
            for (int i = 0; i + 3 < under.Length; i += 4)
                for (int c = 0; c < 3; c++)
                    worstDim = Math.Max(worstDim, Math.Abs(masked[i + c] - (int)MathF.Round(under[i + c] * 0.45f)));
        Check("遮罩停住不越叠越暗", under.Length >= 4 && worstDim <= 12,
              under.Length >= 4
                  ? $"底 rgb({under[2]},{under[1]},{under[0]}) → 遮罩 rgb({masked[2]},{masked[1]},{masked[0]})"
                    + $"，与「一层 55% 黑」差 {worstDim}"
                  : "抓屏失败");

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
        SettleFrames(200);
        // 关掉之后两个后缓冲都得擦干净（消失那一帧整屏重画两帧），角上回到原来的板色
        byte[] restored = ScreenProbe.CaptureRegion(qx, qy, 8, 8);
        int left = ScreenProbe.DiffCount(under, restored, 12);
        Check("关掉后遮罩擦干净", left == 0, $"角上还有 {left} 像素不是原来的板色");
        BoardOn = savedBoard;
        SettleFrames(80);

        Console.WriteLine($"反馈群二维码：通过 {pass}，失败 {fail}");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
