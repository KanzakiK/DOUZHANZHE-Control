# Changelog

该项目所有重要变更均会记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本语义遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

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
