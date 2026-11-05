"""板书 OCR / 视觉大模型基准：统一调用 + 原始结果落盘。

用法：
  python bench.py --list
  python bench.py --models qwen3-vl-32b,qwen-vl-ocr --boards b1
  python bench.py --all

不做评分，只负责把每个 (模型, 图) 的原始返回、耗时、token 落成 JSON，
评分见 score.py，这样重算指标不用重跑（不烧钱）。
"""
import argparse
import base64
import json
import os
import re
import sys
import time
import urllib.request
import urllib.error

BASE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(BASE, "raw")
os.makedirs(RAW, exist_ok=True)

PROMPT = """逐行转录图片中的手写板书内容。要求：
- 按从上到下的书写顺序输出，每行一段；
- 汉字照写，数学符号尽量用 Unicode（∈ ∉ ⊂ ⊄ ∩ ∪ ∥ ⊥ ∠ △ ▱）；
- 复杂公式用 LaTeX，行内包在 $...$ 里；
- 不描述图形、箭头、圈注，不解释，不加标题，不加编号；
- 认不出的字写 ？，不要猜测。"""

PROVIDERS = {
    "siliconflow": ("https://api.siliconflow.cn/v1/chat/completions", "siliconflow"),
    "dashscope": ("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", "dashscope"),
}

# 专用 OCR 模型有自己的“惯用提示词”，硬套通用提示会退化成复读机（实测 PaddleOCR-VL/DeepSeek-OCR）。
# 通用 VLM 用 PROMPT；这里只覆盖有必要的，报告里会注明“各模型用各自最优提示”。
PROMPT_OVERRIDE = {
    "paddleocr-vl": "OCR",
    "deepseek-ocr": "请把图片内容转成 Markdown。",
    "ds-deepseek-ocr": "请把图片内容转成 Markdown。",
    "ds-qwen-vl-ocr": "识别图片中的文字，逐行输出纯文本，不要 HTML 标签，不要解释，公式用 LaTeX。",
}

MODELS = {
    # key: (provider, model id, 备注)
    "qwen3-vl-32b":        ("siliconflow", "Qwen/Qwen3-VL-32B-Instruct", "通用 VLM"),
    "qwen3-vl-8b":         ("siliconflow", "Qwen/Qwen3-VL-8B-Instruct", "小杯通用 VLM"),
    "qwen3-vl-32b-think":  ("siliconflow", "Qwen/Qwen3-VL-32B-Thinking", "带思考"),
    "glm-4.5v":            ("siliconflow", "zai-org/GLM-4.5V", "智谱 VLM"),
    "paddleocr-vl":        ("siliconflow", "PaddlePaddle/PaddleOCR-VL-1.5", "专用 OCR"),
    "deepseek-ocr":        ("siliconflow", "deepseek-ai/DeepSeek-OCR", "专用 OCR"),
    "ds-qwen3-vl-plus":    ("dashscope", "qwen3-vl-plus", "百炼旗舰 VLM"),
    "ds-qwen-vl-ocr":      ("dashscope", "qwen-vl-ocr", "百炼专用 OCR"),
    "ds-qwen35-ocr":       ("dashscope", "qwen3.5-ocr", "百炼新一代 OCR"),
    "ds-deepseek-ocr":     ("dashscope", "vanchin/deepseek-ocr", "百炼上的 DeepSeek-OCR"),
}

BOARDS = {
    "b1": "板书-20261105-100213.png",   # 点线面位置关系
    "b2": "板书-20261103-173142.png",   # 斜二测画法
    "b3": "板书-20261023-105034.png",   # 数列 累加法/累乘法
}

# 板书 PNG 目录：默认放临时目录，可用 BOARDS_DIR 覆盖
IMG_DIR = os.environ.get("BOARDS_DIR") or os.path.join(
    os.environ.get("LOCALAPPDATA", ""), "Temp", "opencode", "boards")


def load_keys():
    """密钥**只从环境变量读**，不落仓库。

        $env:SILICONFLOW_API_KEY = "sk-..."
        $env:DASHSCOPE_API_KEY   = "sk-..."

    开发机上也允许放一个 `keys.json`（同目录，已 gitignore），但默认不走这条路。
    """
    keys = {
        "siliconflow": os.environ.get("SILICONFLOW_API_KEY", ""),
        "dashscope": os.environ.get("DASHSCOPE_API_KEY", ""),
    }
    if not any(keys.values()):
        p = os.path.join(BASE, "keys.json")
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8-sig") as f:
                keys.update(json.load(f))
    if not keys["siliconflow"] and not keys["dashscope"]:
        raise SystemExit("没有密钥：请设置 SILICONFLOW_API_KEY / DASHSCOPE_API_KEY")
    return keys


def img_b64(name):
    with open(os.path.join(IMG_DIR, name), "rb") as f:
        return base64.b64encode(f.read()).decode("ascii")


def slug(s):
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", s)


def call(url, key, model, b64, prompt, timeout=180, retries=2):
    payload = {
        "model": model,
        "messages": [{
            "role": "user",
            "content": [
                {"type": "image_url", "image_url": {"url": "data:image/png;base64," + b64}},
                {"type": "text", "text": prompt},
            ],
        }],
        "temperature": 0,
        "max_tokens": 4096,
        "stream": False,
    }
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    last = None
    for attempt in range(retries + 1):
        req = urllib.request.Request(url, data=data, method="POST", headers={
            "Authorization": "Bearer " + key,
            "Content-Type": "application/json",
        })
        t0 = time.time()
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                body = r.read().decode("utf-8", "replace")
                dt = time.time() - t0
                return json.loads(body), dt, None
        except urllib.error.HTTPError as e:
            dt = time.time() - t0
            body = e.read().decode("utf-8", "replace")[:800]
            last = f"HTTP {e.code} {body}"
            if e.code in (400, 401, 403, 404):
                return None, dt, last
        except Exception as e:
            dt = time.time() - t0
            last = f"{type(e).__name__}: {e}"
        if attempt < retries:
            time.sleep(2 + attempt * 3)
    return None, 0.0, last


def extract_text(resp):
    try:
        msg = resp["choices"][0]["message"]
        c = msg.get("content")
        if isinstance(c, str):
            return c
        if isinstance(c, list):
            return "".join(p.get("text", "") for p in c if isinstance(p, dict))
        return str(c)
    except Exception:
        return ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", default="")
    ap.add_argument("--boards", default="b1,b2,b3")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--force", action="store_true")
    args = ap.parse_args()

    if args.list:
        for k, (p, m, note) in MODELS.items():
            print(f"{k:22s} {p:12s} {m:38s} {note}")
        return

    keys = load_keys()
    model_keys = [s.strip() for s in args.models.split(",") if s.strip()] or list(MODELS)
    board_keys = [s.strip() for s in args.boards.split(",") if s.strip()]

    imgs = {b: img_b64(BOARDS[b]) for b in board_keys}
    print(f"images: {', '.join(f'{b}={len(imgs[b])//1024}KB' for b in board_keys)}", flush=True)

    for mk in model_keys:
        prov, mid, note = MODELS[mk]
        url, keyname = PROVIDERS[prov]
        key = keys[keyname]
        for b in board_keys:
            out_path = os.path.join(RAW, f"{slug(mk)}__{b}.json")
            if os.path.exists(out_path) and not args.force:
                print(f"skip {mk}/{b} (exists)", flush=True)
                continue
            prompt = PROMPT_OVERRIDE.get(mk, PROMPT)
            resp, dt, err = call(url, key, mid, imgs[b], prompt)
            rec = {
                "model_key": mk, "provider": prov, "model": mid, "board": b,
                "prompt": prompt,
                "seconds": round(dt, 2), "error": err,
                "text": extract_text(resp) if resp else "",
                "usage": (resp or {}).get("usage"),
                "raw_id": (resp or {}).get("id"),
            }
            with open(out_path, "w", encoding="utf-8") as f:
                json.dump(rec, f, ensure_ascii=False, indent=1)
            status = "ERR " + (err or "")[:80] if err else f"{rec['seconds']}s {len(rec['text'])}字"
            print(f"{mk:22s} {b}  {status}", flush=True)


if __name__ == "__main__":
    sys.exit(main())
