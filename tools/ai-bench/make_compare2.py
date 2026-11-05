"""图片在上、各模型结果在下、公式由 latex2mathml 预渲染成 MathML（离线、无 JS 依赖）。"""
import base64
import json
import os
import re
import sys

BASE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE)
import score as S  # noqa: E402
from latex2mathml.converter import convert  # noqa: E402

GT = json.load(open(os.path.join(BASE, "gt.json"), encoding="utf-8"))
BOARDS = r"C:\Users\LHL-XZX\AppData\Local\Temp\opencode\boards"
IMGS = {"b1": "板书-20261105-100213.png",
        "b2": "板书-20261103-173142.png",
        "b3": "板书-20261023-105034.png"}

SHOW = [
    ("ark-db-2-1-lite", "豆包 Seed 2.1 Lite", "火山方舟", "🥇 综合最优"),
    ("ark-db-evolving", "豆包 Seed Evolving", "火山方舟", "b2 最准"),
    ("ark-deepseek-v4-1-flash", "DeepSeek-V4.1-Flash", "火山方舟", "快"),
    ("deepseek-flash", "DeepSeek-V4.1-Flash", "DeepSeek 官方", "最快"),
    ("bl-dsv41flash-nothink", "DeepSeek-V4.1-Flash", "阿里百炼", "备用"),
    ("ark-db-2-1-pro", "豆包 Seed 2.1 Pro", "火山方舟", ""),
    ("ark-kimi-k3-1-pro", "Kimi K3.1 Pro", "火山方舟", ""),
    ("zy-glm-4v", "GLM-4V", "智谱", ""),
    ("zy-glm-5.3-flashx", "GLM-5.3-FlashX", "智谱", ""),
    ("ds-qwen35-omni-flash", "qwen3.5-omni-flash", "阿里百炼", "最快"),
    ("ds-qwen3-vl-plus", "qwen3-vl-plus", "阿里百炼", ""),
    ("RapidOCR", "本地 RapidOCR", "本机", "对照组"),
]

MATH_RE = re.compile(r"(\$\$.+?\$\$|\\\[.+?\\\]|\\\(.+?\\\)|\$[^$\n]+?\$)", re.S)


def esc(s):
    return (s or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def render_text(text):
    """把文本里的 $...$ / \\(...\\) 换成 MathML；失败就原样显示。"""
    out = []
    pos = 0
    for m in MATH_RE.finditer(text or ""):
        out.append(esc(text[pos:m.start()]))
        tok = m.group(0)
        disp = False
        if tok.startswith("$$"):
            tex, disp = tok[2:-2], True
        elif tok.startswith("\\["):
            tex, disp = tok[2:-2], True
        elif tok.startswith("\\("):
            tex = tok[2:-2]
        else:
            tex = tok[1:-1]
        try:
            out.append('<span class="m">' +
                       convert(tex.strip(), display="block" if disp else "inline") +
                       '</span>')
        except Exception:
            out.append('<span class="raw">' + esc(tok) + '</span>')
        pos = m.end()
    out.append(esc((text or "")[pos:]))
    return "".join(out)


def load(b):
    out = {}
    for mk, name, plat, tag in SHOW:
        if mk == "RapidOCR":
            p = os.path.join(os.path.dirname(BASE), "rapidocr-result.json")
            if os.path.exists(p):
                for rec in json.load(open(p, encoding="utf-8")):
                    if rec["file"] == IMGS[b]:
                        out[mk] = {"name": name, "plat": plat, "tag": tag,
                                   "text": "\n".join(l["text"] for l in rec["lines"]),
                                   "seconds": rec["seconds"], "usage": None}
            continue
        p = os.path.join(BASE, "raw", f"{mk}__{b}.json")
        if not os.path.exists(p):
            continue
        r = json.load(open(p, encoding="utf-8"))
        if r.get("error") or not r.get("text"):
            continue
        out[mk] = {"name": name, "plat": plat, "tag": tag, "text": r["text"],
                   "seconds": r.get("seconds", 0), "usage": r.get("usage")}
    return out


def badges(b, text):
    if b not in GT:
        return ""
    r = S.score_one(b, text, GT)
    cls = "good" if r["cer"] < 0.06 else ("mid" if r["cer"] < 0.12 else "bad")
    return (f"<span class='badge {cls}'>CER {r['cer']*100:.1f}%</span>"
            f"<span class='badge'>行准 {r['line_ok']*100:.0f}%</span>")


def img_b64(b):
    with open(os.path.join(BOARDS, IMGS[b]), "rb") as f:
        return base64.b64encode(f.read()).decode()


sections = []
for b in ("b1", "b3"):
    models = load(b)
    cards = []
    if b in GT:
        cards.append(
            "<article class='card gt'><header><h3>标准答案</h3>"
            "<span class='plat'>人工逐行转录</span></header>"
            f"<div class='md'>{render_text(chr(10).join(GT[b]['lines']))}</div></article>")
    for mk, m in models.items():
        u = m.get("usage") or {}
        tok = u.get("completion_tokens")
        meta = f"{m['seconds']:.1f}s" + (f" · {tok} tok" if tok else "")
        star = f"<span class='badge star'>{esc(m['tag'])}</span>" if m["tag"] else ""
        cls = "card bad" if mk == "RapidOCR" else "card"
        cards.append(
            f"<article class='{cls}'><header><h3>{esc(m['name'])}</h3>"
            f"<span class='plat'>{esc(m['plat'])}</span>{star}"
            f"<span class='meta'>{meta}</span>{badges(b, m['text'])}</header>"
            f"<div class='md'>{render_text(m['text'])}</div></article>")
    title = GT[b]["name"] if b in GT else "数列（累加法/累乘法）"
    sections.append(
        f"<section id='{b}' class='{'on' if b == 'b1' else ''}'><h2>{b} · {esc(title)}</h2>"
        f"<div class='board'><img src='data:image/png;base64,{img_b64(b)}' alt='{b}'></div>"
        f"<div class='grid'>{''.join(cards)}</div></section>")

HTML = """<!doctype html><meta charset="utf-8">
<title>板书识别 · 公式已渲染对比</title>
<style>
:root{color-scheme:light dark}
body{font:15px/1.8 "Microsoft YaHei",system-ui,sans-serif;margin:0;background:#f5f5f7;color:#1c1c1e}
header.top{padding:16px 20px 6px;background:#fff;border-bottom:1px solid #e5e5ea;position:sticky;top:0;z-index:9}
header.top h1{font-size:17px;margin:0 0 3px}
header.top p{margin:0;color:#6b6b70;font-size:12px}
nav{display:flex;gap:6px;padding:9px 20px;background:#fff;border-bottom:1px solid #e5e5ea;position:sticky;top:62px;z-index:8}
nav button{border:1px solid #d6d6db;background:#fff;border-radius:99px;padding:5px 15px;cursor:pointer;font:inherit}
nav button.on{background:#0a84ff;border-color:#0a84ff;color:#fff}
section{display:none;padding:16px 20px 60px;max-width:1600px;margin:0 auto}
section.on{display:block}
h2{font-size:16px;margin:0 0 10px}
.board{background:#fff;border:1px solid #e5e5ea;border-radius:12px;padding:10px;margin-bottom:16px}
.board img{width:100%;display:block;border-radius:8px;max-height:72vh;object-fit:contain;background:#fff}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(440px,1fr));gap:14px;align-items:start}
.card{background:#fff;border:1px solid #e5e5ea;border-radius:12px;padding:12px 15px}
.card>header{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-bottom:8px;padding-bottom:7px;border-bottom:1px solid #f0f0f2}
.card h3{font-size:13.5px;margin:0}
.plat{font-size:11px;color:#8e8e93;background:#f2f2f5;border-radius:5px;padding:1px 6px}
.meta{font-size:11px;color:#8e8e93}
.badge{font-size:11px;color:#444;background:#f0f0f3;border-radius:5px;padding:1px 6px}
.badge.good{background:#e6f7ec;color:#0a6c2f;font-weight:600}
.badge.mid{background:#fff4dd;color:#8a5a00}
.badge.bad{background:#fde8e8;color:#a11}
.badge.star{background:#eef2ff;color:#2743b3}
.md{font-size:14px;line-height:2.15;white-space:pre-wrap;word-break:break-word}
.md .m{padding:0 2px}
.md .raw{background:#fdecec;border-radius:3px;padding:0 3px;font-family:Consolas,monospace;font-size:12px}
math{font-size:1.12em}
math[display="block"]{margin:.35em 0}
.card.gt{border-color:#0a84ff66;background:#f7fbff}
.card.gt h3{color:#0a6cff}
.card.bad{border-color:#ffb3b3;background:#fff8f8}
@media (prefers-color-scheme:dark){
 body{background:#111;color:#e5e5ea}
 header.top,nav,.board,.card{background:#1a1a1c;border-color:#2c2c2e}
 .card>header{border-color:#2c2c2e}.plat{background:#2a2a2e}
 .card.gt{background:#16202b;border-color:#2f5a8a}
 .card.bad{background:#2a1616;border-color:#6b2b2b}
 .badge{background:#2a2a2e;color:#ccc}
 .badge.good{background:#12351c;color:#7fdc9a}
 .badge.mid{background:#3a2f12;color:#e2b455}
 .badge.bad{background:#3a1616;color:#ff9a9a}
 .badge.star{background:#1b2450;color:#9db0ff}
 .md .raw{background:#3a1616}
}
</style>
<header class="top"><h1>手写板书识别 · 公式已渲染</h1>
<p>上面是原图，下面是各模型识别的结果。LaTeX 已离线渲染成公式；卡片右上角是耗时与 token 数，CER 越低越准。</p></header>
<nav><button data-t="b1" class="on">b1 点线面（有公式）</button><button data-t="b3">b3 数列（纯公式）</button></nav>
__SECTIONS__
<script>
const btns=[...document.querySelectorAll('nav button[data-t]')];
btns.forEach(b=>b.onclick=()=>{document.querySelectorAll('section').forEach(s=>s.classList.toggle('on',s.id===b.dataset.t));btns.forEach(x=>x.classList.toggle('on',x===b));});
</script>
"""

out = os.path.join(BASE, "compare2.html")
open(out, "w", encoding="utf-8").write(HTML.replace("__SECTIONS__", "".join(sections)))
print("written", out, os.path.getsize(out) // 1024, "KB")
