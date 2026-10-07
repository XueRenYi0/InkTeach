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
    /// 界面**会画到 <see cref="QueryBounds"/> 外面**的那一圈余量（逻辑像素），默认 0。
    ///
    /// 引擎是按 QueryBounds 裁剪界面绘制的。可界面偏偏有"必须画在外面"的东西——
    /// 投影、浮出的预览。不给这一圈，它们会被裁掉：**屏幕上什么都看不见，
    /// 而离屏出图看得见**（那条路不裁剪），于是"图里好好的、真机上是空的"。
    ///
    /// 为什么不干脆把 QueryBounds 放大：那份矩形同时是**命中测试与输入小窗**的矩形，
    /// 放大等于"面板旁边一圈点不动、画不了线"。所以这里把"占地方"和"画出来"分开。
    ///
    /// 给默认实现是为了不打扰已有的界面实现（它们确实没有画到外面的东西）。
    /// </summary>
    float PaintMargin => 0f;

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

    /// <summary>
    /// **主屏的工作区**（逻辑像素）：屏幕减掉任务栏之后剩下的那块矩形。
    /// 界面算"默认位置"用它——见 <c>InkEngine.PrimaryWorkArea</c> 的注释（用户 2026-09-27）。
    /// </summary>
    RectF WorkArea { get; }

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

    /// <summary>
    /// 某个动作**当前生效的键位文本**（如 "Ctrl+P"；没有绑定返回 null）。
    /// 悬停提示用它——键位的唯一起源是引擎的 <c>KeyBindings</c>，界面不自抄一份：
    /// 用户改了 `settings.json`（甚至把某个键取消掉）之后，提示跟着变。
    /// </summary>
    string KeyText(KeyAction action);

    /// <summary>
    /// 界面把自己的**浮层主题**推给引擎：选中操作条、颜色/粗细/层级面板、导出格式面板、
    /// 旋转读数都用它的颜色与投影画。
    ///
    /// 为什么要有这一条：浮层和工具条**必须是同一套颜色与投影**（"操作条和工具条要是一家"），
    /// 而它们分别在两个工程里画。各写一份的下场实测过一次——浅色主题改完，
    /// 工具条有了冷灰描边和五层投影，操作条还是蓝灰描边、**一层投影都没有**，
    /// 挨在一起一眼就能看出不是一套。
    ///
    /// 边界不变：**引擎不解释偏好**。谁该用深色是界面的事，界面换主题就推一次，
    /// 引擎照单画；不推 = 引擎用 <see cref="UiTheme.Default"/> 兜底。
    /// </summary>
    void SetFloatingTheme(UiTheme theme);
}

/// <summary>
/// 课堂计时器的三种模式（「更多 → 课堂」页选，运行卡片画在引擎侧）。
///
/// 三个入口各自独立（2026-10-02 用户拍板）：倒计时有预设与 ±1 分步进，
/// 正计时只有分秒，秒表多两位小数——老师和学生报数时读的是不同的精度。
/// </summary>
public enum TimerMode
{
    /// <summary>倒计时：到 0 响一声、闪三下，停在 00:00（再点一下从头开始）。</summary>
    Countdown = 0,
    /// <summary>正计时：从 0 往上（MM:SS / H:MM:SS）。</summary>
    CountUp = 1,
    /// <summary>秒表：从 0 往上，显示到 0.01 秒。</summary>
    Stopwatch = 2,
}

/// <summary>界面对引擎的全部操作能力。刻意做窄，防止界面越权。</summary>
public interface IEngineCommands
{
    void SetTool(Tool tool);

    /// <summary>面板点橡皮格：切回上次用的橡皮形态（整笔/面积），顺手关穿透。</summary>
    void SetEraserPreferred();
    /// <summary>
    /// **换下一档抛物线开口方向**（向上 → 向右 → 向下 → 向左 → 向上）。
    ///
    /// 只动"下一笔抛物线朝哪开"，**不改已经画好的那些**——用户 2026-09-20 明确说
    /// "不打算从选中框调朝向"。入口在图形面板：那一格**已经选中抛物线时再点一次**。
    /// </summary>
    void CycleParabolaAxis();
    /// <summary>
    /// **换下一档直线的线型**（实线 → 虚线 → 点线 → 实线）。
    ///
    /// 只动"下一笔直线用什么线型"，**不改已经画好的那些**（那些各存各的，要改走
    /// 选中后的操作条面板）。入口和抛物线同一个位置：图形面板里「直线」那一段
    /// **已经选中直线时再点一次**——用户 2026-09-20 要的就是"省几个空间格"，
    /// 不为虚线直线 / 点线直线各开一格。
    /// </summary>
    void CycleLineDash();
    /// <summary>
    /// **换下一档立体**：3 → 4 → 5 → 6 → 3（底面几边形）——只动**当前工具那一格**，
    /// 另外两格的档**不受影响**（各记各的，见 <see cref="UiState.SidesOf"/>）。
    ///
    /// 和 <see cref="CycleLineDash"/> 完全同构：入口是图形面板里那一格
    /// **已经选中它时再点一次**（用户 2026-09-20："做成像直线切换那样切换三四五六"）。
    /// 只动"下一笔用几边形"，**不改已经画好的那些**（它们各存各的）。
    /// </summary>
    void CycleSolidSides();
    /// <summary>
    /// **换下一档"双曲线画不画渐近线"**（有 → 无 → 有）。
    ///
    /// 只动"下一笔双曲线画不画那两条虚线"，**不改已经画好的那些**（它们各存各的）。
    /// 入口和抛物线 / 直线 / 棱柱同一个位置：图形面板里「双曲线」那一段
    /// **已经选中它时再点一次**——用户 2026-09-22 要的就是这个："图标就按照有渐近线和无渐近线"。
    ///
    /// ⚠ 画的过程中**一律画**那两条虚线（它们是画法的向导），松手那一刻才按档收口。
    /// </summary>
    void CycleHyperbolaAsymptotes();
    /// <summary>
    /// **开/关图库面板**（"我的图形"，2026-09-22 用户要的"图像收藏"）。
    ///
    /// 入口：图形面板那一格的**最后一段**「图库」（那一段是动作、不是图形工具，
    /// 见 `FullUi.LibrarySegment`）。面板本身由引擎画（缩略图是现场画笔迹，不是位图），
    /// 所以界面这边只需要这一句开关。
    /// </summary>
    void ToggleLibraryPanel();
    /// <summary>
    /// **换下一档"椭圆（带焦点）画不画焦点三角形"**（有 → 无 → 有）。
    ///
    /// 两档都是"椭圆 ＋ 两个焦点"，差别只在**连不连** F₁P、F₂P 那两条边
    /// （用户 2026-09-22 定的口径："还是画焦点，只是不连三角形"）。
    /// 入口同上：那一格**已经选中它时再点一次**；只动"下一笔"，不改已经画好的。
    /// </summary>
    void CycleEllipseFocusTriangle();
    void SetColor(Color4 color);
    void SetWidth(float logicalPx);
    /// <summary>
    /// **笔的线型**（实线 / 虚线 / 点线）。色带条上那个"虚实线切换"用它——
    /// 只影响**以后新画的笔迹**，不碰已经画好的（那些走选中后的操作条面板）。
    /// </summary>
    void SetDash(StrokeDash dash);
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
    /// 截图模式：<paramref name="hideInk"/> true = **隐藏窗口截图**（抓之前把覆盖层藏起来，
    /// 拍到的只有下层内容——屏幕上"我们的笔迹和工具条整个消失"）；false = **截图**
    /// （连板书一起拍，只把我们的取景层藏掉）。参考 InkClass 的两项菜单
    /// （快速截图 / 隐藏界面截图）。
    ///
    /// 它只**换档**、不进取景；真进取景是 <see cref="EnterCapture"/>（8.3.1：
    /// 面板上点哪一段，就用哪种模式进屋）。
    /// </summary>
    void SetCaptureHideInk(bool hideInk);

    /// <summary>
    /// **进入截图取景**（8.3.1，照微信的节奏）：整屏立刻灰下来（遮罩 + 冻结帧当底）、
    /// 系统十字全程跟着、顶部一行提示 + 右上角「✕ 取消」。
    ///
    /// 入口是面板「截屏」格上带里的那两个模式段（点哪段 = 用哪种截法进屋）。
    /// <see cref="SetCaptureHideInk"/> 负责"选哪种"，这一条负责"进屋"。
    /// </summary>
    void EnterCapture();

    /// <summary>
    /// **白板的不透明度**（0.35～1，1 = 实心）。调小 → 下面的题目隐约透出来，
    /// 而**笔迹照样是实的**（半透明只作用于底色那一层）。
    /// </summary>
    void SetBoardOpacity(float opacity);

    /// <summary>
    /// **粘贴**：剪贴板里有我们的对象就粘成可编辑对象，否则当图粘到视口左上角。
    /// 和批注内的 `Ctrl+V` 是同一个动作——**给没有键盘的触摸屏 / 手写板留的入口**
    /// （用户 2026-09-17："截图要不要增加一个粘贴功能…如果是触摸屏或者手写板可能没有键盘"）。
    /// </summary>
    void Paste();

    /// <summary>框选工具下的选择方式：矩形框（碰到墨就选中）／自由套索（圈住 80% 才选中）。</summary>
    void SetSelectMode(SelectMode mode);

    /// <summary>
    /// **新画的坐标系要不要网格**。界面在启动时把上次的偏好推一次（见 FullUi.LoadPrefs）。
    ///
    /// 它只影响**以后画的**：已经画在板上的坐标系各存各的（见 Stroke.Grid），
    /// 改这个开关不会动它们——对象要自包含，不能长大了还受一个全局开关摆布。
    /// </summary>
    void SetCoordGridDefault(bool on);

    /// <summary>
    /// 「更多」抽屉里"停顿成型"那一行（**默认开**，见 计划-图形工具.md §四十二）。
    /// 它只管"以后画的那些参不参与"，不动已经画在板上的东西。
    /// </summary>
    void SetDwellShape(bool on);

    /// <summary>
    /// **压感 → 粗细**的开关（「更多 → 设置 → 书写 → 压感粗细」，2026-10-01 加）。
    ///
    /// 默认**开**；关掉 = **整块板上的压感笔迹立刻等宽**（湿墨也一样），
    /// 手写板的流畅 / 预测 / 采样路径完全不受影响——它只决定"压力参不参与粗细"。
    ///
    /// ⚠ 这是一条**渲染期**开关：文档里每个点存的压力值和每条笔迹的 `HasPressure`
    /// **一个字节都不动**，重新打开就恢复原来的粗细。和 `--nopressure` 是同一条口径
    /// （那个是给对照实验用的命令行版本，它优先）。
    /// </summary>
    void SetPressure(bool on);

    /// <summary>「设置 → 书写 → 精细笔迹」：原始输入补点总开关（2026-10-07 加）。</summary>
    void SetFineStroke(bool on);

    /// <summary>
    /// **悬停提示的总开关**（「更多 → 设置 → 外观 → 悬停提示」，2026-10-02 加）。
    ///
    /// 默认**开**。界面层的提示由界面自己管（它读同一个偏好），这一条只负责
    /// **引擎自己画的浮层**（选中操作条、PPT 控件条与长按菜单）。
    /// 界面在 `LoadPrefs` 和点那一行时各推一次——引擎不读界面偏好（分层纪律）。
    /// </summary>
    void SetTooltips(bool on);

    /// <summary>
    /// **触摸手势总开关**（「更多 → 设置 → 书写 → 触摸手势」，2026-10-05 加，用户点名要的"保险丝"）。
    ///
    /// 默认**开**；关掉 = **只剩单指书写**——双指手势（漫游/翻页/选中变换）、
    /// ≥3 指擦、长按选择、两指点选、单指漫游全部停用（闸门在 `TouchGestures.Enabled`，
    /// 每条判定各自读它）。正在跑的手势就地中断，当前这一笔照常收尾。
    /// 学校大屏万一遇到手势 bug，老师一键退回"纯单指 + 菜单"。
    /// </summary>
    void SetTouchGestures(bool on);

    // [删除 2026-10-05] `SetPredict(bool)`：墨迹预测开关随老预测系统移除。
    // 恢复见 `已停用-渲染实验.md`。

    /// <summary>
    /// 「更多」抽屉里"坐标系网格"那一行被点了一下。
    /// **选中了坐标系就改它们，没选中就翻"新画的默认值"**（语义见 Engine.ToggleSelectionGrid）。
    /// 返回改了几个对象（0 = 改的是默认值，界面据此决定要不要把偏好落盘）。
    /// </summary>
    int ToggleCoordGrid();

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
    /// <summary>
    /// 「更多」抽屉里的「检查更新」被点了一下。
    ///
    /// **没配更新源就什么都不做**（只把状态文字改成"未配置更新源"）——这是默认状态，完全正常。
    /// 检查在**后台线程**做（网络最长 15 秒），所以界面绝不会卡。
    /// </summary>
    void CheckUpdate();

    /// <summary>
    /// 已经查到新版本了，再点一下就**下载 → 校验 sha256 → 换壳重启**。
    /// 下载也在后台线程；下完由引擎拉起换壳脚本并自己退出（脚本等我们退了才换文件）。
    /// 校验不过一律不装（宁可留在旧版本）。
    /// </summary>
    void ApplyUpdate();

    void Restart();

    void Quit();

    /// <summary>
    /// **保存墨迹到 .inkb**（墨迹 A，2026-10-01）：弹系统"另存为"，写整块白板。
    ///
    /// 白板模式有效；**放映中不响应**（`Save(doc)` 只写当前页，手动保存整份 PPT 批注
    /// 是"批注包"的活，见 计划 6.4.1）。结果写进 <see cref="UiState.InkStatus"/>。
    /// </summary>
    void SaveInkFile();

    /// <summary>
    /// **从 .inkb 打开墨迹**（墨迹 A）：弹系统"打开"，替换当前板书。
    ///
    /// 打开前**自动写一份"打开前备份"**（最近 5 份轮转）；失败不动文档、不弹窗，
    /// 结果写进 <see cref="UiState.InkStatus"/>。放映中不响应。
    /// </summary>
    void OpenInkFile();

    /// <summary>
    /// **保存图片**（2026-10-02「更多 → 墨迹 → 保存图片」）：把**整块板书**渲染成图片
    /// 存盘（默认 JPEG 白底，好发微信/邮件），走系统"另存为"（透明 PNG / JPEG 白底 /
    /// PNG 白底 / BMP 四种照旧）。
    ///
    /// 与选中操作条「导出」的分工：导出只导**选中的**、默认透明底（贴课件/抠图）；
    /// 这里导**整块板书**、默认白底（发学生）。空板书 / 放映中不响应；
    /// 不碰选区、不写剪贴板。结果写进 <see cref="UiState.InkStatus"/>。
    /// </summary>
    void SaveBoardImage();

    /// <summary>
    /// **开始墨迹回放**（墨迹 C）：按当时的速度重演**当前一屏**的笔迹。
    ///
    /// 只读模式：不动文档/撤销栈/选中；相机锁定；点画布暂停/继续（不落墨）、
    /// 控制条上有播放/暂停、四档倍速、进度、关闭。开始时会顺手关掉穿透。
    /// 这一屏没有笔迹 / 截图取景中 / 放映中（D 之前）都不响应。
    /// </summary>
    void StartReplay();

    /// <summary>停掉回放（退出后一切复原）。</summary>
    void StopReplay();

    /// <summary>
    /// 打开**计时器窗口**（启动器「课堂 → 计时器」）：1:1 复刻 InkClass 的独立居中窗
    /// （浅色面板 + 环形进度 + 开始/重置/最小化/全屏/关闭；倒计时可点数字改时长）。
    /// </summary>
    void OpenTimerCard();

    /// <summary>
    /// 打开**点名窗口**（启动器「课堂 → 点名」）：900×500 居中窗，左结果 / 右人数与抽奖；
    /// 名单读 `%APPDATA%\InkTeach\Names.txt`，抽过的不重复（抽完自动重置）。
    /// </summary>
    void OpenRollCard();

    /// <summary>「随机一人」：自动抽 1 人、出结果 1.5 秒后自动关（InkClass 快捷态）。</summary>
    void OpenRollOne();
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
    /// **把基色套到某个对象上**：荧光笔（半透明墨）走 <see cref="ToHighlighter"/>，
    /// 其余原样。判据是"这条墨自己的 alpha"。**只有这一份**——原来这条规则在
    /// `SetSelectionColor` 里手写了一遍，自定义取色板又要用（8.2.0），
    /// 抄第二遍就等着哪天两边不一致。
    /// </summary>
    internal static Color4 ForStroke(Color4 baseColor, Stroke s)
        => s.Color.A < 0.99f ? ToHighlighter(baseColor) : baseColor;

    /// <summary>
    /// **笔的色带顺序**（和界面上那排色片一一对应）。放在引擎层是因为**热键换色要用它**
    /// （Ctrl+P 连按 = 换下一个颜色、按住 1 秒 = 回第一个），见 Engine.CycleBandColor。
    /// 界面那边的 `InkUi.Tokens.Palette` 就是**指向这张表**（别各写一份，会飘）。
    ///
    /// 顺序 = 点笔格切色的顺序：常用的排前面（黑红蓝绿黄橙紫白），深色「包边」垫后。
    /// </summary>
    public static readonly (string Name, Color4 Color)[] PenBand =
    {
        // 2026-09-30 砍到 **8 格**（用户定）：删掉原来的 4 个"深色包边"（深蓝/墨绿/酒红/藏青）——
        // 它们是给"浅底上用白笔包边"做的，老师日常用不到，混在循环里只会让人觉得"越转越暗"。
        // 现在绕一圈 8 下（比 12 下快三分之一），而且全是投影上亮得起来的颜色。
        ("黑", new Color4(0.11f, 0.12f, 0.15f, 1f)),
        ("红", new Color4(0.95f, 0.18f, 0.18f, 1f)),
        ("蓝", new Color4(0.13f, 0.45f, 0.90f, 1f)),
        ("绿", new Color4(0.13f, 0.70f, 0.33f, 1f)),
        ("黄", new Color4(0.98f, 0.82f, 0.12f, 1f)),
        ("橙", new Color4(0.98f, 0.55f, 0.09f, 1f)),
        ("紫", new Color4(0.55f, 0.28f, 0.86f, 1f)),
        ("白", new Color4(1.00f, 1.00f, 1.00f, 1f)),
    };

    /// <summary>
    /// **荧光笔的色带顺序**（界面上的 `Tokens.HighlighterPalette` 指向它）。
    /// 表里存的是**基色**，画的时候要过一遍 <see cref="ToHighlighter"/>（`SwitchTool` 里做）。
    /// ⚠ 第一个「荧光黄」的基色必须等于 <see cref="HighlighterDefault"/> 的 RGB——
    /// 否则启动时那个色片不会亮（高亮是按颜色值比的）。
    /// </summary>
    public static readonly (string Name, Color4 Color)[] HighlighterBand =
    {
        ("荧光黄", new Color4(1.00f, 0.85f, 0.15f, 1f)),
        ("荧光绿", new Color4(0.47f, 0.94f, 0.47f, 1f)),
        ("荧光青", new Color4(0.43f, 0.92f, 0.96f, 1f)),
        ("荧光粉", new Color4(1.00f, 0.51f, 0.71f, 1f)),
        ("荧光橙", new Color4(1.00f, 0.67f, 0.27f, 1f)),
    };

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
        ("自定义", new Color4(0f, 0f, 0f, 0f)),      // 8.2.0 起：这一格点开**内置 HSV 小色板**（见 Overlay.DrawPickPanel）
    };
}

/// <summary>
/// **HSV ↔ RGB**（0..1，8.2.0 的自定义取色板用）。
///
/// 为什么放在引擎、只有这一份：色相条和饱和度/明度方块本来就是 HSV 空间的东西，
/// 而引擎其余地方一律 RGB；这个转换只在"取色板进 / 出"两处用，所以给一个十行的
/// 极简实现就够，不引第三方色彩库（和仓库"能用系统/自带就不加依赖"的口径一致）。
/// </summary>
internal static class Hsv
{
    /// <summary>HSV → RGB。h/s/v 都夹在 0..1。</summary>
    public static Color4 ToRgb(float h, float s, float v)
    {
        h = h - MathF.Floor(h);
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);
        float c = v * s;
        float hp = h * 6f;
        float x = c * (1f - MathF.Abs(hp % 2f - 1f));
        float r1, g1, b1;
        switch ((int)MathF.Floor(hp) % 6)
        {
            case 0: r1 = c; g1 = x; b1 = 0f; break;
            case 1: r1 = x; g1 = c; b1 = 0f; break;
            case 2: r1 = 0f; g1 = c; b1 = x; break;
            case 3: r1 = 0f; g1 = x; b1 = c; break;
            case 4: r1 = x; g1 = 0f; b1 = c; break;
            default: r1 = c; g1 = 0f; b1 = x; break;
        }
        float m = v - c;
        return new Color4(r1 + m, g1 + m, b1 + m, 1f);
    }

    /// <summary>RGB → HSV。h 在 0..1（环形），灰度色 h = 0。</summary>
    public static (float H, float S, float V) FromRgb(in Color4 c)
    {
        float r = Math.Clamp(c.R, 0f, 1f), g = Math.Clamp(c.G, 0f, 1f), b = Math.Clamp(c.B, 0f, 1f);
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        float h = 0f;
        if (d > 1e-6f)
        {
            if (max == r) h = ((g - b) / d + 6f) % 6f;
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h /= 6f;
        }
        float s = max <= 1e-6f ? 0f : d / max;
        return (h, s, max);
    }
}

/// <summary>
/// **图形学上那几个"界面也要用"的规则**住在这一份公开的地方。
///
/// 为什么需要它：`Stroke` / `SelectionHandles` 都是引擎**内部**类型，InkUi 看不到
/// （只对 InkTeach 开了 InternalsVisibleTo）。所以凡是"引擎和界面都必须用同一条规则"
/// 的东西，就放到这里——**只有一份实现**，两边都调它，不许各写一份。
/// </summary>
public static class ShapeSpec
{
    /// <summary>
    /// 棱柱底面的**错切量**：相对"底面半高"的比例——**"远处"那一侧往右挪这么多**，
    /// 就是斜二测画法里那条 45° 的纵深轴（这里取一半，稍稍错开一点就够）。
    ///
    /// 它**不是装饰**，是两个真问题的解（用户 2026-09-20 出图时一眼看出来的）：
    ///   · **前后两半完全对齐**：对称压扁的投影下，六棱柱后面那两条被挡住的竖棱
    ///     正好落在前面两条实线的正后方——**完全被挡住，看不见**；
    ///   · **四棱柱没有水平边**：对称投影下它的底面是"正着放的正方形"（左右两条边
    ///     与视线平行 = edge-on），整条立体塌成一块平板。
    /// 错开一点之后：四棱柱的底面成了**前边水平、后边也水平**的平行四边形
    ///（课本上那个样子），六棱柱的虚线也从实线之间露出来了。
    /// </summary>
    public const float PrismDepthSkew = 0.5f;

    /// <summary>
    /// 棱柱**底面顶点的起始角**（度）：`90° + 180°/n`——**正前方（屏幕下方）正好是一条边**。
    /// 三棱柱因此是课本那个帐篷形；偶数边则**恰好有两条水平边**（前边与后边）。
    ///
    /// 画布上（`Stroke.PrismBaseLocal`）和图标上（`IconAtlas.DrawPrism`）**都读它**，
    /// 两边不一致的话，图标画的和画出来的就不是一个东西。
    /// </summary>
    public static int PrismBaseOffsetDegrees(int sides) => 90 + 180 / Math.Max(3, sides);

    /// <summary>
    /// 底面在画面里用的两个尺度：<paramref name="rxUse"/>（横向半边，已经让出错切的位置）
    /// 与 <paramref name="skew"/>（错切量）。**只有这一处实现**——
    /// 顶点位置（`PrismBasePointOffset`）和"哪个侧面看得见"（`PrismFaceVisible`）都从它出发。
    /// </summary>
    public static void PrismFrame(float rx, float ry, out float rxUse, out float skew)
    {
        skew = PrismDepthSkew * ry;
        rxUse = MathF.Max(0.5f, rx - skew * 0.5f);
    }

    /// <summary>
    /// 一个底面顶点**相对底心**的偏移（画面坐标）：输入是它的参数角余弦 / 正弦
    ///（底面上那个**正** n 边形的坐标）＋ 底面外接框的两个半边长。
    ///
    /// 两步，就是一张很朴素的斜二测：
    ///   ① **压扁**：纵向乘 `ry`（底面是"俯视"看的）；
    ///   ② **错切**：越远（`sin` 越负 = 越靠上）越往右挪 `PrismDepthSkew × ry`。
    ///
    /// **只有这一份实现**：画布与图标共用（见 <see cref="PrismBaseOffsetDegrees"/> 那段）。
    /// </summary>
    public static System.Numerics.Vector2 PrismBasePointOffset(float cosT, float sinT, float rx, float ry)
    {
        PrismFrame(rx, ry, out float rxUse, out float skew);
        return new System.Numerics.Vector2(rxUse * cosT - skew * sinT, ry * sinT);
    }

    /// <summary>
    /// **底面上第 k 个侧面看得见吗**（决定那一条底边和两条竖棱画实线还是虚线）。
    ///
    /// 判据是几何事实："**侧面的外法向朝向观察者**"。底面（半径归一成 1 的圆）上，
    /// 弧段中点在 `(midCos, midSin)` 的那个侧面，外法向就是 `(midCos, midSin)` 方向；
    /// 而**观察者在底面坐标里不是正的**——它是 `(错切比, 1)` 方向：
    ///   ① `1` 是"朝观察者"（底面是俯视压扁的，`sin > 0` 那一半离观察者近）；
    ///   ② 那个 `错切比` 是**斜二测带来的水平偏移**，不能漏——正是它让"斜二测的四棱柱"
    ///      **右侧**那个面看得见（和课本上的长方体一模一样：前面 ＋ 右面可见、
    ///      后面 ＋ 左面虚掉，虚掉的三条棱正好是"背面下横 / 左下斜 / 背面左竖"）。
    /// 漏掉它的话，四棱柱会退化成"只有正面看得见"，看着像个空壳子。
    ///
    /// 画布（`Stroke.PrismFamilyEdges`）和图标（`IconAtlas.DrawPrism`）**都调它**。
    /// </summary>
    public static bool PrismFaceVisible(float midCos, float midSin, float rx, float ry)
    {
        PrismFrame(rx, ry, out float rxUse, out float skew);
        return midCos * (skew / rxUse) + midSin > 0f;
    }

    /// <summary>
    /// **棱台的上底缩到多大**（相对下底的比例，按半宽 / 半高一起缩——同形）。
    ///
    /// 用户 2026-09-20 定的口径："上底按**固定比例**缩，**两笔**画完"——
    /// 意思是上底大小**不靠第三笔拖**，画的时候只定它**在哪儿**（第 2 笔那个中心）。
    /// 好处是棱台和棱柱**手感一模一样**（都是两笔），代价是上底大小不能单独调。
    ///
    /// 0.5 是照课本上正四棱台的常见画法取的（上底看起来约是下底的一半）。
    /// 想让上底更大 / 更小，只改这一个数——**画布和图标都读它**（同一份实现）。
    /// </summary>
    public const float FrustumTopScale = 0.5f;

    /// <summary>
    /// 这个工具**是不是"底面有几边形"那一族**（棱柱 / 棱锥 / 棱台）：那一格都
    /// "再点一次换一档 3→4→5→6→3"，但**三格各记各的档**（见 <see cref="UiState.SidesOf"/>）。
    ///
    /// **界面上凡是"按这一族分支"的地方都该问它**（现在有两处：那一格画几个档位点、
    /// 再点一次要不要换档）——各写一份名单的话，加一种立体图形就会漏掉一处
    /// （这次加棱锥 / 棱台时正是一次要改三处：这里、图标的档位名、引擎的 PlanOf）。
    /// </summary>
    public static bool HasSideCount(Tool tool)
        => tool is Tool.Prism or Tool.Pyramid or Tool.Frustum;

    /// <summary>
    /// **「椭圆（带焦点）」那一格的默认顶点位置**：椭圆左上方那个点
    /// （参数角 **−120°**，也就是 `P = O + (−a/2, −√3·b/2)`；局部坐标里 **+y 朝下**，
    /// 所以"负的 sin"才是**上面**）。
    ///
    /// 它是"焦点三角形第三个顶点 P 还没被拖过"时摆的地方，所以**画布和图标读的是同一个数**
    /// （图标层看不到 `Stroke`，那里是引擎内部类型——这一条和 <see cref="SolidEllipseRatio"/>
    /// 同一个理由，只能放在这个公开的规则层里）。
    ///
    /// 为什么是"左上方"而不是"正上方"（看起来最上面）：
    ///   · 正上方 `(0, −b)` 正好是椭圆**上端点手柄**待的地方，P 压上去会把那个手柄**抢走**
    ///     （命中倒着找，后画的 P 先中）——"想拉长半轴，结果拖走的是 P"；
    ///   · 左上这个角离上端点 / 右端点两个手柄都够远，而且**两种朝向的椭圆都适用**
    ///     （横椭圆竖椭圆都是"左上方"，不用按 a / b 谁大分两支）；
    ///   · 它的 x 分量 ≠ 0，所以**永远不和两个焦点共线**（三角形不会退化成一条线段）。
    /// </summary>
    public const float FocusPointDefaultU = -2f * MathF.PI / 3f;     // −120°（左上方）

    /// <summary>
    /// 立体图形里椭圆的**扁率**（短半轴 / 长半轴）：照 InkClass 的 `2.646`。
    ///
    /// 它是"从上往下看"的那个**俯角**——所以一个画面里所有圆都该用同一个：
    /// 圆柱 / 圆锥 / 圆台的底面椭圆、**球的赤道**、以及**图标上那几个椭圆**。
    ///
    /// **为什么放在这里**：图标层（InkUi）看不到 `Stroke`（引擎内部类型），
    /// 而图标上那个椭圆必须和画布上用**同一个俯角**，否则图标比画出来的"躺得更平"。
    /// 所以它是"引擎和界面都要用的那一条规则"，按这个类开头的规矩放这儿、**只有一份**；
    /// `Stroke.SolidEllipseRatio` 只是转发给它（老代码仍然读得到那个名字）。
    /// </summary>
    public const float SolidEllipseRatio = 1f / 2.646f;
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

    /// <summary>
    /// **正在 PPT 放映中**（`InkEngine.PptMode` 的转发）。
    ///
    /// 界面用它做一件事（用户 2026-09-27 定）：**进放映的那一刻**把悬浮条展开、
    /// 并摆回"工作区底边居中"——老师一放片，笔就自己出来了。
    /// ⚠ 只在**边沿**做，不能每次状态变化都摆（否则他拖走的面板会被翻页之类的
    /// 无关状态变化拽回去）。
    /// </summary>
    public bool PptMode { get; init; }
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
    /// <summary>
    /// **笔的线型**（实线 / 虚线 / 点线）。界面色带条上那个"虚实线切换"按它显示当前档。
    ///
    /// 注意它是"**下一笔**用哪种"，不是"板子上那些笔迹现在是什么"——
    /// 那些各存各的，选中之后在操作条的线型面板里改（那边读的是
    /// <c>SelectionHandles.DashOfSelection</c>）。
    /// </summary>
    public StrokeDash Dash { get; init; }
    /// <summary>
    /// **抛物线工具当前的开口方向**（用户 2026-09-20 定：画**之前**定好，不在选中框里改）。
    /// 界面用它把图形面板那一格的图标**转成这个朝向**——不转的话，老师看不出
    /// 下一笔会朝哪开（那格点第二下的作用就是换它）。
    /// </summary>
    public CurveAxis ParabolaAxis { get; init; }
    /// <summary>
    /// **直线工具当前的线型**（用户 2026-09-20 定：图形面板里"直线"那一段
    /// **再点一次换一档**：实线 → 虚线 → 点线 → 实线，这样不用为虚线直线单开格子）。
    /// 界面用它把那一格的图标换成当前档——不换的话，老师看不出"点第二下到底有没有生效"。
    ///
    /// 和 <see cref="Dash"/>（笔的线型）是**两件事**：那一个是笔的色带条管着的，
    /// 这一个只管直线那一格。两个都只作用于"下一笔画出来的"。
    /// </summary>
    public StrokeDash LineDash { get; init; }
    /// <summary>
    /// **立体那一格当前的档**：底面几边形（3~6）。用户 2026-09-20 定：
    /// "我想想能不能做成像直线切换那样切换三四五六"——界面拿它把那一格的图标
    /// 换成三/四/五/六棱柱，并在右边点出**4 个档位点**（和直线那格同一套）。
    ///
    /// ⚠ **棱柱 / 棱锥 / 棱台各一个数，不是一个共享的数**：三格各有各的档位点，
    /// 点谁都只动它自己（用户上手就发现"切一个另外两个也动"是 bug）。
    /// 要哪一个问 <see cref="SidesOf"/>——别自己在界面那边按工具写 switch。
    /// </summary>
    public int PrismSides { get; init; }
    /// <summary>棱锥那一格当前的档（含义同 <see cref="PrismSides"/>，只是各记各的）。</summary>
    public int PyramidSides { get; init; }
    /// <summary>棱台那一格当前的档（含义同上）。</summary>
    public int FrustumSides { get; init; }

    /// <summary>
    /// 某个立体工具当前那一档（界面画档位点、选图标都用它）。
    /// **判据只有这一处**：三格的名字散在界面各处就又会"加一种立体漏一处"。
    /// </summary>
    public int SidesOf(Tool tool) => tool switch
    {
        Tool.Pyramid => PyramidSides,
        Tool.Frustum => FrustumSides,
        _ => PrismSides,
    };

    /// <summary>
    /// 立体档位的**上下限**（3 / 6）。界面要知道"这一格一共几档"才能画档位点，
    /// 而 `Stroke` 是引擎内部类型、InkUi 看不到——所以把这两个数**随状态一起推上去**
    ///（不在界面那边写死一份 3/6：档位范围以后要改成 3~8 的话，那种写法必漏一处）。
    /// </summary>
    public int SolidMinSides { get; init; }
    public int SolidMaxSides { get; init; }
    /// <summary>
    /// **双曲线那一格当前的档**：画不画那两条虚线渐近线（2026-09-22 加，见
    /// <c>Engine.HyperbolaAsymptotes</c>）。界面拿它把那一格的图标在"有渐近线 / 无渐近线"
    /// 两张之间换，并在右边点出**2 个档位点**。
    ///
    /// 注意它是"**下一笔**画不画"，不是"板上那些双曲线现在有没有"——那些各存各的
    /// （见 <c>Stroke.ShowAsymptotes</c>）。和 <see cref="LineDash"/> 是同一条口径。
    /// </summary>
    public bool HyperbolaAsymptotes { get; init; }
    /// <summary>
    /// **椭圆（带焦点）那一格当前的档**：画不画焦点三角形（2026-09-22 加，见
    /// <c>Engine.EllipseFocusTriangle</c>）。两档**都画两个焦点**，差别只在连不连那两条边。
    /// 界面拿它换图标 + 点 2 个档位点。同 <see cref="HyperbolaAsymptotes"/>：只管"下一笔"。
    /// </summary>
    public bool EllipseFocusTriangle { get; init; }
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
    /// <summary>
    /// **新画的坐标系要不要网格**（界面用它显示抽屉里那一行"坐标系网格：开/关"）。
    /// 注意它是"默认值"，不是"板子上那些坐标系现在有没有格"——那些各存各的。
    /// </summary>
    public bool CoordGridDefault { get; init; }
    /// <summary>**停顿成型**开着吗（界面用它显示抽屉里那一行的开关）。
    /// 默认开；关掉只是"以后画的那些不参与"，不影响已经变出来的图形。</summary>
    public bool DwellShapeOn { get; init; }
    /// <summary>
    /// **压感粗细**开着吗（界面用它显示「设置 → 书写 → 压感粗细」那一行的开关）。
    /// 默认开；关掉 = 整块板等宽（渲染期语义，文档里的压力数据不动）。
    /// </summary>
    public bool PressureOn { get; init; }

    /// <summary>「精细笔迹」（原始输入补点）总开关的状态（默认开）。</summary>
    public bool FineStrokeOn { get; init; }
    /// <summary>
    /// **触摸手势总开关**开着吗（界面用它显示「设置 → 书写 → 触摸手势」那一行的开关）。
    /// 默认开；关掉 = 只剩单指书写（双指 / 三指 / 长按 / 漫游全部停用）。
    /// </summary>
    public bool TouchGesturesOn { get; init; }

    // [删除 2026-10-05] `PredictOn`（墨迹预测开关的状态）：随老预测系统移除。
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

    /// <summary>
    /// **自动更新的状态**（2026-09-29 加）。「更多」抽屉里的「检查更新」那一行：
    /// 显示什么文字、点一下做什么，全部看它 + <see cref="UpdateText"/>。
    /// 状态由引擎在后台线程算好、在主线程推进（界面永远不会卡在网络上）。
    /// </summary>
    public UpdateStage UpdateStage { get; init; }
    /// <summary>自动更新的一行状态文字（"未配置更新源" / "检查中…" / "已是最新" / "下载 42%" …）。</summary>
    public string UpdateText { get; init; }

    /// <summary>
    /// 「墨迹」页的状态行（上次保存/打开的结果；没做过事就是空串）。
    /// 保存成功 / 打开成功（含是否备份）/ 各种失败都写在这儿——产品里不弹窗。
    /// </summary>
    public string InkStatus { get; init; }

    /// <summary>回放中吗（界面用它把「墨迹回放」那一行显示成"停"）。</summary>
    public bool ReplayActive { get; init; }
    /// <summary>回放正在播吗（暂停时为 false；界面据此显示 ▶/⏸）。</summary>
    public bool ReplayPlaying { get; init; }
    /// <summary>回放倍速（0.5 / 1 / 2 / 4）。</summary>
    public float ReplaySpeed { get; init; }

    /// <summary>计时器在跑/暂停中；停下后为 false。</summary>
    public bool TimerActive { get; init; }
    /// <summary>计时器暂停中（界面据此显示"继续"）。倒计时到点后为 false（继续显示超时）。</summary>
    public bool TimerPaused { get; init; }
    /// <summary>倒计时跑到 0 了（继续正计时显示超时 `+00:27`）。</summary>
    public bool TimerFinished { get; init; }
    /// <summary>计时器模式。</summary>
    public TimerMode TimerMode { get; init; }
    /// <summary>计时器当前值（毫秒）：倒计时 = 剩余，正计时/秒表 = 已过。</summary>
    public float TimerValueMs { get; init; }
    /// <summary>计时卡片开着吗（设置态或运行态）。</summary>
    public bool TimerCardOpen { get; init; }
    /// <summary>卡片在设置态吗（false = 运行态）。</summary>
    public bool TimerSettingsOpen { get; init; }
    /// <summary>大字（双击放大的）形态开着吗。</summary>
    public bool TimerExpanded { get; init; }
    /// <summary>点名卡片开着吗；<see cref="RollSettingsOpen"/> = 设置态。</summary>
    public bool RollCardOpen { get; init; }
    public bool RollSettingsOpen { get; init; }

    /// <summary>
    /// 点名名单（`%APPDATA%\InkTeach\Names.txt`，一行一个；空数组 = 没名单、用学号）。
    /// 每次装载给整份快照——点名全在界面层做，引擎只负责读盘与推送。
    /// </summary>
    public string[] Names { get; init; }
}

/// <summary>
/// 给界面的指针事件。坐标是**逻辑屏幕坐标**（物理坐标 ÷ DPI 缩放），
/// 和 <see cref="IUiHost.Screen"/>、<see cref="IOverlayUi.Layout"/> 同一套坐标系，
/// 界面不用关心 DPI。
/// </summary>
public readonly struct UiPointerEvent
{
    public UiPointerEvent(float x, float y, float pressure, bool fromPen, bool isEraserTip,
                          bool fromTouch = false, uint pointerId = 0)
    {
        X = x; Y = y; Pressure = pressure; FromPen = fromPen; IsEraserTip = isEraserTip;
        FromTouch = fromTouch;
        PointerId = pointerId;
    }

    public float X { get; }
    public float Y { get; }
    public float Pressure { get; }
    public bool FromPen { get; }
    public bool IsEraserTip { get; }

    /// <summary>
    /// 这一下是**手指**（PT_TOUCH）。给"触摸长按出提示"用（2026-10-02）——
    /// 界面据此区分"鼠标按住"（= 拖动）和"手指按住"（= 长按候选）。
    /// 笔接触不算触摸（笔有自己的 <see cref="FromPen"/>；长按候选 = FromTouch || FromPen）。
    /// </summary>
    public bool FromTouch { get; }

    /// <summary>
    /// 系统给的指针 id（同一根手指/鼠标在整个"按下 → 移动 → 抬起"里不变）。
    /// 触摸长按期间用它挡掉**别的指针**的移动：引擎会把窗口收到的所有移动都转给界面，
    /// 停着的鼠标随便动一下就会被当成"手指滑走了"（2026-10-02 自检实测：长按被搅黄）。
    /// </summary>
    public uint PointerId { get; }
}

/// <summary>
/// 浮层主题里的一层投影：**往外胀多少**（四边同时胀，负值＝往里缩）、**往下挪多少**、
/// 什么颜色。和界面那边 `Tokens.ShadowLayer` 是同一个东西——那个类型在 `InkUi` 里，
/// 引擎看不见它，所以这里再声明一个同样形状的，由界面在推主题时搬过来。
/// </summary>
public readonly record struct UiShadowLayer(float Inflate, float Dy, Color4 Color);

/// <summary>
/// 引擎给界面/浮层用的配色。界面应当只用这些颜色，保证多套界面观感一致。
/// 产品的两套（浅色/深色）由界面推上来见 <see cref="IUiHost.SetFloatingTheme"/>。
/// </summary>
public readonly struct UiTheme
{
    public UiTheme(Color4 panel, Color4 panelBorder, Color4 text, Color4 textMuted,
                   Color4 hover, Color4 activeBg, Color4 activeText, float cornerRadius,
                   Color4 edgeBottom = default, UiShadowLayer[] shadow = null)
    {
        Panel = panel; PanelBorder = panelBorder; Text = text; TextMuted = textMuted;
        Hover = hover; ActiveBg = activeBg; ActiveText = activeText; CornerRadius = cornerRadius;
        EdgeBottom = edgeBottom;
        Shadow = shadow ?? Array.Empty<UiShadowLayer>();
    }

    public Color4 Panel { get; }
    public Color4 PanelBorder { get; }
    public Color4 Text { get; }
    public Color4 TextMuted { get; }
    public Color4 Hover { get; }
    public Color4 ActiveBg { get; }
    public Color4 ActiveText { get; }
    public float CornerRadius { get; }

    /// <summary>底沿那道 1 像素内阴影（"卷边"）。**默认全透明 = 不画**（深色主题就不画）。</summary>
    public Color4 EdgeBottom { get; }

    /// <summary>投影层，逐层往外胀；**空数组 = 不画投影**。</summary>
    public UiShadowLayer[] Shadow { get; }

    /// <summary>
    /// 投影**最远能盖到形状外面多少**（逻辑像素）＝ 各层"胀幅 ＋ 下挪"的最大值。
    ///
    /// 脏区必须按这个往外放：浮层一移动（拖选区、开合面板），旧投影要能被擦掉。
    /// 不放大就是"浮层旁边留一条擦不掉的印子"——和工具条那次（`IOverlayUi.PaintMargin`）
    /// 是同一个坑，只是那边是引擎裁掉了投影、这边是脏区没盖住。
    /// </summary>
    public float ShadowReachLogical
    {
        get
        {
            float m = 0f;
            foreach (var s in Shadow) m = MathF.Max(m, s.Inflate + s.Dy);
            return MathF.Max(0f, m);
        }
    }

    /// <summary>
    /// 默认主题：**没有界面挂上来时的兜底**（自检宿主、无界面模式用）。
    ///
    /// 注意它**不是**产品的浅色主题——产品的浅色/深色两套在 `InkUi.Tokens` 里，
    /// 界面 `Attach` 时会用 <see cref="IUiHost.SetFloatingTheme"/> 推上来。
    /// 数字保持原样（白 94% ＋ 蓝灰描边 ＋ 两层很淡的投影），这样老出图看起来没变。
    /// </summary>
    public static UiTheme Default => new(
        panel: new Color4(0.98f, 0.98f, 0.99f, 0.94f),
        panelBorder: new Color4(0.75f, 0.78f, 0.84f, 0.9f),
        text: new Color4(0.16f, 0.18f, 0.22f, 1f),
        textMuted: new Color4(0.45f, 0.48f, 0.54f, 1f),
        hover: new Color4(0.90f, 0.93f, 0.98f, 1f),
        activeBg: new Color4(0.13f, 0.45f, 0.85f, 1f),
        activeText: new Color4(1f, 1f, 1f, 1f),
        cornerRadius: 10f,
        shadow: new[]
        {
            new UiShadowLayer(0f, 1.5f, new Color4(0f, 0f, 0f, 0.045f)),
            new UiShadowLayer(0f, 3.0f, new Color4(0f, 0f, 0f, 0.090f)),
        });
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
