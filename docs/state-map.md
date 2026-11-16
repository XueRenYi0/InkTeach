# 状态地图（自动生成，勿手改）

> 生成：2026-11-16 13:48 ／ 脚本：`tools/gen-state-map.ps1` ／ ADR-005 D4
> 用法：改一个私有字段前，先在这里查它被哪些同族文件读写；只列被 >=2 个文件引用的字段。
> 它降低定位成本，不降低验证成本；拆不拆类看 ADR-005 D4 的触发条件，不按行数。

## Engine（24 文件 / 343 字段 / 272 个跨文件字段）

| 字段 | 引用文件 | 主要文件 | 写入点 |
| --- | --- | --- | --- |
| `_dirty` | 23 | Engine.cs, Engine.Host.cs, Engine.Selection.Beautify.cs, Engine.Selection.Gesture.cs, Engine.Selection.Vertex.cs, Engine.SelectionBar.Actions.cs …+17 | 303 |
| `_windows` | 14 | Engine.cs, Engine.Host.cs, Engine.Capture.cs, Engine.Clipboard.cs, Engine.Input.cs, Engine.Hotkey.cs …+8 | 1 |
| `_drawing` | 9 | Engine.cs, Engine.Host.cs, Engine.SelectionBar.cs, Engine.Input.cs, Engine.TouchGestures.cs, Engine.Radial.cs …+3 | 32 |
| `_libraryPending` | 8 | Engine.Host.cs, Engine.Hotkey.cs, Engine.Radial.cs, Engine.Library.cs, Engine.Library.Input.cs, Engine.Library.Layout.cs …+2 | 14 |
| `_libraryPendingTitle` | 8 | Engine.Host.cs, Engine.Hotkey.cs, Engine.Radial.cs, Engine.Library.cs, Engine.Library.Input.cs, Engine.Library.Layout.cs …+2 | 13 |
| `_restoreToolAfterLibrarySelection` | 8 | Engine.Input.cs, Engine.Hotkey.cs, Engine.Radial.cs, Engine.Library.cs, Engine.Library.Input.cs, Engine.Library.Layout.cs …+2 | 4 |
| `_selDragMoved` | 7 | Engine.cs, Engine.Selection.cs, Engine.Selection.Gesture.cs, Engine.Selection.Vertex.cs, Engine.SelectionBar.cs, Engine.TouchGestures.cs …+1 | 8 |
| `_dragHandle` | 6 | Engine.cs, Engine.Selection.cs, Engine.Selection.Gesture.cs, Engine.Selection.Vertex.cs, Engine.Cursor.cs, Engine.Library.Pair.cs | 12 |
| `_uiHover` | 6 | Engine.cs, Engine.Host.cs, Engine.Input.cs, Engine.Update.cs, Engine.Cursor.cs, Engine.UiBridge.cs | 11 |
| `_quit` | 6 | Engine.cs, Engine.Host.cs, Engine.Selection.Beautify.cs, Engine.Hotkey.cs, Engine.Update.cs, Engine.UiBridge.cs | 9 |
| `_shapeOneShot` | 6 | Engine.Input.cs, Engine.Hotkey.cs, Engine.Radial.cs, Engine.Library.cs, Engine.Library.Menu.cs, Engine.Library.Pair.cs | 5 |
| `_libraryCategoryInfos` | 6 | Engine.Keyboard.cs, Engine.UiBridge.cs, Engine.Library.cs, Engine.Library.Input.cs, Engine.Library.Layout.cs, Engine.Library.Menu.cs | 2 |
| `_dragIsMove` | 5 | Engine.cs, Engine.Selection.cs, Engine.Selection.Gesture.cs, Engine.Selection.Vertex.cs, Engine.Cursor.cs | 8 |
| `_eraserKind` | 5 | Engine.Host.cs, Engine.TouchGestures.cs, Engine.Hotkey.cs, Engine.Keyboard.cs, Engine.UiBridge.cs | 8 |
| `_camAnimating` | 5 | Engine.cs, Engine.Host.cs, Engine.Update.cs, Engine.Cursor.cs, Engine.UiBridge.cs | 7 |
| `_selDragMatrix` | 5 | Engine.cs, Engine.Selection.cs, Engine.Selection.Gesture.cs, Engine.Selection.Vertex.cs, Engine.TouchGestures.cs | 7 |
| `_autoSelCollapsed` | 5 | Engine.cs, Engine.Selection.Gesture.cs, Engine.Hotkey.cs, Engine.Library.Layout.cs, Engine.Library.Pair.cs | 6 |
| `_stepPlan` | 5 | Engine.cs, Engine.Input.cs, Engine.Hotkey.cs, Engine.Library.Pair.cs, Engine.ShapeTools.cs | 6 |
| `_capturePressHandled` | 5 | Engine.cs, Engine.Capture.cs, Engine.Input.cs, Engine.Library.Pair.cs, Engine.ShapeTools.cs | 5 |
| `_stepIndex` | 5 | Engine.cs, Engine.Input.cs, Engine.Hotkey.cs, Engine.Library.Pair.cs, Engine.ShapeTools.cs | 5 |

## App（28 文件 / 19 字段 / 8 个跨文件字段）

| 字段 | 引用文件 | 主要文件 | 写入点 |
| --- | --- | --- | --- |
| `_clickTargetHwnd` | 2 | App.Dev.ClickTarget.cs, App.cs | 3 |
| `_benchSpread` | 2 | App.Dev.Render.cs, App.Modes.Shape.cs | 1 |
| `_benchPressure` | 2 | App.Dev.Render.cs, App.Modes.Shape.cs | 1 |
| `_panelShowMore` | 2 | App.Dev.PageShow.cs, App.cs | 1 |
| `_benchPts` | 2 | App.Dev.Render.cs, App.Modes.Shape.cs | 1 |
| `_panelShowBand` | 2 | App.Dev.PageShow.cs, App.cs | 1 |
| `_clickLogFile` | 2 | App.Dev.ClickTarget.cs, App.cs | 1 |
| `_panelShowMini` | 2 | App.Dev.PageShow.cs, App.cs | 1 |

## Overlay（13 文件 / 128 字段 / 91 个跨文件字段）

| 字段 | 引用文件 | 主要文件 | 写入点 |
| --- | --- | --- | --- |
| `_ctx` | 10 | Overlay.Capture.cs, Overlay.Content.cs, Overlay.cs, Overlay.Frame.cs, Overlay.PptBar.cs, Overlay.Radial.cs …+4 | 4 |
| `_scratch` | 9 | Overlay.Capture.cs, Overlay.Content.cs, Overlay.cs, Overlay.Frame.cs, Overlay.PptBar.cs, Overlay.Radial.cs …+3 | 1 |
| `_fullFramesLeft` | 5 | Overlay.Capture.cs, Overlay.Content.cs, Overlay.cs, Overlay.Frame.cs, Overlay.Ui.cs | 11 |
| `_app` | 5 | Overlay.Content.cs, Overlay.cs, Overlay.Frame.cs, Overlay.Readout.cs, Overlay.Ui.cs | 3 |
| `_inkStyle` | 4 | Overlay.Capture.cs, Overlay.cs, Overlay.PptBar.cs, Overlay.Ui.cs | 3 |
| `_backBuffer` | 4 | Overlay.Capture.cs, Overlay.cs, Overlay.Frame.cs, Overlay.Ui.cs | 1 |
| `_frameDirty` | 4 | Overlay.Capture.cs, Overlay.cs, Overlay.Frame.cs, Overlay.Ui.cs | 1 |
| `_tiles` | 4 | Overlay.Capture.cs, Overlay.Content.cs, Overlay.cs, Overlay.Ui.cs | 1 |
| `_libraryRectPrev` | 3 | Overlay.cs, Overlay.Frame.cs, Overlay.Ui.cs | 4 |
| `_ctx2` | 3 | Overlay.cs, Overlay.PptBar.cs, Overlay.Ui.cs | 3 |
| `_uiBounds` | 3 | Overlay.cs, Overlay.Frame.cs, Overlay.Ui.cs | 3 |
| `_tilesVersion` | 3 | Overlay.Content.cs, Overlay.cs, Overlay.Ui.cs | 3 |
| `_uiBoundsPrev` | 3 | Overlay.cs, Overlay.Frame.cs, Overlay.Ui.cs | 3 |
| `_readoutFormatSmall` | 3 | Overlay.Capture.cs, Overlay.cs, Overlay.Readout.cs | 2 |
| `_readoutFormat` | 3 | Overlay.Capture.cs, Overlay.cs, Overlay.Readout.cs | 2 |
| `_drewWetStrokeThisFrame` | 3 | Overlay.Capture.cs, Overlay.cs, Overlay.Frame.cs | 2 |
| `_pageDirtyNow` | 3 | Overlay.Content.cs, Overlay.cs, Overlay.Frame.cs | 1 |
| `_inkSegs` | 3 | Overlay.cs, Overlay.PptBar.cs, Overlay.Ui.cs | 1 |
| `_inkTrail` | 2 | Overlay.Capture.cs, Overlay.cs | 9 |
| `_latencyWait` | 2 | Overlay.Capture.cs, Overlay.cs | 4 |

## FullUi（8 文件 / 142 字段 / 112 个跨文件字段）

| 字段 | 引用文件 | 主要文件 | 写入点 |
| --- | --- | --- | --- |
| `_host` | 8 | FullUi.Ai.cs, FullUi.AiMenu.cs, FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs …+2 | 12 |
| `_widgets` | 8 | FullUi.Ai.cs, FullUi.AiMenu.cs, FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs …+2 | 1 |
| `_moreOpen` | 7 | FullUi.Ai.cs, FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs, FullUi.cs …+1 | 9 |
| `_dark` | 7 | FullUi.Ai.cs, FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs, FullUi.cs …+1 | 3 |
| `_screen` | 6 | FullUi.Ai.cs, FullUi.AiMenu.cs, FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 2 |
| `_more` | 6 | FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 1 |
| `_moreH` | 6 | FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 1 |
| `_moreHover` | 5 | FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs, FullUi.Interaction.cs | 38 |
| `_actionFlashUntil` | 5 | FullUi.AiMenu.cs, FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 6 |
| `_penPickerOpen` | 5 | FullUi.Ai.cs, FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 3 |
| `_work` | 5 | FullUi.Ai.cs, FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 2 |
| `_aiMenuOpen` | 5 | FullUi.Ai.cs, FullUi.AiMenu.cs, FullUi.MorePanel.cs, FullUi.cs, FullUi.Interaction.cs | 2 |
| `_bandCell` | 4 | FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 28 |
| `_morePress` | 4 | FullUi.ApiSettings.cs, FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.Interaction.cs | 20 |
| `_morePage` | 4 | FullUi.Ai.cs, FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.Interaction.cs | 19 |
| `_press` | 4 | FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 16 |
| `_railHover` | 4 | FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 15 |
| `_hover` | 4 | FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 14 |
| `_hubHint` | 4 | FullUi.MorePanel.cs, FullUi.Pages.cs, FullUi.Settings.cs, FullUi.Interaction.cs | 10 |
| `_dragging` | 4 | FullUi.MorePanel.cs, FullUi.Settings.cs, FullUi.cs, FullUi.Interaction.cs | 8 |

## Model（8 文件 / 64 字段 / 4 个跨文件字段）

| 字段 | 引用文件 | 主要文件 | 写入点 |
| --- | --- | --- | --- |
| `_builtSmoothVer` | 2 | Model.Stroke.cs, Model.Stroke.Tangent.cs | 3 |
| `_builtInkModelVer` | 2 | Model.Stroke.cs, Model.Stroke.Tangent.cs | 3 |
| `_builtRawLive` | 2 | Model.Stroke.cs, Model.Stroke.Tangent.cs | 2 |
| `_grid` | 2 | Model.EditActions.cs, Model.InkDocument.cs | 2 |

共 72 行；完整字段清单用 `tools/gen-state-map.ps1` 重跑（本文件不手工维护）。
