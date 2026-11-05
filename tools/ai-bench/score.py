"""对 bench.py 落盘的原始结果评分：CER / 行准确率 / 公式行准确率。

用法：
  python score.py            # 打印总表
  python score.py --detail qwen3-vl-32b b1   # 打印逐行对照
"""
import argparse
import glob
import json
import os
import re
import sys

BASE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(BASE, "raw")

# 注意：这里是 str.replace，pattern 必须是**单反斜杠**（r"\in"），
# 写成 r"\\in" 会变成“两个反斜杠”，永远匹配不到，符号随后被删掉——会虚高 CER。
TEX = [
    (r"\not\subset", "⊄"), (r"\not\subset", "⊄"),
    (r"\notin", "∉"), (r"\in", "∈"),
    (r"\nsubseteq", "⊈"), (r"\subseteq", "⊆"), (r"\subset", "⊂"),
    (r"\cap", "∩"), (r"\cup", "∪"), (r"\parallel", "∥"),
    (r"\perp", "⊥"), (r"\angle", "∠"), (r"\triangle", "△"),
    (r"\because", "∵"), (r"\therefore", "∴"),
    (r"\leqslant", "≤"), (r"\geqslant", "≥"), (r"\leq", "≤"), (r"\geq", "≥"),
    (r"\neq", "≠"), (r"\times", "×"), (r"\div", "÷"), (r"\cdot", "·"),
    (r"\pm", "±"), (r"\pi", "π"), (r"\infty", "∞"),
    (r"\alpha", "α"), (r"\beta", "β"), (r"\theta", "θ"),
    (r"\overrightarrow", ""), (r"\mathrm", ""), (r"\text", ""),
    (r"\quad", " "), (r"\qquad", " "), (r"\cdots", "…"), (r"\ldots", "…"),
    (r"\vdots", "…"), (r"\dots", "…"),
]

FORMULA_CHARS = set("∈∉⊂⊄∩∪∥⊥∠△=+×÷√≤≥≠π")


def strip_markup(s):
    s = re.sub(r"```[a-zA-Z]*", "", s)
    s = re.sub(r"<[^>]+>", "", s)
    s = re.sub(r"\\frac\s*\{([^{}]*)\}\s*\{([^{}]*)\}", r"(\1)/(\2)", s)
    for pat, rep in TEX:
        s = s.replace(pat, rep)
    s = s.replace("$", "").replace("\\(", "").replace("\\)", "")
    s = s.replace("\\[", "").replace("\\]", "")
    s = s.replace("\\left", "").replace("\\right", "")
    s = re.sub(r"\\[a-zA-Z]+", "", s)
    s = s.replace("{", "").replace("}", "").replace("\\", "")
    s = re.sub(r"[#*>`|_~]", "", s)
    # 下标写法统一：A₁ / A1 / A_{1} -> A1
    sub = str.maketrans("₀₁₂₃₄₅₆₇₈₉", "0123456789")
    s = s.translate(sub)
    s = s.replace("′", "'").replace("’", "'")
    return s


def norm(s):
    """比较用的强归一：去空白、去标点、全角转半角。"""
    s = strip_markup(s)
    out = []
    for ch in s:
        o = ord(ch)
        if 0xFF01 <= o <= 0xFF5E:
            ch = chr(o - 0xFEE0)
        if ch.isspace():
            continue
        if ch in "，。、；：！？（）【】《》“”‘’…—·":
            continue
        out.append(ch)
    return "".join(out)


def lev(a, b):
    if a == b:
        return 0
    if not a:
        return len(b)
    if not b:
        return len(a)
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def sim(a, b):
    n = max(len(a), len(b))
    return 1.0 if n == 0 else 1.0 - lev(a, b) / n


def is_formula(line):
    return bool(FORMULA_CHARS & set(line))


def split_lines(text):
    ls = [l for l in strip_markup(text).splitlines()]
    return [l for l in ls if norm(l)]


def best_match_scores(gt_lines, hyp_lines):
    """每条 GT 行找最像的 hyp 行（对合并/拆行鲁棒）。"""
    H = [(norm(h), h) for h in hyp_lines]
    out = []
    for g in gt_lines:
        gn = norm(g)
        best, bh = 0.0, ""
        for hn, h in H:
            s = sim(gn, hn)
            if s > best:
                best, bh = s, h
        out.append((g, best, bh))
    return out


def score_one(gt_key, text, gt):
    gt_lines = gt[gt_key]["lines"]
    hyp_lines = split_lines(text)
    pairs = best_match_scores(gt_lines, hyp_lines)
    R = norm("".join(gt_lines))
    H = norm("".join(hyp_lines))
    cer = lev(R, H) / max(1, len(R))
    line_ok = sum(1 for _, s, _ in pairs if s >= 0.90) / len(pairs)
    fl = [(g, s, h) for g, s, h in pairs if is_formula(g)]
    f_ok = (sum(1 for _, s, _ in fl if s >= 0.95) / len(fl)) if fl else None
    return {
        "cer": cer, "len_gt": len(R), "len_hyp": len(H),
        "line_ok": line_ok, "n_lines": len(pairs),
        "formula_ok": f_ok, "n_formula": len(fl),
        "pairs": pairs,
    }


def load_rapidocr(gt):
    p = os.path.join(os.path.dirname(BASE), "rapidocr-result.json")
    if not os.path.exists(p):
        return {}
    data = json.load(open(p, encoding="utf-8"))
    mapping = {
        "板书-20261105-100213.png": "b1",
        "板书-20261103-173142.png": "b2",
    }
    out = {}
    for rec in data:
        b = mapping.get(rec["file"])
        if b:
            out[b] = "\n".join(l["text"] for l in rec["lines"])
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--detail", nargs=2, metavar=("MODEL", "BOARD"))
    args = ap.parse_args()

    gt = json.load(open(os.path.join(BASE, "gt.json"), encoding="utf-8"))

    if args.detail:
        mk, b = args.detail
        p = os.path.join(RAW, f"{mk}__{b}.json")
        rec = json.load(open(p, encoding="utf-8"))
        r = score_one(b, rec["text"], gt)
        print(f"{mk} / {b}  CER={r['cer']*100:.1f}%  行准确={r['line_ok']*100:.0f}%")
        for g, s, h in r["pairs"]:
            flag = "OK " if s >= 0.90 else "!! "
            print(f"{flag}{s:.2f} | 标准: {g}")
            if s < 0.90:
                print(f"        | 识别: {h}")
        return

    rows = []
    rapid = load_rapidocr(gt)
    if rapid:
        for b, text in rapid.items():
            r = score_one(b, text, gt)
            rows.append(("RapidOCR(本地)", b, r))

    for p in sorted(glob.glob(os.path.join(RAW, "*.json"))):
        rec = json.load(open(p, encoding="utf-8"))
        b = rec["board"]
        if b not in gt:
            continue
        if rec.get("error") or not rec.get("text"):
            rows.append((rec["model_key"], b, None))
            continue
        r = score_one(b, rec["text"], gt)
        r["seconds"] = rec["seconds"]
        r["usage"] = rec.get("usage") or {}
        rows.append((rec["model_key"], b, r))

    print(f"{'模型':22s} {'板':3s} {'CER':>7s} {'行准':>6s} {'公式行':>7s} {'耗时':>7s} {'输出token':>9s}")
    print("-" * 78)
    for mk, b, r in rows:
        if r is None:
            print(f"{mk:22s} {b:3s}   ERROR")
            continue
        f = "-" if r["formula_ok"] is None else f"{r['formula_ok']*100:.0f}%"
        sec = f"{r.get('seconds', 0):.1f}s"
        ct = r.get("usage", {}).get("completion_tokens", "-")
        print(f"{mk:22s} {b:3s} {r['cer']*100:6.1f}% {r['line_ok']*100:5.0f}% {f:>7s} {sec:>7s} {str(ct):>9s}")


if __name__ == "__main__":
    sys.exit(main())
