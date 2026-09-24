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
        var families = positives.Select(c => c.Family).Distinct().ToList();
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
            }
        }

        // ── 反例：误报率 ────────────────────────────────────────────────
        //  分两档看，因为它们的性质完全不一样：
        //   · **本该认不出来**的（半圆弧、涂鸦……）——误报就是要修的 bug；
        //   · **本来就是直线型**的汉字笔画（横 / 竖 / 竖弯钩）——判成直线不算错，
        //     那是"这条功能在写字时会咬人多少"的量，靠**停顿 600ms**那道闸门挡，
        //     不靠识别器挡。这一档**只报告、不断言**。
        Console.WriteLine();
        Console.WriteLine("  ── 反例（不该认出来的）──");
        var negatives = corpus.Where(c => !c.ExpectShape && !c.Informational).ToList();
        foreach (var fam in negatives.Select(c => c.Family).Distinct())
        {
            var cases = negatives.Where(c => c.Family == fam).ToList();
            int bad = 0;
            var asWhat = new Dictionary<string, int>();
            foreach (var c in cases)
            {
                var g = ShapeRecognize.Recognize(c.Pts);
                if (g.IsNothing) continue;
                bad++;
                string k = Label(g.Kind);
                asWhat[k] = asWhat.TryGetValue(k, out var n) ? n + 1 : 1;
            }
            string note = bad == 0 ? "" : "  误判成: " + string.Join("，", asWhat.Select(kv => $"{kv.Key}×{kv.Value}"));
            Console.WriteLine($"   {fam,-16} {bad,3}/{cases.Count,-3} 误报 {bad * 100f / cases.Count,5:F1}%{note}");
            negativeRate[fam] = bad / (float)cases.Count;
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
            // ⚠ 比的是 **WorldInkBounds（墨迹框）**，不是 `Bounds`（控制点框）、也不是
            //   `InkBounds`（那个是**局部控制点**的框）：圆 / 椭圆那两个控制点是
            //   "圆心 + 一个点"，照控制点算出来的框是**扁的**（第一版踩了两次：
            //   圆写回的框 height = 0，报出 35% 的差，看着像"识别错了"，其实是取错了框）。
            //   引擎里"选中框 / 脏区"吃的也是墨迹框，所以它才是"用户看到的那个框"。
            var got = s.WorldInkBounds;
            var ink = InkBounds(c.Pts, out float diag);
            float dev = MathF.Max(
                MathF.Max(MathF.Abs(got.MinX - ink.MinX), MathF.Abs(got.MaxX - ink.MaxX)),
                MathF.Max(MathF.Abs(got.MinY - ink.MinY), MathF.Abs(got.MaxY - ink.MaxY)));
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

        // ── **认出来的形状必须和墨迹贴得住**（用户 2026-09-23 的核心反馈）──────────
        //  "矩形识别形变太大，完全和墨迹对不上" —— 这一条就是把"对不上"**量出来**：
        //  认出来的形状，它的轮廓和墨迹的最大偏差（按形状的尺度归一）。
        //  ⚠ 轮廓是从**识别结果的定义元素**反推的，是一份**独立实现**——
        //    要是和识别器里那份验收代码共用同一个函数，这个自检就只是在复读自己。
        Console.WriteLine();
        float worstDev = 0f;
        string worstDevCase = "";
        int devChecked = 0;
        var hardFam = positives.Where(c => c.Hard).Select(c => c.Family).Distinct().ToList();
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
        var missing = ShapeRecognize.RecognizableKinds
            .Where(k => !positives.Any(c => c.Want == k)).ToArray();
        Check("识别器表覆盖：能产的都写了用例", missing.Length == 0,
              missing.Length == 0
                ? $"{ShapeRecognize.RecognizableKinds.Length} 种图形都有正例"
                : "缺用例：" + string.Join("、", missing.Select(Label)));

        // ② 每一种正例的识别率
        foreach (var fam in families)
            Check($"{fam} 识别率 ≥ 90%", rateOf[fam] >= 0.90f, $"实测 {rateOf[fam] * 100f:F1}%");

        // ③ 反例：**本该认不出来**的那一类，误报率要压到 10% 以下
        foreach (var kv in negativeRate)
            Check($"{kv.Key} 误报率 ≤ 10%", kv.Value <= 0.10f, $"实测 {kv.Value * 100f:F1}%");

        // ④ 写回：定义元素真的能画出"原来那一笔占的地方"
        Check("识别结果写回模型后包围盒对得上", worstWriteBack <= 0.15f,
              $"最差 {worstWriteBack * 100f:F1}% 的尺度差（{checkedWriteBack} 条；{worstCase}）");

        // ⑤ 多笔：四条边分着画的矩形要能认出来
        Check("多笔：四条边分着画 → 矩形", rectChain.Kind == StrokeKind.Rectangle,
              $"{Label(rectChain.Kind)}（{ShortRule(rectChain.Rule)}）；语料 {rectNote}");

        // ⑥ 多笔：双曲线的两支**接不上**，所以必须什么都不认（宁可不认，不能乱认）
        Check("多笔：双曲线两支 → 不认（接不上）", hyperChain.IsNothing,
              hyperChain.IsNothing ? $"未识别：{ShortRule(hyperChain.Rule)}" : $"错认成 {Label(hyperChain.Kind)}");

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
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeTriangle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeRectangle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeTiltedRectangle(rnd));
        for (int i = 0; i < PositivePerKind; i++) list.Add(MakeParallelogram(rnd));

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
        for (int i = 0; i < NegativePerKind; i++) list.Add(MakeWave(rnd));         // 波浪线
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

    /// <summary>椭圆：**轴对齐**（我们模型就是轴对齐的），轴比 0.4~0.9。</summary>
    private static Case MakeEllipse(Random rnd)
    {
        float a = 90f + (float)rnd.NextDouble() * 220f;
        float ratio = 0.40f + (float)rnd.NextDouble() * 0.50f;
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

    /// <summary>波浪线（1.5 个周期）：形状很像正弦，但我们的模型表达不了多周期，所以不认。</summary>
    private static Case MakeWave(Random rnd)
    {
        int n = 80;
        var pts = new Vector2[n + 1];
        float amp = 40f + (float)rnd.NextDouble() * 50f;
        float len = 260f + (float)rnd.NextDouble() * 120f;
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            pts[i] = new Vector2(300f + len * t + J(rnd),
                                 400f + MathF.Sin(t * MathF.PI * 3f) * amp + J(rnd));
        }
        return new Case { Family = "波浪线", Pts = pts, Want = StrokeKind.Freehand, ExpectShape = false, Note = "" };
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
