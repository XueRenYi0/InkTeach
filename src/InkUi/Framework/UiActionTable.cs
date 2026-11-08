namespace InkUi;

/// <summary>
/// UI 动作注册表：只做“按 ID 查/执行/问状态”，不碰绘制、输入路由、页面栈。
///
/// 设计边界（照 Excalidraw ActionManager / JetBrains Action System 的薄版）：
/// - 不做命令总线、不做撤销合并、不做表达式树；
/// - 不强制可用性（<see cref="Execute"/> 只执行，调用方先用 <see cref="IsEnabled"/> 判断）；
/// - 重复 ID 直接抛异常：这是程序员错误，不能静默覆盖。
/// </summary>
public sealed class UiActionTable
{
    private readonly Dictionary<string, UiAction> _byId = new(StringComparer.Ordinal);
    private readonly List<UiAction> _ordered = new();

    public int Count => _ordered.Count;

    public IReadOnlyList<UiAction> All => _ordered;

    public void Register(UiAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (string.IsNullOrWhiteSpace(action.Id))
            throw new ArgumentException("UiAction.Id 不能为空", nameof(action));
        if (!_byId.TryAdd(action.Id, action))
            throw new InvalidOperationException($"重复的 UiAction.Id：{action.Id}");
        _ordered.Add(action);
    }

    public UiAction Get(string id)
        => !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out var action) ? action : null;

    /// <summary>执行一条动作。找不到返回 false；可用性由调用方判断（保持旧行为不变）。</summary>
    public bool Execute(string id)
    {
        var action = Get(id);
        if (action == null) return false;
        action.Execute();
        return true;
    }

    public bool IsEnabled(string id) => Get(id)?.IsEnabled?.Invoke() ?? true;

    public bool IsChecked(string id) => Get(id)?.IsChecked?.Invoke() ?? false;
}
