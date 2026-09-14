using System.Diagnostics;

// Measures the memory floor of a bare .NET desktop process, so the InkTeach
// numbers can be split into "runtime overhead" vs "our own graphics stack".
var p = Process.GetCurrentProcess();
p.Refresh();
Console.WriteLine($"trivial .NET console app: working set {p.WorkingSet64 / 1048576.0:F1} MB, "
                + $"private {p.PrivateMemorySize64 / 1048576.0:F1} MB, managed heap {GC.GetTotalMemory(true) / 1048576.0:F1} MB");
