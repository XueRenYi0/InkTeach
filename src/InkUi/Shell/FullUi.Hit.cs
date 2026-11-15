using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using InkEngine;
using InkLocalization;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace InkUi;

/// <summary>
/// 完整界面：收起态是**一个球**，点开是一条**按钮带**；球就是带子最左那一格。
///
/// 这是第一版，刻意只做"按钮 ＋ 命中测试"（色带、粗细滑块、「更多」抽屉都还没有）：
/// 先把**坐标 / DPI / 脏区 / 输入拦截**这四件最容易出错的事验对，
/// 后面搬色带和抽屉都只是堆代码（见 计划-底层对接界面.md 阶段 2）。
///
/// 它只认引擎的三个接口：画自己、解释点击、改状态走命令。
/// </summary>

public sealed partial class FullUi
{
    // ---- 启动器几何 ----------------------------------------------------------

    /// <summary>第 0 组（课堂）的组头 y。</summary>
    private float HubSec0Y() => MoreRect().MinY + MorePad + MoreHeaderH + 8f;

    private int HubRows(int section)
    {
        int count = HubCount(section);
        return Math.Max(1, (count + TileCols() - 1) / TileCols());
    }

    /// <summary>
    /// 每个分组的格子数：课堂 5 个（计时 / 点名 / 随机一人 / AI 出题 / 使用说明）；
    /// 墨迹 **5 个**（保存… / 打开… / 墨迹回放 / 关闭文档 / 历史板书）。
    /// 收藏只留在主工具条那一格，更多里不再放重复入口。
    /// </summary>
    private int HubCount(int section) => 5;

    /// <summary>第 section 组的组头矩形。</summary>
    private RectF HubHeadRect(int section)
    {
        float y = HubSec0Y();
        for (int s = 0; s < section; s++)
            y += MoreGroupHeadH + HubRows(s) * (MoreTile + MoreTileGap) + MoreGroupGap;
        return new RectF { MinX = MoreLeftX(), MinY = y, MaxX = MoreLeftX() + MoreContentW(), MaxY = y + MoreGroupHeadH };
    }

    private RectF HubTileRect(int section, int index)
    {
        var head = HubHeadRect(section);
        int cols = TileCols();
        int col = index % cols, row = index / cols;
        float x = MoreLeftX() + col * (MoreTile + MoreTileGap);
        float y = head.MaxY + row * (MoreTile + MoreTileGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + MoreTile, MaxY = y + MoreTile };
    }

    /// <summary>第 section 组第 index 个格子的全局编号（从 <see cref="HubCount"/> 累加，
    /// 加一组/加一格都不用改这里）。</summary>
    private int HubTileCode(int section, int index)
    {
        // 课堂组第 4 格是「使用说明」，但**编号故意仍用 8**：它原来没有占 3..7，
        // 这样保存/打开/回放/关闭文档/历史板书那五格的编号和既有自检全部不变。
        if (section == 0)
        {
            if (index < 3) return index;
            if (index == 3) return AiTileCode;
            return HelpTileCode;
        }
        // 3..7 = 保存/打开/回放/关文档/历史板书；8 留给「使用说明」。
        return 3 + index;
    }

    /// <summary>当前工作区宽度下能排几列格子。**只用工作区/面板宽算**，不经过
    /// `MoreRect()`——那里有开合动画中的高度值，布局计算（MoreHomeH→HubRows）碰它
    /// 会递归。式子与 MoreRect 的实际宽度同源：min(MoreWidth, max(240, 工作宽-16))。 </summary>
    private int TileCols()
    {
        float w = MathF.Min(MoreWidth(), MathF.Max(240f, (_work.MaxX - _work.MinX) - 16f));
        float contentW = w - MorePad * 2f;
        return Math.Clamp((int)((contentW + MoreTileGap) / (MoreTile + MoreTileGap)), 3, 6);
    }

    private RectF HubStatusRect() => new()
    {
        MinX = MoreLeftX(), MaxY = HubBottomRect(0).MinY - 8f,
        MaxX = MoreLeftX() + MoreContentW(), MinY = HubBottomRect(0).MinY - 8f - MoreStatusH,
    };

    private RectF HubBottomRect(int i)
    {
        var head = HubHeadRect(1);
        float y = head.MaxY + HubRows(1) * (MoreTile + MoreTileGap) + MoreGroupGap + MoreStatusH + 8f;
        // **五格也要放得下**（2026-10-17 加「日志」）：窄面板上按内容宽均分，不超出。
        float tw = MathF.Min(MoreTile,
            (MoreContentW() - (MoreBottomTiles.Length - 1) * MoreTileGap) / MoreBottomTiles.Length);
        float x = MoreLeftX() + i * (tw + MoreTileGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + tw, MaxY = y + tw };
    }

    // ---- 设置子页几何 --------------------------------------------------------

    private float SetToolHeadY() => SettingsY(SettingsSectionTop(SettingsCategory.Toolbar));

    private RectF MoreProfileRect(int i)
    {
        float w = (SettingsContentW() - MoreProfileGap * 2f) / 3f;
        float x = SettingsContentLeft() + i * (w + MoreProfileGap);
        float y = SetToolHeadY() + MoreGroupHeadH;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + MoreProfileH };
    }

    private RectF MoreChipRect(int cell)
    {
        int idx = cell - 1;
        float w = (SettingsContentW() - (MoreChipCols - 1) * MoreChipGap) / MoreChipCols;
        int col = idx % MoreChipCols, row = idx / MoreChipCols;
        float y = MoreProfileRect(0).MaxY + MoreChipTopGap + row * (MoreChipH + MoreChipGap);
        float x = SettingsContentLeft() + col * (w + MoreChipGap);
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + MoreChipH };
    }

    /// <summary>
    /// 行在**连续设置页**里的位置：先找它属于哪个分类、分类里的第几行，
    /// 再加上分类在内容里的起点、减去页面滚动。
    ///
    /// 2026-11-07：从"一分类一页"改成"一整页连续滚动"——所有行都参与绘制/命中，
    /// 左侧分类只是跳到对应段落；这也是"AI/备份/关于只有一个开关"不再显得空的原因。
    /// </summary>
    private RectF MoreRowRect(int i)
    {
        var kind = Rows[i].Kind;
        for (int c = 0; c < SettingsCatNames.Length; c++)
        {
            var rows = SettingsRows((SettingsCategory)c);
            int idx = Array.IndexOf(rows, kind);
            if (idx < 0) continue;
            float y = SettingsY(SettingsSectionTop((SettingsCategory)c)
                                + MoreGroupHeadH + idx * MoreRowH);
            return new RectF
            {
                MinX = SettingsContentLeft(), MinY = y,
                MaxX = SettingsContentLeft() + SettingsContentW(), MaxY = y + MoreRowH,
            };
        }
        return RectF.Empty;
    }

    /// <summary>自检用：不管当前分类，都给出某一行在连续页里的位置。</summary>
    private RectF SettingsRowRectFor(Row kind)
    {
        for (int i = 0; i < Rows.Length; i++)
            if (Rows[i].Kind == kind) return MoreRowRect(i);
        return RectF.Empty;
    }

    private RectF MoreSwitchRect(int i) => new()
    {
        MinX = MoreRowRect(i).MaxX - MoreSwitchW - 4f,
        MinY = (MoreRowRect(i).MinY + MoreRowRect(i).MaxY) * 0.5f - MoreSwitchH * 0.5f,
        MaxX = MoreRowRect(i).MaxX - 4f,
        MaxY = (MoreRowRect(i).MinY + MoreRowRect(i).MaxY) * 0.5f + MoreSwitchH * 0.5f,
    };

    // ---- 语言下拉（几何 / 命中 / 开合）--------------------------------------

    /// <summary>设置页“语言”行当前显示 / 下拉里打勾的那一项（未显式选过就按启动时的选择）。</summary>
    private string PendingLangPref => _host?.GetPref("lang") ?? Loc.PrefCode;

    /// <summary>选中项和启动语言不同才允许“重启以应用”，避免点一下做无意义的重启。</summary>
    private bool LangMenuApplyEnabled
        => !string.Equals(Loc.DisplayName(PendingLangPref), Loc.DisplayName(Loc.PrefCode),
                          StringComparison.Ordinal);

    /// <summary>
    /// 下拉卡片：右边缘对齐“语言”行，优先开在行下方；下面放不下就翻到上方。
    /// 卡片可以伸出设置面板（QueryBounds 在“更多”打开时就是整屏），只夹在屏幕内。
    /// </summary>
    private RectF LangMenuRect()
    {
        var row = MoreRowRect(RowIndex(Row.Language));
        float w = MathF.Min(LangMenuW, _screen.MaxX - _screen.MinX - 16f);
        float h = LangMenuH;
        // 右边缘让开设置页滚动条 10px：否则下拉自己的细滚动条和蓝色页面滚动条挨在一起。
        float x = row.MaxX - 10f - w;
        float y = row.MaxY + 6f;
        const float m = 8f;
        if (y + h > _screen.MaxY - m) y = row.MinY - 6f - h;
        y = Math.Clamp(y, _screen.MinY + m, MathF.Max(_screen.MinY + m, _screen.MaxY - m - h));
        // 翻到上方时会压住标题栏右侧的 ✕：整体左移一点，把关闭按钮让出来。
        if (y < MoreHeaderRect().MaxY)
        {
            float safeRight = MoreCloseRect().MinX - 8f;
            if (x + w > safeRight) x = safeRight - w;
        }
        x = Math.Clamp(x, _screen.MinX + m, MathF.Max(_screen.MinX + m, _screen.MaxX - m - w));
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    private RectF LangMenuHeaderRect()
    {
        var r = LangMenuRect();
        return new RectF
        {
            MinX = r.MinX + LangMenuPad, MinY = r.MinY + LangMenuPad,
            MaxX = r.MaxX - LangMenuPad, MaxY = r.MinY + LangMenuPad + LangMenuHeadH,
        };
    }

    private RectF LangMenuItemsRect()
    {
        var head = LangMenuHeaderRect();
        return new RectF
        {
            MinX = head.MinX, MinY = head.MaxY + 2f,
            MaxX = head.MaxX, MaxY = head.MaxY + 2f + LangMenuRows * LangMenuItemH,
        };
    }

    private RectF LangMenuItemRect(int slot)
    {
        var list = LangMenuItemsRect();
        float y = list.MinY + slot * LangMenuItemH;
        return new RectF { MinX = list.MinX, MinY = y, MaxX = list.MaxX, MaxY = y + LangMenuItemH };
    }

    private RectF LangMenuFooterRect()
    {
        var r = LangMenuRect();
        return new RectF
        {
            MinX = r.MinX + LangMenuPad, MinY = r.MaxY - LangMenuPad - LangMenuFootH,
            MaxX = r.MaxX - LangMenuPad, MaxY = r.MaxY - LangMenuPad,
        };
    }

    /// <summary>命中：MoreHitLangItem+绝对下标 / MoreHitLangApply / MoreHitInside（菜单内空白）/ None。</summary>
    private int LangMenuHit(float x, float y)
    {
        if (!_langMenuOpen) return MoreHitNone;
        if (!LangMenuRect().Contains(x, y)) return MoreHitNone;
        if (LangMenuApplyEnabled && LangMenuFooterRect().Contains(x, y)) return MoreHitLangApply;
        var list = LangMenuItemsRect();
        if (list.Contains(x, y))
        {
            int slot = (int)((y - list.MinY) / LangMenuItemH);
            int i = LangMenuScrollTop + slot;
            if (slot >= 0 && slot < LangMenuRows && i >= 0 && i < LangMenuCount)
                return MoreHitLangItem + i;
        }
        return MoreHitInside;
    }

    private void OpenLangMenu()
    {
        var codes = Loc.PrefCodes;
        string pending = PendingLangPref;
        int sel = -1;
        for (int i = 0; i < codes.Count; i++)
            if (string.Equals(Loc.DisplayName(codes[i]), Loc.DisplayName(pending), StringComparison.Ordinal))
            { sel = i; break; }
        _langMenuScroll = sel >= 0
            ? Math.Clamp(sel - LangMenuRows + 1, 0, LangMenuScrollMax)
            : 0;
        _langMenuOpen = true;
        _langMenuHover = -1;
        _langMenuPress = -1;
        _langMenuDragged = false;
        _moreHover = MoreHitNone;
        _morePress = -1;
        HideTip();
        CancelTipHold();
        Invalidate();
    }

    private void CloseLangMenu()
    {
        if (!_langMenuOpen) return;
        _langMenuOpen = false;
        _langMenuHover = -1;
        _langMenuPress = -1;
        _langMenuDragged = false;
        Invalidate();
    }

    private void SelectLangMenu(int index)
    {
        var codes = Loc.PrefCodes;
        if (index < 0 || index >= codes.Count) return;
        // **system 也显式写下来**：否则“跟随系统”会被安装器语言顶掉（见 Engine 的优先级）。
        _host?.SetPref("lang", codes[index]);
        Invalidate();
    }

    private void ScrollLangMenu(int deltaRows)
    {
        int next = Math.Clamp(_langMenuScroll + deltaRows, 0, LangMenuScrollMax);
        if (next == _langMenuScroll) return;
        _langMenuScroll = next;
        Invalidate();
    }

    private void ApplyLangMenu()
    {
        if (!LangMenuApplyEnabled) return;
        CloseLangMenu();
        _host?.Commands.RestartForLanguage();
    }

    /// <summary>历史清理那一行右侧的值。</summary>
    private string HistoryDaysName(string pref) => pref switch
    {
        "90" => "90 天", "30" => "30 天", "7" => "7 天", _ => "永久",
    };

    // ---- 命中 ----------------------------------------------------------------

    private int MoreHitAt(float x, float y)
    {
        if (!_moreOpen) return MoreHitNone;
        // 下拉可以伸出设置面板，必须先于“面板外 = 空白”判断。
        if (_langMenuOpen)
        {
            int lang = LangMenuHit(x, y);
            if (lang != MoreHitNone) return lang;
        }
        if (!MoreRect().Contains(x, y)) return MoreHitNone;
        if (MoreCloseRect().Contains(x, y)) return MoreHitClose;
        if (_morePage != MorePage.Home && MoreBackRect().Contains(x, y)) return MoreHitBack;

        // A3.3：页面内容命中交给当前 IPage；返回值仍是旧整数码（input 层暂时不改）。
        var pageHit = _pageHost?.Current?.HitTest(x, y) ?? PageHit.None;
        return pageHit.Kind == PageHitKind.None ? MoreHitInside : pageHit.Index;
    }

    // ---- 稳定命中目标（A0，2026-11-08）------------------------------------
    // 先做“旧整数 → 稳定 HitTarget”的适配层：几何仍然只有 MoreHitAt 一份，
    // 新代码用 ActionId，旧代码继续用整数；双跑通过后再把 MoreHitAt 删掉。

    internal HitTarget MoreHitTargetAt(float x, float y)
        => HitTargetFromLegacy(MoreHitAt(x, y));

    private HitTarget HitTargetFromLegacy(int hit)
    {
        if (hit == MoreHitNone) return HitTarget.None;
        if (hit == MoreHitInside) return new HitTarget(HitKind.Inside, "page.surface");
        if (hit == MoreHitClose) return new HitTarget(HitKind.PageClose, "page.close");
        if (hit == MoreHitBack) return new HitTarget(HitKind.PageBack, "page.back");

        if (_morePage == MorePage.Help
            && hit >= HelpHitTabBase && hit < HelpHitTabBase + HelpTabCount)
        {
            int i = hit - HelpHitTabBase;
            return new HitTarget(HitKind.HelpTab, $"page.help.tab{i}", i);
        }

        if (_morePage == MorePage.Settings
            && hit >= MoreHitProfile && hit < MoreHitProfile + 3)
        {
            int i = hit - MoreHitProfile;
            return new HitTarget(HitKind.SettingsProfile, $"settings.toolbar.profile{i}", i);
        }

        if (_morePage == MorePage.Settings
            && hit >= MoreHitChip && hit < MoreHitChip + Cells.Length)
        {
            int cell = hit - MoreHitChip;
            return new HitTarget(HitKind.SettingsChip, $"settings.toolbar.chip{cell}", cell);
        }

        if (hit >= MoreHitRow && hit < MoreHitRow + Rows.Length)
        {
            int i = hit - MoreHitRow;
            return new HitTarget(HitKind.SettingsRow, RowActionId(Rows[i].Kind), i);
        }

        if (hit >= MoreHitTile && hit < MoreHitTile + HubTileTotal)
        {
            int code = hit - MoreHitTile;
            return new HitTarget(HitKind.HubTile, HubActionId(code), code);
        }

        if (hit >= MoreHitBottom && hit < MoreHitBottom + MoreBottomTiles.Length)
        {
            int i = hit - MoreHitBottom;
            return new HitTarget(HitKind.BottomTile, BottomActionId(i), i);
        }

        if (hit >= MoreHitSettingsNav && hit < MoreHitSettingsNav + SettingsCatNames.Length)
        {
            int i = hit - MoreHitSettingsNav;
            return new HitTarget(HitKind.SettingsNav, SettingsNavActionId(i), i);
        }

        if (hit == MoreHitSettingsScroll)
            return new HitTarget(HitKind.SettingsScroll, "more.settings.scroll");

        if (hit >= MoreHitLangItem && hit < MoreHitLangItem + LangMenuCount)
        {
            int i = hit - MoreHitLangItem;
            return new HitTarget(HitKind.PopupItem, $"settings.general.language.item{i}", i);
        }
        if (hit == MoreHitLangApply)
            return new HitTarget(HitKind.PopupItem, "settings.general.language.apply");

        if (hit >= ApiHitProvider && hit <= ApiHitKeyLink)
        {
            string id = hit switch
            {
                ApiHitProvider => "api.provider",
                ApiHitBase => "api.base",
                ApiHitKey => "api.key",
                ApiHitModel => "api.model",
                ApiHitFetch => "api.fetchModels",
                ApiHitSave => "api.save",
                ApiHitCancel => "api.cancel",
                ApiHitDelete => "api.delete",
                ApiHitKeyLink => "api.keyUrl",
                _ => "api.control",
            };
            return new HitTarget(HitKind.PopupItem, id, hit);
        }
        if (hit == ApiHitProviderMenu)
            return new HitTarget(HitKind.PopupItem, "api.provider.menu", hit);
        if (hit >= ApiHitProviderItemBase)
            return new HitTarget(HitKind.PopupItem, $"api.provider.item{hit - ApiHitProviderItemBase}",
                                 hit - ApiHitProviderItemBase);

        string logId = LogActionId(hit);
        if (logId != null)
            return new HitTarget(HitKind.LogButton, logId);

        return new HitTarget(HitKind.Unknown, $"legacy.{hit}", hit);
    }

    private static string HubActionId(int code)
    {
        switch (code)
        {
            case 0: return "more.home.timer";
            case 1: return "more.home.rollCard";
            case 2: return "more.home.rollOne";
            case 3: return "more.home.save";
            case 4: return "more.home.open";
            case 5: return "more.home.replay";
            case 6: return "more.home.closeDocument";
            case 7: return "more.home.history";
        }
        if (code == AiTileCode) return "more.home.ai";
        if (code == HelpTileCode) return "more.home.help";
        return $"more.home.tile{code}";
    }

    private static string BottomActionId(int i) => i switch
    {
        0 => "more.bottom.settings",
        1 => "more.bottom.log",
        2 => "more.bottom.checkUpdate",
        3 => "more.bottom.restart",
        4 => "more.bottom.quit",
        _ => $"more.bottom.{i}",
    };

    private static string SettingsNavActionId(int i) => i switch
    {
        0 => "more.settings.nav.toolbar",
        1 => "more.settings.nav.general",
        2 => "more.settings.nav.writing",
        3 => "more.settings.nav.ink",
        4 => "more.settings.nav.ai",
        5 => "more.settings.nav.backup",
        6 => "more.settings.nav.about",
        _ => $"more.settings.nav.{i}",
    };

    private static string LogActionId(int hit) => hit switch
    {
        MoreHitLogNote => "more.log.note",
        MoreHitLogUpload => "more.log.upload",
        MoreHitLogExport => "more.log.export",
        MoreHitLogOlder => "more.log.older",
        MoreHitLogGroup => "more.log.group",
        MoreHitLogGroupUrl => "more.log.groupUrl",
        MoreHitLogQr => "more.log.qr",
        _ => null,
    };

    // ---- 开/关与换页 ---------------------------------------------------------

    /// <summary>打开面板（入口只有主条「…」那一格）。</summary>
    private void OpenMore()
    {
        // 模态互斥：更多面板打开前无条件关掉收藏/图形面板（未开时是空操作）。
        // 放在这里而不是只写在“更多”格里，保证以后从别处打开更多也不会两个模态叠着。
        _host?.Commands.CloseLibraryPanel();
        // 任务管理器可能在运行期间把自启关掉/打开；每次打开抽屉重读一次。
        _host?.Commands.RefreshAutoStart();
        _railHover = false;
        _rail.To(0f, Tokens.RailMs);

        _moreOpen = true;
        SetMorePage(MorePage.Home, push: false);   // 每次打开都回启动器（肌肉记忆：底栏永远同一处）
        _settingsScroll = 0f;
        _settingsScrollDrag = false;
        ResetSettingsTouch();
        ResetSettingsFocus();
        _moreHover = MoreHitNone;
        _morePress = -1;
        _hubHint = "";
        _more.Jump(0f);
        _more.To(1f, Tokens.MoreOpenMs);
        _moreH.Jump(MoreTargetH());
        Invalidate();
    }

    /// <summary>关闭面板。动画期间 QueryBounds 仍报整屏（遮罩要淡出去）。</summary>
    private void CloseMore()
    {
        if (!_moreOpen) return;
        CloseLangMenu();
        _host?.TextInput?.Commit();
        if (_host?.State.LogNoteEditing == true) _host.Commands.EndLogNoteEdit(true);
        _moreOpen = false;
        _moreHover = MoreHitNone;
        _morePress = -1;
        _settingsScrollDrag = false;
        ResetSettingsTouch();
        ResetSettingsFocus();
        HideTip();              // 面板里的提示跟着收（触摸长按候选也作废）
        CancelTipHold();
        _more.To(0f, Tokens.MoreCloseMs);
        Invalidate();
    }

    /// <summary>关闭动画结束后再清 PageHost，避免淡出时闪回 Home 内容。</summary>
    private void FinishMoreCloseIfDone()
    {
        if (_moreOpen || _more.Running) return;
        if (_pageHost == null || _pageHost.Count == 0) return;
        _pageHost.Close(animate: false);
    }

    private void UpdateMoreHeight()
    {
        float target = MoreTargetH();
        if (MathF.Abs(_moreH.Value - target) > 0.5f) _moreH.To(target, 167);
    }

    /// <summary>画面板：遮罩（全屏）＋ 卡片 ＋ 按页切内容。**最后画**，压住一切。</summary>
    private void DrawMorePanel(ID2D1DeviceContext ctx)
    {
        float a = Math.Clamp(_more.Value, 0f, 1f);
        if (a <= 0.003f) return;
        UpdateMoreHeight();

        var scrim = _dark ? Tokens.ScrimDark : Tokens.ScrimLight;
        ctx.FillRectangle(
            new Vortice.RawRectF(_screen.MinX, _screen.MinY, _screen.MaxX, _screen.MaxY),
            Brush(ctx, new Color4(scrim.R, scrim.G, scrim.B, QA(scrim.A * a))));

        var r = MoreRect();
        var saved = ctx.Transform;
        float k = 0.97f + 0.03f * a;
        var c = new Vector2((r.MinX + r.MaxX) * 0.5f, (r.MinY + r.MaxY) * 0.5f);
        ctx.Transform = Matrix3x2.CreateTranslation(-c.X, -c.Y)
                      * Matrix3x2.CreateScale(k, k)
                      * Matrix3x2.CreateTranslation(c.X, c.Y)
                      * saved;
        try { DrawMorePanelCore(ctx); }
        finally { ctx.Transform = saved; }
    }

    private void DrawMorePanelCore(ID2D1DeviceContext ctx)
    {
        var r = MoreRect();
        var card = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 20f, 20f);
        ctx.FillRoundedRectangle(card, Brush(ctx, _dark ? Tokens.PopoverPanelDark : Tokens.PopoverPanelLight));
        ctx.DrawRoundedRectangle(card, Brush(ctx, BorderCol), 1f);

        // 标题（启动器「更多」/ 子页「设置」「使用说明」）＋ 返回 ＋ 关闭
        string title = _morePage switch
        {
            MorePage.Home => "更多",
            MorePage.Settings => "设置",
            MorePage.Log => "日志与反馈",
            MorePage.Api => "AI 服务",
            _ => "使用说明",
        };
        bool showBack = _morePage != MorePage.Home;
        _widgets.Text(ctx, title,
                      new RectF { MinX = MoreHeaderRect().MinX + (showBack ? MoreCloseSize + 8f : 0f),
                                  MinY = MoreHeaderRect().MinY, MaxX = MoreHeaderRect().MaxX, MaxY = MoreHeaderRect().MaxY },
                      17f, Brush(ctx, InkCol), center: false);
        if (showBack)
        {
            _widgets.Text(ctx, "‹", MoreBackRect(), 24f,
                          Brush(ctx, _moreHover == MoreHitBack ? InkCol : MutedCol));
        }
        _widgets.Text(ctx, "✕", MoreCloseRect(), 15f,
                      Brush(ctx, _moreHover == MoreHitClose ? InkCol : MutedCol));

        if (_pageHost?.Current != null)
            _pageHost.Render(ctx);

        // 下拉必须压在页面内容之上（它可能伸出设置面板）。
        if (_langMenuOpen) DrawLangMenu(ctx);
    }

    /// <summary>
    /// 语言下拉（参考 WinUI 3 ComboBox flyout）：表头 + 11 项（8 行可见，可滚）+ 底部重启按钮。
    /// 当前项用强调色 + 左侧竖条 + 对勾，别只靠颜色区分。
    /// </summary>
    private void DrawLangMenu(ID2D1DeviceContext ctx)
    {
        var r = LangMenuRect();
        var shadow = new RoundedRectangle(
            new Vortice.RawRectF(r.MinX + 1f, r.MinY + 5f, r.MaxX + 1f, r.MaxY + 5f), 12f, 12f);
        ctx.FillRoundedRectangle(shadow, Brush(ctx, new Color4(0f, 0f, 0f, _dark ? 0.34f : 0.14f)));

        var card = new RoundedRectangle(
            new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 12f, 12f);
        // 下拉是浮在设置页上的，必须**完全不透明**：PopoverPanel 的 0.98 alpha 会让
        // 底下的蓝色档位按钮透出来，看着像菜单里多了一块高亮。
        var panelCol = _dark ? Tokens.PopoverPanelDark : Tokens.PopoverPanelLight;
        ctx.FillRoundedRectangle(card, Brush(ctx, new Color4(panelCol.R, panelCol.G, panelCol.B, 1f)));
        ctx.DrawRoundedRectangle(card, Brush(ctx, BorderCol), 1f);

        var sep = new Color4(InkCol.R, InkCol.G, InkCol.B, 0.10f);
        var head = LangMenuHeaderRect();
        _widgets.TextLeftMiddle(ctx, "语言",
            new RectF { MinX = head.MinX + 10f, MinY = head.MinY, MaxX = head.MaxX - 10f, MaxY = head.MaxY },
            12f, Brush(ctx, MutedCol));
        ctx.DrawLine(new Vector2(r.MinX + LangMenuPad, head.MaxY + 1f),
                     new Vector2(r.MaxX - LangMenuPad, head.MaxY + 1f), Brush(ctx, sep), 1f);

        var codes = Loc.PrefCodes;
        string pending = PendingLangPref;
        for (int slot = 0; slot < LangMenuRows; slot++)
        {
            int i = LangMenuScrollTop + slot;
            if (i < 0 || i >= codes.Count) break;
            var ir = LangMenuItemRect(slot);
            bool selected = string.Equals(Loc.DisplayName(codes[i]), Loc.DisplayName(pending),
                                          StringComparison.Ordinal);
            if (_langMenuHover == i)
                ctx.FillRoundedRectangle(
                    new RoundedRectangle(new Vortice.RawRectF(ir.MinX + 2f, ir.MinY + 1f,
                                                             ir.MaxX - 2f, ir.MaxY - 1f), 6f, 6f),
                    Brush(ctx, HoverCol));
            if (selected)
                ctx.FillRoundedRectangle(
                    new RoundedRectangle(new Vortice.RawRectF(ir.MinX + 2f, ir.MinY + 9f,
                                                             ir.MinX + 4f, ir.MaxY - 9f), 1f, 1f),
                    Brush(ctx, Tokens.Accent));

            string name = codes[i] == Loc.SystemCode ? "跟随系统" : Loc.DisplayName(codes[i]);
            _widgets.TextLeftMiddle(ctx, name,
                new RectF { MinX = ir.MinX + 12f, MinY = ir.MinY, MaxX = ir.MaxX - 34f, MaxY = ir.MaxY },
                12.5f, Brush(ctx, selected ? Tokens.Accent : InkCol));

            if (selected)
            {
                // 对勾用两笔画：不依赖字体里有没有 ✓（和 ✕ 一样走线条更稳）。
                float cx = ir.MaxX - 16f, cy = (ir.MinY + ir.MaxY) * 0.5f;
                var pen = Brush(ctx, Tokens.Accent);
                ctx.DrawLine(new Vector2(cx - 5f, cy), new Vector2(cx - 1.5f, cy + 3.5f), pen, 1.6f);
                ctx.DrawLine(new Vector2(cx - 1.5f, cy + 3.5f), new Vector2(cx + 5f, cy - 4f), pen, 1.6f);
            }
        }

        // 11 项只露 8 行：右侧细滚动条（滚轮 / 按住上下拖都能滚）。
        if (LangMenuScrollMax > 0)
        {
            var list = LangMenuItemsRect();
            float trackX = r.MaxX - 5f;
            float top = list.MinY + 2f, bottom = list.MinY + LangMenuRows * LangMenuItemH - 2f;
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(trackX, top, trackX + 2f, bottom), 1f, 1f),
                Brush(ctx, new Color4(InkCol.R, InkCol.G, InkCol.B, 0.10f)));
            float thumbH = MathF.Max(18f, (bottom - top) * LangMenuRows / LangMenuCount);
            float t = (float)LangMenuScrollTop / LangMenuScrollMax;
            float y0 = top + (bottom - top - thumbH) * t;
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(trackX - 0.5f, y0, trackX + 2.5f, y0 + thumbH), 1.5f, 1.5f),
                Brush(ctx, new Color4(InkCol.R, InkCol.G, InkCol.B, 0.28f)));
        }

        var foot = LangMenuFooterRect();
        ctx.DrawLine(new Vector2(r.MinX + LangMenuPad, foot.MinY - 1f),
                     new Vector2(r.MaxX - LangMenuPad, foot.MinY - 1f), Brush(ctx, sep), 1f);
        bool on = LangMenuApplyEnabled;
        if (on && _langMenuHover == MoreHitLangApply)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(foot.MinX + 2f, foot.MinY + 1f,
                                                         foot.MaxX - 2f, foot.MaxY - 1f), 6f, 6f),
                Brush(ctx, HoverCol));
        _widgets.Text(ctx, "重启以应用", foot, 12.5f,
                      Brush(ctx, on ? Tokens.Accent : MutedCol), center: true);
    }

    // ---- 启动器绘制 ----------------------------------------------------------

    /// <summary>启动器格子总数（各分组相加；「加格子忘了别处」用这一条兜住）。</summary>
    private int HubTileTotal
    {
        get
        {
            int n = 0;
            for (int s = 0; s < 2; s++) n += HubCount(s);
            return n;
        }
    }

    /// <summary>
    /// 启动器格子的表：`Hint` **不再画在格子里**（2026-10-02 第二批：搬进悬停/长按提示），
    /// 但它是提示的**唯一文案来源**——绘制、命中、提示都读这一份。
    /// 格子编号：课堂 0..2（计时 / 点名 / 随机一人）、AI 9（AI 出题）、墨迹 3..7（保存… / 打开… /
    /// 墨迹回放 / 关闭文档 / 历史板书——2026-10-16 合并后）。
    /// </summary>
    private (string Label, string Hint, bool Enabled, bool Danger) HubTileInfo(int code) => code switch
    {
        0 => ("计时器", "倒计时 / 正计时 / 秒表；卡片可拖动、双击放大", true, false),
        1 => ("点名", "全名单，抽过的不重复；范围可设", true, false),
        2 => ("随机一人", "自动抽一个，1.5 秒自动关", true, false),
        3 => ("保存…", "整份板书存成 .inkb，或存成图片（jpg / png，好发微信）", _host != null && !_host.State.PptMode && _host.State.StrokeCount > 0, false),
        4 => ("打开…", "打开板书(.inkb) / 图片 / PDF / PPT；图片可多选", _host != null && !_host.State.PptMode, false),
        5 => ("墨迹回放", "把这一屏的板书重演一遍（只读，不动板书）", _host != null && (_host.State.ReplayActive || _host.State.StrokeCount > 0), false),
        6 => ("关闭文档",
              _host != null && _host.State.DocOpen
                  ? $"关掉《{_host.State.DocInfo}》；批注留在画布上"
                  : "关掉当前文档；批注留在画布上",
              _host != null && _host.State.DocOpen, false),
        // 历史板书（2026-10-14 加）：引擎自动留的板书画布快照（不含截图），这里是找回入口。
        7 => ("历史板书",
              "找回以前写在**白板**上的墨迹（自动留存，停手时写；不含截图）",
              _host != null && !_host.State.PptMode && _host.State.HistoryCount > 0, false),
        AiTileCode => ("AI 出题",
              AiEntryEnabled
                  ? "语音/文字出题；1A 先出示例题卡（不联网）"
                  : "开发中：AI 出题先暂停，等新想法后再开放",
              AiEntryEnabled, false),
        HelpTileCode => ("使用说明",
              "4 步上手 / 手势 / 快捷键；F1 也能打开",
              true, false),
        _ => ("", "", false, false),
    };

    /// <summary>
    /// 底栏五格的唯一一份表（2026-10-02 第二批：小字搬进提示，格子上只留标题；
    /// 2026-10-17 加「日志」——放在「设置」后面，上传/导出合并成它一个入口）。
    /// 绘制、命中、提示都读它。
    /// </summary>
    private static readonly (string Label, string Hint, bool Danger)[] MoreBottomTiles =
    {
        ("设置", "外观 / 书写 / 墨迹 / 工具条，都在里面", false),
        ("日志", "上传运行日志给开发；加群反馈也在这里", false),
        ("检查更新", "当前 v{0}；有新版本会提示，也可以直接应用更新", false),
        ("重启软件", "像电脑重启：不恢复本次板书（自动存档还在）", false),
        ("退出", "关掉批注（会先把键位落盘）", true),
    };

    /// <summary>画一个启动器格子：**只有标题**（小字 2026-10-02 搬进了提示）、
    /// 悬停/长按由 FullUi 的提示系统补。</summary>
    private void DrawHubTile(ID2D1DeviceContext ctx, in RectF t, string label, bool enabled, bool danger, int hitCode)
    {
        bool help = hitCode == MoreHitTile + HelpTileCode;
        var rr = new RoundedRectangle(new Vortice.RawRectF(t.MinX, t.MinY, t.MaxX, t.MaxY), 12f, 12f);
        if (_moreHover == hitCode) ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
        ctx.DrawRoundedRectangle(rr, Brush(ctx, help ? Tokens.Accent : BorderCol), help ? 1.5f : 1f);
        Color4 ink = !enabled ? new Color4(InkCol.R, InkCol.G, InkCol.B, 0.35f)
                   : danger ? new Color4(0.85f, 0.22f, 0.22f, 1f)
                   : InkCol;
        // 只留「使用说明」四个字；蓝色的强调边框负责让它在课堂工具里一眼可见。
        _widgets.Text(ctx, label, t, 14f, Brush(ctx, ink));
    }

    /// <summary>日志页右侧的二维码卡片：白底（扫码要对比度）、图 + 一行小字。</summary>
    private void DrawLogQrCard(ID2D1DeviceContext ctx)
    {
        var card = LogQrCardRect();
        var rr = new RoundedRectangle(
            new Vortice.RawRectF(card.MinX, card.MinY, card.MaxX, card.MaxY), 12f, 12f);
        ctx.FillRoundedRectangle(rr, Brush(ctx, new Color4(1f, 1f, 1f, 1f)));
        ctx.DrawRoundedRectangle(rr,
            Brush(ctx, _moreHover == MoreHitLogQr ? Tokens.Accent : BorderCol),
            _moreHover == MoreHitLogQr ? 1.6f : 1f);

        EnsureQrBitmap(ctx);
        float pad = 10f;
        float capH = 22f;
        float side = MathF.Min(card.MaxX - card.MinX - pad * 2f,
                               card.MaxY - card.MinY - pad * 2f - capH);
        side = MathF.Max(40f, side);
        float cx = (card.MinX + card.MaxX) * 0.5f;
        float x0 = cx - side * 0.5f, y0 = card.MinY + pad;
        if (_qrBmp != null)
            ctx.DrawBitmap(_qrBmp, new Vortice.RawRectF(x0, y0, x0 + side, y0 + side),
                           1f, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
        else
            _widgets.Text(ctx, "二维码缺失", new RectF { MinX = card.MinX, MinY = y0,
                                                        MaxX = card.MaxX, MaxY = y0 + side },
                          11f, Brush(ctx, new Color4(0.85f, 0.22f, 0.22f, 1f)));

        _widgets.Text(ctx, $"扫码加群 {ProductInfo.FeedbackQQGroup}",
            new RectF { MinX = card.MinX + 6f, MinY = y0 + side + 2f,
                        MaxX = card.MaxX - 6f, MaxY = y0 + side + 2f + capH },
            10.5f, Brush(ctx, new Color4(0.25f, 0.28f, 0.33f, 1f)));
    }

    // 二维码位图（2026-11-03）：只建一次；899×899 的 PNG 换成 GPU 位图约 3.2MB。
    private ID2D1Bitmap _qrBmp;
    private bool _qrBmpTried;

    private void EnsureQrBitmap(ID2D1DeviceContext ctx)
    {
        if (_qrBmp != null || _qrBmpTried) return;
        _qrBmpTried = true;
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "qq-group-qr.png");
        if (!UiImageBytes.TryLoad(path, out int w, out int h, out byte[] bgra)) return;
        var props = new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            _qrBmp = ctx.CreateBitmap(new SizeI(w, h), handle.AddrOfPinnedObject(),
                                      (uint)(w * 4), props);
        }
        catch (Exception ex)
        {
            Console.WriteLine("二维码位图创建失败：" + ex.Message);
        }
        finally { handle.Free(); }
    }

    private void DrawMoreGroupHead(ID2D1DeviceContext ctx, RectF r, string label)
        => _widgets.Text(ctx, label, r, 12.5f, Brush(ctx, MutedCol), center: false);

    // ---- 设置子页绘制 --------------------------------------------------------

    private void DrawSettingsScrollbar(ID2D1DeviceContext ctx)
    {
        if (SettingsScrollMax() <= 0.5f) return;
        var bar = SettingsScrollbarRect();
        var track = new RoundedRectangle(new Vortice.RawRectF(bar.MinX + 1.5f, bar.MinY, bar.MaxX - 1.5f, bar.MaxY), 2f, 2f);
        ctx.FillRoundedRectangle(track, Brush(ctx, new Color4(BorderCol.R, BorderCol.G, BorderCol.B, 0.25f)));
        var thumb = SettingsScrollThumbRect();
        if (thumb.IsEmpty) return;
        var tr = new RoundedRectangle(new Vortice.RawRectF(thumb.MinX, thumb.MinY, thumb.MaxX, thumb.MaxY), 3f, 3f);
        ctx.FillRoundedRectangle(tr, Brush(ctx, new Color4(Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, 0.55f)));
    }

    private void DrawSettingsNav(ID2D1DeviceContext ctx)
    {
        var selBg = new Color4(Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, 0.08f);
        for (int i = 0; i < SettingsCatNames.Length; i++)
        {
            var r = SettingsNavItemRect(i);
            bool active = (int)_settingsCat == i;
            var rr = new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f);
            if (active)
            {
                ctx.FillRoundedRectangle(rr, Brush(ctx, selBg));
                // 左侧强调竖条：比整块高饱和蓝更轻，和 WinUI NavigationView 一致。
                ctx.FillRoundedRectangle(
                    new RoundedRectangle(
                        new Vortice.RawRectF(r.MinX + 2f, r.MinY + 8f, r.MinX + 5f, r.MaxY - 8f), 1.5f, 1.5f),
                    Brush(ctx, Tokens.Accent));
            }
            else if (_moreHover == MoreHitSettingsNav + i)
            {
                ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
            }
            _widgets.TextLeftMiddle(ctx, SettingsCatNames[i],
                new RectF
                {
                    MinX = r.MinX + SettingsNavTextInset, MinY = r.MinY,
                    MaxX = r.MaxX - 8f, MaxY = r.MaxY,
                },
                12.5f, Brush(ctx, active ? Tokens.Accent : InkCol));
        }
    }

    /// <summary>
    /// 自定义档里**核心格**（鼠标、更多）右上角的小锁：它们不能取消钉住，
    /// 不画锁的话点一下没反应会被当成 bug。锁用线条画，不依赖图标资源。
    /// </summary>
    private void DrawLockBadge(ID2D1DeviceContext ctx, RectF chip)
    {
        var c = new Color4(InkCol.R, InkCol.G, InkCol.B, 0.55f);
        float x = chip.MaxX - 15f, y = chip.MinY + 5f;
        // 先画锁梁（半圆），再用锁身盖住下半截。
        ctx.DrawEllipse(new Ellipse(new Vector2(x + 4.5f, y + 3.5f), 3.2f, 3.6f),
                        Brush(ctx, c), 1.2f);
        ctx.FillRoundedRectangle(
            new RoundedRectangle(new Vortice.RawRectF(x, y + 4f, x + 9f, y + 13f), 2f, 2f),
            Brush(ctx, c));
    }

    private void DrawMoreRow(ID2D1DeviceContext ctx, int i)
    {
        var r = MoreRowRect(i);
        if (_moreHover == MoreHitRow + i)
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(r.MinX, r.MinY, r.MaxX, r.MaxY), 8f, 8f),
                Brush(ctx, HoverCol));

        Color4 ink = Rows[i].Dangerous ? new Color4(0.85f, 0.22f, 0.22f, 1f) : InkCol;

        // 右侧的"值"（历史清理 / 界面大小）：**和标签同一行**、按实测宽度贴右。
        // ⚠ 画它必须 `center: false`——Widgets.Text 的居中参数是**两轴都居中**，
        //   值垂直也居中、而标签是顶部对齐，看起来就像"掉到第二行"
        //   （用户 2026-10-10 指出，顺手把"历史清理"的"永久"也归位）。
        string value = Rows[i].Kind switch
        {
            Row.HistoryDays => HistoryDaysName(_host.GetPref("historyDays")),
            Row.UiSize => UiScalePresets.Name(_host.UiScale),
            Row.Language => Loc.DisplayName(PendingLangPref),
            Row.TouchMode => _host.State.TouchGesturesOn ? "手势" : "单指",
            Row.AiApi => "管理…",
            _ => null,
        };
        // 语言这一行做成“下拉框”的样子：值右边带 ▾，和常见设置页一致。
        float chevronW = Rows[i].Kind == Row.Language ? 14f : 0f;
        float valueW = value != null ? _widgets.Measure(value, 12.5f) : 0f;

        var label = new RectF
        {
            MinX = r.MinX + 4f, MinY = r.MinY,
            MaxX = r.MaxX - (IsToggleRow(i) ? MoreSwitchW + 10f
                             : value != null ? valueW + 10f + chevronW : 4f),
            MaxY = r.MaxY,
        };
        // 2026-10-02 第二批：行下那行 11px 小灰字（Hint）**不再画**——搬进悬停/长按提示；
        // 行上只留标签，整行一条线，清爽。Hint 仍是提示的文案来源（Rows 表那一份）。
        // **垂直居中**（TextLeftMiddle）：右侧开关是居中的，标签贴顶就会差半行
        //（用户 2026-10-10 报的"不在一条线上"）。
        _widgets.TextLeftMiddle(ctx, Rows[i].Label, label, 13f, Brush(ctx, ink));
        if (IsToggleRow(i)) DrawSwitch(ctx, MoreSwitchRect(i), IsOn(i));
        else if (value != null)
        {
            float right = r.MaxX - 4f - chevronW;
            _widgets.TextLeftMiddle(ctx, value,
                          new RectF { MinX = right - valueW, MinY = r.MinY, MaxX = right, MaxY = r.MaxY },
                          12.5f, Brush(ctx, InkCol));
            if (chevronW > 0f)
                _widgets.Text(ctx, "▾",
                              new RectF { MinX = right, MinY = r.MinY, MaxX = r.MaxX - 2f, MaxY = r.MaxY },
                              10f, Brush(ctx, MutedCol), center: true);
        }
    }

    private void ActivateHub(int code)
    {
        switch (code)
        {
            case 0: CloseMore(); _host.Commands.OpenTimerCard(); break;
            case 1: CloseMore(); _host.Commands.OpenRollCard(); break;
            case 2: CloseMore(); _host.Commands.OpenRollOne(); break;
            case 3: CloseMore(); _host.Commands.SaveBoardAny(); break;    // 保存…（2026-10-16 合并入口）
            case 4: CloseMore(); _host.Commands.OpenBoardAny(); break;    // 打开…（2026-10-16 合并入口）
            case 5:
                CloseMore();
                if (_host.State.ReplayActive) _host.Commands.StopReplay();
                else _host.Commands.StartReplay();
                break;
            case 6:                                   // 关闭文档
                CloseMore();
                _host.Commands.CloseDocument();
                break;
            case 7:                                   // 历史板书（2026-10-14）
                CloseMore();
                _host.Commands.OpenInkHistory();
                break;
            case AiTileCode:                          // AI 出题（开发中，入口置灰）
                if (!AiEntryEnabled)
                {
                    // 置灰格不执行功能；留在「更多」里，把原因写进状态行。
                    // 触摸没有悬停，点一下也能看到“开发中”，不用去猜为什么是灰的。
                    _hubHint = "AI 出题开发中，暂未开放";
                    break;
                }
                CloseMore();
                OpenAiPanel();
                break;
            case HelpTileCode:                        // 使用说明（2026-10-19）
                // 和「设置」一样留在 More 面板里换页，不另开一层——DPI、遮罩、
                // 命中、居中都沿用 More，200% 缩放下不会再错位。
                SetMorePage(MorePage.Help, push: true);
                _moreHover = MoreHitNone;
                _morePress = -1;
                _helpScroll = 0f;
                _moreH.To(MoreHelpH(), Tokens.MoreOpenMs);
                Invalidate();
                break;
        }
        Invalidate();
    }

    private void ActivateLog(int hit)
    {
        var st = _host?.State;
        if (st == null) return;

        if (hit == MoreHitLogOlder)
        {
            _logIncludeOlder = !_logIncludeOlder;
            Invalidate();
            return;
        }

        if (hit == MoreHitLogNote)
        {
            if (_host.State.LogNoteEditing)
            {
                _host.Commands.EndLogNoteEdit(true);
                return;
            }
            var r = LogNoteRect();
            _hubHint = "";
            _host.Commands.BeginLogNoteEdit(r.MinX, r.MinY, r.MaxX - r.MinX, r.MaxY - r.MinY);
            return;
        }

        if (_host.State.LogNoteEditing) _host.Commands.EndLogNoteEdit(true);
        string note = _host.State.LogNote ?? "";

        if (hit == MoreHitLogUpload)
        {
            _hubHint = "";
            _host.Commands.UploadLogs(note, _logIncludeOlder);
            Invalidate();
        }
        else if (hit == MoreHitLogExport)
        {
            _hubHint = "";
            _host.Commands.ExportLogs(note, _logIncludeOlder);
            Invalidate();
        }
        else if (hit == MoreHitLogGroup)
        {
            // 2026-11-03：点一下复制群号——触摸屏不用手抄。
            _host.Commands.CopyText(ProductInfo.FeedbackQQGroup);
            _hubHint = $"群号已复制：{ProductInfo.FeedbackQQGroup}";
            Invalidate();
        }
        else if (hit == MoreHitLogGroupUrl)
        {
            // 加群短链：手机点开能直接唤起 QQ；大屏上复制到微信 / 手机更实际。
            _host.Commands.CopyText(ProductInfo.FeedbackQQUrl);
            _hubHint = "加群链接已复制，粘到 QQ / 微信里就能打开";
            Invalidate();
        }
        else if (hit == MoreHitLogQr)
        {
            // 整屏二维码：学生用手机扫，老师不用打字。
            _hubHint = "";
            CloseMore();
            _host.Commands.ShowFeedbackQr();
        }
    }

    private void ActivateBottom(int i)
    {
        switch (i)
        {
            case 0: SetMorePage(MorePage.Settings, push: true); _settingsCat = SettingsCategory.Toolbar;
                _settingsScroll = 0f;
                _settingsScrollDrag = false;
                StopSettingsFling();
                _moreHover = MoreHitNone;
                _moreH.To(MoreSettingsH(), Tokens.MoreOpenMs);
                Invalidate(); break;
            // 「日志」（2026-11-02）：先打开「日志与反馈」页，让用户可选填一句问题；
            // 空着也能直接上传，上传/导出都在这一页完成，状态行就在下面。
            case 1:
                SetMorePage(MorePage.Log, push: true);
                _moreHover = MoreHitNone;
                _morePress = -1;
                _hubHint = "";
                _moreH.To(MoreLogH(), Tokens.MoreOpenMs);
                Invalidate();
                break;
            case 2:
                var stage = _host.State.UpdateStage;
                if (stage == UpdateStage.Available) _host.Commands.ApplyUpdate();
                else if (stage != UpdateStage.Downloading && stage != UpdateStage.Ready)
                    _host.Commands.CheckUpdate();
                Invalidate();
                break;
            case 3: _host.Commands.Restart(); break;   // 像电脑重启：不写会话暂存，板书不接回来（引擎语义见 RestartFromUi）
            case 4: _host.Commands.Quit(); break;
        }
    }


    // ---- 贴边隐藏 -----------------------------------------------------------

    /// <summary>整块（主条 ＋ 上带）**未平移**的矩形。</summary>
    private RectF UnionRect() => PanelRect();

    private static RectF Union(in RectF a, in RectF b) => new()
    {
        MinX = Math.Min(a.MinX, b.MinX), MinY = Math.Min(a.MinY, b.MinY),
        MaxX = Math.Max(a.MaxX, b.MaxX), MaxY = Math.Max(a.MaxY, b.MaxY),
    };

    /// <summary>
    /// 贴边隐藏的位移：**只有"贴底"才藏**（用户 2026-09-30 定，两轮收紧后的现行口径）。
    ///
    /// 现行规则（2026-09-30 下午）：**面板底边离屏幕底边 ≤ `DockHideDistance`(10)** 才藏；
    /// 其他边（左右、上）一律不藏；连**工作区底（任务栏上沿）也不算**——
    /// 默认位置离屏幕底 52（任务栏 48 + 离任务栏 4），不触发；要藏就得把它拖到屏幕最底下。
    ///
    /// 沿革：最早四条边都判（收起态四边藏、展开态上下藏）→ 上午改成"只认底边、离工作区底 ≤40"
    /// → 下午用户点名"改成到屏幕底边 ≤10，其他不变"。
    ///
    /// 位移量照样按**屏幕**底边算：覆盖层是全屏置顶的，不推出屏幕就藏不掉
    ///（露头必须落在屏幕最底那 8 像素上，这也是用户要的"保持现状"）。
    ///
    /// 收不收由 `_peek` 那个状态机管（见 <see cref="UpdatePeek"/>）；这里只回答
    /// "该不该移动、往哪移"。
    /// </summary>
    /// <summary>这一帧的贴边位移（单帧快照；没有快照时现算一次，供 Layout/自检用）。</summary>
    private Vector2 Shift() => _frameShiftValid ? _frameShift : ComputeShift();

    private Vector2 ComputeShift()
    {
        // 面板开着（或开合动画中）一律不平移：模态面板必须纹丝不动，
        // 而且它此刻占的就是整块屏幕——跟着贴边位移会立刻被夹回，看着会跳。
        // 全屏模态（更多 / 使用说明页）不做贴边隐藏的平移：命中矩形和绘制都按屏幕坐标算。
        if (_moreOpen || _more.Running) return Vector2.Zero;
        if (!_hideEnabled) return Vector2.Zero;
        float t = 1f - _peek.Value;
        if (t <= 0.001f) return Vector2.Zero;

        var u = UnionRect();
        float h = u.MaxY - u.MinY;
        // 判定：面板底边离**屏幕**底边还有多少（负数 = 已经压过屏幕底，也算贴底）。
        float db = _screen.MaxY - u.MaxY;
        if (db > Tokens.DockHideDistance) return Vector2.Zero;      // 不够近：不藏

        // 位移量按**屏幕**底边（见上面那段论证）。
        float sb = _screen.MaxY - u.MaxY;
        return new Vector2(0, (h - Tokens.DockPeek + sb) * t);
    }

    /// <summary>
    /// 每帧更新"该不该收起来"。写得像个小状态机，因为规则就三条：
    /// 写字中不许动、指针在里面/正按着/「更多」面板开着不许收、刚离开要等一会儿（防误触）。
    /// </summary>
    /// <summary>
    /// 贴边隐藏："真的在等收合"才继续要帧。
    ///
    /// ⚠ **为什么不能直接用 `! _hoverInside && _peek.Value > 0`**（2026-10-07 修）：
    /// 那条在"**面板根本不会收**"的时候也成立，于是**永远要帧**——
    /// 最典型的一种：开了贴边隐藏但指针**还没碰过面板**（`_peekArmed == false`，
    /// 见 `UpdatePeek` 开头，此时面板按设计保持全开、`_peek.Value == 1`）。
    /// 实测后果：**待机 45.5 fps 持续渲染、单核 4~5%，永不停止**——
    /// 关掉这个开关或删掉设置就是 0 fps。笔记本上就是一直耗电、一直发热。
    ///
    /// 现在只有"armed 之后、且当前不处于 keepOpen、且没在写字"才算"在等收合"：
    /// 三种情况都会收敛到 0 —— 收（`_peek.Value → 0`）、或 keepOpen（`_peekPendingCollapse`
    /// 立刻变假）、或没 arm（同样为假）。
    /// </summary>
    private bool _peekPendingCollapse;

    private void UpdatePeek()
    {
        if (!_hideEnabled) { _peek.To(1f, 0); _peekPendingCollapse = false; return; }

        // **启动之后先不藏**（用户 2026-09-17："在贴边隐藏的情况下，刚启动软件的时候不要隐藏"）。
        //
        // 理由很实在：`_leftAtMs` 初值是负无穷，所以第一帧就满足"离开够久了"——
        // 一开机面板立刻收成屏幕底边那条 8 像素的露头（而且露头正好压在任务栏上），
        // 老师根本找不到它。现在改成：**先露着**，等指针碰过面板一次（`_peekArmed`）
        // 才允许"离开就收"——那时候他已经知道东西在哪儿了。
        if (!_peekArmed) { _peek.To(1f, 0); _peekPendingCollapse = false; return; }

        // **色板打开期间锁住贴边隐藏**（用户 2026-10-28："打开色板的时候如果主条贴边
        // 隐藏了，也会色板原地残留残影"）。色板是挂在工具带上的模态 popover：
        // 指针会移到卡片里（那里不在工具带的 hover 判定区），不锁的话 700ms 后整条
        // 面板带着色板一起下沉——画在新位置、脏区还按旧位置算，就在原地留下半张色板。
        // 这里**原地冻结**：Shift() 在色板打开期间保持这一刻的值，既不继续收，
        // 也不弹回全展开——工具条和色板的位置一动不动。
        if (_penPickerOpen)
        {
            _peek.Jump(_peek.Value);
            _peekPendingCollapse = false;
            _leftAtMs = _host.NowMs;
            return;
        }

        // **写字中：什么都不做**（原样返回），不是"强制展开"。
        //
        // 这条规则的本意一直是"写字的时候不许收"——老师写到屏幕边上，工具条不能自己缩回去。
        // 但它原来和下面 `keepOpen` 用的是同一句 `_peek.To(1f)`，于是"不许收"变成了"必须展开"：
        // **藏好的露头一落笔就被拽出来**（用户 2026-09-18 报的"贴边隐藏以后我一写它就取消贴边了"）。
        // 现在单独拎出来**原样返回**：本来只剩露头就继续露头，本来就开着就继续开着。
        if (_host.State.IsDrawing)
        {
            // 写字这段时间不算"离开"，写完还要等满 700 毫秒才允许收（防误触那条规则照旧）。
            _leftAtMs = _host.NowMs;
            _peekPendingCollapse = false;
            return;
        }

        bool keepOpen = _hoverInside || _press != -1 || _sliderDragging || _moreOpen
                        || _host.NowMs < _peekHoldUntilMs;
        _peekPendingCollapse = !keepOpen && _peek.Value > 0f;   // 只有这一档才需要继续要帧
        if (keepOpen)
        {
            _leftAtMs = _host.NowMs;
            _peek.To(1f, Tokens.SnapMs);
            return;
        }
        if (_host.NowMs - _leftAtMs < 700) return;               // 刚离开：再等等（防误触）
        _peek.To(0f, Tokens.SnapMs);
    }

    /// <summary>
    /// 贴边隐藏的"指针还在面板上吗"判定（**防抖迟滞**，2026-10-05 修"贴边翻页时闪跳"）：
    ///
    ///   · 完全收起时：只认露头那一条（指针扫过"面板本来的位置"不会凭空召唤它）；
    ///   · 展开/收起/动画期间：判定区取 **目标展开后的完整面板 ∪ 当前可见范围**。
    ///
    /// 为什么不能用"当前动画中的矩形"（原来就是）：面板一边长、判定区一边跟着跑，
    /// 指针会被"甩出"判定区 → 收起 → 露头又回到指针下面 → 再展开……一帧一帧地跳。
    /// 这和 Windows 任务栏自动隐藏的迟滞是同一个道理：**展开后的地盘先算进来**，
    /// 隐藏再慢一步（见 Tokens.RailHideDelayMs 与本类的 700ms 防误触）。
    /// </summary>
    private bool HoverInsideForPeek(float x, float y)
    {
        if (_peek.Value > 0.01f)
        {
            var full = UnionRect();
            // `UnionRect` 是**局部坐标**，指针事件是**屏幕坐标**：贴边下沉时两者差一个
            // Shift。以前这里没加，半收起状态下判定区整体偏上，指针明明在面板上
            // 也会被判成"离开"。
            var sh = Shift();
            full = new RectF
            {
                MinX = full.MinX + sh.X, MinY = full.MinY + sh.Y,
                MaxX = full.MaxX + sh.X, MaxY = full.MaxY + sh.Y,
            };
            float pad = Tokens.RailHoverPad;
            if (x >= full.MinX - pad && x <= full.MaxX + pad
                && y >= full.MinY - pad && y <= full.MaxY + pad) return true;
        }
        return QueryBounds().Contains(x, y);
    }

    // ---- 输入 ---------------------------------------------------------------

    public bool PointerDown(in UiPointerEvent e)
    {
        var p = Local(e);
        _settingsFocusVisible = false;        // 鼠标/触摸一按，键盘焦点环先让位（保留 FocusedId）
        _pressId = e.PointerId;                // 这一按是谁按的（长按期间挡别的指针）
        _press = -1;
        _dragging = false;
        _penPickerDismissUp = false;           // 新按下：上一次"关色板"的收尾不再延续
        HideTip();                         // 按下 = 新动作开始，提示先收
        CancelTipHold();                   // 上一次的长按候选也作废（新按下重新计时）
        // **"按下了"不等于"按在面板上"**。引擎会把**每一次**按下都转给界面
        // （界面有权决定吃不吃），所以这里必须自己判一次位置。
        // 无条件置 true 的后果（用户 2026-09-18 报的"贴边隐藏以后我一写它就取消贴边了"）：
        // 在画布上落笔 → 这里置 true、随后返回 false（这一笔归画布）→ 但 true 留了下来，
        // 而**写字期间引擎不转发 PointerMove**（那一笔已经归画布了），没人去把它改回来 →
        // `UpdatePeek` 一直以为"指针还在面板上" → 把藏好的露头重新拽出来。
        _hoverInside = HoverInsideForPeek(e.X, e.Y);
        _peekArmed = true;                 // 碰过了 → 之后允许"离开就收"
        _leftAtMs = _host.NowMs;
        _pressPos = p;
        if (_penPickerOpen)
        {
            PenPickerPointerDown(p.X, p.Y);
            return true;
        }
        if (_textFontMenuOpen)
        {
            int fi = HitTextFontCell(p.X, p.Y);
            if (fi >= 0) _host.Commands.SetTextFont(TextFontSizes[fi]);
            CloseTextFontMenu();
            return true;
        }
        // 拖动记的是**左上角**（和 RawAnchor 同一套语义：锚"带子的左端"）
        _dragStartAnchor = Anchor();

        // **按下也算"焦点在面板上"**：手写笔和触摸没有悬停那一段，
        // 只在 PointerMove 里更新 _railHover 的话，老师用笔点面板时设置条根本不会张开
        // （鼠标能张开、笔不能——这类"只在一种设备上坏"的 bug 最难查）。
        _railHover = !_host.State.PassThrough && BandVisible() && RailHoverZone().Contains(p.X, p.Y);
        _railHoverStamp = _host.NowMs;    // 位置更新打时间戳（写字后"离开一次"的判据用）
        bool touchLike = e.FromTouch || e.FromPen;   // 手指/笔接触（鼠标不参与长按）

        // 贴边隐藏的**触屏节奏**（2026-10-05）：手指/笔把面板按出来后，给一段"够得着"的
        // 停留时间（松手后别 0.7s 就收——手指还得再点一下工具；2500ms 是"看一眼点得中"的量级）。
        // 顺手把**设置条**也带出来：触摸没有悬停，点工具格 = 想看这个工具的设置条。
        if (touchLike && _hoverInside)
        {
            _peekHoldUntilMs = _host.NowMs + 2500;
            _railTouchHoldUntilMs = _host.NowMs + 2500;
        }

        // 「AI 出题」面板：全屏模态，先于「更多」和一切其它命中。
        if (_aiOpen)
        {
            AiPointerDown(p);
            return true;
        }

        // 「更多」面板：全屏模态，先于一切其它命中（它盖住整块屏幕）。
        // 点面板外 = 关闭，而且这一下**不落墨**（消费掉；这也是自检要钉的一条）。
        if (_moreOpen)
        {
            // 语言下拉先吃：选项按下只记候选，抬起还在同一项才选中；
            // 点下拉外面 = 只收起（不顺手执行下面那一行）。
            if (_langMenuOpen)
            {
                int lh = LangMenuHit(p.X, p.Y);
                if ((lh >= MoreHitLangItem && lh < MoreHitLangItem + LangMenuCount)
                    || lh == MoreHitLangApply)
                {
                    _langMenuPress = lh;
                    _langMenuPressPos = p;
                    _langMenuScrollAtPress = _langMenuScroll;
                    _langMenuDragged = false;
                    Invalidate();
                    return true;
                }
                if (lh == MoreHitInside) return true;
                CloseLangMenu();
                return true;
            }
            if (SettingsTouchDown(e, p)) return true;
            int hit = MoreHitAt(p.X, p.Y);
            if (hit == MoreHitNone || hit == MoreHitClose) { CloseMore(); return true; }
            if (hit == MoreHitBack)
            {
                if (_host?.State.LogNoteEditing == true) _host.Commands.EndLogNoteEdit(true);
                GoBackMorePage();
                _moreHover = MoreHitNone;
                Invalidate();
                return true;
            }
            if (_morePage == MorePage.Api)
            {
                ApiPointerDown(hit, p.X, p.Y);
                return true;
            }
            if (hit >= MoreHitSettingsNav && hit < MoreHitSettingsNav + SettingsCatNames.Length)
            {
                var cat = (SettingsCategory)(hit - MoreHitSettingsNav);
                _moreHover = hit;
                ScrollSettingsToCategory(cat);
                _moreH.To(MoreSettingsH(), Tokens.MoreOpenMs);
                return true;
            }
            if (hit == MoreHitSettingsScroll)
            {
                _settingsScrollDrag = true;
                DragSettingsScrollbar(p.Y);
                return true;
            }
            if (_morePage == MorePage.Log
                && hit >= MoreHitLogNote && hit <= MoreHitLogQr)
            {
                ActivateLog(hit);
                return true;
            }
            if (_morePage == MorePage.Help && hit >= HelpHitTabBase && hit < HelpHitTabBase + HelpTabCount)
            {
                SetHelpTab(hit - HelpHitTabBase);
                return true;
            }
            if (hit >= MoreHitProfile && hit < MoreHitProfile + 3)
            { SetProfile((Profile)(hit - MoreHitProfile)); return true; }
            // 区间必须**按表的长度收口**：宫格 30+1..30+12、行 60+0..60+8、启动器格子 140+0..140+5。
            // 写宽了就会串段——历史上宫格写成 +32 吃过行的 60/61（TogglePin 越界抛异常，自检当场红）。
            if (hit >= MoreHitChip && hit <= MoreHitChip + Cells.Length - 1)
            { TogglePin(hit - MoreHitChip); return true; }
            // 行/格子按下时**存原始命中码**，抬起时比对同一个码（同一套手感）。
            // 触摸/笔按住不动 = 长按候选（到点出提示、松手不执行）；鼠标照旧。
            bool pressRow = false;
            if (hit >= MoreHitRow && hit <= MoreHitRow + Rows.Length - 1)
            { _morePress = hit; pressRow = true; }
            if (hit >= MoreHitTile && hit <= MoreHitTile + HubTileTotal - 1)
            { _morePress = hit; pressRow = true; }
            if (hit >= MoreHitBottom && hit <= MoreHitBottom + MoreBottomTiles.Length - 1)
            { _morePress = hit; pressRow = true; }
            if (pressRow)
            {
                if (touchLike && TipContent(1000 + hit).Title != null) ArmTipHold(1000 + hit);
                Invalidate();
                return true;
            }
            return true;                    // 面板里的空白：吃掉，但不关
        }

        if (_expand.Value < 0.5f)
        {
            if (!BallRect().Contains(p.X, p.Y)) return false;
            _press = -2;                    // 球
            return true;
        }

        // ---- 触摸/笔长按（2026-10-02 第二轮）----
        //
        // 鼠标**不走这里**（鼠标按住 = 拖动/划滑条，语义一个字不变）。这一块只服务
        // "按下即生效、必须改成'按住候选、抬起生效'才能长按"的几个：虚实线格、
        // 图形段的 22 格、关闭白板 ✕。别的元素各自有安排：
        //   · 工具格本来就在抬起时执行（见 PointerUp），在下面 idx 分支武装；
        //   · 清空 = 按住 0.8 秒清空、滑条 = 拖动、PPT 页码格 = 长按菜单 → **排除**；
        //   · 色片 / 文字段没有提示（收窄清单）→ 不武装，按下即生效照旧。
        if (touchLike && BandOpen())
        {
            int holdTarget = -1;
            if (HitDashToggle(p.X, p.Y)) holdTarget = 500;
            else if (_bandCell == ShapeCell)
            {
                int sg = HitSegment(p.X, p.Y);
                if (sg >= 0) holdTarget = 200 + sg;
            }
            else if (CurAction == BandAction.CloseBoard && ActionRect().Contains(p.X, p.Y))
                holdTarget = 400;
            if (holdTarget >= 0 && TipContent(holdTarget).Title != null)
            {
                ArmTipHold(holdTarget);
                return true;      // 抬起才执行；中途长按出提示则这次不执行
            }
        }

        // **滑条要排在工具格前面**：它在面板最下沿，和工具格的矩形是重叠的。
        // 排在后面的话，按最下沿那一条会被当成"点了某个工具"（假面板里 groove 也是先判的）。
        // 动作按钮（清空/全选/复制/粘贴）排在最前面：它贴在上带最外沿，和谁都挨着。
        // AI 下拉要**先于**动作格处理：它的菜单浮在带子外面。
        if (_aiMenuOpen && AiMenuClick(p.X, p.Y)) return true;

        int actIdx = BandOpen() ? ActionHitAt(p.X, p.Y) : -1;
        if (actIdx >= 0)
        {
            HandleBandAction(actIdx);
            Invalidate();
            return true;
        }

        if (BandOpen() && BandHasTextSize)
        {
            if (TextSizeButtonRect().Contains(p.X, p.Y))
            {
                if (_textFontMenuOpen) CloseTextFontMenu();
                else OpenTextFontMenu();
                Invalidate();
                return true;
            }
        }
        if (BandOpen() && BandHasWidthButton && HitWidthButton(p.X, p.Y))
        {
            CycleWidthGrade();
            return true;
        }
        if (BandOpen() && BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(p.X, p.Y))
        {
            _sliderDragging = true;
            DragSlider(p.X);       // 按下即跳到该位置（引擎吸附到最近一档），可接着拖
            return true;
        }

        int idx = HitCell(p.X, p.Y);
        if (idx >= 0)
        {
            _press = idx;
            // 工具格：抬起执行；触摸/笔按住不动 = 长按候选（鼠标不武装）
            if (touchLike && TipContent(idx).Title != null) ArmTipHold(idx);
            return true;
        }

        // 主条上的空白（格间缝、两端内边距）也允许拖动整条：
        // 14 格之后整条中心正好落在组间缝里，从中心按下拖不走会被当成"面板坏了"。
        if (BarRect().Contains(p.X, p.Y))
        {
            _press = -3;
            return true;
        }

        // 上带：色片 / 分段（滑条已经在上面判过了）
        if (BandOpen())
        {
            // 虚实线那一格：**按下即生效**（和色片同一个手感——点一下就该看见结果，
            // 不用等抬手；抬手那一下还要给"拖动面板"让路）。
            if (HitDashToggle(p.X, p.Y)) { CycleDash(); return true; }
            int sw = HitSwatch(p.X, p.Y);
            if (sw >= 0) { ActivateSwatch(sw); return true; }
            int sg = HitSegment(p.X, p.Y);
            if (sg >= 0) { ActivateSegment(sg); return true; }
        }

        // 收起的色线：**点一下就张开**（2026-10-09 方案 A①，见 调研-色带-全盘复查与优化.md）。
        // 明确动作立即响应——跳过悬停延迟、写字余温、"离开一次"这三道门槛；
        // 悬停那条路保持"慢一步"的沉稳（240ms + 余温，一个字不动）。
        // 穿透里除外（口径同 UpdateRail："永远保持收起的那条色线"）。
        // ⚠ 命中用**当前那条色线的矩形**（收起态 = 10px），不能用 RailZone（那是"张开后全高"，
        //    图形格能到 ~110px——会把面板正上方画布上的落笔也吃掉）。上下各放宽 10px 好点。
        if (!BandOpen() && !_host.State.PassThrough && BandVisible())
        {
            var bandNow = BandRect();
            var bandHit = new RectF
            {
                MinX = bandNow.MinX, MinY = bandNow.MinY - Tokens.RailHoverPad,
                MaxX = bandNow.MaxX, MaxY = bandNow.MaxY + Tokens.RailHoverPad,
            };
            if (bandHit.Contains(p.X, p.Y))
            {
                _railRequireLeave = false;  // 明确点击 = 老师主动"回来"了，不再要求先离开一次
                _railEnterAtMs = double.NegativeInfinity;
                _rail.To(1f, Tokens.RailMs);
                return true;
            }
        }

        return false;                       // 带子/上带里的空白（两端内边距）：不吃，引擎按"地盘"吞掉
    }

    /// <summary>屏幕坐标 → "没有平移过的"布局坐标（贴边隐藏会整体平移一次）。</summary>
    private Vector2 Local(in UiPointerEvent e)
    {
        var s = Shift();
        return new Vector2(e.X - s.X, e.Y - s.Y);
    }

    public bool PointerMove(in UiPointerEvent e)
    {
        // 触摸长按计时 / 提示停留期间：**别的指针**（停着的鼠标、笔悬停）的移动不许搅局。
        // 引擎会把窗口收到的所有移动都转给界面（UiCapturing 分支），少了这一道，
        // 鼠标随手动一下就被当成"手指滑走了"（2026-10-02 自检实测 569px 假移动）。
        if ((_tipHoldStart > double.NegativeInfinity || _tipHoldFired)
            && e.PointerId != _pressId)
            return true;
        _hoverInside = HoverInsideForPeek(e.X, e.Y);
        if (_hoverInside) _peekArmed = true;   // 指针进过面板 → 之后允许"离开就收"
        _leftAtMs = _host.NowMs;
        var p = Local(e);
        if (_penPickerOpen)
        {
            PenPickerPointerMove(p.X, p.Y);
            return true;
        }
        _railHover = !_host.State.PassThrough && BandVisible() && RailHoverZone().Contains(p.X, p.Y);
        _railHoverStamp = _host.NowMs;    // 位置更新打时间戳（写字后"离开一次"的判据用）

        // 「AI 出题」面板开着时，指针只喂给面板：更新悬停、别再碰主条的悬停/拖动状态
        if (_aiOpen)
        {
            int hit = AiHitAt(p.X, p.Y);
            if (hit != _aiHover) { _aiHover = hit; Invalidate(); }
            return true;
        }

        // 「更多」面板开着时，指针只喂给面板：更新悬停、别再碰主条的悬停/拖动状态
        if (_moreOpen)
        {
            if (_langMenuOpen)
            {
                if (_langMenuPress >= MoreHitLangItem
                    && _langMenuPress < MoreHitLangItem + LangMenuCount)
                {
                    // 手指/鼠标按住上下拖 = 滚列表（11 项里常显示 8 项）。
                    if (!_langMenuDragged
                        && MathF.Abs(p.Y - _langMenuPressPos.Y) > 6f)
                        _langMenuDragged = true;
                    if (_langMenuDragged)
                    {
                        float rows = (_langMenuPressPos.Y - p.Y) / LangMenuItemH;
                        int s = Math.Clamp(_langMenuScrollAtPress + (int)MathF.Round(rows),
                                           0, LangMenuScrollMax);
                        if (s != _langMenuScroll) { _langMenuScroll = s; Invalidate(); }
                    }
                    return true;
                }
                int lh = LangMenuHit(p.X, p.Y);
                int langHover = lh >= MoreHitLangItem && lh < MoreHitLangItem + LangMenuCount
                    ? lh - MoreHitLangItem
                    : lh == MoreHitLangApply ? MoreHitLangApply : -1;
                if (langHover != _langMenuHover) { _langMenuHover = langHover; Invalidate(); }
                HideTip();
                return true;
            }
            if (SettingsTouchMove(e, p)) return true;
            if (_morePage == MorePage.Api)
            {
                ApiPointerMove(p.X, p.Y);
                return true;
            }
            if (_settingsScrollDrag && _morePage == MorePage.Settings)
            {
                DragSettingsScrollbar(p.Y);
                return true;
            }
            int hit = MoreHitAt(p.X, p.Y);
            if (hit != _moreHover) { _moreHover = hit; Invalidate(); }
            // 面板里的提示：鼠标悬停出、手指/笔长按也出（2026-10-02 第二批）
            SetTipTarget(MoreTipTargetAt(hit));
            // 长按候选：在面板上滑走（超过拖动阈值）就取消
            if (_tipHoldStart > double.NegativeInfinity
                && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
                CancelTipHold();
            return true;
        }

        if (_sliderDragging)
        {
            HideTip();                 // 拖滑条时"粗细预览"那张卡在画，别叠提示
            DragSlider(p.X);
            return true;
        }

        if (_press != -1)
        {
            // 触摸/笔长按已经弹过提示：这一次按下作废——不拖动、也不执行（等松手）。
            if (_tipHoldFired) return true;
            // **动作按钮不参与拖动**。
            //
            // 用户实测报的 bug："按住清空的时候，手一抖就把整个面板拖走了"——
            // 面板一走，按钮就不在指针下面了，看着就是"清空没反应"。
            // 只有点在**主条上**（球或者工具格）才算"抓住面板"。
            // （「更多」面板的行有它自己的 `_morePress`，走不到这里——它是全屏模态。）
            bool draggable = _press != 2000 && _press < 1000;
            if (draggable && !_dragging && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
            {
                _dragging = true;
                CancelTipHold();        // 移动就是拖动：长按候选作废（主流消歧：移动=拖、不动=长按）
            }
            if (_dragging)
            {
                _anchor = _dragStartAnchor + (p - _pressPos);
                Invalidate();               // 位置一变就要重画；引擎那边每帧都会加进脏区
            }

            // 按住清空的时候指针滑出按钮 = 算了（各家按钮都是这个约定）。
            // 判定区给 8 像素余量，免得手抖一两像素就取消。
            if (_press == 2000)
            {
                var hit = ActionRect();
                if (p.X < hit.MinX - 8f || p.X > hit.MaxX + 8f
                    || p.Y < hit.MinY - 8f || p.Y > hit.MaxY + 8f)
                {
                    _actionHoldFrom = double.NegativeInfinity;
                    _press = -1;
                    Invalidate();
                }
            }
            return true;
        }

        // 触摸长按已经弹过提示（段/线型/关板那条路）：按住期间移动什么都不做。
        if (_tipHoldFired) return true;
        // 触摸长按还在计时，但手指已经滑走：这次按下不执行、也不弹提示（重新按）。
        if (_tipHoldStart > double.NegativeInfinity
            && Vector2.Distance(p, _pressPos) > Tokens.DragThreshold)
        {
            CancelTipHold();
            Invalidate();
            return true;
        }

        int hover = _expand.Value < 0.5f
            ? (BallRect().Contains(p.X, p.Y) ? -2 : -1)
            : HoverAt(p.X, p.Y);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        SetTipTarget(hover);
        // **整块界面都算"界面"**（2026-10-17 用户报"图库那行有的图标箭头、有的十字"）：
        // 原来只认"正好压在某个可点元素上"，格子之间 / 带子右侧的空白就落回画布光标
        //（图形工具是十字）——同一块面板里两种光标。现在主条和上带的矩形内一律箭头。
        if (hover != -1) return true;
        return PanelRect().Contains(p.X, p.Y)
            || (BandVisible() && BandRect().Contains(p.X, p.Y));
    }

    /// <summary>
    /// 面板打开时吃掉滚轮：说明页内部滚动；其他 More 页也吃掉，避免滚轮把
    /// 遮罩后的画布滚走。没开面板才交还给引擎。
    /// </summary>
    public bool Wheel(float x, float y, int delta)
    {
        if (_aiOpen) return true;
        if (!_moreOpen) return false;
        if (_langMenuOpen)
        {
            if (LangMenuRect().Contains(x, y))
            {
                ScrollLangMenu(delta > 0 ? -1 : 1);
                return true;
            }
            CloseLangMenu();
            return true;          // 滚轮在下拉外面 = 只收起，别把设置页一起滚走
        }
        if (_morePage == MorePage.Api && _apiModelMenuOpen && ApiModelMenuRect().Contains(x, y))
        {
            int max = Math.Max(0, _apiModels.Count - ApiModelVisibleCount);
            if (max > 0)
            {
                _apiModelScroll = Math.Clamp(_apiModelScroll + (delta > 0 ? -1 : 1), 0, max);
                Invalidate();
            }
            return true;
        }
        if (_morePage == MorePage.Settings && SettingsViewportRect().Contains(x, y))
        {
            StopSettingsFling();
            // 设置页比画布更"细"：一格 48 逻辑像素，滚轮/触控板不会一下从书写冲到最底。
            ScrollSettingsTo(_settingsScroll - delta / 120f * 48f);
            return true;
        }
        if (_morePage == MorePage.Help && MoreRect().Contains(x, y))
            ScrollHelp(-delta / 120f * 48f);
        return true;
    }

    public void PointerLeave()
    {
        // 触摸长按：手指还按着的时候，"离开"多半是小窗/主窗切换的假动作——
        // 按住计时 / 已经弹出的提示**不在这里收**（松手或停留到期自会收）。
        if (_tipHoldStart == double.NegativeInfinity && !_tipHoldFired)
        {
            HideTip();
            CancelTipHold();
        }
        // 面板开着时指针只会"离开整块屏幕"（占用 = 全屏）：把面板的悬停/按下也清掉
        if (_aiOpen)
        {
            _aiHover = AiHitOutside;
            Invalidate();
        }
        if (_moreOpen)
        {
            _moreHover = MoreHitNone;
            _morePress = -1;
            _langMenuHover = -1;
            _langMenuPress = -1;
            _langMenuDragged = false;
            _settingsScrollDrag = false;
            Invalidate();
        }
        if (!_hoverInside) return;
        _hoverInside = false;
        _railHover = false;              // 指针离开面板 = 焦点不在了，设置条该收（走 ExitDelay）
        _leftAtMs = _host?.NowMs ?? 0;
        Invalidate();
    }

    /// <summary>
    /// 这一刻指针落在什么上面。上带里的东西用偏移的编号：
    /// 100＋色片下标、200＋分段下标、300 = 滑条。**画与命中同源**，
    /// 不这么编号的话"悬停态"和"命中"会各写一份判断，然后慢慢不一致。
    /// </summary>
    private int HoverAt(float x, float y)
    {
        if (_textFontMenuOpen)
        {
            int fi = HitTextFontCell(x, y);
            if (fi >= 0) return 620 + fi;
            if (TextFontMenuRect().Contains(x, y)) return 619;
        }
        int idx = HitCell(x, y);
        if (idx >= 0) return idx;
        if (!BandOpen()) return -1;
        // 动作按钮：编号 400 + 第几个，和色片 100、分段 200、滑条 300 排成一套
        if (RailOpen && ActionCount > 0)
        {
            int act = ActionHitAt(x, y);
            if (act >= 0) return 400 + act;
        }
        if (BandHasTextSize && RailOpen)
        {
            if (TextSizeButtonRect().Contains(x, y)) return 610;
        }
        if (BandHasWidthButton && HitWidthButton(x, y)) return 501;
        if (BandHasSlider && Widgets.SliderHit(SliderRect()).Contains(x, y)) return 300;
        if (HitDashToggle(x, y)) return 500;                       // 虚实线那一格
        int sw = HitSwatch(x, y);
        if (sw >= 0) return 100 + sw;
        int sg = HitSegment(x, y);
        if (sg >= 0) return 200 + sg;
        return -1;
    }

    public bool PointerUp(in UiPointerEvent e)
    {
        // 色板在这一按里被关掉了：抬起也归它，不再走普通工具条路径。
        if (_penPickerDismissUp)
        {
            _penPickerDismissUp = false;
            _penPickDragging = false;
            _press = -1;
            _dragging = false;
            Invalidate();
            return true;
        }
        if (_penPickerOpen)
        {
            PenPickerPointerUp();
            return true;
        }
        // 触摸长按/提示停留期间，只有**按下的那根手指**的抬起才算数（别的指针抬起忽略）。
        if ((_tipHoldStart > double.NegativeInfinity || _tipHoldFired)
            && e.PointerId != _pressId)
            return true;
        // 「AI 出题」面板：按下已经在 AiPointerDown 里执行；抬起只消费。
        if (_aiOpen)
        {
            Invalidate();
            return true;
        }
        // 「更多」面板：按下和抬起落在**同一行**才算一次执行（和抽屉同一套手感）
        if (_moreOpen)
        {
            if (_langMenuOpen || _langMenuPress >= 0)
            {
                int langPressed = _langMenuPress;
                _langMenuPress = -1;
                if (_langMenuOpen && !_langMenuDragged && langPressed >= 0)
                {
                    int lh = LangMenuHit(e.X, e.Y);
                    if (lh == langPressed)
                    {
                        if (langPressed == MoreHitLangApply) ApplyLangMenu();
                        else if (langPressed >= MoreHitLangItem) SelectLangMenu(langPressed - MoreHitLangItem);
                    }
                }
                _langMenuDragged = false;
                Invalidate();
                return true;
            }
            if (SettingsTouchUp(e)) return true;
            if (_morePage == MorePage.Api) { _morePress = -1; return true; }
            if (_settingsScrollDrag)
            {
                _settingsScrollDrag = false;
                Invalidate();
                return true;
            }
            // 触摸/笔长按已经弹过提示：这一次松手**不执行**（提示停一会儿自己收）——
            // 和主条上那条规则完全一致（Windows/Material 惯例：长按是"看"不是"点"）。
            if (_tipHoldFired)
            {
                CancelTipHold();
                _tipLingerUntil = _host.NowMs + Tokens.TipLingerMs;
                _morePress = -1;
                Invalidate();
                return true;
            }
            int hit = MoreHitAt(e.X, e.Y);
            int pressed = _morePress;
            _morePress = -1;
            if (pressed >= MoreHitRow && pressed <= MoreHitRow + Rows.Length - 1 && hit == pressed)
                ActivateRow(pressed - MoreHitRow);
            else if (pressed >= MoreHitTile && pressed <= MoreHitTile + HubTileTotal - 1 && hit == pressed)
                ActivateHub(pressed - MoreHitTile);
            else if (pressed >= MoreHitBottom && pressed <= MoreHitBottom + MoreBottomTiles.Length - 1 && hit == pressed)
                ActivateBottom(pressed - MoreHitBottom);
            Invalidate();
            return true;
        }
        if (_morePress >= 0) { _morePress = -1; return true; }   // 面板刚关掉的那一下：已消化

        if (_sliderDragging)
        {
            _sliderDragging = false;
            return true;
        }

        // ---- 触摸/笔长按的收尾（2026-10-02 第二轮）----
        if (_tipHoldFired)
        {
            // 长按已经弹过提示：这一次松手**不执行任何功能**（Windows/Material 惯例：
            // 长按是"看"不是"点"）；提示继续停留 TipLingerMs 后自动收。
            CancelTipHold();
            _tipLingerUntil = _host.NowMs + Tokens.TipLingerMs;
            _press = -1;
            _dragging = false;
            _sliderDragging = false;
            Invalidate();
            return true;
        }
        if (_press == -1 && _tipHoldStart > double.NegativeInfinity)
        {
            // "按下即生效"的那几格，触摸/笔改成**抬起才生效**（中途长按出提示则不生效）：
            // 虚实线格、图形段、关闭白板 ✕。
            int target = _tipHoldTarget;
            CancelTipHold();
            if (target == 500) CycleDash();
            else if (target == 400)
            {
                if (_host.State.Board)
                {
                    _host.Commands.SetBoard(false);
                    _actionFlashUntil = _host.NowMs + 260;
                }
            }
            else if (target >= 200 && target < 200 + BandSegmentCount)
                ActivateSegment(target - 200);
            Invalidate();
            return true;
        }
        CancelTipHold();        // 工具格那条路：候选作废，原样走抬起执行

        int idx = _press;
        bool dragged = _dragging;
        _press = -1;
        _dragging = false;
        if (idx == -1) return false;
        if (idx == -3) return true;              // 主条空白：只是拖动/点一下，不执行功能

        if (dragged)
        {
            // **松手不吸附**（用户 2026-09-30："我（说的）吸附是比如拖到任务栏下、它自动靠底边，
            // 这种的不用了"）：拖到哪儿就停在哪儿。
            // 拖动过程本身已经把它夹在屏幕内（每次读 `Anchor()` 都过 `Clamp`），
            // 所以"拖出去找不回来"这条底线仍然在；只是不再自动贴边。
            // 贴边**隐藏**是另一回事：只认底边、离底边够近才触发（见 Shift）。
            return true;
        }

        // 按住清空：松手即取消（够 0.8 秒的那一次已经在 UpdateBandAction 里执行过了）
        if (idx == 2000)
        {
            _actionHoldFrom = double.NegativeInfinity;
            Invalidate();
            return true;
        }
        if (idx == -2) { Toggle(); return true; }        // 点球：展开
        if (idx == 0) { Toggle(); return true; }         // 点带子最左那格：收起
        Activate(idx);
        return true;
    }

    private int HitCell(float x, float y)
    {
        var vis = VisibleCells();
        for (int k = 0; k < vis.Length; k++)
            if (CellRect(k).Contains(x, y)) return vis[k];      // 返还完整档的下标
        return -1;
    }

    private void Toggle()
    {
        bool collapse = _expand.Value > 0.5f;
        // 系统关掉动画时直接跳终态（教室里老机器上很常见）
        _expand.To(collapse ? 0f : 1f, collapse ? Tokens.CollapseMs : Tokens.ExpandMs);

        // **收起时必须把临时状态清干净**：面板、悬停、按下的格、在拖的滑条。
        // 不清的话，缩回一个球之后那些状态还挂在那儿，下次展开时会自己冒出来
        //（假面板当年就是栽在这条上："点更多→点收起→再展开，抽屉自己冒出来"）。
        if (collapse)
        {
            // 面板理论上打不开（它要主条展开才有入口），但状态机收口时一并清掉，
            // 免得以后哪天加了别的入口，缩球时面板还挂在那儿。
            CloseLangMenu();
            _moreOpen = false;
            _more.Jump(0f);
            _moreHover = MoreHitNone;
            _morePress = -1;
            _hover = -1;
            _press = -1;
            _dragging = false;
            _sliderDragging = false;
            CancelTipHold();
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
        }
        Invalidate();
    }

    /// <summary>
    /// 点某一格。**只走命令通道**：界面不许直接改引擎状态（撤销栈、空间索引、
    /// 脏区都靠引擎维护，越权就会破坏这些不变量）。
    /// </summary>
    private void Activate(int idx)
    {
        CloseTextFontMenu();
        var cmd = _host.Commands;
        var st = _host.State;

        // **点之前**上带停在哪一格。笔 / 荧光笔那一格要用它判"这一下是切色、还是只是把设置条拿过来"
        //（`_bandCell` 在下面 `HasBand` 那一块里会被改成 idx，改完就问不出"原来在哪"了）。
        int prevBand = _bandCell;
        // **点之前是不是在穿透**：穿透下点笔 / 荧光笔格 = "我要回来写字"，这一次**不许顺手换色**
        //（和键盘 Ctrl+P 同一条：穿透先退出、第二步才谈换色。2026-10-05 用户报的 bug）。
        bool wasPassThrough = st.PassThrough;

        // **点了别的格子 = 选择格那次"双击"序列到此为止**。
        // 不这么做的话，"选择格 →（200ms）笔格 →（200ms）选择格"会被算成对选择格的双击，
        // 于是老师只是在两个格子之间来回看一眼，就被全选了。
        if (idx != 8) _lastSelCellClickMs = double.NegativeInfinity;

        // 点工具格时，上带跟着换成这个工具的设置（"上带＝这个按钮的设置条"），
        // 并且**钉住展开**——不钉的话指针一移开就收了，选项来不及选。
        //
        // 再点一次**同一个**工具 = 收起它自己的设置条（假面板的用法，也是各家通例）。
        if (HasBand(idx))
        {
            // 设置条显示**这一格的设置**：点白板就看板色＋翻页，点橡皮就看整笔擦/面积擦。
            //
            // 这里以前还有一套"点一次钉住、再点同一个工具收起"的状态（照假面板抄的）。
            // 2026-09-17 用户定了新的语义：**张不张开只看焦点在不在面板上**
            // （见 RailHoverZone）。于是"再点一次收起"这一支必须删掉——
            // 指针还停在面板上，收下去会立刻又张开，是两个规则打架。
            // 指针一离开面板它自己就收（走 800 毫秒的退出延迟），不用老师再点一次。
            //
            // 2026-10-09 方案 A②：点工具格 = **那格的设置条立即张开**（明确动作立即响应，
            // 见 调研-色带-全盘复查与优化.md）。旧代码这里把 `_railEnterAtMs` 重置成 -∞，
            // 注释写"不用再等开门那 120 毫秒"，实际效果却是**倒计时从头再来**——
            // 快速点格反而比只悬停更晚亮，与注释意图相反。
            _bandCell = idx;
            _railEnterAtMs = double.NegativeInfinity;
            _rail.To(1f, Tokens.RailMs);
        }

        switch (idx)
        {
            case 1:
                // 进穿透前先清屏（设置开着 + 桌面批注才真的清；引擎自己判）。
                if (!st.PassThrough) cmd.ClearDesktopOnArrow();
                cmd.SetPassThrough(!st.PassThrough);
                break;
            // 白板那一格：**点三下是一个来回**（用户 2026-09-27 第三次定的）。
            //
            //   ① 板关着               → 开板（顺手把设置条拿过来）
            //   ② 板开着、色带在别处     → **只把色带拿过来，板一动不动**
            //   ③ 板开着、色带也在这一格 → **关板**
            //   ④ 再点一下 → 又回到 ①（开板），往后就这三下循环
            //
            // 为什么要改（用户原话："那个白板我试了几遍，感觉单独弄一个开关还是不习惯"）：
            // 上一版把"关板"挪去了设置条最右端的 ✕，格子上怎么点都关不掉——用起来
            // 反而别扭。现在这一个格子自己就是那个开关，✕ 留着当"一眼看得见的关板键"。
            //
            // 同时**去掉了"再点一下换下一个板色"**（用户："单击切换上面的白板颜色，
            // 我感觉不需要了"）：换板色色带上就摆着三格（白/绿/黑），一点就到；
            // 而这一个格子到底是"开"还是"关"才是大家点它的本意。
            //
            // ② 那条判据必须带 `prevBand == 2`：老师从图形面板点回白板那一格，
            // 意思是"把板拿回来用"，那时候不该顺手把它关掉（同笔 / 荧光笔 / 图形那几格）。
            case 2:
                // "两张纸"模型（2026-10-09 用户定）：白板画在文档上面。文档开着时这一格就是
                // **开板/关板**——开板＝白板盖住 PDF（页图/条让位），关板＝回到文档。
                // 打开文档的那一下会把白板自动收掉（先让你看见文档，见 DocImport.StartDocView），
                // 所以刚开完文档这里必然是"灭"的，点一下才把白板铺上来。
                if (!st.Board) cmd.SetBoard(true);             // ① 开板
                else if (prevBand == 2) cmd.SetBoard(false);   // ③ 关板
                break;                                         // ② 只把色带拿过来，板不动
            // 笔 / 荧光笔那两格：**已经是它、而且色带本来就在这一格 → 换下一个颜色**
            //（用户 2026-09-27 定的"已经是它了，点击切换颜色"）。
            //
            // ⚠ 判据必须带上前一提"色带本来就在这一格"：老师从图形面板点回笔那一格，
            //   意思是"把笔拿回来"，那时候不能悄悄把颜色也换了；而老师一直在用笔时
            //   色带本来就停在笔那一格，连点就是连着换色——正好是想要的手感。
            //
            // ⚠ **切色那条路上也要走一次 `SetTool`**：引擎的 `SwitchTool` 里还兼着
            //   "顺手把穿透关掉"（穿透和工具互斥，见 Engine.SwitchTool）。漏了它就会出现
            //   "点笔格换了色、但还在穿透"——自检里"点工具格＝顺手关掉穿透"那两条当场就红。
            //   `SetTool` 是幂等的（工具没变时只做清理），重复调没有副作用。
            case 3:
                cmd.SetTool(Tool.Pen);
                if (prevBand == 3 && !wasPassThrough) CycleColor();
                break;
            case 4:
                cmd.SetTool(Tool.Highlighter);
                if (prevBand == 4 && !wasPassThrough) CycleColor();
                break;
            case 5: cmd.SetTool(Tool.Laser); break;
            case 6:
                // [2026-10-05 用户定] 橡皮子类型不再"点一下换一次"：点这一格 = 进橡皮
                //（上一次用整笔就整笔、用面积就面积）；整笔/面积去上带那两段里选。
                cmd.SetEraserPreferred();
                break;
            case 7:
                // 文本：和画笔一样是常驻工具。选中后点画布空白 = 在点击处输入；
                // 点已有文字 = 选中它。色带跟着这一格出来（字号 + 颜色 + 完成）。
                // 正在输入时再点这一格 = 完成（触摸屏没有 Ctrl+Enter）。
                if (st.TextBoxEditing) cmd.CommitTextEdit();
                else cmd.SetTool(Tool.Text);
                break;
            case 8:
                // 选择那一格：**不再"再点一下换档"**（矩形/套索去上带那两段里选，2026-10-05）。
                //
                // **双击 = 全选**（500ms 内两击，照 InkClass 那个经典交互）。
                // 没有"再点换档"之后，两击的净效果就是"选中工具 + 全选"——比原来更直白。
                if (_host.NowMs - _lastSelCellClickMs < SelectDoubleClickMs)
                {
                    _lastSelCellClickMs = double.NegativeInfinity;
                    cmd.SetTool(Tool.Marquee);
                    cmd.SetSelectMode(_selModeBeforeDoubleClick);
                    cmd.SelectAll();
                    _actionFlashUntil = _host.NowMs + 260;
                    break;
                }
                _lastSelCellClickMs = _host.NowMs;
                _selModeBeforeDoubleClick = st.SelectMode;
                cmd.SetTool(Tool.Marquee);
                break;
            case 9: cmd.SetTool(Tool.Capture); break;
            case 10: cmd.ToggleShapesPanel(); break;
            case 11:
                // 收藏：打开/关闭收藏弹窗；色带保持当前工具的色线，不参与收藏。
                cmd.ToggleLibraryPanel();
                break;
            case 12: cmd.Undo(); break;
            case 13: cmd.Redo(); break;
            case 14:
                // 「更多」：打开屏幕中央的面板（2026-10-01 起；抽屉从产品路径退场）
                OpenMore();
                break;
        }
        Invalidate();
    }

    /// <summary>哪些格子有上带。没有的（后撤/重做/更多/截屏）点了不长出一条空带子。</summary>
    private static bool HasBand(int cell) => cell is >= 2 and <= 9;

    /// <summary>当前工具对应的格子——键盘换工具时用它把上带掰回来。</summary>
    private static int CellForTool(Tool t)
    {
        // 图形共用第 10 格（哪一种是哪一段由上带里的高亮标出来）。
        // 判据走 HasShapeEntry 而不是再列一遍图形名字：加一种图形只改 ShapeRows。
        if (HasShapeEntry(t)) return 10;
        return t switch
        {
            Tool.Highlighter => 4,
            Tool.Laser => 5,
            Tool.Eraser or Tool.PixelEraser => 6,
            Tool.Text => 7,
            Tool.Marquee => 8,
            Tool.Capture => 9,
            _ => 3,
        };
    }

    public void OnStateChanged(in UiState state)
    {
        _aiPrompt = state.AiPrompt ?? _aiPrompt;
        _aiPromptEditing = state.AiPromptEditing;
        if (state.AiStatus != _lastAiStatus || state.AiBusy != _lastAiBusy)
        {
            _lastAiStatus = state.AiStatus ?? "";
            _lastAiBusy = state.AiBusy;
            Invalidate();
        }
        // 回放一开始：把「更多」/「AI」面板收掉。它们都是**全屏模态**，开着会挡住控制条和画布；
        if (state.ReplayActive && _moreOpen) CloseMore();
        if (state.ReplayActive && _aiOpen) CloseAiPanel();
        if (state.PassThrough && _penPickerOpen) ClosePenColorPicker();

        // 从取色板里点吸管：等 ColorPickActive 真的起来（armed），再等它落下，
        // 然后把取色板放回同一个色位——比“取完就没了、还要再点一次色片”顺手。
        if (_pickerReturnAfterPick)
        {
            if (state.ColorPickActive)
                _pickerReturnArmed = true;
            else if (_pickerReturnArmed)
            {
                _pickerReturnAfterPick = false;
                _pickerReturnArmed = false;
                bool wantHl = _pickerReturnHighlighter;
                if ((wantHl && state.Tool == Tool.Highlighter)
                    || (!wantHl && state.Tool == Tool.Pen))
                {
                    if (wantHl) OpenHighlighterColorPicker(_pickerReturnSlot);
                    else OpenPenColorPicker(_pickerReturnSlot);
                }
            }
        }

        // **工具变了就跟着换上带**（键盘热键是老师更常用的那条路）。
        // 判据是"工具真的换了"，不是"当前带子对不对"：点白板之后带子显示的是板色，
        // 那时工具没变、带子也不该被掰走；而一旦换成别的工具，带子必须跟上，
        // 不然会出现"手里是橡皮、上带还是色板"。
        if (state.Tool != _lastTool)
        {
            _lastTool = state.Tool;
            if (state.Tool != Tool.Text) CloseTextFontMenu();
            int cell = CellForTool(state.Tool);
            if (HasBand(cell)) _bandCell = cell;
            else if (HasShapeEntry(state.Tool))
            {
                // 图形不再有上带：从面板/快捷键选图形时，把旧带子收掉，
                // 否则会留着上一把工具（比如笔）的设置条。
                _bandCell = -1;
                _railHover = false;
                _rail.To(0f, Tokens.RailMs);
            }
            if (_penPickerOpen
                && state.Tool != (_pickerHighlighter ? Tool.Highlighter : Tool.Pen))
                ClosePenColorPicker();
        }

        // ---- 编辑文本框：把底部文字色带张开（选中文字后点色片改色）----
        // 编辑时操作条是隐藏的，没有这一条就没有改色入口；色带只留色片，不显示字号。
        if (state.TextBoxEditing != _lastTextBoxEditing)
        {
            _lastTextBoxEditing = state.TextBoxEditing;
            _railHover = false;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            if (state.TextBoxEditing)
            {
                _bandCell = 7;
                _rail.To(1f, Tokens.RailMs);
            }
            else
            {
                _rail.To(0f, Tokens.RailMs);
            }
            Invalidate();
        }

        // ---- 收藏弹窗开合：**不切换、不展开色带** ----
        // 打开时把设置条收回那条笔色线（同穿透）；关闭后恢复当前工具的悬停行为。
        if (state.LibraryOpen != _lastLibraryOpen)
        {
            _lastLibraryOpen = state.LibraryOpen;
            _railHover = false;
            _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
            if (state.LibraryOpen) _rail.To(0f, Tokens.RailMs);
            Invalidate();
        }

        // ---- 穿透开的那一刻：上带收回那条 10 像素色线（2026-10-02 用户口径）----
        //
        // 用户原话（第二次澄清）："点击穿透以后，色带是横起来的（收起来）。不是说我点了个
        // 穿透，色带就完全没有了。我说的色带消失，就是把它折叠起来，而不是像其他一样，
        // 点过来以后还是展开的。"——所以：**面板高度不变**（贴边隐藏露出来的还是那条线），
        // 只是把设置条收回去、而且穿透期间不许再张开（没有设置可放）。
        // **不动 `_bandCell`**：这里只负责把设置条收起来。退出后带子停在哪一格，
        // 由 `state.Tool` 那一支跟着工具走——2026-10-24 起"用户主动退出穿透 = 画笔"，
        // 所以退回来带子会落在笔格，而不是进穿透前那个工具格。
        //
        // "指针还停在面板上"也必须收：老师刚点的就是穿透格，不清悬停意图的话，120 毫秒后
        // 旧设置条又自己弹开（自检里钉着它）。
        if (state.PassThrough != _lastPass)
        {
            _lastPass = state.PassThrough;
            if (state.PassThrough)
            {
                _railHover = false;
                _railEnterAtMs = _railExitAtMs = double.NegativeInfinity;
                _rail.To(0f, Tokens.RailMs);     // 设置条 → 那条 10 像素色线
                Invalidate();
            }
        }

        // ---- 进放映的那一刻：**展开 + 回到默认位置**（用户 2026-09-27 定）----
        //
        // 用户原话："PPT 开始播放以后，把它打开放到居中靠底部。"
        // 他要的是"一放片，笔就自己出来"——老师上课的第一件事往往是拿起笔，
        // 不该先去找那颗球、或者去把上次拖到角落的面板拽回来。
        //
        // ⚠ **只在边沿做一次**（`!state.PptMode` 之外的那一下）：这一段要是写成
        // "放映中就归位"，那么他放映中把面板拖到一边、翻一页（状态变化 → 又走这里）
        // 就会被**拽回底部居中**——那是比"不归位"更烦人的行为。
        // ⚠ **退出放映不还原**（用户 2026-09-27 选的）：面板保持展开、位置不动。
        // 理由是他刚用完笔，回桌面还要接着写板书，再收起来等于多一步。
        if (state.PptMode && !_lastPpt)
        {
            _anchor = null;                       // 回默认位置（工作区底边居中）
            _peek.Jump(1f);                       // 贴边隐藏开着的话，也先完整露出来
            _leftAtMs = _host.NowMs;              // 这次"露面"重新计离开时间
            if (_expand.Value < 0.5f) _expand.To(1f, Tokens.ExpandMs);
            else _expand.Jump(1f);
        }
        _lastPpt = state.PptMode;

        Invalidate();
    }

}
