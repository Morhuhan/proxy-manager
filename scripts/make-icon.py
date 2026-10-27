"""Renders src/ProxyManager/Assets/app.ico: one input fanning out into two routes.

Usage: python scripts/make-icon.py  (needs Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 1024
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
OUT = Path(__file__).resolve().parent.parent / "src" / "ProxyManager" / "Assets" / "app.ico"

TOP = (59, 130, 246)     # #3B82F6
BOTTOM = (29, 78, 216)   # #1D4ED8
WHITE = (255, 255, 255, 255)


def render() -> Image.Image:
    # Vertical gradient clipped to a rounded square.
    gradient = Image.new("RGBA", (SIZE, SIZE))
    draw = ImageDraw.Draw(gradient)
    for y in range(SIZE):
        t = y / (SIZE - 1)
        color = tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM))
        draw.line([(0, y), (SIZE, y)], fill=color + (255,))

    mask = Image.new("L", (SIZE, SIZE), 0)
    margin = SIZE * 0.04
    ImageDraw.Draw(mask).rounded_rectangle(
        [margin, margin, SIZE - margin, SIZE - margin], radius=SIZE * 0.22, fill=255)
    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    icon.paste(gradient, mask=mask)

    # Glyph: source dot -> junction -> two routes ending in arrowheads.
    draw = ImageDraw.Draw(icon)
    s = SIZE
    stroke = round(s * 0.085)
    dot = s * 0.095
    src = (s * 0.23, s * 0.5)
    junction = (s * 0.42, s * 0.5)
    r = stroke / 2

    draw.line([src, junction], fill=WHITE, width=stroke)
    for y in (0.31, 0.69):
        bend = (s * 0.56, s * y)
        tip_base = (s * 0.66, s * y)
        draw.line([junction, bend, tip_base], fill=WHITE, width=stroke, joint="curve")
        draw.ellipse([bend[0] - r, bend[1] - r, bend[0] + r, bend[1] + r], fill=WHITE)
        draw.polygon([(s * 0.64, s * (y - 0.115)), (s * 0.82, s * y), (s * 0.64, s * (y + 0.115))], fill=WHITE)
    draw.ellipse([junction[0] - r, junction[1] - r, junction[0] + r, junction[1] + r], fill=WHITE)
    draw.ellipse([src[0] - dot, src[1] - dot, src[0] + dot, src[1] + dot], fill=WHITE)
    return icon


def main() -> None:
    OUT.parent.mkdir(parents=True, exist_ok=True)
    big = render()
    # BMP frames: readable by every Windows API (shell, WPF, System.Drawing).
    big.save(OUT, format="ICO", sizes=[(s, s) for s in SIZES], bitmap_format="bmp")
    big.resize((256, 256), Image.LANCZOS).save(OUT.with_suffix(".png"))
    print(f"{OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
