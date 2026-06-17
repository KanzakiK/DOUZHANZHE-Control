// SPDX-License-Identifier: MIT
//
// TelemetryBackgroundService — 后台遥测心跳 + HealthWatchdog
// ============================================================
// 职责：
//   每 250ms 轮询 HAL 读取温度/风扇/状态，
//   通过 WebSocket 推送给前端。
//   HealthWatchdog: 零值连续计数器 + 分级恢复 + Last Known Good 缓存。

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public class TelemetryBackgroundService : BackgroundService
{
    private readonly HardwareAbstractionLayer _hal;
    private readonly WmiInterface _wmi;
    private readonly ILogger<TelemetryBackgroundService> _log;

    // WebSocket 客户端列表
    private static readonly List<WebSocket> _clients = new();
    private static readonly object _clientLock = new();

    private static readonly JsonSerializerOptions _jsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── HealthWatchdog: 零值连续计数器 ──
    private int _cpuTempZeroCount;       // CPU 温度连续为 0 的次数
    private int _fanZeroCount;           // 风扇 RPM 连续为 0 的次数（大扇+小扇都为零时递增）
    private volatile int _recovering;    // 恢复中标志，防止重复触发

    // ── HealthWatchdog: Last Known Good 缓存 ──
    private byte _lkgCpuTemp;            // CPU 温度上次有效值
    private ushort _lkgCpuFan;           // CPU 风扇 RPM 上次有效值
    private ushort _lkgGpuFan;           // GPU 风扇 RPM 上次有效值

    public TelemetryBackgroundService(
        HardwareAbstractionLayer hal,
        WmiInterface wmi,
        ILogger<TelemetryBackgroundService> log)
    {
        _hal = hal;
        _wmi = wmi;
        _log = log;
    }

    /// <summary>添加 WebSocket 客户端到推送列表</summary>
    public static void AddClient(WebSocket ws)
    {
        lock (_clientLock) _clients.Add(ws);
    }

    /// <summary>移除 WebSocket 客户端</summary>
    public static void RemoveClient(WebSocket ws)
    {
        lock (_clientLock) _clients.Remove(ws);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[Telemetry] 后台遥测服务已启动 (HealthWatchdog enabled)");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, ct);

                // ── 读取遥测 ──
                var cpuTemp = _hal.CpuTemperature;
                var gpuTemp = _hal.GpuTemperature;
                var cpuFan = _hal.CpuFanRpm;
                var gpuFan = _hal.GpuFanRpm;

                // ── HealthWatchdog: Last Known Good 更新 ──
                if (cpuTemp > 0) _lkgCpuTemp = cpuTemp;
                if (cpuFan > 0) _lkgCpuFan = cpuFan;
                if (gpuFan > 0) _lkgGpuFan = gpuFan;

                // ── HealthWatchdog: 零值计数器 ──
                // CPU 温度零值检测
                if (cpuTemp == 0)
                    _cpuTempZeroCount++;
                else
                    _cpuTempZeroCount = 0;

                // 风扇 RPM 零值检测（大扇+小扇都为零才算）
                if (cpuFan == 0 && gpuFan == 0)
                    _fanZeroCount++;
                else
                    _fanZeroCount = 0;

                // ── HealthWatchdog: 分级恢复 ──
                HealthWatchdogRecovery();

                // ── HealthWatchdog: Last Known Good 替换（推送缓存值而非 0）──
                if (cpuTemp == 0 && _lkgCpuTemp > 0) cpuTemp = _lkgCpuTemp;
                if (cpuFan == 0 && _lkgCpuFan > 0) cpuFan = _lkgCpuFan;
                if (gpuFan == 0 && _lkgGpuFan > 0) gpuFan = _lkgGpuFan;

                // 构建 JSON 负载（全量遥测）
                var payload = JsonSerializer.Serialize(new
                {
                    cpuUsage = _hal.CpuUsage,
                    cpuTemp,
                    cpuFreq = Math.Round(_hal.CpuFreq, 2),
                    cpuCores = _hal.CpuCores,
                    gpuUsage = _hal.GpuUsage,
                    gpuTemp,
                    gpuFreq = Math.Round(_hal.GpuFreq, 2),
                    gpuVram = _hal.GpuVram,
                    gpuVramUsed = _hal.GpuVramUsed,
                    gpuMemMhz = _hal.GpuMemMhz,
                    gpuPowerDrawW = _hal.GpuPowerDrawW,
                    fanLargeRpm = cpuFan,
                    fanSmallRpm = gpuFan,
                    fanLargeMax = HardwareAbstractionLayer.FanLargeMax,
                    fanSmallMax = HardwareAbstractionLayer.FanSmallMax,
                    memoryUsage = _hal.MemoryUsage,
                    memoryTotalGB = _hal.MemoryTotalGB,
                    memoryFreq = _hal.MemoryFreq,
                    diskUsage = _hal.DiskUsage,
                    diskTotalGB = _hal.DiskTotalGB,
                    diskFreeGB = _hal.DiskFreeGB,
                    kbBrightness = _hal.KeyboardBrightness,
                    fnLock = _wmi.Available ? _wmi.GetFnLock() == 1 : _hal.FnLock,
                    numLock = _hal.NumLock,
                    capsLock = _hal.CapsLock,
                    thermalMode = _wmi.Available ? _wmi.GetThermalMode() : _hal.ThermalMode,
                    powerPlan = _hal.PowerPlan,
                    touchpadLock = _wmi.Available ? _wmi.GetTouchpadLock() == 1 : _hal.TouchpadLocked,
                    igpuOnly = _hal.IgpuOnly,
                    gpuMode = _wmi.Available ? _wmi.GetGpuMode().ToString() : null,
                    timestamp = DateTime.Now.ToString("HH:mm:ss"),
                }, _jsonOpt);

                // 推送给所有 WebSocket 客户端
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                var segment = new ArraySegment<byte>(bytes);

                // 在锁内拍快照，锁外 await 发送
                WebSocket[] snapshot;
                lock (_clientLock) snapshot = _clients.ToArray();

                List<WebSocket> deadClients = new();
                foreach (var ws in snapshot)
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.SendAsync(segment, WebSocketMessageType.Text,
                                true, ct);
                        else
                            deadClients.Add(ws);
                    }
                    catch
                    {
                        deadClients.Add(ws);
                    }
                }

                // 清理断线客户端
                if (deadClients.Count > 0)
                {
                    lock (_clientLock)
                    {
                        foreach (var dead in deadClients)
                            _clients.Remove(dead);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Telemetry] 推送异常: {Msg}", ex.Message);
            }
        }
    }

    /// <summary>
    /// HealthWatchdog 分级恢复逻辑。
    /// 所有恢复动作通过 Task.Run() 异步触发，不阻塞遥测循环。
    /// </summary>
    private void HealthWatchdogRecovery()
    {
        if (_recovering != 0) return; // 恢复中，跳过

        // ── CPU 温度零值恢复 ──
        // 一级: 连续 20 次 (= 5s) → LHM Close→Open
        // 二级: 再连续 20 次 (= 又 5s) → RecoverAfterSleep
        if (_cpuTempZeroCount == 20)
        {
            AppLog.Write("HealthWatchdog", $"CPU 温度连续 {20} 次为 0, 一级恢复: LHM Close→Open");
            _recovering = 1;
            Task.Run(() =>
            {
                try
                {
                    LhmSensor.Close();
                    LhmSensor.Open();
                }
                catch (Exception ex) { AppLog.Write("HealthWatchdog", $"LHM 恢复异常: {ex.Message}"); }
                finally { _recovering = 0; _cpuTempZeroCount = 0; }
            });
        }
        else if (_cpuTempZeroCount >= 40)
        {
            AppLog.Write("HealthWatchdog", $"CPU 温度连续 {40} 次为 0, 二级恢复: RecoverAfterSleep");
            _recovering = 1;
            Task.Run(() =>
            {
                try { DriverBridge.Instance.RecoverAfterSleep(); }
                catch (Exception ex) { AppLog.Write("HealthWatchdog", $"RecoverAfterSleep 异常: {ex.Message}"); }
                finally { _recovering = 0; _cpuTempZeroCount = 0; }
            });
        }

        // ── 风扇 RPM 零值恢复 ──
        // 一级: 连续 40 次 (= 10s) → DriverBridge.Reset()
        // 二级: 再连续 40 次 (= 又 10s) → RecoverAfterSleep
        if (_fanZeroCount == 40)
        {
            AppLog.Write("HealthWatchdog", $"风扇 RPM 连续 {40} 次为 0, 一级恢复: DriverBridge.Reset");
            _recovering = 1;
            Task.Run(() =>
            {
                try { DriverBridge.Instance.Reset(); }
                catch (Exception ex) { AppLog.Write("HealthWatchdog", $"Reset 异常: {ex.Message}"); }
                finally { _recovering = 0; _fanZeroCount = 0; }
            });
        }
        else if (_fanZeroCount >= 80)
        {
            AppLog.Write("HealthWatchdog", $"风扇 RPM 连续 {80} 次为 0, 二级恢复: RecoverAfterSleep");
            _recovering = 1;
            Task.Run(() =>
            {
                try { DriverBridge.Instance.RecoverAfterSleep(); }
                catch (Exception ex) { AppLog.Write("HealthWatchdog", $"RecoverAfterSleep 异常: {ex.Message}"); }
                finally { _recovering = 0; _fanZeroCount = 0; }
            });
        }
    }
}
