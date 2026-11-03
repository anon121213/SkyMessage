// Physical keyboards for the passcode pads (Features/Passcode/PasscodePad.razor): digits and Backspace go to the pad
// in front, so on a computer the lock screen and the passcode dialogs take typed digits without any text field (phones
// use our on-screen keypad; the system keyboard never opens). "In front" = the most recently attached pad that is
// connected, not inside [inert] and not covered by something else at its center (the call screen over the lock screen,
// the lock screen over a settings dialog). While the lock screen's pad is in front, Escape is held back so the app
// underneath does not close its dialogs (app.js treats Escape as Back).

const pads = [];
let listening = false;

function inFront(entry) {
  const el = entry.root;
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

/** Starts sending keys to the pad's OnKey(key) while it is in front; dispose() stops. */
export function attach(root, ref, lockScreen) {
  if (!root || !ref) return { dispose() { } };
  const entry = { root, ref, lockScreen: !!lockScreen };
  pads.push(entry);
  if (!listening) {
    window.addEventListener('keydown', onKeyDown, true);
    listening = true;
  }
  return {
    dispose() {
      const index = pads.indexOf(entry);
      if (index >= 0) pads.splice(index, 1);
      if (!pads.length && listening) {
        window.removeEventListener('keydown', onKeyDown, true);
        listening = false;
      }
    }
  };
}
