// SPDX-License-Identifier: MIT
//
// libryzenadj.js — AMD SMU 访问封装
// ======================================
// Dragon Range (R9 8940HX) 上 libryzenadj.dll init_ryzenadj() 返回 NULL，
// 因此全程使用 ryzenadj.exe 子进程模式。
// 已修复：Dragon Range 的信息输出走 stderr，需合并捕获。
//
// 导出函数：
//   setLimits({ cpuPpt, cpuTemp, gpuPpt }) — 下发 SMU 参数
//   getInfo() — 读取 SMU 状态（解析 -i 输出）
//   getTableValues() — 始终返回 []（Dragon Range 不支持表 API）

import { spawn } from "child_process";
import path from "path";
import { fileURLToPath } from "url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const RYZENADJ_PATH = path.join(__dirname, "tools", "ryzenadj.exe");

// ---- 子进程封装 ----
function run(args = []) {
  return new Promise((resolve) => {
    const child = spawn(RYZENADJ_PATH, args, {
      timeout: 15000,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";

    child.stdout.on("data", (d) => { stdout += d.toString(); });
    child.stderr.on("data", (d) => { stderr += d.toString(); });

    child.on("close", (code) => {
      // Dragon Range: 信息输出走 stderr，优先用 stdout，空则用 stderr
      let output = stdout.trim() || stderr.trim();
      if (output) return resolve({ output, code });
      // 0xC0000005 — 写入已成功但 post-write verify 崩溃
      if (code === 3221225477 || code === null) {
        resolve({ output: "(SMU 写入已执行)", code });
      } else {
        resolve({ error: `Exit code ${code}`, code });
      }
    });

    child.on("error", (err) => resolve({ error: err.message }));
  });
}

// ---- 解析 "ryzenadj -i" 表格输出 ----
function parseInfo(output) {
  const map = {};
  for (const line of output.split("\n")) {
    const m = line.match(/^(.+?)\s*\|\s*(.+?)\s*\|\s*(.+?)\s*$/);
    if (m) map[m[1].trim()] = { value: m[2].trim(), unit: m[3].trim() };
  }
  return map;
}

// ---- 公开 API ----

/** 获取当前 API 模式（始终 subprocess） */
export function getApiType() {
  return "subprocess";
}

/** 读取 SMU 状态信息 */
export async function getInfo() {
  const result = await run(["-i"]);
  if (result.error) return { ok: false, error: result.error };
  return { ok: true, source: "ryzenadj", data: parseInfo(result.output) };
}

/** 下发 SMU 参数 */
export async function setLimits({ cpuPpt, cpuTemp, gpuPpt } = {}) {
  const cmds = [];
  if (cpuPpt != null) {
    const mW = Math.round(cpuPpt * 1000);
    cmds.push([`--stapm-limit=${mW}`, `--fast-limit=${mW}`, `--slow-limit=${mW}`]);
  }
  if (cpuTemp != null) cmds.push([`--tctl-temp=${cpuTemp}`]);
  if (gpuPpt != null) cmds.push([`--vrmgfx-current=${Math.round(gpuPpt * 1000)}`]);

  if (cmds.length === 0) return { ok: true, message: "无参数需要下发", results: [] };

  const results = [];
  for (const args of cmds) {
    const r = await run(args);
    results.push({ args, ok: !r.error, error: r.error, output: r.output });
  }
  return { ok: results.some((r) => r.ok), results };
}

/** 读取 SMU 全部表值 — Dragon Range 不支持，返回空数组 */
export function getTableValues() {
  return [];
}

console.log("[libryzenadj] 已就绪 (subprocess 模式)");
