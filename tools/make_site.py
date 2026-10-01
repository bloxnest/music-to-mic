"""Builds docs/index.html and docs/guide.html from tools/site/*.html (shared icon sprite and footer).

Fills in the screenshot sizes from docs/assets (taken at 2x, shown at half size).
Run: python tools/make_site.py   (needs Pillow)
"""
import math
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SITE = ROOT / "tools" / "site"
DOCS = ROOT / "docs"


def bars():
    """The equalizer behind the hero: 96 bars, heights from a few slow waves, mirrored around the middle."""
    out = []
    for i in range(96):
        x = i * 12.5
        t = i / 95
        h = 40 + 70 * abs(math.sin(t * 9.1) * 0.6 + math.sin(t * 23.7 + 1.3) * 0.3 + math.sin(t * 3.2 + 0.4) * 0.4)
        h = min(220, h * (0.55 + 0.45 * math.sin(t * math.pi)))
        out.append(f'<rect x="{x:.1f}" y="{120 - h / 2:.1f}" width="6" height="{h:.1f}" rx="3"/>')
    return "".join(out)


def size(name):
    path = DOCS / "assets" / name
    if not path.exists():
        return 440, 760
    w, h = Image.open(path).size
    return w // 2, h // 2


def main():
    sprite = (SITE / "sprite.html").read_text(encoding="utf-8").rstrip("\n")
    footer = (SITE / "footer.html").read_text(encoding="utf-8").rstrip("\n")
    main_w, main_h = size("app-main.png")
    setup_w, setup_h = size("app-setup.png")
    for page in ("index", "guide"):
        html = (SITE / f"{page}.html").read_text(encoding="utf-8")
        html = (html.replace("{{SPRITE}}", sprite).replace("{{FOOTER}}", footer).replace("{{BARS}}", bars())
                .replace("{{MAIN_W}}", str(main_w)).replace("{{MAIN_H}}", str(main_h))
                .replace("{{SETUP_W}}", str(setup_w)).replace("{{SETUP_H}}", str(setup_h)))
        (DOCS / f"{page}.html").write_text(html, encoding="utf-8", newline="\n")
    print("site written to", DOCS)


if __name__ == "__main__":
    main()
