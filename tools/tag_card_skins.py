"""
tag_card_skins.py
为皮肤目录中的卡面图片右上角绘制卡牌编号水印标签，便于在游戏内直观识别自定义卡面是否成功加载。
支持直接处理 Steam default 皮肤目录下新增的图片，并同步至本地工程 dist 目录。
"""

import os
import re
import sys
from datetime import datetime
from PIL import Image, ImageDraw, ImageFont

STEAM_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXn6CN\AstralParty_ModLoader\skins\default"
DIST_DIR = r"c:\src\Study\astralparty\modding\msvc\dist\modloader\AstralParty_ModLoader\skins\default"

PREFIXES = [
    'UT_HandCard_',
    'UT_Destiny_',
    'UT_MapEvent_',
    'UT_Event_',
    'UT_Item_AltArt_',
    'UT_IAltArt_',
    'Card_',
    'card_',
]

def get_label(filename: str) -> str:
    stem = os.path.splitext(filename)[0]
    is_sfw = stem.lower().endswith('_sfw')
    clean = re.sub(r'_sfw$', '', stem, flags=re.IGNORECASE)
    for p in PREFIXES:
        if clean.startswith(p):
            clean = clean[len(p):]
            break
    if is_sfw:
        return f"#{clean}-sfw"
    return f"#{clean}"

def get_font_path() -> str:
    candidates = [
        r"C:\Windows\Fonts\arialbd.ttf",
        r"C:\Windows\Fonts\msyhbd.ttc",
        r"C:\Windows\Fonts\arial.ttf",
        r"C:\Windows\Fonts\msyh.ttc",
    ]
    for p in candidates:
        if os.path.exists(p):
            return p
    return "arial.ttf"

def stamp_image(im: Image.Image, text: str, font_path: str) -> Image.Image:
    im = im.convert('RGBA')
    scale = im.height / 400.0
    font_size = max(13, int(30 * scale))
    try:
        font = ImageFont.truetype(font_path, font_size)
    except Exception:
        font = ImageFont.load_default()

    overlay = Image.new('RGBA', im.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)

    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]

    pad_x = max(4, int(10 * scale))
    pad_y = max(2, int(5 * scale))
    margin = max(4, int(10 * scale))
    radius = max(3, int(8 * scale))
    border_w = max(1, int(2 * scale))

    badge_w = tw + pad_x * 2
    badge_h = th + pad_y * 2

    x2 = im.width - margin
    x1 = x2 - badge_w
    y1 = margin
    y2 = y1 + badge_h

    # 绘制深红高对比度圆角角标背景与白色描边
    draw.rounded_rectangle(
        [x1, y1, x2, y2],
        radius=radius,
        fill=(220, 20, 60, 235),
        outline=(255, 255, 255, 245),
        width=border_w
    )

    # 居中绘制白色文本
    text_x = x1 + (badge_w - tw) // 2 - bbox[0]
    text_y = y1 + (badge_h - th) // 2 - bbox[1]
    draw.text((text_x, text_y), text, font=font, fill=(255, 255, 255, 255))

    return Image.alpha_composite(im, overlay)

def main():
    if not os.path.exists(STEAM_DIR):
        print(f"Error: Directory not found: {STEAM_DIR}")
        sys.exit(1)

    force_all = "--all" in sys.argv
    font_path = get_font_path()
    print(f"Using font: {font_path}")

    all_pngs = [f for f in sorted(os.listdir(STEAM_DIR)) if f.lower().endswith(('.png', '.jpg', '.jpeg'))]
    print(f"Total images found in skins/default: {len(all_pngs)}")

    # 筛选未打标的新增文件 (如果不是强制全部，则处理修改时间早于今天批处理时间的未打标文件)
    cutoff_time = datetime(2026, 10, 10, 11, 26, 0)
    files_to_process = []
    for f in all_pngs:
        full_path = os.path.join(STEAM_DIR, f)
        mtime = datetime.fromtimestamp(os.path.getmtime(full_path))
        if force_all or mtime < cutoff_time:
            files_to_process.append(f)

    print(f"Files to process (newly added or unbadged): {len(files_to_process)}")
    if not files_to_process:
        print("No new unbadged images found. All images are already up to date!")
        return

    os.makedirs(DIST_DIR, exist_ok=True)

    processed = 0
    for fname in files_to_process:
        file_path = os.path.join(STEAM_DIR, fname)
        dist_path = os.path.join(DIST_DIR, fname)
        label = get_label(fname)

        with Image.open(file_path) as im:
            tagged = stamp_image(im, label, font_path)

        tagged.save(file_path, "PNG")
        tagged.save(dist_path, "PNG")

        processed += 1
        print(f"[{processed}/{len(files_to_process)}] Badged: {fname} -> {label}")

    print(f"\nSuccessfully badged and synced all {processed} new card images to Steam & dist directories!")

if __name__ == "__main__":
    main()
