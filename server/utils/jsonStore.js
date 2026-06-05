// SPDX-License-Identifier: MIT
//
// jsonStore — 原子写入 + 熔断自愈的 JSON 持久化工具
// ====================================================
// 所有 JSON 落盘操作都必须经过此模块，禁止直接 fs.writeFileSync。
//
// 写入策略: Write-and-Rename（先写 .tmp 再 rename，防止写中断半截文件）
// 读取策略: try-catch 熔断，JSON 损坏时自动重置为骨架，绝不崩溃
//
import fs from "fs";
import path from "path";

/**
 * 原子写入 JSON 文件。
 * 1. 写入 filePath.tmp
 * 2. fs.renameSync(filePath.tmp -> filePath) — 操作系统级原子替换
 */
export function atomicWrite(filePath, data) {
  const tmpPath = filePath + ".tmp";
  fs.writeFileSync(tmpPath, JSON.stringify(data, null, 2), "utf-8");
  fs.renameSync(tmpPath, filePath);
}

/**
 * 安全读取 JSON 文件（熔断自愈）。
 * - 文件不存在 -> 返回骨架
 * - JSON 损坏/非法 -> 强制重置为骨架（atomicWrite 覆写），绝不抛异常
 *
 * @param {string} filePath
 * @param {object} skeleton  — 默认骨架，JSON 损坏时写回磁盘
 * @returns {object}
 */
export function safeRead(filePath, skeleton) {
  try {
    if (!fs.existsSync(filePath)) return skeleton;
    const raw = fs.readFileSync(filePath, "utf-8");
    const parsed = JSON.parse(raw);
    // 确保是纯对象（防普通字符串或数组污染）
    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed))
      throw new Error("JSON 根不是纯对象");
    return parsed;
  } catch (err) {
    console.warn("[jsonStore] JSON 损坏，重置:", err.message);
    atomicWrite(filePath, skeleton);
    return skeleton;
  }
}
