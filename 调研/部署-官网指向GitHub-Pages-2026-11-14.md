# 官网域名指向 GitHub Pages（2026-11-14 决策）

> 目标：`www.inkteach.com` / `inkteach.com` → GitHub Pages（仓库 `XueRenYi0/InkTeach`，
> `main` 分支 `/docs`）。Cloudflare 只保留 DNS，不再用 Cloudflare Pages 托管官网。
>
> 现状：
> - `xuerenyi0.github.io/InkTeach`：9.8.0 + 10 语言，push 自动更新
> - `www.inkteach.com` / `inkteach.com`：Cloudflare Pages 项目 `inkteach`，仍是 9.4.4
> - 旧下载按钮指向 `gitcode.com/XueRenYI0/InkTeach/...`，实测 401

---

## 为什么不用 Cloudflare Pages / 付费

- Cloudflare **Pages 不在 China Network 里**（官方 FAQ：Pages 因 `pages.dev` 证书不在大陆而不可用）。
- Pro（$25/月，年付 $20）和 Business（$250/月，年付 $200）都是海外节点，不会让国内“全部能访问”。
- Enterprise + China Network 是另一套合同：需要 ICP 备案、京东云内容审核、单独订阅；
  Enterprise 通常 $5k+/月起，且 Pages 仍不可用。对免费开源项目不值得。
- 本机实测：`pages.dev` 多次 30s 超时，`www.inkteach.com` 1–20s 不稳定；
  GitHub Pages 稳定在 0.7–1.0s。**当前网络下 GitHub Pages 反而更稳。**
- 如果以后目标学校网络打不开 GitHub Pages，正确做法是**国内云静态托管 + ICP 备案**
  （阿里云 OSS/CDN、腾讯云 COS/CDN），不是给 Cloudflare 付费。

---

## 如果 Cloudflare 账号找不到了（2026-11-14 实查）

- 域名 `inkteach.com` 的注册商是 **阿里云（万网）**，NS 在 Cloudflare。
- 当前能登录的 Cloudflare 账号是 `Xuerenyi1993@gmail.com`，但该账号 **名下没有
  `inkteach.com`**（API 查 zones 返回空）；域名在另一个 Cloudflare 账号里。
- 因此不必强求找回 Cloudflare 账号：直接在 **阿里云域名控制台** 把 NS 改成
  阿里云 DNS（`ns1.alidns.com` / `ns2.alidns.com`），再在阿里云 DNS 里加
  `www` CNAME + 根域 A 记录指到 GitHub Pages 即可。
- 当前公开 DNS 没有看到 MX / TXT 记录，常见子域也没有 A/CNAME；历史上 2020–2021
  的 cPanel/mail 证书属于前一个域名持有人，与现在无关。改 NS 前仍建议在阿里云
  控制台确认没有邮件/其他业务记录。

## 操作步骤

### 1. 先释放旧域名

Cloudflare Dashboard → **Workers & Pages** → 旧项目 `inkteach` → **Custom domains**：

- 移除 `www.inkteach.com`
- 移除 `inkteach.com`

**先不要删项目**，它是回滚用的。

### 2. GitHub Pages 设置自定义域名

GitHub → 仓库 `XueRenYi0/InkTeach` → **Settings → Pages** → **Custom domain**：

- 填 `www.inkteach.com` → Save
- GitHub 可能要求验证域名，给一条 `_github-pages-challenge-XueRenYi0` 的 TXT 记录；
  到 Cloudflare DNS 添加（**DNS only / 灰云**），回 GitHub 点 Verify
- 等 GitHub 显示 DNS check successful

### 3. 添加 `docs/CNAME`

在仓库 `docs/CNAME` 写一行：

```
www.inkteach.com
```

提交并 push。GitHub Pages 会把项目站从 `/InkTeach/` 提升到自定义域名根路径。

> 如果 GitHub 设置自定义域名时已经自动创建了 `docs/CNAME`，跳过这一步。

### 4. Cloudflare DNS

在 Cloudflare → 域名 `inkteach.com` → **DNS**：

| 类型 | 名称 | 内容 | 代理 |
|---|---|---|---|
| CNAME | `www` | `xuerenyi0.github.io` | **DNS only（灰云）** |
| A | `@` | `185.199.108.153` | DNS only |
| A | `@` | `185.199.109.153` | DNS only |
| A | `@` | `185.199.110.153` | DNS only |
| A | `@` | `185.199.111.153` | DNS only |

- **不要动 MX / TXT / 其他子域**（邮件、验证记录）。
- 不要开橙色云代理：GitHub Pages 的证书验证和源站 IP 在代理下容易出问题，
  而且 Cloudflare 海外节点也不会改善国内访问。
- 根域 `inkteach.com` 用 GitHub 官方 A 记录；`www` 是主域名，根域会由 GitHub 跳转到 `www`。

### 5. 等证书 + 开 HTTPS

GitHub → Settings → Pages：

- 等 `Certificate` 变成 Active（通常几分钟，最长 24h）
- 勾选 **Enforce HTTPS**

### 6. 验收

- [ ] `https://www.inkteach.com/` 是 9.8.0，右上角 10 语言下拉
- [ ] `https://inkteach.com/` 自动跳到 `https://www.inkteach.com/`
- [ ] 下载按钮指向 `InkTeach-Releases` / GitCode，返回 200
- [ ] `https://www.inkteach.com/i18n.js` 返回真正的 JS（`content-type: application/javascript`）
- [ ] 手机 4G 不开代理能打开（至少能打开）
- [ ] 旧 Cloudflare Pages 项目 `inkteach` 还在，未删除

### 7. 清理

观察 3–7 天没问题后，再删除 Cloudflare Pages 项目 `inkteach`。
Cloudflare 继续只做 DNS（免费），不影响邮箱和其他记录。

---

## 回滚

如果 GitHub Pages 在目标网络不可用：

1. GitHub → Settings → Pages → 移除 Custom domain
2. Cloudflare Pages 旧项目 `inkteach` → Custom domains → 重新添加
   `www.inkteach.com` / `inkteach.com`
3. DNS 恢复为 Cloudflare Pages 自动生成的记录（或 CNAME 到 `inkteach.pages.dev`）
4. 旧项目还在，立刻恢复 9.4.4

---

## 如果国内访问仍然不行

GitHub Pages 不是国内 CDN，部分学校网络可能打不开。真正稳定的方案只有：

- 国内云静态托管（阿里云 OSS + CDN / 腾讯云 COS + CDN）+ **ICP 备案**；
- 或 Gitee Pages / GitCode Pages 等国内镜像（需看当前政策与实名要求）。

这属于另一个项目，涉及备案（1–2 个月）和持续费用；在没确认“确实大量国内用户打不开”之前，
不建议先做。先把域名指到 GitHub Pages，用访问数据说话。
