using InkEngine;
using Vortice.Mathematics;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>
    /// `--libraryshow [路径]`：离屏出收藏弹窗的横排 / 竖排 / ⋯ 菜单 / 分类抽屉四张图。
    /// 不依赖合成鼠标，锁屏/远程桌面下也能看布局。
    /// </summary>
    private void LibraryShow(string path)
    {
        string dir = Path.Combine(Path.GetTempPath(), "inkteach-library-show");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        ShapeLibrary.DirOverride = dir;

        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        if (CurrentUi is InkUi.FullUi ui) ui.SetExpandForTest(1f);
        SettleFrames(300);

        for (int i = 0; i < 9; i++)
        {
            var st = new Stroke
            {
                Tool = i % 3 == 0 ? Tool.Rectangle : Tool.Pen,
                Kind = i % 3 == 0 ? StrokeKind.Rectangle : StrokeKind.Freehand,
                Color = new Color4(0.15f + i * 0.08f, 0.35f, 0.8f, 1f),
                Width = 5f,
            };
            if (st.Kind == StrokeKind.Rectangle)
            {
                st.AddPoint(100f + i * 30f, 100f, 1f, 0);
                st.AddPoint(240f + i * 30f, 220f, 1f, 8);
            }
            else
            {
                st.AddPoint(100f + i * 30f, 100f, 1f, 0);
                st.AddPoint(180f + i * 30f, 190f, 1f, 4);
                st.AddPoint(260f + i * 30f, 130f, 1f, 8);
            }
            ShapeLibrary.SaveEx(new[] { st });
        }

        CreateLibraryCategory();
        string catA = LibraryCategoryId;
        CreateLibraryCategory();
        string catB = LibraryCategoryId;
        var list = ShapeLibrary.List();
        for (int i = 0; i < list.Count; i++)
            ShapeLibrary.Move(list[i], i % 3 == 0 ? catA : i % 3 == 1 ? catB : ShapeLibrary.Uncategorized);
        ReloadLibrary();
        SetLibraryCategory(LibraryCategoryIds.All);
        OpenLibraryPanel();
        SettleFrames(500);

        var panel = LibraryPanelRectNow();
        if (OffscreenFloatingShot(path, panel.Inflate(16f)))
            Console.WriteLine($"  已出图：{path}");
        else Console.WriteLine("  出图失败：" + path);

        ToggleLibraryOrientation();
        SettleFrames(400);
        var panelV = LibraryPanelRectNow();
        string vpath = Path.Combine(Path.GetDirectoryName(path) ?? ".",
            Path.GetFileNameWithoutExtension(path) + "-v" + Path.GetExtension(path));
        if (OffscreenFloatingShot(vpath, panelV.Inflate(16f)))
            Console.WriteLine($"  已出图：{vpath}");
        else Console.WriteLine("  出图失败：" + vpath);

        LibraryMenu = 4;
        SettleFrames(250);
        string mpath = Path.Combine(Path.GetDirectoryName(path) ?? ".",
            Path.GetFileNameWithoutExtension(path) + "-menu" + Path.GetExtension(path));
        var panelM = LibraryPanelRectNow();
        if (OffscreenFloatingShot(mpath, panelM.Inflate(16f)))
            Console.WriteLine($"  已出图：{mpath}");
        else Console.WriteLine("  出图失败：" + mpath);

        // 整理：勾选框 / 排序把手 / 操作条。
        LibraryMenu = 0;
        LibrarySelectMode = true;
        LibrarySelected.Clear();
        foreach (var e in LibraryEntries.Take(2)) LibrarySelected.Add(e);
        SettleFrames(300);
        string gpath = Path.Combine(Path.GetDirectoryName(path) ?? ".",
            Path.GetFileNameWithoutExtension(path) + "-manage" + Path.GetExtension(path));
        var panelG = LibraryPanelRectNow();
        if (OffscreenFloatingShot(gpath, panelG.Inflate(16f)))
            Console.WriteLine($"  已出图：{gpath}");
        else Console.WriteLine("  出图失败：" + gpath);
        LibrarySelectMode = false;
        LibrarySelected.Clear();

        // 分类管理：铅笔 / 垃圾桶 / 删除确认两行文案（覆盖在卡片上，不占布局）。
        LibraryMenu = 0;
        LibraryCategoriesOpen = true;
        LibraryDrawerManage = true;
        LibraryCategoryScroll = 0;
        SetLibraryCategory(LibraryCategoryIds.All);
        LibraryConfirm = "delcat:" + catA;
        SettleFrames(300);
        string dpath = Path.Combine(Path.GetDirectoryName(path) ?? ".",
            Path.GetFileNameWithoutExtension(path) + "-drawer" + Path.GetExtension(path));
        var panelD = LibraryPanelRectNow();
        if (OffscreenFloatingShot(dpath, panelD.Inflate(16f)))
            Console.WriteLine($"  已出图：{dpath}");
        else Console.WriteLine("  出图失败：" + dpath);

        CloseLibraryPanel();
        ShapeLibrary.DirOverride = null;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        _quit = true;
    }
}
