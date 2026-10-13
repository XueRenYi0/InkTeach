using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 一张位图对象的数据（截图 / 粘贴进来的图）。
///
/// ## 位置
///
/// 它挂在 <see cref="Stroke"/> 上（<c>Stroke.Kind == StrokeKind.Image</c>），
/// **不是另一套对象体系**。这样"复制 / 删除 / 翻转 / 旋转 / 撤销 / 框选 /
/// 空间索引"全部原样复用——这正是不另外造一个 ImageItem 类的理由：
/// 那些能力都是按"对象"写的，不是按"笔迹"写的。
///
/// ## 像素格式：一律 BGRA32
///
/// 两种来源（屏幕截图走 GDI、剪贴板走 CF_DIB）给的都是 BGRA，D2D 直接吃，
/// 中间不做任何解码——不引 WIC、不引 System.Drawing，符合"核心不引依赖"。
///
/// **透明度有两个坑，都在 <see cref="Adopt"/> 里堵住**：
///
///   ① GDI 给的 32 位位图，第 4 个字节是**垃圾**（常年是 0）。按"预乘透明"
///      创建位图的话，整张图会变成完全透明——屏幕上看就是"截图之后什么都没有"。
///      所以来源没声明带 alpha 时，一律把 alpha 写成 255。
///   ② D2D 的预乘格式要求 R/G/B 已经乘过 alpha。带 alpha 的图（比如带透明
///      背景的 PNG 被别的程序转成 CF_DIB 时）不预乘的话，边缘会出现白边。
///      所以这一层统一做一次预乘，交给 D2D 的永远是"预乘好的 BGRA"。
/// </summary>
internal sealed class ImageData
{
    /// <summary>像素宽（物理像素）。对象在画布上占多大由 <see cref="Stroke.Transform"/> 决定。</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>预乘好的 BGRA32 像素，长度 = Width*Height*4。</summary>
    public byte[] Bgra { get; private set; }

    /// <summary>D2D 位图缓存。设备相关，跟几何缓存同一个道理（见 Stroke.Geometry）。</summary>
    private ID2D1Bitmap _bitmap;

    /// <summary>当前存活的位图字节数（诊断用，和 Stroke.LiveGeometries 同一路）。</summary>
    public static long LiveBytes;
    public static int LiveImages;

    public long ByteSize => (long)Width * Height * 4;

    /// <summary>
    /// 收下一份像素。<paramref name="hasAlpha"/> = 来源**声明**这个 alpha 是
    /// 有意义的（CF_DIBV5 的 V4 头、或者我们自己抓的已知格式）。默认 false，
    /// 走"alpha 一律填满"的那条路。
    /// </summary>
    public static ImageData Adopt(int width, int height, byte[] bgra, bool hasAlpha = false)
    {
        if (width <= 0 || height <= 0) return null;
        if (bgra == null || bgra.Length < (long)width * height * 4) return null;

        Normalize(bgra, width * height, hasAlpha);
        var img = new ImageData
        {
            Width = width,
            Height = height,
            Bgra = bgra,
        };
        LiveBytes += img.ByteSize;
        LiveImages++;
        return img;
    }

    /// <summary>
    /// 就地把像素修成"预乘 BGRA"。<paramref name="pixels"/> 是像素**个数**。
    ///
    /// 两个 pass 分开写：绝大多数像素 alpha = 255，第一个 pass 就能算出
    /// "整张图是不是不透明"，是的话第二个 pass 直接跳过——截图这条最常见的路
    /// 因此一次乘法都不做。
    /// </summary>
    private static void Normalize(byte[] bgra, int pixels, bool hasAlpha)
    {
        bool anyTransparent = false;
        if (hasAlpha)
        {
            for (int i = 3; i < pixels * 4; i += 4)
                if (bgra[i] != 255) { anyTransparent = true; break; }
        }
        else
        {
            // GDI 的第 4 字节是垃圾，全部写成不透明。
            for (int i = 3; i < pixels * 4; i += 4) bgra[i] = 255;
        }
        if (!anyTransparent) return;

        // 预乘：通道 = 通道 × alpha / 255。用整数乘加做，避免浮点误差累积。
        for (int i = 0; i < pixels * 4; i += 4)
        {
            int a = bgra[i + 3];
            if (a == 255) continue;
            bgra[i + 0] = (byte)((bgra[i + 0] * a + 127) / 255);
            bgra[i + 1] = (byte)((bgra[i + 1] * a + 127) / 255);
            bgra[i + 2] = (byte)((bgra[i + 2] * a + 127) / 255);
        }
    }

    /// <summary>
    /// 拿（必要时创建）D2D 位图。
    /// **必须由某个 device context 创建**：D2D 资源是设备级的，同一个设备下
    /// 不同窗口的 context 可以共用同一张位图，所以缓存一份就够。
    /// </summary>
    public ID2D1Bitmap GetBitmap(ID2D1DeviceContext ctx)
    {
        if (_bitmap != null) return _bitmap;
        if (ctx == null || Bgra == null) return null;

        var props = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None);

        var handle = GCHandle.Alloc(Bgra, GCHandleType.Pinned);
        try
        {
            _bitmap = ctx.CreateBitmap(new SizeI(Width, Height), handle.AddrOfPinnedObject(),
                                       (uint)(Width * 4), props);
        }
        catch
        {
            _bitmap = null;
        }
        finally
        {
            handle.Free();
        }
        return _bitmap;
    }

    public void Release()
    {
        if (_bitmap != null)
        {
            _bitmap.Dispose();
            _bitmap = null;
        }
    }

}
