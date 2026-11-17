# 官网部署：国内 OSS + 国外 GitHub Pages

> 2026-11-17 起。目标：**发一版不用改网页**；国内走 `inkteach.com`（OSS），国外继续走 GitHub Pages。
> 这份文档只讲“怎么做、为什么这么做、坑在哪”，代码在 `tools\deploy-site.ps1` 和 `tools\sync-site.ps1`。

## 1. 两个问题怎么解的

### 1.1 “每次发版都要改网页” → 不用改了

2026-11-17 之前，版本号、四个下载链接、安装包大小是**写死在 `docs\index.html` 和 `docs\i18n.js` 里**的，
每发一版都得改一遍，改漏一处官网就写着旧版本。

现在的链路（同一份源文件，Pages 和 OSS 都吃它）：

```
src\InkTeach\InkTeach.csproj  <Version>   ← 版本号唯一来源
        │
        ├─ tools\sync-site.ps1   → docs\release.json（版本 / 日期 / 4 个地址 / 2 个大小）
        │                        → docs\index.html 的 JSON-LD + 首屏兜底对象（搜索引擎只认字面值）
        │                        → docs\sitemap.xml 的 lastmod
        │
        └─ docs\site-release.js  → 浏览器读 release.json，把 {V} / {SETUP_MB} / {ZIP_MB} 填掉，
                                   并给 data-href 的按钮赋真实下载地址
```

页面里**只有占位符**（`{V}`、`{SETUP_MB}`），`site-release.js` 有三档兜底：内联对象 → 同目录
`release.json` → jsDelivr 上的 `release.json`。所以“发版当场就有正确版本号”，CDN/缓存没刷也不至于露出梅花括号。

`tools\release.ps1` 已经把顺序固定死（漏一步就停在原地）：

| 步 | 做什么 |
|---|---|
| ① | `sync-site` 写 release.json / JSON-LD / sitemap |
| ② | `publish.ps1` 打包（AOT + zip + Setup + update.json） |
| ②b | 再跑一次 `sync-site`（这次 dist 里已有本版产物 → 大小才是真的），然后 `deploy-site` 传 OSS |
| ③~⑥ | 提交、推 main+tag、GitHub Release、GitCode 镜像、老客户端过渡清单 |

**所以发版还是那一条命令**，只是多了一步自动上传：

```powershell
.\tools\release.ps1 -Notes dist\release-notes-<ver>.md
```

### 1.2 “按量付费，体积不能大” → 首屏砍到约 1/3

| 文件 | 之前 | 现在 | 说明 |
|---|---|---|---|
| `img/demo.gif` | 331 KB | **删了** | 首屏大图，占全站 2/3 |
| `img/demo.mp4` | — | **63 KB** | 同画面 H.264，`autoplay muted loop playsinline` |
| `img/demo-poster.jpg` | — | **12 KB** | 视频封面，首帧就出图，不等视频下载 |
| 白名单 12 个文件合计 | 约 490 KB | **约 250 KB** | 磁盘体积 |

再叠一层 **gzip**（`deploy-site` 对文本类做）：`index.html` 5.8 KB、`style.css` 2.5 KB、
`i18n.js` 34.6 KB、`site-release.js` 2.3 KB、`release.json` 不到 1 KB。
文本类合计约 47 KB。

**一次首次访问 ≈ 122 KB**（文本 47 + 封面 12 + 视频 63），视频进缓存后复访 ≈ 60 KB。

按量付费的钱主要花在**外网流出流量**（存储 0.09 元/GB/月，这点文件一个月不到 1 分钱）：

| 月访问量 | 流量 | 流量费（忙时 0.50 元/GB 粗算） |
|---|---|---|
| 1,000 次 | ≈ 122 MB | **≈ 0.06 元** |
| 10,000 次 | ≈ 1.2 GB | ≈ 0.6 元 |
| 100,000 次 | ≈ 12 GB | ≈ 6 元 |

**安装包永远不放 OSS**：zip 16.7 MB / Setup 13.9 MB，一个下载就是网站首屏的 100 倍流量。
下载继续走 GitHub Releases（国外）+ GitCode（国内），`release.json` 里已经分好了 `gh` / `cn` 两个地址。

## 2. 一次性准备（Bucket 侧，控制台点一遍）

1. **Bucket**：地域选国内（`cn-hangzhou` / `cn-shanghai` 都行），**读写权限 = 公共读**。
   默认会开“阻止公共访问”，要先去 `权限控制 → 阻止公共访问` 关掉。
2. **静态页面**：`数据管理 → 静态页面 → 设置`
   - 默认首页：`index.html`
   - 子目录首页：**不开通**（本站没有子目录首页）
   - 默认 404 页：先留空；以后要自定义就加一个 `error.html`（顺手加进 `deploy-site.ps1` 白名单）
3. **绑定域名**（`Bucket 配置 → 域名管理`）：`www.inkteach.com`，拿到 CNAME 目标
   （形如 `inkteach-site.oss-cn-hangzhou.aliyuncs.com`）。
   - 自定义域名绑定后 OSS 才会**在线预览 HTML**；用 Bucket 默认域名访问 `.html` 会被强制下载。
4. **HTTPS**：`数据管理 → 安全设置 → HTTPS` 上传证书（阿里云有免费证书额度）。
   OSS 自定义域名**不支持 HTTP→HTTPS 强跳**，要么接受 `http://` 直出，要么用云解析 URL 转发
   （免费版 2 条）把 `http://inkteach.com` 301 到 `https://www.inkteach.com`，要么以后在前面接 CDN。
5. **防盗链**（`数据管理 → 安全设置 → Referer`）：白名单 `*.inkteach.com`、`*.github.io`、`*.gitcode.com`，
   允许空 Referer（老浏览器直开链接）。省得别人扒你的 mp4 白花流量。

## 3. 一次性准备（本机）

```powershell
# ① ossutil 2.x
#    https://gosspublic.alicdn.com/ossutil/v2/2.4.1/ossutil-2.4.1-windows-amd64.zip
#    解压后把目录加进 PATH，然后：
ossutil config          # AccessKeyID / AccessKeySecret / Region=cn-hangzhou

# ② 站点配置（进 .gitignore，不进库）
copy tools\oss-site.local.json.example tools\oss-site.local.json
#    填 bucket / region（prefix 留空 = 传到 Bucket 根）
```

RAM 子账号的权限给到最小即可：`oss:PutObject`、`oss:ListParts`、`oss:AbortMultipartUpload`，
作用域限定这一个 Bucket。

第一次跑先看要传什么：

```powershell
.\tools\deploy-site.ps1 -DryRun
```

没问题再去掉 `-DryRun`。**没建 `oss-site.local.json` 时脚本直接跳过**，所以 `release.ps1` 里挂着它也不会
影响现在的 GitHub Pages 流程——这一步可以等备案下来再启用。

## 4. 域名分流（等备案通过再做）

`inkteach.com` 的 NS 已经在阿里云（`dns27/dns28.hichina.com`），云解析**免费版就够**：
它支持 `地域 = 中国地区 / 境外` 两种线路（免费版不支持分省、不支持分国家）。

| 主机记录 | 线路 | 类型 | 值 |
|---|---|---|---|
| `www` | 默认 | CNAME | `inkteach-site.oss-cn-hangzhou.aliyuncs.com` |
| `www` | 境外 | CNAME | `xuerenyi0.github.io` |
| `@` | 默认 | — | **别用 @ 的 CNAME**（会和以后的 MX 邮箱记录打架），用 URL 转发 301 → `https://www.inkteach.com` |

要点：

- **两条 `www` 记录必须都配**，缺“默认”那条会有部分请求解析不到（阿里云文档明确建议留默认线路兜底）。
- OSS 会校验 HTTP 的 `Host` 头必须和绑定的域名一致，所以**不能**“域名 A CNAME 到域名 B、B 再绑 OSS”，
  必须把真正对外访问的那个域名直接绑到 Bucket。
- Pages 那边的 `docs\CNAME` 保持 `www.inkteach.com` 不动——境外线路指回 Pages 时它还要用。
- 备案没下来之前，`www` 只有“境外”这一条能指到 Pages；**默认线路先别指 OSS 国内 Bucket**，
  国内 Bucket 绑未备案域名会被拦（备案是域名 + 国内节点的要求，跟 OSS 本身无关）。

## 5. 备案这件事（当前状态：未备案）

国内 Bucket 绑自定义域名 = 必须 ICP 备案。身边常见的两条路：

| 方式 | 花费 | 说明 |
|---|---|---|
| 轻量应用服务器（国内，包 3 个月以上） | 约 100~150 元起 | 自带 5 个备案服务码；服务器闲着也行，备案看的是“有这个资源” |
| 单独买备案服务码 | 100 元/个 | 阿里云官方售卖，买完绑到一个符合条件的云产品上 |

**备案期间官网照旧**：GitHub Pages 那条线完全不受影响，`docs\` 改动推上去就生效。
所以合理顺序是：先把瘦身和脚本落地（已完成），备案下来后再做第 4 节的解析切换。

## 6. 常见坑

| 现象 | 原因 |
|---|---|
| 打开域名直接下载文件 | 用的还是 Bucket 默认域名，没绑自定义域名 |
| 发版后还显示旧版本号 | `release.json` / `index.html` 被缓存了；`deploy-site` 给 html 设 0 秒、json 设 300 秒，CDN 前面的话还要刷缓存 |
| 中文变乱码 | `--content-type` 少了 `charset=utf-8`（脚本里已经带） |
| 页面上出现 `{V}` | `site-release.js` 没跑起来，或 `release.json` 带 BOM（`sync-site.ps1` 特意写成无 BOM） |
| 图片 403 | 防盗链白名单没放 `*.inkteach.com` 或空 Referer |
| 上传很慢 | `ossutil cp -r docs\` 会把 82 KB 的 `section-map.md` 等内部文档一起传；用 `deploy-site.ps1` 的白名单 |

## 7. 下一步可选优化（还没做）

- `i18n.js` 106 KB（gzip 34.6 KB）是最大的一块：按语言拆成 `i18n.zh-CN.js` 等 10 个文件、
  只加载当前语言，首屏文本能从 ~47 KB 降到 ~15 KB。改动会碰语言切换逻辑，等有需要再做。
- `img/social-preview.png` 67 KB 只在分享卡片用，可以按需再压。
- 想要“HTTP 自动跳 HTTPS + 全球加速”，就在 OSS 前面加 CDN（回源 0.15 元/GB，比直出便宜），
  代价是多一层缓存要刷。
