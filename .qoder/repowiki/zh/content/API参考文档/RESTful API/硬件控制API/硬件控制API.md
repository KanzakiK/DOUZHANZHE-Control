# 硬件控制API

<cite>
**本文档引用的文件**
- [Douzhanzhe.API.http](file://server/api/Douzhanzhe.API.http)
- [Program.cs](file://server/api/Program.cs)
- [WmiInterface.cs](file://server/api/WmiInterface.cs)
- [DriverBridge.cs](file://server/hal/DriverBridge.cs)
- [HardwareAbstractionLayer.cs](file://server/hal/HardwareAbstractionLayer.cs)
- [GpuController.cs](file://server/hal/GpuController.cs)
- [SmuController.cs](file://server/hal/SmuController.cs)
- [CpuAffinityManager.cs](file://server/hal/CpuAffinityManager.cs)
- [dev-api.md](file://docs/dev-api.md)
- [dev-architecture.md](file://docs/dev-architecture.md)
- [dev-backend.md](file://docs/dev-backend.md)
- [dev-ec-map.md](file://docs/dev-ec-map.md)
- [reference-consoles.md](file://docs/reference-consoles.md)
</cite>

## 目录
1. [简介](#简介)
2. [项目结构](#项目结构)
3. [核心组件](#核心组件)
4. [架构总览](#架构总览)
5. [详细组件分析](#详细组件分析)
6. [依赖关系分析](#依赖关系分析)
7. [性能考量](#性能考量)
8. [故障排除指南](#故障排除指南)
9. [结论](#结论)
10. [附录](#附录)

## 简介
本文件面向硬件控制API的使用者与维护者，系统性阐述POST /api/control端点的控制请求格式、支持的控制目标（如键盘背光、Fn锁定、触摸板锁定等）、底层控制机制（EC寄存器写入、WMI命令执行）以及权限、安全与错误处理策略。文档同时提供参数验证与边界检查说明，并通过图示展示关键流程。

## 项目结构
后端采用ASP.NET Core Web API，HAL层封装底层硬件抽象，WMI接口用于与系统固件交互，前端通过HTTP客户端调用API。核心文件分布如下：
- API层：定义HTTP契约与端点（Program.cs、Douzhanzhe.API.http）
- HAL层：抽象硬件控制器（HardwareAbstractionLayer.cs、GpuController.cs、SmuController.cs、CpuAffinityManager.cs、DriverBridge.cs）
- WMI接口：系统级控制通道（WmiInterface.cs）
- 文档：开发API规范、架构设计、后端实现细节、EC映射与参考控制台

```mermaid
graph TB
subgraph "API层"
HTTP["HTTP客户端<br/>Douzhanzhe.API.http"]
API["Web API端点<br/>Program.cs"]
end
subgraph "HAL层"
HAL["硬件抽象层<br/>HardwareAbstractionLayer.cs"]
GPU["GPU控制器<br/>GpuController.cs"]
SMU["SMU控制器<br/>SmuController.cs"]
CPU["CPU亲和性管理<br/>CpuAffinityManager.cs"]
DRV["驱动桥接<br/>DriverBridge.cs"]
end
subgraph "系统接口"
WMI["WMI接口<br/>WmiInterface.cs"]
end
HTTP --> API
API --> HAL
HAL --> GPU
HAL --> SMU
HAL --> CPU
HAL --> DRV
HAL --> WMI
```

**图表来源**
- [Program.cs:1-200](file://server/api/Program.cs#L1-L200)
- [HardwareAbstractionLayer.cs:1-200](file://server/hal/HardwareAbstractionLayer.cs#L1-L200)
- [GpuController.cs:1-200](file://server/hal/GpuController.cs#L1-L200)
- [SmuController.cs:1-200](file://server/hal/SmuController.cs#L1-L200)
- [CpuAffinityManager.cs:1-200](file://server/hal/CpuAffinityManager.cs#L1-L200)
- [DriverBridge.cs:1-200](file://server/hal/DriverBridge.cs#L1-L200)
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)

**章节来源**
- [Program.cs:1-200](file://server/api/Program.cs#L1-L200)
- [dev-architecture.md:1-200](file://docs/dev-architecture.md#L1-L200)

## 核心组件
- 控制端点与契约
  - POST /api/control：接收控制请求，返回执行结果或错误信息
  - 请求体字段：target（控制目标）、value（目标值）、options（可选参数）
  - 响应体字段：success（布尔）、message（字符串）、data（可选负载）

- HAL层职责
  - HardwareAbstractionLayer：统一调度各控制器
  - GpuController：GPU相关控制（如风扇曲线、显卡功耗）
  - SmuController：系统管理单元控制（如PPT限制、频率调节）
  - CpuAffinityManager：CPU亲和性与调度策略
  - DriverBridge：与底层驱动通信的桥接层

- WMI接口
  - WmiInterface：封装WMI命令执行，用于系统级控制（如电源策略、设备状态）

- 文档支撑
  - dev-api.md：API规范与示例
  - dev-ec-map.md：EC寄存器映射与写入规则
  - reference-consoles.md：参考控制台与厂商工具链

**章节来源**
- [Douzhanzhe.API.http:1-200](file://server/api/Douzhanzhe.API.http#L1-L200)
- [HardwareAbstractionLayer.cs:1-200](file://server/hal/HardwareAbstractionLayer.cs#L1-L200)
- [GpuController.cs:1-200](file://server/hal/GpuController.cs#L1-L200)
- [SmuController.cs:1-200](file://server/hal/SmuController.cs#L1-L200)
- [CpuAffinityManager.cs:1-200](file://server/hal/CpuAffinityManager.cs#L1-L200)
- [DriverBridge.cs:1-200](file://server/hal/DriverBridge.cs#L1-L200)
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)
- [dev-api.md:1-200](file://docs/dev-api.md#L1-L200)
- [dev-ec-map.md:1-200](file://docs/dev-ec-map.md#L1-L200)
- [reference-consoles.md:1-200](file://docs/reference-consoles.md#L1-L200)

## 架构总览
POST /api/control的典型调用序列如下：

```mermaid
sequenceDiagram
participant Client as "客户端"
participant API as "API端点<br/>Program.cs"
participant HAL as "硬件抽象层<br/>HardwareAbstractionLayer.cs"
participant CTRL as "具体控制器<br/>GpuController/SmuController/CpuAffinityManager"
participant WMI as "WMI接口<br/>WmiInterface.cs"
Client->>API : "POST /api/control {target, value, options}"
API->>HAL : "解析请求并路由到对应控制器"
HAL->>CTRL : "执行目标控制逻辑"
CTRL->>WMI : "必要时通过WMI下发系统级指令"
WMI-->>CTRL : "返回执行结果"
CTRL-->>HAL : "返回控制结果"
HAL-->>API : "汇总结果"
API-->>Client : "{success, message, data}"
```

**图表来源**
- [Program.cs:1-200](file://server/api/Program.cs#L1-L200)
- [HardwareAbstractionLayer.cs:1-200](file://server/hal/HardwareAbstractionLayer.cs#L1-L200)
- [GpuController.cs:1-200](file://server/hal/GpuController.cs#L1-L200)
- [SmuController.cs:1-200](file://server/hal/SmuController.cs#L1-L200)
- [CpuAffinityManager.cs:1-200](file://server/hal/CpuAffinityManager.cs#L1-L200)
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)

## 详细组件分析

### 控制目标与请求格式
- 支持的控制目标（target示例）
  - keyboard_backlight：键盘背光强度（数值范围需满足设备支持）
  - fn_lock：Fn锁定开关（布尔）
  - touchpad_lock：触摸板锁定开关（布尔）
  - gpu_power_limit：GPU功耗限制（瓦特）
  - cpu_boost：CPU加速策略（枚举：禁用/启用/自动）
  - fan_curve：风扇曲线配置（数组或预设索引）
  - power_mode：电源模式（静音/均衡/高性能）
  - wmi_command：WMI命令名（字符串），配合options传递参数

- 请求体字段
  - target：必需，字符串，控制目标标识
  - value：根据target类型而定，数值、布尔或对象
  - options：可选，对象，传递额外参数（如WMI参数、EC寄存器地址等）

- 响应体字段
  - success：布尔，是否成功
  - message：字符串，简要描述（成功或失败原因）
  - data：可选，返回当前状态或执行结果摘要

- 参数验证与边界检查
  - 数值型：检查范围（如keyboard_backlight在[0,100]或设备支持区间）
  - 布尔型：仅接受true/false
  - 枚举型：限定集合内取值
  - 对象型：校验必填字段存在且类型匹配
  - 边界检查：超出设备能力的值应拒绝并返回错误

- 权限与安全
  - 需要管理员权限执行系统级控制（WMI、EC写入）
  - 建议启用身份认证与授权（如Bearer Token）
  - 对敏感控制（如功耗限制、风扇曲线）增加二次确认或白名单
  - 输入参数必须进行严格过滤，防止注入与越界

- 错误处理策略
  - 参数缺失：返回400并提示缺少字段
  - 类型不匹配：返回400并提示类型错误
  - 超出范围：返回400并提示范围错误
  - 设备不可用：返回503并提示设备离线
  - 执行失败：返回500并携带错误码与建议

**章节来源**
- [Douzhanzhe.API.http:1-200](file://server/api/Douzhanzhe.API.http#L1-L200)
- [dev-api.md:1-200](file://docs/dev-api.md#L1-L200)
- [dev-ec-map.md:1-200](file://docs/dev-ec-map.md#L1-L200)

### EC寄存器写入机制
- 适用场景
  - 键盘背光、Fn锁定、触摸板锁定等需要直接访问嵌入式控制器的设置
- 写入流程
  - 通过DriverBridge与底层驱动通信
  - 按照EC映射表定位寄存器地址与位域
  - 执行写入前进行校验（目标寄存器是否可写、位掩码是否合法）
  - 返回写入结果并更新内部状态缓存

```mermaid
flowchart TD
Start(["进入EC写入"]) --> Validate["校验target/value/options"]
Validate --> Valid{"参数有效?"}
Valid --> |否| Err400["返回400错误"]
Valid --> |是| Locate["解析EC寄存器地址与位域"]
Locate --> Write["执行写入操作"]
Write --> Result{"写入成功?"}
Result --> |否| Err500["返回500错误"]
Result --> |是| Update["更新状态缓存"]
Update --> Done(["返回成功"])
```

**图表来源**
- [DriverBridge.cs:1-200](file://server/hal/DriverBridge.cs#L1-L200)
- [dev-ec-map.md:1-200](file://docs/dev-ec-map.md#L1-L200)

**章节来源**
- [DriverBridge.cs:1-200](file://server/hal/DriverBridge.cs#L1-L200)
- [dev-ec-map.md:1-200](file://docs/dev-ec-map.md#L1-L200)

### WMI命令执行机制
- 适用场景
  - 系统级电源策略、设备状态查询与切换
- 执行流程
  - 通过WmiInterface封装WMI调用
  - 校验命令名称与参数合法性
  - 异步执行并等待结果
  - 将结果映射为API响应

```mermaid
sequenceDiagram
participant API as "API端点"
participant WMI as "WmiInterface"
participant OS as "操作系统WMI服务"
API->>WMI : "Execute(command, params)"
WMI->>OS : "发起WMI调用"
OS-->>WMI : "返回执行结果"
WMI-->>API : "封装结果并返回"
```

**图表来源**
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)

**章节来源**
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)

### 控制器分派与执行
- HardwareAbstractionLayer负责根据target选择具体控制器
- GpuController：处理GPU相关控制（功耗、温度、风扇曲线）
- SmuController：处理系统管理单元相关控制（PPT、频率）
- CpuAffinityManager：处理CPU亲和性与调度策略
- DriverBridge：处理EC寄存器写入
- WmiInterface：处理WMI命令

```mermaid
classDiagram
class HardwareAbstractionLayer {
+dispatch(target, value, options)
+getState()
}
class GpuController {
+setPowerLimit(value)
+setFanCurve(curve)
}
class SmuController {
+setPPT(value)
+setFrequency(freq)
}
class CpuAffinityManager {
+setAffinity(mask)
+setBoost(mode)
}
class DriverBridge {
+writeEC(address, value)
}
class WmiInterface {
+execute(command, params)
}
HardwareAbstractionLayer --> GpuController : "分派"
HardwareAbstractionLayer --> SmuController : "分派"
HardwareAbstractionLayer --> CpuAffinityManager : "分派"
HardwareAbstractionLayer --> DriverBridge : "分派"
HardwareAbstractionLayer --> WmiInterface : "分派"
```

**图表来源**
- [HardwareAbstractionLayer.cs:1-200](file://server/hal/HardwareAbstractionLayer.cs#L1-L200)
- [GpuController.cs:1-200](file://server/hal/GpuController.cs#L1-L200)
- [SmuController.cs:1-200](file://server/hal/SmuController.cs#L1-L200)
- [CpuAffinityManager.cs:1-200](file://server/hal/CpuAffinityManager.cs#L1-L200)
- [DriverBridge.cs:1-200](file://server/hal/DriverBridge.cs#L1-L200)
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)

**章节来源**
- [HardwareAbstractionLayer.cs:1-200](file://server/hal/HardwareAbstractionLayer.cs#L1-L200)
- [GpuController.cs:1-200](file://server/hal/GpuController.cs#L1-L200)
- [SmuController.cs:1-200](file://server/hal/SmuController.cs#L1-L200)
- [CpuAffinityManager.cs:1-200](file://server/hal/CpuAffinityManager.cs#L1-L200)
- [DriverBridge.cs:1-200](file://server/hal/DriverBridge.cs#L1-L200)
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)

### 典型控制操作示例
- 设置键盘背光强度
  - 请求：target=keyboard_backlight, value=75
  - 响应：success=true, message="设置成功"
- 启用Fn锁定
  - 请求：target=fn_lock, value=true
  - 响应：success=true, message="已锁定Fn键"
- 设置GPU功耗上限
  - 请求：target=gpu_power_limit, value=170
  - 响应：success=true, data={current_limit: 170}
- 执行WMI命令
  - 请求：target=wmi_command, value="SetPowerPlan", options={plan: "HighPerformance"}
  - 响应：success=true, message="电源计划已切换"

**章节来源**
- [Douzhanzhe.API.http:1-200](file://server/api/Douzhanzhe.API.http#L1-L200)
- [dev-api.md:1-200](file://docs/dev-api.md#L1-L200)

## 依赖关系分析
- 组件耦合
  - API层仅依赖HAL层接口，保持低耦合
  - HAL层内部控制器相互独立，通过统一调度解耦
- 外部依赖
  - WMI接口依赖操作系统WMI服务
  - DriverBridge依赖底层驱动（需管理员权限）
- 潜在循环依赖
  - 当前结构无循环依赖，分层清晰

```mermaid
graph LR
API["API端点"] --> HAL["硬件抽象层"]
HAL --> GPU["GPU控制器"]
HAL --> SMU["SMU控制器"]
HAL --> CPU["CPU亲和性管理"]
HAL --> DRV["驱动桥接"]
HAL --> WMI["WMI接口"]
```

**图表来源**
- [Program.cs:1-200](file://server/api/Program.cs#L1-L200)
- [HardwareAbstractionLayer.cs:1-200](file://server/hal/HardwareAbstractionLayer.cs#L1-L200)
- [GpuController.cs:1-200](file://server/hal/GpuController.cs#L1-L200)
- [SmuController.cs:1-200](file://server/hal/SmuController.cs#L1-L200)
- [CpuAffinityManager.cs:1-200](file://server/hal/CpuAffinityManager.cs#L1-L200)
- [DriverBridge.cs:1-200](file://server/hal/DriverBridge.cs#L1-L200)
- [WmiInterface.cs:1-200](file://server/api/WmiInterface.cs#L1-L200)

**章节来源**
- [Program.cs:1-200](file://server/api/Program.cs#L1-L200)
- [HardwareAbstractionLayer.cs:1-200](file://server/hal/HardwareAbstractionLayer.cs#L1-L200)

## 性能考量
- 控制延迟
  - EC写入与WMI调用通常在毫秒级，避免频繁高频调用
- 并发控制
  - 对同一目标的并发请求应串行化，防止竞态条件
- 缓存策略
  - 对读取类操作（如当前功耗、温度）可缓存以减少重复查询
- 资源占用
  - 避免在控制路径中执行高开销任务（如大量日志输出）

## 故障排除指南
- 常见错误与处理
  - 400错误：检查请求体字段是否完整、类型是否正确、数值是否在允许范围内
  - 500错误：查看WMI或驱动调用异常，确认管理员权限
  - 503错误：设备离线或驱动未加载，重试或重启服务
- 调试建议
  - 开启API日志，记录请求与响应
  - 使用Douzhanzhe.API.http测试端点，逐步缩小问题范围
  - 参考EC映射与WMI命令文档，核对参数

**章节来源**
- [Douzhanzhe.API.http:1-200](file://server/api/Douzhanzhe.API.http#L1-L200)
- [dev-api.md:1-200](file://docs/dev-api.md#L1-L200)

## 结论
POST /api/control提供了统一的硬件控制入口，通过HAL层与WMI/驱动桥接实现对键盘背光、Fn锁定、触摸板锁定、GPU功耗限制等目标的可控操作。严格的参数验证、权限控制与错误处理确保了系统的稳定性与安全性。建议在生产环境中启用身份认证、最小权限原则与审计日志。

## 附录
- 参考控制台与厂商工具链
  - 参考控制台文档可用于交叉验证与逆向工程
- EC寄存器映射
  - EC寄存器写入需严格遵循映射表，避免破坏设备功能

**章节来源**
- [reference-consoles.md:1-200](file://docs/reference-consoles.md#L1-L200)
- [dev-ec-map.md:1-200](file://docs/dev-ec-map.md#L1-L200)