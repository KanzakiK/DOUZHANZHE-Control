# 任务看板

> **板块说明**：
> - **🧭 后续版本**：候选功能或低优先级改进
> - **✅ 已完成**：已验证通过的功能模块
>
> **整理规则**：
> - **两大板块**：🧭 后续版本 → ✅ 已完成
> - **排序**：开发实施 → 文档同步 → 清理/废弃 → 打包/部署
> - **分类**：后端 → 前端 → 其他；Bug 统一放到所属板块的"已知 Bug"子分类
> - **粒度**：一行 = 一个独立原子任务
> - **状态标记**：`[ ]` 待完成 → `[x]` 已完成（移至 ✅） → `~~删除线~~` 已废弃/已修复 Bug

---

## 🧭 一、后续版本（待完成）

### 后端
- [ ] **五模式全量配置覆盖**: 安静/均衡/野兽/斗战/自定义各保存一套完整配置（风扇转速×2、CPU功耗墙/温度墙、GPU频率偏移），后端新增 GET|POST /api/mode-config 持久化接口（前端 localStorage 已按模式隔离，缺后端持久化）
- [ ] **模式预设**: 新增"安静性能"模式（GPU满血 + 风扇低速）

### 后端 — v1.4.3 代码审核发现
- [x] ~~🔴 **CpuFreq 缓存变量错误**~~: 已修复 (v1.4.3) — `_sgDiskTime` → `_cpuFreqTime` — `HardwareAbstractionLayer.cs:633`
- [x] ~~🔴 **DriverBridge.WriteBit 读-改-写无锁**~~: 已修复 (v1.4.3) — 添加 `lock (_lock)` 包裹读-改-写序列 — `DriverBridge.cs:166`
- [ ] 🔴 **CORS AllowAnyOrigin — CSRF 安全漏洞**: `Program.cs:21` 硬件控制 API 允许任意网站跨域请求（EC 写入/GPU 超频/CPU 功耗），应限制来源为 `127.0.0.1:3100` / `localhost:3100`
- [ ] 🔴 **ec_write 端点无寄存器白名单**: `Program.cs:543-551` 允许写 0x00-0xFF 任意 EC 寄存器，应添加允许写入的寄存器范围白名单
- [ ] 🟠 **遥测热路径 Thread.Sleep 阻塞**: `HardwareAbstractionLayer.cs:613-616,646-648` CpuUsage/CpuFreq 属性中 `Thread.Sleep(200)` 等待 PerformanceCounter 采样，在 250ms 轮询周期中占比过高
- [ ] 🟠 **GpuTemperature 回退同步创建 nvidia-smi 进程**: `HardwareAbstractionLayer.cs:151-195` 属性 getter 中启动进程等待最多 3 秒，导致遥测轮询延迟尖峰
- [x] ~~🟡 **DriverBridge._recovering TOCTOU 竞态**~~: 已修复 (v1.4.3) — `bool` → `int` + `Interlocked.Exchange` 原子操作 — `DriverBridge.cs:38,91`
- [ ] 🟡 **PowerModeChanged Task.Run 未观察异常**: `Program.cs:82-121` 异步恢复任务未被 await 或观察，未预期异常可能导致进程崩溃
- [x] ~~🟡 **FanCurveService.Dispose 不清理 Timer**~~: 已修复 (v1.4.3) — Dispose 先 `_timer?.Dispose()` 再 `RestoreFirmwareControl()` — `FanCurveService.cs:230`
- [x] ~~🟡 **RyzenAdj 进程超时未 Kill**~~: 已修复 (v1.4.3) — 超时后 `proc.Kill()` 防止 ryzenadj 残留 — `SmuController.cs:55`
- [ ] 🟡 **背景图片上传无大小限制**: `Program.cs:1434-1470` base64 编码图片无尺寸校验
- [ ] 🟢 **API 错误响应格式不统一**: Results.Problem（RFC 7807）与 Results.Json(new { ok = false }) 混用

### 前端
- [ ] **自建 OSD Toast**（前端全局 Toast，所有 POST /api/control 操作成功后弹出操作反馈，替代官方 BLDFnHotkeyUtility OSD）
- [ ] **五维雷达图**可视化（CPU/GPU/内存/磁盘/风扇综合评分）

### 前端 — v1.4.3 代码审核发现
- [ ] 🔴 **全应用零 Error Boundary**: 任何组件渲染错误导致白屏，硬件控制面板用户可能误以为命令已成功
- [ ] 🟠 **15 处空 catch 静默吞错**: `.catch(() => {})` 分布在 App.jsx / PerformancePanel / FanCurvePanel / SortableDashboard 等关键组件，硬件命令失败用户无感知
- [ ] 🟠 **useControlState 全局单 hook 管理所有状态**: 遥测每秒更新触发整个 App 组件树重渲染，应将 telemetry/history 拆分为独立 hook 减少不必要渲染
- [x] ~~🟡 **HTML lang 属性错误**~~: 已修复 (v1.4.3) — `en` → `zh-CN` — `index.html:2`
- [x] ~~🟡 **ESLint 配置未排除构建产物**~~: 已修复 (v1.4.3) — `globalIgnores` 补充 `server/**/bin/**` 和 `server/**/wwwroot/**` — `eslint.config.js:8`
- [ ] 🟡 **全应用零 ARIA 无障碍属性**: 0 处 role / aria- 属性，SwitchRow 缺 role="switch"，SliderRow 缺 aria-label，UpdateDialog 缺 role="dialog" + 焦点陷阱

### 下发逻辑审计（28 项 · 2026-06-09）

> 审计范围：模式切换 `dispatchFullMode`、恢复默认 `resetToFactoryDefaults`、单项调整 onChange、风扇曲线、状态管理
> 严重度：🔴 严重 · 🟠 高 · 🟡 中 · 🟢 低
> **已完成 17 项，剩余 11 项待处理**

#### 仍待处理

- [ ] 🟢 **1.5 风扇下发用 raw fetch 而非 adapter**: 其余通道均走专用 adapter 函数，唯独 fan target 内联 `fetch("/api/fan/set-target")`。破坏 adapter 抽象，改错/重试/URL 需多处修改 — `uxtuAdapter.js` L396-403
- [ ] 🟡 **2.2 硬件/UI 重置分两个函数无强制协调**: `resetToFactoryDefaults`(硬件) 和 `resetParams`(UI) 在调用端顺序调用，无组合函数或强制约束。任何新 reset 代码路径须记住两者 — `App.jsx` L149-150
- [ ] 🟡 **3.2 风扇滑块 onChange 捕获了另一风扇的过期值**: 拖动一个风扇滑块时另一风扇 RPM 从 render 闭包读取（stale），`setUxtuParams` 是异步的，`queueFan` 用了更新前的值。快速双拖会发混合对 — `SortableDashboard.jsx` L224-228/L243-247
- [ ] 🟢 **3.6 `handleApply` 与单项 SMU 滑块用不同 API 端点**: `handleApply` POST `/api/uxtu/apply`（批量），单项滑块 POST `/api/smu/set`（单条）。若后端行为不同，同逻辑操作可能产生不同硬件结果 — `PerformancePanel.jsx` L173-185
- [ ] 🟢 **3.7 滑块 onChange 未用 `clampParam` 防御**: `clampParam` 存在于 `uxtuAdapter.js` 但仅在 `dispatchFullMode` 中使用，各滑块 onChange 完全依赖 UI min/max 属性。若存储值越界则无钳位直接下发硬件
- [ ] 🟠 **4.1 `dispatchFullMode` 不感知风扇曲线运行状态**: 模式切换仅检查 overrides 中是否有风扇字段，不知曲线是否激活。切换瞬间手动 RPM 目标可能与曲线控制器短暂冲突 — `uxtuAdapter.js` L392-404
- [ ] 🟡 **4.2 曲线启动后 3s 轮询间隔内未协调 override**: 风扇曲线启动不设 override 标记也不通知 useControlState，`fanCurveActive` 靠 3s 轮询检测。间隔内风扇滑块可能未正确禁用，允许与曲线冲突的手动命令 — `FanCurvePanel.jsx` L223-237, `App.jsx` L42-48
- [ ] 🟢 **4.4 停止逻辑重复**: 风扇曲线停止逻辑在 FanCurvePanel 和 SortableDashboard 两处近乎相同地重复，任何变更须改两处 — `FanCurvePanel.jsx` L239-266, `SortableDashboard.jsx` L170-191
- [ ] 🟡 **5.3 `resetParams`/`clearOverridesFn` 无模式校验即清 overrides**: 两函数直接 `setOverrides({})` 清空模式无关的 overrides React state。若传入错误 mode 或用户在 clear 和 re-render 间切模式，可能清错 overrides — `useControlState.js` L123-133/L304-307
- [ ] 🟠 **C.1 全部下发路径无取消/中止机制**: 五条下发路径均无 abort/cancel 能力。`dispatchFullMode` 用裸 `setTimeout`，各去抖队列仅在同一组件实例内可取消，模式切换不取消 PerformancePanel 的待下去抖命令。v1.4.3 审核补充：PerformancePanel 的 thermalTimer/gpuCoreTimer 声明在卸载清理 effect 之后，遗漏 clearTimeout（`PerformancePanel.jsx` L62-73）；SortableDashboard 的 queueFan 去抖定时器也无卸载清理（`SortableDashboard.jsx` L48-63）
- [ ] 🟡 **C.3 GPU 锁频条件耦合 `enabled` 与基频检查**: 锁频需同时满足 `gpuFreqLimitEnabled` 为真且 `gpuCoreFreqMhz !== GPU_BASE_CLOCK`(2750)。用户开启频率限制并设到恰好 2750 MHz 时 `limit-max` 和 `lock-exact` 都不发。隐式耦合易误导后续维护 — `uxtuAdapter.js` L341-347
- [ ] 🟢 **C.4 `gpuMemFreqMhz` 作数组下标无越界检查**: 用作 4 元素数组 `memMap` 的索引，合法值 0-3。若 localStorage 损坏值 > 3 则取 `undefined`，向后端发 `{ action: "limit-memory", value: undefined }`。非 custom 模式 overrides 无校验 — `uxtuAdapter.js` L350-355

### 自定义风扇曲线 · EC 直写 ITSM 方案（设计方案: [custom-fan-curve-design-2026-06-10.md](custom-fan-curve-design-2026-06-10.md)）

> 核心思路：ModeFanRanges 从钳位器变为路由表，EC 直写 ITSM(0xE4) 选择风扇区间（不触发 ALIB/GPUD），WMI 0x1500 写入目标转速。全范围可用：大扇 1900-4400，小扇 1700-8200。
> 依赖调查报告：[fan-write-investigation-2026-06-10.md](fan-write-investigation-2026-06-10.md) Section 6-7

> **Phase 1 — 后端核心** ✅
- [x] **① RouteMode 路由函数**: FanCurveService.cs 新增 `RouteMode(largeRpm, smallRpm)` — 遍历 ModeFanRanges 找到同时覆盖大扇和小扇目标的最低模式编号，无交集时优先满足大扇，fallback 斗战(3)
- [x] **② Tick 替换钳位为路由 + EC 直写**: FanCurveService.Tick() 中删除现有 ModeFanRanges 钳位逻辑，替换为 RouteMode → 读取 ITSM(0xE4) → 不匹配时 EC 直写 → Thread.Sleep(100) → 在目标模式区间内钳位 → WMI 写转速
- [x] **③ ReadEcPort 公开**: 确认 HardwareAbstractionLayer 中 ReadEcPort(0xE4) 可被 FanCurveService 调用（已确认为 public）
- [x] **④ 启动/停止保护**: Start() 时保存当前 ITSM 到 _savedThermalMode；Stop() 时通过 WMI SetThermalMode(_savedThermalMode) 恢复正常模式链（触发完整 DPTB/GPUD），再 SetFanManual(false) 交还固件
- [x] **⑤ ITSM 守护 + 偏离统计**: 每个 tick 读 ITSM 对比预期值，偏离时重写并递增 _itsmDeviationCount；1 分钟内偏离 ≥5 次记录 Warning 日志
- [x] **⑤b WMI 风扇写入验证 + 锁定检测**: Tick 写入后读回 EC 0x5E/0x5A 确认值生效；连续 3 次写后不生效标记 WMI 通道为"锁定"，触发 Toast 警告并尝试 SetFanManual 重激活（应对 CPU 频率限制导致的通道锁定，见设计方案 §6.7）
- [x] **⑥ /api/fan-curve/route-info 端点**: Program.cs 新增 GET 端点，返回 currentItsm / routedMode / lastLargeTarget / lastSmallTarget / modeChangeCount / itsmDeviationCount / wmiChannelLocked

> **Phase 2 — 前端适配** ✅
- [x] **⑦ FanCurvePanel 移除模式区间带**: 删除蓝色/紫色区间矩形 + 区间标签 + 钳位警告环（`largeClamped` / `smallClamped` 判定）；Y 轴标注改为全范围大扇 1900-4400 / 小扇 1700-8200
- [x] **⑧ 路由状态指示**: 状态栏新增"当前路由模式: [安静]"指示，通过轮询 /api/fan-curve/route-info（3s 间隔）返回的路由信息更新显示
- [x] **⑨ uxtuAdapter getFanRange 改全范围**: `getFanRange(mode)` 不再按模式返回区间，改为返回 `FULL_FAN_RANGE = { largeMin:1900, largeMax:4400, smallMin:1700, smallMax:8200 }`
- [x] **⑩ 曲线激活时禁用手动风扇滑块**: SortableDashboard.jsx 中 queueFan 在 `fanCurveActive` 时不下发，风扇滑块显示为只读状态（已有 disabled + opacity 实现）

> **Phase 3 — 验证测试** ⏸️ 暂缓（核心功能已成型，按需补充测试）
> - ⑪ ITSM 长期稳定性测试（30 分钟游戏负载）
> - ⑫ Fn 热键恢复测试
> - ⑬ AC 插拔恢复测试
> - ⑭ 跨模式曲线测试（安静→均衡→斗战路由切换）
> - ⑮ GPU 功耗保持测试（曲线 + 游戏负载不降频）
> - ⑯ 停止恢复测试（确认恢复 _savedThermalMode + 固件控制）
> - ⑯b CPU 频率限制干扰测试（四种 ITSM 下的 WMI 阻断行为）

> **Phase 4 — 打磨与降级** ⏸️ 暂缓（⑲ 睡眠恢复已实现，其余按需迭代）
> - ⑰ WMI 事件监听加速恢复（可选）
> - ⑱ EC 写入失败降级（连续 3 次失败 → WMI SetThermalMode + SMU 覆盖）
> - [x] ~~⑲ 睡眠/唤醒处理~~ ✅
> - ⑳ 日志完善（路由决策过程 + 偏离计数，已部分实现）

> **多配置预设**: 后续迭代 — 单文件 `fan-curve.json` 改为多配置存储（命名预设），前端增加预设列表 + 命名保存交互

### 其他
- [x] **Debug 页重构**: 将 debug HTML 从 Program.cs 内嵌字符串（~33KB 单行）提取到 `wwwroot/debug.html` 独立文件，补齐缺失的控制区：SMU 功率/温度、CPU powercfg（freq-limit/turbo/core-limit/reset）、GPU NVAPI（超频/温度限制/P-State dump）、风扇曲线服务（start/stop/status/route-info）、EC scan、系统信息。现有 54 个 API 端点中 debug 页仅覆盖 ~10 个
- ~~**CPU 频率限制改用 ryzenadj/SMU 实现**~~: ❌ 已废弃 (v1.4.0) — 核心发现证明 ryzenadj `--max-cpuclk` 通过 PCI config space 写 SMU 寄存器会干扰 EC 内部 PID 控制回路，导致风扇忽略 EC 写入的目标值。刻意回退保持 powercfg `SET_PROC_FREQ_LIMIT` 路径 — `CpuPowerController.cs`
- [ ] **一键降压**: 参考游戏加加 Lite，实现 CPU/GPU 降压功能（降低电压以减少发热和功耗）
- [ ] **游戏自动切换性能模式**: 参考机械革命控制台，监听游戏进程启动/退出，自动切换预设性能模式
- [ ] **提升控制台进程优先级**: 设置 Douzhanzhe 进程为高优先级（High/Above Normal），减少系统资源竞争导致的性能问题

---

## ✅ 二、已完成

### 后端 — 架构与驱动
- [x] DriverBridge 单例（inpoutx64 P/Invoke）
- [x] DriverBridge 32-bit IO: Inp32/Out32 + ReadPhys32/WritePhys32
- [x] HardwareAbstractionLayer（EC 寄存器语义映射）
- [x] WebConsoleAPI（Minimal API + WebSocket + TelemetryBackgroundService）
- [x] DSDT ACPI 反编译 + EC 寄存器全表导出
- [x] 替换 WinRing0 -> inpoutx64 (MIT 自管)
- [x] SmuController 子进程调用 ryzenadj.exe（程序集零外部依赖，运行时依赖 WinRing0 驱动）
- [x] SMU 子进程集成修复: 路径候选 + run.ps1 自动复制 WinRing0/ryzenadj + 移除 Redirect + 接受 0xC0000005 ✅
- [x] WmiInterface.cs（`root\WMI`，32 字节协议，零依赖）
- [x] C# 静态文件服务: `wwwroot/` + `UseStaticFiles()` + `MapFallbackToFile("index.html")`
- [x] C# 反向代理 Node.js 遗留端点
- [x] 开机自启动（后端注册服务）
- [x] 安装程序 / 打包（Inno Setup）
- [x] 版本号统一 + 构建脚本自动化（`-Version` 参数同步四处版本号 + 打包前验证）

### 后端 — 控制端点与遥测
- [x] `POST /api/control`: kb_light, fn_lock, num_lock, caps_lock, touchpad_lock, power_plan, thermal_mode, igpu_only, gpu_mode
- [x] `POST /api/smu/set`: stapm_limit, temp_limit
- [x] `POST /api/fan/set-target` + `POST /api/fan/restore`
- [x] `GET /api/telemetry` 全量遥测（28 字段）
- [x] `WS /ws` WebSocket 实时推送
- [x] `GET /debug` 内嵌 Debug 面板
- [x] 跑通全链路: GET /api/health -> POST /api/control -> 物理 EC 写入生效
- [x] Node.js JSON 持久化迁移: C# `GET|POST /api/custom-params`、`GET|POST /api/ui-state`
- [x] Node.js 遥测迁移: C# 补齐 `systeminformation` 类数据（CPU/内存/硬盘全量）
- [x] TelemetryBackgroundService 无条件推送 250ms: 删除变化检测，间隔 500ms→250ms
- [x] 废弃 Vite dev server: 前端迁入 `wwwroot/`，清理 proxy/watch/concurrently/dev/start/server 脚本
- [x] **遥测扩展**: CPU/GPU 功率 — GPU `power.draw` 实时显示 + CPU 功率状态读取/重置按钮
- [x] **Debug 页 GPU 控制区**: 频率锁/超频/重置 + 当前频率/功耗显示（前端 PerformancePanel 实现；`showGpu` 参数控制显示）
- [x] **SMU 监视器**: 值被覆盖时自动重发（替代 `readjustService.ps1`）— `applySmuBatch` 批量下发 + 模式切换双发 SMU
- [x] **跨平台/非管理员降级模式**（无 inpoutx64 时以 WMI/软件方式运行）— try-catch 优雅降级 + WMI fallback
- [x] **Node.js 全部端点迁移到 C#**，砍掉 Node.js 后端（06-06 已退役，全功能迁至 C# wwwroot/）
- [x] **安静性能模式**: `POST /api/uxtu/apply` 扩展支持 `fanLargeRpmTarget`/`fanSmallRpmTarget`

### 后端 — 风扇控制
- [x] EC 寄存器发现 (0xB2/0xB3) + 公式 `val=round(rpm/maxRpm*255)` 经 ec_writer.cs 验证
- [x] ~~0xB2/0xB3 直写~~ ❌ → **0x5F(大扇)/0x5B(小扇) ✅** WriteEc 直接生效，值=RPM/100，受散热区间限制
- [x] **小风扇控制寄存器 0x5B**: EC 直写 `WriteEc(0x5B, RPM/100)` 已验证
- [x] C# API + Debug 页风扇目标转速滑块 ✅ **0x5F 控制已验证**
- [x] WMI Bellator 协议修复: MaxFanSwitch(20)+MaxFanSpeed(21) data[4]=FanType
- [x] 恢复固件控制: `POST /api/fan/restore` — Debug 页按钮
- [x] EC 16 位风扇竞态修复 (HAL 双读仲裁): CpuFanRpm/GpuFanRpm 最多重读 3 次取非零，瞬态 0 率降至 0%

### 后端 — WMI 迁移 + AppBridge 废弃
- [x] WmiInterface 实现: GPUMode(9) ✅ FnLock(11) ✅ TPLock(12) ✅
- [x] AppBridge 子进程 + AppLib.cs 封装（反射调用 BLD.WMIOperation）
- [x] 运行时依赖自动部署（System.Management + 斗战者控制台.dll）
- [x] GPU 模式控制（混合/集显/独显）真机验证
- [x] 端点: `/api/app-cmd`, `/api/app-status`, `/api/ec-scan`
- [x] Debug 页 AppBridge 命令调试区
- [x] 废弃 AppBridge: 砍掉 AppLib.cs + AppBridge 子项目 + 斗战者控制台.dll 依赖

### 后端 — SMU 调优
- [x] Step 1~D: libryzenadj 封装 -> server.js 重构 -> SmuController 物理地址直写
- [x] Step G: WinRing0x64.dll 仍依赖于 SmuController（通过 ryzenadj.exe），DriverBridge 已全面移除对 WinRing0 的直接依赖
- [x] SMU 写入验证: Dragon Range 地址确认 + ryzenadj 子进程写入 25W 功率墙成功
- [x] Debug 页按钮: SMU 功率/温度设置按钮已整合 ✅
- [x] **SMU BatchApply**: SmuController 批量方法，单次 ryzenadj 子进程调用替代多次串行调用（模式切换 2-3s → ~300ms）
- [x] **CPU 性能控制后端**: CpuPowerController.cs 封装 powercfg，API: `POST /api/cpu/freq-limit`, `/api/cpu/turbo`, `/api/cpu/core-limit`, `/api/cpu/reset`
- [x] **NVAPI 端点**: `GET /api/nvapi/status`, `POST /api/nvapi/overclock`, `POST /api/nvapi/thermal-limit`

### 后端 — GPU 控制 (nvidia-smi)
- [x] **GpuController.cs (nvidia-smi 子进程封装)**: `POST /api/gpu/set` 统一接口
- [x] **GPU 核心频率超频**: KaronOC.dll (蛟龙引擎) P-State 偏移超频，已验证 core +150MHz / mem +300MHz
- [x] **GPU 超频引擎集成**: NVAPI P/Invoke + KaronOC.dll 双层架构，状态读取 + 超频写入
- [x] **GPU 核心频率锁频**: nvidia-smi `--lock-gpu-clocks=min,max` ✅ 已验证可用
- [x] **GPU 核心频率上限限制**: nvidia-smi `--lock-gpu-clocks=,max`
- [x] **重置 GPU 频率**: nvidia-smi `--reset-gpu-clocks` ✅ 已验证可用
- [x] **显存超频**: nvidia-smi `--lock-memory-clocks=min,max` (RTX5060 GDDR7 9001MHz 基线)
- [x] **重置显存**: nvidia-smi `--reset-memory-clocks`
- [x] **前端 GPU 性能面板 (NVAPI + nvidia-smi 统一卡片)**: 核心频率(nvidia-smi lock) + 核心偏移(NVAPI P-State offset) + 显存频率档位 + 温度限制 + 重置按钮
  - [x] **核心频率滑块**: nvidia-smi `--lock-gpu-clocks` 绝对频率 + 自动锁定重试机制
  - [x] **核心偏移滑块**: NVAPI P-State 偏移量(-200~+300 MHz)，模式切换联动
  - [x] **显存频率档位**: 自动/9001/11001/12001 MHz 四档
  - [x] **GPU 温度限制滑块**: `POST /api/nvapi/thermal-limit`，模式切换联动(CustomEvent)
  - [x] **重置 GPU 按钮**: 一键恢复核心频率/显存/偏移/温度限制到默认值
  - [x] ~~**GPU 功耗墙**~~: ❌ nvidia-smi/NVAPI 均不可用，已废弃
  - [x] **不受影响**: CPU 全部控件 / 风扇控制 / 系统开关 / 遥测曲线 / GPU 模式切换 — 均走独立通道，无需改动

### 后端 — 废弃 Node.js
- [x] **废弃 Node.js 后端**: 砍掉 server/server.js + tools/Node.js 仅用文件 + server.js 依赖

### 后端 — CPU 性能控制
- [x] **CPU 性能控制**: 基于 powercfg 实现 CPU 频率限制/关睿频/核心数限制（无需 SMU 驱动）

### 前端 — 遥测与仪表盘
- [x] CPU 占用率/温度/频率/核心数
- [x] GPU 占用率/温度/频率/显存
- [x] 内存占用/总量 + 硬盘占用/总量
- [x] CPU/GPU 风扇转速数字显示
- [x] WebSocket 对接硬件实时遥测（离线时 mock 回退）
- [x] 拖拽排序（@dnd-kit） + 卡片隐藏/显示 + 排序持久化
- [x] 历史曲线图 Sparkline 渲染排查 ✅ NaN 兜底修复
- [x] 隐藏风扇负载曲线: EC 竞态心电图，移除 `<Sparkline>` + `fanPctSeries`

### 前端 — 性能与调节
- [x] CPU 长时/短时功耗墙、温度墙、睿频控制
- [x] GPU 功耗墙、温度墙
- [x] 模式预设（安静/均衡/斗战/野兽/自定义）+ 非自定义模式锁定控件
- [x] 自动应用参数（500ms 去抖） + 模式切换联动
- [x] 路由修复: `SettingsPanel.jsx` halMap 追加 `gpuOnly->igpu_only`、`touchpadLock->touchpad_lock`
- [x] 散热模式: `uxtuAdapter.js` 导出 `thermalModeMap` + `powerPlanHALMap`
- [x] 散热模式: `App.jsx` 5 个模式按钮联动 `POST /api/control target=thermal_mode`
- [x] 模式重构（部分）: CPU/GPU 控件在四种模式下均可调
- [x] 电源计划: `uxtuAdapter.js` 导出 `powerPlanHALMap`
- [x] **电源计划**: `PerformancePanel.jsx` 电源管理按钮双发 C# HAL `power_plan` halValue 断链修复
- [x] **系统信息动态化**: 新增GET /api/system/info + HAL 4 WMI属性 + 前端useEffect
- [x] **电源计划按钮双发修复**: PerformancePanel.jsx POWER_PLANS 添加 halValue，按钮点击实际下发 C# HAL
- [x] **风扇滑块持久化**: 从 `useState(硬编码)` → `useState(() => loadFromLS(...))` + saveToLS，刷新保留上次设定值
- [x] **电压偏移持久化**: localStorage
- [x] **隐藏系统开关冗余项**: 移除独显直连/集显模式/关闭OSD三个SwitchRow 键 `douzhanzhe_voltage_offset`，所有模式通用
- [x] **非自定义模式 slider 闪跳修复**: fetch(/api/custom-params) 仅在 mode === "custom" 时覆盖 uxtuParams，其他模式保持 MODE_PRESETS
- [x] **GPU 统一卡片**: 合并标准/超频模式为单一 GPU 调节卡片，核心频率(nvidia-smi lock) + 核心偏移(NVAPI offset) + 显存档位 + 温度限制，自动锁定 + 重试机制(gpuCmd 2 retries 300ms)
- [x] **NVAPI 核心偏移模式联动**: MODE_PRESETS 加入 gpuCoreOffsetMhz，模式切换时 CustomEvent 同步 PerformancePanel 偏移滑块 + 异步下发 NVAPI
- [x] **GPU 温度限制模式联动**: CustomEvent(`gpu-thermal-updated`) 乐观更新 UI + `applyNvapiThermalLimit` 异步下发
- [x] **排序跳动修复**: SortableCard 高度锁定(800ms useLayoutEffect) + `break-inside: avoid` + localStorage 优先(跳过服务端覆盖)
- [x] **SMU 死代码清理**: 移除 PerformancePanel 中未使用的 smuInfo/smuError 状态和 3 秒延迟 fetch
- [x] **恢复预设按钮**: 模式选择 Card 右上角 `action` 按钮，恢复当前模式 CPU+GPU+风扇全量出厂值
- [x] **模式切换滑块联动**: 切换模式时 `uxtuParams` 跟随 `MODE_PRESETS` 更新，滑块/开关同步跳转
- [x] **每模式独立参数记忆**: localStorage 按模式名隔离 key（`douzhanzhe_params_`+模式名），切换模式时恢复该模式上次调的值，点"恢复预设"才重置
- [x] **模式独立配置管理**: 每个模式独立保存风扇/CPU/GPU参数，切换模式时全量写入

### 前端 — 系统功能
- [x] 主题切换（4 套皮肤）
- [x] 导航标签页 + 持久化
- [x] 键盘背光滑块 UI
- [x] 系统开关 UI（Fn 锁 / NumLock / CapsLock / 触摸板锁）
- [x] GPL v3 许可证 + 技术信息面板
- [x] Toast 通知反馈
- [x] CHANGELOG.md + README.md
- [x] **版本更新推送**: 检测 GitHub Release 最新版本，启动时自动检查 + 弹窗提示（支持跳过/稍后/前往下载），设置页可手动检查
- [x] **模式切换 SMU 双发**: 模式按钮点击时 EC 切换前后双发 SMU（防固件覆盖），温度墙/功耗墙实时生效
- [x] **CPU 调节实时下发修复**: 频率限制、关睿频、核心数滑块追加 queueSmu 实时调用（不再只改 state）
- [x] **WinRing0 自动加载**: run.ps1 (Start-Process -Verb RunAs) + Program.cs 启动时自动提权加载驱动
- [x] **reload-fe.ps1 部署路径修复**: 部署目标从 server/api/wwwroot 改为 server/api/bin/run/wwwroot（服务器实际读取路径）

### 文档
- [x] dev-api.md — C# HAL / Node.js 所有端点定义
- [x] dev-ec-map.md — EC 寄存器全表（已验证状态）
- [x] dev-architecture.md — 系统分层 + 数据流
- [x] dev-backend.md — 后端架构
- [x] dev-frontend.md — 组件树/状态管理
- [x] dev-release-plan.md — Release 1 对比表
- [x] douzhanzhe-progress.md — 项目进度快照
- [x] `dev-api.md`: Vite 代理表端口误标为 :3099 → 修复为 :3100 ✅

### 开发流程
- [x] **写入策略改造**: 放弃 Python 管道，恢复编辑器工具写入 + `_verify_write.py` 后验清洗，copilot-instructions.md §1 重写、主记忆同步
- [x] **反覆写/反回滚规则**: copilot-instructions.md §2 新增"禁止全量覆写"和"禁止隐式回滚"，§3 新增"跨会话衔接契约"
- [x] **`_verify_write.py --check` 模式**: 新增只读诊断模式用于前置破幻对账，守则 §1 更新引用，主记忆同步
- [x] **主记忆跨会话加载流程**: 主记忆顶部新增 5 步加载流程，明确新会话时如何读取本文件+守则+速查表+文档地图
- [x] **会话归档重构**: 98 个会话去重为 26 个独立条目，时间倒序排列，顶部新增速查表，体积从 86KB→24KB
- [x] **`.ship` 流程改进**: 会话归档新增去重检查、定位插入（速查表分隔线处）、速查表同步；新增第 4 步 GitHub 同步（git add→commit→push）
- [x] **速查表滚动截断**: session-archive.md 速查表限 15 行+汇总行，溢出时自动删除最旧行
- [x] **主记忆参考表重构+reference-consoles.md 速查表**: 主记忆 §3 从 3 行扩展为 7 行（BellatorFanControl/UXTU/EnumDLL/nvidia-smi），reference-consoles.md 新增顶部速查表、修复章节编号
- [x] **主记忆 §3/§4 追加近期摘要**: 已知遗留问题追加 2 行、会话归档追加 3 行近期摘要信息
- [x] **`dev-api.md` 顶部维护规则**: 新增分组追加/废弃/同步等更新规则
- [x] **`dev-backend.md` 顶部维护规则**: 新增服务表/架构层/驱动链等更新规则
- [x] **`dev-ec-map.md` 顶部维护规则**: 新增寄存器追加/验证标记/废弃处理等更新规则
- [x] **`dev-frontend.md` 顶部维护规则**: 新增组件树/接口/依赖等更新规则
- [x] **`dev-index.md` 顶部维护规则**: 新增技术栈/部署/索引等更新规则
- [x] **`dev-architecture.md` 顶部维护规则**: 新增架构图/数据流/部署表等更新规则
- [x] **`dev-release-plan.md` 顶部维护规则**: 新增对比表/原则同步等更新规则
- [x] **`reference-consoles.md` 顶部维护规则**: 新增参考项目/结论表等更新规则
- [x] **审计修复 P0 — `.ship` 同步规则补齐**: 新增"§1文档地图同步"，区分"§2近期工作"vs"§4近期摘要"- [x] **审计修复 P1/P2**: dev-release-plan 规则+标题、dev-known-issues 规则+拼写、copilot-instructions §4、"地图"→"映射"、"API Interface Defs"→"API 接口定义"
- [x] **GSD 流程清理**: .plan 模板/verify fail/§6 服务重启 3 项死规则修复

### 清理
- [x] ~~`tools/`: 清理 WinRing0x64 残留~~ ✅ WinRing0x64.dll/.sys 已清除（文件已从 `server/tools/` 移除）
- [x] ~~`tools/`: 移除 WinRing0x64.dll + 废弃 ryzenadj.exe~~ ✅ WinRing0x64.dll/.sys 已清除；❌ ryzenadj.exe 仍为 SmuController 运行时依赖，暂无替代方案

### 参数覆盖层重构 ✅ (Phase 0-3 全部完成)
- [x] **Phase 0**: 恢复被误删的 8 个 API 端点（NVAPI 3 + CPU powercfg 5）
- [x] **Phase 1**: 存储模型重构 — 删除 MODE_PRESETS、新增 sparse override 模型、dispatchFullMode overrides 感知、resetToFactoryDefaults
- [x] **Phase 2**: 恢复默认 — clearOverrides + resetToFactoryDefaults、删除分组恢复按钮
- [x] **Phase 3**: UI 收尾 — 控件灰色/高亮、单项下发三步、风扇 EC 自动 + 曲线互斥、废弃代码清理
- [x] 详见 [plan-override-layer.md](plan-override-layer.md)

### 下发逻辑审计修复 (28 项 · 已完成 17 项)
- [x] ~~🔴 **1.1 快速切模式 SMU 重发覆盖**~~ — 已修复
- [x] ~~🟠 **1.2 isEmpty 路径 CPU reset 未 await**~~ — 已修复
- [x] ~~🟠 **1.3 SMU 重发不可取消**~~ — 已修复（代际计数器）
- [x] ~~🟡 **1.4 自定义模式跳过非 EC 重置**~~ — 不适用（custom 模式已移除）
- [x] ~~🟢 **1.6 NVAPI 温度重置值不一致**~~ — 已修复
- [x] ~~🟡 **2.1 SMU 参数未显式重置**~~ — 已修复（resetCpuPower + GPU/NVAPI 显式重置）
- [x] ~~🟢 **2.3 恢复默认后再污染 localStorage**~~ — 已修复
- [x] ~~🟠 **3.1 全量存储破坏稀疏语义**~~ — 已修复（按 key 单条存储）
- [x] ~~🟡 **3.3 CPU 睿频开关无去抖无回滚**~~ — 已修复
- [x] ~~🟡 **3.4 GPU 显存档位无去抖**~~ — 已修复
- [x] ~~🟢 **3.5 applySmuBatch 死代码**~~ — 已清理
- [x] ~~🟡 **4.3 快捷启动缺参数**~~ — 已修复
- [x] ~~🔴 **4.5 曲线停止后参数未恢复**~~ — 已修复（reapplyOverrides）
- [x] ~~🟠 **5.1 paramsLoaded 缺失于依赖数组**~~ — 已修复
- [x] ~~🟠 **5.2 服务端 fetch 与 dispatch 竞争**~~ — 已修复
- [x] ~~🟢 **5.4 clearOldParams 副作用**~~ — 已修复
- [x] ~~🟡 **C.2 GPU 锁频状态未持久化**~~ — 已修复

### 自定义风扇曲线 EC 直写 — 已修复
- [x] ~~**⑲ 睡眠/唤醒处理**~~: 已实现 — `FanCurveService.RecoverAfterSleep()` 在 PowerModeChanged 事件中调用，强制重发 ITSM + 重置 ShouldWrite

### v1.4.3 代码审核修复
- [x] ~~🔴 **CpuFreq 缓存变量错误**~~: `_sgDiskTime` → `_cpuFreqTime` — `HardwareAbstractionLayer.cs:633`
- [x] ~~🔴 **DriverBridge.WriteBit 竞态**~~: 添加 `lock (_lock)` 包裹读-改-写 — `DriverBridge.cs:166`
- [x] ~~🟡 **DriverBridge._recovering TOCTOU**~~: `bool` → `int` + `Interlocked.Exchange` — `DriverBridge.cs:38,91`
- [x] ~~🟡 **FanCurveService.Dispose 不清理 Timer**~~: 先 `_timer?.Dispose()` 再恢复固件 — `FanCurveService.cs:230`
- [x] ~~🟡 **RyzenAdj 超时未 Kill**~~: 超时后 `proc.Kill()` — `SmuController.cs:55`
- [x] ~~🟡 **HTML lang 属性错误**~~: `en` → `zh-CN` — `index.html:2`
- [x] ~~🟡 **ESLint 未排除构建产物**~~: `globalIgnores` 补充 server 路径 — `eslint.config.js:8`

### 已修复 Bug
- [x] ~~**🔴 `/api/uxtu/apply` 缺少 `SavePerfOverrides`**~~: 已修复 (v1.4.1) — `BatchApply` 成功后持久化 SMU 四参数到 `performance-overrides.json` — `Program.cs` L1057-1068
- [x] ~~**🔴 风扇曲线停止/恢复后 `reapplyOverrides` 未自动触发**~~: 已修复 (v1.4.1) — `handleStop()` 等 500ms 后调 `reapplyOverrides` 重发全部自定义参数 — `FanCurvePanel.jsx` L245-267
- [x] ~~mockTelemetry.js cpuCores:32~~ — 已修复
- [x] **thermal_mode 走 WMI SystemPerMode 修复**: EC 寄存器直写不触发固件完整模式加载（官方控制台不随动、功率预设不生效）→ 改为 WMI Method 8，与官方控制台/验证脚本一致
- [x] **UI 文案/主题修复**: SliderRow/SwitchRow 灰色状态「EC」→「默认」；灰色滑动条颜色跟随主题 `var(--muted)`
- [x] **恢复默认先停风扇曲线**: App.jsx 恢复默认按钮先 `stopFanCurve()`（如曲线运行中），再发 `resetToFactoryDefaults`，防止 EC 被回写
- [x] **MODE_FAN_DEFAULTS 修正**: 对照 reference-consoles.md 官方预设表修正全部模式风扇默认转速；resetParams/模式切换/启动初始化改为 `FULL_PARAMS + MODE_FAN_DEFAULTS[mode] + overrides` 三层合并
- [x] **模式切换兑态修复 (v1.3.1)**: onClick 精简为 setSettings + toast，dispatchFullMode 内部 SMU 两次延迟重发（500ms + 1500ms），消除前端重复 dispatch — 临时修复，根本方案见 [参数覆盖层重构](design-override-layer.md)
- [x] **前端模式按钮高亮加载时序**: 已修复 — uxtuParams 初始值改用 MODE_PRESETS 而非 defaultParams，消除 CPU 调节滑块闪跳
- [x] **版本号不一致（CHANGELOG/iss/SettingsPanel/package.json）**: 已修复 — build-installer.ps1 新增 `-Version` 参数，构建时自动同步四处版本号 + 打包前自动验证前端版本号
- [x] **WebView2 缓存导致前端版本号不更新**: 已修复 — Shell 启动时仅清除 Cache/Code Cache/GPUCache/Service Worker/ShaderCache（保留 Local Storage/IndexedDB，前端 overrides 持久化依赖 localStorage）。index.html 由后端设置 `Cache-Control: no-cache` 确保新 bundle 不被缓存 — `Form1.cs` L94-107
- [x] SortableDashboard.jsx 重复属性 `showGpu={false}` — 仅一处，非重复 ✅
- [x] 历史曲线图 Sparkline NaN 渲染 — 兜底修复 ✅
- [x] `SettingsPanel.jsx` — `osdDisabled` Toast 提示"暂不支持" ✅

## 附录

### 架构迁移对账
| 旧工具 | 功能 | 新架构位置 | 状态 |
|--------|------|-----------|:----:|
| `ec_reader.cs/.exe` | EC IO 读温度/风扇 | C# HAL `ReadEc()` + Node.js `ec_reader.exe` | ✅ |
| `ec_kb_map.exe` | 键盘背光 0x9A 写入 | C# HAL `KeyboardBrightness` | ✅ |
| `ec_writer.cs/.exe` | EC IO 写风扇 0xB2/0xB3 | `HardwareAbstractionLayer` 已实现 (寄存器写入✅, 物理响应❌) | ✅ 🚧 |
| `demo.bat` | ryzenadj SMU 参数下发 | C# `POST /api/smu/set` | ✅ |
| `WinRing0x64` | 内核驱动 (GPL) | inpoutx64 (MIT) | ✅ |
| `ryzenadj.exe` | SMU CLI | SmuController → ryzenadj.exe → WinRing0 | ✅ |
| `libryzenadj.dll` | SMU C API | ❌ 已从跟踪中删除，由 ryzenadj.exe 替代 | ✅ |
| `readjustService.ps1` | SMU 后台监视器 | ❌ 未迁移 | 🚧 |

### 历史记录
- 2026-06-03: C# HAL 架构 + 双后端部署
- 2026-06-04: AppBridge + 电源计划 + Debug 页 + WS 遥测 + UI重构
- 2026-06-05: GPU 模式真机验证 / SmuController 全链路 / Step C/D/G
- 2026-06-05: 任务看板重构为三板块（Release 1 优先 / 后续 / 已完成）
- 2026-06-06: EC 16 位风扇竞态修复 (HAL 双读仲裁) + TelemetryBackgroundService 无条件推送 250ms + HAL 文档同步
- 2026-06-06: 风扇负载曲线隐藏 (EC 16 位竞态心电图) — 前端 `<Sparkline>` 移除 + `fanPctSeries` 清理 + 文档同步
- 2026-06-06: Vite dev server 废弃 — 前端嵌入 C# wwwroot/，清理 dev/start/server 脚本 + concurrently + 文档架构图
- 2026-06-06: 风扇控制全栈后端 (C# HAL/API/Debug) + WriteEc 0x80→0x81 修复 + WaitEcReady 轮询
- - 2026-06-06: 电源计划按钮修复 — PerformancePanel.jsx POWER_PLANS 添加 halValue，按钮点击实际下发 C# HAL
- - 2026-06-06: 持久化修复 — 风扇滑块/电压偏移 localStorage, fetch 非 custom 模式不覆盖, uxtuParams 初始值改 MODE_PRESETS
- 2026-06-08: GPU 统一卡片 + NVAPI 偏移模式联动 + SMU BatchApply + CPU 性能控制 + 排序跳动修复 + SMU 死代码清理
- 2026-06-09: thermal_mode WMI Method 8 修复 + UI 文案/主题修复 + 恢复默认先停曲线 + MODE_FAN_DEFAULTS 修正
- 2026-06-09: GPU/NVAPI/CPU 非 EC 通道无条件重置 + 风扇竞态修复(thermal_mode await+500ms) + 背景图持久化修复 + 下发逻辑全链路审计(28 项发现写入看板)
- 2026-06-10: 风扇+功耗解耦验证 + ITSM 直写绕过 GPUD 验证 + DPTB 9 条 ALIB 完整解码 (fan-write-investigation §6-7)
- 2026-06-10: 自定义风扇曲线产品设计方案 + 任务组排入看板 (EC 直写 ITSM, 20 项原子任务, 4 阶段)
- 2026-06-11: Phase 1+2 实施完成 — RouteMode 路由函数 + Tick EC 直写 ITSM + Start/Stop 保护 + ITSM 守护统计 + WMI 写入验证锁定检测 + route-info API + FanCurvePanel 全范围 + 路由状态轮询 + getFanRange 改全范围 (后端构建 0 error / 前端构建 0 error)
- 2026-06-12: 持久化缺口审计 + WebView2 缓存修复 — 发现两个 🔴 严重问题：① `/api/uxtu/apply` 缺少 `SavePerfOverrides`（SMU 参数不持久化）；② 风扇曲线停止后 `reapplyOverrides` 未自动触发（ACPI 链重置全部参数无机制补回）；修复 Form1.cs 缓存清理从"删整个 WebView2 目录"改为仅清 Cache/CodeCache/GPUCache/ServiceWorker/ShaderCache（保留 Local Storage/IndexedDB）
- 2026-06-13: v1.4.3 全量代码审核 — 前后端共发现 58 项问题（严重 5 / 高 9 / 中 26 / 低 18）。关键发现：CpuFreq 缓存变量 Bug、CORS AllowAnyOrigin CSRF、DriverBridge.WriteBit 竞态、全应用零 Error Boundary、15 处空 catch 静默吞错、useControlState 全局状态导致不必要重渲染。同时核查任务看板未勾项：7 项已修复可画勾、1 项已废弃（CPU 频率限制 ryzenadj 路径）、11 项问题仍存在。新增审核发现至看板后端/前端分类

---

> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)
