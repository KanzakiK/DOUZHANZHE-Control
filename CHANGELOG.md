# Changelog

该项目所有重要变更均会记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本语义遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [1.4.0] — 2026-06-12

FanCurveService 全面重构 — EC 直写 ITSM 路由方案、ryzenadj SMU 干扰根因修复、前端滑动条模式区间校正

### 核心发现

- **EC PID "锁定"的真凶**: ryzenadj `--max-cpuclk` 通过 PCI config space (0xCF8/0xCFC) 写 SMU 寄存器，干扰了 EC 内部 PID 控制回路，导致风扇忽略 EC 0x5E/0x5A 写入的目标值。移除 ryzenadj 频率路径后风扇曲线完全稳定（100+ ticks 无偏离）

### 新增

- **RouteMode 路由函数**: 根据目标大扇/小扇转速自动选择最优散热模式（安静→均衡→野兽→斗战），实现全范围曲线：大扇 1900-4400 RPM，小扇 1700-8200 RPM
- **大扇优先路由 + 小扇钳位**: RouteMode 第一轮找同时覆盖大扇和小扇的模式；无完美匹配时以大扇为准选模式，小扇 Math.Clamp 到该模式范围；兜底选最高模式
- **每 Tick 刷新手动模式**: SetFanManual(true) + SetFanSpeed + EC 寄存器每 5 秒刷新一次，持续声明手动模式控制权，防止 EC 固件定时器退回自动模式
- **ITSM 守护**: 每 tick 校验 ITSM 寄存器，自动修复 Fn 热键/AC 插拔/睡眠唤醒导致的外部偏离
- **路由状态 API**: 新增 `GET /api/fan-curve/route-info`，返回 currentItsm / routedMode / consecutiveDeviation / 温度信息
- **Debug 页风扇曲线监控**: ITSM 路由面板，2 秒轮询 route-info，实时显示曲线状态/路由模式/温度/风扇目标/EC 读回/影子寄存器/WMI 状态，运行日志含 hotspot 温度
- **WMI 写入验证**: 写入后读回 EC 0x5E/0x5A 确认生效，连续失败标记通道锁定

### 修复

- **CPU 频率双写**: 前端 overrides 同时含 cpuFreqLimitEnabled 和 SMU 参数时，后端 `/api/uxtu/apply` 会把频率也传给 ryzenadj（与 `/api/cpu/freq-limit` powercfg 路径重复），现后端移除 CPU 频率 ryzenadj 代码，频率限制仅走 powercfg
- **前端风扇滑动条不受模式限制**: `getFanRange()` 之前被改为忽略 mode 参数、永远返回全范围；现恢复 `FAN_RANGES` 按模式返回合法区间（如 silent 大扇 1900-2900、gaming 大扇 4000-4400）
- **风扇曲线启动不一致**: 首页"自定义"按钮和曲线 tab "应用"按钮行为不同（前者不加载 fan-curve.json）；现 `Start()` 每次启动前加载 fan-curve.json 统一行为
- **EC 偏离恢复副作用**: 旧的 SetThermalMode 切换恢复会触发 ACPI 链导致 ITSM 翻来翻去；现简化为仅记录偏离日志，不主动恢复
- **前端恢复检测死代码**: FanCurvePanel 的 `prevRecoveringRef` 检测和 `reapplyOverrides` 重发逻辑随恢复机制一并移除
- **`nul` 构建失败**: Windows 保留设备名 `nul` 导致 `git add -A` 报 `invalid path`；`.gitignore` 添加 `nul` / `__nul_temp__` 永久解决
- **TickCount 不重置**: 从服务启动而非曲线启动开始计数；现每次 `Start()` 重置 TickCount 和 ConsecutiveDeviation

### 移除

- **ryzenadj 频率路径**: `SmuController.SetCpuFreqLimit()`、`BatchApply` 的 `maxClkMhz` 参数、`GetCapabilities` 的 `cpuFreqLimit`、`/api/smu/set` 的 `cpu_freq_limit` 分支
- **custom 模式**: `thermalModeMap` 的 `custom: null`、`MODE_FAN_DEFAULTS.custom`、`custom-params.json` 配置文件
- **TelemetryPanel.jsx**: 已废弃且无引用的遥测面板组件
- **ReapplyParams**: 全量参数重发机制替换为前端稀疏 overrides
- **FAN_RANGES 误删恢复**: 之前被当作死代码清理的 `FAN_RANGES` 常量实际被 `getFanRange` 使用，已恢复

### 改动

- **FanCurveService.Stop()**: 从"仅停定时器"改为"恢复保存的散热模式 + SetFanManual(false) 交还固件控制"
- **FanCurvePanel**: 移除模式区间带和钳位警告，Y 轴改为全范围显示；恢复检测逻辑替换为连续偏离计数显示
- **Debug 页**: 抽取为独立 `wwwroot/debug.html`；recovery/recovering 字段替换为 consecutiveDeviation + hotspot 温度卡片
- **图标改版**: 重新设计 favicon.svg / logo.svg / logo.png，更新 Shell 应用图标

## [1.3.6] — 2026-06-09

### 重构

- **存储层**: 删除 `MODE_PRESETS` 常量，改用稀疏覆盖 (override) 模型；新增 `saveOverride` / `loadOverrides` / `clearOverrides` 存储函数
- **模式下发**: `dispatchFullMode` 改为 overrides 感知，空 overrides 仅发 `thermal_mode`；SMU 延迟重发改为条件化（仅在有 CPU SMU 字段时触发）
- **恢复默认**: 新增 `resetToFactoryDefaults` + `resetParams`，删除 4 处分组恢复预设按钮
- 新增 `MODE_FAN_DEFAULTS` 各模式风扇默认转速表

### 新增

- **检查更新**: 后端新增 `/api/update/check` 端点（GitHub Releases API），前端启动时自动弹窗 + 设置页手动检查按钮，支持跳过此版本 / 稍后提醒 / 前往下载
- **构建脚本版本号自动化**: `build-installer.ps1` 自动从 `package.json` 读取版本号，通过 ISCC `/dMyAppVersion=` 传入 Inno Setup，修复安装包版本长期卡在 1.3.2 的问题

### 修复

- **GPU 启动安全网**: 拒绝从 `gpu-mode.json` 恢复 iGPU-only (mode=2)，防止独显断电导致 HDMI/DP 无输出；固件状态不匹配时自动修正为混合模式
- **GPU 显存频率锁**: `SetMaxMemoryClock` 参数从 `maxMHz,maxMHz`（精确锁定，GPU 无法降频）还原为 `0,maxMHz`（仅设上限，空闲时自由降频到 9000MHz 以下）
- **GPU 锁频状态同步**: `toggleGpuLock` 写入 `gpuFreqLimitEnabled` / `gpuCoreFreqMhz` 到 overrides，前端锁定状态跟随模式参数下发；新增 useEffect 从 `uxtuParams` 反向同步
- **dGpuDirect 遥测同步**: SettingsPanel 独显直连开关自动跟随实际 GPU mode；移除遗留 `gpuOnly` EC 直写通路，统一走 WMI；集显模式切换增加确认弹窗
- **模式切换竞态**: `thermal_mode` 加 `await` + 500ms 延迟（修复风扇值被 EC 覆盖），GPU/NVAPI/CPU 非 EC 通道改为每次无条件重置再按需覆盖
- **SMU 重发取消**: 快速切模式时旧 SMU 重发定时器自动取消 + generation 计数器二次校验，防止旧闭包覆盖新模式值
- **resetToFactoryDefaults 补全**: 补充 GPU reset-clocks + NVAPI reset（之前缺失）；isEmpty 分支 overrides 为空时也执行非 EC 重置
- **CPU 锁频修正**: "关闭睿频"改用 `min=max=100%` + `boost=2` 实现锁频，不再使用 `boost=0`（会锁死在基础频率 2.4GHz）
- **系统信息编码**: osName 缓存版本号 + 自动清除旧版 GBK 乱码；API 响应头加 `charset=utf-8`
- **自定义背景刷新丢失**: 服务端 `bgOptsPath` 统一用 `configDir`；`Results.File(byte[])` 兼容 HEAD 请求；前端仅在 404 时清除 `hasImage`
- **CPU reset await**: isEmpty 路径四路 powercfg 改为 `Promise.all`，防止与后续手动操作竞争
- **睿频开关去抖**: 600ms 去抖 + 硬件命令失败自动回滚 UI
- **显存档位去抖**: 400ms 去抖，防止快速连点导致多个重试循环重叠
- **NVAPI 温度默认值对齐**: `gpuTempLimitC` 85→87，与 NVAPI 硬件重置值一致
- **GPU 锁频持久化**: `gpuFreqLocked` 写入 localStorage，组件卸载后不丢失
- **风扇曲线快捷启动**: 后端 `Start()` 改 nullable 参数，无参时复用已保存配置
- **build-installer.ps1 编码损坏**: UTF-8 多字节截断（构?/检?/复?/找?）导致 `sysinfo-ext.ps1` 复制失败，重写修复
- **paramsLoaded ref 追踪**: 模式切换 effect 用 `paramsLoadedRef` 消除 hooks 依赖数组违规
- 删除 PerformancePanel 中未使用的 `applySmuBatch` 死代码
