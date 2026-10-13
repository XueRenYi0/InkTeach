using Vortice.Mathematics;

namespace InkEngine;

/// <summary>回放控制条上点到了哪一块。</summary>
internal enum ReplayBarZone
{
    None = 0,
    PlayPause,
    SpeedHalf,      // 0.5×
    Speed1,
    Speed2,
    Speed4,
    Progress,
    Close,
}

/// <summary>
/// 墨迹回放的控制条几何（照 `PptBar` 的写法：**这个类只有几何**，
/// 状态在 <see cref="InkEngine"/>，绘制在 `Overlay`）。
///
/// 布局：`[▶/⏸] [0.5×][1×][2×][4×] [━━●━━] 已播/总长 [✕]`
///
/// **默认贴屏幕底部居中**：播放器的按钮就该在手边——学校大屏上个子低的老师
/// 够不到屏幕顶部（用户 2026-10-01 提的）。主条默认也贴底部居中，所以引擎侧
/// （<c>InkEngine.ReplayBarRect</c>）会在"两者真叠上"的时候把整条**抬到主条上方**：
/// 主条在回放期间不会动（点它 = 退出回放），所以这个避让是稳定的。
/// 尺度与命中都乘 DPI（物理像素）——和 PPT 条、选中操作条同一套规矩。
/// </summary>
internal static class ReplayBar
{
    public const float BarH = 44f;
    public const float Pad = 8f;
    public const float Gap = 6f;
    public const float PlayW = 44f;
    public const float SpeedW = 50f;
    public const float TimeW = 132f;       // "02:14 / 05:41" 放得下
    public const float CloseW = 40f;
    public const float BottomMargin = 20f;    // 离屏幕下边

    public static float BarW(in RectF screen, float dpi)
    {
        float w = (screen.MaxX - screen.MinX) * 0.58f;
        return Math.Clamp(w, 520f * dpi, 760f * dpi);
    }

    /// <summary>控制条矩形（物理像素）。x 居中、贴屏幕**下边**（避让主条由引擎那一层做）。</summary>
    public static RectF RectAt(in RectF screen, float dpi)
    {
        float w = BarW(screen, dpi), h = BarH * dpi;
        float x = (screen.MinX + screen.MaxX) * 0.5f - w * 0.5f;
        float y = screen.MaxY - BottomMargin * dpi - h;
        return new RectF { MinX = x, MinY = y, MaxX = x + w, MaxY = y + h };
    }

    /// <summary>进度轨道（含滑钮能走到的那一段），物理像素。</summary>
    public static RectF ProgressTrack(in RectF bar, float dpi)
    {
        float left = bar.MinX + Pad * dpi + PlayW * dpi + Gap * dpi + 4f * (SpeedW + Gap) * dpi;
        float right = bar.MaxX - Pad * dpi - CloseW * dpi - Gap * dpi - TimeW * dpi - Gap * dpi;
        float cy = (bar.MinY + bar.MaxY) * 0.5f;
        float half = 4f * dpi;                    // 轨道半高
        return new RectF { MinX = left, MinY = cy - half, MaxX = right, MaxY = cy + half };
    }

    /// <summary>某一块的矩形（画与命中同源：绘制、悬停、点击都问它）。</summary>
    public static RectF ZoneRect(in RectF bar, ReplayBarZone zone, float dpi)
    {
        float top = bar.MinY, bottom = bar.MaxY;      // 先把值拿出来：in 参数不能进本地函数
        if (zone == ReplayBarZone.PlayPause)
            return new RectF { MinX = bar.MinX + Pad * dpi, MinY = top,
                               MaxX = bar.MinX + (Pad + PlayW) * dpi, MaxY = bottom };
        if (zone == ReplayBarZone.Close)
            return new RectF { MinX = bar.MaxX - (Pad + CloseW) * dpi, MinY = top,
                               MaxX = bar.MaxX - Pad * dpi, MaxY = bottom };
        float p = bar.MinX + (Pad + PlayW + Gap) * dpi;
        for (int i = 0; i < 4; i++)
        {
            if ((ReplayBarZone)(ReplayBarZone.SpeedHalf + i) == zone)
                return new RectF { MinX = p, MinY = top, MaxX = p + SpeedW * dpi, MaxY = bottom };
            p += (SpeedW + Gap) * dpi;
        }
        // 其余都算进度区（和 ZoneAt 一致）
        var t = ProgressTrack(bar, dpi);
        return new RectF { MinX = t.MinX, MinY = top,
                           MaxX = bar.MaxX - (Pad + CloseW + Gap + TimeW + Gap) * dpi, MaxY = bottom };
    }

    public static ReplayBarZone ZoneAt(in RectF bar, float x, float y, float dpi)
    {
        if (!bar.Contains(x, y)) return ReplayBarZone.None;
        float p = bar.MinX + Pad * dpi;
        if (x < p + PlayW * dpi) return ReplayBarZone.PlayPause;
        p += PlayW * dpi + Gap * dpi;
        for (int i = 0; i < 4; i++)
        {
            if (x < p + SpeedW * dpi) return (ReplayBarZone)(ReplayBarZone.SpeedHalf + i);
            p += SpeedW * dpi + Gap * dpi;
        }
        float right = bar.MaxX - Pad * dpi - CloseW * dpi;
        if (x >= right) return ReplayBarZone.Close;
        // 时间文字那一块不响应（它和进度条同属"读"的区域）
        return ReplayBarZone.Progress;
    }

    /// <summary>进度条上的 x → 0..1。</summary>
    public static float Progress01(in RectF bar, float x, float dpi)
    {
        var t = ProgressTrack(bar, dpi);
        if (t.MaxX <= t.MinX) return 0f;
        return Math.Clamp((x - t.MinX) / (t.MaxX - t.MinX), 0f, 1f);
    }

    public static float SpeedOfZone(ReplayBarZone z) => z switch
    {
        ReplayBarZone.SpeedHalf => 0.5f,
        ReplayBarZone.Speed2 => 2f,
        ReplayBarZone.Speed4 => 4f,
        _ => 1f,
    };

    public static ReplayBarZone ZoneOfSpeed(float speed) => speed switch
    {
        < 0.75f => ReplayBarZone.SpeedHalf,
        < 1.5f => ReplayBarZone.Speed1,
        < 3f => ReplayBarZone.Speed2,
        _ => ReplayBarZone.Speed4,
    };

    public static string SpeedName(float speed) => speed switch
    {
        < 0.75f => "0.5×",
        < 1.5f => "1×",
        < 3f => "2×",
        _ => "4×",
    };

    /// <summary>"02:14" 这种读数（毫秒 → mm:ss；超过一小时给 h:mm:ss）。</summary>
    public static string TimeText(float ms)
    {
        if (ms < 0f) ms = 0f;
        int total = (int)(ms / 1000f + 0.5f);
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m:D2}:{s:D2}";
    }
}

/// <summary>
/// **回放时间轴**（纯数据：每条笔画的起点与时长，毫秒）。不碰渲染、不碰窗口，
/// 自检可以直接钉它。
///
/// 生成口径（用户 2026-10-01 拍板，见 计划 6.4.3）：
///   · 主顺序 = **文档顺序**（跨会话存档的时间戳可能不单调，顺序不能靠时间戳排）；
///   · 每条时长 = `clamp(末点T − 首点T, 120ms, 20s)`——**按当时速度**（这一条是回放的本意）；
///   · 笔间停顿 = `clamp(下条首点T − 本笔末点T, 0, 700ms)`：写一个字、想一会儿再写下
///     一个，**那口"想事情的气"压成一个节拍**（700ms），而不是原样播 2 秒空等
///     （2026-10-01 用户实测："字与字之间等太久，播放不用等那么长时间"）；
///     同字内笔画间的小间隔（几十到几百毫秒）原样保留，书写节奏不变。时间戳往回跳
///     （跨会话）退化成一个 120ms 的短拍；
///   · 单点笔迹（点一下）也给 120ms 的最小可播时长；
///   · **第一笔从 0ms 开始**（没有前置等待）。
/// </summary>
internal sealed class ReplayTimeline
{
    public const float MinDurMs = 120f;        // 单点/极短笔迹的最小可播时长
    public const float MaxDurMs = 20000f;
    /// <summary>笔间停顿的上限 = **一个节拍**：超过它的"想事情的气"一律压到这么多。
    /// 700 是照"说一句话的停顿"取的（≈0.7 秒）；要更快/更慢只改这一个数。</summary>
    public const float MaxGapMs = 700f;
    public const float FallbackGapMs = 120f;   // 时间戳非单调时的"短拍"

    public readonly float[] Start;
    public readonly float[] Duration;
    public readonly float Total;

    private ReplayTimeline(float[] start, float[] dur, float total)
    {
        Start = start;
        Duration = dur;
        Total = total;
    }

    public static ReplayTimeline Build(IReadOnlyList<Stroke> strokes)
    {
        int n = strokes.Count;
        var start = new float[n];
        var dur = new float[n];
        float cursor = 0f;
        float prevEndT = 0f;
        for (int i = 0; i < n; i++)
        {
            var s = strokes[i];
            float t0 = s.Points.Count > 0 ? (float)s.Points[0].T : 0f;
            float t1 = s.Points.Count > 0 ? (float)s.Points[^1].T : t0;
            float d = Math.Clamp(t1 - t0, MinDurMs, MaxDurMs);
            if (i > 0)
            {
                float delta = t0 - prevEndT;
                cursor += delta < 0f ? FallbackGapMs : Math.Clamp(delta, 0f, MaxGapMs);
            }
            start[i] = cursor;
            dur[i] = d;
            cursor += d;
            prevEndT = t1;
        }
        return new ReplayTimeline(start, dur, cursor);
    }

    /// <summary>t 时刻"完全出完"的条数。</summary>
    public int DoneCount(float t)
    {
        int n = Start.Length, done = 0;
        while (done < n && Start[done] + Duration[done] <= t) done++;
        return done;
    }

    /// <summary>
    /// t 时刻"正在长"的那一条：返回下标；<paramref name="param"/> = 它长了多少（0..1）。
    /// 没有正在长的（还没开始 / 全部出完）时返回 -1。
    /// </summary>
    public int Current(float t, out float param)
    {
        int i = DoneCount(t);
        param = 0f;
        if (i >= Start.Length) return -1;
        if (t < Start[i]) return -1;
        float d = MathF.Max(1f, Duration[i]);
        param = Math.Clamp((t - Start[i]) / d, 0f, 1f);
        return i;
    }

    /// <summary>
    /// 位置公式（纯函数）：锚点 ＋ 过了多少真实时间 × 倍速。
    /// 单独抽出来是为了让自检**不依赖墙上时钟**也能钉"2× 走两倍"。
    /// </summary>
    public static float PositionAt(float anchorPos, double anchorClockMs, double nowMs, float speed)
        => anchorPos + (float)((nowMs - anchorClockMs) * speed);
}

/// <summary>
/// 回放会话（墨迹 C）：状态、时间轴推进、控制条输入、以及"编辑操作先退场"。
///
/// 设计要点（见 计划 6.4.3）：
///   · **只读**：不碰文档 / 撤销栈 / 选中；"出一条画一条"靠一张**影子文档**
///     （**共享 Stroke 引用、不拷贝点位**）走现有的分块缓存——多窗口、DPI、板色全白拿，
///     只有"正在长的那一条"走浮动层的前缀几何；
///   · **相机锁定**：回放范围 = 开始时那一屏；编辑 / 翻页 / 开穿透 = 先退出回放；
///   · 开始回放会**顺手关掉穿透**（不然控制条点不到；和"换工具自动关穿透"同一条先例）。
/// </summary>
public partial class InkEngine
{
    private ReplayTimeline _replay;
    private InkDocument _replayDoc;                      // 已出完笔画的影子文档
    private readonly List<Stroke> _replayStrokes = new();
    private int _replayDone;
    private float _replayPosMs;
    private bool _replayPlaying;
    private float _replayAnchorPos;
    private double _replayAnchorClock;
    private float _replaySpeed = 1f;
    private int _replayCurrentIndex = -1;                // 正在长的那一条（-1 = 没有）
    private float _replayCurrentParam;
    private ReplayBarZone _replayHover = ReplayBarZone.None;
    private bool _replayScrubbing;
    private bool _replayCapturing;                       // 这一次按下归控制条（松手由它收尾）

    /// <summary>编辑类命令的闸门：回放中点编辑 = **先收掉回放**，再执行原命令。</summary>
    private void ExitReplayForEdit(string what)
    {
        if (_replay != null) StopReplay(what);
    }

    internal bool ReplayActive => _replay != null;
    internal bool ReplayPlaying => _replayPlaying;
    internal float ReplaySpeed => _replaySpeed;

    /// <summary>内容层该渲染哪份文档：回放中是影子文档，平时就是真文档。</summary>
    internal InkDocument RenderDoc => _replayDoc ?? Doc;

    internal IReadOnlyList<Stroke> ReplayStrokesNow => _replayStrokes;
    internal int ReplayCurrentIndexNow => _replayCurrentIndex;
    internal float ReplayCurrentParamNow => _replayCurrentParam;
    internal float ReplayPosMsNow => _replayPosMs;
    internal float ReplayTotalMsNow => _replay?.Total ?? 0f;

    internal void StartReplayFromUi() => StartReplay();
    internal void StopReplayFromUi() => StopReplay("界面");

    /// <summary>
    /// 开始回放（白板 = 当前一屏；放映中 = 当前这一页）。没有可回放的笔迹就什么都不做。
    /// </summary>
    internal void StartReplay()
    {
        if (_replay != null) return;
        if (CaptureActive) return;                       // 截图取景中不许开

        // 放映中：PPT 条的菜单 / 页号面板先收掉，免得它们飘在回放上面
        //（两条互不挡：回放控制条在底部居中、PPT 条在左下）。
        if (PptMode)
        {
            PptMenuOpen = false;
            PptPagePanelOpen = false;
            PptClearConfirm = false;
        }

        var vp = ViewportCanvas;
        _replayStrokes.Clear();
        foreach (var s in Doc.Strokes)
            if (s.PaddedBounds.Intersects(vp)) _replayStrokes.Add(s);
        if (_replayStrokes.Count == 0)
        {
            Console.WriteLine("[回放] 这一屏没有可回放的墨迹");
            return;
        }

        // 回放是"看"的模式：会打架的东西先收掉（都走现成的安全路径）
        if (PassThrough) SetPassThroughFromUi(false);
        if (LibraryPanelOpen) CloseLibraryPanel();
        if (RadialPaletteActive) CancelRadialPalette("回放");
        Doc.Selected.Clear();

        _replay = ReplayTimeline.Build(_replayStrokes);
        _replayDoc = new InkDocument();
        _replayDoc.InvalidateAll();
        _replayDone = 0;
        _replayPosMs = 0f;
        _replaySpeed = 1f;
        _replayPlaying = true;
        _replayAnchorPos = 0f;
        _replayAnchorClock = NowMs;
        _replayCurrentIndex = -1;
        _replayCurrentParam = 0f;
        UpdateReplayCurrent();
        _replayHover = ReplayBarZone.None;
        _replayScrubbing = false;
        foreach (var w in _windows) w.ForceContentRebuild();
        _dirty = true;
        Console.WriteLine($"[回放] 开始：{(PptMode ? "当前页" : "当前一屏")} {_replayStrokes.Count} 条，"
                          + $"总长 {ReplayBar.TimeText(_replay.Total)}");
        NotifyUiStateChanged();
    }

    internal void StopReplay(string why)
    {
        if (_replay == null) return;
        _replay = null;
        _replayDoc = null;
        _replayStrokes.Clear();
        _replayPlaying = false;
        _replayCurrentIndex = -1;
        _replayScrubbing = false;
        foreach (var w in _windows) w.ForceContentRebuild();
        _dirty = true;
        Console.WriteLine($"[回放] 结束（{why}）");
        NotifyUiStateChanged();
    }

    internal void ReplayTogglePause()
    {
        if (_replay == null) return;
        if (_replayPlaying)
        {
            _replayPosMs = ReplayTimeline.PositionAt(_replayAnchorPos, _replayAnchorClock, NowMs, _replaySpeed);
            if (_replayPosMs > _replay.Total) _replayPosMs = _replay.Total;
            _replayPlaying = false;
        }
        else
        {
            if (_replayPosMs >= _replay.Total - 0.5f) ReplaySeek(0f);   // 播完再点 = 从头
            _replayAnchorPos = _replayPosMs;
            _replayAnchorClock = NowMs;
            _replayPlaying = true;
        }
        UpdateReplayCurrent();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void ReplaySetSpeed(float speed)
    {
        if (_replay == null) return;
        // 切倍速前先把当前位置冻结，再从这往后按新速度走（不然会"跳一下"）
        if (_replayPlaying)
            _replayPosMs = ReplayTimeline.PositionAt(_replayAnchorPos, _replayAnchorClock, NowMs, _replaySpeed);
        _replaySpeed = speed;
        _replayAnchorPos = _replayPosMs;
        _replayAnchorClock = NowMs;
        UpdateReplayCurrent();
        _dirty = true;
        NotifyUiStateChanged();
    }

    internal void ReplaySeek(float t)
    {
        if (_replay == null) return;
        t = Math.Clamp(t, 0f, _replay.Total);
        if (t < _replayPosMs)
        {
            // 往回：影子文档重造（共享引用，丢弃旧的不会伤到笔迹），内容层整层重铺
            _replayDoc = new InkDocument();
            _replayDone = 0;
            foreach (var w in _windows) w.ForceContentRebuild();
        }
        _replayPosMs = t;
        _replayAnchorPos = t;
        _replayAnchorClock = NowMs;
        AdvanceReplay();
        UpdateReplayCurrent();
        _dirty = true;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 刷新"正在长的那一条"（seek / 暂停 / 切倍速之后要**立刻**对，不能等下一帧——
    /// 自检和界面读的都是这一刻的状态）。
    /// </summary>
    private void UpdateReplayCurrent()
    {
        if (_replay == null) { _replayCurrentIndex = -1; _replayCurrentParam = 0f; return; }
        _replayCurrentIndex = _replay.Current(_replayPosMs, out float p);
        _replayCurrentParam = p;
    }

    /// <summary>按当前时刻推进"出完了哪几条"（出完的加进影子文档，走分块缓存的补画快路径）。</summary>
    private void AdvanceReplay()
    {
        while (_replayDone < _replayStrokes.Count)
        {
            float end = _replay.Start[_replayDone] + _replay.Duration[_replayDone];
            if (_replayPosMs < end) break;
            _replayDoc.AppendStroke(_replayStrokes[_replayDone]);
            _replayDone++;
        }
    }

    /// <summary>每帧推一次（只算"现在在哪"，不搬像素）。多窗口下是幂等的。</summary>
    internal void TickReplay()
    {
        if (_replay == null) return;
        if (_replayPlaying)
        {
            float pos = ReplayTimeline.PositionAt(_replayAnchorPos, _replayAnchorClock, NowMs, _replaySpeed);
            if (pos >= _replay.Total)
            {
                pos = _replay.Total;
                _replayPlaying = false;
                _replayAnchorPos = pos;
                NotifyUiStateChanged();
            }
            _replayPosMs = pos;
        }
        AdvanceReplay();
        UpdateReplayCurrent();
    }

    internal RectF ReplayBarRect()
    {
        var screen = ScreenRectPhysical();
        var bar = ReplayBar.RectAt(screen, DpiScale);
        // 和主条（默认也贴底部居中）真叠上了就整条抬到主条上方——**够得着优先**
        //（用户 2026-10-01："个子低的老师够不到顶上"）。主条回放期间不会动，避让稳定。
        var ui = UiRectPhysical();
        if (!ui.IsEmpty && ui.MaxX > bar.MinX && ui.MinX < bar.MaxX
            && ui.MaxY > bar.MinY && ui.MinY < bar.MaxY)
        {
            float h = bar.MaxY - bar.MinY;
            float top = MathF.Max(screen.MinY + 8f * DpiScale, ui.MinY - 8f * DpiScale - h);
            bar = new RectF { MinX = bar.MinX, MinY = top, MaxX = bar.MaxX, MaxY = top + h };
        }
        return bar;
    }

    internal bool ReplayBarContains(float x, float y) => _replay != null && ReplayBarRect().Contains(x, y);

    /// <summary>控制条上的按下：返回 true = 这一下归它（画布上的按下由调用方处理）。</summary>
    internal bool ReplayPointerDown(float x, float y)
    {
        if (_replay == null) return false;
        var bar = ReplayBarRect();
        if (!bar.Contains(x, y)) return false;
        switch (ReplayBar.ZoneAt(bar, x, y, DpiScale))
        {
            case ReplayBarZone.PlayPause:
                ReplayTogglePause();
                break;
            case ReplayBarZone.SpeedHalf:
            case ReplayBarZone.Speed1:
            case ReplayBarZone.Speed2:
            case ReplayBarZone.Speed4:
                ReplaySetSpeed(ReplayBar.SpeedOfZone(ReplayBar.ZoneAt(bar, x, y, DpiScale)));
                break;
            case ReplayBarZone.Progress:
                if (_replayPlaying) ReplayTogglePause();      // 拖进度 = 先暂停（拖完停在那儿）
                _replayScrubbing = true;
                ReplayScrubTo(x);
                break;
            case ReplayBarZone.Close:
                StopReplay("点关闭");
                break;
        }
        _dirty = true;
        return true;
    }

    internal void ReplayPointerMove(float x, float y)
    {
        if (_replay == null) return;
        if (_replayScrubbing) { ReplayScrubTo(x); return; }
        var zone = ReplayBarContains(x, y)
            ? ReplayBar.ZoneAt(ReplayBarRect(), x, y, DpiScale)
            : ReplayBarZone.None;
        if (zone != _replayHover) { _replayHover = zone; _dirty = true; }
    }

    internal void ReplayPointerUp(float x, float y)
    {
        if (_replayScrubbing)
        {
            ReplayScrubTo(x);
            _replayScrubbing = false;      // 松手停在拖到的位置（保持暂停）
        }
        _replayHover = ReplayBarContains(x, y)
            ? ReplayBar.ZoneAt(ReplayBarRect(), x, y, DpiScale)
            : ReplayBarZone.None;
        _dirty = true;
    }

    private void ReplayScrubTo(float x)
    {
        if (_replay == null) return;
        float p = ReplayBar.Progress01(ReplayBarRect(), x, DpiScale);
        ReplaySeek(p * _replay.Total);
    }

    internal ReplayBarZone ReplayHoverZone => _replayHover;
    internal bool ReplayScrubbing => _replayScrubbing;

    // ---- 自检钩子（开发期用；产品代码不碰）--------------------------------

    internal bool StartReplayForTest() { StartReplay(); return _replay != null; }
    internal void StopReplayForTest(string why) => StopReplay(why);
    internal void ReplayTogglePauseForTest() => ReplayTogglePause();
    internal void ReplaySetSpeedForTest(float speed) => ReplaySetSpeed(speed);
    internal void ReplaySeekForTest(float t) => ReplaySeek(t);
    internal void ReplayTickForTest() => TickReplay();
    internal int ReplayDoneForTest => _replayDone;
    internal int ReplayCountForTest => _replayStrokes.Count;
    internal int ReplayScratchCountForTest => _replayDoc?.Strokes.Count ?? -1;
    internal float ReplayTotalForTest => _replay?.Total ?? 0f;
    internal float ReplayPosForTest => _replayPosMs;
    internal float ReplaySpeedForTest => _replaySpeed;
    internal int ReplayCurrentIndexForTest => _replayCurrentIndex;
    internal float ReplayCurrentParamForTest => _replayCurrentParam;
    internal bool ReplayBarContainsForTest(float x, float y) => ReplayBarContains(x, y);
    internal bool ReplayPointerDownForTest(float x, float y) => ReplayPointerDown(x, y);
    internal void ReplayPointerUpForTest(float x, float y) => ReplayPointerUp(x, y);
    internal RectF ReplayBarRectForTest => ReplayBarRect();
}
