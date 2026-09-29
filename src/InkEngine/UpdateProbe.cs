using System.Diagnostics;

namespace InkEngine;

/// <summary>
/// `--updatetest`：**自动更新的自检**（纯离线，不碰网络、不真的换壳）。
///
/// 覆盖的是"错了会很难看"的那几处：
///   · 版本比较——字符串比会把 8.0.10 判成比 8.0.9 旧（这种错会让人永远收不到更新）；
///   · 清单解析——缺字段、多个空格、脏格式，都必须能读出"缺什么"而不是崩；
///   · sha256——**校验不过必须删掉下载的包**（盘上留着来路不明的 zip 是最坏的结果）；
///   · 换壳脚本——生成出来要能过 PowerShell 的**语法解析**（不执行）。
///
/// 真下真装那一步不在这里做：那要动正在运行的目录，属于人工流程（README 里写了怎么验）。
/// </summary>
internal static class UpdateProbe
{
    public static int Run()
    {
        Console.WriteLine("=== 自动更新自检（离线）===");
        Console.WriteLine($"  当前版本 {UpdateFeed.CurrentVersion}；"
                          + (UpdateFeed.Url.Length > 0
                             ? $"用户配置源 {UpdateFeed.Url}"
                             : $"候选源 {UpdateFeed.Sources.Length} 条（未填的会跳过）"));
        Console.WriteLine();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-50} {detail}");
            if (ok) pass++; else fail++;
        }

        // ---- ① 版本比较 ------------------------------------------------------
        Check("版本：8.0.1 > 8.0.0", UpdateFeed.CompareVersions("8.0.1", "8.0.0") > 0);
        Check("版本：8.0.10 > 8.0.9（字符串比会判反）", UpdateFeed.CompareVersions("8.0.10", "8.0.9") > 0);
        Check("版本：8.0.0 == 8.0.0.0", UpdateFeed.CompareVersions("8.0.0", "8.0.0.0") == 0);
        Check("版本：8.0.0 < 8.1", UpdateFeed.CompareVersions("8.0.0", "8.1") < 0);
        Check("版本：垃圾串当 0，不崩", UpdateFeed.CompareVersions("abc", "8.0.0") < 0,
              "abc → 0.0.0");

        // ---- ② 清单解析 ------------------------------------------------------
        var m1 = UpdateFeed.ParseManifest("""
            {
              "version": "8.0.1",
              "url":     "https://example.com/InkTeach-8.0.1-win-x64.zip",
              "sha256":  "AABBCC",
              "notes":   "修了三个 bug"
            }
            """);
        Check("清单：正常解析（含换行与空格）",
              m1 != null && m1.Version == "8.0.1" && m1.Url.EndsWith(".zip")
              && m1.Sha256 == "aabbcc" && m1.Notes == "修了三个 bug",
              m1 == null ? "解析失败" : $"{m1.Version} / sha={m1.Sha256}");

        var m2 = UpdateFeed.ParseManifest("""{ "version": "9.9.9" }""");
        Check("清单：缺 url / sha256 也能解析出来（由引擎判'不完整'）",
              m2 != null && m2.Version == "9.9.9" && m2.Url.Length == 0 && m2.Sha256.Length == 0);

        Check("清单：不是 JSON → null（不崩）", UpdateFeed.ParseManifest("随便一段话") == null);
        Check("清单：null → null", UpdateFeed.ParseManifest(null) == null);

        // ---- ③ 本地取清单（file:// 与裸路径）---------------------------------
        string dir = Path.Combine(Path.GetTempPath(), "inkteach-updatetest");
        try { Directory.CreateDirectory(dir); } catch { }
        string manifestPath = Path.Combine(dir, "update.json");
        File.WriteAllText(manifestPath, """
            { "version": "7.6.5", "url": "本地.zip", "sha256": "00", "notes": "本地自检" }
            """);

        var f1 = UpdateFeed.Fetch(manifestPath, out string e1);
        Check("取清单：裸路径", f1 != null && f1.Version == "7.6.5", e1 ?? "");
        var f2 = UpdateFeed.Fetch("file:///" + manifestPath.Replace('\\', '/'), out string e2);
        Check("取清单：file:// URL", f2 != null && f2.Version == "7.6.5", e2 ?? "");
        var f3 = UpdateFeed.Fetch(Path.Combine(dir, "不存在.json"), out string e3);
        Check("取清单：不存在的文件 → 失败且给出原因", f3 == null && !string.IsNullOrEmpty(e3), e3 ?? "");

        // ---- ③b 镜像加速站：候选表 + zip 地址前缀重写 ---------------------------
        // 背景：教室机连不上 GitHub（实测 21 秒超时），所以候选表前面是国内加速站。
        // 这里盯住两件"错了就装不上"的事：①候选表结构没被改坏；②清单里的 GitHub
        // 直链必须被套上同一个前缀，否则会"查得到新版、下不动包"。
        const string ghZip = "https://github.com/a/b/releases/download/v1/x.zip";
        const string px = "https://gh-proxy.com/";
        Check("镜像：候选源 >= 5 条且都指向同一份 raw 清单",
              UpdateFeed.Sources.Length >= 5
              && UpdateFeed.Sources.All(s => s.Url.Contains("InkTeach/main/update.json")));
        Check("镜像：至少 3 条带加速前缀，GitHub 直连放最后一条",
              UpdateFeed.Sources.Count(s => s.Prefix.Length > 0) >= 3
              && UpdateFeed.Sources[UpdateFeed.Sources.Length - 1].Prefix.Length == 0);
        Check("镜像：GitHub 直链 → 套上前缀", UpdateFeed.RewriteZipUrl(ghZip, px) == px + ghZip);
        Check("镜像：局域网 / Gitee 地址 → 一律不动",
              UpdateFeed.RewriteZipUrl(@"\\server\share\x.zip", px) == @"\\server\share\x.zip"
              && UpdateFeed.RewriteZipUrl("https://gitee.com/a/b/raw/main/x.zip", px) == "https://gitee.com/a/b/raw/main/x.zip");
        Check("镜像：无前缀（GitHub 直连那条）→ 不动", UpdateFeed.RewriteZipUrl(ghZip, "") == ghZip);
        Check("镜像：已带前缀 → 不叠两次", UpdateFeed.RewriteZipUrl(px + ghZip, px) == px + ghZip);

        // ---- ④ sha256 与下载校验 ---------------------------------------------
        string payload = Path.Combine(dir, "payload.bin");
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(payload, bytes);
        string good = Sha(payload);

        Check("sha256：正确 → 通过", UpdateFeed.VerifySha256(payload, good, out _));
        Check("sha256：大小写不敏感", UpdateFeed.VerifySha256(payload, good.ToUpperInvariant(), out _));
        Check("sha256：清单里没写 → 拒绝", !UpdateFeed.VerifySha256(payload, "", out string e4), e4 ?? "");

        bytes[10] ^= 0xFF;
        File.WriteAllBytes(payload, bytes);
        Check("sha256：内容变了 → 不通过", !UpdateFeed.VerifySha256(payload, good, out _));

        // 下载（本地源）校验通过
        File.WriteAllBytes(payload, bytes);
        string dst = Path.Combine(dir, "dl-ok.zip");
        bool dlOk = UpdateFeed.Download(payload, dst, Sha(payload), null, out string e5);
        Check("下载：本地源 + 正确 sha256 → 成功且文件在",
              dlOk && File.Exists(dst), e5 ?? "");

        // 下载校验不过 → **文件必须被删掉**
        string dst2 = Path.Combine(dir, "dl-bad.zip");
        bool dlBad = UpdateFeed.Download(payload, dst2, new string('0', 64), null, out string e6);
        Check("下载：sha256 不对 → 失败，且**不留包**",
              !dlBad && !File.Exists(dst2), e6 ?? "");

        // ---- ⑤ 设置里的 update.url（**整文件重写不能把用户填的抹掉**）---------
        string cfg = Path.Combine(dir, "settings.json");
        string oldOverride = InkSettings.PathOverride;
        InkSettings.PathOverride = cfg;
        try
        {
            File.WriteAllText(cfg, """{ "version": 1, "update": { "url": "https://example.com/u.json" } }""");
            Check("设置：读得到 update.url",
                  InkSettings.LoadUpdateUrl() == "https://example.com/u.json",
                  InkSettings.LoadUpdateUrl() ?? "（null）");

            File.WriteAllText(cfg, """{ "version": 1 }""");
            Check("设置：没有 update 段 → null（不报错）", InkSettings.LoadUpdateUrl() == null);

            // ⚠ 这一条是防回归的：Save 是**整文件重写**，忘了带 update 段就会把
            //   用户填的更新源悄悄抹掉（键位那边用"只写差异"绕开了同一个坑）。
            var map = KeyMap.Default();
            InkSettings.Save(map, null, "https://example.com/u.json");
            Check("设置：保存后 update.url 还在（重写没抹掉）",
                  InkSettings.LoadUpdateUrl() == "https://example.com/u.json",
                  InkSettings.LoadUpdateUrl() ?? "（null）");

            InkSettings.Save(map, null, null);
            Check("设置：不带更新源保存 → 文件里没有 update 段", InkSettings.LoadUpdateUrl() == null);
        }
        finally { InkSettings.PathOverride = oldOverride; }

        // ---- ⑥ 换壳脚本：**在沙箱里真跑一遍**（不碰真目录、不真重启）------------
        //
        // ⚠ 这条以前只做"语法解析"，结果漏掉一个真 bug：Windows PowerShell 5.1 把
        //   没有 BOM 的 UTF-8 脚本按 ANSI 读，中文吃掉引号 → 整份脚本语法错、一行都不跑
        //   （2026-09-29 实测：更新下载完了但装不上）。当时那条"语法检查"还是**通过**的——
        //   因为它用 Get-Content 读文件，解码路径和 PowerShell 宿主不一样。
        //   所以现在改成**真跑**：假安装目录 + 一个能解出 marker 的小 zip + 不存在的 Exe，
        //   跑完验"备份留了、新文件到位、日志里有 extracted"。
        string sandbox = Path.Combine(dir, "sandbox");
        try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
        Directory.CreateDirectory(sandbox);
        string instDir = Path.Combine(sandbox, "App-1.0");
        Directory.CreateDirectory(instDir);
        File.WriteAllText(Path.Combine(instDir, "old-marker.txt"), "old");
        string stage = Path.Combine(sandbox, "stage");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "new-marker.txt"), "new");
        string newZip = Path.Combine(sandbox, "new.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(stage, newZip);
        string swapDir = Path.Combine(sandbox, "upd");
        string swapScript = UpdateFeed.WriteSwapScript(swapDir);

        var (swapOk, swapMsg) = RunSwapSandbox(swapScript, instDir, newZip);
        bool newArrived = File.Exists(Path.Combine(instDir, "new-marker.txt"));
        bool backupKept = Directory.GetDirectories(sandbox, "App-1.0.old-*").Length > 0;
        string swapLog = "";
        try { swapLog = File.ReadAllText(Path.Combine(swapDir, "swap.log")); } catch { }

        Check("换壳（沙箱真跑）：跑得起来且解压到位", swapOk && newArrived, swapMsg);
        Check("换壳（沙箱真跑）：旧目录留了备份（可回滚）", backupKept,
              backupKept ? Directory.GetDirectories(sandbox, "App-1.0.old-*")[0] : "没找到 .old-* 备份");
        Check("换壳（沙箱真跑）：日志里记着 extracted",
              swapLog.Contains("extracted", StringComparison.Ordinal),
              swapLog.Replace('\r', ' ').Replace('\n', ' ').Trim());
        Check("换壳：脚本内容**纯 ASCII**（PS 5.1 按 ANSI 读无 BOM 脚本，中文会吃掉引号）",
              UpdateFeed.SwapScriptBody.All(c => c < 128),
              "含非 ASCII 字符");

        Console.WriteLine();
        Console.WriteLine($"  合计 {pass + fail} 条：通过 {pass}，失败 {fail}");
        Console.WriteLine();
        return fail == 0 ? 0 : 1;
    }

    private static string Sha(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// 在沙箱里跑一遍换壳脚本：`-Exe` 指向一个不存在的程序，所以只验
    /// "等进程退出 → 改名留备份 → 解压 → 重启那一步失败但不崩"。
    /// </summary>
    private static (bool ok, string msg) RunSwapSandbox(string script, string instDir, string zip)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-AppPid");
            psi.ArgumentList.Add("999999");                       // 不存在的 pid → 立刻往下走
            psi.ArgumentList.Add("-AppDir");
            psi.ArgumentList.Add(instDir);
            psi.ArgumentList.Add("-Zip");
            psi.ArgumentList.Add(zip);
            psi.ArgumentList.Add("-Exe");
            psi.ArgumentList.Add(Path.Combine(instDir, "no-such-app.exe"));
            using var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120000))
            {
                try { p.Kill(true); } catch { }
                return (false, "换壳脚本超时（120 秒）");
            }
            string msg = err.Trim().Length > 0 ? err.Trim() : outp.Trim();
            return (p.ExitCode == 0, msg.Length > 0 ? msg : ("exit=" + p.ExitCode));
        }
        catch (Exception ex)
        {
            return (false, "起不来 PowerShell：" + ex.Message);
        }
    }
}
