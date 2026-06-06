// SPDX-License-Identifier: MIT
//
// TelemetryBackgroundService — 后台遥测心跳
// ============================================
// 职责：
//   每 250ms 轮询 HAL 读取温度/风扇/状态，
//   通过 WebSocket 推送给前端。
//   同时检测寄存器变化实现"界面随动"。

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public class TelemetryBackgroundService : BackgroundService
{
    private readonly HardwareAbstractionLayer _hal;
    private readonly ILogger<TelemetryBackgroundService> _log;

    // 上次非零风扇值 — 过滤 EC 瞬态读到 0 的心电图问题
    private ushort _lastCpuFan;
    private ushort _lastGpuFan;

    // WebSocket 客户端列表
    private static readonly List<WebSocket> _clients = new();
    private static readonly object _clientLock = new();

    private static readonly JsonSerializerOptions _jsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TelemetryBackgroundService(
        HardwareAbstractionLayer hal,
        ILogger<TelemetryBackgroundService> log)
    {
        _hal = hal;
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
        _log.LogInformation("[Telemetry] 后台遥测服务已启动");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, ct);

                // 读取遥测
                var cpuTemp = _hal.CpuTemperature;
                var gpuTemp = _hal.GpuTemperature;
                var cpuFan = _hal.CpuFanRpm;
                var gpuFan = _hal.GpuFanRpm;

                // 过滤 EC 瞬态读到 0 的心电图问题：非零才更新 last，零则用 last
                if (cpuFan == 0 && _lastCpuFan > 0) cpuFan = _lastCpuFan; else _lastCpuFan = cpuFan;
                if (gpuFan == 0 && _lastGpuFan > 0) gpuFan = _lastGpuFan; else _lastGpuFan = gpuFan;

                // 构建 JSON 负载（全量遥测）
                var payload = JsonSerializer.Serialize(new
                {
                    cpuUsage = _hal.CpuUsage,
                    cpuTemp,
                    cpuFreq = _hal.CpuFreq,
                    cpuCores = _hal.CpuCores,
                    gpuUsage = _hal.GpuUsage,
                    gpuTemp,
                    gpuFreq = _hal.GpuFreq,
                    gpuVram = _hal.GpuVram,
                    gpuVramUsed = _hal.GpuVramUsed,
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
                    fnLock = _hal.FnLock,
                    numLock = _hal.NumLock,
                    capsLock = _hal.CapsLock,
                    thermalMode = _hal.ThermalMode,
                    powerPlan = _hal.PowerPlan,
                    touchpadLock = _hal.TouchpadLocked,
                    igpuOnly = _hal.IgpuOnly,
                    timestamp = DateTime.Now.ToString("HH:mm:ss"),
                }, _jsonOpt);

                // 推送给所有 WebSocket 客户端
                List<WebSocket> deadClients = new();
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                var segment = new ArraySegment<byte>(bytes);

                lock (_clientLock)
                {
                    foreach (var ws in _clients)
                    {
                        try
                        {
                            if (ws.State == WebSocketState.Open)
                                ws.SendAsync(segment, WebSocketMessageType.Text,
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
                    foreach (var dead in deadClients)
                        _clients.Remove(dead);
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
}
