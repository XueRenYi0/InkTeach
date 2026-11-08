using InkEngine;

namespace InkUi;

/// <summary>页面想要的尺寸。Width/Height ≤ 0 表示“用宿主的默认值”。</summary>
public readonly record struct PageSize(float Width, float Height)
{
    public static readonly PageSize Auto = new(0f, 0f);
}

/// <summary>
/// 页面框架：把一块面板拆成标题/内容/底栏/滚动视口/返回/关闭。
/// 页面只在这个框里画内容，不再自己算页高、标题栏、遮罩。
/// </summary>
public readonly record struct PageFrame(
    RectF Root,
    RectF Header,
    RectF Content,
    RectF Footer,
    RectF ScrollViewport,
    RectF Back,
    RectF Close,
    bool BackVisible,
    bool CloseVisible)
{
    public float Width => Root.MaxX - Root.MinX;
    public float Height => Root.MaxY - Root.MinY;

    public static readonly PageFrame Empty = new(
        RectF.Empty, RectF.Empty, RectF.Empty, RectF.Empty,
        RectF.Empty, RectF.Empty, RectF.Empty, false, false);
}

public enum PageLeaveReason
{
    Back = 0,
    Close = 1,
    Replace = 2,
    PopToRoot = 3,
    AppClose = 4,
}

/// <summary>页面关闭时带回的结果（确认/选择项）。</summary>
public sealed class PageResult
{
    public static readonly PageResult None = new(null);

    public PageResult(object value) => Value = value;

    public object Value { get; }
}

public enum PageHitKind
{
    None = 0,
    Content,
    Back,
    Close,
    Action,
}

/// <summary>页面命中结果：用稳定 ActionId 表达“点到了什么”。</summary>
public readonly record struct PageHit(PageHitKind Kind, string ActionId = "", int Index = -1)
{
    public static readonly PageHit None = new(PageHitKind.None);
}

/// <summary>页面进入时拿到的上下文。PageHost 是页面栈；Host 可为 null（自检）。</summary>
public sealed class PageContext
{
    public IUiHost Host { get; init; }
    public PageHost Pages { get; init; }
    public RectF WorkArea { get; init; }
    public float DpiScale { get; init; } = 1f;
    public double NowMs { get; init; }
}
