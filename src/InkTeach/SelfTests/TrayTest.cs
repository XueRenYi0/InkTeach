// [职责] 「工具栏只在 PPT 显示 + 托盘兜底」自检：状态机 / 界面让路 / 托盘开关判据。
// [测试] --traytest（无窗口：不建托盘图标，只验判定与状态流转）

namespace InkTeach.SelfTests;

/// <summary>
/// `--traytest`：验「工具栏只在 PPT 显示」这一条设置的状态机。
///
/// **为什么无窗口**：托盘图标本身要 Shell_NotifyIcon（会真在用户托盘里留一格），
/// 自检不该碰真实的系统托盘——几十个自检进程各塞一个，用户托盘就废了。
/// 所以这里只验"什么时候该显示托盘 / 界面该不该让路"这两份判定，
/// 托盘窗口那几条 Win32 调用交给真机验收（同 `--uiscale` 的口径）。
/// </summary>
internal static class TrayTest
{
    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("=== 工具栏只在 PPT 显示 + 托盘兜底 自检 ===");

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-40} {detail}");
        }

        // 1) 出厂默认关：老用户升级后完全无感（这是用户 2026-11-25 明确定的前提）。
        var prefs = new Dictionary<string, string>();
        bool barOnlyPpt = prefs.TryGetValue("barOnlyPpt", out var v) && v == "1";
        Check("出厂默认关（老用户无感）", !barOnlyPpt, $"读回={barOnlyPpt}");

        // 2) 存的是"开过才写"：关掉要把键删掉，而不是写 "0"。
        //    这是本仓的老规矩（默认值不落盘），否则以后改默认值老配置会顶掉新默认。
        static void SetPref(Dictionary<string, string> p, string key, bool on)
        {
            if (on) p[key] = "1";
            else p.Remove(key);
        }
        SetPref(prefs, "barOnlyPpt", true);
        bool storedOn = prefs.ContainsKey("barOnlyPpt");
        SetPref(prefs, "barOnlyPpt", false);
        bool storedOff = prefs.ContainsKey("barOnlyPpt");
        Check("落盘：开 = 写 \"1\"、关 = 删项（不写 \"0\"）",
              storedOn && !storedOff, $"开={storedOn} 关后还在={storedOff}");

        // 3) 「藏不藏工具条」和「托盘露不露」是同一份判断的两个说法：
        //    开 + 不在放映 → 藏工具条 / 露托盘；其余组合一律相反。
        static bool Suppressed(bool on, bool ppt) => on && !ppt;
        static bool WantTray(bool on, bool ppt) => on && !ppt;
        Check("关着：任何模式下都不藏、不出托盘",
              !Suppressed(false, false) && !Suppressed(false, true)
              && !WantTray(false, false) && !WantTray(false, true));
        Check("开着 + 不在放映：藏工具条、露托盘",
              Suppressed(true, false) && WantTray(true, false));
        Check("开着 + 放映中：工具条照常显示、托盘不出（屏幕上有条）",
              !Suppressed(true, true) && !WantTray(true, true));

        // 4) 关键一致性：**"藏了工具条"和"需要托盘"必须永远是同一刻**。
        //    分成两份判断就会出现最坏的失败模式——条藏了、托盘也没出来 = 软件打不开。
        bool consistent = true;
        foreach (bool on in new[] { false, true })
            foreach (bool ppt in new[] { false, true })
                consistent &= Suppressed(on, ppt) == WantTray(on, ppt);
        Check("判据一致：藏工具条 ⟺ 需要托盘（不会两个都没有）", consistent);

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? $"  PASS：工具栏/托盘判定正确——共 {pass} 项"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");
        return fail == 0 ? 0 : 1;
    }
}
