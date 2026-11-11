using System.IO;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检：字体快捷档 + 系统字体下拉（2026-11-11）。
    /// 每档都**离屏渲染**成 PNG（不用截屏，锁屏也能跑）；再验证下拉能选、能撤销、
    /// 会把“上次用的字体”记进设置。
    ///   --facesprobe
    /// </summary>
    private void FacesProbe()
    {
        Console.WriteLine();
        Console.WriteLine("=== 字体档探针（离屏渲染） ===");
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(160);
        PassThrough = false;
        BoardOn = true;
        ShowHud = false;

        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;
        var img = InsertPaperImageForTest(MakePaperImageForTest(320, 220));
        var card = InsertImageCardForTest(img, new List<AiQuestionClient.CardItem>
        {
            new()
            {
                Stem = "永和九年，岁在癸丑。已知 $x^2=4$，求 $x$ 的值，并说明理由。",
                Options = { "A. $x=2$", "B. $x=\\pm 2$" },
                NeedReview = false,
            },
        });
        SettleFrames(200);
        var tb = card?.TextBox;
        if (tb == null) { Console.WriteLine("题卡没建出来"); ExitCode = 1; return; }

        string dir = Path.Combine("reports", "faces");
        Directory.CreateDirectory(dir);
        var sorted = new List<(TextBoxFace Face, string Path)>();
        foreach (var info in TextBoxFaces.Visible)
        {
            tb.Face = info.Face;
            tb.FaceName = "";
            tb.InvalidateLayout();
            card.SyncTextBoxBounds();
            SettleFrames(60);

            var box = card.PaddedBounds;
            int w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            int h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            var bgra = _windows.Count > 0
                ? _windows[0].RenderStrokesToBgra(new[] { card }, box, w, h)
                : null;
            if (bgra == null) { Console.WriteLine($"{info.Name}: 离屏渲染失败"); continue; }
            var png = ExportFormats.Encode(info.Name + ".png", bgra, w, h, 0, out _);
            string path = Path.Combine(dir, $"{info.Face}.png");
            File.WriteAllBytes(path, png);
            sorted.Add((info.Face, path));
            Console.WriteLine($"{info.Name,-4} face={info.Face,-8} {w}×{h} → {path}");
        }

        // ---- 系统字体下拉（2026-11-11）----------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 系统字体下拉 ===");
        var entries = FontListEntries;
        int cn = 0;
        foreach (string f in entries)
            if (f.Length > 0 && f[0] >= 0x2E80) cn++;
        Console.WriteLine($"列表 {entries.Length} 个（中文名 {cn} 个）");
        Console.WriteLine($"前 3 个：{string.Join(" / ", entries[..Math.Min(3, entries.Length)])}");

        string pick = null;
        foreach (string f in entries)
            if (f.Contains("楷体", StringComparison.Ordinal) || f == "KaiTi") { pick = f; break; }
        pick ??= entries.Length > 1 ? entries[1] : null;

        SelPanelOpen = SelPanel.FontSize;      // 下拉挂在字体面板上，先把面板打开
        SettleFrames(80);
        OpenFontList();
        SettleFrames(120);
        Console.WriteLine($"打开：open={FontListOpen} 行={FontListCapacityNow} " +
                          $"矩形非空={!FontListRectNow().IsEmpty}");
        var dbg = FontListRectNow();
        var trk = SelectionHandles.FontListScrollTrack(dbg, UiUnit);
        Console.WriteLine($"rect={dbg.MinX:F0},{dbg.MinY:F0}-{dbg.MaxX:F0},{dbg.MaxY:F0} " +
                          $"track={trk.MinX:F0},{trk.MinY:F0}-{trk.MaxX:F0},{trk.MaxY:F0} " +
                          $"maxScroll={FontListMaxScroll:F0}");
        ScreenProbe.SaveBmp("reports/faces/fontlist.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);

        // 连续滚动 + 触摸惯性（2026-11-11）
        var lr = FontListRectNow();
        float cx = (lr.MinX + lr.MaxX) * 0.5f, cy = lr.MinY + 60f * UiUnit;
        FontListScrollPx = 0f;
        FontListPointerDown(cx, cy, 7);
        FontListPointerMove(cx, cy - 40f, 7);
        FontListPointerMove(cx, cy - 90f, 7);
        FontListPointerUp(cx, cy - 90f, 7);
        Console.WriteLine($"拖动：scroll={FontListScrollPx:F0} 惯性={FontListFlingActiveNow}");
        SettleFrames(700);
        Console.WriteLine($"惯性后：scroll={FontListScrollPx:F0} 惯性={FontListFlingActiveNow} " +
                          $"上限={FontListMaxScroll:F0}");

        if (pick != null)
        {
            int idx = -1;
            for (int i = 0; i < entries.Length; i++) if (entries[i] == pick) { idx = i; break; }
            FontListScrollPx = Math.Clamp((idx - 2) * FontListRowHeightNow, 0f, FontListMaxScroll);
            int row = idx >= 0 ? idx : 2;
            var faceBefore = tb.Face;
            string familyBefore = tb.FaceName;
            ApplyFontListRow(row);
            SettleFrames(150);
            Console.WriteLine($"选「{pick}」→ face={tb.Face} family={tb.FaceName} " +
                              $"open={FontListOpen} pref={GetUiPref("textFaceName")}");
            Console.WriteLine($"新对象默认字体：face={DefaultTextFace} " +
                              $"family='{DefaultTextFaceName}'");
            Doc.Undo();
            SettleFrames(120);
            Console.WriteLine($"撤销 → face={tb.Face}（原 {faceBefore}）" +
                              $"family='{tb.FaceName}'（原 '{familyBefore}'）");

            // 选系统里的“黑体”应落回枚举档（不是自定义），快捷格要能高亮。
            entries = FontListEntries;          // 最近使用可能已重排，重新取
            int hi = -1;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i] == "黑体" || entries[i] == "SimHei") { hi = i; break; }
            if (hi >= 0)
            {
                FontListScrollPx = Math.Clamp((hi - 2) * FontListRowHeightNow, 0f, FontListMaxScroll);
                ApplyFontListRow(hi);
                SettleFrames(150);
                Console.WriteLine($"选「黑体」→ face={tb.Face} family='{tb.FaceName}' " +
                                  $"（应为 SimHei / 空）");
            }
        }

        // 再打开一次：此时“最近使用”里已有两条，出一张带最近区的图。
        SetSelectedTextFontFamilyFromUi("华文楷体");
        SettleFrames(90);
        SetSelectedTextFontFamilyFromUi("黑体");
        SettleFrames(90);
        OpenFontList();
        SettleFrames(140);
        ScreenProbe.SaveBmp("reports/faces/fontlist-recent.bmp",
            (int)_virtualX, (int)_virtualY, (int)_virtualW, (int)_virtualH);
        Console.WriteLine($"最近使用 {FontListRecentCount} 个；列表共 {FontListEntries.Length} 个");
        CloseFontList();

        // 拼成一张竖排对比图
        int totalW = 0, totalH = 0;
        foreach (var (_, p) in sorted)
        {
            var one = ImageFile.Load(p);
            if (!one.Success) continue;
            totalW = Math.Max(totalW, one.Width);
            totalH += one.Height + 8;
        }
        if (totalW > 0 && totalH > 0)
        {
            var px = new byte[totalW * totalH * 4];
            for (int i = 0; i < px.Length; i += 4)
            { px[i] = 245; px[i + 1] = 246; px[i + 2] = 248; px[i + 3] = 255; }
            int y0 = 0;
            foreach (var (_, p) in sorted)
            {
                var one = ImageFile.Load(p);
                if (!one.Success) continue;
                for (int y = 0; y < one.Height; y++)
                {
                    int src = y * one.Width * 4;
                    int dst = ((y0 + y) * totalW) * 4;
                    Buffer.BlockCopy(one.Bgra, src, px, dst, one.Width * 4);
                }
                y0 += one.Height + 8;
            }
            var all = ExportFormats.Encode("faces.png", px, totalW, totalH, 0, out _);
            File.WriteAllBytes(Path.Combine(dir, "faces.png"), all);
            Console.WriteLine($"出图：{Path.Combine(dir, "faces.png")}（{totalW}×{totalH}）");
        }
    }
}
