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
| 工具 / 笔迹 / 图片对象 | `InkEngine/Core/Model.Types.cs` | `InkEngine/Core/Model.Stroke.cs`（核心）· `Tool` · `Stroke` | `--unittest Serializer` |
| 图形几何（坐标/曲线/立体） | `InkEngine/Core/Model.Stroke.Curves.cs` | `InkEngine/Core/Model.Stroke.Tangent.cs` · `InkEngine/Core/Model.Stroke.Solids.cs` | `--shapetest` |
| 撤销 / 重做 | `InkEngine/Core/Model.EditActions.cs` | `EditAction` 及各 `*Action` | `--unittest EditAction` |
| 文档 / 页 / 槽 | `InkEngine/Core/Model.InkDocument.cs` | `InkDocument` · `PageSlot` | `--edittest` |
| 选择 / 变换 / 手柄 | `InkEngine/Core/Selection.cs` | 手柄顺序 / 紧框 / 变换矩阵 | `--seltest` / `--handletest` |
| 选中操作条（九格） | `InkEngine/Core/Engine.SelectionBar.cs` | 颜色/粗细/线型 · 取色器 · 菜单 | `--seltest` |
| 图形端点编辑 | `InkEngine/Core/Engine.Selection.Vertex.cs` | `InkEngine/Core/Engine.Selection.Gesture.cs` · `InkEngine/Core/Engine.Selection.Actions.cs` | `--handletest` |
| 空间索引 | `InkEngine/Core/SpatialGrid.cs` | 邻近查询 | `--seltest` |
| 序列化（.inkb） | `InkEngine/Core/InkSerializer.cs` | `SaveStrokes` / `LoadStrokes` | `--savetest` |
| 自动存档 / 崩溃恢复 | `InkEngine/Core/Recovery.cs` | `InkEngine/Core/BackgroundSaver.cs` · `InkEngine/Core/Engine.cs` 自动存档 | `--recoverytest` |
| 板书历史 | `InkEngine/Core/BoardHistory.cs` | `InkEngine/Core/InkHistory.cs` · `InkEngine/Core/Engine.cs` 板书历史 | `--recoverytest` |
| 设置 / 偏好 | `InkEngine/Core/Settings.cs` | `InkEngine/Core/Engine.cs` 界面偏好的存取 | `--settingsaudit` |
| 引擎主循环 / 启动 | `InkEngine/Core/Engine.Host.cs` | `Startup` · `Message loop` · `Window procedure` | `--startupcheck` |

### Input（输入 / 笔迹算法）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 指针按下/移动/抬起 | `InkEngine/Input/Engine.Input.cs` | `Pointer input` | `--inputtest` |
| 笔 / 鼠标 / 触摸采样 | `Input/PenInput.cs` | `Input/PointerInput.cs` · `InkEngine/Input/InputPrecision.cs` | `--inputtest` |
| 输入黑匣子 / 回放 | `Input/InputBlackBox.cs` | `InkTeach/InputReplay.cs` | `--replayinput` |
| 笔迹预测 | `Prediction/InkPredictorLinear.cs` | `InkEngine/Input/Engine.Input.cs` 自绘预测尾 | `--pdmetrics` |
| 笔迹模型 | `InkEngine/Input/InkStrokeModeler.cs` | `InkEngine/Input/InkModel.cs` · `InkEngine/Input/InkModelProbe.cs` | `--inkmodeltest` |
| 平滑 / 运动模型 | `InkEngine/Input/StrokeSmoothing.cs` | `InkEngine/Input/StrokeMotion.cs` · `InkEngine/Input/SmoothProbe.cs` | `--smoothtest` / `--motiontest` |
| 停顿成型 | `InkEngine/Input/Engine.Input.cs` | 停顿成型（两段）· `InkEngine/Input/DwellAssist.cs` | `--dwelltest` |
| 触摸手势 | `InkEngine/Input/Engine.TouchGestures.cs` | `触摸手势（8.4.0）` · `InkEngine/Input/Touch.cs` | `--touchtest` |
| 亚像素 / 湿墨 / 限速 | `InkEngine/Input/Engine.Input.cs` | `湿墨档位` · `长度限速` | `--inputtest` |

### Rendering（覆盖层 / 缓存 / 界面边界）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 覆盖层窗口 / 帧 | `InkEngine/Rendering/Overlay.Frame.cs` | `Frame` · `RenderFrame` | `--selftest 8` |
| 内容层分块缓存 | `InkEngine/Rendering/CanvasTiles.cs` | `InkEngine/Rendering/Overlay.Content.cs` 内容层 | `--tiletest` |
| 界面层合成 | `InkEngine/Rendering/Overlay.Ui.cs` | 界面层 · `InkEngine/Rendering/Ui.cs` · `InkEngine/Rendering/UiHost.cs` | `--uitest` |
| 多边形读数 / 角度标 | `InkEngine/Rendering/Overlay.Readout.cs` | 多边形读数 | `--rotatetest` |
| 选中操作条绘制 | `InkEngine/Rendering/Overlay.SelectionBar.cs` | `InkEngine/Rendering/Overlay.Readout.cs` 读数 | `--seltest` |
| PPT / 回放条 | `InkEngine/Rendering/Overlay.PptBar.cs` | `InkEngine/Rendering/Overlay.Readout.cs` | `--ppttest` |
| Toast / 提示 | `InkEngine/Rendering/Overlay.Toast.cs` | `InkEngine/Rendering/Overlay.Readout.cs` | `--paneltest` |
| 呼出盘 | `InkEngine/Rendering/Overlay.Radial.cs` | 呼出盘 · `InkEngine/Platform/Engine.Radial.cs` | `--radialtest` |
| 截图取景框 | `InkEngine/Rendering/Overlay.Capture.cs` | 截图模式 · `InkEngine/Export/ScreenCapture.cs` · `InkEngine/Input/Engine.Capture.cs` | `--capturetest` |
| 光标形状 | `InkEngine/Rendering/Cursors.cs` | `InkEngine/Rendering/Engine.Cursor.cs` 指针形状 | `--cursortest` |
| SVG 路径 / 图标 | `InkEngine/Rendering/SvgPath.cs` | `InkEngine/Rendering/Icons.Paths.cs` | `--makeicon` |

### Shapes（识别 / 图形卡 / 收藏）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 一笔识别成图形 | `InkEngine/Shapes/ShapeRecognize.cs` | `InkEngine/Shapes/PointCloudRecognizer.cs` | `--inktest` |
| 图形卡目录 | `InkEngine/Shapes/ShapeCatalog.cs` | `InkEngine/Shapes/ShapePreview.cs` | `--shapepaneltest` |
| 收藏库 | `InkEngine/Shapes/ShapeLibrary.cs` | `InkEngine/Shapes/Engine.Library.cs` · `InkEngine/Shapes/Engine.Library.Menu.cs` · `InkEngine/Shapes/Engine.Library.Layout.cs` · `InkEngine/Shapes/Engine.Library.Input.cs` | `--librarytest` |
| 收藏导入导出 | `InkEngine/Shapes/FavoriteTransfer.cs` | 设置页入口 | `--librarytest` |
| 图形渲染 / 参数 | `InkEngine/Shapes/FigureRenderer.cs` | `InkEngine/Shapes/FigureParamBar.cs` | `--shapetest` |
| AI 作图 | `InkEngine/Ai/AiPlotClient.cs` | `InkEngine/Shapes/PlotExpression.cs` · `PlotSampler` · `InkEngine/Shapes/PlotRenderer.cs` | `--plottest` |

### Text（文本框 / 字体 / 公式 / 手写）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 文本框 / 编辑 | `InkEngine/Text/TextBoxEditing.cs` | `InkEngine/Text/TextBoxSelection.cs` · `InkEngine/Text/WindowlessTextControl.cs` | `--textboxsim` |
| 字体 / 文本样式 | `InkEngine/Text/TextStyles.cs` | `InkEngine/Text/TextBoxBackground.cs` | `--textstyle` |
| 公式渲染 | `InkEngine/Text/MathRenderer.cs` | `InkEngine/Text/LatexCompat.cs` | `--mathtest` / `--unittest Latex` |
| 手写优化 | `InkEngine/Text/HandwritingOptimize.cs` | `InkEngine/Text/HandwritingReplace.cs` · `InkEngine/Text/HandwritingComposer.cs` | `--handreplace` |
| 字形描边 | `InkEngine/Text/GlyphText.cs` | `InkEngine/Text/GlyphInk.cs` · `InkEngine/Rendering/GlyphFill.cs` | `--glyphink` |

### Ai（题卡 / OCR / 服务商）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| AI 题卡 | `InkEngine/Ai/AiCard.cs` | `InkEngine/Ai/AiQuestionClient.cs` · `InkEngine/Shapes/FigureRenderer.cs` | `--imgcardtest` |
| OCR | `InkEngine/Ai/OcrClient.cs` | `InkEngine/Ai/OcrProviders.cs` · `InkEngine/Ai/OcrText.cs` | `--handocr` |
| 服务商配置 | `InkEngine/Ai/AiApiSettings.cs` | `InkEngine/Ai/AiApiUi.cs` | `--settingsaudit` |

### Doc / Ppt / Classroom / Replay / Export / Platform

| 模块 | 功能 | 先看 | 测试 |
| --- | --- | --- | --- |
| Doc | PDF / 图片文档页 | `Doc/DocImport.cs` · `InkEngine/Doc/DocPages.cs` · `InkEngine/Doc/Pdfium.cs` | `--doctest` |
| Ppt | 放映批注 / 翻页 / 落盘 | `InkEngine/Ppt/Ppt.cs` · `InkEngine/Ppt/PptLaunch.cs` · `InkEngine/Ppt/PptLink.cs` | `--ppttest` |
| Classroom | 计时 / 点名 | `InkEngine/Classroom/Classroom.cs` · `InkEngine/Classroom/ClassroomDraw.cs` | `--timertest` / `--rolltest` |
| Replay | 回放 | `InkEngine/Replay/Replay.cs` · `InkEngine/Rendering/Overlay.cs` 回放 | `--replaytest` |
| Export | 导出 / 剪贴板 / 图片 / 截图 | `InkEngine/Export/Export.cs` · `InkEngine/Input/Engine.Clipboard.cs` · `Clipboard*.cs` · `Image*.cs` · `InkEngine/Export/PhotoBeautify.cs` | `--iotest` |
| Platform | Win32 / 键位 / 缩放 / 更新 / 隐私 / 日志 / 备份 | `InkEngine/Platform/Native.cs` · `InkEngine/Platform/KeyBindings.cs` · `InkEngine/Platform/UiScale.cs` · `InkEngine/Platform/Update.cs` · `InkEngine/Platform/DiagLog.cs` · `InkEngine/Platform/Backup.cs` | `--keytest` / `--settingsaudit` |
| Platform | Toast / Tooltip / 诊断探针 | `InkEngine/Platform/Toast.cs` · `InkEngine/Platform/Tooltip.cs` · `InkEngine/Platform/Latency.cs` · `InkEngine/Platform/GcLatency.cs` | `--paneltest` |

### InkUi（产品界面）

| 功能 | 先看 | 锚点 / 再看 | 测试 |
| --- | --- | --- | --- |
| 工具条 / 球 / 上带 / 色带 | `InkUi/Shell/FullUi.cs` | 按钮表 · 布局 · 上带 · 色带 · 绘制 | `--paneltest` |
| 更多面板 / 设置页 / 日志页 | `InkUi/Panels/FullUi.More.cs` | `InkUi/Panels/FullUi.Settings.cs` · 日志与反馈页 | `--paneltest` |
| 页面栈 | `InkUi/Panels/FullUi.Pages.cs` | `Framework/IPage.cs` · `InkUi/Framework/PageHost.cs` | `--paneltest` |
| AI 面板 / 菜单 / API | `InkUi/Ai/FullUi.Ai.cs` | `InkUi/Ai/FullUi.AiMenu.cs` · `InkUi/Ai/FullUi.ApiSettings.cs` | `--settingsaudit` |
| 使用说明 | `InkUi/Panels/HelpUi.cs` | 同文件 | `--paneltest` |
| 设计令牌 | `InkUi/Design/Tokens.cs` | **只改这一处** | — |
| 元件 / 图标 / 动画 | `InkUi/Design/Widgets.cs` | `InkUi/Design/IconAtlas.cs` · `InkUi/Design/Icons.g.cs` · `InkUi/Shell/Anim.cs` | `--paneltest` |

### 宿主（InkTeach，开发期用）

| 功能 | 文件 | 测试 |
| --- | --- | --- |
| 启动 / 参数分发 / 模式路由 | `Host/App.cs` | `--startupcheck` |
| Program.Main / `--unittest` 拦截 | `Host/Program.cs` | `--unittest` |
| 内存 / 帧率报告 | `DevTools/App.Dev.Metrics.cs` | `--memory` |
| 合成输入 / 点击目标 | `DevTools/App.Dev.Input.cs` · `InkTeach/DevTools/App.Dev.ClickTarget.cs` | `--passtest` |
| 无界面宿主 | `DevTools/HeadlessUi.cs` · `InkTeach/DevTools/UiProbe.cs` | `--uitest` |
| 自检专项 | `SelfTests/E2E/*.cs` | 见 `docs/test-playbook.md` |
| 图标候选图 | `DevTools/*IconSheet.cs` · `InkTeach/DevTools/PenSheet.cs` | `--makeicon` |

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

- partial 拆文件：`<类名>.<功能>.cs`（先例：`InkUi/Ai/FullUi.Ai.cs`）。
- 文件头 5 行：`职责 / 入口 / 读写状态 / 落盘 Key / 相关自检`。
- 设计数字进 `InkUi/Design/Tokens.cs`；语言文案进 `Locales/*/core.json`。
- 单文件 800–2,000 行；已低于 2,000 行的文件不要再拆；同一私有状态机不许拆两处。
