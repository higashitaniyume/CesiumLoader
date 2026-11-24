#!/usr/bin/env python3
"""Fetch Astral Party Toys GitHub org avatar and generate circular icon + C# embedded texture data."""

import base64
import math
import os
import struct
import subprocess
import tempfile
import urllib.request
import zlib

WIDTH = 96
HEIGHT = 96
AVATAR_URLS = [
    "https://avatars.githubusercontent.com/u/358397909?v=4",
    "https://avatars.githubusercontent.com/u/358397909?v=4&s=256",
]


def download_avatar(dest_path: str) -> None:
    last_err = None
    for url in AVATAR_URLS:
        try:
            req = urllib.request.Request(
                url,
                headers={"User-Agent": "ToysBadgeMod-IconGenerator/1.0"},
            )
            with urllib.request.urlopen(req, timeout=20) as resp:
                data = resp.read()
            if data and len(data) > 100:
                with open(dest_path, "wb") as f:
                    f.write(data)
                return
        except Exception as exc:
            last_err = exc
    raise RuntimeError(f"Failed to download avatar: {last_err}")


def resize_to_rgba_with_dotnet(src_path: str, rgba_path: str, width: int, height: int) -> None:
    ps_script = r"""
param([string]$Src, [string]$Dst, [int]$W, [int]$H)
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($Src)
try {
    $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $attr = New-Object System.Drawing.Imaging.ImageAttributes
            try {
                $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
                $destRect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
                $g.DrawImage($img, $destRect, 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
            } finally {
                $attr.Dispose()
            }
        } finally {
            $g.Dispose()
        }
        $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
        $bmpData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $len = $W * $H * 4
            $bgra = New-Object byte[] $len
            [System.Runtime.InteropServices.Marshal]::Copy($bmpData.Scan0, $bgra, 0, $len)
            $rgba = New-Object byte[] $len
            for ($i = 0; $i -lt $len; $i += 4) {
                $rgba[$i] = $bgra[$i + 2]
                $rgba[$i + 1] = $bgra[$i + 1]
                $rgba[$i + 2] = $bgra[$i]
                $rgba[$i + 3] = $bgra[$i + 3]
            }
            [System.IO.File]::WriteAllBytes($Dst, $rgba)
        } finally {
            $bmp.UnlockBits($bmpData)
        }
    } finally {
        $bmp.Dispose()
    }
} finally {
    $img.Dispose()
}
"""
    with tempfile.NamedTemporaryFile("w", suffix=".ps1", delete=False, encoding="utf-8") as tf:
        tf.write(ps_script)
        ps_path = tf.name
    try:
        subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                ps_path,
                "-Src",
                src_path,
                "-Dst",
                rgba_path,
                "-W",
                str(width),
                "-H",
                str(height),
            ],
            check=True,
        )
    finally:
        if os.path.exists(ps_path):
            os.remove(ps_path)


def clamp01(val: float) -> float:
    if val <= 0.0:
        return 0.0
    if val >= 1.0:
        return 1.0
    return val


def apply_circular_mask_and_ring(raw_rgba: bytes, width: int, height: int) -> bytearray:
    out = bytearray(raw_rgba)
    cx = (width - 1) * 0.5
    cy = (height - 1) * 0.5
    r_outer = 46.5
    feather = 1.5
    ring_thickness = 1.5
    r_ring_inner = r_outer - ring_thickness

    # Subtle bright rim so the circular icon stays legible over both dark and light HUD layers
    ring_r, ring_g, ring_b, ring_alpha = 248.0, 250.0, 255.0, 0.88

    for y in range(height):
        for x in range(width):
            idx = (y * width + x) * 4
            dx = x - cx
            dy = y - cy
            d = math.hypot(dx, dy)

            # Outer circle AA coverage
            outer_cov = clamp01((r_outer + feather * 0.5 - d) / feather)
            if outer_cov <= 0.0:
                out[idx] = 0
                out[idx + 1] = 0
                out[idx + 2] = 0
                out[idx + 3] = 0
                continue

            src_r = float(out[idx])
            src_g = float(out[idx + 1])
            src_b = float(out[idx + 2])
            src_a = float(out[idx + 3]) / 255.0

            # Ring coverage between r_ring_inner and r_outer
            ring_inner_cov = clamp01((d - (r_ring_inner - 0.5)) / 1.0)
            ring_blend = ring_inner_cov * ring_alpha

            final_r = src_r * (1.0 - ring_blend) + ring_r * ring_blend
            final_g = src_g * (1.0 - ring_blend) + ring_g * ring_blend
            final_b = src_b * (1.0 - ring_blend) + ring_b * ring_blend
            final_a = max(src_a, ring_blend) * outer_cov

            out[idx] = int(round(min(255.0, max(0.0, final_r))))
            out[idx + 1] = int(round(min(255.0, max(0.0, final_g))))
            out[idx + 2] = int(round(min(255.0, max(0.0, final_b))))
            out[idx + 3] = int(round(min(255.0, max(0.0, final_a * 255.0))))
    return out


def encode_png_rgba(rgba: bytes, width: int, height: int) -> bytes:
    def chunk(tag: bytes, payload: bytes) -> bytes:
        crc = zlib.crc32(tag + payload) & 0xFFFFFFFF
        return struct.pack(">I", len(payload)) + tag + payload + struct.pack(">I", crc)

    raw_rows = bytearray()
    stride = width * 4
    for y in range(height):
        raw_rows.append(0)  # Filter type 0 (None)
        raw_rows.extend(rgba[y * stride : (y + 1) * stride])

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    idat = zlib.compress(bytes(raw_rows), 9)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat) + chunk(b"IEND", b"")


def flip_vertical_rgba(rgba: bytes, width: int, height: int) -> bytes:
    stride = width * 4
    flipped = bytearray(len(rgba))
    for y in range(height):
        src_start = (height - 1 - y) * stride
        dst_start = y * stride
        flipped[dst_start : dst_start + stride] = rgba[src_start : src_start + stride]
    return bytes(flipped)


def write_csharp_asset(cs_path: str, width: int, height: int, bottom_up_rgba: bytes) -> None:
    compressor = zlib.compressobj(9, zlib.DEFLATED, -15)
    deflated = compressor.compress(bottom_up_rgba) + compressor.flush()
    b64 = base64.b64encode(deflated).decode("ascii")
    lines = [b64[i : i + 100] for i in range(0, len(b64), 100)]
    joined_lines = "\n            + ".join('"' + line + '"' for line in lines)

    cs_content = """using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Runtime;
using UnityEngine;

namespace ToysBadgeMod
{
    internal static class ToysIconData
    {
        public const int Width = """ + str(width) + """;
        public const int Height = """ + str(height) + """;

        private const string DeflatedRgbaBase64 =
            """ + joined_lines + """;

        public static Texture2D CreateDefaultTexture()
        {
            byte[] compressed = Convert.FromBase64String(DeflatedRgbaBase64);
            byte[] rgba = new byte[Width * Height * 4];
            using (MemoryStream ms = new MemoryStream(compressed))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < rgba.Length)
                {
                    int read = ds.Read(rgba, offset, rgba.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
            }

            Texture2D tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.name = "ToysBadgeMod.DefaultIcon";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.LoadRawTextureData(rgba);
            tex.Apply(false, true);
            return tex;
        }

        public static Texture2D LoadCustomTexture(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return CreateDefaultTexture();
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes != null && bytes.Length > 0)
                {
                    Type imgConv = RuntimeAssemblyService.FindTypeBySimpleName("ImageConversion");
                    if (imgConv != null)
                    {
                        MethodInfo load3 = imgConv.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                        MethodInfo load2 = imgConv.GetMethod("LoadImage", new Type[] { typeof(Texture2D), typeof(byte[]) });
                        MethodInfo load = load3 != null ? load3 : load2;
                        if (load != null)
                        {
                            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            tex.name = "ToysBadgeMod.CustomIcon";
                            tex.wrapMode = TextureWrapMode.Clamp;
                            tex.filterMode = FilterMode.Bilinear;
                            object okObj = load3 != null
                                ? load3.Invoke(null, new object[] { tex, bytes, true })
                                : load2.Invoke(null, new object[] { tex, bytes });
                            if (okObj is bool && (bool)okObj && tex.width > 0 && tex.height > 0)
                            {
                                return tex;
                            }
                            UnityEngine.Object.Destroy(tex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SdkLog.Warn("ToysBadgeMod", "加载自定义图标失败，将回退到默认图标: " + ex.Message);
            }

            return CreateDefaultTexture();
        }
    }
}
"""
    os.makedirs(os.path.dirname(cs_path), exist_ok=True)
    with open(cs_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(cs_content)


def main() -> None:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    mod_root = os.path.dirname(script_dir)
    assets_dir = os.path.join(mod_root, "assets")
    src_dir = mod_root
    os.makedirs(assets_dir, exist_ok=True)
    os.makedirs(src_dir, exist_ok=True)

    png_path = os.path.join(assets_dir, "toys_icon.png")
    cs_path = os.path.join(mod_root, "ToysIconData.cs")

    with tempfile.TemporaryDirectory() as tmp:
        raw_avatar = os.path.join(tmp, "avatar_raw.img")
        raw_rgba_file = os.path.join(tmp, "avatar_96.rgba")

        download_avatar(raw_avatar)
        resize_to_rgba_with_dotnet(raw_avatar, raw_rgba_file, WIDTH, HEIGHT)
        with open(raw_rgba_file, "rb") as f:
            raw_rgba = f.read()

    if len(raw_rgba) != WIDTH * HEIGHT * 4:
        raise RuntimeError(f"Unexpected RGBA length: {len(raw_rgba)}")

    masked_rgba = apply_circular_mask_and_ring(raw_rgba, WIDTH, HEIGHT)
    png_bytes = encode_png_rgba(masked_rgba, WIDTH, HEIGHT)
    with open(png_path, "wb") as f:
        f.write(png_bytes)

    bottom_up_rgba = flip_vertical_rgba(masked_rgba, WIDTH, HEIGHT)
    write_csharp_asset(cs_path, WIDTH, HEIGHT, bottom_up_rgba)
    print(f"Generated {png_path} ({len(png_bytes)} bytes) and {cs_path}")


if __name__ == "__main__":
    main()
