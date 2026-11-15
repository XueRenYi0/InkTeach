// [职责] 自检模式注册表：Doc 域（ADR-005 D2）。新增模式只改本文件。
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
    private ModeEntry[] BuildModeTableDoc() => new ModeEntry[]
    {
        new("--docstates", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DocStatesProbe(args.Length > 1 ? args[1] : null);
            return null;
        }),
        new("--doctest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            DocumentTest();
            return null;
        }),
        new("--zoomtest", "文档缩放自检（50~200%：中心不动、页矩形不变、页位图倍率、页+墨一起缩、回到 100%）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ZoomTest();
            return null;
        }),
        new("--handocr", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            HandOcrPng(args.Length > 1 ? args[1] : "",
                       args.Length > 2 ? args[2] : "",
                       args.Length > 3 ? args[3] : "reports/handocr.png",
                       args.Length > 4 && float.TryParse(args[4], out float tl2) ? tl2 : 480f,
                       args.Contains("--real"),
                       args.Contains("--allformulas"));
            _quit = true;
            return null;
        }),
        new("--pdfprobe", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            int pn = args.Length > 2 && int.TryParse(args[2], out var ptmp) ? ptmp : 3;
            PdfProbe(args.Length > 1 ? args[1] : null, pn);
            return null;
        }),
        new("--pptprobe", "[秒]     PPT 连接探针（真机跑：打开 PPT 按 F5，看连不连得上）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            double secs = 12;
            if (args.Length > 1 && double.TryParse(args[1], out var ps)) secs = ps;
            PptProbe(secs);
            return null;
        }),
        new("--pptshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PptShow(args.Length > 1 ? args[1] : null);
            return null;
        }),
        new("--ppttest", "PPT 模式自检（假页码源：隔离 / 页内滚动 / 清除全部墨迹=所有页且可撤销 / 退出写盘 / 再进读回）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PptTest();
            return null;
        }),
    };
}
