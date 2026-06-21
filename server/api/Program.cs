using Douzhanzhe.HAL;
using Douzhanzhe.API;
using System.Net.WebSockets;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

// ---- AppLog 统一日志初始化（所有服务注册之前）----
var _logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Douzhanzhe Console", "logs");
AppLog.Init(_logDir);

// 提升进程与主线程优先级，确保在游戏满载时遥测采样与风扇控制仍能及时响应
var proc = Process.GetCurrentProcess();
proc.PriorityClass = ProcessPriorityClass.High;
Thread.CurrentThread.Priority = ThreadPriority.Highest;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<HardwareAbstractionLayer>();
builder.Services.AddSingleton<SmuController>();
builder.Services.AddSingleton<GpuController>();
builder.Services.AddSingleton<NvapiGpuController>();
builder.Services.AddSingleton<CpuPowerController>();
builder.Services.AddSingleton<WmiInterface>();
builder.Services.AddSingleton<FanCurveService>();
builder.Services.AddSingleton<OsdService>();
builder.Services.AddSingleton<GameProfileService>();
builder.Services.AddSingleton<ProcessMonitorService>();
builder.Services.AddHostedService<TelemetryBackgroundService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.IncludeFields = true);
var app = builder.Build();
var osdService = app.Services.GetRequiredService<OsdService>();
var hal = app.Services.GetRequiredService<HardwareAbstractionLayer>();
var wmi = app.Services.GetRequiredService<WmiInterface>();
app.UseCors();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // index.html 禁止缓存（确保更新后前端 JS bundle 立即生效）
        if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
    }
});
app.MapFallbackToFile("index.html");
// ---- Config directory (shared with Node.js) ----
// 安装环境: AppContext.BaseDirectory\config\
// 开发环境: BaseDirectory\bin\build\ → 需要回退到项目根目录\config\
var configDir = Path.Combine(AppContext.BaseDirectory, "config");
if (!Directory.Exists(configDir))
{
    var devConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
    if (Directory.Exists(devConfig))
        configDir = devConfig;
}
Directory.CreateDirectory(configDir);

// ---- File logger (统一走 AppLog) ----
void Log(string msg)
{
    AppLog.Write("API", msg);
}
Log($"API starting, BaseDir={AppContext.BaseDirectory}, ConfigDir={configDir}");

// ---- 性能设置持久化 (按模式存储) ----
var _perfLock = new object();
var _lastModeFile = "last-mode.json";
bool _pgSuppress = false; // ParameterGuard 睡眠期间暂停标志

string CurrentMode()
{
    var d = JsonRead<Dictionary<string, string>>(_lastModeFile, new());
    return d.TryGetValue("mode", out var m) ? m : "office";
}

void SetCurrentMode(string mode)
{
    JsonWrite(_lastModeFile, new Dictionary<string, string> { ["mode"] = mode });
}

// 前端模式 ID → EC thermal_mode 数值
var _modeToThermal = new Dictionary<string, byte> { ["silent"] = 2, ["office"] = 0, ["beast"] = 1, ["gaming"] = 3 };

void ApplyThermalMode(string mode)
{
    if (!_modeToThermal.TryGetValue(mode, out var thermalVal)) return;
    Log($"[thermal_mode] ← mode={mode} (value={thermalVal})");
    if (wmi.Available)
        wmi.SetThermalMode(thermalVal);
    else
        hal.ThermalMode = thermalVal;
    osdService.Show(mode);
    app.Services.GetRequiredService<ProcessMonitorService>().UpdateCurrentMode(mode);
}

PerformanceOverrides LoadPerfOverrides()
    => JsonRead($"overrides-{CurrentMode()}.json", new PerformanceOverrides());

void SavePerfOverrides(Action<PerformanceOverrides> mutate, string? mode = null)
{
    lock (_perfLock)
    {
        var file = $"overrides-{mode ?? CurrentMode()}.json";
        var o = JsonRead(file, new PerformanceOverrides());
        mutate(o);
        JsonWrite(file, o);
        Log($"[overrides] ✓ saved → {file}{(mode != null ? " (pinned)" : "")}");
    }
}

// ---- 风扇转速写入辅助方法 (WMI + EC 寄存器直写) ----
void ApplyFanSpeed(WmiInterface wmi, HardwareAbstractionLayer hal, int? largeRpm, int? smallRpm)
{
    if (largeRpm.HasValue)
    {
        var speed = (byte)Math.Clamp(largeRpm.Value / 100, 0, 44);
        wmi.SetFanManual(0, true);
        wmi.SetFanSpeed(0, speed);
        hal.WriteEcPort(0x5E, speed);
    }
    if (smallRpm.HasValue)
    {
        var speed = (byte)Math.Clamp(smallRpm.Value / 100, 0, 82);
        wmi.SetFanManual(1, true);
        wmi.SetFanSpeed(1, speed);
        hal.WriteEcPort(0x5A, speed);
    }
}

// ---- 睡眠/休眠恢复：重置底层驱动并重新初始化 ----
SystemEvents.PowerModeChanged += (sender, e) =>
{
    if (e.Mode == PowerModes.Suspend)
    {
        // 系统即将进入睡眠，暂停 ParameterGuard 和 HealthWatchdog 恢复
        _pgSuppress = true;
        TelemetryBackgroundService.SetSleeping(true);
        Log("[PowerEvent] 系统即将睡眠，暂停 ParameterGuard + HealthWatchdog");
    }
    else if (e.Mode == PowerModes.Resume)
    {
        Log("[PowerEvent] 系统从睡眠恢复，重置底层驱动...");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // inpoutx64 内核驱动在 S3/S4 后可能失效，必须重置映射并重新初始化
                DriverBridge.Instance.RecoverAfterSleep();

                // LHM 需要重新初始化（SMN 总线可能在睡眠后失效）
                LhmSensor.Close();
                LhmSensor.Open();

                // NVAPI 也需要重新初始化（GPU 驱动可能在睡眠后重新加载）
                var nv = app.Services.GetRequiredService<NvapiGpuController>();
                nv.Init();

                // GPU 模式恢复 (从 gpu-mode.json)
                var saved = JsonRead<Dictionary<string, int>>("gpu-mode.json", new Dictionary<string, int>());
                if (saved.TryGetValue("gpuMode", out int mode) && mode >= 0 && mode <= 2)
                {
                    var wmi = app.Services.GetRequiredService<WmiInterface>();
                    if (wmi.Available)
                    {
                        if (wmi.SetGpuMode((byte)mode))
                            Log($"[PowerEvent] GPU mode → {mode}");
                        else
                            Log($"[PowerEvent] GPU mode restore to {mode} failed");
                    }
                }

                // 恢复所有性能设置 (CPU/SMU/GPU/NVAPI/固定风扇)
                await RestoreAllPerfSettings("PowerEvent");

                // 自定义风扇曲线: 重新下发 ITSM 模式 + 重置 ShouldWrite 状态
                var fanCurve = app.Services.GetRequiredService<FanCurveService>();
                fanCurve.RecoverAfterSleep();

                Log("[PowerEvent] 全部恢复完成");

                // 等 SMU 完全稳定后再恢复 ParameterGuard 和 HealthWatchdog
                await System.Threading.Tasks.Task.Delay(30_000);
                _pgSuppress = false;
                TelemetryBackgroundService.SetSleeping(false);
                Log("[PowerEvent] ParameterGuard + HealthWatchdog 已恢复");
            }
            catch (Exception ex)
            {
                Log($"[PowerEvent] 恢复异常: {ex.Message}");
            }
        });
    }
};
// ---- JSON persistence helpers ----
T JsonRead<T>(string fileName, T fallback) where T : class
{
    var filePath = Path.Combine(configDir, fileName);
    if (!File.Exists(filePath)) return fallback;
    try
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            IncludeFields = true
        };
        return JsonSerializer.Deserialize<T>(File.ReadAllText(filePath), opts) ?? fallback;
    }
    catch { return fallback; }
}
void JsonWrite<T>(string fileName, T data)
{
    var filePath = Path.Combine(configDir, fileName);
    var tmpPath = filePath + ".tmp";
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true
    });
    File.WriteAllText(tmpPath, json);
    File.Move(tmpPath, filePath, overwrite: true);
}

// ---- 恢复计算类性能设置 (CPU + SMU + GPU + NVAPI, 不含风扇) ----
// 供启动、睡眠恢复、ParameterGuard 共用
async System.Threading.Tasks.Task RestoreComputeSettings(string tag)
{
    try
    {
        var o = LoadPerfOverrides();
        int restored = 0;

        // --- CPU (powercfg) ---
        if (o.Cpu.FreqLimitMhz.HasValue)
        {
            try { await app.Services.GetRequiredService<CpuPowerController>().SetFreqLimitAsync(o.Cpu.FreqLimitMhz.Value); restored++; Log($"[{tag}] CPU freq limit → {o.Cpu.FreqLimitMhz.Value} MHz"); }
            catch (Exception ex) { Log($"[{tag}] CPU freq limit failed: {ex.Message}"); }
        }
        if (o.Cpu.TurboEnabled.HasValue)
        {
            try { await app.Services.GetRequiredService<CpuPowerController>().SetTurboAsync(o.Cpu.TurboEnabled.Value); restored++; Log($"[{tag}] CPU turbo → {o.Cpu.TurboEnabled.Value}"); }
            catch (Exception ex) { Log($"[{tag}] CPU turbo failed: {ex.Message}"); }
        }
        if (o.Cpu.CoreLimitPercent.HasValue && o.Cpu.CoreLimitPercent.Value > 0)
        {
            try { await app.Services.GetRequiredService<CpuPowerController>().SetCoreLimitAsync(o.Cpu.CoreLimitPercent.Value); restored++; Log($"[{tag}] CPU core limit → {o.Cpu.CoreLimitPercent.Value}%"); }
            catch (Exception ex) { Log($"[{tag}] CPU core limit failed: {ex.Message}"); }
        }

        // --- SMU (ryzenadj) — 合并为单次 BatchApply 调用，避免 4 次串行进程启动 ---
        var smu = app.Services.GetRequiredService<SmuController>();
        {
            var stapmMw = o.Smu.StapmLimitW.HasValue ? (uint?)(o.Smu.StapmLimitW.Value * 1000) : null;
            var fastMw = o.Smu.ShortPowerLimitW.HasValue ? (uint?)(o.Smu.ShortPowerLimitW.Value * 1000) : null;
            var slowMw = fastMw;
            var tempC = o.Smu.TempLimitC.HasValue ? (uint?)o.Smu.TempLimitC.Value : null;
            var coAll = o.Smu.CoAll.HasValue ? (int?)o.Smu.CoAll.Value : null;

            if (stapmMw.HasValue || fastMw.HasValue || tempC.HasValue || coAll.HasValue)
            {
                try
                {
                    smu.BatchApply(stapmMw, fastMw, slowMw, tempC, coAll, null);
                    int smuCount = 0;
                    if (stapmMw.HasValue) { smuCount++; Log($"[{tag}] SMU stapm → {o.Smu.StapmLimitW!.Value}W"); }
                    if (fastMw.HasValue) { smuCount++; Log($"[{tag}] SMU short power → {o.Smu.ShortPowerLimitW!.Value}W"); }
                    if (tempC.HasValue) { smuCount++; Log($"[{tag}] SMU temp → {o.Smu.TempLimitC!.Value}°C"); }
                    if (coAll.HasValue) { smuCount++; Log($"[{tag}] SMU CO → {o.Smu.CoAll!.Value}"); }
                    restored += smuCount;
                }
                catch (Exception ex) { Log($"[{tag}] SMU BatchApply failed: {ex.Message}"); }
            }
        }

        // --- GPU mode 检测: 优先读保存的目标模式(gpu-mode.json)，回退到 EC 当前值 ---
        byte gpuMode = 1; // 默认独显
        try
        {
            var gpuModeFile = JsonRead<Dictionary<string, int>>("gpu-mode.json", new Dictionary<string, int>());
            if (gpuModeFile.TryGetValue("gpuMode", out int savedMode) && savedMode >= 0 && savedMode <= 2)
                gpuMode = (byte)savedMode;
            else
                gpuMode = app.Services.GetRequiredService<WmiInterface>().GetGpuMode();
        }
        catch { }

        // --- GPU (nvidia-smi) ---
        // 混合模式(0): 跳过时钟锁定，避免干扰 Optimus P-state 管理
        // 集显模式(2): 跳过所有 GPU 命令（独显不可用）
        var gpu = app.Services.GetRequiredService<GpuController>();
        if (gpuMode != 2 && o.Gpu.CoreFreqMhz.HasValue && o.Gpu.CoreFreqMhz.Value > 0)
        {
            try
            {
                if (gpuMode == 0)
                {
                    Log($"[{tag}] GPU core skipped (hybrid mode, gpuMode=0)");
                }
                else
                {
                    gpu.SetMaxGpuClock(o.Gpu.CoreFreqMhz.Value);
                    if (o.Gpu.FreqLocked == true) gpu.SetExactGpuClock(o.Gpu.CoreFreqMhz.Value);
                    restored++;
                    Log($"[{tag}] GPU core → {o.Gpu.CoreFreqMhz.Value} MHz (locked={o.Gpu.FreqLocked})");
                }
            }
            catch (Exception ex) { Log($"[{tag}] GPU core failed: {ex.Message}"); }
        }
        if (gpuMode != 2 && o.Gpu.MemFreqLevel.HasValue && o.Gpu.MemFreqLevel.Value > 0)
        {
            try
            {
                if (gpuMode == 0)
                {
                    Log($"[{tag}] GPU mem skipped (hybrid mode, gpuMode=0)");
                }
                else
                {
                    var memMap = new int[] { 0, 9001, 11001, 12001 };
                    var idx = Math.Clamp(o.Gpu.MemFreqLevel.Value, 0, 3);
                    if (idx > 0) gpu.SetMaxMemoryClock(memMap[idx]);
                    restored++;
                    Log($"[{tag}] GPU mem level → {idx} ({memMap[idx]} MHz)");
                }
            }
            catch (Exception ex) { Log($"[{tag}] GPU mem failed: {ex.Message}"); }
        }

        // --- NVAPI ---
        // 集显模式(2): 跳过所有 NVAPI（独显不可用）
        // 混合模式(0): NVAPI 偏移/温度正常下发（不干扰 Optimus）
        var nv = app.Services.GetRequiredService<NvapiGpuController>();
        if (gpuMode != 2 && (o.Nvapi.OcCoreOffsetMhz.HasValue || o.Nvapi.OcMemOffsetMhz.HasValue))
        {
            try
            {
                nv.SetP0Offset(o.Nvapi.OcCoreOffsetMhz ?? 0, o.Nvapi.OcMemOffsetMhz ?? 0);
                restored++;
                Log($"[{tag}] NVAPI OC → core={o.Nvapi.OcCoreOffsetMhz ?? 0}, mem={o.Nvapi.OcMemOffsetMhz ?? 0}");
            }
            catch (Exception ex) { Log($"[{tag}] NVAPI OC failed: {ex.Message}"); }
        }
        if (gpuMode != 2 && o.Nvapi.PowerLimitW.HasValue)
        {
            try { nv.SetPowerLimit((uint)(o.Nvapi.PowerLimitW.Value * 1000)); restored++; Log($"[{tag}] NVAPI power → {o.Nvapi.PowerLimitW.Value}W"); }
            catch (Exception ex) { Log($"[{tag}] NVAPI power failed: {ex.Message}"); }
        }
        if (gpuMode != 2 && o.Nvapi.ThermalLimitC.HasValue)
        {
            try { nv.SetThermalLimit(o.Nvapi.ThermalLimitC.Value); restored++; Log($"[{tag}] NVAPI thermal → {o.Nvapi.ThermalLimitC.Value}°C"); }
            catch (Exception ex) { Log($"[{tag}] NVAPI thermal failed: {ex.Message}"); }
        }

        // --- 电源计划 ---
        if (o.PowerPlan.HasValue)
        {
            try
            {
                var hal2 = app.Services.GetRequiredService<HardwareAbstractionLayer>();
                hal2.PowerPlan = o.PowerPlan.Value;
                restored++;
                var planNames = new[] { "平衡", "高性能", "节能" };
                var idx = Math.Clamp(o.PowerPlan.Value, 0, 2);
                Log($"[{tag}] Power plan → {planNames[idx]} ({idx})");
            }
            catch (Exception ex) { Log($"[{tag}] Power plan failed: {ex.Message}"); }
        }

        if (restored > 0) Log($"[{tag}] Compute settings restored: {restored} applied");
        else Log($"[{tag}] No compute settings to restore");
    }
    catch (Exception ex) { Log($"[{tag}] Compute settings restore failed: {ex.Message}"); }
}

// ---- 恢复所有性能设置 (启动 + 睡眠恢复共用, 含风扇) ----
async System.Threading.Tasks.Task RestoreAllPerfSettings(string tag)
{
    await RestoreComputeSettings(tag);

    // --- 固定风扇转速 (仅启动和睡眠恢复时执行, ParameterGuard 不调用) ---
    try
    {
        var o = LoadPerfOverrides();
        if (o.Fan.LargeRpm.HasValue || o.Fan.SmallRpm.HasValue)
        {
            var wmi = app.Services.GetRequiredService<WmiInterface>();
            var hal = app.Services.GetRequiredService<HardwareAbstractionLayer>();
            ApplyFanSpeed(wmi, hal, o.Fan.LargeRpm, o.Fan.SmallRpm);
            Log($"[{tag}] Fan target → large={o.Fan.LargeRpm ?? 0} small={o.Fan.SmallRpm ?? 0}");
        }
    }
    catch (Exception ex) { Log($"[{tag}] Fan target failed: {ex.Message}"); }
}

// ---- ParameterGuard: 60 秒周期性幂等重发计算类参数 ----
_ = System.Threading.Tasks.Task.Run(async () =>
{
    await System.Threading.Tasks.Task.Delay(10_000); // 启动后等 10 秒再开始
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
    var fanCurveSvc = app.Services.GetRequiredService<FanCurveService>();
    try
    {
        while (await timer.WaitForNextTickAsync())
        {
            if (_pgSuppress)
            {
                AppLog.Write("ParameterGuard", "睡眠期间暂停，跳过本轮重发");
                continue;
            }
            try
            {
                await RestoreComputeSettings("ParameterGuard");

                // 风扇固定转速守护: 仅在自定义曲线未运行时守护手动 RPM，防止 EC 漂移
                if (!fanCurveSvc.Active)
                {
                    var o = LoadPerfOverrides();
                    if (o.Fan.LargeRpm.HasValue || o.Fan.SmallRpm.HasValue)
                    {
                        var wmi = app.Services.GetRequiredService<WmiInterface>();
                        var hal = app.Services.GetRequiredService<HardwareAbstractionLayer>();
                        ApplyFanSpeed(wmi, hal, o.Fan.LargeRpm, o.Fan.SmallRpm);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("ParameterGuard", $"参数重发失败: {ex.Message}");
            }
        }
    }
    catch (OperationCanceledException) { /* 正常退出 */ }
});

// ---- 启动时恢复 GPU 模式 (异步，不阻塞服务启动) ----
_ = System.Threading.Tasks.Task.Run(() =>
{
    try
    {
        var saved = JsonRead<Dictionary<string, int>>("gpu-mode.json", new Dictionary<string, int>());
        bool hasSaved = saved.TryGetValue("gpuMode", out int mode) && mode >= 0 && mode <= 2;

        var wmiStartup = app.Services.GetRequiredService<WmiInterface>();
        // 最多重试 3 次，每次间隔 2 秒，等待 WMI 就绪
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (!wmiStartup.Available)
            {
                Log($"[Startup] WMI not available, retry {attempt}/3...");
                System.Threading.Thread.Sleep(2000);
                continue;
            }

            // 读取固件当前 GPU mode
            byte firmware = wmiStartup.GetGpuMode();
            Log($"[Startup] Firmware GPU mode={firmware}, saved={( hasSaved ? mode.ToString() : "none" )}");

            // 有存档且有效 → 恢复到存档值（尊重用户选择，包括 iGPU-only）
            if (hasSaved)
            {
                // 固件已与存档一致，无需切换
                if (firmware == mode)
                {
                    Log($"[Startup] GPU mode already at {mode}, no action needed");
                    return;
                }

                if (!wmiStartup.SetGpuMode((byte)mode))
                {
                    Log($"[Startup] SetGpuMode({mode}) failed, retry {attempt}/3...");
                    System.Threading.Thread.Sleep(2000);
                    continue;
                }
                byte current = wmiStartup.GetGpuMode();
                if (current == mode)
                {
                    Log($"[Startup] GPU mode restored to {mode} (verified)");
                    return;
                }
                Log($"[Startup] GPU mode mismatch: expected {mode}, got {current}, retry {attempt}/3...");
                System.Threading.Thread.Sleep(2000);
                continue;
            }

            // 无存档 → 不干预固件状态，仅记录
            Log($"[Startup] No saved GPU mode, firmware is at {firmware}, no action needed");
            return;
        }
        Log("[Startup] GPU mode restore failed after 3 attempts");
    }
    catch (Exception ex) { Log($"[Startup] GPU mode restore failed: {ex.Message}"); }
});
// ---- 启动时恢复性能设置 (异步，在 GPU 模式恢复之后) ----
_ = System.Threading.Tasks.Task.Run(async () =>
{
    await System.Threading.Tasks.Task.Delay(3000);
    // 清理旧版遗留文件（前端 localStorage 迁移是 overrides 的权威数据源）
    {
        var oldPerfPath = Path.Combine(configDir, "performance-overrides.json");
        if (File.Exists(oldPerfPath))
        {
            File.Delete(oldPerfPath);
            Log("[Startup] Cleaned up legacy performance-overrides.json");
        }
    }
    await RestoreAllPerfSettings("Startup");
});
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("需要 WebSocket 连接");
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    TelemetryBackgroundService.AddClient(ws);
    ProcessMonitorService.AddClient(ws);
    try
    {
        var buf = new byte[4096];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = null;
            try { result = await ws.ReceiveAsync(buf, CancellationToken.None); }
            catch (WebSocketException) { break; }
            if (result != null && result.MessageType == WebSocketMessageType.Close) break;
        }
    }
    finally
    {
        TelemetryBackgroundService.RemoveClient(ws);
        ProcessMonitorService.RemoveClient(ws);
        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
        }
    }
});
app.MapGet("/api/telemetry", (HardwareAbstractionLayer hal, WmiInterface wmi) =>
{
    return Results.Json(new
    {
        cpuUsage = hal.CpuUsage,
        cpuTemp = hal.CpuTemperature,
        cpuFreq = hal.CpuFreq,
        cpuCores = hal.CpuCores,
        gpuUsage = hal.GpuUsage,
        gpuTemp = hal.GpuTemperature,
        gpuFreq = hal.GpuFreq,
        gpuVram = hal.GpuVram,
        gpuVramUsed = hal.GpuVramUsed,
        gpuMemMhz = hal.GpuMemMhz,
        gpuPowerDrawW = hal.GpuPowerDrawW,
        fanLargeRpm = hal.CpuFanRpm,
        fanSmallRpm = hal.GpuFanRpm,
        fanLargeMax = HardwareAbstractionLayer.FanLargeMax,
        fanSmallMax = HardwareAbstractionLayer.FanSmallMax,
        memoryUsage = hal.MemoryUsage,
        memoryTotalGB = hal.MemoryTotalGB,
        memoryFreq = hal.MemoryFreq,
        diskUsage = hal.DiskUsage,
        diskTotalGB = hal.DiskTotalGB,
        diskFreeGB = hal.DiskFreeGB,
        kbBrightness = hal.KeyboardBrightness,
        fnLock = wmi.Available ? wmi.GetFnLock() == 1 : hal.FnLock,
        numLock = hal.NumLock,
        capsLock = hal.CapsLock,
        thermalMode = wmi.Available ? wmi.GetThermalMode() : hal.ThermalMode,
        powerPlan = hal.PowerPlan,
        touchpadLock = wmi.Available ? wmi.GetTouchpadLock() == 1 : hal.TouchpadLocked,
        igpuOnly = hal.IgpuOnly,
        gpuMode = wmi.Available ? wmi.GetGpuMode().ToString() : null,
    });
});
app.MapGet("/api/system/info", (HardwareAbstractionLayer hal) =>
{
    return Results.Json(new
    {
        systemModel = hal.SystemModel,
        cpuName = hal.CpuName,
        cpuCores = hal.CpuCores,
        cpuFreq = Math.Round((double)hal.CpuFreq, 1),
        gpuDiscrete = hal.GpuDiscreteName,
        gpuIntegrated = hal.GpuIntegratedName,
        memoryTotalGB = hal.MemoryTotalGB,
        memoryFreq = hal.MemoryFreq,
        diskTotalGB = hal.DiskTotalGB,
    });
});

// Extended system info (BIOS/OS/disks/memory sticks/GPU driver) — single PowerShell call
var _sysInfoExtCache = "";
var _sysInfoExtTime = DateTime.MinValue;
app.MapGet("/api/system/info-ext", () =>
{
    if ((DateTime.UtcNow - _sysInfoExtTime).TotalSeconds < 60 && !string.IsNullOrEmpty(_sysInfoExtCache))
        return Results.Content(_sysInfoExtCache, "application/json; charset=utf-8");
    try
    {
        // bin/<config>/net8.0/ → project root (up 3), or bin/run/ or bin/build/ → project root (up 2)
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "sysinfo-ext.ps1");
        if (!File.Exists(scriptPath))
            scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "sysinfo-ext.ps1");
        if (!File.Exists(scriptPath))
            scriptPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sysinfo-ext.ps1");
        if (!File.Exists(scriptPath))
            scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "sysinfo-ext.ps1");
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        p.Start();
        if (!p.WaitForExit(8000)) { p.Kill(); return Results.Json(new { error = "timeout" }); }
        var json = p.StandardOutput.ReadToEnd().Trim();
        if (!string.IsNullOrEmpty(json))
        {
            _sysInfoExtCache = json;
            _sysInfoExtTime = DateTime.UtcNow;
        }
        return Results.Content(_sysInfoExtCache, "application/json; charset=utf-8");
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message });
    }
});

app.MapGet("/api/health", (HardwareAbstractionLayer hal) =>
{
    return Results.Json(new
    {
        ok = hal.HealthCheck(),
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
});
app.MapPost("/api/control", (ControlRequest req, HardwareAbstractionLayer hal, WmiInterface wmi, string? mode = null) =>
{
    try
    {
        Log($"[control] ← target={req.Target} value={req.Value}");
        switch (req.Target)
        {
            case "kb_light":
                hal.KeyboardBrightness = (byte)int.Clamp(req.Value, 0, 3);
                break;
            case "fn_lock":
                wmi.SetFnLock(req.Value != 0);
                break;
            case "num_lock":
                hal.NumLock = req.Value != 0;
                break;
            case "caps_lock":
                hal.CapsLock = req.Value != 0;
                break;
            case "touchpad_lock":
                wmi.SetTouchpadLock(req.Value != 0);
                break;
            case "power_plan":
                hal.PowerPlan = req.Value;
                SavePerfOverrides(o => o.PowerPlan = req.Value, mode);
                break;
            case "thermal_mode":
                {
                    var modeNames = new[] { "office", "beast", "silent", "gaming" };
                    var clampedMode = (byte)int.Clamp(req.Value, 0, 3);
                    if (clampedMode < modeNames.Length)
                    {
                        SetCurrentMode(modeNames[clampedMode]);
                        ApplyThermalMode(modeNames[clampedMode]);
                    }
                }
                break;
            case "igpu_only":
                hal.IgpuOnly = req.Value != 0;
                break;
            case "gpu_mode":
                {
                    var gpuVal = (byte)int.Clamp(req.Value, 0, 2);
                    if (!wmi.SetGpuMode(gpuVal))
                        return Results.Problem("WMI GPUMode failed", statusCode: 500);
                    // 持久化用户选择的 GPU 模式，重启后自动恢复
                    JsonWrite("gpu-mode.json", new { gpuMode = gpuVal });
                }
                break;
            case string t when t.StartsWith("ec_write:"):
                {
                    var parts_ = t.Split(':');
                    if (parts_.Length >= 2 && parts_[1].StartsWith("0x"))
                    {
                        byte reg = Convert.ToByte(parts_[1], 16);
                        hal.WriteEcPort(reg, (byte)req.Value);
                    }
                }
                break;
            default:
                return Results.Problem($"未知控制目标: {req.Target}", statusCode: 400);
        }
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// ---- 关闭显示器 ----
app.MapPost("/api/monitor/off", () =>
{
    NativeMethods.SendMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));
    return Results.Ok(new { ok = true });
});

// ---- 快捷键配置 ----
app.MapGet("/api/hotkey/monitor-off", () =>
{
    var cfgPath = Path.Combine(configDir, "hotkey-config.json");
    if (!File.Exists(cfgPath))
        return Results.Json(new { enabled = true, modifiers = "ctrl,shift", key = "Q", conflict = false });
    try
    {
        var json = File.ReadAllText(cfgPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var mo = root.TryGetProperty("monitorOff", out var moVal) ? moVal : root;
        bool conflict = false;
        var statusPath = Path.Combine(configDir, "hotkey-status.json");
        if (File.Exists(statusPath))
        {
            try
            {
                var sJson = File.ReadAllText(statusPath);
                using var sDoc = JsonDocument.Parse(sJson);
                conflict = sDoc.RootElement.TryGetProperty("monitorOffConflict", out var cv) && cv.GetBoolean();
            }
            catch { }
        }
        return Results.Json(new
        {
            enabled = mo.TryGetProperty("enabled", out var ev) ? ev.GetBoolean() : true,
            modifiers = mo.TryGetProperty("modifiers", out var mv) ? mv.GetString() : "ctrl,shift",
            key = mo.TryGetProperty("key", out var kv) ? kv.GetString() : "Q",
            conflict
        });
    }
    catch
    {
        return Results.Json(new { enabled = true, modifiers = "ctrl,shift", key = "Q", conflict = false });
    }
});

app.MapPost("/api/hotkey/monitor-off", (HotkeyConfigRequest req) =>
{
    var cfgPath = Path.Combine(configDir, "hotkey-config.json");
    // 读取现有配置并合并
    var existing = new Dictionary<string, object>();
    if (File.Exists(cfgPath))
    {
        try { existing = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfgPath)) ?? new(); } catch { }
    }
    var monitorOff = new Dictionary<string, object>
    {
        ["enabled"] = req.Enabled,
        ["modifiers"] = req.Modifiers ?? "ctrl,shift",
        ["key"] = req.Key ?? "Q"
    };
    existing["monitorOff"] = monitorOff;
    JsonWrite("hotkey-config.json", existing);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/discover", (HardwareAbstractionLayer hal) =>
{
    return Results.Json(new
    {
        available = hal.HealthCheck(),
        ecBase = $"0x{DriverBridge.EC_BASE:X}",
        driverLoaded = DriverBridge.Instance.Ready,
        touchpad = true,
    });
});
app.MapGet("/api/ec-scan", (HttpContext ctx, HardwareAbstractionLayer hal) =>
{
    var offsetStr = ctx.Request.Query["offset"].FirstOrDefault() ?? "0";
    var countStr = ctx.Request.Query["count"].FirstOrDefault() ?? "16";
    try
    {
        uint offset = offsetStr.StartsWith("0x") ? Convert.ToUInt32(offsetStr, 16) : uint.Parse(offsetStr);
        int count = int.Parse(countStr);
        count = Math.Clamp(count, 1, 64);
        if (offset + count > 0xFF) count = (int)(0xFF - offset);
        if (count <= 0) return Results.Json(new { error = "超出范围" }, statusCode: 400);
        var results = new List<object>();
        for (int i = 0; i < count; i++)
        {
            byte val = 0;
            try { val = hal.ReadEcPort((byte)(offset + i)); } catch { val = 0; }
            results.Add(new { offset = $"0x{offset + i:X2}", value = val });
        }
        return Results.Json(new { ecBase = "0xFE800400", offset, count, results });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});
app.MapPost("/api/smu/set", (SmuController smu, SmuSetRequest req, string? mode = null) =>
{
    try
    {
        Log($"[smu/set] ← {req.Parameter}={req.ValueM}");
        int rc;
        switch (req.Parameter)
        {
            case "stapm_limit":
            case "power_limit":
                rc = smu.SetPowerLimit((uint)(req.ValueM * 1000));
                SavePerfOverrides(o => o.Smu.StapmLimitW = req.ValueM, mode);
                break;
            case "short_power_limit":
                rc = smu.SetShortPowerLimit((uint)(req.ValueM * 1000), (uint)(req.ValueM * 1000));
                SavePerfOverrides(o => o.Smu.ShortPowerLimitW = req.ValueM, mode);
                break;
            case "tctl_temp":
            case "temp_limit":
                rc = smu.SetTempLimit((uint)req.ValueM);
                SavePerfOverrides(o => o.Smu.TempLimitC = req.ValueM, mode);
                break;
            case "co_all":
                rc = smu.SetCurveOptimizer(req.ValueM);
                SavePerfOverrides(o => o.Smu.CoAll = req.ValueM, mode);
                break;
            case "turbo_disable":
                rc = smu.SetTurboDisabled(req.ValueM != 0);
                break;
            default:
                return Results.Json(new { ok = false, error = "unknown parameter: " + req.Parameter });
        }
        return Results.Json(new { ok = rc == 0, rc });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
app.MapGet("/api/smu/status", (SmuController smu) =>
{
    try
    {
        var probe = smu.Probe();
        var caps = smu.GetCapabilities();
        return Results.Json(new { ok = true, probe, source = "ryzenadj", capabilities = caps });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message, source = "ryzenadj" });
    }
});
app.MapPost("/api/fan/set-target", (FanSetRequest req, WmiInterface wmi, HardwareAbstractionLayer hal, string? mode = null) =>
{
    try
    {
        Log($"[fan/set-target] ← large={req.LargeRpm} small={req.SmallRpm}");
        ApplyFanSpeed(wmi, hal, req.LargeRpm, req.SmallRpm);
        // 持久化固定风扇转速，供睡眠恢复 + 启动恢复使用
        SavePerfOverrides(o =>
        {
            if (req.LargeRpm.HasValue) o.Fan.LargeRpm = req.LargeRpm.Value;
            if (req.SmallRpm.HasValue) o.Fan.SmallRpm = req.SmallRpm.Value;
        }, mode);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});
// ---- Fan write strategy test (compare manual flag behavior) ----
app.MapPost("/api/fan/test-write", (FanTestWriteRequest req, WmiInterface wmi) =>
{
    try
    {
        var strategy = req.Strategy ?? "manual-true";
        var largeSpeed = (byte)Math.Clamp((req.LargeRpm ?? 2900) / 100, 0, 44);
        var smallSpeed = (byte)Math.Clamp((req.SmallRpm ?? 6900) / 100, 0, 82);

        switch (strategy)
        {
            case "manual-true":
                // Current approach: Manual(true) + Speed, interleaved
                wmi.SetFanManual(0, true);
                wmi.SetFanSpeed(0, largeSpeed);
                wmi.SetFanManual(1, true);
                wmi.SetFanSpeed(1, smallSpeed);
                break;

            case "speed-only":
                // No manual flag change, just speed writes
                wmi.SetFanSpeed(0, largeSpeed);
                wmi.SetFanSpeed(1, smallSpeed);
                break;

            case "manual-false":
                // Set manual to false first, then speed
                wmi.SetFanManual(0, false);
                wmi.SetFanSpeed(0, largeSpeed);
                wmi.SetFanManual(1, false);
                wmi.SetFanSpeed(1, smallSpeed);
                break;

            case "speed-then-manual":
                // Speed first, then manual (reversed order)
                wmi.SetFanSpeed(0, largeSpeed);
                wmi.SetFanSpeed(1, smallSpeed);
                wmi.SetFanManual(0, true);
                wmi.SetFanManual(1, true);
                break;

            default:
                return Results.Json(new { ok = false, error = "Unknown strategy: " + strategy });
        }

        return Results.Json(new { ok = true, strategy, largeSpeed, smallSpeed });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
app.MapPost("/api/fan/restore", (WmiInterface wmi, string? mode = null) =>
{
    try
    {
        wmi.SetFanManual(0, false);
        wmi.SetFanManual(1, false);
        // 清除持久化的风扇转速
        SavePerfOverrides(o => { o.Fan.LargeRpm = null; o.Fan.SmallRpm = null; }, mode);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
// ---- Fan status read (WMI Bellator GET) ----
app.MapGet("/api/fan/status", (WmiInterface wmi) =>
{
    try
    {
        var manualEnabled = wmi.GetFanManualEnabled();
        var largeTarget = wmi.GetFanSpeed(0) * 100;
        var smallTarget = wmi.GetFanSpeed(1) * 100;
        return Results.Json(new { ok = true, manualEnabled, largeRpmTarget = (int)largeTarget, smallRpmTarget = (int)smallTarget });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ---- Fan Curve (自定义散热曲线) ----
var _fanCurveSvc = app.Services.GetRequiredService<FanCurveService>();
_fanCurveSvc.LoadConfig(); // 启动时加载已保存的曲线

app.MapGet("/api/fan-curve/status", (FanCurveService svc) =>
{
    return Results.Json(new
    {
        ok = true,
        active = svc.Active,
        points = svc.Points.Select(p => new { temp = p.Temp, largeRpm = p.LargeRpm, smallRpm = p.SmallRpm }),
    });
});

app.MapPost("/api/fan-curve/save", (FanCurveService svc, FanCurveSaveRequest req) =>
{
    try
    {
        if (req.Points == null || req.Points.Count < 2)
            return Results.Json(new { ok = false, error = "至少需要 2 个曲线点" });
        var points = req.Points.Select(p => new FanCurvePoint(p.Temp, p.LargeRpm, p.SmallRpm)).ToList();
        svc.SetPoints(points, req.IntervalMs, req.HysteresisC);
        svc.SaveConfig();
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/fan-curve/start", (FanCurveService svc, FanCurveStartRequest? req) =>
{
    try
    {
        svc.Start(req?.IntervalMs, req?.HysteresisC);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/fan-curve/stop", (FanCurveService svc) =>
{
    try
    {
        svc.Stop();
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ---- Fan Curve Route Info (路由状态查询) ----
app.MapGet("/api/fan-curve/route-info", (FanCurveService svc) =>
{
    return Results.Json(new
    {
        ok = true,
        active = svc.Active,
        currentItsm = svc.CurrentItsm,
        routedMode = svc.RoutedMode,
        lastLargeTarget = svc.LastLargeTarget,
        lastSmallTarget = svc.LastSmallTarget,
        itsmDeviationCount = svc.ItsmDeviationCount,
        // EC 读回诊断
        actualCpuFanRpm = svc.ActualCpuFanRpm,
        actualGpuFanRpm = svc.ActualGpuFanRpm,
        ecFanTargetLarge = svc.EcFanTargetLarge,
        ecFanTargetSmall = svc.EcFanTargetSmall,
        ecFanShadowLarge = svc.EcFanShadowLarge,
        ecFanShadowSmall = svc.EcFanShadowSmall,
        lastWmiLargeOk = svc.LastWmiLargeOk,
        lastWmiSmallOk = svc.LastWmiSmallOk,
        tickCount = svc.TickCount,
        consecutiveDeviation = svc.ConsecutiveDeviation,
        cpuTemp = svc.LastCpuTemp,
        gpuTemp = svc.LastGpuTemp,
        hotspot = svc.LastHotspot,
    });
});

// ---- GPU 控制 (nvidia-smi 子进程) ----
app.MapPost("/api/gpu/set", (GpuController gpu, GpuSetRequest req, string? mode = null) =>
{
    try
    {
        Log($"[gpu/set] ← action={req.Action}, value={req.Value ?? req.Max}, min={req.Min}");
        switch (req.Action)
        {
            // 上限限制: --lock-gpu-clocks=0,value (仅传 value 时自动补 min=0)
            case "lock":
            case "lock-clocks":
                if (req.Min.HasValue || req.Max.HasValue)
                    gpu.SetLockGpuClocks(req.Min ?? 0, req.Max ?? 0);
                else
                    gpu.SetMaxGpuClock(req.Value ?? 0);
                break;
            // 精确锁定: --lock-gpu-clocks=min,min (单值锁频)
            case "lock-exact":
                gpu.SetExactGpuClock(req.Value ?? 0);
                break;
            // 上限限制 (显式): --lock-gpu-clocks=0,max
            case "limit":
            case "limit-max":
                gpu.SetMaxGpuClock(req.Value ?? req.Max ?? 0);
                break;
            // 重置核心频率
            case "reset":
            case "reset-clocks":
                gpu.ResetGpuClocks();
                break;
            // 显存区间锁定
            case "lock-memory":
            case "lock-memory-clocks":
                gpu.SetLockMemoryClocks(req.Min ?? 0, req.Max ?? 0);
                break;
            // 显存上限限制
            case "limit-memory":
                gpu.SetMaxMemoryClock(req.Value ?? req.Max ?? 0);
                break;
            // 重置显存频率
            case "reset-memory":
            case "reset-memory-clocks":
                gpu.ResetMemoryClocks();
                break;
            default:
                return Results.Json(new { ok = false, error = "unknown action: " + req.Action });
        }
        // 持久化 GPU 控制设置 (nvidia-smi 路径)
        SavePerfOverrides(o =>
        {
            switch (req.Action)
            {
                case "limit-max" or "limit":
                    o.Gpu.CoreFreqMhz = req.Value ?? req.Max ?? 0;
                    if (o.Gpu.FreqLocked != true) { /* 未锁定时不改变 locked 状态 */ }
                    break;
                case "lock-exact":
                    o.Gpu.CoreFreqMhz = req.Value ?? 0;
                    o.Gpu.FreqLocked = true;
                    break;
                case "reset-clocks" or "reset":
                    o.Gpu.CoreFreqMhz = null;
                    o.Gpu.FreqLocked = null;
                    break;
                case "limit-memory":
                    // 前端传绝对值 9001/11001/12001，转换为 1/2/3 档位
                    var memMap = new Dictionary<int, int> { [9001] = 1, [11001] = 2, [12001] = 3 };
                    var val = req.Value ?? req.Max ?? 0;
                    o.Gpu.MemFreqLevel = memMap.TryGetValue(val, out var lvl) ? lvl : 0;
                    break;
                case "reset-memory-clocks" or "reset-memory":
                    o.Gpu.MemFreqLevel = null;
                    break;
            }
        }, mode);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
app.MapGet("/api/gpu/status", (GpuController gpu) =>
{
    try
    {
        var info = gpu.GetClockInfo();
        var baseClock = gpu.GetBaseClock();
        var maxClock = gpu.GetMaxClock();
        return Results.Json(new { ok = true, coreClockMHz = info.CoreClockMHz, memoryClockMHz = info.MemoryClockMHz, powerDrawW = info.PowerDrawW, baseCoreClockMHz = baseClock, maxCoreClockMHz = maxClock });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ---- NVAPI GPU 控制 (超频/降频/功率/温度) ----
var nvapi = app.Services.GetRequiredService<NvapiGpuController>();
if (!nvapi.Init())
    Log("[NVAPI] 未初始化，超频/功率/温度控制不可用");

// ---- 启动诊断信息 ----
try
{
    var gpuCtrl = app.Services.GetRequiredService<GpuController>();
    var gpuName = nvapi.GpuName;
    var ocEngine = nvapi.OcEngine;
    var driverVer = "";
    try { driverVer = gpuCtrl.GetDriverVersion(); } catch { }
    var gpuMode = wmi.Available ? wmi.GetGpuMode().ToString() : "N/A";
    Log($"[Startup] GPU: {gpuName} | Driver: {driverVer} | Mode: {gpuMode} | OC: {ocEngine}");
}
catch (Exception ex)
{
    Log($"[Startup] Diagnostic log failed: {ex.Message}");
}

app.MapGet("/api/nvapi/status", (NvapiGpuController nv) =>
{
    var s = nv.GetStatus();
    return Results.Json(new {
        ok = s.Available, gpuName = s.GpuName, overclockSupported = s.OverclockSupported, ocEngine = s.OcEngine,
        coreMhz = s.CoreMhz, memMhz = s.MemMhz,
        coreOffsetMhz = s.CoreOffsetMhz, memOffsetMhz = s.MemOffsetMhz,
        powerLimitMw = s.PowerLimitMw, powerMinMw = s.PowerMinMw,
        powerMaxMw = s.PowerMaxMw, powerDefaultMw = s.PowerDefaultMw,
        thermalLimitC = s.ThermalLimitC, thermalMinC = s.ThermalMinC,
        thermalMaxC = s.ThermalMaxC, thermalDefaultC = s.ThermalDefaultC
    });
});

app.MapGet("/api/nvapi/dump-pstates", (NvapiGpuController nv) =>
    Results.Text(nv.DumpPStates(), "text/plain"));

app.MapPost("/api/nvapi/overclock", (NvapiGpuController nv, NvapiOverclockRequest req, string? mode = null) =>
{
    Log($"[nvapi/overclock] ← core={req.CoreOffsetMhz}, mem={req.MemOffsetMhz}");
    if (!nv.IsAvailable) return Results.Json(new { ok = false, error = "NVAPI not available" });
    var rc = nv.SetP0Offset(req.CoreOffsetMhz, req.MemOffsetMhz);
    SavePerfOverrides(o => { o.Nvapi.OcCoreOffsetMhz = req.CoreOffsetMhz; o.Nvapi.OcMemOffsetMhz = req.MemOffsetMhz; }, mode);
    return Results.Json(new { ok = rc == 0, rc });
});

app.MapPost("/api/nvapi/power-limit", (NvapiGpuController nv, NvapiPowerLimitRequest req, string? mode = null) =>
{
    if (!nv.IsAvailable) return Results.Json(new { ok = false, error = "NVAPI not available" });
    var rc = nv.SetPowerLimit((uint)(req.PowerW * 1000)); // W → mW
    SavePerfOverrides(o => o.Nvapi.PowerLimitW = req.PowerW, mode);
    return Results.Json(new { ok = rc == 0, rc });
});

app.MapPost("/api/nvapi/thermal-limit", (NvapiGpuController nv, NvapiThermalLimitRequest req, string? mode = null) =>
{
    Log($"[nvapi/thermal-limit] ← temp={req.TempC}°C");
    if (!nv.IsAvailable) return Results.Json(new { ok = false, error = "NVAPI not available" });
    var rc = nv.SetThermalLimit(req.TempC);
    SavePerfOverrides(o => o.Nvapi.ThermalLimitC = req.TempC, mode);
    return Results.Json(new { ok = rc == 0, rc });
});

// ---- CPU 性能控制 (powercfg 电源计划 API) ----
app.MapGet("/api/cpu/status", (CpuPowerController cpu) =>
{
    try
    {
        var s = cpu.GetStatus();
        return Results.Json(new {
            ok = s.Available,
            turboEnabled = s.TurboEnabled,
            coreLimitPercent = s.CoreLimitPercent,
            freqLimitMhz = s.FreqLimitMhz
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/cpu/freq-limit", async (CpuPowerController cpu, CpuFreqLimitRequest req, string? mode = null) =>
{
    try
    {
        Log($"[cpu/freq-limit] ← mhz={req.Mhz}");
        await cpu.SetFreqLimitAsync(req.Mhz);
        SavePerfOverrides(o => o.Cpu.FreqLimitMhz = req.Mhz, mode);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        Log($"[cpu/freq-limit] ✗ {ex.Message}");
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/cpu/turbo", async (CpuPowerController cpu, CpuTurboRequest req, string? mode = null) =>
{
    try
    {
        Log($"[cpu/turbo] ← enabled={req.Enabled}");
        await cpu.SetTurboAsync(req.Enabled);
        SavePerfOverrides(o => o.Cpu.TurboEnabled = req.Enabled, mode);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        Log($"[cpu/turbo] ✗ {ex.Message}");
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/cpu/core-limit", async (CpuPowerController cpu, CpuCoreLimitRequest req, string? mode = null) =>
{
    try
    {
        Log($"[cpu/core-limit] ← percent={req.Percent}");
        await cpu.SetCoreLimitAsync(req.Percent);
        SavePerfOverrides(o => o.Cpu.CoreLimitPercent = req.Percent, mode);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        Log($"[cpu/core-limit] ✗ {ex.Message}");
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/cpu/reset", async (CpuPowerController cpu, string? mode = null) =>
{
    try
    {
        await cpu.ResetAllAsync();
        SavePerfOverrides(o => { o.Cpu = new CpuOverrides(); }, mode);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ---- Overrides API (前端启动/模式切换/恢复默认) ----
app.MapGet("/api/overrides", () =>
{
    var mode = CurrentMode();
    var overrides = LoadPerfOverrides();
    return Results.Json(new { mode, overrides });
});

app.MapPost("/api/overrides/switch", async (SwitchModeRequest req) =>
{
    Log($"[overrides/switch] ← mode={req.Mode}");
    SetCurrentMode(req.Mode);
    ApplyThermalMode(req.Mode);
    await System.Threading.Tasks.Task.Delay(500); // 等 EC 完成模式预设加载

    var overrides = LoadPerfOverrides();

    // 非覆盖通道: 直接调用硬件控制器重置（绕过 SavePerfOverrides，避免并发模式切换写错文件）
    var gpuCtrl = app.Services.GetRequiredService<GpuController>();
    var nvCtrl = app.Services.GetRequiredService<NvapiGpuController>();

    // GPU 核心/显存时钟: 先清理再让 RestoreComputeSettings 重新应用
    if (!overrides.Gpu.CoreFreqMhz.HasValue && !overrides.Gpu.FreqLocked.HasValue)
    {
        try { gpuCtrl.ResetGpuClocks(); } catch { }
    }
    if (!overrides.Gpu.MemFreqLevel.HasValue)
    {
        try { gpuCtrl.ResetMemoryClocks(); } catch { }
    }

    // NVAPI: 超频偏移和温度限制恢复默认
    if (!overrides.Nvapi.OcCoreOffsetMhz.HasValue && !overrides.Nvapi.OcMemOffsetMhz.HasValue)
    {
        try { nvCtrl.SetP0Offset(0, 0); } catch { }
    }
    if (!overrides.Nvapi.ThermalLimitC.HasValue)
    {
        try { nvCtrl.SetThermalLimit(87); } catch { }
    }

    // CPU 功率配置: 无覆盖时恢复默认（直接写文件，绕过 ResetAllAsync 的 SavePerfOverrides 竞争）
    if (!overrides.Cpu.FreqLimitMhz.HasValue && !overrides.Cpu.TurboEnabled.HasValue && !overrides.Cpu.CoreLimitPercent.HasValue)
    {
        var cpu = app.Services.GetRequiredService<CpuPowerController>();
        try { await cpu.SetFreqLimitAsync(0); } catch { }
        try { await cpu.SetTurboAsync(true); } catch { }
        try { await cpu.SetCoreLimitAsync(100); } catch { }
        // 直接写入新模式文件（CurrentMode 已切换，不受并发 setter 影响）
        lock (_perfLock)
        {
            var file = $"overrides-{req.Mode}.json";
            var o = JsonRead<PerformanceOverrides>(file, new PerformanceOverrides());
            o.Cpu.FreqLimitMhz = null; o.Cpu.TurboEnabled = null; o.Cpu.CoreLimitPercent = null;
            JsonWrite(file, o);
        }
    }

    // 电源计划: 无覆盖时恢复平衡
    if (!overrides.PowerPlan.HasValue)
    {
        try { app.Services.GetRequiredService<HardwareAbstractionLayer>().PowerPlan = 0; } catch { }
    }

    // 应用新模式的全部覆盖设置（CPU/SMU/GPU/NVAPI/电源计划 + 风扇）
    await RestoreAllPerfSettings("switch");

    return Results.Json(new { overrides });
});

app.MapPost("/api/overrides/sync", (SyncOverridesRequest req) =>
{
    Log($"[overrides/sync] ← mode={req.Mode}, clearing overrides");
    var file = $"overrides-{req.Mode}.json";
    lock (_perfLock) JsonWrite(file, new PerformanceOverrides());
    return Results.Ok();
});

app.MapPost("/api/overrides/import", (SyncOverridesRequest req) =>
{
    if (req.Overrides == null) return Results.BadRequest("overrides required");
    var file = $"overrides-{req.Mode}.json";
    lock (_perfLock) JsonWrite(file, req.Overrides);
    Log($"[overrides/import] ← mode={req.Mode}, imported from localStorage migration");
    return Results.Ok();
});

app.MapPost("/api/log", (FrontendLogRequest req) =>
{
    AppLog.Write("UI", $"[{req.Tag}] {req.Msg}");
    return Results.Ok();
});

app.MapPost("/api/wmi/cmd", (WmiInterface wmi, WmiCmdRequest req) =>
{
    try
    {
        byte? value = req.Value.HasValue ? (byte?)req.Value.Value : null;
        var result = wmi.SendRawCommand((byte)req.Method, value);
        var outVal = result.Length > 4 ? (int?)result[4] : null;
        var hexResp = string.Join(" ", result.Take(8).Select(b => b.ToString("X2")));
        return Results.Json(new { ok = true, method = req.Method, value = req.Value, response = hexResp, outValue = outVal });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
// ---- 日志导出（供用户反馈问题时使用）----
app.MapGet("/api/logs/export", () =>
{
    var logFile = Path.Combine(_logDir, "app.log");
    if (!File.Exists(logFile))
        return Results.Json(new { ok = false, error = "日志文件不存在" });
    var bytes = File.ReadAllBytes(logFile);
    var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    return Results.File(bytes, "text/plain; charset=utf-8", $"douzhanzhe-log-{ts}.log");
});

app.MapGet("/api/ui-state", () =>
{
    return Results.Json(JsonRead<UiState>("ui-state.json", new UiState()));
});
app.MapPost("/api/ui-state", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var readOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = JsonSerializer.Deserialize<UiState>(await reader.ReadToEndAsync(), readOpts);
        JsonWrite("ui-state.json", body ?? new UiState());
        return Results.Json(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapGet("/api/default-config", () =>
{
    return Results.Json(JsonRead<DefaultConfig>("dashboard-default.json", new DefaultConfig()));
});
app.MapPost("/api/default-config", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var readOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = JsonSerializer.Deserialize<DefaultConfig>(await reader.ReadToEndAsync(), readOpts);
        JsonWrite("dashboard-default.json", body ?? new DefaultConfig());
        return Results.Json(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// ---- Auto-start options (minimized preference + enabled cache) ----
var autoStartOptsPath = Path.Combine(AppContext.BaseDirectory, "config", "auto-start-opts.json");
Directory.CreateDirectory(Path.GetDirectoryName(autoStartOptsPath)!);

// 读取本地缓存的 auto-start 状态（快速路径，无 COM 开销）
(bool enabled, bool minimized) ReadAutoStartOpts()
{
    try
    {
        if (File.Exists(autoStartOptsPath))
        {
            var json = File.ReadAllText(autoStartOptsPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var en = root.TryGetProperty("enabled", out var ev) && ev.ValueKind == JsonValueKind.True;
            var min = root.TryGetProperty("minimized", out var mv) && mv.ValueKind == JsonValueKind.True;
            return (en, min);
        }
    }
    catch { }
    return (false, false);
}

void WriteAutoStartOpts(bool enabled, bool minimized)
{
    File.WriteAllText(autoStartOptsPath, JsonSerializer.Serialize(new { enabled, minimized }));
}

app.MapGet("/api/auto-start-opts", () =>
{
    var (_, minimized) = ReadAutoStartOpts();
    return Results.Json(new { minimized });
});
app.MapPost("/api/auto-start-opts", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
        if (body == null || !body.TryGetValue("minimized", out var v) || v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)
            return Results.Json(new { ok = false, error = "需要 { minimized: bool }" });
        var minimized = v.GetBoolean();
        var (enabled, _) = ReadAutoStartOpts();
        WriteAutoStartOpts(enabled, minimized);
        return Results.Json(new { ok = true, minimized });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// ---- Auto-start (Windows Task Scheduler) ----
app.MapGet("/api/auto-start", () =>
{
    try
    {
        // 快速路径：先读本地缓存，立即返回
        var (cachedEnabled, _) = ReadAutoStartOpts();

        // 后台异步校验：查计划任务，不一致则修正缓存
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // 等 2 秒再查，避免安装后 Task Scheduler 尚未注册完毕导致误判
                Thread.Sleep(2000);
                using var ts = new TaskService();
                var actual = ts.RootFolder.AllTasks.Any(t => t.Name == "DouzhanzheControl");
                // 二次确认：若缓存为 true 但首次未找到，再等 2 秒重试
                if (!actual && cachedEnabled)
                {
                    Thread.Sleep(2000);
                    actual = ts.RootFolder.AllTasks.Any(t => t.Name == "DouzhanzheControl");
                }
                var (curEnabled, min) = ReadAutoStartOpts();
                if (actual != curEnabled)
                    WriteAutoStartOpts(actual, min);
            }
            catch { /* 校验失败不影响本次响应 */ }
        });

        return Results.Json(new { enabled = cachedEnabled });
    }
    catch { return Results.Json(new { enabled = false }); }
});
app.MapPost("/api/auto-start", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
        if (body == null || !body.TryGetValue("enabled", out var enabledEl) || enabledEl.ValueKind != JsonValueKind.True && enabledEl.ValueKind != JsonValueKind.False)
            return Results.Json(new { ok = false, error = "需要 { enabled: bool }" });
        var enabled = enabledEl.GetBoolean();

        using var ts = new TaskService();
        if (enabled)
        {
            // 定位 Shell.exe：同目录下查找，或 dev 路径回退
            var apiDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            var shellExe = new[] { "Douzhanzhe.Shell.exe" }
                .Select(f => Path.Combine(apiDir, f))
                .FirstOrDefault(File.Exists);
            if (shellExe == null)
            {
                // 开发环境路径回退
                shellExe = Path.GetFullPath(Path.Combine(apiDir, "..", "..", "..", "..", "shell", "Douzhanzhe.Shell", "bin", "Debug", "net8.0-windows", "Douzhanzhe.Shell.exe"));
            }

            // 读取最小化偏好
            var (_, minimized) = ReadAutoStartOpts();

            var td = ts.NewTask();
            td.RegistrationInfo.Description = "Douzhanzhe Console 开机自启";
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartOnRemoteAppSession = false;
            td.Triggers.Add(new LogonTrigger());
            td.Actions.Add(shellExe, minimized ? "--minimized" : "");
            ts.RootFolder.RegisterTaskDefinition("DouzhanzheControl", td);
        }
        else
        {
            if (ts.RootFolder.AllTasks.Any(t => t.Name == "DouzhanzheControl"))
                ts.RootFolder.DeleteTask("DouzhanzheControl");
        }

        // 同步写入本地缓存
        var (_, min) = ReadAutoStartOpts();
        WriteAutoStartOpts(enabled, min);

        return Results.Json(new { ok = true, enabled });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// ---- Custom background image ----
var bgOptsPath = Path.Combine(configDir, "background-opts.json");
// 只匹配图片文件，排除 background-opts.json / background.json 等
string[] BgImageFiles() => Directory.GetFiles(configDir, "background.*")
    .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
             || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
             || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
             || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
    .ToArray();

app.MapGet("/api/background-opts", () =>
{
    try
    {
        if (File.Exists(bgOptsPath))
        {
            var json = File.ReadAllText(bgOptsPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var enabled = root.TryGetProperty("enabled", out var ev) && ev.ValueKind == JsonValueKind.True;
            var opacity = root.TryGetProperty("opacity", out var ov) ? Math.Clamp(ov.GetInt32(), 0, 100) : 50;
            var maskColor = root.TryGetProperty("maskColor", out var mv) && mv.GetString() == "white" ? "white" : "black";
            var hasImage = BgImageFiles().Length > 0;
            return Results.Json(new { enabled, opacity, maskColor, hasImage });
        }
        return Results.Json(new { enabled = false, opacity = 50, maskColor = "black", hasImage = false });
    }
    catch { return Results.Json(new { enabled = false, opacity = 50, maskColor = "black", hasImage = false }); }
});

app.MapPost("/api/background-opts", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
        if (body == null) return Results.Json(new { ok = false, error = "无效请求" });

        // 读取当前配置
        bool enabled = false; int opacity = 50; string maskColor = "black";
        if (File.Exists(bgOptsPath))
        {
            try
            {
                var old = JsonDocument.Parse(File.ReadAllText(bgOptsPath)).RootElement;
                enabled = old.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                opacity = old.TryGetProperty("opacity", out var o) ? o.GetInt32() : 50;
                maskColor = old.TryGetProperty("maskColor", out var m) && m.GetString() == "white" ? "white" : "black";
            }
            catch { }
        }

        if (body.TryGetValue("enabled", out var ev)) enabled = ev.ValueKind == JsonValueKind.True;
        if (body.TryGetValue("opacity", out var ov)) opacity = Math.Clamp(ov.GetInt32(), 0, 100);
        if (body.TryGetValue("maskColor", out var mv)) maskColor = mv.GetString() == "white" ? "white" : "black";

        File.WriteAllText(bgOptsPath, JsonSerializer.Serialize(new { enabled, opacity, maskColor }));
        return Results.Json(new { ok = true, enabled, opacity, maskColor });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

app.MapPost("/api/background", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
        if (body == null || !body.TryGetValue("image", out var imgEl))
            return Results.Json(new { ok = false, error = "需要 { image: base64dataUrl }" });

        var dataUrl = imgEl.GetString() ?? "";
        // 解析 data URL: "data:image/png;base64,xxxx"
        var commaIdx = dataUrl.IndexOf(',');
        if (commaIdx < 0) return Results.Json(new { ok = false, error = "无效的图片数据" });

        var meta = dataUrl[..commaIdx];
        var b64 = dataUrl[(commaIdx + 1)..];
        var ext = "png";
        if (meta.Contains("jpeg") || meta.Contains("jpg")) ext = "jpg";
        else if (meta.Contains("webp")) ext = "webp";

        // 清理旧的背景图片（只删图片文件，不碰 JSON 配置）
        foreach (var old in BgImageFiles())
        {
            try { File.Delete(old); }
            catch { /* 忽略被占用的文件，写入时会被覆盖 */ }
        }

        var filePath = Path.Combine(configDir, $"background.{ext}");
        var tmpPath = filePath + ".tmp";
        await File.WriteAllBytesAsync(tmpPath, Convert.FromBase64String(b64));
        // 原子替换：先写临时文件，再重命名
        if (File.Exists(filePath)) File.Delete(filePath);
        File.Move(tmpPath, filePath);
        return Results.Json(new { ok = true, ext });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

app.MapGet("/api/background", async (HttpContext ctx) =>
{
    try
    {
        var files = BgImageFiles();
        if (files.Length == 0) return Results.NotFound();

        var filePath = files[0];
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };

        // HEAD 请求：Results.File(byte[]) 对 HEAD 不兼容，直接返回 200
        if (ctx.Request.Method == "HEAD")
            return Results.Ok();

        // 读入内存以释放文件句柄，避免与上传操作冲突
        var bytes = await File.ReadAllBytesAsync(filePath);
        return Results.File(bytes, contentType);
    }
    catch { return Results.StatusCode(500); }
});

app.MapDelete("/api/background", () =>
{
    try
    {
        foreach (var f in BgImageFiles())
            File.Delete(f);
        // 同时禁用
        int opacity = 50; string maskColor = "black";
        if (File.Exists(bgOptsPath))
        {
            try
            {
                var old = JsonDocument.Parse(File.ReadAllText(bgOptsPath)).RootElement;
                opacity = old.TryGetProperty("opacity", out var o) ? o.GetInt32() : 50;
                maskColor = old.TryGetProperty("maskColor", out var m) && m.GetString() == "white" ? "white" : "black";
            }
            catch { }
        }
        File.WriteAllText(bgOptsPath, JsonSerializer.Serialize(new { enabled = false, opacity, maskColor }));
        return Results.Json(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// ---- 检查更新 (GitHub Releases API) ----
var _updateHttpClient = new HttpClient();
_updateHttpClient.Timeout = TimeSpan.FromSeconds(8);
_updateHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DouzhanzheConsole-UpdateChecker/1.0");

// 从前端 JS bundle 提取版本号（构建时 SettingsPanel.jsx 中的 "Douzhanzhe Console vX.Y.Z"）
// 注意：覆盖安装时 wwwroot/assets 可能残留多个旧 bundle，必须遍历所有文件取最大版本号
var _appVersion = "0.0.0";
try
{
    var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "assets");
    if (Directory.Exists(wwwroot))
    {
        var jsFiles = Directory.GetFiles(wwwroot, "index-*.js");
        var maxVer = new Version(0, 0, 0);
        foreach (var jsFile in jsFiles)
        {
            try
            {
                var jsContent = File.ReadAllText(jsFile);
                var m = System.Text.RegularExpressions.Regex.Match(jsContent, @"Douzhanzhe Console v(\d+\.\d+\.\d+)");
                if (m.Success && Version.TryParse(m.Groups[1].Value, out var v) && v > maxVer)
                    maxVer = v;
            }
            catch { /* 单个文件读取失败不影响其他 */ }
        }
        if (maxVer > new Version(0, 0, 0))
            _appVersion = maxVer.ToString();
    }
}
catch { /* 读取失败时使用默认值 */ }
Log($"Version: {_appVersion}");

app.MapGet("/api/update/check", async () =>
{
    try
    {
        var CurrentVersion = _appVersion;
        var res = await _updateHttpClient.GetAsync(
            "https://api.github.com/repos/KanzakiK/DOUZHANZHE-Control/releases/latest");

        // 无 release (404) 或网络故障 → 视为无更新
        if (!res.IsSuccessStatusCode)
            return Results.Json(new { available = false, currentVersion = CurrentVersion,
                reason = "无法获取发布信息" });

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latestVersion = tag.TrimStart('v');
        var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        var publishedAt = root.TryGetProperty("published_at", out var p) ? p.GetString() : null;
        var htmlUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;

        var isNewer = false;
        if (Version.TryParse(latestVersion, out var latest) &&
            Version.TryParse(CurrentVersion, out var current))
        {
            isNewer = latest > current;
        }

        return Results.Json(new
        {
            available = isNewer,
            currentVersion = CurrentVersion,
            latestVersion,
            body,
            publishedAt,
            url = htmlUrl
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { available = false, currentVersion = _appVersion,
            error = ex.Message });
    }
});

// ---- 预启动 inpoutx64 内核驱动 ----
// 必须在任何 inpoutx64.dll 的 DllImport 调用之前执行！
// 原因：inpoutx64.dll 的 DllMain 在首次加载时会尝试打开 \\.\InpOut64 设备，
//       如果此时服务未运行，内部变量 bInpOutDriverOpened 会被永久设为 false，
//       即使之后启动了服务，IsInpOutDriverOpen() 也永远返回 false。
try
{
    var inpCheck = Process.Start(new ProcessStartInfo("sc.exe", "query inpoutx64") { UseShellExecute = false, CreateNoWindow = true });
    inpCheck?.WaitForExit(2000);
    if (inpCheck?.ExitCode != 0)
    {
        Log("[inpoutx64] 驱动未运行，尝试启动...");
        // 确保启动类型为 AUTO_START（下次开机自动加载）
        var cfgSvc = Process.Start(new ProcessStartInfo("sc.exe", "config inpoutx64 start=auto") { UseShellExecute = false, CreateNoWindow = true });
        cfgSvc?.WaitForExit(2000);
        // 立即启动驱动服务
        var startSvc = Process.Start(new ProcessStartInfo("sc.exe", "start inpoutx64") { UseShellExecute = false, CreateNoWindow = true });
        startSvc?.WaitForExit(3000);
        // 验证
        var verify = Process.Start(new ProcessStartInfo("sc.exe", "query inpoutx64") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        if (verify != null)
        {
            var outText = verify.StandardOutput.ReadToEnd();
            verify.WaitForExit(1000);
            if (outText.Contains("RUNNING"))
                Log("[inpoutx64] 驱动启动成功");
            else
                Log("[inpoutx64] 驱动启动可能失败: " + outText.Trim());
        }
    }
    else Log("[inpoutx64] 驱动已在运行");
}
catch (Exception ex) { Log("[inpoutx64] 预启动异常: " + ex.Message); }

// ---- Auto-load WinRing0 kernel driver for SMU ----
try
{
    var svcName = "WinRing0_1_2_0";
    var sysPath = Path.Combine(AppContext.BaseDirectory, "WinRing0x64.sys");
    if (File.Exists(sysPath))
    {
        var check = Process.Start(new ProcessStartInfo("sc.exe", "query " + svcName) { UseShellExecute = false, CreateNoWindow = true });
        check?.WaitForExit(2000);
        if (check?.ExitCode != 0)
        {
            Log("[WinRing0] Driver not loaded, attempting to install...");
            var create = Process.Start(new ProcessStartInfo("sc.exe", $"create {svcName} type=kernel start=demand binPath=\"{sysPath}\"") { UseShellExecute = false, CreateNoWindow = true });
            create?.WaitForExit(2000);
            var start = Process.Start(new ProcessStartInfo("sc.exe", "start " + svcName) { UseShellExecute = false, CreateNoWindow = true });
            start?.WaitForExit(2000);
            var verify = Process.Start(new ProcessStartInfo("sc.exe", "query " + svcName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (verify != null)
            {
                var outText = verify.StandardOutput.ReadToEnd();
                verify.WaitForExit(1000);
                if (outText.Contains("RUNNING"))
                    Log("[WinRing0] Driver loaded OK");
                else
                    Log("[WinRing0] Driver load FAILED - SMU control unavailable");
            }
        }
        else Log("[WinRing0] Driver already loaded");
    }
    else Log("[WinRing0] WinRing0x64.sys not found at " + sysPath);
}
catch (Exception ex) { Log("[WinRing0] Error: " + ex.Message); }

// ---- LHM 初始化（WinRing0 加载之后）----
LhmSensor.Open();

// ---- DriverBridge 冷启动重试（安全网） ----
// 正常情况下 inpoutx64 已在预启动阶段启动，这里只是兜底
if (!DriverBridge.Instance.Ready)
{
    Log("[DriverBridge] inpoutx64 首次初始化未成功，尝试最后补救...");
    try
    {
        var startSvc = Process.Start(new ProcessStartInfo("sc.exe", "start inpoutx64") { UseShellExecute = false, CreateNoWindow = true });
        startSvc?.WaitForExit(3000);
    }
    catch (Exception ex) { Log($"[DriverBridge] inpoutx64 补救异常: {ex.Message}"); }
    Log("[DriverBridge] 重试初始化，等待最多 5 秒...");
    DriverBridge.Instance.RetryInit(5000);
    Log($"[DriverBridge] 重试结果: Ready={DriverBridge.Instance.Ready}");
}

// ---- OSD API ----
app.MapPost("/api/osd/show", (OsdShowRequest req, OsdService osd) =>
{
    if (!string.IsNullOrWhiteSpace(req.Text))
        osd.Show(req.Text);
    return Results.Ok();
});

// ---- Game Profiles API ----
app.MapGet("/api/game-profiles", (GameProfileService svc) =>
{
    return Results.Json(new
    {
        enabled = svc.Enabled,
        defaultMode = svc.DefaultMode,
        profiles = svc.GetAll()
    });
});

app.MapPost("/api/game-profiles", (GameProfileRequest req, GameProfileService svc) =>
{
    try
    {
        var profile = new GameProfile
        {
            Name = req.Name ?? "",
            ExePath = req.ExePath ?? "",
            ExeName = req.ExeName ?? Path.GetFileName(req.ExePath ?? ""),
            TargetMode = req.TargetMode ?? svc.DefaultMode,
            Enabled = req.Enabled ?? true,
            Source = req.Source ?? "manual"
        };
        var created = svc.Add(profile);
        return Results.Created($"/api/game-profiles/{created.Id}", created);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPut("/api/game-profiles/{id}", (string id, GameProfileRequest req, GameProfileService svc) =>
{
    try
    {
        var existing = svc.GetById(id);
        if (existing == null)
            return Results.NotFound(new { error = "规则不存在" });

        var updated = svc.Update(id, new GameProfile
        {
            Id = id,
            Name = req.Name ?? existing.Name,
            ExePath = req.ExePath ?? existing.ExePath,
            ExeName = req.ExeName ?? existing.ExeName,
            TargetMode = req.TargetMode ?? existing.TargetMode,
            Enabled = req.Enabled ?? existing.Enabled,
            Source = req.Source ?? existing.Source
        });
        return Results.Ok(updated);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/game-profiles/{id}", (string id, GameProfileService svc) =>
{
    svc.Delete(id);
    return Results.Ok();
});

app.MapPut("/api/game-profiles/config", (GameConfigRequest req, GameProfileService svc) =>
{
    svc.UpdateConfig(req.Enabled, req.DefaultMode);
    return Results.Ok(new { enabled = svc.Enabled, defaultMode = svc.DefaultMode });
});

app.MapGet("/api/game-profiles/status", (ProcessMonitorService svc) =>
{
    return Results.Json(svc.GetStatus());
});

app.MapGet("/api/game-profiles/file-pick", () =>
{
    // 使用 Windows 文件选择对话框
    var ofd = new System.Windows.Forms.OpenFileDialog
    {
        Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        Title = "选择游戏主程序",
        Multiselect = false
    };

    // 需要在 STA 线程上运行
    string? result = null;
    var thread = new Thread(() =>
    {
        if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            result = ofd.FileName;
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (result == null)
        return Results.Ok(new { selected = false, path = (string?)null, name = (string?)null });

    var fileName = Path.GetFileNameWithoutExtension(result);
    return Results.Ok(new { selected = true, path = result, name = fileName });
});

// 扫描已安装游戏（Steam + Epic）
app.MapGet("/api/game-profiles/scan", (GameProfileService profiles) =>
{
    var results = GameScannerService.Scan(profiles);
    return Results.Json(results);
});

// 批量添加游戏
app.MapPost("/api/game-profiles/batch", async (HttpRequest req, GameProfileService profiles) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    if (!body.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "games array required" });

    int added = 0;
    foreach (var g in games.EnumerateArray())
    {
        var name = g.TryGetProperty("name", out var n) ? n.GetString() : null;
        var exePath = g.TryGetProperty("exePath", out var ep) ? ep.GetString() : null;
        var targetMode = g.TryGetProperty("targetMode", out var tm) ? tm.GetString() : "gaming";
        var source = g.TryGetProperty("source", out var src) ? src.GetString() : "scan";

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exePath)) continue;

        var exeName = Path.GetFileName(exePath);
        try
        {
            profiles.Add(new GameProfile
            {
                Name = name,
                ExeName = exeName,
                ExePath = exePath,
                TargetMode = targetMode,
                Source = source,
                Enabled = true
            });
            added++;
        }
        catch { }
    }

    return Results.Ok(new { added });
});

// ---- Start server ----
try
{
    Log("Starting server on http://127.0.0.1:3100");
    app.Run();
}
catch (Exception ex)
{
    Log($"[FATAL] Server failed to start: {ex.GetType().Name}: {ex.Message}");
    Log($"  StackTrace: {ex.StackTrace}");
    throw;
}
public record WmiCmdRequest(int Method, int? Value);
public record GpuSetRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("action")] string Action,
    [property: System.Text.Json.Serialization.JsonPropertyName("min")] int? Min,
    [property: System.Text.Json.Serialization.JsonPropertyName("max")] int? Max,
    [property: System.Text.Json.Serialization.JsonPropertyName("value")] int? Value
);
record ControlRequest(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("value")] int Value
);
record OsdShowRequest([property: JsonPropertyName("text")] string Text);
record SmuSetRequest(string Parameter, int ValueM);
public record FanSetRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("largeRpm")] int? LargeRpm,
    [property: System.Text.Json.Serialization.JsonPropertyName("smallRpm")] int? SmallRpm
);
public record FanTestWriteRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("strategy")] string? Strategy,
    [property: System.Text.Json.Serialization.JsonPropertyName("largeRpm")] int? LargeRpm,
    [property: System.Text.Json.Serialization.JsonPropertyName("smallRpm")] int? SmallRpm
);
// ---- NVAPI 请求模型 ----
public record NvapiOverclockRequest(
    [property: JsonPropertyName("coreOffsetMhz")] int CoreOffsetMhz,
    [property: JsonPropertyName("memOffsetMhz")] int MemOffsetMhz
);
public record NvapiPowerLimitRequest(
    [property: JsonPropertyName("powerW")] int PowerW
);
public record NvapiThermalLimitRequest(
    [property: JsonPropertyName("tempC")] float TempC
);
// ---- CPU 性能控制请求模型 ----
public record CpuFreqLimitRequest(
    [property: JsonPropertyName("mhz")] int Mhz  // 0 = 取消限制
);
public record CpuTurboRequest(
    [property: JsonPropertyName("enabled")] bool Enabled
);
public record CpuCoreLimitRequest(
    [property: JsonPropertyName("percent")] int Percent  // 0-100
);
public record UiState(string[]? CardOrder, string[]? HiddenCards)
{
    public UiState() : this(null, null) { }
    public string[] CardOrder { get; init; } = CardOrder ?? Array.Empty<string>();
    public string[] HiddenCards { get; init; } = HiddenCards ?? Array.Empty<string>();
}
public record DefaultConfig(string[]? Order, string[]? Hidden)
{
    public DefaultConfig() : this(null, null) { }
    public string[] Order { get; init; } = Order ?? Array.Empty<string>();
    public string[] Hidden { get; init; } = Hidden ?? Array.Empty<string>();
}

// ---- Fan Curve 请求模型 ----
public record FanCurveSaveRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("points")] List<FanCurvePointDto>? Points,
    [property: System.Text.Json.Serialization.JsonPropertyName("intervalMs")] int? IntervalMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("hysteresisC")] int? HysteresisC
);
public record FanCurvePointDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("temp")] int Temp,
    [property: System.Text.Json.Serialization.JsonPropertyName("largeRpm")] int LargeRpm,
    [property: System.Text.Json.Serialization.JsonPropertyName("smallRpm")] int SmallRpm
);
public record FanCurveStartRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("intervalMs")] int? IntervalMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("hysteresisC")] int? HysteresisC
);

// ---- 性能设置持久化模型 ----
public class CpuOverrides { public int? FreqLimitMhz; public bool? TurboEnabled; public int? CoreLimitPercent; }
public class GpuOverrides { public int? CoreFreqMhz; public bool? FreqLocked; public int? MemFreqLevel; }
public class NvapiOverrides { public int? OcCoreOffsetMhz; public int? OcMemOffsetMhz; public int? PowerLimitW; public float? ThermalLimitC; }
public class SmuOverrides { public int? StapmLimitW; public int? ShortPowerLimitW; public int? TempLimitC; public int? CoAll; }
public class FanOverrides { public int? LargeRpm; public int? SmallRpm; }
public class PerformanceOverrides { public CpuOverrides Cpu = new(); public GpuOverrides Gpu = new(); public NvapiOverrides Nvapi = new(); public SmuOverrides Smu = new(); public FanOverrides Fan = new(); public int? PowerPlan; }
public record SwitchModeRequest([property: JsonPropertyName("mode")] string Mode);
public record SyncOverridesRequest([property: JsonPropertyName("mode")] string Mode, [property: JsonPropertyName("overrides")] PerformanceOverrides? Overrides);
public record FrontendLogRequest([property: JsonPropertyName("tag")] string Tag, [property: JsonPropertyName("msg")] string Msg);

// ---- 快捷键请求模型 ----
public record HotkeyConfigRequest(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("modifiers")] string? Modifiers,
    [property: JsonPropertyName("key")] string? Key
);

// ---- 系统级 P/Invoke ----
public static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
