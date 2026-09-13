# InkProbe — 屏幕批注软件的性能验证原型

这是一个用来**验证技术选型**的原型，不是成品。它把候选架构里最关键的几件事
真的做出来并测了数据：透明覆盖层、GPU 合成、笔迹缓存、增量更新、穿透模式。

完整的技术方案调研见 [批注软件技术方案调研.md](批注软件技术方案调研.md)。
底层的对象模型设计见 [底层设计-笔迹对象模型.md](底层设计-笔迹对象模型.md)。
底层与多套界面之间的接入口见 [引擎边界-多界面接入规范.md](引擎边界-多界面接入规范.md)。
手写笔锋的实现与自检见 [手写美化-笔锋实现说明.md](手写美化-笔锋实现说明.md)
（**默认关闭**：用户实测后认为不是必需，算法代码保留，需要时用 `--preset handwriting` 打开）。

> **界面已移除（2026-09-13）**：悬浮工具条那一版观感不满意，已连同
> `src/InkUi` 工程一起移出仓库。引擎侧的界面接入口 `IOverlayUi` 完整保留，
> 现在跑的是无界面宿主（纯笔迹），重做界面时只要新写一个 `IOverlayUi` 实现。

---

## 怎么跑

```powershell
# 交互模式：全屏覆盖层，用全局快捷键操作
dotnet run --project src/InkProbe -c Release

# 自检：自己画一笔、截图验证、跑一万笔基准测试，然后退出
dotnet run --project src/InkProbe -c Release -- --selftest 8

# 交互模式（默认就是等宽笔迹，不做手写美化）
dotnet run --project src/InkProbe -c Release -- --nohud

# 可选：打开手写美化 / 看原始笔迹
dotnet run --project src/InkProbe -c Release -- --nohud --preset handwriting
dotnet run --project src/InkProbe -c Release -- --nohud --rawink

# 实测报告：把内存、性能、穿透的数据全部跑一遍并写入文件
dotnet run --project src/InkProbe -c Release -- --report reports/inkprobe-report.txt

# 内存归因报告：分阶段拆出每一部分占多少内存
dotnet run --project src/InkProbe -c Release -- --memory reports/inkprobe-memory.txt
```

需要 .NET 8 SDK（本机已安装）。首次构建会从 nuget.org 拉取 Vortice 系列包。

## 全局快捷键（Ctrl+Alt+…）

| 按键 | 功能 | 按键 | 功能 |
|---|---|---|---|
| P | 穿透模式开关 | 1 | 笔 |
| Z | 撤销 | 2 | 荧光笔 |
| C | 清空 | 3 | 激光笔 |
| I | 性能面板开关 | 4 | 橡皮擦 |
| **6** | **切换笔迹粗细** | 5 | 框选 |
| B | 一万笔性能基准测试 | | |
| M | 内存/显存探测 | Y | 切换穿透实现方式 |
| X | 退出 | | |

手写笔的反向笔尖会自动切换成橡皮擦（走 `PEN_FLAG_INVERTED`）。

## 低层算法

| 算法 | 位置 | 作用 |
|---|---|---|
| 1€ 滤波器（One Euro） | `Algorithms.cs` | 笔输入平滑：慢速时强去抖，快速时几乎不滤波，兼顾跟手 |
| Ramer–Douglas–Peucker | `Algorithms.cs` | 落笔结束后抽稀，实测 44 → 29 个点 |
| 均匀网格空间索引 | `Algorithms.cs` | 命中测试从 O(笔画数) 降到 O(邻近格子)，实测快 32 倍 |
| 脏矩形批量修补 | `Overlay.cs` | 一次 BeginDraw/EndDraw/Flush 处理所有脏区，避免每个矩形等一次 GPU |

## 自动化测试

都不靠肉眼，靠合成输入 + 截屏数像素：

| 命令 | 验证什么 |
|---|---|
| `--inputtest` | 按下→移动→抬起整条路径，沿路径采样确认处处有墨（不是只有一个点） |
| `--erasertest` | 稀疏采样快速划过，确认不遗漏、且整段拖拽只算一步撤销 |
| `--passtest` | 另起进程开目标窗口，合成真实点击，确认穿透时下层窗口确实收到点击 |
| `--widthtest` | 各档粗细的实测墨量对理论值，检查填充带子会不会出洞 |
| `--selftest` | 渲染验证 + 一万笔基准 |
| `--report` | 全量性能与正确性数据 |
| `--memory` | 内存分阶段归因 |
| `--beautifytest` | 手写美化自检（验速度→粗细、起收笔渐细、宽度不超界、耗时） |
| `--beautifyshowcase` | 把四种笔锋摆出来给人看（含白板背景） |
| `--preset <名字>` | 切换笔锋：precise / handwriting / bold / calligraphy |

---

## 代码结构

| 文件 | 作用 |
|---|---|
| `src/InkEngine/Ui.cs` | 引擎与界面的唯一契约（`IOverlayUi` / `IUiHost` / `IEngineCommands`）|
| `src/InkEngine/Engine.cs` | 引擎本体：消息循环、输入处理、热键、工具状态、界面接入 |
| `src/InkEngine/Overlay.cs` | 覆盖窗口、Direct3D11 + Direct2D + DirectComposition 渲染管线 |
| `src/InkEngine/Model.cs` | 笔画数据、文档、激光笔轨迹 |
| `src/InkEngine/Native.cs` | Win32 / 指针输入 / GDI 截屏的 P/Invoke 声明 |
| `src/InkEngine/InkOptimizer.cs` | 笔迹优化的接入点（`IInkOptimizer`）。核心只有这个接口，没有实现 |
| `src/InkEngine/SpatialGrid.cs` | 均匀网格空间索引，给橡皮擦/框选的命中测试用 |
| `src/InkEngine.Optimize/` | **可选**的笔迹优化层：输入平滑、抽稀、贝塞尔拟合、笔锋。核心不依赖它 |
| `tools/gen-fluent-icons.ps1` | 从上游图标库生成上面的路径数据（可复现）|
| `src/InkProbe/App.cs` | 开发期宿主：自动化测试、基准、实测报告（引擎里不含这些）|
| `src/InkProbe/app.manifest` | 每显示器 DPI 感知（PerMonitorV2） |
| `tools/ApiDump` | 反射列出绘图库的接口签名，写这个项目时的辅助工具 |
| `tools/MemBaseline` | 测量纯 .NET 进程的内存底噪，用于给内存数据做归因 |
| `tools/InkAnalyzerProbe` | 验证 Windows 自带的形状识别能否在普通桌面程序里直接用 |
| `src/InkProbeNative` | 同一个覆盖层的 C++ 原生版，用于量化「换语言能省多少内存」 |

### 三层结构

```
核心 InkEngine          文档 · 输入 · 脏区渲染 · 空间索引 · Win32    ← 不认识下面两层
  ├ InkEngine.Optimize 笔迹优化：平滑 / 抽稀 / 贝塞尔拟合 / 笔锋      ← 可选
  └ 界面实现            IOverlayUi 的实现                             ← 可选
```

核心引擎里**没有任何平滑、拟合、笔锋代码**。它只会画两种东西：原始采样点
连成的等宽带子，或者外部算好交给它的轮廓（`Stroke.Outline`）。想改变观感
就装优化器；不装，量到的性能里就不含这部分成本。

| 开关 | 效果 |
|---|---|
| （默认） | 不装优化器。纯底层，画原始采样点 |
| `--smooth` | 装上笔迹优化器，用来和默认做 A/B 对照 |
| `--rawink` | 显式不装，与默认一致，保留是为了兼容旧命令行 |

---

## 已经做完并且验证过的

- 透明覆盖层能正确盖在桌面和其他程序之上（用截屏找像素的方式自动验证）
- 无边框、置顶、不抢焦点、不出现在任务栏
- 笔 / 荧光笔 / 激光笔 / 橡皮擦 / 框选五种工具，手写笔压感影响线宽
- 穿透模式，三种实现方式都做了对照
- 内容层缓存 + 增量更新（结束一笔只重画那一小块）
- 内置性能面板（帧率、耗时、内存、CPU）和一万笔压力测试
- 引擎与界面已拆分：引擎可独立编译，界面只通过 `IOverlayUi` 接入，
  输入优先给界面、界面画在自己的矩形里（代价约 0.4 ms/帧）
  粗细点开是档位表（一步到位）、穿透后给提示并让出点击
- 手写笔锋：速度→粗细、起收笔渐细、圆头端帽、尖角圆弧、边缘粗糙度，
  四档预设（精确/手写美化/粗笔/书法），参数复刻自 perfect-freehand 官方默认值，
  单笔美化 0.1 ms，15 项量化自检通过

## 还没做 / 没验证的

- **穿透模式下真实点击是否落到下层程序**：自动化的命中测试已经通过，但还需要人工点一下确认
- 手写笔的真实压感、延迟手感（需要一台带压感笔的设备）
- 冻结截图模式、多显示器跨屏、HDR 色彩
- 新界面（悬浮工具条等，见引擎边界文档里的接入说明）
- 荧光笔的真实正片叠底混合（目前是半透明黄色近似）
