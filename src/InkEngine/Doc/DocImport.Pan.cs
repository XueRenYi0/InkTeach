// [职责] 文档「手掌」拖页 + 双指捏合缩放 / 放大后平移 / 触控板捏合缩放（文档相机）。
// [测试] --doctest / --touchtest

using System.Numerics;
using System.Runtime.InteropServices;

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
        /// <summary>两指落下时的**屏幕**中点（平移方向锁的起点）。</summary>
        public Vector2 StartMidScreen;
        /// <summary>上一拍的**屏幕**中点（平移增量；绝不能拿画布中点，相机会反过来污染它）。</summary>
        public Vector2 LastMidScreen;
        /// <summary>距离变化连续超过门槛的拍数（防"两个触点事件先后到达"的假捏合）。</summary>
        public int PinchHot;
        /// <summary>这一次按住空格开的手掌（松手才收；原本就是手掌的不动它）。</summary>
        public bool PanBySpace;

        /// <summary>触控板捏合（WM_GESTURE / GID_ZOOM）的基线；见 <see cref="DocPadGestureState"/>。</summary>
        public readonly DocPadGestureState PadPinch = new();
    }

    /// <summary>
    /// 触控板捏合缩放的基线（2026-11-16）。**一枚手势一段基线**：
    /// GID_BEGIN / GID_END / 手指导致的空档都清掉，下一段重新记起点。
    /// 为什么不要"每帧取上一帧距离"：GID_ZOOM 的距离是**指针距离的绝对值**，
    /// 按比例乘出来的倍率必须始终从这一段手势的起点算，否则会累积漂移。
    /// </summary>
    internal sealed class DocPadGestureState
    {
        /// <summary>这一段手势已经记下起点（第一帧只记基线，不缩放）。</summary>
        public bool Active;
        /// <summary>起点两指距离（GID_ZOOM 的 ullArguments 低 16 位，像素）。</summary>
        public float Dist0;
        /// <summary>起点倍率（= 第一帧的 ViewScale）。</summary>
        public float Zoom0 = 1f;

        public void Reset()
        {
            Active = false;
            Dist0 = 0f;
            Zoom0 = 1f;
        }
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
    ///   ① 两指**屏幕**距离变化 &gt; <see cref="TouchGestures.PinchRatio"/>（且至少 10 逻辑像素）→
    ///      锁**捏合缩放**（以两指屏幕中点为锚；距离没变的横向滑动不会被认成缩放）；
    ///   ② 距离没变、放大到 &gt;100% 且屏幕中点位移过了方向锁 → 锁**两轴平移**（不再翻页）；
    ///   ③ 距离没变、100% 下 → 交给原来的方向锁（横 = 翻页、纵 = 漫游）。
    /// 返回 true = 这一拍已经被文档分支消费（不再往下走翻页 / 漫游）。
    ///
    /// ⚠ **只用屏幕坐标**：触点表里每根手指的屏幕点由系统事件直接给，而画布点是
    /// "屏幕点用当时的相机换算"来的——系统逐指发 WM_POINTERUPDATE 时，只有被更新的
    /// 那根换成了新相机，另一根还是旧相机时刻的值；两指表观距离会随平移/缩放乱变，
    /// 把平移误锁成捏合（2026-11-16 200% 实测：横拖 240 → 相机 ΔX +363、倍率 2.0→1.7）。
    ///
    /// ⚠ 这一轮手势的**状态**（<see cref="DocTouchState"/>）声明在本文件上面；手势文件的
    /// 路由只读它 —— 那个文件已经贴着 800 行上限（ADR-002），再塞进来就要拆文件了。
    /// </summary>
    private bool DocTouchGestureMove(in TouchGestures.PairView p, in Vector2 midScreen)
    {
        if (!DocPinchPanEligible) return false;
        float d1 = Vector2.Distance(p.ScreenA, p.ScreenB);
        // 平移增量 = 两指**屏幕中点**的位移（1:1 对应手指移动，和相机倍率无关）。
        float sdx = midScreen.X - _docTouch.LastMidScreen.X;
        float sdy = midScreen.Y - _docTouch.LastMidScreen.Y;
        _docTouch.LastMidScreen = midScreen;

        if (!_docTouch.Pinch && !_docTouch.Pan && _g2Axis == 0)
        {
            // 先看**距离**：变化超过门槛（12% 且至少 10 逻辑像素）→ 认捏合。
            // 门槛要**连续两拍**都过（两个触点的事件先后到达会把距离骗大一行，下一拍又回去）；
            // 变化特别大（> 2 倍门槛）则第一拍就锁，快捏合不拖泥带水。
            //
            // **中点位移必须明显小于距离变化**才算捏合：双指平移时系统逐指上报，
            // 一根先动、一根后动会让"距离"暂涨 60px，中点同时也走了 60px；只看距离
            // 会把平移误锁成捏合（200% 实测相机跳 474px、倍率 2.0→1.6）。
            float need = MathF.Max(TouchGestures.PinchRatio * _docTouch.PinchDist0,
                                   TouchGestures.PinchMinLogical * DpiScale);
            float change = _docTouch.PinchDist0 > 1f ? MathF.Abs(d1 - _docTouch.PinchDist0) : 0f;
            float midMove = (midScreen - _docTouch.StartMidScreen).Length();
            bool spanDominates = change > need
                                 && midMove <= change * 0.35f;
            _docTouch.PinchHot = spanDominates ? _docTouch.PinchHot + 1 : 0;
            if (_docTouch.PinchDist0 > 1f
                && spanDominates
                && (_docTouch.PinchHot >= 2 || change > need * 2f))
            {
                _docTouch.Pinch = true;                    // 从锁定这一刻起连续缩放（首帧不跳）
                Console.WriteLine($"触摸：双指捏合 → 文档缩放（{DocZoomPercentText} 起，d={d1:F0}）");
                _docTouch.PinchDist0 = MathF.Max(1f, d1);
                _docTouch.PinchZoom0 = ViewScale;
            }
            else if (ViewScale > 1.0005f
                     && midMove >= TouchGestures.DirLockLogical * DpiScale)
            {
                // 锁定前的起步位移要补上：方向锁 10 逻辑像素，只按"这一拍增量"走
                // 会丢掉起步的几十像素，触摸拖页就比鼠标"死"。
                _docTouch.Pan = true;                      // 放大后两指拖动 = 平移画布（两轴），不翻页
                DocPanByTouch(midScreen.X - _docTouch.StartMidScreen.X,
                              midScreen.Y - _docTouch.StartMidScreen.Y);
                Console.WriteLine("触摸：放大后双指拖动 → 平移画布");
                return true;
            }
        }

        if (_docTouch.Pinch)
        {
            DocPinchZoomFromTouch(_docTouch.PinchZoom0, _docTouch.PinchDist0, d1,
                                  midScreen.X, midScreen.Y);
            return true;
        }
        if (_docTouch.Pan)
        {
            DocPanByTouch(sdx, sdy);
            return true;
        }
        return false;
    }

    // ======================================================================
    //  触控板捏合缩放（WM_GESTURE / GID_ZOOM，2026-11-16）
    //
    //  参考：Windows 官方 "Windows Touch Gestures"（WM_GESTURE 的 GID_ZOOM：
    //  ullArguments 低 16 位 = 两指距离；ptsLocation = 手势中心，屏幕坐标）＋
    //  Acrobat / Edge / 浏览器 PDF 阅读器的"以手势中心为锚连续缩放"。
    //
    //  和触摸屏那条的分工：触摸屏走 WM_POINTER 的**两指捏合判据**（上面一段），
    //  精确式触控板（precision touchpad）走**系统手势** WM_GESTURE。两条路都汇到
    //  `SetDocZoomCore`（50–300%、补丁重建、页码/百分比通知只有一份）。
    //  同一时刻只允许一条生效：触摸已经占着手势状态机时，WM_GESTURE 一律忽略，
    //  否则一次"两指在触控板上捏 + 一只手在屏幕上捏"会让倍率跳两次。
    // ======================================================================

    /// <summary>
    /// 触控板捏合缩放能不能接管：**文档开着、不在放映/白板、不在文本编辑、
    /// 没有整页界面盖着、触屏手势也没在进行**。
    /// （比 <see cref="DocPinchPanEligible"/> 少一条"有没有选中对象"——触控板捏合
    ///  缩的是**文档相机**，和"选中对象缩放"不冲突，用户不必先取消选中。）
    /// </summary>
    internal bool DocGestureZoomEligible =>
        DocView.IsOpen && !PptMode && !BoardOn && !_textBoxEditing
        && !UiOverlayOpen && _touchMode == TouchMode.None;

    /// <summary>
    /// 处理一条 `WM_GESTURE`。**只吃 GID_ZOOM**（SetGestureConfig 也只放行了这一条）。
    ///
    /// 返回值：`IntPtr.Zero` = 已处理（**不要再交给默认处理**，否则系统会在缩放的
    /// 同时自己滚/缩一遍）；其余返回 false 的路径（句柄拿不到）也回 0，因为这条消息
    /// 本来就不该落到 DefWindowProc 去做默认缩放。
    /// </summary>
    private IntPtr HandleGestureMessage(IntPtr hWnd, IntPtr wParam, IntPtr lParam)
    {
        var gi = new Native.GESTUREINFO { cbSize = (uint)Marshal.SizeOf<Native.GESTUREINFO>() };
        // ⚠ 句柄在 **lParam**（WinUser.h 的注释原话："HGESTUREINFO is received by a
        //   window in the lParam of a WM_GESTURE message"），wParam 不参与。
        if (!Native.GetGestureInfo(lParam, ref gi))
        {
            Native.CloseGestureInfoHandle(lParam);
            return IntPtr.Zero;
        }
        try
        {
            // 一段手势的首尾：只清基线，不缩放。
            if (gi.dwID == Native.GID_BEGIN || gi.dwID == Native.GID_END)
            {
                _docTouch.PadPinch.Reset();
                return IntPtr.Zero;
            }
            // 其它手势理论上到不了（SetGestureConfig 挡住了）；万一来了也吃掉，
            // 不让默认处理去翻页/旋转。
            if (gi.dwID != Native.GID_ZOOM) return IntPtr.Zero;

            ApplyDocGestureZoom((int)(gi.ullArguments & 0xFFFF), gi.ptsLocation.X, gi.ptsLocation.Y);
            return IntPtr.Zero;
        }
        finally
        {
            Native.CloseGestureInfoHandle(lParam);
        }
    }

    /// <summary>
    /// 一帧 GID_ZOOM：第一帧记基线，之后 `zoom = zoom0 × dist / dist0`，
    /// 锚点 = **手势中心**（屏幕坐标；SetDocZoomCore 自己换成画布点并保持不动）。
    /// 夹取（0.5–3.0）在 SetDocZoomCore 里，只有一份。
    /// </summary>
    private void ApplyDocGestureZoom(int distancePx, int screenX, int screenY)
    {
        var g = _docTouch.PadPinch;
        if (!DocGestureZoomEligible)
        {
            g.Reset();                       // 文档关掉 / 触屏在捏：这一段不再续
            return;
        }
        if (distancePx < 1) return;          // 没距离（单指/异常帧）不参与比例

        if (!g.Active)
        {
            g.Active = true;
            g.Dist0 = distancePx;
            g.Zoom0 = ViewScale;
            return;                          // 第一帧只记起点：首帧不跳
        }

        float zoom = g.Zoom0 * (distancePx / g.Dist0);
        if (float.IsNaN(zoom) || float.IsInfinity(zoom)) return;
        SetDocZoomCore(zoom, DocZoomMin, screenX, screenY, quiet: true);
    }

    /// <summary>
    /// 自检钩子（`--touchtest`）：喂一帧合成手势，**和真实 WM_GESTURE 走同一套判据**
    /// （不造 HWND、不碰手势句柄）。<paramref name="gestureId"/> 用 `Native.GID_*`；
    /// GID_BEGIN / GID_END 清基线，GID_ZOOM 用 <paramref name="ullArguments"/> 低 16 位。
    /// </summary>
    internal void HandleDocGestureForTest(uint gestureId, long ullArguments, int screenX, int screenY)
    {
        if (gestureId == Native.GID_BEGIN || gestureId == Native.GID_END)
        {
            _docTouch.PadPinch.Reset();
            return;
        }
        if (gestureId != Native.GID_ZOOM) return;
        ApplyDocGestureZoom((int)(ullArguments & 0xFFFF), screenX, screenY);
    }

    /// <summary>自检用：这一段触控板手势是否已经记下基线。</summary>
    internal bool DocPadGestureActiveForTest => _docTouch.PadPinch.Active;

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

    // ======================================================================
    //  左右边缘整页翻页条（2026-11-25，自研；几何/状态见 Ppt/PageEdge.cs、PageEdgeBar.cs）
    //
    //  放在这里而不是 Ppt.cs 的原因：它服务的**不只是 PPT**——白板也用它翻屏
    //  （白板本身就是"一页一屏"，见 PageBarVisible 的注释）；而 Ppt.cs 已经有 1900 行。
    //
    //  三条硬规矩（用户 2026-11-25 定的）：
    //    1. **默认关**（"兼容之前"= 老用户升级后完全无感），开关落 `ui.pageEdge`；
    //    2. **PDF / 图片文档不加**（文档底部已经有翻页条，再加左右两套就是三套入口）；
    //    3. 平时几乎不可见，指针靠近 / 按下才显形（它压在画布左右中部，会挡内容）。
    // ======================================================================

    /// <summary>边缘翻页条的运行期状态（显隐 / 悬停 / 两块接输入小窗）。</summary>
    internal readonly PageEdgeBar _pageEdge = new();

    /// <summary>设置页那个开关（"ui.pageEdge"；**默认关**，所以默认值不落盘）。</summary>
    internal bool PageEdgeOn
    {
        get => _pageEdge.Enabled;
        set
        {
            if (_pageEdge.Enabled == value) return;
            _pageEdge.Enabled = value;
            SetUiPref("pageEdge", value ? "1" : null);
            if (!value) _pageEdge.HideInputWindows();
            _dirty = true;
            NotifyUiStateChanged();
        }
    }

    /// <summary>
    /// 条这一刻该不该画 / 该不该点得到。判据写一处，绘制 / 命中 / 脏区 / 输入小窗全读它。
    ///
    /// **PPT 放映 + 白板**；以下一律没有：
    ///   · 文档（PDF / 图片）——理由见上面第 2 条；
    ///   · 回放 / 截图 / 吸管 / 收藏落点 / 反馈二维码 / 文本框编辑——这些是**整屏模态**
    ///     或正在占用指针，屏幕上不该有第二个能被点的东西（同 MultiMonitorSkip 的判据）；
    ///   · 穿透：**PPT 放映例外**——那时有条的接输入小窗撑着，老师仍然要能左右翻页
    ///     （和底部 PPT 条同一条口径）；其他模式穿透 = 全让开。
    /// </summary>
    internal bool PageEdgeVisible =>
        _pageEdge.Enabled
        && (BoardOn || (PptMode && !DocView.IsOpen))
        && !ReplayActive && !CaptureActive && !ColorPickActive
        && !LibraryPlacementMode && !FeedbackQrVisible && !_textBoxEditing
        && (PptMode || !PassThrough);

    /// <summary>某一边的整组矩形（物理屏幕坐标；绘制 / 命中 / 脏区都读它）。</summary>
    internal RectF PageEdgeGroupRect(bool left)
        => PageEdge.GroupRect(ScreenRectPhysical(), left, UiUnit);

    /// <summary>某一边第 <paramref name="index"/> 格（0 上翻 / 1 页码 / 2 下翻）。</summary>
    internal RectF PageEdgeCellRect(bool left, int index)
        => PageEdge.CellRect(ScreenRectPhysical(), left, index, UiUnit);

    /// <summary>
    /// 两边的整组**外接矩形**（物理屏幕坐标）。⚠ 它横跨整屏，**只能用于诊断/报告**，
    /// 不许拿它算脏区或多屏跳过（那会变成每次悬停重画半个屏幕，见 Overlay.Frame 的注释）。
    /// </summary>
    internal RectF PageEdgeBothRect()
    {
        var l = PageEdgeGroupRect(true);
        var r = PageEdgeGroupRect(false);
        l.Add(r.MinX, r.MinY);
        l.Add(r.MaxX, r.MaxY);
        return l;
    }

    /// <summary>这一下点在某一边的整组上吗（**只有看得见的那一块算**——同 PptBarContains）。</summary>
    internal bool PageEdgeContains(float x, float y)
        => PageEdgeVisible && PageEdge.SideAt(x, y, ScreenRectPhysical(), UiUnit) != 0;

    /// <summary>
    /// 按下：返回 true = 这一下归翻页条（引擎别再当笔画 / 别漏给下层）。
    /// 判定顺序：**先看点在哪一格**——
    ///   · 上格 = 上一页；下格 = 下一页（**按下即翻页**，触屏没有 hover，最跟手）；
    ///   · 中格（页码）：只显形，不翻页（它是"第几页"的读数，点了也不该跳）。
    /// 点在两格之间的缝里也算它的（吃掉这一下，别落墨 / 别漏给下层）。
    /// </summary>
    internal bool PageEdgePress(float x, float y)
    {
        if (!PageEdgeVisible) return false;
        var screen = ScreenRectPhysical();
        float dpi = UiUnit;
        int side = PageEdge.SideAt(x, y, screen, dpi);
        if (side == 0) return false;

        _pageEdge.Press(x, y, screen, dpi);
        int cell = PageEdge.CellAt(x, y, screen, left: side < 0, dpi);
        if (cell == 0) BarPrev();
        else if (cell == 2) BarNext();
        _dirty = true;
        return true;
    }

    /// <summary>移动：更新悬停格与"靠近就显形"的位置；返回 true = 指针就在条上（吃掉这一下）。</summary>
    internal bool PageEdgePointerMove(float x, float y)
    {
        if (!_pageEdge.Enabled) return false;
        var screen = ScreenRectPhysical();
        _pageEdge.Track(x, y, screen, UiUnit);
        return PageEdgeVisible && PageEdge.SideAt(x, y, screen, UiUnit) != 0;
    }

    /// <summary>指针离开覆盖层：不再算"靠近"（条自己会淡回去）。</summary>
    internal void PageEdgePointerLeft()
    {
        if (!_pageEdge.Enabled) return;
        _pageEdge.PointerLeft();
        _dirty = true;
    }

    /// <summary>
    /// 每帧推进：显隐动画 + 穿透下的两块接输入小窗。
    /// **两处都调**（主循环 Loop + RenderAll），和 StepPptBar 同一个理由：
    /// 自检是"抽消息＋渲染"驱动的，挂主循环里那条路永远验不到。
    /// </summary>
    internal void StepPageEdge()
    {
        var screen = ScreenRectPhysical();
        bool vis = PageEdgeVisible;
        int hoverBefore = _pageEdge.HoverCell;
        _pageEdge.Step(NowMs, vis, screen, UiUnit);

        // 穿透 + 放映：覆盖层收不到指针消息，靠这两块 1/255 的小窗接。
        // 不穿透时覆盖层自己就收得到，小窗一律收掉（别多一层吃点击）。
        _pageEdge.SyncInputWindows(_hInstance, _className, screen, UiUnit, needed: vis && PassThrough);

        if (!vis) return;
        if (_pageEdge.HoverCell != hoverBefore || _pageEdge.Animating) { _dirty = true; ApplyCursor(); }
    }

    /// <summary>
    /// 接输入小窗的消息。**只喂翻页条**（按下 / 移动 / 抬起），坐标是**物理屏幕坐标**；
    /// `PointerX/PointerY` 同时换成画布坐标（光标 / 落点反馈读它），和 PptInputWndProc 逐句对齐。
    /// </summary>
    private IntPtr PageEdgeInputWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        void SetPointerCanvas(float sx, float sy)
        {
            float cx = sx, cy = sy;
            ScreenToCanvas(ref cx, ref cy);
            PointerX = cx; PointerY = cy; PointerInside = true;
        }

        switch (msg)
        {
            case Native.WM_NCHITTEST: _cntNcHitTest++; _cntNcHitClient++; return new IntPtr(Native.HTCLIENT);
            case Native.WM_MOUSEACTIVATE: return new IntPtr(Native.MA_NOACTIVATE);
            case Native.WM_SETCURSOR: ApplyCursor(force: true); return new IntPtr(1);
            case Native.WM_ERASEBKGND: return new IntPtr(1);
            case Native.WM_PAINT:
                Native.BeginPaint(hWnd, out var ps); Native.EndPaint(hWnd, ref ps); return IntPtr.Zero;

            case Native.WM_POINTERDOWN:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntDown++; LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                if (PageEdgePress(sx, sy))
                {
                    _drawing = false;
                    Native.SetCapture(hWnd);       // 拖动要持续收到消息（会滑出小窗）
                }
                _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUPDATE:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!ReadPointer(id, out float sx, out float sy, out _, out _, out uint ptype))
                    return IntPtr.Zero;
                StampInput(); _cntMove++; LastPointerType = ptype;
                SetPointerCanvas(sx, sy);
                PageEdgePointerMove(sx, sy);
                _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUP:
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (ReadPointer(id, out float sx, out float sy, out _, out _, out _))
                {
                    StampInput(); _cntUp++;
                    SetPointerCanvas(sx, sy);
                }
                _pageEdge.Release();
                // 必须无条件放开（按下那一刻 SetCapture 过）：不然整机鼠标挂在小窗上。
                Native.ReleaseCapture();
                _drawing = false; _dirty = true; ApplyCursor();
                return IntPtr.Zero;
            }

            case Native.WM_POINTERCAPTURECHANGED:
                _cntCaptureLost++;
                _pageEdge.Release();
                _dirty = true;
                return IntPtr.Zero;

            // 指针滑出小窗：悬停收掉（条会淡回去）。
            case Native.WM_POINTERLEAVE:
                _pageEdge.PointerLeft();
                _dirty = true;
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;    // 它不画任何东西，别的消息一概不问
        }
    }

    /// <summary>自检用：条此刻的显形进度（0 = 几乎不可见，1 = 完全显形）。</summary>
    internal float PageEdgeRevealForTest => _pageEdge.Reveal;

    /// <summary>自检用：指针落在哪一格（100＋格号 左 / 200＋格号 右 / -1）。</summary>
    internal int PageEdgeHoverForTest => _pageEdge.HoverCell;

    /// <summary>自检用：穿透下的接输入小窗在不在（0 左 / 1 右）。</summary>
    internal bool PageEdgeInputShownForTest(int side) => _pageEdge.InputShownForTest(side);
}

