// P0 探针：对比 C# 前向（InkDlPredictor）与 PyTorch 金标（golden.bin）。
// 用法：dotnet run --project tools\dl-probe -c Release -- <导出目录>
//   例： -- "C:\...\_ml-predict\runs\v4\export"
using System.Text.Json;

using InkEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        string dir = args.Length > 0
            ? args[0]
            : @"C:\Users\LHL-XZX\Desktop\软件\_ml-predict\runs\v4\export";
        string wBin = Path.Combine(dir, "weights.bin");
        string wMeta = Path.Combine(dir, "weights.meta.json");
        string gBin = Path.Combine(dir, "golden.bin");
        string gMeta = Path.Combine(dir, "golden.meta.json");
        foreach (var f in new[] { wBin, wMeta, gBin, gMeta })
        {
            if (!File.Exists(f)) { Console.WriteLine($"缺文件：{f}"); return 2; }
        }

        var p = InkDlPredictor.Load(wBin, wMeta);
        int L = p.Hist, H = p.Horizons.Length;

        var gm = JsonDocument.Parse(File.ReadAllText(gMeta)).RootElement;
        int N = gm.GetProperty("n").GetInt32();
        var bytes = File.ReadAllBytes(gBin);
        int offX = 4;
        int offMu = offX + N * L * 8 * 4;
        int offLv = offMu + N * H * 2 * 4;
        int offTau = offLv + N * H * 2 * 4;

        var x = new float[L * 8];
        var mu = new float[H * 2];
        var lv = new float[H * 2];
        double maxMu = 0, maxLv = 0, maxTau = 0;
        double sumMu = 0;
        for (int i = 0; i < N; i++)
        {
            Buffer.BlockCopy(bytes, offX + i * L * 8 * 4, x, 0, L * 8 * 4);
            p.Predict(x, mu, lv, out float tau);
            for (int k = 0; k < H * 2; k++)
            {
                float gmu = BitConverter.ToSingle(bytes, offMu + (i * H * 2 + k) * 4);
                float glv = BitConverter.ToSingle(bytes, offLv + (i * H * 2 + k) * 4);
                maxMu = Math.Max(maxMu, Math.Abs(mu[k] - gmu));
                sumMu += Math.Abs(mu[k] - gmu);
                maxLv = Math.Max(maxLv, Math.Abs(lv[k] - glv));
            }
            maxTau = Math.Max(maxTau, Math.Abs(tau - BitConverter.ToSingle(bytes, offTau + i * 4)));
        }

        Console.WriteLine($"金标 {N} 条  L={L}  H={H}");
        Console.WriteLine($"max|Δ|  mu={maxMu:E2}  lv={maxLv:E2}  tau={maxTau:E2}");
        Console.WriteLine($"mean|Δ| mu={sumMu / (N * H * 2):E2}");

        // 性能：单点前向耗时（20k 次）
        var xr = new float[L * 8];
        for (int i = 0; i < L * 8; i++) xr[i] = 0.01f * (i % 17);
        for (int i = 0; i < 1000; i++) p.Predict(xr, mu, lv, out _);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iters = 20000;
        for (int i = 0; i < iters; i++) p.Predict(xr, mu, lv, out _);
        sw.Stop();
        Console.WriteLine($"性能：{sw.Elapsed.TotalMicroseconds / iters:F1} µs/点（{iters} 次）");

        bool ok = maxMu < 1e-3 && maxLv < 1e-3 && maxTau < 1e-3;
        Console.WriteLine(ok ? "== PASS ==" : "== FAIL ==");
        return ok ? 0 : 1;
    }
}
