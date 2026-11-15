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
}
