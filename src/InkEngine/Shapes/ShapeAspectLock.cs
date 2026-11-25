// [职责] 图形绘制期的正比例约束：Shift 把矩形夹成正方形、椭圆夹成正圆。
// [测试] --unittest ShapeAspectLock

using System.Numerics;

namespace InkEngine;

/// <summary>
/// 绘制期的 **1:1 正比例** 约束（阶段 2，2026-11-25）：只做几何、不读引擎状态，
/// 所以自检可以直接钉它（四个象限的符号必须保住）。
///
/// 口径（和 Office / Figma 的 Shift 一致）：取较**长**的那条边为准，
/// 也就是"拖到哪、框就至少到你拖的那个点"——用短边会把形状缩回去，手感是"越拖越小"。
/// </summary>
internal static class ShapeAspectLock
{
    /// <summary>
    /// 把"起点 → 指针"这个框夹成 1:1：**保留拖拽象限方向**，边长取 `max(|dx|, |dy|)`。
    /// 零长度（还没拖出去）原样返回。
    /// </summary>
    internal static Vector2 FitSquare(Vector2 origin, Vector2 pointer)
    {
        float dx = pointer.X - origin.X;
        float dy = pointer.Y - origin.Y;
        float len = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
        if (!(len > 1e-4f)) return pointer;
        return new Vector2(origin.X + MathF.CopySign(len, dx),
                           origin.Y + MathF.CopySign(len, dy));
    }
}
