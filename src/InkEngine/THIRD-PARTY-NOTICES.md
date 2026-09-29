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

---

## 其它参考过、但没有复制代码的项目

| 项目 | 许可证 | 用在哪 |
|---|---|---|
| `steveruizok/perfect-freehand` | MIT | 只读过思路（曾经按它重写过笔锋，那一层已于 2026-09-14 整层删除，代码未留） |
| `Inkeys` | **GPL-3.0** | **只读过思路，没有复制任何代码** |
| `WXRIW/Ink-Canvas`（Ink Canvas / 文档里叫 InkClass） | **GPL-3.0** | **只参考了做法与思路**（选页逻辑、图形绘制流程、停顿变直线、手势判据等），**没有复制代码**；本项目是 Win32 + Direct2D 的独立实现，与它的 WPF 实现不同 |
| `$P Point-Cloud Recognizer`（华盛顿大学） | New BSD | 单笔图形识别（`PointCloudRecognizer.cs`），按其公开算法实现 |
