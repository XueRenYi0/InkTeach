using InkEngine;

namespace InkTeach;

internal sealed partial class App
{
    /// <summary>出图：图片选中条 + ⋯ 菜单（保存移进去），给验收看图标顺序。</summary>
    private void SelBeautifyShow(string path)
    {
        SetUiFactory(() => new InkUi.FullUi());
        SettleFrames(300);
        PassThrough = false;
        BoardOn = true;
        Doc.Clear();
        Doc.ClearHistory();
        Tool = Tool.Marquee;

        var img = Doc.AddImage(MakeTestImage(480, 360), 600f, 420f, 1f);
        Doc.SelectOnly(new[] { img });
        SettleFrames(400);

        var bar = SelectionBarCanvasRect;
        if (OffscreenFloatingShot(path, bar.Inflate(50f)))
            Console.WriteLine($"  已出图：{path}");
        else Console.WriteLine("  出图失败：" + path);

        OpenSelectionMenuForTest(ai: false);
        SettleFrames(250);
        var menu = SelectionMenuRectNow();
        var both = bar;
        both.Add(menu);
        string more = Path.Combine(Path.GetDirectoryName(path) ?? ".",
            Path.GetFileNameWithoutExtension(path) + "-more" + Path.GetExtension(path));
        if (OffscreenFloatingShot(more, both.Inflate(30f)))
            Console.WriteLine($"  已出图：{more}");
        else Console.WriteLine("  出图失败：" + more);

        _quit = true;
    }
}
