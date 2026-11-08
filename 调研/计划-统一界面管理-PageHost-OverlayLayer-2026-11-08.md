# 计划：统一界面管理（PageHost + TextInputService + OverlayLayerManager）

日期：2026-11-08　　状态：**计划，未开工**

> 前置：
> - `设计-统一界面-窗口-控件框架-2026-11-07.md`（大框架已拍板不做；只做薄管理层）
> - `方案-Avalonia-接入-2026-11-07.md`（Avalonia 只作备选，不整包接入）
> - `调研-统一界面-优秀参考与先进管理方式-2026-11-08.md`（Flutter/WPF/Avalonia/WinUI/Chromium/Slate/Unity/Qt/Web/TSF 参考与映射）
> - `调研-统一界面-补充学习对象-2026-11-08.md`（Dear ImGui/Excalidraw/tldraw/VS Code/JetBrains/AppKit/APG 等）
> - `计划-统一界面-适配补充调研-2026-11-08.md`（A0：Action 表/稳定 ID/FocusRing/Popup 栈）
> - 当前设置页已改成连续滚动，滚轮/双指/惯性已通（提交 `0c6ba51`）
>
> 目标：**以后新增界面只做“加一个 Page / 注册一个 OverlayLayer”，不再每加一个界面
> 就重新改输入优先级、页面高度、遮罩、返回/关闭、焦点、滚动。**

---

## 〇、结论先行

1. **不做通用 UI 框架，做三个薄管理器：**
   - `PageHost`：覆盖层里的页面（更多 / 设置 / 收藏 / 图形 / 帮助 / 日志 / AI 子页）；
   - `TextInputService`：**一个**原生 EDIT/RichEdit 宿主，页面共用；文字输入不再算第三类界面；
   - `OverlayLayerManager`：浮层（PPT 条 / 回放条 / 计时卡 / 点名卡 / 收藏浮层 / 操作条 / 提示）。
2. **覆盖层里最终只有两类用户可见界面：页面 + 浮层。**
   文字输入是页面里的服务；系统文件/颜色对话框仍用系统。
3. **真窗口另算一层**：`WindowManager`（白板 / PDF / 展台 / 笔记），
   不混进覆盖层；页面可以装进窗口，也可以留在覆盖层。
4. **先做 PageHost + 设置试点（2～3 周）**，再收文字输入，再迁更多/帮助/日志，
   最后做浮层管理器。**不要一次重写 Overlay。**
5. 工程总量（1 人粗估）：
   - PageHost + 设置试点：**2～3 周**；
   - TextInputService + AI 子页试点：**2～3 周**；
   - 更多/帮助/日志迁移：**1～2 周**；
   - OverlayLayerManager + PPT/回放/卡片/收藏：**3～4 周**；
   - 选择条/提示/取景等收尾：**2～3 周**（可选）。
   核心 **8～12 周**，全部 **10～15 周**；真窗口另立项目。

---

## 一、现状盘点（为什么现在每加一个界面都累）

### 1.1 代码分布

| 层/文件 | 行数 | 现在管什么 |
|---|---|---|
| `src/InkUi/FullUi.cs` | **8648** | 工具条/色带/呼出盘、更多（Home/Settings/Help/Log）、7 个设置分类、16 行、收藏/图形入口、AI 菜单/面板 |
| `src/InkUi/FullUi.Ai.cs` / `.AiMenu.cs` | 356 / 168 | AI 面板、AI 菜单 |
| `src/InkUi/HelpUi.cs` | 519 | 使用说明页 |
| `src/InkEngine/Overlay.cs` | **8694** | **87 个 Draw/Hit 方法**：PPT 条/菜单/页号面板、回放条、收藏/图形浮层、选择操作条/面板、提示、取景、HUD、光标、激光…… |
| `src/InkEngine/Engine.cs` | 2 万+ | 输入主循环、输入优先级、图库/浮层/卡片/PPT 条的命中编排 |
| `Ppt.cs` / `PptBar.cs` | 1558 / 174 | PPT 条/菜单/页号面板的几何、状态、拖动 |
| `Replay.cs` | 561 | 回放条 |
| `Classroom.cs` / `ClassroomDraw.cs` | 2099 / 713 | 计时/点名卡 |
| `LibraryLayout.cs` + Overlay/Engine | 488 + 大部分 | 收藏/图形浮层、分类抽屉、拖放 |
| `Selection.cs` + Overlay | 3059 | 选中框、操作条、颜色/层级/字体/导出面板 |
| `TextBoxEditing.cs` | 1949 | 画布文本框 + RichEdit 宿主 |
| `AiApiSettings.cs` | 479 | 独立 API 配置窗 |
| 其他原生输入 | 9 组 | 重命名 / 计时 / 点名 / PPT 页码 / 日志备注 / AI 提示词 / API 字段 / 文本框 |

### 1.2 三个结构性问题

1. **页面壳重复**：更多/设置/帮助/日志各自算标题栏、返回/关闭、遮罩、高度、滚动、命中、输入优先级；加一页就再写一遍。
2. **输入优先级散落**：`OnPointerDown/Move/Up` 里按“图库 → 回放条 → 界面 → 计时卡 → 点名卡 → PPT 条 → 穿透 → 画布”手写；加一个浮层要改多处，容易漏。
3. **文字宿主重复**：9 组各自创建/定位/销毁原生窗；IME、光标、提交、DPI、焦点各写一套。

### 1.3 当前输入优先级（事实）

```text
图库面板 → 回放条 → 界面（工具条/更多/设置）→ 计时卡 → 点名卡
→ PPT 条 → 穿透放行 → 画布
```

这条顺序现在写在 `Engine.OnPointerDown/Move/Up` 的多个分支里，没有一份“可查询的注册表”。

---

## 二、目标架构

```text
InkEngine / InkUi
├─ WindowManager（另立项目）
│    ├─ 白板窗口
│    ├─ PDF 窗口
│    ├─ 视频展台
│    └─ 笔记窗口
│         └─ PageHost（页面可以装进窗口）
│
└─ 覆盖层（现有 Overlay）
     ├─ PageHost（页面）
     │    ├─ 更多 / 设置 / 收藏 / 图形 / 帮助 / 日志 / AI 子页
     │    └─ TextInputService（原生 EDIT/RichEdit 宿主）
     └─ OverlayLayerManager（浮层）
          ├─ PPT 条 / 页号面板 / 菜单
          ├─ 回放条
          ├─ 计时卡 / 点名卡
          ├─ 收藏浮层 / 图形浮层
          ├─ 选择操作条 / 颜色/层级/字体/导出面板
          ├─ 提示 / 悬停卡
          └─ 取景 / 截图 HUD（特殊层）
```

### 2.1 职责边界

| 组件 | 管什么 | 不管什么 |
|---|---|---|
| `WindowManager` | 真窗口、标题栏、最小化/最大化/全屏、任务栏、位置记忆 | 页面内容、画布墨迹 |
| `PageHost` | 页面栈、标题栏、返回/关闭、遮罩、页面高度、滚动容器、焦点、页面转场 | 窗口边框、浮层 z-order |
| `TextInputService` | 一个原生输入宿主、定位、IME、光标/选区、提交/取消、滚动跟随 | 页面布局、业务校验（回调给页面） |
| `OverlayLayerManager` | 浮层注册、z-order、输入优先级、显示/隐藏、拖动、位置记忆 | 页面内容、画布渲染 |
| 画布/墨迹 | 笔迹、湿墨、分块、选择框手柄、光标、激光 | 页面/浮层管理 |

---

## 三、PageHost（先做）

### 3.1 页面契约

```text
IPage
  Id / Title
  PreferredHeight?        // 页面想要的高度；宿主夹进工作区
  CanBack                 // 是否显示返回
  OnEnter() / OnExit()
  Layout(PageFrame frame)
  Render(ctx, PageFrame frame)
  HitTest(x, y) -> PageHit
  PointerDown/Move/Up(e) -> bool
  Wheel(x, y, delta) -> bool
  IsAnimating -> bool
  FocusManager?           // 可选：表单页
```

`PageFrame` 提供：

- `HeaderRect`、`ContentRect`、`FooterRect`；
- `ScrollViewport`（内容裁剪/滚动）；
- `CloseRect`、`BackRect`；
- `PopupLayer`；
- `DpiScale`、`NowMs`、`Theme`。

### 3.2 PageHost 负责

- **页面栈**：`Push/Pop/Replace`；一次只显示一个；
- **标题栏**：返回 / 标题 / 关闭；
- **遮罩**：整屏半透明，点外关闭（可配置）；
- **高度**：页面给 `PreferredHeight`，宿主夹进工作区（默认不再顶天立地）；
- **滚动**：`ScrollViewport` 统一滚轮/双指/惯性（复用刚做好的 `UiTouchFling`）；
- **焦点**：Esc/Enter/Tab、点击外部、弹层焦点陷阱；
- **转场**：高度/透明度 150～200ms；系统关动画时跳终态；
- **自检**：页面截图、命中、滚动、关闭/返回。

### 3.3 第一批页面

| 页面 | 来源 | 迁移方式 |
|---|---|---|
| `SettingsPage` | 当前 `DrawMoreSettings` + 设置分类/行 | **试点**：原样包进 Page，不改业务 |
| `MoreHomePage` | `DrawMoreHome` + Hub 格子 | 第二 |
| `HelpPage` | `DrawHelp` / `HelpUi` | 第三 |
| `LogPage` | `DrawLogPage` | 第四 |
| `ApiSettingsPage` | 新页，替代 `AiApiSettings` 独立窗 | 文字输入试点后 |

> 迁移期用 `LegacyPage` 适配器：把现有 `DrawMoreXxx` / `MoreHitAt` 包一层；
> 不改绘制逻辑，只把“壳”交给 PageHost。页面内部以后再逐步拆小。

### 3.4 页面尺寸规则

- 宽：`min(55% 工作宽, 980)`（沿用当前）；
- 高：`min(页面 PreferredHeight, 工作高 - 24)`；
- 设置页正文目标 **470～480**（当前 560，偏高）；
- 行高 44（当前 48）；组头 24（当前 26）；
- 页面内边距 20；滚动条在右缘留白内；
- 左栏（设置分类）固定在内容区，不跟页面滚；分类多时自己滚/折叠成图标栏（后续）。

### 3.5 验收

- 设置页行为与现在一致：7 分类、16 行、连续滚动、双指惯性、滚轮、滚动条；
- `--settingsaudit` 从 18 项扩到页面级：页面打开/关闭/返回、页面高度、滚动、命中；
- 代码变化：`FullUi` 里的设置页绘制/命中/高度/滚动代码迁到 `SettingsPage`；
  `FullUi` 只保留“工具条 + PageHost + 浮层”装配；
- 不新增前台自检；用离屏渲染 + 隐藏窗口。

---

## 四、TextInputService（文字输入收口）

### 4.1 目标

- **一个**原生输入宿主，不再 9 组各建一套；
- 页面只画输入框；点击后服务把宿主定位到输入框上；
- 支持单行/多行/富文本；
- IME、光标、选区、复制粘贴、撤销、DPI、失焦提交；
- 页面滚动时跟随/隐藏；页面关闭时提交/取消；
- 仍然是原生 EDIT/RichEdit，不自己写输入法。

### 4.2 接口（计划）

```text
ITextInputService
  Begin(TextInputRequest req)
  UpdateRect(rect)
  Commit()
  Cancel()
  IsEditing
  EditingId

TextInputRequest
  Id / Rect / InitialText
  Mode: SingleLine | MultiLine | RichText
  MaxLength
  Validator(text) -> error?
  OnCommit(text) / OnCancel()
  OwnerSurface: Overlay | Window
```

### 4.3 宿主策略

- 覆盖层是 `WS_EX_NOACTIVATE`，文字输入仍需要一个**可激活的顶层 TOOLWINDOW**；
- 服务只维护一个（或每个窗口一个）宿主，复用它，不再每个功能建一个；
- 无边框、无标题、TOPMOST、无任务栏；位置跟随输入框；
- 背景：白板 = 板色；PPT/PDF = 柔和浅底（真透明暂不做，见 §4.5）；
- 输入框失焦/滚动出视口/页面关闭 → 提交或取消；
- 关闭宿主时把焦点还给页面/画布。

### 4.4 迁移顺序

1. **AI API 页**（3 个字段 + 模型下拉）——第一个页面内表单；
2. **日志备注**（多行）；
3. **重命名 / 计时 / 点名 / PPT 页码**（单行）；
4. **画布文本框**（富文本，最后接，保持现有 `TextBoxEditing` 逻辑）。

### 4.5 透明背景

- 原生 RichEdit **不能真透明**（现有代码注释已写明）；
- 第一版：无边框 + 板色/柔和浅底 + 内边距；PPT/PDF 不再一块生白；
- 真透明/只有光标：需要自绘文本 + TSF 或分层色键，**单独评估，不进本计划**。

### 4.6 验收

- 同一时间只有一个输入宿主；切换字段不闪、不丢字；
- 中文 IME（微软拼音/搜狗）、光标、选区、复制粘贴、撤销正常；
- 100/150/200% DPI、页面滚动、页面关闭、点击外部；
- 后台自检：宿主矩形跟随页面、提交/取消、无残留窗口。

---

## 五、OverlayLayerManager（浮层收口）

### 5.1 浮层契约

```text
IOverlayLayer
  Id
  Priority            // 越大越先吃输入
  Visible
  Bounds
  ConsumesInput
  HitTest(x, y) -> bool
  PointerDown/Move/Up(e) -> bool
  Wheel(x, y, delta) -> bool
  Draw(ctx)
  IsAnimating
  DragPolicy           // None / Move / Custom
  PersistKey           // 位置/尺寸记忆
```

### 5.2 Manager 负责

- 注册/注销；
- 排序（固定优先级表）；
- 显示/隐藏（穿透、全屏、页面打开时统一处理）；
- 输入路由：按优先级问，谁先返回 true 谁吃；
- 拖动会话：阈值、夹取、松开落盘；
- 位置记忆：统一 `PlacementStore`；
- 自检：每层可见性/命中/拖动/记忆。

### 5.3 优先级表（初稿）

| 层 | 优先级 | 说明 |
|---|---|---|
| 原生文字输入宿主 | 1000 | 编辑中最高 |
| 页面模态（PageHost） | 900 | 打开页面时盖住浮层 |
| 取景/截图特殊层 | 850 | 模态 |
| 收藏/图形浮层 | 800 | 当前最上常驻浮层 |
| 回放条 | 700 | 回放控制 |
| 计时卡 / 点名卡 | 600 | 运行型卡片 |
| PPT 条 / 页号面板 / 菜单 | 500 | 放映工具 |
| 选择操作条 / 面板 | 400 | 选中对象 |
| 提示 / 悬停卡 | 300 | 不吃输入 |
| HUD | 100 | 调试 |

> 具体数值可调，但**只在这一张表里调**，不再散落在 `OnPointerDown/Move/Up`。

### 5.4 迁移顺序

1. **PPT 条 / 页号面板 / 菜单**（现有 `PptBar` 几何可直接包）；
2. **回放条**；
3. **计时卡 / 点名卡**；
4. **收藏/图形浮层**（引擎侧绘制，先用适配器注册，不搬绘制）；
5. **选择操作条 / 颜色/层级/字体/导出面板**；
6. **提示 / 取景 / HUD**（特殊层，最后）。

### 5.5 验收

- 输入优先级只读表；新增浮层不改 `OnPointerDown/Move/Up`；
- PPT 条拖动/翻页/菜单/页号面板不回归；
- 回放条、计时/点名卡、收藏浮层不抢输入；
- 穿透/页面打开/全屏时统一隐藏/恢复；
- 后台自检：每层命中、拖动、位置记忆、可见性。

---

## 六、输入优先级（统一后）

```text
1. 原生文字输入宿主（编辑中）
2. PageHost 当前页面（模态）
3. 取景/截图（特殊模态）
4. OverlayLayer 按 Priority 从高到低
5. 工具条 / 色带（FullUi 自身）
6. 穿透放行
7. 画布 / 墨迹
```

规则：

- 页面打开时，浮层默认不接输入（但可以画在下面）；
- 文字输入宿主编辑中，页面/浮层都不抢；
- 浮层里谁 `ConsumesInput=true` 谁参与；提示/HUD 不参与；
- 穿透时页面/浮层按各自策略隐藏或保留（PPT 条保留，收藏让开，照现有规则）；
- 一次只允许一个拖动会话。

---

## 七、迁移映射（代码 → 新组件）

| 现有 | 新组件 | 迁移方式 |
|---|---|---|
| `MorePage.Home/Settings/Help/Log` | `PageHost` 的 4 个 Page | `LegacyPage` 先包，后拆 |
| `DrawMoreSettings` / `MoreRowRect` / `MoreHitAt` | `SettingsPage` | 试点原样搬 |
| `AiApiSettings` 独立窗 | `ApiSettingsPage` | 文字服务接好后替换 |
| `TextBoxEditing` | `TextInputService`（富文本模式） | 最后接，保留现有逻辑 |
| 9 组原生输入 | `TextInputService` | 先 3 组，再全量 |
| `PptBar` / `Ppt.cs` 条/菜单/面板 | `PptBarLayer` + 后续 PageBar 控件 | 几何先包，不改 |
| `ReplayBar` | `ReplayLayer` | 包 |
| `Classroom` 计时/点名卡 | `TimerLayer` / `RollLayer` | 包 |
| `LibraryPanel` / `LibraryLayout` | `LibraryLayer` | 引擎侧适配器 |
| `SelectionBar` / 面板 | `SelectionLayer` | 包 |
| `Tooltip` / HUD / 取景 | `TooltipLayer` / `HudLayer` / `CaptureLayer` | 特殊层 |
| 选中框手柄 / 光标 / 激光 | **不迁** | 留画布层 |

---

## 八、分阶段计划

### Phase 0：设计冻结（1～2 天）

- 确认本计划、页面/浮层清单、优先级表；
- 确认页面尺寸/行高（设置正文 470～480、行高 44）；
- 确认 TextInputService 边界（原生宿主，不写输入法）；
- 不动代码。

### Phase 1：PageHost + 设置试点（2～3 周）

> 适配补充：`计划-统一界面-适配补充调研-2026-11-08.md` 的 **A0（Action 表/稳定 ID/FocusRing/Popup 栈）已于 2026-11-08 落地**，`--settingsaudit` 25 项全绿；本阶段从 PageHost 骨架开始。

- `PageHost` / `Page` / `PageFrame` / `LegacyPage`；
- `SettingsPage` 包当前设置页（连续滚动/双指/滚轮/滚动条原样）；
- 页面高度 470～480、行高 44、组头 24；
- 关闭/返回/点外/Esc、焦点、页面转场；
- 自检：`--settingsaudit` 扩展 + 离屏截图 + 隐藏窗口；
- **停止点**：设置页行为一致、代码变短、无前台自检。

### Phase 2：TextInputService + AI 子页（2～3 周）

- 一个原生输入宿主（单行/多行/富文本接口）；
- 先接 3 组：AI API 字段、日志备注、重命名；
- `ApiSettingsPage` 替代独立 API 窗；
- 页面内输入框定位/滚动跟随/提交/取消；
- 自检：IME 手测 + 宿主跟踪 + 无残留；
- **停止点**：AI 配置完全在设置页内完成，不再弹独立窗。

### Phase 3：迁更多/帮助/日志（1～2 周）

- `MoreHomePage`、`HelpPage`、`LogPage`；
- 删除/收窄 `FullUi` 里的 MorePage 壳代码；
- 自检：四页打开/返回/关闭/滚动/命中；
- **停止点**：`FullUi` 只保留装配 + 工具条。

### Phase 4：OverlayLayerManager（3～4 周）

- 层注册/优先级/输入路由/拖动/记忆；
- 先接 PPT 条/页号面板/菜单；
- 再接回放条、计时/点名卡；
- 收藏/图形用引擎侧适配器；
- 自检：每层命中/拖动/记忆/穿透/页面打开；
- **停止点**：新增浮层只注册，不改输入主循环。

### Phase 5：选择条/提示/取景（2～3 周，可选）

- 选择操作条/颜色/层级/字体/导出面板；
- 提示/HUD/取景特殊层；
- 画布手柄/光标/激光**不动**；
- 自检全量相关项。

### Phase 6：WindowManager（另立项目）

- 白板/PDF/展台/笔记真窗口；
- PageHost 可装进窗口；
- 文档/视图拆分是多窗口的前置，不混在本计划。

---

## 九、代码落点（建议）

```text
src/InkUi/
  Framework/
    PageHost.cs
    Page.cs
    PageFrame.cs
    LegacyPage.cs
    PlacementStore.cs
    OverlayLayerManager.cs      // 或放 InkEngine，见下
    OverlayLayer.cs
  Pages/
    SettingsPage.cs
    MoreHomePage.cs
    HelpPage.cs
    LogPage.cs
    ApiSettingsPage.cs
  FullUi.cs                     // 只做装配：工具条 + PageHost + 浮层
src/InkEngine/
  TextInputHost.cs              // 原生输入宿主的实现（需要 Native/IME/焦点）
  OverlayLayerRegistry.cs       // 如果浮层包含引擎侧绘制，放这里
```

**边界：**

- PageHost/Page 在 `InkUi`，只依赖 `IOverlayUi`/`IUiHost`；
- TextInputService 的**宿主实现**在 `InkEngine`（需要 Win32/IME/焦点），
  通过 `IUiHost` 暴露给页面；页面只看到 `ITextInputService`；
- OverlayLayerManager 如果管理引擎侧绘制的层，放 `InkEngine`；
  UI 自己的层通过接口注册；**不要为了统一把 Overlay 的绘制全搬走**；
- 引擎契约（`IOverlayUi`）先不改；`FullUi` 内部装配新组件。

---

## 十、自检与验收

### 10.1 自检原则

- **无窗口/后台优先**：页面几何、命中、滚动、提交用 fake host + 离屏；
- 需要真窗口时用隐藏窗口，不抢前台；
- 每阶段一条专项，不超过 1 分钟；
- 全量自检只在发布前跑。

### 10.2 每阶段验收

| 阶段 | 自检 | 通过标准 |
|---|---|---|
| P1 | `--settingsaudit` + 页面截图 | 7 分类/16 行/滚动/惯性/高度；无回归 |
| P2 | `--textinputaudit` + IME 手测 | 一个宿主；中文/光标/选区/提交；无残留 |
| P3 | `--pageaudit` | 四页打开/返回/关闭/滚动/命中 |
| P4 | `--overlayaudit` | 每层优先级/命中/拖动/记忆/穿透 |
| P5 | 相关专项 | 选择条/提示/取景不回归 |

### 10.3 指标

- 页面：高度 ≤ 工作高 - 24；正文 470～480；行高 44；
- 输入：同一时间 1 个宿主；切换字段不丢字；滚动跟随；
- 浮层：输入优先级只读表；新增层 0 处主循环修改；
- 性能：页面打开/关闭 ≤200ms；滚动 60fps；空闲 0 帧；
- 内存：页面切换不持续增长；宿主关闭即释放。

---

## 十一、风险与缓解

| # | 风险 | 缓解 |
|---|---|---|
| 1 | PageHost 过度抽象，页面差异大 | 用组合（PageFrame + 内容），不用深继承；`LegacyPage` 先包 |
| 2 | 文字宿主 IME/焦点问题 | 仍用原生 EDIT/RichEdit；一个宿主；IME/DPI 手测 |
| 3 | 覆盖层 NOACTIVATE 下输入焦点 | 宿主用可激活 TOOLWINDOW；关闭后还焦点；不改成 child |
| 4 | 浮层迁移回归 | 先包几何/命中，不改绘制；逐层迁移；每层自检 |
| 5 | 输入优先级漏项 | 只读优先级表；`--overlayaudit` 逐层命中 |
| 6 | 引擎/UI 边界被打破 | PageHost 只走 `IUiHost`；TextInput 宿主在引擎；不直接碰 Doc |
| 7 | 页面转场/高度抖动 | 页面给 PreferredHeight；宿主夹取；系统关动画跳终态 |
| 8 | 迁移期双路径 | 功能开关/常量；旧路径保留到新路径通过自检 |
| 9 | 收藏/图形在引擎侧 | 先用适配器注册为 Layer，不强行搬 UI |
| 10 | 时间超支 | 每阶段有停止点；P1 不通过就停，不铺开 |

---

## 十二、不做

- 不做通用 UI 框架/XAML/绑定/设计器；
- 不自己写输入法/TSF/文本编辑器；
- 不重写 Overlay 的墨迹/画布/选择框/光标；
- 不把收藏/图形绘制从引擎搬到 UI（先用适配器）；
- 不在本计划里做白板/PDF/展台/笔记真窗口（另立项目）；
- 不追求 3 列设置；先单列紧凑，2 列只给短行、宽屏可开。

---

## 十三、下一步

**先做 Phase 1：PageHost + SettingsPage 试点**，约 2～3 周。
做完只看三件事：

1. 设置页行为与现在一致（18 项自检全绿）；
2. 设置页代码从 `FullUi` 迁出后，`FullUi` 明显变短；
3. 新增一个测试页（比如“关于”或 AI 子页占位）只写内容，不改输入优先级/遮罩/返回。

通过后再做 Phase 2（文字输入 + AI 子页）；不通过就停在 Phase 1，不继续铺。
