using System.Globalization;
using System.Numerics;
using InkEngine;

namespace InkTeach;

/// <summary>
/// **黑匣子回放**（`--replayinput &lt;文件&gt;`）：把 `--reclive` 记录的真实输入按原时间轴
/// **1:1 喂回引擎**（起笔/采样/收笔全走真入口、推时钟、不 sleep），逐帧采样"墨尖"，
/// 算出：**供给率 / 帧间墨尖位移（含前 5 大）/ 方向翻转 / 门触发**。
///
/// 用途：用户真机上"跳一下"说不清时——按一下记录，把文件发回来，这里把那一跳
/// 变成"第几秒、哪一帧、多少像素"。判据口径与 `--pdmetrics`（离线语料版）同源。
/// </summary>
internal static class InputReplayProbe
{
    private readonly record struct Ev(double T, char K, float X, float Y, float P, uint Type);

    public static int Run(App app, string[] args)
    {
        string path = null;
        double frameMs = 10.0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--replayinput" && i + 1 < args.Length) path = args[i + 1];
            else if (args[i] == "--frame" && i + 1 < args.Length
                     && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fm) && fm >= 1)
                frameMs = fm;
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.WriteLine("  用法：--replayinput <黑匣子文件> [--frame 10]");
            return 2;
        }

        Console.WriteLine("=== 黑匣子回放（--replayinput）===");
        Console.WriteLine($"  输入：{path}；帧距 {frameMs:F1}ms");

        var evs = Load(path);
        if (evs.Count == 0) { Console.WriteLine("  文件里没有事件。"); return 2; }
        int strokes = 0, samples = 0;
        foreach (var e in evs) { if (e.K == 'B') strokes++; else if (e.K == 'S') samples++; }
        double dur = evs[^1].T - evs[0].T;
        Console.WriteLine($"  事件 {evs.Count}（起笔 {strokes} / 采样 {samples} / 收笔 {strokes}？），"
                          + $"时长 {dur / 1000.0:F1}s");

        // ---- 主循环：事件与帧交替推进 ----
        var tips = new List<(int Stroke, double T, float X, float Y)>();
        int frame = 0, frameWithStroke = 0, frameWithTail = 0;
        int strokeIdx = -1, gates = 0, reversals = 0;
        double nextFrame = evs[0].T;
        double lastT = evs[^1].T;

        void DoFrame(double t)
        {
            app.NowMs = t;
            app.UpdatePredictTail();
            if (app.ActiveStroke == null) return;
            frameWithStroke++;
            float tx, ty;
            if (app.PredictTailActive && app.PredictTailPoints.Count > 0)
            {
                var p = app.PredictTailPoints[^1];
                tx = p.X; ty = p.Y;
                frameWithTail++;
            }
            else
            {
                var p = app.ActiveStroke.Points[^1];
                tx = p.X; ty = p.Y;
            }
            tips.Add((strokeIdx, t, tx, ty));
        }

        foreach (var e in evs)
        {
            while (nextFrame <= e.T) { frame++; DoFrame(nextFrame); nextFrame += frameMs; }
            app.NowMs = e.T;
            switch (e.K)
            {
                case 'B':
                    if (strokeIdx >= 0) { var c0 = app.PredictGateCounters; gates += c0.gates; reversals += c0.reversals; }
                    strokeIdx++;
                    app.ReplayBeginForTest(e.X, e.Y, e.P, e.Type);
                    break;
                case 'S': app.ReplaySampleForTest(e.X, e.Y, e.P); break;
                case 'E':
                    var c = app.PredictGateCounters;
                    gates += c.gates; reversals += c.reversals;
                    app.ReplayEndForTest();
                    break;
            }
        }
        while (nextFrame <= lastT + 200) { frame++; DoFrame(nextFrame); nextFrame += frameMs; }

        // ---- 指标 ----
        var jumps = new List<(double D, int Stroke, double T)>();
        int flips = 0;
        for (int i = 1; i < tips.Count; i++)
        {
            var a = tips[i - 1]; var b = tips[i];
            if (a.Stroke != b.Stroke) continue;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d > 0.05) jumps.Add((d, b.Stroke, b.T));
            if (i >= 2 && tips[i - 1].Stroke == tips[i - 2].Stroke)
            {
                double px = a.X - tips[i - 2].X, py = a.Y - tips[i - 2].Y;
                double pl = Math.Sqrt(px * px + py * py);
                if (pl > 3 && d > 3)
                {
                    double cos = (px * dx + py * dy) / (pl * d);
                    if (cos < Math.Cos(100.0 * Math.PI / 180.0)) flips++;
                }
            }
        }
        jumps.Sort((x, y) => y.D.CompareTo(x.D));

        double supply = frameWithStroke > 0 ? 100.0 * frameWithTail / frameWithStroke : 0;
        Console.WriteLine($"  有笔帧 {frameWithStroke} / 总帧 {frame}；**供给率（尾激活）{supply:F1}%**");
        Console.WriteLine($"  门触发：急转 {gates} 次 / 反向 {reversals} 次"
                          + (samples > 0 ? $"（每千采样 {1000.0 * (gates + reversals) / samples:F1}）" : ""));
        Console.WriteLine($"  方向翻转（>100° 且两侧位移>3px）：{flips} 次");
        Console.WriteLine("  帧间墨尖位移（前 5 大）:");
        for (int i = 0; i < Math.Min(5, jumps.Count); i++)
            Console.WriteLine($"    {jumps[i].D,7:F1}px  @ 第 {jumps[i].Stroke + 1} 笔、t={jumps[i].T:F0}ms");
        Console.WriteLine($"  最大帧间位移：{(jumps.Count > 0 ? jumps[0].D : 0):F1}px"
                          + $"（中位 {(jumps.Count > 0 ? jumps[jumps.Count / 2].D : 0):F1}px）");
        return 0;
    }

    private static List<Ev> Load(string path)
    {
        var list = new List<Ev>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2) continue;
            char k = t[0][0];
            if (k != 'B' && k != 'S' && k != 'E') continue;
            if (!double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tt)) continue;
            float F(int i, float dflt) =>
                i < t.Length && float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : dflt;
            list.Add(new Ev(tt, k, F(2, 0), F(3, 0), F(4, 0.5f),
                            (uint)(t.Length > 5 && uint.TryParse(t[5], out var ty) ? ty : 4)));
        }
        return list;
    }
}
