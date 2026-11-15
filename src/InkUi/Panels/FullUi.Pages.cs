// [职责] 更多面板的各页（启动器 / 设置 / 说明 / 日志 / AI 服务）：绘制与命中；
//        含启动器下方那一条「最近文档」。
// [测试] --paneltest

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

    // ---- 「最近文档」（2026-11-15）--------------------------------------------
    //
    // 位置：墨迹组下面、状态行上面的一条。0 条时**整条不出现**（高度 0，面板不变）；
    // 1～3 条按行堆叠，行高 34、行距 4（3 行 = 110）；**不做内部滚动**，多出来的
    // 只显示最新 3 条（右上角一句"显示最新 3 份"）。
    //
    // 标题不额外占高度：它画在原来「墨迹组 → 状态行」那条 `MoreGroupGap` 的缝里，
    // 所以 3 行时整块只比原来高 110 —— 和需求里的空间预算对齐。

    /// <summary>墨迹组最后一格的下沿（「最近文档」从这里往下排）。</summary>
    private float HubInkBottomY() => HubHeadRect(1).MaxY + HubRows(1) * (MoreTile + MoreTileGap);

    /// <summary>这一条占的高度（0 条 = 0）。标题借组距那条缝，不算进来。</summary>
    private float RecentDocsBlockH()
    {
        int n = RecentDocsShown;
        return n <= 0 ? 0f : n * MoreRecentRowH + (n - 1) * MoreRecentRowGap;
    }

    /// <summary>最后一行和状态行之间留一点气（0 条时不占）。</summary>
    private float RecentDocsBottomGap => RecentDocsBlockH() > 0f ? 4f : 0f;

    private RectF RecentDocsHeadRect()
    {
        float y = HubInkBottomY();
        return new RectF
        {
            MinX = MoreLeftX(), MinY = y,
            MaxX = MoreLeftX() + MoreContentW(), MaxY = y + MoreGroupGap,
        };
    }

    private RectF RecentRowRect(int i)
    {
        float y = HubInkBottomY() + MoreGroupGap + i * (MoreRecentRowH + MoreRecentRowGap);
        return new RectF
        {
            MinX = MoreLeftX(), MinY = y,
            MaxX = MoreLeftX() + MoreContentW(), MaxY = y + MoreRecentRowH,
        };
    }

    // 「最近文档」的可见列表缓存：File.Exists 是磁盘调用，不能在每一帧的绘制/命中里
    // 各扫一遍（网络盘/杀软下会拖慢面板）。偏好一变（打开新文档/测试种数据）或过了
    // 2 秒才重扫；文件刚被删掉时最多多显示 2 秒，点它会被引擎兜底成"文件已经不在了"。
    private string _recentDocsCacheRaw;
    private List<RecentDoc> _recentDocsCache;
    private double _recentDocsCacheAtMs;

    /// <summary>读偏好里的最近文档，**跳过文件已经不在了的**（不显示、不报错）。</summary>
    private List<RecentDoc> RecentDocsVisible()
    {
        string raw = _host?.GetPref(RecentDocs.PrefKey) ?? "";
        double now = _host?.NowMs ?? 0;
        if (_recentDocsCache != null && raw == _recentDocsCacheRaw
            && now - _recentDocsCacheAtMs < 2000)
            return _recentDocsCache;

        var all = RecentDocs.Parse(raw);
        var live = new List<RecentDoc>(all.Count);
        foreach (var d in all)
        {
            if (d == null || d.Paths.Length == 0) continue;
            foreach (var p in d.Paths)
            {
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) { live.Add(d); break; }
            }
        }
        _recentDocsCacheRaw = raw;
        _recentDocsCacheAtMs = now;
        _recentDocsCache = live;
        return live;
    }

    private static string RecentDocTitle(RecentDoc d)
    {
        if (d == null || d.Paths.Length == 0) return "";
        string name = Path.GetFileName(d.PrimaryPath);
        return d.Paths.Length > 1 ? $"{name} 等 {d.Paths.Length} 个文件" : name;
    }

    /// <summary>相对时间：今天 HH:mm / 昨天 / MM-dd（跨年补年份）。</summary>
    private static string RecentTimeText(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime(); }
        catch { return ""; }
        var now = DateTimeOffset.Now;
        if (at.Date == now.Date) return $"今天 {at:HH:mm}";
        if (at.Date == now.Date.AddDays(-1)) return "昨天";
        return at.Year == now.Year
            ? at.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>文件类型小徽标：PDF 红 / PPT 橙 / 图片蓝（跟主色一套，不引新颜色）。</summary>
    private static (Color4 Fill, Color4 Ink, string Label) RecentBadgeStyle(string path)
    {
        var ext = Path.GetExtension(path ?? "");
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            return (new Color4(0.85f, 0.22f, 0.22f, 0.16f), new Color4(0.80f, 0.17f, 0.17f, 1f), "PDF");
        if (string.Equals(ext, ".ppt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".pptx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".ppsx", StringComparison.OrdinalIgnoreCase))
            return (new Color4(0.92f, 0.48f, 0.12f, 0.16f), new Color4(0.86f, 0.42f, 0.08f, 1f), "PPT");
        return (new Color4(Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, 0.14f), Tokens.Accent, "图");
    }

    private void DrawRecentDocs(ID2D1DeviceContext ctx)
    {
        var live = RecentDocsVisible();
        int n = Math.Min(RecentDocsShown, live.Count);
        if (n <= 0) return;

        var head = RecentDocsHeadRect();
        DrawMoreGroupHead(ctx, head, "最近文档");
        if (live.Count > n)
            _widgets.Text(ctx, $"显示最新 {n} 份",
                          new RectF { MinX = head.MaxX - 120f, MinY = head.MinY, MaxX = head.MaxX, MaxY = head.MaxY },
                          11f, Brush(ctx, MutedCol), center: false);

        for (int i = 0; i < n; i++)
        {
            var row = RecentRowRect(i);
            var doc = live[i];
            bool hot = _moreHover == MoreHitRecent + i;

            var rr = new RoundedRectangle(
                new Vortice.RawRectF(row.MinX, row.MinY, row.MaxX, row.MaxY), 8f, 8f);
            // 每行垫一层很轻的槽底（和滑条/设置页同一个语言），不开悬停也看得出是三条。
            ctx.FillRoundedRectangle(rr, Brush(ctx, _dark ? Tokens.TroughDark : Tokens.TroughLight));
            if (hot)
            {
                ctx.FillRoundedRectangle(rr, Brush(ctx, HoverCol));
                ctx.DrawRoundedRectangle(rr,
                    Brush(ctx, new Color4(Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B, 0.55f)), 1f);
            }

            // 类型徽标：24×18，垂直居中。
            var badge = new RectF
            {
                MinX = row.MinX + 8f,
                MinY = row.MinY + (MoreRecentRowH - 18f) * 0.5f,
                MaxX = row.MinX + 32f,
                MaxY = row.MinY + (MoreRecentRowH - 18f) * 0.5f + 18f,
            };
            var (fill, ink, label) = RecentBadgeStyle(doc.PrimaryPath);
            ctx.FillRoundedRectangle(
                new RoundedRectangle(new Vortice.RawRectF(badge.MinX, badge.MinY, badge.MaxX, badge.MaxY), 5f, 5f),
                Brush(ctx, fill));
            _widgets.Text(ctx, label, badge, 9.5f, Brush(ctx, ink));

            // 右侧时间：右对齐；名字拿剩下的宽度，放不下就省略号（提示里有全名）。
            string time = RecentTimeText(doc.OpenedAtUnixSeconds);
            float timeW = time.Length == 0 ? 0f : MathF.Min(96f, _widgets.Measure(time, 11f) + 2f);
            float nameLeft = badge.MaxX + 8f;
            float nameRight = row.MaxX - 10f - (timeW > 0f ? timeW + 8f : 0f);
            if (nameRight < nameLeft + 40f) nameRight = nameLeft + 40f;

            string name = Ellipsize(RecentDocTitle(doc), nameRight - nameLeft, 12.5f);
            _widgets.TextLeftMiddle(ctx, name,
                new RectF { MinX = nameLeft, MinY = row.MinY, MaxX = nameRight, MaxY = row.MaxY },
                12.5f, Brush(ctx, InkCol));

            if (timeW > 0f)
                _widgets.Text(ctx, time,
                    new RectF { MinX = row.MaxX - 10f - timeW, MinY = row.MinY, MaxX = row.MaxX - 8f, MaxY = row.MaxY },
                    11f, Brush(ctx, MutedCol));
        }
    }

    /// <summary>单行省略（中文按字截）：放不下就二分找最长的可显示前缀 + "…"。</summary>
    private readonly Dictionary<(string Text, int Width, int Size), string> _ellipsisCache = new();

    private string Ellipsize(string text, float width, float size)
    {
        if (string.IsNullOrEmpty(text) || width <= 4f) return "";
        int wKey = (int)MathF.Round(width);
        int sKey = (int)MathF.Round(size * 100f);
        var key = (text, wKey, sKey);
        if (_ellipsisCache.TryGetValue(key, out var cached)) return cached;

        string fitted = text;
        if (_widgets.Measure(text, size) > width)
        {
            const string tail = "…";
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                int cut = mid;
                // 别把 emoji / 生僻字（代理对）劈成两半
                if (cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1])) cut--;
                if (_widgets.Measure(text[..cut] + tail, size) <= width) lo = mid;
                else hi = mid - 1;
            }
            int take = lo;
            if (take > 0 && take < text.Length && char.IsHighSurrogate(text[take - 1])) take--;
            fitted = take <= 0 ? tail : text[..take] + tail;
        }

        if (_ellipsisCache.Count > 256) _ellipsisCache.Clear();
        _ellipsisCache[key] = fitted;
        return fitted;
    }

    /// <summary>点「最近文档」某一行：关面板 → 按完整路径直接重开（文件没了由引擎兜底提示）。</summary>
    private void ActivateRecent(int i)
    {
        var live = RecentDocsVisible();
        if (i < 0 || i >= live.Count) return;
        var paths = live[i].Paths;
        CloseMore();
        _host?.Commands.OpenRecentDocument(paths);
        Invalidate();
    }

    // ---- 「最近文档」自检 / 出图钩子 -----------------------------------------

    internal float MoreHomeHForTest => MoreHomeH();
    internal int RecentDocsShownForTest => RecentDocsShown;
    internal RectF RecentRowRectForTest(int i) => RecentRowRect(i);
    internal int MoreHitRecentBaseForTest => MoreHitRecent;

    /// <summary>自检用：单行省略的结果（长中文文件名不许溢出）。</summary>
    internal string EllipsizeForTest(string text, float width, float size) => Ellipsize(text, width, size);

    /// <summary>
    /// 自检/出图用：直接写一条「最近文档」偏好（不真打开文件）。每条 path 算一份，
    /// 时间依次往前推一天——出图能同时看到"今天 / 昨天 / 日期"三种写法。
    /// 不传 = 清空（回到 0 条）。
    /// </summary>
    internal void SeedRecentDocsForTest(params string[] paths)
    {
        if (_host == null) return;
        if (paths == null || paths.Length == 0)
        {
            _host.SetPref(RecentDocs.PrefKey, null);
            Invalidate();
            return;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var list = new List<RecentDoc>(paths.Length);
        for (int i = 0; i < paths.Length; i++)
            list.Add(new RecentDoc(now - i * 86400L, new[] { paths[i] }));
        _host.SetPref(RecentDocs.PrefKey, RecentDocs.Format(list));
        Invalidate();
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
        public virtual PageSize PreferredSize => _size;
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
            int code = HitCode(x, y);
            if (code == MoreHitNone) return PageHit.None;
            var t = Ui.HitTargetFromLegacy(code);
            return new PageHit(PageHitKind.Action, t.ActionId, code);
        }

        /// <summary>页面内容命中；返回旧整数码（A3.3 的兼容层）。</summary>
        protected abstract int HitCode(float x, float y);

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

        /// <summary>「最近文档」条数变了，面板高度跟着变：每次布局都重新问，
        /// 不用记构造时那一份（否则打开过文档之后面板还停在旧高度）。</summary>
        public override PageSize PreferredSize => new PageSize(0f, Ui.MoreHomeH());

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

            // 最近文档：墨迹组下面、状态行上面（0 条时整条不出现）
            Ui.DrawRecentDocs(ctx);

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

        protected override int HitCode(float x, float y)
        {
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < Ui.HubCount(s); i++)
                    if (Ui.HubTileRect(s, i).Contains(x, y))
                        return MoreHitTile + Ui.HubTileCode(s, i);
            // 最近文档（只认真正画出来的那几行）
            int recent = Math.Min(Ui.RecentDocsShown, Ui.RecentDocsVisible().Count);
            for (int i = 0; i < recent; i++)
                if (Ui.RecentRowRect(i).Contains(x, y))
                    return MoreHitRecent + i;
            for (int i = 0; i < MoreBottomTiles.Length; i++)
                if (Ui.HubBottomRect(i).Contains(x, y))
                    return MoreHitBottom + i;
            return MoreHitInside;
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
                        for (int oi = 1; oi < CellOrder.Length; oi++)
                        {
                            int cell = CellOrder[oi];
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

        protected override int HitCode(float x, float y)
        {
            int nav = Ui.SettingsNavAt(x, y);
            if (nav >= 0) return MoreHitSettingsNav + nav;

            // 滚动条在内容右侧的留白里（不在视口矩形内），要先判它。
            if (Ui.SettingsScrollMax() > 0.5f && Ui.SettingsScrollbarHitRect().Contains(x, y))
                return MoreHitSettingsScroll;

            // 右侧是一整页连续内容：先限制在视口里，再按滚动后的矩形命中。
            if (!Ui.SettingsViewportRect().Contains(x, y)) return MoreHitInside;

            for (int i = 0; i < 3; i++)
                if (Ui.MoreProfileRect(i).Contains(x, y)) return MoreHitProfile + i;
            if (Ui.ChipsShown)
                for (int oi = 1; oi < CellOrder.Length; oi++)
                {
                    int cell = CellOrder[oi];
                    if (FullUi.CanUnpin(cell) && Ui.MoreChipRect(cell).Contains(x, y))
                        return MoreHitChip + cell;
                }

            for (int i = 0; i < Rows.Length; i++)
                if (Ui.MoreRowRect(i).Contains(x, y)) return MoreHitRow + i;
            return MoreHitInside;
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

        protected override int HitCode(float x, float y)
        {
            for (int i = 0; i < HelpTabCount; i++)
                if (Ui.HelpTabRect(i).Contains(x, y)) return HelpHitTabBase + i;
            return MoreHitInside;
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

        protected override int HitCode(float x, float y)
        {
            if (Ui.LogNoteRect().Contains(x, y)) return MoreHitLogNote;
            if (Ui.LogUploadRect().Contains(x, y)) return MoreHitLogUpload;
            if (Ui.LogExportRect().Contains(x, y)) return MoreHitLogExport;
            if (Ui.LogToggleRect().Contains(x, y)) return MoreHitLogOlder;
            if (Ui.LogQrCardRect().Contains(x, y)) return MoreHitLogQr;
            if (Ui.LogGroupUrlRect().Contains(x, y)) return MoreHitLogGroupUrl;
            if (Ui.LogGroupRect().Contains(x, y)) return MoreHitLogGroup;
            return MoreHitInside;
        }
    }

    private sealed class ApiPage : MorePageBase
    {
        public ApiPage(FullUi ui)
            : base(ui, "more.api", "AI 服务", new PageSize(0f, ui.MoreApiH()), canBack: true) { }

        public override void Render(ID2D1DeviceContext ctx, in PageFrame frame)
            => Ui.DrawApiPage(ctx);

        protected override int HitCode(float x, float y) => Ui.ApiHitAt(x, y);
    }
}
