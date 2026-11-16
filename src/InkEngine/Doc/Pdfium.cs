using System.Runtime.InteropServices;
using System.Text;

namespace InkEngine;

/// <summary>
/// PDFium 的最小接入：**P/Invoke 直连，不走 PDFiumSharp 之类的托管包装**。
///
/// 为什么自己写这 11 个函数（而不是引一个包）：
///   ① NativeAOT 下纯 DllImport 最稳（无 COM/WinRT 投影、无反射、无 trim 风险）；
///   ② 我们只要 5 件事——开文档 / 页数 / 页尺寸 / 渲一页 / 关文档；
///   ③ 渲染**直接进我们自己分配的 BGRA 缓冲**（FPDFBitmap_CreateEx 包我们的数组），
///      零额外拷贝，出门就是 <see cref="ImageData.Adopt"/> 要的格式。
///
/// 许可：PDFium 本体 BSD-3-Clause，第三方（freetype/harfbuzz/icu/libjpeg 等）全宽松。
/// 详见 src/InkEngine/THIRD-PARTY-NOTICES.md。
///
/// ⚠ **线程安全**：PDFium 自己不是线程安全的，而本仓库现在**真的**有两条线程碰它
/// （补丁 worker 在后台渲一块区域，UI 线程开/关文档、读页尺寸）。所以：
///   · 所有碰句柄的调用都在 <see cref="Gate"/> 里（开、关、页尺寸、整页渲、区域渲）；
///   · **句柄是否为空也在锁里查**——"读字段 → 调原生"之间句柄可能刚被另一条线程关掉，
///     在外面查等于没查（2026-11-16 评审 P0-1）。
/// </summary>
internal static class Pdfium
{
    private const string Lib = "pdfium";

    // ---- FPDFBitmap 格式与渲染标志（fpdfview.h）--------------------------------
    private const int FPDFBitmap_BGRA = 4;
    private const uint FPDF_ANNOT = 0x01;          // 把 PDF 自带的批注也画出来
    private const uint White = 0xFFFFFFFF;

    /// <summary>单页位图像素上限（约 24MP ≈ 96MB CPU）：极端长页/超大幅面按它缩，别把内存吃掉。</summary>
    public const long MaxPagePixels = 24_000_000;

    // ---- 最小接口面 ------------------------------------------------------------------
    /// <summary>
    /// 初始化配置（fpdfview.h：`version` 必须 = 2；其余 0 = 用默认字体路径、自己建 isolate）。
    /// 官方 getting-started 与 PDFiumSharp 的布局一致：
    /// version / m_pUserFontPaths / m_pIsolate / m_v8EmbedderSlot。
    /// （不直接用 `FPDF_InitLibrary()`：头文件明说那是兼容壳、将来废弃。）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FPDF_LIBRARY_CONFIG
    {
        public int Version;
        public IntPtr UserFontPaths;
        public IntPtr Isolate;
        public uint V8EmbedderSlot;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibraryWithConfig(ref FPDF_LIBRARY_CONFIG config);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibrary();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadDocument(byte[] filePathUtf8, byte[] passwordUtf8);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetPageCount(IntPtr document);

    /// <summary>
    /// ⚠ `FPDF_GetPageSizeByIndexF(doc, index, FS_SIZEF* size)` 的第三个参数是**结构体指针**。
    /// 第一版照着"两个 out float"写（看着像）→ 原生代码往第一个 out 的地址写 8 字节，
    /// 栈被踩 → 打开 PDF 后**访问违例闪退**（2026-10-07 实测，自检当场抓住）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FS_SIZEF
    {
        public float Width, Height;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, ref FS_SIZEF size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format,
                                                     IntPtr firstScan, int stride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FPDFBitmap_FillRect(IntPtr bitmap, int left, int top,
                                                   int width, int height, uint color);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page,
                                                     int startX, int startY, int sizeX, int sizeY,
                                                     int rotate, int flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint FPDF_GetLastError();

    // ---- 加载与就绪 ------------------------------------------------------------------
    /// <summary>
    /// 全局锁：PDFium 不是线程安全的，**所有**碰文档句柄的调用都要在它里面。
    /// internal 是为了让 <see cref="PdfiumDoc"/> 的 Dispose/Size 也用同一把锁。
    /// </summary>
    internal static readonly object Gate = new();
    private static bool _tried;
    private static string _loadError;

    /// <summary>
    /// 确保 pdfium.dll 能加载（并初始化一次库）。返回 false 时 <paramref name="error"/>
    /// 是一句给用户看的提示。**懒加载**：第一次真要用 PDF 才走这里（不调用 = 零开销）。
    /// </summary>
    public static bool EnsureLoaded(out string error)
    {
        lock (Gate)
        {
            error = _loadError;
            if (_tried) return _loadError == null;
            _tried = true;

            try
            {
                PreloadIfNotBesideExe();
                // 官方姿势（getting-started）：version = 2，其余全零。
                // 老一点的构建没有 WithConfig 这个入口时才退回兼容壳。
                try
                {
                    var cfg = new FPDF_LIBRARY_CONFIG { Version = 2 };
                    FPDF_InitLibraryWithConfig(ref cfg);
                }
                catch (EntryPointNotFoundException)
                {
                    FPDF_InitLibrary();
                }
                return true;
            }
            catch (Exception ex)
            {
                _loadError = "找不到 PDFium 原生库（跑 tools\\fetch-pdfium.ps1 取回，或重装本软件）：" + ex.Message;
                error = _loadError;
                Console.WriteLine("    [文档] " + _loadError);
                return false;
            }
        }
    }

    /// <summary>
    /// 开发兜底：exe 旁边没有 pdfium.dll 时，从 exe 往上找 `vendor\pdfium-win-x64\bin\pdfium.dll`
    /// 手动 Load 一次——之后 `DllImport("pdfium")` 会命中这个已加载的模块。
    /// （发布版由 publish.ps1 把 dll 放在 exe 旁边，这条路走不到。）
    /// </summary>
    private static void PreloadIfNotBesideExe()
    {
        string baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "pdfium.dll"))) return;

        var d = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "vendor", "pdfium-win-x64", "bin", "pdfium.dll");
            if (File.Exists(p))
            {
                NativeLibrary.Load(p);
                Console.WriteLine("    [文档] pdfium.dll 来自开发目录：" + p);
                return;
            }
        }
    }

    /// <summary>UTF-8 + NUL 结尾（FPDF_LoadDocument 的文件名按 UTF-8 解释，中文路径必须这么传）。</summary>
    private static byte[] Utf8Z(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var b = Encoding.UTF8.GetBytes(s);
        var z = new byte[b.Length + 1];
        Array.Copy(b, z, b.Length);
        return z;
    }

    // ======================================================================
    //  文档
    // ======================================================================

    /// <summary>打开一份 PDF。失败时 <paramref name="error"/> 是一句提示（加密 / 损坏都走这里）。</summary>
    public static bool Open(string path, out PdfiumDoc doc, out string error)
    {
        doc = null;
        error = null;
        if (!EnsureLoaded(out error)) return false;

        lock (Gate)
        {
            IntPtr handle = IntPtr.Zero;
            try { handle = FPDF_LoadDocument(Utf8Z(path), null); }
            catch (Exception ex) { error = "打开 PDF 失败：" + ex.Message; return false; }

            if (handle == IntPtr.Zero)
            {
                uint code = FPDF_GetLastError();
                error = code switch
                {
                    1 => "文件读不了（不存在或没权限）",
                    2 => "不是有效的 PDF 文件",
                    3 => "PDF 文件损坏",
                    4 => "PDF 有密码，暂不支持",
                    _ => $"打开 PDF 失败（错误码 {code}）",
                };
                return false;
            }

            int count = FPDF_GetPageCount(handle);
            if (count <= 0)
            {
                FPDF_CloseDocument(handle);
                error = "这份 PDF 一页都没有";
                return false;
            }

            doc = new PdfiumDoc(handle, count);
            return true;
        }
    }

    // ---- 给 PdfiumDoc 用的内部帮手（都在锁里，句柄也在锁里查）----------------------

    /// <summary>
    /// 页尺寸。**锁里读句柄**：关文档那条线程可能正在关它，锁外读到的句柄
    /// 下一秒就可能是野指针。
    /// </summary>
    internal static bool PageSize(PdfiumDoc owner, int index, out float w, out float h)
    {
        w = 595f;
        h = 842f;
        if (owner == null) return false;
        lock (Gate)
        {
            IntPtr doc = owner.Handle;
            if (doc == IntPtr.Zero) return false;
            var size = default(FS_SIZEF);
            bool ok = FPDF_GetPageSizeByIndexF(doc, index, ref size);
            if (ok) { w = size.Width; h = size.Height; }
            return ok;
        }
    }

    /// <summary>关文档（和渲染同一把锁；句柄为空不调原生）。</summary>
    internal static void CloseDoc(IntPtr doc)
    {
        if (doc == IntPtr.Zero) return;
        lock (Gate) FPDF_CloseDocument(doc);
    }

    /// <summary>
    /// 渲一页进**我们自己的数组**：白底、按输出尺寸缩放（PDFium 自己缩），返回 BGRA。
    /// </summary>
    internal static unsafe byte[] Render(PdfiumDoc owner, int index, int outW, int outH)
    {
        if (owner == null || outW <= 0 || outH <= 0) return null;

        lock (Gate)
        {
            IntPtr doc = owner.Handle;      // 锁里再查：句柄可能已经被关文档那条线程放掉
            if (doc == IntPtr.Zero) return null;
            var buf = new byte[(long)outW * outH * 4];

            fixed (byte* p = buf)
            {
                IntPtr bitmap = FPDFBitmap_CreateEx(outW, outH, FPDFBitmap_BGRA, (IntPtr)p, outW * 4);
                if (bitmap == IntPtr.Zero) return null;
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, outW, outH, White);
                    IntPtr page = FPDF_LoadPage(doc, index);
                    if (page == IntPtr.Zero) return null;
                    try
                    {
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, outW, outH, 0, (int)FPDF_ANNOT);
                    }
                    finally { FPDF_ClosePage(page); }
                }
                finally { FPDFBitmap_Destroy(bitmap); }
            }
            return buf;
        }
    }
    /// <summary>
    /// 渲一页的**一块区域**（设备像素）进我们自己的数组：白底、整页按
    /// <paramref name="fullW"/>×<paramref name="fullH"/> 缩放，只有
    /// [rx,rx+rw)×[ry,ry+rh) 那块落进缓冲。
    ///
    /// 做法：`FPDF_RenderPageBitmap` 的 startX/startY 是**目标位图里的起点**，
    /// 负值把整页坐标平移过来（PDFium 会按设备裁剪自动只画落在缓冲里的部分），
    /// 所以 `startX = -rx` 就是"整页渲完后裁出这一块"。
    /// **不用 `FPDF_RenderPageBitmapWithMatrix`**：矩阵版对裁剪/旋转/表单的行为
    /// 和整页版不是一套（缩放过 1 倍以上时字重会变），这里要和底图逐像素同一套渲染路径。
    /// 区域渲染和整页渲染在重叠区必须逐像素一致（`--zoomtest` 有对照断言）。
    /// </summary>
    internal static unsafe byte[] RenderRegion(PdfiumDoc owner, int index, int fullW, int fullH,
                                               int rx, int ry, int rw, int rh)
    {
        if (owner == null || fullW <= 0 || fullH <= 0 || rw <= 0 || rh <= 0) return null;
        if (rx < 0) rx = 0;
        if (ry < 0) ry = 0;
        if (rx >= fullW || ry >= fullH) return null;
        if (rw > fullW - rx) rw = fullW - rx;
        if (rh > fullH - ry) rh = fullH - ry;

        long bytes = (long)rw * rh * 4;
        if (bytes <= 0 || bytes > int.MaxValue) return null;

        lock (Gate)
        {
            IntPtr doc = owner.Handle;      // 锁里再查（同上：补丁 worker 和关文档是两条线程）
            if (doc == IntPtr.Zero) return null;
            var buf = new byte[(int)bytes];

            fixed (byte* p = buf)
            {
                IntPtr bitmap = FPDFBitmap_CreateEx(rw, rh, FPDFBitmap_BGRA, (IntPtr)p, rw * 4);
                if (bitmap == IntPtr.Zero) return null;
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, rw, rh, White);
                    IntPtr page = FPDF_LoadPage(doc, index);
                    if (page == IntPtr.Zero) return null;
                    try
                    {
                        FPDF_RenderPageBitmap(bitmap, page, -rx, -ry, fullW, fullH, 0, (int)FPDF_ANNOT);
                    }
                    finally { FPDF_ClosePage(page); }
                }
                finally { FPDFBitmap_Destroy(bitmap); }
            }
            return buf;
        }
    }
}

/// <summary>
/// 一份打开的 PDF：页数 + 每页尺寸（点）+ 按需渲染。
/// 生命周期跟着"当前文档"走（<c>InkEngine.CloseDocument</c> 里 Dispose）。
/// </summary>
internal sealed class PdfiumDoc : IDisposable
{
    private IntPtr _doc;
    private readonly float[] _w, _h;

    public int PageCount { get; }

    internal PdfiumDoc(IntPtr doc, int pageCount)
    {
        _doc = doc;
        PageCount = pageCount;
        _w = new float[pageCount];
        _h = new float[pageCount];
    }

    /// <summary>
    /// 原生句柄（0 = 已经关了）。⚠ **只在 <see cref="Pdfium.Gate"/> 里读**：
    /// 锁外读到非 0 不代表下一秒还非 0——关文档那条线程随时会把它关掉。
    /// </summary>
    internal IntPtr Handle => _doc;

    /// <summary>页尺寸（点，1pt = 1/72 英寸）。读不到就给 A4 兜底。</summary>
    public (float W, float H) Size(int index)
    {
        if (index < 0 || index >= PageCount) return (595f, 842f);
        if (_w[index] <= 0.5f)
        {
            if (!Pdfium.PageSize(this, index, out float w, out float h) || w < 0.5f || h < 0.5f)
            {
                w = 595f; h = 842f;
            }
            _w[index] = w;
            _h[index] = h;
        }
        return (_w[index], _h[index]);
    }

    public byte[] RenderPage(int index, int outW, int outH)
        => Pdfium.Render(this, index, outW, outH);

    /// <summary>
    /// 渲第 <paramref name="index"/> 页的一块区域（设备像素；见 <see cref="Pdfium.RenderRegion"/>）。
    /// 高清补丁只走这一条——整页 200% 重渲是 143MB，而这里通常只有一块屏。
    /// </summary>
    public byte[] RenderRegion(int index, int fullW, int fullH, int rx, int ry, int rw, int rh)
        => Pdfium.RenderRegion(this, index, fullW, fullH, rx, ry, rw, rh);

    /// <summary>
    /// 关文档。**锁里把句柄摘掉、再在同一把锁里关**：
    /// 渲染线程可能正拿着它渲（它整段都在锁里），这里等它出来；
    /// 摘掉之后新来的渲染在锁里查到 0 就直接返回 null，不会碰已释放的文档。
    /// </summary>
    public void Dispose()
    {
        if (_doc == IntPtr.Zero) return;
        IntPtr doc;
        lock (Pdfium.Gate)
        {
            doc = _doc;
            _doc = IntPtr.Zero;
        }
        Pdfium.CloseDoc(doc);
    }
}
