#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从国际服客户端生成「多语言支持」模组的数据包。

国服客户端只下发简中文本：68 张 STR 表只有简中列，Addressables 目录里也只有
`Simplified Chinese` 一个 FairyGUI 文案包。本脚本从本机已安装的国际服客户端里取出
英 / 日 / 繁三份数据，写成模组运行期读取的紧凑格式：

    data/en.pack  data/ja.pack  data/tc.pack     STR 表正文（表名 → id → 文本）
    data/en.xml   data/ja.xml   data/tc.xml      FairyGUI 界面文案

用法（默认自动找 Steam 库里的国际服目录）：

    python mods/LanguageUnlockMod/tools/build-language-data.py
    python mods/LanguageUnlockMod/tools/build-language-data.py --int-root "D:\\Steam\\Astral Party\\8vJXnINT"

依赖 UnityPy（读取 Addressables 缓存里的 __data）。只读，不修改游戏文件。
"""
from __future__ import annotations

import argparse
import os
import struct
import sys

try:
    import UnityPy
except ImportError:  # pragma: no cover - 维护者工具
    sys.exit("需要 UnityPy：pip install UnityPy")

LANG_XML_ASSETS = {
    10: ("en", "English"),
    22: ("ja", "Japanese"),
    41: ("tc", "Traditional Chinese"),
}

# STR 表里的列号（protobuf 字段号，见 STRSettingsLocalConfigure 等）
FIELD_SIMPLIFIED = 2
FIELD_ENGLISH = 3
FIELD_JAPANESE = 4
FIELD_TRADITIONAL = 5

PACK_MAGIC = b"APL1"


# ---------------------------------------------------------------------------
# protobuf 线格式（只读，够用即可）
# ---------------------------------------------------------------------------
def _read_varint(buf: bytes, i: int):
    value = 0
    shift = 0
    while True:
        b = buf[i]
        i += 1
        value |= (b & 0x7F) << shift
        if not b & 0x80:
            return value, i
        shift += 7


def _fields(buf: bytes):
    """产出 (字段号, 线类型, 值)。线类型 0=varint 1=64bit 2=bytes 5=32bit。"""
    i = 0
    size = len(buf)
    while i < size:
        key, i = _read_varint(buf, i)
        field, wire = key >> 3, key & 7
        if wire == 0:
            value, i = _read_varint(buf, i)
            yield field, wire, value
        elif wire == 1:
            yield field, wire, buf[i:i + 8]
            i += 8
        elif wire == 2:
            length, i = _read_varint(buf, i)
            yield field, wire, buf[i:i + length]
            i += length
        elif wire == 5:
            yield field, wire, buf[i:i + 4]
            i += 4
        else:
            raise ValueError("unsupported wire type %d" % wire)


def _int32(raw: bytes) -> int:
    return struct.unpack("<i", raw)[0]


def parse_str_table(blob: bytes):
    """STR 表 → {id: {column: text}}。字段 1 是条目消息。"""
    rows = {}
    for field, wire, value in _fields(blob):
        if field != 1 or wire != 2:
            continue
        row = {}
        for f, w, v in _fields(value):
            if w != 2:
                continue
            row[f] = bytes(v).decode("utf-8", "replace")
        if not row:
            continue
        # 字段 1 是 sfixed32 id，线类型 5，会被上面的 w != 2 跳过
        entry_id = None
        for f, w, v in _fields(value):
            if f == 1 and w == 5:
                entry_id = _int32(v)
                break
        if entry_id is None:
            continue
        rows[entry_id] = row
    return rows


# ---------------------------------------------------------------------------
# 数据包
# ---------------------------------------------------------------------------
def _write_string(buf: bytearray, text: str) -> None:
    raw = text.encode("utf-8")
    buf += struct.pack("<i", len(raw))
    buf += raw


def build_pack(lang: int, tables: dict) -> bytes:
    """tables: {表名: {id: text}}，只写非空条目。"""
    buf = bytearray()
    buf += PACK_MAGIC
    buf += struct.pack("<i", lang)
    names = [name for name in sorted(tables) if tables[name]]
    buf += struct.pack("<i", len(names))
    for name in names:
        entries = tables[name]
        _write_string(buf, name)
        buf += struct.pack("<i", len(entries))
        for entry_id in sorted(entries):
            buf += struct.pack("<i", entry_id)
            _write_string(buf, entries[entry_id])
    return bytes(buf)


# ---------------------------------------------------------------------------
# 从国际服安装目录找 bundle
# ---------------------------------------------------------------------------
def _bundle_candidates(root: str):
    bundles = os.path.join(
        root, "AstralParty_INT_Data", "com.unity.addressables", "AssetBundles"
    )
    if not os.path.isdir(bundles):
        raise SystemExit("找不到 Addressables 缓存目录: " + bundles)
    for group in os.listdir(bundles):
        group_dir = os.path.join(bundles, group)
        if not os.path.isdir(group_dir):
            continue
        for entry in os.listdir(group_dir):
            data = os.path.join(group_dir, entry, "__data")
            if os.path.isfile(data):
                yield data


def collect_text_assets(root: str):
    """扫描 bundle，返回 {TextAsset 名: 文本}。按体积档位跳过无关 bundle。"""
    wanted_xml = {name for _, name in LANG_XML_ASSETS.values()}
    found = {}
    scanned = 0
    for path in _bundle_candidates(root):
        size = os.path.getsize(path)
        likely_gamedata = 1_500_000 <= size <= 3_000_000
        likely_lang = 10_000 <= size <= 80_000
        if not (likely_gamedata or likely_lang):
            continue
        scanned += 1
        try:
            env = UnityPy.load(path)
        except Exception:
            continue
        for obj in env.objects:
            if obj.type.name != "TextAsset":
                continue
            try:
                data = obj.read()
            except Exception:
                continue
            name = data.m_Name
            if not (name.startswith("STR") or name in wanted_xml):
                continue
            raw = data.m_Script
            if isinstance(raw, str):
                raw = raw.encode("utf-8", "surrogateescape")
            found[name] = raw
        if "STRSettings" in found and wanted_xml.issubset(found):
            break
    return found, scanned


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    mod_root = os.path.dirname(here)
    out_dir = os.path.join(mod_root, "data")

    parser = argparse.ArgumentParser()
    parser.add_argument("--int-root", default=None, help="国际服客户端目录（含 AstralParty_INT_Data）")
    parser.add_argument("--out", default=out_dir)
    parser.add_argument("--configs", default=None, help="改用已解包的 STR*.bin 目录（跳过 bundle 扫描）")
    parser.add_argument("--xml-dir", default=None, help="改用已解包的英/日/繁 XML 目录")
    args = parser.parse_args()

    if args.int_root is None:
        args.int_root = r"C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXnINT"

    if args.configs and args.xml_dir:
        tables_by_name = {}
        for entry in os.listdir(args.configs):
            if entry.startswith("STR") and entry.endswith(".bin"):
                with open(os.path.join(args.configs, entry), "rb") as f:
                    tables_by_name[entry[:-4]] = f.read()
        xml_by_lang = {}
        for lang, (code, asset) in LANG_XML_ASSETS.items():
            with open(os.path.join(args.xml_dir, code + ".xml"), "rb") as f:
                xml_by_lang[lang] = f.read()
        scanned = 0
    else:
        assets, scanned = collect_text_assets(args.int_root)
        tables_by_name = {n: v for n, v in assets.items() if n.startswith("STR")}
        xml_by_lang = {}
        for lang, (_, asset) in LANG_XML_ASSETS.items():
            if asset not in assets:
                raise SystemExit("国际服数据里缺少界面文案资源: " + asset)
            xml_by_lang[lang] = assets[asset]
        if "STRSettings" not in tables_by_name:
            raise SystemExit("国际服数据里缺少 STR 表（扫描了 %d 个 bundle）" % scanned)

    os.makedirs(args.out, exist_ok=True)
    total_entries = 0
    for lang, (code, _) in LANG_XML_ASSETS.items():
        column = {10: FIELD_ENGLISH, 22: FIELD_JAPANESE, 41: FIELD_TRADITIONAL}[lang]
        tables = {}
        for name, blob in tables_by_name.items():
            rows = parse_str_table(blob)
            if not rows:
                continue
            picked = {}
            for entry_id, row in rows.items():
                text = row.get(column, "")
                if not text:
                    continue
                # 与简中相同的条目不必占空间：运行期会回退到简中
                if text == row.get(FIELD_SIMPLIFIED, ""):
                    continue
                picked[entry_id] = text
            if picked:
                tables[name] = picked
                total_entries += len(picked)
        pack = build_pack(lang, tables)
        path = os.path.join(args.out, code + ".pack")
        with open(path, "wb") as f:
            f.write(pack)
        xml_path = os.path.join(args.out, code + ".xml")
        with open(xml_path, "wb") as f:
            f.write(xml_by_lang[lang].lstrip(b"\xef\xbb\xbf"))
        print("%-2s %6d 条 / %d 张表  pack=%d KB  xml=%d KB" % (
            code, sum(len(v) for v in tables.values()), len(tables),
            (len(pack) + 1023) // 1024,
            (os.path.getsize(xml_path) + 1023) // 1024,
        ))

    print("扫描 bundle: %d，输出目录: %s，条目合计 %d" % (scanned, args.out, total_entries))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
