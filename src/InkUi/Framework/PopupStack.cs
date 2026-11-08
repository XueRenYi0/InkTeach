using InkEngine;
using Vortice.Direct2D1;

namespace InkUi;

/// <summary>弹层的关闭策略。按位组合，和 Web popover 的 auto/manual、Qt closePolicy 同义。</summary>
[Flags]
public enum DismissPolicy
{
    None = 0,
    Outside = 1,
    Escape = 2,
    OutsideOrEscape = Outside | Escape,
}

/// <summary>
/// 一个弹层：菜单、下拉、确认框。只描述“范围/关闭策略/绘制/关闭回调”，
/// 不负责页面栈、输入优先级、文字宿主；这些由 PageHost / OverlayLayerManager 管。
/// </summary>
public sealed class Popup
{
    public string Id { get; init; } = "";
    public string OwnerPageId { get; init; }
    public RectF Bounds { get; init; }
    public DismissPolicy Dismiss { get; init; } = DismissPolicy.OutsideOrEscape;

    /// <summary>关闭后是否把焦点还给触发项（由页面/PageHost 执行）。</summary>
    public bool RestoresFocus { get; init; } = true;

    /// <summary>点外但又不允许轻关闭时，是否吞掉这一下（默认吞，避免点到下层）。</summary>
    public bool BlocksInputBelow { get; init; } = true;

    public Action OnClose { get; init; }

    /// <summary>可选的精确命中；null = 用 Bounds。</summary>
    public Func<float, float, bool> HitTest { get; init; }

    public Action<ID2D1DeviceContext> Draw { get; init; }

    public bool Contains(float x, float y)
        => HitTest != null ? HitTest(x, y) : Bounds.Contains(x, y);
}

/// <summary>
/// 弹层栈：Esc/点外只问最上面一层；关掉一层后，下一层才重新暴露。
/// 这是 Dear ImGui Popup 栈 / Radix Dismissable Layer / Web top layer 的薄版。
/// 不做 z-order 表、不做页面栈、不做焦点陷阱（这些是 PageHost/OverlayLayerManager 的职责）。
/// </summary>
public sealed class PopupStack
{
    private readonly List<Popup> _stack = new();

    public int Count => _stack.Count;

    public bool IsEmpty => _stack.Count == 0;

    public Popup Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

    public IReadOnlyList<Popup> All => _stack;

    public void Push(Popup popup)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (string.IsNullOrWhiteSpace(popup.Id))
            throw new ArgumentException("Popup.Id 不能为空", nameof(popup));
        _stack.Add(popup);
    }

    /// <summary>关闭最上面一层。返回 true = 真关了一层。</summary>
    public bool PopTop()
    {
        if (_stack.Count == 0) return false;
        Popup top = _stack[_stack.Count - 1];
        _stack.RemoveAt(_stack.Count - 1);
        top.OnClose?.Invoke();
        return true;
    }

    /// <summary>关闭某个页面的所有弹层（页面关闭时调用）。</summary>
    public int CloseAll(string ownerPageId = null)
    {
        int closed = 0;
        for (int i = _stack.Count - 1; i >= 0; i--)
        {
            if (ownerPageId != null
                && !string.Equals(_stack[i].OwnerPageId, ownerPageId, StringComparison.Ordinal))
                continue;
            Popup popup = _stack[i];
            _stack.RemoveAt(i);
            popup.OnClose?.Invoke();
            closed++;
        }
        return closed;
    }

    /// <summary>
    /// Esc：只关最上面一层；最上层不允许 Esc 时，不越级关下层。
    /// 返回 true = 这一下已被弹层消费。
    /// </summary>
    public bool HandleEscape()
    {
        if (_stack.Count == 0) return false;
        Popup top = _stack[_stack.Count - 1];
        if ((top.Dismiss & DismissPolicy.Escape) == 0) return top.BlocksInputBelow;
        PopTop();
        return true;
    }

    /// <summary>
    /// 点外：只问最上面一层。
    /// - 点在弹层内 → false（让弹层自己处理）；
    /// - 允许 Outside 关闭 → 关掉并返回 true；
    /// - 不允许关闭但 BlocksInputBelow → 吞掉，返回 true；
    /// - 否则 false（让下层处理）。
    /// </summary>
    public bool HandlePointerDown(float x, float y)
    {
        if (_stack.Count == 0) return false;
        Popup top = _stack[_stack.Count - 1];
        if (top.Contains(x, y)) return false;
        if ((top.Dismiss & DismissPolicy.Outside) != 0)
        {
            PopTop();
            return true;
        }
        return top.BlocksInputBelow;
    }

    public void Draw(ID2D1DeviceContext ctx)
    {
        for (int i = 0; i < _stack.Count; i++)
            _stack[i].Draw?.Invoke(ctx);
    }
}
