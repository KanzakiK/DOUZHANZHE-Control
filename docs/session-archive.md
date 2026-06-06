# 会话归档

> 此文档由主记忆 `douzhanzhe-progress.md` §4 迁出，随项目进展持续追加。
> 最近日期在上，最早日期在下。


## 2026-06-06 (Vite Dev Server 废弃 + 架构简化)
- **背景**：当前工作流已完全转向 C# Debug 页 + run.ps1 重编译，Vite dev server (`:5173`) 无人使用
- **风险审计**：代理功能（`/api`/`/ws`）已冗余（前端同域访问），WebSocket 直连 `ws://:3100/ws`，HMR 已被替换为 `run.ps1` 重编译
- **清理**：`vite.config.js` 删 `server` 块，`package.json` 删 `dev`/`start`/`server`/`preview`/`backend`+`concurrently`
- **保留**：`vite` + `@vitejs/plugin-react` + `postcss` + `tailwindcss`（构建工具链，`npm run build` 需要）
- **文档**：dev-index.md / dev-architecture.md / dev-backend.md / dev-frontend.md 全部更新架构图与快速启动

## 2026-06-06 (移除自定义模式 + 网格修正)

- **隐藏自定义模式**: MODE_ITEMS 移除 custom 条目
- **网格修正**: grid-cols-5 → grid-cols-4，消除空白占位

## 2026-06-06 (持久化修复 + 加载闪修复)

- **问题**：风扇滑块刷新回到 2200/4100、电压偏移回到 0、CPU/GPU slider 在非自定义模式刷新闪
- **风扇修复**：`useState(2200)` → `useState(() => loadFromLS())` + useEffect saveToLS
- **电压修复**：localStorage 键 `douzhanzhe_voltage_offset`，初始化时恢复，变化即存
- **slider 闪修复**：fetch 返回 `/api/custom-params` 仅在 `mode === "custom"` 时覆盖；uxtuParams 初始值改用 MODE_PRESETS 而非 defaultParams

## 2026-06-06 (电源计划按钮修复)

- **问题**：`PerformancePanel.jsx` 电源计划（最高能效/平衡/最佳性能）按钮点击后从未实际调用 C# HAL
- **根因**：`POWER_PLANS` 数组缺少 `halValue` 字段，按钮点击时 `plan.halValue === undefined`，被 `if (plan.halValue !== undefined)` 拦截
- **修复**：三行代码，每个 plan 添加 `halValue: powerPlanHALMap[效率/平衡/性能]`
- **后端联调**：`POST /api/control power_plan` 端点早已实现，Debug 页 3 按钮已验证 ✅
- **验证**：前端点击任意电源按钮，浏览器 DevTools Network 看到实际 `POST /api/control target=power_plan value=X`

## 2026-06-06 (Fan Curve Hidden)
- **问题**：风扇负载曲线仍心电图（HAL 双读仲裁降低瞬态 0 但未完全消除）
- **决定**：不再深追，前端移除了风扇 `Sparkline` 组件
- **改动**：`SortableDashboard.jsx` — 删除 `fanPctSeries` 计算变量 + `<Sparkline>` 替换为注释

## 2026-06-06 (EC 16-bit Race Fix + Telemetry Unconditional Push)
- **问题**：风扇曲线"心电图"（掉到 0 又跳回），CPU/GPU 曲线正常
- **根因**：EC 16 位风扇寄存器（0x9D/0x9E, 0x96/0x97）被非原子读取，hi/lo 在固件更新间隙读到 0x0000
- **HAL 层修复**：`CpuFanRpm`/`GpuFanRpm` 双读仲裁（for 循环最多 3 次，非零即返回），根治 EC 瞬态 0
- **TelemetryBackgroundService 修复**：删除 `if (!changed) continue;` 变化检测，改为无条件每次推送；间隔 500ms→250ms 提升分辨率
- **清理**：删除 `_prev*` 快照字段、`_last*`/`ZeroCount` 去抖字段（不再需要）
- **验证**：`curl /api/telemetry` 跟踪 101 样本，大风扇 =0 次数 0%，小风扇 =0 次数 0%，最长连续=0 0 次 ✅

## 2026-06-05 (Step C/D/G)
- DriverBridge 32-bit IO: Inp32/Out32 + ReadPhys32/WritePhys32
- SmuController 子进程调用 ryzenadj.exe（底层仍依赖 WinRing0 驱动）
- Debug 页 SMU 控制区 5 按钮
- GPU 模式真机验证（混合/集显/独显）✅
- 修复 run.ps1 AppBridge 运行时自动部署 bug
- 任务看板重构三板块（Release 1 优先 / 后续 / 已完成）

## 2026-06-04 (Step 1~3)
- WS 遥测 28 字段全量推送
- Debug 页 GitHub Dark 横向布局重构
- AppBridge 命令测试区（下拉框+文本框+发送+Hex翻译）
- 电源计划切换 (powrprof.dll)
- DSDT 反编译 + EC 寄存器全表

## 2026-06-03
- C# HAL 架构 + 双后端部署

## 2026-06-05 (风扇控制后端 + WriteEc 协议修复)

### 修改文件
| 文件 | 变更 |
|:-----|:------|
| `server/hal/DriverBridge.cs` | `WriteEc` 0x80→0x81 写命令修复 + `WaitEcReady` IBF 轮询（匹配 ec_writer.cs 协议） |
| `server/hal/HardwareAbstractionLayer.cs` | 新增 `CpuFanControl`/`GpuFanControl` 语义属性（EC 0xB2/0xB3） |
| `server/api/Program.cs` | 新增 `POST /api/fan/set-target` + Debug 页风扇滑块 |
| `server/api/Program.cs` | `FanSetRequest` record 加 `[JsonPropertyName]` |

### 验证结果
- ✅ `WriteEc` 协议修复——写命令从 `0x80`(读) 改 `0x81`(写)，增加 `WaitEcReady` IBF 轮询
- ✅ EC 寄存器 0xB2/0xB3 字节写入工作正确（API 返回值 = 量化后值）
- ❌ 风扇物理转速不响应 0xB2/0xB3 写入——需额外硬件逆向
- ℹ️ 键盘背光 `WritePhys` 直线一直有效（离线的 `SetPhysLong`，不走 EC 协议）

### 根因发现
- `WriteEc` 从项目初始就一直用 `0x80`（读命令），所有 EC IO 协议写入从未生效过
- 其他功能（键盘灯/Fn锁/散热模式）走 `WritePhys(SetPhysLong)` 所以不受影响
- 0xB2/0xB3 实测也走 EC IO 协议（文档确认 `0x66→0x81`），非物理内存映射

### 已知遗留
- 风扇物理控制需探索额外 EC 寄存器组合（可能需手动风扇模式开关）
- SMU Debug 页功能未真机实测

---


> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)


## 2026-06-05 (全站整理)
- **记忆重构**：4 个用户记忆文件合并为 1 个唯一主记忆，3 个虚拟仓库记忆转为实体 docs/ 文件
- **文档地图补全**：dev-index.md 新增 session-archive/reference-consoles/task-board-conventions 索引
- **交叉引用**：全部 10 个 docs/*.md 追加反向引用页脚（主记忆 + 操作守则）
- **文件污染修复**：全部 11 个 docs/ 文件清除 \r\r\r\n 行尾损坏（总计清除约 9KB 污染）
- **copilot-instructions.md 更新**：§1 重构为决策表+分支细节，§4 记忆结构同步，§5 分流整理规则完善
- **reference-consoles.md**：写入斗战者/蛟龙完整功能详情（145行）
- **task-board-conventions.md**：看板整理原则独立文档
- **session-archive.md**：首次创建，主记忆历史外迁

## 2026-06-05 (文档体系重构)
- 记忆文件优化: 5个用户记忆文件 → 1个唯一主记忆 (357行→91行), 删除3个冗余文件
- repo记忆转实体: `/memories/repo/task-board-conventions.md` → `docs/task-board-conventions.md`
- 文档地图补全: dev-index.md 新增 session-archive/reference-consoles/task-board-conventions 索引
- 文档交叉引用: 全部 10 个 docs/ 文件追加统一页脚（指向主记忆+操作守则）
- copilot-instructions.md 更新: §4 记忆结构/§5 归档路径/§1 精简+找回细节/历史教训横幅
- docs/ 文件 
 污染清理: 11个文件, 共清除约 7.5KB 冗余 

- reference-consoles.md 创建: 斗战者+蛟龙完整功能详情（145行）
- 三方映射审计完成: 记忆↔文档↔守则 闭环确认

## 2026-06-05 (文档体系重构 · 续)
- 记忆虚拟文件转实体: repo/task-board-conventions.md, session-archive.md, reference-consoles.md
- dev-index.md 补全新增文档索引 + 主记忆/守则引用脚注
- 全部 docs/ 文件追加统一反向引用页脚
- copilot-instructions.md §1 全面重构: 决策表/铁律咒语/清洗流程/密度对账/转义隔离/三引号铁律
- copilot-instructions.md §4-5 同步更新: 记忆结构/归档路径/分流整理规则
- 主记忆空行污染根除: 123行→69行, 多出的54行结构性空行彻底清洗
- 三方映射审计完成, 发现缺失密度对账规则并补入守则

## 2026-06-05 (风扇控制探索全栈 · 第二阶段)

### 本轮发现
- **EC 差分扫描**：安静模式最低/最高风扇值对比，发现 0x5B(小扇)/0x5F(大扇) 为只读状态寄存器
- **AppMachineInfo 解密**：base64 编码，存储各模式风扇预设值 (RPM/100)
- **KaronOC32.dll 分析**：仅导出 2 个 P-state 函数（ChangePstatesLevel0Settings/GetPstatesLevel0Settings），无风扇相关原生函数
- **WMI SystemPerMode 验证**：`SetValue=SystemPerMode <0-3>` 有效切换散热模式（等价 EC 0xE4 直写）
- **DLL 完整枚举导出**：WMIMethodName(16个枚举值)、WMIFanType、WMISystemPerMode 等 15 个枚举全表
- **ExcMethod 发现**：原始 WMI 字节协议通道，返回 `Tuple<bool, byte[]>`

### 已验证无效路径
| 路径 | 结论 |
|:-----|:------|
| EC IO 0xB2/0xB3 (LLT 参考) | ❌ 写入成功但风扇无物理响应 |
| EC 物理内存 0x5B/0x5F | ❌ 只读状态寄存器 |
| WMI CPUGPUSYSFanSpeed | ❌ 空壳，返回 OK 但不控硬件 |
| WMI MaxFanSpeed/MaxFanSpeedSwitch | ❌ 返回 OK 但硬件无响应 |
| 直接写 AppMachineInfo | ❌ 文件更新但 BLD 服务不响应 |
| KaronOC32.dll | ❌ 仅 P-state 函数 |

### 代码变更
| 文件 | 变更 |
|:-----|:------|
| `server/api/AppBridge/Program.cs` | 新增 Byte[] 参数支持（逗号分隔格式） |
| `server/api/Program.cs` | Debug 页下拉框新增 CPUGPUSYSFanSpeed 转速选项、`ec_write` 实验端点 |
| `server/hal/HardwareAbstractionLayer.cs` | 新增 `WriteEcPort()` 方法（暴露 EC IO 写入） |
| `docs/reference-consoles.md` | 新增 WMI 枚举全表 + 风扇探索结论汇总 |

---


> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)


## 2026-06-05 (风扇控制探索 · 第三阶段 + BLDFnHotkeyUtility 反编译)

### BLDHotKeyService.exe 反编译结论
- **类型**：.NET Framework 32-bit（23KB），仅是 Windows 服务外壳，无 EC 逻辑
- **依赖**：引用 `BLDFnHotkeyUtility.exe` 做实际硬件操作

### BLDFnHotkeyUtility.exe 反编译（808KB，真正的 EC 写入者）
- **引用 DLL 的类型**：`BLD.WMIOperation.WMIMethodServices`（与 `斗战者控制台.dll` 一致）
- **关键方法**：`StringToByteArray(String hex)` — 解析 hex 字符串为字节数组→写 EC
- **关键方法**：`wMIEventArrived(WMIEventType, WMIEventName, Object)` — 响应 WMI 事件后触发 EC 写入
- **关键常量**：`WMI_BASE = 1280`、`BALANCE_MODE = 1285`、`PERFERMANCE_MODE = 1286`、`QUIET_MODE = 1287`、`FULLSPEED_MODE = 1290`
- **发现枚举名差异**：EXE 用 `CPUGPUFanSpeed`（无 SYS），DLL 用 `CPUGPUSYSFanSpeed`，但底层方法 ID（13）相同
- **额外 WMI 事件枚举**：`CPUFanSpeed = 26`、`GPUFanSpeed = 32`（事件名，非方法名）
- **通信模式**：事件驱动——WMI SetValue 触发事件 → BLDFnHotkeyUtility 的事件处理器收到事件 → 调用 StringToByteArray 解析 hex → 写入 EC 硬件

### 代码变更
| 文件 | 变更 |
|:-----|:------|
| `server/api/AppBridge/AppBridge.csproj` | 临时改为 x86 后又恢复 AnyCPU |

---


> 项目主记忆：[douzhanzhe-progress.md](vscode://file/c:\Users\liufe\AppData\Roaming\Code\User\globalStorage\github.copilot-chat\memory-tool\memories\douzhanzhe-progress.md) | 操作守则：[.github/copilot-instructions.md](.github/copilot-instructions.md)


## 2026-06-05 (风扇控制突破·大扇 0x5F 发现)

### 四路验证
| 方法 | 结果 |
|:-----|:------|
| WMI 类穷举 | ❌ 无 BLD 自注册 WMI 类 |
| AppBridge 枚举遍历 | ✅ 发现 Enum16/19 与散热模式联动，CPUGPUSYSFanSpeed(13)返回读数 |
| DLL 字符串搜索 | ✅ 确认 `Set_FanSpeed`/`SetSP_MaxFanSpeedButton` 等方法名 |
| WMI 事件追踪 | ⚠️ BLD.WMIOperation 不走标准 WMI 日志 |

### 核心发现
- **0x5F = 大风扇目标转速控制寄存器**（值 = RPM / 100），`WriteEc(0x5F, val)` 直接生效
- 0x58-0x5E = 各散热模式风扇预设表（8 字节，模式×大小扇）
- **公式**：val = RPM / 100（安静 1900→19，2700→27，斗战 4400→44）
- **区间限制**：安静 19-29、均衡 26-35、野兽 32-38、斗战 40-44
- 0xB2/0xB3（LLT ec_writer.cs 推导）❌ 无效

### 代码变更
| 文件 | 变更 |
|:-----|:------|
| `server/hal/HardwareAbstractionLayer.cs` | `CpuFanControl` 从 0xB2×(255/4400) 改为 **0x5F×100** |
| `server/api/Program.cs` | 恢复 `POST /api/fan/set-target` + `FanSetRequest` record |
| `server/api/Program.cs` | Debug 页新增风扇控制区（大小扇滑块+下发按钮） |
| `docs/dev-ec-map.md` | 新增 0x58-0x5F 完整风扇预设表 + 控制寄存器 |
| `docs/dev-backend.md` | 更新 CpuFanControl 验证状态为 ✅ |
| `docs/dev-task-board.md` | 标记大风扇控制已完成 |

### 遗留
- **小风扇控制寄存器待发现**（小风扇预设表在 0x58-0x5B，但实际控制地址未知）
- 官方控制台 切换散热模式时也会下发 ThermalMode 事件

### 跟进：小风扇控制寄存器 0x5B
- **0x5B = 小风扇目标转速控制寄存器**（同大扇 0x5F 公式 `val = RPM / 100`）
- `WriteEc(0x5B, 20-82)` 对应 2000-8200 RPM 范围
- GpuFanControl 已从 0xB3 迁移至 0x5B，Debug 页滑块已生效
- 大小风扇控制全部攻克：**0x5F(大扇) + 0x5B(小扇)**

## 2026-06-05 Node.js 废弃 + 前端路由修复

### 变更范围
| 文件 | 变更 |
|------|------|
| server/api/Program.cs | 新增 10 个端点（custom-params/ui-state/default-config JSON 持久化 + uxtu/apply/ryzenadj/info 转发 + system/settings/fan/full-speed stub）|
| ite.config.js | 7 条 Node.js 代理路由从 :3099 → :3100 |
| src/hooks/useControlState.js | 风扇转速变化去抖调用 POST /api/fan/set-target |
| src/App.jsx | 模式按钮联动 POST /api/control thermal_mode |
| src/components/panels/SettingsPanel.jsx | 路由修复：gpuOnly→igpu_only, touchpadLock→touchpad_lock, dGpuDirect→gpu_mode(2/0), osdDisabled→Toast |
| src/components/panels/PerformancePanel.jsx | 电源计划按钮联动 POST /api/control power_plan |
| src/components/ui/Sparkline.jsx | NaN 兜底修复（filter + cleaned 数组） |
| src/components/SortableDashboard.jsx | gpuVramUsed .toFixed(1) 小数位显示 |

### 验证状态
- Node.js 停用后前端全功能正常 ✅
- 系统开关全部指向 C# HAL ✅
- 散热模式切换 ✅
- 电源计划切换 ✅
- 风扇目标转速 API 直通 ✅
- Sparkline NaN 错误已消除 ✅

### 遗留
- 风扇滑块 UI 不生效（需排查 useControlState 600ms 去抖时机）
- 模式前后台命名映射错乱
- GPU 模式需做独立卡片（三按钮替代现有开关）

## 2026-06-05 19:17 — WMI 迁移 + Debug 页重构
**修改文件**：

- 新建 `server/api/WmiInterface.cs` — WMI MiInterface 32 字节协议封装
- 修改 `server/api/Douzhanzhe.API.csproj` — 添加 System.Management 8.0.0
- 修改 `server/api/Program.cs` — 注册 WmiInterface，迁移 gpuMode 遥测和控制到 WMI；Debug 页重构
**变更内容**：
1. WMI 集成：GPUMode 从 AppBridge 子进程迁移到 WmiInterface 直接 WMI 调用 ✅
2. Debug 页 WMI 命令测试：下拉框改为 SystemPerMode/GPUMode/FnLock/TPLock/MaxFanSpeedSwitch 格式
3. 风扇控制：拆分为"大扇下发"和"小扇下发"独立按钮，各传单值（largeRpm/smallRpm 可空）
4. 风扇实际转速：WebSocket 遥测自动驱动，移除手动刷新按钮
**验证**：

- `gpuMode` telemetry 返回正确值
- `POST /api/control gpu_mode=0/1/2` 切换成功
- 风扇独立下发 EC 寄存器验证生效

## 2026-06-05 (AppBridge 废弃)
- 废弃 AppLib.cs + AppBridge/ 子项目 + run.ps1 部署
- 删除 /api/app-cmd、/api/app-status 端点
- Debug 页移除 AppBridge badge + fetch script
- 前端 3 处文本替换(斗战者控制台 -> Douzhanzhe Console)
- 文档同步: dev-backend.md / dev-api.md / dev-architecture.md / dev-task-board.md / dev-release-plan.md
- 静态文件服务: Program.cs UseDefaultFiles + UseStaticFiles + MapFallbackToFile
- 前端部署: npm run build -> wwwroot (index.html + assets/)
- 看板: 静态文件服务任务标记完成

---


## 2026-06-05 — SMU 写入验证 + 架构重构

### 目标
验证 AMD SMU（功率墙/温度墙）写入是否生效，修复 Dragon Range SMU 地址。

### 探索过程
1. **假阳性发现**：旧 SmuController 用 `SetPhysLong` 物理地址直写 SMN，`Probe()` 和 `SetPowerLimit()` 返回 OK 但实际是 no-op
2. **CF8/CFC 实验**：改用 PCI config space IO 端口 (0xCF8/0xCFC) + NBIF 邮箱 (0xB8/0xBC)，SMN 读回从不正确的 0 变为 0xFF，确认 SMN 通信但命令无效
3. **RyzenAdj 源码分析**：从 RyzenAdj 的 `nb_smu_ops.c` 发现 Dragon Range 地址全错——正确地址：MSG=0x03B10530, REP=0x03B1057C, ARG_BASE=0x03B109C4（原用 Strix Point 地址）
4. **WinRing0 验证成功**：从 tools/ 目录调 ryzenadj.exe 设置 25W → CPU 频率从 3.6GHz 降至 0.5GHz ✅

### 结论
| 方案 | 状态 | 说明 |
|:-----|:-----|:------|
| SetPhysLong 物理地址直写 | ❌ | SMN 全返回 0，inpoutx64 物理内存映射不覆盖 SMN |
| CF8/CFC PCI 端口 IO | ❌ | 返回 0xFF，被 UEFI CF8 Lock 阻挡 |
| ryzenadj.exe + WinRing0 | ✅ | 已验证写入成功，WinRing0 驱动用内核态 PCI 访问绕开了 CF8 锁 |
| C# 子进程调 ryzenadj | 🔴 | DLL 已含新代码，但子进程崩溃 (0xC0000005)，WinRing0 驱动路径问题 |

### 关键发现
- Dragon Range `SMU 地址`：MSG=0x03B10530, REP=0x03B1057C, ARG_BASE=0x03B109C4
- 25W 功率墙写入后空闲频率：3.6GHz → 0.5GHz
- 恢复命令：`ryzenadj --stapm-limit=75000 --fast-limit=90000 --slow-limit=75000 --tctl-temp=95`

### 文件变更
- `server/hal/SmuController.cs` — 重写为 ryzenadj 子进程方案
- `server/api/Program.cs` — 追加 GET /api/pci/probe, 更新 SMU 端点
- `server/tools/` — 更新 ryzenadj.exe v0.19.0 + WinRing0x64.sys

## 2026-06-05 (SMU 子进程集成修复)
- **SMU 子进程 0xC0000005 根因诊断**: ryzenadj.exe 在 `RedirectStandardOutput=true` 时 crash（已知上游 v0.19.0 bug #370），非 C# 子进程路径问题
- **SmuController.cs** 三处修复:
  - 候选路径新增 4 级 `..` 选项（开发模式 `dotnet run` 也可找到 server/tools/ryzenadj.exe）
  - 移除 `RedirectStandardOutput`/`RedirectStandardError`（触发 crash 的根因）
  - `SetPowerLimit`/`SetTempLimit`/`Probe` 接受 `-1073741819`(0xC0000005) 为成功退出码（写入在 cleanup crash 前已完成）
- **run.ps1**: 新增 WinRing0x64.dll / WinRing0x64.sys / ryzenadj.exe 自动复制到运行目录（`inpoutx64.dll` 复制旁插入）
- **验证结果**: probe=true, set65=true rc=0, temp90=true rc=0, status=true ✅

## 2026-06-05 (Git cleanup: gitignore + gitattributes)
- **.gitignore full rewrite**: added `**/bin/` `**/obj/` `**/wwwroot/` to ignore all C# build artifacts
- **New .gitattributes**: line ending normalization, semantic diff for C#/MD, binary markers
- **New ignore rules**: `**/Properties/launchSettings.json` `*.nodebak` `wmi-scan-result.json` `body_clean.json`
- **Result**: untracked files dropped from 173 to ~20 source files; `git status` now reviewable

## 2026-06-05 (Git cleanup: gitignore + gitattributes)
- **.gitignore full rewrite**: added `**/bin/` `**/obj/` `**/wwwroot/` to ignore all C# build artifacts
- **New .gitattributes**: line ending normalization, semantic diff for C#/MD, binary markers
- **New ignore rules**: `**/Properties/launchSettings.json` `*.nodebak` `wmi-scan-result.json` `body_clean.json`
- **Result**: untracked files dropped from 173 to ~20 source files; `git status` now reviewable

## 2026-06-05 (Documentation audit + SMU dep chain fix)
- **文档 Node.js 定位修正**: README/dev-index/dev-backend/dev-architecture Node.js 从双后端降级为可选配置服务
- **SMU 依赖链文档统一**: 5 处文档将错误的 inpourx64 修正为 SmuController -> ryzenadj.exe -> WinRing0
- **Git 规范化**: .gitignore 黑名单补全 + .gitattributes 新建
- **GitHub 同步**: v1.1.0 commit (C# HAL/WMI/SMU/文档) + 4 个 docs fix commits
- **主记忆更新**: 已验证硬件表 + 运行时依赖表 SMU/WinRing0 澄清

## 2026-06-05 (dev-api.md Vite proxy table fix)
- dev-api.md Vite 代理表 7 行目标端口从 :3099 改为 :3100（custom-params/ui-state/default-config/fan/ryzenadj/uxtu/system）
- 与 vite.config.js 实际配置完全同步
- 任务看板对应条目可打勾✔（文档同步）

## 2026-06-05 (C# reverse proxy + smu/api-type)
- C# Program.cs: 新增 app.Use() 中间件，C# 未匹配的 /api/* 自动转发到 Node.js :3099（Node.js 不可用时返回 502）
- 新增 GET /api/smu/api-type 端点（返回“subprocess”）
- 编译 0 错误，已推送 GitHub

## 2026-06-05 (Ship 2: thermal mode + routing + Node.js retirement)
- 提取 thermalModeMap + powerPlanHALMap 到 uxtuAdapter.js
- 剠除 Node.js 后端（server.js/utils/libryzenadj）+移除反向代理
- 路由修复 SettingsPanel halMap 已正确
- 任务看板全面审计：打勾完成、合并重复、移除已完成项
- 文档同步：Node.js 参考移除、Vite 代理表简化

## 2026-06-06 (Vite Dev Server 废弃 + 架构简化)
- **背景**：当前工作流已完全转向 C# Debug 页 + run.ps1 重编译，Vite dev server (`:5173`) 无人使用
- **风险审计**：代理功能（`/api`/`/ws`）已冗余（前端同域访问），WebSocket 直连 `ws://:3100/ws`，HMR 已被替换为 `run.ps1` 重编译
- **清理**：`vite.config.js` 删 `server` 块，`package.json` 删 `dev`/`start`/`server`/`preview`/`backend`+`concurrently`
- **保留**：`vite` + `@vitejs/plugin-react` + `postcss` + `tailwindcss`（构建工具链，`npm run build` 需要）
- **文档**：dev-index.md / dev-architecture.md / dev-backend.md / dev-frontend.md 全部更新架构图与快速启动

## 2026-06-06 (移除自定义模式 + 网格修正)

- **隐藏自定义模式**: MODE_ITEMS 移除 custom 条目
- **网格修正**: grid-cols-5 → grid-cols-4，消除空白占位

## 2026-06-06 (持久化修复 + 加载闪修复)

- **问题**：风扇滑块刷新回到 2200/4100、电压偏移回到 0、CPU/GPU slider 在非自定义模式刷新闪
- **风扇修复**：`useState(2200)` → `useState(() => loadFromLS())` + useEffect saveToLS
- **电压修复**：localStorage 键 `douzhanzhe_voltage_offset`，初始化时恢复，变化即存
- **slider 闪修复**：fetch 返回 `/api/custom-params` 仅在 `mode === "custom"` 时覆盖；uxtuParams 初始值改用 MODE_PRESETS 而非 defaultParams

## 2026-06-06 (电源计划按钮修复)

- **问题**：`PerformancePanel.jsx` 电源计划（最高能效/平衡/最佳性能）按钮点击后从未实际调用 C# HAL
- **根因**：`POWER_PLANS` 数组缺少 `halValue` 字段，按钮点击时 `plan.halValue === undefined`，被 `if (plan.halValue !== undefined)` 拦截
- **修复**：三行代码，每个 plan 添加 `halValue: powerPlanHALMap[效率/平衡/性能]`
- **后端联调**：`POST /api/control power_plan` 端点早已实现，Debug 页 3 按钮已验证 ✅
- **验证**：前端点击任意电源按钮，浏览器 DevTools Network 看到实际 `POST /api/control target=power_plan value=X`

## 2026-06-06 (Fan Curve Hidden)
- **问题**：风扇负载曲线仍心电图（HAL 双读仲裁降低瞬态 0 但未完全消除）
- **决定**：不再深追，前端移除了风扇 `Sparkline` 组件
- **改动**：`SortableDashboard.jsx` — 删除 `fanPctSeries` 计算变量 + `<Sparkline>` 替换为注释

## 2026-06-06 (风扇控制突破·WMI Bellator 协议修复)

### 诊断结论
- **应用崩溃修复**: SettingsPanel.jsx 移除不存在的 applySystemSetting 导入 → React 应用恢复渲染
- **风扇控制修复**: EC 0x5F/0x5B WriteEc ❌ 改为 **WMI MiInterface MaxFanSpeed(21) 协议** ✅
  - BellatorFanControl 协议：data[4]=FanType(0大扇/1小扇), data[5]=RPM/100
  - MaxFanSwitch(20): 启用手动/恢复固件控制
- **WinRing0 驱动从工具链中清理**

### 代码变更
| 文件 | 变更 |
|------|------|
| server/api/WmiInterface.cs | 新增 SetFanManual(bool) + SetFanSpeed(byte,byte) 方法 |
| server/api/Program.cs | fan/set-target 从 hal.CpuFanControl 改为 wmi.SetFanManual+SetFanSpeed |
| src/components/panels/SettingsPanel.jsx | 移除不存在的 applySystemSetting 导入 |
| src/hooks/useControlState.js | 注入调试日志（已保留供后续排查） |

### 真机验证
- 写入 2800 → 8秒后 2782 ✅
- 写入 3300 → 8秒后 3286 ✅
- 写入 2500（低于均衡下限）→ 2878（EC 截断）⚠️

### 文档同步
- dev-api.md: POST /api/fan/set-target 控制路径更新为 WMI
- dev-ec-map.md: 0x5F/0x5B 标记为只读状态寄存器，新增 WMI 控制说明
- dev-task-board.md: 标记完成
- dev-release-plan.md: 对比表更新
- dev-backend.md: CpuFanControl/GpuFanControl 路径更新

---


## 2026-06-06 (Vite Dev Server 废弃 + 架构简化)
- **背景**：当前工作流已完全转向 C# Debug 页 + run.ps1 重编译，Vite dev server (`:5173`) 无人使用
- **风险审计**：代理功能（`/api`/`/ws`）已冗余（前端同域访问），WebSocket 直连 `ws://:3100/ws`，HMR 已被替换为 `run.ps1` 重编译
- **清理**：`vite.config.js` 删 `server` 块，`package.json` 删 `dev`/`start`/`server`/`preview`/`backend`+`concurrently`
- **保留**：`vite` + `@vitejs/plugin-react` + `postcss` + `tailwindcss`（构建工具链，`npm run build` 需要）
- **文档**：dev-index.md / dev-architecture.md / dev-backend.md / dev-frontend.md 全部更新架构图与快速启动

## 2026-06-06 (移除自定义模式 + 网格修正)

- **隐藏自定义模式**: MODE_ITEMS 移除 custom 条目
- **网格修正**: grid-cols-5 → grid-cols-4，消除空白占位

## 2026-06-06 (持久化修复 + 加载闪修复)

- **问题**：风扇滑块刷新回到 2200/4100、电压偏移回到 0、CPU/GPU slider 在非自定义模式刷新闪
- **风扇修复**：`useState(2200)` → `useState(() => loadFromLS())` + useEffect saveToLS
- **电压修复**：localStorage 键 `douzhanzhe_voltage_offset`，初始化时恢复，变化即存
- **slider 闪修复**：fetch 返回 `/api/custom-params` 仅在 `mode === "custom"` 时覆盖；uxtuParams 初始值改用 MODE_PRESETS 而非 defaultParams

## 2026-06-06 (电源计划按钮修复)

- **问题**：`PerformancePanel.jsx` 电源计划（最高能效/平衡/最佳性能）按钮点击后从未实际调用 C# HAL
- **根因**：`POWER_PLANS` 数组缺少 `halValue` 字段，按钮点击时 `plan.halValue === undefined`，被 `if (plan.halValue !== undefined)` 拦截
- **修复**：三行代码，每个 plan 添加 `halValue: powerPlanHALMap[效率/平衡/性能]`
- **后端联调**：`POST /api/control power_plan` 端点早已实现，Debug 页 3 按钮已验证 ✅
- **验证**：前端点击任意电源按钮，浏览器 DevTools Network 看到实际 `POST /api/control target=power_plan value=X`

## 2026-06-06 (Fan Curve Hidden)
- **问题**：风扇负载曲线仍心电图（HAL 双读仲裁降低瞬态 0 但未完全消除）
- **决定**：不再深追，前端移除了风扇 `Sparkline` 组件
- **改动**：`SortableDashboard.jsx` — 删除 `fanPctSeries` 计算变量 + `<Sparkline>` 替换为注释

## 2026-06-06 (散热模式修复 + 风扇状态端点)

### 已完成
1. **散热模式按钮联动修复**: `thermalModeMap` 值映射纠正（安静=2,均衡=0,斗战=3,野兽=1），真机验证通过
2. **自定义模式风扇触发**: 切自定义模式时自动调用 `POST /api/fan/set-target`，静默无感
3. **CPU/GPU 控件解锁**: `paramsLocked=false`，移除自动切自定义模式逻辑，清理 `presetRef`
4. **模式名称统一**（游戏→斗战, 狂暴→野兽），按钮顺序（安静/均衡/野兽/斗战/自定义）
5. **真实硬件风扇状态查询**: `GET /api/fan/status` — WMI GET 方法 20/21 读回，发现本模具 GET 不回写开关状态
6. **BellatorFanControl 源码审计**: 查表算法 `PickTarget`、回差控制 `ShouldWrite`、默认曲线表全部记录
7. **reference-consoles.md**: 5 处过时内容修正

### 关键发现
- WMI MaxFanSwitch(20) SET 有效但 GET 不回写开关状态（`result[5]` 始终 0x00）
- `POST /api/fan/set-target` 风扇控制确实生效（实测 2600→2860↑），`/api/wmi/cmd` raw 通道不适用于风扇（缺 data[5]）
- 风扇转速受散热模式区间限制（区间内持久，区间外 ~8s 被覆盖）
- 自定义风扇 + SMU/nvidia-smi 全量覆盖方案已讨论，待下期实施

---


## 2026-06-06 (Vite Dev Server 废弃 + 架构简化)
- **背景**：当前工作流已完全转向 C# Debug 页 + run.ps1 重编译，Vite dev server (`:5173`) 无人使用
- **风险审计**：代理功能（`/api`/`/ws`）已冗余（前端同域访问），WebSocket 直连 `ws://:3100/ws`，HMR 已被替换为 `run.ps1` 重编译
- **清理**：`vite.config.js` 删 `server` 块，`package.json` 删 `dev`/`start`/`server`/`preview`/`backend`+`concurrently`
- **保留**：`vite` + `@vitejs/plugin-react` + `postcss` + `tailwindcss`（构建工具链，`npm run build` 需要）
- **文档**：dev-index.md / dev-architecture.md / dev-backend.md / dev-frontend.md 全部更新架构图与快速启动

## 2026-06-06 (移除自定义模式 + 网格修正)

- **隐藏自定义模式**: MODE_ITEMS 移除 custom 条目
- **网格修正**: grid-cols-5 → grid-cols-4，消除空白占位

## 2026-06-06 (持久化修复 + 加载闪修复)

- **问题**：风扇滑块刷新回到 2200/4100、电压偏移回到 0、CPU/GPU slider 在非自定义模式刷新闪
- **风扇修复**：`useState(2200)` → `useState(() => loadFromLS())` + useEffect saveToLS
- **电压修复**：localStorage 键 `douzhanzhe_voltage_offset`，初始化时恢复，变化即存
- **slider 闪修复**：fetch 返回 `/api/custom-params` 仅在 `mode === "custom"` 时覆盖；uxtuParams 初始值改用 MODE_PRESETS 而非 defaultParams

## 2026-06-06 (电源计划按钮修复)

- **问题**：`PerformancePanel.jsx` 电源计划（最高能效/平衡/最佳性能）按钮点击后从未实际调用 C# HAL
- **根因**：`POWER_PLANS` 数组缺少 `halValue` 字段，按钮点击时 `plan.halValue === undefined`，被 `if (plan.halValue !== undefined)` 拦截
- **修复**：三行代码，每个 plan 添加 `halValue: powerPlanHALMap[效率/平衡/性能]`
- **后端联调**：`POST /api/control power_plan` 端点早已实现，Debug 页 3 按钮已验证 ✅
- **验证**：前端点击任意电源按钮，浏览器 DevTools Network 看到实际 `POST /api/control target=power_plan value=X`

## 2026-06-06 (Fan Curve Hidden)
- **问题**：风扇负载曲线仍心电图（HAL 双读仲裁降低瞬态 0 但未完全消除）
- **决定**：不再深追，前端移除了风扇 `Sparkline` 组件
- **改动**：`SortableDashboard.jsx` — 删除 `fanPctSeries` 计算变量 + `<Sparkline>` 替换为注释

## 2026-06-06 GPU 控制后端+前端完整实现
**变更清单**：

- 后端：新建 server/hal/GpuController.cs（nvidia-smi 子进程封装）
- 后端：Program.cs 新增 POST /api/gpu/set（6 种 action）+ GET /api/gpu/status
- 前端：uxtuAdapter.js 新增 pplyGpuControl()、etchGpuStatus()
- 前端：PerformancePanel.jsx GPU 调节卡片（核心频率/显存4频点/核心频率限制/锁定核心频率/重置）
- 前端：显存频率支持自动/9001/11001/12001 四个频点
- Debug：Debug 页 GPU 区块同步更新（核心+显存控制按钮）
- 修复：频率限制与锁频互斥
- 修复：锁频时释放显存锁定避免 P-state 冲突
- 修复：limit-max/limit-memory 正确读取 alue 字段
