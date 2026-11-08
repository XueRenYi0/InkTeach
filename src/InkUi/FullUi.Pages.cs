using InkEngine;
using Vortice.Direct2D1;

namespace InkUi;

public sealed partial class FullUi
{
    /// <summary>
    /// A3：把更多/设置/帮助/日志/AI 服务做成有名字的 IPage 类型。
    /// 这一版仍复用 FullUi 现有 Draw/Hit（不在一次改动里搬几千行绘制），
    /// 但 PageHost 栈里已经是具体页面，不再是一袋匿名回调；下一步再逐页搬内容。
    /// </summary>
    private IPage CreatePage(MorePage page) => page switch
    {
        MorePage.Settings => new SettingsPage(this),
        MorePage.Help => new HelpPage(this),
        MorePage.Log => new LogPage(this),
        MorePage.Api => new ApiPage(this),
        _ => new MoreHomePage(this),
    };

    private abstract class MorePageBase : IPage
    {
        protected readonly FullUi Ui;
        private readonly PageSize _size;

        protected MorePageBase(FullUi ui, string id, string title, PageSize size, bool canBack)
        {
            Ui = ui;
            Id = id;
            Title = title;
            _size = size;
            CanBack = canBack;
        }

        public string Id { get; }
        public string Title { get; }
        public PageSize PreferredSize => _size;
        public bool CanBack { get; }
        public bool Modal => true;
        public bool IsAnimating => false;

        public virtual void OnEnter(PageContext ctx) { }
        public virtual void OnLeave(PageLeaveReason reason, PageResult result) { }
        public virtual void OnPause() { }
        public virtual void OnResume() { }
        public void Layout(in PageFrame frame) { }
        public abstract void Render(ID2D1DeviceContext ctx, in PageFrame frame);

        public PageHit HitTest(float x, float y)
        {
            var t = Ui.MoreHitTargetAt(x, y);
            return t.IsNone ? PageHit.None : new PageHit(PageHitKind.Action, t.ActionId, t.Index);
        }

        public bool PointerDown(in UiPointerEvent e) => false;
        public bool PointerMove(in UiPointerEvent e) => false;
        public bool PointerUp(in UiPointerEvent e) => false;
        public bool Wheel(float x, float y, int delta) => false;
        public bool Key(int vk, bool ctrl, bool shift, bool alt, bool repeat) => false;
    }

    private sealed class MoreHomePage : MorePageBase
    {
        public MoreHomePage(FullUi ui)
            : base(ui, "more.home", "更多", new PageSize(0f, ui.MoreHomeH()), canBack: false) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
            => Ui.DrawMoreHome(ctx);
    }

    private sealed class SettingsPage : MorePageBase
    {
        public SettingsPage(FullUi ui)
            : base(ui, "more.settings", "设置", new PageSize(0f, ui.MoreSettingsH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
            => Ui.DrawMoreSettings(ctx);
    }

    private sealed class HelpPage : MorePageBase
    {
        public HelpPage(FullUi ui)
            : base(ui, "more.help", "使用说明", new PageSize(0f, ui.MoreHelpH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
            => Ui.DrawHelpPage(ctx);
    }

    private sealed class LogPage : MorePageBase
    {
        public LogPage(FullUi ui)
            : base(ui, "more.log", "日志与反馈", new PageSize(0f, ui.MoreLogH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
            => Ui.DrawLogPage(ctx);
    }

    private sealed class ApiPage : MorePageBase
    {
        public ApiPage(FullUi ui)
            : base(ui, "more.api", "AI 服务", new PageSize(0f, ui.MoreApiH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
            => Ui.DrawApiPage(ctx);
    }
}
