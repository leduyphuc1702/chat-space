#!/usr/bin/env python3
"""Static SF Pro / SF Mono instances for the Windows build.

macOS draws the system font from the variable SFNS.ttf, choosing the optical size (opsz) from the point
size and applying the font's `trak` tracking. Avalonia can do neither, so this writes one static face per
(weight, optical size), subset to Latin + Vietnamese, plus a JSON tracking table the app applies itself.
"""
import json
import sys
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

OUT = Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)

# SwiftUI Font.Weight -> SF wght axis value (fvar named instances) and CSS weight class.
WEIGHTS = {
    "ultraLight": (30.925, 100),
    "thin": (110.725, 200),
    "light": (274.315, 300),
    "regular": (400.0, 400),
    "medium": (510.0, 500),
    "semibold": (590.0, 600),
    "bold": (700.0, 700),
    "heavy": (860.0, 800),
    "black": (1000.0, 900),
}
OPSZ = [17, 20, 24, 28]

UNICODES = (
    list(range(0x20, 0x7F)) + list(range(0xA0, 0x250)) + list(range(0x300, 0x370)) + list(range(0x1E00, 0x1F00))
    + list(range(0x2000, 0x2070)) + list(range(0x20A0, 0x20C1)) + list(range(0x2100, 0x2200))
    + list(range(0x2190, 0x2200)) + list(range(0x2200, 0x2300)) + list(range(0x25A0, 0x2600))
)


def rename(font: TTFont, family: str, style: str, weight_class: int) -> None:
    name = font["name"]
    full = f"{family} {style}"
    ps = (family + "-" + style).replace(" ", "")
    for rec in list(name.names):
        if rec.nameID in (1, 2, 3, 4, 6, 16, 17, 21, 22):
            name.removeNames(nameID=rec.nameID)
    for nid, val in ((1, family), (2, "Regular"), (3, ps), (4, full), (6, ps), (16, family), (17, style)):
        name.setName(val, nid, 3, 1, 0x409)
        name.setName(val, nid, 1, 0, 0)
    font["OS/2"].usWeightClass = weight_class
    font["OS/2"].fsSelection = (font["OS/2"].fsSelection & ~0b1100001) | 0b1000000  # REGULAR bit only
    font["head"].macStyle = 0


def subset_font(font: TTFont) -> None:
    opts = subset.Options()
    opts.layout_features = ["*"]
    opts.name_IDs = ["*"]
    opts.notdef_outline = True
    opts.drop_tables += ["trak", "MERG", "meta", "STAT"]
    s = subset.Subsetter(opts)
    s.populate(unicodes=UNICODES)
    s.subset(font)


src = "/System/Library/Fonts/SFNS.ttf"
for opsz in OPSZ:
    for wname, (wght, wclass) in WEIGHTS.items():
        f = TTFont(src)
        inst = instancer.instantiateVariableFont(f, {"wght": wght, "wdth": 100, "opsz": opsz, "GRAD": 400})
        subset_font(inst)
        rename(inst, f"SF Pro {opsz}", wname, wclass)
        inst.save(OUT / f"SFPro-{opsz}-{wname}.ttf")
        print("wrote", f"SFPro-{opsz}-{wname}.ttf")

mono = TTFont("/System/Library/Fonts/SFNSMono.ttf")
if "fvar" in mono:
    axes = {a.axisTag: a.defaultValue for a in mono["fvar"].axes}
    for wname in ("regular", "medium", "semibold", "bold"):
        m = TTFont("/System/Library/Fonts/SFNSMono.ttf")
        loc = dict(axes)
        loc["wght"] = max(min(WEIGHTS[wname][0], max(a.maxValue for a in m["fvar"].axes if a.axisTag == "wght")),
                          min(a.minValue for a in m["fvar"].axes if a.axisTag == "wght"))
        inst = instancer.instantiateVariableFont(m, loc)
        subset_font(inst)
        rename(inst, "SF Mono", wname, WEIGHTS[wname][1])
        inst.save(OUT / f"SFMono-{wname}.ttf")
        print("wrote", f"SFMono-{wname}.ttf")

# Tracking (track 0) in font units per 2048 em, keyed by point size; the app interpolates.
trak = TTFont(src)["trak"].horizData[0.0]
(OUT / "sf-tracking.json").write_text(json.dumps({"unitsPerEm": 2048, "track": {str(k): v for k, v in trak.items()}}))
print("wrote sf-tracking.json")
