# 生成「收藏图标候选」对照页（开发期选型用，不进产品）
# 图标全部来自 Fluent UI System Icons（MIT），和现有图标同一上游。
import html
import re
import tempfile
import urllib.parse
import urllib.request
from pathlib import Path

BASE = "https://cdn.jsdelivr.net/gh/microsoft/fluentui-system-icons@main/assets"
OUT = Path(__file__).resolve().parent.parent / "design" / "favorite-icons.html"
CACHE = Path(tempfile.gettempdir()) / "inkteach-favorite-icons"

CANDIDATES = [
    # key, folder, slug, 中文名, 一句话判断, 推荐级别
    ("library", "Library", "library", "现在的：书架 Library",
     "像图书馆/资料库，不像“把东西收进来”。", "current"),
    ("bookmark", "Bookmark", "bookmark", "A. 书签 Bookmark",
     "微信收藏/浏览器书签的隐喻，一看就是“存起来”。", "pick"),
    ("bookmarkAdd", "Bookmark Add", "bookmark_add", "B. 书签＋ Bookmark Add",
     "图标自带加号，“收进去”最直白；24px 下略复杂。", "pick"),
    ("starAdd", "Star Add", "star_add", "C. 星标 Star（主条）／ Star Add（选中栏）",
     "主条用不带加号的星标；选中栏的收藏动作用带加号的 Star Add；已收藏变实心星＋对勾。", "selected"),
    ("folderAdd", "Folder Add", "folder_add", "D. 文件夹＋ Folder Add",
     "像“放进文件夹”；更像整理资料，不像收藏动作。", "short"),
    ("collections", "Collections", "collections", "E. 收藏集 Collections",
     "像一叠收藏卡；语义偏“集合”，动作感弱。", "short"),
    ("archive", "Archive", "archive", "F. 收纳盒 Archive",
     "像归档/压缩，和“收藏”有一点距离。", "short"),
    ("save", "Save", "save", "G. 软盘 Save",
     "像保存文件，容易和“导出/保存板书”混。", "no"),
    ("mailInboxAdd", "Mail Inbox Add", "mail_inbox_add", "H. 收件箱＋ Mail Inbox Add",
     "“收”的动作明确；但更像收邮件。", "no"),
    ("bookmarkMultiple", "Bookmark Multiple", "bookmark_multiple", "I. 多书签 Bookmark Multiple",
     "像收藏夹；24px 下几条书签容易糊成一团。", "no"),
]


def fetch(folder: str, slug: str, variant: str) -> str:
    CACHE.mkdir(parents=True, exist_ok=True)
    cache = CACHE / f"{slug}_{variant}.svg"
    if cache.exists():
        return cache.read_text(encoding="utf-8")
    url = f"{BASE}/{urllib.parse.quote(folder)}/SVG/ic_fluent_{slug}_24_{variant}.svg"
    req = urllib.request.Request(url, headers={"User-Agent": "InkTeach-icon-sheet"})
    with urllib.request.urlopen(req, timeout=12) as r:
        text = r.read().decode("utf-8")
    cache.write_text(text, encoding="utf-8")
    return text


def clean_svg(svg: str, size: int) -> str:
    # 只留 viewBox + path；颜色交给 currentColor（和 IconAtlas 的用法一致）
    svg = re.sub(r"<svg[^>]*>", f'<svg viewBox="0 0 24 24" width="{size}" height="{size}" fill="currentColor" xmlns="http://www.w3.org/2000/svg">', svg, count=1)
    svg = re.sub(r'fill="(?!none)[^"]*"', 'fill="currentColor"', svg)
    svg = svg.replace('fill="none"', 'fill="none"')
    return svg


BAR_ICONS = [
    ("Dismiss", "dismiss"),
    ("Lock Open", "lock_open"),
    ("Layer", "layer"),
    ("Save", "save"),
    ("Copy", "copy"),
    ("Flip Horizontal", "flip_horizontal"),
    ("Flip Vertical", "flip_vertical"),
    ("Delete", "delete"),
]


def bar_icon(folder: str, slug: str) -> str:
    return clean_svg(fetch(folder, slug, "regular"), 24)


bar_a = {
    "collapse": bar_icon(*BAR_ICONS[0]),
    "lock": bar_icon(*BAR_ICONS[1]),
    "layer": bar_icon(*BAR_ICONS[2]),
    "export": bar_icon(*BAR_ICONS[3]),
    "copy": bar_icon(*BAR_ICONS[4]),
    "flipH": bar_icon(*BAR_ICONS[5]),
    "flipV": bar_icon(*BAR_ICONS[6]),
    "delete": bar_icon(*BAR_ICONS[7]),
}
bookmark_reg = clean_svg(fetch("Bookmark", "bookmark", "regular"), 24)
bookmark_add = clean_svg(fetch("Bookmark Add", "bookmark_add", "regular"), 24)
star_add = clean_svg(fetch("Star Add", "star_add", "regular"), 24)


def bar_html(fav: str, fav_is_add: bool) -> str:
    color = '<span class="color-ring"></span>'
    badge = "" if fav_is_add else '<i class="badge plus bar-badge">+</i>'
    cells = [
        bar_a["collapse"], color, bar_a["lock"], bar_a["layer"], bar_a["export"],
        f'<span class="fav-wrap">{fav}{badge}</span>',
        bar_a["copy"], bar_a["flipH"], bar_a["flipV"], bar_a["delete"],
    ]
    return '<div class="bar-card">' + ''.join(f'<div class="bar-cell">{c}</div>' for c in cells) + '</div>'


star_reg = clean_svg(fetch("Star", "star", "regular"), 24)
star_fil = clean_svg(fetch("Star", "star", "filled"), 24)
star_add_btn = clean_svg(fetch("Star Add", "star_add", "regular"), 24)
star_add_big = clean_svg(fetch("Star Add", "star_add", "regular"), 48)
bookmark_filled = clean_svg(fetch("Bookmark", "bookmark", "filled"), 24)
bookmark_add_btn = clean_svg(fetch("Bookmark Add", "bookmark_add", "regular"), 24)
bookmark_add_big = clean_svg(fetch("Bookmark Add", "bookmark_add", "regular"), 48)

rows = []
for key, folder, slug, label, note, level in CANDIDATES:
    reg = clean_svg(fetch(folder, slug, "regular"), 24)
    fil = clean_svg(fetch(folder, slug, "filled"), 24)
    big = clean_svg(fetch(folder, slug, "regular"), 48)
    action_svg, action_extra = reg, '<i class="badge plus">+</i>'
    if key == "starAdd":
        reg, fil, big = star_reg, star_fil, star_add_big
        action_svg, action_extra = star_add_btn, ""
    elif key == "bookmarkAdd":
        reg, fil, big = bookmark_reg, bookmark_filled, bookmark_add_big
        action_svg, action_extra = bookmark_add_btn, ""
    badge = level if level in ("pick", "selected") else ""
    tag = ""
    if level == "selected":
        tag = '<span class="tag selected">你选的</span>'
    elif level == "pick":
        tag = '<span class="tag">首推</span>'
    elif level == "current":
        tag = '<span class="tag current">现在</span>'
    rows.append(f"""
    <section class="row {badge}">
      <div class="meta">
        <div class="title">{html.escape(label)} {tag}</div>
        <div class="note">{html.escape(note)}</div>
      </div>
      <div class="states">
        <div class="state"><div class="cell idle">{reg}</div><span>未选中</span></div>
        <div class="state"><div class="cell idle">{action_svg}{action_extra}</div><span>收藏动作</span></div>
        <div class="state"><div class="cell active">{fil}</div><span>打开/激活</span></div>
        <div class="state"><div class="cell idle badge-check">{fil}<i class="badge check">✓</i></div><span>已收藏</span></div>
        <div class="state detail"><div class="cell big">{big}</div><span>48px 细节</span></div>
      </div>
    </section>
    """)

doc = f"""<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>收藏图标候选 · InkTeach</title>
<style>
  :root {{
    --ink: #1b1c20; --muted: #6b7280; --line: #e5e7eb; --accent: #0078d4;
    --cell: #f3f4f6; --hover: #e8f0fe; --paper: #ffffff;
  }}
  * {{ box-sizing: border-box; }}
  body {{ margin: 0; padding: 28px; background: #eef1f5; color: var(--ink);
         font-family: "Microsoft YaHei UI", "Segoe UI", sans-serif; }}
  .wrap {{ max-width: 1180px; margin: 0 auto; background: var(--paper);
           border: 1px solid var(--line); border-radius: 16px; padding: 26px 28px 30px;
           box-shadow: 0 18px 50px rgba(15,23,42,.10); }}
  h1 {{ margin: 0 0 6px; font-size: 22px; letter-spacing: .02em; }}
  .sub {{ color: var(--muted); font-size: 13px; line-height: 1.7; }}
  .sub b {{ color: var(--ink); }}
  .callout {{ margin: 16px 0 20px; padding: 12px 14px; border-radius: 10px;
              background: #f0f7ff; border: 1px solid #cfe4fb; color: #164e7a;
              font-size: 13px; line-height: 1.7; }}
  .row {{ display: grid; grid-template-columns: 300px 1fr; gap: 16px; align-items: center;
          padding: 14px 12px; border-top: 1px solid var(--line); }}
  .row:first-of-type {{ border-top: 0; }}
  .row.pick {{ background: linear-gradient(90deg, #f7fbff, #ffffff 55%); }}
  .row.selected {{ background: linear-gradient(90deg, #f0fdf4, #ffffff 55%); }}
  .row.current {{ opacity: .72; }}
  .title {{ font-size: 14px; font-weight: 700; margin-bottom: 5px; }}
  .note {{ font-size: 12px; color: var(--muted); line-height: 1.6; }}
  .tag {{ display: inline-block; margin-left: 6px; padding: 1px 6px; border-radius: 999px;
          background: var(--accent); color: #fff; font-size: 10px; font-weight: 600;
          vertical-align: 2px; }}
  .tag.current {{ background: #9ca3af; }}
  .tag.selected {{ background: #16a34a; }}
  .states {{ display: flex; gap: 18px; align-items: flex-start; flex-wrap: wrap; }}
  .state {{ display: flex; flex-direction: column; align-items: center; gap: 5px; }}
  .state > span {{ font-size: 10.5px; color: var(--muted); white-space: nowrap; }}
  .cell {{ width: 40px; height: 40px; border-radius: 9px; background: var(--cell);
           display: flex; align-items: center; justify-content: center; color: var(--ink);
           position: relative; }}
  .cell.active {{ background: var(--accent); color: #fff; }}
  .cell.big {{ width: 64px; height: 64px; border-radius: 12px; }}
  .cell svg {{ display: block; }}
  .badge {{ position: absolute; right: 3px; bottom: 3px; width: 12px; height: 12px;
            border-radius: 50%; background: var(--accent); color: #fff; font-style: normal;
            font-size: 10px; line-height: 12px; text-align: center; font-weight: 700;
            box-shadow: 0 0 0 1.5px var(--paper); }}
  .badge.check {{ background: #16a34a; }}
  .foot {{ margin-top: 18px; color: var(--muted); font-size: 12px; line-height: 1.8; }}
  .foot code {{ background: #f3f4f6; padding: 1px 5px; border-radius: 4px; }}
  .bar-demo {{ margin: 4px 0 8px; }}
  .bar-label {{ font-size: 12px; color: var(--muted); margin: 10px 0 6px; }}
  .bar-label b {{ color: var(--ink); }}
  .bar-card {{ display: inline-flex; align-items: center; padding: 8px; gap: 0;
               background: #fff; border: 1px solid #e5e7eb; border-radius: 10px;
               box-shadow: 0 8px 20px rgba(15,23,42,.08); }}
  .bar-cell {{ width: 40px; height: 40px; display: flex; align-items: center; justify-content: center;
               color: var(--ink); position: relative; }}
  .fav-wrap {{ position: relative; display: inline-flex; }}
  .bar-badge {{ right: -1px; bottom: -1px; width: 11px; height: 11px; font-size: 9px; line-height: 11px;
                box-shadow: 0 0 0 1.5px #fff; }}
  .color-ring {{ width: 18px; height: 18px; border-radius: 50%; border: 3px solid #e11d48;
                 box-shadow: inset 0 0 0 2px #fff; }}
</style></head>
<body><div class="wrap">
  <h1>收藏图标候选</h1>
  <div class="sub">全部来自 <b>Fluent UI System Icons（MIT）</b>，和现在工具条/操作条同一上游、同一 24 网格；
  最终会走 <code>gen-ui-icons.ps1 → Icons.g.cs → IconAtlas</code> 这条现有流程。角标只是示意，最终可跟主题走。</div>
  <div class="callout"><b>已定：C 星标。</b>入口/浏览用 <b>Star</b>，操作条“收进来”用 <b>Star Add</b>，
  已收藏用<b>实心 Star ＋ 对勾</b>。这和抖音收藏是同一套记忆：星标＝收藏，加号＝收进来。</div>
  <div class="bar-demo">
    <div class="bar-label"><b>放进操作条的样子（实际 40px 格 / 24px 图标）</b>——看它和撤销/删除/导出会不会撞脸</div>
    <div class="bar-label">C 选中栏的收藏动作：Star Add（带加号；主条入口用不带加号的 Star）：</div>
    {bar_html(star_add, True)}
    <div class="bar-label">A 书签＋右下角小角标（备选）：</div>
    {bar_html(bookmark_reg, False)}
    <div class="bar-label">B 书签＋自带加号（备选）：</div>
    {bar_html(bookmark_add, True)}
  </div>
  {''.join(rows)}
  <div class="foot">看的时候重点看：<b>24px 那三格</b>在真实大小下能不能一眼认出来；
  <b>48px</b> 那格看线条和缺口是否干净；以及和旁边的撤销/删除/导出会不会“撞脸”。</div>
</div></body></html>"""

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(doc, encoding="utf-8")
print(OUT)
