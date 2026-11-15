# InkTeach 模块地图（给 AI 的检索入口）

> 改代码前先查这张表：**功能 → 模块目录 → 文件 → 段落锚点**。只读命中的 1–2 个文件，
> 不整文件通读。表里没有的功能再 `grep`；查完把新入口补回本表（同一个 commit）。
>
> 行号不写在这里：`docs/section-map.md`（脚本自动生成）或 `grep -n "锚点"`。
> 2026-11-15 已完成按功能域分目录（纯 `git mv`，namespace 不变）；目录见第 4 节。

## 1. 分层与目标目录

```
InkLocalization  ←  InkEngine  ←  InkUi  ←  InkTeach
   文案/语言         引擎/文档      产品界面      宿主 + 自检
```

```
src/InkEngine/   Core/ Input/ Rendering/ Shapes/ Text/ Ai/ Doc/ Ppt/ Classroom/ Replay/ Export/ Platform/
src/InkUi/       Shell/ Panels/ Ai/ Framework/ Design/
src/InkTeach/    Host/ DevTools/ SelfTests/{Unit,E2E}
```

规矩：
- 界面只认 `IOverlayUi / IUiHost / IEngineCommands`（`InkEngine/Ui.cs`），不认识文档模型。
- 引擎不出现按钮 / 配色 / 文案。
- **只移文件、不改 namespace**：`partial` 的所有部分必须同 namespace，目录只负责检索。

## 2. 功能 → 模块 → 文件

### Core（文档模型 / 撤销 / 持久化）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 工具 / 笔迹 / 图片对象 | `Model.Types.cs` | `Model.Stroke.cs` · `Tool` · `Stroke` · `InkPiece` | `--unittest Serializer` |
| 撤销 / 重做 | `Model.EditActions.cs` | `EditAction` 及各 `*Action` | `--unittest EditAction` |
| 文档 / 页 / 槽 | `Model.InkDocument.cs` | `InkDocument` · `PageSlot` | `--edittest` |
| 选择 / 变换 / 手柄 | `Selection.cs` | 手柄顺序 / 紧框 / 变换矩阵 | `--seltest` / `--handletest` |
| 空间索引 | `SpatialGrid.cs` | 邻近查询 | `--seltest` |
| 序列化（.inkb） | `InkSerializer.cs` | `SaveStrokes` / `LoadStrokes` | `--savetest` |
| 自动存档 / 崩溃恢复 | `Recovery.cs` | `BackgroundSaver.cs` · `Engine.cs` 自动存档 | `--recoverytest` |
| 板书历史 | `BoardHistory.cs` | `InkHistory.cs` · `Engine.cs` 板书历史 | `--recoverytest` |
| 设置 / 偏好 | `Settings.cs` | `Engine.cs` 界面偏好的存取 | `--settingsaudit` |
| 引擎主循环 / 启动 | `Engine.Host.cs` | `Startup` · `Message loop` · `Window procedure` | `--startupcheck` |

### Input（输入 / 笔迹算法）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 指针按下/移动/抬起 | `Engine.Input.cs` | `Pointer input` | `--inputtest` |
| 笔 / 鼠标 / 触摸采样 | `Input/PenInput.cs` | `Input/PointerInput.cs` · `InputPrecision.cs` | `--inputtest` |
| 输入黑匣子 / 回放 | `Input/InputBlackBox.cs` | `InkTeach/InputReplay.cs` | `--replayinput` |
| 笔迹预测 | `Prediction/InkPredictorLinear.cs` | `Engine.Input.cs` 自绘预测尾 | `--pdmetrics` |
| 笔迹模型 | `InkStrokeModeler.cs` | `InkModel.cs` · `InkModelProbe.cs` | `--inkmodeltest` |
| 平滑 / 运动模型 | `StrokeSmoothing.cs` | `StrokeMotion.cs` · `SmoothProbe.cs` | `--smoothtest` / `--motiontest` |
| 停顿成型 | `Engine.Input.cs` | 停顿成型（两段）· `DwellAssist.cs` | `--dwelltest` |
| 触摸手势 | `Engine.Input.cs` | `触摸手势（8.4.0）` · `Touch.cs` | `--touchtest` |
| 亚像素 / 湿墨 / 限速 | `Engine.Input.cs` | `湿墨档位` · `长度限速` | `--inputtest` |

### Rendering（覆盖层 / 缓存 / 界面边界）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 覆盖层窗口 / 帧 | `Overlay.Frame.cs` | `Frame` · `RenderFrame` | `--selftest 8` |
| 内容层分块缓存 | `CanvasTiles.cs` | `Overlay.Content.cs` 内容层 | `--tiletest` |
| 界面层合成 | `Overlay.Ui.cs` | 界面层 · `Ui.cs` · `UiHost.cs` | `--uitest` |
| 多边形读数 / 角度标 | `Overlay.Readout.cs` | 多边形读数 | `--rotatetest` |
| 呼出盘 | `Overlay.Radial.cs` | 呼出盘 · `Engine.Radial.cs` | `--radialtest` |
| 截图取景框 | `Overlay.Capture.cs` | 截图模式 · `ScreenCapture.cs` | `--capturetest` |
| 光标形状 | `Cursors.cs` | `Engine.Cursor.cs` 指针形状 | `--cursortest` |
| SVG 路径 / 图标 | `SvgPath.cs` | `Icons.Paths.cs` | `--makeicon` |

### Shapes（识别 / 图形卡 / 收藏）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 一笔识别成图形 | `ShapeRecognize.cs` | `PointCloudRecognizer.cs` | `--inktest` |
| 图形卡目录 | `ShapeCatalog.cs` | `ShapePreview.cs` | `--shapepaneltest` |
| 收藏库 | `ShapeLibrary.cs` | `Engine.Library.cs` · `LibraryLayout.cs` · `CollectionFingerprint.cs` | `--librarytest` |
| 收藏导入导出 | `FavoriteTransfer.cs` | 设置页入口 | `--librarytest` |
| 图形渲染 / 参数 | `FigureRenderer.cs` | `FigureParamBar.cs` | `--shapetest` |
| AI 作图 | `AiPlotClient.cs` | `PlotExpression.cs` · `PlotSampler` · `PlotRenderer.cs` | `--plottest` |

### Text（文本框 / 字体 / 公式 / 手写）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 文本框 / 编辑 | `TextBoxEditing.cs` | `TextBoxSelection.cs` · `WindowlessTextControl.cs` | `--textboxsim` |
| 字体 / 文本样式 | `TextStyles.cs` | `TextBoxBackground.cs` | `--textstyle` |
| 公式渲染 | `MathRenderer.cs` | `LatexCompat.cs` | `--mathtest` / `--unittest Latex` |
| 手写优化 | `HandwritingOptimize.cs` | `HandwritingReplace.cs` · `HandwritingComposer.cs` | `--handreplace` |
| 字形描边 | `GlyphText.cs` | `GlyphInk.cs` · `GlyphFill.cs` | `--glyphink` |

### Ai（题卡 / OCR / 服务商）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| AI 题卡 | `AiCard.cs` | `AiQuestionClient.cs` · `FigureRenderer.cs` | `--imgcardtest` |
| OCR | `OcrClient.cs` | `OcrProviders.cs` · `OcrText.cs` | `--handocr` |
| 服务商配置 | `AiApiSettings.cs` | `AiApiUi.cs` | `--settingsaudit` |

### Doc / Ppt / Classroom / Replay / Export / Platform

| 模块 | 功能 | 先看 | 测试 |
| --- | --- | --- | --- |
| Doc | PDF / 图片文档页 | `Doc/DocImport.cs` · `DocPages.cs` · `Pdfium.cs` | `--doctest` |
| Ppt | 放映批注 / 翻页 / 落盘 | `Ppt.cs` · `PptLaunch.cs` · `PptLink.cs` | `--ppttest` |
| Classroom | 计时 / 点名 | `Classroom.cs` · `ClassroomDraw.cs` | `--timertest` / `--rolltest` |
| Replay | 回放 | `Replay.cs` · `Overlay.cs` 回放 | `--replaytest` |
| Export | 导出 / 剪贴板 / 图片 / 截图 | `Export.cs` · `Clipboard*.cs` · `Image*.cs` · `PhotoBeautify.cs` | `--iotest` |
| Platform | Win32 / 键位 / 缩放 / 更新 / 隐私 / 日志 / 备份 | `Native.cs` · `KeyBindings.cs` · `UiScale.cs` · `Update.cs` · `DiagLog.cs` · `Backup.cs` | `--keytest` / `--settingsaudit` |
| Platform | Toast / Tooltip / 诊断探针 | `Toast.cs` · `Tooltip.cs` · `Latency.cs` · `GcLatency.cs` | `--paneltest` |

### InkUi（产品界面）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 工具条 / 球 / 上带 / 色带 | `FullUi.cs` | 按钮表 · 布局 · 上带 · 色带 · 绘制 | `--paneltest` |
| 更多面板 / 设置页 / 日志页 | `FullUi.More.cs` | `FullUi.Settings.cs` · 日志与反馈页 | `--paneltest` |
| 页面栈 | `FullUi.Pages.cs` | `Framework/IPage.cs` · `PageHost.cs` | `--paneltest` |
| AI 面板 / 菜单 / API | `FullUi.Ai.cs` | `FullUi.AiMenu.cs` · `FullUi.ApiSettings.cs` | `--settingsaudit` |
| 使用说明 | `HelpUi.cs` | 同文件 | `--paneltest` |
| 设计令牌 | `Tokens.cs` | **只改这一处** | — |
| 元件 / 图标 / 动画 | `Widgets.cs` | `IconAtlas.cs` · `Icons.g.cs` · `Anim.cs` | `--paneltest` |

### 宿主（InkTeach，开发期用）

| 功能 | 文件 | 测试 |
| --- | --- | --- |
| 启动 / 参数分发 / 模式路由 | `Host/App.cs` | `--startupcheck` |
| Program.Main / `--unittest` 拦截 | `Host/Program.cs` | `--unittest` |
| 内存 / 帧率报告 | `DevTools/App.Dev.Metrics.cs` | `--memory` |
| 合成输入 / 点击目标 | `DevTools/App.Dev.Input.cs` · `App.Dev.ClickTarget.cs` | `--passtest` |
| 无界面宿主 | `DevTools/HeadlessUi.cs` · `UiProbe.cs` | `--uitest` |
| 自检专项 | `SelfTests/E2E/*.cs` | 见 `docs/test-playbook.md` |
| 图标候选图 | `DevTools/*IconSheet.cs` · `PenSheet.cs` | `--makeicon` |

### 本地化

| 功能 | 文件 |
| --- | --- |
| 语言表 / `Loc.Tr` | `src/InkLocalization/Loc.cs` |
| 9 种语言文案 | `src/InkLocalization/Locales/<lang>/core.json`（中文是源文） |

## 3. 找代码的顺序

1. 查本表 → 得到模块目录、1–2 个文件、段落锚点。
2. 到 `docs/section-map.md` 查行号（或 `grep -n "锚点"`）。
3. `Read(offset, limit=250)` 只读那一段；跨层只追 1 跳。
4. 改完先跑 L1（纯逻辑）或 L2 里对应的一条；不要顺手全量。
5. 新增 / 拆分 / 改名文件 → 同 commit 改本表 + 重跑 `tools/gen-section-map.ps1`。

## 4. 目录迁移进度（2026-11-15 起）

| 模块 | 状态 | 说明 |
| --- | --- | --- |
| `Core/`、`Rendering/`、`Shapes/`、`Text/`、`Ai/`、`Ppt/`、`Classroom/`、`Replay/`、`Export/`、`Platform/` | 已迁移 | 2026-11-15，纯 `git mv`，namespace 不变 |
| `Input/`、`Prediction/`、`Doc/` | 已存在 | 沿用，未改路径 |
| `InkUi/Shell|Panels|Ai|Design` | 已迁移 | 同上 |
| `InkTeach/Host|DevTools|SelfTests/{Unit,E2E}` | 已迁移 | 同上；E2E 保留现有 53 个文件 |

迁移规则：
- **Move 与 Split 分离**：一个 commit 只搬家，不改内容；切文件下一个 commit。
- 每搬完一个模块：`dotnet build -c Release` + `--unittest` + `Run-FuncSuite -Only <分组>`。
- 全仓搬完：`baseline-post` + `tools/compare-baseline.ps1` 必须退出码 0。

## 5. 索引维护

| 索引 | 谁维护 | 什么时候动 | 写不写行号 |
| --- | --- | --- | --- |
| 本文件 | 人 / AI | 新增、拆分、改名文件，或入口方法换名 | **不写** |
| `docs/section-map.md` | `tools/gen-section-map.ps1` | 拆完文件、大改后、发版前 | 写，自动生成 |

- 只改方法内部：两份都不用动。
- 校验：`tools/check-module-map.ps1` 发版前跑。
- **禁止**把行号抄进本文件。

## 6. 命名约定

- partial 拆文件：`<类名>.<功能>.cs`（先例：`FullUi.Ai.cs`）。
- 文件头 5 行：`职责 / 入口 / 读写状态 / 落盘 Key / 相关自检`。
- 设计数字进 `Tokens.cs`；语言文案进 `Locales/*/core.json`。
- 单文件 800–2,000 行；已低于 2,000 行的文件不要再拆；同一私有状态机不许拆两处。
