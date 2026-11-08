using InkEngine;

namespace InkUi;

/// <summary>
/// 一条“界面上可点的动作”。它不是第二套命令系统：
/// 真正的业务仍然走 <see cref="IEngineCommands"/> / PageHost；
/// 这里只把“标题、图标、快捷键、可用性、选中态、执行”集中成一份，
/// 让工具条/菜单/设置行/快捷键/自检不再各写一套。
/// </summary>
public enum UiActionKind
{
    /// <summary>点一下执行（备份、恢复、打开页面等）。</summary>
    Command = 0,

    /// <summary>开关：有选中态，点一下翻转。</summary>
    Toggle = 1,

    /// <summary>档位：点一下/左右键循环到下一档。</summary>
    Choice = 2,

    /// <summary>进入另一个页面/子页。</summary>
    Navigate = 3,
}

/// <summary>
/// 一条 UI 动作。字段刻意保持扁平：不做继承、不做表达式树、不做命令总线。
/// </summary>
public sealed class UiAction
{
    /// <summary>稳定 ID（ASCII、点分隔、全小写）。标签改了 ID 不改；发布后不重用。</summary>
    public string Id { get; init; } = "";

    /// <summary>中文标题。</summary>
    public string Label { get; init; } = "";

    /// <summary>副标题/说明（可为空）。</summary>
    public string Hint { get; init; }

    /// <summary>图标 key（可为空）。</summary>
    public string Icon { get; init; }

    public UiActionKind Kind { get; init; } = UiActionKind.Command;

    /// <summary>显示用的快捷键文本；真正的键位仍以 KeyBindings 为准。</summary>
    public string ShortcutText { get; init; }

    /// <summary>与引擎快捷键共享时填；界面不靠它执行，只用来查键位文本。</summary>
    public KeyAction? EngineAction { get; init; }

    /// <summary>null = 总是可用。</summary>
    public Func<bool> IsEnabled { get; init; }

    /// <summary>null = 不是开关/档位。</summary>
    public Func<bool> IsChecked { get; init; }

    /// <summary>执行入口。必须只做这一条动作，不在这里写页面/输入优先级。</summary>
    public Action Execute { get; init; } = static () => { };
}
