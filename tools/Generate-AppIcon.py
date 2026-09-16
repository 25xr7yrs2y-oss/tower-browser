#!/usr/bin/env python3
"""Generate deterministic Windows icon assets from the approved source image."""

from __future__ import annotations

import argparse
import io
from pathlib import Path

from PIL import Image, ImageCms, ImageFilter, ImageOps


PNG_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256, 512)
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    args.output.mkdir(parents=True, exist_ok=True)
    png_dir = args.output / "Icons"
    png_dir.mkdir(parents=True, exist_ok=True)

    with Image.open(args.source) as source:
        source.load()
        oriented = ImageOps.exif_transpose(source)
        if oriented.width != oriented.height:
            raise ValueError(
                f"Icon source must be square; received {oriented.width}x{oriented.height}."
            )

        rgb = oriented.convert("RGB")
        embedded_profile = source.info.get("icc_profile")
        if embedded_profile:
            rgb = ImageCms.profileToProfile(
                rgb,
                ImageCms.getOpenProfile(io.BytesIO(embedded_profile)),
                ImageCms.createProfile("sRGB"),
                outputMode="RGB",
            )

        # Saving decoded pixels as PNG intentionally strips EXIF and other
        # source metadata. Pixel values have already been normalized to sRGB.
        master = rgb
        master.save(args.output / "OfficialIconSource.png", format="PNG", optimize=True)
        master.save(args.output / "IconMaster.png", format="PNG", optimize=True)

        for size in PNG_SIZES:
            icon = master.resize((size, size), Image.Resampling.LANCZOS)
            if size <= 48:
                icon = icon.filter(ImageFilter.UnsharpMask(radius=0.55, percent=115, threshold=2))
            icon.save(png_dir / f"app-icon-{size}.png", format="PNG", optimize=True)

        master.save(
            args.output / "AppIcon.ico",
            format="ICO",
            sizes=[(size, size) for size in ICO_SIZES],
            # WPF's Windows Imaging Component decoder rejects the PNG-compressed
            # ICO produced by Pillow on supported Windows builds. DIB frames are
            # larger but decode reliably in both WPF and the Windows shell.
            bitmap_format="bmp",
        )


if __name__ == "__main__":
    main()
