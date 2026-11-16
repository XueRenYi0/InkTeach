const fs = require('fs');
const path = require('path');
const dir = path.join(process.cwd(), 'docs');
const rel = JSON.parse(fs.readFileSync(path.join(dir, 'release.json'), 'utf8'));
const html = fs.readFileSync(path.join(dir, 'index.html'), 'utf8');
const i18n = fs.readFileSync(path.join(dir, 'i18n.js'), 'utf8');

// 1) 语法
new Function(fs.readFileSync(path.join(dir, 'site-release.js'), 'utf8'));
new Function(i18n);

// 2) 兜底对象必须和 release.json 一致
const m = html.match(/window\.INKTEACH_RELEASE_FALLBACK\s*=\s*(\{[\s\S]*?\n\});/);
if (!m) throw new Error('index.html 里没有 INKTEACH_RELEASE_FALLBACK');
const fb = eval('(' + m[1] + ')');
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);
if (fb.version !== rel.version) throw new Error(`兜底版本 ${fb.version} != release.json ${rel.version}`);
if (fb.setup.cn !== rel.setup.cn) throw new Error('setup.cn 不一致');
if (fb.zip.cn !== rel.zip.cn) throw new Error('zip.cn 不一致');
if (fb.setup.mb !== rel.setup.mb) throw new Error(`setup.mb ${fb.setup.mb} != ${rel.setup.mb}`);

// 3) 页面里不许还有写死的当前版本号（JSON-LD / 首屏兜底那两段除外——它们本来就要字面值）
const cur = rel.version;
const htmlBare = html
  .replace(/<script type="application\/ld\+json">[\s\S]*?<\/script>/, '')
  .replace(/<script>\s*\/\* 发版时由[\s\S]*?<\/script>/, '');
for (const [name, text] of [['index.html', htmlBare], ['i18n.js', i18n]]) {
  const lines = text.split(/\r?\n/);
  lines.forEach((l, i) => {
    if (l.includes(cur)) throw new Error(`${name}:${i + 1} 还写死了 ${cur}: ${l.trim()}`);
  });
}

// 4) 占位符齐全 + data-href 键合法
const keys = new Set([...html.matchAll(/data-href="([^"]+)"/g)].map(x => x[1]));
for (const k of keys) if (!['setup', 'zip', 'setup-cn', 'zip-cn'].includes(k)) throw new Error('未知 data-href: ' + k);
if (!keys.has('setup-cn')) throw new Error('缺 setup-cn 按钮');

// 5) 模拟页面渲染：把 i18n 的 zh 文案 + release.json 填一遍，确认没有残留 {V}
const i18nCtx = { window: {}, document: { readyState: 'complete', addEventListener() { }, querySelectorAll: () => [], getElementById: () => null, querySelector: () => null, documentElement: {}, title: '' }, navigator: { languages: ['zh-CN'] }, localStorage: { getItem: () => null, setItem() { } }, URLSearchParams: global.URLSearchParams, history: { replaceState() { } }, setTimeout };
const vm = require('vm');
vm.createContext(i18nCtx);
vm.runInContext(i18n, i18nCtx);
const zh = i18nCtx.window.INK_I18N_DATA['zh-CN'];
const map = { '{V}': rel.version, '{SETUP_MB}': String(rel.setup.mb), '{ZIP_MB}': String(rel.zip.mb), '{DATE}': rel.date };
let leftover = [];
for (const [k, v] of Object.entries(zh)) {
  let s = v;
  for (const [p, val] of Object.entries(map)) s = s.split(p).join(val);
  if (/\{\w+\}/.test(s)) leftover.push(k + ': ' + s);
}
if (leftover.length) throw new Error('文案里还有没填的占位：\n' + leftover.join('\n'));

console.log('site selftest PASS: version', rel.version, '/ setup', rel.setup.mb, 'MB / zip', rel.zip.mb, 'MB / data-href', [...keys].join(','));
