# API Interface Defs

---

## C# HAL API (:3100)

### GET /api/health
Return: `{ ok: bool, timestamp: long }`

### GET /api/telemetry
全量遥测：CPU/GPU 温度/占用/频率、风扇 RPM、内存、硬盘、系统开关状态。

### POST /api/control
硬件控制。Body: `{ target: string, value: int }`

| target | value | 功能 |
|--------|-------|------|
| `kb_light` | 0-3 | 键盘背光亮度 |
| `fn_lock` | 0/1 | Fn 锁 |
| `num_lock` | 0/1 | 数字键锁定 |
| `caps_lock` | 0/1 | 大写键锁定 |
| `touchpad_lock` | 0/1 | 触摸板锁定 |
| `power_plan` | GUID/int | 电源计划 |
| `thermal_mode` | 0-3 | 散热模式 (0=均衡,1=野兽,2=安静,3=斗战) |
| `igpu_only` | 0/1 | 仅集显模式 |
| `gpu_mode` | 0-2 | GPU 模式 (0=混合,1=集显,2=独显, AppBridge) |

### GET /api/discover
硬件探测。Return: `{ available, ecBase, driverLoaded, touchpad }`

### GET /api/app-cmd
AppBridge 通用命令通道。Query: `cmd=string`
Return: `{ cmd, result }`

### GET /api/app-status
AppBridge 健康检查。Return: `{ available: bool }`

### GET /api/ec-scan
EC 寄存器批量扫描。Query: `offset, length`
Return: `{ offset, length, hex }`

### POST /api/smu/set
SMU 参数下发 (ryzenadj.exe 子进程 + WinRing0)。Body: `{ parameter: string, valueM: int }`

| parameter | valueM | 功能 |
|-----------|--------|------|
| `stapm_limit` / `power_limit` | mW | CPU 长时/快速/慢速功耗墙 (stapm+fast+slow 三限同设) |
| `tctl_temp` / `temp_limit` | °C | CPU 温度墙 |
Return: `{ ok: bool, rc: int }`(rc=0 成功)

### GET /api/smu/probe
SMU 连通性探测 (ryzenadj.exe 子进程)。Return: `{ ok: bool, source: "ryzenadj" }`

### POST /api/fan/set-target
风扇目标转速下发。Body: `{ largeRpm: int, smallRpm: int }`
- 写入 EC 寄存器 0x5F(大扇)/0x5B(小扇)，值 = RPM/100
- 限幅：大扇 0-4400 RPM，小扇 0-8200 RPM
- Return: `{ ok: bool, largeRpm: int, smallRpm: int }`

### POST /api/fan/restore
恢复固件风扇曲线。调用 WMI `MaxFanSpeedSwitch(0)`。
- Body: 无
- Return: `{ ok: bool }`

### POST /api/wmi/cmd
通用 WMI 原始字节命令通道（`root\WMI` MiInterface）。
- Body: `{ method: int, value?: int }`
  - `method` = 方法号（如 8=SystemPerMode, 9=GPUMode, 22=CPUThermometer）
  - `value` = 可选（不传则为读取，传则为设置）
- Return: `{ ok: bool, method: int, value: int?, response: string, outValue: int? }`
- maxRpm: 大扇=4400, 小扇=8200
- Return: `{ ok: bool, largeRpm: int, smallRpm: int }`
- ❌ LLT 参考，寄存器写入回读成功但风扇物理无响应——本机模具不适用此地址

### WS /ws
WebSocket 实时遥测推送。发送 JSON 对象，28 字段：

```
cpuUsage, cpuTemp, cpuFreq, cpuCores,
gpuUsage, gpuTemp, gpuFreq, gpuVram, gpuVramUsed,
memoryUsage, memoryTotalGB, memoryFreq,
diskUsage, diskTotalGB, diskFreeGB,
fanLargeRpm, fanSmallRpm, fanLargeMax, fanSmallMax,
kbBrightness, fnLock, numLock, capsLock, thermalMode, powerPlan,
touchpadLock, igpuOnly, timestamp
```

### GET /debug
内嵌 HTML Debug 面板（按钮/滑块测试所有功能，WS 遥测可视化）。

---

## ~~Node.js API (:3099)~~ **已废弃 — 端点已全部迁移至 C#**

### ~~GET /api/telemetry~~ **已废弃** — C# HAL 已覆盖全部遥测字段

### GET /api/ryzenadj/info
SMU 状态读取（通过 libryzenadj.js 封装，子进程 ryzenadj -i）。Return: `{ ok, source: "ryzenadj", data: { [field]: { value, unit } } }`

### GET /api/smu/api-type
返回当前 SMU 访问通道。Return: `"subprocess"`

### POST /api/uxtu/apply
SMU 参数下发。Body 兼容两种格式：

```json
// 前端格式
{ "profile": "custom", "params": { "cpuLongPptW": 65, "cpuTempLimitC": 90, ... } }
// 旧格式
{ "limits": { "cpu": { "pptLimitW": 65 }, "gpu": { "pptLimitW": 115 } } }
```

### POST /api/system/settings
> **废弃**：此功能基于 Lenovo Legion LLT 项目的 WMI 方案。本机为宝龙达 OEM 模具，Legion 专用的 `LENOVO_*` WMI 类全部不存在。
> **替代方案**：C# HAL `POST /api/control`（EC 直写）+ AppBridge（官方 DLL 反射调用）。
Body: `{ key: string, value: any }`

### POST /api/fan/full-speed
> **废弃**：同样基于 LLT/WMI，此硬件不支持。
> **替代方案**：风扇转速控制走 EC IO 寄存器 0xB2(大扇)/0xB3(小扇)，`val = round(rpm / maxRpm × 255)`。待实现 `POST /api/fan/set-target`。

### GET /api/fan/set-target
*待实现* — 安静性能模式。Body: `{ fan: "large"|"small", rpm: number }`
写入 EC 寄存器 0xB2(大扇)/0xB3(小扇)，公式 `val = round(rpm / maxRpm * 255)`

### GET|POST /api/custom-params
自定义参数持久化。JSON: `{ cpuLongPptW, gpuPptLimitW, fanLargeRpmTarget, fanSmallRpmTarget }`

### GET|POST /api/ui-state
仪表盘状态持久化。JSON: `{ cardOrder: string[], hiddenCards: string[] }`

### GET|POST /api/default-config
默认配置读写。

### GET /api/discover
> **废弃**：WMI 类探测（仅用于调试，此硬件基本不可用）。
> **替代方案**：C# HAL `GET /api/discover` 返回驱动状态。

### GET /debug
内嵌 HTML Debug 面板（SMU 信息、风扇控制、参数管理）。

---

## C# HAL API — 新增迁移端点

### GET /api/ryzenadj/info
SMU 探测（通过 SmuController）。Return: { ok: bool, data: { probeResult: bool, source: "inpoutx64" } }

### POST /api/uxtu/apply
SMU 参数下发（转发到 SmuController）。兼容两种格式：
`json
{ "params": { "cpuLongPptW": 65, "cpuTempLimitC": 90, "gpuPptLimitW": 115 } }
{ "limits": { "cpu": { "pptLimitW": 65, "tempLimitC": 90 }, "gpu": { "pptLimitW": 115 } } }
`

### GET | POST /api/custom-params
自定义参数 JSON 持久化（共享 Node.js 遗留的 config/ 目录）。

### GET | POST /api/ui-state
仪表盘状态持久化（卡片排序 + 隐藏状态）。

### GET | POST /api/default-config
默认配置持久化。

### POST /api/system/settings
**废弃 stub** — 返回 "此端点已废弃，请使用 /api/control"

### POST /api/fan/full-speed
**废弃 stub** — 返回 "此端点已废弃，请使用 /api/fan/set-target 手动控制风扇"

---

## Vite Proxy


> **权威来源**：`vite.config.js`。架构总览见 [dev-architecture.md](dev-architecture.md#Vite-代理规则)。

| 前端路径 | 目标 | 说明 |
|----------|------|------|
| `/api/telemetry` | :3100 | C# HAL 遥测 |
| `/api/control` | :3100 | C# HAL 硬件控制 |
| `/api/health` | :3100 | C# HAL 健康检查 |
| `/api/discover` | :3100 | C# HAL 硬件探测 |
| `/ws` | ws://:3100 | C# HAL WebSocket（WS直连绕过代理） |
| `/api/uxtu` | :3099 | Node.js SMU 参数 |
| `/api/system` | :3099 | Node.js 系统设置 |
| `/api/ryzenadj` | :3099 | Node.js RyzenAdj |
| `/api/fan` | :3099 | Node.js 风扇 |
| `/api/custom-params` | :3099 | Node.js 配置持久化 |
| `/api/ui-state` | :3099 | Node.js UI 状态 |
| `/api/default-config` | :3099 | Node.js 默认配置 |

---
> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
