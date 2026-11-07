// Checks js/folders.js slotAt — the place a dragged folder tab takes — without a browser: a tab wider than its
// neighbours can be dropped first and last (held at either end of the strip it takes that end's place), a tab takes a
// neighbour's place once it covers half of it, and one that has not moved keeps its own.
// Usage: node tools/ui-check/folder-tab-slots.mjs (prints each case; exit code 1 when one fails).
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// folders.js imports app.js, which reads the document and listens to the window when it loads.
const noop = () => { };
globalThis.document = { documentElement: { dataset: {} }, addEventListener: noop };
globalThis.window = { addEventListener: noop };
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { slotAt } = await import(pathToFileURL(resolve(repo, 'src/Client/SkyMessage.Client.UI/wwwroot/js/folders.js')).href);

// The folders' tabs as QA measured them at 1280x800 ("All chats" does not move): Новые 78 | Контакты 101 | Группы 111 |
// Непрочитанные 178, 6 px apart.
const gap = 6;
const tabs = (widths) => {
  let left = 130;
  return widths.map((width) => { const item = { left, width }; left += width + gap; return item; });
};
const items = tabs([78, 101, 111, 178]);
// How far the drag can take a tab: its left edge to the first tab's, its right edge to the last tab's (layout()'s clamp).
const min = (from, list = items) => list[0].left - list[from].left;
const max = (from, list = items) => list[list.length - 1].left + list[list.length - 1].width - (list[from].left + list[from].width);
// The wide one first, a narrower one last.
const wideFirst = tabs([178, 78, 101, 111]);

const cases = [
  ['Непрочитанные held at the end takes the last place (Группы is narrower)', slotAt(wideFirst, 0, max(0, wideFirst)), 3],
  ['Контакты held at the start takes the first place (it is wider than Новые)', slotAt(items, 1, min(1)), 0],
  ['Непрочитанные held at the start takes the first place', slotAt(items, 3, min(3)), 0],
  ['Новые held at the end takes the last place (Непрочитанные is twice as wide)', slotAt(items, 0, max(0)), 3],
  ['Контакты held at the end takes the last place', slotAt(items, 1, max(1)), 3],
  ['A tab that has not moved keeps its place', slotAt(items, 2, 0), 2],
  ['Контакты covering less than half of Новые stays second', slotAt(items, 1, -(gap + 78 / 2) + 2), 1],
  ['Контакты covering half of Новые takes its place', slotAt(items, 1, -(gap + 78 / 2) - 2), 0],
  ['Группы covering half of Непрочитанные takes its place', slotAt(items, 2, gap + 178 / 2 + 2), 3],
  ['Группы covering less than half of Непрочитанные stays third', slotAt(items, 2, gap + 178 / 2 - 2), 2],
];

let failed = 0;
for (const [name, got, want] of cases) {
  const ok = got === want;
  if (!ok) failed++;
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}: ${got}${ok ? '' : ` (expected ${want})`}`);
}
process.exit(failed ? 1 : 0);
