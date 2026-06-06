# EC 寄存器映射

> DSDT 反编译确认 | OperationRegion (ECF2, SystemMemory, 0xFE800400, 0xFF)
>
> **📋 更新规则**：
> - 新鉴定的寄存器 → 追加到对应功能章节，注明地址/公式/已验证状态
> - 验证通过的路径标记 ✅，无效的标记 ❌
> - 废弃的寄存器假设 → 加 ~~删除线~~ 保留供历史参考
> - 同步更新主记忆 §1 文档地图中 `dev-ec-map.md` 的描述

[TOC]

## 物理内存基地址
| 区域 | 基地址 | 大小 | 来源 |
|------|--------|------|------|
| EC 寄存器 | 0xFE800400 | 0xFF | DSDT ECF2 |
| USB Type-C | 0xFE800A80 | 0x80 | SSDT USBD |
| dGPU 控制 | 0xFED81E40 + (Arg0<<1) | 0x02 | DSDT DSAD |

## EC 寄存器偏移表
| 偏移 | 物理地址 | 名称 | 尺寸 | 类型 | 说明 | 验证状态 |
|------|----------|------|------|------|------|----------|
| 0x10 | FE800410 | EVMR/EVMN | 8+8 | 只读 | 事件 | ❌ |
| 0x18-1F | FE800418 | TSR1-7 | 8×7 | 只读 | 温度传感器 | ❌ |
| 0x20 | FE800420 | LSTE(bit0)/FNHK(bit3)/CRHK(bit5)/OCFL(bit6) | bits | 读写 | Fn 锁/快捷键 | ✅ WriteBit (SetPhysLong) |
| 0x22 | FE800422 | LCPU/GSTS | 8+8 | 只读 | CPU状态 | ❌ |
| 0x25 | FE800425 | TOCP/CALK/NULK/FNRC/ALTQ | bits | 只读 | 大写/数字/Fn**状态指示**（键盘控制器单向通知，写入需 SendKeys） | ✅ 已通过 Win32 keybd_event (SendKeys) 实现，CapsLock/NumLock 切换 + OSD 正常 |
| 0x28 | FE800428 | SMPR | 8 | 读写 | SMU 命令 | ❌ |
| 0x2C | FE80042C | SDAT | 16 | 读写 | SMU 数据 | ❌ |
| 0x58-5F | FE800458 | F3SF~FASQ | 8×8 | 读写 | 风扇状态 | ❌ |
| 0x60 | FE800460 | ECWR | 8 | 读写 | EC 写入状态 | ❌ |
| 0x96-97 | FE800496 | F3HI/LO | 16 | 只读 | **GPU 风扇 RPM** | ✅ ec_reader |
| 0x99 | FE800499 | KBTY | 8 | 只读 | 键盘类型 | ❌ |
| 0x9A | FE80049A | **KBNL** | 8 | **读写** | **键盘背光 0-3** | ✅ ec_kb_map |
| 0x9B-9C | FE80049B | F1HI/LO | 16 | 只读 | CPU 风扇 RPM | ✅ ec_reader |
| 0x9D-9E | FE80049D | F2HI/LO | 16 | 只读 | 中间风扇 RPM | ✅ ec_reader |
| 0xE0 | FE8004E0 | GPUT | 8 | 只读 | GPU 温度 | ❌ 加nvsmi |
| 0xE1 | FE8004E1 | CPUT | 8 | 只读 | CPU 温度 | ✅ ec_reader 0x1C |
| 0xE4 | FE8004E4 | ITSM | 8 | 读写 | 散热模式 (0=均衡,1=野兽,2=安静,3=斗战) | ✅ WritePhys (SetPhysLong) 写入有效 |
| 0xE8 | FE8004E8 | LEDM | 8 | 读写 | LED 模式 | ❌ |

## EC Index/Data 寄存器 (IO 端口 0x62/0x66 协议)
> 这些寄存器通过 EC IO 端口协议（0x66 命令→0x62 数据）读写，**不是物理内存映射**。
> 发现来源：旧版 ec_writer.cs (WinRing0x64.dll) 真机验证，提交 3fb31f0。

| 索引 | 功能 | 范围 | 写入方式 | 验证状态 |
|------|------|------|---------|---------|
| 0x58 | 小扇(战斗) 预设值 | 80 (= 8000 RPM) | 只读(预设表) | ✅ DSDT 反编译+差分验证 |
| 0x59 | 小扇(野兽) 预设值 | 69 (= 6900 RPM) | 只读(预设表) | ✅ DSDT 反编译+差分验证 |
| 0x5A | 小扇(均衡) 预设值 | 64 (= 6400 RPM) | 只读(预设表) | ✅ DSDT 反编译+差分验证 |
| **0x5B** | **小扇(GPU) 只读状态寄存器** | **0-82 (×100=RPM)** | **`WriteEc(0x5B, val)`** | **❌ 物理风扇不响应；改用 WMI MaxFanSpeed(21)** |
| 0x5C | 大扇(斗战) 预设值 | 43 (= 4300 RPM) | 只读(预设表) | ✅ DSDT 反编译+差分验证 |
| 0x5D | 大扇(野兽) 预设值 | 35 (= 3500 RPM) | 只读(预设表) | ✅ DSDT 反编译+差分验证 |
| 0x5E | 大扇(均衡) 预设值 | 29 (= 2900 RPM) | 只读(预设表) | ✅ DSDT 反编译+差分验证 |
| **0x5F** | **大扇(CPU) 只读状态寄存器** | **0-255 (×100=RPM)** | **`WriteEc(0x5F, val)`** | **❌ 物理风扇不响应；改用 WMI MaxFanSpeed(21)** |
| 0xB2 | 大风扇(CPU) 转速控制 ❌ | 0-255 (源自 LLT ec_writer.cs) | `WriteEc(0xB2, val)` | ❌ LLT 参考，本机写入回读成功但风扇**无物理响应** |
| 0xB3 | 小风扇(GPU) 转速控制 ❌ | 0-255 (源自 LLT ec_writer.cs) | `WriteEc(0xB3, val)` | ❌ LLT 参考，本机写入回读成功但风扇**无物理响应** |

> **转速公式（0x5F 本机已验证）**：字节值 = RPM目标值 / 100
> 例：大扇设 2200RPM → val = 22 (0x16)
> 例：大扇设 2700RPM → val = 27 (0x1B)
>
> **注意**：风扇受散热模式区间限制：
> - 安静: 1900-2900 (val=19-29)
> - 均衡: 2600-3500 (val=26-35)
> - 野兽: 3200-3800 (val=32-38)
> - 斗战: 4000-4400 (val=40-44)
>
> 超出区间上限的值会被 EC 截断为区间上限。
>
> ✅ **小风扇控制寄存器同 0x5B** — `WriteEc(0x5B, val=RPM/100)` 直接生效（和大扇 0x5F 行为一致）

## IO 端口
| 端口 | 协议 | 说明 |
|------|------|------|
| 0x62 | EC 数据 | EC_DATA (读/写) |
| 0x66 | EC 命令 | EC_SC (0x80=读命令) |
| 0x72 | APM 命令 | APMD (SMI 触发) |
| 0x73 | APM 触发 | APMC = 0xE4 触发 SMI |

## EC 协议时序
`
读: Write(0x66, 0x80) → Sleep(2ms) → Write(0x62, reg) → Sleep(5ms) → Read(0x62)
写: Write(0x66, 0x81) → 等待 IBF 空 → Write(0x62, reg) → 等待 IBF 空 → Write(0x62, val)
    - 0x81 = EC RAM 写入命令（旧版误用 0x80 读命令导致从未生效）
    - IBF 轮询: 读取 0x66, 检查 bit1(IBF)=0 时继续
    - 写入后读回验证：发出 0x80 读命令 → 发地址 → 读 0x62 数据
`

## 写入注意事项
- IO 端口写入 (0x62) 在某些实现中无效，需要使用物理内存 (MapPhysToLin/SetPhysLong)
- 预映射缓存的指针写入无效，必须单地址映射或 SetPhysLong
- SetPhysLong 只能处理 32 位物理地址

## EC 写入教训与原则

### 1. 寄存器写入策略优先级
| 方法 | 适用场景 | 验证状态 |
|------|---------|---------|
| `WritePhys(SetPhysLong)` | 物理内存写入，地址 < 4GB | ✅ KBNL(0x9A) 已验证 |
| `ReadEc(IO端口)` → 改位 → `WritePhys(SetPhysLong)` | 需先读取当前状态，再写入 | ✅ FnLock(0x20 bit3) 已验证 |
| `WriteBit(ReadPhys→改位→WritePhys)` | ⚠️ **避免使用** — ReadPhys 预映射缓存指针解引用抛 NRE | ❌ 踩坑 |
| `WriteEc(IO端口协议 0x62/0x66)` | 某些 EC 寄存器不响应内存写入时备用 | ⚠️ 部分寄存器不生效 |

### 2. 状态通知寄存器（只读）
某些 EC 寄存器（如 0xFE800425）是**键盘控制器/EC 硬件的状态通知出口**。
- EC/键盘控制器**单向写入**这些地址来报告状态
- 外部写入（无论 IO 端口还是物理内存）**不产生任何硬件效果**
- 这些功能需要通过 Windows API 实现：`keybd_event` / `SendKeys` / `SendMessage`

### 3. 已确认只读的功能
| 寄存器 | 功能 | 替代写入方案 |
|--------|------|------------|
| 0xFE800425 bit1 | CapsLock | `[System.Windows.Forms.SendKeys]::SendWait('{CAPSLOCK}')` | ✅ 已通过 C# HAL `Console.CapsLock` + `keybd_event` 真机验证 |
| 0xFE800425 bit2 | NumLock | `[System.Windows.Forms.SendKeys]::SendWait('{NUMLOCK}')` | ✅ 已通过 C# HAL `Console.NumberLock` + `keybd_event` 真机验证 |

### 4. Windows API vs EC IO 策略
- **EC IO/物理内存写入**：适用于 EC 原生将写入映射为硬件动作的寄存器（KBNL键盘背光、FNHK Fn锁、ITSM散热模式）
- **Windows API 写入**：适用于操作系统级功能（CapsLock/NumLock/OBS等受Windows输入系统管理的功能）

## DSDT 反编译完整区域 (2026-06-04 重新提取)

> DSDT 文件：`_dsdt_orig.aml` (34KB, 从注册表 HKLM\\HARDWARE\\ACPI\\DSDT\\INSYDE 提取)

### 所有 SystemMemory OperationRegion
| 区域 | 物理地址 | 大小 | 用途 |
|------|---------|------|------|
| ECF2 | 0xFE800400 | 0xFF | EC 寄存器主区域 (当前正在使用) |
| ECF3 | 0xFE800B00 | 0xFF | 第二 EC RAM 区 (未使用) |
| FACR | 0xFED81E00 | 0x100 | ACPI 功能寄存器 (含 DSAD) |
| GSMG | 0xFED81500 | ~0x400 | GSMI 通信区 |
| GSMM | 0xFED80000 | 0x1000 | GSMI 共享内存 |
| SMIC | 0xFED80000 | 0x8000 | SMI 通信区 |
| IOMX | 0xFED80D00 | 0x100 | IO MUX 配置 |
| LUIE | 0xFEDC0020 | 0x4 | USB-C/EC 事件 |
| BLDN | ~0xF7DB18 | — | 设备信息 (DGDS/GPUT/UMAD) |
| GNVS | ~0xBAF398 | — | ACPI 全局变量 |
| ADCR | (动态) | 0x02 | DSAD 方法的 OperationRegion (见下方) |

### DSAD 方法体解析
```
DSAD (Arg0) — 双字节操作 (Arg0 << 1 + 0xFED81E40)
Field ADCR {
    ADTD, 2,     // bit 1:0 — 动作类型
    ADPS, 1,     // bit 2   — 动作电源状态
    ADPD, 1,     // bit 3   — 电源下电 (1=断电)
    ADSO, 1,     // bit 4
    ADSC, 1,     // bit 5
    ADSR, 1,     // bit 6
    ADIS, 1,     // bit 7
    ADDS, 3,     // bit 10:8
}
```
- **Arg0=0x0B** → 地址 `0xFED81E56` → 控制 dGPU 电源 (ADPD bit3)
  - ADPD=1 → dGPU 断电 (集显模式)
  - ADPD=0 → dGPU 通电 (混合/独显)
- **GSMI 方法**: `Store(Arg0, APMD); Store(0xE4, APMC); Sleep(2)` — SMI 触发 (IO 0x72/0x73)

### 已知不可访问的区域
| 地址范围 | 原因 | 验证方式 |
|---------|------|---------|
| 0xFED81E00-0xFED81EFF | inpoutx64 的 SetPhysLong 不支持该物理地址范围 | AccessViolation |
| 0xFE800B00-0xFE800BFF | 同上 | 返回 0 |
| 0xFED81500+ | 同上 | 返回 0 |

---
> 项目主记忆：[douzhanzhe-progress.md](.github/copilot-instructions.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
