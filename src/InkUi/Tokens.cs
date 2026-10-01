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

    /// <summary>
    /// 收起态球里那圈笔色：**半径 = 球的半径 × 这个比例**，线宽 2.5。
    /// 和 <see cref="BallIcon"/> 是一对，两个数必须一起看（几何见 BallIcon 那段）。
    /// </summary>
    public const float BallRing = 0.76f;

    /// <summary>
    /// 收起态球里那个笔图标的**边长** = 球的半径 × 这个比例。
    ///
    /// **这一对数的几何**（球半径 R = 24）：
    ///   · 色圈：半径 0.76R = 18.24，线宽 2.5 → **内沿落在 16.99**
    ///   · 图标：边长 0.85R = 20.4 → 半边长 10.2 → 离内沿还剩 6.8 的空
    /// 想再放大，天花板是"正方形的对角线塞进内圆"：边长 ≈ 2 × 16.99 ÷ √2 ≈ **24**，
    /// 也就是 **1.0R 是上限**，再大四个角就压到圈上了。
    ///
    /// **为什么会有这一段注释**：产品里原来是 1.4R（边长 33.6），半边长 16.8——**正好顶到
    /// 内沿 16.99**，用户 2026-09-18 报的"收起来的小圆球的笔碰到色圈了"就是这个。
    /// 假面板一直是 0.70R，产品抄的时候把 0.7 写成了 1.4（当年那个数还是按"面板一半宽"
    /// 算的，那个 bug 见 \调研-界面设计与框架选型.md 3.4.1）。
    ///
    /// 定到 0.85 是**按眼睛挑的**：0.70（假面板原数）在圈里偏空、1.00 有点顶，
    /// 0.85 一眼就看得出是支笔、圈边也还留着呼吸。四档对照见
    /// [reports/收起球-笔图标比例-四档.png](../reports/收起球-笔图标比例-四档.png)。
    /// </summary>
    public const float BallIcon = 0.85f;

    /// <summary>
    /// 默认位置**离工作区底边**留多少（逻辑像素）。
    ///
    /// 沿革：12（"浮着"的观感）→ **4**（用户 2026-09-27："紧贴任务栏上方一点点、
    /// 两者不重叠"）。同一句里他要的两件事决定了两个词：**工作区**（不是屏幕，
    /// 这样才不压任务栏）＋ **小间隔**（4，这样看着就是"靠最底部"）。
    ///
    /// ⚠ 它是"默认位置"的间距，**不是**贴边时的间隙（那是 <see cref="DockGap"/>）。
    /// 拖过之后面板按 <see cref="DockGap"/> 夹在屏幕内，跟这个数无关。
    /// </summary>
    public const float EdgeMargin = 4f;

    /// <summary>贴边之后几乎贴着屏幕（不是 12——12 是"浮着"的留白）。</summary>
    public const float DockGap = 2f;

    /// <summary>
    /// 贴边隐藏时露出来的头：8 像素。8 看得见、点得到、悬停能唤出；
    /// 再细就得靠"蹭屏幕边缘"了，教室里不友好（Windows 任务栏留的是 2）。
    /// </summary>
    public const float DockPeek = 8f;

    /// <summary>
    /// **收起态**贴边隐藏之后，露出来的那条"把手"有多长（逻辑像素）。
    ///
    /// 为什么要有它：露出来的原来是**球自己被屏幕切掉的那一小片弧**——一条上宽下窄的
    /// 曲边，看着像残影（用户 2026-09-18 原话："收起的时候我会看见一个残影，似乎是色条"）。
    /// 现在收下去之后不再露球的弧，改成一条**直的药丸把手**（颜色 = 当前笔色，和色线同一条）：
    /// **长 = 球的直径 48**，厚 = 露头那 8 像素（和展开态露出来的那条一样厚）。
    ///
    /// 试过**半径 24**（用户"看那个好"），**否掉**：仓库自己那条规矩是
    /// "48 是点得到的最小东西，触摸屏上也够"（见 <see cref="Ball"/>），24 连这条底线都不到，
    /// 而隐藏态恰恰是最需要一次点中的时候——教室里没键盘，工具找不到是真问题。
    /// </summary>
    public const float PeekTab = 48f;

    /// <summary>
    /// **离屏幕底边多近才算"贴底"、才允许贴边隐藏**（逻辑像素）。
    ///
    /// 沿革：
    ///   · 原来叫 `SnapDistance`（40）：拖动松手时离**任意一条边** 40 以内就自动吸过去；
    ///   · 2026-09-30 上午：取消拖动吸附（拖到哪停哪），它只剩"隐藏触发距离"一个用途；
    ///   · 2026-09-30 下午（**当前**）：用户点名改成"**到屏幕底边 ≤ 10**，其他不变"——
    ///     判定基准从**工作区底**（任务栏上沿）换成**屏幕底**，数字 40 → **10**。
    ///
    /// 这条改完的实际含义：**默认位置（工作区底 − 4）不再触发隐藏**——
    /// 本机任务栏 48 高，默认位置离屏幕底 52，52 > 10；要收起来得把面板**拖到屏幕最底下**
    /// （压过任务栏那一段）附近。用户要的就是这个"要藏必须明确拖到底"的手感。
    /// </summary>
    public const float DockHideDistance = 10f;

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

    /// <summary>
    /// 平时那条**色线**：6 像素。数字照抄假面板（`BandIdle = 6 / BandOpen = 34`）。
    /// 它不只是装饰——12 段色片压成一条线以后**仍然可以直接点**，
    /// 不用先"展开再选"（用户明确说喜欢这个）。
    /// </summary>
    public const float BandLine = 6f;

    /// <summary>色线长成设置条的时长（和悬停展开同一套时长，观感才是一路的）。</summary>
    public const double RailMs = 167;

    /// <summary>鼠标离色线多近就算"碰到了"（上下各让一点，不用精确压在 6 像素上）。</summary>
    public const float RailHoverPad = 10f;

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

    /// <summary>
    /// 「更多」面板（屏幕中央那块）的开合时长。打开 167 = 和悬停展开同一套节奏；
    /// 关闭 120 = 用户已经按了关闭，快一点。
    /// </summary>
    public const double MoreOpenMs = 167;
    public const double MoreCloseMs = 120;

    // ---- 颜色 ---------------------------------------------------------------

    /// <summary>
    /// 面板底。2026-09-18 改过：起因是"深色有高级感、白色没有"。
    ///
    /// 老值是"白 80% 透光"。我们没有毛玻璃（明确不做），所以这个值有两个毛病：
    ///   ① 面板的颜色其实由**背后的东西**决定——白底 PPT 上发灰、彩色底上发脏。
    ///      深色面板后面本来就是暗的、明度差天然够，所以同样的问题在深色上看不出来；
    ///   ② 面板和亮背景的明度几乎一样，只能靠 1 像素描边定形，看着就是"贴上去的"。
    /// 现在改成**近实心、带一点冷调的近白**（留 6% 透明是为了还知道"后面有东西"）。
    /// </summary>
    public static readonly Color4 PanelLight = new(0.984f, 0.984f, 0.992f, 0.94f);
    public static readonly Color4 PanelDark = new(0.125f, 0.125f, 0.125f, 0.80f);

    /// <summary>
    /// 1px 描边。**它比透明度更影响"立不立得住"**：没有这道边，圆角会糊进背景里。
    /// 浅色带冷调：纯黑描边会发脏，也容易和暖色的课件底子打架。
    /// </summary>
    public static readonly Color4 BorderLight = new(0.09f, 0.10f, 0.13f, 0.14f);
    public static readonly Color4 BorderDark = new(1f, 1f, 1f, 0.12f);

    /// <summary>图标：#1B1B1F。白 80% 底上压到彩色背景，最差也守得住 3:1。</summary>
    public static readonly Color4 InkLight = new(0.106f, 0.106f, 0.122f, 1f);
    public static readonly Color4 InkDark = new(0.949f, 0.949f, 0.949f, 1f);

    /// <summary>
    /// 次要墨色：**浮层**里那些"看得见但不用读"的东西（粗细滑条的底轨、没选中的刻度点）。
    /// </summary>
    public static readonly Color4 InkMutedLight = new(0.45f, 0.48f, 0.54f, 1f);
    public static readonly Color4 InkMutedDark = new(0.62f, 0.65f, 0.70f, 1f);

    /// <summary>
    /// **浮层**（选中操作条、颜色/层级/导出小面板）的圆角。10 是它们一直以来的值：
    /// 比面板的 12 略小——它们是小一号的临时浮层，圆角跟着小一点才不显胖。
    /// </summary>
    public const float FloatingCorner = 10f;

    /// <summary>当前工具：不透明强调色底 + 白图标（微软规范：毛玻璃上别放强调色文字）。</summary>
    public static readonly Color4 Accent = new(0f, 0.404f, 0.753f, 1f);
    public static readonly Color4 AccentInk = new(1f, 1f, 1f, 1f);

    /// <summary>悬停底：黑 7%（系统 SubtleFill 的量级）。</summary>
    public static readonly Color4 HoverLight = new(0f, 0f, 0f, 0.07f);
    public static readonly Color4 HoverDark = new(1f, 1f, 1f, 0.08f);

    /// <summary>
    /// 「更多」面板背后的遮罩。浅色 18%：够读出"后面被压住了"，又不把板书糊成一片；
    /// 深色 42%：深色主题背景本来就暗，轻了读不出"这是模态"。
    /// 它同时是"点面板外 = 关闭"的那块地（见 FullUi 的「更多」面板一节）。
    /// </summary>
    public static readonly Color4 ScrimLight = new(0f, 0f, 0f, 0.18f);
    public static readonly Color4 ScrimDark = new(0f, 0f, 0f, 0.42f);

    /// <summary>
    /// 「更多」面板的圆角：18——主条是胶囊（24）、上带是 12，
    /// 屏幕中央这块大卡片取中间偏大，既不"糊成一块"也不像药丸。
    /// </summary>
    public const float MoreRadius = 18f;

    /// <summary>
    /// 色带那条**凹槽**：色片躺在里面才像"装在面板上"，直接贴在白底上会显得浮。
    ///
    /// 2026-09-18 浅色从"黑 10%"降到"黑 6%"。面板底改成近实心以后，10% 的凹槽在白面板上
    /// 是一条**明显偏灰的横带**（实测 224 对主条的 248，差 24 个色阶），比色片本身还抢眼；
    /// 降下来之后靠 `WellEdgeLight` 那道边把"凹槽"的形留住 —— **浅填充 ＋ 一道硬边**，
    /// 而不是**深填充**。深色不动：深色本来就是靠"提亮表面"表示凹进去的。
    /// </summary>
    public static readonly Color4 TroughLight = new(0f, 0f, 0f, 0.06f);
    public static readonly Color4 TroughDark = new(1f, 1f, 1f, 0.12f);

    /// <summary>
    /// 凹槽**朝主条那一侧**的 1 像素暗线（只有浅色用）。
    /// 它是"这道槽是刻进面板的"唯一的凭据——没有它，降下来的凹槽就只是一条淡灰带
    /// （Windows 的口径：浅色靠描边定形，深色靠提亮表面）。
    /// </summary>
    public static readonly Color4 WellEdgeLight = new(0.09f, 0.10f, 0.13f, 0.06f);

    /// <summary>滑条底轨（没拖的时候很淡）与描边。</summary>
    public static readonly Color4 TrackLight = new(0f, 0f, 0f, 0.07f);
    public static readonly Color4 TrackDark = new(1f, 1f, 1f, 0.08f);

    /// <summary>
    /// 面板**顶沿**那道 1 像素的内高光（照假面板：Windows 11 的层次感靠它）。
    /// **只有深色能用**——见下面 <see cref="EdgeLightBottom"/>。
    /// </summary>
    public static readonly Color4 TopSheenDark = new(1f, 1f, 1f, 0.08f);

    /// <summary>
    /// 浅色主题面板**底沿**那道 1 像素的内阴影（"卷边"）。
    ///
    /// 浅色的层次**不能照抄深色**：深色靠"顶部一道白高光"读接光，而白 55% 画在
    /// 白 94% 的面板上等于没画（老值 `TopSheenLight` 就是这样，一直在白画一次）。
    /// 浅色要的是**反过来**那一条：把底沿压暗一点，面板才像"一块有厚度的板"。
    /// </summary>
    public static readonly Color4 EdgeLightBottom = new(0.09f, 0.10f, 0.13f, 0.06f);

    /// <summary>
    /// 一层投影：**往外胀多少**（<paramref name="Inflate"/>，四边同时胀）、**往下挪多少**
    /// （<paramref name="Dy"/>）、什么颜色。
    /// </summary>
    public readonly record struct ShadowLayer(float Inflate, float Dy, Color4 Color);

    /// <summary>
    /// 投影。**关键在"胀"，不在"挪"** —— 老值是两块深浅不同的矩形往下挪 1px / 3px
    /// （没有模糊），屏幕上就是面板下沿一条 3 像素的灰边，看着像印刷套版没对准。
    ///
    /// 为什么"往外胀"能当模糊用（这是这个数组的全部原理）：
    ///   每一层都比上一层**再大一圈**，所以"离面板越远，能盖住它的层数越少"，
    ///   累计出来的暗度自然一层比一层淡 —— 这就是一条近似的衰减曲线。
    ///   反过来（层越大越靠外、却都一样深）会让阴影越往下越黑，那是错的。
    ///
    /// ⚠ 2026-10-01 用户报"菜单栏周围浮着一个半透明、带黑影的袋子"——根因是**层数太少**：
    ///   每层都是硬边填充，最外那层 3% 不透明度在浅色背景上就是一条看得清的圆角轮廓
    ///   （≈8 个色阶），看起来像一只"袋子"套在卡片外面。修法：5 层 → 8 层，
    ///   外层降到 ≤1%（≈2 个色阶，肉眼不可见）、相邻层差 ≤0.3%（半个色阶），
    ///   把台阶磨平；近卡处也顺势从 18% 收到 13%，本来就不该那么重。
    ///   真模糊（D2D GaussianBlur）代价见 `reports/性能-面板每帧代价.md`，仍然不加。
    ///   改这里的胀幅记得同步 `PaintMargin`（外层仍取 16＋8＝24，正好没超）。
    /// </summary>
    public static readonly ShadowLayer[] ShadowLight =
    {
        new(1.0f,  0.5f, new(0.09f, 0.10f, 0.13f, 0.0240f)),
        new(2.0f,  1.0f, new(0.09f, 0.10f, 0.13f, 0.0230f)),
        new(3.5f,  1.8f, new(0.09f, 0.10f, 0.13f, 0.0210f)),
        new(5.5f,  2.7f, new(0.09f, 0.10f, 0.13f, 0.0190f)),
        new(8.0f,  4.0f, new(0.09f, 0.10f, 0.13f, 0.0165f)),
        new(11.0f, 5.5f, new(0.09f, 0.10f, 0.13f, 0.0140f)),
        new(13.5f, 6.7f, new(0.09f, 0.10f, 0.13f, 0.0115f)),
        new(16.0f, 8.0f, new(0.09f, 0.10f, 0.13f, 0.0090f)),
    };

    /// <summary>
    /// 深色：**和白主题同一套曲线、颜色用纯黑**（深色的卡片大多是深底，黑投影在深色板上
    /// 本来就不显；真正会看到它的是"深色卡片浮在浅色白板/讲义上"的场合）。
    /// 老值是两层 10%/6%、只胀 3px——硬边小圈；2026-10-01 第一版修"袋子"时又矫枉过正
    /// 把胀幅拉到 16px，成了一个大光晕（用户当天就指出来"黑主题还有"）。这一版直接和
    /// 浅色逐层同参，只是颜色不同：外沿 0.9%（≈2 个色阶，看不出边）。
    /// </summary>
    public static readonly ShadowLayer[] ShadowDark =
    {
        new(1.0f,  0.5f, new(0f, 0f, 0f, 0.0240f)),
        new(2.0f,  1.0f, new(0f, 0f, 0f, 0.0230f)),
        new(3.5f,  1.8f, new(0f, 0f, 0f, 0.0210f)),
        new(5.5f,  2.7f, new(0f, 0f, 0f, 0.0190f)),
        new(8.0f,  4.0f, new(0f, 0f, 0f, 0.0165f)),
        new(11.0f, 5.5f, new(0f, 0f, 0f, 0.0140f)),
        new(13.5f, 6.7f, new(0f, 0f, 0f, 0.0115f)),
        new(16.0f, 8.0f, new(0f, 0f, 0f, 0.0090f)),
    };

    /// <summary>
    /// 界面**会画到占用矩形外面**的那一圈余量（逻辑像素）= 投影最远胀到多少。
    ///
    /// 引擎是按占用矩形（`IOverlayUi.QueryBounds`）裁剪界面绘制的，所以不给这一圈，
    /// 投影会被裁掉、屏幕上根本看不见（离屏出图看得见，那是另一条路）。
    /// 它**只影响裁剪与脏区**，不参与命中测试——所以不会出现"面板旁边点不动"。
    /// 改 `ShadowLight` 的胀幅记得同步改它。
    /// </summary>
    public const float PaintMargin = 24f;

    // ---- 调色板 -------------------------------------------------------------

    /// <summary>
    /// **笔的色片表**（上带那一排）。2026-09-27 用户重新定的版：
    /// "常使用的几种颜色以外，不常用的拿掉，然后增加一些包边的颜色"。
    ///
    ///   · **1~8 常用**：黑 / 红 / 蓝 / 绿 / 黄 / 橙 / 紫 / 白
    ///     —— 拿掉了灰、青、粉、棕（课堂用得少）；"白"留着，黑板 / 绿板上要写白字；
    ///   · **9~12 深色「包边」**：深蓝 / 墨绿 / 酒红 / 藏青
    ///     —— 比常用色更沉，压在浅色底上更稳，做正文或强调用。
    ///
    /// 两个刻意的取舍：
    ///   · 深色组没收"墨黑"（和 1 号黑重复）、也没收"深棕"（和刚拿掉的棕重复），
    ///     否则一排里会出现两个几乎一样的色；
    ///   · 顺序就是**点笔格切色的顺序**（见 `FullUi.CycleColor`）：常用的排前面，
    ///     连点几下就能在最常用的几支里转一圈，不用绕到深色那头。
    ///
    /// ⚠ 色片的高亮是**按颜色值比**的（`IsSwatchActive`，阈值 0.02），所以这张表里
    /// 的颜色值和引擎的默认色（`InkPalette.PenDefault` = 红）必须对得上——现在一致。
    /// </summary>
    public static readonly (string Name, Color4 Color)[] Palette = InkEngine.InkPalette.PenBand;

    /// <summary>
    /// **荧光笔的色片表**（和笔那张分开，2026-09-27 用户定的"独立一张亮色表"）。
    ///
    /// 为什么必须分开：荧光笔画出来是**半透明的宽色带**（`InkPalette.ToHighlighter`
    /// 把色相压到 32% 不透明），所以它只适合**亮色相**——以前和笔共用一张表，
    /// 能选到"黑 / 灰 / 棕"，画出来就是一道灰道道，什么也标不出来。
    /// 这张表只留了 5 个亮的，也就不用再纠结"哪个不适合高亮"。
    ///
    /// ⚠ 第一个「荧光黄」的值**和引擎默认色一字不差**（`InkPalette.HighlighterDefault`
    /// = (1, 0.85, 0.15)）：不这么对的话，启动时荧光笔那个色片不会亮
    ///（高亮是按颜色值比出来的，阈值 0.02，差一点就匹配不上）。
    /// 所以这张表**别单独改第一个色**，要改就两处一起改。
    /// </summary>
    public static readonly (string Name, Color4 Color)[] HighlighterPalette = InkEngine.InkPalette.HighlighterBand;
}
