using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 设计令牌：**界面里所有尺寸、颜色、时长只在这一个文件里**。
///
/// 出处是 [调研-界面设计与框架选型.md] 第三节 + 后面几轮的修正
/// （高度\宽度\档位\上下文设置条）。数字后面那行注释写的是"凭什么这么定"，
/// 改数字之前先看一眼理由——这些不是随手调的。
///
/// 单位一律是**逻辑像素**（引擎负责乘 DPI，界面自己永远不碰物理像素）。
/// </summary>
internal static class Tokens
{
    // ---- 网格与尺寸 ---------------------------------------------------------

    /// <summary>所有间距都是 4 的倍数（对齐系统控件的节奏）。</summary>
    public const float Grid = 4f;

    /// <summary>展开后按钮带的高。＝ 按钮 40 ＋ 上下各 4（任务栏档实测比例）。</summary>
    public const float BarHeight = 48f;

    /// <summary>按钮命中区。WinUI 的触控目标是 40，鼠标当然够。</summary>
    public const float Button = 40f;

    /// <summary>图标。24 是 Fluent 的原生网格，不用缩放就不显小也不糊。</summary>
    public const float Icon = 24f;

    /// <summary>组内间隙：同组要"挨着"。</summary>
    public const float GapInGroup = 4f;

    /// <summary>组分隔：8 + 1px 线 + 8 = 17。组间要明显大于组内，眼睛才分得开块。</summary>
    public const float GroupDivider = 17f;

    /// <summary>条两端的内边距：胶囊首尾要透气。</summary>
    public const float BarPad = 8f;

    /// <summary>收起态那个球。48 是"点得到的最小东西"，触摸屏上也够。</summary>
    public const float Ball = 48f;
    public const float BallHover = 52f;
    public const float BallPress = 46f;

    /// <summary>浮着的时候离屏幕边至少留这么多（投影裁切 + 视觉呼吸）。</summary>
    public const float EdgeMargin = 12f;

    /// <summary>贴边之后几乎贴着屏幕（不是 12——12 是"浮着"的留白）。</summary>
    public const float DockGap = 2f;

    /// <summary>拖到离边 40 以内就吸附。</summary>
    public const float SnapDistance = 40f;

    /// <summary>拖动的判定阈值：超过它才算"拖动"，否则算"点了一下"。</summary>
    public const float DragThreshold = 4f;

    /// <summary>圆角一律"高 ÷ 2"：球→条是一次连续变形，不会先变圆角再变长。</summary>
    public static float PillRadius(float height) => height * 0.5f;

    /// <summary>
    /// 上带（当前工具的设置条）的高。色片 26 ＋ 上下各 4。
    /// 它**不做成胶囊**：两块叠起来用大圆角会形成"两段弧"，很难看
    /// （设计稿 ③ 那一叠专门看过），所以上带是圆角 12 的方片。
    /// </summary>
    public const float BandHeight = 34f;
    public const float BandRadius = 12f;

    /// <summary>色片：26 的方块，缝 6。缝是必须的——挨在一起会糊成一条彩带。</summary>
    public const float Swatch = 26f;
    public const float SwatchGap = 6f;

    /// <summary>分段选择框（整笔/面积、矩形/套索、小/中/大）的高与内边距。</summary>
    public const float SegmentHeight = 26f;
    public const float SegmentPad = 12f;

    /// <summary>滑条：轨道 4 高、滑钮 16（系统的滑块就是这个量级）。</summary>
    public const float SliderTrack = 4f;
    public const float SliderKnob = 16f;
    public const float SliderWidth = 150f;

    // ---- 动效 ---------------------------------------------------------------

    /// <summary>展开 200 ms、收起 150 ms（收起要比展开快：用户已经决定了）。</summary>
    public const double ExpandMs = 200;
    public const double CollapseMs = 150;

    /// <summary>悬停/按下的反馈时长。</summary>
    public const double HoverMs = 80;

    /// <summary>贴边吸附的时长（与展开同一套曲线，保持一致）。</summary>
    public const double SnapMs = 167;

    // ---- 颜色 ---------------------------------------------------------------

    /// <summary>面板底：白 80%。"微微能看到下面"，又不让图标被背景花色吃掉。</summary>
    public static readonly Color4 PanelLight = new(1f, 1f, 1f, 0.80f);
    public static readonly Color4 PanelDark = new(0.125f, 0.125f, 0.125f, 0.80f);

    /// <summary>
    /// 1px 描边。**它比透明度更影响"立不立得住"**：没有这道边，圆角会糊进背景里。
    /// </summary>
    public static readonly Color4 BorderLight = new(0f, 0f, 0f, 0.10f);
    public static readonly Color4 BorderDark = new(1f, 1f, 1f, 0.12f);

    /// <summary>图标：#1B1B1F。白 80% 底上压到彩色背景，最差也守得住 3:1。</summary>
    public static readonly Color4 InkLight = new(0.106f, 0.106f, 0.122f, 1f);
    public static readonly Color4 InkDark = new(0.949f, 0.949f, 0.949f, 1f);

    /// <summary>当前工具：不透明强调色底 + 白图标（微软规范：毛玻璃上别放强调色文字）。</summary>
    public static readonly Color4 Accent = new(0f, 0.404f, 0.753f, 1f);
    public static readonly Color4 AccentInk = new(1f, 1f, 1f, 1f);

    /// <summary>悬停底：黑 7%（系统 SubtleFill 的量级）。</summary>
    public static readonly Color4 HoverLight = new(0f, 0f, 0f, 0.07f);
    public static readonly Color4 HoverDark = new(1f, 1f, 1f, 0.08f);

    /// <summary>投影用"同形状往下叠 2 层、逐层变淡"近似（真模糊贵一个量级）。</summary>
    public static readonly Color4 Shadow1 = new(0f, 0f, 0f, 0.10f);
    public static readonly Color4 Shadow2 = new(0f, 0f, 0f, 0.06f);

    // ---- 调色板 -------------------------------------------------------------

    /// <summary>
    /// 笔/荧光笔的 12 色（满饱和，别往白里混——混过的版本被用户否过一次）。
    /// 顺序：中性三色 + 暖 + 冷，色板排两行时好看。
    /// </summary>
    public static readonly (string Name, Color4 Color)[] Palette =
    {
        ("黑", new Color4(0.11f, 0.12f, 0.15f, 1f)),
        ("灰", new Color4(0.45f, 0.48f, 0.52f, 1f)),
        ("白", new Color4(1.00f, 1.00f, 1.00f, 1f)),
        ("红", new Color4(0.95f, 0.18f, 0.18f, 1f)),
        ("橙", new Color4(0.98f, 0.55f, 0.09f, 1f)),
        ("黄", new Color4(0.98f, 0.82f, 0.12f, 1f)),
        ("绿", new Color4(0.13f, 0.70f, 0.33f, 1f)),
        ("青", new Color4(0.10f, 0.72f, 0.78f, 1f)),
        ("蓝", new Color4(0.13f, 0.45f, 0.90f, 1f)),
        ("紫", new Color4(0.55f, 0.28f, 0.86f, 1f)),
        ("粉", new Color4(0.98f, 0.55f, 0.68f, 1f)),
        ("棕", new Color4(0.55f, 0.36f, 0.22f, 1f)),
    };
}
