using System;
using System.Collections.Generic;

namespace InkEngine;

/// <summary>浮层关闭策略（引擎侧；和 InkUi.PopupStack 的语义一致，但不跨工程引用）。</summary>
[Flags]
public enum OverlayDismissPolicy
{
    None = 0,
    Outside = 1,
    Escape = 2,
    OutsideOrEscape = Outside | Escape,
}

/// <summary>
/// 一个可参与输入路由的浮层。绘制仍由 Overlay/现有代码负责；
/// 这个接口只回答“在不在、吃不吃、捕获后跟谁”。
/// </summary>
public interface IOverlayLayer
{
    string Id { get; }
    int Priority { get; }
    bool Visible { get; }
    bool ConsumesInput { get; }
    bool BlocksInputBelow { get; }
    bool WantsCapture { get; }
    RectF Bounds { get; }
    OverlayDismissPolicy DismissPolicy { get; }
    bool IsAnimating { get; }

    bool HitTest(float x, float y);
    bool PointerDown(float x, float y, uint pointerId, uint pointerType);
    bool PointerMove(float x, float y, uint pointerId);
    bool PointerUp(float x, float y, uint pointerId);
    bool Wheel(float x, float y, int delta);

    /// <summary>Esc/点外真正关闭时调用；不负责绘制和状态存储。</summary>
    void Dismiss();
}

/// <summary>
/// 浮层输入路由：按 Priority 从高到低问 HitTest/PointerDown，谁先返回 true 谁吃；
/// 按下后若 WantsCapture，则 Move/Up 只发给它（指针捕获）。
///
/// 边界：只管输入，不管绘制、页面栈、文字宿主、窗口。现有 PPT/回放/计时/点名/收藏
/// 通过 LegacyLayerAdapter 注册；绘制代码不搬。
/// </summary>
public sealed class OverlayLayerManager
{
    private readonly List<IOverlayLayer> _layers = new();
    private IOverlayLayer _capture;
    private uint _capturePointer;

    public IReadOnlyList<IOverlayLayer> Layers => _layers;
    public IOverlayLayer CaptureLayer => _capture;
    public uint CapturePointerId => _capturePointer;

    public void Register(IOverlayLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (string.IsNullOrWhiteSpace(layer.Id))
            throw new ArgumentException("OverlayLayer.Id 不能为空", nameof(layer));
        if (_layers.Exists(x => x.Id == layer.Id))
            throw new InvalidOperationException($"重复的 OverlayLayer.Id：{layer.Id}");
        _layers.Add(layer);
        Sort();
    }

    public bool Unregister(string id)
    {
        int at = _layers.FindIndex(x => x.Id == id);
        if (at < 0) return false;
        if (ReferenceEquals(_layers[at], _capture)) ClearCapture();
        _layers.RemoveAt(at);
        return true;
    }

    public IOverlayLayer Get(string id)
        => _layers.Find(x => string.Equals(x.Id, id, StringComparison.Ordinal));

    /// <summary>只读命中探测：不调用 PointerDown，不产生任何副作用（影子路由/调试用）。</summary>
    public IOverlayLayer PeekHit(float x, float y)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || !layer.ConsumesInput) continue;
            if (layer.HitTest(x, y)) return layer;
        }
        return null;
    }

    public void ClearCapture()
    {
        _capture = null;
        _capturePointer = 0;
    }

    /// <summary>只登记捕获，不调用 PointerDown（迁移期：旧分支已经处理按下时用）。</summary>
    public bool Capture(string id, uint pointerId)
    {
        var layer = Get(id);
        if (layer == null) return false;
        _capture = layer;
        _capturePointer = pointerId;
        return true;
    }

    /// <summary>按下：先看捕获，再按优先级问可见且吃输入的层。</summary>
    public bool PointerDown(float x, float y, uint pointerId, uint pointerType,
                            int minPriority = int.MinValue, int maxPriority = int.MaxValue)
    {
        if (_capture != null && _capturePointer == pointerId)
            return _capture.PointerDown(x, y, pointerId, pointerType);

        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (layer.Priority < minPriority || layer.Priority > maxPriority) continue;
            if (!layer.Visible || !layer.ConsumesInput) continue;
            if (!layer.HitTest(x, y)) continue;
            if (layer.PointerDown(x, y, pointerId, pointerType))
            {
                if (layer.WantsCapture)
                {
                    _capture = layer;
                    _capturePointer = pointerId;
                }
                return true;
            }
            if (layer.BlocksInputBelow) return true;
        }
        return false;
    }

    public bool PointerMove(float x, float y, uint pointerId)
    {
        if (_capture != null && _capturePointer == pointerId)
            return _capture.PointerMove(x, y, pointerId);
        return false;
    }

    public bool PointerUp(float x, float y, uint pointerId)
    {
        if (_capture == null || _capturePointer != pointerId) return false;
        var layer = _capture;
        ClearCapture();
        return layer.PointerUp(x, y, pointerId);
    }

    public bool Wheel(float x, float y, int delta)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || !layer.ConsumesInput) continue;
            if (!layer.HitTest(x, y)) continue;
            if (layer.Wheel(x, y, delta)) return true;
            if (layer.BlocksInputBelow) return true;
        }
        return false;
    }

    /// <summary>Esc：只关最上面一个允许 Esc 的层；最上层不允许时吞掉，不越级。</summary>
    public bool HandleEscape()
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || !layer.ConsumesInput) continue;
            if ((layer.DismissPolicy & OverlayDismissPolicy.Escape) == 0)
                return layer.BlocksInputBelow;
            layer.Dismiss();
            if (ReferenceEquals(layer, _capture)) ClearCapture();
            return true;
        }
        return false;
    }

    /// <summary>点外：只问最上面一个可见、吃输入且允许 Outside 的层。</summary>
    public bool HandleOutsideClick(float x, float y)
    {
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible || !layer.ConsumesInput) continue;
            if (layer.HitTest(x, y)) return false;
            if ((layer.DismissPolicy & OverlayDismissPolicy.Outside) == 0)
                return layer.BlocksInputBelow;
            layer.Dismiss();
            if (ReferenceEquals(layer, _capture)) ClearCapture();
            return true;
        }
        return false;
    }

    private void Sort()
        => _layers.Sort((a, b) => a.Priority == b.Priority
            ? string.CompareOrdinal(a.Id, b.Id)
            : a.Priority.CompareTo(b.Priority));
}
