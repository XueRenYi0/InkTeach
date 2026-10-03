# InkTeach · Classroom Annotation for Windows

[中文](README.md) | [English](README.en.md)

**A transparent overlay you can write on — over PowerPoint, WPS, or any other app.**
Low-latency pen input with pressure support, wrapped in a collapsible floating toolbar:
pen / highlighter / laser pointer / erasers / selection / screenshot / 22 shape tools /
whiteboard / timer / random-student picker. Everything stays on your machine, and the
installer needs no administrator rights.

[![Release](https://img.shields.io/github/v/release/XueRenYi0/InkTeach?label=release&color=blue)](https://github.com/XueRenYi0/InkTeach/releases/latest)
[![License](https://img.shields.io/github/license/XueRenYi0/InkTeach)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-lightgrey)
[![Downloads](https://img.shields.io/github/downloads/XueRenYi0/InkTeach/total)](https://github.com/XueRenYi0/InkTeach/releases)

**Download**: [GitHub Releases](https://github.com/XueRenYi0/InkTeach/releases/latest)
(`InkTeach-Setup-<version>.exe` — next-next-done, or the portable
`InkTeach-<version>-win-x64.zip`) ｜ [GitCode mirror](https://gitcode.com/xzx1xzzx/InkTeach/releases)

<p align="center"><img src="design/readme/00-演示.gif" width="800" alt="Demo: draw a rough circle, hold still, it snaps into a real circle; select and drag it; then screenshot mode"></p>

## What it is

- **An overlay, not screen recording**: a transparent always-on-top window. All ink lives on
  the annotation layer; click the "mouse" cell (or press `Ctrl+Alt+T`) to pass the mouse
  straight through to the app underneath.
- **Built for pen input**: pressure-sensitive width, low end-to-end latency, system-level
  "wet ink" while writing (details below).
- **Classroom tools included**: countdown / stopwatch timer, random-student picker,
  whiteboard with three colors, PowerPoint slideshow integration (per-slide isolation,
  auto save/restore, ink replay).
- **Fully local**: settings and autosaves live in `%APPDATA%\InkTeach`; update checks only
  happen when you click, and packages are sha256-verified before install.
- **Open source**: GPL-3.0 (commercial licensing available). Third-party credits in
  [THIRD-PARTY-NOTICES.md](src/InkEngine/THIRD-PARTY-NOTICES.md).

## UI & Features

### A toolbar that tucks itself away

A capsule toolbar sits at the bottom of the screen. A **context strip grows above it for the
current tool** — colors + dash style + width for the pen, 22 segments for shapes, eraser size,
board color + pattern for the whiteboard, capture mode for screenshots. Drag it anywhere;
dock it to the bottom edge and it hides (fast-out, slow-in). Two profiles (minimal / full),
right-click to unpin a cell, hover ~0.5s (touch: press and hold 0.6s) for name + shortcut + hint.

<p align="center"><img src="design/readme/01-主工具带.png" width="880" alt="Toolbar with the pen's context strip"></p>

### Writing

- **Pressure** matches WPF / Windows Ink: `width = preset × clamp(2.0 × pressure, 0.10, 2.0)`
  (half pressure = 1.0×, full pressure = 2.0×), rendered with Direct2D's native ink
  (`ID2D1Ink`, per-point radius); mouse / non-pressure devices fall back to uniform width.
- **Latency**: each frame waits for the previous one to reach the screen before pumping
  messages and presenting (Present→photon dropped from 3.5 refresh cycles to **1.0**).
  Real pens get system-composited wet ink; a GC low-latency mode covers the whole stroke.
- **Hold-to-shape**: draw a stroke, hold the pen still for ~400ms — it snaps into a clean
  shape (line / circle / rectangle / triangle…); keep holding to drag it into place.
  Snapping uses the same ±1° special-angle soft snap as the line tool.
- **Two erasers**: whole-stroke erase, and area erase that splits a stroke in two
  (size follows pen speed; what you see is exactly what gets erased).

### Selection & editing

Point-select (Shift adds / Alt removes), rectangle marquee (touch-to-select), lasso
(80% of representative points inside). Selection box: 8 handles + rotation grip with
90° soft snap (`Shift` = 15° grid, `Alt` = free). Tiny objects switch to a **minimum
operation box** so handles never cover the content, and the scale anchor stays on the
object's own opposite corner.

The 10-cell action bar: **collapse / color / lock / layer / export / library / duplicate /
flip-h / flip-v / delete**. The color panel has a continuous width slider (live value,
one undo step on release), three dash styles, a 4×4 palette and an HSV picker. Dragging and
rotating never touch the model: the selection is lifted onto the floating layer, the content
layer repaints zero tiles, and the whole gesture is one undo step.

<p align="center"><img src="design/readme/03-选中与操作条.png" width="700" alt="Selection, action bar and color panel"></p>

### 22 shape tools

- Basics: line / arrow / rectangle / ellipse / circle / triangle / parallelogram / axes
- Conics & trig: ellipse-with-foci / parabola / hyperbola / sine / cosine / wave / tangent
- Solids: cylinder / cone / cone frustum / sphere / prism / pyramid / pyramid frustum

Every defining element (endpoints, semi-axes, vertices, focus, asymptotes) is a draggable
handle, with special-shape snapping (square, perfect circle, pose angles) and live angle /
radius / length readouts.

<p align="center"><img src="design/readme/08-图形上带.png" width="880" alt="Shape strip: 22 segments including conics and solids"></p>

### Screenshot · export · clipboard

- **Screenshot**: freeze the screen on press → drag a frame (dimmed mask, crosshair guides,
  size readout) → adjust with 8 handles / arrow keys → **lands in place**, auto-selected and
  copied. Two modes: "include annotations" and "hide overlay" (captures the content below).
- **Export**: selection to transparent PNG / white PNG / JPEG / BMP; "More → Save image"
  saves the whole board (JPEG on white by default).
- **Clipboard**: `Ctrl+C` puts both object bytes and a transparent PNG on the clipboard in
  one session — paste back into InkTeach as editable objects, or into Word / PPT / WeChat
  as an image.

<p align="center"><img src="design/readme/04-截图取景.png" width="560" alt="Screenshot adjust mode: handles, size readout, confirm/cancel"></p>

### Whiteboard · PowerPoint · classroom tools

- **Whiteboard**: an opaque mask (white / green / black + grid or ruled pattern); switching
  back to slides keeps your ink.
- **PowerPoint mode**: enters automatically in slideshow, leaves automatically to the desktop
  page; pages are fully isolated (scrolling still works inside a page, per-page scroll
  memory), auto save/restore, ink replay per page; zero side effects when PowerPoint/WPS is
  missing.
- **Timer**: countdown / stopwatch / chronometer, mutually independent; collapses to 320×150
  or goes true fullscreen; rings once and keeps counting overtime.
- **Random student**: rolling animation then freeze; no repeats until the pool is empty;
  name list at `%APPDATA%\InkTeach\Names.txt`.

<p align="center">
<img src="design/readme/02-更多面板.png" width="620" alt="More panel: classroom and ink entry points">&nbsp;&nbsp;
<img src="design/readme/06-课堂计时器.png" width="620" alt="Classroom timer">
</p>
<p align="center"><img src="design/readme/07-随机点名.png" width="620" alt="Random student picker"></p>

### Dark theme · minimal profile · auto update

<p align="center"><img src="design/readme/05-深色主题.png" width="880" alt="Dark theme"></p>

- **Dark theme** covers the toolbar, floating panels, tooltips and the PPT control bar.
- **Minimal profile**: `[mouse][board][pen][eraser][undo][more]`; both profiles keep the
  same relative order.
- **Auto update**: only on user click; six sources probed in parallel (China-friendly mirrors
  first), download sha256-verified, then a shell-swap restart with rollback and untouched
  user data.

## Under the hood & performance

```
InkTeach   host: CLI / self-tests / benchmarks (the shipped InkTeach.exe)
  ├ InkUi      product UI: ball ↔ toolbar, central "More" panel, classroom windows (all custom-drawn)
  └ InkEngine  engine: document model · input · rendering · hit-testing · storage · Win32
               the only contracts: IOverlayUi · IUiHost · IEngineCommands
```

- **Window**: borderless, topmost, no focus stealing, no taskbar entry; pass-through uses
  hit-test exemption (`WM_NCHITTEST → HTTRANSPARENT`).
- **Pipeline**: Direct3D11 + Direct2D + DirectComposition. Ink is D2D native stroking
  (`DrawGeometry`, round caps/joins); pressure goes through `ID2D1Ink`. No third-party UI
  framework — the whole interface is custom-drawn.
- **Content layer**: canvas-space **tiling** (`CanvasTiles`) with dirty-rect incremental
  patching — writing repaints only the affected tile; scrolling/paging stays smooth.
- **Input**: `WM_POINTER` for pen / touch / mouse; real pens get system-composited wet ink;
  a guard keeps a second finger from stealing the stroke in progress.
- **Hit-testing**: uniform spatial grid, O(nearby cells) instead of O(strokes) (~32× faster).
- **Engine / UI split**: the UI only knows `IOverlayUi` / `IUiHost` / `IEngineCommands`;
  swapping the whole UI does not touch the engine.

### Measured numbers (2× DPI machine; sources in the docs below)

| Item | Number |
|---|---|
| Present → photon | **1.0 refresh cycle** (3.5 without pacing) |
| Dragging a 10k-stroke selection | content layer repaints **0 tiles/frame**; record 2.7ms avg / 5.5ms max |
| Idle frame | **0** (the UI only draws when something moves) |
| Panel / animation frame | ~0.5–1.5 ms |
| Full 10k-stroke rebuild | ~190 ms (stress benchmark) |
| GC while writing | `SustainedLowLatency` for the whole stroke, asserted no gen-2 collection |
| Memory (10k strokes, 2× DPI) | ~430 MB working set incl. shared |

## Install

1. **Installer (recommended)**: `InkTeach-Setup-<version>.exe` — next-next-done,
   **no UAC** (per-user install to `%LOCALAPPDATA%\Programs\InkTeach`). Silent deploy:
   ```powershell
   .\InkTeach-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
   ```
2. **Portable**: unzip `InkTeach-<version>-win-x64.zip` and run `InkTeach.exe`.
   Copy only the app folder — not the dev machine's `%APPDATA%\InkTeach`.

**Requirements**: Windows 10 / 11 x64, **no .NET install needed** (self-contained).
PowerPoint/WPS integration requires PowerPoint or WPS installed; everything else works without.

## Shortcuts (summary)

| Scope | Keys |
|---|---|
| Global | `Ctrl+Alt+T` pass-through ｜ `Ctrl+Alt+X` quit |
| In-app | `Ctrl+P` pen ｜ `Ctrl+I` highlighter ｜ `Ctrl+L` laser ｜ `Ctrl+E` eraser ｜ `Ctrl+M` select ｜ `Ctrl+S` screenshot ｜ `Ctrl+Q` radial palette (hold & flick) |
| Editing | `Ctrl+Z / Y` undo / redo ｜ `Ctrl+A` select all ｜ `Ctrl+C / V` copy / paste ｜ `Delete` ｜ `Esc` ｜ arrows nudge (`Shift` ×10) |
| Other | `Ctrl+6` width ｜ `Ctrl+Shift+C` clear ｜ `PageUp / PageDown` page |

Full key table: [快捷键总表.md](快捷键总表.md) (Chinese).

## Build & self-test

```powershell
# interactive (requires .NET 8 SDK)
dotnet run --project src/InkTeach -c Release

# rendering verification + 10k-stroke stress benchmark
dotnet run --project src/InkTeach -c Release -- --selftest 8

# functional suite (per-case pass/fail) and performance suite (numbers only)
pwsh -File tools/bench/Run-FuncSuite.ps1
pwsh -File tools/bench/Run-PerfSuite.ps1
```

Self-tests never rely on human eyes: synthetic input plus pixel-counted screenshots, covering
60+ switches (input path, erasers, selection, screenshot, PPT, timer, export, auto-update…),
each with assertions; the process exits non-zero on failure.

## Docs

Most design docs are in Chinese; start with the development notes:

| Document | Content |
|---|---|
| [开发笔记.md](开发笔记.md) | the full former README: measurements, pitfalls, self-test catalog, release & update details |
| [架构-分层与规则.md](架构-分层与规则.md) | layering and single-source rules |
| [底层设计-笔迹对象模型.md](底层设计-笔迹对象模型.md) | stroke / shape / image object model |
| [对标-微软墨迹栈与我们的架构.md](对标-微软墨迹栈与我们的架构.md) | comparison with WPF / UWP Ink |
| [延时-实测与优化.md](延时-实测与优化.md) | end-to-end latency measurements |
| [计划-图形工具.md](计划-图形工具.md) | the 22 shapes and hold-to-shape spec |

## License

**Copyright (C) 2026 XueRenYi0**. **GPL-3.0** (see [LICENSE](LICENSE)): free to use, modify
and distribute, including commercially; derivatives must stay GPL-3.0 with attribution.
**Commercial licensing** (closed-source / embedding) is available on request — the author
holds the full copyright, so dual licensing is possible.
