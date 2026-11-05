# AI 板书识别基准

2026-11-05 建的，用来回答“手写板书能不能识别、选哪个模型、多少钱、多快”。
完整结论见 `调研/AI-板书识别-基准-2026-11-05.md`。

## 跑之前

```powershell
$env:SILICONFLOW_API_KEY = "sk-..."   # https://cloud.siliconflow.cn
$env:DASHSCOPE_API_KEY   = "sk-..."   # https://bailian.console.aliyun.com
```

密钥只从环境变量读，**不要写进 `keys.json` 提交**（该文件已 gitignore）。

## 三步

```powershell
# 1) 看有哪些模型 / 板子
python tools/ai-bench/bench.py --list

# 2) 跑识别（结果落盘 raw/，重复跑会跳过；--force 重跑）
python tools/ai-bench/bench.py --boards b1,b2,b3
python tools/ai-bench/bench.py --models qwen3-vl-32b,glm-4.5v --boards b1

# 3) 评分（不花钱，可反复算）
python tools/ai-bench/score.py
python tools/ai-bench/score.py --detail qwen3-vl-32b b1
```

## 输入从哪来

板书 PNG 由 `.inkb` 渲染：

```powershell
InkTeach.exe --inkboardpng "板书-20261105-100213.inkb" "板书-20261105-100213.png"
```

默认去 `%LOCALAPPDATA%\Temp\opencode\boards` 找图，可用 `BOARDS_DIR` 覆盖。

## 加一张新板子

1. 渲染出 PNG，放进 `BOARDS_DIR`；
2. 在 `bench.py` 的 `BOARDS` 里加一行；
3. 人工逐行转录，写进 `gt.json`（**标准答案错一行，CER 就虚高几个点**）；
4. 重跑 `bench.py` + `score.py`。

## 注意

- **专用 OCR 模型有自己的提示词**（PaddleOCR-VL 用 `OCR`，DeepSeek-OCR 用“转 Markdown”），
  硬套通用提示会复读。见 `PROMPT_OVERRIDE`。
- **不要缩图**：实测缩到 40% 准确率明显下降，而且并不更快。
- 整板识别 30–60s，选区识别 2–9s；产品里只该做选区。
