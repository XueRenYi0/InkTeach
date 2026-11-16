# InkTeach 语义索引（跨文件流程）

> **结构看 `docs/tree-map.md`**（自动生成：目录 = 功能域、文件名 = 功能块、行数、文件头职责）。
> 本文只记树答不了的那一层：**用户需求 → 首读哪 1 个文件 → 下一跳**。
> 不列全部文件、不写行号；行号看 `docs/section-map.md`，或直接 `grep -n`。
>
> 维护：只在新功能、入口换文件、跨文件关系变化时改本文；纯改方法内部不动。

## 1. 先看哪一层

```
需求/功能词
  → docs/tree-map.md     找“大概在哪个目录、有多大”（自动、零维护）
  → 本文                  找“先读哪个文件、下一跳是谁”（人工、30 条）
  → section-map / grep    找“文件内哪一段”
```

分层：`InkLocalization ← InkEngine ← InkUi ← InkTeach`。界面只认
`IOverlayUi / IUiHost / IEngineCommands`（`InkEngine/Ui.cs`），不碰文档模型。

## 2. 跨文件流程（30 条）

| 需求 / 功能 | 首读 | 下一跳 | 测试 |
| --- | --- | --- | --- |
| 新增/修改自检模式 | `InkTeach/Host/App.Modes.<域>.cs`（Dev/Doc/Ink/Shape/Sys/Ui） | `App.Modes.Catalog.cs`（总表）· `--list-modes` | 该模式自身 |
| 操作条颜色/粗细/线型 | `InkEngine/Core/Engine.SelectionBar.cs` | 外观 `InkEngine/Rendering/Overlay.SelectionBar.cs` | `--seltest` |
| 操作条按钮动作 | `InkEngine/Core/Engine.SelectionBar.Actions.cs` | `Engine.SelectionBar.cs` | `--seltest` |
| 框选/套索/拖动/对齐 | `InkEngine/Core/Engine.Selection.Gesture.cs` | `InkEngine/Core/Selection.cs` | `--seltest` / `--lassotest` |
| 图形端点编辑/吸附 | `InkEngine/Core/Engine.Selection.Vertex.cs` | `Model.Stroke.Curves.cs` · `Model.Stroke.Tangent.cs` · `Model.Stroke.Solids.cs` | `--handletest` |
| 图片美化 | `InkEngine/Core/Engine.Selection.Beautify.cs` | `InkEngine/Export/PhotoBeautify.cs` | `--inkbeautify` |
| 收藏拖拽排序 | `InkEngine/Shapes/Engine.Library.Input.cs` | `ShapeLibrary.cs::ReorderBefore` | `--librarytest` |
| 收藏面板/菜单/布局 | `InkEngine/Shapes/Engine.Library.cs` | `Engine.Library.Menu.cs` · `Engine.Library.Layout.cs` | `--librarytest` |
| 收藏存储/分类/回收站 | `InkEngine/Shapes/ShapeLibrary.cs` | `LibraryLayout.cs` · `CollectionFingerprint.cs` | `--librarytest` |
| 截图取景框/读数 | `InkEngine/Rendering/Overlay.Capture.cs` | 同文件 `DrawCaptureOverlay` 段 | `--capturetest` |
| 截图确认/数据 | `InkEngine/Input/Engine.Capture.cs` | `InkEngine/Export/ScreenCapture.cs` | `--capturetest` |
| 停顿成型 | `InkEngine/Input/Engine.Input.cs` | `InkEngine/Input/DwellAssist.cs` | `--dwelltest` |
| 触摸手势 | `InkEngine/Input/Engine.TouchGestures.cs` | `InkEngine/Input/Touch.cs` | `--touchtest` |
| 指针输入/橡皮 | `InkEngine/Input/Engine.Input.cs` | `InkEngine/Input/PenInput.cs` · `PointerInput.cs` | `--inputtest` / `--erasertest` |
| 导出/剪贴板 | `InkEngine/Input/Engine.Clipboard.cs` | `InkEngine/Export/Export.cs` · `Clipboard*.cs` | `--iotest` |
| 自动存档/恢复 | `InkEngine/Core/Recovery.cs` | `InkEngine/Core/Engine.cs` 自动存档段 | `--recoverytest` |
| 板书历史 | `InkEngine/Core/BoardHistory.cs` | `InkEngine/Core/Engine.cs` 板书历史段 | `--recoverytest` |
| 文档页（PDF/图片） | `InkEngine/Doc/DocImport.cs` | `DocPages.cs`（1× 底图） · `DocPatch.cs`（可见区高清补丁） · `Pdfium.cs` · `DocStore.cs` · `DocResumeStore.cs`（上次看到第几页） | `--doctest` / `--zoomtest` / `--docmem` |
| PPT 批注 | `InkEngine/Ppt/Ppt.cs` | `PptLaunch.cs` · `PptLink.cs` · `PptComLate.cs` | `--ppttest` |
| 回放 | `InkEngine/Replay/Replay.cs` | `Rendering/Overlay.Readout.cs` 回放段 | `--replaytest` |
| AI 题卡 | `InkEngine/Ai/AiCard.cs` | `AiQuestionClient.cs` · `Shapes/FigureRenderer.cs` | `--imgcardtest` |
| OCR / 手写优化 | `InkEngine/Ai/OcrClient.cs` | `InkEngine/Text/HandwritingOptimize.cs` | `--handocr` / `--handreplace` |
| 文本框 / 公式 | `InkEngine/Text/TextBoxEditing.cs` | `MathRenderer.cs` · `LatexCompat.cs` | `--textboxsim` |
| 图形识别 | `InkEngine/Shapes/ShapeRecognize.cs` | `PointCloudRecognizer.cs` | `--inktest` |
| 图形卡 / 图形库 | `InkEngine/Shapes/ShapeCatalog.cs` | `ShapePreview.cs` | `--shapepaneltest` |
| 设置页 | `InkUi/Panels/FullUi.Settings.cs` | `InkEngine/Core/Settings.cs` | `--settingsaudit` |
| 更多面板 / 日志页 | `InkUi/Panels/FullUi.MorePanel.cs` | `InkUi/Shell/FullUi.Interaction.cs` | `--paneltest` |
| 最近文档（更多 → 最近文档） | `InkUi/Panels/FullUi.Pages.cs`（那一小条） | `InkEngine/Doc/DocImport.cs` 打开成功时记一条 · `Doc/DocStore.cs` 的 `RecentDocs` 线格式 | `--paneltest` / `--doctest` |
| 键位 / 热键 | `InkEngine/Platform/KeyBindings.cs` | `InkEngine/Platform/Engine.Hotkey.cs` | `--keytest` |
| 自动更新 | `InkEngine/Platform/Update.cs` | `InkEngine/Platform/Engine.Update.cs` | `--updatetest` |
| 启动 / 模式分发 | `InkTeach/Host/App.cs` | `InkEngine/Core/Engine.Host.cs` | `--startupcheck` |
| 本地化 | `InkLocalization/Loc.cs` | `Locales/*/core.json` | `--langaudit` |

## 3. 容易走错的同名/近名文件

| 需求 | 认准 | 不要误读 |
| --- | --- | --- |
| 产品选择框 | `InkEngine/Core/Selection.cs` | `InkTeach/SelfTests/E2E/Selection.cs`（自检） |
| 截图三件套 | `Overlay.Capture.cs`（画）/ `Engine.Capture.cs`（状态）/ `ScreenCapture.cs`（抓屏） | 三个都叫 Capture，先按“画/状态/抓屏”分 |
| 收藏存储 vs 面板 | `Shapes/ShapeLibrary.cs`（数据）/ `Shapes/Engine.Library.cs`（面板） | 不要从 `ShapeLibrary` 找 UI |
| 引擎图标 vs 界面图标 | `Rendering/Icons.Paths.cs` / `InkUi/Design/Icons.g.cs` | 一个引擎操作条，一个界面 |
| 宿主主文件 | `InkTeach/Host/App.cs` | `Host/App.Dev.*`、`DevTools/App.Dev.*` 是开发工具 |

## 4. 找代码的顺序

1. `tree-map.md` 定位目录和文件；
2. 本表拿“首读 + 下一跳”；
3. `section-map.md` 或 `grep -n "段落标题"` 拿行号；
4. `Read(offset, limit=250)` 只读那一段；跨层只追 1 跳；
5. 改完跑对应 L1/L2（见 `docs/test-playbook.md`）。

## 5. 维护规则

- 本文只维护“跨文件/易混淆”的 30 条；新增文件先看 `tree-map`，不要往这里加全量清单。
- 新增/改名/换入口：改对应 1 行 + 重跑 `tools/gen-tree-map.ps1`。
- 只改方法内部：本文、tree-map、section-map 都不用动。
- 校验（发版前一起跑）：
  - `tools/check-tree-map.ps1`（树是否过期）
  - `tools/check-section-map.ps1`（行数是否过期）
  - `tools/check-module-map.ps1`（本文引用的文件是否存在）

## 6. 命名与文件头

- partial 拆文件：`<主类型>.<功能>.cs`（`Engine.SelectionBar.cs`、`Overlay.Capture.cs`）。
- 避免 `Misc` / `More` / `Actions` 这类不说明产品功能的尾词；确实没有更好名字时才用。
- 关键文件头 3 行：`// [职责] ...` / `// [测试] ...`；`tree-map` 会自动摘出职责。
- 单文件 800–2,000 行；已低于 2,000 行的文件不要再拆；同一个私有状态机不许拆两处。
