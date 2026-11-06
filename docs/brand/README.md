# SkyMessage brand — «Горизонт» (Horizon)

The logo is an **outgoing message bubble** — a circle with one tight square corner at the bottom right, the corner an
outgoing message has in SkyMessage itself — **cut near its base by a straight horizon**. Above the cut is the sky, below
it a thin warm band of dawn. One idea, three flat colours, no decoration. `sheet.png` is the approved presentation
sheet (launcher, themed icon, notification, the mark in the app, the lockup, the palette).

## Construction

Everything is drawn on the Android adaptive-icon canvas: **108 dp = 456 units**, grid **m = 3 dp = 12.67 units**.

| Element | Geometry |
|---|---|
| Bubble | circle R 7m (88.67) centred at (52.5, 52.5) dp = (221.67, 221.67); the bottom-right quadrant squared off |
| Sky | the bubble above the horizon, down to 4m under the centre (y 272.33) |
| Horizon (the cut) | 1m thick (y 272.33 … 285), always empty: the surface shows through |
| Dawn | a 2m band under the cut (y 285 … 310.33); its outgoing corner rounded m/2 |
| Safe zone | the farthest point (the dawn's corner) is about 27 dp from the centre, inside the 66 dp safe circle of adaptive icons |
| Lockup | "SkyMessage" in Montserrat 700 (outlines, OFL), cap height 7m = half the bubble, the letters stand on the horizon, 3m from the mark |

All straight edges sit on the 1.5 dp grid (whole pixels at 48 and 96 px). The 24 dp notification glyph (`notif.svg`) and
the 32 px favicon (`favicon-32.svg`) are redrawn on their own pixel grids (same idea, edges on whole pixels).

## Colours (`palette.json`)

| Name | Hex | Role |
|---|---|---|
| Night indigo | `#2A1F86` → `#4A35C8` | The icon ground: one vertical two-stop gradient — the brand's only gradient, on the icon ground only. On light surfaces `#2A1F86` is also the sky of the mark and the wordmark. |
| Sky white | `#FFFFFF` | The sky on the icon and on dark surfaces; the wordmark on dark. |
| Dawn peach | `#FFAE86`, on light `#D9622F` | The dawn band — the one warm note: peach on the icon and on dark surfaces, the deeper `#D9622F` on light surfaces (3.34:1 on `#F6F3FF`, 3.66:1 on white; on the light sky see below). |
| Accent violet | `#6A4BFF` | The UI accent (unchanged): buttons, outgoing bubbles, links, the notification accent. Not a logo colour. |

Contrast: white on Night indigo 12.82:1 (7.97:1 on the gradient's bottom), peach on Night indigo 7.11:1, Night indigo
on `#F6F3FF` 11.71:1, white on `#0D0B1E` 19.38:1. In the app the theme tokens are `--sm-brand-sky`, `--sm-brand-dawn`
and `--sm-brand-ink` (`app.css`; light: `#2A1F86` / `#D9622F` / `#2A1F86`, dark: white / `#FFAE86` / white).

The light-surface dawn is below 3:1 where the lockups actually stand: on the light sky `#D9622F` measures 3.02:1 at its
top (`#ECE6FF`), 2.99:1 behind the welcome lockup and 2.84:1 behind About's and the boot splash's (`#E7DEFF`); only the
Settings card reaches 3.1:1. That is acceptable for a logo — logotypes carry no contrast requirement (WCAG 1.4.3), and
the mark stays recognisable by its Night-indigo sky (at least 9.9:1 on every light surface). A deeper light dawn,
`#D45B27`, would keep 3:1 on every light sky (3.06:1 at the darkest) — a colour change only the owner can approve.
`palette.json` is the delivered v2 file with this note corrected to the measured values; the colours are as delivered.

## Rules

- Flat colour only. The single two-stop gradient lives in the icon ground; no other gradient, no glow, no shadow, no
  sparkles, no gloss (docs/DESIGN.md, "No AI clichés").
- Sky above the horizon, dawn below it — never swapped. Sky: white on dark and on the ground, Night indigo on light.
  Dawn: `#FFAE86` on dark and on the ground, `#D9622F` on light.
- The horizon cut is always negative space, 1m thick; the dawn band is 2m.
- The wordmark is one solid colour (Night indigo on light, white on dark), never gradient-filled; its baseline sits on
  the horizon.
- Use the geometry as it is: do not redraw, restyle or "improve" it; at small sizes use the hinted files.
- Nothing around the logo echoes the rejected first logo (a sun behind a cloud): no sun, no puffy three-circle cloud.
  The app's illustrations use capsule clouds, the moon, stars and a horizon line (docs/DESIGN.md §5), and decoration
  keeps clear of the logo.

## Files

| File | What it is |
|---|---|
| `bg.svg` | Adaptive-icon background layer: the Night-indigo gradient (= `SkyMessage.App/Resources/AppIcon/appicon.svg`). |
| `fg.svg` | Adaptive-icon foreground layer: white sky + peach dawn (= `appiconfg.svg`; also the splash mark). |
| `mono.svg` | Themed-icon layer (Android 13+), both pieces in one colour (→ `drawable/appicon_monochrome.xml`). |
| `notif.svg` | Notification glyph, 24 dp, white (→ `drawable/ic_stat_skymessage.xml`, the web `badge-96.png`). |
| `icon.svg` | The launcher view (72 of the 108 dp) on the squircle tile (→ web `icon.svg`, the manifest's scalable icon; `icon-192/512.png`; legacy launcher PNGs). |
| `favicon-32.svg` | The tile hinted for 32 px (→ web `favicon.svg`, the browser tab icon, and `favicon-32.png`). |
| `mark-light.svg`, `mark-dark.svg` | The mark alone for light (`#F6F3FF`) and dark (`#0D0B1E`) surfaces. |
| `lockup.svg`, `lockup-dark.svg` | Mark + wordmark for light and dark surfaces. |
| `palette.json` | The colours, their roles (en/ru), contrast and the rules (the light-dawn contrast note measured, see Colours). |
| `play-store-512.png` | Store listing icon, 512 px, full bleed, opaque. |
| `sheet.png` | The approved presentation sheet (compressed; brand colours exact). |

## Where it is used

- **Android** (`src/Client/SkyMessage.App`): `<MauiIcon>` from `appicon.svg` + `appiconfg.svg` on `#2A1F86`; the adaptive
  icon XMLs in `Platforms/Android/Resources/mipmap-anydpi-v26` add the monochrome layer; legacy `appicon.png` /
  `appicon_round.png` (48 dp, all densities) show the launcher view on the tile / in a circle; the notification small
  icon is `ic_stat_skymessage`, tinted with the UI accent (`NotificationChannels.AccentColor`, the one constant).
  The splash is the mark on `#2A1F86`. Android 8–11 draw it at its BaseSize, 288 dp (the mark about 112 dp wide).
  Android 12+ ignore the BaseSize: MAUI's `drawable-v31/maui_splash_image` fixes the image at 108 dp and the system
  scales it to its 192 dp icon circle, so the whole 456-unit canvas spans 192 dp, the mark is about 75 dp wide and its
  farthest point (the dawn's corner) about 48 dp from the centre — inside the 96 dp radius, never clipped. (A
  `values-v31` theme with a 108 dp vector of `fg.svg` as `windowSplashScreenAnimatedIcon` would give 12+ the same
  112 dp mark; not done — the owner's call.)
- **iOS / Mac Catalyst** (`src/Client/SkyMessage.App`): a second `<MauiIcon>`, `Resources/AppIcon/Apple/appicon.svg`:
  the launcher view full bleed as one opaque layer (the drawing of `apple-touch-icon.png`, the mark at 58 % of the
  icon); the system draws the corners. The Android layers would leave the mark at 39 % of the whole 108 dp canvas.
- **Web** (`src/Client/SkyMessage.Web/wwwroot`): `icons/favicon.svg` (the tab icon: `favicon-32.svg`, hinted, crisp at
  32 device px; linked before `favicon-32.png` for browsers without SVG favicons), `icons/icon.svg` (`icon.svg`, exact
  geometry: the manifest's scalable "any" icon), `apple-touch-icon.png`, `icon-192/512.png` (purpose "any"),
  `maskable-192/512.png` (full bleed, the mark inside the 80 % circle), `badge-96.png` (notification badge,
  "monochrome"); `manifest.webmanifest` uses `#2A1F86` as its background and theme colour.
- **In the app** (`Kit/Logo.razor`): the tile (rail, chat-list placeholder, sign-in, QR codes), the mark, and the lockup
  (`Wordmark`: boot splash, welcome screen, About, the Settings footer). `Animated` opens the horizon once, only where
  the logo is introduced: the first signed-out screen of an app run and About (docs/MOTION.md); the boot splash and
  every later screen show it finished. Static copies for anything outside the app:
  `SkyMessage.Client.UI/wwwroot/img/logo.svg`, `logo-mark.svg`.

## Regenerating the PNGs

Every PNG is the SVG rastered by headless Chrome at its final size — nothing is drawn by hand:

```bash
python3 tools/brand/export.py           # writes every web, Android and iOS asset below and play-store-512.png
python3 tools/brand/export.py --check   # renders into a temp dir, compares with the repository, checks Kit/Logo.razor
```

It needs Pillow and Chrome/Chromium (`CHROME_PATH`, else the macOS app or `/opt/pw-browsers/chromium`). MAUI renders the
adaptive layers and the splash PNGs itself at build time from the SVGs. After changing the artwork, update the path
constants in `Kit/Logo.razor` (the check fails until they match) and rebuild the Android app.
