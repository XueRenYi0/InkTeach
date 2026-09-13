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

## 其它参考过、但没有复制代码的项目

| 项目 | 许可证 | 用在哪 |
|---|---|---|
| `steveruizok/perfect-freehand` | MIT | 笔锋算法（`src/InkEngine.Optimize/` 里是按其思路重写的实现） |
| `Inkeys` | **GPL-3.0** | **只读过思路，没有复制任何代码**（GPL 有传染性，不能进闭源产品） |
