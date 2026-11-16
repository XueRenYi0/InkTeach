// [职责] 触摸手势：单指/双指/三指/长按/惯性。
// [测试] --touchtest

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
    // =====================================================================
    //  触摸手势（8.4.0）——规格与调研见 调研-触摸手势-学校大屏.md
    //
    //  分工：Touch.cs 只回答"这一下是什么"（触点表 + 干净开始的角色判定 + 长按计时）；
    //  这里按 _touchMode 路由动作：写字 / 擦 / 漫游 / 翻页 / 框选·点选 / 选中变换。
    // =====================================================================

    private readonly TouchGestures _touch = new();
    private TouchMode _touchMode = TouchMode.None;

    /// <summary>触摸选出来的选中：**临时**的（框和操作条要显示，但工具不换）——和 `_dwellSelected` 同一路。</summary>
    private bool _touchSelected;

    private Vector2 _g2StartMid, _g2LastMid;   // 双指：起点中点 / 上一帧中点
    private int _g2Axis;              // 0 未定 / 1 横（翻页）/ 2 纵（漫游）
    private uint _g2IdA, _g2IdB;      // 钉住的两根手指（2026-10-09：手掌/杂触点不参与；0 = 未钉）
    private float _g2ScrollSm;        // 双指漫游的滚动量平滑（压"两指事件交错"的半格锯齿）
    private bool _g2Turned;           // 这一次手势已经翻过页（一次手势只翻一页）
    private bool _g2Transform;
    private bool _g2TextBoxScroll;
    private Stroke _g2TextBoxScrollStroke;
    private float _g2TextBoxScrollStart = 0f, _g2TextBoxScrollStartY = 0f;  // 双指纵滑起点（当前路径未赋值，恒为 0；保留行为）
    private bool _g2Tap;              // 两指点按候选（松手时结算）
    private float _g2Dist0;           // 变换用：起始两指距离（双指只做缩放，绕选区中心）

    // ---- 文档捏合 / 放大后平移（2026-11-16）--------------------------------
    // 第二根手指落下后**先观察再锁**（判据见 `Doc/DocImport.Pan.cs::DocTouchGestureMove`）：
    // 距离变化超过 <see cref="TouchGestures.PinchRatio"/> → 锁"文档缩放"；距离不变又放大了 →
    // 锁"两轴平移"。一旦锁定，这一轮手势不再换分支（缩放 / 翻页 / 漫游 / 平移四选一）。
    // 这一小撮状态放在 <see cref="DocTouchState"/> 里（ADR-005 D3：新状态走组合对象，
    // 不再往 Engine 上摊私有字段）；字段本身在 `Doc/DocImport.Pan.cs` 声明。

    // ---- 双指纵滑的惯性（2026-10-28）--------------------------------------
    // 真机反馈：上下滑松手就停，不如浏览器"甩一下继续走"。
    // 速度单位 = 逻辑像素 / 毫秒；滚动仍走 ScrollCanvasBy，和滚轮/上下键同一条路。
    private float _g2FlingVy;         // 松手时的纵向速度（带符号；负 = 手指向上、画布向下滚）
    private double _g2LastMoveMs;     // 上一次双指移动的时刻（算瞬时速度）
    private bool _g2FlingActive;      // 惯性还在跑
    private double _g2FlingLastMs;    // 惯性上一拍的时刻
    private bool _g2UiScroll;         // 这一次双指纵滑由界面消费（设置页）
    private float _g2UiVy;            // 界面消费时的松手速度（界面逻辑像素/ms）
    private const float TouchFlingMax = TouchGestures.FlingMaxVy;          // 最大速度（逻辑像素/ms，约 1800px/s）
    private const float TouchFlingStop = TouchGestures.FlingStopVy;        // 低于它 = 停（约 20px/s）
    private const float TouchFlingDecayPerSec = TouchGestures.FlingDecayPerSec; // 指数衰减；越小惯性越大
    private const float TouchFlingMaxStepMs = TouchGestures.FlingMaxStepMs;     // 卡顿后别一跳半屏

    /// <summary>一根手指按下那一刻的选区快照。这一下可能在图形工具下被"点框外 = 收起选区"
    /// 清掉（见 <see cref="ClearSelectionForNewContext"/>）；但它若紧接着升级成双指手势，
    /// 那个"点框外"其实没发生——手势起手时把选区救回来
    ///（2026-10-05 真机反馈："选中以后，双指放在别处转圈怎么没反应"）。</summary>
    private readonly List<Stroke> _touchSelSnapshot = new();

    /// <summary>双指上下滑的滚动系数：换算成逻辑像素后走**和鼠标滚轮/上下键同一个**
    /// `ScrollCanvasBy`。2026-10-05 真机反馈"1:1 滑得不够" → 暂定 2 倍；
    /// 觉得快了/还慢，只调这一个数。</summary>
    private const float TouchScrollGain = 2f;
    private Vector2 _roamLast;        // 单指漫游：上一帧位置
    private uint _roamId;             // 单指漫游：认哪一根手指（其它触点的移动不许开船）
    /// <summary>长按起头的那根手指（框选 / 拖动选中）：**只有它**能画框 / 拖东西。
    /// 别的触点（掌根、幽灵触点、第二根手指）的移动一律不许劫持——真机上"拖着拖着
    /// 东西突然飞到掌根底下"就是这里漏判（2026-10-05，和 `_roamId` 同一条纪律）。</summary>
    private uint _selTouchId;
    private bool _radialTouchMode;    // 轮盘这次是触屏呼出的（松手改成触点驱动；没划动=留在盘上）
    private bool _radialSticky;       // 触屏轮盘：没划动松手后留在盘上等点选（点空白/Esc 取消）

    /// <summary>读触摸模式（启动时一次）。2026-10-24 恢复为「手势 / 单指书写」两档，
    /// 默认手势；`--notouch` 仍可作本次运行的开发对照。</summary>
    internal void LoadTouchPrefs()
    {
        // 2026-10-24：只保留“手势 / 单指书写”两档；多指同时书写以后再做。
        // 默认手势（没有 touch.mode = 手势）；单指档完全关闭触摸手势。
        _touch.Enabled = GetUiPref("touch.mode") != "single";
        _touch.SingleFingerRoam = false;
        _touch.LongPressSelect = true;
        _touch.TwoFingerTapSelect = true;    // 当前实现就是“两指轻点开轮盘”；不另做两指点选
        _touch.TwoFingerHold = true;

        // 面积橡皮取消：不再看面积触发擦除；只保留三指及以上擦。
        // 面积/基线仍由 TouchGestures 采样，TouchStats 会写日志。
        _touch.ThreeFingerErase = true;
        _touch.PalmFactor = GetUiPref("touch.palmLevel") switch
        {
            "0" => 4f,      // 仅统计用
            "2" => 2f,      // 仅统计用
            _ => 3f,        // 仅统计用
        };
    }

    /// <summary>这块屏"报不报多个触点 / 报不报面积"——只给设置页和日志用，不影响判定。</summary>
    internal (int maxTouches, bool sawArea) TouchCapability => (_touch.MaxSeen, _touch.SawArea);

    /// <summary>
    /// 触摸触点落下。返回 true = 这一下**已经被触摸层接掉**（调用方直接 return）；
    /// 返回 false = 按普通写字那条路走（单指小面积）。
    /// </summary>
    private bool TouchDownDispatch(IntPtr hWnd, uint id, float x, float y, float sizePx,
                                   int contactW, int contactH, out bool stealPointer)
    {
        stealPointer = false;
        var v = _touch.Down(id, x, y, sizePx, NowMs, DpiScale);

        // 面积只采样、不参与判定；明细见 TouchStats。
        TouchStats.NoteDown(_touch.Count, contactW, contactH, sizePx,
                            _touch.FingerBaseline, _touch.PalmFactor,
                            _touch.WouldPalm, v.ToString(), _touch.Enabled, Tool);

        if (v == TouchVerdict.Write)
        {
            // 单指这一下会走"普通写字那条路"（可能被它清掉选区）——先记快照，
            // 手势升级时若发现选区没了就靠它救回来（见 BeginTouchTwoFinger）。
            _touchSelSnapshot.Clear();
            if (Doc.Selected.Count > 0) _touchSelSnapshot.AddRange(Doc.Selected);
        }
        else if (v != TouchVerdict.Ignore)
        {
            // 手势要接管：第一根手指可能已经起了别的头（框选工具下那个还没拖的框，
            // 以及图形工具下"按框外顺手收起选区"）——只能撤，不能 apply。
            CancelTouchPressMarquee();
        }
        switch (v)
        {
            case TouchVerdict.Ignore:
                return true;                       // 吃掉：不抢正在写的那一笔、也不落墨

            case TouchVerdict.Erase:
                stealPointer = true;
                // 可能是"两指变换进行中，第三根手指落下来变成擦"：先把变换收账
                //（一点没动就不进撤销栈），不然 SelDragging 会一直挂着（松手后全变成拖动）。
                if (SelDragging) EndSelDrag();
                _g2Transform = false;
                CancelTouchStroke();
                _touchMode = TouchMode.Erase;
                _drawing = true;
                Native.SetCapture(hWnd);
                GcLatency.Enter();
                BeginStrokeMeasure();
                // **跟鼠标同一条路**（2026-10-05 用户口径）：批次按当前那把橡皮开
                //（整笔擦 = BeginErase、面积擦 = BeginEraseRect），下面的 TouchEraseSample 同源。
                // 先 EndErase 兜底：如果第一根手指是**橡皮工具**按下来的（那一路自己开了批次），
                // 这里先把它收账，免得批次被顶掉（没有批在开时 EndErase 是空操作）。
                Doc.EndErase();
                if (_eraserKind == Tool.PixelEraser)
                {
                    Doc.BeginEraseRect();
                    // **动态橡皮**：和鼠标按下同一套（Reset → 移动中放大）。
                    // 真机反馈"三指橡皮没有动态大小"就是这里没接上（PixelEraseDragging 一直是 false）。
                    ResetDynamicEraser();
                    PixelEraseDragging = true;
                    if (TouchEraseCenter(out var c0)) { _lastEraseX = c0.X; _lastEraseY = c0.Y; }
                }
                else Doc.BeginErase();
                // 面积橡皮已取消；走到这里只剩三指及以上擦（主动动作），按下即擦。
                TouchEraseSample();
                Console.WriteLine("触摸：三指及以上 → 擦除");
                return true;

            case TouchVerdict.Gesture2:
                stealPointer = true;
                // 从"擦"切到手势：先把这一轮擦除收账（不然 BeginEraseRect 一直挂着）。
                if (_touchMode == TouchMode.Erase) { Doc.EndErase(); EndStrokeMeasure(); PixelEraseDragging = false; }
                CancelTouchStroke();
                _touchMode = TouchMode.Gesture2;
                _drawing = true;
                Native.SetCapture(hWnd);
                // **两指手势也要起 40ms 心跳**：`TouchGestures.Tick` 里的"两指长按=呼出盘"
                // 要靠它（写字那颗定时器只在单指写字的路上启动；漏了这里=长按永远不判）。
                StartDwellTimer();
                BeginTouchTwoFinger();
                return true;

            case TouchVerdict.Roam:
                stealPointer = true;
                CancelTouchStroke();
                _touchMode = TouchMode.Roam;
                _drawing = true;
                Native.SetCapture(hWnd);
                _roamId = id;
                _roamLast = new Vector2(x, y);
                Console.WriteLine("触摸：单指漫游");
                return true;

            default:
                return false;                      // Write：走原来的写字那条路
        }
    }

    /// <summary>撤掉第一根手指在框选工具下起的那一个"还没拖的框"（手势接管时用；不能 apply）。
    /// 不撤的话：旋转/擦除期间框还画在屏幕上，手势结束后它也不走（"松手了框还在"）。</summary>
    private void CancelTouchPressMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;
        LassoPath.Clear();
        _dirty = true;
    }

    /// <summary>触摸触点移动（按当前模式路由）。返回 true = 已经处理完（调用方直接 return）。</summary>
    private bool TouchMoveDispatch(uint id, float x, float y)
    {
        if (_touchMode == TouchMode.None) return false;
        _touch.Move(id, x, y, DpiScale);

        switch (_touchMode)
        {
            case TouchMode.Write:
                return false;                      // 写字：走原来的路（长按由定时器判）

            case TouchMode.Erase:
                TouchEraseSample();
                return true;

            case TouchMode.Gesture2:
                TouchGestureMove();
                return true;

            case TouchMode.Marquee:
                if (id != _selTouchId) return true;   // 只有"长按起头那根手指"能画框（掌根/别的触点不算）
                ExtendMarqueeTo(x, y);
                _dirty = true;
                return true;

            case TouchMode.SelDrag:
                if (id != _selTouchId) return true;   // 同理：别的触点不许把选中的东西拽走
                UpdateSelDrag(x, y);
                return true;

            case TouchMode.Radial:
            {
                // 轮盘开着：方向 = **当前触点的平均位**（一根手指先抬也不断；确认在松手那一刻）。
                var views = _touch.Views;
                if (views.Count == 0) return true;
                float mx = 0, my = 0;
                foreach (var v in views) { mx += v.Pos.X; my += v.Pos.Y; }
                PointerX = mx / views.Count;
                PointerY = my / views.Count;
                UpdateRadialSelection();
                _dirty = true;
                return true;
            }

            case TouchMode.Roam:
            {
                // 只认**开始漫游的那根手指**：屏幕上报来的其它触点（残点 / 掌根 / 误触）
                // 的移动不许开船——合成注入里实测到"残留触点在 (1440,1200) 反复发移动，
                // 把相机拽得乱跳"；真机上同理（多报的触点不该影响漫游）。
                if (id != _roamId) return true;
                var p = new Vector2(x, y);
                float dy = p.Y - _roamLast.Y;
                _roamLast = p;
                ScrollCanvasBy(dy / DpiScale);      // 和滚轮/上下键同一个函数（1:1 逻辑像素）
                return true;
            }
        }
        return false;
    }

    /// <summary>触摸触点抬起（按当前模式收尾）。返回 true = 调用方直接 return（写字那条除外）。</summary>
    private bool TouchUpDispatch(uint id)
    {
        // 表里的触点**永远要清**（哪怕这一刻没有手势在跑）：上面"先喂表"那条路
        // 可能把一根手指留在表里，漏清会让下一轮的"干净开始"计数错乱。
        if (_touchMode == TouchMode.None) { _touch.Up(id); return false; }
        bool normalStroke = _touchMode == TouchMode.Write;

        // 两指点按要在"减掉这一根之前"判（判据要求两个触点都还在）
        // 捏合 / 平移分支已经锁了：那两指不是"点一下"，不许在松手时出盘。
        if (_touchMode == TouchMode.Gesture2 && !_docTouch.Pinch && !_docTouch.Pan
            && _touch.TwoFingerTap(NowMs, DpiScale, out _))
            _g2Tap = true;

        switch (_touchMode)
        {
            case TouchMode.Erase:
                // **最后一根"真在表里"的手指抬起才收账**：三指擦中途松 1~2 根手指，会话要继续擦
                //（2026-10-05 真机反馈："三指会变大，但剩一根手指接着擦时就不变了"——
                //  以前每抬一根就 EndErase + 关动态：尺寸冻在基准、批次也碎成一步一次撤销）。
                // `Contains` 用来挡"幽灵 up"（合成注入会补发已抬起触点的 up；真机丢 up 后同理）：
                // 只有当前还在表里的那次抬起才算数，收账因此**恰好一次**。
                if (_touch.Contains(id) && _touch.Count <= 1)
                {
                    Doc.EndErase();             // 一次擦除 = 一步撤销
                    EndStrokeMeasure();
                    PixelEraseDragging = false; // 面积擦回基准（动态大小别残留）
                }
                break;

            case TouchMode.Gesture2:
                TouchGestureReleased();
                break;

            case TouchMode.Radial:
                // 最后一根手指抬起 = 确认；没划动就松手 → "留在盘上等点选"（见 CommitRadialPalette）。
                if (_touch.Count <= 1) CommitRadialPalette();
                break;

            case TouchMode.Marquee:
                // 只有"起头那根手指"抬手才结算；别的触点抬手不结算（框还在画）
                if (id == _selTouchId)
                {
                    ApplyMarquee();             // 松手出选区
                    _touchSelected = Doc.Selected.Count > 0;
                }
                break;

            case TouchMode.SelDrag:
                if (id == _selTouchId)          // 同上：别的触点抬手不许把拖动提前提交掉
                {
                    EndSelDrag();               // 提交（一步撤销；一点没动就不进撤销栈）
                    _touchSelected = Doc.Selected.Count > 0;
                }
                break;
        }

        _touch.Up(id);

        if (_touch.Any) return !normalStroke;   // 还有手指按着：手势继续

        _touchMode = TouchMode.None;
        _touch.Reset();
        _touchSelSnapshot.Clear();
        _g2Transform = false;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _docTouch.Pinch = false;
        _docTouch.Pan = false;
        _docTouch.PinchHot = 0;
        _docTouch.PinchDist0 = 0f;                // 0 = 这一轮没记起点：捏合判据不成立（防旧值串到下一轮）
        _g2IdA = _g2IdB = 0;               // 手势收场：钉子放掉（下一轮重新钉）
        _g2ScrollSm = 0f;
        StopDwellTimer();
        _dirty = true;
        return !normalStroke;                   // 写字那条：清完触点后照常走 EndStroke
    }

    /// <summary>中断触摸手势（丢捕获 / 换设备 / 意外路径）：把开了头的都收干净。</summary>
    private void TouchAbort()
    {
        if (_touchMode == TouchMode.None && !_touch.Any) return;
        StopTouchFling();
        if (_touchMode == TouchMode.Erase) { Doc.EndErase(); EndStrokeMeasure(); PixelEraseDragging = false; }
        // 触屏呼出的轮盘：手势中断 = 盘也收掉（别留一个没人管的盘在屏幕上）。
        if (RadialPaletteActive && _radialTouchMode) CancelRadialPalette("手势中断");
        // ⚠ **选择拖动 / 框选不在这里结束**（2026-09-30 实测）：触摸长按之后，
        // 系统偶尔会在"按住不动"的某一刻发一次丢捕获，把拖动提前 `EndSelDrag()` 掉——
        // 症状就是"长按选中了对象，但拖不动"（笔画计数却涨了：那一拖被当成接着写字）。
        // 留给松手那条路（`EndStroke` 里的 `if (SelDragging) EndSelDrag()`）去收：
        // 它才是真的抬手时刻，而且对鼠标那条路本来就是同一条。
        _touch.Reset();
        _touchSelSnapshot.Clear();
        _touchMode = TouchMode.None;
        _g2Transform = false;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _docTouch.Pinch = false;
        _docTouch.Pan = false;
        _docTouch.PinchHot = 0;
        _docTouch.PinchDist0 = 0f;                // 中断：捏合起点一并作废
        _g2IdA = _g2IdB = 0;               // 手势中断：钉子一并放掉
        _g2ScrollSm = 0f;
        Console.WriteLine("触摸手势中断（丢捕获/换设备）");
        _dirty = true;
    }

    /// <summary>长按成立（40ms 心跳驱动）：撤掉那一小笔，进"点选 / 框选"。</summary>
    private void TouchLongPressFire()
    {
        if (_touchMode != TouchMode.Write) return;
        var c = _touch.Views.Count > 0 ? _touch.Views[0] : default;
        float x = c.Pos.X, y = c.Pos.Y;
        _selTouchId = c.Id;              // 这一根是"框选 / 拖动"的起头手指（见 _selTouchId）

        CancelTouchStroke();

        // 先问"手指下面有没有东西"（点选：选中并直接进入拖动）；
        // 没东西 → 框选（松手出选区）。
        if (TryBeginSelectionGesture(x, y, shift: false, alt: false))
        {
            _touchSelected = Doc.Selected.Count > 0;
            _touchMode = TouchMode.SelDrag;
        }
        else
        {
            BeginMarqueeAt(x, y, shift: false, alt: false);
            _touchMode = TouchMode.Marquee;
        }
        _touch.ClearLongPress();
        _dirty = true;
    }

    /// <summary>撤掉"刚起头的那一小笔"（不进文档、不留撤销）。</summary>
    private void CancelTouchStroke()
    {
        if (ActiveStroke != null)
        {
            Doc.Dirty.Add(ActiveStroke.PaddedBounds);
            ActiveStroke = null;
        }
        // [随老预测系统删除 2026-10-05] 这里原来是 `ClearRenderTail(); ActiveStrokeOnTrail = false;`
        _dwell.Reset();
        _dwellInk = null;
        _dismissTapArmed = false;
        StopDwellTimer();
        _dirty = true;
    }

    /// <summary>三指 / 手掌擦会话正在跑（Overlay 的落点反馈与脏区都读它）。</summary>
    internal bool TouchEraseSessionActive => _touchMode == TouchMode.Erase;

    /// <summary>这次擦除会话用的是不是面积擦（跟随用户当前那把橡皮，和鼠标同源）。</summary>
    internal bool TouchEraseSessionPixel => _eraserKind == Tool.PixelEraser;

    /// <summary>擦除会话里的活触点（画落点反馈用；每次现取，不缓存——避免"出现不消失"）。</summary>
    internal IReadOnlyList<TouchGestures.ContactView> TouchViewsLive => _touch.Views;

    /// <summary>擦除手势的落点：**所有触点的中心**（没有触点返回 false）。擦除、落点反馈、
    /// 脏区三处共用这一个点，保证"看见的 = 擦掉的"永远是同一个位置。</summary>
    internal bool TouchEraseCenter(out Vector2 c)
    {
        var list = _touch.Views;
        if (list.Count == 0) { c = default; return false; }
        float sx = 0f, sy = 0f;
        for (int i = 0; i < list.Count; i++) { sx += list[i].Pos.X; sy += list[i].Pos.Y; }
        c = new Vector2(sx / list.Count, sy / list.Count);
        return true;
    }

    /// <summary>擦一下：**整个手势只用一个"鼠标橡皮"**，落点 = 所有触点的中心
    ///（2026-10-05 真机反馈："三个指头出来三个橡皮擦"——一只手应该是一个橡皮，和鼠标同源）。
    /// 整笔擦 = `EraseAt(中心, EraserRadius)`（碰到就整条删）；
    /// 面积擦 = `EraseRectAt(中心, 鼠标落点框半宽/半高)`——尺寸和鼠标按下那一份完全一致，
    /// 而且**动态橡皮照旧**：喂速度 → 快扫变大（和鼠标 `EraseRectAlongPath` 同一套）。</summary>
    private void TouchEraseSample()
    {
        if (!TouchEraseCenter(out var c)) return;
        if (_eraserKind == Tool.PixelEraser)
        {
            UpdateDynamicEraser(c.X, c.Y);       // 动态橡皮：开关关掉时它自己把系数归 1
            _lastEraseX = c.X; _lastEraseY = c.Y;
            Doc.EraseRectAt(c.X, c.Y, PixelEraserCursorHalfWidthPx, PixelEraserCursorHalfHeightPx);
        }
        else
        {
            _lastEraseX = c.X; _lastEraseY = c.Y;
            Doc.EraseAt(c.X, c.Y, EraserRadius);
        }
        _dirty = true;
    }

    /// <summary>双指开始：有选中 → 借"整体拖动"那套（detach + 预览矩阵 + 松手一步撤销）；没选中 → 等方向。</summary>
    private void BeginTouchTwoFinger()
    {
        if (!_touch.TryPair(out var p)) return;

        // ⚠ 2026-10-09（真机"双指上下滑嘟嘟抖"的根修）：**把这两根手指的 id 钉住**，
        // 这一轮手势全程只认它们（TouchGestureMove 用 id 版 TryPair）。
        // 之前的读法"表里前两个"会被两类事搅乱——① 掌根/杂触点混进表；
        // ② 手指抬-落一下被重报、追加到表尾——"前两个"换人，中点来回跳。
        // 重入保护：钉子还活着就不重置（手指 blip 重落会再走到这里，不能把
        // _g2StartMid / 轴锁 重新洗一遍）。
        if (_g2IdA != 0 && _touch.Contains(_g2IdA) && _touch.Contains(_g2IdB)) return;
        StopTouchFling();
        _g2IdA = p.IdA;
        _g2IdB = p.IdB;
        _g2ScrollSm = 0f;
        _g2FlingVy = 0f;
        _g2UiScroll = false;
        _g2UiVy = 0f;
        _g2LastMoveMs = NowMs;

        _g2StartMid = _g2LastMid = (p.A + p.B) * 0.5f;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _g2Transform = false;
        _docTouch.Pinch = false;
        _docTouch.Pan = false;
        _docTouch.PinchHot = 0;

        // 第一根手指按下时可能把选区清掉了（图形工具"点框外 = 收起"，见 ClearSelectionForNewContext）
        // ——这一下紧接着升级成双指手势，说明那个"点框外"根本没发生。把选区救回来，
        // 手势照常变换（2026-10-05 真机反馈："选中以后双指放别处转圈怎么没反应"）。
        if (Doc.Selected.Count == 0 && _touchSelSnapshot.Count > 0)
        {
            foreach (var st in _touchSelSnapshot)
                if (Doc.Strokes.Contains(st)) Doc.Selected.Add(st);
            if (Doc.Selected.Count > 0)
            {
                _touchSelected = true;
                _dirty = true;
            }
        }
        _touchSelSnapshot.Clear();

        if (Doc.Selected.Count == 0)
        {
            // 捏合判据的起点：两指刚落下的距离 / 当时的相机倍率（选中时不记，走对象缩放）。
            _docTouch.PinchDist0 = MathF.Max(1f, Vector2.Distance(p.A, p.B));
            _docTouch.PinchZoom0 = ViewScale;
            return;
        }

        // 有选中：在**选区中心**起一次"整体拖动"。借现成那套的好处：
        // 预览走 _selDragMatrix、松手 EndSelDrag 提交（一步撤销）、锁定对象自动摘掉。
        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        if (TryBeginSelectionGesture((aabb.MinX + aabb.MaxX) * 0.5f,
                                     (aabb.MinY + aabb.MaxY) * 0.5f, shift: false, alt: false))
        {
            _g2Transform = true;
            _g2Dist0 = MathF.Max(1f, Vector2.Distance(p.A, p.B));
            // 旋转读数归位：旋转已经只走"拖旋转手柄"那条路，双指不再产生度数。
            SelRotationSnapped = false;
            SelRotationReadsPose = false;
            SelRotationReadsInclination = false;
            _touchSelected = true;
            Console.WriteLine("触摸：双指 → 缩放选中对象（旋转用旋转手柄、移动用单指拖）");
        }
    }

    /// <summary>双指移动：有选中 → 变换对象；没选中 → 纵滑漫游 / 横滑翻页（方向锁 + 一次一页）。</summary>
    private void TouchGestureMove()
    {
        // **只认钉住的那两根**（2026-10-09）：手掌、杂触点在表里也当没看见。
        if (!_touch.TryPair(_g2IdA, _g2IdB, out var p)) return;
        var mid = (p.A + p.B) * 0.5f;

        if (_g2TextBoxScroll)
        {
            var ts = _g2TextBoxScrollStroke;
            var tc = ts?.TextBox;
            if (tc == null) return;
            float target = Math.Clamp(_g2TextBoxScrollStart - (mid.Y - _g2TextBoxScrollStartY),
                                      0f, tc.MaxScroll);
            if (MathF.Abs(target - tc.ScrollY) > 0.1f)
            {
                tc.ScrollY = target;
                Doc.Dirty.Add(ts.PaddedBounds);
                Doc.StructureChangedSinceRender = true;
                Doc.Version++;
                _dirty = true;
            }
            return;
        }

        if (_g2Transform)
        {
            // 绕**选区中心**缩放（不带平移、不带旋转）。
            // 2026-10-05 用户口径（第二轮定稿）：双指**只管缩放**；移动走单指拖、
            // 旋转走**拖旋转手柄**——三条路各管一件事，互相不打架（和鼠标那边一一对应）。
            var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var center = new Vector2((aabb.MinX + aabb.MaxX) * 0.5f, (aabb.MinY + aabb.MaxY) * 0.5f);
            float d1 = Vector2.Distance(p.A, p.B);
            float scale = Math.Clamp(d1 / _g2Dist0, 0.1f, 10f);

            _selDragMatrix = Matrix3x2.CreateTranslation(-center.X, -center.Y)
                           * Matrix3x2.CreateScale(scale)
                           * Matrix3x2.CreateTranslation(center);
            _selDragMoved = true;
            _dirty = true;
            return;
        }
        float dx = mid.X - _g2LastMid.X, dy = mid.Y - _g2LastMid.Y;
        _g2LastMid = mid;

        // 文档：捏合缩放 / 放大后两轴平移（先观察再锁；锁住后这一轮不再换分支）。
        if (DocTouchGestureMove(p, mid, dx, dy)) return;

        if (_g2Axis == 0)
        {
            var total = mid - _g2StartMid;
            float lockPx = TouchGestures.DirLockLogical * DpiScale;
            if (total.Length() < lockPx) return;
            if (MathF.Abs(total.X) >= MathF.Abs(total.Y) * TouchGestures.DirRatio) _g2Axis = 1;
            else if (MathF.Abs(total.Y) >= MathF.Abs(total.X) * TouchGestures.DirRatio) _g2Axis = 2;
            else return;                       // 斜着：先不动，等主方向明确
        }

        if (_g2Axis == 2)
        {
            // 上下 = 漫游：走**鼠标滚轮/上下键同一个** ScrollCanvasBy（不再自己写相机数学）。
            // 系数见 TouchScrollGain（真机反馈 1:1 不够，暂定 2 倍）。
            // **2026-10-09 二轮双保险**（用户复测仍报"抖得厉害"）：
            //   ① 两指的事件交错到达时，中点会呈"半格锯齿"——轻 EMA 抹平；
            //   ② 单事件异常大跳（丢帧重放 / 掌缘蹭入）限幅，别让画面猛跳。
            _g2ScrollSm += (dy - _g2ScrollSm) * 0.55f;
            float lim = 90f * DpiScale;
            float use = Math.Clamp(_g2ScrollSm, -lim, lim);
            // 记瞬时速度，松手后 StepTouchFling 接着跑（2026-10-28）。
            // 单位统一成**逻辑像素 / ms**；dt 太小/太大都夹一下，防异常事件把速度打飞。
            double dtMs = Math.Clamp(NowMs - _g2LastMoveMs, 1.0, 40.0);
            _g2LastMoveMs = NowMs;

            // **设置页这类整页界面先接**（2026-11-07）：接住后不再滚画布，
            // 惯性也由界面在 PrepareFrame 里用同一套 UiTouchFling 参数走。
            if (!_g2UiScroll && UiVisibleNow && Ui != null)
            {
                var begin = new UiTouchScrollEvent(UiTouchScrollPhase.Begin,
                    mid.X / UiUnit, mid.Y / UiUnit, 0f, 0f, NowMs);
                if (Ui.TouchScroll(begin)) _g2UiScroll = true;
            }
            if (_g2UiScroll)
            {
                float uiDy = use * TouchScrollGain / UiUnit;
                float uiVy = uiDy / (float)dtMs;
                _g2UiVy = Math.Clamp(_g2UiVy * 0.70f + uiVy * 0.30f,
                                     -TouchFlingMax, TouchFlingMax);
                Ui?.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Move,
                    mid.X / UiUnit, mid.Y / UiUnit, uiDy, _g2UiVy, NowMs));
                return;
            }

            float instVy = (use * TouchScrollGain / DpiScale) / (float)dtMs;
            _g2FlingVy = Math.Clamp(_g2FlingVy * 0.70f + instVy * 0.30f,
                                    -TouchFlingMax, TouchFlingMax);
            ScrollCanvasBy(use * TouchScrollGain / DpiScale);
            return;
        }

        _g2FlingVy = 0f;                       // 横滑翻页不带纵向惯性
        if (_g2Turned || dx == 0f) return;
        float totalX = mid.X - _g2StartMid.X;
        if (MathF.Abs(totalX) < TouchGestures.PageTurnLogical * DpiScale) return;
        _g2Turned = true;                      // 一次手势只翻一页（抬手再滑才是下一页）
        bool next = totalX < 0;                // 往左滑 = 下一页
        if (BoardOn) FlipPage(next);           // 白板自己的页，优先于 PPT 翻页
        else if (PptMode)
        {
            if (next) PptNextFromUi(); else PptPrevFromUi();
        }
        else FlipPage(next);
        Console.WriteLine($"触摸：双指横滑 → {(next ? "下一页" : "上一页")}");
    }

    /// <summary>双指手势收尾（最后一根指头抬起时）。</summary>
    private void TouchGestureReleased()
    {
        _g2TextBoxScroll = false;
        _g2TextBoxScrollStroke = null;
        // **两指轻点优先于"变换提交"**：点按没有位移，不该走变换那条路。
        // （2026-10-05 实测：画布上有选中对象时，轻点会被 `_g2Transform` 分支吃掉——
        //   老顺序把 transform 放在前面，轻点永远轮不到。）
        if (_g2Tap && !_docTouch.Pinch && !_docTouch.Pan && !_g2Turned && _g2Axis == 0 && _touch.Views.Count >= 2)
        {
            if (SelDragging) EndSelDrag();     // 万一 transform 那条已经起了拖动（零位移=不进撤销栈）
            _g2Transform = false;
            var mid = (_touch.Views[0].Pos + _touch.Views[1].Pos) * 0.5f;
            OpenRadialPaletteFromTouch(mid.X, mid.Y, sticky: true);
            _g2Tap = false;
            return;
        }
        if (_g2Transform)
        {
            if (SelDragging) EndSelDrag();     // 提交变换（一步撤销；没动就不进撤销栈）
            _g2Transform = false;
            return;
        }
        // 界面消费的双指纵滑：把松手速度交给界面，界面用同一套 UiTouchFling 继续跑。
        // 不能在这里再开画布惯性——否则设置页松手后画布会在底下偷偷滚。
        if (_g2UiScroll)
        {
            Ui?.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.End,
                _g2LastMid.X / UiUnit, _g2LastMid.Y / UiUnit, 0f, _g2UiVy, NowMs));
            _g2UiScroll = false;
            _g2UiVy = 0f;
            _g2Tap = false;
            return;
        }
        // 最后一根手指抬起：纵向滑动如果还有速度，交给惯性继续跑。
        // 只认最后一根（第一根先抬时 _touch.Count 还是 2，不能提前开船）。
        if (_g2Axis == 2 && _touch.Count <= 1
            && NowMs - _g2LastMoveMs <= 150
            && MathF.Abs(_g2FlingVy) >= TouchFlingStop)
        {
            _g2FlingActive = true;
            _g2FlingLastMs = NowMs;
            _dirty = true;
        }
        _g2Tap = false;
    }

    /// <summary>
    /// 双指纵滑松手后的惯性：每帧按指数衰减，滚动仍走 <see cref="ScrollCanvasBy"/>。
    /// 到边界（顶上/底下）或速度低于 <see cref="TouchFlingStop"/> 就停。
    /// </summary>
    private void StepTouchFling()
    {
        if (!_g2FlingActive) return;

        double dtMs = Math.Clamp(NowMs - _g2FlingLastMs, 0.0, TouchFlingMaxStepMs);
        _g2FlingLastMs = NowMs;
        if (dtMs <= 0.0) return;

        float decay = MathF.Exp(-TouchFlingDecayPerSec * (float)(dtMs / 1000.0));
        _g2FlingVy *= decay;
        if (MathF.Abs(_g2FlingVy) < TouchFlingStop)
        {
            StopTouchFling();
            return;
        }

        float before = ViewOffsetY;
        ScrollCanvasBy(_g2FlingVy * (float)dtMs);
        if (MathF.Abs(ViewOffsetY - before) < 0.01f)
            StopTouchFling();                  // 已经到边：别让速度空转
    }

    private void StopTouchFling()
    {
        // 界面消费的惯性也要刹住：松手后 `_g2UiScroll` 已经清零，不能只看它。
        Ui?.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Cancel, 0f, 0f, 0f, 0f, NowMs));
        _g2UiScroll = false;
        _g2UiVy = 0f;
        _g2FlingActive = false;
        _g2FlingVy = 0f;
    }

    /// <summary>
    /// 40ms 心跳（笔画进行中那颗定时器每次都调）：长按成立就把这一下升级成"选择"。
    /// 判据全在 <see cref="TouchGestures.Tick"/>（路径 ≤8 逻辑像素 = 和"点一下不留墨"同一个数）。
    /// </summary>
    private void TouchTick()
    {
        _touch.Tick(NowMs, DpiScale);
        if (_touch.LongPressFired && _touchMode == TouchMode.Write) TouchLongPressFire();

        // 捏合 / 平移分支已经锁住：两指长按在这条分支里没有意义（不许半路冒出盘）。
        if ((_docTouch.Pinch || _docTouch.Pan) && _touchMode == TouchMode.Gesture2) _touch.ClearLongPress();

        // 两指长按成立 → 呼出盘（触屏入口）。进 Radial 模式后：方向由触点平均位喂（TouchMoveDispatch），
        // 松手在 TouchUpDispatch 里确认；没划动就松手 = 留在盘上等点选（见 CommitRadialPalette）。
        if (_touch.TwoFingerHoldFired && _touchMode == TouchMode.Gesture2)
        {
            _touch.ClearLongPress();
            // 呼出盘的位置用**钉住的那两根**（2026-10-09）；钉子不在时退回"表里前两个"。
            TouchGestures.PairView pair = default;
            bool pairOk = _g2IdA != 0 ? _touch.TryPair(_g2IdA, _g2IdB, out pair)
                                      : _touch.TryPair(out pair);
            if (pairOk)
            {
                // 有选中时这两指可能已经把变换起头了（SelDragging）——呼出盘之前先收账
                //（一点没动就不进撤销栈），不然盘一开这个拖动会一直挂着。
                if (SelDragging) EndSelDrag();
                _g2Transform = false;
                _touchMode = TouchMode.Radial;
                OpenRadialPaletteFromTouch((pair.A.X + pair.B.X) * 0.5f,
                                           (pair.A.Y + pair.B.Y) * 0.5f, sticky: false);
            }
        }
    }

    /// <summary>自检用：触摸层这一刻的模式 / 触点表。</summary>
    internal TouchMode TouchModeForTest => _touchMode;
    internal int TouchCountForTest => _touch.Count;
    internal bool TouchSelectedForTest => _touchSelected;
    internal int _touchDebugAxis => _g2Axis;
    /// <summary>自检用：这一轮双指手势锁定的是"文档捏合"还是"放大后平移"（没锁 = 都 false）。</summary>
    internal bool TouchDocPinchForTest => _docTouch.Pinch;
    internal bool TouchDocPanForTest => _docTouch.Pan;
    internal bool TouchFlingActiveForTest => _g2FlingActive;
    internal float TouchFlingVelocityForTest => _g2FlingVy;
    internal void StopTouchFlingForTest() => StopTouchFling();
    /// <summary>自检用：直接喂一个触点取判定（合成触摸的 `rcContact` 系统不认，面积那条只能这么测）。</summary>
    internal TouchVerdict TouchClassifyForTest(uint id, float x, float y, float sizePx)
        => _touch.Down(id, x, y, sizePx, NowMs, DpiScale);
    internal void TouchResetForTest() { _touch.Reset(); _touchMode = TouchMode.None; _touchSelected = false; }

}
