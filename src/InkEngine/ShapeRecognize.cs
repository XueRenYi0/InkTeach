using System.Numerics;

namespace InkEngine;

/// <summary>
/// **一次识别的结果**。
///
/// `Def` 是**定义元素**——口径和"用图形工具拖出来的那几个点"**完全一样**
/// （见各 StrokeKind 的注释：直线是两个端点、圆是"圆心 + 圆周点"、
/// 椭圆是"中心 + 外角点"、矩形是两个对角点、三角形三个顶点、平行四边形三个顶点）。
/// 这样"把一笔变成图形"这件事就只是**把 Def 写进一场普通图形的 Points**，
/// 不需要为识别单开一条几何通道（见 <see cref="InkEngine"/> 的转换那一段）。
/// </summary>
internal struct ShapeGuess
{
    /// <summary>认出来的种类；<see cref="StrokeKind.Freehand"/> = **什么都没认出来**。</summary>
    public StrokeKind Kind;

    /// <summary>定义元素（长度 2 或 3，见类型注释）。没认出来时是空数组。</summary>
    public Vector2[] Def;

    /// <summary>
    /// **姿态角**（度，逆时针为正）：定义元素永远是"正着的"（轴对齐），
    /// 歪的那个量放在这里，由 <see cref="ShapeRecognize.ApplyRotation"/> 写进对象的变换。
    ///
    /// 为什么分成"定义元素 + 姿态角"两样，而不是直接把点摆到歪的位置上：
    /// 因为引擎里**旋转本来就是这么存的**（`Selection.RotateMatrix` 绕中心转，见
    /// <see cref="Stroke.Transform"/>），分成两样之后"识别出来的斜椭圆"和
    /// "画一个正椭圆再拖旋转柄"得到的对象**是同一个东西**——选中、拖半轴、读取数、
    /// 存档、撤销全都自动一致，不需要为识别单开一条路。
    /// </summary>
    public float RotationDeg;

    /// <summary>姿态角的**旋转中心**（定义元素的局部坐标）：椭圆是它的中心、矩形是外框中心。
    /// 单列出来是为了不给调用方留"猜中心在哪"的余地（矩形那两个定义元素是**对角点**，
    /// 中点才是中心；椭圆第一个点就已经是中心——两者不一样）。</summary>
    public Vector2 RotPivot;

    /// <summary>
    /// **曲线的朝向**（只有抛物线 / 双曲线用得上，别的图形忽略它）。
    ///
    /// 为什么单列一位：图形对象里朝向是**独立存**的（`Stroke.SetParabolaAxis` 写进
    /// `CurveAxis`，**不在定义元素里**）—— 识别出来了却传不过去，写回的对象就会退到
    /// 默认值（抛物线默认"开口向上"），于是"画一条开口向下的抛物线、变出来是向上的"。
    /// `--inktest` 的写回包围盒断言当场抓到这个：差 **53.7%**，正好差一个"抬高量"。
    /// </summary>
    public CurveAxis Axis;

    /// <summary>"像不像"，0.5~1：刚好压线过关时是 0.5，越接近 1 越标准。
    /// **它不是概率**，只是"离门槛还有多远"的线性读数，用来在几个候选之间仲裁。</summary>
    public float Score;

    /// <summary>命中的是**哪一条判据**（连残差数字一起）。只为自检报告与排查用：
    /// 出问题时要一眼看出"是被哪条规矩放过去/拦下来的"。</summary>
    public string Rule;

    public bool IsNothing => Kind == StrokeKind.Freehand;

    public static ShapeGuess None(string rule)
        => new ShapeGuess { Kind = StrokeKind.Freehand, Def = Array.Empty<Vector2>(), Score = 0f, Rule = rule };

    public static ShapeGuess Hit(StrokeKind kind, float score, string rule, params Vector2[] def)
        => new ShapeGuess { Kind = kind, Def = def, Score = score, Rule = rule };

    /// <summary>带**姿态角**的命中（斜椭圆 / 斜矩形走这条）。</summary>
    public static ShapeGuess HitRot(StrokeKind kind, float score, string rule,
                                    float rotDeg, Vector2 pivot, params Vector2[] def)
        => new ShapeGuess
        {
            Kind = kind, Def = def, Score = score, Rule = rule,
            RotationDeg = rotDeg, RotPivot = pivot,
        };

    /// <summary>带**曲线朝向**的命中（抛物线走这条；双曲线将来同理）。</summary>
    public static ShapeGuess HitAxis(StrokeKind kind, float score, string rule,
                                     CurveAxis axis, params Vector2[] def)
        => new ShapeGuess { Kind = kind, Def = def, Score = score, Rule = rule, Axis = axis };
}

/// <summary>
/// **手绘一笔 → 我们图形库里的哪一个图形**（纯几何，不含任何状态、不认识文档、不看时钟）。
///
/// 为什么要独立成一层（见 架构-分层与规则.md 五-1"算法和它的调用点分开写"）：
/// 识别是**慢路径**（停顿那一刻才算一次），调用时机、幽灵预览、撤销全在引擎那一侧；
/// 这里只回答一个问题："这串点最像什么"。于是它可以用合成数据**离线量准确率**，
/// 而不用真拿笔去画——`--inktest` 就是干这个的。
///
/// ⚠ **它是"拟合"**，而架构文档 §二 现在明令核心不许出现拟合/简化。两条不是一回事：
/// 当年禁的是"**让笔迹更好看**"（滤波、笔锋、贝塞尔），这里是"**把一笔换成另一种表达**"
/// （和图形工具同一族）。这条例外要写进那份文档，不能靠默记。
///
/// 支持的六种（判据都能从"一笔的轨迹"**唯一推出来**）：
///   直线 / 圆 / 椭圆（轴对齐）/ 三角形 / 矩形 / 平行四边形。
/// **明确不做**的，以及为什么：
///   · **箭头**：手画箭头绝大多数是"画到末端再原路返回、回程画另一边翅膀"——
///     回程和去程**重叠**（overtrace），角点检测在重叠段上不干净；
///     而且我们库里箭头主要长在数轴 / 坐标系上，那两个有专门工具。
///   · **抛物线 / 双曲线 / 正弦 / 余弦**：一笔推不出"定义元素"（正弦我们的模型是**一个周期**，
///     而老师手画往往两三个周期，变出来和画的不一样 = 比不变更糟）。
///   · **数轴 / 坐标系**：同样推不出来，而且它们本来就有专门工具。
///   · **立体图形**：要两笔（正面 + 深度），是"跨笔画推断意图"，误触率高（见
///     计划-底层性能与功能.md 7.4：这一条连 InkClass 也只是"多笔拼多边形"那一档）。
/// </summary>
internal static class ShapeRecognize
{
    // =====================================================================
    //  门槛常量。**每个数都要能说出出处**（写死一个"看着差不多"的数，
    //  下次出问题就没人知道该不该动它）。
    // =====================================================================

    /// <summary>最短参与长度（逻辑像素，**按"笔画总长"算**）。出处：InkClass
    /// `MW_SimulatePressure.cs` 的 `LineAssistMinLen = 40`——"短笔画（标点、部首）不参与"。
    ///
    /// ⚠ **2026-09-24 从 40 提到 56**（用户："图形太小不用出图，正常不会画很太小的图"）。
    /// 56 不是随手取的，是从另一条规则反推的：**"点一下不留墨"的容差是 8 逻辑像素**
    /// （`Engine.DwellTapSlopLogical`），而那条判据量的是**定义元素首末两点**的——
    /// 对圆来说就是**半径**（定义元素 = 圆心 ＋ 圆周点）。于是"能被认出来的圆"和
    /// "会被当成点一下的圆"这两条规则不许打架：2πr ≥ 56 ⇒ r ≥ 8.9 > 8 ✓。
    /// （提到 56 之前是够得着的：2πr ≥ 40 只要求 r ≥ 6.4，于是 r ∈ [6.4, 8) 的圆
    ///  会被识别出来、又被"点一下"那条抹掉——`--dwelltest` H4 当场实测过。）
    ///
    /// 代价：短于 ~56 逻辑像素（≈1.5cm）的笔画不再变图形，**原样留着手绘**。
    /// 这是**良性失败**（用户要的就是这个）：不动它，用户看得见自己画的东西。</summary>
    internal const float MinLength = 56f;

    /// <summary>直线的"直度"上限：**最大垂距 ÷ 弦长**。出处：InkClass 的
    /// `LineAssistMaxDevRatio = 0.12`（原话：写字中途停顿的弯笔画不触发）。
    ///
    /// ⚠ **分母是"弦长"（首末点直线距离），不是"笔画总长"**——这一条是量出来的：
    /// 拿总长当分母时，一个 260×170 的闭合矩形"最大垂距 ÷ 周长"只有 9.9%，
    /// 会被判成直线（`--inktest` 第一次跑就红在这条上，34/40 个矩形判成了直线）。
    /// 弦长当分母的话，闭合形状的弦长接近 0，比值直接爆掉——**这正是我们要的**。</summary>
    internal const float LineDevRatio = 0.12f;

    /// <summary>直线的第二道闸：**笔画总长 ÷ 弦长**（"绕了多远"）。
    ///
    /// 只有"最大垂距"这一条挡不住**末端带钩**的笔画：一根 250 px 的竖笔，
    /// 末尾拐出 25 px 的小钩（汉字里的竖弯钩就是它），最远那点到弦只有 9%，
    /// 直度判据会放它过去。而"绕路比"对它很敏感：竖弯钩 ≈ 1.25，
    /// 尺子画的线 ≈ 1.00，带 2% 弓的线 ≈ 1.001，四分之一圆弧 ≈ 1.11。
    /// 1.12 就是照着"放过弓着的线、拦住带钩的笔画和圆弧"量出来的。</summary>
    internal const float LineArcExcessRatio = 1.12f;

    /// <summary>圆 / 椭圆的**相对残差**上限（最大 |点到圆周的距离差| ÷ 半径）。
    /// 这个数是我们自己在 `--inktest` 上量出来的：见 README 的那张准确率表。
    /// 它同时决定"多弯的圈还算圆"——太松会把鹅卵石形状吸成正圆。</summary>
    internal const float CircleResidualRatio = 0.09f;
    internal const float EllipseResidualRatio = 0.10f;

    /// <summary>拟合类（圆 / 椭圆）要求**画够了多少角度**。手画的半圆弧也满足"残差小"
    /// （它只是整圆的一部分），但**变出来会多出大半圈**——用户画一半却得到一个整圆，
    /// 这是最不能接受的一类"自作主张"。
    ///
    /// 原来是 300°（差 60° 以内算画圆了），**2026-09-23 收到 280°**：用户上手反馈
    /// "椭圆有时候识别不了"——手画椭圆往往收口差得更多（一笔画下来差 90° 很常见）。
    /// 280° 仍然把 180° 的半圆弧和 90° 的圆弧挡在外面（语料里有这两条反例）。</summary>
    internal const float MinArcCoverageDeg = 260f;

    /// <summary>起点到终点的缺口 ÷ 周长 ≤ 这个值 = **围上了**（拟合类 / 折线类都要求）。
    /// 0.18 的出处：Excalidraw 的 `gapRatio` 用的就是这个思路（笔画的闭合度特征）。</summary>
    internal const float CloseGapRatio = 0.18f;

    /// <summary>折线类（三角形 / 矩形 / 平行四边形）放宽到这一档：手画多边形**经常收口不严**
    /// （三角形最后一条边差一截），这一档之下我们把缺口当成一条虚的闭合边。</summary>
    internal const float LooseCloseGapRatio = 0.35f;

    /// <summary>一条"边"够不够直：边内所有点离两端连线的最大垂距 ÷ 弦长。
    /// 它同时挡住两件事：一个是"手抖的圆被数成十几边形"，一个是"弧形的四边被当成四边形"。</summary>
    internal const float EdgeStraightRatio = 0.06f;

    /// <summary>角点的**转向角**下限（度）：0° = 笔直往前，90° = 直角转。
    ///
    /// 这个数是量出来的，三档之间隔着量级：
    ///   · 手抖（±0.8 px 摊到 16 px 的弦上）转不到 10°；
    ///   · 手画的圆（半径 ≥ 100 px）每一处只转约 15°；
    ///   · 手画的直角 ≈ 90°，连很钝的角也有 35° 以上。
    /// 28° 就落在"手抖/圆弧"和"真角"中间那条空档里。</summary>
    internal const float CornerTurnMinDeg = 28f;

    /// <summary>**验收**：折线类（三角形 / 矩形 / 平行四边形）变出来之后，**墨迹上每一个点
    /// 到理想轮廓的最大距离** ÷ 形状对角线，不许超过这个数。
    ///
    /// 为什么必须有这一条（用户 2026-09-23 上手反馈的"矩形识别形变太大，完全和墨迹对不上"）：
    /// 前面那些判据都是"**看几个角、看几个数**"——角点只要有一个找偏了，
    /// 判据照样能过（比如把矩形认成平行四边形），结果就是一个和墨迹**毫不相干**的形状。
    /// 这条不一样：它把**整条墨迹**拿去和"变出来那个形状的轮廓"对一遍，
    /// 差太远就**什么都不说**（宁可不变，也不能变出一个对不上的）。
    ///
    /// 5.5% 是量出来的：手画的矩形（带圆角、带手抖）最大偏差约 2~3%，
    /// 而"角点找偏一格"那种错认通常在 15% 以上——中间留着很宽的空档，好调。
    /// ⚠ 只查"墨迹 → 轮廓"这一个方向：反过来查（轮廓 → 墨迹）会把
    /// "用户没画完的那一段"也算成错误，而**补全收口正是这个功能的本来目的**。</summary>
    internal const float FitTolRatio = 0.055f;

    /// <summary>验收容差的**绝对下限**（逻辑像素）：小图形（几十像素）里，
    /// 手抖和采样密度占的比例大，纯按比例算会把它们全判死。</summary>
    internal const float FitTolFloorLogical = 4f;

    /// <summary>**姿态角的收口门槛**（度）：歪得比它小就当"正着画的"，不做旋转。
    ///
    /// ⚠ **2026-09-23 从 12° 收到 4°**（用户上手反馈"矩形识别形变太大，完全和墨迹对不上"）：
    /// "收口成正的"这件事**本身就是一次形变**——一个歪 7° 的 260×170 矩形被掰正之后，
    /// 四个角要顶到框外约 18 px（≈ 对角线的 6%，`--inktest` 里"矩形·圆角/过冲/波浪边"
    /// 三族就是被这个判据挡下来的）。用户的优先级很清楚：**先要"像我画的那个"**，
    /// 想要正的他自己拖一下旋转柄（或者画的时候歪小于 4°）。
    /// 4° 和"画直线时那个吸附容差"（`Engine.DwellSnapDeg`）是同一个数：都表示
    /// "小到这个程度，说明他本来就想画正的"。</summary>
    internal const float TiltSnapDeg = 4f;

    /// <summary>
    /// 圆和椭圆的仲裁：轴比（长轴÷短轴）**大于**这个数才判椭圆，否则判圆。
    ///
    /// ★ **2026-09-26 从 1.12 改成 1.20 —— 这一条是量出来的，不是拍的**
    /// （用户报："圆老是识别不出来，是因为椭圆抢它的吗？我们这里是不是要定一个分界线，
    ///   接近于某一个分界线就是圆，扁的就是椭圆"——判据本来就是这个，问题在**线划在哪**）。
    ///
    /// 原来的 1.12 是对着**理想圆**的语料定的（`RecoProbe` 那条 `MakeCircle` 径向只抖
    /// **1.5%** → 拟合出来的轴比永远 ≈1.00 → 必然 `<1.12` → 必然判圆）。
    /// 所以它**恒过**，而**真手画**的圆是被压出来的：径向有一个二倍角分量
    /// （`r(θ)=r₀(1+ε·cos2θ)` 的轴比 ≈ `1+2ε`），ε 取 3~8% → 轴比 1.06~1.19。
    /// `--inktest` 里那张"分界线扫描表"（50 条手画圆 + 50 条手画椭圆）实测：
    ///
    /// | 阈值 | 判对(圆) | 判对(椭圆) | 合计 |
    /// |---|---|---|---|
    /// | 1.10 | 38% | 98% | 68% |
    /// | **1.12（原来）** | **50%** | 96% | 73% |
    /// | 1.15 | 76% | 94% | 85% |
    /// | **1.20（现在）** | **100%** | 88% | **94%** |
    /// | 1.25 | 100% | 86% | 93% |
    /// | 1.40 | 100% | 66% | 83% |
    ///
    /// —— 手画圆的轴比中位**正好落在 1.12**，所以原来那条线把**一半的圆**送给了椭圆。
    /// 1.20 是合计最高的一档（94%），代价是"轴比 1.15~1.20 的椭圆会被当成圆"（约 8%）：
    /// 那一档**本来就分不开**（见下表），按用户"接近分界线就算圆"的口径，
    /// **往圆那边让**是对的（圆是更高频、也更"该圆"的形状）。
    ///
    /// ⚠ 这一条**只影响"圆还是椭圆"**：两个拟合各自的残差闸（`CircleResidualRatio`
    /// 9% / `EllipseResidualRatio`）照旧，所以"四不像"不会因为这条线被吸进圆里。
    /// </summary>
    internal const float EllipseAxisRatioMin = 1.20f;

    /// <summary>重采样间距（逻辑像素）：角点检测和拟合都要求点大致等距，
    /// 而原始采样是"快的地方稀、慢的地方密"。4 这个数和笔迹在屏幕上的
    /// 常见采样密度同量级（实测约 4~6 px 一个点）。</summary>
    private const float ResampleStep = 4f;

    /// <summary>重采样的点数上下限：太少算不出角，太多白算。</summary>
    private const int ResampleMin = 16;
    private const int ResampleMax = 400;

    /// <summary>多笔（<see cref="RecognizeChain"/>）时，两笔之间允许多大的缝还连起来。
    /// 判据是"缝 ÷ 两笔平均长度"，会取一个下限 20 px——老师分四笔画矩形，
    /// 角上没接严的缝通常就是这个量级。</summary>
    internal const float ChainGapRatio = 0.14f;
    internal const float ChainGapMinLogical = 20f;

    /// <summary>
    /// 识别器**能产出的全部种类**。自检按它做"表覆盖"断言：加了新识别器却忘了写用例，
    /// 会当场红（照 `--shapebandtest` 那条"名单一致"的做法，同一个理由：
    /// **写死名单会随功能移位而静默失效**）。
    /// </summary>
    internal static StrokeKind[] RecognizableKinds => new[]
    {
        StrokeKind.Line,
        StrokeKind.Circle,
        StrokeKind.Ellipse,
        StrokeKind.Triangle,
        StrokeKind.Rectangle,
        StrokeKind.Parallelogram,
        StrokeKind.Parabola,        // 二次函数四种开口（§43.4.4 一）—— 2026-09-25 加
        StrokeKind.Hyperbola,       // 高中双曲线（§43.4.11）—— 2026-09-25 加
        StrokeKind.Sine,            // 正弦（§43.4.4 三）—— 2026-09-26 加
        StrokeKind.Cosine,          // 余弦（§43.4.4 三）—— 2026-09-26 加
        StrokeKind.Wave,            // 波浪线（多个周期，§43.4.4 三）—— 2026-09-26 加
    };

    /// <summary>
    /// 把识别结果的**曲线朝向**写进对象（抛物线 / 双曲线）。
    ///
    /// 为什么单独一个函数、和 <see cref="ApplyRotation"/> 并排：朝向**不在定义元素里**，
    /// 是对象上独立的一位（<see cref="Stroke.SetParabolaAxis"/>）。**写回只走
    /// `SetPoints` ＋ 这两个 Apply_**（照 42.9 第 3 条："不许再抄一份几何"）——
    /// 引擎（`DwellAssist.BuildShapeStroke`）和自检（`RecoProbe`）都调它，
    /// 各写各的迟早差一处（本轮就是漏了这一处：写回包围盒差 53.7%）。
    ///
    /// ⚠ **这里面那三句、顺序一句都不能动** —— `SetParabolaAxis` 有两处反直觉的地方，
    /// 两个顺序都试过、都错，实测数字记在这儿：
    ///   ① 它的第一行是 `if (Kind != … || Points.Count &lt; 2) return;`
    ///      → **点还没放的时候调它等于没调**（先写朝向 → 朝向根本没生效，包围盒差 **53.7%**）；
    ///   ② 写下去的时候它会顺手把"经过点"按**新旧两套基重新投影**一次
    ///      —— 那是给"用户在操作条上换朝向"用的（为了换完曲线不瘪）；
    ///      而识别出来的 `Def` **已经是目标基下的坐标**，那一次投影是多余的、会把 `q` 搅乱
    ///      （只在放点之后写一次 → 包围盒差 **29.3%**，而且怎么算都对不上一个自洽的
    ///      (轴向, p, 跨度) 组合 —— 一眼就该怀疑"有一条几何被改了两次"）。
    /// 所以是：**先放点（让它肯写）→ 写朝向（接受那次多余投影）→ 再放一次真点（盖回来）**。
    /// </summary>
    internal static void ApplyAxis(Stroke s, in ShapeGuess g)
    {
        if (s == null) return;

        // **双曲线不走 `SetParabolaAxis`** —— 它走对象自己的"两步创建"：
        //   `SetHyperbolaFromAsymptote`（中心 ＋ 渐近线框角点）→ `SetHyperbolaThroughPoint`
        //   （经过点，由它反解"曲线自己的实半轴"）。
        //
        // 为什么必须走这两个入口、而不是自己填三个点：**朝向是这两步自己重定的**
        // （`HyperbolaAxisThroughPoint`），自己填点会让朝向停在占位值上（`SetHyperbolaFromAsymptote`
        // 里那句"占位朝向，第二步重定"就是这个意思）。
        // 而且这两个函数**正是图形工具那边用的入口** —— 同一条路，不抄第二份几何（42.9 第 3 条）。
        if (g.Kind == StrokeKind.Hyperbola && g.Def != null && g.Def.Length >= 3)
        {
            var hd = g.Def;
            // 末位 `minSize` 只是"防零宽框"的下限（里面就是 `Max(minSize, |Δ|)`），
            // 真实大小由角点和经过点决定 —— 给个很小的值即可。
            s.SetHyperbolaFromAsymptote(hd[0].X, hd[0].Y, hd[1].X, hd[1].Y, 1f);
            s.SetHyperbolaThroughPoint(hd[2].X, hd[2].Y);
            // 渐近线是**拟合出来的已知量**，默认画上（和图形工具那两档里的"带渐近线"一致）。
            s.SetShowAsymptotes(true);
            return;
        }

        // **正弦 / 余弦**：走对象自己的入口 `SetWaveBox`（和图形工具那一拖**同一个函数**）。
        //
        // 它的两个定义元素是"**起点 ＋ 终点**"，终点一次定下**周期和振幅**
        // （见 `Model.SetWaveBox`：终点 = `(起点.x + 周期, 起点.y + dy)`）。
        // 识别器给的 `Def` 已经是这两个点，所以这里只是把它落到对象上 ——
        // **没有姿态角、没有曲线朝向**（正余弦不旋转、也没有开口方向那一维）。
        if (g.Kind == StrokeKind.Sine || g.Kind == StrokeKind.Cosine)
        {
            if (g.Def == null || g.Def.Length < 2) return;
            var wd = g.Def;
            // 末位 `minSize` 是"防零宽 / 零高"的下限，真实大小由这两点决定（和双曲线那处同理）。
            s.SetWaveBox(wd[0].X, wd[0].Y, wd[1].X, wd[1].Y, 1f);
            return;
        }

        // **波浪线**（多周期）：两个定义元素和上面一样（起点 ＋ 终点＝要画多长 ＋ 振幅），
        // **但周期是第三个点**（`Def[2]`，只用到它的 x —— 见 `Stroke.SetWavePeriod`）。
        // 这一步不能少：不写的话周期会落回"= 振幅"那条老规矩，多周期墨迹会被压扁/拉长。
        if (g.Kind == StrokeKind.Wave)
        {
            if (g.Def == null || g.Def.Length < 3) return;
            var wd = g.Def;
            s.SetWaveBox(wd[0].X, wd[0].Y, wd[1].X, wd[1].Y, 1f);
            s.SetWavePeriod(wd[2].X - wd[0].X);
            return;
        }

        if (g.Kind != StrokeKind.Parabola) return;
        var def = g.Def;
        s.SetPoints(def);
        s.SetParabolaAxis(g.Axis);
        s.SetPoints(def);
    }

    /// <summary>
    /// **两端切线的夹角**（度，0~180）—— 用户 2026-09-25 定的那条"老师感觉"约定：
    ///
    /// &gt; "末端那个地方切线接近平行，那肯定是抛物线；如果末端切线切出来以后形成
    /// &gt; 一个**显著的夹角**（45°、60°、120° 这种），那肯定就是双曲线。"
    ///
    /// **为什么它成立**（几何上验算过，不是感觉）：
    ///   · **抛物线**从顶点往外越走越陡，两端都趋向**竖直** → 两条切线**趋于平行** → 夹角 → **180°**；
    ///   · **双曲线**一支的两端各自贴向**它自己的那条渐近线** `y = ±(b/a)x`，
    ///     而渐近线的角度**卡住不动** → 夹角 → **2·arctan(b/a)**，是一个**卡在中间的角**
    ///     （`b/a = 0.5` → 53°、`= 1` → 90°、`= 1.7` → 119°）—— 正是用户说的 45°/60°/120°。
    ///
    /// **为什么它比"两个模型比残差"强**（这是换判据的真正理由）：
    ///   残差是**逐点比**的，噪声直接进分子，比值被噪声主导 —— 实测真实手抖下双曲线只剩 40%。
    ///   而这里算的是**一个窗口里几十个点的方向**，噪声**在窗口内被平均掉**，所以稳得多。
    ///
    /// 取法：在**首尾各取一个窗口**（外圈 20%，至少 3 个点），各算一次**主方向**
    /// （2×2 协方差的主特征向量 = 最小二乘意义下的直线方向），都**朝外**定向
    /// （从弧的中间指向那一端），再取两者的夹角。
    /// 点数太少（&lt; 8）返回 180 —— 即"看不出夹角"，交回给别的判据去分。
    /// </summary>
    internal static float EndTangentAngleDeg(IReadOnlyList<Vector2> pts)
    {
        if (pts == null || pts.Count < 8) return 180f;
        int n = pts.Count;
        int win = Math.Max(3, n / 5);
        var mid = pts[n / 2];

        var uStart = WindowDir(pts, 0, win);
        var uEnd = WindowDir(pts, n - win, n);
        // 都朝外定向：从弧的中间指向各自那一端（不这样定向的话，"平行"和"反平行"
        // 会被 `acos` 折成同一个角 —— 而 180° 和 0° 在这里恰恰是**相反的两族**）
        if (Vector2.Dot(uStart, pts[0] - mid) < 0f) uStart = -uStart;
        if (Vector2.Dot(uEnd, pts[n - 1] - mid) < 0f) uEnd = -uEnd;

        float dot = Math.Clamp(Vector2.Dot(uStart, uEnd), -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }

    /// <summary>一段窗口的主方向（2×2 协方差的主特征向量，即最小二乘直线方向）。</summary>
    private static Vector2 WindowDir(IReadOnlyList<Vector2> pts, int from, int to)
    {
        var c = Vector2.Zero;
        for (int i = from; i < to; i++) c += pts[i];
        c /= to - from;
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = from; i < to; i++)
        {
            double dx = pts[i].X - c.X, dy = pts[i].Y - c.Y;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        if (sxx + syy < 1e-12) return Vector2.UnitX;
        double th = 0.5 * Math.Atan2(2 * sxy, sxx - syy);   // 主方向角
        return new Vector2((float)Math.Cos(th), (float)Math.Sin(th));
    }

    /// <summary>两笔合成双曲线时，**对称校验的容差**（占另一笔尺度的比例）。
    ///
    /// ⚠ 它是 **static 字段**（不是 const）**只为一件事**：`--inktest` 里那段
    /// "手画两支"的**只报告扫描表**要把它**扫一遍**（0.25 / 0.35 / 0.45 / 0.6），
    /// 拿"正例识别率 ↑"和"误报率 ↑"两条曲线一起看，才知道该停在哪一档 ——
    /// 用户 2026-09-26 第二次报"双曲线怎么都变不出来，是不是卡得太严"，
    /// 而**松到多少**必须量出来（不许拍）。
    /// ⚠ 除自检外**没有第二处写它**。
    /// </summary>
    internal static float TwoBranchSymmetryTol = 0.25f;

    /// <summary>
    /// **两笔 → 双曲线**（用户 2026-09-25 定的约定）：
    ///
    /// &gt; "如果连续画两只（第一只、第二只），那它一定是双曲线；如果画单只，就识别为抛物线。
    /// &gt; 不管它实际上是抛物线还是双曲线，我们只要按照这个来区分。"
    ///
    /// **为什么这一刀最干净**：形状判断是**连续、有噪声**的（夹角、残差全栽在这上面 ——
    /// 真实手抖下双曲线只有 25~40%），而"**画了几笔**"是**离散、零噪声**的信号：
    /// 老师画两支的时候，**他自己知道**在画双曲线。所以族的选择**不再交给拟合**，
    /// 拟合只负责"造一个像的"（用户原话："不需要拟合得有多准确，只要能识别对即可"）。
    ///
    /// **保险（必须有）**：两笔得**像同一个双曲线的两支**（共用中心、近似**中心对称**）。
    /// 没有这条，一条**被系统拆成两笔**的抛物线就会变成双曲线 ✗
    /// （中途笔尖轻提一下、或者停顿把笔截断，都可能拆笔 —— 这事真的会发生）。
    ///
    /// **不分先后**：哪一笔当"第一笔"都一样 —— 中心对称是**对称关系**，双向试等价。
    ///
    /// **不怎么办**：认不出来就 `IsNothing`，**继续往下走**原来那条"按缝拼接"的路 ——
    /// 绝不抢别的活（所以两个不相干的笔画在这里被否掉之后，仍能各归各）。
    /// </summary>
    internal static ShapeGuess TryTwoBranchHyperbola(IReadOnlyList<Vector2> a,
                                                     IReadOnlyList<Vector2> b, float scale)
    {
        if (a.Count < 8 || b.Count < 8) return ShapeGuess.None("两笔里有一笔太短");

        // ①b **两支的尺寸必须相当**（2026-09-26 加，`--dwelltest` 抓到的真误配）。
        //
        //   双曲线的两支**全等**，所以人画出来的两支尺寸自然接近（实测 14 对真样本：
        //   0.83~0.99）。反例是"**一个没成型的小团 ＋ 一条长直线**"（0.14）——
        //   它居然能过"对称"那道闸（原因见 `TwoBranchSizeRatioMin` 的注释：小支当查询时
        //   偏差天然小、容差又按大支算）。放在这里（**所有闸之前、且是 O(n)**）：
        //   最便宜的一条先挡掉，后面那些 O(n·m) 的校验就不用白算了。
        float diagA = Diag(a), diagB = Diag(b);
        float sizeRatio = MathF.Min(diagA, diagB) / MathF.Max(diagA, diagB);
        if (sizeRatio < TwoBranchSizeRatioMin)
            return ShapeGuess.None($"两支大小差太多（尺寸比 {sizeRatio:F2} < {TwoBranchSizeRatioMin:F2}，"
                                   + $"A 对角线 {diagA:F0}、B 对角线 {diagB:F0}）"
                                   + "—— 小的那支比大的小一倍多，不像同一个双曲线的两支");

        // ① 中心 = "两支**离得最近的那一对点**"的中点。
        //    对称的两支，最近的一对正是**两个顶点**，它们的中点也就是双曲线的中心 ——
        //    这一步不需要拟合，所以不受手抖影响。
        float best = float.MaxValue;
        Vector2 pa = a[0], pb = b[0];
        foreach (var p in a)
            foreach (var q in b)
            {
                float d = Vector2.DistanceSquared(p, q);
                if (d < best) { best = d; pa = p; pb = q; }
            }
        var o = (pa + pb) * 0.5f;

        // ② 对称校验：把 A 绕中心**点反射**，量它到 B 那条折线的最大偏差。
        //    中心对称正是双曲线自己的性质（`HyperbolaPoint` 里 `branch` 取 ±1 的两支
        //    就是互为点反射 —— 也正是"另一支免费补出来"那条的几何根据）。
        //
        // ⚠⚠ **两种顺序都要试，取小的那个** —— 这是自检抓出来的真 bug：
        //   点反射**会把走向翻过来**（参数 `t` 从 +1.6 变 −1.6），而
        //   `MaxDeviationToPolyline` 用的是**滑窗**（它假设两条折线的点序一一对应，
        //   那对"墨迹 vs 拟合曲线"永远成立）。顺序一反就整段错位 ——
        //   实测：**自己造的对称语料被判成"镜像偏差 412 > 容差 111"**，
        //   看着像"两笔不对称"，其实是比法错了。而**用户画第二支时从上往下还是
        //   从下往上本来就是随机的**，所以不能假定顺序。
        //
        // ❗❗ **还有第三个方向：`B → 镜像 A`**（2026-09-26 加，用户报"画几次才成一次"）。
        //   上面那两种都是"拿**镜像 A** 当查询" —— 用户要是第一支画得长、第二支画得短
        //   （手画天天这样），镜像 A 多出来的那一截**全都算成"不对称"**：
        //   实测（`--inktest` 的"手画两支"只报告段）**识别率只有 58%，25 条失败里 23 条
        //   卡在这一条**。而"一支画得长一支画得短"**根本不是不对称** ——
        //   两条曲线在**重叠的那一段**上贴得住就够了。
        //   所以四个方向取最小：这等于**拿短的那一支当查询**（谁是短的、由数值自己定）——
        //   和 `InkNearOutline` 只查"墨迹 → 轮廓"那一个方向是**同一条口径**
        //  （那边：不查"轮廓 → 墨迹"，因为"用户没画完的那一段"正是要补的）。
        var mirrored = new Vector2[a.Count];
        for (int i = 0; i < a.Count; i++) mirrored[i] = 2f * o - a[a.Count - 1 - i];
        var mirroredRev = new Vector2[a.Count];
        for (int i = 0; i < a.Count; i++) mirroredRev[i] = 2f * o - a[i];
        float devA = CurveFit.MaxDeviationToPolyline(mirrored, b);
        float devB = CurveFit.MaxDeviationToPolyline(mirroredRev, b);
        float devC = CurveFit.MaxDeviationToPolyline(b, mirrored);      // 短的那支当查询
        float devD = CurveFit.MaxDeviationToPolyline(b, mirroredRev);
        float dev = MathF.Min(MathF.Min(devA, devB), MathF.Min(devC, devD));
        float sizeB = Diag(b);
        bool symmetric = dev <= TwoBranchSymmetryTol * sizeB;

        // ★★ **第三轮（用户 2026-09-26）：对称过不了时，退一步只问"是不是两段反弧"** ——
        //
        // 用户原话："**双曲线识别可不可以改为，连续两段反弧线就识别为双曲线？
        // 因为我手测了一下，双曲线还是很难画出来**。"
        //
        // 他的理由站得住：**"中心对称"是拿"理想作图"当门槛**，而老师是**手画**的
        // （一支大一支小、位置差一截都常见）。所以对称过不了时**不再直接否掉**，
        // 退一步问一个**只跟形状有关、跟"画得多准"无关**的问题。
        //
        // ⚠⚠ **"反弧"必须问对**：第一版写成"两段的拱起方向符号相反"，实测**误报 12%**
        //（"同一边画两遍"两支的拱向本来就同号 ✗ 判据没抓住）✗。
        //   正确的问法是"**两段的拱起方向都朝向对方**"—— 因为双曲线的一支，
        //   **顶点是这一支最靠内的一点**：把它首末连成弦，弧是**朝中心那股**鼓的
        //（弧口朝外、顶点朝内）。所以两支的拱向必然**互指** ✓✓。
        //   这样一来，"同一边画两遍"**必然被挡住**：两支拱向同侧 ⇒ 对其中一支来说，
        //   另一支在它拱向的**背面** ✗（同号的两支，不可能同时"都朝对方拱"）✓。
        //
        // ⚠ **这条判据挡不住什么**（所以下面②b、③那两道闸一道都不能少）：
        //   · "**半个椭圆 ＋ 半个椭圆**"（画一个椭圆分两笔）—— 它们**也是两段反弧** ✗，
        //     要靠 **②b 端点不能接在一起** 挡住（原来是靠 `TryFitConic` 的判别式，
        //     换成"直接按双曲线拟合"之后那道闸没了，所以**换了一条**——见 ②b）；
        //   · 两支大小 / 位置差得太离谱（那已经不是同一个双曲线了）→ 拟合残差那道闸 ✓。
        var bulgeA = BulgeDir(a);
        var bulgeB = BulgeDir(b);
        bool towardEachOther = false;
        if (bulgeA != Vector2.Zero && bulgeB != Vector2.Zero)
        {
            var ca = Centroid(a);
            var cb = Centroid(b);
            var ab = cb - ca;
            if (ab.LengthSquared() > 1e-6f)
            {
                var dir = Vector2.Normalize(ab);
                // 0.2 是"别只靠一点点夹角就认"，不是精细阈值：真正的双曲线两支这一项 ≈1。
                towardEachOther = Vector2.Dot(bulgeA, dir) > 0.2f
                               && Vector2.Dot(bulgeB, -dir) > 0.2f;
            }
        }
        if (!symmetric && !towardEachOther)
            return ShapeGuess.None($"两笔不像同一个双曲线的两支（镜像偏差 {dev:F0} > "
                                   + $"{TwoBranchSymmetryTol * sizeB:F0}，而且不是两段反弧"
                                   + $"（拱起方向 A {BulgeWord(bulgeA)}、B {BulgeWord(bulgeB)}，"
                                   + $"要**互指**才算）；中心 ({o.X:F0},{o.Y:F0})；"
                                   + $"A 框 {BBox(a)}；B 框 {BBox(b)}）");

        // ②b **两笔的端点不能"接在一起"**（2026-09-26 加，**用手画的 14 对真样本量出来的**）。
        //
        //   ⚠ 这一条是**原来那道"判别式"闸的替身**：这一轮把"自由拟合圆锥曲线、看判别式分族"
        //     换成了"**直接按双曲线拟合**"（理由见 ③），判别式那道闸就没了 ——
        //     而"**半个椭圆 ＋ 半个椭圆**"（一个椭圆分两笔画）会被新拟合器愉快地当成
        //     一条很扁的双曲线 ✗✗，**必须换一条判据挡住它**。
        //
        //   两者的区别很干净：**半椭圆的两笔端点接在一起**（首尾就是同一个点、间隙 ≈ 0），
        //   而**双曲线的两支离得很开**（最近的一对点是顶点，端点还要更远）。
        //   实测：用户 14 对真样本这一项都在 0.3 以上，半椭圆那一档 ≈ 0.00 → 分得开 ✓。
        float endGap = MathF.Min(MathF.Min(Vector2.Distance(a[0], b[0]), Vector2.Distance(a[0], b[^1])),
                                 MathF.Min(Vector2.Distance(a[^1], b[0]), Vector2.Distance(a[^1], b[^1])));
        float sizeAB = MathF.Max(Diag(a), Diag(b));
        if (endGap < TwoBranchEndGapTol * sizeAB)
            return ShapeGuess.None($"两笔的端点接在一起（{endGap:F0} < {TwoBranchEndGapTol * sizeAB:F0}）"
                                   + "—— 像是一条闭合曲线分成两笔画的，不是双曲线的两支");

        // ③ **直接按双曲线拟合**（不再"自由拟合圆锥曲线 ＋ 看判别式"）——
        //
        //    ⚠ 这一改是**用户手画的 14 对真样本**逼出来的：14 对里 **8 对**卡在
        //      「判别式说这是椭圆」✗。原因很实在：老师画的双曲线**常常只画到顶点附近**，
        //      那两段弧并起来，**自由拟合最省事的那条圆锥曲线就是一条扁椭圆** ✗。
        //      但按用户定的口径"**画两支就是双曲线**"，族**早在笔数上定了**，
        //      拟合只负责**定形** —— 所以正确做法是"**就按双曲线拟合、看它贴不贴得住**" ✓。
        //
        //    拟合的式子：`u·x̂² − v·ŷ² = 1`（`u = 1/a²`、`v = 1/b²`，`x̂ = x − 中心`）——
        //    对 `u`、`v` 是**线性**的，正规方程 2×2 有闭式解 ✓。
        //    朝向（实轴在 x 还是 y）问**拱起方向**：两支拱向互指的那个方向就是实轴方向 ✓。
        var axisDir = bulgeA != Vector2.Zero && bulgeB != Vector2.Zero
                    ? bulgeA - bulgeB                      // 两支拱向相反 ⇒ 差向量就是实轴方向
                    : Centroid(b) - Centroid(a);
        if (axisDir.LengthSquared() < 1e-6f) axisDir = Vector2.UnitX;
        bool transverseX = MathF.Abs(axisDir.X) >= MathF.Abs(axisDir.Y);
        var union = new List<Vector2>(a.Count + b.Count);
        union.AddRange(a);
        union.AddRange(b);
        // ★ **验收容差乘一个倍率**（2026-09-26 加，用户第二次报"双曲线怎么都变不出来"）。
        //   原来倍率是 1（和单笔同一条容差），而**两笔拼出来的墨迹天生比一笔"散"**：
        //   两支各自的大小、开口、位置都有出入，并起来喂给一条双曲线，残差必然比单笔大。
        //   实测（`--inktest` 那张扫描表）：**28 条失败里 27 条**卡的就是"贴不住墨迹"，
        //   而且**和对称容差一点关系都没有**（对称容差从 0.25 扫到 0.80，识别率一动不动）。
        float tolScale = symmetric ? TwoBranchAcceptTolScale : TwoBranchLooseTolScale;
        // 两个朝向都试一遍（万一"拱向"给的朝向和真正的实轴差了 90°），取先成功的那个 ——
        // **不写死"就用拱向给的那个"**，免得一条判据错两层。
        var guessX = FitTwoBranchHyperbolaAs(union, o, transverseX, scale, tolScale);
        if (!guessX.IsNothing) return guessX;
        return FitTwoBranchHyperbolaAs(union, o, !transverseX, scale, tolScale);
    }

    /// <summary>
    /// **按给定朝向拟合一条轴对齐双曲线**：`u·x̂² − v·ŷ² = 1`（`u = 1/a²`、`v = 1/b²`），
    /// 验收集是"**拟合出来的这条双曲线贴不贴得住墨迹**"（最大 Sampson 距离）。
    ///
    /// 参数不合法（`u ≤ 0` 或 `v ≤ 0` —— 说明这一族在这个朝向下拟合不出来，比如它其实是椭圆）
    /// 就返回 `IsNothing`，由调用方换个朝向再试。
    /// </summary>
    private static ShapeGuess FitTwoBranchHyperbolaAs(IReadOnlyList<Vector2> src, Vector2 o,
                                                      bool transverseX, float scale, float tolScale)
    {
        // ① **实半轴 `a` = 这堆墨迹离中心最近的距离**（也就是顶点到中心的距离）。
        //
        //    ⚠ 为什么把 `a` **先钉住**、而不和 `b` 一起最小二乘（2026-09-26，**用户手画的
        //      14 对真样本量出来的**）：老师画双曲线**常常只画到顶点附近**，而那些点上
        //      `ŷ ≈ 0` —— **共轭半轴 `b` 几乎不可辨识** ✗。一起解的话，`b` 连**符号**都被
        //      噪声带着跑（实测 8/14 报 `u>0、v<0`，判据自己都说不清是双曲线还是椭圆 ✗）。
        //      而**顶点距离 `a` 是认得出来的**：它就是最近的那一点 ✓（和画多长、画多草无关）。
        float aAxis = float.MaxValue;
        foreach (var p in src)
            aAxis = MathF.Min(aAxis, MathF.Abs(transverseX ? p.X - o.X : p.Y - o.Y));
        if (aAxis < ConicMinHalfAxis * scale)
            return ShapeGuess.None($"墨迹离中心太近（{aAxis:F1}）——不像双曲线的顶点");

        // ② **共轭半轴 `b`：从每一点的"弯"里反解，取中位数**（抗离群）。
        //    反解：`x̂²/a² − ŷ²/b² = 1` ⇒ `b² = ŷ² / (x̂²/a² − 1)`。
        //
        //    ⚠ 分母 ≤ 0 的点（落在**顶点以内** = 弧**朝里**弯）**没有 `b²` 可言** ——
        //      那正是"**它其实是椭圆的头**"的情形（椭圆在顶点附近 `x ≈ a − a·ŷ²/(2b²)`，
        //      弧朝里弯 ✗）。所以这一条**就是原来那道"判别式"的替身**，但它量的是
        //      **弧朝哪边弯**（几何上直接可读），比拟合系数的符号稳得多 ✓。
        //      —— 实测：用户 14 对真样本的弧**全部朝外弯**（弧口朝外、顶点朝内），
        //         而"半椭圆分两笔"那一档全部落在这一条上 ✓。
        var b2 = new List<float>();
        foreach (var p in src)
        {
            float x = transverseX ? p.X - o.X : p.Y - o.Y;
            float y = transverseX ? p.Y - o.Y : p.X - o.X;
            float denom = x * x / (aAxis * aAxis) - 1f;
            if (denom > 0.01f) b2.Add(y * y / denom);      // 1% 以上才算"弯得可信"
        }
        if (b2.Count < 6 || b2.Count < src.Count / 8)
            return ShapeGuess.None($"这两笔的弧**朝里弯**（{src.Count - b2.Count}/{src.Count} 个点"
                                   + "落在顶点以内）—— 像椭圆的头，不是双曲线");
        b2.Sort();
        float bAxis = MathF.Sqrt(MathF.Max(b2[b2.Count / 2], 1e-6f));
        if (bAxis < ConicMinHalfAxis * scale)
            return ShapeGuess.None($"共轭半轴太小（{bAxis:F1}）——模型会缩成一个点");

        // ③ 验收：**最大 Sampson 距离** `|f| / |∇f|`（比代数残差稳，§43.4.6 那条）
        double u = 1.0 / ((double)aAxis * aAxis), v = 1.0 / ((double)bAxis * bAxis);
        float worst = 0f;
        foreach (var p in src)
        {
            double dx = p.X - o.X, dy = p.Y - o.Y;
            double X = transverseX ? dx * dx : dy * dy;
            double Y = transverseX ? dy * dy : dx * dx;
            double f = u * X - v * Y - 1.0;
            double gx = transverseX ? 2 * u * dx : -2 * v * dx;
            double gy = transverseX ? -2 * v * dy : 2 * u * dy;
            float g = MathF.Sqrt((float)(gx * gx + gy * gy));
            float d = g > 1e-6f ? (float)Math.Abs(f) / g : float.MaxValue;
            worst = MathF.Max(worst, d);
        }
        float tol = FitTol(scale, src) * tolScale;
        if (worst > tol)
            return ShapeGuess.None($"按双曲线拟合了，还是贴不住墨迹（最远 {worst:F0} > 容差 {tol:F0}）");

        // ④ 组装定义元素（口径和原来完全一样：中心 ＋ 渐近线框上的点 ＋ 曲线上的点）
        float reach = 0f;
        foreach (var p in src)
            reach = MathF.Max(reach, MathF.Abs(transverseX ? p.X - o.X : p.Y - o.Y));
        float tEnd = MathF.Acosh(MathF.Max(1f, reach / MathF.Max(aAxis, 1e-3f)));
        var axis = transverseX ? CurveAxis.TransverseX : CurveAxis.TransverseY;
        var q = Stroke.HyperbolaPoint(o, aAxis, bAxis, axis, 1, tEnd);
        var box = new Vector2(aAxis, bAxis) * MathF.Cosh(tEnd);   // 渐近线框放大一档（照旧）
        return ShapeGuess.HitAxis(StrokeKind.Hyperbola, 0.85f,
            $"双曲线·两支（{(transverseX ? "左右" : "上下")}开口，实半轴 {aAxis:F0}、共轭 {bAxis:F0}，"
            + $"贴住墨迹：最远差 {worst:F0} ≤ 容差 {tol:F0}）",
            axis, o, o + box, q);
    }

    /// <summary>
    /// **这一笔朝哪边"拱"**：返回**拱起方向的单位向量**（弦的法向）；太直了看不出拱向 → `Zero`。
    ///
    /// 做法：取**首末点连成的弦**，把弦**规范化**（保证 `dx > 0`；`dx ≈ 0` 时保证 `dy > 0`），
    /// 再算**中段那些点**在弦的哪一侧 —— 叉积的**平均符号**决定方向，法向就是那个方向。
    ///
    /// ⚠ **必须先规范化弦的方向**：叉积符号会随"**从哪一头画起**"整体翻转，
    ///   而用户画第二支时从上往下还是从下往上**本来就是随机的** ——
    ///   不规范化的话"拱向"会时对时错（和上一轮"镜像要试两个顺序"是同一个坑）。
    ///
    /// ⚠ 判"太直"的阈值取 **2% 弦长**（中段平均偏离）：手抖是 ±0.8px 量级，
    ///   而一段真正的弧，中段偏离弦是**弦长的百分之几到几十**，两者量级差得开。
    /// </summary>
    private static Vector2 BulgeDir(IReadOnlyList<Vector2> pts)
    {
        int n = pts.Count;
        if (n < 5) return Vector2.Zero;
        var p0 = pts[0];
        var p1 = pts[n - 1];
        var chord = p1 - p0;
        if (chord.LengthSquared() < 1e-6f) return Vector2.Zero;
        // 规范化：让弦指向"右 / 下"（dx>0，或 dx≈0 时 dy>0）
        if (chord.X < 0f || (MathF.Abs(chord.X) < 1e-3f * MathF.Abs(chord.Y) && chord.Y < 0f))
        {
            (p0, p1) = (p1, p0);
            chord = -chord;
        }
        float sum = 0f;
        int cnt = 0;
        for (int i = n / 4; i <= n * 3 / 4; i++)          // 中段：避开两端的手抖和收笔
        {
            var d = pts[i] - p0;
            sum += chord.X * d.Y - chord.Y * d.X;          // 叉积（>0 = 在弦的"正法向"那一侧）
            cnt++;
        }
        if (cnt == 0) return Vector2.Zero;
        float mean = sum / cnt;
        if (MathF.Abs(mean) < 0.02f * chord.LengthSquared()) return Vector2.Zero;
        var normal = new Vector2(-chord.Y, chord.X);       // `cross(chord, normal) = |chord|² > 0`
        var dir = Vector2.Normalize(normal);
        return mean > 0f ? dir : -dir;
    }

    /// <summary>一串点的**重心**（判"拱起方向是不是朝向对方"用）。</summary>
    private static Vector2 Centroid(IReadOnlyList<Vector2> pts)
    {
        var s = Vector2.Zero;
        foreach (var p in pts) s += p;
        return pts.Count > 0 ? s / pts.Count : Vector2.Zero;
    }

    /// <summary>拱起方向写成给人看的话（判据字符串里用）。</summary>
    private static string BulgeWord(Vector2 dir)
        => dir == Vector2.Zero ? "没拱（太直）" : $"({dir.X:F2},{dir.Y:F2})";

    /// <summary>`BulgeDir` / `Centroid` 的**自检与离线分析入口**（`--inkfile` 要按同一条口径
    /// 量用户手画的样本，所以判据只留上面那一份，这里只开个口子）。</summary>
    internal static Vector2 BulgeDirForTest(IReadOnlyList<Vector2> pts) => BulgeDir(pts);

    /// <inheritdoc cref="BulgeDirForTest"/>
    internal static Vector2 CentroidForTest(IReadOnlyList<Vector2> pts) => Centroid(pts);

    /// <summary>
    /// **只靠"反弧"认下时，验收容差的倍率**（比 <see cref="TwoBranchAcceptTolScale"/> 松得多）。
    ///
    /// 依据是用户 2026-09-25 自己定的那条口径：
    /// "**只要用户有这个意向，且匹配度超过一定百分比，我们就把它拟合出来。
    ///   不需要拟合得有多准确，只要能识别对即可。**"
    /// —— 画两段反弧就是"有这个意向"，此时该由模型给出一条**规整的双曲线**，
    /// 而不是拿"贴得多紧"去把人挡在门外。
    ///
    /// ⚠ 松的是**残差**，**判别式（异号才算双曲线）和中心对称那道闸的替代品（反弧）都不松** ——
    ///   所以"半个椭圆 + 半个椭圆"变不成双曲线。
    /// ⚠ 取值 **扫出来**（`--inktest` 那张扫描表）。
    /// </summary>
    internal static float TwoBranchLooseTolScale = 8f;

    /// <summary>
    /// **两笔合成时，验收容差的倍率**（默认 1 = 和单笔同一条）。
    ///
    /// ⚠ 和 <see cref="TwoBranchSymmetryTol"/> 一样是 **static 字段**，只为让
    /// `--inktest` 那张扫描表把它**扫一遍**（倍率 ↑ → 正例识别率 ↑，但要同时看
    /// "同一边画两遍 / 中心离很远"两类误报有没有抬头）—— **松到多少必须量出来**。
    /// 除自检外没有第二处写它。
    /// </summary>
    internal static float TwoBranchAcceptTolScale = 3f;

    /// <summary>
    /// **两笔的端点"接在一起"的判定**（占尺度的比例，小于它就否掉）：见 `TryTwoBranchHyperbola`
    /// 的 ②b —— 它是**"判别式"那道闸的替身**，专门挡"**一个椭圆分成两笔**"（两笔共用端点）。
    ///
    /// 实测：用户手画的 14 对真样本，这一项**都在 0.3 以上**；"半椭圆分两笔"那一档 ≈ 0.00
    /// —— 取 **0.10** 落在中间，两边都留足余量（`--inktest` 那张表盯着两边的误报）。
    /// </summary>
    internal static float TwoBranchEndGapTol = 0.10f;

    /// <summary>
    /// **两支的尺寸比下限**（小/大，小于它就否掉）：见 `TryTwoBranchHyperbola` 的 ①b。
    ///
    /// ⚠ 这一条是 `--dwelltest` 的「直线拖端点：转向 + 吸水平」**当场抓出来的真误配**：
    ///   上一个用例留下一个"太小不转换"的小圆（r=15，直径 30），接着画的直线长 300
    ///   —— 这两个**被配成了一对双曲线**（尺寸比 0.14），于是那支直线变成了双曲线的一支、
    ///   拖端点当然就不对了。
    ///
    /// 为什么"对称"那道闸拦不住它：对称校验的容差是 `TwoBranchSymmetryTol × 大支的对角线`，
    ///   而偏差是"**查询点 → 参考折线**"的最大距离。**小支当查询**时，它那几十个点全落在
    ///   大支附近（那个小圆镜像后正好落在大支的端点上，偏差只有 15）→ 15 ≤ 0.25×300 = 75
    ///   → 判"对称" ✓。**这条洞是系统性的**：查询越小，越容易"贴住"，所以必须**另外**卡尺寸。
    ///
    /// 实测分得很开：用户手画的 **14 对真样本，尺寸比全在 0.83~0.99**（双曲线的两支本来就
    ///   全等，画得像的人自然是 1:1）；这次误配那对是 **0.14**。取 **0.45**（≈2.2 倍）
    ///   落在中间，两边都留足余量。
    /// </summary>
    internal static float TwoBranchSizeRatioMin = 0.45f;

    /// <summary>一串点的**包围盒对角线**（当这一笔的"尺度"用）。</summary>
    private static float Diag(IReadOnlyList<Vector2> p)
    {
        var (minX, minY, maxX, maxY) = Extent(p);
        return MathF.Max(1f, Vector2.Distance(new Vector2(minX, minY), new Vector2(maxX, maxY)));
    }

    /// <summary>一串点的包围盒（诊断用，拼成一行）。</summary>
    private static string BBox(IReadOnlyList<Vector2> p)
    {
        var (minX, minY, maxX, maxY) = Extent(p);
        return $"[{minX:F0},{minY:F0}]-[{maxX:F0},{maxY:F0}]";
    }

    private static (float minX, float minY, float maxX, float maxY) Extent(IReadOnlyList<Vector2> p)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var q in p)
        {
            minX = MathF.Min(minX, q.X); maxX = MathF.Max(maxX, q.X);
            minY = MathF.Min(minY, q.Y); maxY = MathF.Max(maxY, q.Y);
        }
        return (minX, minY, maxX, maxY);
    }

    // =====================================================================
    //  对外入口
    // =====================================================================

    /// <summary>一笔（一串采样点）→ 候选。认不出来时返回 <see cref="ShapeGuess.IsNothing"/>。
    ///
    /// <paramref name="scale"/> = **逻辑像素 → 画布单位**的换算（就是 DpiScale）。
    /// 只有几个"以像素为口径"的门槛用它（最短长度、验收容差的下限）——
    /// 引擎那边一律画布坐标，而"最短 40 像素"这种话是说给人听的逻辑像素。
    /// ⚠ **起笔那一侧不许再抄一遍这个门槛**（`TickDwellShape` 原来自己乘了一次
    /// `MinLength * DpiScale`）——同一个数写在两处必漏一处，这里说了算。</summary>
    internal static ShapeGuess Recognize(IReadOnlyList<Vector2> raw, float scale = 1f)
    {
        if (raw == null || raw.Count < 3) return ShapeGuess.None("点太少");
        return RecognizeCore(raw, scale <= 0f ? 1f : scale, null);
    }

    /// <summary>
    /// **多笔** → 候选（把几笔接成一条链再走同一套判据）。
    ///
    /// 为什么要它：手画的矩形 / 三角形经常是分笔画的（每条边一笔），
    /// 主流软件里 OneNote 与 GoodNotes（`Snap to Endpoints`）都支持多笔拼一个图形，
    /// OneNote 官方还专门建议"笔画尽量首尾相连"——**接不上就不连**，
    /// 这条规矩就是我们这里的 <see cref="ChainGapRatio"/>。
    ///
    /// ⚠ 它**不是**"任意几笔凑一个图形"：接不上就什么都不认（返回 None）。
    /// 这正是 计划-底层性能与功能.md 7.4 说的那条红线——半路凑出来的图形误触率太高。
    ///
    /// ⚠⚠ **截至 2026-09-23，这个方法只有自检（`--inktest`）在调，引擎还没接它**
    ///（`TickDwellShape` 只把**当前这一笔**送去识别）。也就是说"四条边分着画一个矩形"
    /// 目前是**函数层通了、真机没这条路**。为什么不顺手接上：多笔必须在**抬笔那一刻**
    /// 触发（停顿是在每一段之后就触发的，画到第一段就会把它变成一条线，根本攒不出四条边），
    /// 而"抬笔时自动把最近几笔合成一个图形"有误触风险（老师画几条线就可能被凑成矩形），
    /// InkClass 为此把形状识别做成**按形状分类的开关**。要不要接、怎么接，见
    /// 计划-图形工具.md §42.7 待做项，**等用户定**。
    /// </summary>
    internal static ShapeGuess RecognizeChain(IReadOnlyList<IReadOnlyList<Vector2>> parts, float scale = 1f)
    {
        if (parts == null || parts.Count == 0) return ShapeGuess.None("没有笔画");
        if (parts.Count == 1) return Recognize(parts[0], scale);
        scale = scale <= 0f ? 1f : scale;

        var lens = new float[parts.Count];
        float total = 0f, longest = 0f;
        int startAt = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            lens[i] = PathLength(parts[i]);
            total += lens[i];
            if (lens[i] > longest) { longest = lens[i]; startAt = i; }
        }
        if (total < MinLength) return ShapeGuess.None($"总长太短（{total:F0}）");

        // ★ **两笔 → 双曲线**（用户 2026-09-25 定的约定：**画两支就是双曲线，画一支就是抛物线**）。
        //
        // 为什么放在"按缝拼接"**之前**：两支之间的缝本来就很大（那是**分开的两支**，
        // 不是"收口没画严"），所以下面那条按缝拼的路**必然**把它判成"离得太远、不硬接"。
        // 这里用**另一条判据**（中心对称）来接，而不是缝。
        //
        // 认不出就 `IsNothing`，继续往下走 —— **绝不抢别的活**。
        if (parts.Count == 2)
        {
            var two = TryTwoBranchHyperbola(parts[0], parts[1], scale);
            if (!two.IsNothing) return two;
        }

        // 缝的上限：既要有下限（手画收口本就不严），又不能太大
        //（太大就成了"跨半个屏幕也硬连"，等于凭空造图形）。
        float maxGap = MathF.Max(ChainGapMinLogical, ChainGapRatio * total / parts.Count);

        // 从最长那一笔开始接：起点选"最像主体"的一笔，比从随手第一笔开始稳。
        var used = new bool[parts.Count];
        var chain = new List<Vector2>(parts[startAt]);
        // 每一点属于第几笔（给 $P 用的：它的 Resample 靠这个**不在笔画之间连出不存在的线**）
        var ids = new List<int>();
        foreach (var _ in parts[startAt]) ids.Add(1);
        int nextId = 2;
        void AppendTail(IReadOnlyList<Vector2> seg, bool flip)
        {
            if (flip) { for (int k = seg.Count - 1; k >= 0; k--) { chain.Add(seg[k]); ids.Add(nextId); } }
            else { for (int k = 0; k < seg.Count; k++) { chain.Add(seg[k]); ids.Add(nextId); } }
            nextId++;
        }
        void PrependHead(IReadOnlyList<Vector2> seg, bool flip)
        {
            var front = new List<Vector2>(seg.Count);
            var frontIds = new List<int>(seg.Count);
            if (!flip) { for (int k = seg.Count - 1; k >= 0; k--) { front.Add(seg[k]); frontIds.Add(nextId); } }
            else { for (int k = 0; k < seg.Count; k++) { front.Add(seg[k]); frontIds.Add(nextId); } }
            front.AddRange(chain);
            frontIds.AddRange(ids);
            chain = front;
            ids = frontIds;
            nextId++;
        }
        used[startAt] = true;
        float headGap = 0f;

        // 贪心接链：每一步找"离当前链任意一端最近"的那一笔，够近就接上（必要时掉个头）。
        for (int step = 1; step < parts.Count; step++)
        {
            int best = -1; bool bestAtTail = true, bestFlip = false;
            float bestGap = float.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                if (used[i] || parts[i].Count == 0) continue;
                var a = parts[i][0];
                var b = parts[i][^1];
                float dTailA = Vector2.Distance(chain[^1], a);
                float dTailB = Vector2.Distance(chain[^1], b);
                float dHeadA = Vector2.Distance(chain[0], a);
                float dHeadB = Vector2.Distance(chain[0], b);
                if (dTailA < bestGap) { bestGap = dTailA; best = i; bestAtTail = true; bestFlip = false; }
                if (dTailB < bestGap) { bestGap = dTailB; best = i; bestAtTail = true; bestFlip = true; }
                if (dHeadA < bestGap) { bestGap = dHeadA; best = i; bestAtTail = false; bestFlip = false; }
                if (dHeadB < bestGap) { bestGap = dHeadB; best = i; bestAtTail = false; bestFlip = true; }
            }
            if (best < 0) break;
            if (bestGap > maxGap)
                return ShapeGuess.None($"第 {step + 1} 笔离得太远（缝 {bestGap:F0} > {maxGap:F0}）——不硬接");

            used[best] = true;
            var seg = parts[best];
            if (bestAtTail) AppendTail(seg, bestFlip);
            else { PrependHead(seg, bestFlip); headGap = bestGap; }
            _ = headGap;
        }

        return RecognizeCore(chain, scale, ids);
    }

    /// <summary>
    /// **把一条线吸到水平 / 垂直**（定点不动，只动另一头），夹角小于容差才吸。
    ///
    /// 为什么识别器里要有它：老师画坐标轴、画分割线，要的就是"正"的
    /// （InkClass 专门为这件事留了 `LineAssistSnapDeg = 4.0`，注释原话
    /// "定型时角度吸附：接近水平/垂直吸正（画坐标轴刚需）"）。
    /// 它是**纯函数**，所以自检可以直接卡边界（3.9° 吸、4.1° 不吸）。
    /// </summary>
    internal static (Vector2 a, Vector2 b, float angleDeg) SnapToAxis(
        Vector2 a, Vector2 b, float tolDeg = 4f)
    {
        float dx = MathF.Abs(b.X - a.X), dy = MathF.Abs(b.Y - a.Y);
        float angle = MathF.Atan2(dy, dx) * 180f / MathF.PI;      // 0~90
        if (angle < tolDeg) return (a, new Vector2(b.X, a.Y), angle);              // 吸水平
        if (angle > 90f - tolDeg) return (a, new Vector2(a.X, b.Y), angle);        // 吸垂直
        return (a, b, angle);
    }

    // =====================================================================
    //  主判据。**顺序是有讲究的**，每条都说明为什么排在这儿。
    // =====================================================================

    private static ShapeGuess RecognizeCore(IReadOnlyList<Vector2> raw, float scale, IReadOnlyList<int> strokeIds)
    {
        var pts = Resample(Clean(raw));
        float total = PathLength(pts);
        if (pts.Count < 3 || total < MinLength * scale)
            return ShapeGuess.None($"太短（{total / scale:F0} < {MinLength:F0} 逻辑像素）");

        float gap = Vector2.Distance(pts[0], pts[^1]);
        var reasons = new List<string>();

        // ① **直线最先判**：它是唯一一条"开放形状"的判据，而且最严
        //    （所有点到一条直线的最大偏离 ≤ 12% 弦长，且没绕远路）。画得极扁的闭合形状
        //    也会落在这里，那正是我们想要的——它本来就是一条线。
        //    ⚠ 这条**不走 $P**：它是最常用的一条（"停顿变直线"就是它），而且它需要一个
        //      $P 给不出的严格性——"带钩的笔画 / 浅弧"必须被挡掉（判据是"绕路比"）。
        //      InkClass 也是这么分的：直线走它自己的 `ShouldStraightenLine`，
        //      其余图形才交给识别器（见它的 `MW_SimulatePressure&InkToShape.cs`）。
        var line = TryLine(pts, total, gap);
        if (!line.IsNothing) return line;
        reasons.Add("直线:" + line.Rule);

        // ② **$P 点云识别**决定"先按哪个图形去定形"。
        //
        // 为什么把分类交给 $P（参考官方实现，见 PointCloudRecognizer.cs 的文件头）：
        //    原来是自己"数角点 + 判直角/平行"——那是**单点判据**，手画的圆角、过冲、
        //    波浪边随便一样就能把它带偏（用户 2026-09-23 反馈的"矩形识别形变太大、
        //    完全和墨迹对不上"就是它）。$P 把整条笔迹当**无序点云**和模板比，
        //    对上面那些毛病天生免疫，而且它是学术界公开、被大量项目用过的东西。
        //    **$P 只负责"像哪个"**，"长什么样"仍然由下面的拟合给、由验收那一关把关。
        var ranked = PointCloudRec.Recognize(ToPcPoints(raw, strokeIds), 4);
        if (ranked.Count == 0) return ShapeGuess.None("点云识别没有候选（笔迹太短或退化）");
        foreach (var r in ranked)
        {
            var g = FitByLabel(r.Name, pts, scale);
            if (!g.IsNothing) return g;
            reasons.Add($"{r.Name}(距 {r.Distance:F2})：{g.Rule}");
        }

        // ②.5 **单笔不走"是不是双曲线"这条判断** —— 用户 2026-09-25 定的约定：
        //
        // > "**画两支就是双曲线，画一支就是抛物线**。不管它实际上是抛物线还是双曲线，
        // >  我们只要按照这个来区分。"
        //
        // 所以族的选择**由笔数定**（两笔那条路见 `TryTwoBranchHyperbola`），
        // **单笔一律不出双曲线** —— 走到这里只有"直线 / 抛物线"两种可能。
        //
        // ⚠ 这里原先挂着一个"用两端切线夹角分流"的圆锥曲线拟合（`TryFitConic(pts, scale)`），
        //   **2026-09-25 拆掉了**。拆的理由：夹角是个**连续、有噪声**的形状判据，
        //   实测两族在 95°~113° 有**硬重叠**（`b/a < 1` 的双曲线和抛物线角度区间本来就同一个），
        //   换任何阈值都绕不过去；而且它**违反新约定**（单笔抛物线有 15% 被它判成双曲线 ✗）。
        //   拟合器本身没删 —— 它是"两笔"那条路在用的（`familyKnown: true` 跳过夹角闸）。

        // ②.6 **二次函数（四种开口）** —— 计划 §43.4.4（一）。
        //
        // 为什么排在这里（$P 那一圈**之后**）：
        //   · 它**不许抢现有六种的活** —— $P 认得出的（直线 / 圆 / 椭圆 / 三角 / 矩形 / 平四）
        //     前面就已经返回了，走到这里说明六种都不像；
        //   · 前面两道闸门顺手也挡住了"抛物线被半路截走"：`TryLine` 很严（偏离 ≤ 弦长 12%），
        //     真抛物线过不去；圆 / 椭圆的拟合都要求**首末点靠近**（"像围起来的"），
        //     开放的一笔过不去。
        var para = TryFitParabola(pts, scale);
        if (!para.IsNothing) return para;
        reasons.Add("二次函数:" + para.Rule);

        // ②.7 **正弦 / 余弦** —— 计划 §43.4.4（三）。**排最后**，两个理由：
        //   · 它是这一族里**参数最多**的一个（4 个：偏移 / 两个系数 / 频率），
        //     而"多一个参数必须换来成倍的改善"那条规矩要求它**排在更少承诺的候选之后**
        //     （抛物线只有 3 个参数，前面刚试过、没贴住才会走到这里）；
        //   · 它是**唯一要搜频率**的一族 —— 最贵，也该最后做（计划 §43.4.8 那张顺序表）。
        var wave = TryFitWave(pts, scale);
        if (!wave.IsNothing) return wave;
        reasons.Add("正余弦:" + wave.Rule);

        // ③ 所有候选都贴不住 → **什么都不认**（宁可不变，也不能变出一个对不上的）。
        return ShapeGuess.None(string.Join("；", reasons));
    }

    /// <summary>
    /// 把识别出来的**图形名**变成"几何 + 验收"。
    ///
    /// 三条规矩：
    ///   · **同名只算一次**（$P 那边已经把同名模板并成一条了，见 `PointCloudRec.Recognize`）；
    ///   · **四边形族里"矩形优先"**：矩形是更具体的那一个（一个四角都是直角的平行四边形
    ///     就是矩形），所以两个标签都先当矩形试、贴不住才当平行四边形——
    ///     否则手画的矩形被 $P 排成"平行四边形"时，用户会看到自己的框变成了平行四边形；
    ///   · 每个拟合内部都带**验收**（贴不住就返回 None，由外层的下一个候选接着试）。
    /// </summary>
    private static ShapeGuess FitByLabel(string name, IReadOnlyList<Vector2> p, float scale)
    {
        switch (name)
        {
            case PointCloudRec.NameCircle:
                return TryCircle(p, scale);
            case PointCloudRec.NameEllipse:
            {
                var el = TryEllipse(p, scale);
                if (el.IsNothing) return el;
                // **拟合说它是圆的，那它就是圆。**
                // $P 会把"收口差一点的圈"排成椭圆——因为它的归一化是**按外接框缩放**的
                // （官方 `Scale`：除以 max(宽, 高)），缺口那一侧框小了，圈就被拉扁成椭圆的样子。
                // 这和 §42.6.1 里那个病根同源，只不过那一关已经在拟合里治好了：
                // 椭圆拟合给出的是**真的轴比**，轴比接近 1 就说明它是圈。
                //
                // ★★ **2026-09-26：不再"另跑一次圆拟合"**（用户报"圆老是识别不出来"）。
                //   原来这一支是 `TryCircle(p, scale)`，失败就退回椭圆 —— 而**真手画的圆**
                //   （径向被压 3~8%）**圆拟合经常过不了**（残差闸 9% ＋ 手抖 ＋ 缺口把圆心推偏），
                //   于是退回椭圆 ✗。实测：40 条手画圆里 **19 条**就是这么变成椭圆的
                //   （`--inktest` 里"圆·手画"那一族，52.5%）。
                //   现在改成**直接用椭圆拟合自己的结果造一个圆**：轴比 <
                //   `EllipseAxisRatioMin`（1.20）本来就等价于"离圆最远 ~9%"
                //   —— 那正是 `CircleResidualRatio` 那个量级，**两条闸在这里是同一件事**，
                //   再卡一次只是把已经认下的东西退回去。
                //   ⚠ 覆盖度（`MinArcCoverageDeg`）不用再查：`TryEllipse` 刚刚查过同一条。
                if (EllipseAxisRatioOf(el) < EllipseAxisRatioMin)
                {
                    float ra = MathF.Abs(el.Def[1].X - el.Def[0].X);
                    float rb = MathF.Abs(el.Def[1].Y - el.Def[0].Y);
                    float rr = (ra + rb) * 0.5f;                       // 长短轴的均值当半径
                    var cc = el.Def[0];
                    return ShapeGuess.Hit(StrokeKind.Circle, el.Score,
                                          $"圆（轴比 {EllipseAxisRatioOf(el):F2} 够圆，"
                                          + $"由椭圆拟合定半径 {rr:F0}）", cc, cc + new Vector2(rr, 0f));
                }
                return el;
            }
            case PointCloudRec.NameLine:
                return TryFitLine(p, scale);
            case PointCloudRec.NameRectangle:
            case PointCloudRec.NameParallelogram:
            {
                var rect = TryFitRectangle(p, scale);
                if (!rect.IsNothing) return rect;
                var para = TryFitParallelogram(p, scale);
                if (!para.IsNothing) return para;
                return ShapeGuess.None($"{rect.Rule}；{para.Rule}");
            }
            case PointCloudRec.NameTriangle:
                return TryFitTriangle(p, scale);
            default:
                return ShapeGuess.None($"没有 {name} 的拟合");
        }
    }

    /// <summary>给 $P 用的点云：**原始采样点**（$P 自己会重采样），带笔画编号。</summary>
    private static List<PcPoint> ToPcPoints(IReadOnlyList<Vector2> raw, IReadOnlyList<int> strokeIds)
    {
        var list = new List<PcPoint>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            int id = strokeIds != null && i < strokeIds.Count ? strokeIds[i] : 1;
            list.Add(new PcPoint(raw[i].X, raw[i].Y, id));
        }
        return list;
    }

    /// <summary>直线：主轴（最小二乘）拟合 + 两道闸（最大垂距 ÷ 弦长、总长 ÷ 弦长）。
    /// 端点取**所有点在主轴上的投影极值**，不是"首点/末点"——手画线起收笔常有回勾，
    /// 用首末点会让直线短一截或者歪一点。</summary>
    private static ShapeGuess TryLine(IReadOnlyList<Vector2> p, float total, float chord)
    {
        // 弦长太短 = 首末点几乎重合 = 这是个**围起来的形状**（矩形 / 圆 / 三角形……），
        // 不是直线。这一条比什么判据都干脆。
        if (chord < MinLength) return ShapeGuess.None($"首末点离得太近（{chord:F0}），像围起来的");

        // ★ **两端切线夹角大的，不许当直线**（用户 2026-09-25 定的约定，见
        //   `EndTangentAngleDeg` / `ConicAngleThresholdDeg`）。
        //
        //   为什么必须加在这一道闸上：**双曲线支比抛物线更"像直线"** ——
        //   它越往外越平、就是贴向渐近线（这正是用户的原话："双曲线就是越来越接近于直线"）。
        //   于是它比抛物线更容易过"够不够直"那一关，被这里**半路收走**。
        //   实测（`--inktest`）：40 条双曲线里 **14 条**就是死在这里（判成了直线），
        //   而这 14 条的两端夹角**全在 104° 以上**，本来就是"该判弧"的那一类。
        //
        //   ⚠ **不会误伤真直线**：手画的直线（哪怕起收笔带回勾）两端主方向几乎一致，
        //     夹角只有几度 —— 离 104° 远得很。这条闸只有"末端明显没攒住"的弧才中。
        float endAng = EndTangentAngleDeg(p);
        if (endAng >= ConicAngleThresholdDeg)
            return ShapeGuess.None($"两端夹角 {endAng:F0}°（末端没攒住 → 是弧，不是直线）");

        if (!FitLine(p, out var mean, out var dir, out float maxDev)) return ShapeGuess.None("拟合失败");
        float devRatio = maxDev / chord;
        if (devRatio > LineDevRatio)
            return ShapeGuess.None($"不够直（偏离 {devRatio * 100f:F1}%）");

        float excess = total / chord;
        if (excess > LineArcExcessRatio)
            return ShapeGuess.None($"绕远了（总长是弦长的 {excess:F2} 倍），像带钩的笔画或圆弧");

        float minT = float.MaxValue, maxT = float.MinValue;
        foreach (var q in p)
        {
            float t = (q.X - mean.X) * dir.X + (q.Y - mean.Y) * dir.Y;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }
        var a = mean + dir * minT;
        var b = mean + dir * maxT;
        if (Vector2.Distance(a, b) < MinLength) return ShapeGuess.None("投影后太短");

        float score = 1f - 0.5f * (devRatio / LineDevRatio);
        return ShapeGuess.Hit(StrokeKind.Line, score,
                              $"直线（偏离 {devRatio * 100f:F1}%，绕路 {excess:F2}）", a, b);
    }

    /// <summary>
    /// 圆：代数最小二乘（Kasa）拟合 + 残差 + **画够了多少角度**。
    ///
    /// 为什么还要查角度覆盖：一段半圆弧的残差同样很小（它就是整圆的一部分），
    /// 但我们的圆对象**永远画整圈**——把半圆吸成整圆是"自作主张"里最讨厌的一种。
    /// </summary>
    private static ShapeGuess TryCircle(IReadOnlyList<Vector2> p, float scale)
    {
        if (!FitCircle(p, out var c, out float r, out float err) || r < scale)
            return ShapeGuess.None("圆拟合失败");
        if (err > CircleResidualRatio) return ShapeGuess.None($"不像圆（残差 {err * 100f:F1}%）");

        float cover = AngularCoverageDeg(p, c);
        if (cover < MinArcCoverageDeg) return ShapeGuess.None($"只画了 {cover:F0}° 的弧");

        float score = 1f - 0.5f * (err / CircleResidualRatio);
        return ShapeGuess.Hit(StrokeKind.Circle, score, $"圆（残差 {err * 100f:F1}%，{cover:F0}°）",
                              c, c + new Vector2(r, 0f));
    }

    /// <summary>
    /// 椭圆（**带姿态角**）：**正规的椭圆最小二乘拟合**（把二次曲线
    /// `Ax²+Bxy+Cy²+Dx+Ey+F=0` 直接解出来），圆心、两个半轴、姿态角都是**一次拟合的结果**。
    ///
    /// 为什么换掉了原来那套"按外接框各向异性归一化 + 拟合圆"的做法（2026-09-23 用户上手反馈）：
    /// 那个做法的两个半轴**是从外接框里推出来的**，于是"墨迹没画满"（收口差一大截）的时候，
    /// 外接框在缺口那一侧小一块 → 推出来的轴就不对 → 表现就是用户说的两句话：
    ///   · "**圆**也是（和墨迹对不上）"——一个收口差一点的圈被框拉扁，判成了椭圆；
    ///   · "**椭圆**有时候识别不了"——本身是椭圆的，框一偏心，残差就过不去。
    /// 换成"整条曲线一起最小二乘"之后，**缺口只影响拟合的权重，不会让轴偏心**。
    ///
    /// 姿态角也顺带解决了：原来要另走一趟 PCA 估主轴（还得加个"够不够扁"的闸门挡噪声），
    /// 现在 θ 就是拟合结果里的一个量。收口规矩不变：**小歪当正着，大歪保留**。
    /// </summary>
    private static ShapeGuess TryEllipse(IReadOnlyList<Vector2> p, float scale)
    {
        if (!FitEllipseConic(p, out var ce, out float a, out float b, out float tiltScreen, out float err,
                             out string why))
            return ShapeGuess.None("椭圆拟合失败：" + why);
        if (a < scale || b < scale) return ShapeGuess.None("轴太短");
        if (err > EllipseResidualRatio) return ShapeGuess.None($"不像椭圆（残差 {err * 100f:F1}%）");

        // 覆盖度在**椭圆自己的坐标系**里量（两个半轴各自归一化成圆之后，角度才均匀）
        var flat = RotateAbout(p, ce, -tiltScreen);
        float cover = AngularCoverageDeg(flat, ce, a, b);
        if (cover < MinArcCoverageDeg) return ShapeGuess.None($"只画了 {cover:F0}° 的弧");

        // 最长的那根轴对应"外角点"的哪个方向：我们模型存的是"中心 + 外角点"，
        // 所以 a 必须是**中心到外角点**的横向半轴、b 是纵向半轴，
        // 而姿态角负责把它摆到用户画的那个方向上。
        float ratio = MathF.Max(a, b) / MathF.Max(1e-3f, MathF.Min(a, b));
        float score = 1f - 0.5f * (err / EllipseResidualRatio);
        bool tilted = MathF.Abs(tiltScreen) > TiltSnapDeg;
        float rotDeg = tilted ? -tiltScreen : 0f;      // 屏幕角度 → 用户看到的"逆时针为正"
        string note = tilted ? $"，姿态 {rotDeg:F0}°" : "";
        string rule = $"椭圆 {a * 2:F0}×{b * 2:F0}{note}（轴比 {ratio:F2}，残差 {err * 100f:F1}%）";

        var p0 = ce;
        var p1 = ce + new Vector2(a, b);              // 定义元素：中心 + 外角点
        return tilted
            ? ShapeGuess.HitRot(StrokeKind.Ellipse, score, rule, rotDeg, ce, p0, p1)
            : ShapeGuess.Hit(StrokeKind.Ellipse, score, rule, p0, p1);
    }

    /// <summary>
    /// **直线**（$P 说是直线、但上面那条严格判据没放过的兜底）。
    ///
    /// 严格判据（<see cref="TryLine"/>）管的是"绕路比"——带钩的笔画、明显的浅弧它都挡。
    /// 走到这里说明"$P 觉得像直线，但严格判据觉得绕远了"，那就用**同一把尺子**再量一次：
    /// 墨迹到这条线段的距离 ≤ 弦长的 12%（和严格判据同一个数），过了就认。
    /// ⚠ 这一条**不能放宽**：`竖弯钩`那种笔画（一根竖笔带个小钩）就是靠这一关和停顿那道闸门
    /// 一起拦住的（见 计划-图形工具.md §42.6 里"汉字笔画"那一档）。
    /// </summary>
    private static ShapeGuess TryFitLine(IReadOnlyList<Vector2> p, float scale)
    {
        if (p.Count < 2) return ShapeGuess.None("点太少");
        var a = p[0];
        var b = p[^1];
        float chord = Vector2.Distance(a, b);
        if (chord < MinLength * scale) return ShapeGuess.None("首末点太近");

        float worst = 0f;
        foreach (var q in p) worst = MathF.Max(worst, PointLineDistance(q, a, b));
        float ratio = worst / chord;
        if (ratio > LineDevRatio)
            return ShapeGuess.None($"不够直（偏离 {ratio * 100f:F1}% > {LineDevRatio * 100f:F0}%）");

        return ShapeGuess.Hit(StrokeKind.Line, 1f - 0.5f * (ratio / LineDevRatio),
                              $"直线（$P 候选，偏离 {ratio * 100f:F1}%）", a, b);
    }

    /// <summary>
    /// **矩形**：几何来自"最贴墨迹的外接框"（凸包取候选方向 + 分位数边界 + 贴住度当目标函数），
    /// 最后用**墨迹到四条边线的垂距**验收。
    ///
    /// ⚠ 它**不看角点**：手画的矩形角上有圆角、有起笔过冲、边还带波浪，角点很容易
    /// 多点一个或少点一个——那是"单点判据"的老毛病（用户 2026-09-23 反馈的
    /// "矩形识别形变太大、完全和墨迹对不上"正是它）。外接框是**整条墨迹一起算的**。
    /// 圆 / 椭圆不会误撞这一档：它们的墨迹离外接框的角很远（约半径的 30%），验收直接挡掉；
    /// 而且现在**"是不是矩形"由 $P 说了算**，这一档只在 $P 报了矩形/平行四边形时才跑。
    /// </summary>
    private static ShapeGuess TryFitRectangle(IReadOnlyList<Vector2> src, float scale)
    {
        if (src.Count < 4) return ShapeGuess.None("点太少");
        float tol = FitTol(scale, src);
        var hull = ConvexHull(src);
        BestFitBox(src, hull, out float boxTilt, out float bw, out float bh, out var boxCenter);
        bool boxTilted = MathF.Abs(boxTilt) > TiltSnapDeg;
        var ca = new Vector2(boxCenter.X - bw * 0.5f, boxCenter.Y - bh * 0.5f);
        var cb = new Vector2(boxCenter.X + bw * 0.5f, boxCenter.Y + bh * 0.5f);
        var quad = RectQuad(ca, cb, boxTilted ? -boxTilt : 0f, boxCenter);
        if (!InkNearBoxLines(src, quad, tol, out float dR))
            return ShapeGuess.None($"矩形框贴不住（最远 {dR:F0} > 容差 {tol:F0}）");
        if (!InkCoversEveryEdge(src, quad, tol, out float coverR))
            return ShapeGuess.None($"矩形框有一条边没画：最差那条边只盖住 {coverR * 100f:F0}%"
                                   + "（要 ≥60%；收笔差一截 或 框对歪了，都落在这条上）");

        string rule = boxTilted
            ? $"矩形 {bw:F0}×{bh:F0}，姿态 {-boxTilt:F0}°（最远差 {dR:F0}）"
            : $"矩形 {bw:F0}×{bh:F0}（最远差 {dR:F0}）";
        return boxTilted
            ? ShapeGuess.HitRot(StrokeKind.Rectangle, 0.9f, rule, -boxTilt, boxCenter, ca, cb)
            : ShapeGuess.Hit(StrokeKind.Rectangle, 0.9f, rule, ca, cb);
    }

    /// <summary>
    /// **三角形**：取"转角最强的 3 个角点"当三个顶点（手画三角形常常在角上画过头，
    /// 角点会数出 4~5 个，所以**取最强的那 3 个**而不是"数量必须正好 3"），
    /// 再验收到三条边的距离。
    /// </summary>
    private static ShapeGuess TryFitTriangle(IReadOnlyList<Vector2> src, float scale)
    {
        if (!TryPickCorners(src, 3, out var tri, out string why))
            return ShapeGuess.None("三角形：" + why);
        float tol = FitTol(scale, src);
        if (!InkNearOutline(src, tri, tol, out float d))
            return ShapeGuess.None($"三条边贴不住墨迹（最远 {d:F0} > 容差 {tol:F0}）");
        if (!InkCoversEveryEdge(src, tri, tol, out float coverT))
            return ShapeGuess.None($"三角形有一条边没画：最差那条边只盖住 {coverT * 100f:F0}%");
        return ShapeGuess.Hit(StrokeKind.Triangle, 0.9f, $"三角形（最远差 {d:F0}）", tri[0], tri[1], tri[2]);
    }

    /// <summary>
    /// **平行四边形**：取转角最强的 4 个角点，用其中三个当顶点（第四个由模型自己推，
    /// 见 <see cref="StrokeKind.Parallelogram"/>：`第2 + 第3 − 第1`），再验收。
    /// </summary>
    private static ShapeGuess TryFitParallelogram(IReadOnlyList<Vector2> src, float scale)
    {
        if (!TryPickCorners(src, 4, out var quad, out string why))
            return ShapeGuess.None("平行四边形：" + why);
        float tol = FitTol(scale, src);
        var paraQuad = new[] { quad[0], quad[1], quad[1] + quad[3] - quad[0], quad[3] };
        if (!InkNearOutline(src, paraQuad, tol, out float d))
            return ShapeGuess.None($"四条边贴不住墨迹（最远 {d:F0} > 容差 {tol:F0}）");
        if (!InkCoversEveryEdge(src, paraQuad, tol, out float coverP))
            return ShapeGuess.None($"平行四边形有一条边没画：最差那条边只盖住 {coverP * 100f:F0}%");
        return ShapeGuess.Hit(StrokeKind.Parallelogram, 0.9f, $"平行四边形（最远差 {d:F0}）",
                              quad[0], quad[1], quad[3]);
    }

    // =====================================================================
    //  二次函数（四种开口）—— 计划 §43.4.4（一），用户 2026-09-24 定的第一族
    // =====================================================================

    /// <summary>二次拟合的最坏偏差要比**直线**拟合小多少倍，才准它当抛物线
    /// （见 <see cref="TryFitParabola"/> 门槛 ①）。**量出来再定**：`--inktest` 的
    /// 抛物线正例 ×（汉字 / 字母 / 折线 / 直线的浅弧）反例一起过。</summary>
    private const float ParabolaGainOverLine = 4f;

    /// <summary>顶点两侧**至少各有这么一段**（占横向半跨度的比例）才算"画了整支"
    /// （见 <see cref="TryFitParabola"/> 门槛 ③）。只画半支现在**不认** ——
    /// 模型画抛物线是绕顶点对称铺开的，半支会被凭空补出另一半。</summary>
    private const float ParabolaVertexMargin = 0.2f;

    /// <summary>**两端切线夹角的阈值**（度）：`≥ 它` 判**双曲线**，`< 它` 判**抛物线**。
    ///
    /// **这是用户 2026-09-25 定的那条"老师感觉"约定**，原话：
    /// "末端切线接近平行，那肯定是抛物线；切出一个**显著的夹角**（45°/60°/120° 这种），
    /// 那肯定就是双曲线。" —— 用法见 <see cref="EndTangentAngleDeg"/>。
    ///
    /// **阈值 90° 的依据（2026-09-25 第二次定）**：
    ///   ⚠ 夹角**不等于"开口"** —— 双曲线的两端夹角收敛到 **2·arctan(b/a)**：
    ///     `b/a = 0.5` → 53°、**`= 1` → 90°**、`= 1.7` → 119°。
    ///   而**课本上最标准的双曲线是 `x²/4 − y²/4 = 1`（a = b）→ 夹角正好 90°**。
    ///   第一版取 104° 是**被语料带偏**的：我那批语料开口造在 0.9~1.8（偏"开"）、
    ///   中位 123°，于是 104° 看着正合适 —— 但那**比课本更开**。
    ///   用户上手反馈"**大部分还是被判成抛物线**"就是这个原因：
    ///   **画得越标准（a = b）越会掉到门槛下面** ✗ 这是个设计错，不是调参问题。
    ///   放到 90°：课本那种标准双曲线正落在线上（`≥` 判双曲线）✓，
    ///   实测抛物线判对从 87.5% 略降到 ~85%（放给双曲线的那一侧，符合用户"松"的取向）。
    ///
    ///   （另：夹角**小于 90° 的双曲线**（b/a < 1，很扁的）用这一个特征**本来就分不开**，
    ///    它和抛物线在同一个角度区间 —— 这条是**硬边界**，要再提升得加第二把尺子。）
    ///
    /// ⚠ **它不是数学判据，是"按老师感觉"的约定** —— 严格说两族的角度范围可以重叠
    /// （抛物线也能画得很平、双曲线也能画得很陡）。这条量的是"**老师真会画的那种**"在哪。
    /// 所以别拿它当"识别率 100%"来读，它是"约 85~90%"。
    ///
    /// ⚠ 它**替掉的是一版"两个模型比残差"的判据**：那一版在真实手抖下双曲线只剩 **40%**
    /// （噪声直接进残差的分子，比值被噪声主导）。夹角是**窗口里几十个点的方向**，
    /// 噪声在窗口内被平均掉 —— 所以稳得多。**换判据就是这一条的理由。**</summary>
    private const float ConicAngleThresholdDeg = 104f;

    /// <summary>双曲线的半轴下限（逻辑像素）：太小的话模型会缩成一个点。</summary>
    private const float ConicMinHalfAxis = 6f;

    // =====================================================================
    //  正弦 / 余弦（计划-图形工具.md §43.4.4 三）
    // =====================================================================

    /// <summary>**周期数的搜索区间**：`k = f·L`（`L` = 墨迹横向跨度、`f` = 频率）。
    ///
    /// ★ **2026-09-26 放宽成 [0.85, 12]**（原来只到 1.20）：多周期那条路（写回**波浪线**）
    /// 打开了，所以搜索要能覆盖"好几个周期"。
    ///
    /// **为什么放宽不怕倍频错误**（当初把"多周期正弦"列进"不做"就是怕它）：
    ///   ① 验收那一关是"**对象会画的那条曲线贴不贴得住墨迹**" —— 倍频解（钻到 2f、3f）
    ///      在**多周期**墨迹上残差极大（模型只有一个"周期数"参数，套不上别的周期数）；
    ///   ② 更根本的：**多周期数据本身让频率良态**（病态的是"不到一个周期"那一头，
    ///      见 `WaveMinCycles`）。当初的担心来自短弧，不是长弧。
    ///   ③ 粗网格步长（≈ 0.07 个周期）**细于周期图主瓣宽度**（≈ 1 个周期），
    ///      细化窗口又只有 ±1 步 —— 所以细化**不可能跳到隔壁那个瓣上去**。
    /// 区间取得比验收门槛宽：**拟合要能找到真频率**，才轮得到"该不该认"那两道闸去判。</summary>
    private const float WaveCyclesSearchMin = 0.85f;
    private const float WaveCyclesSearchMax = 12f;

    /// <summary>
    /// **至少要"将近一个周期"才认**（用户 2026-09-26 定："我打算正弦余弦**最起码一个周期
    /// 起步**才能识别"）。
    ///
    /// 取 **0.90** 而不是 1.00：老师"画一个周期"时**收笔早一点**很常见，而那**不丢信息**
    /// —— 模型那一个周期本来就会比墨迹**长出去一点**（长出去没关系，见 `TryFitWave` 的注释）。
    /// 卡死 1.00 的手感是"我明明画了一个周期它不认"。
    ///
    /// ⚠ **试过放宽到 0.75，量出代价后放回来了**（2026-09-26 第二次，用户："正余弦波浪线
    ///   也把它放松一点，因为这种图形一般比较少画"——他说得有理，但实测不行）：
    ///   0.75 时 **单支双曲线有 11/40 被认成 Cosine** ✗（而且那一档原来误报是 **0**）。
    ///   原因很直白：**这一族参数最多（频率 / 相位 / 振幅 / 中线），是最"全能"的模型** ——
    ///   门槛一低，它就会去覆盖别的族的笔画（一支双曲线看着就像一段余弦）。
    ///   一句话：**"出现得少"不等于"可以松"**，因为松的是**判据**，被抢的是**别的族**。
    ///   它自己的识别率本来就 95~100%，没有量出卡的地方。
    ///
    /// ⚠ 这一条为什么不能没有：只画半截的墨迹本身确实**躺在**某个完整正弦的曲线上，
    ///   没有门槛的话"半个拱"会被认出来、然后被补成整个周期 —— 那不是用户要的。</summary>
    private const float WaveMinCycles = 0.90f;

    /// <summary>频率搜索的网格与细化：三段（粗扫 → 细化 → 再细化）。
    /// 分辨率 = 区间宽 ÷ (160×32×32)，远远够用（验收那一关是"曲线贴不贴得住墨迹"，
    /// 周期差一点点都会顶出来，所以频率必须搜准）。
    /// 粗扫 **160 步**是为了让步长（≈0.07 个周期）细于周期图主瓣（≈1 个周期）—— 见
    /// <see cref="WaveCyclesSearchMax"/> 那条注释第 ③ 点。</summary>
    private const int WaveFreqSteps = 160;
    private const int WaveFreqRefine = 32;
    private const int WaveFreqPasses = 3;

    /// <summary>
    /// **明显多个周期才算波浪线**（周期数下限）。
    ///
    /// 分流口径（和 `FitByLabel` 里"四边形族里**矩形优先**"同一条规矩：**更具体的先认**）：
    ///   · 约一个周期 → **正弦 / 余弦**（课本上那一格，就是用户要的那个对象）——
    ///     它**先试**，贴得住就是它；
    ///   · 贴不住（起笔相位偏了 / 画出了一个多周期）→ 让**波浪线**接（它的长度是自由的，
    ///     能把"起点挪到零点"那一点偏移补回来，所以宽容得多）；
    ///   · 但波浪线只在**k ≥ 1.25** 时才接：把"只有 1.1 个周期"的墨迹塞进波浪线，
    ///     会得到一个"既不像课本的正弦、也不是多周期波"的对象 —— 那一档**不认**，
    ///     墨迹原样留着（用户 2026-09-26 定的"不改模型、表达不了就用判据挡"）。
    /// </summary>
    private const float WaveMultiMinCycles = 1.25f;

    /// <summary>**单值检验**允许的回退比例（相对横向跨度）：模型**横向一律往右画**
    /// （见 `Model.WavePointAt` 的注释），所以这一笔在 x 上来回走就不是它的函数图象。
    /// 5% 是给"手抖 + 收笔过冲"留的量。</summary>
    private const float WaveMonoSlack = 0.05f;

    /// <summary>
    /// **二次函数（四种开口）**。规格见 计划-图形工具.md §43.4.4（一）。
    ///
    /// **两条支路各试一次，谁过验收用谁** —— 这就同时回答了"上下开口还是左右开口"：
    /// 按 `y = f(x)` 拟合成了就是上下开口、按 `x = f(y)` 成了就是左右开口。
    /// **不需要另写"单值检验"**：开口方向那一维天生是函数、另一维天生不是，所以错的那一支
    /// 残差必然大，自己就被验收挡掉（§43.4.3 第 1 步）。
    ///
    /// **写回现有 `Parabola` 对象**（§43.4.6 坑 4：不新造类型、不新写一份几何）。
    /// 它的定义元素是"**顶点 + 曲线经过的那个点**"（见 `Model.ParabolaPointAt`），
    /// 形状参数 `p` 由经过点反解。本函数把拟合结果换算成这两点：
    ///   · 顶点 `v` = 拟合抛物线的顶点；
    ///   · **`p = 1/(2|a|)`** —— 模型那条参数化是 `P(t) = v + perp·(t·p) + dir·(t²·p/2)`，
    ///     和手写的 `沿轴 = a·横跨²` 对一下就是它；
    ///   · 经过点 `q = v + perp·T + dir·a·T²`（`T` = 墨迹的横向半跨度）——
    ///     于是模型"画到经过点那儿"正好铺满用户画的那一段。
    ///
    /// **两道门槛**（缺一条就会把直线 / 浅弧 / 汉字笔画认成抛物线）：
    ///   ① **多拟合一个二次项，必须换来成倍的改善**：二次拟合的最坏偏差要比**直线**拟合的
    ///      最坏偏差小 <see cref="ParabolaGainOverLine"/> 倍以上。这条是"它到底是不是二次
    ///      曲线"的**不变量** —— 浅弧、直线、圆的一小段都过不了。而且它**只看数字、不看形状**，
    ///      所以对"只画右半支"这种课上很常见的半条抛物线同样成立。
    ///      （试过"矢高 ÷ 弦长"那条判据：只画半边时弦很斜、矢高很小，会把半条抛物线判死。）
    ///      ⚠ 这和 §43.4.6 坑 3"R² 不能跟更高次比"**不矛盾**：那条说的是"别让复杂度自己赢"；
    ///      这里正相反 —— **要求多出来的那个参数换来成倍的改善**，才准它赢。
    ///   ② `p ≥ ParabolaMinP`：别认出一个张口窄到缩成一条线的抛物线（模型自己的下限）。
    ///
    /// 过了门槛再走**同一把验收尺子**（§43 的 `CurveFit.MaxDeviationToPolyline` + `FitTol`）：
    /// 贴不住就不认 —— 宁可不变，也不能变出一条对不上的曲线。
    /// </summary>
    private static ShapeGuess TryFitParabola(IReadOnlyList<Vector2> src, float scale)
    {
        if (src.Count < 8) return ShapeGuess.None("二次函数：点太少");
        float tol = FitTol(scale, src);
        int n = src.Count;

        ShapeGuess best = ShapeGuess.None("二次函数：两条支路都没贴住");
        string why = "";
        float bestWorst = float.MaxValue;

        for (int dim = 0; dim < 2; dim++)      // dim 0：按 y=f(x)（上下开口）；dim 1：按 x=f(y)（左右开口）
        {
            var u = new double[n];
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                u[i] = dim == 0 ? src[i].X : src[i].Y;
                w[i] = dim == 0 ? src[i].Y : src[i].X;
            }

            if (!TryFitPoly(u, w, 2, out var c2, out double residQuad, out double um, out double us)) continue;
            if (!TryFitPoly(u, w, 1, out _, out double residLine, out _, out _)) continue;

            // 门槛 ①：二次项得真的换来改善（否则它本来就更像一条直线）
            double gain = residQuad > 1e-6 ? residLine / residQuad : double.MaxValue;
            if (gain < ParabolaGainOverLine)
            {
                why = $"弯得不够（二次只比直线好 {gain:F1} 倍 < {ParabolaGainOverLine:F0}）";
                continue;
            }
            if (residQuad > tol) { why = $"贴不住（最远 {residQuad:F0} > 容差 {tol:F0}）"; continue; }

            // 归一化变量 û = (u − um)/us 下的系数 → 换回原尺度：w = A·u² + B·u + C
            double A = c2[2] / (us * us);
            if (Math.Abs(A) < 1e-12) { why = "二次项退化成零"; continue; }

            // 顶点（先在 û 里求，再换回原尺度）
            double uv = um - us * c2[1] / (2.0 * c2[2]);
            double wv = c2[0] - c2[1] * c2[1] / (4.0 * c2[2]);

            // 开口方向：dim 0 时 +w 是画布 +y（向下）；dim 1 时 +w 是画布 +x（向右）
            CurveAxis axis = dim == 0
                ? (A > 0 ? CurveAxis.OpenDown : CurveAxis.OpenUp)
                : (A > 0 ? CurveAxis.OpenRight : CurveAxis.OpenLeft);

            var v = dim == 0 ? new Vector2((float)uv, (float)wv)
                             : new Vector2((float)wv, (float)uv);

            // 门槛 ②：张口下限（模型里 p 太小整条曲线会缩成一个点）
            float p = (float)(1.0 / (2.0 * Math.Abs(A)));
            if (p < Stroke.ParabolaMinP)
            {
                why = $"张口太窄（p = {p:F1} < {Stroke.ParabolaMinP:F0}）";
                continue;
            }

            // 横向半跨度 T + **门槛 ③：顶点必须落在墨迹横向跨度的内部**
            //
            // 两条理由：
            //   ① 模型画抛物线是**绕顶点左右对称铺开**的（见 `Model.ParabolaSpanOf`：
            //      "左右（或上下）对称地铺开"），所以只有"**顶点两侧都画了**"的墨迹才能被它
            //      如实还原。只画半支（课上很常见的画法）会**凭空补出另一半** ——
            //      那和 §43.4.4（二）里双曲线"补出另一支"是**同一个决策点**，
            //      先不做，等用户定（现在只画半支就退回保形平滑，墨迹不会凭空长东西）。
            //   ② 它顺带把"浅弧 / 圆的一小段"挡得更死：那些墨迹拟合出来的顶点通常落在
            //      墨迹外很远（`sMin`、`sMax` 同号）。
            var (dir, perp) = Stroke.ParabolaBasis(axis);
            float sMin = float.MaxValue, sMax = float.MinValue;
            foreach (var pt in src)
            {
                float s = Vector2.Dot(pt - v, perp);
                sMin = MathF.Min(sMin, s); sMax = MathF.Max(sMax, s);
            }
            float T = MathF.Max(MathF.Abs(sMin), MathF.Abs(sMax));
            if (T < 1e-3f) { why = "横向没有跨度"; continue; }
            if (sMin > -ParabolaVertexMargin * T || sMax < ParabolaVertexMargin * T)
            {
                why = $"顶点不在墨迹中间（横向 {sMin:F0}..{sMax:F0}）——只画半支的先不认";
                continue;
            }

            // ⚠ 沿轴偏移要取 **|A|**：`dir` 本身已经带符号了（它就是"开口方向"），
            //    再用带符号的 `A·T²` 等于把符号算两遍 —— 曲线会画到顶点的**反面**去
            //（2026-09-25 实测：偏差 220 ≈ 2×238，正是这个错）。
            var q = v + perp * T + dir * (MathF.Abs((float)A) * T * T);

            // 验收：把曲线采成折线，量"墨迹到折线的最大距离"（和 §43 保形平滑**同一把尺子**）
            // ⚠ 采样范围要铺到**墨迹的横向跨度**（`u ∈ [−T/p, T/p]`），**不能写死 [−1,1]**：
            //    参数 `u = 1` 在横跨方向上只走到 `p`，而 `T` 常常远大于 `p`
            //    —— 写死 [−1,1] 只盖住中间那一段，两端各差出去一截
            //（2026-09-25 实测：左右开口那批偏差 110 ≈ T − p，正是这个错）。
            float uMax = T / p;
            const int samples = 64;
            var poly = new Vector2[samples + 1];
            for (int i = 0; i <= samples; i++)
                poly[i] = Stroke.ParabolaPointAt(v, q, axis, -uMax + 2f * uMax * i / samples);
            float worst = CurveFit.MaxDeviationToPolyline(src, poly);
            if (worst > tol) { why = $"曲线贴不住墨迹（最远 {worst:F0} > 容差 {tol:F0}）"; continue; }

            if (worst < bestWorst)
            {
                bestWorst = worst;
                float score = 0.9f * MathF.Min(1f, (float)(gain / (ParabolaGainOverLine * 3f)));
                string side = dim == 0
                    ? (A > 0 ? "下" : "上")
                    : (A > 0 ? "右" : "左");
                best = ShapeGuess.HitAxis(StrokeKind.Parabola, score,
                                          $"抛物线（开口向{side}，p={p:F0}，最远差 {worst:F0}，二次比直线好 {gain:F1} 倍）",
                                          axis, v, q);
            }
        }

        return best.IsNothing ? ShapeGuess.None(why.Length > 0 ? why : best.Rule) : best;
    }

    /// <summary>
    /// **正弦 / 余弦**（计划-图形工具.md §43.4.4 三）。
    ///
    /// ## 公式从哪来（参考，不是自己推的）
    ///
    /// 固定频率的三系数**谐波回归**：`y = β₀ + β₁·cos(2πfx) + β₂·sin(2πfx)`。
    /// **固定 `f` 时它对 β 是线性的**（正规方程 3×3 闭式解），所以只需要在 `f` 上做
    /// **一维搜索** —— 这就是"变量投影 / 浓缩代价函数"那一套（Handel 在 IEEE T-IM 2000
    /// 上比过：**数据只盖住一个周期的一小部分时，一维浓缩搜索比 IEEE-1057 那套四参数迭代更稳**），
    /// 也是统计课上的标准课件做法（Berkeley Stat 153：对 `f` 建网格、每个 `f` 做一次 OLS、
    /// 取 RSS 最小的那个）。振幅 `A = √(β₁²+β₂²)`、相位由 `atan2` 给。
    /// **不需要 Levenberg–Marquardt。**
    ///
    /// ## 为什么"至少要一个周期"是**技术上的必要条件**（不只是用户的手感要求）
    ///
    /// 少于一个周期时，`cos` 和 `sin` 两列在采样点上**近似共线** → 正规方程病态，
    /// `A`、相位全是噪声（这正是上面那篇论文说的"只盖住一小部分周期"那一档）。
    /// 计划 §43.4.4（三）当初也写着"至少覆盖一个完整峰谷"。
    ///
    /// ## 验收：**先把对象会画的那条曲线建出来，再量墨迹贴不贴得住它**
    ///
    /// 这是本仓库的验收规矩（"自己不能给自己打分"+"宁可不变，也不能变出一条对不上的"）。
    /// 模型曲线**直接调对象自己那条算式**（`Stroke.WaveYAt`，和渲染 / 轮廓折线同一份），
    /// 采成折线之后用 `CurveFit.MaxDeviationToPolyline` 量 —— 于是"识别出来的正弦"
    /// 和"用图形工具拖出来的正弦"**同一条几何**（§42.9 第 3 条：不许再抄一份几何）。
    ///
    /// 这一条尺子顺手把三件事一起管了（所以**不需要**另写判据）：
    ///   · **形状 + 周期 + 振幅**对不对；
    ///   · **起点相位**：模型的正弦起点**只能落在零点上**、余弦只能落在**极值点**上
    ///     （见 `Model.WaveYAt`）—— 老师从任意相位起笔时，模型必须在这些点上落笔，
    ///     "落错相位"的代价会**当场变成墨迹贴不住曲线**；
    ///   · **"多画出去的那一截"**：模型只有**一个周期**，墨迹要是画了一个半周期，
    ///     多出来的那半截必然离曲线很远 → 直接不认（用户 2026-09-26 拍板：**不改模型**，
    ///     遇到模型表达不了的画法就用判据挡，保持手绘）。
    ///
    /// ## 写回
    ///
    /// `Def = [起点, 终点]`，口径和图形工具那两格**完全一样**（见 `Model.SetWaveBox`）：
    /// 终点 = `(起点.x + 周期, 起点.y + dy)`，`|dy|` = 正弦的振幅（余弦是 2 倍，因为
    /// `(1−cos)/2` 的峰谷差是 2A），`dy` 的符号 = "先往上还是先往下"。
    /// 直线 / 抛物线用的"姿态角""曲线朝向"这两位**正余弦都不需要**（它没有旋转、没有开口方向）。
    /// </summary>
    private static ShapeGuess TryFitWave(IReadOnlyList<Vector2> src, float scale)
    {
        int n = src.Count;
        if (n < 8) return ShapeGuess.None("正余弦：点太少");

        // ── 单值检验（§43.4.3 第 1 步）+ "横向一律往右画"那条模型语义 ──────────
        float xMin = float.MaxValue, xMax = float.MinValue;
        foreach (var q in src) { xMin = MathF.Min(xMin, q.X); xMax = MathF.Max(xMax, q.X); }
        float L = xMax - xMin;
        if (L < 1e-3f) return ShapeGuess.None("正余弦：横向没有跨度");
        float run = src[0].X, back = 0f;
        for (int i = 0; i < n; i++)
        {
            run = MathF.Max(run, src[i].X);
            back = MathF.Max(back, run - src[i].X);
        }
        if (back > WaveMonoSlack * L)
            return ShapeGuess.None($"正余弦：这一笔在横向来回走（回退 {back:F0}）——不是函数图象");

        float tol = FitTol(scale, src);

        // ── 频率搜索：横坐标写成"周期数 k = f·L"，在窄带里三段细化 ──────────────
        double kLo = WaveCyclesSearchMin, kHi = WaveCyclesSearchMax, step = 0, kBest = 0;
        double bestSq = double.MaxValue;
        for (int pass = 0; pass < WaveFreqPasses; pass++)
        {
            int steps = pass == 0 ? WaveFreqSteps : WaveFreqRefine;
            step = (kHi - kLo) / steps;
            for (int i = 0; i <= steps; i++)
            {
                double k = kLo + step * i;
                if (!TryHarmonic(src, k / L, out _, out double sq, out _)) continue;
                if (sq < bestSq) { bestSq = sq; kBest = k; }
            }
            if (bestSq == double.MaxValue) return ShapeGuess.None("正余弦：拟合退化（方程奇异）");
            kLo = kBest - step; kHi = kBest + step;      // 下一轮在这个小区间里细化
        }

        double f = kBest / L, T = L / kBest;
        if (!TryHarmonic(src, f, out var beta, out _, out double maxResid))
            return ShapeGuess.None("正余弦：拟合退化（方程奇异）");

        double amp = Math.Sqrt(beta[1] * beta[1] + beta[2] * beta[2]);
        if (amp < 1e-3) return ShapeGuess.None("正余弦：振幅退化成零");
        float c0 = (float)beta[0];

        // `y = c0 + A·sin(2πf·x + ψ)`，其中 `β₁ = A·sinψ`、`β₂ = A·cosψ`
        // → 相位零点（`y = c0` 且斜率大于零的那个）在 `xZero`，其后每隔 `T` 一个。
        double xZero = -Math.Atan2(beta[1], beta[2]) / Math.Tau / f;

        // ── 六族"落笔方式"，每种再按周期平移找最贴的一个 ──────────────────────
        //
        // 模型那两条算式（`Model.WaveYAt`）决定了起点**只能**在下面这些点上：
        //   · 正弦 / **波浪线**：`y = c0` 的**零点**上（斜率朝下的那个 dy<0、朝上的 dy>0）；
        //   · 余弦：**极值点**上（从"屏幕上那个峰"起 dy>0、从"谷"起 dy<0）。
        // 于是六族 = 正弦·升 / 正弦·降 / 余弦·峰起 / 余弦·谷起 / 波浪线·升 / 波浪线·降。
        //
        // **波浪线那两族的长度是自由的**（它的定义元素就是"起点 ＋ 终点"，而且终点
        // 是"要画多长"、周期另外存一个量）—— 所以起点被挪到零点之后，把长度补到
        // 墨迹的右端就够了：**曲线从头到尾和墨迹完全重合**，多出来的只是起点左边
        // 那一小截 δ（见下面 `frameW`）。这也正是它比正弦 / 余弦宽容得多的原因。
        var best = ShapeGuess.None("正余弦：模型那条曲线贴不住墨迹");
        float bestWorst = float.MaxValue, bestT = 0f, bestA = 0f;
        float bestOverhang = float.MaxValue;
        // **每族最好的那一条**（只给"没认出来"时的判据字符串用）：不认的时候
        // 光有一句"贴不住"没法判断是谁贴不住、差多少 —— 六族的数字摆出来，一眼就知道
        // 卡在"多画出去的那一截"还是"起笔相位"。这是本仓库一直用的排查抓手
        //（见 `RecoProbe` 里"错例要能一眼看出被哪条规矩拦下的"）。
        var famWorst = new float[6];
        var famCycles = new float[6];
        for (int i = 0; i < 6; i++) famWorst[i] = float.MaxValue;

        float y0Base = c0;
        // **波浪线这一族只在"墨迹真的画了多个周期"时才参与**（`allowWave`）。
        // 为什么不交给后面的闸：一条**一个周期**的墨迹，波浪线那条路**照样能贴到 0**
        //（起点挪到零点、长度往左补出半个周期，就变成"2 个周期"的对象了）——
        // 于是它会**抢走正弦 / 余弦**，最后被闸拦掉，**连本来合格的 Sin/Cos 一起丢**。
        // 实测：不设这一条时 正弦 97.5% → 22.5% ✗。
        bool allowWave = kBest >= WaveMultiMinCycles;

        for (int fam = 0; fam < 6; fam++)
        {
            bool wave = fam >= 4;                                     // 4/5 = 波浪线（多周期）
            bool cos = fam >= 2 && !wave;                             // 2/3 = 余弦（一个周期）
            if (wave && !allowWave) continue;
            float dyMag = cos ? 2f * (float)amp : (float)amp;
            // 起点相位的基准：正弦 / 波浪线用零点；余弦用极值点（峰在 xZero − T/4）。
            double xRef = (fam % 2) == 0 ? xZero : xZero + T * 0.5;   // 升 / 降 两族
            float dy = (fam % 2) == 0 ? dyMag : -dyMag;               // 先下 / 先上
            if (cos)
            {
                // 余弦：family 2 = 起点在屏幕上的**峰**（画布 y 小）→ dy > 0 往谷走；
                //       family 3 = 起点在**谷** → dy < 0。
                bool fromCrest = fam == 2;
                xRef = fromCrest ? xZero - T * 0.25 : xZero + T * 0.25;
                dy = fromCrest ? dyMag : -dyMag;
            }
            float y0 = cos ? c0 - dy * 0.5f : y0Base;

            // **这一族的候选起点**（起点.x ＋ 框宽，两个数就定下这一条曲线）：
            //
            //   · 正弦 / 余弦：长度**固定 = 一个周期**，所以左右各试一个，看谁盖得住墨迹；
            //   · **波浪线：只试两个"端点方案"**（长度自由，中间那些只是白多画一截）：
            //       **A 对齐墨迹左端** —— 起点就放在墨迹开始的地方，框宽 = 墨迹跨度。
            //         长度和墨迹**一模一样**，代价是"起笔相位偏多少，曲线就偏多少"。
            //       **B 对齐零点** —— 起点挪到左边最近的零点（相位零误差），
            //         代价是**往左多伸出一小截 δ**（≤ 半个周期）。
            //     A 先试：它在**不改变对象占的地方**的前提下尽量贴住；A 贴不住才退到 B。
            var places = new List<(float sx, float fw)>(3);
            if (wave)
            {
                float crossX = (float)(xRef + Math.Floor((xMin - xRef) / T) * T);   // 左边最近的零点
                places.Add((xMin, L));                          // A
                places.Add((crossX, xMax - crossX));            // B
            }
            else
            {
                int j0 = (int)Math.Round((xMin - xRef) / T);
                for (int d = 0; d < 3; d++)
                    places.Add(((float)(xRef + (j0 + (d == 0 ? 0 : d == 1 ? -1 : 1)) * T), (float)T));
            }

            foreach (var (sx, fw) in places)
            {
                var start = new Vector2(sx, y0);
                var end = new Vector2(sx + fw, y0 + dy);
                var kind = wave ? StrokeKind.Wave : (cos ? StrokeKind.Cosine : StrokeKind.Sine);
                // `uFrom`：起点落在墨迹左端之外时，把多出来的那一截剪掉（见 `WavePolyline`）。
                float uFrom = (float)((xMin - sx) / T);
                var poly = WavePolyline(start, end, kind, (float)T, uFrom);
                float worst = CurveFit.MaxDeviationToPolyline(src, poly);
                if (worst < famWorst[fam]) { famWorst[fam] = worst; famCycles[fam] = fw / (float)T; }
                // 往左多伸出去多少：越少越好（它不改形状，只改"对象比墨迹宽多少"）。
                float overhang = MathF.Max(0f, xMin - sx);
                // 排序键，按重要性从先到后：① **贴不贴得住**（进容差的一律优于进不去的）；
                //   ② **往左多伸出去多少**（都贴得住时，占地方最准的那个赢）；
                //   ③ 偏差本身。
                // ⚠ 次序不能反：先比偏差的话，"对齐零点"那条（偏差 0、但多伸半格的）
                //   会打败"对齐墨迹左端"（偏差一点、但一格不多）—— 实测就是这么
                //   写回包围盒差出 27.7% 的。
                int fit = worst <= tol ? 0 : 1;
                int bestFit = bestWorst <= tol ? 0 : 1;
                bool better = fit < bestFit
                           || (fit == bestFit && overhang < bestOverhang - 1e-3f)
                           || (fit == bestFit && MathF.Abs(overhang - bestOverhang) <= 1e-3f
                               && worst < bestWorst - 1e-3f);
                if (better)
                {
                    bestWorst = worst; bestOverhang = overhang;
                    bestT = (float)T; bestA = (float)amp;
                    string name = wave ? "波浪线" : (cos ? "余弦" : "正弦");
                    // 波浪线的第三个定义元素 = **一个周期的宽度**（见 `Stroke.SetWavePeriod`）。
                    best = wave
                        ? ShapeGuess.Hit(StrokeKind.Wave,
                                         MathF.Max(0.5f, 1f - 0.5f * worst / tol),
                                         $"{name}（{fw / (float)T:F2} 个周期，周期 {T:F0}、振幅 {amp:F0}，"
                                         + $"最远差 {worst:F0}，谐波回归残差 {maxResid:F1}）",
                                         start, end, start + new Vector2((float)T, 0f))
                        : ShapeGuess.Hit(kind,
                                         MathF.Max(0.5f, 1f - 0.5f * worst / tol),
                                         $"{name}（{kBest:F2} 个周期，周期 {T:F0}、振幅 {amp:F0}，"
                                         + $"最远差 {worst:F0}，谐波回归残差 {maxResid:F1}）",
                                         start, end);
                }
            }
        }

        // ── 两道闸：先"至少一个周期"，再"贴得住" ─────────────────────────────
        if (kBest < WaveMinCycles)
            return ShapeGuess.None($"正余弦：只画了 {kBest:F2} 个周期（< {WaveMinCycles:F2}）"
                                   + "——至少要一个完整周期才认");
        if (bestWorst > tol)
            return ShapeGuess.None($"正余弦：模型那条曲线贴不住墨迹（最远 {bestWorst:F0} > "
                                   + $"容差 {tol:F0}；{bestT:F0} 宽 / 振幅 {bestA:F0}）"
                                   + "——多画出去的那一截、或起笔相位对不上，都落在这一条上"
                                   + FamilyDiagnostics(famWorst, famCycles));
        return best;
    }

    /// <summary>六族候选各自的"最远差 / 周期数"，拼成一段给人看的排查字符串
    /// （只在**没认出来**时进判据，认出来了不花这个钱）。</summary>
    private static string FamilyDiagnostics(float[] famWorst, float[] famCycles)
    {
        string[] names = { "正弦·升", "正弦·降", "余弦·峰", "余弦·谷", "波浪·升", "波浪·降" };
        var sb = new System.Text.StringBuilder("【各族最远差/周期数：");
        for (int i = 0; i < famWorst.Length; i++)
        {
            if (i > 0) sb.Append('、');
            sb.Append(names[i]).Append(' ')
              .Append(famWorst[i] == float.MaxValue ? "未试" : $"{famWorst[i]:F0}/{famCycles[i]:F2}");
        }
        return sb.Append('】').ToString();
    }

    /// <summary>把模型那条曲线采成折线 —— **调的就是对象自己的算式**（`Stroke.WaveYAt`），
    /// 不重抄一份几何（§42.9 第 3 条）。
    ///
    /// 采样范围 `u ∈ [uFrom, 周期数]`（`周期数 = 框宽 ÷ 周期`），**和 `Stroke.WavePointAt` 一致**：
    ///   · 正弦 / 余弦：框宽 = 一个周期 → `u ∈ [0,1]`；
    ///   · 波浪线：框宽 = 要画多长、周期是第三个定义元素 → `u` 可以走好几个周期。
    ///
    /// ⚠ **`uFrom` 这一刀不能省**（2026-09-26 实测踩到的）：起点落在零点上 ⇒ 模型会
    /// **从墨迹左端之外**开始（最多一个周期）。而 `CurveFit.MaxDeviationToPolyline`
    /// 是**两条单调点串的滑窗比对**，它默认"第一个点对第一段" —— 模型左边多出来那一截
    /// 会让最近段的下标被打到窗口右端、然后**一路追不上**，量出来的偏差是**假的**
    ///（实测：真正贴合的波浪线被报成"最远差 38"）。所以这里**把左端多出来的那一段剪掉**：
    /// 从墨迹左端对应的相位开始采 —— 墨迹仍然全在采样范围里，量的还是"墨迹贴不贴得住曲线"。
    /// </summary>
    private static Vector2[] WavePolyline(Vector2 start, Vector2 end, StrokeKind kind,
                                          float period, float uFrom)
    {
        float width = MathF.Abs(end.X - start.X);
        float cycles = period > 1e-3f ? width / period : 1f;
        if (uFrom < 0f) uFrom = 0f;
        if (uFrom > cycles) uFrom = cycles;
        float span = cycles - uFrom;
        // 段数**按周期数给**（每个周期 64 段）：折线弦高误差 ≈ (2π/64)²/8×振幅 ≈ 0.2% 振幅，
        // 远小于容差；写死 128 段的话 12 个周期只剩每周期 10 段，误差会盖过容差本身。
        int seg = Math.Clamp((int)MathF.Ceiling(64f * span), 16, 1024);
        var poly = new Vector2[seg + 1];
        for (int i = 0; i <= seg; i++)
        {
            float u = uFrom + span * i / (float)seg;
            poly[i] = new Vector2(start.X + period * u, Stroke.WaveYAt(start, end, kind, u));
        }
        return poly;
    }

    /// <summary>
    /// **固定频率的三系数谐波回归**：`y = β₀ + β₁·cos(2πfx) + β₂·sin(2πfx)`，
    /// 正规方程 3×3 闭式解（对 β 线性，所以只需要搜 `f` 一个维度）。
    /// 返回 false = 方程奇异（采样点退化 / 横向没有跨度）。
    ///
    /// 同时给出两个残差：`sq`（平方和，**搜索 `f` 用它** —— 它就是最小二乘的目标函数）
    /// 和 `maxResid`（最大竖直残差，**只进诊断字符串**）。**验收不看这两个** ——
    /// 验收走几何距离（`MaxDeviationToPolyline`），和抛物线 / 双曲线同一把尺子。
    /// </summary>
    private static bool TryHarmonic(IReadOnlyList<Vector2> src, double f,
                                    out double[] beta, out double sq, out double maxResid)
    {
        beta = new double[3];
        sq = double.MaxValue; maxResid = double.MaxValue;
        if (f <= 0) return false;

        var m = new double[3, 3];
        var rhs = new double[3];
        var col = new double[3];
        for (int i = 0; i < src.Count; i++)
        {
            double ph = Math.Tau * f * src[i].X;
            col[0] = 1.0; col[1] = Math.Cos(ph); col[2] = Math.Sin(ph);
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) m[r, c] += col[r] * col[c];
                rhs[r] += col[r] * src[i].Y;
            }
        }
        if (!SolveSmall(m, rhs, beta)) return false;

        double ss = 0, worst = 0;
        for (int i = 0; i < src.Count; i++)
        {
            double ph = Math.Tau * f * src[i].X;
            double fit = beta[0] + beta[1] * Math.Cos(ph) + beta[2] * Math.Sin(ph);
            double d = src[i].Y - fit;
            ss += d * d;
            worst = Math.Max(worst, Math.Abs(d));
        }
        sq = ss; maxResid = worst;
        return true;
    }

    /// <summary>
    /// 归一化变量下的**最小二乘多项式拟合**：`û = (u − ū)/s ∈ [−1,1]`（`s` = 离均值最远的距离），
    /// 解出 `w = Σ coef[k]·û^k`，并给出**最坏残差**。
    ///
    /// **为什么必须归一化**（§43.4.6 坑 1 那条，只是那里说的是双曲线）：`u` 是画布坐标、上千，
    /// 不归一化的话正规方程里 `u⁴` 到 10¹³，解出来全是数值噪声。
    /// 返回 false = 退化（自变量没有跨度 / 方程奇异），调用方换下一支。
    /// </summary>
    private static bool TryFitPoly(double[] u, double[] w, int order,
                                   out double[] coef, out double maxResid,
                                   out double mean, out double span)
    {
        int k = order + 1;
        coef = new double[k];
        maxResid = double.MaxValue;
        mean = 0; span = 0;

        int n = u.Length;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += u[i];
        mean = sum / n;
        double s = 0;
        for (int i = 0; i < n; i++) s = Math.Max(s, Math.Abs(u[i] - mean));
        if (s < 1e-6) return false;
        span = s;

        // 正规方程 (VᵀV)·coef = Vᵀw，V 是 [1, û, û², …]
        var m = new double[k, k];
        var rhs = new double[k];
        for (int i = 0; i < n; i++)
        {
            double x = (u[i] - mean) / s;
            var pw = new double[2 * order + 1];
            pw[0] = 1;
            for (int e = 1; e <= 2 * order; e++) pw[e] = pw[e - 1] * x;
            for (int r = 0; r < k; r++)
            {
                for (int c = 0; c < k; c++) m[r, c] += pw[r + c];
                rhs[r] += pw[r] * w[i];
            }
        }
        if (!SolveSmall(m, rhs, coef)) return false;

        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (u[i] - mean) / s;
            double fit = 0, xp = 1;
            for (int e = 0; e < k; e++) { fit += coef[e] * xp; xp *= x; }
            worst = Math.Max(worst, Math.Abs(w[i] - fit));
        }
        maxResid = worst;
        return true;
    }

    /// <summary>小规模稠密线性方程组（≤ 3×3）：列主元高斯消元。奇异时返回 false。</summary>
    private static bool SolveSmall(double[,] m, double[] rhs, double[] x)
    {
        int k = x.Length;
        var a = new double[k, k];
        Array.Copy(m, a, m.Length);
        var b = (double[])rhs.Clone();

        for (int c = 0; c < k; c++)
        {
            int piv = c;
            for (int r = c + 1; r < k; r++)
                if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-12) return false;
            if (piv != c)
            {
                for (int j = 0; j < k; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
                (b[c], b[piv]) = (b[piv], b[c]);
            }
            for (int r = c + 1; r < k; r++)
            {
                double f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int j = c; j < k; j++) a[r, j] -= f * a[c, j];
                b[r] -= f * b[c];
            }
        }
        for (int r = k - 1; r >= 0; r--)
        {
            double acc = b[r];
            for (int j = r + 1; j < k; j++) acc -= a[r, j] * x[j];
            x[r] = acc / a[r, r];
        }
        return true;
    }

    /// <summary>
    /// 挑出转角最强的 n 个角点（**保持原来的环绕顺序**，因为定义元素的顺序对渲染有意义）。
    ///
    /// ⚠ **先旋转、再找角，后面一切都读旋转后的那个数组**：
    ///   `FindCorners` 返回的是下标，而这些下标是对着"它吃进去的那个数组"的。
    ///   第一版就是这里栽的——角点在下标上是按旋转后的数组算的，回头却拿原数组去取点，
    ///   于是"每条边都不直"（4 场矩形全被判成"折线:边不直"）。
    /// </summary>
    private static bool TryPickCorners(IReadOnlyList<Vector2> src, int n,
                                       out Vector2[] picked, out string why)
    {
        picked = Array.Empty<Vector2>();
        var (p, closed) = LoopRotated(src);
        var corners = FindCorners(p, closed);
        if (corners.Count < n) { why = $"角点只有 {corners.Count} 个（要 {n} 个）"; return false; }

        // 角点不够直就别谈是折线图形（手抖的圆也能数出好几个角）
        if (!AllEdgesStraight(p, corners)) { why = "边不直"; return false; }

        // 按"转角强度"排序取前 n 个 → 再按**原来的环绕次序**摆回去
        var ranked = new List<int>(corners);
        ranked.Sort((i, j) => TurnStrength(p, closed, j).CompareTo(TurnStrength(p, closed, i)));
        var keep = ranked.GetRange(0, n);
        keep.Sort();
        // 环绕顺序：FindCorners 返回的就是环上顺序，排序后按下标升序即可（最后一个回到第一个）
        picked = new Vector2[n];
        for (int i = 0; i < n; i++) picked[i] = p[keep[i]];
        why = "";
        return true;
    }

    /// <summary>某个角点转得有多"急"（度，越接近 180 越不是角；用来挑最强的那几个）。</summary>
    private static float TurnStrength(IReadOnlyList<Vector2> arr, bool closed, int at)
    {
        int n = arr.Count;
        int step = 4;
        if (n < step * 2 + 1) return 0f;
        int prev = (at - step + n) % n;
        int next = (at + step) % n;
        if (!closed && (at < step || at > n - 1 - step)) return 0f;
        return 180f - InteriorAngleDeg(arr[prev], arr[at], arr[next]);
    }

    /// <summary>折线类的验收容差（画布单位）：按形状尺度给，另设一个绝对下限（小图形别被判死）。</summary>
    private static float FitTol(float scale, IReadOnlyList<Vector2> ink)
        => MathF.Max(FitTolFloorLogical * scale, FitTolRatio * Diagonal(ink));

    /// <summary>
    /// 矩形的四个角（按 <paramref name="rotDeg"/> 绕 <paramref name="pivot"/> 摆好）。
    /// `rotDeg` 是**用户口径**（逆时针为正），所以摆的时候用 `-rotDeg`（见 SelectionHandles.RotateMatrix）。
    /// </summary>
    private static Vector2[] RectQuad(Vector2 a, Vector2 b, float rotDeg, Vector2 pivot)
    {
        var mn = new Vector2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        var mx = new Vector2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        var q = new[]
        {
            new Vector2(mn.X, mn.Y), new Vector2(mx.X, mn.Y),
            new Vector2(mx.X, mx.Y), new Vector2(mn.X, mx.Y),
        };
        if (MathF.Abs(rotDeg) < 0.01f) return q;
        float rad = -rotDeg * MathF.PI / 180f;
        for (int i = 0; i < 4; i++) q[i] = RotateOne(q[i], pivot, rad);
        return q;
    }

    /// <summary>
    /// **验收**：墨迹上每个点到"理想轮廓"的距离都不许超过容差。
    ///
    /// 轮廓一律按**闭合多边形**处理（三角形、四边形、矩形都是）——
    /// ⚠ 第一版给四边形传了个 `loop=false` 去"跳过收口边"，结果是**四条边只查了三条**：
    /// 靠近那条没查的边的墨迹点，距离算出来是"到对面那条边"，值直接是**整个高**，
    /// 于是所有矩形/平行四边形都被判"贴不住"（`--inktest` 里三个矩形族全军覆没）。
    /// 闭合形状就是闭合的，**一个边都不能省**。
    ///
    /// ⚠ 只查"墨迹 → 轮廓"这一个方向（详见 <see cref="FitTolRatio"/> 那段注释）：
    /// 反过来查会把"用户没画完的那一段"当成错误，而补全收口正是这个功能的本来目的。
    /// </summary>
    private static bool InkNearOutline(IReadOnlyList<Vector2> ink, IReadOnlyList<Vector2> outline,
                                       float tol, out float worst)
    {
        worst = 0f;
        int n = outline.Count;
        if (n < 2) return false;
        for (int i = 0; i < ink.Count; i++)
        {
            float best = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                float d = PointSegmentDistance(ink[i], outline[k], outline[(k + 1) % n]);
                if (d < best) best = d;
                if (best <= tol) break;              // 已经够近，不用再算别的边
            }
            if (best > worst) worst = best;
            if (worst > tol) return false;
        }
        return true;
    }

    /// <summary>
    /// **矩形的验收**：墨迹到"四条边所在**直线**"的垂距（不是到线段）。
    ///
    /// 为什么和折线那一档（<see cref="InkNearOutline"/>）不同：这个框是**按墨迹的外接范围
    /// 造出来的**（凸包 ⇒ 长宽天然就对得上），唯一会错的是"框摆歪了 / 框太窄了"——
    /// 那正是垂距能抓的。
    /// 而"到线段"会把**起笔过冲**（画过角再拐，真手写天天有）当成不匹配：过冲那一段
    /// 落在线段之外、离最近那个角十几到三十几像素，`--inktest` 里"矩形·过冲"那一族
    /// 就因此被判成"贴不住"（40 条里只认出来 2 条）。
    /// </summary>
    private static bool InkNearBoxLines(IReadOnlyList<Vector2> ink, IReadOnlyList<Vector2> quad,
                                        float tol, out float worst)
    {
        worst = 0f;
        int n = quad.Count;
        if (n < 2) return false;
        for (int i = 0; i < ink.Count; i++)
        {
            float best = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                float d = PointLineDistance(ink[i], quad[k], quad[(k + 1) % n]);
                if (d < best) best = d;
                if (best <= tol) break;
            }
            if (best > worst) worst = best;
            if (worst > tol) return false;
        }
        return true;
    }

    /// <summary>
    /// **每一条边都得基本画出来**：把每条边按长度采样 20 份，要求**至少 60% 的采样点附近有墨**
    /// （同一个容差）。
    ///
    /// 为什么需要它（`--inktest` 抓到的假报）：只有"墨迹 → 轮廓"那一关时，
    /// 一个"V"字折线（一个拐角、两段）会被认成三角形——顶点是真的、墨迹也都在轮廓上，
    /// 只是**第三条边根本没画**。同理，"U"形会被补成矩形。
    ///
    /// ⚠ 为什么不是"端点附近有墨就算"（第一版就是这么写的，没挡住 V 形）：
    /// V 的两条臂在**收口边**的两端附近确实有墨，于是那条凭空多出来的边"看着有墨"。
    /// 所以判据要落到**整条边的长度**上：V 的收口边只有两头有墨（覆盖率约 20%），
    /// 而手画矩形收口差一截时每条边的覆盖率还有 80~95%。
    ///
    /// ★ **2026-09-26：加 `out worstCover`**（用户报"矩形常常什么都不认"）——
    /// 原来这条只回答"过 / 不过"，**不报"最差那条边盖住多少"**，于是真机上报的
    /// "有一条边没画"完全看不出是"收笔差一截"还是"框整个对歪了"。
    /// 这两个病因的修法完全不同（放宽收口容差 vs 修 `BestFitBox`），
    /// 所以数字必须摆出来 —— 这是本仓库那条"**判据要能一眼看出卡在哪**"的老规矩。
    /// </summary>
    private static bool InkCoversEveryEdge(IReadOnlyList<Vector2> ink, IReadOnlyList<Vector2> outline,
                                           float tol, out float worstCover)
    {
        const int Samples = 20;
        // ⚠ **门槛试过放宽到 0.45，又放回来了**（2026-09-26，用户报"椭圆和矩形两个都常常
        //   什么都不认"之后做的实验）：实测对**手画语料没有任何影响** ——
        //   因为"收笔差半条边"的那个缺口，在**角上那截墨**（离缺口端点不到一个容差）
        //   的帮衬下覆盖率本来就过了 60%（`--inktest` 里"矩形·大缺口"这条难例
        //   在 0.6 和 0.45 下都是 **40/40 认对**）。没有量出好处，就不动它
        //   —— 这道闸的本职是挡"V 形被补成三角形 / U 形被补成矩形"（那些覆盖率只有约 20%）。
        const float Enough = 0.6f;
        int n = outline.Count;
        worstCover = 1f;
        for (int k = 0; k < n; k++)
        {
            var a = outline[k];
            var b = outline[(k + 1) % n];
            int hit = 0;
            for (int s = 0; s < Samples; s++)
            {
                var q = Vector2.Lerp(a, b, (s + 0.5f) / Samples);
                foreach (var p in ink)
                {
                    if (Vector2.Distance(q, p) <= tol) { hit++; break; }
                }
            }
            float cover = hit / (float)Samples;
            if (cover < worstCover) worstCover = cover;
            if (hit < Samples * Enough) return false;
        }
        return true;
    }

    /// <summary>点到**线段**的距离（不是到直线：多边形外面那半截不算近）。</summary>
    private static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    // =====================================================================
    //  几何工具（每个都短小、可单独解释）
    // =====================================================================

    /// <summary>去掉挨得太近的重复点（原始采样里"笔停住"时会连出一串同一个点）。</summary>
    private static List<Vector2> Clean(IReadOnlyList<Vector2> raw)
    {
        var outp = new List<Vector2>(raw.Count);
        foreach (var q in raw)
        {
            if (outp.Count == 0 || Vector2.Distance(outp[^1], q) > 0.01f) outp.Add(q);
        }
        return outp;
    }

    /// <summary>等距重采样（$1 Recognizer 的做法）：把"密一阵疏一阵"的原始点
    /// 变成**相邻间距大致相等**的一串点。角点检测和拟合都要求这个前提，
    /// 否则"点密集的地方"会被算法当成"走得慢/更直"。</summary>
    private static List<Vector2> Resample(List<Vector2> pts)
    {
        if (pts.Count < 2) return pts;
        float total = PathLength(pts);
        if (total <= 0f) return pts;
        int want = Math.Clamp((int)(total / ResampleStep) + 1, ResampleMin, ResampleMax);
        float step = total / (want - 1);

        var outp = new List<Vector2>(want) { pts[0] };
        float acc = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            float d = Vector2.Distance(pts[i - 1], pts[i]);
            if (d <= 0f) continue;
            if (acc + d >= step)
            {
                float t = (step - acc) / d;
                var q = Vector2.Lerp(pts[i - 1], pts[i], t);
                outp.Add(q);
                pts.Insert(i, q);       // 插回原数组：下一次从这半个点之后继续量
                acc = 0f;
            }
            else acc += d;
        }
        while (outp.Count < want) outp.Add(pts[^1]);
        return outp;
    }

    /// <summary>一笔走过的**总路程**（相邻点距离之和，画布单位）。
    /// 识别器自己用它判"够不够长"；引擎那边判"这一下是点还是画"也用它
    /// （见 `Engine.DwellTapLeaveNoInk`）——**同一个量只写一处**。</summary>
    internal static float PathLength(IReadOnlyList<Vector2> p)
    {
        float s = 0f;
        for (int i = 1; i < p.Count; i++) s += Vector2.Distance(p[i - 1], p[i]);
        return s;
    }

    private static void Bounds(IReadOnlyList<Vector2> p, out Vector2 mn, out Vector2 mx)
    {
        mn = p[0]; mx = p[0];
        foreach (var q in p)
        {
            if (q.X < mn.X) mn.X = q.X;
            if (q.Y < mn.Y) mn.Y = q.Y;
            if (q.X > mx.X) mx.X = q.X;
            if (q.Y > mx.Y) mx.Y = q.Y;
        }
    }

    /// <summary>总的包围盒对角线（自检里当"尺度"用）。</summary>
    private static float Diagonal(IReadOnlyList<Vector2> p)
    {
        Bounds(p, out var mn, out var mx);
        return Vector2.Distance(mn, mx);
    }

    /// <summary>最小二乘（主成分）拟合直线：返回重心、单位方向、以及**最大垂距**。
    /// 方向取协方差矩阵的主特征向量 = `0.5·atan2(2Sxy, Sxx−Syy)`——
    /// 注意**不要**用"把 x 当自变量、y 当因变量"那条回归：竖着的线在那里会直接崩掉
    /// （斜率无穷），Sezgin 的手绘识别论文里专门提过这一点。</summary>
    private static bool FitLine(IReadOnlyList<Vector2> p, out Vector2 mean, out Vector2 dir, out float maxDev)
    {
        mean = Vector2.Zero; dir = new Vector2(1f, 0f); maxDev = 0f;
        if (p.Count < 2) return false;
        var m = Vector2.Zero;
        foreach (var q in p) m += q;
        m /= p.Count;

        float sxx = 0f, sxy = 0f, syy = 0f;
        foreach (var q in p)
        {
            float dx = q.X - m.X, dy = q.Y - m.Y;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        float theta = 0.5f * MathF.Atan2(2f * sxy, sxx - syy);
        dir = new Vector2(MathF.Cos(theta), MathF.Sin(theta));

        foreach (var q in p)
        {
            float dx = q.X - m.X, dy = q.Y - m.Y;
            float dev = MathF.Abs(dx * dir.Y - dy * dir.X);      // 二维叉积 = 到主轴的垂距
            if (dev > maxDev) maxDev = dev;
        }
        mean = m;
        return true;
    }

    /// <summary>代数最小二乘圆拟合（Kasa）：把 `x²+y²+Dx+Ey+F=0` 对 D/E/F 做最小二乘，
    /// 解一个 3×3 线性方程组（克拉默法则，不用引矩阵库）。
    /// 返回**相对残差** = max|点到圆心距离 − r| ÷ r。
    ///
    /// 为什么用 Kasa 而不是更讲究的 Taubin / Pratt：Kasa 在"点分布比较均匀的整圈"
    /// 上表现够好（我们的场景正是如此），而且三行公式谁都看得懂。
    /// 它的已知弱点是"短弧 + 噪声"时偏心——所以**我们先看角度覆盖**，
    /// 覆盖不够的（半圆弧那类）根本走不到残差这一步。</summary>
    private static bool FitCircle(IReadOnlyList<Vector2> p, out Vector2 center, out float r, out float err)
    {
        center = Vector2.Zero; r = 0f; err = 1f;
        if (p.Count < 3) return false;

        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxu = 0, syu = 0, su = 0;
        int n = p.Count;
        foreach (var q in p)
        {
            double x = q.X, y = q.Y, u = x * x + y * y;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            sxu += x * u; syu += y * u; su += u;
        }
        // 解： [sxx sxy sx][D]   [-sxu]
        //      [sxy syy sy][E] = [-syu]
        //      [sx  sy  n ][F]   [-su ]
        double a11 = sxx, a12 = sxy, a13 = sx;
        double a21 = sxy, a22 = syy, a23 = sy;
        double a31 = sx, a32 = sy, a33 = n;
        double det = Det3(a11, a12, a13, a21, a22, a23, a31, a32, a33);
        if (Math.Abs(det) < 1e-9) return false;

        double b1 = -sxu, b2 = -syu, b3 = -su;
        double d = Det3(b1, a12, a13, b2, a22, a23, b3, a32, a33) / det;
        double e = Det3(a11, b1, a13, a21, b2, a23, a31, b3, a33) / det;
        double f = Det3(a11, a12, b1, a21, a22, b2, a31, a32, b3) / det;

        double cx = -d * 0.5, cy = -e * 0.5;
        double rr = cx * cx + cy * cy - f;
        if (rr <= 0) return false;
        double rad = Math.Sqrt(rr);
        // ⚠ 这里**只**保证"半径是个正数"：半径下限由调用方按自己的尺度去卡
        //（圆那条要 ≥ 1 像素；椭圆那条是在**归一化空间**里拟合的，半径本来就 ≈ 1，
        // 拿"≥1 像素"去卡会把所有椭圆都判成"拟合失败"——第一次跑自检就栽在这上面）。
        if (double.IsNaN(rad) || double.IsInfinity(rad) || rad < 1e-3) return false;

        double worst = 0;
        foreach (var q in p)
        {
            double dist = Math.Sqrt((q.X - cx) * (q.X - cx) + (q.Y - cy) * (q.Y - cy));
            double dev = Math.Abs(dist - rad);
            if (dev > worst) worst = dev;
        }
        center = new Vector2((float)cx, (float)cy);
        r = (float)rad;
        err = (float)(worst / rad);
        return true;
    }

    private static double Det3(double a, double b, double c, double d, double e, double f, double g, double h, double i)
        => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

    /// <summary>
    /// 绕 <paramref name="c"/> 画到了多少角度（0~360）。
    ///
    /// 做法：把每个点相对圆心的**方位角**排序，找**最大的一个空档**，
    /// `360 − 最大空档` 就是"实际覆盖"的角度。比"首末点夹角"稳得多——
    /// 那样在闭合圆上会算出 0°（首末几乎同向）。
    ///
    /// `ax / ay` 是给椭圆用的**半轴**：椭圆上的点要**各自除以自己那个方向的半轴**
    /// 才能变成"角度均匀"的圆（`x/a`、`y/b`），否则四角处的角度分布会被拉歪。
    /// （只除其中一个方向是不对的——横扁和竖长两种椭圆的歪法正好相反。）
    /// </summary>
    private static float AngularCoverageDeg(IReadOnlyList<Vector2> p, Vector2 c, float ax = 1f, float ay = 1f)
    {
        float sx = MathF.Max(1e-4f, ax), sy = MathF.Max(1e-4f, ay);
        var angs = new List<float>(p.Count);
        foreach (var q in p)
        {
            float dx = (q.X - c.X) / sx, dy = (q.Y - c.Y) / sy;
            angs.Add(MathF.Atan2(dy, dx) * 180f / MathF.PI);
        }
        angs.Sort();
        float biggestGap = 0f;
        for (int i = 1; i < angs.Count; i++)
        {
            float g = angs[i] - angs[i - 1];
            if (g > biggestGap) biggestGap = g;
        }
        float wrap = angs[0] + 360f - angs[^1];
        if (wrap > biggestGap) biggestGap = wrap;
        return MathF.Max(0f, 360f - biggestGap);
    }

    /// <summary>
    /// **围起来的形状：把点列旋转半圈**，让"首尾接缝"落到数组中间去。
    ///
    /// 为什么必须转：整条笔迹的接缝（起笔点 = 收笔点）本来在最两端，
    /// 而"转角/稻草距离"这类算法在两端都要靠**邻居**，接缝那个角会因为邻居不全而量不准。
    /// 转半圈之后接缝在正中间，邻居齐了。
    ///
    /// 返回的第二个值 = **它到底围起来没有**：围起来的形状，下标要**环绕**取点
    ///（见 <see cref="FindCorners"/> 里的 `At`）——因为那个数组其实是个环，
    /// 没有"两端"。不围起来的（缺口 > 35% 周长）就原样返回一个副本。
    /// </summary>
    private static (List<Vector2> pts, bool closed) LoopRotated(IReadOnlyList<Vector2> p)
    {
        int n = p.Count;
        float total = PathLength(p);
        if (total <= 0f || n < 4) return (new List<Vector2>(p), false);
        if (Vector2.Distance(p[0], p[^1]) / total > LooseCloseGapRatio)
            return (new List<Vector2>(p), false);

        var rot = new List<Vector2>(n);
        int half = n / 2;
        for (int i = 0; i < n; i++) rot.Add(p[(i + half) % n]);
        return (rot, true);
    }

    /// <summary>
    /// 找角点：**转角法**。
    ///
    /// 对每个点取前后各 `k` 个点（16 px）连成两条弦，量它们**转了多少度**
    /// （0° = 笔直往前，90° = 直角转）。然后：
    ///   ① 三点平滑一下，压掉手抖造成的单点毛刺；
    ///   ② 取"在 ±k 邻域里最大"且转角 ≥ <see cref="CornerTurnMinDeg"/> 的点当角；
    ///   ③ 挨得太近的角合并（同一个角会被报两次）。
    ///
    /// 为什么不用 ShortStraw 的**稻草距离**（`|p[i−w]−p[i+w]|` 的局部极小，
    /// Wolin/Eoff/Hammond, SBIM 2008）：第一版就是它，`--inktest` 实测**不稳**——
    /// 400×132 的矩形只找到 **3** 个角、503×451 的找到 **7** 个（真角漏了、抖动又长出假角），
    /// 40 个矩形／平行四边形全军覆没。根子在稻草距离是**相对量**（"局部极小 vs 均值 × 0.95"），
    /// 手抖一多，均值自己先漂了；而**转角是绝对量（度）**，阈值好定、出错也好解释。
    ///
    /// ⚠ `closed` 决定**下标要不要环绕**，这一条也是踩出来的：
    ///   围起来的形状旋转半圈之后，有一个角正好落在数组**头尾**（旋转把某个角搬到了 0 号位），
    ///   而扫描循环为了照顾开放形状的两端跳过了前 k / 后 k 个点 —— 于是**每次都是同一个角漏掉**，
    ///   症状是"矩形永远只有 3 个角"（四个不同尺寸的矩形案例全都漏同一个角，这才定位到它）。
    ///   所以：围起来的形状从头扫到尾、邻居环绕取；开放的才跳过两端。
    /// </summary>
    private static List<int> FindCorners(IReadOnlyList<Vector2> arr, bool closed)
    {
        int n = arr.Count;
        const int k = 4;                       // 前后各 4 个重采样点 ≈ 16 px 的弦
        if (n < 2 * k + 3) return new List<int>();

        // 取点：围起来的下标环绕（它是个环），开放的夹在两端（没有"环上那个邻居"）
        Vector2 At(int i) => arr[closed ? ((i % n) + n) % n : Math.Clamp(i, 0, n - 1)];

        // ① 每个点的转向角
        var turn = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (!closed && (i < k || i >= n - k)) { turn[i] = 0f; continue; }   // 开放形状的两端量不了
            turn[i] = 180f - InteriorAngleDeg(At(i - k), arr[i], At(i + k));
        }
        // 平滑（三点平均）：真角是"连续几度都在转"，抖动是"单点尖峰"，平滑后前者留、后者平
        var sm = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = turn[i];
            int c = 1;
            if (closed || i > 0) { s += At2(turn, i - 1, n, closed); c++; }
            if (closed || i < n - 1) { s += At2(turn, i + 1, n, closed); c++; }
            sm[i] = s / c;
        }

        // ② 候选：在 ±k 邻域里最突出，且过了门槛
        var cand = new List<int>();
        int lo = closed ? 0 : k;
        int hi = closed ? n : n - k;
        for (int i = lo; i < hi; i++)
        {
            if (sm[i] < CornerTurnMinDeg) continue;
            bool biggest = true;
            for (int j = i - k; j <= i + k; j++)
            {
                int jj = closed ? ((j % n) + n) % n : Math.Clamp(j, 0, n - 1);
                if (sm[jj] > sm[i]) { biggest = false; break; }
            }
            if (biggest) cand.Add(i);
        }

        // ③ 合并挨得太近的（同一个角被报两次）：留转角更大的那个
        var outp = new List<int>(cand.Count);
        foreach (var c in cand)
        {
            if (outp.Count > 0 && c - outp[^1] <= 2 * k)
            {
                if (sm[c] > sm[outp[^1]]) outp[^1] = c;
            }
            else outp.Add(c);
        }
        // 环上的第一 / 最后一个也可能是同一个角（它们隔着"接缝"挨着）
        if (closed && outp.Count > 1 && outp[0] + n - outp[^1] <= 2 * k)
        {
            if (sm[outp[^1]] > sm[outp[0]]) outp[0] = outp[^1];
            outp.RemoveAt(outp.Count - 1);
        }
        return outp;
    }

    /// <summary>按"环 / 段"两种口径取一个数组元素（给平滑那一步用）。</summary>
    private static float At2(float[] a, int i, int n, bool closed)
        => a[closed ? ((i % n) + n) % n : Math.Clamp(i, 0, n - 1)];

    /// <summary>
    /// **椭圆最小二乘拟合**（正规做法）：把二次曲线
    /// `A x² + B xy + C y² + D x + E y + F = 0` 解出来，再从中读出中心、半轴、姿态角。
    ///
    /// 做法：先用**迹约束** `A + C = 1`（这保证解出来的是椭圆、不会退化成双曲线或直线，
    /// 而且把它代进去之后方程组是**线性**的，不用解广义特征值）：
    ///     A(x² − y²) + B xy + D x + E y + F = −y²
    /// 五个未知量 → 一个 5×5 正规方程组（用高斯消元解）。
    ///
    /// **残差**用"点到曲线的几何距离"的一阶近似：`|Q(p)| ÷ |∇Q(p)|`——
    /// 直接拿代数残差当误差是不行的（x 一大，x² 项的误差就被放大几十倍，
    /// 于是"大椭圆"永远判过关、"小椭圆"永远判不过）。
    ///
    /// 坐标系：先减去重心再拟合（x² 会到 10⁶ 量级，居心能让方程组的条件数降下来），
    /// double 精度下够用，不需要再做尺度归一化。
    /// </summary>
    private static bool FitEllipseConic(IReadOnlyList<Vector2> p, out Vector2 center,
                                        out float a, out float b, out float tiltScreen, out float err,
                                        out string why)
    {
        center = Vector2.Zero; a = b = 0f; tiltScreen = 0f; err = 1f; why = "";
        if (p.Count < 6) { why = "点太少"; return false; }

        var m = Mean(p);
        // **先居心、再缩放到"半径 ≈ 1"**：方程组里同时有 x²（上千）和常数 1，
        // 条件数能到 1e8 以上，不缩放的话 double 也解不准（第一版就是这么崩的：
        // 干净的正椭圆也一律"不成椭圆"）。缩放因子 s 事后按次数乘回去即可（见下）。
        double s = 0;
        foreach (var q in p) s += Vector2.Distance(q, m);
        s = Math.Max(1e-3, s / p.Count);
        var mat = new double[5, 5];
        var rhs = new double[5];
        void AddRow(double x, double y)
        {
            double r0 = x * x - y * y, r1 = x * y, r2 = x, r3 = y;
            mat[0, 0] += r0 * r0; mat[0, 1] += r0 * r1; mat[0, 2] += r0 * r2; mat[0, 3] += r0 * r3; mat[0, 4] += r0;
            mat[1, 1] += r1 * r1; mat[1, 2] += r1 * r2; mat[1, 3] += r1 * r3; mat[1, 4] += r1;
            mat[2, 2] += r2 * r2; mat[2, 3] += r2 * r3; mat[2, 4] += r2;
            mat[3, 3] += r3 * r3; mat[3, 4] += r3;
            mat[4, 4] += 1.0;
            double t = -y * y;                      // 目标 = −y²（见函数注释里的式子）
            rhs[0] += r0 * t; rhs[1] += r1 * t; rhs[2] += r2 * t; rhs[3] += r3 * t; rhs[4] += t;
        }
        foreach (var q in p) AddRow((q.X - m.X) / s, (q.Y - m.Y) / s);
        for (int i = 0; i < 5; i++)                 // 对称的那一半补上
            for (int k = i + 1; k < 5; k++) mat[k, i] = mat[i, k];
        if (!SolveLinear(mat, rhs, 5)) { why = "方程组奇异"; return false; }

        double A = rhs[0], B = rhs[1], D = rhs[2] * s, E = rhs[3] * s, F = rhs[4] * s * s;
        double C = 1.0 - A;                                   // 迹约束（A + C = 1）
        double disc = B * B - 4.0 * A * C;
        if (disc >= 0)
        {
            why = $"不成椭圆（A={A:F3} B={B:F3} C={C:F3}，判别式 {disc:F3} ≥ 0）";
            return false;
        }

        // 中心：解 [2A B; B 2C]·c = (−D, −E)
        double det = 4.0 * A * C - B * B;
        if (Math.Abs(det) < 1e-12) { why = "中心解不出"; return false; }
        double cx = (-2.0 * C * D + B * E) / det;
        double cy = (B * D - 2.0 * A * E) / det;

        // 姿态角（屏幕坐标：x 右、y 下）与两个半轴
        double theta = 0.5 * Math.Atan2(B, A - C);
        double ct = Math.Cos(theta), st = Math.Sin(theta);
        double A2 = A * ct * ct + B * ct * st + C * st * st;
        double C2 = A * st * st - B * ct * st + C * ct * ct;
        // 把中心代进去 → 常数项
        double F2 = A * cx * cx + B * cx * cy + C * cy * cy + D * cx + E * cy + F;
        // ⚠ **整条式子的正负是任意的**（乘 −1 还是同一条曲线）：所以不能要求"二次项为负"，
        //    正确的判据是"**二次项同号、常数项反号**"——这样 u²/(−F′/A′) + v²/(−F′/C′) = 1
        //    的两个分母才都是正的。（第一版就是要求 A′<0：干净的正椭圆全被它判成"不成椭圆"。）
        if (A2 * C2 <= 0) { why = $"不成椭圆（二次项异号 A′={A2:F3} C′={C2:F3}）"; return false; }
        if (A2 * F2 >= 0) { why = $"不成椭圆（常数项没反号 F′={F2:F1}）"; return false; }
        double sa = Math.Sqrt(-F2 / A2), sb = Math.Sqrt(-F2 / C2);
        if (double.IsNaN(sa) || double.IsNaN(sb) || sa <= 0 || sb <= 0)
        {
            why = $"半轴解不出（F′={F2:F1} a={sa:F1} b={sb:F1}）";
            return false;
        }
        // **把长轴摆到局部 x 上**（否则"姿态角"会随符号整体差 90°，读数上很怪）：
        // 半轴换了，方向就得跟着转 90°，两者一起换，**形状一个字都不变**。
        if (sa < sb) { (sa, sb) = (sb, sa); theta += Math.PI / 2; }
        theta = NormalizeTilt180((float)(theta * 180.0 / Math.PI)) * MathF.PI / 180f;

        // 残差：几何距离的一阶近似 |Q| / |∇Q|，再按"最大半轴"归一成相对量
        double worst = 0;
        foreach (var q in p)
        {
            double x = q.X - m.X, y = q.Y - m.Y;
            double Q = A * x * x + B * x * y + C * y * y + D * x + E * y + F;
            double gx = 2 * A * x + B * y + D, gy = B * x + 2 * C * y + E;
            double g = Math.Sqrt(gx * gx + gy * gy);
            if (g < 1e-12) continue;
            double d = Math.Abs(Q) / g;
            if (d > worst) worst = d;
        }
        double scale = Math.Max(sa, sb);

        center = new Vector2((float)(m.X + cx), (float)(m.Y + cy));
        a = (float)sa;
        b = (float)sb;
        tiltScreen = (float)(theta * 180.0 / Math.PI);
        err = (float)(worst / scale);
        return true;
    }

    /// <summary>
    /// 解 `n×n` 线性方程组（列主元高斯消元），解写回 <paramref name="b"/>。
    /// 奇异就返回 false（调用方当成"这次拟合不成立"）。
    /// </summary>
    private static bool SolveLinear(double[,] a, double[] b, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-12) return false;
            if (piv != col)
            {
                for (int k = col; k < n; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }
            double p = a[col, col];
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r, col] / p;
                if (f == 0) continue;
                for (int k = col; k < n; k++) a[r, k] -= f * a[col, k];
                b[r] -= f * b[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * b[k];
            b[r] = s / a[r, r];
        }
        return true;
    }

    /// <summary>
    /// **最贴墨迹的外接框**：返回长边方向（屏幕角度，已归一到 ±45°）、宽、高、中心。
    ///
    /// 做法：候选方向取**凸包的每条边**（矩形的边一定出现在凸包上），
    /// 每个方向算一个外接框，然后**挑"墨迹到框的最大距离"最小的那个**
    ///（同样小就取面积小的那个）。
    ///
    /// ⚠ 判据是"贴得住"，**不是"面积最小"**（第一版用的是旋转卡壳求最小面积）：
    /// 手画的矩形边上带一点波浪（±3px），最小面积那一档会被"稍微转一点、切掉波浪"
    /// 这种小便宜骗走 —— 转出来的框斜着切过墨迹，角上差二十几像素，
    /// `--inktest` 里三个矩形族当场就被它判成"贴不住"。
    /// 而"贴得住"才是用户要的（"要和墨迹对得上"），那就直接拿它当目标函数。
    /// </summary>
    private static void BestFitBox(IReadOnlyList<Vector2> ink, IReadOnlyList<Vector2> hull,
                                   out float tiltScreen, out float w, out float h, out Vector2 center)
    {
        tiltScreen = 0f; w = h = 0f; center = Vector2.Zero;
        if (hull.Count < 3)
        {
            // 退化情形（墨迹几乎是一条线）：用它的外接框兜底
            Bounds(ink, out var mn, out var mx);
            w = mx.X - mn.X; h = mx.Y - mn.Y;
            center = new Vector2((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f);
            return;
        }

        // 边界取**分位数**（去掉两端的 5%）而不是极值：手画框在角上总"画过一点"，
        // 那几根细尾巴（十来像素长、几个点）会把外接框整体撑大 20~30 px ——
        // `--inktest` 里"矩形·过冲"那一族 40 条只能认出来 2 条，就是被它们撑的。
        // 而按分位数取，落在尾巴上的那几个点被剔掉，**密度高的那一段才是框**（干净的手画框
        // 没有尾巴，分位数和极值本来就一样，不会白缩）。
        int n = ink.Count;
        int k = Math.Max(1, n / 20);
        var us = new float[n];
        var vs = new float[n];

        float bestWorst = float.MaxValue, bestArea = float.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            var e = hull[(i + 1) % hull.Count] - hull[i];
            if (e.LengthSquared() < 1e-6f) continue;
            var dir = Vector2.Normalize(e);
            var nrm = new Vector2(-dir.Y, dir.X);
            for (int q = 0; q < n; q++)
            {
                us[q] = Vector2.Dot(ink[q], dir);
                vs[q] = Vector2.Dot(ink[q], nrm);
            }
            Array.Sort(us);
            Array.Sort(vs);
            float minU = us[k], maxU = us[n - 1 - k], minV = vs[k], maxV = vs[n - 1 - k];
            float bw = maxU - minU, bh = maxV - minV;
            if (bw <= 0f || bh <= 0f) continue;
            var c = dir * ((minU + maxU) * 0.5f) + nrm * ((minV + maxV) * 0.5f);
            var quad = BoxQuad(c, bw, bh, dir, nrm);
            // 目标函数 = **墨迹到四条边线的最大垂距**（和认不认那一关同一把尺子）：
            // 框太窄、框摆歪，它立刻涨上去；而"画过头"那几根尾巴是**沿着边线**出去的，
            // 垂距≈0，正好不算账。
            float worst = MaxLineDistance(ink, quad);
            float area = bw * bh;
            bool better = worst < bestWorst - 0.05f
                       || (MathF.Abs(worst - bestWorst) <= 0.05f && area < bestArea);
            if (!better) continue;
            bestWorst = worst;
            bestArea = area;
            w = bw;
            h = bh;
            center = c;
            tiltScreen = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
        }

        // ⚠ **差 90° 就得把宽高对调**：方向要归一到 (-45°, 45°]（矩形是对称的，边朝左朝右是同一件事），
        //   而归一的那一步减去 90° 之后，**盒子里"哪个方向是宽"就换了** ——
        //   不对调的话，一个竖长的框会被横过来（宽高互换），验收当场判它"贴不住"，
        //   然后它就被认成平行四边形了。
        //   `--inktest` 里"矩形 77.5% / 斜矩形 80% / 圆角 62.5%"那几族全栽在这。
        float t = tiltScreen;
        while (t > 45f) { t -= 90f; (w, h) = (h, w); }
        while (t <= -45f) { t += 90f; (w, h) = (h, w); }
        tiltScreen = t;
    }

    /// <summary>外接框的四个角（按 <paramref name="dir"/> / <paramref name="nrm"/> 这个局部正交基摆）。</summary>
    private static Vector2[] BoxQuad(Vector2 center, float w, float h, Vector2 dir, Vector2 nrm)
        => new[]
        {
            center - dir * (w * 0.5f) - nrm * (h * 0.5f),
            center + dir * (w * 0.5f) - nrm * (h * 0.5f),
            center + dir * (w * 0.5f) + nrm * (h * 0.5f),
            center - dir * (w * 0.5f) + nrm * (h * 0.5f),
        };

    /// <summary>这些点到闭合轮廓**四条边所在直线**的最大垂距（给"挑最贴的那个框"当目标函数）。</summary>
    private static float MaxLineDistance(IReadOnlyList<Vector2> pts, IReadOnlyList<Vector2> outline)
    {
        float worst = 0f;
        int n = outline.Count;
        foreach (var q in pts)
        {
            float best = float.MaxValue;
            for (int k = 0; k < n; k++)
                best = MathF.Min(best, PointLineDistance(q, outline[k], outline[(k + 1) % n]));
            if (best > worst) worst = best;
        }
        return worst;
    }

    /// <summary>凸包（Andrew 单调链）。返回逆时针一圈的点。</summary>
    private static List<Vector2> ConvexHull(IReadOnlyList<Vector2> pts)
    {
        var s = new List<Vector2>(pts);
        s.Sort((x, y) => x.X != y.X ? x.X.CompareTo(y.X) : x.Y.CompareTo(y.Y));
        if (s.Count < 3) return s;

        static float Cross(Vector2 o, Vector2 a, Vector2 b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new List<Vector2>();
        foreach (var q in s)                       // 下凸壳
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], q) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(q);
        }
        int lower = hull.Count + 1;
        for (int i = s.Count - 2; i >= 0; i--)     // 上凸壳
        {
            var q = s[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], q) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(q);
        }
        hull.RemoveAt(hull.Count - 1);             // 首尾重复
        return hull;
    }

    /// <summary>
    /// **自检专用**：这一笔按椭圆拟合出来的**轴比**（≥1）；拟合不出来给 **0**。
    ///
    /// 只给 `--inktest` 里那张"**圆 / 椭圆的分界线该划在哪**"的扫描表用 ——
    /// 那条线（<see cref="EllipseAxisRatioMin"/>）原来是对着"**理想圆**"的语料定的，
    /// 而真手画的圆径向能抖 3~8%、拟合出来的轴比经常 1.1~1.2，于是**被椭圆抢走**
    ///（用户 2026-09-26："圆老是识别不出来，是因为椭圆抢它的吗？"——就是）。
    /// 要把线划对，就得先能量出两种语料各自的轴比分布，所以留这个薄封装。
    /// ⚠ 它**不含任何逻辑**（和 `Recognize` 走的是同一个 `TryEllipse`），
    /// 也别拿它去做判据。
    /// </summary>
    internal static float EllipseAxisRatioForTest(IReadOnlyList<Vector2> raw)
    {
        if (raw == null || raw.Count < 3) return 0f;
        var g = TryEllipse(Resample(Clean(raw)), 1f);
        return g.IsNothing ? 0f : EllipseAxisRatioOf(g);
    }

    /// <summary>椭圆定义元素（中心 + 外角点）的轴比，≥ 1。</summary>
    private static float EllipseAxisRatioOf(in ShapeGuess g)
    {
        if (g.Def.Length < 2) return 1f;
        float a = MathF.Abs(g.Def[1].X - g.Def[0].X);
        float b = MathF.Abs(g.Def[1].Y - g.Def[0].Y);
        float hi = MathF.Max(a, b), lo = MathF.Max(1e-3f, MathF.Min(a, b));
        return hi / lo;
    }

    /// <summary>B 点处的**内角**（度，0~180）：`180°` = 三点共线（不是角），`90°` = 直角。
    /// 用 `acos` 的夹角公式；`Vector2` 没有 `AngleBetween`，就自己写。</summary>
    private static float InteriorAngleDeg(Vector2 a, Vector2 b, Vector2 c)
    {
        var u = a - b; var v = c - b;
        float lu = u.Length(), lv = v.Length();
        if (lu < 1e-4f || lv < 1e-4f) return 180f;
        float cos = Math.Clamp(Vector2.Dot(u, v) / (lu * lv), -1f, 1f);
        return MathF.Acos(cos) * 180f / MathF.PI;
    }

    /// <summary>相邻两个角之间那条"边"够不够直：边上所有点到两端连线的垂距 ÷ 弦长。</summary>
    private static bool AllEdgesStraight(IReadOnlyList<Vector2> p, List<int> corners)
    {
        int n = p.Count;
        for (int k = 0; k < corners.Count; k++)
        {
            int i0 = corners[k];
            int i1 = corners[(k + 1) % corners.Count];
            var a = p[i0];
            var b = p[i1];
            float chord = Vector2.Distance(a, b);
            if (chord < 4f) continue;                 // 太短的一段不判（噪声占比大）
            if (i1 <= i0) i1 += n;                    // 环形取样
            float worst = 0f;
            for (int i = i0 + 1; i < i1; i++)
            {
                var q = p[i % n];
                float dev = PointLineDistance(q, a, b);
                if (dev > worst) worst = dev;
            }
            if (worst / chord > EdgeStraightRatio) return false;
        }
        return true;
    }

    private static float PointLineDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len = ab.Length();
        if (len < 1e-4f) return Vector2.Distance(p, a);
        var ap = p - a;
        // 二维叉积（`Vector2` 没有 `Cross`，就写开）：|ab × ap| ÷ |ab| = 点到直线的距离
        return MathF.Abs(ab.X * ap.Y - ab.Y * ap.X) / len;
    }

    /// <summary>点集的**重心**。</summary>
    private static Vector2 Mean(IReadOnlyList<Vector2> p)
    {
        var m = Vector2.Zero;
        foreach (var q in p) m += q;
        return p.Count == 0 ? m : m / p.Count;
    }

    /// <summary>绕 <paramref name="c"/> 转 <paramref name="degScreen"/> 度（**屏幕方向**：正 = 顺时针）。
    /// 平面旋转的**逆**矩阵就是转 -deg，所以"转正"和"映回"用同一个函数、只差一个负号。</summary>
    private static List<Vector2> RotateAbout(IReadOnlyList<Vector2> p, Vector2 c, float degScreen)
    {
        float rad = degScreen * MathF.PI / 180f;
        var outp = new List<Vector2>(p.Count);
        foreach (var q in p) outp.Add(RotateOne(q, c, rad));
        return outp;
    }

    private static Vector2 RotateOne(Vector2 q, Vector2 c, float rad)
    {
        float cs = MathF.Cos(rad), sn = MathF.Sin(rad);
        float dx = q.X - c.X, dy = q.Y - c.Y;
        return new Vector2(c.X + dx * cs - dy * sn, c.Y + dx * sn + dy * cs);
    }

    /// <summary>角度归一化到 (-90°, 90°]：椭圆/矩形的"主轴"是条线，差 180° 是同一件事。</summary>
    private static float NormalizeTilt180(float deg)
    {
        deg %= 180f;
        if (deg > 90f) deg -= 180f;
        if (deg <= -90f) deg += 180f;
        return deg;
    }

    /// <summary>角度归一化到 (-45°, 45°]：**矩形**用这个——邻边差 90°，边朝左和朝右是同一件事。</summary>
    private static float NormalizeTilt90(float deg)
    {
        deg %= 90f;
        if (deg > 45f) deg -= 90f;
        if (deg <= -45f) deg += 90f;
        return deg;
    }

    /// <summary>
    /// 把识别结果里的**姿态角**写进对象：绕 <see cref="ShapeGuess.RotPivot"/> 转
    /// <see cref="ShapeGuess.RotationDeg"/> 度。
    ///
    /// **全工程只有这一处**把识别出来的姿态角变成变换，写回和自检都走它——
    /// 两处各写一遍的话，"自检说对了、真画出来是反的"这类事迟早发生。
    /// 用的矩阵就是引擎拖旋转柄那一个（`Selection.RotateMatrix`），所以两者完全同源。
    /// </summary>
    internal static void ApplyRotation(Stroke s, in ShapeGuess g)
    {
        if (s == null || MathF.Abs(g.RotationDeg) < 0.01f) return;
        s.Transform = SelectionHandles.RotateMatrix(g.RotationDeg, g.RotPivot);
    }

    /// <summary>自检用：一条笔迹的尺度（包围盒对角线），报告里当"大小"列。</summary>
    internal static float ScaleOf(IReadOnlyList<Vector2> p) => Diagonal(p);

    /// <summary>
    /// **排查用**：把"折线那条判据"的中间结果直接吐出来（重采样后多少点、找到几个角、
    /// 分别落在哪、边直不直）。
    ///
    /// 为什么要开这个口子：折线判据卡住时对外只有一句"边不直"，而那可能是
    /// "角点根本没找到"或"找到了但位置偏了"或"边真的弯"——三种原因的修法完全不同。
    /// 出问题时要能直接问出来，不能靠猜（`--inktest` 的失败明细里会打这一行）。
    /// </summary>
    internal static string CornerReportForTest(IReadOnlyList<Vector2> raw)
    {
        var resampled = Resample(Clean(raw));
        var (p, closed) = LoopRotated(resampled);
        var corners = FindCorners(p, closed);
        var sb = new System.Text.StringBuilder();
        sb.Append($"重采样后 {p.Count} 点（原始 {raw.Count} 点），{(closed ? "围起来了" : "开放")}，角点 {corners.Count} 个");
        foreach (var i in corners)
            sb.Append($" @({p[i].X:F0},{p[i].Y:F0})");
        if (corners.Count >= 3)
            sb.Append($"，边直={AllEdgesStraight(p, corners)}");
        return sb.ToString();
    }
}
