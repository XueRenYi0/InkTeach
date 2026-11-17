using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// `--uidragghost`：**强制整屏上屏**（`--fullpresent` 语义）下合成鼠标拖工具条 30 帧，
    /// 每帧沿拖动轨迹抓屏，看有没有"本帧不该有的旧界面像素"（2026-11-16 残影评审 P0-1 / 5.1）。
    ///
    /// 为什么要开整屏上屏：残影只在"这一帧走 `Present()` 整屏兜底、却只重画了脏区"时出现——
    /// 后缓冲里是两帧前的画面，界面只记了一帧旧位置，N-2 帧那块旧界面就被整屏交上去了。
    /// 平时要碰巧凑上"脏区 >80% / >16 块"才触发（用户说的"偶尔"），开着它每帧都触发，
    /// 修之前**必然** FAIL、修之后必须 PASS。
    ///
    /// 判据的"真值"：
    ///   · 起点矩形以外 → 拖动前那张基准图（那里是白板底）；
    ///   · 起点矩形以内、终点矩形以外 → 拖完后**强制整屏重画两帧**再抓的那张（两个缓冲都干净）；
    ///   · 两个都在里面的像素不判（那里本来就该有界面）。
    /// 每步停 3~4 帧再抓（抓屏比 QueryBounds 晚一两帧），只允许界面出现在这一帧的矩形里，外面再有差异就算残影。
    /// </summary>
    /// <param name="areaMode">
    /// `--uidragghost area`：**不**强制整屏上屏，左上角开性能面板、右下角放一个选中对象（两块临时图元
    /// 一左上一右下），拖同样 30 步，打出每帧上屏面积 / 整屏兜底帧数——看"临时图元按块记脏区"
    /// （残影评审 P1-1）到底省了多少。残影判据照旧。
    /// </param>
    private void UiDragGhostTest(bool areaMode = false)
    {
        Console.WriteLine();
        Console.WriteLine(areaMode
            ? "=== 界面拖动残影 · 面积模式（局部上屏 + 左上 HUD + 右下选中对象，拖工具条 30 帧）==="
            : "=== 界面拖动残影（强制整屏上屏 + 合成鼠标拖工具条 30 帧）===");

        if (SkipIfNoSyntheticInput("界面拖动残影（需要合成鼠标拖工具条）")) { _quit = true; return; }

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        if (CurrentUi is not InkUi.FullUi ui || _windows.Count == 0)
        {
            Console.WriteLine("  FAIL: 界面没挂上");
            ExitCode = 1; _quit = true; return;
        }

        bool savedFull = OverlayWindow.FullPresent;
        OverlayWindow.FullPresent = !areaMode;
        Doc.Clear();
        Doc.ClearHistory();
        BoardOn = true;                       // 白板打底：桌面上的别的程序不会混进判据
        Tool = Tool.Pen;
        if (areaMode)
        {
            ShowHud = true;                   // 左上角
            var s = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0.9f, 0.2f, 0.2f, 1f), Width = 6f,
            };
            float sx = _virtualX + _virtualW - 220f * DpiScale, sy = _virtualY + _virtualH * 0.55f;
            for (int i = 0; i <= 4; i++) s.AddPoint(sx + i * 30f * DpiScale, sy + (i % 2) * 20f * DpiScale, 0.5f, i * 16);
            Doc.AddStroke(s);
            Doc.SelectOnly(new[] { s });      // 右下：选中框 + 操作条
        }
        Doc.InvalidateAll();
        float k = DpiScale * UiScale;
        float pad = UiPaintMarginNow * k + 3f;

        RectF Phys(RectF r) => new()
        {
            MinX = r.MinX * k, MinY = r.MinY * k, MaxX = r.MaxX * k, MaxY = r.MaxY * k,
        };

        int parkX = _virtualX + 40, parkY = _virtualY + 40;
        SendMouse(parkX, parkY, 0);
        SettleFrames(500);

        var start = Phys(ui.QueryBounds());
        var bar = Phys(ui.BarRectForTest);
        const int steps = 30;
        // 往上拖、三档高度轮流跳（0 / −d / −2d）：
        //   · 每步必须**大于投影余量**——小步拖时 N-2 那一条只落在透明投影里，肉眼和判据都看不见；
        //   · 三档轮换才保证 N-2 ≠ N、≠ N-1（两档来回跳时 N-2 = N，残影被自己盖住）。
        // 第 30 步落回起点高度，拖完停住时旧位置（−d / −2d）上的残影最显眼。
        float d = MathF.Max(3f * pad, 60f * DpiScale);
        d = MathF.Min(d, (start.MinY - _virtualY - pad - 20f) / 2f);

        // 抓屏带：起点 ∪ 三档高度整条轨迹，带上投影余量
        int bx = (int)MathF.Max(_virtualX, start.MinX - pad - 8f);
        int by = (int)MathF.Max(_virtualY, start.MinY - 2f * d - pad - 8f);
        int br = (int)MathF.Min(_virtualX + _virtualW, start.MaxX + pad + 8f);
        int bb = (int)MathF.Min(_virtualY + _virtualH, start.MaxY + pad + 8f);
        int bw = br - bx, bh = bb - by;
        if (bw <= 0 || bh <= 0)
        {
            Console.WriteLine($"  FAIL: 界面矩形不对 ({start.MinX:F0},{start.MinY:F0})..({start.MaxX:F0},{start.MaxY:F0})");
            OverlayWindow.FullPresent = savedFull;
            ExitCode = 1; _quit = true; return;
        }

        byte[] baseline = ScreenProbe.CaptureRegion(bx, by, bw, bh);

        float px = (bar.MinX + bar.MaxX) * 0.5f, py = (bar.MinY + bar.MaxY) * 0.5f;
        SendMouse((int)px, (int)py, 0); SettleFrames(60);
        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTDOWN); SettleFrames(50);

        var frames = new byte[steps][];
        var allowed = new RectF[steps];
        var fullCount = 0;
        float maxMoved = 0f;
        // 面积模式：逐帧记上屏面积（SettleFrames 同一套"抽消息 + 渲染"，只是每帧读一次读数）
        int areaFrames = 0, areaFullFrames = 0;
        double areaSum = 0, areaMax = 0;
        void SettleAndMeasure(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                PumpMessages();
                StepCameraAnim();
                UpdatePredictTail();
                RenderAll();
                var w0 = _windows[0];
                areaFrames++;
                areaSum += w0.LastPresentAreaPercent;
                areaMax = Math.Max(areaMax, w0.LastPresentAreaPercent);
                if (w0.LastPresentFull) areaFullFrames++;
            }
        }
        for (int i = 1; i <= steps; i++)
        {
            SendMouse((int)px, (int)(py - (i % 3) * d), 0);
            // 停 3~4 帧再抓：抓屏比 QueryBounds 晚一两帧（DWM 合成），只允许界面出现在这一帧的位置
            SettleAndMeasure(45);
            var cur = Phys(ui.QueryBounds());
            allowed[i - 1] = cur.Inflate(pad);
            frames[i - 1] = ScreenProbe.CaptureRegion(bx, by, bw, bh);
            if (_windows[0].LastPresentFull) fullCount++;
            maxMoved = MathF.Max(maxMoved, start.MaxY - cur.MaxY);
        }
        SendMouse((int)px, (int)py, Native.MOUSEEVENTF_LEFTUP);
        SettleFrames(60);
        SendMouse(parkX, parkY, 0);
        SettleFrames(400);

        var final = Phys(ui.QueryBounds());
        byte[] finalShot = ScreenProbe.CaptureRegion(bx, by, bw, bh);

        // 真值：两帧整屏重画之后，两个后缓冲都是干净的
        foreach (var w in _windows) w.RequestFullFrames();
        SettleFrames(300);
        byte[] truth = ScreenProbe.CaptureRegion(bx, by, bw, bh);

        var startIn = start.Inflate(pad);
        var finalIn = final.Inflate(pad);

        int Ghost(byte[] shot, RectF ok)
        {
            if (shot == null || shot.Length != baseline.Length || truth.Length != baseline.Length) return int.MaxValue;
            int n = 0;
            for (int y = 0; y < bh; y++)
            {
                float sy = by + y + 0.5f;
                for (int x = 0; x < bw; x++)
                {
                    float sx = bx + x + 0.5f;
                    if (Inside(ok, sx, sy)) continue;
                    byte[] exp;
                    if (!Inside(startIn, sx, sy)) exp = baseline;
                    else if (!Inside(finalIn, sx, sy)) exp = truth;
                    else continue;
                    int o = (y * bw + x) * 4;
                    if (Math.Abs(shot[o] - exp[o]) > 24 || Math.Abs(shot[o + 1] - exp[o + 1]) > 24
                        || Math.Abs(shot[o + 2] - exp[o + 2]) > 24) n++;
                }
            }
            return n;
        }

        int worst = 0, worstAt = -1, badFrames = 0;
        for (int i = 0; i < steps; i++)
        {
            int g = Ghost(frames[i], allowed[i]);
            if (g >= 200) badFrames++;
            if (g > worst) { worst = g; worstAt = i; }
        }
        int finalGhost = ScreenProbe.DiffCount(finalShot, truth, 24);

        string shotPath = Path.Combine("reports", "shots", "uidragghost.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(shotPath));
        ScreenProbe.SaveBuffer(shotPath, worstAt >= 0 ? frames[worstAt] : finalShot, bw, bh);

        Console.WriteLine($"  抓屏带 ({bx},{by}) {bw}×{bh}；投影余量 {pad:F0}px；每档 {d:F0}px");
        Console.WriteLine($"  界面起点 ({start.MinX:F0},{start.MinY:F0})..({start.MaxX:F0},{start.MaxY:F0})"
                          + $" → 终点 ({final.MinX:F0},{final.MinY:F0})..({final.MaxX:F0},{final.MaxY:F0})，最高拖上去 {maxMoved:F0}px");
        if (areaMode)
            Console.WriteLine($"  拖动期间上屏面积: 平均 {(areaFrames > 0 ? areaSum / areaFrames : 0):F1}%  最大 {areaMax:F1}%"
                              + $"  整屏上屏 {areaFullFrames}/{areaFrames} 帧（选中 {Doc.Selected.Count} 个）");
        else
            Console.WriteLine($"  整屏上屏帧: {fullCount}/{steps}（应当 = {steps}）");
        Console.WriteLine($"  逐帧残影: 最坏 {worst} 像素（第 {worstAt + 1} 帧），超 200 像素的帧 {badFrames}/{steps}");
        Console.WriteLine($"  拖完停住：与整屏重画真值的差异 {finalGhost} 像素");
        Console.WriteLine($"  截图: {Path.GetFullPath(shotPath)}");

        bool dragged = maxMoved > d * 1.5f;
        bool ok = dragged && (areaMode || fullCount == steps) && badFrames == 0 && finalGhost < 200;
        Console.WriteLine(ok
            ? areaMode ? "  PASS: 局部上屏下拖动工具条，没有旧界面像素" : "  PASS: 整屏上屏下拖动工具条，没有旧界面像素"
            : !dragged ? $"  FAIL: 工具条没拖动（最高只上去 {maxMoved:F0}px）"
            : !areaMode && fullCount != steps ? "  FAIL: 不是每帧都整屏上屏，判据不成立"
            : "  FAIL: 拖动轨迹上留着旧界面像素（残影）");

        OverlayWindow.FullPresent = savedFull;
        BoardOn = false;
        ui.SetAnchorForTest(null);
        SettleFrames(100);
        ExitCode = ok ? 0 : 1;
        _quit = true;

        static bool Inside(RectF r, float x, float y) => x >= r.MinX && x < r.MaxX && y >= r.MinY && y < r.MaxY;
    }
}
