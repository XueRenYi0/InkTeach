using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DesignSheet;

namespace UiMock;

/// <summary>
/// 三带面板的全部画法。窗口和出图都用这一份代码，所以"看到的"和"截出来的"永远一致。
///
/// 这一版的核心变化（第五轮）：
///   · 上带不再只是色带，而是「**当前工具的设置条**」——
///     笔看色片、橡皮看「整笔/面积」、截屏看「直接/隐藏批注」、图形看图形选择……
///     平时它退化成一条 6 像素的色线（纯装饰），碰到才长成该工具的选项。
///   · 色片改成 **12 色、满饱和、带间隙**（原来那版把它往白里混了 45%，所以不鲜艳）。
///   · 图标放大一档：按钮 44、图标 24（Fluent 原生 24 网格，不再缩到 20）。
/// </summary>
internal static class PanelDraw
{
    // ---- 尺寸令牌 -------------------------------------------------------

    /// <summary>面板宽度不是写死的：它由按钮尺寸算出来（图标放大，条子就得跟着变长）。</summary>
    public const double PanelWBase = 592;

    public static double BarContentWidth(double btn) => BarContentWidth(btn, AllToolsIndex.Length, false);

    static readonly int[] AllToolsIndex = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

    // 工具下标写成具名常量：以后再往主条里插一格，只改这一处，不用满文件找数字
    public const int ToolMouse = 0, ToolBoard = 1, ToolPen = 2, ToolHighlighter = 3, ToolLaser = 4,
                     ToolEraser = 5, ToolSelect = 6, ToolShapes = 7, ToolCapture = 8,
                     ToolUndo = 9, ToolRedo = 10, ToolMore = 11;

    /// <summary>极简档：只钉"笔 / 橡皮"，加一个永远的"更多"（下标是 Tools 里的原始下标）。</summary>
    public static readonly int[] MiniTools = { ToolPen, ToolEraser, ToolMore };

    /// <summary>极简档的 4 个颜色：红 / 黑 / 蓝 / 白（白笔是投影刚需）。</summary>
    public static readonly int[] MiniPalette = { 0, BlackIndex, 5, WhiteIndex };

    /// <summary>色片下标 → <see cref="Pen"/> 里的真实下标（极简档只摆 4 个色块）。</summary>
    public static int ColorIndexOf(PanelState s, StripSpec spec, int chip)
        => (s.Mini && spec.Kind == StripKind.Colors && !spec.Decorative) ? MiniPalette[chip] : chip;

    /// <summary>这一刻主条上钉了哪些工具。</summary>
    public static int[] VisibleTools(PanelState s) => s.Mini ? MiniTools : AllToolsIndex;

    public static double BarContentWidth(double btn, int count, bool mini)
    {
        double w = Pad * 2 + btn + (SepGap * 2 + 1);
        for (int i = 0; i < count; i++)
        {
            w += btn;
            if (i < count - 1)
                w += (!mini && Array.IndexOf(GroupEnds, i) >= 0) ? SepGap * 2 + 1 : Gap;
        }
        return w;
    }
    public const double BandIdle = 6;     // 平时：一条色线
    public const double BandOpen = 34;    // 碰到：该工具的设置条
    public const double GrooveH = 18;     // 底部：滑条独占的那一行
    // 瘦身档：滑条不再独占一行，嵌进按钮带下沿；色线 6→4、色板 34→28。
    // 面板下面多留 12 像素"透明命中带"，滑条的手感才够（真实产品里就是让界面矩形比可见面板大一圈）。
    public const double SlimBandIdle = 4, SlimBandOpen = 28, SlimHitPad = 12;
    public const double Gap = 4, SepGap = 8, Pad = 8;

    /// <summary>
    /// 四档"图标/按钮"搭配，按 S 循环。
    /// 第 0 档是照 Windows 11 任务栏量的（栏高 48、图标 24、按钮 40、间距 4）——
    /// 图标 24 占按钮 40 的 60%，和任务栏一模一样。
    /// </summary>
    public static readonly (double Btn, double Icon, double Row)[] Scales =
    {
        (40, 24, 48),   // 任务栏档（默认）：照 Windows 11 任务栏的比例
        (44, 24, 56),   // 前几轮的"现状"
        (48, 28, 62),   // 再大一档
        (40, 20, 52),   // 小图标档（图标 20）
    };

    public static readonly (string Icon, string Filled, string Name)[] Tools =
    {
        ("mouse", "mouseFilled", "鼠标"),          // ToolMouse
        ("board", "board", "白板"),                // ToolBoard —— 和鼠标同属"画布模式"，所以紧挨着
        ("pen", "penFilled", "笔"),                // ToolPen
        ("highlighter", "highlighterFilled", "荧光笔"),
        ("laser", "laserFilled", "激光笔"),
        ("eraser", "eraserFilled", "橡皮擦"),
        ("select", "selectFilled", "选择"),
        ("shapes", "shapesFilled", "图形"),
        ("capture", "captureFilled", "截屏"),
        ("undo", "undoFilled", "后撤"),
        ("redo", "redoFilled", "重做"),
        ("more", "moreFilled", "更多"),
    };

    /// <summary>这些下标之后插一条分隔线：鼠标+白板 ｜ 四种笔 ｜ 编辑类 ｜ 后撤重做 ｜ 更多。</summary>
    public static readonly int[] GroupEnds = { ToolBoard, ToolEraser, ToolCapture, ToolRedo };

    /// <summary>后撤/重做是"动作"，点了就执行，不切换上下文。</summary>
    public static bool IsAction(int tool) => tool is ToolUndo or ToolRedo;
    /// <summary>"更多"是入口：点了开合抽屉，也不改当前工具。</summary>
    public static bool IsEntry(int tool) => tool == ToolMore;

    /// <summary>白板是"画布开关"，不是工具：点了不改当前工具，只切底色。</summary>
    public const int BoardTool = ToolBoard;

    /// <summary>板色：白板 / 绿板 / 黑板 —— 教学上最常见的三种。</summary>
    public static readonly (string Name, Color Color)[] BoardColors =
    {
        ("白板", C(0xFA, 0xFA, 0xF8)),
        ("绿板", C(0x1E, 0x3A, 0x2E)),
        ("黑板", C(0x18, 0x1A, 0x1E)),
    };

    // ---- 「更多」抽屉 -----------------------------------------------------

    public const double DrawerW = 380, DrawerH = 228, DrawerGap = 8;

    /// <summary>抽屉里的三组东西：应用 / 界面 / 学科工具（占位）。</summary>
    public static readonly (string Group, (string Icon, string Label, int Kind)[] Items)[] Drawer =
    {
        ("应用", new[]
        {
            ("arrowSync", "检查更新", 0),
            ("arrowClockwise", "重启", 0),
            ("power", "退出", 0),
        }),
        ("界面", new[]
        {
            ("darkTheme", "深色主题", 1),
            ("dockRow", "贴边隐藏", 2),
            ("color", "装饰带", 3),
        }),
        ("学科工具（即将加入）", new[]
        {
            ("ruler", "直尺", -1),
            ("mathFormula", "量角器", -1),
            ("grid", "田字格", -1),
        }),
    };

    // ---- 12 色 ----------------------------------------------------------
    // 前 9 个和引擎的 InkPalette 一致（红橙黄绿青蓝紫黑白），后 3 个是这一轮新增：粉 / 棕 / 灰。
    public static readonly Color[] Pen =
    {
        C(0xF2, 0x2E, 0x2E), C(0xFA, 0x8C, 0x17), C(0xFA, 0xD1, 0x1F),
        C(0x21, 0xB3, 0x54), C(0x1A, 0xB8, 0xC7), C(0x21, 0x73, 0xE6),
        C(0x8C, 0x47, 0xDB), C(0xE8, 0x4D, 0x8A), C(0x8A, 0x5A, 0x2B),
        C(0x6B, 0x72, 0x80), C(0x1C, 0x1F, 0x26), C(0xFF, 0xFF, 0xFF),
    };

    public static readonly string[] PenName =
    { "红", "橙", "黄", "绿", "青", "蓝", "紫", "粉", "棕", "灰", "黑", "白" };

    /// <summary>黑色的下标（深色主题要提亮）与白色的下标（浅色主题要退一档）。</summary>
    public const int BlackIndex = 10, WhiteIndex = 11;

    // ---- 主题 -----------------------------------------------------------

    static Color PanelFill(bool dark) => dark ? C(0x20, 0x20, 0x22, 0xCC) : C(0xFF, 0xFF, 0xFF, 0xCC);
    static Color PanelEdge(bool dark) => dark ? C(0xFF, 0xFF, 0xFF, 0x1F) : C(0x00, 0x00, 0x00, 0x1A);
    static Color InkColor(bool dark) => dark ? C(0xF2, 0xF2, 0xF2) : C(0x1B, 0x1B, 0x1F);
    static Color MutedInk(bool dark) => dark ? C(0x9A, 0x9E, 0xA6) : C(0x7A, 0x7E, 0x86);
    static Color RailTrough(bool dark) => dark ? C(0xFF, 0xFF, 0xFF, 0x1F) : C(0x00, 0x00, 0x00, 0x1A);
    static Color ChipFill(bool dark, bool selected) => selected
        ? (dark ? C(0xFF, 0xFF, 0xFF, 0x1C) : C(0x00, 0x00, 0x00, 0x0A))
        : (dark ? C(0xFF, 0xFF, 0xFF, 0x0A) : C(0x00, 0x00, 0x00, 0x05));

    /// <summary>色片的显示色：极值色离开背景（浅底的白退一档、深底的黑提亮一档）。</summary>
    public static Color SwatchDisplay(int i, bool dark)
    {
        if (i == WhiteIndex && !dark) return C(0xF7, 0xF8, 0xFA);
        if (i == BlackIndex && dark) return C(0x3C, 0x41, 0x4A);
        return Pen[i];
    }

    static Color SwatchEdge(int i, bool dark)
    {
        bool extreme = (i == WhiteIndex && !dark) || (i == BlackIndex && dark);
        if (extreme) return dark ? C(0xFF, 0xFF, 0xFF, 0x66) : C(0x00, 0x00, 0x00, 0x40);
        return dark ? C(0x00, 0x00, 0x00, 0x33) : C(0x00, 0x00, 0x00, 0x22);
    }

    // ---- 上带的内容：每个工具自己的设置条 -------------------------------

    public enum StripKind { None, Colors, Segments, Shapes, Toggles }

    public sealed class StripSpec
    {
        public StripKind Kind = StripKind.None;
        public string[] Labels = Array.Empty<string>();
        public string[] Icons = Array.Empty<string>();
        public bool[] On = Array.Empty<bool>();
        public int Sel;
        /// <summary>滑条在这一档存不存在；不存在的话底部那条会整条收起。</summary>
        public bool HasSlider;
        public string SliderHint = "";
        /// <summary>没有选项的工具：上带退回成"装饰用色带"，不可点。</summary>
        public bool Decorative;
        /// <summary>
        /// 右端的"动作"按钮：模式是模式，动作是动作 —— 动作点一下就执行，不会保持高亮。
        /// HoldMs > 0 表示"按住才算数"（清空用），否则是普通点击。
        /// </summary>
        public string ActionIcon;
        public string ActionLabel = "";
        public int ActionId = -1;      // 0 = 清空，1 = 全选
        public double ActionHoldMs;
    }

    public const int ActionClear = 0, ActionSelectAll = 1;

    public static StripSpec SpecOf(PanelState s)
    {
        var sp = new StripSpec();
        // 后撤 / 重做是"动作"：它们没有自己的设置，上带就退回成装饰色带
        if (IsAction(s.Tool))
            return new StripSpec { Kind = StripKind.Colors, Decorative = true, Sel = s.Color };
        switch (s.Tool)
        {
            case ToolPen: // 笔
                sp.Kind = StripKind.Colors; sp.Sel = s.Color; sp.HasSlider = true; sp.SliderHint = "笔宽";
                break;
            case ToolHighlighter: // 荧光笔
                sp.Kind = StripKind.Colors; sp.Sel = s.Color; sp.HasSlider = true; sp.SliderHint = "荧光笔宽";
                break;
            case ToolLaser: // 激光笔
                sp.Kind = StripKind.Segments; sp.Labels = new[] { "小", "中", "大" };
                sp.Sel = s.LaserSize; sp.HasSlider = true; sp.SliderHint = "光点大小";
                break;
            case ToolEraser: // 橡皮擦 —— 你说的"线擦还是面积擦"
                sp.Kind = StripKind.Segments; sp.Labels = new[] { "整笔擦", "面积擦" };
                sp.Sel = s.EraserMode; sp.HasSlider = true; sp.SliderHint = "橡皮大小";
                // 清空挂在橡皮这条的右端：擦一点 / 擦一块 / 全擦掉，语义是一路的。
                // **按住才算数**（0.8 秒），因为清空是可撤销、但代价很大的动作。
                sp.ActionIcon = "broom"; sp.ActionLabel = "清空"; sp.ActionId = ActionClear; sp.ActionHoldMs = 800;
                break;
            case ToolSelect: // 选择
                sp.Kind = StripKind.Segments; sp.Labels = new[] { "矩形框选", "自由套索" }; sp.Sel = s.SelectMode;
                // 全选是"动作"不是"模式"：点一下就执行，不会保持高亮
                sp.ActionIcon = "selectAll"; sp.ActionLabel = "全选"; sp.ActionId = ActionSelectAll;
                break;
            case ToolShapes: // 图形
                sp.Kind = StripKind.Shapes; sp.Sel = s.ShapeKind;
                sp.Icons = new[] { "lineWeight", "arrowRight", "square", "circle", "triangle", "shapes" };
                sp.Labels = new[] { "直线", "箭头", "矩形", "椭圆", "三角", "平行四边形" };
                sp.HasSlider = true; sp.SliderHint = "线宽";
                break;
            case ToolCapture: // 截屏 —— 你说的"直接截 / 隐藏界面截"
                sp.Kind = StripKind.Segments; sp.Labels = new[] { "直接截取", "隐藏批注截取" }; sp.Sel = s.CaptureHideInk ? 1 : 0;
                break;
            case ToolMore: // 「更多」是入口不是工具：上带留作装饰（里面的开关都搬进抽屉了）
                sp.Kind = StripKind.Colors; sp.Decorative = true; sp.Sel = s.Color;
                break;
            case ToolBoard: // 白板：上带就是三种板色（正好复用"上带＝这个按钮的设置条"这条规则）
                sp.Kind = StripKind.Segments; sp.Labels = new[] { "白板", "绿板", "黑板" };
                sp.Sel = s.BoardColor;
                break;
            default: // 鼠标（穿透）
                sp.Kind = StripKind.Segments; sp.Labels = new[] { "直接操作", "穿透点击" }; sp.Sel = s.PassThrough ? 1 : 0;
                break;
        }
        return sp;
    }

    // ---- 布局 -----------------------------------------------------------

    public sealed class Layout
    {
        public double W, H, Band, BtnRow, GrooveBandH, Radius, E;
        /// <summary>上带是否处于"收起成一条色线"的样子（此时不画任何控件，免得挤出汉字）。</summary>
        public bool Collapsed;
        public Rect Rail, Buttons, Groove, Ball;
        public Rect[] Tiles = Array.Empty<Rect>();
        public Rect[] Items = Array.Empty<Rect>();   // 上带里的色片 / 分段 / 图形 / 开关
        public Rect ActionRect;                      // 上带右端的"动作"按钮（没有就是空矩形）
        public double HitPadBottom;                  // 元素比可见面板多出来的高度（瘦身档的透明命中带）
        public double SliderY;                       // 滑条中心线
        public double SliderZoneTop, SliderZoneBottom;
        public double OriginY;                       // 面板在元素里的纵向偏移（抽屉打开时下移）
        public double OriginX;                       // 面板在元素里的横向偏移（抽屉比面板宽时居中）
        public double ContentW;                      // 元素内容宽度（= max(面板宽, 抽屉宽)）
        public Rect DrawerRect;                      // 「更多」抽屉
        public (Rect R, int Group, int Index)[] MoreItems = Array.Empty<(Rect, int, int)>();
        public int[] ToolIndices = Array.Empty<int>();   // 每个按钮对应的原始工具下标
        public double Btn = 44, Icon = 24;
    }

    public static Layout Compute(PanelState s)
    {
        var scale = Scales[Math.Clamp(s.IconScale, 0, Scales.Length - 1)];
        var spec = SpecOf(s);
        double e = Clamp01(s.E);
        bool slim = s.Slim;
        double bandIdle = slim ? SlimBandIdle : BandIdle;
        double bandOpen = slim ? SlimBandOpen : BandOpen;
        double bandFull = bandIdle + (bandOpen - bandIdle) * Clamp01(s.Rail);
        double band = bandFull * Clamp01((e - 0.25) / 0.75);
        // 底带永远占住高度：有滑条时它是滑条，没有时它是一条装饰线。
        // 瘦身档里它不再单独占一行（groove = 0），而是嵌进按钮带下沿。
        double groove = (slim ? 0 : GrooveH) * e;

        var L = new Layout { E = e, Btn = scale.Btn, Icon = scale.Icon };
        var vis = VisibleTools(s);
        double fullW = BarContentWidth(scale.Btn, vis.Length, s.Mini);
        L.W = 48 + (fullW - 48) * e;
        L.Band = band;
        L.GrooveBandH = groove;
        L.H = Math.Max(48, band + scale.Row * e + groove);
        L.Radius = 24 - 6 * e;
        L.BtnRow = Math.Max(0, L.H - band - groove);

        L.Rail = new Rect(0, 0, L.W, band);
        L.Buttons = new Rect(0, band, L.W, L.BtnRow);
        L.Groove = new Rect(0, L.H - groove, L.W, groove);
        L.Ball = new Rect(0, 0, L.W, L.H);
        L.HitPadBottom = slim ? SlimHitPad * e : 0;
        L.SliderY = slim ? L.H - 3 : L.H - groove / 2;
        L.SliderZoneTop = slim ? L.H - 20 : L.H - groove - 8;
        L.SliderZoneBottom = L.H + L.HitPadBottom;

        // 「更多」抽屉：贴在面板右侧、往上长（贴底时下面没有空间）
        if (s.MoreOpen && e > 0.9)
        {
            L.OriginY = DrawerH + DrawerGap;
            // 抽屉可能比面板宽（极简档只有 201）—— 那就把两者居中放进同一条内容宽度里
            L.ContentW = Math.Max(L.W, DrawerW);
            L.OriginX = (L.ContentW - L.W) / 2;
            double dx = (L.ContentW - DrawerW) / 2;
            L.DrawerRect = new Rect(dx, 0, DrawerW, DrawerH);
            var its = new List<(Rect, int, int)>();
            double gy = 12;
            for (int g = 0; g < Drawer.Length; g++)
            {
                gy += 16;
                for (int i = 0; i < Drawer[g].Items.Length; i++)
                    its.Add((new Rect(dx + 14 + i * (112 + 8), gy, 112, 46), g, i));
                gy += 46 + 10;
            }
            L.MoreItems = its.ToArray();
        }
        else L.ContentW = L.W;

        // 上带的内容：贴着条的中间排
        var items = new List<Rect>();
        // 收起时（≤12）永远是那条色线 —— 不管当前是哪个工具。
        // 之前这里会按工具去画"分段/开关"，于是 6 像素高的带子里挤出汉字来，很难看。
        L.Collapsed = band <= 12;
        if (band > 2 && e > 0.6)
        {
            double inset = 16;
            // 高度和缝隙都必须**连续**：原来在 band=14 处从 14 突降到 7，
            // 正好卡在开合动画中间 —— 那就是"划过色带大小闪一下"的来源。
            double itemH = Math.Min(26, band * 0.8);
            double cy = band / 2 - itemH / 2;
            StripKind kind = L.Collapsed ? StripKind.Colors : spec.Kind;

            // 右端的"动作"按钮：模式占左边，动作占右边 —— 两者之间留 8 像素分隔
            bool showAction = !L.Collapsed && !string.IsNullOrEmpty(spec.ActionIcon) && band > 20;
            // 动作按钮随面板宽度伸缩：极简档只有 201 宽，还给 152 的话两个模式段就没地方了
            double actionW = showAction ? Math.Clamp(L.W * 0.36, 62, 152) : 0;
            double avail = L.W - inset * 2 - (actionW > 0 ? actionW + 8 : 0);
            L.ActionRect = showAction
                ? new Rect(L.W - inset - actionW, cy, actionW, itemH)
                : Rect.Empty;
            switch (kind)
            {
                case StripKind.Colors:
                {
                    // 极简档只给 4 个色块（红/黑/蓝/白）；完整档给全部 12 个
                    int n = (s.Mini && kind == StripKind.Colors && !spec.Decorative) ? MiniPalette.Length : Pen.Length;
                    double gap = 2 + 2 * Clamp01((band - 10) / 16);
                    double cw = (avail - gap * (n - 1)) / n;
                    for (int i = 0; i < n; i++)
                        items.Add(new Rect(inset + i * (cw + gap), cy, cw, itemH));
                    break;
                }
                case StripKind.Segments:
                {
                    int n = spec.Labels.Length;
                    // 平分上带的空间（你说的"两段应该平分"）——不再是一个固定宽度居中摆着
                    double gap = 6;
                    double cw = (avail - gap * (n - 1)) / n;
                    double x0 = inset;
                    for (int i = 0; i < n; i++)
                        items.Add(new Rect(x0 + i * (cw + gap), cy, cw, itemH));
                    break;
                }
                case StripKind.Shapes:
                {
                    int n = spec.Icons.Length;
                    double gap = 6;
                    double cw = Math.Min(84, (avail - gap * (n - 1)) / n);
                    double total = n * cw + (n - 1) * gap;
                    double x0 = inset + (avail - total) / 2;
                    for (int i = 0; i < n; i++)
                        items.Add(new Rect(x0 + i * (cw + gap), cy, cw, itemH));
                    break;
                }
                case StripKind.Toggles:
                {
                    int n = spec.Labels.Length;
                    double gap = 8, cw = 150;
                    double total = n * cw + (n - 1) * gap;
                    double x0 = (L.W - total) / 2;
                    for (int i = 0; i < n; i++)
                        items.Add(new Rect(x0 + i * (cw + gap), cy, cw, itemH));
                    break;
                }
            }
        }
        L.Items = items.ToArray();

        // 按钮
        var tiles = new List<Rect>();
        if (e > 0.55)
        {
            double btn = L.Btn;
            // 瘦身档：把行底下留出 8 像素给滑条，其余按比例分给上边距（最少 2 像素，免得贴顶）
            double padTop = slim ? Math.Clamp(scale.Row - scale.Btn - 8, 2, 8) : 6;
            double cy = band + padTop + btn / 2;
            double cx = Pad + btn / 2;
            tiles.Add(new Rect(cx - btn / 2, cy - btn / 2, btn, btn));
            cx += btn / 2 + SepGap * 2 + 1;
            for (int i = 0; i < vis.Length; i++)
            {
                tiles.Add(new Rect(cx, cy - btn / 2, btn, btn));
                cx += btn;
                if (i < vis.Length - 1)
                    cx += (!s.Mini && Array.IndexOf(GroupEnds, i) >= 0) ? SepGap * 2 + 1 : Gap;
            }
        }
        L.Tiles = tiles.ToArray();
        L.ToolIndices = vis;
        return L;
    }

    // ---- 绘制 -----------------------------------------------------------

    public static void Draw(DrawingContext c, PanelState s)
    {
        var L = Compute(s);
        bool dark = s.Dark;

        // 「更多」抽屉先画（它在面板上方），然后把面板整体下移再照常画
        if (s.MoreOpen && !L.DrawerRect.IsEmpty) DrawDrawer(c, s, L);
        if (L.OriginY > 0.5)
        {
            c.PushTransform(new TranslateTransform(L.OriginX, L.OriginY));
            DrawPanel(c, s, L);
            c.Pop();
            return;
        }
        if (L.OriginX > 0.5)
        {
            c.PushTransform(new TranslateTransform(L.OriginX, 0));
            DrawPanel(c, s, L);
            c.Pop();
            return;
        }
        DrawPanel(c, s, L);
    }

    static void DrawPanel(DrawingContext c, PanelState s, Layout L)
    {
        bool dark = s.Dark;

        if (L.E < 0.02)
        {
            DrawBall(c, L, s);
            return;
        }

        Shadow(c, new Rect(0, 0, L.W, L.H), L.Radius);
        c.DrawRoundedRectangle(new SolidColorBrush(PanelFill(dark)),
                               new Pen(new SolidColorBrush(PanelEdge(dark)), 1),
                               Inset(new Rect(0, 0, L.W, L.H)), L.Radius, L.Radius);

        if (L.Band > 1)
        {
            c.PushClip(new RectangleGeometry(new Rect(0, 0, L.W, L.H), L.Radius, L.Radius));
            c.DrawRectangle(new SolidColorBrush(RailTrough(dark)), null, L.Rail);
            DrawStrip(c, s, L);
            c.Pop();
        }

        if (L.Tiles.Length > 0) DrawButtons(c, s, L);
        if (L.E > 0.55) DrawGroove(c, s, L);

        // 面板顶部一道极淡的内高光（Windows 11 的层次感靠它）
        if (L.Band > 20)
        {
            c.PushClip(new RectangleGeometry(new Rect(0, 0, L.W, L.H), L.Radius, L.Radius));
            c.DrawRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x14) : C(0xFF, 0xFF, 0xFF, 0x8C)),
                            null, new Rect(1, 0.5, L.W - 2, 1));
            c.Pop();
        }

        // 清空执行完：整块面板闪一圈红边 —— 让"刚刚发生了一件事"被看见
        if (s.ClearFlash > 0.01)
        {
            c.DrawRoundedRectangle(null,
                                   new Pen(new SolidColorBrush(C(0xE0, 0x2B, 0x2B, (byte)(0x99 * s.ClearFlash))), 2.5),
                                   Inset(new Rect(0, 0, L.W, L.H)), L.Radius, L.Radius);
        }
    }

    /// <summary>上带：按当前工具画色片 / 分段 / 图形 / 开关。</summary>
    static void DrawDrawer(DrawingContext c, PanelState s, Layout L)
    {
        bool dark = s.Dark;
        var r = L.DrawerRect;
        // 抽屉跟着面板一起淡入淡出
        double fade = Clamp01(s.ContentFade);
        if (fade < 0.999) { c.PushOpacity(fade); c.PushTransform(new TranslateTransform(0, (1 - fade) * 6)); }

        Shadow(c, r, 14);
        c.DrawRoundedRectangle(new SolidColorBrush(PanelFill(dark)),
                               new Pen(new SolidColorBrush(PanelEdge(dark)), 1),
                               Inset(r), 14, 14);

        foreach (var it in L.MoreItems)
        {
            var cell = it.R;
            var item = Drawer[it.Group].Items[it.Index];
            bool soon = item.Kind < 0;
            bool hover = !soon && s.MoreHover == (it.Group * 10 + it.Index);
            if (hover)
                c.DrawRoundedRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x14) : C(0x00, 0x00, 0x00, 0x0C)),
                                       null, cell, 8, 8);
            var fg = soon
                ? new SolidColorBrush(C(0x9A, 0xA0, 0xAA))
                : new SolidColorBrush(InkColor(dark));
            Icon(c, item.Icon, cell.X + 22, cell.Y + cell.Height / 2, 20, fg);
            Text(c, item.Label, cell.X + 40, cell.Y + (cell.Height - 17) / 2, 12.5, fg);
        }

        // 组标签画在每行格子的上方
        double gy = r.Y + 12;
        for (int g = 0; g < Drawer.Length; g++)
        {
            Text(c, Drawer[g].Group, r.X + 14, gy, 11.5, new SolidColorBrush(dark ? C(0x9A, 0x9E, 0xA6) : C(0x6B, 0x70, 0x78)));
            gy += 16 + 46 + 10;
        }

        if (fade < 0.999) { c.Pop(); c.Pop(); }
    }

    static void DrawStrip(DrawingContext c, PanelState s, Layout L)
    {
        var spec = SpecOf(s);
        bool dark = s.Dark;

        // 切工具时内容"连着变"：淡入淡出 + 轻微上移（Windows 的 Connected 原则）
        double fade = Clamp01(s.ContentFade);
        if (fade < 0.999)
        {
            c.PushOpacity(fade);
            c.PushTransform(new TranslateTransform(0, (1 - fade) * -5));
        }

        // 收起态就是"一条色线"：**整条用当前笔色**，不分段、没有文字。
        // 展开的过程中，这条线淡出、控件淡入 —— 两个状态之间是"连着"的。
        double t = Clamp01((L.Band - 12) / 14);   // 0 = 还是一条线，1 = 完全是控件
        if (t < 0.999)
        {
            Color line = SwatchDisplay(s.Color, dark);
            // 白笔在浅底上会看不见：给一条极淡的描边兜底
            Pen edge = (s.Color == WhiteIndex && !dark)
                ? new Pen(new SolidColorBrush(C(0x00, 0x00, 0x00, 0x33)), 1)
                : null;
            c.PushOpacity(1 - t);
            c.DrawRectangle(new SolidColorBrush(line), edge, new Rect(0, 0, L.W, L.Band));
            c.Pop();
        }
        if (t <= 0.001)
        {
            if (fade < 0.999) { c.Pop(); c.Pop(); }
            return;
        }
        c.PushOpacity(t);

        for (int i = 0; i < L.Items.Length; i++)
        {
            var r = L.Items[i];
            switch (spec.Kind)
            {
                case StripKind.Colors:
                {
                    int pen = ColorIndexOf(s, spec, i);      // 片下标 → Pen 里的真实下标（极简档只给 4 个）
                    bool sel = pen == s.Color;
                    // 满饱和（不再往白里混）；只在"平时那条细线"上略压一点亮度，让它退成装饰
                    Color fill = SwatchDisplay(pen, dark);
                    if (spec.Decorative) fill = WithAlpha(fill, 0x9A);   // 装饰用：暗一档，明确"这里不能点"
                    double grow = 0;
                    var rr = new Rect(r.X, r.Y - grow, r.Width, r.Height + grow * 2);
                    // 平时只有 6 像素高：这时候再套 1px 描边，整条会被描边"吃掉"而发灰 —— 只有展开时才描边
                    Pen chipEdge = new Pen(new SolidColorBrush(SwatchEdge(pen, dark)), sel ? 1.4 : 1);
                    c.DrawRoundedRectangle(new SolidColorBrush(fill), chipEdge,
                                           rr, Math.Min(7, r.Height / 2), Math.Min(7, r.Height / 2));
                    if (sel)
                        c.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(dark ? Brushes.White.Color : C(0x1B, 0x1B, 0x1F)), 1.6),
                                               new Rect(rr.X + 2, rr.Y + 2, rr.Width - 4, rr.Height - 4), 5, 5);
                    // 一道内高光：色片才有"实体感"（这是"颜值"上最便宜的一笔）
                    if (rr.Height >= 14)
                        c.DrawRoundedRectangle(new SolidColorBrush(C(0xFF, 0xFF, 0xFF, dark ? (byte)0x22 : (byte)0x59)), null,
                                               new Rect(rr.X + 2, rr.Y + 1, rr.Width - 4, Math.Max(1, rr.Height * 0.26)), 3, 3);
                    break;
                }
                case StripKind.Segments:
                {
                    bool sel = i == spec.Sel;
                    c.DrawRoundedRectangle(new SolidColorBrush(sel ? Accent : ChipFill(dark, false)),
                                           new Pen(new SolidColorBrush(sel ? Colors.Transparent : (dark ? C(0xFF, 0xFF, 0xFF, 0x22) : C(0x00, 0x00, 0x00, 0x1E))), 1),
                                           r, 6, 6);
                    Label(c, spec.Labels[i], r, r.Width < 62 ? 11 : 12.5, sel ? Brushes.White : new SolidColorBrush(InkColor(dark)), sel);
                    break;
                }
                case StripKind.Shapes:
                {
                    bool sel = i == spec.Sel;
                    c.DrawRoundedRectangle(new SolidColorBrush(sel ? Accent : ChipFill(dark, false)),
                                           new Pen(new SolidColorBrush(sel ? Colors.Transparent : (dark ? C(0xFF, 0xFF, 0xFF, 0x22) : C(0x00, 0x00, 0x00, 0x1E))), 1),
                                           r, 6, 6);
                    Icon(c, spec.Icons[i], r.X + r.Width / 2, r.Y + r.Height / 2, 18,
                         new SolidColorBrush(sel ? Colors.White : InkColor(dark)));
                    break;
                }
                case StripKind.Toggles:
                {
                    bool on = spec.On[i];
                    c.DrawRoundedRectangle(new SolidColorBrush(on ? C(0x00, 0x67, 0xC0, 0x2E) : ChipFill(dark, false)),
                                           new Pen(new SolidColorBrush(on ? Accent : (dark ? C(0xFF, 0xFF, 0xFF, 0x22) : C(0x00, 0x00, 0x00, 0x1E))), 1),
                                           r, 6, 6);
                    Icon(c, on ? "check" : "dismiss", r.X + 16, r.Y + r.Height / 2, 14,
                         new SolidColorBrush(on ? Accent : MutedInk(dark)));
                    Text(c, spec.Labels[i], r.X + 28, r.Y + (r.Height - 17) / 2, 12.5,
                         new SolidColorBrush(dark ? C(0xE8, 0xE8, 0xE8) : C(0x2A, 0x2E, 0x36)));
                    break;
                }
            }
        }

        // 右端的"动作"按钮：点一下就执行，不保持高亮（和左边的"模式"是两回事）
        if (!L.ActionRect.IsEmpty)
        {
            var ar = L.ActionRect;
            bool clear = spec.ActionId == ActionClear;
            Color tint = clear ? C(0xE0, 0x2B, 0x2B) : C(0x00, 0x00, 0x00);
            byte baseA = clear ? (byte)0x1A : (byte)0x00;
            Color fill = clear ? WithAlpha(tint, baseA) : ChipFill(dark, false);
            Color edge = clear ? WithAlpha(tint, 0x66) : (dark ? C(0xFF, 0xFF, 0xFF, 0x22) : C(0x00, 0x00, 0x00, 0x1E));
            if (s.ActionHoverID == spec.ActionId) fill = clear ? WithAlpha(tint, 0x2E) : ChipFill(dark, true);

            c.DrawRoundedRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(edge), 1), ar, 6, 6);

            Color fg = clear ? WithAlpha(tint, 0xE6) : new SolidColorBrush(InkColor(dark)).Color;
            bool narrow = ar.Width < 104;
            Icon(c, spec.ActionIcon, narrow ? ar.X + ar.Width / 2 : ar.X + 22, ar.Y + ar.Height / 2, 18, new SolidColorBrush(fg));
            if (!narrow) Text(c, spec.ActionLabel, ar.X + 40, ar.Y + (ar.Height - 17) / 2, 12.5, new SolidColorBrush(fg));

            // 按住式动作：底部一条进度，走满才真的执行
            if (spec.ActionHoldMs > 0)
            {
                double trackY = ar.Bottom - 4.5, trackW = ar.Width - 20;
                c.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(tint, 0x2E)), null,
                                       new Rect(ar.X + 10, trackY, trackW, 2), 1, 1);
                if (s.ClearHold > 0.001)
                    c.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(tint, 0xE6)), null,
                                           new Rect(ar.X + 10, trackY, trackW * Clamp01(s.ClearHold), 2), 1, 1);
            }

            // 执行完闪一下，让人知道"点到了"
            if (s.ActionFlash > 0.01)
                c.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(WithAlpha(tint, (byte)(0xAA * s.ActionFlash))), 2), ar, 6, 6);
        }

        c.Pop();   // t
        if (fade < 0.999)
        {
            c.Pop();
            c.Pop();
        }
    }

    static void DrawButtons(DrawingContext c, PanelState s, Layout L)
    {
        bool dark = s.Dark;
        double op = Clamp01((L.E - 0.55) / 0.45);
        c.PushOpacity(op);

        var ball = L.Tiles[0];
        c.DrawEllipse(new SolidColorBrush(dark ? C(0x3A, 0x3A, 0x3E) : C(0xEC, 0xEE, 0xF2)), null,
                      new Point(ball.X + ball.Width / 2, ball.Y + ball.Height / 2), ball.Width / 2 - 2, ball.Width / 2 - 2);
        Icon(c, "chevronDown", ball.X + ball.Width / 2, ball.Y + ball.Height / 2, L.Icon * 0.9,
             new SolidColorBrush(InkColor(dark)));

        for (int i = 0; i < L.ToolIndices.Length && i + 1 < L.Tiles.Length; i++)
        {
            int tool = L.ToolIndices[i];
            var t = L.Tiles[i + 1];
            double cx = t.X + t.Width / 2, cy = t.Y + t.Height / 2;
            bool active = tool == s.Tool
                       || (tool == BoardTool && s.BoardOn)
                       || (IsEntry(tool) && s.MoreOpen);
            if (active)
                c.DrawRoundedRectangle(new SolidColorBrush(Accent), null, t, 9, 9);
            else if (i == s.HoverTile)
                c.DrawRoundedRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x14) : C(0x00, 0x00, 0x00, 0x0C)),
                                       null, t, 9, 9);

            // 按下 = 高度降一档（规范里 Rest 2 → Pressed 1）：底色再深一点 + 缩到 0.94
            bool pressed = i == s.PressTile;
            if (pressed)
            {
                double shrink = 1.5;
                var pr = new Rect(t.X + shrink, t.Y + shrink, t.Width - shrink * 2, t.Height - shrink * 2);
                c.DrawRoundedRectangle(new SolidColorBrush(active
                        ? C(0x00, 0x52, 0x9C)
                        : (dark ? C(0xFF, 0xFF, 0xFF, 0x24) : C(0x00, 0x00, 0x00, 0x18))), null, pr, 8, 8);
            }

            if (tool == ToolLaser)
                DrawLaser(c, cx, cy, L.Icon, new SolidColorBrush(active ? Colors.White : InkColor(dark)), s.LaserStyle);
            else
                Icon(c, active ? Tools[tool].Filled : Tools[tool].Icon, cx, cy, L.Icon,
                     new SolidColorBrush(active ? Colors.White : InkColor(dark)));

            if (!s.Mini && Array.IndexOf(GroupEnds, i) >= 0 && i + 2 < L.Tiles.Length)
            {
                double sepX = (t.X + t.Width + L.Tiles[i + 2].X) / 2;
                c.DrawRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x28) : C(0x00, 0x00, 0x00, 0x1E)),
                                null, new Rect(sepX, cy - 11, 1, 22));
            }
        }
        c.Pop();
    }

    static void DrawGroove(DrawingContext c, PanelState s, Layout L)
    {
        var spec = SpecOf(s);
        bool dark = s.Dark;
        double inset = 16, cy = L.SliderY;

        // 没有滑条的工具：这里留一条"踢脚线"当装饰，面板高度才稳得住
        if (!spec.HasSlider)
        {
            c.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(Pen[s.Color], 0x40)), null,
                                   new Rect(inset, cy - 1, L.W - inset * 2, 2), 1, 1);
            return;
        }

        double trackH = 4 + 2 * Clamp01(s.Groove);
        double trackW = L.W - inset * 2;
        Color ink = Pen[s.Color];
        c.DrawRoundedRectangle(new SolidColorBrush(dark ? C(0xFF, 0xFF, 0xFF, 0x14) : C(0x00, 0x00, 0x00, 0x12)), null,
                               new Rect(inset, cy - trackH / 2, trackW, trackH), trackH / 2, trackH / 2);
        c.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(ink, s.Groove > 0.4 ? (byte)0xCC : (byte)0x66)), null,
                               new Rect(inset, cy - trackH / 2, trackW * s.Slider01, trackH), trackH / 2, trackH / 2);

        // 右边的"笔尖预览"：橡皮画圆（面积擦画竖长矩形），别的一律画点
        double px = L.W - inset - 12;
        if (s.Groove > 0.4)
        {
            c.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(C(0x30, 0x34, 0x3C)), 1),
                          new Point(inset + trackW * s.Slider01, cy), 8, 8);
            if (s.Tool == ToolEraser && s.EraserMode == 0)
                c.DrawEllipse(null, new Pen(new SolidColorBrush(ink), 2.4), new Point(px, cy), 3 + 8 * s.Slider01, 3 + 8 * s.Slider01);
            else if (s.Tool == ToolEraser)
                c.DrawRoundedRectangle(new SolidColorBrush(WithAlpha(ink, 0xCC)), null,
                                       new Rect(px - (4 + 7 * s.Slider01) / 1.6, cy - (4 + 7 * s.Slider01), (4 + 7 * s.Slider01) / 0.8, (4 + 7 * s.Slider01) * 2), 3, 3);
            else
                c.DrawEllipse(new SolidColorBrush(ink), null, new Point(px, cy), 2 + 7 * s.Slider01, 2 + 7 * s.Slider01);
        }
    }

    static void DrawBall(DrawingContext c, Layout L, PanelState s)
    {
        double r = L.W / 2;
        Shadow(c, new Rect(0, 0, L.W, L.H), r);
        c.DrawEllipse(new SolidColorBrush(PanelFill(s.Dark)), new Pen(new SolidColorBrush(PanelEdge(s.Dark)), 1),
                      new Point(r, r), r - 0.5, r - 0.5);
        c.DrawEllipse(null, new Pen(new SolidColorBrush(Pen[s.Color]), 2.5), new Point(r, r), r * 0.76, r * 0.76);
        Icon(c, "pen", r, r, r * 0.7, new SolidColorBrush(InkColor(s.Dark)));
    }

    /// <summary>
    /// 激光笔图标候选（按 L 循环）。上游 Fluent 没有这个专名，所以这里有三种来源：
    /// Material Symbols 的 stylus_laser_pointer（专名，Apache-2.0）、
    /// 我们按 Fluent 的 24 网格自绘的几个、以及 Fluent 自己的近义图标。
    /// </summary>
    static void DrawLaser(DrawingContext c, double cx, double cy, double size, Brush brush, int style)
    {
        switch (style)
        {
            case 0: Icon(c, "msStylusLaser", cx, cy, size, brush); return;
            case 1: Icon(c, "msStylusLaserFill", cx, cy, size, brush); return;
            case 2: LaserPenBeam(c, cx, cy, size, brush); return;
            case 3: LaserBeamCone(c, cx, cy, size, brush); return;
            case 4: Icon(c, "laserFlash", cx, cy, size, brush); return;
            case 5: Icon(c, "laserRecord", cx, cy, size, brush); return;
            case 6: Icon(c, "laserTarget", cx, cy, size, brush); return;
            case 7: LaserRays(c, cx, cy, size, brush); return;
            default: LaserArcs(c, cx, cy, size, brush); return;
        }
    }

    /// <summary>自绘：笔 ＋ 光束 ＋ 落点（照 Excalidraw 那个思路，但画成 Fluent 的线宽）。</summary>
    static void LaserPenBeam(DrawingContext c, double cx, double cy, double size, Brush brush)
    {
        double k = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(k, k));
        var body = new Pen(brush, 3.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var thin = new Pen(brush, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        c.DrawLine(body, new Point(17.0, 4.6), new Point(12.4, 9.2));      // 笔身
        c.DrawLine(thin, new Point(11.6, 10.0), new Point(9.4, 12.2));      // 光束
        c.DrawEllipse(brush, null, new Point(6.6, 15.0), 2.1, 2.1);         // 落点
        c.Pop();
        c.Pop();
    }

    /// <summary>自绘：光点 ＋ 短射线。</summary>
    static void LaserRays(DrawingContext c, double cx, double cy, double size, Brush brush)
    {
        double k = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(k, k));
        var pen = new Pen(brush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var dot = new Point(12, 12);
        c.DrawEllipse(brush, null, dot, 3.0, 3.0);
        foreach (double a in new[] { -90.0, -40, 10, 55, 125, 180, 235 })
        {
            double r = a * Math.PI / 180.0;
            c.DrawLine(pen,
                new Point(dot.X + Math.Cos(r) * 6.0, dot.Y + Math.Sin(r) * 6.0),
                new Point(dot.X + Math.Cos(r) * 9.2, dot.Y + Math.Sin(r) * 9.2));
        }
        c.Pop();
        c.Pop();
    }

    /// <summary>自绘：光点 ＋ 三道弧（第一版那个，留着做对比）。</summary>
    static void LaserArcs(DrawingContext c, double cx, double cy, double size, Brush brush)
    {
        double k = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(k, k));
        var pen = new Pen(brush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var p0 = new Point(6.5, 17.5);
        c.DrawEllipse(brush, null, p0, 2.6, 2.6);
        foreach (double rad in new[] { 7.0, 11.0, 15.0 })
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(Polar(p0, rad, -96), false, false);
                gc.ArcTo(Polar(p0, rad, -18), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            c.DrawGeometry(null, pen, g);
        }
        c.Pop();
        c.Pop();
    }

    /// <summary>自绘：光点 ＋ 一束斜射出去的光（锥体填一层半透明，小尺寸下才有体量）。</summary>
    static void LaserBeamCone(DrawingContext c, double cx, double cy, double size, Brush brush)
    {
        double s = size / 24.0;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(s, s));
        var pen = new Pen(brush, 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var dot = new Point(6, 18);
        var cone = new StreamGeometry();
        using (var g = cone.Open())
        {
            g.BeginFigure(dot, true, true);
            g.LineTo(new Point(20.5, 3.5), true, false);
            g.LineTo(new Point(21.5, 13.5), true, false);
        }
        cone.Freeze();
        var col = (brush as SolidColorBrush)?.Color ?? Colors.Black;
        c.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x4D, col.R, col.G, col.B)), null, cone);
        c.DrawLine(pen, new Point(7.6, 16.4), new Point(20.5, 3.5));
        c.DrawLine(pen, new Point(8.4, 18.4), new Point(21.5, 13.5));
        c.DrawEllipse(brush, null, dot, 2.7, 2.7);
        c.Pop();
        c.Pop();
    }

    // ---- 小工具 ---------------------------------------------------------

    static void Shadow(DrawingContext c, Rect r, double radius)
    {
        // 按 Windows 11 的"高度"规范：浮出层（Flyout）级的阴影应当更大更软。
        // 这里用 4 层往下叠出来（真实产品里界面矩形要比可见面板大一圈，阴影才有地方铺开）。
        (double off, byte a)[] layers = { (9, 0x05), (6, 0x08), (3.5, 0x10), (2, 0x1A) };
        foreach (var (off, a) in layers)
            c.DrawRoundedRectangle(new SolidColorBrush(C(0x00, 0x00, 0x00, a)), null,
                                   new Rect(r.X + 1, r.Y + 1 + off, r.Width - 2, r.Height - 2), radius, radius);
    }

    static Rect Inset(Rect r) => new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));

    /// <summary>极坐标取点（画弧线用）。</summary>
    static Point Polar(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180.0;
        return new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
    }

    static void Label(DrawingContext c, string text, Rect r, double size, Brush b, bool bold)
        => Text(c, text, r.X + (r.Width - Measure(text, size)) / 2, r.Y + (r.Height - size * 1.35) / 2, size, b, bold);

    static double Measure(string s, double size)
    {
        var ft = Fmt(s, size, Brushes.Black, false);
        return ft.Width;
    }

    public static void Text(DrawingContext c, string s, double x, double y, double size, Brush b, bool bold = false)
        => c.DrawText(Fmt(s, size, b, bold), new Point(x, y));

    public static FormattedText Fmt(string s, double size, Brush b, bool bold)
        => new FormattedText(s, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                             FlowDirection.LeftToRight,
                             new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                                          FontStyles.Normal,
                                          bold ? FontWeights.SemiBold : FontWeights.Normal,
                                          FontStretches.Normal),
                             size, b, 1.0);

    public static void Icon(DrawingContext c, string name, double cx, double cy, double size, Brush brush)
    {
        string path;
        try { path = IconPaths.Get(name); } catch { return; }
        var geo = Geometry.Parse(path);
        // 外部库的图标自带 viewBox（Material 用的是 0 -960 960 960），按它换算
        double vbW = 24, vbX = 0, vbY = 0;
        if (IconPaths.TryGetBox(name, out var box) && !string.IsNullOrWhiteSpace(box))
        {
            var parts = box.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[0], out var bx) && double.TryParse(parts[1], out var by)
                && double.TryParse(parts[2], out var bw))
            { vbX = bx; vbY = by; vbW = bw; }
        }
        double s = size / vbW;
        c.PushTransform(new TranslateTransform(cx - size / 2, cy - size / 2));
        c.PushTransform(new ScaleTransform(s, s));
        c.PushTransform(new TranslateTransform(-vbX, -vbY));
        c.DrawGeometry(brush, null, geo);
        c.Pop();
        c.Pop();
        c.Pop();
    }

    public static readonly Color Accent = C(0x00, 0x67, 0xC0);
    public static Color C(byte r, byte g, byte b, byte a = 0xFF) => Color.FromArgb(a, r, g, b);
    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}

/// <summary>假面板的全部状态。出图模式就是把这些值摆成不同组合再画一遍。</summary>
internal sealed class PanelState
{
    public double E = 1;         // 0 = 收起成球，1 = 完全展开
    public double Rail;          // 0 = 6 像素的色线，1 = 该工具的设置条
    public double Groove;        // 0 = 滑条常态，1 = 悬停/拖动
    public double Slider01 = 0.45;
    public int Color = 0;
    public int Tool = 1;
    public int HoverTile = -1;
    public int PressTile = -1;
    public int IconScale;        // 0 = 任务栏档（默认）／1 = 前几轮的现状／2 = 更大／3 = 小图标
    public bool Dark;
    public bool ShowDeco = true;
    public bool AutoHide;
    /// <summary>瘦身档：滑条嵌进按钮带下沿，色线与色板各收一点。</summary>
    public bool Slim = true;     // 默认就走瘦身档（按 H 可以切回现状对照）
    /// <summary>极简档：主条只钉"笔 / 橡皮 / 更多"，笔的设置条只给 4 个色块。</summary>
    public bool Mini;
    public int LaserStyle = 2;   // 默认用"自绘：笔＋光束＋落点"（按 L 可换其它候选）
    public int LaserSize = 1;
    public int EraserMode;       // 0 整笔擦 / 1 面积擦
    public int SelectMode;       // 0 矩形框选 / 1 自由套索
    public int ShapeKind;
    public bool CaptureHideInk;
    public bool PassThrough;
    /// <summary>切工具时上带内容的淡入淡出（0 = 正在换，1 = 换好了）。</summary>
    public double ContentFade = 1;
    /// <summary>按住式动作的进度（清空：按住 0.8 秒，走满才执行）。</summary>
    public double ClearHold;
    /// <summary>鼠标是不是停在这个动作按钮上（用来做 hover 反馈）。</summary>
    public int ActionHoverID = -1;
    /// <summary>动作执行后的闪一下（0~1，自己衰减）。</summary>
    public double ActionFlash;
    /// <summary>清空执行后的整块红边（0~1，自己衰减）。</summary>
    public double ClearFlash;
    /// <summary>「更多」抽屉开着没有；以及鼠标停在抽屉里哪一格。</summary>
    public bool MoreOpen;
    public int MoreHover = -1;
    /// <summary>白板开着没有；板色 0 白 / 1 绿 / 2 黑。</summary>
    public bool BoardOn;
    public int BoardColor;
}
