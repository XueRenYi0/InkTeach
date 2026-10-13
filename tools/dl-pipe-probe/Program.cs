// P1 对拍探针：retimed reclive → DlPredictController → 逐样本 CSV（显示点第 3 个 = 光子处）。
// 用法：
//   dotnet run --project tools\dl-pipe-probe -c Release -- --file <retimed.txt> --weights <导出目录>
//        --fixed-s 2.045 --fixed-f 0.5 --gamma 0.9 --photon 10 --out <csv>
// 规则：B 点不喂（引擎不喂）；每个 S 点喂后立即预测一次（等效"每点一帧"，k=1 透传）。
using System.Globalization;

using InkEngine;

internal static class Program
{
    private static int Main(string[] args)
    {
        string file = null, weights = null, outp = null;
        float fixedS = -1f, fixedF = -1f, gamma = 0.9f, photon = 10f;
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--file": file = args[++i]; break;
                case "--weights": weights = args[++i]; break;
                case "--out": outp = args[++i]; break;
                case "--fixed-s": fixedS = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--fixed-f": fixedF = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--gamma": gamma = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--photon": photon = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
            }
        }
        if (file == null || weights == null || outp == null)
        {
            Console.WriteLine("参数缺：--file/--weights/--out");
            return 2;
        }

        var model = InkDlPredictor.Load(Path.Combine(weights, "weights.bin"),
                                        Path.Combine(weights, "weights.meta.json"));
        var ctl = new DlPredictController(model)
        {
            Gamma = gamma,
            PhotonMs = photon,
            FixedS = fixedS > 0 ? fixedS : null,
            FixedFScale = fixedF > 0 ? fixedF : null,
        };
        var pts = new PredictedPoint[3];
        var lines = new List<string> { "stroke,sample,x,y" };
        int stroke = -1, sample = 0;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            char k = t[0][0];
            if (k == 'B')
            {
                stroke++;
                sample = 0;
                ctl.Reset();
                continue;
            }
            if (k != 'S') continue;
            float x = float.Parse(t[2], CultureInfo.InvariantCulture);
            float y = float.Parse(t[3], CultureInfo.InvariantCulture);
            float p = float.Parse(t[4], CultureInfo.InvariantCulture);
            double tms = double.Parse(t[1], CultureInfo.InvariantCulture);
            sample++;
            ctl.Add(x, y, p, tms, true);
            int n = ctl.PredictPoints(pts);
            if (n > 0)
                lines.Add($"{stroke},{sample}," +
                          $"{pts[2].X.ToString("F6", CultureInfo.InvariantCulture)}," +
                          $"{pts[2].Y.ToString("F6", CultureInfo.InvariantCulture)}");
        }
        File.WriteAllLines(outp, lines);
        Console.WriteLine($"rows: {lines.Count - 1} → {outp}");
        return 0;
    }
}
