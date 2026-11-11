using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// `--photobeautify &lt;输入目录&gt; &lt;输出目录&gt;`：
    /// 用**正式要接入的 C# 算法**批量跑图片，出原图/美化图，给验收对比用。
    /// </summary>
    private void PhotoBeautifyShow(string inputDir, string outputDir)
    {
        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
        {
            Console.WriteLine("用法：--photobeautify <输入目录> <输出目录>");
            ExitCode = 1;
            _quit = true;
            return;
        }
        Directory.CreateDirectory(outputDir);
        var files = Directory.GetFiles(inputDir)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var index = new StreamWriter(Path.Combine(outputDir, "index.tsv"), false,
                                           new System.Text.UTF8Encoding(false));
        int i = 0, ok = 0, skip = 0;
        foreach (var path in files)
        {
            i++;
            string id = $"img{i:00}";
            var sw = Stopwatch.StartNew();
            var loaded = ImageFile.Load(path);
            if (!loaded.Success)
            {
                Console.WriteLine($"{id} 读取失败：{Path.GetFileName(path)}：{loaded.Error}");
                skip++;
                continue;
            }
            var pixels = PhotoBeautify.Process(loaded.Bgra, loaded.Width, loaded.Height);
            sw.Stop();
            SaveBgra(Path.Combine(outputDir, id + "-orig.png"), loaded.Bgra, loaded.Width, loaded.Height);
            if (pixels == null)
            {
                skip++;
                Console.WriteLine($"{id} 跳过（已足够白/透明）：{Path.GetFileName(path)}");
                index.WriteLine($"{id}\t{Path.GetFileName(path)}\t{loaded.Width}\t{loaded.Height}\tskip");
                continue;
            }
            SaveBgra(Path.Combine(outputDir, id + "-beautified.png"), pixels, loaded.Width, loaded.Height);
            index.WriteLine($"{id}\t{Path.GetFileName(path)}\t{loaded.Width}\t{loaded.Height}\t{sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"{id} {Path.GetFileName(path)}  {loaded.Width}×{loaded.Height}  {sw.ElapsedMilliseconds}ms");
            ok++;
        }
        Console.WriteLine($"图片美化：处理 {ok} 张，跳过 {skip} 张，输出 {Path.GetFullPath(outputDir)}");
        _quit = true;
    }

    private static void SaveBgra(string path, byte[] bgra, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }
}
