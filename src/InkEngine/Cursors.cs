using System.Runtime.InteropServices;
using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 指针该长什么样。**只表达语义，不表达"能不能做"**——能不能做由命中判定决定。
///
/// 命名对齐微软的指针形状表（Pointer shapes），不是为了好看：
/// 用户对"双箭头 = 拉伸""四向箭头 = 移动"的认知是系统级的，改名字可以，
/// 改语义不行。
/// </summary>
internal enum CursorKind
{
    /// <summary>普通箭头。也是"没有更合适的东西"时的兜底。</summary>
    Default = 0,

    /// <summary>精确取点。图形类工具（笔、荧光笔、图形、框选）都用它。</summary>
    Cross,

    /// <summary>
    /// 斜笔（系统"笔"光标 IDC_PEN，InkClass / Ink Canvas 同款）：笔工具在
    /// **鼠标 / 手写板**下用它（2026-09-27 加，用户点名要的）。
    ///
    /// 为什么它合适：热点在**笔尖**、笔杆朝右下，写字时笔杆不盖落点；
    /// 而且它就是系统光标——跟随用户的"指针大小/颜色/指针方案"辅助设置，
    /// 这两条自绘做不到。
    ///
    /// ⚠ 它**不跟墨色、不跟笔宽**（固定黑白位图，实测见 tmp/penprobe）。
    /// 2026-09-27 晚试过一版"自绘彩笔"（笔身填墨色、笔杆跟笔宽），用户看过真机后
    /// 判定"填充颜色有点难看，原来那支就挺好"，**已整批撤回**——想要"看得见真实粗细"
    /// 的人可以切到落点环那条路（见 Engine.PenShowsCursor），不在这支笔上做。
    ///
    /// 2026-10-13 二试（`--selfpen`）：**形状与笔身原样读系统位图**（不再手画），
    /// 只把**笔帽**染成当前色（见 SelfPen / TintPen）。
    /// </summary>
    Pen,

    /// <summary>
    /// 自绘·染色笔（2026-10-13，`--selfpen`）：形状 = 系统斜笔的位图原样，
    /// **笔身一个像素不改**，只有**笔帽**（笔轴最后一段）的浅色芯染成当前笔色
    /// （深轮廓 / 环扣描边保留，见 TintPen 三版）。**走系统光标通道**
    /// （SetCursor 自定义位图），延迟与系统笔同级——纯外观升级。
    /// </summary>
    SelfPen,

    /// <summary>整体移动（选中框内部）。</summary>
    Move,

    /// <summary>左右拉伸（选中框左右边中点）。</summary>
    ResizeWE,

    /// <summary>上下拉伸（选中框上下边中点）。</summary>
    ResizeNS,

    /// <summary>对角缩放：左上 / 右下。</summary>
    ResizeNWSE,

    /// <summary>对角缩放：右上 / 左下。</summary>
    ResizeNESW,

    /// <summary>旋转手柄。系统没有这个光标，我们自己画（见 RenderRotate）。</summary>
    Rotate,

    /// <summary>不显示指针（触摸、手写笔落笔、自绘落点反馈的时候）。</summary>
    Hidden,

    /// <summary>不插手：让系统或下层窗口决定。穿透模式用它。</summary>
    Leave,
}

/// <summary>
/// 光标工厂。三件事：
///
///   1. **系统光标**（箭头、十字、四向拉伸、移动）直接取现成的。
///      它们会自动跟随用户的"指针大小/颜色"辅助功能设置，也能在任何 DPI 下
///      保持锐利——自绘做不到这两点，所以能用系统的就不自己画。
///   2. **隐藏光标**用一个 1×1 全透明的自绘光标。不用 ShowCursor(false)：
///      那个是计数式的，任何一处多调一次就会永久改变指针的显示状态。
///   3. **旋转光标**系统里没有，只能自绘。位图按**当前指针尺寸**生成
///      （SM_CXCURSOR，本机 200% 缩放下是 64 像素），不然在高分屏上会糊。
///
/// 自绘的位图 = 白色外圈 + 深色内芯的双层描边：投影（浅底）和深色 PPT 上
/// 都必须看得见。这个取舍和橡皮圆环一致（见 Overlay.DrawEraserCursor）。
/// </summary>
internal static class Cursors
{
    /// <summary>
    /// 落点反馈外圈的**最小**半径（逻辑像素）。细笔（1.5 逻辑像素）画出来只有
    /// 3 物理像素宽，单靠真实宽度根本看不见落点，所以外面套一个固定尺寸的圈。
    /// 取 6 逻辑像素（直径 12）：投影上看得清，又不至于把细笔衬得像粗笔。
    /// </summary>
    public const float RingMinRadiusLogical = 6f;

    /// <summary>
    /// 激光落点的最小半径（逻辑像素）。激光是"指哪儿"，点太小看不见；
    /// 但也不能无脑放大——落点和轨迹的头部（芯 0.625×粗细）对不上就成了"两个东西"。
    /// </summary>
    public const float DotMinRadiusLogical = 3f;

    private static readonly Dictionary<int, IntPtr> s_system = new();
    private static readonly Dictionary<int, IntPtr> s_rotate = new();
    private static IntPtr s_hidden;

    /// <summary>染色笔缓存（key = 色 RGB + 尺寸）。**句柄 Zero = 该色读位图失败，
    /// 直接走系统笔**——也缓存，免得每帧重试失败路径。</summary>
    private static readonly Dictionary<(uint Rgb, int Size), IntPtr> s_selfPen = new();

    /// <summary>染色**像素**缓存（HCURSOR 与"应用帧笔画"共用）：(色, 尺寸) → (像素, 热点)。
    /// Px = null 表示读失败（标记住，不每帧重试）。</summary>
    private static readonly Dictionary<(uint Rgb, int Size), (byte[] Px, int HotX, int HotY)> s_selfPenPx = new();

    /// <summary>系统光标句柄（进程内共享，取到就缓存）。</summary>
    public static IntPtr System(int idc)
    {
        if (s_system.TryGetValue(idc, out var h)) return h;
        h = Native.LoadCursor(IntPtr.Zero, new IntPtr(idc));
        s_system[idc] = h;
        return h;
    }

    /// <summary>1×1 全透明光标：用来"把指针藏起来"。</summary>
    public static IntPtr Hidden()
    {
        if (s_hidden != IntPtr.Zero) return s_hidden;
        var px = new byte[4];                  // 一个像素，全 0 = 全透明
        s_hidden = Create(px, 1, 0, 0);
        return s_hidden;
    }

    /// <summary>旋转光标。sizePx 取系统指针尺寸（SM_CXCURSOR）。</summary>
    public static IntPtr Rotate(int sizePx)
    {
        sizePx = Math.Clamp(sizePx, 16, 256);
        if (s_rotate.TryGetValue(sizePx, out var h)) return h;
        var px = RenderRotate(sizePx);
        h = Create(px, sizePx, sizePx / 2, sizePx / 2);   // 热点在轴心
        s_rotate[sizePx] = h;
        return h;
    }

    // ---- 自绘染色笔（--selfpen，2026-10-13 试作）--------------------------------

    /// <summary>
    /// 染色笔的**像素生成**（HCURSOR 版与"应用帧笔画"共用同一份）：
    /// 读系统 IDC_PEN 位图 → 染色（见 TintPen）→ 预乘 BGRA。
    /// 结果按 (颜色, 尺寸) 缓存（含失败标记）；失败返回 null，调用方回退系统笔。
    /// </summary>
    public static byte[] TintedPenPixels(Color4 pen, int sizePx, out int hotX, out int hotY)
    {
        var key = (RgbKey(pen), sizePx);
        if (!s_selfPenPx.TryGetValue(key, out var e))
        {
            e = TryBuildPen(pen);
            s_selfPenPx[key] = e;
        }
        hotX = e.HotX;
        hotY = e.HotY;
        return e.Px;
    }

    /// <summary>像素能不能生成（给"帧笔接管"的判据用——读不出来就不接管，免得光标消失）。</summary>
    public static bool PenPixelsReadable(Color4 pen, int sizePx)
        => TintedPenPixels(pen, sizePx, out _, out _) != null;

    /// <summary>
    /// 自绘染色笔（HCURSOR 版）：形状 = 系统 IDC_PEN 位图原样，
    /// 配色见 TintPen 三版（**笔身原样 + 笔帽当前色**）。
    ///
    /// 任何一步失败都回退**系统原笔**，并把失败记进缓存（同色不再重复尝试）——
    /// 试作功能，绝不为了颜色把光标弄丢。
    /// </summary>
    public static IntPtr TintedPen(Color4 pen, int sizePx)
    {
        var cacheKey = (RgbKey(pen), sizePx);
        if (s_selfPen.TryGetValue(cacheKey, out var cached))
            return cached != IntPtr.Zero ? cached : System(Native.IDC_PEN);

        IntPtr made = IntPtr.Zero;
        try
        {
            var px = TintedPenPixels(pen, sizePx, out int hotX, out int hotY);
            if (px != null)
            {
                int n = (int)Math.Sqrt(px.Length / 4);      // 正方形约定（见 TryBuildPen）
                made = Create(px, n, hotX, hotY);
            }
        }
        catch { /* 回退系统笔 */ }

        // 失败也记缓存（Zero 作标记）：避免每帧重试失败路径。
        // ⚠ 只销毁"我们自绘的"（非 Zero）——系统句柄不能碰（见 DisposeAll）。
        s_selfPen[cacheKey] = made;
        return made != IntPtr.Zero ? made : System(Native.IDC_PEN);
    }

    /// <summary>颜色 → RGB 缓存键（忽略 alpha；帧笔那边也用它做位图缓存键）。</summary>
    public static uint RgbKey(Color4 pen)
        => ((uint)Math.Clamp((int)(pen.R * 255f + 0.5f), 0, 255) << 16)
         | ((uint)Math.Clamp((int)(pen.G * 255f + 0.5f), 0, 255) << 8)
         | (uint)Math.Clamp((int)(pen.B * 255f + 0.5f), 0, 255);

    /// <summary>读系统斜笔位图并染色。失败 (null, 0, 0)。</summary>
    private static (byte[] Px, int HotX, int HotY) TryBuildPen(Color4 pen)
    {
        try
        {
            IntPtr src = System(Native.IDC_PEN);
            if (src == IntPtr.Zero || !Native.GetIconInfo(src, out var ii)) return (null, 0, 0);
            try
            {
                var bm = new Native.BITMAP();
                if (ii.hbmColor == IntPtr.Zero
                    || Native.GetObjectBitmap(ii.hbmColor, Marshal.SizeOf<Native.BITMAP>(), ref bm) <= 0
                    || bm.bmWidth <= 0 || bm.bmWidth != bm.bmHeight) return (null, 0, 0);

                int n = bm.bmWidth;
                var bmi = new Native.BITMAPINFO
                {
                    bmiHeader = new Native.BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                        biWidth = n,
                        biHeight = -n,          // 负 = 自上而下，和 Create 的填法一致
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = (int)Native.BI_RGB,
                    },
                };
                var px = new byte[n * n * 4];
                IntPtr dc = Native.GetDC(IntPtr.Zero);
                int lines;
                try
                {
                    lines = Native.GetDIBits(dc, ii.hbmColor, 0, (uint)n, px, ref bmi,
                                             Native.DIB_RGB_COLORS);
                }
                finally { if (dc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, dc); }
                if (lines <= 0 || !HasAlphaSemantics(px)) return (null, 0, 0);

                TintPen(px, n, pen);
                return (px, ii.xHotspot, ii.yHotspot);
            }
            finally
            {
                // GetIconInfo 给的是位图**副本**，不删就是 GDI 泄漏（同 HotspotOf 的提醒）
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            }
        }
        catch { return (null, 0, 0); }
    }

    /// <summary>位图里有没有 alpha 语义（透明/半透明像素占到一定比例）。没有就认不出"笔的形状"，放弃染色。</summary>
    private static bool HasAlphaSemantics(byte[] px)
    {
        int n = px.Length / 4, zero = 0, semi = 0;
        for (int i = 3; i < px.Length; i += 4)
        {
            byte a = px[i];
            if (a == 0) zero++;
            else if (a < 255) semi++;
        }
        return zero > n / 20 || semi > n / 50;   // ≥5% 全透明 或 ≥2% 半透明
    }

    /// <summary>
    /// 染色（三版，2026-10-13 晚）：**只动笔帽，笔身原样**——用户点名"不管哪种，
    /// 都统一用系统笔的样式，然后在笔帽增加同色"（系统通道 / 应用帧共用这一份像素）。
    ///   ① 笔身（rel &lt; 0.70）——颜色**原样保留**（系统笔的深轮廓 + 白芯一个像素不改；
    ///      只做"直通 → 预乘"的格式归一，见下）；
    ///   ② 笔帽（笔轴最后一段，rel ≥ 0.70）——**浅色芯**换成当前色（0.88 实色，
    ///      和上一版"笔尾色块"同口径）；深色像素（轮廓 / 尾巴那个环扣的描边）
    ///      原样保留——浅底靠深轮廓勾边、深底靠彩块，两头都看得见；
    ///   ③ 不再另加外轮廓（二版那条是为"淡彩笔身"补的；现在笔身就是原版，自带轮廓）。
    /// 读出的是**直通（非预乘）** BGRA（探针实测：边缘是"颜色恒定、alpha 渐隐"的
    /// 直通结构，如 (18,20,38,a=18)），写回**预乘**（Create / CreateBitmap 的约定）
    /// ——所以**每个不透明像素都必须写**："原样" = 颜色不变，格式仍要归一。
    /// </summary>
    private static void TintPen(byte[] px, int n, Color4 pen)
    {
        // sMax = 可见像素的 max(x+y)：笔体沿对角线的末端（热点 (0,0) = 笔尖）
        int sMax = 1;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (px[(y * n + x) * 4 + 3] > 8) sMax = Math.Max(sMax, x + y);

        // 笔帽实色 = 当前色留大头
        float cR = pen.R * 0.88f + 0.12f, cG = pen.G * 0.88f + 0.12f, cB = pen.B * 0.88f + 0.12f;

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a == 0) continue;

                float rr, gg, bb;
                float lum = (r * 0.299f + g * 0.587f + b * 0.114f) / 255f;   // 0 = 黑，1 = 白
                if ((x + y) / (float)sMax >= 0.70f && lum >= 0.45f)
                {
                    rr = cR; gg = cG; bb = cB;              // ② 笔帽浅色芯 → 当前色
                }
                else
                {
                    rr = r / 255f; gg = g / 255f; bb = b / 255f;   // ① 笔身 / 深色 → 原样
                }
                float af = a / 255f;                        // 直通 → 预乘
                px[i]     = (byte)Math.Clamp(bb * af * 255f + 0.5f, 0f, 255f);
                px[i + 1] = (byte)Math.Clamp(gg * af * 255f + 0.5f, 0f, 255f);
                px[i + 2] = (byte)Math.Clamp(rr * af * 255f + 0.5f, 0f, 255f);
            }
        }
    }

    /// <summary>
    /// 光标工厂总入口。能用系统现成的就用系统现成的（它们自动跟随用户的
    /// "指针大小 / 颜色 / 指针方案"辅助设置）；系统没有的才自绘（见类注释）。
    /// </summary>
    public static IntPtr HandleFor(CursorKind kind, int sizePx) => kind switch
    {
        CursorKind.Pen => System(Native.IDC_PEN),
        CursorKind.Cross => System(Native.IDC_CROSS),
        CursorKind.Move => System(Native.IDC_SIZEALL),
        CursorKind.ResizeWE => System(Native.IDC_SIZEWE),
        CursorKind.ResizeNS => System(Native.IDC_SIZENS),
        CursorKind.ResizeNWSE => System(Native.IDC_SIZENWSE),
        CursorKind.ResizeNESW => System(Native.IDC_SIZENESW),
        CursorKind.Rotate => Rotate(sizePx),
        // SelfPen 正常走 TintedPen（要当前笔色，见 Engine.ApplyCursor）；这里兜底系统笔。
        CursorKind.SelfPen => System(Native.IDC_PEN),
        CursorKind.Hidden => Hidden(),
        _ => System(Native.IDC_ARROW),
    };

    /// <summary>只用来把光标名字打进日志/自检表。</summary>
    public static string Name(CursorKind kind) => kind switch
    {
        CursorKind.Pen => "IDC_PEN 斜笔",
        CursorKind.Cross => "IDC_CROSS 十字",
        CursorKind.Move => "IDC_SIZEALL 移动",
        CursorKind.ResizeWE => "IDC_SIZEWE 左右拉伸",
        CursorKind.ResizeNS => "IDC_SIZENS 上下拉伸",
        CursorKind.ResizeNWSE => "IDC_SIZENWSE 对角拉伸",
        CursorKind.ResizeNESW => "IDC_SIZENESW 对角拉伸",
        CursorKind.Rotate => "自绘·旋转（圆弧箭头）",
        CursorKind.SelfPen => "自绘·染色笔（笔身原样·笔帽当前色）",
        CursorKind.Hidden => "不显示指针",
        CursorKind.Leave => "不插手（交给下层窗口）",
        _ => "IDC_ARROW 箭头",
    };

    /// <summary>退出时释放自绘光标。系统光标不归我们管，不能删。</summary>
    public static void DisposeAll()
    {
        foreach (var h in s_rotate.Values) if (h != IntPtr.Zero) Native.DestroyCursor(h);
        s_rotate.Clear();
        // 染色笔：**只销毁我们自绘的**（缓存里的 Zero = 该色走系统笔，系统句柄不能碰）。
        foreach (var h in s_selfPen.Values) if (h != IntPtr.Zero) Native.DestroyCursor(h);
        s_selfPen.Clear();
        if (s_hidden != IntPtr.Zero) { Native.DestroyCursor(s_hidden); s_hidden = IntPtr.Zero; }
    }

    /// <summary>
    /// 自检用：读一个光标的**热点**。读不到返回 (-1,-1)。
    ///
    /// 用途：斜笔的"不挡视线"全靠热点在笔尖——一旦哪天取错光标序号
    /// （或系统换了资源），屏幕上就会变成"笔杆压在落点上"，肉眼很难第一时间发现。
    /// 实测值：斜笔热点 = (0,0) = 位图左上角 = 笔尖（见 tmp/penprobe）。
    /// </summary>
    public static (int x, int y) HotspotOf(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.GetIconInfo(h, out var info)) return (-1, -1);
        var pt = (info.xHotspot, info.yHotspot);
        // GetIconInfo 给的是位图**副本**，不删就是 GDI 对象泄漏（自检里跑一次也要收干净）
        if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
        if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
        return pt;
    }

    /// <summary>
    /// 把自绘的旋转光标导成 BMP（放大 4 倍、衬灰白棋盘底），给自检用。
    ///
    /// 光标是"看不见摸不着"的东西：形状对不对、热点在不在轴心，只有导成图
    /// 才检查得了。棋盘底是为了同时看清浅色外圈和深色内芯。
    /// </summary>
    public static void DumpRotate(string path, int sizePx, int scale = 4)
    {
        var src = RenderRotate(sizePx);
        int w = sizePx * scale, h = sizePx * scale;
        var img = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // 纯灰底：棋盘格会在缩略图里干扰对形状的判断
                float bg = 0.72f;
                float r = bg, g = bg, b = bg;

                int sx = x / scale, sy = y / scale;
                int si = (sy * sizePx + sx) * 4;
                float a = src[si + 3] / 255f;                 // 预乘过的 BGRA
                float sb = src[si + 0] / 255f, sg = src[si + 1] / 255f, sr = src[si + 2] / 255f;
                b = sb * a + b * (1f - a);
                g = sg * a + g * (1f - a);
                r = sr * a + r * (1f - a);

                int di = (y * w + x) * 4;
                img[di + 0] = (byte)Math.Clamp(b * 255f, 0f, 255f);
                img[di + 1] = (byte)Math.Clamp(g * 255f, 0f, 255f);
                img[di + 2] = (byte)Math.Clamp(r * 255f, 0f, 255f);
                img[di + 3] = 255;
            }
        }

        // 参照物（只出现在导出图里，不在真光标里）：
        //   顶端一小段品红 = "12 点钟方向"，中心十字 = 热点（旋转轴心）。
        for (int y = 0; y < 3; y++)
            for (int x = w / 2 - 10; x < w / 2 + 10; x++)
                SetPx(img, w, x, y, 255, 0, 255);
        for (int d = -12; d <= 12; d++)
        {
            SetPx(img, w, w / 2 + d, h / 2, 0, 160, 255);
            SetPx(img, w, w / 2, h / 2 + d, 0, 160, 255);
        }
        // 四个方位的标记点：右=绿、下=黄、左=青、上=品红（用来核对朝向）
        float rr = sizePx * 0.30f * scale;
        Mark(img, w, h, w / 2 + (int)rr, h / 2, 0, 200, 0);
        Mark(img, w, h, w / 2, h / 2 + (int)rr, 220, 220, 0);
        Mark(img, w, h, w / 2 - (int)rr, h / 2, 0, 200, 200);
        Mark(img, w, h, w / 2, h / 2 - (int)rr, 255, 0, 255);

        int stride = w * 4;
        int dataSize = stride * h;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(14 + 40 + dataSize);
        bw.Write(0); bw.Write(14 + 40);
        bw.Write(40); bw.Write(w); bw.Write(h);        // 正高度 = 行序自下而上
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(dataSize);
        bw.Write(2835); bw.Write(2835);
        bw.Write(0); bw.Write(0);
        for (int y = h - 1; y >= 0; y--) bw.Write(img, y * stride, stride);
    }

    private static void SetPx(byte[] img, int w, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= w || y >= img.Length / (w * 4)) return;
        int i = (y * w + x) * 4;
        img[i + 0] = b; img[i + 1] = g; img[i + 2] = r; img[i + 3] = 255;
    }

    private static void Mark(byte[] img, int w, int h, int cx, int cy, byte r, byte g, byte b)
    {
        for (int y = -3; y <= 3; y++)
            for (int x = -3; x <= 3; x++)
                SetPx(img, w, cx + x, cy + y, r, g, b);
    }

    /// <summary>
    /// 沿圆环采样一圈，报告哪些角度有墨（自检用：把"眼睛看"换成"数像素"）。
    /// 0° = 右，90° = 下，180° = 左，270° = 上（屏幕坐标，y 向下）。
    /// 期望的形状：只有 270°~330° 是空的（缺口在右上）。
    /// </summary>
    public static string RotateRingReport(int sizePx)
    {
        var px = RenderRotate(sizePx);
        float c = (sizePx - 1) * 0.5f;
        float r = sizePx * 0.30f;
        var sb = new System.Text.StringBuilder();
        for (int a = 0; a < 360; a += 15)
        {
            float rad = a * MathF.PI / 180f;
            int x = (int)MathF.Round(c + r * MathF.Cos(rad));
            int y = (int)MathF.Round(c + r * MathF.Sin(rad));
            int i = (y * sizePx + x) * 4;
            sb.Append(a).Append(':').Append(px[i + 3] > 40 ? '有' : '空').Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 环外（半径 &gt; 1.5r）的墨点数量。正常必须是 0——箭头最远只到 1.42r。
    ///
    /// 这条自检有来历：箭头坐标曾经多加过一次圆心偏移，整块被推到右下角，
    /// 屏幕上看着像"箭头从圆环上掉下来了"。肉眼很难判断这种错，数像素很快。
    /// </summary>
    public static int RotateStrayPixels(int sizePx)
    {
        var px = RenderRotate(sizePx);
        float c = (sizePx - 1) * 0.5f;
        float r = sizePx * 0.30f;
        int count = 0;
        for (int y = 0; y < sizePx; y++)
        {
            for (int x = 0; x < sizePx; x++)
            {
                if (px[(y * sizePx + x) * 4 + 3] <= 40) continue;
                float dx = x - c, dy = y - c;
                if (MathF.Sqrt(dx * dx + dy * dy) > r * 1.5f) count++;
            }
        }
        return count;
    }

    // ------------------------------------------------------------------
    //  位图 → HCURSOR
    // ------------------------------------------------------------------

    /// <summary>
    /// 32bpp 预乘 BGRA → HCURSOR。颜色位图负责半透明，单色 AND 掩码留全 0
    /// （Vista 以后有 alpha 通道时不再看掩码，但留着更保险）。
    /// </summary>
    private static IntPtr Create(byte[] px, int size, int hotX, int hotY)
    {
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,          // 负数 = 自上而下，和我们填像素的顺序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (int)Native.BI_RGB,
            },
        };

        IntPtr color = Native.CreateDIBSection(IntPtr.Zero, ref bmi, Native.DIB_RGB_COLORS,
                                              out IntPtr bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

        Marshal.Copy(px, 0, bits, Math.Min(px.Length, size * size * 4));

        IntPtr mask = Native.CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new Native.ICONINFO
        {
            fIcon = false,
            xHotspot = hotX,
            yHotspot = hotY,
            hbmMask = mask,
            hbmColor = color,
        };
        IntPtr cursor = Native.CreateIconIndirect(ref info);

        Native.DeleteObject(color);
        if (mask != IntPtr.Zero) Native.DeleteObject(mask);
        return cursor;
    }

    // ------------------------------------------------------------------
    //  旋转光标：圆弧箭头 + 中心轴点
    // ------------------------------------------------------------------

    /// <summary>
    /// 画一个顺时针的圆弧箭头（缺口在右上），圆心一个点表示旋转轴。
    ///
    /// 形状用"点到图形的距离"描述，每个像素 4×4 超采样求覆盖率，
    /// 这样边缘是抗锯齿的——手画像素点阵在高分屏上会很难看。
    /// </summary>
    private static byte[] RenderRotate(int n)
    {
        var px = new byte[n * n * 4];

        float c = (n - 1) * 0.5f;
        float r = n * 0.30f;                            // 圆弧半径
        float coreW = MathF.Max(1.4f, n * 0.085f);      // 深色芯线宽
        float haloW = coreW + MathF.Max(1.4f, n * 0.075f);   // 白色外圈（每边各宽一点）
        float dotR = MathF.Max(1.2f, n * 0.062f);       // 中心轴点

        // 缺口：以 300°（右上）为中心，宽 75°。箭头在圆弧末端（262.5°，正上偏左），
        // 朝右——这就是大家认得的"顺时针旋转"。
        const float gapCenterDeg = 300f;
        const float gapHalfDeg = 37.5f;
        float arcStartDeg = gapCenterDeg + gapHalfDeg;            // 337.5°
        float arcSweepDeg = 360f - gapHalfDeg * 2f;               // 285°
        float headDeg = arcStartDeg + arcSweepDeg;                // 262.5°（追一圈回来）

        var head = HeadPoints(n, c, r, headDeg);
        var t0 = head.tip;
        var t1 = head.b1;
        var t2 = head.b2;

        const int ss = 4;                                // 4×4 超采样
        float inv = 1f / (ss * ss);
        const float white = 0.95f, dark = 0.93f;
        float darkR = 0.16f, darkG = 0.18f, darkB = 0.22f;   // 深色芯（浅底上立得住）

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int haloHits = 0, coreHits = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        float px0 = x + (sx + 0.5f) / ss - c;
                        float py0 = y + (sy + 0.5f) / ss - c;
                        float d = MathF.Sqrt(px0 * px0 + py0 * py0);
                        bool inCore = false, inHalo = false;

                        if (InArc(px0, py0, d, out bool arcCore, out bool arcHalo,
                                  r, coreW * 0.5f, haloW * 0.5f, arcStartDeg, arcSweepDeg))
                        {
                            inCore |= arcCore; inHalo |= arcHalo;
                        }
                        if (d <= dotR) { inCore = true; inHalo = true; }
                        if (InTriangle(px0, py0, t0, t1, t2)) { inCore = true; inHalo = true; }

                        if (inHalo) haloHits++;
                        if (inCore) coreHits++;
                    }
                }

                if (haloHits == 0) continue;

                float aHalo = haloHits * inv * white;
                float aCore = coreHits * inv * dark;

                // 先铺白圈，再把深色芯叠上去（source-over，两者都是预乘存储）
                float oa = aCore + aHalo * (1f - aCore);
                float or_ = darkR * aCore + 1f * aHalo * (1f - aCore);
                float og = darkG * aCore + 1f * aHalo * (1f - aCore);
                float ob = darkB * aCore + 1f * aHalo * (1f - aCore);

                int i = (y * n + x) * 4;
                px[i + 0] = (byte)Math.Clamp(ob * 255f, 0f, 255f);   // B
                px[i + 1] = (byte)Math.Clamp(og * 255f, 0f, 255f);   // G
                px[i + 2] = (byte)Math.Clamp(or_ * 255f, 0f, 255f);  // R
                px[i + 3] = (byte)Math.Clamp(oa * 255f, 0f, 255f);   // A
            }
        }
        return px;
    }

    /// <summary>圆弧上的一点：到圆心的距离落在环带里，且角度在弧段内。</summary>
    private static bool InArc(float x, float y, float d, out bool inCore, out bool inHalo,
                              float r, float coreHalf, float haloHalf, float startDeg, float sweepDeg)
    {
        inCore = inHalo = false;
        float ang = MathF.Atan2(y, x) * 180f / MathF.PI;
        if (ang < 0f) ang += 360f;
        float t = ang - startDeg;
        if (t < 0f) t += 360f;
        if (t > sweepDeg) return false;
        inCore = MathF.Abs(d - r) <= coreHalf;
        inHalo = MathF.Abs(d - r) <= haloHalf;
        return inHalo;
    }

    private static bool InTriangle(float px, float py,
                                   System.Numerics.Vector2 a, System.Numerics.Vector2 b,
                                   System.Numerics.Vector2 c)
    {
        var p = new System.Numerics.Vector2(px, py);
        float d1 = Cross(p - a, b - a);
        float d2 = Cross(p - b, c - b);
        float d3 = Cross(p - c, a - c);
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }

    private static float Cross(System.Numerics.Vector2 u, System.Numerics.Vector2 v) => u.X * v.Y - u.Y * v.X;

    /// <summary>箭头三角形的三个顶点（尖、两个底角）。</summary>
    private static (System.Numerics.Vector2 tip, System.Numerics.Vector2 b1, System.Numerics.Vector2 b2)
        HeadPoints(int n, float c, float r, float headDeg)
    {
        float rad = headDeg * MathF.PI / 180f;
        var dir = new System.Numerics.Vector2(-MathF.Sin(rad), MathF.Cos(rad));  // 切线（顺时针方向）
        var nrm = new System.Numerics.Vector2(MathF.Cos(rad), MathF.Sin(rad));   // 径向
        // **相对圆心**的坐标：像素循环里的采样点也是相对圆心的，两边必须同源。
        // （这里曾经多加过一次圆心偏移，箭头就被画到了右下角——多出来的那一块
        //   还盖不住，看起来像"箭头从环上掉下来了"。）
        var onCircle = new System.Numerics.Vector2(r * nrm.X, r * nrm.Y);
        _ = c;
        return (onCircle + dir * (n * 0.16f),
                onCircle + nrm * (n * 0.125f) - dir * (n * 0.04f),
                onCircle - nrm * (n * 0.125f) - dir * (n * 0.04f));
    }

}
