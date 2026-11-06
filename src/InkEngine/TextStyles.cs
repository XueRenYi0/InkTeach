using System;

namespace InkEngine;

/// <summary>
/// 普通文本框可选字体。**只放系统自带、Windows 上一定有的字体**，
/// 不把字体文件打进安装包；团队备课换机器也不会缺字。
///
/// 参考：Word 的字体下拉 / ClassIn 文本框的字体档位（用户 2026-11-06 点名要）。
/// 不照搬 Word 的完整字体列表：老师板书场景只需要“楷体 / 宋体 / 黑体 / 雅黑”四档。
/// </summary>
internal enum TextBoxFace
{
    YaHei = 0,
    KaiTi = 1,
    SimSun = 2,
    SimHei = 3,
}

internal static class TextBoxFaces
{
    internal readonly record struct Info(TextBoxFace Face, string Name, string DirectWrite);

    /// <summary>顺序 = 面板里的格序，也是存档里的编号，**不要重排**。</summary>
    public static readonly Info[] All =
    {
        new(TextBoxFace.YaHei, "雅黑", "Microsoft YaHei UI"),
        new(TextBoxFace.KaiTi, "楷体", "KaiTi"),
        new(TextBoxFace.SimSun, "宋体", "SimSun"),
        new(TextBoxFace.SimHei, "黑体", "SimHei"),
    };

    /// <summary>行距档位（倍数）。1.5 是现有默认，前后各留常用档。</summary>
    public static readonly float[] LineSpacings = { 1.0f, 1.2f, 1.5f, 1.8f, 2.0f };

    public const float DefaultLineSpacing = 1.55f;

    public static Info Get(TextBoxFace face)
    {
        foreach (var info in All)
            if (info.Face == face) return info;
        return All[0];
    }

    public static string DirectWrite(TextBoxFace face) => Get(face).DirectWrite;

    public static string Name(TextBoxFace face) => Get(face).Name;

    public static TextBoxFace Clamp(int index)
        => index >= 0 && index < All.Length ? All[index].Face : TextBoxFace.YaHei;

    public static float ClampSpacing(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0.9f, 2.5f) : DefaultLineSpacing;
}
