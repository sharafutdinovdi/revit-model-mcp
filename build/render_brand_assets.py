# /// script
# requires-python = ">=3.11"
# dependencies = ["playwright", "pillow"]
# ///
"""Render every raster brand asset from the mark and the HTML sources in docs/assets.

Run from the repository root:

    uv run build/render_brand_assets.py

The first run needs a Chromium build: ``uvx playwright install chromium``.
The HTML sources load Inter from Google Fonts, so rendering needs network access.
"""

from __future__ import annotations

import io
import struct
from pathlib import Path

from PIL import Image
from playwright.sync_api import Browser, sync_playwright

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "docs" / "assets"
INSTALL_ICONS = ROOT / "build" / "install" / "Resources" / "Icons"
ADDIN_ICONS = ROOT / "src" / "RevitModelMcp.Addin" / "Resources" / "Icons"

NAVY = "#081C38"
BLUE = "#368EF5"
CYAN = "#3CC7D4"
SIDE = "#1B5BB0"

FONT_LINK = '<link href="https://fonts.googleapis.com/css2?family=Inter:wght@700;800&amp;display=swap" rel="stylesheet">'


def mark(top: str, mid: str, bottom: str, side: str) -> str:
    """Return the three-slab mark as inline SVG markup."""
    slabs = ((34, 46, 58, bottom), (20, 32, 44, mid), (6, 18, 30, top))
    parts = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">']
    for t, m, b, face in slabs:
        parts.append(f'<polygon points="32,{t} 56,{m} 32,{b} 8,{m}" fill="{face}"/>')
        parts.append(
            f'<polygon points="8,{m} 32,{b} 32,{b + 5} 8,{m + 5}" fill="{side}"/>'
        )
        parts.append(
            f'<polygon points="32,{b} 56,{m} 56,{m + 5} 32,{b + 5}" fill="{side}"/>'
        )
    parts.append("</svg>")
    return "".join(parts)


NEUTRAL_MARK = mark(CYAN, BLUE, BLUE, SIDE)


def shoot(
    browser: Browser,
    html: str | Path,
    size: tuple[int, int],
    scale: float = 1,
    scheme: str = "light",
    transparent: bool = False,
) -> bytes:
    """Screenshot an HTML string or file at the given CSS size."""
    page = browser.new_page(
        viewport={"width": size[0], "height": size[1]},
        device_scale_factor=scale,
        color_scheme=scheme,
    )
    if isinstance(html, Path):
        page.goto(html.as_uri(), wait_until="load")
    else:
        page.set_content(html, wait_until="load")
    page.evaluate("document.fonts.ready")
    page.wait_for_timeout(300)
    data = page.screenshot(
        omit_background=transparent,
        clip={"x": 0, "y": 0, "width": size[0], "height": size[1]},
    )
    page.close()
    return data


def icon(browser: Browser, svg: str, size: int, tile: bool = False) -> bytes:
    """Render a square icon, optionally on a rounded navy tile."""
    if tile:
        pad = round(size * 0.17)
        body = (
            f'<div style="width:{size}px;height:{size}px;background:{NAVY};border-radius:{round(size * 0.22)}px;'
            f'display:flex;align-items:center;justify-content:center">'
            f'<div style="width:{size - 2 * pad}px;height:{size - 2 * pad}px">{svg}</div></div>'
        )
    else:
        body = f'<div style="width:{size}px;height:{size}px">{svg}</div>'
    html = f'<html><body style="margin:0;background:transparent"><style>svg{{width:100%;height:100%;display:block}}</style>{body}</body></html>'
    return shoot(browser, html, (size, size), transparent=True)


def write_ico(path: Path, frames: dict[int, bytes]) -> None:
    """Write a multi-size ICO with PNG-compressed frames."""
    sizes = sorted(frames)
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    body = b""
    for size in sizes:
        data = frames[size]
        dim = 0 if size >= 256 else size
        entries += struct.pack(
            "<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset + len(body)
        )
        body += data
    path.write_bytes(header + entries + body)


def quantized_png(data: bytes) -> bytes:
    """Reduce a banner render to a 256-colour PNG that stays under the size budget."""
    image = Image.open(io.BytesIO(data)).convert("RGB")
    palette = image.quantize(
        256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE
    )
    out = io.BytesIO()
    palette.save(out, format="PNG", optimize=True)
    return out.getvalue()


def installer_banner() -> str:
    return (
        f"<html><head>{FONT_LINK}</head><body style='margin:0;background:#fff'>"
        "<div style='width:493px;height:58px;position:relative'>"
        f"<div style='position:absolute;right:16px;top:9px;width:40px;height:40px'>{NEUTRAL_MARK}</div>"
        "</div><style>svg{width:100%;height:100%;display:block}</style></body></html>"
    )


def installer_dialog() -> str:
    return (
        f"<html><head>{FONT_LINK}</head><body style='margin:0;background:#fff;font-family:Inter,sans-serif'>"
        f"<div style='width:493px;height:312px;position:relative;background:#fff'>"
        f"<div style='position:absolute;left:0;top:0;width:164px;height:312px;background:{NAVY}'>"
        f"<div style='position:absolute;left:34px;top:62px;width:96px;height:96px'>{NEUTRAL_MARK}</div>"
        "<div style='position:absolute;left:0;top:178px;width:164px;text-align:center;color:#fff;"
        "font-weight:800;font-size:20px;line-height:1.2;letter-spacing:-0.01em'>Revit Model<br>MCP</div>"
        "</div></div><style>svg{width:100%;height:100%;display:block}</style></body></html>"
    )


def main() -> None:
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()

        icons_dir = ASSETS / "icons"
        icons_dir.mkdir(exist_ok=True)
        for size in (32, 64, 128, 256, 512):
            (icons_dir / f"icon-{size}.png").write_bytes(
                icon(browser, NEUTRAL_MARK, size)
            )

        (ROOT / "bundle" / "icon.png").write_bytes(
            icon(browser, NEUTRAL_MARK, 512, tile=True)
        )
        for size in (16, 32):
            (ADDIN_ICONS / f"Activity{size}.png").write_bytes(
                icon(browser, NEUTRAL_MARK, size)
            )
        write_ico(
            INSTALL_ICONS / "ShellIcon.ico",
            {
                size: icon(browser, NEUTRAL_MARK, size)
                for size in (16, 24, 32, 48, 64, 128, 256)
            },
        )
        (INSTALL_ICONS / "BannerImage.png").write_bytes(
            shoot(browser, installer_banner(), (493, 58))
        )
        (INSTALL_ICONS / "BackgroundImage.png").write_bytes(
            shoot(browser, installer_dialog(), (493, 312))
        )

        hero = ASSETS / "hero.html"
        (ASSETS / "hero-light.png").write_bytes(
            quantized_png(shoot(browser, hero, (1200, 440), 2, "light"))
        )
        (ASSETS / "hero-dark.png").write_bytes(
            quantized_png(shoot(browser, hero, (1200, 440), 2, "dark"))
        )
        (ASSETS / "social-card.png").write_bytes(
            shoot(browser, ASSETS / "social-card.html", (1280, 640))
        )

        browser.close()


if __name__ == "__main__":
    main()
