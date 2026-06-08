# 后端架构

> **📋 更新规则**：
> - 新增/废弃后端服务 → 更新服务部署表（端口/技术/职责）
> - 新增架构层/组件 → 在对应分层章节追加说明
> - 驱动/依赖链变更 → 同步更新"驱动依赖链"章节
> - 同步更新主记忆 §1 文档地图中 `dev-backend.md` 的描述

[TOC]

## 服务部署

| 服务 | 端口 | 技术 | 职责 |
|------|------|------|------|
| C# HAL API | :3100 | .NET 8 Minimal API | 遥测、硬件控制、WebSocket、SMU、Debug、配置持久化、开机自启 |
| Douzhanzhe.Shell | — | WinForms + WebView2 | 桌面壳，系统托盘最小化，退出时自动杀掉后端 API 进程（`KillProcessOnPort(3100)`）；`server/shell/` |

### Vite 代理分流（已废弃）

> Vite dev server (`:5173`) 已移除。前端页面由 `run.ps1` 中 `npm run build` 构建后嵌入 C# API `wwwroot/`。
> 统一访问 `http://127.0.0.1:3100/`。
>
> 前端的 WebSocket 直连 `ws://127.0.0.1:3100/ws`。

---

## C# HAL 三层架构

```
server/api/Program.cs                     # WebConsoleAPI — HTTP 端点
  ↓ 依赖注入                    ↓ 依赖注入
server/hal/HardwareAbstractionLayer.cs    # 硬件映射层 — 语义化属性
server/hal/NvapiGpuController.cs          # NVAPI + KaronOC GPU 控制
server/api/WmiInterface.cs                # WMI ACPI MICommonInterface 直调
  ↓                                        ↓
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
| CpuFanRpm | EC IO 0x9D/0x9E | `ReadEc(0x9D)<<8|ReadEc(0x9E)` — **双读仲裁**（最多 3 次，非零即返） | ✅ EC 16 位竞态已修复 |
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

- **250ms** 轮询 HardwareAbstractionLayer（原 500ms）
- **每次轮询无条件推送**全量遥测（删除了变化检测过滤 `if (!changed) continue;`）
- 推送到所有已连接的 WebSocket 客户端
- 管理客户端增删（线程安全 List + lock）


---

> **主线方案**：`POST /api/smu/set` → `SmuController` → ryzenadj.exe 子进程 + WinRing0 驱动。Dragon Range SMU 地址 MSG=0x03B10530, REP=0x03B1057C, ARG_BASE=0x03B109C4（参考 RyzenAdj nb_smu_ops.c）。已验证 25W 功率墙写入将 CPU 频率从 3.6GHz 降至 0.5GHz。C# 子进程方案已修复（路径调整 + 移除输出重定向；ryzenadj v0.19.0 已知 exit 时无害崩溃 0xC0000005，不影响实际写入）。
>
> **WinRing0 驱动自动加载**：run.ps1 启动时通过 `Start-Process -Verb RunAs` 提权创建+启动 WinRing0 内核驱动（绕过杀毒拦截）；Program.cs 启动时自动检测驱动状态，未加载则通过 `sc.exe create/start` 自动安装。双重保障确保 SMU 可用。

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
| Fn 锁 (fnLock) | LLT (LENOVO_OTHER_METHOD) | ✅ C# HAL EC 直写可用，已迁移至 WMI Method 11 |
| 触摸板锁 (touchpadLock) | LLT (LENOVO_OTHER_METHOD) | ✅ WMI Method 12 直调（替代 PowerShell PnP） |
| 风扇全速 | LLT (LENOVO_FAN_METHOD) | ❌ 废弃 |
| WMI 类探测 | LLT (6 个 LENOVO_* 类) | ❌ 废弃 |
| 键盘背光 (kbBrightnessLevel) | ec_kb_map.exe | ✅ 保留 |

---

### WMI 能力分析

> 2026-06-04 通过扫描 `ROOT/WMI` 命名空间确认。

`ROOT/WMI` 命名空间中存在 `MICommonInterface`（实例 `ACPI\PNP0C14\MIFS_0`），可通过 `MiInterface` 方法调用 ACPI WMI 命令。参考斗战者控制台.dll 反编译和 BellatorFanControl 源码，已验证可用的方法：

| 方法 | 编号 | 已验证 |
|:-----|:----:|:------:|
| SystemPerMode | 8 | ✅ |
| GPUMode | 9 | ✅ |
| FnLock | 11 | ✅ |
| TPLock | 12 | ✅ |
| CPUGPUSYSFanSpeed | 13 | ✅ 读取, Set空壳 |
| MaxFanSpeedSwitch | 20 | ✅ Bellator协议 |
| MaxFanSpeed | 21 | ✅ Bellator协议 |
| CPUThermometer | 22 | ✅ |
| CPUPower | 23 | ✅ |

详见 `WmiInterface.cs` 封装。项目**不依赖**斗战者控制台.dll，直调 WMI MiInterface。

**Legion 专用类 (`LENOVO_*` 命名空间) 在本机（宝龙达模具）全部不可用。**

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
│   ├── HardwareAbstractionLayer.cs  # EC 寄存器语义映射
│   ├── NvapiGpuController.cs        # NVAPI + KaronOC GPU 控制
│   └── SmuController.cs             # ryzenadj.exe 子进程 SMU
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
│   ├── libryzenadj.dll           # ~~SMU 库~~ ❌ 已从跟踪中删除，由 SmuController 替代
│   ├── ec_reader.cs / .exe       # EC 寄存器读取
│   └── ec_kb_map.exe             # 键盘背光控制
└── package.json
```

---

## 启动方式

> 启动命令见 [dev-index.md#快速启动](dev-index.md)。C# HAL API 单服务即可运行。
>
> C# HAL 必须以管理员权限运行（inpoutx64 驱动要求）。

## 6. SmuController — ryzenadj.exe 子进程封装 (当前方案)

> **实际方案**：`SmuController` 通过 `Process.Start` 调用 `ryzenadj.exe` 子进程（带 WinRing0 驱动），
> 完成 AMD SMU 参数写入（stapm_limit/fast_limit/slow_limit/tctl_temp）。
>
> **备选方案已废弃**：原本 `libryzenadj.dll` 的 C API 直调方案在 Dragon Range 上
> `init_ryzenadj()` 返回 NULL（硬件不支持 PM 表 API），且依赖 WinRing0。
>
> **未来（搁置）**：通过 `DriverBridge.WritePhys32/ReadPhys32` 直接访问 SMN 物理地址，
> 零外部 DLL 依赖，零 WinRing0。当前 ryzenadj 子进程方案工作正常，暂无迁移必要。

### 硬件地址 (Dragon Range MP1) — ryzenadj 源码参考

| 寄存器 | 地址 | 用途 |
|---------|-------------|------|
| MSG | `0x03B10928` | 写命令码 |
| REP | `0x03B10578` | 读响应 |
| ARG_BASE | `0x03B10998` | 写参数 arg0 |

### 命令对照表 (ryzenadj nb_smu_ops.c)

| 功能 | SMU msg | Value |
|-----------|---------|-------|
| set_stapm_limit | `0x4f` | mW |
| set_fast_limit | `0x3e` | mW |
| set_slow_limit | `0x5f` | mW |
| set_tctl_temp | `0x3f` | °C |

### 调用链

```
POST /api/smu/set
  → SmuController (Server/hal/SmuController.cs)
    → RyzenAdj(string[] args)
      → Process.Start(ryzenadj.exe)
        → WinRing0.sys (内核驱动)
```

### API 端点

| 端点 | 方法 | 说明 |
|------------|--------|------|
| `/api/smu/set` | POST | SMU 参数下发。支持: `power_limit`, `short_power_limit`, `temp_limit`, `co_all`, `cpu_freq_limit`, `turbo_disable` |
| `/api/smu/probe` | GET | SMU 连通性探测。Return: `{ ok, source: "ryzenadj" }` |
| `/api/smu/status` | GET | SMU 状态 + 能力清单。Return: `{ ok, probe, source, capabilities }` |
| `/api/smu/api-type` | GET | 实现方式说明。Return: `{ ok, type: "subprocess", source }` |
| `/api/ryzenadj/info` | GET | SMU 探测（前端兼容别名）。Return: `{ ok, data: { probeResult, type, source } }` |
| `/api/uxtu/apply` | POST | SMU 参数下发兼容格式。新增字段: `cpuShortPptW`, `cpuVoltageOffset`, `cpuFreqLimitEnabled`, `cpuFreqLimitMhz`, `cpuTurboDisabled` |
| `/api/smu/raw` | POST | 原始 SMU 命令（当前本后端不支持） |
| `/api/smu/read-reg` | GET | SMN 寄存器读取（当前本后端不支持） |

### 已知限制
- Dragon Range 上 PM 表 API 被硬件锁死，`get_table_values()` 不可用
- `SmuController.Probe()` 已确认可访问（`{ ok: true }`），参数下发成功（`{ rc: 0 }`）
- ryzenadj v0.19.0 退出时可能无害崩溃 0xC0000005（不影响实际写入），已适配为成功退出码
- `SetVrmCurrent`、`SendRawSmuCommand`、`ReadSmnRegister` 均为存根（throw NotSupportedException）
- 新增方法: `SetShortPowerLimit(fastMw, slowMw)`、`SetCurveOptimizer(mV)`、`SetCpuFreqLimit(mhz)`、`SetTurboDisabled(bool)` — 均通过 RyzenAdj 子进程实现
- 运行时依赖 `WinRing0` 驱动（由 `run.ps1` 自动部署）+ `ryzenadj.exe`

## 7. CpuAffinityManager — CPU 核心数限制 (进程亲和性)

> 通过 `Process.ProcessorAffinity` 设置所有进程的 CPU 亲和性掩码，限制可用核心数。
> 新建进程自动应用限制（WMI `Win32_ProcessStartTrace` 监听）。

### 依赖
- `System.Management` NuGet 包

### API 集成
| 端点 | 来源 | 说明 |
|------|------|------|
| `POST /api/uxtu/apply` | `body.Params.CpuCoreLimit` | 0=不限制，>0=限制核心数 |

### 已知限制
- 仅限 Windows（`Process.ProcessorAffinity` + WMI 平台限制）
- 单处理器组支持（逻辑核心 ≤ 64）
- `CpuAffinityManager.Reset()` 不会恢复已有进程的原始亲和性

## 8. NvapiGpuController — NVAPI + KaronOC GPU 控制

> GPU 状态读取 (NVAPI P/Invoke nvapi64.dll) + 超频 (蛟龙控制台 KaronOC.dll)。
> 文件：`server/hal/NvapiGpuController.cs`

### 双层架构

| 层 | 引擎 | 用途 | 状态 |
|----|------|------|------|
| 状态读取 | NVAPI P/Invoke | 时钟频率、温度限制、功率读取、P-States 诊断 | ✅ 已验证 |
| 超频写入 | KaronOC.dll (蛟龙) | GPU 核心/显存 P-State 偏移 (MHz) | ✅ 已验证 |
| 超频回退 | NVAPI SetPStates20 | RTX 5060 Laptop GPU 返回 -104 (NOT_SUPPORTED) | ❌ 不可用 |

### KaronOC.dll (蛟龙超频引擎)

- **来源**：蛟龙控制台 JiaoLong 7.3 (`D:\Program Files\JiaoLong7.3\KaronOC.dll`)
- **原理**：原生 C++ DLL，内部调用 NVAPI SetPStates20，使用 V2/7416 字节 P-States 结构体绕过笔记本 GPU 限制
- **导出函数**：
  - `ChangePstatesLevel0Settings(int coreOffsetMhz, int memOffsetMhz)` → int (0=成功)
  - `GetPstatesLevel0Settings(void* gpuHandle, void* outputBuf)` → int
- **PDB 来源**：`D:\work\Git\NvOCVerify` — 蛟龙作者的 NVAPI 超频验证项目

### NVAPI 结构体实测数据 (Blackwell RTX 5060 Laptop GPU)

| 结构体 | 版本号 | 大小 | 说明 |
|---------|------|------|------|
| P-States 2.0 (Get) | V3 | 7416 bytes | KaronOC 使用的尺寸 |
| P-States 2.0 (Set) | V2 | 7416 bytes | KaronOC 使用的尺寸 |
| P-States 2.0 (我们的) | V1 | 7316 bytes | 直调 NVAPI 可用 |
| Clock Freq | V2 | 264 bytes | 时钟频率读取 |
| Thermal Info | V1 | 88 bytes | 温度限制范围 |
| Thermal Status | V1 | 40 bytes | 当前温度限制 (5 uint/entry) |
| Power Info | V1 | 184 bytes | 笔记本 GPU 全零 |
| Power Status | V1 | 72 bytes | 笔记本 GPU 全零 |

### 调用链

```
POST /api/nvapi/overclock
  → NvapiGpuController.SetP0Offset(coreMhz, memMhz)
    → KaronOC.ChangePstatesLevel0Settings(core, mem)
      → 内部 NVAPI Init + EnumGPUs + GetPStates20 + SetPStates20
```

### API 端点

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/nvapi/status` | GET | GPU 状态 (时钟/温度限制/功率/OC支持/引擎) |
| `/api/nvapi/overclock` | POST | GPU 超频 Body: `{ coreOffsetMhz, memOffsetMhz }` |
| `/api/nvapi/dump-pstates` | GET | P-States 诊断输出 |
| `/api/nvapi/thermal-limit` | POST | 温度限制设置 Body: `{ tempC }` |
| `/api/nvapi/power-limit` | POST | 功率限制设置 Body: `{ powerW }` (笔记本 GPU 不支持) |

### 已知限制
- NVAPI SetPStates20 直调在 RTX 5060 Laptop GPU 返回 -104 (NOT_SUPPORTED)
- NVAPI 功率控制在笔记本 GPU 全返回零
- 超频依赖蛟龙 KaronOC.dll（需安装 JiaoLong 7.3 或将 DLL 复制到应用目录）
- P-States 偏移范围: core [-1000, 1000] MHz, mem [-1000, 3000] MHz

## 5. AppBridge — ~~反射调用官方控制台 DLL~~ 🗑️ 已废弃 (2026-06-05)

所有功能已通过 `WmiInterface`（`root\WMI` 原生直通）替代，无需 `斗战者控制台.dll` 依赖。
- `AppLib.cs`、`AppBridge/` 子项目已删除
- `run.ps1` 移除 AppBridge 自动部署
- Debug 页移除 AppBridge 状态 badge
- 前端"斗战者控制台"文本改为"Douzhanzhe Console"

---

> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
