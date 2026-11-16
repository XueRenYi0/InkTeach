// [职责] 呼出盘：按住/划向扇区/松手。
// [测试] --radialtest

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using InkLocalization;
using Vortice.Direct2D1;
using Vortice.Mathematics;


namespace InkEngine;

public partial class InkEngine
{
    // =====================================================================
    //  呼出盘（Ctrl+Alt+Shift+Q）：按住 → 划向扇区 → 松手
    // =====================================================================
    //
    // 来龙去脉：《调研-笔键方案.md》附录 C/D（键盘呼出版；笔身键版留待真机实测硬件）。
    // 行为一句话：**按住才出来的标迹菜单**——按着不动会看到盘，120ms 内直接划走 =
    // 盘不闪（熟手路），松手确认、Esc / 落笔 / 松在中心 = 取消。
    //
    // 和主程序其它部分的接口，全部照现有语义：
    //   · **穿透里照样能出盘**（2026-10-04 用户定）：它是"从下层把笔抢回来"的入口，
    //     选扇区 = 退出穿透 + 换工具/选色（**刻意不同于**工具键 8.5 那条"穿透不响应"）；
    //   · **2026-10-04 起它是常驻全局键**（`Ctrl+Alt+Shift+Q`）：前台是 PPT/WPS
    //     也照样出盘，不再需要临时全局键表那一路；
    //   · 扇区里选工具 = 和按 Ctrl+P/I/L/E/M **同一条命令**（含"已经是它 → 换色/换档"）；
    //   · 颜色扇区 = "给我这支颜色的笔"（不在笔上就切到笔，走 SwitchTool）。
    //
    // 扇区顺序（从北起、顺时针，**排序 V-a**：上下左右四个正位给前四高频、四角给次频；
    // 用户 2026-09-30 定）：笔 / 黑 / 红 / 蓝 / 橡皮 / 框选 / 荧光笔 / 激光。
    // 正位 = 笔·红·橡皮·荧光笔；四角 = 黑·蓝·框选·激光。黑红蓝 = `InkPalette.PenBand`
    // 的前三个（色带本来就是"常用的排前面：黑红蓝绿…"），且保持顺时针相邻。
    // 尺寸与视觉规格对齐（2026-09-30 v4 定稿：方案 S「全扇面」、直径 192）：
    // 盘半径 96、死区 24、锁定 36；环带 40→96、图标 24 居中在 R68（见 Overlay）。
    // 判位一直是**按角度分 45° 扇区**，扇面化只换画法：死区/锁定/滞回一个字没动。
    // internal（不是 private）：自检要拿它和 Overlay 的扇面几何对表——
    // "图标环 ± 图标半径"必须落在（锁定距离, 盘半径）里，改单边忘另一边就会红。
    internal const float RadialRadiusLogical = 96f;
    // 2026-10-25 定稿：死区 24、锁定 36 不变（原手感）。
    // 色环在 30~36，指针在中央圆里就可能已经选中扇区，这是原设计的行为。
    internal const float RadialDeadZoneLogical = 24f;
    internal const float RadialLockLogical = 36f;
    private const double RadialShowDelayMs = 120;     // 出盘延迟（熟手路：不等盘直接划）
    /// <summary>清屏扇区要停住这么久才执行（和橡皮带的“清空”同一个防误触时长）。</summary>
    private const double RadialClearHoldMs = 800;

    internal bool RadialPaletteActive { get; private set; }
    internal bool RadialPaletteVisible { get; private set; }
    internal int RadialPaletteSector { get; private set; } = -1;
    internal float RadialCenterX, RadialCenterY;      // 画布坐标（和 PointerX/Y 同源）

    private double _radialOpenedAtMs;
    private bool _radialMoved;                        // 离开过锁定距离（中央文案用）
    private uint _radialVk = 0x51;                    // 呼出键的主键（松手轮询按它查）

    /// <summary>清屏扇区（西南）：停住 0.8s 才执行，防止划错就清空整页。</summary>
    internal const int RadialClearSector = 5;
    /// <summary>进入清屏扇区的时刻（0 = 没进）。盘上进度条用 <see cref="RadialClearProgress"/>。</summary>
    private double _radialClearArmedAtMs;

    /// <summary>
    /// 扇区名：画盘、日志、自检共用一份（顺序 = 从北顺时针）。
    /// 2026-10-25 v3：红笔居中、东西翻色、四角常用（撤销/荧光/清屏/框选）。
    /// </summary>
    internal static readonly string[] RadialSectorNames =
        { "红笔", "荧光笔", "下一色", "框选", "橡皮", "清屏", "上一色", "撤销" };

    /// <summary>中央文案要用的"划过又回中心"判据（盘开着时才有意义）。</summary>
    internal bool RadialMovedForDraw => _radialMoved;

    /// <summary>打开呼出盘（按住的那一刻）。条件不满足就静默不动。</summary>
    /// <remarks>
    /// **穿透里也能开**（2026-10-04 用户定）：呼出盘升为全局键就是为了"在别的程序
    /// 前面也能快速把笔调出来"，穿透开着时按它**照常出盘**——选一个扇区就等于
    /// "我现在就要写"，顺手退出穿透（和点面板工具格同一条路，见 CommitRadialPalette）。
    /// 这**刻意不同于**工具键 `Ctrl+P/I/L/E/M`：那五个在穿透下不响应（2026-09-30 定的
    /// "穿透 = 键盘归下层"），呼出盘是"从下层把笔抢回来"的那一个入口，规则不同。
    ///
    /// ⚠ 代价：穿透时我们**收不到鼠标移动消息**——`WS_EX_TRANSPARENT` 把覆盖层
    /// 从系统输入里整个摘掉（见 `_uiInputHwnd` 那段注释）。所以穿透里盘心与方向
    /// 都得靠系统光标的当前位置（`GetCursorPos`），不能等鼠标消息。
    /// </remarks>
    private void OpenRadialPalette()
    {
        if (RadialPaletteActive) return;
        if (CaptureActive) return;
        if (_drawing)
        {
            Console.WriteLine("书写中：呼出盘不响应（抬笔后再按）");
            return;
        }

        // 穿透时 PointerX/Y 停在旧位置（收不到移动消息），盘心要用系统光标的当前位置。
        SyncPointerFromCursor();

        // 记下这次实际绑定的主键（键位可改；松手轮询按它查，写死 Q 会在改键后失灵）。
        // ⚠ 2026-10-04 起呼出盘是**全局**键（`Ctrl+Alt+Shift+Q`）：作用域必须查 Global，
        // 查批注内会查不到（用户改键后更是直接失灵）。
        var binding = Keys.Find(KeyScope.Global, KeyAction.RadialPalette);
        _radialVk = binding != null && binding.Chord.IsValid ? binding.Chord.Vk : 0x51;

        RadialPaletteActive = true;
        RadialPaletteVisible = false;          // 120ms 之后（或移动之后）才真正画出来
        RadialPaletteSector = -1;
        _radialMoved = false;
        _radialTouchMode = false;              // 键盘/鼠标那套：松手轮询按键
        _radialSticky = false;
        _radialClearArmedAtMs = 0;
        _radialOpenedAtMs = NowMs;
        RadialCenterX = PointerX;              // 盘心 = 按下的那一刻指针在哪
        RadialCenterY = PointerY;
        _dirty = true;
        Console.WriteLine(PassThrough
            ? "呼出盘（穿透中）：按住划向扇区，松手 = 退出穿透 + 切到它（松在中心/划回中心/落笔 = 取消）"
            : "呼出盘：按住划向扇区，松手确认（松在中心/划回中心/落笔 = 取消）");
    }

    /// <summary>
    /// **触屏版呼出盘**（2026-10-05）：两指长按（划动松手确认）或两指轻点（留在盘上点选）。
    ///
    /// 与键盘版的差别就两条：
    ///   ① 松手不是"轮询按键"，而是**由触点驱动**——两根手指的平均位就是方向（TouchMoveDispatch
    ///      的 `TouchMode.Radial` 分支在喂），最后一根抬起时确认（TouchUpDispatch）；
    ///   ② **没划动就松手 = 留在盘上等点选**（`_radialSticky`；点扇区/点盘外见
    ///      <see cref="RadialPaletteTryPointerDown"/>）——键盘那套的"松在中心=取消"不变。
    /// </summary>
    private void OpenRadialPaletteFromTouch(float x, float y, bool sticky)
    {
        if (RadialPaletteActive) return;
        if (CaptureActive) return;

        RadialPaletteActive = true;
        RadialPaletteVisible = false;
        RadialPaletteSector = -1;
        _radialMoved = false;
        _radialTouchMode = true;
        _radialSticky = sticky;
        _radialClearArmedAtMs = 0;
        _radialOpenedAtMs = NowMs;
        RadialCenterX = x;
        RadialCenterY = y;
        PointerX = x;
        PointerY = y;
        _dirty = true;
        Console.WriteLine(sticky
            ? "呼出盘（触屏·轻点）：点扇区确认，点盘外/中心取消"
            : "呼出盘（触屏·长按）：划向扇区松手确认；没划动=留在盘上等点选");
    }

    /// <summary>
    /// 呼出盘开着时的一次落点：**命中盘面 = 点选扇区并确认**（触屏"轻点呼出"靠它；
    /// 鼠标/笔同样可用）；点在盘外 = 取消并返回 false（这一下继续走原路，该写字写字）。
    /// </summary>
    private bool RadialPaletteTryPointerDown(float x, float y)
    {
        if (!RadialPaletteActive) return false;
        if (!RadialPaletteVisible) { CancelRadialPalette("未出盘就落笔"); return false; }
        float dx = x - RadialCenterX, dy = y - RadialCenterY;
        float r = RadialRadiusLogical * DpiScale;
        if (dx * dx + dy * dy > r * r) { CancelRadialPalette("点盘外"); return false; }

        // 点选是一次"有意的方向输入"：把 `_radialMoved` 置上——点中心按下去 = 取消
        //（而不是被 Commit 里的"没划动就留盘"分支当成又一次静置）。
        _radialMoved = true;
        PointerX = x;
        PointerY = y;
        UpdateRadialSelection();

        // 清屏：点一下只开始/继续 0.8s 确认期，不关盘；到点由 Pump 执行。
        // 触摸“两指点一下 → 点扇区”没有按住过程，这样也能保留防误触。
        if (RadialPaletteSector == RadialClearSector)
        {
            if (_radialClearArmedAtMs > 0 && NowMs - _radialClearArmedAtMs >= RadialClearHoldMs)
                CommitRadialPalette(clearConfirmed: true);
            else
                _dirty = true;                       // 还在确认期：盘留着，进度条继续走
            return true;
        }

        CommitRadialPalette();
        return true;
    }

    /// <summary>
    /// 穿透时把指针位置同步成**系统光标的当前位置**。
    ///
    /// 为什么必须有它：穿透给覆盖层加了 `WS_EX_TRANSPARENT`，系统那一层就完全不
    /// 给它投递鼠标消息（连 WM_NCHITTEST 都不问），`PointerX/Y` 会一直停在最后
    /// 一次正常模式下的位置。呼出盘靠"盘心 ↔ 指针的位移"选扇区，位置是旧的就等于
    /// 盘心乱跳、方向失灵。`GetCursorPos` 与焦点无关，穿透期间照常给真位置
    ///（和 `BeginCaptureMode` 里那句 `GetCursorPos` 同一个理由与写法）。
    /// </summary>
    private void SyncPointerFromCursor()
    {
        if (!PassThrough) return;             // 正常模式有真鼠标消息，别去抢
        if (!Native.GetCursorPos(out var p)) return;
        PointerX = ScreenToCanvasX(p.X);
        PointerY = ScreenToCanvasY(p.Y);         // 屏幕 → 画布（相机）
        PointerInside = true;
    }

    /// <summary>松手 = 确认。没位移/死区 = 取消（触屏没划动则留盘等点选）；有扇区就执行那条命令。</summary>
    private void CommitRadialPalette(bool clearConfirmed = false)
    {
        if (!RadialPaletteActive) return;
        UpdateRadialSelection();               // 以松手这一刻的指针为准（快划不丢）
        int sec = RadialPaletteSector;

        // 清屏：必须停满 0.8s（Pump 到点会带 clearConfirmed=true 调进来）。
        // 划到就松手 = 不清，只提示——比“划错一下整页没了”安全得多。
        if (sec == RadialClearSector && !clearConfirmed)
        {
            CancelRadialPalette("清屏要按住 0.8 秒");
            ApplyCursor();
            return;
        }

        // 触屏版：**没划动就松手 = 留在盘上等点选**（Blender marking menu 的那半套；
        // 点扇区/点中心、点盘外都走 RadialPaletteTryPointerDown）。
        if (sec < 0 && _radialTouchMode && !_radialMoved)
        {
            _radialSticky = true;
            Console.WriteLine("呼出盘：没划动 → 留在盘上等点选（点空白/Esc 取消）");
            return;
        }

        RadialPaletteActive = false;
        RadialPaletteVisible = false;
        RadialPaletteSector = -1;
        _radialTouchMode = false;
        _radialSticky = false;
        _radialClearArmedAtMs = 0;
        _dirty = true;

        if (sec < 0)
        {
            Console.WriteLine("呼出盘 → 取消");
        }
        else
        {
            // **穿透中选扇区 = "我现在就要写"：顺手退出穿透**（2026-10-04 用户定）。
            // 和点面板工具格同一条路（`SwitchTool` 里那句 `if (PassThrough) SetPassThrough(false)`）。
            // ⚠ 顺序不能反：工具键在穿透下"不响应"（2026-09-30 定的 8.5 那条），
            // 不先退穿透的话 `ToolKeyPress` 会直接 return，按下去**悄无声息**——
            // 正是用户报的那个现象。这里先退（板态不恢复：换了工具的意思是"我要在
            // 眼前这片东西上写"，和 SwitchTool 的选择一致，见 SetPassThrough 的说明）。
            if (PassThrough) SetPassThrough(false, restoreBoard: false);

            Console.WriteLine($"呼出盘 → {RadialSectorNames[sec]}");
            // v3（2026-10-25）：北=红笔（固定）、东西=翻色、四角=撤销/荧光/清屏/框选。
            switch (sec)
            {
                case 0: PickPenColorFromPalette(1); break;      // 红笔（PenBand[1]）
                case 1: ToolKeyPress(KeyAction.ToolHighlighter); break;
                case 2: CyclePenColor(+1); break;               // 下一色
                case 3: ToolKeyPress(KeyAction.ToolMarquee); break;
                case 4: ToolKeyPress(KeyAction.ToolEraser); break;
                case 5: ClearCurrentPageFromUi(); break;         // 只清当前页；到这里一定停满 0.8s
                case 6: CyclePenColor(-1); break;                // 上一色
                case 7: UndoFromUi(); break;                     // 撤销
            }
        }
        ApplyCursor();
        _dirty = true;
    }

    /// <summary>
    /// 清屏扇区的 0~1 进度（盘上画进度条）。
    /// 只有停在清屏扇区、且计时已武装时才大于 0；离开扇区立刻归零。
    /// </summary>
    internal float RadialClearProgress =>
        RadialPaletteSector == RadialClearSector && _radialClearArmedAtMs > 0
            ? (float)Math.Clamp((NowMs - _radialClearArmedAtMs) / RadialClearHoldMs, 0, 1)
            : 0f;

    /// <summary>西格要显示的“上一支笔”颜色（当前色不在色带里时从白开始）。</summary>
    internal Color4 RadialPrevPenColor => PenBandColorAtOffset(-1);

    /// <summary>东格要显示的“下一支笔”颜色（当前色不在色带里时从黑开始）。</summary>
    internal Color4 RadialNextPenColor => PenBandColorAtOffset(+1);

    /// <summary>盘下提示里的“上一色/下一色”名字。</summary>
    internal string RadialPrevPenName => InkPalette.PenBand[PenBandOffsetIndex(-1)].Name;
    internal string RadialNextPenName => InkPalette.PenBand[PenBandOffsetIndex(+1)].Name;

    /// <summary>
    /// 在 `InkPalette.PenBand` 里按当前笔色找位置再前后挪一格。
    /// 不着色带（主条自定义色）时：下一色从黑（0）开始、上一色从白（末尾）开始。
    /// </summary>
    private int PenBandOffsetIndex(int dir)
    {
        int n = InkPalette.PenBand.Length;
        int idx = -1;
        for (int i = 0; i < n; i++)
        {
            if (ColorNear(CurrentColor, InkPalette.PenBand[i].Color, 0.02f)) { idx = i; break; }
        }
        if (idx < 0) idx = dir > 0 ? -1 : 0;
        return ((idx + dir) % n + n) % n;
    }

    private Color4 PenBandColorAtOffset(int dir) => InkPalette.PenBand[PenBandOffsetIndex(dir)].Color;

    /// <summary>呼出盘“上一色 / 下一色”：切到笔 + 在色带里前后挪一格（2026-10-25 v3）。</summary>
    private void CyclePenColor(int dir)
    {
        if (Tool != Tool.Pen) SwitchTool(Tool.Pen);
        SetColorFromUi(PenBandColorAtOffset(dir));
    }

    /// <summary>颜色扇区 = "给我这支颜色的笔"（不在笔上就切到笔；穿透互斥等照常态）。</summary>
    private void PickPenColorFromPalette(int bandIndex)
    {
        bandIndex = Math.Clamp(bandIndex, 0, InkPalette.PenBand.Length - 1);
        if (Tool != Tool.Pen) SwitchTool(Tool.Pen);
        SetColorFromUi(InkPalette.PenBand[bandIndex].Color);
    }

    private void CancelRadialPalette(string why)
    {
        if (!RadialPaletteActive) return;
        RadialPaletteActive = false;
        RadialPaletteVisible = false;
        RadialPaletteSector = -1;
        _radialTouchMode = false;
        _radialSticky = false;
        _radialClearArmedAtMs = 0;
        _dirty = true;
        Console.WriteLine($"呼出盘 → 取消（{why}）");
    }

    /// <summary>
    /// 每帧一次：出盘延迟、方向重算、松手轮询、超时。
    ///
    /// **为什么要有轮询**：全局热键（常驻的 `Ctrl+Alt+Shift+Q` 和放映临时那批）只给
    /// WM_HOTKEY（按下），没有松手消息；`GetAsyncKeyState` 看的是物理键状态，
    /// 60fps 下误差 ≤16ms。
    /// </summary>
    private void PumpRadialPalette()
    {
        if (!RadialPaletteActive) return;

        // 穿透里没有鼠标消息，扇区方向只能每帧问系统要一次（见 SyncPointerFromCursor）。
        SyncPointerFromCursor();

        if (RadialTestHold)
        {
            // 自检/摆样：不轮询、不超时，只把盘按出来（出图与状态断言用）。
            if (!RadialPaletteVisible) { RadialPaletteVisible = true; _dirty = true; }
            UpdateRadialSelection();
            RadialClearHoldDone();
            return;
        }

        if (!RadialPaletteVisible && NowMs - _radialOpenedAtMs >= RadialShowDelayMs)
        {
            RadialPaletteVisible = true;
            _dirty = true;
        }

        UpdateRadialSelection();
        if (RadialClearHoldDone()) return;

        // 触屏版（长按呼出）：松手不是按键，而是触点（TouchUpDispatch 里最后一根抬起时确认）。
        if (!_radialTouchMode && (Native.GetAsyncKeyState((int)_radialVk) & 0x8000) == 0)
        {
            CommitRadialPalette();
            return;
        }
        // 2026-10-25 用户定：**不再做 5s 超时**。盘一直留到松手 / 点选 / 点空白 / Esc。
    }

    /// <summary>清屏停满 0.8s → 执行并关盘。返回 true = 盘已经关了。</summary>
    private bool RadialClearHoldDone()
    {
        if (RadialPaletteSector != RadialClearSector) return false;
        if (_radialClearArmedAtMs <= 0) return false;
        if (NowMs - _radialClearArmedAtMs < RadialClearHoldMs) return false;
        CommitRadialPalette(clearConfirmed: true);
        return true;
    }

    /// <summary>按当前指针位置重算扇区（死区 / 锁定距离 / 跨扇区滞回都在这里）。</summary>
    private void UpdateRadialSelection()
    {
        float dpi = DpiScale;
        float dx = PointerX - RadialCenterX, dy = PointerY - RadialCenterY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float dead = RadialDeadZoneLogical * dpi;
        float lockR = RadialLockLogical * dpi;

        int sec;
        if (dist < dead)
        {
            sec = -1;                                   // 死区：松手 = 取消
        }
        else
        {
            float deg = MathF.Atan2(dy, dx) * (180f / MathF.PI);
            if (RadialPaletteSector < 0 && dist < lockR)
            {
                sec = -1;                               // 还不够远：先别锁方向
            }
            else if (RadialPaletteSector >= 0)
            {
                // 滞回：已经选中一个扇区时，出界 9° 以内仍算它（边界抖动不跳扇区）。
                float delta = Normalize180(deg - (-90f + 45f * RadialPaletteSector));
                sec = MathF.Abs(delta) <= 22.5f + 9f ? RadialPaletteSector : SectorIndexFromDeg(deg);
            }
            else sec = SectorIndexFromDeg(deg);
        }

        if (dist >= lockR) _radialMoved = true;
        if (sec != RadialPaletteSector)
        {
            RadialPaletteSector = sec;
            // 清屏扇区：进入那一刻开始计时（离开立刻取消）。
            _radialClearArmedAtMs = sec == RadialClearSector ? NowMs : 0;
            _dirty = true;
        }
    }

    /// <summary>指针角度 → 扇区号（0 = 北，顺时针）。北在上：-90° 起、每 45° 一个。</summary>
    internal static int SectorIndexFromDeg(float deg)
    {
        int i = (int)MathF.Round(deg / 45f);
        return ((i + 2) % 8 + 8) % 8;
    }

    private static float Normalize180(float deg)
    {
        while (deg <= -180f) deg += 360f;
        while (deg > 180f) deg -= 360f;
        return deg;
    }

    // ---- 呼出盘自检钩子（--radialtest / --radialshow）----

    /// <summary>自检/摆样：跳过松手轮询与超时（状态断言与"定格出图"用）。</summary>
    internal bool RadialTestHold;

    internal void RadialOpenForTest(float canvasX, float canvasY)
    {
        PointerX = canvasX; PointerY = canvasY;
        OpenRadialPalette();
    }

    internal void RadialMoveForTest(float canvasX, float canvasY)
    {
        PointerX = canvasX; PointerY = canvasY;
        UpdateRadialSelection();
    }

    internal void RadialPumpForTest() => PumpRadialPalette();
    internal void RadialCommitForTest() => CommitRadialPalette();
    internal void RadialCancelForTest(string why) => CancelRadialPalette(why);

    /// <summary>自检：放映临时全局键表里有没有某个动作（以及它的键）。</summary>
    internal static (uint Mod, uint Vk)? PptHotkeyEntryForTest(KeyAction a)
    {
        foreach (var (mod, vk, act) in PptHotkeys)
            if (act == a) return (mod, vk);
        return null;
    }

    private bool HandleKeyDown(IntPtr wParam, bool isRepeat)
    {
        // 键位表驱动：按"当前修饰键状态 + 主键"拼成一个和弦，去批注内作用域里查。
        // 查不到就**不吞这个键**（返回 false），交给系统/下层程序——吞掉所有按键
        // 会让批注键盘模式下连输入法都用不了。
        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;

        int vk = wParam.ToInt32();
        // 反馈群 / 赞赏码：Esc = 关掉（在说明页/面板之前，它是最上面那层）。
        if (!isRepeat && vk == 0x1B /*VK_ESCAPE*/ && FeedbackQrVisible)
        {
            HideFeedbackQr();
            return true;
        }
        // 使用说明层优先：打开时 Esc/F1 关闭、左右切页，其它键也不许落到画布。
        // 关闭时 F1 打开；只在批注键盘模式收到（和 Ctrl+P/I/L 同一入口）。
        if (Ui != null && Ui.HandleHelpKey(vk)) return true;
        // 取色板色值输入框：编辑期间优先吃键盘（数字/A-F/退格/回车/Esc）。
        if (Ui != null && Ui.HandlePickerKey(vk, (mods & KeyChord.ModCtrl) != 0)) return true;
        // 页面级键盘（设置页/API 页）：Tab/方向键/Enter/Space；只在页面打开时消费。
        if (Ui != null && Ui.HandleUiKey(vk,
                (mods & KeyChord.ModCtrl) != 0,
                (mods & KeyChord.ModShift) != 0,
                (mods & KeyChord.ModAlt) != 0,
                isRepeat)) return true;
        // 文档模式专用：`H` = 手掌开关；按住 `空格` = 临时手掌（2026-11-16）。
        // 放在界面层之后（设置页先接自己的 Space）、键位表之前（这两个键不在键位表里）。
        if (DocPanKeyDown((uint)vk, mods, isRepeat)) return true;
        // 界面最上面那层（AI 面板 / 更多 / 色板）：Esc 先关它。
        if (!isRepeat && vk == 0x1B /*VK_ESCAPE*/ && Ui != null && Ui.HandleEscape()) return true;
        // 文档恢复提示：Esc = 只关卡（不跳）。排在界面层之后——界面开着时先关界面。
        if (!isRepeat && vk == 0x1B /*VK_ESCAPE*/ && DocResumeVisible)
        {
            DismissDocResume("Esc");
            return true;
        }
        // 收藏面板点了一张卡、还没落笔：Esc = 取消这次插入（别让老师找不到怎么退）。
        if (!isRepeat && vk == 0x1B /*VK_ESCAPE*/ && _libraryPending != null)
        {
            _libraryPending = null;
            _libraryPendingTitle = null;
            SetInkStatus("已取消插入收藏");
            _dirty = true;
            return true;
        }
        // 图形页「∞ 连续」：Esc = 结束连续并回原工具。面板里的下拉/菜单开着时，
        // 第一下先按老规矩关它们，免得抢掉“关菜单”的手感。
        if (!isRepeat && vk == 0x1B /*VK_ESCAPE*/ && ShapeRepeatContinuous
            && !CaptureActive && !ColorPickActive && !UiOverlayOpen && !PptMode && !PassThrough && !_drawing
            && (!LibraryPanelOpen
                || (LibraryPanelVisibleNow
                    && !LibraryCategoryMenuOpen && !LibraryCategoriesOpen && LibraryMenu == 0
                    && !LibrarySelectMode && LibraryUseSelected == null)))
        {
            ShapeRepeatContinuous = false;
            _shapeOneShot = false;
            if (_restoreToolAfterLibrarySelection)
            {
                // 上一次单次图形/收藏插入还留着“点空白回原工具”的尾巴，Esc 一起收干净。
                ExitLibraryPlacementSelection();
            }
            else if (_shapeFromPanel && IsShapeTool(Tool))
            {
                var back = _shapeOneShotReturn;
                if (back == Tool.Capture || back == Tool.Laser) back = Tool.Pen;
                _shapeFromPanel = false;
                SwitchTool(back);
            }
            if (LibraryPanelVisibleNow) CloseLibraryPanel();
            SetInkStatus("已退出连续画");
            _dirty = true;
            NotifyUiStateChanged();
            return true;
        }

        // 收藏面板：Esc = 先关菜单，再关面板（和其它浮层同一套手感）。
        if (!isRepeat && vk == 0x1B /*VK_ESCAPE*/ && LibraryPanelVisibleNow)
        {
            if (LibraryCategoryMenuOpen)
            {
                LibraryCategoryMenuOpen = false;
                LibraryCategoryMenuHover = -1;
            }
            else if (LibraryCategoriesOpen)
            {
                LibraryCategoriesOpen = false;
                LibraryDrawerManage = false;
                LibraryConfirm = "";
            }
            else if (LibraryMenu != 0) CloseLibraryMenu();
            else if (LibrarySelectMode) ExitLibraryManage();
            else if (LibraryUseSelected != null)
            {
                LibraryUseSelected = null;
                CancelLibraryPanelDrag();
                NotifyUiStateChanged();
            }
            else if (_restoreToolAfterLibrarySelection)
            {
                ExitLibraryPlacementSelection();
            }
            else CloseLibraryPanel();
            _dirty = true;
            return true;
        }
        if (!isRepeat && vk == 0x70 /*F1*/ && mods == 0)
        {
            Ui?.ToggleHelp();
            _dirty = true;
            return true;
        }

        var chord = new KeyChord(mods, (uint)wParam.ToInt32());

        // 呼出盘开着时，Esc = 取消。
        // ⚠ 这条只在"修饰键已经先松开"之后才真能收到——**Ctrl+Esc 是系统保留的
        //    "打开开始菜单"**（Alt+Esc 还会切窗口），Windows 不会把它送进窗口
        //    （真机自检里量到过：按住 Ctrl+Q 时按 Esc，我们一条消息都收不到；现在
        //    呼出键多了 Alt/Shift，一样要全部松开之后 Esc 才送得到）。
        //    所以提交只认 Q 松手（见 HandleKeyUp），先松 Ctrl/Alt/Shift 盘还留着，
        //    这时 Esc 才有效；主取消路径是"松在死区 / 划回中心 / 落笔"。
        if (RadialPaletteActive && wParam.ToInt32() == 0x1B /*VK_ESCAPE*/)
        {
            CancelRadialPalette("Esc");
            _dirty = true;
            return true;
        }

        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null) return false;

        // 呼出盘和工具键一样"只看第一次按下"（按住不放的自动重复不再重开）。
        if (hit.Action == KeyAction.RadialPalette)
        {
            if (!isRepeat) RunAction(hit.Action);
            _dirty = true;
            return true;
        }

        if (IsToolKey(hit.Action))
        {
            // **自动重复的按下吞掉**：按住不放时 Windows 会连发 KEYDOWN，而"已经是它 →
            // 换色"这条会被连发带着一路狂转（所以工具键只看第一次按下，松手才算一次）。
            if (!isRepeat) ToolKeyPress(hit.Action);
            _dirty = true;
            return true;
        }
        RunAction(hit.Action);
        _dirty = true;
        return true;
    }

    /// <summary>松键：只服务工具键的手势（长按/双击判定），其余键不看松键。</summary>
    private bool HandleKeyUp(IntPtr wParam)
    {
        // 文档模式：按住空格开的临时手掌，松手收回（只认我们开的那一次）。
        if (DocPanKeyUp((uint)wParam.ToInt32())) { _dirty = true; return true; }

        // 呼出盘：**Q 松手 = 确认；只认主键，不认修饰键**。
        // 为什么：① 用户经常先松 Ctrl/Alt/Shift 再松 Q，按和弦查会漏；
        // ② 先松修饰键之后盘还留着，这时按 Esc 才是"真能送到我们手里"的取消
        //   （Ctrl+Esc 被系统的开始菜单占了，见 HandleKeyDown 那段）。
        if (RadialPaletteActive && wParam.ToInt32() == _radialVk)
        {
            CommitRadialPalette();
            _dirty = true;
            return true;
        }

        uint mods = 0;
        if ((Native.GetAsyncKeyState(0x11 /*VK_CONTROL*/) & 0x8000) != 0) mods |= KeyChord.ModCtrl;
        if ((Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0) mods |= KeyChord.ModAlt;
        if ((Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0) mods |= KeyChord.ModShift;
        var chord = new KeyChord(mods, (uint)wParam.ToInt32());
        var hit = Keys.For(KeyScope.Annotation).FirstOrDefault(b => b.Chord.Equals(chord));
        if (hit == null || !IsToolKey(hit.Action)) return false;
        ToolKeyUp(hit.Action, NowMs);
        return true;
    }

    /// <summary>
    /// 全选。顺便切到选择工具——不切的话手柄和操作条不会出现，
    /// 用户会以为"全选没生效"。
    /// </summary>
    internal void SelectAll()
    {
        Doc.Selected.Clear();
        foreach (var s in Doc.Strokes) Doc.Selected.Add(s);
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
    }

    /// <summary>
    /// 滚轮滚动画布。**只改相机偏移 ViewOffsetY，一个对象的数据都不动**——
    /// 这是滚动能做到 O(1) 的前提。绝不能像 InkClass 那样把平移烘焙进点坐标
    /// （那是 O(对象数)/次，一万笔滚一格要重写十万个点）。
    ///
    /// 只做纵向。往下滚 = 内容上移 = 偏移变负。
    ///
    /// 代价：**这里一个像素都不重画**。内容层是画布空间的分块缓存
    /// （CanvasTiles.cs），滚动只是换个位置把已经画好的块贴上去；只有在
    /// 新露出**没画过**的块时才会光栅化那一小块。所以滚一格的代价与文档
    /// 里有多少笔无关——从"整层重画"（一万笔 82~154ms，滚一格卡一下）
    /// 变成"贴图 + 偶尔补一两块"。这也正是 Win32 ScrollWindowEx 和浏览器
    /// 合成器滚动图层用的手法。
    /// </summary>
}
