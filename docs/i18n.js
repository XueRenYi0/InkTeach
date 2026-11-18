/* InkTeach website translations (2026-11-17).
   2026-11-14 起这里放的是 10 种语言的完整字典（约 107 KB / gzip 34.5 KB），
   但每个访客只用 1 种 —— 它成了首屏最大的单个文本资源。2026-11-17 按语言拆开：
   本文件只剩骨架（语言表 + 选语言 + DOM 替换），字典改到 docs/i18n/<code>.js，
   由 load() 按需要拉当前语言那一份。

   兜底顺序：当前语言 → en → zh-CN。但拆开后 S 里通常只有当前语言，
   所以取不到就是取不到（valueFor 返回 null，对应节点保持 HTML 里写死的中文）。
   页面在语言文件到达之前显示 HTML 里写死的中文，这是预期的，不做骨架屏、不隐藏 body。 */
(function () {
  "use strict";

  // 2026-11-17 拆页：字典里只有一组 meta.*（单页时代写的是首页文案），六页套同一套会全部重名。
  // 子页面（有 .page-head h1 的）在这里一次性记下自己的标题和 meta，apply() 结束时写回去：
  //   · 中文用 <head> 里写好的 SEO 标题（带关键词，别被首页标题盖掉）；
  //   · 别的语言没有逐页 key（96 个 key 不动），用本页 h1 的当前语言文案 + InkTeach，
  //     h1 本来就挂着 data-i18n，切到英文/日文时标题也跟着走，六页仍然各不相同。
  // 以前这段是每页内联 12 行复制 4 份，现在收进来，改一次就够。
  var pageHead = document.querySelector(".page-head h1");
  var ownTitle = document.title;
  var ownMeta = [];
  (function () {
    var sels = ['meta[name="description"]', 'meta[property="og:title"]', 'meta[property="og:description"]'];
    for (var i = 0; i < sels.length; i++) {
      var el = document.querySelector(sels[i]);
      if (el) ownMeta.push([el, el.getAttribute("content")]);
    }
  })();

  var LANGS = [
    { code: "zh-CN", name: "简体中文" },
    { code: "en",    name: "English" },
    { code: "ja",    name: "日本語" },
    { code: "ko",    name: "한국어" },
    { code: "de",    name: "Deutsch" },
    { code: "fr",    name: "Français" },
    { code: "es",    name: "Español" },
    { code: "pt-BR", name: "Português (Brasil)" },
    { code: "ru",    name: "Русский" },
    { code: "it",    name: "Italiano" }
  ];

  // 字典由 i18n/<code>.js 往这里挂；骨架本身不放任何文案。
  var S = {};
  window.INK_I18N_DATA = S;

  // 每个语言只插一次 <script>。state[code] = "loading" | "loaded" | "failed"
  // failed 允许下次再试一次：一次网络抖动不该让这个语言永久不可用。
  var state = {};
  // 同一个语言还在下载时，后来的人排队等它，别再插第二个 <script>。
  var waiting = {};

  function load(lang, done) {
    var cb = typeof done === "function" ? done : function () { };
    if (state[lang] === "loaded") { cb(true); return; }
    if (state[lang] === "loading") { waiting[lang].push(cb); return; }
    state[lang] = "loading";
    waiting[lang] = [cb];
    // ok=false 表示 script 没拿到（网络断/文件不存在）：不标 loaded，下次进来还能再试。
    function finish(ok) {
      state[lang] = ok ? "loaded" : "failed";
      var list = waiting[lang] || [];
      waiting[lang] = [];
      for (var i = 0; i < list.length; i++) {
        try { list[i](!!ok); } catch (e) { }
      }
    }
    try {
      var el = document.createElement("script");
      el.src = "i18n/" + lang + ".js";
      el.async = false;
      el.onload = function () { finish(true); };
      el.onerror = function () { finish(false); };
      document.head.appendChild(el);
    } catch (e) {
      finish(false);
    }
  }

  function normalize(code) {
    if (!code) return null;
    var c = String(code).trim().toLowerCase().replace("_", "-");
    if (c === "zh" || c.indexOf("zh-") === 0) return "zh-CN";
    if (c === "pt" || c.indexOf("pt-") === 0) return "pt-BR";
    if (c === "en" || c.indexOf("en-") === 0) return "en";
    if (c === "ja" || c.indexOf("ja-") === 0) return "ja";
    if (c === "ko" || c.indexOf("ko-") === 0) return "ko";
    if (c === "de" || c.indexOf("de-") === 0) return "de";
    if (c === "fr" || c.indexOf("fr-") === 0) return "fr";
    if (c === "es" || c.indexOf("es-") === 0) return "es";
    if (c === "ru" || c.indexOf("ru-") === 0) return "ru";
    if (c === "it" || c.indexOf("it-") === 0) return "it";
    for (var i = 0; i < LANGS.length; i++) {
      if (LANGS[i].code.toLowerCase() === c) return LANGS[i].code;
    }
    return null;
  }

  function pickLanguage() {
    try {
      var q = new URLSearchParams(window.location.search).get("lang");
      var n = normalize(q);
      if (n) return n;
    } catch (e) { }
    try {
      var saved = normalize(localStorage.getItem("inkteach.lang"));
      if (saved) return saved;
    } catch (e) { }
    var nav = navigator.languages || [navigator.language || ""];
    for (var i = 0; i < nav.length; i++) {
      var n2 = normalize(nav[i]);
      if (n2) return n2;
    }
    return "zh-CN";
  }

  function valueFor(lang, key) {
    var t = S[lang] || {};
    if (t[key] != null) return t[key];
    var en = S.en || {};
    if (en[key] != null) return en[key];
    var zh = S["zh-CN"] || {};
    if (zh[key] != null) return zh[key];
    return null;
  }

  function apply(lang, pushUrl) {
    // 字典没到（加载失败）：这次什么也别改——尤其别把语言下拉框和 localStorage 改成
    // 一个其实没生效的语言。页面保持当前文字，用户再点一次还能重试。
    if (!S[lang]) {
      if (S["zh-CN"]) lang = "zh-CN";
      else return;
    }
    document.documentElement.lang = lang;

    var nodes = document.querySelectorAll("[data-i18n]");
    for (var i = 0; i < nodes.length; i++) {
      var v = valueFor(lang, nodes[i].getAttribute("data-i18n"));
      if (v != null) nodes[i].textContent = v;
    }
    var htmlNodes = document.querySelectorAll("[data-i18n-html]");
    for (var j = 0; j < htmlNodes.length; j++) {
      var h = valueFor(lang, htmlNodes[j].getAttribute("data-i18n-html"));
      if (h != null) htmlNodes[j].innerHTML = h;
    }
    var altNodes = document.querySelectorAll("[data-i18n-alt]");
    for (var k = 0; k < altNodes.length; k++) {
      var a = valueFor(lang, altNodes[k].getAttribute("data-i18n-alt"));
      if (a != null) altNodes[k].alt = a;
    }
    // 2026-11-17：首屏演示从 <img> 换成 <video>，它没有 alt，用 aria-label 承载同一句说明。
    var ariaNodes = document.querySelectorAll("[data-i18n-aria]");
    for (var k2 = 0; k2 < ariaNodes.length; k2++) {
      var ar = valueFor(lang, ariaNodes[k2].getAttribute("data-i18n-aria"));
      if (ar != null) ariaNodes[k2].setAttribute("aria-label", ar);
    }

    // 拆开后字典可能还没到（加载失败时也不会有），取不到就别写，免得标题变成 "null"。
    var title = valueFor(lang, "meta.title");
    if (title != null) document.title = title;
    var desc = document.querySelector('meta[name="description"]');
    if (desc) {
      var d = valueFor(lang, "meta.desc");
      if (d != null) desc.setAttribute("content", d);
    }
    var ogt = document.querySelector('meta[property="og:title"]');
    if (ogt) {
      var ogtv = valueFor(lang, "meta.ogtitle");
      if (ogtv != null) ogt.setAttribute("content", ogtv);
    }
    var ogd = document.querySelector('meta[property="og:description"]');
    if (ogd) {
      var ogdv = valueFor(lang, "meta.ogdesc");
      if (ogdv != null) ogd.setAttribute("content", ogdv);
    }
    var ogl = document.querySelector('meta[property="og:locale"]');
    if (ogl) {
      var locales = {
        "zh-CN": "zh_CN", en: "en_US", ja: "ja_JP", ko: "ko_KR", de: "de_DE",
        fr: "fr_FR", es: "es_ES", "pt-BR": "pt_BR", ru: "ru_RU", it: "it_IT"
      };
      ogl.setAttribute("content", locales[lang] || "en_US");
    }

    // 子页面把自己那一套标题/描述写回去（首页没有 .page-head，上面的通用值就是对的）。
    if (pageHead) {
      var h1Text = (pageHead.textContent || "").trim();
      if (lang === "zh-CN") {
        if (ownTitle) document.title = ownTitle;
      } else if (h1Text) {
        document.title = h1Text + " · InkTeach";
      }
      for (var mi = 0; mi < ownMeta.length; mi++) {
        ownMeta[mi][0].setAttribute("content", ownMeta[mi][1]);
      }
    }

    var sel = document.getElementById("langSelect");
    if (sel) sel.value = lang;

    var docLink = document.querySelector("[data-doc-link]");
    if (docLink) {
      docLink.href = lang === "zh-CN" ? "https://github.com/XueRenYi0/InkTeach/blob/main/README.md"
        : lang === "en" ? "https://github.com/XueRenYi0/InkTeach/blob/main/README.en.md"
          : "https://github.com/XueRenYi0/InkTeach/blob/main/README." + lang + ".md";
    }

    try { localStorage.setItem("inkteach.lang", lang); } catch (e) { }

    // 2026-11-17：版本号/下载链接不再写死在这里，改由 site-release.js 按 release.json 填。
    // 切语言时文案会被整段重写，所以要通知它再填一次（{V} / {SETUP_MB} 等占位）。
    try {
      var ev = document.createEvent("Event");
      ev.initEvent("inkteach:lang", false, false);
      document.dispatchEvent(ev);
    } catch (e) { }

    if (pushUrl) {
      try {
        var u = new URL(window.location.href);
        u.searchParams.set("lang", lang);
        history.replaceState(null, "", u.pathname + u.search + u.hash);
      } catch (e) { }
    }
  }

  // 已经真正生效的语言（用于加载失败时把下拉框复原，避免下拉框显示 ja、页面还是中文）。
  var applied = "";

  function init() {
    var sel = document.getElementById("langSelect");
    if (sel) {
      for (var i = 0; i < LANGS.length; i++) {
        var o = document.createElement("option");
        o.value = LANGS[i].code;
        o.textContent = LANGS[i].name;
        sel.appendChild(o);
      }
      // 切语言：先等字典到位，再整页替换，避免半中半外。
      sel.addEventListener("change", function () {
        var next = normalize(sel.value) || "zh-CN";
        load(next, function (ok) {
          if (ok && S[next]) { apply(next, true); return; }
          // 没拿到：下拉框退回已经生效的那个，中文页面不动。
          if (applied) sel.value = applied;
        });
      });
    }
    var lang = pickLanguage();
    load(lang, function (ok) {
      if (ok && S[lang]) { applied = lang; apply(lang, false); return; }
      // 首屏语言加载失败：退回中文（如果中文也没到，就停在 HTML 里写死的文案）。
      if (lang !== "zh-CN" && S["zh-CN"]) { applied = "zh-CN"; apply("zh-CN", false); }
    });
  }

  window.INK_I18N_LANGS = LANGS;

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
