// [职责] 自检模式注册表：Shape 域（ADR-005 D2）。新增模式只改本文件。
// [测试] --list-modes / --unittest

using System.Buffers;
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
    private ModeEntry[] BuildModeTableShape() => new ModeEntry[]
    {
        new("--aiplot", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AiPlotTest();
            _quit = true;
            return null;
        }),
        new("--arrowcleartest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ArrowClearTest();
            _quit = true;
            return null;
        }),
        new("--dynerasertest", "动态橡皮（曲线：死区到 0.8 / 慢=基准1.0不缩 / 快封顶 2.5；窗口抗抖；慢扫 vs 快扫；框跟速度；严丝合缝）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DynEraserTest();
            return null;
        }),
        new("--eraseimage", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseImageProbe();
            return null;
        }),
        new("--eraserlab", "[前缀]  橡皮手测台：铺样例 + 记录每条拖拽，给人用鼠标测（不自动退出）", args =>
        {
            // **橡皮手测台**：这是给人用的交互会话，不是自检——铺好样例、打开遥测，
            // 然后把控制权交回引擎的正常消息循环（return -1 就是"没有测试模式"）。
            EraserLab(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null,
                      args.Contains("--syscursor"));
            return -1;
        }),
        new("--erasertest", "橡皮擦正确性", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraserTest();
            return null;
        }),
        new("--eraseselfcross", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            EraseSelfCrossProbe();
            return null;
        }),
        new("--figure", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FigureTest();
            _quit = true;
            return null;
        }),
        new("--figurelive", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FigureLiveTest();
            _quit = true;
            return null;
        }),
        new("--figurequad", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FigureQuadTest();
            _quit = true;
            return null;
        }),
        new("--glypherase", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            GlyphErasePng(args.Length > 1 ? args[1] : "reports/glypherase-before.png",
                          args.Length > 2 ? args[2] : "reports/glypherase-after.png");
            _quit = true;
            return null;
        }),
        new("--imageerase", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageEraseProbe();
            return null;
        }),
        new("--motiontest", "运动模型自检（baseline / catmull / mean2 同批语料出表）", args =>
        {
            // 对照台总自检：M0/M1/M2/M3/M4/M5 同一批语料出表。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = MotionProbe.Run();
            _quit = true;
            return null;
        }),
        new("--pixelerasetest", "像素橡皮正确性（切成两段 / 框里无墨 / 一步撤销）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PixelEraserTest();
            return null;
        }),
        new("--plotfromcard", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PlotFromCardTest();
            _quit = true;
            return null;
        }),
        new("--plotlive", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PlotLiveTest(args.Length > 1 ? args[1] : null);
            _quit = true;
            return null;
        }),
        new("--plottest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PlotSuiteTest(args.Length > 1 ? args[1] : null);
            _quit = true;
            return null;
        }),
        new("--selbench", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelBenchTest();
            return null;
        }),
        new("--seldrag", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelDragShowcase();
            return null;
        }),
        new("--selfcross", "自交叠色自检（一笔自己穿过自己，颜色不能变深）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelfCrossTest();
            return null;
        }),
        new("--selftest", "[秒]     自动画几笔并检查渲染", args =>
        {
            double seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 12;
            // 基准的两个开关（2026-10-07）：`--benchpressure` 让笔画带压感，
            // `--benchpts N` 指定每笔点数——老基准每笔只 19.5 点，比真实写法轻近 9 倍，
            // 不问这两个问题，"一万笔吃多少"答不准。
            _benchPressure = args.Contains("--benchpressure");
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--benchpts" && int.TryParse(args[i + 1], out int bp))
                    _benchPts = Math.Clamp(bp, 2, 4000);
                if (args[i] == "--benchspread" && int.TryParse(args[i + 1], out int bs))
                    _benchSpread = Math.Clamp(bs, 1, 60);
            }
            _autoExitAt = NowMs + seconds * 1000;
            _nextLogAt = NowMs;
            SelfTest(seconds);
            return null;
        }),
        new("--seltest", "框选自检（框到的就该选中）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelTest();
            return null;
        }),
        new("--shapeaudittest", "图形面板 41 张卡逐张仿真（种类 / 档位 / 紧框 / 手柄 / 小样）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeAuditTest();
            _quit = true;
            return null;
        }),
        new("--shapetest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeTest();
            return null;
        }),
        new("--shapetooltest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeToolTest();
            return null;
        }),
        new("--snaptest", "对象吸附自检（3×3 线组合 / 软相交 / 抛物线顶点 / 正弦拼缝）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SnapTest();
            return null;
        }),
        new("--touchguardtest", "触摸自检（合成触摸：PT_TOUCH 通路 + 第二根手指不许抢笔）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TouchGuardTest();
            return null;
        }),
        new("--touchtest", "触摸手势自检（单指写 / 双指漫游翻页 / 文档捏合缩放 / 放大后平移 / H·空格手掌 / 三指擦 / 长按选 / 选中变换 / 总开关）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TouchTest();
            return null;
        }),
    };
}
