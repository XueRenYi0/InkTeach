using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// **对象的剪贴板通道**：把选中的批注对象放进剪贴板，粘回来仍是**可编辑对象**
/// （跨页、跨窗口、跨程序实例都行），而不是一张图。
///
/// 与"粘贴图片"（<see cref="ClipboardImage"/>）的分工：
///   · 我们自己写的格式 `InkTeach.InkObjects` 里是**对象字节**（复用存档格式），
///     同一时间还会放一份 **CF_DIB**——外部程序（Word / PPT / 微信）粘到的是图，至少能用；
///   · 粘贴时**先看有没有我们的对象格式**，有就还原成对象，没有才退回"当图粘"。
///
/// 为什么不是 ISF：那一步的价值是"粘进 Word/OneNote 是可编辑墨迹"，需要写 ISF 编解码器
/// （或用 WinRT / Tablet COM 投影），属于独立一轮；见 调研-ISF互通原理.md 第五、七节。
///
/// 三个老坑照 <see cref="ClipboardImage"/> 的注释办：剪贴板被别的程序占着要**重试**、
/// DIB **自下而上**要翻行、`SetClipboardData` 成功之后那块内存归系统（**不要**自己 free）。
/// </summary>
internal static class ClipboardInk
{
    private static uint _format;

    /// <summary>我们的剪贴板格式号（注册一次，之后缓存）。</summary>
    private static uint Format
    {
        get
        {
            if (_format == 0)
                _format = Native.RegisterClipboardFormat(InkSerializer.ClipboardFormatName);
            return _format;
        }
    }

    /// <summary>把一批对象序列化成剪贴板用的字节（**只含对象**：不带画布块/分页）。</summary>
    public static byte[] Serialize(IReadOnlyList<Stroke> items)
    {
        if (items == null || items.Count == 0) return null;
        var tmp = new InkDocument();
        for (int i = 0; i < items.Count; i++) tmp.AppendStroke(items[i].Clone());
        return InkSerializer.Save(tmp);
    }

    /// <summary>
    /// 放剪贴板：**同时**放对象格式（可粘回可编辑对象）和一张图
    /// （<paramref name="dibBgra"/> 可以是 null = 只放对象）。
    /// </summary>
    public static bool Set(byte[] objects, byte[] dibBgra = null, int dibW = 0, int dibH = 0)
    {
        if (objects == null || objects.Length == 0) return false;
        if (!ClipboardImage.OpenWithRetry(IntPtr.Zero)) return false;
        try
        {
            if (!Native.EmptyClipboard()) return false;
            bool ok = PutBytes(Format, objects);
            if (dibBgra != null && dibW > 0 && dibH > 0)
                ok &= ClipboardImage.PutDibIntoOpenClipboard(dibBgra, dibW, dibH);
            return ok;
        }
        finally { Native.CloseClipboard(); }
    }

    /// <summary>
    /// 剪贴板里有没有我们的对象；有就还原成一批对象。
    /// **身份重新发**（Id 归零）：文件里的 Id 是原对象的，直接插进文档会和原件撞号，
    /// 撤销 / 多选就会指错对象。
    /// </summary>
    public static bool TryGetObjects(out List<Stroke> items)
    {
        items = null;
        uint fmt = Format;
        if (fmt == 0) return false;
        if (!ClipboardImage.OpenWithRetry(IntPtr.Zero)) return false;
        try
        {
            if (!Native.IsClipboardFormatAvailable(fmt)) return false;
            IntPtr h = Native.GetClipboardData(fmt);
            if (h == IntPtr.Zero) return false;
            IntPtr p = Native.GlobalLock(h);
            if (p == IntPtr.Zero) return false;
            try
            {
                long n = (long)Native.GlobalSize(h);
                // 防呆：剪贴板里的长度字段可能是垃圾，几十兆以上直接拒绝（同 ReadString 的教训）。
                if (n <= 0 || n > 64L * 1024 * 1024) return false;
                var buf = new byte[n];
                Marshal.Copy(p, buf, 0, (int)n);

                var tmp = new InkDocument();
                InkSerializer.LoadInto(tmp, buf);
                items = tmp.Strokes;
                foreach (var s in items) s.Id = 0;
                return items.Count > 0;
            }
            finally { Native.GlobalUnlock(h); }
        }
        catch (Exception ex)
        {
            Console.WriteLine("剪贴板里的对象读不出来（忽略，当图粘）：" + ex.Message);
            items = null;
            return false;
        }
        finally { Native.CloseClipboard(); }
    }

    /// <summary>把一段字节写进剪贴板（调用方保证已打开并已清空）。</summary>
    private static bool PutBytes(uint format, byte[] bytes)
    {
        IntPtr hMem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (hMem == IntPtr.Zero) return false;
        IntPtr p = Native.GlobalLock(hMem);
        if (p == IntPtr.Zero) { Native.GlobalFree(hMem); return false; }
        try { Marshal.Copy(bytes, 0, p, bytes.Length); }
        finally { Native.GlobalUnlock(hMem); }

        if (Native.SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            Native.GlobalFree(hMem);      // 交出去失败，内存还归我们
            return false;
        }
        return true;                       // 成功之后所有权归系统，千万别 free
    }
}
