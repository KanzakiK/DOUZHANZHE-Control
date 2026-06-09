# Changelog

该项目所有重要变更均会记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本语义遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

## [1.3.2] — 2026-06-09

### 新增

- **版本更新推送**: 启动时自动检查 GitHub Release 最新版本，弹窗显示更新日志（支持跳过此版本/稍后提醒/前往下载）；设置页“关于”卡片新增手动检查更新按钮
- **构建脚本版本号变量化**: `build-installer.ps1 -Version` 参数通过 ISCC `/dMyAppVersion=` 直接传入 Inno Setup，iss 文件改用 `#ifndef` 条件编译，不再需要正则替换文件内容

### 修复

- **NVAPI 和 CPU powercfg API 端点恢复**: 从 git 历史恢复被 `198f650` 提交意外删除的 8 个 API 端点，NVAPI 超频和 CPU 频率控制功能现已恢复正常
  - `POST /api/nvapi/overclock` - NVAPI 超频偏移
  - `POST /api/nvapi/thermal-limit` - NVAPI 温度限制
  - `GET /api/nvapi/status` - NVAPI 状态查询
  - `POST /api/cpu/freq-limit` - CPU 频率限制
  - `POST /api/cpu/turbo` - CPU 睿频开关
  - `POST /api/cpu/core-limit` - CPU 核心数限制
  - `POST /api/cpu/reset` - CPU powercfg 重置
  - `GET /api/cpu/status` - CPU powercfg 状态查询
- **GPU 模式 WMI 映射修正**: 独显=1, 集显=2（之前映射错误导致模式切换异常）
- **GPU 模式启动恢复加固**: 启动时正确读取并恢复上一次 GPU 模式
- **背景上传预览修复**: 首次上传/删除后使用 createObjectURL 即时预览，无需等待后端响应
- **ASP.NET Core Runtime 依赖检测**: 安装包安装时自动检测并安装 ASP.NET Core Runtime（之前只检测 .NET Desktop Runtime）
- **run.ps1 保留 config**: 构建部署时不覆盖用户配置文件
- **ISCC 路径修正**: Inno Setup 编译器默认路径更新为当前用户安装位置
- **性能模式切换竞态修复**: 模式切换统一由 `useControlState` 单路径下发，`dispatchFullMode` 内部改为两次延迟 SMU 重发（500ms + 1500ms），消除前端重复 dispatch 导致的固件覆盖问题

## [1.2.1] — 2026-06-09

### 修复

- **自定义背景重启丢失修复**：移除前端启动时强制覆盖逻辑，开关状态、透明度、遮罩颜色由本地 `localStorage` 决定，重启后不再被重置
- **自定义背景 API 实现**：新增 `/api/background` 和 `/api/background-opts` 端点，支持图片上传/读取/删除与配置持久化
- **开机自启丢失修复**：移除计划任务 XML 中不兼容的 `DisallowStartOnRemoteAppSession` 节点，解决部分 Windows 版本创建失败问题
- **安装包权限错误修复**：Shell/API 添加 `app.manifest` 引用获取管理员权限，安装脚本使用 `runascurrentuser` 避免覆盖安装时错误 740
- **配置目录权限修复**：后端配置统一写入 `%LOCALAPPDATA%`，避免 `Program Files` 下的权限拦截
- **WebView2 缓存自动清理**：Shell 启动时自动清除旧缓存，确保前端更新后立即生效

### 基础设施

- **构建脚本版本号自动化**：`build-installer.ps1` 新增 `-Version` 参数，构建时自动同步四处版本号（CHANGELOG/SettingsPanel/iss/package.json）+ 打包前验证
- **修复版本号不一致**：纠正源码中四处版本号与实际版本不符的问题

## [1.2.0] — 2026-06-08

### 新增

- **桌面壳 (Douzhanzhe.Shell)**: WinForms + WebView2 原生桌面应用，取代浏览器访问
  - 单实例互斥锁运行，重复启动时通过 AttachThreadInput 激活已有窗口
  - 系统托盘最小化（关闭窗口/最小化均隐藏到托盘）
  - 管理员权限自动提权（ShellExecute runas → 计划任务后备方案）
  - 窗口尺寸/位置记忆（关闭时保存到 `config/window-state.json`，支持多显示器校验）
  - 默认窗口尺寸 1500×1200
  - 启动时自动拉起后端 API 进程
- **Inno Setup 安装包**: 一键安装，自动检测并安装 .NET 8 Desktop Runtime 和 WebView2 Runtime
  - 覆盖安装时自动停止运行中的进程、停止 WinRing0x64 内核驱动释放文件锁
  - 开机自启选项（创建/删除计划任务，配置持久化到 `auto-start-opts.json`）
  - 卸载时可选保留用户配置
- **自定义背景图片**: 上传本地图片作为界面背景
  - 支持透明度滑块调节 (0-100%)
  - 黑色/白色遮罩切换
  - 图片预览和一键删除
  - 配置持久化到 `background-opts.json` + 前端 localStorage 快速读取
- **开机自启状态持久化**: 本地缓存 + 计划任务异步校验，进入页面立即显示正确状态
- **30+ 主题皮肤**: 从 4 套扩展到 30 余套（赛博霓虹、极客数据、机甲紫黑、橙光、蓝调、森林、浪潮、朋克、德古拉、漆黑、蔚蓝、咖啡、商务、暗金、万圣、北欧、暖阳、复古、幻彩、亮白、马卡龙、翡翠、幻紫、黄蜂、汽水、青黄、淡彩、冬日、物语、花园等）
- **主题感知半透明**: 背景启用时卡片和侧边栏使用 `color-mix(in srgb, var(--card) 60%, transparent)` 跟随主题色

### 变更

- GPU 模式预设统一：gaming/beast 模式 `ocCoreOffsetMhz` 从 200/100 改为 0，与手动 GPU 调节保持一致
- 散热曲线页面移除 `maxWidth: 900` 限制，宽度与其他页面一致
- Card 组件从内联 `style` 改为 `.dzcard` CSS 类，支持主题感知样式覆盖
- 侧边栏从内联样式改为 `.console-panel` CSS 类统一管理

### 修复

- 单实例窗口激活：使用 AttachThreadInput 绕过 Windows 前台窗口限制，点击快捷方式可正确激活已有窗口
- 覆盖安装时 WinRing0x64.sys 文件锁：安装前 `sc stop/delete` 停止内核驱动服务
- 背景开关状态竞态：从 event 广播改为 props + callback（状态提升到 App.jsx），消除 GET/POST 竞态
- 覆盖安装时开机自启状态丢失：安装程序强制写入配置 + 后端异步校验加延迟重试
- WebSocket 断连无限重连
- 散热曲线重复点定位
- GPU 模式 `parseInt(null)` 异常
- localStorage 异常未捕获
- API 同步阻塞启动

## [1.2.0] — 2026-06-08

### 新增

- **自定义散热曲线**: 独立标签页，SVG 可视化温度-转速曲线编辑器，支持保存/加载/启停/恢复预设，后台 FanCurveService 定时执行
- **GPU 模式持久化**: 用户选择的 GPU 模式写入 `gpu-mode.json`，服务重启后自动通过 WMI 恢复

### 变更

- 散热曲线面板操作按钮和状态栏移至顶部
- 首页模式选择卡片移至仪表盘最上方
- 统一所有恢复按钮命名为"恢复预设"
- 更新默认仪表盘卡片排列顺序

### 仓库维护

- `.gitignore` 新增忽略规则，从 Git 跟踪中移除 158 个非源码文件

## [1.1.0] — 2026-06-05

### 新增

- **C# HAL 后端**: .NET 8 Minimal API 替代 AppBridge，含 WMI/inpoutx64/RyzenAdj 全链路
- GPU 模式控制、GPU 锁频、FnLock 控制、风扇直写、SMU 集成
- Debug 页面 `/debug`

### 变更

- AppBridge 退役，全功能由 WmiInterface 替代
- SMU 控制从 Node.js 迁移至 C# SmuController

## [1.0.0] — 2026-06-03

### 新增

- 实时遥测监控面板、CPU/GPU 调节、风扇控制、键盘背光、模式预设
- 自定义仪表盘拖拽排序、状态持久化、4 套主题皮肤
