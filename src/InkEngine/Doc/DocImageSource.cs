using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 图片文件 → 页配方 / 页位图（**GDI+ 解码**）。
///
/// ## 为什么用 GDI+（而不是 WIC / Skia）
/// `System.Drawing.Common` 已经是本仓库的依赖（导出那条路在用，NativeAOT 下也验过），
/// 零新增包；PNG / JPG / BMP / GIF（首帧）/ TIFF 全都能读。**引擎核心不引第三方解码器**。
/// 代价：GDI+ 的解码是"整张全解"，大图有瞬时峰值——对策见下面的"源缓存"与导入时的降采样。
///
/// ## 三件必须做的事
///   ① **EXIF 方向**：手机照片的"正"在 EXIF 的 0x0112 里，不读它，竖着一张横着进来；
///   ② **白底合成**：带透明的 PNG 直接上覆盖层会透出桌面——先铺白再画；
///   ③ **降采样**：按屏幕物理像素算，"这台电脑上能看清"为准（10-03 已定），
///      像素账 = 宽×高×4（200% 屏上一张 A4 就是 35.9 MB）。
///
/// ## 源缓存（一张，≤12MP）
/// 切片页是"从同一张原图里裁不同条"——解一次留一份，滚回来重生成就不用再解。
/// 超过 12MP 的不留（那正是要省的内存）；<see cref="TrimCache"/> 在关文档时清。
/// **单线程使用**（渲染线程调用），和引擎其它部分同一条纪律。
/// </summary>
internal static class DocImageSource
{
    /// <summary>"打开文档…"里认的图片扩展名（过滤器和判断共用一份）。</summary>
    public static bool IsImage(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = System.IO.Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" => true,
            _ => false,
        };
    }

    /// <summary>宽或高超过 2 屏就切片（见 PlanSpecs）。</summary>
    private const int SliceScreens = 2;

    /// <summary>放大上限：小图撑宽到 2×，再大就是糊，不如两边留白。</summary>
    private const float MaxUpscale = 2.0f;

    /// <summary>源缓存上限（像素）。12MP ≈ 48MB。</summary>
    private const long MaxCachePixels = 12_000_000;

    // ======================================================================
    //  尺寸 / EXIF
    // ======================================================================

    /// <summary>
    /// 一张已解码的图 + **它的源流**。两个必须同生共死（查过同行经验，两个坑我们都堵）：
    ///   · `new Bitmap(path)` 会**锁住文件直到 Bitmap 释放**（MSDN 明说）——老师想换图/删图就动不了；
    ///   · `Image.FromStream(fs)` 又要求**流在 Bitmap 活着的全程都开着**（GDI+ 延迟解码、随时回读源流）。
    /// 所以：用 `FileShare.ReadWrite|Delete` 把字节复制进 MemoryStream（文件句柄立刻还回去），
    /// Bitmap 拿这个内存流解码，**流陪着它、一起缓存、一起释放**。
    /// </summary>
    private sealed class LoadedImage : IDisposable
    {
        public Bitmap Bitmap;
        public MemoryStream Stream;

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Stream?.Dispose();
            Stream = null;
        }
    }

    private static LoadedImage LoadSafe(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var ms = new MemoryStream((int)Math.Min(fs.Length, int.MaxValue));
            fs.CopyTo(ms);
            ms.Position = 0;
            try
            {
                var bmp = new Bitmap(ms);
                return new LoadedImage { Bitmap = bmp, Stream = ms };
            }
            catch
            {
                ms.Dispose();
                throw;
            }
        }
        finally
        {
            fs.Dispose();
        }
    }

    /// <summary>
    /// 读尺寸与 EXIF 方向，返回**转正之后**的宽高（方向 5~8 会交换宽高）。
    /// 文件损坏 / 格式不认返回 false。
    /// </summary>
    public static bool TryReadInfo(string path, out int w, out int h)
    {
        w = h = 0;
        try
        {
            using var img = LoadSafe(path);
            int o = ReadOrientation(img.Bitmap);
            w = img.Bitmap.Width; h = img.Bitmap.Height;
            if (o >= 5 && o <= 8) (w, h) = (h, w);
            return w > 0 && h > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    [文档] 读图失败：{path}：{ex.Message}");
            return false;
        }
    }

    private static int ReadOrientation(Bitmap bmp)
    {
        try
        {
            if (Array.IndexOf(bmp.PropertyIdList, 0x0112) >= 0)
            {
                var p = bmp.GetPropertyItem(0x0112);
                if (p?.Value != null && p.Value.Length >= 2) return p.Value[0] | (p.Value[1] << 8);
            }
        }
        catch { }
        return 1;
    }

    /// <summary>
    /// EXIF 方向 → GDI+ 的旋转翻转（照业界那张标准表）。
    /// 拆成 internal 是为了能自检（造 8 种方向的小图直接验映射，不依赖 EXIF 往返）。
    /// </summary>
    internal static void ApplyOrientation(Bitmap bmp, int orientation)
    {
        switch (orientation)
        {
            case 2: bmp.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
            case 3: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 4: bmp.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
            case 5: bmp.RotateFlip(RotateFlipType.Rotate90FlipX); break;
            case 6: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 7: bmp.RotateFlip(RotateFlipType.Rotate270FlipX); break;
            case 8: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
        }
    }

    // ======================================================================
    //  布局：一图 → 一页或几页（长图切片）
    // ======================================================================

    /// <summary>
    /// 一张图铺成几页（10-03 已定 + 2026-10-07 长图补充）：
    ///   · **fit width**：页宽 = 屏宽（小图最多放大 <see cref="MaxUpscale"/>×，不无限放大）；
    ///   · 页高 ≤ 2 屏 → **一页**（往下滚着看）；
    ///   · 页高 &gt; 2 屏 → 按 **1 屏高**切片，一片一页（1080×32000 的长截图 = 32 片，原样清晰）。
    ///
    /// 切片的源矩形按"缩放前"的像素算，前后片首尾相接、不重不漏（自检盯着）。
    /// </summary>
    public static List<DocPages.Spec> PlanSpecs(string path, int imgW, int imgH, float screenW, float screenH)
    {
        var list = new List<DocPages.Spec>();
        if (imgW <= 0 || imgH <= 0) return list;

        float scale = MathF.Min(screenW / imgW, MaxUpscale);
        if (!(scale > 0.01f)) scale = 1f;

        int outW = Math.Max(1, (int)MathF.Round(imgW * scale));
        int total = Math.Max(1, (int)MathF.Round(imgH * scale));
        int sliceH = Math.Max(1, (int)MathF.Round(screenH));
        int n = total <= sliceH * SliceScreens ? 1 : (int)Math.Ceiling(total / (float)sliceH);

        for (int k = 0; k < n; k++)
        {
            int dy0 = k * sliceH;
            int dy1 = n == 1 ? total : Math.Min(total, dy0 + sliceH);
            if (dy1 <= dy0) break;
            float sy0 = dy0 / scale, sy1 = dy1 / scale;
            list.Add(new DocPages.Spec
            {
                Kind = 0,
                Source = path,
                SourceIndex = k,
                SrcX = 0f,
                SrcY = sy0,
                SrcW = imgW,
                SrcH = sy1 - sy0,
                OutW = outW,
                OutH = dy1 - dy0,
            });
        }

        // 最后一片太薄（< 1/4 屏）就并进前一片：不然"比 2 屏多一点"的图会切出
        // 一条 2 像素的碎页（5 片），并完是 4 片——观感和翻页都顺。
        if (list.Count >= 2)
        {
            var last = list[^1];
            if (last.OutH < sliceH / 4)
            {
                var prev = list[^2];
                prev.SrcH += last.SrcH;
                prev.OutH += last.OutH;
                list.RemoveAt(list.Count - 1);
            }
        }
        return list;
    }

    // ======================================================================
    //  渲染：配方 → BGRA
    // ======================================================================

    /// <summary>
    /// 生成一页的像素：解码（或取缓存）→ 裁源矩形 → 缩放到目标尺寸 → 白底合成 → BGRA。
    /// 失败返回 null（调用方会把这一页标成"失败"，一句浅红占位）。
    /// </summary>
    public static byte[] RenderSpec(DocPages.Spec spec)
    {
        var img = GetImage(spec.Source);
        if (img == null) return null;
        try
        {
            return RenderRegion(img.Bitmap, spec.SrcX, spec.SrcY, spec.SrcW, spec.SrcH, spec.OutW, spec.OutH);
        }
        finally
        {
            // 缓存持有它 → 不放；超大图（没进缓存）→ 这次用完就放
            if (!ReferenceEquals(img, _cache)) img.Dispose();
        }
    }

    private static LoadedImage _cache;
    private static string _cacheKey;

    /// <summary>关文档时清掉解码缓存（"用完释放"）。</summary>
    public static void TrimCache()
    {
        _cache?.Dispose();
        _cache = null;
        _cacheKey = null;
    }

    /// <summary>取解码结果（带 EXIF 转正）。命中缓存直接给；没命中就解一张，小的留下。</summary>
    private static LoadedImage GetImage(string path)
    {
        string key = path;
        try { key += "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        if (_cache != null)
        {
            if (_cacheKey == key) return _cache;
            _cache.Dispose();
            _cache = null;
            _cacheKey = null;
        }

        LoadedImage img = null;
        try
        {
            img = LoadSafe(path);
            ApplyOrientation(img.Bitmap, ReadOrientation(img.Bitmap));
        }
        catch (Exception ex)
        {
            img?.Dispose();
            Console.WriteLine($"    [文档] 解码失败：{path}：{ex.Message}");
            return null;
        }

        if ((long)img.Bitmap.Width * img.Bitmap.Height <= MaxCachePixels)
        {
            _cache = img;
            _cacheKey = key;
        }
        return img;
    }

    /// <summary>裁一块源区域、缩放、白底合成 → BGRA（预乘不必：alpha 全 255）。</summary>
    private static byte[] RenderRegion(Bitmap src, float sx, float sy, float sw, float sh, int outW, int outH)
    {
        if (outW <= 0 || outH <= 0 || sw <= 0 || sh <= 0) return null;

        // 防御：源矩形夹进图内（浮点取整可能差半个像素）
        sx = Math.Clamp(sx, 0f, Math.Max(0f, src.Width - 1f));
        sy = Math.Clamp(sy, 0f, Math.Max(0f, src.Height - 1f));
        sw = Math.Clamp(sw, 1f, src.Width - sx);
        sh = Math.Clamp(sh, 1f, src.Height - sy);

        using var dst = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.White);      // ① 白底：透明 PNG 不能透出桌面
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, outW, outH), sx, sy, sw, sh, GraphicsUnit.Pixel);
        }

        // GDI+ 的 32bppArgb 在内存里就是 BGRA（小端 ARGB），D2D 直接吃。
        var px = new byte[(long)outW * outH * 4];
        var rect = new Rectangle(0, 0, outW, outH);
        var data = dst.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < outH; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * outW * 4, outW * 4);
        }
        finally
        {
            dst.UnlockBits(data);
        }
        return px;
    }
}
