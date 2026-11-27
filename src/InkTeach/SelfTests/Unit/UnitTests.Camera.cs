using InkEngine;

namespace InkTeach;

/// <summary>
/// 相机（ViewScale / ViewOffsetX / ViewOffsetY）的坐标换算。
///
/// 阶段 1 的硬约束：**ViewScale 恒为 1**，所以先钉住"屏幕↔画布往返一致"，
/// 再顺手验一遍缩放公式本身（用临时实例，不改产品默认值）。
/// </summary>
internal static class UnitTestsCamera
{
    [UnitTest]
    internal static void Camera_Helpers_RoundTrip_At_Scale1()
    {
        var e = new InkEngine.InkEngine { ViewScale = 1f };

        // 代表值：0 / 整屏偏移 / 小数偏移 / 负屏（多显示器在 primary 上方）
        var offsets = new[] { 0f, -1800f, 37.5f, -5231.25f };
        var values = new[] { 0f, 1f, -960f, 1919.5f, 123456.75f };

        foreach (float offset in offsets)
        {
            e.ViewOffsetY = offset;
            e.ViewOffsetX = 0f;
            foreach (float v in values)
            {
                // scale 1 + offsetX 0 时 X 必须原样通过
                Check.Near(v, e.ScreenToCanvasX(v), file: "ViewScale=1");
                Check.Near(v, e.CanvasToScreenX(v), file: "ViewScale=1");

                float canvasY = e.ScreenToCanvasY(v);
                Check.Near(v - offset, canvasY, file: "ViewScale=1");
                Check.Near(v, e.CanvasToScreenY(canvasY), file: "ViewScale=1 round-trip");
            }
        }

        // ref 版本必须和单值版本同一套（输入第一跳就用它）
        float x = 640f, y = 480f;
        e.ViewOffsetY = -120f;
        e.ViewOffsetX = 0f;
        e.ScreenToCanvas(ref x, ref y);
        Check.Near(640f, x, file: "ScreenToCanvas");
        Check.Near(600f, y, file: "ScreenToCanvas");
    }

    [UnitTest]
    internal static void Camera_Helpers_ScaleFormula_IsSymmetric()
    {
        var e = new InkEngine.InkEngine { ViewScale = 1.25f, ViewOffsetX = -100f, ViewOffsetY = -1800f };

        foreach (float cx in new[] { -500f, 0f, 960f, 4321.5f })
        {
            float sx = e.CanvasToScreenX(cx);
            Check.Near(cx * 1.25f - 100f, sx, file: "CanvasToScreenX");
            Check.Near(cx, e.ScreenToCanvasX(sx), file: "ScreenToCanvasX round-trip");
        }
        foreach (float cy in new[] { -500f, 0f, 1080f, 9876.25f })
        {
            float sy = e.CanvasToScreenY(cy);
            Check.Near(cy * 1.25f - 1800f, sy, file: "CanvasToScreenY");
            Check.Near(cy, e.ScreenToCanvasY(sy), file: "ScreenToCanvasY round-trip");
        }
    }

    [UnitTest]
    internal static void DocPanFling_FastFlick_CoastsAndDecays_SlowDragDoesNot()
    {
        var f = new DocPanFling();

        // 1. 快甩：起惯性 + 限幅不超过 UiTouchFling.MaxVelocity + 指数衰减至停
        f.Begin(0.0);
        f.Sample(15f, 45f, 16.0);
        f.Sample(15f, 45f, 32.0);
        f.Sample(15f, 45f, 48.0);
        Check.True(f.Release(52.0, allowX: true), "快甩必须起惯性");
        Check.True(f.Active, "惯性应激活");
        float launchSpeed = MathF.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
        Check.True(launchSpeed <= UiTouchFling.MaxVelocity + 1e-4f, "初速度必须受 MaxVelocity 限制");

        double t = 52.0;
        float prevSpeed = launchSpeed;
        int steps = 0;
        while (f.Step(t += 16.0, out float dx, out float dy))
        {
            float curSpeed = MathF.Sqrt(f.Vx * f.Vx + f.Vy * f.Vy);
            Check.True(curSpeed < prevSpeed, "惯性速度必须单调衰减");
            Check.True(dx > 0f && dy > 0f, "步进位移方向应与初速度一致");
            prevSpeed = curSpeed;
            steps++;
            if (steps > 200) throw new Exception("惯性未在预期帧数内衰减停止");
        }
        Check.True(steps > 0, "快甩至少滑行 1 帧");
        Check.True(!f.Active, "衰减到阈值以下必须停止");

        // 2. 慢拖不起惯性
        f.Begin(1000.0);
        f.Sample(0.5f, 1.0f, 1020.0);
        f.Sample(0.5f, 1.0f, 1060.0);
        Check.True(!f.Release(1070.0, allowX: true), "慢拖不应起惯性");
        Check.True(!f.Active, "慢拖后不应激活");

        // 3. 停手 >120ms 再松手不起惯性
        f.Begin(2000.0);
        f.Sample(20f, 40f, 2016.0);
        f.Sample(20f, 40f, 2032.0);
        Check.True(!f.Release(2160.0, allowX: true), "停住超过 120ms 松手不应起惯性");
        Check.True(!f.Active, "停顿后不应激活");

        // 4. allowX=false 时 X 严格为 0
        f.Begin(3000.0);
        f.Sample(30f, 25f, 3016.0);
        f.Sample(30f, 25f, 3032.0);
        Check.True(f.Release(3040.0, allowX: false), "纵向快甩 allowX=false 仍应起惯性");
        Check.Near(0f, f.Vx, 1e-6f);
        Check.True(f.Step(3056.0, out float stepX, out float stepY), "allowX=false 步进应有效");
        Check.Near(0f, stepX, 1e-6f);
        Check.True(stepY > 0f, "allowX=false 仅 Y 应有位移");

        // 5. 撞边即停：单轴撞边清空该轴，双轴都停时惯性退出
        f.Begin(4000.0);
        f.Sample(20f, 20f, 4016.0);
        f.Sample(20f, 20f, 4032.0);
        Check.True(f.Release(4040.0, allowX: true), "准备双轴惯性");
        f.ClampStoppedAxes(stoppedX: true, stoppedY: false);
        Check.Near(0f, f.Vx, 1e-6f);
        Check.True(f.Active, "仅 X 撞边时 Y 惯性应继续");
        f.ClampStoppedAxes(stoppedX: false, stoppedY: true);
        Check.Near(0f, f.Vy, 1e-6f);
        Check.True(!f.Active, "双轴都撞边时必须停掉惯性");
    }

    [UnitTest]
    internal static void TouchGestures_ZoomedOut_UsesScreenCoordsForPairDistAndPath()
    {
        var tg = new TouchGestures { Enabled = true };

        // 50% 缩放：画布间距 600 (> PairMaxLogical 420)，但屏幕间距 300 (<= 420)
        var v1 = tg.Down(1, x: 200f, y: 200f, sizePx: 0f, nowMs: 100.0, dpi: 1f, screenX: 100f, screenY: 100f);
        var v2 = tg.Down(2, x: 800f, y: 200f, sizePx: 0f, nowMs: 110.0, dpi: 1f, screenX: 400f, screenY: 100f);
        Check.Equal(TouchVerdict.Write, v1);
        Check.Equal(TouchVerdict.Gesture2, v2);

        // 路径累加：画布位移 20px，屏幕位移 5px -> Path 必须按屏幕位移累计为 5px
        tg.Reset();
        tg.Down(1, x: 200f, y: 200f, sizePx: 0f, nowMs: 200.0, dpi: 1f, screenX: 100f, screenY: 100f);
        tg.Move(1, x: 220f, y: 200f, dpi: 1f, screenX: 105f, screenY: 100f);
        Check.Equal(1, tg.Views.Count);
        Check.Near(5f, tg.Views[0].Path, 0.001f);
        Check.True(!tg.Views[0].Moved, "屏幕仅移动 5px 不应触发 Moved");
    }

    [UnitTest]
    internal static void DocPan_PenHotkey_ExitsHandModeWithoutCyclingColor()
    {
        var e = new InkEngine.InkEngine();
        e.DocView.Open(
            new List<DocPages.Spec> { new() { OutW = 800, OutH = 600, Source = "mem.pdf" } },
            "mem", 400f, 0f, 24f);
        e.Tool = Tool.Pen;
        e.DocPanMode = true;
        int swatchBefore = e.PenSwatchSelected;

        e.RunActionForTest(KeyAction.ToolPen);

        Check.True(!e.DocPanMode, "在手掌模式下按画笔快捷键必须退出手掌模式");
        Check.Equal(Tool.Pen, e.Tool);
        Check.Equal(swatchBefore, e.PenSwatchSelected);
    }
}
