// 设置页后台自检（2026-11-07）：
// **不创建任何窗口、不注入鼠标、不抢前台**——只摆几何 + 调命中 + 逐行执行一次。
// 用户在用电脑时也可以跑；它只往控制台打印。
//
// 背景：设置页出现过"有的分类里的行点不动"（MoreHitAt 在非工具栏分类提前 return），
// 以及"一个分类只有一行，看着像没内容"。这里把"每一类、每一行都能命中/能执行"
// 钉成一条可重复的后台用例。

using System.Numerics;
using System.Reflection;
using InkEngine;
using InkUi;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkTeach;

internal static class SettingsAudit
{
    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("=== 设置页后台自检（无窗口）===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-34} {detail}");
        }

        var screen = new RectF { MinX = 0, MinY = 0, MaxX = 1280, MaxY = 800 };
        var ui = new FullUi();
        var host = new AuditHost(screen);
        ui.Attach(host);
        ui.OpenSettingsPageForTest();

        int cats = ui.SettingsCategoryCountForTest;
        int rows = ui.SettingsRowCountForTest;
        Check("分类数", cats == 7, $"实际 {cats}");
        Check("行数", rows > 0, $"实际 {rows}");

        // 1) 每一行都必须属于且只属于一个分类
        int unassigned = 0;
        for (int i = 0; i < rows; i++)
            if (ui.SettingsRowCategoryForTest(i) < 0) unassigned++;
        Check("所有行都归到分类", unassigned == 0, $"未归类 {unassigned}");

        // 2) 左侧分类：每一格都能命中
        int navBad = 0;
        for (int c = 0; c < cats; c++)
        {
            var r = ui.SettingsNavRectForTest(c);
            int hit = ui.MoreHitAtForTest((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
            if (hit != 320 + c) { navBad++; Console.WriteLine($"    分类 {c} 命中 {hit}（应为 {320 + c}）"); }
        }
        Check("左侧分类全部可点", navBad == 0, $"异常 {navBad}");

        // 3) 滚到每一段，段内每一行都要在视口里、且命中自己
        int rowBad = 0, offscreen = 0;
        for (int c = 0; c < cats; c++)
        {
            ui.SettingsSetCategoryForTest(c);
            var vp = ui.SettingsViewportRectForTest;
            for (int i = 0; i < rows; i++)
            {
                if (ui.SettingsRowCategoryForTest(i) != c) continue;
                var r = ui.SettingsRowRectForTest(i);
                if (r.IsEmpty) { rowBad++; Console.WriteLine($"    行 {i} 矩形为空"); continue; }
                float cx = (r.MinX + r.MaxX) * 0.5f, cy = (r.MinY + r.MaxY) * 0.5f;
                if (!vp.Contains(cx, cy)) { offscreen++; Console.WriteLine($"    行 {i} 不在视口（y={cy:F0}）"); continue; }
                int hit = ui.MoreHitAtForTest(cx, cy);
                if (hit != 60 + i) { rowBad++; Console.WriteLine($"    行 {i} 命中 {hit}（应为 {60 + i}）"); }
            }
        }
        Check("所有行在所属段落可见", offscreen == 0, $"离屏 {offscreen}");
        Check("所有行都能命中", rowBad == 0, $"异常 {rowBad}");

        // 4) 连续滚动：能滚、滚到底最后一行仍可见、滚动条能拖
        bool canScroll = ui.SettingsScrollMaxForTest > 1f;
        Check("内容高于视口、可连续滚动", canScroll, $"max={ui.SettingsScrollMaxForTest:F0}");
        if (canScroll)
        {
            ui.SettingsScrollToForTest(ui.SettingsScrollMaxForTest);
            float max = ui.SettingsScrollMaxForTest;
            Check("滚到底不越界", MathF.Abs(ui.SettingsScrollValueForTest - max) < 0.5f,
                  $"{ui.SettingsScrollValueForTest:F0}/{max:F0}");
            Check("滚到底高亮最后一段", ui.SettingsActiveCategoryForTest == cats - 1,
                  $"高亮 {ui.SettingsActiveCategoryForTest}（应为 {cats - 1}）");

            int last = -1;
            for (int i = 0; i < rows; i++)
                if (ui.SettingsRowCategoryForTest(i) == cats - 1) last = i;
            var lr = ui.SettingsRowRectForTest(last);
            var vp = ui.SettingsViewportRectForTest;
            Check("滚到底最后一行可见", vp.Contains((lr.MinX + lr.MaxX) * 0.5f, (lr.MinY + lr.MaxY) * 0.5f),
                  $"y={lr.MinY:F0}..{lr.MaxY:F0} / vp={vp.MinY:F0}..{vp.MaxY:F0}");

            var thumb = ui.SettingsScrollThumbRectForTest;
            int sh = ui.MoreHitAtForTest((thumb.MinX + thumb.MaxX) * 0.5f, (thumb.MinY + thumb.MaxY) * 0.5f);
            Check("滚动条可命中", sh == 340, $"命中 {sh}");
        }

        // 6) 双指纵滑 + 滚轮：设置页必须接住，不能漏到画布
        {
            ui.SettingsScrollToForTest(0);
            var vp = ui.SettingsViewportRectForTest;
            float cx = (vp.MinX + vp.MaxX) * 0.5f, cy = (vp.MinY + vp.MaxY) * 0.5f;
            bool begin = ui.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Begin, cx, cy, 0, 0, 0));
            bool move = ui.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Move, cx, cy, -100, -0.5f, 0));
            float afterMove = ui.SettingsScrollValueForTest;
            bool end = ui.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.End, cx, cy, 0, -0.5f, 0));
            Check("双指纵滑接住并滚动", begin && move && end && afterMove > 1f,
                  $"begin={begin} move={move} end={end} scroll={afterMove:F0}");

            float beforeFling = ui.SettingsScrollValueForTest;
            host.NowMs = 16;
            ui.PrepareFrame();
            bool flingMoved = ui.SettingsScrollValueForTest > beforeFling + 0.5f;
            Check("双指松手后惯性继续", flingMoved,
                  $"{beforeFling:F0} → {ui.SettingsScrollValueForTest:F0}");

            ui.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Cancel, 0, 0, 0, 0, 0));
            ui.SettingsScrollToForTest(0);
            bool wheel = ui.Wheel(cx, cy, -120);
            Check("设置页滚轮可滚", wheel && ui.SettingsScrollValueForTest > 1f,
                  $"wheel={wheel} scroll={ui.SettingsScrollValueForTest:F0}");
        }

        // 7) 指针级双指纵滑：设置页的触摸按下会被 UI 先吃掉，必须自己记两根手指
        {
            ui.SettingsScrollToForTest(0);
            var vp = ui.SettingsViewportRectForTest;
            float cx = (vp.MinX + vp.MaxX) * 0.5f, cy = (vp.MinY + vp.MaxY) * 0.5f;
            var p1 = new UiPointerEvent(cx - 20, cy, 0.5f, false, false, true, 11);
            var p2 = new UiPointerEvent(cx + 20, cy, 0.5f, false, false, true, 12);
            ui.PointerDown(p1);
            ui.PointerDown(p2);
            ui.PointerMove(new UiPointerEvent(cx - 20, cy - 80, 0.5f, false, false, true, 11));
            ui.PointerMove(new UiPointerEvent(cx + 20, cy - 80, 0.5f, false, false, true, 12));
            float moved = ui.SettingsScrollValueForTest;
            ui.PointerUp(new UiPointerEvent(cx - 20, cy - 80, 0.5f, false, false, true, 11));
            ui.PointerUp(new UiPointerEvent(cx + 20, cy - 80, 0.5f, false, false, true, 12));
            ui.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Cancel, 0, 0, 0, 0, 0));
            Check("指针级双指纵滑", moved > 1f, $"scroll={moved:F0}");
        }

        // 5) 每一行都能执行一次（不弹窗：命令全走 no-op 代理）
        // 先出两张图（顶部/底部），放在执行之前——执行会切深色主题，图会变。
        ui.SettingsScrollToForTest(0);
        Check("顶部整页离屏绘制", RenderSettingsPng(ui, "settings-audit-top.png"),
              "reports/settings-audit-top.png");
        if (canScroll)
        {
            ui.SettingsScrollToForTest(ui.SettingsScrollMaxForTest);
            Check("底部整页离屏绘制", RenderSettingsPng(ui, "settings-audit-bottom.png"),
                  "reports/settings-audit-bottom.png");
        }

        int actBad = 0;
        for (int i = 0; i < rows; i++)
        {
            try { ui.ActivateRowForTest(i); }
            catch (Exception ex)
            {
                actBad++;
                Console.WriteLine($"    行 {i} 执行异常：{ex.GetType().Name}: {ex.Message}");
            }
        }
        Check("所有行都能执行", actBad == 0, $"异常 {actBad}");

        Console.WriteLine($"  合计：{pass} 通过 / {fail} 失败");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>离屏渲染：用 D2D device context 画到一张 CPU 可读位图，再写 PNG。</summary>
    private static bool RenderSettingsPng(FullUi ui, string fileName)
    {
        const int pad = 16;
        var panel = ui.MoreRectForTest;
        int w = (int)MathF.Ceiling(panel.MaxX - panel.MinX) + pad * 2;
        int h = (int)MathF.Ceiling(panel.MaxY - panel.MinY) + pad * 2;
        if (w <= 0 || h <= 0 || Gfx.D2DDevice == null) return false;

        var pf = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
        using var ctx = Gfx.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        using var target = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
        using var cpu = ctx.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0,
            new BitmapProperties1(pf, 96f, 96f, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));

        ctx.Target = target;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0.92f, 0.92f, 0.94f, 1f));
        ctx.Transform = Matrix3x2.CreateTranslation(-panel.MinX + pad, -panel.MinY + pad);
        ui.Render(ctx, UiTheme.Default);
        ctx.Transform = Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        ctx.Target = null;
        if (hr.Failure) return false;

        cpu.CopyFromBitmap(new System.Drawing.Point(0, 0), target);
        var map = cpu.Map(MapOptions.Read);
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(
                IntPtr.Add(map.Bits, y * (int)map.Pitch), buf, y * w * 4, w * 4);
        cpu.Unmap();

        string dir = Path.Combine(AppContext.BaseDirectory, "reports");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName),
                           PngWriter.EncodeBgraPremultiplied(buf, w, h));
        return true;
    }

    /// <summary>无窗口宿主：命令用 DispatchProxy 全部 no-op，状态用默认 UiState。</summary>
    private sealed class AuditHost : IUiHost
    {
        private readonly Dictionary<string, string> _prefs = new();
        private readonly RectF _screen;

        public AuditHost(RectF screen) => _screen = screen;

        public IEngineCommands Commands { get; } = DispatchProxy.Create<IEngineCommands, NoopCommands>();
        public UiState State { get; } = new();
        public RectF Screen => _screen;
        public RectF WorkArea => _screen;
        public float DpiScale => 1f;
        public float UiScale => 1f;
        public bool IsReattach => false;
        public Vortice.DirectWrite.IDWriteFactory TextFactory => Gfx.WriteFactory;
        public Vortice.Direct2D1.ID2D1Factory1 PathFactory => Gfx.D2DFactory;
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

    /// <summary>IEngineCommands 太大，用反射代理全部返回默认值；只为让 ActivateRow 不碰真引擎。</summary>
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
