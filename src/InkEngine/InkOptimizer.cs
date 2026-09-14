namespace InkEngine;

/// <summary>
/// 笔迹优化的接入点。**核心引擎只有这个接口，没有任何实现。**
///
/// 设计意图：把"笔迹长什么样"和"笔迹怎么被画出来"彻底分开。
///
///   核心引擎负责：采样、文档、脏区、上屏。它只会画两种东西——
///     ① 原始采样点连成的等宽带子（最朴素）；
///     ② 外部算好交给它的几何。
///   优化层负责：怎么把那串原始采样点变成"像用笔写出来"的几何。
///
/// 所以核心引擎里**没有**滤波器、抽稀、曲线拟合、笔锋这些概念。
/// 不注册优化器（<see cref="InkOptimizers.Current"/> 保持 null）时，
/// 引擎就是纯底层：指针报什么坐标就用什么坐标。
///
/// 为什么接口是 internal：它要收发 <see cref="Stroke"/>、<see cref="InkPoint"/>
/// 这些引擎内部类型。等引擎要对外提供稳定 API 时，再把模型一起公开，
/// 这里跟着改成 public 即可。
/// </summary>
internal interface IInkOptimizer
{
    /// <summary>
    /// 开始新的一笔，<paramref name="x"/> / <paramref name="y"/> 是落笔点。
    ///
    /// 优化器在这里复位自己的状态（例如滤波器），否则上一笔结束时的速度会
    /// 被带进这一笔的开头，起笔处就会鼓一块。落笔点一并交进来是给滤波器
    /// "打底"用的：不给的话第一个采样点会被当成未初始化而直接放行。
    /// </summary>
    void BeginStroke(float x, float y);

    /// <summary>
    /// 平滑一个指针采样点。原地修改 <paramref name="x"/> / <paramref name="y"/>。
    /// <paramref name="dtSeconds"/> 是距上一次采样的时间，滤波器需要它来计算速度。
    /// </summary>
    void Smooth(ref float x, ref float y, double dtSeconds);

    /// <summary>
    /// 两个采样点之间的最小间距（**逻辑**像素）。小于它就直接丢掉；引擎
    /// 会自己乘上 DPI 缩放。0 = 不抽稀，每个指针消息都收（最密的原始轨迹）。
    /// </summary>
    float SampleStepLogicalPx { get; }

    /// <summary>
    /// 抬笔，做后处理。允许原地改写 <paramref name="stroke"/> 的几何
    /// （填充轮廓 / 中心线 / 宽度曲线），也可以什么都不做。
    /// </summary>
    void EndStroke(Stroke stroke, float dpiScale);

    /// <summary>
    /// 上一次 <see cref="EndStroke"/> 结果的说明，写日志用。没有就返回 null。
    /// </summary>
    string LastReport { get; }
}

/// <summary>
/// 当前装上的优化器。**null = 不装，引擎走纯底层路径。**
///
/// 由宿主在启动时决定（见 InkTeach 的处理）：底层性能测试就该让它保持 null，
/// 这样量到的数字里不含任何平滑/美化成本。
/// </summary>
internal static class InkOptimizers
{
    public static IInkOptimizer Current;
}
