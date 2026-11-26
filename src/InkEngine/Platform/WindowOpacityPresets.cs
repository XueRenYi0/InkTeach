namespace InkEngine;

/// <summary>
/// **整个批注窗口的透明度**档表（用户 2026-11-25："加一个透明度设置"）。
///
/// **档位表只在这一份**（引擎、界面、自检都读它）——本仓的老规矩：
/// 同一份名单写两处必漏一处。
///
/// 和 `BoardOpacity`（白板底色半透明、笔迹仍实）不同，这是**合成期**的整体透明度：
/// 笔迹、工具栏、面板一起淡出，能透出下面的 PPT / 桌面。
/// 命中测试、书写、穿透都不受影响。
///
/// 为什么放 public 而不是 internal：InkUi 是**独立程序集**，界面要用同一份档表
/// 算"下一档"和显示百分比（同 `UiScalePresets` 的做法）。
/// </summary>
public static class WindowOpacityPresets
{
    /// <summary>五档：不透明 / 85% / 70% / 55% / 40%（再低浮层就不好点了）。</summary>
    public static readonly float[] Tiers = { 1f, 0.85f, 0.70f, 0.55f, 0.40f };

    /// <summary>吸附到最近档（配置里手改过的怪值 / 旧版残留值不会产生"不存在"的档）。</summary>
    public static float Snap(float v)
    {
        if (float.IsNaN(v)) return Tiers[0];
        float best = Tiers[0], bd = float.MaxValue;
        foreach (var t in Tiers)
        {
            float d = System.MathF.Abs(t - v);
            if (d < bd) { bd = d; best = t; }
        }
        return best;
    }

    /// <summary>下一档（设置页"点一下换一档"的循环；到顶回头）。</summary>
    public static float Next(float v)
    {
        int i = System.Array.IndexOf(Tiers, Snap(v));
        return Tiers[(i + 1) % Tiers.Length];
    }

    /// <summary>存进配置的字符串：**不透明档不写项**（只写和默认不一样的那份的老规矩）。</summary>
    public static string StoreValue(float v)
    {
        v = Snap(v);
        return v >= 0.995f
            ? null
            : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
