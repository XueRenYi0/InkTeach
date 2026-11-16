// [职责] 文档「手掌」拖页 + 双指捏合缩放 / 放大后平移（文档相机）。
// [测试] --doctest / --touchtest

using System.Numerics;

namespace InkEngine;

public partial class InkEngine
{
    // ======================================================================
    //  文档「手掌」平移（2026-11-15：PDF 模式像放映）
    //
    //  用户定：文档模式里**没有用户穿透**，主条那格从"箭头（穿透）"变成"手（拖页）"；
    //  打开文档默认就是笔，退出文档回到桌面、那格恢复成普通穿透箭头。
    //  参考：浏览器 / Acrobat / PPT 阅读视图的"抓手"——按下拖动整页上下走，不落墨。
    //  本轮只做**纵向**（相机只有一个 ViewOffsetY）；缩放不在这一批。
    // ======================================================================

    /// <summary>
    /// 文档「手掌」开着吗（主条箭头那一格在文档模式里就是它）。
    /// 开着时画布按下 = 拖页：不落墨、不选中、不给文本工具。
    /// 只由 <see cref="ToggleDocPanFromUi"/> 打开；**换工具 / 关文档 / 进穿透都会关掉**。
    /// </summary>
    internal bool DocPanMode;

    /// <summary>正在拖（按下到抬手之间）。只认屏幕坐标的位移差。</summary>
    private bool _docPanDragging;

    /// <summary>上一拍的屏幕坐标（只用来算位移，不参与别处）。</summary>
    private float _docPanLastScreenX;
    private float _docPanLastScreenY;

    /// <summary>上一拍界面看到的页码；变了才通知界面（免得每帧重建一次状态快照）。</summary>
    private int _docPanLastPage;

    /// <summary>
    /// 主条那一格（文档模式下 = 手掌）：开 / 关拖页。
    /// 没有文档时是空操作——那种情况下那一格是普通穿透格（界面按 DocOpen 分派）。
    /// </summary>
    internal void ToggleDocPanFromUi()
    {
        if (!DocView.IsOpen) return;
        SetDocPan(!DocPanMode);
    }

    /// <summary>手掌真正的开关（那一格 / `H` / 按住空格都走这里：状态与提示只有一份）。</summary>
    private void SetDocPan(bool on)
    {
        if (DocPanMode == on) return;
        DocPanMode = on;
        if (!on) EndDocPan();
        SetInkStatus(on ? "手掌：拖动画布上下看页（再点一次回到工具）" : "手掌已关：回到工具");
        Console.WriteLine($"[文档] 手掌{(on ? "开" : "关")}");
        _dirty = true;
        ApplyCursor();
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 把"手掌"收掉（换工具 / 关文档 / 进穿透 / 打开新文档都走这里）。
    /// 收成一处判据：这几个入口各写一遍的话，迟早漏一处，表现就是"手里是笔、一动却是拖页"。
    /// </summary>
    private void ExitDocPan(string why)
    {
        // 按住空格开的那一份也跟着作废：否则松手时还去关一个已经换人的手掌。
        _docTouch.PanBySpace = false;
        if (!DocPanMode && !_docPanDragging) return;
        DocPanMode = false;
        EndDocPan();
        _dirty = true;
        ApplyCursor();
        Console.WriteLine($"[文档] 手掌关闭（{why}）");
    }

    /// <summary>
    /// 按下：进入拖页。<paramref name="screenX"/>/<paramref name="screenY"/> 是**屏幕**坐标
    /// （平移只认屏幕位移，换成画布坐标会被相机自己抵消掉）。
    /// </summary>
    private void BeginDocPan(float screenX, float screenY)
    {
        // 手掌开始 = 用户要自己拖着看：恢复提示先收（不跳）。
        DismissDocResume("手掌");
        // 翻页动画还在跑就先停在当前帧：否则"手里在拖、相机自己在飞"，两边打架。
        _camAnimating = false;
        _docPanDragging = true;
        _docPanLastScreenX = screenX;
        _docPanLastScreenY = screenY;
        _docPanLastPage = BarPageNow;
    }

    /// <summary>
    /// 拖动中：按位移改相机（和滚轮同一套符号），夹进合法范围。
    /// **放大到 >100% 时横向也跟着拖**（页比视口宽，左右要能看全）；≤100% 保持只走纵向
    /// （阶段 1 的硬保证：100% 横向相机严格为 0）。
    /// </summary>
    private void UpdateDocPan(float screenX, float screenY)
    {
        if (!_docPanDragging) return;
        float dx = screenX - _docPanLastScreenX;
        float dy = screenY - _docPanLastScreenY;
        _docPanLastScreenX = screenX;
        _docPanLastScreenY = screenY;
        if (MathF.Abs(dx) < 0.01f && MathF.Abs(dy) < 0.01f) return;

        // 手指/笔往下拖 = 内容跟着往下走 = 相机偏移变大（screen = canvas + ViewOffsetY）。
        if (MathF.Abs(dy) >= 0.01f) ViewOffsetY = ClampOffset(ViewOffsetY + dy);
        if (ViewScale > 1.0005f && MathF.Abs(dx) >= 0.01f)
            ViewOffsetX = ClampOffsetX(ViewOffsetX + dx);
        _dirty = true;
        NoteDocResumeChange();

        // 页码条跟着换页（页号变了才重建界面状态）。
        int page = BarPageNow;
        if (page != _docPanLastPage)
        {
            _docPanLastPage = page;
            NotifyUiStateChanged();
        }
        ApplyCursor();
    }

    /// <summary>抬手：结束拖动（UI 通知一次，让页码/按钮回到静止态）。</summary>
    private void EndDocPan()
    {
        if (!_docPanDragging) return;
        _docPanDragging = false;
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 画布按下这一刻归手掌吗：文档开着 + 手掌开 + 不在穿透。
    /// **真实输入和自检问同一条判据**（自检直接调它，免得"测的是另一条路"）。
    /// 归 = 进入拖动并返回 true（调用方负责 SetCapture / 光标 / 脏区）。
    /// </summary>
    internal bool TryBeginDocPanPress(float screenY) => TryBeginDocPanPress(float.NaN, screenY);

    internal bool TryBeginDocPanPress(float screenX, float screenY)
    {
        if (!DocPanMode || !DocView.IsOpen || PassThrough) return false;
        // 只给 Y 的老入口（自检 / 旧调用）：横向起点用"当前值"，那一拍只有纵向位移。
        BeginDocPan(float.IsNaN(screenX) ? _docPanLastScreenX : screenX, screenY);
        return true;
    }

    // ---- 真实指针接进来的入口（引擎只看"这一串消息归不归手掌"）----------------

    /// <summary>
    /// 拖动中的移动：还在拖、而且就是按下的那根指针 → 平移并返回 true。
    /// 返回 false = 不归手掌（调用方继续走原来的悬停/工具分支）。
    /// </summary>
    internal bool DocPanMoveFromPointer(uint id, float screenX, float screenY)
    {
        if (!_docPanDragging || id != _activePointer) return false;
        UpdateDocPan(screenX, screenY);
        return true;
    }

    /// <summary>抬手：结束拖动并返回 true（调用方负责 ReleaseCapture / 光标 / 标脏）。</summary>
    internal bool DocPanUpFromPointer(uint id)
    {
        if (!_docPanDragging || id != _activePointer) return false;
        EndDocPan();
        return true;
    }

    // ---- 自检钩子（--doctest / --zoomtest）：拖动 / 抬手直接喂给同一套方法 ----------------

    /// <summary>只给 Y 的老入口（阶段 2 及以前的自检）：X 不产生位移。</summary>
    internal void DocPanDragForTest(float screenY)
        => UpdateDocPan(_docPanLastScreenX, screenY);

    internal void DocPanDragForTest(float screenX, float screenY)
        => UpdateDocPan(screenX, screenY);

    internal void DocPanReleaseForTest() => EndDocPan();
    internal bool DocPanDraggingForTest => _docPanDragging;

    // ======================================================================
    //  双指：文档捏合缩放 / 放大后平移（2026-11-16）
    //
    //  参考：Acrobat / Edge 阅读器 + iOS/Android 的 ScaleGestureDetector——
    //  "两指距离变化 → 以两指中点为锚缩放；距离不变 → 按主方向平移/翻页"。
    //  判据和状态机在 `Engine.TouchGestures.cs`（一个手势只有一个状态机），
    //  这里只放**文档相机那一步**（和手掌共用夹取），免得手势文件里再抄一套相机数学。
    // ======================================================================

    /// <summary>
    /// 文档双指手势 + 「按住空格临时手掌」的那一小撮状态。
    /// ADR-005 D3：**新状态走组合对象**，不再往 `Engine` 上摊私有字段
    /// （手势路由仍然只有一个：`Input/Engine.TouchGestures.cs` 读它，判据在下面那一段）。
    /// </summary>
    internal sealed class DocTouchState
    {
        /// <summary>这一轮双指手势锁成了"文档缩放"。</summary>
        public bool Pinch;
        /// <summary>这一轮双指手势锁成了"文档平移"（放大后两轴）。</summary>
        public bool Pan;
        /// <summary>锁定时两指距离（从这一刻起连续缩放，首帧不跳；0 = 这一轮没记起点）。</summary>
        public float PinchDist0;
        /// <summary>锁定时的相机倍率。</summary>
        public float PinchZoom0 = 1f;
        /// <summary>距离变化连续超过门槛的拍数（防"两个触点事件先后到达"的假捏合）。</summary>
        public int PinchHot;
        /// <summary>这一次按住空格开的手掌（松手才收；原本就是手掌的不动它）。</summary>
        public bool PanBySpace;
    }

    private readonly DocTouchState _docTouch = new();

    /// <summary>
    /// 双指捏合 / 放大后平移能不能接管文档：文档开着、没选中对象、不在文本编辑、
    /// 界面上没压着全屏层（设置页这类整页界面先接自己的滚动）。
    /// </summary>
    internal bool DocPinchPanEligible =>
        DocView.IsOpen && !PptMode && !BoardOn && !UiOverlayOpen
        && Doc.Selected.Count == 0 && !_textBoxEditing;

    /// <summary>
    /// 双指捏合：按两指距离比例缩放，**锚点 = 当前两指中点**（屏幕坐标）。
    /// 走 <see cref="SetDocZoomCore"/>，所以 50–300% 夹取、补丁重建、页码/百分比通知全都跟着走。
    /// </summary>
    internal void DocPinchZoomFromTouch(float startZoom, float startDist, float dist, float midX, float midY)
    {
        if (!DocPinchPanEligible) return;
        float ratio = startDist > 1f ? dist / startDist : 1f;
        SetDocZoomCore(startZoom * ratio, DocZoomMin, midX, midY, quiet: true);
    }

    /// <summary>放大后两指拖动：**两轴**平移画布（夹取和手掌同一套；不再只走纵向滚）。</summary>
    internal void DocPanByTouch(float dx, float dy)
    {
        if (!DocPinchPanEligible) return;
        if (MathF.Abs(dx) < 0.01f && MathF.Abs(dy) < 0.01f) return;
        ViewOffsetX = ClampOffsetX(ViewOffsetX + dx);
        ViewOffsetY = ClampOffset(ViewOffsetY + dy);
        _dirty = true;
        NoteDocResumeChange();
    }

    /// <summary>
    /// 双指手势里的**文档分支**：捏合缩放 / 放大后两轴平移（2026-11-16）。
    ///
    /// 判据（参考 Acrobat / Edge + ScaleGestureDetector 的"先观察再锁"）：
    ///   ① 两指距离变化 &gt; <see cref="TouchGestures.PinchRatio"/>（且至少 10 逻辑像素）→
    ///      锁**捏合缩放**（以两指中点为锚；距离没变的横向滑动不会被认成缩放）；
    ///   ② 距离没变、放大到 &gt;100% 且位移过了方向锁 → 锁**两轴平移**（不再翻页）；
    ///   ③ 距离没变、100% 下 → 交给原来的方向锁（横 = 翻页、纵 = 漫游）。
    /// 返回 true = 这一拍已经被文档分支消费（不再往下走翻页 / 漫游）。
    ///
    /// ⚠ 这一轮手势的**状态**（<see cref="DocTouchState"/>）声明在本文件上面；手势文件的
    /// 路由只读它 —— 那个文件已经贴着 800 行上限（ADR-002），再塞进来就要拆文件了。
    /// </summary>
    private bool DocTouchGestureMove(in TouchGestures.PairView p, in Vector2 mid, float dx, float dy)
    {
        if (!DocPinchPanEligible) return false;
        float d1 = Vector2.Distance(p.A, p.B);

        if (!_docTouch.Pinch && !_docTouch.Pan && _g2Axis == 0)
        {
            // 先看**距离**：变化超过门槛（12% 且至少 10 逻辑像素）→ 认捏合。
            // 门槛要**连续两拍**都过（两个触点的事件先后到达会把距离骗大一行，下一拍又回去）；
            // 变化特别大（> 2 倍门槛）则第一拍就锁，快捏合不拖泥带水。
            float need = MathF.Max(TouchGestures.PinchRatio * _docTouch.PinchDist0,
                                   TouchGestures.PinchMinLogical * DpiScale);
            float change = _docTouch.PinchDist0 > 1f ? MathF.Abs(d1 - _docTouch.PinchDist0) : 0f;
            _docTouch.PinchHot = change > need ? _docTouch.PinchHot + 1 : 0;
            if (_docTouch.PinchDist0 > 1f && (_docTouch.PinchHot >= 2 || change > need * 2f))
            {
                _docTouch.Pinch = true;                    // 从锁定这一刻起连续缩放（首帧不跳）
                Console.WriteLine($"触摸：双指捏合 → 文档缩放（{DocZoomPercentText} 起，d={d1:F0}）");
                _docTouch.PinchDist0 = MathF.Max(1f, d1);
                _docTouch.PinchZoom0 = ViewScale;
            }
            else if (ViewScale > 1.0005f
                     && (mid - _g2StartMid).Length() >= TouchGestures.DirLockLogical * DpiScale)
            {
                _docTouch.Pan = true;                      // 放大后两指拖动 = 平移画布（两轴），不翻页
                Console.WriteLine("触摸：放大后双指拖动 → 平移画布");
            }
        }

        if (_docTouch.Pinch)
        {
            DocPinchZoomFromTouch(_docTouch.PinchZoom0, _docTouch.PinchDist0, d1, mid.X, mid.Y);
            return true;
        }
        if (_docTouch.Pan)
        {
            DocPanByTouch(dx, dy);
            return true;
        }
        return false;
    }

    // ======================================================================
    //  键盘：`H` = 手掌开关；按住 `空格` = 临时手掌（2026-11-16）
    //
    //  参考：Photoshop / Figma / Acrobat 的"空格临时抓手"——按住才生效、松手回到原工具；
    //  本来就开着抓手时按空格不动它（避免"按一下反而关掉"）。`H` 是 Acrobat / Edge
    //  阅读器里"抓手工具"的通用键（空格是临时版）。
    // ======================================================================

    /// <summary>文档模式才吃这些键：文档开着、不在放映 / 穿透、不在文本编辑。
    /// （穿透 = 键盘和指针都让给下层，那时画布本来就收不到点击，手掌也没有意义。）</summary>
    private bool DocKeyModeAllowed => DocView.IsOpen && !PptMode && !PassThrough && !_textBoxEditing;

    /// <summary>
    /// 文档模式的 `H` / `空格`（按下）。返回 true = 这个键归我们（调用方直接 return）。
    /// 只认**没有修饰键**的一下：`Ctrl+H` / `Shift+空格` 照旧走原路（一个键都不抢）。
    /// </summary>
    internal bool DocPanKeyDown(uint vk, uint mods, bool isRepeat)
    {
        if (mods != 0 || !DocKeyModeAllowed) return false;
        if (vk == 0x48 /*H*/)
        {
            if (!isRepeat) ToggleDocPanFromUi();      // 和主条那一格同一条动作
            return true;
        }
        if (vk != 0x20 /*Space*/) return false;
        if (isRepeat) return true;                    // 按住不放的自动重复照吞
        if (!DocPanMode) { SetDocPan(true); _docTouch.PanBySpace = true; }
        return true;
    }

    /// <summary>
    /// 松手：**只有按下那一下是我们开的手掌才收回**；原本就是手掌的照旧留着。
    /// 返回 true = 这一下归我们。
    /// </summary>
    internal bool DocPanKeyUp(uint vk)
    {
        if (vk != 0x20 /*Space*/ || !_docTouch.PanBySpace) return false;
        _docTouch.PanBySpace = false;
        SetDocPan(false);
        return true;
    }

    // ---- 自检钩子（--touchtest）：H / 空格走同一条判据，不另写一套 ----------------

    internal bool DocPanSpaceHeldForTest => _docTouch.PanBySpace;
}

