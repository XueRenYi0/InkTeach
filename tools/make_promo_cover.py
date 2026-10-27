# -*- coding: utf-8 -*-
"""生成抖音封面 1080x1920"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_promo_video as m
from PIL import Image, ImageDraw

OUT = m.OUTDIR
BASE = m.render_frame(2.6)
PANEL = BASE.crop((36, 344, 1044, 1164)).convert("RGBA")

L = m.BG.copy().convert("RGBA")
m.brand(L, 1.0)
m.put(L, "PPT 上直接写", (540, 296), 106, True, m.INK)
m.put(L, "不用切白板，也不用找笔", (540, 424), 38, False, m.INK2)
L.alpha_composite(PANEL, (18, 474))

d = ImageDraw.Draw(L)
d.rounded_rectangle((150, 1330, 930, 1334), 2, fill=(222, 228, 238))
m.put(L, "InkTeach", (540, 1420), 76, True, m.INK)
m.put(L, "开源免费　·　Windows 10 / 11　·　免装 .NET", (540, 1512), 30, False, m.INK2)
m.put(L, "搜索：InkTeach课堂批注", (540, 1580), 28, False, (140, 150, 165))

L.convert("RGB").save(os.path.join(OUT, "抖音封面-1080x1920.png"))
print("cover ok")
