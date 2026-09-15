using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace UiMock;

/// <summary>
/// 假面板本体：一个只画三带面板的元素。
/// 它不接引擎、不认识文档、不画笔迹 —— 只负责"看起来对不对、点起来顺不顺"。
/// </summary>
internal sealed class PanelElement : FrameworkElement
{
    public PanelState State { get; } = new();

    public event Action<PanelDraw.Layout> LayoutChanged;
    public event Action MovedByUser;
    public event Action<string> Status;

    readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// 一条按时间走的动画（不是"每帧逼近一点"）。
    /// 时长和曲线按 Windows 的动效规范：Direct Entrance 167ms、曲线约等于 cubic-bezier(0,0,0,1)；
    /// 纯透明度过渡用 83ms 线性（Bare Minimum）。
    /// </summary>
    sealed class Anim
    {
        double _from, _to, _t0, _dur;
        bool _running;
        public double Value { get; private set; }
        public Anim(double v) { Value = v; _from = _to = v; }
        public void To(double target, double durMs, double now)
        {
            if (_running && Math.Abs(_to - target) < 1e-6) return;
            if (!_running && Math.Abs(Value - target) < 1e-6) return;
            _from = Value; _to = target; _t0 = now; _dur = Math.Max(1, durMs); _running = true;
        }
        public bool Step(double now, bool linear = false)
        {
            if (!_running) return false;
            double u = Math.Min(1, (now - _t0) / _dur);
            double k = linear ? u : 1 - Math.Pow(1 - u, 3);
            Value = _from + (_to - _from) * k;
            if (u >= 1) _running = false;
            return true;
        }
    }

    readonly Anim _e = new(1), _rail = new(0), _groove = new(0), _fade = new(1);
    int _pendingTool = -1;
    bool _dragWindow, _dragSlider;
    Point _downAt, _grabScreen;

    // 悬停意图：路过不算数。指针要在热区里停一小会儿，才真的展开。
    bool _railWant, _grooveWant, _railPinned, _groovePinned;
    double _railEnterAt = -1, _railExitAt = -1, _grooveEnterAt = -1, _grooveExitAt = -1;
    bool _holdAction;
    double _lastTick;

    double Now => _clock.Elapsed.TotalMilliseconds;

    public PanelElement()
    {
        _timer.Tick += (_, __) => Step();
        Loaded += (_, __) => { _timer.Start(); InvalidateMeasure(); };
        MouseMove += OnMove;
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        MouseLeave += OnLeave;
        Cursor = Cursors.Arrow;
    }

    // ---- 动画 -----------------------------------------------------------

    void Step()
    {
        double now = Now;
        double dt = _lastTick <= 0 ? 16 : Math.Min(64, now - _lastTick);
        _lastTick = now;
        bool changed = _e.Step(now) | _rail.Step(now) | _groove.Step(now) | _fade.Step(now, linear: true);
        changed |= ApplyDwell(now);
        changed |= StepActions(dt);
        State.E = _e.Value;
        State.Rail = _rail.Value;
        State.Groove = _groove.Value;
        State.ContentFade = _fade.Value;

        // 淡出到底 → 换工具 → 淡回来（内容"连着变"，不是"啪"地换）
        if (_pendingTool >= 0 && _fade.Value <= 0.03)
        {
            State.Tool = _pendingTool;
            _pendingTool = -1;
            _fade.To(1, 83, now);
            changed = true;
        }
        if (!changed) return;
        InvalidateMeasure();
        InvalidateVisual();
        LayoutChanged?.Invoke(PanelDraw.Compute(State));
    }

    /// <summary>
    /// 动作按钮：普通动作点一下就执行；"按住式"（清空）要按满 0.8 秒，走满才执行、松手即取消。
    /// </summary>
    bool StepActions(double dt)
    {
        bool changed = false;
        var spec = PanelDraw.SpecOf(State);

        if (_holdAction && spec.ActionHoldMs > 0)
        {
            State.ClearHold += dt / spec.ActionHoldMs;
            if (State.ClearHold >= 1)
            {
                State.ClearHold = 0;
                _holdAction = false;
                FireAction(spec);
            }
            changed = true;
        }
        else if (State.ClearHold > 0)
        {
            State.ClearHold = Math.Max(0, State.ClearHold - dt / 140);   // 松手/移开：140ms 弹回去
            changed = true;
        }

        if (State.ActionFlash > 0)
        {
            State.ActionFlash = Math.Max(0, State.ActionFlash - dt / 520);
            changed = true;
        }
        if (State.ClearFlash > 0)
        {
            State.ClearFlash = Math.Max(0, State.ClearFlash - dt / 620);
            changed = true;
        }
        return changed;
    }

    void FireAction(PanelDraw.StripSpec spec)
    {
        switch (spec.ActionId)
        {
            case PanelDraw.ActionClear:
                State.ActionFlash = 1;
                State.ClearFlash = 1;
                Status?.Invoke("清空：整块画布擦干净 —— 可撤销（后撤 / Ctrl+Z 能回来）");
                break;
            case PanelDraw.ActionSelectAll:
                State.ActionFlash = 1;
                Status?.Invoke("全选：选中画布上所有对象（引擎里就是 Ctrl+A）");
                break;
        }
    }

    /// <summary>点抽屉里的一格。kind：0 应用（更新/重启/退出）、1 深色、2 贴边隐藏、3 装饰带、-1 即将加入。</summary>
    void FireMoreItem(int group, int index)
    {
        var item = PanelDraw.Drawer[group].Items[index];
        switch (item.Kind)
        {
            case 0:
                if (item.Label == "退出") { Status?.Invoke("退出 —— 关掉面板"); Application.Current.Shutdown(); return; }
                Status?.Invoke(item.Label + "：界面占位（引擎里还没有这件事，先放在这里）");
                break;
            case 1: ToggleDark(); break;
            case 2: State.AutoHide = !State.AutoHide; Status?.Invoke("贴边隐藏：" + (State.AutoHide ? "开" : "关")); InvalidateVisual(); break;
            case 3: State.ShowDeco = !State.ShowDeco; Status?.Invoke("装饰带：" + (State.ShowDeco ? "显示" : "隐藏")); InvalidateVisual(); break;
            default: Status?.Invoke(item.Label + "：学科工具，即将加入"); break;
        }
    }

    /// <summary>
    /// 悬停意图：指针进入热区要停 120ms（上带）/ 90ms（下带）才展开；
    /// 离开后要过 220ms / 200ms 才收回。**路过不再改变任何东西** —— 这是"划过就变大小"的根治办法。
    /// </summary>
    bool ApplyDwell(double now)
    {
        bool changed = false;

        if (!_railPinned)
        {
            if (_railWant)
            {
                _railExitAt = -1;
                if (_rail.Value < 0.5)
                {
                    if (_railEnterAt < 0) _railEnterAt = now;
                    else if (now - _railEnterAt >= 120) { _rail.To(1, 167, now); _railEnterAt = -1; changed = true; }
                }
                else _railEnterAt = -1;
            }
            else
            {
                _railEnterAt = -1;
                if (_rail.Value > 0.5)
                {
                    if (_railExitAt < 0) _railExitAt = now;
                    else if (now - _railExitAt >= 220) { _rail.To(0, 167, now); _railExitAt = -1; changed = true; }
                }
                else _railExitAt = -1;
            }
        }

        if (!_groovePinned && !_dragSlider)
        {
            if (_grooveWant)
            {
                _grooveExitAt = -1;
                if (_groove.Value < 0.5)
                {
                    if (_grooveEnterAt < 0) _grooveEnterAt = now;
                    else if (now - _grooveEnterAt >= 90) { _groove.To(1, 167, now); _grooveEnterAt = -1; changed = true; }
                }
                else _grooveEnterAt = -1;
            }
            else
            {
                _grooveEnterAt = -1;
                if (_groove.Value > 0.5)
                {
                    if (_grooveExitAt < 0) _grooveExitAt = now;
                    else if (now - _grooveExitAt >= 200) { _groove.To(0, 167, now); _grooveExitAt = -1; changed = true; }
                }
                else _grooveExitAt = -1;
            }
        }
        return changed;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var L = PanelDraw.Compute(State);
        // 抽屉在面板上方（OriginY）；瘦身档在面板下方还有一条透明命中带
        return new Size(L.ContentW, L.OriginY + L.H + L.HitPadBottom);
    }

    protected override void OnRender(DrawingContext dc) => PanelDraw.Draw(dc, State);

    // ---- 外部命令（控制台热键） -----------------------------------------

    public void ToggleExpand()
    {
        if (State.E > 0.5)
        {
            _e.To(0, 167, Now);
            _rail.To(0, 167, Now);
            _groove.To(0, 167, Now);
            State.HoverTile = -1;
        }
        else _e.To(1, 167, Now);
        InvalidateVisual();
    }

    public void ToggleRail()
    {
        // 键盘是"明确的操作"：不走路过的等待，直接展开/收起，并钉住（否则会被悬停逻辑抢回去）
        bool open = _rail.Value < 0.5;
        _railPinned = open;
        _railWant = false;
        _rail.To(open ? 1 : 0, 167, Now);
        Status?.Invoke(open ? "上带：钉住展开（再按 R 收起）" : "上带：收回成一条色线");
    }

    public void ToggleGroove()
    {
        bool open = _groove.Value < 0.5;
        _groovePinned = open;
        _grooveWant = false;
        _groove.To(open ? 1 : 0, 167, Now);
        Status?.Invoke(open ? "滑条：成形（可以拖）" : "滑条：收回");
    }

    public void SetTool(int i)
    {
        if (i < 0 || i >= PanelDraw.Tools.Length) return;

        // 「更多」是入口：开合抽屉，不改当前工具
        if (i == PanelDraw.BoardTool)
        {
            // 白板是"画布开关"：点了不改当前工具，只切底色
            State.BoardOn = !State.BoardOn;
            if (State.BoardOn)
            {
                State.PassThrough = false;   // 开着白板还穿透的话，点下去打到的是看不见的窗口
                Status?.Invoke($"白板：开（{PanelDraw.BoardColors[State.BoardColor].Name}）—— 桌面被盖住，笔迹还在；穿透已自动关掉");
            }
            else Status?.Invoke("白板：关 —— 回到透明批注");
            _railPinned = true;
            _rail.To(1, 167, Now);           // 顺便把板色那一行露出来
            InvalidateVisual();
            LayoutChanged?.Invoke(PanelDraw.Compute(State));
            return;
        }

        if (PanelDraw.IsEntry(i))
        {
            State.MoreOpen = !State.MoreOpen;
            State.MoreHover = -1;
            if (State.MoreOpen) { _railPinned = false; _railWant = false; _rail.To(0, 167, Now); }
            InvalidateMeasure();
            InvalidateVisual();
            LayoutChanged?.Invoke(PanelDraw.Compute(State));
            Status?.Invoke(State.MoreOpen ? "更多：抽屉打开（更新 / 重启 / 退出 / 界面开关 / 学科工具占位）" : "更多：抽屉收起");
            return;
        }
        if (State.MoreOpen)   // 换工具就收起抽屉
        {
            State.MoreOpen = false;
            InvalidateMeasure();
            LayoutChanged?.Invoke(PanelDraw.Compute(State));
        }
        if (PanelDraw.IsAction(i))
        {
            Status?.Invoke(i == 8 ? "后撤（动作，不改上下文）" : "重做（动作，不改上下文）");
            return;
        }

        // 再点一次"当前工具" = 开合它自己的设置条（用户提的用法，也是各家软件的通例）
        if (i == State.Tool && _pendingTool < 0)
        {
            bool open = _rail.Value < 0.5;
            _railPinned = open;
            _railWant = false;
            _rail.To(open ? 1 : 0, 167, Now);
            Status?.Invoke(open
                ? $"{PanelDraw.Tools[i].Name}：展开它的设置条（再点一次收起）"
                : $"{PanelDraw.Tools[i].Name}：收起设置条（再点一次展开）");
            InvalidateVisual();
            return;
        }

        _pendingTool = i;
        _fade.To(0, 83, Now);     // 先淡出，淡到底再换内容
        _railPinned = true;       // 点了工具就把它自己的设置条留在那儿（不然 220ms 后就收了，选不了选项）
        _rail.To(1, 167, Now);
        var spec = PanelDraw.SpecOf(new PanelState { Tool = i });
        string lower = spec.HasSlider ? spec.SliderHint : "装饰线（这个工具没有可调的）";
        Status?.Invoke($"工具：{PanelDraw.Tools[i].Name} —— 上带是「{KindName(spec.Kind)}」，下带是「{lower}」");
        InvalidateVisual();
    }

    static string KindName(PanelDraw.StripKind k) => k switch
    {
        PanelDraw.StripKind.Colors => "12 色片",
        PanelDraw.StripKind.Segments => "分段选择",
        PanelDraw.StripKind.Shapes => "图形选择",
        PanelDraw.StripKind.Toggles => "开关",
        _ => "无",
    };

    public void ApplyItem(int i)
    {
        var spec = PanelDraw.SpecOf(State);
        if (spec.Decorative) return;   // 装饰带不可点
        switch (spec.Kind)
        {
            case PanelDraw.StripKind.Colors:
                State.Color = PanelDraw.ColorIndexOf(State, spec, i);
                Status?.Invoke("笔色：" + PanelDraw.PenName[State.Color]);
                break;
            case PanelDraw.StripKind.Segments:
                ApplySegment(i);
                break;
            case PanelDraw.StripKind.Shapes:
                State.ShapeKind = i;
                Status?.Invoke("图形：" + spec.Labels[i]);
                break;
            case PanelDraw.StripKind.Toggles:
                ApplyToggle(i);
                break;
        }
        InvalidateVisual();
    }

    void ApplySegment(int i)
    {
        switch (State.Tool)
        {
            case PanelDraw.ToolMouse:
                State.PassThrough = i == 1;
                Status?.Invoke(State.PassThrough ? "鼠标：穿透点击（引擎里就是 SetPassThrough）" : "鼠标：直接操作");
                break;
            case PanelDraw.ToolLaser:
                State.LaserSize = i;
                Status?.Invoke("激光笔光点：" + new[] { "小", "中", "大" }[i]);
                break;
            case PanelDraw.ToolEraser:
                State.EraserMode = i;
                Status?.Invoke(i == 0 ? "橡皮：整笔擦（引擎里是 EraseAt：碰到哪条删哪条）" : "橡皮：面积擦（引擎里是像素橡皮，一笔切成两段）");
                break;
            case PanelDraw.ToolSelect:
                State.SelectMode = i;
                Status?.Invoke(i == 0 ? "选择：矩形框选（碰到就选）" : "选择：自由套索（80% 判据）");
                break;
            case PanelDraw.ToolCapture:
                State.CaptureHideInk = i == 1;
                Status?.Invoke(i == 0 ? "截屏：直接截取（含批注）" : "截屏：隐藏批注截取（先把自己的覆盖层藏起来）");
                break;
            case PanelDraw.BoardTool:
                State.BoardColor = i;
                State.BoardOn = true;      // 选了板色 = 想用这块板
                Status?.Invoke("板色：" + PanelDraw.BoardColors[i].Name);
                break;
            default:
                Status?.Invoke("选项：" + PanelDraw.SpecOf(State).Labels[i]);
                break;
        }
    }

    void ApplyToggle(int i)
    {
        switch (i)
        {
            case 0: State.ShowDeco = !State.ShowDeco; Status?.Invoke("装饰带：" + (State.ShowDeco ? "显示" : "隐藏")); break;
            case 1: State.AutoHide = !State.AutoHide; Status?.Invoke("贴边隐藏：" + (State.AutoHide ? "开" : "关")); break;
            case 2: State.Dark = !State.Dark; Status?.Invoke(State.Dark ? "深色主题" : "浅色主题"); break;
        }
    }

    public void SetColor(int i) => ApplyItem(i);

    public void NudgeSlider(double delta)
    {
        State.Slider01 = PanelDraw.Clamp01(State.Slider01 + delta);
        _groovePinned = true;
        _groove.To(1, 167, Now);
        InvalidateVisual();
        var spec = PanelDraw.SpecOf(State);
        Status?.Invoke($"{spec.SliderHint}：{SliderValue():F1}");
    }

    double SliderValue()
    {
        if (State.Tool == PanelDraw.ToolEraser) return 8 + State.Slider01 * 56;      // 橡皮 8～64
        if (State.Tool == PanelDraw.ToolLaser) return 4 + State.Slider01 * 20;      // 激光 4～24
        if (State.Tool == PanelDraw.ToolHighlighter) return 16 + State.Slider01 * 48;     // 荧光笔 16～64
        return 1.5 + State.Slider01 * 38.5;                       // 笔 1.5～40
    }

    public void CycleIconScale()
    {
        State.IconScale = (State.IconScale + 1) % PanelDraw.Scales.Length;
        var sc = PanelDraw.Scales[State.IconScale];
        string[] names = { "任务栏档", "前几轮的现状", "更大一档", "小图标档" };
        Status?.Invoke($"图标搭配：{names[State.IconScale]} —— 按钮 {sc.Btn:F0} / 图标 {sc.Icon:F0}（面板总高 {PanelDraw.Compute(State).H:F0}）");
        InvalidateMeasure();
        InvalidateVisual();
        LayoutChanged?.Invoke(PanelDraw.Compute(State));
    }

    /// <summary>现状（平时 80 / 展开 108）与瘦身档（平时 60 / 展开 84）之间切换。</summary>
    public void ToggleMini()
    {
        State.Mini = !State.Mini;
        // 切过去以后，如果当前工具不在这一档里，就落到"笔"
        bool visible = Array.IndexOf(PanelDraw.VisibleTools(State), State.Tool) >= 0;
        if (!visible) State.Tool = PanelDraw.ToolPen;
        State.MoreOpen = false;
        State.HoverTile = -1;
        InvalidateMeasure();
        InvalidateVisual();
        LayoutChanged?.Invoke(PanelDraw.Compute(State));
        var L = PanelDraw.Compute(State);
        Status?.Invoke(State.Mini
            ? $"极简档：主条只钉「笔 / 橡皮 / 更多」，宽 {L.W:F0}（完整档是 {PanelDraw.BarContentWidth(PanelDraw.Scales[State.IconScale].Btn):F0}）"
            : $"完整档：{PanelDraw.VisibleTools(State).Length} 项，宽 {L.W:F0}");
    }

    public void ToggleSlim()
    {
        State.Slim = !State.Slim;
        var L0 = PanelDraw.Compute(new PanelState { E = 1, Rail = 0, Tool = 1, Slim = false });
        var L1 = PanelDraw.Compute(new PanelState { E = 1, Rail = 1, Tool = 1, Slim = false });
        var S0 = PanelDraw.Compute(new PanelState { E = 1, Rail = 0, Tool = 1, Slim = true });
        var S1 = PanelDraw.Compute(new PanelState { E = 1, Rail = 1, Tool = 1, Slim = true });
        Status?.Invoke(State.Slim
            ? $"瘦身档：平时 {S0.H:F0}（原 {L0.H:F0}）／展开 {S1.H:F0}（原 {L1.H:F0}）—— 滑条嵌进按钮带下沿"
            : $"现状：平时 {L0.H:F0} ／展开 {L1.H:F0}");
        InvalidateMeasure();
        InvalidateVisual();
        LayoutChanged?.Invoke(PanelDraw.Compute(State));
    }

    public void ToggleDark()
    {
        State.Dark = !State.Dark;
        InvalidateVisual();
        Status?.Invoke(State.Dark
            ? "深色主题 —— 注意黑块已提亮一档、并带了浅色描边"
            : "浅色主题 —— 注意白块已退一档、并带了深色描边");
    }

    public void CycleLaser()
    {
        State.LaserStyle = (State.LaserStyle + 1) % 9;
        InvalidateVisual();
        string[] n =
        {
            "Material 专名 stylus_laser_pointer", "Material 专名（实心）", "自绘：笔＋光束＋落点",
            "自绘：光束锥", "Fluent：Flash 闪电", "Fluent：Record 圆点",
            "Fluent：Target 靶心", "自绘：光点＋短射线", "自绘（第一版）：光点＋三道弧",
        };
        Status?.Invoke("激光笔图标：" + n[State.LaserStyle]);
    }

    // ---- 指针 -----------------------------------------------------------

    void OnLeave(object s, MouseEventArgs e)
    {
        State.HoverTile = -1;
        State.ActionHoverID = -1;
        _holdAction = false;
        _railPinned = false;
        _groovePinned = false;
        if (!_dragWindow && !_dragSlider)
        {
            _railWant = false;
            _grooveWant = false;
        }
        InvalidateVisual();
    }

    void OnMove(object s, MouseEventArgs e)
    {
        var p = e.GetPosition(this);

        // 抽屉先判（它在面板上方，用原始坐标）
        var L0 = PanelDraw.Compute(State);
        if (State.MoreOpen)
        {
            int hov = -1;
            foreach (var it in L0.MoreItems)
                if (it.R.Contains(p)) hov = it.Group * 10 + it.Index;
            State.MoreHover = hov;
            if (hov >= 0)
            {
                Cursor = Cursors.Hand;
                InvalidateVisual();
                return;
            }
        }
        else if (State.MoreHover >= 0) State.MoreHover = -1;

        // 面板部分：换算到面板自己的坐标系（抽屉打开时面板整体下移了）
        p = new Point(p.X, p.Y - L0.OriginY);
        p = new Point(p.X - L0.OriginX, p.Y);
        if (_dragWindow) { DragWindowTo(p); e.Handled = true; return; }
        if (_dragSlider) { SetSliderFromX(p.X); e.Handled = true; return; }

        var L = PanelDraw.Compute(State);
        var spec = PanelDraw.SpecOf(State);

        // 动作按钮：鼠标停上去只做视觉反馈，**不改变任何状态**
        State.ActionHoverID = (!L.ActionRect.IsEmpty && L.ActionRect.Contains(p)) ? spec.ActionId : -1;
        if (_holdAction && !L.ActionRect.Contains(p)) _holdAction = false;   // 按着走开 = 取消

        int hover = -1;
        for (int i = 0; i < L.ToolIndices.Length && i + 1 < L.Tiles.Length; i++)
            if (L.Tiles[i + 1].Contains(p)) hover = i;

        // 上带的命中区**锚在按钮带上沿**（那个位置在屏幕上是固定的），
        // 所以条子长大长小时，鼠标相对它的位置不会变 —— 修掉"反复横跳"。
        double rowTop = L.Band;
        bool onRail = L.Band > 0.5 && p.Y <= rowTop + 8;
        // 滑条：命中区由布局给出（普通档 = 那条带子上下各借 8；瘦身档 = 面板下沿 + 透明命中带）
        bool onGroove = spec.HasSlider && p.Y >= L.SliderZoneTop && p.Y <= L.SliderZoneBottom;

        State.HoverTile = hover;
        // 只登记"想不想"，真正开合交给 ApplyDwell（路过不算数）
        _railWant = onRail;
        _grooveWant = onGroove;
        // 鼠标自己走进热区时，解除键盘/点击留下的"钉住"
        if (onRail) _railPinned = false;
        if (onGroove) _groovePinned = false;
        Cursor = hover >= 0 || onRail ? Cursors.Hand : onGroove ? Cursors.SizeWE : Cursors.Arrow;
        InvalidateVisual();
    }

    void OnDown(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        _downAt = p;
        var L = PanelDraw.Compute(State);

        // 抽屉里的东西（原始坐标）
        if (State.MoreOpen)
            foreach (var it in L.MoreItems)
            {
                if (!it.R.Contains(p)) continue;
                FireMoreItem(it.Group, it.Index);
                e.Handled = true;
                return;
            }
        // 面板部分：换算到面板自己的坐标系
        p = new Point(p.X - L.OriginX, p.Y - L.OriginY);
        _downAt = p;

        if (L.E > 0.9)
        {
            if (L.Tiles.Length > 0 && L.Tiles[0].Contains(p)) { ToggleExpand(); e.Handled = true; return; }

            for (int i = 0; i < L.ToolIndices.Length && i + 1 < L.Tiles.Length; i++)
                if (L.Tiles[i + 1].Contains(p))
                {
                    State.PressTile = i;
                    InvalidateVisual();
                    SetTool(L.ToolIndices[i]);
                    e.Handled = true;
                    return;
                }

            // 上带里的东西：平时那 6 像素太细，命中区上下各借 8 像素
            var spec = PanelDraw.SpecOf(State);

            // 右端的"动作"按钮：全选点一下就执行；清空要按住 0.8 秒
            if (!L.ActionRect.IsEmpty && L.ActionRect.Contains(p))
            {
                if (spec.ActionHoldMs > 0)
                {
                    _holdAction = true;
                    State.ClearHold = 0;
                    CaptureMouse();
                    Status?.Invoke($"按住不放 {(int)spec.ActionHoldMs} 毫秒执行「{spec.ActionLabel}」（松手即取消）");
                }
                else
                {
                    FireAction(spec);
                }
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            double borrow = State.Rail > 0.5 ? 0 : 8;
            for (int i = 0; !spec.Decorative && i < L.Items.Length; i++)
            {
                var r = L.Items[i];
                r.Inflate(0, borrow);
                if (!r.Contains(p)) continue;
                ApplyItem(i);
                e.Handled = true;
                return;
            }

            if (spec.HasSlider && p.Y >= L.SliderZoneTop && p.Y <= L.SliderZoneBottom)
            {
                _dragSlider = true;
                CaptureMouse();
                SetSliderFromX(p.X);
                e.Handled = true;
                return;
            }
        }

        _dragWindow = true;
        _grabScreen = ScreenDiu(p);
        CaptureMouse();
    }

    void OnUp(object s, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        bool moved = Math.Abs(p.X - _downAt.X) > 4 || Math.Abs(p.Y - _downAt.Y) > 4;
        if (_dragWindow && !moved && State.E < 0.5) ToggleExpand();
        _dragWindow = false;
        _dragSlider = false;
        _holdAction = false;          // 松手：没走满就取消（进度条自己弹回去）
        State.PressTile = -1;
        InvalidateVisual();
        ReleaseMouseCapture();
    }

    void SetSliderFromX(double x)
    {
        var L = PanelDraw.Compute(State);
        const double inset = 16;
        double trackW = Math.Max(1, L.W - inset * 2);
        State.Slider01 = PanelDraw.Clamp01((x - inset) / trackW);
        _groove.To(1, 167, Now);
        InvalidateVisual();
        Status?.Invoke($"{PanelDraw.SpecOf(State).SliderHint}：{SliderValue():F1}");
    }

    /// <summary>元素坐标 → 屏幕坐标（逻辑像素）。PointToScreen 给的是物理像素，这里换算回来。</summary>
    Point ScreenDiu(Point p)
    {
        var dev = PointToScreen(p);
        double sx = 1, sy = 1;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            var m = src.CompositionTarget.TransformToDevice;
            sx = m.M11;
            sy = m.M22;
        }
        return new Point(dev.X / sx, dev.Y / sy);
    }

    void DragWindowTo(Point p)
    {
        var host = Window.GetWindow(this);
        if (host == null) return;
        var now = ScreenDiu(p);
        host.Left += now.X - _grabScreen.X;
        host.Top += now.Y - _grabScreen.Y;
        _grabScreen = now;
        MovedByUser?.Invoke();
    }
}

/// <summary>只有面板的透明悬浮窗：贴在任何 PPT 上看观感用。</summary>
internal class MockWindow : Window
{
    public readonly Canvas Root = new Canvas();
    public readonly PanelElement Panel = new PanelElement();
    double _anchorBottom;
    double _anchorLeft;

    public MockWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Content = Root;

        Root.Children.Add(Panel);
        Panel.LayoutChanged += ApplyLayout;
        Panel.MovedByUser += () =>
        {
            var L = PanelDraw.Compute(Panel.State);
            _anchorLeft = Left + L.OriginX;
            _anchorBottom = Top + L.OriginY + L.H;
        };

        Loaded += (_, __) =>
        {
            var wa = SystemParameters.WorkArea;
            _anchorLeft = wa.Left + (wa.Width - PanelDraw.BarContentWidth(PanelDraw.Scales[Panel.State.IconScale].Btn)) / 2;
            _anchorBottom = wa.Bottom - 60;
            ApplyLayout(PanelDraw.Compute(Panel.State));
            SuppressActivation();
        };
    }

    protected virtual void ApplyLayout(PanelDraw.Layout L)
    {
        Width = L.ContentW;
        Height = L.OriginY + L.H + L.HitPadBottom;
        // 锚的是"面板自己的左下角"，所以抽屉展开时窗口往左上长、面板本身不动
        Left = _anchorLeft - L.OriginX;
        Top = _anchorBottom - L.OriginY - L.H;
    }

    /// <summary>不抢焦点、不出现在 Alt-Tab 里 —— 和产品里的覆盖层一个待遇。</summary>
    void SuppressActivation()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(h, GwLExStyle).ToInt64();
        ex |= WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(h, GwLExStyle, new IntPtr(ex));
    }

    const int GwLExStyle = -20;
    const long WsExNoActivate = 0x08000000L;
    const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

/// <summary>带一张假 PPT 的窗口：不用真开 PowerPoint 就能做"抢戏测试"。</summary>
internal sealed class DemoWindow : MockWindow
{
    public DemoWindow()
    {
        Width = 1120;
        Height = 660;
        Left = 120;
        Top = 80;
        Root.Children.Insert(0, new SlideElement { Width = 1120, Height = 660, St = Panel.State });
    }

    protected override void ApplyLayout(PanelDraw.Layout L)
    {
        Canvas.SetLeft(Panel, (1120 - L.W) / 2);
        Canvas.SetTop(Panel, 600 - L.H);
    }
}
