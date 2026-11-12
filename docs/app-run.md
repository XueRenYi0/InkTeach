# InkTeach 运行契约（给 agent 用）

## 类型
C# / .NET 10 / Direct2D 自绘（Vortice）。不是 WPF/WinForms，**UI Automation 拿不到控件**，只能按坐标操作。

## 启动
```powershell
.\run.ps1
```
带参数时用 `--` 分隔：
```powershell
.\run.ps1 -- --nohud
```

## 内置自检（优先用这些，不要另造）
| 命令 | 用途 |
|---|---|
| `.\run.ps1 --paneltest` | 产品界面自检，球 → 按钮带最小闭环（报告 100 项） |
| `.\run.ps1 --shapepaneltest` | 图形面板专项（入口 / 页签 / 选图形 / 蓝色互斥） |
| `.\run.ps1 --recoverytest` | 恢复 / 崩溃专项 |
| `.\run.ps1 --keytest` | 按键绑定断言 |
| `.\run.ps1 --hotkeytest` | 真键输入流（SendInput 发 Ctrl+Alt+O） |

## 截图（自带，1:1 像素）
```powershell
.\run.ps1 --screenshot reports\shots\<name>.png
.\run.ps1 --screenshot reports\shots\<name>.png <x> <y> <w> <h>
```
只截一块时给 x y w h，坐标是屏幕坐标。

## 录视频（真光标）
先标定坐标（生成带网格的截图）：
```powershell
powershell -ExecutionPolicy Bypass -File scripts\audit-ui.ps1 -Calibrate
```
坐标怎么填见 `scripts/audit-steps.md`。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\audit-ui.ps1 -Seconds 8
```
带动作脚本：
```powershell
powershell -ExecutionPolicy Bypass -File scripts\audit-ui.ps1 -Steps scripts\audit-steps.json
```
输出：
- `reports\video\audit-*.mp4`（30fps，含系统真光标）
- `reports\frames\*.png`（4fps 抽帧，给视觉审核用）

## 关闭
```powershell
Stop-Process -Name InkTeach -Force
```
`run.ps1` 是 `dotnet run`，会多一个 dotnet 父进程；杀 InkTeach 即可，父进程会自己退出。

## agent 使用规则
1. 先跑最相关的内置自检，拿到报告再决定要不要录视频。
2. 视频只在有交互动效、光标形状、笔迹跟手的场景用。
3. 录之前把无关窗口最小化，避免录进隐私内容。
4. 截图坐标是屏幕坐标，不是窗口内坐标；先取窗口 rect 再换算。
5. 不要改 `run.ps1` / `publish.ps1`。
