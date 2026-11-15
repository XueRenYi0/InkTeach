# InkTeach 架构决策记录（ADR）

## ADR-001 匿名设备统计 v2（2026-11-14）

### 背景

设备卡 v1 依赖首次启动的模态同意框，用户反馈打扰课堂；同时报表只能看台数和版本，
无法判断地区分布与「卡不卡」。需要在**不收集 IP / 省市 / 账号 / 板书**的前提下，
把「总台数、国家、卡顿」变成可长期统计的核心报表。

### 决策

1. **身份**：沿用首次启动生成的随机安装 ID（`Guid.NewGuid().ToString("N")`，存
   `settings.json` 的 `ui.log.id`）。只用于同一台机器的去重计数；不读硬件序列号、
   不读账号、不读 IP。更新程序不换 ID；重装 / 换机 / 换 Windows 用户 = 新 ID。
2. **数据模型**：设备卡仍是一台机器一个 JSON（`devices/<id>.json`，按天覆盖），
   v2 新增 `lang`（界面语言）、`region`（Windows 区域设置的国家代码，ISO 3166-1）、
   `tz`（时区偏移）。保留版本、OS、机型、CPU、内存、GPU、屏幕/DPI/刷新率、
   帧率/慢帧/最大帧/内存、触摸统计、首次/最近日期。**不写 IP、省/市/区**。
3. **同意模型**：`privacy.consent = granted | denied` + `privacy.ver = 2`，
   同时落盘 `privacy.json`（不受备份恢复影响）。
   - 默认开启；**欧盟 / 欧洲经济区 / 英国 / 瑞士默认关闭**（可在设置主动开启）。
   - 旧版显式 `denied` 升级后保持关闭；旧版 `granted` 保持开启。
   - 首次 / 升级后只显示一条 6 秒非阻塞 Toast，不再弹模态框。
4. **日志与设备统计解耦**：`LogUploadConsentGranted` 只认
   `privacy.logUpload == "granted"`，不再回退到设备统计同意。运行日志可能含
   路径 / 堆栈，必须第一次手动上传时单独确认。
5. **关闭语义**：设置 → 关于 →「匿名设备统计」关闭 = 写 `denied` + 停止上传 +
   异步删除本机 ID 对应的设备卡。

### 备选方案与否决理由

| 方案 | 否决理由 |
| --- | --- |
| 用 IP 反查国家 | 违反「不取 IP」目标；且网络层 IP 会被第三方仓库记录 |
| 用省/市定位做地区报表 | 需要 IP 或系统定位权限，超出设备统计范围 |
| 每次启动弹模态同意 | 打断课堂；用户已明确要求尽量不弹窗 |
| 设备统计开启即视为同意上传日志 | 日志含路径 / 堆栈，风险高于设备卡，必须单独确认 |
| 新建 `privacy.stats` 键 | 迁移面变大；复用 `privacy.consent` 即可兼容旧版 |

### 影响

- 改动文件：`Engine.cs`（同意 / 迁移 / 提示）、`DiagLog.cs`（设备卡字段、日志同意）、
  `FullUi.cs`（设置行）、`HelpUi.cs`（帮助卡）、9 种语言 `core.json`、
  `PRIVACY.md`、`docs/privacy.html`、`tools/device-stats.ps1`。
- 报表：`tools/device-stats.ps1` 增加国家 / 语言 / 慢帧 Top 统计；慢帧（`slow150`）
  是「卡不卡」的主指标，`fps` 在空闲时可能很低，仅作参考。
- 未做：省份 / 城市 / IP 定位；硬件指纹；跨设备账号体系。

### 验证

- `dotnet build` 通过；`--langaudit` 9 种语言 528 key、缺失 0、占位符 0；
  `--settingsaudit` 46 通过 / 0 失败。
- 迁移实测：旧 `privacy.json` 为 `ver=1 consent=denied` 时升级后仍为 `denied`；
  `granted` 升级后保持 `granted`。
- 设置页 10 种语言截图经视觉审核通过（无裁切 / 漏翻 / 开关异常）。

## ADR-002 面向 AI 按需读写的大文件解耦与索引（2026-11-15）

> 2026-11-15 修订：**目录结构与测试策略以 ADR-003 为准**；本 ADR 的索引先行、
> 纯剪切、静态初始化集中、逐文件 commit 规则继续有效。

### 背景

实测（`ReadAllLines` 真行数，排除 `bin/obj/.revert`）：238 个 `.cs`、175,547 行。

| 文件 | 行数 | 问题 |
| --- | --- | --- |
| `src/InkEngine/Engine.cs` | 21,685 | 一个 partial 类横跨输入 / 渲染 / 落盘 / 热键 / 收藏 / 取色板，600+ 方法，0 个 `#region` |
| `src/InkTeach/App.cs` | 10,340 | 产品启动 + 内存报告 + 合成输入 + 离屏探针 + `Program` 全在一个文件 |
| `src/InkEngine/Overlay.cs` | 9,782 | 内容层 / 界面层 / 帧 / 读数 / 呼出盘 / 截图 |
| `src/InkEngine/Model.cs` | 9,444 | 29 个顶层类型：`Stroke`(5.5k)、`InkDocument`(2k)、`EditAction` 家族(0.9k)… |
| `src/InkUi/FullUi.cs` | 9,452 | 布局 / 上带 / 色带 / 设置页 / 命中 / 输入 / 绘制 / 更多面板 |
| `SelfTests/` | 35,754（53 文件） | 占全部代码 20.4%；`PanelAndKeys` 6,374、`ShapeLibrary` 4,485 |

另有 2,229 处 `ForTest/TestOnly` 钩子散在产品文件里。文件分布不是整体失控：133 个文件 <300 行，
33 个 >1,000 行，其中 9 个 >3,000 行——问题是**少数巨石 + 没有导航索引**。

AI 的实际代价：改一个功能要先 `grep` 出几十处同名方法，再在 2 万行文件里定位；
`App.cs` 与 20% 的自检代码同名，命中噪音高；`docs/` 只有 ADR，没有"功能 → 文件"映射。

### 决策

四步走，顺序固定：**索引 → 多类型拆离 → 宿主拆离 → 巨石切 partial**。

1. **索引先行（双层）**：
   - `docs/module-map.md`（人工，≤150 行，**禁写行号**）：功能词 → 文件 → 段落锚点 →
     自检命令；只在新增/拆分/改名文件时同 commit 更新。
   - `docs/section-map.md`（脚本 `tools/gen-section-map.ps1` 生成）：大文件的顶层类型 +
     banner 标题 → 行号；行号漂移后重跑脚本，不手工维护。
   - `docs/dev-rules.md` 第 1 条改为"先查 module-map，再 grep，再读那一段"。
2. **多类型文件先拆**：`Model.cs` 按顶层类型拆成独立文件（零 partial 风险）：
   `EditActions.cs`、`InkDocument.cs`、`LaserTrail.cs`、`Model.Stroke.cs`。
3. **宿主拆离**：`App.cs` 只留启动与模式分发（≤1,500 行），开发工具拆
   `App.Dev.Render.cs`（离屏渲染）、`App.Dev.Metrics.cs`（内存/帧率报告）、
   `App.Dev.Input.cs`（合成输入）、`App.Dev.ClickTarget.cs`（第二进程点击目标）。
4. **巨石切 partial**：`Overlay.cs` / `FullUi.cs` / `Engine.cs` 各按内聚大块切 4–6 个
   partial；**不按每个小 banner 切碎**。
5. **测试分三层，先快后慢**：
   - **L1 纯逻辑快检** `--unittest <过滤词>`：在 `PrepareHostStartup`（建窗口之前）拦截，
     反射执行 `SelfTests/UnitTests.*.cs` 里带 `[UnitTest]` 的静态方法；复用宿主现成的
     临时目录隔离（`InkSettings.PathOverride` / `Recovery` / `PptStore` / `DocStore`），
     绝不碰用户真实板书与配置；零 NuGet 依赖，目标 <2 秒，可按方法名过滤单跑。
   - **L2 专项自检**：现有 `--keytest` / `--radialtest` 等，改哪个模块跑哪条。
   - **L3 全量**：`--paneltest` 等，只在发版前跑。
   - **不建 `tests/` 新项目、不引 xUnit / MTP**：新项目会绕过宿主的路径隔离，
     直接读写用户 `%LOCALAPPDATA%`；且本机外网受限，还原新包有失败风险。

### 边界规则（硬红线）

- **单文件硬上限 2,000 行**（对齐 `Read` 工具单次 2,000 行的物理上限），
  新拆出的功能文件目标 800–2,000 行；单类 partial 文件总数 ≤20 个。
- **反碎片化禁令**：不设"高/中/低频"行数分档——哪个模块高频会随阶段变；
  **已低于 2,000 行的文件严禁为了凑行数再拆**；同一个私有状态机
  （`TouchGestures`、`SelectDrag` 这类）不许拆到两个文件。
- **有效指标是 Read Budget，不是文件行数**：任意单点需求，经索引定位后
  单次会话 `Read` 总量 ≤800 行、跨文件 ≤3 个；文件内每个 banner 段落 ≤300 行。
- **纯剪切**：拆分 commit 只允许 剪切 + 粘贴 + 补 `using` + 文件头 5 行契约；
  禁止顺手改访问修饰符、字段名、初始化表达式、注释。
- **静态初始化集中**：有依赖关系的 `static readonly` / 静态构造函数必须留在主文件；
  C# 不保证跨 partial 文件的静态初始化顺序。
- **不动目录**：不建 `Core/Input/Render` 子目录。`tools/doc-audit.ps1` 按路径前缀对账，
  且扁平目录下 `Engine.Input.cs` 比嵌套路径更短、更好检索。
- **每个拆出的文件头固定 5 行**：`职责 / 入口 / 读写状态 / 落盘 Key / 相关自检`。
- **同一 commit 更新 `docs/module-map.md`**：新增或拆分文件必须同步索引。

### 备选方案与否决理由

| 方案 | 否决理由 |
| --- | --- |
| 建 `Core/Input/Render/Features/Ui` 子目录 | 破坏 `tools/doc-audit.ps1` 的路径对账；AI 用 glob/grep 找文件，扁平命名更短 |
| 单文件 500–1,200 行 | 会把 21.7k 行切成 25–35 个碎片；跨文件追状态的上下文成本反而更高 |
| 拆 `SelfTests/` 下 53 个文件 | 自检只在新增断言时按测试名定点读 50 行，从不整文件通读；拆它是无收益体力活 |
| 提取 `InputController` / `RenderPipeline` 等服务类 | 要改字段可见性、引入跨对象引用，直接威胁 `App : InkEngine` 的自检继承；当前收益不值 |
| 引入 DI / 事件总线 / 新项目 | 违反"不新增依赖、不引入新概念"；对按需读取没有帮助 |
| 新建 `tests/InkEngine.Tests` + xUnit v3 / MTP | 会绕过宿主现成的临时目录隔离，测试直接读写用户真实 `%LOCALAPPDATA%`；本机外网受限，新包还原有失败风险；违反 `dev-rules.md` 第 8 条 |
| 按高频/中频/低频分三档行数 | 哪个模块高频会随阶段变；为凑行数会把内聚逻辑硬拆碎。统一 2,000 硬上限 + Read Budget 更稳 |
| 引入 Roslyn / MCP / tree-sitter 做符号索引 | 新增依赖与常驻进程；当前 `grep` + `gen-section-map.ps1` 已覆盖同一需求 |

### 阶段计划

| 阶段 | 改动 | 验证 | 回滚 |
| --- | --- | --- | --- |
| P0 索引 | `docs/module-map.md`（无行号）；`tools/gen-section-map.ps1` + `docs/section-map.md`；`tools/check-module-map.ps1`；`dev-rules.md` 第 1 条；`AGENTS.md` 顶部入口 | 两个脚本跑通；`check-module-map` 无 MISSING | `git checkout -- docs tools AGENTS.md` |
| P1 安全网 + 样板 | ① `--unittest <filter>` 反射分发器 + `Check.True`（带 `CallerFilePath/LineNumber`）+ `--list-modes`；② `Model.cs` 拆 4 个文件；③ `App.cs` 拆 4 个 Dev partial | `dotnet build -c Release` + `--unittest Edit` + `--keytest` | 单独 commit，`git revert` |
| P2 UI 巨石 | `FullUi.cs` 拆 `FullUi.Settings.cs` / `FullUi.MorePanel.cs`；`Overlay.cs` 拆 `Overlay.Radial.cs` / `Overlay.Capture.cs` | `--paneltest` 的相关子项、`--radialtest` | 每文件一个 commit |
| P3 引擎巨石 | `Engine.cs` 切 `Engine.Input.cs` / `Engine.Persistence.cs` / `Engine.LibraryUi.cs` / `Engine.ColorPicker.cs` / `Engine.SelfCheckHooks.cs`，主文件压到 ~8k | 对应专项：`--dwelltest` / `--recoverytest` / `--shapelibrary` | 每文件一个 commit |

### 验收标准

- `dotnet build src/InkTeach/InkTeach.csproj -c Release`：0 error、0 新增 warning。
- 每阶段对应专项退出码 0；全量自检仍只在发版前跑。
- P3 完成后跑一次 `publish.ps1` 的 AOT 冒烟，确认 `doc-audit` 路径映射未断。
- AI 读取预算：常见功能改动经 `module-map.md` + `section-map.md` 定位后，
  单次 `Read` ≤3 个文件、总量 ≤800 行。

### 不做什么

- 不改命名空间、不拆新 csproj、不拆 `SelfTests/`（目录策略见 ADR-003）。
- 不建 `tests/` 测试项目、不引 xUnit / MTP / Roslyn / MCP。
- 不引入接口抽象、DI、事件总线、新状态机。
- 不在重构 commit 里顺手修 bug 或改行为。

## ADR-003 按功能域分目录 + 基线驱动重构（2026-11-15）

### 背景

ADR-002 选择“扁平目录 + 单文件 2,000 行上限”，理由是避免破坏 `tools/doc-audit.ps1`
的路径对账。用户 2026-11-15 明确否决：文件越来越大时，AI 仍要在一堆 `Engine.*.cs`
里按前缀猜模块；要求**按功能划分模块、文件按功能划分、建文件夹**，按 AI 规范化开发流程做。

同时发现两件事实：
1. 全部 238 个 `.cs` 都是扁平 namespace（`InkEngine` / `InkUi` / `InkTeach`），
   且大量 `partial class` 跨文件。**partial 的所有部分必须在同一 namespace**，
   所以“改 namespace 跟目录走”不可行，也没必要。
2. 仓库已有 `tools/bench/Run-FuncSuite.ps1`：46 条 E2E 用例、21 个功能分组、
   独立进程 + 超时 + `summary.json`。之前没把它当标准入口，才误以为“测试没清单”。

### 决策

1. **一级功能域目录，禁止深层嵌套**（`Features/Shapes/` 这种两层不要）。
2. **namespace 保持扁平**：只移动文件，`namespace InkEngine;` / `InkUi;` / `InkTeach;` 不变。
   编译器按语法树合并 partial，跨目录无影响；零 `using` 改动、AOT 字节级等价。
3. **Move 与 Split 分离**：同一 commit 只做 `git mv` + 改脚本路径；
   切分长文件必须是下一个 commit。否则 Git 识别不出 rename，出问题无法二分。
4. **文档先行**：`docs/module-map.md` 按目标目录组织；`docs/feature-specs.md`
   承接 AGENTS.md 的具体功能规则；`docs/test-playbook.md` 定义测试分层与基线对比。
5. **基线驱动**：重构前跑 `Run-FuncSuite.ps1 -OutDir reports/baseline-pre`；
   每搬完一个模块跑对应分组；全仓搬完跑 `baseline-post` + `compare-baseline.ps1`。

### 目标目录（InkEngine 12 个一级目录）

| 目录 | 放什么 |
| --- | --- |
| `Core/` | `Model.cs` 拆出的 Tool/Stroke/InkPiece/EditActions/InkDocument/LaserTrail；`Selection.cs`、`SpatialGrid.cs`、`InkHistory`、`BoardHistory`、`Recovery`、`BackgroundSaver`、`InkSerializer`、`InkFile`、`Settings`、`Engine.cs` 主文件 |
| `Input/` | `PenInput`、`PointerInput`、`InputPrecision`、`InputBlackBox`、`Prediction/`、`DwellAssist`、`Touch`、`TouchStats`；笔迹算法群 `InkModel`、`InkStrokeModeler`、`StrokeSmoothing`、`StrokeMotion`、`CurveFit`、`WpfInkFit`、`GoogleInkSlidingWindow`、`PressureSim` |
| `Rendering/` | `Overlay` 拆 Frame/Content/Ui/Capture/Radial/Readout；`CanvasTiles`、`SvgPath`、`GlyphFill`、`Cursors`；界面边界 `Ui`、`UiHost`、`OverlayLayer*`、`UiImageBytes` |
| `Shapes/` | `ShapeRecognize`、`PointCloudRecognizer`、`ShapeCatalog`、`ShapePreview`、`ShapeLibrary`、`LibraryLayout`、`CollectionFingerprint`、`FavoriteTransfer`、`Figure*`、`Plot*` |
| `Text/` | `TextBox*`、`TextStyles`、`TextInputService`、`WindowlessTextControl`、`MathRenderer`、`LatexCompat`、`GlyphText`、`GlyphInk`、`Handwriting*` |
| `Ai/` | `AiCard`、`AiPlotClient`、`AiQuestionClient`、`Ocr*`、`AiApi*` |
| `Doc/` | 现有 `Doc/*`（保持原名，不改 `Documents/`） |
| `Ppt/` | `Ppt*` |
| `Classroom/` | `Classroom`、`ClassroomDraw` |
| `Replay/` | `Replay` |
| `Export/` | `Export`、`Clipboard*`、`Image*`、`PhotoBeautify`、`ScreenCapture` |
| `Platform/` | `Native`、`KeyBindings`、`UiScale`、`AutoStart`、`Update*`、`Privacy`、`DiagLog`、`LogFeedback`、`Backup`、`Toast`、`Tooltip`、`Latency`、`GcLatency`、`EraserTelemetry`、`SelfTestCancel` |

`InkUi`：`Shell/`、`Panels/`、`Ai/`、`Framework/`、`Design/`。
`InkTeach`：`Host/`、`DevTools/`、`SelfTests/`（内分 `Unit/`、`E2E/`）。

### 备选方案与否决理由

| 方案 | 否决理由 |
| --- | --- |
| 跨项目 vertical slice（一个功能一个项目） | `InkEngine` 不能引用 `InkUi`，单向依赖契约会被破坏 |
| `Features/Shapes/` 两层目录 | 多一层无检索收益；现有 `Doc/` 还得改两次名 |
| namespace 跟目录走（`InkEngine.Shapes`） | partial 必须同 namespace；改 namespace 牵连全仓 using，收益为零 |
| 一次性全仓 `git mv` | 200 个文件混合拆分，Git 识别不出 rename，自检挂了无法二分 |
| 新建 xUnit 项目 / `--testreport` 侵入 | 绕过宿主临时目录隔离；已有 `Run-FuncSuite.ps1` + `summary.json`，够用 |

### 阶段计划

| 阶段 | 动作 | 验证 |
| --- | --- | --- |
| P0 文档 + 基线 | `module-map` 按目录重写；`feature-specs.md`；`test-playbook.md`；跑 `baseline-pre`（46 条） | `baseline-pre/summary.json` 46 PASS；`compare-baseline` 自比一致 |
| P1 逐模块移动 | 按 Core → Input → Rendering → Shapes → Text → Ai → Doc → Ppt → Classroom → Replay → Export → Platform 顺序，每模块一个 `git mv` commit + 改脚本路径 | 每模块 `dotnet build` + `--unittest` + `Run-FuncSuite -Only <分组>` 与基线一致 |
| P2 拆巨石 | 每模块的 `Engine.*` / `Overlay.*` / `FullUi.*` 切 partial（800–1,800 行） | 同上 + `gen-section-map.ps1` 重跑 |
| P3 收尾 | 全仓 `baseline-post`；`compare-baseline`；AOT 冒烟 | 0 新增 FAIL、PASS 数不减；`publish.ps1` 通过 |

### 验收标准

- 每模块移动后：`git status` 显示 `R100` 重命名（不是删除+新增）。
- `--unittest` 全绿；对应 `Run-FuncSuite` 分组红绿与基线一致。
- 全仓收尾：`compare-baseline.ps1 -Before baseline-pre -After baseline-post` 退出码 0。
- 不改 namespace、不改行为、不新增依赖。

### 不做什么

- 不拆 `SelfTests/` 的 53 个 E2E 文件；只在旁边加 `Unit/`。
- 不改 `InkEngine.csproj` 的项目引用；不加新项目。
- 不在移动文件的同时改逻辑；不顺手修 bug。
- 不引 Roslyn / MCP / 向量索引；目录 + `module-map` + `grep` 足够。

### 执行结果（2026-11-15）

- **P0 完成**：`module-map` / `feature-specs` / `test-playbook` 落地；`baseline-pre` 46/46 PASS。
- **P1 完成**：195 个文件按 12+5+3 个功能域 `git mv`，Git 全部识别为 `R100`；namespace 未改。
- **P2 完成（细化到功能块）**：
  - `Engine.Selection` 3,276 → 主 67 + `SelectionBar` 712（操作条九格：颜色/粗细/线型/取色器/菜单）
    + `Selection.Vertex` 1,033 + `Selection.Gesture` 1,443 + `Beautify` 144 + `Actions` 161。
  - `Engine.Library` 3,380 → 主 1,106 + `Library.Menu` 388 + `Library.Layout` 564 +
    `Library.Input` 994 + `Library.Misc` 536。
  - `Engine.Input` 3,220 → 主 1,948 + `Capture` 355 + `TouchGestures` 732 + `Clipboard` 490。
  - `Overlay.Readout` 3,201 → 主 1,477 + `SelectionBar` 1,041 + `PptBar` 630 + `Toast` 318。
  - `App.cs` 3,940 → 主 2,230 + `App.Dev.{Scroll,Ppt,Show,Cursor,ShapeShow}`（127–577）。
  - `App.Dev.ClickTarget` 4,292 → 主 78 + `App.Dev.{Sheets,PageShow,AiShow,TextShow,Showcase,
    Smooth,Geometry,Bench,Erase}`（65–1,019）。
- **行为等价**：`baseline-post2/3/4/5` 均 **46/46 PASS**；`compare-baseline` 退出码 0。
- **当前产品代码最大文件**：`Panels/FullUi.Settings.cs` 2,808、`Core/Selection.cs` 2,471、
  `Ai/AiCard.cs` 2,415、`Model.Stroke.Tangent` 2,364、`Core/Engine.cs` 2,357。
  全部 ≤2,808，多数 ≤1,200；同一功能域的代码在一个文件里，按 `section-map` 的 banner 读。
- **仅剩自检未拆**：`SelfTests/E2E/PanelAndKeys` 5,146、`ShapeLibrary` 3,889、
  `Diagnostics` 2,736。AI 改产品时不会读它们；按 ADR 保持现状。
