// [职责] 文档「上次看到第 N 页」恢复提示：读写位置、卡片命中、继续/从头、落盘节流。
// [测试] --doctest（F 组）/ --panelshow --docresume

using System.Globalization;

namespace InkEngine;

/// <summary>
/// 跨会话恢复阅读位置的那一层（和批注落盘 <see cref="DocStore"/> 分开）。
///
/// 参考产品：Acrobat / Edge PDF 阅读器 / Kindle 的"继续阅读"提示——
///   · 打开时先摆在文档第一页，不偷偷跳走；
///   · 顶部一张小卡问"上次看到第 N 页，继续 / 从头"；
///   · 继续 = 恢复页 + 缩放 + 页内位置；从头 = 只关卡片。
/// 我们的差异：不弹模态、不挡画布（只有卡片矩形吃点击），
/// 且**同一次运行内重开不弹**（那是文档自己的相机记忆，不是跨会话）。
/// </summary>
public partial class InkEngine
{
    /// <summary>恢复卡现在可见吗（界面状态快照 / 自检看它）。</summary>
    internal bool DocResumeVisible { get; private set; }

    /// <summary>这张卡从盘上读到的位置（可见期间非空）。</summary>
    private DocResumePosition _docResume;

    /// <summary>悬停的按钮：0 = 无，1 = 继续，2 = 从头（浮层绘制读它）。</summary>
    internal int DocResumeHover;
    /// <summary>按下的按钮（0/1/2）：按下捕获、抬起时还在同一颗才执行。</summary>
    internal int DocResumePressed;

    /// <summary>节流：有一次位置变化还没写，以及下一次最早写盘时刻。</summary>
    private bool _docResumeDirty;
    private double _docResumeNextSaveAtMs;

    /// <summary>
    /// 刚打开文档那一拍为真：打开流程会顺手把工具切回笔（产品规则），
    /// 那一次 `SwitchTool` 不算"用户换工具"，不能把刚弹出的卡关掉。
    /// </summary>
    private bool _docResumeJustOpened;

    // ======================================================================
    //  读 / 写位置
    // ======================================================================

    /// <summary>
    /// 打开文档时读一次：**只有存档时刻早于本进程启动**才提示（跨会话）；
    /// 同一次运行里重开看到的是刚存的（≥ 启动时刻），不弹。
    /// 打开仍然从第 1 页 / 100% / 页顶开始——这里一个相机都不改。
    /// </summary>
    private void LoadDocResumePrompt(string storeKey)
    {
        _docResume = null;
        DocResumeVisible = false;
        DocResumeHover = 0;
        DocResumePressed = 0;
        _docResumeDirty = false;
        if (string.IsNullOrEmpty(storeKey) || DocView.Count == 0) return;

        var saved = DocResumeStore.Load(storeKey);
        if (saved == null || saved.SavedAtUnix >= DocResumeStore.ProcessStartUnix) return;

        int page = Math.Clamp(saved.Page, 1, Math.Max(1, DocView.Count));
        bool meaningful = page > 1 || saved.FractionY > 0.02f || saved.FractionX > 0.02f
                          || MathF.Abs(saved.Zoom - 1f) > 0.005f;
        if (!meaningful) return;

        _docResume = saved;
        DocResumeVisible = true;
        _docResumeJustOpened = true;
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"[文档] 上次看到第 {page} 页 · {saved.Zoom * 100f:F0}%（跨会话提示）");
    }

    /// <summary>
    /// 把这一刻的阅读位置算成"页码 + 页内比例"（和缩放无关）。
    /// 页码按**视口顶边**所在页：恢复时先让那一页出现，再按比例下移。
    /// </summary>
    private DocResumePosition CaptureDocResume()
    {
        if (!DocView.IsOpen || DocView.Count == 0) return null;
        var vp = ViewportCanvas;
        int i = DocView.IndexAt(vp.MinY);
        if (i < 0) i = DocView.FirstAtOrAfter(vp.MinY);
        if (i < 0) i = 0;
        if (i >= DocView.Count) i = DocView.Count - 1;
        var p = DocView.At(i)?.Rect;
        if (p == null) return null;
        float w = MathF.Max(1f, p.Value.MaxX - p.Value.MinX);
        float h = MathF.Max(1f, p.Value.MaxY - p.Value.MinY);
        return new DocResumePosition
        {
            Page = i + 1,
            FractionY = Math.Clamp((vp.MinY - p.Value.MinY) / h, 0f, 1f),
            FractionX = Math.Clamp((vp.MinX - p.Value.MinX) / w, 0f, 1f),
            Zoom = Math.Clamp(ViewScale, 0.2f, 4f),
            SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }

    /// <summary>有任何会改变阅读位置的动作就调它：立刻写一次，之后 1 秒内合并。</summary>
    private void NoteDocResumeChange()
    {
        if (DocStoreKey.Length == 0 || !DocView.IsOpen) return;
        _docResumeDirty = true;
        if (NowMs >= _docResumeNextSaveAtMs) SaveDocResumeNow();
    }

    /// <summary>立刻写一次当前位置（关文档 / 退出 / 节流到点）。</summary>
    internal void SaveDocResumeNow()
    {
        if (DocStoreKey.Length == 0 || !DocView.IsOpen) { _docResumeDirty = false; return; }
        var pos = CaptureDocResume();
        if (pos == null) return;
        DocResumeStore.Save(DocStoreKey, pos);
        _docResumeDirty = false;
        _docResumeNextSaveAtMs = NowMs + 1000;
    }

    /// <summary>主循环 / 渲染每拍调：把节流窗口内攒下的位置变化补写一次。</summary>
    internal void StepDocResume()
    {
        if (_docResumeJustOpened) _docResumeJustOpened = false;
        if (!_docResumeDirty || NowMs < _docResumeNextSaveAtMs) return;
        SaveDocResumeNow();
    }

    // ======================================================================
    //  卡片文案 / 几何（画与命中同一份）
    // ======================================================================

    internal string DocResumePageText()
    {
        if (_docResume == null) return "";
        int page = Math.Clamp(_docResume.Page, 1, Math.Max(1, DocView.Count));
        string text = $"上次看到第 {page} 页";
        if (MathF.Abs(_docResume.Zoom - 1f) > 0.005f)
            text += $" · {MathF.Round(_docResume.Zoom * 100f):F0}%";
        return text;
    }

    internal string DocResumeTimeText()
    {
        if (_docResume == null || _docResume.SavedAtUnix <= 0) return "";
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(_docResume.SavedAtUnix).ToLocalTime();
            return t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch { return ""; }
    }

    /// <summary>卡片这一刻的矩形（屏幕物理像素）：顶边居中、避开顶部界面和底部页码条。</summary>
    internal DocResumeCardRects DocResumeRectsNow()
    {
        var screen = ScreenRectPhysical();
        float unit = UiUnit;
        var r = DocResumeLayout.Compute(screen, unit, DocResumePageText(), DocResumeTimeText());

        // 顶部若有界面（工具条拖到上半屏），卡片让到它下面。
        var ui = UiQueryBoundsNow();
        if (!ui.IsEmpty)
        {
            float uiBottom = ui.MaxY * unit;
            float uiTop = ui.MinY * unit;
            if (uiTop < r.Card.MaxY && uiBottom > r.Card.MinY)
            {
                float dy = (uiBottom + 12f * unit) - r.Card.MinY;
                if (dy > 0f) r = DocResumeLayout.Shift(r, dy);
            }
        }

        // 页码条在屏幕底边；万一卡片被界面压下来撞到它，就停在条上方。
        if (PageBarVisible)
        {
            var bar = PptBarRect();
            if (r.Card.Intersects(bar))
            {
                float dy = (bar.MinY - 12f * unit) - r.Card.MaxY;
                if (dy < 0f) r = DocResumeLayout.Shift(r, dy);
            }
        }

        // 最后兜底：整张卡必须在屏幕内（窄屏 / 极端界面档位）。
        if (r.Card.MaxY > screen.MaxY - 8f * unit)
            r = DocResumeLayout.Shift(r, (screen.MaxY - 8f * unit) - r.Card.MaxY);
        if (r.Card.MinY < screen.MinY + 8f * unit)
            r = DocResumeLayout.Shift(r, (screen.MinY + 8f * unit) - r.Card.MinY);
        return r;
    }

    /// <summary>只要卡片矩形（脏区 / 自检用）。</summary>
    internal RectF DocResumeCardRect() => DocResumeRectsNow().Card;

    // ======================================================================
    //  指针：卡片消费、按钮反馈、继续 / 从头
    // ======================================================================

    /// <summary>
    /// 移动：指针在卡上就更新悬停。返回 true = 这一下归卡片（调用方不再画工具环）。
    /// </summary>
    internal bool UpdateDocResumeHover(float screenX, float screenY)
    {
        if (!DocResumeVisible) return false;
        var r = DocResumeRectsNow();
        bool inside = r.Card.Contains(screenX, screenY);
        int hit = inside ? (r.Continue.Contains(screenX, screenY) ? 1
                          : r.StartOver.Contains(screenX, screenY) ? 2 : 0) : 0;
        if (hit != DocResumeHover)
        {
            DocResumeHover = hit;
            _dirty = true;
        }
        return inside;
    }

    /// <summary>
    /// 按下：只有卡片矩形吃这一下（其余画布照常落墨）。返回 true = 已接管。
    /// 按下按钮先给按下反馈，抬起时还在同一颗才执行（和工具条按钮同一套手感）。
    /// </summary>
    internal bool TryDocResumePointerDown(IntPtr hWnd, float screenX, float screenY)
    {
        if (!DocResumeVisible) return false;
        var r = DocResumeRectsNow();
        if (!r.Card.Contains(screenX, screenY)) return false;
        DocResumePressed = r.Continue.Contains(screenX, screenY) ? 1
                         : r.StartOver.Contains(screenX, screenY) ? 2 : 0;
        DocResumeHover = DocResumePressed;
        Native.SetCapture(hWnd);
        _dirty = true;
        ApplyCursor();
        return true;
    }

    /// <summary>抬起：按下的那颗和抬起的这颗一致才执行。</summary>
    internal void DocResumePointerUp(float screenX, float screenY)
    {
        int pressed = DocResumePressed;
        DocResumePressed = 0;
        if (!DocResumeVisible) return;
        var r = DocResumeRectsNow();
        int hit = r.Continue.Contains(screenX, screenY) ? 1
                : r.StartOver.Contains(screenX, screenY) ? 2 : 0;
        if (pressed != 0 && hit == pressed)
        {
            if (pressed == 1) DocResumeContinue();
            else DocResumeStartOver();
            return;
        }
        DocResumeHover = hit;
        _dirty = true;
    }

    /// <summary>
    /// 「继续」：恢复页 + 缩放 + 页内比例（视口左上角落到存档位置），再夹进合法范围。
    /// 只在卡片可见、文档还开着时有效。
    /// </summary>
    internal void DocResumeContinue()
    {
        if (!DocResumeVisible || _docResume == null || !DocView.IsOpen || DocView.Count == 0) return;
        var pos = _docResume;
        int idx = Math.Clamp(pos.Page - 1, 0, DocView.Count - 1);
        DocResumeVisible = false;
        DocResumeHover = 0;
        DocResumePressed = 0;
        _docResume = null;

        float zoom = Math.Clamp(pos.Zoom, DocZoomFitMin, DocZoomMax);
        ViewScale = zoom;
        DocZoom = zoom;
        DocView.SetViewScale(zoom);

        var p = DocView.At(idx)?.Rect;
        if (p != null)
        {
            float w = MathF.Max(1f, p.Value.MaxX - p.Value.MinX);
            float h = MathF.Max(1f, p.Value.MaxY - p.Value.MinY);
            ViewOffsetX = ClampOffsetX(_virtualX - (p.Value.MinX + pos.FractionX * w) * zoom);
            ViewOffsetY = ClampOffset(_virtualY - (p.Value.MinY + pos.FractionY * h) * zoom);
        }
        _camAnimating = false;
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"[文档] 继续上次位置：第 {idx + 1} 页 · {zoom * 100f:F0}%");
    }

    /// <summary>「从头」：只关卡片，不动相机（打开本来就在第 1 页 / 100% / 页顶）。</summary>
    internal void DocResumeStartOver()
    {
        if (!DocResumeVisible) return;
        DismissDocResume("从头");
    }

    /// <summary>
    /// 关掉提示（不跳转）。下面这些入口必须调用：从头、Esc、落笔/擦除、换工具、
    /// 翻页/跳页、缩放、手掌开始、关文档。**点画布其它地方不算**。
    /// </summary>
    internal void DismissDocResume(string why)
    {
        if (!DocResumeVisible) return;
        DocResumeVisible = false;
        DocResumeHover = 0;
        DocResumePressed = 0;
        _docResume = null;
        _dirty = true;
        NotifyUiStateChanged();
        Console.WriteLine($"[文档] 恢复提示关闭（{why}）");
    }

    // ======================================================================
    //  自检钩子（--doctest / --panelshow --docresume）
    // ======================================================================

    /// <summary>走真实的打开流程（含读位置、建页、相机就位），但不经过文件对话框。</summary>
    internal void StartDocViewForTest(List<DocPages.Spec> specs, string title, string storeKey)
        => StartDocView(specs, title, storeKey);

    /// <summary>模拟"第一笔内容落笔"那一刻的关闭（真实输入在 OnPointerDown 调同一处）。</summary>
    internal void NoteDocResumeContentStartForTest() => DismissDocResume("落笔");
}
