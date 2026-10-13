// 本文件由 App.cs 拆出（2026-10-07）：Helpers 这一组。
// **纯搬家，逻辑一字未改** —— 靠 partial class 共享 App 的私有成员。
// 拆开的目的：产品代码与自检代码互不干扰，人和 AI 读代码时不必互相穿插。

using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{


    private void GenerateStrokes(int strokeCount)
    {
        var rnd = new Random(20260913);
        Doc.Clear();
        for (int i = 0; i < strokeCount; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = PenColor,
                Width = 2.5f + (float)rnd.NextDouble() * 4f,
                HasPressure = _benchPressure,   // 默认 false = 老行为；开了才走变宽那条渲染路
            };
            float x = _virtualX + (float)rnd.NextDouble() * _virtualW;
            float y = _virtualY + (float)rnd.NextDouble() * _virtualH;
            // ⚠ `rnd.Next(24)` 无论开不开都要调 —— 不然随机数序列会错位，
            // 默认那一路的基线就不一样了（老基准的数字就没法跟以前比）。
            int ptsRand = 8 + rnd.Next(24);
            int pts = _benchPts > 0 ? _benchPts : ptsRand;
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            for (int j = 0; j < pts; j++)
            {
                ang += (float)((rnd.NextDouble() - 0.5) * 0.7);
                float d = 5f + (float)rnd.NextDouble() * 9f;
                x += MathF.Cos(ang) * d;
                y += MathF.Sin(ang) * d;
                x = Math.Clamp(x, _virtualX + 1, _virtualX + _virtualW - 1);
                y = Math.Clamp(y, _virtualY + 1, _virtualY + _virtualH - 1);
                // 压感模式下给一条**像真写字**的压力起伏（起笔轻、中段重、收笔轻），
                // 而不是纯随机 —— 纯随机会让每一笔的宽度乱跳，和真实几何差得远。
                float pv;
                if (_benchPressure)
                {
                    float u = pts <= 1 ? 0.5f : j / (float)(pts - 1);
                    pv = Math.Clamp(0.12f + 0.85f * MathF.Sin(u * MathF.PI)
                                    + (float)rnd.NextDouble() * 0.06f, 0.02f, 1f);
                }
                else pv = (float)rnd.NextDouble();
                s.AddPoint(x, y, pv, NowMs);
            }
            Doc.AppendStroke(s);
        }
        Doc.ClearHistory();
    }

    /// <summary>
    /// 铺开 <paramref name="screens"/> 屏的笔画。
    ///
    /// 为什么要有它：<see cref="GenerateStrokes"/> 把笔画全塞在一屏里，那是
    /// "满屏"的极端形状，和真实板书不一样。真实情况是**纵向累积**——一节课
    /// 往下写十几屏，每屏只有那么多字。两者的差别对分块缓存是决定性的：
    /// 前者的重建代价随总量涨，后者不该涨。
    /// </summary>
    /// <summary>`--benchpressure`：基准里的笔画**带压感**（走 ID2D1Ink 那条变宽渲染路）。
    ///
    /// 为什么要有它（2026-10-07）：原来的基准笔画**没设 `HasPressure`**，
    /// 于是"压感开/关"对它毫无影响 —— 拿它做压感的 A/B 是**假的**（我做过一次，
    /// 两边数字一模一样、连点数都相同，才发现这点）。
    /// 默认关 = 老行为逐字不变（连随机数消耗顺序都保持，见下面 pts 那段）。</summary>


    private void GenerateStrokesSpread(int strokeCount, int screens)
    {
        var rnd = new Random(20260913);
        Doc.Clear();
        float spanH = _virtualH * screens;
        for (int i = 0; i < strokeCount; i++)
        {
            var s = new Stroke
            {
                Tool = Tool.Pen,
                Color = PenColor,
                Kind = StrokeKind.Freehand,
                Width = 2.5f + (float)rnd.NextDouble() * 4f,
                HasPressure = _benchPressure,   // 默认 false = 老行为；开了才走变宽那条渲染路
            };
            float x = _virtualX + (float)rnd.NextDouble() * _virtualW;
            float y = _virtualY + (float)rnd.NextDouble() * spanH;
            // ⚠ `rnd.Next(24)` 无论开不开都要调 —— 不然随机数序列会错位，
            // 默认那一路的基线就不一样了（老基准的数字就没法跟以前比）。
            int ptsRand = 8 + rnd.Next(24);
            int pts = _benchPts > 0 ? _benchPts : ptsRand;
            float ang = (float)(rnd.NextDouble() * Math.PI * 2);
            for (int j = 0; j < pts; j++)
            {
                ang += (float)((rnd.NextDouble() - 0.5) * 0.7);
                float d = 5f + (float)rnd.NextDouble() * 9f;
                x = Math.Clamp(x + MathF.Cos(ang) * d, _virtualX + 1, _virtualX + _virtualW - 1);
                y = Math.Clamp(y + MathF.Sin(ang) * d, _virtualY + 1, _virtualY + spanH - 1);
                // 压感模式下给一条**像真写字**的压力起伏（起笔轻、中段重、收笔轻），
                // 而不是纯随机 —— 纯随机会让每一笔的宽度乱跳，和真实几何差得远。
                float pv;
                if (_benchPressure)
                {
                    float u = pts <= 1 ? 0.5f : j / (float)(pts - 1);
                    pv = Math.Clamp(0.12f + 0.85f * MathF.Sin(u * MathF.PI)
                                    + (float)rnd.NextDouble() * 0.06f, 0.02f, 1f);
                }
                else pv = (float)rnd.NextDouble();
                s.AddPoint(x, y, pv, NowMs);
            }
            Doc.AppendStroke(s);
        }
        Doc.ClearHistory();
    }


    /// <summary>
    /// 自检用：造一个坐标系（不经过工具，直接摆点）。
    /// 三个定义点 = 外框两角 + 原点，和 <see cref="Stroke.SetAxisBox"/> 的规则一致。
    ///
    /// 参数就是 <c>SetAxisBox</c> 的那四个：**(x, y) = 原点**、**(x+w, y+h) = 往右下拖了多远**
    /// （外框 = 原点 ± (w, h)）。所以外框的左上角在 (x−w, y−h)——传参时留神别让它跑出屏幕。
    /// </summary>
    private Stroke MakeCoordinateForTest(float x, float y, float w, float h, bool grid)
    {
        var s = new Stroke
        {
            Tool = Tool.Coordinate,
            Kind = StrokeKind.Coordinate,
            Color = new Color4(1f, 0f, 1f, 1f),        // 品红：便于在屏幕上数像素
            Width = 6f * DpiScale,
            Grid = grid,
        };
        // 先铺够三个点（SetAxisBox 要求控制点已经够数），再按外框算。
        for (int i = 0; i < 3; i++) s.AddPoint(x, y, 1f, NowMs);
        s.SetAxisBox(x, y, x + w, y + h);
        return s;
    }


    /// <summary>造一张"截图"：渐变 + 棋盘格，模拟真实内容（尺寸给的是像素）。</summary>
    private static ImageData MakeTestImage(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(200 - y * 100 / h);                          // B
                px[i + 1] = (byte)(180 - x * 80 / w);                       // G
                px[i + 2] = (byte)(150 + ((x / 16 + y / 16) % 2) * 30);     // R
                px[i + 3] = 255;
            }
        return ImageData.Adopt(w, h, px, hasAlpha: true);
    }


    /// <summary>自检用：造一个数轴（两个定义点 = 左端 / 右端，y 相同）。</summary>
    private Stroke MakeNumberLineForTest(float x, float y, float len)
    {
        var s = new Stroke
        {
            Tool = Tool.NumberLine,
            Kind = StrokeKind.NumberLine,
            Color = new Color4(1f, 0f, 1f, 1f),
            Width = 6f * DpiScale,
        };
        for (int i = 0; i < 2; i++) s.AddPoint(x, y, 1f, NowMs);
        s.SetAxisBox(x, y, x + len, y);
        return s;
    }

    /// <summary>
    /// 出图：坐标系（带网格 / 不带网格各一个）＋ 数轴 ＋ 一条虚线。
    /// 用法 `--axisshow [路径]`，默认 `reports/坐标系与数轴.bmp`。
    /// 自检盯的是"数值对不对"，这一张是给**眼睛**看的：箭头大不大、网格疏密、
    /// 虚线在粗笔上是不是像样——这些量没有一条能写成判据。
    /// </summary>

}
