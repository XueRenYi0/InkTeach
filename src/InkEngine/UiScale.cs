namespace InkEngine;

/// <summary>
/// 界面缩放（2026-10-10 用户拍板"做 A：档位式全局缩放"）。
///
/// **档位表只在这一份**（引擎、界面、自检都读它）——本仓的老规矩：
/// 同一份名单写两处必漏一处。
///
/// 语义：`物理 = 逻辑 × DpiScale × UiScale`。
/// 界面层自己的坐标空间**不变**（单位仍是"逻辑像素"，界面永远不碰物理像素）；
/// 引擎在"逻辑 → 物理"的那一步把这枚乘数一起乘上，同时把界面的
/// "逻辑屏幕"除以 UiScale（不然 1.3 倍下工具条会被推到屏幕外）。
///
/// 谁缩、谁不缩（和用户过的口径）：**只有界面**——工具条 / 色带 / 抽屉 /
/// 设置页 / 图库面板。笔迹宽度、画布内容、取景框这些"纸上的尺"不跟着缩。
/// 浮层家具（选中框手柄、操作条、课堂计时窗）是第二批，见交接单。
/// </summary>
public static class UiScalePresets
{
    /// <summary>五档：小 / 较小 / 标准 / 较大 / 大（下限按用户要求加过一档 0.8）。</summary>
    public static readonly float[] Tiers = { 0.8f, 0.9f, 1f, 1.15f, 1.3f };

    /// <summary>吸附到最近档（配置与命令行进来的一切数值都先过这里；
    /// 顺手认一下 80/100/130 这种百分比写法）。</summary>
    public static float Snap(float v)
    {
        if (v > 10f) v /= 100f;            // 100 → 1.0、130 → 1.3
        float best = 1f, bd = float.MaxValue;
        foreach (var t in Tiers)
        {
            float d = System.MathF.Abs(t - v);
            if (d < bd) { bd = d; best = t; }
        }
        return best;
    }

    /// <summary>档位短名（设置页那一行右侧显示用）。</summary>
    public static string Name(float v) => Snap(v) switch
    {
        <= 0.8f => "小",
        <= 0.9f => "较小",
        <= 1.0f => "标准",
        <= 1.15f => "较大",
        _ => "大",
    };

    /// <summary>下一档（设置页"点一下换一档"的循环；到顶回头）。</summary>
    public static float Next(float v)
    {
        int i = System.Array.IndexOf(Tiers, Snap(v));
        return Tiers[(i + 1) % Tiers.Length];
    }

    /// <summary>存进配置的字符串：**标准档不写项**（只写和默认不一样的那份的老规矩）。</summary>
    public static string StoreValue(float v)
    {
        v = Snap(v);
        return v == 1f
            ? null
            : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
