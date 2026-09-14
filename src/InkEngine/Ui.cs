using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 界面接入点。引擎只认这个接口，不认识任何具体界面。
///
/// 设计要点：
/// 1. 界面**不自己占一层画布**。引擎给界面分配一张位图，界面画到那张位图上；
///    只有界面声明"我变了"（<see cref="IUiHost.InvalidateUi"/>）时才重画。
///    这样界面做多花哨（圆角、毛玻璃、动效）都只影响它自己那一次重绘，
///    不会拖慢每一帧的笔迹渲染。
/// 2. 输入先给界面做命中测试，界面返回 true 表示"这次点击我消费了"，
///    引擎就不会把它变成笔画。
/// 3. 界面要改引擎状态，只能通过 <see cref="IUiHost.Commands"/>，
///    不允许直接动文档，否则撤销栈会乱。
/// </summary>
public interface IOverlayUi
{
    /// <summary>调试/统计用的名字。</summary>
    string Name { get; }

    /// <summary>界面是否显示。隐藏时引擎不画它、也不给它输入。</summary>
    bool Visible { get; }

    /// <summary>引擎装配时调用一次，界面从这里拿到命令入口与状态。</summary>
    void Attach(IUiHost host);

    /// <summary>
    /// 布局。屏幕尺寸/DPI 变化时调用。
    /// 界面在这里算出自己占用的矩形并返回（引擎据此做命中测试与缓存分配）。
    /// </summary>
    RectF Layout(RectF screen, float dpiScale);

    /// <summary>
    /// 界面这一刻实际占用的矩形（逻辑屏幕坐标）。
    ///
    /// 和 <see cref="Layout"/> 的区别很重要：Layout 只在尺寸/DPI 变化时调用，
    /// 而界面的占位会随它自己的状态变——悬浮条被拖动、展开调色板、折叠收起、
    /// 或者干脆暂时消失。引擎每帧都问这个方法一次，拿到的矩形用来做命中测试
    /// 和脏区计算。返回空矩形表示"这一刻我不占地方"。
    ///
    /// </summary>
    RectF QueryBounds();

    /// <summary>
    /// 绘制界面内容。坐标系原点 = 界面矩形左上角（引擎已经把画布平移到那里）。
    /// **只在缓存失效时被调用**，界面可以放心在里面做复杂绘制。
    /// </summary>
    void Render(ID2D1DeviceContext ctx, UiTheme theme);

    /// <summary>指针按下。返回 true = 这次输入归界面，引擎不再当笔画处理。</summary>
    bool PointerDown(in UiPointerEvent e);

    /// <summary>指针移动（含悬停）。返回 true 表示界面消费。</summary>
    bool PointerMove(in UiPointerEvent e);

    /// <summary>指针抬起。</summary>
    bool PointerUp(in UiPointerEvent e);

    /// <summary>引擎状态变化（工具/颜色/粗细/撤销深度等），界面据此刷新显示。</summary>
    void OnStateChanged(in UiState state);
}

/// <summary>引擎提供给界面的能力。</summary>
public interface IUiHost
{
    /// <summary>改引擎状态的唯一入口。</summary>
    IEngineCommands Commands { get; }

    /// <summary>引擎当前状态（只读快照）。</summary>
    UiState State { get; }

    /// <summary>屏幕范围（虚拟桌面坐标）。</summary>
    RectF Screen { get; }

    /// <summary>DPI 缩放（逻辑像素 → 物理像素）。</summary>
    float DpiScale { get; }

    /// <summary>
    /// true 表示这是"换回同一个界面"，不是首次接入。界面据此决定要不要重新
    /// 采纳引擎给的屏幕尺寸——重新采纳会把界面摆到按另一套坐标算的位置上。
    /// </summary>
    bool IsReattach { get; }

    /// <summary>
    /// 文字工厂。界面画图标/标签用它创建 <c>IDWriteTextFormat</c>，
    /// 不必自己再建一套 DirectWrite。
    /// </summary>
    IDWriteFactory TextFactory { get; }

    /// <summary>Direct2D 工厂。界面要画自定义几何（路径）时用它创建。</summary>
    ID2D1Factory1 PathFactory { get; }

    /// <summary>引擎的单调时钟（毫秒）。动画与"几秒后自动隐藏"用这个。</summary>
    double NowMs { get; }

    /// <summary>
    /// 新加的笔画数量。界面每帧拿它和自己上次看到的值比一下，就能知道
    /// "画布刚变了"，从而立刻刷新按钮的可用状态。
    ///
    /// 为什么需要它：按钮的灰/亮是靠状态回调驱动的，而状态回调只在"工具、颜色、
    /// 粗细"这些设置变化时触发。老师写完一笔时画布变了、但设置没变，清空按钮
    /// 就一直是灰的，得再点一下别的东西才亮——这是实测发现的 bug。
    /// </summary>
    int DocumentVersion { get; }

    /// <summary>
    /// 告诉引擎"我的外观变了，请重画我的缓存"。
    /// 悬停高亮、按钮按下、数值变化时都要调这个。
    /// **不要每帧调**——那就等于每帧重画界面，等于白做缓存。
    /// </summary>
    void InvalidateUi();
}

/// <summary>界面对引擎的全部操作能力。刻意做窄，防止界面越权。</summary>
public interface IEngineCommands
{
    void SetTool(Tool tool);
    void SetColor(Color4 color);
    void SetWidth(float logicalPx);
    void Undo();
    void Redo();
    void Clear();
    void SetPassThrough(bool on);
    /// <summary>
    /// 白板开关。开=给画布铺一层不透明底色（遮住桌面，像一块白板）；
    /// 关=透明批注，直接写在别的程序上面。
    /// </summary>
    void SetBoard(bool on);
    void Quit();
}

/// <summary>课堂常用色。界面直接拿它画色板，保证多套界面配色一致。</summary>
public static class InkPalette
{
    /// <summary>默认笔色：正红。屏幕上最醒目，也是老师批改最常用的颜色。</summary>
    public static readonly Color4 PenDefault = new(0.95f, 0.18f, 0.18f, 1f);

    /// <summary>默认荧光笔色：黄。</summary>
    public static readonly Color4 HighlighterDefault = new(1f, 0.85f, 0.15f, 0.32f);

    /// <summary>笔的色板。荧光笔用的是同一组基色，只把透明度调低。</summary>
    public static readonly (string Name, Color4 Color)[] Presets =
    {
        ("红", new Color4(0.95f, 0.18f, 0.18f, 1f)),
        ("橙", new Color4(0.98f, 0.55f, 0.09f, 1f)),
        ("黄", new Color4(0.98f, 0.82f, 0.12f, 1f)),
        ("绿", new Color4(0.13f, 0.70f, 0.33f, 1f)),
        ("青", new Color4(0.10f, 0.72f, 0.78f, 1f)),
        ("蓝", new Color4(0.13f, 0.45f, 0.90f, 1f)),
        ("紫", new Color4(0.55f, 0.28f, 0.86f, 1f)),
        ("黑", new Color4(0.11f, 0.12f, 0.15f, 1f)),
        ("白", new Color4(1f, 1f, 1f, 1f)),
    };

    /// <summary>把笔色换算成荧光笔色：保留色相，降低不透明度。</summary>
    public static Color4 ToHighlighter(Color4 pen) => new(pen.R, pen.G, pen.B, 0.32f);
}

/// <summary>引擎状态的只读快照，界面拿来显示。</summary>
public readonly struct UiState
{
    public UiState(Tool tool, Color4 color, Color4 paletteBase, float width,
                   bool passThrough, bool board, int undoDepth, int redoDepth, int strokeCount)
    {
        Tool = tool;
        Color = color;
        PaletteBase = paletteBase;
        Width = width;
        PassThrough = passThrough;
        Board = board;
        UndoDepth = undoDepth;
        RedoDepth = redoDepth;
        StrokeCount = strokeCount;
    }

    public Tool Tool { get; }
    /// <summary>当前工具实际用的颜色（荧光笔是半透明的）。</summary>
    public Color4 Color { get; }
    /// <summary>
    /// 当前颜色的"基色"，永远是不透明的。荧光笔的色相与它一致，
    /// 界面的色板用它来判定"选中的是哪个色块"。
    /// </summary>
    public Color4 PaletteBase { get; }
    public float Width { get; }
    public bool PassThrough { get; }
    /// <summary>是否处于白板模式（画布有不透明底色）。</summary>
    public bool Board { get; }
    /// <summary>可撤销步数——界面的"撤销"按钮据此变灰。</summary>
    public int UndoDepth { get; }
    public int RedoDepth { get; }
    public int StrokeCount { get; }
}

/// <summary>
/// 给界面的指针事件。坐标是**逻辑屏幕坐标**（物理坐标 ÷ DPI 缩放），
/// 和 <see cref="IUiHost.Screen"/>、<see cref="IOverlayUi.Layout"/> 同一套坐标系，
/// 界面不用关心 DPI。
/// </summary>
public readonly struct UiPointerEvent
{
    public UiPointerEvent(float x, float y, float pressure, bool fromPen, bool isEraserTip)
    {
        X = x; Y = y; Pressure = pressure; FromPen = fromPen; IsEraserTip = isEraserTip;
    }

    public float X { get; }
    public float Y { get; }
    public float Pressure { get; }
    public bool FromPen { get; }
    public bool IsEraserTip { get; }
}

/// <summary>引擎给界面的配色。界面应当只用这些颜色，保证多套界面观感一致。</summary>
public readonly struct UiTheme
{
    public UiTheme(Color4 panel, Color4 panelBorder, Color4 text, Color4 textMuted,
                   Color4 hover, Color4 activeBg, Color4 activeText, float cornerRadius)
    {
        Panel = panel; PanelBorder = panelBorder; Text = text; TextMuted = textMuted;
        Hover = hover; ActiveBg = activeBg; ActiveText = activeText; CornerRadius = cornerRadius;
    }

    public Color4 Panel { get; }
    public Color4 PanelBorder { get; }
    public Color4 Text { get; }
    public Color4 TextMuted { get; }
    public Color4 Hover { get; }
    public Color4 ActiveBg { get; }
    public Color4 ActiveText { get; }
    public float CornerRadius { get; }

    /// <summary>默认主题（浅色悬浮条，和教室投影的观感搭配）。</summary>
    public static UiTheme Default => new(
        panel: new Color4(0.98f, 0.98f, 0.99f, 0.94f),
        panelBorder: new Color4(0.75f, 0.78f, 0.84f, 0.9f),
        text: new Color4(0.16f, 0.18f, 0.22f, 1f),
        textMuted: new Color4(0.45f, 0.48f, 0.54f, 1f),
        hover: new Color4(0.90f, 0.93f, 0.98f, 1f),
        activeBg: new Color4(0.13f, 0.45f, 0.85f, 1f),
        activeText: new Color4(1f, 1f, 1f, 1f),
        cornerRadius: 10f);
}

/// <summary>不显示任何界面的空实现。切边界时用它验证"引擎不依赖界面"。</summary>
public sealed class NullUi : IOverlayUi
{
    public string Name => "（无界面）";
    public bool Visible => false;
    public void Attach(IUiHost host) { }
    public RectF Layout(RectF screen, float dpiScale) => RectF.Empty;
    public RectF QueryBounds() => RectF.Empty;
    public void Render(ID2D1DeviceContext ctx, UiTheme theme) { }
    public bool PointerDown(in UiPointerEvent e) => false;
    public bool PointerMove(in UiPointerEvent e) => false;
    public bool PointerUp(in UiPointerEvent e) => false;
    public void OnStateChanged(in UiState state) { }
}
