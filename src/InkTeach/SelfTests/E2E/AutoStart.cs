using Microsoft.Win32;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// `--autostarttest`：只动当前用户、且只动临时子键。
    /// 验四件事：写入格式、任务管理器禁用位、换路径修复、关闭清理。
    /// </summary>
    private void AutoStartTest()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-42} {detail}");
        }

        string root = @"Software\InkTeach\SelfTest\AutoStart-" + Guid.NewGuid().ToString("N");
        string runKey = root + @"\Run";
        string approvedKey = root + @"\StartupApproved";
        const string exe1 = @"C:\Program Files\Ink Teach\InkTeach.exe";
        const string exe2 = @"D:\教学工具\InkTeach\InkTeach.exe";

        try
        {
            AutoStart.UseTestPaths(runKey, approvedKey);

            Check("初始状态：未启用", !AutoStart.IsEnabled(), $"IsEnabled={AutoStart.IsEnabled()}");
            Check("关闭一个不存在的项也成功",
                  AutoStart.TryDisable(out string disableMissingErr),
                  disableMissingErr);

            Check("打开自启", AutoStart.TryEnable(exe1, out string enableErr), enableErr);
            Check("读回状态是开", AutoStart.IsEnabled(), $"IsEnabled={AutoStart.IsEnabled()}");

            string command = Registry.CurrentUser.OpenSubKey(runKey)?.GetValue(AutoStart.ValueName) as string;
            Check("命令行带引号并带 --startup",
                  command == "\"" + exe1 + "\" --startup",
                  command ?? "(空)");
            Check("命令行不会重复 --startup",
                  command != null && command.Split("--startup").Length == 2,
                  command ?? "(空)");

            using (var approved = Registry.CurrentUser.CreateSubKey(approvedKey, writable: true))
            {
                approved?.SetValue(AutoStart.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                                   RegistryValueKind.Binary);
            }
            Check("任务管理器禁用位让状态变成关", !AutoStart.IsEnabled(), $"IsEnabled={AutoStart.IsEnabled()}");

            Check("再次打开会清掉禁用位", AutoStart.TryEnable(exe1, out string reEnableErr), reEnableErr);
            Check("清掉禁用位后状态是开", AutoStart.IsEnabled(), $"IsEnabled={AutoStart.IsEnabled()}");
            using (var approved = Registry.CurrentUser.OpenSubKey(approvedKey))
            {
                Check("StartupApproved 的旧值已删除",
                      approved?.GetValue(AutoStart.ValueName) == null,
                      approved?.GetValue(AutoStart.ValueName)?.ToString() ?? "(无)");
            }

            Check("换路径会修命令行", AutoStart.EnsureCurrentPath(exe2, out string repairErr), repairErr);
            string repaired = Registry.CurrentUser.OpenSubKey(runKey)?.GetValue(AutoStart.ValueName) as string;
            Check("路径已改成新 exe",
                  repaired == "\"" + exe2 + "\" --startup",
                  repaired ?? "(空)");

            Check("关闭自启", AutoStart.TryDisable(out string disableErr), disableErr);
            Check("关闭后状态是关", !AutoStart.IsEnabled(), $"IsEnabled={AutoStart.IsEnabled()}");
            Check("关闭后 Run 值已删除",
                  Registry.CurrentUser.OpenSubKey(runKey)?.GetValue(AutoStart.ValueName) == null,
                  "Run 值");
            Check("关闭后 StartupApproved 旧值已删除",
                  Registry.CurrentUser.OpenSubKey(approvedKey)?.GetValue(AutoStart.ValueName) == null,
                  "StartupApproved 值");
            Check("没有 Run 值时修路径不误开",
                  !AutoStart.EnsureCurrentPath(exe2, out _) && !AutoStart.IsEnabled(),
                  $"IsEnabled={AutoStart.IsEnabled()}");

            // 走一遍引擎命令（UiHost → Engine）而不只是静态帮手。
            SetAutoStartFromUi(true);
            Check("设置命令能打开并更新 UiState",
                  AutoStartOn && AutoStart.IsEnabled(),
                  $"AutoStartOn={AutoStartOn}，IsEnabled={AutoStart.IsEnabled()}");
            SetAutoStartFromUi(false);
            Check("设置命令能关闭并更新 UiState",
                  !AutoStartOn && !AutoStart.IsEnabled(),
                  $"AutoStartOn={AutoStartOn}，IsEnabled={AutoStart.IsEnabled()}");
        }
        catch (Exception ex)
        {
            Check("自检过程无异常", false, ex.Message);
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); } catch { }
            AutoStart.ResetTestPaths();
        }

        if (fail == 0) Console.WriteLine($"PASS: autostarttest {pass}/{pass + fail}");
        Console.WriteLine($"autostarttest: {pass} 通过 / {fail} 失败");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
