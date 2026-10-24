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

    // 2026-10-20：说明页自己的字号回落到和「设置」同一套：
    // 标题/卡片 14、正文 13、辅助 12.5、页脚 11.5；不再单独用 16.5/14.5。
    private const float HelpTabFont = 13f;
    private const float HelpTitleFont = 14f;
    private const float HelpBodyFont = 13f;
    private const float HelpNoteFont = 12.5f;
    private const float HelpKeyFont = 12f;
    private const float HelpFootFont = 11.5f;

    private static readonly string[] HelpTabNames = { "快速上手", "手势", "快捷键" };

    private int _helpTab;
    private float _helpScroll;

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
        _helpScroll = 0f;
        // 同一 MoreRect 的宽度不变，只在内容需要时做一次高度动画。
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
            case 0x26:                         // ↑
                ScrollHelp(-36f);
                return true;
            case 0x28:                         // ↓
                ScrollHelp(+36f);
                return true;
            case 0x21:                         // PageUp
                ScrollHelp(-(HelpViewportRect().MaxY - HelpViewportRect().MinY) * 0.8f);
                return true;
            case 0x22:                         // PageDown
                ScrollHelp(+(HelpViewportRect().MaxY - HelpViewportRect().MinY) * 0.8f);
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
        _helpScroll = 0f;
        _moreH.To(MoreHelpH(), 167);            // 快捷键页更高，切换时收一下高度
        Invalidate();
    }

    /// <summary>说明页外框（标题、页签、上下留白）；内容区在下面单独算。</summary>
    private static float HelpShellH => MorePad * 2 + MoreHeaderH + 8f + 42f + 14f;

    private float HelpLayoutW()
    {
        float w = MathF.Min(MoreWidth(), MathF.Max(240f, (_work.MaxX - _work.MinX) - 16f));
        return MathF.Max(120f, w - MorePad * 2f);
    }

    /// <summary>当前宽度/页签下，内容真正需要的高度（不裁切）。</summary>
    private float HelpDesiredH()
    {
        float w = HelpLayoutW();
        switch (_helpTab)
        {
            case 0:
            {
                int cols = w < 620f ? 1 : 2;
                int rows = (4 + cols - 1) / cols;
                return rows * (132f + 14f) + 4f + 112f;
            }
            case 1:
            {
                int cols = w < 620f ? 1 : 2;
                int rows = (10 + cols - 1) / cols;
                return rows * 62f + 8f + 86f;
            }
            default:
            {
                // 快捷键：宽度够就两列；窄屏改一列，内容在面板里滚动。
                bool one = w < 620f;
                int[] n = { 3, 6, 7, 3, 2 };
                float GroupH(int count) => 26f + count * 28f;
                if (one)
                {
                    float h = 0f;
                    for (int i = 0; i < n.Length; i++)
                        h += GroupH(n[i]) + (i + 1 < n.Length ? 12f : 0f);
                    return h + 24f;
                }
                float left = GroupH(n[0]) + 12f + GroupH(n[1]);
                float right = GroupH(n[2]) + 12f + GroupH(n[3]) + 12f + GroupH(n[4]);
                return MathF.Max(left, right) + 24f;
            }
        }
    }

    private float MoreHelpH()
    {
        float workH = _work.MaxY - _work.MinY;
        float maxPanel = MathF.Max(260f, MathF.Min(workH - 16f, workH * 0.80f));
        return MathF.Min(HelpShellH + HelpDesiredH(), maxPanel);
    }

    // =====================================================================
    //  布局 / 命中
    // =====================================================================

    private RectF HelpTabRect(int i)
    {
        var r = MoreRect();
        float gap = 10f;
        float avail = r.MaxX - r.MinX - MorePad * 2f;
        float w = MathF.Min(150f, MathF.Max(80f, (avail - gap * (HelpTabCount - 1)) / HelpTabCount));
        float x = r.MinX + MorePad + i * (w + gap);
        float y = r.MinY + MorePad + MoreHeaderH + 8f;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + 42f };
    }

    /// <summary>可见内容区（滚动视口）。</summary>
    private RectF HelpViewportRect()
    {
        var r = MoreRect();
        float y = r.MinY + MorePad + MoreHeaderH + 8f + 42f + 14f;
        return new RectF
        {
            MinX = r.MinX + MorePad,
            MinY = y,
            MaxX = r.MaxX - MorePad,
            MaxY = MathF.Max(y, r.MaxY - MorePad),
        };
    }

    /// <summary>内容的完整矩形（可以比视口高；绘制时向上平移 _helpScroll）。</summary>
    private RectF HelpFullContentRect()
    {
        var v = HelpViewportRect();
        float h = MathF.Max(v.MaxY - v.MinY, HelpDesiredH());
        return new RectF
        {
            MinX = v.MinX,
            MinY = v.MinY - _helpScroll,
            MaxX = v.MaxX,
            MaxY = v.MinY - _helpScroll + h,
        };
    }

    private float HelpMaxScroll()
    {
        float vh = HelpViewportRect().MaxY - HelpViewportRect().MinY;
        return MathF.Max(0f, HelpDesiredH() - vh);
    }

    private void ScrollHelp(float delta)
    {
        float max = HelpMaxScroll();
        float next = Math.Clamp(_helpScroll + delta, 0f, max);
        if (MathF.Abs(next - _helpScroll) < 0.01f) return;
        _helpScroll = next;
        Invalidate();
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
            _widgets.Text(ctx, HelpTabNames[i], tab, HelpTabFont,
                          Brush(ctx, active ? Tokens.AccentInk : InkCol));
        }

        var viewport = HelpViewportRect();
        _helpScroll = Math.Clamp(_helpScroll, 0f, HelpMaxScroll());
        ctx.PushAxisAlignedClip(
            new Vortice.RawRectF(viewport.MinX, viewport.MinY, viewport.MaxX, viewport.MaxY),
            AntialiasMode.Aliased);
        var content = HelpFullContentRect();
        switch (_helpTab)
        {
            case 0: DrawHelpQuick(ctx, content); break;
            case 1: DrawHelpGestures(ctx, content); break;
            default: DrawHelpShortcuts(ctx, content); break;
        }
        ctx.PopAxisAlignedClip();

        // 内容超过视口时，右侧给一条很轻的滚动提示；不抢面板内容。
        float maxScroll = HelpMaxScroll();
        if (maxScroll > 0.5f)
        {
            float trackX = viewport.MaxX + 5f;
            float h = viewport.MaxY - viewport.MinY;
            float thumbH = MathF.Max(28f, h * h / (h + maxScroll));
            float t = maxScroll <= 0.01f ? 0f : _helpScroll / maxScroll;
            float top = viewport.MinY + (h - thumbH) * t;
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(trackX, top, trackX + 3f, top + thumbH), 1.5f, 1.5f),
                Brush(ctx, _dark ? new Color4(1f, 1f, 1f, 0.28f) : new Color4(0f, 0f, 0f, 0.22f)));
        }
    }

    private void DrawHelpQuick(ID2D1DeviceContext ctx, in RectF content)
    {
        string[][] cards =
        {
            new[] { "直接写", "选笔，或直接用手写笔 / 鼠标写。\nCtrl+P 切笔。" },
            new[] { "把鼠标还给下层", "点「鼠标」格，或按\nCtrl+Alt+Shift+T；再按一次回到画笔。" },
            new[] { "换工具不用找", "按住 Ctrl+Alt+Shift+Q，\n划向扇区，松手；中心取消。" },
            new[] { "写错了", "Ctrl+Z 撤销；橡皮擦局部；\nCtrl+Shift+C 清空当前板书。" },
            new[] { "设备与性能统计", "首次弹窗选择；设置 → 书写可改。\n不含姓名、账号、课件或屏幕内容。" },
        };

        float gap = 14f;
        int cols = (content.MaxX - content.MinX) < 620f ? 1 : 2;
        float w = (content.MaxX - content.MinX - gap * (cols - 1)) / cols;
        float h = 132f;
        for (int i = 0; i < cards.Length; i++)
        {
            int col = i % cols, row = i / cols;
            var box = new RectF
            {
                MinX = content.MinX + col * (w + gap),
                MinY = content.MinY + row * (h + gap),
                MaxX = content.MinX + col * (w + gap) + w,
                MaxY = content.MinY + row * (h + gap) + h,
            };
            DrawHelpCard(ctx, box, i + 1, cards[i][0], cards[i][1]);
        }

        float mapY = content.MinY + ((cards.Length + cols - 1) / cols) * (h + gap) + 4f;
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
                      HelpNoteFont, Brush(ctx, MutedCol), center: false);
        _widgets.Text(ctx, "底部 = 工具带；工具上方 = 当前设置；右下「更多」= 文件 / 课堂 / 设置。\nPPT / 白板 / 文档打开时，底部还会出现页码条。",
                      new RectF { MinX = map.MinX + 16f, MinY = map.MinY + 38f,
                                  MaxX = map.MaxX - 14f, MaxY = map.MaxY - 10f },
                      HelpBodyFont, Brush(ctx, InkCol), center: false);
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
        _widgets.Text(ctx, number.ToString(), num, 11.5f, Brush(ctx, Tokens.AccentInk));
        _widgets.Text(ctx, title,
                      new RectF { MinX = box.MinX + 50f, MinY = box.MinY + 9f,
                                  MaxX = box.MaxX - 12f, MaxY = box.MinY + 38f },
                      HelpTitleFont, Brush(ctx, InkCol), center: false);
        _widgets.Text(ctx, body,
                      new RectF { MinX = box.MinX + 16f, MinY = box.MinY + 42f,
                                  MaxX = box.MaxX - 14f, MaxY = box.MaxY - 10f },
                      HelpBodyFont, Brush(ctx, InkCol), center: false);
    }

    private void DrawHelpGestures(ID2D1DeviceContext ctx, in RectF content)
    {
        (string Title, string Note)[] rows =
        {
            ("触摸模式：手势", "默认；双指 / 三指 / 长按都可用"),
            ("触摸模式：单指", "只允许一个触点写字，无手势"),
            ("单指 / 笔书写", "直接写字，和鼠标一样"),
            ("笔停约 0.4 秒", "自动变成规整图形"),
            ("单指长按 0.5 秒", "手势模式：框选 / 点选"),
            ("双指上下 / 左右", "手势模式：滚动画布 / 翻一页"),
            ("双指轻点 / 长按", "手势模式：呼出工具轮盘"),
            ("选中后双指", "手势模式：移动 / 缩放 / 旋转"),
            ("三指", "手势模式：擦除"),
            ("退出穿透", "回到画笔（点穿透格 / 全局键）"),
        };

        float gap = 22f;
        int cols = (content.MaxX - content.MinX) < 620f ? 1 : 2;
        float w = (content.MaxX - content.MinX - gap * (cols - 1)) / cols;
        float rowH = 62f;
        for (int i = 0; i < rows.Length; i++)
        {
            int col = i / 5, row = i % 5;
            if (cols == 1) { col = 0; row = i; }
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
                          HelpBodyFont, Brush(ctx, InkCol), center: false);
            _widgets.Text(ctx, rows[i].Note,
                          new RectF { MinX = box.MinX + 18f, MinY = box.MinY + 28f,
                                      MaxX = box.MaxX, MaxY = box.MinY + 56f },
                          HelpNoteFont, Brush(ctx, MutedCol), center: false);
        }

        float calloutY = content.MinY + (cols == 1 ? 10f : 5f) * rowH + 8f;
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
                      HelpNoteFont, Brush(ctx, MutedCol), center: false);
        _widgets.Text(ctx, "手势模式：双指上下 = 滚动，左右 = 翻页；三指 = 擦；轮盘中心 = 取消。\n单指模式没有这些手势；面积/手掌擦已取消，擦除请用工具栏的整笔/像素橡皮。",
                      new RectF { MinX = callout.MinX + 16f, MinY = callout.MinY + 34f,
                                  MaxX = callout.MaxX - 14f, MaxY = callout.MaxY - 9f },
                      HelpBodyFont, Brush(ctx, InkCol), center: false);
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
            ("PPT 翻页", $"{HelpKey(KeyAction.FlipPageUp)} / {HelpKey(KeyAction.FlipPageDown)} / ←→"),
        };
        (string Label, string Key)[] help =
        {
            ("使用说明", "F1"),
            ("关闭说明", "Esc"),
        };

        bool oneCol = (content.MaxX - content.MinX) < 620f;
        float gap = 32f;
        float w = oneCol
            ? content.MaxX - content.MinX
            : (content.MaxX - content.MinX - gap) * 0.5f;
        float leftX = content.MinX;
        float rightX = content.MinX + w + gap;
        float y = content.MinY;
        if (oneCol)
        {
            y = DrawHelpShortcutGroup(ctx, leftX, y, w, "全局键（任何软件前台）", global);
            y = DrawHelpShortcutGroup(ctx, leftX, y + 12f, w, "工具（批注键盘）", tools);
            y = DrawHelpShortcutGroup(ctx, leftX, y + 12f, w, "编辑（批注键盘）", edit);
            y = DrawHelpShortcutGroup(ctx, leftX, y + 12f, w, "白板 / 放映", board);
            y = DrawHelpShortcutGroup(ctx, leftX, y + 12f, w, "说明", help);
        }
        else
        {
            y = DrawHelpShortcutGroup(ctx, leftX, y, w, "全局键（任何软件前台）", global);
            y = DrawHelpShortcutGroup(ctx, leftX, y + 12f, w, "工具（批注键盘）", tools);

            float ry = content.MinY;
            ry = DrawHelpShortcutGroup(ctx, rightX, ry, w, "编辑（批注键盘）", edit);
            ry = DrawHelpShortcutGroup(ctx, rightX, ry + 12f, w, "白板 / 放映", board);
            ry = DrawHelpShortcutGroup(ctx, rightX, ry + 12f, w, "说明", help);
        }

        _widgets.Text(ctx, "这里显示当前生效键位；改键后本页会同步。",
                      new RectF { MinX = content.MinX, MinY = content.MaxY - 24f,
                                  MaxX = content.MaxX, MaxY = content.MaxY },
                      HelpFootFont, Brush(ctx, MutedCol), center: false);
    }

    private float DrawHelpShortcutGroup(
        ID2D1DeviceContext ctx, float x, float y, float w, string title,
        (string Label, string Key)[] rows)
    {
        _widgets.Text(ctx, title,
                      new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + 24f },
                      HelpNoteFont, Brush(ctx, MutedCol), center: false);
        float rowY = y + 26f;
        foreach (var row in rows)
        {
            _widgets.Text(ctx, row.Label,
                          new RectF { MinX = x, MinY = rowY, MaxX = x + w * 0.40f, MaxY = rowY + 27f },
                          HelpBodyFont, Brush(ctx, InkCol), center: false);
            _widgets.Text(ctx, row.Key,
                          new RectF { MinX = x + w * 0.38f, MinY = rowY, MaxX = x + w, MaxY = rowY + 27f },
                          HelpKeyFont, Brush(ctx, InkCol));
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
        _helpScroll = 0f;
        _moreH.Jump(MoreHelpH());
        Invalidate();
    }

    internal void SetHelpTabForTest(int tab) => SetHelpTab(tab);
    internal float HelpScrollForTest => _helpScroll;
    internal float HelpDesiredHForTest => HelpDesiredH();
    internal float HelpMoreHForTest => MoreHelpH();
}
