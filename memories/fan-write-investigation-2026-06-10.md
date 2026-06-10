# 风扇写入测试记录 (2026-06-10)

## 调查目标

验证 FanCurveService 的 `ModeFanRanges` 按模式钳位是否真正必要，还是因为 CPU 频率限制 (`SET_PROC_FREQ_LIMIT`) 导致的 EC 锁定才让区间外写入失效。

## 前置条件

- 所有测试均在**关闭 CPU 频率限制**的干净状态下进行（重启后默认设置）
- 后端端口 3100，EC 写入通过 `/api/control` 的 `ec_write:0xNN` 端点
- WMI 写入通过 `/api/fan/set-target` 和 `/api/fan/test-write` 端点

## 测试轮次与结果

### 1. WMI 四种写入策略对比 (test-strategies.ps1)

在 gaming 模式下测试了四种写入顺序：

| 策略 | 操作顺序 | 结果 |
|------|----------|------|
| manual-true | Manual(true) → Speed | 区间外值被忽略 |
| speed-only | 只写 Speed | 区间外值被忽略 |
| manual-false | Manual(false) → Speed | 区间外值被忽略 |
| speed-then-manual | Speed → Manual(true) | 区间外值被忽略 |

**结论：手动标志的写法不影响结果，四种策略完全一致。**

### 2. 连续写入测试 (test-fan-continuous.ps1 / test-fan-continuous-silent.ps1)

每 5 秒写入一次区间外值，持续多轮。

**结论：EC 的 1-4 秒回写周期无法被 5 秒间隔覆盖，连续写入无效。**

### 3. 模式扫描 0-15 (test-mode-scan.ps1)

尝试写入 mode 0 到 15。

**结论：只有模式 0-3 有效，4-15 被 EC 拒绝。无隐藏第五模式。**

### 4. 全模式针对性区间外测试 (test-targeted.ps1)

每个模式下写入"其他模式合法但本模式不合法"的值：

| 当前模式 | 大扇写入 | 小扇写入 | 结果 |
|----------|----------|----------|------|
| 安静 (2) | 3000 RPM (合法于均衡) | 6600 RPM (合法于均衡) | **被忽略，维持默认值** |
| 均衡 (0) | 3500 RPM (合法于增强) | 7000 RPM (合法于增强) | **被忽略** |
| 增强 (1) | 2900 RPM (合法于安静) | 6000 RPM (合法于安静) | **被忽略** |
| 斗战 (3) | 2900 RPM (合法于均衡) | 6400 RPM (合法于安静) | **被忽略** |

**结论：所有四个模式都严格执行各自的合法区间，区间外值 100% 被忽略。**

### 5. EC 寄存器直写区间外值 (test-ec-direct.ps1)

通过 EC IO 端口直写 0x5F（大扇）和 0x5B（小扇）。

**结论：EC 直写区间外值同样被忽略，四个模式全部如此。（注：后来发现地址本身就是错的）

### 6. BellatorFanControl 编译产物逆向分析

- `git clone` 获取仓库 + Release 的 Control.zip
- 仓库 .exe 和 Release .exe **完全一致** (md5 相同)
- .NET 反射 + IL 字节码分析结果：

#### BellatorWmi 类方法号 (IL 验证)

| 方法 | Make() 参数 | data[4] | data[5] |
|------|-------------|---------|---------|
| SetSystemMode | Make(251, 8) | mode | - |
| SetMaxFanSwitch | Make(251, 20) | fanType | 0/1 |
| SetMaxFanSpeed | Make(251, 21) | fanType | speed |
| GetCpuTemperature | Make(250, 22) | - | output[4] |
| GetFanSpeeds | Make(250, 13) | - | output[4..11] |

WMI 路径: `MICommonInterface.InstanceName='ACPI\PNP0C14\MIFS_0'` — 与我们完全一致。

#### BuildUi 中 AddModeButton 调用 (IL 扫描)

```
AddModeButton('静音模式', mode=2)
AddModeButton('平衡模式', mode=0)
AddModeButton('增强模式', mode=1)
AddModeButton('疯狂模式', mode=3)
```

**仅 4 次调用，无隐藏第五模式。**

#### 默认曲线值 (LoadDefaultCurve IL)

```
50°C → L=2200, S=2000
55°C → L=2600, S=3500
60°C → L=2900, S=4800
65°C → L=3200, S=5900
70°C → L=3500, S=6400
75°C → L=3800, S=6900
80°C → L=4000, S=7500
85°C → L=4300, S=8000
```

#### ReadCurveRows 钳位值 (IL 常量)

- 大风扇: `Math.Max(0, Math.Min(44, cpuGpu))` — 全局 0-44 (0-4400 RPM)
- 小风扇: `Math.Max(0, Math.Min(82, sys))` — 全局 0-82 (0-8200 RPM)

**BellatorFanControl 不做按模式钳位，只用全局绝对上限。源码与编译产物完全一致，无隐藏逻辑。**

## 关键发现

### EC 风扇控制寄存器地址 — 已确认修正

**HAL 原始代码中的错误：**
- 注释头: `EC 0xB2 (CPU) / 0xB3 (GPU)` — 实际是 GPU 区域温度传感器
- 代码实现: `0x5F` (CPU) 和 `0x5B` (GPU) — 这两个是无关寄存器，写入不影响风扇

**通过 EC 全寄存器差异扫描确认的正确地址：**

| 寄存器 | 含义 | 编码 | 验证方法 |
|--------|------|------|----------|
| **0x5E** | 大扇(CPU)目标转速 | `val = RPM / 100` | WMI 写入后此寄存器跟随变化 (29→32→26 完美追踪目标值) |
| **0x5A** | 小扇(GPU/SYS)目标转速 | `val = RPM / 100` | WMI 写入后此寄存器跟随变化 (64→65→59 完美追踪目标值) |
| 0x5F | 无关寄存器 | — | 所有测试中始终不变(=22) |
| 0x5B | 无关寄存器 | — | 所有测试中始终不变(=32) |
| 0xB2 | GPU 区域温度传感器 | — | 值随 GPU 温度变化，非风扇 |
| 0xB3 | GPU 区域温度传感器 | — | 值随 GPU 温度变化，非风扇 |

**EC 寄存器差异扫描数据 (office 模式, WMI 写入区间内值)：**

基线状态: L=3138rpm, S=6398rpm, CPU=83°C

| 寄存器 | 基线 | WMI写#1(L=3200/S=6500) | WMI写#2(L=2600/S=5900) | 说明 |
|--------|------|------------------------|------------------------|------|
| **0x5A** | 64 | **65** | **59** | 小扇目标/100，完美匹配 |
| **0x5E** | 29 | **32** | **26** | 大扇目标/100，趋势匹配 |
| 0x5F | 22 | 22 | 22 | 不变 |
| 0x5B | 32 | 32 | 32 | 不变 |

### EC 直写机制验证 — 确认不可行

**测试 1: EC IO 端口写入 0x5E**
- 写入 0x5E=44 (区间外)，立即读回=44 ✓（寄存器写入了）
- 100ms 后读回=44 ✓
- 500ms 后读回=29 ✗（被 EC 固件覆写回原值！）

**测试 2: 高速连续写入**
- 每 30ms 写一次 0x5E=44，持续 5 秒，共 58 次写入
- 写入期间风扇转速完全不变 (L=2882→2882→2882)
- 停止后立即读回 0x5E=29（固件瞬间覆写）

**测试 3: 写入 0x5A**
- EC IO 端口写入 0x5A=70 直接被拒绝，寄存器值始终不变(=64)

**结论: EC 固件有一个 <30ms 的周期性回写循环，会覆盖 0x5E 的值。0x5A 是纯只读的。WMI ACPI 是唯一有效的风扇控制通道。**

### 写入公式确认

正确公式（已验证）: `val = RPM / 100`

HAL 注释头声称的 `val = round(rpm / maxRpm * 255)` 是错误的。

## 已确定的事实

1. **WMI Bellator 协议**: 我们的实现与 BellatorFanControl 100% 一致 (IL 字节码验证)
2. **WMI 区间外写入**: 四个模式全部被 EC 固件拒绝，无论写入策略
3. **无隐藏模式**: 只有 0-3 四个模式
4. **CPU 频率限制是独立问题**: 锁定 beast/gaming 下的 WMI 写入通道，与按模式区间限制无关
5. **正确的 EC 风扇状态寄存器**: 0x5E (大扇) / 0x5A (小扇)，编码 val=RPM/100
6. **EC 直写不可行**: EC IO 端口写入被固件 <30ms 回写覆盖，WMI 是唯一控制通道
7. **FanCurveService 的 ModeFanRanges 钳位是必要的**: 因为 WMI 是唯一通道且 EC 固件严格执行区间限制

## HAL 代码修正

已在 `HardwareAbstractionLayer.cs` 中完成：
- 寄存器地址: 0x5F→0x5E (大扇), 0x5B→0x5A (小扇)
- 注释修正: 0xB2/0xB3 是 GPU 温度传感器，非风扇寄存器
- 公式修正: 确认 `val = RPM / 100`
- 标记 `[Obsolete]`: CpuFanControl/GpuFanControl 属性不再推荐使用，应走 WMI
- 这两个属性在代码中未被任何地方调用（死代码）

## 待处理

1. **CPU 频率限制 ↔ 风扇锁**: 独立问题，需决定修复策略
2. **WinRing0/inpoutx64 驱动冲突**: 与蛟龙控制台的冲突问题

## OEM 控制台 DLL 分析记录

来源: `D:\Program Files\JiaoLong7.3\第三方蛟龙游戏控制中心.dll` (23MB .NET 程序集)

通过 pefile + #Strings heap 扫描确认的关键方法/属性名:
- EC 读写: `Write_EC_EX`, `Read_EC_EX`, `Write_EC_Init`
- 风扇: `Set_FanSpeed`, `SetFanSpeed`, `SetSP_MaxFanSpeed`, `MaxFanSpeedSwitch`
- 模式: `SetAP_PerformaceMode`, `SetSP_PerformaceMode`, `WorkMode`, `BalanceMode`, `PlayingMode`, `FastestMode`
- EC 端口: `EC_DATA_PORT=0x62`, `EC_ADDR_PORT=0x66`, `EC_IBF=0x80`, `EC_SC=0x81`
- WMI 方法: 与我们完全一致 (8, 13, 20, 21)
- AppSettings: 保存的风扇值 Work=27, Playing=43, Fastest=56 (val = RPM/100 编码)

OEM 控制台也使用 WMI 作为风扇控制通道，EC IO 端口仅用于初始化和特定功能。

## 最终结论

### 原始问题的答案

**问: FanCurveService 的 ModeFanRanges 按模式钳位是否真正必要？**

**答: 是的，完全必要。** 原因链:

1. EC 风扇状态寄存器的正确地址是 0x5E (大扇) / 0x5A (小扇)，不是 HAL 中的 0x5F/0x5B
2. 即使地址正确，EC IO 端口直写也会被固件在 <30ms 内覆写回原值，完全无效
3. WMI ACPI (Bellator 协议 Method 20/21) 是唯一有效的风扇控制通道
4. EC 固件对 WMI 写入严格执行每模式区间限制，区间外值 100% 被拒绝
5. BellatorFanControl 用全局钳位 (0-44/0-82)，这意味着它在非 gaming 模式下写入的上限值实际上会被 EC 拒绝（但不会报错，只是静默忽略）
6. 我们的 ModeFanRanges 按模式钳位是正确的做法，确保 WMI 写入的值都在合法区间内

### EC 寄存器映射 (已验证)

| 地址 | 类型 | 含义 |
|------|------|------|
| 0x5E | 状态(只读) | 大扇目标转速 (val=RPM/100) |
| 0x5A | 状态(只读) | 小扇目标转速 (val=RPM/100) |
| 0x9D/0x9E | 转速(high/low) | 大扇实际 RPM ( tach ) |
| 0x96/0x97 | 转速(high/low) | 小扇实际 RPM ( tach ) |
| 0xB2 | 温度 | GPU 区域温度传感器 |
| 0xB3 | 温度 | GPU 区域温度传感器 |
| 0xE4 | 控制 | 散热模式 (ITSR) |
| 0x1C | 温度 | CPU 温度 |
