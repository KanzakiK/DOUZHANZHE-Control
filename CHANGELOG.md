# Changelog

该项目所有重要变更均会记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本语义遵循 [Semantic Versioning](https://semver.org/spec/v2.0.0.html)。

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
