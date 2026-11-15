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
}
