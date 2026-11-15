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

/// <summary>
/// 开发期宿主：持有引擎，外加一整套自动化测试/基准工具。
/// 继承只是为了让这些工具直接读引擎内部状态（产品代码请用组合：new InkEngine()）。
/// </summary>

internal sealed partial class App
{
    private void SelShowcase(string path = null)
    {
        BoardOn = true;                 // 白底，不然手柄压在桌面上看不清
        Doc.Clear();
        Doc.ClearHistory();

        // **必须挂上界面**：操作条/小面板的颜色与投影是界面推给引擎的
        // （见 IUiHost.SetFloatingTheme）——不挂界面拿到的是 UiTheme.Default 那套兜底，
        // 出的图就不是产品里看到的样子。
        // 注意顺序：**偏好要先设**，SetUiFactory 是立刻挂载的（挂载时就会读偏好）。
        // --dark：深色那档也出一张（浮层的深色是这一轮新加的）
        if (Environment.GetCommandLineArgs().Contains("--dark")) SetUiPref("dark", "1");
        SetUiFactory(() => new InkUi.FullUi());

        float cx = VirtualScreen.MinX + 720;
        float top = VirtualScreen.MinY + 300;

        // 一条自由笔迹
        var free = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0.11f, 0.12f, 0.15f, 1f), Width = 6f,
        };
        for (int i = 0; i <= 80; i++)
        {
            float t = i / 80f;
            free.AddPoint(cx - 320 + t * 640, top + 80 + MathF.Sin(t * 7f) * 70f, 0.5f, i * 8);
        }
        Doc.AddStroke(free);

        // 一个带旋转 + 非等比缩放的矩形：用来核对"变换参与选中框"这件事
        var rect = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.95f, 0.18f, 0.18f, 1f), Width = 6f,
        };
        rect.AddPoint(cx - 260, top + 350, 1f, 0);
        rect.AddPoint(cx + 260, top + 520, 1f, 0);
        var center = new Vector2(cx, top + 435);
        rect.Transform = Matrix3x2.CreateRotation(0.14f, center)
                      * Matrix3x2.CreateScale(1.3f, 0.85f, center);
        Doc.AddStroke(rect);

        // 一条**很宽**的荧光笔：用来看选中框有没有把墨圈住。
        // 框按中心线算的话，这条 64 像素宽的带子上会压着四条边——一眼就能看出来。
        var wide = new Stroke
        {
            Tool = Tool.Highlighter, Kind = StrokeKind.Freehand,
            Color = HighlighterCurrent, Width = 32f * DpiScale,
        };
        wide.AddPoint(cx - 420, top + 690, 0.5f, 0);
        wide.AddPoint(cx + 420, top + 690, 0.5f, 1);
        Doc.AddStroke(wide);

        // 2026-09-30（8.2.0）：出图也把**面板**带上——
        //   --ink    开墨迹面板（颜色/粗细/线型，含新的连续滑条）
        //   --layer  开层级面板
        //   --custom 再叠上自定义取色板（色相条 + 饱和度/明度方块）
        // 排版好不好看只能靠图看，所以这些"一张图里要有面板"的对照必须有出口。
        //
        // ⚠ 工具必须是**框选**：操作条/面板由 `SelectionBarShown` 把守
        //（只有框选 / 图形工具才有点得到的那一块）。忘了这一句时出图里只有框和手柄，
        // 看图的还以为"面板没开"——出图这条路第一版就踩了这个。
        var cmdArgs = Environment.GetCommandLineArgs();
        Tool = Tool.Marquee;
        SelBarCollapsed = false;
        bool wantInk = cmdArgs.Contains("--ink"), wantLayer = cmdArgs.Contains("--layer");
        bool wantCustom = cmdArgs.Contains("--custom");
        SelPanelOpen = (wantInk || wantCustom) ? SelPanel.Ink : wantLayer ? SelPanel.Layer : SelPanel.None;

        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Doc.InvalidateAll();
        // 色板放在**选中摆好之后**开：起点色取的是"选中墨迹"，先开就退成手里那支笔的默认色
        // （第一版出图就踩了这个：选中那条黑墨，色板却停在红色上）。
        if (wantCustom) OpenCustomColorForTest();

        // `--dragmid`：把粗细滑条**按在中间不松手**——给"拖动中显示数值"出对照图用。
        // 真机上它靠指针消息驱动，出图这条路没有指针，只能借自检那两个钩子把它摆出来。
        if (cmdArgs.Contains("--dragmid"))
        {
            var sl = SelectionHandles.SliderRect(SelectionHandles.FrameOf(Doc.Selected).CanvasAabb,
                                                  FurnitureDpi, FurnitureViewport, SelectionHandles.SwatchCount);
            float mx = SelectionHandles.SliderXOfT(0.62f, SelectionHandles.FrameOf(Doc.Selected).CanvasAabb,
                                                   FurnitureDpi, FurnitureViewport, SelectionHandles.SwatchCount);
            SelectionGestureForTest(mx, (sl.MinY + sl.MaxY) * 0.5f);
            PanelDragMoveForTest(mx + 2f, (sl.MinY + sl.MaxY) * 0.5f);
        }
        SettleFrames(800);

        // 量尺：把三处浮层的实际尺寸/间距打出来（不凭感觉调，前后两版都跑同一条）。
        PrintOverlayMetrics();

        if (path == null)
        {
            Console.WriteLine("选中框已摆好，等外部截图（这个模式不会自己退出）");
            return;
        }

        // 出图（离屏，锁屏/远程也能出）：范围 = 选中框 ∪ 操作条 ∪（开着的）面板，再留一圈给投影。
        // 操作条挂在框下方（SelectionHandles.BarRect 算的就是那个位置），
        // 所以把它一起并进来，否则出图会把操作条切掉一半。
        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        var bar = SelectionHandles.BarRect(aabb, FurnitureDpi, FurnitureViewport);
        var region = new RectF
        {
            MinX = MathF.Min(aabb.MinX, bar.MinX), MinY = MathF.Min(aabb.MinY, bar.MinY),
            MaxX = MathF.Max(aabb.MaxX, bar.MaxX), MaxY = MathF.Max(aabb.MaxY, bar.MaxY),
        };
        if (SelPanelOpen == SelPanel.Ink)
        {
            region.Add(SelectionHandles
                .PanelRect(aabb, FurnitureDpi, FurnitureViewport, SelectionHandles.SwatchCount));
            if (CustomColorOpen)
                region.Add(SelectionHandles.CustomPanelRect(aabb, FurnitureDpi, FurnitureViewport,
                                                           SelectionHandles.SwatchCount));
        }
        else if (SelPanelOpen == SelPanel.Layer)
        {
            region.Add(SelectionHandles.LayerPanelRect(aabb, FurnitureDpi, FurnitureViewport));
        }
        if (!OffscreenFloatingShot(path, region.Inflate(30f))) Console.WriteLine("出图失败");
        _quit = true;
    }

    /// <summary>
    /// **浮层量尺**（8.2.0）：把三处浮层（操作条 / 墨迹面板 / 层级面板）的实际尺寸和
    /// 内边距 / 格间距 / 分组缝按**几何读出来**打成一张表。
    ///
    /// 为什么要有它（用户 2026-09-30："先用 --uitest 把三处浮层的实际尺寸/间距量出来，
    /// 不凭感觉调"）：收紧要收在哪儿、收了多少，得先有前后两版的同一把尺子。
    /// 它走的是**绘制/命中同一份几何函数**，所以量出来的就是屏幕上真正画出来的那个数，
    /// 不是另抄一份"设计稿数字"。
    ///
    /// 单位一律逻辑像素（dpi 传 1、visible 传空 = 不夹取，量的是纯公式）。
    /// </summary>
    private void PrintOverlayMetrics()
    {
        const float dpi = 1f;
        var sel = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        var none = RectF.Empty;
        int sc = SelectionHandles.SwatchCount;

        var bar = SelectionHandles.BarRect(sel, dpi, none);
        var b0 = SelectionHandles.BarButtonRect(0, sel, dpi, none);
        var b1 = SelectionHandles.BarButtonRect(1, sel, dpi, none);
        float barPad = b0.MinX - bar.MinX;
        float barGap = b1.MinX - b0.MaxX;
        float barH = bar.MaxY - bar.MinY;
        float barRadius = MathF.Min(FloatingTheme.CornerRadius, barH * 0.5f);

        var p = SelectionHandles.PanelRect(sel, dpi, none, sc);
        var s0 = SelectionHandles.SwatchRect(0, sel, dpi, none, sc);
        var s1 = SelectionHandles.SwatchRect(1, sel, dpi, none, sc);
        var sRow = SelectionHandles.SwatchRect(SelectionHandles.SwatchColumns, sel, dpi, none, sc);
        var c0 = SelectionHandles.StyleCellRect(0, sel, dpi, none, sc);
        var c1 = SelectionHandles.StyleCellRect(1, sel, dpi, none, sc);

        var lp = SelectionHandles.LayerPanelRect(sel, dpi, none);
        var l0 = SelectionHandles.LayerCellRect(0, sel, dpi, none);
        var l1 = SelectionHandles.LayerCellRect(1, sel, dpi, none);

        Console.WriteLine();
        Console.WriteLine("=== 浮层量尺（逻辑像素；dpi=1、不夹取）===");
        Console.WriteLine($"  操作条     {bar.MaxX - bar.MinX:F0} × {barH:F0}   两端内边距 {barPad:F1}   "
                          + $"按钮 {b0.MaxX - b0.MinX:F0}（方格子）格缝 {barGap:F1}  "
                          + $"悬停底 {SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}×"
                          + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}  "
                          + $"圆角 {barRadius:F0}");
        Console.WriteLine($"  墨迹面板   {p.MaxX - p.MinX:F0} × {p.MaxY - p.MinY:F0}   内边距 {s0.MinX - p.MinX:F1}   "
                          + $"色片 {s0.MaxX - s0.MinX:F0} 缝 {s1.MinX - s0.MaxX:F1} 行缝 {sRow.MinY - s0.MaxY:F1}");
        Console.WriteLine($"            线型格 {c0.MaxX - c0.MinX:F1}×{c0.MaxY - c0.MinY:F0}、格缝 {c1.MinX - c0.MaxX:F1}，"
                          + $"滑条行 {SelectionHandles.SliderRowLogical:F0} / 线型行 {SelectionHandles.StyleRowLogical:F0}，"
                          + $"面板离条 {SelectionHandles.PanelGapLogical:F0}");
        Console.WriteLine($"  层级面板   {lp.MaxX - lp.MinX:F0} × {lp.MaxY - lp.MinY:F0}   内边距 {l0.MinX - lp.MinX:F1}   "
                          + $"格 {l0.MaxX - l0.MinX:F0} 缝 {l1.MinX - l0.MaxX:F1}");
        var pk = SelectionHandles.CustomPanelRect(sel, dpi, none, sc);
        var psv = SelectionHandles.PickSvRect(sel, dpi, none, sc);
        var phue = SelectionHandles.PickHueRect(sel, dpi, none, sc);
        Console.WriteLine($"  取色板     {pk.MaxX - pk.MinX:F0} × {pk.MaxY - pk.MinY:F0}   内边距 {psv.MinX - pk.MinX:F1}   "
                          + $"SV {psv.MaxX - psv.MinX:F0} 色相条 {phue.MaxX - phue.MinX:F0}  格缝 {phue.MinX - psv.MaxX:F1}");
        Console.WriteLine();
    }

    /// <summary>
    /// **浮层尺寸 token 自检**（8.2.0 验收①）：三处浮层（操作条 / 墨迹面板 / 层级面板 / 取色板）
    /// 的内边距、格间距、格与缝必须**从同一套 token 读出来**。
    ///
    /// 判据是"几何读出来的数 == token 算出来的数"（容差 0.01）——哪天有人绕过 token
    /// 顺手写一个 10f，这里当场变红。`--selftest` 和 `--uitest` 共用同一份（计划里写的是
    /// 两条自检，两边跑到的就是同一个它）。
    ///
    /// <paramref name="check"/> 是调用方自己的红绿回调（两边的输出格式不一样）。
    /// </summary>
    private void CheckFloatOverlayTokens(Action<string, bool, string> check)
    {
        float dpi = UiUnit;
        const float tol = 0.01f;
        var sel = new RectF { MinX = 400, MinY = 300, MaxX = 800, MaxY = 600 };
        int sc = SelectionHandles.SwatchCount;

        var bar = SelectionHandles.BarRect(sel, dpi, RectF.Empty);
        var btn0 = SelectionHandles.BarButtonRect(0, sel, dpi, RectF.Empty);
        var btn1 = SelectionHandles.BarButtonRect(1, sel, dpi, RectF.Empty);
        float barPad = (btn0.MinX - bar.MinX) / dpi;
        float barGap = (btn1.MinX - btn0.MaxX) / dpi;
        float barW = (bar.MaxX - bar.MinX) / dpi;
        float barWTok = SelectionHandles.FloatPadLogical * 2
                      + SelectionHandles.BarButtonCount * SelectionHandles.BarButtonWidthLogical
                      + (SelectionHandles.BarButtonCount - 1) * SelectionHandles.BarGapLogical;
        bool barOk = Near(barPad, SelectionHandles.FloatPadLogical, tol)
                  && Near(barGap, SelectionHandles.BarGapLogical, tol)
                  && Near(barW, barWTok, tol)
                  && Near((bar.MaxY - bar.MinY) / dpi, SelectionHandles.BarHeightLogical, tol);

        var p = SelectionHandles.PanelRect(sel, dpi, RectF.Empty, sc);
        var s0 = SelectionHandles.SwatchRect(0, sel, dpi, RectF.Empty, sc);
        var s1 = SelectionHandles.SwatchRect(1, sel, dpi, RectF.Empty, sc);
        var sRow = SelectionHandles.SwatchRect(SelectionHandles.SwatchColumns, sel, dpi, RectF.Empty, sc);
        var c0 = SelectionHandles.StyleCellRect(0, sel, dpi, RectF.Empty, sc);
        var c1 = SelectionHandles.StyleCellRect(1, sel, dpi, RectF.Empty, sc);
        int rows = (sc + SelectionHandles.SwatchColumns - 1) / SelectionHandles.SwatchColumns;
        float panelWTok = SelectionHandles.FloatPadLogical * 2
                        + SelectionHandles.SwatchColumns * SelectionHandles.SwatchSizeLogical
                        + (SelectionHandles.SwatchColumns - 1) * SelectionHandles.FloatGapLogical;
        float panelHTok = SelectionHandles.FloatPadLogical * 2
                        + SelectionHandles.SliderRowLogical + SelectionHandles.StyleRowLogical
                        + rows * SelectionHandles.SwatchSizeLogical + (rows - 1) * SelectionHandles.FloatGapLogical;
        bool panelOk = Near((s0.MinX - p.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                    && Near((s1.MinX - s0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((sRow.MinY - s0.MaxY) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((s0.MaxX - s0.MinX) / dpi, SelectionHandles.SwatchSizeLogical, tol)
                    && Near((c1.MinX - c0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((c0.MaxY - c0.MinY) / dpi, SelectionHandles.StyleRowLogical, tol)
                    && Near((p.MaxX - p.MinX) / dpi, panelWTok, tol)
                    && Near((p.MaxY - p.MinY) / dpi, panelHTok, tol);

        // 8.2.1：悬停 / 激活的底必须是**正方形**（用户点名的那条："悬停是长方形不是正方形，
        // 所以分散"）——逐格核对"宽 = 高 = 格宽 − 2×inset"，并检查它居中。
        bool chipOk = true; string chipNote = "";
        for (int i = 0; i < SelectionHandles.BarButtonCount; i++)
        {
            var chip = SelectionHandles.BarHoverChip(i, sel, dpi, RectF.Empty);
            var btnI = SelectionHandles.BarButtonRect(i, sel, dpi, RectF.Empty);
            float cw = (chip.MaxX - chip.MinX) / dpi, chh = (chip.MaxY - chip.MinY) / dpi;
            float want = SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2;
            if (MathF.Abs(cw - chh) > tol || MathF.Abs(cw - want) > tol
                || MathF.Abs((chip.MinX + chip.MaxX) * 0.5f - (btnI.MinX + btnI.MaxX) * 0.5f) > tol * dpi)
            {
                chipOk = false;
                chipNote = $"第 {i} 格底 {cw:F1}×{chh:F1}（该 {want:F0}×{want:F0} 且居中）";
                break;
            }
        }

        var lp = SelectionHandles.LayerPanelRect(sel, dpi, RectF.Empty);
        var l0 = SelectionHandles.LayerCellRect(0, sel, dpi, RectF.Empty);
        var l1 = SelectionHandles.LayerCellRect(1, sel, dpi, RectF.Empty);
        bool layerOk = Near((l0.MinX - lp.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                    && Near((l1.MinX - l0.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                    && Near((l0.MaxX - l0.MinX) / dpi, SelectionHandles.LayerCellLogical, tol);

        var pk = SelectionHandles.CustomPanelRect(sel, dpi, RectF.Empty, sc);
        var psv = SelectionHandles.PickSvRect(sel, dpi, RectF.Empty, sc);
        var phue = SelectionHandles.PickHueRect(sel, dpi, RectF.Empty, sc);
        bool pickOk = Near((psv.MinX - pk.MinX) / dpi, SelectionHandles.FloatPadLogical, tol)
                   && Near((phue.MinX - psv.MaxX) / dpi, SelectionHandles.FloatGapLogical, tol)
                   && Near((psv.MaxX - psv.MinX) / dpi, SelectionHandles.PickSvLogical, tol)
                   && Near((phue.MaxX - phue.MinX) / dpi, SelectionHandles.PickHueLogical, tol);

        // 圆角也是 token：三处浮层都读界面推上来的主题（= InkUi.Tokens.FloatingCorner）。
        bool cornerOk = Near(FloatingTheme.CornerRadius, InkUi.Tokens.FloatingCorner, tol);

        check("浮层尺寸：内边距/格间距/格尺寸/圆角都在 token 上（操作条）", barOk,
              $"内边距 {barPad:F1}、格缝 {barGap:F1}、整条 {barW:F0}×{(bar.MaxY - bar.MinY) / dpi:F0}");
        check("浮层尺寸：操作条悬停/激活底是正方形（8.2.1，用户点名）", chipOk,
              chipOk
                  ? $"{SelectionHandles.BarButtonCount} 格逐格核对："
                    + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}×"
                    + $"{SelectionHandles.BarButtonWidthLogical - SelectionHandles.BarHoverInsetLogical * 2:F0}、居中"
                  : chipNote);
        check("浮层尺寸：墨迹面板在 token 上（含行缝与整卡宽高）", panelOk,
              $"内边距 {(s0.MinX - p.MinX) / dpi:F1}、色片缝 {(s1.MinX - s0.MaxX) / dpi:F1}、"
              + $"行缝 {(sRow.MinY - s0.MaxY) / dpi:F1}、卡片 {(p.MaxX - p.MinX) / dpi:F0}×{(p.MaxY - p.MinY) / dpi:F0}");
        check("浮层尺寸：层级面板在 token 上", layerOk,
              $"内边距 {(l0.MinX - lp.MinX) / dpi:F1}、格缝 {(l1.MinX - l0.MaxX) / dpi:F1}");
        check("浮层尺寸：自定义取色板在 token 上", pickOk,
              $"内边距 {(psv.MinX - pk.MinX) / dpi:F1}、SV {(psv.MaxX - psv.MinX) / dpi:F0}、"
              + $"色相条 {(phue.MaxX - phue.MinX) / dpi:F0}");
        check("浮层圆角：走界面主题令牌（三处一个数）", cornerOk,
              $"主题 {FloatingTheme.CornerRadius:F0} vs InkUi.Tokens.FloatingCorner {InkUi.Tokens.FloatingCorner:F0}");
    }

    private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;
    private (bool ok, string detail) CheckHotkeyDoc(KeyMap map)
    {
        const string docPath = "快捷键总表.md";
        if (!File.Exists(docPath))
            return (true, $"{docPath} 不在当前目录，这一项跳过");

        var lines = File.ReadAllText(docPath).Split('\n');
        var missing = new List<string>();
        foreach (var b in map.Bindings)
        {
            string chord = b.Chord.ToString();
            string name = KeyMap.Describe(b.Action);
            bool found = lines.Any(l => l.Contains(chord) && l.Contains(name));
            if (!found) missing.Add($"{b.Scope} {chord} {name}");
        }

        return (missing.Count == 0,
                missing.Count == 0
                    ? $"共 {map.Bindings.Count} 条，全部查得到"
                    : "缺：" + string.Join("；", missing.Take(6))
                      + (missing.Count > 6 ? $" 等 {missing.Count} 条" : ""));
    }

    /// <summary>
    /// 落点反馈摆样：把指针停在屏幕中央、按指定工具把落点反馈挂出来，等外部截图。
    /// 目的和 --rotateshow 一样——光标这类东西只有看在图上才算验过。
    /// </summary>
    private void CursorShowcase(string tool, float widthLogical)
    {
        Doc.Clear();
        Doc.ClearHistory();
        Tool = tool switch
        {
            "highlighter" or "hl" => Tool.Highlighter,
            "laser" => Tool.Laser,
            "eraser" => Tool.Eraser,
            "pixeleraser" or "blockeraser" => Tool.PixelEraser,
            "capture" or "screenshot" => Tool.Capture,      // 截图：看自绘的取景框落点
            _ => Tool.Pen,
        };
        if (widthLogical > 0f)
        {
            if (Tool == Tool.Highlighter) HighlighterWidthLogical = widthLogical;
            else if (Tool == Tool.Laser) LaserWidthLogical = widthLogical;
            else if (Tool == Tool.Eraser) EraserRadiusLogical = widthLogical;
            else if (Tool == Tool.PixelEraser) PixelEraserWidthLogical = widthLogical;
            else PenWidthLogical = widthLogical;
        }

        PassThrough = false;
        PointerInside = true;
        _drawing = false;
        LastPointerType = showcasePenDevice ? Native.PT_PEN : Native.PT_MOUSE;
        PointerX = _virtualX + _virtualW * 0.5f;
        PointerY = _virtualY + _virtualH * 0.5f;
        ShowHud = false;
        _dirty = true;
        SettleFrames(600);
        string size = Tool == Tool.PixelEraser
            ? $"矩形 {PixelEraserWidthLogical:F0}×{PixelEraserHeightLogical:F0} 逻辑像素"
              + $"（高:宽 = {GoldenRatio:F3}）"
            : $"宽 {CurrentToolWidthLogical} 逻辑像素（外圈 {CursorOuterRadius:F0}px）";
        Console.WriteLine($"落点反馈已摆好：{Tool} {size}"
                        + $"（{DrawnCursor}，脏区 {DrawnCursorRadius:F0}px）");
        Console.WriteLine("cursor showcase ready");
    }

    private bool showcasePenDevice = false;

    /// <summary>
    /// 像素橡皮摆样（--pixeleraseshow）：画几条"板书"，用竖着的黄金比矩形抹一下，
    /// **自己抓屏**存两张图——擦之前（带落点）和擦之后（落点挪开）。
    ///
    /// 为什么不靠外部截图工具：自检已经能抓屏了（ScreenProbe），摆样自己存图少一步人工。
    /// 落点在第二张里必须挪开——矩形正压在切口上，就看不出"一笔被切成了两段"。
    /// </summary>
    private void PixelEraserShowcase()
    {
        Doc.Clear();
        Doc.ClearHistory();
        float cx = _virtualX + _virtualW * 0.5f;
        float cy = _virtualY + _virtualH * 0.5f;
        float dpi = DpiScale;

        // 三条横墨（笔迹，会被切成两段）、一条荧光笔（同样会被切）、
        // 一个矩形（图形，轮廓被碰到时整条删）。三种对象一屏看全。
        // 墨水的位置要落在橡皮的竖边范围（±半高）里——落在外面就看不出"擦掉了一整条"。
        foreach (float dy in new[] { -120f, -60f, 0f })
        {
            var s = new Stroke
            {
                Tool = Tool.Pen, Color = new Color4(0.10f, 0.11f, 0.13f, 1f), Width = 8f * dpi,
            };
            for (int i = 0; i <= 120; i++)
                s.AddPoint(cx - 520 + i * 9f, cy + dy + MathF.Sin(i * 0.32f) * 6f, 0.9f, i);
            Doc.AddStroke(s);
        }
        var hl = new Stroke
        {
            Tool = Tool.Highlighter, Color = new Color4(1f, 0.84f, 0.16f, 1f), Width = 26f * dpi,
        };
        for (int i = 0; i <= 60; i++) hl.AddPoint(cx - 440 + i * 15f, cy + 110, 1f, i);
        Doc.AddStroke(hl);

        var box = new Stroke
        {
            Tool = Tool.Rectangle, Kind = StrokeKind.Rectangle,
            Color = new Color4(0.9f, 0.2f, 0.2f, 1f), Width = 5f * dpi,
        };
        box.AddPoint(cx + 40, cy - 250, 1f, 0);       // 左边压在橡皮里（橡皮半宽 = 93 物理像素）
        box.AddPoint(cx + 280, cy + 250, 1f, 0);
        Doc.AddStroke(box);

        // **多段图形**（长方体 12 条棱、圆柱 5 笔）——2026-09-20 修的那两类：
        // 擦一刀之后外形逐笔不变：被挡的棱 / 被挡的半圈熔完**还是细虚线**，
        // 段与段之间也**不会连出假线**。都摆在橡皮的竖直带里，好看出"确实切了一刀"。
        var cub = new Stroke
        {
            Tool = Tool.Cuboid, Kind = StrokeKind.Cuboid,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * dpi,
        };
        cub.SetCuboidFront(cx - 40, cy + 40, cx + 180, cy + 240);
        cub.SetCuboidDepth(cx + 20, cy - 20);         // 深度 = |40 − (−20)| = 60
        Doc.AddStroke(cub);

        var cyl = new Stroke
        {
            Tool = Tool.Cylinder, Kind = StrokeKind.Cylinder,
            Color = new Color4(0.15f, 0.45f, 0.9f, 1f), Width = 5f * dpi,
        };
        cyl.SetSolidBox(cx - 320, cy - 300, cx - 80, cy + 60);   // 右边那条母线压在橡皮里
        Doc.AddStroke(cyl);

        Tool = Tool.PixelEraser;
        PassThrough = false;
        ShowHud = false;
        PointerInside = true;
        LastPointerType = Native.PT_MOUSE;
        PointerX = cx; PointerY = cy;
        Doc.InvalidateAll();
        SettleFrames(600);

        int w = (int)(PixelEraserWidthLogical * dpi * 4.6f);
        int h = (int)(PixelEraserHeightLogical * dpi * 3.6f);
        int x0 = (int)cx - w / 2, y0 = (int)cy - h / 2;

        string shot1 = Path.Combine("reports", "像素橡皮-1-落点与擦之前.bmp");
        ScreenProbe.SaveBmp(shot1, x0, y0, w, h);
        Console.WriteLine($"已存 {Path.GetFullPath(shot1)}");
        Console.WriteLine($"  落点：{(int)PixelEraserWidthLogical}×{(int)PixelEraserHeightLogical} 逻辑像素"
                        + $"（{PixelEraserWidthLogical * dpi:F0}×{PixelEraserHeightLogical * dpi:F0} 物理像素）");

        int before = Doc.Strokes.Count;
        Doc.BeginEraseRect();
        Doc.EraseRectAt(cx, cy, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
        Doc.EndErase();

        // 落点挪开再拍：矩形正压在切口上就看不出切成了什么样。
        PointerX = _virtualX + 120; PointerY = _virtualY + _virtualH - 120;
        SettleFrames(600);

        string shot2 = Path.Combine("reports", "像素橡皮-2-擦之后.bmp");
        ScreenProbe.SaveBmp(shot2, x0, y0, w, h);
        Console.WriteLine($"已存 {Path.GetFullPath(shot2)}");
        Console.WriteLine($"  擦之前 {before} 条 → 擦之后 {Doc.Strokes.Count} 条"
                        + "（三条横墨 + 荧光笔各切成两截；矩形熔成笔迹后再切；"
                        + "长方体、圆柱熔成**一条棱一段**——虚线棱 / 虚线半圈还是虚线，"
                        + "段与段之间没有假线）");
        for (int i = 0; i < Doc.Strokes.Count; i++)
        {
            var s = Doc.Strokes[i];
            Console.WriteLine($"    #{i} {s.Kind,-9} 点 {s.Points.Count,4} 擦除区间 {s.Erased.Count} 段"
                            + $" 剩余 {s.RemainingRuns().Count} 段"
                            + $"  x {s.Bounds.MinX:F0}..{s.Bounds.MaxX:F0}"
                            + $"  y {s.Bounds.MinY:F0}..{s.Bounds.MaxY:F0}");
        }
        Console.WriteLine("pixel eraser showcase done");
        _quit = true;
    }

    /// <summary>
    /// 旋转标签摆样：选中一个对象、把框转到指定角度、把度数标签挂出来不动，
    /// 由外部截图人工核对（位置、可读性、吸附变色）。这个模式不会自己退出。
    /// </summary>
    private void RotateShowcase(float deg)
    {
        Doc.Clear();
        Doc.ClearHistory();
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }

        float sx = _virtualX + _virtualW * 0.5f, sy = _virtualY + _virtualH * 0.55f;
        var stroke = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = PenColor, Width = 8f * DpiScale,
        };
        for (int i = 0; i <= 48; i++)
        {
            float a = i / 48f * MathF.PI * 2f;
            stroke.AddPoint(sx + MathF.Cos(a) * 170f * DpiScale,
                            sy + MathF.Sin(a) * 120f * DpiScale, 0.9f, i * 8);
        }
        Doc.AddStroke(stroke);
        Doc.Selected.Clear();
        Doc.Selected.Add(stroke);
        Tool = Tool.Marquee;

        // 真的转过去：框和标签都跟着转，截图才是"正在转"的样子
        var m = Matrix3x2.CreateRotation(deg * MathF.PI / 180f, new Vector2(sx, sy));
        Doc.SetTransformLive(stroke, m);
        Doc.SetTransformLive(stroke, m);
        _dirty = true;
        SettleFrames(300);

        SelRotating = true;
        SelRotationDegrees = deg;
        SelRotationSnapped = Math.Abs(deg % 90f) < 0.5f;
        _dirty = true;
        SettleFrames(700);
        Console.WriteLine($"旋转标签已摆好：{SelectionHandles.FormatDegrees(deg)}"
                        + (SelRotationSnapped ? "（吸附态）" : "（自由态）"));
        Console.WriteLine("rotate showcase ready");
    }
}
