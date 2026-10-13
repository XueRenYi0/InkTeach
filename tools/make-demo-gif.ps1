<#
  make-demo-gif.ps1 —— 把 `--demogif` 录出来的一串 BMP 帧拼成 GIF（README 头部那张动图）。

  用法：
    .\tools\make-demo-gif.ps1 -Dir tmp\demo -Out design\readme\00-演示.gif
    .\tools\make-demo-gif.ps1 -Dir tmp\demo -Out out.gif -Width 800 -Fps 8

  依赖：python + Pillow（Pillow 只在这一步用；产品 / 自检都不依赖它）。
  帧是 `--demogif` 用 `ScreenProbe` 截的 32 位 BMP，Pillow 逐张缩放调色板后合成。
#>
param(
    [Parameter(Mandatory = $true)][string]$Dir,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Width = 800,       # 输出宽度（帧会被等比缩到这么宽）
    [int]$Fps = 8
)
$ErrorActionPreference = "Stop"

$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { throw "需要 python（并装好 Pillow：pip install pillow）——只有拼图这一步要它" }

# 帧按文件名排序（--demogif 用的就是 0000.bmp 递增）
$pyCode = @'
import sys, glob, os
from PIL import Image
d, out, width, fps = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
files = sorted(glob.glob(os.path.join(d, "*.bmp")))
if not files:
    sys.exit("目录里没有 *.bmp 帧：" + d)
frames = []
for f in files:
    im = Image.open(f).convert("RGB")
    w, h = im.size
    im = im.resize((width, max(1, round(h * width / w))), Image.LANCZOS)
    frames.append(im.convert("P", palette=Image.ADAPTIVE, colors=128))
os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
frames[0].save(out, save_all=True, append_images=frames[1:],
               duration=int(1000 / fps), loop=0, optimize=True)
print("%d 帧 -> %s  %.2f MB" % (len(frames), out, os.path.getsize(out) / 1048576.0))
'@
$tmp = Join-Path $env:TEMP "inkteach-make-gif.py"
Set-Content -Path $tmp -Value $pyCode -Encoding UTF8
& python $tmp $Dir $Out $Width $Fps
if ($LASTEXITCODE -ne 0) { throw "拼 GIF 失败（python 退出码 $LASTEXITCODE）" }
