// SPDX-License-Identifier: GPL-3.0-only
//
// ProcessMonitorService — 游戏进程监控 + 自动模式切换
// ====================================================
// 职责：
//   - 心跳轮询系统进程列表，匹配游戏规则
//   - 维护活跃游戏列表，驱动性能模式自动切换
//   - 通过 WebSocket 通知前端
//
// 设计决策：纯 1s 心跳，放弃 WMI 事件订阅
//   WMI __InstanceCreationEvent / __InstanceDeletionEvent WITHIN 2 存在"静默失效"——
//   连接状态为 Open 但不再推送事件（系统睡眠/唤醒、WMI 服务内部损坏），
//   且 ManagementEventWatcher.Stopped 并不总会触发，自愈逻辑抓不住。
//   因此整个 v2.0 简化为单一数据源：每秒遍历一次 Toolhelp 进程树，
//   和上一次活跃列表做差算出差值（新增 JOIN / 消失 EXIT）。
//   匹配到启动器后持续跟踪全部后代；启动器退出时，只要游戏子进程仍存活，
//   本次游戏会话就不会结束。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    // 活跃游戏列表（单一数据源，心跳 tick 维护）
    private readonly List<ActiveGameTracking> _activeGames = new();
    private readonly object _lock = new();

    // 活跃游戏数量（静态可访问，供 ParameterGuard 等模块查询）
    private static int _activeGameCount;
    public static int ActiveGameCount => _activeGameCount;

    // 快照模式（列表从空变非空时记录）
    private string? _snapshotMode;
    // 当前配置获取器：权威来源 = last-mode.json 的 CurrentMode()（启动时 Configure 注入，避免本地副本失同步）
    private Func<string> _currentConfig = () => "office";
    // 配置 id → 性能模式裸名（供 Rank 优先级比较）
    private Func<string, string> _resolveThermal = id => id;
    // 后端直接应用模式的唯一入口（不依赖前端 WS 是否在线）
    private Func<string, Task>? _applyMode;

    // WebSocket 客户端列表
    private static readonly List<WebSocket> _clients = new();
    private static readonly object _clientLock = new();

    // 心跳定时器
    private System.Threading.Timer? _heartbeatTimer;
    private int _tickRunning;

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

        // 立即启动心跳：端口立即可用，第一次 tick 在 1s 后执行
        _heartbeatTimer = new System.Threading.Timer(
            _ => _ = HeartbeatTickAsync(), null, 1_000, 1_000);
    }

    /// <summary>启动后注入配置解析依赖（指向 last-mode.json 与配置→性能模式解包点）</summary>
    public void Configure(Func<string>? currentConfig, Func<string, string>? resolveThermal,
        Func<string, Task>? applyMode = null)
    {
        if (currentConfig != null) _currentConfig = currentConfig;
        if (resolveThermal != null) _resolveThermal = resolveThermal;
        if (applyMode != null) _applyMode = applyMode;
    }

    /// <summary>
    /// 应用内启动游戏时立即登记根进程，避免短命启动器在下一次心跳前退出而漏检。
    /// </summary>
    public void RegisterRoot(GameProfile profile, int processId, string? processName = null)
    {
        if (processId <= 0 || _disposed) return;

        var exeName = NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(exeName))
            exeName = NormalizeProcessName(profile.ExeName);
        if (string.IsNullOrEmpty(exeName))
        {
            try { exeName = NormalizeProcessName(Process.GetProcessById(processId).ProcessName); }
            catch { return; }
        }

        lock (_lock)
        {
            if (_activeGames.Any(g => g.Processes.ContainsKey(processId))) return;

            if (_activeGames.Count == 0)
            {
                _snapshotMode = CanonicalMode(_currentConfig());
                AppLog.Write("ProcessMonitor", $"SNAPSHOT: {_snapshotMode}");
            }

            var game = new ActiveGameTracking(
                processId, exeName, CanonicalMode(profile.TargetMode));
            game.Processes[processId] = exeName;
            _activeGames.Add(game);
            _activeGameCount = _activeGames.Count;
            AppLog.Write(
                "ProcessMonitor",
                $"JOIN (launch): {exeName} (PID {processId}) target={profile.TargetMode}");
        }

        _ = HeartbeatTickAsync();
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
                    Name = g.RootExeName,
                    TargetMode = g.TargetMode,
                    Pid = g.RootPid
                }).ToList(),
                EffectiveMode = effectiveMode ?? "none",
                SnapshotMode = _snapshotMode ?? "none"
            };
        }
    }

    // ── 心跳 tick ────────────────────────────────────────────
    //
    // 每秒一次。遍历当前系统进程树，和活跃列表做差：
    //   新出现的游戏进程 → JOIN（匹配规则 → 切模式）
    //   活跃列表里消失的 → EXIT（清理 → 恢复/降级模式）

    private async Task HeartbeatTickAsync()
    {
        if (_disposed || Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return;
        try
        {
            // 1. 一次抓取 PID、父 PID、进程名
            var snapshot = ProcessTreeSnapshot.Capture();
            if (snapshot.Count == 0) return;
            var childrenByParent = snapshot.Values.ToLookup(p => p.ParentPid);

            string? targetMode = null;
            string? action = null;
            lock (_lock)
            {
                // 2. 先扩展每个游戏会话的后代，再判断会话是否仍存活。
                foreach (var game in _activeGames)
                {
                    TrackDescendants(game, childrenByParent);
                }

                var exited = _activeGames
                    .Where(g => !IsGameAlive(g, snapshot))
                    .ToList();
                foreach (var game in exited)
                {
                    _activeGames.Remove(game);
                    AppLog.Write(
                        "ProcessMonitor",
                        $"EXIT: {game.RootExeName} (PID {game.RootPid}, tracked={game.Processes.Count})");
                }

                // 3. 再加新启动的游戏进程（进程表里存在但尚未归属于任何游戏会话）
                if (_profiles.Enabled)
                {
                    var claimedPids = _activeGames
                        .SelectMany(g => g.Processes.Keys)
                        .ToHashSet();

                    foreach (var proc in snapshot.Values)
                    {
                        if (claimedPids.Contains(proc.Pid)) continue;

                        var profile = _profiles.MatchByExeName(proc.Name);
                        if (profile == null) continue;

                        if (_activeGames.Count == 0)
                        {
                            _snapshotMode = CanonicalMode(_currentConfig());
                            AppLog.Write("ProcessMonitor", $"SNAPSHOT: {_snapshotMode}");
                        }

                        var normalizedTarget = CanonicalMode(profile.TargetMode);
                        var game = new ActiveGameTracking(
                            proc.Pid, proc.Name, normalizedTarget);
                        game.Processes[proc.Pid] = proc.Name;
                        TrackDescendants(game, childrenByParent);
                        _activeGames.Add(game);
                        AppLog.Write(
                            "ProcessMonitor",
                            $"JOIN: {proc.Name} (PID {proc.Pid}) target={profile.TargetMode}");
                    }
                }

                _activeGameCount = _activeGames.Count;

                // 4. 每秒核对一次权威配置；切换失败后下一 tick 会自动重试。
                var currentMode = CanonicalMode(_currentConfig());
                if (_activeGames.Count == 0)
                {
                    if (_snapshotMode != null && !ModeEquals(_snapshotMode, currentMode))
                    {
                        targetMode = _snapshotMode;
                        action = "RESTORE";
                    }
                }
                else
                {
                    var effectiveMode = GetEffectiveMode();
                    if (effectiveMode != null && !ModeEquals(effectiveMode, currentMode))
                    {
                        targetMode = effectiveMode;
                        action = "SWITCH";
                    }
                }
            }

            if (targetMode != null)
            {
                var applied = await ApplyModeAsync(targetMode, action ?? "SWITCH");
                if (applied && action == "RESTORE")
                {
                    lock (_lock) _snapshotMode = null;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"HeartbeatTick error: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _tickRunning, 0);
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

    private static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";
    }

    private static void TrackDescendants(
        ActiveGameTracking game,
        ILookup<int, ProcessSnapshotEntry> childrenByParent)
    {
        var pending = new Queue<int>(game.Processes.Keys);
        var visited = new HashSet<int>();

        while (pending.Count > 0)
        {
            var parentPid = pending.Dequeue();
            if (!visited.Add(parentPid)) continue;

            foreach (var child in childrenByParent[parentPid])
            {
                if (!game.Processes.TryAdd(child.Pid, child.Name)) continue;
                pending.Enqueue(child.Pid);
                AppLog.Write(
                    "ProcessMonitor",
                    $"DESCENDANT: {child.Name} (PID {child.Pid}) parent={parentPid} game={game.RootExeName}");
            }
        }
    }

    private static bool IsGameAlive(
        ActiveGameTracking game,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> snapshot)
    {
        foreach (var (pid, trackedName) in game.Processes)
        {
            if (snapshot.TryGetValue(pid, out var current)
                && string.Equals(current.Name, trackedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string CanonicalMode(string mode) => mode switch
    {
        "silent" or "office" or "beast" or "gaming" => $"cfg-{mode}",
        _ => mode
    };

    private static bool ModeEquals(string left, string right)
        => string.Equals(CanonicalMode(left), CanonicalMode(right), StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ApplyModeAsync(string mode, string action)
    {
        var from = CanonicalMode(_currentConfig());
        AppLog.Write("ProcessMonitor", $"{action}: → {mode} (from {from})");

        try
        {
            var appliedByBackend = _applyMode != null;
            if (appliedByBackend)
            {
                await _applyMode!(mode);
            }
            else
            {
                // 兼容测试/独立构造：没有后端应用回调时仍通知前端处理。
                _osd.Show(mode);
            }
            await NotifyModeSwitchAsync(mode, appliedByBackend);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"{action} failed → {mode}: {ex.Message}");
            return false;
        }
    }

    // ── WebSocket 通知 ───────────────────────────────────────

    private async Task NotifyModeSwitchAsync(string mode, bool appliedByBackend)
    {
        var msg = JsonSerializer.Serialize(
            new { type = "auto_switch", mode, applied = appliedByBackend }, _jsonOpt);
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
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, cts.Token);
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
    }

    // ── Dispose ──────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _heartbeatTimer?.Dispose(); } catch { }
        _heartbeatTimer = null;

        AppLog.Write("ProcessMonitor", "Service disposed");
    }

    private sealed class ActiveGameTracking
    {
        public ActiveGameTracking(int rootPid, string rootExeName, string targetMode)
        {
            RootPid = rootPid;
            RootExeName = rootExeName;
            TargetMode = targetMode;
        }

        public int RootPid { get; }
        public string RootExeName { get; }
        public string TargetMode { get; }
        public Dictionary<int, string> Processes { get; } = new();
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
