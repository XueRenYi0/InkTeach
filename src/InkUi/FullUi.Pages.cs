using System.Numerics;
using InkEngine;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace InkUi;

public sealed partial class FullUi
{
    /// <summary>
    /// A3：把更多/设置/帮助/日志/AI 服务做成有名字的 IPage 类型。
    /// 这一版仍复用 FullUi 现有 Draw/Hit（不在一次改动里搬几千行绘制），
    /// 但 PageHost 栈里已经是具体页面，不再是一袋匿名回调；下一步再逐页搬内容。
    /// </summary>
    private IPage CreatePage(MorePage page) => page switch
    {
        MorePage.Settings => new SettingsPage(this),
        MorePage.Help => new HelpPage(this),
        MorePage.Log => new LogPage(this),
        MorePage.Api => new ApiPage(this),
        _ => new MoreHomePage(this),
    };

    internal void OpenHelpPageForTest()
    {
        _moreOpen = true;
        EnsurePageHost();
        SetMorePage(MorePage.Home, push: false);
        SetMorePage(MorePage.Help, push: true);
        _moreH.Jump(MoreHelpH());
        _more.Jump(1f);
    }

    internal void OpenHomePageForTest()
    {
        _moreOpen = true;
        EnsurePageHost();
        SetMorePage(MorePage.Home, push: false);
        _moreH.Jump(MoreHomeH());
        _more.Jump(1f);
    }

    private abstract class MorePageBase : IPage
    {
        protected readonly FullUi Ui;
        private readonly PageSize _size;

        protected MorePageBase(FullUi ui, string id, string title, PageSize size, bool canBack)
        {
            Ui = ui;
            Id = id;
            Title = title;
            _size = size;
            CanBack = canBack;
        }

        public string Id { get; }
        public string Title { get; }
        public PageSize PreferredSize => _size;
        public bool CanBack { get; }
        public bool Modal => true;
        public bool IsAnimating => false;

        public virtual void OnEnter(PageContext ctx) { }
        public virtual void OnLeave(PageLeaveReason reason, PageResult result) { }
        public virtual void OnPause() { }
        public virtual void OnResume() { }
        public void Layout(in PageFrame frame) { }
        public abstract void Render(ID2D1DeviceContext ctx, in PageFrame frame);

        public PageHit HitTest(float x, float y)
        {
            var t = Ui.MoreHitTargetAt(x, y);
            return t.IsNone ? PageHit.None : new PageHit(PageHitKind.Action, t.ActionId, t.Index);
        }

        public bool PointerDown(in UiPointerEvent e) => false;
        public bool PointerMove(in UiPointerEvent e) => false;
        public bool PointerUp(in UiPointerEvent e) => false;
        public bool Wheel(float x, float y, int delta) => false;
        public bool Key(int vk, bool ctrl, bool shift, bool alt, bool repeat) => false;
    }

    private sealed class MoreHomePage : MorePageBase
    {
        public MoreHomePage(FullUi ui)
            : base(ui, "more.home", "更多", new PageSize(0f, ui.MoreHomeH()), canBack: false) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
        {
            string[] heads = { "课堂", "墨迹" };
            for (int s = 0; s < 2; s++)
            {
                Ui.DrawMoreGroupHead(ctx, Ui.HubHeadRect(s), heads[s]);
                for (int i = 0; i < Ui.HubCount(s); i++)
                {
                    int code = Ui.HubTileCode(s, i);
                    var info = Ui.HubTileInfo(code);
                    Ui.DrawHubTile(ctx, Ui.HubTileRect(s, i), info.Label, info.Enabled, info.Danger,
                                   MoreHitTile + code);
                }
            }

            // 状态行：命令反馈（保存/打开/更新）——产品里不弹窗，结果都落在这儿。
            // 没有要说的就显示**版本号**（用户 2026-10-08："软件里最好能看到版本号"）。
            var st = Ui._host.State;
            string status = !string.IsNullOrEmpty(Ui._hubHint) ? Ui._hubHint
                          : !string.IsNullOrEmpty(st.InkStatus) ? st.InkStatus
                          : st.UpdateStage != UpdateStage.Idle && !string.IsNullOrEmpty(st.UpdateText) ? st.UpdateText
                          : $"v{ProductInfo.Version}";
            var sr = Ui.HubStatusRect();
            if (status.Length != 0)
                Ui._widgets.Text(ctx, status, sr, 11.5f, Ui.Brush(ctx, Ui.MutedCol), center: false);

            // 底栏固定五格（表在上面，绘制/命中/提示共用）
            for (int i = 0; i < MoreBottomTiles.Length; i++)
                Ui.DrawHubTile(ctx, Ui.HubBottomRect(i), MoreBottomTiles[i].Label, true, MoreBottomTiles[i].Danger,
                               MoreHitBottom + i);

            // 新版本红点：面板已经打开时落在「检查更新」这一格上（主条那颗此时被遮住）。
            if (Ui.UpdateAvailable)
            {
                var tr = Ui.HubBottomRect(2);
                Ui.DrawUpdateDot(ctx, new Vector2(tr.MaxX - 8f, tr.MinY + 8f), 4.5f, 1f);
            }
        }
    }

    private sealed class SettingsPage : MorePageBase
    {
        public SettingsPage(FullUi ui)
            : base(ui, "more.settings", "设置", new PageSize(0f, ui.MoreSettingsH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
        {
            Ui.DrawSettingsNav(ctx);

            var vp = Ui.SettingsViewportRect();
            ctx.PushAxisAlignedClip(new Vortice.RawRectF(vp.MinX, vp.MinY, vp.MaxX, vp.MaxY),
                                    AntialiasMode.Aliased);

            // **一整页连续内容**：所有分类按顺序排下来，滚动只移动内容，不换页。
            for (int c = 0; c < SettingsCatNames.Length; c++)
            {
                var cat = (SettingsCategory)c;
                float headY = Ui.SettingsY(Ui.SettingsSectionTop(cat));
                var head = new RectF
                {
                    MinX = Ui.SettingsContentLeft(), MinY = headY,
                    MaxX = Ui.SettingsContentLeft() + Ui.SettingsContentW(),
                    MaxY = headY + MoreGroupHeadH,
                };
                Ui.DrawMoreGroupHead(ctx, head, SettingsCatNames[c]);

                if (cat == SettingsCategory.Toolbar)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var s = Ui.MoreProfileRect(i);
                        bool active = Ui.ProfileIndex() == i;
                        var rr = new RoundedRectangle(new Vortice.RawRectF(s.MinX, s.MinY, s.MaxX, s.MaxY), 8f, 8f);
                        if (active) ctx.FillRoundedRectangle(rr, Ui.Brush(ctx, Tokens.Accent));
                        else if (Ui._moreHover == MoreHitProfile + i) ctx.FillRoundedRectangle(rr, Ui.Brush(ctx, Ui.HoverCol));
                        ctx.DrawRoundedRectangle(rr, Ui.Brush(ctx, active ? Tokens.Accent : Ui.BorderCol), 1f);
                        Ui._widgets.Text(ctx, FullUi.ProfileName(i), s, 12.5f,
                                      Ui.Brush(ctx, active ? Tokens.AccentInk : Ui.InkCol));
                    }
                    if (Ui.ChipsShown)
                    {
                        for (int cell = 1; cell < Cells.Length; cell++)
                        {
                            var cr = Ui.MoreChipRect(cell);
                            bool pinned = Ui._pinned[cell];
                            bool locked = !FullUi.CanUnpin(cell);
                            var rr = new RoundedRectangle(new Vortice.RawRectF(cr.MinX, cr.MinY, cr.MaxX, cr.MaxY), 8f, 8f);
                            if (!locked && Ui._moreHover == MoreHitChip + cell)
                                ctx.FillRoundedRectangle(rr, Ui.Brush(ctx, Ui.HoverCol));
                            ctx.DrawRoundedRectangle(rr, Ui.Brush(ctx, pinned ? Tokens.Accent : Ui.BorderCol), 1f);
                            Ui.DrawCellIcon(ctx, cell, cr, Tokens.Icon,
                                         new Color4(Ui.InkCol.R, Ui.InkCol.G, Ui.InkCol.B, pinned ? 1f : 0.35f),
                                         pinned, Ui._host.State);
                            if (!pinned)
                                Ui._widgets.Text(ctx, "+", new RectF { MinX = cr.MaxX - 18f, MinY = cr.MinY + 2f, MaxX = cr.MaxX - 3f, MaxY = cr.MinY + 18f },
                                              12f, Ui.Brush(ctx, Ui.MutedCol));
                            if (locked) Ui.DrawLockBadge(ctx, cr);
                        }
                    }
                    else
                    {
                        Ui._widgets.Text(ctx, "挑工具：切到「自定义」档，这里就能钉 / 取消钉",
                                      new RectF
                                      {
                                          MinX = Ui.SettingsContentLeft(),
                                          MinY = Ui.MoreProfileRect(0).MaxY + MoreChipTopGap,
                                          MaxX = Ui.SettingsContentLeft() + Ui.SettingsContentW(),
                                          MaxY = Ui.MoreProfileRect(0).MaxY + MoreChipTopGap + 32f,
                                      },
                                      11.5f, Ui.Brush(ctx, Ui.MutedCol), center: false);
                    }
                }
                else
                {
                    foreach (var kind in SettingsRows(cat))
                        for (int i = 0; i < Rows.Length; i++)
                            if (Rows[i].Kind == kind) { Ui.DrawMoreRow(ctx, i); break; }
                }
            }

            ctx.PopAxisAlignedClip();
            if (Ui._settingsFocusVisible) Ui.DrawSettingsFocusRing(ctx);
            Ui.DrawSettingsScrollbar(ctx);
        }
    }

    private sealed class HelpPage : MorePageBase
    {
        public HelpPage(FullUi ui)
            : base(ui, "more.help", "使用说明", new PageSize(0f, ui.MoreHelpH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
        {
            for (int i = 0; i < HelpTabCount; i++)
            {
                var tab = Ui.HelpTabRect(i);
                bool active = i == Ui._helpTab;
                bool hot = Ui._moreHover == HelpHitTabBase + i;
                var rr = new RoundedRectangle(
                    new Vortice.RawRectF(tab.MinX, tab.MinY, tab.MaxX, tab.MaxY), 11f, 11f);
                if (active) ctx.FillRoundedRectangle(rr, Ui.Brush(ctx, Tokens.Accent));
                else if (hot) ctx.FillRoundedRectangle(rr, Ui.Brush(ctx, Ui.HoverCol));
                ctx.DrawRoundedRectangle(rr, Ui.Brush(ctx, active ? Tokens.Accent : Ui.BorderCol), 1f);
                Ui._widgets.Text(ctx, HelpTabNames[i], tab, HelpTabFont,
                              Ui.Brush(ctx, active ? Tokens.AccentInk : Ui.InkCol));
            }

            var viewport = Ui.HelpViewportRect();
            Ui._helpScroll = Math.Clamp(Ui._helpScroll, 0f, Ui.HelpMaxScroll());
            ctx.PushAxisAlignedClip(
                new Vortice.RawRectF(viewport.MinX, viewport.MinY, viewport.MaxX, viewport.MaxY),
                AntialiasMode.Aliased);
            var content = Ui.HelpFullContentRect();
            switch (Ui._helpTab)
            {
                case 0: Ui.DrawHelpQuick(ctx, content); break;
                case 1: Ui.DrawHelpGestures(ctx, content); break;
                default: Ui.DrawHelpShortcuts(ctx, content); break;
            }
            ctx.PopAxisAlignedClip();

            // 内容超过视口时，右侧给一条很轻的滚动提示；不抢面板内容。
            float maxScroll = Ui.HelpMaxScroll();
            if (maxScroll > 0.5f)
            {
                float trackX = viewport.MaxX + 5f;
                float h = viewport.MaxY - viewport.MinY;
                float thumbH = MathF.Max(28f, h * h / (h + maxScroll));
                float t = maxScroll <= 0.01f ? 0f : Ui._helpScroll / maxScroll;
                float top = viewport.MinY + (h - thumbH) * t;
                ctx.FillRoundedRectangle(
                    new RoundedRectangle(new Vortice.RawRectF(trackX, top, trackX + 3f, top + thumbH), 1.5f, 1.5f),
                    Ui.Brush(ctx, Ui._dark ? new Color4(1f, 1f, 1f, 0.28f) : new Color4(0f, 0f, 0f, 0.22f)));
            }
        }
    }

    private sealed class LogPage : MorePageBase
    {
        public LogPage(FullUi ui)
            : base(ui, "more.log", "日志与反馈", new PageSize(0f, ui.MoreLogH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
        {
            var st = Ui._host?.State;
            string note = st?.LogNote ?? "";
            bool editing = st?.LogNoteEditing ?? false;

            Ui._widgets.Text(ctx, "刚才遇到了什么问题？（选填）",
                new RectF
                {
                    MinX = Ui.MoreLeftX(), MinY = Ui.MoreHeaderRect().MaxY + 6f,
                    MaxX = Ui.LogLeftRight(), MaxY = Ui.MoreHeaderRect().MaxY + 26f,
                },
                12.5f, Ui.Brush(ctx, Ui.MutedCol), center: false);

            var nr = Ui.LogNoteRect();
            var nrr = new RoundedRectangle(new Vortice.RawRectF(nr.MinX, nr.MinY, nr.MaxX, nr.MaxY), 10f, 10f);
            ctx.FillRoundedRectangle(nrr, Ui.Brush(ctx, Ui.HoverCol));
            ctx.DrawRoundedRectangle(nrr,
                Ui.Brush(ctx, editing || Ui._moreHover == MoreHitLogNote ? Tokens.Accent : Ui.BorderCol),
                editing ? 1.6f : 1f);
            string noteText = editing
                ? "正在输入…（回车确认 / Esc 取消）"
                : (note.Length > 0 ? note : "例如：清屏后屏幕底部还剩一条黑线");
            Ui._widgets.TextLeftMiddle(ctx, noteText,
                new RectF { MinX = nr.MinX + 12f, MinY = nr.MinY, MaxX = nr.MaxX - 12f, MaxY = nr.MaxY },
                13.5f, Ui.Brush(ctx, note.Length > 0 || editing ? Ui.InkCol : Ui.MutedCol));

            string scope = Ui._logIncludeOlder
                ? "将上传：最多 4 份运行日志（≤8MB）、环境摘要、这段描述。"
                : "将上传：最近 2 份运行日志（≤4MB）、环境摘要、这段描述。";
            if (st?.LogUploadConsentOn != true) scope += " 首次上传会先征求同意。";
            Ui._widgets.TextLeftMiddle(ctx, scope, Ui.LogScopeRect(), 11f, Ui.Brush(ctx, Ui.MutedCol));

            var tr = Ui.LogToggleRect();
            Ui._widgets.TextLeftMiddle(ctx, "包含更早日志（最多 4 份 / 8MB）",
                new RectF { MinX = tr.MinX, MinY = tr.MinY, MaxX = tr.MaxX - MoreSwitchW - 12f, MaxY = tr.MaxY },
                12.5f, Ui.Brush(ctx, Ui.InkCol));
            var sw = new RectF
            {
                MinX = tr.MaxX - MoreSwitchW,
                MinY = tr.MinY + (tr.MaxY - tr.MinY - MoreSwitchH) * 0.5f,
                MaxX = tr.MaxX,
                MaxY = tr.MinY + (tr.MaxY - tr.MinY - MoreSwitchH) * 0.5f + MoreSwitchH,
            };
            Ui.DrawSwitch(ctx, sw, Ui._logIncludeOlder);

            var ur = Ui.LogUploadRect();
            var urr = new RoundedRectangle(new Vortice.RawRectF(ur.MinX, ur.MinY, ur.MaxX, ur.MaxY), 10f, 10f);
            ctx.FillRoundedRectangle(urr, Ui.Brush(ctx, Tokens.Accent));
            Ui._widgets.Text(ctx, "上传日志", ur, 14f, Ui.Brush(ctx, Tokens.AccentInk));

            var er = Ui.LogExportRect();
            var err = new RoundedRectangle(new Vortice.RawRectF(er.MinX, er.MinY, er.MaxX, er.MaxY), 10f, 10f);
            ctx.DrawRoundedRectangle(err,
                Ui.Brush(ctx, Ui._moreHover == MoreHitLogExport ? Tokens.Accent : Ui.BorderCol), 1f);
            Ui._widgets.Text(ctx, "只导出 zip", er, 12.5f,
                Ui.Brush(ctx, Ui._moreHover == MoreHitLogExport ? Tokens.Accent : Ui.InkCol));

            string status = !string.IsNullOrEmpty(Ui._hubHint) ? Ui._hubHint
                          : !string.IsNullOrEmpty(st?.InkStatus) ? (st?.InkStatus ?? "")
                          : "日志不会自动上传；上传成功后会显示编号。";
            Ui._widgets.TextLeftMiddle(ctx, status,
                new RectF
                {
                    MinX = Ui.MoreLeftX(), MinY = ur.MaxY + 10f,
                    MaxX = Ui.LogLeftRight(), MaxY = ur.MaxY + 10f + MoreStatusH,
                },
                11f, Ui.Brush(ctx, Ui.MutedCol));

            // 右侧二维码卡片（2026-11-03）：一直可见，学生拿手机就能扫；点它整屏放大。
            Ui.DrawLogQrCard(ctx);

            // 加群反馈（2026-11-03）：整行可点，点一下把群号复制走——触摸屏不用手抄。
            var gr = Ui.LogGroupRect();
            var grr = new RoundedRectangle(new Vortice.RawRectF(gr.MinX, gr.MinY, gr.MaxX, gr.MaxY), 10f, 10f);
            bool groupHover = Ui._moreHover == MoreHitLogGroup;
            bool urlHover = Ui._moreHover == MoreHitLogGroupUrl;
            bool anyHover = groupHover || urlHover;
            ctx.FillRoundedRectangle(grr, Ui.Brush(ctx, Ui.HoverCol));
            ctx.DrawRoundedRectangle(grr, Ui.Brush(ctx, anyHover ? Tokens.Accent : Ui.BorderCol),
                                     anyHover ? 1.5f : 1f);
            var cr = Ui.LogGroupCopyRect();
            var lr = Ui.LogGroupUrlRect();
            Ui._widgets.TextLeftMiddle(ctx, $"遇到问题？加 QQ 群 {ProductInfo.FeedbackQQGroup}",
                new RectF { MinX = gr.MinX + 12f, MinY = gr.MinY, MaxX = cr.MinX - 10f, MaxY = gr.MaxY },
                12.5f, Ui.Brush(ctx, Ui.InkCol));
            Ui._widgets.Text(ctx, "复制群号", cr, 11.5f,
                          Ui.Brush(ctx, groupHover ? Tokens.Accent : Ui.MutedCol));
            Ui._widgets.Text(ctx, "复制链接", lr, 11.5f,
                          Ui.Brush(ctx, urlHover ? Tokens.Accent : Ui.MutedCol));
        }
    }

    private sealed class ApiPage : MorePageBase
    {
        public ApiPage(FullUi ui)
            : base(ui, "more.api", "AI 服务", new PageSize(0f, ui.MoreApiH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
            => Ui.DrawApiPage(ctx);
    }
}
