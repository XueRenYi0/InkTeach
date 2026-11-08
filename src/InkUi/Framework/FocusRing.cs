using InkEngine;

namespace InkUi;

/// <summary>页面内一个可聚焦目标。ActionId 与 <see cref="UiAction"/> 共用一套稳定 ID。</summary>
public readonly record struct FocusTarget(string ActionId, RectF Rect, bool Enabled = true);

/// <summary>
/// 页面内焦点环：只回答“下一个/上一个是谁、当前是谁、矩形在哪”。
/// 不执行动作、不画图、不管页面栈；这些由页面/PageHost 负责。
///
/// 借用 Unity UI Toolkit 的 FocusRing / Qt FocusScope / APG 的 Tab 语义：
/// - 默认按加入顺序（视觉顺序）走；
/// - 跳过禁用项；
/// - 首尾循环；
/// - 保留 FocusedId，页面重建 targets 时尽量恢复。
/// </summary>
public sealed class FocusRing
{
    private readonly List<FocusTarget> _targets = new();
    private int _index = -1;

    public int Count => _targets.Count;

    public string FocusedId
        => _index >= 0 && _index < _targets.Count ? _targets[_index].ActionId : null;

    public RectF FocusedRect
        => _index >= 0 && _index < _targets.Count ? _targets[_index].Rect : RectF.Empty;

    public void Clear()
    {
        _targets.Clear();
        _index = -1;
    }

    /// <summary>
    /// 重建目标列表。preserveId 为空时保留当前 FocusedId；找不到就落到第一个可用项。
    /// 这样页面滚动/行增删后焦点不会丢。
    /// </summary>
    public void SetTargets(IReadOnlyList<FocusTarget> targets, string preserveId = null)
    {
        string keep = !string.IsNullOrEmpty(preserveId) ? preserveId : FocusedId;
        _targets.Clear();
        if (targets != null)
        {
            for (int i = 0; i < targets.Count; i++)
                _targets.Add(targets[i]);
        }

        _index = -1;
        if (!string.IsNullOrEmpty(keep))
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].Enabled && string.Equals(_targets[i].ActionId, keep, StringComparison.Ordinal))
                {
                    _index = i;
                    return;
                }
            }
        }

        _index = FirstEnabledIndex();
    }

    public bool MoveNext() => MoveBy(+1);

    public bool MovePrev() => MoveBy(-1);

    /// <summary>列表语义：下 = 下一个；到末尾循环。</summary>
    public bool MoveDown() => MoveBy(+1);

    /// <summary>列表语义：上 = 上一个；到开头循环。</summary>
    public bool MoveUp() => MoveBy(-1);

    /// <summary>导航栏/横向控件：先按上一个/下一个处理，具体由页面决定。</summary>
    public bool MoveLeft() => MoveBy(-1);

    public bool MoveRight() => MoveBy(+1);

    public void Restore(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        for (int i = 0; i < _targets.Count; i++)
        {
            if (_targets[i].Enabled && string.Equals(_targets[i].ActionId, id, StringComparison.Ordinal))
            {
                _index = i;
                return;
            }
        }
    }

    private bool MoveBy(int delta)
    {
        if (_targets.Count == 0) return false;
        int start = _index;
        int i = _index;
        for (int step = 0; step < _targets.Count; step++)
        {
            i += delta;
            if (i < 0) i = _targets.Count - 1;
            else if (i >= _targets.Count) i = 0;
            if (_targets[i].Enabled)
            {
                _index = i;
                return true;
            }
        }
        _index = start;
        return false;
    }

    private int FirstEnabledIndex()
    {
        for (int i = 0; i < _targets.Count; i++)
            if (_targets[i].Enabled) return i;
        return -1;
    }
}
