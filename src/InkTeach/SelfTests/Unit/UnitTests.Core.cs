using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

/// <summary>
/// 第一批 L1 纯逻辑测试：不建窗口、不碰 D2D、不读写用户目录。
/// 只覆盖“改坏了单元测试能立刻报”的纯函数/纯数据层。
/// </summary>
internal static class UnitTestsCore
{
    private static Stroke MakeStroke(int points = 4, float width = 2f)
    {
        var s = new Stroke
        {
            Tool = Tool.Pen,
            Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0.2f, 0.3f, 1f),
            Width = width,
        };
        for (int i = 0; i < points; i++)
            s.Points.Add(new InkPoint { X = i * 10, Y = i * 5, P = 0.5f, T = i * 8 });
        return s;
    }

    [UnitTest]
    internal static void Serializer_RoundTrip_PreservesCountAndColor()
    {
        var src = MakeStroke(4);
        src.Id = 42;
        byte[] blob = InkSerializer.SaveStrokes(new[] { src });
        var back = InkSerializer.LoadStrokes(blob, out int maxId);

        Check.Equal(1, back.Count, file: "InkSerializer");
        Check.Equal(4, back[0].Points.Count, file: "InkSerializer");
        Check.Near(src.Color.R, back[0].Color.R, file: "InkSerializer");
        Check.Near(src.Color.G, back[0].Color.G, file: "InkSerializer");
        Check.Equal(42, maxId, file: "InkSerializer");
    }

    [UnitTest]
    internal static void EditAction_AddStrokes_UndoRedo()
    {
        var doc = new InkDocument();
        var s = MakeStroke();
        var act = new AddStrokesAction();
        act.Strokes.Add(s);

        act.Redo(doc);
        Check.Equal(1, doc.Strokes.Count, file: "AddStrokesAction");
        act.Undo(doc);
        Check.Equal(0, doc.Strokes.Count, file: "AddStrokesAction");
        act.Redo(doc);
        Check.Equal(1, doc.Strokes.Count, file: "AddStrokesAction");
    }

    [UnitTest]
    internal static void UiScale_Snap_To_Nearest_Tier()
    {
        Check.Equal(1f, UiScalePresets.Snap(1.02f));
        Check.Equal(1.3f, UiScalePresets.Snap(1.27f));
        Check.Equal(0.8f, UiScalePresets.Snap(0.70f));
    }

    [UnitTest]
    internal static void CollectionFingerprint_SameContent_SameKey()
    {
        var a = MakeStroke(4);
        var b = MakeStroke(4);
        var c = MakeStroke(5);

        string fa = CollectionFingerprint.Of(new[] { a });
        string fb = CollectionFingerprint.Of(new[] { b });
        string fc = CollectionFingerprint.Of(new[] { c });

        Check.Equal(fa, fb, file: "CollectionFingerprint");
        Check.True(fa != fc, "different point count should change fingerprint", file: "CollectionFingerprint");
    }

    [UnitTest]
    internal static void Serializer_Format_Is_Stable()
    {
        var s = MakeStroke(3);
        s.Id = 7;
        byte[] blob = InkSerializer.SaveStrokes(new[] { s });
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(blob));
        // Golden Master：格式一变就红。有意改格式时更新这一行并在 commit 说明原因。
        Check.Equal("198DB9B4C51D4A54045EAFF8AD80435D6624C8F81DE0109509A35DC8CEEBF33D",
            hash, file: "InkSerializer");
    }

    // ---- raw 补点分段校准（关 ink 快写乱画的回归哨兵）------------------------

    [UnitTest]
    internal static void RawInputSegmentBuffer_AheadOfPointer_NoBackwardAndKeepsTail()
    {
        var buf = new RawInputSegmentBuffer();
        var output = new List<(float X, float Y, double TimeMs)>();

        // WM_INPUT 超前 1 包：指针消息只走到 100，raw 已经报到了 150。
        buf.Add(50f, 0f, 1);
        buf.Add(50f, 0f, 2);
        buf.Add(50f, 0f, 3);

        int n = buf.EmitBetween(0f, 0f, 0, 100f, 0f, 10, 3f, output);
        Check.Equal(1, n, file: "RawInputSegmentBuffer");
        Check.Near(50f, output[0].X, file: "RawInputSegmentBuffer");
        Check.Near(0f, output[0].Y, file: "RawInputSegmentBuffer");
        Check.True(output[0].X <= 100f, "emitted point must not overshoot the segment end",
            file: "RawInputSegmentBuffer");
        // 第 3 包（超前那一包）留给下一段，不能丢也不能提前画。
        Check.Equal(1, buf.Count, file: "RawInputSegmentBuffer");

        int n2 = buf.EmitBetween(100f, 0f, 10, 200f, 0f, 20, 3f, output);
        Check.Equal(1, n2, file: "RawInputSegmentBuffer");
        Check.Near(150f, output[0].X, file: "RawInputSegmentBuffer");
        Check.True(output[0].TimeMs >= 10, "next segment timestamps must not go backward",
            file: "RawInputSegmentBuffer");
        Check.Equal(0, buf.Count, file: "RawInputSegmentBuffer");
    }

    [UnitTest]
    internal static void RawInputSegmentBuffer_RejectsNoiseAndBackwardPoints()
    {
        var buf = new RawInputSegmentBuffer();
        var output = new List<(float X, float Y, double TimeMs)>();

        // 横向 100px 的一段里掺了垂直 80px 的噪点，另有一条倒退的报。
        buf.Add(20f, 0f, 1);
        buf.Add(20f, -80f, 2);
        buf.Add(20f, 0f, 3);
        buf.Add(-40f, 0f, 4);
        buf.Add(80f, 0f, 5);

        int n = buf.EmitBetween(0f, 0f, 0, 100f, 0f, 10, 3f, output);
        Check.Equal(1, n, file: "RawInputSegmentBuffer");
        Check.Near(20f, output[0].X, file: "RawInputSegmentBuffer");
        Check.Near(0f, output[0].Y, file: "RawInputSegmentBuffer");
        for (int i = 1; i < n; i++)
            Check.True(output[i].TimeMs >= output[i - 1].TimeMs,
                "timestamps must be monotonic", file: "RawInputSegmentBuffer");
        Check.Equal(0, buf.Count, file: "RawInputSegmentBuffer");
    }

    [UnitTest]
    internal static void RawInputSegmentBuffer_ScaleMismatch_DoesNotOvershoot()
    {
        var output = new List<(float X, float Y, double TimeMs)>();

        // 0.5x 比例偏差：raw 报的位移比指针消息少一半，累计会冲过终点 1.5 倍。
        var slow = new RawInputSegmentBuffer();
        for (int i = 0; i < 6; i++) slow.Add(25f, 0f, i);
        slow.EmitBetween(0f, 0f, 0, 100f, 0f, 60, 3f, output);
        Check.True(output.Count > 0, "half-scale segment should still emit", file: "RawInputSegmentBuffer");
        for (int i = 0; i < output.Count; i++)
        {
            Check.True(output[i].X >= 0f && output[i].X <= 100f,
                "emitted point must stay inside [start, end]", file: "RawInputSegmentBuffer");
            if (i > 0) Check.True(output[i].X > output[i - 1].X,
                "emitted points must move forward", file: "RawInputSegmentBuffer");
        }

        // 2x 比例偏差：超前的余量必须留给下一段，而不是冲出终点再被拉回来。
        var fast = new RawInputSegmentBuffer();
        for (int i = 0; i < 4; i++) fast.Add(50f, 0f, i);
        fast.EmitBetween(0f, 0f, 0, 100f, 0f, 10, 3f, output);
        for (int i = 0; i < output.Count; i++)
            Check.True(output[i].X <= 100f, "double-scale must not overshoot",
                file: "RawInputSegmentBuffer");
        fast.EmitBetween(100f, 0f, 10, 200f, 0f, 20, 3f, output);
        for (int i = 0; i < output.Count; i++)
            Check.True(output[i].X >= 100f && output[i].X <= 200f,
                "tail must be emitted inside the next segment", file: "RawInputSegmentBuffer");
    }
}
