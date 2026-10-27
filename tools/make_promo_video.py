# -*- coding: utf-8 -*-
"""InkTeach 抖音宣传片生成器：1080x1920 / 30fps / ~39s"""
import os, math, subprocess, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

FFMPEG = "C:/Users/LHL-XZX/.workbuddy/binaries/ffmpeg/ffmpeg.exe"
W, H, FPS = 1080, 1920, 30
DUR = 39.0
ROOT = "C:/Users/LHL-XZX/Desktop/软件/InkTeach"
ASSET = ROOT + "/design/readme"
OUTDIR = ROOT + "/调研/宣传视频"
os.makedirs(OUTDIR, exist_ok=True)

FONT_B = "C:/Windows/Fonts/msyhbd.ttc"
FONT_R = "C:/Windows/Fonts/msyh.ttc"

INK = (22, 25, 31)
INK2 = (98, 106, 120)
BLUE = (45, 118, 240)
RED = (223, 58, 63)
WHITE = (255, 255, 255)
CAP_Y = 1320

_fc = {}
def F(size, bold=True):
    k = (size, bold)
    if k not in _fc:
        _fc[k] = ImageFont.truetype(FONT_B if bold else FONT_R, size)
    return _fc[k]

def clamp(x, a=0.0, b=1.0):
    return a if x < a else (b if x > b else x)

def E(x):
    x = clamp(x); return 1 - (1 - x) ** 3

def EIO(x):
    x = clamp(x); return 3 * x * x - 2 * x * x * x

def lerp(a, b, t):
    return a + (b - a) * t

# ---------------- 背景 ----------------
def make_bg():
    im = Image.new("RGB", (W, H), (250, 251, 253))
    d = ImageDraw.Draw(im)
    top, bot = (251, 252, 254), (230, 236, 244)
    for y in range(H):
        k = (y / (H - 1)) ** 1.3
        d.line((0, y, W, y), fill=tuple(int(lerp(top[i], bot[i], k)) for i in range(3)))
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse((-320, -360, 780, 620), fill=(110, 165, 255, 34))
    gd.ellipse((520, 1220, 1320, 1960), fill=(255, 205, 165, 26))
    glow = glow.filter(ImageFilter.GaussianBlur(110))
    return Image.alpha_composite(im.convert("RGBA"), glow).convert("RGB")

BG = make_bg()

# ---------------- 阴影 / 卡片 ----------------
PAD = 56
_sh = {}
def shadow(w, h, r=28, blur=22, alpha=42):
    key = (w, h, r, blur, alpha)
    if key in _sh:
        return _sh[key]
    im = Image.new("RGBA", (w + PAD * 2, h + PAD * 2), (0, 0, 0, 0))
    ImageDraw.Draw(im).rounded_rectangle((PAD, PAD + 8, PAD + w, PAD + h + 8), r,
                                         fill=(22, 28, 42, alpha))
    im = im.filter(ImageFilter.GaussianBlur(blur))
    _sh[key] = im
    return im

def card(L, box, r=28, fill=WHITE, sh=True, blur=22, salpha=42, outline=None, ow=0):
    x0, y0, x1, y1 = [int(v) for v in box]
    w, h = x1 - x0, y1 - y0
    if sh:
        L.alpha_composite(shadow(w, h, r, blur, salpha), (x0 - PAD, y0 - PAD))
    ImageDraw.Draw(L).rounded_rectangle((x0, y0, x1, y1), r, fill=fill,
                                        outline=outline, width=ow)

# ---------------- 文字 ----------------
def text_img(s, size, bold=True, fill=INK, pad=6):
    f = F(size, bold)
    bb = f.getbbox(s)
    im = Image.new("RGBA", (max(int(bb[2] - bb[0]) + pad * 2, 1),
                            max(int(bb[3] - bb[1]) + pad * 2, 1)), (0, 0, 0, 0))
    ImageDraw.Draw(im).text((pad - bb[0], pad - bb[1]), s, font=f, fill=fill)
    return im

def put(L, s, center, size=48, bold=True, fill=INK, alpha=1.0):
    if alpha <= 0.01:
        return
    im = text_img(s, size, bold, fill)
    if alpha < 0.999:
        im.putalpha(im.getchannel("A").point(lambda v: int(v * alpha)))
    L.alpha_composite(im, (int(center[0] - im.width / 2 + 6), int(center[1] - im.height / 2 + 6)))

def put_left(L, s, xy, size=40, bold=True, fill=INK, alpha=1.0):
    if alpha <= 0.01:
        return
    im = text_img(s, size, bold, fill)
    if alpha < 0.999:
        im.putalpha(im.getchannel("A").point(lambda v: int(v * alpha)))
    L.alpha_composite(im, (int(xy[0] - 6), int(xy[1] - 6)))

def cap(L, main, sub=None, a=1.0):
    if a <= 0.01:
        return
    size = 50 if len(main) <= 14 else 44
    im = text_img(main, size, True, (255, 255, 255))
    bw = min(int(im.width - 12 + 84), 960)
    bh = 98
    box = Image.new("RGBA", (bw, bh), (0, 0, 0, 0))
    ImageDraw.Draw(box).rounded_rectangle((0, 0, bw - 1, bh - 1), 30, fill=(22, 25, 31, 236))
    if a < 0.999:
        box.putalpha(box.getchannel("A").point(lambda v: int(v * a)))
    L.alpha_composite(box, ((W - bw) // 2, CAP_Y))
    put(L, main, (W // 2, CAP_Y + bh // 2), size, True, (255, 255, 255), a)
    if sub:
        put(L, sub, (W // 2, CAP_Y + bh + 44), 32, False, (104, 114, 130), a)

# ---------------- 素材 ----------------
def load(name):
    im = Image.open(os.path.join(ASSET, name)).convert("RGBA")
    return im

def crop_black(im, box=(0, 0, 1051, 701)):
    return im.crop(box)

def round_mask(im, r=18):
    m = Image.new("L", im.size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, im.width - 1, im.height - 1), r, fill=255)
    out = im.copy()
    out.putalpha(m)
    return out

A_FUNC = round_mask(crop_black(load("20-函数图像.png")), 10)
A_SOLID = round_mask(crop_black(load("22-立体几何.png")), 10)
A_BAR = load("01-主工具带.png")
A_PENS = round_mask(load("26-三种笔.png"), 14)
A_SHOT = round_mask(load("04-截图取景.png"), 14)
A_MORE = round_mask(load("02-更多面板.png"), 14)

def wheel_img(size=620):
    im = load("17-呼出盘-深色.png").resize((size, size), Image.LANCZOS)
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).ellipse((int(size * 0.035), int(size * 0.035),
                               size - int(size * 0.035), size - int(size * 0.035)), fill=255)
    im.putalpha(m)
    return im
A_WHEEL = wheel_img(640)

def fit(im, w, h):
    s = min(w / im.width, h / im.height)
    return im.resize((max(1, int(im.width * s)), max(1, int(im.height * s))), Image.LANCZOS)

def contain(L, im, box, alpha=1.0, scale=1.0):
    x0, y0, x1, y1 = box
    bw, bh = x1 - x0, y1 - y0
    t = fit(im, int(bw * scale), int(bh * scale))
    if alpha < 0.999:
        t = t.copy()
        t.putalpha(t.getchannel("A").point(lambda v: int(v * alpha)))
    L.alpha_composite(t, (int(x0 + (bw - t.width) / 2), int(y0 + (bh - t.height) / 2)))

def cursor(L, xy, a=1.0):
    if a <= 0.01:
        return
    im = Image.new("RGBA", (44, 58), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    pts = [(4, 4), (4, 50), (15, 39), (22, 55), (31, 50), (24, 35), (38, 35)]
    d.polygon(pts, fill=(255, 255, 255, 255), outline=(20, 22, 28, 255))
    d.line(pts + [pts[0]], fill=(20, 22, 28, 255), width=3, joint="curve")
    if a < 0.999:
        im.putalpha(im.getchannel("A").point(lambda v: int(v * a)))
    L.alpha_composite(im, (int(xy[0]), int(xy[1])))

def pill(L, s, center, size=26, fill=WHITE, fg=(58, 66, 80), padx=22, h=58,
         alpha=1.0, outline=(226, 231, 240)):
    im = text_img(s, size, True, fg)
    bw = im.width - 12 + padx * 2
    box = Image.new("RGBA", (bw, h), (0, 0, 0, 0))
    ImageDraw.Draw(box).rounded_rectangle((0, 0, bw - 1, h - 1), h // 2, fill=fill,
                                          outline=outline, width=2)
    if alpha < 0.999:
        box.putalpha(box.getchannel("A").point(lambda v: int(v * alpha)))
    L.alpha_composite(box, (int(center[0] - bw / 2), int(center[1] - h / 2)))
    put(L, s, center, size, True, fg, alpha)

# ================= 场景 =================
def s1(L, t, dur):
    """钩子"""
    d = ImageDraw.Draw(L)
    card(L, (70, 372, 1010, 1128), 32)
    put_left(L, "第二课时 · 函数的单调性", (118, 410), 26, False, (132, 142, 156))
    put_left(L, "函数的单调性", (116, 456), 48, True, INK)
    d.line((118, 536, 470, 536), fill=(228, 233, 241), width=3)
    put_left(L, "若 x1 < x2 时 f(x1) < f(x2)，", (118, 572), 31, False, (74, 82, 96))
    put_left(L, "则 f(x) 在区间 I 上单调递增。", (118, 700), 32, True, (40, 46, 58))
    # 坐标轴 + 递增曲线
    ax, ay = 320, 940
    d.line((ax - 150, ay, ax + 330, ay), fill=(196, 204, 216), width=3)
    d.line((ax, ay + 120, ax, ay - 160), fill=(196, 204, 216), width=3)
    d.text((ax + 340, ay - 16), "x", font=F(24, False), fill=(150, 158, 172))
    d.text((ax + 12, ay - 182), "y", font=F(24, False), fill=(150, 158, 172))
    d.text((ax - 26, ay + 6), "O", font=F(24, False), fill=(150, 158, 172))
    curve = []
    for i in range(90):
        x = -120 + i * 4.6
        y = -(0.55 * x - 0.0006 * x * x)
        curve.append((ax + x, ay + y))
    d.line(curve, fill=(120, 132, 150), width=4, joint="curve")
    # 放映角标
    d.rounded_rectangle((862, 398, 986, 440), 21, fill=(240, 244, 249))
    d.ellipse((880, 412, 892, 424), fill=RED)
    put(L, "放映中", (938, 419), 22, False, (110, 120, 134))
    # 圈重点
    prog = clamp((t - 0.4) / 1.7)
    if prog > 0:
        n = 220
        pts = []
        for i in range(int(n * prog) + 1):
            th = -0.5 + (i / n) * (2 * math.pi * 1.04)
            rx, ry = 300, 50
            ang = 0.0
            x = 400 + rx * math.cos(th)
            y = 720 + ry * math.sin(th)
            pts.append((x, y))
        if len(pts) > 2:
            d.line(pts, fill=RED, width=9, joint="curve")
        if prog < 0.985:
            cursor(L, (pts[-1][0] + 6, pts[-1][1] - 2))
    cap(L, "讲 PPT 时，最烦的是切来切去", "打开白板 → 写完 → 再切回来", E((t - 0.2) / 0.45))


def s2_deprecated(L, t, dur):
    """透明覆盖层"""
    d = ImageDraw.Draw(L)
    card(L, (70, 400, 1010, 1148), 32)
    put_left(L, "第二课时 · 函数的单调性", (118, 434), 26, False, (132, 142, 156))
    put_left(L, "函数的单调性", (116, 480), 46, True, INK)
    for i, s in enumerate(["若 x1 < x2 时 f(x1) < f(x2)，", "则 f(x) 在区间 I 上单调递增。"]):
        put_left(L, s, (118, 592 + i * 50), 30, False, (74, 82, 96))
    d.line((118, 760, 962, 760), fill=(232, 236, 244), width=2)
    put_left(L, "例：比较 f(2) 与 f(3) 的大小。", (118, 800), 30, False, (110, 120, 136))

    # 覆盖层（透明蓝 + 笔迹）
    pa = E(clamp((t - 0.35) / 0.6))
    off = -34 * E(clamp((t - 1.75) / 0.5))      # 分离动画
    if pa > 0:
        lay = Image.new("RGBA", (940, 748), (0, 0, 0, 0))
        ld = ImageDraw.Draw(lay)
        ld.rounded_rectangle((0, 0, 939, 747), 32, fill=(45, 118, 240, int(34 * pa)),
                             outline=(45, 118, 240, int(215 * pa)), width=3)
        L.alpha_composite(lay, (70, int(400 + off)))
        # 笔迹
        bp = clamp((t - 0.7) / 0.55)
        if bp > 0:
            n = 120
            pts = []
            for i in range(int(n * bp) + 1):
                th = -0.6 + (i / n) * (2 * math.pi * 1.03)
                pts.append((430 + 210 * math.cos(th), 830 + 44 * math.sin(th) + off))
            if len(pts) > 2:
                d.line(pts, fill=RED, width=10, joint="curve")
        wp = clamp((t - 1.15) / 0.5)
        if wp > 0:
            pts = []
            n = 60
            for i in range(int(n * wp) + 1):
                x = 118 + i * 3.4
                pts.append((x, 692 + 12 * math.sin(i / 5.0) + off))
            if len(pts) > 2:
                d.line(pts, fill=(232, 176, 40), width=16, joint="curve")
        if t < 1.9:
            cursor(L, (790, 806 + off), 1.0 if pa > 0.6 else 0)

    # 角标
    a2 = E(clamp((t - 0.75) / 0.4))
    im = text_img("板书层 · 透明 · 独立", 24, True, (255, 255, 255))
    bw = im.width - 12 + 40
    bg_ = Image.new("RGBA", (bw, 50), (0, 0, 0, 0))
    ImageDraw.Draw(bg_).rounded_rectangle((0, 0, bw - 1, 49), 25, fill=(45, 118, 240))
    if a2 < 0.999:
        bg_.putalpha(bg_.getchannel("A").point(lambda v: int(v * a2)))
    L.alpha_composite(bg_, (70, 336))
    put(L, "板书层 · 透明 · 独立", (70 + bw // 2, 361), 24, True, (255, 255, 255), a2)

    # 分离说明
    a3 = E(clamp((t - 2.0) / 0.4))
    if a3 > 0:
        put(L, "↑ 你的板书　　↓ PPT 原文件", (540, 1180), 28, True, (110, 120, 136), a3)
    cap(L, "一层透明板，盖在任何软件上", "板书在自己的图层里，原文件一点没动",
        E((t - 0.25) / 0.45))


def s3(L, t, dur):
    """停顿成图形"""
    d = ImageDraw.Draw(L)
    card(L, (74, 330, 1006, 1180), 32)
    for gy in range(372, 1140, 36):
        for gx in range(116, 980, 36):
            d.ellipse((gx, gy, gx + 3, gy + 3), fill=(234, 238, 244))
    cx, cy, R = 520, 690, 186
    draw_t = 2.05
    p = clamp(t / draw_t)
    mp = E(clamp((t - 2.5) / 0.45))
    n = 260
    cnt = int(n * (p if p < 1 else 1))
    pts = []
    for i in range(cnt + 1):
        th = -1.5 + (i / n) * (2 * math.pi * 1.02)
        wob = 0.10 * math.sin(3 * th + 1.1) + 0.055 * math.sin(5 * th + 0.4)
        r = R * (1 + lerp(wob, 0.0, mp))
        pts.append((cx + r * math.cos(th) * (1 + 0.06 * (1 - mp)),
                    cy + r * 0.94 * math.sin(th) * (1 - 0.02 * (1 - mp))))
    if len(pts) > 2:
        d.line(pts, fill=INK, width=9, joint="curve")
    if p < 1:
        cursor(L, (pts[-1][0] + 8, pts[-1][1] - 4))
    # 选中框
    sp = E(clamp((t - 2.95) / 0.35))
    if sp > 0:
        bx0, bx1 = cx - R - 26, cx + R + 26
        by0, by1 = cy - R * 0.94 - 26, cy + R * 0.94 + 26
        col = (45, 118, 240, int(235 * sp))
        d.rectangle((bx0, by0, bx1, by1), outline=col, width=3)
        for hx in (bx0, (bx0 + bx1) / 2, bx1):
            for hy in (by0, (by0 + by1) / 2, by1):
                if abs(hx - (bx0 + bx1) / 2) < 2 and abs(hy - (by0 + by1) / 2) < 2:
                    continue
                d.rectangle((hx - 8, hy - 8, hx + 8, hy + 8), fill=(255, 255, 255),
                            outline=(45, 118, 240), width=2)
        d.line(((bx0 + bx1) / 2, by0 - 28, (bx0 + bx1) / 2, by0), fill=col, width=3)
        d.ellipse(((bx0 + bx1) / 2 - 9, by0 - 41, (bx0 + bx1) / 2 + 9, by0 - 23),
                  fill=(255, 255, 255), outline=(45, 118, 240), width=2)
    # 工具带
    tb = fit(A_BAR, 640, 148)
    L.alpha_composite(tb, (int(540 - tb.width / 2), 1026))
    cap(L, "一个歪圆，停 0.4 秒", "自己变成规整的圆，还能接着拖", E((t - 0.35) / 0.5))


def s4(L, t, dur):
    """呼出盘"""
    sc = 640
    box = (540 - sc // 2, 336, 540 + sc // 2, 336 + sc)
    sh = Image.new("RGBA", (sc + 140, sc + 140), (0, 0, 0, 0))
    ImageDraw.Draw(sh).ellipse((70, 80, 70 + sc, 80 + sc), fill=(20, 26, 40, 110))
    sh = sh.filter(ImageFilter.GaussianBlur(30))
    L.alpha_composite(sh, (box[0] - 70, box[1] - 70))
    L.alpha_composite(A_WHEEL, (box[0], box[1]))
    ring = Image.new("RGBA", (sc + 8, sc + 8), (0, 0, 0, 0))
    ImageDraw.Draw(ring).ellipse((4, 4, sc + 3, sc + 3), outline=(255, 255, 255, 24), width=3)
    L.alpha_composite(ring, (box[0] - 4, box[1] - 4))
    # 高亮扇区
    hp = E(clamp((t - 0.85) / 0.4))
    if hp > 0:
        hl = Image.new("RGBA", (sc + 20, sc + 20), (0, 0, 0, 0))
        c = sc // 2 + 10
        ImageDraw.Draw(hl).pieslice((c - 300, c - 300, c + 300, c + 300), -82, -42,
                                    fill=(255, 255, 255, int(52 * hp)))
        L.alpha_composite(hl, (box[0] - 10, box[1] - 10))
    # 光标
    p = EIO(clamp((t - 0.4) / 0.8))
    ex = 540 + 170 * math.cos(math.radians(-62))
    ey = 656 + 170 * math.sin(math.radians(-62))
    cursor(L, (lerp(540, ex, p), lerp(656, ey, p)))
    pill(L, "Ctrl + Alt + Shift + Q", (540, 1030), 30, WHITE, (54, 62, 76), 26, 66,
         E(clamp((t - 0.25) / 0.4)))
    put(L, "全局热键 · 任何软件在前台都能用", (540, 1108), 26, False, (118, 128, 143),
        E(clamp((t - 0.45) / 0.4)))
    cap(L, "工具不用找，按住一个键划一下", "八个扇区：笔 / 橡皮 / 荧光 / 激光 / 框选",
        E((t - 0.3) / 0.45))


def s5(L, t, dur):
    """数学图像"""
    d = ImageDraw.Draw(L)
    card(L, (70, 372, 1010, 1088), 32)
    inner = (100, 430, 980, 1050)
    sw = EIO(clamp((t - 2.75) / 0.55))
    zoom = 1.0 + 0.05 * EIO(clamp(t / 3.2))
    contain(L, A_FUNC, inner, alpha=1 - sw, scale=zoom)
    if sw > 0:
        contain(L, A_SOLID, inner, alpha=sw, scale=1.0 + 0.03 * sw)
    put_left(L, "函数图像 · 圆锥曲线" if sw < 0.5 else "立体几何", (100, 396), 26, True,
             (124, 134, 150))
    if sw < 0.5:
        tags = ["抛物线 · 四种开口", "双曲线 · 渐近线", "正余弦 · 正切"]
    else:
        tags = ["圆柱 / 圆锥 / 圆台 / 球", "棱柱 / 棱锥 / 棱台"]
    x = 70
    for i, s in enumerate(tags):
        a = clamp((t - 0.5 - i * 0.2) / 0.4) if sw < 0.5 else clamp(sw - i * 0.2)
        im = text_img(s, 26, True, (58, 66, 80))
        bw = im.width - 12 + 46
        a = E(a)
        pill(L, s, (x + bw // 2, 1130), 26, WHITE, (58, 66, 80), 23, 60, a)
        x += bw + 14
    main = "22 类图形，个个都能拖" if sw < 0.5 else "底面、顶点、母线都能改"
    sub = "每个定义元素都是可以直接拖的手柄" if sw < 0.5 else "拖动时实时显示角度 / 半径 / 长度"
    cap(L, main, sub, E((t - 0.3) / 0.45))


def s6(L, t, dur):
    """其他能力：三张横条卡"""
    items = [(A_PENS, "三种笔", "笔 / 荧光笔 / 激光笔，按一下就切"),
             (A_SHOT, "截图取景", "按下冻结整屏，拖框取景，原位落回"),
             (A_MORE, "课堂工具", "PDF / 图片批注 · 白板 · 计时 · 点名")]
    y0, ch, gap = 424, 232, 26
    for i, (im, title, desc) in enumerate(items):
        a = E(clamp((t - 0.12 * i) / 0.45))
        if a <= 0.01:
            continue
        yy = y0 + i * (ch + gap) + 34 * (1 - a)
        card(L, (70, yy, 1010, yy + ch), 26)
        contain(L, im, (88, yy + 16, 452, yy + ch - 16), alpha=a)
        put_left(L, title, (492, yy + 44), 36, True, INK, a)
        put_left(L, desc, (492, yy + 116), 25, False, INK2, a)
        hi = clamp((t - 1.0 - i * 0.45) / 0.3)
        if hi > 0:
            k = 1 - clamp((hi - 0.55) / 0.45) * 0.5
            ImageDraw.Draw(L).rounded_rectangle(
                (66, yy - 4, 1014, yy + ch + 4), 30,
                outline=(45, 118, 240, int(215 * k)), width=4)
    cap(L, "笔 / 荧光笔 / 激光笔，按一下就切", "截图原位落 · PDF 与图片逐页批注",
        E((t - 0.3) / 0.45))


def s7(L, t, dur):
    """基础信息"""
    rows = [("Windows 10 / 11 · 64 位", "安装包下一步到底，绿色版解压即用", None),
            ("免装 .NET · 免管理员权限", "自包含发布，不弹 UAC", None),
            ("安装包", "双击安装，只给自己装", "8.96"),
            ("绿色版 zip", "解压就能跑，拷 U 盘带走", "9.96")]
    y = 380
    for i, (a_, b_, num) in enumerate(rows):
        p = E(clamp((t - 0.1 - i * 0.15) / 0.42))
        if p <= 0.01:
            continue
        yy = y + 40 * (1 - p)
        h = 212
        card(L, (74, yy, 1006, yy + h), 26)
        d = ImageDraw.Draw(L)
        cx, cy = 158, yy + h // 2
        d.ellipse((cx - 36, cy - 36, cx + 36, cy + 36), fill=(240, 245, 255))
        if i == 0:
            d.rectangle((cx - 19, cy - 14, cx + 19, cy + 12), outline=BLUE, width=3)
            d.line((cx - 24, cy + 19, cx + 24, cy + 19), fill=BLUE, width=3)
        elif i == 1:
            d.line((cx, cy - 21, cx, cy + 5), fill=BLUE, width=4)
            d.line((cx, cy + 5, cx + 15, cy + 15), fill=BLUE, width=4)
            d.arc((cx - 20, cy - 20, cx + 20, cy + 20), 20, 310, fill=BLUE, width=3)
        else:
            d.rounded_rectangle((cx - 15, cy - 21, cx + 15, cy + 21), 4, outline=BLUE, width=3)
            d.line((cx - 15, cy - 8, cx + 15, cy - 8), fill=BLUE, width=3)
        if num is None:
            put_left(L, a_, (226, yy + 52), 36, True, INK, p)
            put_left(L, b_, (226, yy + 118), 27, False, INK2, p)
        else:
            put_left(L, a_, (226, yy + 40), 30, True, INK2, p)
            v = float(num) * E(clamp((t - 0.45 - i * 0.08) / 0.7))
            put_left(L, f"{v:.2f}", (226, yy + 96), 70, True, BLUE, p)
            put_left(L, "MB", (226 + 162, yy + 130), 30, True, (124, 134, 150), p)
            put_left(L, b_, (226 + 258, yy + 120), 24, False, (142, 152, 168), p)
        y += h + 26
    cap(L, "小到能拷进 U 盘", "不装 .NET、不要管理员权限，教室机直接跑", E((t - 0.4) / 0.45))


def s8(L, t, dur):
    """开发背景"""
    d = ImageDraw.Draw(L)
    card(L, (70, 340, 1010, 1140), 36, fill=(21, 24, 31, 255), blur=30, salpha=70)
    p = E(clamp(t / 0.32))
    put_left(L, "开发背景", (122, 388), 26, True, (128, 140, 158), p)
    put_left(L, "用 DeepSeek 深度开发", (120, 434), 46, True, (255, 255, 255), p)
    k = 0.6 + 0.4 * E(clamp((t - 0.15) / 0.75))
    put_left(L, f"{100 * k:.0f} 亿", (120, 528), 104, True, (255, 255, 255), p)
    put_left(L, "token 累计消耗", (122, 668), 30, False, (138, 150, 168), p)
    d.line((122, 730, 958, 730), fill=(48, 54, 66), width=2)
    pts = [("Direct2D 全自绘", "没有一个第三方 UI 控件"),
           ("一万笔拖动", "内容层 0 块 / 帧重画"),
           ("60+ 项自动化自检", "每个数字都有断言")]
    for i, (a_, b_) in enumerate(pts):
        q = E(clamp((t - 0.55 - i * 0.15) / 0.35))
        yy = 768 + i * 100
        d.ellipse((124, yy + 9, 136, yy + 21), fill=(45, 118, 240, int(255 * q)))
        put_left(L, a_, (156, yy - 4), 31, True, (236, 241, 249), q)
        put_left(L, b_, (156, yy + 42), 25, False, (126, 138, 156), q)
    cap(L, "一行一行抠出来的跟手", "百亿 token，换 1.0 个刷新周期的延迟", E((t - 0.3) / 0.45))


def s9(L, t, dur):
    """结尾"""
    p = E(clamp(t / 0.5))
    put(L, "InkTeach", (540, 470), 116, True, INK, p)
    put(L, "PPT 上直接写", (540, 600), 60, True, BLUE, p)
    tb = fit(A_BAR, 620, 140)
    L.alpha_composite(tb, (int(540 - tb.width / 2), 700))
    put(L, "开源免费 · GPL-3.0　｜　Windows 10 / 11　｜　免装 .NET", (540, 880), 28, False,
        INK2, E(clamp((t - 0.2) / 0.45)))
    s = "评论区回「课堂」，我发你下载"
    im = text_img(s, 38, True, WHITE)
    bw = im.width - 12 + 84
    bx, by = (W - bw) // 2, 960
    pill_im = Image.new("RGBA", (bw, 100), (0, 0, 0, 0))
    ImageDraw.Draw(pill_im).rounded_rectangle((0, 0, bw - 1, 99), 32, fill=BLUE)
    if p < 0.999:
        pill_im.putalpha(pill_im.getchannel("A").point(lambda v: int(v * p)))
    L.alpha_composite(pill_im, (bx, by))
    put(L, s, (540, by + 50), 38, True, WHITE, p)
    put(L, "抖音 / 视频号：InkTeach课堂批注", (540, 1120), 28, False, (136, 146, 162),
        E(clamp((t - 0.3) / 0.45)))
    put(L, "老师的一句话，就能少切几十次屏", (540, 1178), 26, False, (160, 168, 182),
        E(clamp((t - 0.55) / 0.45)))


def s2(L, t, dur):
    """透明覆盖层"""
    d = ImageDraw.Draw(L)
    card(L, (70, 380, 1010, 1152), 32)
    put_left(L, "第二课时 · 函数的单调性", (118, 414), 26, False, (132, 142, 156))
    put_left(L, "函数的单调性", (116, 458), 46, True, INK)
    put_left(L, "若 x1 < x2 时 f(x1) < f(x2)，", (118, 566), 30, False, (74, 82, 96))
    put_left(L, "则 f(x) 在区间 I 上单调递增。", (118, 614), 30, False, (74, 82, 96))
    d.line((118, 700, 962, 700), fill=(232, 236, 244), width=2)
    put_left(L, "例：比较 f(2) 与 f(3) 的大小", (118, 742), 30, False, (110, 120, 136))
    for i, s in enumerate(["A. f(2) > f(3)", "B. f(2) < f(3)", "C. 无法确定"]):
        put_left(L, s, (124, 812 + i * 48), 28, False, (146, 156, 170))

    # 板书层（透明覆盖）
    pa = E(clamp((t - 0.35) / 0.6))
    if pa > 0:
        lay = Image.new("RGBA", (940, 772), (0, 0, 0, 0))
        ImageDraw.Draw(lay).rounded_rectangle(
            (0, 0, 939, 771), 32, fill=(45, 118, 240, int(22 * pa)),
            outline=(45, 118, 240, int(150 * pa)), width=3)
        L.alpha_composite(lay, (70, 380))

    # 荧光笔高亮（半透明，压在字上也能读）
    hp = E(clamp((t - 0.95) / 0.55))
    if hp > 0:
        hl = Image.new("RGBA", (470, 56), (0, 0, 0, 0))
        ImageDraw.Draw(hl).rounded_rectangle((0, 0, int(470 * hp), 55), 10,
                                             fill=(246, 190, 46, 78))
        L.alpha_composite(hl, (110, 610))

    # 红圈圈例题
    prog = clamp((t - 1.35) / 0.95)
    if prog > 0:
        n = 180
        pts = []
        for i in range(int(n * prog) + 1):
            th = -0.55 + (i / n) * (2 * math.pi * 1.03)
            rx, ry, ang = 268, 46, -0.02
            x = 366 + rx * math.cos(th) * math.cos(ang) - ry * math.sin(th) * math.sin(ang)
            y = 762 + rx * math.cos(th) * math.sin(ang) + ry * math.sin(th) * math.cos(ang)
            pts.append((x, y))
        if len(pts) > 2:
            d.line(pts, fill=RED, width=9, joint="curve")
        if prog < 0.97:
            cursor(L, (pts[-1][0] + 6, pts[-1][1] - 2))

    # 角标
    a2 = E(clamp((t - 0.7) / 0.4))
    im = text_img("板书层 · 透明 · 独立", 24, True, WHITE)
    bw = im.width - 12 + 40
    bg_ = Image.new("RGBA", (bw, 50), (0, 0, 0, 0))
    ImageDraw.Draw(bg_).rounded_rectangle((0, 0, bw - 1, 49), 25, fill=(45, 118, 240))
    if a2 < 0.999:
        bg_.putalpha(bg_.getchannel("A").point(lambda v: int(v * a2)))
    L.alpha_composite(bg_, (70, 316))
    put(L, "板书层 · 透明 · 独立", (70 + bw // 2, 341), 24, True, WHITE, a2)

    # 图例
    a3 = E(clamp((t - 1.9) / 0.4))
    if a3 > 0:
        d.rounded_rectangle((110, 1176, 134, 1200), 6, outline=(45, 118, 240, int(200 * a3)),
                            width=3)
        put(L, "你的板书", (216, 1188), 26, False, (96, 106, 122), a3)
        d.rounded_rectangle((360, 1176, 384, 1200), 6, fill=(236, 239, 245))
        put(L, "PPT 原文件（没变）", (500, 1188), 26, False, (96, 106, 122), a3)
    cap(L, "一层透明板，盖在任何软件上", "板书在自己的图层里，原文件一点没动",
        E((t - 0.25) / 0.45))


SCENES = [(0.0, 3.2, s1), (3.2, 6.9, s2), (6.9, 11.9, s3), (11.9, 15.6, s4),
          (15.6, 21.3, s5), (21.3, 26.1, s6), (26.1, 30.9, s7),
          (30.9, 35.5, s8), (35.5, 39.0, s9)]
XF = 0.26


def brand(L, a):
    if a <= 0.01:
        return
    d = ImageDraw.Draw(L)
    w = 186
    logo = Image.new("RGBA", (w, 56), (0, 0, 0, 0))
    ImageDraw.Draw(logo).rounded_rectangle((0, 0, w - 1, 55), 28,
                                           fill=(255, 255, 255, int(240 * a)))
    L.alpha_composite(logo, (70, 110))
    d.ellipse((90, 126, 108, 144), fill=(45, 118, 240, int(255 * a)))
    put(L, "InkTeach", (170, 138), 27, True, (48, 56, 70), a)


def render_frame(t):
    frame = BG.copy().convert("RGBA")
    for (a0, a1, fn) in SCENES:
        if t < a0 or t > a1:
            continue
        if t < a0 + XF:
            alpha = clamp((t - a0) / XF)
        elif t > a1 - XF:
            alpha = clamp((a1 - t) / XF)
        else:
            alpha = 1.0
        if alpha <= 0.005:
            continue
        L = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        fn(L, clamp(t - a0, 0, a1 - a0), a1 - a0)
        if alpha < 0.999:
            L.putalpha(L.getchannel("A").point(lambda v: int(v * alpha)))
        frame = Image.alpha_composite(frame, L)
    lay = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    brand(lay, clamp(min(t / 0.8, (DUR - t) / 0.6)))
    frame = Image.alpha_composite(frame, lay)
    return frame.convert("RGB")


def test():
    for name, t in [("1hook", 2.4), ("2layer", 6.2), ("3shape", 10.9), ("4wheel", 14.8),
                    ("5math", 18.2), ("5solid", 20.8), ("6grid", 25.4), ("7info", 30.4),
                    ("8dev", 34.8), ("9end", 38.5)]:
        render_frame(t).save(os.path.join(OUTDIR, f"chk-{name}.png"))
    print("test ok")


def main():
    out = os.path.join(OUTDIR, "InkTeach-抖音宣传-1080x1920.mp4")
    cmd = [FFMPEG, "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
           "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-preset", "slow",
           "-b:v", "6M", "-maxrate", "10M", "-bufsize", "16M",
           "-pix_fmt", "yuv420p", "-movflags", "+faststart", out]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                         stderr=subprocess.DEVNULL)
    total = int(DUR * FPS)
    for i in range(total):
        fr = render_frame(i / FPS)
        if i == 60:
            fr.save(os.path.join(OUTDIR, "封面.png"))
        p.stdin.write(fr.tobytes())
        if i % 120 == 0:
            print(f"frame {i}/{total}", flush=True)
    p.stdin.close()
    p.wait()
    print("OK", out, round(os.path.getsize(out) / 1024 / 1024, 2), "MB")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--test":
        test()
    else:
        main()
