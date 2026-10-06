# InkTeach 优化 · 环境与自检基线

日期：2026-10-06　　仓库：`C:\Users\LHL-XZX\Desktop\软件\InkTeach`
上游：<https://github.com/XueRenYi0/InkTeach>（v8.9.2，GPL-3.0）

这份是**基线记录**：动手优化之前，先把"环境长什么样、自检本来什么颜色"钉住，
以后每次改完只跟这份比——**比红灯有没有变多**，而不是看它是不是全绿。

---

## 一、这台机器上装了什么

这台机器原本**没有 git、没有 .NET、没有 PowerShell**。装完如下：

| 工具 | 版本 | 位置 | 备注 |
|---|---|---|---|
| Git | 2.55.0 | `C:\Program Files\Git\cmd` | |
| .NET SDK | 10.0.401 | `%LOCALAPPDATA%\Microsoft\dotnet` | **非管理员**，装不到 `C:\Program Files\dotnet` |
| PowerShell | 7.6.6 | PATH 上的 App Execution Alias | **跑自检必须用它**，见第二节 |

三个非管理员/新手环境带来的坑，都已处理：

1. **`DOTNET_ROOT`（用户级）= `%LOCALAPPDATA%\Microsoft\dotnet`**
   开发版 `InkTeach.exe` 是 framework-dependent 的 apphost，它只认注册表里的
   默认安装位置。SDK 装在用户目录时，exe 会直接罢工并打印
   `You must install .NET to run this application`。
   设了 `DOTNET_ROOT` 之后 apphost 才能找到运行时。
2. **用户级 PATH 已加 dotnet**，新开终端自动生效。
3. **必须广播 `WM_SETTINGCHANGE`**，否则改了 `HKCU\Environment` 也不生效：
   注册表里值是对的，但 Explorer 仍用**缓存的旧环境块**去启动新进程，
   实测新终端里 `DOTNET_ROOT` 为空、`dotnet` 不在 PATH、`dotnet build` 退出码 9009。
   广播之后新进程的 PATH 里就出现了 dotnet / Git / `.dotnet\tools`，`dotnet --version` 正常。
   —— 已经广播过了。**如果哪天又改了环境变量，记得重新广播，或注销重登。**

> 发布的绿色版 / 安装包是 self-contained 或 NativeAOT，**不依赖这些**，拷走就能用。

## 二、⚠️ 必须用 pwsh 跑自检，不能用 powershell.exe

这是本次摸环境时最值得记的一条，**它会伪造出"测试没判据"的假象**。

`tools/bench/Run-FuncSuite.ps1` 里判定红绿的行是：

```powershell
$text = (Get-Content $out -Raw ...)      # 没写 -Encoding
...
elseif ($text -match '\bPASS\b' -or $text -match '通过') { 'PASS' }
else { 'DATA' }                          # 跑完了但没判据
```

- 本机 ANSI 代码页是 **936（GBK）**；
- 程序写到重定向文件的 stdout 是 **UTF-8**；
- Windows PowerShell 5.1 的 `Get-Content` 不带 `-Encoding` 时按 ANSI(=GBK) 解码，
  于是 `'通过'` 这个中文判据**永远匹配不上**；
- 结果：一大批**实际通过**的用例被判成 `DATA`。

实测：同一份代码、同一个二进制

| 运行器 | 结果 |
|---|---|
| `powershell.exe`（5.1） | 39 项：通过 27，失败 0，**没判据 8**，跳过 4 |
| `pwsh`（7.6.6） | 39 项：通过 37，失败 1，**没判据 0**，跳过 1 |

那 8 个 `DATA`（`seltest` / `captest` / `cornertest` / `selfcross` /
`appendtest` / `pagetest` / `iotest` / `patterntest`）**全都是真的通过**，
原始输出里写着"合计 N 项：通过 N，失败 0"。作者平时用 `pwsh`，所以他那边看不到这个问题。

**结论：自检必须用 `pwsh -File tools/bench/Run-FuncSuite.ps1`。**
已封装成仓库外的 `run-selftest.ps1`（放外面是为了让仓库保持 pristine，方便与 upstream 对比）。

## 三、基线自检结果（`pristine` = 未改动的原始代码）

```
pwsh -File tools/bench/Run-FuncSuite.ps1 -AppRoot <repo> -OutDir reports/func-baseline-pwsh
```

**39 项：通过 37，失败 1，跳过 1，没判据 0**（退出码 1）

> 脚本默认 `$AppRoot = 'D:\文件集中\code\批注'`（作者机器的路径），
> 在这台机器上**必须**用 `-AppRoot` 覆盖，否则找不到 exe。

### 唯一失败：`--passtest`（间歇性）

最后一条判据 `加 LAYERED+TRANSPARENT` —— 期望"穿透模式下点下层，
下层窗口收到点击"，实测 `下层窗口收到点击: 否` → FAIL。

**同一个症状在 `--uitest` 里也出现**（第 ⑧ 项 `穿透·面板外交下层`：
期望下层收到 ≥1 次点击，实得 0 次）。

同一份二进制反复跑的结果：

| 用例 | 独立跑 6～8 次的结果 |
|---|---|
| `--passtest` | 4/6 PASS、2 次**整个进程零输出**、后来又 8/8 PASS |
| `--uitest` | 独立跑 3/3 都是 **30 通过 / 2 失败**（就是上面那两条）；套件里跑却 PASS |

即：**穿透相关的"点击要能落到下层窗口"这条，在这个环境里不稳定。**
它跨进程、依赖 z 序与窗口命中，属于**环境敏感的用例**。
目前**无法判定是代码 bug 还是本机环境（无 GPU 合成的桌面 / 窗口 z 序）导致**，
需要单独查——见下面"已知待办"。

### 唯一跳过：`--trailtest`

输出写得很清楚：`SKIP: 当前是鼠标（轨迹只对真笔生效），这一项不适用`。
委托墨迹轨迹只对真压感笔生效，**没有真笔就必然跳过**，这是正常的，
不是故障。

## 四、⚠️ 必须用 `-m:1 -nodeReuse:false` 编译，否则稳定失败

这条是本次摸环境时**最费时间**的一条，而且症状极具误导性。

### 现象

```
C:\...\sdk\10.0.401\NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')
    [...\src\InkUi\InkUi.csproj]
0 个警告
1 个错误
已用时间 00:00:00.62
```

`dotnet build src\InkTeach\InkTeach.csproj -c Release` **稳定失败**（0.6 秒就挂）。

### 真实堆栈（`-v diag` 抓的）

```
System.ArgumentNullException: Value cannot be null. (Parameter 'path1')
   at System.IO.Path.Combine(String path1, String path2)
   at NuGet.Common.NuGetEnvironment.CalculateFolderPath(NuGetFolderPath folder)
   at NuGet.Configuration.XPlatMachineWideSetting..ctor()
   at NuGet.Build.Tasks.GetRestoreSettingsTask.Execute()
```

即：NuGet 在算"某个文件夹路径"时 `Path.Combine(path1, path2)` 的 `path1` 是 null。

### 排除掉的所有错误假设

| 怀疑 | 实测 | 结论 |
|---|---|---|
| 环境变量没配好 | 反射直调 `NuGet.Common.NuGetEnvironment.GetFolderPath/CalculateFolderPath`，8 个 `NuGetFolderPath` 枚举值**全部返回正常路径**，无 null | 排除 |
| `USERPROFILE` 缺失 | 已设置，且 `NuGetPackageRoot = C:\Users\LHL-XZX\.nuget\packages\` 正确 | 排除 |
| `obj/` 被搞坏 | 删掉全部 `obj/` + `bin/` 后从零编译，**照样失败** | 排除 |
| 某个包没还原 | `InkEngine.csproj`、`InkUi.csproj` **单独编译都成功**（exit 0），只有编译 `InkTeach.csproj` 才失败 | 排除 |
| 机器级 NuGet 配置坏了 | `C:\Program Files (x86)\NuGet` 与 `C:\ProgramData\NuGet` 都不存在（正常情况就不该存在） | 排除 |

### 真正的触发条件

**MSBuild 的多节点 + 节点复用**。

| 命令 | 结果 |
|---|---|
| `dotnet build ... -c Release`（默认，多节点+复用） | **失败**（反复复现） |
| `dotnet build ... -c Release -m:1` | 成功 |
| `dotnet build ... -c Release -nodeReuse:false` | 成功 |
| `dotnet build ... -c Release -m:1 -nodeReuse:false` | 成功（连跑 3 次全过） |

`-m:1` 和 `-nodeReuse:false` **各自单独就能修好**，所以不是"某一个开关"，而是
**多节点/复用这条路径本身**在这台机器上会踩到 NuGet 的
`ConcurrentDictionary.GetOrAdd` + `CalculateFolderPath` 这条竞态。

> 会话最开始那次编译是成功的（41 秒），所以它是**竞态**而非确定性失败——
> 也就是说，默认参数下编译成功与否不可靠，这比稳定失败更麻烦。
> `run-selftest.ps1` 已固定带上 `-m:1 -nodeReuse:false`。

## 五、顺带发现的两个既有问题（都不是本次改动引入的）

1. **仓库里带着一份崩溃日志**：`%LOCALAPPDATA%\InkTeach\last-crash.txt`，
   时间 `2026-10-05 14:22:51`，早于本次任何操作：

   ```
   System.NullReferenceException: Object reference not set to an instance of an object.
      at InkEngine.InkEngine.UpdateSelDrag(Single x, Single y)
      at InkEngine.InkEngine.TouchMoveDispatch(UInt32 id, Single x, Single y)
      at InkEngine.InkEngine.OnPointerMove(...)
   ```

   触摸拖动选中框时的一个空引用。**触摸拖选**正是清单里 C8（学校大屏）要用到的路径。

2. **连续快速启动偶发零输出**：同一秒内紧接着起第二个进程时，
   偶尔 stdout 一个字节都没有（2/6，后来 8/8 复现不到）。
   没留下孤儿进程，也没有新崩溃日志，暂列为待观察。

## 六、git 基线怎么用

```
分支/提交:
  ba38272  baseline: InkTeach v8.9.2 原始代码快照（来自 GitHub zip，未改动）
  pristine  -> 指向同一个提交，"永远没改过的原始代码"
  upstream  -> https://github.com/XueRenYi0/InkTeach.git（只作参照，勿 push）
```

- 564 个文件进库；`bin/ obj/ reports/func-*/` 等已被 `.gitignore` 正确排除
  （跑完自检 `git status` 仍然是干净的）。
- `.gitattributes` 规定 `* text=auto eol=lf`，所以全局设了
  `core.autocrlf=false`：由 git 按该规则在**入库时**归一化，
  而不是反过来把工作区改成 CRLF。
- **随时可以回到原点**：`git checkout pristine -- .` 或 `git reset --hard pristine`

### 优化时的规矩

1. 每个改动开一个分支，别在 `pristine` 上直接动。
2. 跑 `.\run-selftest.ps1`，**只要"没判据"还是 0、失败数没有超过 1**（那个 passtest），
   就认为没有回归。
3. `passtest` 会间歇性抽风，判断回归时**要重跑确认**，别拿单次结果下结论。