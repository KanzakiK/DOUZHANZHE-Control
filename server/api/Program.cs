using Douzhanzhe.HAL;
using Douzhanzhe.API;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32.TaskScheduler;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<HardwareAbstractionLayer>();
builder.Services.AddSingleton<SmuController>();
builder.Services.AddSingleton<GpuController>();
builder.Services.AddSingleton<NvapiGpuController>();
builder.Services.AddSingleton<CpuPowerController>();
builder.Services.AddSingleton<WmiInterface>();
builder.Services.AddSingleton<FanCurveService>();
builder.Services.AddHostedService<TelemetryBackgroundService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
var app = builder.Build();
app.UseCors();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
// ---- Config directory (shared with Node.js) ----
// 统一使用 AppContext.BaseDirectory 解析，无论工作目录如何都能定位到 server/api/config/
var configDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
Directory.CreateDirectory(configDir);
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
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
    File.WriteAllText(tmpPath, json);
    File.Move(tmpPath, filePath, overwrite: true);
}
// ---- 启动时恢复 GPU 模式 (异步，不阻塞服务启动) ----
_ = System.Threading.Tasks.Task.Run(() =>
{
    try
    {
        var saved = JsonRead<Dictionary<string, int>>("gpu-mode.json", new Dictionary<string, int>());
        if (saved.TryGetValue("gpuMode", out int mode) && mode >= 0 && mode <= 2)
        {
            var wmiStartup = app.Services.GetRequiredService<WmiInterface>();
            if (wmiStartup.Available)
            {
                wmiStartup.SetGpuMode((byte)mode);
                Console.WriteLine($"[Startup] GPU mode restored to {mode}");
            }
        }
    }
    catch (Exception ex) { Console.WriteLine($"[Startup] GPU mode restore failed: {ex.Message}"); }
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
        thermalMode = hal.ThermalMode,
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
        return Results.Content(_sysInfoExtCache, "application/json");
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
        return Results.Content(_sysInfoExtCache, "application/json");
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
app.MapPost("/api/control", (ControlRequest req, HardwareAbstractionLayer hal, WmiInterface wmi) =>
{
    try
    {
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
                break;
            case "thermal_mode":
                hal.ThermalMode = (byte)int.Clamp(req.Value, 0, 3);
                break;
            case "igpu_only":
                hal.IgpuOnly = req.Value != 0;
                break;
            case "ec_write":
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
app.MapPost("/api/smu/set", (SmuController smu, SmuSetRequest req) =>
{
    try
    {
        int rc;
        switch (req.Parameter)
        {
            case "stapm_limit":
            case "power_limit":
                rc = smu.SetPowerLimit((uint)(req.ValueM * 1000));
                break;
            case "short_power_limit":
                rc = smu.SetShortPowerLimit((uint)(req.ValueM * 1000), (uint)(req.ValueM * 1000));
                break;
            case "tctl_temp":
            case "temp_limit":
                rc = smu.SetTempLimit((uint)req.ValueM);
                break;
            case "co_all":
                rc = smu.SetCurveOptimizer(req.ValueM);
                break;
            case "cpu_freq_limit":
                rc = smu.SetCpuFreqLimit((uint)req.ValueM);
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
app.MapPost("/api/smu/raw", (SmuController smu, SmuRawRequest req) =>
{
    try
    {
        var resp = smu.SendRawSmuCommand(req.Cmd, req.Arg0);
        return Results.Json(new { ok = true, cmd = req.Cmd, arg0 = req.Arg0, response = resp });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
app.MapGet("/api/smu/probe", (SmuController smu) =>
{
    try
    {
        var ok = smu.Probe();
        return Results.Json(new { ok, source = "ryzenadj" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message, source = "ryzenadj" });
    }
});
app.MapGet("/api/pci/probe", () =>
{
    try
    {
        var io = Douzhanzhe.HAL.DriverBridge.Instance;
        io.WriteIo32((short)0xCF8, unchecked((int)(0x80000000u | 0x00)));
        var vendorDevice = (uint)io.ReadIo32((short)0xCFC);
        var vendorId = vendorDevice & 0xFFFF;
        var deviceId = vendorDevice >> 16;
        return Results.Json(new { ok = true, vendorId = $"0x{vendorId:X4}", deviceId = $"0x{deviceId:X4}", isAmd = vendorId == 0x1022 });
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
app.MapGet("/api/smu/read-reg", (SmuController smu, HttpContext ctx) =>
{
    try
    {
        var addrStr = ctx.Request.Query["addr"].FirstOrDefault() ?? "0";
        addrStr = addrStr.Trim();
        if (addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            addrStr = addrStr.Substring(2);
        uint addr = Convert.ToUInt32(addrStr, 16);
        var value = smu.ReadSmnRegister(addr);
        return Results.Json(new { ok = true, addr = $"0x{addr:X}", value });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
app.MapPost("/api/fan/set-target", (FanSetRequest req, WmiInterface wmi) =>
{
    try
    {
        // Bellator 协议: 先启用手动风扇模式，再设转速
        wmi.SetFanManual(true);
        if (req.LargeRpm.HasValue)
        {
            var speed = (byte)Math.Clamp(req.LargeRpm.Value / 100, 0, 44);
            wmi.SetFanSpeed(0, speed); // FanType 0 = CPUGPUFan
        }
        if (req.SmallRpm.HasValue)
        {
            var speed = (byte)Math.Clamp(req.SmallRpm.Value / 100, 0, 82);
            wmi.SetFanSpeed(1, speed); // FanType 1 = SYSFan
        }
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});
app.MapPost("/api/fan/restore", (WmiInterface wmi) =>
{
    try
    {
        wmi.SetFanManual(false);
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
        var interval = req?.IntervalMs ?? 3000;
        var hysteresis = req?.HysteresisC ?? 3;
        svc.Start(interval, hysteresis);
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
// ---- GPU 控制 (nvidia-smi 子进程) ----
app.MapPost("/api/gpu/set", (GpuController gpu, GpuSetRequest req) =>
{
    try
    {
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
    Console.WriteLine("[NVAPI] 未初始化，超频/功率/温度控制不可用");

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

app.MapPost("/api/nvapi/overclock", (NvapiGpuController nv, NvapiOverclockRequest req) =>
{
    if (!nv.IsAvailable) return Results.Json(new { ok = false, error = "NVAPI not available" });
    var rc = nv.SetP0Offset(req.CoreOffsetMhz, req.MemOffsetMhz);
    return Results.Json(new { ok = rc == 0, rc });
});

app.MapPost("/api/nvapi/power-limit", (NvapiGpuController nv, NvapiPowerLimitRequest req) =>
{
    if (!nv.IsAvailable) return Results.Json(new { ok = false, error = "NVAPI not available" });
    var rc = nv.SetPowerLimit((uint)(req.PowerW * 1000)); // W → mW
    return Results.Json(new { ok = rc == 0, rc });
});

app.MapPost("/api/nvapi/thermal-limit", (NvapiGpuController nv, NvapiThermalLimitRequest req) =>
{
    if (!nv.IsAvailable) return Results.Json(new { ok = false, error = "NVAPI not available" });
    var rc = nv.SetThermalLimit(req.TempC);
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

app.MapPost("/api/cpu/freq-limit", async (CpuPowerController cpu, CpuFreqLimitRequest req) =>
{
    try
    {
        await cpu.SetFreqLimitAsync(req.Mhz);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/cpu/turbo", async (CpuPowerController cpu, CpuTurboRequest req) =>
{
    try
    {
        await cpu.SetTurboAsync(req.Enabled);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/cpu/core-limit", async (CpuPowerController cpu, CpuCoreLimitRequest req) =>
{
    try
    {
        await cpu.SetCoreLimitAsync(req.Percent);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/cpu/reset", async (CpuPowerController cpu) =>
{
    try
    {
        await cpu.ResetAllAsync();
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ---- Node.js 废弃迁移端点 ----
app.MapPost("/api/uxtu/apply", async (HttpContext ctx, SmuController smu) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = JsonSerializer.Deserialize<UxtuApplyRequest>(await reader.ReadToEndAsync(), jsonOpts);
        if (body == null) return Results.Json(new { ok = false, error = "invalid body" });
        int? cpuPpt = body.Params?.CpuLongPptW ?? body.Limits?.Cpu?.PptLimitW;
        int? cpuShortPpt = body.Params?.CpuShortPptW;
        int? cpuTemp = body.Params?.CpuTempLimitC ?? body.Limits?.Cpu?.TempLimitC;
        int? gpuPpt = body.Params?.GpuPptLimitW ?? body.Limits?.Gpu?.PptLimitW;
        int? cpuVoltage = body.Params?.CpuVoltageOffset;
        bool? cpuFreqEnabled = body.Params?.CpuFreqLimitEnabled;
        int? cpuFreqMhz = body.Params?.CpuFreqLimitMhz;
        bool? cpuTurboOff = body.Params?.CpuTurboDisabled;
        int? cpuCoreLimit = body.Params?.CpuCoreLimit;
        // 批量单次 ryzenadj 调用
        uint? stapmMw = cpuPpt.HasValue ? (uint)(cpuPpt.Value * 1000) : null;
        uint? fastMw = cpuShortPpt.HasValue ? (uint)(cpuShortPpt.Value * 1000) : stapmMw;
        uint? slowMw = fastMw;
        uint? tempC = cpuTemp.HasValue ? (uint)cpuTemp.Value : null;
        int? coAllMv = cpuVoltage;
        uint? maxClkMhz = (cpuFreqEnabled == true && cpuFreqMhz.HasValue) ? (uint)cpuFreqMhz.Value : null;
        bool? turboOff = cpuTurboOff;
        var rc = smu.BatchApply(stapmMw, fastMw, slowMw, tempC, coAllMv, maxClkMhz, turboOff);
        if (cpuCoreLimit.HasValue) { CpuAffinityManager.SetCoreLimit(cpuCoreLimit.Value); }
        return Results.Json(new { ok = rc == 0, message = rc == 0 ? "OK" : $"rc={rc}" });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapGet("/api/ryzenadj/info", (SmuController smu) =>
{
    try
    {
        var probeOk = smu.Probe();
        return Results.Json(new { ok = probeOk, data = new { probeResult = probeOk, type = "subprocess", source = "ryzenadj" } });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
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
app.MapPost("/api/system/settings", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<SystemSettingsRequest>(await reader.ReadToEndAsync());
        Console.WriteLine($"[system] {body?.Key}={body?.Value} — Node.js 已废弃，此端点仅做兼容");
        return Results.Json(new { ok = false, error = "此端点已废弃，请使用 /api/control" });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapPost("/api/fan/full-speed", () =>
{
    return Results.Json(new { ok = false, error = "此端点已废弃，请使用 /api/fan/set-target 手动控制风扇" });
});
app.MapGet("/api/smu/api-type", () =>
{
    return Results.Json(new { ok = true, type = "subprocess", source = "smucontroller->ryzenadj" });
});
app.MapGet("/api/custom-params", () =>
{
    return Results.Json(JsonRead<Dictionary<string, object?>>("custom-params.json", new Dictionary<string, object?>()));
});
app.MapPost("/api/custom-params", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(await reader.ReadToEndAsync());
        JsonWrite("custom-params.json", body ?? new Dictionary<string, object?>());
        return Results.Json(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
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

// ---- Auto-start options (minimized preference) ----
var autoStartOptsPath = Path.Combine(AppContext.BaseDirectory, "config", "auto-start-opts.json");
Directory.CreateDirectory(Path.GetDirectoryName(autoStartOptsPath)!);

app.MapGet("/api/auto-start-opts", () =>
{
    try
    {
        if (File.Exists(autoStartOptsPath))
        {
            var json = File.ReadAllText(autoStartOptsPath);
            var opts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            var minimized = opts != null && opts.TryGetValue("minimized", out var v) && v.ValueKind == JsonValueKind.True && v.GetBoolean();
            return Results.Json(new { minimized });
        }
        return Results.Json(new { minimized = false });
    }
    catch { return Results.Json(new { minimized = false }); }
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
        File.WriteAllText(autoStartOptsPath, JsonSerializer.Serialize(new { minimized }));
        return Results.Json(new { ok = true, minimized });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// ---- Auto-start (Windows Task Scheduler) ----
app.MapGet("/api/auto-start", () =>
{
    try
    {
        using var ts = new TaskService();
        var exists = ts.RootFolder.AllTasks.Any(t => t.Name == "DouzhanzheControl");
        return Results.Json(new { enabled = exists });
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
            var minimized = false;
            if (File.Exists(autoStartOptsPath))
            {
                try
                {
                    var json = File.ReadAllText(autoStartOptsPath);
                    var opts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    minimized = opts != null && opts.TryGetValue("minimized", out var mv) && mv.ValueKind == JsonValueKind.True && mv.GetBoolean();
                }
                catch { }
            }

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
        return Results.Json(new { ok = true, enabled });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
app.MapGet("/debug", () =>
{
    var html = """<!DOCTYPE html><html><head><meta charset="utf-8"><title>C# HAL Debug</title><style>body{background:#0d1117;color:#c9d1d9;font:13px/1.5 monospace;padding:16px;max-width:960px;margin:0 auto}h2{color:#58a6ff;border-bottom:1px solid #30363d;padding-bottom:6px;margin:20px 0 10px;font-size:14px}.section{background:#161b22;border:1px solid #30363d;border-radius:8px;padding:12px 16px;margin-bottom:14px}label{color:#8b949e;min-width:80px;margin:4px 0;font-size:12px}input[type=range]{width:120px;vertical-align:middle;cursor:pointer}button{background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace}button:hover{background:#30363d}pre{background:#0d1117;border:1px solid #30363d;border-radius:6px;padding:10px;overflow:auto;max-height:380px;font:12px monospace;margin:8px 0;color:#7ee787}.badge{display:inline-block;padding:1px 8px;border-radius:10px;font-size:11px;background:#30363d}.badge.on{background:#1c4a2b;color:#3fb950}.badge.off{background:#632f2f;color:#f85149}.row{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin:4px 0}.val{color:#0f0;min-width:20px;display:inline-block;text-align:center}.res{color:#58a6ff;min-width:24px;display:inline-block;text-align:center;font-weight:700;margin:0 4px}.spacer{flex:1}</style></head><body><h2>C# HAL 调试面板</h2><p style="color:#8b949e">端口 3100 <span class="badge on">运行中</span> <span class="badge off">管理员</span></p><div class="section"><h2>灯光与锁</h2><div class="row"><label>键盘背光</label><input type="range" min="0" max="3" value="0" oninput="this.nextElementSibling.textContent=this.value;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'kb_light',value:+this.value})})"><span class="val">0</span></div><div class="row"><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'fn_lock',value:1})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('fnRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('fnRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">ON</button><span class="val">Fn</span><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'fn_lock',value:0})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('fnRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('fnRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">OFF</button><span id="fnRes" class="res">-</span><span class="spacer"></span><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'caps_lock',value:1})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('capsRes').textContent=d.ok?'ON':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('capsRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">ON</button><span class="val">Caps</span><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'caps_lock',value:0})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('capsRes').textContent=d.ok?'OFF':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('capsRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">OFF</button><span id="capsRes" class="res">-</span><span class="spacer"></span><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'num_lock',value:1})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('numRes').textContent=d.ok?'ON':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('numRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">ON</button><span class="val">Num</span><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'num_lock',value:0})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('numRes').textContent=d.ok?'OFF':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('numRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">OFF</button><span id="numRes" class="res">-</span><div class="row"><label>触摸板</label><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'touchpad_lock',value:1})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('tpRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('tpRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">锁定</button><span class="val">触控板</span><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'touchpad_lock',value:0})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('tpRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('tpRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">解锁</button><span id="tpRes" class="res">-</span></div></div></div><div class="section"><h2>系统开关</h2><div class="row"><label>散热模式</label><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'thermal_mode',value:0})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('thermalRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('thermalRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">均衡 0</button><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'thermal_mode',value:1})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('thermalRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('thermalRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">野兽 1</button><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'thermal_mode',value:2})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('thermalRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('thermalRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">安静 2</button><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'thermal_mode',value:3})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('thermalRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('thermalRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">斗战 3</button><span id="thermalRes" class="res">-</span><div class="row"><label>电源计划</label><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'power_plan',value:0})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('ppRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('ppRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">平衡 0</button><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'power_plan',value:1})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('ppRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('ppRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">高性能 1</button><button onclick="var b=this;fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:'power_plan',value:2})}).then(function(r){return r.json()}).then(function(d){b.style.borderColor=d.ok?'#0f0':'#f00';document.getElementById('ppRes').textContent=d.ok?'OK':(d.title||'ERR')}).catch(function(e){b.style.borderColor='#f00';document.getElementById('ppRes').textContent='NET'})" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;margin:3px;font:12px monospace">节能 2</button><span id="ppRes" class="res">-</span></div></div></div><div class="section"><h2>WebSocket 遥测</h2><div id="wsStatus" style="color:#888;margin-bottom:8px">🔴 未连接</div><div id="wsGrid" style="display:grid;grid-template-columns:1fr 1fr 1fr 1fr 1fr;gap:8px 16px;background:#0d1117;border:1px solid #30363d;border-radius:6px;padding:12px;font:14px monospace;margin:8px 0"></div><script>document.addEventListener('DOMContentLoaded',function(){var fields=[["CPU 占用","cpuUsage","%"],["CPU 温度","cpuTemp","°C"],["CPU 频率","cpuFreq","GHz"],["GPU 占用","gpuUsage","%"],["GPU 温度","gpuTemp","°C"],["GPU 频率","gpuFreq","GHz"],["GPU 显存","gpuVram","GB"],["GPU 显存用","gpuVramUsed","GB"],["CPU 核心","cpuCores",""],["大风扇","fanLargeRpm","RPM"],["小风扇","fanSmallRpm","RPM"],["风扇最大","fanLargeMax","RPM"],["风扇最小","fanSmallMax","RPM"],["内存占用","memoryUsage","%"],["内存总量","memoryTotalGB","GB"],["内存频率","memoryFreq","MHz"],["磁盘占用","diskUsage","%"],["磁盘总量","diskTotalGB","GB"],["磁盘剩余","diskFreeGB","GB"],["键盘灯","kbBrightness",""],["Fn锁","fnLock",""],["NumLock","numLock",""],["CapsLock","capsLock",""],["散热模式","thermalMode",""],["电源计划","powerPlan",""],["触控板锁","touchpadLock",""],["集显只","igpuOnly",""],["时间戳","timestamp",""]];var grid=document.getElementById('wsGrid');function render(data){var h='';for(var i=0;i<fields.length;i++){var f=fields[i];var v=data[f[1]];if(v===true)v="✅";else if(v===false)v="❌";h+='<div style="background:#161b22;border:1px solid #30363d;border-radius:4px;padding:6px 10px"><div style="color:#8b949e;font-size:11px;margin-bottom:2px">'+f[0]+'</div><div style="color:#d2a8ff;font-size:15px;font-weight:700">'+v+' <span style="color:#484f58;font-size:11px;font-weight:400">'+f[2]+'</span></div></div>';}grid.innerHTML=h;}var ws=null;function connect(){var s=document.getElementById('wsStatus');if(!s){setTimeout(connect,500);return}try{if(ws)try{ws.close()}catch(e){}s.textContent='🔴 连接中...';s.style.color='#888';ws=new WebSocket('ws://127.0.0.1:3100/ws');ws.onopen=function(){s.textContent='🟢 已连接';s.style.color='#3fb950'};ws.onmessage=function(e){var d;try{d=JSON.parse(e.data);render(d)}catch(ex){return}if(typeof d.fanLargeRpm!=='undefined')document.getElementById('fanLargeActual').textContent=d.fanLargeRpm;if(typeof d.fanSmallRpm!=='undefined')document.getElementById('fanSmallActual').textContent=d.fanSmallRpm};ws.onerror=function(){s.textContent='🔴 连接错误';s.style.color='#f85149'};ws.onclose=function(){s.textContent='🔴 已断开 (3秒后重连)';s.style.color='#f85149';setTimeout(connect,3000)}}catch(ex){s.textContent='🔴 '+ex.message;s.style.color='#f85149';setTimeout(connect,3000)}}connect()});</script></div><div class="section"><h2>WMI 命令测试</h2><div class="row"><select id="wmiCmdSelect" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 8px;font:12px monospace;cursor:pointer" onchange="var inp=document.getElementById('wmiCmdInput');var v=this.value;if(v=='')return;inp.value=v"><option value="">-- 选择命令 --</option><option value="SystemPerMode 0">SystemPerMode 0 (均衡)</option><option value="SystemPerMode 1">SystemPerMode 1 (野兽)</option><option value="SystemPerMode 2">SystemPerMode 2 (安静)</option><option value="SystemPerMode 3">SystemPerMode 3 (斗战)</option><option value="GPUMode 0">GPUMode 0 (混合)</option><option value="GPUMode 1">GPUMode 1 (集显)</option><option value="GPUMode 2">GPUMode 2 (独显)</option><option value="KeyboardType 0">KeyboardType 10 (读键盘类型)</option><option value="FnLock 0">FnLock 0 (关)</option><option value="FnLock 1">FnLock 1 (开)</option><option value="TPLock 0">TPLock 0 (解锁)</option><option value="TPLock 1">TPLock 1 (锁定)</option><option value="CPUGPUSYSFanSpeed 0">CPUGPUSYSFanSpeed 13 (读风扇,可能空壳)</option><option value="RGBKeyboardMode 0">RGBKeyboardMode 16 (键盘RGB模式)</option><option value="RGBKeyboardColor 0">RGBKeyboardColor 17 (键盘RGB颜色)</option><option value="RGBKeyboardBrightness 0">RGBKeyboardBrightness 18 (键盘RGB亮度)</option><option value="SystemAcType 0">SystemAcType 19 (读AC类型)</option><option value="MaxFanSpeedSwitch 0">MaxFanSpeedSwitch 0 (恢复固件)</option><option value="MaxFanSpeedSwitch 1">MaxFanSpeedSwitch 1 (启用手动)</option><option value="MaxFanSpeedSwitch 2">MaxFanSpeedSwitch 查询(只读)</option><option value="MaxFanSpeed 0">MaxFanSpeed 21 (读最大风扇速度)</option><option value="CPUThermometer 0">CPUThermometer 22 (读CPU温度)</option><option value="CPUPower 0">CPUPower 23 (读CPU功率)</option></select></div><div class="row"><input id="wmiCmdInput" type="text" placeholder="命令 值 (例: GPUMode 1)" style="flex:1;background:#0d1117;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 8px;font:12px monospace" onkeydown="if(event.key==='Enter')sendWmiCmd()"><button onclick="sendWmiCmd()" style="background:#21262d;border:1px solid #30363d;color:#c9d1d9;border-radius:4px;padding:4px 12px;cursor:pointer;font:12px monospace">发送</button></div><pre id="wmiOut" style="background:#0d1117;border:1px solid #30363d;border-radius:4px;padding:6px 10px;overflow:auto;max-height:200px;font:11px monospace;color:#58a6ff;margin:4px 0;white-space:pre">-</pre><script>function sendWmiCmd(){var inp=document.getElementById('wmiCmdInput'),out=document.getElementById('wmiOut');if(!inp.value.trim()){out.textContent='ç©º';return}var txt=inp.value.trim();var parts=txt.split(' ');var cmd=parts[0];var val=parts.length>1?parseInt(parts[1])||0:0;var map={};map['SystemPerMode']=['thermal_mode',4];map['GPUMode']=['gpu_mode',3];map['FnLock']=['fn_lock',1];map['TPLock']=['touchpad_lock',1];if(map[cmd]){var t=map[cmd][0];var maxv=map[cmd][1];var cv=Math.min(val,maxv);fetch('/api/control',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({target:t,value:cv})}).then(function(r){return r.json()}).then(function(d){out.textContent=JSON.stringify(d);out.style.color=d.ok?'#3fb950':'#f85149'})['catch'](function(e){out.textContent='ERR:'+e.message;out.style.color='#f85149'})}else if(cmd==='MaxFanSpeedSwitch'){if(val==0){fetch('/api/fan/restore',{method:'POST'}).then(function(r){return r.json()}).then(function(d){out.textContent=d.ok?'å·²æ¢å¤åºä»¶æ§å¶':'ERR';out.style.color=d.ok?'#3fb950':'#f85149'})['catch'](function(e){out.textContent='ERR:'+e.message;out.style.color='#f85149'})}else{out.textContent='å·²å¯ç¨æå¨æ§å¶';out.style.color='#3fb950'}}else{var body={};var parts2=txt.split(' ');var num=parseInt(parts2[0]);if(!isNaN(num)){body.method=num;if(parts2.length>1&&!isNaN(parseInt(parts2[1]))){body.value=parseInt(parts2[1])}}else{out.textContent='æªç¥å½ä»¤: '+cmd;return}fetch('/api/wmi/cmd',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}).then(function(r){return r.json()}).then(function(d){out.textContent=JSON.stringify(d);out.style.color=d.ok?'#3fb950':'#f85149'})['catch'](function(e){out.textContent='ERR:'+e.message;out.style.color='#f85149'})}}</script></div><div class="section"><h2>风扇控制</h2><div class="row"><label>大扇目标</label><input type="range" id="fanLargeSlider" min="0" max="4400" step="100" value="2200" oninput="document.getElementById('fanLargeVal').textContent=this.value"/><span class="val" id="fanLargeVal">2200</span><span style="color:#8b949e;font-size:12px">RPM</span></div><div class="row"><button class="ok" onclick="(async function(){var l=+document.getElementById('fanLargeSlider').value||2200;var r=await fetch('/api/fan/set-target',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({largeRpm:l})});var d=await r.json();if(d.ok){document.getElementById('fanLargeResult').textContent='OK '+d.largeRpm}else{document.getElementById('fanLargeResult').textContent='ERR'}})().catch(function(e){document.getElementById('fanLargeResult').textContent='ERR '+e.message})">大扇下发</button><span class="res" id="fanLargeResult">-</span><span style="color:#8b949e;font-size:12px;margin-left:8px">实际: </span><span class="res" id="fanLargeActual">-</span><span style="color:#8b949e;font-size:12px">RPM</span><span style="color:#484f58;font-size:11px">(auto)</span></div><div class="row"><label>小扇目标</label><input type="range" id="fanSmallSlider" min="0" max="8200" step="100" value="3600" oninput="document.getElementById('fanSmallVal').textContent=this.value"/><span class="val" id="fanSmallVal">3600</span><span style="color:#8b949e;font-size:12px">RPM</span></div><div class="row"><button class="ok" onclick="(async function(){var s=+document.getElementById('fanSmallSlider').value||3600;var r=await fetch('/api/fan/set-target',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({smallRpm:s})});var d=await r.json();if(d.ok){document.getElementById('fanSmallResult').textContent='OK '+d.smallRpm}else{document.getElementById('fanSmallResult').textContent='ERR'}})().catch(function(e){document.getElementById('fanSmallResult').textContent='ERR '+e.message})">小扇下发</button><span class="res" id="fanSmallResult">-</span><span style="color:#8b949e;font-size:12px;margin-left:8px">实际: </span><span class="res" id="fanSmallActual">-</span><span style="color:#8b949e;font-size:12px">RPM</span><span style="color:#484f58;font-size:11px">(auto)</span></div><div class="row"><button class="warn" onclick="(async function(){fetch('/api/fan/set-target',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({largeRpm:0,smallRpm:0})}).then(function(r){return r.json()}).then(function(d){document.getElementById('fanRestoreResult').textContent=d.ok?'OK':'ERR';document.getElementById('fanRestoreResult').style.color=d.ok?'#3fb950':'#f85149'})})().catch(function(e){document.getElementById('fanRestoreResult').textContent='ERR '+e.message;document.getElementById('fanRestoreResult').style.color='#f85149'})">恢复固件控制</button><span class="res" id="fanRestoreResult">-</span><span style="color:#8b949e;font-size:12px;margin-left:4px">MaxFanSpeedSwitch 0</span></div></div><div class="section"><h2>SMU 控制 (ryzenadj 子进程)</h2><div class="row"><button class="warn" onclick="fetch('/api/pci/probe').then(function(r){return r.json()}).then(function(d){document.getElementById('pciResult').textContent=d.ok?'AMD '+d.deviceId:'ERR'})">PCI 探针</button><span id="pciResult" style="color:#8b949e;margin-left:8px">-</span><button class="warn" onclick="fetch('/api/smu/api-type').then(function(r){return r.json()}).then(function(d){document.getElementById('apiTypeResult').textContent=d.type})">API 类型</button><span id="apiTypeResult" style="color:#8b949e;margin-left:8px">-</span></div><div class="row"><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'power_limit',valueM:65000})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuResult').textContent=d.ok?'OK':'ERR'})">长时功耗 65W</button><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'short_power_limit',valueM:75000})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuResult').textContent=d.ok?'OK':'ERR'})">短时功耗 75W</button><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'temp_limit',valueM:90})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuResult').textContent=d.ok?'OK':'ERR'})">温度墙 90C</button><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'temp_limit',valueM:85})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuResult').textContent=d.ok?'OK':'ERR'})">温度墙 85C</button><span id="smuResult" style="color:#8b949e;margin-left:8px">-</span></div><div class="row"><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'co_all',valueM:-20})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuCOResult').textContent=d.ok?'OK':'ERR'})">电压 CO -20</button><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'co_all',valueM:0})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuCOResult').textContent=d.ok?'OK':'ERR'})">电压 CO 0</button><label style="font-size:11px;color:#636e6b">Curve Optimizer (mV)</label><span id="smuCOResult" style="color:#8b949e;margin-left:8px">-</span></div><div class="row"><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'cpu_freq_limit',valueM:3000})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuFreqResult').textContent=d.ok?'OK':'ERR'})">频率限制 3.0GHz</button><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'cpu_freq_limit',valueM:5000})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuFreqResult').textContent=d.ok?'OK':'ERR'})">频率 5.0GHz</button><span id="smuFreqResult" style="color:#8b949e;margin-left:8px">-</span></div><div class="row"><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'turbo_disable',valueM:1})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuTurboResult').textContent=d.ok?'ON':'ERR'})">关睿频</button><button class="ok" onclick="fetch('/api/smu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({parameter:'turbo_disable',valueM:0})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuTurboResult').textContent=d.ok?'OFF':'ERR'})">开睿频</button><span id="smuTurboResult" style="color:#8b949e;margin-left:8px">-</span></div><div class="row"><button class="warn" onclick="fetch('/api/smu/probe').then(function(r){return r.json()}).then(function(d){document.getElementById('smuProbeResult').textContent=d.ok?'OK':'ERR'})">探测 SMU</button><button class="warn" onclick="fetch('/api/smu/status').then(function(r){return r.json()}).then(function(d){var h='probe='+(d.probe?'Y':'N')+' ';var c=d.capabilities||{};var b=[];if(c.powerLimit)b.push('pw');if(c.shortPowerLimit)b.push('sPpt');if(c.tempLimit)b.push('tmp');if(c.curveOptimizer)b.push('CO');if(c.cpuFreqLimit)b.push('freq');if(c.turboDisabled)b.push('tbo');h+=b.join(' ');document.getElementById('smuStatusResult').textContent=h})">SMU Status</button><span id="smuProbeResult" style="color:#8b949e;margin-left:8px">-</span><span id="smuStatusResult" style="color:#7ee787;margin-left:8px;font-size:12px">-</span></div><div class="row"><button class="warn" onclick="fetch('/api/smu/raw',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({cmd:0x4f,arg0:65000})}).then(function(r){return r.json()}).then(function(d){document.getElementById('smuRawResult').textContent=d.ok?'OK':'ERR '+d.error})">Raw 0x4f (stapm 65W)</button><span id="smuRawResult" style="color:#8b949e;margin-left:4px;font:11px monospace">-</span><span style="color:#636e6b;font-size:11px">本后端不支援原始 SMU 命令</span></div></div></div></div></div><div class="section"><h2>GPU 控制 (nvidia-smi)</h2><div class="row"><label>核心频率</label><input type="number" id="gpuFreq" placeholder="MHz" value="2700" style="width:80px;background:#0d1117;border:1px solid #30363d;color:#c9d1d9;padding:2px 6px;border-radius:4px"><button onclick="var v=document.getElementById('gpuFreq').value;if(!v)return;fetch('/api/gpu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'lock-exact',value:+v})}).then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d)})">锁频</button><button onclick="fetch('/api/gpu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'limit-max',value:+document.getElementById('gpuFreq').value})}).then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d)})">设上限</button><button onclick="fetch('/api/gpu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'reset-clocks'})}).then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d)})">重置</button></div><div class="row"><label>显存频率</label><button onclick="fetch('/api/gpu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'reset-memory-clocks'})}).then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d)})">自动</button><button onclick="fetch('/api/gpu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'limit-memory',value:9001})}).then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d)})">9001</button><button onclick="fetch('/api/gpu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'limit-memory',value:11001})}).then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d)})">11001</button><button onclick="fetch('/api/gpu/set',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'limit-memory',value:12001})}).then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d)})">12001</button></div><div class="row"><button onclick="fetch('/api/gpu/status').then(function(r){return r.json()}).then(function(d){document.getElementById('gpuRes').textContent=JSON.stringify(d,null,2)})">读取状态</button></div><div class="row"><pre id="gpuRes" style="min-height:60px;color:#58a6ff">点击按钮查看结果</pre></div></div></div></div></body></html>""";
    return Results.Content(html, "text/html");
});
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
            Console.WriteLine("[WinRing0] Driver not loaded, attempting to install...");
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
                    Console.WriteLine("[WinRing0] Driver loaded OK");
                else
                    Console.WriteLine("[WinRing0] Driver load FAILED - SMU control unavailable");
            }
        }
        else Console.WriteLine("[WinRing0] Driver already loaded");
    }
    else Console.WriteLine("[WinRing0] WinRing0x64.sys not found at " + sysPath);
}
catch (Exception ex) { Console.WriteLine("[WinRing0] Error: " + ex.Message); }

app.Run();
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
public record SmuRawRequest(uint Cmd, uint Arg0);
record SmuSetRequest(string Parameter, int ValueM);
public record FanSetRequest(
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
// ---- Node.js 迁移端点请求/响应模型 ----
public record UxtuApplyRequest(
    UxtuParams? Params,
    UxtuLimits? Limits
);
public record UxtuParams(
    [property: JsonPropertyName("cpuLongPptW")] int? CpuLongPptW,
    [property: JsonPropertyName("cpuShortPptW")] int? CpuShortPptW,
    [property: JsonPropertyName("cpuTempLimitC")] int? CpuTempLimitC,
    [property: JsonPropertyName("gpuPptLimitW")] int? GpuPptLimitW,
    [property: JsonPropertyName("cpuVoltageOffset")] int? CpuVoltageOffset,
    [property: JsonPropertyName("cpuFreqLimitEnabled")] bool? CpuFreqLimitEnabled,
    [property: JsonPropertyName("cpuFreqLimitMhz")] int? CpuFreqLimitMhz,
    [property: JsonPropertyName("cpuTurboDisabled")] bool? CpuTurboDisabled,
    [property: JsonPropertyName("cpuCoreLimit")] int? CpuCoreLimit
);
public record UxtuLimits(
    [property: JsonPropertyName("cpu")] UxtuCpuLimits? Cpu,
    [property: JsonPropertyName("gpu")] UxtuGpuLimits? Gpu
);
public record UxtuCpuLimits(
    [property: JsonPropertyName("pptLimitW")] int? PptLimitW,
    [property: JsonPropertyName("tempLimitC")] int? TempLimitC
);
public record UxtuGpuLimits(
    [property: JsonPropertyName("pptLimitW")] int? PptLimitW
);
public record SystemSettingsRequest(string? Key, int? Value);
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
