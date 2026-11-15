# AI 开发规范（硬性）

> 30 秒版：**先 `tree-map` 找文件 → 再 `module-map` 找首读/下一跳 → 再 `section-map`/grep 找段落**；
> 只读命中段 + 直接调用方 1 处，不通读整仓。改完跑一条相关测试，不跑全量。

## 1. 三层索引（谁维护）

| 层 | 文件 | 谁维护 | 用途 |
| --- | --- | --- | --- |
| 结构 | `docs/tree-map.md` | `tools/gen-tree-map.ps1` 自动 | 目录、文件、行数、`[职责]` |
| 语义 | `docs/module-map.md` | 人工，30 条左右 | 需求词 → 首读 → 下一跳 → 测试 |
| 行号 | `docs/section-map.md` | `tools/gen-section-map.ps1` 自动 | 大文件 banner → 行号 |

自动生成的地图**禁止手改**；人工只维护 `module-map` 的跨文件流程和易混淆入口。

## 2. 目录与命名

- 新代码放现有功能域：`InkEngine/{Core,Input,Rendering,Shapes,Text,Ai,Doc,Ppt,Classroom,Replay,Export,Platform}`、
  `InkUi/{Shell,Panels,Ai,Framework,Design}`、`InkTeach/{Host,DevTools,SelfTests/{Unit,E2E}}`。
- 不为同一功能新建平行目录、项目、依赖；没有匹配域时先停下说明边界。
- 文件用具体功能名：`<主类型>.<功能>.cs`（`Engine.SelectionBar.cs`、`Overlay.Capture.cs`）。
- **禁止**新文件用 `Misc`、`More`、`Common`、`Helpers`、`Utils`、`Part1`、`Actions` 这类兜底尾词
  （`SelectionBar.Actions` 这种“前缀已说明归属”的除外）。
- namespace 保持扁平；partial 跨目录不改 namespace。

## 3. 文件规模与拆分

- 手写产品文件目标 **800–1,800 行**，硬上限 **2,000 行**。
- 超 2,000 行：下一次触及该文件时必须按职责拆，或在 commit 里写明豁免原因和下一步。
- **拆分以职责边界为准，不为凑行数拆**；同一个私有状态机、单一算法、紧耦合序列化模型不许切散。
- 自检文件不设硬上限；>1,500 行按独立测试主题拆（`PanelAndKeys` 是已知例外）。
- 已知例外（下一次触及必须处理）：`Core/Selection.cs` 3,368、`Panels/FullUi.Settings.cs` 3,088。

## 4. 文件头

超过 600 行、`module-map` 入口、易混淆文件，开头必须有：

```csharp
// [职责] 一句话：这个文件负责什么（不写整个模块）
// [测试] --xxxtest（无专项写“无专项，人工验证：...”）
// [入口] 主要类型/方法（可选，跨文件入口建议写）
```

- `tree-map` 只摘 `[职责]`；`check-headers.ps1` 检查 >600 行文件有没有头。
- 读到没头的文件：不猜职责，先 `grep` 类型/方法声明，只读相关段；改它时补头。

## 5. 三条流程

**小改 / 修 bug**
1. 索引定位入口 + 直接调用方 1 处 + 一条测试；只读相关段。
2. 修 bug 先补能复现的测试（或记录可复现步骤）。
3. 最小改动；跑 1 条 L1 或 L2；索引按第 6 节更新。

**功能**
1. 先找成熟参考（AGENTS §0.2），写清入口/状态/失败行为/测试层级。
2. 放对应功能域；遵守命名、规模、文件头。
3. 加/改测试；更新 `module-map` 1 行（若入口或跨文件关系变了）。
4. 跑相关 L1/L2，报告命令与结果。

**重构**
1. 先记行为基线（相关 L1/L2 或 `Run-FuncSuite -Only`）。
2. 只改结构，不改行为；行为变更另立任务。
3. Move 与 Split 分 commit；每个 commit 可单独回滚。
4. 跑相关测试 + 重生成索引；全仓重构收尾才跑全量对比。

## 6. 索引更新（按改动类型）

| 改动 | module-map | gen-tree-map | gen-section-map | 跑 check |
| --- | --- | --- | --- | --- |
| 只改方法内部 | 不动 | 行数变了要重跑 | 行数变了要重跑 | tree + section |
| 新增文件 | 加 1 行（若是新流程） | 必须 | 必须 | 全部 |
| 改名/移动/拆分 | 改对应行 | 必须 | 必须 | 全部 |
| 只改注释/文件头 | 不动 | 必须（职责变了） | 通常不用 | tree |

命令：

```powershell
powershell -ExecutionPolicy Bypass -File tools/gen-tree-map.ps1
powershell -ExecutionPolicy Bypass -File tools/gen-section-map.ps1
powershell -ExecutionPolicy Bypass -File tools/check-tree-map.ps1
powershell -ExecutionPolicy Bypass -File tools/check-section-map.ps1
powershell -ExecutionPolicy Bypass -File tools/check-module-map.ps1
powershell -ExecutionPolicy Bypass -File tools/check-headers.ps1
```

## 7. 测试选择

| 层 | 命令 | 什么时候 |
| --- | --- | --- |
| L1 纯逻辑 | `.\run.ps1 --unittest <过滤词>` | 序列化/EditAction/表达式/设置迁移/布局几何 |
| L2 模块专项 | `.\run.ps1 --xxxtest` | 窗口/输入/渲染/像素/模块集成 |
| L3 全量 | `Run-FuncSuite.ps1` | 发版前；重构收尾可 `-Only <组>` |
| AOT | `.\publish.ps1 -NoZip -NoSetup -NoManifest -Label AOTsmoke` | 发版前；碰到反射/序列化/原生互操作时 |

- 每次改动至少跑**一条**与入口直接相关的测试；UI/交互不能只用 build 或 grep 当证据。
- 全量只在发版前；日常改完不跑全量。

## 8. 提交前检查（8 项）

1. 只改了目标功能；没有无关重命名、注释掉的旧代码、调试输出。
2. 新文件/改动文件符合目录、命名、规模规则。
3. >600 行文件有 `[职责]` + `[测试]` 头。
4. `module-map` 的首读/下一跳/测试与实际一致（入口变了才改）。
5. `check-tree-map` / `check-section-map` / `check-module-map` / `check-headers` 全过；
   ADR-005 起用 `tools/check-all.ps1` 一条命令跑完（含 build 告警基线与结构基线）。
6. `dotnet build -c Release` 通过（或 `.\run.ps1` 编译通过）。
7. 跑了 1 条相关 L1/L2，记录命令与结果；未跑全量要注明。
8. 有 AOT 风险或发版时，跑了 AOT 冒烟。

## 9. 十条硬规则

1. 先索引后读代码；禁止整仓通读。
2. 新代码放现有功能域；新目录层级先评审。
3. 文件名用功能词；禁止 `Misc/More/Common/Helpers/Utils`。
4. 单文件硬上限 2,000 行；超限拆或写明豁免。
5. 拆分以职责/状态机边界为准，不为行数拆。
6. >600 行/入口/易混淆文件必须有文件头。
7. 新增/改名/拆分同 commit 更新索引并重生成地图。
8. 每次提交过 build + 文件头 + 三个 check；功能改动跑 1 条 L2。
9. 全量 E2E + AOT 只在发版前；日常不跑。
10. 不新增 NuGet、不改 csproj/WPF 结构；例外先说明。

## 10. 禁止事项

- 禁止把新逻辑继续堆进已超 2,000 行的文件。
- **ADR-005 起：禁止给 `Engine` / `FullUi` / `Overlay` 新增私有字段或 partial 文件**；
  新状态用组合对象，存量冻结。空 `catch` 必须带一行原因。
- **拆分只看 ADR-005 D4 的触发条件**（同一字段组 2 次以上真实 bug，或单次改动跨 >5 文件），
  不按行数、不按"看起来太大"。
- 禁止手改 `tree-map` / `section-map`。
- 禁止只跑 build/grep 就宣称 UI/输入/渲染已验证。
- 禁止把单一状态机拆到多个文件“凑小”。
- 禁止为改一个文件顺手重命名无关文件。
- 禁止留过期索引、注释掉的旧代码、调试输出。

## 11. 规范失效信号

- `check-*` 只在发版前跑，或经常“下次再补索引”。
- `module-map` 行数不涨，但每行越来越长、路径指向目录、测试项缺失。
- 大文件持续增长，理由是“还没超 2,000”或“先不动”。
- 文件头是复制粘贴的泛词，或 >600 行文件普遍没头。
- `Misc/More` 换成别的泛化词，名字合规但内容是杂物。
- 每次跑同一条无关 L2；UI 改动长期没有可复现证据。
