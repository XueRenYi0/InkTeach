# 方案：Win10 / Win11 系统托管湿墨与旁听接入评估（交给 DeepSeek V4.1）

> 日期：2026-10-12  
> 用途：本文件是后续实现的规格与决策门槛，不是已合并代码的说明。执行者先完成文中的探针与证据核对，再决定是否进入产品接线。  
> 范围：只评估“系统画湿墨、InkTeach 同时取得真实采样并继续使用现有笔迹模型”的桌面宿主方案，以及 Windows 10 / Windows 11 的替代路线。本文件不授权改写现有输入、渲染或对象模型。

---

## 0. 给实现者的决定摘要

### 当前决策

1. **Windows 11 继续以现有 DComp 委托墨迹轨迹为低延迟辅助主路，不替换。**它已经在 InkTeach 产品代码中按“系统画尚未上屏的一小段 + InkTeach 画整笔并逐帧结账”的协议接好；已验证系统/分支为 Win11 24H2（26100.7462）及更新版本。轨迹不可用或失败时，现有自绘湿墨继续工作。
2. **Windows 10 默认继续走 InkTeach 自绘湿墨；不把 DelegatedInkTrail 当作 Win10 能力。**Win10 上的实际产品基线是现有 `WM_POINTER` 输入、`Overlay.RenderFrame` 自绘、`Present` 帧配速和收笔后提交干墨。
3. **`InkDesktopHost + InkPresenter + CoreWetStrokeUpdateSource` 是 Win10 候选实验，不是可直接上线的替换后端。**官方 API 版本范围看起来覆盖 Win10；但裸 Win32 输入能否进入 Presenter、Presenter 是否能和本项目的每屏透明置顶窗口及 DComp/flip 交换链共存、UI 点击和穿透如何处理，均必须由独立探针在目标系统验证。
4. **不要把它与裸 Win32 `CoreInkPresenterHost` / `IInkPresenterDesktop` 接入尝试合并成同一条“已验证路线”。**仓库 `tools/coreink-probe/` 的 2026-10-12 测试在当前 Win11 24H2 上，对合成笔、鼠标、真笔、线程和前台变体均未使 Presenter 收到输入；旁听口也为 0 事件。2026-09-14 的旧成功记录与后续复测矛盾，旧记录不得作为产品可行性证明。
5. **唯一允许的实施顺序：独立探针 → Win10/Win11 两台真实设备验证 → 只做可关闭的实验后端 → 对照验收 → 再讨论默认策略。**在所有门槛通过前，`--syswet` 不得改成自动启用，现有后端不得删除或重构。

### 一句话判断

系统托管湿墨对 Win10 的理论增量较大（当前没有 Win11 式委托轨迹）；但产品不能只问“系统是否能画”，还必须同时回答“同一支笔的数据是否能完整进入 InkTeach、干墨是否无缝接手、工具栏/穿透/每屏窗口是否仍正确”。目前最优且安全的方案是 **Win11 保留 DComp 轨迹，Win10 保留自绘；将 InkDesktopHost/CoreWet 作为有严格退出条件的试验路线**。

---

## 1. 现有 InkTeach 架构事实（实施时不得破坏）

### 1.1 窗口、输入与界面命中

- `src/InkEngine/Engine.cs::CreateOverlays`：每个显示器创建一个 `OverlayWindow`，引擎负责统一输入和文档状态。
- `src/InkEngine/Overlay.cs::Create` / `CreateDeviceResources`：每屏覆盖窗是置顶、免激活的透明 Win32 窗口；用 DXGI composition swap chain、D2D、DComp 视觉树呈现。DComp 树当前为交换链子视觉，委托轨迹作为上层兄弟视觉。
- `Engine.WndProc` 在 `WM_POINTERDOWN/UPDATE/UP` 中调用 `OnPointerDown/OnPointerMove/OnPointerUp`；`WM_POINTERCAPTURECHANGED` 会走 `EndStroke`。绘制不是一个只收坐标的简单窗口过程。
- `Engine.OnPointerDown` 依次处理呼出盘、图库、回放、工具栏 UI、UI 占用区、计时卡/点名卡、PPT 条、穿透、触摸手势、单活动指针守卫、截图、橡皮、框选、图形和自由笔迹。把输入整段交给 InkPresenter 会绕开这套分流。
- 工具栏不是只靠覆盖窗口命中。`Engine.EnsureUiInputWindow` 创建单独、透明、置顶的“接输入小窗”，`UiInputWndProc` 再把消息转给界面；这是为了同时支持 `WS_EX_TRANSPARENT` 穿透和工具条可点。PPT 条、计时卡、点名卡也有单独输入窗。
- `Engine.SetPassThrough` / `ApplyPassThroughStyle` 会给覆盖窗口加 `WS_EX_TRANSPARENT`（按穿透档位也可能加 `WS_EX_LAYERED`）。系统墨迹宿主若要接管笔输入，必须额外验证穿透档下系统是否仍能接到笔、是否会阻断下层 PPT/应用；不可假设该样式与 InkPresenter 输入天然兼容。

### 1.2 采样、笔迹模型和提交

- `src/InkEngine/Input/PenInput.cs::PenSampleBuffer.Read`：同步读取当前 `PT_PEN` 消息的 `GetPointerPenInfoHistory`，反转成时间序，读取逐点坐标、压力、倾角、旋转和硬件 QPC；容量上限 64。
- `Engine.AppendStrokeSamples`：把真实输入转换到画布坐标并调用 `AddPointCleaned`，现有坐标净化、时间轴、缺压回填、PressureWidth、Wintab/raw 补点和逐笔诊断均在这条链路中。
- `Engine.BeginFreehandStrokeAt`：创建 `ActiveStroke`，记录工具、颜色、宽度、虚实线和压力状态，并开启当前窗口的轨迹。`Engine.ExtendFreehandStroke` 追加输入。这里的 `ActiveStroke` 是产品对象，不应改为 `InkStroke`。
- `Engine.EndStroke`：判断取消/图形/停顿成型/自由笔迹提交，最后调用 `Doc.AddStroke` 或相应文档动作；自动保存、撤销、回放、选中、导出均围绕 `Stroke` / `Doc`。
- `src/InkEngine/Model.cs`：`Stroke` 点数据和产品属性是 InkTeach 的唯一持久对象；不要让系统湿墨笔迹直接写入 `Doc`，也不要把系统 `InkStroke` 容器当成第二份文档。

### 1.3 当前湿墨/干墨协议（Win11 的基准实现）

- `Overlay.CreateDeviceResources`：`IDCompositionInkTrailDevice` → `CreateDelegatedInkTrailForSwapChain`，轨迹必须挂在 `_visual` 的交换链上方；每个显示器各自探测。
- `Engine.FeedInkTrail`：按笔/鼠标/触摸选择 `_pen` 或 `_ptr` 的真实历史点并将屏幕坐标和半径送给对应 `OverlayWindow`。
- `Overlay.BeginInkTrail` / `AddInkTrailPoints`：从新笔开始并记录 generation。系统轨迹只有“尚未由应用上屏的一段”，不是整笔数据模型。
- `Overlay.RenderFrame`：渲染 `ActiveStroke` 的自绘湿墨；只有 `TrailDrawing && app.IsSystemWetStroke(app.ActiveStroke)` 时才抑制自绘。
- `Overlay.Present`：成功上屏后，仅在本帧应用确实画了湿墨时调用 `TrimInkTrailAsDrawn` 结账删除已由应用画上的点；`EndInkTrailDeferred` 则等干墨帧 Present 后再撤系统轨迹。
- `Engine.IsSystemWetStroke` 以对象引用识别当前笔。`--syswet` / `--ownwet` 是诊断 A/B 开关，不是已经批准的后端自动选择器。

**核心不变量：**`Stroke`/`Doc` 中的数据必须只来自被验证的真实采样序列；湿墨可临时交由系统，但不能因此漏点、重排、重复入档或改变工具语义。干墨上屏后，系统临时墨迹应在同一次可见交接中撤掉，任何后端失败都不得导致提交数据丢失。

---

## 2. Windows 版本与 API 能力边界

### 2.1 能力矩阵

| 路线 / 能力 | API 的最低文档版本 | Win10 | Win11 | InkTeach 当前结论 |
|---|---|---|---|---|
| `IDCompositionDelegatedInkTrail` / `DelegatedInkTrailVisual` | WinRT 类要求 10.0.20348 / contract v12；Win32 COM 运行时必须 QI 探测 | Win10 客户端最高常见 build 19045，不具备此产品通道 | Win11 21H2+ 可能有 API；Chromium 生产实现以 24H2 为启用门槛，旧版存在屏幕区域闪烁风险 | 24H2+ 已在产品接好；21H2–23H2 需继续保守评估；不可将 COM QI 成功等同“视觉上有效” |
| `D2D1Ink`（干墨/压力笔迹） | Windows 10 D2D 1.3 能力，运行时 QI/创建探测 | 可作为现有自绘/干墨能力 | 可用 | 是干墨渲染能力，不是系统低延迟输入宿主 |
| `IInkDesktopHost` / `IInkPresenterDesktop` | 官方桌面 API：Windows 10 desktop app；InkPresenter 文档指出 InkDesktopHost 从 Win10 1511 起 | API 面存在；输入、WinRT presenter 获取、DComp 组合和真笔路由仍需验证 | API 面存在 | 官方宿主合同真实，但 COM 接口面小；不存在从 `IInkCommitRequestHandler` 回调直接取 Ink 点的 API |
| `CoreWetStrokeUpdateSource` / `CoreWetStrokeUpdateEventArgs` | Windows 10 Anniversary Update 1607 / build 14393 / UniversalApiContract v3 | 类型/API 版本覆盖 1607+；这不等于裸 Win32 可用 | 可用 | 它必须绑定一个真正收到输入的 `InkPresenter`；无法单独旁听任意 `WM_POINTER` |
| `CoreInkPresenterHost` | Windows 10 Fall Creators Update 1709 / build 16299 / contract v5 | 类型/API 版本覆盖 1709+ | 可用 | 裸 Win32 实测当前失败/历史结果矛盾；不得当成 Win10 通用接入保证 |
| 当前输入＋自绘湿墨 | 本项目已有代码，无新系统接口 | 可用 | 可用 | 所有 OS 的强制回退；Win10 生产基线 |

### 2.2 版本策略

1. 以“**运行时能力探测 + 实际湿墨探针 + 输入探针**”决定后端，不能只看 `Environment.OSVersion`，也不能只看 SDK target framework、API contract 元数据或 COM QI。
2. 系统版本至少记录 `RtlGetVersion` 的 major/minor/build、edition（若可取得）、DPI、刷新率、设备输入类型、驱动版本、是否穿透、窗口样式和每屏 monitor/DPI。测试记录必须包含这些字段。
3. Windows 10 的系统墨迹宿主候选最低验证线：
   - `IInkDesktopHost` 候选：Win10 1511（10586）起 API 理论存在；
   - `CoreWetStrokeUpdateSource`：1607（14393）起；
   - 若选择 `CoreInkPresenterHost`，不得低于 1709（16299）。
   - **产品最低版本不要在纸面上定为以上任一版本。**只有实际探针跑通、并定义最小受支持 SKU/build 后才可提高兼容声明；本方案当前产品降级线是“任何版本都保留自绘”。
4. Windows 11 委托轨迹：24H2（26100）+ 可列为已验证候选；21H2–23H2 先默认按“DComp 轨迹关闭、自绘湿墨”验收，除非在对应 build 上完成闪烁、长笔迹和显示器覆盖测试后另行拍板。
5. Windows 10 已于 2025-10-14 结束常规支持。学校的 LTSC/IoT/ESU 生命周期应单独核对；本技术兼容性不能被宣传为安全支持承诺。

### 2.3 三种不同的系统墨迹路线不可混称

**A. Delegated Ink Trail（当前 DComp 路线）**：InkTeach 收到输入并提供点；系统只画尾端；InkTeach 仍需自绘完整活动笔画、提交干墨、回收尾点。Win10 不可用。

**B. InkDesktopHost（Win32 COM 桌面宿主）**：官方允许桌面应用用 `IInkDesktopHost` 创建/初始化 `IInkPresenterDesktop` 并放入应用的 DirectComposition visual tree。它不等于“消息自然会同时到我们的 `WndProc`”。`IInkPresenterDesktop` 的公开方法只有 `SetRootVisual/SetSize/GetSize/SetCommitRequestHandler/OnHighContrastChanged`；提交回调无笔画 payload。若需要 Ink 数据，必须确认是否能可靠取得关联 WinRT `InkPresenter` 面，并通过实际事件取得数据。

**C. CoreInkPresenterHost（Windows.UI.Composition/WinRT 宿主）**：在自有 Windows.UI.Composition 树里承载 Presenter，不需 InkCanvas；通常需要 `DispatcherQueue`、`Compositor`、`DesktopWindowTarget`。这与 B 不是同一个对象/视觉互操作路径；在当前裸 Win32 探针上的输入结果为失败，不得借用旧成功数字宣称当前已验证。

**CoreWetStrokeUpdateSource 是附着在 InkPresenter 上的湿墨事件入口，不是独立输入捕获器。**它可在系统 Presenter 渲染前报告 `NewInkPoints` 并通过 `Disposition` 控制笔画状态。前置条件是 Presenter 本身收到该 pointer。只有满足此前置条件，旁听事件才有意义。

---

## 3. 证据台账：什么已知，什么还不能引用

### 3.1 当前可引用的仓库证据

1. `调研/实测-委托墨迹轨迹-2026-10-11.md`：本机 Win11 24H2 26100.7462 上系统委托轨迹通过视觉树挂载、逐帧采样和 generation 结账后，笔/鼠标/触摸均实际出墨；轨迹显示的是未上屏一小段。预测点填采样间隙曾甩尾，已撤。
2. `tools/coreink-probe/README.md`、`tools/coreink-probe/Program.cs`、`reports/coreink-probe.txt`：
   - 2026-10-12 当前机器为 Win11 24H2 26100.7462；合成笔、合成鼠标、真笔均未进入 Presenter；`CoreInkIndependentInputSource` 可创建订阅但 0 事件；窗口仍收到指针消息。
   - 多种变量（前台/target/线程/设备集）结果一致；探针 README 要优先于 2026-09-14 原始输出中的旧结论。
   - `reports/coreink-probe.txt` 是 2026-09-14 留档：其中旧探针曾记录 Presenter 1 笔/墨迹像素 2615，但后续不能复现；它只证明某次环境/探针组合曾出现成功，不证明当前产品设备和受支持的 Win32 路由。
3. `调研/调研-系统墨迹两条路线-委托轨迹与桌面宿主.md` §七记录 `IInkDesktopHost` 对接指南的核对：COM 初始化方法和 `IInkD2DRenderer` 的存在属实；“提交回调取出点集/压力/时间戳”错误；裸 Win32 路由多次未复现。

### 3.2 关于“32 个旁听点、30/30 帧、约 1.4 ms”的处理

先前讨论/外部验证曾给出 `CoreWetStrokeUpdateSource` 旁听约 32 个点/轮、30/30 帧看到系统出墨、快甩时平均约 1.4 ms、最大约 2.5 ms 的笔尖湿墨行程差，并观察到同帧清宿主墨迹会短暂空白。**当前仓库中没有找到可复现该组数字的原始报告/程序、OS build、API 宿主和测量脚本。**

因此本方案把它们标记为“待归档的先行实验结果”，不是产品验收证据。DeepSeek 在任何代码接线前必须：

- 找回并归档该组结果对应的原始探针/视频/计数/脚本；找不到时，在 `tools/ink-host-probe/` 重做同口径测试。
- 写清楚实验到底用了 `IInkDesktopHost` 还是 `CoreInkPresenterHost`、XAML Island 还是纯 Win32、Presenter 的 HWND/visual 如何连接、旁听源是 `CoreWetStrokeUpdateSource` 还是 `CoreInkIndependentInputSource`。不能只写“系统宿主”。
- 若旧样例运行在 XAML/UWP 或不同窗口模型，数字只能说明该模型可行，不能外推到 InkTeach 裸 Win32 覆盖层。
- 复测“同帧清除导致空白”的生命周期：`NewInkPoints` 事件、`WetStrokeStopping/Completed/Canceled`、系统墨迹移除、InkTeach 自绘帧和 Present 顺序均需逐帧记录。

---

## 4. 候选方案对比与推荐

| 方案 | Win10 | Win11 | 输入数据归属 | 渲染/集成代价 | 结论 |
|---|---|---|---|---|---|
| 1. 现有 WM_POINTER + 自绘湿墨 + D2D 干墨 | 可用 | 可用 | InkTeach 全权掌握 | 无新增架构风险；湿墨仍由本进程按帧呈现 | 必须保留的通用 fallback；Win10 当前默认方案 |
| 2. 当前 DComp DelegatedInkTrail + 自绘整笔 | 不可用 | 已在 24H2 26100.7462 上验证；旧 Win11 build 暂保守降级 | InkTeach 全权掌握 | 当前已接入；保持分段实线适配、视觉树与每帧结账契约 | Win11 主方案；不要改成整笔系统托管 |
| 3. `InkDesktopHost` + Presenter + `CoreWetStrokeUpdateSource` | API 版本理论覆盖 Win10 1511+/1607+，实际路由未证 | API 可用，但裸 Win32 Presenter 接输入目前不通过复测 | 若 Presenter 收到原始笔，需由 CoreWet 旁听后喂回产品模型；WM_POINTER 可能不再到原窗 | 需解决输入命中、逐屏视觉、DComp 交换链共存、UI/穿透、点回灌线程和干湿交接 | **仅作为 Win10 探针候选；必须先过阶段门槛** |
| 4. `CoreInkPresenterHost` + CoreWet | 1709+ API 理论存在；不能据此推导可工作 | 当前裸 Win32 探针接管输入失败；先不投入产品开发 | 同上 | Windows.UI.Composition DesktopWindowTarget 与 DComp/DXGI 内容整合成本高 | 除非出现可复现的官方/生产样例，暂不作为实施基础 |
| 5. XAML `InkCanvas` / WinUI `InkPresenter` | API 可用 | API 可用 | 系统控件为主 | 控件所属窗口，不是盖任意程序的全屏透明批注层；要保留产品形态需改写窗口/输入栈 | 不适合作为 InkTeach 的产品后端 |
| 6. 只用 `CoreInkIndependentInputSource` 旁听裸 Win32 输入 | 独立输入 API 本身很早就存在 | 当前探针 API 可创建，但真笔/合成输入均 0 事件 | 输入必须先进入绑定的 Presenter | 旁听口不能独立订阅任意 HWND；不解决宿主路由失败 | 不是替代方案，不要把“能创建/订阅”当成功 |

### 推荐推进路线

**立即可落地的策略（不需改代码）：**保持 Win11 DComp / 所有不具备有效系统宿主的系统自绘。Win10 不要等系统宿主才保证用户可写。

**唯一值得做的下一轮实验：**小型 `ink-host-probe`，分别验证官方 Win32 `InkDesktopHost` 和 WinRT `CoreInkPresenterHost`/CoreWet 两种候选，优先找出“Presenter 收到了真实 PT_PEN 输入，且同一 stroke 的 `NewInkPoints` 可逐点旁听”这一事实。若任一路线不能在真笔上做到，立即停止产品接线。

**更优的现实替代方案：**若裸桌面宿主不可行，不再追逐“给现有 WM_POINTER 消息抄送一个系统 Presenter”——系统没有通用的抄送 API，而且重注入会把 InkTeach 自身处理延迟带进湿墨。留在 `WM_POINTER`，用当前 DComp 刷新边界配速、合并历史、raw 补点（只对支持的设备）、自绘湿墨及稳定的干墨提交。需要进一步提速时，另开小型测量任务优化当前自绘路径，而不是替换输入主权。

---

## 5. 独立探针规格（阶段 0，先做，不改产品）

### 5.1 代码位置与约束

- 新建 `tools/ink-host-probe/`，不要覆盖 `tools/coreink-probe/`；沿用其 Win32 窗口计数、合成指针、真笔人工测试、日志和 README 方式。
- Probe 使用 x64、与 InkTeach 一致的 .NET 目标和 Windows SDK 投影。先以 JIT `dotnet run` 便于调试；只有原生/COM 互操作确实要求时再单独验证 NativeAOT。
- Probe 至少提供 `--route inkdesktop`、`--route corehost`、`--penonly`、`--synthetic`、`--pentest`、`--mainthread`、`--transparent`、`--passthrough`、`--monitor N` 等可组合选项；未知参数应打印帮助而不是静默忽略。
- 每次运行写一份 UTF-8 JSONL/文本报告到 `reports/ink-host-probe-<route>-<build>-<date>.txt`，附可复现操作步骤和 probe commit hash。

### 5.2 探针必须单独记录的计数

1. OS build / edition / DPI / monitor bounds / refresh rate / 窗口样式和 z-order。
2. Win32：`WM_POINTERDOWN/UPDATE/UP/CAPTURECHANGED`、WM_MOUSE、输入窗口 HWND 和 pointer type；按 ID 计数，不只汇总总消息。
3. Presenter：创建/初始化阶段每一步 HRESULT/异常、`InputDeviceTypes`、`IsInputEnabled`、StrokesCollected 笔数和点数、Presenter visual 是否可见。
4. `CoreInkIndependentInputSource`：创建成功与否、事件类型计数、pointer ID、线程 ID。该路仅作对照，不能代替 CoreWet。
5. `CoreWetStrokeUpdateSource`：创建成功与否；每个事件名/线程 ID/PointerId/Disposition/NewInkPoints 数；每个点的 Position、Pressure、TiltX/Y、Timestamp；每笔总点数、空事件数、时间戳单调率、点数与注入数/ WM_POINTER 数的比值。
6. 屏幕结果：至少 120 fps 录屏或每帧采样像素；按下期间系统墨迹可见帧数、落后像素/向量、抬笔后的残留帧；不能用“每隔十几个输入点截一次图”判断短暂湿墨。
7. 四种输入分别测试：真笔 Windows Ink (`PT_PEN`)、真笔鼠标模式 (`PT_MOUSE`)、合成笔、触摸；鼠标另列。不可从合成输入推断真笔可用。

### 5.3 探针用例及 Pass/Fail

**P0-1 初始化：**每个系统宿主独立建窗；记录 `DispatcherQueue` 创建线程、Compositor/target/host/presenter/thread。阶段通过条件为无异常、root visual 可见、应用可正常退出/释放。

**P0-2 真笔输入：**测试人员在窗口中书写水平线、急转折线和快速圆弧，每种 10 次；按下至抬起期间 Presenter 至少收到 10 条完整 stroke，CoreWet 起始/连续/完成事件闭合，NewInkPoints 总点数非零、顺序单调；同时把本 HWND 和各 UI input HWND 的 Win32 消息计数对照保存。

**P0-3 旁听准确性：**同一物理笔画的 CoreWet 点序列与（若同路能收到）WM_POINTER history 对齐；允许采样频率不同，但须解释差异。压力有压感设备上与 `InkPoint.Pressure` 变化一致；无压感设备不伪造压力能力。时间戳字段需记录并判断是否稳定，不能声称它与 `Qpc.Now` 同 epoch；必要时以每笔首点为零、用回调 QPC 只做相对锚定。

**P0-4 UI 命中与穿透：**画布落笔应到 Presenter；工具条/计时卡/PPT 条 UI 输入窗内不得开始系统湿墨，按钮按下、拖出、抬起行为仍由现有 UI 流程消费；全屏穿透时底层窗口仍可点击。任何工具条面积、触屏/笔 hover 或 pass-through 失败即 No-Go。

**P0-5 湿干交接：**先不取消系统湿墨，让 Presenter 独立完成整笔；采集系统墨迹像素直到系统自动移除/结束，再由 probe 的 D2D 画等价干墨。逐帧比较交接，不得用同帧 `Disposition=Canceled` 清宿主墨迹再假定它仍然出墨。至少测轻压/重压、抬笔、`PointerLost`、捕获改变、快速连笔、长笔（>128 点并持续 5 秒）。

**P0-6 回归配置：**每屏窗口、不同 DPI（100/150/200%）、混合刷新率/副屏左侧负坐标、`WS_EX_NOACTIVATE`、`WS_EX_NOREDIRECTIONBITMAP`、`WS_EX_TRANSPARENT`/layered、普通桌面与全屏 PPT；记录哪些配置不受支持。

**阶段门槛：**只要真笔输入未到 Presenter，或 CoreWet 没给可用点，或输入事件导致功能窗口丢消息，停止该候选。不得进入应用改造来“试试也许会好”。

---

## 6. 目标产品架构（只有探针通过后才允许实现）

### 6.1 不要将 API 接线直接塞进 `Engine.WndProc`

引入后端边界，使现有路由保持可回退；建议文件（具体命名可调整，职责不可合并）：

```text
src/InkEngine/Input/InkInputBackend.cs       // IInkInputBackend + InkSampleBatch/InkSample
src/InkEngine/Input/Win32PointerBackend.cs   // 包装当前 WM_POINTER / PenSampleBuffer 路径
src/InkEngine/Input/SystemWetBackend.cs      // 条件实现；InkPresenter + CoreWet event subscription
src/InkEngine/Input/InkBackendSelector.cs    // runtime probe、OS policy、诊断状态和 fallback
src/InkEngine/InkHost/InkHostController.cs   // per-monitor host/visual 生命周期；不拥有 Doc/Stroke
```

接口示意（仅规格，不要求照抄语法）：

```csharp
interface IInkInputBackend : IDisposable
{
    InkBackendKind Kind { get; }
    InkBackendCapabilities Capabilities { get; }
    void BeginStroke(in InkStrokeRequest request);
    void DrainSamples(Span<InkSample> destination, out int count);
    void EndStroke(InkStrokeEndReason reason);
}
```

- `InkSample` 必须是非 WinRT 的值类型：物理屏幕 X/Y 或明确的 monitor-local 像素、pressure + `HasPressure`、tilt、rotation（若源可提供）、每笔相对时间和 pointer/monitor ID。
- `SystemWetBackend` 在事件线程只复制 `NewInkPoints` 到有界缓冲区，记录溢出和字段缺失；**不得**从回调操作 `Doc`、D2D context、DComp、UI、文件、日志 I/O、同步等待主线程。
- `Engine.Loop` / `DrainMessages` 只在主线程 drain 队列，再经既有 `AddPointCleaned` 写进当前 `ActiveStroke`；同一个 `AddPointCleaned` 和 `EndStroke` 是功能/文档的唯一入口。
- 探针显示回调可能多线程时使用线程安全的有界多生产者队列；确认单生产者后可换预分配 ring buffer。队列必须预分配、满时有计数和可见错误，不可无上限 `ConcurrentQueue` 堆积。
- 第一版产品实验后端只支持一个活跃笔 pointer；遇到第二笔/触摸/鼠标/未支持工具时，拒绝系统宿主路由或让其走既有 WM_POINTER（按探针已验证的路由办法），不可让两套后端同时提交一笔。

### 6.2 输入与工具仲裁

现有 `OnPointerDown` 有丰富命中与工具仲裁，系统 Presenter 的 `InputDeviceTypes` 只能按设备大类设定，不能替 InkTeach 判断当前工具。必须采用以下规则：

1. **产品默认只考虑真笔 + `Tool.Pen` + 实线 + 可接受的笔属性**。荧光笔、激光笔、橡皮、选择/图形/截图、虚线/点线不启用系统宿主墨迹；它们继续走 InkTeach 路由。
2. 在系统宿主开始接管输入前，必须决定当前 `Tool` 和命中区域。现有 GUI 的输入窗是独立 HWND，适合作为最上层命中豁免；但指针从画布进入 UI、笔按在 UI 后拖出、切工具、切穿透等转场须真机覆盖。不能只在 `WetStrokeStarting` 事后取消 UI stroke，因为系统可能已经 capture 该 pointer，后续 UI `WM_POINTER` 不一定还会来。
3. `WetStrokeStarting` 的 UI 命中取消仅可作为二级防线：确认 `Disposition=Canceled` 后系统墨迹确实不显示/结束事件如预期，再使用。取消也不等于把输入消息重新投递到另一 HWND；不能依靠“取消 Presenter 就会回到 WndProc”。
4. 对系统湿墨支持的那一笔，Win32 输入与 CoreWet 旁听不能同时 `AppendStrokeSamples`；按 pointer ID/ stroke token 只选一个作为产品点源。若 WM_POINTER 和 CoreWet 都收到，产品配置应由 selector 只选一条，不应两边都写入。
5. 系统轨迹起笔失败、初始化未就绪、当前工具不支持、点流无效时必须在起笔前回落到 Win32 自绘。若 Presenter 中途停止/丢失/队列溢出，记录 stroke 状态并执行显式失败策略；禁止“系统还显示但 InkTeach 没有可提交数据”或“提交只有前半笔”的静默情况。

### 6.3 点坐标、压力、时间与对象数据

- `InkPoint.Position` 文档单位是**相对 inking area 左上角的 DIP**；InkTeach `Stroke.Points` 使用画布物理像素。换算必须明确包含宿主窗口 monitor-local 原点、当屏 DPI、虚拟桌面负原点和当前 `ScreenToCanvas` 相机转换。不可将 `Position` 直接当作物理屏幕坐标。
- 每点压力来自 `InkPoint.Pressure`。仅当点源/设备确实支持压力时设置 `HasPressure`；不要沿用 `ReadPointer` 中旧式 `pressure<=0.01 => 0.5` 的 hover/无压感混淆。现有 `PenSampleBuffer` 已把能力和占位值分离，模型应遵循同一语义。
- `InkPoint.TiltX/TiltY/Timestamp` 能否在目标 Win10/Win11 实际设备上稳定给出，逐字段测。`InkPoint.Timestamp` 的官方定义是点时标，但其时钟 epoch 与本地 QPC 是否一致未承诺；用同一笔首个有效时标作相对零点，校验非递减、跳变和 wrap。无效时标用接收时刻构造单调的相对时间并将降级标志写入诊断，不能输出负时间或伪造 QPC 精度。
- `InkPoint` 无 `PEN_FLAG_INVERTED/ERASER`、pen barrel 键或完整 Win32 PointerFlags 等价保证。若这些状态无法从另一个已验证旁路拿到，宿主后端就只适用于不需要它们的 Tool.Pen 情况；不能让笔尾橡皮变成墨迹。
- CoreWet `NewInkPoints` 是“自上次事件以来处理的点”；`WetStrokeCompleted` 和 `WetStrokeCanceled` 的 NewInkPoints 总为空，`WetStrokeStopping` 也可能为空。实现者必须在 Starting/Continuing/Stopping 中累计点，Completed/Canceled 只负责闭合状态；不得只在 Completed 读取点。
- 一个 CoreWet stroke token（host ID + PointerId + monotonically increasing local stroke sequence）只映射一个 `ActiveStroke`。`PointerId` 可能复用，token 需在每笔后递增。完成事件必须对应唯一 EndStroke；cancel/lost/capture changed 必须关闭且不重复提交。

### 6.4 渲染/视觉树与每屏窗口

1. `InkDesktopHost` 官方路径的优势是面向 Win32 DComp visual tree；探针应先确认 presenter visual 是否能插进现有 `_visual`，并和 `_swapVisual` 的排序正确共存。
2. `CoreInkPresenterHost` 使用 Windows.UI.Composition 树/`DesktopWindowTarget`。若它占用当前 HWND 的 compositor root，`Overlay` 的 DComp swap chain 必须通过受支持互操作（例如 Composition surface for swap chain）迁入同一个 Windows Composition 树；这是表达层重写，不是 `SetRootVisual` 一行代码。若最小原型需要整体改写 Overlay，则判定不符合本阶段范围。
3. InkTeach 为每屏建 `OverlayWindow`。实验方案必须是每屏独立 host/Presenter 和独立坐标映射，或证明一个 host 的视觉/输入可跨屏；不可把副屏笔迹按主屏 origin 偏移。
4. 当前 DComp 委托轨迹的事件/资源按 `OverlayWindow` 管理（`_trailProbed` 按窗口）。新后端也应逐窗口生命周期；设备资源重建、显示器热插拔、分辨率/DPI 变化、窗口销毁时要结束活动笔并释放 event token/COM/WinRT 对象。
5. 若为了 system wet 新建独立全屏宿主 HWND，必须先证明它可正确置顶且与原 Overlay/UI input window z-order 协调、同一位置无双影、系统命中正确、点击穿透有效。不要把多个全屏透明窗口叠在一起后依赖“视觉看起来没问题”。

### 6.5 湿墨到干墨交接（首个版本的安全实现）

优先实现**重叠一帧的“先自绘、后关湿墨”交接**，不要追求一帧内互斥切换：

1. 收到系统 CoreWet 点时，先将真实点 drain 到主线程并继续追加 `ActiveStroke`，同时 Presenter 暂时保持系统 wet visual。
2. `EndStroke` 正常完成时，先走现有提交逻辑：`ActiveStroke.RawWhileLive=false` → `Doc.AddStroke(ActiveStroke)`；标记一次 dirty frame。
3. 下一次 `RenderAll` 让 Overlay 内容层/活动笔/分块缓存按现有路径把完整干墨画入，并成功 `Present`。
4. **只在这一帧干墨已成功上屏以后**，再请求结束/清除系统 Presenter 的 wet stroke（或切到 custom drying 后确认 ink layer 的 wet visual 按 API 生命周期移除）；不得先撤湿墨再等待主线程渲染。
5. 允许最多一帧短暂湿/干叠加，但须通过 alpha/color 对比证明不会显著变粗/变深；若此项对高亮工具/半透明笔无法满足，就不要给该工具启用宿主路线。
6. 失败必须优先避免空白帧：Present 失败时湿墨继续保留，重试一次/记录错误；禁止按“计划已渲染”提前释放 Presenter stroke。已有 `Overlay.EndInkTrailDeferred` / `Present` 成功后删点的代码可作顺序参考，但系统 API 生命周期必须独立验证，不能直接套调用名。
7. 不要默认 `ActivateCustomDrying` 就能将 Presenter 变成“只画 wet、不存 stroke”。它会改变 InkPresenter 干燥/容器行为，需要与 `InkSynchronizer`、`IInkD2DRenderer` 一起实测。第一轮先让 Presenter 自己完成完整 stroke 并做像素对照，随后才实验 disposition/custom-drying；保留 `Stroke` 点数据仍由 CoreWet 流负责。

---

## 7. 运行时选择和降级状态机

### 7.1 `InkBackendSelector` 必须显式给出状态

建议枚举：

```text
OwnWetOnly                 // 默认安全态，现有 Win32 输入 + InkTeach 自绘
DelegatedTrailPlusOwnWet   // Win11 轨迹可用；现有产品态
SystemWetSideTapCandidate // 探针已过；仅用户/测试显式 opt-in
SystemWetActive            // 当前 stroke 的 Presenter + CoreWet 均运行且数据有效
SystemWetFailedFallback   // 禁止下笔进入系统宿主，回落 OwnWetOnly
```

### 7.2 决策次序

1. 初始化 `OverlayWindow` 的现有自绘/委托轨迹资源；自绘始终可用时才允许启动窗口。
2. 委托轨迹只在版本策略、DComp QI/视觉树挂载、可见性探针和本窗口资源全部通过时打开；否则 `_inkTrail` 为 null 并走 OwnWetOnly。具体 build 门槛按本文件 2.2。
3. 系统湿墨后端**默认禁用**，只在显式实验参数（新 flag 建议 `--systemwetprobe`，与历史 `--syswet` 保持兼容但不重用其语义）下启动。`--ownwet` 永远强制关闭系统湿墨。
4. 后端选择在每笔 `BeginFreehandStrokeAt` 之前决定，不允许在笔画进行到一半静默切换输入数据源。
5. 候选初始化失败时打印简短原因、HRESULT/Exception 细节写诊断日志、销毁半初始化 host，继续现有 Win32 自绘。产品不得弹“需安装运行时”或网络下载，不得阻止用户开写。
6. Win10 即使 API contract 存在但 Presenter 输入失败，也显示/记录 `OwnWetOnly (CoreWet unavailable: presenter received 0 pointers)`，不得重复尝试每笔导致卡顿。
7. Win11 24H2+ 默认继续 `DelegatedTrailPlusOwnWet`。系统宿主候选只允许单独 A/B，不能叠加 `--syswet` 或同时让多个后端都画完整 wet stroke。

### 7.3 兼容和取消策略

- `--ownwet` 必须能在任何探针/候选故障后立即验证“原应用路径还好”。
- `--noinktrail` 保持现有语义：关闭委托轨迹；不得被新宿主 flag 改写。
- `--trailonly` / `--wetinktest` 属委托轨迹诊断；不要拿来判 CoreWet。新探针应独立命名 `--systemwettest` / `--systemwet-live`。
- 不更改已有用户偏好含义/配置 schema；实验 flag 默认关且不写持久设置。只有完成真机/低配机回归、另行批准后才讨论用户设置项。
- 设备/Windows 版本/宿主创建/事件注册/视觉目标任一能力不确定时，优先走系统支持更广的 OwnWetOnly，而不是根据版本号冒险。

---

## 8. 分阶段实施工作单（DeepSeek V4.1 按序执行）

### 阶段 A：证据归档与探针（允许新增 probe 代码，不碰 `src/InkEngine`）

1. 按第 5 节新建 `tools/ink-host-probe/`。
2. 先做 API 创建矩阵：
   - route 1：`IInkDesktopHost` 创建 Presenter、visual 和 WinRT presenter 面获取方式；
   - route 2：`CoreInkPresenterHost` + `CoreInkIndependentInputSource` + `CoreWetStrokeUpdateSource`；
   - 不允许把 “COM 对象 QI 成功” 记作输入通路通过。
3. 先以合成笔注入发现消息路由，再以真笔 Windows Ink 模式验收。若 Win10 1607/1709+ 真实旧设备不可得，当前阶段不得宣称 Win10 产品支持，只可写“未验证”。
4. 验收并归档各 P0 测项。如果候选只在 XAML/UWP 输入树成功，记录为该宿主成功、InkTeach 裸 Win32 No-Go；不得拉入产品。

### 阶段 B：接入后端抽象（只有至少一个目标 Win10 真机 P0 全 PASS 后允许）

1. 新增 `InkSample`/`IInkInputBackend`/`InkBackendSelector` 和 `Win32PointerBackend`；先把当前输入路径包装而不改变行为。
2. 运行现有完整 `--selftest` 与功能/压力/触摸/光标/UI/穿透自检，确认包装前后日志、落笔点、工具行为一致。此提交不接系统宿主。
3. 增加 `SystemWetBackend` 仅负责创建 presenter、CoreWet event tokens、预分配队列和诊断；feature flag 默认关。事件回调不得向 Win32 WndProc 递交合成消息。
4. `Engine.DrainMessages` / 主循环每帧开头在 UI/消息派发后 drain 样本；若收到系统 stroke start 而 UI 尚未选好当前 Tool，候选应拒绝起笔或由已证明有效的 UI topmost input window 把输入挡住。
5. `BeginFreehandStrokeAt/ExtendFreehandStroke/EndStroke` 维持统一 Stroke/Doc 入口。对系统 wet stroke，不读取 `_pen` 来补同一支输入，也不把 CoreWet 新点再次 Feed 到 DComp 委托轨迹。
6. 扩展 `[笔画]` 报告：backend、route、pointer ID/token、CoreWet points、WM_POINTER points、pressure/tilt/timestamp 有效数、队列 high-water/drop 数、wet-to-dry 帧数、fallback 原因、系统版本/build、monitor/DPI。不能打印每个点到控制台。

### 阶段 C：交接与实验开关

1. 新增 `--systemwettest --systemwet-live N` 真笔测试，复用 `InkMotion`/`ScreenProbe` 的“逐帧取样”纪律，但不能重用委托轨迹判据。
2. 通过第 6.5 节的 overlap handoff 实现单笔系统湿墨到 InkTeach 干墨交接。
3. 至少做 `Tool.Pen` 实线、不透明 1 种配置；其他颜色 alpha、压力、高 DPI、虚线、荧光笔、激光器一概明确 opt-out。
4. 所有用户可见路径保持 `--ownwet` 一键退回；若 probe route 运行过程中 CoreWet stop/cancel/lost，确保 EndStroke 幂等，不产生重复 Doc.AddStroke。

### 阶段 D：产品决策（不能由实现者自行默认开启）

只有以下全部成立，才可提交“建议打开 Win10 系统湿墨”供产品拍板：Win10 至少两种不同设备/驱动真笔输入 PASS；Win11 24H2+ 对照不回归；每种支持的 UI/穿透/副屏/DPI 测项 PASS；长时间书写无漏点/重复点/队列积压；湿干交接的视觉和延迟收益在盲测可辨；失败自动回退；无新运行时安装要求；自检和文档有证据。否则阶段 D 的结论必须为“保持自绘”。

---

## 9. 风险清单与明确处置

| 风险 | 具体症状 | 防护/停止条件 |
|---|---|---|
| 裸 Win32 输入没有进入 presenter | WM_POINTER 正常、CoreWet 0 点；旧探针已有相同迹象 | 这是最早 No-Go；不开发产品后端 |
| 输入被 Presenter/系统窗口截走 | 工具条按钮失灵、停顿成型/手势/橡皮丢消息 | 先解决窗口 z-order/hit-test；不能靠后来取消 wet stroke 返还输入 |
| DComp 与 Windows Composition root 冲突 | 黑屏/副屏无图、交换链不再 present、设备重建 | `CoreInkPresenterHost` route 若要迁移全套 Overlay root，则不属于本次局部接入，停止 |
| InkPoint DIP/屏幕坐标错 | 与底层 PPT 位置偏移、跨 DPI/副屏飘移 | 使用每个 presenter 的 monitor/DPI 原点换算；测试负虚拟坐标和 DPI 变化 |
| CoreWet 回调线程阻塞 | 抖动、wet latency 增加、系统输入丢包 | callback 只复制值到有界队列；无同步 invoke、I/O、D2D 调用 |
| 点时标不可直接与 QPC 对齐 | 速度/笔画时长异常、回放乱序 | 每笔相对时间，验证 monotonic；不能假称同一绝对时钟 |
| host wet 没有与 InkTeach 点完全一致 | 抬笔粗细/颜色/曲线跳变 | 只允许 Pen 实线起步；像素 A/B；设置可见重叠 handoff |
| 同帧先撤 wet 再画 dry | 一帧空白或笔尖断线 | Present 成功后再通知系统结束/清除，不能只凭回调完成假设已上屏 |
| 长笔系统缓冲上限/自动回收未知 | 湿墨中途消失或未结束残留 | 5 秒/10k 点压力；记录系统自清和完成时序；不能借委托轨迹 128 点限制推断 CoreWet 上限 |
| PT_PEN 与 PT_MOUSE 模式不同 | 关 Windows Ink 后没压力/Presenter 不收鼠标 | 按真实输入类型识别，PT_MOUSE 仍用现有 Wintab/raw 自绘路径 |
| 系统更新/驱动差异 | 某 build/驱动无湿墨或 UI 事件变化 | capability-based runtime fallback；每次错误最多记录一次，不重试每笔 |
| Win10 安全生命周期 | 仍可运行但系统不再常规安全更新 | 支持矩阵写明 OS edition/ESU/LTSC 边界，非技术兼容承诺 |

---

## 10. 验收清单

### API 与输入

- [ ] 报告标明具体 host route；不是用“CoreInk”统称。
- [ ] Win10 真笔 `PT_PEN` 输入到 Presenter 的 Starting/Continuing/Stopping/Completed 事件完整且有 NewInkPoints。
- [ ] CoreWet 点数与真实 stroke 预期匹配；没有静默丢点，点序、pressure、timestamp 的有效率有数字。
- [ ] Win11 真笔、Win10 真笔、鼠标模式、合成笔、触摸分别有结果；不可将一项结果复制给所有设备。
- [ ] `WetStrokeCompleted/Canceled` 的 NewInkPoints 空值已正确处理；capture lost/unexpected stop 不会漏掉或重复 EndStroke。
- [ ] system host 创建失败/接口不可用/无输入事件可以自动进入 OwnWetOnly，不使应用启动失败。

### 交互和窗口

- [ ] Pen 工具、实线落笔工作；工具栏/自绘面板/计时卡/点名/PPT 条点击和拖动工作。
- [ ] `WS_EX_TRANSPARENT` / 两种穿透档位下底层程序仍可点击；不出现透明 host 吞键。
- [ ] 副屏、屏幕左侧负坐标、每屏独立 DPI、分辨率变化、拔插屏幕均无漂移/残留。
- [ ] 触摸手势、第二根手指、指针 capture lost、笔尾橡皮、桶身键的限制明确；不支持的情况保留现有路径。

### 视觉与数据

- [ ] 书写中系统 wet 像素真实可见，不是 InkTeach 的自绘假对照；逐帧采样率不低于显示刷新率。
- [ ] 抬笔后同一中心线/宽度/压力完成干墨接手；没有空白帧；重叠一帧不出现明显变粗/变深。
- [ ] `Doc.Strokes` 只增加一条预期 `Stroke`；点数/压力/颜色/时间轴通过对照；撤销一次撤销完整一笔；回放、序列化、自动保存、导出一致。
- [ ] 系统 wet 不被保存到 INKB、不进入 Undo 两次、不作为第二份 InkStroke 文档长期驻留。
- [ ] `Present` 失败、device lost、host shutdown、应用退出和显示器重建时，不遗留系统笔画或 COM/WinRT 资源。

### 性能与回归

- [ ] 系统 wet 相对 `--ownwet` 有可测的实际收益（采样/上屏时间与落后像素分开报），不是只依赖主观描述。
- [ ] callback 墙钟耗时平均/最大值、队列高水位和 drop count 已报；无无限队列增长。
- [ ] 书写 5 分钟、长笔 >10,000 点、连续快速 100 笔、连续切工具/穿透/显示器布局，均无泄漏/卡顿/异常帧。
- [ ] 运行现有 `tools/run-selftest.ps1` 及功能/性能测试套件；触摸、UI hit-test、stroke serialization、wet/dry、DComp trail 都 PASS。
- [ ] Win11 原有 DComp 轨迹仍按现状工作：轨迹视觉在 `_swapVisual` 上方，正常模式喂点/结账相符；无轨迹能力时 OwnWetOnly。
- [ ] 默认启动在 Win10 和 Win11 都保留已验证的现有湿墨回退；实验 flag 默认 off。

---

## 11. 代码修改索引（规划，不代表已实施）

### 必读文件

- `src/InkEngine/Engine.cs`：字段 `_pen`/`_ptr`、`WndProc`、`OnPointerDown/Move/Up`、`BeginFreehandStrokeAt`、`ExtendFreehandStroke`、`AppendStrokeSamples`、`EndStroke`、`ReadPointer`、`DrainMessages`/`Loop`/`RenderAll`。
- `src/InkEngine/Overlay.cs`：`CreateDeviceResources`、DComp visual root、`RenderFrame`、活动笔抑制条件、`Present`、`EndInkTrailDeferred`/`TrimInkTrailAsDrawn`、`Dispose`。
- `src/InkEngine/Input/PenInput.cs`：`PenSample` / `PenSampleBuffer.Read` 是现有真实点字段和时间基准，不要破坏。
- `src/InkEngine/Model.cs`：`Stroke`、`PressureWidth`、文档对象边界；系统对象不能替代产品对象。
- `src/InkTeach/SelfTests/InkMotion.cs`：现有 `--wetinktest`、`--wetdrytest` 和输入/渲染自检；增加独立系统宿主测试时避免混淆轨迹与 CoreWet。
- `tools/coreink-probe/README.md`、`tools/coreink-probe/Program.cs`、`reports/coreink-probe.txt`：失败与矛盾证据，禁止覆盖历史。

### 第一阶段允许修改的范围

只添加 `tools/ink-host-probe/`、对应 `reports/` 结果和本方案后续的证据更新。未经阶段 A 通过，不得改 `src/InkEngine/`、`src/InkTeach/SelfTests/`、`README.md`、用户偏好或默认 flag。

### 一旦开始产品实验后的硬边界

- 不改 `Stroke` 序列化版本；新湿墨 provider 不要求持久化新字段。
- 不改当前 DComp trail API/视觉树/回收时序作为“配合宿主”的顺手重构。
- 仅由 `EndStroke` 提交文档对象，不从系统 `StrokesCollected` 再提交同一支笔。
- Win32、自绘 Wet 和 DComp Trail 需可以分别强制关/开，诊断值能打印每个 monitor 当前实际选择的后端。
- 不新增 WinAppSDK/XAML 必需运行时；学校机器无需安装额外框架，基础自绘路径仍是绿色启动。

---

## 12. 参考文档和仓库内证据

### Microsoft Learn / API

- `CoreWetStrokeUpdateSource`（Win10 1607 / 14393 / UniversalApiContract v3；Create(InkPresenter) 与 WetStroke* 事件）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.core.corewetstrokeupdatesource
- `CoreWetStrokeUpdateEventArgs`（`PointerId`、`Disposition`、`NewInkPoints`；Completed/Canceled NewInkPoints 为空，Stopping 可能为空）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.core.corewetstrokeupdateeventargs  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.core.corewetstrokeupdateeventargs.newinkpoints
- `CoreWetStrokeDisposition`（Inking/Completed/Canceled）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.core.corewetstrokedisposition
- `InkPoint`（Position 为相对 inking area 左上角的 DIP；Pressure/Tilt/Timestamp）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.inkpoint
- `CoreInkPresenterHost`（Win10 1709 / 16299 / contract v5）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.core.coreinkpresenterhost
- `CoreInkIndependentInputSource`（说明事件发生在 ink background thread，必须轻量；它绑定 InkPresenter）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.core.coreinkindependentinputsource
- `InkPresenter`（Windows.UI.Input.Inking；InkDesktopHost Win10 1511+、CoreInkPresenterHost Win10 1709+ 的文档备注）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.inkpresenter
- `IInkDesktopHost` / `IInkPresenterDesktop`（Win32 API 与方法列表）：  
  https://learn.microsoft.com/en-us/windows/win32/api/inkpresenterdesktop/nn-inkpresenterdesktop-iinkdesktophost  
  https://learn.microsoft.com/en-us/windows/win32/api/inkpresenterdesktop/nn-inkpresenterdesktop-iinkpresenterdesktop
- `InkPresenter.ActivateCustomDrying` / MSDN Magazine “Digital Ink - Ink Interaction in Windows 10”（自定义干燥与 SIS/VSIS/SwapChainPanel 同步区别）：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.input.inking.inkpresenter.activatecustomdrying  
  https://learn.microsoft.com/en-us/archive/msdn-magazine/2015/windows-10-special-issue/digital-ink-ink-interaction-in-windows-10
- WinRT `DelegatedInkTrailVisual` API 最低 contract：  
  https://learn.microsoft.com/en-us/uwp/api/windows.ui.composition.delegatedinktrailvisual

### 本仓库

- `调研/实测-委托墨迹轨迹-2026-10-11.md`
- `调研/调研-系统墨迹两条路线-委托轨迹与桌面宿主.md`
- `调研/调研-压感数据与墨迹预测.md` §7（包含 2026-10-12 CoreInk 复测结果及当时“旁听”待验证项；本方案进一步明确它不能作为现存 API/产品能力证据）
- `tools/coreink-probe/README.md`
- `tools/coreink-probe/Program.cs`
- `reports/coreink-probe.txt`（历史矛盾记录，须与后续 probe README 一起阅读）

---

## 13. DeepSeek 完成后回复格式

实现者交付时必须逐项回答，不得只说“编译通过”：

1. 实际修改文件列表；若只做 probe，应明确 `src/` 未改。
2. 每个 API route 的 OS/build、硬件、Windows Ink/鼠标模式、真笔/合成输入、消息计数、CoreWet 事件/点数和像素证据。
3. 阶段 A 的 P0 表格（PASS/FAIL/SKIP + 报告链接）；未跑真笔必须写 SKIP，不能按合成输入推断。
4. 最终决策：Win10 候选是否通过；Win11 原路径是否完全不变；哪些版本/设备自动 fallback。
5. wet→dry 逐帧顺序、点/压力/时间轴映射、UI/穿透/多屏处理说明。
6. 现有 `--ownwet` 和 DComp trail 自检结果、完整功能自检结果、性能/内存对照。
7. 任何未验证的字段或设备都列为限制，不得写入支持说明。

**只要当前 Win11 裸 Win32 CoreInk 探针仍是 0 输入事件、Win10 真笔尚未通过或系统与 InkTeach 的点流不能一一正确对应，最终答复应是：方案已评估，产品继续使用“Win11 DComp 委托轨迹 + 全系统自绘湿墨回退”，不启用 Windows Ink 桌面宿主。**
