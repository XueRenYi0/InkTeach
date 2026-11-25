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
        // 2026-11-25 v36：每条笔画末尾追加"精确折线标记"（Stroke.ExactPolyline），
        // 多一个字节 → 这个哈希整体换代（只改格式，不动其它字段）。
        Check.Equal("B6E153E17BED6302DC740679B2E1D06A88C7ABBC1D3CCF2B799907F1E5475EF2",
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

    // ---- 截图框选条：三颗按钮的布局 / 贴到白板的落图位置 ----------------------

    [UnitTest]
    internal static void CaptureButtons_ThreeButtons_OrderAndClampedInViewport()
    {
        // 视口 (0,0)..(1600,900)，框在右下角：按钮必须整体夹回视口内、且仍是
        // 左→右 cancel < toBoard < ok、三颗等宽等距。
        var vp = new RectF { MinX = 0f, MinY = 0f, MaxX = 1600f, MaxY = 900f };
        InkEngine.InkEngine.ComputeCaptureButtons(vp, 1200f, 700f, 1590f, 890f,
                                                  96f, 30f, 8f,
                                                  out var cancel, out var toBoard, out var ok);

        Check.True(cancel.MaxX <= toBoard.MinX && toBoard.MaxX <= ok.MinX, "从左到右必须 cancel < toBoard < ok",
                   file: "ComputeCaptureButtons");
        Check.Near(96f, cancel.MaxX - cancel.MinX, 0.001f, file: "按钮宽");
        Check.Near(8f, toBoard.MinX - cancel.MaxX, 0.001f, file: "间距 1");
        Check.Near(8f, ok.MinX - toBoard.MaxX, 0.001f, file: "间距 2");
        Check.True(ok.MaxX <= vp.MaxX - 3.9f, "最后一颗不能出右边", file: "ComputeCaptureButtons");
        Check.True(ok.MaxY <= vp.MaxY - 3.9f, "按钮不能出底边", file: "ComputeCaptureButtons");
        Check.True(cancel.MinY >= vp.MinY, "按钮不能出顶边", file: "ComputeCaptureButtons");
        // 下方放不下 → 翻到框上方
        Check.True(ok.MaxY <= 700f, "下方放不下时应翻到框上方", file: "ComputeCaptureButtons");

        // 框在左上角、下方放得下：按钮贴框下面，且不越过框的左缘太多
        InkEngine.InkEngine.ComputeCaptureButtons(vp, 100f, 100f, 400f, 260f,
                                                  96f, 30f, 8f,
                                                  out var c2, out var b2, out var o2);
        Check.True(MathF.Abs(c2.MinY - 268f) < 0.001f, "放得下时按钮贴在框下方 gap 处", file: "ComputeCaptureButtons");
        Check.True(o2.MaxX - c2.MinX < vp.MaxX, "三颗按钮不超视口", file: "ComputeCaptureButtons");
    }

    [UnitTest]
    internal static void BoardCapturePlacement_NormalAndTall_TopCenteredWithWriteSpace()
    {
        var vp = new RectF { MinX = 0f, MinY = 0f, MaxX = 1600f, MaxY = 900f };

        // 普通单题 300×200（dpi 1、画布 100%）：1:1、水平居中、只留顶部距。
        var normal = InkEngine.InkEngine.ComputeBoardCapturePlacement(vp, 300f, 200f, 1f, 1f);
        Check.Near(1f, normal.Scale, 0.001f, file: "普通题应 1:1");
        Check.Near(48f, normal.Y, 0.001f, file: "顶部留距");
        Check.Near((1600f - 300f) * 0.5f, normal.X, 0.5f, file: "水平居中");

        // 超高题目 300×2000：缩到 55% 视口高以内，下方仍留作答空白。
        var tall = InkEngine.InkEngine.ComputeBoardCapturePlacement(vp, 300f, 2000f, 1f, 1f);
        float drawnH = 2000f * tall.Scale;
        Check.True(drawnH <= 900f * 0.55f - 48f + 0.5f, "超高题必须缩进 55% 视口高", file: "贴白板");
        Check.True(tall.Scale < 1f, "超高题要缩小", file: "贴白板");
        Check.True(tall.Y + drawnH <= 900f * 0.55f + 1f, "上半屏放题", file: "贴白板");
        Check.True(900f - (tall.Y + drawnH) >= 900f * 0.44f, "下方至少留 ~45% 作答区", file: "贴白板");
        Check.Near((1600f - 300f * tall.Scale) * 0.5f, tall.X, 0.5f, file: "缩小后仍水平居中");
    }

    // ---- 呼出盘色带轮切：重复色不能打转（2026-11-23 修的真 bug）-------------------

    [UnitTest]
    internal static void RadialBandStep_DuplicateColors_VisitsEverySlot()
    {
        // 用户实测的色带：红 / 黑 / 红 / 白 / 红 / 蓝（3 个红）。
        Color4 red = new(1f, 0f, 0f, 1f);
        Color4 black = new(0f, 0f, 0f, 1f);
        Color4 white = new(1f, 1f, 1f, 1f);
        Color4 blue = new(0f, 0f, 1f, 1f);
        var band = new[] { red, black, red, white, red, blue };

        // 正向：0 → 1(黑) → 2(红) → 3(白) → 4(红) → 5(蓝) → 0(红)
        int slot = 0;
        var forward = new List<int>();
        for (int i = 0; i < band.Length; i++)
        {
            slot = InkEngine.InkEngine.StepPenBandIndex(band, slot, band[slot], +1);
            forward.Add(slot);
        }
        Check.Equal("1,2,3,4,5,0", string.Join(',', forward), file: "RadialBandStep");

        // 反向：0 → 5(蓝) → 4(红) → 3(白) → 2(红) → 1(黑) → 0(红)
        slot = 0;
        var backward = new List<int>();
        for (int i = 0; i < band.Length; i++)
        {
            slot = InkEngine.InkEngine.StepPenBandIndex(band, slot, band[slot], -1);
            backward.Add(slot);
        }
        Check.Equal("5,4,3,2,1,0", string.Join(',', backward), file: "RadialBandStep");
    }

    [UnitTest]
    internal static void RadialBandStep_FallsBackWhenSlotUnusable()
    {
        Color4 red = new(1f, 0f, 0f, 1f);
        Color4 black = new(0f, 0f, 0f, 1f);
        Color4 white = new(1f, 1f, 1f, 1f);
        Color4 blue = new(0f, 0f, 1f, 1f);
        Color4 green = new(0f, 1f, 0f, 1f);
        var band = new[] { red, black, red, white, red, blue };

        // 没有选中槽位（-1）：按颜色回退到首个相近色（槽 2 的红 → 命中槽 0），再正向挪一格。
        Check.Equal(1, InkEngine.InkEngine.StepPenBandIndex(band, -1, band[2], +1), file: "RadialBandStep");
        // 槽位越界（99）也走同一条回退。
        Check.Equal(1, InkEngine.InkEngine.StepPenBandIndex(band, 99, band[2], +1), file: "RadialBandStep");

        // 槽位和颜色脱节（槽 3 是白，当前色却是蓝）：不能信槽位，改按颜色找到槽 5，再正向到 0。
        Check.Equal(0, InkEngine.InkEngine.StepPenBandIndex(band, 3, blue, +1), file: "RadialBandStep");
        Check.Equal(4, InkEngine.InkEngine.StepPenBandIndex(band, 3, blue, -1), file: "RadialBandStep");

        // 当前色不在色带里：正向从第一格开始、反向从最后一格开始（旧语义）。
        Check.Equal(0, InkEngine.InkEngine.StepPenBandIndex(band, -1, green, +1), file: "RadialBandStep");
        Check.Equal(5, InkEngine.InkEngine.StepPenBandIndex(band, -1, green, -1), file: "RadialBandStep");

        // 空色带不能除零、不能抛。
        Check.Equal(0, InkEngine.InkEngine.StepPenBandIndex(Array.Empty<Color4>(), 0, red, +1),
            file: "RadialBandStep");
    }

    [UnitTest]
    internal static void DocColorInvert_PreservesHue_And_SoftensBlackWhite()
    {
        static (int B, int G, int R, int A) One(byte b, byte g, byte r, byte a = 255)
        {
            var buf = new byte[] { b, g, r, a };
            InkEngine.DocColorInvert.ApplyInPlace(buf);
            return (buf[0], buf[1], buf[2], buf[3]);
        }

        // 纯白 → 护眼底 #1A1C20（B=32, G=28, R=26）；alpha 原样。
        var white = One(255, 255, 255);
        Check.Equal(32, white.B, file: "白→底 B");
        Check.Equal(28, white.G, file: "白→底 G");
        Check.Equal(26, white.R, file: "白→底 R");
        Check.Equal(255, white.A, file: "alpha 不动");

        // 纯黑 → 护眼字 #E8EAED（B=237, G=234, R=232）。
        var black = One(0, 0, 0);
        Check.Equal(237, black.B, file: "黑→字 B");
        Check.Equal(234, black.G, file: "黑→字 G");
        Check.Equal(232, black.R, file: "黑→字 R");

        // 纯红 / 纯蓝：色相、饱和度不变（明度反转对它们 delta = 0），仍以本色为主。
        var red = One(0, 0, 255);
        Check.True(red.R > 200 && red.G < 40 && red.B < 40,
                   $"纯红必须还是红：({red.R},{red.G},{red.B})", file: "保色反色");
        var blue = One(255, 0, 0);
        Check.True(blue.B > 200 && blue.R < 40 && blue.G < 40,
                   $"纯蓝必须还是蓝：({blue.R},{blue.G},{blue.B})", file: "保色反色");

        // byteCount 之外一个字节都不许动（补丁缓冲是单槽复用的，尾巴上是上一块的数据）。
        var tail = new byte[8] { 255, 255, 255, 255, 7, 8, 9, 10 };
        InkEngine.DocColorInvert.ApplyInPlace(tail, 4);
        Check.Equal((byte)7, tail[4], file: "越界 4");
        Check.Equal((byte)8, tail[5], file: "越界 5");
        Check.Equal((byte)9, tail[6], file: "越界 6");
        Check.Equal((byte)10, tail[7], file: "越界 7");

        // 非 4 倍数 / null 不能抛（尾巴按半像素丢弃）。
        InkEngine.DocColorInvert.ApplyInPlace(new byte[3], 3);
        InkEngine.DocColorInvert.ApplyInPlace(null);
    }
}
