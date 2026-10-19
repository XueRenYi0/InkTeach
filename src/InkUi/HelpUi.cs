using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 使用说明页（2026-10-19）。
///
/// 它不是一个单独的弹窗，而是「更多」里的第三页：和「设置」共用同一个
/// MoreRect / 遮罩 / DPI 缩放 / 命中测试，因此 200% 缩放下也居中、
/// 点击页签不会跑到面板外。左上角的返回箭头回到「更多」首页。
///
/// 只做三页：快速上手 / 手势 / 快捷键；白板、课堂工具和故障排查后续再加。
/// 快捷键一律从当前 KeyMap 实时取，不在这里抄一份会过期的键位表。
/// </summary>
public sealed partial class FullUi
{
    private const int HelpTileCode = 8;       // 不占 3..7，保持既有墨迹格编号不变
    private const int HelpTabCount = 3;
    private const int HelpHitTabBase = 40;    // MoreHit 区间：40..42

    private static readonly string[] HelpTabNames = { "快速上手", "手势", "快捷键" };

    private int _helpTab;

    // =====================================================================
    //  入口 / 页面切换
    // =====================================================================

    /// <summary>F1 与「更多」里的入口共用同一个动作。</summary>
    public void ToggleHelp()
    {
        if (_moreOpen && _morePage == MorePage.Help)
        {
            CloseMore();
            return;
        }

        if (!_moreOpen) OpenMore();
        _morePage = MorePage.Help;
        _moreHover = MoreHitNone;
        _morePress = -1;
        _helpTab = Math.Clamp(_helpTab, 0, HelpTabCount - 1);
        // 说明页比首页宽：更多面板的宽度/高度都按页切换；切换时高度做一次短动画。
        _moreH.To(MoreHelpH(), Tokens.MoreOpenMs);
        Invalidate();
    }

    /// <summary>说明页打开时吃掉键盘：Esc/F1 关闭，左右键切页；其它键也不许落到画布。</summary>
    public bool HandleHelpKey(int vk)
    {
        if (!_moreOpen || _morePage != MorePage.Help) return false;
        switch (vk)
        {
            case 0x1B:                         // Esc
            case 0x70:                         // F1
                CloseMore();
                return true;
            case 0x25:                         // ←
                SetHelpTab(_helpTab - 1);
                return true;
            case 0x27:                         // →
                SetHelpTab(_helpTab + 1);
                return true;
            default:
                return true;
        }
    }

    private void SetHelpTab(int i)
    {
        int next = Math.Clamp(i, 0, HelpTabCount - 1);
        if (next == _helpTab) return;
        _helpTab = next;
        _moreH.To(MoreHelpH(), 167);            // 快捷键页更高，切换时收一下高度
        Invalidate();
    }

    private float MoreHelpH()
    {
        float contentH = _helpTab switch
        {
            0 => 400f,                         // 2×2 卡片 + 界面小地图
            1 => 410f,                         // 5 行 × 2 列 + 最容易搞混
            _ => 450f,                         // 两组快捷键列 + 页脚
        };
        return MorePad * 2 + MoreHeaderH + 8f + 42f + 14f + contentH;
    }

    // =====================================================================
    //  布局 / 命中
    // =====================================================================

    private RectF HelpTabRect(int i)
    {
        var r = MoreRect();
        float w = 150f, gap = 10f;
        float x = r.MinX + MorePad + i * (w + gap);
        float y = r.MinY + MorePad + MoreHeaderH + 8f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + 42f };
    }

    private RectF HelpContentRect()
    {
        var r = MoreRect();
        float y = r.MinY + MorePad + MoreHeaderH + 8f + 42f + 14f;
        return new RectF
        {
            MinX = r.MinX + MorePad,
            MinY = y,
            MaxX = r.MaxX - MorePad,
            MaxY = r.MaxY - MorePad,
        };
    }

    // =====================================================================
    //  绘制
    // =====================================================================

    private void DrawHelpPage(ID2D1DeviceContext ctx)
    {
        for (int i = 0; i < HelpTabCount; i++)
        {
            var tab = HelpTabRect(i);
            bool active = i == _helpTab;
            bool hot = _moreHover == HelpHitTabBase + i;
            var rr = new RoundedRectangle(
                new Vortice.RawRectF(tab.MinX, tab.MinY, tab.MaxX, tab.MaxY), 11f, 11f);
            if (active) ctx.FillRoundedRectangle(rr, Brush(ctx, Tokens.Accent));
            else if (hot) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            ctx.DrawRoundedRectangle(rr, Brush(ctx, active ? Tokens.Accent : BorderCol), 1f);
            _widgets.Text(ctx, HelpTabNames[i], tab, 14.5f,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
        }

        var content = HelpContentRect();
        switch (_helpTab)
        {
            case 0: DrawHelpQuick(ctx, content); break;
            case 1: DrawHelpGestures(ctx, content); break;
            default: DrawHelpShortcuts(ctx, content); break;
        }
    }

    private void DrawHelpQuick(ID2D1DeviceContext ctx, in RectF content)
    {
        string[][] cards =
        {
            new[] { "直接写", "选笔，或直接用手写笔 / 鼠标写。\nCtrl+P 切笔。" },
            new[] { "把鼠标还给下层", "点「鼠标」格，或按\nCtrl+Alt+Shift+T；再按一次拿回来。" },
            new[] { "换工具不用找", "按住 Ctrl+Alt+Shift+Q，\n划向扇区，松手；中心取消。" },
            new[] { "写错了", "Ctrl+Z 撤销；橡皮擦局部；\nCtrl+Shift+C 清空当前板书。" },
        };

        float gap = 14f;
        float w = (content.MaxX - content.MinX - gap) * 0.5f;
        float h = 132f;
        for (int i = 0; i < cards.Length; i++)
        {
            int col = i % 2, row = i / 2;
            var box = new RectF
            {
                MinX = content.MinX + col * (w + gap),
                MinY = content.MinY + row * (h + gap),
                MaxX = content.MinX + col * (w + gap) + w,
                MaxY = content.MinY + row * (h + gap) + h,
            };
            DrawHelpCard(ctx, box, i + 1, cards[i][0], cards[i][1]);
        }

        float mapY = content.MinY + 2f * (h + gap) + 4f;
        var map = new RectF
        {
            MinX = content.MinX, MinY = mapY,
            MaxX = content.MaxX, MaxY = MathF.Min(content.MaxY, mapY + 112f),
        };
        if (map.MaxY <= map.MinY) return;
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(map.MinX, map.MinY, map.MaxX, map.MaxY), 12f, 12f),
            Brush(ctx, _dark ? new Color4(1f, 1f, 1f, 0.04f) : new Color4(0f, 0f, 0f, 0.03f)));
        _widgets.Text(ctx, "界面小地图",
                      new RectF { MinX = map.MinX + 16f, MinY = map.MinY + 12f,
                                  MaxX = map.MaxX - 14f, MaxY = map.MinY + 38f },
                      13.5f, Brush(ctx, MutedCol), center: false);
        _widgets.Text(ctx, "底部 = 工具带；工具上方 = 当前设置；右下「更多」= 文件 / 课堂 / 设置。\nPPT / 白板 / 文档打开时，底部还会出现页码条。",
                      new RectF { MinX = map.MinX + 16f, MinY = map.MinY + 38f,
                                  MaxX = map.MaxX - 14f, MaxY = map.MaxY - 10f },
                      14f, Brush(ctx, InkCol), center: false);
    }

    private void DrawHelpCard(ID2D1DeviceContext ctx, in RectF box, int number, string title, string body)
    {
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(box.MinX, box.MinY, box.MaxX, box.MaxY), 12f, 12f),
            Brush(ctx, _dark ? new Color4(1f, 1f, 1f, 0.045f) : new Color4(0f, 0f, 0f, 0.035f)));
        ctx.DrawRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(box.MinX, box.MinY, box.MaxX, box.MaxY), 12f, 12f),
            Brush(ctx, BorderCol), 1f);

        var num = new RectF { MinX = box.MinX + 14f, MinY = box.MinY + 12f,
                              MaxX = box.MinX + 40f, MaxY = box.MinY + 38f };
        ctx.FillEllipse(new Ellipse(new Vector2((num.MinX + num.MaxX) * 0.5f, (num.MinY + num.MaxY) * 0.5f),
                                   13f, 13f), Brush(ctx, Tokens.Accent));
        _widgets.Text(ctx, number.ToString(), num, 12f, Brush(ctx, Tokens.AccentInk));
        _widgets.Text(ctx, title,
                      new RectF { MinX = box.MinX + 50f, MinY = box.MinY + 10f,
                                  MaxX = box.MaxX - 12f, MaxY = box.MinY + 40f },
                      16.5f, Brush(ctx, InkCol), center: false);
        _widgets.Text(ctx, body,
                      new RectF { MinX = box.MinX + 16f, MinY = box.MinY + 44f,
                                  MaxX = box.MaxX - 14f, MaxY = box.MaxY - 10f },
                      13.5f, Brush(ctx, InkCol), center: false);
    }

    private void DrawHelpGestures(ID2D1DeviceContext ctx, in RectF content)
    {
        (string Title, string Note)[] rows =
        {
            ("单指 / 笔书写", "直接写字，和鼠标一样"),
            ("笔停约 0.4 秒", "自动变成规整图形"),
            ("单指长按约 0.5 秒", "进入框选 / 点选"),
            ("双指上下拖", "滚动画布"),
            ("双指左右快滑", "翻一页：白板或 PPT"),
            ("双指轻点", "选中对象"),
            ("双指长按约 0.5 秒", "呼出工具轮盘"),
            ("选中后双指", "移动 / 缩放 / 旋转"),
            ("三指", "擦除"),
            ("手掌", "擦除（屏幕支持接触面积时）"),
        };

        float gap = 22f;
        float w = (content.MaxX - content.MinX - gap) * 0.5f;
        float rowH = 62f;
        for (int i = 0; i < rows.Length; i++)
        {
            int col = i / 5, row = i % 5;
            var box = new RectF
            {
                MinX = content.MinX + col * (w + gap),
                MinY = content.MinY + row * rowH,
                MaxX = content.MinX + col * (w + gap) + w,
                MaxY = content.MinY + row * rowH + rowH - 4f,
            };
            var dot = new Vector2(box.MinX + 5f, box.MinY + 17f);
            ctx.FillEllipse(new Ellipse(dot, 3.5f, 3.5f), Brush(ctx, Tokens.Accent));
            _widgets.Text(ctx, rows[i].Title,
                          new RectF { MinX = box.MinX + 18f, MinY = box.MinY,
                                      MaxX = box.MaxX, MaxY = box.MinY + 30f },
                          14.5f, Brush(ctx, InkCol), center: false);
            _widgets.Text(ctx, rows[i].Note,
                          new RectF { MinX = box.MinX + 18f, MinY = box.MinY + 28f,
                                      MaxX = box.MaxX, MaxY = box.MinY + 56f },
                          12.5f, Brush(ctx, MutedCol), center: false);
        }

        float calloutY = content.MinY + 5f * rowH + 8f;
        var callout = new RectF
        {
            MinX = content.MinX, MinY = calloutY,
            MaxX = content.MaxX, MaxY = MathF.Min(content.MaxY, calloutY + 86f),
        };
        if (callout.MaxY <= callout.MinY) return;
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(callout.MinX, callout.MinY, callout.MaxX, callout.MaxY), 12f, 12f),
            Brush(ctx, _dark ? new Color4(1f, 1f, 1f, 0.045f) : new Color4(0f, 0f, 0f, 0.035f)));
        _widgets.Text(ctx, "最容易搞混的几条",
                      new RectF { MinX = callout.MinX + 16f, MinY = callout.MinY + 10f,
                                  MaxX = callout.MaxX - 14f, MaxY = callout.MinY + 34f },
                      13.5f, Brush(ctx, MutedCol), center: false);
        _widgets.Text(ctx, "双指上下 = 滚动，左右 = 翻页；三指 = 擦；轮盘中心松手 = 取消。\n手掌擦需要屏幕上报接触面积；不支持的屏幕不会触发。",
                      new RectF { MinX = callout.MinX + 16f, MinY = callout.MinY + 34f,
                                  MaxX = callout.MaxX - 14f, MaxY = callout.MaxY - 9f },
                      13.5f, Brush(ctx, InkCol), center: false);
    }

    private string HelpKey(KeyAction a)
    {
        string k = Key(a);
        return string.IsNullOrEmpty(k) ? "未绑定" : k;
    }

    private void DrawHelpShortcuts(ID2D1DeviceContext ctx, in RectF content)
    {
        (string Label, string Key)[] global =
        {
            ("穿透 / 拿回", HelpKey(KeyAction.TogglePassThrough)),
            ("退出软件", HelpKey(KeyAction.Quit)),
            ("呼出轮盘", HelpKey(KeyAction.RadialPalette)),
        };
        (string Label, string Key)[] tools =
        {
            ("笔", HelpKey(KeyAction.ToolPen)),
            ("荧光笔", HelpKey(KeyAction.ToolHighlighter)),
            ("激光笔", HelpKey(KeyAction.ToolLaser)),
            ("橡皮", HelpKey(KeyAction.ToolEraser)),
            ("选择", HelpKey(KeyAction.ToolMarquee)),
            ("截图", HelpKey(KeyAction.ToolCapture)),
        };
        (string Label, string Key)[] edit =
        {
            ("撤销 / 重做", $"{HelpKey(KeyAction.Undo)} / {HelpKey(KeyAction.Redo)}"),
            ("全选", HelpKey(KeyAction.SelectAll)),
            ("复制 / 粘贴", $"{HelpKey(KeyAction.Copy)} / {HelpKey(KeyAction.Paste)}"),
            ("删除选中", HelpKey(KeyAction.DeleteSelected)),
            ("取消", HelpKey(KeyAction.CancelSelection)),
            ("放大 / 缩小", $"{HelpKey(KeyAction.ScaleUp)} / {HelpKey(KeyAction.ScaleDown)}"),
            ("清空当前板书", HelpKey(KeyAction.Clear)),
        };
        (string Label, string Key)[] board =
        {
            ("白板开关", HelpKey(KeyAction.ToggleBoard)),
            ("白板 / 文档翻页", $"{HelpKey(KeyAction.FlipPageUp)} / {HelpKey(KeyAction.FlipPageDown)}"),
            ("PPT 翻页", $"{HelpKey(KeyAction.FlipPageUp)} / {HelpKey(KeyAction.FlipPageDown)} 或 ← / →"),
        };
        (string Label, string Key)[] help =
        {
            ("使用说明", "F1"),
            ("关闭说明", "Esc"),
        };

        float gap = 32f;
        float w = (content.MaxX - content.MinX - gap) * 0.5f;
        float leftX = content.MinX;
        float rightX = content.MinX + w + gap;
        float y = content.MinY;
        y = DrawHelpShortcutGroup(ctx, leftX, y, w, "全局键（任何软件前台）", global);
        y = DrawHelpShortcutGroup(ctx, leftX, y + 12f, w, "工具（批注键盘）", tools);

        float ry = content.MinY;
        ry = DrawHelpShortcutGroup(ctx, rightX, ry, w, "编辑（批注键盘）", edit);
        ry = DrawHelpShortcutGroup(ctx, rightX, ry + 12f, w, "白板 / 放映", board);
        ry = DrawHelpShortcutGroup(ctx, rightX, ry + 12f, w, "说明", help);

        _widgets.Text(ctx, "这里显示当前生效键位；改键后本页会同步。",
                      new RectF { MinX = content.MinX, MinY = content.MaxY - 24f,
                                  MaxX = content.MaxX, MaxY = content.MaxY },
                      11.5f, Brush(ctx, MutedCol), center: false);
    }

    private float DrawHelpShortcutGroup(
        ID2D1DeviceContext ctx, float x, float y, float w, string title,
        (string Label, string Key)[] rows)
    {
        _widgets.Text(ctx, title,
                      new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + 24f },
                      13f, Brush(ctx, MutedCol), center: false);
        float rowY = y + 26f;
        foreach (var row in rows)
        {
            _widgets.Text(ctx, row.Label,
                          new RectF { MinX = x, MinY = rowY, MaxX = x + w * 0.60f, MaxY = rowY + 27f },
                          13.5f, Brush(ctx, InkCol), center: false);
            _widgets.Text(ctx, row.Key,
                          new RectF { MinX = x + w * 0.56f, MinY = rowY, MaxX = x + w, MaxY = rowY + 27f },
                          13.5f, Brush(ctx, InkCol));
            rowY += 28f;
        }
        return rowY;
    }

    // ---- 自检入口 --------------------------------------------------------
    internal bool HelpOpenForTest => _moreOpen && _morePage == MorePage.Help;
    internal int HelpTabForTest => _helpTab;
    internal RectF HelpTabRectForTest(int i) => HelpTabRect(i);

    internal void OpenHelpForTest()
    {
        if (!_moreOpen) OpenMore();
        _morePage = MorePage.Help;
        _moreHover = MoreHitNone;
        _moreH.Jump(MoreHelpH());
        Invalidate();
    }

    internal void SetHelpTabForTest(int tab) => SetHelpTab(tab);
}
