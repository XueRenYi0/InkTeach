// 板书历史自检（2026-10-14 加）：`--historytest`。
// 配套实现：src/InkEngine/BoardHistory.cs（留什么、留多少、怎么轮转）。
// **只谈事实**：文件真的写出来了、图片真的没进去、轮转真的按总量删、载入真的走"打开墨迹"那条路。

using System.Globalization;
using System.Numerics;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 板书历史自检（`--historytest`）：
    ///   ① 快照写出来了、**只含笔迹**（截图不进）、能读回；
    ///   ② 轮转：**总量超上限从最老的删**（份数不限）；
    ///   ③ 找回：从历史目录"打开墨迹"→ 板书被替换 + **当前板书先被备份**；
    ///   ④ 附带验："清除全部墨迹"在板书上可撤销。
    /// 全程走临时目录（<see cref="BoardHistory.DirOverride"/>），不碰用户的历史目录。
    /// </summary>
    private void BoardHistoryTest()
    {
        Console.WriteLine();
        Console.WriteLine("=== 板书历史自检（快照 / 轮转 / 找回）===");

        // 独立的临时目录（App 的自检 override 也在临时目录，这里再嵌一层，互不干扰）。
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inkteach-historytest");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        BoardHistory.DirOverride = dir;
        BoardHistory.CloseSession();

        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-44} {detail}");
        }

        // ---------- ① 快照：只含笔迹（截图不进） ----------
        Doc.Clear();
        Doc.ResetToSinglePage();
        Doc.SwitchPage(InkDocument.BoardPageKey);   // 内容写在白板槽（历史只认它，2026-10-16）
        var pen = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(1f, 0f, 0f, 1f), Width = 6f,
        };
        pen.AddPoint(300f, 300f, 1f, 0);
        pen.AddPoint(500f, 360f, 1f, 8);
        Doc.AddStroke(pen);
        var pen2 = new Stroke
        {
            Tool = Tool.Pen, Kind = StrokeKind.Freehand,
            Color = new Color4(0f, 0f, 1f, 1f), Width = 6f,
        };
        pen2.AddPoint(600f, 500f, 1f, 0);
        pen2.AddPoint(700f, 560f, 1f, 8);
        Doc.AddStroke(pen2);
        Doc.AddImage(MakeTestImage(64, 48), 900f, 700f, 1f);      // 截图：**不该进快照**
        SettleFrames(120);

        Check("（准备）板上 3 个对象（2 笔 + 1 张图）", Doc.Strokes.Count == 3, $"{Doc.Strokes.Count} 个");

        bool wrote = SnapshotBoardHistoryForTest();
        Check("写快照：成功（文件落盘）", wrote && BoardHistory.Count == 1,
              $"写了={wrote}，目录里 {BoardHistory.Count} 份");

        var files = BoardHistory.List();
        string snap = files.Count > 0 ? files[0].Path : null;
        int loaded = -1;
        if (snap != null)
        {
            try
            {
                var strokes = InkSerializer.LoadStrokes(System.IO.File.ReadAllBytes(snap), out _);
                loaded = strokes.Count;
            }
            catch (Exception ex) { Console.WriteLine("    读回失败：" + ex.Message); }
        }
        Check("快照内容：**只有 2 笔**（截图那条被丢掉）", loaded == 2, $"读回 {loaded} 条");
        // 64×48 的截图 = 12KB 像素：阈值取 8KB，图片要是漏进去了这一条会红。
        Check("快照体积：足够小（图片像素没进去）",
              snap != null && new System.IO.FileInfo(snap).Length < 8 * 1024,
              snap == null ? "没有文件" : $"{new System.IO.FileInfo(snap).Length} 字节");

        // ---- ①.5 桌面批注不进快照（2026-10-16 用户定：桌面不保存、不进历史）----
        {
            Doc.SwitchPage(0);                       // 去桌面写一笔
            var desk = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0.6f, 0f, 1f), Width = 6f,
            };
            desk.AddPoint(200f, 800f, 1f, 0); desk.AddPoint(300f, 830f, 1f, 8);
            Doc.AddStroke(desk);
            bool wrote2 = SnapshotBoardHistoryForTest();          // 同一会话文件覆盖写
            int loaded2 = -1;
            try
            {
                if (snap != null)
                    loaded2 = InkSerializer.LoadStrokes(
                        System.IO.File.ReadAllBytes(snap), out _).Count;
            }
            catch { }
            Check("桌面批注**不进快照**（白板独立槽）", wrote2 && loaded2 == 2, $"读回 {loaded2} 条");
            Doc.SwitchPage(InkDocument.BoardPageKey);             // 回白板继续后面的用例
        }

        // ---------- ② 轮转：总量超上限从最老的删 ----------
        long keepMax = BoardHistory.MaxBytes;
        BoardHistory.MaxBytes = 300 * 1024;                      // 300 KB：拿小上限来验逻辑
        string rot = System.IO.Path.Combine(dir, "轮转测试");
        Directory.CreateDirectory(rot);
        for (int i = 0; i < 5; i++)
        {
            string f = System.IO.Path.Combine(rot, $"假-{i}.inkb");
            System.IO.File.WriteAllBytes(f, new byte[100 * 1024]);        // 每份 100 KB
            // 时间**显式错开**：Enforce 是按最后写盘时间删最老的（同一秒内的顺序不可靠）。
            System.IO.File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-10 + i));
        }
        // Enforce 只扫历史根目录，不扫子目录——把"轮转测试"里的文件挪到根目录来跑。
        string[] moved = new string[5];
        for (int i = 0; i < 5; i++)
        {
            moved[i] = System.IO.Path.Combine(dir, $"假-{i}.inkb");
            System.IO.File.Move(System.IO.Path.Combine(rot, $"假-{i}.inkb"), moved[i]);
            System.IO.File.SetLastWriteTimeUtc(moved[i], DateTime.UtcNow.AddMinutes(-10 + i));
        }
        BoardHistory.Enforce();
        int left = Directory.GetFiles(dir, "*.inkb").Length;
        bool oldestGone = !System.IO.File.Exists(moved[0]) && !System.IO.File.Exists(moved[1]);
        Check("轮转：总量超上限 → 从最老的删（5×100KB → ≤3 份）", left <= 3 && oldestGone,
              $"剩 {left} 份；最老两份还在？{!oldestGone}");
        BoardHistory.MaxBytes = keepMax;
        // 清掉假文件，别干扰后面的"找回"（真快照那一份要留着）。
        for (int i = 0; i < 5; i++) { try { System.IO.File.Delete(moved[i]); } catch { } }
        try { Directory.Delete(rot, true); } catch { }

        // ---------- ③ 找回：走"打开墨迹"那条路（先备份当前板书） ----------
        int backupsBefore = InkFileStore.BackupCount();
        Doc.Clear();                                             // 当前板书先清空，好验"载入回来了"
        SettleFrames(120);
        Check("（准备）当前板书已清空", Doc.Strokes.Count == 0, $"{Doc.Strokes.Count} 个");

        bool opened = snap != null && OpenInkFileForTest(snap);
        SettleFrames(150);
        Check("找回：从历史载入 → 板书回来了（2 笔，且没有截图）",
              opened && Doc.Strokes.Count == 2, $"载入={opened}，{Doc.Strokes.Count} 个");
        Check("找回：载入前自动备份了当前（空）板书？→ 备份份数不减少",
              InkFileStore.BackupCount() >= backupsBefore,
              $"备份 {backupsBefore} → {InkFileStore.BackupCount()}");

        // ---------- ④ 顺带：清除全部墨迹在板书上可撤销 ----------
        int before = Doc.Strokes.Count;
        ClearAllInkFromUi();
        SettleFrames(120);
        bool cleared = Doc.Strokes.Count == 0;
        UndoFromUi();
        SettleFrames(150);
        Check("清除全部墨迹：清了能一步撤销回来（板书）",
              cleared && Doc.Strokes.Count == before,
              $"清空后 {0} → 撤销后 {Doc.Strokes.Count}（原 {before}）");

        // ---------- ⑤ 白板独立槽（2026-10-16 用户定）：切槽 / 留存 / 清空按槽 ----------
        {
            // 起手归零：清当前槽（③④ 之后人站在白板上），回桌面。
            ClearAllInkFromUi();
            SettleFrames(120);
            if (BoardOn) SetBoardFromUi(false);
            SettleFrames(120);
            Doc.Clear();

            var d1 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0f, 0f, 1f), Width = 5f,
            };
            d1.AddPoint(400f, 400f, 1f, 0); d1.AddPoint(460f, 420f, 1f, 6);
            Doc.AddStroke(d1);
            int deskBase = Doc.Strokes.Count;                       // 桌面 1 笔

            SetBoardFromUi(true);
            SettleFrames(120);
            Check("独立槽：开白板 = 切到白板槽（桌面那笔不跟过来）",
                  BoardOn && Doc.PageKey == InkDocument.BoardPageKey && Doc.Strokes.Count == 0,
                  $"页键 {Doc.PageKey}，{Doc.Strokes.Count} 条");

            var b1 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0f, 1f, 1f), Width = 5f,
            };
            b1.AddPoint(500f, 500f, 1f, 0); b1.AddPoint(560f, 520f, 1f, 6);
            Doc.AddStroke(b1);

            SetBoardFromUi(false);
            SettleFrames(120);
            Check("独立槽：关白板 = 回桌面，桌面那笔还在",
                  !BoardOn && Doc.PageKey == 0 && Doc.Strokes.Count == deskBase,
                  $"页键 {Doc.PageKey}，{Doc.Strokes.Count} 条");

            SetBoardFromUi(true);
            SettleFrames(120);
            Check("独立槽：再进白板，白板那笔还在",
                  Doc.Strokes.Count == 1 && Doc.Strokes[0] == b1, $"{Doc.Strokes.Count} 条");

            // 橡皮「清空墨迹」= 只清当前这一屏（2026-10-25 用户定），下一屏必须留着
            ViewOffsetY = 0f;
            SettleFrames(100);
            var b2 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0.4f, 0f, 1f), Width = 5f,
            };
            b2.AddPoint(500f, _virtualY + PageHeightCanvas + 300f, 1f, 0);
            b2.AddPoint(560f, _virtualY + PageHeightCanvas + 320f, 1f, 6);
            Doc.AddStroke(b2);
            SettleFrames(100);
            ClearCurrentPageFromUi();
            SettleFrames(120);
            Check("清空墨迹：白板只清当前屏，下一屏还在",
                  Doc.Strokes.Count == 1 && Doc.Strokes.Contains(b2),
                  $"{Doc.Strokes.Count} 条（下一屏那笔应在）");
            UndoFromUi();
            SettleFrames(120);
            Check("清空墨迹：撤销把当前屏那笔找回来",
                  Doc.Strokes.Count == 2 && Doc.Strokes.Contains(b1) && Doc.Strokes.Contains(b2),
                  $"{Doc.Strokes.Count} 条");
            // 把测试用的第二屏那笔收掉，后面的“按槽清空”仍按 1 笔算
            Doc.ClearRect(new RectF
            {
                MinX = _virtualX, MinY = _virtualY + PageHeightCanvas,
                MaxX = _virtualX + _virtualW, MaxY = _virtualY + PageHeightCanvas * 2f,
            });
            SettleFrames(100);

            // 清空按槽：在桌面清空，白板不受影响
            SetBoardFromUi(false);
            SettleFrames(120);
            Doc.Clear();
            SetBoardFromUi(true);
            SettleFrames(120);
            Check("独立槽：在桌面清空，白板不受影响", Doc.Strokes.Count == 1, $"{Doc.Strokes.Count} 条");
            Doc.Clear();
            SetBoardFromUi(false);
            SettleFrames(120);
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? $"  PASS：板书历史正确（快照不含图 / 轮转 / 找回 / 清除可撤销 / 桌面不进快照）——共 {pass} 项"
            : $"  FAIL：{fail} 项不对（{pass} 项通过）");

        // 收尾：临时目录与记忆都放掉
        BoardHistory.CloseSession();
        BoardHistory.DirOverride = null;
        try { Directory.Delete(dir, true); } catch { }
        Doc.Clear();
        SettleFrames(80);
        _quit = true;
        if (fail > 0) ExitCode = 1;
    }
}
