# Motion & gestures

How SkyMessage moves and what fingers can do. Every screen follows this; `docs/DESIGN.md` is the visual language,
`docs/UI-KIT.md` covers components.
The audience includes older relatives on budget Android phones: motion must explain what happened, never get in
the way, and stay smooth on a slow WebView.

## 1. Motion system

Tokens (`wwwroot/css/app.css`, `:root`):

| Token | Value | Use |
|---|---|---|
| `--sm-dur-fast` | 140ms | press feedback, small state flips (icons, checks, badges) |
| `--sm-dur` | 220ms | most enter/exit transitions, sheets, menus |
| `--sm-dur-slow` | 320ms | page-sized movement, viewer open/close |
| `--sm-ease` | cubic-bezier(0.2, 0.8, 0.2, 1) | default |
| `--sm-ease-out` | cubic-bezier(0.16, 1, 0.3, 1) | things arriving (decelerate) |
| `--sm-ease-in` | cubic-bezier(0.4, 0, 1, 1) | things leaving (accelerate) |
| `--sm-spring` | cubic-bezier(0.34, 1.56, 0.64, 1) | pops with a little overshoot (badges, reactions, send) |
| `--sm-spring-soft` | cubic-bezier(0.22, 1.25, 0.36, 1) | dialogs, menus, toasts arriving (gentle overshoot) |
| `--sm-dur-xslow` | 480ms | decorative entrances (empty states, auth, splash) |

Shared keyframes: `sm-fade-in`, `sm-pop-in`, `sm-pop` (0.6 → 1.08 → 1), `sm-bump` (1 → 1.18 → 1),
`sm-sheet-in`, `sm-slide-in-right`, `sm-slide-in-left`, `sm-slide-out-right`, `sm-drop-in`, `sm-rise-in`,
`sm-msg-in` (incoming message), `sm-msg-out` (outgoing message), `sm-shake`, `sm-spin`, `sm-shimmer`, `sm-pulse`,
`sm-dialog-in`, `sm-menu-in`, `sm-toast-in`, `sm-float`, `sm-ring` (pulsing ring), `sm-burst`
(particles: set `--dx/--dy`), `sm-draw` (stroke drawing), the sky's `sm-orb-a/b/c`, `sm-cloud-drift`, `sm-twinkle`,
`sm-comet`, and the logo's own `sm-logo-horizon-up/down` (below).

Rules:
1. **Only `transform` and `opacity` animate** in lists, pages and anything that runs during scroll. Never animate
   `width/height/top/left/margin/box-shadow` on many elements; no `filter`/`backdrop-filter` animations. The one
   exception is the folder tabs' pill: a single absolutely placed element whose width glides with its position.
2. **Short.** Nothing above 320ms except the call ring and looping indicators. Interactions respond within 1 frame.
3. **Enter decelerates, exit accelerates**, exits are ~30% shorter than enters.
4. **Animate changes, not loading.** History pages, resyncs and re-renders appear instantly; only things the user
   just did or that just arrived animate (a new message, a reaction, a badge change). One exception: when a screen
   mounts, its content may rise in once (`.sm-stagger`, ≤ 350ms in total).
5. **Reduced motion** (`html[data-reduce-motion="true"]`, set from the Animations setting or the OS) turns CSS
   animations/transitions off globally. JS-driven motion must check `reduceMotion()` from `js/app.js` and jump to the
   end state (gestures still work, they just don't spring).
6. **Haptics** (`HapticFeedback`) mark gesture thresholds: crossing the commit point of a swipe, a double-tap
   reaction, a long-press menu. Never on plain taps.
7. `will-change` only while a gesture or animation runs; remove it afterwards (the ambient sky layers are the
   exception: they animate for as long as they are visible).
8. **Ambient decoration** (the sky, call rings) is slow (seconds, not milliseconds),
   subtle, and never competes with content. No sparkles anywhere (DESIGN.md, "No AI clichés"). It pauses under `[inert]` (covered
   screens), stops with reduced motion and freezes in `html[data-fx="lite"]`. The user's own backgrounds (a gradient, a
   photo) never move at all: a photo is a still picture with its blur baked in, not a filter, and the sky's lights,
   clouds and stars are not drawn over them (`html[data-sky-own]`, `SkyBackdrop Lights`), so nothing composites
   invisible animated layers.

### The logo

`<Logo Animated="true">` plays **one** motion, once, when it appears: **the horizon opens**. The bubble shows whole
for a beat — sky and dawn meeting, no cut — then the cut opens from the middle to its full width (1m) and the mark
rests exactly as drawn. Delay 280 ms (the screen's own entrance comes first), `--sm-dur-xslow` (480 ms),
`cubic-bezier(0.3, 0, 0.2, 1)`: a soft start, a long settle.

Where: only where the logo is introduced — the signed-out intro (the first signed-out screen of an app run, `AuthIntro`:
welcome, or sign in / new account when the run starts there) and About when it opens. Everywhere else the logo is simply
there, finished:
- **Never on the boot splash.** It often lasts a fraction of a second (100–300 ms on a normal start), so the opening
  would be cut off and only its first frames — the bubble without its cut — would show. The boot splash continues the
  Android splash, which already shows the finished mark; its lockup only rises in.
- **Never on later signed-out screens** of the same run (welcome ↔ sign in ↔ new account): they rise in with the
  logo finished, so the opening is not replayed on every screen change.
- Not on screens that come back often (a lock screen): the motion is a greeting, not a loading indicator.

- How: two seam strips in the bubble's silhouette (a clip path) fill the cut; `sm-logo-horizon-up/down` slide them
  apart (transform only) until their edges meet the sky's bottom and the dawn band's top; at rest they lie outside the
  clip, so the finished logo is pixel-identical to a static one. Nothing loops, nothing glows, no shimmer, nothing
  rises like a sun.
- Reduced motion (the setting or the OS) and `html[data-fx="lite"]` skip it: the logo is simply there, finished.
- A re-render does not replay it; only a newly created `Animated` logo plays it again (About, each time it opens).

## 2. Gesture map

| Where | Gesture | Result |
|---|---|---|
| Any phone page with a back target (chat, profile, group info, settings pages, media) | drag right (start ≥ 24px from the left edge) | page follows the finger, the page underneath shows through; release past 35% of the width or fast → Back, else springs back |
| Chat list (with folders, "Swipe on a chat" = switch folders, the default once there are folders) | swipe left / right | the list follows the finger with the next / previous folder beside it (its first rows, from the top of what is visible) and the tabs' pill moves part of the way (a soft tint under dark labels); past 30% of the width or a flick → both slide on until the neighbour fills the screen and becomes the list without a jump (a shorter folder then scrolls up smoothly to where it ends); short of it they spring back; at the first/last folder it gives a little (rubber band); a haptic tick at the commit point. In `data-fx="lite"` and with reduced motion the list alone moves: it slides out and the next folder slides in from that side |
| Chat list item ("Swipe on a chat" = chat actions, or no folders) | swipe left | reveals Mute and Archive; full swipe archives (Undo toast) |
| Chat list item (as above) | swipe right | reveals Pin and Read (in a folder: the folder's own pin); full swipe toggles pin |
| Folder tab | tap / long-press (right-click) | open the folder (the pill glides, the list slides in from its side) / its menu |
| A folder's tab (not "All chats") | long-press, then drag sideways (mouse: drag) | the tab lifts, the others glide aside, the strip scrolls by itself near its ends; drop → new order of the folders (synced); letting go without moving (or resting) opens the menu instead |
| Folder tab strip | drag sideways / vertical mouse wheel | scrolls the tabs when they do not fit (its own pan: no folder swipe there; the wheel goes on to the list at the strip's ends) |
| Settings › Folders, a folder's handle | drag up/down (any pointer, no long-press) | the row follows, the others glide aside; drop → new order (synced) |
| Chat list item | long-press | context menu (existing) |
| Pinned chat list item (2+ pinned) | long-press, then drag up/down (mouse: drag) | lifts the row and reorders the pinned chats; letting go without moving (or resting) opens the menu |
| Message | swipe left | reply (existing) |
| Message (not on media) | double-tap | toggle the quick reaction (Settings › Chats › Quick reaction) |
| Message | long-press | message menu with reactions (existing); the bubble lifts slightly |
| Composer mic | tap / hold / slide left / slide up | switch voice↔video / record / cancel / lock (existing) |
| Media viewer | swipe sideways / down, pinch, double-tap | next-prev / close / zoom (existing) |
| Avatar viewer (profile, group info, chat header photo) | swipe sideways / down, pinch, double-tap | earlier-later photo of the history / close / zoom; it grows out of the tapped avatar and flies back into it (viewer.js) |
| Avatar crop editor | drag, pinch, wheel, slider | pan / zoom 1–5× (the photo always covers the square); rotate and flip glide 240 ms |
| Expanded call screen | drag down | minimize to the call bar |
| Bottom sheet, phone menu | drag down | close (existing) |
| In-app notification banner | swipe up | dismiss (existing) |
| Toast | swipe sideways | dismiss |

Coexistence:
- A pinned row lifted by its long-press owns the touch: it moves only vertically, the row swipe leaves it alone and the
  list does not scroll under it (it scrolls by itself near the edges). Before the lift, swipes and scrolling win.
- Swipe-back only claims **rightward** horizontal pans; message swipe-to-reply only **leftward**; chat list items
  claim both but live on a root page (no swipe-back there).
- On the chat list either the folder swipe or the row swipe owns sideways pans, never both: the setting picks one
  (`data-folder-swipe` on the list pane, `data-row-swipe-off` on the scroller). The folder swipe is touch/pen only (mouse
  users click the tabs, scroll the strip with the wheel and move between tabs with the arrow keys), leaves a pinned row
  lifted for reordering alone, and is off in the archive. Folder pins drag like global ones (long-press, then up/down).
- A folder's tab lifted by its long-press owns the touch like a lifted pinned row: the strip does not scroll under it.
  Before the lift, sideways drags scroll the strip.
- An element that owns horizontal drags itself (sliders, horizontal scrollers, the PiP window, the media and avatar
  viewers, the crop editor, the composer, the recorder) carries `data-no-swipe-back`; swipe-back never starts inside it.
- A recognizer decides its axis after ~8px (`|dx| > 1.5·|dy|` for horizontal) and only then captures the pointer, so
  vertical scrolling stays native (`touch-action: pan-y` on horizontally swipeable surfaces).
- The Android system back gesture keeps the screen edges; ours starts 24px in.
