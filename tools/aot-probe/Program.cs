// AOT 探针（临时调查工具，不属于产品）：
// 在 NativeAOT 下依次建立 D3D11 → D2D（含 ID2D1DeviceContext2 / ID2D1Ink 样式）→
// DWrite → DirectComposition（含委托墨迹接口探测），再离屏画一帧；
// 最后测 System.Drawing.Common 的 JPEG 编码。
//
// 通过标准：全部打印 OK（System.Drawing 一项允许单独失败，记录清楚即可）。
using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

static class Program
{
    static int Main()
    {
        Console.WriteLine("AOT-PROBE: 开始；IsDynamicCodeSupported=" + RuntimeFeature.IsDynamicCodeSupported);
        try
        {
            // ---- 1. D3D11 ----
            var levels = new[]
            {
                Vortice.Direct3D.FeatureLevel.Level_11_0,
                Vortice.Direct3D.FeatureLevel.Level_10_1,
                Vortice.Direct3D.FeatureLevel.Level_10_0,
            };
            D3D11.D3D11CreateDevice((IDXGIAdapter)null, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, levels, out var device).CheckError();
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var desc = adapter.Description;
            Console.WriteLine($"D3D11: OK; 适配器 = {desc.Description}; FeatureLevel = {device.FeatureLevel}");

            // ---- 2. D2D ----
            using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(
                Vortice.Direct2D1.FactoryType.SingleThreaded, DebugLevel.None);
            using var d2dDevice = d2dFactory.CreateDevice(dxgiDevice);
            using var ctx = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            Console.WriteLine("D2D 设备/上下文: OK");

            // ID2D1DeviceContext2 + ID2D1Ink 样式（压感笔迹那条渲染路）
            using var ctx2 = ctx.QueryInterfaceOrNull<ID2D1DeviceContext2>();
            if (ctx2 != null)
            {
                using var inkStyle = ctx2.CreateInkStyle(new InkStyleProperties
                {
                    NibShape = InkNibShape.Round,
                    NibTransform = Matrix3x2.Identity,   // 必须显式给单位阵（零矩阵=笔尖零尺寸）
                });
                Console.WriteLine("ID2D1DeviceContext2 / CreateInkStyle: OK");
            }
            else
            {
                Console.WriteLine("ID2D1DeviceContext2: 不可用（压感笔迹会退回等宽描边）");
            }

            // ---- 3. DWrite ----
            using var dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
            Console.WriteLine("DirectWrite: OK");

            // ---- 4. DComp + 委托墨迹接口 ----
            using var dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            using var inkTrailDevice = dcomp.QueryInterfaceOrNull<IDCompositionInkTrailDevice>();
            Console.WriteLine("DirectComposition: OK; IDCompositionInkTrailDevice="
                              + (inkTrailDevice != null ? "可用" : "不可用"));

            // ---- 5. 离屏画一帧（走 D2D 的 COM 面）----
            var pf = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied);
            var props = new BitmapProperties1(pf, 96f, 96f, BitmapOptions.Target);
            using var target = ctx.CreateBitmap(new SizeI(64, 64), IntPtr.Zero, 0, props);
            ctx.Target = target;
            ctx.BeginDraw();
            ctx.Clear(new Color4(0f, 0f, 0f, 0f));
            using (var brush = ctx.CreateSolidColorBrush(new Color4(1f, 0f, 0f, 1f)))
                ctx.FillRectangle(new Rect(0f, 0f, 64f, 64f), brush);
            ctx.EndDraw();
            ctx.Target = null;
            Console.WriteLine("D2D 离屏绘制: OK");

            // ---- 6. System.Drawing.Common（JPEG 编码那条路）----
            try
            {
                using var bmp = new System.Drawing.Bitmap(16, 16);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.Clear(System.Drawing.Color.Red);
                var path = Path.Combine(Path.GetTempPath(), "aot-probe.jpg");
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
                long len = new FileInfo(path).Length;
                File.Delete(path);
                Console.WriteLine($"System.Drawing JPEG: OK（{len} 字节）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("System.Drawing JPEG: FAIL - " + ex.GetType().Name + ": " + ex.Message);
            }

            Console.WriteLine("AOT-PROBE: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("AOT-PROBE: FAIL - " + ex);
            return 1;
        }
    }
}
