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

    /// <summary>单页像素预算（极端长页/超大幅面按它缩；防止一页 96MB）。</summary>
    public static int MaxPagePixels => Low ? 8_000_000 : 12_000_000;

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
            try { primary = Native.GetSystemMetrics(0); } catch { }
            return $"{Name}/<={LayoutWidth(primary)}px/cache1/flip{FlipMs:F0}ms";
        }
    }
}
