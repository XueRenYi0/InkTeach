// 探针专用：PredictedPoint 的最小定义（与 InkPredictor.cs 里的同形）。
// 单独编译 DlPredictController 时用它替代引擎里的那份，避免拖进整个 InkPredictor。
namespace InkEngine;

internal struct PredictedPoint
{
    public float X, Y;
    public double TimeMs;
}
