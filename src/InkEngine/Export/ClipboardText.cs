using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// 剪贴板纯文本（CF_UNICODETEXT）。取色器的色值复制/粘贴走这一份；
/// 图片剪贴板仍走 <see cref="ClipboardImage"/>，两边互不影响。
/// </summary>
internal static class ClipboardText
{
    public static string Get()
    {
        if (!Native.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr h = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr p = Native.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Native.GlobalUnlock(h); }
        }
        finally { Native.CloseClipboard(); }
    }

    public static bool Set(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!Native.OpenClipboard(IntPtr.Zero)) return false;
        IntPtr mem = IntPtr.Zero;
        try
        {
            Native.EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            mem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)(uint)bytes);
            if (mem == IntPtr.Zero) return false;
            IntPtr p = Native.GlobalLock(mem);
            if (p == IntPtr.Zero) return false;
            try
            {
                // 补结尾的 0（PtrToStringUni 读时依赖它）。
                var chars = new char[text.Length + 1];
                text.CopyTo(0, chars, 0, text.Length);
                chars[text.Length] = '\0';
                Marshal.Copy(chars, 0, p, chars.Length);
            }
            finally { Native.GlobalUnlock(mem); }

            if (Native.SetClipboardData(Native.CF_UNICODETEXT, mem) == IntPtr.Zero) return false;
            mem = IntPtr.Zero;      // 所有权归系统
            return true;
        }
        finally
        {
            if (mem != IntPtr.Zero) Native.GlobalFree(mem);
            Native.CloseClipboard();
        }
    }
}
