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
/// ⚠ **线程安全**：PDFium 自己不是线程安全的。本仓库只在渲染线程调它
///（所有自检/产品路径都在这条线程），但这里仍加一把静态锁兜底——
/// 宁可多一次无关紧要的 lock，也不要将来某天在后台线程里踩到它。
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
    private static readonly object Gate = new();
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

    // ---- 给 PdfiumDoc 用的内部帮手（都在锁里）--------------------------------------
    internal static bool PageSize(IntPtr doc, int index, out float w, out float h)
    {
        var size = default(FS_SIZEF);
        bool ok = FPDF_GetPageSizeByIndexF(doc, index, ref size);
        w = size.Width;
        h = size.Height;
        return ok;
    }

    internal static void CloseDoc(IntPtr doc) => FPDF_CloseDocument(doc);

    /// <summary>
    /// 渲一页进**我们自己的数组**：白底、按输出尺寸缩放（PDFium 自己缩），返回 BGRA。
    /// </summary>
    internal static unsafe byte[] Render(IntPtr doc, int index, int outW, int outH)
    {
        if (outW <= 0 || outH <= 0) return null;
        var buf = new byte[(long)outW * outH * 4];

        lock (Gate)
        {
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
        }
        return buf;
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

    /// <summary>页尺寸（点，1pt = 1/72 英寸）。读不到就给 A4 兜底。</summary>
    public (float W, float H) Size(int index)
    {
        if (index < 0 || index >= PageCount) return (595f, 842f);
        if (_w[index] <= 0.5f)
        {
            if (!Pdfium.PageSize(_doc, index, out float w, out float h) || w < 0.5f || h < 0.5f)
            {
                w = 595f; h = 842f;
            }
            _w[index] = w;
            _h[index] = h;
        }
        return (_w[index], _h[index]);
    }

    public byte[] RenderPage(int index, int outW, int outH)
        => _doc == IntPtr.Zero ? null : Pdfium.Render(_doc, index, outW, outH);

    public void Dispose()
    {
        if (_doc != IntPtr.Zero)
        {
            Pdfium.CloseDoc(_doc);
            _doc = IntPtr.Zero;
        }
    }
}
