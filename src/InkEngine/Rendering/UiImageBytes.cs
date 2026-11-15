namespace InkEngine;

/// <summary>
/// 给界面层读一张图（2026-11-03：日志页的群二维码）。
///
/// **只做"文件 → 预乘 BGRA"这一件事**：不解码成 D2D 位图、不持有缓存——
/// 界面自己建位图、自己管生命周期。界面层（InkUi）只认公开契约，
/// 拿不到引擎的 internal 解码器，所以这里开一个很窄的口子。
/// </summary>
public static class UiImageBytes
{
    /// <summary>
    /// 读一张图（PNG / JPG / BMP…），返回**预乘 alpha** 的 BGRA，可直接交给
    /// `ID2D1DeviceContext.CreateBitmap`（PixelFormat = B8G8R8A8_UNorm + Premultiplied）。
    /// 失败返回 false，并在控制台留下原因。
    /// </summary>
    public static bool TryLoad(string path, out int width, out int height, out byte[] bgra)
    {
        width = height = 0;
        bgra = null;
        try
        {
            var r = ImageFile.Load(path);
            if (!r.Success)
            {
                Console.WriteLine("界面图片读不出来：" + r.Error + $"（{path}）");
                return false;
            }
            width = r.Width;
            height = r.Height;
            bgra = r.Bgra;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("界面图片读取失败：" + ex.Message);
            return false;
        }
    }
}
