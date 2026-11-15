# 目录树（自动生成，勿手改）

> 生成：2026-11-15 ／ 脚本：`tools/gen-tree-map.ps1`
> 用途：目录 = 功能域、文件名 = 功能块；先看树定位目录，再看行数决定读哪一段。
> 语义导航（“某个需求该看哪 1–2 个文件”）看 `docs/module-map.md`；段落行号看 `docs/section-map.md`。


    **InkEngine/Ai/**
      - `AiApiSettings.cs` 37 行
      - `AiApiUi.cs` 189 行
      - `AiCard.cs` 2,760 行 — AI 题卡：题干/答案/图形/交互。
      - `AiPlotClient.cs` 343 行
      - `AiQuestionClient.cs` 509 行
      - `OcrClient.cs` 620 行
      - `OcrProviders.cs` 410 行
      - `OcrText.cs` 148 行

    **InkEngine/Classroom/**
      - `Classroom.cs` 2,099 行
      - `ClassroomDraw.cs` 714 行

    **InkEngine/Core/**
      - `BackgroundSaver.cs` 157 行
      - `BoardHistory.cs` 156 行
      - `Engine.cs` 2,639 行 — 引擎核心状态、自动存档、板书历史、主循环字段（主文件）。
      - `Engine.Host.cs` 2,217 行 — 启动、消息循环、窗口过程（WndProc）。
      - `Engine.Selection.Actions.cs` 180 行 — 操作条按钮动作分发（RunBarAction / RunBarButton）。
      - `Engine.Selection.Beautify.cs` 163 行 — 选中图片的本地美化（应用/回退）。
      - `Engine.Selection.cs` 68 行
      - `Engine.Selection.Gesture.cs` 1,462 行 — 框选/套索/拖动/对齐吸附/美化入口。
      - `Engine.Selection.Vertex.cs` 1,052 行 — 图形端点编辑：起手/拖动/吸附/读数/提交。
      - `Engine.SelectionBar.cs` 731 行 — 选中操作条的状态与动作：颜色/粗细/线型/锁定/层级/取色器/菜单。
      - `InkFile.cs` 346 行
      - `InkHistory.cs` 97 行
      - `InkSerializer.cs` 879 行
      - `Model.EditActions.cs` 920 行 — 撤销/重做动作（Add/Remove/Erase/Transform/SetProp 等）。
      - `Model.InkDocument.cs` 2,012 行 — 文档/页/槽/撤销栈/空间索引。
      - `Model.LaserTrail.cs` 185 行 — 激光笔轨迹（淡出、采样）。
      - `Model.Stroke.cs` 971 行 — Stroke 核心：点、颜色、粗细、变换、几何入口（含 InkPiece 嵌套类）。
      - `Model.Stroke.Curves.cs` 1,130 行 — 坐标系/数轴/四种曲线的几何构建。
      - `Model.Stroke.Solids.cs` 884 行 — 立体图形（圆柱/圆锥/球/棱柱等）的几何构建。
      - `Model.Stroke.Tangent.cs` 2,566 行 — 正切曲线的几何构建。
      - `Model.Types.cs` 853 行 — Tool / StrokeKind / CurveAxis / RectF / InkPoint 等基础类型。
      - `Recovery.cs` 182 行
      - `Selection.cs` 3,371 行 — 选择框、手柄、变换矩阵、紧框与命中。
      - `Settings.cs` 257 行
      - `SpatialGrid.cs` 109 行
      - `ToolNames.cs` 53 行

    **InkEngine/Doc/**
      - `DocImageSource.cs` 292 行
      - `DocImport.cs` 542 行
      - `DocMemory.cs` 64 行
      - `DocPages.cs` 533 行
      - `DocStore.cs` 96 行
      - `Pdfium.cs` 314 行

    **InkEngine/Export/**
      - `ClipboardImage.cs` 191 行
      - `ClipboardInk.cs` 134 行
      - `ClipboardText.cs` 59 行
      - `Export.cs` 1,175 行
      - `ImageData.cs` 224 行
      - `ImageFile.cs` 208 行
      - `PhotoBeautify.cs` 345 行
      - `ScreenCapture.cs` 106 行

    **InkEngine/Input/**
      - `CurveFit.cs` 88 行
      - `DwellAssist.cs` 122 行
      - `Engine.Capture.cs` 374 行 — 截图状态、确认/取消、手柄与读数。
      - `Engine.Clipboard.cs` 506 行
      - `Engine.Input.cs` 1,952 行 — 指针输入、停顿成型、动态橡皮。
      - `Engine.TouchGestures.cs` 751 行 — 触摸手势：单指/双指/三指/长按/惯性。
      - `GoogleInkSlidingWindow.cs` 667 行
      - `InkModel.cs` 285 行
      - `InkModelProbe.cs` 278 行
      - `InkStrokeModeler.cs` 1,318 行
      - `InputBlackBox.cs` 76 行
      - `InputPrecision.cs` 75 行
      - `MotionProbe.cs` 589 行
      - `PenInput.cs` 173 行
      - `PointerInput.cs` 143 行
      - `PressureSim.cs` 306 行
      - `SmoothProbe.cs` 313 行
      - `StrokeMotion.cs` 834 行
      - `StrokeSmoothing.cs` 523 行
      - `Touch.cs` 336 行
      - `TouchStats.cs` 135 行
      - `WpfInkFit.cs` 599 行

    **InkEngine/Platform/**
      - `AutoStart.cs` 196 行
      - `Backup.cs` 457 行
      - `DiagLog.cs` 1,270 行 — 运行日志、设备卡、日志上传同意。
      - `Engine.Hotkey.cs` 608 行
      - `Engine.Keyboard.cs` 484 行
      - `Engine.Radial.cs` 796 行
      - `Engine.Update.cs` 1,096 行
      - `EraserTelemetry.cs` 329 行
      - `GcLatency.cs` 109 行
      - `KeyBindings.cs` 576 行 — 键位表、作用域、冲突检测、解析。
      - `Latency.cs` 216 行
      - `LogFeedback.cs` 93 行
      - `Native.cs` 1,448 行
      - `Privacy.cs` 62 行
      - `SelfTestCancel.cs` 140 行
      - `Toast.cs` 40 行
      - `Tooltip.cs` 245 行
      - `UiScale.cs` 63 行
      - `Update.cs` 951 行 — 自动更新：版本比较、清单解析、下载/换壳。
      - `UpdateProbe.cs` 364 行

    **InkEngine/Ppt/**
      - `Ppt.cs` 1,561 行
      - `PptBar.cs` 174 行
      - `PptComLate.cs` 319 行
      - `PptComSource.cs` 440 行
      - `PptLaunch.cs` 236 行
      - `PptLink.cs` 193 行

    **InkEngine/Prediction/**
      - `InkPredictorLinear.cs` 273 行

    **InkEngine/Rendering/**
      - `CanvasTiles.cs` 444 行
      - `Cursors.cs` 492 行
      - `Engine.Cursor.cs` 788 行
      - `Engine.UiBridge.cs` 983 行
      - `GlyphFill.cs` 341 行
      - `Icons.Paths.cs` 142 行
      - `LegacyOverlayLayer.cs` 74 行
      - `Overlay.Capture.cs` 787 行 — 截图取景框绘制：遮罩/准线/读数/手柄。
      - `Overlay.Content.cs` 575 行
      - `Overlay.cs` 1,360 行
      - `Overlay.Frame.cs` 1,635 行
      - `Overlay.PptBar.cs` 651 行
      - `Overlay.Radial.cs` 654 行
      - `Overlay.Readout.cs` 1,466 行 — 读数、收藏面板、滚动条、回放/PPT 条绘制。
      - `Overlay.SelectionBar.cs` 1,065 行 — 选中操作条/色板/字体/图层/菜单的绘制。
      - `Overlay.Toast.cs` 339 行
      - `Overlay.Ui.cs` 1,442 行
      - `OverlayLayerManager.cs` 192 行
      - `OverlayLayerRegistration.cs` 215 行
      - `SvgPath.cs` 271 行
      - `Ui.cs` 1,629 行
      - `UiHost.cs` 257 行
      - `UiImageBytes.cs` 40 行

    **InkEngine/Replay/**
      - `Replay.cs` 560 行

    **InkEngine/Shapes/**
      - `CollectionFingerprint.cs` 99 行
      - `Engine.Library.cs` 1,109 行 — 收藏库：面板开合/页签/分类/选择/落盘。
      - `Engine.Library.Input.cs` 1,013 行 — 收藏库指针输入：拖拽/整理/排序/页签拖动。
      - `Engine.Library.Layout.cs` 583 行 — 收藏面板插入/几何/尺寸/拖拽落位。
      - `Engine.Library.Menu.cs` 407 行 — 收藏库 ⋯ 菜单、过滤、滚动。
      - `Engine.Library.Misc.cs` 555 行 — 收藏库杂项：双支配对/隐藏配对/结束笔画。
      - `Engine.ShapeTools.cs` 1,570 行
      - `FavoriteTransfer.cs` 149 行
      - `FigureParamBar.cs` 126 行
      - `FigureRenderer.cs` 349 行
      - `LibraryLayout.cs` 541 行
      - `PlotExpression.cs` 260 行
      - `PlotRenderer.cs` 484 行
      - `PlotSampling.cs` 335 行
      - `PlotSpecJson.cs` 462 行
      - `PointCloudRecognizer.cs` 403 行
      - `ShapeCatalog.cs` 126 行
      - `ShapeLibrary.cs` 829 行 — 收藏条目存储：.ink/.meta、分类、回收站、指纹去重。
      - `ShapePreview.cs` 161 行
      - `ShapeRecognize.cs` 2,925 行 — 一笔墨迹 → 图形识别（$P 点云 + 拟合 + 贴合判据）。

    **InkEngine/Text/**
      - `GlyphInk.cs` 286 行
      - `GlyphText.cs` 280 行
      - `HandwritingBlocks.cs` 277 行
      - `HandwritingComposer.cs` 187 行
      - `HandwritingOptimize.cs` 845 行
      - `HandwritingReplace.cs` 398 行
      - `LatexCompat.cs` 387 行
      - `MathRenderer.cs` 234 行
      - `TextBoxBackground.cs` 82 行
      - `TextBoxEditing.cs` 2,662 行 — 普通文本框插入/编辑/结束/富文本只读卡。
      - `TextBoxSelection.cs` 474 行
      - `TextInputService.cs` 568 行
      - `TextStyles.cs` 413 行
      - `WindowlessTextControl.cs` 1,093 行

  **InkLocalization/**
    - `Loc.cs` 344 行

    **InkTeach/DevTools/**
      - `App.Dev.AiShow.cs` 1,035 行
      - `App.Dev.Bench.cs` 578 行
      - `App.Dev.ClickTarget.cs` 79 行
      - `App.Dev.Erase.cs` 81 行
      - `App.Dev.Geometry.cs` 470 行
      - `App.Dev.Input.cs` 76 行
      - `App.Dev.Metrics.cs` 590 行
      - `App.Dev.PageShow.cs` 560 行
      - `App.Dev.Render.cs` 484 行
      - `App.Dev.Sheets.cs` 382 行
      - `App.Dev.Showcase.cs` 438 行
      - `App.Dev.Smooth.cs` 621 行
      - `App.Dev.TextShow.cs` 488 行
      - `AppIconUi.cs` 199 行
      - `CaptureIconSheet.cs` 152 行
      - `DwellProbe.cs` 806 行
      - `HeadlessUi.cs` 31 行
      - `InputReplay.cs` 146 行
      - `LaserIconSheet.cs` 130 行
      - `PdMetrics.cs` 183 行
      - `PenIcons.g.cs` 97 行
      - `PenSheet.cs` 163 行
      - `RecoProbe.cs` 2,185 行
      - `ShapeIconSheet.cs` 141 行
      - `ToolIconSheet.cs` 263 行
      - `UiProbe.cs` 120 行

    **InkTeach/Host/**
      - `App.cs` 2,277 行 — 宿主启动、参数分发、模式路由、用法帮助。
      - `App.Dev.Cursor.cs` 538 行
      - `App.Dev.Ppt.cs` 249 行
      - `App.Dev.Scroll.cs` 147 行
      - `App.Dev.ShapeShow.cs` 597 行
      - `App.Dev.Show.cs` 429 行
      - `Program.cs` 545 行

      **InkTeach/SelfTests/E2E/**
        - `AiApiShow.cs` 44 行
        - `ApiConfigTest.cs` 103 行
        - `AppClassroomTests.cs` 626 行
        - `ArrowClearTest.cs` 94 行
        - `AutoStart.cs` 106 行
        - `Backup.cs` 210 行
        - `BandAction.cs` 81 行
        - `BoardAndLaser.cs` 1,374 行
        - `Diagnostics.cs` 3,281 行
        - `Documents.cs` 1,235 行
        - `Erasers.cs` 1,000 行
        - `FacesProbe.cs` 186 行
        - `GlyphEraseTest.cs` 59 行
        - `GlyphInkTest.cs` 213 行
        - `GlyphLineTest.cs` 88 行
        - `HandBboxTest.cs` 131 行
        - `HandBlocksTest.cs` 28 行
        - `HandOcrTest.cs` 174 行
        - `HandReplaceTest.cs` 124 行
        - `HandTextBoxTest.cs` 92 行
        - `Helpers.cs` 190 行
        - `History.cs` 284 行
        - `ImageCardTest.cs` 418 行
        - `InkBeautify.cs` 82 行
        - `InkMotion.cs` 2,595 行
        - `InkPng.cs` 45 行
        - `InkShape.cs` 1,529 行
        - `LibraryShow.cs` 198 行
        - `Navigation.cs` 1,151 行
        - `OverlayAudit.cs` 218 行
        - `PageAudit.cs` 153 行
        - `PanelAndKeys.cs` 6,374 行
        - `PhotoBeautifyShow.cs` 80 行
        - `PlotFromCard.cs` 174 行
        - `PlotSuiteTest.cs` 205 行
        - `PptStorage.cs` 1,695 行
        - `PredictLinear.cs` 111 行
        - `Qr.cs` 48 行
        - `SelAiShow.cs` 126 行
        - `SelBarShow.cs` 40 行
        - `Selection.cs` 2,845 行
        - `SelMenuTest.cs` 215 行
        - `SettingsAudit.cs` 550 行
        - `ShapeAudit.cs` 104 行
        - `ShapeLibrary.cs` 4,485 行
        - `ShapePanel.cs` 842 行
        - `SnapTest.cs` 219 行
        - `Startup.cs` 41 行
        - `TextBoxSim.cs` 429 行
        - `TextColorTest.cs` 96 行
        - `TextInputAudit.cs` 100 行
        - `TextStyleShow.cs` 125 行
        - `ToastTest.cs` 43 行
        - `Touch.cs` 1,268 行

      **InkTeach/SelfTests/Unit/**
        - `UnitTestRunner.cs` 131 行
        - `UnitTests.Core.cs` 92 行

    **InkUi/Ai/**
      - `FullUi.Ai.cs` 399 行
      - `FullUi.AiMenu.cs` 168 行
      - `FullUi.ApiSettings.cs` 754 行

    **InkUi/Design/**
      - `IconAtlas.cs` 1,937 行
      - `Icons.g.cs` 498 行
      - `Tokens.cs` 465 行
      - `Widgets.cs` 149 行

    **InkUi/Framework/**
      - `FocusRing.cs` 121 行
      - `HitTarget.cs` 34 行
      - `IPage.cs` 38 行
      - `LegacyPage.cs` 52 行
      - `PageFrame.cs` 76 行
      - `PageHost.cs` 351 行
      - `PopupStack.cs` 134 行
      - `UiAction.cs` 59 行
      - `UiActionTable.cs` 45 行

    **InkUi/Panels/**
      - `FullUi.More.cs` 2,335 行 — 更多面板：首页/设置/说明/日志/AI 服务入口。
      - `FullUi.Pages.cs` 434 行
      - `FullUi.Settings.cs` 3,091 行 — 设置页：工具条/通用/书写/墨迹/AI/备份。
      - `HelpUi.cs` 499 行

    **InkUi/Shell/**
      - `Anim.cs` 89 行
      - `FullUi.cs` 1,828 行 — 工具条/球/上带/色带的主体布局与绘制。
      - `FullUi.Hit.cs` 2,285 行 — 界面命中、开合、换页、贴边隐藏。
