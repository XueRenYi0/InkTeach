/* InkTeach website: release data binding (2026-11-17).
 *
 * 为什么有它：发版时**不再让大模型改页面**。版本号、四个下载地址、安装包大小
 * 全部来自 `release.json`（tools\sync-site.ps1 生成），这个脚本把它们塞进页面。
 *
 * 三档兜底（任何一档失败都不会露出 {V} 这种占位）：
 *   ① index.html 里内联的 window.INKTEACH_RELEASE_FALLBACK（sync-site 每次发版就地改）；
 *   ② 同目录 release.json（正常路径，CDN 缓存友好）；
 *   ③ jsDelivr 上的 release.json（Pages 没更新时也能拿到）。
 *
 * 约定：
 *   · HTML / i18n.js 里写 `{V}` = 版本号，`{SETUP_MB}` / `{ZIP_MB}` = 大小（MB）；
 *   · 带 `data-href="setup"|"zip"|"setup-cn"|"zip-cn"` 的链接，href 由这里赋；
 *   · `#updateCards` 里如果有 highlights，就重画那三张卡。
 */
(function () {
  "use strict";

  var candidates = [
    "release.json?_=" + Date.now(),
    "https://cdn.jsdelivr.net/gh/XueRenYi0/InkTeach@main/docs/release.json?_=" + Date.now()
  ];

  var current = window.INKTEACH_RELEASE_FALLBACK || null;
  var settled = false;

  function fetchJson(i) {
    if (i >= candidates.length) { settled = true; return; }
    var xhr = new XMLHttpRequest();
    try {
      xhr.open("GET", candidates[i], true);
      xhr.timeout = 6000;
      xhr.onreadystatechange = function () {
        if (xhr.readyState !== 4) return;
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            var rel = JSON.parse(xhr.responseText);
            if (rel && rel.version) { current = rel; apply(rel); settled = true; return; }
          } catch (e) { }
        }
        fetchJson(i + 1);
      };
      xhr.ontimeout = function () { fetchJson(i + 1); };
      xhr.onerror = function () { fetchJson(i + 1); };
      xhr.send();
    } catch (e) { fetchJson(i + 1); }
  }

  function text(v) {
    return (v === null || v === undefined) ? "" : String(v);
  }

  // 大小只留数字：空 / 非数字都当"没有"（页面就不显示"多大"那一句）。
  function num(v) {
    return (v === null || v === undefined || v === "" || isNaN(Number(v))) ? "" : String(v);
  }

  // 占位替换：**只动 textContent / innerHTML 里的 {…}**，不碰其它结构。
  function fillPlaceholders(rel) {
    var map = {
      "{V}": text(rel.version),
      "{SETUP_MB}": num(rel.setup && rel.setup.mb),
      "{ZIP_MB}": num(rel.zip && rel.zip.mb),
      "{DATE}": text(rel.date)
    };
    var nodes = document.querySelectorAll("[data-i18n],[data-i18n-html],.note,.pill,footer p");
    for (var i = 0; i < nodes.length; i++) {
      var el = nodes[i];
      var src = el.innerHTML;
      if (src.indexOf("{") < 0) continue;
      var next = src;
      for (var k in map) {
        if (!Object.prototype.hasOwnProperty.call(map, k)) continue;
        next = next.split(k).join(map[k]);
      }
      if (next !== src) el.innerHTML = next;
    }
  }

  function setLinks(rel) {
    var pairs = {
      "setup": rel.setup && rel.setup.gh,
      "zip": rel.zip && rel.zip.gh,
      "setup-cn": rel.setup && rel.setup.cn,
      "zip-cn": rel.zip && rel.zip.cn
    };
    var nodes = document.querySelectorAll("[data-href]");
    for (var i = 0; i < nodes.length; i++) {
      var href = pairs[nodes[i].getAttribute("data-href")];
      if (href) nodes[i].setAttribute("href", href);
    }
  }

  function esc(s) {
    return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }

  // 首页「最近更新」三张卡：release.json 给了 highlights 就重画，没给就沿用静态卡。
  function renderHighlights(rel) {
    var host = document.getElementById("updateCards");
    if (!host || !rel.highlights || !rel.highlights.length) return;
    var html = "";
    for (var i = 0; i < rel.highlights.length; i++) {
      var h = rel.highlights[i];
      if (!h || !h.title) continue;
      html += '<div class="card"><h3>' + esc(h.title) + "</h3><p>" + esc(h.body || "") + "</p></div>";
    }
    if (html) host.innerHTML = html;
  }

  function apply(rel) {
    if (!rel || !rel.version) return;
    fillPlaceholders(rel);
    setLinks(rel);
    renderHighlights(rel);
  }

  // i18n.js 每次切换语言都会整段重写文案（占位又回来了），所以切完要再填一次。
  document.addEventListener("inkteach:lang", function () {
    if (current) apply(current);
  });

  // 先用手上的（内联兜底），再把网络上的换上来。
  if (current) apply(current);
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", function () { if (current) apply(current); });
  }
  fetchJson(0);
})();
