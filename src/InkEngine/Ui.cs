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

    /// <summary>
    /// 界面这一刻有没有动画在跑。引擎每帧问一次（和 <see cref="QueryBounds"/> 同一套路）：
    /// 只要有一处返回 true，引擎就持续出帧，直到动画结束自动归零。
    ///
    /// 为什么必须有这个出口：引擎只在"有脏区或有东西在动"时才渲染一帧，
    /// 而"在动"原来只算激光、正在书写、复制闪一下。界面在 <see cref="Render"/>
    /// 里调 <see cref="IUiHost.InvalidateUi"/> 设的那点脏，会被同一帧末尾的
    /// `_dirty = false` 抹掉——**展开动画会停在第一帧**。
    ///
    /// 规矩：动画期间返回 true，**结束时必须自己变成 false**，
    /// 否则空闲时也会一直出帧（那是笔记本电池最恨的一种 bug）。
    /// </summary>
    bool IsAnimating { get; }

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

    /// <summary>
    /// 指针离开了界面这一块。
    ///
    /// 为什么必须有这个出口：引擎只会把**落在界面矩形内**的移动转给界面，
    /// 所以"指针什么时候走了"界面自己是算不出来的。而这件事有真实后果——
    /// 贴边隐藏要"移开一会儿才收"、悬停展开要收回去，都靠它。
    /// </summary>
    void PointerLeave();

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

    /// <summary>
    /// 读一条**界面自己的**偏好（深色主题、贴边隐藏、档位、钉住……）。
    /// 返回 null = 没有存过，界面用自己的默认值。
    ///
    /// 为什么放在引擎这边：配置文件只有一个，读写规则（只写差异、坏了不影响启动）
    /// 已经写在 <see cref="InkSettings"/> 里了，界面不该再自己实现一遍。
    /// **引擎只当仓库**：它不认识这些键的含义，界面说存什么就存什么。
    /// </summary>
    string GetPref(string key);

    /// <summary>
    /// 记一条界面偏好。传 null = 回到默认（引擎会把这一项删掉，不会写进配置文件）。
    /// </summary>
    void SetPref(string key, string value);
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

    /// <summary>白板的底色（白/绿/黑）。换底色会整层重画，和开关同理。</summary>
    void SetBoardColor(Color4 color);

    /// <summary>
    /// 白板底纹：<paramref name="pattern"/> 0 = 无、1 = 方格、2 = 横线；
    /// <paramref name="stepLogical"/> 是间距（逻辑像素，8～240）。
    /// 和板色一样，它是**画进分块缓存**的：换一次整层重铺一次，平时零开销。
    /// </summary>
    void SetBoardPattern(int pattern, float stepLogical);

    /// <summary>
    /// 截图模式：<paramref name="hideInk"/> true = **隐藏批注截取**（抓之前把覆盖层藏起来，
    /// 拍到的只有下层内容）；false = **直接截取**（连板书一起拍，只把取景框藏掉）。
    /// 参考 InkClass 的两项菜单（快速截图 / 隐藏界面截图）。
    /// </summary>
    void SetCaptureHideInk(bool hideInk);

    /// <summary>
    /// **白板的不透明度**（0.35～1，1 = 实心）。调小 → 下面的题目隐约透出来，
    /// 而**笔迹照样是实的**（半透明只作用于底色那一层）。
    /// </summary>
    void SetBoardOpacity(float opacity);

    /// <summary>框选工具下的选择方式：矩形框（碰到墨就选中）／自由套索（圈住 80% 才选中）。</summary>
    void SetSelectMode(SelectMode mode);

    /// <summary>全选（引擎会顺手把工具切到框选，免得用户以为没生效）。</summary>
    void SelectAll();

    /// <summary>
    /// 整屏翻页：<paramref name="down"/> = 往下翻一屏。
    /// "一屏 = 一页"——相机正好走一个视口高，翻完屏幕上不留半行字；
    /// 往下永远翻得动（画布下面永远多一屏），到顶了往上翻就不动。
    /// </summary>
    void FlipPage(bool down);

    /// <summary>
    /// 重启软件。**先把板书暂存**，再拉起新进程、退出自己，新进程启动时读回来。
    /// 和"界面崩了自动重启"走的是同一条路（见 Recovery）：重启的前提是不丢东西。
    /// 教室里没有键盘的机器上，这是"感觉不对就重开一次"的唯一入口。
    /// </summary>
    void Restart();

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

    /// <summary>
    /// 白板的三种底色。老师实际就这三种用法：白板讲课、绿板（像传统黑板）、
    /// 黑板（投影暗的时候不刺眼）。界面直接拿它画那三格。
    /// </summary>
    public static readonly (string Name, Color4 Color)[] BoardPresets =
    {
        ("白板", new Color4(0.99f, 0.99f, 0.98f, 1f)),
        ("绿板", new Color4(0.13f, 0.36f, 0.24f, 1f)),
        ("黑板", new Color4(0.10f, 0.11f, 0.13f, 1f)),
    };

    /// <summary>
    /// **选中框属性面板**的色板（2026-09-16 加）：4 列排布 —— 中性一行、
    /// 暖色一行、冷色一行、末格"自定义取色"（占位，本轮禁用）。
    ///
    /// 为什么不直接用上面的 <see cref="Presets"/>：那 9 色是上带 / 假面板在用的，
    /// 改它会把假面板的色片布局一起牵动。这里独立一张，互不影响。
    /// </summary>
    public static readonly (string Name, Color4 Color)[] SelectionSwatches =
    {
        ("白",   new Color4(1.00f, 1.00f, 1.00f, 1f)),
        ("浅灰", new Color4(0.72f, 0.75f, 0.78f, 1f)),
        ("深灰", new Color4(0.36f, 0.38f, 0.42f, 1f)),
        ("黑",   new Color4(0.11f, 0.12f, 0.15f, 1f)),
        ("红",   new Color4(0.95f, 0.18f, 0.18f, 1f)),
        ("橙",   new Color4(0.98f, 0.55f, 0.09f, 1f)),
        ("黄",   new Color4(0.98f, 0.82f, 0.12f, 1f)),
        ("浅绿", new Color4(0.55f, 0.85f, 0.30f, 1f)),
        ("青",   new Color4(0.10f, 0.72f, 0.78f, 1f)),
        ("蓝",   new Color4(0.13f, 0.45f, 0.90f, 1f)),
        ("紫",   new Color4(0.55f, 0.28f, 0.86f, 1f)),
        ("粉",   new Color4(0.98f, 0.55f, 0.68f, 1f)),
        ("自定义", new Color4(0f, 0f, 0f, 0f)),      // 占位：要接系统取色器，下一批
    };
}

/// <summary>
/// 引擎状态的只读快照，界面拿来显示。
///
/// 用 init 属性而不是十几个位置参数：这个快照只会越加越多（每加一个界面要显示的东西
/// 就多一项），位置参数到了十来个以后，"谁在第几位"就是纯粹的踩雷。
/// 构造点只有引擎里那一处（<c>SnapshotState</c>）。
/// </summary>
public readonly struct UiState
{
    public Tool Tool { get; init; }
    /// <summary>当前工具实际用的颜色（荧光笔是半透明的）。</summary>
    public Color4 Color { get; init; }
    /// <summary>
    /// 当前颜色的"基色"，永远是不透明的。荧光笔的色相与它一致，
    /// 界面的色板用它来判定"选中的是哪个色块"。
    /// </summary>
    public Color4 PaletteBase { get; init; }
    /// <summary>当前工具的宽度（笔/荧光笔/激光各记各的）。</summary>
    public float Width { get; init; }
    /// <summary>下面这几个是"按工具取"的原始值：滑块要用它们，否则切工具时会跳。</summary>
    public float PenWidth { get; init; }
    public float HighlighterWidth { get; init; }
    public Color4 HighlighterColor { get; init; }
    public float LaserWidth { get; init; }
    public bool PassThrough { get; init; }
    /// <summary>是否处于白板模式（画布有不透明底色）。</summary>
    public bool Board { get; init; }
    /// <summary>白板底色（界面用它高亮"现在是哪种板"）。</summary>
    public Color4 BoardColor { get; init; }
    /// <summary>白板底纹：0 = 无，1 = 方格，2 = 横线（界面用它高亮当前那一档）。</summary>
    public int BoardPattern { get; init; }
    /// <summary>底纹间距（逻辑像素）。</summary>
    public float BoardPatternStep { get; init; }
    /// <summary>白板的不透明度（1 = 实心；调小能隐约看见下面的题目）。</summary>
    public float BoardOpacity { get; init; }
    /// <summary>截图模式：true = 隐藏批注截取（只有下层内容），false = 直接截取（连板书一起拍）。</summary>
    public bool CaptureHideInk { get; init; }
    /// <summary>框选的选择方式（界面用它高亮"矩形/套索"那一格）。</summary>
    public SelectMode SelectMode { get; init; }
    /// <summary>现在在第几屏（1 起）。界面用它显示"第 N 屏"。</summary>
    public int ScreenIndex { get; init; }
    /// <summary>还能不能往上翻（到顶了就不行）。"下一屏"永远可用。</summary>
    public bool CanFlipPageUp { get; init; }
    /// <summary>
    /// 老师这一刻是不是正在写。界面用它判断"别在人家写字的时候动界面"——
    /// 比如贴边隐藏：手正在写，界面突然收起来或者浮出来，都会打断。
    /// </summary>
    public bool IsDrawing { get; init; }
    /// <summary>可撤销步数——界面的"撤销"按钮据此变灰。</summary>
    public int UndoDepth { get; init; }
    public int RedoDepth { get; init; }
    public int StrokeCount { get; init; }
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
    public bool IsAnimating => false;
    public void Attach(IUiHost host) { }
    public RectF Layout(RectF screen, float dpiScale) => RectF.Empty;
    public RectF QueryBounds() => RectF.Empty;
    public void Render(ID2D1DeviceContext ctx, UiTheme theme) { }
    public bool PointerDown(in UiPointerEvent e) => false;
    public bool PointerMove(in UiPointerEvent e) => false;
    public void PointerLeave() { }
    public bool PointerUp(in UiPointerEvent e) => false;
    public void OnStateChanged(in UiState state) { }
}
