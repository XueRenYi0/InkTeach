using System.IO.Compression;
using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// 自检 `--backuptest`：全局备份包**打包 → 改数据 → 恢复 → 坏包拒绝**整条链。
    ///
    /// 两个根都用临时目录（<see cref="AppBackup.RoamingRootOverride"/> /
    /// <see cref="AppBackup.LocalRootOverride"/>），**绝不动用户真实数据**。
    /// </summary>
    private void BackupTest()
    {
        Console.WriteLine("===== 全局备份 / 恢复（收藏 / 历史板书 / 设置 / 课件批注）=====");
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"  {(ok ? "通过" : "失败")}  {name,-40} {detail}");
        }

        string root = Path.Combine(Path.GetTempPath(), "inkteach-backup-test");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        string roaming = Path.Combine(root, "roaming");
        string local = Path.Combine(root, "local");
        Directory.CreateDirectory(Path.Combine(roaming, "Library"));
        Directory.CreateDirectory(Path.Combine(local, "History"));
        Directory.CreateDirectory(Path.Combine(local, "Ppt", "demo"));
        Directory.CreateDirectory(Path.Combine(local, "Doc", "abc"));

        string libA = Path.Combine(roaming, "Library", "a.ink");
        string libMeta = Path.Combine(roaming, "Library", "a.meta");
        string settings = Path.Combine(roaming, "settings.json");
        string names = Path.Combine(roaming, "Names.txt");
        string history = Path.Combine(local, "History", "板书-20261025-120000.inkb");
        string ppt = Path.Combine(local, "Ppt", "demo", "00000001.inkb");
        string doc = Path.Combine(local, "Doc", "abc", "0.inkb");

        File.WriteAllText(libA, "ink-a");
        File.WriteAllText(libMeta, "meta-a");
        File.WriteAllText(settings, "k=1");
        File.WriteAllText(names, "张三\n李四");
        File.WriteAllText(history, "history");
        File.WriteAllText(ppt, "ppt-ink");
        File.WriteAllText(doc, "doc-ink");

        AppBackup.RoamingRootOverride = roaming;
        AppBackup.LocalRootOverride = local;

        string zip = Path.Combine(root, "backup.zip");
        var progress = new List<int>();
        bool ok = AppBackup.Create(zip, out int files, out long bytes, out int skipped, out string err,
                                   p => progress.Add(p));
        Check("备份成功，zip 真落盘", ok && File.Exists(zip) && bytes > 0,
              $"ok={ok}，文件 {files} 个，{bytes} 字节，跳过 {skipped}，错误 {err}");
        Check("备份进度从 0 走到 100 且不回退",
              progress.Count >= 2 && progress[0] == 0 && progress[^1] == 100
              && progress.Zip(progress.Skip(1), (a, b) => b >= a).All(x => x),
              string.Join("% → ", progress) + "%");

        var namesInZip = new List<string>();
        try
        {
            using var z = ZipFile.OpenRead(zip);
            foreach (var e in z.Entries) namesInZip.Add(e.FullName.Replace('\\', '/'));
        }
        catch (Exception ex) { Console.WriteLine("  读包失败：" + ex.Message); }

        Check("包里有清单（格式版本）", namesInZip.Contains(AppBackup.ManifestName),
              string.Join(", ", namesInZip.Take(4)) + (namesInZip.Count > 4 ? " …" : ""));
        Check("收藏 / 设置 / 名单 / 历史 / PPT 批注 / 文档批注都进包了",
              files >= 7
              && namesInZip.Contains("roaming/Library/a.ink")
              && namesInZip.Contains("roaming/settings.json")
              && namesInZip.Contains("roaming/Names.txt")
              && namesInZip.Contains("local/History/板书-20261025-120000.inkb")
              && namesInZip.Contains("local/Ppt/demo/00000001.inkb")
              && namesInZip.Contains("local/Doc/abc/0.inkb"),
              $"文件 {files} 个：" + string.Join(", ", namesInZip.Where(n => n != AppBackup.ManifestName)));
        Check("不含 PDF / PPT 原文件（只打墨迹批注目录）",
              !namesInZip.Exists(n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                                      || n.EndsWith(".ppt", StringComparison.OrdinalIgnoreCase)
                                      || n.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)),
              namesInZip.Exists(n => n.Contains("."))
                  ? "包内没有 pdf/ppt/pptx"
                  : "包内没有扩展名");

        // ---- 改一遍现有数据，再恢复：要能整个换回来 ----
        File.WriteAllText(settings, "k=2");
        File.Delete(history);
        string added = Path.Combine(roaming, "Library", "new.ink");
        File.WriteAllText(added, "new-after-backup");

        ok = AppBackup.Restore(zip, out int applied, out err, _ => { });
        Check("恢复成功（校验通过才换入）", ok, $"ok={ok}，换入 {applied} 项，错误 {err}");
        Check("设置恢复成备份里的内容",
              File.Exists(settings) && File.ReadAllText(settings) == "k=1",
              File.Exists(settings) ? File.ReadAllText(settings) : "（文件没了）");
        Check("历史板书恢复",
              File.Exists(history) && File.ReadAllText(history) == "history",
              File.Exists(history) ? File.ReadAllText(history) : "（文件没了）");
        Check("收藏目录整个换回（备份之后新加的条目不在）",
              File.Exists(libA) && !File.Exists(added),
              $"a.ink={File.Exists(libA)}，new.ink={File.Exists(added)}");
        Check("旧数据留在 .before-restore（恢复错了还能捞）",
              File.Exists(Path.Combine(roaming, "Library.before-restore", "new.ink")),
              "Library.before-restore/new.ink");

        // ---- 坏包：路径穿越必须拒绝，且现有数据不动 ----
        string bad = Path.Combine(root, "bad.zip");
        using (var z = ZipFile.Open(bad, ZipArchiveMode.Create))
        {
            var m = z.CreateEntry(AppBackup.ManifestName);
            using (var w = new StreamWriter(m.Open())) w.Write("format=1\n");
            var e = z.CreateEntry("roaming/../../evil.txt");
            using (var w = new StreamWriter(e.Open())) w.Write("evil");
        }
        bool badOk = AppBackup.Restore(bad, out _, out string badErr);
        Check("坏包（路径穿越）被拒绝",
              !badOk && !string.IsNullOrEmpty(badErr),
              $"ok={badOk}，错误 {badErr}");
        Check("坏包不会动现有数据", File.ReadAllText(settings) == "k=1",
              File.ReadAllText(settings));
        Check("坏包没写出目标目录之外的文件", !File.Exists(Path.Combine(root, "evil.txt")),
              Path.Combine(root, "evil.txt"));

        // ---- 收藏单独导出 / 导入：按内容去重、分类按名字合并 ----
        {
            string lib1 = Path.Combine(root, "fav-one");
            string lib2 = Path.Combine(root, "fav-two");
            ShapeLibrary.DirOverride = lib1;
            var pen = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(1f, 0f, 1f, 1f), Width = 6f,
            };
            pen.AddPoint(100f, 100f, 1f, 0);
            pen.AddPoint(220f, 160f, 1f, 8);
            var saved = ShapeLibrary.SaveEx(new[] { pen });
            var cat = ShapeLibrary.CreateCategory("手绘");
            if (saved.Entry != null) ShapeLibrary.Move(saved.Entry, cat.Id);

            string favZip = Path.Combine(root, "favorites.zip");
            bool favOk = FavoriteTransfer.Export(favZip, out int favFiles, out long favBytes, out string favErr);
            Check("收藏导出成功（带分类表）", favOk && favFiles >= 3 && favBytes > 0,
                  $"ok={favOk}，文件 {favFiles} 个，{favBytes} 字节，错误 {favErr}");

            ShapeLibrary.DirOverride = lib2;
            bool impOk = FavoriteTransfer.Import(favZip, out var r1, out string impErr);
            var imported = ShapeLibrary.List();
            Check("收藏导入成功，分类按名字建出来",
                  impOk && r1.Imported == 1 && imported.Count == 1
                  && imported[0].CategoryName == "手绘",
                  $"ok={impOk}，新增 {r1.Imported}，重复 {r1.Duplicates}，失败 {r1.Failed}，"
                  + $"分类 {imported.FirstOrDefault()?.CategoryName ?? "（空）"}，错误 {impErr}");

            bool impAgain = FavoriteTransfer.Import(favZip, out var r2, out _);
            Check("同一个收藏包再导入 → 按内容去重，不重复存",
                  impAgain && r2.Imported == 0 && r2.Duplicates == 1 && ShapeLibrary.List().Count == 1,
                  $"新增 {r2.Imported}，重复 {r2.Duplicates}，现有 {ShapeLibrary.List().Count} 个");

            // 更新收藏：内容撞另一条要拒绝；只改自己则放行。
            var pen2 = new Stroke
            {
                Tool = Tool.Pen, Kind = StrokeKind.Freehand,
                Color = new Color4(0f, 0f, 1f, 1f), Width = 8f,
            };
            pen2.AddPoint(400f, 400f, 1f, 0);
            pen2.AddPoint(520f, 470f, 1f, 8);
            var saved2 = ShapeLibrary.SaveEx(new[] { pen2 });
            var entries = ShapeLibrary.List();
            Check("收藏列表新的在前（刚存的排第一）",
                  entries.Count >= 2 && entries[0].CreatedAt >= entries[^1].CreatedAt,
                  $"第一条 {entries.FirstOrDefault()?.CreatedAt:HH:mm:ss.fff}，"
                  + $"最后一条 {entries.LastOrDefault()?.CreatedAt:HH:mm:ss.fff}");
            var first = entries.Find(e => e.Path != saved2.Entry?.Path);
            var dup = ShapeLibrary.Replace(first, saved2.Entry?.Strokes);
            Check("更新收藏：改完撞另一条 → 拒绝（不制造重复）",
                  dup.Duplicate && !dup.Success && ShapeLibrary.List().Count == 2,
                  $"重复 {dup.Duplicate}，成功 {dup.Success}，条目 {ShapeLibrary.List().Count} 个");

            float beforeW = first.Strokes[0].Width;
            first.Strokes[0].Width = beforeW + 2f;
            var upd = ShapeLibrary.Replace(first, first.Strokes);
            var reread = ShapeLibrary.List().Find(e => e.Path == first.Path);
            Check("更新收藏：只改自己 → 写入成功、内容真的变了",
                  upd.Success && reread != null
                  && MathF.Abs(reread.Strokes[0].Width - (beforeW + 2f)) < 0.01f,
                  $"成功 {upd.Success}，宽度 {reread?.Strokes?[0].Width:F1}（原 {beforeW:F1}）");

            ShapeLibrary.DirOverride = null;
            try { if (Directory.Exists(lib1)) Directory.Delete(lib1, true); } catch { }
            try { if (Directory.Exists(lib2)) Directory.Delete(lib2, true); } catch { }
        }

        AppBackup.RoamingRootOverride = null;
        AppBackup.LocalRootOverride = null;
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }

        Console.WriteLine(fail == 0
            ? $"  PASS: 全局备份 / 恢复都对（{pass} 项）"
            : $"  FAIL: {fail} 项不对（{pass} 项通过）");
        Console.WriteLine($"  结果: {pass} 项通过, {fail} 项失败");
        ExitCode = fail == 0 ? 0 : 1;
        _quit = true;
    }
}
