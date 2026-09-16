using InkEngine;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 无界面宿主：不画任何东西、不接受任何点击。
///
/// 它存在的唯一理由是**把引擎的界面接口接上**——引擎要求接入一个
/// <see cref="IOverlayUi"/>，这里就给一个最小实现，让引擎跑纯笔迹模式。
///
/// 这样做的另一个好处是：引擎里"界面可以为空"这条设计被真实地跑到了，
/// 而不是停在纸面上。
/// </summary>
internal sealed class HeadlessUi : IOverlayUi
{
    public string Name => "无界面";
    public bool Visible => false;
    public bool IsAnimating => false;
    public void Attach(IUiHost host) { }
    public RectF Layout(RectF screen, float dpiScale) => RectF.Empty;
    public RectF QueryBounds() => RectF.Empty;
    public void Render(ID2D1DeviceContext ctx, UiTheme theme) { }
    public bool PointerDown(in UiPointerEvent e) => false;
    public bool PointerMove(in UiPointerEvent e) => false;
    public bool PointerUp(in UiPointerEvent e) => false;
    public void OnStateChanged(in UiState state) { }
}
