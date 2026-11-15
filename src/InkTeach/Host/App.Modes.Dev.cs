// [职责] 自检模式注册表：Dev 域（ADR-005 D2）。新增模式只改本文件。
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
    private ModeEntry[] BuildModeTableDev() => new ModeEntry[]
    {
        new("--ballprobe", "[前缀]  收起球贴边诊断（左/右/四角 × 显示/隐藏，逐个出图）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BallProbe(args.Length > 1 ? args[1] : "reports/ball");
            return null;
        }),
        new("--coordtest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int n = args.Length > 1 && int.TryParse(args[1], out var c) ? c : 12;
            CoordTest(n);
            return null;
        }),
        new("--cursortest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CursorTest();
            return null;
        }),
        new("--dialogprobe", "<前缀> [--save]  导出对话框探针（真弹框 + 点它的下拉 + 连拍三张；", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DialogProbe(args.Length > 1 ? args[1] : "tmp/dlg");
            return null;
        }),
        new("--facesprobe", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FacesProbe();
            _quit = true;
            return null;
        }),
        new("--gclatencytest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            GcLatencyTest();
            return null;
        }),
        new("--imgcardlive", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageCardLiveTest(args.Length > 1 ? args[1] : null);
            _quit = true;
            return null;
        }),
        new("--imgcardreplay", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageCardReplayTest(args.Length > 1 ? args[1] : null,
                                args.Length > 2 ? args[2] : null);
            _quit = true;
            return null;
        }),
        new("--imgcardtest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ImageCardTest();
            _quit = true;
            return null;
        }),
        new("--inputtest", "指针输入路径自检", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            InputPathTest();
            return null;
        }),
        new("--latbench", "<csv>    延时实测（分场景 + 分位数 + 稳定性）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            LatencyBench(args.Length > 1 ? args[1] : "reports/latency.csv");
            return null;
        }),
        new("--memory", "[文件]     输出内存归因报告", args =>
        {
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            MemoryReport(args.Length > 1 ? args[1] : "reports/inkprobe-memory.txt");
            return null;
        }),
        new("--passtest", "穿透真机测试（跨进程点击）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PassThroughTest();
            return null;
        }),
        new("--pdmetrics", "<语料> [--dt 15] [--limit N] [--sweep]  预测器离线评测（SCUT/UCI：供给/吃到/角度误差/超前）", args =>
        {
            // 大数据管线第二环：真实语料（SCUT 中文 / UCI）离线评测预测器——
            // 供给率 / 吃到率 / 角度误差 / 超前（见 PdMetricsProbe 的口径注释）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = PdMetricsProbe.Run(args);
            _quit = true;
            return null;
        }),
        new("--pressurediag", "压感采集诊断（合成笔注入：合并点、压感有效位、压力分布）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PressureDiagTest();
            return null;
        }),
        new("--pressuretest", "压感自检（映射函数 / 上屏粗细随压力变 / 无压感与虚线回退 / 开关往返 / 存档往返）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PressureTest();
            return null;
        }),
        new("--replayinput", "<文件> [--frame 10]  黑匣子回放：供给率/帧间墨尖位移/方向翻转（诊断）", args =>
        {
            // 黑匣子回放：把 `--reclive` 记录的真实输入 1:1 喂回引擎，算出预测尾的指标。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ExitCode = InputReplayProbe.Run(this, args);
            _quit = true;
            return null;
        }),
        new("--replaytest", "墨迹回放自检（时间轴 / 前缀 / 暂停 / 倍速 / 只读 / 控制条 / 翻页退出）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ReplayTest();
            return null;
        }),
        new("--report", "[文件]     输出完整性能报告", args =>
        {
            _autoExitAt = NowMs;
            _nextLogAt = double.MaxValue;
            Report(args.Length > 1 ? args[1] : "reports/inkprobe-report.txt");
            return null;
        }),
        new("--transformtest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            TransformTest();
            return null;
        }),
        new("--wheeltest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            WheelTest();
            return null;
        }),
        new("--writetest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double mins = args.Length > 1 && double.TryParse(args[1], out var wm) ? wm : 20;
            double sps = args.Length > 2 && double.TryParse(args[2], out var ws) ? ws : 6;
            DenseWrite = args.Contains("dense");
            WriteTest(mins, sps);
            return null;
        }),
    };
}
