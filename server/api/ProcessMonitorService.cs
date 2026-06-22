// SPDX-License-Identifier: GPL-3.0-only
//
// ProcessMonitorService — 游戏进程监控 + 自动模式切换
// ====================================================
// 职责：
//   - 订阅 WMI 进程生命周期事件
//   - 匹配游戏规则，维护活跃游戏列表
//   - 通过 WebSocket 通知前端切换模式

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Douzhanzhe.HAL;
using Microsoft.Win32;

namespace Douzhanzhe.API;

public sealed class ProcessMonitorService : IDisposable
{
    private readonly GameProfileService _profiles;
    private readonly OsdService _osd;

    // WMI 事件订阅
    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;

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
    // 当前模式（每次 thermal_mode API 调用时更新）
    private string? _currentMode;

    // WebSocket 客户端列表
    private static readonly List<WebSocket> _clients = new();
    private static readonly object _clientLock = new();

    // 退出延迟定时器
    private readonly Dictionary<int, CancellationTokenSource> _exitTimers = new();

    private bool _disposed;

    private static readonly JsonSerializerOptions _jsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ProcessMonitorService(GameProfileService profiles, OsdService osd)
    {
        _profiles = profiles;
        _osd = osd;

        SubscribeWmiEvents();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        AppLog.Write("ProcessMonitor", "Service started");
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

    // ── 当前模式跟踪 ─────────────────────────────────────────

    public void UpdateCurrentMode(string mode)
    {
        _currentMode = mode;
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
            _startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"));
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();

            _stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'"));
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();

            AppLog.Write("ProcessMonitor", "WMI event subscriptions created");
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"WMI subscription failed: {ex.Message}");
        }
    }

    private void UnsubscribeWmiEvents()
    {
        try
        {
            _startWatcher?.Stop();
            _startWatcher?.Dispose();
            _startWatcher = null;

            _stopWatcher?.Stop();
            _stopWatcher?.Dispose();
            _stopWatcher = null;
        }
        catch (Exception ex)
        {
            AppLog.Write("ProcessMonitor", $"WMI unsubscribe failed: {ex.Message}");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            AppLog.Write("ProcessMonitor", "System resumed, re-subscribing WMI events");
            UnsubscribeWmiEvents();
            SubscribeWmiEvents();
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

                // 列表从空变非空时记录快照
                if (_activeGames.Count == 0)
                {
                    _snapshotMode = _currentMode;
                    AppLog.Write("ProcessMonitor", $"SNAPSHOT: {_snapshotMode}");
                }

                // 添加到活跃列表
                _activeGames.Add((processId, processName, profile.TargetMode));
                _activeGameCount = _activeGames.Count;
                AppLog.Write("ProcessMonitor", $"JOIN: {processName} (PID {processId}) target={profile.TargetMode}, activeGames={_activeGameCount}");

                // 计算是否需要切换
                var newMode = GetEffectiveMode();
                if (newMode != null && newMode != _currentMode)
                {
                    AppLog.Write("ProcessMonitor", $"SWITCH: {processName} (PID {processId}) → {newMode} (from {_currentMode})");
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
                if (game.Pid == 0) return; // 不在列表中

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
                // 列表为空，恢复快照模式
                if (_snapshotMode != null && _snapshotMode != _currentMode)
                {
                    AppLog.Write("ProcessMonitor", $"RESTORE: {processName} exited → {_snapshotMode} (snapshot)");
                    NotifyModeSwitch(_snapshotMode);
                }
            }
            else
            {
                // 还有游戏在运行，检查是否需要降级
                var newMode = GetEffectiveMode();
                if (newMode != null && Rank(newMode) < Rank(_currentMode ?? "office"))
                {
                    AppLog.Write("ProcessMonitor", $"DOWNGRADE: {processName} exited → {newMode}");
                    NotifyModeSwitch(newMode);
                }
            }
        }
    }

    // ── 模式优先级 ───────────────────────────────────────────

    private static int Rank(string mode) => mode switch
    {
        "silent" => 0,
        "office" => 1,
        "beast" => 2,
        "gaming" => 3,
        _ => 1
    };

    private string? GetEffectiveMode()
    {
        if (_activeGames.Count == 0) return null;
        return _activeGames
            .OrderByDescending(g => Rank(g.TargetMode))
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
                    ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                else
                    deadClients.Add(ws);
            }
            catch
            {
                deadClients.Add(ws);
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

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
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
