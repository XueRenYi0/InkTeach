# InkLocalization · 多语言层

这个工程只做一件事：把**用户可见的中文源文**翻译成当前语言。

## 目录

```
Loc.cs                      语言入口（Tr / T / FontFamily / LocaleName / 语言列表）
Locales/<code>/core.json    核心标签（主条、设置、帮助、对话框过滤器…）
Locales/<code>/help.json    可选：按领域拆文件；加载器会自动合并
Locales/<code>/dialogs.json 可选
```

- 文件是 `EmbeddedResource`，打进程序集；**不要**改成 `.resx` / satellite assemblies。
  当前发布是 NativeAOT 单 exe，satellite 资源在单文件下要额外验证。
- JSON 格式：
  ```json
  { "strings": { "中文源文": "Translation" } }
  ```
- 中文源文就是 key（gettext 风格）。源文改动会让旧翻译自然失效并回退，
  不会把错译文挂到新文案上。
- 回退链：当前语言 → 英文 → 原中文。
- 语言代码用 BCP-47；资源名会把 `-` 转成 `_`（`pt-BR` → `pt_BR`），加载器已处理。

## 加一条文案

1. 在 UI 里保持中文源文，走 `Loc.Tr("中文源文")`，或带参数：
   `Loc.T("已选 {0}", count)`。
2. 在 `Locales/en/core.json`（或对应领域文件）加英文翻译。
3. 其他语言可先不加，英文会回退；补齐时只改 JSON。
4. 自检：`INKTEACH_LOC_AUDIT=1` + `--lang en` 运行页面，退出时会打印
   `LOC-MISSING`，把缺的源文补进 JSON 即可。

> 静态 UI 文字大多经过 `InkUi/Widgets.cs` 的 `Text` / `TextLeftMiddle` / `Measure`，
> 已经在入口统一 `Loc.Tr`；**不需要**逐条改 `FullUi.cs` 的调用点。
> 动态拼接、Toast、文件对话框、引擎弹窗仍要在源头用 `Loc.T` / `Loc.Tr`。

## 加一种语言

> **当前目标已冻结在 10 个语言**（2026-11-13 用户定），不要因为“顺手”往 `Loc.All` 里加；
> 没有明确产品需求就先不加。

1. 在 `Loc.cs` 的 `All` 里加 `LocaleInfo`（代码、英文名、本地名、字体、locale、RTL、Inno 名）。
2. 建 `Locales/<code>/core.json`，先复制 `en/core.json`，逐条翻译。
3. 安装器 `installer/InkTeach.iss` 的 `[Languages]` 加对应官方 `.isl`。
4. 先跑 `--lang <code>` 的 `--panelshow`，检查字体/换行/溢出；再跑 `INKTEACH_LOC_AUDIT=1`。

## 不要做

- 不要把日志、`SelfTests/**`、遥测、代码注释塞进翻译表。
- 不要翻译用户自己的板书/PDF/图片文字。
- 不要在没有 `Loc` 的地方硬编码新语言文案；否则下次又要满仓库找。
- 不要在 `FullUi.cs` / `Engine.cs` 里继续堆大段多语言字符串；按领域加 JSON 文件。
