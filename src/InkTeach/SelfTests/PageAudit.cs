using InkEngine;
using InkUi;
using Vortice.Direct2D1;

namespace InkTeach;

/// <summary>
/// 页面栈后台自检（A1 第 1 周）：不建窗口、不抢前台。
/// 只验证 PageHost 的栈语义、生命周期、尺寸夹取、标题/内容/返回/关闭几何。
/// </summary>
internal static class PageAudit
{
    public static int Run()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        var metrics = new PageHostMetrics();
        double now = 1000d;
        var host = new PageHost(metrics, () => now);
        host.SetWorkArea(new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 800 });

        var home = new TestPage("home", "更多", new PageSize(0, 570), canBack: false);
        var settings = new TestPage("settings", "设置", new PageSize(0, 476), canBack: true);
        var api = new TestPage("api", "AI 服务", new PageSize(0, 700), canBack: true);

        host.Open(home, animate: false);
        Check("打开根页面", host.IsOpen && host.Count == 1 && ReferenceEquals(host.Current, home),
              $"open={host.IsOpen} count={host.Count} current={host.Current?.Id}");
        Check("根页面生命周期", home.EnterCount == 1 && home.PauseCount == 0 && home.ResumeCount == 0,
              $"enter={home.EnterCount} pause={home.PauseCount} resume={home.ResumeCount}");
        Check("面板尺寸 55% / 570", MathF.Abs(host.PanelRect.MaxX - host.PanelRect.MinX - 704f) < 0.5f
              && MathF.Abs(host.PanelRect.MaxY - host.PanelRect.MinY - 570f) < 0.5f,
              $"w={host.PanelRect.MaxX - host.PanelRect.MinX:F0} h={host.PanelRect.MaxY - host.PanelRect.MinY:F0}");
        Check("标题/内容几何", host.Frame.Header.MaxY - host.Frame.Header.MinY == metrics.HeaderH
              && host.Frame.Content.MaxY > host.Frame.Content.MinY
              && host.Frame.Close.Contains(host.Frame.Close.MinX + 1, host.Frame.Close.MinY + 1),
              $"header={host.Frame.Header.MinY:F0}..{host.Frame.Header.MaxY:F0} content={host.Frame.Content.MinY:F0}..{host.Frame.Content.MaxY:F0}");
        Check("根页不可返回、有关闭", !host.Frame.BackVisible && host.Frame.CloseVisible,
              $"back={host.Frame.BackVisible} close={host.Frame.CloseVisible}");

        host.Push(settings);
        Check("压入设置页", host.CanGoBack && host.Count == 2 && ReferenceEquals(host.Current, settings),
              $"count={host.Count} canBack={host.CanGoBack}");
        Check("压入生命周期", home.PauseCount == 1 && settings.EnterCount == 1,
              $"home.pause={home.PauseCount} settings.enter={settings.EnterCount}");
        Check("设置页高度 476", MathF.Abs(host.PanelRect.MaxY - host.PanelRect.MinY - 476f) < 0.5f,
              $"h={host.PanelRect.MaxY - host.PanelRect.MinY:F0}");
        var backHit = host.HitTest(host.Frame.Back.MinX + 1, host.Frame.Back.MinY + 1);
        Check("返回按钮可命中", backHit.Kind == PageHitKind.Back && backHit.ActionId == "page.back",
              $"{backHit.Kind}/{backHit.ActionId}");

        host.Push(api);
        Check("压入 AI 页", host.Count == 3 && ReferenceEquals(host.Current, api)
              && MathF.Abs(host.PanelRect.MaxY - host.PanelRect.MinY - 700f) < 0.5f,
              $"count={host.Count} h={host.PanelRect.MaxY - host.PanelRect.MinY:F0}");

        bool popped = host.Pop(new PageResult("ok"));
        Check("弹出并带回结果", popped && ReferenceEquals(host.Current, settings)
              && api.LeaveCount == 1 && api.LastLeave == PageLeaveReason.Back
              && settings.ResumeCount == 1 && Equals(host.LastResult.Value, "ok"),
              $"popped={popped} current={host.Current?.Id} leave={api.LeaveCount} resume={settings.ResumeCount}");

        host.Pop();
        Check("再弹出回根页", ReferenceEquals(host.Current, home)
              && settings.LeaveCount == 1 && settings.LastLeave == PageLeaveReason.Back
              && home.ResumeCount == 1,
              $"current={host.Current?.Id} leave={settings.LeaveCount} resume={home.ResumeCount}");

        var tall = new TestPage("tall", "超高页", new PageSize(0, 2000), canBack: true);
        host.Push(tall);
        Check("超高页面被工作区夹取", host.PanelRect.MaxY - host.PanelRect.MinY <= 800f - metrics.MaxHeightMargin + 0.5f,
              $"h={host.PanelRect.MaxY - host.PanelRect.MinY:F0}");
        host.Pop();

        var replace = new TestPage("replace", "替换页", new PageSize(0, 400), canBack: false);
        host.Replace(replace);
        Check("替换栈顶", host.Count == 1 && ReferenceEquals(host.Current, replace)
              && home.LeaveCount == 1 && home.LastLeave == PageLeaveReason.Replace,
              $"count={host.Count} current={host.Current?.Id} home.leave={home.LeaveCount}");

        host.Close(animate: false);
        Check("关闭页面栈", !host.IsOpen && host.Count == 0 && replace.LeaveCount == 1
              && replace.LastLeave == PageLeaveReason.Close,
              $"open={host.IsOpen} count={host.Count} leave={replace.LeaveCount}");

        // LegacyPage 适配器：迁移期把现有页面包进来时，Enter/Leave 必须走通。
        int legacyEnter = 0, legacyLeave = 0;
        var legacy = new LegacyPage
        {
            Id = "legacy",
            Title = "旧页",
            PreferredSize = new PageSize(0, 480),
            CanBack = true,
            Enter = _ => legacyEnter++,
            Leave = (_, _) => legacyLeave++,
        };
        host.Open(home, animate: false);
        host.Push(legacy);
        bool legacyPushed = ReferenceEquals(host.Current, legacy) && legacyEnter == 1;
        host.Pop();
        Check("LegacyPage 适配", legacyPushed && legacyLeave == 1,
              $"pushed={legacyPushed} enter={legacyEnter} leave={legacyLeave}");

        Console.WriteLine($"  合计：{pass} 通过 / {fail} 失败");
        return fail == 0 ? 0 : 1;
    }

    private sealed class TestPage : IPage
    {
        public TestPage(string id, string title, PageSize size, bool canBack)
        {
            Id = id;
            Title = title;
            PreferredSize = size;
            CanBack = canBack;
        }

        public string Id { get; }
        public string Title { get; }
        public PageSize PreferredSize { get; }
        public bool CanBack { get; }
        public bool Modal => true;
        public bool IsAnimating => false;

        public int EnterCount { get; private set; }
        public int LeaveCount { get; private set; }
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public PageLeaveReason LastLeave { get; private set; }

        public void OnEnter(PageContext ctx) => EnterCount++;
        public void OnLeave(PageLeaveReason reason, PageResult result)
        {
            LeaveCount++;
            LastLeave = reason;
        }
        public void OnPause() => PauseCount++;
        public void OnResume() => ResumeCount++;
        public void Layout(in PageFrame frame) { }
        public void Render(ID2D1DeviceContext ctx, in PageFrame frame) { }
        public PageHit HitTest(float x, float y) => new(PageHitKind.Content, "page.content");
        public bool PointerDown(in UiPointerEvent e) => true;
        public bool PointerMove(in UiPointerEvent e) => true;
        public bool PointerUp(in UiPointerEvent e) => true;
        public bool Wheel(float x, float y, int delta) => true;
        public bool Key(int vk, bool ctrl, bool shift, bool alt, bool repeat) => true;
    }
}
