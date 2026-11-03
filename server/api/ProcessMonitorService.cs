// SPDX-License-Identifier: GPL-3.0-only
//
// ProcessMonitorService — 游戏进程监控 + 自动模式切换
// ====================================================
// 职责：
//   - 订阅 WMI 进程生命周期事件
//   - 匹配游戏规则，维护活跃游戏列表
//   - 通过 WebSocket 通知前端切换模式
//
// 双重检测：WMI 事件（实时）+ 心跳轮询（兜底）
//   WMI __InstanceCreationEvent / __InstanceDeletionEvent WITHIN 2 是主力检测路径。
//   实测存在 WMI 静默失效场景：连接正常但不再推送事件（系统睡眠/唤醒、WMI 服务内部状态损坏），
//   且 ManagementEventWatcher.Stopped 事件并不总会触发，自愈逻辑抓不住这种故障。
//   因此额外加一个 15s 心跳轮询，主动校验活跃进程是否还活着 + 全系统进程扫描兜底遗漏。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public sealed class ProcessMonitorService : IDisposable
{
    private readonly GameProfileService _profiles;
    private readonly OsdService _osd;

    // WMI 事件订阅
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    // watcher 断开自愈：0=空闲 1=重建中（Stop()/Dispose() 也会触发 Stopped，需防重入）
    private int _resubscribing;
    // 订阅字段的互斥锁：订阅建立放在后台线程（见构造函数），Dispose 需与之串行
    private readonly object _watcherLock = new();

    // 活跃游戏列表
    private readonly List<(int Pid, string ExeName, string TargetMode)> _activeGames = new();
    private readonly object _lock = new();

    // 活跃游戏数量（静态可访问，供 ParameterGuard 等模块查询）
    private static int _activeGameCount;
    public static int ActiveGameCount => _activeGameCount;

    // EAC/反作弊进程追踪
    private static readonly HashSet<string> AntiCheatProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "EasyAntiCheat_EOS.exe",    // EOS 版本（APEX, Fortnite 等）
        "EasyAntiCheat.exe",        // 旧版 EAC
        "EasyAntiCheat_Setup.exe",  // EAC 安装程序
        "BEService.exe",            // BattlEye Service
        "start_protected_game.exe"  // EOS SDK 的反作弊启动器
    };
    private static int _antiCheatRefCount;
    public static bool AntiCheatActive => _antiCheatRefCount > 0;

    // 快照模式（列表从空变非空时记录）
    private string? _snapshotMode;
    // 当前配置获取器：权威来源 = last-mode.json 的 CurrentMode()（启动时 Configure 注入，避免本地副本失同步）
    private Func<string> _currentConfig = () => "office";
    // 配置 id → 性能模式裸名（供 Rank 优先级比较）
    private Func<string, string> _resolveThermal = id => id;

    // WebSocket 客户端列表
    private static readonly List<WebSocket> _clients = new();
    private static readonly object _clientLock = new();

    // 退出延迟定时器
    private readonly Dictionary<int, CancellationTokenSource> _exitTimers = new();

    // 心跳轮询定时器（兜底机制，15s 检查一次活跃进程存活 + 60s 一次全系统扫描）
    private System.Threading.Timer? _heartbeatTimer;
    private int _heartbeatTick; // 自增 tick，用于区分"仅校验存活"和"全系统扫描"

    private bool _disposed;

    private static readonly JsonSerializerOptions _jsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ProcessMonitorService(GameProfileService profiles, OsdService osd,
        Func<string>? currentConfig = null,
        Func<string, string>? resolveThermal = null)
    {
        _profiles = profiles;
        _osd = osd;
        if (currentConfig != null) _currentConfig = currentConfig;
        if (resolveThermal != null) _resolveThermal = resolveThermal;

        AppLog.Write("ProcessMonitor", "Service started");

        // WMI 事件订阅建立极慢：__InstanceCreationEvent / __InstanceDeletionEvent 需要先做
        // 进程基线枚举，实测本机每个 watcher 的 Start() 约 10s、合计约 20s。
        // 此前在构造函数里同步建立，而本服务在 Program.cs 顶层（app.Run() 之前）就被解析，
        // 导致端口 3100 要等 ~20s 才监听 → Shell 就绪检测一直等待（启动黑屏 ~20s），
        // 且前端页面加载时后端仍在启动 → 误报"后端未连接"。
        // 改为后台线程建立：端口立即可用，游戏自动切换功能稍后就绪。
        _ = Task.Run(() =>
        {
            try
            {
                SubscribeWmiEvents();
                // 订阅建立后立即做一次进程基线枚举 + 启动心跳轮询
                BaselineScan();
                StartHeartbeat();
            }
            catch (Exception ex) { AppLog.Write("ProcessMonitor", $"async init failed: {ex.Message}"); }
        });
    }

    /// <summary>启动后注入配置解析依赖（指向 last-mode.json 与配置→性能模式解包点）</summary>
    public void Configure(Func<string>? currentConfig, Func<string, string>? resolveThermal)
    {
        if (currentConfig != null) _currentConfig = currentConfig;
        if (resolveThermal != null) _resolveThermal = resolveThermal;
    }

    // ── WebSocket 客户端管理 ─────────────────────────────────

    public static void AddClient(WebSocket ws)
    {
        lock (_clientLock) _clients.Add(ws);
    }

    public static void RemoveClient(WebSocket ws)
    {
        lock (_clientLock) _clients.Remove(ws);
    }

    // ── 状态查询 ─────────────────────────────────────────────

    public AutoSwitchStatus GetStatus()
    {
        lock (_lock)
        {
            var effectiveMode = GetEffectiveMode();
            return new AutoSwitchStatus
            {
                ServiceRunning = true,
                GlobalEnabled = _profiles.Enabled,
                ActiveGames = _activeGames.Select(g => new ActiveGameInfo
                {
                    Name = g.ExeName,
                    TargetMode = g.TargetMode,
                    Pid = g.Pid
                }).ToList(),
                EffectiveMode = effectiveMode ?? "none",
                SnapshotMode = _snapshotMode ?? "none"
            };
        }
    }

    // ── WMI 事件订阅 ─────────────────────────────────────────

    private void SubscribeWmiEvents()
    {
        try
        {
            // 使用 __InstanceCreationEvent 替代 Win32_ProcessStartTrace
            // Win32_ProcessStartTrace 需要管理员权限，__InstanceCreationEvent 不需要
            var startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"));
            startWatcher.EventArrived += OnProcessStarted;
            startWatcher.Stopped += OnWatcherStopped;
            startWatcher.Start();

            var stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"));
            stopWatcher.EventArrived += OnProcessStopped;
            stopWatcher.Stopped += OnWatcherStopped;
            stopWatcher.Start();

            lock (_watcherLock)
            {
                if (_disposed)
                {
                    // 建立订阅期间已被 Dispose：立即停掉，避免泄漏
                    try { startWatcher.Stop(); startWatcher.Dispose(); } catch { }
                    try { stopWatcher.Stop(); stopWatcher.Dispose(); } catch { }
                    return;
                }
                _startWatcher = startWatcher;
                _stopWatcher = stopWatcher;
            }

            AppLog.Write("ProcessMonitor", "WMI event subscriptions created");
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"WMI subscription failed: {ex.Message}");
        }
    }

    private void UnsubscribeWmiEvents()
    {
        ManagementEventWatcher? start;
        ManagementEventWatcher? stop;
        lock (_watcherLock)
        {
            start = _startWatcher;
            stop = _stopWatcher;
            _startWatcher = null;
            _stopWatcher = null;
        }
        // Stop() 可能同步触发 Stopped 回调 → 放在锁外，避免与自愈路径互锁
        try
        {
            start?.Stop();
            start?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"WMI unsubscribe failed: {ex.Message}");
        }
        try
        {
            stop?.Stop();
            stop?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"WMI unsubscribe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// watcher 断开自愈：WMI 服务重启/连接断开等导致订阅失效时自动重建，
    /// 避免"睡眠后游戏自动切换失灵"这类静默故障。
    /// Stopped 在主动 Stop()/Dispose() 时也会触发：_resubscribing 防重入，_disposed 拦截销毁路径。
    /// </summary>
    private void OnWatcherStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _resubscribing, 1, 0) != 0) return;
        try
        {
            AppLog.Write("ProcessMonitor", "watcher stopped unexpectedly, re-subscribing");
            UnsubscribeWmiEvents();
            SubscribeWmiEvents();
            // 重订阅后立即做一次基线枚举，防止重订阅期间漏掉的进程事件
            BaselineScan();
        }
        finally
        {
            Interlocked.Exchange(ref _resubscribing, 0);
        }
    }

    // ── 启动时基线枚举 ───────────────────────────────────────
    //
    // WMI 订阅建立之前已经在运行的进程不会产生 CreationEvent，
    // 导致服务重启后已在跑的游戏不会被识别。
    // 遍历当前所有 Win32_Process，匹配游戏规则加入活跃列表。

    private void BaselineScan()
    {
        if (!_profiles.Enabled) return;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name FROM Win32_Process");
            var processes = searcher.Get();
            int added = 0;

            lock (_lock)
            {
                foreach (var p in processes)
                {
                    try
                    {
                        var name = p["Name"]?.ToString() ?? "";
                        var pid = Convert.ToInt32(p["ProcessId"]);

                        // 跳过已经在活跃列表里的
                        if (_activeGames.Any(g => g.Pid == pid)) continue;

                        var profile = _profiles.MatchByExeName(name);
                        if (profile == null) continue;

                        // 列表从空变非空时记录快照
                        if (_activeGames.Count == 0)
                        {
                            _snapshotMode = _currentConfig();
                            AppLog.Write("ProcessMonitor", $"SNAPSHOT (baseline): {_snapshotMode}");
                        }

                        _activeGames.Add((pid, name, profile.TargetMode));
                        added++;
                    }
                    catch { /* 单个进程读取失败跳过 */ }
                }
                _activeGameCount = _activeGames.Count;

                if (added > 0)
                {
                    AppLog.Write("ProcessMonitor", $"BASELINE_SCAN: +{added} existing game processes, total active={_activeGameCount}");

                    // 基线枚举后检查是否需要切换模式
                    var newMode = GetEffectiveMode();
                    var curCfg = _currentConfig();
                    if (newMode != null && newMode != curCfg)
                    {
                        AppLog.Write("ProcessMonitor", $"SWITCH (baseline): {newMode} (from {curCfg})");
                        NotifyModeSwitch(newMode);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"BaselineScan error: {ex.Message}");
        }
    }

    // ── 心跳轮询（兜底机制） ─────────────────────────────────
    //
    // WMI 事件流可能静默失效（连接状态仍为 Open 但不再推送事件），
    // 且 ManagementEventWatcher.Stopped 并不总会触发，自愈逻辑抓不住。
    // 本机制每 15s 主动检查活跃列表里的进程是否还活着，
    // 每 60s 额外扫一遍全系统进程看有没有遗漏的 START 事件。

    private void StartHeartbeat()
    {
        _heartbeatTimer = new System.Threading.Timer(_ => HeartbeatTick(), null, 15_000, 15_000);
        AppLog.Write("ProcessMonitor", "Heartbeat started (15s)");
    }

    private void HeartbeatTick()
    {
        if (_disposed) return;
        try
        {
            int tick = Interlocked.Increment(ref _heartbeatTick);
            bool fullScan = (tick % 4 == 0); // 每 4 * 15s = 60s 做一次全系统扫描

            // 第 1 步：校验活跃进程是否还活着
            List<int> deadPids = new();
            lock (_lock)
            {
                foreach (var g in _activeGames)
                {
                    if (!IsProcessAlive(g.Pid, g.ExeName))
                        deadPids.Add(g.Pid);
                }
            }

            // 清理死进程（直接退出，不走 3s 延迟——兜底场景，进程确实已死一段时间）
            foreach (var pid in deadPids)
            {
                ForceExitProcess(pid, reason: "heartbeat-dead");
            }

            // 第 2 步：全系统扫描，兜底遗漏的 START 事件
            if (fullScan && _profiles.Enabled)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name FROM Win32_Process");
                    var processes = searcher.Get();

                    lock (_lock)
                    {
                        foreach (var p in processes)
                        {
                            try
                            {
                                var name = p["Name"]?.ToString() ?? "";
                                var pid = Convert.ToInt32(p["ProcessId"]);

                                // 已在活跃列表里的跳过
                                if (_activeGames.Any(g => g.Pid == pid)) continue;

                                var profile = _profiles.MatchByExeName(name);
                                if (profile == null) continue;

                                // 列表从空变非空时记录快照
                                if (_activeGames.Count == 0)
                                {
                                    _snapshotMode = _currentConfig();
                                    AppLog.Write("ProcessMonitor", $"SNAPSHOT (heartbeat): {_snapshotMode}");
                                }

                                _activeGames.Add((pid, name, profile.TargetMode));
                                AppLog.Write("ProcessMonitor", $"JOIN (heartbeat): {name} (PID {pid}) target={profile.TargetMode}");
                            }
                            catch { }
                        }
                        _activeGameCount = _activeGames.Count;

                        // 检查是否需要切换
                        var newMode = GetEffectiveMode();
                        var curCfg = _currentConfig();
                        if (newMode != null && newMode != curCfg)
                        {
                            AppLog.Write("ProcessMonitor", $"SWITCH (heartbeat): {newMode} (from {curCfg})");
                            NotifyModeSwitch(newMode);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("ProcessMonitor", $"Heartbeat full-scan error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"HeartbeatTick error: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查进程是否还活着。PID 复用防御：Process.GetProcessById 返回的实例
    /// 名字和记录的 ExeName 不一致时视为"进程已死（PID 被复用）"。
    /// </summary>
    private static bool IsProcessAlive(int pid, string expectedName)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            // PID 复用防御：比对名字。Process.ProcessName 不含 .exe 后缀，
            // 但 expectedName 通常含 .exe，做一次宽松比较
            var procName = proc.ProcessName;
            if (string.Equals(procName, expectedName, StringComparison.OrdinalIgnoreCase))
                return true;
            // 去掉 .exe 后缀再比
            var expectedBase = expectedName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? expectedName[..^4]
                : expectedName;
            return string.Equals(procName, expectedBase, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }   // 进程不存在
        catch (System.ComponentModel.Win32Exception) { return false; } // 无法访问（已退出但 PID 未回收）
    }

    /// <summary>
    /// 强制退出清理（供心跳兜底使用），立即从活跃列表移除并尝试恢复模式，
    /// 不走 WMI 事件路径的 3s 延迟定时器。
    /// </summary>
    private void ForceExitProcess(int processId, string reason)
    {
        lock (_lock)
        {
            var game = _activeGames.FirstOrDefault(g => g.Pid == processId);
            if (game.Pid == 0) return; // 已清理

            // 取消可能在跑的延迟退出定时器
            if (_exitTimers.TryGetValue(processId, out var cts))
            {
                try { cts.Cancel(); } catch { }
                _exitTimers.Remove(processId);
            }

            _activeGames.RemoveAll(g => g.Pid == processId);
            _activeGameCount = _activeGames.Count;
            AppLog.Write("ProcessMonitor", $"EXIT ({reason}): {game.ExeName} (PID {processId}), activeGames={_activeGameCount}");

            if (_activeGames.Count == 0)
            {
                var curCfg = _currentConfig();
                if (_snapshotMode != null && _snapshotMode != curCfg)
                {
                    AppLog.Write("ProcessMonitor", $"RESTORE ({reason}): → {_snapshotMode}");
                    NotifyModeSwitch(_snapshotMode);
                }
            }
            else
            {
                var newMode = GetEffectiveMode();
                var curCfg = _currentConfig();
                if (newMode != null && RankOf(newMode) < RankOf(curCfg))
                {
                    AppLog.Write("ProcessMonitor", $"DOWNGRADE ({reason}): → {newMode}");
                    NotifyModeSwitch(newMode);
                }
            }
        }
    }

    // ── 进程事件处理 ─────────────────────────────────────────

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var processName = targetInstance["Name"]?.ToString() ?? "";
            var processId = Convert.ToInt32(targetInstance["ProcessId"]);

            // 反作弊进程检测（独立于游戏规则，始终监控）
            if (AntiCheatProcesses.Contains(processName) || processName.Contains("AntiCheat", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _antiCheatRefCount);
                AppLog.Write("EAC-MONITOR", $"[!] 反作弊进程启动: {processName} (PID {processId}), refCount={_antiCheatRefCount}");
            }

            if (!_profiles.Enabled) return;

            var profile = _profiles.MatchByExeName(processName);
            if (profile == null) return;

            lock (_lock)
            {
                // 取消退出定时器（如果有同名进程正在退出）
                var existingExitTimer = _exitTimers.FirstOrDefault(kv =>
                    _activeGames.Any(g => g.Pid == kv.Key && g.ExeName.Equals(processName, StringComparison.OrdinalIgnoreCase)));
                if (existingExitTimer.Value != null)
                {
                    existingExitTimer.Value.Cancel();
                    _exitTimers.Remove(existingExitTimer.Key);
                    AppLog.Write("ProcessMonitor", $"CANCEL_EXIT: {processName} restarted");
                }

                // 列表从空变非空时记录快照（权威当前配置）
                if (_activeGames.Count == 0)
                {
                    _snapshotMode = _currentConfig();
                    AppLog.Write("ProcessMonitor", $"SNAPSHOT: {_snapshotMode}");
                }

                // 添加到活跃列表
                _activeGames.Add((processId, processName, profile.TargetMode));
                _activeGameCount = _activeGames.Count;
                AppLog.Write("ProcessMonitor", $"JOIN: {processName} (PID {processId}) target={profile.TargetMode}, activeGames={_activeGameCount}");

                // 计算是否需要切换
                var newMode = GetEffectiveMode();
                var curCfg = _currentConfig();
                if (newMode != null && newMode != curCfg)
                {
                    AppLog.Write("ProcessMonitor", $"SWITCH: {processName} (PID {processId}) → {newMode} (from {curCfg})");
                    NotifyModeSwitch(newMode);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"OnProcessStarted error: {ex.Message}");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var processName = targetInstance["Name"]?.ToString() ?? "";
            var processId = Convert.ToInt32(targetInstance["ProcessId"]);

            // 反作弊进程退出检测（独立于游戏规则，始终监控）
            if (AntiCheatProcesses.Contains(processName) || processName.Contains("AntiCheat", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Decrement(ref _antiCheatRefCount);
                if (_antiCheatRefCount < 0) _antiCheatRefCount = 0;
                AppLog.Write("EAC-MONITOR", $"[√] 反作弊进程退出: {processName} (PID {processId}), refCount={_antiCheatRefCount}");
            }

            if (!_profiles.Enabled) return;
            lock (_lock)
            {
                var game = _activeGames.FirstOrDefault(g => g.Pid == processId);
                if (game.Pid == 0) return; // 不在列表中（可能已被心跳清理）

                // 启动延迟退出
                var cts = new CancellationTokenSource();
                _exitTimers[processId] = cts;

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000, cts.Token);
                        ProcessExitDelayed(processId, processName, game.TargetMode);
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        lock (_lock) _exitTimers.Remove(processId);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"OnProcessStopped error: {ex.Message}");
        }
    }

    private void ProcessExitDelayed(int processId, string processName, string targetMode)
    {
        lock (_lock)
        {
            // 检查是否还在列表中（可能被重启取消）
            if (!_activeGames.Any(g => g.Pid == processId)) return;

            _activeGames.RemoveAll(g => g.Pid == processId);
            _activeGameCount = _activeGames.Count;
            AppLog.Write("ProcessMonitor", $"EXIT: {processName} (PID {processId}), activeGames={_activeGameCount}");

            if (_activeGames.Count == 0)
            {
                // 列表为空，恢复快照（切回启动前的配置 id）
                var curCfg = _currentConfig();
                if (_snapshotMode != null && _snapshotMode != curCfg)
                {
                    AppLog.Write("ProcessMonitor", $"RESTORE: {processName} exited → {_snapshotMode} (snapshot)");
                    NotifyModeSwitch(_snapshotMode);
                }
            }
            else
            {
                // 还有游戏在运行，检查是否需要降级
                var newMode = GetEffectiveMode();
                var curCfg = _currentConfig();
                if (newMode != null && RankOf(newMode) < RankOf(curCfg))
                {
                    AppLog.Write("ProcessMonitor", $"DOWNGRADE: {processName} exited → {newMode}");
                    NotifyModeSwitch(newMode);
                }
            }
        }
    }

    // ── 模式优先级 ───────────────────────────────────────────

    private static int Rank(string thermalModeName) => thermalModeName switch
    {
        "silent" => 0,
        "office" => 1,
        "beast" => 2,
        "gaming" => 3,
        _ => 1
    };

    // 配置 id → 优先级（先解包成性能模式裸名再排名，适配 cfg- 前缀配置 id）
    private int RankOf(string cfgId) => Rank(_resolveThermal(cfgId));

    private string? GetEffectiveMode()
    {
        if (_activeGames.Count == 0) return null;
        return _activeGames
            .OrderByDescending(g => RankOf(g.TargetMode))
            .First()
            .TargetMode;
    }

    // ── WebSocket 通知 ───────────────────────────────────────

    private void NotifyModeSwitch(string mode)
    {
        var msg = JsonSerializer.Serialize(new { type = "auto_switch", mode }, _jsonOpt);
        var bytes = Encoding.UTF8.GetBytes(msg);
        var segment = new ArraySegment<byte>(bytes);

        WebSocket[] snapshot;
        lock (_clientLock) snapshot = _clients.ToArray();

        List<WebSocket> deadClients = new();
        foreach (var ws in snapshot)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    // 必须带超时：僵死客户端会让 SendAsync 永久挂起，
                    // 这里 .Wait() 无上限会把自动切换逻辑整条卡死
                    if (!ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None).Wait(2000))
                    {
                        deadClients.Add(ws);
                        try { ws.Abort(); } catch { }
                    }
                }
                else
                    deadClients.Add(ws);
            }
            catch
            {
                deadClients.Add(ws);
                try { ws.Abort(); } catch { }
            }
        }

        if (deadClients.Count > 0)
        {
            lock (_clientLock)
            {
                foreach (var ws in deadClients)
                    _clients.Remove(ws);
            }
        }

        // 同时显示 OSD
        _osd.Show(mode);
    }

    // ── Dispose ──────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 停心跳定时器（先停再释放，防止回调与 Dispose 并发）
        try { _heartbeatTimer?.Dispose(); } catch { }
        _heartbeatTimer = null;

        UnsubscribeWmiEvents();

        lock (_lock)
        {
            foreach (var cts in _exitTimers.Values)
                cts.Cancel();
            _exitTimers.Clear();
        }

        AppLog.Write("ProcessMonitor", "Service disposed");
    }
}

// ── 状态模型 ─────────────────────────────────────────────────

public class AutoSwitchStatus
{
    public bool ServiceRunning { get; set; }
    public bool GlobalEnabled { get; set; }
    public List<ActiveGameInfo> ActiveGames { get; set; } = new();
    public string EffectiveMode { get; set; } = "none";
    public string SnapshotMode { get; set; } = "none";
}

public class ActiveGameInfo
{
    public string Name { get; set; } = "";
    public string TargetMode { get; set; } = "";
    public int Pid { get; set; }
}
