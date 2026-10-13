# 第三方内容与许可证

本目录的代码包含以下第三方内容。

---

## Fluent UI System Icons

- **文件**：`src/InkEngine/Icons.Paths.cs`（由 `tools/gen-icons.ps1` 生成）
- **内容**：操作条图标的 SVG 路径数据（`d` 字符串）
- **来源**：https://github.com/microsoft/fluentui-system-icons
- **许可证**：MIT License，Copyright (c) 2020 Microsoft Corporation

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

本项目只使用其 SVG 中的路径数据，不引入它的任何运行时代码。
MIT 是宽松许可：可以商用、可以闭源，只需保留版权与许可声明（本文件即是）。

---

## ink-stroke-modeler（Google，**已按 1:1 移植代码**）

- **文件**：`src/InkEngine/InkStrokeModeler.cs`（上游 `stroke_modeler` 全部核心的 C# 移植）、
  接入层 `src/InkEngine/InkModel.cs`、探针 `src/InkEngine/InkModelProbe.cs`
- **来源**：https://github.com/google/ink-stroke-modeler （main @ `f2388813`，2026-08-02）
- **内容**：wobble 平滑、弹簧位置建模、收笔追赶、触笔状态投影（压力/倾角/方向）、
  StrokeEnd 预测器；算法、运算顺序与默认参数按上游 1:1 保留
- **许可证**：Apache License 2.0，Copyright 2022-2024 Google LLC

```
Copyright 2022-2024 Google LLC

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

移植说明：只做了"外壳适配"（C++ → C#、时间换成 double 秒、absl::Status 换成异常），
算法本体未改；已知差异（Kalman 预测器未移植、参数校验简化）写在该文件头部。
Apache-2.0 与本项目的 GPL-3.0 兼容；分发时须保留上述声明与文件头的许可注释。

---

## google/ink（滑动时间窗输入建模，**已按 1:1 移植代码**）

- **文件**：`src/InkEngine/GoogleInkSlidingWindow.cs`
- **来源**：https://github.com/google/ink （main @ 2026-10-01；文件见该文件头部注释）
- **内容**：`SlidingWindowInputModeler` / `PassthroughInputModeler` / `StrokeInputModeler`
  编排层与相关类型；算法、顺序、默认值（20ms 窗、180Hz 上采样）按上游 1:1 保留
- **许可证**：Apache License 2.0，Copyright 2024-2025 Google LLC（声明同上一节）

---

## dotnet/wpf（墨迹拟合 Bezier + CuspData，**已按 1:1 移植代码**）

- **文件**：`src/InkEngine/WpfInkFit.cs`
- **来源**：https://github.com/dotnet/wpf （`PresentationCore/MS/internal/Ink/Bezier.cs`、`CuspData.cs`）
- **内容**：带误差容限的逼近式贝塞尔拟合 + 尖点检测（`--mean2fit` 用）
- **许可证**：MIT License，Copyright (c) .NET Foundation and Contributors
  （声明文本同本文件上方 Fluent UI 一节的 MIT 全文，仅换版权行）

---

## Xournal++（行为参考，**未复制代码**）

- **文件**：`src/InkEngine/StrokeMotion.cs`（M5 `mean`、M7 `gauss`）、
  `src/InkEngine/PressureSim.cs`（`--simpressure` 模拟压力）、
  `src/InkEngine/Input/PenInput.cs`（缺压回填）
- **来源**：https://github.com/xournalpp/xournalpp
  （`src/core/control/tools/StrokeStabilizer.cpp`、`src/core/gui/inputdevices/PenInputHandler.cpp`）
- **内容**：M5 的"最近 N 点算术平均"思路、M7 的速度高斯权重平均（权重公式、权重 <0.01
  截断、收笔二次样条）；`PressureSim` 的 `inferPressureValue`/`filterPressure` 行为
  （反速度 arctan → EMA → 静止特判 → 下限/倍率）与"个别事件缺压力时沿用上一次有效压力"。
  以上均按上游**行为**自实现；不包含上游代码
- **许可证**：GNU GPL v2 or later（上游）。本项目整体同为 GPL-3.0，行为参考不引入额外义务；
  若未来改为逐行移植代码，需在本文档保留其版权与许可声明

---

## perfect-freehand（行为/公式参考；MIT）

- **文件**：`src/InkEngine/PressureSim.cs`（`--pfpressure` 模拟压力、`--simtaper` 起收笔锥化）
- **来源**：https://github.com/steveruizok/perfect-freehand
  （`packages/perfect-freehand/src/simulatePressure.ts`、`getStrokePoints.ts`、
  `getStrokeOutlinePoints.ts` 等）
- **内容**：`simulatePressure`（`sp = 距离 ÷ 笔宽`、目标 `1 − sp`、每点移动 `sp × 0.275`、
  起笔 0.25）、`streamline` 位置低通（仅用于测速）、起收笔锥化的系数与缓动
  （起点 `t(2−t)`、终点 `1−(1−t)³`、"按距端点路径长 ÷ 锥长"），均按上游行为/公式自实现
- **许可证**：MIT License，Copyright (c) 2021 Steve Ruiz（允许移植与再分发，
  保留版权与许可声明即可；本文件即为声明）

---

## Inno Setup（只在**打安装包**时用到，不进 App）

- **文件**：`installer\InkTeach.iss`（安装脚本）、`installer\ChineseSimplified.isl`（中文界面文字）
- **来源**：https://jrsoftware.org/isinfo.php （安装包编译器本体，免费、闭源、宽松的自行分发许可）
  中文语言包取自社区翻译：https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation
- **许可证**：Inno Setup 本体按其自带许可（允许自由分发用它生成的安装包，包含商用）；
  中文语言包为 **MIT License，Copyright (c) 2019-2020 kirakira**
- **说明**：Inno Setup **不进入 App 的运行时**，只由 `publish.ps1` 在开发机上生成
  `InkTeach-Setup-<版本>.exe`；生成的安装包里**不包含** Inno Setup 本身。中文语言包
  （MIT）随仓库放了一份，仅用于编译期，须保留其版权声明。

---

## 本项目自身的许可证

**GPL-3.0**（见仓库根目录的 `LICENSE`）。作者选它，是打算用
"**开源版 + 商业授权（双许可）**"的方式发布：社区按 GPL 自由使用，
需要闭源嵌入 / 商用的再谈商业授权。

本项目是**独立实现**（Win32 + Direct2D）；对其它项目的参考只到
"做法 / 思路 / 参数取值"这一层，**没有复制代码**（见下表）。
**例外**：`src/InkEngine/InkStrokeModeler.cs` 是 `google/ink-stroke-modeler`
（Apache-2.0）的 1:1 移植，已按 Apache-2.0 在本文件上一节完整署名——这是
2026-10-03 起用户拍板的"单源对照实验"里唯一直接移植的代码。

---

## 其它参考过、但没有复制代码的项目

| 项目 | 许可证 | 用在哪 |
|---|---|---|
| `steveruizok/perfect-freehand` | MIT | 只读过思路（曾经按它重写过笔锋，那一层已于 2026-09-14 整层删除，代码未留） |
| `Inkeys` | **GPL-3.0** | **只读过思路，没有复制任何代码** |
| `WXRIW/Ink-Canvas`（Ink Canvas / 文档里叫 InkClass） | **GPL-3.0** | **只参考了做法与思路**（选页逻辑、图形绘制流程、停顿变直线、手势判据等），**没有复制代码**；本项目是 Win32 + Direct2D 的独立实现，与它的 WPF 实现不同 |
| `$P Point-Cloud Recognizer`（华盛顿大学） | New BSD | 单笔图形识别（`PointCloudRecognizer.cs`），按其公开算法实现 |
