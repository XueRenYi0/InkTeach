# 调研：SnowAir 的底层、功能与可借鉴点

日期：2026-11-02
对象：**SnowAir Lite v0.1.15**（commit `2fab020`）
来源：<https://github.com/CM-idea/SnowAir>（Apache-2.0；定制版 Ling 未开源）
源码：`C:\Users\LHL-XZX\Desktop\软件\downloads\SnowAir-src`
程序：`C:\Users\LHL-XZX\Desktop\软件\downloads\SnowAir-Lite.exe`

> 研究方法与边界：以源码阅读、静态统计、托盘待命内存和 `--selftest` 为主；
> 没有逐项点击 UI，也没有做 HDR/多屏/长截图/录屏的实机压力测试。
> 功能清单来自 README、Release Notes 和源码入口，具体交互以实际版本为准。

---

## 0. 一句话结论

SnowAir 的底层**不神秘**：C++20 + 自绘 Ling（Windows.UI.Composition + Yoga）+ Direct2D，主要是把 Windows 现成能力（DXGI 桌面复制、UI Automation、Media Foundation、WinRT OCR、ZXing、libwebp）组合得很顺，再把**标注对象**和**属性栏交互**做细。

它真正值得我们学的，不是渲染引擎，而是这四样：

1. **标注对象族**：气泡/爆裂、序号、尺寸、角度、文字描边、连线箭头等；
2. **属性栏的三态样式**：初始默认 / 本次会话记忆 / 选中对象临时预览，取消选中能还原；
3. **截图管线**：DXGI + HDR 色调映射 + GDI 回退、工作线程抓屏、抓完再铺盾牌；
4. **图标与布局的产品化**：悬停子工具、初始属性表、工具栏布局编辑器、命令键分层。

InkTeach 的渲染底座（分块缓存、脏矩形、AOT、湿墨、空间索引）比 SnowAir 更强，
**不要换底层**；把 SnowAir 的“标注层”和“交互细节”增量搬过来即可。

---

## 1. 研究范围与硬指标

| 项 | 数字 / 事实 |
|---|---|
| 版本 | SnowAir Lite v0.1.15（v0.1.14 的下一版，标注功能一致） |
| 可执行文件 | 4,288,512 字节（约 4.09 MiB），单文件、静态 CRT |
| SHA-256 | `7DE597F03EBDC557D5BF71FD4BD502D8949A1F48D8D17ACA3E07497451E10AE1` |
| 源码规模 | 193 个 `.cpp/.h/.hpp`，61,627 行 |
| 模块行数 | `Win/` 31,299；`Tool/` 9,836；`Shape/` 8,343 |
| 最大文件 | `WinCap.cpp` 4,352 行；`WinSettingHistory.cpp` 2,769；`ToolSub.cpp` 2,593 |
| 技术栈 | C++20、Ling GUI、Windows.UI.Composition、Yoga、Direct2D |
| 构建 | VS 2026 v145、静态 CRT（`MultiThreaded`）、全程序优化、按体积/速度分档 |
| 依赖 | `dwmapi`、`uxtheme`、`windowsapp`、`mf*`、`uiautomationcore`、`oleacc`、`libwebp`、`ZXing`、`Yoga`、`Ling` |
| 自检 | `--selftest` 通过，12 项纯逻辑检查，退出码 0 |
| 托盘待命内存 | 约 15.2 MB 工作集 / 22.2 MB 私有 / 13 线程（**未初始化图形设备**，不能直接和 InkTeach 常驻 93 MB 比） |

> 说明：源码里的 `deps/Ling` 不随仓库发布，所以**不能从源码直接构建**；
> 只能读 SnowAir 本体代码，UI 框架本身是黑盒。开源范围是 Lite；专业版是另一套 Qt6，不在仓库里。

---

## 2. 底层拆解

### 2.1 技术栈：C++20 + Ling + D2D

- Ling 基于 **Windows.UI.Composition** 做窗口/合成，Yoga 做 flex 布局，Direct2D 负责绘制。
- 每个窗口（截图、设置、贴图、工具栏、弹层）都是自绘；没有 MFC/Qt 控件。
- 静态 CRT + 单 exe，所以 4 MB 级、无需运行库；这是“教室机拷 U 盘就能跑”的路线。
- 和 InkTeach 的 D2D + DirectComposition 是同一类路线，只是语言/UI 框架不同；
  **没有必须换栈的理由**。

### 2.2 启动：托盘待命，按需初始化

- 不带参数启动只挂托盘，不建窗口、不初始化图形设备（`main.cpp` / `App::App`）。
- `F1` 或托盘单击才进截图；`--enter=...` 是一次性入口，只对本次会话生效。
- `--selftest` 在 `App::init()` 之前跑，不碰图形设备。
- 设置中心 8 页：全局热键 / 快捷翻译 / 系统按键 / 外观 / 功能 / 截图历史 / 插件 / 关于。

**可学**：可选的重资源（PDF、OCR、录屏、AI）延迟初始化；不要让主流程为低频功能付启动成本。
**不必学**：托盘待命 15 MB 不能直接当目标——InkTeach 是常驻覆盖层，图形设备本来就要常开。

### 2.3 截图管线：DXGI/HDR + 工作线程 + 盾牌

入口在 `Win/ScreenCapture.cpp` 与 `Win/WinCap.cpp`：

1. **抓屏放在工作线程**：热键回调立即返回，避免系统认为应用“忙”而闪忙光标；
   抓完再回 UI 线程建覆盖窗（`WinCap::init`，约 322–363 行）。
2. **先抓屏、后铺盾牌**：盾牌是置顶、可收鼠标的窗口；先铺会顶掉光标下目标窗口的
   hover 态（网页 tooltip / 菜单高亮），抓到的底图就缺这些效果。
3. **HDR 感知**：优先 DXGI 桌面复制，向系统要 `R16G16B16A16_FLOAT`（scRGB），
   在软件里做 HDR→SDR 色调映射；普通屏仍要 BGRA8；失败回退 GDI。
4. **全屏覆盖窗**：静止底图 + 同表面标注；选区遮罩、控点、信息栏、标注都在同一张
   D2D 表面上画，导出时按选区裁切。
5. **元素拾取**：`CutMask.cpp` 用 `IUIAutomation` 做“悬停找窗口/元素”，带缓存请求、
   跨进程调用超时预算、驻留细化（80ms 停稳后重查，最多 5 次）、桌面图标特判。
6. **多屏/DPI**：选区、控点、信息栏都按物理像素/每屏 DPI 处理；录屏还有“单块屏”约束。
7. **长截图/录屏**：长截图自动发滚轮、拼帧、侧栏预览、可在长图上继续标注；
   录屏走 Media Foundation（MP4 H.264/H.265）+ GIF/WebP/APNG，支持系统声/麦克风/暂停。

**可学**：HDR/DXGI 抓屏、工作线程抓屏、抓完再铺盾牌、UIA 元素拾取（按需）。
**注意**：UIA 元素拾取是 SnowAir 里最复杂的部分之一；不要一上来做全量元素树，
先做 `WindowFromPoint` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 的窗口级，
UIA 只作为可选增强。

### 2.4 标注引擎：ShapeBase + AnnotHost + History

#### ShapeBase：所有标注的统一契约

`Shape/ShapeBase.h` 把“一个标注”该回答的问题收在一处：

- `paint(ctx)`：怎么画；
- `applyStyle()` / `syncToolFromShape()`：工具样式 → 对象、对象样式 → 属性栏；
- `mouseDown/Move/Up/Wheel`、`setCursor`、`hitErase`：交互与命中；
- `paintDragger`、`editSnapshot` / `applyEditSnapshot`、`translateBy`：选中、缩放、旋转、微调；
- `isViewportFilter()` / `isEphemeral()`：水印/包浆这类“视口滤镜”、渐隐笔这类“临时笔迹”；
- `isLinkTarget` / `hitLinkBody` / `linkEdgePoint` / `syncLinks`：连线箭头吸附到矩形/椭圆边缘。

这套契约的价值：**加一种标注，只需实现这几个问题**；选中、拖动、缩放、旋转、撤销、
属性回显、导出都能复用同一条路径。SnowAir 的 `ShapeRect/ShapeEllipse/ShapeArrow/ShapeLine/
ShapeText/ShapeNumber/ShapeMosaic/ShapeHighlight/...` 都是这个基类的子类。

#### AnnotHost：一个宿主，五种表面

`Win/AnnotHost.h` 的注释写得很清楚：截图窗 / 桌面钉图共用标注宿主；
长截图、录屏、演示画布也复用它。宿主负责：

- 标注坐标 ↔ 客户区坐标（`scale`、`annotToClientPt` / `clientToAnnotPt`）；
- 选中/悬停/草稿/文字编辑状态；
- `paintShapes()` 的绘制顺序、控件层、导出时跳过临时笔迹；
- `history`、属性栏、光标、`refreshFiltersAbove`。

**可学**：标注层不要和某一个窗口绑死；宿主只提供“坐标、底图、刷新、撤销”，
标注对象只回答“怎么画、怎么命中、怎么改”。InkTeach 的 `InkObject` 已经是这个思路，
但可以把 SnowAir 的 `applyStyle/syncToolFromShape/editSnapshot/link` 这几条契约补进去。

#### History：对象快照 + 墓碑

`History.cpp`：

- `shapes` 是 `vector<unique_ptr<ShapeBase>>`；撤销不是删对象，而是把 `isUndo=true`（墓碑），
  重做再翻回来；新操作时 `removeUndoShape()` 把尾部墓碑清掉。
- 几何拖动/缩放/旋转：`ShapeEditStep` 记录 before/after 快照；
- 一次文字编辑会话：`addTextEditStep` 把整段文字 + 几何作为一步；
- 白板改背景尺寸：`ShapeBoardResize` 也是“一步对象”；
- 线上的尺寸/角度文字与宿主线是一个整体：撤销/删除会一起走。

**可学**：一步撤销的粒度按“用户动作”而不是“数据变化”；文字编辑会话算一步；
线 + 线上文字算一组。
**不要照搬**：SnowAir 的 History 是会话级、线性扫描；InkTeach 有分页/PPT 隔离，
撤销栈必须按页/按文档，不能做成全局一条。

#### 渲染：全量重画，没有分块缓存

`AnnotHost::paintShapes()` 每帧遍历所有 `shapes` 并 `paint()`；命中也是线性扫描。
对截图标注（几十个对象）足够，但**不适合 InkTeach 的一万笔**。
InkTeach 的 256px 分块 + 脏矩形 + 空间索引是正确路线，继续保留。

### 2.5 属性栏与交互：ToolCap + ToolSub + ToolPicker

- `ToolCap`：截图主工具条。槽位：显示光标、矩形、箭头、画笔、文字、序号、马赛克、橡皮、
  撤销、录屏、贴图、OCR、翻译、长截图、保存、关闭、剪贴板等。
- `ToolSub`：当前工具的属性栏。按工具切换内容：矩形（填充/虚线/圆角/多边形边数/星形角数）、
  箭头（5 种箭头/虚线/圆头/粗细/颜色）、标注（直线/波浪/气泡/角度/尺寸/爆裂）、
  画笔（普通/软笔/虚线/荧光笔/渐隐）、文字（字号/粗斜体/描边）、序号（数字/字母/Emoji/自定义文字）、
  马赛克（马赛克/模糊）、高亮、水印、包浆、橡皮、贴图透明度、白板、录屏。
- `ToolPicker`：主栏图标悬停弹出子工具气泡（箭头槽：箭头/标注；马赛克槽：马赛克/包浆/水印/高亮；
  OCR 槽：识别/二维码）。气泡 120ms 延迟关，主栏↔气泡之间留“通道”不关，
  点按关闭后同一按钮要移开再停悬才重开。
- `ToolNestedPanel` / `ToolColorPanel`：二级/三级浮层（描边色、颜色面板），独立顶层窗口，
  避免被宿主客户区裁剪。

**最值得学的是“三态样式”**（`AnnotHost::presentSelectedAnnotStyle` + `ToolSub`）：

1. **初始默认**：设置里每种工具的默认形态/数值（`ToolInitialDefs.h` 一张表驱动设置预览、
   工具栏图标、停显文案）。
2. **本次会话记忆**：用户刚用过的颜色/粗细/形态（可选）。
3. **选中对象临时预览**：选中一个已有标注时，`beginShapeStylePreview()` 先快照工具状态，
   把对象样式载入属性栏；取消选中 `endShapeStylePreview()` 整体还原；
   如果用户用这套样式继续画了新标注，`commitShapeStylePreview()` 才真正采纳。

这样不会出现“我改了一下选中的气泡颜色，下一笔也变成气泡色”的污染。
`ShapeBase::applyStyle()` 统一把当前工具样式套到选中对象，`syncToolFromShape()` 反向回显；
所有标注共用一条路径，不再按类型各写分支。

**其他可学的小交互**：

- 右键逐层退出：文字编辑 → 草稿 → 选中 → 属性开关 → 工具 → 选区 → 退出；
  每次只退一层。
- 再点已选中的工具 = 关闭工具和属性栏；取消选中恢复默认属性。
- 点未选中的标注即可拖动；拖边/缩放要松开后重新在边缘按下，避免同一手势误触发。
- 选中框角外 4–26px 是旋转环，不额外画旋转柄；四角内侧有圆角控制点。
- 序号工具支持数字/字母/Emoji/任意中文；自动递增，也可手填。
- 尺寸显示实际厘米（按显示器物理 DPI），角度三点绘制并实时显示 0–360°。
- 文字描边：先画轮廓再画填充，填充盖住内侧，只留外侧描边；投影上更清楚。
- 线上文字会“挖掉”底图让线断开，避免文字压线；这个技巧依赖同表面底图，
  InkTeach 覆盖层拿不到 PPT 底图，应改成文字描边/光晕。

### 2.6 数据与工程：配置、命令键、布局、测试

- **配置**：`Setting.cpp` 写 `config.json.tmp` → `MoveFileEx` 原子替换；解析失败保留 `.bad`，
  有 `.bak` 可回退；exe 同目录放空 `config.json` 可切便携模式。
- **命令键**：`CommandKeys.h/.cpp` 把“全局热键”和“会话内命令键”分开：
  - `Scope::Session` 只在本软件窗口有焦点时生效；`Scope::Global` 只在对应会话期间临时注册；
  - `Surface` 掩码（截图/录屏/全屏画布）决定同一键位在不同工具栏是否可以复用；
  - 命令 id 与工具栏槽位 id 分离，避免布局持久化把两者混用；
  - 冲突判定按“界面掩码有无交集”，不是“场景是否相同”。
- **工具栏布局**：`ToolbarStore` 用 active/inactive 两个列表存布局，稳定 id；
  旧配置里的 `ellipse` 被合并时自动清掉，新槽位（白板/录屏）按语义位置补进去；
  “恢复默认”是删除覆盖项，不是写一份等于默认的配置。
- **初始属性**：`ToolInitialDefs.h` 一张表定义每种工具的形态、数值范围、默认值、图标；
  拖到第一位的形态 = 默认形态；设置预览、工具栏图标、停显文案共用这张表。
- **测试**：`SelfTest.cpp` 只有 77 行、12 项纯逻辑检查（RoundBox/LinkGeom/颜色打包）；
  **没有 UI 自动化**。InkTeach 的 60+ 项功能/性能自检反而更成熟，不要反向学。

### 2.7 代码质量：优点与问题

**优点**

- 注释写“为什么”，带实测值/踩坑记录（120ms、4–26px、物理 DPI、HDR 先抓后铺盾牌等）。
- 单一职责的“表”做得较好：初始属性表、命令键表、工具栏布局、图标码表。
- 对 Windows 边角情况有处理：多屏 DPI、HDR、受保护内容、UIA 超时、录屏单屏约束、
  配置损坏回退。
- 静态 CRT + 单 exe + 按需初始化，产品化很务实。

**问题（不要照搬）**

- `WinCap.cpp` 4,352 行、`ToolSub.cpp` 2,593 行，巨型窗口类，职责过多；
  InkTeach 应保持引擎/UI 分层，拆成多个 presenter/service。
- 标注对象直接持有 `AnnotHost*`、直接调 `win->refresh()`/`win->dpi`，
  模型和窗口耦合；InkTeach 的 `InkObject` 是数据对象，更干净。
- 大量 `dynamic_cast` 和裸指针；`clearAllShapes` 的注释专门提醒悬垂指针风险。
  InkTeach 有 `StrokeKind` 枚举，可以用 switch/类型表，AOT 也更友好。
- 全量重画 + 线性命中；对截图够用，对一万笔不够。
- `physicalDpi()` 只取主屏 `GetDeviceCaps`，多屏不同物理密度时厘米读数会偏；
  搬的时候要按每块屏取。
- 样式修改（颜色/线宽）大多不进撤销栈；InkTeach 应把“对象属性修改”也做成一步撤销。
- 测试薄；UI 交互靠人工。

---

## 3. 功能盘点

| 模块 | SnowAir 能力 | 对 InkTeach 的价值 |
|---|---|---|
| 截图 | 区域/全屏、悬停找窗口/元素、选区调整、锁比例、圆角/阴影、放大镜/取色、显示光标、重复上次区域、自动保存、历史 | 中：HDR/DXGI、元素拾取、放大镜可择机学；不要做独立截图编辑器 |
| 标注 | 笔/荧光笔/渐隐笔、5 种箭头、直线/波浪/气泡/角度/尺寸/爆裂、矩形/椭圆/多边形/星形、文字+描边、序号（数字/字母/Emoji/自定义）、马赛克/模糊、包浆/水印/高亮、橡皮、撤销/重做、连线箭头、样式回显 | **高：主要学习对象** |
| 长截图 | 自动滚轮、拼帧、侧栏预览、长图上继续标注、保存/复制/贴图 | 低：不是课堂主场景 |
| 录屏 | MP4 H.264/H.265、GIF/WebP/APNG、系统声/麦克风、暂停、按键浮层、光标、单屏约束 | 低：不做 |
| OCR/二维码/翻译 | WinRT OCR + PP-OCR 插件、ZXing、Bergamot 离线 + 微软/谷歌/有道/百度 | 低/可选：可作为“识别 PPT 文字”的远期能力 |
| 贴图/剪贴板/晾衣架 | 钉图、透明度/边框/阴影/OCR、点击穿透、剪贴板历史、截图/便签挂到屏幕顶端 | 低：InkTeach 有收藏/图片批注，不重复 |
| 演示画布 | 全屏实时画布、白板、图片/PPT 背景、标注、录屏、贴边自动隐藏、鼠标穿透 | 低：InkTeach 本身就是覆盖层/白板 |
| 历史 | 截图/录屏历史、标签、时间筛选、搜索、保留期、回收站、悬停预览 | 中：标签/筛选/回收站思路可参考，但对象不同 |
| 设置 | 深浅/跟随系统/自定义主题、工具栏布局编辑器、初始属性、全局热键、系统按键、插件、5 语言、检查更新 | **高：工具栏布局/初始属性/命令键** |
| 基础 | 托盘、单实例、命令行、便携配置、自动更新、原子写配置 | 中：原子写/便携模式可学；更新 InkTeach 已成熟 |

---

## 4. 我们能学什么（按优先级）

### P0：直接补“标注”短板

#### 4.1 标注对象族：先做 5 个，教学价值最高

| 新对象 | 教学场景 | 复用现有 | 建议优先级 |
|---|---|---|---|
| **序号徽章** | 步骤 1/2/3、实验步骤、错题标记 | `TextBoxData` + 圆形/圆角矩形 | P0（最便宜） |
| **气泡/爆裂** | 解释、提示、易错点、旁注 | `TextBoxData` + 外框（圆角+尾巴/爆裂轮廓） | P0 |
| **尺寸** | 几何边长、物理长度、比例 | 箭头 + 中点文字；按 DPI 换算 cm | P0 |
| **角度** | 几何角、受力方向、旋转角 | 三点折线 + 弧 + 实时读数 | P0 |
| **文字描边** | 投影/深色背景上保证可读 | 文字渲染加轮廓色/宽度 | P0（渲染层小改） |
| **连线箭头** | 受力图、流程图、关系图、标注图 | 箭头 + 目标形状边缘吸附 | P1（要稳定 ID） |
| **多边形/星形** | 几何、装饰、重点框 | 矩形参数化（边数/角数 3–20） | P1 |
| **圆角 0–100%** | 圆角框、标签、按钮样式 | 矩形参数 | P1 |
| **高亮/马赛克/水印** | 截图打码、重点高亮 | 需要底图采样；覆盖层难 | P2/不做 |

> InkTeach 已有 22 种数学图形（抛物线、双曲线、圆锥曲线、立体几何…），这是 SnowAir
> 没有的优势；新增标注对象是**补短**，不是替换。

#### 4.2 三态样式：解决“改选中对象污染下一笔”

SnowAir 的做法：

- `beginShapeStylePreview()`：快照当前工具状态；
- `syncToolFromShape()`：把选中对象的颜色/线宽/字号/形态载入属性栏；
- 取消选中 `endShapeStylePreview()`：整体还原；
- 用户用这套样式继续画了新标注 `commitShapeStylePreview()`：才真正采纳；
- `ShapeBase::applyStyle()`：属性栏改动统一套到选中对象，所有类型共用一条虚函数。

InkTeach 落地建议：

- 在 `InkUi` 增加 `ToolStyleScope`：`Default` / `Session` / `SelectionPreview`；
- 选中变化时只改 UI 状态，不写 `Engine` 的默认笔样式；
- 属性栏改动走 `IEngineCommands.ApplyStyleToSelection`，同时记一步撤销；
- 取消选中/切换工具/删除对象时可靠还原（用状态快照，不靠散落的布尔值）。

#### 4.3 撤销分组：一步 = 一个用户动作

SnowAir 的做法：

- 画一个对象 = 一步；
- 拖动/缩放/旋转已有对象 = 一步（before/after 几何快照）；
- 一次文字编辑会话 = 一步（文字 + 几何）；
- 线 + 线上文字 = 一组，删/撤销一起走；
- 白板改背景尺寸 = 一步“空对象”。

InkTeach 落地建议：

- 在 `InkEngine` 增加 `CompoundEdit`（Begin/Commit/Rollback），内部收集多个子操作；
- 新标注的“画 + 自动文字（尺寸/角度）”作为一个 compound；
- 文字编辑会话、连线箭头 + 标签、批量样式修改都用 compound；
- 保持按页/按文档的撤销栈，不要做成 SnowAir 那样的会话全局栈。

#### 4.4 悬停子工具 + 初始属性表：解决“图标由少到多”

SnowAir 的做法：

- 主栏只放 7 个绘图槽；一个槽可挂多个子工具，悬停弹气泡选择；
- 当前子工具的图标成为槽位图标；停显文案仍是工具名（身份不变）；
- 气泡 120ms 延迟关，主栏↔气泡之间留通道；点按关闭后要移开再停悬才重开；
- `ToolInitialDefs.h` 一张表定义每个工具的形态、数值范围、默认值、图标；
- 设置里的“初始属性”拖拽排序，第一位 = 默认形态；设置预览/工具栏/停显共用同一张表。

InkTeach 落地建议：

- 给 `形状/标注` 增加 `ToolFamily`：一个主槽 + 子工具列表（如“标注”槽下：气泡/序号/尺寸/角度）；
- `ToolCatalog` 作为唯一来源：稳定 key、图标、名称、默认形态、数值范围、快捷键、兜底入口；
- 主栏图标跟随“当前子工具”，但 tooltip/身份不变；设置里可拖拽排序；
- 迁移沿用现有 `cellsVersion` 规则：旧格全钉、新格不钉，外观不变。

#### 4.5 连线箭头：教学图里的“关系”能力

SnowAir 的 `ShapeBase` 连线契约：

- 矩形/椭圆可作为 `isLinkTarget`，提供 `hitLinkBody`、`linkCenter`、`linkRotation`、
  `linkEdgePoint(localAngle)`；
- 箭头/线段有 `hasLink`、`linkStartPoint`、`linkButtonPos`、`hitLinkButton`、`syncLinks`；
- 目标移动/缩放/旋转时，端点重新贴回目标边缘；
- 线中点有“叉”，点它删掉这条线。

InkTeach 落地建议：

- `Stroke` 增加 `LinkTargetId`（稳定 ID）、`LinkEnd`（起点/终点）、`LinkSide`；
- 变换提交时统一 `SyncLinks()`，不要在每个拖动分支里各写一份；
- 序列化存目标 ID，不存指针；目标删除时自动脱钩；
- 先支持矩形/椭圆，再扩到多边形/图片。

### P1：截图与工程化

#### 4.6 HDR/DXGI 抓屏

InkTeach 当前截图/取色走 `BitBlt`（`src/InkEngine/Engine.cs`、`Native.cs`）。
SnowAir 的 `ScreenCapture.cpp` 用 DXGI 桌面复制 + FP16 scRGB + 软件色调映射，
失败回退 GDI。对 HDR 笔记本/投影仪，这能避免截图过曝/发灰。

落地建议：

- 用 Vortice.DXGI 的 `IDXGIOutput5::DuplicateOutput1` 要 `R16G16B16A16_FLOAT`；
- 按 `DXGI_OUTPUT_DESC1` 的 `MaxLuminance`/`SDRWhiteLevel` 做色调映射；
- 失败/受保护内容/多 GPU 混合时回退 BitBlt；
- 选区/取色仍用同一张冻结帧，保证“看到的就是取到的”。

#### 4.7 UIA 元素拾取 + 放大镜/取色

SnowAir 的“悬停找元素”用 `IUIAutomation`，带缓存、超时、驻留细化、桌面图标特判。
它很强，但也是复杂度最高的部分。

落地建议：

- 第一阶段只做窗口级：`WindowFromPoint` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`；
- 第二阶段再加 UIA 元素级，必须放到工作线程/带超时，不能卡 UI；
- 放大镜/取色从冻结帧采样，显示 Hex/RGB，`Ctrl+C` 复制；这个便宜且实用；
- 双击空白 = 复制并退出，只在“有标注 + 没点在对象/控点/信息栏”时触发。

#### 4.8 命令键分层

SnowAir 的 `CommandKeys` 值得对照 InkTeach 的快捷键总表：

- `Scope::Session` vs `Scope::Global`：不要把撤销/复制/完成注册成全局键；
- `Surface` 掩码：同一个键在不同工具栏可以有不同的命令；
- 命令 id 与工具栏槽位 id 分离；
- 冲突判定按掩码交集；只读固定键只挡改绑、不挡派发；
- “恢复默认”删除覆盖项，而不是写一份等于默认的配置。

InkTeach 已有快捷键总表和冲突实测；可以补“按界面掩码判定冲突”和“命令/槽位分离”，
避免以后工具栏变多后键位打架。

#### 4.9 工具栏布局编辑器 + 迁移

SnowAir 的 `ToolbarStore`：

- 每种表面（截图/录屏/演示/贴图）独立的 active/inactive 列表；
- 稳定 id；旧 id 合并时自动清理；新槽位按语义位置补入；
- 恢复默认 = 删除覆盖项；`hasSavedLayout` 决定运行时是否套用。

InkTeach 已有钉住格和 `cellsVersion` 迁移；可以借鉴“active/inactive 两列表”替代
一堆布尔值，排序/迁移/恢复默认更简单；同时保留“身份不能变”的稳定 key。

#### 4.10 配置原子写 + 便携模式

- 写临时文件再 `MoveFileEx` 替换，避免半截 JSON；
- 解析失败保留 `.bad`，有 `.bak` 回退；
- exe 同目录空 `config.json` 切便携模式（U 盘/教室机）。

InkTeach 已有备份/恢复；把配置写入改成原子替换是低成本加固。

### P2：不建议现在做

- **长截图**：自动滚轮/拼帧/预览是一整套独立编辑器，和课堂批注关系弱。
- **录屏**：Media Foundation + 多编码器 + 多屏约束，维护成本高。
- **OCR/二维码/翻译**：可作为“识别 PPT 文字”的远期能力，但不是当前主线。
- **贴图/剪贴板/晾衣架**：InkTeach 有收藏/图片批注，不要重复造。
- **演示画布**：InkTeach 本身就是覆盖层/白板，不需要再做一个全屏画布。
- **插件 ABI**：SnowAir 用 C ABI 把“晾衣架”放出去；InkTeach 现阶段不需要插件化，
  但可以把实验性 AI 功能隔离开。

---

## 5. 不建议照搬

| 不搬 | 原因 |
|---|---|
| C++/Ling 重写 | InkTeach 的 .NET 10 AOT + Vortice + D2D/DComp 已经跑通；换栈成本巨大，收益不成比例 |
| 全量重画 | SnowAir 标注对象少；InkTeach 一万笔必须靠分块/脏矩形/空间索引 |
| 巨型窗口类 | `WinCap.cpp` 4,352 行、`ToolSub.cpp` 2,593 行；InkTeach 应保持引擎/UI 分层 |
| 会话全局撤销 | InkTeach 有 PPT 分页/文档隔离；撤销必须按页/按文档 |
| 独立截图标注编辑器 | InkTeach 已有“截图落成对象 + 直接在画布上标”的替代路；不要为了像微信再加一套临时编辑器 |
| UIA 全量元素树 | 复杂、易受目标应用影响；先窗口级，UIA 作为可选增强 |
| 长截图/录屏/OCR/翻译/贴图/晾衣架 | 产品面铺得太广，偏离课堂批注主线 |
| 77 行自检 | InkTeach 的 60+ 项自检更强；不要降低测试标准 |
| 主屏物理 DPI 换算 | 多屏会偏；应按每块屏取物理 DPI |

---

## 6. 建议落地顺序

### 第一阶段：标注对象 + 样式/撤销（最贴近用户反馈）

1. **序号徽章**（最便宜，先跑通“新对象 + 属性栏 + 撤销”）；
2. **气泡/爆裂**（复用 `TextBoxData`，加外框/尾巴/圆角）；
3. **尺寸/角度**（复用箭头/线段手柄，加自动文字）；
4. **文字描边**（渲染层小改，投影可读性收益大）；
5. 同步做 **三态样式** + **CompoundEdit**，否则新对象越多，样式/撤销越乱。

### 第二阶段：关系与图标治理

6. **连线箭头**（稳定 ID + 边缘吸附 + 目标变换同步）；
7. **多边形/星形 + 圆角**（矩形参数化）；
8. **ToolFamily / ToolCatalog / 悬停子工具**（解决图标增长）；
9. **工具栏布局编辑器 + 迁移**（active/inactive 两列表）。

### 第三阶段：截图与工程化（按需）

10. **DXGI/HDR 抓屏**（有 HDR 教室机/投影仪需求再做）；
11. **窗口级元素拾取 + 放大镜/取色**（先窗口级，UIA 可选）；
12. **命令键分层**（工具栏变多、键位冲突变多时再做）；
13. **配置原子写/便携模式**（低成本，可随时做）。

---

## 7. 照搬边界（按最高原则写清）

- **参考产品/版本/来源**：SnowAir Lite v0.1.15，commit `2fab020`，
  <https://github.com/CM-idea/SnowAir>，Apache-2.0。
- **照搬逻辑（不复制代码）**：
  - 标注对象契约：`paint / hit / handles / applyStyle / syncToolFromShape / editSnapshot / link`；
  - 三态样式：默认 / 会话 / 选中预览 + 快照还原；
  - 撤销粒度：一个用户动作一步，文字会话/线+文字成组；
  - 悬停子工具：一槽多子工具 + 120ms 延迟关 + 通道保持 + 点后抑制；
  - 初始属性表：一张表驱动设置预览/工具栏图标/停显/默认形态；
  - 工具栏布局：active/inactive + 稳定 id + 迁移 + 删除覆盖项恢复默认；
  - 命令键：Session/Global + Surface 掩码 + 命令 id/槽位 id 分离；
  - 配置：临时文件 + 原子替换 + `.bak`/`.bad`；
  - 抓屏：工作线程 + 先抓后铺盾牌 + DXGI/HDR + GDI 回退。
- **不搬**：C++/Ling 栈、全量重画、巨型窗口类、会话全局撤销、独立截图编辑器、
  UIA 全量元素树、长截图/录屏/OCR/翻译/贴图/晾衣架整条产品线、77 行自检标准。
- **我们的差异**：
  - 保留 InkTeach 的分块缓存/脏矩形/空间索引/AOT/湿墨；
  - 保留 22 种数学图形与 PPT 分页隔离；
  - 标注对象进 `InkObject` 数据模型，不进窗口类；
  - 覆盖层拿不到 PPT 底图，线上文字用描边/光晕而不是“挖背景”；
  - 尺寸/角度按每块屏物理 DPI 换算；
  - 样式修改也进撤销，和现有操作条口径一致。

---

## 8. 证据索引

| 结论 | 源码位置 |
|---|---|
| 技术栈/启动/托盘/演示画布 | `Native/ARCHITECTURE.md`、`Native/Src/main.cpp`、`App.cpp` |
| 全屏截图窗 + 同表面标注 | `Native/Src/Win/WinCap.cpp`、`WinCap.h` |
| 抓屏工作线程 + 先抓后铺盾牌 | `Native/Src/Win/WinCap.cpp` 约 322–363 行 |
| DXGI/HDR + GDI 回退 | `Native/Src/Win/ScreenCapture.cpp` |
| UIA 元素拾取 | `Native/Src/Win/CutMask.cpp` |
| 标注对象契约 | `Native/Src/Shape/ShapeBase.h` |
| 标注宿主/选中/样式回显 | `Native/Src/Win/AnnotHost.h`、`AnnotHost.cpp` |
| 撤销/编辑步/文字步 | `Native/Src/History.h`、`History.cpp` |
| 属性栏/三态样式 | `Native/Src/Tool/ToolSub.h`、`ToolSub.cpp`（`beginShapeStylePreview` 约 1546 行） |
| 悬停子工具/120ms | `Native/Src/Tool/ToolCap.cpp`（`onHoverMove` 约 596 行） |
| 初始属性表 | `Native/Src/Tool/ToolInitialDefs.h` |
| 工具栏布局/迁移 | `Native/Src/Tool/ToolbarStore.h`、`ToolbarStore.cpp` |
| 命令键分层 | `Native/Src/CommandKeys.h`、`CommandKeys.cpp` |
| 配置原子写 | `Native/Src/Setting.cpp`（`writeConfigAtomic` 约 116 行） |
| 自检 | `Native/Src/SelfTest.cpp`（77 行，12 项） |
| 气泡/爆裂/文字描边 | `Native/Src/Shape/ShapeText.h`、`ShapeText.cpp` |
| 角度/波浪/尺寸 | `Native/Src/Shape/ShapeLine.h`、`ShapeArrow.h` |
| 序号 | `Native/Src/Shape/ShapeNumber.h` |
| 连线几何 | `Native/Src/Shape/LinkGeom.h`、`ShapeBase.h` 的 link 虚函数 |
| 长截图 | `Native/Src/Win/CapLong.h`、`CapLong.cpp` |
| 录屏 | `Native/Src/Win/CapVideo.h`、`VideoMp4.hpp`、`VideoGif.hpp` |
| OCR/翻译 | `Native/Src/Ocr/`、`Native/Src/Translate/` |
| 插件/晾衣架 | `Native/Src/Plugin/` |

---

## 9. 参考

- SnowAir 仓库：<https://github.com/CM-idea/SnowAir>
- SnowAir Releases：<https://github.com/CM-idea/SnowAir/releases>
- 本地源码：`downloads/SnowAir-src`
- 本地程序：`downloads/SnowAir-Lite.exe`
- InkTeach 现有架构：`调研/架构-分层与规则.md`、`调研/底层设计-笔迹对象模型.md`
- InkTeach 现有图形：`src/InkEngine/Model.cs` 的 `StrokeKind`
