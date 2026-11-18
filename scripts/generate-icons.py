"""Build the CadreLink Windows and Android assets from one geometric mark."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ACCENT = "#c9ee42"
INK = "#111411"
MARK = [
    [(23, 43), (23, 68), (30, 75), (43, 75), (53, 65), (33, 65),
     (33, 47), (41, 39), (53, 39), (63, 29), (37, 29)],
    [(85, 65), (85, 40), (78, 33), (65, 33), (55, 43), (75, 43),
     (75, 61), (67, 69), (55, 69), (45, 79), (71, 79)],
    [(38, 61), (61, 38), (70, 38), (70, 47), (47, 70), (38, 70)],
]
scale = 8
image = Image.new("RGBA", (108 * scale, 108 * scale))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((5 * scale, 5 * scale, 103 * scale, 103 * scale), 22 * scale, fill=ACCENT)
for part in MARK:
    draw.polygon([(x * scale, y * scale) for x, y in part], fill=INK)

windows = ROOT / "windows" / "Assets"
android = ROOT / "android" / "res"
branding = ROOT / "branding"
for folder in (windows, branding):
    folder.mkdir(exist_ok=True, parents=True)

logo = image.resize((256, 256), Image.Resampling.LANCZOS)
logo.save(branding / "cadrelink.png")
logo.save(windows / "CadreLink.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

for dpi, size in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
    folder = android / f"mipmap-{dpi}"
    folder.mkdir(exist_ok=True)
    image.resize((size, size), Image.Resampling.LANCZOS).save(folder / "ic_cadrelink.png")

def make_vector(fill):
    paths = "\n".join(
        f'    <path android:fillColor="{fill}" android:pathData="M' +
        " L".join(f"{x},{y}" for x, y in part) + ' Z"/>' for part in MARK
    )
    return ('<vector xmlns:android="http://schemas.android.com/apk/res/android" android:width="108dp"\n'
            '    android:height="108dp" android:viewportWidth="108" android:viewportHeight="108">\n'
            f"{paths}\n</vector>\n")

(android / "drawable" / "cadrelink_foreground.xml").write_text(make_vector(INK), encoding="utf-8")
(android / "drawable" / "cadrelink_notification.xml").write_text(make_vector("#ffffff"), encoding="utf-8")
(android / "mipmap-anydpi-v26" / "ic_cadrelink.xml").write_text(
    '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
    '    <background android:drawable="@color/cadrelink_icon"/>\n'
    '    <foreground android:drawable="@drawable/cadrelink_foreground"/>\n'
    '    <monochrome android:drawable="@drawable/cadrelink_notification"/>\n'
    '</adaptive-icon>\n', encoding="utf-8"
)
(android / "values" / "cadrelink.xml").write_text(
    f'<resources><color name="cadrelink_icon">{ACCENT}</color></resources>\n', encoding="utf-8"
)
