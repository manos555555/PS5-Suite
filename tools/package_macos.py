#!/usr/bin/env python3
"""Package the macOS single-file builds as PS5Suite.app inside a .zip.

Why: raw Mach-O binaries in GitHub Releases lose the POSIX exec bit when
downloaded via a browser, so Finder opens them as text. A zip preserves
permissions and a .app bundle gives a double-clickable experience.

Usage: python tools/package_macos.py <binary> <icon.png> <out.zip> <version>
"""
import io
import os
import struct
import sys
import zipfile

try:
    from PIL import Image
except ImportError:
    Image = None

INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>PS5Suite</string>
    <key>CFBundleIdentifier</key><string>com.manos.ps5suite</string>
    <key>CFBundleName</key><string>PS5 Suite</string>
    <key>CFBundleDisplayName</key><string>PS5 Suite</string>
    <key>CFBundleVersion</key><string>{ver}</string>
    <key>CFBundleShortVersionString</key><string>{ver}</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleIconFile</key><string>icon</string>
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSArchitecturePriority</key><array><string>{arch}</string></array>
</dict>
</plist>
"""


def make_icns(png_path: str) -> bytes:
    """Build an .icns from a PNG source (PNG-encoded icon data, macOS 10.7+)."""
    if Image is None:
        return b""
    img = Image.open(png_path).convert("RGBA")
    entries = b""
    for size, tag in ((128, b"ic07"), (256, b"ic08"), (512, b"ic09"), (1024, b"ic10")):
        buf = io.BytesIO()
        img.resize((size, size), Image.LANCZOS).save(buf, "PNG")
        data = buf.getvalue()
        entries += tag + struct.pack(">I", len(data) + 8) + data
    return b"icns" + struct.pack(">I", len(entries) + 8) + entries


def add_entry(zf: zipfile.ZipFile, arcname: str, data: bytes, mode: int):
    zi = zipfile.ZipInfo(arcname)
    zi.compress_type = zipfile.ZIP_DEFLATED
    zi.create_system = 3  # Unix — so external_attr carries POSIX mode
    zi.external_attr = (mode & 0xFFFF) << 16
    zf.writestr(zi, data)


def main() -> int:
    binary, icon_png, out_zip, ver = sys.argv[1:5]
    arch = "arm64" if "arm64" in out_zip.lower() else "x86_64"
    app = "PS5Suite.app/Contents"

    with open(binary, "rb") as f:
        bin_data = f.read()
    icns = make_icns(icon_png)

    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        add_entry(zf, f"{app}/", b"", 0o755)
        add_entry(zf, f"{app}/Info.plist", INFO_PLIST.format(ver=ver, arch=arch).encode(), 0o644)
        add_entry(zf, f"{app}/MacOS/", b"", 0o755)
        add_entry(zf, f"{app}/MacOS/PS5Suite", bin_data, 0o755)
        add_entry(zf, f"{app}/Resources/", b"", 0o755)
        if icns:
            add_entry(zf, f"{app}/Resources/icon.icns", icns, 0o644)

    print(f"Wrote {out_zip} ({os.path.getsize(out_zip) / 1048576:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
