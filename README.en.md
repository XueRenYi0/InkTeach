# InkTeach · Annotation overlay for the Windows classroom

[中文](README.md) | [English](README.en.md)

**A transparent overlay that lets you write on top of PowerPoint, WPS, or literally any app.**
Not "one more whiteboard" — a board layer *pasted over* someone else's software. Your ink lives
on its own layer, the slide underneath is untouched, and one click hands the mouse back.

[![Release](https://img.shields.io/github/v/release/XueRenYi0/InkTeach?label=release&color=blue)](https://github.com/XueRenYi0/InkTeach/releases/latest)
[![License](https://img.shields.io/github/license/XueRenYi0/InkTeach)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-lightgrey)
[![Downloads](https://img.shields.io/github/downloads/XueRenYi0/InkTeach/total)](https://github.com/XueRenYi0/InkTeach/releases)

**Download**: [GitHub Releases](https://github.com/XueRenYi0/InkTeach/releases/latest) (`InkTeach-Setup-<ver>.exe`, or the portable `InkTeach-<ver>-win-x64.zip`)
｜[GitCode mirror (China)](https://gitcode.com/xzx1xzzx/InkTeach/releases)
｜**Website**: <https://xuerenyi0.github.io/InkTeach/>

**Requirements**: Windows 10 / 11 x64, **no .NET install needed** (self-contained).
PowerPoint/WPS integration requires PowerPoint or WPS installed; everything else works without.

<p align="center"><img src="design/readme/00-演示.gif" width="800" alt="Demo: draw a rough circle, hold still, it snaps into a real circle; select and drag it; then screenshot mode"></p>

---

## What makes it different

### ① The settings rail is a 6-pixel line until you need it

Above the selected tool there is a **settings rail that is never left open** — it is collapsed to a
**6-pixel** strip. Hovering expands it to the full **34-pixel** rail in 120ms; leaving, it waits
**450ms** before collapsing. That "fast in, slow out" timing is the same one Windows uses for
taskbar auto-hide.

**And it stays clickable while collapsed**: all 12 swatches remain directly clickable in that
6-pixel strip, because picking a colour is the highest-frequency action and forcing a two-step
interaction is the same as saying it's not good enough.

<p align="center"><img src="design/readme/01-主工具带.png" width="880" alt="Toolbar: the settings rail expanded above the capsule bar"></p>

<p align="center"><img src="design/readme/14-色带收起.png" width="880" alt="Rail collapsed: day-to-day it is just a 6-pixel colour line"></p>

### ② Dock it away and only a 48-pixel handle is left

Drag the bar to the bottom edge and it shrinks to a **handle** — 48 long, 8 thick, tinted with your
current ink colour. 48 is not arbitrary: the project's own rule says "48 is the smallest thing you
can reliably hit, which is enough for touchscreens" — and *hiding it is exactly the moment you most
need one clean hit*, because a classroom often has no keyboard at all.

Docking triggers only when you are within **10 px of the screen bottom** (not of the taskbar), so the
default position never hides by accident. Hiding has to be a deliberate drag.

<p align="center"><img src="design/readme/15-贴边隐藏.png" width="300" alt="Dock-away: shown as a round knob (left), hidden at the bottom edge it becomes a 48×8 handle (right)"></p>

### ③ The radial palette — hold a global key, flick, release

`Ctrl+Alt+Shift+Q` (**a global key: it works while any other app is in front**): hold it and eight
sectors appear at the cursor; flick toward the tool or colour you want and **release to apply**.
Sectors = pen / black / red / blue / eraser / select / highlighter / laser.

- Skipping the wait: the palette appears after 120ms, so an expert can flick before it is ever shown.
- **It also works while pass-through is on** — it is the way to *take the pen back* from the app
  underneath: picking a sector exits pass-through and switches to it. Release at the dead centre to
  just cancel and stay passed through.
- It adds no new commands at all: every sector runs the exact same code path as the tool keys.

### ④ Pass-through, and global keys to come back

Click the "mouse" cell (or press `Ctrl+Alt+Shift+T`) and the mouse goes to the app underneath, so
you can click your slide normally. When you want to write again you don't have to hunt for the bar —
global keys pull you back: the palette to switch tools, the same key to leave pass-through, and one
to quit. All three global keys are `Ctrl+Alt+Shift+…` — three modifiers — precisely so they don't
collide with other software.

### ⑤ A rough shape becomes a real one when you pause

Draw a wonky circle, pause about 400ms, and it snaps into a **proper** circle; keep holding and you
can still drag it into place before releasing. Same for lines, rectangles, triangles and
parallelograms. Snapping is the same machinery as "draw a line with the line tool" (soft snap ±1°
at special angles, `Shift` for a 15° grid, `Alt` for free).

### ⑥ 22 shapes, including the ones maths teachers actually need

Line / arrow / rectangle / ellipse / circle / triangle / parallelogram / coordinate system;
parabola / hyperbola / sine / cosine / wave / tangent; cylinder / cone / frustum / sphere /
prism / pyramid / truncated pyramid.

**Every defining element is a draggable handle** (endpoints, semi-axes, vertex, foci, asymptotes…),
with shape snapping while dragging (square, circle, orientation) and live angle / radius / length
readouts.

<p align="center"><img src="design/readme/08-图形上带.png" width="880" alt="Shape rail: 22 entries, conics and solids included"></p>

### ⑦ Per-slide isolation in PowerPoint

Slideshow mode enters and leaves automatically. **Ink on each slide is fully isolated**; you can
still scroll within a slide and each slide remembers where you were. Slides are saved and restored
automatically, and **this slide's ink can be replayed**. If PowerPoint/WPS isn't there, you get
*zero symptoms* — just no linkage, no errors, no dialogs.

### ⑧ A dark theme that was re-tuned, not inverted

The dark theme covers the toolbar, floating panels, tooltips, the PowerPoint strip and the classroom
windows. The two themes were graded **separately**: the light one reads thickness from a dark bottom
edge, the dark one from a light top sheen. Simply inverting the light values collapses the sense of
depth.

<p align="center"><img src="design/readme/16-色带展开-深色.gif" width="880" alt="Animation: in the dark theme the rail goes from a thin line to the full settings rail"></p>

<p align="center"><img src="design/readme/05-深色主题.png" width="880" alt="Dark theme toolbar"></p>

<p align="center">
<img src="design/readme/13-更多面板-深色.png" width="600" alt="Dark theme: the More panel">
&nbsp;&nbsp;
<img src="design/readme/11-课堂窗-深色-计时.png" width="600" alt="Dark theme: timer window">
</p>

---

## The interface

### One toolbar that folds away

A capsule toolbar pinned to the bottom of the screen; **the rail above it grows for the current
tool** — pen = swatches + line style + width, shapes = 22 entries, eraser = size,
whiteboard = board colour + pattern, capture = two capture modes.

The bar can be dragged anywhere and docks to the bottom edge; two density modes
(**minimal** keeps `[mouse][board][pen][eraser][undo][more]`, and both modes keep the same order);
right-click cancels or restores pinning; hovering about 0.5s (long-press 0.6s on touch) pops up
"name + shortcut + description".

### Writing

- **Pressure** follows the WPF / Windows Ink convention:
  `width = preset × clamp(2.0 × pressure, 0.10, 2.0)` (half pressure = 1.0×, full = 2.0×), rendered
  with Direct2D's native ink `ID2D1Ink` (per-point radius). Mouse and pressure-less devices fall back
  to a constant width.
- **Responsiveness**: each frame waits for "the previous frame is already on screen" before pumping
  messages and submitting, which takes *Present returned → pixels changed* from 3.5 refresh periods
  down to **1.0**. On a real pen the ink is composited by the system (wet ink), and a low-latency GC
  mode is enabled while writing — a self-test asserts no second-generation collection during a stroke.
- **Two erasers**: whole-stroke (delete whichever stroke you touch) and area (erase only what you
  touch, splitting the stroke in two). The area eraser's size follows hand speed (no shrink when
  slow, capped at 2.5× on a fast sweep), and the box you see *is* the affected region.

### Selecting and editing

Click to select ink (`Shift` add / `Alt` subtract), rectangular marquee (touch selects), or lasso
(a point counts when 80% of its samples are inside). The selection frame has 8 handles plus a rotate
handle, with 90° soft snapping (`Shift` 15° grid, `Alt` free). **Small objects automatically switch
to a minimal frame** so handles never sit on top of the content and the scaling anchor stays on the
object's own corner.

Ten-cell action bar: **collapse / colour / lock / layer / export / gallery / duplicate / flip H /
flip V / delete**. The colour panel has a continuous width slider (live readout while dragging,
one undo step on release), three line styles, a 4×4 palette plus HSV picking. Duplicate supports a
"drag out a copy" mode, and **during drag or rotation the model is not touched at all** — the preview
is drawn on the floating layer and committed on release as a single undo step.

<p align="center"><img src="design/readme/03-选中与操作条.png" width="620" alt="Selection frame, action bar and colour panel"></p>

### Capture · export · clipboard

- **Capture**: pressing freezes the whole screen → drag a frame (mask with a hole, guides, size
  readout) → release to adjust (8 handles, arrow-key nudging) → the image **lands in place**, is
  auto-selected and goes to the clipboard. Two modes: "with annotation" and "hide the overlay"
  (captures only what is underneath).
- **Export**: selected objects to transparent PNG / white-background PNG / JPEG / BMP;
  "More → Save image" writes the whole board to a file.
- **Clipboard object channel**: `Ctrl+C` puts **both** the object bytes and a transparent PNG into
  the same clipboard session — paste back into InkTeach and it's still **an editable object**,
  paste into Word / PowerPoint / WeChat and it's an image.

<p align="center"><img src="design/readme/04-截图取景.png" width="520" alt="Capture adjust: 8 handles, size readout, confirm/cancel"></p>

### Whiteboard · classroom tools

- **Whiteboard**: an opaque layer (white / green / black plus grid or ruled patterns); your ink is
  still there when you switch back to the slide.
- **Timer / random student picker**: two independent centred windows that share the toolbar's visual
  language, in both themes. The timer keeps three independent modes (switching tabs doesn't stop a
  running one, it rings at zero), collapses to a 320×150 clock, and can go truly fullscreen; at zero
  the **digits and the progress ring turn red together**, readable from the back of the room.
  The picker never repeats anyone (and resets when exhausted); the roster lives in
  `%APPDATA%\InkTeach\Names.txt`, and with no roster it can pick student numbers `1–N` directly.

<p align="center">
<img src="design/readme/06-课堂计时器.png" width="600" alt="Timer: ring progress, labelled primary button">
&nbsp;&nbsp;
<img src="design/readme/07-随机点名.png" width="600" alt="Random picker: drawn names as chips">
</p>

<p align="center">
<img src="design/readme/09-计时改时长.png" width="560" alt="Editing the duration: hours/minutes/seconds columns">
&nbsp;&nbsp;
<img src="design/readme/10-计时最小化.png" width="320" alt="Minimised: 320×150 clock">
&nbsp;&nbsp;
<img src="design/readme/12-课堂窗-深色-点名.png" width="560" alt="Random picker, dark theme">
</p>

> The timer's ring **follows your current ink colour** — a red pen gives a red ring, switching to a
> blue pen turns it blue. The single safety valve: if the ink colour is below 3:1 contrast against
> the panel (a white pen on a light panel, say) it falls back to the accent colour, because an
> invisible timer is a bug, not a style.

---

## Why Direct2D and .NET 8

A common question: "isn't Direct2D overkill for an annotation app?"
It isn't. We didn't pick it to draw animations — we picked it because three things **only this path
can do**, and none of them is a visual effect.

### Why Direct2D / DirectComposition

**① The system compositor draws the ink; the CPU only sends points.**
With pressure we use D2D's native ink `ID2D1Ink` (per-point radius). During a real pen stroke the
"wet ink" trail is handed to **Windows' inking engine** (DwmInk trail) — it is not something we
fake. The point isn't "our latency is lower" (anyone can claim latency); it's that **wet ink doesn't
have to be chased**: the OS provides it and our only job is delivering points on time.

**② Three layers, one pipeline, each with incremental dirty-rect updates.**
The board content layer, the overlay UI and the PowerPoint strip are separate composition targets.
The content layer is tiled in canvas space (`CanvasTiles`) and patched by dirty rectangle — a new
stroke repaints only that tile, and **dragging a selection of 10,000 objects repaints 0 content
tiles per frame**: the preview goes to the floating layer and only lands on release.

**③ Every pixel of UI is drawn by us — there is not a third-party control in the app.**
Corners, shadows, hover states, the colour rail, segments, chips: all our own geometry. The payoff
is concrete: **dark theme, 200% DPI and projector colour shifts are all changes to one file**
(`src/InkUi/Tokens.cs`) — no fighting a framework's theming, no living with its behaviour.

**The cost, stated plainly**: with no visual designer, we substitute "single source of truth plus
offscreen rendering" — `--panelshow` renders any cell or panel offscreen as a design proof.

### Rendering-pipeline details (added in this round)

This round adds no features; it re-walks the dirty-rect / camera / tile-cache path against mature
upstreams — **Xournal++'s tiles and preload, Rnote's viewport margin, MyPaint's tile discipline,
Excalidraw's camera handling, Windows Terminal's back-buffer invariant**. Where their stable
approach fits, we align with it instead of reinventing it:

| Detail | What we do |
|---|---|
| **Canvas-space tile cache** | The content layer is baked into **256 px tiles** (origins aligned to canvas pixels, seamless); tiles stay inside a budget and are LRU-evicted beyond it |
| **Dirty rects + partial present** | Each frame submits the minimal set of dirty rectangles (`Present1`); dirty rects of persistent UI such as the scrollbar are **gated by visibility** — an invisible scrollbar contributes nothing, otherwise "scrollbar ∪ ink" would push a neighbouring column off screen |
| **Two-frame back-buffer consistency** | After a camera change (scroll / zoom) the next **two frames repaint the full screen**, so both back buffers hold the new camera — borrowed from Windows Terminal's back-buffer discipline |
| **Off-viewport prefetch** | A ring of tiles outside the viewport is baked during idle frames (rate-limited, budgeted, and can be turned off) — scrolling into a new area never has to "catch up" (Xournal++ preload / Rnote viewport margin) |
| **Camera rounding** | Camera translation is rounded to device pixels, so static content repainted in place cannot smear from sub-pixel jitter |
| **Zero-repaint dragging** | Dragging / rotating a 10,000-object selection repaints **0 content tiles per frame**: the preview lives on the floating layer and lands on release (one undo step) |
| **Order-preserving undo** | Undoing a "clear" **inserts each stroke back at its original index**, so the document is byte-for-byte what it was before the clear |

### Why .NET 8 (instead of C++, WPF or WinUI3)

| Reason | What it actually means |
|---|---|
| **It has to install on school machines** | Self-contained: unzip and run. **No UAC, no .NET install, works from a USB stick.** School PCs often lock down installation — if it can't install, it can't be used |
| **No UI framework baggage** | What we need is "a transparent overlay with per-frame incremental redraw". WPF/WinUI composition and theming are pure overhead here (WinUI's Mica/acrylic drops frames on projectors, and we deliberately do no blur) |
| **The stack leaves the AOT door open** | "Custom-drawn UI + P/Invoke + Vortice" is one of the **few Windows desktop stacks that can go Native AOT** — WPF, WinForms and WinUI3 officially don't support trimming/AOT. If AOT lands later it is a structural win, not a micro-optimisation |
| **We control the GC** | Writing switches to `SustainedLowLatency`, and a self-test asserts **no second-generation collection happens during a whole stroke** — something you only get if you can drive GC modes yourself |

**Not done yet, and known weak spots** (no overclaiming):
- **Native AOT isn't enabled** (blockers: `dynamic` COM in `PptLink`, `System.Drawing.Common`,
  Vortice/SharpGen compatibility); we ship JIT for now.
- **.NET 8 security updates end 2026-11-10**; moving to .NET 10 has to happen before that.
- **Memory isn't cheap**: 93MB committed when idle (392MB with 10,000 strokes) — about 31MB is the
  .NET runtime plus assemblies, ~42MB D3D/D2D devices and swap chains, ~26MB our own code and data.

### Measured numbers (2× display / Iris Xe; method and environment in the docs below)

| Metric | Number |
|---|---|
| Present returned → pixels changed | **1.0 refresh period** (3.5 without pacing) |
| Dragging a 10,000-object selection | **0 tiles/frame** repainted; mean 2.7ms / max 5.5ms per frame |
| Idle frames | **0** (the UI draws when something moves) |
| Panel / animation frames | ~0.5 – 1.5 ms |
| Cold start (window visible) | **267 ms** (blank WPF window on the same machine: 402 ms) |
| Full re-tile of 10,000 strokes | ~190 ms (stress benchmark) |
| GC during writing | `SustainedLowLatency` held for a whole stroke, asserted no 2nd-gen collection |
| Memory (idle / 10,000 strokes) | 93 MB / 392 MB committed |

---

## Install

1. **Installer (recommended)**: `InkTeach-Setup-<ver>.exe` — double-click, next, done.
   **No UAC**: it installs per-user into `%LOCALAPPDATA%\Programs\InkTeach`, which is what lets
   auto-update replace the app without admin rights. Silent install for fleets:
   ```powershell
   .\InkTeach-Setup-<ver>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
   ```
2. **Portable**: unzip `InkTeach-<ver>-win-x64.zip` and run `InkTeach.exe`. Copy only the app
   folder to another machine — don't carry over `%APPDATA%\InkTeach` from your dev box.

**All data stays local**: settings in `%APPDATA%\InkTeach`, auto-archives in
`%LOCALAPPDATA%\InkTeach`, the roster in `%APPDATA%\InkTeach\Names.txt`. Update checks only go
online when you click them, and the downloaded package's sha256 is verified before it replaces
anything.

## Shortcuts (summary)

| Scope | Keys |
|---|---|
| Global | `Ctrl+Alt+Shift+T` pass-through ｜ `Ctrl+Alt+Shift+X` quit ｜ `Ctrl+Alt+Shift+Q` radial palette (hold & flick; **works in pass-through too** — picking a sector exits it) |
| In-app | `Ctrl+P` pen ｜ `Ctrl+I` highlighter ｜ `Ctrl+L` laser ｜ `Ctrl+E` eraser ｜ `Ctrl+M` select ｜ `Ctrl+S` screenshot |
| Editing | `Ctrl+Z / Y` undo / redo ｜ `Ctrl+A` select all ｜ `Ctrl+C / V` copy / paste ｜ `Delete` ｜ `Esc` ｜ arrows nudge (`Shift` ×10) |
| Other | `Ctrl+6` width ｜ `Ctrl+Shift+C` clear ｜ `PageUp / PageDown` page |

Pressing the same tool key again cycles it (pen/highlighter change colour, eraser toggles
whole-stroke/area, select toggles rectangle/lasso). During a slideshow the tool keys are
temporarily promoted to global hotkeys; `←→` pages and `↑↓` scroll the canvas.
Full table, change log and measured conflicts: [快捷键总表.md](快捷键总表.md) (Chinese).

## Build & self-test

```powershell
# interactive (requires .NET 8 SDK)
dotnet run --project src/InkTeach -c Release

# key bindings (also checks docs match the code)
dotnet run --project src/InkTeach -c Release -- --keytest

# rendering verification + 10,000-stroke stress benchmark
dotnet run --project src/InkTeach -c Release -- --selftest 8

# functional suite (red/green) and performance suite (numbers only)
pwsh -File tools/bench/Run-FuncSuite.ps1
pwsh -File tools/bench/Run-PerfSuite.ps1
```

The self-tests don't rely on eyeballs: synthetic input plus pixel counting, covering the input
path, erasers, selection, capture, PowerPoint, timer/picker, export and auto-update across 60+
switches. **Every number has an assertion** and a failing run exits with code 1.
Full list in [开发笔记.md](开发笔记.md) (Chinese).

## Architecture

```
InkTeach   host: CLI / self-test / benchmarks (double-click InkTeach.exe when published)
  ├ InkUi      product UI: ball ↔ toolbar, the centred "More" panel, classroom windows (all custom-drawn)
  └ InkEngine  engine: document model · input · rendering · hit-testing · storage · Win32
               the only contracts: IOverlayUi · IUiHost · IEngineCommands
```

- **Window**: borderless, always-on-top, never steals focus, never in the taskbar; pass-through uses
  a hit-test exemption (`WM_NCHITTEST → HTTRANSPARENT`) to hand the mouse to the app below.
- **Input**: `WM_POINTER` unifies pen / touch / mouse; a real pen's wet ink is delegated to the
  system compositor; touch has a guard so a second finger can't steal the stroke in progress.
- **Hit-testing**: a uniform spatial grid takes picking from O(strokes) to O(neighbouring cells)
  (measured 32× faster).
- **Engine / UI split**: the UI only knows three contracts, so swapping the whole UI cannot affect
  the engine (today `src/InkUi` is the one mounted).

## Documentation

| Document | Contents |
|---|---|
| [快捷键总表.md](快捷键总表.md) | **Single source of truth for shortcuts**: every binding, its scope, the change log, measured registration conflicts (Chinese) |
| [开发笔记.md](开发笔记.md) | Pitfalls, measured data, self-test inventory, release and auto-update details (Chinese) |
| [架构-分层与规则.md](架构-分层与规则.md) | Layering, single-source discipline, self-test rules (Chinese) |
| [延时-实测与优化.md](延时-实测与优化.md) | Per-scenario end-to-end latency measurements (Chinese) |
| [性能-内存与卡顿实测.md](性能-内存与卡顿实测.md) | Memory accounting and hitch attribution (Chinese) |
| [调研-启动内存与WPF对比.md](调研-启动内存与WPF对比.md) | Startup memory baseline and the Native AOT evaluation (Chinese) |
| [对标-微软墨迹栈与我们的架构.md](对标-微软墨迹栈与我们的架构.md) | Capability comparison against WPF / UWP Ink (Chinese) |

## Licence

**Copyright (C) 2026 XueRenYi0**. Released under **GPL-3.0** (see [LICENSE](LICENSE)): free to
use, modify and redistribute (including commercially); derivatives must stay open source under
GPL-3.0 and keep the attribution and licence notices. For **closed-source commercial use or
embedding in your own product**, contact the author about a **commercial licence** (copyright is
held solely by the author, so dual licensing is possible).

Projects we took ideas from but copied no code from (Ink Canvas / Inkeys / perfect-freehand / $P and
others) are listed with their licences in
[src/InkEngine/THIRD-PARTY-NOTICES.md](src/InkEngine/THIRD-PARTY-NOTICES.md).
