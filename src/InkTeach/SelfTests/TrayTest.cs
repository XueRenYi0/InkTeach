// [职责] 「非课件时工具栏」自检：三档落盘兼容、收球贴边两阶段、仅 PPT 显示桌面零痕迹与托盘单击呼出/收起。
// [测试] --traytest（无窗口：驱动真实 FullUi、Engine 状态机与模拟 IUiHost，不建托盘/窗口）

using System.Reflection;
using InkEngine;
using InkUi;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace InkTeach.SelfTests;

/// <summary>
/// `--traytest`：验「非课件时工具栏」（沿用 <c>barOnlyPpt</c> 配置键）三档状态机。
/// </summary>
internal static class TrayTest
{
    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("=== 非课件时工具栏（三档）自检 ===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-42} {detail}");
        }

        TestPersistence(Check);
        TestAutoStowTransitions(Check);
        TestPptOnlyTrayAndTempToggle(Check);
        TestDefaultBottomDockHide(Check);

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? $"  PASS：非课件时工具栏三档状态机正确——共 {pass} 项"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");
        return fail == 0 ? 0 : 1;
    }

    private static void TestPersistence(Action<string, bool, string> check)
    {
        var prefs = new Dictionary<string, string>();
        static BarIdleMode ReadPref(Dictionary<string, string> p)
            => global::InkEngine.InkEngine.ParseBarIdleMode(p.TryGetValue("barOnlyPpt", out var v) ? v : null);
        static void WritePref(Dictionary<string, string> p, BarIdleMode mode)
        {
            string s = global::InkEngine.InkEngine.StoreBarIdleMode(mode);
            if (s == null) p.Remove("barOnlyPpt");
            else p["barOnlyPpt"] = s;
        }

        check("出厂默认 Off（老用户无感）", ReadPref(prefs) == BarIdleMode.Off, $"读回={ReadPref(prefs)}");

        prefs["barOnlyPpt"] = "1";
        check("兼容旧配置 \"1\" -> PptOnlyTray", ReadPref(prefs) == BarIdleMode.PptOnlyTray, $"读回={ReadPref(prefs)}");

        WritePref(prefs, BarIdleMode.StowEdge);
        bool storedStow = prefs.TryGetValue("barOnlyPpt", out var vStow) && vStow == "stow"
                       && ReadPref(prefs) == BarIdleMode.StowEdge;
        WritePref(prefs, BarIdleMode.PptOnlyTray);
        bool storedPptOnly = prefs.TryGetValue("barOnlyPpt", out var vPpt) && vPpt == "1"
                          && ReadPref(prefs) == BarIdleMode.PptOnlyTray;
        WritePref(prefs, BarIdleMode.Off);
        bool storedOff = !prefs.ContainsKey("barOnlyPpt") && ReadPref(prefs) == BarIdleMode.Off;
        check("三档落盘：Off=删项 / StowEdge=\"stow\" / PptOnlyTray=\"1\"",
              storedStow && storedPptOnly && storedOff,
              $"stow={storedStow} pptOnly={storedPptOnly} off={storedOff}");
    }

    private static void TestAutoStowTransitions(Action<string, bool, string> check)
    {
        var screen = new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 800 };
        var work = new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 752 };
        var host = new TrayHost(screen, work);
        var ui = new FullUi();
        ui.Attach(host);

        void Push(BarIdleMode mode, bool ppt, bool doc, bool board, bool suppressed = false)
        {
            host.State = host.State with
            {
                BarIdleMode = mode,
                BarSuppressed = suppressed,
                PptMode = ppt,
                DocOpen = doc,
                Board = board,
            };
            ui.OnStateChanged(host.State);
            ui.PrepareFrame();
        }

        void StepFrames(int count, double stepMs = 16)
        {
            for (int i = 0; i < count; i++) { host.NowMs += stepMs; ui.PrepareFrame(); }
        }

        static float WidthOf(in RectF r) => r.MaxX - r.MinX;
        static float HeightOf(in RectF r) => r.MaxY - r.MinY;

        Push(BarIdleMode.StowEdge, ppt: false, doc: false, board: false);
        check("收球贴边初始未触发前：保持展开", WidthOf(ui.QueryBounds()) > 200f, $"w={WidthOf(ui.QueryBounds()):F0}");

        Push(BarIdleMode.StowEdge, ppt: true, doc: false, board: false);
        StepFrames(20);
        check("收球贴边进入 PPT 放映：保持展开", WidthOf(ui.QueryBounds()) > 200f, $"w={WidthOf(ui.QueryBounds()):F0}");

        Push(BarIdleMode.StowEdge, ppt: false, doc: false, board: false);
        StepFrames(5);
        var midPhase1 = ui.QueryBounds();
        check("退出 PPT 阶段 1：先原地收成球、不直接下沉",
              ui.IsAnimating && midPhase1.MaxY < screen.MaxY - 10f,
              $"anim={ui.IsAnimating} maxY={midPhase1.MaxY:F1}");

        StepFrames(15);
        bool phase2Started = ui.IsAnimating;
        StepFrames(40);
        var stowed = ui.QueryBounds();
        check("退出 PPT 阶段 2：沉到底边变 8px 短把手并回 0 FPS",
              phase2Started && !ui.IsAnimating && HeightOf(stowed) <= 9.5f && stowed.MaxY >= screen.MaxY - 1f,
              $"anim={ui.IsAnimating} h={HeightOf(stowed):F1} maxY={stowed.MaxY:F1}");

        Push(BarIdleMode.StowEdge, ppt: true, doc: false, board: false);
        StepFrames(30);
        check("收球贴边后进入 PPT：自动唤醒展开", WidthOf(ui.QueryBounds()) > 200f && !ui.IsAnimating,
              $"w={WidthOf(ui.QueryBounds()):F0}");

        Push(BarIdleMode.StowEdge, ppt: false, doc: false, board: false);
        StepFrames(60);
        check("再次退出 PPT：回到 8px 短把手", HeightOf(ui.QueryBounds()) <= 9.5f, $"h={HeightOf(ui.QueryBounds()):F1}");

        Push(BarIdleMode.StowEdge, ppt: false, doc: true, board: false);
        StepFrames(30);
        check("收球贴边后打开 PDF：自动唤醒展开", WidthOf(ui.QueryBounds()) > 200f && !ui.IsAnimating,
              $"w={WidthOf(ui.QueryBounds()):F0}");

        Push(BarIdleMode.StowEdge, ppt: false, doc: false, board: false);
        StepFrames(60);
        check("关闭 PDF 后：自动收球贴边", HeightOf(ui.QueryBounds()) <= 9.5f && !ui.IsAnimating,
              $"h={HeightOf(ui.QueryBounds()):F1}");

        Push(BarIdleMode.StowEdge, ppt: false, doc: false, board: true);
        StepFrames(30);
        check("收球贴边后打开白板：自动唤醒展开", WidthOf(ui.QueryBounds()) > 200f && !ui.IsAnimating,
              $"w={WidthOf(ui.QueryBounds()):F0}");

        Push(BarIdleMode.StowEdge, ppt: true, doc: false, board: true);
        StepFrames(20);
        Push(BarIdleMode.StowEdge, ppt: false, doc: false, board: true);
        StepFrames(60);
        check("白板打开中退出 PPT：不误收球贴边",
              WidthOf(ui.QueryBounds()) > 200f && HeightOf(ui.QueryBounds()) > 40f,
              $"w={WidthOf(ui.QueryBounds()):F0} h={HeightOf(ui.QueryBounds()):F0}");

        TestManualExpandExitsStow(ui, screen, Push, StepFrames, check);
    }

    private static void TestManualExpandExitsStow(
        FullUi ui, in RectF screen,
        Action<BarIdleMode, bool, bool, bool, bool> push, Action<int, double> stepFrames,
        Action<string, bool, string> check)
    {
        push(BarIdleMode.Off, false, false, false, false);
        push(BarIdleMode.StowEdge, false, true, false, false);
        stepFrames(20, 16);
        push(BarIdleMode.StowEdge, false, false, false, false);
        stepFrames(60, 16);

        var tab = ui.QueryBounds();
        float tx = (tab.MinX + tab.MaxX) * 0.5f, ty = (tab.MinY + tab.MaxY) * 0.5f;
        ui.PointerMove(new UiPointerEvent(tx, ty, 0f, false, false, false, 1));
        stepFrames(25, 16);
        var ball = ui.QueryBounds();
        float bx = (ball.MinX + ball.MaxX) * 0.5f, by = (ball.MinY + ball.MaxY) * 0.5f;
        ui.PointerDown(new UiPointerEvent(bx, by, 0f, false, false, false, 1));
        ui.PointerUp(new UiPointerEvent(bx, by, 0f, false, false, false, 1));
        ui.PointerMove(new UiPointerEvent(screen.MaxX * 0.5f, 100f, 0f, false, false, false, 1));
        stepFrames(150, 16);
        var expanded = ui.QueryBounds();
        check("手动点击悬浮球展开：退出收球贴边态且不再自动沉回",
              expanded.MaxX - expanded.MinX > 200f && expanded.MaxY < screen.MaxY - 10f && !ui.IsAnimating,
              $"w={expanded.MaxX - expanded.MinX:F0} maxY={expanded.MaxY:F1} anim={ui.IsAnimating}");
    }

    private static void TestPptOnlyTrayAndTempToggle(Action<string, bool, string> check)
    {
        var engine = new global::InkEngine.InkEngine();
        engine.BarIdleMode = BarIdleMode.PptOnlyTray;
        check("仅 PPT 显示·桌面初始：隐藏工具栏且常驻托盘",
              engine.BarSuppressedNow && engine.WantTray && !engine.BarTempShown,
              $"suppressed={engine.BarSuppressedNow} wantTray={engine.WantTray}");

        engine.ToggleBarFromTray();
        check("托盘单击呼出：临时显示工具栏且托盘仍在",
              engine.BarTempShown && !engine.BarSuppressedNow && engine.WantTray,
              $"temp={engine.BarTempShown} suppressed={engine.BarSuppressedNow} tray={engine.WantTray}");

        engine.ToggleBarFromTray();
        check("托盘再次单击收起：回到桌面零痕迹",
              !engine.BarTempShown && engine.BarSuppressedNow && engine.WantTray,
              $"temp={engine.BarTempShown} suppressed={engine.BarSuppressedNow}");

        engine.SetPptModeForTest(true);
        bool inPptShown = !engine.BarSuppressedNow && !engine.WantTray;
        engine.SetPptModeForTest(false);
        engine.BoardOn = true;
        bool inBoardShown = !engine.BarSuppressedNow && !engine.WantTray;
        engine.BoardOn = false;
        check("进入 PPT 或打开白板：自动显条并隐藏托盘",
              inPptShown && inBoardShown && engine.BarSuppressedNow && engine.WantTray,
              $"ppt={inPptShown} board={inBoardShown}");

        engine.ExitPptOnlyFromTray();
        check("托盘「恢复常驻工具栏」：切回 Off 且不抑制",
              engine.BarIdleMode == BarIdleMode.Off && !engine.BarSuppressedNow && !engine.WantTray,
              $"mode={engine.BarIdleMode}");

        var screen = new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 800 };
        var work = new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 752 };
        var host = new TrayHost(screen, work);
        var ui = new FullUi();
        ui.Attach(host);

        host.State = host.State with { BarIdleMode = BarIdleMode.PptOnlyTray, BarSuppressed = true };
        ui.OnStateChanged(host.State);
        ui.PrepareFrame();
        check("UI 桌面零痕迹：Visible=false、Bounds 为空、0 FPS",
              !ui.Visible && ui.QueryBounds().IsEmpty && ui.ChromeBounds.IsEmpty
              && ui.BarBounds.IsEmpty && ui.PaintMargin == 0f && !ui.IsAnimating,
              $"vis={ui.Visible} boundsEmpty={ui.QueryBounds().IsEmpty} anim={ui.IsAnimating}");

        ui.ShowSettings();
        ui.PrepareFrame();
        bool settingsOpenOk = ui.Visible && ui.MoreOpenForTest && ui.MorePageIdForTest == "more.settings"
                           && ui.QueryBounds().Equals(screen) && ui.ChromeBounds.IsEmpty;
        check("托盘打开设置：模态豁免可见但底栏仍抑制",
              settingsOpenOk,
              $"vis={ui.Visible} page={ui.MorePageIdForTest} chromeEmpty={ui.ChromeBounds.IsEmpty}");

        // 点设置卡片外关闭：关闭动画期间点击底栏区域不得误触隐藏按钮
        ui.PointerDown(new UiPointerEvent(10f, 10f, 0f, false, false, false, 1));
        ui.PointerUp(new UiPointerEvent(10f, 10f, 0f, false, false, false, 1));
        host.NowMs += 16;
        ui.PrepareFrame();
        bool closingBlocked = !ui.MoreOpenForTest && ui.IsAnimating
                           && !ui.PointerDown(new UiPointerEvent(screen.MaxX * 0.5f, work.MaxY - 24f, 0f, false, false, false, 1));
        for (int i = 0; i < 40; i++) { host.NowMs += 16; ui.PrepareFrame(); }
        check("关闭设置动画期间不误点底栏，完成后恢复零痕迹",
              closingBlocked && !ui.Visible && ui.QueryBounds().IsEmpty && !ui.IsAnimating,
              $"blocked={closingBlocked} vis={ui.Visible} anim={ui.IsAnimating}");

        host.State = host.State with { BarSuppressed = false };
        ui.OnStateChanged(host.State);
        ui.Activate();
        for (int i = 0; i < 30; i++) { host.NowMs += 16; ui.PrepareFrame(); }
        check("取消抑制并 Activate：工具栏正常展开可见",
              ui.Visible && (ui.QueryBounds().MaxX - ui.QueryBounds().MinX) > 200f,
              $"vis={ui.Visible} w={ui.QueryBounds().MaxX - ui.QueryBounds().MinX:F0}");
    }

    private static void TestDefaultBottomDockHide(Action<string, bool, string> check)
    {
        var screen = new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 800 };
        var work = new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 752 };
        var host = new TrayHost(screen, work);
        host.SetPref("hide", "1");
        var ui = new FullUi();
        ui.Attach(host);
        ui.PrepareFrame();

        // 在默认底边位置碰一下面板再移开：新规则允许默认底边直接贴边隐藏。
        var b = ui.QueryBounds();
        ui.PointerMove(new UiPointerEvent((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f, 0f, false, false, false, 1));
        ui.PointerMove(new UiPointerEvent(screen.MaxX * 0.5f, 100f, 0f, false, false, false, 1));
        for (int i = 0; i < 80; i++) { host.NowMs += 20; ui.PrepareFrame(); }
        var hidden = ui.QueryBounds();
        check("贴边隐藏·默认底边位置（未拖走）：允许自动藏到底边",
              (hidden.MaxY - hidden.MinY) <= 12f && hidden.MaxY >= screen.MaxY - 1f,
              $"h={hidden.MaxY - hidden.MinY:F1} maxY={hidden.MaxY:F1}");

        // 被拖到左中段后，离开不隐藏。
        ui.SetAnchorForTest(new System.Numerics.Vector2(20f, 300f));
        ui.PrepareFrame();
        var atLeft = ui.QueryBounds();
        check("贴边隐藏·拖离底边后（左中段）：不藏",
              (atLeft.MaxY - atLeft.MinY) > 30f,
              $"h={atLeft.MaxY - atLeft.MinY:F1}");
    }

    private sealed class TrayHost : IUiHost
    {
        private readonly Dictionary<string, string> _prefs = new();
        private readonly RectF _screen, _work;

        public TrayHost(RectF screen, RectF work)
        {
            _screen = screen;
            _work = work;
        }

        public IEngineCommands Commands { get; } = DispatchProxy.Create<IEngineCommands, NoopCommands>();
        public UiState State { get; set; } = new() { WindowOpacity = 1f };
        public RectF Screen => _screen;
        public RectF WorkArea => _work;
        public float DpiScale => 1f;
        public float UiScale => 1f;
        public bool IsReattach => false;
        public IDWriteFactory TextFactory => Gfx.WriteFactory;
        public ID2D1Factory1 PathFactory => Gfx.D2DFactory;
        public double NowMs { get; set; }
        public int DocumentVersion => 0;
        public void InvalidateUi() { }
        public string GetPref(string key) => _prefs.TryGetValue(key, out var v) ? v : null;
        public void SetPref(string key, string value)
        {
            if (value == null) _prefs.Remove(key);
            else _prefs[key] = value;
        }
        public string KeyText(KeyAction action) => null;
        public void SetFloatingTheme(UiTheme theme) { }
    }

    private class NoopCommands : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            var rt = targetMethod.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt == typeof(bool)) return false;
            if (rt == typeof(int)) return 0;
            if (rt == typeof(float)) return 0f;
            if (rt == typeof(double)) return 0d;
            if (rt == typeof(string)) return null;
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }
}
