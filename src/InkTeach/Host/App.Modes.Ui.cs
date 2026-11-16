// [职责] 自检模式注册表：Ui 域（ADR-005 D2）。新增模式只改本文件。
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
    private ModeEntry[] BuildModeTableUi() => new ModeEntry[]
    {
        new("--aiapishow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            AiApiShow(args.Length > 1 ? args[1] : "reports/aiapi.bmp");
            _quit = true;
            return null;
        }),
        new("--aishow", "[图]       AI 面板 + 题卡 + 文本框摆样：面板 / 折叠 / 选中 / 展开 / 文本框 / 改色六张（默认 reports/ai-card.bmp）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AiShow(args.Length > 1 ? args[1] : "reports/ai-card.bmp");
            return null;
        }),
        new("--apiconfig", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ApiConfigTest();
            _quit = true;
            return null;
        }),
        new("--apimenushot", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUiFactory(() => new InkUi.FullUi());
            SettleFrames(200);
            if (CurrentUi is not InkUi.FullUi ui)
            {
                Console.WriteLine("FAIL：UI 不是 FullUi");
                ExitCode = 1;
                _quit = true;
                return ExitCode;
            }
            ui.OpenApiPageForTest();
            SettleFrames(300);
            ui.ApiClickForTest(402);      // 先进入 Key 编辑
            SettleFrames(200);
            ui.ApiClickForTest(400);      // 再开服务商下拉：EDIT 必须收起
            SettleFrames(300);
            string path = args.Length > 1 ? args[1] : "reports/api-menu.bmp";
            bool ok = ScreenProbe.SaveBmp(path, (int)_virtualX, (int)_virtualY,
                                          (int)_virtualW, (int)_virtualH);
            bool inputClosed = !ui.ApiInputEditingForTest;
            bool menuOpen = ui.ApiProviderMenuOpenForTest;
            Console.WriteLine($"inputClosed={inputClosed} menuOpen={menuOpen}");
            Console.WriteLine((ok ? "已写出 " : "写出失败 ") + path);
            ExitCode = ok && inputClosed && menuOpen ? 0 : 1;
            _quit = true;
            return null;
        }),
        new("--axisshow", "[路径]   出图：坐标系（带网格/不带）+ 数轴 + 一条虚线", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            AxisShowcase(args.Length > 1 ? args[1] : "reports/坐标系与数轴.bmp");
            return null;
        }),
        new("--bandactiontest", "选择带上带的三连（全选 / 复制 / 粘贴）几何与提示", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            BandActionTest();
            _quit = true;
            return null;
        }),
        new("--captureicons", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureIconShow(args.Length > 1 ? args[1] : "reports/capture-icons.bmp");
            return null;
        }),
        new("--captureshow", "<图>  截图取景框 + 尺寸读数出图（离屏）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CaptureShow(args.Length > 1 ? args[1] : "reports/capture-frame.png");
            return null;
        }),
        new("--conicshow", "[路径]  出图：双曲线两档（有/无渐近线）+ 椭圆两档（有/无焦点三角形）", args =>
        {
            // 2026-09-22：双曲线两档（有 / 无渐近线）＋ 椭圆两档（有 / 无焦点三角形）
            // 摆成一张四格的图——这一批要看的正是"两档之间的差别"。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ConicShowcase(args.Length > 1 ? args[1] : "reports/圆锥曲线-两档.bmp");
            return null;
        }),
        new("--cursorshow", "<笔|荧光笔|激光笔|橡皮|像素橡皮> [宽]  落点摆样", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string which = args.Length > 1 ? args[1].ToLowerInvariant() : "pen";
            float width = args.Length > 2 && float.TryParse(args[2], out var w) ? w : 0f;
            showcasePenDevice = args.Contains("pen");
            CursorShowcase(which, width);
            return null;
        }),
        new("--curveshow", "[路径]  出图：抛物线四种开口 + 双曲线两个方向 + 正弦/余弦各一周期", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            CurveShowcase(args.Length > 1 ? args[1] : "reports/四种曲线.bmp");
            return null;
        }),
        new("--fontlistshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            FontListShow(args.Length > 1 ? args[1] : "reports/fontlist.bmp");
            _quit = true;
            return null;
        }),
        new("--iconshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            IconShow(args.Length > 1 ? args[1] : "reports/laser-icons.bmp");
            return null;
        }),
        new("--libraryshow", "[图]  收藏弹窗摆样（横排 / 竖排 / 分类菜单各出一张；不依赖合成鼠标）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            string shot = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                ? args[1] : "reports/library-show.png";
            LibraryShow(shot);
            return null;
        }),
        new("--makeicon", "<图.ico>      用界面自己的渲染生成程序图标（线条笔＋白砖＋带笔锋的红笔迹）", args =>
        {
            // 生成程序图标（用界面自己的渲染画 <see cref="AppIconUi"/> 那张：白砖＋大笔），见 MakeIcon。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            MakeIcon(args.Length > 1 ? args[1] : "assets/InkTeach.ico");
            return null;
        }),
        new("--pageshow", "<图>     整屏翻页摆样（相机停在两屏之间 / 正好对齐，各出一张）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PageShow(args.Length > 1 ? args[1] : "reports/page-lines.bmp");
            return null;
        }),
        new("--panelperf", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PanelPerf();
            return null;
        }),
        new("--panelshow", "<图> [--band] [--mini] [--more [--page N] [--clickrow N] [--langpick N] [--recent N]] [--cell N] [--shape 名字] [--zoom N] [--docpan] [--docresume] [--pdfclock] [--update]   界面出图（离屏；--more --page 0=启动器 1=设置；--clickrow 点设置第 N 行，--recent 塞 N 条最近文档，--docpan 文档模式（手掌格），--docresume 文档恢复提示卡，--pdfclock PDF + 时间挂钟，--update 摆出新版本红点）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // 可选的两个参数（出"改之前 / 改之后"的对照图专用，产品里都不会传）：
            //   第 2 个 ＝ 描边型外库图标强制线宽（不传就按 IconAtlas.StrokedIcons 那一份）；
            //   第 3 个 ＝ 激光笔那一格强制成哪个档号（不传就用产品那支）。
            if (args.Length > 2 && float.TryParse(args[2], out var forcedStroke))
                InkUi.IconAtlas.DevForceStroke = forcedStroke;
            if (args.Length > 3 && int.TryParse(args[3], out var laserVariant))
                InkUi.IconAtlas.DevLaserVariant = laserVariant;
            PanelShow(args.Length > 1 ? args[1] : "reports/panel-第一版.png");
            return null;
        }),
        new("--paneltest", "产品界面自检（球 → 按钮带这条最小闭环）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PanelTest();
            return null;
        }),
        new("--penshow", "[路径] [bars]  出图：上游那批「带笔的」图标；带第 2 个参数就每格加一副「三道杠」", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SetUi(new PenSheet());
            BoardOn = true;                      // 白板打底：图里没有桌面上的杂东西
            SettleFrames(400);
            string path = args.Length > 1 ? args[1] : "reports/pen-candidates.png";
            if (!OffscreenShot(path)) Console.WriteLine("出图失败");
            _quit = true;
            return null;
        }),
        new("--pixeleraseshow", "像素橡皮摆样（擦之前/之后各存一张图，自己抓屏）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PixelEraserShowcase();
            return null;
        }),
        new("--prismshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrismShowcase(args.Length > 1 ? args[1] : "reports/棱柱.bmp");
            return null;
        }),
        new("--prismtest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            PrismTest();
            return null;
        }),
        new("--qrtest", "反馈群二维码（读图 / 打开 / 点一下关闭）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            QrTest();
            _quit = true;
            return null;
        }),
        new("--radialshow", "[图]   呼出盘摆样：定格在屏幕中央出图（默认 reports/radial-palette.bmp）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RadialShow(args.Length > 1 ? args[1] : "reports/radial-palette.bmp");
            return null;
        }),
        new("--radialtest", "呼出盘自检（Ctrl+Alt+Shift+Q：开 / 划 / 松 / 取消 / 穿透 / 松键轮询）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RadialTest();
            return null;
        }),
        new("--replayshow", "<图>   墨迹回放摆样（铺几笔 → 播到一半 → 截控制条那一块）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ReplayShow(args.Length > 1 ? args[1] : "reports/replay-bar.bmp");
            return null;
        }),
        new("--rotateshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            float deg = args.Length > 1 && float.TryParse(args[1], out var d) ? d : 90f;
            RotateShowcase(deg);
            return null;
        }),
        new("--rotatetest", "旋转度数读数与吸附（含真机拖动 + 上屏核对）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            RotateTest();
            return null;
        }),
        new("--scrollflash", "", args =>
        {
            // 诊断：**"滚轮滚动之后一按鼠标就闪/错位"**（用户 2026-10-04 报，
            // 关键线索：滚动之后、按下才闪，松手就不闪；以前竖写时也遇到过）。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollFlashTest();
            return null;
        }),
        new("--scrollshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollShowcase();
            return null;
        }),
        new("--scrollwrite", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ScrollWriteTest();
            return null;
        }),
        new("--selaishow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelAiShow(args.Length > 1 ? args[1] : "reports/selai.bmp");
            _quit = true;
            return null;
        }),
        new("--selbeautifyshow", "[图]  出图：图片选中条（美化 / 保存进⋯）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelBeautifyShow(args.Length > 1 ? args[1] : "reports/sel-beautify.bmp");
            return null;
        }),
        new("--selmenutest", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelMenuTest();
            _quit = true;
            return null;
        }),
        new("--selshowcase", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SelShowcase(args.Length > 1 ? args[1] : null);
            return null;
        }),
        new("--shapeiconshow", "[路径] 出图：图形面板图标的对照表（含每一档的变体）", args =>
        {
            // 图形面板那些自绘图标的**对照表**（含每一档的变体），见 ShapeIconSheet。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapeIconShow(args.Length > 1 ? args[1] : "reports/shape-icons.bmp");
            return null;
        }),
        new("--shapepaneltest", "图形面板专项（入口 / 页签 / 选图形 / 蓝色互斥）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ShapePanelTest();
            _quit = true;
            return null;
        }),
        new("--shapetoolshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            // `--circle / --ellipse / --triangle / --parallelogram / --rectangle / --parabola /
            //  --cuboid` 选图形（不给就是直线）；`--snap` = 那一张要拍"吸附生效中"的帧；
            // `--angles` = 内角 / 夹角那一组读数；`--pose` = 拖旋转柄时的**姿态角**读数。
            // 后两个（抛物线 / 长方体）是 2026-09-20 加的：它们代表"**没有特殊手柄**"的那一类，
            // 要拍的是"通用八手柄 ＋ 旋转柄那根线挂哪儿"（用户："抛物线的旋转手柄线连的地方很奇怪"）。
            string kind = args.Contains("--circle") ? "circle"
                        : args.Contains("--ellipse") ? "ellipse"
                        : args.Contains("--triangle") ? "triangle"
                        : args.Contains("--parallelogram") ? "parallelogram"
                        : args.Contains("--parabola") ? "parabola"
                        : args.Contains("--cuboid") ? "cuboid"
                        : args.Contains("--spacecoordinate") ? "spacecoordinate"
                        : args.Contains("--rectangle") ? "rectangle" : "line";
            ShapeToolShowcase(args.Length > 1 ? args[1] : "reports/shape-line-handles.bmp",
                              kind, args.Contains("--drag"), args.Contains("--draw"),
                              args.Contains("--rotate"), args.Contains("--rotated"),
                              args.Contains("--snap"), args.Contains("--angles"),
                              args.Contains("--pose"), args.Contains("--autosel"));
            return null;
        }),
        new("--smoothshow", "[图]   出图：曲线化开/关对照（同一组样本各存一张 -off / -on，32 位 BMP）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            SmoothShowcase(args.Length > 1 ? args[1] : "reports/曲线化-对照.bmp");
            return null;
        }),
        new("--toastshow", "", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ToastShow(args.Length > 1 ? args[1] : "reports/toast.bmp");
            _quit = true;
            return null;
        }),
        new("--toolicons", "[路径]  出图：白板 / 激光笔的图标候选（未选中 / 选中 / 放大三格）", args =>
        {
            // 2026-09-26：白板 / 激光笔的图标候选（用户："白板：感觉不像"、
            // "激光笔：我喜欢那种'笔射出一道光'的形态"），见 ToolIconSheet。
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            ToolIconShow(args.Length > 1 ? args[1] : "reports/tool-icons.bmp");
            return null;
        }),
        new("--uitest", "界面输入通路自检（合成点击，看谁收到）", args =>
        {
            _autoExitAt = double.MaxValue;
            _nextLogAt = double.MaxValue;
            UiInputTest();
            return null;
        }),
    };
}
