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

### GET /api/fan/status
风扇状态查询（WMI Bellator GET）。
Return: `{ ok: bool, manualEnabled: bool, largeRpmTarget: int, smallRpmTarget: int }`
- manualEnabled: ⚠️ 本模具 WMI GET 不回写开关状态，始终为 false
- largeRpmTarget / smallRpmTarget: 固件当前认的目标转速（RPM）

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
SMU 参数下发 (SmuController → ryzenadj.exe 子进程 + WinRing0)。Body: `{ parameter: string, valueM: int }`

| parameter | valueM | 功能 |
|-----------|--------|------|
| `stapm_limit` / `power_limit` | mW | CPU 长时/快速/慢速功耗墙 (stapm+fast+slow 三限同设) |
| `tctl_temp` / `temp_limit` | °C | CPU 温度墙 |
Return: `{ ok: bool, rc: int }`(rc=0 成功)

### GET /api/smu/probe
SMU 连通性探测 (SmuController → ryzenadj.exe 子进程)。Return: `{ ok: bool, source: "ryzenadj" }`

### POST /api/fan/set-target
风扇目标转速下发（WMI MiInterface MaxFanSpeed 协议）。Body: `{ largeRpm: int, smallRpm: int }`
- 通过 WMI MaxFanSwitch(20) 启用手动风扇模式，再调用 MaxFanSpeed(21) 设转速
- 协议：`data[4]=FanType(0=大扇/1=小扇)`, `data[5]=RPM/100`
- 限幅：大扇 0-4400 RPM，小扇 0-8200 RPM
- Return: `{ ok: bool }`
- **注意**：受散热模式区间限制（安静1900-2900, 均衡2600-3500, 野兽3200-3800, 斗战4000-4400），超出下限的值会被 EC 截断

### POST /api/fan/restore
恢复固件风扇曲线。调用 WMI MaxFanSwitch(20, FanType=0, enable=0)。
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

---
> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
