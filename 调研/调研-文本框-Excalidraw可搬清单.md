# 调研：Excalidraw 文本框可搬清单（尽可能全套）

日期：2026-11-01　　状态：**清单已整理，按 P0/P1/P2 分批落地**

> 目标：不再自己设计文本框，直接照 Excalidraw 的文本对象模型抄。
> 本文只列**能搬的行为**，不搬它的 React/DOM 代码。

---

## 〇、一句话结论

Excalidraw 的文本模块可以拆成 **8 组逻辑**，其中 **约 80% 能直接搬到我们这里**：

1. 对象模型：`originalText`（原文）和 `text`（换行后的显示文本）分开；
2. 创建：单击 = 自动宽，横向拖 = 固定宽；
3. 编辑窗：原生输入框覆盖在文字上，实时重排；
4. 换行：自动宽到上限就转固定宽，高度永远按内容；
5. 手柄：左右拖 = 改宽重排；角拖 = 等比放大字号；双指 = 画布缩放；
6. 空文本：提交时 `trim()` 为空就删，不留幽灵对象；
7. 键盘：Esc/Ctrl+Enter 提交，Tab/Shift+Tab 缩进，纯文本粘贴；
8. 持久化：原文、自动宽、字号、行高、对齐、宽高都存，读档按原文重排。

**不搬**的主要是：形状/箭头/便签里的绑定文字、旋转、RTL、富文本、React 代码。
这些要么我们没有，要么会破坏“普通文本框”的简单性。

---

## 一、对象模型（最值得先搬）

Excalidraw 的文本对象不是一个 `string`，而是一组字段：

| 字段 | 作用 | 我们现在 | 建议 |
|---|---|---|---|
| `originalText` | 用户输入的原文，保留手动换行 | 只有 `Text` | **新增**；改宽/重排都从它出发 |
| `text` | 按当前宽度换行后的显示文本 | 排版时临时算 | 可作为缓存/显示文本，不覆盖原文 |
| `autoResize` | 宽是否随内容长 | `AutoWidth` | 沿用，但语义改成“宽自动，高永远自动” |
| `width` / `height` | 当前包围盒 | 已有 | 宽可手动；高由行数算 |
| `fontSize` | 字号 | 已有 | 已有，改成直接选 |
| `lineHeight` | 单位行高（W3C 语义） | 固定 1.55 | 保留 1.55，但按 `fontSize × lineHeight` 算高 |
| `fontFamily` | 字体 | 固定微软雅黑 | P2，可加 |
| `textAlign` | 左/中/右 | 左对齐 | P2，可加 |
| `verticalAlign` | 上/中/下 | 上对齐 | 普通文本框用不到；P2 |
| `containerId` | 绑定的形状/箭头/便签 | 无 | **不搬**；AI 题卡另算 |
| `angle` | 旋转 | 文本框不旋转 | **不搬**（保持现状） |
| `opacity` | 对象透明度 | 无 | **不搬** |

### 1.1 为什么要 `originalText` + `text` 两份

Excalidraw 在固定宽度时会把换行结果写进 `text`，但编辑时始终用 `originalText`。
否则每改一次宽度，就把“上一次的换行”当成用户输入，越改越乱，手动换行也会丢。

我们的渲染层现在是“每次排版时临时换行”，所以原文没丢；但要做**编辑窗回填、光标定位、改宽重排**，最好显式分开：

- `OriginalText`：用户敲进去的原文，含 `\n`、空格、Markdown 标记；
- `Text`：按当前宽度换行后的显示文本（可缓存，不参与编辑）；
- 编辑窗永远打开 `OriginalText`；
- 改宽/改字号后，用 `OriginalText` 重新 `wrap`。

### 1.2 文本规范化（Excalidraw 的 `normalizeText`）

- `\r\n` / `\r` → `\n`；
- `\t` → 8 个空格（RichEdit 里 Tab 也按 4/8 空格处理，见键盘一节）；
- 连续空行保留（空行也算一行高）；
- 粘贴只取纯文本，不把浏览器/Word 的字体、颜色、行距带进来。

我们现在 `ReadTextBoxEditText` 已经做了 `\r\n` 归一；把 Tab 和空行补齐即可。

### 1.3 空行高度

Excalidraw 的 `measureText` 会把空行替换成一个空格再量，保证：

> 高度 = 行数 × 行高

我们的 `TextBoxLayout` 已经按行累加，空行也会占一行；只要保持“每个 `\n` 都是一行”，就不会出现“空行不占高”。

---

## 二、创建文本（Excalidraw 的 App.textTool.ts）

| 操作 | Excalidraw 行为 | 我们建议 |
|---|---|---|
| 文本工具单击空白 | 新建 `autoResize=true` 的文本，进入编辑 | 照搬 |
| 文本工具单击已有文字 | 直接编辑，光标落在点击处 | 照搬（替换现在“第一下选中、第二下编辑”） |
| 文本工具横向拖 > 36px | 新建 `autoResize=false` 的固定宽文本，宽度 = 拖出的宽 | **建议搬**；这样“写一段话”不用先建再拖宽 |
| 拖拽阈值 | `TEXT_AUTOWRAP_THRESHOLD = 36px` | 照搬，避免手抖把单击变成几个字的窄框 |
| 创建后工具 | 默认切回选择工具（除非锁定） | **不照搬**：我们文本是常驻工具，和画笔一致 |
| 悬停已有文字 | 文字光标 + 元素高亮 | P1，可搬 |
| 空容器中心 | 绑定为容器的标签 | **不搬**（没有形状文字） |

### 2.1 单击 vs 拖拽

Excalidraw 用这一对 gesture 解决“单行标签”和“段落框”：

- **单击**：`autoResize=true`，宽度贴合内容，不主动换行；
- **横向拖**：`autoResize=false`，宽度 = 拖出的宽度，文字在框内换行，高度自动。

我们之前的“第一次输入自动、提交后固定”可以简化成这个模型：
**单击新建默认自动宽；想固定宽就直接拖一下。** 不需要等第一次提交。

### 2.2 创建后的选中/编辑

Excalidraw 创建后进入 `editingTextElement`，编辑窗覆盖在文字上；提交后按工具锁决定是否回到选择工具。
我们保持“文本工具常驻”，所以提交后仍留在文本工具，符合用户“和画笔同一套逻辑”的要求。

---

## 三、编辑窗（Excalidraw 的 textWysiwyg.tsx）

Excalidraw 用一个绝对定位的 `<textarea>` 覆盖在画布文字上，核心细节：

| 细节 | Excalidraw | 我们对应 |
|---|---|---|
| 字体/字号/行高/颜色 | 与画布文字完全一致 | RichEdit 已设字体/字号/颜色/行距 |
| 宽度 | 与元素同宽（+0.5 缓冲） | 编辑宿主铺在卡片上 |
| 高度 | 元素高 × 1.05，防跳动 | 可加 5% 缓冲 |
| 换行 | `autoResize=true` → `white-space: pre`（不换行）；固定宽 → `pre-wrap` | RichEdit 现在关横向滚动，等价于 `pre-wrap`；需要按模式切换 |
| 滚动 | `overflow: hidden`，**没有内部滚动条** | 普通文本框不画滚动条 |
| 光标定位 | 点击场景坐标 → 行号 → 行内字符偏移 | 用 `EM_CHARFROMPOS` 或我们的排版行映射 |
| 输入实时更新 | `oninput` → 重排元素 | 已有 `OnTextBoxEditChanged`，改成“宽固定、高自动” |
| 提交 | Esc / Ctrl+Enter / 失焦 / 点画布 / beforeunload | 已有，补齐“点画布只提交不新建” |
| 移动端 | 点画布外 `pointerdown` 也提交（blur 不可靠） | 我们已有失焦提交，触摸屏再加一层兜底 |
| 属性面板 | 点属性面板不提交，保持编辑 | 我们点色带会失焦提交；P1 再处理 |
| 粘贴 | 只取纯文本；Excalidraw 元素只取文字 | 已有纯文本粘贴 |
| Tab | 选中行缩进/反缩进 4 空格 | P1，可搬 |
| Ctrl+/- | 编辑中直接改字号 | P2，可搬 |

### 3.1 点击定位（重点）

Excalidraw 的做法：

1. 把点击的场景坐标转成文本局部坐标；
2. 用 `getWrappedTextLines` 找到点在第几条视觉行；
3. 用浏览器 Range 量出这一行每个字符的 x，找最近的字符偏移；
4. `setSelectionRange(offset, offset)`。

我们不需要 Range，直接用 Win32：

1. 点击屏幕坐标 → 编辑窗客户区坐标；
2. `EM_CHARFROMPOS` 得到字符索引；
3. `EM_SETSEL(index, index)`；
4. `EM_SCROLLCARET`。

这比“永远放末尾”精确，且不需要自己实现断行。

### 3.2 光标跟随：滚画布，不滚文本框（重点）

Excalidraw 的 `textWysiwyg` 有一段 `onEditorBoxScroll`：

> 编辑框本身不滚；浏览器为了让光标可见而产生的滚动偏移，被转成**画布平移**，再加 5px 余量。

这正好解决“长文本怎么办”：

- 文本框没有内部滚动条；
- 光标打到屏幕底部时，画布向上平移，文字继续向下长；
- 全局只有一套滚动，和画笔模式一致。

我们可以在 RichEdit 的 `EN_SELCHANGE` / `EN_UPDATE` 里读光标位置，接近屏幕底部时调 `ViewOffsetY`。这是 P0/P1 的关键项，建议做。

---

## 四、换行与尺寸（Excalidraw 的 textElement.ts / textWrapping.ts）

### 4.1 自动宽的“上限”

Excalidraw 不是无限自动宽：

- `TEXT_MAX_WRAP_WIDTH = 800`（场景单位）；
- `TEXT_VIEWPORT_PADDING = 20`（视口两侧留白）；
- 自动宽文本的宽度 = `min(内容自然宽, 视口可用宽, 800)`；
- **超过上限时，自动切成 `autoResize=false`，在 800 宽处换行**，不是继续往右长。

我们建议照搬：

- 自动宽上限 = `min(屏幕可用宽 - 40, 800 × Unit)`；
- 到上限就转固定宽 + 换行 + 高度继续长；
- 老师仍可手动拖宽超过 800（手动优先）。
- 这样“一行字拖得很宽”不会发生，长段落也不会变成一条很宽的横条。

### 4.2 高度

Excalidraw 的高度永远由排版算：

```
height = 行数 × fontSize × lineHeight
```

没有“固定高”状态，没有滚动条。我们照搬，正好解决用户不喜欢的“拖高留白 / 拖少滚动”。

### 4.3 自动宽 ↔ 固定宽的切换

| 触发 | Excalidraw | 我们 |
|---|---|---|
| 新建单击 | `autoResize=true` | 同 |
| 横向拖 | `autoResize=false`，宽=拖出宽 | 同 |
| 左右边拖 | `autoResize=false`，按新宽重排，高自动 | 同 |
| 内容超过 800 | 自动转 `autoResize=false`，在 800 处换行 | 同 |
| 角拖 | 按比例放大字号（见手柄一节） | 同 |
| 恢复自动宽 | 右侧小竖线手柄 → `actionTextAutoResize` | 双击左右竖线，或加一个小 grip |

### 4.4 锚点

Excalidraw 在自动宽↔固定宽切换时保留对齐锚点：

- 左对齐：左边不动，往右长/收；
- 居中：中心不动；
- 右对齐：右边不动。

我们目前是左对齐、左上角锚定，所以只需要“左边不动、往下长”。以后加居中/右对齐再补锚点逻辑。

### 4.5 最小宽度

Excalidraw 用“空文本宽度 + padding”和“最大字符宽 + padding”做下限。
我们已经有更好的“最长不可断 token”下限（中文按单字、英文按词、公式按可缩），保留我们的，不换成 Excalidraw 的近似值。

---

## 五、选择、手柄、放大缩小（Excalidraw 的 resizeElements.ts）

这是用户最关心的部分。Excalidraw 的文本手柄不是一套逻辑，而是三套：

| 手柄 | Excalidraw 行为 | 我们建议 |
|---|---|---|
| 左右边（e/w） | 按新宽度重排 `originalText`，`autoResize=false`，高度按行数重算 | **照搬**，但把“中点小手柄”换成**左右两条整高竖线**，超长文本框任意一段都能拖 |
| 角（nw/ne/sw/se） | 按比例放大/缩小**字号**，宽高一起缩放；角拖按“增长更多的轴”走，保持文本比例 | **建议搬**：这就是“放大缩小”功能 |
| 上下边（n/s） | 也按比例放大字号（不是改高） | **不搬**：我们高度永远自动，上下边不出现，避免误解 |
| 双指捏合 | **缩放画布**，不缩放单个元素 | **照搬**：双指永远画布缩放 |
| 自动宽恢复 | 右侧一条 16px 小竖线，点击恢复 `autoResize=true`，保留锚点 | 改成“双击左右竖线”或一个小 `↔` grip |

### 5.1 放大缩小和字号会不会冲突？

不会，只要**不引入第二套 `scale` 字段**：

- 角拖 = 改 `fontSize`；
- 字号菜单 = 也改 `fontSize`；
- 双指 = 改画布 `zoom`，不动元素；
- 文本框的 `width/height` 永远由 `fontSize + 文本 + 宽模式` 推出来。

Excalidraw 就是这么做的：角拖最终写的是 `fontSize`，不是 `element.scale`。
所以“角拖放大”和“字号 20 → 24”是同一件事的两种入口，不会打架。

### 5.2 双指缩放要不要保留？

**保留，但只缩放画布。** 理由：

- 白板/笔记类产品（Excalidraw、Miro、FigJam、OneNote）双指都是画布缩放；
- 如果双指缩放选中文本，会和“编辑文字时的双指滚动/缩放”冲突；
- 会让保存的数据多一个 `scale`，导出、命中、字号、笔宽全部要乘它；
- 老师想放大字，角拖或字号菜单更直接。

所以最终交互是：

- **左右两条整高竖线** = 改宽；
- **四个角** = 等比放大/缩小字号；
- **双指** = 画布缩放；
- **字号按钮** = 精确字号。

---

## 六、空文本、撤销（Excalidraw 的 App.text.ts / textWysiwyg.tsx）

Excalidraw 的规则非常干脆：

```ts
const isDeleted = !nextOriginalText.trim();
```

提交时：

- `trim()` 为空 → 整个文本元素标记删除；
- 新建后没输入 → 删除，不留在撤销栈里；
- 已有文本被清空 → 删除；撤销恢复**原文**；
- 创建 + 输入算**一步撤销**，不会出现“先撤销文字、再撤销空框”；
- 绑定的形状/箭头会解绑（我们没有绑定，跳过）。

我们照搬，并补一条：打开旧文件时清理空的普通文本框。

---

## 七、文字属性与 UI（Excalidraw 的 actionProperties / 字体）

| 属性 | Excalidraw | 我们建议 |
|---|---|---|
| 字号 | 一组预设 + 增减快捷键 | `[15 ▾]` + 4×3 网格；编辑中 Ctrl+/- 可后补 |
| 颜色 | `strokeColor`，和图形共用调色板 | 黑/红/当前色 3 格 + 取色板 |
| 字体 | 多字体 | P2，先固定微软雅黑 |
| 对齐 | 左/中/右；绑定文字还有垂直对齐 | P2；普通文本框先左对齐 |
| 行高 | 按字体给 unitless lineHeight | 固定 1.55，和现有排版一致 |
| 透明度 | 有 | 不搬 |
| 旋转 | 有 | 不搬（文本框保持不旋转） |
| 富文本 | **没有**（纯文本） | 我们保留 Markdown 渲染，编辑窗显示源码 |

---

## 八、键盘、输入法、触摸

| 操作 | Excalidraw | 我们 |
|---|---|---|
| 换行 | Enter | RichEdit 原生 |
| 提交 | Esc / Ctrl+Enter / 点外面 | 同 |
| 缩进 | Tab / Shift+Tab（选中行 4 空格） | P1，可搬；RichEdit 里拦截 Tab |
| 全选 | Ctrl+A | 原生 |
| 行首/行尾 | Home/End | 原生 |
| 文首/文末 | Ctrl+Home/End | 原生 |
| 选词 | 双击 | 原生 |
| 中文输入法 | 浏览器 composition | RichEdit 原生，更稳 |
| 触摸提交 | 点画布外提交；有虚拟键盘 | 我们已有「完成」按钮 |
| 触摸选词 | 长按 | RichEdit 原生 |
| 双指 | 画布缩放 | 画布缩放 |

一个差异：Excalidraw 桌面端双击已有文本会**全选**（`autoSelect: !isTouchScreen`）。
我们不要全选，改成“光标落在点击处”，否则老师想在某一行补字时会把整段替换掉。

---

## 九、持久化、复制粘贴

需要存的字段（普通文本框）：

- `OriginalText`（原文）
- `Text`（换行后的显示文本，可重算，可不存）
- `AutoWidth`（宽是否自动）
- `Width`
- `FontSize`
- `LineHeight`（固定 1.55 可写死）
- `Unit`
- `Ink`（颜色）
- `Role`（Normal / AiQuestion）

`Height` 可以由 `Text + Width + FontSize + LineHeight` 重算，但为了兼容旧档可以继续存。
读旧档时：

- 普通文本框 `AutoHeight` 一律按 true 处理；
- `AutoWidth` 缺失时按 false（旧档没有这个字段）；
- `Text` 当 `OriginalText` 用；
- 空文本清理。

复制粘贴：

- 只取纯文本；
- 统一换行、Tab；
- 空文本忽略；
- 粘贴后按当前宽模式重排。

---

## 十、明确不搬的部分

| 功能 | 为什么不搬 |
|---|---|
| 形状/椭圆/菱形/箭头/便签里的绑定文字 | 我们没有“图形内文字”这个对象；AI 题卡是独立卡片 |
| 容器自动长高、文字居中、椭圆内接矩形 | 同上，属于容器文字，不是普通文本框 |
| 旋转 | 文本框保持不旋转，减少命中/编辑/存档复杂度 |
| RTL / 双向文字 | 中文课堂暂不需要；RichEdit 编辑天然支持，渲染层不专门做 |
| 多字体 | 先固定微软雅黑，避免字体回退/测量不一致 |
| 多选文字一起缩放 | P2；先保证单个文本框稳定 |
| 富文本（粗体/斜体/下划线按钮） | Excalidraw 也没有；我们保留 Markdown |
| React/DOM textarea 实现 | 我们是 C#/D2D + Win32 RichEdit，只搬行为 |
| 工具用完后自动切回选择 | 我们文本是常驻工具，和画笔一致 |

---

## 十一、建议的落地顺序

### P0（这一轮，解决用户已提的全部问题）

1. `OriginalText` / 显示文本分开；`normalizeText`（换行、Tab）。
2. 高度永远自动；删除固定高/内部滚动/上下手柄。
3. 宽自动 → 超过上限（800×Unit 或视口宽）转固定宽换行。
4. 左右两条整高竖线拖宽；四个角等比缩放字号；双指仍是画布缩放。
5. 单击/双击进入编辑，光标落在点击处。
6. 空文本提交即删；老框清空后删除，撤销恢复原文。
7. 「更多」里删掉文本框入口；文本工具单击已有文字直接编辑。
8. 色带 3 格 + 字号菜单。

### P1（紧接着做）

1. 文本工具横向拖创建固定宽文本（36px 阈值）。
2. 光标跟随：到屏幕底部时平移画布，不滚文本框。
3. Tab/Shift+Tab 缩进选中行。
4. 悬停已有文字时高亮 + I-beam。
5. 点色带/字号菜单时不结束编辑（Excalidraw 的 properties 处理）。
6. 恢复自动宽的专用小 grip / 双击竖线。

### P2（以后）

1. 左/中/右对齐、垂直对齐。
2. 字体族。
3. 多选文字等比缩放。
4. 旋转。
5. 形状内文字 / 便签（如果以后做）。

---

## 十二、结论

**能搬，而且值得整套搬。** Excalidraw 已经把“白板上的纯文本”这一件事拆得很干净：

- 它没有富文本、没有固定高、没有内部滚动；
- 它用 `originalText` + 换行缓存解决“改宽不破坏原文”；
- 它用“单击自动宽 / 拖拽固定宽”解决单行和段落；
- 它用“左右改宽 / 角缩放字号 / 双指缩放画布”解决放大缩小和字号冲突；
- 它用“提交时 trim 为空就删”解决空文本；
- 它用“画布跟随光标”解决长文本，不需要第二个滚动条。

我们只需要把它的 **React textarea + canvas** 换成 **Win32 RichEdit + D2D**，
行为照搬，代码不照搬。这样就不用再一项项设计，剩下的只是实现工作量。

---

## 参考源码（Excalidraw master）

- `packages/excalidraw/components/App.text.ts`：创建/编辑会话、最大宽度、光标跟随、空文本提交
- `packages/excalidraw/components/App.textTool.ts`：单击/拖拽创建、目标判定、36px 阈值
- `packages/excalidraw/wysiwyg/textWysiwyg.tsx`：编辑窗、点击定位、提交、粘贴、Tab、画布跟随
- `packages/element/src/textElement.ts`：`redrawTextBoundingBox`、`refreshTextDimensions`、容器文字
- `packages/element/src/textWrapping.ts`：CJK/英文/emoji 换行、原文偏移
- `packages/element/src/textMeasurements.ts`：`measureText`、行高、`normalizeText`、字符宽度缓存
- `packages/element/src/resizeElements.ts`：左右改宽、角缩放字号、最小字号/宽度
- `packages/excalidraw/textAutoResizeHandle.ts`：恢复自动宽的小竖线手柄
- `packages/excalidraw/actions/actionTextAutoResize.ts`：恢复自动宽、保留锚点
- `packages/element/src/newElement.ts`：`newTextElement`、`autoResize` 默认 true、锚点
- `packages/common/src/constants.ts`：`TEXT_MAX_WRAP_WIDTH=800`、`TEXT_VIEWPORT_PADDING=20`、`TEXT_AUTOWRAP_THRESHOLD=36`
