// SPDX-License-Identifier: GPL-3.0-only
//
// ProcessMonitorService — 游戏进程监控 + 自动模式切换
// ====================================================
// 职责：
//   - 心跳轮询系统进程列表，匹配游戏规则
//   - 维护活跃游戏列表，驱动性能模式自动切换
//   - 通过 WebSocket 通知前端
//
// 设计决策：纯 3s 心跳，放弃 WMI 事件订阅
//   WMI __InstanceCreationEvent / __InstanceDeletionEvent WITHIN 2 存在"静默失效"——
//   连接状态为 Open 但不再推送事件（系统睡眠/唤醒、WMI 服务内部损坏），
//   且 ManagementEventWatcher.Stopped 并不总会触发，自愈逻辑抓不住。
//   因此整个 v2.0 简化为单一数据源：每 3s 遍历一次系统进程列表，
//   和上一次活跃列表做差算出差值（新增 JOIN / 消失 EXIT）。
//   3s 延迟对"启动游戏切斗战模式"完全在用户感知阈值以下。

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
    private readonly List<(int Pid, string ExeName, string TargetMode)> _activeGames = new();
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

    // WebSocket 客户端列表
    private static readonly List<WebSocket> _clients = new();
    private static readonly object _clientLock = new();

    // 心跳定时器
    private System.Threading.Timer? _heartbeatTimer;

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

        // 立即启动心跳：端口立即可用，第一次 tick 在 3s 后执行
        _heartbeatTimer = new System.Threading.Timer(_ => HeartbeatTick(), null, 3_000, 3_000);
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

    // ── 心跳 tick ────────────────────────────────────────────
    //
    // 每 3s 一次。遍历当前系统进程列表，和活跃列表做差：
    //   新出现的游戏进程 → JOIN（匹配规则 → 切模式）
    //   活跃列表里消失的 → EXIT（清理 → 恢复/降级模式）
    //
    // 用 Process.GetProcesses() 而非 WMI ManagementObjectSearcher：
    //   前者直接读系统进程表，后者需要 RPC 到 WMI 服务，前者轻量得多。

    private void HeartbeatTick()
    {
        if (_disposed) return;
        try
        {
            // 1. 拿到当前系统进程表
            var snapshot = Process.GetProcesses();

            // 2. 转成 pid→name 字典（Process.ProcessName 不带 .exe，补回去匹配规则的 ExeName）
            var currentPids = new HashSet<int>();
            var currentNames = new Dictionary<int, string>();
            foreach (var proc in snapshot)
            {
                try
                {
                    currentPids.Add(proc.Id);
                    // 补 .exe 后缀（ProcessName 没有，但匹配规则里 ExeName 通常有）
                    currentNames[proc.Id] = proc.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? proc.ProcessName
                        : proc.ProcessName + ".exe";
                }
                finally { proc.Dispose(); }
            }

            lock (_lock)
            {
                bool modeChanged = false;

                // 3. 先清理退出的进程（活跃列表里存在但当前进程表里消失的）
                var exited = _activeGames.Where(g => !currentPids.Contains(g.Pid)).ToList();
                foreach (var g in exited)
                {
                    _activeGames.Remove(g);
                    AppLog.Write("ProcessMonitor", $"EXIT: {g.ExeName} (PID {g.Pid})");
                    modeChanged = true;
                }

                // 4. 再加新启动的游戏进程（进程表里存在但活跃列表里没有，且匹配规则）
                if (_profiles.Enabled)
                {
                    foreach (var kv in currentNames)
                    {
                        int pid = kv.Key;
                        string name = kv.Value;

                        // 已在活跃列表里的跳过
                        if (_activeGames.Any(g => g.Pid == pid)) continue;

                        var profile = _profiles.MatchByExeName(name);
                        if (profile == null) continue;

                        // 列表从空变非空时记录快照
                        if (_activeGames.Count == 0)
                        {
                            _snapshotMode = _currentConfig();
                            AppLog.Write("ProcessMonitor", $"SNAPSHOT: {_snapshotMode}");
                        }

                        _activeGames.Add((pid, name, profile.TargetMode));
                        AppLog.Write("ProcessMonitor", $"JOIN: {name} (PID {pid}) target={profile.TargetMode}");
                        modeChanged = true;
                    }
                }

                _activeGameCount = _activeGames.Count;

                // 5. 有变化时计算是否需要切换模式
                if (modeChanged)
                {
                    var newMode = GetEffectiveMode();
                    var curCfg = _currentConfig();

                    if (_activeGames.Count == 0)
                    {
                        // 全部退出 → 恢复快照
                        if (_snapshotMode != null && _snapshotMode != curCfg)
                        {
                            AppLog.Write("ProcessMonitor", $"RESTORE: → {_snapshotMode} (snapshot)");
                            NotifyModeSwitch(_snapshotMode);
                        }
                    }
                    else if (newMode != null && newMode != curCfg)
                    {
                        AppLog.Write("ProcessMonitor", $"SWITCH: → {newMode} (from {curCfg})");
                        NotifyModeSwitch(newMode);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"HeartbeatTick error: {ex.Message}");
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

        try { _heartbeatTimer?.Dispose(); } catch { }
        _heartbeatTimer = null;

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
