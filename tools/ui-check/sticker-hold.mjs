// Checks js/stickers.js holdGesture — the long-press of the sticker and GIF panels — without a browser, on a fake clock:
// one touch opens one preview whether our 450 ms timer or Android's own long-press (a contextmenu event, before or after
// it, also while the finger is still down) comes first; the click that ends a hold is swallowed (it would land on the
// preview that just opened); taps, drags and later clicks pass.
// Usage: node tools/ui-check/sticker-hold.mjs (prints each case; exit code 1 when one fails).
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { holdGesture } = await import(pathToFileURL(resolve(repo, 'src/Client/SkyMessage.Client.UI/wwwroot/js/stickers.js')).href);

function scenario(steps) {
  let time = 0;
  let nextId = 1;
  const timers = new Map();
  const clock = {
    now: () => time,
    set: (fn, ms) => { const id = nextId++; timers.set(id, { fn, at: time + ms }); return id; },
    clear: (id) => { timers.delete(id); },
  };
  const holds = [];
  const swallowed = [];
  const g = holdGesture(clock, (key) => holds.push(key));
  const advance = (to) => {
    while (true) {
      const due = [...timers.entries()].filter(([, t]) => t.at <= to).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      timers.delete(due[0]);
      time = due[1].at;
      due[1].fn();
    }
    time = to;
  };
  for (const [at, action, ...args] of steps) {
    advance(at);
    if (action === 'click') swallowed.push(g.click());
    else g[action](...args);
  }
  advance(time + 5000);
  return { holds, swallowed };
}

const touch = (key = 'a') => ['down', key, 100, 100, 1];
const cases = [
  ['A hold fires once after 450 ms', scenario([[0, ...touch()], [1000, 'up']]).holds, ['a']],
  ['Android long-press after our timer, finger still down: no second preview',
    scenario([[0, ...touch()], [500, 'context', 'a'], [700, 'up'], [750, 'click']]).holds, ['a']],
  ['Android long-press with a long accessibility delay (1.5 s), finger still down: no second preview',
    scenario([[0, ...touch()], [1500, 'context', 'a'], [1600, 'up']]).holds, ['a']],
  ['Android long-press right after the release: no second preview',
    scenario([[0, ...touch()], [480, 'up'], [600, 'context', 'a']]).holds, ['a']],
  ['Android long-press before our timer: fired at once, the timer does not fire again',
    scenario([[0, ...touch()], [400, 'context', 'a'], [900, 'up']]).holds, ['a']],
  ['The click that ends a hold is swallowed',
    scenario([[0, ...touch()], [600, 'up'], [620, 'click']]).swallowed, [true]],
  ['The click of a hold ended by Android is swallowed too',
    scenario([[0, ...touch()], [400, 'context', 'a'], [700, 'up'], [710, 'click']]).swallowed, [true]],
  ['A tap is no hold, and its click goes through',
    (() => { const r = scenario([[0, ...touch()], [120, 'up'], [130, 'click']]); return [r.holds.length, ...r.swallowed]; })(), [0, false]],
  ['Moving more than 10 px cancels the hold',
    scenario([[0, ...touch()], [200, 'move', 100, 120, 1], [700, 'up']]).holds, []],
  ['A small tremble does not',
    scenario([[0, ...touch()], [200, 'move', 104, 106, 1], [700, 'up']]).holds, ['a']],
  ['Scrolling (pointercancel) cancels the hold',
    scenario([[0, ...touch()], [200, 'cancel'], [700, 'up']]).holds, []],
  ['A right-click fires at once',
    scenario([[0, 'context', 'b']]).holds, ['b']],
  ['A click long after a hold goes through',
    scenario([[0, ...touch()], [600, 'up'], [3000, 'click']]).swallowed, [false]],
  ['Only one click is swallowed',
    scenario([[0, ...touch()], [600, 'up'], [620, 'click'], [700, 'click']]).swallowed, [true, false]],
  ['A second touch after a hold is a new gesture',
    scenario([[0, ...touch('a')], [600, 'up'], [2000, ...touch('b')], [2600, 'up']]).holds, ['a', 'b']],
];

let failed = 0;
for (const [name, got, want] of cases) {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  if (!ok) failed++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}: ${JSON.stringify(got)}${ok ? '' : ` (expected ${JSON.stringify(want)})`}`);
}
process.exit(failed ? 1 : 0);
