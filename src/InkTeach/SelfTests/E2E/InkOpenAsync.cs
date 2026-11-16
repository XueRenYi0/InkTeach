// [职责] 「异步打开墨迹 / 打开提示」E2E：大 .inkb 不卡界面、提示可见、按代作废、失败不动板书。
// [测试] --doctest

// 为什么单开一组（2026-11-16）：
//   「打开墨迹」改成后台读盘 + 解析之后，要验的**不是**"能不能打开"（`--inkfiletest`
//   已经用同步路验透了），而是四件只有异步才会发生的事：
//     ① 发起立刻返回、屏幕上先挂"正在打开…"（大文件不再假死）；
//     ② 结果没落地之前，板书一个字节不动；载入期间写的那几笔**要进打开前备份**（不丢）；
//     ③ 连开两份 → 第一份作废（按代作废，不是叠加）；
//     ④ 缺失 / 坏文件：状态行说明、当前板书原样。
//   判据全部是"能数出来的东西"（帧数 / 对象数 / 备份条数 / toast 文案），不靠观感。

using Vortice.Mathematics;
using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// ===== H 组：异步「打开墨迹」+「正在打开…」提示 =====
    /// 调用方传 `DocumentTest` 里那个 `Check(name, ok, detail)` 本地函数。
    /// 本方法只管"临时目录 + 干净起点 + 收尾"，判据分组见下面三个 Part。
    /// </summary>
    private void InkOpenAsyncTestGroup(Action<string, bool, string> Check)
    {
        Console.WriteLine("  -- H 异步打开墨迹：大文件不卡 / 提示可见 / 按代作废 / 失败不动板书 --");

        string temp = Path.Combine(Path.GetTempPath(), "inkteach-doctest-inkopen");
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        catch { /* 有意忽略：清理失败不影响主流程（ADR-005 D5） */ }
        Directory.CreateDirectory(temp);
        string backupDir = Path.Combine(temp, "backup");
        string prevBackup = InkFileStore.BackupDirOverride;
        InkFileStore.BackupDirOverride = backupDir;

        try
        {
            // 干净起点：白板槽 + 当前就在白板（别掺前面几组的残留）
            CloseDocument();
            if (PassThrough) SetPassThroughFromUi(false);
            Doc.SwitchPage(InkDocument.BoardPageKey);
            Doc.Clear();
            Doc.ClearHistory();
            var marker = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 0f, 1f), Width = 4f,
            };
            marker.AddPoint(200f, 200f, 1f, 0);
            marker.AddPoint(260f, 240f, 1f, 20);
            Doc.AddStroke(marker);
            Doc.InvalidateAll();

            const int bigCount = 1600;                 // 够大：读盘 + 解析是能被感知的一段活
            const int smallCount = 300;
            string bigA = Path.Combine(temp, "大板书-A.inkb");
            string bigB = Path.Combine(temp, "大板书-B.inkb");
            WriteBigInkForTest(bigA, bigCount, 11);
            WriteBigInkForTest(bigB, smallCount, 22);

            InkOpenPartStartAndLanding(Check, bigA, bigCount, marker, backupDir);
            InkOpenPartCancelAndFailures(Check, bigA, bigB, smallCount, temp);
            InkOpenPartSyncAndHint(Check, bigB, smallCount, temp);
        }
        finally
        {
            Doc.SwitchPage(InkDocument.BoardPageKey);
            Doc.Clear();
            Doc.ClearHistory();
            InkFileStore.BackupDirOverride = prevBackup;
            try { Directory.Delete(temp, true); }
            catch { /* 有意忽略：清理失败不影响主流程（ADR-005 D5） */ }
        }
    }

    /// <summary>H1 / H2：发起立刻返回 + 提示先挂上；载入期间继续出帧、还能写、那几笔进备份。</summary>
    private void InkOpenPartStartAndLanding(Action<string, bool, string> Check,
                                            string bigA, int bigCount, Stroke marker, string backupDir)
    {
        long frames0 = FrameCounter;
        bool started = StartInkOpenForTest(bigA);
        bool busyNow = InkOpenBusy;
        bool hintNow = ToastText.Contains("正在打开") && ToastText.Contains("大板书-A.inkb");
        Check("H1 发起：立刻返回、提示已挂上", started && busyNow && hintNow,
              $"busy={busyNow} toast=「{ToastText}」");
        Check("H1 发起：结果没落地之前板书原样",
              Doc.Strokes.Count == 1 && Doc.Strokes[0] == marker, $"{Doc.Strokes.Count} 条");

        var during = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0.6f, 0f, 1f), Width = 5f,
        };
        during.AddPoint(400f, 400f, 1f, 0);
        during.AddPoint(460f, 430f, 1f, 20);
        Doc.AddStroke(during);                      // 结果还没被采纳 → 这一笔落在**旧**板书上
        Check("H2 载入期间还能写（界面没被卡住）", Doc.Strokes.Count == 2, $"{Doc.Strokes.Count} 条");

        int pumps = 0;
        double t0 = _clock.Elapsed.TotalMilliseconds;
        while (InkOpenBusy && _clock.Elapsed.TotalMilliseconds - t0 < 20000)
        {
            PumpInkOpenForTest();
            pumps++;
        }
        bool done = !InkOpenBusy;
        Check("H2 载入期间持续出帧（等到结果也不超时）",
              done && pumps > 0 && FrameCounter > frames0, $"拍数={pumps}，帧 {frames0}→{FrameCounter}");
        Check("H2 落地后：白板槽 = 文件里的对象数、当前在白板",
              Doc.Strokes.Count == bigCount && Doc.PageKey == InkDocument.BoardPageKey && BoardOn,
              $"页键={Doc.PageKey} 板={BoardOn} {Doc.Strokes.Count} 条（应 {bigCount}）");
        Check("H2 落地后：提示收掉、状态行说明白",
              !ToastVisible && InkStatus.Contains("已打开") && InkStatus.Contains("大板书-A.inkb"),
              $"toast=「{ToastText}」状态={InkStatus}");

        int backupStrokes = -1;
        var backups = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, "*.inkb") : Array.Empty<string>();
        if (backups.Length > 0)
        {
            var probe = new InkDocument();
            try { InkSerializer.LoadInto(probe, File.ReadAllBytes(backups[0])); backupStrokes = probe.Strokes.Count; }
            catch { /* 有意忽略：备份解不开就是判据失败，不让它炸测试（ADR-005 D5） */ }
        }
        Check("H2 打开前备份 = 载入期间的最新板书（2 条，新写的没丢）",
              backups.Length == 1 && backupStrokes == 2,
              $"{backups.Length} 份，最后一袋 {backupStrokes} 条");
    }

    /// <summary>H3 / H4：连开两份第一份作废；缺失 / 坏文件只写状态、不动板书。</summary>
    private void InkOpenPartCancelAndFailures(Action<string, bool, string> Check,
                                              string bigA, string bigB, int smallCount, string temp)
    {
        bool s1 = StartInkOpenForTest(bigA);
        bool s2 = StartInkOpenForTest(bigB);
        string toastB = ToastText;
        bool hintB = toastB.Contains("大板书-B.inkb");
        bool waited = WaitInkOpenForTest();
        Check("H3 第二份发起：提示换成第二份", s1 && s2 && hintB, $"toast=「{toastB}」");
        Check("H3 最终是第二份（第一份被作废）",
              waited && Doc.Strokes.Count == smallCount && InkStatus.Contains("大板书-B.inkb"),
              $"{Doc.Strokes.Count} 条（应 {smallCount}）状态={InkStatus}");

        int beforeBad = Doc.Strokes.Count;
        bool startedMissing = StartInkOpenForTest(Path.Combine(temp, "没有这份.inkb"));
        Check("H4 缺失文件：不发起任务、状态行说明、板书不动",
              !startedMissing && !InkOpenBusy && InkStatus.Contains("打不开")
              && Doc.Strokes.Count == beforeBad, $"状态={InkStatus}");

        string bad = Path.Combine(temp, "坏板书.inkb");
        File.WriteAllText(bad, "这不是墨迹");
        bool startedBad = StartInkOpenForTest(bad);
        bool waitedBad = WaitInkOpenForTest();
        Check("H4 坏文件：后台报错、板书一个字节不动",
              startedBad && waitedBad && InkStatus.Contains("打不开")
              && Doc.Strokes.Count == beforeBad, $"状态={InkStatus}，{Doc.Strokes.Count} 条");
    }

    /// <summary>H5 / H6：同步老路不变；「正在打开…」提示在阻塞调用前真的上屏、结束收掉。</summary>
    private void InkOpenPartSyncAndHint(Action<string, bool, string> Check,
                                        string bigB, int smallCount, string temp)
    {
        bool sync = OpenInkFileForTest(bigB);
        Check("H5 同步老路（OpenInkFileForTest）行为不变：当场落地",
              sync && Doc.Strokes.Count == smallCount, $"{Doc.Strokes.Count} 条，状态={InkStatus}");

        long f0 = FrameCounter;
        ShowBusyHintForTest("测试课件.pdf");
        bool hinted = ToastText.Contains("正在打开") && ToastText.Contains("测试课件.pdf");
        Check("H6 打开前的提示：文案挂上 + 强制推了一帧",
              hinted && FrameCounter > f0, $"toast=「{ToastText}」帧 {f0}→{FrameCounter}");
        ClearToast();
        Check("H6 打开结束：提示收掉（不残留 30 秒）", !ToastVisible, $"toast=「{ToastText}」");

        string ghostPdf = Path.Combine(temp, "没有这份.pdf");
        string errGhost = OpenDocuments(new[] { ghostPdf });
        Check("H6 PDF 打不开：提示换成失败原因（2 秒后自消）",
              errGhost != null && ToastText.Contains("打开失败"), $"toast=「{ToastText}」");
        ClearToast();
    }

    /// <summary>
    /// 造一份"大" .inkb：<paramref name="count"/> 条自由笔迹 × 60 个点。
    /// 直接走序列化器（不经过界面），所以文件本身就是产品能读的那一种。
    /// </summary>
    private static void WriteBigInkForTest(string path, int count, int seed)
    {
        var rnd = new Random(seed);
        var strokes = new List<Stroke>(count);
        for (int i = 0; i < count; i++)
        {
            var s = new Stroke
            {
                Id = i + 1,
                Tool = Tool.Pen,
                Kind = StrokeKind.Freehand,
                Color = new Color4(0.1f, 0.2f, 0.8f, 1f),
                Width = 3f,
            };
            float x = 100f + (float)rnd.NextDouble() * 800f;
            float y = 100f + (float)rnd.NextDouble() * 600f;
            for (int j = 0; j < 60; j++)
            {
                x += (float)rnd.NextDouble() * 6f - 3f;
                y += (float)rnd.NextDouble() * 6f - 3f;
                s.AddPoint(x, y, 0.5f, j * 2);
            }
            strokes.Add(s);
        }
        File.WriteAllBytes(path, InkSerializer.SaveStrokes(strokes));
    }
}
