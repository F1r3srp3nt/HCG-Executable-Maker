// Launches a packaged desktop app that HCG Executable Maker built from a web project, with NO development server, and
// checks what a user would see: the page renders from inside the app (app://bundle), a client-side route survives a
// reload, a lazy-loaded route loads its chunk and assets from the app (also after a reload), the window keeps context
// isolation / sandbox / webSecurity with no Node integration, pages carry the content security policy, links to other
// sites open outside the app (never in it), other schemes are refused, and paths outside the bundle are not served.
//
// Usage: node launch-check.cjs <app executable> <screenshot dir> <home text> <deep route> <deep route text>
//          [--lazy <route> <text>] [--links] [--no-sandbox] [--prefix name]
// Needs playwright-core (resolved from NODE_PATH). --no-sandbox is for the test harness only (Linux containers that run
// as root, where Chromium refuses its sandbox); the app itself never disables the renderer sandbox.
const { _electron } = require('playwright-core');
const [exe, shots, homeText, deepRoute, deepText, ...rest] = process.argv.slice(2);
const opt = (name, n = 1) => { const i = rest.indexOf(name); return i < 0 ? null : rest.slice(i + 1, i + 1 + n); };
const lazy = opt('--lazy', 2);
const prefix = (opt('--prefix') || ['launch'])[0];
const fail = (m) => { console.error('FAIL: ' + m); process.exitCode = 1; };
const pass = (m) => console.log('PASS: ' + m);

(async () => {
  const app = await _electron.launch({ executablePath: exe, args: rest.includes('--no-sandbox') ? ['--no-sandbox'] : [], timeout: 60000 });
  try {
    const win = await app.firstWindow();
    const failed = [];
    const requested = [];
    win.on('requestfailed', (r) => failed.push(`${r.url()} (${r.failure()?.errorText})`));
    win.on('request', (r) => requested.push(r.url()));
    await win.waitForSelector('#root *', { timeout: 20000 });
    const url = win.url();
    url.startsWith('app://bundle/') ? pass(`loaded from inside the app: ${url}`) : fail(`unexpected URL ${url}`);
    const text = await win.locator('body').innerText();
    text.includes(homeText) ? pass(`home page shows "${homeText}"`) : fail(`home page text: ${text.slice(0, 200)}`);
    await win.screenshot({ path: `${shots}/${prefix}-01-home.png` });

    const prefs = await app.evaluate(({ BrowserWindow }) => {
      const p = BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences();
      return { contextIsolation: p.contextIsolation, sandbox: p.sandbox, nodeIntegration: p.nodeIntegration, webSecurity: p.webSecurity };
    });
    prefs.contextIsolation && prefs.sandbox && prefs.webSecurity && !prefs.nodeIntegration
      ? pass(`window security: ${JSON.stringify(prefs)}`) : fail(`window security: ${JSON.stringify(prefs)}`);
    const nodeVisible = await win.evaluate(() => typeof require !== 'undefined' || typeof process !== 'undefined');
    nodeVisible ? fail('Node.js is visible to the page') : pass('no Node.js in the page');
    const csp = await win.evaluate(async () => (await fetch('/')).headers.get('content-security-policy'));
    csp && csp.includes("script-src 'self'") ? pass(`pages carry a content security policy: ${csp.slice(0, 80)}…`) : fail(`no content security policy on pages (${csp})`);

    await win.evaluate((r) => { history.pushState({}, '', r); dispatchEvent(new PopStateEvent('popstate')); }, deepRoute);
    await win.waitForTimeout(500);
    await win.reload();
    await win.waitForSelector('#root *', { timeout: 20000 });
    const deep = await win.locator('body').innerText();
    deep.includes(deepText) ? pass(`route ${deepRoute} after a reload shows "${deepText}" (${win.url()})`) : fail(`route ${deepRoute} after reload: ${deep.slice(0, 200)}`);
    await win.screenshot({ path: `${shots}/${prefix}-02-deep-route.png` });

    if (lazy) {
      const [lazyRoute, lazyText] = lazy;
      await win.goto('app://bundle/');
      await win.waitForSelector('#root *', { timeout: 20000 });
      const before = requested.length;
      await win.click(`a[href="${lazyRoute}"]`);
      await win.waitForFunction((t) => document.body.innerText.includes(t), lazyText, { timeout: 20000 });
      const chunks = requested.slice(before).filter((u) => /\.js(\?|$)/.test(u));
      chunks.length > 0 && chunks.every((u) => u.startsWith('app://bundle/'))
        ? pass(`lazy route ${lazyRoute} loaded its chunk from the app: ${chunks.map((u) => u.replace('app://bundle', '')).join(', ')}`)
        : fail(`lazy route ${lazyRoute}: chunks requested ${JSON.stringify(chunks)}`);
      const imageOk = await win.evaluate(() => [...document.images].every((i) => i.complete && i.naturalWidth > 0));
      imageOk ? pass('the lazy chunk\'s asset (image) loaded') : fail('an image of the lazy route did not load');
      await win.reload();
      await win.waitForFunction((t) => document.body.innerText.includes(t), lazyText, { timeout: 20000 });
      pass(`lazy route ${lazyRoute} survives a reload (deep link straight into a lazy chunk)`);
      await win.screenshot({ path: `${shots}/${prefix}-03-lazy-route.png` });
    }

    if (rest.includes('--links')) {
      await win.goto('app://bundle/');
      await win.waitForSelector('[data-testid="external"]', { timeout: 20000 });
      await app.evaluate(({ shell }) => { globalThis.__opened = []; shell.openExternal = async (u) => { globalThis.__opened.push(u); }; });
      await win.click('[data-testid="external"]');
      await win.waitForTimeout(800);
      const opened = await app.evaluate(() => globalThis.__opened);
      const windows = await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().length);
      opened.includes('https://example.com/field-guide') && windows === 1
        ? pass('an https link opens in the user\'s browser, not in the app (no second window)') : fail(`external link: opened ${JSON.stringify(opened)}, windows ${windows}`);
      const at = win.url();
      await win.click('[data-testid="file-link"]');
      await win.waitForTimeout(800);
      const after = await app.evaluate(() => globalThis.__opened);
      win.url() === at && after.length === opened.length ? pass('a file:// link is refused (the window stays in the app, nothing is opened)') : fail(`file link: url ${win.url()}, opened ${JSON.stringify(after)}`);
    }

    const probes = await win.evaluate(async () => {
      const out = {};
      for (const u of ['/../../../../etc/passwd', '/%2e%2e/%2e%2e/%2e%2e/etc/hosts', '/..%2f..%2f..%2fetc%2fhosts', '/assets/..%2f..%2f..%2f..%2fetc%2fhosts']) {
        try { const t = await (await fetch(u)).text(); out[u] = /root:|localhost/.test(t) && !t.includes('<div id="root">') ? 'LEAK' : 'ok'; }
        catch { out[u] = 'ok'; }
      }
      return out;
    });
    Object.values(probes).every((v) => v === 'ok') ? pass('paths outside the bundle are not served') : fail(`path probes: ${JSON.stringify(probes)}`);
    const appFailures = failed.filter((u) => u.startsWith('app://'));
    appFailures.length === 0 ? pass('every request for the app\'s own files succeeded') : fail(`failed requests: ${appFailures.join(', ')}`);
    const remote = failed.filter((u) => !u.startsWith('app://'));
    if (remote.length > 0) console.log(`NOTE: remote requests that failed (network/remote services, not the app's files): ${remote.join(', ')}`);
  } finally {
    await app.close();
  }
})().catch((e) => { fail(String(e)); });
