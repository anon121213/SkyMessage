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
`sm-comet`.

Rules:
1. **Only `transform` and `opacity` animate** in lists, pages and anything that runs during scroll. Never animate
   `width/height/top/left/margin/box-shadow` on many elements; no `filter`/`backdrop-filter` animations.
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
8. **Ambient decoration** (the sky, the animated logo, call rings) is slow (seconds, not milliseconds),
   subtle, and never competes with content. No sparkles anywhere (DESIGN.md, "No AI clichés"). It pauses under `[inert]` (covered
   screens), stops with reduced motion and freezes in `html[data-fx="lite"]`.

## 2. Gesture map

| Where | Gesture | Result |
|---|---|---|
| Any phone page with a back target (chat, profile, group info, settings pages, media) | drag right (start ≥ 24px from the left edge) | page follows the finger, the page underneath shows through; release past 35% of the width or fast → Back, else springs back |
| Chat list item | swipe left | reveals Mute and Archive; full swipe archives (Undo toast) |
| Chat list item | swipe right | reveals Pin and Read; full swipe toggles pin |
| Chat list item | long-press | context menu (existing) |
| Message | swipe left | reply (existing) |
| Message (not on media) | double-tap | toggle the quick reaction (Settings › Chats › Quick reaction) |
| Message | long-press | message menu with reactions (existing); the bubble lifts slightly |
| Composer mic | tap / hold / slide left / slide up | switch voice↔video / record / cancel / lock (existing) |
| Media viewer | swipe sideways / down, pinch, double-tap | next-prev / close / zoom (existing) |
| Expanded call screen | drag down | minimize to the call bar |
| Bottom sheet, phone menu | drag down | close (existing) |
| In-app notification banner | swipe up | dismiss (existing) |
| Toast | swipe sideways | dismiss |

Coexistence:
- Swipe-back only claims **rightward** horizontal pans; message swipe-to-reply only **leftward**; chat list items
  claim both but live on a root page (no swipe-back there).
- An element that owns horizontal drags itself (sliders, horizontal scrollers, the PiP window, the media viewer, the
  composer, the recorder) carries `data-no-swipe-back`; swipe-back never starts inside it.
- A recognizer decides its axis after ~8px (`|dx| > 1.5·|dy|` for horizontal) and only then captures the pointer, so
  vertical scrolling stays native (`touch-action: pan-y` on horizontally swipeable surfaces).
- The Android system back gesture keeps the screen edges; ours starts 24px in.
