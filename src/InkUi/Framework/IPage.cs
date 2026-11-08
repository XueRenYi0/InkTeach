using InkEngine;
using Vortice.Direct2D1;

namespace InkUi;

/// <summary>
/// 一个覆盖层页面。只写自己的内容；标题栏/返回/关闭/遮罩/高度/滚动/焦点由 PageHost 统一。
/// 页面不直接碰引擎文档；要改状态走 PageContext.Host.Commands。
/// </summary>
public interface IPage
{
    string Id { get; }
    string Title { get; }

    /// <summary>页面想要的尺寸；宿主会夹进工作区。</summary>
    PageSize PreferredSize { get; }

    bool CanBack { get; }
    bool Modal { get; }

    void OnEnter(PageContext ctx);
    void OnLeave(PageLeaveReason reason, PageResult result);
    void OnPause();
    void OnResume();

    void Layout(in PageFrame frame);
    void Render(ID2D1DeviceContext ctx, in PageFrame frame);

    PageHit HitTest(float x, float y);

    bool PointerDown(in UiPointerEvent e);
    bool PointerMove(in UiPointerEvent e);
    bool PointerUp(in UiPointerEvent e);
    bool Wheel(float x, float y, int delta);
    bool Key(int vk, bool ctrl, bool shift, bool alt, bool repeat);

    bool IsAnimating { get; }
}
