using InkEngine;
using Vortice.Direct2D1;

namespace InkUi;

/// <summary>
/// 迁移期适配器：把现有 DrawMoreXxx / MoreHitAt 包成一个 IPage，不改业务绘制。
/// 新页面直接实现 IPage；老页面先用它接进 PageHost，等自检通过后再拆内容。
/// </summary>
public sealed class LegacyPage : IPage
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public PageSize PreferredSize { get; init; } = PageSize.Auto;
    public bool CanBack { get; init; } = true;
    public bool Modal { get; init; } = true;

    public Action<PageContext> Enter { get; init; }
    public Action<PageLeaveReason, PageResult> Leave { get; init; }
    public Action Pause { get; init; }
    public Action Resume { get; init; }

    public Action<PageFrame> LayoutAction { get; init; }
    public Action<ID2D1DeviceContext, PageFrame> RenderAction { get; init; }

    public Func<float, float, PageHit> HitTestFunc { get; init; }
    public Func<UiPointerEvent, bool> Down { get; init; }
    public Func<UiPointerEvent, bool> Move { get; init; }
    public Func<UiPointerEvent, bool> Up { get; init; }
    public Func<float, float, int, bool> WheelFunc { get; init; }
    public Func<int, bool, bool, bool, bool, bool> KeyFunc { get; init; }
    public Func<bool> Animating { get; init; }

    public void OnEnter(PageContext ctx) => Enter?.Invoke(ctx);
    public void OnLeave(PageLeaveReason reason, PageResult result) => Leave?.Invoke(reason, result);
    public void OnPause() => Pause?.Invoke();
    public void OnResume() => Resume?.Invoke();

    public void Layout(in PageFrame frame) => LayoutAction?.Invoke(frame);
    public void Render(ID2D1DeviceContext ctx, in PageFrame frame) => RenderAction?.Invoke(ctx, frame);

    public PageHit HitTest(float x, float y) => HitTestFunc?.Invoke(x, y) ?? PageHit.None;

    public bool PointerDown(in UiPointerEvent e) => Down?.Invoke(e) ?? false;
    public bool PointerMove(in UiPointerEvent e) => Move?.Invoke(e) ?? false;
    public bool PointerUp(in UiPointerEvent e) => Up?.Invoke(e) ?? false;
    public bool Wheel(float x, float y, int delta) => WheelFunc?.Invoke(x, y, delta) ?? false;
    public bool Key(int vk, bool ctrl, bool shift, bool alt, bool repeat)
        => KeyFunc?.Invoke(vk, ctrl, shift, alt, repeat) ?? false;

    public bool IsAnimating => Animating?.Invoke() ?? false;
}
