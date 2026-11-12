using System;
using System.IO;

namespace InkEngine;

/// <summary>
/// 隐私同意记录（2026-11-13）。
///
/// 为什么要单独存一份：这份记录以前和界面偏好一起放在 `settings.json` 的 `ui` 段里，
/// 而「从备份恢复」会整份替换 `settings.json`——只要备份里没有这条（或者备份来自
/// 老版本），下次启动就会**再弹一次**隐私说明。用户反馈“总是弹出来，太烦了”。
///
/// 现在单独写 `privacy.json`（和 settings.json 同目录、但不进备份包），
/// 恢复设置/换版本都不会丢；只有采集范围真的变了（<see cref="InkEngine.PrivacyVersion"/> 升级）
/// 才会再问一次。
/// </summary>
internal static class PrivacyStore
{
    private static string PathNow()
    {
        string dir = Path.GetDirectoryName(InkSettings.FilePath);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "privacy.json");
    }

    public static (int Ver, string Consent, string At)? Load()
    {
        try
        {
            string p = PathNow();
            if (!File.Exists(p)) return null;
            int ver = 0;
            string consent = "", at = "";
            foreach (string line in File.ReadAllLines(p))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line[..eq].Trim();
                string v = line[(eq + 1)..].Trim();
                if (k == "ver" && int.TryParse(v, out int n)) ver = n;
                else if (k == "consent") consent = v;
                else if (k == "at") at = v;
            }
            if (ver <= 0 || consent.Length == 0) return null;
            return (ver, consent, at);
        }
        catch { return null; }
    }

    public static void Save(int ver, bool granted, string at)
    {
        try
        {
            string p = PathNow();
            File.WriteAllText(p,
                "ver=" + ver + "\n" +
                "consent=" + (granted ? "granted" : "denied") + "\n" +
                "at=" + (at ?? "") + "\n");
        }
        catch { }
    }
}
