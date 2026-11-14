# Cloudflare Pages · Git 集成与官网域名迁移（2026-11-14）

> 目标：把 `www.inkteach.com` / `inkteach.com` 从旧的 Direct Upload 项目
> `inkteach`（还停在 9.4.4）迁到新的 **Git 集成** Pages 项目；
> 以后每次 push `main` 自动部署，不再手动上传。
>
> 现状：
> - `xuerenyi0.github.io/InkTeach`：GitHub Pages，已 9.8.0 + 10 语言
> - `www.inkteach.com` / `inkteach.com`：Cloudflare Pages 项目 `inkteach`，
>   仍是 9.4.4（Direct Upload，最后部署 2026-10-29）
> - `inkteach.pages.dev`：和官网同内容，确认是同一个 Pages 项目

---

## 0. 先判断：要不要保留两个站点

Cloudflare Pages **不保证**在中国大陆比 GitHub Pages 更快/更通：
- 免费版走海外节点（香港/新加坡/洛杉矶等），延迟通常 100–300ms；
- `*.pages.dev` 在部分网络会被拦，自定义域名通常好一些，但也不保证；
- 真正的国内下载通道仍是 **GitCode**（二进制），不是 Cloudflare。

建议：
1. 先建新项目，用 `*.pages.dev` 和自定义域名各测一次（手机 4G / 教室网络 / 家里宽带）。
2. 如果比 GitHub Pages 明显好，保留 **Cloudflare 主站 + GitHub Pages 备份**（零成本）。
3. 如果一样慢/一样被拦，只保留 GitHub Pages 即可，Cloudflare 项目删掉。
4. 不建议为了“多一个访问点”同时维护两套不同内容——两边都从 `docs/` 构建，内容不会分叉。

---

## 1. 先试最省事的：给现有项目接 Git

Cloudflare 官方文档说 Direct Upload 项目**不能**转 Git 集成（要新建项目），
但部分 Dashboard 版本会在现有项目里提供入口。先看一眼：

1. Cloudflare Dashboard → **Workers & Pages** → `inkteach`
2. **Settings** → **Builds & deployments**
3. 看有没有 **Connect to Git / Git repository** 入口
4. 有 → 连接 `XueRenYi0/InkTeach`，按下面第 2 步填；能成功就不用迁域名
5. 没有 / 灰色 → 走第 2 步新建项目

> 不要为了“试试”删旧项目；旧项目是现在官网的唯一线上版本。

---

## 2. 新建 Git 集成的 Pages 项目

1. **Workers & Pages** → **Create application** → **Pages** → **Connect to Git**
2. GitHub 授权：只给 `XueRenYi0/InkTeach` 仓库权限（不要给全部仓库）
3. 选择仓库：`XueRenYi0/InkTeach`
4. 项目名：`inkteach-git`（`inkteach` 已被旧项目占用）
   - 上线地址：`https://inkteach-git.pages.dev`
5. **Set up builds and deployments**：

| 配置项 | 值 |
|---|---|
| Production branch | `main` |
| Framework preset | `None` |
| Build command | `exit 0` |
| Build output directory | `docs` |
| Root directory | 留空（仓库根） |
| Environment variables | 不填 |
| Build watch paths | 可选：`docs/*`（只让官网改动触发构建） |

6. **Save and Deploy**
7. 打开 `https://inkteach-git.pages.dev/`，确认：
   - 首屏是 9.8.0
   - 右上角有语言下拉（10 种）
   - 下载按钮指向 `InkTeach-Releases` / GitCode 新地址
   - `/i18n.js` 能打开（不是 404 页面）

> 构建失败先看 Deployments → 最新一次 → Build log。
> 这个站是纯静态，不需要 npm install；`exit 0` 只是让 Cloudflare 有个成功命令。

---

## 3. 迁移自定义域名

> 同一个主机名不能同时挂在两个 Pages 项目上；先移除旧项目，再加到新项目。
> 中间会有几秒到几分钟的解析/证书空窗，建议在没人上课的时间做。

1. 旧项目 `inkteach` → **Custom domains**
   - 移除 `www.inkteach.com`
   - 移除 `inkteach.com`
   - **先不要删项目**（回滚用）
2. 新项目 `inkteach-git` → **Custom domains** → **Set up a custom domain**
   - 添加 `www.inkteach.com`
   - 添加 `inkteach.com`
3. DNS（域名在同一个 Cloudflare 账号时通常自动处理；否则手动加）：
   - `www` → CNAME → `inkteach-git.pages.dev`（Proxied / 橙色云）
   - 根域 `inkteach.com` → CNAME → `inkteach-git.pages.dev`
     （Cloudflare 支持根域 CNAME 展平；不要动 MX/TXT 等邮件记录）
4. 等 Custom domains 状态变成 **Active**（证书签发通常几分钟）
5. 测试：
   - `https://www.inkteach.com/` 和 `https://inkteach.com/` 都是 9.8.0
   - 右上角语言切换可用
   - 下载按钮不是 401
   - `http://` 会自动跳 `https://`

**回滚**：如果新项目有问题，把两个域名从 `inkteach-git` 移除，重新加回
`inkteach`；旧项目还在，立刻恢复。

**清理**：观察 3–7 天没问题后，再删除旧项目 `inkteach`。

---

## 4. 以后的发布流程

- 正常发版：改代码/文档 → `git push upstream main`
- Cloudflare Pages 自动构建 `main` + `docs/` 并部署
- GitHub Pages 也会自动构建同一份 `docs/`
- 不需要再手动上传 `inkteach-site-*.zip`
- 两边的差异只可能是构建时间；内容同源

> 如果只想保留一个：在 GitHub 仓库 Settings → Pages 里关掉 GitHub Pages，
> 或者删除 Cloudflare 项目；不要两边各改各的。

---

## 5. 验收清单

- [ ] `https://inkteach-git.pages.dev/` 是 9.8.0 + 10 语言
- [ ] `https://www.inkteach.com/` 是 9.8.0 + 10 语言
- [ ] `https://inkteach.com/` 是 9.8.0 + 10 语言
- [ ] 语言下拉 10 项，切到 English/日本語/Deutsch 正常
- [ ] 下载按钮指向 `InkTeach-Releases` / GitCode，返回 200
- [ ] `https://www.inkteach.com/i18n.js` 返回真正的 JS（不是 200 的 index 回退）
- [ ] 手机 4G 不开代理能打开（至少能打开）
- [ ] 旧项目 `inkteach` 保留，未删除
- [ ] GitHub push 后 Cloudflare Deployments 出现新构建
