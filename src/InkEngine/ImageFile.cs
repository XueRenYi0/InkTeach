using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 图片文件 → 画布 / 收藏用的 BGRA（**保留透明通道**）。
///
/// 和 <see cref="DocImageSource"/> 的区别：
///   · 文档页要“白底合成”，因为一页纸不能透出桌面；
///   · 贴纸 / 透明 PNG 必须保留 alpha，所以这里**不铺白底**，直接画到透明画布上。
///
/// 解码仍走 GDI+：PNG / JPG / BMP / GIF（首帧）/ TIFF 都能读，不额外引包。
/// EXIF 方向在这里转正；大图按最长边和总像素数缩小，避免收藏里一条贴纸就占几十 MB。
/// </summary>
internal static class ImageFile
{
    /// <summary>打开对话框的过滤器（第一项专门给透明贴纸）。</summary>
    public const string OpenFilter =
        "透明贴纸 / PNG（推荐）\0*.png\0"
      + "图片（PNG / JPG / BMP / GIF / TIFF）\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff\0\0";

    /// <summary>最长边上限。再大对课堂投影没有意义，只会让收藏读盘变慢。</summary>
    private const int MaxSide = 2048;
    private const long MaxPixels = (long)MaxSide * MaxSide;
    private const long MaxFileBytes = 64L * 1024 * 1024;

    public sealed class Result
    {
        public int Width;
        public int Height;
        /// <summary>**预乘 alpha** 的 BGRA，直接交给 <see cref="ImageData.AdoptPremultiplied"/>。</summary>
        public byte[] Bgra;
        public bool HasAlpha;
        public string Error;

        public bool Success => Bgra != null && Width > 0 && Height > 0;
    }

    /// <summary>读一张图，转成**预乘 alpha** 的 BGRA（缩放也在预乘空间里做，透明边缘不发白）。</summary>
    public static Result Load(string path)
    {
        var result = new Result();
        if (string.IsNullOrWhiteSpace(path))
        {
            result.Error = "没有文件";
            return result;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                result.Error = "文件不存在";
                return result;
            }
            if (info.Length > MaxFileBytes)
            {
                result.Error = "图片太大（超过 64MB）";
                return result;
            }

            using var loaded = LoadSafe(path);
            var bmp = loaded.Bitmap;
            ApplyOrientation(bmp, ReadOrientation(bmp));

            int srcW = bmp.Width, srcH = bmp.Height;
            if (srcW <= 0 || srcH <= 0)
            {
                result.Error = "图片尺寸无效";
                return result;
            }

            float scale = 1f;
            if (srcW > MaxSide || srcH > MaxSide || (long)srcW * srcH > MaxPixels)
            {
                scale = MathF.Min(MaxSide / (float)srcW, MaxSide / (float)srcH);
                float byPixels = MathF.Sqrt(MaxPixels / (float)((long)srcW * srcH));
                if (byPixels < scale) scale = byPixels;
                if (!(scale > 0f)) scale = 1f;
            }

            int outW = Math.Max(1, (int)MathF.Round(srcW * scale));
            int outH = Math.Max(1, (int)MathF.Round(srcH * scale));

            // PArgb = 预乘 alpha。透明 PNG 缩放时在预乘空间混合，边缘不会出现白边 / 黑边。
            using var dst = new Bitmap(outW, outH, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dst))
            {
                // **透明底**：透明 PNG 的 alpha 必须原样留下，不能像文档页那样铺白。
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceOver;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(bmp, new Rectangle(0, 0, outW, outH),
                            new Rectangle(0, 0, srcW, srcH), GraphicsUnit.Pixel);
            }

            var pixels = new byte[(long)outW * outH * 4];
            var rect = new Rectangle(0, 0, outW, outH);
            var data = dst.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < outH; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * outW * 4, outW * 4);
            }
            finally
            {
                dst.UnlockBits(data);
            }

            result.Width = outW;
            result.Height = outH;
            result.Bgra = pixels;
            result.HasAlpha = ScanAlpha(pixels);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

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

    /// <summary>
    /// 先整份读进内存再解码：GDI+ 会延迟读源流，直接 `new Bitmap(path)` 可能把文件锁到
    /// Bitmap 释放为止（老师换图 / 删图时就删不掉）。和 DocImageSource 同一条规矩。
    /// </summary>
    private static LoadedImage LoadSafe(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
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

    /// <summary>有没有任何一个像素不是全不透明（没有 alpha 的 JPG 会直接返回 false）。</summary>
    private static bool ScanAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 255) return true;
        return false;
    }

    internal static int ReadOrientation(Bitmap bmp)
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
    /// EXIF 方向 → GDI+ 的旋转翻转（业界那张标准表）。
    /// 文档页和贴纸导入共用这一份，避免两处映射慢慢长歪。
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
}
