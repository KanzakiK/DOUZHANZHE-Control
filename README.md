# Douzhanzhe Console

斗战者控制台

联想 Legion N176 2025 的硬件解放工具。

## 功能

- 遥测 - CPU/GPU 占用率、温度、频率、显存、风扇实时监控
- CPU - 功耗墙、温度墙、睿频控制、核心限制、电压调节
- GPU - 功耗墙、温度墙、超频、频率锁定
- 风扇 - 目标转速、全速模式
- 键盘背光 - 0-3 级亮度（物理内存直接访问）
- 模式 - 静音/办公/游戏/狂野/自定义 一键切换
- 主题 - 4 套皮肤

## 技术栈

前端：React 19 + Vite 8 + Tailwind CSS 3 + @dnd-kit
后端：Node.js + Express 5 + WebSocket
SMU：RyzenAdj (LGPL-3.0)
EC/硬件：inpoutx64 (MIT)

## 快速开始

npm install
npm run build
npm start

需要管理员权限运行后端。

## 参考

LLT - github.com/BartoszCichecki/LenovoLegionToolkit
UXTU - github.com/JamesCJ60/Universal-x86-Tuning-Utility
RyzenAdj - github.com/FlyGoat/RyzenAdj
inpoutx64 - highrez.co.uk/downloads/inpout32

## 许可证

GNU General Public License v3.0
