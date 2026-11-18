// ============================================================================
//  InputDeliveryStats.cs —— 输入投递结构遥测（T0 诊断，**只统计、不改变任何行为**）
//
//  [职责] 统计"喂进引擎的点"到底是怎么投递的：同一批里时标是否相同、连批有多长。
//  [测试] 无专项（只读统计）；真机按 `[笔画]` 行里的 `[投递 …]` 一段看
//  [入口] Sample / Reset / Describe
//
//  为什么需要它（2026-11-17，Opus 5.5 评审的核心争点）：
//  评审认为"拐弯甩尾"的根因是**时间语义**——批内点拿到同一个时标时，
//  `InkPredictorLinear.Add` 的 `dt <= 0` 分支会直接 return，速度与方向都不更新，
//  于是下一批首点用"整批间隔"去除"一个子步位移"，速度被低估约 n 倍（n = 批内点数），
//  方向取的是本批**最旧**那一小段（拐弯时就是旧方向）。
//
//  这条推断是否成立，取决于真机到底是不是"批内同毫秒"。以前只能靠猜：
//  `[笔画]` 行的"合并 N（M 条消息 → K 点）"只给出平均点数，看不出时标是否相同。
//  这里补上唯一缺的那一位：**喂点序列里 dt ≤ 0 的占比**，以及连批长度。
//
//  纪律：
//   · 纯统计，不改时间、不改喂养顺序、不参与任何判断（关掉它行为逐字相同）；
//   · 状态是进程级静态量，**每笔起笔清零**（同 InputBlackBox 的薄钩子口径）；
//   · 零分配（全是计数与两个 double），不产生 GC 噪音——`[笔画]` 行的分配那栏要能对得上。
// ============================================================================
namespace InkEngine;

/// <summary>
/// 输入投递结构统计：把"批内时标是否相同"这件事从推断变成读数。
/// 口径与 <see cref="InkPredictorLinear"/> 拿到的东西**完全一致**（同样是喂点的 t），
/// 所以这里读到 dt≤0，预测器那边就必然走那个 `dt <= 0 → return` 的分支。
/// </summary>
internal static class InputDeliveryStats
{
    /// <summary>本次统计里喂进来的点数。</summary>
    public static int Points { get; private set; }
    /// <summary>其中"与上一点时标相同或更早"的点数（预测器会因此跳过一次速度更新）。</summary>
    public static int DtZero { get; private set; }
    /// <summary>连续同刻的**段数**（一段 = 一次批内合并；段内第 1 点有真实 dt）。</summary>
    public static int TieRuns { get; private set; }
    /// <summary>最长的一段连续同刻里"被跳过"的点数（0 = 从来没出现过同刻点）。</summary>
    public static int TieMax { get; private set; }

    private static double _lastT;
    private static int _run;
    private static bool _hasLast;

    /// <summary>起笔时调用：一笔一清零（新的一笔不该继承上一笔的投递结构）。</summary>
    public static void Reset()
    {
        Points = DtZero = TieRuns = TieMax = 0;
        _lastT = 0;
        _run = 0;
        _hasLast = false;
    }

    /// <summary>
    /// 记一个喂进来的点。**O(1)、零分配**，调用点就在 `AddPointCleaned` 的入口。
    /// </summary>
    public static void Sample(double t)
    {
        Points++;
        if (_hasLast && t - _lastT <= 0.0)
        {
            DtZero++;
            _run++;
            if (_run > TieMax) TieMax = _run;
        }
        else
        {
            if (_run > 0) TieRuns++;
            _run = 0;
        }
        _lastT = t;
        _hasLast = true;
    }

    /// <summary>`[笔画]` 行用：一句话说清这一笔的投递结构。没数据时返回空串（不占版面）。</summary>
    public static string Describe()
    {
        if (Points <= 0) return "";
        if (DtZero == 0) return $"[投递 {Points} 点 · 时标全不同]";
        // 段数要把"还在进行中的最后一段"算上，否则收笔时最后一批会被漏记。
        int runs = TieRuns + (_run > 0 ? 1 : 0);
        double pct = 100.0 * DtZero / Points;
        return $"[投递 {Points} 点 · dt≤0 {DtZero}（{pct:F0}%）· 同刻 {runs} 段 · 最长 {TieMax}]";
    }
}
