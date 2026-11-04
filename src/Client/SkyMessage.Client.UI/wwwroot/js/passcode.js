// Physical keyboards for the passcode pads (Features/Passcode/PasscodePad.razor): digits and Backspace go to the pad
// in front, so on a computer the lock screen and the passcode dialogs take typed digits without any text field (phones
// use our on-screen keypad; the system keyboard never opens). "In front" = the most recently attached pad that is
// connected, not inside [inert] and not covered by something else at its center (the call screen over the lock screen,
// the lock screen over a settings dialog). While the lock screen is in front, Escape is held back so the app
// underneath does not close its dialogs (app.js treats Escape as Back); the lock screen gets it instead (holdEscape).

const pads = [];
const holds = [];
let listening = false;

function inFront(entry) {
  const el = entry.root;
  if (!el) return false;
  if (!el.isConnected || el.closest('[inert]')) return false;
  const r = el.getBoundingClientRect();
  if (r.width === 0 || r.height === 0) return false;
  const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
  return !!hit && (hit === el || el.contains(hit));
}

function keyOf(e) {
  if (/^[0-9]$/.test(e.key)) return e.key;
  if (e.key === 'Backspace' || e.key === 'Delete') return 'Backspace';
  return null;
}

function onKeyDown(e) {
  if (e.defaultPrevented || e.isComposing || e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === 'Escape') {
    for (let i = holds.length - 1; i >= 0; i--) {
      const hold = holds[i];
      if (!inFront(hold)) continue;
      e.preventDefault();
      e.stopPropagation();
      hold.ref.invokeMethodAsync('OnEscape').catch(() => { });
      return;
    }
  }
  for (let i = pads.length - 1; i >= 0; i--) {
    const entry = pads[i];
    if (!inFront(entry)) continue;
    const key = keyOf(e);
    if (key) {
      e.preventDefault();
      e.stopPropagation();
      // A held digit types once; a held Backspace keeps erasing.
      if (!e.repeat || key === 'Backspace') entry.ref.invokeMethodAsync('OnKey', key).catch(() => { });
    } else if (e.key === 'Escape' && entry.lockScreen) {
      e.preventDefault();
      e.stopPropagation();
    }
    return;
  }
}

function listen() {
  if (listening) return;
  window.addEventListener('keydown', onKeyDown, true);
  listening = true;
}

function unlisten() {
  if (pads.length || holds.length || !listening) return;
  window.removeEventListener('keydown', onKeyDown, true);
  listening = false;
}

/** Starts sending keys to the pad's OnKey(key) while it is in front; dispose() stops. */
export function attach(root, ref, lockScreen) {
  if (!root || !ref) return { dispose() { } };
  const entry = { root, ref, lockScreen: !!lockScreen };
  pads.push(entry);
  listen();
  return {
    dispose() {
      const index = pads.indexOf(entry);
      if (index >= 0) pads.splice(index, 1);
      unlisten();
    }
  };
}

/** The lock screen: Escape goes to its OnEscape() while it is in front (never to the app underneath); dispose() stops. */
export function holdEscape(root, ref) {
  if (!root || !ref) return { dispose() { } };
  const entry = { root, ref };
  holds.push(entry);
  listen();
  return {
    dispose() {
      const index = holds.indexOf(entry);
      if (index >= 0) holds.splice(index, 1);
      unlisten();
    }
  };
}
