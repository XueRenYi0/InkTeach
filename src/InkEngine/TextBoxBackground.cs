using Vortice.Mathematics;

namespace InkEngine;

/// <summary>
/// 文本框 / 只读卡的背景预设。
///
/// 只有四个常用档，不在这里做 HSV 取色：
///  0 透明：文字直接浮在画布上；
///  1 白纸：白底 + 浅灰边；
///  2 板色：跟当前白板同色（只读卡强制浅色，避免黑底黑字）；
///  3 浅色卡：暖白底 + 暖灰边，适合便签/重点框。
///
/// 最终背景是真 alpha；编辑态由 RichEdit 画不透明近似色（见 TextBoxEditing）。
/// </summary>
internal static class TextBoxBackground
{
    public const int PresetCount = 4;

    public static readonly string[] Names = { "透明", "白纸", "板色", "浅色" };

    public static Color4 Fill(int preset, Color4 board, bool card)
    {
        switch (preset)
        {
            case 1:
                return new Color4(1f, 1f, 1f, 0.98f);
            case 2:
                // 只读卡的文字颜色是固定的深色/红色，深色板色会看不清；
                // 卡片统一退回浅色，普通文字才真正跟板色走。
                return card
                    ? new Color4(0.985f, 0.980f, 0.960f, 1f)
                    : new Color4(board.R, board.G, board.B, 0.98f);
            case 3:
                return new Color4(0.985f, 0.975f, 0.955f, 1f);
            default:
                return new Color4(0f, 0f, 0f, 0f);
        }
    }

    public static Color4 Border(int preset, Color4 board, bool card)
    {
        switch (preset)
        {
            case 1:
                return new Color4(0.86f, 0.88f, 0.91f, 1f);
            case 2:
                if (card) return new Color4(0.86f, 0.84f, 0.80f, 1f);
                return Luma(board) > 0.55f
                    ? new Color4(0f, 0f, 0f, 0.18f)
                    : new Color4(1f, 1f, 1f, 0.30f);
            case 3:
                return new Color4(0.87f, 0.85f, 0.80f, 1f);
            default:
                return new Color4(0f, 0f, 0f, 0f);
        }
    }

    public static float Luma(Color4 c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;

    public static Color4 InkFor(Color4 fill)
        => Luma(fill) > 0.55f
            ? new Color4(0.12f, 0.14f, 0.18f, 1f)
            : new Color4(0.98f, 0.98f, 0.98f, 1f);

    public static bool Same(Color4 a, Color4 b, float eps = 0.015f)
        => MathF.Abs(a.R - b.R) < eps
           && MathF.Abs(a.G - b.G) < eps
           && MathF.Abs(a.B - b.B) < eps
           && MathF.Abs(a.A - b.A) < eps;

    /// <summary>把半透明前景叠到不透明底色上，得到编辑窗能用的实色近似。</summary>
    public static Color4 Over(Color4 fg, Color4 bg)
    {
        float a = Math.Clamp(fg.A, 0f, 1f);
        return new Color4(
            fg.R * a + bg.R * (1f - a),
            fg.G * a + bg.G * (1f - a),
            fg.B * a + bg.B * (1f - a),
            1f);
    }
}
