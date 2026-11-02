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

    // 读取保存的目标 GPU 模式 (gpu-mode.json)，带回退路径和文件变更缓存
    private static string? _gpuModeConfigDir;
    private static int? _cachedSavedGpuMode;
    private static DateTime _cachedGpuModeWriteTime = DateTime.MinValue;

    private static string ResolveConfigDir()
    {
        if (_gpuModeConfigDir != null) return _gpuModeConfigDir;
        var dir = Path.Combine(AppContext.BaseDirectory, "config");
        var sharedDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "config"));
        if (Directory.Exists(sharedDir)) dir = sharedDir;
        else if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        _gpuModeConfigDir = dir;
        return dir;
    }

    private int? GetSavedGpuMode()
    {
        try
        {
            var path = Path.Combine(ResolveConfigDir(), "gpu-mode.json");
            if (!File.Exists(path)) return null;
            var writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime == _cachedGpuModeWriteTime) return _cachedSavedGpuMode;
            var json = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            int? mode = null;
            if (doc.RootElement.TryGetProperty("gpuMode", out var val))
                mode = val.GetInt32();
            _cachedSavedGpuMode = mode;
            _cachedGpuModeWriteTime = writeTime;
            return mode;
        }
        catch { }
        return null;
    }

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

    /// <summary>单个客户端的推送超时（毫秒）。超过即判定为僵死连接。</summary>
    private const int SendTimeoutMs = 1500;

    /// <summary>首帧是否已记录（用于首帧延迟诊断日志）</summary>
    private int _firstFrameLogged;

    /// <summary>
    /// 向单个客户端推送一帧。
    /// 必须带发送超时：客户端进程被杀 / 半开时 socket 仍报 Open，SendAsync 会一直挂起
    /// （发送缓冲区写满后永不返回）→ 整个推送循环卡死 → 所有前端都收不到遥测，
    /// 表现为"后端未连接"且迟迟不自愈。超时即判定僵死，Abort 掉并登记移除。
    /// </summary>
    private static async Task SendFrameAsync(WebSocket ws, ArraySegment<byte> segment,
        List<WebSocket> dead, object deadLock)
    {
        try
        {
            using var sendCts = new CancellationTokenSource(SendTimeoutMs);
            await ws.SendAsync(segment, WebSocketMessageType.Text, true, sendCts.Token);
        }
        catch (Exception ex)
        {
            lock (deadLock) dead.Add(ws);
            try { ws.Abort(); } catch { }   // 立即断开，不等关闭握手（否则可能再挂一次）
            AppLog.Write("Telemetry", $"WS 客户端推送失败（{ex.GetType().Name}），已断开该连接");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("[Telemetry] 后台遥测服务已启动 (HealthWatchdog enabled)");

        // 冷启动预热：等待驱动和 EC 寄存器稳定（开机自启时 inpoutx64 可能刚加载完成）
        await Task.Delay(2000, ct);

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
                    gpuMode = _wmi.Available ? _wmi.GetGpuMode().ToString() : null,
                    savedGpuMode = GetSavedGpuMode(),
                    // 壁纸自动轮换：把最近一次轮换时间戳塞进遥测帧（Unix 秒），前端发现变化即刷新背景图
                    bgLastRotated = BackgroundRotationService.LastRotatedUtc.HasValue
                        ? (long)(BackgroundRotationService.LastRotatedUtc.Value - DateTime.UnixEpoch).TotalSeconds
                        : 0,
                    timestamp = DateTime.Now.ToString("HH:mm:ss"),
                }, _jsonOpt);

                // 推送给所有 WebSocket 客户端
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                var segment = new ArraySegment<byte>(bytes);

                // 在锁内拍快照，锁外并发发送
                WebSocket[] snapshot;
                lock (_clientLock) snapshot = _clients.ToArray();

                List<WebSocket> deadClients = new();
                var deadLock = new object();
                var sends = new List<Task>();
                foreach (var ws in snapshot)
                {
                    if (ws.State != WebSocketState.Open) { deadClients.Add(ws); continue; }
                    sends.Add(SendFrameAsync(ws, segment, deadClients, deadLock));
                }
                await Task.WhenAll(sends);

                // 首帧延迟诊断：冷启动首帧包含驱动预热 + 首轮硬件读取，是"前端等很久才有数据"的关键指标
                if (Interlocked.Exchange(ref _firstFrameLogged, 1) == 0)
                    _log.LogInformation("[Telemetry] 首帧已推送（距服务启动 {Elapsed} ms，客户端 {Clients} 个）",
                        sw.ElapsedMilliseconds, snapshot.Length);

                // 清理断线/僵死客户端
                if (deadClients.Count > 0)
                {
                    lock (_clientLock)
                    {
                        foreach (var dead in deadClients)
                            _clients.Remove(dead);
                    }
                    _log.LogWarning("[Telemetry] 清理僵死 WebSocket 客户端 {Count} 个", deadClients.Count);
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
                    // PawnIO EC 路径无需恢复
                    AppLog.Write("HealthWatchdog", "CPU 温度零值计数复位");
                }
                catch (Exception ex) { AppLog.Write("HealthWatchdog", $"温度恢复异常: {ex.Message}"); }
                finally { _recovering = 0; _cpuTempZeroCount = 0; }
            });
        }
        else if (_cpuTempZeroCount >= 40)
        {
            AppLog.Write("HealthWatchdog", $"CPU 温度连续 {40} 次为 0 (PawnIO EC 路径可能异常)");
            _recovering = 1;
            Task.Run(() =>
            {
                try { _recovering = 0; _cpuTempZeroCount = 0; }
                catch { }
            });
        }

        // ── 风扇 RPM 零值恢复 ──
        if (_fanZeroCount == 40)
        {
            AppLog.Write("HealthWatchdog", $"风扇 RPM 连续 {40} 次为 0 (PawnIO EC 路径回退正常)");
            _recovering = 1;
            Task.Run(() =>
            {
                try { AppLog.Write("HealthWatchdog", "风扇一级恢复: 复位零值计数器"); }
                finally { _recovering = 0; _fanZeroCount = 0; }
            });
        }
        else if (_fanZeroCount >= 80)
        {
            AppLog.Write("HealthWatchdog", $"风扇 RPM 连续 {80} 次为 0 (PawnIO EC 路径可能异常)");
            _recovering = 1;
            Task.Run(() =>
            {
                try { AppLog.Write("HealthWatchdog", "风扇零值计数复位"); }
                finally { _recovering = 0; _fanZeroCount = 0; }
            });
        }
    }
}
