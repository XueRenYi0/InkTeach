using System.IO;
using System.Linq;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    private sealed class PlotCase
    {
        public string Cat;
        public string Prompt;
        public string Kind = "functions";
        public string Tag = "";
        public int MinSeries = 1;
        public bool NeedPieces;
        public bool NeedParams;
    }

    /// <summary>
    /// 初高中常见函数作图抽样：真实模型 → PlotSpec → 本地采样/渲染。
    /// 目标不是考模型，而是测“从一句话到一张能看的图”的整条链路成功率。
    /// </summary>
    private void PlotSuiteTest(string filter = null)
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 作图中高中函数抽样 ===");
        var cfg = OcrClient.LoadConfig();
        if (!cfg.IsConfigured)
        {
            Console.WriteLine("AI 未配置，跳过");
            ExitCode = 1;
            return;
        }

        var cases = new List<PlotCase>
        {
            // 幂函数
            new() { Cat = "power", Prompt = "画函数 y=x^2 的图象。" },
            new() { Cat = "power", Prompt = "画函数 y=x^3 的图象。" },
            new() { Cat = "power", Prompt = "画函数 y=x^(1/3) 的图象。" },
            new() { Cat = "power", Prompt = "画函数 y=x^(2/3) 的图象。" },
            new() { Cat = "power", Prompt = "画函数 y=sqrt(x) 的图象。", Tag = "sqrt" },
            new() { Cat = "power", Prompt = "画函数 y=cbrt(x) 的图象。", Tag = "cbrt" },
            new() { Cat = "power", Prompt = "画函数 y=1/x 的图象。" },

            // 指数 / 对数
            new() { Cat = "explog", Prompt = "画函数 y=2^x 的图象。" },
            new() { Cat = "explog", Prompt = "画函数 y=e^x 的图象。" },
            new() { Cat = "explog", Prompt = "画函数 y=ln(x) 的图象。", Tag = "ln" },
            new() { Cat = "explog", Prompt = "画函数 y=lg(x) 的图象。", Tag = "lg" },
            new() { Cat = "explog", Prompt = "画函数 y=log₂(x) 的图象。", Tag = "log2" },
            new() { Cat = "explog", Prompt = "画函数 y=log2(x) 的图象。", Tag = "log2" },
            new() { Cat = "explog", Prompt = "画函数 y=log(x)（常用对数）的图象。", Tag = "lg" },

            // 三角
            new() { Cat = "trig", Prompt = "画函数 y=sin(x) 在 [-2*pi,2*pi] 的图象。", Tag = "sin" },
            new() { Cat = "trig", Prompt = "画函数 y=cos(2*x) 在 [-2*pi,2*pi] 的图象。", Tag = "cos" },
            new() { Cat = "trig", Prompt = "画函数 y=tan(x) 在 (-pi/2,pi/2) 的图象。", Tag = "tan" },
            new() { Cat = "trig", Prompt = "画函数 y=cot(x) 在 (0,pi) 的图象。", Tag = "cot" },
            new() { Cat = "trig", Prompt = "画函数 y=sec(x) 的图象。", Tag = "sec" },
            new() { Cat = "trig", Prompt = "画函数 y=2*sin(2*x+pi/3)+1 的图象。", Tag = "sin" },

            // 复合函数
            new() { Cat = "composite", Prompt = "画复合函数 y=ln(x^2-1) 的图象。", Tag = "ln" },
            new() { Cat = "composite", Prompt = "画复合函数 y=sqrt(4-x^2) 的图象。", Tag = "sqrt" },
            new() { Cat = "composite", Prompt = "画复合函数 y=e^(-x^2) 的图象。" },
            new() { Cat = "composite", Prompt = "画复合函数 y=abs(x^2-2*x) 的图象。", Tag = "abs" },
            new() { Cat = "composite", Prompt = "画复合函数 y=1/(x^2-1) 的图象。" },
            new() { Cat = "composite", Prompt = "画复合函数 y=ln(sin(x)) 在 (0,pi) 的图象。", Tag = "ln" },

            // 分段
            new() { Cat = "piecewise", Prompt = "画分段函数 y=2^x (x>0)，y=x^2 (x<=0) 的图象。",
                    NeedPieces = true },
            new() { Cat = "piecewise", Prompt = "画分段函数 y=x+1 (x<0)，y=x^2 (x>=0) 的图象。",
                    NeedPieces = true },
            new() { Cat = "piecewise", Prompt = "画分段函数 y=-x (x<0)，y=x (x>=0) 的图象。",
                    NeedPieces = true },
            new() { Cat = "piecewise", Prompt = "画分段函数 y=1/x (x!=0)，y=0 (x=0) 的图象。",
                    NeedPieces = true },

            // 含参
            new() { Cat = "param", Prompt = "画含参函数 y=a*x^2+b*x+c 的图象，a=1,b=-2,c=-1。",
                    NeedParams = true },
            new() { Cat = "param", Prompt = "画含参函数 y=a*sin(b*x+c) 的图象，a=2,b=2,c=0。",
                    NeedParams = true },
            new() { Cat = "param", Prompt = "画含参函数 y=k/x 的图象，k=2。", NeedParams = true },
            new() { Cat = "param", Prompt = "画含参函数 y=a^x 的图象，a=2。", NeedParams = true },

            // 圆锥
            new() { Cat = "conic", Prompt = "画椭圆 x^2/9+y^2/4=1 的图象，标出焦点。", Kind = "conic" },
            new() { Cat = "conic", Prompt = "画椭圆 (x-2)^2/4+(y+1)^2/9=1 的图象。", Kind = "conic" },
            new() { Cat = "conic", Prompt = "画双曲线 x^2/4-y^2/9=1 的图象，标出渐近线。", Kind = "conic" },
            new() { Cat = "conic", Prompt = "画双曲线 y^2/9-x^2/4=1 的图象。", Kind = "conic" },
            new() { Cat = "conic", Prompt = "画抛物线 y^2=4x 的图象，标出焦点和准线。", Kind = "conic" },
            new() { Cat = "conic", Prompt = "画抛物线 x^2=4y 的图象。", Kind = "conic" },
            new() { Cat = "conic", Prompt = "画圆 x^2+y^2=4 的图象。", Kind = "conic" },

            // 高中题
            new() { Cat = "exam", Prompt = "已知函数 f(x)=x^3-3*x，画出它的图象。" },
            new() { Cat = "exam", Prompt = "画出函数 y=2*sin(2*x+pi/6) 的图象。", Tag = "sin" },
            new() { Cat = "exam", Prompt = "画出函数 y=abs(x-1)+abs(x+2) 的图象。", Tag = "abs" },
            new() { Cat = "exam", Prompt = "画出函数 y=ln(x+1) 与 y=x 的图象。", MinSeries = 2 },
        };
        if (!string.IsNullOrWhiteSpace(filter))
            cases = cases.Where(c => c.Cat.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || c.Prompt.Contains(filter, StringComparison.OrdinalIgnoreCase))
                         .ToList();

        string dir = Path.Combine("reports", "plot-suite");
        Directory.CreateDirectory(dir);
        int pass = 0;
        var catTotal = new Dictionary<string, int>();
        var catPass = new Dictionary<string, int>();
        for (int i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            PlotSpec spec = null;
            string err = "";
            double sec = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var r = Task.Run(() => AiPlotClient.ParseAsync(
                        c.Prompt, cfg, CancellationToken.None)).GetAwaiter().GetResult();
                    spec = r.Spec; err = r.Error; sec = r.Seconds;
                    if (r.Ok && spec != null) break;
                }
                catch (Exception ex) { err = ex.Message; }
            }

            bool kindOk = spec != null && spec.Kind == c.Kind;
            int series = spec?.Series.Count ?? 0;
            int pieces = 0;
            string all = "";
            if (spec != null)
            {
                foreach (var s in spec.Series)
                {
                    pieces += s.Pieces.Count;
                    all += " " + PlotSpecJson.HighSchool(s.Expr);
                    foreach (var p in s.Pieces)
                        all += " " + PlotSpecJson.HighSchool(p.Expr);
                }
                if (spec.Conic != null) all += " conic:" + spec.Conic.Type;
            }
            bool tagOk = string.IsNullOrEmpty(c.Tag)
                         || all.Contains(c.Tag, StringComparison.OrdinalIgnoreCase);
            bool piecesOk = !c.NeedPieces || pieces >= 2;
            bool paramsOk = !c.NeedParams || (spec?.Params.Count ?? 0) > 0;
            bool seriesOk = c.Kind == "conic"
                ? spec?.Conic != null
                : series >= c.MinSeries;
            bool renderOk = false;
            ImageData img = null;
            if (kindOk)
            {
                try { img = PlotRenderer.Render(spec); renderOk = img != null; }
                catch (Exception ex) { err = "render:" + ex.Message; }
            }
            bool ok = kindOk && seriesOk && tagOk && piecesOk && paramsOk && renderOk;
            if (ok) pass++;
            catTotal[c.Cat] = catTotal.GetValueOrDefault(c.Cat) + 1;
            if (ok) catPass[c.Cat] = catPass.GetValueOrDefault(c.Cat) + 1;

            if (img != null)
            {
                string path = Path.Combine(dir,
                    $"{(ok ? "ok" : "fail")}-{i:00}-{c.Cat}.png");
                try
                {
                    var bytes = ExportFormats.Encode(path, img.Bgra, img.Width, img.Height,
                                                     1, out _);
                    if (bytes != null) File.WriteAllBytes(path, bytes);
                }
                catch { }
                img.Release();
            }

            string shortPrompt = c.Prompt.Length > 42 ? c.Prompt[..42] + "…" : c.Prompt;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} [{c.Cat}] {shortPrompt}");
            Console.WriteLine($"      kind={spec?.Kind ?? "-"}/{(c.Kind)} " +
                              $"series={series} pieces={pieces} params={spec?.Params.Count ?? 0} " +
                              $"tag={tagOk} render={renderOk} {sec:F1}s " +
                              (string.IsNullOrEmpty(err) ? "" : "err=" + ClipPlotSuite(err, 80)));
        }

        Console.WriteLine();
        Console.WriteLine("=== 分类成功率 ===");
        foreach (var kv in catTotal)
        {
            int p = catPass.GetValueOrDefault(kv.Key);
            Console.WriteLine($"{kv.Key,-10} {p}/{kv.Value} " +
                              $"{(kv.Value > 0 ? p * 100f / kv.Value : 0f):F0}%");
        }
        Console.WriteLine($"总体：{pass}/{cases.Count} " +
                          $"{(cases.Count > 0 ? pass * 100f / cases.Count : 0f):F0}%");
        Console.WriteLine($"截图：{dir}");
        ExitCode = pass * 100 >= cases.Count * 80 ? 0 : 1;
    }

    private static string ClipPlotSuite(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";
}
