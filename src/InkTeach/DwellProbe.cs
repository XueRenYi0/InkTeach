using System.Numerics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// **停顿成型的自检**（`--dwelltest`）。规格与实测口径见 计划-图形工具.md §四十二。
///
/// 两条原则（都是这个仓库踩出来的）：
///   1. **走真入口**：起笔 / 采样 / 轮询 / 抬手全部调引擎里真在用的那几个函数
///      （`BeginFreehandStrokeAt` / `ExtendFreehandStroke` / `TickDwellShape` / `EndStroke`），
///      不在测试里另造一条笔迹对象——那样测的只是"我自己填的字段"
///      （教训见 project_memory："我造了个对象、字段是我自己填的"不算测）。
///   2. **推时钟、不 sleep**：`NowMs` 是自检直接往前推的。600ms 一个用例、十几个用例，
///      sleep 会让自检从"一眼看完"变成"等十秒"，而且真机上时序不可复现。
///
/// 断言全部落在**看得见的结果**上：文档里多了什么对象、它是不是被选中、撤销栈怎么变、
/// 撤销之后回到的是不是用户手画的那一笔。
/// </summary>
internal static class DwellProbe
{
    // 画布单位的语料尺寸。为什么用"画布单位"而不是逻辑像素：引擎内部一律画布坐标，
    // 逻辑像素要乘 DpiScale 才是画布单位（见 `ShapeMinDragLogical` 那些常量的用法）。
    // 这几个数在 DpiScale = 1~4 上都满足"要么明显够长、要么明显太短"。
    private const float LineLen = 300f;      // 一条明显的直线
    private const float ShortLen = 20f;      // 明显短于门槛（40 逻辑像素）
    private const float JitterAmp = 1f;      // 死区内的抖动（死区 ≥ 2 逻辑像素）

    internal static int Run(App app)
    {
        Console.WriteLine();
        Console.WriteLine("=== 停顿成型：自检（走真入口 + 推时钟，不 sleep）===");
        Console.WriteLine($"  停顿时长门槛 {DwellAssist.HoldMs:F0}ms、死区 {DwellAssist.DeadZoneLogical:F0} 逻辑像素、"
                          + $"轮询 {DwellAssist.TickMs}ms、拖动起步死区 {InkEngine.InkEngine.DwellDragSlopLogical:F0} 逻辑像素");
        Console.WriteLine("  交互（用户 2026-09-25 照 ClassIn 定）：**一律先当幽灵，抬手才定型** ——"
                          + "直线拖另一头转向、其它图形整体平移；抬手后只有 **直线不选中**（顺手一划，接着写）");
        Console.WriteLine($"  ⚠ 识别门槛 {ShapeRecognize.MinLength:F0} 逻辑像素（≈1.5cm）——比它短的笔画**不转换**，原样留着手绘");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-30} {detail}");
        }

        app.RunActionForTest(KeyAction.ToolPen);

        // ── A. 停够 600ms：该认出来、该进入"幽灵预览" ──────────────────────
        double t0 = app.NowMs = 1000;
        var line = WobblyLine(300f, 400f, LineLen, 0);
        Drive(app, line, 400);                       // 画完，但先不推进时钟
        app.NowMs = t0 + 400 + DwellAssist.HoldMs;   // 停住 600ms
        app.DwellTickForTest();
        Check("停 " + DwellAssist.HoldMs + "ms → 认出直线", app.ActiveStroke != null && app.ActiveStroke.Kind == StrokeKind.Line
                                    && app.DwellStateForTest == DwellState.Armed,
              $"ActiveStroke = {KindName(app.ActiveStroke)}, 状态 {app.DwellStateForTest}");

        // ── B. 只停 559ms：**不该**触发（门槛的边界）────────────────────────
        double t1 = app.NowMs = 5000;
        Drive(app, WobblyLine(300f, 700f, LineLen, 0), 200);
        app.NowMs = t1 + 200 + DwellAssist.HoldMs - 41;   // 差一个轮询周期多一点
        app.DwellTickForTest();
        Check("停不到门槛 → 不变（边界）", app.ActiveStroke != null
                                        && app.ActiveStroke.Kind == StrokeKind.Freehand
                                        && app.DwellStateForTest == DwellState.Tracking,
              $"Kind = {KindName(app.ActiveStroke)}, 状态 {app.DwellStateForTest}");
        app.DwellEndForTest();                            // 这一笔抬手提交成普通笔迹

        // ── C. 死区：笔尖静止时的亚像素抖动**不许**把计时刷新掉 ────────────
        //   这一条是整条功能的命门：没死区 → 抖动一直刷新 → 600ms 永远攒不满 →
        //   "看起来像没生效"（真机上最难查的一类）。
        double t2 = app.NowMs = 9000;
        Drive(app, WobblyLine(300f, 900f, LineLen, 0), 120);
        // ⚠ 抖动要抖在**笔停下来的那个位置**（这一笔的末点 = 起点 + 长度），
        //    不是抖在半路上——半路那个点离末点一百多像素，那是"真的动了"，
        //    是测试自己写错了（第一版就是这么挂的）。
        float endX = 300f + LineLen, endY = 900f;
        for (int i = 0; i < 20; i++)                      // 抖 20 次，够 800ms
        {
            app.NowMs += 40;
            app.DwellMoveForTest(endX + (i % 2 == 0 ? JitterAmp : -JitterAmp),
                                 endY + (i % 3 == 0 ? 0f : 0.5f));
        }
        app.DwellTickForTest();
        Check("死区内的抖动不刷新计时", app.ActiveStroke != null
                                      && app.ActiveStroke.Kind == StrokeKind.Line,
              $"抖 800ms 后 Kind = {KindName(app.ActiveStroke)}");

        // ── D. 抬笔定型：进文档 + **直线不选中** + 一步撤销 ────────────────
        int nBefore = app.Doc.Strokes.Count;
        int undoBefore = app.Doc.UndoDepth;
        app.DwellEndForTest();
        var committed = app.Doc.Strokes.Count > nBefore ? app.Doc.Strokes[^1] : null;
        Check("抬手定型：图形进文档", committed != null && committed.Kind == StrokeKind.Line,
              committed == null ? "文档里没多东西" : $"新增 {KindName(committed)}");
        // 用户 2026-09-23 定的**例外**：停顿变出来的直线抬手后**不**选中（顺手一划，接着写下一笔）；
        // 其它形状仍然自动选中。图形工具画的直线不在此列（那条路照旧选中）。
        Check("**直线**抬手后不选中（定的例外）",
              app.Doc.Selected.Count == 0 && !app.DwellSelectedForTest,
              $"选中 {app.Doc.Selected.Count} 条，_dwellSelected = {app.DwellSelectedForTest}");
        Check("一步撤销（不是两步）", app.Doc.UndoDepth == undoBefore + 1,
              $"撤销栈 {undoBefore} → {app.Doc.UndoDepth}");

        // ── E/F. 撤销 → 回**手绘原迹**；重做 → 又变回图形 ─────────────────
        app.RunActionForTest(KeyAction.Undo);
        var back = app.Doc.Strokes.Count > 0 ? app.Doc.Strokes[^1] : null;
        Check("撤销一步回到手绘原迹", back != null && back.Kind == StrokeKind.Freehand
                                     && back.Points.Count > 3,
              back == null ? "文档空了" : $"{KindName(back)}／{back.Points.Count} 个点");
        app.RunActionForTest(KeyAction.Redo);
        var again = app.Doc.Strokes.Count > 0 ? app.Doc.Strokes[^1] : null;
        Check("重做又变回图形", again != null && again.Kind == StrokeKind.Line,
              again == null ? "文档空了" : KindName(again));

        // ── G. 太短的一笔不参与（标点 / 部首）──────────────────────────────
        double t3 = app.NowMs = 20000;
        Drive(app, WobblyLine(300f, 1100f, ShortLen, 0), 80);
        app.NowMs = t3 + 80 + DwellAssist.HoldMs * 2;
        app.DwellTickForTest();
        Check("太短的笔画不参与", app.ActiveStroke != null
                                 && app.ActiveStroke.Kind == StrokeKind.Freehand,
              $"长 {ShortLen:F0}（门槛 {ShapeRecognize.MinLength * app.DpiScale:F0} 画布单位）"
              + $" → {KindName(app.ActiveStroke)}");
        app.DwellEndForTest();

        // ── H. 非直线：**幽灵 → 按住可整体平移 → 松手才定型 + 选中** ──────────────────
        //    用户 2026-09-25 试过 ClassIn 之后定的（2026-09-24 那版是"识别到就定型并选中"）。
        //    ClassIn 的手感：图形变出来以后**先别松手**，直接把它拖到位，松手才选中——
        //    比"松手 → 再点它 → 再拖"少两步。**翻转的就是这一组断言**：
        //    tick 那一刻不许进文档、不许选中；此时拖动必须**只平移、绝不改形状**。
        double t4 = app.NowMs = 30000;
        var cpts = Circle(500f, 400f, 120f);
        Drive(app, cpts, 300);
        app.NowMs = t4 + 300 + DwellAssist.HoldMs;
        int nBeforeH = app.Doc.Strokes.Count;
        app.DwellTickForTest();               // ← 还没抬手（`DwellEndForTest` 还没调）
        Check("停 " + DwellAssist.HoldMs + "ms → 认出圆，但**还没进文档**（幽灵期）",
              app.Doc.Strokes.Count == nBeforeH
              && app.ActiveStroke != null && app.ActiveStroke.Kind == StrokeKind.Circle,
              $"文档条数 {nBeforeH} → {app.Doc.Strokes.Count}、"
              + (app.ActiveStroke == null ? "ActiveStroke 已放手" : $"ActiveStroke = {KindName(app.ActiveStroke)}"));
        Check("幽灵期**不选中**（矩形框还没出来）",
              app.Doc.Selected.Count == 0,
              $"选中 {app.Doc.Selected.Count} 条");

        // **幽灵期拖动 = 改大小**（用户 2026-09-25 上手 ClassIn 之后逐条定）：
        //   (a) 圆形"**圆心不动**，改变圆的大小" —— 这一条正好拿圆来验，判据两条：
        //       ① 有一颗定义元素**一个像素都没动**（那就是圆心 = 锚点）；
        //       ② 新的半径 = **指针到那颗不动点的距离**（不是原来那个半径）。
        //   (b)(c) 矩形 / 不能旋转 —— 矩形那条在 `--shapetooltest` 的拖角用例里已经覆盖
        //   （"矩形·正方形：拖四角时修正成精确正方形"）；"幽灵期不做旋转"这一条就是
        //   "全程只挪定义元素"，这里顺手验：**姿态矩阵一个数都不许变**。
        // ⚠ 判据故意不写"该是哪一颗不动、半径该等于多少"：锚点是引擎里挑的、自检看不见；
        //   写死下标 = 下次挑法一动就静默失效（仓库里踩过好几次）。
        var ghost0 = DefPoints(app.ActiveStroke);        // 成型那一刻的 (圆心, 圆周点)
        var pose0 = app.ActiveStroke.Transform;
        var target = new Vector2(700f, 400f);            // 圆心 (500,400) 正右方 200 处
        app.DwellMoveForTest(target.X, target.Y);
        var ghost1 = DefPoints(app.ActiveStroke);
        var pose1 = app.ActiveStroke.Transform;

        bool ghostOk = ghost0.Length == 2 && ghost1.Length == 2;
        int stillIdx = -1;
        if (ghostOk)
        {
            if (Vector2.Distance(ghost1[0], ghost0[0]) <= 0.01f) stillIdx = 0;
            else if (Vector2.Distance(ghost1[1], ghost0[1]) <= 0.01f) stillIdx = 1;
        }
        float rBefore = ghostOk ? Vector2.Distance(ghost0[0], ghost0[1]) : 0f;
        float rAfter = ghostOk && stillIdx >= 0 ? Vector2.Distance(ghost1[0], ghost1[1]) : 0f;
        float rWant = ghostOk && stillIdx >= 0 ? Vector2.Distance(target, ghost1[stillIdx]) : -1f;
        Check("幽灵期拖动 = **改大小**：圆心不动、半径 = 指针到圆心的距离",
              ghostOk && stillIdx >= 0 && MathF.Abs(rAfter - rWant) <= 0.5f
              && rAfter > rBefore + 1f,
              $"不动的那个 = 第 {stillIdx} 点；半径 {rBefore:F1} → {rAfter:F1}（期望 {rWant:F1}）");
        Check("幽灵期拖动**不做旋转**（姿态矩阵一个数都不变）",
              pose0.M11 == pose1.M11 && pose0.M12 == pose1.M12
              && pose0.M21 == pose1.M21 && pose0.M22 == pose1.M22,
              $"M11 {pose0.M11:F4} → {pose1.M11:F4}、M12 {pose0.M12:F4} → {pose1.M12:F4}");

        // 松手 → 定型 + 选中
        app.DwellEndForTest();
        var hShape = app.Doc.Strokes.Count > nBeforeH ? app.Doc.Strokes[^1] : null;
        Check("抬手才定型：图形进文档",
              hShape != null && hShape.Kind == StrokeKind.Circle && app.ActiveStroke == null,
              hShape == null ? "文档里没多东西" : $"文档新增 {KindName(hShape)}、ActiveStroke 已放手");
        Check("松手才选中（框跟着内容走：还是那颗改过大小的圆）",
              hShape != null && app.Doc.Selected.Count == 1 && app.Doc.Selected[0] == hShape
              && app.DwellSelectedForTest && DefDrift(ghost1, hShape) <= 0.01f,
              $"选中 {app.Doc.Selected.Count} 条、_dwellSelected = {app.DwellSelectedForTest}"
              + $"、定义元素漂移 {DefDrift(ghost1, hShape):F2}");

        // ── H3. **平行四边形**：幽灵 → 松手定型 + 选中（用户 2026-09-23 报过它"整个消失"）──
        //    这一组盯的就是那条反馈：变出来了没有、进文档了没有、选中了没有。
        //    幽灵期盯"还在手上"，抬手盯"**必须提交出去**"——当初那个 bug 正是抬手连
        //    手绘原迹一起丢。
        double t4c = app.NowMs = 36000;
        var ppts = ParaPath(1500f, 400f, 240f, 150f, 70f);
        Drive(app, ppts, 300);
        app.NowMs = t4c + 300 + DwellAssist.HoldMs;
        int nBeforePara = app.Doc.Strokes.Count;
        app.DwellTickForTest();
        Check("平行四边形：幽灵期已经在手上（看着就像成型了）",
              app.ActiveStroke != null && app.ActiveStroke.Kind == StrokeKind.Parallelogram,
              app.ActiveStroke == null ? "幽灵不在手上（这一笔看着像没成型）"
                                       : $"ActiveStroke = {KindName(app.ActiveStroke)}");
        app.DwellEndForTest();               // 抬手定型（后面 J 还要拿它当"上一个停顿图形"）
        var para = app.Doc.Strokes.Count > nBeforePara ? app.Doc.Strokes[^1] : null;
        Check("平行四边形：抬手定型，没被丢掉",
              para != null && para.Kind == StrokeKind.Parallelogram,
              para == null ? "文档里没多东西（这一笔连手绘原迹一起没了）"
                           : $"文档新增 {KindName(para)}");
        Check("平行四边形：松手即选中",
              para != null && app.Doc.Selected.Count == 1 && app.Doc.Selected[0] == para
              && app.DwellSelectedForTest,
              $"选中 {app.Doc.Selected.Count} 条，_dwellSelected = {app.DwellSelectedForTest}");

        // ── H4. **太小的图形不转换**（用户 2026-09-24 定："图形太小不用出图，
        //    正常不会画很太小的图"）───────────────────────────────────────────────
        //    判据就在识别器那条最短长度上（`ShapeRecognize.MinLength`）：比它短的笔画
        //    **一概不认**，原样留着手绘 —— 这是**良性失败**（用户看得见自己画的东西）。
        //    它顺带把另一条规则也保住了："点一下不留墨"的容差是 8 逻辑像素、量的又是
        //    **图形的首末两点**（对圆来说就是半径）——56 保证 r ≥ 8.9 > 8，两条不打架。
        //    ⚠ 这一笔的按下点在**上一个停顿图形的框外**，所以 `_dismissTapArmed` 为真（常态）：
        //      "墨必须原样留下"正是在这个状态下验的（没有这条豁免，它会被当成"点一下"抹掉）。
        double t4d = app.NowMs = 38000;
        int nBeforeSmall = app.Doc.Strokes.Count;    // ⚠ 账要记在**整笔之前**（识别到就定型，tick 里就进文档）
        Drive(app, Circle(2600f, 400f, 15f), 200);   // 半径 15 画布单位 = 7.5 逻辑像素（周长 47 < 门槛 56）
        app.NowMs = t4d + 200 + DwellAssist.HoldMs;
        app.DwellTickForTest();
        app.DwellEndForTest();
        var small = app.Doc.Strokes.Count > nBeforeSmall ? app.Doc.Strokes[^1] : null;
        Check("太小的图形：不转换，墨原样留下",
              small != null && small.Kind == StrokeKind.Freehand && !app.DwellSelectedForTest,
              small == null ? $"文档里没多东西（条数 {nBeforeSmall} → {app.Doc.Strokes.Count}——墨被丢了）"
                            : $"留下 {KindName(small)}（期望：手绘笔迹、不转换）");

        // ── I. 直线 armed 后拖端点 = 转向 / 伸缩，且吸到水平 ±4° ──────────
        double t5 = app.NowMs = 40000;
        Drive(app, WobblyLine(300f, 500f, LineLen, 0), 200);
        app.NowMs = t5 + 200 + DwellAssist.HoldMs;
        app.DwellTickForTest();
        // 往"接近水平偏 3°"的方向拖（起点钉住）：应当被吸成正水平
        float rad = 3f * MathF.PI / 180f;
        app.DwellMoveForTest(300f + 200f * MathF.Cos(rad), 500f + 200f * MathF.Sin(rad));
        var ghost = app.ActiveStroke;
        bool flat = ghost != null && ghost.Points.Count >= 2
                    && MathF.Abs(ghost.Points[^1].Y - ghost.Points[0].Y) < 0.01f;
        Check("直线拖端点：转向 + 吸水平", flat,
              ghost == null ? "没有幽灵" : $"终点 y 偏移 {MathF.Abs(ghost.Points[^1].Y - ghost.Points[0].Y):F2}");
        app.DwellEndForTest();

        // ── J. **取消选中那一击不留墨**（用户 2026-09-23 要的）─────────────
        //     ⚠ 这里必须用**会选中的形状**（圆）。直线抬手不选中了，
        //       用它就造不出"有选中框可取消"这个前提（第一版是线段，改规则后当场红）。
        double t6 = app.NowMs = 50000;
        Drive(app, Circle(1300f, 700f, 110f), 300);
        app.NowMs = t6 + 300 + DwellAssist.HoldMs;
        app.DwellTickForTest();
        app.DwellEndForTest();                        // 变出圆 + 自动选中
        int nBeforeTap = app.Doc.Strokes.Count;
        int undoBeforeTap = app.Doc.UndoDepth;
        bool hadSelection = app.Doc.Selected.Count == 1;
        // 点框外（框大致是"圆心 ± 半径"，往 y 方向走出去 260 就肯定在外面）
        app.DwellBeginForTest(1300f, 700f + 260f);
        app.NowMs += 60;
        app.DwellMoveForTest(1300f + 3f, 700f + 260f);   // ≤ 点选容差
        app.DwellEndForTest();
        Check("点框外：取消选中", hadSelection && app.Doc.Selected.Count == 0 && !app.DwellSelectedForTest,
              $"点之前选中 {hadSelection}，点之后 {app.Doc.Selected.Count} 条");
        Check("点框外：不留墨、不进撤销栈",
              app.Doc.Strokes.Count == nBeforeTap && app.Doc.UndoDepth == undoBeforeTap,
              $"条数 {nBeforeTap} → {app.Doc.Strokes.Count}，撤销栈 {undoBeforeTap} → {app.Doc.UndoDepth}");

        // ── K. 开关关掉 → 停多久都不变（默认开，但得能关）──────────────────
        app.SetDwellEnabledForTest(false);
        double t7 = app.NowMs = 60000;
        Drive(app, WobblyLine(300f, 1300f, LineLen, 0), 80);
        app.NowMs = t7 + 80 + DwellAssist.HoldMs * 2;
        app.DwellTickForTest();
        bool off = app.ActiveStroke != null && app.ActiveStroke.Kind == StrokeKind.Freehand
                   && app.DwellStateForTest == DwellState.Idle;
        app.SetDwellEnabledForTest(true);
        Check("开关关掉 → 一点都不动", off,
              $"Kind = {KindName(app.ActiveStroke)}，状态 {app.DwellStateForTest}");
        app.DwellEndForTest();

        // ── L. 扫掠：图形 × 尺寸 × 按住期乱动 × `_dismissTapArmed` 两态 —— **墨不许凭空消失** ──
        //
        // 这一条是"自动化能不能抓住这类 bug"的正面回答（用户 2026-09-24 问的）：
        // 不用人上手点，只要**把不变量写出来**，机器就能搜出反例。
        //
        // **不变量**：一笔结束之后，「文档 +1」或「撤销栈 +1」必须命中一个；
        //   两个都不动 = 用户画的墨凭空没了（就是用户报的"图形整个会消失掉"）。
        //
        // 为什么要把这几维**叉起来**：这类是**交互故障**——单独看每一维都正常，
        // 必须几个条件同时成立才炸（NIST 的 interaction rule：绝大多数故障由 1~2 个
        // 因素交互触发，见 架构-分层与规则.md 的自检规矩）。H4 钉住的那条就是
        // "平行四边形 × 缩小 × 按下点在框外"三件事同时成立。
        //
        // 语料**固定种子** → 逐点可复现（跑两次数字必须一样）。
        // 位置按"第几个试验"排网格、间距 900 画布单位：**离上一个的选中框足够远**，
        // 免得这一笔的按下被"拖动上一个图形"吃掉（那会变成假失败）。
        double t8 = app.NowMs = 60000;
        int trials = 0, shapedCount = 0, lostInk = 0;
        var seenKinds = new HashSet<StrokeKind>();     // 扫掠真的认出来过哪些图形（覆盖断言用）
        foreach (float mag in new[] { 0f, 40f })              // 按住期间的"手腕乱动"（画布单位）
        {
            foreach (bool keepSel in new[] { true, false })   // 按下点是否带选中框（决定 `_dismissTapArmed`）
            {
                foreach (float size in new[] { 0.55f, 1f, 1.7f })
                {
                    foreach (string kind in new[] { "圆", "直线", "三角形", "矩形", "平行四边形", "抛物线", "椭圆", "五边形（认不出）" })
                    {
                        trials++;
                        t8 += 3000;
                        app.NowMs = t8;
                        // `false` 那一档：先撤掉文档选中 → `TryDwellSelectionPress` 早退，
                        // 于是"点框外"这一位**不会**被立起来。
                        if (!keepSel) app.Doc.SelectOnly(Array.Empty<Stroke>());
                        int col = (trials - 1) % 4, row = (trials - 1) / 4;
                        float cx = 400f + col * 900f, cy = 8000f + row * 900f;
                        var pts = SamplePath(kind, size, cx, cy);
                        // ⚠ 账要在**整笔之前**记：抬手定型会把这一笔自己提交进文档，
                        //   记晚了就会把它算进"原来就有"。
                        int doc0 = app.Doc.Strokes.Count, undo0 = app.Doc.UndoDepth;
                        Drive(app, pts, 300);
                        app.NowMs += DwellAssist.HoldMs;
                        app.DwellTickForTest();
                        // 成型之后"手腕乱动"一下（**两类图形此刻都还挂在 `ActiveStroke` 上**：
                        // 幽灵期抬手才定型 —— 直线乱动是转向/伸缩，非直线是整体平移，
                        // 这一条盯的是"墨不许凭空消失 / 变形"）。
                        if (mag > 0f && app.ActiveStroke != null
                            && app.ActiveStroke.Kind == StrokeKind.Line)
                        {
                            // 乱动方向/大小用固定种子（可复现）：从收笔位置往外挪 mag 那么远
                            var rnd = new Random(trials * 7 + 1);
                            double ang = rnd.NextDouble() * Math.PI * 2;
                            var a = pts[^1];
                            app.NowMs += 40;
                            app.DwellMoveForTest(a.X + MathF.Cos((float)ang) * mag,
                                                 a.Y + MathF.Sin((float)ang) * mag);
                        }
                        app.DwellEndForTest();
                        var added = app.Doc.Strokes.Count > doc0 ? app.Doc.Strokes[^1] : null;
                        if (added != null && added.Kind != StrokeKind.Freehand)
                        {
                            shapedCount++;
                            seenKinds.Add(added.Kind);
                        }
                        if (added == null && app.Doc.UndoDepth == undo0)
                        {
                            lostInk++;
                            Console.WriteLine($"    ★ 墨不见了：{kind}/尺寸 {size:F2}/乱动 {mag:F0}"
                                              + $"/{(keepSel ? "带选中" : "无选中")}");
                        }
                    }
                }
            }
        }
        Check("扫掠：墨不凭空消失（不变量）", lostInk == 0,
              $"{trials} 次：{shapedCount} 次成型、{lostInk} 次把墨丢了");

        // 覆盖断言（照 `--shapebandtest` 那套"名单一致"的做法）：识别表里能认的图形，
        // 自检必须**每种都真认出来过**——加了新识别器却忘了给语料，这里当场红。
        // （不写死一份"图形名单"：名单只认 `ShapeRecognize.RecognizableKinds` 那一处。）
        //
        // ⚠ **断言放在 Run 的最后**，不在这儿：`seenKinds` 要由**所有**小组一起喂 ——
        //   双曲线是"**两笔**成型"那条路产出的（单笔早已不出双曲线，用户 2026-09-25 定的
        //   "画两支就是双曲线"），只有下面的"两笔"那组能认出来。放在这一组里断言的话，
        //   它必然报"漏了 Hyperbola"——那是**断言写错了**，不是功能坏了（第一版就是这么红的）。

        // ── H6. armed 之后"笔尖没真的动" → 图形**一个像素都不许动**（不变量）────────
        //
        // 用户 2026-09-24 上手报的："横着画一条变直线，再竖着画一条变直线，
        // **竖着的那条变成了很短的一个横直线**"。
        // 真因：直线那一支把 `Points[0]` 当支点，而 `TryLine` 给的端点是
        // **拟合方向的投影极值**（哪一头落到 `Points[0]` 是不定的）——
        // 笔尖正好停在那头上时，"停手"期间那点亚像素抖动（`±1` 画布单位）就够
        // 把它压成零长度，`SnapToAxis` 一看没有方向 → 吸成水平 → 一条很短的小横线。
        // 除直线外，其它图形当时也在"跟着手抖改大小/转角"（只是幅度小、不易察觉）。
        //
        // 断言落在**定义元素逐点不变**上：对哪个 Kind 都成立，不含任何实现假设。
        double t9 = app.NowMs = 330000;
        int drift = 0, checkedItems = 0;
        // 直线要**两个画向都测**：识别器给的端点顺序取决于拟合方向的符号，
        // 出问题的那一半正好是"笔尖那一头落到 `Points[0]`"（用户报的就是它）。
        var h6 = new List<(string Name, Vector2[] Pts)>
        {
            ("直线·横", SamplePath("直线", 1f, 400f, 30000f)),
            ("直线·竖（往下）", WobblyLine(1400f, 30000f, 300f, 90f)),
            ("直线·竖（往上）", ReverseOf(WobblyLine(2400f, 30000f, 300f, 90f))),
            ("圆", SamplePath("圆", 1f, 3400f, 30000f)),
            ("椭圆", SamplePath("椭圆", 1f, 4400f, 30000f)),
            ("三角形", SamplePath("三角形", 1f, 5400f, 30000f)),
            ("矩形", SamplePath("矩形", 1f, 6400f, 30000f)),
            ("平行四边形", SamplePath("平行四边形", 1f, 7400f, 30000f)),
            ("抛物线", SamplePath("抛物线", 1f, 8400f, 30000f)),
        };
        foreach (var (name, kpts) in h6)
        {
            t9 += 3000;
            app.NowMs = t9;
            int nBefore6 = app.Doc.Strokes.Count;
            Drive(app, kpts, 300);
            app.NowMs += DwellAssist.HoldMs;
            app.DwellTickForTest();
            // 要看的是"刚成型的那一个"：**两类图形此刻都还挂在 `ActiveStroke` 上**
            //（幽灵期，抬手才定型；见 `ArmDwellShape`）——这就是"别丢墨"那一轮的观察对象。
            var still = app.ActiveStroke
                        ?? (app.Doc.Strokes.Count > nBefore6 ? app.Doc.Strokes[^1] : null);
            if (still == null || still.Kind == StrokeKind.Freehand)
            {
                Console.WriteLine($"    （{name} 这一档没成型，跳过）");
                continue;
            }
            checkedItems++;
            var before = DefPoints(still);
            // 喂 8 个"笔尖没动"的采样：±1 画布单位（远小于拖动死区 5 逻辑像素 = 10 画布单位）
            for (int i = 0; i < 8; i++)
            {
                app.NowMs += 40;
                app.DwellMoveForTest(kpts[^1].X + (i % 2 == 0 ? 1f : -1f),
                                     kpts[^1].Y + (i % 3 == 0 ? 0f : 0.5f));
            }
            float worst = DefDrift(before, still);   // 盯的还是"刚成型那一个"（非直线它在文档里）
            if (worst > 0.01f) drift++;
            Console.WriteLine($"    {name}：定义元素最大漂移 {worst:F2} 画布单位（{before.Length} 个点）");
            app.DwellEndForTest();
        }
        Check("armed 静止：图形一个像素都不动", drift == 0 && checkedItems == h6.Count,
              $"{checkedItems} 档成型，{drift} 档漂了（>0.01 画布单位）");

        // 直线还有一条：**拖出去时钉住的是"离笔尖远的那一头"**，不是"笔尖那一头"
        //（拿 `Points[0]` 当支点的话，哪一头是它不定——钉错时线会整条跳走）。
        // 判据：**成型那一刻的远端端点，拖完之后必须还在原处**（用户看得见的那一头不动）。
        double t10 = app.NowMs = 380000;
        {
            var vs = WobblyLine(1400f, 32000f, 300f, 90f);   // 竖着往下画：笔尖停在下端
            Drive(app, vs, 300);
            app.NowMs += DwellAssist.HoldMs;
            app.DwellTickForTest();
            bool armed = app.ActiveStroke != null && app.ActiveStroke.Kind == StrokeKind.Line;
            var penEnd = new Vector2(vs[^1].X, vs[^1].Y);
            var farEnd = Vector2.Zero;
            float fd = -1f;
            if (armed)
                foreach (var q in DefPoints(app.ActiveStroke))
                    if (Vector2.Distance(q, penEnd) > fd) { fd = Vector2.Distance(q, penEnd); farEnd = q; }
            app.NowMs += 40;
            app.DwellMoveForTest(vs[^1].X + 120f, vs[^1].Y);  // 往右拖 120（大于死区 10）
            float pinDist = float.MaxValue;
            if (armed)
                foreach (var q in DefPoints(app.ActiveStroke))
                    pinDist = MathF.Min(pinDist, Vector2.Distance(q, farEnd));
            Check("直线拖动：钉住的是远端（不是笔尖那一头）", armed && pinDist <= 0.5f,
                  armed ? $"远端端点拖完挪了 {pinDist:F2} 画布单位（原在离笔尖 {fd:F0} 处）" : "没成型");
            app.DwellEndForTest();
        }

        // ── 两笔 → 双曲线（用户 2026-09-25 定的约定："**画两支就是双曲线，画一支就是抛物线**"）
        //
        // 这一段是**引擎级**的（走真入口：落笔 / 采样 / 停顿 / 抬手 / 撤销），验五件事：
        //   · 两笔对称 → 文档里变成**一个**双曲线，**两笔原迹都收走**（净 +1）；
        //   · **一步** Ctrl+Z → 回到**两笔手绘**（不是一笔、不是空白）；
        //   · 两笔**不相干** → **不许**变成双曲线（"绝不抢别的活"）。
        app.NowMs = 900000;   // 把时钟推到一个干净的起点（和前面各组不重叠）
        {
            var right = Branch(2200f, 36000f, 80f, 90f, 1.6f, +1f);
            var left = Branch(2200f, 36000f, 80f, 90f, 1.6f, -1f);

            // ① 第一支：**抬手就走、不停顿** —— 这正是"画两支"里第一支该有的画法。
            Drive(app, right, 300);
            app.DwellEndForTest();
            int n0 = app.Doc.Strokes.Count;
            int undo0 = app.Doc.UndoDepth;
            var ink1 = app.Doc.Strokes.Count > 0 ? app.Doc.Strokes[^1] : null;
            Check("两笔① 第一支（不停顿）在文档里仍是**手绘的墨**",
                  ink1 != null && ink1.Kind == StrokeKind.Freehand,
                  ink1 == null ? "文档空了" : KindName(ink1));

            // ② 第二支：画完停顿 → 应当配成双曲线
            //    先记下"藏之前"的版本号：下面 ⑨b 要拿它验**渲染层真的收到通知了没有**。
            long verBeforeHide = app.Doc.Version;
            Drive(app, left, 300);
            app.NowMs += DwellAssist.HoldMs;
            app.DwellTickForTest();

            // ⑧ 幽灵期按**中心**缩放（用户 2026-09-25 定："双曲线应该按中心为锚点放大缩小"）。
            //    判据：拖完**中心一个像素都不动**、而整体尺寸**变大**。
            //    （通用规则"离笔尖最远的那一个当锚点"在双曲线上会挑到**渐近线角点**，
            //     那样缩放就绕角点转、图形整个跑偏 —— 所以这条要单独钉。）
            var hpGhost = app.ActiveStroke;
            Vector2 Cen() => hpGhost != null && hpGhost.Points.Count > 0
                ? new Vector2(hpGhost.Points[0].X, hpGhost.Points[0].Y) : Vector2.Zero;
            float BoxR() => hpGhost != null && hpGhost.Points.Count > 1
                ? Vector2.Distance(Cen(), new Vector2(hpGhost.Points[1].X, hpGhost.Points[1].Y)) : 0f;
            var c0 = Cen();
            float box0 = BoxR();
            app.NowMs += 40;
            app.DwellMoveForTest(2200f, 36620f);        // 从中心（y=36000）**往外**拖
            var c1 = Cen();
            float box1 = BoxR();
            Check("两笔⑧ 幽灵期缩放**绕中心**（中心不动、尺寸变大）",
                  Vector2.Distance(c0, c1) <= 0.5f && box1 > box0 * 1.05f,
                  $"中心挪了 {Vector2.Distance(c0, c1):F2}；渐近线框 {box0:F0} → {box1:F0}");

            // ⑨ **识别那一刻第一笔就藏起来**（用户 2026-09-25 定；原来要等抬手才消失）。
            //    判据两半都要：幽灵期**是隐藏的**，而且**文档里还在**（撤销要靠它）。
            Check("两笔⑨ 幽灵期第一笔已隐藏、但仍在文档里",
                  ink1 != null && ink1.HiddenForPairing && app.Doc.Strokes.Contains(ink1),
                  ink1 == null ? "文档空了"
                      : $"隐藏 = {ink1.HiddenForPairing}、还在文档里 = {app.Doc.Strokes.Contains(ink1)}");

            // ⑨b ⚠⚠ **这一条才是用户报的那个 bug 的判据**（用户 2026-09-25：
            //     "停顿那一刻第一笔没消失，松手以后它才消失"）。
            //
            // 根因：内容层是**分块位图缓存**（`CanvasTileCache`），第一笔在这一刻**早就烘进块里**；
            // 而块**只在 `Doc.Version` 变化时**才重光栅化（`Overlay.SyncTiles`）。
            // 于是"只置 `HiddenForPairing` 字段"的做法：`DrawStroke` 是跳过了它，
            // 但**没有任何一帧重画那一块** → 屏幕上原样留着旧像素，一直留到抬手提交
            // （那一步 `RemoveStroke` 抬了版本号、块才重画）—— 看着就是"松手才消失"。
            //
            // ⚠ 判据刻意**不查 `HiddenForPairing` 字段**：第一版⑨就只查字段，
            //   于是这个 bug 从它眼皮底下过去了（"改了字段、没让缓存失效"是本仓库的老教训，
            //   而"只查字段的自检照不出它"同样是）。这里查的是**渲染层收到通知没有**。
            bool dirtyCovers = app.Doc.Dirty.Full;
            if (!dirtyCovers && ink1 != null)
                foreach (var dr in app.Doc.Dirty.Rects)
                    if (dr.Intersects(ink1.PaddedBounds)) { dirtyCovers = true; break; }
            Check("两笔⑨b 藏起来那一刻**内容层缓存被作废**（抬版本号 ＋ 标脏那一块）",
                  app.Doc.Version > verBeforeHide && dirtyCovers,
                  $"版本号 {verBeforeHide} → {app.Doc.Version}、脏区盖住那一笔 = {dirtyCovers}"
                  + "（不抬版本号就永远不重画那一块 = 屏幕上不消失）");

            app.DwellEndForTest();
            var got = app.Doc.Strokes.Count > 0 ? app.Doc.Strokes[^1] : null;
            Check("两笔② 两笔对称 → **双曲线**", got != null && got.Kind == StrokeKind.Hyperbola,
                  got == null ? "文档是空的" : KindName(got));
            // 喂给最后那条覆盖断言：双曲线**只有这条路**能认出来（单笔不再出双曲线）。
            if (got != null) seenKinds.Add(got.Kind);
            // ⑩ **隐藏位不许泄漏**：提交之后文档里不该还有任何隐藏的笔 ——
            //    泄漏的话，Ctrl+Z 放回来的那一笔是隐身的，"撤销之后屏幕上什么都没回来" ✗
            int hiddenLeft = 0;
            foreach (var s in app.Doc.Strokes) if (s.HiddenForPairing) hiddenLeft++;
            Check("两笔⑩ 提交后没有笔还带着隐藏位（否则撤销会放回一条隐身的墨）",
                  hiddenLeft == 0, $"文档里还有 {hiddenLeft} 笔是隐藏的");
            Check("两笔③ 两笔原迹都被收走（文档净 +1、不是 +2）",
                  app.Doc.Strokes.Count == n0,
                  $"文档 {n0} → {app.Doc.Strokes.Count}（期望不变：少两笔、多一个双曲线）");
            Check("两笔④ 撤销栈**只加了一步**", app.Doc.UndoDepth == undo0 + 1,
                  $"撤销栈 {undo0} → {app.Doc.UndoDepth}");

            // ③ **一步** Ctrl+Z 回到**两笔手绘**
            // ⚠ 数的是**增量**，不是"文档里一共几笔手绘" —— 前面各组还留着十几笔呢
            //   （第一版就是数总数，于是报"手绘 18 笔"当失败，其实是断言写错了）。
            int freeBefore = 0;
            foreach (var s in app.Doc.Strokes) if (s.Kind == StrokeKind.Freehand) freeBefore++;
            app.RunActionForTest(KeyAction.Undo);
            int freeAfter = 0, hyper = 0;
            foreach (var s in app.Doc.Strokes)
            {
                if (s.Kind == StrokeKind.Freehand) freeAfter++;
                if (s.Kind == StrokeKind.Hyperbola) hyper++;
            }
            Check("两笔⑤ 一步 Ctrl+Z 回到**两笔**手绘（双曲线没了）",
                  freeAfter == freeBefore + 2 && hyper == 0,
                  $"手绘 {freeBefore} → {freeAfter}（期望 +2）、双曲线还剩 {hyper} 个");
            app.RunActionForTest(KeyAction.Redo);

            // ⑦ 渐近线要**铺到和支线一样远**（用户 2026-09-25 上手反馈："**渐近线看起来
            //    明显太短了，长度要和双曲线匹配**"，并给了对照图）。
            //    判据：渐近线框（`Points[1]`）离中心的距离，**≥ 支线实际铺开的距离** ——
            //    后者用**真墨迹**算（沿实轴方向的最大偏移），不是拿模型的参数自证。
            //    上下开口 ⇒ 实轴是 y 方向。
            var hp = app.Doc.Strokes.Count > 0 ? app.Doc.Strokes[^1] : null;
            float reachInk = 0f;
            foreach (var p in left) reachInk = MathF.Max(reachInk, MathF.Abs(p.Y - 36000f));
            float boxReach = hp == null || hp.Points.Count < 2
                ? 0f : MathF.Abs(hp.Points[1].Y - hp.Points[0].Y);
            Check("两笔⑦ 渐近线铺到和支线一样远",
                  hp != null && hp.Kind == StrokeKind.Hyperbola && boxReach >= reachInk * 0.9f,
                  $"渐近线实轴半宽 {boxReach:F0} vs 支线铺开 {reachInk:F0}");
        }
        {
            // 反例：两笔**不相干**（同一边画了两次）→ **不许**合成双曲线。
            // 用"同一条右支画两遍"是因为它**看起来就像两支、但关于中心不对称** ——
            // 正是这条判据要挡的东西（真机上"两支画在同一边"是会发生的）。
            var same1 = Branch(2200f, 36000f, 80f, 90f, 1.6f, +1f);
            var same2 = Branch(2230f, 36000f, 80f, 90f, 1.6f, +1f);
            Drive(app, same1, 300);
            app.DwellEndForTest();                       // 第一笔不停顿
            int n1 = app.Doc.Strokes.Count;
            Drive(app, same2, 300);
            app.NowMs += DwellAssist.HoldMs;
            app.DwellTickForTest();
            app.DwellEndForTest();
            var bad = app.Doc.Strokes.Count > n1 ? app.Doc.Strokes[^1] : null;
            Check("两笔⑥ 两笔不相干（同一边画两遍）→ **不许**变成双曲线",
                  bad != null && bad.Kind != StrokeKind.Hyperbola,
                  bad == null ? "文档里没多东西" : KindName(bad));
        }

        // ── 覆盖断言（放在最后：`seenKinds` 由上面**所有**小组一起喂）────────────────
        //    识别表里能认的每一种图形，这一趟自检必须**真认出来过**：加了新识别器
        //    却忘了给语料，这里当场红。名单只认 `ShapeRecognize.RecognizableKinds` 一处，
        //    不在这里另写一份（写死名单会随功能移位而静默失效，本仓库踩过好几次）。
        var missingKinds = new List<string>();
        foreach (var k in ShapeRecognize.RecognizableKinds)
            if (!seenKinds.Contains(k)) missingKinds.Add(k.ToString());
        Check("自检覆盖了识别表里每一种图形", missingKinds.Count == 0,
              missingKinds.Count == 0
                  ? $"{seenKinds.Count} 种都出现过（识别表 {ShapeRecognize.RecognizableKinds.Length} 种）"
                  : "漏了：" + string.Join("、", missingKinds));

        Console.WriteLine();
        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>双曲线的**一支**：`x = cx ± a·cosh t`、`y = cy + b·sinh t`（左右开口）。
    /// `sign = +1` 右支、`−1` 左支 —— 两支**互为关于 (cx,cy) 的点反射**，
    /// 正是引擎那条判据（`TryTwoBranchHyperbola` 里的中心对称）要找的东西。</summary>
    private static Vector2[] Branch(float cx, float cy, float a, float b, float tMax, float sign)
    {
        int n = 90;
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = (-1f + 2f * i / n) * tMax;
            pts[i] = new Vector2(cx + sign * a * MathF.Cosh(t), cy + b * MathF.Sinh(t));
        }
        return pts;
    }

    /// <summary>把一串点前后翻转（同一个手势、相反画向）。</summary>
    private static Vector2[] ReverseOf(Vector2[] p)
    {
        var q = (Vector2[])p.Clone();
        Array.Reverse(q);
        return q;
    }

    /// <summary>一个图形的定义元素（画布坐标的副本）。
    /// ⚠ 传 null 时返回空数组、**不抛异常**：这是自检的观察窗，某一条用例没成型时
    /// 它要是把整轮崩掉，后面几十条就白跑了（2026-09-25 踩过：`DefPoints(hShape)`
    /// 在"还没定型"那一刻拿到 null，整轮 FATAL）。</summary>
    private static Vector2[] DefPoints(Stroke s)
    {
        if (s == null) return Array.Empty<Vector2>();
        var a = new Vector2[s.Points.Count];
        for (int i = 0; i < a.Length; i++) a[i] = new Vector2(s.Points[i].X, s.Points[i].Y);
        return a;
    }

    /// <summary>两组定义元素之间的最大漂移（点数不同 = 巨量，表示"结构都变了"）。</summary>
    private static float DefDrift(Vector2[] before, Stroke now)
    {
        if (now == null) return float.MaxValue;
        var after = DefPoints(now);
        if (before.Length != after.Length) return float.MaxValue;
        float worst = 0f;
        for (int i = 0; i < before.Length; i++)
            worst = MathF.Max(worst, Vector2.Distance(before[i], after[i]));
        return worst;
    }

    /// <summary>
    /// 把一串点**按真入口**喂进去：起笔 + 逐点移动。
    /// 时钟按 4ms 一个采样点往前推（真笔大致就是这个密度）。
    /// </summary>
    private static void Drive(App app, IReadOnlyList<Vector2> pts, double drawMs)
    {
        double t0 = app.NowMs;
        app.DwellBeginForTest(pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Count; i++)
        {
            app.NowMs = t0 + drawMs * i / (pts.Count - 1);
            app.DwellMoveForTest(pts[i].X, pts[i].Y);
        }
        app.NowMs = t0 + drawMs;
    }

    /// <summary>一条"手画的"直线：带一点弓 + 手抖（合成语料，和 `--inktest` 同一套思路）。</summary>
    private static Vector2[] WobblyLine(float x, float y, float len, float tiltDeg)
    {
        var rnd = new Random(7);
        float rad = tiltDeg * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
        var nrm = new Vector2(-dir.Y, dir.X);
        int n = Math.Max(4, (int)(len / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            pts[i] = new Vector2(x, y) + dir * (len * t)
                   + nrm * (len * 0.012f * MathF.Sin(MathF.PI * t))
                   + new Vector2((float)(rnd.NextDouble() * 2 - 1) * 0.6f,
                                 (float)(rnd.NextDouble() * 2 - 1) * 0.6f);
        }
        return pts;
    }

    /// <summary>
    /// 一个"手画的"**闭合多边形**（顶点依次一笔走完、回到起点，带手抖）。
    ///
    /// 矩形 / 平行四边形 / 三角形 / 五边形都走它：以前这三份循环各写了一遍，
    /// 一模一样 —— 扫掠要的图形一多，这种复制就会变成"改一处漏一处"。
    /// ⚠ `seed` 是语料的身份：同一个 seed + 同一组顶点 → **逐点一样**，
    /// 所以已有用例的数字不会因为这次收敛而变。
    /// </summary>
    private static Vector2[] PolyPath(Vector2[] v, int seed)
    {
        var rnd = new Random(seed);
        var pts = new List<Vector2>();
        for (int i = 0; i < v.Length; i++)
        {
            var a = v[i];
            var b = v[(i + 1) % v.Length];
            int n = Math.Max(4, (int)(Vector2.Distance(a, b) / 4f));
            for (int k = 0; k <= n; k++)
            {
                if (i > 0 && k == 0) continue;
                float t = k / (float)n;
                pts.Add(Vector2.Lerp(a, b, t) + new Vector2(
                    (float)(rnd.NextDouble() * 2 - 1) * 0.8f, (float)(rnd.NextDouble() * 2 - 1) * 0.8f));
            }
        }
        return pts.ToArray();
    }

    /// <summary>一个"手画的"矩形（四边一笔走完、回到起点、带手抖）。</summary>
    private static Vector2[] RectPath(float cx, float cy, float w, float h)
        => PolyPath(new[]
        {
            new Vector2(cx - w / 2, cy - h / 2), new Vector2(cx + w / 2, cy - h / 2),
            new Vector2(cx + w / 2, cy + h / 2), new Vector2(cx - w / 2, cy + h / 2),
        }, 23);

    /// <summary>一个"手画的"平行四边形（四边一笔走完，上下两边错开 `skew`，带手抖）。</summary>
    private static Vector2[] ParaPath(float cx, float cy, float w, float h, float skew)
        => PolyPath(new[]
        {
            new Vector2(cx - w / 2 - skew / 2, cy - h / 2),
            new Vector2(cx + w / 2 - skew / 2, cy - h / 2),
            new Vector2(cx + w / 2 + skew / 2, cy + h / 2),
            new Vector2(cx - w / 2 + skew / 2, cy + h / 2),
        }, 29);

    /// <summary>
    /// 一个"手画的"**闭合圈**（收口差一点，和 §42.6 的语料一致）：
    /// 圆 = `rx == ry`，椭圆 = `rx ≠ ry`。
    /// </summary>
    private static Vector2[] LoopPath(float cx, float cy, float rx, float ry, int seed)
    {
        var rnd = new Random(seed);
        float sweep = MathF.PI * 2f * 0.94f;
        int n = Math.Max(8, (int)(MathF.Max(rx, ry) * sweep / 4f));
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float ang = sweep * i / n;
            float j = 1f + (float)(rnd.NextDouble() * 2 - 1) * 0.012f;   // 手抖（按半径比例）
            pts[i] = new Vector2(cx + MathF.Cos(ang) * rx * j, cy + MathF.Sin(ang) * ry * j);
        }
        return pts;
    }

    private static Vector2[] Circle(float cx, float cy, float r) => LoopPath(cx, cy, r, r, 11);
    private static Vector2[] EllipsePath(float cx, float cy, float rx, float ry) => LoopPath(cx, cy, rx, ry, 13);

    /// <summary>不规则五边形：**识别表里没有它**，用来当"认不出来"的语料。</summary>
    private static Vector2[] PentagonPath(float cx, float cy, float s)
        => PolyPath(new[]
        {
            new Vector2(cx - 1.05f * s, cy - 0.55f * s), new Vector2(cx + 0.15f * s, cy - 1.15f * s),
            new Vector2(cx + 1.15f * s, cy - 0.15f * s), new Vector2(cx + 0.55f * s, cy + 1.0f * s),
            new Vector2(cx - 0.95f * s, cy + 0.75f * s),
        }, 37);

    /// <summary>扫掠用：按名字取一档语料（`s` 是尺寸系数）。</summary>
    private static Vector2[] SamplePath(string kind, float s, float cx, float cy)
        => kind switch
        {
            "直线" => WobblyLine(cx - 150f * s, cy, 300f * s, 0f),
            "圆" => Circle(cx, cy, 120f * s),
            "椭圆" => EllipsePath(cx, cy, 170f * s, 105f * s),
            "三角形" => PolyPath(new[]
            {
                new Vector2(cx, cy - 110f * s), new Vector2(cx + 130f * s, cy + 90f * s),
                new Vector2(cx - 130f * s, cy + 90f * s),
            }, 31),
            "矩形" => RectPath(cx, cy, 240f * s, 150f * s),
            "平行四边形" => ParaPath(cx, cy, 240f * s, 150f * s, 70f * s),
            // 二次函数（四种开口）：**顶点在中间**（绕顶点两侧都画了）。
            // ⚠ 只画半支的**故意不认** —— 模型画抛物线是绕顶点对称铺开的（见
            //   `ShapeRecognize.TryFitParabola` 门槛 ③），半支会被凭空补出另一半。
            "抛物线" => ParabolaPath(cx, cy, 150f * s, 130f * s),
            _ => PentagonPath(cx, cy, 120f * s),          // "五边形（认不出）"
        };

    /// <summary>一条**顶点在中间**的抛物线（开口向上）：横向半跨度 `hw`、两端比顶点高 `rise`。
    /// 画布 y 向下，所以"向上开口"是 `cy − …`。</summary>
    private static Vector2[] ParabolaPath(float cx, float cy, float hw, float rise)
    {
        int n = 64;
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float u = -1f + 2f * i / n;
            pts[i] = new Vector2(cx + u * hw, cy - rise * u * u);
        }
        return pts;
    }

    /// <summary>当前半成品图形**定义元素的首末两点**之间的距离 —— 正是
    /// <c>Engine.ShapeDragLongEnough</c> 量的那个数（自检要用真值，别自己另算一套）。</summary>
    private static float DefChord(App app)
    {
        var s = app.ActiveStroke;
        if (s == null || s.Points.Count < 2) return 0f;
        return Vector2.Distance(new Vector2(s.Points[0].X, s.Points[0].Y),
                                new Vector2(s.Points[^1].X, s.Points[^1].Y));
    }

    private static string KindName(Stroke s)
        => s == null ? "无" : (s.Kind == StrokeKind.Freehand ? $"笔迹({s.Points.Count}点)" : s.Kind.ToString());
}
