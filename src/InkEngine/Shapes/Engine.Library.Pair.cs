// [职责] 收藏库杂项：双支配对/隐藏配对/结束笔画。
// [测试] --inktest

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;


public partial class InkEngine
{
    internal int LibraryDrawerVisibleCountForPaint => LibraryDrawerVisibleCount();
    internal bool LibraryRenameEditingForTest
        => TextInput?.IsEditing == true
           && string.Equals(TextInput.EditingId, "library.rename", StringComparison.Ordinal);
    internal void SetLibraryRenameTextForTest(string text) => _textInput?.SetTextForTest(text);
    internal void EndLibraryCategoryRenameForTest(bool save) => EndLibraryCategoryRename(save);
    internal bool LibraryManageSortingForPaint => LibraryManageSorting;
    internal int LibraryManageDropIndex;
    internal bool LibraryManageModeForTest => LibrarySelectMode;
    internal ShapeLibrary.Entry LibraryUseSelectedForTest => LibraryUseSelected;

    private void InsertLibraryGroupAt(IReadOnlyList<ShapeLibrary.Entry> entries, float x, float y)
    {
        if (entries == null || entries.Count == 0) return;
        var strokes = new List<Stroke>();
        foreach (var e in entries)
            if (e.Strokes != null) strokes.AddRange(e.Strokes);
        if (strokes.Count == 0) return;
        _libraryPending = strokes;
        _libraryPendingTitle = entries.Count == 1 ? entries[0].Title : $"{entries.Count} 项";
        TryInsertLibraryAt(x, y);
        SetInkStatus(entries.Count == 1
            ? $"已插入「{entries[0].Title}」"
            : $"已插入 {entries.Count} 项");
        // 图钉灭 = 放完这一张就让开（面板状态仍然保留，再点收藏格可回来）。
        if (!LibraryPanelPinned) CloseLibraryPanel();
        _dirty = true;
    }

    /// <summary>
    /// 改**配对期隐藏**这一位，并且**顺手让它对应的内容块失效**。
    ///
    /// ## 为什么必须有这个函数的"顺手失效"（这是用户 2026-09-25 报的 bug）
    ///
    /// 症状：画两支的时候，**停顿那一刻第一笔没消失，等抬手才消失**。
    ///
    /// 根因是**只改了字段、没让渲染缓存失效**（正是本仓库记过的一条老教训）：
    /// 内容层不是每帧重画、而是**分块位图缓存**（`CanvasTileCache`），第一笔这一刻
    /// **早就烘进块里**了；而块只在 `Doc.Version` 变化时才重光栅化（`Overlay.SyncTiles`）。
    /// 于是只置字段的话：`Overlay.DrawStroke` 确实会跳过它，但**没有任何一帧重画那一块**，
    /// 屏幕上原样留着旧像素 —— 一直留到抬手提交（那一步 `RemoveStroke` 抬了版本号，
    /// 块被重画），**看起来就是"松手以后才消失"**。
    ///
    /// 所以"藏"和"让那一块重画"必须**同时**发生 —— 这就是这个函数存在的全部理由，
    /// 两个调用点（藏 / 放回来）都要走它，**不要直接给字段赋值**。
    /// </summary>
    private void SetPairHidden(Stroke s, bool on)
    {
        if (s == null || s.HiddenForPairing == on) return;
        s.HiddenForPairing = on;
        // 模型本身一个字没改（这一笔还在文档原处、撤销栈照样靠它），
        // 只是告诉渲染层"这块要重来"（见 Document.InvalidateContent 的注释）。
        Doc.InvalidateContent(s.PaddedBounds);
    }

    /// <summary>
    /// **两笔配对**：这一笔和"上一笔"是不是同一个双曲线的两支（用户 2026-09-25 定的约定）。
    ///
    /// &gt; "**画两支就是双曲线，画一支就是抛物线**。不管它实际上是抛物线还是双曲线，
    /// &gt;  我们只要按照这个来区分。"
    ///
    /// 为什么这一刀比"判形状"干净：形状判断是**连续、有噪声**的（夹角、残差都栽在这儿 ——
    /// 真实手抖下双曲线只剩 25~40%），而"**画了几笔**"是**离散、零噪声**的：老师画两支的时候，
    /// **他自己知道**在画双曲线。所以族的选择**不再交给拟合**，拟合只负责"造一个像的"。
    ///
    /// 认出来 → 返回那个双曲线，并把"上一笔"钉在 <see cref="_twoBranchPrev"/> 上
    /// （抬手提交时**一起收走**，见 `EndStroke`）；认不出 → `IsNothing`，
    /// **这一笔照原来的单笔路走**（绝不抢活）。
    ///
    /// 挑"上一笔"的四条，都很保守 —— 因为**误配的代价是"把两笔变成一个"**：
    ///   ① **文档里最后一笔**（不往前找更早的，那会开始猜）；
    ///   ② 在 **20 秒**窗口内（见 <see cref="TwoBranchWindowMs"/>）；
    ///   ③ 是**手绘的墨**（`Freehand`）—— 已经成型的对象不参与；
    ///   ④ 两边点数都够，最后交给 `TryTwoBranchHyperbola` 判**中心对称**。
    /// </summary>
    private ShapeGuess TryTwoBranchPair(Stroke ink, Vector2[] pts)
    {
        if (Doc.Strokes.Count == 0) return ShapeGuess.None("没有上一笔");
        if (NowMs - _lastInkEndMs > TwoBranchWindowMs) return ShapeGuess.None("上一笔太久");
        var prev = Doc.Strokes[^1];

        // **诊断日志**（用户 2026-09-25 上手"一个也画不出来"）：
        // 一行说清"上一笔什么样、这一笔什么样、卡在哪一道闸"。
        // 真机排查全靠它 —— 否则只能靠猜，而这条路已经猜错好几轮了。
        // 限流：最多每 500ms 一行（这个方法在停顿期间每 40ms 走一次）。
        bool log = NowMs - _pairLogMs > 500;
        if (log) _pairLogMs = NowMs;

        ShapeGuess g;
        // **"第一支"用哪一串点**：正常就是那一笔手绘的墨；
        // 但要是它**刚被停顿成型过**（收笔前手停了一下），对象里只剩 2~3 个定义点，
        // 这里就用**当初那一笔原迹**（引用比较确认它还是文档里最后一笔，见字段注释）。
        // 拿不到就置 null（= 这一笔不能当"第一支"用）。
        var prevInk = prev;
        if (prev.Kind != StrokeKind.Freehand)
            prevInk = prev == _lastDwellShape && _lastDwellInk != null ? _lastDwellInk : null;

        if (prevInk == null)
        {
            // ⚠ 这一条**很容易中**：第一支要是也停顿过（那 400ms 的静止），
            //   它就**已经变成一个对象**了，而对象的 `Points` 只剩定义点、
            //   **拿不回原来那些采样点**，于是配不成对。日志里会打出 `上一笔 Parabola`。
            //   （2026-09-26 起：**刚停顿成型的那一笔**能从 `_lastDwellInk` 拿回原迹，
            //    所以只有"更早的、别处来的对象"才会走到这里。）
            g = ShapeGuess.None("上一笔不是手绘的墨（它已经被成型过了）");
        }
        else if (prevInk.Points.Count < 8 || pts.Length < 8)
        {
            g = ShapeGuess.None("有一笔太短");
        }
        else
        {
            var prevPts = new Vector2[prevInk.Points.Count];
            for (int i = 0; i < prevPts.Length; i++)
                prevPts[i] = new Vector2(prevInk.Points[i].X, prevInk.Points[i].Y);
            g = ShapeRecognize.TryTwoBranchHyperbola(prevPts, pts, DpiScale);
            if (!g.IsNothing)
            {
                _twoBranchPrev = prev;
                _twoBranchIndex = Doc.Strokes.Count - 1;
                // ★ **幽灵期先把第一笔藏起来**（用户 2026-09-25 定：
                //   "识别的那一刻，第一笔就该消失"）—— 原来要等抬手提交才消失。
                //   ⚠ 只是**不画**（`Overlay.DrawStroke` 会跳过），文档一个字没动 ——
                //     撤销栈要靠它还在原处。
                SetPairHidden(prev, true);
            }
        }

        if (log)
            Console.WriteLine($"[两笔配对] {(g.IsNothing ? "否" : "**是双曲线**")}：{g.Rule}"
                              + $"（上一笔 {prev.Kind}·{prevInk?.Points.Count ?? 0} 点"
                              + $"{(prevInk != null && prevInk != prev ? "（用它当初的原迹）" : "")}，"
                              + $"这一笔 {pts.Length} 点）");
        return g;
    }

    private void EndStroke()
    {
        InputBlackBox.End(NowMs);   // 黑匣子：收笔（含"每笔收到即落盘"）
        _lastStrokeEndMs = NowMs;   // 板书历史："手停了没有"的判据（见 MaybeHistorySnapshot）
        // 触摸手势的兜底中断（8.4.0）：正常收笔时 `_touchMode` 已经是 None（触摸那条自己收过），
        // 只有"丢捕获 / 意外路径"会带着没结束的模式走到这里——把开了头的（擦除批次、选中拖动）收干净。
        TouchAbort();

        // 激光笔抬手：这条**开始计时**（停留 2 秒后再整体淡出，见 `LaserTrail.HoldMs`）。
        // 放在最前面：下面那几条分支（截屏 / 图形 / 多笔）都和激光笔无关，不必等它们；
        // 而且**每一条收笔路径都要走到**（正常抬手、丢捕获都走 `EndStroke`）——
        // 漏一次的话那条轨迹就永远是"还在写"，既不淡也不会消失。
        Laser.Release(NowMs);

        if (ScrollBarDragging) EndScrollBarDrag();
        // `--syswet`：湿墨只有系统轨迹这一份，撤除要等干墨那一帧 Present 之后
        //（否则"轨迹先没了、干墨还没上屏"会闪一帧）；其余路径照旧立即撤。
        bool sysWetEnding = _systemWetStroke != null;
        foreach (var w in _windows)
            if (sysWetEnding) w.EndInkTrailDeferred(); else w.EndInkTrail();
        _systemWetStroke = null;
        // **录墨迹**（用户 2026-09-26 提"我手画多少条双曲线给你，你按这些来定制判据"）：
        // 把这一笔的**原始采样点**追加到录制文件。放在最前面 —— 此时 `ActiveStroke`
        // 要么还是原始墨、要么是停顿成型换掉的那个对象（所以真正要用的点见 `_recordPts`）。
        FlushInkRecord();
        // **抬手就关掉停顿那颗定时器**：它只在"有笔在写"的时候有意义（见 StartDwellTimer）。
        StopDwellTimer();
        // [删除 2026-10-05] 收笔时清"预测尾"：随老预测系统一起移除（渲染尾已不存在）。
        Doc.EndErase();
        // 面积擦抬手：框回基准尺寸（悬停时显示的 = 下次按下去第一下擦掉的那一块）。
        PixelEraseDragging = false;
        if (CaptureActive)
        {
            // 起框那一次松手 → 进调整阶段；调整里拖手柄那几次松手 → 只收手。
            if (!CaptureAdjusting) EndCapture();
            else EndCaptureAdjustDrag();
            _capturePressHandled = false;
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        // 确认/取消那一下抬手：截图已经收场，别再落到"框选"那条路上
        //（不然 Tool=Marquee 会让它顺手把刚落下的图取消选中）。
        if (_capturePressHandled)
        {
            _capturePressHandled = false;
            _drawing = false;
            _dirty = true;
            _cntDown = _cntMove = _cntUp = _cntCaptureLost = 0;
            return;
        }
        // 记下"**这一笔到此结束**"的时刻 —— 两笔配对（画两支 = 双曲线）要用它算时间窗口
        // （见 `TwoBranchWindowMs`，现在是 20 秒）。
        // ⚠ 放在这里（**截屏那条分支之后**）：`CaptureActive` 那条路也走 `EndStroke`，
        //   截一次屏不该把两笔的窗口冲掉。
        _lastInkEndMs = NowMs;

        if (ActiveStroke != null)
        {
            // **多笔图形**：松手 = **这一笔完成**（照 InkClass：推进就发生在 MouseUp，
            // 见 `MW_ShapeDrawing.cs:1814-1822` 的 `drawMultiStepShapeCurrentStep`）。
            // 推进一步之后再看"还有没有下一笔"。
            //
            // 注意这里**不以"拖了多远"为准**：多笔图形的每一笔定的是**一个几何量**
            //（渐近线框 / 曲线过哪），"在目标位置点一下"和"拖过去"一样有效 ——
            // 所以不拿 ShapeDragLongEnough 去卡它（那是给一笔成形的图形防误点用的）。
            if (_stepPlan != null) _stepIndex++;

            // 松手就把"**这一笔吸到了什么**"收回去（「直棱柱」那颗胶囊只在拖动中出现，
            // 和拖顶点吸附那边同一个口径：松手就没有了）。不清的话它会一直挂在屏幕上。
            _stepSnap = ShapeSnapKind.None;

            // 还有下一笔 → 这一下松手**不算完**：半成品停在 ActiveStroke 里
            //（**不进文档、也不写撤销记录**），等最后一笔松手再统一提交。
            // 这样在用户眼里这几笔是连着的，中途不会先冒出一条"半成品"曲线、再把它改掉，
            // 撤销也不会多出好几条记录（InkClass 同样把两笔塞进同一个提交里，`:1846-1850`）。
            bool stepPending = _stepPlan != null && _stepIndex < _stepPlan.Apply.Length;

            // 图形：拖动太短 = 误点，**不提交**。什么都不留（连撤销记录都不留）——
            // 一个退化的图形（零长度直线、零面积矩形）在画面上看不见，却能被点中、
            // 能被框选，是最难解释的一类杂物。
            //
            // **多笔图形**没有"拖多长"可依（见上），所以改看**定义元素齐没齐**：
            // 双曲线少一个"曲线经过的点"就等于什么都没定下来（`MinCurvePoints` 说它要三个点），
            // 这种半成品不提交。尺寸下限在各自的算式里已经卡死
            //（`SetHyperbolaFromAsymptote`），退化不成"看不见却占着一条对象"。
            bool commit = !stepPending
                       && ActiveStroke.Points.Count > 0
                       && (_stepPlan != null
                           ? ActiveStroke.Points.Count >= Stroke.MinCurvePoints(ActiveStroke.Kind)
                           // 三种来源分三档：
                           //   · 自由笔迹：不卡长度（它本来就是一笔一画）；
                           //   · **停顿变出来的图形**：也不卡长度 —— 识别器已经验收过它
                           //    （总长 ≥ 40 逻辑像素、每条边都贴得住墨，见 ShapeRecognize）；
                           //     而**这条门槛量的是图形、不是墨**（"定义元素首末两点够不够远"），
                           //     对"定义元素天生就短"的图形会误杀，一误杀就是**图形和手绘原迹
                           //     一起被丢掉**（用户 2026-09-24 报的"图形整个会消失掉"就是这一类）。
                           //     ⚠ 今天这一档**够不到**：识别器的"总长 ≥ 40 逻辑像素"把定义元素的
                           //     尺度顶在了这条门槛之上（圆：r ≥ 6.4 逻辑像素 > 4）。留着它是防
                           //     "以后加一个定义元素很小的图形"（比如一个"点"）——那时它会立刻用上，
                           //     而它挡的是**用户的墨凭空消失**，不是省一行代码的事；
                           //   · 图形工具：照旧要"拖够长"（防误点、防画出退化的零面积图形
                           //     ——那种东西看不见却点得中，是最难解释的一类杂物）。
                           : (!IsShapeTool(ActiveStroke.Tool) && _dwellInk == null)
                             || _dwellInk != null
                             || ShapeDragLongEnough(ActiveStroke));

            // **取消选中那一击不留墨**（用户 2026-09-23 定）。
            //
            // 起因：停顿变出来的图形是自动选中的，而"点一下别处"的意图是收起那个框。
            // 那一击原本会落**一个实心圆点**（`Stroke.IsSinglePoint`），于是"取消选中"
            // 这个纯粹的动作会在屏幕上留下墨——**每次取消都脏一块**，板书时特别烦。
            // 判据是"整段位移 ≤ DwellTapSlopLogical"，所以真画一笔绝不会被吃掉。
            //
            // ⚠ **停顿成型那一笔不在这条里**（`_dwellInk == null` 才算）：它既然被识别器
            //   认出来了，就绝不可能是"点一下"（识别器要总长 ≥ 40 逻辑像素）；而这里量的是
            //   **图形**的首末两点 —— 圆的定义元素是（圆心，圆周点），首末两点距离就是**半径**，
            //   半径小于点选容差（8 逻辑像素）的圆会正好落进来，整笔（图形 ＋ 手绘原迹）
            //   被当成"点一下"抹掉。而这条**够得着**：圆的识别门槛是 2πr ≥ 40 → r ≥ 6.4 逻辑
            //   像素，所以 6.4~8 这一段的圆真会被误杀（`--dwelltest` H4 用半径 7.5 逻辑像素的圆
            //   钉住；把那半句删掉它当场红："整笔没了"）。
            if (commit && _dwellInk == null && _dismissTapArmed && !IsShapeTool(ActiveStroke.Tool)
                && _stepPlan == null && DwellTapLeaveNoInk(ActiveStroke))
            {
                commit = false;
                Console.WriteLine("[停顿成型] 这一下只是取消选中，不留墨");
            }

            // 这一次提交的是不是一个**刚成型的图形**（要不要自动选中它，见下面"画完自动选中"）
            Stroke committedShape = null;
            if (commit)
            {
                // **双曲线的渐近线按档收口**（用户 2026-09-22："化的时候是都有渐近线，
                // 但是最终显示没有"）：画的过程中一直是"有"（那是向导，见 BeginShapeAt），
                // 交到文档里的这一刻才按面板那一档决定 —— 于是"无渐近线"那一档
                // 画的时候照样看得到那两条虚线，画完就只剩曲线。
                // ⚠ 必须走 `SetShowAsymptotes`（它会抬 Revision）——直接给字段赋值的话，
                // 屏幕上那份"带渐近线"的辅助几何缓存在那儿不重建，画完照样看得见那两条虚线
                //（用户 2026-09-22 报的就是这个："我选择的不带渐近线的，但是画完以后还有渐近线"）。
                if (ActiveStroke.Kind == StrokeKind.Hyperbola)
                    ActiveStroke.SetShowAsymptotes(HyperbolaAsymptotes);
                if (_dwellInk != null)
                {
                    // 停顿成型：这一笔不再是"正在写"（曲线化恢复生效）。即使定型成的是
                    // 自由笔迹（`_dwellInk` 那条路），也不该再带着"活笔"的标志。
                    ActiveStroke.RawWhileLive = false;
                    // **停顿成型**：走"替换型"提交——图形进文档，手绘原迹跟着撤销栈走，
                    // 于是按一次 Ctrl+Z 回到**自己画的那一笔**（见 DwellShapeAction）。
                    //
                    // ★ **两笔成型**（`_twoBranchPrev != null`，用户 2026-09-25 定的
                    //   "**画两支就是双曲线**"）：这时**上一笔也要收走** ——
                    //   `RemoveStroke` 是"效果先应用"，记账全由下面那条动作负责
                    //   （它带两笔原迹，所以 **一步 Ctrl+Z 回到两笔手绘**）。
                    if (_twoBranchPrev != null)
                    {
                        var prev = _twoBranchPrev;
                        int prevIdx = _twoBranchIndex;
                        _twoBranchPrev = null;
                        _twoBranchIndex = -1;
                        // ⚠ **必须先把隐藏位清掉**：不清的话 Ctrl+Z 把这一笔放回来时
                        //   它还是隐身的 —— 屏幕上"撤销之后什么都没回来"，而且再也变不回来 ✗
                        //   （走 `SetPairHidden` 而不是直接赋值：它会把"那一笔原来待的块"
                        //     一起标脏，见那个函数的注释）
                        SetPairHidden(prev, false);
                        Doc.RemoveStroke(prev);
                        Doc.AddDwellShape(ActiveStroke, _dwellInk, prev, prevIdx);
                    }
                    else
                    {
                        Doc.AddDwellShape(ActiveStroke, _dwellInk);
                        // **把"这一支的原迹"留一个引用**（见 `_lastDwellShape` 那段注释）：
                        // 画双曲线的第一支时手要是停了一下，它就先变成了一个**抛物线对象**，
                        // 而对象里拿不回采样点 → 两笔配对就断了。只认**抛物线**这一种：
                        // 那是"画一支双曲线被误判成抛物线"的真实情形；别的种类（圆 / 椭圆…）
                        // 不在这里放行 —— 它们的形状和"一支双曲线"差太远，误配的代价更大。
                        if (ActiveStroke.Kind == StrokeKind.Parabola)
                        {
                            _lastDwellShape = ActiveStroke;
                            _lastDwellInk = _dwellInk;
                        }
                    }
                    _dwellCommitted = true;
                    // **直线抬手后不选中**（用户 2026-09-23 定）：直线是"顺手一划"，画完要立刻接着
                    // 写下一笔——弹出一个选中框 + 操作条反而挡路；按住期间已经能调（转向/伸缩），
                    // 抬手之后想再调就再点它一下（和 GoodNotes"松手后点一下才选中"同一个手感）。
                    // ⚠ 这一条例外**只在这条路上**：图形工具画的直线照旧自动选中（那是既有设计）。
                    if (ActiveStroke.Kind != StrokeKind.Line) committedShape = ActiveStroke;
                    Console.WriteLine($"[停顿成型] 定型 {ActiveStroke.Kind}"
                                      + (committedShape != null ? "（已选中；Ctrl+Z 可回手绘）" : "（未选中；Ctrl+Z 可回手绘）"));
                }
                else
                {
                    // 落笔：从这一刻起曲线化生效（几何缓存键里带着这个标志，会自己重画）
                    ActiveStroke.RawWhileLive = false;
                    Doc.AddStroke(ActiveStroke);
                    // 真提交进文档了才算"成型"——下面那一步要拿它做自动选中。
                    if (IsShapeTool(ActiveStroke.Tool)) committedShape = ActiveStroke;
                }
                // **先结账再打印**：这一段（含 AddStroke 的快照/缓存收拾）才是分配最集中的地方，
                // 晚一步收仪表，印出来的就是上一笔的数字。
                EndStrokeMeasure();
                // 这一笔的**历时 + 真实采样率**（2026-10-07 加，10-07 晚修正口径）：
                // "收到 12 个点"本身说明不了任何事——12 个点摊在 0.1 秒上是 120Hz（正常），
                // 摊在 0.8 秒上就只有 15Hz（异常）。排查"点是不是被稀疏了"必须先有这个分母。
                //
                // ⚠ **口径必须是"采样点/秒"，不是"消息/秒"**：开 Windows Ink 时系统会把
                //   2~3 个硬件采样合并进一条消息（真机实测 12 条消息 → 31 个采样点），
                //   拿消息数当频率会**把笔的采样率报低一半以上**（第一版就是这么写的）。
                //   同时把合并比打出来——它本身就说明"这一路到底有没有合并可恢复"。
                // 普通手写用第一个点的采样时间；形状识别 / 图形工具的点 T 是 0，
                // 退回按下时刻，否则会印出“历时 28 分钟”这种假数（2026-10-19 学校日志）。
                double pointT = ActiveStroke.Points.Count > 0 ? ActiveStroke.Points[0].T : 0;
                double strokeStart = pointT > 0 ? pointT : _measureStartMs;
                double strokeMs = Math.Max(0, NowMs - strokeStart);
                double sampleHz = strokeMs > 1 ? LastCoalescedSamples * 1000.0 / strokeMs : 0;
                double msgHz = strokeMs > 1 ? LastCoalescedMessages * 1000.0 / strokeMs : 0;
                string merge = LastCoalescedMessages > 0
                    ? (LastCoalescedMessages == LastCoalescedSamples ? "1:1（系统没合并）"
                       : $"{(double)LastCoalescedSamples / LastCoalescedMessages:F1}:1（合并历史已恢复）")
                    : "无";
                // **这一笔真正写进点里的压力区间**（2026-10-07 加）。
                //
                // 为什么必须有它：那天我连着犯两次同类的错 ——
                //   ① 兜底值取 0.5 → 开头鼓粗点
                //   ② raw 补点写成 0 → 开 ink 时整条线全是细口子（用户："笔记很脏"）
                // 两次都**只有一个数字能看出来**：点的压力最小值/最大值。
                // 光看"采到的压力"那一栏是不够的 —— **采到了**和**用上了**是两件事，
                // 中间任何一段出错它都看不见。
                float ptMinP = 0f, ptMaxP = 0f;
                int ptSteps = 0;                       // 压力"跳变"次数（相邻点压力不同的次数）
                if (ActiveStroke.Points.Count > 0)
                {
                    ptMinP = float.MaxValue; ptMaxP = float.MinValue;
                    foreach (var q in ActiveStroke.Points)
                    {
                        if (q.P < ptMinP) ptMinP = q.P;
                        if (q.P > ptMaxP) ptMaxP = q.P;
                    }
                    for (int i = 1; i < ActiveStroke.Points.Count; i++)
                        if (MathF.Abs(ActiveStroke.Points[i].P - ActiveStroke.Points[i - 1].P) > 0.0005f) ptSteps++;
                }
                _lastStrokeReport =
                    $"路径={DescribeInputPath(_activePointerType)}"
                    + $"　采集到 {ActiveStroke.Points.Count} 个点"
                    + $"，历时 {strokeMs:F0} ms → 采样 {sampleHz:F0} Hz（消息 {msgHz:F0} Hz）"
                    + $"，收到 按下{_cntDown} 移动{_cntMove} 抬起{_cntUp} 丢失捕获{_cntCaptureLost}"
                    + $"，设备={PointerTypeName(_activePointerType)}"
                    + $"，压感={(ActiveStrokeHasPressure ? "有" : "无")}"
                    // 真正落到点里的压力区间。**有压感但 min 是 0** 就说明有路把点写成了 0
                    // （2026-10-07 那个"笔记很脏"的 bug 正是这样：raw 补的点全被写成 0）。
                    + (ActiveStroke.Points.Count > 0
                        ? $"，点压力 {ptMinP:F2}~{ptMaxP:F2}（跳变 {ptSteps}/{ActiveStroke.Points.Count} 点）"
                          + (ActiveStrokeHasPressure && ptMinP <= 0.001f ? " **有0！疑似有路把它写成0**" : "")
                        : "")
                    + $"，合并 {merge}（{LastCoalescedMessages} 条消息 → {LastCoalescedSamples} 点）"
                    // 投递结构（2026-11-17，T0 诊断）：**这批点的时标到底是不是同一个**。
                    // "合并"那一栏只给平均点数，看不出时标；而"批内同刻"正是预测器
                    // `dt<=0` 分支被反复跳过、速度被低估/方向取旧段的前提。
                    // 真机读数（Win10 学生机 / 手写板关 Ink / 学校大屏各写两分钟）决定
                    // T1「时间组 + 最小二乘速度」是 P0 还是 P2——不凭推断动核心算法。
                    + InputDeliveryStats.Describe()
                    // 委托轨迹（2026-10-11）：**喂进去 ≠ 留在系统里**——默认模式下我们自己
                    // 画到哪就把轨迹结账到哪，所以"结账"应当≈"喂"；只喂不结账（结账远小于喂）
                    // 就是"长笔画中途变透明"的那种病。`--trailonly` 下我们自己不画、也就不结账
                    //（那正是"湿墨只剩系统轨迹"的诊断模式，结账 0 是预期）。
                    + (OverlayWindow.InkTrailEnabled
                        ? $"，[轨迹 喂{OverlayWindow.InkTrailPointsFed - _trailFedSeen}"
                          + $" 结账{OverlayWindow.InkTrailPointsRemoved - _trailRemovedSeen}]"
                        : "")
                    + (RawProbeEnabled
                        // 时长≈0 的空笔（退出收尾结算）不报频率：`条数 / 1ms` 会印出
                        // "14000 Hz" 这种假数（2026-10-08 用户两份数据结尾都有）。
                        ? $"，[原始输入 {_rawReportsThisStroke} 条 → " + (strokeMs > 1
                            ? $"{_rawReportsThisStroke * 1000.0 / strokeMs:F0} Hz]"
                            : "—（时长≈0）]")
                        : "")
                    + (RawInputCapture
                        ? $"，[raw 补点 +{_rawPointsAdded}]"
                          + (RawProbeEnabled
                             ? $"[挡：无笔{_rawSkipNoStroke} API{_rawSkipApi} 类型{_rawSkipType} 绝对{_rawSkipAbs} 零移{_rawSkipZero}"
                               + $" 非手写{_rawSkipKind} 门{_rawSkipGate} 无锚{_rawSkipAnchor} 无中间点{_rawSkipNoInter} 上限{_rawSkipCap} 太近{_rawSkipNear}]"
                             : "")
                        : "")
                    // [删除 2026-10-05] 预测器/预测尾/喂 DWM 段数的日志：随预测系统一起移除。
                    // 分配与 GC：低配机排查"偶发卡顿"的**唯一依据**。
                    // 第 2 代那一位出现在书写期间，就说明这一笔画到一半被全堆回收打断过。
                    + $"，分配 {StrokeAllocBytes / 1024.0:F1} KB（整笔会话累计，非驻留）/GC {StrokeGc0}/{StrokeGc1}/{StrokeGc2}";
                Console.WriteLine("[笔画] " + _lastStrokeReport);
                // `--strokefile`：把这一行**追加到文件**。
                // 为什么要有它：这一行的数字（尤其"压力 a~b/上限"）是调手感参数的**唯一依据**，
                // 而截图会折行、会被笔迹挡住、会转述错——**今天已经因此栽了三次**。
                // 默认关（不写文件、零开销），要诊断时才开。
                if (_strokeFilePath.Length > 0)
                {
                    try
                    {
                        System.IO.File.AppendAllText(_strokeFilePath,
                            $"[{DateTime.Now:HH:mm:ss}] {_lastStrokeReport}{Environment.NewLine}",
                            new System.Text.UTF8Encoding(true));
                    }
                    catch (Exception ex) { Console.WriteLine($"  ⚠ 写笔画日志失败：{ex.Message}"); }
                }
                if (StrokeGc2 > 0)
                    Console.WriteLine($"  ⚠ 这一笔期间发生了 {StrokeGc2} 次第 2 代 GC"
                                      + "（低配机上这就是一次可见的卡顿，值得查是哪里在分配）");
            }
            else if (!stepPending && IsShapeTool(ActiveStroke.Tool) && _stepPlan == null)
            {
                Console.WriteLine($"[图形] 拖动太短（< {ShapeMinDragLogical:F0} 逻辑像素），没提交");
            }
            // 多笔式没走完：**连 ActiveStroke 都不清**——它还要接着给下一笔当预览用；
            // 表和"画到第几笔"也跟着留在原地（下一笔按下时要用）。
            if (!stepPending)
            {
                ActiveStroke = null;
                _stepPlan = null;
                _stepIndex = 0;
            }

            // **画完自动选中**（用户 2026-09-22 定）：图形一成型就把它的选中框亮出来，
            // 老师可以立刻拖它、拖顶点改形状——不用先去点"框选"再回头点它一下。
            // （截图和粘贴早就是"落下就选中"，这条把图形也拉齐了。）
            //
            // **工具不换**（这一点和截图 / 粘贴不同，它们换成了框选，因为"接着画下一个"
            // 对它们不存在）：手里还是原来那个图形工具，所以"这一下算动它还是算接着画"
            // 的分流落在 `OnPointerDown`（`AutoSelectionPress`）。
            //
            // 而且刚画完是**收起来**的形态（一颗圆钮），点一下它才展开成常规那一套
            // ——理由见 `_autoSelCollapsed`（用户上手之后定的：一整条摊在图形下面又重又挡）。
            // **面板选的一次性图形：画完一个就回到上一次的非图形工具**（和收藏插入后
            // 回到原工具同一套直觉）。直接在主条选的图形工具不在此列，保持原来“接着画”。
            bool oneShotShape = _shapeOneShot && committedShape != null;
            if (oneShotShape)
            {
                _shapeOneShot = false;
                var back = _shapeOneShotReturn;
                if (back == Tool.Capture || back == Tool.Laser) back = Tool.Pen;
                // 画完先选中；工具切到框选，点空白时沿用收藏插入那一套“回到上一个工具”。
                SwitchTool(Tool.Marquee);
                Doc.SelectOnly(new[] { committedShape });
                _autoSelCollapsed = false;
                _toolBeforeLibraryInsert = back;
                _restoreToolAfterLibrarySelection = true;
                _dirty = true;
                NotifyUiStateChanged();
            }
            else if (committedShape != null)
            {
                Doc.SelectOnly(new[] { committedShape });
                _autoSelCollapsed = true;
                _dirty = true;
            }
            // **停顿变出来的那个框，工具还是笔** —— 所以还要记一笔"这一次选中
            // 是停顿给的"，否则那个框会"画得出来、点不着"（见 `_dwellSelected`）。
            // 直线上这一位是 false（它抬手不选中，见上面）：没有选中框，也就没有"点不着"的问题。
            _dwellSelected = committedShape != null && _dwellCommitted;
        }
        // 停顿成型这一笔的账在这里结清：状态机复位、原迹引用放手
        //（原迹已经交给撤销栈了，见 DwellShapeAction —— 这里放手不会丢东西）。
        _dwell.Reset();
        _dwellInk = null;
        _dismissTapArmed = false;
        // 拖动**手柄**（改几何）的收尾：**和当前工具无关**。
        //
        // 挂在下面"选择工具"那条分支里是不够的：手柄拖动属于**选中**那一套，
        // 而松手时手上的工具可能已经不是选择工具了（拖到一半按热键换了工具，
        // 见 SwitchTool——它只作废多笔图形的半成品，不动手柄拖动）。
        // 那种情况下这一拖会**永远不提交**：屏幕上拖得好好的，松手一看 a 一点没变
        //（自检当场抓出来过）。
        //
        // ⚠ 判据是 `SelDragging` 而**不是**"是不是框选工具"：画完自动选中那个框
        //（见上面 `AutoSelectionPress` 那段）是在**图形工具**下起手拖的，
        // 按工具判的话这一拖同样永远不提交。
        // 文本框的双击编辑**等松手再兑现**：按下去先交给选择手势，
        // 拖过就是移动，没拖才是双击。必须在 EndSelDrag 之前读 `_selDragMoved`，
        // 它在收尾里会被清掉。
        bool pendingTextEdit = _pendingTextEdit && SelDragging
                            && !_selDragMoved && _dragHandle == SelHandle.None;
        bool wasSelDragging = SelDragging;
        if (wasSelDragging) EndSelDrag();
        if (wasSelDragging)
        {
            if (pendingTextEdit) EnterPendingTextBoxEdit();
            else _pendingTextEdit = false;
        }
        else if (_sliderDragging) EndWidthSliderDrag();        // 粗细滑条：松手 = 一步撤销
        else if (_colorDragging) EndColorPickerDrag(PointerX, PointerY);   // 取色板：板内应用 / 板外取消
        else if (Tool == Tool.Marquee || MarqueeActive) ApplyMarquee();    // MarqueeActive：触摸长按那条路（工具可能还是笔）
        if (!wasSelDragging) _pendingTextEdit = false;
        EndStrokeMeasure();      // 兜底：没收过的分支（取消、切换工具等）也把总账结掉
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

}
