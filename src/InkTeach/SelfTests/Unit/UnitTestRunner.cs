using System.Diagnostics;
using System.Runtime.CompilerServices;
using InkEngine;

namespace InkTeach;

/// <summary>
/// 给纯逻辑用的最小单元测试框架（零 NuGet 依赖、AOT 安全）。
///
/// 为什么不用 xUnit：
///   1. 仓库规矩是不新增包、不改 csproj；本机外网受限，还原新包有风险。
///   2. 新建独立测试项目会绕过宿主现成的临时目录隔离，测试可能直接读写
///      用户真实的 settings / 板书 / 历史目录。
///
/// 为什么用**显式注册表**而不是反射：
///   NativeAOT 会裁剪 `Assembly.GetTypes()` 找不到的类型；反射版在 AOT 产物里
///   一条测试都找不到（2026-11-15 AOT 冒烟实测）。显式列表让测试永远在。
///
/// 用法：
///   .\run.ps1 --unittest            跑全部
///   .\run.ps1 --unittest EditAction 只跑名字里含 EditAction 的
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
    /// <summary>
    /// 测试注册表。新增测试：在 UnitTests.*.cs 里写 internal static void，
    /// 然后在这里加一行。**不要改回反射**（AOT 会裁掉）。
    /// </summary>
    private static readonly (string Name, Action Body)[] Tests =
    {
        (nameof(UnitTestsCore.CollectionFingerprint_SameContent_SameKey),
            UnitTestsCore.CollectionFingerprint_SameContent_SameKey),
        (nameof(UnitTestsCore.EditAction_AddStrokes_UndoRedo),
            UnitTestsCore.EditAction_AddStrokes_UndoRedo),
        (nameof(UnitTestsCore.Serializer_Format_Is_Stable),
            UnitTestsCore.Serializer_Format_Is_Stable),
        (nameof(UnitTestsCore.Serializer_RoundTrip_PreservesCountAndColor),
            UnitTestsCore.Serializer_RoundTrip_PreservesCountAndColor),
        (nameof(UnitTestsCore.UiScale_Snap_To_Nearest_Tier),
            UnitTestsCore.UiScale_Snap_To_Nearest_Tier),
    };

    public static int Run(string[] args)
    {
        string filter = args.Length > 1 ? args[1] : "";
        ApplyIsolation();

        var selected = new List<(string Name, Action Body)>();
        foreach (var t in Tests)
            if (filter.Length == 0
                || t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                selected.Add(t);

        if (selected.Count == 0)
        {
            Console.WriteLine($"没有匹配 \"{filter}\" 的单元测试");
            return 1;
        }

        int failed = 0;
        var all = Stopwatch.StartNew();
        foreach (var t in selected)
        {
            var one = Stopwatch.StartNew();
            try
            {
                t.Body();
                Console.WriteLine($"PASS {t.Name} ({one.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL {t.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"unit tests: {selected.Count - failed} passed / {failed} failed / {all.ElapsedMilliseconds} ms");
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
