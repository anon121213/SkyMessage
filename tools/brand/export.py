#!/usr/bin/env python3
"""Regenerates every shipped icon asset from the brand sources in docs/brand («Горизонт», see docs/brand/README.md).

    python3 tools/brand/export.py            write the files below from docs/brand
    python3 tools/brand/export.py --check    render into a temp dir and compare with the repository (exit 1 on any
                                             difference); also checks that Kit/Logo.razor carries the exact paths

Needs Pillow and a local Chrome or Chromium (CHROME_PATH, else the macOS install or /opt/pw-browsers/chromium): every
PNG is the SVG rastered by headless Chrome at its final size, transparent background, nothing redrawn. Writes:

  web (src/Client/SkyMessage.Web/wwwroot/icons)
    favicon.svg            = docs/brand/favicon-32.svg, the tile hinted for 32 px (straight edges on whole pixels): the
                           browser tab icon, crisp at 32 device px (the exact drawing blurs the 1m cut at that size)
    favicon-32.png         favicon-32.svg rastered at 32 px (browsers without SVG favicons)
    icon.svg               = docs/brand/icon.svg (the launcher view: 72 of the 108 dp canvas on the squircle tile), the
                           manifest's scalable icon (sizes "any"): exact geometry for large renders
    icon-192/512.png       icon.svg (purpose "any": the squircle tile, transparent corners)
    maskable-192/512.png   bg + fg, the same 72 dp view full bleed (purpose "maskable"; the mark is inside the 80 % circle)
    apple-touch-icon.png   the full-bleed view at 180 px, opaque (iOS draws its own corners)
    badge-96.png           docs/brand/notif.svg at 96 px: the white notification glyph (Web Push badge, "monochrome")
  Android (src/Client/SkyMessage.App)
    Resources/AppIcon/appicon.svg, appiconfg.svg, Resources/Splash/splash.svg   bg.svg, fg.svg, the mark for the splash
    Platforms/Android/Resources/drawable/appicon_monochrome.xml   mono.svg as a VectorDrawable (themed icon, Android 13+)
    Platforms/Android/Resources/drawable/ic_stat_skymessage.xml   notif.svg as a VectorDrawable (notification small icon)
    Platforms/Android/Resources/mipmap-*/appicon.png, appicon_round.png   legacy launcher icons (48 dp: 48..192 px), the
                           launcher view on the squircle tile / in a circle; they replace MAUI's full-canvas squares
  iOS / Mac Catalyst (src/Client/SkyMessage.App)
    Resources/AppIcon/Apple/appicon.svg   the full-bleed view as one opaque layer (= apple-touch-icon.png's drawing):
                           the mark at 58 % of the icon, as approved; the system draws the corners
  docs/brand/play-store-512.png   the full-bleed view at 512 px, opaque (store listing)
  src/Client/SkyMessage.Client.UI/wwwroot/img/logo.svg, logo-mark.svg   static copies for anything outside the app (the
                           tile = icon.svg; the mark in mark-light.svg colours, mark-dark.svg's when the viewer prefers dark)
The adaptive-icon XMLs (Platforms/Android/Resources/mipmap-anydpi-v26) are hand-written and only reference the layers.
"""
import argparse
import filecmp
import os
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

from PIL import Image, ImageChops

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BRAND = os.path.join(REPO, "docs", "brand")
WEB = "src/Client/SkyMessage.Web/wwwroot/icons"
APP = "src/Client/SkyMessage.App"
DROID = APP + "/Platforms/Android/Resources"
LOGO = "src/Client/SkyMessage.Client.UI/Kit/Logo.razor"
SVG_NS = "{http://www.w3.org/2000/svg}"
DENSITIES = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}


def chrome_path():
    if os.environ.get("CHROME_PATH"):
        return os.environ["CHROME_PATH"]
    if sys.platform == "darwin":
        return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
    return "/opt/pw-browsers/chromium"


def paths_of(svg_file):
    """(fill, d) of every <path> of a brand SVG, in document order."""
    root = ET.parse(svg_file).getroot()
    return [(p.get("fill"), p.get("d")) for p in root.iter(SVG_NS + "path")]


def stops_of(svg_file):
    root = ET.parse(svg_file).getroot()
    return [s.get("stop-color") for s in root.iter(SVG_NS + "stop")]


def view_svg(shape):
    """The launcher view (units 76..380 of the 456-unit = 108 dp canvas) from bg.svg + fg.svg: full bleed, or in a circle
    (the round legacy icon). The ground gradient runs over the whole canvas, as on the adaptive icon."""
    top, bottom = stops_of(os.path.join(BRAND, "bg.svg"))
    pieces = "".join(f'  <path fill="{fill}" d="{d}"/>\n' for fill, d in paths_of(os.path.join(BRAND, "fg.svg")))
    clip = ('    <clipPath id="view"><circle cx="228" cy="228" r="152"/></clipPath>\n' if shape == "circle" else "")
    body = (f'  <g clip-path="url(#view)">\n  <rect x="76" y="76" width="304" height="304" fill="url(#ground)"/>\n{pieces}  </g>\n'
            if shape == "circle" else f'  <rect x="76" y="76" width="304" height="304" fill="url(#ground)"/>\n{pieces}')
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="304" height="304" viewBox="76 76 304 304">\n  <defs>\n'
            '    <linearGradient id="ground" x1="0" y1="0" x2="0" y2="456" gradientUnits="userSpaceOnUse">\n'
            f'      <stop offset="0" stop-color="{top}"/>\n      <stop offset="1" stop-color="{bottom}"/>\n'
            f'    </linearGradient>\n{clip}  </defs>\n{body}</svg>\n')


def raster(svg_file, size, out_png, work, rgb=False):
    page = os.path.join(work, "page.html")
    with open(page, "w", encoding="utf-8") as f:
        f.write("<!doctype html><html><head><meta charset='utf-8'><style>html,body{margin:0;background:transparent}"
                f"</style></head><body><img src='file://{os.path.abspath(svg_file)}' "
                f"style='display:block;width:{size}px;height:{size}px'></body></html>")
    shot = os.path.join(work, "shot.png")
    if os.path.exists(shot):
        os.remove(shot)
    subprocess.run([chrome_path(), "--headless=new", "--disable-gpu", "--hide-scrollbars", "--allow-file-access-from-files",
                    "--default-background-color=00000000", "--no-first-run", "--no-default-browser-check",
                    "--disable-extensions", "--force-device-scale-factor=1", f"--user-data-dir={os.path.join(work, 'profile')}",
                    f"--window-size={size},{size}", f"--screenshot={shot}", "file://" + page],
                   check=True, capture_output=True, timeout=120)
    im = Image.open(shot).convert("RGBA").crop((0, 0, size, size))
    if rgb:
        im = im.convert("RGB")
    os.makedirs(os.path.dirname(out_png), exist_ok=True)
    im.save(out_png, optimize=True)


def vector_drawable(svg_file, size_dp, viewport, comment, fill):
    lines = ['<?xml version="1.0" encoding="utf-8"?>', comment,
             '<vector xmlns:android="http://schemas.android.com/apk/res/android"',
             f'    android:width="{size_dp}dp"', f'    android:height="{size_dp}dp"',
             f'    android:viewportWidth="{viewport}"', f'    android:viewportHeight="{viewport}">']
    lines += [f'    <path android:fillColor="{fill}" android:pathData="{d}" />' for _, d in paths_of(svg_file)]
    return "\n".join(lines + ["</vector>", ""])


MONO_COMMENT = """<!-- SkyMessage themed icon (Android 13+): the monochrome layer of the adaptive icon, docs/brand/mono.svg converted
     with exact paths (the 108dp canvas = 456 units, same geometry as the foreground). The system uses its alpha only. -->"""
NOTIF_COMMENT = """<!-- SkyMessage notification icon «Горизонт» (docs/brand/notif.svg): the bubble cut by the horizon as a white
     silhouette, redrawn on the 24dp grid (live area 2..22, every straight edge on a whole unit, gap 17..19). The
     system tints it; NotificationChannels.AccentColor colours it in the shade. -->"""
SPLASH_COMMENT = """<!-- SkyMessage splash mark: the launcher foreground (docs/brand/fg.svg, same geometry) on the brand ground #2A1F86
     (MauiSplashScreen Color). Android 8-11 draw it at BaseSize, 288 dp: the mark is about 112 dp wide. On Android 12+
     MAUI's drawable-v31/maui_splash_image fixes the image at 108 dp and the system scales it to its 192 dp icon circle,
     whatever the BaseSize: the whole 456-unit canvas spans 192 dp, the mark is about 75 dp wide and its farthest point
     (the dawn's corner) about 48 dp from the centre, well inside the 96 dp radius: the mask never clips it. -->"""
APPLE_COMMENT = """<!-- SkyMessage app icon for iOS and Mac Catalyst «Горизонт»: the launcher view (units 76..380 of the
     108 dp canvas, docs/brand/bg.svg + fg.svg) full bleed, one opaque layer: the same drawing as the web
     apple-touch-icon.png, the mark at 58 % of the icon. The system draws the corners. -->"""


def splash_svg():
    pieces = "".join(f'  <path fill="{fill}" d="{d}"/>\n' for fill, d in paths_of(os.path.join(BRAND, "fg.svg")))
    return ('<?xml version="1.0" encoding="UTF-8"?>\n' + SPLASH_COMMENT + "\n"
            '<svg xmlns="http://www.w3.org/2000/svg" width="456" height="456" viewBox="0 0 456 456">\n' + pieces + "</svg>\n")


def apple_icon_svg():
    return '<?xml version="1.0" encoding="UTF-8"?>\n' + APPLE_COMMENT + "\n" + view_svg("full")


def mark_svg():
    """The mark for light surfaces (mark-light.svg), switching to the dark colours (mark-dark.svg) by the viewer's
    colour scheme."""
    (sky_l, sky), (dawn_l, dawn) = paths_of(os.path.join(BRAND, "mark-light.svg"))
    (sky_d, _), (dawn_d, _) = paths_of(os.path.join(BRAND, "mark-dark.svg"))
    return ('<?xml version="1.0" encoding="UTF-8"?>\n'
            '<!-- SkyMessage mark «Горизонт» (docs/brand/mark-light.svg; mark-dark.svg colours when the viewer prefers dark). -->\n'
            '<svg xmlns="http://www.w3.org/2000/svg" width="177.33" height="177.33" viewBox="133 133 177.33 177.33">\n'
            f'  <style>@media (prefers-color-scheme: dark) {{ .sky {{ fill: {sky_d}; }} .dawn {{ fill: {dawn_d}; }} }}</style>\n'
            f'  <path class="sky" fill="{sky_l}" d="{sky}"/>\n  <path class="dawn" fill="{dawn_l}" d="{dawn}"/>\n</svg>\n')


def build(out):
    """Writes every asset under `out` (the repository, or a temp dir for --check) at its repository path."""
    work = tempfile.mkdtemp(prefix="brand-export-")
    try:
        def put(rel, text):
            path = os.path.join(out, rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)

        def copy(src, rel):
            path = os.path.join(out, rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            shutil.copyfile(os.path.join(BRAND, src), path)

        full, circle = os.path.join(work, "full.svg"), os.path.join(work, "circle.svg")
        with open(full, "w") as f:
            f.write(view_svg("full"))
        with open(circle, "w") as f:
            f.write(view_svg("circle"))
        icon = os.path.join(BRAND, "icon.svg")

        copy("favicon-32.svg", f"{WEB}/favicon.svg")
        copy("icon.svg", f"{WEB}/icon.svg")
        raster(os.path.join(BRAND, "favicon-32.svg"), 32, os.path.join(out, WEB, "favicon-32.png"), work)
        for size in (192, 512):
            raster(icon, size, os.path.join(out, WEB, f"icon-{size}.png"), work)
            raster(full, size, os.path.join(out, WEB, f"maskable-{size}.png"), work)
        raster(full, 180, os.path.join(out, WEB, "apple-touch-icon.png"), work, rgb=True)
        raster(os.path.join(BRAND, "notif.svg"), 96, os.path.join(out, WEB, "badge-96.png"), work)
        raster(full, 512, os.path.join(out, "docs/brand/play-store-512.png"), work, rgb=True)

        copy("bg.svg", f"{APP}/Resources/AppIcon/appicon.svg")
        copy("fg.svg", f"{APP}/Resources/AppIcon/appiconfg.svg")
        put(f"{APP}/Resources/AppIcon/Apple/appicon.svg", apple_icon_svg())
        put(f"{APP}/Resources/Splash/splash.svg", splash_svg())
        put(f"{DROID}/drawable/appicon_monochrome.xml",
            vector_drawable(os.path.join(BRAND, "mono.svg"), 108, 456, MONO_COMMENT, "#FF000000"))
        put(f"{DROID}/drawable/ic_stat_skymessage.xml",
            vector_drawable(os.path.join(BRAND, "notif.svg"), 24, 24, NOTIF_COMMENT, "#FFFFFFFF"))
        for density, size in DENSITIES.items():
            raster(icon, size, os.path.join(out, DROID, f"mipmap-{density}", "appicon.png"), work)
            raster(circle, size, os.path.join(out, DROID, f"mipmap-{density}", "appicon_round.png"), work)

        copy("icon.svg", "src/Client/SkyMessage.Client.UI/wwwroot/img/logo.svg")
        put("src/Client/SkyMessage.Client.UI/wwwroot/img/logo-mark.svg", mark_svg())
    finally:
        shutil.rmtree(work, ignore_errors=True)


def same_png(a, b):
    ia, ib = Image.open(a), Image.open(b)
    if ia.size != ib.size or ia.mode != ib.mode:
        return False
    return ImageChops.difference(ia.convert("RGBA"), ib.convert("RGBA")).getbbox() is None


def check():
    tmp = tempfile.mkdtemp(prefix="brand-check-")
    problems = []
    try:
        build(tmp)
        for root, _, files in os.walk(tmp):
            for name in files:
                rel = os.path.relpath(os.path.join(root, name), tmp)
                mine, theirs = os.path.join(root, name), os.path.join(REPO, rel)
                if not os.path.exists(theirs):
                    problems.append(f"missing: {rel}")
                elif not (same_png(mine, theirs) if name.endswith(".png") else filecmp.cmp(mine, theirs, shallow=False)):
                    problems.append(f"differs: {rel}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    logo = open(os.path.join(REPO, LOGO), encoding="utf-8").read()
    wanted = [d for _, d in paths_of(os.path.join(BRAND, "fg.svg"))] + [d for _, d in paths_of(os.path.join(BRAND, "lockup.svg"))]
    tile = re.search(r'<clipPath id="tile"><path d="([^"]+)"', open(os.path.join(BRAND, "icon.svg")).read()).group(1)
    for d in wanted + [tile]:
        if d not in logo:
            problems.append(f"{LOGO}: a path of docs/brand is missing ({d[:40]}…)")
    for p in problems:
        print(p)
    print("brand assets: " + ("OK" if not problems else f"{len(problems)} problem(s)"))
    return 1 if problems else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true", help="compare a fresh export with the repository")
    if parser.parse_args().check:
        sys.exit(check())
    build(REPO)
    print("written from docs/brand")


if __name__ == "__main__":
    main()
