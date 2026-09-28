using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace InkEngine;

/// <summary>自动更新的当前状态（界面拿它显示「检查更新」那一行）。</summary>
public enum UpdateStage
{
    /// <summary>没配更新源（默认状态，完全正常）。</summary>
    NotConfigured = 0,
    Idle,
    Checking,
    UpToDate,
    /// <summary>发现新版本（再点一次就下载）。</summary>
    Available,
    Downloading,
    /// <summary>下载校验完毕，马上重启换壳。</summary>
    Ready,
    Failed,
}

/// <summary>
/// **自动更新**（2026-09-29 起）。三件事：查清单 → 比版本 → 下载并校验 sha256；
/// 下载好之后交给一个"换壳"脚本（它等我们退出 → 把旧目录改名 → 解压新的 → 重启）。
///
/// 清单（`update.json`，扁平、只有字符串，**故意不用 JSON 库**——和 Settings 同一个理由）：
/// <code>
/// {
///   "version": "8.0.1",
///   "url":     "https://github.com/&lt;账号&gt;/&lt;仓库&gt;/releases/latest/download/InkTeach-8.0.1-win-x64.zip",
///   "sha256":  "……64 位十六进制……",
///   "notes":   "一句话说明",
///   "minVersion": "8.0.0"
/// }
/// </code>
///
/// 三个设计决定：
///   1. **不用 GitHub API**（`/releases/latest` 那个匿名限流 60 次/小时，几十台教室机一起
///      点就废了）。用 `releases/latest/download/update.json` 这个**恒定重定向**——
///      每个 release 把 update.json 挂成附件即可，没有 API、没有 token。
///   2. **地址默认留空**（<see cref="DefaultUrl"/>）：没配就什么都不做，
///      界面显示"未配置更新源"。想换成局域网共享只改字符串，代码不动。
///   3. **换壳用 PowerShell 脚本**（Win10/11 自带），不落地 .cmd：路径里的空格、
///      中文、引号在 .cmd 里是灾难。脚本内容固定，路径用参数传。
/// </summary>
internal static class UpdateFeed
{
    /// <summary>
    /// 默认更新源。**留空 = 不检查**（用户 2026-09-29："先不补，后期再补"）。
    /// 以后填成：`https://github.com/&lt;账号&gt;/&lt;仓库&gt;/releases/latest/download/update.json`
    /// 用户也可以用 settings.json 的 `update.url` 覆盖它（不用重新编译）。
    /// </summary>
    public const string DefaultUrl = "";

    /// <summary>实际用的源：settings.json 覆盖默认值（引擎启动时赋值）。</summary>
    public static string Url = DefaultUrl;

    /// <summary>本程序的版本（入口程序集，即 InkTeach.exe 的 `&lt;Version&gt;`）。</summary>
    public static string CurrentVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly()
                      ?? System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "0.0.0"; }
    }

    /// <summary>清单内容。字段都是字符串，空 = 没写。</summary>
    internal sealed class Manifest
    {
        public string Version = "";
        public string Url = "";
        public string Sha256 = "";
        public string Notes = "";
        public string MinVersion = "";
    }

    // ---- 版本比较 ------------------------------------------------------------

    /// <summary>
    /// 版本比较：`a &gt; b` 返回正数。只认数字段（"8.0.1" / "8.0.0.0" / "8.0.1-beta" 都行，
    /// 非数字段当 0）。**不能用字符串比**——"8.0.10" &gt; "8.0.9" 但字符串比会反过来。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = ParseParts(a);
        var pb = ParseParts(b);
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            int va = i < pa.Length ? pa[i] : 0;
            int vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va - vb;
        }
        return 0;
    }

    private static int[] ParseParts(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return Array.Empty<int>();
        var parts = v.Trim().Split('.');
        var nums = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            int end = 0;
            while (end < p.Length && char.IsAsciiDigit(p[end])) end++;
            nums.Add(end > 0 && int.TryParse(p.AsSpan(0, end), out int n) ? n : 0);
        }
        return nums.ToArray();
    }

    // ---- 取清单 --------------------------------------------------------------

    /// <summary>
    /// 取清单。**同时支持 http(s) 和本地路径 / file://**——后者是给自检用的：
    /// 自检不能依赖网络（也不该往真 GitHub 上打请求）。
    /// 失败返回 null 并给出用户能看懂的原因（网络错误、404、格式不对……）。
    /// </summary>
    public static Manifest Fetch(string url, out string error)
    {
        error = null;
        string text;
        try
        {
            if (IsLocal(url, out string path))
            {
                if (!File.Exists(path)) { error = $"清单文件不存在：{path}"; return null; }
                text = File.ReadAllText(path);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("InkTeach-updater/" + CurrentVersion);
                text = http.GetStringAsync(url).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            error = "取清单失败：" + Flatten(ex);
            return null;
        }

        var m = ParseManifest(text);
        if (m == null) { error = "清单格式不对（要 {\"version\":\"…\",\"url\":\"…\",\"sha256\":\"…\"}）"; return null; }
        if (m.Version.Length == 0) { error = "清单里没有 version"; return null; }
        return m;
    }

    private static bool IsLocal(string url, out string path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { path = new Uri(url).LocalPath; return true; } catch { return false; }
        }
        if (url.Contains("://", StringComparison.Ordinal)) return false;   // http / https / 别的
        path = url;                                                       // 裸路径
        return true;
    }

    /// <summary>扁平 JSON 扫描：只认 `"键": "值"` 一层（和我们自己的清单格式对上）。</summary>
    internal static Manifest ParseManifest(string text)
    {
        if (text == null) return null;
        int open = text.IndexOf('{');
        int close = text.LastIndexOf('}');
        if (open < 0 || close <= open) return null;
        var body = text.Substring(open + 1, close - open - 1);

        var m = new Manifest();
        int i = 0;
        while (i < body.Length)
        {
            int q1 = body.IndexOf('"', i);
            if (q1 < 0) break;
            int q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) break;
            string key = body.Substring(q1 + 1, q2 - q1 - 1);

            int colon = body.IndexOf(':', q2 + 1);
            if (colon < 0) break;
            int v1 = body.IndexOf('"', colon + 1);
            if (v1 < 0) break;
            int v2 = body.IndexOf('"', v1 + 1);
            if (v2 < 0) break;
            string value = body.Substring(v1 + 1, v2 - v1 - 1);

            switch (key.ToLowerInvariant())
            {
                case "version": m.Version = value.Trim(); break;
                case "url": m.Url = value.Trim(); break;
                case "sha256": m.Sha256 = value.Trim().ToLowerInvariant(); break;
                case "notes": m.Notes = value; break;
                case "minversion": m.MinVersion = value.Trim(); break;
            }
            i = v2 + 1;
        }
        return m;
    }

    // ---- 下载与校验 ----------------------------------------------------------

    /// <summary>
    /// 下载到 <paramref name="destFile"/> 并校验 sha256。**zhi 校验不过就删掉下载的文件**
    /// （绝不把一个来路不明的 zip 留在盘上）。<paramref name="progress"/> 可空。
    /// </summary>
    public static bool Download(string url, string destFile, string sha256,
                                Action<long, long> progress, out string error)
    {
        error = null;
        try
        {
            var dir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (IsLocal(url, out string path))
            {
                File.Copy(path, destFile, overwrite: true);
                progress?.Invoke(1, 1);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("InkTeach-updater/" + CurrentVersion);
                using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                                     .GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                using var src = resp.Content.ReadAsStream();
                using var dst = File.Create(destFile);
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    got += n;
                    progress?.Invoke(got, total);
                }
            }
        }
        catch (Exception ex)
        {
            error = "下载失败：" + Flatten(ex);
            TryDelete(destFile);
            return false;
        }

        if (!VerifySha256(destFile, sha256, out error))
        {
            TryDelete(destFile);          // 校验不过的包绝不留下
            return false;
        }
        return true;
    }

    /// <summary>校验 sha256（大小写不敏感）。<paramref name="expect"/> 为空 = 拒绝（宁可不让自动更新）。</summary>
    public static bool VerifySha256(string file, string expect, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(expect))
        {
            error = "清单里没有 sha256——拒绝下载（宁可不让它自动装）";
            return false;
        }
        try
        {
            using var fs = File.OpenRead(file);
            var hash = SHA256.HashData(fs);
            string got = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(got, expect.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                error = $"sha256 对不上（期望 {expect[..Math.Min(12, expect.Length)]}…，实际 {got[..12]}…）";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "算 sha256 失败：" + Flatten(ex);
            return false;
        }
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    private static string Flatten(Exception ex)
    {
        var e = ex;
        while (e.InnerException != null) e = e.InnerException;
        return e.Message;
    }

    // ---- 换壳 ----------------------------------------------------------------

    /// <summary>
    /// 换壳脚本的内容（固定；路径与 PID 由命令行参数传，避免引号地狱）。
    ///
    /// ⚠ **这份内容必须是纯 ASCII**（2026-09-29 踩过，代价是"更新装不上"）：
    /// Windows PowerShell 5.1 会把**没有 BOM 的 UTF-8 脚本按 ANSI 代码页读**，
    /// 中文字符的字节会把后面那个引号"吃掉"→ 整份脚本语法错误、一行都不执行。
    /// 写文件时另外补一个 BOM 做双保险（见 <see cref="WriteSwapScript"/>）。
    /// </summary>
    internal const string SwapScriptBody = """
        param(
          [Parameter(Mandatory=$true)][int]$AppPid,
          [Parameter(Mandatory=$true)][string]$AppDir,
          [Parameter(Mandatory=$true)][string]$Zip,
          [Parameter(Mandatory=$true)][string]$Exe
        )
        # ASCII-ONLY. Windows PowerShell 5.1 reads a BOM-less UTF-8 script using the
        # ANSI code page; a multi-byte char can swallow the next quote and the script
        # then fails to parse (measured 2026-09-29). Keep this file ASCII.
        $ErrorActionPreference = 'Continue'
        $log = Join-Path (Split-Path -Parent $PSCommandPath) 'swap.log'
        function Log($m) { try { Add-Content -LiteralPath $log -Value ("{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) } catch {} }

        Log "waiting for pid $AppPid (dir=$AppDir)"
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 300
        }
        Start-Sleep -Milliseconds 800

        # remove backups left by earlier updates (bounded: one generation)
        $parent = Split-Path -Parent $AppDir
        $leaf = Split-Path -Leaf $AppDir
        Get-ChildItem -LiteralPath $parent -Directory -Filter "$leaf.old-*" -ErrorAction SilentlyContinue |
            ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop; Log ("removed old backup " + $_.Name) } catch {} }

        $bak = "$AppDir.old-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
        $renamed = $false
        for ($i = 0; $i -lt 40; $i++) {
            try { Rename-Item -LiteralPath $AppDir -NewName (Split-Path -Leaf $bak) -ErrorAction Stop; $renamed = $true; break }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $renamed) { Log "rename failed (dir locked); giving up"; exit 1 }
        Log "old dir -> $bak"

        try {
            New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
            Expand-Archive -LiteralPath $Zip -DestinationPath $AppDir -Force -ErrorAction Stop
            Log "extracted $Zip"
        } catch {
            Log ("extract failed: " + $_.Exception.Message + " -- rolling back")
            try { Remove-Item -LiteralPath $AppDir -Recurse -Force -ErrorAction SilentlyContinue
                  Rename-Item -LiteralPath $bak -NewName $leaf -ErrorAction Stop } catch {}
            exit 1
        }

        try { Start-Process -FilePath $Exe; Log "restarted $Exe" }
        catch { Log ("restart failed: " + $_.Exception.Message) }
        """;

    /// <summary>
    /// 把换壳脚本写到更新目录里，返回脚本路径。
    /// **UTF-8 带 BOM**（虽然内容是纯 ASCII，也把 BOM 写上——PowerShell 5.1 靠它认编码）。
    /// </summary>
    public static string WriteSwapScript(string updateDir)
    {
        Directory.CreateDirectory(updateDir);
        string path = Path.Combine(updateDir, "swap.ps1");
        File.WriteAllText(path, SwapScriptBody,
                          new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>
    /// 拉起换壳脚本（隐藏窗口，脚本自己等我们退出）。返回 false = 没拉起来，
    /// 调用方**不要退出**（留在原地继续跑，总比"更新没装成、软件也没了"强）。
    /// </summary>
    public static bool LaunchSwap(string script, string appDir, string zip, string exe, out string error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(script),
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-AppPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("-AppDir");
            psi.ArgumentList.Add(appDir);
            psi.ArgumentList.Add("-Zip");
            psi.ArgumentList.Add(zip);
            psi.ArgumentList.Add("-Exe");
            psi.ArgumentList.Add(exe);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = "拉换壳脚本失败：" + Flatten(ex);
            return false;
        }
    }
}
