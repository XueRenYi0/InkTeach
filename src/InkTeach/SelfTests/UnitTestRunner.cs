using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using InkEngine;

namespace InkTeach;

/// <summary>
/// 给纯逻辑用的最小单元测试框架（零 NuGet 依赖）。
///
/// 为什么不用 xUnit：
///   1. 仓库规矩是不新增包、不改 csproj；本机外网受限，还原新包有风险。
///   2. 新建独立测试项目会绕过宿主现成的临时目录隔离，测试可能直接读写
///      用户真实的 settings / 板书 / 历史目录。
///
/// 用法：
///   .\run.ps1 --unittest            跑全部
///   .\run.ps1 --unittest EditAction 只跑名字里含 EditAction 的
///
/// 测试方法：internal static void，标 [UnitTest]，不建窗口、不碰 D2D。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class UnitTestAttribute : Attribute
{
    public string Name { get; }

    public UnitTestAttribute(string name = null) => Name = name;
}

/// <summary>断言失败时带上 File:Line，AI 不用再 grep 找现场。</summary>
internal static class Check
{
    public static void True(bool cond, string msg = "expected true",
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!cond) throw new Exception($"{file}:{line} {msg}");
    }

    public static void Equal<T>(T expected, T actual,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{file}:{line} expected <{expected}>, got <{actual}>");
    }

    public static void Near(float expected, float actual, float tol = 0.0001f,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (Math.Abs(expected - actual) > tol)
            throw new Exception($"{file}:{line} expected ~{expected}, got {actual}");
    }
}

internal static class UnitTestRunner
{
    public static int Run(string[] args)
    {
        string filter = args.Length > 1 ? args[1] : "";
        ApplyIsolation();

        var tests = typeof(UnitTestRunner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<UnitTestAttribute>() != null)
            .Where(m => filter.Length == 0
                        || (m.DeclaringType.Name + "." + m.Name)
                           .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(m => m.DeclaringType.Name)
            .ThenBy(m => m.Name)
            .ToList();

        if (tests.Count == 0)
        {
            Console.WriteLine($"没有匹配 \"{filter}\" 的单元测试");
            return 1;
        }

        int failed = 0;
        var all = Stopwatch.StartNew();
        foreach (var m in tests)
        {
            string name = m.DeclaringType.Name + "." + m.Name;
            var one = Stopwatch.StartNew();
            try
            {
                m.Invoke(null, null);
                Console.WriteLine($"PASS {name} ({one.ElapsedMilliseconds} ms)");
            }
            catch (TargetInvocationException ex)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"unit tests: {tests.Count - failed} passed / {failed} failed / {all.ElapsedMilliseconds} ms");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>和 --selfcheck 同一套隔离：测试绝不碰用户真实配置 / 板书 / 历史。</summary>
    private static void ApplyIsolation()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "inkteach-unittest");
        try { Directory.CreateDirectory(tmp); } catch { }

        if (InkSettings.PathOverride == null)
            InkSettings.PathOverride = Path.Combine(tmp, "settings.json");
        if (Recovery.AutoSavePathOverride == null)
            Recovery.AutoSavePathOverride = Path.Combine(tmp, "autosave.ink");
        if (PptStore.RootOverride == null)
            PptStore.RootOverride = Path.Combine(tmp, "ppt");
        if (DocStore.RootOverride == null)
            DocStore.RootOverride = Path.Combine(tmp, "doc");
        if (BoardHistory.DirOverride == null)
            BoardHistory.DirOverride = Path.Combine(tmp, "history");
        DiagLog.Retarget(Path.Combine(tmp, "logs"));
    }
}
