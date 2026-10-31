// 全量页面截图：隐藏权限横幅（非提权 dev 实例特有，正式版管理员运行不显示）+ 系统信息页序列号脱敏
const { chromium } = require("playwright-core");

const BASE = process.env.BASE_URL || "http://127.0.0.1:3101";
const TABS = [
  { key: "dashboard", name: "dashboard" },
  { key: "control", name: "control" },
  { key: "fan", name: "fan" },
  { key: "games", name: "games" },
  { key: "platform", name: "platform" },
  { key: "sysinfo", name: "sysinfo" },
  { key: "settings", name: "settings" },
];
const HIDE_CSS = ".admin-banner,.fan-alert{display:none!important}";

(async () => {
  const browser = await chromium.launch({ channel: "msedge", headless: true });
  for (const tab of TABS) {
    const page = await browser.newPage({
      viewport: { width: 1280, height: 820 },
      deviceScaleFactor: 2,
      colorScheme: "dark",
    });
    await page.addInitScript((k) => localStorage.setItem("dz_page", k), tab.key);
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await page.addStyleTag({ content: HIDE_CSS });
    await page.evaluate(() => document.fonts?.ready);
    await page.waitForTimeout(2200);

    if (tab.key === "sysinfo") {
      // 序列号脱敏：按行标签「序列号」找到同行右侧的值文本节点替换为占位符
      const n = await page.evaluate(() => {
        let count = 0;
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
        const hits = [];
        while (walker.nextNode()) {
          const t = walker.currentNode;
          if (/^[A-Z0-9]{16,24}$/.test(t.textContent.trim())) hits.push(t);
        }
        for (const t of hits) {
          t.textContent = "████████████████████";
          count++;
        }
        return count;
      });
      console.log(`[${tab.name}] serial redacted: ${n}`);
    }

    await page.screenshot({ path: `screenshots/${tab.name}.png`, fullPage: true });
    console.log(`saved screenshots/${tab.name}.png`);
    await page.close();
  }
  await browser.close();
  console.log("ALL DONE");
})();
