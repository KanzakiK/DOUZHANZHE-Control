const { chromium } = require('playwright-core');
const path = require('path');
const fs = require('fs');

const EDGE_PATH = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const OUT = 'output/ui-regression';

const VIEWPORTS = [
  { name: '1280x720', width: 1280, height: 720 },
  { name: '1600x900', width: 1600, height: 900 },
  { name: '1920x1080', width: 1920, height: 1080 },
  { name: '2560x1440', width: 2560, height: 1440 },
  { name: '3840x2160', width: 3840, height: 2160 },
];

const PAGES = [
  { key: 'dashboard', label: '仪表盘' },
  { key: 'control', label: '控制面板' },
  { key: 'fan', label: '风扇控制' },
  { key: 'platform', label: '平台控制' },
  { key: 'games', label: '游戏' },
  { key: 'sysinfo', label: '系统信息' },
  { key: 'settings', label: '设置' },
];

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch({ executablePath: EDGE_PATH, headless: true });
  for (const vp of VIEWPORTS) {
    const ctx = await browser.newContext({ viewport: { width: vp.width, height: vp.height } });
    const page = await ctx.newPage();
    await page.goto('http://127.0.0.1:3101/', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1500);
    for (const { key, label } of PAGES) {
      try {
        await page.getByRole('button', { name: label }).first().click({ timeout: 2000 });
      } catch {
        await page.evaluate((p) => localStorage.setItem('dz_page', p), key);
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForTimeout(600);
      }
      await page.waitForTimeout(400);
      await page.screenshot({ path: path.join(OUT, `${vp.name}-${key}.png`), fullPage: true });
    }
    await ctx.close();
  }
  await browser.close();
})();
