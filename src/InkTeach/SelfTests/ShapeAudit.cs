using InkEngine;
using System.Numerics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 图形面板 **39 张卡逐张仿真**（2026-11-03 用户："把所有图形全部挨个仿真检测"）。
    ///
    /// 每张卡都走正式路径：`SetShapePresetFromUi(p)` 设工具 / 档位 → 按
    /// `ShapeStepCount` 拖 1~2 笔 → 检查 `StrokeKind`、档位、紧框、轮廓、手柄、卡片小样。
    /// 只做几何与对象层，不依赖鼠标/屏幕像素，所以 39 张能在一秒内跑完。
    /// </summary>
    private void ShapeAuditTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 图形面板 · 39 张卡逐张仿真 ===");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            if (!ok) Console.WriteLine($"  失败  {name,-18} {detail}");
        }

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(200);
        PassThrough = false;

        int noHandles = 0, noRotate = 0;
        Span<ShapeHandle> hs = stackalloc ShapeHandle[8];
        foreach (var p in ShapeCatalog.All)
        {
            Doc.Clear();
            Doc.ClearHistory();
            SetShapePresetFromUi(p);

            int steps = ShapeStepCount(p.Tool);
            var drags = new (float Ax, float Ay, float Bx, float By)[steps];
            if (p.Tool == Tool.Parabola)
                drags[0] = p.Variant == 0
                    ? (400f, 500f, 400f, 120f)    // 开口上
                    : (200f, 300f, 600f, 300f);   // 开口右
            else
                drags[0] = (200f, 200f, 620f, 460f);
            for (int k = 1; k < steps; k++)
                drags[k] = (300f, 220f, 520f, 160f);   // 第 2 笔：方向 / 顶点 / 深度
            ShapeStepsForTest(p.Tool, drags);

            var s = Doc.Strokes.Count == 1 ? Doc.Strokes[0] : null;
            var wantKind = KindOfShapeTool(p.Tool);
            Check($"{p.Label}：画出来是 {wantKind}",
                  s != null && s.Kind == wantKind,
                  s == null ? $"对象 {Doc.Strokes.Count} 个" : $"Kind={s.Kind}");

            bool variantOk = true;
            if (s != null)
            {
                variantOk = p.Tool switch
                {
                    Tool.Coordinate => s.Grid == (p.Variant == 0),
                    Tool.ConicEllipse => s.FocusTriangle == (p.Variant == 0),
                    Tool.Hyperbola => s.ShowAsymptotes == (p.Variant == 0),
                    Tool.Parabola => s.CurveAxis == (p.Variant == 0 ? CurveAxis.OpenUp : CurveAxis.OpenRight),
                    Tool.Prism or Tool.Pyramid or Tool.Frustum => s.PrismSidesClamped == p.Variant,
                    _ => true,
                };
            }
            Check($"{p.Label}：档位 {p.Variant} 落对", variantOk,
                  s == null ? "没有对象" : $"Kind={s.Kind} V={p.Variant}");

            if (s != null)
            {
                var b = s.WorldInkBounds;
                bool finite = float.IsFinite(b.MinX) && float.IsFinite(b.MinY)
                           && float.IsFinite(b.MaxX) && float.IsFinite(b.MaxY)
                           && b.MaxX > b.MinX && b.MaxY > b.MinY;
                int outline = 0;
                foreach (var q in s.ShapeOutline())
                    if (float.IsFinite(q.X) && float.IsFinite(q.Y)) outline++;
                Check($"{p.Label}：紧框/轮廓有效", finite && outline > 0,
                      $"框 {b.MinX:F0},{b.MinY:F0}..{b.MaxX:F0},{b.MaxY:F0}，点 {outline}");

                int hn = SelectionHandles.ShapeHandlesOf(s, hs);
                if (hn == 0) noHandles++;
                if (!SelectionHandles.RotateHandleVisible(s)) noRotate++;
                Console.WriteLine($"  {p.Label,-10} {s.Kind,-15} handles={hn} "
                                + $"rot={(SelectionHandles.RotateHandleVisible(s) ? "Y" : "N")} "
                                + $"outline={outline}");
            }

            var pv = ShapePreview.Create(p);
            Check($"{p.Label}：卡片小样", pv.Kind == wantKind && pv.Points.Count > 0,
                  $"{pv.Kind}，{pv.Points.Count} 点");
        }

        Console.WriteLine();
        Console.WriteLine($"  无特殊手柄（走通用八手柄）的卡：{noHandles} 张");
        Console.WriteLine($"  无旋转柄的卡：{noRotate} 张");
        Console.WriteLine($"图形全量仿真：通过 {pass} 项，失败 {fail} 项");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
