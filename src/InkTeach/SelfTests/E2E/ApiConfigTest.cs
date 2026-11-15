using System.IO;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>自检：多服务商配置（预置 / 多条自定义 / 删除 / 旧格式迁移 / 推荐标签）。
    ///   --apiconfig
    /// </summary>
    private void ApiConfigTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== AI 服务商配置 ===");
        string dir = Path.Combine(Path.GetTempPath(), "InkTeachApiConfigTest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "ai.json");
        string old = OcrClient.ConfigPathOverride;
        OcrClient.ConfigPathOverride = file;
        bool ok = true;

        void Check(string name, bool pass, string detail = "")
        {
            Console.WriteLine($"  {(pass ? "OK  " : "FAIL")} {name} {detail}");
            ok &= pass;
        }

        try
        {
            var s = OcrProviders.Load();
            Check("默认预置 5 家", s.Providers.Count == 5, s.Providers.Count.ToString());
            Check("DeepSeek 推荐", OcrProviders.IsRecommended("deepseek-official", "deepseek-flash"));
            Check("阿里不推荐", !OcrProviders.IsRecommended("dashscope", "qwen3.5-omni-flash"));
            Check("显示名带推荐", OcrProviders.DisplayModel("deepseek-official", "deepseek-flash").EndsWith("推荐"));

            var c1 = OcrProviders.NewCustom("https://api.example.com/v1", s);
            c1.Key = "k1"; c1.Model = "m1"; s.Providers.Add(c1);
            var c2 = OcrProviders.NewCustom("https://api.example.com/v1", s);
            c2.Key = "k2"; c2.Model = "m2"; s.Providers.Add(c2);
            s.ActiveId = c2.Id;
            OcrProviders.Save(s);

            var r = OcrProviders.Load();
            var customs = r.Providers.FindAll(p => !p.Builtin);
            Check("自定义可以有多条", customs.Count == 2, customs.Count.ToString());
            Check("同域名自动加序号", customs[0].Name != customs[1].Name,
                  customs[0].Name + " / " + customs[1].Name);
            Check("当前配置保存", r.ActiveId == c2.Id, r.ActiveId);
            Check("自定义不误标推荐", !OcrProviders.IsRecommended(c2.Id, "m2"));

            r.Providers.RemoveAll(p => p.Id == c1.Id);
            if (r.ActiveId == c1.Id) r.ActiveId = r.Providers[0].Id;
            OcrProviders.Save(r);
            var afterDel = OcrProviders.Load();
            Check("删除自定义只删一条", afterDel.Providers.FindAll(p => !p.Builtin).Count == 1,
                  afterDel.Providers.FindAll(p => !p.Builtin).Count.ToString());

            // 旧版扁平文件迁移。
            File.WriteAllText(file, "{\"baseUrl\":\"https://api.deepseek.com\",\"key\":\"old\",\"model\":\"deepseek-flash\"}");
            var legacy = OcrProviders.Load();
            Check("旧格式迁移", legacy.Providers.Count == 5 && legacy.Active?.Key == "old",
                  legacy.Providers.Count + " / " + (legacy.Active?.Id ?? ""));
        }
        catch (System.Exception ex)
        {
            Console.WriteLine("FAIL：" + ex.Message);
            ok = false;
        }
        finally
        {
            OcrClient.ConfigPathOverride = old;
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(ok ? "结论：OK" : "结论：FAIL");
        ExitCode = ok ? 0 : 1;
    }

    /// <summary>自检：拉当前配置的模型列表（不打印 key）。--listmodels</summary>
    private void ListModelsTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 获取模型列表 ===");
        var cfg = OcrClient.LoadConfig();
        Console.WriteLine($"当前配置：{cfg.BaseUrl} / {cfg.Model} / 已配置={cfg.IsConfigured}");
        if (!cfg.IsConfigured)
        {
            Console.WriteLine("结论：FAIL（未配置）");
            ExitCode = 1;
            return;
        }
        var r = OcrClient.ListModelsAsync(cfg, System.Threading.CancellationToken.None)
                          .GetAwaiter().GetResult();
        Console.WriteLine($"结果：ok={r.Ok}，{r.Models.Count} 个模型，错误={r.Error}");
        for (int i = 0; i < Math.Min(8, r.Models.Count); i++)
            Console.WriteLine("  " + r.Models[i]);
        bool rec = OcrProviders.IsRecommended("deepseek-official", cfg.Model);
        Console.WriteLine($"当前模型推荐：{rec}");
        Console.WriteLine(r.Ok ? "结论：OK" : "结论：FAIL");
        ExitCode = r.Ok ? 0 : 1;
    }
}
