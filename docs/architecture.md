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
- **当前产品代码最大文件（`ReadAllLines` 总行数，含空行）**：`Core/Selection.cs` 3,368、
  `Panels/FullUi.Settings.cs` 3,088、`Shapes/ShapeRecognize.cs` 2,922、`Ai/AiCard.cs` 2,757、
  `Text/TextBoxEditing.cs` 2,659、`Core/Engine.cs` 2,636。
  14 个产品文件在 2,000–3,368；其余 ≤2,000，多数 ≤600。
  同一功能域的代码在一个文件里，按 `section-map` 的 banner 读。
- **仅剩自检未拆**：`SelfTests/E2E/PanelAndKeys` 5,146、`ShapeLibrary` 3,889、
  `Diagnostics` 2,736。AI 改产品时不会读它们；按 ADR 保持现状。

### AOT 冒烟（2026-11-15）

命令：`publish.ps1 -Label AOTsmoke -NoZip -NoSetup -NoManifest`。
结果：**成功**；产物 `dist/InkTeach-AOTsmoke-win-x64/`，`InkTeach.exe` 18.15 MB，
整包 9 个文件 38.57 MB，GUI 子系统、pdfium 已带。

AOT 产物实测（不是 JIT）：

| 命令 | 结果 |
| --- | --- |
| `--unittest` | **5/5 PASS**（0 ms） |
| `--keytest` | PASS（键位表 / 冲突 / 落盘 / 真机注册 / 批注内按键） |
| `--startupcheck` | **5/5 PASS**（待命态穿透、小球、叫醒展开） |
| `--shapepaneltest` | **55/55 PASS**（图形面板 / 收藏 / 拖拽 / 落盘 / 绘制） |

发现并修复：`UnitTestRunner` 原来用 `Assembly.GetTypes()` + 反射找测试，AOT 裁剪后
一条都找不到（`--unittest` 报“没有匹配的单元测试”）。改成**显式注册表**
（`(name, Action)[]`），消除 IL2026/IL2070，AOT 下恢复 5/5。

已知限制（不影响产品）：
- `--settingsaudit` 用 `DispatchProxy.Create`，NativeAOT 不支持动态代理，AOT 下会卡住；
  该自检不要放进 AOT 冒烟。产品本身不依赖它。
- 其余 AOT 警告是既有 `JsonSerializer` 匿名类型（`OcrProviders.Save`、若干自检），
  本次拆分未引入新的产品级警告。

## ADR-004 索引分层：目录树负责结构，语义表只留跨文件流程（2026-11-15）

### 背景

用户提出：索引能不能就是文件夹目录本身？“章节名 = 文件夹名，节 = 子文件名”，
这样目录天然同步，不用维护一份单独的目录。

两份独立评审（A：方案设计；B：反方挑错）结论一致：

- 文件树能回答“大概在哪个目录、有哪些文件”，**不能**回答“用户说的这个功能先读哪个文件”：
  - “收藏拖拽排序”在 `Engine.Library.Input.cs`，排序在 `ShapeLibrary.cs::ReorderBefore`；
  - “截图”有三个 Capture：画（`Overlay.Capture`）、状态（`Engine.Capture`）、抓屏（`ScreenCapture`）；
  - “尺寸读数”只是 `Overlay.Capture.cs` 里的一段，文件名表达不了。
- 纯文件树约覆盖 **60%** 索引职责；剩下 40% 是功能别名、跨文件关系、文件内段落。
- 全量手工 `module-map`（88 条）维护成本高；纯树又会退回 grep。

### 决策

三层索引，各管一段：

| 层 | 文件 | 谁维护 | 内容 |
| --- | --- | --- | --- |
| 结构 | `docs/tree-map.md` | `tools/gen-tree-map.ps1` 自动生成 | 目录树 + 每个文件行数 + `// [职责]` 摘要 |
| 语义 | `docs/module-map.md` | 人工，**30 条 / ≤100 行** | 需求词 → 首读文件 → 下一跳 → 测试；只留跨文件流程和易混淆入口 |
| 行号 | `docs/section-map.md` | `tools/gen-section-map.ps1` 自动生成 | 大文件 banner → 行号 |

配套规则：

1. **文件头只给关键文件**（>600 行、易混淆、跨文件入口），格式两行：
   `// [职责] ...` / `// [测试] ...`。不强制 270 个文件都写。
2. **tree-map 只认 `// [职责]`**，不再猜文件里第一个 `<summary>`（那多半是某个字段/枚举的注释，
   会误导）。没写职责就只显示行数。
3. **三个校验脚本**，发版前一起跑：
   - `check-tree-map.ps1`：重新生成并对比，检测树是否过期；
   - `check-section-map.ps1`：逐文件对比“N 行”，检测行号是否过期；
   - `check-module-map.ps1`：语义表引用的文件是否存在。
4. **命名**：partial 文件用 `<主类型>.<功能>.cs`；避免 `Misc` / `More` / `Actions`
   这类不说明产品功能的尾词（`Layout` / `Render` / `Capture` 这类能说明职责的保留）。

### 备选与否决

| 方案 | 否决理由 |
| --- | --- |
| 纯文件树，不要语义表 | 用户需求词和代码名不总一致；跨文件关系、段内定位、测试入口都答不了 |
| 全量手工 `module-map`（88 条） | 维护成本高、路径变更易漂移；大量条目可由 tree-map 直接回答 |
| 所有 270 个文件强制 5 行头 | 把一张集中地图换成 270 处分散注释，检查成本更高；只给关键文件写 |
| Roslyn/MCP 自动符号图 | 新依赖/常驻进程；当前脚本 + grep 已够 |

### 影响与验证

- `module-map` 187 行/88 条 → **约 100 行/30 条**；`tree-map` 321 行自动生成；
  39 个关键文件补了 `// [职责]` 头。
- `--unittest` 5/5 通过；`check-tree-map` / `check-section-map` / `check-module-map` 均返回 0。
- 评分（两份独立评审）：手工 map 6/10，树 + 少量语义表 **8/10**。

## ADR-005 AI 修改成本：状态爆炸半径与追加式修改的收口（2026-11-15，提案）

> **状态：P0 + P1 已执行（2026-11-15）。** P0 = 护栏（check-all / state-map / 告警与空 catch 清零）；
> P1 = 自检分发改注册表（`App.Modes.*.cs`）。P2 仍按需，不排期。
> 本 ADR 只增补 ADR-002/003/004，不推翻任何一条；冲突处已在「与既有 ADR 的关系」写明。

### 背景（2026-11-15 实测）

规模与超标：

| 指标 | 现状 | ADR-002 口径 |
| --- | --- | --- |
| .cs / 行数 | 270 / 164,424 | — |
| 文件 >800 / >1,000 / >2,000 行 | 55 / 42 / **19** | 新拆文件 800–2,000 |
| 其中自检文件 >2,000 | 5 | 自检不设硬上限 |
| 未登记的超 2,000 行产品文件 | **12** | 超限需拆分或写明豁免 |
| 方法 >100 / >200 / >400 行 | 187 / 68 / 23 | 无 |
| 空 `catch {}` | 191（**171 处无注释**） | 无 |
| 编译告警 | 12 条（未使用字段/本地函数、CS8632） | 0 新增 |
| L1 单元测试 | **5 条** | 目标 <2 秒 |
| SelfTests 代码量 | 33,417 行（编进发布 exe） | 不拆 |

真正的问题不是行数，是**状态不可见**和**追加式修改**：

| partial 族 | 文件 | 行数 | 私有字段 | 被同族其他文件引用 | 比例 |
| --- | --- | --- | --- | --- | --- |
| `Engine` | 24 | 20,247 | 344 | **272** | **79%** |
| `FullUi` | 8 | 10,276 | 140 | 113 | 81% |
| `Overlay` | 13 | 9,730 | 123 | 86 | 70% |
| `Model` | 8 | 8,646 | 64 | 4 | 6% |

`Engine._dirty` 被 **23 个文件**读写，`_windows` 被 15 个文件引用。`Model` 只有 6%，说明
"几何算法自洽、状态不外泄"是能做到的；`Engine` 的 79% 是反面样本。

变更集中在同一批文件（近 150 commit）：`App.cs` 62 次、`Engine.cs` 57、`FullUi.cs` 38、
`Overlay.cs` 35、`Selection.cs` 20。近 38 个 `src` commit 的改动文件数中位 **4**、均值 13.7
（被大重构拉高）。

追加式修改的典型：`App.RunModeDispatch` **1,935 行、197 个 mode（195 条 `if`/`else if` 分支）**；
`--help` 手写 130 条，**77 个 mode 不在 help 里**，另有 `--list-modes` 从未实现。
help 文案在基类与 `App` 里各写一份，已经开始漂移。

### 决策

**D1. 指标增加"状态爆炸半径"，不新增行数硬指标。**
沿续 ADR-002 的 Read Budget（单需求 ≤800 行、≤3 文件、banner 段 ≤300 行）；
新增：一个私有字段被多少个同族 partial 文件读写 = 该字段的爆炸半径。
改动排序用 `churn × 半径`，不用行数。按此排序，第一优先是 `App.cs`（62 次 × 197 分支），
不是 `Engine.cs`（57 次 × 24 文件，但拆分风险最高）。

**D2. 消灭"追加式修改"：自检分发改注册表。**
`RunModeDispatch` 的 197 个 `if` 分支改为按域的显式注册表：

- handler 写成 `SelfTests/` 下的静态类/静态方法，**不新增 App 的 partial**（见 D6）；
- 每个域一个 `static (string Mode, string Help, Func<App, string[], int> Run)[]` 数组，
  集中在 `SelfTestCatalog.cs` 拼接；
- `--help` 与新增 `--list-modes`（输出 mode → 文件 → 验证命令）从注册表生成，不再手写；
- AOT 安全：照抄 `UnitTestRunner` 已验过的显式数组，禁止 `Assembly.GetTypes()`/反射；
- **序言保全**：`App.cs:144–209` 的临时目录隔离（`InkSettings.PathOverride` / `Recovery` /
  `PptStore` / `DocStore` / `BoardHistory` / `DiagLog`）必须保持原序逐行不动——2026-10-01
  PptStore 污染事故就是顺序问题。重构 commit 内必须重跑 `--unittest` + `--keytest` +
  `--startupcheck`。

**D3. 只做增量收口，存量不追溯。**
新代码不得给 `Engine` / `FullUi` / `Overlay` 新增私有字段或 partial 文件；需要新状态时
新建组合对象（`internal` 字段原样搬迁，不改可见性、不改字段名）。存量 24 个 Engine
partial 冻结，不主动重写。

**D4. 可见性优先于重构：状态地图是"拆类的裁判"，不是替代品。**
新增 `tools/gen-state-map.ps1` → `docs/state-map.md`：只列"被 ≥2 个同族文件引用"的字段，
按扇入 Top 20，总长 ≤400 行；每条给"读它的文件 / 写它的文件"。
它降低**定位成本**，不降低**验证成本**（`_dirty` 仍要改 23 处）。
因此拆分触发条件写死：**同一字段组出现 2 次以上真实 bug，或单次改动跨 >5 文件**，
才允许做组合提取；否则只更新地图。

**D5. 规范变成可执行检查，用"基线白名单"而不是 diff 解析。**
- 根 `.editorconfig` + `Directory.Build.props`：现有 12 条告警清零，之后禁止新增
  （`TreatWarningsAsErrors` 先只对新增项目/新文件开，避免存量红海）。
- `tools/check-all.ps1` 一条命令：build + 4 个 check + 结构检查，输出 ≤20 行；
  `publish.ps1` 前置调用。
- `--archetest`（或并入 `--unittest`）：文件 >800 行、方法 >120 行、字段跨 >5 文件、
  空 catch 无注释、新文件无 `[职责]` → 对照 `tools/structure-baseline.json` 白名单；
  只报"新增违规"，存量 19 个文件/23 个方法显式登记。不做 git diff 解析（rename 会误判）。

**D6. partial 预算裁决。**
ADR-002 写"单类 partial ≤20"，`Engine` 已 24 个。本 ADR 明确：
存量 `Engine` 24 个冻结为既成事实；新增 partial 文件原则上禁止（含自检）。
**施工例外（P1 实测）：** 自检 handler 要读 `App` 的私有状态（`_benchPressure` / `_autoExitAt` 等），
只能挂 `App` partial，不能放 `SelfTests/` 的静态类。P1 新增 6 个模式表文件
（`App.Modes.{Dev,Doc,Ink,Shape,Sys,Ui}.cs`）+ 1 个总表 `App.Modes.Catalog.cs`，
`App` 族 21 → 28，**冻结于此**；其余族（Model 8 / FullUi 8 / Overlay 13 / Engine 24）不动。
模式表文件里的"一个方法装 N 个 lambda"是数据表，不按逻辑方法计入方法长度基线。

### 与既有 ADR 的关系

- ADR-002「不提取 `InputController` / `RenderPipeline` 服务类」**继续有效**：D3 只禁止新增，
  D4 只在触发条件下允许小步组合，不做大重构。
- ADR-002「不引 Roslyn/MCP/tree-sitter」**继续有效**：状态地图用正则+现有脚本，不加依赖。
- ADR-002「Read Budget ≤800 行/≤3 文件」**继续有效**，D1 增补状态维度。
- ADR-003「AOT 等价」**继续有效**：D2 的注册表必须是显式数组。
- ADR-004「三层索引」**继续有效**：`state-map` 是第四张表（状态），不替代前三张。

### 不做什么

| 不做 | 理由 |
| --- | --- |
| Roslyn / MCP / tree-sitter 符号图 | ADR-002 已否决；`grep` + section-map + state-map 够用 |
| 每模块 `Ports.cs` / `_feature.md` | 三个评审一致反对：没有第二个实现时只是多一个文件要读；`module-map` 已覆盖 |
| 条件编译把自检剔出发布 exe | AOT 冒烟实测要跑 `--unittest`/`--keytest`；剔除会破坏现有验证链 |
| Engine/FullUi 大重构、按行数拆 | ADR-002 已否决；D4 的触发条件未满足前不做 |
| 拆 `SelfTests/` | ADR-002 已否决；自检按需读 50 行，不整文件通读 |
| 改 `App : InkEngine` 继承、字段可见性 | 会破坏自检读内部状态；D3 明确禁止 |
| 新增 DI / 事件总线 / 新项目 | 硬规则第 10 条；对按需读取无帮助 |

### 分期

| 阶段 | 内容 | 验证 | 回滚 |
| --- | --- | --- | --- |
| P0 护栏（1–2 天） | `.editorconfig` + `Directory.Build.props`；`check-all.ps1`；`gen-state-map.ps1` + `state-map.md`；171 处空 catch 各补一行原因；根 `AGENTS.md` ≤120 行 | build 0 error、0 新增 warning；4 个 check 全过；`check-all` ≤60 秒 | 纯新增文件 + 注释，`git revert` |
| P1 注册表（3–5 天） | `SelfTestCatalog` + 197 个 handler 迁移；`--list-modes`；help 自动生成；`--archetest` 基线 | `--unittest` / `--keytest` / `--startupcheck` 三条全过；`--help` 行数 ≥ 迁移前 | 单个 commit，纯剪切 |
| P2 按需（不做计划） | 新功能的组合化；state-map 触发的小步 seam；纯逻辑补 L1 | 各自 L2 | 每步独立 commit |

### 验收标准

- `App.cs` ≤400 行（现 2,277）；新增一条自检的 diff ≤2 个文件（现：大 if 链 + 两处 help）。
- 新增文件 0 告警、0 无注释空 `catch`、0 新 partial；`check-all` 全绿且 ≤60 秒。
- `state-map` 覆盖 5 个 partial 族、≤400 行；人工抽检 10 条字段→文件映射正确。
- 回放最近 10 个需求：经 `module-map` + `section-map` + `state-map` 定位后，
  单次读取 ≤3 个文件、≤800 行（人工记录在 `reports/`）。

### 三模型对照（2026-11-15）

同一份草案分别交给 Claude Sonnet 5.5、Grok 4.7、GLM-5.3-Flash 只读评审，结论一致为
**GO-WITH-CHANGES**，但侧重点不同：

| 模型 | 采纳 | 否决/降级 |
| --- | --- | --- |
| Sonnet 5.5 | 先抽 `DirtyTracker` 做 seam；注册表；空 catch 改 `Swallow(ex, tag)`；门槛分新旧 | 自检条件编译（与 AOT 冒烟冲突）；`Ports.cs`/`_feature.md`（仪式） |
| Grok 4.7 | AOT 显式注册表；`App : InkEngine` 继承不可动；不追溯废止 partial；索引不替代三层 | 追溯废止 ADR-002 的 partial 决策；把 `symbols.json` 当新索引层 |
| GLM-5.3-Flash | 序言保全条款；`state-map` 是拆类的裁判；state-map 限 ≤400 行；archetest 降级 | diff 级 archetest（rename 误判）；`Swallow()` 横扫 191 处 |

共识（三条评审都提到）：**按状态所有权而不是行数**、**只做增量不追溯**、**注册表必须 AOT 显式**。
分歧只有一处：`Engine` 要不要现在拆。本 ADR 取"不拆"，理由是 ADR-002 已评估过风险收益，
且当前没有"2 次真实 bug / 跨 >5 文件"的证据；先用 `state-map` 把风险量化。

### 风险

- **注册表迁移 1,935 行**是本 ADR 最大动作：必须纯剪切、序言不动、三条自检全过；
  任何一条不过就整 commit 回滚，不夹带修复。
- **state-map 正则误判**（字段名出现在字符串/注释里）：只输出 ≥2 文件引用项，人工抽检 10 条；
  误报不影响构建，只影响阅读。
- **拆类诱惑**：D4 的触发条件必须写进 `dev-standard.md`，否则下个会话又会以"文件太大"为由开拆。

### 执行结果（2026-11-15）

| 项 | 结果 |
| --- | --- |
| P0 告警 | 12 条 → **0 条**；`tools/check-warnings.ps1` 基线为空，新增即 fail |
| P0 空 catch | 188 处补了同行原因（多行形式不增行号），未注释数 **0** |
| P0 state-map | `docs/state-map.md` 93 行 / 73 个跨文件字段；Engine 272 个字段入表 |
| P0 门禁 | `tools/check-all.ps1`：build + tree/section/module/headers + structure，**13–15 秒**，全绿 |
| P1 分发 | 191 个 `if (mode == ...)` → 6 个域表 + 1 个总表；`App.cs` 2,277 → **315 行** |
| P1 保真 | 191 个分支体与改造前**逐字节一致**（归一化空白后 0 diff）；新增 `--list-modes` |
| P1 帮助 | `--help` 的模式表由注册表生成（192 条 + 22 个开关），不再手写 |
| P1 自检 | `--unittest` 5/5、`--keytest` PASS、`--startupcheck` 5/5、`--selftest 2` 正常退出 |
| 回滚 | P0 一个 commit；P1 一个 commit；均可单独 `git revert` |

未做：P2（新功能组合化、state-map 触发的 seam、纯逻辑补 L1）。触发条件未满足前不动存量。

### AOT 冒烟（2026-11-15，P1 后）

命令：`publish.ps1 -Label AOTsmoke -NoZip -NoSetup -NoManifest`。结果：**成功**；
`dist/InkTeach-AOTsmoke-win-x64/`，exe 18.19 MB，整包 34.37 MB / 6 文件，GUI 子系统、pdfium 已带。

| 命令 | 结果 |
| --- | --- |
| `--unittest` | **5/5 PASS**（0 ms） |
| `--keytest` | PASS（键位表 / 冲突 / 落盘 / 真机注册 / 批注内按键） |
| `--startupcheck` | **5/5 PASS** |
| `--shapepaneltest` | **37/37 PASS** |
| `--help` | 284 行；与 JIT 仅差 session id / 设备 id |
| `--list-modes` | 193 条（191 个模式 + `--list-modes` + `--unittest`） |
| `--paneltest` | **SKIP**：锁屏下合成输入不可用（不是失败） |

视觉取证（机器锁屏，拿不到真屏录像）：

- AOT 与 JIT 同参数出图逐像素对比：`--panelshow` 1618×260 **0/1,682,720**、
  `--libraryshow` 1584×644 **0/4,080,384**、`--captureshow` 1048×1128 **0/4,728,576**、
  `--ballprobe` 184×184 **0/135,424**，max-delta 全 0。
- `--board` 的 PrintWindow 只能拿到白板层（flag=0 全黑、flag=2 白底 + 1/1），
  UI 层抓不到是 DirectComposition 的限制；`--ballprobe` 证明小球层正常渲染。
- 结论：**AOT 渲染与 JIT 一致，判定 PASS**。遗留一条产品 backlog（非 AOT 回归）：
  收藏面板 8 列，超出 AGENTS §2 的 3–6 列上限；JIT 旧图同样 8 列。

AOT 分析告警（IL2026/IL3050/IL2072、`Microsoft.DotNet.ILCompiler 10.0.12` 的 NU1903）
均为存量，不是本次注册表改造引入；注册表是显式数组，未新增反射。
