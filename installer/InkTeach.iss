; ============================================================================
;  InkTeach 安装包（Inno Setup 6；由 publish.ps1 自动调用，不用手动跑）
;
;  几条设计决定，都和"自动更新"绑在一起看：
;
;   1. PrivilegesRequired=lowest → **每用户安装**（默认装到
;      %LOCALAPPDATA%\Programs\InkTeach）。三个理由：
;        ① 教室机器多半没有管理员权限，装"只给当前用户"不用输密码；
;        ② 不弹 UAC：双击 → 直接装完（用户 2026-09-29 要的"简单点操作"；
;           2026-10-09 再简化：**五页全关**——欢迎/目录/任务/就绪/完成都不出现，
;           桌面图标不再问、直接建，装完自动把软件拉起来。老师只需要"双击一次"）；
;        ③ **最关键**：App 的"下载 → 换壳 → 重启"更新需要能写自己的目录。
;           装到 Program Files 就得每次更新弹 UAC（或者干脆失败）。
;
;   2. [UninstallDelete] 整目录删：换壳更新之后目录里是新版文件（不在原始安装
;      清单里），按"整目录"删才能干净。卸载列表里的版本号是**安装时**记的，
;      换壳更新后不会变——这是换壳路线的已知小瑕疵（功能不受影响，README 记了）。
;
;   3. 装之前 taskkill InkTeach.exe：它是常驻的透明覆盖层，Restart Manager
;      "礼貌关窗口"那套未必关得掉。
;
;   4. 中文界面用社区翻译 ChineseSimplified.isl（Inno 官方不带中文；取自
;      kira-96/Inno-Setup-Chinese-Simplified-Translation，MIT），随仓库放在
;      installer\ 下，见 THIRD-PARTY-NOTICES.md。
;
;  版本号 / 包目录由 publish.ps1 用 /D 传进来（保证和 exe 里的版本同源）。
; ============================================================================

#define AppName "InkTeach"
#define AppExeName "InkTeach.exe"
#define AppPublisher "XueRenYi0"
#define AppUrl "https://github.com/XueRenYi0/InkTeach"

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\dist\InkTeach-0.0.0-win-x64"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
; AppId 是"同一个软件"的身份证：升级时按它找旧版本，不会装成两份。
AppId={{7E2C4B1A-9D3F-4A6E-B8C1-5F0A2D7E9C34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 2026-10-09 用户："安装还要点好几次，有点烦" → **整套向导页全关**：
; 双击 = 直接开始装（只剩一张进度页），装完自动打开软件。
; 桌面图标不再问（原来是个 checkedonce 任务页），直接建——教室机器上没人会拒绝它。
DisableWelcomePage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutDir}
OutputBaseFilename=InkTeach-Setup-{#AppVersion}
SetupIconFile=..\src\InkTeach\assets\InkTeach.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "屏幕批注 / 白板工具"
; 桌面图标：**无条件建**（原来挂在 Tasks 上，为了让"零点击安装"成立，任务页整个不要了）
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "屏幕批注 / 白板工具"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; Flags: runhidden; RunOnceId: "killapp"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  { 装 / 升级之前先关掉正在运行的 InkTeach（覆盖层常驻，礼貌关窗口未必管用）。 }
  Exec('taskkill.exe', '/IM {#AppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
