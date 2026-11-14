# InkTeach 架构决策记录（ADR）

## ADR-001 匿名设备统计 v2（2026-11-14）

### 背景

设备卡 v1 依赖首次启动的模态同意框，用户反馈打扰课堂；同时报表只能看台数和版本，
无法判断地区分布与「卡不卡」。需要在**不收集 IP / 省市 / 账号 / 板书**的前提下，
把「总台数、国家、卡顿」变成可长期统计的核心报表。

### 决策

1. **身份**：沿用首次启动生成的随机安装 ID（`Guid.NewGuid().ToString("N")`，存
   `settings.json` 的 `ui.log.id`）。只用于同一台机器的去重计数；不读硬件序列号、
   不读账号、不读 IP。更新程序不换 ID；重装 / 换机 / 换 Windows 用户 = 新 ID。
2. **数据模型**：设备卡仍是一台机器一个 JSON（`devices/<id>.json`，按天覆盖），
   v2 新增 `lang`（界面语言）、`region`（Windows 区域设置的国家代码，ISO 3166-1）、
   `tz`（时区偏移）。保留版本、OS、机型、CPU、内存、GPU、屏幕/DPI/刷新率、
   帧率/慢帧/最大帧/内存、触摸统计、首次/最近日期。**不写 IP、省/市/区**。
3. **同意模型**：`privacy.consent = granted | denied` + `privacy.ver = 2`，
   同时落盘 `privacy.json`（不受备份恢复影响）。
   - 默认开启；**欧盟 / 欧洲经济区 / 英国 / 瑞士默认关闭**（可在设置主动开启）。
   - 旧版显式 `denied` 升级后保持关闭；旧版 `granted` 保持开启。
   - 首次 / 升级后只显示一条 6 秒非阻塞 Toast，不再弹模态框。
4. **日志与设备统计解耦**：`LogUploadConsentGranted` 只认
   `privacy.logUpload == "granted"`，不再回退到设备统计同意。运行日志可能含
   路径 / 堆栈，必须第一次手动上传时单独确认。
5. **关闭语义**：设置 → 关于 →「匿名设备统计」关闭 = 写 `denied` + 停止上传 +
   异步删除本机 ID 对应的设备卡。

### 备选方案与否决理由

| 方案 | 否决理由 |
| --- | --- |
| 用 IP 反查国家 | 违反「不取 IP」目标；且网络层 IP 会被第三方仓库记录 |
| 用省/市定位做地区报表 | 需要 IP 或系统定位权限，超出设备统计范围 |
| 每次启动弹模态同意 | 打断课堂；用户已明确要求尽量不弹窗 |
| 设备统计开启即视为同意上传日志 | 日志含路径 / 堆栈，风险高于设备卡，必须单独确认 |
| 新建 `privacy.stats` 键 | 迁移面变大；复用 `privacy.consent` 即可兼容旧版 |

### 影响

- 改动文件：`Engine.cs`（同意 / 迁移 / 提示）、`DiagLog.cs`（设备卡字段、日志同意）、
  `FullUi.cs`（设置行）、`HelpUi.cs`（帮助卡）、9 种语言 `core.json`、
  `PRIVACY.md`、`docs/privacy.html`、`tools/device-stats.ps1`。
- 报表：`tools/device-stats.ps1` 增加国家 / 语言 / 慢帧 Top 统计；慢帧（`slow150`）
  是「卡不卡」的主指标，`fps` 在空闲时可能很低，仅作参考。
- 未做：省份 / 城市 / IP 定位；硬件指纹；跨设备账号体系。

### 验证

- `dotnet build` 通过；`--langaudit` 9 种语言 528 key、缺失 0、占位符 0；
  `--settingsaudit` 46 通过 / 0 失败。
- 迁移实测：旧 `privacy.json` 为 `ver=1 consent=denied` 时升级后仍为 `denied`；
  `granted` 升级后保持 `granted`。
- 设置页 10 种语言截图经视觉审核通过（无裁切 / 漏翻 / 开关异常）。
