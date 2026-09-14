using System.Text;

namespace InkEngine;

/// <summary>
/// 键位的作用域。**这是整套快捷键设计的地基**，也是当年计划里第 6 个要拍板的问题。
///
///   · <see cref="Global"/>：进程级热键（`RegisterHotKey`），**任何程序在前台都生效**。
///     只放"切换类"动作：开关批注、换工具、穿透、清空、退出。
///     绝不能全局注册 Ctrl+C / Ctrl+V / Ctrl+Z——那会把所有程序的复制粘贴撤销
///     全抢走，是流氓软件的行为（Windows 上也只有极少数软件敢这么干）。
///   · <see cref="Annotation"/>：批注层拿到键盘时才生效（"批注键盘模式"）。
///     编辑类动作都在这里：撤销 / 重做 / 全选 / 复制 / 删除 / 方向键微调。
///
/// 一个动作可以同时挂两档（撤销既有全局 Ctrl+Alt+Z，也有批注内的 Ctrl+Z）。
/// </summary>
internal enum KeyScope
{
    Global = 0,
    Annotation = 1,
}

/// <summary>
/// 快捷键能触发的动作。**动作是引擎的，不是界面的**：界面将来只是给这些动作
/// 画按钮、显示键位提示。
/// </summary>
internal enum KeyAction
{
    None = 0,

    // —— 全局（切换类）——
    TogglePassThrough,
    ToolPen,
    ToolHighlighter,
    ToolLaser,
    ToolEraser,
    ToolAreaEraser,
    ToolCapture,
    ToolMarquee,
    ToolLine,
    ToolRectangle,
    ToolEllipse,
    ToolCircle,
    ToolTriangle,
    ToolParallelogram,
    Undo,
    Clear,
    ToggleHud,
    CycleWidth,
    ToggleKeyboardMode,
    CyclePassThroughMode,
    Quit,
    HostBenchmark,          // 开发期：性能基准（宿主实现）
    HostMemoryProbe,        // 开发期：内存探测（宿主实现）

    // —— 批注内（编辑类）——
    Redo,
    SelectAll,
    Duplicate,
    DeleteSelected,
    CancelSelection,
    ToggleVertexEdit,
    PasteImage,
    NudgeLeft,
    NudgeUp,
    NudgeRight,
    NudgeDown,
    NudgeLeftFar,
    NudgeUpFar,
    NudgeRightFar,
    NudgeDownFar,
}

/// <summary>
/// 一个按键组合：修饰键 + 主键。主键用 Windows 虚拟键码（VK）。
/// </summary>
internal readonly struct KeyChord : IEquatable<KeyChord>
{
    public const uint ModCtrl = 0x0002;
    public const uint ModAlt = 0x0001;
    public const uint ModShift = 0x0004;

    public readonly uint Modifiers;
    public readonly uint Vk;

    public KeyChord(uint modifiers, uint vk)
    {
        Modifiers = modifiers & (ModCtrl | ModAlt | ModShift);
        Vk = vk;
    }

    public bool IsValid => Vk != 0;

    /// <summary>至少要有一个修饰键才适合当全局热键（否则会把普通打字全抢走）。</summary>
    public bool HasModifier => (Modifiers & (ModCtrl | ModAlt)) != 0;

    public bool Equals(KeyChord other) => Modifiers == other.Modifiers && Vk == other.Vk;
    public override bool Equals(object obj) => obj is KeyChord k && Equals(k);
    public override int GetHashCode() => (int)(Modifiers * 397 ^ Vk);

    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Modifiers & ModCtrl) != 0) sb.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) sb.Append("Alt+");
        if ((Modifiers & ModShift) != 0) sb.Append("Shift+");
        sb.Append(VkName(Vk));
        return sb.ToString();
    }

    /// <summary>
    /// "Ctrl+Alt+P" / "Ctrl+Z" / "Delete" / "Shift+Left" 这样的写法 → 键。
    /// 解析失败返回 false 并给出原因（配置文件里写错了要能告诉用户，不能静默忽略）。
    /// </summary>
    public static bool TryParse(string text, out KeyChord chord, out string error)
    {
        chord = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "空的按键"; return false; }

        uint mods = 0;
        string main = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModCtrl; continue;
                case "alt": mods |= ModAlt; continue;
                case "shift": mods |= ModShift; continue;
            }
            if (main != null) { error = $"只允许一个主键（出现了“{main}”和“{part}”）"; return false; }
            main = part;
        }

        if (main == null) { error = "只有修饰键，没有主键"; return false; }
        if (!TryParseVk(main, out uint vk)) { error = $"不认识的键名“{main}”"; return false; }
        chord = new KeyChord(mods, vk);
        return true;
    }

    private static bool TryParseVk(string name, out uint vk)
    {
        vk = 0;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }
        }
        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f')
            && int.TryParse(name.AsSpan(1), out int fn) && fn >= 1 && fn <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }
        switch (name.ToLowerInvariant())
        {
            case "delete": case "del": vk = 0x2E; return true;
            case "esc": case "escape": vk = 0x1B; return true;
            case "left": vk = 0x25; return true;
            case "up": vk = 0x26; return true;
            case "right": vk = 0x27; return true;
            case "down": vk = 0x28; return true;
            case "space": vk = 0x20; return true;
            case "enter": case "return": vk = 0x0D; return true;
            case "tab": vk = 0x09; return true;
            case "backspace": vk = 0x08; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup": case "pgup": vk = 0x21; return true;
            case "pagedown": case "pgdn": vk = 0x22; return true;
        }
        return false;
    }

    private static string VkName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x2E => "Delete", 0x1B => "Esc", 0x25 => "Left", 0x26 => "Up",
            0x27 => "Right", 0x28 => "Down", 0x20 => "Space", 0x0D => "Enter",
            0x09 => "Tab", 0x08 => "Backspace", 0x24 => "Home", 0x23 => "End",
            0x21 => "PageUp", 0x22 => "PageDown", _ => "VK" + vk.ToString("X2"),
        };
    }
}

/// <summary>
/// 键位表：动作 → 键，带作用域。**可改、可查冲突、可落盘**。
///
/// 为什么要有这一层（而不是继续写死一串 `Ctrl+Alt+X`）：
///   1. 教室机上微信 / QQ / 输入法 / 教学软件都会抢热键（实测 `Ctrl+Alt+W`
///      被微信截图占了，只能换成 `Ctrl+Alt+6`）。**抢了要能看见、能改**。
///   2. 键位是"用户的东西"：有人习惯 Photoshop 的 `[` `]`，有人习惯 PPT 的。
///   3. 编辑类动作（Ctrl+Z 等）只能活在批注键盘模式里，两者的边界必须显式写下来，
///      否则将来某次改动很容易把 Ctrl+C 注册成全局热键——那是灾难性的。
/// </summary>
internal sealed class KeyMap
{
    internal sealed class Binding
    {
        public KeyScope Scope;
        public KeyAction Action;
        public KeyChord Chord;
        public string Note;         // 界面上给用户看的一句话
        public KeyChord DefaultChord;
        public bool IsDefault => Chord.Equals(DefaultChord);
    }

    public readonly List<Binding> Bindings = new();

    /// <summary>有没有被改过（退出时据此决定要不要落盘）。</summary>
    public bool Dirty;

    /// <summary>注册全局热键时失败的条目（被别的程序占着），给界面/日志用。</summary>
    public readonly List<string> GlobalFailures = new();

    public IEnumerable<Binding> For(KeyScope scope) => Bindings.Where(b => b.Scope == scope);

    public Binding Find(KeyScope scope, KeyAction action)
        => Bindings.FirstOrDefault(b => b.Scope == scope && b.Action == action);

    /// <summary>这个组合在这个作用域里被谁占着（null = 没人占）。</summary>
    public Binding OccupiedBy(KeyScope scope, KeyChord chord, KeyAction except)
        => Bindings.FirstOrDefault(b => b.Scope == scope && b.Action != except && b.Chord.Equals(chord));

    /// <summary>这个组合在**批注内**作用域被谁占着——全局键和批注键不能互相盖。</summary>
    public bool AnyScopeOccupied(KeyChord chord, KeyAction except)
        => Bindings.Any(b => b.Action != except && b.Chord.Equals(chord));

    /// <summary>
    /// 改一个键位。**冲突一律拒绝并给出原因**，不做"悄悄把原来的顶掉"——
    /// 用户改键时最怕的就是按下去触发了另一件事。
    /// </summary>
    public bool TrySet(KeyScope scope, KeyAction action, KeyChord chord, out string error)
    {
        error = null;
        var b = Find(scope, action);
        if (b == null) { error = $"{scope} 作用域里没有 {action} 这个动作"; return false; }
        if (!chord.IsValid) { error = "空的按键"; return false; }

        if (scope == KeyScope.Global && !chord.HasModifier)
        {
            error = $"全局热键 {chord} 没有修饰键：那会把正常打字抢走，拒绝";
            return false;
        }

        var other = OccupiedBy(scope, chord, action) ?? (scope == KeyScope.Annotation
            ? Bindings.FirstOrDefault(x => x.Scope == KeyScope.Annotation && x.Action != action && x.Chord.Equals(chord))
            : null);
        if (other != null)
        {
            error = $"{chord} 已经是“{Describe(other.Action)}”（{other.Scope}）的键";
            return false;
        }

        b.Chord = chord;
        Dirty = true;
        return true;
    }

    public void ResetToDefault(KeyScope scope, KeyAction action)
    {
        var b = Find(scope, action);
        if (b == null) return;
        b.Chord = b.DefaultChord;
        Dirty = true;
    }

    /// <summary>
    /// 自检用：整张表有没有内部矛盾。返回空列表 = 干净。
    /// </summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        foreach (var scope in new[] { KeyScope.Global, KeyScope.Annotation })
        {
            var seen = new Dictionary<KeyChord, KeyAction>();
            foreach (var b in For(scope))
            {
                if (!b.Chord.IsValid) { problems.Add($"{scope}/{b.Action} 没有键"); continue; }
                if (seen.TryGetValue(b.Chord, out var prev))
                    problems.Add($"{scope} 里 {b.Chord} 同时是 {prev} 和 {b.Action}");
                else seen[b.Chord] = b.Action;

                if (scope == KeyScope.Global && !b.Chord.HasModifier)
                    problems.Add($"全局热键 {b.Chord}（{b.Action}）没有修饰键");
            }
        }
        return problems;
    }

    /// <summary>动作的中文名（日志、冲突提示、将来的键位表都用它）。</summary>
    public static string Describe(KeyAction a) => a switch
    {
        KeyAction.TogglePassThrough => "穿透模式开关",
        KeyAction.ToolPen => "笔",
        KeyAction.ToolHighlighter => "荧光笔",
        KeyAction.ToolLaser => "激光笔",
        KeyAction.ToolEraser => "笔记橡皮擦（整笔）",
        KeyAction.ToolAreaEraser => "面积橡皮擦（矩形范围）",
        KeyAction.ToolCapture => "截图",
        KeyAction.ToolMarquee => "框选",
        KeyAction.ToolLine => "直线",
        KeyAction.ToolRectangle => "矩形",
        KeyAction.ToolEllipse => "椭圆",
        KeyAction.ToolCircle => "圆",
        KeyAction.ToolTriangle => "三角形",
        KeyAction.ToolParallelogram => "平行四边形",
        KeyAction.Undo => "撤销",
        KeyAction.Redo => "重做",
        KeyAction.Clear => "清空",
        KeyAction.ToggleHud => "性能面板开关",
        KeyAction.CycleWidth => "切换当前工具粗细",
        KeyAction.ToggleKeyboardMode => "批注键盘模式开关",
        KeyAction.CyclePassThroughMode => "切换穿透实现方式",
        KeyAction.Quit => "退出",
        KeyAction.HostBenchmark => "性能基准（开发）",
        KeyAction.HostMemoryProbe => "内存探测（开发）",
        KeyAction.SelectAll => "全选",
        KeyAction.Duplicate => "复制一份",
        KeyAction.DeleteSelected => "删除选中",
        KeyAction.CancelSelection => "取消选择",
        KeyAction.ToggleVertexEdit => "顶点编辑开关",
        KeyAction.PasteImage => "粘贴剪贴板里的图",
        KeyAction.NudgeLeft => "左移 1",
        KeyAction.NudgeUp => "上移 1",
        KeyAction.NudgeRight => "右移 1",
        KeyAction.NudgeDown => "下移 1",
        KeyAction.NudgeLeftFar => "左移 10",
        KeyAction.NudgeUpFar => "上移 10",
        KeyAction.NudgeRightFar => "右移 10",
        KeyAction.NudgeDownFar => "下移 10",
        _ => a.ToString(),
    };

    private void Add(KeyScope scope, KeyAction action, string chord, string note)
    {
        KeyChord.TryParse(chord, out var c, out _);
        Bindings.Add(new Binding
        {
            Scope = scope, Action = action, Chord = c, DefaultChord = c, Note = note,
        });
    }

    /// <summary>
    /// 默认键位表。
    ///
    /// 全局键一律 `Ctrl+Alt+…`：单 Ctrl/Alt 的键早被系统和其他软件占满了，
    /// 而 Ctrl+Alt 组合既好按、又几乎没人抢（唯一踩到的是微信的 Ctrl+Alt+W）。
    /// 批注内的键照 Windows 通用习惯（和 Word / 画图一致），不自己发明。
    /// </summary>
    public static KeyMap Default()
    {
        var m = new KeyMap();
        const KeyScope G = KeyScope.Global, A = KeyScope.Annotation;

        m.Add(G, KeyAction.TogglePassThrough, "Ctrl+Alt+P", "全屏批注：能画 / 不能画");
        m.Add(G, KeyAction.ToolPen, "Ctrl+Alt+1", "换成笔");
        m.Add(G, KeyAction.ToolHighlighter, "Ctrl+Alt+2", "换成荧光笔");
        m.Add(G, KeyAction.ToolLaser, "Ctrl+Alt+3", "换成激光笔");
        m.Add(G, KeyAction.ToolEraser, "Ctrl+Alt+4", "换成笔记橡皮擦（碰到哪一条就整条删掉）");
        m.Add(G, KeyAction.ToolAreaEraser, "Ctrl+Alt+E", "换成面积橡皮擦（黄金分割比矩形，范围内的墨被切掉）");
        m.Add(G, KeyAction.ToolCapture, "Ctrl+Alt+S", "截图：拖一个框，抓到的图放到左上角并进剪贴板");
        m.Add(G, KeyAction.ToolMarquee, "Ctrl+Alt+5", "换成框选（选择/移动/缩放/旋转）");
        m.Add(G, KeyAction.ToolLine, "Ctrl+Alt+7", "换成直线");
        m.Add(G, KeyAction.ToolRectangle, "Ctrl+Alt+8", "换成矩形");
        m.Add(G, KeyAction.ToolEllipse, "Ctrl+Alt+9", "换成椭圆");
        m.Add(G, KeyAction.ToolCircle, "Ctrl+Alt+0", "换成圆");
        m.Add(G, KeyAction.ToolTriangle, "Ctrl+Alt+T", "换成三角形");
        // Ctrl+Alt+H 在本机被别的程序占着（实测注册失败 1409），换 J。
        m.Add(G, KeyAction.ToolParallelogram, "Ctrl+Alt+J", "换成平行四边形");
        m.Add(G, KeyAction.Undo, "Ctrl+Alt+Z", "撤销一步");
        m.Add(G, KeyAction.Clear, "Ctrl+Alt+C", "清空整页");
        m.Add(G, KeyAction.ToggleHud, "Ctrl+Alt+I", "显示/隐藏性能面板");
        m.Add(G, KeyAction.CycleWidth, "Ctrl+Alt+6", "切成当前工具的下一档粗细");
        m.Add(G, KeyAction.ToggleKeyboardMode, "Ctrl+Alt+K", "键盘归批注层（编辑快捷键生效）");
        m.Add(G, KeyAction.CyclePassThroughMode, "Ctrl+Alt+Y", "换穿透的实现方式");
        m.Add(G, KeyAction.Quit, "Ctrl+Alt+X", "退出");
        m.Add(G, KeyAction.HostBenchmark, "Ctrl+Alt+B", "开发期：一万笔基准");
        m.Add(G, KeyAction.HostMemoryProbe, "Ctrl+Alt+M", "开发期：内存/显存探测");

        m.Add(A, KeyAction.Undo, "Ctrl+Z", "撤销一步");
        m.Add(A, KeyAction.Redo, "Ctrl+Y", "重做");
        m.Add(A, KeyAction.SelectAll, "Ctrl+A", "全选");
        m.Add(A, KeyAction.Duplicate, "Ctrl+D", "复制一份");
        m.Add(A, KeyAction.DeleteSelected, "Delete", "删除选中");
        m.Add(A, KeyAction.CancelSelection, "Esc", "取消选择");
        m.Add(A, KeyAction.ToggleVertexEdit, "Enter", "进/出顶点编辑（选中一个图形后可用）");
        m.Add(A, KeyAction.PasteImage, "Ctrl+V", "把剪贴板里的图粘到左上角");
        m.Add(A, KeyAction.NudgeLeft, "Left", "左移 1 像素");
        m.Add(A, KeyAction.NudgeUp, "Up", "上移 1 像素");
        m.Add(A, KeyAction.NudgeRight, "Right", "右移 1 像素");
        m.Add(A, KeyAction.NudgeDown, "Down", "下移 1 像素");
        m.Add(A, KeyAction.NudgeLeftFar, "Shift+Left", "左移 10 像素");
        m.Add(A, KeyAction.NudgeUpFar, "Shift+Up", "上移 10 像素");
        m.Add(A, KeyAction.NudgeRightFar, "Shift+Right", "右移 10 像素");
        m.Add(A, KeyAction.NudgeDownFar, "Shift+Down", "下移 10 像素");
        return m;
    }

    /// <summary>把整张表拼成一段文本（启动日志、自检、将来的键位页都用它）。</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var scope in new[] { KeyScope.Global, KeyScope.Annotation })
        {
            sb.AppendLine(scope == KeyScope.Global
                ? "全局热键（任何程序在前台都生效）："
                : "批注内快捷键（批注键盘模式打开时生效）：");
            foreach (var b in For(scope))
                sb.AppendLine($"  {b.Chord,-16} {Describe(b.Action),-14} {b.Note}");
        }
        return sb.ToString();
    }
}
