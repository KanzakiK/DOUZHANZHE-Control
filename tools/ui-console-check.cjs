const { chromium } = require('playwright-core');
const fs = require('fs');

const EDGE_PATH = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const BASE = 'http://127.0.0.1:3101';
const PAGES = ['dashboard', 'control', 'fan', 'platform', 'games', 'sysinfo', 'settings'];

(async () => {
  const browser = await chromium.launch({ executablePath: EDGE_PATH, headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 720 } });
  const page = await context.newPage();
  const logs = [];
  page.on('console', (msg) => {
    if (msg.type() === 'error') logs.push(`console ${msg.type()}: ${msg.text()}`);
  });
  page.on('pageerror', (err) => logs.push(`pageerror: ${err.message}`));
  page.on('requestfailed', (req) => logs.push(`requestfailed: ${req.url()} ${req.failure()?.errorText}`));

  for (const key of PAGES) {
    await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded' });
    await page.evaluate((p) => localStorage.setItem('dz_page', p), key);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1000);
    logs.push(`visited ${key}: url=${page.url()}`);
  }
  await browser.close();
  fs.mkdirSync('output', { recursive: true });
  fs.writeFileSync('output/ui-console-check.log', logs.join('\n') + '\n');
  console.log(logs.filter(l => !l.startsWith('visited ')).join('\n') || 'clean: 0 errors / 0 failed requests');
})();
