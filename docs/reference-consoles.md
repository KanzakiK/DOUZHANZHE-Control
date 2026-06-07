# 官方控制台参考

> 各官方/第三方控制台的完整功能详情、依赖关系和功能对比。仅作开发参考和功能对标，项目不直接引用外部文件。
>
> **📋 更新规则**：
> - 新增参考项目 → 在新章节追加（按连续编号）
> - 功能详情/依赖变更 → 更新对应章节的表格
> - 发现无效路径 → 在结论表中追加行并标记 ❌
> - 同步更新主记忆 §3 `参考项目` 表

---

## 速查表

| 章节 | 内容 |
|:-----|:-----|
| §1 | 斗战者控制台 — WMI 枚举、风扇写入路径、功能详情 |
| §2 | 蛟龙控制台 — WinRing0 驱动改造版、功能详情、KaronOC.dll 逆向、CPU 控制逆向 |
| §3 | BellatorFanControl — WMI MiInterface 协议、风扇曲线算法 |
| §4 | 运行时依赖关系 |
| §5 | 功能对比 + BLDFnHotkeyUtility 反编译 |

---


## 1. 斗战者官方控制台功能详情

### CPU 监控区
- **CPU 使用率仪表盘**：动态显示当前 CPU 整体负载百分比。
- **CPU 核心数看板**：显示当前处理器的物理与逻辑核心总数。
- **CPU 实时频率**：显示当前处理器核心的实时运行主频 (GHz)。
- **CPU 实时温度**：显示当前处理器核心的实时温度 (°C)。
- **CPU 负载历史曲线图**：以折线图形式展现近期 CPU 负载的波动和变化趋势。

### GPU 监控区
- **GPU 使用率仪表盘**：动态显示当前独立显卡整体负载百分比。
- **显存容量看板**：显示当前独立显卡的物理显存总容量 (GB)。
- **GPU 实时频率**：显示当前独立显卡核心的实时运行主频 (GHz)。
- **GPU 实时温度**：显示当前独立显卡核心的实时温度 (°C)。
- **GPU 负载历史曲线图**：以折线图形式展现近期 GPU 负载的波动和变化趋势。

### 风扇信息与控制区
- **恢复默认按钮**：一键清除手动调速，恢复官方预设的风扇转速曲线。
- **大风扇当前转速**：显示大风扇当前的实时转速 (RPM) 及占用百分比。
- **大风扇转速调节**：在预设区间内通过加减 (+/-) 按钮手动调整大风扇目标转速。
- **小风扇当前转速**：显示小风扇当前的实时转速 (RPM) 及占用百分比。
- **小风扇转速调节**：在预设区间内通过加减 (+/-) 按钮手动调整小风扇目标转速。

#### 各模式风扇转速区间 (官方预设)
| 模式 | 大风扇下限 | 大风扇上限 | 大风扇预设 | 小风扇下限 | 小风扇上限 | 小风扇预设 |
|:-----|:----------:|:----------:|:----------:|:----------:|:----------:|:----------:|
| 安静 | 1900 RPM | 2900 RPM | 2200 RPM | 1700 RPM | 6400 RPM | 2000 RPM |
| 均衡 | 2600 RPM | 3500 RPM | 2900 RPM | 5900 RPM | 6900 RPM | 6400 RPM |
| 野兽 | 3200 RPM | 3800 RPM | 3500 RPM | 6400 RPM | 7200 RPM | 6900 RPM |
| 斗战 | 4000 RPM | 4400 RPM | 4300 RPM | 7500 RPM | 8200 RPM | 8000 RPM |

> **说明**：每个散热模式下风扇转速可在预设区间内线性调节，切换模式后区间自动切换。此行为是官方 DLL 内部逻辑，不依赖 EC 寄存器映射。

### 存储信息监控区
- **内存占用环形图**：动态显示当前系统运行内存的占用百分比。
- **内存系统容量**：显示当前电脑安装的运行内存 (RAM) 总容量。
- **内存运行频率**：显示当前内存的实时运行频率 (MT/s)。
- **硬盘占用环形图**：动态显示当前主硬盘的空间占用百分比。
- **硬盘可用容量**：显示当前硬盘剩余的可使用空间 (GB)。
- **硬盘总容量**：显示当前主硬盘的总存储空间 (GB)。

### 性能设置区
- **五维性能雷达图**：直观展示当前性能模式在"CPU、GPU、节能、静音、散热"五个维度的平衡侧重点。
- **斗战模式按钮**：一键切换至官方最高性能输出档位，解锁高功耗。
- **野兽模式按钮**：一键切换至高能游戏档位，优化游戏帧率与散热平衡。
- **均衡模式按钮**：一键切换至日常办公与影音娱乐平衡档位，兼顾噪音与性能。
- **安静模式按钮**：一键切换至低功耗、低噪音档位，延长续航并保持静音。

### 常规设置区
- **独显直连开关**：切换屏幕信号由独立显卡直接输出，屏蔽核显以提升游戏帧率。
- **集显模式开关**：彻底关闭独立显卡仅使用处理器核显，以获得极致的省电与续航。
- **数字键锁定开关**：控制键盘右侧小键盘数字键的激活与关闭状态。
- **大写键锁定开关**：控制键盘 CapsLock 大写锁定的激活与关闭状态。
- **功能键锁定开关**：控制 Fn 键的锁定状态（切换 F1-F12 为传统功能键或多媒体快捷键）。
- **触摸板锁定开关**：一键禁用或启用笔记本自带的触摸板，防止游戏时手掌误触。
- **关闭 OSD 显示开关**：开启后，系统在切换模式或调节音量/亮度时，屏幕上不再弹窗提示。
- **键盘灯光亮度调节滑块**：通过滑动条线性调节键盘背光的整体明暗亮度。
- **屏幕颜色校准按钮**：一键跳转至屏幕色彩校正与显示器颜色调节界面。

### 安装与依赖
- **安装目录**：`C:\Program Files (x86)\斗战者控制台\`
- **关键 DLL**：`斗战者控制台.dll` → `BLD.WMIOperation.WMIMethodServices` (static 类)
- **定位**：仅作 WMI 协议参考（DLL 反编译枚举表）。项目自身通过 `WmiInterface.cs`（`System.Management` NuGet）直接调用 WMI MiInterface，无需部署斗战者控制台.dll。FnLock/TPLock 已由 C# HAL EC 直写替代，GPUMode 也已通过 WMI Method 9 直调。

### DLL 反编译：BLD.WMIOperation.WMIMethodServices
| 枚举 | 值 | 用途 |
|:-----|:---|:------|
| `WMIMethodName.SystemPerMode` | 8 | 散热模式切换 (0=均衡,1=野兽,2=安静,3=斗战) ✅ 已验证 |
| `WMIMethodName.GPUMode` | 9 | GPU 模式 (0=混合,1=集显,2=独显) ✅ 已验证 |
| `WMIMethodName.FnLock` | 11 | Fn 锁 ✅ 已验证 |
| `WMIMethodName.TPLock` | 12 | 触摸板锁 ✅ 已验证 |
| `WMIMethodName.CPUGPUSYSFanSpeed` | 13 | ❌ WMI 空壳，返回 OK 但不写 EC/文件 |
| `WMIMethodName.MaxFanSpeedSwitch` | 20 | ❌ 返回 OK 但硬件无响应 |
| `WMIMethodName.MaxFanSpeed` | 21 | ❌ 返回 OK 但硬件无响应 |
| `WMIMethodName.CPUThermometer` | 22 | CPU 温度读取 ✅ |
| `WMIMethodName.CPUPower` | 23 | CPU 功率读取 ✅ |
| `WMIFanType.CPUGPUFan` | 0 | 大扇(CPU) 类型标识 |
| `WMIFanType.SYSFan` | 1 | 小扇(SYS) 类型标识 |

> `SetValue(WMIMethodName, Object)` 有效（单字节），`SetValue(WMIMethodName, Byte[])` 对多数枚举无效。
> `ExcMethod(Byte[])` 是原始 WMI 字节协议通道（未封装枚举名）。

### 风扇写入路径探索结论 (2026-06-05)
| 路径 | 结果 | 说明 |
|:-----|:----:|:------|
| EC IO 0xB2/0xB3 (LLT ec_writer.cs 参考) | ❌ | 写入回读成功但风扇无物理响应 |
| EC 物理内存 0x5B/0x5F (差分扫描发现) | ❌ | 只读状态寄存器 |
| WMI `SetValue=CPUGPUSYSFanSpeed <Byte[]>` | ❌ | 空壳，返回 OK 但不控硬件 |
| WMI MaxFanSwitch(20)+MaxFanSpeed(21) (独立`SetValue`无FanType) | ❌ | 缺 data[4]=FanType，调用不生效 |
| WMI MaxFanSwitch(20)+MaxFanSpeed(21) (完整 Bellator 协议) | ✅ 区间内持久/区间外~8s覆盖 | data[4]=FanType(0大扇/1小扇), data[5]=RPM/100 |
| 直接写 `AppMachineInfo` (base64 RPM/100) | ❌ | 文件更新但硬件不响应 |
| KaronOC32.dll 原生导出 | ❌ | 仅 2 个 P-state 函数，无关风扇 |
| 实时 EC 差分扫描 | ⏳ | 待实施——需连续监控 |

> **风扇控制状态**：Bellator 完整协议（MaxFanSwitch+MaxFanSpeed+data[4]=FanType）已验证 ✅，散热区间内持久、区间外~8s 被固件覆盖。独立 `SetValue`（缺 data[4]）不生效。

## 2. 蛟龙游戏控制中心功能详情

### CPU 信息与底层微调区
- **CPU 使用率环形图**：动态显示当前 CPU 整体负载百分比。
- **CPU 实时温度**：显示当前处理器核心的实时温度 (°C)。
- **CPU 实时频率**：显示当前处理器核心的实时运行主频 (GHz)。
- **CPU 核心数看板**：显示当前处理器的核心总数规格。
- **限制频率 (MHz) 调节**：通过滑块或数值输入框，手动锁死或限制 CPU 允许达到的最高主频。
- **关闭睿频开关**：一键禁用 CPU 的自动加速睿频技术，强制使其运行在基础频率以降低发热。
- **温度墙调节 (°C)**：通过滑块或数值输入框，手动修改处理器触发过热降频的温度阈值。
- **限制内核数下拉框**：点击可展开菜单，手动关闭部分 CPU 核心，限制参与工作的内核数量。
- **电源管理下拉框**：点击可展开菜单，直接切换 Windows 系统底层的电源计划策略（如平衡、高性能）。
- **电压调节 (降压)**：通过滑块或数值输入框进行 Offset 调压，轻微降低核心电压以实现降温增效。
- **长时间功耗 (W) 限制**：即 PL1/SPL 功耗墙，手动微调 CPU 在持续高负载下的最大稳定功耗上限。
- **短时间功耗 (W) 限制**：即 PL2/fPPT 功耗墙，手动微调 CPU 在应对突发爆发负载时的瞬时最高功耗。
- **CPU 负载历史曲线图**：以折线图形式展现近期 CPU 负载的波动和变化趋势。

### GPU 信息与超频锁频区
- **GPU 使用率环形图**：动态显示当前独立显卡整体负载百分比。
- **GPU 实时温度**：显示当前独立显卡的实时温度 (°C)。
- **GPU 实时频率**：显示当前独立显卡核心的实时运行主频 (GHz)。
- **限制频率 (MHz) 调节**：通过滑块或数值输入框，手动限制显卡核心的最高允许运行频率。
- **显存容量看板**：显示当前独立显卡的显存及共享显存的总容量。
- **显卡超频 (MHz) 调节**：通过滑块或数值输入框，直接对 GPU 核心频率进行拉频超频，提升图形性能。
- **显存超频 (MHz) 调节**：通过滑块或数值输入框，直接对独立显存频率进行超频，增大显存带宽。
- **锁定频率开关及调节**：开启开关并输入固定数值，强制将 GPU 核心锁死在特定频率，消除游戏掉帧卡顿。
- **GPU 负载历史曲线图**：以折线图形式展现近期 GPU 负载的波动和变化趋势。

### 常规设置与自动化区
- **独显直连开关**：切换屏幕信号由独立显卡直接输出，屏蔽核显。
- **数字键锁定开关**：控制键盘右侧小键盘数字键的激活与关闭状态。
- **大写键锁定开关**：控制键盘 CapsLock 大写锁定的激活与关闭状态。
- **功能键锁定开关**：控制 Fn 键的锁定状态（切换传统功能键或多媒体快捷键）。
- **触摸板锁定开关**：一键禁用或启用笔记本自带的触摸板。
- **关闭 OSD 显示开关**：开启后，屏蔽系统切换状态时的屏幕弹窗提示。
- **开机自启开关**：开启后，该控制中心随 Windows 开机自动常驻后台，确保超频与降压参数自动生效。
- **温度曲线自启开关**：开启后，软件接管系统风扇，自动加载并应用自定义的"温度-转速"联动策略。
- **自适应模式开关**：开启后，软件根据当前运行的程序类型，在后台智能且动态地调整硬件功耗分配。

### 内存信息监控区
- **内存占用环形图**：动态显示当前系统运行内存的占用百分比。
- **内存系统容量**：显示当前电脑安装的运行内存 (RAM) 总容量。
- **内存运行频率**：显示当前内存的实时运行频率 (MHz)。

### 硬盘信息监控区
- **硬盘占用环形图**：动态显示当前主硬盘的空间占用百分比。
- **硬盘可用容量**：显示当前硬盘剩余的可使用空间 (GB)。
- **硬盘总容量**：显示当前主硬盘的总存储空间 (GB)。

### 风扇信息区
- **风扇占用环形图**：动态显示当前总体风扇转速的负载百分比。
- **当前转速**：显示当前风扇系统的实时综合转速 (RPM)。
- **最大转速调节**：通过加减 (+/-) 按钮手动调整风扇系统允许达到的最高转速上限。

### 性能设置区
- **性能模式快捷图标**：直观展示当前处于何种预设性能状态。
- **狂飙模式按钮**：一键开启极限硬件输出档位，配合超频参数使用。
- **游戏模式按钮**：一键切换至标准游戏预设，兼顾画面帧率与散热。
- **办公模式按钮**：一键切换至低功耗、低发热的日常办公档位。
- **自定义模式按钮**：一键激活并应用用户在上方手动调节的所有 CPU/GPU 超频、降压与功耗墙参数。
- **温度曲线模式按钮**：一键使硬件输出完全挂钩用户自定义的风扇散热曲线，按需控温。
- **安静性能模式按钮**：一键切换至特殊平衡档，在保证一定性能的前提下强制压低风扇噪音。

### 安装与依赖
- **安装目录**：`D:\Program Files\JiaoLong7.3\`
- **驱动**：WinRing0 (WinRing0_1_2_0.sys)
- **定位**：第三方改造版，.NET 未混淆，仅作开发参考

### KaronOC.dll 逆向分析 (2026-06-07)

> **文件**：`D:\Program Files\JiaoLong7.3\KaronOC.dll`（60256 bytes, x64 原生 C++ DLL）
> **另有**：`KaronOC32.dll`（x86 版本，功能相同）
> **PDB 路径**：`D:\work\Git\NvOCVerify\x64\Release\KaronOC.pdb`（蛟龙作者的 NVAPI 超频验证项目）

#### 导出函数
| 函数名 | 签名 | 说明 |
|:-------|:-----|:------|
| `ChangePstatesLevel0Settings` | `int(int coreOffsetMhz, int memOffsetMhz)` | GPU P0 超频写入（Cdecl, rcx=core, edx=mem） |
| `GetPstatesLevel0Settings` | `int(IntPtr gpuHandle, IntPtr outputBuf)` | GPU P0 状态读取 |

#### 内部 NVAPI 调用链
1. 使用 `nvapi_Direct_GetMethod` 替代标准 NVAPI 初始化流程
2. 通过 `nvapi_QueryInterface` + 函数 ID 获取 NVAPI 内部函数指针
3. 核心频率 MHz → kHz 转换：`imul ebp, ebp, 0x3e8`（乘以 1000）

| NVAPI 函数 | 函数 ID | KaronOC 结构体版本 | 结构体大小 |
|:-----------|:--------|:-----------------|:----------|
| `GPU_GetPStates20` | `0x6FF81213` | V3 | 7416 bytes |
| `GPU_SetPStates20` | `0x0F4DAE6B` | V2 | 7416 bytes |

#### 与我们直调 NVAPI 的差异
| 对比项 | 我们直调 NVAPI | KaronOC.dll |
|:-------|:--------------|:------------|
| 初始化方式 | `nvapi_Initialize()` 标准流程 | `nvapi_Direct_GetMethod` 直接获取 |
| SetPStates20 结构体 | V1 / 7316 bytes | V2 / 7416 bytes |
| RTX 5060 Laptop GPU 结果 | **返回 -104 (NOT_SUPPORTED)** | **返回 0 (SUCCESS)** |
| 超频实测 | ❌ 不可用 | ✅ core +150MHz / mem +300MHz 验证通过 |

#### 对我们的价值
- **已集成**：`NvapiGpuController.cs` 优先加载 KaronOC.dll 作为超频引擎（`OcEngine = "karonoc"`）
- **回退机制**：若 KaronOC 不可用，回退到直调 NVAPI SetPStates20（笔记本 GPU 通常失败）
- **搜索路径**：`D:\Program Files\JiaoLong7.3\` → `C:\Program Files\JiaoLong7.3\` → `AppContext.BaseDirectory`
- **宝龙达模具兼容**：斗战者笔记本与蛟龙控制台共享宝龙达 OEM 模具，KaronOC.dll 可直接使用


### CPU 控制技术逆向 (2026-06-07)

> **方法**：MetadataLoadContext 安全反射 + IL 字节码反编译（`_enum_dll_proj`）
> **目标**：`第三方蛟龙游戏控制中心.dll`（22MB，.NET 8 WPF 程序集）
> **核心发现**：频率/睿频/核心数走 Windows 电源管理 API；功耗/温度/电压走 ryzenadj→SMU

#### 底层通道概览

| 控件 | 底层技术 | 实现类 | 命令格式 |
|:-----|:---------|:------|:--------|
| 频率限制 | Windows powercfg | `NVIDIA.NvTuning.SetShowCPUMHz` | `powercfg /setacvalueindex ...` |
| 关闭睿频 | Windows powercfg | `NVIDIA.NvTuning.SetTuro` | `powercfg /setacvalueindex ...` |
| 核心数限制 | Windows powercfg (3项) | `NVIDIA.NvTuning.SetCpuCore` | `powercfg /setacvalueindex ...` |
| 功耗策略 | Windows powercfg | `NVIDIA.NvTuning.SetCPUPerformanceStrategy` | `powercfg /setacvalueindex ...` |
| PL1 功耗墙 | ryzenadj | `MainWindow.CPU_Power_Consumption` | `--slow-limit={W}` |
| PL2 功耗墙 | ryzenadj | `MainWindow.CPU_Power_PL_Consumption` | `--fast-limit={W}` |
| 温度墙 | ryzenadj | `MainWindow.Temperature_Wall` | `--tctl-temp=... --cHTC-temp=... --apu-skin-temp=...` |
| 电压偏移 | ryzenadj | `MainWindow.CPUVoltage` | `--set-coall={mV}` |

#### 频率限制 — `SetShowCPUMHz(mhz, flag)`

**SubGroup GUID**：`54533251-82be-4824-96c1-47b60b740d00`（Windows 处理器电源设置）  
**Setting GUID**：`75b0ae3f-bce0-45a7-8c89-c9611c25e100`（处理器频率限制，OEM 扩展）  
**范围**：1500 ~ 5400 MHz

**IL 反编译还原的完整执行流程**：
```
1. powercfg /overlaysetactive overlay_scheme_none          ← 关闭覆盖方案
2. await Task.Delay(100)                                    ← 等待方案切换
3. PowerGetActiveScheme(out schemeGuid)                     ← 获取当前方案
4. powercfg /setacvalueindex {schemeGuid} SubGroup Setting {mhz}   ← 设置 AC 频率上限
5. await Task.Delay(100)
6. powercfg /setdcvalueindex {schemeGuid} SubGroup Setting {mhz}   ← 设置 DC 频率上限
7. powercfg /setacvalueindex {schemeGuid} SubGroup Setting 0        ← 清除 AC 频率（归零）
8. await Task.Delay(100)
9. powercfg /setdcvalueindex {schemeGuid} SubGroup Setting 0        ← 清除 DC 频率（归零）
10. PowerSetActiveScheme(IntPtr.Zero, schemeGuid)           ← 重新激活方案
```

> ⚠️ IL 中发现先设置 `{mhz}` 后归零 `0` 的路径均无条件执行（无分支跳过），推测 flag 参数控制 MessageBox 显示逻辑，实际频率生效依赖 Windows 电源管理内部状态。

#### 关闭/启用睿频 — `SetTuro(flag)`

**Setting GUID**：`be337238-0d82-4146-a960-4f3749d470c7`（Processor performance boost mode）

| 操作 | AC/DC 值 | IL 流程 |
|:-----|:-------|:--------|
| 禁用睿频 (flag=false) | `0` | overlaysetactive → setacvalueindex=0 → setdcvalueindex=0 → PowerSetActiveScheme |
| 启用睿频 (flag=true) | `2` | overlaysetactive → setacvalueindex=2 → setdcvalueindex=2 → PowerSetActiveScheme |

> 标准 Windows 电源设置，值 0=禁用，2=激进模式（值 1=启用但未在代码中使用）

#### 核心数控制 — `SetCpuCore(corePercent)`

同时设置 **3 个电源参数**（均使用 corePercent 值）：

| Setting GUID | Windows 含义 | IL 中的用途 |
|:-------------|:-----------|:----------|
| `8baa4a8a-14c6-4451-8e8b-14bdbd197537` | Processor power throttling max | 处理器功耗上限 % |
| `0cc5b647-c1df-4637-891a-dec35c318583` | Processor maximum state | 处理器最大状态 % |
| `ea062031-0e34-4ff1-9b6d-eb1059334028` | Processor hardware threading | 处理器硬件线程数 |

每个参数都同时设置 AC + DC：
```
powercfg /setacvalueindex {scheme} SubGroup {GUID} {corePercent}
powercfg /setdcvalueindex {scheme} SubGroup {GUID} {corePercent}
```

**恢复** `RecCpuCore()`：3 个参数全部设为 `100`（无限制）

#### 功耗策略 — `SetCPUPerformanceStrategy(power)`

**Setting GUID**：`36687f9e-e3a5-4dbf-b1dc-15eb381c6863`（Processor idle demotion）  
**IL 命令**：
```
powercfg /setacvalueindex {scheme} SubGroup 36687f9e... {power}
powercfg /setdcvalueindex {scheme} SubGroup 36687f9e... {power}
```

#### 功耗墙 — ryzenadj 命令链

| UI 控件 | ryzenadj 命令 | 值范围 | 执行路径 |
|:-------|:------------|:-----|:--------|
| PL1 (SPL) Checked | `--slow-limit={W}` | 10-105W | → `RyzenAdj_To_UXTU.Translate()` → SMU 直写 |
| PL1 Unchecked | `--slow-limit={stored}` | — | 恢复存储值 |
| PL2 (FPPT) Checked | `--fast-limit={W}` | 10-120W | → `RyzenAdj_To_UXTU.Translate()` → SMU 直写 |
| PL2 Unchecked | `--fast-limit={stored}` | — | 恢复存储值 |

#### 温度墙 — ryzenadj 命令链

| UI 控件 | ryzenadj 命令 | 执行路径 |
|:-------|:------------|:--------|
| 设置温度墙 | `--tctl-temp={v} --cHTC-temp={v} --apu-skin-temp={v}` | → Translate → SMU |
| 恢复默认 | `--tctl-temp=99 --cHTC-temp=99 --apu-skin-temp=99` | → Translate → SMU |

#### 电压偏移 — ryzenadj 命令

| UI 控件 | ryzenadj 命令 | 范围 | 限制 |
|:-------|:------------|:---|:----|
| CPU 降压 | `--set-coall={mV}` | -50 ~ 0 mV | **只允许降压**，代码硬编码限制 |

#### 所有 powercfg 命令的通用前置步骤

```csharp
// 每次修改电源设置前必执行
NvTuning.RunPowershellCommand("powercfg /overlaysetactive overlay_scheme_none");
await Task.Delay(100);
// 获取当前活动电源方案 GUID
PowerGetActiveScheme(out IntPtr schemePtr);
var schemeGuid = Marshal.PtrToStructure(schemePtr);
LocalFree(schemePtr);
// 设置 AC + DC
RunPowershellCommand($"powercfg /setacvalueindex {schemeGuid} {subGroup} {setting} {value}");
RunPowershellCommand($"powercfg /setdcvalueindex {schemeGuid} {subGroup} {setting} {value}");
// 重新激活方案
PowerSetActiveScheme(IntPtr.Zero, schemeGuid);
```

#### ryzenadj 命令翻译层

所有 ryzenadj 字符串命令统一通过 `Universal_x86_Tuning_Utility.Scripts.RyzenAdj_To_UXTU.Translate(ryzenAdjString, isAutoReapply)` 转换：
- 解析 ryzenadj 命令行参数
- 映射到 UXTU 内部 SMU 命令 ID
- 通过 `RyzenSmu.Smu` → `WinRing0` → SMU 寄存器直写

#### 与我们的控制方案对比

| 功能 | 蛟龙方案 | 斗战者可复用方案 |
|:-----|:-------|:------------|
| CPU 频率限制 | powercfg（Windows 电源 API） | ✅ 直接复用 powercfg，无需 SMU |
| 关闭睿频 | powercfg be337238...=0 | ✅ 直接复用，标准 Windows 设置 |
| 核心数限制 | powercfg 三参数同设 | ✅ 直接复用 |
| PL1/PL2 功耗墙 | ryzenadj → SMU | ✅ 已有 SmuController，可直写 SMU |
| 温度墙 | ryzenadj → SMU | ✅ 已有 SmuController |
| 电压偏移 | ryzenadj → SMU | ✅ 已有 SmuController |

> **结论**：CPU 频率/睿频/核心数控制无需 SMU 驱动，直接 `Process.Start("powercfg", args)` 即可实现。功耗/温度/电压已有 SmuController 直写能力，无需引入 WinRing0。


## 3. BellatorFanControl（第三方独立控制器）

> **仓库**：https://github.com/Aveare/BellatorFanControl/
> **定位**：WinForms 独立风扇曲线控制器，不修改原厂控制台程序本体。**仅依赖 WMI MICommonInterface，无需斗战者控制台.dll。**

### 功能列表
- 切换静音(2)/平衡(0)/增强(1)/疯狂(3)四种散热模式
- 显示 CPU 温度(WMI CPUThermometer) + GPU 温度(nvidia-smi)
- 显示大风扇 1、大风扇 2、小风扇实时 RPM(WMI CPUGPUSYSFanSpeed)
- **自定义风扇曲线**（温度→转速联动，拖拽曲线点）
- 保存/加载曲线配置（`%APPDATA%\BellatorFanControl\curve.ini`）
- 恢复固件风扇控制（WMI MaxFanSwitch=0）
- 启动时自动定位桌面右下角

### MiInterface 协议（与我们发现的完全一致）
| 方法 | 编号 | 用法 | 验证 |
|:-----|:----:|:-----|:----:|
| SystemPerMode | 8 | `data[4]=mode` | ✅ |
| CPUGPUSYSFanSpeed | 13 | Get: OutData[4..5]=大扇1, [6..7]=大扇2, [10..11]=小扇 | ✅ 注意：需读满 12 字节，仅 Take(8) 会截断小扇数据 |
| MaxFanSwitch | 20 | `data[4]=FanType, data[5]=0/1` 启用手动控制 | ✅ SET有效,⚠️ GET不回写开关状态 |
| MaxFanSpeed | 21 | `data[4]=FanType, data[5]=val` 设转速(RPM/100) | ✅ 区间内持久 |
| CPUThermometer | 22 | OutData[4]=温度°C | ⏳ 待真机验证 |

### 默认风扇曲线表
| 温度 | 大扇(x100) | RPM | 小扇(x100) | RPM |
|:----|:----------:|:---:|:----------:|:---:|
| 50°C | 22 | 2200 | 20 | 2000 |
| 55°C | 26 | 2600 | 35 | 3500 |
| 60°C | 29 | 2900 | 48 | 4800 |
| 65°C | 32 | 3200 | 59 | 5900 |
| 70°C | 35 | 3500 | 64 | 6400 |
| 75°C | 38 | 3800 | 69 | 6900 |
| 80°C | 40 | 4000 | 75 | 7500 |
| 85°C | 43 | 4300 | 80 | 8000 |

### 自动控制参数
- **检查间隔**：默认 3 秒（用户可设 2-60 秒）
- **降档回差**：默认 3°C（1-15°C 可调），避免温度临界点频繁跳档
- **生效条件**：温度上升即写，温度下降需超过回差才写
- **写入逻辑**：`SetMaxFanSwitch(type, true)` → `SetMaxFanSpeed(type, val)`

### 限制
- ❌ 无 GPU 模式切换（未调 MiInterface(9)）
- ❌ 无 FnLock/TPLock/键盘背光/电源计划等系统控制
- ❌ WinForms 本地 GUI，无法远程访问
- ⚠️ 依赖 WMI，风扇写入在区间外 ~8s 被固件覆盖（EC 直写 0x5F/0x5B 更持久）

### 对我们的参考价值
| 借鉴项 | 说明 |
|:-------|:-----|
| 风扇曲线算法 | `PickTarget(hotspot)` 查表 + `ShouldWrite()` 回差控制 |
| 默认曲线表 | 可直接复用到前端的自定义曲线功能 |
| WMI MiInterface 封装 | `BellatorWmi` 类结构可作为 `WmiInterface.cs` 的设计参考 |
---

## 4. 运行时依赖关系

| 组件 | 依赖 | 状态 |
|:----|:----|:----:|
| WmiInterface.GPUMode | `System.Management` NuGet → WMI MiInterface(9) | ✅ WMI 直调 |
| C# HAL SmuController | inpoutx64 (MIT) | ✅ 无外部依赖 |
| FnLock / TPLock / 散热模式 / 键盘背光 | inpoutx64 (MIT) EC 直写 | ✅ 无外部依赖 |
| NvapiGpuController (超频) | KaronOC.dll (蛟龙控制台 JiaoLong 7.3) | ✅ 本地 DLL P/Invoke |
| CPU 频率限制/关睿频/核心数 | Windows powercfg 命令 | ✅ 无需驱动 |
| CPU 功耗墙/温度墙/电压偏移 | SmuController → inpoutx64 SMU 直写 | ✅ 已有 |
| ~~ryzenadj -> WinRing0x64.dll -> WinRing0.sys~~ | 已淘汰 | ❌ SmuController 替代 |

## 5. 功能对比

详见 [dev-release-plan.md](dev-release-plan.md) 完整对比表。


### BLDFnHotkeyUtility.exe 反编译 (2026-06-05)
> **位置**：`C:\Program Files (x86)\斗战者控制台\BLDFnHotkeyUtility.exe`（808KB，.NET Framework 32-bit）
> **定位**：真正的硬件写入者——监听 WMI 事件后通过 `StringToByteArray` 解析 hex 命令写 EC。

| 方法/常量 | 值 | 说明 |
|:----------|:---|:------|
| `StringToByteArray(String hex)` | — | 解析 hex 字符串为字节数组→写 EC |
| `wMIEventArrived(type, name, value)` | — | WMI 事件处理器，触发 EC 写入 |
| `WMI_BASE` | 1280 | WMI 事件标识基地址 |
| `BALANCE_MODE` | 1285 | 均衡模式 ID |
| `PERFERMANCE_MODE` | 1286 | 野兽模式 ID |
| `QUIET_MODE` | 1287 | 安静模式 ID |
| `FULLSPEED_MODE` | 1290 | 斗战模式 ID |
| `CPUFanSpeed (event)` | 26 | WMI **事件**名（非方法名） |
| `GPUFanSpeed (event)` | 32 | WMI **事件**名 |
| `BLD.CAPSNUM` | — | 大小写锁 WMI 类 |
| `BLD.OSD` | — | OSD 显示 WMI 类 |

> **关键结论**：`CPUGPUFanSpeed`（EXE 枚举名）和 `CPUGPUSYSFanSpeed`（DLL 枚举名）底层方法 ID 相同（13），但独立 `SetValue` 调用无法触发事件→EC 写入链，需要完整的 WMI 事件服务上下文。

---
> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
