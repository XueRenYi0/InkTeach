// [职责] 文档「手掌」拖页：文档模式下按下拖动平移相机（不落墨、不选中）。
// [测试] --doctest

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
        DocPanMode = !DocPanMode;
        if (!DocPanMode) EndDocPan();
        SetInkStatus(DocPanMode ? "手掌：拖动画布上下看页（再点一次回到工具）" : "手掌已关：回到工具");
        Console.WriteLine($"[文档] 手掌{(DocPanMode ? "开" : "关")}");
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
}
