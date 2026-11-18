const fs = require('fs');
const path = require('path');
const dir = path.join(process.cwd(), 'docs');
const rel = JSON.parse(fs.readFileSync(path.join(dir, 'release.json'), 'utf8'));

// 2026-11-17 拆页：官网从「一个 index.html」变成六个页面。
// 这个自检原来只读 index.html，拆完就管不住新页面了（新页写死版本号 / 死链 / 非法 data-href 都不会被发现），
// 所以下面统一按「页面集合」处理；index.html 特有的检查（内联兜底对象）单独留在第 2 段。
const pageFiles = fs.readdirSync(dir).filter(f => f.endsWith('.html')).sort();
if (!pageFiles.includes('index.html')) throw new Error('docs 里没有 index.html');
const pages = pageFiles.map(f => ({ name: f, text: fs.readFileSync(path.join(dir, f), 'utf8') }));
const html = pages.find(p => p.name === 'index.html').text;
const i18n = fs.readFileSync(path.join(dir, 'i18n.js'), 'utf8');

// 1) 语法
new Function(fs.readFileSync(path.join(dir, 'site-release.js'), 'utf8'));
new Function(i18n);

// 2) 兜底对象必须和 release.json 一致
const m = html.match(/window\.INKTEACH_RELEASE_FALLBACK\s*=\s*(\{[\s\S]*?\n\});/);
if (!m) throw new Error('index.html 里没有 INKTEACH_RELEASE_FALLBACK');
const fb = eval('(' + m[1] + ')');
if (fb.version !== rel.version) throw new Error(`兜底版本 ${fb.version} != release.json ${rel.version}`);
if (fb.setup.cn !== rel.setup.cn) throw new Error('setup.cn 不一致');
if (fb.zip.cn !== rel.zip.cn) throw new Error('zip.cn 不一致');
if (fb.setup.mb !== rel.setup.mb) throw new Error(`setup.mb ${fb.setup.mb} != ${rel.setup.mb}`);

// 3) 页面里不许还有写死的当前版本号（JSON-LD / 首屏兜底那两段除外——它们本来就要字面值）
//    index.html 之外的内联兜底对象不存在（拆页时约定只在首页放一份），所以别的页面整份都要干净。
const cur = rel.version;
const stripWritable = text => text
  .replace(/<script type="application\/ld\+json">[\s\S]*?<\/script>/g, '')
  .replace(/<script>\s*\/\* 发版时由[\s\S]*?<\/script>/g, '')
  .replace(/<script>(?=[\s\S]*?INKTEACH_RELEASE_FALLBACK)[\s\S]*?<\/script>/g, '');
for (const [name, text] of [...pages.map(p => [p.name, stripWritable(p.text)]), ['i18n.js', i18n]]) {
  const lines = text.split(/\r?\n/);
  lines.forEach((l, i) => {
    if (l.includes(cur)) throw new Error(`${name}:${i + 1} 还写死了 ${cur}: ${l.trim()}`);
  });
}

// 4) 每页的 <head> 基本盘：title + canonical（拆页后三页共用一句 description 就没意义了，
//    这里只卡必须存在的两项；内容好坏靠人看，不靠脚本猜）。
for (const p of pages) {
  if (!/<title>[^<]+<\/title>/.test(p.text)) throw new Error(p.name + ' 没有 <title>');
  if (!/<link rel="canonical" href="https?:\/\/[^"]+"/.test(p.text)) throw new Error(p.name + ' 没有 <link rel="canonical">');
  if (!/<meta name="description" content="[^"]+"/.test(p.text)) throw new Error(p.name + ' 没有 meta description');
}
// 结构化数据只留首页一份，别的页面别重复（重复会跟首页打架）。
for (const p of pages) {
  const has = /<script type="application\/ld\+json">/.test(p.text);
  if (p.name !== 'index.html' && has) throw new Error(p.name + ' 不该带 JSON-LD（只留 index.html）');
}

// 5) data-href 键合法 + 全局至少一个 setup-cn
const keys = new Set();
for (const p of pages) for (const x of p.text.matchAll(/data-href="([^"]+)"/g)) keys.add(x[1]);
for (const k of keys) if (!['setup', 'zip', 'setup-cn', 'zip-cn'].includes(k)) throw new Error('未知 data-href: ' + k);
if (!keys.has('setup-cn')) throw new Error('缺 setup-cn 按钮');

// 6) 内部链接：href="xxx.html" / href="#xxx" 必须真的存在（拆页最容易留下死锚点）。
//    只查站内相对链接；外链、图片、css、release.json、./ 这些不归这段管。
const anchorsOf = text => new Set([...text.matchAll(/\sid="([^"]+)"/g)].map(x => x[1]));
const pageByName = new Map(pages.map(p => [p.name, p]));
for (const p of pages) {
  const own = anchorsOf(p.text);
  for (const x of p.text.matchAll(/href="([^"]+)"/g)) {
    const href = x[1];
    if (/^https?:|^mailto:|^\/\//.test(href)) continue;
    if (href.startsWith('#')) {
      const id = href.slice(1);
      if (id && !own.has(id)) throw new Error(`${p.name}: 锚点 #${id} 在本页不存在`);
      continue;
    }
    const [file, frag] = href.split('#');
    if (!file.endsWith('.html')) continue;
    const target = pageByName.get(file.replace(/^\.\//, ''));
    if (!target) throw new Error(`${p.name}: 链接到不存在的页面 ${file}`);
    if (frag && !anchorsOf(target.text).has(frag)) throw new Error(`${p.name}: ${file} 里没有锚点 #${frag}`);
  }
}

// 7) 占位符齐全：页面里出现的 {V} / {SETUP_MB} / {ZIP_MB} / {DATE} 必须能被 release.json 填满。
//    拆页后占位符散在 index / download / i18n 文案里，这里按「字符串表」统一比对，
//    不再只认 index.html 那一段。
const map = {
  '{V}': rel.version,
  // 大小可能为 null（发版前 dist 里还没产物，sync-site 会写成 null）——这时页面按 site-release.js
  // 的规则变成空串，等于"不显示多大"，不算错误。所以这里也只要求"能被填掉"，不要求有数字。
  '{SETUP_MB}': rel.setup.mb == null ? '' : String(rel.setup.mb),
  '{ZIP_MB}': rel.zip.mb == null ? '' : String(rel.zip.mb),
  '{DATE}': rel.date
};
const phSources = [...pages.map(p => [p.name, p.text])];
if (!map['{V}']) throw new Error('release.json 没有 version');
if (!map['{DATE}']) throw new Error('release.json 没有 date');
for (const [name, text] of phSources) {
  const found = new Set([...text.matchAll(/\{([A-Z_]+)\}/g)].map(t => t[0]));
  for (const p of found) {
    if (!(p in map)) throw new Error(`${name}: 未知占位符 ${p}（只认 ${Object.keys(map).join(' / ')}）`);
  }
  // 出现占位符就必须挂 site-release.js，否则那一页永远显示 {V}（features/math/classroom 不该出现占位符）。
  if (found.size && !/src="site-release\.js"/.test(text)) {
    throw new Error(`${name}: 有占位符 {${[...found].join('} {')}} 却没加载 site-release.js`);
  }
}

// 8) 模拟页面渲染：先跑骨架 i18n.js（只有语言表 + 加载逻辑），再逐个跑 docs/i18n/<code>.js，
//    把 zh 文案 + release.json 填一遍，确认没有残留 {V}
const vm = require('vm');
const i18nCtx = {
  window: {},
  document: {
    readyState: 'complete', addEventListener() { }, querySelectorAll: () => [],
    getElementById: () => null, querySelector: () => null, documentElement: {}, title: '',
    // 骨架的 load() 会插 <script>；自检里不真的去拉文件，用一个空壳顶住（后面直接读文件跑）。
    createElement: () => ({ set src(v) { }, set async(v) { }, onload: null, onerror: null }),
    head: { appendChild() { } }
  },
  navigator: { languages: ['zh-CN'] }, localStorage: { getItem: () => null, setItem() { } },
  URLSearchParams: global.URLSearchParams, history: { replaceState() { } }, setTimeout
};
vm.createContext(i18nCtx);
vm.runInContext(i18n, i18nCtx);

const langs = i18nCtx.window.INK_I18N_LANGS;
if (!Array.isArray(langs) || langs.length !== 10) throw new Error('i18n.js 的 INK_I18N_LANGS 不是 10 种语言');
const codes = langs.map(l => l.code);

// 2026-11-17 按语言拆分后：每个语言一个文件，逐个跑进同一个 context，凑成完整字典。
const i18nDir = path.join(dir, 'i18n');
for (const code of codes) {
  const file = path.join(i18nDir, code + '.js');
  if (!fs.existsSync(file)) throw new Error('缺少语言文件 docs/i18n/' + code + '.js');
  const text = fs.readFileSync(file, 'utf8');
  new Function(text); // 语法
  vm.runInContext(text, i18nCtx);
}

const data = i18nCtx.window.INK_I18N_DATA;
const keysOf = code => Object.keys(data[code] || {});
for (const code of codes) {
  if (!data[code]) throw new Error('i18n/' + code + '.js 没有挂上 INK_I18N_DATA["' + code + '"]');
  const n = keysOf(code).length;
  // 2026-11-18：原来写死 96，加 key 就误报。改成「至少有 1 个，且下面按 zh-CN 逐个比对」。
  if (n < 1) throw new Error('i18n/' + code + '.js 是空的');
}
// 10 种语言的 key 集合必须完全一致，缺了就点名。
const baseKeys = keysOf('zh-CN');
for (const code of codes) {
  const have = new Set(keysOf(code));
  const missing = baseKeys.filter(k => !have.has(k));
  const extra = keysOf(code).filter(k => !baseKeys.includes(k));
  if (missing.length || extra.length) {
    throw new Error('i18n/' + code + '.js 与 zh-CN key 不一致；缺 [' + missing.join(', ') + ']，多 [' + extra.join(', ') + ']');
  }
}

// 9) 拆页的核心风险：卡片搬过去了，data-i18n 却搬漏/写错 → 那句文案永远停在中文。
//    这里反查：页面上挂的每个 key 必须真的在字典里。
const baseKeySet = new Set(baseKeys);
for (const p of pages) {
  for (const attr of ['data-i18n', 'data-i18n-html', 'data-i18n-alt', 'data-i18n-aria']) {
    const re = new RegExp(attr + '="([^"]+)"', 'g');
    for (const x of p.text.matchAll(re)) {
      if (!baseKeySet.has(x[1])) throw new Error(`${p.name}: ${attr}="${x[1]}" 在 i18n 字典里没有`);
    }
  }
}

const zh = data['zh-CN'];
let leftover = [];
for (const [k, v] of Object.entries(zh)) {
  let s = v;
  for (const [p, val] of Object.entries(map)) s = s.split(p).join(val);
  if (/\{\w+\}/.test(s)) leftover.push(k + ': ' + s);
}
if (leftover.length) throw new Error('文案里还有没填的占位：\n' + leftover.join('\n'));

// 10) 拆页后最容易犯的第二个错：页面搬过去了，某个 section / 卡片搬漏。
//     这里用「不重复锚点」兜一下：每页该有的锚点必须唯一出现一次（重复多半是复制粘贴带过来两份）。
const wantAnchors = {
  'index.html': ['what'],
  'features.html': ['features'],
  'math.html': ['math'],
  'classroom.html': ['classroom'],
  'download.html': ['install', 'updates', 'opensource']
};
for (const [f, ids] of Object.entries(wantAnchors)) {
  const page = pages.find(p => p.name === f);
  if (!page) throw new Error('缺页面 ' + f);
  for (const id of ids) {
    const n = (page.text.match(new RegExp('\\sid="' + id + '"', 'g')) || []).length;
    if (n !== 1) throw new Error(`${f}: 锚点 #${id} 出现 ${n} 次，应恰好 1 次`);
  }
  if (/href="#(features|math|classroom|install|updates|opensource)"/.test(page.text) && f === 'index.html') {
    throw new Error('index.html 还留着指向已删除区块的锚点');
  }
}

console.log('site selftest PASS: version', rel.version, '/ setup', rel.setup.mb, 'MB / zip', rel.zip.mb, 'MB / pages',
  pages.map(p => p.name).join(','), '/ data-href', [...keys].join(','));
