# /// script
# requires-python = ">=3.11"
# dependencies = ["playwright", "pillow"]
# ///
"""Render every raster brand asset from the mark and the HTML sources in docs/assets.

Run from the repository root:

    uv run build/render_brand_assets.py [--readme-media]

The first run needs a Chromium build: ``uvx playwright install chromium``.
``--readme-media`` renders only the README hero and gallery animations. They need ffmpeg
with libwebp and the raw Revit takes in work/ws/visuals-spike/film/takes (not in git).
The HTML sources load Inter from Google Fonts, so rendering needs network access.
"""

from __future__ import annotations

import io
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw
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

FPS = 15
TAKES = ROOT / "work" / "ws" / "visuals-spike" / "film" / "takes"

# Source windows come from work/ws/visuals-spike/film/slots.json. Crops are (width, height, x, y) on
# the 1884 x 874 recordings; "rate" retimes the take and "dur" is the output length in seconds.
WIDE = (1554, 874, 163, 0)
HERO_TAKE = {
    "file": "A1.mp4",
    "trim": 1.4,
    "rate": 3.0,
    "dur": 6.4,
    "crop": (1216, 874, 334, 0),
}
GALLERY = (
    {
        "name": "build",
        "file": "A6-take2.mp4",
        "trim": 0.3,
        "rate": 3.0,
        "dur": 5.4,
        "crop": WIDE,
    },
    {
        "name": "assemble",
        "file": "A3-alt.mp4",
        "trim": 0.7,
        "rate": 3.0,
        "dur": 6.0,
        "crop": WIDE,
    },
    {
        "name": "air",
        "file": "F1-take2.mp4",
        "trim": 0.6,
        "rate": 1.75,
        "dur": 6.0,
        "crop": (1400, 788, 277, 0),
    },
    {
        "name": "warnings",
        "file": "W1.mp4",
        "trim": 0.4,
        "rate": 2.5,
        "dur": 5.8,
        "crop": WIDE,
    },
)


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
    query: str = "",
) -> bytes:
    """Screenshot an HTML string or file at the given CSS size."""
    page = browser.new_page(
        viewport={"width": size[0], "height": size[1]},
        device_scale_factor=scale,
        color_scheme=scheme,
    )
    url = html.as_uri() + query if isinstance(html, Path) else None
    if url:
        page.goto(url, wait_until="load")
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


def photo_png(data: bytes) -> bytes:
    """Reduce a render with screenshots in it to a dithered 256-colour PNG."""
    image = Image.open(io.BytesIO(data)).convert("RGB")
    palette = image.quantize(
        256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.FLOYDSTEINBERG
    )
    out = io.BytesIO()
    palette.save(out, format="PNG", optimize=True)
    return out.getvalue()


def rounded_mask(size: tuple[int, int], radius: int) -> Image.Image:
    """Return an anti-aliased rounded-rectangle alpha mask."""
    scale = 4
    big = Image.new("L", (size[0] * scale, size[1] * scale), 0)
    ImageDraw.Draw(big).rounded_rectangle(
        (0, 0, big.width - 1, big.height - 1), radius * scale, fill=255
    )
    return big.resize(size, Image.Resampling.LANCZOS)


def outline(size: tuple[int, int], radius: int) -> Image.Image:
    """Return a transparent image with a faint rounded outline for light tiles."""
    scale = 4
    big = Image.new("RGBA", (size[0] * scale, size[1] * scale), (0, 0, 0, 0))
    ImageDraw.Draw(big).rounded_rectangle(
        (0, 0, big.width - 1, big.height - 1),
        radius * scale,
        outline=(8, 28, 56, 56),
        width=2 * scale,
    )
    return big.resize(size, Image.Resampling.LANCZOS)


def ffmpeg(*args: str | Path) -> None:
    subprocess.run(["ffmpeg", "-y", "-v", "error", *map(str, args)], check=True)


def clip_filter(take: dict, size: tuple[int, int]) -> str:
    """Trim, retime, crop, scale and fade a take through white so a loop has no visible cut."""
    crop_w, crop_h, crop_x, crop_y = take["crop"]
    fade = 0.3
    return (
        f"setpts=(PTS-STARTPTS)/{take['rate']},fps={FPS},"
        f"crop={crop_w}:{crop_h}:{crop_x}:{crop_y},"
        f"scale={size[0]}:{size[1]}:flags=lanczos,"
        f"fade=t=in:st=0:d={fade}:color=white,"
        f"fade=t=out:st={take['dur'] - fade}:d={fade}:color=white,format=rgba"
    )


def source_args(take: dict) -> list[str | Path]:
    return [
        "-ss",
        take["trim"],
        "-t",
        take["dur"] * take["rate"],
        "-i",
        TAKES / take["file"],
    ]


def encode_webp(frames: Path, out: Path, quality: int, limit_mb: float) -> None:
    """Assemble PNG frames into a looping animated WebP and enforce the size budget."""
    images = [Image.open(f).convert("RGBA") for f in sorted(frames.glob("*.png"))]
    images[0].save(
        out,
        save_all=True,
        append_images=images[1:],
        duration=round(1000 / FPS),
        loop=0,
        quality=quality,
        method=6,
    )
    size = out.stat().st_size / 1e6
    if size > limit_mb:
        raise SystemExit(f"{out.name} is {size:.1f} MB, over the {limit_mb} MB budget")
    print(f"{out.relative_to(ROOT)}: {size:.2f} MB, {len(images)} frames")


def render_hero(browser: Browser, work: Path) -> None:
    """Composite the build-up recording into the hero window, in both colour schemes."""
    take = HERO_TAKE
    inner = (498, 358)
    mask = work / "hero-mask.png"
    rounded_mask(inner, 13).save(mask)
    poster_time = take["trim"] + (take["dur"] - 0.7) * take["rate"]
    poster = work / "hero-poster.png"
    ffmpeg(
        "-ss",
        poster_time,
        "-i",
        TAKES / take["file"],
        "-frames:v",
        1,
        "-vf",
        f"crop={take['crop'][0]}:{take['crop'][1]}:{take['crop'][2]}:{take['crop'][3]},scale={inner[0]}:{inner[1]}:flags=lanczos",
        poster,
    )
    for scheme in ("light", "dark"):
        base = work / f"hero-base-{scheme}.png"
        base.write_bytes(
            shoot(
                browser,
                ASSETS / "hero.html",
                (1200, 440),
                1,
                scheme,
                query="?layer=base",
            )
        )
        bubble = work / f"hero-bubble-{scheme}.png"
        bubble.write_bytes(
            shoot(
                browser,
                ASSETS / "hero.html",
                (1200, 440),
                1,
                scheme,
                True,
                "?layer=bubble",
            )
        )
        out = ASSETS / f"hero-{scheme}.webp"
        frames = work / f"frames-hero-{scheme}"
        frames.mkdir()
        ffmpeg(
            "-loop",
            1,
            "-framerate",
            FPS,
            "-t",
            take["dur"],
            "-i",
            base,
            *source_args(take),
            "-i",
            mask,
            "-loop",
            1,
            "-i",
            bubble,
            "-filter_complex",
            f"[1:v]{clip_filter(take, inner)}[v];[2:v]format=gray[m];[v][m]alphamerge[va];"
            "[0:v][va]overlay=647:41:shortest=1[b];[b][3:v]overlay=0:0:shortest=1,format=yuv420p[o]",
            "-map",
            "[o]",
            "-an",
            frames / "%04d.png",
        )
        encode_webp(frames, out, 72, 4)
        (ASSETS / f"hero-{scheme}.png").write_bytes(
            photo_png(
                shoot(
                    browser,
                    ASSETS / "hero.html",
                    (1200, 440),
                    2,
                    scheme,
                    query=f"?frame={poster.as_uri()}",
                )
            )
        )


def render_gallery(work: Path) -> None:
    """Encode the README gallery tiles: rounded, outlined, looping animated WebP."""
    size = (600, 338)
    mask = work / "tile-mask.png"
    rounded_mask(size, 16).save(mask)
    border = work / "tile-border.png"
    outline(size, 16).save(border)
    out_dir = ASSETS / "gallery"
    out_dir.mkdir(exist_ok=True)
    for take in GALLERY:
        out = out_dir / f"{take['name']}.webp"
        frames = work / f"frames-{take['name']}"
        frames.mkdir()
        ffmpeg(
            *source_args(take),
            "-i",
            mask,
            "-i",
            border,
            "-filter_complex",
            f"[0:v]{clip_filter(take, size)}[v];[1:v]format=gray[m];[v][m]alphamerge[va];"
            "[va][2:v]overlay=0:0:format=auto[o]",
            "-map",
            "[o]",
            "-an",
            frames / "%04d.png",
        )
        encode_webp(frames, out, 62, 2.5)


def render_readme_media(browser: Browser) -> None:
    """Render the hero and the gallery from the raw takes (needs ffmpeg)."""
    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        render_hero(browser, work)
        render_gallery(work)


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
        "font-weight:800;font-size:20px;line-height:1.2;letter-spacing:-0.01em'>Model<br>MCP</div>"
        "</div></div><style>svg{width:100%;height:100%;display:block}</style></body></html>"
    )


def main(media_only: bool = False) -> None:
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()

        if media_only:
            render_readme_media(browser)
            browser.close()
            return

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

        if TAKES.is_dir() and shutil.which("ffmpeg"):
            render_readme_media(browser)
        else:
            print(
                "Skipping README animations: needs ffmpeg and the raw takes in", TAKES
            )
        (ASSETS / "social-card.png").write_bytes(
            shoot(browser, ASSETS / "social-card.html", (1280, 640))
        )

        browser.close()


if __name__ == "__main__":
    main(media_only="--readme-media" in sys.argv[1:])
