// [职责] 文档「上次看到第 N 页」恢复提示 E2E（挂在 --doctest 末尾的 F 组）。
// [测试] --doctest

using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// F 组：阅读位置落盘 / 跨会话提示 / 继续 / 从头 / 落笔关闭 / 同次运行重开不弹 /
    /// 翻页与缩放落盘 / 清墨不删阅读位置。
    ///
    /// 每个子用例用**不同的 storeKey**：文档页空间按 key 分配槽位（`_pageScroll`），
    /// 同 key 重开会恢复上次相机（这是产品里"同次运行重开"的既有行为），
    /// 换个 key 才能拿到"新会话第一页"的干净起点。key 必须是**十六进制**
    /// （产品里的键就是 FNV 的 8 位十六进制；非十六进制会被 SlotOf 归到同一个槽）。
    /// </summary>
    private void DocResumeTestGroup(Action<string, bool, string> check)
    {
        Console.WriteLine("  -- F 文档恢复提示：上次看到第 N 页 · 继续 / 从头 --");
        bool oldWorker = DocView.UseWorker;
        bool oldPageWorker = DocPageWorker;
        DocPageWorker = false;
        DocView.UseWorker = false;

        if (DocView.IsOpen) CloseDocument();
        if (BoardOn) SetBoardFromUi(false);
        if (Doc.PageKey != 0) Doc.SwitchPage(0);
        Doc.Clear();
        Doc.ClearHistory();

        var specs = new List<DocPages.Spec>();
        for (int i = 0; i < 5; i++) specs.Add(DocSpec(1400, 2000, $"resume-{i}.png", i));

        DocResumeGroupBasics(check, specs);
        DocResumeGroupPersistence(check, specs);

        if (DocView.IsOpen) CloseDocument();
        DocPageWorker = oldPageWorker;
        DocView.UseWorker = oldWorker;
        ResetResumeWindow();
    }

    /// <summary>F1–F3：跨会话打开不自动跳 / 继续 / 从头。</summary>
    private void DocResumeGroupBasics(Action<string, bool, string> check, List<DocPages.Spec> specs)
    {
        {
            const string key = "a1b2c3d1";
            SeedResume(key, 3, 0.25f, 1.25f);
            OpenResumeDoc(specs, key);
            check("恢复卡：跨会话打开 → 卡片可见", DocResumeVisible, $"可见={DocResumeVisible}");
            check("恢复卡：不自动跳（第 1 页 / 100% / 页顶）",
                  BarPageNow == 1 && MathF.Abs(DocZoom - 1f) < 0.001f && MathF.Abs(ViewOffsetY) < 2f,
                  $"页={BarPageNow} 缩放={DocZoom:F2} camY={ViewOffsetY:F0}");
            check("恢复卡：文案含页码与缩放",
                  DocResumePageText().Contains("第 3 页") && DocResumePageText().Contains("125%"),
                  DocResumePageText());
            CloseDocument();
            DocResumeStore.Delete(key);
        }

        {
            const string key = "a1b2c3d2";
            SeedResume(key, 3, 0.25f, 1.25f);
            OpenResumeDoc(specs, key);
            float wantY = _virtualY - (DocView.TopOf(2) + 0.25f * 2000f) * 1.25f;
            DocResumeContinue();
            SettleFrames(80);
            check("恢复卡：继续 → 卡片收起", !DocResumeVisible, $"可见={DocResumeVisible}");
            check("恢复卡：继续 → 第 3 页 + 125%",
                  BarPageNow == 3 && MathF.Abs(DocZoom - 1.25f) < 0.001f,
                  $"页={BarPageNow} 缩放={DocZoom:F2}");
            check("恢复卡：继续 → 页内比例对上（0.25）",
                  MathF.Abs(ViewOffsetY - wantY) < 3f,
                  $"camY={ViewOffsetY:F0}（应 {wantY:F0}）");
            CloseDocument();
            DocResumeStore.Delete(key);
        }

        {
            const string key = "a1b2c3d3";
            SeedResume(key, 3, 0.25f, 1.5f);
            OpenResumeDoc(specs, key);
            DocResumeStartOver();
            SettleFrames(60);
            check("恢复卡：从头 → 卡片收起 + 第 1 页 / 100%",
                  !DocResumeVisible && BarPageNow == 1 && MathF.Abs(DocZoom - 1f) < 0.001f,
                  $"可见={DocResumeVisible} 页={BarPageNow} 缩放={DocZoom:F2} camY={ViewOffsetY:F0}");
            CloseDocument();
            DocResumeStore.Delete(key);
        }
    }

    /// <summary>F4–F7：落笔关闭 / 同次重开不弹 / 翻页缩放落盘 / 清墨不删阅读位置。</summary>
    private void DocResumeGroupPersistence(Action<string, bool, string> check, List<DocPages.Spec> specs)
    {
        {
            const string key = "a1b2c3d4";
            SeedResume(key, 4, 0.1f, 1f);
            OpenResumeDoc(specs, key);
            int before = Doc.Strokes.Count;
            NoteDocResumeContentStartForTest();
            var s = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                                 Color = new Color4(0f, 0f, 0f, 1f), Width = 6f };
            s.AddPoint(200f, 300f, 1f, 0);
            s.AddPoint(360f, 380f, 1f, 0);
            Doc.AddStroke(s);
            check("恢复卡：落笔 → 卡片收起", !DocResumeVisible, $"可见={DocResumeVisible}");
            check("恢复卡：落笔 → 这一笔照常写进文档", Doc.Strokes.Count == before + 1,
                  $"笔画 {before} → {Doc.Strokes.Count}");
            CloseDocument();
            DocResumeStore.Delete(key);
        }

        {
            const string key = "a1b2c3d5";
            SeedResume(key, 5, 0.3f, 1.25f);
            OpenResumeDoc(specs, key);
            bool first = DocResumeVisible;
            CloseDocument();                    // 关闭时写入"现在"
            OpenResumeDoc(specs, key);
            check("恢复卡：同一次运行内重开不弹", first && !DocResumeVisible,
                  $"第一次={first} 重开={DocResumeVisible}");
            CloseDocument();
            DocResumeStore.Delete(key);
        }

        {
            const string key = "a1b2c3d6";
            OpenResumeDoc(specs, key);
            BarGoto(4);
            SettleFrames((int)DocMemory.FlipMs + 80);
            SetDocZoomFromUi(1.5f);
            SettleFrames(80);
            CloseDocument();
            var saved = DocResumeStore.Load(key);
            check("恢复卡：关闭时落盘最新页 / 缩放",
                  saved != null && saved.Page == 4 && MathF.Abs(saved.Zoom - 1.5f) < 0.01f,
                  saved == null ? "没读到文件" : $"页={saved.Page} 缩放={saved.Zoom:F2}");
            check("恢复卡：时间戳是「现在」（不是跨会话旧值）",
                  saved != null && saved.SavedAtUnix >= DocResumeStore.ProcessStartUnix,
                  saved == null ? "没读到文件" : $"{saved.SavedAtUnix} vs {DocResumeStore.ProcessStartUnix}");

            OpenResumeDoc(specs, key);
            var ink = new Stroke { Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                                   Color = new Color4(0f, 0f, 0f, 1f), Width = 8f };
            ink.AddPoint(300f, 400f, 1f, 0);
            ink.AddPoint(480f, 520f, 1f, 0);
            Doc.AddStroke(ink);
            SaveDocInk(force: true);
            bool inkOnDisk = DocStore.Load(key) != null;
            ClearDocMarks();
            check("恢复卡：清墨前盘上有批注", inkOnDisk, "0.inkb 存在");
            check("恢复卡：清空墨迹不删阅读位置",
                  DocStore.Load(key) == null && DocResumeStore.Exists(key),
                  $"ink={(DocStore.Load(key) != null ? "还在" : "已删")} resume={DocResumeStore.Exists(key)}");
            CloseDocument();
            DocResumeStore.Delete(key);
            DocStore.Delete(key);
        }
    }

    /// <summary>种一条"上次会话"的阅读位置（时刻固定为 100 = 一定早于本进程）。</summary>
    private static void SeedResume(string key, int page, float fy, float zoom)
    {
        DocResumeStore.Delete(key);
        DocResumeStore.Save(key, new DocResumePosition
        {
            Page = page, FractionY = fy, FractionX = 0f, Zoom = zoom, SavedAtUnix = 100,
        });
    }

    /// <summary>走真实打开流程；页位图换成纯色假源（测试只关心相机与卡片）。</summary>
    private void OpenResumeDoc(List<DocPages.Spec> specs, string key)
    {
        ResetResumeWindow();
        StartDocViewForTest(specs, "恢复测试.pdf", key);
        DocView.Generator = sp => FakeDocPageBgra(sp.OutW, sp.OutH, 236, 240, 246);
        SettleFrames(80);
    }

    /// <summary>相机回到零点（每个子用例从"页顶"起，判据才确定）。</summary>
    private void ResetResumeWindow()
    {
        ViewOffsetY = 0f;
        foreach (var w in _windows) { w.ViewOffsetX = 0f; w.ViewOffsetY = 0f; }
    }
}
