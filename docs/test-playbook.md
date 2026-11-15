# 测试手册（给 AI 和重构用）

> 先选层，再跑命令。**默认只跑 L1 或 L2 的一条；L3 只在发版前或全仓重构收尾时跑**
> （AGENTS.md §0.1）。

## 1. 三层

| 层 | 入口 | 建窗口 | 典型耗时 | 什么时候跑 |
| --- | --- | --- | --- | --- |
| L1 纯逻辑 | `.\run.ps1 --unittest [过滤词]` | 否 | 20–100 ms | 改序列化 / EditAction / 识别 / 表达式 / 设置迁移 / 布局几何 |
| L2 模块专项 | `.\run.ps1 --xxxtest` | 是（无头/隐藏） | 0.5–20 s | 改哪个模块跑哪条；命令见第 3 节 |
| L3 功能套件 | `tools/bench/Run-FuncSuite.ps1` | 是 | 5–8 分钟（46 条） | 发版前；全仓目录重构前后各一次 |

## 2. L1：`--unittest`

```powershell
.\run.ps1 --unittest                   # 全部
.\run.ps1 --unittest EditAction        # 名字含 EditAction 的
```

- 测试文件：`src/InkTeach/SelfTests/UnitTests.*.cs`。
- 写法：`[UnitTest] private static void 模块_行为_预期()`。
- 断言：`Check.True / Equal / Near`，失败自动带 `File:Line`。
- 隔离：`UnitTestRunner.ApplyIsolation()` 把 settings / 自动存档 / PPT / 文档 / 历史 /
  日志全部指到 `%TEMP%\inkteach-unittest`。**测试不许碰用户真实数据**。
- 不建窗口、不碰 D2D、不读网络。

### 2.1 Golden Master（行为快照）

用于“重构前后效果一样”的硬证据。只在 L1 放三类：

1. **序列化字节**：固定 `InkDocument` → `InkSerializer.Save` → SHA256 + 往返字段。
2. **图形识别**：10 条固定采样点 → `ShapeRecognize` → `StrokeKind` + 控制点坐标（±1e-3）。
3. **布局几何**：3 档窗口尺寸 → `LibraryLayout` / `UiScale` → 行列数与卡片 `RectF`。

快照变了先问“是不是有意改的”；有意改才更新期望值，并在 commit 说明里写原因。

## 3. L2：按功能分组的专项命令

| 分组 | 命令 | 说明 |
| --- | --- | --- |
| 坐标 / 相机 | `--coordtest`、`--cameratest` | 画在哪儿显示在哪儿；滚动只改相机 |
| 图形对象 | `--shapetest`、`--imagetest`、`--capturetest` | 命中、图像对象、截图 |
| 剪贴板 | `--clipboardtest` | 复制成对象 / 给外部一张图 / 智能粘贴 |
| 存档 | `--savetest` | 保存加载往返 + 坏数据 |
| 变换与撤销 | `--transformtest`、`--handletest`、`--edittest`、`--rotatetest` | 手柄 / 取消 / 旋转 |
| 选中与框选 | `--seltest`、`--lassotest` | 框选、套索 |
| 笔迹形状 | `--widthtest`、`--captest`、`--cornertest`、`--selfcross` | 墨量、端点、折角、自交 |
| 输入路径 | `--inputtest`、`--appendtest` | 按下→移动→抬起、续写 |
| 橡皮擦 | `--erasertest`、`--pixelerasetest`、`--dynerasertest` | 整笔 / 像素 / 动态 |
| 光标 | `--cursortest` | 工具 × 设备的落点反馈 |
| 渲染与缓存 | `--tiletest`、`--selftest 8` | 分块、上屏、一万笔 |
| 滚动 / 翻页 | `--wheeltest`、`--scrollwrite`、`--ghosttest`、`--pagetest` | 残影、翻页 |
| 文档 / PPT | `--doctest`、`--ppttest` | 文档页、放映隔离 |
| 导出 | `--iotest` | PNG/JPEG 逐像素 |
| 白板底纹 | `--patterntest` | 线距、空闲 0 开销 |
| 实笔轨迹 | `--trailtest` | 委托墨迹 |
| 穿透 | `--passtest`、`--uitest` | 跨进程点击、界面输入通路 |
| 产品界面 | `--paneltest`、`--shapepaneltest`、`--shapeaudittest`、`--bandactiontest`、`--qrtest` | 球→带、图形面板、动作条 |
| 键位 | `--keytest` | 表 / 冲突 / 落盘 / 注册 |
| 图形动作 | `--snaptest` | 对象吸附 |
| 启动 / 自启 | `--autostarttest`、`--startupcheck` | Run 键、待命态 |
| 停顿成型 | `--dwelltest` | 一笔停一下 → 图形 |
| 识别 | `--inktest` | 一笔墨迹 → 认出什么图形 |
| 收藏 / 图形库 | `--librarytest`、`--libraryshow` | 收藏增删改查 |

新增专项：一个命令一个文件，放 `src/InkTeach/SelfTests/`；命令名必须能从
`Run-FuncSuite.ps1` 的 `$cases` 表里查到。

## 4. L3：功能套件与基线

```powershell
# 重构前 / 发版前：全量，落 summary.json
powershell -ExecutionPolicy Bypass -File tools/bench/Run-FuncSuite.ps1 -OutDir reports\baseline-pre

# 只跑某个分组（重构某个模块时）
powershell -ExecutionPolicy Bypass -File tools/bench/Run-FuncSuite.ps1 -Only 产品界面 -OutDir reports\check-panel

# 重构后对比（退出码 0 = 一致）
powershell -ExecutionPolicy Bypass -File tools/compare-baseline.ps1 `
    -Before reports\baseline-pre\summary.json `
    -After  reports\baseline-post\summary.json
```

判定口径：
- `PASS` / `FAIL` / `TIMEOUT` / `SKIP` / `DATA`（跑完但没判据，视为要修）。
- `compare-baseline.ps1` 只看**新增 FAIL/TIMEOUT、PASS 数减少、用例消失**；
  耗时明显变慢只提示，不判失败。
- 2026-11-15 基线：46 条 **46 PASS**（`reports/baseline-pre/summary.json`）。

### 4.1 已知坑

- 输出文件按 **UTF-8** 读；按 ANSI 读会把“通过/失败”变乱码，整条误判成 `DATA`。
- `$AppRoot` 默认从脚本位置推导（旧版硬编码 `D:\文件集中\code\批注`，换机器跑不了）。
- `passtest` 必须 Normal 窗口启动；其余用 Hidden（见脚本注释）。
- 套件会占用剪贴板 / 前台窗口，跑之前把无关窗口最小化。

### 4.2 索引校验（拆完文件 / 发版前）

```powershell
powershell -ExecutionPolicy Bypass -File tools/check-module-map.ps1   # 地图引用的文件是否存在
powershell -ExecutionPolicy Bypass -File tools/check-section-map.ps1  # section-map 行数是否过期
```

两个都返回 0 才算索引可用；`check-section-map` 报 STALE 时重跑 `tools/gen-section-map.ps1`。

## 5. 写测试的硬规则

1. **一个测试只验一件事**；名字写清“模块_行为_预期”。
2. 失败信息必须包含 expected / actual（`Check` 已带）。
3. 不依赖执行顺序；每条自己造数据、自己清理。
4. 不碰网络、不碰用户真实目录；需要落盘用 `Path.GetTempPath()`。
5. 纯逻辑进 L1；需要窗口/输入/像素的进 L2；不要为了“快”把 E2E 塞进 L1。
6. 修 bug 先补一条会红的测试，再改代码。
