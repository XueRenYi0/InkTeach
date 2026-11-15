// [职责] 自检模式注册表：总表与查找（ADR-005 D2）。
// [测试] --list-modes / --unittest

#nullable enable annotations

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using InkEngine;
namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 一个自检模式：mode 字符串、help 说明、执行体。
    /// `Run` 返回 null = 交给正常消息循环；返回非 null = 直接作为退出码。
    /// </summary>
    private readonly record struct ModeEntry(string Mode, string Help, Func<string[], int?> Run);

    private ModeEntry[]? _modeEntries;
    private ModeEntry[] ModeEntries => _modeEntries ??= BuildModeEntries();

    private ModeEntry[] BuildModeEntries()
    {
        var all = new List<ModeEntry>(200);
        all.AddRange(BuildModeTableDev());
        all.AddRange(BuildModeTableDoc());
        all.AddRange(BuildModeTableInk());
        all.AddRange(BuildModeTableShape());
        all.AddRange(BuildModeTableSys());
        all.AddRange(BuildModeTableUi());
        return all.ToArray();
    }

    private ModeEntry? FindMode(string mode)
    {
        foreach (var e in ModeEntries)
            if (string.Equals(e.Mode, mode, StringComparison.Ordinal)) return e;
        return null;
    }
}
