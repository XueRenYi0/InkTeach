using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>框选工具下拖空白处的选择方式（`Ctrl+Alt+9` 切）。见 InkEngine.SelMode。</summary>
/// <summary>
/// 框选工具下的两种选择方式。<b>public</b>：它是界面契约的一部分
/// （界面要能"切成套索"，见 <see cref="IEngineCommands.SetSelectMode"/>）。
/// </summary>
public enum SelectMode
{
    /// <summary>矩形框选：碰到墨就算选中（OneNote 的语义）。</summary>
    Rect = 0,
    /// <summary>自由套索：代表点 80% 落在圈里才算选中（WPF 的语义）。</summary>
    Lasso = 1,
}

internal enum PassThroughMode
{
    /// <summary>WM_NCHITTEST -> HTTRANSPARENT only (safe, but the documented
    /// hand-off only reaches windows owned by the same thread).</summary>
    HitTest = 0,

    /// <summary>Adds WS_EX_TRANSPARENT to the window style on top of HitTest.</summary>
    ExTransparent = 1,

    /// <summary>Adds WS_EX_LAYERED | WS_EX_TRANSPARENT (the classic click-through
    /// recipe). Kept as a probe because it may disturb DirectComposition.</summary>
    LayeredTransparent = 2,
}

public class InkEngine
{
    // ---- document / interaction state ------------------------------------
    internal readonly InkDocument Doc = new();
    internal readonly LaserTrail Laser = new();

    /// <summary>
    /// 界面自己的偏好（深色主题、贴边隐藏、档位、钉住）。引擎**只存不解释**：
    /// 它不知道"极简档"是什么，界面说存什么就存什么。落盘在 settings.json 的 `ui` 段。
    /// </summary>
    internal readonly Dictionary<string, string> UiPrefs = new();
    private bool _uiPrefsDirty;

    /// <summary>自检/基准模式：**不读也不写**用户配置（判据要确定，更不能改用户的设置）。</summary>
    internal bool SelfCheckMode;

    // ---- 自动存档（崩溃恢复）------------------------------------------------
    //
    // 产品里**没有"保存"这个动作**，所以按"持久画布"来做：每 15 秒（板书变了才写）
    // 存一份到 %LOCALAPPDATA%，下次打开自动接上。
    // 为什么值得：一节课的板书丢了是最坏的失败模式；同类开源软件（Xournal++）
    // 正因为"崩溃就丢"被用户提了严重数据丢失的 issue。

    /// <summary>自动存档的间隔（毫秒）。板书没变就不写。</summary>
    private double _autoSaveEveryMs = 15000;
    private double _nextAutoSaveAtMs;
    private long _autoSavedVersion = -1;

    /// <summary>自检用：这一场跑下来自动存档写了多少次（验证"没变就不写"）。</summary>
    internal int AutoSaveCount { get; private set; }

    /// <summary>自检用：把自动存档间隔调短（不然得等 15 秒才验得到节流）。</summary>
    internal void SetAutoSaveIntervalForTest(double ms)
    {
        _autoSaveEveryMs = ms;
        _nextAutoSaveAtMs = 0;
    }

    /// <summary>自检用：走一遍"启动时接上上次板书"这条真路径（含坏文件容错）。</summary>
    internal void RestoreAutoSaveForTest() => RestoreAutoSaveIfAny();

    /// <summary>
    /// **启动时要不要接上上次的板书**（`ui` 段里的 `restoreInk`）。**默认关**。
    ///
    /// 用户 2026-09-17："退出以后再打开不用恢复墨迹吧……后期可以设置，但是我觉得
    /// 默认不恢复墨迹。" 理由写在 <see cref="RestoreAutoSaveIfAny"/> 里：教室机器是
    /// 公用的，一开机就铺满上一节课的板书不合理。
    ///
    /// 它同时管**写**：不读的东西不必写（见 <see cref="MaybeAutoSave"/>）。
    /// </summary>
    internal bool RestoreInkOnStartup => GetUiPref("restoreInk") == "1";

    /// <summary>自检用：立刻按当前文档写一次自动存档（不看间隔）。</summary>
    internal void AutoSaveNow()
    {
        try { Recovery.SaveAuto(InkSerializer.Save(Doc)); }
        catch (Exception ex) { Console.WriteLine("自动存档失败：" + ex.Message); }
        _autoSavedVersion = Doc.Version;
        AutoSaveCount++;
    }

    /// <summary>
    /// 到了间隔、而且板书真的变了，就写一次。
    /// 判据用文档版本号（`Doc.Version`）——它在每次增删改时都会加一，
    /// 比"每 15 秒无条件写一遍"省得多（一万笔的全量序列化不是白给的）。
    ///
    /// **偏好关着就一次都不写**：默认档不读那个文件（见 <see cref="RestoreAutoSaveIfAny"/>），
    /// 写一个没人读的文件只是白白序列化 + 在用户的盘上留一份板书。
    /// </summary>
    private void MaybeAutoSave()
    {
        if (!RestoreInkOnStartup) return;
        if (NowMs < _nextAutoSaveAtMs) return;
        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;
        if (Doc.Version == _autoSavedVersion) return;
        AutoSaveNow();
    }
    internal Stroke ActiveStroke;
    internal Tool Tool = Tool.Pen;
    /// <summary>Tool sizes are authored in logical pixels and scaled by the
    /// monitor DPI at use. Without this everything looks half-size on a 150%
    /// display, which is exactly how a 18px eraser turns into an unusable dot.</summary>
    internal float DpiScale = 1f;

    internal float EraserRadiusLogical = 22f;
    /// <summary>
    /// 整笔橡皮的落点半径（逻辑像素）的档位。**以前它根本没有档位**——
    /// 界面上给橡皮画了粗细滑条，可引擎里 `SetWidthFromUi` 把它归到"其它"那一支，
    /// 结果改的是**笔宽**：老师拖橡皮的滑条，笔迹粗细悄悄变了、橡皮一点没变。
    /// 现在四种工具各记各的，橡皮也有自己的三档。
    /// </summary>
    internal static readonly float[] EraserRadiusPresets = { 12f, 22f, 34f };
    internal int EraserRadiusIndex = 1;
    /// <summary>两种橡皮各自的可调范围（逻辑像素）。界面滑条的范围要和这里一致。</summary>
    internal const float EraserRadiusMin = 8f, EraserRadiusMax = 48f;
    internal const float PixelEraserMinWidth = 30f, PixelEraserMaxWidth = 160f;
    internal float PenWidthLogical = 3f;
    internal float HighlighterWidthLogical = 18f;
    /// <summary>
    /// 激光笔的粗细。以前激光借的是**笔宽**（`Engine.cs` 里那条
    /// `tool == Highlighter ? Highlighter : Pen`），于是"切粗细"对激光没意义、
    /// 落点的点大小也没法跟笔迹对上。现在四种工具各记各的。
    /// </summary>
    internal float LaserWidthLogical = 4f;
    internal float EraserRadius => EraserRadiusLogical * DpiScale;
    private float _lastEraseX, _lastEraseY;

    /// <summary>
    /// 像素橡皮的落点尺寸（逻辑像素）：**竖着的黄金比例矩形**，高 : 宽 = 1.618。
    ///
    /// 默认宽 93 × 高 150：用户实测的用法是"从上往下抹一段"（一列板书、一个竖排的字），
    /// 不是横扫一行，所以竖边比横边长。Ctrl+Alt+6 在这里的几档之间切（档位同样守黄金比）。
    /// </summary>
    internal const float GoldenRatio = 1.618f;
    internal float PixelEraserWidthLogical = 93f;
    internal static readonly float[] PixelEraserWidthPresets = { 46f, 93f, 150f };
    internal int PixelEraserWidthIndex = 1;
    internal float PixelEraserHeightLogical => PixelEraserWidthLogical * GoldenRatio;
    internal float PixelEraserHalfWidthPx => PixelEraserWidthLogical * 0.5f * DpiScale;
    internal float PixelEraserHalfHeightPx => PixelEraserHeightLogical * 0.5f * DpiScale;

    /// <summary>截图工具的拖动矩形（画布坐标）。</summary>
    internal bool CaptureActive;
    internal float CapMinX, CapMinY, CapMaxX, CapMaxY;

    /// <summary>
    /// 截图模式（用户 2026-09-17 要的"对接"，参考 InkClass 的两项菜单）：
    ///   · **true = 隐藏界面截取**（默认，也是原来的行为）：抓之前把整个覆盖层藏起来，
    ///     拍到的只有下层内容——**批注、白板、面板一起藏**，一概不入镜。
    ///     （用户 2026-09-17 更正过叫法：原来写"隐藏批注截取"，
    ///      但它其实连白板和面板一起藏，所以界面上把这档叫**"隐藏界面"**。）
    ///   · **false = 直接截取**：**连板书一起拍**（老师想把"PPT + 我写的批注"一起给别人，
    ///     或者把自己写的解题过程做成一张图）。
    ///
    /// 直接截取时**取景框本身**还是要藏掉（不然那个琥珀色框会拍进图里）——
    /// 见 <see cref="CaptureFrameHidden"/>。
    /// </summary>
    internal bool CaptureHideInk = true;

    /// <summary>
    /// 这一帧不画"我们自己盖在画面上的东西"——取景框**和落点光标**（只在"直接截取"
    /// 抓屏的那一瞬为真）。光标也得藏：它是琥珀色的，留着就会被拍进图里。
    /// </summary>
    internal bool CaptureFrameHidden;

    // ---- 白板底纹（方格 / 横线）--------------------------------------------
    //
    // 用户 2026-09-17 要的："给白板增加网格和横线功能，可以参[考]inkclass 的实现"。
    // InkClass 那边（`MW_WhiteboardPattern.cs`）的做法与取舍：
    //   · 三档：**无 / 方格 / 横线**；
    //   · **不是笔迹**——"橡皮擦不掉、撤销不涉及、选择选不中、换页共用"；
    //   · 线细 0.5 逻辑像素（辅助参考线，纤细不抢视觉）；线色随板面明暗自适应
    //     （浅板：灰 60%；深板：白 20%）；
    //   · 间距可调（它给的是 16～240 的滑块）。
    //
    // 我们这里更进一步：底纹和板色一起**画进分块缓存**（见 Overlay.DrawBoardPattern），
    // 所以滚动、翻页、写字都不花额外的钱；代价只有"换底纹/换板色时整层重铺一次"，
    // 和换板色本来就是同一件事。

    /// <summary>白板底纹：0 = 无，1 = 方格，2 = 横线。</summary>
    internal int BoardPattern;

    /// <summary>
    /// **白板的不透明度**（用户 2026-09-17："增加一个透明度的拖动功能，这样可以批注的时候
    /// 隐约看见下面的题目，但是书写有干净"）。
    ///
    /// 1 = 实心板面（现在这样）；调小 → 板面半透明，下面的 PPT / 题目隐约透出来。
    /// **关键是"书写干净"**：半透明只作用于**底色**——底色是画进分块缓存的第一层，
    /// 笔迹画在它上面、本身不透明，所以字照样是实的（自检里专门验这一条）。
    /// </summary>
    internal float BoardOpacity = 1f;
    /// <summary>可调范围：0.35 再低就看不清自己写的字了，1.0 = 实心。</summary>
    internal const float BoardOpacityMin = 0.35f, BoardOpacityMax = 1f;

    internal void SetBoardOpacityFromUi(float v)
    {
        v = Math.Clamp(v, BoardOpacityMin, BoardOpacityMax);
        if (MathF.Abs(BoardOpacity - v) < 0.005f) return;
        BoardOpacity = v;
        // 和换板色 / 换底纹一样：底色是画进分块缓存的，所以所有块都过期了
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }
    /// <summary>底纹间距（逻辑像素）。InkClass 的范围是 16～240，我们按逻辑像素存。</summary>
    internal float BoardPatternStepLogical = 40f;

    /// <summary>底纹间距（画布像素 = 物理像素）。</summary>
    internal float BoardPatternStepPx => Math.Clamp(BoardPatternStepLogical, 8f, 240f) * DpiScale;

    internal void SetBoardPatternFromUi(int pattern, float stepLogical)
    {
        pattern = Math.Clamp(pattern, 0, 2);
        stepLogical = Math.Clamp(stepLogical, 8f, 240f);
        if (BoardPattern == pattern && MathF.Abs(BoardPatternStepLogical - stepLogical) < 0.01f) return;
        BoardPattern = pattern;
        BoardPatternStepLogical = stepLogical;
        // 和换板色一样：底纹是**画进分块缓存**的，所以所有块都过期了
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetCaptureHideInkFromUi(bool hideInk)
    {
        if (CaptureHideInk == hideInk) return;
        CaptureHideInk = hideInk;
        NotifyUiStateChanged();
    }

    /// <summary>Pen width presets, in logical pixels. Cycled with Ctrl+Alt+W
    /// until there is a proper on-screen control for it.</summary>
    internal static readonly float[] WidthPresets = { 1.5f, 3f, 6f, 10f, 16f, 24f };
    internal int WidthPresetIndex = 1;

    /// <summary>
    /// 每种工具各自的粗细档位。**笔和荧光笔的档位不是一回事**：荧光笔是"涂一大条"，
    /// 1.5 像素这种档位对它没意义；激光更小。共用一张表的结果就是
    /// "选了荧光笔按 Ctrl+Alt+6 没反应"（它改的是笔宽）——实测就是这个 bug。
    /// </summary>
    internal static readonly float[] HighlighterWidthPresets = { 8f, 18f, 32f };
    internal static readonly float[] LaserWidthPresets = { 4f, 8f, 14f };
    internal int HighlighterWidthIndex = 1;
    internal int LaserWidthIndex = 0;

    /// <summary>当前工具的粗细（逻辑像素）。落点反馈、界面状态都读它。</summary>
    internal float CurrentToolWidthLogical => Tool switch
    {
        Tool.Highlighter => HighlighterWidthLogical,
        Tool.Laser => LaserWidthLogical,
        Tool.PixelEraser => PixelEraserWidthLogical,
        Tool.Eraser => EraserRadiusLogical,
        _ => PenWidthLogical,
    };

    internal bool PointerInside;
    internal float PointerX, PointerY;
    /// <summary>
    /// 最后一次指针消息来自什么设备（鼠标 / 笔 / 触摸）。**悬停的移动也要更新**——
    /// 指针形状直接取决于它，见 <see cref="ComputeCursorKind"/>。
    /// </summary>
    internal uint LastPointerType = Native.PT_MOUSE;
    internal bool ShowHud = true;

    /// <summary>
    /// 橡皮的实测采集（"手测台" --eraserlab）。**平时是 null**，所有上报点都是 `?.`，
    /// 正常使用一分钱开销都不多花。见 <see cref="EraserTelemetry"/>。
    /// </summary>
    internal EraserTelemetry EraserTelemetry;

    /// <summary>
    /// **复制拖拽模式**：点了操作条的复制按钮之后进入，此时按住选中内容拖动 = **拖出一份副本**
    /// （原件不动），可以连着拖多份；再点按钮 / 点空白 / 换工具退出。
    ///
    /// 语义抄 InkClass（那边已经踩过一轮坑）：点图标**只进模式**，不是"点一下原地克隆一份"
    /// ——后者副本固定偏移 24px、落点不可控，已经废弃。
    /// </summary>
    internal bool CopyDragArmed;

    /// <summary>复制拖拽这一次拖动里"插入副本"那条待提交的动作（松手时和位移合成一步）。</summary>
    private AddStrokesAction _pendingClone;

    /// <summary>
    /// 橡皮的落点反馈之外，**同时显示系统箭头**（默认藏起来，只画我们自己的圈/方块）。
    ///
    /// 留这个开关是为了做 A/B：自绘的方块光标本质是"我们画的一帧"，天生比系统光标晚
    /// 一个刷新周期（16.7 ms）；系统箭头画在硬件光标平面上，几乎没有延迟。用户说
    /// "不跟手"时，把两个都显示出来——箭头跟手、方块形状准，哪种更舒服由用户说了算。
    /// </summary>
    internal bool EraserKeepsSystemCursor;
    internal string HudText = "";
    internal bool MarqueeActive;

    /// <summary>
    /// 框选工具下拖空白处时的**选择方式**：矩形框 ↔ 自由套索（`Ctrl+Alt+9` 切）。
    ///
    /// 为什么不给界面按钮：工具条上已经挤了六种工具，而"选择方式"是个**低频设置**
    /// （一节课可能一次都不切）。一个键 + 面板上一行字（见 BuildHudText）足够，
    /// 还省掉一次"图标到底画哪个"的审美争论。**不落盘**（和工具尺寸一样活在内存里）。
    ///
    /// 两种手势的判据**故意不一样**（见 InkDocument.ApplyMarquee / ApplyLasso）：
    /// 拖矩形＝"我框住这一片"（碰到就算），画一圈＝"我把这一条圈起来了"（80% 在内）。
    /// </summary>
    internal SelectMode SelMode = SelectMode.Rect;

    /// <summary>套索拖出来的路径（**画布坐标**，间距 ≥ <see cref="LassoStepLogical"/>）。</summary>
    internal readonly List<Vector2> LassoPath = new();

    /// <summary>
    /// 指针**此刻**的位置（画布坐标）。套索的路径是抽稀过的（间距 ≥3 逻辑像素），
    /// 只画路径的话线头会比指针慢半拍（200% 缩放下差 6 个物理像素，看得出来）。
    /// 所以预览画"路径 + 这一个点"，手感才是线头咬着指针走。
    /// </summary>
    internal Vector2 LassoLive;

    /// <summary>套索路径的采样间距（逻辑像素）：太密没用，还会把点数撑到几百。</summary>
    internal const float LassoStepLogical = 3f;

    /// <summary>
    /// 框选矩形 / 截图取景框的**锚点**（按下那一点，画布坐标）。
    ///
    /// 矩形必须按"锚点 ↔ 当前点"来算，**不能**在每次移动时累积 min/max：
    /// 累积的话指针往回走框不会跟着缩，屏幕上看到的是"扫过的最大范围"，
    /// 手感就是"不跟手"（用户 2026-09-15 实测反馈："人家是选中起始点以后，
    /// 鼠标不管怎么变，它都是一个可变的矩形，但是我们这个似乎不是"）。
    /// </summary>
    private float _mqAnchorX, _mqAnchorY;

    /// <summary>截图取景框的锚点（同上，两处用的是同一套算法）。</summary>
    private float _capAnchorX, _capAnchorY;

    /// <summary>按下那一刻的修饰键（套索的 Shift 加选 / Alt 减选）。</summary>
    private bool _lassoAdditive, _lassoSubtractive;

    // ---- 选择手势（框选工具 = 选择工具）---------------------------------
    // 四种情形在这里分流：点操作条按钮 / 拖手柄 / 在选中范围里整体拖 / 空白处重新框选。
    internal bool SelDragging;
    internal int SelBarHover = -1;          // 悬停的按钮，-1 = 无（给渲染用）

    /// <summary>
    /// 操作条**收起**了吗（用户 2026-09-16 更正：条首那个 ✕ 不是"取消选择"，是"收起"）。
    /// 收起后整条不画，只在框下方留一个小圆钮，点它展开——投影时不挡板书。
    /// 注意收起的是**条**，框与手柄照旧（参考实现也是这样）。
    /// </summary>
    internal bool SelBarCollapsed;

    /// <summary>当前开着的浮动面板（同一时刻只开一个）。见 <see cref="SelPanel"/>。</summary>
    internal SelPanel SelPanelOpen = SelPanel.None;

    /// <summary>正在拖颜色面板里的粗细滑条。</summary>
    private bool _sliderDragging;

    /// <summary>复制成功之后选区"闪一下"的到期时刻（0.25 秒，见 Overlay.DrawSelection）。</summary>
    internal double SelFlashUntilMs;

    /// <summary>复制成功时选区闪一下的时长（毫秒）。</summary>
    public const double SelFlashMs = 250;
    /// <summary>
    /// 批注键盘模式。开 = 键盘归批注层（编辑快捷键生效）；
    /// 关 = 覆盖层永不抢焦点，键盘还给下层程序。
    ///
    /// 这是个**明确的取舍**：开着的时候放映中的 PPT 收不到键盘，因为覆盖层
    /// 拿着前台焦点。用户拍板"先不考虑 PPT，先把底层做好"，所以默认开，
    /// 用 Ctrl+Alt+K 切换。
    /// </summary>
    internal bool KeyboardMode = true;

    /// <summary>
    /// 相机：画布坐标 → 屏幕坐标的纵向偏移。**滚动只改这一个数**，
    /// 不动任何对象数据——这是滚动能做到 O(1) 的前提。
    ///
    /// 往下滚 = 内容上移 = 这个值变负。只做纵向：板书是纵向累积的，
    /// 横向没有用武之地，还省掉双指横扫与"画横线"的手势冲突。
    /// </summary>
    internal float ViewOffsetY;

    /// <summary>
    /// 最后一次"滚动条该露面"的时刻（滚动或悬停）。滚动条按这个时间淡出——
    /// 静止 3 秒后消失（InkClass 实测 1.5 秒太快，用户会找不到它）。
    /// </summary>
    internal double ScrollBarActiveAtMs = double.MinValue;

    /// <summary>指针正停在滚动条上：滑块加粗、不淡出。拖动中也算（滑块不能半路淡掉）。</summary>
    internal bool ScrollBarHover;
    internal bool ScrollBarDragging;
    /// <summary>按下时指针相对滑块顶边的偏移，拖动中保持它不变，滑块才不"跳"。</summary>
    private float _scrollGrabDy;
    private OverlayWindow _scrollWindow;

    /// <summary>屏幕坐标 → 画布坐标（相机）。输入进来第一件事就是过这个。</summary>
    internal void ScreenToCanvas(ref float x, ref float y) => y -= ViewOffsetY;

    /// <summary>当前视口在**画布坐标**里的范围（相机之后）。</summary>
    internal RectF ViewportCanvas => new()
    {
        MinX = _virtualX,
        MinY = _virtualY - ViewOffsetY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY - ViewOffsetY + _virtualH,
    };

    /// <summary>
    /// 画布范围 = 内容边界 ∪ 当前视口 ∪ **视口下方一屏空白**。
    ///
    /// 最后那一屏是必须的：只取"内容 ∪ 视口"的话，**空文档的画布恰好一屏**，
    /// 下边界就是 0，往下滚立刻被夹回去——而滚不动就写不到下面去，写不到下面
    /// 内容就不长，内容不长范围就不长……**死锁**。
    /// （实测踩过：用户一打开软件，滚轮完全没反应，就是这个。）
    ///
    /// 留一屏空白等于"永远有一张白纸在下面"，像记事本一样。
    /// </summary>
    internal RectF CanvasExtent
    {
        get
        {
            var vp = ViewportCanvas;
            var r = Doc.Extent(vp);
            r.Add(new RectF
            {
                MinX = vp.MinX, MinY = vp.MaxY,
                MaxX = vp.MaxX, MaxY = vp.MaxY + _virtualH,
            });
            return r;
        }
    }
    // 这两个给自检看（--cursortest 要构造"拖拽中"的状态）。
    internal SelHandle _dragHandle = SelHandle.None;
    internal bool _dragIsMove;
    /// <summary>
    /// 这一次拖动的"点选起手笔迹"（按下时命中的那一条）。松手时若**一点没移动**，
    /// 就把多选收窄成只选它——PPT/Figma 的行为。见 EndSelDrag。
    /// </summary>
    internal Stroke _dragHitStroke;

    /// <summary>
    /// 点选的命中容差（逻辑像素）。细笔只有 1.5 逻辑像素宽，严格按墨判等于点不中；
    /// 4 像素和操作条手柄的容差同一量级。见 <see cref="InkDocument.HitObjectAt"/>。
    /// </summary>
    internal const float ClickToleranceLogical = 4f;
    private SelectionFrame _dragFrame;

    /// <summary>
    /// 这一次手势**真的动过**吗（指针离按下那一点超过了点击容差）。
    ///
    /// 只用来决定"装饰要不要收起来"（见 <see cref="SelChromeCollapsed"/>）：
    /// 单击一下不该让手柄和操作条闪一下。
    /// </summary>
    private bool _selDragMoved;

    /// <summary>
    /// 拖动预览（见 <see cref="DragPreviewActive"/>）里**从内容层摘出去**的那一批，
    /// 判定用（内容层重画一块时 O(1) 问"这条要不要跳过"）。见 <see cref="IsContentDetached"/>。
    /// </summary>
    private readonly HashSet<Stroke> _detachSet = new();

    /// <summary>
    /// 同一批，按**文档顺序**排一遍。画的时候用它——
    /// 一个集合的遍历顺序不是 z 序，多选里荧光笔压着字那种情况就会翻来翻去。
    /// </summary>
    private readonly List<Stroke> _detachOrdered = new();

    /// <summary>
    /// 上一帧拖动预览占的画布范围。
    ///
    /// 脏区要写成"上一帧 ∪ 这一帧"：后缓冲里躺着的是两帧前的画面，而这两块并起来
    /// 正好覆盖两帧之间的差集（这和以前"旧位置 ∪ 新位置"是同一套账，见
    /// <c>Overlay.UpdateFrameDirty</c> 的注释）。
    /// </summary>
    private RectF _dragPrevBounds;

    /// <summary>正在拖旋转手柄——度数标签靠它决定显不显示。</summary>
    internal bool SelRotating;
    /// <summary>
    /// 当前这一拖一共转了多少度（**逆时针为正、顺时针为负**，相对按下那一刻）。
    ///
    /// **不设上限**（用户 2026-09-15 定："贴合我们高中数学"）：转两圈就是 720°，
    /// 倒着转就是负数，超过 ±180° 也**不绕回**——所以它不是"起点到当前点的夹角"，
    /// 而是每帧一小步累加出来的（见 <see cref="_rotAccumDeg"/>）。
    /// </summary>
    internal float SelRotationDegrees;
    /// <summary>这个角度是"吸"出来的（90° 或 Shift 15° 网格），标签要变色提示。</summary>
    internal bool SelRotationSnapped;

    /// <summary>
    /// 本次旋转的**累积角**（度，逆时针为正）。每帧用
    /// <see cref="SelectionHandles.RotationStepDegrees"/> 取一小步加上去。
    ///
    /// 为什么不直接量"起点 → 当前点"：那个量天生只有 ±180° 的分辨率，
    /// 从起点直接甩到 200° 的位置，量出来是 -160°（最短路径），"转了一圈"
    /// 这件事就丢了。一帧一步地累积才记得住转了几圈。
    /// </summary>
    private float _rotAccumDeg;
    /// <summary>上一帧指针在**框坐标**里的位置（算这一帧转了多少用）。</summary>
    private Vector2 _rotPrevPoint;

    private Vector2 _dragStartPoint;
    private Matrix3x2[] _dragStartXform;
    private Stroke[] _dragTargets;
    private Matrix3x2 _selDragMatrix = Matrix3x2.Identity;
    /// <summary>调试开关：不画自己那一笔，只留系统合成器画出来的委托轨迹。</summary>
    internal bool SuppressActiveStroke;
    internal float MqMinX, MqMinY, MqMaxX, MqMaxY;
    internal bool PassThrough;
    /// <summary>
    /// Windows only honours click-through for a *layered* window, so the
    /// default has to include WS_EX_LAYERED. The other modes are kept as
    /// controls for the automated pass-through test.
    /// </summary>
    internal PassThroughMode PassMode = PassThroughMode.LayeredTransparent;
    internal bool NoContentCache;
    internal double NowMs;

    internal static readonly Color4 PenColor = InkPalette.PenDefault;
    internal static readonly Color4 HighlighterColor = InkPalette.HighlighterDefault;

    // 下面这些标成 internal 而不是 private，是给开发期测试宿主看的：
    // InkTeach 通过 InternalsVisibleTo 继承引擎、直接读内部状态来做自动化
    // 测量。产品界面走 IOverlayUi，不碰这些。
    internal readonly List<OverlayWindow> _windows = new();
    internal IntPtr _hInstance;
    internal string _className;
    private Native.WndProcDelegate _wndProc;
    private static readonly Dictionary<IntPtr, OverlayWindow> s_map = new();
    internal bool _quit;
    internal bool _dirty = true;
    private bool _animating;
    internal bool _drawing;
    private uint _activePointer;
    private uint _activePointerType;

    // ---- 压感采集与笔迹预测（实现见 Input/PenInput.cs、Prediction/InkPredictor.cs）----
    //  这两件事共用同一条时间轴：采样点自带硬件时标，压感保真靠它，外推预测也靠它。
    private readonly PenSampleBuffer _pen = new();
    private readonly InkPredictor _predictor = new();
    private readonly PredictedPoint[] _predBuf = new PredictedPoint[8];
    private readonly Vector2[] _trailReal = new Vector2[PenSampleBuffer.MaxSamples];
    private readonly Vector2[] _trailPred = new Vector2[8];

    /// <summary>湿墨预测开关。真笔专属，<c>--nopredict</c> 关掉。</summary>
    internal bool PredictEnabled = true;
    /// <summary>当前预测地平线（毫秒，8~15）。诊断用。</summary>
    internal double PredictHorizonMs => _predictor.HorizonMs;
    /// <summary>前带量的硬上限（像素）。诊断用。</summary>
    internal float PredictLeadCap => _predictor.MaxDistance;
    /// <summary>本笔有没有压感（设备级判断，不是看数值）。</summary>
    internal bool ActiveStrokeHasPressure;
    /// <summary>上一笔的合并率/预测统计（诊断与自检用）。</summary>
    internal int LastCoalescedSamples, LastCoalescedMessages;
    /// <summary>累计统计（--penlive 结束时汇总用）：真笔的压感/倾角/合并情况。</summary>
    internal int PenTotalPoints, PenPressurePoints, PenMessages, PenSamples;
    internal bool PenSawPressureMask, PenSawTiltMask, PenSawRotationMask;
    /// <summary>预测把湿墨往前带了多少（像素）——"说不清有没有用"时就看这个数。</summary>
    internal double PredLeadSum; internal int PredLeadCount; internal float PredLeadMax;
    internal int _cntDown, _cntMove, _cntUp, _cntCaptureLost;
    internal string _lastStrokeReport;
    private long _hotkeysRegistered;

    /// <summary>
    /// 键位表：动作 → 键 + 作用域。**可配置、可查冲突、可落盘**（见 KeyBindings.cs）。
    /// 启动时先读用户配置的覆盖项，退出时把改动写回去。
    /// </summary>
    internal KeyMap Keys = KeyMap.Default();

    internal int _virtualX, _virtualY, _virtualW, _virtualH;

    internal double _autoExitAt = double.MaxValue;

    // ---- perf accounting --------------------------------------------------
    internal readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
    internal double _fps;
    private int _frames;
    private double _fpsWindowStart;
    private TimeSpan _lastCpu;
    private double _lastCpuWall;
    private double _cpuPercent;
    internal double _peakWorkingSetMb;

    // ---- 界面接入口 -------------------------------------------------------
    // 引擎只认 IOverlayUi。界面通过 UiHost 改引擎状态，永远不直接碰文档。
    internal UiHost Host;
    internal IOverlayUi Ui;
    internal bool UiInvalidatePending;
    internal Color4 CurrentColor = InkPalette.PenDefault;
    internal Color4 HighlighterCurrent = InkPalette.HighlighterDefault;
    internal bool UiCapturing;

    /// <summary>自检用：系统问了我们几次命中测试、其中几次回答"这块是我的"。</summary>
    internal int _cntNcHitTest, _cntNcHitClient;

    // ==== 面板的"接输入小窗"（方案 B，见 计划-底层对接界面.md 4.1）=================
    //
    // 为什么需要它：穿透必须靠 WS_EX_TRANSPARENT（跨进程才点得到下层），
    // 而那个样式让窗口**在系统那一层就被排除在输入之外**——实测 WM_NCHITTEST
    // 一次都不会被调用。所以"面板可点"和"点击穿透"在同一个窗口里是互斥的。
    //
    // 解法：画面照旧画在覆盖层上（不新增渲染路径、不抢层序），另外开一块
    // **只收输入、不画东西**的小窗，正好盖住面板矩形。系统按窗口分发输入，
    // 于是"穿透"和"面板可点"天然同时成立。
    private IntPtr _uiInputHwnd;
    private bool _uiInputShown;
    private RectF _uiInputRect;                 // 物理像素

    /// <summary>
    /// 指针这一刻是不是停在界面自己那一块上。
    /// 两个地方要用：悬停时光标要给箭头（不是笔尖/橡皮圈）；穿透模式下
    /// WM_SETCURSOR 要区分"面板之外让下层决定"与"面板之上我们自己给箭头"。
    /// </summary>
    private bool _uiHover;

    /// <summary>
    /// 白板模式：给整块画布铺一层不透明的底色，遮住桌面和别的程序。
    /// 关掉就是原本的"透明批注"——直接写在别人的 PPT、网页上面。
    /// </summary>
    internal bool BoardOn;
    internal Color4 BoardColor = new(0.99f, 0.99f, 0.98f, 1f);
    internal double _lastRebuildMs;
    internal double _lastRecordMs;
    internal double _lastPresentMs;
    // 分块缓存的状态（面板与自检读数）
    internal int _tilesUsed, _tilesVisible, _tilesBudget, _tilesRasterized;
    private double _inputToPresentMs;
    private double _lastInputMs = -1;

    // ---- 延时探针（见 Latency.cs）---------------------------------------
    /// <summary>打开后每画完一帧就记一条延时样本。只有测量模式会打开。</summary>
    internal bool LatencyRecording;
    internal readonly LatencyRecorder Latency = new();
    /// <summary>最近一条指针消息自带的硬件时标（QPC 计数，可能是 0 = 不可信）。</summary>
    private ulong _lastInputPerfQpc;
    /// <summary>最近一条指针消息被我们拿到的时刻（QPC）。</summary>
    private ulong _lastInputMsgQpc;
    /// <summary>已经记过样的那条消息，避免同一帧重复记。</summary>
    private ulong _lastSampledMsgQpc;
    /// <summary>上一条指针消息到达的时刻，用来量实际输入采样间隔。</summary>
    private ulong _prevInputMsgQpc;
    /// <summary>这一帧收进了几个指针采样点。</summary>
    private int _framePoints;
    /// <summary>累计渲染帧数。测量模式用它算真实帧率（样本条数 ≠ 帧数）。</summary>
    internal long FrameCounter;
    internal double _nextLogAt;
    private double _accRecord, _accPresent, _accHud, _accStats;
    private int _accFrames;
    private double _nextStatsAt;
    private double _lastStatsMs;
    /// <summary>上一帧画性能面板花了多久。面板自己的代价也要看得见，否则它会掩盖问题。</summary>
    private double _lastHudMs;
    private double _hudAvgMs;        // 面板代价的窗口均值（单帧会被"重排那一帧"带偏）
    private double _accPanel;
    private double _hudRedrawMs, _hudRedrawSum, _hudRedrawPerSec;
    private double _hudRateWall;
    private long _hudRedrawCount, _hudRedrawTotal, _hudRedrawAtWall;
    private double _workingSetMb;
    private double _privateMb;
    private double _privateWsMb;      // 专用工作集 = 任务管理器"内存"列
    private double _sharedCommitMb;   // 共享提交（工作集里有多少是别的进程也有的）
    private double _gpuMb;
    private double _lastTopmostMs;

    /// <summary>Everything that talks to the OS for reporting: process times,
    /// working set, CPU share. Runs a few times a second, never per frame.</summary>
    private void UpdateStats()
    {
        var sw = Stopwatch.StartNew();

        double wall = _clock.Elapsed.TotalMilliseconds;
        // 一次调用拿齐内存三个口径；CPU 用 GetProcessTimes。
        // 以前走 Process.GetCurrentProcess()+Refresh()，那条路要几百微秒到几毫秒
        // （统计那一栏 3~4 ms 的大头就是它）——面板自己的开销不该跟被观测的东西一个量级。
        var c = new Native.PROCESS_MEMORY_COUNTERS_EX2();
        c.cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>();
        bool memOk = Native.GetProcessMemoryInfo(Native.GetCurrentProcess(), ref c, c.cb);
        if (memOk)
        {
            _workingSetMb = c.WorkingSetSize.ToUInt64() / 1048576.0;
            _privateMb = c.PrivateUsage.ToUInt64() / 1048576.0;
            double pws = c.PrivateWorkingSetSize.ToUInt64() / 1048576.0;
            // 老系统上 EX2 的后两个字段是 0：那时退回"工作集"，不编一个假数出来。
            _privateWsMb = pws > 0 ? pws : _workingSetMb;
            _sharedCommitMb = c.SharedCommitUsage / 1048576.0;
        }

        if (Native.GetProcessTimes(Native.GetCurrentProcess(), out _, out _, out long kernel, out long user))
        {
            var cpu = TimeSpan.FromTicks(kernel + user);
            double cpuDelta = (cpu - _lastCpu).TotalMilliseconds;
            double wallDelta = wall - _lastCpuWall;
            if (wallDelta > 250)
            {
                _cpuPercent = cpuDelta / wallDelta / Environment.ProcessorCount * 100.0;
                _lastCpu = cpu;
                _lastCpuWall = wall;
            }
        }

        _gpuMb = GpuUsedMb();
        if (_workingSetMb > _peakWorkingSetMb) _peakWorkingSetMb = _workingSetMb;
        sw.Stop();
        _lastStatsMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Stamped whenever a pointer message is handled, so the loop can
    /// report how long input took to reach the screen.</summary>
    private void StampInput() => _lastInputMs = _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// 把"这一帧覆盖的那条最新输入"记成一条延时样本。调用点在每个窗口都
    /// Present 之后——只有到那时候才知道这一帧实际什么时候被交出去。
    ///
    /// 只记**新**输入：同一帧里收到多条指针消息时以最后一条为准（它才是
    /// 屏幕上那一笔的笔尖位置），已经记过的就不再记。
    /// </summary>
    private void RecordLatencySample()
    {
        if (_windows.Count == 0) return;
        if (_lastInputMsgQpc == 0 || _lastInputMsgQpc == _lastSampledMsgQpc) return;
        var w0 = _windows[0];
        if (w0.LastPresentEndQpc < _lastInputMsgQpc) return;   // 这一帧还没把输入交出去

        _lastSampledMsgQpc = _lastInputMsgQpc;

        // 计时起点优先用**采样点自己的时标**，而不是"我们读到它的时刻"。
        //
        // 原因是消息会被系统合并：注入 140 Hz 的输入，应用实际只收到约 60 条
        // WM_POINTERUPDATE（其余被合并进 historyCount）。用"读到时刻"当起点，
        // 会把"系统什么时候肯把消息给我们"算进应用延时里，量出来一半的帧
        // 是 1.5 ms、另一半是 16 ms——那是投递节奏，不是渲染慢。
        // 用采样点时标当起点，才是"笔尖动 → 这一帧交出去"的干净口径。
        bool perfOk = HardwareStampUsable(_lastInputPerfQpc, _lastInputMsgQpc);
        ulong origin = perfOk ? _lastInputPerfQpc : _lastInputMsgQpc;

        var s = new LatencySample
        {
            InputToMsgMs = perfOk ? Qpc.Ms(_lastInputPerfQpc, _lastInputMsgQpc) : double.NaN,
            MsgToPresentMs = Qpc.Ms(origin, w0.LastPresentStartQpc),
            PresentBlockMs = Qpc.Ms(w0.LastPresentStartQpc, w0.LastPresentEndQpc),
            PointsInFrame = _framePoints,
            PresentCount = w0.LastPresentCount,
        };
        // 合计从"采样点产生"算起（拿不到采样点时标就退回"读到消息"那一刻）。
        s.TotalMs = s.MsgToPresentMs + s.PresentBlockMs;

        // 上屏时刻：窗口化合成交换链上，GetFrameStatistics 给的是"最近一次真正
        // 上屏的帧"。它可能比我们这一帧早（负值）——那种情况不算，标记为不可得。
        if (w0.LastFrameStatsOk && w0.LastDisplayQpc > w0.LastPresentEndQpc)
        {
            s.PresentToDisplayMs = Qpc.Ms(w0.LastPresentEndQpc, w0.LastDisplayQpc);
            s.HasDisplay = s.PresentToDisplayMs < w0.RefreshPeriodMs * 4;
        }

        Latency.Add(s);
        _framePoints = 0;
    }

    /// <summary>测量模式在每个场景开始前调用：把探针的游标清干净，避免上一场景的
    /// 残留时标把第一条样本算成"等了三百毫秒"。</summary>
    internal void ResetLatencyProbe()
    {
        _lastInputMsgQpc = 0;
        _lastSampledMsgQpc = 0;
        _prevInputMsgQpc = 0;
        _framePoints = 0;
    }

    /// <summary>
    /// 判断 POINTER_INFO.PerformanceCount 能不能当 QPC 用。
    ///
    /// 真实手写笔上它是硬件的 QPC 时标，值得采信；但**合成输入（SendInput /
    /// 注入）上它可能是 0 或别的计数器**，直接减会得到荒谬的数。所以先做
    /// 两条检查：非零、且不晚于我们收到它的时刻、且差值在 2 秒以内。
    /// 不通过就写 NaN，报告里那一列会是"不可得"，不会拿假数当结论。
    /// </summary>
    private static bool HardwareStampUsable(ulong stamp, ulong msgQpc)
        => stamp != 0 && stamp <= msgQpc && (msgQpc - stamp) * Qpc.TicksToMs < 2000.0;

    internal string LastCaptureReport = "";

    // =====================================================================
    //  Startup
    // =====================================================================

    internal int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Native.EnableMouseInPointer(true);

        Gfx.Init();
        NowMs = _clock.Elapsed.TotalMilliseconds;

        _hInstance = Native.GetModuleHandle(null);
        _className = "InkTeachOverlay_" + Guid.NewGuid().ToString("N");
        if (!RegisterWindowClass())
            return 2;

        string mode = args.Length > 0 ? args[0] : "";
        SelfCheckMode = mode.Length > 0;

        // 用户偏好（深色主题/贴边隐藏/档位/钉住，以及"启动要不要接上上次的板书"）。
        //
        // **必须读在恢复板书之前**：接不接上次的板书由 `ui.restoreInk` 决定
        // （默认不接，见 RestoreAutoSaveIfAny）。
        // 自检/基准模式**不读**——判据要确定，而且不该拿用户的设置去跑自检。
        if (InkSettings.PathOverride == null && args.Contains("--nosettings"))
            InkSettings.PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                              "inkteach-nosettings.json");
        if (!SelfCheckMode)
            foreach (var w in InkSettings.LoadUiPrefs(UiPrefs))
                Console.WriteLine("settings: " + w);

        // 上次因为界面出问题重启过？把板书读回来（读走就删，只恢复一次）。
        // 自检/基准模式不掺和：那些模式不该被"上次留下的板书"影响判据。
        if (mode.Length == 0) RestoreSessionIfAny();

        // 上次的自动存档（崩溃/正常退出都留）：**默认不接**，见 RestoreAutoSaveIfAny。
        if (mode.Length == 0) RestoreAutoSaveIfAny();

        // 宿主自己的启动分支（开发期的点击目标、截图工具等）。返回 true
        // 表示这条命令行已经由宿主处理完，引擎不再往下走。产品界面不需要覆写。
        if (PrepareHostStartup(mode, args, out int hostExit))
            return hostExit;

        // --nohud：关掉调试性能面板。它是给开发看的，每帧要花约 1.9 ms
        // （文字排版 + 进程计数），测底层性能时必须排除掉，否则量到的是
        // 测量工具本身而不是渲染引擎。
        if (args.Contains("--nohud")) ShowHud = false;

        // 对照实验用：--nohist 关掉脏区的多帧回溯，应当立刻出现残影，
        // 用来证明残影测试本身是有效的（而不是永远通过）。
        if (args.Contains("--nohist")) OverlayWindow.TransientHistoryFrames = 0;
        // ---- 委托墨迹轨迹（湿墨交给系统合成器画）-----------------------------
        //
        // 默认策略（2026-09-13 实测之后定）：**系统里真有笔设备就默认开**，
        // 没有就关。理由：这条通道对鼠标输入没有意义（实测合成鼠标下它不画），
        // 而对手写笔它是"笔尖跟手"的唯一正解——湿墨由 DWM 画，我们自己的
        // 渲染延时（敲定前实测约 1~3 帧）就不再压在笔尖上。
        //
        // 以前默认关是因为"本机没有笔、验证不了"。这台机器有笔数字化器，
        // 而且引擎只对 PT_PEN 调用它，鼠标路径完全不受影响。
        bool penDevicePresent =
            (Native.GetSystemMetrics(Native.SM_DIGITIZER)
             & (Native.NID_INTEGRATED_PEN | Native.NID_EXTERNAL_PEN)) != 0;
        OverlayWindow.InkTrailEnabled = penDevicePresent && !args.Contains("--noinktrail");
        if (args.Contains("--inktrail")) OverlayWindow.InkTrailEnabled = true;
        if (args.Contains("--noinktrail")) OverlayWindow.InkTrailEnabled = false;

        // ---- 笔迹预测 ---------------------------------------------------------
        // 默认跟着湿墨轨迹一起开（只对真笔生效）。--nopredict 关掉；
        // --predictms N 调地平线，超出 8~15 ms 会被收进范围（见原理文档第三节）。
        PredictEnabled = OverlayWindow.InkTrailEnabled && !args.Contains("--nopredict");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--predictms" && double.TryParse(args[i + 1], out double pm))
                _predictor.HorizonMs = pm;
        // 前带量的硬上限（像素）。默认 12 px 足够快机器；负载大、延迟高时要放宽才看得清效果。
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--predictlead" && float.TryParse(args[i + 1], out float pl))
                _predictor.MaxDistance = Math.Clamp(pl, 4f, 40f);
        _predictor.ClampHorizon();

        // ---- 呈现节奏 ---------------------------------------------------------
        // 默认改成"等到合成边界再抽输入、立刻 Present(0)"。实测这一项把
        // "Present 返回 → 像素亮"从 3.5 个刷新周期压到 1 个（见
        // reports/延时-实测与优化.md）。--nopace 退回老的 Present(1) 阻塞行为。
        OverlayWindow.VBlankPaced = !args.Contains("--nopace");
        if (OverlayWindow.VBlankPaced) OverlayWindow.LatencyWaitEnabled = true;

        _virtualX = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        _virtualY = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        _virtualW = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        _virtualH = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        Console.WriteLine($"virtual desktop: {_virtualW}x{_virtualH} at ({_virtualX},{_virtualY})");

        if (!CreateOverlays())
            return 3;

        // 键位：先读用户配置的覆盖项，再注册全局热键。读坏了不阻塞启动——
        // 用默认键位跑起来，但把问题逐条打出来（静默回退最坑人）。
        foreach (var w in InkSettings.Load(Keys))
            Console.WriteLine("settings: " + w);

        RegisterHotkeys();

        // 自检/基准模式下面板默认关掉，除非显式 --hud。
        //
        // 为什么：面板是个大黑框，画在屏幕左上角**压在一切之上**。而很多自检的
        // 判据恰恰是"数屏幕上的墨"（--coordtest / --inputtest / --erasertest /
        // --ghosttest / --tiletest / --appendtest / --passtest / --rotatetest…），
        // 面板往上一盖，那些用例会**假失败**——面板越大盖得越多，这次把面板
        // 放大到 700×258 逻辑像素就当场踩到了。交互模式仍然默认开着。
        if (mode.Length > 0 && !args.Contains("--hud")) ShowHud = false;

        int dispatch = RunModeDispatch(mode, args);
        if (dispatch >= 0)
            return dispatch;

        // 走到这里说明是**交互模式**：把键位表打出来（连同注册失败的条目）。
        // 测试模式不打印——三十多行会把每条自检的日志淹掉。
        // 以前这个方法**根本没人调用**，所以键位一直只活在 README 里。
        PrintUsageHelp();

        Loop();
        Shutdown();
        return ExitCode;
    }

    /// <summary>
    /// 启动阶段、窗口创建之前的宿主分支。引擎本身没有这样的模式，
    /// 开发期的截图/点击目标工具挂在宿主（InkTeach）里。
    /// </summary>
    protected virtual bool PrepareHostStartup(string mode, string[] args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }

    /// <summary>
    /// 读回"因为界面出问题而重启"之前暂存的板书。**读走就删**（只恢复一次），
    /// 太旧的由 <see cref="Recovery.TakeSession"/> 自己丢掉。
    /// </summary>
    private void RestoreSessionIfAny()
    {
        var blob = Recovery.TakeSession();
        if (blob == null) return;

        try
        {
            InkSerializer.LoadInto(Doc, blob);
            Doc.InvalidateAll();
            Console.WriteLine($"已恢复上次界面出问题时暂存的板书：{Doc.Strokes.Count} 笔");
        }
        catch (Exception ex)
        {
            Console.WriteLine("会话恢复失败（那份暂存已丢弃）：" + ex.Message);
        }
    }

    /// <summary>
    /// 启动时接上上次的板书（自动存档）。
    /// **读走不删**：它就是这份板书本身，下次打开还要接着用。
    /// 读坏了只提示、从空白开始——自动存档不该成为"起不来"的理由。
    /// </summary>
    private void RestoreAutoSaveIfAny()
    {
        _nextAutoSaveAtMs = NowMs + _autoSaveEveryMs;

        // **默认不接上一次的板书**（用户 2026-09-17："退出以后再打开不用恢复墨迹吧……
        // 我觉得默认不恢复墨迹"）。
        //
        // 为什么这个默认是对的：教室的机器是**公用**的，上一节课（甚至上一个班）的板书
        // 一开机又铺满整个屏幕，老师第一件事就得先清空；而且"打开就有别人的东西"
        // 本身就不合适。课与课之间要的是一块干净的白板。
        //
        // 注意和图省事的临时暂存（<see cref="RestoreSessionIfAny"/>）分开：那条只在
        // **软件自己主动重启**（界面上点"重启"、界面出问题自动重建）时才走，
        // 那是"重启不该丢东西"，和"下次打开要不要接上"是两件事，仍然保留。
        //
        // 开关放在界面偏好里（`ui.restoreInk = "1"`），以后在"更多"抽屉里给一行就能改；
        // 现在没有这一项 = 不恢复。
        if (!RestoreInkOnStartup)
        {
            Console.WriteLine("启动：不接上次的板书（默认；要接上就在设置里打开 restoreInk）");
            return;
        }

        var blob = Recovery.LoadAuto();
        if (blob == null) return;

        try
        {
            InkSerializer.LoadInto(Doc, blob);
            Doc.InvalidateAll();
            _autoSavedVersion = Doc.Version;
            Console.WriteLine($"已接上上次的板书：{Doc.Strokes.Count} 笔");
        }
        catch (Exception ex)
        {
            Console.WriteLine("自动存档读不出来（已忽略，从空白开始）：" + ex.Message);
        }
    }

    /// <summary>
    /// 窗口建好、快捷键注册完之后的分支。返回 -1 表示"没有特殊模式，
    /// 正常跑消息循环"；返回 &gt;= 0 表示直接以该值退出。
    /// 产品界面只会拿到 -1；测试模式由 InkTeach 覆写。
    /// </summary>
    protected virtual int RunModeDispatch(string mode, string[] args) => -1;

    /// <summary>
    /// 收尾时给进程的退出码。默认 0；自检发现有 FAIL 时置 1，
    /// 这样脚本/CI 不用去解析控制台文字也能判红绿（和 tools/ui-mock 的自检一个规矩）。
    /// 产品路径永远是 0。
    /// </summary>
    protected int ExitCode;

    private bool RegisterWindowClass()
    {
        _wndProc = WndProc;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className,
            // 兜底光标。**这一句是"永远转圈"的解药**：hCursor 留空（NULL）时
            // 系统会给一个默认的等待类光标，用户看到的就是一直转圈。
            // 具体形状由 WM_SETCURSOR 按状态覆盖（见 ApplyCursor）。
            hCursor = Cursors.System(Native.IDC_ARROW),
        };
        ushort atom = Native.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    private bool CreateOverlays()
    {
        bool ok = true;
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var w = new OverlayWindow();
            var err = w.Create(_hInstance, _className, r.Left, r.Top, r.Width, r.Height);
            if (err != null)
            {
                Console.WriteLine($"overlay create failed on monitor {hMon}: {err}");
                ok = false;
                return false;
            }
            s_map[w.Hwnd] = w;
            _windows.Add(w);
            Console.WriteLine($"overlay on monitor {hMon}: {r.Width}x{r.Height} at ({r.Left},{r.Top}) dpi={w.Dpi}");
            Console.WriteLine($"委托墨迹轨迹(InkTrail): {OverlayWindow.InkTrailNote}");
            return true;
        }, IntPtr.Zero);

        if (_windows.Count == 0)
        {
            Console.WriteLine("no monitors found");
            return false;
        }

        // Refresh the HUD ~4x a second so the numbers move even when idle.
        //
        // 顺便每隔 250 ms 重新"置顶"一次（WM_TIMER 里调 ReassertTopmost）。
        // 这个必须够快：**别的置顶窗口会把我们盖住**——实测输入法状态条
        // （wetype 的 StatusBarWnd）、桌面小组件（RocketView）都是 TOPMOST，
        // 谁最后调 SetWindowPos 谁在上面。被盖住的后果不只是"看不见"：
        // 合成/真实点击都会落到那个窗口上，批注一个字都画不出来。
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)1, 250, IntPtr.Zero);
        DpiScale = _windows[0].Dpi / 96f;

        Host?.UpdateScreen(LogicalVirtualScreen);
        Console.WriteLine($"DPI 缩放 {DpiScale:F2}（逻辑 {_windows[0].Width / DpiScale:F0}x{_windows[0].Height / DpiScale:F0}）");

        // 把键盘模式落到窗口样式上。字段默认是开的，但样式要等窗口建好才能改——
        // 不调这一句，默认值和实际样式就对不上（得按一次 Ctrl+Alt+K 才生效）。
        SetKeyboardMode(KeyboardMode);
        return ok;
    }

    private void RegisterHotkeys()
    {
        // 全局热键**不再是一串写死的 (id, key)**：动作与键位由 Keys 这张表决定，
        // id 只是"注册顺序"，HandleHotkey 拿它反查动作。这样键位可配置、可查冲突，
        // 而引擎里不认识"Ctrl+Alt+P"这种东西。
        IntPtr h = _windows[0].Hwnd;
        _hotkeyActions.Clear();
        Keys.GlobalFailures.Clear();

        foreach (var b in Keys.For(KeyScope.Global))
        {
            int id = _hotkeyActions.Count + 1;
            uint mod = Native.MOD_NOREPEAT;
            if ((b.Chord.Modifiers & KeyChord.ModCtrl) != 0) mod |= Native.MOD_CONTROL;
            if ((b.Chord.Modifiers & KeyChord.ModAlt) != 0) mod |= Native.MOD_ALT;
            if ((b.Chord.Modifiers & KeyChord.ModShift) != 0) mod |= Native.MOD_SHIFT;

            _hotkeyActions.Add(b.Action);
            if (!Native.RegisterHotKey(h, id, mod, b.Chord.Vk))
            {
                // **注册失败要留痕**：教室机上微信 / QQ / 输入法 / 教学软件都会抢键
                // （实测 Ctrl+Alt+W 就被微信截图占了）。以前这里只打一行控制台，
                // 用户根本不知道，只觉得"这个键没反应"。
                int err = Marshal.GetLastWin32Error();
                string msg = $"{b.Chord}（{KeyMap.Describe(b.Action)}）注册失败，错误 {err}"
                           + (err == 1409 ? "：已被别的程序占用" : "");
                Keys.GlobalFailures.Add(msg);
                Console.WriteLine("hotkey " + msg);
            }
            else _hotkeysRegistered++;
        }
        Console.WriteLine($"registered {_hotkeysRegistered}/{Keys.Bindings.Count(b => b.Scope == KeyScope.Global)} global hotkeys");
    }

    /// <summary>注册顺序 → 动作。按这个顺序 RegisterHotKey，WM_HOTKEY 的 id 就是它。</summary>
    private readonly List<KeyAction> _hotkeyActions = new();

    internal KeyAction ActionForHotkeyId(int id)
        => id >= 1 && id <= _hotkeyActions.Count ? _hotkeyActions[id - 1] : KeyAction.None;

    /// <summary>自检用：这批全局热键实际注册成功了几个。</summary>
    internal long HotkeysRegistered => _hotkeysRegistered;

    // =====================================================================
    //  Message loop
    // =====================================================================

    internal void Loop()
    {
        while (!_quit)
        {
            DrainMessages();
            if (_quit) break;

            NowMs = _clock.Elapsed.TotalMilliseconds;

            if (NowMs >= _autoExitAt) break;

            // 跑满一分钟还没出事 → 把"因为界面出问题而重启"的计数清掉。
            // 不这么做的话，上午两次、下午两次，第四次就会莫名其妙不许重启。
            if (!_restartCountCleared && NowMs > 60_000)
            {
                Recovery.ClearRestartCount();
                _restartCountCleared = true;
            }

            Laser.Prune(NowMs);
            StepCameraAnim();                 // 翻页动画（167ms）
            if (NeedsFrame())
            {
                // VBlankPaced：先等到合成边界，**再抽一次消息**，然后画、提交。
                //
                // 顺序是有讲究的。等到边界之后抽，把"等边界期间到达的输入"
                // 也算进这一帧，笔尖就是最新的；反过来（先抽、再等一个周期）
                // 等于让最新的那一笔在队列里多躺一个刷新周期。
                if (OverlayWindow.VBlankPaced)
                {
                    Native.DwmFlush();
                    DrainMessages();
                    _dirty = true;
                }
                // 渲染期间界面可能又提出"我还要一帧"（在 Render 里调 InvalidateUi）。
                // 用序号认出来，别让这一句 _dirty = false 把它抹掉——
                // 抹掉的表现就是"动画或一次性外观变化卡在第一帧"。
                long seqBefore = _uiInvalidateSeq;
                long docVerBefore = Doc.Version;
                RenderAll();
                // 渲染期间**改了文档**（"按住清空"就是在界面的 Render 里够时间的）
                // 也要再要一帧：这一帧贴出去的是改之前的像素。
                // 少了后面这半句，清空之后屏幕上那层墨会一直留着——见 RenderAll 里的注释。
                _dirty = _uiInvalidateSeq != seqBefore || Doc.Version != docVerBefore;
            }
            else
            {
                Native.WaitMessage();
            }
        }
    }

    /// <summary>把消息队列里现有的消息全部处理掉，不阻塞。测试模式复用同一份，
    /// 免得"测量用的循环"和"真正的循环"因为顺序不同而量出两个结论。</summary>
    internal void DrainMessages()
    {
        while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) { _quit = true; break; }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        NowMs = _clock.Elapsed.TotalMilliseconds;
    }

    internal void RenderAll()
    {
        FrameCounter++;
        // **当前页跟着相机走**：模型层需要一个"现在在第几页"的整数（新写的笔要标页、
        // 撤销栈的每一步要记页），而它只该由一处告诉它——每帧同步一次，滚轮/翻页/
        // 自动翻页/程序化跳页全都自动覆盖。
        //
        // **放映中例外**：那时候"当前页"是幻灯片页（SlidePageBase + 位置），
        // 不该被相机反算出来——相机反算只会得到白板页号。由 PollSlides 负责。
        PollSlides();
        if (!SlideNow.Showing) Doc.CurrentPage = CurrentPage;
        double frameStart = NowMs;
        double wall0 = _clock.Elapsed.TotalMilliseconds;

        // 自动存档：**放在这里**（每帧都过）而不是主循环里——主循环那条路
        // 只在"真有帧"时才走到，而自检是用"抽消息＋渲染"驱动的，挂在主循环里
        // 自检就永远验不到它（第一版就是这么漏的：改了板书也不写）。
        // 自检模式走临时路径（Recovery.AutoSavePathOverride），不会碰用户的板书。
        MaybeAutoSave();

        // The HUD text and the process counters cost an order of magnitude more
        // than drawing the ink does, so they refresh a few times a second rather
        // than every frame. Measured before this change: 2.7 ms + 3.5 ms per
        // frame, against 0.7 ms for the actual rendering.
        var swHud = Stopwatch.StartNew();
        bool statsRan = false;
        if (wall0 >= _nextStatsAt)
        {
            // 面板显示时才需要 4 Hz 刷新；只打日志的话 1 Hz 就够。
            // 进程计数一次要 ~3 ms，没必要每秒跑 4 次。
            _nextStatsAt = wall0 + (ShowHud ? 250 : 1000);
            UpdateStats();
            // 只在真的要显示面板时才拼字符串。BuildHudText 会去查进程内存，
            // 那是整条路径里最贵的一步；面板关掉还在跑就纯属浪费——
            // 实测它让空闲 CPU 从 ~0.3% 涨到 2~3.6%（单核）。
            if (ShowHud) HudText = BuildHudText();
            statsRan = true;
        }
        swHud.Stop();

        // 相机写给各覆盖窗口：渲染的每一处变换都用它（见 OverlayWindow.CanvasToWindow）。
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = ViewOffsetY; }

        // 面板的接输入小窗跟着界面这一刻占的地方走（方案 B）。
        // 放在渲染之前：这一帧界面画在哪，输入就该收在哪，两件事同源。
        UpdateUiInputWindow();

        // 记下渲染前的文档版本：**渲染期间界面可能改文档**（"按住清空"就是在界面的
        // Render 回调里够时间的——界面没有别的"每帧回调"可用）。见下面的判断。
        long docVerBeforeRender = Doc.Version;

        foreach (var w in _windows)
            w.RenderFrame(this);
        foreach (var w in _windows)
            w.Present();

        if (LatencyRecording) RecordLatencySample();

        // 界面自己要求的重画（InvalidateUi）已经在这一帧贴完，可以清掉了。
        UiInvalidatePending = false;

        // Every window has now applied this round of changes, so the stale
        // regions can be dropped. Doing it here (rather than inside a window)
        // is what keeps multi-monitor setups correct.
        //
        // **但渲染期间改了文档就不能清**：这一帧贴出去的是改之前的像素，
        // 脏区得留给下一帧。这里踩过一个真 bug（用户报的"橡皮清空没有用、
        // 还把墨迹卡住、连常规橡皮都擦不掉"）：清空是在界面的 Render 里触发的，
        // 引擎渲染完无条件 Reset()，于是**文档已经空了、屏幕上那层墨还留着**——
        // 看着像清空失效，而橡皮也擦不掉（文档里已经没有东西可擦了）。
        if (Doc.Version == docVerBeforeRender)
        {
            Doc.Dirty.Reset();
            Doc.AppendedSinceRender.Clear();
            Doc.StructureChangedSinceRender = false;
        }

        var w0 = _windows[0];
        _lastRebuildMs = w0.LastRebuildMs;
        _lastRecordMs = w0.LastRecordMs;
        _lastPresentMs = w0.LastPresentMs;
        _tilesUsed = w0.LastTileCount;
        _tilesVisible = w0.LastTileVisible;
        _tilesBudget = w0.LastTileBudget;
        _tilesRasterized = w0.LastPatchCount;

        // How long the newest input took to reach the screen. This is our own
        // contribution; the display pipeline adds up to one more scan-out.
        // 只在"这一帧确实处理了新输入"时采样，而且采完就作废。
        //
        // 这里原来有个 bug：_lastInputMs 用完不清零，于是空闲帧（激光动画、
        // 或只是等垂直同步的那一帧）会把同一条旧输入再算一遍，而且越算越大，
        // 把 EMA 一路往上带。面板上"输入到上屏"那个数因此偏大且不收敛。
        if (_lastInputMs > 0)
        {
            double sample = _clock.Elapsed.TotalMilliseconds - _lastInputMs;
            if (sample >= 0 && sample < 200)
                _inputToPresentMs = _inputToPresentMs <= 0
                    ? sample
                    : _inputToPresentMs * 0.85 + sample * 0.15;
            _lastInputMs = -1;
        }

        double wall = _clock.Elapsed.TotalMilliseconds;
        _lastFrameMs = wall - frameStart;
        // 手测台：把这一帧的"重画 + 整帧"耗时喂给采集器（它只在拖拽中累计）。
        if (EraserTelemetry != null)
        {
            EraserTelemetry.Frame(_windows.Count > 0 ? _windows[0].LastPatchMs : 0,
                                  _lastFrameMs, _inputToPresentMs);
            EraserTelemetry.Beat(NowMs, _fps, Doc);
        }

        _frames++;
        if (wall - _fpsWindowStart >= 500)
        {
            _fps = _frames * 1000.0 / (wall - _fpsWindowStart);
            _frames = 0;
            _fpsWindowStart = wall;
        }

        _accRecord += _lastRecordMs;
        _accPresent += _lastPresentMs;
        // 面板自己的代价从窗口那边取（重排 + 贴图），不是这里的"统计"耗时——
        // 混在一起会让"面板很贵"这件事被统计代码盖住。
        _lastHudMs = _windows.Count > 0 ? _windows[0].LastHudMs : 0;
        _accHud += swHud.Elapsed.TotalMilliseconds;
        _accPanel += _lastHudMs;
        if (_windows.Count > 0)
        {
            double redraw = _windows[0].LastHudRedrawMs;
            if (redraw > 0) { _hudRedrawSum += redraw; _hudRedrawCount++; }
            _hudRedrawTotal = _windows[0].HudRedraws;
        }
        if (statsRan) _accStats += _lastStatsMs;
        _accFrames++;

        if (NowMs >= _nextLogAt)
        {
            _nextLogAt = NowMs + 1000;
            int n = Math.Max(1, _accFrames);
            Console.WriteLine(
                $"[{NowMs / 1000,6:F1}s] fps={_fps,5:F1}"
                + $" 记录={_accRecord / n,5:F2} 上屏={_accPresent / n,5:F2}"
                  + $" 面板={_hudRedrawMs,4:F2}ms×{_hudRedrawPerSec:F0}/s 统计={_lastStatsMs,5:F2}"
                  + $" 合计={(_accRecord + _accPresent + _accHud + _accStats) / n,6:F2}ms"
                  + $" 置顶={_lastTopmostMs,5:F2}ms"
                  + $" 脏区={_windows[0].LastPresentRectCount}块/{_windows[0].LastPresentAreaPercent,5:F1}%"
                  + $" ws={_workingSetMb,6:F1}MB");
            _accRecord = _accPresent = _accHud = _accStats = 0;
            _hudAvgMs = _accPanel / n;
            _accPanel = 0;
            double secs = Math.Max(0.001, (NowMs - _hudRateWall) / 1000.0);
            _hudRedrawPerSec = (_hudRedrawTotal - _hudRedrawAtWall) / secs;
            _hudRedrawAtWall = _hudRedrawTotal;
            _hudRateWall = NowMs;
            _hudRedrawMs = _hudRedrawCount > 0 ? _hudRedrawSum / _hudRedrawCount : 0;
            _hudRedrawSum = 0; _hudRedrawCount = 0;
            _accFrames = 0;
        }
    }

    /// <summary>
    /// 显存已用量（MB）。
    ///
    /// 为什么要显示它：**核显的显存是从系统内存里分的**，而它**不计入进程工作集**。
    /// 任务管理器那一列在核显机器上会把 GPU 共享显存算进去，于是出现
    /// "任务管理器 1000MB、面板只有 100MB"——不是面板不准，是只报了一个口径，
    /// 恰好漏掉了最大的一块。
    /// </summary>
    private static double GpuUsedMb()
    {
        try
        {
            if (Gfx.Adapter3 == null) return 0;
            var info = Gfx.Adapter3.QueryVideoMemoryInfo(0, Vortice.DXGI.MemorySegmentGroup.Local);
            return info.CurrentUsage / 1048576.0;
        }
        catch { return 0; }
    }
    private string BuildHudText()
    {
        // 面板的排版原则：**一行一类事**，数字对齐，单位统一。
        // 内存那一行是重点：三个口径并列，并且把"任务管理器里叫哪一列"写出来
        // ——用户拿它跟任务管理器对照时最需要的就是这句话。
        return
            $"帧率 {_fps,6:F1} fps（空闲不重绘）   帧耗时 {_lastFrameMs,5:F2} ms   输入→上屏 {_inputToPresentMs,5:F1} ms\n" +
            $"绘制 {_lastRecordMs,5:F2} ms      上屏 {_lastPresentMs,5:F2} ms      分块光栅 {_lastRebuildMs,5:F2} ms\n" +
            $"面板 {_hudRedrawMs,4:F2} ms/次 ×{_hudRedrawPerSec,3:F0}/秒（摊到每帧 {_hudAvgMs,4:F2} ms）   统计 {_lastStatsMs,5:F2} ms\n" +
            $"内存 {_privateWsMb,6:F1} MB（专用工作集 = 任务管理器的“内存”）\n" +
            $"已提交 {_privateMb,6:F1} MB（=“提交大小”）   工作集 {_workingSetMb,6:F1} MB（含共享）\n" +
            $"共享提交 {_sharedCommitMb,6:F1} MB   显存 {_gpuMb,6:F1} MB   CPU {_cpuPercent,4:F1} %\n" +
            $"笔画 {Doc.Strokes.Count}      点数 {Doc.TotalPoints}\n" +
            $"选中 {Doc.Selected.Count}      工具 {ToolName(Tool)}{SelectModeTag()}{(PassThrough ? "（穿透中）" : "")}      粗细 {CurrentToolWidthLogical,4:F1}      撤销栈 {Doc.UndoDepth}\n" +
            $"分块 {_tilesUsed}/{_tilesBudget}（可见 {_tilesVisible}，本帧光栅 {_tilesRasterized}）      网格 {Doc.GridCells}\n" +
            $"Ctrl+Alt：1笔 2荧光 3激光 4橡皮 7像素橡皮 5框选 9矩形/套索 6粗细 Z撤销 C清空\n" +
            (EraserTelemetry != null
                ? $"橡皮手测台：记录中 · 已记 {EraserTelemetry.DragCount} 条拖拽（退出时写汇总）\n"
                : "") +
            $"其它 Ctrl+Alt：I面板 P穿透 K键盘 Y穿透方式 X退出";
    }

    /// <summary>面板上跟着"工具"显示的当前选择方式（只有框选工具用得上）。</summary>
    private string SelectModeTag()
        => Tool == Tool.Marquee ? (SelMode == SelectMode.Lasso ? "·套索" : "·矩形") : "";

    private static string ToolName(Tool t) => t switch
    {
        Tool.Pen => "笔",
        Tool.Highlighter => "荧光笔",
        Tool.Laser => "激光笔",
        Tool.Eraser => "橡皮擦",
        Tool.PixelEraser => "像素橡皮",
        Tool.Capture => "截图",
        Tool.Marquee => "框选",
        Tool.Line => "直线",
        Tool.Rectangle => "矩形",
        Tool.Ellipse => "圆",
        Tool.Arrow => "箭头",
        _ => "?",
    };

    // =====================================================================
    //  Window procedure
    // =====================================================================

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 面板的"接输入小窗"（方案 B）：它只收输入，什么都不画。
        if (_uiInputHwnd != IntPtr.Zero && hWnd == _uiInputHwnd)
            return UiInputWndProc(hWnd, msg, wParam, lParam);

        // 宿主自己的窗口（开发期的点击目标）先处理。产品界面不会用到这一层。
        if (HandleHostWindowMessage(hWnd, msg, wParam, lParam, out var hostResult))
            return hostResult;

        // 滚轮：滚动画布（只改相机偏移，不动对象数据）。见 HandleWheel。
        if (msg == 0x020A /*WM_MOUSEWHEEL*/) return HandleWheel(wParam);
        // 批注键盘模式下的按键。只有这个模式收得到——见 SetKeyboardMode。
        if ((msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN) && HandleKeyDown(wParam))
            return IntPtr.Zero;

        switch (msg)
        {
            case Native.WM_NCHITTEST:
                // 这两个计数只给自检用：它回答"穿透时系统到底还问不问我"，
                // 这是判定"能不能只靠命中测试做区域穿透"的唯一硬证据。
                _cntNcHitTest++;
                if (!PassThrough)
                {
                    _cntNcHitClient++;
                    return new IntPtr(Native.HTCLIENT);
                }
                // 穿透模式下，**界面自己那一块仍然归界面**。
                //
                // 以前这里一律回 HTTRANSPARENT，于是"开着穿透还想用工具条"这个
                // 最常见的组合直接失效：命中测试在系统那一层就把我们排除了，
                // WM_POINTERDOWN 根本轮不到引擎，悬浮球变成了一个画出来的装饰。
                HitTestPoint(lParam, out float hitX, out float hitY);
                bool mine = UiContains(hitX, hitY);
                if (mine) _cntNcHitClient++;
                return new IntPtr(mine ? Native.HTCLIENT : Native.HTTRANSPARENT);

            // 指针形状。**必须显式回答**：不处理时系统会拿窗口类的光标兜底，
            // 而类光标为 NULL 的表现就是"永远转圈"（这次修的就是它）。
            //
            // 只在"移动且输入未被捕获"时才会收到这条消息——所以拖拽中的光标
            // 得在按下那一刻自己设（见 OnPointerDown），不能等这里。
            case Native.WM_SETCURSOR:
                // 非客户区（边框、缩放角）由系统按自己的规矩来。
                if ((lParam.ToInt64() & 0xFFFF) != Native.HTCLIENT) break;
                // 穿透：面板之外让下层窗口决定；**面板自己那一块要给箭头**
                // （否则"工具条上顶着一个笔尖圈"或者一个转圈光标）。
                if (PassThrough && !_uiHover) break;
                ApplyCursor(force: true);
                return new IntPtr(1);                   // TRUE = 已处理，别再兜底

            case Native.WM_POINTERENTER:
                PointerInside = true;
                TrackPointerLeave(hWnd);
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                // 指针离开窗口：落点反馈（橡皮圆环、笔尖环）必须跟着消失，
                // 否则手一移开，屏幕上就留下一个圈。
                PointerInside = false;
                _dirty = true;
                ApplyCursor();
                // 注意：**这里不能叫 Ui.PointerLeave()**。覆盖层的"离开"在面板接管输入时
                // 恰恰是"指针进了面板"的那一刻（人从画布移到工具条上），
                // 界面收到的会是反的。界面那条离开由接输入小窗发（见 UiInputWndProc）。
                return IntPtr.Zero;

            case Native.WM_MOUSELEAVE:
                PointerInside = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
                OnPointerDown(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUPDATE:
                OnPointerMove(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERUP:
                OnPointerUp(hWnd, wParam);
                return IntPtr.Zero;

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                EndStroke();
                return IntPtr.Zero;

            case Native.WM_HOTKEY:
                HandleHotkey(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;

            case Native.WM_TIMER:
                // 只有要显示性能面板时才需要周期性重绘；面板关掉还每秒重画 4 次
                // 纯属白烧电。
                if (ShowHud) _dirty = true;
                UpdateScrollBarHoverFromCursor();
                var swTop = Stopwatch.StartNew();
                ReassertTopmost();
                swTop.Stop();
                _lastTopmostMs = swTop.Elapsed.TotalMilliseconds;
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                _quit = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 让宿主处理属于它自己创建的窗口的消息。返回 true 表示消息已被消费。
    /// </summary>
    protected virtual bool HandleHostWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        return false;
    }

    private void ReassertTopmost()
    {
        // **弹着系统对话框的时候一律不抬**（用户 2026-09-17 报的"点开一下就收回去、
        // 选不到 jpg"，2026-09-17 探针定位）。
        //
        // 这一抬是每秒一次的定时器干的（WM_TIMER），平时是必要的：全屏覆盖层被别的
        // 程序抢到后面去就"看不见也画不上"。但**弹着"另存为"的时候它是有害的**：
        // 覆盖层被重新抬到置顶，就去跟对话框抢那一层，对话框自己的**下拉列表**
        // （普通弹窗，不在置顶层）随即被盖住 / 被关掉——探针连着拍三张看得很清楚：
        // 点下拉 150 毫秒时列表好好地开着（PNG / JPEG 两条），900 毫秒时已经没了。
        // 那 900 毫秒正好压着一次定时器。
        //
        // 对话框期间由 ExportFileDialog 自己保证"它在最上面、而且一直置顶"
        // （见 CenterAndBringUp），关掉之后 ReturnFocusAfterDialog 再把覆盖层拾回来。
        if (ExportDialogOpen) return;

        foreach (var w in _windows)
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        // 接输入小窗**必须排在最后抬**：两块都是置顶窗口，谁最后 SetWindowPos
        // 谁在上面（见 计划-底层对接界面.md 4.1 的"层序"纪律）。只在这一处抬，
        // 别处不许再抬它，否则会出现"面板偶尔被自己的笔迹层盖住"。
        if (_uiInputShown && _uiInputHwnd != IntPtr.Zero)
            Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 这一刻该不该出一帧：脏了，或者有东西还在动。
    ///
    /// 三个"在动"的来源，各有各的理由：
    ///   · 激光轨迹：它自己会到期消失，不驱动的话最后一帧画完就没人再画了，
    ///     那道高亮会一直挂在屏幕上；
    ///   · 正在书写：笔尖这条线每帧都在变；
    ///   · **界面自己声明的动画**（<see cref="IOverlayUi.IsAnimating"/>）：
    ///     展开/收起、悬停展开、贴边吸附全靠它，缺了就是"动画停在第一帧"。
    ///
    /// 自检直接调它来数帧——这样"引擎给不给帧"用的是**同一个判据**，
    /// 不会出现"测的是一套、跑的是另一套"。
    /// </summary>
    internal bool NeedsFrame()
    {
        _animating = Laser.ActiveAt(NowMs) || _drawing || SelFlashing
                   || UiIsAnimatingNow || _camAnimating;
        return _dirty || _animating;
    }

    // =====================================================================
    //  Pointer input
    // =====================================================================

    private void OnPointerDown(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntDown++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 相机：屏幕 → 画布

        // 界面优先：点在悬浮条上就是操作界面，不是画一笔。
        //
        // 两件容易踩的事：
        //  1) 这一段必须**排在穿透判断之前**——不然开着穿透时界面永远收不到按下；
        //  2) 传给界面的是**逻辑屏幕坐标**，不是画布坐标。界面的布局与绘制都在
        //     屏幕坐标里，喂它画布坐标的话相机一滚，命中就整体偏掉一块。
        //
        // 注：面板在实际产品里由"接输入小窗"（方案 B）接管，走的不是这条路；
        // 这里留着是**兜底**——万一那块小窗没建起来，至少非穿透模式下还能用。
        if (UiPointerDown(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted))
        {
            _drawing = false;
            // 界面也要捕获指针：拖出悬浮条、在按钮上滑开都需要继续收到消息。
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 落在界面画的那一块里、但界面没吃这一下：这一下就算了。
        // ① 不落墨——面板底下的墨看不见，面板一挪又冒出来，属于"看不见却被记下来"；
        // ② 不透给下层——界面声明占用的区域（QueryBounds）就是它的地盘，
        //    要放行得让界面自己别把那一块算进去。
        if (UiContains(screenX, screenY))
        {
            _uiHover = true;
            _drawing = false;
            _dirty = true;
            return;
        }

        // 界面之外的穿透：交下层窗口，我们不收这一下。
        if (PassThrough) return;

        _activePointer = id;
        _activePointerType = ptype;
        PointerX = x; PointerY = y; PointerInside = true;

        // 滚动条次之：它只占右边缘一条窄带，但"拖滑块"比"在那儿画一笔"更特殊。
        if (TryBeginScrollBarDrag(screenX, screenY))
        {
            _drawing = true;
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        _drawing = true;
        Native.SetCapture(hWnd);
        // 拖拽中系统不再发 WM_SETCURSOR（输入已被捕获），光标必须在按下这一刻定下来。
        ApplyCursor();

        var tool = inverted ? Tool.Eraser : Tool;
        switch (tool)
        {
            case Tool.Eraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginErase();
                EraserTelemetry?.BeginDrag(Tool.Eraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseAt(x, y, EraserRadius);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.PixelEraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginEraseRect();
                EraserTelemetry?.BeginDrag(Tool.PixelEraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseRectAt(x, y, PixelEraserHalfWidthPx, PixelEraserHalfHeightPx);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.Capture:
                // 和框选同一个手势。**和框选一样存画布坐标**（渲染那一层就
                // 不必为它单独换算一次），抓屏时再换算回屏幕坐标
                // （screen = canvas + 相机偏移，见 EndCapture）。
                BeginCaptureAt(x, y);
                break;

            case Tool.Marquee:
                // 先问选择手势（按钮 / 手柄 / 整体拖动）；都没接才起新的框选。
                // 修饰键在这一刻读一次就传进去（而不是在判定函数里读键盘）——
                // 这样"Shift 加选 / Alt 减选"这条分支能被自检直接驱动。
                if (!TryBeginSelectionGesture(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0))
                {
                    BeginMarqueeAt(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0);
                }
                break;

            case Tool.Laser:
                Laser.Clear();
                Laser.Visible = true;
                Laser.Add(x, y, NowMs);
                break;

            default:
                float trailW = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                                         : tool == Tool.Laser ? LaserWidthLogical
                                                         : PenWidthLogical) * DpiScale;
                // 只对真笔（PT_PEN）起轨迹：这条通道是给"笔尖跟手"用的，
                // 鼠标/触摸走它没有意义，而且会平白多一条系统画出来的线。
                if (ptype == Native.PT_PEN)
                    WindowAt(screenX, screenY)?.BeginInkTrail(
                        tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor, trailW * 0.5f);
                ActiveStroke = new Stroke
                {
                    Tool = tool,
                    Color = tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
                    Width = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                                     : tool == Tool.Laser ? LaserWidthLogical
                                                     : PenWidthLogical) * DpiScale,
                    // **起笔就标页**：这一笔属于"起笔时看着的那一页"。
                    // 为什么按起点而不是按几何现算：笔画是可以跨页界的（"页之间不设墙"），
                    // 跨界的那些必须有个确定的归属，"从哪儿开始写"是最符合直觉的那条规则。
                    Page = CurrentPage,
                };
                // 起笔：预测器从这一刻开始积累；落笔这条消息里可能已经合并了几个采样点，
                // 一起收进来（以前只取最新那一个）。
                _predictor.Reset();
                ActiveStrokeHasPressure = false;
                LastCoalescedSamples = LastCoalescedMessages = 0;
                AppendStrokeSamples(id, ptype, x, y, pressure);
                FeedInkTrail(ptype, trailW * 0.5f, screenX, screenY);
                break;
        }
        _dirty = true;
    }

    private void OnPointerMove(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntMove++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 相机：屏幕 → 画布，下游全按画布坐标走
        PointerX = x; PointerY = y; PointerInside = true;

        // 界面捕获了指针（例如按下按钮后滑出去），消息全归界面。
        if (UiCapturing)
        {
            UiPointerMove(screenX, screenY, pressure, false, inverted);   // 逻辑屏幕坐标
            _dirty = true;
            return;
        }

        // 界面优先：悬停也要转发。
        //
        // 悬停高亮、"靠近才长大"、"悬停 120 ms 才浮出"全靠这条。以前只有
        // **捕获分支**会转发，而"鼠标停在按钮上"恰恰是没捕获的状态——
        // 于是界面的悬停永远不会亮，而且不报错，只是"感觉不跟手"。
        // 书写中不转发：那一笔已经归画布了，界面这时候不该再动。
        if (!_drawing && UiPointerMove(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted))
        {
            if (!_uiHover) { _uiHover = true; ApplyCursor(); }
            _dirty = true;
            return;
        }
        if (_uiHover) { _uiHover = false; ApplyCursor(); }

        // 穿透模式：我们不收输入，也不该动光标（那是下层窗口的事）。
        if (PassThrough) { _dirty = true; return; }

        // 拖滚动条：和"画一笔"互斥。
        if (ScrollBarDragging)
        {
            UpdateScrollBarDrag(screenY);
            ApplyCursor();
            _dirty = true;
            return;
        }

        if (!_drawing || id != _activePointer)
        {
            // 悬停路径。以前这里直接 return，"没落笔"时引擎完全不知道指针在哪儿，
            // 于是悬停光标、滚动条悬停、落点预览都无从谈起。
            UpdateScrollBarHover(screenX, screenY);
            UpdateBarHover(x, y);                  // 操作条九格的 hover 态（画与命中同源）
            ApplyCursor();
            if (DrawnCursor != ToolCursorShape.None) _dirty = true;
            return;
        }

        var tool = inverted ? Tool.Eraser : Tool;
        // 手测台：数"指针消息"而不是"擦除步"——消息之间的间隔才是跟不跟手。
        if (tool == Tool.Eraser || tool == Tool.PixelEraser) EraserTelemetry?.Move(NowMs);
        switch (tool)
        {
            case Tool.Eraser:
                EraseAlongPath(x, y);
                break;

            case Tool.PixelEraser:
                EraseRectAlongPath(x, y);
                break;

            case Tool.Capture:
                if (CaptureActive) ExtendCaptureTo(x, y);
                break;

            case Tool.Marquee:
                // 拖着颜色面板里的粗细滑条：只吸档位，不进"整体拖动"。
                if (_sliderDragging) SetWidthStepAt(x, LiveSelectionFrame.CanvasAabb);
                else if (SelDragging) UpdateSelDrag(x, y);
                else ExtendMarqueeTo(x, y);
                break;

            case Tool.Laser:
                Laser.Add(x, y, NowMs);
                break;

            default:
                if (ActiveStroke != null)
                {
                    // 指针报什么坐标就存什么坐标：不做平滑、不做抽稀。
                    // 但**一条消息里的点要全部收下**——系统会把来不及投递的移动合并
                    // （自己实测：注入 140 Hz，应用只收到约 60 条消息，其余在 history 里），
                    // 只取最新那一个等于把笔的采样率砍半（见 Input/PenInput.cs）。
                    AppendStrokeSamples(id, ptype, x, y, pressure);
                    FeedInkTrail(ptype, ActiveStroke.Width * 0.5f, screenX, screenY);
                }
                break;
        }
        // 拖拽中（已捕获）：系统不会再发 WM_SETCURSOR，每帧自己维护。
        ApplyCursor();
        _dirty = true;
    }

    private void OnPointerUp(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntUp++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);

        // 界面优先收尾，否则会留下"按钮一直按着"的状态。
        if (UiCapturing && ReadPointer(id, out float ux, out float uy, out float upressure,
                                       out bool uinverted, out _))
        {
            UiPointerUp(ux, uy, upressure, false, uinverted);
            // **必须显式放开捕获**：按钮上也走 SetCapture（拖出按钮、在按钮上滑开
            // 都要继续收到消息），而系统**不会**在按键抬起时替我们放开。
            // 忘了这一句的后果不是"按钮卡住"，而是**整台机器的鼠标事件都还挂在
            // 我们窗口上**：穿透、点下层程序、换到别的软件全乱，而且现象离原因很远。
            Native.ReleaseCapture();
            UiCapturing = false;
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (ScrollBarDragging)
        {
            EndScrollBarDrag();
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (id != _activePointer) return;
        Native.ReleaseCapture();
        _drawing = false;
        EndStroke();
    }

    /// <summary>
    /// Walks the eraser along the segment the pointer just travelled instead of
    /// only testing the newest position. Without this, a quick flick leaves gaps
    /// between samples and only some of the crossed strokes disappear - which is
    /// what "the eraser feels unresponsive" actually looks like.
    /// </summary>
    private void EraseAlongPath(float x, float y)
    {
        float r = EraserRadius;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, r * 0.5f)), 1, 48);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseAt(ex, ey, r);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    /// <summary>
    /// 像素橡皮沿指针走过的路径扫一遍。理由和整笔橡皮一样：只测最新那一点，快划就会在
    /// 两次采样之间漏掉一整段。步长取**短边的一半**——相邻两个位置至少重叠一半，
    /// 中间不会有缝；上限 64 步，免得一次大跨度拖动把每帧的代价拉爆。
    /// </summary>
    private void EraseRectAlongPath(float x, float y)
    {
        float hw = PixelEraserHalfWidthPx, hh = PixelEraserHalfHeightPx;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, MathF.Min(hw, hh))), 1, 64);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseRectAt(ex, ey, hw, hh);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    // =====================================================================
    //  截图
    //
    //  流程（每一步都有它非这么做不可的理由）：
    //
    //   1. 拖一个框（和框选同一个手势，用户不用学新动作）；
    //   2. **把自己的覆盖层藏起来**再抓屏——不然抓到的就是"批注盖在 PPT 上"
    //      那一坨，等于把老师写的字又贴回去一张；
    //   3. 抓到的是一比一的**物理像素**，按 DPI 折成画布尺寸；
    //   4. 放到**视口左上角**并立刻选中——用户看得见、拖得动、能接着复制/翻转；
    //   5. 同时写进剪贴板：老师要把它贴到别的地方（PPT、微信、Word）是常见动作。
    //  =====================================================================

    /// <summary>
    /// 覆盖层藏起来时用的句柄列表。**多屏也要一起藏**，否则副屏上抓到的还是
    /// 我们自己画的东西（多屏教室很常见）。
    /// </summary>
    private IntPtr[] OverlayHandles()
    {
        var list = new List<IntPtr>(_windows.Count);
        foreach (var w in _windows) list.Add(w.Hwnd);
        // 抓屏时要连"接输入小窗"一起藏：它是 1/255 的一层灰，肉眼看不见，
        // 但拍进图里就是一层脏（截图界面永远不该出现在截图里）。
        if (_uiInputHwnd != IntPtr.Zero) list.Add(_uiInputHwnd);
        return list.ToArray();
    }

    /// <summary>截图落地的位置：**视口左上角**往里缩一点（见 CaptureMargin 的注释）。</summary>
    internal const float CaptureMarginLogical = 24f;

    /// <summary>
    /// "直接截取"抓屏期间：**藏掉取景框、但保留板书**。用 using 包起来，
    /// 异常路径也不会把框永久藏掉（藏着的框=看不见自己画到哪儿，会以为软件坏了）。
    /// </summary>
    private IDisposable HiddenCaptureFrame()
    {
        CaptureFrameHidden = true;
        _dirty = true;
        RenderAll();                 // 先把自己这一帧重画成"没有框"的样子
        Native.DwmFlush();           // 等合成器贴上去，再抓
        System.Threading.Thread.Sleep(60);
        Native.DwmFlush();
        return new CaptureFrameRestore(this);
    }

    private sealed class CaptureFrameRestore : IDisposable
    {
        private readonly InkEngine _e;
        public CaptureFrameRestore(InkEngine e) => _e = e;
        public void Dispose()
        {
            _e.CaptureFrameHidden = false;
            _e._dirty = true;
        }
    }

    private void EndCapture()
    {
        CaptureActive = false;
        _dirty = true;

        // 画布坐标 → 屏幕坐标：相机偏移加回去（ScreenToCanvas 的逆运算）。
        // 相机只有纵向偏移（横向没有滚动），所以 x 不用换算。
        int x = (int)MathF.Round(MathF.Min(CapMinX, CapMaxX));
        int y = (int)MathF.Round(MathF.Min(CapMinY, CapMaxY) + ViewOffsetY);
        int w = (int)MathF.Round(MathF.Abs(CapMaxX - CapMinX));
        int h = (int)MathF.Round(MathF.Abs(CapMaxY - CapMinY));

        // 抓 1 像素宽的框没有意义（多半是"点了一下"），直接当没发生过。
        if (w < 4 || h < 4)
        {
            Console.WriteLine("截图取消：框太小");
            return;
        }

        byte[] pixels;
        if (CaptureHideInk)
        {
            // 隐藏批注截取（默认）：整个覆盖层藏起来，拍到的只有下层内容
            using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                pixels = ScreenCapture.Grab(x, y, w, h);
        }
        else
        {
            // **直接截取**：连板书一起拍，只把取景框藏掉。
            //
            // 框是我们自己画的（浮动层），所以不用藏窗口、也不用等合成器撤窗口——
            // 重画一帧就没了。等一拍再抓，是为了让合成器把这一帧真的贴上去
            // （和 HiddenOverlay 那边同一个道理，只是这里只有一帧的事）。
            using (HiddenCaptureFrame())
                pixels = ScreenCapture.Grab(x, y, w, h);
        }

        // 窗口藏过又显示，后缓冲里的内容是旧的：整层作废重画，
        // 否则屏幕上会留下一块擦不掉的残影。
        Doc.InvalidateAll();

        if (pixels == null)
        {
            Console.WriteLine("截图失败：抓屏返回空");
            return;
        }

        ClipboardImage.SetImage(pixels, w, h);

        var img = ImageData.Adopt(w, h, pixels);
        if (img == null) return;

        // 画布坐标 = 视口左上角 + 一点边距。**边距是必须的**：贴着 (0,0) 的话
        // 选中框的旋转手柄会跑到屏幕外，用户抓不到它。
        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);

        // 落到视口左上角之后可能超出视口（图比屏幕大），把相机拉回去看全它。
        EnsureVisible(placed);
        if (placed != null)
        {
            Doc.SelectOnly(new[] { placed });
            Tool = Tool.Marquee;             // 刚截完图，最可能的下一件事是摆放它
            NotifyUiStateChanged();
        }
        Console.WriteLine($"截图 {w}×{h} 物理像素 → 画布 {w / DpiScale:F0}×{h / DpiScale:F0}，已放到左上角并选中");
    }

    /// <summary>把相机滚到"这个对象看得见"。只做纵向——横向没有滚动这回事。</summary>
    private void EnsureVisible(Stroke s)
    {
        if (s == null) return;
        var vp = ViewportCanvas;
        var b = s.PaddedBounds;
        if (b.MaxY > vp.MaxY) ViewOffsetY += b.MaxY - vp.MaxY;
        else if (b.MinY < vp.MinY) ViewOffsetY += b.MinY - vp.MinY;
        ClampViewOffset();
    }

    /// <summary>
    /// 从剪贴板粘一张图（Ctrl+V）。位置规则和截图一致：**视口左上角**，
    /// 粘完立刻选中。
    /// </summary>
    /// <summary>
    /// 复制选中对象到剪贴板：**对象字节 + 一张图**。
    ///
    /// 对象字节让"粘回来仍是可编辑对象"（跨页、跨窗口、跨程序实例都行）；
    /// 图是给外部程序的兜底（Word / PPT / 微信粘到的是图，至少能用）。
    /// 只放对象不放图也行，但那样"复制一段板书贴到微信"就没反应——两头都要顾。
    /// </summary>
    internal bool CopySelectionToClipboard()
    {
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("复制：没有选中任何东西（先用框选或点选选中）");
            return false;
        }

        byte[] objects = ClipboardInk.Serialize(sel);

        // 同时渲染一张图：范围 = 这批对象的画布范围 + 4 逻辑像素留白
        byte[] dib = null; int w = 0, h = 0;
        var box = EditRegion.Of(sel);
        if (!box.IsEmpty && _windows.Count > 0)
        {
            box = box.Inflate(4f * DpiScale);
            w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            // 防呆：图太大就不放了（对象格式照放）
            if ((long)w * h <= 32_000_000) dib = _windows[0].RenderStrokesToBgra(sel, box, w, h);
            else { w = h = 0; }
        }

        bool ok = ClipboardInk.Set(objects, dib, w, h);
        Console.WriteLine(ok
            ? $"复制 {sel.Count} 个对象（同时放了一张 {w}×{h} 的图：外部程序也粘得上）"
            : "复制失败（剪贴板正被别的程序占着？）");

        // 屏幕上的反馈只有**选区闪一下**（用户反馈的"复制以后看不出来"）。
        // 提示条 2026-09-16 按用户要求去掉了：屏幕上一个字都不留。
        if (ok) SelFlashUntilMs = NowMs + SelFlashMs;
        return ok;
    }

    // ---- 导出（选中 → 透明底 PNG）------------------------------------------
    //
    // 用户 2026-09-17 定：**只导选中的**、**透明底 PNG**、**弹系统"另存为"**，
    // 而且**同时放进剪贴板**（两样都要）。落点与命名按
    // [调研-选中框-反馈-导出-层级-属性.md] 第二节。
    //
    // 屏幕上**不留字**（用户 2026-09-16 明确要求把提示条去掉），只让选区闪一下——
    // 文件对话框本身就是最清楚的反馈。

    /// <summary>
    /// 把选中的内容离屏渲染成一张**透明底** BGRA（和"复制到剪贴板"那条路同一个渲染器）。
    /// 返回 null = 不能导（没选中 / 太大 / 渲染失败），每种情况都有日志。
    /// </summary>
    private byte[] RenderSelectionForExport(out int w, out int h)
    {
        w = h = 0;
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("导出：没有选中任何东西（先用框选或点选选中）");
            return null;
        }
        var box = EditRegion.Of(sel);
        if (box.IsEmpty) return null;
        box = box.Inflate(4f * DpiScale);                     // 和"复制"同一个留白
        w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 32_000_000)
        {
            Console.WriteLine($"导出：选中的内容太大（{w}×{h}）");
            w = h = 0;
            return null;
        }
        if (_windows.Count == 0) { w = h = 0; return null; }

        var bgra = _windows[0].RenderStrokesToBgra(sel, box, w, h);
        if (bgra == null) { Console.WriteLine("导出：离屏渲染失败"); w = h = 0; }
        return bgra;
    }

    /// <summary>
    /// 写 PNG ＋ **同时放进剪贴板**（对象 ＋ 这张图，和 Ctrl+C 同一个剪贴板合同），
    /// 再让选区闪一下。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path)
        => WriteExport(bgra, w, h, path, 1);

    /// <summary>
    /// 写盘。<paramref name="filterIndex"/> = 老师在类型栏选的那一条（决定"透明底 / 白底"），
    /// 编码器按扩展名决定——两者的分工见 `ExportFormats.Encode` 的注释。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path, int filterIndex)
    {
        LastExportPath = null;
        LastExportFilterIndex = 0;
        try
        {
            var bytes = ExportFormats.Encode(path, bgra, w, h, filterIndex, out string tag);
            if (bytes == null) { Console.WriteLine($"导出：{tag} 编码失败"); return false; }
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"导出成功：{path}（{w}×{h}，{tag}，{bytes.Length / 1024.0:F0} KB）");
            LastExportPath = Path.GetFullPath(path);
            LastExportFilterIndex = filterIndex;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"导出失败：{ex.Message}");
            return false;
        }

        // 用户 2026-09-17 更正：**导出不再顺手放进剪贴板**——"复制"那条路已经有了
        // （选中 → Ctrl+C / 操作条上的复制），导出就只管落盘，职责单一、也少一次
        // 剪贴板写入（剪贴板是全局资源，能少碰就少碰）。
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **导出按钮做的事**：离屏渲染 → 弹"另存为" → 写盘。取消 = 什么都不做。
    ///
    /// 格式**就在那个对话框里选**（见 <see cref="ExportFormats"/>：透明底 / 白底 /
    /// 文件大小 / 老软件兼容四种，每种把优势写在类型名里）。我们这边**不再先弹一层
    /// 自己的格式面板**——用户 2026-09-17："那下面这两个图标就没有用了吧，用户都可以
    /// 自己保存图片了"：说得对，两处选格式反而绕，点导出直接出对话框还少一步。
    /// （触摸屏上那个下拉现在也点得开了：真因是覆盖层每秒抢层，见 ReassertTopmost。）
    /// </summary>
    /// <param name="defaultFilterIndex">
    /// 默认选中第几条（1 起）。界面把"上次用的那条"记在偏好里传进来，
    /// 所以常用 JPG 的老师下次不用再选。
    /// </param>
    /// <returns>用户最终选的那一条（1 起）；取消或没导成返回 0。</returns>
    internal int ExportSelection(int defaultFilterIndex = 1)
    {
        ExportAttempts++;
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return 0;

        // 自检模式**绝不弹对话框**：系统对话框会阻塞在那里等消息，
        // 而自检是靠"抽消息 + 渲染"自己推进的——一弹就把整条自检卡死到超时。
        // （自检要验的是"渲染 → 编码 → 写盘 → 剪贴板"这条链，那条由 --iotest 走
        //  ExportSelectionToPathForTest，不经过这里。）
        if (!ExportDialogEnabled)
        {
            Console.WriteLine("导出：自检模式不弹另存为对话框（见 --iotest）");
            return 0;
        }

        // 默认扩展名跟着选的那一条走（第 1 条 PNG 透明底是老师最常要的：
        // "贴到别处不带走白底"）。
        int want = Math.Clamp(defaultFilterIndex, 1, ExportFormats.Count);
        string suggested = $"选中-{DateTime.Now:yyyyMMdd-HHmm}" + ExportFormats.ExtensionFor(want);
        string path = null;
        int chosen = want;
        BorrowFocusForDialog();                 // 覆盖层平时不抢焦点，弹框前临时放开
        ExportDialogOpen = true;                // 弹框期间不许再抬覆盖层，见 ReassertTopmost
        try { path = ExportFileDialog.AskForImage(OwnerHwnd(), suggested, want, out chosen); }
        catch (Exception ex)
        {
            // **弹框这一步出错绝不允许打死软件**。真踩过：.NET 7 起结构体字段不能用
            // StringBuilder，那一版一点"导出"整个进程就没了（异常从 P/Invoke 冒到 Main）。
            // 现在最坏也只是"这次导不出去"，并且把原因说清楚。
            Console.WriteLine($"导出：弹另存为失败（{ex.GetType().Name}: {ex.Message}）");
            return 0;
        }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }

        if (path == null) { Console.WriteLine("导出：取消"); return 0; }
        return WriteExport(bgra, w, h, path, chosen) ? chosen : 0;
    }

    /// <summary>
    /// 导出按钮真正调的那一层：**记住老师上次选的那一条格式**（`ui.exportFormat`），
    /// 下次直接默认它——常用 JPG 的老师不用每次都去下拉里找。
    ///
    /// 这一条偏好由**引擎自己解释**（别的 `ui.*` 都是界面说了算）。理由：操作条
    /// 是引擎那边画和命中测试的（`Selection.cs`），导出按钮压根不经过 `InkUi`，
    /// 所以"记住上次的选择"只能落在这里。键名照旧放在 `ui` 段，不另开一节。
    ///
    /// 注意：自检模式下这条偏好**读不到也写不进**（自检不碰用户配置），
    /// 所以自检里走的是默认第 1 条，判据是确定的。
    /// </summary>
    private void ExportSelectionPref()
    {
        int last = ExportDefaultFilterIndex();
        int chosen = ExportSelection(last);
        if (chosen >= 1) SetUiPref("exportFormat", chosen.ToString());
    }

    /// <summary>
    /// 默认选中第几条格式：读偏好 `ui.exportFormat`，读不出来/越界就回到第 1 条（PNG 透明底）。
    /// 单独抽出来是为了能自检（"记住上次的选择"这条逻辑不依赖对话框）。
    /// </summary>
    internal int ExportDefaultFilterIndex()
        => int.TryParse(GetUiPref("exportFormat"), out int v)
           && v >= 1 && v <= ExportFormats.Count ? v : 1;

    /// <summary>自检用：不弹对话框，直接写到指定路径（其余流程一模一样）。</summary>
    internal bool ExportSelectionToPathForTest(string path, int filterIndex = 1)
    {
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return false;
        return WriteExport(bgra, w, h, path, filterIndex);
    }

    private IntPtr OwnerHwnd() => _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

    /// <summary>
    /// 导出时要不要弹系统的"另存为"。**自检模式必须关掉**（见 <see cref="ExportSelection"/>）：
    /// 系统对话框会阻塞等消息，而自检是自己抽消息推进的，一弹就卡到超时。
    /// 产品里保持 true。
    /// </summary>
    internal bool ExportDialogEnabled = true;

    /// <summary>
    /// "系统对话框正开着"。**只用来按住 <see cref="ReassertTopmost"/>**：
    /// 那一下每秒一次的置顶重抬会去跟对话框抢层，顺手把它的下拉列表关掉
    /// （用户报的"选不到 jpg"就是它）。见 <see cref="ReassertTopmost"/> 的注释。
    /// </summary>
    internal bool ExportDialogOpen;

    /// <summary>
    /// 上一次**真的写出去**的那个文件（全路径；没成功过就是 null），以及它是不是 JPEG。
    ///
    /// `--dialogprobe --save` 靠它确认"对话框 → 路径 → 编码 → 落盘"整条链真的通了，
    /// 而不是只看对话框弹没弹出来。
    /// </summary>
    internal string LastExportPath;
    /// <summary>上一次成功导出用的那一条格式（1 起，见 <see cref="ExportFormats"/>）。</summary>
    internal int LastExportFilterIndex;

    /// <summary>
    /// 试过几次导出（**包括没有选中、被自检挡下、用户取消**）。
    ///
    /// 自检要的"点一下"和"真的弹框"分开：`--edittest` 在自检模式下点导出，
    /// 对话框是关着的，只能靠这个计数器证明那一下真的走到了导出这条路
    /// （而不是"什么都没发生"）。
    /// </summary>
    internal int ExportAttempts;

    // 弹系统对话框期间借一下焦点：把 WS_EX_NOACTIVATE 摘掉、弹完装回去。
    // 和"批注键盘模式"同一个手法（那边是长期摘掉，这里是临时的）。
    private bool _focusBorrowed;

    private void BorrowFocusForDialog()
    {
        if (_focusBorrowed) return;
        _focusBorrowed = true;
        // **同时把"置顶"摘掉**：我们的覆盖层是 WS_EX_TOPMOST + 铺满整屏，
        // 而系统对话框自己的弹层（**文件类型那个下拉列表**）是普通弹窗——
        // 它会被置顶的覆盖层盖住，用户看到的就是"点开一下就收回去、选不到 JPG"
        // （用户 2026-09-17 报的）。弹框期间我们不需要浮在最上面，摘掉就好。
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | 0x0020 /*SWP_FRAMECHANGED*/);
            Native.SetWindowPos(w.Hwnd, new IntPtr(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        if (_windows.Count > 0) Native.SetForegroundWindow(_windows[0].Hwnd);
    }

    private void ReturnFocusAfterDialog()
    {
        if (!_focusBorrowed) return;
        _focusBorrowed = false;
        if (KeyboardMode) return;               // 键盘模式本来就要求能激活，别把它的样式改回去
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            // 把"置顶"装回去（覆盖层平时必须浮在所有程序上面）
            Native.SetWindowPos(w.Hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 粘贴：**优先粘回可编辑对象**——剪贴板里有我们的对象格式就还原成对象，
    /// 没有就退回"当图片粘贴"（原有行为）。落在视口左上角并自动选中，粘完就能拖走。
    /// </summary>
    internal bool PasteFromClipboard()
    {
        if (ClipboardInk.TryGetObjects(out var objs) && objs.Count > 0)
        {
            var vp = ViewportCanvas;
            float m = CaptureMarginLogical * DpiScale;
            var box = EditRegion.Of(objs);
            var move = Matrix3x2.CreateTranslation(vp.MinX + m - box.MinX, vp.MinY + m - box.MinY);
            foreach (var s in objs) s.Transform = s.Transform * move;   // 插入前平移（画布坐标）

            Doc.AddStrokes(objs);          // 一步撤销
            Doc.SelectOnly(objs);
            Tool = Tool.Marquee;
            NotifyUiStateChanged();
            _dirty = true;
            Console.WriteLine($"粘贴 {objs.Count} 个对象（仍是可编辑对象）");
            return true;
        }
        return PasteImageFromClipboard();
    }

    internal bool PasteImageFromClipboard()
    {
        if (!ClipboardImage.TryGetImage(out var pixels, out int w, out int h, out bool hasAlpha))
        {
            Console.WriteLine("粘贴：剪贴板里没有可用的图像（只认 CF_DIB / CF_DIBV5）");
            return false;
        }
        var img = ImageData.Adopt(w, h, pixels, hasAlpha);
        if (img == null) return false;

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);
        EnsureVisible(placed);
        Doc.SelectOnly(new[] { placed });
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
        _dirty = true;
        Console.WriteLine($"粘贴图片 {w}×{h}");
        return true;
    }

    private void EndStroke()
    {
        if (ScrollBarDragging) EndScrollBarDrag();
        foreach (var w in _windows) w.EndInkTrail();
        Doc.EndErase();
        if (CaptureActive)
        {
            EndCapture();
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        if (ActiveStroke != null)
        {
            if (ActiveStroke.Points.Count > 0)
            {
                Doc.AddStroke(ActiveStroke);
                _lastStrokeReport =
                    $"采集到 {ActiveStroke.Points.Count} 个点"
                    + $"，收到 按下{_cntDown} 移动{_cntMove} 抬起{_cntUp} 丢失捕获{_cntCaptureLost}"
                    + $"，设备={PointerTypeName(_activePointerType)}"
                    + $"，压感={(ActiveStrokeHasPressure ? "有" : "无")}"
                    + $"，合并({LastCoalescedMessages} 条消息 → {LastCoalescedSamples} 个采样点)";
                Console.WriteLine("[笔画] " + _lastStrokeReport);
            }
            ActiveStroke = null;
        }
        if (Tool == Tool.Marquee)
        {
            if (_sliderDragging) _sliderDragging = false;      // 滑条松手：只是停，不用收尾
            else if (SelDragging) EndSelDrag();
            else ApplyMarquee();
        }
        _drawing = false;
        _dirty = true;
        _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
        // 手测台：一次拖拽收尾。**必须在 EndErase 之后**——撤销栈深度要算上刚提交的那一步。
        EraserTelemetry?.EndDrag(Doc, Doc.UndoDepth, NowMs);
    }

    private static string PointerTypeName(uint t) => t switch
    {
        Native.PT_MOUSE => "鼠标",
        Native.PT_TOUCH => "触摸",
        Native.PT_PEN => "笔",
        _ => "未知",
    };

    // =====================================================================
    //  指针形状（光标）
    //
    //  一条规则：**每次指针移动都要能回答"这个位置、这个工具、这个设备，
    //  用户接下来能做什么"**。回答不了就退到箭头，绝不留空——留空就是
    //  系统兜底，也就是这次修的"永远转圈"。
    //
    //  优先级（上面的压下面的）：
    //    1. 穿透模式   → 不插手，交给下层窗口
    //    2. 触摸       → 不显示指针
    //    3. 拖拽中     → 用按下那一刻的语义（此时系统不再发 WM_SETCURSOR）
    //    4. 滚动条 / 选中框手柄 / 操作条
    //    5. 工具       → 画布上的形状
    // =====================================================================

    internal enum ToolCursorShape
    {
        None,
        /// <summary>圆环：橡皮、手写笔悬停。直接表达"落点范围内会怎样"。</summary>
        Ring,
        /// <summary>
        /// 矩形：像素橡皮。它的落点**就是一个矩形**（竖着的黄金比例），
        /// 所以光标不该画成圆——画成圆等于告诉用户一个错的形状。
        /// </summary>
        Rect,
        /// <summary>
        /// 实心圆盘：荧光笔。落点处就是一个直径 = 笔宽的圆——单击一下留下的
        /// 墨点也是它，所以"光标"和"点下去会得到什么"是同一个形状。
        /// </summary>
        Disc,
        /// <summary>实心点：激光笔。激光表达的是"我说的是这里"，不是"多宽"。</summary>
        Dot,
        /// <summary>
        /// 取景框（四个角括号 + 中心小十字）：截图工具。
        ///
        /// 为什么单独给它一个：截图 / 框选 / 图形原来**共用一个系统十字**
        /// （`ToolCursorKind` 里那一行 `Marquee or Capture or Line or ...`），
        /// 老师分不出"我现在是要截图还是要选框"——用户 2026-09-17 当场点了这一条。
        /// 取景框的角括号和"拖出来一个框"是同一个意思，一眼就分得开。
        /// </summary>
        Frame,
    }

    private IntPtr _cursorApplied;
    private int _cursorSizePx, _cursorSizeDpi;

    /// <summary>
    /// 系统指针的物理像素尺寸（跟随 DPI 与用户的"指针大小"辅助功能设置）。
    /// 本机 200% 缩放下是 64。自绘光标按它生成，才不会在高分屏上糊掉。
    /// </summary>
    internal int CursorSizePx
    {
        get
        {
            int dpi = Math.Max(96, (int)MathF.Round(96f * DpiScale));
            if (_cursorSizePx == 0 || dpi != _cursorSizeDpi)
            {
                int v = Native.GetSystemMetricsForDpi(Native.SM_CXCURSOR, (uint)Math.Min(dpi, 768));
                _cursorSizePx = v > 0 ? v : (int)(32 * DpiScale);
                _cursorSizeDpi = dpi;
            }
            return _cursorSizePx;
        }
    }

    /// <summary>当前该显示什么光标。纯函数（不碰系统），所以能拿来做自检。</summary>
    internal CursorKind ComputeCursorKind()
    {
        // 指针停在界面自己那一块上：给箭头。
        // 界面上的按钮不该顶着一个笔尖圈/橡皮圈——那一圈是"落点反馈"，
        // 只对画布有意义。
        if (_uiHover && !_drawing) return CursorKind.Default;

        if (PassThrough) return CursorKind.Leave;                 // 谁来接管由系统决定
        if (LastPointerType == Native.PT_TOUCH) return CursorKind.Hidden;

        if (_drawing)
        {
            // 拖拽中不重新做命中测试：指针早就离开手柄了，重测会让光标在半路变回去。
            if (Tool == Tool.Marquee && SelDragging)
                return _dragIsMove ? CursorKind.Move : HandleCursor(_dragHandle);
            return ToolCursorKind;
        }

        // 滚动条：不换光标。手型按规范只能表示链接；四向箭头又会被读成"移动/拉伸"。
        // "能拖"这件事交给滑块自己的悬停反馈（变粗、变深）去说。
        if (ScrollBarHover) return CursorKind.Default;

        if (Tool == Tool.Marquee && Doc.Selected.Count > 0)
        {
            var k = SelectionCursor(PointerX, PointerY);
            if (k.HasValue) return k.Value;      // null = 这一带没有特殊语义，交给工具
        }

        return ToolCursorKind;
    }

    /// <summary>画布上的工具光标（不含滚动条、操作条、手柄）。</summary>
    private CursorKind ToolCursorKind => Tool switch
    {
        // 现在四种工具都自己画落点反馈，所以系统光标一律藏起来——
        // 自绘落点 + 系统光标叠在一起是"箭头套圆环"，很难看（见 DrawnCursor 的注释）。
        // 以前笔在鼠标下用的是系统十字（没有宽度信息），那正是这次要补的。
        Tool.Pen => CursorKind.Hidden,
        Tool.Highlighter => CursorKind.Hidden,      // 落点由自绘的宽度圆盘表达
        Tool.Laser => CursorKind.Hidden,            // 落点由自绘的实心点表达
        // 落点由自绘圆环 / 矩形表达；开了 EraserKeepsSystemCursor 就两个都显示（A/B 用）。
        Tool.Eraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        Tool.PixelEraser => EraserKeepsSystemCursor ? CursorKind.Default : CursorKind.Hidden,
        // 截图有**自己的**落点形状（取景框角括号，见 ToolCursorShape.Frame），
        // 所以系统光标藏起来——不然就是"十字 + 角括号"叠在一起。
        Tool.Capture => CursorKind.Hidden,
        // 框选 / 图形仍然用十字准星："从这儿拖到那儿"的通用语言。
        Tool.Marquee or Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Arrow
            => CursorKind.Cross,
        _ => CursorKind.Default,
    };

    /// <summary>
    /// 选中框上的光标（含操作条、八个手柄、旋转手柄、框内拖动）。
    /// 返回 null = 指针不在选中框这一带，调用方继续往下问工具。
    ///
    /// 用可空值而不是"返回 Default 表示没有"：Default（箭头）本身也是一个
    /// **明确答案**（比如操作条按钮上就该是箭头），两者混在一起写，
    /// 就会出现"按钮上反而露出手柄光标"这种错。
    /// </summary>
    private CursorKind? SelectionCursor(float canvasX, float canvasY)
    {
        var frame = SelectionHandles.FrameOf(Doc.Selected);
        float dpi = DpiScale;
        var aabb = frame.CanvasAabb;

        // **操作条是"一块"，不只是几个按钮**（用户 2026-09-17 报的："操作对应图标的时候
        // 一会是十字光标，一会是箭头图标"）。
        //
        // 原来只认按钮本体（`BarButtonAt`），按钮之间的分隔线、两头的留白都不算，
        // 于是那些位置上就退回工具光标——框选工具是十字，鼠标在条上横着滑过去就是
        // 箭头／十字／箭头／十字。条在视觉上是一整块白色胶囊，指针落在它上面就该是箭头。
        // 收起态那颗圆钮、以及挂在条下面的小面板（颜色/粗细、层级、导出）同理。
        if (SelBarCollapsed)
        {
            if (SelectionHandles.BarCollapsedRect(aabb, dpi, ViewportCanvas).Contains(canvasX, canvasY))
                return CursorKind.Default;
        }
        else
        {
            if (SelectionHandles.BarRect(aabb, dpi, ViewportCanvas).Contains(canvasX, canvasY))
                return CursorKind.Default;
            if (SelPanelOpen != SelPanel.None
                && SelectionHandles.PanelContains(canvasX, canvasY, aabb, dpi, ViewportCanvas,
                                                  SelPanelOpen, SelectionHandles.SwatchCount))
                return CursorKind.Default;
        }

        var h = SelectionHandles.HitTest(canvasX, canvasY, frame, dpi);
        if (h != SelHandle.None) return HandleCursor(h);

        var lp = frame.ToLocalPoint(new Vector2(canvasX, canvasY));
        bool inside = lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
                   && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY;
        return inside ? CursorKind.Move : null;
    }

    private static CursorKind HandleCursor(SelHandle h) => h switch
    {
        SelHandle.TopLeft or SelHandle.BottomRight => CursorKind.ResizeNWSE,
        SelHandle.TopRight or SelHandle.BottomLeft => CursorKind.ResizeNESW,
        SelHandle.Left or SelHandle.Right => CursorKind.ResizeWE,
        SelHandle.Top or SelHandle.Bottom => CursorKind.ResizeNS,
        SelHandle.Rotate => CursorKind.Rotate,
        _ => CursorKind.Default,
    };

    /// <summary>把当前该有的光标设上。重复调用是安全的（同一个句柄不重复设）。</summary>
    internal void ApplyCursor(bool force = false)
    {
        var kind = ComputeCursorKind();
        if (kind == CursorKind.Leave) return;          // 不插手
        var h = Cursors.HandleFor(kind, CursorSizePx);
        if (h == IntPtr.Zero) return;
        if (!force && h == _cursorApplied) return;
        Native.SetCursor(h);
        _cursorApplied = h;
    }

    /// <summary>
    /// 自己要画落点反馈（橡皮圆环、笔尖环、荧光笔圆盘）时，必须把系统光标藏起来，
    /// 否则就是"箭头 + 圆环"叠在一起。触摸不画：手指没有悬停，画了会留在屏幕上。
    /// </summary>
    internal ToolCursorShape DrawnCursor
    {
        get
        {
            // CaptureFrameHidden = "正在抓屏的那一瞬"：取景框和**落点光标**都不画。
            // 光标是琥珀色的，留着它就会被"直接截取"拍进图里（自检抓到过：图片最外一圈
            // 多出 15 个琥珀像素——拖框正好收在角上，光标就压在那一角）。
            if (PassThrough || !PointerInside || LastPointerType == Native.PT_TOUCH
                || CaptureFrameHidden)
                return ToolCursorShape.None;

            // 指针停在面板上（接输入小窗接管了）：这一圈落点反馈该消失。
            // 不判这一条的话，指针移到面板上之后，覆盖层收不到任何指针消息，
            // 上一帧的圆环会**留在屏幕上不动**——看起来就像卡住了。
            if (_uiHover) return ToolCursorShape.None;

            // 规则一句话：**鼠标没有笔尖，所以悬停和书写都要画**；
            // **手写笔的笔尖本身就是落点**，一落笔就不该再跟一个圈
            // （会把手写的位置挡住，而且笔尖和圈的中心差一两像素时看着像错位）。
            bool penTip = LastPointerType == Native.PT_PEN;

            switch (Tool)
            {
            case Tool.Eraser:
                // 橡皮反过来：它表达的是"这一块会被擦掉"，擦除中更要看得到。
                return ToolCursorShape.Ring;
            case Tool.PixelEraser:
                // 同理：矩形要一直看得见，擦除中更要说清"这一块正在被擦"。
                return ToolCursorShape.Rect;
            case Tool.Capture:
                // 截图：取景框角括号。**正在拖的时候不画**——那时取景框本身就是反馈，
                // 再叠一个跟着走的角括号只是噪音（用户 2026-09-17："光标配合也感觉不好"）。
                return CaptureActive ? ToolCursorShape.None : ToolCursorShape.Frame;
                case Tool.Pen:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Ring;
                case Tool.Highlighter:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Disc;
                case Tool.Laser:
                    return penTip && _drawing ? ToolCursorShape.None : ToolCursorShape.Dot;
                default:
                    return ToolCursorShape.None;
            }
        }
    }

    /// <summary>自绘落点反馈的半径（像素）：脏区按它扩，不然快速移动会留残影。</summary>
    internal float DrawnCursorRadius
    {
        get
        {
            switch (DrawnCursor)
            {
                case ToolCursorShape.Ring:
                    return CursorOuterRadius * 1.35f + 10f;
                case ToolCursorShape.Disc:
                    // 圆盘：脏区按半径算，再留出描边和抗锯齿的边。
                    return MathF.Max(HighlighterWidthLogical * DpiScale * 0.5f, 2f) + 12f;
                case ToolCursorShape.Rect:
                    // 矩形：按**半对角线**扩，四个角才不会在快速移动时留残影。
                    return MathF.Sqrt(PixelEraserHalfWidthPx * PixelEraserHalfWidthPx
                                    + PixelEraserHalfHeightPx * PixelEraserHalfHeightPx) + 12f;
                case ToolCursorShape.Dot:
                    return CursorDotRadius * 1.5f + 10f;
                case ToolCursorShape.Frame:
                    // 取景框：半对角线（角括号撑在四角）再留一点
                    return 12f * DpiScale * 1.4143f + 10f;
                default:
                    return 0f;
            }
        }
    }

    /// <summary>
    /// 笔/激光落点反馈的**真实**半径（像素）：就是"这一笔有多粗"的一半。
    /// 细笔（1.5 逻辑像素）不能被"看得见的下限"撑掉——那会让人误判笔宽，
    /// 所以下限由外层那个固定尺寸的圈负责（见 Overlay.DrawRingCursor）。
    /// </summary>
    internal float CursorRingTrueRadius => CurrentToolWidthLogical * 0.5f * DpiScale;

    /// <summary>
    /// 激光轨迹的粗细（像素）。**默认值和以前一模一样**：以前轨迹写死
    /// "芯 5 像素 + 外发光 26 像素"，和任何宽度设置都无关；现在按激光自己的
    /// 粗细缩放（0.625 这个系数就是让默认 4 逻辑像素 × 2 倍屏 = 5 像素，
    /// 外观不变），这样切粗细对激光才是有意义的。
    /// </summary>
    internal float LaserCorePx => MathF.Max(2f, LaserWidthLogical * DpiScale * 0.625f);
    internal float LaserGlowPx => LaserCorePx * 5.2f;

    /// <summary>
    /// 激光落点的半径（像素）。取"轨迹头部的粗细"（细激光有个可见下限），
    /// 这样点和它拖出来的尾巴是同一个尺寸，看起来是一支笔而不是"点上加条线"。
    /// </summary>
    internal float CursorDotRadius
        => MathF.Max(LaserCorePx, Cursors.DotMinRadiusLogical * DpiScale);

    /// <summary>
    /// 落点反馈**外圈**的半径（像素）。细笔的真实半径只有 1.5 像素，
    /// 单画那个圈等于没画，所以外面再套一个固定尺寸的圈：
    ///   内圈 = 真实笔宽（1:1，"我写出来就这么粗"）
    ///   外圈 = 最小可见尺寸（"落点在这儿"）
    /// 橡皮本来就只表达范围，只有一个圈。
    /// </summary>
    internal float CursorOuterRadius => Tool == Tool.Eraser
        ? EraserRadius
        : MathF.Max(CursorRingTrueRadius, Cursors.RingMinRadiusLogical * DpiScale);

    /// <summary>
    /// 订一次"指针离开窗口"的通知。覆盖层以前完全没订过，所以 PointerInside
    /// 永远停在 true——手移开之后落点环会留在屏幕上。
    /// </summary>
    private void TrackPointerLeave(IntPtr hWnd)
    {
        var tme = new Native.TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
            dwFlags = Native.TME_LEAVE,
            hwndTrack = hWnd,
        };
        Native.TrackMouseEvent(ref tme);
    }

    // =====================================================================
    //  滚动条（拖动 + 悬停）
    //
    //  以前只有滚轮能滚：滚动条是**画出来的、拖不动的东西**。光标这一题的
    //  前提是"滑块真的能拖"，所以在这里补上。形状仍然是箭头（理由见调研文档
    //  5.3：手型只能表示链接，四向箭头会被读成缩放/移动）。
    // =====================================================================

    /// <summary>命中宽度（逻辑像素）。滑块只有 4 像素宽，按 4 像素判定等于点不中。</summary>
    private const float ScrollBarGrabLogical = 16f;

    /// <summary>上下夹住相机偏移（滚轮和拖滚动条共用）。</summary>
    internal void ClampViewOffset()
    {
        ViewOffsetY = ClampOffset(ViewOffsetY);
    }

    /// <summary>把一个相机偏移夹进合法范围（翻页要"先算目标再决定动不动"，所以抽出来）。</summary>
    private float ClampOffset(float y)
    {
        var extent = CanvasExtent;
        float lowest = _virtualH - extent.MaxY;
        if (y > 0f) y = 0f;
        if (y < lowest) y = lowest;
        return y;
    }

    // ---- 整屏翻页（"一屏 = 一页"）-----------------------------------------
    //
    // 用户 2026-09-16 选的方案（见 调研-白板翻页.md）：**还是同一张连续画布**，
    // 只是相机**按整屏跳**——上一屏/下一屏各走一个视口高，翻完屏幕上不留半行字。
    //
    // 为什么不做"每页一套笔迹"（InkClass 那种）：它的"页"主要是为了跟 PPT 对齐，
    // 我们暂时不接 PPT；而且它为此付出了"坐标物化 + 按页记偏移"的代价（它的注释写着
    // 切回原页会"位置对不上、滚上去的内容再也滚不回来"）。我们只动相机，没有这些问题。

    private double _camFrom, _camTo, _camStartMs;
    private bool _camAnimating;
    private const double CamAnimMs = 167;      // 和界面同一套时长（167ms 是 Windows 的 Direct Entrance）

    /// <summary>
    /// 翻一屏。<paramref name="down"/> = 往下翻（内容上移、偏移变负，和滚轮同一套符号）。
    /// 返回是否真的翻了（已经在顶/底就不动）。
    /// </summary>
    internal bool FlipPage(bool down)
    {
        float want = ClampOffset(down ? ViewOffsetY - _virtualH : ViewOffsetY + _virtualH);
        if (Math.Abs(want - ViewOffsetY) < 1f) return false;
        return AnimateCameraTo(want);
    }

    /// <summary>
    /// 翻页动画：167ms、快出缓停（和面板的展开同一套曲线）。
    ///
    /// **每帧推进一次**，由主循环调。自检的 `SettleFrames` 也调同一份——
    /// 自检是手动抽帧、不走主循环，少这一句就会量出"翻页返回 true 但相机
    /// 一直停在起点"（`--pagetest` 第一版就是这么自己把自己骗了一次）。
    /// </summary>
    internal void StepCameraAnim()
    {
        if (!_camAnimating) return;
        double t = (NowMs - _camStartMs) / CamAnimMs;
        if (t >= 1.0) { ViewOffsetY = (float)_camTo; _camAnimating = false; }
        else
        {
            float k = 1f - MathF.Pow(1f - (float)t, 3f);
            ViewOffsetY = (float)(_camFrom + (_camTo - _camFrom) * k);
        }
        _dirty = true;
    }

    /// <summary>当前在第几屏（1 起）。相机偏移除以视口高 + 1。</summary>
    internal int ScreenIndex => (int)Math.Round(-ViewOffsetY / Math.Max(1f, _virtualH)) + 1;

    /// <summary>还能不能往上翻（到顶了就不行；往下永远可以——画布下面永远多一屏）。</summary>
    internal bool CanFlipPageUp => ViewOffsetY < -1f;

    /// <summary>
    /// **画布上某个 y 属于第几页**（白板空间，0 起）。一页 = 一个视口高，
    /// 第一页的顶 = 虚拟桌面顶（和页界线、底纹共用同一个原点）。
    ///
    /// 页是"画布上的一段纵向区域"，所以它**永远是现算的**——不存任何"页表"，
    /// 也没有"最多 101 页"这种硬顶（InkClass 有，见 计划-白板与PPT-页逻辑.md 第 1.4 节）。
    /// </summary>
    internal int PageOfCanvasY(float y)
        => (int)MathF.Floor((y - PageTopCanvas) / PageHeightCanvas);

    /// <summary>
    /// **现在看着的是第几页**。取视口**中心**所在的页，而不是上边界：
    /// 滚轮细滚到一半（上边界正好压在页界线上）时，"我在哪一页"应该跟着
    /// 看得最多的那一片走，否则清空会清掉上一页。
    /// </summary>
    internal int CurrentPage
    {
        get
        {
            float top = PageTopCanvas - ViewOffsetY;        // 视口上沿在画布坐标里的位置
            return PageOfCanvasY(top + PageHeightCanvas * 0.5f);
        }
    }

    /// <summary>
    /// "一页"在**画布坐标**里的高 = 一个视口高；第一页的顶 = 虚拟桌面顶。
    /// 页界线（白板模式下画的那条淡线）按这两个值算，所以它**固定在画布上**、
    /// 不随相机动——能被烘进分块缓存，平时零成本。
    /// </summary>
    internal float PageHeightCanvas => _virtualH;
    internal float PageTopCanvas => _virtualY;
    /// <summary>底纹的横向锚点（第一页的左边界）——和页界线共用同一个原点。</summary>
    internal float PageLeftCanvas => _virtualX;

    /// <summary>
    /// 系统的"在 Windows 中显示动画"开关（`SPI_GETCLIENTAREAANIMATION`）。
    /// 关掉时所有动效直接跳终态——教室老机器上常关，设计文稿里也把这条列为硬规范。
    /// 取不到就当作"开着"（跟系统默认一致）。
    /// </summary>
    private bool? _animOn;
    internal bool ClientAreaAnimationOn
    {
        get
        {
            if (_animOn == null)
            {
                int v = 1;
                try
                {
                    if (!Native.SystemParametersInfo(Native.SPI_GETCLIENTAREAANIMATION, 0, ref v, 0)) v = 1;
                }
                catch { v = 1; }
                _animOn = v != 0;
            }
            return _animOn.Value;
        }
    }

    private bool TryBeginScrollBarDrag(float screenX, float screenY)
    {
        var w = WindowAt(screenX, screenY);
        if (w == null || !w.TryScrollBar(this, out var sb)) return false;
        if (!sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f)) return false;

        // 点在轨道上（滑块以外）：先把滑块中心挪到指针处，再当成"滑块内拖动"。
        // macOS / Figma 都是这个手感：点哪儿滚到哪儿，比"翻一页"更符合直觉。
        if (screenY < sb.ThumbTop || screenY > sb.ThumbTop + sb.ThumbLen)
        {
            float want = Math.Clamp(screenY - sb.ThumbLen * 0.5f, sb.Top, sb.Top + sb.MaxTravel);
            ApplyScrollBarThumbTop(w, sb, want);
            w.TryScrollBar(this, out sb);
        }

        ScrollBarDragging = true;
        ScrollBarHover = true;
        ScrollBarActiveAtMs = NowMs;
        _scrollWindow = w;
        _scrollGrabDy = screenY - sb.ThumbTop;
        return true;
    }

    private void UpdateScrollBarDrag(float screenY)
    {
        if (_scrollWindow == null) return;
        if (!_scrollWindow.TryScrollBar(this, out var sb)) return;
        float want = Math.Clamp(screenY - _scrollGrabDy, sb.Top, sb.Top + sb.MaxTravel);
        ApplyScrollBarThumbTop(_scrollWindow, sb, want);
    }

    private void EndScrollBarDrag()
    {
        ScrollBarDragging = false;
        _scrollWindow = null;
    }

    /// <summary>滑块顶边 → 相机偏移。绘制时那段换算的逆运算（见 Overlay.DrawScrollBar）。</summary>
    private void ApplyScrollBarThumbTop(OverlayWindow w, in OverlayWindow.ScrollBarLayout sb, float thumbTop)
    {
        if (sb.MaxTravel <= 0.001f) return;
        float frac = Math.Clamp((thumbTop - sb.Top) / sb.MaxTravel, 0f, 1f);
        float viewTop = sb.ExtentMinY + frac * (sb.ExtentH - w.Height);
        ViewOffsetY = w.OriginY - viewTop;
        ClampViewOffset();
        ScrollBarActiveAtMs = NowMs;
        _dirty = true;
    }

    /// <summary>指针是否停在滚动条上。停在上面就不该淡出，所以每次移动和每次心跳都要问。</summary>
    internal void UpdateScrollBarHover(float screenX, float screenY)
    {
        bool hover = false;
        if (!PassThrough && !ScrollBarDragging)
        {
            var w = WindowAt(screenX, screenY);
            if (w != null && w.TryScrollBar(this, out var sb))
                hover = sb.HitTest(screenX, screenY, ScrollBarGrabLogical, w.Dpi / 96f);
        }

        if (hover != ScrollBarHover)
        {
            ScrollBarHover = hover;
            _dirty = true;                      // 滑块要从 4px 变到 9px
        }
        if (hover || ScrollBarDragging) ScrollBarActiveAtMs = NowMs;
    }

    /// <summary>
    /// 心跳（250ms 一次）里按鼠标位置刷新滚动条悬停。没有这一步，指针停在滑块上
    /// 不动时没人通知，滑块会自己淡出——用户会觉得"这玩意儿点不中"。
    /// </summary>
    private void UpdateScrollBarHoverFromCursor()
    {
        if (PassThrough || ScrollBarDragging) return;
        if (!Native.GetCursorPos(out var p)) return;
        UpdateScrollBarHover(p.X, p.Y);
    }

    /// <summary>找出包含某个屏幕坐标的覆盖窗口（多屏时用）。</summary>
    private OverlayWindow WindowAt(float screenX, float screenY)
    {
        foreach (var w in _windows)
        {
            if (screenX >= w.OriginX && screenX < w.OriginX + w.Width &&
                screenY >= w.OriginY && screenY < w.OriginY + w.Height)
                return w;
        }
        return _windows.Count > 0 ? _windows[0] : null;
    }

    /// <summary>
    /// 把这条指针消息里的**全部**采样点追加进当前笔画。
    ///
    /// 真笔走合并点（`GetPointerPenInfoHistory`，见 <c>Input/PenInput.cs</c>）：
    /// 系统会把来不及投递的移动合并进一条消息，只取最新那一个等于把采样率砍半。
    /// 非笔设备、或读不到合并点时退回"一个消息一个点"的老路（行为与以前完全一致）。
    ///
    /// 坐标：笔画存**画布**坐标；预测器喂**屏幕**坐标（湿墨轨迹也是屏幕空间）。
    /// </summary>
    private void AppendStrokeSamples(uint id, uint ptype, float curCanvasX, float curCanvasY, float curPressure)
    {
        if (ActiveStroke == null) return;

        if (ptype != Native.PT_PEN || _pen.Read(id, NowMs, _lastInputMsgQpc) == 0)
        {
            ActiveStroke.AddPoint(curCanvasX, curCanvasY, curPressure, NowMs);
            return;
        }

        LastCoalescedMessages++;
        LastCoalescedSamples += _pen.Count;
        PenMessages++;
        PenSamples += _pen.Count;
        PenSawPressureMask |= _pen.AnyPressure;
        PenSawTiltMask |= _pen.HasTilt;
        PenSawRotationMask |= _pen.Last.Rotation != 0;
        for (int i = 0; i < _pen.Count; i++)
        {
            var s = _pen[i];
            float cx = s.X, cy = s.Y;
            ScreenToCanvas(ref cx, ref cy);
            ActiveStroke.AddPoint(cx, cy, s.Pressure, s.TimeMs);
            _predictor.Add(s.X, s.Y, s.TimeMs);
            PenTotalPoints++;
            if (s.HasPressure) PenPressurePoints++;
        }
        ActiveStrokeHasPressure |= _pen.AnyPressure;
    }

    /// <summary>
    /// 湿墨：把这一条消息里的真实点（屏幕坐标）连同**预测点**一起交给系统合成器。
    ///
    /// 预测只作用于湿墨——它画的是"正在写的这一笔"的最后一小段，真实点一到就被覆盖，
    /// 不进存档、也不会变成一条真的笔画。这是"不甩墨"的第一道保险。
    /// </summary>
    private void FeedInkTrail(uint ptype, float radius, float screenX, float screenY)
    {
        if (ptype != Native.PT_PEN || !OverlayWindow.InkTrailEnabled) return;
        var win = WindowAt(screenX, screenY);
        if (win == null) return;

        int realCount = 0;
        if (_pen.Count > 0)
        {
            for (int i = 0; i < _pen.Count && realCount < _trailReal.Length; i++)
                _trailReal[realCount++] = new Vector2(_pen[i].X, _pen[i].Y);
        }
        if (realCount == 0) _trailReal[realCount++] = new Vector2(screenX, screenY);

        int predCount = 0;
        if (PredictEnabled)
        {
            int n = _predictor.Predict(_predBuf);
            for (int i = 0; i < n && predCount < _trailPred.Length; i++)
                _trailPred[predCount++] = new Vector2(_predBuf[i].X, _predBuf[i].Y);
        }

        if (predCount > 0)
        {
            float lead = Vector2.Distance(_trailReal[realCount - 1], _trailPred[predCount - 1]);
            PredLeadSum += lead;
            PredLeadCount++;
            if (lead > PredLeadMax) PredLeadMax = lead;
        }

        win.AddInkTrailPoints(_trailReal, realCount, _trailPred, predCount, radius);
    }

    private void ApplyMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;

        // 选择方式是套索时走另一条判据（80% 在内 + 贴边无限延伸），见 ApplyLassoSelection。
        if (SelMode == SelectMode.Lasso) { ApplyLassoSelection(); return; }

        float l = MqMinX, t = MqMinY, r = MqMaxX, b = MqMaxY;
        if (r - l < 4 || b - t < 4)
        {
            // 这是一次**点击**，不是拖框。单击空白处就该取消选中。
            // 之前这里直接 return、什么都不做，用户得点两下才取消，很别扭。
            Doc.Selected.Clear();
            _dirty = true;
            return;
        }

        Doc.ApplyMarquee(new RectF { MinX = l, MinY = t, MaxX = r, MaxY = b });
        // **不在这里自动拆开**。原来这里调了 SplitErasedSelection()，被否掉了：
        // 拆成两个对象会让半透明荧光笔在"两截互相穿过"的地方**混合两次、颜色变深**——
        // 那等于"我什么都没擦，只是框选了一下，墨却变了"。原则是**擦除只是像素没了**：
        // 除了被擦掉的那一块，其他像素在任何操作前后都该一模一样。
        // 想单独摆弄某一截时用显式动作（Ctrl+Alt+8 拆开擦断的笔迹），见 RunAction。
        Console.WriteLine($"marquee selected {Doc.Selected.Count} strokes");
        _dirty = true;
    }

    /// <summary>
    /// 切换框选工具下"拖空白处"的方式：矩形框 ↔ 自由套索。
    ///
    /// **只切方式，不换工具**——用户按这个键的时候手上可能还拿着笔（正在写），
    /// 换工具会顺手把选中清掉（见 SwitchTool），那是他没要的副作用。
    /// 方式不落盘：和工具尺寸一样活在内存里，重开就是默认的矩形。
    /// </summary>
    private void ToggleSelectMode()
    {
        SetSelectMode(SelMode == SelectMode.Lasso ? SelectMode.Rect : SelectMode.Lasso);
    }

    /// <summary>
    /// 切到指定的选择方式。键盘那一路（Ctrl+Alt+9）和界面那一路
    /// （<see cref="SetSelectModeFromUi"/>）**都走这里**，免得两条路各写一套规则。
    /// </summary>
    private void SetSelectMode(SelectMode mode)
    {
        SelMode = mode;
        // 半路切就把没画完的圈丢掉，免得下一次按下接在旧路径后面。
        LassoPath.Clear();
        MarqueeActive = false;
        Console.WriteLine(mode == SelectMode.Lasso
            ? "选择方式：自由套索（圈住 80% 就算选中；圈到屏幕边＝当作无限延伸）"
            : "选择方式：矩形框（碰到墨就算选中）");
        if (Tool != Tool.Marquee)
            Console.WriteLine("  （现在不是框选工具：按 Ctrl+Alt+5 换到框选才用得上）");
    }

    /// <summary>自检用：切一次选择方式（等同于按一下 Ctrl+Alt+9）。</summary>
    internal void ToggleSelectModeForTest() => ToggleSelectMode();

    /// <summary>
    /// 框选工具按下时起一个新手势。**抽出来是为了自检能走到同一段代码**
    /// （见 <see cref="MarqueeDragForTest"/>）：合成鼠标在锁屏 / 被别的程序占着捕获时
    /// 进不来，那几条自检只能记跳过——而这段接线恰恰是最容易写错的地方。
    /// 修饰键由调用方读键盘后传进来，判定函数保持纯逻辑。
    /// </summary>
    private void BeginMarqueeAt(float x, float y, bool shift, bool alt)
    {
        MarqueeActive = true;
        _mqAnchorX = x; _mqAnchorY = y;
        MqMinX = MqMaxX = x; MqMinY = MqMaxY = y;
        // 套索：路径从按下这一点开始；修饰键也在这一刻记下来（松手时用它决定加选/减选）。
        LassoPath.Clear();
        LassoLive = new Vector2(x, y);
        if (SelMode == SelectMode.Lasso) LassoPath.Add(LassoLive);
        _lassoAdditive = shift;
        _lassoSubtractive = alt;
    }

    /// <summary>
    /// 拖框中一路移动。
    ///
    /// 矩形：**锚点 ↔ 当前点**（四个方向都能拉，往回走框跟着缩回去）。
    /// 套索：抽稀后收进路径，同时记下指针当前位置给预览用（见 <see cref="LassoLive"/>）。
    /// </summary>
    private void ExtendMarqueeTo(float x, float y)
    {
        if (SelMode != SelectMode.Lasso)
        {
            // 矩形框 = 从锚点拉到当前点的那一个矩形。**不是**"扫过的最大范围"。
            MqMinX = MathF.Min(_mqAnchorX, x); MqMaxX = MathF.Max(_mqAnchorX, x);
            MqMinY = MathF.Min(_mqAnchorY, y); MqMaxY = MathF.Max(_mqAnchorY, y);
            return;
        }

        // 套索：间距 < 3 逻辑像素的点对判据没有贡献，只会把多边形从几十个顶点撑到几千个，
        // 而每条笔迹的"点在不在圈里"都要乘这个顶点数。
        LassoLive = new Vector2(x, y);
        if (LassoPath.Count == 0) { LassoPath.Add(LassoLive); }
        else
        {
            var last = LassoPath[LassoPath.Count - 1];
            if (Vector2.Distance(last, LassoLive) >= LassoStepLogical * DpiScale)
                LassoPath.Add(LassoLive);
        }

        // 脏区**只增不减**（和矩形那条路刻意不同）：已经画到屏幕上的那一段线，
        // 不能因为指针往回移就不重画——不重画就擦不掉，屏幕上会留下一截旧线。
        MqMinX = MathF.Min(MqMinX, x); MqMaxX = MathF.Max(MqMaxX, x);
        MqMinY = MathF.Min(MqMinY, y); MqMaxY = MathF.Max(MqMaxY, y);
    }

    /// <summary>截图取景框按下（和框选同一套锚点算法）。</summary>
    private void BeginCaptureAt(float x, float y)
    {
        CaptureActive = true;
        _capAnchorX = x; _capAnchorY = y;
        CapMinX = CapMaxX = x; CapMinY = CapMaxY = y;
    }

    /// <summary>截图取景框拖动：锚点 ↔ 当前点（往回拖要跟着缩，否则就是"不跟手"）。</summary>
    private void ExtendCaptureTo(float x, float y)
    {
        CapMinX = MathF.Min(_capAnchorX, x); CapMaxX = MathF.Max(_capAnchorX, x);
        CapMinY = MathF.Min(_capAnchorY, y); CapMaxY = MathF.Max(_capAnchorY, y);
    }

    /// <summary>
    /// 自检用：只驱动截图取景框的"按下 → 拖"，**不真的抓屏**（抓屏要藏窗口，
    /// 还会把这一帧的测试环境弄乱）。取景框和框选框用的是同一套锚点算法。
    /// </summary>
    internal void CaptureFrameDragForTest(float ax, float ay, float x1, float y1, float x2 = float.NaN, float y2 = float.NaN)
    {
        BeginCaptureAt(ax, ay);
        ExtendCaptureTo(x1, y1);
        if (!float.IsNaN(x2)) ExtendCaptureTo(x2, y2);
    }

    /// <summary>
    /// 自检用：把一条路径按"按下 → 一路移动 → 松手"**真实跑一遍**——走的是和鼠标
    /// 完全相同的三个入口（<see cref="BeginMarqueeAt"/> / <see cref="ExtendMarqueeTo"/> /
    /// <see cref="ApplyMarquee"/>），只是不经过操作系统的消息队列。
    /// </summary>
    internal void MarqueeDragForTest(IReadOnlyList<Vector2> path,
                                     bool shift = false, bool alt = false)
    {
        if (path == null || path.Count == 0) return;
        BeginMarqueeAt(path[0].X, path[0].Y, shift, alt);
        for (int i = 1; i < path.Count; i++) ExtendMarqueeTo(path[i].X, path[i].Y);
        ApplyMarquee();               // 松手：按 SelMode 走矩形或套索
    }

    /// <summary>
    /// 套索松手。判据在 <see cref="InkDocument.ApplyLasso"/>（80% 在内 + 贴边无限延伸），
    /// 这里只管三件事：路径太短就当**单击空白**（取消选中，和框选一致）、
    /// 修饰键加减选、清掉路径。
    /// </summary>
    private void ApplyLassoSelection()
    {
        if (LassoPath.Count < 3 || (MqMaxX - MqMinX) < 4f || (MqMaxY - MqMinY) < 4f)
        {
            // 点一下空白就想"什么都别选"——和框选那条同样的处理（见 ApplyMarquee）。
            Doc.Selected.Clear();
            CopyDragArmed = false;
            LassoPath.Clear();
            _dirty = true;
            return;
        }

        int n = Doc.ApplyLasso(LassoPath, ViewportCanvas, LassoEdgeSnapLogical * DpiScale,
                               additive: _lassoAdditive, subtractive: _lassoSubtractive);
        Console.WriteLine(_lassoSubtractive
            ? $"lasso removed {n} strokes（剩 {Doc.Selected.Count} 条）"
            : $"lasso selected {n} strokes（圈 {LassoPath.Count} 个点"
              + (_lassoAdditive ? $"，Shift 加选，共 {Doc.Selected.Count} 条）" : "）"));
        LassoPath.Clear();
        _dirty = true;
    }

    /// <summary>
    /// "圈到屏幕边"的判定容差（逻辑像素）：指针离可见区域边线这么近就当作贴边，
    /// 那一段按无限延伸处理（见 <see cref="InkDocument.ApplyLasso"/>）。
    ///
    /// 取 4：指针能被拖到窗口外（有捕获），所以贴边基本都能满足；取太大又会让
    /// "离边还有一点点"的圈也偷偷延伸出去，把屏幕外的墨一起圈进来。
    /// </summary>
    internal const float LassoEdgeSnapLogical = 4f;

    /// <summary>
    /// 读一条指针消息，顺便把两个时标记下来（延时探针用）：
    ///   pi.PerformanceCount —— 系统给的硬件/驱动时标（QPC 计数）
    ///   现在                —— 我们真正拿到它的时刻
    /// 两者之差 = 输入栈 + 我们自己的消息队列。**这一段里只有队列是我们的责任**，
    /// 而队列拖延的直接原因就是 Present(1) 把线程卡在垂直同步里。
    /// </summary>
    private bool ReadPointer(uint id, out float x, out float y, out float pressure,
                             out bool inverted, out uint pointerType)
    {
        x = y = 0; pressure = 0.5f; inverted = false; pointerType = 0;
        if (!Native.GetPointerInfo(id, out var pi)) return false;
        _lastInputPerfQpc = pi.PerformanceCount;
        _lastInputMsgQpc = Qpc.Now;
        if (LatencyRecording && _prevInputMsgQpc != 0)
        {
            double gap = Qpc.Ms(_prevInputMsgQpc, _lastInputMsgQpc);
            if (gap > 0 && gap < 500) Latency.AddInputGap(gap);
        }
        _prevInputMsgQpc = _lastInputMsgQpc;
        _framePoints++;
        x = pi.ptPixelLocationX;
        y = pi.ptPixelLocationY;
        pointerType = pi.pointerType;
        if (pi.pointerType == Native.PT_PEN && Native.GetPointerPenInfo(id, out var pen))
        {
            pressure = pen.pressure / 1024f;
            if (pressure <= 0.01f) pressure = 0.5f;
            inverted = (pen.penFlags & (Native.PEN_FLAG_INVERTED | Native.PEN_FLAG_ERASER)) != 0;
        }
        return true;
    }

    // =====================================================================
    //  Hotkeys
    // =====================================================================

    private void HandleHotkey(int id)
    {
        // id 是"注册顺序"，动作由键位表决定（见 RegisterHotkeys）。
        var action = ActionForHotkeyId(id);
        if (action == KeyAction.None) return;
        RunAction(action, id);
        // 换工具/换模式之后指针形状立刻要跟着变。
        ApplyCursor();
        _dirty = true;
    }

    /// <summary>快捷键里属于宿主（开发工具）的那几个。产品界面用不到。</summary>
    protected virtual void HandleHostHotkey(int id) { }

    /// <summary>
    /// 执行一个动作。**全局热键和批注内快捷键走同一个入口**——否则同一个动作
    /// 会有两份实现，早晚会不一致（"按 Ctrl+Alt+Z 撤销得好好的，按 Ctrl+Z 却少清了激光"）。
    /// </summary>
    private void RunAction(KeyAction action, int hotkeyId = 0)
    {
        // 手测台：事件流水。撤销尤其重要——"擦完马上撤销"就是"这一擦不是我想要的"。
        if (EraserTelemetry != null && action != KeyAction.None)
            EraserTelemetry.Note(KeyMap.Describe(action), NowMs);
        switch (action)
        {
            case KeyAction.TogglePassThrough: SetPassThrough(!PassThrough); break;
            case KeyAction.ToolPen: SwitchTool(Tool.Pen); break;
            case KeyAction.ToolHighlighter: SwitchTool(Tool.Highlighter); break;
            case KeyAction.ToolLaser: SwitchTool(Tool.Laser); break;
            case KeyAction.ToolEraser: SwitchTool(Tool.Eraser); break;
            case KeyAction.ToolPixelEraser: SwitchTool(Tool.PixelEraser); break;
            case KeyAction.SplitErased:
            {
                int n = Doc.SplitErasedSelection();
                Console.WriteLine(n > 0
                    ? $"拆开擦断的笔迹：{n} 条 → 各段成为独立对象（可单独搬运/删除）"
                    : "拆开擦断的笔迹：选中的里面没有被擦断的（先用框选选中它）");
                break;
            }
            case KeyAction.ToolCapture: SwitchTool(Tool.Capture); break;
            case KeyAction.ToolMarquee: SwitchTool(Tool.Marquee); break;
            case KeyAction.SelectShape: ToggleSelectMode(); break;
            case KeyAction.Undo: Doc.Undo(); Laser.Clear(); break;
            case KeyAction.Redo: Doc.Redo(); break;
            case KeyAction.Copy: CopySelectionToClipboard(); break;
            case KeyAction.Clear: Doc.Clear(); Laser.Clear(); break;
            case KeyAction.ToggleHud: ShowHud = !ShowHud; break;
            case KeyAction.CycleWidth: CycleWidth(); break;
            case KeyAction.ToggleKeyboardMode: SetKeyboardMode(!KeyboardMode); break;
            case KeyAction.CyclePassThroughMode: CyclePassThroughMode(); break;
            case KeyAction.Quit: _quit = true; break;
            // 开发期的基准与内存探测不属于引擎，交给宿主覆写
            case KeyAction.HostBenchmark:
            case KeyAction.HostMemoryProbe:
                HandleHostHotkey(hotkeyId != 0 ? hotkeyId : (int)action);
                break;

            case KeyAction.SelectAll: SelectAll(); break;
            case KeyAction.Duplicate: Doc.DuplicateSelected(); break;
            case KeyAction.DeleteSelected: Doc.DeleteSelected(); break;
            case KeyAction.CancelSelection: Doc.Selected.Clear(); break;
            case KeyAction.Paste: PasteFromClipboard(); break;
            case KeyAction.NudgeLeft: Nudge(-1f, 0f); break;
            case KeyAction.NudgeUp: Nudge(0f, -1f); break;
            case KeyAction.NudgeRight: Nudge(1f, 0f); break;
            case KeyAction.NudgeDown: Nudge(0f, 1f); break;
            case KeyAction.NudgeLeftFar: Nudge(-10f, 0f); break;
            case KeyAction.NudgeUpFar: Nudge(0f, -10f); break;
            case KeyAction.NudgeRightFar: Nudge(10f, 0f); break;
            case KeyAction.NudgeDownFar: Nudge(0f, 10f); break;
            case KeyAction.FlipPageUp: FlipPageFromUi(false); break;
            case KeyAction.FlipPageDown: FlipPageFromUi(true); break;
        }
    }

    /// <summary>
    /// 方向键微调。**已知待改**：按住方向键会重复触发，每次都是一条撤销记录；
    /// 要接"连续微调合并成一步"，得等编辑命令支持合并。
    /// </summary>
    private void Nudge(float dx, float dy)
    {
        Doc.ApplyTransform(Matrix3x2.CreateTranslation(dx, dy));
        _dirty = true;
    }

    /// <summary>
    /// 切换**当前工具**的粗细。以前这里写死改笔宽，于是"选了荧光笔按切粗细没反应"
    /// ——改的不是它。四种工具各有一张档位表（见 WidthPresets 的注释）。
    /// </summary>
    /// <summary>
    /// 换工具。**用户规则（2026-09-15 定，也是 InkClass / PPT / Figma 的惯例）**：
    /// "选中是临时上下文"——除了框选工具自己，换到任何别的工具都**收起选区**。
    ///
    /// 不这么做会出两个问题：① 选区遮罩会把接下来的第一笔吃掉（老师点完工具画不出来，
    /// 得先点一下空白）；② 用户看着"我已经换工具了，怎么还选着"。
    /// 滚动、激光、键盘编辑键**不算**换上下文（见 计划-选中工具.md 的那张表）。
    /// </summary>
    private void SwitchTool(Tool t)
    {
        if (Tool != t && t != Tool.Marquee) ClearSelectionForNewContext();
        Tool = t;

        // **穿透和工具是互斥的**（用户 2026-09-17 定）。
        //
        // 穿透开着的时候点击落到下层程序上，画布根本收不到笔——这时"选中了笔"是个假状态：
        // 按钮亮着、写不出字。所以换工具（点面板也好、按热键也好）等于一句"我要开始用了"，
        // 顺手把穿透关掉。反过来，点面板上那个"鼠标"格是明说要穿透，它单独开。
        if (PassThrough) SetPassThrough(false);
    }

    /// <summary>收起选区（换工具、以及"与选中无关的新操作"走这里）。</summary>
    private void ClearSelectionForNewContext()
    {
        CopyDragArmed = false;
        if (Doc.Selected.Count == 0) return;
        Doc.Selected.Clear();
        _dirty = true;
    }

    internal void CycleWidth()
    {
        switch (Tool)
        {
            case Tool.Highlighter:
                HighlighterWidthIndex = (HighlighterWidthIndex + 1) % HighlighterWidthPresets.Length;
                HighlighterWidthLogical = HighlighterWidthPresets[HighlighterWidthIndex];
                break;
            case Tool.Laser:
                LaserWidthIndex = (LaserWidthIndex + 1) % LaserWidthPresets.Length;
                LaserWidthLogical = LaserWidthPresets[LaserWidthIndex];
                break;
            case Tool.PixelEraser:
                PixelEraserWidthIndex = (PixelEraserWidthIndex + 1) % PixelEraserWidthPresets.Length;
                PixelEraserWidthLogical = PixelEraserWidthPresets[PixelEraserWidthIndex];
                break;
            case Tool.Eraser:
                // 整笔橡皮也有自己的档位。以前它落进 default，按 Ctrl+Alt+6 改的是**笔宽**
                // ——"拿着橡皮调粗细，笔迹变粗了"就是这么来的。
                EraserRadiusIndex = (EraserRadiusIndex + 1) % EraserRadiusPresets.Length;
                EraserRadiusLogical = EraserRadiusPresets[EraserRadiusIndex];
                break;
            default:
                WidthPresetIndex = (WidthPresetIndex + 1) % WidthPresets.Length;
                PenWidthLogical = WidthPresets[WidthPresetIndex];
                break;
        }
        Console.WriteLine($"{Tool} 粗细 -> {CurrentToolWidthLogical} 逻辑像素"
                        + $"（本机实际 {CurrentToolWidthLogical * DpiScale:F0} 物理像素）");
        EraserTelemetry?.Note($"{ToolName(Tool)}粗细 → {CurrentToolWidthLogical:F0} 逻辑像素", NowMs);
        NotifyUiStateChanged();
    }

    private void SetPassThrough(bool on)
    {
        PassThrough = on;
        if (!on) Laser.Visible = false;

        // **穿透和白板互斥**（用户 2026-09-17 问的那条）。两个方向都要挡：
        //   · 开白板 → 关穿透：白板是不透明的一层，穿透是"点击落到下层程序"；
        //     两个一起开着，老师看到的是白板、点到的却是白板下面那个看不见的窗口。
        //   · 开穿透 → 关白板：同上，反过来也一样说不通。
        // 关掉的那一方**不自动回来**（和"关板不自动开穿透"一致）：老师再点一下就行，
        // 而"悄悄替你恢复"才是难查的那类行为。
        if (on && BoardOn)
        {
            BoardOn = false;
            Doc.InvalidateAll();
            NotifyUiStateChanged();
        }
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        // 穿透时把指针交还给下层窗口（ApplyCursor 会在穿透模式下自动放手）；
        // 退出穿透要立刻把属于我们的光标设回来，不必等下一次鼠标移动。
        if (!on) ApplyCursor(force: true);
        Console.WriteLine($"pass-through = {on} (mode {PassMode})");
    }


    private void CyclePassThroughMode()
    {
        PassMode = (PassThroughMode)(((int)PassMode + 1) % 3);
        foreach (var w in _windows) ApplyPassThroughStyle(w);
        Console.WriteLine($"pass-through implementation = {PassMode}");
    }

    internal void ApplyPassThroughStyle(OverlayWindow w)
    {
        long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
        const long clear = Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        ex &= ~clear;

        if (PassThrough)
        {
            if (PassMode == PassThroughMode.ExTransparent)
                ex |= Native.WS_EX_TRANSPARENT;
            else if (PassMode == PassThroughMode.LayeredTransparent)
                ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        }

        Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
    }

    /// <summary>
    /// 关窗、注销快捷键、释放设备。子类若要覆写，务必先调用 base。
    /// </summary>
    protected virtual void Shutdown()
    {
        foreach (var w in _windows)
        {
            Native.KillTimer(w.Hwnd, (IntPtr)1);
            for (int i = 1; i <= _hotkeyActions.Count + 2; i++) Native.UnregisterHotKey(w.Hwnd, i);
            w.Dispose();
        }
        // 只写"改过的"键位和"界面改过的"偏好；都没动过就不碰用户的配置文件。
        FlushSettings();

        // 退出前补一次自动存档（下一次打开接上）。自检模式不动用户的存档。
        if (Doc.Version != _autoSavedVersion) AutoSaveNow();

        if (_uiInputHwnd != IntPtr.Zero)
        {
            Native.DestroyWindow(_uiInputHwnd);
            _uiInputHwnd = IntPtr.Zero;
            _uiInputShown = false;
        }

        _windows.Clear();
        s_map.Clear();
        Cursors.DisposeAll();
        Gfx.Shutdown();
        // 手测台：退出时把汇总写出来（逐条数据在每条拖拽结束时就已经落盘了）。
        EraserTelemetry?.Close(Doc, NowMs);
        Console.WriteLine("shutdown complete");
    }

    // =====================================================================
    //  界面接入（引擎边界）
    //
    //  引擎负责：笔画/文档/输入/渲染/工具状态。
    //  界面负责：画自己、解释点击。
    //  两边唯一的通道就是 IOverlayUi / IUiHost，引擎里没有任何具体界面代码。
    // =====================================================================

    /// <summary>
    /// 换界面。传 null 或 <see cref="NullUi"/> 就是无界面（纯笔迹）。
    /// 界面只在覆盖层建好之后接入；运行中换界面也是安全的。
    /// </summary>
    /// <param name="keepScreen">
    /// 传 true 表示沿用当前这个界面已经算好的屏幕尺寸。自检里"临时换掉再换回来"
    /// 会用到处，避免界面按引擎内部的窗口尺寸重新布局（那把界面摆到别处去了）。
    /// 正式产品代码用默认值即可。
    /// </param>
    public void SetUi(IOverlayUi ui, bool keepScreen = false)
    {
        Ui = ui ?? new NullUi();
        // 换了界面就得让各窗口重新布局：覆盖窗口缓存着"上次给 Layout 的屏幕"，
        // 不清的话新界面永远等不到 Layout，QueryBounds 会一直返回空（看不见也点不到）。
        foreach (var w in _windows) w.InvalidateUiLayout();
        _uiFaults = 0;
        _uiHover = false;
        // 界面看到的屏幕坐标是逻辑像素：它按自己的逻辑尺寸布局，引擎负责按 DPI 放大。
        Host = new UiHost(this, LogicalVirtualScreen, DpiScale, keepScreen);
        Ui.Attach(Host);
        InvalidateUi();
        NotifyUiStateChanged();
    }

    /// <summary>当前接入的界面（诊断用）。</summary>
    public IOverlayUi CurrentUi => Ui;

    /// <summary>虚拟桌面范围，界面据此决定悬浮条放在哪。</summary>
    public RectF VirtualScreen => new()
    {
        MinX = _virtualX,
        MinY = _virtualY,
        MaxX = _virtualX + _virtualW,
        MaxY = _virtualY + _virtualH,
    };

    /// <summary>虚拟桌面的**逻辑**范围（除以 DPI 缩放），界面布局用这个。</summary>
    public RectF LogicalVirtualScreen => new()
    {
        MinX = _virtualX / DpiScale,
        MinY = _virtualY / DpiScale,
        MaxX = (_virtualX + _virtualW) / DpiScale,
        MaxY = (_virtualY + _virtualH) / DpiScale,
    };

    /// <summary>
    /// 界面声明"外观变了，请重画我的缓存"。可以由界面的任意线程调用，
    /// 主线程在下一帧消费。**不要每帧调**，那等于每帧重画整个界面。
    /// </summary>
    public void InvalidateUi()
    {
        UiInvalidatePending = true;
        _uiInvalidateSeq++;
        _dirty = true;
    }

    /// <summary>
    /// 界面的重画请求流水号。渲染循环拿它判"渲染期间界面是不是又提了请求"，
    /// 从而**不抹掉**那一帧请求（见 Loop 里那段的注释）。
    /// </summary>
    private long _uiInvalidateSeq;

    internal UiState SnapshotState() => new()
    {
        Tool = Tool,
        Color = Tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
        PaletteBase = Tool == Tool.Highlighter
            ? new Color4(HighlighterCurrent.R, HighlighterCurrent.G, HighlighterCurrent.B, 1f)
            : CurrentColor,
        Width = CurrentToolWidthLogical,
        // 按工具分开给：只给一个 Width 的话，滑块在切工具时会跳（三个工具各记各的宽度）。
        PenWidth = PenWidthLogical,
        HighlighterWidth = HighlighterWidthLogical,
        HighlighterColor = HighlighterCurrent,
        LaserWidth = LaserWidthLogical,
        PassThrough = PassThrough,
        Board = BoardOn,
        BoardColor = BoardColor,
        BoardPattern = BoardPattern,
        BoardPatternStep = BoardPatternStepLogical,
        BoardOpacity = BoardOpacity,
        CaptureHideInk = CaptureHideInk,
        SelectMode = SelMode,
        ScreenIndex = ScreenIndex,
        CanFlipPageUp = CanFlipPageUp,
        SlidePosition = SlideNow.Showing ? SlideNow.Position : 0,
        SlideCount = SlideNow.Showing ? SlideNow.Count : 0,
        IsDrawing = _drawing,
        UndoDepth = Doc.UndoDepth,
        RedoDepth = Doc.RedoDepth,
        StrokeCount = Doc.Strokes.Count,
    };

    private void NotifyUiStateChanged()
    {
        if (Ui == null) return;
        var snapshot = SnapshotState();
        UiGuard("OnStateChanged", () => Ui.OnStateChanged(snapshot));
    }

    internal void SetToolFromUi(Tool tool)
    {
        SwitchTool(tool);
        EraserTelemetry?.Note($"界面换工具 → {ToolName(tool)}", NowMs);
        ApplyCursor();
        _dirty = true;
        NotifyUiStateChanged();
    }

    // ---- 自检钩子（测试要驱动"换工具/操作条按钮/拖动中移动"这些私有路径）----

    /// <summary>自检用：执行一个键位动作（换工具、撤销……都从同一个入口进）。</summary>
    internal void RunActionForTest(KeyAction a) => RunAction(a);

    /// <summary>自检用：点操作条第 index 个按钮。</summary>
    internal void RunBarActionForTest(int index)
        => RunBarAction(index, SelectionHandles.FrameOf(Doc.Selected),
                        SelectionHandles.FrameOf(Doc.Selected).CanvasAabb);

    /// <summary>自检用：拖动中移动指针（选择手势）。</summary>
    internal void UpdateSelectionGestureForTest(float x, float y) => UpdateSelDrag(x, y);

    internal void SetColorFromUi(Color4 color)
    {
        // 荧光笔用同一组基色，只是降低不透明度——老师看到的色相是一致的。
        if (Tool == Tool.Highlighter)
            HighlighterCurrent = InkPalette.ToHighlighter(color);
        else
            CurrentColor = color;
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void SetWidthFromUi(float logicalPx)
    {
        // 外层的 0.5～64 只是"别把明显离谱的值放进来"的兜底；**真正的范围按工具算**。
        // 这个 64 曾经把面积橡皮卡住过：它的横边要能到 160（一块大橡皮），
        // 被外层夹在 64 之后，"拖到最右"只能到 64（--paneltest 当场抓到）。
        float v = Math.Clamp(logicalPx, 0.5f, PixelEraserMaxWidth);

        // **按当前工具路由**。以前只有"荧光笔 / 激光 / 其它"三支，
        // 两种橡皮全落进"其它"→ 改的是笔宽（界面上的橡皮滑条是个摆设）。
        switch (Tool)
        {
            case Tool.Highlighter:
                HighlighterWidthLogical = v;
                int hi = Array.IndexOf(HighlighterWidthPresets, v);
                if (hi >= 0) HighlighterWidthIndex = hi;
                break;

            case Tool.Laser:
                LaserWidthLogical = v;
                int li = Array.IndexOf(LaserWidthPresets, v);
                if (li >= 0) LaserWidthIndex = li;
                break;

            case Tool.PixelEraser:
                // 像素橡皮改的是**那一块橡皮的横边**（高 = 横边 × 黄金比）。
                // 它的范围比笔宽大得多（一块橡皮 30～160 逻辑像素），
                // 所以这里单独夹一次，不跟笔共用那个 64 的上限。
                PixelEraserWidthLogical = Math.Clamp(v, PixelEraserMinWidth, PixelEraserMaxWidth);
                int pi = Array.IndexOf(PixelEraserWidthPresets, PixelEraserWidthLogical);
                if (pi >= 0) PixelEraserWidthIndex = pi;
                break;

            case Tool.Eraser:
                // 整笔橡皮改的是**落点半径**（碰到哪儿就删哪一条）
                EraserRadiusLogical = Math.Clamp(v, EraserRadiusMin, EraserRadiusMax);
                int ei = Array.IndexOf(EraserRadiusPresets, EraserRadiusLogical);
                if (ei >= 0) EraserRadiusIndex = ei;
                break;

            default:
                PenWidthLogical = v;
                int idx = Array.IndexOf(WidthPresets, v);
                if (idx >= 0) WidthPresetIndex = idx;
                break;
        }
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void UndoFromUi()
    {
        Doc.Undo();
        FollowUndoPage();
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void RedoFromUi()
    {
        Doc.Redo();
        FollowUndoPage();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// **撤销跨页时自动翻回去**（页模型的行为之一）。
    ///
    /// 场景：老师在第 3 页按撤销，而撤销掉的是第 1 页写的那一笔——相机不跟过去，
    /// 屏幕上就"什么都没变"（那一笔在第 1 页上回来了，可他看不见）。
    /// 所以撤销/重做完看一眼"这一步发生在哪一页"，不在眼前就整屏翻过去。
    ///
    /// 只在**白板空间**做：幻灯片空间里翻页意味着换页，那是 PPT 说了算的
    /// （见 调研-对接PPT.md），不能我们自己偷偷把老师的放映翻走。
    /// </summary>
    private void FollowUndoPage()
    {
        int p = Doc.LastActionPage;
        if (p < 0 || p == CurrentPage) return;
        if (Math.Abs(p - CurrentPage) > 200) return;      // 明显不合理的页号，别乱跳
        GotoPage(p);
    }

    /// <summary>
    /// 把相机**动画移到第 n 页**（和翻页同一套曲线、同一个时长）。
    ///
    /// **这里故意不走 <see cref="ClampOffset"/>**（踩过一次，记下来）：
    /// 那个夹取是按"画布上**现在有多少内容**"算的，而"翻到第 N 页"是**明确的意图**——
    /// 而且经常正好发生在内容刚变的那一刻（撤销把第 3 页那笔撤没了，内容范围立刻缩回去，
    /// 于是想去第 3 页却被夹在第 2 页；自检里就是这么被抓到的：撤销后停在第 2 页）。
    /// 允许停在"暂时没有内容的页"没问题：老师看到的就是那一页（空的），
    /// 下一次滚轮/翻页又会照常按内容夹回来。只有"第 1 页之上"确实没有东西，那个上限保留。
    /// </summary>
    internal bool GotoPage(int page)
    {
        float want = PageTopCanvas - page * PageHeightCanvas;
        if (want > 0f) want = 0f;
        if (Math.Abs(want - ViewOffsetY) < 1f) return false;
        return AnimateCameraTo(want);
    }

    // ---- 幻灯片页空间（2026-09-17，阶段 1）---------------------------------
    //
    // 白板页 0、1、2… 从画布顶上往下排；**幻灯片页排在另一个区段**（页号从
    // SlidePageBase 起），于是"白板页"和"幻灯片页"天然是两个不相交的空间
    // ——用户点头的那条默认（白板页和 PPT 页是两套页空间）。
    //
    // 为什么用"页号偏移"而不是另开一套坐标：页号本来就是一个整数，页的**位置**
    // 由 `PageTop + page × 页高` 算出来。加一个大偏移就能让两套互不干扰，
    // 而清空本页、页归属、落盘这些逻辑一行都不用改（它们只认那个整数）。
    // 取 100000：白板要排到十万页才可能撞上，实际不可能；也留着以后接别的页空间。

    /// <summary>幻灯片页的页号起点（幻灯片第 1 页 = 这个值 + 1）。</summary>
    internal const int SlidePageBase = 100000;

    /// <summary>幻灯片页号 → 白板那套页号（给"清空本页/归属"复用）。</summary>
    internal static int SlidePageOfPosition(int position) => SlidePageBase + position;

    /// <summary>
    /// **"现在是第几页"的来源**。null = 没接（永远如此时一切照旧）。
    /// 生产里是 PowerPoint / WPS 的 COM 实现，自检里是 <see cref="FakeSlideSource"/>。
    /// </summary>
    internal ISlideSource Slides;

    /// <summary>最近一次探测到的放映状态（界面读它显示 `PPT 3/12`）。</summary>
    internal SlideState SlideNow;

    private double _nextSlidePollAtMs;

    /// <summary>进放映之前站在白板的哪一页（放映结束要回到它）。</summary>
    private int _whiteboardPageBeforeShow;

    /// <summary>
    /// **探测放映状态**：轮询而不是挂 COM 事件（理由见 调研-对接PPT.md 3.4：
    /// 老师翻页有三个来源——我们的按钮、键盘、遥控翻页器，轮询一律看得见）。
    ///
    /// 三条纪律：
    ///   ① 每 250ms 才问一次（COM 调用再便宜也不该每帧付）；
    ///   ② **探测失败一律当"没有 PPT"**——不弹错、不卡、不提示（老师那边一切照旧）；
    ///   ③ 页变了才动相机（"切页 = 换一块画布"，O(1)，不重放任何历史）。
    /// </summary>
    private void PollSlides()
    {
        if (Slides == null) return;
        if (NowMs < _nextSlidePollAtMs) return;
        _nextSlidePollAtMs = NowMs + 250;

        bool ok;
        SlideState st;
        try { ok = Slides.TryGetState(out st); }
        catch (Exception ex)
        {
            Console.WriteLine($"幻灯片探测失败（当作没有 PPT）：{ex.Message}");
            Slides = null;                       // 一次都不再纠缠，退回纯白板
            return;
        }
        if (!ok) st = default;

        bool wasShowing = SlideNow.Showing;
        bool changed = !st.Equals(SlideNow);
        SlideNow = st;
        if (!changed) return;

        // 模型层要知道"新写的东西属于哪一页/哪一叠"（见 InkDocument.AddStroke 那个漏斗）
        Doc.CurrentSlideId = st.Showing ? st.SlideId : 0;
        Doc.CurrentDeckKey = st.Showing ? st.DeckKey : null;

        if (st.Showing)
        {
            // 记下"进放映之前站在白板的哪一页"——放映结束要**回到那一页**，
            // 而不是傻站在幻灯片页区段上（那里在白板空间里是一片空白）。
            if (!wasShowing) _whiteboardPageBeforeShow = CurrentPage;
            // 放映空间：新笔自动落到这一页（页号 = 幻灯片页区间里的那一个）
            Doc.CurrentPage = SlidePageOfPosition(st.Position);
            GotoPage(Doc.CurrentPage);
            Console.WriteLine($"放映：第 {st.Position}/{st.Count} 页"
                            + $"（幻灯片身份 {st.SlideId}），批注跟着换到这一页");
        }
        else if (wasShowing)
        {
            // 放映结束：**回到进放映之前那一页白板**。老师刚从 PPT 退出来，
            // 眼前该是他自己的板书，而不是幻灯片区段那片空白。
            GotoPage(_whiteboardPageBeforeShow);
            Doc.CurrentPage = _whiteboardPageBeforeShow;
            Console.WriteLine($"放映结束：回到白板第 {_whiteboardPageBeforeShow + 1} 页");
        }
        NotifyUiStateChanged();
        _dirty = true;
    }

    private bool AnimateCameraTo(float want)
    {
        if (!ClientAreaAnimationOn)
        {
            ViewOffsetY = want;               // 系统关了动画就直接跳终态（老机器上是常事）
            _dirty = true;
            return true;
        }
        _camFrom = ViewOffsetY; _camTo = want; _camStartMs = NowMs; _camAnimating = true;
        _dirty = true;
        return true;
    }

    internal void ClearFromUi()
    {
        Doc.CurrentPage = CurrentPage;      // 保险：提交那一步要知道它在哪一页
        // **只清当前这一页**（页模型带来的行为改动，2026-09-17）。
        // 老师在第 5 页讲完一题点清空，前面四页的板书原样留着——这是"分页"最实在的好处，
        // 也是 InkClass 那些用户真正在用的行为。清空**不回第 1 页**（用户点过头的默认：
        // "在第 12 页清空就还在第 12 页接着写"）。
        int cleared = Doc.ClearPage(CurrentPage);
        Console.WriteLine(cleared > 0
            ? $"清空：只清第 {CurrentPage + 1} 页（{cleared} 个对象），其余页没动"
            : $"清空：第 {CurrentPage + 1} 页本来就是空的");
        Laser.Clear();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void QuitFromUi() => _quit = true;

    // ---- 界面偏好的存取（引擎只当仓库，不解释内容）----------------------

    /// <summary>界面来问一条偏好。没有就返回 null（界面自己知道默认值）。</summary>
    internal string GetUiPref(string key)
        => key != null && UiPrefs.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// 界面记一条偏好。传 null 表示"回到默认"，那就把这一项**删掉**——
    /// 配置文件里只留和默认不一样的项，以后默认值改了，老配置不会把新默认顶掉。
    /// </summary>
    internal void SetUiPref(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (value == null)
        {
            if (UiPrefs.Remove(key)) _uiPrefsDirty = true;
            return;
        }
        if (UiPrefs.TryGetValue(key, out var old) && old == value) return;
        UiPrefs[key] = value;
        _uiPrefsDirty = true;
    }

    /// <summary>把键位与界面偏好写盘（只在真的改过时才写）。自检模式一律不写。</summary>
    internal void FlushSettings()
    {
        if (SelfCheckMode) return;
        if (!Keys.Dirty && !_uiPrefsDirty) return;
        InkSettings.Save(Keys, UiPrefs);
        _uiPrefsDirty = false;
    }

    /// <summary>自检用：**无视自检模式的禁令**，立刻把设置写盘（用来验证"记得住"这条链子）。</summary>
    internal void SaveSettingsForTest() => InkSettings.Save(Keys, UiPrefs);

    /// <summary>
    /// 自检用：把内存里的界面偏好清空、**从文件重读**。
    /// 不这么做的话，"重开界面读回了偏好"其实读的是内存里那份，等于没验到落盘。
    /// </summary>
    internal void ReloadUiPrefsForTest()
    {
        UiPrefs.Clear();
        foreach (var w in InkSettings.LoadUiPrefs(UiPrefs))
            Console.WriteLine("settings: " + w);
    }

    /// <summary>
    /// 界面上那个"重启"：给老师一个"感觉不对就重开一次"的出口
    /// （教室大屏 + 手写板的机器上可能没有键盘，界面是唯一入口）。
    /// 走的是和"界面崩了自动重启"同一条路：**先暂存板书**，再拉起新进程、退出自己；
    /// 新进程启动时会把它读回来，所以重启不丢东西。
    /// </summary>
    internal void RestartFromUi()
    {
        try { Recovery.SaveSession(InkSerializer.Save(Doc)); }
        catch (Exception ex) { Console.WriteLine("板书暂存失败：" + ex.Message); }

        if (!RestartSelf("界面上的重启"))
        {
            Console.WriteLine("重启没成功，继续用（板书还在）");
            return;
        }
        RestartRequested = true;
        _quit = true;
    }

    internal void SetPassThroughFromUi(bool on)
    {
        SetPassThrough(on);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 开关白板。底色一变，整个内容层都要重画——缓存里那张图是按旧底色画的。
    /// </summary>
    internal void SetBoardFromUi(bool on)
    {
        if (BoardOn == on) return;
        BoardOn = on;
        // 开白板就顺手关掉穿透（另一边在 SetPassThrough 里，两个方向都挡，理由见那儿）
        if (on) SetPassThrough(false);
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 装界面，**并留下"再造一个"的办法**。产品代码用这一条，不要用 <see cref="SetUi"/>：
    /// 界面出问题时引擎要能自己把界面重建回来（教室大屏很可能没有键盘，
    /// 界面是唯一的出口，见 <see cref="Recovery"/> 的注释）。
    ///
    /// 传进来的工厂必须每次都能造一个**全新的**界面（不要复用同一个实例——
    /// 那个实例正是刚被判定"状态坏了"的那一个）。
    /// </summary>
    public void SetUiFactory(Func<IOverlayUi> factory)
    {
        _uiFactory = factory;
        SetUi(factory != null ? factory() : null);
    }

    /// <summary>界面切选择方式（矩形框 / 自由套索）。</summary>
    internal void SetSelectModeFromUi(SelectMode mode)
    {
        if (SelMode == mode) return;
        SetSelectMode(mode);
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 界面换板色。底色一变整个内容层都要重画——缓存里那张图是按旧底色画的
    /// （和 <see cref="SetBoardFromUi"/> 同理）。
    /// </summary>
    internal void SetBoardColorFromUi(Color4 color)
    {
        if (BoardColor.Equals(color)) return;
        BoardColor = color;
        Doc.InvalidateAll();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>界面上的"全选"。<see cref="SelectAll"/> 自己会把工具切成框选，免得用户以为没生效。</summary>
    internal void SelectAllFromUi()
    {
        SelectAll();
        _dirty = true;
    }

    /// <summary>界面上的"上一屏 / 下一屏"（整屏翻页）。</summary>
    internal void FlipPageFromUi(bool down)
    {
        // **放映中，这两个按钮驱动 PPT 翻页**（用户 2026-09-17 点头的默认，
        // 见 调研-对接PPT.md 第五节第 3 条）：老师按"下一屏"就是要翻 PPT 的下一页，
        // 而不是在 PPT 底下的白板上翻。翻完立刻探测一次，批注跟着换过去。
        if (SlideNow.Showing && Slides != null)
        {
            bool ok = down ? Slides.Next() : Slides.Previous();
            if (!ok) Console.WriteLine(down ? "放映：已经是最后一页" : "放映：已经是第一页");
            _nextSlidePollAtMs = 0;             // 别等那 250ms 的节拍
            return;
        }
        if (!FlipPage(down)) return;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 指针按下的第一站：先问界面。返回 true 表示这次输入归界面（比如按到了
    /// 悬浮条上的按钮），引擎不再把它变成笔画。
    /// </summary>
    private bool UiPointerDown(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (!UiVisibleNow) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        if (!UiGuard("PointerDown", () => Ui.PointerDown(e), false)) return false;
        UiCapturing = true;
        return true;
    }

    private bool UiPointerMove(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (!UiVisibleNow) return false;

        // 只有在界面已经捕获输入或指针落在界面矩形内时才转发，避免没必要的调用。
        if (!UiCapturing && !UiContains(x, y)) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        return UiGuard("PointerMove", () => Ui.PointerMove(e), false);
    }

    private bool UiPointerUp(float x, float y, float pressure, bool fromPen, bool eraserTip)
    {
        if (!UiVisibleNow || !UiCapturing) return false;
        var e = new UiPointerEvent(x / DpiScale, y / DpiScale, pressure, fromPen, eraserTip);
        bool consumed = UiGuard("PointerUp", () => Ui.PointerUp(e), false);
        UiCapturing = false;
        return consumed;
    }

    private bool UiContains(float x, float y)
    {
        x /= DpiScale; y /= DpiScale;   // 物理 → 逻辑
        foreach (var w in _windows)
            if (w.UiBoundsLogicalContains(x, y)) return true;
        return false;
    }

    /// <summary>
    /// 取 WM_NCHITTEST 的 lParam 里的屏幕坐标。
    ///
    /// 它是两个**有符号** 16 位拼起来的**物理**像素坐标。必须按有符号取：
    /// 副屏在主屏左边/上边时坐标是负的，按无符号取会得到 6 万多，
    /// 贴边、命中测试全错——而单屏 + 正坐标的机器上完全看不出来。
    /// </summary>
    private static void HitTestPoint(IntPtr lParam, out float x, out float y)
    {
        long v = lParam.ToInt64();
        x = (short)(v & 0xFFFF);
        y = (short)((v >> 16) & 0xFFFF);
    }

    // ---- 面板的"接输入小窗"（方案 B）--------------------------------------

    // ---- 界面回调的统一入口：**都是防弹的** ------------------------------
    //
    // 为什么值得包这一层：界面里的一个 bug 不该让"板书"这个主功能跟着不可用。
    // 以前的保护只有绘制那一段（`Overlay.DrawUi` 里的 try/catch 打印），
    // 而 `PointerDown/Move/Up`、`OnStateChanged`、`QueryBounds`、`IsAnimating`
    // 全是裸调——界面一抛，异常就冒到消息循环里，老师当场什么都画不出来。
    //
    // 规矩：**3 秒内 3 次**异常就把界面停用（换成 NullUi，并藏掉接输入小窗），
    // 笔迹照常。
    //
    // 为什么是"一个时间窗内 3 次"而不是"连续 3 次调用"：界面每帧都会被调好几次
    // （Visible / QueryBounds / Render / PointerMove…），只要别处的调用成功就把
    // 计数清零的话，一条只在 PointerDown 里踩的 bug 永远攒不到 3 次。
    // 反过来"一次就停用"又太狠——偶发一次不该让工具条消失一整节课。
    private int _uiFaults;
    private double _uiLastFaultMs = double.NegativeInfinity;

    /// <summary>界面工厂：有它引擎才能"自己把界面重建一个回来"。</summary>
    private Func<IOverlayUi> _uiFactory;

    /// <summary>最近一分钟里重建过几次界面。重建也失败就得往上走一级（重启软件）。</summary>
    private int _uiRebuilds;
    private double _uiRebuildWindowStartMs = double.NegativeInfinity;

    /// <summary>自检用：这一次"重启软件"有没有真的被发起。</summary>
    internal bool RestartRequested { get; private set; }

    /// <summary>重启计数清过一次就够了（跑满一分钟算"稳住了"）。</summary>
    private bool _restartCountCleared;

    /// <summary>
    /// 界面的异常次数与处置。**三级阶梯**，一级比一级重，但都不留死局：
    ///
    ///   ① 重建界面（引擎自己 new 一个回来）—— 板书不动，工具条重新挂一遍；
    ///   ② 重启软件 —— 重启前把板书暂存下来，重启后读回来，**什么都不丢**；
    ///   ③ 都不行就退回无界面 —— 至少还能写，而且是必现 bug 时的唯一出路
    ///      （否则就是无限重启，屏幕一直在闪，比停用更糟）。
    ///
    /// 为什么必须往"重启"走：教室的大屏 + 手写板机器上很可能没有键盘，
    /// 界面就是唯一的出口；界面没了又没法重启，就是死局（用户 2026-09-16 提的）。
    /// </summary>
    private void NoteUiFault(string what, Exception ex)
    {
        if (NowMs - _uiLastFaultMs > 3000) _uiFaults = 0;   // 隔久了当新的一轮
        _uiLastFaultMs = NowMs;
        _uiFaults++;
        if (_uiFaults < 3)
        {
            Console.WriteLine($"界面异常（{what}，第 {_uiFaults} 次）：{ex.Message}");
            return;
        }

        Console.WriteLine($"界面 3 秒内抛了 {_uiFaults} 次异常（最后一次在 {what}）：{ex.Message}");

        // ① 还能重建就先重建：不丢板书，通常也够用（界面多半是状态被搞坏了）。
        if (_uiFactory != null && UiRebuildAllowed())
        {
            RebuildUi(what);
            return;
        }

        // ② 重建也救不回来 → 重启软件（先把板书存下来）。
        if (TryRestartForUiProblem(what)) return;

        // ③ 最后的兜底：退回无界面。必现的 bug 走到这里，至少还能写。
        DisableUi(what);
    }

    private void RebuildUi(string what)
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) { _uiRebuilds = 0; _uiRebuildWindowStartMs = NowMs; }
        _uiRebuilds++;
        _uiFaults = 0;

        Console.WriteLine($"  → 重建界面（这个界面是第 {_uiRebuilds} 次重建）—— 板书不受影响");
        try
        {
            SetUi(_uiFactory());
        }
        catch (Exception ex)
        {
            Console.WriteLine("界面重建失败：" + ex.Message);
            SetUi(null);
        }
    }

    /// <summary>一分钟内最多重建两次；再多说明重建不管用，该往上走一级。</summary>
    private bool UiRebuildAllowed()
    {
        if (NowMs - _uiRebuildWindowStartMs > 60_000) return true;
        return _uiRebuilds < 2;
    }

    /// <summary>
    /// 因为界面出问题而重启软件。返回 true = 已经开始重启（调用方别再往下走）。
    /// **先存板书再重启**：重启的前提是不丢东西。
    /// </summary>
    private bool TryRestartForUiProblem(string what)
    {
        int n = Recovery.NoteRestart();
        if (n > Recovery.MaxRestartsInWindow)
        {
            Console.WriteLine($"  → 这个窗口内已经重启过 {n - 1} 次，不再重启（多半是必现的问题）");
            return false;
        }

        try { Recovery.SaveSession(InkSerializer.Save(Doc)); } catch (Exception ex) { Console.WriteLine("板书暂存失败：" + ex.Message); }
        Console.WriteLine($"  → 重启软件（第 {n} 次），板书已暂存，重启后自动读回来");

        if (!RestartSelf(what)) return false;
        RestartRequested = true;
        _quit = true;           // 主循环退出 → Shutdown → 进程结束，新进程接手
        return true;
    }

    /// <summary>
    /// 真的把软件重新拉起来。**宿主可以覆写**（自检里只记一笔，不真拉进程）。
    /// 返回 false = 没拉起来，那就别退出——退回无界面继续跑总比"退出后什么都没有"强。
    /// </summary>
    protected virtual bool RestartSelf(string why)
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("拉起新进程失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>三级都走不通时的兜底：停用界面，笔迹照常。</summary>
    private void DisableUi(string what)
    {
        Console.WriteLine($"  → 界面已停用（{what}）。笔迹不受影响；修好之后重开软件就回来了。");
        Ui = new NullUi();
        UiCapturing = false;
        Native.ReleaseCapture();
        _uiHover = false;
        if (_uiInputHwnd != IntPtr.Zero && _uiInputShown)
        {
            Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
            _uiInputShown = false;
        }
        _dirty = true;
    }

    private T UiGuard<T>(string what, Func<T> call, T fallback)
    {
        try { return call(); }
        catch (Exception ex) { NoteUiFault(what, ex); return fallback; }
    }

    private void UiGuard(string what, Action call)
    {
        try { call(); }
        catch (Exception ex) { NoteUiFault(what, ex); }
    }

    internal bool UiVisibleNow => Ui != null && UiGuard("Visible", () => Ui.Visible, false);
    internal RectF UiQueryBoundsNow() =>
        Ui == null ? RectF.Empty : UiGuard("QueryBounds", () => Ui.QueryBounds(), RectF.Empty);
    internal bool UiIsAnimatingNow => Ui != null && UiGuard("IsAnimating", () => Ui.IsAnimating, false);
    internal RectF UiLayoutNow(RectF screen, float dpiScale) =>
        Ui == null ? RectF.Empty : UiGuard("Layout", () => Ui.Layout(screen, dpiScale), RectF.Empty);
    internal void UiRenderNow(ID2D1DeviceContext ctx, UiTheme theme) =>
        UiGuard("Render", () => Ui.Render(ctx, theme));

    /// <summary>
    /// 每次渲染前调用一次：让接输入小窗跟着界面这一刻占的地方走。
    /// 界面隐藏或没占地方时把它藏起来——藏起来就等于"不存在于输入里"。
    /// </summary>
    private void UpdateUiInputWindow()
    {
        RectF logical = UiVisibleNow ? UiQueryBoundsNow() : RectF.Empty;

        if (logical.IsEmpty)
        {
            if (_uiInputShown && _uiInputHwnd != IntPtr.Zero)
            {
                Native.ShowWindow(_uiInputHwnd, Native.SW_HIDE);
                _uiInputShown = false;
                _uiHover = false;
            }
            return;
        }

        EnsureUiInputWindow();
        if (_uiInputHwnd == IntPtr.Zero) return;

        // 逻辑 → 物理只在这里做一次（界面的坐标系永远只有逻辑那一套）。
        var phys = new RectF
        {
            MinX = logical.MinX * DpiScale, MinY = logical.MinY * DpiScale,
            MaxX = logical.MaxX * DpiScale, MaxY = logical.MaxY * DpiScale,
        };

        if (_uiInputShown && phys.Equals(_uiInputRect)) return;

        Native.SetWindowPos(_uiInputHwnd, Native.HWND_TOPMOST,
            (int)MathF.Floor(phys.MinX), (int)MathF.Floor(phys.MinY),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxX - phys.MinX)),
            Math.Max(1, (int)MathF.Ceiling(phys.MaxY - phys.MinY)),
            Native.SWP_NOACTIVATE | (_uiInputShown ? 0 : Native.SWP_SHOWWINDOW));

        _uiInputRect = phys;
        _uiInputShown = true;
    }

    private void EnsureUiInputWindow()
    {
        if (_uiInputHwnd != IntPtr.Zero) return;

        long exStyle = Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW
                     | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        _uiInputHwnd = Native.CreateWindowEx(exStyle, _className, "InkTeachUiInput",
            0x80000000L /*WS_POPUP*/, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_uiInputHwnd == IntPtr.Zero)
        {
            Console.WriteLine("界面接输入小窗创建失败: " + Marshal.GetLastWin32Error()
                              + "（面板在穿透模式下会点不动）");
            return;
        }

        // 1/255 的不透明度：肉眼看不见，但对命中测试来说它**实实在在地在这**。
        Native.SetLayeredWindowAttributes(_uiInputHwnd, 0, 1, Native.LWA_ALPHA);
    }

    /// <summary>
    /// 接输入小窗的消息。它不画任何东西，只负责把指针事件翻成界面事件。
    /// 坐标一律是**物理屏幕坐标**——<c>UiPointer*</c> 那三个包装负责除以 DPI，
    /// 跟覆盖层那条路完全同一套口径。
    /// </summary>
    private IntPtr UiInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // 这块就是面板本身，不需要再判断"落在面板里的哪个位置"。
            case Native.WM_NCHITTEST:
                _cntNcHitTest++; _cntNcHitClient++;
                return new IntPtr(Native.HTCLIENT);

            // 点面板不许把下层程序的焦点抢走（老师点一下按钮，PPT 还是前台）。
            case Native.WM_MOUSEACTIVATE:
                return new IntPtr(Native.MA_NOACTIVATE);

            case Native.WM_ERASEBKGND:
                return new IntPtr(1);

            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps);
                Native.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;

            case Native.WM_POINTERENTER:
                _uiHover = true; _dirty = true; ApplyCursor();
                return IntPtr.Zero;

            case Native.WM_POINTERLEAVE:
                _uiHover = false; _dirty = true; ApplyCursor();
                // 界面那条"人走了"由这里发：方案 B 下，指针离开面板＝离开这块接输入小窗。
                UiGuard("PointerLeave", () => Ui.PointerLeave());
                return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntDown++;
                LastPointerType = ptype;
                _uiHover = true;

                if (UiPointerDown(sx, sy, pressure, ptype == Native.PT_PEN, inverted))
                {
                    // 界面也要捕获：在按钮上滑开、拖出面板，都要继续收到消息。
                    Native.SetCapture(hWnd);
                }
                // 界面没吃这一口也**不落墨、不透给下层**：QueryBounds 声明的就是
                // 界面的地盘（面板底下的墨看不见，将来面板一挪又冒出来）。
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out float pressure,
                                 out bool inverted, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntMove++;
                LastPointerType = ptype;
                _uiHover = true;
                UiPointerMove(sx, sy, pressure, ptype == Native.PT_PEN, inverted);
                _dirty = true;
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (ReadPointer(id, out float sx, out float sy, out float pressure,
                                out bool inverted, out _))
                {
                    StampInput(); _cntUp++;
                    UiPointerUp(sx, sy, pressure, false, inverted);
                }
                Native.ReleaseCapture();
                UiCapturing = false;
                _dirty = true;
                ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                UiCapturing = false;
                _uiHover = false;
                _dirty = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>控制台用法说明。产品界面不会走这里。</summary>
    protected virtual void PrintUsageHelp()
    {
        Console.WriteLine();
        Console.WriteLine("InkTeach 批注原型已启动。键位（可配置，见 " + InkSettings.FilePath + "）：");
        Console.Write(Keys.ToText());
        if (Keys.GlobalFailures.Count > 0)
        {
            Console.WriteLine("  ！下面这些键没注册上（被别的程序占了？换一个键）：");
            foreach (var f in Keys.GlobalFailures) Console.WriteLine("    " + f);
        }
        Console.WriteLine();
    }

    // =====================================================================
    //  选择手势（框选工具 = 选择工具）
    // =====================================================================

    // ---- 拖动期收装饰（方案 A）与拖动预览（方案 B）------------------------

    /// <summary>
    /// 拖动 / 旋转手势里"装饰该收起来"吗（方案 A）。
    ///
    /// 三条判据：
    ///   · 正在拖（<see cref="SelDragging"/>）；
    ///   · **真的动过**（超过 <see cref="ClickToleranceLogical"/>）——单击一下不该闪；
    ///   · 是**移动或旋转** —— 缩放手势不收：拖某个手柄时另外几个手柄是有用的参照，
    ///     而光标也已经说明抓的是哪一个。
    ///
    /// 为什么可以收：按下那一刻指针就被 SetCapture 接管了，手柄与操作条
    /// **此刻根本点不中**，画着就是"看得见、点不到"。
    /// 注意：**脏区照旧算**（见 Overlay.ComputeTransientBounds），靠两帧回溯把
    /// 消失的那一块擦干净。
    /// </summary>
    internal bool SelChromeCollapsed
        => SelDragging && _selDragMoved
        && (_dragIsMove || _dragHandle == SelHandle.Rotate);

    /// <summary>拖动预览在不在：手势进行中，而且真的有一批对象被摘出来了。</summary>
    internal bool DragPreviewActive => SelDragging && _detachOrdered.Count > 0;

    /// <summary>拖动预览要画的那一批（按文档顺序，用 <see cref="DragPreviewMatrix"/> 变换）。</summary>
    internal IReadOnlyList<Stroke> DragPreviewStrokes => _detachOrdered;

    /// <summary>
    /// 拖动预览的实时变换（画布坐标），乘在对象**按下那一刻**的变换之上。
    ///
    /// 内容层那条老路会把对象变换成"按下时的变换 × 这个矩阵"，这里左乘的正是它，
    /// 所以两种画法应当像素一致（自检里有一条专门盯这件事）。
    /// </summary>
    internal Matrix3x2 DragPreviewMatrix => _selDragMatrix;

    /// <summary>这一条现在被摘出内容层了吗（内容层重画一块时要跳过它）。</summary>
    internal bool IsContentDetached(Stroke s) => _detachSet.Count > 0 && _detachSet.Contains(s);

    // =====================================================================
    //  操作条（九格）+ 浮动面板 + 提示条：状态与动作
    // =====================================================================

    /// <summary>选区正在"闪一下"吗（复制成功的反馈）。</summary>
    internal bool SelFlashing => NowMs < SelFlashUntilMs;

    /// <summary>
    /// 改选中对象的颜色。**荧光笔保留半透明**（`InkPalette.ToHighlighter`），
    /// **图像对象跳过**（它没有"颜色"这个概念），其余按同一色值统改。
    /// 返回真正改掉了几条（0 = 一条都没改，调用方据此给提示）。
    /// </summary>
    internal int SetSelectionColor(Color4 baseColor)
    {
        var targets = new List<Stroke>();
        var colors = new List<Color4>();
        int changed = 0;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            // 荧光笔与普通墨迹共用同一个"基色"，但荧光笔要转成半透明——
            // 直接按基色刷会把荧光笔刷成实心（那种"越改越糟"的效果）。
            var c = s.Color.A < 0.99f ? InkPalette.ToHighlighter(baseColor) : baseColor;
            if (s.Color.Equals(c)) continue;
            targets.Add(s);
            colors.Add(c);
            changed++;
        }
        if (changed == 0) return 0;
        Doc.ApplyColors(targets, colors.ToArray());   // 一条动作 = 一步撤销
        // 屏幕反馈按用户要求**只留"选中框上看得见的变化"**（颜色环、框的粗细），
        // 不再弹提示条；需要留痕的写控制台（这一版一直如此）。
        Console.WriteLine($"改颜色：{changed} 个对象");
        return changed;
    }

    /// <summary>
    /// 改选中对象的粗细（**逻辑**像素档位，内部乘 DPI 换成物理宽度）。
    /// 图像对象跳过。返回改了几条。
    /// </summary>
    internal int SetSelectionWidth(float logicalWidth)
    {
        float w = logicalWidth * DpiScale;
        var targets = new List<Stroke>();
        int changed = 0;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (MathF.Abs(s.Width - w) < 0.01f) continue;
            targets.Add(s);
            changed++;
        }
        if (changed == 0) return 0;
        Doc.ApplyWidths(targets, w);
        Console.WriteLine($"改粗细：{changed} 个对象 → {logicalWidth:F1} 逻辑像素");
        return changed;
    }

    /// <summary>
    /// 锁定 / 解锁选中对象（用户 2026-09-16 定的语义：**锁定后能选中、但拖不动**）。
    /// 全部未锁 → 全锁；全部已锁 → 全解；混合 → 全锁。
    /// </summary>
    internal int ToggleSelectionLock()
    {
        if (Doc.Selected.Count == 0) return 0;
        bool anyUnlocked = false;
        foreach (var s in Doc.Selected) if (!s.Locked) { anyUnlocked = true; break; }
        bool lockTo = anyUnlocked;                 // 有没锁的就全锁上，否则全解

        var targets = new List<Stroke>();
        foreach (var s in Doc.Selected) if (s.Locked != lockTo) targets.Add(s);
        int n = targets.Count;
        if (n > 0) Doc.ApplyLock(targets, lockTo);
        if (n > 0)
            Console.WriteLine(lockTo ? $"已锁定 {n} 个对象" : $"已解锁 {n} 个对象");
        return n;
    }

    /// <summary>层级：把选中的整体置顶 / 置底（用户 2026-09-16 定：只做这两个）。</summary>
    internal bool ReorderSelection(bool toFront)
    {
        if (Doc.Selected.Count == 0) return false;
        if (Doc.Selected.Count >= Doc.Strokes.Count)        // 全选时置顶/置底没有意义
        {
            Console.WriteLine("层级：整页都选中了，没有层级可调");
            return false;
        }
        Doc.ReorderSelected(toFront);
        Console.WriteLine(toFront ? "层级：已置顶" : "层级：已置底");
        return true;
    }

    /// <summary>
    /// 点了浮动面板上的东西。返回 true = 这一次按下被面板消费掉了。
    /// 面板的几何全部来自 <see cref="SelectionHandles"/>（渲染与命中同源）。
    /// </summary>
    private bool HandlePanelClick(float x, float y)
    {
        var aabb = LiveSelectionFrame.CanvasAabb;
        float dpi = DpiScale;
        int sc = SelectionHandles.SwatchCount;
        var part = SelectionHandles.PanelPartAt(x, y, aabb, dpi, ViewportCanvas, SelPanelOpen, sc);

        switch (part)
        {
            case SelectionHandles.PanelPart.Slider:
                _sliderDragging = true;
                SetWidthStepAt(x, aabb);
                return true;

            case SelectionHandles.PanelPart.StyleSolid:
                return true;                       // 现在就是实线，点了不变

            case SelectionHandles.PanelPart.StyleDashed:
                // 用户 2026-09-16 定：先放控件，虚线底层下一批做（要加 dash 渲染 + 存档一位）。
                Console.WriteLine("虚线：底层还没做（下一批接 dash 渲染）");
                return true;

            case SelectionHandles.PanelPart.LayerFront:
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: true);
                return true;

            case SelectionHandles.PanelPart.LayerBack:
                SelPanelOpen = SelPanel.None;
                ReorderSelection(toFront: false);
                return true;

            // 导出格式：0 = PNG（透明底）、1 = JPG（白底）。
        }

        if (part >= SelectionHandles.PanelPart.SwatchBase)
        {
            int i = part - SelectionHandles.PanelPart.SwatchBase;
            var swatches = InkPalette.SelectionSwatches;
            if (i < 0 || i >= swatches.Length) return true;
            if (i == swatches.Length - 1)      // 末格 = 自定义取色（本轮占位）
            {
                Console.WriteLine("自定义取色：下一批接系统取色器");
                return true;
            }
            SetSelectionColor(swatches[i].Color);
            return true;
        }
        return false;                          // 面板的空白处：什么都不做
    }

    /// <summary>颜色面板里"粗细滑条"当前的档位表：全荧光笔就用荧光笔那三档。</summary>
    private float[] WidthStepsForSelection()
    {
        bool anyPen = false, anyHighlighter = false;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            if (s.Tool == Tool.Highlighter) anyHighlighter = true; else anyPen = true;
        }
        return (anyHighlighter && !anyPen) ? HighlighterWidthPresets : WidthPresets;
    }

    /// <summary>把滑条拖到 x 处 → 吸附到最近的档位并应用。</summary>
    private void SetWidthStepAt(float x, in RectF aabb)
    {
        var steps = WidthStepsForSelection();
        int i = SelectionHandles.SliderNearestStep(x, steps.Length, aabb, DpiScale, ViewportCanvas,
                                                   SelectionHandles.SwatchCount);
        SetSelectionWidth(steps[i]);
    }

    /// <summary>当前选区在滑条上对应的档位（取第一条非图像对象）。</summary>
    internal int SliderStepOfSelection(in RectF aabb)
    {
        var steps = WidthStepsForSelection();
        float wLogical = 0f;
        foreach (var s in Doc.Selected)
        {
            if (s.IsImage) continue;
            wLogical = s.Width / DpiScale;
            break;
        }
        int best = 0; float bestD = float.MaxValue;
        for (int i = 0; i < steps.Length; i++)
        {
            float d = MathF.Abs(steps[i] - wLogical);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>选区里的档位表长度（渲染滑条刻度、命中同源用）。</summary>
    internal int SliderStepCount() => WidthStepsForSelection().Length;

    /// <summary>
    /// 操作条上鼠标悬停的是哪一格（-1 = 没在条上）。**只在没按住时算**：
    /// 拖动中指针早就离开按钮了，重算只会让高亮乱跳。
    /// </summary>
    private void UpdateBarHover(float x, float y)
    {
        int hover = -1;
        if (Tool == Tool.Marquee && Doc.Selected.Count > 0 && !_drawing)
        {
            var aabb = LiveSelectionFrame.CanvasAabb;
            if (SelBarCollapsed)
            {
                var dot = SelectionHandles.BarCollapsedRect(aabb, DpiScale, ViewportCanvas);
                hover = dot.Contains(x, y) ? (int)SelBarButton.Collapse : -1;
            }
            else hover = SelectionHandles.BarButtonAt(x, y, aabb, DpiScale, ViewportCanvas);
        }
        if (hover == SelBarHover) return;
        SelBarHover = hover;
        _dirty = true;
    }

    /// <summary>
    /// 手势开始：把这一批对象从**内容层**里摘出去（方案 B）。
    ///
    /// 三步，代价 O(文档条数)，而且一个手势只做一次：
    ///   ① 收成集合（内容层重画时判定 O(1)）；
    ///   ② 按文档顺序排一份（画的时候要 z 序，集合的遍历顺序不是 z 序）；
    ///   ③ 把它们**按下那一刻**压过的块标脏一次 —— 重画那些块时跳过这一批，
    ///      于是"它们原来待的地方"在内容层里当场就干净了；之后的整个手势期间
    ///      内容层一帧都不用再动（相机滚动也照旧成立，因为块活在画布空间）。
    /// </summary>
    private void DetachForDrag()
    {
        _detachSet.Clear();
        _detachOrdered.Clear();
        foreach (var s in _dragTargets) _detachSet.Add(s);
        if (_detachSet.Count > 0)
        {
            foreach (var s in Doc.Strokes)
                if (_detachSet.Contains(s)) _detachOrdered.Add(s);
            foreach (var s in _dragTargets) Doc.InvalidateContent(s.PaddedBounds);
        }
        _dragPrevBounds = PreviewBounds(Matrix3x2.Identity);
    }

    /// <summary>手势结束：这一批回到内容层（下一次重画就带上它们了）。</summary>
    private void ReattachAfterDrag()
    {
        _detachSet.Clear();
        _detachOrdered.Clear();
        _dragPrevBounds = RectF.Empty;
        _selDragMoved = false;
    }

    /// <summary>
    /// 这一批对象在矩阵 <paramref name="m"/> 下占的画布范围（轴对齐）。
    ///
    /// 脏区必须是正矩形，而对象和选区框都可能是斜的，所以取四个角变换后的包围盒。
    /// 往外多留 2 逻辑像素：抗锯齿的边缘会跑出精确包围盒一点点，漏了就会在屏幕上
    /// 留一条发丝一样的残影。
    /// </summary>
    private RectF PreviewBounds(in Matrix3x2 m)
    {
        var r = RectF.Empty;
        foreach (var s in _dragTargets)
        {
            var b = s.PaddedBounds;
            if (b.IsEmpty) continue;
            if (m.IsIdentity) { r.Add(b); continue; }
            r.Add(TransformBounds(b, m));
        }
        return r.IsEmpty ? r : r.Inflate(2f * DpiScale);
    }

    /// <summary>把一个轴对齐矩形过一遍矩阵，取四个角变换后的新包围盒。</summary>
    private static RectF TransformBounds(in RectF b, in Matrix3x2 m)
    {
        if (b.IsEmpty) return RectF.Empty;
        if (m.IsIdentity) return b;
        var p0 = Vector2.Transform(new Vector2(b.MinX, b.MinY), m);
        var p1 = Vector2.Transform(new Vector2(b.MaxX, b.MinY), m);
        var p2 = Vector2.Transform(new Vector2(b.MaxX, b.MaxY), m);
        var p3 = Vector2.Transform(new Vector2(b.MinX, b.MaxY), m);
        var r = RectF.Empty;
        r.Add(p0.X, p0.Y); r.Add(p1.X, p1.Y);
        r.Add(p2.X, p2.Y); r.Add(p3.X, p3.Y);
        return r;
    }

    /// <summary>
    /// **这一帧该画的那个选中框**：拖动预览期间要用实时变换，不能再用模型里的。
    ///
    /// 为什么必须分开：方案 B 里模型到松手才动（那样内容层才能一帧都不重画），
    /// 于是 <see cref="SelectionHandles.FrameOf"/> 拿到的是**按下那一刻**的框 ——
    /// 直接拿它画，拖动中框和手柄会留在原地不动、内容和框分家（松手才"啪"地跳回来）。
    ///
    /// 算法和 <see cref="SelectionHandles.FrameOf"/> 是**同一条**：每个对象的**世界**
    /// 包围盒先过一遍实时矩阵，再并成轴对齐的框。所以一条和多条没有分支——
    /// 框永远正着，旋转中每帧重新贴合当前内容（会"呼吸"，这是"框永远正着"的代价，
    /// 见 SelectionFrame 的注释）。
    /// 不在拖动中就原样返回（绝大多数帧走这条，零开销）。
    /// </summary>
    internal SelectionFrame LiveSelectionFrame
    {
        get
        {
            var f = SelectionHandles.FrameOf(Doc.Selected);
            if (!DragPreviewActive || f.IsEmpty) return f;
            var r = RectF.Empty;
            foreach (var s in Doc.Selected)
            {
                // 锁定的对象**不跟着动**（用户 2026-09-16 定的 B 语义），
                // 所以它们那块按"当前位置"算——不然框会跟着它们一起漂。
                r.Add(s.Locked ? s.WorldInkBounds : TransformBounds(s.WorldInkBounds, _selDragMatrix));
            }
            return new SelectionFrame { Local = r, ToCanvas = Matrix3x2.Identity };
        }
    }

    /// <summary>
    /// 框选工具按下时的分流。返回 true 表示这次按下已经被选择手势接掉。
    ///
    /// 顺序有讲究（从 1 到 5，**不能换**）：
    ///   1. 操作条按钮——它和手柄、框都紧挨着，放后面就点不中；
    ///   2. 手柄（缩放 / 旋转）；
    ///   3. 在**当前选区框内** → 整体拖动；
    ///   4. **点选**：按在一条墨迹上就选中它（没有选中时也走这条）；
    ///   5. 都没有 → 返回 false，交给外面的框选。
    ///
    /// 第 3 步必须在第 4 步之前：否则"拖动已选中的一块"会被判成"点选其中一条"，
    /// 多选就再也搬不动了。（2026-09-15 实现点选时定的顺序。）
    /// </summary>
    private bool TryBeginSelectionGesture(float x, float y, bool shift, bool alt)
    {
        _dragHitStroke = null;
        _pendingClone = null;
        float dpi = DpiScale;

        bool move = false;
        var h = SelHandle.None;
        var frame = SelectionHandles.FrameOf(Doc.Selected);

        // —— ⓪ 收起态圆钮 / 浮动面板 / 操作条：优先级最高（它们盖在别的东西上面）——
        //
        // 顺序：**面板内部 → 操作条 → 面板外部**。为什么把"条"放在"面板外部"之前：
        // 面板开着时去点"层级"那一格，应该直接换成层级面板，而不是"先关掉再被这一格
        // 又打开"。而点面板和条以外的任何地方，才把面板收起来（并且这一次点击**继续**
        // 往下走，于是"点空白取消选中"的习惯不会因为面板开着就失灵）。
        if (Doc.Selected.Count > 0)
        {
            var aabb0 = LiveSelectionFrame.CanvasAabb;

            if (SelBarCollapsed)
            {
                // 收起态：只有一个圆钮，点它展开（框和手柄照旧，收起的只是"条"）。
                var dot = SelectionHandles.BarCollapsedRect(aabb0, dpi, ViewportCanvas);
                if (dot.Contains(x, y))
                {
                    SelBarCollapsed = false;
                    _dirty = true;
                    return true;
                }
            }
            else if (SelPanelOpen != SelPanel.None
                     && SelectionHandles.PanelContains(x, y, aabb0, dpi, ViewportCanvas,
                                                       SelPanelOpen, SelectionHandles.SwatchCount))
            {
                HandlePanelClick(x, y);
                return true;                        // 面板上的点击一律吃掉（包括空白处）
            }
            else
            {
                int btn0 = SelectionHandles.BarButtonAt(x, y, aabb0, dpi, ViewportCanvas);
                if (btn0 >= 0) { RunBarAction(btn0, frame, aabb0); return true; }
                if (SelPanelOpen != SelPanel.None) { SelPanelOpen = SelPanel.None; _dirty = true; }
            }
        }

        // —— 1~3：有选中时才谈得上（没选中就直接跳到第 4 步的点选）——
        if (Doc.Selected.Count > 0)
        {
            // 操作条定位用框的**画布轴对齐范围**：框本身可能是斜的，但"它占了屏幕上
            // 哪一块"永远是个正矩形，操作条贴在那个矩形的下面才对。
            var aabb = frame.CanvasAabb;

            // （操作条与面板已经在 ⓪ 里处理过了，这里只剩手柄 / 框内拖动 / 点选）
            h = SelectionHandles.HitTest(x, y, frame, dpi);
            if (h == SelHandle.None)
            {
                // 没点在手柄上：把指针变回框坐标，看是不是落在框里（整体拖动）。
                var lp = frame.ToLocalPoint(new Vector2(x, y));
                move = lp.X >= frame.Local.MinX && lp.X <= frame.Local.MaxX
                    && lp.Y >= frame.Local.MinY && lp.Y <= frame.Local.MaxY;

                // 顺手记下"指针底下是哪一条"：松手时若一点没移动，就把多选**收窄成只选它**
                // （PPT/Figma 的行为）。落在框内空白处 → 记不到东西 → 松手不改选择。
                if (move) _dragHitStroke = Doc.HitObjectAt(x, y, ClickToleranceLogical * dpi);
            }
        }

        // —— 4：点选（2026-09-15 新增）——
        if (h == SelHandle.None && !move)
        {
            var hit = Doc.SelectAt(x, y, ClickToleranceLogical * dpi,
                                   additive: shift, subtractive: alt);
            if (hit == null) return false;                // 5：空白 → 交给框选

            _dragHitStroke = hit;
            _dirty = true;
            // 加选/减选时**不进拖动**：方便连着点几条攒出一个选择。
            if (shift || alt) return true;

            // 无修饰键：选中它并**直接进入整体拖动**（点住就能拖，和 PPT 一样）。
            frame = SelectionHandles.FrameOf(Doc.Selected);
            move = true;
        }

        // —— 4.5：复制拖拽模式：按住选中内容拖动 = **拖出一份副本**（原件不动）——
        // 克隆放在这里（按下那一刻），副本与原件完全重合，随后这一次拖动移动的就是副本；
        // 松手时把"插入副本 + 移动副本"合成一步撤销（见 EndSelDrag）。
        if (move && CopyDragArmed)
        {
            _pendingClone = Doc.CloneSelectedInPlace();
            if (_pendingClone.Strokes.Count > 0)
            {
                frame = SelectionHandles.FrameOf(Doc.Selected);   // 副本与原件重合，框不变
                _dirty = true;
            }
            else _pendingClone = null;
        }

        _dragHandle = h;
        _dragIsMove = move;
        _dragFrame = frame;
        _dragStartPoint = new Vector2(x, y);
        _selDragMatrix = Matrix3x2.Identity;
        _selDragMoved = false;
        _dragTargets = Doc.Selected.ToArray();

        // 锁定（用户 2026-09-16 定的 B 语义）：**能选中，但拖不动**。
        // 做法是把锁定的从"这一次手势要动的那批"里摘掉——选区框照旧画（框算的是
        // Doc.Selected），只是它们不跟着动。全锁时提示一句、并且什么都不动。
        {
            int lockedCount = 0;
            foreach (var s in _dragTargets) if (s.Locked) lockedCount++;
            if (lockedCount > 0)
            {
                if (lockedCount == _dragTargets.Length)
                {
                    // 锁定的对象拖不动——这一句只在控制台留痕（屏幕反馈按用户要求去掉了提示条；
                    // 锁图标本身就是状态指示）。
                    Console.WriteLine($"拖动：这 {lockedCount} 个对象已锁定，拖不动");
                    SelDragging = true;                 // 吃掉这次按下：别退化成重新框选
                    _dragTargets = Array.Empty<Stroke>();
                    _dragStartXform = Array.Empty<Matrix3x2>();
                    _dragHandle = SelHandle.None;
                    _dragIsMove = false;
                    _dirty = true;
                    return true;
                }
                var movable = new List<Stroke>(_dragTargets.Length - lockedCount);
                foreach (var s in _dragTargets) if (!s.Locked) movable.Add(s);
                _dragTargets = movable.ToArray();
                Console.WriteLine($"拖动：{lockedCount} 个对象已锁定，不跟着动");
            }
        }

        _dragStartXform = new Matrix3x2[_dragTargets.Length];
        for (int i = 0; i < _dragTargets.Length; i++)
            _dragStartXform[i] = _dragTargets[i].Transform;

        // 方案 B：把这一批从**内容层**摘出去，改由浮动层画实时预览。
        // 模型在整个手势里一个字都不改，所以内容层一帧都不用重画。
        DetachForDrag();

        // 旋转：累积角从 0 起，指针的"上一帧位置"就是按下这一点。
        // 必须在这里归零——上一次拖拽攒下来的角度绝不能带进这一次。
        _rotAccumDeg = 0f;
        _rotPrevPoint = frame.ToLocalPoint(_dragStartPoint);

        SelDragging = true;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 自检用：直接走一次"选择手势分流"（操作条 / 手柄 / 框内拖动 / 点选都从这儿进）。
    /// 返回 false = 这次按下没被选择手势接掉（调用方会起框选）。
    ///
    /// 为什么把修饰键当参数：点选里的 Shift/Alt 分支要看真键盘，自检没法按着 Shift 跑，
    /// 于是"读键盘"这一步留在调用方（鼠标按下那处），判定函数保持纯逻辑。
    /// </summary>
    internal bool SelectionGestureForTest(float x, float y, bool shift = false, bool alt = false)
        => TryBeginSelectionGesture(x, y, shift, alt);

    /// <summary>自检用：结束一次选择手势（松手）。</summary>
    internal void EndSelectionGestureForTest() => EndSelDrag();

    /// <summary>
    /// 拖动中：每帧都从**按下那一刻的变换**重算，而不是在上一帧结果上继续乘。
    /// 后者会累积浮点误差，拖得越久偏得越多，撤销也回不到原样。
    /// </summary>
    private void UpdateSelDrag(float x, float y)
    {
        var cur = new Vector2(x, y);
        bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
        // Alt = 临时关掉吸附。Shift 是"硬网格 15°"，Alt 是"完全自由"，两个修饰键
        // 各管一头，中间那档（默认的 90° 软吸附）不用按键。
        bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;

        Matrix3x2 m, localM;
        if (_dragIsMove)
        {
            // 整体移动：指针在画布上走多少，对象就走多少。**不能**在框坐标里算
            // 再共轭回来——框是斜的时候那样会走偏方向。
            m = Matrix3x2.CreateTranslation(cur - _dragStartPoint);
        }
        else if (_dragHandle == SelHandle.Rotate)
        {
            // —— 旋转：**一帧一步地累积**（角度不设上限，见 SelRotationDegrees）——
            //
            // 角度在**框坐标**里量（和真正施加的旋转矩阵同一套输入）。
            // 选中框现在一律轴对齐（ToCanvas 是单位阵），所以框坐标 == 画布坐标，
            // 量出来的角度就是屏幕上看到的角度——这也正是"读数按眼睛看的方向"那条规则。
            var c = new Vector2((_dragFrame.Local.MinX + _dragFrame.Local.MaxX) * 0.5f,
                                (_dragFrame.Local.MinY + _dragFrame.Local.MaxY) * 0.5f);
            var p = _dragFrame.ToLocalPoint(cur);
            _rotAccumDeg += SelectionHandles.RotationStepDegrees(c, _rotPrevPoint, p);
            _rotPrevPoint = p;

            // 吸附作用在**累积角**上：90° / 15° 的整数倍在负角度、超过一圈的角度上照样对得上。
            float localDeg = SelectionHandles.SnapRotationDegrees(_rotAccumDeg, shift, alt, out bool snapped);

            // 这一段是"框坐标和屏幕反手时，把读数翻回眼睛看到的方向"的通用保险：
            // 镜像过的对象 + 斜框的组合下，本地角度的正负和屏幕是反的。
            // 选中框改成**一律轴对齐**之后 ToCanvas 恒为单位阵，这里恒不触发；
            // 留着是因为公式本来就要覆盖"框有自己的朝向"那一天（见 SelectionFrame 的注释）。
            SelRotationDegrees = SelectionHandles.IsMirrored(_dragFrame.ToCanvas) ? -localDeg : localDeg;
            SelRotationSnapped = snapped;
            SelRotating = true;

            // 矩阵用**吸附后的那个数**（没镜像时它和标签上的数一模一样）。
            localM = SelectionHandles.RotateMatrix(localDeg, c);
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }
        else
        {
            // 手柄换算在**框坐标**里做（缩放的锚点是"对角那个手柄"，只有在框坐标里
            // 才是"沿着框的两条边"），再共轭回画布坐标：M = F⁻¹ · M_local · F
            localM = SelectionHandles.DragMatrix(_dragHandle, _dragFrame,
                                                 _dragStartPoint, cur, DpiScale, shift, shift, alt);
            m = Conjugate(_dragFrame.ToCanvas, localM);
        }

        // 方案 B：**不动模型**，实时位移只活在 _selDragMatrix 里，由浮动层画预览。
        //
        // 以前这里是"每帧 SetTransformLive"，它会把渲染版本号抬起来，于是内容层
        // 每一帧都要把"走过的面积"里的所有笔迹重画一遍——实测散布全屏的一小撮
        // 是 19ms/帧，而其中被拖的只有那几条。现在内容层只在**手势开始**被标脏一次
        // （把这一批从块里摘出去那一下），中途一帧都不碰。
        //
        // 脏区 = 上一帧预览的位置 ∪ 这一帧预览的位置：后缓冲里躺着的是两帧前的画面，
        // 而这两块并起来正好覆盖"两帧之间差了哪些像素"（和以前"旧位 ∪ 新位"同一套账）。
        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);
        _selDragMatrix = m;
        _dragPrevBounds = PreviewBounds(m);
        if (!_dragPrevBounds.IsEmpty) Doc.Dirty.Add(_dragPrevBounds);

        // 方案 A：真的动过才收装饰（单击一下不该闪）。
        if (!_selDragMoved
            && Vector2.Distance(cur, _dragStartPoint) > ClickToleranceLogical * DpiScale)
            _selDragMoved = true;

        _dirty = true;
    }

    /// <summary>
    /// 松手：先把模型恢复到按下那一刻，再提交**一条**变换命令。
    ///
    /// 这样拖动过程中的实时预览不产生撤销记录，而撤销一步就精确回到拖动前
    /// ——不会出现"拖的时候动了三十次，要按三十次撤销"那种事。
    /// </summary>
    private void EndSelDrag()
    {
        SelDragging = false;
        SelRotating = false;            // 度数标签只在拖动中出现
        _rotAccumDeg = 0f;              // 下一次拖拽从 0 开始数（标签也不显示了）
        if (_dragTargets == null) return;

        // 一批都没得动（例如选中的全锁着）：只把状态收干净，**不要**提交空动作
        // （提交了就是"点一下多一条空撤销记录"）。
        if (_dragTargets.Length == 0)
        {
            _dragTargets = null;
            _dragStartXform = null;
            _dragHandle = SelHandle.None;
            _dragIsMove = false;
            _dragHitStroke = null;
            _pendingClone = null;
            _selDragMatrix = Matrix3x2.Identity;
            ReattachAfterDrag();
            _dirty = true;
            return;
        }

        for (int i = 0; i < _dragTargets.Length; i++)
        {
            Doc.SetTransformLive(_dragTargets[i], _dragStartXform[i]);
            Doc.Dirty.Add(_dragTargets[i].PaddedBounds);
        }

        Doc.Selected.Clear();
        foreach (var t in _dragTargets) Doc.Selected.Add(t);
        // **一点没移动就别进撤销栈**：按一下选中框（或者点选一条）原本会多出一条
        // "原样"的撤销记录——点选一多，撤销栈里全是这种空记录。
        if (_pendingClone != null)
        {
            // 拖出副本：**插入副本 + 移动副本 = 一步撤销**（拖错了按一次就全回去）。
            if (_selDragMatrix.IsIdentity)
            {
                Doc.CommitCompound(_pendingClone);
            }
            else
            {
                var moveAct = new TransformObjectsAction(Doc.Selected, _selDragMatrix);
                moveAct.Redo(Doc);
                Doc.CommitCompound(_pendingClone, moveAct);
            }
            _pendingClone = null;
        }
        else if (!_selDragMatrix.IsIdentity)
        {
            Doc.ApplyTransform(_selDragMatrix);
        }

        // "点一下把多选收窄成单选"：按下时命中了一条、而这一次**一点没移动** → 只留它
        // （PPT/Figma 的行为）。移动过就正常整体搬，不收窄。
        if (_dragIsMove && _dragHitStroke != null && _selDragMatrix.IsIdentity
            && Doc.Selected.Count > 1 && Doc.Selected.Contains(_dragHitStroke))
            Doc.SelectOnly(new[] { _dragHitStroke });

        _dragTargets = null;
        _dragStartXform = null;
        _dragHandle = SelHandle.None;
        _dragIsMove = false;
        _dragHitStroke = null;
        _pendingClone = null;
        _selDragMatrix = Matrix3x2.Identity;

        // 方案 B：这一批回到内容层（下一次重画就把它们画进块里）。
        // 顺序放在最后：上面那条"提交变换"已经把旧位与新位都标脏了，
        // 所以这一帧的重画一定会带上它们（而不是继续画预览）。
        ReattachAfterDrag();

        _dirty = true;
    }

    /// <summary>
    /// 操作条按钮。下标与 SelectionHandles.BarButtonAt 的返回值和渲染时的
    /// 图标数组一一对应：0 复制 / 1 删除 / 2 左右翻转 / 3 上下翻转 / 4 旋转。
    /// </summary>
    private void RunBarAction(int index, in SelectionFrame frame, in RectF aabb)
    {
        // 下标 ↔ 语义只在 SelBarButton 里写一遍（渲染、命中、动作三处共用它）。
        var btn = (SelBarButton)index;
        switch (btn)
        {
            // **收起工具条**（用户 2026-09-16 更正：条首那个 ✕ 不是"取消选择"）。
            // 收起后只剩一个小圆钮，点它展开；框和手柄照旧，收起的只是"条"。
            case SelBarButton.Collapse:
                SelBarCollapsed = true;
                SelPanelOpen = SelPanel.None;
                CopyDragArmed = false;
                break;

            case SelBarButton.Color:
                SelPanelOpen = SelPanelOpen == SelPanel.Ink ? SelPanel.None : SelPanel.Ink;
                break;

            case SelBarButton.Lock:
                ToggleSelectionLock();
                break;

            case SelBarButton.Layer:
                SelPanelOpen = SelPanelOpen == SelPanel.Layer ? SelPanel.None : SelPanel.Layer;
                break;

            case SelBarButton.Export:
                // **直接弹系统"另存为"**，格式在那个对话框的类型栏里选（见 ExportFormats）。
                //
                // 早先前这里先开一层我们自己的两格面板，理由是"系统那个下拉点不开"
                // （用户报的"选不到 jpg"）。那个真因后来查清了：**覆盖层每秒抢一次置顶**，
                // 把对话框的下拉挤掉了（见 ReassertTopmost）。真因修掉之后，
                // 用户 2026-09-17 提的那句就成立了——"那下面这两个图标就没有用了吧，
                // 用户都可以自己保存图片了"：两处选格式反而绕，删掉还少一步。
                ExportSelectionPref();
                break;

            // **进入/退出"复制拖拽模式"**，不是"点一下原地克隆一份"。
            // 抄 InkClass 的结论：点击即克隆那版"副本固定偏移 24px、落点不可控"，已废弃；
            // 现在点图标只进模式（图标高亮 + 框变虚线），之后按住选中内容拖 = 拖出副本。
            case SelBarButton.Copy:
                CopyDragArmed = !CopyDragArmed;
                break;

            // 翻转绕**框自己的轴**。框现在一律轴对齐，所以它就是屏幕的左右/上下翻——
            // 和"框永远正着"同一条规则（见 SelectionFrame 的注释）。
            case SelBarButton.FlipH:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: true)));
                break;
            case SelBarButton.FlipV:
                Doc.ApplyTransform(Conjugate(frame.ToCanvas,
                    SelectionHandles.MirrorMatrix(frame.Local, horizontal: false)));
                break;

            case SelBarButton.Delete:
            {
                int n = Doc.Selected.Count, locked = 0;
                foreach (var s in Doc.Selected) if (s.Locked) locked++;
                Doc.DeleteSelected();
                Console.WriteLine(locked > 0
                    ? $"删除 {n - locked} 个对象（{locked} 个锁定，没删）"
                    : $"删除 {n} 个对象");
                break;
            }
        }
        Laser.Clear();
        // 删除之后选区可能空了；模式跟着选区走（InkClass 同款：选区没了就退出）。
        if (Doc.Selected.Count == 0) CopyDragArmed = false;
        _dirty = true;
    }

    /// <summary>
    /// 把"框坐标下的变换"翻译成"画布坐标下的变换"：<c>F⁻¹ · M · F</c>。
    ///
    /// 框是斜的时候，同一句"沿框的横轴放大两倍"在画布坐标里是个斜的缩放。
    /// 共轭就是在两套坐标之间翻译这件事——不用为"斜着的情况"另写一套公式。
    /// </summary>
    private static Matrix3x2 Conjugate(in Matrix3x2 frame, in Matrix3x2 localM)
    {
        if (frame.IsIdentity) return localM;
        if (!Matrix3x2.Invert(frame, out var inv)) return localM;
        return inv * localM * frame;
    }
    // =====================================================================
    //  批注键盘模式
    // =====================================================================

    /// <summary>
    /// 开关批注键盘模式。
    ///
    /// 开：去掉 WS_EX_NOACTIVATE 并把窗口提到前台，键盘归批注层，编辑类
    ///     快捷键（Ctrl+Z / Ctrl+D / Delete / 方向键…）才有地方落地。
    /// 关：加回 WS_EX_NOACTIVATE，覆盖层回到"永不抢焦点"，键盘还给下层程序
    ///     ——那时候只有全局热键（Ctrl+Alt+…）可用。
    ///
    /// 取舍说清楚：开着的时候，放映中的 PPT 收不到键盘。
    /// </summary>
    internal void SetKeyboardMode(bool on)
    {
        KeyboardMode = on;
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            if (on) ex &= ~Native.WS_EX_NOACTIVATE;
            else ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
                | Native.SWP_NOACTIVATE | 0x0020 /*SWP_FRAMECHANGED*/);
            if (on) Native.SetForegroundWindow(w.Hwnd);
        }
        Console.WriteLine($"批注键盘模式 = {on}"
            + (on ? "（键盘归批注层；此时下层程序收不到键盘）" : "（键盘还给下层程序）"));
        _dirty = true;
    }

    /// <summary>
    /// 批注键盘模式下的按键。**只在 <see cref="KeyboardMode"/> 打开时收到。**
    ///
    /// 键位一律照 Windows 的通用习惯，不自己发明：
    ///   Ctrl+Z 撤销 / Ctrl+Y 重做 / Ctrl+A 全选 / Delete 删除 / Esc 取消选择
    ///   Ctrl+D 复制一份（系统剪贴板还没接，先用 D）
    ///   方向键移动 1 像素，按住 Shift 是 10 像素
    ///
    /// 已知待改：方向键按住会重复触发，每次都是一条撤销记录。要接"连续微调
    /// 合并成一步"，得等编辑命令支持合并（撤销栈里相邻同类动作合并）。
    /// </summary>
    private bool HandleKeyDown(IntPtr wParam)
    {
        // 键位表驱动：按"当前修饰键状态 + 主键"拼成一个和弦，去批注内作用域里查。
        // 查不到就**不吞这个键**（返回 false），交给系统/下层程序——吞掉所有按键
        // 会让批注键盘模式下连输入法都用不了。
        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;

        var chord = new KeyChord(mods, (uint)wParam.ToInt32());
        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null) return false;

        RunAction(hit.Action);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// 全选。顺便切到选择工具——不切的话手柄和操作条不会出现，
    /// 用户会以为"全选没生效"。
    /// </summary>
    internal void SelectAll()
    {
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 滚轮滚动画布。**只改相机偏移 ViewOffsetY，一个对象的数据都不动**——
    /// 这是滚动能做到 O(1) 的前提。绝不能像 InkClass 那样把平移烘焙进点坐标
    /// （那是 O(对象数)/次，一万笔滚一格要重写十万个点）。
    ///
    /// 只做纵向。往下滚 = 内容上移 = 偏移变负。
    ///
    /// 代价：**这里一个像素都不重画**。内容层是画布空间的分块缓存
    /// （CanvasTiles.cs），滚动只是换个位置把已经画好的块贴上去；只有在
    /// 新露出**没画过**的块时才会光栅化那一小块。所以滚一格的代价与文档
    /// 里有多少笔无关——从"整层重画"（一万笔 82~154ms，滚一格卡一下）
    /// 变成"贴图 + 偶尔补一两块"。这也正是 Win32 ScrollWindowEx 和浏览器
    /// 合成器滚动图层用的手法。
    /// </summary>
    internal IntPtr HandleWheel(IntPtr wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        // 往下滚时 delta = -120，要先变负（内容上移）——所以这里是 +=。
        // 写成 -= 会把符号翻过来：滚轮往下 = 视图往上，正好抵掉，
        // 再被"不许滚过顶部"夹回 0，表现就是**完全滚不动**。
        ViewOffsetY += delta / 120f * 72f * DpiScale;   // 一格 = 72 逻辑像素
        // 上下都夹住画布范围（内容边界 ∪ 一屏）。
        // 不夹的话会滚进无尽的空白，而且比例滚动条拿不到有意义的范围。
        // 下边界是"视口底边贴住内容底边"——接着写，内容长出去，范围自己长出来，
        // 所以不会把人卡在底边。
        ClampViewOffset();

        ScrollBarActiveAtMs = NowMs;                     // 滚动时让滚动条露面
        // 注意：**不调 Doc.InvalidateAll()**。那会把整个文档标脏、让所有分块
        // 重画——正是这次要拔掉的病根。分块缓存自己会发现相机变了，只做重合成。
        _dirty = true;
        return IntPtr.Zero;
    }}
