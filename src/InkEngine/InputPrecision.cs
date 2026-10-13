using System.Runtime.InteropServices;

namespace InkEngine;

/// <summary>
/// **D1：亚像素输入**（2026-10-03，第一轮对照实验的第 0 层）。
///
/// 问题：`POINTER_INFO.ptPixelLocation` 是**整数像素**——笔的硬件分辨率远高于它，
/// 取整后 ±0.5px 的噪声在细笔（1~2px 宽）上就是肉眼可见的抖动。
/// `ptHimetricLocation` 是设备坐标（0.01mm ≈ 0.04px），配合
/// <see cref="Native.GetPointerDeviceRects"/> 可以映射回**小数像素**。
///
/// 开关：`--himetric`。默认关（保持现状，方便 A/B）；关掉时所有行为与以前一模一样。
/// 拿不到矩形、或设备不报 himetric（全 0 而像素坐标非 0）时**逐点退回整数像素**。
///
/// 缓存按设备句柄（sourceDevice）一份，不在每个点里查系统调用。
/// </summary>
internal static class InputPrecision
{
    public static bool UseHimetric;

    private struct DeviceMap
    {
        public Native.RECT Device;
        public Native.RECT Display;
        public bool Ok;
    }

    private static readonly Dictionary<IntPtr, DeviceMap> s_maps = new();

    /// <summary>诊断：成功映射 / 退回整数像素的点数（启动横幅或自检里能看出来）。</summary>
    public static long MappedPoints, FallbackPoints;

    /// <summary>
    /// 把一条 himetric 坐标映射成小数像素。失败返回 false（调用方用整数像素）。
    /// </summary>
    public static bool TryMap(IntPtr device, int himetricX, int himetricY,
                              int pixelX, int pixelY, out float x, out float y)
    {
        x = y = 0;
        if (!UseHimetric || device == IntPtr.Zero) { FallbackPoints++; return false; }
        // 设备不报 himetric 时坐标是全 0；而真正的 (0,0) 只可能出现在像素坐标同样是 0 时。
        if (himetricX == 0 && himetricY == 0 && (pixelX != 0 || pixelY != 0))
        {
            FallbackPoints++;
            return false;
        }

        if (!s_maps.TryGetValue(device, out var map))
        {
            map.Ok = Native.GetPointerDeviceRects(device, out map.Device, out map.Display)
                     && map.Device.Right > map.Device.Left
                     && map.Device.Bottom > map.Device.Top
                     && map.Display.Right > map.Display.Left
                     && map.Display.Bottom > map.Display.Top;
            s_maps[device] = map;
        }
        if (!map.Ok) { FallbackPoints++; return false; }

        float fx = (himetricX - map.Device.Left) / (float)(map.Device.Right - map.Device.Left);
        float fy = (himetricY - map.Device.Top) / (float)(map.Device.Bottom - map.Device.Top);
        x = map.Display.Left + fx * (map.Display.Right - map.Display.Left);
        y = map.Display.Top + fy * (map.Display.Bottom - map.Display.Top);
        MappedPoints++;
        return true;
    }

    /// <summary>清掉设备矩形缓存（设备热插拔/切屏时调用；当前只在启动时清一次）。</summary>
    public static void Reset()
    {
        s_maps.Clear();
        MappedPoints = 0;
        FallbackPoints = 0;
    }
}
