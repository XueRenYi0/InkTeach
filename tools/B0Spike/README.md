# B0 spike: windowless RichEdit（ITextServices + ITextHost）

验证目标：**编辑时背景真正透明**（不是纯色矩形），同时保留 RichEdit 的
IME / 光标 / 选区 / 撤销 / 剪贴板。

## 结论（2026-11-09）

- `TxGetBackStyle` 返回 `TXTBACK_TRANSPARENT` 有效：文字直接压在棋盘格和红色竖条上，
  **没有白底/灰底矩形**。
- 选区正常：`EM_SETSEL(0,6)` 后前 6 个字有半透明蓝色高亮（见 `b0-transparent.png`）。
- 光标正常：鼠标点击能把 caret 放到对应字符位置（见 `b0-caret.png`）。
- 输入正常：`WM_CHAR` 转发后字符进入文本，`WM_GETTEXTLENGTH` / `EM_EXGETSEL` 与预期一致。
- IME 宿主已接上：`TxImmGetContext` 返回非空（`ImmGetContext(hwnd)`），
  `WM_IME_*` 已转发；候选窗位置需要真人用中文输入法再确认一次。
- `ITextServices2` 可以 QI 成功（`S_OK`），但 **`TxDrawD2D` 在系统 `Msftedit.dll` 上不画字**
  （返回 `S_OK`，WIC/D2D 目标都是空白）。原因：D2D/DirectWrite 的 RichEditD2D 只随 Office 的
  `riched20.dll` 提供，没有移植到系统 `msftedit.dll`。
  **正式接入用 `ITextServices::TxDraw`（GDI）+ 宿主先画背景**，不要依赖 `TxDrawD2D`。
- 性能（GDI `TxDraw`，1200×800）：16k 字符约 **1.9–2.0 ms/帧**；
  在该文本上追加 500 个 `WM_CHAR` 共约 **320 ms**（约 0.64 ms/字符）。
  普通文本框远小于 16k，编辑时足够。
- 透明是“宿主先画背景 + RichEdit 只画字”，窗口本身仍是不透明窗口；
  只要宿主画的背景和画布一致（纯色纸面，或编辑前截取画布快照），视觉上就是无缝的。
  真·逐像素透明（背景是视频/动态内容）需要额外做 alpha 提取或换编辑器，不建议第一版做。

一个坑：`TxNotify` 里再对 `EN_CHANGE/EN_UPDATE` 调 `InvalidateRect` 会形成重绘循环，
把定时器饿死。宿主应依赖 RichEdit 自己的 `TxInvalidateRect` / `TxViewChange`。

## 构建

需要 VS 2022 Build Tools（x64）+ Windows SDK：

```bat
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=x64
cd tools\B0Spike
cl /nologo /EHsc /O2 /utf-8 B0Spike.cpp /link /SUBSYSTEM:WINDOWS /OUT:bin\B0Spike.exe user32.lib gdi32.lib ole32.lib imm32.lib d2d1.lib windowscodecs.lib
```

## 运行

```bat
bin\B0Spike.exe              :: 手动输入（中文 IME / 光标 / 选区）
bin\B0Spike.exe --selftest   :: 自动追加文字并选中一段，写 b0-selftest.log
bin\B0Spike.exe --bench      :: 16k 字符绘制 + 500 次 WM_CHAR 基准，写 b0-selftest.log
bin\B0Spike.exe --d2d        :: 反证：TxDrawD2D 在系统 Msftedit 上返回 S_OK 但不画字
```

`shot.ps1` 只用于把窗口截图保存成 PNG（会把目标窗口置顶后抓屏）。

## 文件

- `B0Spike.cpp`：spike 源码；`Host` 实现 `ITextHost`，`WM_PAINT` 里先画棋盘格背景，
  再 `TxDraw` 画文字（透明）。
- `b0-transparent.png`：透明背景 + 选区。
- `b0-caret.png`：透明背景 + 点击后的 caret。
