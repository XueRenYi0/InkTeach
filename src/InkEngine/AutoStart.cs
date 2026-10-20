using Microsoft.Win32;

namespace InkEngine;

/// <summary>
/// 开机自动启动（每用户，HKCU，不要求管理员）。
///
/// 只登记一个当前用户的 Run 值，不装服务、不建计划任务：
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Run
///   InkTeach = "C:\...\InkTeach.exe" --startup
///
/// 另外读一遍任务管理器的 StartupApproved 状态。用户在那里点过“禁用”之后，
/// Run 值还在，但系统不会再启动它；界面不能只看见 Run 值就显示成“开”。
/// </summary>
internal static class AutoStart
{
    internal const string ValueName = "InkTeach";
    private const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    // 自检时改成临时键，绝不能碰用户真正的开机启动项。
    internal static string RunKeyPath = DefaultRunKey;
    internal static string ApprovedKeyPath = DefaultApprovedKey;

    internal static void ResetTestPaths()
    {
        RunKeyPath = DefaultRunKey;
        ApprovedKeyPath = DefaultApprovedKey;
    }

    internal static void UseTestPaths(string runKeyPath, string approvedKeyPath)
    {
        RunKeyPath = runKeyPath;
        ApprovedKeyPath = approvedKeyPath;
    }

    /// <summary>
    /// 写进 Run 值的完整命令行。路径必须带引号：Program Files、中文目录、
    /// 用户名里有空格都靠它；末尾的 --startup 是产品自己的“后台待命”标记。
    /// </summary>
    internal static string BuildCommand(string exePath)
        => string.IsNullOrWhiteSpace(exePath) ? "" : "\"" + exePath + "\" --startup";

    internal static bool IsEnabled()
    {
        try
        {
            string command = ReadCommand();
            return !string.IsNullOrWhiteSpace(command) && !DisabledByTaskManager();
        }
        catch (Exception ex)
        {
            Console.WriteLine("开机自启：读取注册表失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Run 里有没有这一项（不管任务管理器是否禁用）。安装/升级后用来区分
    /// “没登记”和“登记了但用户禁用了”：前者可以按用户偏好补写，后者绝不能擅自打开。
    /// </summary>
    internal static bool HasEntry()
    {
        try { return !string.IsNullOrWhiteSpace(ReadCommand()); }
        catch { return false; }
    }

    /// <summary>
    /// 打开自启。成功返回 true；失败时 error 里带一句能写进日志的原因。
    /// 写 Run 值成功、但仍被 StartupApproved 挡住时也算失败（界面不能骗人）。
    /// </summary>
    internal static bool TryEnable(string exePath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(exePath))
        {
            error = "取不到当前程序路径";
            return false;
        }

        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
            {
                if (key == null)
                {
                    error = "打不开当前用户的 Run 键";
                    return false;
                }
                key.SetValue(ValueName, BuildCommand(exePath), RegistryValueKind.String);
            }
            ClearTaskManagerDisabledFlag();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (!IsEnabled())
        {
            error = "Run 值已写，但任务管理器仍把这一项标成禁用";
            return false;
        }
        return true;
    }

    /// <summary>关闭自启；同时清掉任务管理器的旧状态，避免下次打开时残留“禁用”。</summary>
    internal static bool TryDisable(out string error)
    {
        error = "";
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                if (key != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            ClearTaskManagerDisabledFlag();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (IsEnabled())
        {
            error = "Run 值没能删掉";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 程序升级、换目录后把旧路径修成当前 exe。只在已经启用时调用；
    /// 返回 true 表示确实改写了。用户禁用过的项不会被这里重新打开。
    /// </summary>
    internal static bool EnsureCurrentPath(string exePath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        try
        {
            string old = ReadCommand();
            if (string.IsNullOrWhiteSpace(old)) return false;
            string want = BuildCommand(exePath);
            if (string.Equals(old.Trim(), want, StringComparison.OrdinalIgnoreCase)) return false;

            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                if (key == null) return false;
                key.SetValue(ValueName, want, RegistryValueKind.String);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>
    /// StartupApproved\Run 的 12 字节里，第一个字节是状态：
    /// 02 / 08 = 允许，03 / 07 = 用户从任务管理器禁用。没有这一项 = 允许。
    /// </summary>
    private static bool DisabledByTaskManager()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            if (key?.GetValue(ValueName) is byte[] b && b.Length > 0)
                return b[0] == 0x03 || b[0] == 0x07;
        }
        catch
        {
            // 读不到就按“没被禁用”处理：Run 值存在时，宁可在界面上显示开，
            // 也不要因为一个附属键读失败把用户已经设好的自启说成关。
        }
        return false;
    }

    private static void ClearTaskManagerDisabledFlag()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
