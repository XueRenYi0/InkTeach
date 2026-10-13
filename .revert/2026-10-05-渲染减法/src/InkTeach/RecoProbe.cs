using System.Numerics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// **墨迹识别的单独测试**（`--inktest`）—— 只跑"一笔墨迹 → 认出什么图形"这一条路
/// （分类走 $P 点云、定形走拟合、最后一关是"贴不贴得住墨迹"，见 ShapeRecognize 与
/// PointCloudRecognizer）。
///
/// ⚠ **日常改识别相关的东西，跑这一条就够了**（用户 2026-09-23 定：
/// "编一个针对墨迹识别的单独测试，墨迹测试走这条路，一般不要全量测试了除非提交的时候"）。
/// 别每次都去跑 `--shapetooltest` / `--axistest` / `--curvetest` 那一圈全量；
/// 全量留到**提交前**跑一遍。
///
/// 它回答的是用户 2026-09-23 提的那个问题："识别器究竟能识别出来哪些效果。"
/// 所以它**不是**"跑一遍看红不红"，而是**出一张准确率表**：
///   · 每种图形给一批**合成的、带手抖的**笔迹，报识别率和"认错到哪去了"；
///   · 再给一批**反例**（半圆弧、S 形、波浪线、涂鸦、折线……），报误报率；
///   · 还有一批**难例**（圆角 / 过冲 / 波浪边 / 大缺口），判据是"认对或不认都行，
///     就是不许认成另一个形状"；
///   · 顺便验证"识别出来的定义元素写回模型之后，占的地方和原来那笔一样"
///     —— 这一条专治"我造了个对象、字段是我自己填的"那类假绿：
///     坐标约定错了（比如把圆的定义元素写成两个角点）这里当场就露。
///
/// 为什么用**合成**笔迹而不是真拿笔去画：数字要可复现、要能一次跑几百条、
/// 而且要能精确制造"边界情况"（倾斜 9° 的矩形、只画了 200° 的圆弧）。
/// 合成数据不能替代真机手感，但它能先把"判据本身对不对"钉死。
/// **固定随机种子**，同一份代码必须跑出同一张表。
/// </summary>
internal static class RecoProbe
{
    /// <summary>每种正例多少条。</summary>
    private const int PositivePerKind = 40;

    /// <summary>每种反例多少条。</summary>
    private const int NegativePerKind = 20;

    /// <summary>基础手抖幅度（逻辑像素）：手写板上笔尖的抖动大致就是这个量级。</summary>
    private const float Jitter = 0.8f;

    /// <summary>一条测试用的笔迹：它属于哪一族、期望认成什么、以及参数说明（出错时用它复现）。</summary>
    private struct Case
    {
        public string Family;
        public string Note;
        public Vector2[] Pts;
        public StrokeKind Want;      // 反例填 Freehand（= 什么都不该认出来）
        public bool ExpectShape;     // true = 正例（该认出来）；false = 反例（认出来才算错）
        /// <summary>true = **只报告、不断言**的那一类：汉字笔画（横 / 竖 / 竖弯钩）。
        /// 判成"直线"不算识别错——它本来就是直线型笔画；真正拦它的是停顿 600ms 那道闸门。</summary>
        public bool Informational;
        /// <summary>true = **难例**：只要求"要么认对、要么不认"，**不许认出一个变成别的样子**的。
        /// 这一档是用户 2026-09-23 反馈的"矩形识别形变太大、完全和墨迹对不上"专门加的：
        /// 圆角、过冲、波浪边、大缺口——真手写全是这样，而它以前会变成一个对不上的形状。</summary>
        public bool Hard;
    }

    internal static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("=== 墨迹识别：单独测试（分类 $P 点云 + 拟合定形 + 墨迹验收）===");
        Console.WriteLine($"  合成手绘笔迹：正例每种 {PositivePerKind} 条、反例每种 {NegativePerKind} 条，"
                          + $"手抖 ±{Jitter:F1} px，固定随机种子 20260923（数字可复现）");

        // $P 的模板数 + 单次识别耗时（模板一多就慢，这条数字决定它能不能用在"边画边认"上）
        Console.Write($"  $P 点云识别：模板 {PointCloudRec.TemplateCount} 个，");
        {
            var timed = MakeGappyCircle(new Random(5)).Pts;
            const int K = 50;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < K; i++) ShapeRecognize.Recognize(timed);
            sw.Stop();
            Console.WriteLine($"单次识别约 {sw.Elapsed.TotalMilliseconds / K:F2} ms（含拟合与验收）");
        }
        Console.WriteLine($"  门槛：最短 {ShapeRecognize.MinLength:F0} px、"
                          + $"直线偏离 ≤ {ShapeRecognize.LineDevRatio * 100f:F0}%、"
                          + $"圆/椭圆残差 ≤ {ShapeRecognize.CircleResidualRatio * 100f:F0}%、"
                          + $"拟合类要画够 {ShapeRecognize.MinArcCoverageDeg:F0}°");
        Console.WriteLine();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        var rnd = new Random(20260923);
        var corpus = BuildCorpus(rnd);

        // ── 正例：按族统计识别率与"认错到哪去了" ──────────────────────────
        Console.WriteLine("  ── 正例（该认出来的）──");
        var positives = corpus.Where(c => c.ExpectShape).ToList();
        // **难例**那一档（`Hard`）先收出来：它的判据不一样（"认对或不认都行、
        // 就是不许认成别的形状"），所以**不进下面这张识别率表**，
        // 也不进"识别率 ≥ 90%"那条断言 —— 它由后面的"难例"那一段单独统计。
        var hardFam = positives.Where(c => c.Hard).Select(c => c.Family).Distinct().ToList();
        var families = positives.Select(c => c.Family).Distinct()
                                .Where(f => !hardFam.Contains(f)).ToList();
        var rateOf = new Dictionary<string, float>();
        foreach (var fam in families)
        {
            var cases = positives.Where(c => c.Family == fam).ToList();
            int ok = 0;
            var wrong = new Dictionary<string, int>();
            foreach (var c in cases)
            {
                var g = ShapeRecognize.Recognize(c.Pts);
                if (g.Kind == c.Want) { ok++; continue; }
                string k = Label(g.Kind) + (g.IsNothing ? $"（{ShortRule(g.Rule)}）" : "");
                wrong[k] = wrong.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            float rate = ok / (float)cases.Count;
            rateOf[fam] = rate;
            string detail = wrong.Count == 0
                ? "—"
                : string.Join("，", wrong.Select(kv => $"{kv.Key}×{kv.Value}"));
            Console.WriteLine($"   {fam,-10} {ok,3}/{cases.Count,-3} {rate * 100f,6:F1}%   判成: {detail}");
            // 这一族没全对 → **顺手把第一条错例的中间结果打出来**：
            // "角点找到几个、落在哪"是折线那条判据唯一能自查的抓手，
            // 不打出来就只剩"改阈值试试"这一种办法。
            if (ok < cases.Count)
            {
                var firstBad = cases.First(c => ShapeRecognize.Recognize(c.Pts).Kind != c.Want);
                Console.WriteLine($"        ↳ 错例 {firstBad.Note}：{ShapeRecognize.CornerReportForTest(firstBad.Pts)}");
                // **完整判据**也打一份（不截断）：这一族卡在哪一道闸、差多少，一眼就看得到
                // ——本仓库那条"判据要能一眼看出卡在哪"的老规矩。
                Console.WriteLine($"        ↳ 完整判据：{ShapeRecognize.Recognize(firstBad.Pts).Rule}");
            }
        }

        // ── 反例：误报率 ────────────────────────────────────────────────
        //  分两档看，因为它们的性质完全不一样：
        //   · **本该认不出来**的（半圆弧、涂鸦……）——误报就是要修的 bug；
        //   · **本来就是直线型**的汉字笔画（横 / 竖 / 竖弯钩）——判成直线不算错，
        //     那是"这条功能在写字时会咬人多少"的量，靠**停顿 600ms**那道闸门挡，
        //     不靠识别器挡。这一档**只报告、不断言**。
        // ── 【选特征用・只报告不断言】两端切线的夹角分布（抛物线 vs 双曲线）────────
        //
        // §43.4.11：用户 2026-09-25 定的约定 —— "末端攒到一块（平行）→ 抛物线；
        // 留着一个中间夹角 → 双曲线"。这一段**先看两堆分不分得开**，再定阈值。
        // **不许先拍一个数再让用户去真机上发现不行**（那正是上一轮栽跟头的方式）。
        // 顺带按**弧长**分组报一遍：这样能直接看出"**画到多长才认得出来**"，
        // 也就是用户那句"开口大才认得出来"的精确边界。
        Console.WriteLine();
        Console.WriteLine("  ── 两端切线夹角的分布（选特征用，只报告）──");
        foreach (var fam in new[] { "抛物线", "双曲线" })
        {
            var cs = positives.Where(c => c.Family == fam).ToList();
            if (cs.Count == 0) continue;
            var rows = cs.Select(c => (ang: ShapeRecognize.EndTangentAngleDeg(c.Pts),
                                       len: ShapeRecognize.PathLength(c.Pts))).ToList();
            var byAng = rows.OrderBy(r => r.ang).ToList();
            float lenMid = rows.OrderBy(r => r.len).ElementAt(rows.Count / 2).len;
            var shortOnes = rows.Where(r => r.len <= lenMid).Select(r => r.ang).OrderBy(a => a).ToList();
            var longOnes = rows.Where(r => r.len > lenMid).Select(r => r.ang).OrderBy(a => a).ToList();
            Console.WriteLine($"   {fam,-4} 夹角排序："
                + string.Join(" ", byAng.Select(r => $"{r.ang:F0}")));
            Console.WriteLine($"        中位 {byAng[byAng.Count / 2].ang:F0}°"
                + $" ｜ 短弧(长≤{lenMid:F0}) 中位 {shortOnes[shortOnes.Count / 2]:F0}°"
                + $" ｜ 长弧 中位 {longOnes[longOnes.Count / 2]:F0}°");
        }

        Console.WriteLine();
        Console.WriteLine("  ── 反例（不该认出来的）──");
        var negatives = corpus.Where(c => !c.ExpectShape && !c.Informational).ToList();

        // **四分之一弧（90° 圆弧）判成抛物线，按口径不算误报**（用户 2026-09-25 选的 A）。
        // 理由：90° 圆弧和抛物线**在数学上只差约 1.4%** —— 圆的四次项相对二次项只有 12%，
        // 落在容差（2.5 逻辑像素）之内，**肉眼上就是同一个形状**。而拟合出来的抛物线顶点
        // 落在弧的中间、左右对称铺开，看着就是原来那段弧（符合"大差不差"）。
        // ⚠ **180° 半圆弧照旧是硬反例**（实测 0%）：那种开口太宽，抛物线贴不住。
        // ⚠ 判成**别的**形状仍然算误报 —— 这一档放开的只有"抛物线"这一种结果。
        // ⚠ **半周期正弦**也在这一档：它只有半个周期，`WaveMinCycles = 0.90` 那道门槛
        //   会把它挡住、落到抛物线那一档（"半个拱"本来就像一个抛物线的局部）。
        //   实测 20/20 判成抛物线 —— **按口径算对**（和四分之一弧同一条理），
        //   真误报（认成别的形状）仍然是 0，那条才是不许破的。
        var quarterArcOk = new HashSet<string> { "四分之一弧", "半周期正弦" };

        foreach (var fam in negatives.Select(c => c.Family).Distinct())
        {
            var cases = negatives.Where(c => c.Family == fam).ToList();
            int bad = 0, asParabola = 0;
            var asWhat = new Dictionary<string, int>();
            foreach (var c in cases)
            {
                var g = ShapeRecognize.Recognize(c.Pts);
                if (g.IsNothing) continue;
                if (quarterArcOk.Contains(fam) && g.Kind == StrokeKind.Parabola) { asParabola++; continue; }
                bad++;
                string k = Label(g.Kind);
                asWhat[k] = asWhat.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            string note = bad == 0 ? "" : "  误判成: " + string.Join("，", asWhat.Select(kv => $"{kv.Key}×{kv.Value}"));
            // **放开的那几条要明说** —— 藏起来就成了"把 bug 写成期望"的反面（把行为藏起来）。
            if (asParabola > 0) note += $"（另有 {asParabola} 条判成了抛物线 —— 按口径算对）";
            Console.WriteLine($"   {fam,-16} {bad,3}/{cases.Count,-3} 误报 {bad * 100f / cases.Count,5:F1}%{note}");
            negativeRate[fam] = bad / (float)cases.Count;
        }

        // ── 【识别率·只报告】两笔 → 双曲线：识别率 ＋ 卡在哪一道闸 ＋ 容差扫描 ────────
        //
        // 为什么单开这一段：原来"两支"只有**一条**用例（`TwoBranchHyperbola`，完美对称 +
        // ±0.8px 抖动），它**恒过** —— 用户 2026-09-26 连报三轮"画几次才成一次 / 怎么都变
        // 不出来 / 要不就按两段反弧认吧"，在自检里**一个字都看不见**。
        // 这里按**真人手画**造三档语料（一般 / 很草 / 潦草）＋ 三类必须挡住的反例，
        // 把两张容差表扫出来：**该松哪一条，看这张表说话，不许先拍一个数**。
        Console.WriteLine();
        Console.WriteLine("  ── 手画两支 → 双曲线（只报告：识别率 ＋ 卡在哪一道闸）──");
        {
            const int tries = 60;
            // ① 当前这一档的识别率 ＋ 卡住的理由分布（"一般手画"）
            int ok = 0;
            var why = new Dictionary<string, int>();
            var ratios = new List<float>();
            for (int i = 0; i < tries; i++)
            {
                var p0 = HandTwoBranches(new Random(70100 + i * 17), 0);
                ratios.Add(SymmetryRatio(p0.a, p0.b, p0.center));
                var g = ShapeRecognize.TryTwoBranchHyperbola(p0.a, p0.b, 1f);
                if (g.Kind == StrokeKind.Hyperbola) { ok++; continue; }
                string key = Group(g.Rule);
                why[key] = why.TryGetValue(key, out var c0) ? c0 + 1 : 1;
            }
            ratios.Sort();
            Console.WriteLine($"   一般手画：{ok}/{tries} = {ok * 100f / tries:F0}% 认成双曲线");
            Console.WriteLine($"   手画一对的「镜像偏差 ÷ 另一支尺度」：中位 {ratios[tries / 2]:F2}、"
                              + $"90 分位 {ratios[(int)(tries * 0.9)]:F2}、最大 {ratios[^1]:F2}");
            foreach (var kv in why.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"     卡在：{kv.Key} ×{kv.Value}");

            // ② 三档正例 ＋ 三类反例（都只在内存里过一遍，不进出图）
            var lv0 = new List<(Vector2[] a, Vector2[] b)>();
            var lv1 = new List<(Vector2[] a, Vector2[] b)>();
            var lv2 = new List<(Vector2[] a, Vector2[] b)>();
            var negSame = new List<(Vector2[] a, Vector2[] b)>();
            var negOther = new List<(Vector2[] a, Vector2[] b)>();
            var negHalf = new List<(Vector2[] a, Vector2[] b)>();
            for (int i = 0; i < tries; i++)
            {
                var p0 = HandTwoBranches(new Random(70100 + i * 17), 0);
                var p1 = HandTwoBranches(new Random(91000 + i * 23), 1);
                var p2 = HandTwoBranches(new Random(95000 + i * 37), 2);
                lv0.Add((p0.a, p0.b));
                lv1.Add((p1.a, p1.b));
                lv2.Add((p2.a, p2.b));
                negSame.Add(TwoBranchesSameSide(new Random(92000 + i * 29)));
                negOther.Add(TwoBranchesUnrelated(new Random(93000 + i * 31)));
                negHalf.Add(TwoHalfEllipse(new Random(94000 + i * 41)));
            }
            var looseWhy = new Dictionary<string, int>();
            foreach (var (aa, bb) in lv2)
            {
                var g = ShapeRecognize.TryTwoBranchHyperbola(aa, bb, 1f);
                if (!g.IsNothing) continue;
                string key = Group(g.Rule);
                looseWhy[key] = looseWhy.TryGetValue(key, out var c2) ? c2 + 1 : 1;
            }
            Console.WriteLine("   潦草档卡在哪儿：" + (looseWhy.Count == 0 ? "没有卡住的"
                : string.Join("、", looseWhy.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}"))));

            // 数的口径**只写一处**，两张表都用它（写两处必漏一处）
            (int a, int b, int c, int d, int e, int f) Count()
            {
                int n0 = 0, n1 = 0, n2 = 0, f0 = 0, f1 = 0, f2 = 0;
                for (int i = 0; i < tries; i++)
                {
                    if (ShapeRecognize.TryTwoBranchHyperbola(lv0[i].a, lv0[i].b, 1f).Kind == StrokeKind.Hyperbola) n0++;
                    if (ShapeRecognize.TryTwoBranchHyperbola(lv1[i].a, lv1[i].b, 1f).Kind == StrokeKind.Hyperbola) n1++;
                    if (ShapeRecognize.TryTwoBranchHyperbola(lv2[i].a, lv2[i].b, 1f).Kind == StrokeKind.Hyperbola) n2++;
                    if (ShapeRecognize.TryTwoBranchHyperbola(negSame[i].a, negSame[i].b, 1f).Kind == StrokeKind.Hyperbola) f0++;
                    if (ShapeRecognize.TryTwoBranchHyperbola(negOther[i].a, negOther[i].b, 1f).Kind == StrokeKind.Hyperbola) f1++;
                    if (ShapeRecognize.TryTwoBranchHyperbola(negHalf[i].a, negHalf[i].b, 1f).Kind == StrokeKind.Hyperbola) f2++;
                }
                return (n0, n1, n2, f0, f1, f2);
            }
            void Row(string tag, bool mark)
            {
                var (n0, n1, n2, f0, f1, f2) = Count();
                Console.WriteLine($"   {tag}  {n0 * 100f / tries,6:F0}%  {n1 * 100f / tries,6:F0}%"
                                  + $"  {n2 * 100f / tries,6:F0}%  {f0 * 100f / tries,9:F0}%"
                                  + $"  {f1 * 100f / tries,10:F0}%  {f2 * 100f / tries,12:F0}%"
                                  + (mark ? "   ← 当前" : ""));
            }
            float tolDefault = ShapeRecognize.TwoBranchSymmetryTol;    // 记下当前档（不另抄一份常量）
            float accDefault = ShapeRecognize.TwoBranchAcceptTolScale;
            float looseDefault = ShapeRecognize.TwoBranchLooseTolScale;

            // ⚠ **这张表是"证明不该动对称闸"**：正例一动不动，误报反而爬到 48% ✗
            Console.WriteLine("   对称容差  一般手画   很草手画   潦草手画   误报(同一边)  误报(不相干)  误报(半椭圆两笔)");
            foreach (float tol in new[] { 0.25f, 0.60f, 0.80f })
            {
                ShapeRecognize.TwoBranchSymmetryTol = tol;
                Row($"{tol:F2}", MathF.Abs(tol - tolDefault) < 1e-4f);
            }
            ShapeRecognize.TwoBranchSymmetryTol = tolDefault;          // 扫完**放回去**
            // ⚠ **这张表是要动的**：验收容差的倍率（"贴不贴得住墨迹"那条）
            Console.WriteLine("   验收倍率  一般手画   很草手画   潦草手画   误报(同一边)  误报(不相干)  误报(半椭圆两笔)");
            foreach (float sc in new[] { 1f, 3f, 8f, 16f, 32f })
            {
                ShapeRecognize.TwoBranchAcceptTolScale = sc;
                Row($"{sc,5:F0}×", MathF.Abs(sc - accDefault) < 1e-4f);
            }
            ShapeRecognize.TwoBranchAcceptTolScale = accDefault;
            // 对称过不了时走"反弧"档，用的是**另一个**倍率（`TwoBranchLooseTolScale`）
            Console.WriteLine("   （只靠反弧认下的那些，用的是 TwoBranchLooseTolScale，现在 = "
                              + $"{looseDefault:F0}×；上面这张表里 0.25 那一行的正例就靠它）");

            static string Group(string rule)
                => rule.Contains("不像同一个双曲线") ? "两笔不够对称/不是反弧"
                 : rule.Contains("判别式") ? "圆锥拟合说它不是双曲线"
                 : rule.Contains("贴不住") ? "圆锥曲线贴不住墨迹"
                 : rule.Contains("太短") ? "有一笔太短"
                 : rule.Length > 20 ? rule[..20] : rule;
        }

        // ── 【分界线·只报告】圆 / 椭圆那条线该划在哪 ──────────────────────────
        //
        // 用户 2026-09-26 的原话："圆老是识别不出来，是因为椭圆抢它的吗？
        // 我们这里是不是要定一个分界线，接近于某一个分界线就是圆，扁的就是椭圆，这样规定。"
        // ——判据**本来就是这个**（轴比 < `EllipseAxisRatioMin` → 圆），问题只在**线划在哪**：
        // 原来的 1.12 是拿"理想圆（径向只抖 1.5%）"定的，而**真手画**的圆径向抖 3~8%、
        // 拟合出来的轴比经常 1.1~1.2 → 被椭圆抢走。
        Console.WriteLine();
        Console.WriteLine("  ── 圆 / 椭圆的轴比分界线（只报告）──");
        {
            const int perKind = 50;
            var cr = new List<float>();
            var er = new List<float>();
            var rc = new Random(88001);
            for (int i = 0; i < perKind; i++) cr.Add(ShapeRecognize.EllipseAxisRatioForTest(MakeHandCircle(rc).Pts));
            var re = new Random(88002);
            for (int i = 0; i < perKind; i++) er.Add(ShapeRecognize.EllipseAxisRatioForTest(MakeHandEllipse(re).Pts));
            cr.Sort(); er.Sort();
            float Pct(List<float> v, float p) => v[Math.Clamp((int)(v.Count * p), 0, v.Count - 1)];
            Console.WriteLine($"   手画圆（{perKind} 条）轴比：中位 {Pct(cr, 0.5f):F2}、"
                              + $"75 分位 {Pct(cr, 0.75f):F2}、90 分位 {Pct(cr, 0.90f):F2}");
            Console.WriteLine($"   手画椭圆（{perKind} 条）轴比：中位 {Pct(er, 0.5f):F2}、"
                              + $"10 分位 {Pct(er, 0.10f):F2}、最小 {er[0]:F2}");
            Console.WriteLine("   阈值    判对(圆)   判对(椭圆)   合计");
            foreach (float thr in new[] { 1.10f, 1.12f, 1.15f, 1.20f, 1.25f, 1.30f, 1.40f })
            {
                int okC = cr.Count(r => r > 0f && r < thr);
                int okE = er.Count(r => r >= thr);
                string mark = MathF.Abs(thr - ShapeRecognize.EllipseAxisRatioMin) < 1e-4f ? "  ← 当前" : "";
                Console.WriteLine($"   {thr:F2}    {okC * 100f / perKind,7:F0}%   {okE * 100f / perKind,8:F0}%"
                                  + $"   {(okC + okE) * 100f / (2 * perKind),5:F0}%{mark}");
            }
        }

        // ── 汉字笔画：只报告 ───────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  ── 汉字笔画（判成直线**不算错**，这一档只看「会咬多少」、不断言）──");
        var info = corpus.Where(c => c.Informational).ToList();
        foreach (var fam in info.Select(c => c.Family).Distinct())
        {
            var cases = info.Where(c => c.Family == fam).ToList();
            int line = cases.Count(c => ShapeRecognize.Recognize(c.Pts).Kind == StrokeKind.Line);
            int other = cases.Count(c => !ShapeRecognize.Recognize(c.Pts).IsNothing)
                      - line;
            Console.WriteLine($"   {fam,-16} {line,3}/{cases.Count,-3} 会被判成直线 {line * 100f / cases.Count,5:F1}%"
                              + (other > 0 ? $"（另有 {other} 条判成别的图形）" : ""));
        }
        Console.WriteLine("   说明：它们是**直线型笔画**（一根 250 px 的竖笔带 25 px 的小钩，");
        Console.WriteLine("         本来就是「带钩的直线」）。拦它们的是停顿 600ms 那道闸门，不是识别器。");

        // ── 定义元素写回模型：占的地方对不对 ────────────────────────────
        //  ⚠ 这一条是这次自检里**最值钱**的一条。前面那些只验"判成哪个 Kind"，
        //    而"Kind 对了、定义元素写错了"照样画不出东西（例如把圆的定义元素
        //    写成两个角点，画出来是个半径翻倍的圆）。判据不能是"我自己填的字段等于我自己填的字段"，
        //    所以这里把 Def **真的写进一场普通图形对象**，再拿它算出来的包围盒
        //    跟原始墨迹的包围盒比。
        Console.WriteLine();
        float worstWriteBack = 0f;
        string worstCase = "";
        int checkedWriteBack = 0;
        // 波浪线**单独一条尺子**（见下面那段长注释）：它的对象一定会比墨迹往左多出一截。
        int waveChecked = 0, waveBad = 0;
        float waveOverMax = 0f, wavePeriodMax = 0f;
        int rotChecked = 0;
        float worstAxisDot = 1f;
        string worstAxisCase = "";
        var writeBackWorst = new List<(float rel, string text)>();
        foreach (var c in positives)
        {
            var g = ShapeRecognize.Recognize(c.Pts);
            if (g.Kind != c.Want) continue;
            var s = new Stroke { Kind = g.Kind, Width = 3f };
            s.SetPoints(g.Def);
            // **姿态角也要写**——斜椭圆 / 斜矩形靠它（走的是和引擎拖旋转柄同一个矩阵）。
            ShapeRecognize.ApplyRotation(s, g);
            // **曲线朝向也走同一个函数**（不能各写各的：本轮漏过一处，包围盒差 53.7%）。
            ShapeRecognize.ApplyAxis(s, g);
            // ⚠ 比的是 **WorldInkBounds（墨迹框）**，不是 `Bounds`（控制点框）、也不是
            //   `InkBounds`（那个是**局部控制点**的框）：圆 / 椭圆那两个控制点是
            //   "圆心 + 一个点"，照控制点算出来的框是**扁的**（第一版踩了两次：
            //   圆写回的框 height = 0，报出 35% 的差，看着像"识别错了"，其实是取错了框）。
            //   引擎里"选中框 / 脏区"吃的也是墨迹框，所以它才是"用户看到的那个框"。
            var got = s.WorldInkBounds;
            var ink = InkBounds(c.Pts, out float diag);

            // ★ **双曲线要按"两支"比**（2026-09-25 实测发现的）：
            //   墨迹只画了**一支**，而对象内部那个 `branch` 循环**永远画两支**
            //   —— 那正是我们要的"画一支就补出另一支"。所以拿"一支的框"去比"两支的框"
            //   必然差一大截（实测最差 **78.8%**），看着像几何错了，
            //   其实是**框就取错了**（和圆那次"取成控制点框"是同一类错，见下面那段注释）。
            //   做法：双曲线**关于中心是中心对称的**，所以把墨迹的框**按中心点反射**再取并集，
            //   得到的就是"两支"的框。
            float inkMinX = ink.MinX, inkMaxX = ink.MaxX, inkMinY = ink.MinY, inkMaxY = ink.MaxY;
            if (g.Kind == StrokeKind.Hyperbola && g.Def != null && g.Def.Length >= 1)
            {
                var o = g.Def[0];
                inkMinX = MathF.Min(ink.MinX, 2f * o.X - ink.MaxX);
                inkMaxX = MathF.Max(ink.MaxX, 2f * o.X - ink.MinX);
                inkMinY = MathF.Min(ink.MinY, 2f * o.Y - ink.MaxY);
                inkMaxY = MathF.Max(ink.MaxY, 2f * o.Y - ink.MinY);
            }
            float dev = MathF.Max(
                MathF.Max(MathF.Abs(got.MinX - inkMinX), MathF.Abs(got.MaxX - inkMaxX)),
                MathF.Max(MathF.Abs(got.MinY - inkMinY), MathF.Abs(got.MaxY - inkMaxY)));

            // ★★ **波浪线要单独一条尺子**（2026-09-26 加这一族时发现的）：
            //
            //   它的起点**必须落在零点上**（模型语义：`Model.WaveYAt` 里 `u=0` 时 `y=起点.y`），
            //   所以对象**一定从墨迹左端之外开始** —— 往左最多多出**半个周期**
            //（两个 `dy` 方向把可选的零点铺成"每半周期一个"）。那是**设计**，不是写错了。
            //   而"差 ÷ 对角线"那种百分比尺子对"只画了 1.6 个周期"的墨迹必然报到 20% 以上
            //（实测 19.7%），**是拿错的尺子量对的东西** —— 和双曲线那次"拿一支的框比两支的框"
            //   是同一类错。
            //
            //   波浪线量三件事，都是它真正的口径：
            //     ① **右端对齐**：曲线不许可比墨迹短（短了就是"墨被吃掉一截"）；
            //     ② **往左多出的部分 ≤ 半个周期**；③ 纵向范围不许差（振幅拟合得对不对）。
            if (g.Kind == StrokeKind.Wave && g.Def != null && g.Def.Length >= 3)
            {
                float period = MathF.Abs(g.Def[2].X - g.Def[0].X);
                float over = MathF.Max(0f, ink.MinX - got.MinX);
                float height = MathF.Max(1f, ink.MaxY - ink.MinY);
                float devY = MathF.Max(MathF.Abs(got.MinY - ink.MinY), MathF.Abs(got.MaxY - ink.MaxY));
                bool ok = got.MaxX >= ink.MaxX - 2f
                       && over <= period * 0.5f + 2f
                       && devY <= 0.10f * height + 2f;
                waveChecked++;
                if (over > waveOverMax) { waveOverMax = over; wavePeriodMax = period; }
                if (!ok) waveBad++;
                continue;      // 不走下面那条百分比
            }

            float rel = dev / MathF.Max(1f, diag);
            checkedWriteBack++;

            // **姿态角的方向对不对**：包围盒**查不出**这个错（θ 和 −θ 的外接矩形一模一样），
            // 所以单独比一次"对象的长轴方向" vs "墨迹自己量出来的主轴方向"。
            // 差了符号（镜像）时点积会掉到 0.5 上下，这里当场红。
            //
            // ⚠ 只查"墨迹本身就明显长"的那些：接近正方形的形状，它的"主轴朝哪"**本来就是噪声**
            //  （两个特征值几乎相等），拿它当期望值去较真等于自己造一个必错的题——
            //  第一版就报了 0.978 的"失败"，那条语料是一个 263×268 的矩形。
            if (MathF.Abs(g.RotationDeg) > 0.01f && Anisotropy(c.Pts) >= 1.15f)
            {
                // 本地**长轴**是哪个方向：矩形那两个定义元素是**对角点**，谁长谁就是长轴
                //（画一个"瘦高"的矩形时，本地 +x 反而落在短边上——第一版就假定它是 +x，
                //  于是把"长轴对齐"报成 0.007，看着像姿态角搞反了，其实是指错了轴）。
                float dw = MathF.Abs(g.Def[1].X - g.Def[0].X);
                float dh = MathF.Abs(g.Def[1].Y - g.Def[0].Y);
                var localMajor = dw >= dh ? Vector2.UnitX : Vector2.UnitY;
                var axis = Vector2.Normalize(Vector2.TransformNormal(localMajor, s.Transform));
                var wantDir = PrincipalDir(c.Pts);
                float dot = MathF.Abs(Vector2.Dot(axis, wantDir));
                rotChecked++;
                if (dot < worstAxisDot) { worstAxisDot = dot; worstAxisCase = $"{c.Family}（{c.Note}）"; }
            }

            // 留最差的三条明细：这一条一旦红了，**必须能直接看出是哪条语料、差在哪**，
            // 否则就只剩"把容差放大"这一种反应（那是把 bug 写成期望）。
            writeBackWorst.Add((rel,
                $"{c.Family} {c.Note}：墨迹 [{ink.MinX:F0},{ink.MinY:F0}]-[{ink.MaxX:F0},{ink.MaxY:F0}] "
                + $"vs 写回 [{got.MinX:F0},{got.MinY:F0}]-[{got.MaxX:F0},{got.MaxY:F0}]"));
            if (rel > worstWriteBack) { worstWriteBack = rel; worstCase = $"{c.Family}（{c.Note}）"; }
        }
        writeBackWorst.Sort((a, b) => b.rel.CompareTo(a.rel));
        foreach (var w in writeBackWorst.Take(3))
            Console.WriteLine($"     最差写回 {w.rel * 100f,5:F1}%  {w.text}");
        // 波浪线那条尺子的读数（见上面那段长注释）
        Console.WriteLine($"     波浪线写回（另一条尺子）：{waveChecked} 条，右端对齐 ＋ 往左多出"
                          + $"最多 {waveOverMax:F0} px（该条半周期 {wavePeriodMax * 0.5f:F0} px）");

        // ── **认出来的形状必须和墨迹贴得住**（用户 2026-09-23 的核心反馈）──────────
        //  "矩形识别形变太大，完全和墨迹对不上" —— 这一条就是把"对不上"**量出来**：
        //  认出来的形状，它的轮廓和墨迹的最大偏差（按形状的尺度归一）。
        //  ⚠ 轮廓是从**识别结果的定义元素**反推的，是一份**独立实现**——
        //    要是和识别器里那份验收代码共用同一个函数，这个自检就只是在复读自己。
        Console.WriteLine();
        float worstDev = 0f;
        string worstDevCase = "";
        int devChecked = 0;
        foreach (var c in positives)
        {
            var g = ShapeRecognize.Recognize(c.Pts);
            if (g.IsNothing) continue;
            if (g.Kind != c.Want) continue;                    // 认错的另有一档统计
            float dev = Deviation(g, c.Pts);
            devChecked++;
            if (dev > worstDev) { worstDev = dev; worstDevCase = $"{c.Family}（{c.Note}）"; }
        }
        Console.WriteLine($"  认出来的形状与墨迹的最大偏差：{worstDev * 100f:F1}%"
                          + $"（{devChecked} 条；最差 {worstDevCase}）");
        // 最差那条的**中间量**：偏差一大就要能一眼看出是"框小了 / 中心偏了 / 角度反了"，
        // 否则只能靠猜（这一条加进来之后，矩形那批的病因当场就定位了）。
        if (worstDev > 0.10f)
        {
            foreach (var c in positives)
            {
                var g = ShapeRecognize.Recognize(c.Pts);
                if (g.IsNothing || g.Kind != c.Want) continue;
                if (MathF.Abs(Deviation(g, c.Pts) - worstDev) > 1e-6f) continue;
                Bounds(c.Pts, out var bmn, out var bmx);
                Console.WriteLine($"     ↳ {c.Family} {c.Note}");
                Console.WriteLine($"        识别成 {Label(g.Kind)}，Def = ({g.Def[0].X:F0},{g.Def[0].Y:F0})-"
                                  + $"({g.Def[1].X:F0},{g.Def[1].Y:F0})，姿态 {g.RotationDeg:F1}°，"
                                  + $"中心 ({g.RotPivot.X:F0},{g.RotPivot.Y:F0})");
                Console.WriteLine($"        墨迹框 ({bmn.X:F0},{bmn.Y:F0})-({bmx.X:F0},{bmx.Y:F0})"
                                  + $" = {bmx.X - bmn.X:F0}×{bmx.Y - bmn.Y:F0}");
                break;
            }
        }

        // ── 难例一档：**认对 / 不认都行，不许认成别的形状** ────────────────
        Console.WriteLine();
        Console.WriteLine("  ── 难例（圆角 / 过冲 / 波浪边 / 大缺口）：认对或不认都行，不许认成另一个形状 ──");
        int hardWrong = 0;
        foreach (var fam in hardFam)
        {
            var cases = positives.Where(c => c.Family == fam).ToList();
            int ok = 0, none = 0;
            var wrong = new Dictionary<string, int>();
            foreach (var c in cases)
            {
                var g = ShapeRecognize.Recognize(c.Pts);
                if (g.IsNothing) { none++; continue; }
                if (g.Kind == c.Want) { ok++; continue; }
                string k = Label(g.Kind);
                wrong[k] = wrong.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            int wrongN = wrong.Values.Sum();
            hardWrong += wrongN;
            Console.WriteLine($"   {fam,-14} 认对 {ok,3}／不认 {none,3}／认错 {wrongN,3}"
                              + (wrong.Count == 0 ? "" : "   认成: " + string.Join("，", wrong.Select(kv => $"{kv.Key}×{kv.Value}"))));
        }

        // ⚠ 这两条验的**只是函数层**（`RecognizeChain`），**引擎还没接这条路**——
        //    真机上"四条边分着画一个矩形"现在**不会**被合成（见 RecognizeChain 的注释与 §42.7）。
        //    留着它是因为这段逻辑本身是对的，将来接上时不用重写；但**别把它当成"已支持"**。
        // ── 多笔（把几笔接成一条链）─────────────────────────────────────
        Console.WriteLine();
        var fourEdgeRect = FourStrokeRectangle(rnd, out var rectNote);
        var rectChain = ShapeRecognize.RecognizeChain(fourEdgeRect);
        var hyper = TwoBranchHyperbola(rnd);
        var hyperChain = ShapeRecognize.RecognizeChain(hyper);

        // ── 吸附边界 ────────────────────────────────────────────────────
        var snapIn = ShapeRecognize.SnapToAxis(new Vector2(0, 0), SnapEnd(3.9f), 4f);
        var snapOut = ShapeRecognize.SnapToAxis(new Vector2(0, 0), SnapEnd(4.1f), 4f);

        // ── 断言 ────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("  ── 断言 ──");

        // ① 表覆盖：识别器能产的每一种都得有用例（加识别器忘写用例 → 当场红）
        //
        // ⚠ **"两笔"那条路要一起算**：`Hyperbola` **只有两笔能产出**（单笔一律不出双曲线 ——
        //   用户 2026-09-25 定的"**画两支就是双曲线、画一支就是抛物线**"），
        //   单笔语料里**根本没有它**。只看单笔语料，这里必然报"缺 Hyperbola"——
        //   那是**断言写错了**，不是功能坏了。
        var twoParts = TwoBranchHyperbola(new Random(20260901));
        var twoBranchKind = ShapeRecognize.TryTwoBranchHyperbola(twoParts[0], twoParts[1], 1f).Kind;
        var producedKinds = positives.Select(c => c.Want).Concat(new[] { twoBranchKind }).ToHashSet();
        var missing = ShapeRecognize.RecognizableKinds.Where(k => !producedKinds.Contains(k)).ToArray();
        Check("识别器表覆盖：能产的都写了用例", missing.Length == 0,
              missing.Length == 0
                ? $"{ShapeRecognize.RecognizableKinds.Length} 种图形都有正例"
                : "缺用例：" + string.Join("、", missing.Select(Label)));

        // ② 每一种正例的识别率
        //
        // ⚠ **"椭圆·手画"这一族按 85% 卡**（不是 90%），理由是**口径本身**，不是坏了：
        //   按用户 2026-09-26 定的那条分界线（`EllipseAxisRatioMin = 1.20`），
        //   "轴比 1.15~1.20 的手画椭圆"**必然**归圆 —— 扫描表实测那一档椭圆侧就是 **88%**
        //（见"圆 / 椭圆的轴比分界线"那段）。拿 90% 去卡它 = **预设一道按口径答不对的题**。
        //   ⚠ 真正要守住的是"**一个都不许'什么都不认'**"（现在 40 条里 0 条）。
        var rateTarget = new Dictionary<string, float> { ["椭圆·手画"] = 0.85f };
        foreach (var fam in families)
        {
            float target = rateTarget.TryGetValue(fam, out var tt) ? tt : 0.90f;
            Check($"{fam} 识别率 ≥ {target * 100f:F0}%", rateOf[fam] >= target,
                  $"实测 {rateOf[fam] * 100f:F1}%"
                  + (target < 0.90f ? "（低轴比的手画椭圆按分界线归圆，是口径不是错）" : ""));
        }

        // ③ 反例：**本该认不出来**的那一类，误报率要压到 10% 以下
        foreach (var kv in negativeRate)
            Check($"{kv.Key} 误报率 ≤ 10%", kv.Value <= 0.10f, $"实测 {kv.Value * 100f:F1}%");

        // ④ 写回：定义元素真的能画出"原来那一笔占的地方"
        Check("识别结果写回模型后包围盒对得上", worstWriteBack <= 0.15f,
              $"最差 {worstWriteBack * 100f:F1}% 的尺度差（{checkedWriteBack} 条；{worstCase}）");

        // ④b 写回·波浪线：**另一条尺子**（右端对齐 ＋ 往左多出 ≤ 半周期）——
        //     为什么不能用上面那条百分比，见 `MakeWaveMulti` 附近那段长注释。
        Check("波浪线写回：右端对齐、往左最多多出半个周期", waveChecked > 0 && waveBad == 0,
              waveChecked == 0 ? "一条波浪线都没认出来（说明这条路没走通）"
                               : $"{waveChecked} 条，不合格 {waveBad} 条；"
                                 + $"往左最多多出 {waveOverMax:F0} px（半周期 {wavePeriodMax * 0.5f:F0} px）");

        // ⑤ 多笔：四条边分着画的矩形要能认出来
        Check("多笔：四条边分着画 → 矩形", rectChain.Kind == StrokeKind.Rectangle,
              $"{Label(rectChain.Kind)}（{ShortRule(rectChain.Rule)}）；语料 {rectNote}");

        // ⑥ 多笔：双曲线的两支 —— **用户 2026-09-25 定的约定**：
        //    "**画两支就是双曲线，画一支就是抛物线**"（见 `TryTwoBranchHyperbola`）。
        //    所以两支**对称**时必须认成双曲线。
        //
        //    ⚠ 这条**原先是反过来的**（"接不上 → 不认"）—— 那是旧口径，2026-09-25 改掉。
        //      旧口径的理由是"两支的缝 277 > 上限 25，不硬接"；而新路**根本不看缝**，
        //      看的是"两笔是不是关于同一个中心点对称"（中心对称正是双曲线自己的性质）。
        Check("多笔：双曲线两支（对称）→ 双曲线", hyperChain.Kind == StrokeKind.Hyperbola,
              hyperChain.IsNothing ? $"未识别：{ShortRule(hyperChain.Rule)}"
                                   : $"认成 {Label(hyperChain.Kind)}");

        // ⑦ 吸附边界：3.9° 吸、4.1° 不吸（画坐标轴刚需）
        Check("角度吸附边界（±4°）", snapIn.b.Y == 0f && snapOut.b.Y != 0f,
              $"3.9° → 吸平（y={snapIn.b.Y:F1}）；4.1° → 不动（y={snapOut.b.Y:F1}）");

        // ⑧ 斜的形状：姿态角的**方向**没搞反（镜像)。包围盒查不出这个，必须单独卡。
        Check("斜椭圆 / 斜矩形 的姿态方向没镜像", rotChecked > 0 && worstAxisDot >= 0.90f,
              rotChecked == 0
                ? "一条带姿态角的用例都没有（说明斜的没认出来）"
                : $"{rotChecked} 条带姿态角，最差长轴对齐 {worstAxisDot:F3}（{worstAxisCase}）");

        // ⑨ **认出来的必须和墨迹贴得住**（用户 2026-09-23 的核心反馈："完全和墨迹对不上"）。
        //    上限放在 10%：识别器里对折线类是 5.5%，圆/椭圆另有残差闸（约 9~10%），
        //    这里是**独立量一遍**，宽松一点，只抓"整体变形"那一档。
        Check("认出来的形状和墨迹贴得住（≤10%）", worstDev <= 0.10f,
              $"最差 {worstDev * 100f:F1}%（{devChecked} 条；{worstDevCase}）");

        // ⑩ 难例：**不许认成另一个形状**（认不出来是允许的——宁可不变也不变形）
        Check("难例：没有一个被认成别的形状", hardWrong == 0,
              hardWrong == 0 ? $"{hardFam.Count} 族难例，认对或不认" : $"{hardWrong} 条认错了");

        Console.WriteLine();
        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>反例里"本该认不出来"那一档的误报率，按族记着，最后一起断言。</summary>
    private static readonly Dictionary<string, float> negativeRate = new();

    // =====================================================================
    //  语料：合成"手画的"笔迹
    // =====================================================================

    private static List<Case> BuildCorpus(Random rnd)
    {
        var list = new List<Case>();

        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeLine(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeCircle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeEllipse(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeTiltedEllipse(rnd));
        // **真手画的圆 / 椭圆 / 矩形**（2026-09-26 加）：独立种子（理由同上面那条注释：
        // 不能扰动别的族）。上面那几条都是"理想图形 + 1.5% 抖动"，量不出用户报的
        // "圆被椭圆抢走"和"椭圆 / 矩形两个都常常什么都不认"。
        var handRnd = new Random(20260931);
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeHandCircle(handRnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeHandEllipse(handRnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeHandRect(handRnd));
        // 收笔差半条边的矩形（难例）—— `InkCoversEveryEdge` 那个门槛的见证用例，独立种子。
        var gapRnd = new Random(20260932);
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeRectBigGap(gapRnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeTriangle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeRectangle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeTiltedRectangle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeParallelogram(rnd));
        // 抛物线**单独一个随机种子**：它和别的族共用 `rnd` 的话，多插几句就整体挪位，
        // 别的族的用例会跟着换一批（实测：加进来之后"矩形·过冲"从 90% 掉到 87.5%，
        // 那是语料换了、不是功能坏了）。独立的种子 = **扰动不了别人**。
        var paraRnd = new Random(20260925);
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeParabola(paraRnd));

        // **双曲线**（§43.4.11）：同样带**自己的种子**（理由同上：不能扰动别的族）。
        var hypRnd = new Random(20260926);
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeHyperbola(hypRnd));

        // **正弦 / 余弦 / 波浪线**（§43.4.4 三）：各自带**自己的种子**（理由同上）。
        var waveRnd = new Random(20260927);
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeWaveOne(waveRnd, cosine: false));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeWaveOne(waveRnd, cosine: true));
        var waveMultiRnd = new Random(20260929);
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeWaveMulti(waveMultiRnd));
        // 半周期正弦：**"至少将近一个周期"那条门槛的挡箭牌**；三角波：这一族**最容易误报**
        // 的一种（看着像波，其实是一段段直线）—— 它走**难例**那一档（理由见 `MakeTriangleWave`）。
        var halfSineRnd = new Random(20260928);
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeHalfSine(halfSineRnd));
        var triRnd = new Random(20260930);
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeTriangleWave(triRnd));

        // **难例**（用户 2026-09-23 上手反馈那一批）：真手写全是这个样子。
        // 这一档的判据不一样：**认对 / 不认都行，就是不许认成另一个形状**
        //（以前"角点找偏一个 → 矩形变成平行四边形"，正是用户说的"形变太大、对不上"）。
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeRoundedRect(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeOvershootRect(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeWavyEdgeRect(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeGappyCircle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeGappyTiltedEllipse(rnd));

        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeHalfArc(rnd));      // 半圆弧
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeQuarterArc(rnd));   // 四分之一弧
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeSShape(rnd));       // S 形
        // ⚠ 这里原先还有一条"**波浪线（1.5 个周期）→ 不认**"的反例 —— **摘掉了**：
        //   多周期现在是**正例**（写回 `Wave`，见 `MakeWaveMulti`）。旧口径（"模型表达不了
        //   多周期，所以不认"）已经不成立，留着它就是把过期口径当期望。
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakePolyline(rnd));     // 折线（一个折角）
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeScribble(rnd));     // 涂鸦
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeDot(rnd));          // 点 / 极短笔画

        // 汉字笔画：**只报告、不断言**的那一档（横 / 竖 / 竖弯钩都是"直线型"）。
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeHorizontal(rnd));
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeVertical(rnd));
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeHook(rnd));

        return list;
    }

    private static float J(Random rnd) => (float)(rnd.NextDouble() * 2 - 1) * Jitter;

    /// <summary>
    /// **二次函数（四种开口）**：随机开口方向、随机张口、随机顶点位置，
    /// **顶点落在墨迹中间**（绕顶点左右都画了）。
    ///
    /// ⚠ **现在只造"整支"**，不造"只画半支"：模型画抛物线是**绕顶点对称铺开**的
    /// （见 `Model.ParabolaSpanOf`），半支会被凭空补出另一半，所以半支**暂时不认**
    /// （`TryFitParabola` 门槛 ③ 明确挡住了）。等用户定"半支要不要补另一半"再补语料 ——
    /// 那和 §43.4.4（二）里双曲线"补出另一支"是同一个决策点。
    /// </summary>
    private static Case MakeParabola(Random rnd)
    {
        int d4 = rnd.Next(4);
        var dir = d4 switch
        {
            0 => new Vector2(0f, -1f),      // 开口向上（画布 y 向下，所以是 −y）
            1 => new Vector2(0f, 1f),       // 向下
            2 => new Vector2(1f, 0f),       // 向右
            _ => new Vector2(-1f, 0f),      // 向左
        };
        var perp = new Vector2(-dir.Y, dir.X);

        float T = 90f + (float)rnd.NextDouble() * 170f;              // 横向半跨度
        float rise = T * (0.4f + (float)rnd.NextDouble() * 0.9f);    // 两端比顶点高多少
        float aMag = rise / (T * T);                                 // 沿轴 = a·横跨²
        var v = new Vector2(600f + (float)rnd.NextDouble() * 300f,
                            500f + (float)rnd.NextDouble() * 300f);  // 顶点

        int n = 72;
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float s = (-1f + 2f * i / n) * T;                        // 横跨偏移 −T..T
            var p = v + perp * s + dir * (aMag * s * s);
            // 低频漂移 ＋ 高频噪声（和 `--smoothtest` 的语料同一个口径）
            float ph = i / (float)n * MathF.PI * 2f;
            pts[i] = p + perp * (2.5f * MathF.Sin(ph))
                       + new Vector2(J(rnd) * 0.6f, J(rnd) * 0.6f);
        }
        string side = d4 switch { 0 => "上", 1 => "下", 2 => "右", _ => "左" };
        return new Case { Family = "抛物线", Pts = pts, Want = StrokeKind.Parabola, ExpectShape = true,
                          Note = $"开口向{side}、T {T:F0}、抬高 {rise:F0}" };
    }

    /// <summary>
    /// **高中双曲线**（§43.4.11）：按标准方程 `x²/a² − y²/b² = 1`（或上下开口）造**一支**的墨迹。
    ///
    /// **为什么只造一支**：这正是课上最常见的画法，也是口径里定的那条路 ——
    /// "一笔画一支 → 对象自己把另一支补出来"（`HyperbolaPoint` 里那个 `branch` 循环）。
    /// "两笔各画一支"是**多笔**那条路（§42.7 第 7 条），要单独一轮。
    ///
    /// **为什么"画多长"要拉开**：用户 2026-09-25 上手的原话是"**双曲线只有在开口很大时
    /// 才能识别出来**"。按 §43.4.11 门槛 ①，这不是算法不够好 —— **短弧上判据要的信息
    /// 根本不在墨迹里**（近渐近区之前，双曲线支和抛物线真的分不出来）。
    /// 所以语料里把"画多长"（参数 `t` 的范围）拉开，才能看出召回率**随弧长**怎么变，
    /// 而不是只报一个平均分。
    ///
    /// 参数化：`x = ±a·cosh t`、`y = b·sinh t`（`t` 的绝对值越大 = 越靠外 = 越接近渐近线），
    /// **渐近线斜率 = b / a**（就是用户说的"开口大小"）。
    /// </summary>
    private static Case MakeHyperbola(Random rnd)
    {
        bool transX = rnd.Next(2) == 0;                          // 左右开口 / 上下开口
        int branch = rnd.Next(2);                                // 画哪一支（±）
        // ⚠ 2026-09-25 用户上手实测：**一条都没认出来** —— 说明第一版语料**太干净、太均衡**
        //   （噪声 0.6 px、`a` 50~90、开口 0.6~1.4、弧长 1.2~1.8），那种"标准漂亮"的双曲线
        //   真机上不会出现。这里按**真实手感**重造：噪声 2 px 高频 ＋ 4 px 低频、`a` 30~110。
        //
        // ⚠⚠ **开口和弧长要按"老师真会画的"来定，不能什么都造**（第二版踩的）：
        //   把开口造到 0.4（很扁）之后，**27.5% 被判成了直线** —— 因为扁的双曲线支
        //   在一段弧内**本来就近乎一条直线**，被 `TryLine` 半路接走。
        //   而那恰好违反用户 2026-09-25 定的那条规矩："**开口大 = 双曲线**"
        //   —— 老师真画的时候，**画的就是那个"开口大"的**。所以语料就按这条造：
        //   开口 0.9~1.8、弧长 1.3~2.1（**画到能看见渐近区**）。
        //   分母里塞进"老师不会画的扁短弧"，量出来的只是自己的自娱自乐。
        float a = 40f + (float)rnd.NextDouble() * 70f;           // 实半轴
        float slope = 0.9f + (float)rnd.NextDouble() * 0.9f;     // 渐近线斜率 = "开口大小"
        float b = slope * a;
        float tMax = 1.3f + (float)rnd.NextDouble() * 0.8f;      // 画到多远（越小 = 越短弧）
        var o = new Vector2(600f + (float)rnd.NextDouble() * 300f,
                            500f + (float)rnd.NextDouble() * 300f);

        int n = 72;
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = (-1f + 2f * i / n) * tMax;
            float cu = a * MathF.Cosh(t);
            float cv = b * MathF.Sinh(t);
            // 实轴方向放 cosh、另一个方向放 sinh（两种朝向只换谁是谁）
            var p = transX
                ? o + new Vector2(branch == 0 ? cu : -cu, cv)      // 左右：x = ±a·cosh t
                : o + new Vector2(cv, branch == 0 ? cu : -cu);      // 上下：y = ±a·cosh t
            float ph = i / (float)n * MathF.PI * 2f;
            var perp = transX ? new Vector2(0f, 1f) : new Vector2(1f, 0f);
            pts[i] = p + perp * (4f * MathF.Sin(ph))
                       + new Vector2(J(rnd) * 2f, J(rnd) * 2f);
        }
        return new Case
        {
            Family = "双曲线",
            Pts = pts,
            // ★ **单笔这一支的期望是"抛物线"**（用户 2026-09-25 改的口径：
            //   "**画两支就是双曲线，画一支就是抛物线**"，族由**笔数**定）——
            //   单笔那条路**已经不出双曲线**了，所以它是**难例**：
            //   认成抛物线或不认都行，**不许认成别的形状**（实测 认对 19／不认 21／认错 0）。
            Want = StrokeKind.Parabola,
            ExpectShape = true,
            Hard = true,
            Note = $"{(transX ? "左右" : "上下")}开口·{(branch == 0 ? "右/上" : "左/下")}支、"
                 + $"实半轴 {a:F0}、开口 {slope:F1}、tMax {tMax:F2}",
        };
    }

    /// <summary>直线：随机方向、随机长度、**带一点弓**（手画的线不可能是尺子画的）。</summary>
    private static Case MakeLine(Random rnd)
    {
        float ang = (float)(rnd.NextDouble() * Math.PI * 2);
        float len = 120f + (float)rnd.NextDouble() * 400f;
        var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
        var nrm = new Vector2(-dir.Y, dir.X);
        var a = new Vector2(300f + (float)rnd.NextDouble() * 200f, 300f + (float)rnd.NextDouble() * 200f);
        float bow = len * (float)rnd.NextDouble() * 0.015f;
        int n = Math.Max(2, (int)(len / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            pts[i] = a + dir * (len * t) + nrm * (bow * MathF.Sin(MathF.PI * t))
                     + new Vector2(J(rnd), J(rnd));
        }
        return new Case { Family = "直线", Pts = pts, Want = StrokeKind.Line, ExpectShape = true,
                          Note = $"长 {len:F0}、{ang * 180f / MathF.PI:F0}°、弓 {bow / len * 100f:F1}%" };
    }

    /// <summary>圆：随机半径与起点相位、**留一点收口缺口**（手画的圈几乎都收不严）、半径带噪声。</summary>
    private static Case MakeCircle(Random rnd)
    {
        float r = 60f + (float)rnd.NextDouble() * 240f;
        float phase = (float)(rnd.NextDouble() * Math.PI * 2);
        float gapRatio = (float)rnd.NextDouble() * 0.12f;              // 缺口最多 12% 周长
        float sweep = MathF.PI * 2f * (1f - gapRatio);
        var c = new Vector2(400f + (float)rnd.NextDouble() * 100f, 400f + (float)rnd.NextDouble() * 100f);
        int n = Math.Max(8, (int)(r * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float rr = r * (1f + (float)(rnd.NextDouble() * 2 - 1) * 0.015f) + J(rnd);
            float ang = phase + sweep * t;
            pts[i] = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rr;
        }
        return new Case { Family = "圆", Pts = pts, Want = StrokeKind.Circle, ExpectShape = true,
                          Note = $"r {r:F0}、缺口 {gapRatio * 100f:F1}%" };
    }

    /// <summary>
    /// **真手画的圆**（2026-09-26 加，用户报"圆老是识别不出来，是椭圆抢它的吗"）。
    ///
    /// ⚠ 和上面那条 `MakeCircle` 的差别就是这一条存在的全部理由：那条的径向只抖 **1.5%**，
    /// 拟合出来的轴比永远 ≈1.00 → 必然 `< EllipseAxisRatioMin` → **恒过、量不出任何东西**。
    /// 真手画的圆是被**压**出来的：径向有个**二倍角**分量（就是把圆压扁那个，
    /// `r(θ)=r₀(1+ε·cos2θ)` 的轴比 ≈ `1+2ε`）＋ 一个三倍角 ＋ 收口缺口。
    /// ε 取 3%~8% → 轴比 1.06~1.19，**正好压在原来那条 1.12 的线上**（实测中位数就是 1.12）。
    /// </summary>
    private static Case MakeHandCircle(Random rnd)
    {
        float r = 40f + (float)rnd.NextDouble() * 210f;
        float squash = 0.030f + (float)rnd.NextDouble() * 0.050f;      // 二倍角 = 压扁
        float lobe = 0.015f + (float)rnd.NextDouble() * 0.020f;        // 三倍角
        float p1 = (float)(rnd.NextDouble() * Math.PI * 2);
        float p2 = (float)(rnd.NextDouble() * Math.PI * 2);
        float gapRatio = (float)rnd.NextDouble() * 0.10f;
        float sweep = MathF.PI * 2f * (1f - gapRatio);
        var c = new Vector2(500f, 500f);
        int n = Math.Max(10, (int)(r * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float ang = sweep * i / n;
            float k = 1f + squash * MathF.Cos(2f * ang + p1) + lobe * MathF.Cos(3f * ang + p2);
            pts[i] = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (r * k)
                       + new Vector2(J(rnd), J(rnd));
        }
        return new Case { Family = "圆·手画", Pts = pts, Want = StrokeKind.Circle, ExpectShape = true,
                          Note = $"r {r:F0}、压扁 {squash * 100f:F1}%、缺口 {gapRatio * 100f:F1}%" };
    }

    /// <summary>
    /// **真手画的椭圆**：轴比 **1.15~2.2**（外加和手画圆同一套手抖）。
    ///
    /// ⚠ 轴比下限**故意取 1.15**：比 1.15 更圆的"椭圆"和"被手压扁的圆"**本来就分不开**
    /// （见"圆 / 椭圆分界线"那张扫描表），拿它当期望 = **预设一道按口径答不对的题**。
    /// ⚠ 那颗**"蛋形"分量（一倍角）不能省**：它**模型表达不了**（椭圆是中心对称的），
    /// 所以它才是真正在考"残差容差够不够"的那一项；只造二倍角（= 一个更扁的椭圆）
    /// 等于**还是理想图形**。
    /// </summary>
    private static Case MakeHandEllipse(Random rnd)
    {
        float a = 60f + (float)rnd.NextDouble() * 240f;
        float ratio = 0.45f + (float)rnd.NextDouble() * 0.42f;         // 轴比 1.15 ~ 2.22
        float b = a * ratio;
        float squash = 0.020f + (float)rnd.NextDouble() * 0.035f;
        float egg = 0.020f + (float)rnd.NextDouble() * 0.040f;
        float p1 = (float)(rnd.NextDouble() * Math.PI * 2);
        float p3 = (float)(rnd.NextDouble() * Math.PI * 2);
        float gapRatio = (float)rnd.NextDouble() * 0.10f;
        float sweep = MathF.PI * 2f * (1f - gapRatio);
        var c = new Vector2(500f, 500f);
        int n = Math.Max(10, (int)(MathF.Max(a, b) * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float ang = sweep * i / n;
            float k = 1f + squash * MathF.Cos(2f * ang + p1) + egg * MathF.Cos(ang + p3);
            pts[i] = c + new Vector2(MathF.Cos(ang) * a * k + J(rnd), MathF.Sin(ang) * b * k + J(rnd));
        }
        return new Case { Family = "椭圆·手画", Pts = pts, Want = StrokeKind.Ellipse, ExpectShape = true,
                          Note = $"{a * 2:F0}×{b * 2:F0}（轴比 {1f / ratio:F2}）、"
                                 + $"压扁 {squash * 100f:F1}%、蛋形 {egg * 100f:F1}%" };
    }

    /// <summary>
    /// **真手画的矩形**（2026-09-26 加，用户报"椭圆和矩形两个都常常什么都不认"）。
    ///
    /// 现有那两条（`MakeRectangle` / `MakeRoundedRect`）都是**理想矩形**再抹一下角，
    /// 而真手画的矩形有四样"模型表达不了"的误差，少一样都量不出东西：
    ///   ① **角不是正 90°**（顶点随机挪 0~5% 边长）；② **圆角**（半径 3~18% 短边）；
    ///   ③ **边上低频起伏**（±1.5~4px）；④ **收笔差一截**（少画 0~10% 周长）。
    /// </summary>
    private static Case MakeHandRect(Random rnd)
    {
        float w = 60f + (float)rnd.NextDouble() * 350f;
        float h = 50f + (float)rnd.NextDouble() * 230f;
        float tilt = (float)(rnd.NextDouble() * 24 - 12) * 0.25f;
        float rad = tilt * MathF.PI / 180f;
        var c = new Vector2(500f, 500f);
        var v = new List<Vector2>
        {
            new(-w / 2, -h / 2), new(w / 2, -h / 2), new(w / 2, h / 2), new(-w / 2, h / 2)
        };
        for (int i = 0; i < 4; i++)                       // ① 角不是正 90°
        {
            float px = (float)(rnd.NextDouble() * 2 - 1) * 0.05f * w;
            float py = (float)(rnd.NextDouble() * 2 - 1) * 0.05f * h;
            var local = v[i] + new Vector2(px, py);
            v[i] = c + new Vector2(local.X * MathF.Cos(rad) - local.Y * MathF.Sin(rad),
                                   local.X * MathF.Sin(rad) + local.Y * MathF.Cos(rad));
        }
        float roundR = MathF.Min(w, h) * (0.03f + (float)rnd.NextDouble() * 0.15f);   // ② 圆角
        float wobAmp = 1.5f + (float)rnd.NextDouble() * 2.5f;                          // ③ 起伏
        float ph = (float)(rnd.NextDouble() * Math.PI * 2);

        // 顺着四边加角走一圈：**直边也要密采样**（每边 10 个点），角用二次贝塞尔（= 圆角）。
        // ⚠ **四条边一条都不能漏**：第一版在循环里"入边 → 角"地走，循环一结束
        //   **最后一条边（v3→v0）整条丢了** ✗ —— 40 条里 39 条被拦在"有一条边没画"，
        //   看着像判据太严，其实**语料自己真少画了一条边**（判据报得对）。
        //   改成"先算齐 8 个切点，再逐条边走"就不会漏。
        var tas = new Vector2[4];
        var tcs = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var a = v[(i + 3) % 4];
            var b = v[i];
            var nxt = v[(i + 1) % 4];
            float rr = MathF.Min(roundR, 0.45f * MathF.Min(Vector2.Distance(a, b), Vector2.Distance(b, nxt)));
            tas[i] = b - Vector2.Normalize(b - a) * rr;
            tcs[i] = b + Vector2.Normalize(nxt - b) * rr;
        }
        var raw = new List<Vector2>();
        for (int i = 0; i < 4; i++)
        {
            var from = tcs[(i + 3) % 4];
            for (int k = 0; k <= 10; k++) raw.Add(Vector2.Lerp(from, tas[i], k / 10f));
            for (int k = 1; k <= 6; k++)
            {
                float t = k / 6f;
                raw.Add((1 - t) * (1 - t) * tas[i] + 2 * (1 - t) * t * v[i] + t * t * tcs[i]);
            }
        }
        float total = 0f;
        for (int i = 0; i < raw.Count; i++) total += Vector2.Distance(raw[i], raw[(i + 1) % raw.Count]);
        // ④ 收笔缺口取 0~10% 周长（几十像素）：真人收笔差一截就是这个量级。
        float keep = 0.90f + (float)rnd.NextDouble() * 0.10f;
        var pts = new List<Vector2>();
        float run = 0f;
        for (int i = 0; i < raw.Count; i++)
        {
            var p = raw[i];
            var d = raw[(i + 1) % raw.Count] - p;
            var dir = d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : new Vector2(1f, 0f);
            var perp = new Vector2(-dir.Y, dir.X);
            pts.Add(p + perp * (wobAmp * MathF.Sin(run / total * MathF.Tau * 2f + ph))
                      + new Vector2(J(rnd), J(rnd)));
            run += d.Length();
            if (run > total * keep) break;
        }
        return new Case { Family = "矩形·手画", Pts = pts.ToArray(), Want = StrokeKind.Rectangle,
                          ExpectShape = true,
                          Note = $"{w:F0}×{h:F0}、圆角 R{roundR:F0}、起伏 {wobAmp:F1}、收笔 {keep * 100f:F0}%" };
    }

    /// <summary>
    /// **收笔差半条边的矩形**（缺口 ≈ 短边的一半）：真人画完最后一条边常常就这么停手。
    /// 它进**难例**那一档（"认对或不认都行、不许认成别的形状"）。
    /// ⚠ 它同时量过 `InkCoversEveryEdge` 那个门槛（0.6）**要不要放宽**：实测
    ///   **0.6 和 0.45 都是 40/40 认对**（缺口那截虽然没墨，但**角上那截墨**离缺口端点
    ///   不到一个容差，帮衬之下覆盖率本来就过 60%）→ 那个门槛**没动**（没量出好处就不拧常量）。
    /// </summary>
    private static Case MakeRectBigGap(Random rnd)
    {
        float w = 200f + (float)rnd.NextDouble() * 220f;
        float h = 120f + (float)rnd.NextDouble() * 140f;
        var c = new Vector2(500f, 500f);
        var vs = new[]
        {
            c + new Vector2(-w / 2, -h / 2), c + new Vector2(w / 2, -h / 2),
            c + new Vector2(w / 2, h / 2), c + new Vector2(-w / 2, h / 2)
        };
        // **自己走一圈**（不用 `WalkPolygon`）：要保证"最后一条边"就是**短边**（左边，长 h），
        // 缺口才落在短边上 —— 落在长边上时缺口只占那条边的 20~30%，0.6 和 0.45 都过得去，
        // 这条用例就白造了。
        var pts = new List<Vector2>();
        for (int i = 0; i < 4; i++)
        {
            var a = vs[i];
            var b = vs[(i + 1) % 4];
            float len = Vector2.Distance(a, b);
            int steps = Math.Max(2, (int)(len / 4f));
            int upto = i == 3 ? steps / 2 : steps;      // 最后一条边（左边）**只画一半**
            for (int k = 0; k < upto; k++)
                pts.Add(Vector2.Lerp(a, b, k / (float)steps) + new Vector2(J(rnd), J(rnd)));
        }
        return new Case { Family = "矩形·大缺口", Pts = pts.ToArray(),
                          Want = StrokeKind.Rectangle, ExpectShape = true, Hard = true,
                          Note = $"左边（{h:F0}）只画了一半" };
    }

    /// <summary>椭圆：**轴对齐**（我们模型就是轴对齐的），轴比 0.45~0.87（= 1.15~2.22）。</summary>
    private static Case MakeEllipse(Random rnd)
    {
        float a = 90f + (float)rnd.NextDouble() * 220f;
        float ratio = 0.45f + (float)rnd.NextDouble() * 0.42f;         // 轴比 1.15~2.22
        float b = a * ratio;
        float gapRatio = (float)rnd.NextDouble() * 0.12f;
        float sweep = MathF.PI * 2f * (1f - gapRatio);
        var c = new Vector2(400f + (float)rnd.NextDouble() * 100f, 400f + (float)rnd.NextDouble() * 100f);
        int n = Math.Max(8, (int)(MathF.Max(a, b) * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float ang = sweep * t;
            float k = 1f + (float)(rnd.NextDouble() * 2 - 1) * 0.015f;
            pts[i] = c + new Vector2(MathF.Cos(ang) * a * k + J(rnd), MathF.Sin(ang) * b * k + J(rnd));
        }
        return new Case { Family = "椭圆", Pts = pts, Want = StrokeKind.Ellipse, ExpectShape = true,
                          Note = $"{a * 2:F0}×{b * 2:F0}（轴比 {1f / ratio:F2}）" };
    }

    /// <summary>**斜椭圆**（姿态 18~80°）：用户 2026-09-23 定的"斜椭圆也认"。
    ///
    /// 轴比刻意取强一点（1.33~2.86）：太接近圆的椭圆，"主轴朝哪"本来就是噪声，
    /// 那种形状该判成圆（判据见 ShapeRecognize.EllipseAxisRatioMin），不是这里的考题。
    /// </summary>
    private static Case MakeTiltedEllipse(Random rnd)
    {
        float a = 110f + (float)rnd.NextDouble() * 200f;
        float ratio = 0.35f + (float)rnd.NextDouble() * 0.40f;
        float b = a * ratio;
        float tilt = (18f + (float)rnd.NextDouble() * 62f) * (rnd.Next(2) == 0 ? 1f : -1f);
        float rad = tilt * MathF.PI / 180f;
        float gapRatio = (float)rnd.NextDouble() * 0.10f;
        float sweep = MathF.PI * 2f * (1f - gapRatio);
        var c = new Vector2(400f + (float)rnd.NextDouble() * 100f, 400f + (float)rnd.NextDouble() * 100f);
        int n = Math.Max(8, (int)(a * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float ang = sweep * t;
            float k = 1f + (float)(rnd.NextDouble() * 2 - 1) * 0.015f;
            var local = new Vector2(MathF.Cos(ang) * a * k, MathF.Sin(ang) * b * k);
            // 先按姿态转，再加手抖（手抖是"屏幕上的"，不该跟着形状转——真人就是手抖）
            var p = new Vector2(local.X * MathF.Cos(rad) - local.Y * MathF.Sin(rad),
                                local.X * MathF.Sin(rad) + local.Y * MathF.Cos(rad));
            pts[i] = c + p + new Vector2(J(rnd), J(rnd));
        }
        return new Case { Family = "斜椭圆", Pts = pts, Want = StrokeKind.Ellipse, ExpectShape = true,
                          Note = $"{a * 2:F0}×{b * 2:F0}、姿态 {tilt:F0}°" };
    }

    /// <summary>**斜矩形**（姿态 15~35°）：和斜椭圆同一条规矩（见 ShapeRecognize.TiltSnapDeg）。</summary>
    private static Case MakeTiltedRectangle(Random rnd)
    {
        float w = 160f + (float)rnd.NextDouble() * 300f;
        float h = 90f + (float)rnd.NextDouble() * 180f;
        float tilt = (15f + (float)rnd.NextDouble() * 20f) * (rnd.Next(2) == 0 ? 1f : -1f);
        float rad = tilt * MathF.PI / 180f;
        var c = new Vector2(400f, 400f);
        var v = new List<Vector2>
        {
            new(-w / 2, -h / 2), new(w / 2, -h / 2), new(w / 2, h / 2), new(-w / 2, h / 2)
        };
        for (int i = 0; i < 4; i++)
        {
            float x = v[i].X, y = v[i].Y;
            v[i] = c + new Vector2(x * MathF.Cos(rad) - y * MathF.Sin(rad),
                                   x * MathF.Sin(rad) + y * MathF.Cos(rad));
        }
        return new Case { Family = "斜矩形", Pts = WalkPolygon(v, rnd), Want = StrokeKind.Rectangle,
                          ExpectShape = true, Note = $"{w:F0}×{h:F0}、姿态 {tilt:F0}°" };
    }


    /// <summary>**圆角矩形**：手画矩形几乎都是圆角的（收笔来不及拐直角）。
    /// 生成办法：先按直角走一圈，再整条做多点滑动平均——角就被抹圆了，边也带一点自然弧度。</summary>
    private static Case MakeRoundedRect(Random rnd)
    {
        var c0 = MakeRectangle(rnd);
        return new Case
        {
            Family = "矩形·圆角", Pts = Smooth(c0.Pts, 5), Want = StrokeKind.Rectangle,
            ExpectShape = true, Hard = true, Note = c0.Note + "（圆角）",
        };
    }

    /// <summary>**过冲矩形**：每一笔都画过角一点再拐（真人画框就是这么画的）。
    /// 角上因此出现一个小"拐点"，正是以前角点会找偏的地方。</summary>
    private static Case MakeOvershootRect(Random rnd)
    {
        float w = 180f + (float)rnd.NextDouble() * 260f;
        float h = 110f + (float)rnd.NextDouble() * 180f;
        float tilt = (float)(rnd.NextDouble() * 2 - 1) * 7f;
        float rad = tilt * MathF.PI / 180f;
        var c = new Vector2(400f, 400f);
        var v = new List<Vector2>
        {
            new(-w / 2, -h / 2), new(w / 2, -h / 2), new(w / 2, h / 2), new(-w / 2, h / 2)
        };
        for (int i = 0; i < 4; i++)
        {
            float x = v[i].X, y = v[i].Y;
            v[i] = c + new Vector2(x * MathF.Cos(rad) - y * MathF.Sin(rad),
                                   x * MathF.Sin(rad) + y * MathF.Cos(rad));
        }
        var pts = new List<Vector2>();
        for (int i = 0; i < 4; i++)
        {
            var a = v[i];
            var b = v[(i + 1) % 4];
            var dir = Vector2.Normalize(b - a);
            float overPrev = i == 0 ? 0f : 12f + (float)rnd.NextDouble() * 22f;
            float overNext = 12f + (float)rnd.NextDouble() * 22f;
            var p0 = a - dir * overPrev;              // 上一笔冲过头的那一节
            var p1 = b + dir * overNext;              // 这一笔也冲过头
            int steps = Math.Max(4, (int)(Vector2.Distance(p0, p1) / 4f));
            for (int k = 0; k <= steps; k++)
            {
                if (i > 0 && k == 0) continue;
                pts.Add(Vector2.Lerp(p0, p1, k / (float)steps) + new Vector2(J(rnd), J(rnd)));
            }
        }
        return new Case { Family = "矩形·过冲", Pts = pts.ToArray(), Want = StrokeKind.Rectangle,
                          ExpectShape = true, Hard = true, Note = $"{w:F0}×{h:F0}、歪 {tilt:F0}°（角上过冲）" };
    }

    /// <summary>**波浪边矩形**：边不直（低频波 + 手抖）。以前这种边会长出假角点，
    /// 一个假角就够把矩形认成别的形状。</summary>
    private static Case MakeWavyEdgeRect(Random rnd)
    {
        float w = 180f + (float)rnd.NextDouble() * 240f;
        float h = 120f + (float)rnd.NextDouble() * 160f;
        var c = new Vector2(400f, 400f);
        var v = new List<Vector2>
        {
            c + new Vector2(-w / 2, -h / 2), c + new Vector2(w / 2, -h / 2),
            c + new Vector2(w / 2, h / 2), c + new Vector2(-w / 2, h / 2)
        };
        var pts = new List<Vector2>();
        for (int i = 0; i < 4; i++)
        {
            var a = v[i]; var b = v[(i + 1) % 4];
            var dir = Vector2.Normalize(b - a);
            var nrm = new Vector2(-dir.Y, dir.X);
            int steps = Math.Max(4, (int)(Vector2.Distance(a, b) / 4f));
            for (int k = 0; k <= steps; k++)
            {
                if (i > 0 && k == 0) continue;
                float t = k / (float)steps;
                float wave = MathF.Sin(t * MathF.PI) * (2.5f + (float)rnd.NextDouble() * 2f);
                pts.Add(Vector2.Lerp(a, b, t) + nrm * wave + new Vector2(J(rnd), J(rnd)));
            }
        }
        return new Case { Family = "矩形·波浪边", Pts = pts.ToArray(), Want = StrokeKind.Rectangle,
                          ExpectShape = true, Hard = true, Note = $"{w:F0}×{h:F0}（边带波）" };
    }

    /// <summary>**收口差很多的圆**（缺口 15~25%）：用户反馈的"圆也是"——
    /// 以前轴是从外接框推的，缺口一大轴就歪，圈就被拉成椭圆。</summary>
    private static Case MakeGappyCircle(Random rnd)
    {
        float r = 90f + (float)rnd.NextDouble() * 180f;
        float phase = (float)(rnd.NextDouble() * Math.PI * 2);
        float gapRatio = 0.15f + (float)rnd.NextDouble() * 0.10f;
        float sweep = MathF.PI * 2f * (1f - gapRatio);
        var c = new Vector2(400f + (float)rnd.NextDouble() * 80f, 400f + (float)rnd.NextDouble() * 80f);
        int n = Math.Max(8, (int)(r * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float rr = r * (1f + (float)(rnd.NextDouble() * 2 - 1) * 0.015f);
            float ang = phase + sweep * i / n;
            pts[i] = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rr;
        }
        return new Case { Family = "圆·大缺口", Pts = pts, Want = StrokeKind.Circle, ExpectShape = true,
                          Hard = true, Note = $"r {r:F0}、缺口 {gapRatio * 100f:F0}%" };
    }

    /// <summary>**又歪又没画满的椭圆**（用户说的"椭圆有时候识别不了"）。</summary>
    private static Case MakeGappyTiltedEllipse(Random rnd)
    {
        float a = 100f + (float)rnd.NextDouble() * 160f;
        float ratio = 0.40f + (float)rnd.NextDouble() * 0.35f;
        float b = a * ratio;
        float tilt = (20f + (float)rnd.NextDouble() * 55f) * (rnd.Next(2) == 0 ? 1f : -1f);
        float rad = tilt * MathF.PI / 180f;
        float gapRatio = 0.08f + (float)rnd.NextDouble() * 0.10f;
        float sweep = MathF.PI * 2f * (1f - gapRatio);
        var c = new Vector2(400f + (float)rnd.NextDouble() * 80f, 400f + (float)rnd.NextDouble() * 80f);
        int n = Math.Max(8, (int)(a * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float ang = sweep * t;
            float k = 1f + (float)(rnd.NextDouble() * 2 - 1) * 0.015f;
            var local = new Vector2(MathF.Cos(ang) * a * k, MathF.Sin(ang) * b * k);
            var p = new Vector2(local.X * MathF.Cos(rad) - local.Y * MathF.Sin(rad),
                                local.X * MathF.Sin(rad) + local.Y * MathF.Cos(rad));
            pts[i] = c + p + new Vector2(J(rnd), J(rnd));
        }
        return new Case { Family = "椭圆·歪+缺口", Pts = pts, Want = StrokeKind.Ellipse, ExpectShape = true,
                          Hard = true, Note = $"{a * 2:F0}×{b * 2:F0}、姿态 {tilt:F0}°、缺口 {gapRatio * 100f:F0}%" };
    }

    /// <summary>整条点列做多点滑动平均（把尖角抹圆、把折线抹顺）。</summary>
    private static Vector2[] Smooth(Vector2[] pts, int window)
    {
        var outp = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            var s = Vector2.Zero;
            int n = 0;
            for (int k = -window; k <= window; k++)
            {
                int j = Math.Clamp(i + k, 0, pts.Length - 1);
                s += pts[j]; n++;
            }
            outp[i] = s / n;
        }
        return outp;
    }

    /// <summary>三角形：三个随机顶点，逐边画、**角上过冲一点**（真人画三角形都会画过）。</summary>
    private static Case MakeTriangle(Random rnd)
    {
        var v = RandomPolygon(rnd, 3, 150f, 380f);
        return new Case { Family = "三角形", Pts = WalkPolygon(v, rnd), Want = StrokeKind.Triangle,
                          ExpectShape = true, Note = "3 顶点" };
    }

    /// <summary>矩形：**倾斜 ≤ 7°**（超过 10° 我们这一版不认，见 ShapeRecognize.MaxAxisTiltDeg）。</summary>
    private static Case MakeRectangle(Random rnd)
    {
        float w = 140f + (float)rnd.NextDouble() * 300f;
        float h = 90f + (float)rnd.NextDouble() * 220f;
        float tilt = (float)(rnd.NextDouble() * 2 - 1) * 7f;
        var c = new Vector2(400f, 400f);
        var v = new List<Vector2>
        {
            new(-w / 2, -h / 2), new(w / 2, -h / 2), new(w / 2, h / 2), new(-w / 2, h / 2)
        };
        float rad = tilt * MathF.PI / 180f;
        for (int i = 0; i < 4; i++)
        {
            float x = v[i].X, y = v[i].Y;
            v[i] = c + new Vector2(x * MathF.Cos(rad) - y * MathF.Sin(rad),
                                   x * MathF.Sin(rad) + y * MathF.Cos(rad));
        }
        return new Case { Family = "矩形", Pts = WalkPolygon(v, rnd), Want = StrokeKind.Rectangle,
                          ExpectShape = true, Note = $"{w:F0}×{h:F0}、歪 {tilt:F1}°" };
    }

    /// <summary>平行四边形：任意方向（模型允许任意三个顶点），邻边夹角 40~70°。
    ///
    /// ⚠ 夹角**刻意避开 90° ± 15° 那一段**：那个范围里的四边形"本来就该判成矩形"
    /// （差十几度，肉眼和判据都当矩形），把它算成"平行四边形识别失败"是在测一个
    /// **没有正确答案的题**——这正是"别把'我以为的设计'写成期望"那条教训。
    /// 判据该在哪一刀切，写在 <see cref="ShapeRecognize.RightAngleTolDeg"/> 的注释里。
    /// </summary>
    private static Case MakeParallelogram(Random rnd)
    {
        float baseLen = 150f + (float)rnd.NextDouble() * 250f;
        float sideLen = 90f + (float)rnd.NextDouble() * 180f;
        float dir = rnd.Next(2) == 0 ? 1f : -1f;                       // 两边都试：斜向左 / 斜向右
        float skew = (40f + (float)rnd.NextDouble() * 30f) * MathF.PI / 180f * dir;
        var c = new Vector2(400f, 400f);
        var u = new Vector2(baseLen, 0f);
        var w = new Vector2(MathF.Cos(skew), MathF.Sin(skew)) * sideLen;
        var v = new List<Vector2>
        {
            c - u * 0.5f - w * 0.5f, c + u * 0.5f - w * 0.5f,
            c + u * 0.5f + w * 0.5f, c - u * 0.5f + w * 0.5f
        };
        return new Case { Family = "平行四边形", Pts = WalkPolygon(v, rnd), Want = StrokeKind.Parallelogram,
                          ExpectShape = true, Note = $"邻边夹角 {MathF.Abs(skew) * 180f / MathF.PI:F0}°" };
    }

    /// <summary>半圆弧：残差同样很小，但**没画满一圈**，必须不认。</summary>
    private static Case MakeHalfArc(Random rnd)
        => ArcCase(rnd, "半圆弧", 180f);

    private static Case MakeQuarterArc(Random rnd)
        => ArcCase(rnd, "四分之一弧", 90f);

    private static Case ArcCase(Random rnd, string fam, float sweepDeg)
    {
        float r = 120f + (float)rnd.NextDouble() * 180f;
        float start = (float)(rnd.NextDouble() * Math.PI * 2);
        float sweep = sweepDeg * MathF.PI / 180f;
        var c = new Vector2(400f, 400f);
        int n = Math.Max(6, (int)(r * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float ang = start + sweep * i / n;
            pts[i] = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r + new Vector2(J(rnd), J(rnd));
        }
        return new Case { Family = fam, Pts = pts, Want = StrokeKind.Freehand, ExpectShape = false,
                          Note = $"r {r:F0}、{sweepDeg:F0}°" };
    }

    /// <summary>S 形：两段反向的弧接起来。它既不直、也不闭合——什么都不该认出来。</summary>
    private static Case MakeSShape(Random rnd)
    {
        int n = 60;
        var pts = new Vector2[n + 1];
        float h = 200f + (float)rnd.NextDouble() * 150f;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float x = 400f + MathF.Sin(t * MathF.PI * 2f) * 60f;
            float y = 300f + h * t;
            pts[i] = new Vector2(x + J(rnd), y + J(rnd));
        }
        return new Case { Family = "S 形", Pts = pts, Want = StrokeKind.Freehand, ExpectShape = false, Note = "" };
    }

    /// <summary>
    /// **正弦 / 余弦**（§43.4.4 三）：**一个周期**，从**特征相位**起笔
    /// （正弦从零点、余弦从峰/谷）—— 模型的正弦起点只能落在这些点上（见 `TryFitWave`），
    /// 所以语料必须按模型的"落笔方式"造，否则量的是"我造了一个它表达不了的相位"。
    /// ⚠ 起笔相位**故意留 ±4° 的小偏差**（真人不会刚好压在零点上）——
    ///   容差窗口的实测在 `--inktest` 里另一张表（`TryFitWave` 的注释里有那张表）。
    /// </summary>
    private static Case MakeWaveOne(Random rnd, bool cosine)
    {
        int n = 96;
        var pts = new Vector2[n + 1];
        float amp = 50f + (float)rnd.NextDouble() * 80f;
        float period = 150f + (float)rnd.NextDouble() * 150f;
        float ph0 = (float)(rnd.NextDouble() * 2 - 1) * 4f * MathF.PI / 180f;
        float x0 = 300f, y0 = 400f;
        var start = new Vector2(x0, y0);
        var end = new Vector2(x0 + period, y0 + (cosine ? 2f : 1f) * amp);
        for (int i = 0; i <= n; i++)
        {
            float u = i / (float)n;
            pts[i] = new Vector2(x0 + period * u + J(rnd),
                                 Stroke.WaveYAt(start, end, cosine ? StrokeKind.Cosine : StrokeKind.Sine,
                                                u + ph0 / MathF.Tau) + J(rnd));
        }
        return new Case
        {
            Family = cosine ? "余弦" : "正弦",
            Pts = pts,
            Want = cosine ? StrokeKind.Cosine : StrokeKind.Sine,
            ExpectShape = true,
            Note = $"周期 {period:F0}、振幅 {amp:F0}、起笔相位 {ph0 * 180f / MathF.PI:F1}°",
        };
    }

    /// <summary>
    /// **波浪线**（多个周期，写回 `StrokeKind.Wave`）：周期和振幅**没有固定比例**，
    /// 而周期是对象的**第三个定义元素**（`Stroke.SetWavePeriod`），所以能忠实还原。
    ///
    /// ⚠ **它以前是反例**（"模型表达不了多周期，所以不认"）—— 那是旧口径。
    /// 起笔相位**故意放开到 ±半个周期**：多周期这条路**天生不受相位限制**
    ///（长度自由，起点挪到零点之后把长度补到右端就行）—— 这是它和正弦 / 余弦最大的区别。
    /// </summary>
    private static Case MakeWaveMulti(Random rnd)
    {
        int n = 200;
        var pts = new Vector2[n + 1];
        float amp = 35f + (float)rnd.NextDouble() * 55f;
        float period = 70f + (float)rnd.NextDouble() * 90f;
        float cycles = 1.5f + (float)rnd.NextDouble() * 4.5f;
        float len = period * cycles;
        float ph0 = (float)(rnd.NextDouble() - 0.5) * MathF.PI;
        float x0 = 300f, y0 = 400f;
        var start = new Vector2(x0, y0);
        var end = new Vector2(x0 + len, y0 + amp);
        for (int i = 0; i <= n; i++)
        {
            float u = cycles * i / n;
            pts[i] = new Vector2(x0 + period * u + J(rnd),
                                 Stroke.WaveYAt(start, end, StrokeKind.Wave, u + ph0 / MathF.Tau) + J(rnd));
        }
        return new Case
        {
            Family = "波浪线",
            Pts = pts,
            Want = StrokeKind.Wave,
            ExpectShape = true,
            Note = $"周期 {period:F0}、振幅 {amp:F0}、{cycles:F1} 个周期、起笔相位 {ph0 * 180f / MathF.PI:F0}°",
        };
    }

    /// <summary>**半周期正弦**（一个"拱"）：`WaveMinCycles = 0.90` 那道门槛的挡箭牌 ——
    /// 它本身确实躺在某个完整正弦的曲线上，但**只画了半个周期**，不该被补成整个周期。</summary>
    private static Case MakeHalfSine(Random rnd)
    {
        int n = 60;
        var pts = new Vector2[n + 1];
        float amp = 60f + (float)rnd.NextDouble() * 70f;
        float len = 200f + (float)rnd.NextDouble() * 160f;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            pts[i] = new Vector2(300f + len * t + J(rnd),
                                 400f + amp * MathF.Sin(t * MathF.PI) + J(rnd));
        }
        return new Case { Family = "半周期正弦", Pts = pts, Want = StrokeKind.Freehand,
                          ExpectShape = false, Note = "" };
    }

    /// <summary>
    /// **三角波**（折线型的波）：看着像波，但它是**一段段直线**接起来的。
    ///
    /// ⚠ **实测 20/20 全被判成波浪线，而且是分不开的**：三角波的三次谐波只有基波的
    /// **1/9**（`8/π²·Σ sin/(2k+1)²`），"最好的正弦"和它的最大偏差只有
    /// **≈10.5% 的振幅** —— 而**手写的正弦本身**也就这个量级的偏差。卡紧到能分开的程度，
    /// 真实手写的正弦会一起毙掉（那是更糟的错）。
    ///
    /// 所以它按**难例**处理（"认对或不认都行、**不许认成别的形状**"），期望填 `Wave`。
    /// ⚠ 如果以后用户说"锯齿波不该变成光滑的波"，那得给这一族加一条**独立的**判据
    ///（§43.4.3 第 3 步要求的那种），候选是"**曲率的集中度**"—— 要单独量一轮。
    /// </summary>
    private static Case MakeTriangleWave(Random rnd)
    {
        int n = 160;
        var pts = new Vector2[n + 1];
        float amp = 45f + (float)rnd.NextDouble() * 55f;
        float period = 120f + (float)rnd.NextDouble() * 100f;
        float cycles = 2f + (float)rnd.NextDouble() * 2f;
        float len = period * cycles;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float u = cycles * t;
            float tri = MathF.Abs(2f * (u - MathF.Floor(u + 0.5f)));
            pts[i] = new Vector2(300f + len * t + J(rnd), 400f + amp * (2f * tri - 1f) + J(rnd));
        }
        return new Case { Family = "三角波", Pts = pts, Want = StrokeKind.Wave,
                          ExpectShape = true, Hard = true, Note = "" };
    }

    /// <summary>折线：一个折角、两段各一百多像素。开放形状里"两个角"不在库里。</summary>
    private static Case MakePolyline(Random rnd)
    {
        float l = 130f + (float)rnd.NextDouble() * 120f;
        float ang = (60f + (float)rnd.NextDouble() * 60f) * MathF.PI / 180f;
        var a = new Vector2(300f, 300f);
        var b = a + new Vector2(l, 0f);
        var c = b + new Vector2(MathF.Cos(MathF.PI - ang), MathF.Sin(MathF.PI - ang)) * l;
        var pts = new List<Vector2>();
        for (int i = 0; i <= 20; i++) pts.Add(Vector2.Lerp(a, b, i / 20f) + new Vector2(J(rnd), J(rnd)));
        for (int i = 1; i <= 20; i++) pts.Add(Vector2.Lerp(b, c, i / 20f) + new Vector2(J(rnd), J(rnd)));
        return new Case { Family = "折线", Pts = pts.ToArray(), Want = StrokeKind.Freehand, ExpectShape = false, Note = "" };
    }

    /// <summary>涂鸦：平滑随机游走。它连"笔迹"都不算，更不该被认成图形。</summary>
    private static Case MakeScribble(Random rnd)
    {
        int n = 90;
        var pts = new Vector2[n];
        var p = new Vector2(400f, 400f);
        float dir = (float)(rnd.NextDouble() * Math.PI * 2);
        for (int i = 0; i < n; i++)
        {
            dir += (float)(rnd.NextDouble() - 0.5) * 1.1f;      // 每步转一点点 = 平滑的乱走
            p += new Vector2(MathF.Cos(dir), MathF.Sin(dir)) * 7f;
            pts[i] = p;
        }
        return new Case { Family = "涂鸦", Pts = pts, Want = StrokeKind.Freehand, ExpectShape = false, Note = "" };
    }

    /// <summary>点 / 极短笔画：短于门槛，一律不动（标点、部首都属于这一档）。</summary>
    private static Case MakeDot(Random rnd)
    {
        float l = 8f + (float)rnd.NextDouble() * 24f;
        int n = 6;
        var a = new Vector2(400f, 400f);
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
            pts[i] = a + new Vector2(l * i / n, J(rnd));
        return new Case { Family = "点/短笔画", Pts = pts, Want = StrokeKind.Freehand, ExpectShape = false,
                          Note = $"长 {l:F0}" };
    }

    /// <summary>汉字"横"：本来就该被认成直线（这一档不断言，只统计）。</summary>
    private static Case MakeHorizontal(Random rnd)
        => HandWriting(rnd, "汉字·横", new Vector2(1f, 0f), 70f, 200f);

    /// <summary>汉字"竖"：同上。</summary>
    private static Case MakeVertical(Random rnd)
        => HandWriting(rnd, "汉字·竖", new Vector2(0f, 1f), 100f, 250f);

    private static Case HandWriting(Random rnd, string fam, Vector2 dir, float minLen, float maxLen)
    {
        float len = minLen + (float)rnd.NextDouble() * (maxLen - minLen);
        var nrm = new Vector2(-dir.Y, dir.X);
        var a = new Vector2(400f, 400f);
        int n = Math.Max(3, (int)(len / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            pts[i] = a + dir * (len * t) + nrm * (len * 0.01f * MathF.Sin(MathF.PI * t))
                     + new Vector2(J(rnd), J(rnd));
        }
        return new Case { Family = fam, Pts = pts, Want = StrokeKind.Line, ExpectShape = false,
                          Informational = true, Note = $"长 {len:F0}" };
    }

    /// <summary>竖弯钩：带小钩的竖笔——**直线型笔画**，归到"只报告"那一档（见 Case.Informational）。</summary>
    private static Case MakeHook(Random rnd)
    {
        float v = 150f + (float)rnd.NextDouble() * 100f;
        float hook = 25f + (float)rnd.NextDouble() * 20f;
        var a = new Vector2(400f, 250f);
        var b = a + new Vector2(0f, v);
        var c = b + new Vector2(hook, hook * 0.5f);
        var pts = new List<Vector2>();
        for (int i = 0; i <= 24; i++) pts.Add(Vector2.Lerp(a, b, i / 24f) + new Vector2(J(rnd), J(rnd)));
        for (int i = 1; i <= 8; i++) pts.Add(Vector2.Lerp(b, c, i / 8f) + new Vector2(J(rnd), J(rnd)));
        return new Case { Family = "汉字·竖弯钩", Pts = pts.ToArray(), Want = StrokeKind.Freehand,
                          ExpectShape = false, Informational = true, Note = $"钩 {hook:F0}" };
    }

    // =====================================================================
    //  语料工具
    // =====================================================================

    /// <summary>围绕中心撒三个（或更多）顶点，保证不太退化（边长与夹角都有下限）。</summary>
    private static List<Vector2> RandomPolygon(Random rnd, int count, float minR, float maxR)
    {
        var c = new Vector2(400f, 400f);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var v = new List<Vector2>();
            for (int i = 0; i < count; i++)
            {
                float ang = (float)(rnd.NextDouble() * Math.PI * 2);
                float r = minR + (float)rnd.NextDouble() * (maxR - minR);
                v.Add(c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r);
            }
            // 太扁 / 太钝的一律重撒（那种"三角形"和一条线没区别）。
            // 下限定在 35°：识别器的角点门槛是"转向 ≥ 28°"，也就是内角 ≤ 152°——
            // 合成语料得落在**这个判据能覆盖的范围内**，不然测的是"语料太刁"而不是"识别器不行"。
            bool ok = true;
            for (int i = 0; i < count; i++)
            {
                float ang = AngleAt(v[(i + count - 1) % count], v[i], v[(i + 1) % count]);
                if (ang < 35f || ang > 145f) { ok = false; break; }
            }
            if (ok) return v;
        }
        return new List<Vector2> { c + new Vector2(-100f, -100f), c + new Vector2(100f, -100f), c + new Vector2(0f, 100f) };
    }

    private static float AngleAt(Vector2 a, Vector2 b, Vector2 c)
    {
        var u = a - b; var v = c - b;
        if (u.Length() < 1e-4f || v.Length() < 1e-4f) return 180f;
        return MathF.Acos(Math.Clamp(Vector2.Dot(u, v) / (u.Length() * v.Length()), -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>沿着多边形的边一笔画完（首尾各过冲一点点，真人就是这么收口的）。</summary>
    private static Vector2[] WalkPolygon(List<Vector2> v, Random rnd)
    {
        var pts = new List<Vector2>();
        for (int i = 0; i < v.Count; i++)
        {
            var a = v[i];
            var b = v[(i + 1) % v.Count];
            var dir = Vector2.Normalize(b - a);
            int steps = Math.Max(4, (int)(Vector2.Distance(a, b) / 4f));
            for (int k = 0; k <= steps; k++)
            {
                if (i > 0 && k == 0) continue;               // 上一段的末点就是这一段的起点
                var p = Vector2.Lerp(a, b, k / (float)steps);
                p += Vector2.Normalize(new Vector2(-dir.Y, dir.X)) * J(rnd) + new Vector2(J(rnd), J(rnd));
                pts.Add(p);
            }
        }
        return pts.ToArray();
    }

    // =====================================================================
    //  多笔语料
    // =====================================================================

    /// <summary>四条边**分四笔**画的矩形（角上故意留一点缝，模仿真人的收口）。</summary>
    private static List<IReadOnlyList<Vector2>> FourStrokeRectangle(Random rnd, out string note)
    {
        float w = 260f, h = 170f;
        var c = new Vector2(400f, 400f);
        var v = new List<Vector2>
        {
            c + new Vector2(-w / 2, -h / 2), c + new Vector2(w / 2, -h / 2),
            c + new Vector2(w / 2, h / 2), c + new Vector2(-w / 2, h / 2)
        };
        float gap = 8f;                                   // 每个角上留 8 像素的缝
        var parts = new List<IReadOnlyList<Vector2>>();
        for (int i = 0; i < 4; i++)
        {
            var a = v[i]; var b = v[(i + 1) % 4];
            var dir = Vector2.Normalize(b - a);
            var p0 = a + dir * gap;
            var p1 = b - dir * gap;
            int steps = Math.Max(4, (int)(Vector2.Distance(p0, p1) / 4f));
            var seg = new Vector2[steps + 1];
            for (int k = 0; k <= steps; k++) seg[k] = Vector2.Lerp(p0, p1, k / (float)steps) + new Vector2(J(rnd), J(rnd));
            parts.Add(seg);
        }
        note = $"{w:F0}×{h:F0}、角上留缝 {gap:F0} px";
        return parts;
    }

    /// <summary>双曲线的两支（分两笔）。**两支之间接不上**，所以正确答案是"什么都不认"。</summary>
    private static List<IReadOnlyList<Vector2>> TwoBranchHyperbola(Random rnd)
    {
        float a = 90f, b = 60f;
        var c = new Vector2(400f, 400f);
        var parts = new List<IReadOnlyList<Vector2>>();
        foreach (var sign in new[] { 1f, -1f })
        {
            var pts = new List<Vector2>();
            for (int i = 0; i <= 40; i++)
            {
                float t = -1f + 2f * i / 40f;
                pts.Add(c + new Vector2(sign * a * MathF.Cosh(t), b * MathF.Sinh(t))
                          + new Vector2(J(rnd), J(rnd)));
            }
            parts.Add(pts.ToArray());
        }
        return parts;
    }

    // =====================================================================
    //  两笔 → 双曲线：语料与辅助量（2026-09-26 三轮实测用的）
    // =====================================================================

    /// <summary>
    /// **真手画的两支**（用户报"画几次才成一次"时加的语料）。
    ///
    /// ⚠ 上面那条 `TwoBranchHyperbola` 是**完美对称 + ±0.8px 抖动**，它**恒过** ——
    /// 拿它当"两支"的用例，等于在量"我自己造的完美图形"，真机的识别率一个字都看不出来。
    /// `level` 是三档"第二支和第一支差多远"：
    ///   · **0 一般手画**：实半轴相同、长度 0.55~1.35 倍、开口 0.75~1.25 倍、
    ///     中心差 ±20px、手抖 3.5px；
    ///   · **1 很草**（用户："手画出一个比较对称的图形还是比较难的"）：实半轴 ±30%、
    ///     长度 0.35~1.6 倍、开口 0.5~1.7 倍、中心差 ±45px、手抖 6.5px、**转 ±7°**；
    ///   · **2 潦草**（用户："连续两段反弧线就识别为双曲线"）：实半轴 0.5~1.6 倍、
    ///     长度 0.3~1.8 倍、开口 0.4~2.0 倍、中心差 ±80px、手抖 9px、**转 ±15°** ——
    ///     这一档就是"**我只求画出两段反弧这个意向**"，不追求画得像。
    /// </summary>
    private static (Vector2[] a, Vector2[] b, Vector2 center) HandTwoBranches(Random rnd, int level)
    {
        bool transX = rnd.Next(2) == 0;
        float aLen = 45f + (float)rnd.NextDouble() * 70f;
        float slope = 0.8f + (float)rnd.NextDouble() * 1.0f;
        float tA = 1.2f + (float)rnd.NextDouble() * 0.9f;
        float aScale = level == 0 ? 1f
                     : level == 1 ? 0.70f + (float)rnd.NextDouble() * 0.60f
                                  : 0.50f + (float)rnd.NextDouble() * 1.10f;
        float tScale = level == 0 ? 0.55f + (float)rnd.NextDouble() * 0.80f
                     : level == 1 ? 0.35f + (float)rnd.NextDouble() * 1.25f
                                  : 0.30f + (float)rnd.NextDouble() * 1.50f;
        float sScale = level == 0 ? 0.75f + (float)rnd.NextDouble() * 0.50f
                     : level == 1 ? 0.50f + (float)rnd.NextDouble() * 1.20f
                                  : 0.40f + (float)rnd.NextDouble() * 1.60f;
        float offMax = level == 0 ? 20f : level == 1 ? 45f : 80f;
        float wob = level == 0 ? 3.5f : level == 1 ? 6.5f : 9f;
        float rotB = level == 0 ? 0f
                   : level == 1 ? ((float)rnd.NextDouble() - 0.5f) * 0.24f
                                : ((float)rnd.NextDouble() - 0.5f) * 0.52f;
        var o = new Vector2(600f + (float)rnd.NextDouble() * 200f,
                            600f + (float)rnd.NextDouble() * 200f);
        var oB = o + new Vector2((float)(rnd.NextDouble() - 0.5) * 2f * offMax,
                                 (float)(rnd.NextDouble() - 0.5) * 2f * offMax);
        float bLen = aLen * slope;
        return (HyperBranch(o, aLen, bLen, tA, +1, transX, wob, 0f, rnd),
                HyperBranch(oB, aLen * aScale, bLen * sScale, tA * tScale, -1, transX, wob, rotB, rnd),
                o);
    }

    /// <summary>
    /// **造一支双曲线**：`x = ±a·cosh t`、`y = b·sinh t`（或上下开口），带低频手抖 ＋ 高频噪声。
    /// `rot` = "这一支自己转了多少"（手画第二支的方向不会和第一支严格相反）。
    /// 两支共用这一个函数：**造语料的算式不许抄两份**（抄两份就会一边改一边没改）。
    /// </summary>
    private static Vector2[] HyperBranch(Vector2 c, float a, float b, float tMax, int sign,
                                         bool transX, float wob, float rot, Random rnd)
    {
        int n = 56;
        var pts = new Vector2[n + 1];
        var perp = transX ? new Vector2(0f, 1f) : new Vector2(1f, 0f);
        for (int i = 0; i <= n; i++)
        {
            float t = (-1f + 2f * i / n) * tMax;
            float cu = a * MathF.Cosh(t);
            float cv = b * MathF.Sinh(t);
            var local = transX ? new Vector2(sign * cu, cv) : new Vector2(cv, sign * cu);
            if (rot != 0f)
                local = new Vector2(local.X * MathF.Cos(rot) - local.Y * MathF.Sin(rot),
                                    local.X * MathF.Sin(rot) + local.Y * MathF.Cos(rot));
            float ph = i / (float)n * MathF.PI * 2f;
            pts[i] = c + local + perp * (wob * MathF.Sin(ph))
                       + new Vector2(J(rnd) * 2f, J(rnd) * 2f);
        }
        return pts;
    }

    /// <summary>**同一支画两遍**（两支都在右边、位置差一点）：笔数对了，但它们**不是
    /// 一个双曲线的两支** —— 属于"必须挡住"的那一类。
    /// ⚠ 第一版图省事直接拿 `HandTwoBranches` 的两支来当"同一边"，**忘了把第二支的 sign
    ///   翻成同侧** —— 于是它其实是**正例**，量出来的"误报 45%"是假的
    ///（和正例的 53% 正好互补，一眼就该看出来）。造语料时**符号也要对着**。</summary>
    private static (Vector2[] a, Vector2[] b) TwoBranchesSameSide(Random rnd)
    {
        bool transX = rnd.Next(2) == 0;
        float aLen = 45f + (float)rnd.NextDouble() * 70f;
        float slope = 0.8f + (float)rnd.NextDouble() * 1.0f;
        float tMax = 1.2f + (float)rnd.NextDouble() * 0.9f;
        var o = new Vector2(600f + (float)rnd.NextDouble() * 200f,
                            600f + (float)rnd.NextDouble() * 200f);
        var oB = o + new Vector2((float)(rnd.NextDouble() - 0.5) * 60f,
                                 (float)(rnd.NextDouble() - 0.5) * 60f);
        return (HyperBranch(o, aLen, aLen * slope, tMax, +1, transX, 3.5f, 0f, rnd),
                HyperBranch(oB, aLen, aLen * slope, tMax, +1, transX, 3.5f, 0f, rnd));
    }

    /// <summary>**两支完全不相干**：一支双曲线 ＋ 一条半圆弧。这是"两笔配对"最该挡住的那类。
    /// ⚠ 别拿"两支中心离得很远"当反例 —— 中心远 + 支画得短，**本来就可以是一条双曲线的
    ///   两支**（`a` 大、`t` 小），那种"误报"多半是**认对了**（第一版就踩了这个）。</summary>
    private static (Vector2[] a, Vector2[] b) TwoBranchesUnrelated(Random rnd)
    {
        bool transX = rnd.Next(2) == 0;
        float aLen = 45f + (float)rnd.NextDouble() * 70f;
        float slope = 0.8f + (float)rnd.NextDouble() * 1.0f;
        float tMax = 1.2f + (float)rnd.NextDouble() * 0.9f;
        var o = new Vector2(600f + (float)rnd.NextDouble() * 200f,
                            600f + (float)rnd.NextDouble() * 200f);
        var a = HyperBranch(o, aLen, aLen * slope, tMax, +1, transX, 3.5f, 0f, rnd);
        int n = 40;
        var b = new Vector2[n + 1];
        float r = 80f + (float)rnd.NextDouble() * 120f;
        var c2 = new Vector2(900f + (float)rnd.NextDouble() * 200f,
                             700f + (float)rnd.NextDouble() * 200f);
        float a0 = (float)(rnd.NextDouble() * Math.PI * 2);
        for (int i = 0; i <= n; i++)
            b[i] = c2 + new Vector2(MathF.Cos(a0 + MathF.PI * i / n), MathF.Sin(a0 + MathF.PI * i / n)) * r
                      + new Vector2(J(rnd), J(rnd));
        return (a, b);
    }

    /// <summary>
    /// **一个椭圆分两笔**（右半弧 ＋ 左半弧）：这一对**也是"两段反弧"** ✗，
    /// 但它**绝不能**变成双曲线 —— 它是"反弧"这条新判据**最危险的误报对手**（2026-09-26 加）。
    /// 挡住它的是 `TryFitConic` 里那条**判别式**：两笔并起来落在**椭圆**上（`a·c > 0`）✓。
    /// 所以这张表里它必须一直是 **0%**。
    /// </summary>
    private static (Vector2[] a, Vector2[] b) TwoHalfEllipse(Random rnd)
    {
        float a = 140f + (float)rnd.NextDouble() * 120f;
        float ratio = 0.45f + (float)rnd.NextDouble() * 0.40f;
        float b = a * ratio;
        float rad = (float)(rnd.NextDouble() * 80 - 40) * MathF.PI / 180f;
        var c = new Vector2(700f + (float)rnd.NextDouble() * 200f,
                            700f + (float)rnd.NextDouble() * 200f);
        Vector2 At(float ang)
        {
            var local = new Vector2(MathF.Cos(ang) * a, MathF.Sin(ang) * b);
            return c + new Vector2(local.X * MathF.Cos(rad) - local.Y * MathF.Sin(rad),
                                   local.X * MathF.Sin(rad) + local.Y * MathF.Cos(rad))
                     + new Vector2(J(rnd), J(rnd));
        }
        int n = 40;
        var half1 = new Vector2[n + 1];      // 右半弧（−90° → +90°）
        var half2 = new Vector2[n + 1];      // 左半弧（+90° → +270°）
        for (int i = 0; i <= n; i++)
        {
            half1[i] = At(-MathF.PI / 2 + MathF.PI * i / n);
            half2[i] = At(MathF.PI / 2 + MathF.PI * i / n);
        }
        return (half1, half2);
    }

    /// <summary>
    /// **读用户手画的样本**（`--inkfile &lt;文件&gt;`，配合 `--recink` 录出来的那份）。
    ///
    /// 用户 2026-09-26 的想法："**我手画多少条双曲线给你，你根据这些双曲线来定制一个方案**"
    /// —— 真手画的样本是合成语料造不出来的，所以**这条路的结论优先于**那些语料
    ///（这一轮的合成语料已经错两次：一次漏画一条边、一次反例的 `sign` 写反）。
    ///
    /// 配对口径：**连续两笔 = 一条双曲线**（他画的时候两笔是连着画的）。
    /// 每一对打三样东西：
    ///   ① 两笔的**独立读数**（点数 / 拱起方向**是否互指**）；
    ///   ② **两笔一起**判（`TryTwoBranchHyperbola`：认成什么、卡在哪一道闸、差多少）；
    ///   ③ **单笔各自**判（看"一支单独会不会被认成抛物线"——那决定配对时手上还有没有原迹）。
    /// </summary>
    internal static int RunInkFile(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine($"  没有这个文件：{path}"); return 1; }
        var strokes = new List<(int seq, long t, string guess, List<Vector2> pts)>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("--- stroke"))
            {
                var sp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long t = 0;
                foreach (var part in sp) if (part.StartsWith("t=")) long.TryParse(part[2..], out t);
                int g = line.IndexOf("guess=", StringComparison.Ordinal);
                strokes.Add((int.Parse(sp[2]), t, g >= 0 ? line[(g + 6)..] : "", new List<Vector2>()));
            }
            else if (line.Length > 0 && strokes.Count > 0)
            {
                var c = line.Split(',');
                if (c.Length == 2 && float.TryParse(c[0], out var x) && float.TryParse(c[1], out var y))
                    strokes[^1].pts.Add(new Vector2(x, y));
            }
        }
        Console.WriteLine("  ── 用户手画的样本 ──");
        Console.WriteLine($"  文件：{path}");
        Console.WriteLine($"  共 {strokes.Count} 笔；每笔点数："
                          + string.Join(" ", strokes.Select(s => s.pts.Count)));
        Console.WriteLine();
        int pairs = 0, pairOk = 0;
        var blockWhy = new Dictionary<string, int>();
        for (int i = 0; i + 1 < strokes.Count; i += 2)
        {
            var A = strokes[i].pts.ToArray();
            var B = strokes[i + 1].pts.ToArray();
            if (A.Length < 8 || B.Length < 8) continue;         // `--recink` 兜底录的空段
            pairs++;
            float dt = (strokes[i + 1].t - strokes[i].t) / 1000f;
            var bulgeA = ShapeRecognize.BulgeDirForTest(A);
            var bulgeB = ShapeRecognize.BulgeDirForTest(B);
            var gap = ShapeRecognize.CentroidForTest(B) - ShapeRecognize.CentroidForTest(A);
            if (gap.LengthSquared() < 1e-6f) gap = new Vector2(1f, 0f);
            var dir = Vector2.Normalize(gap);
            float dotA = Vector2.Dot(bulgeA, dir), dotB = Vector2.Dot(bulgeB, -dir);
            var both = ShapeRecognize.TryTwoBranchHyperbola(A, B, 1f);
            var gA = ShapeRecognize.Recognize(A, 1f);
            var gB = ShapeRecognize.Recognize(B, 1f);
            if (both.Kind == StrokeKind.Hyperbola) pairOk++;
            else { string k = WhyGroup(both.Rule); blockWhy[k] = blockWhy.TryGetValue(k, out var c0) ? c0 + 1 : 1; }
            bool pointing = dotA > 0.2f && dotB > 0.2f;
            // 两支的**尺寸比**（小/大）：这是"两笔是不是一个双曲线的两支"最硬的一条读数
            // —— 若一支只有另一支的 1/7（比如"一个没成型的小圆"配一条长直线），
            // 那无论镜像贴得多准都不是同一个双曲线。定"尺寸比闸"就是靠这一列。
            float dA = DiagOf(A), dB = DiagOf(B);
            float ratio = MathF.Min(dA, dB) / MathF.Max(1f, MathF.Max(dA, dB));
            Console.WriteLine($"  第 {pairs} 对（笔 {strokes[i].seq}＋{strokes[i + 1].seq}、"
                              + $"间隔 {dt:F1}s、{A.Length}/{B.Length} 点、"
                              + $"尺寸 {dA:F0}/{dB:F0} = 比 {ratio:F2}）");
            Console.WriteLine($"     拱向互指？ A·d={dotA,5:F2}  B·(−d)={dotB,5:F2}  "
                              + (pointing ? "是 ✓" : "**不是** ✗")
                              + $"（A 拱 {Fmt(bulgeA)}、B 拱 {Fmt(bulgeB)}）");
            Console.WriteLine($"     两笔一起：{(both.Kind == StrokeKind.Hyperbola ? "**认成双曲线 ✓**" : "没认出来")}"
                              + $"  {Short(both.Rule)}");
            Console.WriteLine($"     单笔各自：A → {gA.Kind}（{Short(gA.Rule)}）");
            Console.WriteLine($"               B → {gB.Kind}（{Short(gB.Rule)}）");
            Console.WriteLine($"     软件当时记的 guess：{strokes[i].guess}  |  {strokes[i + 1].guess}");
        }
        Console.WriteLine();
        Console.WriteLine($"  合计：{pairs} 对，两笔认定成功 {pairOk} 对"
                          + $"（{pairOk * 100f / Math.Max(1, pairs):F0}%）");
        foreach (var kv in blockWhy.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"     卡在：{kv.Key} ×{kv.Value}");
        return 0;

        static string Fmt(Vector2 v) => v == Vector2.Zero ? "太直" : $"({v.X:F2},{v.Y:F2})";
        static string Short(string s) => s.Length > 76 ? s[..76] + "…" : s;
        static string WhyGroup(string rule)
            => rule.Contains("大小差太多") ? "两支尺寸差太多（不像同一个双曲线）"
             : rule.Contains("不像同一个双曲线") ? "两笔不够对称 / 不是反弧"
             : rule.Contains("判别式") ? "圆锥拟合说它不是双曲线"
             : rule.Contains("贴不住") ? "圆锥曲线贴不住墨迹"
             : rule.Contains("太短") ? "有一笔太短"
             : rule.Length > 24 ? rule[..24] : rule;
    }

    /// <summary>**独立的对称性读数**（用**真实中心**，不是识别器估的那个）：量出"手画的一对
    /// 到底有多对称"，才知道容差该放哪儿。识别器估的中心会略偏，所以它量到的数比这里
    /// 略大 —— 这张表给的是**下界**。
    /// ⚠ **四个方向取最小**（和识别器同一条口径：**拿短的那支当查询**）—— 不然
    ///   "一支画长一支画短"会被算成"不对称"，量出来的分布是假的。</summary>
    private static float SymmetryRatio(Vector2[] a, Vector2[] b, Vector2 center)
    {
        var mirror = new Vector2[a.Length];
        for (int k = 0; k < a.Length; k++) mirror[k] = 2f * center - a[a.Length - 1 - k];
        var mirrorFwd = new Vector2[a.Length];
        for (int k = 0; k < a.Length; k++) mirrorFwd[k] = 2f * center - a[k];
        float d = MathF.Min(MathF.Min(DevToPolyline(mirror, b), DevToPolyline(mirrorFwd, b)),
                            MathF.Min(DevToPolyline(b, mirror), DevToPolyline(b, mirrorFwd)));
        return d / MathF.Max(1f, DiagOf(b));
    }

    /// <summary>每个点到一条折线的最近距离的最大值（诊断用：点数只有几十，全扫得起）。</summary>
    private static float DevToPolyline(Vector2[] pts, Vector2[] poly)
    {
        float worst = 0f;
        foreach (var p in pts)
        {
            float best = float.MaxValue;
            for (int k = 0; k + 1 < poly.Length; k++) best = MathF.Min(best, PointSeg(p, poly[k], poly[k + 1]));
            worst = MathF.Max(worst, best);
        }
        return worst;
    }

    /// <summary>一串点的包围盒对角线（当"尺度"用）。</summary>
    private static float DiagOf(Vector2[] pts)
    {
        InkBounds(pts, out float diag);
        return diag;
    }

    // =====================================================================
    //  小工具
    // =====================================================================

    /// <summary>造一条"刚好差一点"的线：给定倾角，返回它的另一个端点（供吸附边界用）。</summary>
    private static Vector2 SnapEnd(float deg)
    {
        float rad = deg * MathF.PI / 180f;
        return new Vector2(MathF.Cos(rad) * 300f, MathF.Sin(rad) * 300f);
    }

    /// <summary>墨迹的**各向异性** `√(λ1/λ2)`（≥1，越大越"长"）：判断"主轴可不可信"用。</summary>
    private static float Anisotropy(Vector2[] pts)
    {
        var m = Vector2.Zero;
        foreach (var p in pts) m += p;
        m /= Math.Max(1, pts.Length);
        float sxx = 0f, sxy = 0f, syy = 0f;
        foreach (var p in pts)
        {
            float dx = p.X - m.X, dy = p.Y - m.Y;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        float trace = sxx + syy;
        float disc = MathF.Sqrt(MathF.Max(0f, trace * trace - 4f * (sxx * syy - sxy * sxy)));
        float l1 = (trace + disc) * 0.5f, l2 = (trace - disc) * 0.5f;
        return l2 < 1e-6f ? 10f : MathF.Sqrt(MathF.Max(1f, l1 / l2));
    }

    /// <summary>墨迹自己的**主轴方向**（单位向量）：自检拿它核对"姿态角没搞反"。</summary>
    private static Vector2 PrincipalDir(Vector2[] pts)
    {
        var m = Vector2.Zero;
        foreach (var p in pts) m += p;
        m /= Math.Max(1, pts.Length);
        float sxx = 0f, sxy = 0f, syy = 0f;
        foreach (var p in pts)
        {
            float dx = p.X - m.X, dy = p.Y - m.Y;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        float theta = 0.5f * MathF.Atan2(2f * sxy, sxx - syy);
        return new Vector2(MathF.Cos(theta), MathF.Sin(theta));
    }

    /// <summary>
    /// **识别出来的形状和墨迹差多远**：墨迹上每个点到"理想轮廓"的最大距离 ÷ 形状尺度。
    ///
    /// 轮廓是从**识别结果的定义元素**反推的（`ShapeGuess.Def` 的口径见
    /// 计划-图形工具.md §42.2），这里是**另一份独立实现**——
    /// 要是和识别器里那份验收代码共用同一个函数，这个自检就只是在复读自己
    ///（"别把'我以为的设计'直接写成期望"，见 project_memory）。
    /// </summary>
    private static float Deviation(in ShapeGuess g, Vector2[] ink)
    {
        var def = g.Def;
        if (def.Length < 2) return 0f;
        Bounds(ink, out var mn, out var mx);
        float diag = MathF.Max(1f, Vector2.Distance(mn, mx));
        float worst = 0f;

        switch (g.Kind)
        {
            case StrokeKind.Line:
            case StrokeKind.Arrow:
                foreach (var q in ink) worst = MathF.Max(worst, PointSeg(q, def[0], def[1]));
                break;

            case StrokeKind.Circle:
            {
                float r = Vector2.Distance(def[1], def[0]);
                foreach (var q in ink) worst = MathF.Max(worst, MathF.Abs(Vector2.Distance(q, def[0]) - r));
                break;
            }

            case StrokeKind.Ellipse:
            {
                float a = MathF.Max(1e-3f, MathF.Abs(def[1].X - def[0].X));
                float b = MathF.Max(1e-3f, MathF.Abs(def[1].Y - def[0].Y));
                foreach (var q in ink)
                {
                    var lp = ToLocal(q, g.RotPivot, g.RotationDeg);
                    double dx = lp.X - def[0].X, dy = lp.Y - def[0].Y;
                    // 隐式函数 + 梯度：|Q| / |∇Q| ≈ 到椭圆的几何距离（和识别器里同一个近似）
                    double Q = (dx * dx) / (a * a) + (dy * dy) / (b * b) - 1.0;
                    double gx = 2.0 * dx / (a * a), gy = 2.0 * dy / (b * b);
                    double gl = Math.Sqrt(gx * gx + gy * gy);
                    if (gl > 1e-9) worst = MathF.Max(worst, (float)Math.Abs(Q / gl));
                }
                break;
            }

            case StrokeKind.Rectangle:
            {
                // ⚠ 比法：把**墨迹点转到对象的局部坐标系**，再量它到"轴对齐框那四条边"的距离。
                //   第一版是把框的四个角**转到画布**再比——而这里用的 `ToLocal` 是
                //   **逆变换**，等于把框往反方向转了 2×姿态角，量出来 31% 的假偏差
                //（"识别出来的形状和墨迹贴得住"那条断言一度红在这，其实是自检自己转反了）。
                var lo = new Vector2(MathF.Min(def[0].X, def[1].X), MathF.Min(def[0].Y, def[1].Y));
                var hi = new Vector2(MathF.Max(def[0].X, def[1].X), MathF.Max(def[0].Y, def[1].Y));
                foreach (var q in ink)
                {
                    var lp = ToLocal(q, g.RotPivot, g.RotationDeg);
                    float d = MathF.Min(MathF.Min(MathF.Abs(lp.X - lo.X), MathF.Abs(lp.X - hi.X)),
                                        MathF.Min(MathF.Abs(lp.Y - lo.Y), MathF.Abs(lp.Y - hi.Y)));
                    worst = MathF.Max(worst, d);
                }
                break;
            }

            case StrokeKind.Triangle:
            case StrokeKind.Parallelogram:
            {
                // ⚠ 平行四边形的第四个顶点要**插在中间**：模型的口径是
                //   `第2 + 第3 − 第1`（见 StrokeKind.Parallelogram），顺序是 v0 → v1 → 推出来的 → v3。
                //   直接 append 到末尾的话，多边形自己跟自己交叉（蝴蝶结），
                //   量出来的距离是假的（"认出来的形状和墨迹贴得住"那条断言一度红在这）。
                Vector2[] poly = g.Kind == StrokeKind.Parallelogram
                    ? new[] { def[0], def[1], def[1] + def[2] - def[0], def[2] }
                    : new[] { def[0], def[1], def[2] };
                foreach (var q in ink)
                {
                    float best = float.MaxValue;
                    for (int i = 0; i < poly.Length; i++)
                        best = MathF.Min(best, PointSeg(q, poly[i], poly[(i + 1) % poly.Length]));
                    worst = MathF.Max(worst, best);
                }
                break;
            }
        }
        return worst / diag;
    }

    /// <summary>点到线段的距离。</summary>
    private static float PointSeg(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>把画布上的点**转回对象的局部坐标系**（逆着姿态角转，绕 <see cref="ShapeGuess.RotPivot"/>）。
    /// 姿态角是"用户口径"（逆时针为正），所以逆变换就是屏幕上顺时针转它——
    /// 和 `SelectionHandles.RotateMatrix`（内部是 `CreateRotation(-deg)`）正好互逆。</summary>
    private static Vector2 ToLocal(Vector2 q, Vector2 pivot, float rotDeg)
    {
        if (MathF.Abs(rotDeg) < 0.01f) return q;
        float rad = rotDeg * MathF.PI / 180f;
        float cs = MathF.Cos(rad), sn = MathF.Sin(rad);
        float dx = q.X - pivot.X, dy = q.Y - pivot.Y;
        return new Vector2(pivot.X + dx * cs - dy * sn, pivot.Y + dx * sn + dy * cs);
    }

    private static void Bounds(Vector2[] p, out Vector2 mn, out Vector2 mx)
    {
        mn = mx = p[0];
        foreach (var q in p)
        {
            if (q.X < mn.X) mn.X = q.X;
            if (q.Y < mn.Y) mn.Y = q.Y;
            if (q.X > mx.X) mx.X = q.X;
            if (q.Y > mx.Y) mx.Y = q.Y;
        }
    }

    private static RectF InkBounds(Vector2[] pts, out float diag)
    {
        var b = new RectF { MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
        foreach (var p in pts)
        {
            if (p.X < b.MinX) b.MinX = p.X;
            if (p.Y < b.MinY) b.MinY = p.Y;
            if (p.X > b.MaxX) b.MaxX = p.X;
            if (p.Y > b.MaxY) b.MaxY = p.Y;
        }
        diag = Vector2.Distance(new Vector2(b.MinX, b.MinY), new Vector2(b.MaxX, b.MaxY));
        return b;
    }

    /// <summary>把"为什么没认出来"那条长串压短：只留箭头后面的部分，太长就截断。</summary>
    private static string ShortRule(string rule)
    {
        if (string.IsNullOrEmpty(rule)) return "";
        int i = rule.IndexOf('→');
        string s = i >= 0 ? rule[(i + 1)..] : rule;
        s = s.Replace("；", " / ").Trim();
        return s.Length <= 96 ? s : s[..96] + "…";
    }

    private static string Label(StrokeKind k) => k switch
    {
        StrokeKind.Freehand => "—",
        StrokeKind.Line => "直线",
        StrokeKind.Rectangle => "矩形",
        StrokeKind.Ellipse => "椭圆",
        StrokeKind.Circle => "圆",
        StrokeKind.Triangle => "三角形",
        StrokeKind.Parallelogram => "平行四边形",
        StrokeKind.Arrow => "箭头",
        _ => k.ToString(),
    };
}
