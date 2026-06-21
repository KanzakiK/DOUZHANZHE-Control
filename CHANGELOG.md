# Changelog

该项目所有重要变更均会记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本语义遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [1.6.7] — 2026-06-21

配置稳定性修复 + 混合模式外接显示器修复 + 风扇守护 + Shell 崩溃自动恢复

### 修复

- **模式配置互相污染**: 快速切换模式或调节参数后切模式，会导致一个模式的设置（如功耗、频率）被错误写入另一个模式的配置文件。现在所有参数写入都会钉死到正确的模式文件
- **混合模式下外接显示器卡顿**: 混合模式（Optimus）下，程序每 60 秒下发 GPU 锁频命令干扰了显卡电源管理。现在混合模式下自动跳过 GPU 时钟锁定，NVAPI 超频偏移和温度墙不受影响
- **手动风扇转速达不到设定值**: 风扇转速设好后很快被 EC 固件拉回默认值。现在增加 EC 寄存器直写 + ParameterGuard 每 60 秒守护，与自定义风扇曲线使用相同的写入方式
- **后端崩溃后界面无法访问**: 后端进程异常退出后，界面卡在 `ERR_CONNECTION_REFUSED` 不会自动恢复。现在 Shell 每 8 秒检测后端健康状态，崩溃后自动重启并刷新界面
- **部分参数被意外重置**: 参数守护（ParameterGuard）在某些场景下会用默认值覆盖用户自定义设置（如超频偏移被重置为 0、睿频被意外开启）。现在只在配置文件中有对应值时才下发

### 改进

- **模式切换即时响应**: 去掉了 400ms 的切换延迟，点击模式按钮立即生效，切换期间界面会短暂锁定防止误操作
- **GPU 面板智能禁用**: 混合模式下自动禁用核心频率/显存频率调节（这些在混合模式下无效），集显模式下禁用所有 GPU 控件
- **风扇曲线停止后自动恢复参数**: 关闭自定义风扇曲线后，之前被曲线覆盖的功耗、频率等参数会自动恢复

## [1.6.3] — 2026-06-18

修复部分用户 CPU 温度始终为 0 的问题（回退寄存器修正）

### 修复

- **CPU 温度 EC 回退寄存器修正**: 1.6.1 引入的 EC 回退使用了错误的寄存器地址 `0xE1`，在部分机器上仍然读不到温度。修正为 v1.4.8 验证过的 `0x1C`（IO 端口协议），并增加 `<128` 上界校验防止读到异常数据

## [1.6.2] — 2026-06-18

增强 CPU 温度读取诊断 + 三路回退

### 修复

- **CPU 温度三路回退**: LHM SMN → EC IO 端口协议 (0x62/0x66) → EC 物理内存映射，增加 EC IO 端口读路径
- **三路诊断日志**: 当三路均返回 0 时，打印每路的具体返回值，便于精确定位哪个环节失效

## [1.6.1] — 2026-06-18

修复部分用户 CPU 温度始终显示为 0 的问题

### 修复

- **CPU 温度读取双路径回退**: 当 LHM SMN 总线读取失败时，自动回退到 EC 寄存器 0xE1（物理内存映射），解决部分用户机器上 CPU 温度始终显示为 0 的问题
- **LHM 传感器诊断日志**: 启动时打印所有 CPU 温度传感器名称和数值，便于排查硬件兼容性问题

### 技术细节

- `CpuTemperature` 属性改为 LHM 优先、EC 兜底的双路径架构，与 `GpuTemperature` 的回退策略保持一致
- EC 回退值增加 `<128` 上界校验，防止读到异常数据

## [1.6.0] — 2026-06-18

游戏进程自动切换性能模式 + OSD 切换提示

### 新增

- **游戏自动切换**: 检测到游戏进程启动时自动切换到预设性能模式，游戏退出时恢复原模式。支持多开场景（如同时挂原神 + 明日方舟），以最高优先级模式为准
- **游戏管理 Tab**: 新增"游戏"标签页，支持手动添加游戏规则、全局开关、默认模式设置
- **Steam / Epic 游戏扫描**: 一键扫描已安装的 Steam 和 Epic 游戏，弹窗勾选后批量添加到规则列表，自动过滤工具类程序
- **主页状态指示**: 自动切换激活时，主页显示当前运行的游戏和目标模式
- **OSD 模式切换提示**: 切换性能模式时（手动或自动），屏幕底部居中显示 OSD 提示，带模式主题色胶囊边框（安静=绿、均衡=蓝、野兽=橙、斗战=红），2-3 秒后自动淡出

### 改进

- **添加游戏流程优化**: 对话框第一步改为浏览选择路径（自动提取进程名），游戏名称默认用文件名填充，去掉多余的进程名输入框
- **自定义风扇曲线跨模式保持**: 手动和自动切换模式时均保持自定义风扇曲线运行，仅"恢复默认"和用户手动关闭才停止

### 技术细节

- 使用 WMI `__InstanceCreationEvent`/`__InstanceDeletionEvent`（WITHIN 2 轮询）检测游戏进程启停
- 模式切换完全复用前端 dispatchFullMode 流程，后端不碰性能参数
- 3 秒延迟退出防止游戏重启时误恢复
- 休眠唤醒后自动重新订阅 WMI 事件
- OSD 使用 Win32 分层窗口（WS_EX_LAYERED）+ 逐像素 alpha，独立 STA 线程运行，"填外胶囊再填内胶囊"实现均匀 6px 边框
- 3 秒 OSD 冷却防止快速切换时重复弹出
- 新增 `/api/osd/show` API 端点，支持自定义 OSD 消息
- 游戏扫描通过 Steam `libraryfolders.vdf` + `appmanifest_*.acf` 和 Epic `Manifests/*.item` 自动发现已安装游戏，智能定位主 exe

## [1.5.1] — 2026-06-18

修复开机自启风扇转速归零 + 待机自动关机

### 修复

- **开机自启后风扇转速显示为 0（彻底修复）**: 1.5.0 的重试机制未能解决根本问题——inpoutx64.dll 在被首次加载时，如果驱动服务尚未运行，会将设备打开失败的结果永久缓存，之后即使服务启动了也无法恢复。现在在加载 DLL 之前先确保驱动服务已启动，彻底解决此问题
- **待机时自动关机**: 参数守护和遥测恢复机制在系统进入睡眠的过渡期间仍在下发 SMU 指令（功耗/降压等），干扰了正在下电的硬件导致关机。现在睡眠期间自动暂停所有硬件写入，唤醒后等系统稳定再恢复

### 改进

- **驱动初始化诊断**: Init() 增加详细日志（DLL 路径、设备打开状态、耗时），方便排查底层驱动加载问题
- **遥测冷启动预热**: 后台遥测服务启动后等待 2 秒再开始采集，避免驱动尚未就绪时触发 HealthWatchdog 误判

## [1.5.0] — 2026-06-17

遥测稳定性优化 + 模式切换参数同步修复

### 新增

- **遥测健康守护**: 后台持续监控传感器数据，发现温度/频率/功耗归零时自动恢复，解决长时间运行或休眠后遥测数据卡死在 0 的问题
- **参数自动重发**: 每 60 秒自动重新下发自定义参数（CPU 功耗/频率、GPU 频率、风扇转速等），防止系统或固件静默重置导致设置悄悄失效
- **CPU 温度读取优化**: 改用 LibreHardwareMonitor 直读 CPU 温度，修复部分场景下温度跳动或归零的问题
- **统一日志系统**: 所有模块共用一个日志文件（2MB 自动轮转），方便排查问题
- **日志导出**: 关于卡片新增"导出日志"按钮，一键导出日志文件，方便发给开发者排查问题
- **电源计划持久化**: 选择的电源计划（平衡/高性能/节能）会保存下来，重启后自动恢复

### 修复

- **切换模式后 SMU 和 CPU 设置没有保存**: 切换散热模式时，SMU 功耗/温度参数和 CPU 频率限制/睿频/核心数限制没有被写入配置文件（GPU、NVAPI、风扇不受影响，走的是独立保存通道）。原因是批量保存端点内部组件加载失败导致整个保存流程崩溃，现已修复
- **核心数限制数值存错**: 设置 8 核限制时被错误地存为 8%（应该是 50%），导致参数守护进程重新下发时核心数严重偏低。现已修正换算逻辑
- **睿频开关冲突**: 睿频同时被两条通道控制，可能互相覆盖导致状态不一致。现在统一由一条通道管理

### 改进

- **风扇转速读取**: 更快更稳定的读取方式，减少偶尔跳数
- **风扇偏差自动修复**: 检测到风扇转速偏离目标时，从仅记录升级为自动修复
- **底层通信稳定性**: EC 寄存器通信加超时保护，SMN 总线加互斥锁，减少硬件通信偶发异常
- **日志降噪**: ryzenadj 已知上游崩溃（参数已写入成功，仅退出码异常）不再刷屏日志

## [1.4.9] — 2026-06-14

修复设置丢失和模式切换残留问题

### 修复

- **CPU 功耗/频率设置重启后丢失**: 通过性能面板调整的 CPU 参数没有被写入配置文件，导致每次重启应用都要重新设置。现在这些设置会正确保存，重启后自动恢复
- **模式切换后 GPU 设置残留**: 从设过 GPU 频率的模式切到没设过的模式时，上一个模式的 GPU 频率锁没有解除，现在切换模式会自动清理不再需要的 GPU 设置

### 改进

- **启动更快**: 跳过了每次启动都探测显卡是否支持超频的步骤（目标机型固定，无需重复检测）
- **版本号自动同步**: 关于页面的版本号现在跟随 package.json 自动更新，不再需要手动改两处

## [1.4.7] — 2026-06-13

修复 CPU 遥测数据不准确

### 修复

- **CPU 占用率不准确**: `CpuUsage` 使用 `PerformanceCounter` 每次创建新实例导致采样不完整，且 `Sleep(200)` 阻塞遥测线程。改用 Win32 `GetSystemTimes` API 通过两次采样差值计算全核总占用率，无需阻塞，数据更准确
- **CPU 频率显示偏差**: `CpuFreq` 同样每次新建 `PerformanceCounter` 并 `Sleep(200)`，且基频硬编码为 2.4 GHz 导致频率值偏差。改为持久化 `PerformanceCounter` 实例（去掉 Sleep），启动时通过 WMI 自动检测真实基频

## [1.4.5] — 2026-06-13

提升进程与线程优先级，合并 SMU 命令为单次调用，加速启动恢复

### 新增

- **进程优先级提升**: 启动时将进程优先级设为 `High`，主线程优先级设为 `Highest`，防止游戏高负载时系统调度器延迟 HAL 线程执行，确保 EC 寄存器采样、风扇曲线控制等实时任务不被饿死
- **SMU 命令合并**: 启动/睡眠恢复时的 4 次串行 ryzenadj 调用（stapm、short power、temp、CO）合并为单次 `BatchApply` 调用，设置恢复耗时从 ~11 秒降至 ~3 秒

## [1.4.4] — 2026-06-13

代码质量审核修复 — 修复遥测缓存、硬件寄存器竞态、进程残留等问题

### 问题

- 遥测面板 CPU 频率刷新不稳定：缓存命中条件错误地依赖了磁盘刷新时间戳，导致缓存经常不命中，每次都走 200ms 的阻塞直读路径，遥测更新明显卡顿
- 底层驱动寄存器的"读-改-写"操作没有加锁：虽然目前只有一个调用点，但如果未来出现并发写入，会导致寄存器位丢失，硬件行为不可预测
- 睡眠恢复流程中恢复标志的设置不是原子操作：理论上两个线程可能同时进入恢复流程，导致驱动重复初始化
- 风扇曲线服务关闭时没有先停定时器：Timer 回调可能在服务 Dispose 之后仍然执行一次
- ryzenadj 子进程超时后没有被强制终止：如果 ryzenadj 卡住超过 15 秒，进程会一直残留
- HTML 页面语言标记为英文：屏幕阅读器会按英文发音朗读中文界面
- 安装后后端无法启动：`dotnet publish -r win-x64` 将 RID 专属 DLL 展平到根目录，但 `deps.json` 仍指向 `runtimes/win/lib/net8.0/` 路径，当 `runtimes/` 目录存在时（WebView2 创建），.NET 运行时优先走 RID 路径找不到文件直接抛 `FileNotFoundException`（涉及 SystemEvents、EventLog、PerformanceCounter、System.Management 等 6 个 DLL）

### 修复

- **CPU 频率缓存命中修复**: 将缓存检查的时间戳从磁盘刷新时间改为 CPU 频率读取时间，0.5 秒内的重复查询正确命中缓存，遥测热路径不再频繁阻塞
- **寄存器写入加锁**: 对底层驱动的位操作方法添加互斥锁，防止并发写入导致寄存器损坏
- **恢复标志原子化**: 睡眠恢复标志改为原子操作（`Interlocked.Exchange`），杜绝并发重入
- **风扇曲线 Timer 清理**: 服务 Dispose 时先停止定时器再恢复固件控制，避免回调在销毁后执行
- **ryzenadj 超时清理**: 子进程 15 秒超时后强制 Kill，防止残留进程占用资源
- **HTML 语言修正**: 页面语言属性从 `en` 改为 `zh-CN`
- **RID DLL 路径修复**: 构建脚本自动扫描 `deps.json` 的 `runtimeTargets`，将展平的 DLL 复制到正确的 `runtimes/win/lib/net8.0/` 路径，解决安装后后端无法启动的问题

### 改进

- **ESLint 配置**: 排除后端构建产物目录，lint 不再扫描压缩后的 React 产物报无意义错误
- **任务看板整理**: 核查历史审计任务，7 项已修复画勾、1 项废弃、新增 18 项审核发现、已完成项归入已完成分类
- **仓库清理**: 移除临时测试项目和旧构建产物备份，更新 `.gitignore` 规则

## [1.4.3] — 2026-06-13

睡眠恢复完整参数下发 — 修复唤醒后自定义参数未重新应用

### 问题

- 系统从睡眠恢复时，仅恢复了底层驱动和 NVAPI，但所有硬件控制参数（CPU 频率/功耗、SMU 功耗/温度、GPU 频率/显存、NVAPI 超频/功耗/温度、GPU 模式、固定风扇转速、自定义风扇曲线 ITSM 模式）均未重新下发，导致睡眠后 EC 重置回 BIOS 默认状态，所有自定义设置失效
- 固定风扇转速仅存储在浏览器 localStorage，服务端无持久化，睡眠后无法恢复

### 修复

- **完整参数恢复**: 提取 `RestoreAllPerfSettings()` 共享函数（启动 + 睡眠共用），睡眠唤醒后自动恢复 CPU 频率限制/睿频/核心限制、SMU 功耗/温度/CO、GPU 核心频率/显存/锁频、NVAPI 超频偏移/功耗/温度限制
- **GPU 模式恢复**: 睡眠恢复时从 `gpu-mode.json` 恢复混合/独显模式
- **风扇转速服务端持久化**: 新增 `FanOverrides` 类，`/api/fan/set-target` 保存固定风扇转速到 `performance-overrides.json`，`/api/fan/restore` 清除保存值
- **风扇曲线 ITSM 重发**: `FanCurveService.RecoverAfterSleep()` 在睡眠后立即重发 ITSM 模式 + 重置 ShouldWrite 状态，确保下一个 Tick 必定写入
- **异步执行**: `PowerModeChanged` 改为 `Task.Run` 异步执行，避免阻塞事件回调线程

### 改进

- **检查更新弹窗**: 点击关于卡片的“检查更新”按钮现在会弹出完整的更新对话框（显示更新日志 + 跳过/稍后/下载按钮），而不是仅显示 toast 通知

### 新增

- `Microsoft.Win32.SystemEvents` NuGet 包依赖（提供 `PowerModeChanged` 事件）

## [1.4.2] — 2026-06-13

睡眠/休眠恢复支持 — 修复系统唤醒后后端进程崩溃退出

### 问题

- 系统从 S3/S4 睡眠恢复时，`inpoutx64` 内核驱动状态丢失，但 `DriverBridge` 的 `_ecMap` 物理内存映射指针未失效。`TelemetryBackgroundService` 每 250ms 读取硬件时访问野指针，触发 `AccessViolationException`（.NET 8 无法被 try/catch 捕获），进程直接被 OS 杀死，前端所有 fetch 报 "Failed to fetch"

### 修复

- **DriverBridge 睡眠恢复**: 新增 `RecoverAfterSleep()` 方法，立即将 `_driverOk=false` + `_ecOk=false`（让遥测 Tick 安全返回 0），等 1.5s 驱动稳定后重新 `Init()`
- **PowerModeChanged 事件监听**: `Program.cs` 注册 `SystemEvents.PowerModeChanged`，系统唤醒时自动调用 `RecoverAfterSleep()` + `NvapiGpuController.Init()`
- **P/Invoke 异常保护**: `ReadPhys` / `ReadEc` / `WriteEc` / `WaitEcReady` / `ReadIo` / `WriteIo` 全部加 `SEHException` 捕获，驱动异常时自动降级为安全默认值
- **ReadPhys 多级降级**: EC 预映射区域访问加 `AccessViolationException` 捕获，失败时降级到 `GetPhysLong` 兆底路径
- **字段线程安全**: `_init` / `_driverOk` / `_ecMap` / `_ecOk` / `_recovering` 改为 `volatile`，确保跨线程可见性

### 新增

- `Microsoft.Win32.SystemEvents` NuGet 包依赖（提供 `PowerModeChanged` 事件）

## [1.4.1] — 2026-06-12

持久化修复 — 覆盖安装 / 重启后用户自定义参数不再丢失

### 修复

- **覆盖安装丢失前端配置**: 安装程序 (ISS) 在 `ssInstall` 阶段对整个 WebView2 用户数据目录执行 `DelTree`，导致 `%LOCALAPPDATA%\Douzhanzhe Console\WebView2\EBWebView\Default\Local Storage` 被删除，前端 overrides（`douzhanzhe_overrides_{mode}`）全部丢失。现改为仅清除 Cache / Code Cache / GPUCache / Service Worker / GrShaderCache / ShaderCache 六个缓存目录，保留 Local Storage 和 IndexedDB — `douzhanzhe-setup.iss`
- **Shell 启动缓存清理路径错误**: `Form1.cs` 中缓存目录路径缺少 `EBWebView\` 前缀（实际结构为 `userDataDir\EBWebView\Default\Cache` 而非 `userDataDir\Default\Cache`），`Directory.Exists` 始终返回 false，等于启动时从未清理缓存。现已补上正确前缀 — `Form1.cs` L97-98
- **`/api/uxtu/apply` SMU 参数不持久化**: 批量下发端点只调 `BatchApply()` 不调 `SavePerfOverrides()`，通过此端点下发的 SMU 四参数（stapm / short-power / temp / CO）不会写入 `performance-overrides.json`，重启后丢失。各独立端点已有持久化，唯独此批量端点遗漏。现已补上 — `Program.cs` L1057-1068
- **风扇曲线停止后参数未恢复**: `FanCurveService.Stop()` 通过 `SetThermalMode` 切回原模式触发 ACPI 链，SMU/CPU/GPU/NVAPI 全部回出厂预设，用户自定义参数一次性丢失。`handleStop()` 仅恢复风扇转速 override。现在 `Stop()` 完成后等 500ms（让固件完成模式切换），再调 `reapplyOverrides(mode, overrides)` 重发全部自定义参数 — `FanCurvePanel.jsx` L245-267
- **首页风扇卡片停止逻辑不一致**: `SortableDashboard` 首页停止按钮只恢复风扇转速（`/api/fan/set-target` 或 `/api/fan/restore`），未调 `reapplyOverrides`，SMU/GPU/NVAPI/CPU 参数全丢。现已对齐 `FanCurvePanel.handleStop` 行为：停曲线 → 等 500ms → `reapplyOverrides` — `SortableDashboard.jsx` L170-193

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
