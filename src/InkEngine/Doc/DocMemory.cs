using System;

namespace InkEngine;

/// <summary>
/// PDF / 文档页的内存档案（2026-10-17）。
///
/// 结论：PDF 的内存大头是**页位图**（BGRA，4 字节/像素），不是 PDFium。
/// 1080p 一页 A4 约 1920×2717×4 = 20.9MB；旧策略“视口上下各一屏”最多同时
/// 驻留 3 页 ≈ 63MB。双屏扩展时页宽按整个虚拟桌面算，单页能到 83MB。
///
/// 新策略（所有机器统一）：
///   · 静止只驻留**当前视口相交的页**（通常 1 页，跨页缝时 2 页）；
///   · 翻页动画期间**钉住目标页**，后台渲染，动画结束就只剩目标页；
///   · 渲染宽度按内存分档，避免 4GB/4K 机器被一页 80MB 打爆。
/// </summary>
internal enum DocMemoryMode { Auto, Low, Standard, High }

internal static class DocMemory
{
    /// <summary>命令行覆盖：--pdflowmem / --pdfhighmem（诊断、真机对照用）。</summary>
    public static DocMemoryMode Force = DocMemoryMode.Auto;

    static int Ram => DiagLog.RamGb;

    public static bool Low => Force == DocMemoryMode.Low
        || (Force == DocMemoryMode.Auto && Ram > 0 && Ram <= 4);
    public static bool High => Force == DocMemoryMode.High
        || (Force == DocMemoryMode.Auto && Ram > 8);

    /// <summary>PDF 页的渲染宽度上限。0 = 不额外限制（用主屏物理宽）。</summary>
    public static int RenderWidthCap => Low ? 1600 : High ? 0 : 1920;

    /// <summary>
    /// 单页**基准**像素预算（极端长页/超大幅面按它缩；防止一页 96MB）。
    ///
    /// "基准"= 100% 时的位图尺寸。导入时只按它缩，**不改 100% 的观感**
    /// （缩放是后加的一层，见 <see cref="PageRenderScaleCap"/>）。
    /// </summary>
    public static int MaxPagePixelsBase => Low ? 8_000_000 : 12_000_000;

    /// <summary>
    /// 单页像素预算的**上限**（含缩放放大后的位图）。用 200% 时的一页 A4 反推：
    ///   · 2520 宽屏（本机）A4 = 2520×3566 ≈ 9.0MP，2× = 35.9MP ≈ 143MB；
    ///   · 1920 宽屏 A4 2× ≈ 20.9MP ≈ 83MB。
    /// 所以高清档给 36MP（16GB 机器只驻留一页，能吃得下）；标准档 24MP；
    /// 省内存档 9MP（150% 刚好够）。超过它的页在缩放时自动降倍率，极端长页仍被夹住。
    /// </summary>
    public static int MaxPagePixels => Low ? 9_000_000 : High ? 36_000_000 : 24_000_000;

    /// <summary>
    /// 文档缩放时页位图的**重渲染倍率上限**（历史口径：≤100% 时一律 1，只缩不合，不重渲）。
    /// 2026-11-16 起页位图不再按缩放重渲（见 <see cref="DocPatchCache"/>），
    /// 这一位只留给 `--docmem` 报告和旧自检读数，不再参与任何渲染决定。
    /// </summary>
    public static float PageRenderScaleCap => Low ? 1.5f : 2.0f;

    /// <summary>
    /// 页缓存窗口 = 视口本身（0 屏余量）。静止 1 页；跨页缝/翻页时最多 2 页。
    /// </summary>
    public const float CacheMarginScreens = 0f;

    /// <summary>PDF 翻页动画时长：低配 1 秒（给扫描版 PDF 留渲染时间）；高配 450ms 更跟手。</summary>
    public static double FlipMs => Low ? 1000.0 : 450.0;

    public static string Name => Low ? "省内存" : High ? "高清" : "标准";

    /// <summary>按主屏物理宽和档位算 PDF 实际渲染宽。</summary>
    public static int LayoutWidth(int primaryW)
    {
        int cap = RenderWidthCap;
        int w = cap > 0 ? Math.Min(primaryW, cap) : primaryW;
        return Math.Max(1, w);
    }

    public static string Note
    {
        get
        {
            int primary = 0;
            try { primary = Native.GetSystemMetrics(0); } catch { /* 有意忽略：失败不影响主流程（ADR-005 D5） */ }
            return $"{Name}/<={LayoutWidth(primary)}px/cache1/flip{FlipMs:F0}ms";
        }
    }
}
