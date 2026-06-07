# GPU控制器API

<cite>
**本文档引用的文件**
- [GpuController.cs](file://server/hal/GpuController.cs)
- [Program.cs](file://server/api/Program.cs)
- [HardwareAbstractionLayer.cs](file://server/hal/HardwareAbstractionLayer.cs)
- [WmiInterface.cs](file://server/api/WmiInterface.cs)
- [TelemetryBackgroundService.cs](file://server/api/TelemetryBackgroundService.cs)
- [DriverBridge.cs](file://server/hal/DriverBridge.cs)
- [SmuController.cs](file://server/hal/SmuController.cs)
- [Douzhanzhe.API.csproj](file://server/api/Douzhanzhe.API.csproj)
</cite>

## 目录
1. [简介](#简介)
2. [项目结构](#项目结构)
3. [核心组件](#核心组件)
4. [架构总览](#架构总览)
5. [详细组件分析](#详细组件分析)
6. [依赖关系分析](#依赖关系分析)
7. [性能考虑](#性能考虑)
8. [故障排除指南](#故障排除指南)
9. [结论](#结论)
10. [附录](#附录)

## 简介
本文件面向DOUZHANZHE-Control项目的GPU控制器API，聚焦NVIDIA GPU的控制接口，涵盖以下能力：
- 核心频率锁定与上限限制
- 显存频率锁定与上限限制
- 频率重置
- GPU状态查询、时钟信息获取与功率监控
- 提供完整的性能调优示例与兼容性要求说明

该API通过nvidia-smi子进程执行底层控制命令，结合硬件抽象层(HAL)与WMI接口，提供稳定可控的GPU频率管理能力。

## 项目结构
后端采用ASP.NET Core Web API，核心模块分布如下：
- API层：定义REST接口与路由，负责请求解析、参数校验与响应封装
- HAL层：抽象硬件访问，提供统一的遥测与控制接口
- 控制器层：具体实现GPU、SMU、风扇等设备控制逻辑
- 遥测服务：后台定时推送系统状态至前端

```mermaid
graph TB
subgraph "API层"
API_Program["Program.cs<br/>REST路由与端点"]
API_Wmi["WmiInterface.cs<br/>WMI接口"]
API_Telemetry["TelemetryBackgroundService.cs<br/>后台遥测"]
end
subgraph "HAL层"
HAL_Hardware["HardwareAbstractionLayer.cs<br/>硬件抽象与遥测"]
HAL_Driver["DriverBridge.cs<br/>EC/IO访问桥"]
HAL_Gpu["GpuController.cs<br/>GPU控制"]
HAL_Smu["SmuController.cs<br/>SMU控制"]
end
API_Program --> HAL_Gpu
API_Program --> HAL_Smu
API_Program --> HAL_Hardware
API_Program --> API_Wmi
API_Program --> API_Telemetry
HAL_Gpu --> HAL_Hardware
HAL_Smu --> HAL_Hardware
HAL_Hardware --> HAL_Driver
```

**图表来源**
- [Program.cs:1-783](file://server/api/Program.cs#L1-L783)
- [GpuController.cs:1-116](file://server/hal/GpuController.cs#L1-L116)
- [HardwareAbstractionLayer.cs:1-772](file://server/hal/HardwareAbstractionLayer.cs#L1-L772)
- [WmiInterface.cs:1-210](file://server/api/WmiInterface.cs#L1-L210)
- [TelemetryBackgroundService.cs:1-143](file://server/api/TelemetryBackgroundService.cs#L1-L143)
- [DriverBridge.cs:1-150](file://server/hal/DriverBridge.cs#L1-L150)
- [SmuController.cs:1-142](file://server/hal/SmuController.cs#L1-L142)

**章节来源**
- [Program.cs:1-783](file://server/api/Program.cs#L1-L783)
- [Douzhanzhe.API.csproj:1-40](file://server/api/Douzhanzhe.API.csproj#L1-L40)

## 核心组件
- GPU控制器(GpuController)：封装nvidia-smi子进程，提供锁频、限频、重置等操作
- 硬件抽象层(HAL)：统一遥测与系统信息读取，提供GPU温度、频率、显存等数据
- WMI接口：提供系统级控制（如GPU模式、Fn锁、触摸板锁等）
- 后台遥测服务：周期性推送系统状态至前端

**章节来源**
- [GpuController.cs:1-116](file://server/hal/GpuController.cs#L1-L116)
- [HardwareAbstractionLayer.cs:1-772](file://server/hal/HardwareAbstractionLayer.cs#L1-L772)
- [WmiInterface.cs:1-210](file://server/api/WmiInterface.cs#L1-L210)
- [TelemetryBackgroundService.cs:1-143](file://server/api/TelemetryBackgroundService.cs#L1-L143)

## 架构总览
GPU控制流程通过API路由进入，根据action分派到GpuController执行nvidia-smi命令；同时可结合HAL进行状态查询与遥测展示。

```mermaid
sequenceDiagram
participant Client as "客户端"
participant API as "Program.cs<br/>/api/gpu/set"
participant Ctrl as "GpuController.cs"
participant SMI as "nvidia-smi"
Client->>API : POST /api/gpu/set {action, min/max/value}
API->>Ctrl : 分派对应操作
Ctrl->>SMI : 执行 --lock-gpu-clocks/--reset-gpu-clocks 等
SMI-->>Ctrl : 返回执行结果
Ctrl-->>API : 返回状态
API-->>Client : JSON响应
```

**图表来源**
- [Program.cs:396-447](file://server/api/Program.cs#L396-L447)
- [GpuController.cs:42-86](file://server/hal/GpuController.cs#L42-L86)

**章节来源**
- [Program.cs:396-447](file://server/api/Program.cs#L396-L447)
- [GpuController.cs:1-116](file://server/hal/GpuController.cs#L1-L116)

## 详细组件分析

### GPU控制器API设计
- 请求体字段
  - action：操作类型，支持"lock"/"lock-clocks"、"lock-exact"、"limit"/"limit-max"、"reset"/"reset-clocks"、"lock-memory"/"lock-memory-clocks"、"limit-memory"、"reset-memory"/"reset-memory-clocks"
  - min/max/value：数值参数，用于指定频率上下限或精确频率
- 响应体字段
  - ok：布尔值，表示操作是否成功
  - error：字符串，错误信息（当ok=false时）

```mermaid
flowchart TD
Start(["接收请求"]) --> Parse["解析 action/min/max/value"]
Parse --> Switch{"根据 action 分支"}
Switch --> |lock/lock-clocks| LockRange["设置核心频率区间<br/>--lock-gpu-clocks=min,max"]
Switch --> |lock-exact| LockExact["设置精确核心频率<br/>--lock-gpu-clocks=value"]
Switch --> |limit/limit-max| LimitMax["设置核心频率上限<br/>--lock-gpu-clocks=0,max"]
Switch --> |reset/reset-clocks| ResetCore["重置核心频率<br/>--reset-gpu-clocks"]
Switch --> |lock-memory/lock-memory-clocks| LockMemRange["设置显存频率区间<br/>--lock-memory-clocks=min,max"]
Switch --> |limit-memory| LimitMemMax["设置显存频率上限<br/>--lock-memory-clocks=max,max"]
Switch --> |reset-memory/reset-memory-clocks| ResetMem["重置显存频率<br/>--reset-memory-clocks"]
Switch --> |其他| Error["返回未知操作错误"]
LockRange --> Exec["执行 nvidia-smi"]
LockExact --> Exec
LimitMax --> Exec
ResetCore --> Exec
LockMemRange --> Exec
LimitMemMax --> Exec
ResetMem --> Exec
Exec --> Resp["返回 {ok:true} 或错误信息"]
Error --> Resp
```

**图表来源**
- [Program.cs:396-447](file://server/api/Program.cs#L396-L447)
- [GpuController.cs:42-86](file://server/hal/GpuController.cs#L42-L86)

**章节来源**
- [Program.cs:396-447](file://server/api/Program.cs#L396-L447)
- [GpuController.cs:42-86](file://server/hal/GpuController.cs#L42-L86)

### GPU状态查询与功率监控
- 状态端点：GET /api/gpu/status
- 返回字段
  - coreClockMHz：当前核心频率(MHz)
  - memoryClockMHz：当前显存频率(MHz)
  - powerDrawW：当前功耗(W)
  - baseCoreClockMHz：基准核心频率
  - maxCoreClockMHz：硬件最大核心频率
- 实现原理：调用nvidia-smi查询当前频率与功耗，并解析输出

```mermaid
sequenceDiagram
participant Client as "客户端"
participant API as "Program.cs<br/>/api/gpu/status"
participant Ctrl as "GpuController.cs"
participant SMI as "nvidia-smi"
Client->>API : GET /api/gpu/status
API->>Ctrl : GetClockInfo()
Ctrl->>SMI : --query-gpu=clocks.current.graphics,clocks.current.memory,power.draw --format=csv,noheader,nounits
SMI-->>Ctrl : 输出三列数值
Ctrl-->>API : 解析为结构体
API-->>Client : JSON响应
```

**图表来源**
- [Program.cs:448-461](file://server/api/Program.cs#L448-L461)
- [GpuController.cs:77-107](file://server/hal/GpuController.cs#L77-L107)

**章节来源**
- [Program.cs:448-461](file://server/api/Program.cs#L448-L461)
- [GpuController.cs:77-107](file://server/hal/GpuController.cs#L77-L107)

### GPU性能调优完整示例
以下示例演示常见的调优流程，建议在具备管理员权限且已安装nvidia驱动的环境中执行：

- 场景一：锁频
  - 动作：lock-exact
  - 参数：value=目标频率(MHz)
  - 适用：需要固定GPU频率以稳定性能或降低噪音
  - 注意：确保目标频率在显卡支持范围内

- 场景二：上限限制
  - 动作：limit-max
  - 参数：value=上限频率(MHz)
  - 适用：允许频率自动调节但不超过设定上限

- 场景三：显存频率限制
  - 动作：limit-memory
  - 参数：value=显存频率(单位：特定数值，参考显卡支持)
  - 适用：限制显存频率以控制发热与功耗

- 场景四：重置
  - 动作：reset-clocks 或 reset-memory-clocks
  - 适用：恢复默认频率策略

- 场景五：状态核对
  - 动作：GET /api/gpu/status
  - 适用：确认当前频率与功耗状态

```mermaid
sequenceDiagram
participant User as "用户"
participant API as "Program.cs"
participant GPU as "GpuController.cs"
participant SMI as "nvidia-smi"
User->>API : POST /api/gpu/set {action : "lock-exact", value : 2700}
API->>GPU : SetExactGpuClock(2700)
GPU->>SMI : --lock-gpu-clocks=2700
SMI-->>GPU : 执行成功
GPU-->>API : ok=true
API-->>User : {"ok" : true}
User->>API : GET /api/gpu/status
API->>GPU : GetClockInfo()
GPU->>SMI : 查询当前频率与功耗
SMI-->>GPU : 返回数值
GPU-->>API : 结构体
API-->>User : {"coreClockMHz" : ..., "powerDrawW" : ...}
```

**图表来源**
- [Program.cs:396-461](file://server/api/Program.cs#L396-L461)
- [GpuController.cs:42-86](file://server/hal/GpuController.cs#L42-L86)

**章节来源**
- [Program.cs:396-461](file://server/api/Program.cs#L396-L461)
- [GpuController.cs:42-86](file://server/hal/GpuController.cs#L42-L86)

### 兼容性要求与注意事项
- 系统与驱动
  - 需要Windows平台与已安装NVIDIA驱动
  - nvidia-smi需在PATH中可执行
- 权限要求
  - 需要管理员权限以执行频率锁定与重置
- 参数范围
  - 频率参数需在显卡支持范围内，否则nvidia-smi会拒绝
- 超时与错误处理
  - nvidia-smi执行存在超时机制，超时或非零退出码会抛出异常
- 并发与稳定性
  - 频率调整可能影响系统稳定性，建议在测试环境先行验证

**章节来源**
- [GpuController.cs:12-40](file://server/hal/GpuController.cs#L12-L40)

## 依赖关系分析
- 组件耦合
  - API层通过依赖注入使用GpuController与SmuController
  - GpuController依赖nvidia-smi子进程执行命令
  - HAL层提供系统信息与遥测，被API层与遥测服务共享
- 外部依赖
  - nvidia-smi：NVIDIA官方工具，用于频率与功耗查询/控制
  - WMI：系统级控制接口（如GPU模式、Fn锁等）
  - WinRing0：SMU控制所需的内核驱动（可选）

```mermaid
graph LR
API["Program.cs"] --> GPU["GpuController.cs"]
API --> SMU["SmuController.cs"]
API --> HAL["HardwareAbstractionLayer.cs"]
API --> WMI["WmiInterface.cs"]
GPU --> SMI["nvidia-smi"]
SMU --> WINRING0["WinRing0x64.sys"]
HAL --> DRIVER["DriverBridge.cs"]
```

**图表来源**
- [Program.cs:1-783](file://server/api/Program.cs#L1-L783)
- [GpuController.cs:1-116](file://server/hal/GpuController.cs#L1-L116)
- [SmuController.cs:1-142](file://server/hal/SmuController.cs#L1-L142)
- [HardwareAbstractionLayer.cs:1-772](file://server/hal/HardwareAbstractionLayer.cs#L1-L772)
- [DriverBridge.cs:1-150](file://server/hal/DriverBridge.cs#L1-L150)

**章节来源**
- [Program.cs:1-783](file://server/api/Program.cs#L1-L783)
- [Douzhanzhe.API.csproj:1-40](file://server/api/Douzhanzhe.API.csproj#L1-L40)

## 性能考虑
- 频率锁定与上限限制
  - 锁定频率可减少波动，提升稳定性；但可能牺牲部分性能
  - 限频可在保证性能的同时控制发热与功耗
- 功率监控
  - 通过nvidia-smi查询功耗，结合温度监控避免过热
- 遥测频率
  - 后台遥测每250ms推送一次，兼顾实时性与系统开销

[本节为通用指导，无需特定文件引用]

## 故障排除指南
- nvidia-smi超时或失败
  - 检查nvidia-smi是否在PATH中，确认驱动安装正确
  - 确认以管理员权限运行
- 频率设置无效
  - 确认输入频率在显卡支持范围内
  - 尝试先reset再重新设置
- 状态查询异常
  - 若HAL回退到nvidia-smi查询，可能受网络/权限影响
  - 检查防火墙与杀毒软件拦截

**章节来源**
- [GpuController.cs:12-40](file://server/hal/GpuController.cs#L12-L40)
- [HardwareAbstractionLayer.cs:147-195](file://server/hal/HardwareAbstractionLayer.cs#L147-L195)

## 结论
DOUZHANZHE-Control的GPU控制器API通过简洁的REST接口与nvidia-smi集成，提供了核心频率锁定、显存频率限制与频率重置等关键能力。配合状态查询与功率监控，可实现较为完善的GPU性能调优方案。建议在测试环境充分验证后再应用于生产环境，并严格遵循权限与兼容性要求。

[本节为总结性内容，无需特定文件引用]

## 附录

### API端点一览
- POST /api/gpu/set
  - 请求体：action、min、max、value
  - 响应：ok、error
- GET /api/gpu/status
  - 响应：coreClockMHz、memoryClockMHz、powerDrawW、baseCoreClockMHz、maxCoreClockMHz

**章节来源**
- [Program.cs:396-461](file://server/api/Program.cs#L396-L461)