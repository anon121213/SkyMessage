// Screenshots of the web client in headless Chrome, emulating a phone (touch, DPR 2), for checking UI work without
// a device. Usage:
//   node tools/ui-shot/shot.mjs --base http://127.0.0.1:8080 --out <dir> --steps <steps.json>
//        [--user artur_dev] [--width 390] [--height 844] [--theme light|dark] [--port 9301]
// steps.json: an array run in order, for example
//   [{"go": "/chat/<id>"}, {"wait": 1500}, {"tap": ".composer__record"}, {"shot": "after-tap"},
//    {"swipe": {"selector": ".chat", "dx": 220, "dy": 0, "ms": 260}}, {"eval": "document.title"},
//    {"type": "hello"}, {"key": "Enter"}, {"waitFor": ".msg"}]
// "swipe" drives real touch events (pointer type "touch") from the element's center (or "x"/"y"), "hold" keeps the
// finger down (ms) before moving, "tap" accepts "x"/"y" instead of a selector. Logs in through the UI with the dev
// account from tools/e2e/dev-accounts.json unless --user none. Screenshots: <out>/<n>-<name>.png.
import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, v, i, all) => (v.startsWith('--') ? [...acc, [v.slice(2), all[i + 1]]] : acc), []));
const base = (args.base || 'http://127.0.0.1:8080').replace(/\/$/, '');
const out = resolve(args.out || 'ui-shots');
const width = Number(args.width || 390), height = Number(args.height || 844);
const port = Number(args.port || 9300 + Math.floor(Math.random() * 600));
const steps = args.steps ? JSON.parse(readFileSync(args.steps, 'utf8')) : [];
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
mkdirSync(out, { recursive: true });

const chromePath = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const profile = mkdtempSync(join(tmpdir(), 'ui-shot-'));
const chrome = spawn(chromePath, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  `--window-size=${width},${height}`, '--hide-scrollbars', '--no-first-run', '--no-default-browser-check',
  '--autoplay-policy=no-user-gesture-required', '--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', 'about:blank'],
  { stdio: 'ignore' });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const cleanup = () => { try { chrome.kill('SIGKILL'); } catch { } try { rmSync(profile, { recursive: true, force: true }); } catch { } };
process.on('exit', cleanup);

let target;
for (let i = 0; i < 100 && !target; i++) {
  try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find((t) => t.type === 'page'); } catch { await sleep(100); }
}
if (!target) { console.error('Chrome did not start'); process.exit(2); }
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r, { once: true }));
let seq = 0;
const pending = new Map();
ws.addEventListener('message', (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
  if (m.method === 'Runtime.exceptionThrown') console.log('  [page error]', m.params.exceptionDetails?.exception?.description?.split('\n')[0]);
  if (m.method === 'Runtime.consoleAPICalled' && m.params.type === 'error') console.log('  [console.error]', m.params.args.map((a) => a.value ?? a.description).join(' ').slice(0, 300));
});
const send = (method, params = {}) => new Promise((r) => { const id = ++seq; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
const evaluate = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(r.result.exceptionDetails.exception?.description || 'evaluate failed');
  return r.result?.result?.value;
};
const center = (selector) => evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) return null;
  const b = el.getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; })()`);
const waitFor = async (selector, ms = 15000) => {
  for (const end = Date.now() + ms; Date.now() < end; await sleep(150)) if (await evaluate(`!!document.querySelector(${JSON.stringify(selector)})`)) return true;
  throw new Error(`timed out waiting for ${selector}`);
};
const touch = (type, x, y) => send('Input.dispatchTouchEvent', { type, touchPoints: type === 'touchEnd' ? [] : [{ x, y, id: 1 }] });

await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 2, mobile: true });
await send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
if (args.theme) await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: args.theme }] });
const go = async (path) => { await send('Page.navigate', { url: base + path }); await sleep(600); };

if ((args.user || 'artur_dev') !== 'none') {
  const account = JSON.parse(readFileSync(join(repo, 'tools/e2e/dev-accounts.json'), 'utf8')).accounts.find((a) => a.username === (args.user || 'artur_dev'));
  await go('/login');
  await waitFor('input[autocomplete="username"]', 30000);
  await evaluate(`document.querySelector('input[autocomplete="username"]').focus()`);
  await send('Input.insertText', { text: account.username });
  await evaluate(`document.querySelector('input[type="password"]').focus()`);
  await send('Input.insertText', { text: account.password });
  await sleep(300);
  await evaluate(`document.querySelector('form button[type="submit"]').click()`);
  for (let i = 0; i < 100 && (await evaluate('location.pathname')).startsWith('/login'); i++) await sleep(150);
  await sleep(1200);
}

let shotNo = 0;
for (const step of steps) {
  try {
    if (step.go) await go(step.go);
    else if (step.wait) await sleep(step.wait);
    else if (step.waitFor) await waitFor(step.waitFor, step.ms);
    else if (step.eval) console.log('  eval:', JSON.stringify(await evaluate(step.eval))?.slice(0, 500));
    else if (step.type) await send('Input.insertText', { text: step.type });
    else if (step.key) {
      for (const t of ['keyDown', 'keyUp']) await send('Input.dispatchKeyEvent', { type: t, key: step.key, code: step.key, windowsVirtualKeyCode: step.key === 'Enter' ? 13 : 0 });
    } else if (step.tap) {
      const p = typeof step.tap === 'string' ? await center(step.tap) : step.tap;
      if (!p) throw new Error(`no element ${step.tap}`);
      await touch('touchStart', p.x, p.y); await sleep(step.ms || 60); await touch('touchEnd', p.x, p.y);
      if (step.double) { await sleep(90); await touch('touchStart', p.x, p.y); await sleep(60); await touch('touchEnd', p.x, p.y); }
    } else if (step.swipe) {
      const s = step.swipe;
      const p = s.selector ? await center(s.selector) : { x: s.x, y: s.y };
      if (!p) throw new Error(`no element ${s.selector}`);
      const x0 = s.x ?? p.x, y0 = s.y ?? p.y, n = Math.max(6, Math.round((s.ms || 250) / 16));
      await touch('touchStart', x0, y0);
      if (s.hold) await sleep(s.hold);
      for (let i = 1; i <= n; i++) { await touch('touchMove', x0 + (s.dx || 0) * i / n, y0 + (s.dy || 0) * i / n); await sleep((s.ms || 250) / n); }
      if (s.shotBeforeRelease) {
        const { data } = (await send('Page.captureScreenshot', { format: 'png' })).result;
        writeFileSync(join(out, `${String(++shotNo).padStart(2, '0')}-${s.shotBeforeRelease}.png`), Buffer.from(data, 'base64'));
      }
      await touch('touchEnd', x0 + (s.dx || 0), y0 + (s.dy || 0));
    } else if (step.shot) {
      await sleep(step.delay ?? 250);
      const { data } = (await send('Page.captureScreenshot', { format: 'png' })).result;
      const file = join(out, `${String(++shotNo).padStart(2, '0')}-${step.shot}.png`);
      writeFileSync(file, Buffer.from(data, 'base64'));
      console.log('  shot:', file);
    }
  } catch (err) {
    console.log(`  step ${JSON.stringify(step).slice(0, 120)} failed: ${err.message}`);
  }
}
ws.close();
cleanup();
process.exit(0);
