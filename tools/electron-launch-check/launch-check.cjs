// Launches a packaged desktop app that HCG Executable Maker built from a web project, with NO development server, and
// checks what a user would see: the page renders from inside the app (app://bundle), a client-side route survives a
// reload, the window keeps context isolation / sandbox / webSecurity with no Node integration, and paths outside the
// bundle are not served.
//
// Usage: node launch-check.cjs <app executable> <screenshot dir> <home text> <deep route> <deep route text> [--no-sandbox]
// Needs playwright-core (resolved from NODE_PATH). --no-sandbox is for the test harness only (Linux containers that run
// as root, where Chromium refuses its sandbox); the app itself never disables the renderer sandbox.
const { _electron } = require('playwright-core');
const [exe, shots, homeText, deepRoute, deepText, ...flags] = process.argv.slice(2);
const fail = (m) => { console.error('FAIL: ' + m); process.exitCode = 1; };
const pass = (m) => console.log('PASS: ' + m);

(async () => {
  const app = await _electron.launch({ executablePath: exe, args: flags.includes('--no-sandbox') ? ['--no-sandbox'] : [], timeout: 60000 });
  try {
    const win = await app.firstWindow();
    const failed = [];
    win.on('requestfailed', (r) => failed.push(r.url()));
    await win.waitForSelector('#root *', { timeout: 20000 });
    const url = win.url();
    url.startsWith('app://bundle/') ? pass(`loaded from inside the app: ${url}`) : fail(`unexpected URL ${url}`);
    const text = await win.locator('body').innerText();
    text.includes(homeText) ? pass(`home page shows "${homeText}"`) : fail(`home page text: ${text.slice(0, 200)}`);
    await win.screenshot({ path: `${shots}/launch-01-home.png` });

    const prefs = await app.evaluate(({ BrowserWindow }) => {
      const p = BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences();
      return { contextIsolation: p.contextIsolation, sandbox: p.sandbox, nodeIntegration: p.nodeIntegration, webSecurity: p.webSecurity };
    });
    prefs.contextIsolation && prefs.sandbox && prefs.webSecurity && !prefs.nodeIntegration
      ? pass(`window security: ${JSON.stringify(prefs)}`) : fail(`window security: ${JSON.stringify(prefs)}`);
    const nodeVisible = await win.evaluate(() => typeof require !== 'undefined' || typeof process !== 'undefined');
    nodeVisible ? fail('Node.js is visible to the page') : pass('no Node.js in the page');

    await win.evaluate((r) => { history.pushState({}, '', r); dispatchEvent(new PopStateEvent('popstate')); }, deepRoute);
    await win.waitForTimeout(500);
    await win.reload();
    await win.waitForSelector('#root *', { timeout: 20000 });
    const deep = await win.locator('body').innerText();
    deep.includes(deepText) ? pass(`route ${deepRoute} after a reload shows "${deepText}" (${win.url()})`) : fail(`route ${deepRoute} after reload: ${deep.slice(0, 200)}`);
    await win.screenshot({ path: `${shots}/launch-02-deep-route.png` });

    const probes = await win.evaluate(async () => {
      const out = {};
      for (const u of ['/../../../../etc/passwd', '/%2e%2e/%2e%2e/%2e%2e/etc/hosts', '/..%2f..%2f..%2fetc%2fhosts', '/assets/..%2f..%2f..%2f..%2fetc%2fhosts']) {
        try { const t = await (await fetch(u)).text(); out[u] = /root:|localhost/.test(t) && !t.includes('<div id="root">') ? 'LEAK' : 'ok'; }
        catch { out[u] = 'ok'; }
      }
      return out;
    });
    Object.values(probes).every((v) => v === 'ok') ? pass('paths outside the bundle are not served') : fail(`path probes: ${JSON.stringify(probes)}`);
    failed.length === 0 ? pass('no failed requests') : fail(`failed requests: ${failed.join(', ')}`);
  } finally {
    await app.close();
  }
})().catch((e) => { fail(String(e)); });
