using InkEngine;
using Vortice.Direct2D1;

namespace InkUi;

/// <summary>PageHost 的尺寸/标题栏尺度。全部是界面逻辑像素。</summary>
public sealed class PageHostMetrics
{
    public float WidthRatio { get; init; } = 0.55f;
    public float MinWidth { get; init; } = 560f;
    public float MaxWidth { get; init; } = 980f;

    public float DefaultHeight { get; init; } = 570f;
    public float MinHeight { get; init; } = 240f;
    public float MaxHeightMargin { get; init; } = 24f;

    public float Padding { get; init; } = 20f;
    public float HeaderH { get; init; } = 44f;
    public float ContentGap { get; init; } = 10f;
    public float FooterH { get; init; } = 0f;
    public float ChromeButton { get; init; } = 44f;

    public double OpenMs { get; init; } = 180d;
    public double CloseMs { get; init; } = 160d;
}

/// <summary>
/// 覆盖层页面栈：push / pop / replace / popToRoot + 页面生命周期 + 标题/内容/返回/关闭几何。
/// 不负责遮罩绘制、输入优先级、浮层层序、文字宿主；这些是 FullUi / OverlayLayerManager / TextInputService 的事。
///
/// 设计参考：
/// - Flutter Navigator（栈 + 返回结果）
/// - WPF NavigationService/Journal（回退、KeepAlive 默认关闭）
/// - Qt StackView（push/pop/replace）
/// - WinUI NavigationView（设置页固定在左/底部导航）
/// </summary>
public sealed class PageHost
{
    private readonly PageHostMetrics _metrics;
    private readonly List<IPage> _stack = new();
    private readonly List<PageContext> _contexts = new();
    private Func<double> _clock;
    private IUiHost _host;
    private RectF _workArea;
    private RectF _panel;
    private PageFrame _frame = PageFrame.Empty;
    private bool _open;
    private double _animStart;
    private double _animDuration;
    private float _animFrom;
    private float _animTo = 1f;

    public PageHost(PageHostMetrics metrics = null, Func<double> clock = null)
    {
        _metrics = metrics ?? new PageHostMetrics();
        _clock = clock ?? (() => 0d);
    }

    public PageHostMetrics Metrics => _metrics;

    /// <summary>接引擎时钟；生产里在 Attach 之后调用。自检可以直接用 clock 构造。</summary>
    public void Bind(IUiHost host)
    {
        _host = host;
        _clock = () => host.NowMs;
    }

    public int Count => _stack.Count;
    public bool IsEmpty => _stack.Count == 0;
    public bool IsOpen => _open;
    public bool CanGoBack => _open && _stack.Count > 1;
    public IPage Current => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

    public RectF PanelRect => _panel;
    public PageFrame Frame => _frame;
    public PageResult LastResult { get; private set; } = PageResult.None;

    /// <summary>打开动画进度：0 = 刚打开，1 = 完全展开。没有动画时直接返回终值。</summary>
    public float OpenT
    {
        get
        {
            if (_animDuration <= 0d) return _animTo;
            double t = (_clock() - _animStart) / _animDuration;
            if (t <= 0d) return _animFrom;
            if (t >= 1d) return _animTo;
            float u = 1f - (float)t;
            return _animFrom + (_animTo - _animFrom) * (1f - u * u * u);
        }
    }

    public bool IsAnimating
        => (_animDuration > 0d && _clock() < _animStart + _animDuration)
           || (Current?.IsAnimating ?? false);

    public void SetWorkArea(RectF workArea)
    {
        _workArea = workArea;
        Layout();
    }

    /// <summary>打开根页面。已有栈时先按 Replace 清掉（用于“重新打开更多”）。</summary>
    public void Open(IPage root, bool animate = true)
    {
        if (root == null) return;
        if (_stack.Count > 0) ClearStack(PageLeaveReason.Replace);
        _stack.Add(root);
        _contexts.Add(BuildContext());
        _open = true;
        root.OnEnter(_contexts[_contexts.Count - 1]);
        StartAnim(animate ? 0f : 1f, 1f, animate ? _metrics.OpenMs : 0d);
        Layout();
    }

    /// <summary>关闭整个页面栈。保留 Current 直到调用方下一帧收尾；这里直接清空，避免半开状态。</summary>
    public void Close(bool animate = true)
    {
        if (_stack.Count == 0)
        {
            _open = false;
            return;
        }
        IPage current = Current;
        current.OnLeave(PageLeaveReason.Close, PageResult.None);
        _stack.Clear();
        _contexts.Clear();
        _open = false;
        StartAnim(OpenT, 0f, animate ? _metrics.CloseMs : 0d);
        Layout();
    }

    /// <summary>压入一页：当前页 Pause，新页 Enter。</summary>
    public void Push(IPage page)
    {
        if (page == null) return;
        if (_stack.Count == 0)
        {
            Open(page, animate: false);
            return;
        }
        Current.OnPause();
        _stack.Add(page);
        _contexts.Add(BuildContext());
        page.OnEnter(_contexts[_contexts.Count - 1]);
        _open = true;
        if (_animDuration <= 0d) StartAnim(1f, 1f, 0d);
        Layout();
    }

    /// <summary>弹出一页：当前页 Leave(Back)，上一页 Resume。返回 false = 没有可弹的页。</summary>
    public bool Pop(PageResult result = null)
    {
        if (_stack.Count == 0) return false;
        PageResult r = result ?? PageResult.None;
        IPage current = Current;
        current.OnLeave(PageLeaveReason.Back, r);
        _stack.RemoveAt(_stack.Count - 1);
        _contexts.RemoveAt(_contexts.Count - 1);
        LastResult = r;

        if (_stack.Count > 0)
        {
            _open = true;
            Current.OnResume();
            Layout();
        }
        else
        {
            _open = false;
            StartAnim(OpenT, 0f, _metrics.CloseMs);
            Layout();
        }
        return true;
    }

    /// <summary>替换栈顶：当前页 Leave(Replace)，新页 Enter。</summary>
    public void Replace(IPage page)
    {
        if (page == null) return;
        if (_stack.Count == 0)
        {
            Open(page, animate: false);
            return;
        }
        Current.OnLeave(PageLeaveReason.Replace, PageResult.None);
        _stack.RemoveAt(_stack.Count - 1);
        _contexts.RemoveAt(_contexts.Count - 1);
        _stack.Add(page);
        _contexts.Add(BuildContext());
        page.OnEnter(_contexts[_contexts.Count - 1]);
        _open = true;
        Layout();
    }

    /// <summary>回到根页面，只保留第一页。返回关掉的页数。</summary>
    public int PopToRoot(PageResult result = null)
    {
        if (_stack.Count <= 1) return 0;
        PageResult r = result ?? PageResult.None;
        int closed = 0;
        while (_stack.Count > 1)
        {
            Current.OnLeave(PageLeaveReason.PopToRoot, r);
            _stack.RemoveAt(_stack.Count - 1);
            _contexts.RemoveAt(_contexts.Count - 1);
            closed++;
        }
        LastResult = r;
        _open = true;
        Current.OnResume();
        Layout();
        return closed;
    }

    private void ClearStack(PageLeaveReason reason)
    {
        for (int i = _stack.Count - 1; i >= 0; i--)
            _stack[i].OnLeave(reason, PageResult.None);
        _stack.Clear();
        _contexts.Clear();
    }

    /// <summary>按当前页的 PreferredSize 和工作区重算面板/标题/内容/返回/关闭。</summary>
    public void Layout()
    {
        if (_stack.Count == 0)
        {
            _panel = RectF.Empty;
            _frame = PageFrame.Empty;
            return;
        }

        float workW = MathF.Max(0f, _workArea.MaxX - _workArea.MinX);
        float workH = MathF.Max(0f, _workArea.MaxY - _workArea.MinY);
        if (workW <= 0f || workH <= 0f)
        {
            _panel = RectF.Empty;
            _frame = PageFrame.Empty;
            return;
        }

        PageSize pref = Current.PreferredSize;
        float width = _metrics.WidthRatio > 0f ? workW * _metrics.WidthRatio : workW;
        width = Math.Clamp(width, _metrics.MinWidth, _metrics.MaxWidth);
        if (pref.Width > 0f) width = MathF.Min(width, pref.Width);
        width = MathF.Min(width, workW);

        float height = pref.Height > 0f ? pref.Height : _metrics.DefaultHeight;
        height = MathF.Min(height, workH - _metrics.MaxHeightMargin);
        height = MathF.Max(height, MathF.Min(_metrics.MinHeight, workH));

        float x = _workArea.MinX + (workW - width) * 0.5f;
        float y = _workArea.MinY + (workH - height) * 0.5f;
        _panel = new RectF { MinX = x, MinY = y, MaxX = x + width, MaxY = y + height };

        float pad = _metrics.Padding;
        float headerTop = _panel.MinY + pad;
        var header = new RectF
        {
            MinX = _panel.MinX + pad,
            MinY = headerTop,
            MaxX = _panel.MaxX - pad,
            MaxY = headerTop + _metrics.HeaderH,
        };

        float footerH = _metrics.FooterH;
        var footer = footerH > 0f
            ? new RectF
            {
                MinX = header.MinX,
                MinY = _panel.MaxY - pad - footerH,
                MaxX = header.MaxX,
                MaxY = _panel.MaxY - pad,
            }
            : RectF.Empty;

        float contentBottom = (footerH > 0f ? footer.MinY : _panel.MaxY - pad) - _metrics.ContentGap;
        var content = new RectF
        {
            MinX = header.MinX,
            MinY = header.MaxY + _metrics.ContentGap,
            MaxX = header.MaxX,
            MaxY = MathF.Max(header.MaxY + _metrics.ContentGap, contentBottom),
        };

        float btn = _metrics.ChromeButton;
        var back = new RectF
        {
            MinX = header.MinX,
            MinY = header.MinY,
            MaxX = header.MinX + btn,
            MaxY = header.MinY + btn,
        };
        var close = new RectF
        {
            MinX = header.MaxX - btn,
            MinY = header.MinY,
            MaxX = header.MaxX,
            MaxY = header.MinY + btn,
        };

        _frame = new PageFrame(
            _panel, header, content, footer, content, back, close,
            Current.CanBack, true);

        Current.Layout(_frame);
    }

    public void Render(ID2D1DeviceContext ctx)
    {
        if (_stack.Count == 0) return;
        Current.Render(ctx, _frame);
    }

    public PageHit HitTest(float x, float y)
    {
        if (_stack.Count == 0 || !_open) return PageHit.None;
        if (Current.CanBack && _frame.BackVisible && _frame.Back.Contains(x, y))
            return new PageHit(PageHitKind.Back, "page.back");
        if (_frame.CloseVisible && _frame.Close.Contains(x, y))
            return new PageHit(PageHitKind.Close, "page.close");
        return Current.HitTest(x, y);
    }

    public bool PointerDown(in UiPointerEvent e) => _open && Current?.PointerDown(e) == true;
    public bool PointerMove(in UiPointerEvent e) => _open && Current?.PointerMove(e) == true;
    public bool PointerUp(in UiPointerEvent e) => _open && Current?.PointerUp(e) == true;
    public bool Wheel(float x, float y, int delta) => _open && Current?.Wheel(x, y, delta) == true;
    public bool Key(int vk, bool ctrl, bool shift, bool alt, bool repeat)
        => _open && Current?.Key(vk, ctrl, shift, alt, repeat) == true;

    public PageContext ContextForCurrent
        => _contexts.Count > 0 ? _contexts[_contexts.Count - 1] : null;

    private PageContext BuildContext() => new()
    {
        Host = _host,
        Pages = this,
        WorkArea = _workArea,
        DpiScale = _host?.DpiScale ?? 1f,
        NowMs = _clock(),
    };

    private void StartAnim(float from, float to, double durationMs)
    {
        _animFrom = from;
        _animTo = to;
        _animStart = _clock();
        _animDuration = MathF.Max(0f, (float)durationMs);
    }
}
