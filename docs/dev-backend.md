# 后端架构

[TOC]

## 服务部署

| 服务 | 端口 | 技术 | 职责 |
|------|------|------|------|
| C# HAL API | :3100 | .NET 8 Minimal API | 遥测、硬件控制、WebSocket、SMU、Debug |
| Node.js | :3099 | Express 5 | (可选) UI 配置 JSON 持久化 |

### Vite 代理分流

> 详细代理规则表见 [dev-architecture.md#Vite-代理规则](dev-architecture.md)。
> 完整配置见 `vite.config.js`。
>
> 前端的 WebSocket 直连 `ws://127.0.0.1:3100/ws`（绕过 Vite 代理）。

---

## C# HAL 三层架构

```
server/api/Program.cs                     # WebConsoleAPI — HTTP 端点
  ↓ 依赖注入
server/hal/HardwareAbstractionLayer.cs    # 硬件映射层 — 语义化属性
  ↓
server/hal/DriverBridge.cs                # 驱动桥接层 — inpoutx64 P/Invoke
  ↓
inpoutx64.sys + inpoutx64.dll             # 内核驱动 (MIT)
```

### 1. DriverBridge (server/hal/DriverBridge.cs)

inpoutx64.dll 的线程安全单例封装。项目引用：`Douzhanzhe.HAL.csproj` (.NET 8 classlib, AllowUnsafeBlocks)。

| 方法 | 说明 |
|------|------|
| `ReadPhys(addr)` | 读物理内存字节 |
| `WritePhys(addr, val)` | 写物理内存字节（优先 SetPhysLong, 兜底 MapPhysToLin） |
| `WriteBit(addr, bit, set)` | 物理内存位操作 |
| `ReadWord(addr)` | 16 位小端读 |
| `ReadIo(port)` / `WriteIo(port, val)` | IO 端口读写 |
| `ReadEc(reg)` / `WriteEc(reg, val)` | EC IO 端口协议 (0x62/0x66) |
| `Init(timeout)` | 等待驱动就绪 + 预映射 EC 区域 |

**写入策略**：预映射缓存的指针写入 (MapPhysToLin) 对某些地址无效（如 KBNL @ 0xFE80049A），因此 `WritePhys` 优先使用 `SetPhysLong`（32 位地址）或动态 `MapPhysToLin` 单地址映射。

**EC 协议时序**：
```
读: Write(0x66, 0x80) → Sleep(2ms) → Write(0x62, reg) → Sleep(5ms) → Read(0x62)
写: Write(0x66, 0x80) → Sleep(2ms) → Write(0x62, reg) → Sleep(1ms) → Write(0x62, val)
```

**已知限制**：
- `SetPhysLong` 只能访问 32 位物理地址 (< 4GB)
- DSDT `OperationRegion (ECF2, SystemMemory, 0xFE800400, 0xFF)`

### 2. HardwareAbstractionLayer (server/hal/HardwareAbstractionLayer.cs)

EC 寄存器地址映射为语义化 C# 属性。

| 属性/方法 | EC 来源 | 访问方式 | 验证状态 |
|-----------|---------|----------|----------|
| CpuTemperature | EC IO 0x1C | `ReadEc(0x1C)` | ✅ ec_reader 验证 |
| GpuTemperature | 物理内存 0xFE8004E0 | `ReadPhys(BASE + 0xE0)` | ❌ 返回 0，需 nvidia-smi |
| CpuFanRpm | EC IO 0x9D/0x9E | `ReadEc(0x9D)<<8|ReadEc(0x9E)` | ✅ ec_reader 验证 |
| GpuFanRpm | EC IO 0x96/0x97 |
| **CpuFanControl ✅** | **EC Index 0x5F (EC 实时差分扫描发现)** | **`WriteEc(0x5F, val)` val=RPM/100** | **✅ 2026-06-05 真机验证，Range 19-44 (1900-4400 RPM)，受散热模式区间限制** |
| GpuFanControl ❌ | EC Index 0xB3 (LLT ec_writer.cs 推导) / 待发现 | `WriteEc(0xB3, val)` ❌ | ❌ 0xB3 已验证无效。小风扇控制寄存器待差分扫描发现 | `ReadEc(0x96)<<8|ReadEc(0x97)` | ✅ ec_reader 验证 |
| KeyboardBrightness | 物理内存 0xFE80049A | `WritePhys(BASE+0x9A, val)` — SetPhysLong | ✅ 双向验证 |
| FnLock | 物理内存 0xFE800420 bit3 | `ReadBit/WriteBit` | 🔧 代码实现，未验证 |
| CapsLock | Win32 keybd_event (VK_CAPITAL) | `Console.CapsLock` getter + `keybd_event` setter | ✅ SendKeys 真机验证 |
| NumLock | Win32 keybd_event (VK_NUMLOCK) | `Console.NumberLock` getter + `keybd_event` setter | ✅ SendKeys 真机验证 |
| ThermalMode | 物理内存 0xFE8004E4 | `ReadPhys/WritePhys` | 🔧 代码实现，未验证 |
| SetDgpuState(bool) | 0xFED81E40 + (0x0B<<1) | DSAD method 物理写入 ❌ inpoutx64 被硬件保护拦截 | ❌ 不可用 |
| SendSmi(byte) | IO 0x72/0x73 | GSMI 协议 — 端口写错(CMOS端口) | ❌ 无效 |


### 2.5 WmiInterface (新增)

`MICommonInterface.MiInterface` WMI ACPI 接口封装，零外部依赖（仅 `System.Management`）。

| 方法名 | 方法编号 | 操作 | 验证状态 |
|--------|---------|------|----------|
| SystemPerMode | 8 | 散热模式 | ✅ 已验证 |
| GPUMode | 9 | GPU 模式切换 | ✅ **WmiInterface 已验证** |
| FnLock | 11 | Fn 锁 | ✅ 已验证 |
| TPLock | 12 | 触摸板锁 | ✅ 已验证 |
| CPUGPUSYSFanSpeed | 13 | 风扇转速读取/写入 | ✅ 读取可用，**区间内持久** |
| MaxFanSpeedSwitch | 20 | 启用手动风扇控制 | ✅ 恢复固件控制 |
| MaxFanSpeed | 21 | 设置风扇目标转速 (val=RPM/100) | ✅ 区间内持久，区间外 ~8s 回写 |
| CPUThermometer | 22 | CPU 温度读取 | ✅ 已验证 |

**协议格式**：
- InData: `byte[32]`，`[1]=250(Get)/251(Set)`，`[3]=方法编号`，`[4..5]=参数`
- OutData: `byte[]`，`[4..11]` 为返回值

**AppBridge 退役路线**：✅ GPUMode 已迁移到 WmiInterface，FnLock/TPLock 待迁移。废弃 `斗战者控制台.dll` 依赖。
### 3. WebConsoleAPI (server/api/Program.cs)

.NET 8 Minimal API + WebSocket。

| 端点 | 方法 | 说明 |
|------|------|------|
| /api/telemetry | GET | 遥测大盘（温度/风扇/系统开关） |
| /api/control | POST | 硬件控制 (kb_light/fn_lock/num_lock/caps_lock/touchpad_lock/igpu_only/thermal_mode) |
| /api/health | GET | 健康检查 |
| /api/discover | GET | 硬件探测 |
| /ws | WebSocket | 实时遥测推送 |

### 4. TelemetryBackgroundService (server/api/TelemetryBackgroundService.cs)

- 500ms 轮询 HardwareAbstractionLayer
- 检测温度/风扇值变化后才推送（避免无效数据）
- 推送到所有已连接的 WebSocket 客户端
- 管理客户端增删（线程安全 List + lock）

---

## Node.js 辅助服务 (server/server.js) — 仅配置持久化

### 技术栈
- Express 5 + `ws` WebSocket 库
- `systeminformation` — CPU/内存/硬盘占用
- `child_process` — RyzenAdj / nvidia-smi / ec_reader / ec_kb_map / PowerShell WMI
- 管理员权限自动检测 (`net session`)

### 遥测采集流程

```
gatherTelemetry() — Promise.all 并行:
├── si.currentLoad() — CPU 占用率
├── si.mem() — 内存占用
├── si.fsSize() — 硬盘占用
├── readGpuData() — nvidia-smi (温度/占用/频率/显存)
├── readFanSpeed() — ec_reader.exe (风扇 RPM, 串行带 200ms 间隔, EMA 滤波)
├── readCpuFreq() — WMI PercentProcessorPerformance
├── readCpuTemp() — ec_reader.exe temp (EC 0x70)
└── readMemoryFreq() — WMI MemoryClock → Win32_PhysicalMemory
```

**风扇 EMA 滤波**：
- 超过 40% 跳变用 0.15 系数，否则用 0.35
- CPU/GPU 风扇独立滤波

### API 端点

> 完整端点定义、参数、返回值见 [dev-api.md#Nodejs-API](dev-api.md)。

### SMU 控制

由 C# `SmuController` 通过 RyzenAdj 子进程完成。见 `server/hal/SmuController.cs`。

> 历史：Node.js 版 (`child_process`) 已废弃，全功能迁移至 C# HAL（路径修复 + Redirect 移除 + 0xC0000005 退出码适配）。

> **主线方案**：`POST /api/smu/set` → `SmuController` → ryzenadj.exe 子进程 + WinRing0 驱动。Dragon Range SMU 地址 MSG=0x03B10530, REP=0x03B1057C, ARG_BASE=0x03B109C4（参考 RyzenAdj nb_smu_ops.c）。已验证 25W 功率墙写入将 CPU 频率从 3.6GHz 降至 0.5GHz。C# 子进程方案已修复（路径调整 + 移除输出重定向；ryzenadj v0.19.0 已知 exit 时无害崩溃 0xC0000005，不影响实际写入）。

通过 RyzenAdj (`server/tools/ryzenadj.exe`) 下发 AMD SMU 参数。

### WMI 系统开关 (已迁移至 C# WmiInterface)

> 以下 Lenovo Legion 专用 WMI 类在本机（宝龙达模具）上全部不可用。
> 替代方案：C# HAL `WmiInterface` (MiInterface 通道) + `DriverBridge` (EC 物理内存直写)。
> **AppBridge (斗战者控制台.dll) 已退役**，全功能由 WmiInterface 替代。
(https://github.com/BartoszCichecki/LenovoLegionToolkit) (LLT) 项目提供的 WMI 方案。
> LLT 针对正版 Lenovo Legion 模具设计，本机为**宝龙达 OEM** 模具（联想 Legion N176 2025），
> `LENOVO_*` WMI 类全部不存在。**LLT 项目对本机型无参考价值。**
>
> **替代方案**：
> - EC 物理内存直写（C# HAL `WritePhys`/`WriteEc`）：已验证通过 KBNL(背光)、FNHK(Fn锁)、ITSM(散热模式) 等
> - AppBridge（反射调用官方 `斗战者控制台.dll`）：已验证可用 GPU 模式、Fn 锁、触摸板锁等
> - 官方控制台 DLL 逆向：见 §5 AppBridge
>
> 以下 Lenovo Legion 专用 WMI 类在本机（宝龙达模具）上全部不可用，
> 已在 server.js 中废弃为直接返回"此硬件不支持"。

| 功能 | 来源 | 状态 |
|------|------|------|
| 独显直连 (dGpuDirect) | LLT (LENOVO_OTHER_METHOD) | ❌ 废弃 |
| 强冷模式 (fanBoost) | LLT (LENOVO_FAN_METHOD) | ❌ 废弃 |
| Fn 锁 (fnLock) | LLT (LENOVO_OTHER_METHOD) | ✅ C# HAL EC 直写可用 |
| 触摸板锁 (touchpadLock) | LLT (LENOVO_OTHER_METHOD) | ❌ 废弃 |
| 风扇全速 | LLT (LENOVO_FAN_METHOD) | ❌ 废弃 |
| WMI 类探测 | LLT (6 个 LENOVO_* 类) | ❌ 废弃 |
| 键盘背光 (kbBrightnessLevel) | ec_kb_map.exe | ✅ 保留 |

---

### WMI 能力分析

> 2026-06-04 通过扫描 `ROOT/WMI` 命名空间确认。

**结论：宝龙达 OEM 模具不通过 WMI 暴露任何硬件控制接口。**

- `LENOVO_*` 命名空间 (Legion 专用): 全部不存在
- `ASUS*` / `Clevo*` / `MSI*` / `GIGABYTE*` 等其他 OEM 类: 全部不存在
- `AMD_ACPI` — 存在，但仅有只读方法 (`QueryVersion`, `Init`, `GetObjectID`)
- 电池类 (`BatteryCycleCount`, `BatteryStatus` 等) — 存在，但为只读事件类

**所有硬件控制必须走 EC 物理内存直写路线** (`DriverBridge` `ReadPhys`/`WritePhys` 或 `ReadEc`/`WriteEc`)，已验证通过的寄存器映射见 [C# HAL 层](#2-hardwareabstractionlayer-serverhalhardwareabstractionlayercs)。

**标准 Win32 WMI 读取可用：**

| 数据 | WMI 类/属性 |
|------|------------|
| CPU 占用率 | `Win32_PerfFormattedData_PerfOS_Processor.PercentProcessorTime` |
| CPU 频率 | `Win32_PerfFormattedData_Counters_ProcessorInformation.PercentProcessorPerformance` |
| 内存总量 | `Win32_PhysicalMemory` Capacity 求和 |
| 内存使用 | `Win32_OperatingSystem` FreePhysicalMemory |
| 内存频率 | `Win32_PhysicalMemory.ConfiguredClockSpeed` (实际运行频率) |
| 内存频率(回退) | `Win32_PerfFormattedData_Counters_MemoryPerformance.MemoryClock` |
| 系统信息 | `Win32_ComputerSystem`, `Win32_BIOS` |

---

## 文件结构

```
server/
├── hal/                          # C# HAL 类库
│   ├── Douzhanzhe.HAL.csproj     # .NET 8 classlib (AllowUnsafeBlocks)
│   ├── DriverBridge.cs           # inpoutx64 P/Invoke 桥接
│   └── HardwareAbstractionLayer.cs  # EC 寄存器语义映射
└── SmuController.cs              # inpoutx64 物理地址直写 SMU
├── api/                          # C# Web API
│   ├── Douzhanzhe.API.csproj     # .NET 8 Web (refs Hal)
│   ├── Program.cs                # Minimal API + WebSocket
│   ├── TelemetryBackgroundService.cs  # 500ms 心跳推送
│   └── Properties/
├── config/                       # 运行时持久化
│   ├── dashboard-default.json    # 默认配置 (git tracked)
│   ├── ui-state.json             # 卡片排序/隐藏 (gitignored)
│   └── custom-params.json        # 自定义参数 (gitignored)
├── tools/                        # 硬件访问工具
│   ├── inpoutx64.dll             # MIT 开源驱动
│   ├── ryzenadj.exe              # AMD SMU 控制
│   ├── libryzenadj.dll           # SMU 库 (参考留存，当前使用 SmuController 直写)
│   ├── ec_reader.cs / .exe       # EC 寄存器读取
│   └── ec_kb_map.exe             # 键盘背光控制
├── package.json
└── server.js                     # Express 后端
```

---

## 启动方式

> 启动命令见 [dev-index.md#快速启动](dev-index.md)。三终端并行：C# HAL + Node.js + Vite。
>
> C# HAL 必须以管理员权限运行（inpoutx64 驱动要求）。

## 6. SmuController — inpoutx64 物理地址直写 SMU (主线方案)

> 替代方案：原本 `libryzenadj.dll` 的 C API 直调方案在 Dragon Range 上
> `init_ryzenadj()` 返回 NULL（硬件不支持 PM 表 API），且 依赖 WinRing0。
> **SmuController** 通过 `DriverBridge.WritePhys32/ReadPhys32` 直接访问 SMN 物理地址，
> 零外部 DLL 依赖，零 WinRing0，与现有的 inpoutx64 驱动共用。

### 硬件地址 (Dragon Range MP1)

| 寄存器 | 地址 | 用途 |
|---------|-------------|------|
| MSG | `0x03B10928` | 写命令码 |
| REP | `0x03B10578` | 读响应 |
| ARG_BASE | `0x03B10998` | 写参数 arg0 |

### 命令对照表

| 功能 | SMU msg | Value |
|-----------|---------|-------|
| set_stapm_limit | `0x4f` | mW |
| set_fast_limit | `0x3e` | mW |
| set_slow_limit | `0x5f` | mW |
| set_tctl_temp | `0x3f` | °C |

### 驱动依赖链

```
POST /api/smu/set
  → SmuController (Server/hal/SmuController.cs)
    → DriverBridge.WritePhys32/ReadPhys32 (inpoutx64.dll)
      → SetPhysLong/GetPhysLong (inpoutx64 引擎)
        → inpoutx64.sys (内核驱动)
```

### API 端点

| 端点 | 方法 | 说明 |
|------------|--------|------|
| `/api/smu/set` | POST | SMU 参数下发。Body: `{ parameter, valueM }` |
| `/api/smu/probe` | GET | SMU 连通性探测。Return: `{ ok, source: "inpoutx64" }` |

### 已知限制
- Dragon Range 上 PM 表 API 被硬件锁死，`get_table_values()` 不可用
- `SmuController.Probe()` 已确认可访问（`{ ok: true }`），参数下发成功（`{ rc: 0 }`）
- 仅支持 32 位物理地址 (< 4GB)，与 inpoutx64 SetPhysLong 限制一致

## 5. AppBridge — ~~反射调用官方控制台 DLL~~ 🗑️ 已废弃 (2026-06-05)

所有功能已通过 `WmiInterface`（`root\WMI` 原生直通）替代，无需 `斗战者控制台.dll` 依赖。
- `AppLib.cs`、`AppBridge/` 子项目已删除
- `run.ps1` 移除 AppBridge 自动部署
- Debug 页移除 AppBridge 状态 badge
- 前端"斗战者控制台"文本改为"Douzhanzhe Console"

---

> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
