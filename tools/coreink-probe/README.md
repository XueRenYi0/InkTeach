# coreink-probe（系统墨迹宿主探针 · 留档）

来源：`Desktop\winui\overlay-aot\coreink-probe`（2026-10-12 归档入仓，防丢失）。
用途：验证"裸 Win32 程序能否让系统墨迹引擎（CoreInkPresenterHost）接管笔输入"——
即"无 XAML 宿主"路线。含真笔测试模式与多种对照变体。

## 运行

- 合成笔注入（默认）：直接跑 exe，9 秒自动结束；
- **真笔测试**：`CoreInkProbe.exe --pentest`（浅蓝书写带内用笔写，`Ctrl+Alt+Shift+Q` 结束；
  结果落盘 `桌面\软件\真笔探针结果.txt`）；
- 变体开关：
  - `--mainthread` 主线程装配（对齐 UWP 单线程模型）；
  - `--mouse` 合成鼠标拖拽；`--target` hwndTarget 直投；`--activate` 抢前台；
  - `--secs N` 自定义时长；`--nomsg` 真笔结束时不弹对话框。

## 实测结论（2026-10-12，本机 Win11 24H2 26100.7462）

- 合成笔 / 合成鼠标 / **真笔**：窗口收到全部指针消息（~370 / 1974 条），
  InkPresenter **0 笔**、旁听口（CoreInkIndependentInputSource）**0 事件**；
- 主线程装配变体：同样 0 笔；
- → **裸 Win32 没有输入路径进入系统墨迹引擎**；这些 API 服务的是 XAML/UWP 输入栈
  （Office 的墨迹走 XAML 岛屿；`IInkPresenterDesktop` 无任何公开使用代码）。
- 详细记录：`调研/调研-压感数据与墨迹预测.md` §7（"2026-10-12 复现实验"两段）。

## 复现

```powershell
dotnet build CoreInkProbe.csproj -c Debug
bin\Debug\net10.0-windows10.0.19041.0\win-x64\CoreInkProbe.exe
```

（NativeAOT 配置在 csproj 里；`dotnet build/run` 走 JIT 即可，publish 时才 AOT。）
