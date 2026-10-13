using System.Text;

namespace InkEngine;

/// <summary>
/// 键位的作用域。**这是整套快捷键设计的地基**，也是当年计划里第 6 个要拍板的问题。
///
///   · <see cref="Global"/>：进程级热键（`RegisterHotKey`），**任何程序在前台都生效**。
///     只放"切换类"动作：穿透、呼出盘、退出（2026-10-04 起统一 `Ctrl+Alt+Shift+…`）。
///     绝不能全局注册 Ctrl+C / Ctrl+V / Ctrl+Z——那会把所有程序的复制粘贴撤销
///     全抢走，是流氓软件的行为（Windows 上也只有极少数软件敢这么干）。
///   · <see cref="Annotation"/>：批注层拿到键盘时才生效（"批注键盘模式"）。
///     编辑类动作都在这里：撤销 / 重做 / 全选 / 复制 / 删除 / 方向键微调。
///
/// 一个动作可以同时挂两档（现在没有这样的动作；将来要加"全局也生效"的键时才用得上）。
/// </summary>
internal enum KeyScope
{
    Global = 0,
    Annotation = 1,
}

/// <summary>
/// 快捷键能触发的动作。**动作是引擎的，不是界面的**：界面给这些动作画按钮、
/// 显示键位提示（悬停提示用 <see cref="IUiHost.KeyText"/> 查当前键位）。
///
/// 2026-10-02 从 internal 改为 public：界面层要按动作名查键位文本画 tooltip，
/// 键位改了（settings.json）提示要跟着变，所以枚举本身也得给界面看得见。
/// </summary>
public enum KeyAction
{
    None = 0,

    // —— 全局（切换类）—— 2026-10-04 起实际配全局键的是 **穿透 / 呼出盘 / 退出**
    //（名单的权威在 KeyMap.GlobalAllowed；这里的枚举分组只是历史沿革，别拿它当现役表）
    TogglePassThrough,
    ToolPen,
    ToolEraser,
    ToggleKeyboardMode,
    Quit,

    // —— 以下动作**都挂在应用内**（批注键盘模式打开时生效）；
    // 唯一的例外是 RadialPalette（2026-10-04 起升为全局键，见它的注释）——
    ToolHighlighter,
    ToolLaser,
    ToolPixelEraser,
    /// <summary>把选区里"被橡皮擦断"的笔迹拆成独立对象（默认不拆，见 Model.SplitErasedSelection）。</summary>
    SplitErased,
    ToolCapture,
    ToolMarquee,

    /// <summary>呼出盘（Ctrl+Alt+Shift+Q）：按住 → 划向扇区 → 松手。
    /// 八扇区 = 笔 / 黑 / 红 / 蓝 / 荧光笔 / 橡皮 / 框选 / 激光（见 Engine 的呼出盘那段）。
    /// **全局作用域**（2026-10-04 用户定：从批注内 `Ctrl+Q` 升级上来，和另外两条全局键同形）。
    /// **穿透里照样能呼出**（和工具键那五个刻意不同）：它是"从下层把笔抢回来"的入口，
    /// 选扇区 = 退出穿透 + 换工具/选色。⚠ 全局热键只送按下、没有松手消息，
    /// 松手靠 PumpRadialPalette 每帧轮询（见那里）。</summary>
    RadialPalette,
    // 图形工具（直线 / 矩形 / 椭圆 / 圆 / 三角形 / 平行四边形 / 箭头 / 坐标系）
    // **刻意一个键都没有**（用户 2026-09-19 定："图形不需要加快捷键，通通取消掉"）。
    // 原来给圆 / 三角形 / 平行四边形 / 坐标系 / 数轴配过 `Ctrl+Alt+O/T/G/F/N`，这一轮全撤：
    // 它们的入口就是主条「图形」那一格的上带，点一下换一种，比记八个 `Ctrl+Alt+?` 快；
    // 也省掉一串注册冲突（`Ctrl+Alt+H` 就被别的程序占着）。
    // 所以这里**没有** ToolCircle / ToolTriangle / ToolParallelogram / ToolCoordinate / ToolNumberLine。
    /// <summary>框选工具下拖空白处的方式：矩形框 ←→ 自由套索（见 InkEngine.SelMode）。</summary>
    SelectShape,
    Undo,
    Clear,
    /// <summary>白板开关（Ctrl+B）：和点面板白板格同一条命令（`SetBoardFromUi`），开会顺手退穿透。</summary>
    ToggleBoard,
    ToggleHud,
    CycleWidth,

    // —— 批注内（编辑类）——
    Redo,
    /// <summary>复制选中对象到剪贴板（对象 + 一张图）。</summary>
    Copy,
    SelectAll,
    Duplicate,
    DeleteSelected,
    CancelSelection,
    /// <summary>粘贴：剪贴板里有我们的对象就粘对象，否则当图粘。</summary>
    Paste,
    NudgeLeft,
    NudgeUp,
    NudgeRight,
    NudgeDown,
    NudgeLeftFar,
    NudgeUpFar,
    NudgeRightFar,
    NudgeDownFar,
    /// <summary>选中对象放大 / 缩小（Ctrl+= / Ctrl+-）：以选区框中心为锚（和双指捏合同一路），
    /// 连续按合并成一步撤销（见 Engine 的"连续微调会话"）。</summary>
    ScaleUp,
    ScaleDown,
    /// <summary>白板整屏翻页：相机上移一屏（到顶就不动）。</summary>
    FlipPageUp,
    /// <summary>白板整屏翻页：相机下移一屏（下面永远还有一屏空白）。</summary>
    FlipPageDown,
    /// <summary>放映批注模式：**代 WPS/PPT 翻上一页**（键盘在我们手里，它收不到 ← 了）。</summary>
    PptPrev,
    /// <summary>放映批注模式：**代 WPS/PPT 翻下一页**。</summary>
    PptNext,
    /// <summary>放映批注模式：↑ = 视口上移（看上面的内容；有选中时改为微调）。</summary>
    PanUp,
    /// <summary>放映批注模式：↓ = 视口下移（看下面的内容；有选中时改为微调）。</summary>
    PanDown,
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
            // 缩放键（2026-10-05）：主键是 OEM 的等号 / 减号，虚拟键码固定（0xBB / 0xBD）。
            // 名字给几种写法，settings.json 手写时不至于踩空。
            case "=": case "equal": case "plus": vk = 0xBB; return true;
            case "-": case "minus": vk = 0xBD; return true;
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
            0x21 => "PageUp", 0x22 => "PageDown", 0xBB => "=", 0xBD => "-",
            _ => "VK" + vk.ToString("X2"),
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

    /// <summary>
    /// 某个动作**当前生效的键位文本**（如 `Ctrl+P`；一个动作挂了多处绑定就用 ` / ` 连起来；
    /// 没有任何绑定返回 null）。悬停提示用它——键位的唯一起源就是这张表，
    /// 用户改了 `settings.json` 之后提示跟着变，不会留一份写死的旧键位。
    /// </summary>
    public string KeyText(KeyAction action)
    {
        var parts = new List<string>();
        foreach (var b in Bindings)
            if (b.Action == action && b.Chord.IsValid) parts.Add(b.Chord.ToString());
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

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
        KeyAction.ToolEraser => "橡皮擦",
        KeyAction.ToolPixelEraser => "像素橡皮",
        KeyAction.SplitErased => "拆开擦断的笔迹",
        KeyAction.ToolCapture => "截图",
        KeyAction.ToolMarquee => "框选",
        KeyAction.RadialPalette => "呼出盘",
        KeyAction.SelectShape => "选择方式",
        KeyAction.Undo => "撤销",
        KeyAction.Redo => "重做",
        KeyAction.Copy => "复制选中",
        KeyAction.Clear => "清空",
        KeyAction.ToggleHud => "性能面板开关",
        KeyAction.CycleWidth => "切换当前工具粗细",
        KeyAction.ToggleKeyboardMode => "批注键盘模式开关",
        KeyAction.ToggleBoard => "白板开关",
        KeyAction.Quit => "退出",
        KeyAction.SelectAll => "全选",
        KeyAction.Duplicate => "复制一份",
        KeyAction.DeleteSelected => "删除选中",
        KeyAction.CancelSelection => "取消选择",
        KeyAction.Paste => "粘贴（对象优先，否则当图）",
        KeyAction.NudgeLeft => "左移 1",
        KeyAction.NudgeUp => "上移 1",
        KeyAction.NudgeRight => "右移 1",
        KeyAction.NudgeDown => "下移 1",
        KeyAction.NudgeLeftFar => "左移 10",
        KeyAction.NudgeUpFar => "上移 10",
        KeyAction.NudgeRightFar => "右移 10",
        KeyAction.NudgeDownFar => "下移 10",
        KeyAction.ScaleUp => "放大选中",
        KeyAction.ScaleDown => "缩小选中",
        KeyAction.FlipPageUp => "上一屏",
        KeyAction.FlipPageDown => "下一屏",
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
    /// **两条作用域的边界（2026-09-19 收窄过一次）**：
    ///   · **全局**（现在是 `Ctrl+Alt+Shift+…`）只放"最最最常用"的少数几条——穿透、
    ///     退出、呼出盘。理由：全局热键是**抢别的程序的键**（注册多了还会撞车、
    ///     被微信/QQ 占掉，见《快捷键总表》第五节），老师上课时真正闭着眼睛要按的
    ///     就那么几个；
    ///   · **应用内**（批注键盘模式打开时生效，默认开）放其余全部：工具、图形、粗细、
    ///     性能面板、清空、撤销、编辑类……
    ///
    /// **键盘模式必须留在全局**，这是上面那条规则唯一的例外：它一关，键盘就还给下层程序、
    /// **应用内快捷键全部失效**，降级等于把唯一的回头路锁死。
    ///
    /// 判据写在这里的用途是"下次加键时有根尺子"，不是装饰：`--keytest` 会断言
    /// **全局里不许出现名单之外的动作**。
    /// </summary>
    /// <remarks>
    /// 2026-09-29 又砍了一次（用户定）：**只留穿透 + 退出**（当时键盘模式还在）。
    /// 2026-09-30：**键盘模式那条也暂时撤了**（用户："暂时没需求 + 防误触，默认就行"）。
    /// 2026-10-04（用户定）：**全局键统一加 `Shift`**（`Ctrl+Alt+Shift+…`，避免和别的
    /// 软件抢 `Ctrl+Alt+T` / `Ctrl+Alt+X` 这类常见组合），呼出盘也从批注内 `Ctrl+Q`
    /// **升级成全局 `Ctrl+Alt+Shift+Q`**——所以现在实际注册的全局热键是 **3 条**：
    /// 穿透、退出、呼出盘。
    /// `ToggleKeyboardMode` **仍留在白名单里**：它只是没有绑键（见 Default() 里那段注释），
    /// 将来要做「更多」抽屉里的「键盘穿透」开关、或者把 `Ctrl+Alt+Shift+K` 加回来，都还合法。
    /// </remarks>
    private static readonly KeyAction[] GlobalAllowed =
    {
        KeyAction.TogglePassThrough, KeyAction.ToggleKeyboardMode, KeyAction.RadialPalette,
        KeyAction.Quit,
    };

    /// <summary>自检用：全局作用域允许出现哪些动作（见上面的说明）。</summary>
    internal static IReadOnlyList<KeyAction> GlobalAllowedActions => GlobalAllowed;

    public static KeyMap Default()
    {
        var m = new KeyMap();
        const KeyScope G = KeyScope.Global, A = KeyScope.Annotation;

        // ---- 全局：最最常用的 3 条（见 GlobalAllowed 的说明）----
        // ⚠ 2026-10-04（用户定）：**三条全局键统一加 `Shift`**——`Ctrl+Alt+T` / `Ctrl+Alt+X`
        // 这类两修饰键组合在别的软件里也常被占/容易误触，`Ctrl+Alt+Shift+…` 是三层修饰，
        // 几乎不撞车。历史：穿透 2026-09-30 从 `Ctrl+Alt+P` 换成 `Ctrl+Alt+T`（用户报
        // "P 容易和笔的 `Ctrl+P` 撞"）；这次再整体抬一层，形状不变、只是多按一个 Shift。
        m.Add(G, KeyAction.TogglePassThrough, "Ctrl+Alt+Shift+T", "全屏批注：能画 / 不能画（穿透给下层）");
        // **键盘模式（键盘归批注层）这条全局键 2026-09-30 暂时取消**（用户："我暂时没有
        // 需求，可不可以取消这个快捷键，防止误触，然后你留好注释，默认就行"）。
        //
        // 取消的理由与现状：
        //   · 功能本身**还在**（`SetKeyboardMode` / `KeyAction.ToggleKeyboardMode` 都没删），
        //     默认就是**开**（键盘归批注、应用内快捷键生效）——正好是老师要的状态；
        //   · 唯一的代价：现在**没法把它关掉**（关掉之后键盘还给下层程序打字）。
        //     用户明确说暂时不需要，所以把这唯一的入口先撤了，顺手消掉误触（Ctrl+Alt+K 挨着
        //     Ctrl+Alt+P，课堂上容易碰）。
        //   · **以后要恢复**：把下面这行加回来即可；做抽屉开关的话见 README 的"8.1.3 清单"
        //     （命令通道 + Row + 勾选渲染 + 白名单，四件一起做才动它）。
        // m.Add(G, KeyAction.ToggleKeyboardMode, "Ctrl+Alt+Shift+K", "键盘归批注层（编辑与工具快捷键生效）");
        m.Add(G, KeyAction.Quit, "Ctrl+Alt+Shift+X", "退出");
        // 呼出盘（用户 2026-09-30 定 `Ctrl+Q`、2026-10-04 升级为全局 `Ctrl+Alt+Shift+Q`）：
        // 按住 → 光标处出八扇区 → 划向扇区 → 松手。扇区 = 笔 / 黑 / 红 / 蓝 / 荧光笔 /
        // 橡皮 / 框选 / 激光（黑红蓝 = 色带前三）。用户原话："把轮盘的 Ctrl+Q 也变成
        // 全局快捷键，即变成 Ctrl+Alt+Shift+Q，这样好一些"。
        // 设计稿、理论、和其它快捷键的对应关系见《调研-笔键方案.md》附录 C/D；
        // 行为要点：**穿透里照样能呼出**（选扇区 = 退出穿透 + 换工具，与工具键那条
        // "穿透下不响应"刻意相反——它是把笔从下层抢回来的入口）、前台是 PPT/WPS 也照样出盘、
        // 扇区里全是现有命令（工具 = 按 Ctrl+P/I/L/E/M 同一条路，颜色 = "给我这支颜色的笔"）。
        // ⚠ 全局热键只送按下（WM_HOTKEY）、没有松手消息，松手靠 PumpRadialPalette 轮询。
        m.Add(G, KeyAction.RadialPalette, "Ctrl+Alt+Shift+Q",
              "呼出盘：按住 → 划向扇区 → 松手（笔/黑/红/蓝/荧光笔/橡皮/框选/激光）");

        // ---- 应用内：原有那一批（编辑类 + 翻页 + 微调）----
        m.Add(A, KeyAction.Undo, "Ctrl+Z", "撤销一步");
        m.Add(A, KeyAction.Redo, "Ctrl+Y", "重做");
        // 重做别名（2026-10-05 用户定）：很多软件里 `Ctrl+Shift+Z` 也是重做，零风险加一条；
        // 和 `Ctrl+Y` **同一条动作**（`KeyText` 会自动拼成 "Ctrl+Y / Ctrl+Shift+Z" 给提示用）。
        m.Add(A, KeyAction.Redo, "Ctrl+Shift+Z", "重做（别名）");
        m.Add(A, KeyAction.Copy, "Ctrl+C", "复制选中对象（粘回来仍是可编辑对象，同时给外部程序一张图）");
        m.Add(A, KeyAction.SelectAll, "Ctrl+A", "全选");
        m.Add(A, KeyAction.Duplicate, "Ctrl+D", "复制一份");
        m.Add(A, KeyAction.DeleteSelected, "Delete", "删除选中");
        m.Add(A, KeyAction.CancelSelection, "Esc", "取消选择");
        m.Add(A, KeyAction.Paste, "Ctrl+V", "粘贴：优先粘回可编辑对象，否则把图粘到左上角");
        m.Add(A, KeyAction.NudgeLeft, "Left", "左移 1 像素");
        m.Add(A, KeyAction.NudgeUp, "Up", "上移 1 像素");
        m.Add(A, KeyAction.NudgeRight, "Right", "右移 1 像素");
        m.Add(A, KeyAction.NudgeDown, "Down", "下移 1 像素");
        m.Add(A, KeyAction.NudgeLeftFar, "Shift+Left", "左移 10 像素");
        m.Add(A, KeyAction.NudgeUpFar, "Shift+Up", "上移 10 像素");
        m.Add(A, KeyAction.NudgeRightFar, "Shift+Right", "右移 10 像素");
        m.Add(A, KeyAction.NudgeDownFar, "Shift+Down", "下移 10 像素");
        // 选中对象的键盘缩放（2026-10-05 用户定）：以**选区框中心**为锚（和双指捏合同一个取点），
        // 一下 10%；连续按合并成一步撤销（见 Engine 的"连续微调会话"）。
        // 主键是 OEM 的 `=` / `-`：解析器认识 `Ctrl+=` / `Ctrl+-` 这种写法（KeyChord.TryParseVk）。
        m.Add(A, KeyAction.ScaleUp, "Ctrl+=", "选中对象放大 10%（以选区中心为锚）");
        m.Add(A, KeyAction.ScaleDown, "Ctrl+-", "选中对象缩小 10%（以选区中心为锚）");

        // 白板翻页。**故意只放批注内**：注册成全局热键会把 PPT / PDF / 浏览器
        // 的 PageUp / PageDown 全抢走——那正是"绝不能全局注册编辑类键"的同一条理由。
        // 教室里没键盘的老师走面板上带那两个按钮（见 InkUi.FullUi）。
        m.Add(A, KeyAction.FlipPageUp, "PageUp", "白板翻到上一屏（已经在最上面就不动）");
        m.Add(A, KeyAction.FlipPageDown, "PageDown", "白板翻到下一屏（下面永远还有一屏空白）");

        // ---- 应用内：2026-09-19 从全局**降下来**的那一批 ----
        //
        // 键位规则只有一条：**去掉 Alt 那一层**（全局 `Ctrl+Alt+2` → 应用内 `Ctrl+2`）。
        // 好处是肌肉记忆和《快捷键总表》里那几行都不用重新学，一句"降了一档"就说得清；
        // Ctrl+数字本来就是应用内最顺手的一档（和 Ctrl+Z / Ctrl+C 那一套同一个手感）。
        // 两个例外：
        //   · **清空**用 `Ctrl+Shift+C`——`Ctrl+C` 是"复制选中"，清空占它就太危险了；
        //   · **截图**用 `Ctrl+S`（原来全局是 Ctrl+Alt+S），不做成 Ctrl+Shift+S 是因为
        //     S 这一档在应用内空着，按一下最快。
        //
        // 图形（直线/矩形/椭圆/圆/三角形/平行四边形/箭头/坐标系）**一个键都没有**
        // （用户 2026-09-19 定："图形不需要加快捷键，通通取消掉"）：它们的入口就是
        // 主条「图形」那一格的上带——点一下就是换一种，比记八个 `Ctrl+Alt+?` 快。
        // ---- 五个主工具键：对齐 PowerPoint 放映那一套（用户 2026-09-29 定）----
        //
        // 为什么是 PPT 那套：Ctrl+P / Ctrl+I / Ctrl+E / Ctrl+L 正好是 PowerPoint 放映里的
        // 笔 / 荧光笔 / 橡皮 / 激光笔，老师换软件不用重新学；Ctrl+M = 选中是我们自己补的一格。
        //
        // **2026-09-30 收口为"只留单击"**（双击轮换与按住回第一色那两套手势停用，
        // 见 commit addc4ce 与《调研-快捷键-焦点与穿透》）：单击 = 切到它；
        // **已经是它** → 再按只对**笔 / 荧光笔**换色（2026-10-05 用户定：
        // 橡皮整笔/面积、框选矩形/套索不再同键切换，子类型一律去面板上带里选）。
        m.Add(A, KeyAction.ToolPen, "Ctrl+P", "换成笔（已经是笔→再按换色）");
        m.Add(A, KeyAction.ToolHighlighter, "Ctrl+I", "换成荧光笔（半透明大笔；单击语义同笔）");
        m.Add(A, KeyAction.ToolLaser, "Ctrl+L", "换成激光笔（只留痕迹，不留墨）");
        m.Add(A, KeyAction.ToolEraser, "Ctrl+E", "换成橡皮擦（碰到哪一条就整条删掉；整笔/面积看上带）");
        m.Add(A, KeyAction.ToolMarquee, "Ctrl+M", "换成框选（选择/移动/缩放/旋转；矩形/套索看上带）");
        // **旧别名取消**（用户 2026-09-30："只保留一套"）：原来的 Ctrl+7（面积擦）、
        // Ctrl+9（切选择方式）删掉——那两件事现在都归"连按同一个工具键"：
        // Ctrl+E 连按切整笔/面积、Ctrl+M 连按切矩形/套索，不再占额外键位。
        m.Add(A, KeyAction.ToolCapture, "Ctrl+S", "截图：拖一个框，抓到的图放到左上角、自动选中并进剪贴板");
        // ⚠ 呼出盘 2026-10-04 从这一档**升到全局**（`Ctrl+Alt+Shift+Q`，见上面全局那一段）：
        // 它现在是全局键，这里不再留批注内的 `Ctrl+Q` 副本（留了就会一个动作两把键、
        // 又回到"冲突"的老问题）。
        m.Add(A, KeyAction.CycleWidth, "Ctrl+6", "切成当前工具的下一档粗细");
        m.Add(A, KeyAction.SplitErased, "Ctrl+8", "把选中的、被擦断的笔迹拆成独立对象（只服务老存档）");
        m.Add(A, KeyAction.Clear, "Ctrl+Shift+C", "清空整页（可撤销）");
        // 白板开关（2026-10-05 用户定）：和点面板白板格同一条命令（`SetBoardFromUi`，
        // 开板会顺手退穿透）；**只在批注内**——放映时白板归 PptBar 那套，不抢全局键。
        m.Add(A, KeyAction.ToggleBoard, "Ctrl+B", "开/关白板（和点白板格同一条命令）");
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
