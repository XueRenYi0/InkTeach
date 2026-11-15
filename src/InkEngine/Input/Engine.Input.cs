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
    //  Pointer input
    // =====================================================================

    private void OnPointerDown(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        EnsureOverlayLayers();           // A4.1：注册浮层适配器（影子路由/后续切换；不产生输入副作用）
        // 反馈群 / 赞赏码是全屏覆盖层：任何一点都先关它，这一下不再落到画布/界面。
        if (FeedbackQrVisible) { HideFeedbackQr(); return; }
        StopTouchFling();                // 任何按下（画布/界面/笔）= 先刹住双指惯性
        _cntDown++;
        ClearEngineTooltip();            // 按下 = 新动作开始，悬停提示先收
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        TouchDiagFeed(id, ptype, down: true);

        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);

        // 字体下拉：按下先“武装”拖动；抬起时没拖动才算点选。
        if (FontListPointerDown(x, y, id)) { ApplyCursor(); return; }

        // 文本框编辑中：点编辑框以外 = 先提交，**同一下**继续走文本工具分流。
        //（ClassIn 的行为：点空白马上出新输入框，旧框变成普通显示、不再带操作条。）
        // 点界面（尤其是文本色带/操作条）只提交，不新建。
        if (_textBoxEditing)
        {
            bool inside = false;
            if (_textBoxEditStroke != null)
            {
                var b = _textBoxEditStroke.PaddedBounds;
                inside = screenX >= b.MinX && screenX <= b.MaxX
                      && screenY >= b.MinY + ViewOffsetY && screenY <= b.MaxY + ViewOffsetY;
            }
            if (!inside && !UiContains(screenX, screenY))
                EndTextBoxEditFromUi(true);
        }

        // 收藏触摸滚动进行中：第二根手指一律吞掉，别把第二下落到卡片/画布上。
        if ((_libraryTouchScrollArmed || _libraryTouchScrolling) && ptype == Native.PT_TOUCH)
            return;

        if (ColorPickActive)
        {
            PointerX = x; PointerY = y; PointerInside = true;
            // 左下角面板是“工具”，不是取色点：点复制=复制，点面板/提示=不取色。
            OverlayWindow cpw = null;
            foreach (var win in _windows)
                if (win.Hwnd == hWnd) { cpw = win; break; }
            if (cpw != null)
            {
                if (cpw.ColorPickCopyRect(this).Contains(x, y))
                {
                    CopyColorPickHex();
                    return;
                }
                if (cpw.ColorPickPanelRect(this).Contains(x, y)) return;
                if (cpw.ColorPickHintRect(this).Contains(x, y)) return;
            }
            CommitColorPickAt(x, y);
            return;
        }   // 相机：屏幕 → 画布

        // 呼出盘开着时的落点：**点在盘面里 = 点选扇区并确认**（触屏"两指轻点呼出"靠它；
        // 鼠标/笔同样可用）；点在盘外 = 取消，这一下**继续走原路**（该写字写字）。
        // （旧行为是"任何落笔一律取消"——现在多出"盘上点选"这一条；设计稿附录 C 的
        //   "落笔表示我要写字了"仍然成立：点盘外还是取消。）
        if (RadialPaletteActive && RadialPaletteTryPointerDown(x, y)) return;

        // 收藏弹窗是**常驻浮层**（用户 2026-10-26 定）：点面板外/画布/换工具都不收，
        // 只有 Esc、关闭、再点收藏格才收。这里只让面板自己先吃它范围内的按下。
        if (LibraryPanelVisibleNow
            && OverlayLayers.PointerDown(x, y, id, ptype, 800, 800))
        {
            ApplyCursor();
            return;
        }
        // 点面板外：分类下拉先收起，这一下继续照常走画布/界面。
        if (LibraryPanelVisibleNow && LibraryCategoryMenuOpen)
        {
            LibraryCategoryMenuOpen = false;
            LibraryCategoryMenuHover = -1;
            _dirty = true;
        }

        // 使用态选中一张后，直接点画布 = 落点插入；面板保持打开。
        if (LibraryPanelVisibleNow && LibraryUseSelected != null
            && !InUiPopup(x, y) && !UiContains(screenX, screenY)
            && !PptBarContains(screenX, screenY)
            && !TimerCardContains(screenX, screenY)
            && !RollCardContains(screenX, screenY))
        {
            var picked = LibraryUseSelected;
            LibraryUseSelected = null;
            InsertLibraryGroupAt(new[] { picked }, x, y);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 回放（墨迹 C）优先于界面与画布，三种落点：
        //   · 控制条上 → 归它（播放/暂停、倍速、拖进度、关闭）；
        //   · 界面上 → **先退出回放**，这一下照常给界面（换工具/开面板 = 老师要接管了）；
        //   · 画布上 → **暂停/继续，不落墨**（讲课时最顺手的动作，用户已拍板）。
        if (_replay != null)
        {
            // A4.8：回放条完全由 OverlayLayerManager 路由（范围只限 replay）。
            // 按下 → 捕获；移动/松手由管理器转给适配器，旧 fallback 已删。
            if (ReplayBarContains(screenX, screenY))
            {
                if (OverlayLayers.PointerDown(screenX, screenY, id, ptype, 700, 700))
                {
                    _drawing = false;
                    Native.SetCapture(hWnd);
                    _dirty = true;
                    ApplyCursor();
                }
                return;
            }
            // 界面和 PPT 条、计时卡片都算"要接管"：先退出回放，这一下照常往下走
            //（点 PPT 条 = 翻页/长按菜单/跳页；点界面 = 换工具/开面板；点卡片 = 暂停/停）。
            if (UiContains(screenX, screenY) || PptBarContains(screenX, screenY)
                || TimerCardContains(screenX, screenY) || RollCardContains(screenX, screenY))
            {
                StopReplay("点界面或 PPT 条");
                // 不 return：这一下照常给下面的界面 / PPT 条处理
            }
            else
            {
                ReplayTogglePause();
                _dirty = true;
                return;
            }
        }

        // 页号面板（PPT / 文档）开着时：点"别的地方"就收起来——
        // 2026-10-09 用户定："跳完不消失、可以接着点；点其他位置才消失"。
        // 面板/条自己的命中不在这里处理（那是 PptBarPointerDown 的活：格子里跳页、
        // 里面空白吃掉、条上再点页码格 = 收起）。这里只兜"连 PptBar 都没走到"的落点——
        // 最典型的是**点界面（工具条）**：它在 PptBar 之前就把按下吃掉了。
        if (PptPagePanelOpen && !PptBarContains(screenX, screenY))
        {
            PptPagePanelOpen = false;
            _dirty = true;
        }

        // 界面优先：点在悬浮条上就是操作界面，不是画一笔。
        //
        // 两件容易踩的事：
        //  1) 这一段必须**排在穿透判断之前**——不然开着穿透时界面永远收不到按下；
        //  2) 传给界面的是**逻辑屏幕坐标**，不是画布坐标。界面的布局与绘制都在
        //     屏幕坐标里，喂它画布坐标的话相机一滚，命中就整体偏掉一块。
        //
        // 注：面板在实际产品里由"接输入小窗"（方案 B）接管，走的不是这条路；
        // 这里留着是**兜底**——万一那块小窗没建起来，至少非穿透模式下还能用。
        if (UiPointerDown(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted,
                          ptype == Native.PT_TOUCH, id))
        {
            _drawing = false;
            // 界面也要捕获指针：拖出悬浮条、在按钮上滑开都需要继续收到消息。
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 落在界面画的那一块里、但界面没吃这一下：这一下就算了。
        // ① 不落墨——面板底下的墨看不见，面板一挪又冒出来，属于"看不见却被记下来"；
        // ② 不透给下层——界面声明占用的区域（QueryBounds）就是它的地盘，
        //    要放行得让界面自己别把那一块算进去。
        if (UiContains(screenX, screenY))
        {
            _uiHover = true;
            _drawing = false;
            _dirty = true;
            return;
        }

        // 文本工具：点已有文字 = 选中/再点编辑；点空白 = 在点击处新建并输入。
        if (Tool == Tool.Text && !inverted)
        {
            // 这条分支在下面统一的 `_activePointer = id` 之前就 return 了；
            // 不在这里记下指针，OnPointerUp 会因 `id != _activePointer` 直接丢掉，
            // 选中拖动就永远收不了尾（表现为"选中了拖不动/松手还在拖"）。
            _activePointer = id;
            _activePointerType = ptype;
            if (TextToolPointerDown(screenX, screenY))
            {
                // 文本工具点住已选中的文字时，TextToolPointerDown 会起一次选择拖动；
                // 这时 `_drawing` 必须是 true，否则 OnPointerMove 会走"悬停"分支，
                // 永远到不了 SelDragging——表现就是"选中了但拖不动"。
                _drawing = SelDragging;
                Native.SetCapture(hWnd);
                _dirty = true;
                ApplyCursor();
                return;
            }
            // 点到了非文本对象：TextToolPointerDown 已切回选择工具，
            // 这一下继续走下面的标准点选/拖动。
        }

        // AI 题卡（文档对象）：**只在使用「选择」工具时**才由它接管按钮/右边缘；
        // 笔、荧光笔、激光、图形和橡皮一律放行——题卡不能挡住写字。
        if (Tool == Tool.Marquee && !inverted && TextBoxPointerDown(screenX, screenY))
        {
            _drawing = false;
            _aiCardCapturing = true;
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 课堂计时卡片（引擎侧浮层）：**排在界面之后、PPT 条之前、穿透之前**——
        // 和 PPT 条同一条规矩：看得见的那一块就是点得到的（穿透/放映下也要能暂停/停）。
        if (OverlayLayers.PointerDown(screenX, screenY, id, ptype, 600, 600))
        {
            _drawing = false;
            _timerCapturing = true;   // 旧拖动判据；捕获生命周期仍归管理器
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 课堂点名卡片（引擎侧浮层）：和计时卡同一槽位规则。
        if (OverlayLayers.PointerDown(screenX, screenY, id, ptype, 590, 590))
        {
            _drawing = false;
            _rollCapturing = true;    // 旧拖动判据；捕获生命周期仍归管理器
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 底部那两条 PPT 控件（放映时才在）：**排在界面之后、穿透之前**。
        //   · 界面在它上面（见 RenderFrame 的绘制顺序），所以界面先问；
        //   · 必须在穿透之前——开着穿透时老师照样得能点翻页 / 拖进度条
        //     （NCHITTEST 里给它开了区域豁免，见那里的注释）。
        if (OverlayLayers.PointerDown(screenX, screenY, id, ptype, 500, 500))
        {
            _drawing = false;
            _pptCapturing = true;      // 旧输入窗/拖动判据；捕获生命周期仍归管理器
            Native.SetCapture(hWnd);        // 长按要持续收到移动（判"拿起后有没有拖走"）
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 界面之外的穿透：交下层窗口，我们不收这一下。
        if (PassThrough) return;

        // 图库里点了一张之后（`_libraryPending` 非空）：**这一次按下就是"把它放下来"**
        // ——和"粘贴对象"同一条路（一步撤销、放完自动选中）。
        // 位置：排在界面之后（点在工具条上不该跑到画布上插东西）、落笔之前
        //（它不是"画一笔"，不能进 _drawing）。
        if (LibraryInsertArmed)
        {
            TryInsertLibraryAt(x, y);
            ApplyCursor();
            return;
        }

        // 图钉灭：开始用画布时面板让开（选卡后先留着，方便改单次/连续）。
        AutoCloseLibraryOnCanvasUse();

        // 从收藏放下的对象还在临时选中：点空白 = 取消选中并回到插入前的工具。
        // 这一下只做“退出”，不落墨；点另一个对象则继续走正常选择。
        if (_restoreToolAfterLibrarySelection && Doc.Selected.Count > 0
            && AutoSelectionZoneAt(x, y) == AutoSelZone.None
            && Doc.HitObjectAt(x, y, ClickToleranceLogical * DpiScale) == null)
        {
            ExitLibraryPlacementSelection();
            return;
        }

        // 触摸手势层（8.4.0）：**触摸的按下必须先喂触点表，再做"同一时刻只跟一条指针"的守卫**。
        //
        // ⚠ 次序就是这里最要命（2026-10-05 实测根因）：第一根手指落下的瞬间 `_drawing`
        // 就成 true 了，守卫放在前面的话**第二根手指永远进不了触点表**——
        // 合成注入探针当时读出来是"两指按下后：触点 = 1、模式 = Write"，双指手势永远起不来。
        // 触点表只负责"记下这一刻有几根手指"；抢不抢笔由 TouchVerdict 回答：
        //   · Gesture2 / Erase / Roam → 吃掉，并接管 activePointer（成为手势主人）；
        //   · Ignore（写字中途蹭到的手指）→ 吃掉，但**不许抢** activePointer
        //     （抢了第一根手指的移动就会被 `id != _activePointer` 挡掉、正在写的字当场断线）；
        //   · Write → 不消费，继续走下面原来的写字守卫与起笔。
        //
        // 只有"当前在写的也是触摸"（或没有指针在写）时才喂：笔 / 鼠标在写的时候，
        // 触摸照旧走守卫忽略，不搅局、也不在表里留残留。
        int touchW = 0, touchH = 0;
        float touchSize = ptype == Native.PT_TOUCH ? ReadTouchSize(id, out touchW, out touchH) : 0f;
        if (ptype == Native.PT_TOUCH && (!_drawing || _activePointerType == Native.PT_TOUCH)
            && TouchDownDispatch(hWnd, id, x, y, touchSize, touchW, touchH, out bool touchSteal))
        {
            if (touchSteal)
            {
                _activePointer = id;
                _activePointerType = ptype;
                PointerX = x; PointerY = y; PointerInside = true;
            }
            return;
        }

        // **同一时刻只跟一条指针**——这是 OnPointerMove/OnPointerUp 里那句
        // `id != _activePointer` 的另一半。已经有指针在手（正在写、正在拖滚动条）时，
        // 后来的按下直接忽略。
        //
        // 这条守卫以前没有，而**只有触摸踩得到**：鼠标和笔一次只有一个指针，
        // 触摸屏上写字时蹭到的第二根手指（或者落屏的掌根）会让下面 `_activePointer = id`
        // 把指针抢走、`ActiveStroke` 被换成新的一笔——而正在写的那一笔**还没进文档**，
        // 于是"写一半的字凭空消失"。它顺带就是最基础的掌心抑制：掌根不再抢笔。
        if (_drawing) return;

        // Phase 5.0：选择操作条（优先级 400）先走浮层管理器。
        // 只切“按下”；未命中/未消费继续走原来的工具分流（条本身不需要指针捕获）。
        if (OverlayLayers.PointerDown(x, y, id, ptype, 400, 400))
        {
            _dirty = true;
            ApplyCursor();
            return;
        }

        _activePointer = id;
        _activePointerType = ptype;
        PointerX = x; PointerY = y; PointerInside = true;

        // 滚动条次之（**2026-10-09 起停用**，见 ScrollBarEnabled）：它只占右边缘一条窄带，
        // "拖滑块"比"在那儿画一笔"更特殊——代价是右缘 16px 会吃掉"写到边缘"的起笔。
        if (ScrollBarEnabled && TryBeginScrollBarDrag(screenX, screenY))
        {
            _drawing = true;
            Native.SetCapture(hWnd);
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (ptype == Native.PT_TOUCH)
        {
            _touchMode = TouchMode.Write;      // 单指写字：长按由心跳判（见 TouchTick）
            StartDwellTimer();
        }

        _drawing = true;
        Native.SetCapture(hWnd);
        // 书写会话开始：进 GC 低延迟档（见 GcLatency.cs），并起分配/GC 仪表。
        // 一笔接一笔写的时候，低延迟档会一直待着；最后一笔结束满几秒才退回。
        GcLatency.Enter();
        BeginStrokeMeasure();
        // 拖拽中系统不再发 WM_SETCURSOR（输入已被捕获），光标必须在按下这一刻定下来。
        ApplyCursor();

        // 截图（8.3.1）：待机 / 拖框 / 调整三种状态先都过这里。
        // P5.1：按下先走浮层管理器（范围 850）；
        // 返回 false = 没点取消/没说“已吃掉”，照常走工具分流（也就是"从这里起框"）。
        if (CaptureActive && OverlayLayers.PointerDown(x, y, id, ptype, 850, 850))
        {
            _capturePressHandled = true;
            return;
        }

        var tool = inverted ? Tool.Eraser : Tool;
        switch (tool)
        {
            case Tool.Eraser:
                _lastEraseX = x; _lastEraseY = y;
                Doc.BeginErase();
                EraserTelemetry?.BeginDrag(Tool.Eraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseAt(x, y, EraserRadius);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.PixelEraser:
                _lastEraseX = x; _lastEraseY = y;
                ResetDynamicEraser();
                PixelEraseDragging = true;
                Doc.BeginEraseRect();
                EraserTelemetry?.BeginDrag(Tool.PixelEraser, x, y, NowMs);
                {
                    bool log = EraserTelemetry != null;
                    long t0 = log ? Stopwatch.GetTimestamp() : 0;
                    int hit = Doc.EraseRectAt(x, y, PixelEraserCursorHalfWidthPx, PixelEraserCursorHalfHeightPx);
                    if (log)
                        EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                             Stopwatch.GetElapsedTime(t0).TotalMilliseconds, x, y, NowMs);
                }
                break;

            case Tool.Capture:
                // 和框选同一个手势。**和框选一样存画布坐标**（渲染那一层就
                // 不必为它单独换算一次），抓屏时再换算回屏幕坐标
                // （screen = canvas + 相机偏移，见 EndCapture）。
                BeginCaptureAt(x, y);
                break;

            case Tool.Marquee:
                // 先问选择手势（按钮 / 手柄 / 整体拖动）；都没接才起新的框选。
                // 修饰键在这一刻读一次就传进去（而不是在判定函数里读键盘）——
                // 这样"Shift 加选 / Alt 减选"这条分支能被自检直接驱动。
                if (!TryBeginSelectionGesture(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0))
                {
                    BeginMarqueeAt(
                        x, y,
                        shift: (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0,
                        alt: (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0);
                }
                break;

            case Tool.Laser:
                // **新起一条**（抬手那几条还在淡出，原样留着——照 ClassIn：可以同时有好几条）。
                // 粗细在这里记进这一条（每条自己记，见 `LaserTrail.Stroke.WidthLogical`）。
                Laser.Begin(x, y, NowMs, LaserWidthLogical);
                break;

            // 图形工具：**同一个手势**"按下记起点 → 拖动改终点 → 松手提交"
            // （接一个等于接七个，差别只在 Kind，见 计划-图形工具.md 8.2 / 9.1）。
            //
            // 判据走 <see cref="IsShapeTool"/>，**不再在这里逐个列 case**：
            // 以前这里是一串 `case Tool.Line: …`，和 IsShapeTool 是**同一件事的两份名单**。
            // 2026-09-19 加坐标系 / 数轴时这里就漏改了，症状是"工具切过去了、
            // 画出来的还是一坨自由笔迹"——`--axistest` 第一次跑就把它抓出来了。
            // 合成一份之后，以后再加图形种类不会再漏第二个地方。
            default:
                if (IsShapeTool(tool))
                {
                    // **画完自动选中那个框**（见 EndStroke）：按下先问"这一下是不是在动它"。
                    //   · 在它身上（圆钮 / 操作条 / 手柄 / 旋转柄 / **框内任意一点**）
                    //     → 交给选择手势那一套（拖动、拉手柄、点条，和框选工具下**完全一样**）；
                    //   · 在**框外** → 收起这个框，这一笔照常画。
                    // 用户 2026-09-22 定的口径："点击了其他地方，这个选中框就取消"；
                    // 而"只要收缩，其他的都不变"——所以框里照样能拖（第一版把框里判成
                    // "接着画一笔"，用户上手就是"不能拖动位置，只能拉伸缩放"）。
                    // 判据只有 `AutoSelectionZoneAt` 这一处（光标那边问 `SelectionInteractiveAt`）。
                    if (Doc.Selected.Count > 0)
                    {
                        bool shift = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
                        bool alt = (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0;
                        if (AutoSelectionPress(x, y, shift, alt)) { _dirty = true; return; }
                        ClearSelectionForNewContext();
                    }

                    // **多笔图形**（目前只有双曲线：两笔，见表 StepPlan）。规则照 InkClass：
                    // **一笔 = 按下-拖-松手**，松手推进下一笔（见 _stepIndex 那段注释）。
                    //   ① 第 1 笔按下 → 起半成品（`_stepPlan` / `_stepIndex` 也在那里落）；
                    //   ② 后面几笔按下 → **什么都不新建**，只接着改那个半成品
                    //     （中心沿用第 1 笔那个，InkClass 也是这么干的）。
                    if (_stepPlan != null && _stepIndex > 0)
                    {
                        ApplyStepGeometry(x, y);
                        break;
                    }
                    var plan = PlanOf(tool);
                    if (plan != null)
                    {
                        BeginStepShape(tool, plan, x, y);
                        ApplyStepGeometry(x, y);      // 按下也算一次（见那个函数的注释）
                        break;
                    }
                    BeginShapeAt(tool, x, y);
                    break;
                }

                // **笔下面那个看得见的选中框**（停顿成型 / 触摸长按选出来的）：这一下同样要
                // 先问"是不是在动它"。用户 2026-09-23 定的口径和图形工具下**完全一样**：
                // 点框里 → 拖动 / 拉手柄；点框外 → 收起这个框、**这一笔照常画**
                //（只是"只不过点了一下"的话不留墨）。
                //
                // 2026-10-05 真机反馈："长按选中以后不能移动操作"——判据从只认 `_dwellSelected`
                // 扩到**也认 `_touchSelected`**：触摸长按选出来的框，在笔下面同样是
                // "按住框里拖 = 移动、按框外 = 收起并照常写"（和框选 / 图形工具下同一套行为）。
                if (TryDwellSelectionPress(x, y)) { _dirty = true; return; }

                BeginFreehandStrokeAt(id, ptype, x, y, screenX, screenY, pressure);
                break;
        }
        _dirty = true;
    }

    /// <summary>
    /// 笔下面那一下：**如果那个"看得见的选中框"还在，先问它是不是在动它**
    ///（返回 true = 这一下被选择手势吃掉了，调用方直接收工）。
    ///
    /// 判据认两种框：`_dwellSelected`（停顿成型）和 `_touchSelected`（触摸长按选中）。
    /// 2026-10-05 真机反馈："长按选中以后不能移动操作"——触摸选出来的框以前在笔下面
    /// 不拦，按上去就变成接着写一笔；现在和框选 / 图形工具下同一条行为。
    ///
    /// ⚠ **只有这一处实现**：`OnPointerDown` 和 `--dwelltest` 都问它。
    /// 判据写两份的话，自检验的其实是"自检自己那一份"，等于没验
    ///（"同一个名单写在多处必漏一处"，见 架构-分层与规则.md 五-7）。
    /// </summary>
    private bool TryDwellSelectionPress(float x, float y)
    {
        if ((!_dwellSelected && !_touchSelected) || Doc.Selected.Count == 0) return false;
        bool shiftKey = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
        bool altKey = (Native.GetAsyncKeyState(0x12 /*VK_MENU*/) & 0x8000) != 0;
        if (AutoSelectionPress(x, y, shiftKey, altKey)) return true;
        // 框外：收起这个框，这一笔照常画——只是"如果这一下只是个点"，墨不留
        //（见 EndStroke 里那条 `_dismissTapArmed`）。
        ClearSelectionForNewContext();
        _dismissTapArmed = true;
        return false;
    }

    /// <summary>
    /// 起一笔自由笔迹（笔 / 荧光笔 / 激光笔都走这里）。
    ///
    /// 从 `OnPointerDown` 里**抽出来**的理由有两条：
    ///   · 停顿成型的自检要能从"按下"这一步**真跑一遍**（`--dwelltest`），
    ///     而不是在测试里另造一条笔迹对象——那样测的就不是这条路了
    ///     （"我造了个对象、字段是我自己填的"不算测，见 project_memory 的教训）；
    ///   · 起笔的几件事（线型 / 合成器轨迹 / 预测器 / 停顿跟踪）本来就该在一处写完。
    /// </summary>
    private void BeginFreehandStrokeAt(uint id, uint ptype, float x, float y,
                                       float screenX, float screenY, float pressure)
    {
        var tool = Tool;
        float trailW = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                                 : tool == Tool.Laser ? LaserWidthLogical
                                                 : PenWidthLogical) * DpiScale;
        // 这一笔的线型：**只有笔吃那个开关**（见 PenDash），别的工具一律实线。
        var dash = tool == Tool.Pen ? PenDash : StrokeDash.Solid;
        // **虚线笔迹不起委托墨迹**：那条轨迹由系统合成器画，画不出我们的线型
        // （它只会画一条实线），一笔写完就会"实线突然变虚线"闪一下。
        // 退回落自己画反而是对的——自己画的湿墨本来就是虚线，前后一致。
        //
        // 2026-10-11：**不再只对真笔开**——笔/鼠标/触摸都喂。来由：真笔那条通道调通后
        // （挂视觉树 + 结账），用户实测"鼠标模式（手写板关 ink）不跟手"——因为鼠标路径
        // 一个点都没喂过。逐帧采样**实测**：鼠标**也画**（拖动中量到过 163 像素的尾巴）。
        // ⚠ 这条通道画的是"还没上屏的那一小段"——我们自己的帧一提交就把它消化掉，
        //   所以肉眼看常是"闪一下"（`--trailonly` 下尤其明显），那是结构性表现，不是 bug。
        var wetWin = WindowAt(screenX, screenY);
        bool trailStarted = false;
        if (dash == StrokeDash.Solid)
            trailStarted = wetWin?.BeginInkTrail(
                tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor, trailW * 0.5f) ?? false;
        _pen.BeginStroke();   // 缺压回填的基准只活在"一笔"之内（见 PenSampleBuffer.BeginStroke）
        ResetPredictTail();   // B4：新的一笔，预测器从零开始
        _trailActiveThisStroke = trailStarted;   // 委托墨迹：本笔有系统湿墨 → 预测退场×scale
        InputBlackBox.Begin(NowMs, x, y, pressure, ptype);   // 黑匣子：起笔（回放用）
        ActiveStroke = new Stroke
        {
            Tool = tool,
            Color = tool == Tool.Highlighter ? HighlighterCurrent : CurrentColor,
            Width = (tool == Tool.Highlighter ? HighlighterWidthLogical
                                              : tool == Tool.Laser ? LaserWidthLogical
                                              : PenWidthLogical) * DpiScale,
            Dash = dash,
            // **正在写的这一笔画折线**（曲线只用在落笔之后）：曲线的最后一段每来一个
            // 新点就要回头重算，笔尖后面那几十像素会一直微微动 —— 用户 2026-09-28
            // 实测的原话是"上面会出残影一直在那闪"。见 Stroke.RawWhileLive。
            RawWhileLive = true,
        };
        // `--syswet`：真笔 + 实线笔，且轨迹真的起来了 → 湿墨交给系统（我们不再自绘）。
        // 荧光笔/激光笔不在此列——系统轨迹画不出它们的观感；`--ownwet` 可强制回老路。
        // ⚠ 仍然**只给真笔**：鼠标/触摸的轨迹系统认不认还没结论，不能让"我们不自绘"去赌。
        _systemWetStroke = (trailStarted && OverlayWindow.SystemWetPreferred && tool == Tool.Pen
                            && ptype == Native.PT_PEN)
            ? ActiveStroke : null;
        // 起笔：落笔这条消息里可能已经合并了几个采样点，一起收进来（以前只取最新那一个）。
        ActiveStrokeHasPressure = false;
        _cleanBuf.Clear();
        // 委托轨迹：这一笔的喂点/结账差值基线（`[笔画]` 里报"这一笔"的数）。
        _trailFedSeen = OverlayWindow.InkTrailPointsFed;
        _trailRemovedSeen = OverlayWindow.InkTrailPointsRemoved;
        LastCoalescedSamples = LastCoalescedMessages = 0;
        _rawReportsThisStroke = 0;      // `--rawprobe`：原始输入计数每笔归零
        _rawPointsAdded = 0;            // `--rawinput`：补进来的点数每笔归零
        _rawSkipNoStroke = _rawSkipApi = _rawSkipType = _rawSkipAbs = 0;
        _rawSkipZero = _rawSkipKind = _rawSkipGate = _rawSkipAnchor = 0;
        _rawSkipCap = _rawSkipNear = _rawSkipNoInter = 0;
        _rawAbsValid = false;           // 绝对报的"上一条"每笔作废（差值不能跨笔累加）
        _rawSinceLastPtr = _rawTotalSincePtr = _ptrMsgsThisStroke = 0;
        _rawHasIntermediates = false;   // 判据要攒几条才有结论
        _rawAnchorValid = false;        // 锚点由这一笔的第一条指针消息建立
        SetRawCapture(true);            // 原始输入：**只在这一笔期间收**（空闲必须关）
        AppendStrokeSamples(id, ptype, x, y, screenX, screenY, pressure);
        // 半径**逐点算**（见 TrailRadius）：有压感的笔，湿墨的粗细必须和干墨一致。
        FeedInkTrail(ptype, TrailRadius(), screenX, screenY);
        // 委托轨迹可能在创建成功后、第一次真正喂点时才失败。失败就不再让位
        //（`--syswet` 只该在"系统真的在画"时生效；不给位的话我们自己接着画）。
        if (trailStarted && wetWin != null && wetWin.InkTrailRuntimeFailed)
            _systemWetStroke = null;

        // **停顿成型跟着这一笔开始计时**（见 计划-图形工具.md §四十二）。
        // 激光笔不参与：它只是"指一下"，本来就不留墨，把它变出一个图形来没有意义。
        _dwellInk = null;
        _dwellCommitted = false;
        // 录墨迹（`--recink`）：这一笔从零开始攒（见 `FlushInkRecord`）。
        _recordPts = null;
        _recordGuess = "";
        // 上一笔要是还挂着（**配对没走到提交**就复位了，比如中途换了工具），
        // **必须把它放回可见** —— 否则那一笔会**永远隐身** ✗（见 `Stroke.HiddenForPairing`）。
        // ⚠ 走 `SetPairHidden`：它同时把那一笔占的块标脏，否则"放回来了、屏幕上还是不显示"
        //   （配对期那几帧已经把块重画成"没有它"的版本了）。
        SetPairHidden(_twoBranchPrev, false);
        _twoBranchPrev = null;
        _twoBranchIndex = -1;

        if (DwellShapeEnabled && tool != Tool.Laser)
        {
            _dwell.Begin(NowMs, new Vector2(x, y), DwellAssist.DeadZoneLogical * DpiScale);
            StartDwellTimer();
        }
        else _dwell.Reset();
    }

    /// <summary>
    /// 这一帧的指针移动（自由笔迹那一支）。从 `OnPointerMove` 抽出来，
    /// 理由和 <see cref="BeginFreehandStrokeAt"/> 同一个：停顿成型的自检要能**真跑**这一条。
    /// </summary>
    private void ExtendFreehandStroke(uint id, uint ptype, float x, float y,
                                      float screenX, float screenY, float pressure)
    {
        // **停顿成型**：armed 之后这一笔不再堆采样点（它已经变成幽灵图形了），
        // 指针移动一律交给 `ArmedStrokeMove`：**只有直线**会动（拖另一头转向 / 伸缩），
        // 其它图形**只预览、什么都不改**（抬手才定型 + 自动选中，见那个函数）。
        // ⚠ 这里**没有"取消"**：早先那条"非直线拖走就取消、这一帧的点照常进笔迹"是
        //    我们自己定的，早去掉了。
        bool dwellConsumed = _dwell.State == DwellState.Armed
                          && ArmedStrokeMove(x, y, screenX, screenY);
        if (dwellConsumed) return;

        // 指针报什么坐标就存什么坐标：不做平滑、不做抽稀。
        // 但**一条消息里的点要全部收下**——系统会把来不及投递的移动合并
        // （自己实测：注入 140 Hz，应用只收到约 60 条消息，其余在 history 里），
        // 只取最新那一个等于把笔的采样率砍半（见 Input/PenInput.cs）。
        AppendStrokeSamples(id, ptype, x, y, screenX, screenY, pressure);
        FeedInkTrail(ptype, TrailRadius(), screenX, screenY);
        // 停顿跟踪：**死区内的抖动不算"动过"**（见 DwellAssist.DeadZoneLogical）——
        // 少了它，笔尖静止时那点亚像素抖动会把计时一直刷新，
        // "停 600ms"永远攒不满，整条功能看着像没生效。
        _dwell.Sample(NowMs, new Vector2(x, y));
    }

    // ---------------------------------------------------------------------
    //  停顿成型：计时 / 触发 / 按住调整 / 定型（规格见 计划-图形工具.md §四十二）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 开那颗 40ms 的轮询定时器（**只在笔画进行中开**）。
    ///
    /// 为什么非要有它：**笔不动就没有 `WM_POINTERUPDATE`**，而主循环空闲时阻塞在
    /// `WaitMessage()`（见 `Loop`）——不主动醒过来，就永远问不出"停了多久"。
    ///
    /// 为什么单独一颗而不是搭现成那颗 250ms 的：250ms 的粒度下一笔"600ms 的停顿"
    /// 会在 500~750ms 之间随机触发，手感是飘的；40ms 只多醒 25 次/秒，而且**只在写字期间**。
    /// </summary>
    private void StartDwellTimer()
    {
        if (_dwellTimerOn || _windows.Count == 0) return;
        Native.SetTimer(_windows[0].Hwnd, (IntPtr)DwellTimerId, DwellAssist.TickMs, IntPtr.Zero);
        _dwellTimerOn = true;
    }

    private void StopDwellTimer()
    {
        if (!_dwellTimerOn || _windows.Count == 0) return;
        Native.KillTimer(_windows[0].Hwnd, (IntPtr)DwellTimerId);
        _dwellTimerOn = false;
    }

    /// <summary>
    /// **轮询那一刻**：笔停够了就识别一次。这是整条路上唯一的"时间判据"入口
    ///（`Engine.Loop` 里 `NowMs` 已经刷新过了，见那里的顺序）。
    /// </summary>
    private void TickDwellShape()
    {
        // 触摸手势的长按（8.4.0）：**和停顿成型共用这一次心跳**。
        // 两者靠判据分开：停顿成型要"已经画出东西"（点数 ≥3 + 识别器的最短长度），
        // 长按要"从头到尾没画出去"（路径 ≤8 逻辑像素，和"点一下不留墨"同一个数）。
        TouchTick();

        if (_dwell.State != DwellState.Tracking) return;
        if (!_dwell.StillEnough(NowMs)) return;

        var ink = ActiveStroke;
        if (ink == null || ink.Points.Count < 3) return;

        var pts = new Vector2[ink.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(ink.Points[i].X, ink.Points[i].Y);
        // **录墨迹**（`--recink`）：识别器看到的就是这一串点，直接留个引用给它存 ✓
        if (InkRecordPath != null) _recordPts = pts;
        // 最短长度那条门槛**在识别器里**（见 ShapeRecognize.Recognize 的 scale 参数）——
        // 这里不再自己乘一遍 DpiScale：同一个数写在两处，迟早会漂（用户 2026-09-23 的教训清单里
        // 第一条就是这个）。
        // ★ **两笔 → 双曲线：2026-10-17 用户定：停用**（"停顿变双曲线容易误触，双曲线
        //   使用频率不高，到时候直接在图形库里面找就行了"）。配对识别器本身保留
        //   （`TryTwoBranchPair` / `TryTwoBranchHyperbola` 还在，自检与将来要恢复时用），
        //   只是**停顿成型这条路不再调用它**——单笔仍然只出"直线/抛物线"那一套。
        //
        // var pair = TryTwoBranchPair(ink, pts);
        // if (!pair.IsNothing) { ... ArmDwellShape(ink, pair); return; }

        var guess = ShapeRecognize.Recognize(pts, DpiScale);
        if (guess.IsNothing)
        {
            // **`--reclog` 诊断**（用户 2026-09-26 报"椭圆和矩形两个都常常什么都不认"那一条）：
            // 真机上"没认出来"原来**一点痕迹都没有**，只能靠合成语料去猜。
            // 这一行把"为什么没认"直接打出来（`Rule` 里每一道闸都带着数字）。
            if (InkLogEnabled)
                Console.WriteLine($"[成型] 没认出来：{guess.Rule}（{pts.Length} 点）");
            if (InkRecordPath != null) _recordGuess = "没认出来：" + guess.Rule;
            // 认不出图形 → **什么都不做，墨迹原样留着**（用户 2026-09-25 定）。
            //
            // 这里原先兜底"保形平滑"（把这一笔换成误差带内的光滑曲线，§43 第一步），
            // 2026-09-25 用户上手之后**取消**了 —— 原话："把'保形平滑'这个功能取消掉吧，
            // 因为它影响我画抛物线"。
            // 原因很实在：平滑**只在"认不出图形"时生效**，而**画抛物线（尤其只画半支的）
            // 经常正好落在这一档** —— 于是老师画的那条曲线被悄悄换成另一条，
            // 看着像"它自己变了形"。而这恰恰违反 §42 那条老规矩。
            //
            // 换成"什么都不做"之后：**认不出 = 良性失败**（墨还是他自己那一笔），
            // 规矩回到"**宁可不变，也不能变出一个对不上的**"。
            return;
        }

        if (InkLogEnabled) Console.WriteLine($"[成型] 认出了 {guess.Kind}：{guess.Rule}");
        if (InkRecordPath != null) _recordGuess = $"认出了 {guess.Kind}：" + guess.Rule;
        ArmDwellShape(ink, guess);
    }

    /// <summary>
    /// **把这一笔写进录制文件**（见 `InkRecordPath`）——`EndStroke` 一进来就调。
    ///
    /// 格式（好读、也好写脚本解析）：
    /// <code>
    /// --- stroke 3  t=812345  guess=没认出来：两笔不像同一个双曲线的两支（…）
    /// 301.25,400.50
    /// 305.10,402.75
    /// …
    /// </code>
    /// 段头一行给出"第几笔 / 什么时候 / 当时认成了什么（或为什么没认）"，
    /// 后面一行一个原始采样点。**连续两笔就是一条双曲线**（我按这个来配对统计）。
    ///
    /// ⚠ 写不进去（路径不对 / 文件被占）**绝不能影响画图** —— 整段包在 try 里，
    ///   失败了只往控制台说一声。
    /// </summary>
    private void FlushInkRecord()
    {
        if (InkRecordPath == null) return;

        // `--recinkp`：除了坐标，**再录压力和"这个点是从哪条路来的"**。
        //
        // 为什么需要（2026-10-07）：用户报"笔记很脏"，而日志里的数字全都对得上 ——
        // 光看数字已经查不下去了，必须看到**真实的点**：
        // 到底是压力写错了、还是补点把位置补歪了、还是别的。
        // 来源那一列是关键：`1` = raw 补点，`0` = 指针消息，一眼就能分辨。
        if (InkRecordWithPressure && ActiveStroke != null && ActiveStroke.Points.Count >= 2)
        {
            try
            {
                using var wp = new StreamWriter(InkRecordPath, append: true);
                _recordSeq++;
                wp.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  hp={(ActiveStroke.HasPressure ? 1 : 0)}"
                             + $"  pts={ActiveStroke.Points.Count}");
                var rawSet = new System.Collections.Generic.HashSet<int>(_rawAddedIdx);
                for (int i = 0; i < ActiveStroke.Points.Count; i++)
                {
                    var q = ActiveStroke.Points[i];
                    wp.WriteLine($"{q.X:F2},{q.Y:F2},{q.P:F4},{q.T:F1},{(rawSet.Contains(i) ? 1 : 0)}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[录墨迹] 写失败：{ex.Message}"); }
            return;
        }

        // 优先用"识别器看到的那串点"；这一笔没停顿（没触发识别）就用收笔时的手绘点兜底。
        // ⚠ 兜底要**点数够**才算：停顿成型后 `ActiveStroke` 已经被换成图形对象（只剩两三个
        //   定义点），那种不能当墨迹录进去（否则我会拿一堆"两根点"当样本 ✗）。
        var pts = _recordPts;
        if ((pts == null || pts.Length < 8) && ActiveStroke != null && ActiveStroke.Points.Count >= 8)
        {
            pts = new Vector2[ActiveStroke.Points.Count];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new Vector2(ActiveStroke.Points[i].X, ActiveStroke.Points[i].Y);
        }
        try
        {
            using var w = new StreamWriter(InkRecordPath, append: true);
            _recordSeq++;
            if (pts == null || pts.Length < 8)
                w.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  "
                            + $"guess=（这一笔没录到原始点：没停顿、或点数不够）");
            else
            {
                w.WriteLine($"--- stroke {_recordSeq}  t={NowMs:F0}  "
                            + $"guess={(_recordGuess.Length == 0 ? "（没到识别那一步）" : _recordGuess)}");
                foreach (var p in pts) w.WriteLine($"{p.X:F2},{p.Y:F2}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[录墨迹] 写不进去（{InkRecordPath}）：{ex.Message}");
        }
        _recordPts = null;
        _recordGuess = "";
    }

    /// <summary>
    /// **认出图形了**：把正在写的这一笔换成幽灵预览（用户还按着笔）。
    ///
    /// 做法就是**换掉 `ActiveStroke` 指向的对象**（换成一个普通图形对象）：
    ///   · 渲染不用写一行新代码——"正在书写的那一笔"本来就是每帧重画的（见 `DrawStroke`）；
    ///   · 抬手时 `EndStroke` 提交的就是这个对象，**图形从来没进过文档**，
    ///     所以"一步撤销回手绘"靠的是 <see cref="DwellShapeAction"/> 把原迹带在记录里。
    /// </summary>
    private void ArmDwellShape(Stroke ink, in ShapeGuess guess)
    {
        // B4.1（2026-10-08 真机）：**换笔必须复位预测尾**——下面会把 ActiveStroke 从
        // 手绘笔迹换成图形，但预测器还留着旧笔迹的末点/速度；不复位的话，
        // 下一帧 `UpdatePredictTail` 会把"预测点 − 图形末点"当尾巴偏移，从图形末端
        // 画出一条指向旧原点的长尾（用户真机："拖端点会飘出另外一条线段，慢慢长回
        // 变直线时的原始位置点、松手消失"）。原理与清单见 调研-预测尾-原理与注意点.md §二。
        ResetPredictTail();
        var shape = DwellAssist.BuildShapeStroke(guess, ink);

        // 停手 = 这一笔不再长了：把"正在写"的三条通道全收掉。
        // 不收的话屏幕上会同时留着一条手绘的歪线和一条规整图形（InkClass 那边
        // 为此要切 `EditingMode`，还留下过"两条线""预览残留"一串坑；我们这边
        // 采样和上屏都在引擎手里，所以要收的只有这三处）。
        foreach (var w in _windows)
            w.EndInkTrail();
        _systemWetStroke = null;

        var anchor = _dwell.Anchor;                       // 笔停住的位置
        double stillMs = _dwell.StillMs(NowMs);           // 复位之前先量

        if (guess.Kind == StrokeKind.Line)
        {
            // ── **直线：唯一的例外，继续当"幽灵"按住**（用户 2026-09-24 定）────────
            // 直线的另一头正是画完最常要调的（转成水平 / 竖直），按住就能拖——比"先松手、
            // 再点它、再拖手柄"少两步。抬手才定型，而且**不选中**（顺手一划，接着写）。
            // 要记两件事：
            //   · 起步死区基准：笔尖还在这儿附近 → 线一个像素都不改（见 DwellDragSlopLogical）；
            //   · 钉住的那一头：**离笔尖远的那一头**。不能拿 `Points[0]`：识别器给的端点是
            //     "拟合方向的投影极值"，哪一头落在那里**不定**，钉错时抖一下就把线压成零长度
            //     （用户 2026-09-24 报的"竖线变成很短的小横线"）。
            _dwellInk = ink;
            ActiveStroke = shape;
            _dwellArmAnchor = anchor;
            _dwellGhostOrigin = null;   // 直线走"拖另一头改形状"那条路，不吃幽灵改大小的基准
            _dwellGhostAnchorIdx = -1;
            _dwellGhostNearIdx = -1;
            if (guess.Def.Length >= 2)
                _dwellLinePin = Vector2.Distance(guess.Def[0], anchor) >= Vector2.Distance(guess.Def[1], anchor)
                              ? guess.Def[0] : guess.Def[1];
            // 读数在**成型这一刻就位**（用户 2026-10-05 报的两个 bug：
            //   ① 第一次用时胶囊飞在屏幕左上角——`_shapeAnchor` 还是默认的 (0,0)；
            //   ② 之后每次都先停在上一条线上——那是上一条留下的锚点，笔一动才跳回来。
            // 锚点挂在**离笔尖近的那一头**（接下来会跟着笔尖走的那一端），
            // 和拖动中的算法同一条判据（`_dwellLinePin` 取的就是远的那一头）。
            if (ActiveStroke.Points.Count >= 2)
            {
                var q0 = new Vector2(ActiveStroke.Points[0].X, ActiveStroke.Points[0].Y);
                var q1 = new Vector2(ActiveStroke.Points[^1].X, ActiveStroke.Points[^1].Y);
                _shapeInclination = SelectionHandles.InclinationDegrees(q0, q1);
                _shapeLength = Vector2.Distance(q0, q1);
                _shapeInclinationSnapped = false;
                _shapeAnchor = Vector2.Distance(q0, anchor) <= Vector2.Distance(q1, anchor) ? q0 : q1;
            }
            _dwell.Fire();
            _dirty = true;
            Console.WriteLine($"[停顿成型] 停 {stillMs:F0}ms → {guess.Kind}（{guess.Rule}）"
                              + "；按住还能拖另一头转向，松手定型（**不选中**，接着写）");
            return;
        }

        // ── **其它图形：也是幽灵 —— 按住拖到位，松手才定型 ＋ 选中** ──────────────────
        // 用户 2026-09-25 试过 ClassIn 之后改的（上一版 2026-09-24 是"识别到就定型并选中"）：
        // ClassIn 的手感是"图形变出来以后先别松手，直接把它拖到位，松手才选中"——
        // 比"松手 → 再点它 → 再拖"少两步，而画完就想挪位置这件事在板书里非常常见。
        //
        // ⚠ 和 2026-09-24 砍掉的那套幽灵**不是一回事**：砍掉的是"按住改大小 / 转角"，
        //   这里**只平移**（见 ArmedStrokeMove）——定义元素之间的相对关系一个都不动，
        //   而那批"图形消失 / 变形"的病因全出在改几何上。
        //   机制也完全复用直线那条：图形**从来没进过文档**，它就是 `ActiveStroke`；
        //   抬手时 `EndStroke` 提交（`_dwellInk != null` 那条路），并因为"不是直线"
        //   走 `committedShape` → 自动选中。
        _dwellInk = ink;
        ActiveStroke = shape;
        _dwellArmAnchor = anchor;
        var org = new Vector2[shape.Points.Count];
        for (int i = 0; i < org.Length; i++)
            org[i] = new Vector2(shape.Points[i].X, shape.Points[i].Y);
        _dwellGhostOrigin = org;

        // **锚点 / 跟着笔尖走的那一点**：**成型时挑定、拖动全程不变**。
        //   · 锚点 = 离笔尖**最远**的那个定义元素 —— 和直线的 `_dwellLinePin` **同一条判据**
        //     （人收笔就停在"终点"那一带，所以远端才是"起点"）；
        //   · 跟着走的 = 离笔尖**最近**的那一个（= 用户拖出来的那个终点）。
        // ⚠ 两个下标必须在这里定死：拖动中再算的话，指针一移动它们就可能互换
        //   → 图形会"翻来翻去"（同一个病根见 §42.6.2 第三条：直线当初拿 `Points[0]` 当支点）。
        //
        // ★ **双曲线：锚点钉死"中心"**（用户 2026-09-25 定）：
        //     "它在幽灵模式下，应该是**按照中心为锚点**进行放大缩小。"
        //   通用规则（离笔尖最远的那一个）在这里会挑错 —— 双曲线有**三个**定义元素
        //   （中心 / 渐近线角点 / 经过点），笔尖停在支线上时"离笔尖最远的"很可能是
        //   **渐近线角点**，那样缩放就绕那个角点转，图形会整个跑偏 ✗
        //   所以中心一律不动，**跟着笔尖走的那一个在"其余两点"里挑**（离笔尖最近的）。
        if (guess.Kind == StrokeKind.Hyperbola && org.Length >= 3)
        {
            _dwellGhostAnchorIdx = 0;                       // 中心 = `Points[0]`，一律不动
            _dwellGhostNearIdx = 1;
            float nD = float.MaxValue;
            for (int i = 1; i < org.Length; i++)
            {
                float dd = Vector2.Distance(org[i], anchor);
                if (dd < nD) { nD = dd; _dwellGhostNearIdx = i; }
            }
        }
        else
        {
            _dwellGhostAnchorIdx = 0;
            _dwellGhostNearIdx = 1;
            float farD = -1f, nearD = float.MaxValue;
            for (int i = 0; i < org.Length; i++)
            {
                float dd = Vector2.Distance(org[i], anchor);
                if (dd > farD) { farD = dd; _dwellGhostAnchorIdx = i; }
                if (dd < nearD) { nearD = dd; _dwellGhostNearIdx = i; }
            }
        }
        if (org.Length < 2 || _dwellGhostAnchorIdx == _dwellGhostNearIdx)
        {
            // 退化（少于两个点 / 两点重合）→ 不吃幽灵改大小，只预览（和 2026-09-24 那版一样）
            _dwellGhostAnchorIdx = -1;
            _dwellGhostNearIdx = -1;
        }

        _dwell.Fire();
        _dirty = true;
        Console.WriteLine($"[停顿成型] 停 {stillMs:F0}ms → {guess.Kind}（{guess.Rule}）"
                          + "；按住能拖到位，松手定型（**并选中**；Ctrl+Z 可回手绘）");
    }

    /// <summary>
    /// armed 之后的指针移动。返回 true = 这一下被"幽灵预览"吃掉了（不再往笔迹里堆点）。
    ///
    /// **两种图形都吃移动**（用户 2026-09-25 试过 ClassIn 之后定），差别只在"拖的是什么"：
    ///   · **直线**：**离笔尖远的那一头钉住**、拖出去就是**转向 / 伸缩**（照 ClassIn / InkClass 的
    ///     `LineAssistMove`），吸附**和画直线同一套**（特殊角软吸附 ±1°、Shift 15° 硬网格、
    ///     Alt 自由；2026-10-05 统一——原来只吸 0/90、容差 4°）—— 画坐标轴就靠这一下。留着它，是因为
    ///     直线是"顺手一划"，它的另一头正是画完最常要调的（转成水平 / 竖直），
    ///     而在选中态里调要多两步（先点它、再拖手柄）。
    ///   · **其它图形**：**改大小**（用户 2026-09-25 上手 ClassIn 之后逐条定的）——
    ///     **锚点不动，拖出去就是改另一个对角 / 半径**：
    ///     圆形"圆心不动、改变圆的大小"；矩形"有一个顶点不动，相当于拖另外一个对角线"；
    ///     而且"**旋转功能好像不能转了，应该就是拖动改大小**"（幽灵期不做旋转）。
    ///     通用规则 = **离笔尖最远的那个定义元素钉住、离笔尖最近的那个跟笔尖走**，
    ///     其余定义元素按**逐分量比例**跟着走（见下面实现）。
    ///     ⚠ 和 2026-09-24 砍掉的那套**不是**同一批代码：那三条"图形消失 / 变形"的病因
    ///     查清了**都不是幽灵态的错**（是 `EndStroke` 两道门槛量错了对象、以及直线锚点
    ///     拿了 `Points[0]` —— 三条现在都已修，见 §42.6.2）；而同节实测的"所有图形都在
    ///     跟着手抖改大小"则由**起步死区**挡着（下面第一段）。
    ///     抬手 → 定型 + 自动选中（见 `EndStroke`）。
    /// </summary>
    private bool ArmedStrokeMove(float x, float y, float screenX, float screenY)
    {
        var s = ActiveStroke;
        if (s == null) return false;

        var p = new Vector2(x, y);
        // **起步死区**（两条路共用）：笔尖还在成型那一刻的位置附近（只有驱动上报的亚像素抖动）
        // → 一个像素都不改。没有它，"按住不动"其实一直在改：直线那边笔尖正好停在支点
        // 那一头时会把线压成零长度（用户 2026-09-24 报的"竖线变成很短的小横线"；
        // `--dwelltest` H6 实测漂移 299.51）；图形这边会看到图形"自己抖着改大小"。
        // ⚠ **这一条是幽灵改大小的前提**（不是可选优化）：§42.6.2 实测过"所有图形当时都在
        //   跟着手抖改大小（1.0~3.4 画布单位）"，没有死区，重新加回来就是把那个毛病一起加回来。
        if (Vector2.Distance(p, _dwellArmAnchor) <= DwellDragSlopLogical * DpiScale)
            return true;      // 这一下照样被"幽灵预览"吃掉，只是不改

        // ── 直线：拖另一头 → 转向 / 伸缩（改形状）────────────────────────────────
        if (s.Kind == StrokeKind.Line)
        {
            if (s.Points.Count < 2) return true;
            // **和"画直线"同一套吸附**（用户 2026-10-05："停顿变直线那个吸附太大，
            // 和画直线统一一下"）：软吸附到特殊角 ±1°，Shift = 15° 硬网格、Alt = 自由。
            // 原来走的是识别器那套"只吸 0/90、容差 4°"（`SnapToAxis`）——两条路各一套、
            // 容差差 4 倍；长线上一偏就是几十像素。现在**只有 `SnapEndPoint` 这一处权威实现**。
            bool shift = (Native.GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0;
            bool alt = (Native.GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0;
            var a = _dwellLinePin;
            var b = SelectionHandles.SnapEndPoint(a, p, shift, alt, out bool snapped);
            s.SetPoints(new[] { a, b });
            _shapeInclination = SelectionHandles.InclinationDegrees(a, b);
            // 长度读数和 α 同源同帧。⚠ 这一位原来没写：幽灵期拖长拖短，标签里的"长"
            // 一直停在 0/旧值（用户 2026-10-05："拉长变短，那个长度也没变"）。
            _shapeLength = Vector2.Distance(a, b);
            _shapeInclinationSnapped = snapped;
            _shapeAnchor = b;
            return true;
        }

        // ── 其它图形：改大小（锚点不动，拖出去 = 改另一个对角 / 半径）────────────────
        // 用户 2026-09-25 上手 ClassIn 逐条定的规则：
        //   (a) 圆形：**圆心不动**，改变圆的大小；
        //   (b) 矩形：**有一个顶点不动**，拖动相当于拖另外一个对角线；
        //   (c) **旋转功能不能转**，就是拖动改大小。
        // 下面这一小段同时覆盖 (a)(b)(c)，而且**一行几何都不重写**（照 42.9 第 3 条）：
        //   ① 锚点（离笔尖最远那个定义元素）**一个像素都不动** → (a)(b) 的"不动"那半句；
        //   ② "跟着走的那个"（离笔尖最近的定义元素）**直接设成指针** → 对圆就是
        //      "圆周点搬到指针上"，半径自然 = |指针 − 圆心|，圆心没动 —— 正好是 (a)；
        //      对矩形就是"另一个对角 = 指针"，起始角没动 —— 正好是 (b)；
        //   ③ 其余定义元素（三角形 / 平行四边形 / 坐标系那种三个点以上的）按**逐分量比例**
        //      跟着走，比例从"锚点 → 跟着走的那点"这一段算；
        //   ④ 全程只挪点、**不做姿态旋转** → (c)。
        // ⚠ ③ 的除法要防零：某分量在基准里就是 0（比如圆的圆周点正好在正右方）时，
        //   那个分量取 1（不变）。②对"跟着走的那个"是**直接赋值**，不经过除法，所以
        //   圆永远不会因为这个退化。
        if (_dwellGhostOrigin == null || _dwellGhostOrigin.Length < 2
            || _dwellGhostAnchorIdx < 0 || _dwellGhostNearIdx < 0) return true;

        int n = _dwellGhostOrigin.Length;
        var moved = new Vector2[n];
        for (int i = 0; i < n; i++) moved[i] = _dwellGhostOrigin[i];

        var A = _dwellGhostOrigin[_dwellGhostAnchorIdx];
        moved[_dwellGhostAnchorIdx] = A;          // ① 锚点不动（写一遍，意思写在代码里）
        moved[_dwellGhostNearIdx] = p;            // ② 跟着笔尖走的那一点

        var e0 = _dwellGhostOrigin[_dwellGhostNearIdx] - A;
        var e1 = p - A;
        float sx = MathF.Abs(e0.X) > 1e-3f ? e1.X / e0.X : 1f;
        float sy = MathF.Abs(e0.Y) > 1e-3f ? e1.Y / e0.Y : 1f;
        for (int i = 0; i < n; i++)               // ③ 其余点按比例
        {
            if (i == _dwellGhostAnchorIdx || i == _dwellGhostNearIdx) continue;
            var rel = _dwellGhostOrigin[i] - A;
            moved[i] = A + new Vector2(rel.X * sx, rel.Y * sy);
        }
        s.SetPoints(moved);
        return true;
    }

    /// <summary>
    /// **取消选中那一击不留墨**（用户 2026-09-23 定）：这一笔如果是"点一下"
    /// （**笔尖走过的总路程** ≤ <see cref="DwellTapSlopLogical"/> 逻辑像素），
    /// 就不提交、不进撤销栈。
    ///
    /// 为什么单独定这条：点一下空白原本会落**一个点**（`Stroke.IsSinglePoint` 渲染成一个实心圆点），
    /// 而"点一下别处"的意图是"收起那个选中框"，留一个墨点等于**每次取消选中都脏一块屏幕**
    /// （InkClass 也是这么处理的，见它的 `TryDiscardDismissTapStroke`）。
    ///
    /// ⚠ **判据为什么不是"首末两点的位移"**（两个来源都那么写，这里都不能参考）：
    ///   · 参考实现 Ink Canvas 用的是"**抬起点 − 按下点** ≤ 6px"
    ///     （`画布测试/Ink-Canvas-Dev/Ink Canvas/MW_PopupLayers.cs:230` 的
    ///      `DismissTapMaxMovePx`；它只在"按下真的收起了可见面板"时才立这个标记，
    ///      所以踩到的机会少）；
    ///   · 我们早先量的是**笔迹首末两点**。
    ///   两者对"点一下"都对，但对"**一笔画成个闭合圈**"（圆 / 四边形 / 五边形……首末两点
    ///   天生重合，位移 ≈ 0）会把**整笔**判成"点一下" → 用户的墨凭空消失。
    ///   这不是推演：`--dwelltest` 的 L 扫掠当场抓到 6 例（"五边形（认不出）"三档尺寸 ×
    ///   两档乱动 × 带选中，全部消失）。
    ///   换成**路程**就天然分得开：点在原地 ≈ 0，任何真画的一笔都是几十上百像素。
    /// </summary>
    private bool DwellTapLeaveNoInk(Stroke s)
    {
        if (s.Points.Count < 2) return true;
        var pts = new Vector2[s.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(s.Points[i].X, s.Points[i].Y);
        return ShapeRecognize.PathLength(pts) <= DwellTapSlopLogical * DpiScale;
    }

    // ---- 自检入口（`--dwelltest` 用；四条都是"薄封装"，不含任何逻辑）--------
    //
    // 为什么做成四个薄壳而不是一个"大模拟函数"：自检要能在**中间任何一步**断言
    //（比如"停 559ms 时**还没**变"），塞进一个函数里就只能在最后看一眼结果。
    // 时钟是自检**直接推 `NowMs`** 的——不许 sleep（600ms 一次、十几个用例，
    // 自检会从"一眼看完"变成"等十秒"，而且真机上时序不可复现）。

    /// <summary>自检用：按一下（**连"笔下面那个选中框"的分流一起走**，判据就是
    /// <see cref="TryDwellSelectionPress"/> 那一份，和真按下同源）。</summary>
    internal void DwellBeginForTest(float x, float y)
    {
        if (TryDwellSelectionPress(x, y)) return;      // 被选择手势吃掉：这一笔不该起
        BeginFreehandStrokeAt(0, Native.PT_MOUSE, x, y, x, y, 0.5f);
    }

    /// <summary>自检用：喂一个采样点（走真入口 <see cref="ExtendFreehandStroke"/>）。</summary>
    internal void DwellMoveForTest(float x, float y)
    {
        // 和真入口 `OnPointerMove` 一样：**手里没有活笔画就没有"移动"可言**
        // （非直线是"识别到就定型"，那一刻这一笔已经结束、`ActiveStroke` 已放手；
        //  真机的移动落在 `OnPointerMove` 的 `if (ActiveStroke != null)` 外面，什么都不做）。
        // 这里少写这一句的话，自检喂点会直接捅进 `ExtendFreehandStroke` 里的空引用。
        if (ActiveStroke == null) return;
        ExtendFreehandStroke(0, Native.PT_MOUSE, x, y, x, y, 0.5f);
    }

    /// <summary>自检用：假装 40ms 定时器响了一次。</summary>
    internal void DwellTickForTest() => TickDwellShape();

    /// <summary>自检用：抬手（走真入口 <see cref="EndStroke"/>）。</summary>
    internal void DwellEndForTest() => EndStroke();

    // ---- 黑匣子（`--reclive` / `--replayinput`）用的薄钩子 -------------------------
    // 回放的原则和 DwellProbe 一样：**走真入口、推时钟**——起笔/采样/收笔都进真函数，
    // 这样净化、建模、预测器全部按当时的同一套代码复现。

    /// <summary>黑匣子回放：起笔（含 ptype / 压力）。</summary>
    internal void ReplayBeginForTest(float x, float y, float p, uint ptype)
        => BeginFreehandStrokeAt(0, ptype, x, y, x, y, p);

    /// <summary>黑匣子回放：喂一个采样点（和 `AddPointCleaned` 同入口、同时钟口径[NowMs]）。</summary>
    internal void ReplaySampleForTest(float x, float y, float p) => AddPointCleaned(x, y, p, NowMs);

    /// <summary>黑匣子回放：收笔。</summary>
    internal void ReplayEndForTest() => EndStroke();

    /// <summary>预测器诊断（黑匣子回放报告用）——2026-10-15：线性预测没有"门"这套离散计数，
    /// 回放报告里的门触发列随之撤除。</summary>

    /// <summary>自检用：走真入口画一个单笔图形（起手 → 控制点 → 抬手）。</summary>
    internal void ShapeDragForTest(float ax, float ay, float bx, float by)
    {
        BeginShapeAt(Tool, ax, ay);
        UpdateShapePreview(bx, by);
        EndStroke();
    }

    /// <summary>
    /// 自检用：按正式多笔表模拟一次完整图形（1 笔或 2 笔都用同一份 StepPlan）。
    /// 每个 tuple = 这一笔的起点 / 终点；`steps.Length` 由 <see cref="ShapeStepCount"/> 给出。
    /// </summary>
    internal void ShapeStepsForTest(Tool tool, (float Ax, float Ay, float Bx, float By)[] steps)
    {
        var plan = PlanOf(tool);
        if (plan == null || plan.Apply.Length <= 1)
        {
            BeginShapeAt(tool, steps[0].Ax, steps[0].Ay);
        }
        else
        {
            BeginStepShape(tool, plan, steps[0].Ax, steps[0].Ay);
        }
        for (int i = 0; i < steps.Length; i++)
        {
            UpdateShapePreview(steps[i].Bx, steps[i].By);
            EndStroke();
        }
    }

    /// <summary>自检用：直接拖动某个图形的定义点（走正式 Begin/Update/Commit）。</summary>
    internal void VertexDragForTest(Stroke s, ShapeHandle h,
                                    float x0, float y0, float x1, float y1)
    {
        BeginVertexDrag(s, h, x0, y0);
        UpdateVertexDrag(x1, y1);
        CommitVertexDrag();
    }

    /// <summary>自检用：面板选卡的一次性图形还等不等“画完回去”。</summary>
    internal bool ShapeOneShotForTest => _shapeOneShot;

    /// <summary>自检用：当前图形工具是不是从面板卡片选的。</summary>
    internal bool ShapeFromPanelForTest => _shapeFromPanel;

    /// <summary>自检用：状态机在哪一档。</summary>
    internal DwellState DwellStateForTest => _dwell.State;

    /// <summary>自检用：这一次自动选中是不是"停顿变出来的"。</summary>
    internal bool DwellSelectedForTest => _dwellSelected;

    /// <summary>自检用：开关（默认开的那个）。用完要还原。</summary>
    internal void SetDwellEnabledForTest(bool on) => DwellShapeEnabled = on;

    private void OnPointerMove(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntMove++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        if (!ReadPointer(id, out float sx, out float sy, out float pressure, out bool inverted, out uint ptype)) return;
        LastPointerType = ptype;
        TouchDiagFeed(id, ptype, down: false);
        float screenX = sx, screenY = sy;
        float x = sx, y = sy;
        ScreenToCanvas(ref x, ref y);   // 相机：屏幕 → 画布，下游全按画布坐标走
        PointerX = x; PointerY = y; PointerInside = true;

        // 字体下拉：拖动滚动（含惯性）。
        if (FontListPointerMove(x, y, id)) { _dirty = true; return; }

        // 文本框右侧滚动条拖动：只滚内容，不滚画布。
        if (_textBoxScrollDragging)
        {
            UpdateTextBoxScrollDrag(y);
            return;
        }

        // 触摸滚动中：只认第一根手指；其它手指的移动吞掉。
        if ((_libraryTouchScrollArmed || _libraryTouchScrolling)
            && ptype == Native.PT_TOUCH && id != _libraryTouchScrollId)
            return;

        if (ColorPickActive)
        {
            UpdateColorPickHover(x, y);
            _dirty = true;
            return;
        }

        // 回放：控制条悬停 / 拖进度（屏幕坐标，和条自己的坐标系一致）。
        // 拖进度时不再往下走——这一串移动归控制条。
        if (OverlayLayers.CaptureLayer?.Id == "replay" && ReplayScrubbing)
        {
            if (OverlayLayers.PointerMove(screenX, screenY, id))
            {
                _dirty = true;
                ApplyCursor();
                return;
            }
        }
        if (_replay != null)
        {
            ReplayPointerMove(screenX, screenY);
            if (_replayScrubbing) return;
        }

        // A4.9：library 被管理器捕获时，移动也先走管理器（含卡片拖动/抽屉拖动）；
        // 未消费时仍落回下面的旧 library 逻辑。
        if (OverlayLayers.CaptureLayer?.Id == "library")
        {
            if (OverlayLayers.PointerMove(x, y, id))
            {
                ClearEngineTooltip();
                ApplyCursor();
                _dirty = true;
                return;
            }
        }

        // 整理 / 页签 / 滚动条：这一串移动归收藏面板。
        if (LibraryPanelVisibleNow && LibraryPanelPointerMove(x, y, id))
        {
            ClearEngineTooltip();
            ApplyCursor();
            return;
        }

        // 收藏弹窗：拖标题栏 / 拖右下角。拖动期间整串移动都归它。
        if (LibraryPanelVisibleNow && LibraryPanelDragMove(x, y))
        {
            ClearEngineTooltip();
            ApplyCursor();
            return;
        }

        // 选中的收藏跟着指针走：每次移动都重画一帧，虚影才不粘滞。
        if (LibraryPanelVisibleNow && LibraryUseSelected != null && !_drawing)
            _dirty = true;

        // 图库面板的悬停（和按下同一条口径：面板是最上面那一层）。
        // 格子亮一下是"这一格点得中"的反馈；光标停在复选框上也是同一套。
        if (LibraryPanelVisibleNow && !_drawing && !InUiPopup(x, y))
        {
            var m = LibraryPanelInfoNow();
            var panel = m.Panel;
            bool inside = LibraryLayout.Contains(panel, x, y);
            int hov = inside ? LibraryLayout.CellAt(m, LibraryEntries.Count, x, y, LibraryScrollY) : -1;
            if (hov != LibraryHover)
            {
                LibraryHover = hov;
                _dirty = true;      // 悬停高亮变了才重画，不是每次移动都重画
            }
            // 进 / 出面板各标一次脏：从画布带进来的落点环（笔 / 橡皮的圈）要擦掉。
            // 光靠脏区的"前两帧临时图元"不够——最后一步移动若没别的原因标脏，
            // 就不会有渲染帧去擦那一圈（屏幕上会留一个圆环印子）。
            if (inside != _onDrawnChrome) { _onDrawnChrome = inside; _dirty = true; }
            if (inside) { ApplyCursor(); return; }     // 面板里：吃掉，别让底下的内容跟着动
        }

        // 界面捕获了指针（例如按下按钮后滑出去），消息全归界面。
        if (UiCapturing)
        {
            ClearEngineTooltip();
            UiPointerMove(screenX, screenY, pressure, false, inverted,
                          ptype == Native.PT_TOUCH, id);   // 逻辑屏幕坐标
            _dirty = true;
            return;
        }

        // 界面优先：悬停也要转发。
        //
        // 悬停高亮、"靠近才长大"、"悬停 120 ms 才浮出"全靠这条。以前只有
        // **捕获分支**会转发，而"鼠标停在按钮上"恰恰是没捕获的状态——
        // 于是界面的悬停永远不会亮，而且不报错，只是"感觉不跟手"。
        // 书写中不转发：那一笔已经归画布了，界面这时候不该再动。
        if (!_drawing && UiPointerMove(screenX, screenY, pressure, ptype == Native.PT_PEN, inverted,
                                       ptype == Native.PT_TOUCH, id))
        {
            ClearEngineTooltip();                 // 指针在界面那条上：引擎侧提示让位
            if (!_uiHover) { _uiHover = true; ApplyCursor(); }
            _dirty = true;
            return;
        }
        if (_uiHover) { _uiHover = false; ApplyCursor(); }

        // A4.6：计时/点名/PPT 一旦被管理器捕获，移动也先走管理器；
        // 未消费时仍落到下面的旧 hover/drag 逻辑。
        if (OverlayLayers.CaptureLayer?.Id is "timer" or "roll" or "ppt")
        {
            if (OverlayLayers.PointerMove(screenX, screenY, id))
            {
                ClearEngineTooltip();
                _dirty = true;
                ApplyCursor();
                return;
            }
        }

        // 课堂计时卡片：拖动跟手 / 悬停（排在 PPT 条之前、穿透之前——同按下顺序）。
        if (!_drawing && TimerCardPointerMove(screenX, screenY))
        {
            ClearEngineTooltip();
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 课堂点名卡片：同上。
        if (!_drawing && RollCardPointerMove(screenX, screenY))
        {
            ClearEngineTooltip();
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 底部那两条 PPT 控件（放映时才在）：悬停高亮 / 进度条拖动。
        // 排在穿透之前——穿透时它的悬停与拖动照样要跟手。
        if (!_drawing && PptBarPointerMove(screenX, screenY))
        {
            UpdateEngineTooltip();                // 条上的悬停提示（页码/箭头/菜单）
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 穿透模式：我们不收输入，也不该动光标（那是下层窗口的事）。
        if (PassThrough) { ClearEngineTooltip(); _dirty = true; return; }

        // 拖滚动条：和"画一笔"互斥。
        if (ScrollBarDragging)
        {
            ClearEngineTooltip();
            UpdateScrollBarDrag(screenY);
            ApplyCursor();
            _dirty = true;
            return;
        }

        // 触摸手势的移动（8.4.0）：**排在"同一指针"那道判定之前**——
        // 双指手势里第二根手指的移动本来会被它挡掉（id != _activePointer）。
        if (_touchMode != TouchMode.None && TouchMoveDispatch(id, x, y))
        {
            ApplyCursor();
            return;
        }

        if (!_drawing || id != _activePointer)
        {
            // 悬停路径。以前这里直接 return，"没落笔"时引擎完全不知道指针在哪儿，
            // 于是悬停光标、滚动条悬停、落点预览都无从谈起。
            UpdateScrollBarHover(screenX, screenY);
            UpdateBarHover(x, y);                  // 操作条九格的 hover 态（画与命中同源）
            UpdateEngineTooltip();                 // 引擎侧悬停提示（操作条 / PPT 条）
            ApplyCursor();
            if (DrawnCursor != ToolCursorShape.None) _dirty = true;
            return;
        }

        // 截图的"松开后调整"（8.3.0）：拖手柄/拖整体。**和当前工具无关**。
        // P5.1：被管理器捕获时移动先走管理器；未捕获时仍走旧分支。
        if (OverlayLayers.CaptureLayer?.Id == "capture")
        {
            if (OverlayLayers.PointerMove(x, y, id))
            {
                _dirty = true;
                ApplyCursor();
                return;
            }
        }
        if (CaptureAdjusting) { CaptureAdjustMove(x, y); return; }

        // 浮层面板里的拖动（粗细滑条 / 自定义取色板）：**和当前工具无关**。
        // 面板在任何工具下都可能开着（图形工具那个自动选中的框也能摊开），
        // 旧实现只在 Marquee 分支里处理滑条——图形工具里就拖不动。
        if (_sliderDragging) { DragWidthSliderTo(x); return; }
        if (_colorDragging) { DragColorPickerTo(x, y); return; }

        // 选择拖动 / 框选：**按状态路由，不依赖 `_touchMode`**（2026-09-30 修）。
        // 触摸长按成立之后，模式字段可能被异常路径（丢捕获等）清掉——那时拖动会掉进
        // 工具分支、被当成"接着写一笔"（实测症状：长按选中了对象，但拖不动）。
        // 这两条对鼠标那条路是**同一个调用**（原来在 Marquee 分支里），所以重复无害。
        if (SelDragging) { UpdateSelDrag(x, y); return; }
        if (MarqueeActive) { ExtendMarqueeTo(x, y); _dirty = true; return; }

        var tool = inverted ? Tool.Eraser : Tool;
        // 手测台：数"指针消息"而不是"擦除步"——消息之间的间隔才是跟不跟手。
        if (tool == Tool.Eraser || tool == Tool.PixelEraser) EraserTelemetry?.Move(NowMs);
        switch (tool)
        {
            case Tool.Eraser:
                EraseAlongPath(x, y);
                break;

            case Tool.PixelEraser:
                EraseRectAlongPath(x, y);
                break;

            case Tool.Capture:
                if (CaptureActive) ExtendCaptureTo(x, y);
                break;

            case Tool.Marquee:
                if (SelDragging) UpdateSelDrag(x, y);
                else ExtendMarqueeTo(x, y);
                break;

            case Tool.Laser:
                Laser.Add(x, y, NowMs);
                break;

            // 图形：只更新**控制点**（图形由它的定义元素决定），不往里堆采样点。
            // 预览就走"正在书写的那一笔"那条路（它本来就每帧重画），所以实时。
            //
            // 判据同样走 <see cref="IsShapeTool"/>，**不在这里逐个列 case**——
            // 和 OnPointerDown 那条是同一个理由（那边漏改过一次，两个地方一起修掉）。
            default:
                // 拖**手柄**（改几何）/ 拖整条，排在图形前面：这一类拖动是在**按下那一刻**
                // 被 OnPointerDown 接下的，而那时的工具可能还是图形工具
                //（画完自动选中那个框，见 `AutoSelectionPress` 那一段），也可能拖到一半
                // 被热键换了工具（见 SwitchTool）。两种情况 `ActiveStroke` 都是 null ——
                // 落到下面 UpdateShapePreview 上就什么都不发生（鼠标拖得动、模型一动不动）。
                if (SelDragging)
                {
                    UpdateSelDrag(x, y);
                    break;
                }
                if (ActiveStroke != null && IsShapeTool(tool))
                {
                    UpdateShapePreview(x, y);
                    break;
                }
                if (ActiveStroke != null)
                {
                    ExtendFreehandStroke(id, ptype, x, y, screenX, screenY, pressure);
                }
                break;
        }
        // 拖拽中（已捕获）：系统不会再发 WM_SETCURSOR，每帧自己维护。
        ApplyCursor();
        _dirty = true;
    }

    private void OnPointerUp(IntPtr hWnd, IntPtr wParam)
    {
        StampInput();
        _cntUp++;
        uint id = (uint)(wParam.ToInt64() & 0xFFFF);
        // 字体下拉：拖动结束 = 起惯性；没拖动 = 点行。
        if (FontListPointerUp(PointerX, PointerY, id))
        {
            _dirty = true;
            ApplyCursor();
            return;
        }
        if ((_libraryTouchScrollArmed || _libraryTouchScrolling)
            && id != _libraryTouchScrollId)
            return;
        if (ColorPickActive) return;
        // P5.1：截图取景被管理器捕获时，先清掉捕获；
        // 确认/取消仍走 `_capturePressHandled`，调整拖动仍走 `EndStroke` 里原来的截图分支。
        if (OverlayLayers.CaptureLayer?.Id == "capture" && OverlayLayers.CapturePointerId == id)
            OverlayLayers.PointerUp(PointerX, PointerY, id);
        // 划线选收尾：先于"卡片拖动"，也先于"面板拖动"（它们在这条路上是互斥的）。
        // A4.7：library 被管理器捕获时，松手交给管理器（含抽屉拖动收尾）。
        if (OverlayLayers.CaptureLayer?.Id == "library")
        {
            float lx = PointerX, ly = PointerY;
            if (ReadPointer(id, out float lsx, out float lsy, out _, out _, out _))
            {
                lx = lsx; ly = lsy;
                ScreenToCanvas(ref lx, ref ly);
            }
            OverlayLayers.PointerUp(lx, ly, id);
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (LibraryPanelVisibleNow && LibraryPanelPointerUp(PointerX, PointerY, id))
        {
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (_libraryDrag != 0)
        {
            LibraryPanelDragEnd();
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }
        if (TouchHud && _touchDiagIds.Remove(id))
        {
            RebuildTouchHud();
            _dirty = true;
        }

        // 界面优先收尾，否则会留下"按钮一直按着"的状态。
        if (UiCapturing && ReadPointer(id, out float ux, out float uy, out float upressure,
                                       out bool uinverted, out uint uptype))
        {
            UiPointerUp(ux, uy, upressure, uptype == Native.PT_PEN, uinverted,
                        uptype == Native.PT_TOUCH, id);
            // **必须显式放开捕获**：按钮上也走 SetCapture（拖出按钮、在按钮上滑开
            // 都要继续收到消息），而系统**不会**在按键抬起时替我们放开。
            // 忘了这一句的后果不是"按钮卡住"，而是**整台机器的鼠标事件都还挂在
            // 我们窗口上**：穿透、点下层程序、换到别的软件全乱，而且现象离原因很远。
            // 双指时第一根抬手不能放：另一根还在界面上，等最后一根再放。
            if (!UiCapturing) Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        if (ScrollBarDragging)
        {
            // 防御（2026-10-09）：这条路径在"触摸拖滚动条"时**必须先清触摸触点表**——
            // 真机 bug 的根因就是它：拖完抬手漏清 → 表里留下"幽灵手指" → 之后写字、
            // 再拖滚动条全被那张表吃掉（直到重启）。滚动条同日已停用，这句留着防复活。
            if (_activePointerType == Native.PT_TOUCH) _touch.Up(id);
            EndScrollBarDrag();
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // AI 题卡：按下归过它就无条件放开捕获（哪怕卡片已经被 ✕ 删掉）。
        if (_aiCardCapturing)
        {
            TextBoxPointerUp();
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // A4.4：计时/点名/PPT 的捕获所有权已交给 OverlayLayerManager；松手由它收尾。
        if (OverlayLayers.CaptureLayer is { Id: "timer" or "roll" or "ppt" })
        {
            if (ReadPointer(id, out float mx, out float my, out _, out _, out _))
                OverlayLayers.PointerUp(mx, my, id);
            else
                OverlayLayers.PointerUp(0, 0, id);
            _timerCapturing = false;
            _rollCapturing = false;
            _pptCapturing = false;
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 课堂计时卡片：**只要按下归过它，就无条件放开捕获**（哪怕卡片已经被 ✕ 关掉、
        // 或者 ReadPointer 这一次失败）——漏掉这一句就是"整机鼠标挂在我们窗口上"。
        if (_timerCapturing)
        {
            if (ReadPointer(id, out float tx, out float ty, out _, out _, out _))
                TimerCardPointerUp(tx, ty);
            _timerCapturing = false;
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 课堂点名卡片：同上。
        if (_rollCapturing)
        {
            if (ReadPointer(id, out float rx2, out float ry2, out _, out _, out _))
                RollCardPointerUp(rx2, ry2);
            _rollCapturing = false;
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // PPT 控件条（放映时才在）：**这一次按下是它吃掉的就由它收尾**——
        // 长按（拿起 / 拖走）和短按（弹页号面板）都在这里结束。
        // 必须显式 ReleaseCapture（按下那一刻 SetCapture 过）：忘了这一句，
        // **整台机器的鼠标都还挂在我们窗口上**（见上面那句注释）。
        if (_pptCapturing && ReadPointer(id, out float bx, out float by, out _, out _, out _))
        {
            _pptCapturing = false;
            PptBarPointerUp(bx, by);
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 回放控制条：这一下按下归它就由它收尾（拖进度条的松手也在这里）。
        // A4.3：回放条由 OverlayLayerManager 捕获时，松手也走管理器。
        if (OverlayLayers.CaptureLayer?.Id == "replay")
        {
            if (ReadPointer(id, out float rx0, out float ry0, out _, out _, out _))
                OverlayLayers.PointerUp(rx0, ry0, id);
            else
                OverlayLayers.PointerUp(0, 0, id);
            Native.ReleaseCapture();
            _drawing = false;
            _dirty = true;
            ApplyCursor();
            return;
        }

        // 触摸手势收尾（8.4.0）：和界面 / 滚动条那两块同一个形状——自己收尾、自己放捕获。
        // **不能等 `id != _activePointer` 那道闸**：双指手势里第二根手指的抬手本来会被它挡掉。
        if (TouchUpDispatch(id))
        {
            if (!_touch.Any)                    // 全部抬起：这一轮手势收场
            {
                Native.ReleaseCapture();
                _drawing = false;
                ApplyCursor();
            }
            _dirty = true;
            return;
        }

        if (id != _activePointer) return;
        Native.ReleaseCapture();
        _drawing = false;
        EndStroke();
    }

    /// <summary>动态橡皮：落笔那一刻把速度与系数归到基准（第一下就是滑条那个大小，移动中才渐入）。</summary>
    private void ResetDynamicEraser()
    {
        double now = NowMs;
        _eraseSpeedEma = 0f;
        _eraseFine = _eraseGross = 0f;
        _eraseFineDist = _eraseFineMs = 0f;
        _eraseGrossDist = _eraseGrossMs = 0f;
        _eraseDynFactor = 1f;
        _eraseTarget = 1f;
        _eraseHoldBaseMs = now;
        _eraseDecaying = false;
        _eraseTestIdle = 0f;
        _eraseLastMs = now;
        RebuildEraserHud();
    }

    /// <summary>
    /// 给定速度的**稳态**目标系数（自检和文档共用；回差带里取"从慢往上走"那一支）。
    /// 曲线：`0.8 以下 → 1.0`（基准，不缩）；以上 `1.0 + (v − 0.8) × 1.0`，夹到 1.0~2.5（2.3 到顶）。
    /// </summary>
    internal static float EraserTargetFactorForSpeed(float speedPxPerMs)
    {
        if (speedPxPerMs <= EraserSpeedBack) return EraserFactorMin;
        return Math.Clamp(EraserFactorMin + (speedPxPerMs - EraserSpeedKnee) * EraserSpeedSlope,
                          EraserFactorMin, EraserFactorMax);
    }

    /// <summary>
    /// 平滑一步：朝目标挪（涨/收时间常数不同 + 最少步长），带**回差**——
    /// 速度落在 [0.6, 0.8] 之间时目标保持不变，速度在门槛附近抖也不会来回切。
    ///
    /// <paramref name="allowGrow"/> = false 时**只许收、不许涨**（空闲缓释用）：
    /// 停住不动的时候 gross 通道还没漏空、目标暂时还在高处，不允许它把尺寸**越停越大**。
    /// </summary>
    private void ApplyEraserTarget(double nowMs, bool allowGrow = true)
    {
        float target;
        if (_eraseSpeedEma < EraserSpeedBack) target = EraserFactorMin;
        else if (_eraseSpeedEma > EraserSpeedKnee) target = EraserTargetFactorForSpeed(_eraseSpeedEma);
        else target = _eraseTarget;                          // 回差带：保持

        if (!allowGrow && target > _eraseDynFactor) target = _eraseDynFactor;   // 空闲：只许收
        _eraseTarget = target;
        float gap = target - _eraseDynFactor;
        if (MathF.Abs(gap) < 0.001f) { _eraseDynFactor = target; return; }

        float dt = (float)Math.Max(0.5, nowMs - _eraseLastMs);
        float tau = target > _eraseDynFactor ? EraserGrowTauMs : EraserShrinkTauMs;
        float step = gap * (1f - MathF.Exp(-dt / tau));
        if (MathF.Abs(step) < EraserMinStep)
            step = MathF.Sign(gap) * MathF.Min(EraserMinStep, MathF.Abs(gap));
        _eraseDynFactor += step;
    }

    /// <summary>
    /// 速度估计：两条通道各自"按时间/距离攒满一个窗口"再结算，取**较大值**当目标速度。
    /// · fine（40ms / 12px）：跟手，快扫立刻反映；
    /// · gross（350ms / 120px）：慢通道——慢下来时它还停在旧速度上，尺寸不会一慢就塌。
    /// （抄 MyPaint 的 Fine/Gross Speed：fine 跟手、gross "changes very slowly"。）
    /// </summary>
    private void FeedEraserSpeed(float dist, float dtMs)
    {
        float dt = MathF.Max(0.5f, dtMs);

        _eraseFineDist += dist; _eraseFineMs += dt;
        if (_eraseFineMs >= EraserFineWindowMs || _eraseFineDist >= EraserFineWindowPx)
        {
            _eraseFine = _eraseFineDist / MathF.Max(1f, _eraseFineMs);
            _eraseFineDist = 0f; _eraseFineMs = 0f;
        }

        _eraseGrossDist += dist; _eraseGrossMs += dt;
        if (_eraseGrossMs >= EraserGrossWindowMs || _eraseGrossDist >= EraserGrossWindowPx)
        {
            _eraseGross = _eraseGrossDist / MathF.Max(1f, _eraseGrossMs);
            _eraseGrossDist = 0f; _eraseGrossMs = 0f;
        }

        _eraseSpeedEma = MathF.Max(_eraseFine, _eraseGross);
    }

    /// <summary>
    /// 动态橡皮：喂一个采样点（画布坐标）更新"速度 → 尺寸系数"。
    /// 速度走 `FeedEraserSpeed`（按时间窗口）；目标与平滑见 `ApplyEraserTarget`。
    /// </summary>
    private void UpdateDynamicEraser(float x, float y)
    {
        if (!DynamicEraser) { _eraseDynFactor = 1f; return; }
        if (EraserFactorOverrideForTest > 0f) { _eraseDynFactor = EraserFactorOverrideForTest; return; }
        double now = NowMs;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dt = (float)Math.Max(0.5, now - _eraseLastMs);
        FeedEraserSpeed(MathF.Sqrt(dx * dx + dy * dy), dt);
        ApplyEraserTarget(now);
        _eraseLastMs = now;
        _eraseHoldBaseMs = now;          // 有新移动：缓释的保持期重新开始
        _eraseDecaying = false;
        _eraseTestIdle = 0f;

        // 诊断浮层：拖动中限频刷新（60ms），让人能看清"我这一下是什么速度"。
        if (EraserHud && now >= _eraserHudNextMs)
        {
            _eraserHudNextMs = now + 60;
            RebuildEraserHud();
            _dirty = true;
        }
    }

    /// <summary>
    /// 每帧推一次：指针**停住**之后把尺寸顺着缓释回去（不再等下一次移动来触发）。
    ///
    /// 为什么要这一条：尺寸只在**收到指针消息**时才更新——鼠标停住 = 没有消息 = 尺寸**冻在最大**；
    /// 再轻轻一动，速度窗口立刻算出很小的速度 → 目标回到下限 → 看起来"突然变小"（用户报的）。
    /// 现在停住超过 HoldMs 就每帧喂一个"零距离"样本：两条通道随时间自己漏空，目标随之回落到下限，
    /// 尺寸按"收"的时间常数顺着缩回去——**不需要另写一套衰减逻辑**（和 MyPaint 的"输入滤波"同源：
    /// 他们的速度输入在停止时自然归零，我们只是把"归零"在空闲时也喂进去）。
    ///
    /// "正在减速"（fine 明显小于 gross）= 快扫刚停下 → 用更短的保持时间**提前**开始收；
    /// 这就是"加速度"以稳定形式入场的位置（两个滤波量的差 = 加速度的符号，噪声被压了两遍）。
    /// </summary>
    internal void TickEraserIdleDecay()
    {
        if (!DynamicEraser || !PixelEraseDragging) return;
        if (TickEraserIdleDecayCore(NowMs)) _dirty = true;   // 还在缓释 → 保证还有下一帧
    }

    /// <summary>缓释一步。返回 true = 这一步动过（调用方据此保持刷新）。</summary>
    private bool TickEraserIdleDecayCore(double nowMs)
    {
        if (!_eraseDecaying)
        {
            // 保持期：从"上一次移动"开始算，够 HoldMs 才进入缓释（期间尺寸**故意冻住**）。
            float idle = (float)(nowMs - _eraseHoldBaseMs);
            if (idle < EraserIdleHoldMs) return false;
            _eraseDecaying = true;
            _eraseLastMs = nowMs - 1.0;                 // 缓释的第一步按 1ms 推进
        }
        float step = (float)Math.Max(1.0, nowMs - _eraseLastMs);
        FeedEraserSpeed(0f, step);                      // 零距离样本 → 通道随时间漏空
        ApplyEraserTarget(nowMs, allowGrow: false);     // 空闲：只许收、不许涨
        _eraseLastMs = nowMs;
        return true;
    }

    /// <summary>自检用：模拟"空闲 dtMs"（保持期累计、之后每步缓释），返回当前系数。</summary>
    internal float DynamicEraserIdleForTest(float dtMs)
    {
        if (!_eraseDecaying)
        {
            _eraseTestIdle += dtMs;
            if (_eraseTestIdle < EraserIdleHoldMs) return _eraseDynFactor;   // 保持期内不动
            _eraseDecaying = true;
        }
        FeedEraserSpeed(0f, dtMs);
        ApplyEraserTarget(_eraseLastMs + dtMs, allowGrow: false);
        _eraseLastMs += dtMs;
        return _eraseDynFactor;
    }

    /// <summary>刷新橡皮诊断浮层的文字（速度 / 目标 / 当前系数 / 尺寸 + 曲线参数）。</summary>
    private void RebuildEraserHud()
    {
        if (!EraserHud) return;
        float f = PixelEraseDragging ? _eraseDynFactor : 1f;
        EraserHudText = $"速度 {_eraseSpeedEma:F2} px/ms（fine {_eraseFine:F2} / gross {_eraseGross:F2}）　"
                      + $"目标 ×{_eraseTarget:F2}　当前 ×{f:F2}　"
                      + $"尺寸 {PixelEraserWidthLogical * f:F0}×{PixelEraserHeightLogical * f:F0} 逻辑像素"
                      + $"（基准 {PixelEraserWidthLogical:F0}×{PixelEraserHeightLogical:F0}）\n"
                      + $"曲线：≤{EraserSpeedKnee:F2} 恒 ×{EraserFactorMin:F1}（死区）→ ≥{EraserSpeedTop:F1} 封顶 ×{EraserFactorMax:F1}；"
                      + $"回差 {EraserSpeedBack:F2}/{EraserSpeedKnee:F2}；"
                      + $"通道 fine {EraserFineWindowMs:F0}ms / gross {EraserGrossWindowMs:F0}ms；"
                      + $"停 {EraserIdleHoldMs:F0}ms 后缓释（减速 {EraserDecelHoldMs:F0}ms）；"
                      + $"平滑 涨 {EraserGrowTauMs:F0} / 收 {EraserShrinkTauMs:F0} ms";
    }

    /// <summary>自检用：把尺寸系数钉死在一个值上（验"框和擦严丝合缝"时不受手速影响）。0 = 不覆盖。</summary>
    internal float EraserFactorOverrideForTest;

    /// <summary>自检用：把一个速度（物理像素/毫秒）喂进去，看算出什么**稳态**系数（后门关掉时恒 1）。</summary>
    internal float DynamicEraserFactorForTest(float speedPxPerMs)
    {
        if (!DynamicEraser) return 1f;
        _eraseSpeedEma = speedPxPerMs;
        _eraseTarget = EraserTargetFactorForSpeed(speedPxPerMs);
        _eraseDynFactor = _eraseTarget;
        return _eraseDynFactor;
    }

    /// <summary>自检用：喂一步"已知速度"（走真实的目标 + 平滑那一段），返回当前系数——慢速抖不抖靠它。</summary>
    internal float DynamicEraserAdvanceForTest(float speedPxPerMs, double dtMs)
    {
        _eraseSpeedEma = speedPxPerMs;
        ApplyEraserTarget(_eraseLastMs + dtMs);
        return _eraseDynFactor;
    }

    /// <summary>自检用：直接喂"距离 + 时间"给速度窗口，返回窗口速度（px/ms）——验窗口抗不抗抖。</summary>
    internal float DynamicEraserFeedForTest(float dist, double dtMs)
    {
        FeedEraserSpeed(dist, (float)dtMs);
        return _eraseSpeedEma;
    }

    /// <summary>自检用：把动态橡皮的状态归零（等价于"刚落笔"）。</summary>
    internal void ResetDynamicEraserForTest() => ResetDynamicEraser();

    /// <summary>自检用：直接开/关动态橡皮（产品里走 `--eraserfixed`）。</summary>
    internal bool DynamicEraserForTest { get => DynamicEraser; set => DynamicEraser = value; }

    /// <summary>
    /// Walks the eraser along the segment the pointer just travelled instead of
    /// only testing the newest position. Without this, a quick flick leaves gaps
    /// between samples and only some of the crossed strokes disappear - which is
    /// what "the eraser feels unresponsive" actually looks like.
    /// </summary>
    private void EraseAlongPath(float x, float y)
    {
        float r = EraserRadius;
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, r * 0.5f)), 1, 48);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseAt(ex, ey, r);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    /// <summary>
    /// 像素橡皮沿指针走过的路径扫一遍。理由和整笔橡皮一样：只测最新那一点，快划就会在
    /// 两次采样之间漏掉一整段。步长取**短边的一半**——相邻两个位置至少重叠一半，
    /// 中间不会有缝；上限 64 步，免得一次大跨度拖动把每帧的代价拉爆。
    /// </summary>
    private void EraseRectAlongPath(float x, float y)
    {
        float dx = x - _lastEraseX, dy = y - _lastEraseY;
        // 动态橡皮：这一段的"速度"算出的尺寸 = 框画的那个尺寸（同一份，看见的就是擦的）。
        // 落笔那一下还没速度 → 系数 1 → 和悬停时看到的框一样大。
        UpdateDynamicEraser(x, y);
        float hw = PixelEraserCursorHalfWidthPx, hh = PixelEraserCursorHalfHeightPx;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        int steps = Math.Clamp((int)(dist / MathF.Max(1f, MathF.Min(hw, hh))), 1, 64);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float ex = _lastEraseX + dx * t, ey = _lastEraseY + dy * t;
            bool log = EraserTelemetry != null;
            long t0 = log ? Stopwatch.GetTimestamp() : 0;
            int hit = Doc.EraseRectAt(ex, ey, hw, hh);
            if (log)
                EraserTelemetry.Step(hit, Doc.TotalIntervals, Doc.Strokes.Count,
                                     Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ex, ey, NowMs);
        }
        _lastEraseX = x;
        _lastEraseY = y;
    }

    // =====================================================================
    //  截图
    //
    //  流程（每一步都有它非这么做不可的理由）：
    //
    //   1. 拖一个框（和框选同一个手势，用户不用学新动作）；
    //   2. **把自己的覆盖层藏起来**再抓屏——不然抓到的就是"批注盖在 PPT 上"
    //      那一坨，等于把老师写的字又贴回去一张；
    //   3. 抓到的是一比一的**物理像素**，按 DPI 折成画布尺寸；
    //   4. 放到**视口左上角**并立刻选中——用户看得见、拖得动、能接着复制/翻转；
    //   5. 同时写进剪贴板：老师要把它贴到别的地方（PPT、微信、Word）是常见动作。
    //  =====================================================================

    /// <summary>
    /// 覆盖层藏起来时用的句柄列表。**多屏也要一起藏**，否则副屏上抓到的还是
    /// 我们自己画的东西（多屏教室很常见）。
    /// </summary>
    private IntPtr[] OverlayHandles()
    {
        var list = new List<IntPtr>(_windows.Count);
        foreach (var w in _windows) list.Add(w.Hwnd);
        // 抓屏时要连"接输入小窗"一起藏：它是 1/255 的一层灰，肉眼看不见，
        // 但拍进图里就是一层脏（截图界面永远不该出现在截图里）。
        // PPT 条那块小窗同理（它也是我们的窗口）。
        if (_uiInputHwnd != IntPtr.Zero) list.Add(_uiInputHwnd);
        if (_pptInputHwnd != IntPtr.Zero) list.Add(_pptInputHwnd);
        if (_timerInputHwnd != IntPtr.Zero) list.Add(_timerInputHwnd);
        if (_rollInputHwnd != IntPtr.Zero) list.Add(_rollInputHwnd);
        return list.ToArray();
    }

    /// <summary>截图落地的位置：**视口左上角**往里缩一点（见 CaptureMargin 的注释）。</summary>
    internal const float CaptureMarginLogical = 24f;

    /// <summary>
    /// 松手：**先不定局**（8.3.0）——框太小**不退出取景**（8.3.1：单击一下只当"还没想好"，
    /// 回到等拖框的状态），否则进入"调整"阶段等确认。
    /// 抓屏已经在进入取景时做完了（冻结帧），这里只判尺寸、切状态。
    /// </summary>
    private void EndCapture()
    {
        _dirty = true;

        var s = CaptureFrameScreen();
        int w = (int)MathF.Round(s.MaxX - s.MinX);
        int h = (int)MathF.Round(s.MaxY - s.MinY);
        float minPx = CaptureMinSizeLogical * DpiScale;
        if (w < minPx || h < minPx)
        {
            // 单击（没拖出框）：**继续取景**，不要整个退掉——
            // 老师的动作常常是"点一下看看、再拖"，一下点掉整个模式太凶。
            CapMinX = CapMaxX = PointerX;
            CapMinY = CapMaxY = PointerY;
            _capDrag = CaptureDrag.None;
            Console.WriteLine("截图：没拖出框，继续取景");
            return;
        }

        CaptureAdjusting = true;
        _capDrag = CaptureDrag.None;
        Console.WriteLine($"截图：框 {w}×{h} 物理像素（松开后可调整：拖边/拖角/拖整体，"
                          + "Enter/双击 或点「✓」完成，Esc 取消）");
    }

    /// <summary>
    /// **确认截图**：裁冻结帧 → 剪贴板 → **原位落图** → 选中、切回框选。
    /// 返回是否真的落了图。`Enter` / 点「✓ 完成」都走它。
    /// </summary>
    internal bool ConfirmCapture()
    {
        if (!CaptureActive) return false;
        var s = CaptureFrameScreen();
        int x = (int)MathF.Round(s.MinX), y = (int)MathF.Round(s.MinY);
        int w = (int)MathF.Round(s.MaxX - s.MinX), h = (int)MathF.Round(s.MaxY - s.MinY);
        if (w <= 0 || h <= 0) { CancelCapture(); return false; }

        byte[] pixels = CaptureFrozen
            ? CropCaptureFrozen(x, y, w, h)
            : GrabCaptureNow(x, y, w, h);          // 兜底：冻结没抓到 → 老路径现抓

        // 状态先收干净：遮罩/框/冻结帧立刻消失（屏幕上"啪"地回到常态）
        CaptureActive = false;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        ReleaseCaptureFrozen();
        _dirty = true;

        if (pixels == null)
        {
            Console.WriteLine("截图失败：抓屏返回空");
            return false;
        }

        // **不再默认写系统剪贴板**（2026-11-03 用户："截图可能也会默认进剪贴板，
        // 这样就不用进剪贴板了"）：截图只落图、选中，要外发走「导出」或选中后 Ctrl+C。
        var img = ImageData.Adopt(w, h, pixels);
        if (img == null) return false;

        // **原位落**（8.3.0，用户定的）：画布坐标 = 屏幕坐标 − 相机纵向偏移（x 不滚）。
        // 夹进视口、留一圈边距——**边距是必须的**：贴着 (0,0) 的话选中框的旋转手柄会跑到屏幕外。
        //
        // ⚠ `scale = 1`：**1 物理像素 = 1 画布单位**（画布坐标就是物理像素，见 ViewportCanvas
        //   与 `--imagetest` 的上屏探针）。旧版写的是 `1f / DpiScale`，那是把图**缩小 DpiScale 倍**——
        //   200% 缩放的屏上贴出来只有一半大（用户 2026-09-30："截图感觉也不对"，这条是硬伤）。
        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        float px = Math.Clamp(x, vp.MinX + m, MathF.Max(vp.MinX + m, vp.MaxX - m - w));
        float py = Math.Clamp(y - ViewOffsetY, vp.MinY + m, MathF.Max(vp.MinY + m, vp.MaxY - m - h));
        var placed = Doc.AddImage(img, px, py, 1f);
        if (placed != null)
        {
            Doc.SelectOnly(new[] { placed });
            Tool = Tool.Marquee;             // 刚截完图，最可能的下一件事是摆放它
            SetInkStatus(Loc.T("截图 {0}×{1} 已落图并选中", w, h));
        }
        Console.WriteLine($"截图 {w}×{h} 物理像素 → 原位落在画布 ({px:F0},{py:F0})，已选中");
        RegainKeyboardFocus();          // 回到批注态：前台/键盘要回来（见那段注释）
        return placed != null;
    }

    /// <summary>
    /// 兜底：冻结帧不在（按下时抓失败）时按老路径**现抓**——
    /// 隐藏界面模式藏整个覆盖层（60ms 的闪就闪在这里），直接截取只藏我们的框/光标。
    /// </summary>
    private byte[] GrabCaptureNow(int x, int y, int w, int h)
    {
        if (CaptureHideInk)
        {
            using (ScreenCapture.HiddenOverlay(OverlayHandles()))
                return ScreenCapture.Grab(x, y, w, h);
        }
        CaptureFrameHidden = true;
        _dirty = true;
        RenderAll();
        Native.DwmFlush();
        var p = ScreenCapture.Grab(x, y, w, h);
        CaptureFrameHidden = false;
        _dirty = true;
        return p;
    }

    /// <summary>
    /// 取消截图（Esc / 右键 / 「✕ 取消」/ 点框外）：什么都不落，把冻结帧和状态收干净，
    /// 并且**回到进截图之前用的工具**（8.3.1；不再是"留在截图工具上空等"）。
    /// </summary>
    internal void CancelCapture()
    {
        if (!CaptureActive) return;
        CaptureActive = false;
        CaptureAdjusting = false;
        _capDrag = CaptureDrag.None;
        _capturePressHandled = false;
        ReleaseCaptureFrozen();
        var back = _toolBeforeCapture;
        if (back == Tool.Capture) back = Tool.Pen;      // 兜底，正常走不到
        SwitchTool(back);
        _dirty = true;
        ApplyCursor();
        NotifyUiStateChanged();
        RegainKeyboardFocus();          // 回到批注态：前台/键盘要回来（见那段注释）
        Console.WriteLine("截图取消");
    }

    /// <summary>「✕ 取消」那颗按钮（**画布坐标**）：待机/拖框中一直挂在右上角。</summary>
    internal RectF CaptureCancelRect()
    {
        float s = DpiScale;
        float w = CaptureBtnWLogical * s, h = CaptureBtnHLogical * s;
        var vp = ViewportCanvas;
        float pad = 16f * s;
        return new RectF
        {
            MinX = vp.MaxX - pad - w, MinY = vp.MinY + pad,
            MaxX = vp.MaxX - pad, MaxY = vp.MinY + pad + h,
        };
    }

    internal bool CaptureCancelHit(float x, float y) => CaptureCancelRect().Contains(x, y);

    /// <summary>
    /// 调整阶段的两颗按钮：「✓ 完成」「✕ 取消」。
    /// AI 不放在这里（2026-11-11 用户定）：截完就是一张图片，选中后按 AI 即可，
    /// 截图条只留“完成/取消”，少两颗按钮、少一次误触。
    /// </summary>
    internal void CaptureButtons(out RectF ok, out RectF cancel)
    {
        float w = CaptureBtnWLogical * DpiScale, h = CaptureBtnHLogical * DpiScale;
        float gap = CaptureBtnGapLogical * DpiScale;
        var vp = ViewportCanvas;
        float total = w * 2 + gap;
        float x0 = CapMaxX - total;
        x0 = Math.Clamp(x0, vp.MinX + 4f, MathF.Max(vp.MinX + 4f, vp.MaxX - total - 4f));
        float y0 = CapMaxY + gap;
        if (y0 + h > vp.MaxY) y0 = CapMinY - gap - h;              // 下方放不下 → 放框上方
        y0 = Math.Clamp(y0, vp.MinY + 4f, MathF.Max(vp.MinY + 4f, vp.MaxY - h - 4f));
        cancel = new RectF { MinX = x0, MinY = y0, MaxX = x0 + w, MaxY = y0 + h };
        ok = new RectF { MinX = x0 + w + gap, MinY = y0, MaxX = x0 + w + gap + w, MaxY = y0 + h };
    }

    /// <summary>指针压在哪颗按钮上（✓ / ✕）——光标形状要用。</summary>
    internal bool CaptureButtonHit(float x, float y)
    {
        CaptureButtons(out var ok, out var cancel);
        return ok.Contains(x, y) || cancel.Contains(x, y);
    }

    /// <summary>调整阶段 8 个手柄里，指针压在哪个上（没有 = None）。拖动与光标共用同一套判据。</summary>
    internal CaptureDrag CaptureHandleAt(float x, float y)
    {
        CaptureHandles(out var tl, out var t, out var tr, out var r,
                       out var br, out var b, out var bl, out var l);
        float hit = CaptureHandleHitLogical * DpiScale;
        if (Near(x, y, tl, hit)) return CaptureDrag.TL;
        if (Near(x, y, tr, hit)) return CaptureDrag.TR;
        if (Near(x, y, bl, hit)) return CaptureDrag.BL;
        if (Near(x, y, br, hit)) return CaptureDrag.BR;
        if (Near(x, y, l, hit)) return CaptureDrag.L;
        if (Near(x, y, r, hit)) return CaptureDrag.R;
        if (Near(x, y, t, hit)) return CaptureDrag.T;
        if (Near(x, y, b, hit)) return CaptureDrag.B;
        return CaptureDrag.None;
    }

    /// <summary>
    /// 截图模式下的按下（8.3.1）：待机 / 拖框 / 调整三种状态先都过这里。
    /// 返回 true = 这一下被截图吃掉；返回 false = 照常走工具分流（也就是"从这里起框"）。
    /// </summary>
    private bool CapturePointerDown(float x, float y)
    {
        if (!CaptureAdjusting)
        {
            // 待机/拖框中：右上角那颗「✕ 取消」
            if (CaptureCancelHit(x, y)) { CancelCapture(); return true; }
            return false;                            // 其余交给 `case Tool.Capture` 起框
        }

        // ---- 调整阶段 ----
        CaptureButtons(out var ok, out var cancel);
        if (ok.Contains(x, y)) { ConfirmCapture(); return true; }
        if (cancel.Contains(x, y)) { CancelCapture(); return true; }

        _capDragX = x; _capDragY = y;
        _capD0MinX = CapMinX; _capD0MinY = CapMinY; _capD0MaxX = CapMaxX; _capD0MaxY = CapMaxY;
        _capDrag = CaptureHandleAt(x, y);
        if (_capDrag == CaptureDrag.None)
        {
            bool inside = x >= CapMinX && x <= CapMaxX && y >= CapMinY && y <= CapMaxY;
            if (!inside) { CancelCapture(); return true; }   // 点框外 = 取消（微信/QQ 的口径）
            // 双击框内 = 完成（微信的手感）：400ms、6 逻辑像素以内算同一处
            double now = NowMs;
            bool dbl = now - _capLastClickMs < 400
                       && MathF.Abs(x - _capLastClickX) < 6f * DpiScale
                       && MathF.Abs(y - _capLastClickY) < 6f * DpiScale;
            _capLastClickMs = now; _capLastClickX = x; _capLastClickY = y;
            if (dbl) { ConfirmCapture(); return true; }
            _capDrag = CaptureDrag.Move;
        }
        _dirty = true;
        return true;
    }

    /// <summary>调整阶段的 8 个手柄中心（左上/上中/右上/右中/右下/下中/左下/左中）。</summary>
    internal void CaptureHandles(out Vector2 tl, out Vector2 t, out Vector2 tr, out Vector2 r,
                                out Vector2 br, out Vector2 b, out Vector2 bl, out Vector2 l)
    {
        float cx = (CapMinX + CapMaxX) * 0.5f, cy = (CapMinY + CapMaxY) * 0.5f;
        tl = new Vector2(CapMinX, CapMinY); t = new Vector2(cx, CapMinY);
        tr = new Vector2(CapMaxX, CapMinY); r = new Vector2(CapMaxX, cy);
        br = new Vector2(CapMaxX, CapMaxY); b = new Vector2(cx, CapMaxY);
        bl = new Vector2(CapMinX, CapMaxY); l = new Vector2(CapMinX, cy);
    }

    private static bool Near(float x, float y, Vector2 p, float r)
        => MathF.Abs(x - p.X) <= r && MathF.Abs(y - p.Y) <= r;

    /// <summary>
    /// 调整阶段的**方向键微调**（8.3.1）：方向键 = 框整体挪 1 逻辑像素（投影上鼠标很难微调），
    /// `Shift` + 方向键 = 改大小（右下那两条边）。
    /// </summary>
    private void CaptureNudge(int vk)
    {
        if (!CaptureAdjusting) return;
        float s = 1f * DpiScale;
        float dx = vk == 0x25 ? -s : vk == 0x27 ? s : 0f;
        float dy = vk == 0x26 ? -s : vk == 0x28 ? s : 0f;
        bool shift = (Native.GetAsyncKeyState(0x10 /*VK_SHIFT*/) & 0x8000) != 0;
        var vs = VirtualScreen;
        float minYc = vs.MinY - ViewOffsetY, maxYc = vs.MaxY - ViewOffsetY;
        float minSz = CaptureMinSizeLogical * DpiScale;
        if (shift)
        {
            CapMaxX = Math.Clamp(CapMaxX + dx, CapMinX + minSz, vs.MaxX);
            CapMaxY = Math.Clamp(CapMaxY + dy, CapMinY + minSz, maxYc);
        }
        else
        {
            float w = CapMaxX - CapMinX, h = CapMaxY - CapMinY;
            float l = Math.Clamp(CapMinX + dx, vs.MinX, vs.MaxX - w);
            float t = Math.Clamp(CapMinY + dy, minYc, maxYc - h);
            CapMinX = l; CapMaxX = l + w;
            CapMinY = t; CapMaxY = t + h;
        }
        _dirty = true;
    }

    /// <summary>调整阶段拖动：整体移 / 拖某条边 / 拖某个角，夹在屏幕里、不小于最小尺寸。</summary>
    private void CaptureAdjustMove(float x, float y)
    {
        if (_capDrag == CaptureDrag.None) return;
        float dx = x - _capDragX, dy = y - _capDragY;
        float minSz = CaptureMinSizeLogical * DpiScale;
        var vs = VirtualScreen;
        float minYc = vs.MinY - ViewOffsetY, maxYc = vs.MaxY - ViewOffsetY;

        float l = _capD0MinX, r = _capD0MaxX, t = _capD0MinY, b = _capD0MaxY;
        switch (_capDrag)
        {
            case CaptureDrag.Move: l += dx; r += dx; t += dy; b += dy; break;
            case CaptureDrag.L: l = MathF.Min(l + dx, r - minSz); break;
            case CaptureDrag.R: r = MathF.Max(r + dx, l + minSz); break;
            case CaptureDrag.T: t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.B: b = MathF.Max(b + dy, t + minSz); break;
            case CaptureDrag.TL:
                l = MathF.Min(l + dx, r - minSz); t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.TR:
                r = MathF.Max(r + dx, l + minSz); t = MathF.Min(t + dy, b - minSz); break;
            case CaptureDrag.BL:
                l = MathF.Min(l + dx, r - minSz); b = MathF.Max(b + dy, t + minSz); break;
            case CaptureDrag.BR:
                r = MathF.Max(r + dx, l + minSz); b = MathF.Max(b + dy, t + minSz); break;
        }
        // 夹在屏幕里；夹完尺寸可能被压小 → 按"拖的是哪条边"把对边让回去，守住最小尺寸
        l = Math.Clamp(l, vs.MinX, vs.MaxX); r = Math.Clamp(r, vs.MinX, vs.MaxX);
        t = Math.Clamp(t, minYc, maxYc);   b = Math.Clamp(b, minYc, maxYc);
        if (r - l < minSz)
        {
            if (_capDrag is CaptureDrag.L or CaptureDrag.TL or CaptureDrag.BL) l = r - minSz;
            else r = l + minSz;
        }
        if (b - t < minSz)
        {
            if (_capDrag is CaptureDrag.T or CaptureDrag.TL or CaptureDrag.TR) t = b - minSz;
            else b = t + minSz;
        }
        CapMinX = l; CapMaxX = r; CapMinY = t; CapMaxY = b;
        _dirty = true;
    }

    /// <summary>调整拖动收尾（抬手）。</summary>
    private void EndCaptureAdjustDrag()
    {
        _capDrag = CaptureDrag.None;
        _dirty = true;
    }

    // =====================================================================
    //  触摸手势（8.4.0）——规格与调研见 调研-触摸手势-学校大屏.md
    //
    //  分工：Touch.cs 只回答"这一下是什么"（触点表 + 干净开始的角色判定 + 长按计时）；
    //  这里按 _touchMode 路由动作：写字 / 擦 / 漫游 / 翻页 / 框选·点选 / 选中变换。
    // =====================================================================

    private readonly TouchGestures _touch = new();
    private TouchMode _touchMode = TouchMode.None;

    /// <summary>触摸选出来的选中：**临时**的（框和操作条要显示，但工具不换）——和 `_dwellSelected` 同一路。</summary>
    private bool _touchSelected;

    private Vector2 _g2StartMid, _g2LastMid;   // 双指：起点中点 / 上一帧中点
    private int _g2Axis;              // 0 未定 / 1 横（翻页）/ 2 纵（漫游）
    private uint _g2IdA, _g2IdB;      // 钉住的两根手指（2026-10-09：手掌/杂触点不参与；0 = 未钉）
    private float _g2ScrollSm;        // 双指漫游的滚动量平滑（压"两指事件交错"的半格锯齿）
    private bool _g2Turned;           // 这一次手势已经翻过页（一次手势只翻一页）
    private bool _g2Transform;
    private bool _g2TextBoxScroll;
    private Stroke _g2TextBoxScrollStroke;
    private float _g2TextBoxScrollStart, _g2TextBoxScrollStartY;        // 有选中：这一次双指是在变换对象
    private bool _g2Tap;              // 两指点按候选（松手时结算）
    private float _g2Dist0;           // 变换用：起始两指距离（双指只做缩放，绕选区中心）

    // ---- 双指纵滑的惯性（2026-10-28）--------------------------------------
    // 真机反馈：上下滑松手就停，不如浏览器"甩一下继续走"。
    // 速度单位 = 逻辑像素 / 毫秒；滚动仍走 ScrollCanvasBy，和滚轮/上下键同一条路。
    private float _g2FlingVy;         // 松手时的纵向速度（带符号；负 = 手指向上、画布向下滚）
    private double _g2LastMoveMs;     // 上一次双指移动的时刻（算瞬时速度）
    private bool _g2FlingActive;      // 惯性还在跑
    private double _g2FlingLastMs;    // 惯性上一拍的时刻
    private bool _g2UiScroll;         // 这一次双指纵滑由界面消费（设置页）
    private float _g2UiVy;            // 界面消费时的松手速度（界面逻辑像素/ms）
    private const float TouchFlingMax = TouchGestures.FlingMaxVy;          // 最大速度（逻辑像素/ms，约 1800px/s）
    private const float TouchFlingStop = TouchGestures.FlingStopVy;        // 低于它 = 停（约 20px/s）
    private const float TouchFlingDecayPerSec = TouchGestures.FlingDecayPerSec; // 指数衰减；越小惯性越大
    private const float TouchFlingMaxStepMs = TouchGestures.FlingMaxStepMs;     // 卡顿后别一跳半屏

    /// <summary>一根手指按下那一刻的选区快照。这一下可能在图形工具下被"点框外 = 收起选区"
    /// 清掉（见 <see cref="ClearSelectionForNewContext"/>）；但它若紧接着升级成双指手势，
    /// 那个"点框外"其实没发生——手势起手时把选区救回来
    ///（2026-10-05 真机反馈："选中以后，双指放在别处转圈怎么没反应"）。</summary>
    private readonly List<Stroke> _touchSelSnapshot = new();

    /// <summary>双指上下滑的滚动系数：换算成逻辑像素后走**和鼠标滚轮/上下键同一个**
    /// `ScrollCanvasBy`。2026-10-05 真机反馈"1:1 滑得不够" → 暂定 2 倍；
    /// 觉得快了/还慢，只调这一个数。</summary>
    private const float TouchScrollGain = 2f;
    private Vector2 _roamLast;        // 单指漫游：上一帧位置
    private uint _roamId;             // 单指漫游：认哪一根手指（其它触点的移动不许开船）
    /// <summary>长按起头的那根手指（框选 / 拖动选中）：**只有它**能画框 / 拖东西。
    /// 别的触点（掌根、幽灵触点、第二根手指）的移动一律不许劫持——真机上"拖着拖着
    /// 东西突然飞到掌根底下"就是这里漏判（2026-10-05，和 `_roamId` 同一条纪律）。</summary>
    private uint _selTouchId;
    private bool _radialTouchMode;    // 轮盘这次是触屏呼出的（松手改成触点驱动；没划动=留在盘上）
    private bool _radialSticky;       // 触屏轮盘：没划动松手后留在盘上等点选（点空白/Esc 取消）

    /// <summary>读触摸模式（启动时一次）。2026-10-24 恢复为「手势 / 单指书写」两档，
    /// 默认手势；`--notouch` 仍可作本次运行的开发对照。</summary>
    internal void LoadTouchPrefs()
    {
        // 2026-10-24：只保留“手势 / 单指书写”两档；多指同时书写以后再做。
        // 默认手势（没有 touch.mode = 手势）；单指档完全关闭触摸手势。
        _touch.Enabled = GetUiPref("touch.mode") != "single";
        _touch.SingleFingerRoam = false;
        _touch.LongPressSelect = true;
        _touch.TwoFingerTapSelect = true;    // 当前实现就是“两指轻点开轮盘”；不另做两指点选
        _touch.TwoFingerHold = true;

        // 面积橡皮取消：不再看面积触发擦除；只保留三指及以上擦。
        // 面积/基线仍由 TouchGestures 采样，TouchStats 会写日志。
        _touch.ThreeFingerErase = true;
        _touch.PalmFactor = GetUiPref("touch.palmLevel") switch
        {
            "0" => 4f,      // 仅统计用
            "2" => 2f,      // 仅统计用
            _ => 3f,        // 仅统计用
        };
    }

    /// <summary>这块屏"报不报多个触点 / 报不报面积"——只给设置页和日志用，不影响判定。</summary>
    internal (int maxTouches, bool sawArea) TouchCapability => (_touch.MaxSeen, _touch.SawArea);

    /// <summary>
    /// 触摸触点落下。返回 true = 这一下**已经被触摸层接掉**（调用方直接 return）；
    /// 返回 false = 按普通写字那条路走（单指小面积）。
    /// </summary>
    private bool TouchDownDispatch(IntPtr hWnd, uint id, float x, float y, float sizePx,
                                   int contactW, int contactH, out bool stealPointer)
    {
        stealPointer = false;
        var v = _touch.Down(id, x, y, sizePx, NowMs, DpiScale);

        // 面积只采样、不参与判定；明细见 TouchStats。
        TouchStats.NoteDown(_touch.Count, contactW, contactH, sizePx,
                            _touch.FingerBaseline, _touch.PalmFactor,
                            _touch.WouldPalm, v.ToString(), _touch.Enabled, Tool);

        if (v == TouchVerdict.Write)
        {
            // 单指这一下会走"普通写字那条路"（可能被它清掉选区）——先记快照，
            // 手势升级时若发现选区没了就靠它救回来（见 BeginTouchTwoFinger）。
            _touchSelSnapshot.Clear();
            if (Doc.Selected.Count > 0) _touchSelSnapshot.AddRange(Doc.Selected);
        }
        else if (v != TouchVerdict.Ignore)
        {
            // 手势要接管：第一根手指可能已经起了别的头（框选工具下那个还没拖的框，
            // 以及图形工具下"按框外顺手收起选区"）——只能撤，不能 apply。
            CancelTouchPressMarquee();
        }
        switch (v)
        {
            case TouchVerdict.Ignore:
                return true;                       // 吃掉：不抢正在写的那一笔、也不落墨

            case TouchVerdict.Erase:
                stealPointer = true;
                // 可能是"两指变换进行中，第三根手指落下来变成擦"：先把变换收账
                //（一点没动就不进撤销栈），不然 SelDragging 会一直挂着（松手后全变成拖动）。
                if (SelDragging) EndSelDrag();
                _g2Transform = false;
                CancelTouchStroke();
                _touchMode = TouchMode.Erase;
                _drawing = true;
                Native.SetCapture(hWnd);
                GcLatency.Enter();
                BeginStrokeMeasure();
                // **跟鼠标同一条路**（2026-10-05 用户口径）：批次按当前那把橡皮开
                //（整笔擦 = BeginErase、面积擦 = BeginEraseRect），下面的 TouchEraseSample 同源。
                // 先 EndErase 兜底：如果第一根手指是**橡皮工具**按下来的（那一路自己开了批次），
                // 这里先把它收账，免得批次被顶掉（没有批在开时 EndErase 是空操作）。
                Doc.EndErase();
                if (_eraserKind == Tool.PixelEraser)
                {
                    Doc.BeginEraseRect();
                    // **动态橡皮**：和鼠标按下同一套（Reset → 移动中放大）。
                    // 真机反馈"三指橡皮没有动态大小"就是这里没接上（PixelEraseDragging 一直是 false）。
                    ResetDynamicEraser();
                    PixelEraseDragging = true;
                    if (TouchEraseCenter(out var c0)) { _lastEraseX = c0.X; _lastEraseY = c0.Y; }
                }
                else Doc.BeginErase();
                // 面积橡皮已取消；走到这里只剩三指及以上擦（主动动作），按下即擦。
                TouchEraseSample();
                Console.WriteLine("触摸：三指及以上 → 擦除");
                return true;

            case TouchVerdict.Gesture2:
                stealPointer = true;
                // 从"擦"切到手势：先把这一轮擦除收账（不然 BeginEraseRect 一直挂着）。
                if (_touchMode == TouchMode.Erase) { Doc.EndErase(); EndStrokeMeasure(); PixelEraseDragging = false; }
                CancelTouchStroke();
                _touchMode = TouchMode.Gesture2;
                _drawing = true;
                Native.SetCapture(hWnd);
                // **两指手势也要起 40ms 心跳**：`TouchGestures.Tick` 里的"两指长按=呼出盘"
                // 要靠它（写字那颗定时器只在单指写字的路上启动；漏了这里=长按永远不判）。
                StartDwellTimer();
                BeginTouchTwoFinger();
                return true;

            case TouchVerdict.Roam:
                stealPointer = true;
                CancelTouchStroke();
                _touchMode = TouchMode.Roam;
                _drawing = true;
                Native.SetCapture(hWnd);
                _roamId = id;
                _roamLast = new Vector2(x, y);
                Console.WriteLine("触摸：单指漫游");
                return true;

            default:
                return false;                      // Write：走原来的写字那条路
        }
    }

    /// <summary>撤掉第一根手指在框选工具下起的那一个"还没拖的框"（手势接管时用；不能 apply）。
    /// 不撤的话：旋转/擦除期间框还画在屏幕上，手势结束后它也不走（"松手了框还在"）。</summary>
    private void CancelTouchPressMarquee()
    {
        if (!MarqueeActive) return;
        MarqueeActive = false;
        LassoPath.Clear();
        _dirty = true;
    }

    /// <summary>触摸触点移动（按当前模式路由）。返回 true = 已经处理完（调用方直接 return）。</summary>
    private bool TouchMoveDispatch(uint id, float x, float y)
    {
        if (_touchMode == TouchMode.None) return false;
        _touch.Move(id, x, y, DpiScale);

        switch (_touchMode)
        {
            case TouchMode.Write:
                return false;                      // 写字：走原来的路（长按由定时器判）

            case TouchMode.Erase:
                TouchEraseSample();
                return true;

            case TouchMode.Gesture2:
                TouchGestureMove();
                return true;

            case TouchMode.Marquee:
                if (id != _selTouchId) return true;   // 只有"长按起头那根手指"能画框（掌根/别的触点不算）
                ExtendMarqueeTo(x, y);
                _dirty = true;
                return true;

            case TouchMode.SelDrag:
                if (id != _selTouchId) return true;   // 同理：别的触点不许把选中的东西拽走
                UpdateSelDrag(x, y);
                return true;

            case TouchMode.Radial:
            {
                // 轮盘开着：方向 = **当前触点的平均位**（一根手指先抬也不断；确认在松手那一刻）。
                var views = _touch.Views;
                if (views.Count == 0) return true;
                float mx = 0, my = 0;
                foreach (var v in views) { mx += v.Pos.X; my += v.Pos.Y; }
                PointerX = mx / views.Count;
                PointerY = my / views.Count;
                UpdateRadialSelection();
                _dirty = true;
                return true;
            }

            case TouchMode.Roam:
            {
                // 只认**开始漫游的那根手指**：屏幕上报来的其它触点（残点 / 掌根 / 误触）
                // 的移动不许开船——合成注入里实测到"残留触点在 (1440,1200) 反复发移动，
                // 把相机拽得乱跳"；真机上同理（多报的触点不该影响漫游）。
                if (id != _roamId) return true;
                var p = new Vector2(x, y);
                float dy = p.Y - _roamLast.Y;
                _roamLast = p;
                ScrollCanvasBy(dy / DpiScale);      // 和滚轮/上下键同一个函数（1:1 逻辑像素）
                return true;
            }
        }
        return false;
    }

    /// <summary>触摸触点抬起（按当前模式收尾）。返回 true = 调用方直接 return（写字那条除外）。</summary>
    private bool TouchUpDispatch(uint id)
    {
        // 表里的触点**永远要清**（哪怕这一刻没有手势在跑）：上面"先喂表"那条路
        // 可能把一根手指留在表里，漏清会让下一轮的"干净开始"计数错乱。
        if (_touchMode == TouchMode.None) { _touch.Up(id); return false; }
        bool normalStroke = _touchMode == TouchMode.Write;

        // 两指点按要在"减掉这一根之前"判（判据要求两个触点都还在）
        if (_touchMode == TouchMode.Gesture2 && _touch.TwoFingerTap(NowMs, DpiScale, out _))
            _g2Tap = true;

        switch (_touchMode)
        {
            case TouchMode.Erase:
                // **最后一根"真在表里"的手指抬起才收账**：三指擦中途松 1~2 根手指，会话要继续擦
                //（2026-10-05 真机反馈："三指会变大，但剩一根手指接着擦时就不变了"——
                //  以前每抬一根就 EndErase + 关动态：尺寸冻在基准、批次也碎成一步一次撤销）。
                // `Contains` 用来挡"幽灵 up"（合成注入会补发已抬起触点的 up；真机丢 up 后同理）：
                // 只有当前还在表里的那次抬起才算数，收账因此**恰好一次**。
                if (_touch.Contains(id) && _touch.Count <= 1)
                {
                    Doc.EndErase();             // 一次擦除 = 一步撤销
                    EndStrokeMeasure();
                    PixelEraseDragging = false; // 面积擦回基准（动态大小别残留）
                }
                break;

            case TouchMode.Gesture2:
                TouchGestureReleased();
                break;

            case TouchMode.Radial:
                // 最后一根手指抬起 = 确认；没划动就松手 → "留在盘上等点选"（见 CommitRadialPalette）。
                if (_touch.Count <= 1) CommitRadialPalette();
                break;

            case TouchMode.Marquee:
                // 只有"起头那根手指"抬手才结算；别的触点抬手不结算（框还在画）
                if (id == _selTouchId)
                {
                    ApplyMarquee();             // 松手出选区
                    _touchSelected = Doc.Selected.Count > 0;
                }
                break;

            case TouchMode.SelDrag:
                if (id == _selTouchId)          // 同上：别的触点抬手不许把拖动提前提交掉
                {
                    EndSelDrag();               // 提交（一步撤销；一点没动就不进撤销栈）
                    _touchSelected = Doc.Selected.Count > 0;
                }
                break;
        }

        _touch.Up(id);

        if (_touch.Any) return !normalStroke;   // 还有手指按着：手势继续

        _touchMode = TouchMode.None;
        _touch.Reset();
        _touchSelSnapshot.Clear();
        _g2Transform = false;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _g2IdA = _g2IdB = 0;               // 手势收场：钉子放掉（下一轮重新钉）
        _g2ScrollSm = 0f;
        StopDwellTimer();
        _dirty = true;
        return !normalStroke;                   // 写字那条：清完触点后照常走 EndStroke
    }

    /// <summary>中断触摸手势（丢捕获 / 换设备 / 意外路径）：把开了头的都收干净。</summary>
    private void TouchAbort()
    {
        if (_touchMode == TouchMode.None && !_touch.Any) return;
        StopTouchFling();
        if (_touchMode == TouchMode.Erase) { Doc.EndErase(); EndStrokeMeasure(); PixelEraseDragging = false; }
        // 触屏呼出的轮盘：手势中断 = 盘也收掉（别留一个没人管的盘在屏幕上）。
        if (RadialPaletteActive && _radialTouchMode) CancelRadialPalette("手势中断");
        // ⚠ **选择拖动 / 框选不在这里结束**（2026-09-30 实测）：触摸长按之后，
        // 系统偶尔会在"按住不动"的某一刻发一次丢捕获，把拖动提前 `EndSelDrag()` 掉——
        // 症状就是"长按选中了对象，但拖不动"（笔画计数却涨了：那一拖被当成接着写字）。
        // 留给松手那条路（`EndStroke` 里的 `if (SelDragging) EndSelDrag()`）去收：
        // 它才是真的抬手时刻，而且对鼠标那条路本来就是同一条。
        _touch.Reset();
        _touchSelSnapshot.Clear();
        _touchMode = TouchMode.None;
        _g2Transform = false;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _g2IdA = _g2IdB = 0;               // 手势中断：钉子一并放掉
        _g2ScrollSm = 0f;
        Console.WriteLine("触摸手势中断（丢捕获/换设备）");
        _dirty = true;
    }

    /// <summary>长按成立（40ms 心跳驱动）：撤掉那一小笔，进"点选 / 框选"。</summary>
    private void TouchLongPressFire()
    {
        if (_touchMode != TouchMode.Write) return;
        var c = _touch.Views.Count > 0 ? _touch.Views[0] : default;
        float x = c.Pos.X, y = c.Pos.Y;
        _selTouchId = c.Id;              // 这一根是"框选 / 拖动"的起头手指（见 _selTouchId）

        CancelTouchStroke();

        // 先问"手指下面有没有东西"（点选：选中并直接进入拖动）；
        // 没东西 → 框选（松手出选区）。
        if (TryBeginSelectionGesture(x, y, shift: false, alt: false))
        {
            _touchSelected = Doc.Selected.Count > 0;
            _touchMode = TouchMode.SelDrag;
        }
        else
        {
            BeginMarqueeAt(x, y, shift: false, alt: false);
            _touchMode = TouchMode.Marquee;
        }
        _touch.ClearLongPress();
        _dirty = true;
    }

    /// <summary>撤掉"刚起头的那一小笔"（不进文档、不留撤销）。</summary>
    private void CancelTouchStroke()
    {
        if (ActiveStroke != null)
        {
            Doc.Dirty.Add(ActiveStroke.PaddedBounds);
            ActiveStroke = null;
        }
        // [随老预测系统删除 2026-10-05] 这里原来是 `ClearRenderTail(); ActiveStrokeOnTrail = false;`
        _dwell.Reset();
        _dwellInk = null;
        _dismissTapArmed = false;
        StopDwellTimer();
        _dirty = true;
    }

    /// <summary>三指 / 手掌擦会话正在跑（Overlay 的落点反馈与脏区都读它）。</summary>
    internal bool TouchEraseSessionActive => _touchMode == TouchMode.Erase;

    /// <summary>这次擦除会话用的是不是面积擦（跟随用户当前那把橡皮，和鼠标同源）。</summary>
    internal bool TouchEraseSessionPixel => _eraserKind == Tool.PixelEraser;

    /// <summary>擦除会话里的活触点（画落点反馈用；每次现取，不缓存——避免"出现不消失"）。</summary>
    internal IReadOnlyList<TouchGestures.ContactView> TouchViewsLive => _touch.Views;

    /// <summary>擦除手势的落点：**所有触点的中心**（没有触点返回 false）。擦除、落点反馈、
    /// 脏区三处共用这一个点，保证"看见的 = 擦掉的"永远是同一个位置。</summary>
    internal bool TouchEraseCenter(out Vector2 c)
    {
        var list = _touch.Views;
        if (list.Count == 0) { c = default; return false; }
        float sx = 0f, sy = 0f;
        for (int i = 0; i < list.Count; i++) { sx += list[i].Pos.X; sy += list[i].Pos.Y; }
        c = new Vector2(sx / list.Count, sy / list.Count);
        return true;
    }

    /// <summary>擦一下：**整个手势只用一个"鼠标橡皮"**，落点 = 所有触点的中心
    ///（2026-10-05 真机反馈："三个指头出来三个橡皮擦"——一只手应该是一个橡皮，和鼠标同源）。
    /// 整笔擦 = `EraseAt(中心, EraserRadius)`（碰到就整条删）；
    /// 面积擦 = `EraseRectAt(中心, 鼠标落点框半宽/半高)`——尺寸和鼠标按下那一份完全一致，
    /// 而且**动态橡皮照旧**：喂速度 → 快扫变大（和鼠标 `EraseRectAlongPath` 同一套）。</summary>
    private void TouchEraseSample()
    {
        if (!TouchEraseCenter(out var c)) return;
        if (_eraserKind == Tool.PixelEraser)
        {
            UpdateDynamicEraser(c.X, c.Y);       // 动态橡皮：开关关掉时它自己把系数归 1
            _lastEraseX = c.X; _lastEraseY = c.Y;
            Doc.EraseRectAt(c.X, c.Y, PixelEraserCursorHalfWidthPx, PixelEraserCursorHalfHeightPx);
        }
        else
        {
            _lastEraseX = c.X; _lastEraseY = c.Y;
            Doc.EraseAt(c.X, c.Y, EraserRadius);
        }
        _dirty = true;
    }

    /// <summary>双指开始：有选中 → 借"整体拖动"那套（detach + 预览矩阵 + 松手一步撤销）；没选中 → 等方向。</summary>
    private void BeginTouchTwoFinger()
    {
        if (!_touch.TryPair(out var p)) return;

        // ⚠ 2026-10-09（真机"双指上下滑嘟嘟抖"的根修）：**把这两根手指的 id 钉住**，
        // 这一轮手势全程只认它们（TouchGestureMove 用 id 版 TryPair）。
        // 之前的读法"表里前两个"会被两类事搅乱——① 掌根/杂触点混进表；
        // ② 手指抬-落一下被重报、追加到表尾——"前两个"换人，中点来回跳。
        // 重入保护：钉子还活着就不重置（手指 blip 重落会再走到这里，不能把
        // _g2StartMid / 轴锁 重新洗一遍）。
        if (_g2IdA != 0 && _touch.Contains(_g2IdA) && _touch.Contains(_g2IdB)) return;
        StopTouchFling();
        _g2IdA = p.IdA;
        _g2IdB = p.IdB;
        _g2ScrollSm = 0f;
        _g2FlingVy = 0f;
        _g2UiScroll = false;
        _g2UiVy = 0f;
        _g2LastMoveMs = NowMs;

        _g2StartMid = _g2LastMid = (p.A + p.B) * 0.5f;
        _g2Axis = 0;
        _g2Turned = false;
        _g2Tap = false;
        _g2Transform = false;

        // 第一根手指按下时可能把选区清掉了（图形工具"点框外 = 收起"，见 ClearSelectionForNewContext）
        // ——这一下紧接着升级成双指手势，说明那个"点框外"根本没发生。把选区救回来，
        // 手势照常变换（2026-10-05 真机反馈："选中以后双指放别处转圈怎么没反应"）。
        if (Doc.Selected.Count == 0 && _touchSelSnapshot.Count > 0)
        {
            foreach (var st in _touchSelSnapshot)
                if (Doc.Strokes.Contains(st)) Doc.Selected.Add(st);
            if (Doc.Selected.Count > 0)
            {
                _touchSelected = true;
                _dirty = true;
            }
        }
        _touchSelSnapshot.Clear();

        if (Doc.Selected.Count == 0) return;

        // 有选中：在**选区中心**起一次"整体拖动"。借现成那套的好处：
        // 预览走 _selDragMatrix、松手 EndSelDrag 提交（一步撤销）、锁定对象自动摘掉。
        var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
        if (TryBeginSelectionGesture((aabb.MinX + aabb.MaxX) * 0.5f,
                                     (aabb.MinY + aabb.MaxY) * 0.5f, shift: false, alt: false))
        {
            _g2Transform = true;
            _g2Dist0 = MathF.Max(1f, Vector2.Distance(p.A, p.B));
            // 旋转读数归位：旋转已经只走"拖旋转手柄"那条路，双指不再产生度数。
            SelRotationSnapped = false;
            SelRotationReadsPose = false;
            SelRotationReadsInclination = false;
            _touchSelected = true;
            Console.WriteLine("触摸：双指 → 缩放选中对象（旋转用旋转手柄、移动用单指拖）");
        }
    }

    /// <summary>双指移动：有选中 → 变换对象；没选中 → 纵滑漫游 / 横滑翻页（方向锁 + 一次一页）。</summary>
    private void TouchGestureMove()
    {
        // **只认钉住的那两根**（2026-10-09）：手掌、杂触点在表里也当没看见。
        if (!_touch.TryPair(_g2IdA, _g2IdB, out var p)) return;
        var mid = (p.A + p.B) * 0.5f;

        if (_g2TextBoxScroll)
        {
            var ts = _g2TextBoxScrollStroke;
            var tc = ts?.TextBox;
            if (tc == null) return;
            float target = Math.Clamp(_g2TextBoxScrollStart - (mid.Y - _g2TextBoxScrollStartY),
                                      0f, tc.MaxScroll);
            if (MathF.Abs(target - tc.ScrollY) > 0.1f)
            {
                tc.ScrollY = target;
                Doc.Dirty.Add(ts.PaddedBounds);
                Doc.StructureChangedSinceRender = true;
                Doc.Version++;
                _dirty = true;
            }
            return;
        }

        if (_g2Transform)
        {
            // 绕**选区中心**缩放（不带平移、不带旋转）。
            // 2026-10-05 用户口径（第二轮定稿）：双指**只管缩放**；移动走单指拖、
            // 旋转走**拖旋转手柄**——三条路各管一件事，互相不打架（和鼠标那边一一对应）。
            var aabb = SelectionHandles.FrameOf(Doc.Selected).CanvasAabb;
            var center = new Vector2((aabb.MinX + aabb.MaxX) * 0.5f, (aabb.MinY + aabb.MaxY) * 0.5f);
            float d1 = Vector2.Distance(p.A, p.B);
            float scale = Math.Clamp(d1 / _g2Dist0, 0.1f, 10f);

            _selDragMatrix = Matrix3x2.CreateTranslation(-center.X, -center.Y)
                           * Matrix3x2.CreateScale(scale)
                           * Matrix3x2.CreateTranslation(center);
            _selDragMoved = true;
            _dirty = true;
            return;
        }
        float dx = mid.X - _g2LastMid.X, dy = mid.Y - _g2LastMid.Y;
        _g2LastMid = mid;

        if (_g2Axis == 0)
        {
            var total = mid - _g2StartMid;
            float lockPx = TouchGestures.DirLockLogical * DpiScale;
            if (total.Length() < lockPx) return;
            if (MathF.Abs(total.X) >= MathF.Abs(total.Y) * TouchGestures.DirRatio) _g2Axis = 1;
            else if (MathF.Abs(total.Y) >= MathF.Abs(total.X) * TouchGestures.DirRatio) _g2Axis = 2;
            else return;                       // 斜着：先不动，等主方向明确
        }

        if (_g2Axis == 2)
        {
            // 上下 = 漫游：走**鼠标滚轮/上下键同一个** ScrollCanvasBy（不再自己写相机数学）。
            // 系数见 TouchScrollGain（真机反馈 1:1 不够，暂定 2 倍）。
            // **2026-10-09 二轮双保险**（用户复测仍报"抖得厉害"）：
            //   ① 两指的事件交错到达时，中点会呈"半格锯齿"——轻 EMA 抹平；
            //   ② 单事件异常大跳（丢帧重放 / 掌缘蹭入）限幅，别让画面猛跳。
            _g2ScrollSm += (dy - _g2ScrollSm) * 0.55f;
            float lim = 90f * DpiScale;
            float use = Math.Clamp(_g2ScrollSm, -lim, lim);
            // 记瞬时速度，松手后 StepTouchFling 接着跑（2026-10-28）。
            // 单位统一成**逻辑像素 / ms**；dt 太小/太大都夹一下，防异常事件把速度打飞。
            double dtMs = Math.Clamp(NowMs - _g2LastMoveMs, 1.0, 40.0);
            _g2LastMoveMs = NowMs;

            // **设置页这类整页界面先接**（2026-11-07）：接住后不再滚画布，
            // 惯性也由界面在 PrepareFrame 里用同一套 UiTouchFling 参数走。
            if (!_g2UiScroll && UiVisibleNow && Ui != null)
            {
                var begin = new UiTouchScrollEvent(UiTouchScrollPhase.Begin,
                    mid.X / UiUnit, mid.Y / UiUnit, 0f, 0f, NowMs);
                if (Ui.TouchScroll(begin)) _g2UiScroll = true;
            }
            if (_g2UiScroll)
            {
                float uiDy = use * TouchScrollGain / UiUnit;
                float uiVy = uiDy / (float)dtMs;
                _g2UiVy = Math.Clamp(_g2UiVy * 0.70f + uiVy * 0.30f,
                                     -TouchFlingMax, TouchFlingMax);
                Ui?.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Move,
                    mid.X / UiUnit, mid.Y / UiUnit, uiDy, _g2UiVy, NowMs));
                return;
            }

            float instVy = (use * TouchScrollGain / DpiScale) / (float)dtMs;
            _g2FlingVy = Math.Clamp(_g2FlingVy * 0.70f + instVy * 0.30f,
                                    -TouchFlingMax, TouchFlingMax);
            ScrollCanvasBy(use * TouchScrollGain / DpiScale);
            return;
        }

        _g2FlingVy = 0f;                       // 横滑翻页不带纵向惯性
        if (_g2Turned || dx == 0f) return;
        float totalX = mid.X - _g2StartMid.X;
        if (MathF.Abs(totalX) < TouchGestures.PageTurnLogical * DpiScale) return;
        _g2Turned = true;                      // 一次手势只翻一页（抬手再滑才是下一页）
        bool next = totalX < 0;                // 往左滑 = 下一页
        if (BoardOn) FlipPage(next);           // 白板自己的页，优先于 PPT 翻页
        else if (PptMode)
        {
            if (next) PptNextFromUi(); else PptPrevFromUi();
        }
        else FlipPage(next);
        Console.WriteLine($"触摸：双指横滑 → {(next ? "下一页" : "上一页")}");
    }

    /// <summary>双指手势收尾（最后一根指头抬起时）。</summary>
    private void TouchGestureReleased()
    {
        _g2TextBoxScroll = false;
        _g2TextBoxScrollStroke = null;
        // **两指轻点优先于"变换提交"**：点按没有位移，不该走变换那条路。
        // （2026-10-05 实测：画布上有选中对象时，轻点会被 `_g2Transform` 分支吃掉——
        //   老顺序把 transform 放在前面，轻点永远轮不到。）
        if (_g2Tap && !_g2Turned && _g2Axis == 0 && _touch.Views.Count >= 2)
        {
            if (SelDragging) EndSelDrag();     // 万一 transform 那条已经起了拖动（零位移=不进撤销栈）
            _g2Transform = false;
            var mid = (_touch.Views[0].Pos + _touch.Views[1].Pos) * 0.5f;
            OpenRadialPaletteFromTouch(mid.X, mid.Y, sticky: true);
            _g2Tap = false;
            return;
        }
        if (_g2Transform)
        {
            if (SelDragging) EndSelDrag();     // 提交变换（一步撤销；没动就不进撤销栈）
            _g2Transform = false;
            return;
        }
        // 界面消费的双指纵滑：把松手速度交给界面，界面用同一套 UiTouchFling 继续跑。
        // 不能在这里再开画布惯性——否则设置页松手后画布会在底下偷偷滚。
        if (_g2UiScroll)
        {
            Ui?.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.End,
                _g2LastMid.X / UiUnit, _g2LastMid.Y / UiUnit, 0f, _g2UiVy, NowMs));
            _g2UiScroll = false;
            _g2UiVy = 0f;
            _g2Tap = false;
            return;
        }
        // 最后一根手指抬起：纵向滑动如果还有速度，交给惯性继续跑。
        // 只认最后一根（第一根先抬时 _touch.Count 还是 2，不能提前开船）。
        if (_g2Axis == 2 && _touch.Count <= 1
            && NowMs - _g2LastMoveMs <= 150
            && MathF.Abs(_g2FlingVy) >= TouchFlingStop)
        {
            _g2FlingActive = true;
            _g2FlingLastMs = NowMs;
            _dirty = true;
        }
        _g2Tap = false;
    }

    /// <summary>
    /// 双指纵滑松手后的惯性：每帧按指数衰减，滚动仍走 <see cref="ScrollCanvasBy"/>。
    /// 到边界（顶上/底下）或速度低于 <see cref="TouchFlingStop"/> 就停。
    /// </summary>
    private void StepTouchFling()
    {
        if (!_g2FlingActive) return;

        double dtMs = Math.Clamp(NowMs - _g2FlingLastMs, 0.0, TouchFlingMaxStepMs);
        _g2FlingLastMs = NowMs;
        if (dtMs <= 0.0) return;

        float decay = MathF.Exp(-TouchFlingDecayPerSec * (float)(dtMs / 1000.0));
        _g2FlingVy *= decay;
        if (MathF.Abs(_g2FlingVy) < TouchFlingStop)
        {
            StopTouchFling();
            return;
        }

        float before = ViewOffsetY;
        ScrollCanvasBy(_g2FlingVy * (float)dtMs);
        if (MathF.Abs(ViewOffsetY - before) < 0.01f)
            StopTouchFling();                  // 已经到边：别让速度空转
    }

    private void StopTouchFling()
    {
        // 界面消费的惯性也要刹住：松手后 `_g2UiScroll` 已经清零，不能只看它。
        Ui?.TouchScroll(new UiTouchScrollEvent(UiTouchScrollPhase.Cancel, 0f, 0f, 0f, 0f, NowMs));
        _g2UiScroll = false;
        _g2UiVy = 0f;
        _g2FlingActive = false;
        _g2FlingVy = 0f;
    }

    /// <summary>
    /// 40ms 心跳（笔画进行中那颗定时器每次都调）：长按成立就把这一下升级成"选择"。
    /// 判据全在 <see cref="TouchGestures.Tick"/>（路径 ≤8 逻辑像素 = 和"点一下不留墨"同一个数）。
    /// </summary>
    private void TouchTick()
    {
        _touch.Tick(NowMs, DpiScale);
        if (_touch.LongPressFired && _touchMode == TouchMode.Write) TouchLongPressFire();

        // 两指长按成立 → 呼出盘（触屏入口）。进 Radial 模式后：方向由触点平均位喂（TouchMoveDispatch），
        // 松手在 TouchUpDispatch 里确认；没划动就松手 = 留在盘上等点选（见 CommitRadialPalette）。
        if (_touch.TwoFingerHoldFired && _touchMode == TouchMode.Gesture2)
        {
            _touch.ClearLongPress();
            // 呼出盘的位置用**钉住的那两根**（2026-10-09）；钉子不在时退回"表里前两个"。
            TouchGestures.PairView pair = default;
            bool pairOk = _g2IdA != 0 ? _touch.TryPair(_g2IdA, _g2IdB, out pair)
                                      : _touch.TryPair(out pair);
            if (pairOk)
            {
                // 有选中时这两指可能已经把变换起头了（SelDragging）——呼出盘之前先收账
                //（一点没动就不进撤销栈），不然盘一开这个拖动会一直挂着。
                if (SelDragging) EndSelDrag();
                _g2Transform = false;
                _touchMode = TouchMode.Radial;
                OpenRadialPaletteFromTouch((pair.A.X + pair.B.X) * 0.5f,
                                           (pair.A.Y + pair.B.Y) * 0.5f, sticky: false);
            }
        }
    }

    /// <summary>自检用：触摸层这一刻的模式 / 触点表。</summary>
    internal TouchMode TouchModeForTest => _touchMode;
    internal int TouchCountForTest => _touch.Count;
    internal bool TouchSelectedForTest => _touchSelected;
    internal int _touchDebugAxis => _g2Axis;
    internal bool TouchFlingActiveForTest => _g2FlingActive;
    internal float TouchFlingVelocityForTest => _g2FlingVy;
    internal void StopTouchFlingForTest() => StopTouchFling();
    /// <summary>自检用：直接喂一个触点取判定（合成触摸的 `rcContact` 系统不认，面积那条只能这么测）。</summary>
    internal TouchVerdict TouchClassifyForTest(uint id, float x, float y, float sizePx)
        => _touch.Down(id, x, y, sizePx, NowMs, DpiScale);
    internal void TouchResetForTest() { _touch.Reset(); _touchMode = TouchMode.None; _touchSelected = false; }

    /// <summary>把相机滚到"这个对象看得见"。只做纵向——横向没有滚动这回事。</summary>
    private void EnsureVisible(Stroke s)
    {
        if (s == null) return;
        var vp = ViewportCanvas;
        var b = s.PaddedBounds;
        if (b.MaxY > vp.MaxY) ViewOffsetY += b.MaxY - vp.MaxY;
        else if (b.MinY < vp.MinY) ViewOffsetY += b.MinY - vp.MinY;
        ClampViewOffset();
    }

    /// <summary>上一次「复制」的整块范围：判断原件还在不在当前这一屏。</summary>
    private RectF? _lastCopyBox;

    /// <summary>上一次「复制」写进剪贴板的原始字节：粘贴时比对，确认中间没被别的程序换过。</summary>
    private byte[] _lastCopiedBytes;
    private int _pasteStep;

    // ---- 反馈群二维码（2026-11-03）-----------------------------------------
    // 图在 assets/qq-group.jpg，跟着输出走；点「二维码」时整屏展示，点一下 / Esc 关闭。
    // 只读一次，失败也不重试（缺图是发版问题，不是运行时问题）。
    internal ImageData FeedbackQrImage;
    internal bool FeedbackQrVisible;
    private bool _feedbackQrTried;

    /// <summary>显示 / 隐藏反馈群二维码。</summary>
    internal void ToggleFeedbackQr()
    {
        if (FeedbackQrVisible) { HideFeedbackQr(); return; }

        if (!_feedbackQrTried)
        {
            _feedbackQrTried = true;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "assets", "qq-group-qr.png");
                var r = ImageFile.Load(path);
                if (r.Success)
                    FeedbackQrImage = ImageData.AdoptPremultiplied(r.Width, r.Height, r.Bgra);
                else
                    Console.WriteLine("二维码：读不出来 —— " + r.Error + $"（{path}）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("二维码：读取失败 " + ex.Message);
            }
        }

        if (FeedbackQrImage == null)
        {
            SetInkStatus("二维码图缺失：assets/qq-group.jpg");
            return;
        }

        FeedbackQrVisible = true;
        _dirty = true;
        RequestFullFrames();
        SetInkStatus("扫一扫加群；点一下关闭");
        Console.WriteLine("二维码：整屏展示");
    }

    internal void HideFeedbackQr()
    {
        if (!FeedbackQrVisible) return;
        FeedbackQrVisible = false;
        _dirty = true;
        RequestFullFrames();
        Console.WriteLine("二维码：关闭");
    }

    /// <summary>整屏重画两帧：二维码是覆盖层，出现/消失时脏区不能只算局部。</summary>
    private void RequestFullFrames()
    {
        foreach (var w in _windows) w.RequestFullFrames();
    }

    /// <summary>
    /// 复制选中对象到剪贴板：**对象字节 + 一张图**。
    ///
    /// 对象字节让"粘回来仍是可编辑对象"（跨页、跨窗口、跨程序实例都行）；
    /// 图是给外部程序的兜底（Word / PPT / 微信粘到的是图，至少能用）。
    /// 只放对象不放图也行，但那样"复制一段板书贴到微信"就没反应——两头都要顾。
    /// </summary>
    internal bool CopySelectionToClipboard()
    {
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("复制：没有选中任何东西（先用框选或点选选中）");
            return false;
        }

        byte[] objects = ClipboardInk.Serialize(sel);
        _lastCopiedBytes = objects;

        // 同时渲染一张图：范围 = 这批对象的画布范围 + 4 逻辑像素留白
        byte[] dib = null; int w = 0, h = 0;
        var box = EditRegion.Of(sel);
        if (!box.IsEmpty)
        {
            // 记住"原件在哪 + 多大"：粘贴时判断它还在不在当前屏，并落在旁边 24px
            _lastCopyBox = box;
            _pasteStep = 0;
        }
        if (!box.IsEmpty && _windows.Count > 0)
        {
            box = box.Inflate(4f * DpiScale);
            w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
            h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
            // 防呆：图太大就不放了（对象格式照放）
            if ((long)w * h <= 32_000_000) dib = _windows[0].RenderStrokesToBgra(sel, box, w, h);
            else { w = h = 0; }
        }

        bool ok = ClipboardInk.Set(objects, dib, w, h);
        Console.WriteLine(ok
            ? $"复制 {sel.Count} 个对象（同时放了一张 {w}×{h} 的图：外部程序也粘得上）"
            : "复制失败（剪贴板正被别的程序占着？）");

        // 屏幕上的反馈只有**选区闪一下**（用户反馈的"复制以后看不出来"）。
        // 提示条 2026-09-16 按用户要求去掉了：屏幕上一个字都不留。
        if (ok) SelFlashUntilMs = NowMs + SelFlashMs;
        return ok;
    }

    /// <summary>
    /// 操作条「复制一份」：**原件原位保留，副本错位 24px 并选中**。
    ///
    /// 2026-11-03（用户）：**不再顺手写系统剪贴板**——"复制到剪贴板"是「选择」上带
    /// 右端那颗「复制」的事；条上这颗只管再制，避免只想复制图形却把剪贴板冲掉。
    /// </summary>
    internal bool CopySelectionAndDuplicate()
    {
        int n = Doc.Selected.Count;
        if (n == 0)
        {
            SetInkStatus("复制：先选中要复制的对象");
            return false;
        }

        bool duplicated = Doc.DuplicateSelected();
        SelFlashUntilMs = NowMs + SelFlashMs;
        if (duplicated)
        {
            SetInkStatus(Loc.T("已复制 {0} 个对象：原件保留，新副本已选中", n));
            return true;
        }

        string why = string.IsNullOrEmpty(Doc.LastRejectReason) ? "副本创建失败" : Doc.LastRejectReason;
        SetInkStatus(Loc.T("复制失败：{0}", why));
        return false;
    }

    // ---- 导出（选中 → 透明底 PNG）------------------------------------------
    //
    // 用户 2026-09-17 定：**只导选中的**、**透明底 PNG**、**弹系统"另存为"**，
    // 而且**同时放进剪贴板**（两样都要）。落点与命名按
    // [调研-选中框-反馈-导出-层级-属性.md] 第二节。
    //
    // 屏幕上**不留字**（用户 2026-09-16 明确要求把提示条去掉），只让选区闪一下——
    // 文件对话框本身就是最清楚的反馈。

    /// <summary>
    /// 把选中的内容离屏渲染成一张**透明底** BGRA（和"复制到剪贴板"那条路同一个渲染器）。
    /// 返回 null = 不能导（没选中 / 太大 / 渲染失败），每种情况都有日志。
    /// </summary>
    private byte[] RenderSelectionForExport(out int w, out int h)
    {
        w = h = 0;
        var sel = Doc.Selected;
        if (sel.Count == 0)
        {
            Console.WriteLine("导出：没有选中任何东西（先用框选或点选选中）");
            return null;
        }
        var box = EditRegion.Of(sel);
        if (box.IsEmpty) return null;
        box = box.Inflate(4f * DpiScale);                     // 和"复制"同一个留白
        w = Math.Max(1, (int)MathF.Ceiling(box.MaxX - box.MinX));
        h = Math.Max(1, (int)MathF.Ceiling(box.MaxY - box.MinY));
        if ((long)w * h > 32_000_000)
        {
            Console.WriteLine($"导出：选中的内容太大（{w}×{h}）");
            w = h = 0;
            return null;
        }
        if (_windows.Count == 0) { w = h = 0; return null; }

        var bgra = _windows[0].RenderStrokesToBgra(sel, box, w, h);
        if (bgra == null) { Console.WriteLine("导出：离屏渲染失败"); w = h = 0; }
        return bgra;
    }

    /// <summary>
    /// 写 PNG ＋ **同时放进剪贴板**（对象 ＋ 这张图，和 Ctrl+C 同一个剪贴板合同），
    /// 再让选区闪一下。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path)
        => WriteExport(bgra, w, h, path, 1);

    /// <summary>
    /// 写盘。<paramref name="filterIndex"/> = 老师在类型栏选的那一条（决定"透明底 / 白底"），
    /// 编码器按扩展名决定——两者的分工见 `ExportFormats.Encode` 的注释。
    /// </summary>
    internal bool WriteExport(byte[] bgra, int w, int h, string path, int filterIndex)
    {
        LastExportPath = null;
        LastExportFilterIndex = 0;
        try
        {
            var bytes = ExportFormats.Encode(path, bgra, w, h, filterIndex, out string tag);
            if (bytes == null) { Console.WriteLine($"导出：{tag} 编码失败"); return false; }
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"导出成功：{path}（{w}×{h}，{tag}，{bytes.Length / 1024.0:F0} KB）");
            LastExportPath = Path.GetFullPath(path);
            LastExportFilterIndex = filterIndex;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"导出失败：{ex.Message}");
            return false;
        }

        // 用户 2026-09-17 更正：**导出不再顺手放进剪贴板**——"复制"那条路已经有了
        // （选中 → Ctrl+C / 操作条上的复制），导出就只管落盘，职责单一、也少一次
        // 剪贴板写入（剪贴板是全局资源，能少碰就少碰）。
        SelFlashUntilMs = NowMs + SelFlashMs;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// **导出按钮做的事**：离屏渲染 → 弹"另存为" → 写盘。取消 = 什么都不做。
    ///
    /// 格式**就在那个对话框里选**（见 <see cref="ExportFormats"/>：透明底 / 白底 /
    /// 文件大小 / 老软件兼容四种，每种把优势写在类型名里）。我们这边**不再先弹一层
    /// 自己的格式面板**——用户 2026-09-17："那下面这两个图标就没有用了吧，用户都可以
    /// 自己保存图片了"：说得对，两处选格式反而绕，点导出直接出对话框还少一步。
    /// （触摸屏上那个下拉现在也点得开了：真因是覆盖层每秒抢层，见 ReassertTopmost。）
    /// </summary>
    /// <param name="defaultFilterIndex">
    /// 默认选中第几条（1 起）。界面把"上次用的那条"记在偏好里传进来，
    /// 所以常用 JPG 的老师下次不用再选。
    /// </param>
    /// <returns>用户最终选的那一条（1 起）；取消或没导成返回 0。</returns>
    internal int ExportSelection(int defaultFilterIndex = 1)
    {
        ExportAttempts++;
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return 0;

        // 自检模式**绝不弹对话框**：系统对话框会阻塞在那里等消息，
        // 而自检是靠"抽消息 + 渲染"自己推进的——一弹就把整条自检卡死到超时。
        // （自检要验的是"渲染 → 编码 → 写盘 → 剪贴板"这条链，那条由 --iotest 走
        //  ExportSelectionToPathForTest，不经过这里。）
        if (!ExportDialogEnabled)
        {
            Console.WriteLine("导出：自检模式不弹另存为对话框（见 --iotest）");
            return 0;
        }

        // 默认扩展名跟着选的那一条走（第 1 条 PNG 透明底是老师最常要的：
        // "贴到别处不带走白底"）。
        int want = Math.Clamp(defaultFilterIndex, 1, ExportFormats.Count);
        string suggested = $"选中-{DateTime.Now:yyyyMMdd-HHmm}" + ExportFormats.ExtensionFor(want);
        string path = null;
        int chosen = want;
        BorrowFocusForDialog();                 // 覆盖层平时不抢焦点，弹框前临时放开
        ExportDialogOpen = true;                // 弹框期间不许再抬覆盖层，见 ReassertTopmost
        try { path = ExportFileDialog.AskForImage(OwnerHwnd(), suggested, want, "导出选中的内容", out chosen); }
        catch (Exception ex)
        {
            // **弹框这一步出错绝不允许打死软件**。真踩过：.NET 7 起结构体字段不能用
            // StringBuilder，那一版一点"导出"整个进程就没了（异常从 P/Invoke 冒到 Main）。
            // 现在最坏也只是"这次导不出去"，并且把原因说清楚。
            Console.WriteLine($"导出：弹另存为失败（{ex.GetType().Name}: {ex.Message}）");
            return 0;
        }
        finally { ExportDialogOpen = false; ReturnFocusAfterDialog(); }

        if (path == null) { Console.WriteLine("导出：取消"); return 0; }
        return WriteExport(bgra, w, h, path, chosen) ? chosen : 0;
    }

    /// <summary>
    /// 导出按钮真正调的那一层：**记住老师上次选的那一条格式**（`ui.exportFormat`），
    /// 下次直接默认它——常用 JPG 的老师不用每次都去下拉里找。
    ///
    /// 这一条偏好由**引擎自己解释**（别的 `ui.*` 都是界面说了算）。理由：操作条
    /// 是引擎那边画和命中测试的（`Selection.cs`），导出按钮压根不经过 `InkUi`，
    /// 所以"记住上次的选择"只能落在这里。键名照旧放在 `ui` 段，不另开一节。
    ///
    /// 注意：自检模式下这条偏好**读不到也写不进**（自检不碰用户配置），
    /// 所以自检里走的是默认第 1 条，判据是确定的。
    /// </summary>
    private void ExportSelectionPref()
    {
        int last = ExportDefaultFilterIndex();
        int chosen = ExportSelection(last);
        if (chosen >= 1) SetUiPref("exportFormat", chosen.ToString());
    }

    /// <summary>
    /// 默认选中第几条格式：读偏好 `ui.exportFormat`，读不出来/越界就回到第 1 条（PNG 透明底）。
    /// 单独抽出来是为了能自检（"记住上次的选择"这条逻辑不依赖对话框）。
    /// </summary>
    internal int ExportDefaultFilterIndex()
        => int.TryParse(GetUiPref("exportFormat"), out int v)
           && v >= 1 && v <= ExportFormats.Count ? v : 1;

    /// <summary>自检用：不弹对话框，直接写到指定路径（其余流程一模一样）。</summary>
    internal bool ExportSelectionToPathForTest(string path, int filterIndex = 1)
    {
        var bgra = RenderSelectionForExport(out int w, out int h);
        if (bgra == null) return false;
        return WriteExport(bgra, w, h, path, filterIndex);
    }

    private IntPtr OwnerHwnd() => _windows.Count > 0 ? _windows[0].Hwnd : IntPtr.Zero;

    /// <summary>
    /// 导出时要不要弹系统的"另存为"。**自检模式必须关掉**（见 <see cref="ExportSelection"/>）：
    /// 系统对话框会阻塞等消息，而自检是自己抽消息推进的，一弹就卡到超时。
    /// 产品里保持 true。
    /// </summary>
    internal bool ExportDialogEnabled = true;

    /// <summary>
    /// "系统对话框正开着"。**只用来按住 <see cref="ReassertTopmost"/>**：
    /// 那一下每秒一次的置顶重抬会去跟对话框抢层，顺手把它的下拉列表关掉
    /// （用户报的"选不到 jpg"就是它）。见 <see cref="ReassertTopmost"/> 的注释。
    /// </summary>
    internal bool ExportDialogOpen;

    /// <summary>
    /// 上一次**真的写出去**的那个文件（全路径；没成功过就是 null），以及它是不是 JPEG。
    ///
    /// `--dialogprobe --save` 靠它确认"对话框 → 路径 → 编码 → 落盘"整条链真的通了，
    /// 而不是只看对话框弹没弹出来。
    /// </summary>
    internal string LastExportPath;
    /// <summary>上一次成功导出用的那一条格式（1 起，见 <see cref="ExportFormats"/>）。</summary>
    internal int LastExportFilterIndex;

    /// <summary>
    /// 试过几次导出（**包括没有选中、被自检挡下、用户取消**）。
    ///
    /// 自检要的"点一下"和"真的弹框"分开：`--edittest` 在自检模式下点导出，
    /// 对话框是关着的，只能靠这个计数器证明那一下真的走到了导出这条路
    /// （而不是"什么都没发生"）。
    /// </summary>
    internal int ExportAttempts;

    // 弹系统对话框期间借一下焦点：把 WS_EX_NOACTIVATE 摘掉、弹完装回去。
    // 和"批注键盘模式"同一个手法（那边是长期摘掉，这里是临时的）。
    private bool _focusBorrowed;

    private void BorrowFocusForDialog()
    {
        if (_focusBorrowed) return;
        _focusBorrowed = true;
        // **同时把"置顶"摘掉**：我们的覆盖层是 WS_EX_TOPMOST + 铺满整屏，
        // 而系统对话框自己的弹层（**文件类型那个下拉列表**）是普通弹窗——
        // 它会被置顶的覆盖层盖住，用户看到的就是"点开一下就收回去、选不到 JPG"
        // （用户 2026-09-17 报的）。弹框期间我们不需要浮在最上面，摘掉就好。
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
            // ⚠ **不要 SWP_FRAMECHANGED**：覆盖层是 DComp 合成的全屏层，强制重算框架
            // 会在换样式那一帧闪一下白（2026-10-05 用户报"保存图片先闪白屏"的来源之一）。
            // WS_EX_NOACTIVATE 属激活类样式，去掉它不需要 FRAMECHANGED。
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
            Native.SetWindowPos(w.Hwnd, new IntPtr(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        if (_windows.Count > 0) Native.SetForegroundWindow(_windows[0].Hwnd);
    }

    private void ReturnFocusAfterDialog()
    {
        if (!_focusBorrowed) return;
        _focusBorrowed = false;
        if (KeyboardMode) return;               // 键盘模式本来就要求能激活，别把它的样式改回去
        foreach (var w in _windows)
        {
            long ex = Native.GetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(w.Hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
            // 同 BorrowFocusForDialog：这里也不带 SWP_FRAMECHANGED（闪白来源）。
            Native.SetWindowPos(w.Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            // 把"置顶"装回去（覆盖层平时必须浮在所有程序上面）
            Native.SetWindowPos(w.Hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// 粘贴：**优先粘回可编辑对象**——剪贴板里有我们的对象格式就还原成对象，
    /// 没有就退回"当图片粘贴"（原有行为）。有原件位置就落在它旁边 24px，
    /// 否则落在视口左上角；粘完自动选中，直接能拖走。
    /// </summary>
    internal bool PasteFromClipboard()
    {
        ExitReplayForEdit("粘贴");
        if (ClipboardInk.TryGetObjectsWithRaw(out var objs, out var rawBytes) && objs.Count > 0)
        {
            var vp = ViewportCanvas;
            float m = CaptureMarginLogical * DpiScale;
            var box = EditRegion.Of(objs);
            float bw = box.MaxX - box.MinX, bh = box.MaxY - box.MinY;
            // 落点（2026-11-03）：
            //   · 原件**还在当前这一屏** → 挨着它放 24px（和 Ctrl+D 同一个手感）；
            //   · 翻了页 / 滚远了（原件不在眼前）→ 落在**当前屏中央**，不把人拽回去。
            //     这条照 Figma：视图离原件太远时，粘到当前视图中间。
            //   · 剪贴板不是我们刚复制的那批（另一个实例 / 外部程序）→ 老规矩，视口左上角。
            // 只认"剪贴板里还是我们刚复制的那批"（比字节）：中间被别的程序换过就不算。
            bool sameAsCopied = rawBytes != null && _lastCopiedBytes != null
                                && rawBytes.AsSpan().SequenceEqual(_lastCopiedBytes);
            bool sourceVisible = sameAsCopied && _lastCopyBox is RectF src
                                 && src.MaxY > vp.MinY + m && src.MinY < vp.MaxY - m
                                 && src.MaxX > vp.MinX + m && src.MinX < vp.MaxX - m;

            float tx, ty;
            if (sameAsCopied)
            {
                _pasteStep++;
                if (sourceVisible && _lastCopyBox is RectF srcBox)
                {
                    tx = srcBox.MinX + 24f * _pasteStep;
                    ty = srcBox.MinY + 24f * _pasteStep;
                }
                else
                {
                    tx = vp.MinX + (vp.MaxX - vp.MinX - bw) * 0.5f + 24f * (_pasteStep - 1);
                    ty = vp.MinY + (vp.MaxY - vp.MinY - bh) * 0.5f + 24f * (_pasteStep - 1);
                }
            }
            else
            {
                tx = vp.MinX + m;
                ty = vp.MinY + m;
            }

            // 夹进当前屏：连续粘贴、原件贴边、或图形比屏还大时都不会跑出去
            float maxX = MathF.Max(vp.MinX + m, vp.MaxX - m - bw);
            float maxY = MathF.Max(vp.MinY + m, vp.MaxY - m - bh);
            tx = Math.Clamp(tx, vp.MinX + m, maxX);
            ty = Math.Clamp(ty, vp.MinY + m, maxY);

            var move = Matrix3x2.CreateTranslation(tx - box.MinX, ty - box.MinY);
            foreach (var s in objs) s.Transform = s.Transform * move;   // 插入前平移（画布坐标）

            Doc.AddStrokes(objs);          // 一步撤销
            Doc.SelectOnly(objs);
            Tool = Tool.Marquee;
            NotifyUiStateChanged();
            _dirty = true;
            SetInkStatus(Loc.T("已粘贴 {0} 个对象（仍是可编辑对象）", objs.Count));
            Console.WriteLine($"粘贴 {objs.Count} 个对象（仍是可编辑对象）");
            return true;
        }
        return PasteImageFromClipboard();
    }

    internal bool PasteImageFromClipboard()
    {
        if (!ClipboardImage.TryGetImage(out var pixels, out int w, out int h, out bool hasAlpha))
        {
            Console.WriteLine("粘贴：剪贴板里没有可用的图像（只认 CF_DIB / CF_DIBV5）");
            return false;
        }
        var img = ImageData.Adopt(w, h, pixels, hasAlpha);
        if (img == null) return false;

        var vp = ViewportCanvas;
        float m = CaptureMarginLogical * DpiScale;
        var placed = Doc.AddImage(img, vp.MinX + m, vp.MinY + m, 1f / DpiScale);
        EnsureVisible(placed);
        Doc.SelectOnly(new[] { placed });
        Tool = Tool.Marquee;
        NotifyUiStateChanged();
        _dirty = true;
        Console.WriteLine($"粘贴图片 {w}×{h}");
        return true;
    }

}
