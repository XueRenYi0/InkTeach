using System;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 激光笔的轨迹（**可以同时有好几条**）。
///
/// 用户 2026-09-27 定的行为，照 ClassIn 那个叫「**拖拽激光笔**」的工具
/// （ClassIn 帮助中心把它和普通的「激光笔」并列成两个工具）：
///   · **手写期间整条一直留着**——以前是 600ms 的滑动窗口，写着写着开头就没了；
///   · **松手之后先停留 2 秒**（<see cref="HoldMs"/>），再**整条一起淡出**（<see cref="FadeMs"/>）；
///   · 抬手再写一条时，**上一条还在淡出，不会被新的顶掉**（所以这里是个集合）。
///
/// 业界对照：Drawboard 的 Laser Pointer 把这两种做成同一工具的两个模式
/// （Trail ↔ Point），并把"松手后才开始淡出"叫 Timing = After complete；
/// InkCanvas-Ultra 的 `MW_LaserPointer.cs` 是"停留 1.2 秒 + 淡出 0.6 秒"，
/// 这里按用户定的 2 秒。
///
/// ⚠ 它**不是笔迹**（这一条一直没变）：不进文档、橡皮擦不掉、不撤销、不存档。
/// </summary>
internal sealed class LaserTrail
{
    /// <summary>松手后停留多久才开始淡出（用户 2026-09-27 定的 **2 秒**）。</summary>
    public const double HoldMs = 2000;
    /// <summary>淡出用多久（整条一起淡）。</summary>
    public const double FadeMs = 600;

    /// <summary>
    /// 采样门槛（**平方**距离，画布像素）。
    ///
    /// 数值参考 InkCanvas-Ultra 的 `LaserSampleThreshold = 6.25`（＝ 2.5 像素的平方，
    /// 见它的 `MW_LaserPointer.cs:39`）。为什么现在必须设：以前靠 600ms 窗口顺带把点数限住了，
    /// 现在**整条一直留着**，不设门槛的话一条长轨迹能攒到几千个点，
    /// 而每一帧都要绕着它重建红边和白芯两层轮廓（见 Overlay.DrawLaser）。
    /// </summary>
    private const float SampleThresholdSq = 6.25f;

    /// <summary>一条轨迹：一串采样点 ＋ "淡到哪儿了"。</summary>
    internal sealed class Stroke
    {
        public readonly List<InkPoint> Points = new();
        /// <summary>
        /// 这一条的粗细（逻辑像素，写下那一刻的激光宽）。
        /// **必须每条自己记一份**：它是"写下时"的属性——共用当前值的话，
        /// 老师画完一条、抬手把粗细调大、再画下一条，**上一条会跟着一起变胖**。
        /// </summary>
        public float WidthLogical = 4f;
        /// <summary>淡出进度 0..1（1 ＝ 已经淡完、会被丢掉）。</summary>
        public float Fade;
    }

    private readonly List<Stroke> _strokes = new();

    /// <summary>
    /// 这一批已经淡完（或清掉）之后，**还欠屏幕一帧空画面**。
    ///
    /// 为什么不能只靠 `Visible`：主循环是先 `Prune` 再问 `NeedsFrame`。
    /// 最后一帧淡到 1 时 `Prune` 把最后一条移掉，`Visible` 立刻变 false；
    /// 如果这一拍不额外要一帧，屏幕上留下的就是**上一帧那条还没擦掉的激光**，
    /// 要等下一次鼠标移动/界面变化才被脏区顺手擦掉——这就是用户 2026-10-18
    /// 报的"激光笔写的时候偶尔会留残影"。
    /// </summary>
    private bool _needsClearFrame;

    /// <summary>淡完/清空之后还要不要补一帧空画面（`NeedsFrame` 会读它）。</summary>
    public bool NeedsClearFrame => _needsClearFrame;

    /// <summary>那一帧空画面已经贴出去了，清掉欠账。</summary>
    public void ClearFramePainted() => _needsClearFrame = false;

    /// <summary>正在写的那条（没有就 null）。</summary>
    public Stroke Writing { get; private set; }

    /// <summary>
    /// **最后一条抬手的时刻**；`-inf` ＝ 还有笔在写（或者这一批还没开始抬手）。
    ///
    /// ⚠ 计时是**整批共用一个**，不是每条各算各的——这正是用户 2026-09-27 报的那条：
    /// "我第一笔写完写第二笔……只要它还在写，第一笔就不会消失；等最后写完以后，
    /// 它们才会一起消失"。每条各算的话，第二笔还在写的时候第一笔就已经到点淡掉了。
    /// </summary>
    private double _releasedAtMs = double.NegativeInfinity;

    /// <summary>这一刻要画的所有轨迹（正在写的 ＋ 还在停留/淡出的）。</summary>
    public IReadOnlyList<Stroke> Strokes => _strokes;

    /// <summary>有没有轨迹要画。**画不画、要不要继续出帧都问它**（见 `NeedsFrame`）——
    /// 停留那 2 秒里画面其实没变，但必须继续出帧，否则淡出的那一刻没人去推进。
    /// </summary>
    public bool Visible => _strokes.Count > 0;

    /// <summary>这一刻所有轨迹里最粗的那一条（脏区要按它往外扩）。</summary>
    public float MaxWidthLogical
    {
        get
        {
            float w = 0f;
            foreach (var s in _strokes) if (s.WidthLogical > w) w = s.WidthLogical;
            return w;
        }
    }

    /// <summary>按下：**新起一条**（还在淡出的那些原样留着，不能被顶掉）。</summary>
    public void Begin(float x, float y, double now, float widthLogical)
    {
        // 有新笔在写 → 这一批**重新算作"还没抬手"**：
        // 计时器打回 -inf，正在停留/淡出的那几条也跟着一起"续命"
        //（用户定的：只要还在写，前面那些就不许消失）。
        _releasedAtMs = double.NegativeInfinity;
        Writing = new Stroke { WidthLogical = widthLogical };
        Writing.Points.Add(new InkPoint { X = x, Y = y, P = 1, T = now });
        _strokes.Add(Writing);
    }

    /// <summary>移动：往正在写的那条上追加（太密的点不要，见 <see cref="SampleThresholdSq"/>）。</summary>
    public void Add(float x, float y, double now)
    {
        var s = Writing;
        if (s == null || s.Points.Count == 0) return;
        var last = s.Points[s.Points.Count - 1];
        float dx = x - last.X, dy = y - last.Y;
        if (dx * dx + dy * dy < SampleThresholdSq) return;
        s.Points.Add(new InkPoint { X = x, Y = y, P = 1, T = now });
    }

    /// <summary>
    /// 抬手：**整批开始计时**（不是给这一条单独计时）。
    /// 抬手后先停留 <see cref="HoldMs"/> 再一起淡出；中途再落笔会由 <see cref="Begin"/>
    /// 把计时器打回 -inf，等于"这笔还没写完，先别淡"。
    /// </summary>
    public void Release(double now)
    {
        // ⚠ 没有正在写的就直接返回：`EndStroke` / `SwitchTool` 是**所有工具**收笔都会走的，
        //   在这里无条件写计时器的话，老师用画笔画一条、抬手，就会顺手给
        //   还在淡出的激光"续命"（激光从此老是不消失）。
        if (Writing == null) return;
        _releasedAtMs = now;
        Writing = null;
    }

    /// <summary>
    /// 每帧推进：算淡出进度、丢掉已经淡完的。
    /// **必须每帧调**（和面板动画同一套节奏）：只在抬手那一刻算的话，
    /// 停留结束之后没人去改 `Fade`，那条轨迹会永远挂在屏幕上。
    /// </summary>
    public void Prune(double now)
    {
        // 还有笔在写（或者这一批压根没抬手过）→ 全部原样留着，一点都别淡。
        if (double.IsNegativeInfinity(_releasedAtMs))
        {
            for (int i = 0; i < _strokes.Count; i++) _strokes[i].Fade = 0f;
            return;
        }

        // 整批共用同一个"抬手到现在"的时间差 → 所有条的 Fade 一模一样，
        // 所以它们总是**一起**淡完、一起被丢掉（用户要的正是这个）。
        double since = now - _releasedAtMs;
        float fade = since <= HoldMs
            ? 0f                                                    // 停留期：一点都没淡
            : (float)Math.Clamp((since - HoldMs) / FadeMs, 0.0, 1.0);

        bool hadStrokes = _strokes.Count > 0;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            _strokes[i].Fade = fade;
            if (fade >= 1f) _strokes.RemoveAt(i);
        }
        // 最后一帧的淡出已经画过了，但那一帧还留在后缓冲里；必须再出一帧
        // 空画面把它擦掉（见 _needsClearFrame 的说明）。
        if (hadStrokes && _strokes.Count == 0) _needsClearFrame = true;
    }

    public void Clear()
    {
        if (_strokes.Count > 0 || Writing != null) _needsClearFrame = true;
        _strokes.Clear();
        Writing = null;
        _releasedAtMs = double.NegativeInfinity;
    }
}
