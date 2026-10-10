# 输入栈盘点与"切换到 RTS（RealTimeStylus）"可行性

日期：2026-10-10　　任务：用户问"我们现在的输入处理，以及更换的 RTS 的可行性"——**只调研，不写码**。
（同日追加：§七 核对传闻"微软建议实时批注用 RTS"。同日修正：文中"湿墨交给 DWM 委托墨迹"几处，
按 **2026-10-05 真笔实测定案**改写——**本机系统不渲染、实况湿墨＝自绘**，见 `延时-实测与优化.md` §八。）

> **口径**：按墨迹语境，RTS = **RealTimeStylus**（Windows 7 / Tablet PC 时代的实时笔输入栈；
> WPF `InkCanvas` 的 `StylusPlugIn` 底层就是它）。若你听到的是别的意思，见文末「附录：RTS 的其它含义」。

---

## 一、结论先行

**不建议换，也基本不可行**——不是"做不出来"，而是"换上去整体更差"：

1. **硬约束**：RTS 一旦启用，窗口**收不到 `WM_POINTER` / `WM_TOUCH`**（官方与实测一致）。
   我们现在的输入层、触摸手势、光标、悬停、穿透……**全部**架在 `WM_POINTER` 上——
   换 RTS 等于把整条输入链路重写，不是"换个模块"。
2. **收益≈0**：RTS 的历史卖点是"绕过消息队列、贴硬件包率、独立渲染线程"。
   这三样我们**已有等价或更好的**：Raw Input 补点把包率从 60–79Hz 抬到 **154–194Hz**；
   湿墨通道（DWM 委托墨迹轨迹）**第一时间就接好了**——但要按实况修正一句：
   **用户手写板上系统不渲染它**（2026-10-05 真笔实测：COM 全成功、屏幕上无墨，
   `--syswet` 不可启用），所以实际湿墨一直是**自绘**，靠合成边界配速把上屏压到 1.0 帧
   （60Hz 端到端 ≈26ms；120Hz 时上屏段 ≈10ms）；
   我们实测"消息排队→拿到"这一段只占 **0.05ms**——RTS 想省的正是这一段，省了也白省。
3. **延迟地板不在输入栈**：60Hz 屏上端到端 ≈26ms，其中 16.7ms 是**刷新周期**（合成+扫描输出，应用改不了）。
   输入栈再快也动不了这 16.7ms 的 99%。
4. **形态冲突**：RTS 要"挂到自己的窗口/控件上"，**不能跨进程**；而我们做的是
   "全屏置顶透明**覆盖层** + 穿透"——RTS 的服务模型天生不是给这种形状用的。
5. **生态逆风**：微软把 `WM_POINTER` 当现代统一栈（Win8 起）；连 WPF 自己的
   "改用 pointer"迁移开关（`EnablePointerSupport`）都**从未打磨完成**（社区里 Surface Pro 9 崩溃、
   press-and-hold 失效等一堆问题）。社区结论（SDL、专业绘图圈）："没理由碰 RTS"。

一句话版：**"没接 RTS"不是缺功能，是没走老路；现在更不会回头走。**

---

## 二、RTS 是什么（事实快照）

### 2.1 两套形态

| 形态 | 入口 | 说明 |
|---|---|---|
| **COM**（原生） | `IRealTimeStylus`（`rtscom.h` / `RTSCom.dll`，XP Tablet PC 起；`IRealTimeStylus3` = Win7 起支持多点） | 挂到窗口/控件；插件分**同步插件**（高优先级线程、逐包实时，可过滤/修改/取消包）与**异步插件**（UI 线程）；官方附送 `DynamicRenderer`（独立"动态渲染线程"画湿墨）、`GestureRecognizer`、`StrokeBuilder` |
| **WPF 托管层** | `System.Windows.Input.StylusPlugIns`（`StylusPlugIn` / `DynamicRenderer`） | 就是 COM RTS 的托管包装；**.NET 10 的 WPF 里仍然可用**（文档一直在更新） |

### 2.2 数据通路与历史卖点

- RTS 的数据来自 **`wisptis.exe`**（WISPTIS = Windows Ink Services Platform Tablet Input Subsystem），
  走"进程锁 + 共享内存"直接把触点/笔点喂给订阅者，**绕过窗口消息队列**——这是它当年"低延迟"的来源。
- 同步插件运行在**高优先级线程**上（官方警告：只能做"计算不重"的活，如包过滤）；
  湿墨由 `DynamicRenderer` 在**独立渲染线程**画，UI 线程卡住也不断墨——这是 WPF 写字"跟手"的老配方。

### 2.3 明确的硬约束（官方文档口径）

- **"You cannot attach the RealTimeStylus object to a window or control in a different process."**（不能跨进程挂）
- 启用 RTS 后，**窗口消息里收不到 `WM_POINTER` / `WM_TOUCH`**（多个来源一致："
  开启 RealTimeStylus 之后，将不能从窗口消息里面收到 WM_Pointer 或 WM_Touch 消息"）。
- **多个 RTS 实例的 Enable 互斥**：自己再开一个会和已有 RTS（包括 WPF 底层自带的那套）**互相打架**，
  典型症状是"窗口失焦回来丢触摸 / WPF 收不到 Touch 事件"。
- `SetAllTabletsMode(useMouseForInput)` 可让鼠标也进 RTS，但**触摸与鼠标互斥**——收到触摸就收不到鼠标，
  和 `EnableMouseInPointer` 那种"鼠标也走统一指针"的模型**方向相反**。
- 同步插件、异步插件、包描述（`GetPacketDescriptionData` 想拿压力/倾角要自己声明）……
  接入是"一整套机床"，不是一行开关。

### 2.4 生态现状

- 微软方向：`WM_POINTER`（Win8 起）＝鼠标/触摸/笔统一的现代输入栈；
  RTS 页面没有写"deprecated"，但属于 Tablet PC 时代的兼容栈（`wisptis.exe` 在 Win10/11 仍常驻，
  主要是给旧程序兜底）。
- WPF 自己都在试"搬去 pointer"：`Switch.System.Windows.Input.Stylus.EnablePointerSupport=true`
  （4.7/Win10 1703+）——而 dotnet/wpf 维护者的原话大概是："**这个开关因为底层的技术问题从未打磨完成**，
  主要用来绕过 WISP 栈的卡点"；另有 Surface Pro 9 全系崩溃、`IsPressAndHoldEnabled` 失效等 open issue。
- 社区：SDL 2024 年的讨论里，维护者直接说 "There is also the RealTimeStylus class in RTSCom.dll…
  **Seems like a mess. There should be no reason to touch those** when the Windows Ink events give
  us what we need."；绘图圈（Krita/Clip Studio 等）的合流方向也是"Windows Ink（WM_POINTER）"。

---

## 三、我们现在的输入处理（全景）

**形态**：多屏、每屏一个无边框置顶透明覆盖层（`WS_EX_NOREDIRECTIONBITMAP` + DComp），支持**穿透**
（透明屏）；窗口是 `WS_EX_NOACTIVATE`，点击/笔不夺焦点。

| 环节 | 做法 | 关键数字/位置 |
|---|---|---|
| 采样主通道 | **`WM_POINTER`** 直收；`EnableMouseInPointer` 让鼠标同一路进；`GetPointerPenInfoHistory` **合并点全读**（倒序还原）；`penMask` 四位（压力/旋转/倾X/倾Y）全用；逐点自带 **QPC 时标** | `Input/PenInput.cs`；缓冲 64 条 |
| 包率增强 | **Raw Input（`WM_INPUT`）补点**（2026-07 实测定调、**默认开**）：指针消息只有 **60–79Hz**，raw 有 **154–194Hz**；只补采样点，不另起管线 | `Engine.cs` `HandleRawInput` |
| Wintab | 兼容通道，**默认关**（另有"某些板子开 Wintab 会把 WM_POINTER 顶掉"的实测记录） | `--wintab` 可开 |
| 湿墨（正在写的这一笔） | **自绘**（合成边界配速，1.0 帧上屏）。DWM 委托墨迹轨迹**已接**（只对真笔喂点）——但 **2026-10-05 实测：用户手写板上系统不渲染**，属备用通道 | `延时-实测与优化.md` §八；`--syswet` 勿用 |
| 预测 | 自写一阶/二阶外推（10ms、只推位置），**默认关**（用户口径："开着末端会跳、手感分不出"） | `Prediction/InkPredictor.cs` |
| 触摸手势 | 自研 `TouchGestures`（双指漫游/三指擦/长按选择/两指点选…），建立在触点/指针之上；总开关已撤、常开 | `Touch.cs` 家族 |
| 光标/悬停 | 自研状态机（设备 × 工具 × 悬停目标）；笔的悬停走 `WM_POINTERUPDATE` 的 `InContact` 判定 | `光标设计调研.md` |
| 端到端延时 | Present(1) → 合成边界配速改造后：**Present→上屏 16.3ms（1.0 帧）**；**输入→Present 仅 2.2ms**；自绘一帧 1.8ms；端到端 **≈26ms @60Hz**；**地板 = 刷新周期 16.7ms**；其中"消息排队→我们拿到" **0.05ms** | `延时-实测与优化.md` |

**要点**：整套东西是"现代两条通路"（WM_POINTER 采样 + 湿墨自绘配速；DWM 委托墨迹已接、本机未渲染），
且**每一段的账都量过**。这不是"还没接 RTS 的半成品"，而是刻意选的形状。

---

## 四、逐项对照：换 RTS 会得到什么、失去什么

| 维度 | 我们（现状） | 换成 RTS | 判定 |
|---|---|---|---|
| 包率（采样密度） | 指针 60–79Hz + raw 补点 **154–194Hz** | RTS 直读设备包（同为设备包率档位） | **打平**（我们没有"包率损失"给它救） |
| 消息队列段延迟 | 实测 **0.05ms**（采样→我们拿到） | 绕队列直读（省的就是这一段） | **≈0 收益** |
| 端到端地板 | 16.7ms@60Hz（刷新周期） | 同样要过 DWM 合成/扫描输出 | **打平**（谁也改不了合成器） |
| 湿墨低延迟 | 自绘湿墨 + 合成边界配速（1.0 帧上屏；委托轨迹已接、用户硬件上系统不渲染） | WPF `DynamicRenderer`（独立动态渲染线程；WPF 专属） | **打平**（我们上屏 1.0 帧；RTS 的 DR 非 WPF 用不到） |
| 覆盖层/穿透形态 | 全屏置顶透明 + 穿透 + 多屏 | RTS 挂窗口、**不能跨进程**；穿透下窗口不是输入目标 | **RTS 输**（形态不匹配） |
| 触摸/鼠标 | 统一走 `WM_POINTER`（含鼠标） | **启用后 WM_POINTER/WM_TOUCH 全没**；鼠标与触摸互斥 | **RTS 输**（整条触摸手势/光标要重做） |
| 多屏多窗口 | 每屏一层，各自独立 | 多 RTS 实例 Enable 互斥（与 WPF/别家互相打架） | **RTS 输**（学校多屏场景踩雷） |
| 工程成本 | 已稳定；AOT 干净 | 手写 `IRealTimeStylus` 全套 COM vtable（AOT 下）+ 包描述解析 + 插件线程纪律 | **RTS 输**（大改 + 回归风险） |
| 生态方向 | 与微软现代栈同向 | 与微软/WPF 迁移方向**相反** | **RTS 输** |

---

## 五、如果真要换，会发生什么（损失清单）

1. **`WM_POINTER`/`WM_TOUCH` 断供**：触摸手势（双指/三指/长按）、悬停、光标状态机、穿透里的输入判定
   全部要按 RTS 插件模型重写——这些恰是这两年真机一堆细节打磨出来的地方（色带/手势/光标三套调研文档的成果）。
2. **统一指针模型散架**：鼠标在 RTS 里是"另一个开关、且和触摸互斥"，`EnableMouseInPointer` 那套作废；
   "笔/手指/鼠标同一路进"的简化不复存在。
3. **多屏/多窗口互斥坑**：多个 RTS Enable 互斥（连 WPF 自带的都会打），多屏各挂一个的局面很脆。
4. **学校三环境的雷区**：手写板驱动、WPS ink、学校触摸屏——RTS/Wintab/WISP 之间的驱动级互斥
   历史包袱太重（我们已经在 Wintab 文档里记录过"开 Wintab 顶掉 WM_POINTER"这类案例）。
5. **AOT 成本**：`IRealTimeStylus` 是自定义 COM 接口（非 IDispatch），要在 AOT 下用原始 vtable 手写
   全套方法（我们 `PptComLate.cs` 干过一次这种活，规模不小），且后续每个插件接口都要维护。
6. **唯一独有能力也用不上**：同步插件"在包被提升为指针消息之前过滤/修改/取消"——我们需要吗？
   需要"关系统手势"的地方（长按当右键等），我们已用 `WM_TABLET_QUERYSYSTEMGESTURESTATUS` 在窗口级关掉了，
   不需要为此换栈。

**净结果**：为理论上的"少一段 0.05ms"（占比 0.2%）+ 一个用不上的包过滤能力，交出整条已调稳的现代输入链路。

---

## 六、什么条件下才值得回头考虑

1. 出现**"`WM_POINTER` + Raw Input 双路都拿不到包"**的特定设备/驱动场景（先去查驱动设置/Wintab/贴桌面，
   RTS 是最后的备胎）——目前三类真机环境（手写板×2、学校触摸屏）都没有这种症状。
2. 需要**系统级"包过滤/取消"**才能实现的交互（比如在输入还未提升前就吞掉某类手势）——目前没有此类需求。
3. 未来若整体**换到 WPF/InkCanvas 栈**（那是另一个层面的架构抉择，见
   `调研-启动内存与WPF对比.md`：那条路内存 100–200MB 级、覆盖层形态也要重做）——那时 RTS 才有语境。

---

## 七、传闻核对："微软不建议用指针路写实时批注、建议用 RTS"？

> 2026-10-10 追加。这个"听说"**一半是真的，但语境是 WPF**——拆开看。

### 7.1 它的出处（为什么会有这个说法）

- **WPF 的触摸/笔底层默认就是 RTS**（`PenImc` → RealTimeStylus → `wisptis` 共享内存）。
  WPF 当年用它的原因（源码笔记口径）：一是 XP 时代的触摸支持，二是 **`InkCanvas` 需要高性能笔迹**。
- WPF 后来给了"改用 Pointer 消息"的开关（`Switch.System.Windows.Input.Stylus.EnablePointerSupport=true`），
  而中文 WPF 圈最常被引用的结论是：**"这个特性不支持实时的笔迹——因为笔迹需要运行在 UI 线程，
  会导致比较差的性能。"**（林德熙）
- 于是"**在 WPF 里写实时批注，别开 Pointer 开关，走 StylusPlugIn（RTS 那套）**"——**这层意思是对的**：
  WPF 高速书写的正统做法是在**触摸线程**拿点（`StylusPlugIn.OnStylusMove`），而不是在主线程等路由事件
  （UI 线程一忙就拖笔）。注意：在 WPF 里用 `InkCanvas`/触笔插件，**就等于在用 RTS**——微软把它包好了，
  这不是"建议你手接 RTS COM"。
- 网上流传的延时对比（林德熙 WPF Demo，触摸场景）：`WM_Touch` **12ms** / `WM_Pointer` **6ms** /
  **RealTimeStylus ≈4.6ms**——"RTS 最快"的说法就是从这类测试来的。作者自己也强调：
  "**快和慢是相对的**……受第三方干扰和主线程忙碌影响"，且那是**触摸**场景的 Demo 数据。

### 7.2 正解（这句话不能推广到我们）

1. **微软面向开发者的正规建议不是"手接 RTS"，而是"用平台的低延迟墨水通道"**：
   实时墨迹不能靠"在 UI 线程逐帧自己画"——WPF 世界的通道就是 **RTS 底**（StylusPlugIn + DynamicRenderer，
   触笔线程 + 动态渲染线程）；UWP/WinUI 世界的通道是 **InkPresenter** 的低延迟管线。
2. **桌面自绘墨迹（我们这个形态）的正规通道＝委托墨迹轨迹（`DelegatedInkTrailVisual`）**：
   由**系统合成器替应用画正在写的这一笔**（应用忙也照样出墨、不必每帧自绘）——和 RTS 的 DynamicRenderer
   **是同一个思想**（湿墨不走应用 UI 线程），只是换成合成层实现、面向所有桌面应用。
   **这条通道我们第一时间就接了**；但要诚实记一笔：**在用户手写板上系统并未真的画**
   （2026-10-05 真笔实测，见 `延时-实测与优化.md` §八）——那是合成器/设备层行为、**不是我们没接**。
   实况下我们的低延迟由**自绘 + 合成边界配速**兜住（1.0 帧上屏；120Hz 上 ~10ms），
   这条自绘路径与输入栈选型无关。
3. **"RTS 快 1.4ms"的账在我们链上早就被绕掉了**：
   - 它快在"点从 wisptis 到应用少一段消息路径 + 不在繁忙主线程排队"；
   - 我们：消息排队段实测 **0.05ms**；湿墨段已交 DWM（免主线程）；端到端 26ms 里 **16.7ms 是刷新地板**；
   - 而换 RTS 要拆掉：`WM_POINTER`/`WM_TOUCH` 全断供（触摸手势/光标/悬停/穿透重做）、多实例互斥、
     COM/AOT 成本（§四、§五已列全）。
4. **"WPF 的 Pointer 开关不支持实时笔迹"≠"WM_POINTER 写不了低延迟墨迹"**：
   那说的是 **WPF 没给 Pointer 栈做等价的动态渲染实现**（它的湿墨通道只建在 WISP/RTS 上）。
   对不用 WPF 框架、自己控合成与消息环的应用，指针消息 + 委托墨迹就是微软给的自绘墨迹正道——
   UWP/WinUI 的墨迹入口（InkPresenter）同样不是"UI 线程逐帧自绘"。

### 7.3 一句话口径

> "微软建议实时批注别在 UI 线程硬画、要用专用低延迟墨水通道"——**对，这代 Windows 给桌面应用的
> 那条通道（DWM 委托墨迹）我们已经接好**（本机是系统没画，不是我们没接；且实况自绘也贴到 1.0 帧上屏）；
> "所以要手接 RTS"——**不对**，那是 WPF 框架内部（InkCanvas/StylusPlugIn）的老实现；
> 对我们这种非 WPF 覆盖层，换它反而要放弃整条 `WM_POINTER` 输入栈——结论维持：**不换**。

---

## 八、出处（供核对）

- Learn：`RealTimeStylus class` / `IRealTimeStylus (rtscom.h)` / `Working with the RealTimeStylus Class` /
  `Plug-ins and the RealTimeStylus Class` / `StylusPlugIn Class` / `DynamicRenderer Class` /
  `Working with the StylusInput APIs`（Win32 Tablet 区）
- Learn：`MITIGATION: Pointer-based touch and stylus support`（WPF `EnablePointerSupport` 开关）
- dotnet/wpf：#7700（维护者："EnablePointerSupport was never fully fleshed out…"）、#5939（PressAndHold 失效）、
  #8435（Surface Pro 9 崩溃）
- 腾讯云开发者社区《WPF 从零自己实现从 RealTimeStylus 获取触摸信息》（RTS 数据通路 wisptis/共享内存、
  与 WM_TOUCH/WM_POINTER 互斥、"多 RTS Enable 互斥"的实测）
- SDL issue #11479（"Seems like a mess… no reason to touch those"）
- Wacom 开发者文档（Windows Ink 家族里把 RTS 与 WPF/Microsoft.Ink 并列）、
  helpdeskgeek/wisptis 说明（wisptis 在 Win10/11 仍常驻、用途与吐槽）
- 林德熙（lindexi）：《win10 支持默认把触摸提升 Pointer 消息》（**"这个特性不支持实时的笔迹……
  笔迹需要运行在 UI 线程"**）、《WPF 从零自己实现从 RealTimeStylus 获取触摸信息》（RTS 数据通路、
  与 WM_POINTER/WM_TOUCH 互斥、多实例互斥、延时 Demo：WM_Touch 12ms / WM_Pointer 6ms / RTS 4.6ms）、
  《WPF 底层 从手指触摸屏幕到笔迹在屏幕显示中间的步骤》（高速书写推荐 StylusPlugIn＝触摸线程取点）
- dotnet/wpf #3379、#5939（`EnablePointerSupport` 与 PressAndHold 冲突；官方/"Windows 的限制"口径）
- Learn：`Architecture of the StylusInput APIs`、`Dynamic-Renderer Plug-ins`、`Inking Controls (InkCanvas/InkPresenter)`；
  WinRT `DelegatedInkTrailVisual` + MSEdge Explainers《Web Ink Enhancement: Delegated Ink Trail》
  （系统合成器代画湿墨、绕开应用 UI 线程的同一思想）
- 本地实测与盘点：`延时-实测与优化.md`、`对标-微软墨迹栈与我们的架构.md`（§十"接没接 RTS"）、
  `优化记录/修复记录-Wintab压力-接入.md`、`Input/PenInput.cs`、`Engine.cs`

---

## 附录：RTS 的其它含义（防串线）

- **直播推流**（Real-Time Streaming / RTMP / RTC）：我们**没有**接任何推流/云 SDK；本软件是本地批注层，
  "把批注画面推给远端"是另一条产品线。——同 `对标-微软墨迹栈与我们的架构.md` §十。
- **Rust（编程语言）**：换语言的评估单独有一份：`调研-换语言评估-C++与Rust.md`（结论维持：不动）。
- 如果你听到的 "rts" 是某个**具体产品/芯片/引擎名**，说一声，我按那个名字单独查。
