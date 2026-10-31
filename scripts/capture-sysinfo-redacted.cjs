// 截图系统信息页并将序列号脱敏（一次性工具）
const { chromium } = require("playwright-core");

(async () => {
  const browser = await chromium.launch({ channel: "msedge", headless: true });
  const page = await browser.newPage({
    viewport: { width: 1280, height: 820 },
    deviceScaleFactor: 2,
    colorScheme: "dark",
  });
  await page.addInitScript(() => {
    localStorage.setItem("dz_page", "sysinfo");
  });
  await page.goto("http://127.0.0.1:3101/", { waitUntil: "networkidle" });
  await page.evaluate(() => document.fonts?.ready);
  await page.waitForTimeout(2200);

  // 将序列号值替换为占位符（按行标签「序列号」找到同行右侧的值元素）
  const redacted = await page.evaluate(() => {
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
  console.log("redacted text nodes:", redacted);

  await page.screenshot({ path: "screenshots/sysinfo.png", fullPage: true });
  console.log("saved screenshots/sysinfo.png");
  await browser.close();
})();
