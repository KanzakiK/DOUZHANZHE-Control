using Douzhanzhe.HAL;
using Douzhanzhe.API;
using System.Net.WebSockets;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

// ---- SMU 控制器 ----
// AMD: AmdSmuController (PawnIO RyzenSMU.bin)
// Intel: IntelPowerController (PawnIO IntelMSR.bin)
Lazy<AmdSmuController?> _newSmu = new(() =>
{
    try { return new AmdSmuController(); }
    catch (Exception ex) { Log($"AmdSmuController 初始化失败: {ex.Message}"); return null; }
}, LazyThreadSafetyMode.ExecutionAndPublication);
Lazy<IntelPowerController?> _intelSmu = new(() =>
{
    try { return new IntelPowerController(); }
    catch (Exception ex) { Log($"IntelPowerController 初始化失败: {ex.Message}"); return null; }
}, LazyThreadSafetyMode.ExecutionAndPublication);

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
builder.Services.AddSingleton<HardwareDetector>();
builder.Services.AddSingleton<GpuController>();
builder.Services.AddSingleton<NvapiGpuController>();
builder.Services.AddSingleton<CpuPowerController>();
builder.Services.AddSingleton<WmiInterface>();
builder.Services.AddSingleton<OsdService>();
builder.Services.AddSingleton<ProcessMonitorService>();
builder.Services.AddHostedService<TelemetryBackgroundService>();
// ---- Config directory (shared with Node.js) ----
// 安装环境: AppContext.BaseDirectory\config\
// 开发环境: 统一使用 shared config (server/config/)
var configDir = Path.Combine(AppContext.BaseDirectory, "config");
var sharedConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "config"));
if (Directory.Exists(sharedConfig))
    configDir = sharedConfig;
else if (!Directory.Exists(configDir))
    Directory.CreateDirectory(configDir);
builder.Services.AddSingleton<ProfileService>(sp => new ProfileService(configDir));
builder.Services.AddSingleton<GameProfileService>(sp => new GameProfileService(configDir));
builder.Services.AddSingleton<FanCurveService>(sp => new FanCurveService(
    sp.GetRequiredService<HardwareAbstractionLayer>(),
    sp.GetRequiredService<WmiInterface>(),
    sp.GetRequiredService<ILogger<FanCurveService>>(),
    configDir));
builder.Services.AddSingleton<BackgroundRotationService>(sp => new BackgroundRotationService(configDir));
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundRotationService>());
// 游戏封面服务（双注册: 具体类型供端点注入, HostedService 驱动后台队列）
builder.Services.AddSingleton<GameCoverService>(sp => new GameCoverService(configDir, sp.GetRequiredService<GameProfileService>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameCoverService>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(
        "http://localhost:3100", "http://127.0.0.1:3100",
        "http://localhost:3101", "http://127.0.0.1:3101")
     .AllowAnyMethod()
     .AllowAnyHeader()));
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.IncludeFields = true);
// last-mode.json：记录当前选中配置 id 的唯一权威持久化文件。
// 提早在 builder.Build() 前声明，供启动初始化 ProcessMonitor（CurrentMode()）使用。
var _lastModeFile = "last-mode.json";
var app = builder.Build();
var osdService = app.Services.GetRequiredService<OsdService>();
var hal = app.Services.GetRequiredService<HardwareAbstractionLayer>();
var wmi = app.Services.GetRequiredService<WmiInterface>();
app.UseCors();
app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => {
        // index.html 禁止缓存（确保更新后前端 JS bundle 立即生效）
        if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
    }
});

// ---- File logger (统一走 AppLog) ----
void Log(string msg)
{
    AppLog.Write("API", msg);
}
Log($"API starting, BaseDir={AppContext.BaseDirectory}, ConfigDir={configDir}");

// 观察所有未被消费的后台任务异常，避免异步异常静默丢失
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log($"[TaskScheduler] UnobservedTaskException: {e.Exception.Message}");
    e.SetObserved();
};

// ---- PawnIO 驱动检测 + EC 初始化 ----
PawnIoDetection.LogStatus();
try
{
    var pawnIoOk = DriverBridge.Instance.InitPawnIO();
    Log($"PawnIO InitPawnIO: {(pawnIoOk ? "OK" : "失败")}");
}
catch (Exception ex)
{
    Log($"PawnIO InitPawnIO 异常: {ex.Message}");
}

// ---- 开机自启任务自愈：修复存量任务的 72h ExecutionTimeLimit ----
// 背景：安装器 (douzhanzhe-setup.iss) 创建的任务为 PT0S（无限），但设置页开关路径
// 曾未显式设置该字段，TaskScheduler 库默认 72 小时，导致 shell 连续运行满 72h 被
// 任务计划程序静默硬杀（表现为托盘图标消失、后端成孤儿进程，且无任何崩溃日志）。
// v2.0.2 起注册时显式置零（见 /api/autostart），此处对存量坏任务做一次性修复。
_ = System.Threading.Tasks.Task.Run(async () =>
{
    try
    {
        await System.Threading.Tasks.Task.Delay(5_000); // 避开启动高峰，延迟执行
        const string autoStartTaskName = "DouzhanzheControl";
        using var ts = new TaskService();
        var existing = ts.RootFolder.AllTasks.FirstOrDefault(t => t.Name == autoStartTaskName);
        if (existing == null) return; // 未开启自启，无需处理
        var limit = existing.Definition.Settings.ExecutionTimeLimit;
        if (limit == TimeSpan.Zero) return; // 已是无限，正常
        existing.Definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        existing.RegisterChanges();
        Log($"[AutoStart] 自启任务 '{autoStartTaskName}' 的 ExecutionTimeLimit={limit}（会触发 72h 硬终止 bug），已修复为无限 (PT0S)");
    }
    catch (Exception ex)
    {
        Log($"[AutoStart] 自启任务自愈检查失败: {ex.Message}");
    }
});

// ---- 性能设置持久化 (按模式存储) ----
var _perfLock = new object();
var _jsonWriteLock = new object();

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
var _ecWriteWhitelist = new HashSet<byte> { 0x5A, 0x5E, 0xE4 };

// 唯一解包点：配置 id（cfg-…）→ 性能模式裸名(silent/office/beast/gaming)。
// 供 switch / clear / Rank / resetToFactoryDefaults 统一调用，避免各处硬编码。
// 优先查 ProfileEntry.ThermalMode；若是裸性能模式名则原样返回；否则回退 office。
string ResolveConfigThermal(string cfgId)
{
    if (_modeToThermal.ContainsKey(cfgId)) return cfgId;
    try
    {
        var svc = app.Services.GetRequiredService<ProfileService>();
        var entry = svc.GetById(cfgId);
        if (entry != null && !string.IsNullOrEmpty(entry.Value.Entry.ThermalMode)
            && _modeToThermal.ContainsKey(entry.Value.Entry.ThermalMode))
            return entry.Value.Entry.ThermalMode;
    }
    catch { /* 后端/DI 未就绪时走回退 */ }
    return "office";
}

// 统一内置配置判定：内置 id 固定为 cfg-silent/office/beast/gaming（或旧裸性能模式名兼容）。
bool IsBuiltinConfig(string cfgId)
    => cfgId is "cfg-silent" or "cfg-office" or "cfg-beast" or "cfg-gaming"
       || _modeToThermal.ContainsKey(cfgId);

// 启动注入 ProcessMonitor 的配置解析依赖（必须在上述局部函数已声明后调用）
app.Services.GetRequiredService<ProcessMonitorService>().Configure(
    () => CurrentMode(), ResolveConfigThermal);

void ApplyThermalMode(string mode)
{
    if (!_modeToThermal.TryGetValue(mode, out var thermalVal)) return;
    Log($"[thermal_mode] ← mode={mode} (value={thermalVal})");
    if (wmi.Available)
        wmi.SetThermalMode(thermalVal);
    else
        hal.ThermalMode = thermalVal;
    osdService.Show(mode);
}

PerformanceOverrides LoadPerfOverrides()
    => ProfileLoadOverrides(CurrentMode());

PerformanceOverrides ProfileLoadOverrides(string cfgId)
{
    try
    {
        var svc = app.Services.GetRequiredService<ProfileService>();
        return svc.GetById(cfgId)?.Overrides ?? new PerformanceOverrides();
    }
    catch { return new PerformanceOverrides(); }
}

void SavePerfOverrides(Action<PerformanceOverrides> mutate, string? mode = null)
{
    lock (_perfLock)
    {
        var id = mode ?? CurrentMode();
        var svc = app.Services.GetRequiredService<ProfileService>();
        var o = svc.GetById(id)?.Overrides ?? new PerformanceOverrides();
        mutate(o);
        // 单一存储：内置/用户统一写 ProfileService（profiles/），不再写 overrides-*.json
        try { svc.SaveOverrides(id, o); }
        catch (Exception ex) { Log($"[overrides] save failed: {ex.Message}"); }
        Log($"[overrides] ✓ saved → config '{id}'");
    }
}

// ---- 风扇转速写入辅助方法 (WMI + EC 寄存器直写) ----
(int LargeMin, int LargeMax, int SmallMin, int SmallMax) FanRpmRange(string? mode) => mode switch
{
    "silent" => (1900, 2900, 1700, 6400),
    "office" => (2600, 3500, 5900, 6900),
    "gaming" => (4000, 4400, 7500, 8200),
    "beast" => (3200, 3800, 6400, 7200),
    _ => (0, 4400, 0, 8200)
};

void ApplyFanSpeed(WmiInterface wmi, HardwareAbstractionLayer hal, int? largeRpm, int? smallRpm, string? mode = null)
{
    var range = FanRpmRange(mode);
    if (largeRpm.HasValue)
    {
        var rpm = Math.Clamp(largeRpm.Value, range.LargeMin, range.LargeMax);
        var speed = (byte)Math.Clamp(rpm / 100, 0, 44);
        wmi.SetFanManual(0, true);
        wmi.SetFanSpeed(0, speed);
        hal.WriteEcPort(0x5E, speed);
        AppLog.Write("FanEC", $"EC直写 0x5E={speed} (大风扇 {rpm}RPM, mode={mode ?? "?"})");
    }
    if (smallRpm.HasValue)
    {
        var rpm = Math.Clamp(smallRpm.Value, range.SmallMin, range.SmallMax);
        var speed = (byte)Math.Clamp(rpm / 100, 0, 82);
        wmi.SetFanManual(1, true);
        wmi.SetFanSpeed(1, speed);
        hal.WriteEcPort(0x5A, speed);
        AppLog.Write("FanEC", $"EC直写 0x5A={speed} (小风扇 {rpm}RPM, mode={mode ?? "?"})");
    }
}

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
    lock (_jsonWriteLock)
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
}

// ---- 恢复计算类性能设置 (CPU + SMU + GPU + NVAPI, 不含风扇) ----
// 供启动、睡眠恢复、ParameterGuard 共用
async System.Threading.Tasks.Task RestoreComputeSettings(string tag)
{
    try
    {
        var o = LoadPerfOverrides();

        //  并行化说明（优化 overrides/switch 延迟，实测 2.5~3s → 目标 ~1~1.3s）：
        //  CPU/powercfg、SMU(PawnIO 邮箱)、GPU(nvidia-smi)+NVAPI 是三个互相独立的硬件/OS 通道，
        //  原本串行执行；现各自保持内部原有串行顺序（避免破坏硬件写入），仅跨通道并行。
        //  ⚠️ 保持串行边界（防竞态）：
        //    - CPU 与电源计划同组：两者都操作「当前电源方案」SCHEME_CURRENT，不能与其它 powercfg 并发；
        //    - SMU 单设备，串行；GPU+nvapi 共享 gpuMode 判定，同组串行；
        //    - 跨通道不做任何共享可变状态写（restored 只在各自通道内 + 汇总）。
        var cpuTask = System.Threading.Tasks.Task.Run(() => RestoreCpuAndPowerPlan(tag, o));
        var smuTask = System.Threading.Tasks.Task.Run(() => RestoreSmu(tag, o));
        var gpuTask = System.Threading.Tasks.Task.Run(() => RestoreGpuAndNvapi(tag, o));
        await System.Threading.Tasks.Task.WhenAll(cpuTask, smuTask, gpuTask);

        int restored = cpuTask.Result + smuTask.Result + gpuTask.Result;
        if (restored > 0) Log($"[{tag}] Compute settings restored: {restored} applied (parallel)");
        else Log($"[{tag}] No compute settings to restore");
    }
    catch (Exception ex) { Log($"[{tag}] Compute settings restore failed: {ex.Message}"); }
}

// CPU (powercfg) + 电源计划 —— 两者都操作当前活动电源方案，保持串行
int RestoreCpuAndPowerPlan(string tag, PerformanceOverrides o)
{
    int restored = 0;
    var cpu = app.Services.GetRequiredService<CpuPowerController>();

    // --- CPU (powercfg) ---
    // 批量化：3 个参数合并为一次 ApplyCpuAsync（单个电源方案往返），
    // 去掉 2 次多余的 GetActiveScheme/DisableOverlay/SetActiveScheme 子进程开销。
    {
        int? freq = o.Cpu.FreqLimitMhz.HasValue ? o.Cpu.FreqLimitMhz.Value : (int?)null;
        bool? turbo = o.Cpu.TurboEnabled.HasValue ? o.Cpu.TurboEnabled.Value : (bool?)null;
        int? core = (o.Cpu.CoreLimitPercent.HasValue && o.Cpu.CoreLimitPercent.Value > 0) ? o.Cpu.CoreLimitPercent.Value : (int?)null;
        if (freq.HasValue || turbo.HasValue || core.HasValue)
        {
            try
            {
                cpu.ApplyCpuAsync(freq, turbo, core).GetAwaiter().GetResult();
                if (freq.HasValue) { restored++; Log($"[{tag}] CPU freq limit → {freq.Value} MHz"); }
                if (turbo.HasValue) { restored++; Log($"[{tag}] CPU turbo → {turbo.Value}"); }
                if (core.HasValue) { restored++; Log($"[{tag}] CPU core limit → {core.Value}%"); }
            }
            catch (Exception ex) { Log($"[{tag}] CPU apply failed (batched): {ex.Message}"); }
        }
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
    return restored;
}

// SMU (AmdSmuController) —— PawnIO 邮箱单设备，保持原有单次调用串行
int RestoreSmu(string tag, PerformanceOverrides o)
{
    int restored = 0;
    Action<uint?, uint?, uint?, uint?, int?> applySmu = (stapmMw, fastMw, slowMw, tempC, coAll) =>
    {
        var ctrl = _newSmu.Value;
        if (ctrl == null) return;
        if (stapmMw.HasValue) ctrl.SetPowerLimit(stapmMw.Value);
        if (fastMw.HasValue) ctrl.SetShortPowerLimit(fastMw.Value, slowMw ?? fastMw.Value);
        if (tempC.HasValue) ctrl.SetTempLimit(tempC.Value);
        if (coAll.HasValue) ctrl.SetCurveOptimizer(coAll.Value);
    };

    var stapmMw = o.Smu.StapmLimitW.HasValue ? (uint?)(o.Smu.StapmLimitW.Value * 1000) : null;
    var fastMw = o.Smu.ShortPowerLimitW.HasValue ? (uint?)(o.Smu.ShortPowerLimitW.Value * 1000) : null;
    var slowMw = fastMw;
    var tempC = o.Smu.TempLimitC.HasValue ? (uint?)o.Smu.TempLimitC.Value : null;
    var coAll = o.Smu.CoAll.HasValue ? (int?)o.Smu.CoAll.Value : null;

    if (stapmMw.HasValue || fastMw.HasValue || tempC.HasValue || coAll.HasValue)
    {
        try
        {
            applySmu(stapmMw, fastMw, slowMw, tempC, coAll);
            if (stapmMw.HasValue) { restored++; Log($"[{tag}] SMU stapm → {o.Smu.StapmLimitW!.Value}W"); }
            if (fastMw.HasValue) { restored++; Log($"[{tag}] SMU short power → {o.Smu.ShortPowerLimitW!.Value}W"); }
            if (slowMw.HasValue) { restored++; Log($"[{tag}] SMU slow power → {o.Smu.ShortPowerLimitW!.Value}W"); }
            if (tempC.HasValue) { restored++; Log($"[{tag}] SMU temp → {o.Smu.TempLimitC!.Value}°C"); }
            if (coAll.HasValue) { restored++; Log($"[{tag}] SMU CO → {o.Smu.CoAll!.Value}"); }
        }
        catch (Exception ex) { Log($"[{tag}] SMU BatchApply failed: {ex.Message}"); }
    }
    return restored;
}

// GPU (nvidia-smi) + NVAPI —— 共享 gpuMode 判定，同组串行
int RestoreGpuAndNvapi(string tag, PerformanceOverrides o)
{
    int restored = 0;

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
            var rc = nv.SetP0Offset(o.Nvapi.OcCoreOffsetMhz ?? 0, o.Nvapi.OcMemOffsetMhz ?? 0);
            restored++;
            Log($"[{tag}] NVAPI OC → core={o.Nvapi.OcCoreOffsetMhz ?? 0}, mem={o.Nvapi.OcMemOffsetMhz ?? 0} (rc={rc})");
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
    return restored;
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
            try
            {
                var cycleStart = DateTime.Now;
                AppLog.Write("ParameterGuard", $"── 周期开始 [{cycleStart:HH:mm:ss}] ──");

                await RestoreComputeSettings("ParameterGuard");

                // 风扇固定转速守护: 仅在自定义曲线未运行时守护手动 RPM，防止 EC 漂移
                var fanGuardActive = false;
                if (!fanCurveSvc.Active)
                {
                    var o = LoadPerfOverrides();
                    if (o.Fan.LargeRpm.HasValue || o.Fan.SmallRpm.HasValue)
                    {
                        var wmi = app.Services.GetRequiredService<WmiInterface>();
                        var hal = app.Services.GetRequiredService<HardwareAbstractionLayer>();
                        ApplyFanSpeed(wmi, hal, o.Fan.LargeRpm, o.Fan.SmallRpm);
                        fanGuardActive = true;
                    }
                }

                // 参数下发后等时钟稳定，记录实际运行数据快照
                await System.Threading.Tasks.Task.Delay(10_000);
                LogTelemetrySnapshot();

                var elapsed = (DateTime.Now - cycleStart).TotalMilliseconds;
                AppLog.Write("ParameterGuard", $"── 周期完成 耗时={elapsed:F0}ms, 风扇守护={fanGuardActive}, 曲线运行={fanCurveSvc.Active}, 活跃游戏={ProcessMonitorService.ActiveGameCount} ──");
            }
            catch (Exception ex)
            {
                AppLog.Write("ParameterGuard", $"参数重发失败: {ex.Message}");
            }
        }
    }
    catch (OperationCanceledException) { /* 正常退出 */ }
});

// ---- 遥测快照: 参数下发后记录实际运行数据 ----
void LogTelemetrySnapshot()
{
    try
    {
        // CPU
        var cpuFreq = hal.CpuFreq;
        var cpuTemp = hal.CpuTemperature;

        // GPU (HAL: LHM/WMI 来源)
        var gpuFreq = hal.GpuFreq;
        var gpuTemp = hal.GpuTemperature;
        var gpuMemMhz = hal.GpuMemMhz;
        var gpuPower = hal.GpuPowerDrawW;

        // Fan
        var fanLarge = hal.CpuFanRpm;
        var fanSmall = hal.GpuFanRpm;

        // NVAPI (精确频率 + P0 偏移量)
        var nv = app.Services.GetRequiredService<NvapiGpuController>();
        string nvPart = "";
        if (nv.IsAvailable)
        {
            var ns = nv.GetStatus();
            nvPart = $" | NVAPI core={ns.CoreMhz:F0}MHz(Δ{(ns.CoreOffsetMhz >= 0 ? "+" : "")}{ns.CoreOffsetMhz}) " +
                     $"mem={ns.MemMhz:F0}MHz(Δ{(ns.MemOffsetMhz >= 0 ? "+" : "")}{ns.MemOffsetMhz}) " +
                     $"pwr={ns.PowerLimitMw / 1000}W thr={ns.ThermalLimitC:F0}°C";
        }

        AppLog.Write("Telemetry",
            $"CPU {cpuFreq:F2}GHz/{cpuTemp}°C | " +
            $"GPU {gpuFreq:F2}GHz/{gpuMemMhz}MHz/{gpuTemp}°C/{gpuPower:F0}W | " +
            $"Fan {fanLarge}/{fanSmall}RPM{nvPart}");
    }
    catch (Exception ex)
    {
        AppLog.Write("Telemetry", $"快照失败: {ex.Message}");
    }
}

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
            WebSocketReceiveResult? result = null;
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
        gpuMode = wmi.Available ? wmi.GetGpuMode().ToString() : null,
    });
});
// 扩展系统信息服务（C# 直查，替代 sysinfo-ext.ps1 子进程架构）
var _sysInfoSvc = new SystemInfoService();

app.MapGet("/api/system/info", (HardwareAbstractionLayer hal) =>
{
    return Results.Json(new
    {
        systemModel = hal.SystemModel,
        systemManufacturer = _sysInfoSvc.SystemManufacturer,
        cpuName = hal.CpuName,
        cpuCores = hal.CpuCores,
        cpuThreads = hal.CpuThreads,
        cpuBaseFreq = Math.Round((double)hal.CpuBaseFreqMHz / 1000, 2), // 标称基频 GHz（Win32_Processor.MaxClockSpeed）
        cpuMaxFreq = hal.CpuBoostGHz > 0 ? (double?)hal.CpuBoostGHz : null, // 按型号查表（斗战者在售 SKU），表外返回 null
        cpuFreq = Math.Round((double)hal.CpuFreq, 1),                   // 兼容保留（当前实时频率）
        cpuL3Cache = hal.CpuL3CacheMB,
        gpuDiscrete = hal.GpuDiscreteName,
        gpuIntegrated = hal.GpuIntegratedName,
        gpuVramGB = hal.GpuVram,
        gpuTgp = _sysInfoSvc.TgpW > 0 ? (double?)_sysInfoSvc.TgpW : null, // nvidia-smi power.limit
        gpuDx = _sysInfoSvc.DirectXVersion,
        memoryTotalGB = hal.MemoryTotalGB,
        memoryFreq = hal.MemoryFreq,
        diskTotalGB = hal.DiskTotalGB,
        diskFreeGB = hal.DiskFreeGB,
    });
});

// 扩展系统信息（BIOS/OS/磁盘/内存条/电池/显示器/网卡等）—— C# 直查 + 分级缓存
app.MapGet("/api/system/info-ext", () => Results.Json(_sysInfoSvc.GetExtSnapshot()));

app.MapGet("/api/health", (HardwareAbstractionLayer hal) =>
{
    return Results.Json(new
    {
        ok = hal.HealthCheck(),
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
});

bool IsCurrentProcessElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

bool? ShellReportedElevation()
{
    try
    {
        var filePath = Path.Combine(configDir, "permission.json");
        if (!File.Exists(filePath)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("isElevated", out var el) ||
            (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
        {
            return null;
        }
        var pid = root.TryGetProperty("pid", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number
            ? pidEl.GetInt32()
            : 0;
        if (pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.ProcessName.Contains("Douzhanzhe.Shell", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            catch
            {
                return null;
            }
        }
        return el.GetBoolean();
    }
    catch
    {
        return null;
    }
}

// ---- 硬件探测: 平台信息 + 能力集 ----
app.MapGet("/api/platform/info", (HardwareDetector detector, HardwareAbstractionLayer hal) =>
{
    var info = detector.Detect();
    var isElevated = ShellReportedElevation() ?? IsCurrentProcessElevated();
    return Results.Json(new
    {
        vendor = info.Vendor,
        model = info.Model,
        oem = info.Oem.ToString(),
        oemBoard = info.OemBoard,
        capabilities = info.Capabilities,
        isElevated,
        ecCpuTemp = hal.CpuTemperature,
        ecGpuTemp = hal.GpuTemperature,
    });
});

// ---- 优雅关闭: 停止内核驱动 + 触发应用退出 ----
app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
{
    Log("[shutdown] 收到关闭请求，停止内核驱动...");
    StopKernelDrivers();
    lifetime.StopApplication();
    return Results.Json(new { ok = true });
});

// ---- ApplicationStopping 兜底: 确保驱动被停止 ----
app.Lifetime.ApplicationStopping.Register(() =>
{
    AppLog.Write("API", "[shutdown] ApplicationStopping 触发，停止内核驱动...");
    StopKernelDrivers();
});

void StopKernelDrivers()
{
    string[] services = []; // v2.0 已由 PawnIO 替代
    foreach (var svc in services)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"stop {svc}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            Log($"[shutdown] {svc} 驱动已停止");
        }
        catch (Exception ex) { Log($"[shutdown] {svc} 停止失败: {ex.Message}"); }
    }
}
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
                        if (!_ecWriteWhitelist.Contains(reg))
                            return Results.Problem($"EC 寄存器 0x{reg:X2} 不在白名单", statusCode: 400);
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

// ---- 快捷键配置（数据驱动，全局冲突检测） ----

// 默认快捷键定义
var defaultHotkeys = new Dictionary<string, object>
{
    ["monitor-off"] = new { modifiers = "ctrl,shift", key = "Q", enabled = true },
    ["mode-office"] = new { modifiers = "ctrl,shift", key = "1", enabled = true },
    ["mode-beast"]  = new { modifiers = "ctrl,shift", key = "2", enabled = true },
    ["mode-silent"] = new { modifiers = "ctrl,shift", key = "3", enabled = true },
    ["mode-gaming"] = new { modifiers = "ctrl,shift", key = "4", enabled = true },
};

// GET /api/hotkey — 返回所有快捷键配置 + 冲突状态
app.MapGet("/api/hotkey", () =>
{
    var hotkeys = new Dictionary<string, object>();
    // 从默认值开始
    foreach (var (id, def) in defaultHotkeys)
        hotkeys[id] = def;
    // 覆盖用户配置
    var cfgPath = Path.Combine(configDir, "hotkey-config.json");
    if (File.Exists(cfgPath))
    {
        try
        {
            var json = File.ReadAllText(cfgPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("hotkeys", out var hkObj))
            {
                foreach (var prop in hkObj.EnumerateObject())
                {
                    var entry = new Dictionary<string, object>();
                    entry["modifiers"] = prop.Value.TryGetProperty("modifiers", out var m) ? m.GetString() ?? "ctrl,shift" : "ctrl,shift";
                    entry["key"] = prop.Value.TryGetProperty("key", out var k) ? k.GetString() ?? "Q" : "Q";
                    entry["enabled"] = prop.Value.TryGetProperty("enabled", out var e) ? e.GetBoolean() : true;
                    hotkeys[prop.Name] = entry;
                }
            }
            else
            {
                // 兼容旧格式: { "monitorOff": { "modifiers": ..., "key": ..., "enabled": ... } }
                if (doc.RootElement.TryGetProperty("monitorOff", out var mo))
                {
                    var entry = new Dictionary<string, object>();
                    entry["modifiers"] = mo.TryGetProperty("modifiers", out var m) ? m.GetString() ?? "ctrl,shift" : "ctrl,shift";
                    entry["key"] = mo.TryGetProperty("key", out var k) ? k.GetString() ?? "Q" : "Q";
                    entry["enabled"] = mo.TryGetProperty("enabled", out var e) ? e.GetBoolean() : true;
                    hotkeys["monitor-off"] = entry;
                }
            }
        }
        catch { }
    }
    // 读取冲突状态（Shell 写入）
    var conflicts = new HashSet<string>();
    var statusPath = Path.Combine(configDir, "hotkey-status.json");
    if (File.Exists(statusPath))
    {
        try
        {
            var sJson = File.ReadAllText(statusPath);
            using var sDoc = JsonDocument.Parse(sJson);
            if (sDoc.RootElement.TryGetProperty("conflicts", out var cArr))
                foreach (var c in cArr.EnumerateArray())
                    if (c.GetString() is string s) conflicts.Add(s);
            // 兼容旧格式
            if (sDoc.RootElement.TryGetProperty("monitorOffConflict", out var cv) && cv.GetBoolean())
                conflicts.Add("monitor-off");
        }
        catch { }
    }
    // 组装结果
    var result = new Dictionary<string, object>();
    foreach (var (id, val) in hotkeys)
    {
        // 默认值是匿名类型，用户配置是 Dictionary<string, object>，需要统一处理
        string mods = "ctrl,shift";
        string k = "Q";
        bool en = true;
        if (val is Dictionary<string, object> d)
        {
            if (d.TryGetValue("modifiers", out var mv)) mods = mv.ToString() ?? mods;
            if (d.TryGetValue("key", out var kv)) k = kv.ToString() ?? k;
            if (d.TryGetValue("enabled", out var ev) && ev is bool bv) en = bv;
        }
        else if (val != null) // 匿名类型，通过反射读取属性
        {
            var t = val.GetType();
            if (t.GetProperty("modifiers")?.GetValue(val) is string ms) mods = ms;
            if (t.GetProperty("key")?.GetValue(val) is string ks) k = ks;
            if (t.GetProperty("enabled")?.GetValue(val) is bool eb) en = eb;
        }
        result[id] = new { modifiers = mods, key = k, enabled = en, conflict = conflicts.Contains(id) };
    }
    return Results.Json(result);
});

// POST /api/hotkey — 更新指定快捷键
app.MapPost("/api/hotkey", (JsonElement body) =>
{
    if (!body.TryGetProperty("id", out var idEl)) return Results.BadRequest(new { error = "missing id" });
    var id = idEl.GetString() ?? "";
    var cfgPath = Path.Combine(configDir, "hotkey-config.json");
    // 读取现有 hotkeys 配置
    var hotkeys = new Dictionary<string, JsonElement>();
    if (File.Exists(cfgPath))
    {
        try
        {
            var json = File.ReadAllText(cfgPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("hotkeys", out var hk))
                foreach (var p in hk.EnumerateObject())
                    hotkeys[p.Name] = p.Value.Clone();
        }
        catch { }
    }
    // 合并新值
    var entry = new Dictionary<string, object>();
    entry["modifiers"] = body.TryGetProperty("modifiers", out var m) ? m.GetString() ?? "ctrl,shift" : "ctrl,shift";
    entry["key"] = body.TryGetProperty("key", out var k) ? k.GetString() ?? "Q" : "Q";
    entry["enabled"] = body.TryGetProperty("enabled", out var e) ? e.GetBoolean() : true;
    var merged = new Dictionary<string, object>();
    foreach (var (hkId, hkVal) in hotkeys)
    {
        var d = new Dictionary<string, object>();
        if (hkVal.TryGetProperty("modifiers", out var mm)) d["modifiers"] = mm.GetString() ?? "ctrl,shift";
        if (hkVal.TryGetProperty("key", out var kk)) d["key"] = kk.GetString() ?? "Q";
        if (hkVal.TryGetProperty("enabled", out var ee)) d["enabled"] = ee.GetBoolean();
        merged[hkId] = d;
    }
    merged[id] = entry;
    JsonWrite("hotkey-config.json", new { hotkeys = merged });
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
app.MapPost("/api/smu/set", (SmuSetRequest req, HardwareDetector detector, string? mode = null) =>
{
    try
    {
        var platform = detector.Detect();
        Log($"[smu/set] ← {req.Parameter}={req.ValueM} (vendor={platform.Vendor})");

        var ctrl = platform.IsIntel ? (ISmuControl?)_intelSmu.Value : (ISmuControl?)_newSmu.Value;
        if (ctrl == null)
            return Results.Json(new { ok = false, error = "SMU 控制器未初始化" });

        int rc = req.Parameter switch
        {
            "stapm_limit" or "power_limit" => ctrl.SetPowerLimit((uint)(req.ValueM * 1000)),
            "short_power_limit" => ctrl.SetShortPowerLimit((uint)(req.ValueM * 1000), (uint)(req.ValueM * 1000)),
            "tctl_temp" or "temp_limit" => ctrl.SetTempLimit((uint)req.ValueM),
            "co_all" => ctrl.SetCurveOptimizer(req.ValueM),
            "turbo_disable" => ctrl.SetTurboDisabled(req.ValueM != 0),
            _ => -2,
        };

        if (rc == -2)
            return Results.Json(new { ok = false, error = "unknown parameter: " + req.Parameter });

        // 持久化
        var modeVal = req.ValueM;
        switch (req.Parameter)
        {
            case "stapm_limit" or "power_limit": SavePerfOverrides(o => o.Smu.StapmLimitW = modeVal, mode); break;
            case "short_power_limit": SavePerfOverrides(o => o.Smu.ShortPowerLimitW = modeVal, mode); break;
            case "tctl_temp" or "temp_limit": SavePerfOverrides(o => o.Smu.TempLimitC = modeVal, mode); break;
            case "co_all": SavePerfOverrides(o => o.Smu.CoAll = modeVal, mode); break;
        }

        return Results.Json(new { ok = rc == 0, rc });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});
app.MapGet("/api/smu/status", (HardwareDetector detector) =>
{
    try
    {
        var platform = detector.Detect();

        if (platform.IsIntel)
        {
            var intelCtrl = _intelSmu.Value;
            var intelProbe = intelCtrl?.Probe() ?? false;
            return Results.Json(new
            {
                ok = true,
                source = "intel-msr",
                probe = intelProbe,
                capabilities = intelCtrl?.GetCapabilities(),
            });
        }

        var ctrl = _newSmu.Value;
        var probe = ctrl?.Probe() ?? false;
        return Results.Json(new
        {
            ok = true,
            source = "pawnio-amd",
            probe,
            capabilities = ctrl?.GetCapabilities(),
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/fan/set-target", (FanSetRequest req, WmiInterface wmi, HardwareAbstractionLayer hal, string? mode = null) =>
{
    try
    {
        Log($"[fan/set-target] ← large={req.LargeRpm} small={req.SmallRpm}");
        ApplyFanSpeed(wmi, hal, req.LargeRpm, req.SmallRpm, mode);
        // 持久化固定风扇转速，供睡眠恢复 + 启动恢复使用
        SavePerfOverrides(o =>
        {
            var range = FanRpmRange(mode);
            if (req.LargeRpm.HasValue) o.Fan.LargeRpm = Math.Clamp(req.LargeRpm.Value, range.LargeMin, range.LargeMax);
            if (req.SmallRpm.HasValue) o.Fan.SmallRpm = Math.Clamp(req.SmallRpm.Value, range.SmallMin, range.SmallMax);
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
        intervalMs = svc.IntervalMs,
        hysteresisC = svc.HysteresisC,
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
        deviationAlert = svc.DeviationAlert,
        largeDeviationRpm = svc.LargeDeviationRpm,
        smallDeviationRpm = svc.SmallDeviationRpm,
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
    var driverVer = "";
    try { driverVer = gpuCtrl.GetDriverVersion(); } catch { }
    var gpuMode = wmi.Available ? wmi.GetGpuMode().ToString() : "N/A";
    Log($"[Startup] GPU: {gpuName} | Driver: {driverVer} | Mode: {gpuMode}");
}
catch (Exception ex)
{
    Log($"[Startup] Diagnostic log failed: {ex.Message}");
}

app.MapGet("/api/nvapi/status", (NvapiGpuController nv) =>
{
    var s = nv.GetStatus();
    return Results.Json(new {
        ok = s.Available, gpuName = s.GpuName, overclockSupported = s.OverclockSupported,
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
    // thermal 先：用唯一解包点把配置 id → 性能模式裸名，再下发 EC（用户配置也能正确下发生效散热模式）
    ApplyThermalMode(ResolveConfigThermal(req.Mode));
    await System.Threading.Tasks.Task.Delay(250); // 等 EC 完成模式预设加载（500→250，配合并行 after 缩短延迟）

    var overrides = LoadPerfOverrides();
    // 用户自建 profile: 从 ProfileService 加载配置（内置模式使用 overrides-{mode}.json）
    var profileSvc = app.Services.GetRequiredService<ProfileService>();
    var isBuiltin = IsBuiltinConfig(req.Mode);
    if (!isBuiltin)
    {
        var profileData = profileSvc.GetById(req.Mode);
        if (profileData.HasValue)
        {
            overrides = profileData.Value.Overrides;
            // 同步写入 overrides-{id}.json，供 ParameterGuard 周期性重发
            SavePerfOverrides(o => { o.Cpu = overrides.Cpu; o.Gpu = overrides.Gpu; o.Nvapi = overrides.Nvapi; o.Smu = overrides.Smu; o.Fan = overrides.Fan; o.PowerPlan = overrides.PowerPlan; }, req.Mode);
            Log($"[overrides/switch] 加载用户 profile '{req.Mode}' 的配置");
        }
    }

    // 非覆盖通道: 直接调用硬件控制器重置（绕过 SavePerfOverrides，避免并发模式切换写错文件）
    var gpuCtrl = app.Services.GetRequiredService<GpuController>();
    var nvCtrl = app.Services.GetRequiredService<NvapiGpuController>();
    var cpuReset = app.Services.GetRequiredService<CpuPowerController>();

    // 重置阶段并行化（与 RestoreComputeSettings 相同的通道独立性原则）：
    //  GPU+nvapi 一组（共享 GPU），CPU powercfg+电源计划一组（都操作当前电源方案），互不依赖 → 并行。
    var gpuResetTask = System.Threading.Tasks.Task.Run(() =>
    {
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
    });

    var cpuResetTask = System.Threading.Tasks.Task.Run(() =>
    {
        // CPU 功率配置: 无覆盖时恢复默认（写入当前配置，CurrentMode 已切换不受并发影响）
        if (!overrides.Cpu.FreqLimitMhz.HasValue && !overrides.Cpu.TurboEnabled.HasValue && !overrides.Cpu.CoreLimitPercent.HasValue)
        {
            try { cpuReset.ApplyCpuAsync(0, true, 100).GetAwaiter().GetResult(); } catch { }
            SavePerfOverrides(o =>
            {
                o.Cpu.FreqLimitMhz = null; o.Cpu.TurboEnabled = null; o.Cpu.CoreLimitPercent = null;
            }, req.Mode);
        }
        // 电源计划: 无覆盖时恢复平衡
        if (!overrides.PowerPlan.HasValue)
        {
            try { app.Services.GetRequiredService<HardwareAbstractionLayer>().PowerPlan = 0; } catch { }
        }
    });

    await System.Threading.Tasks.Task.WhenAll(gpuResetTask, cpuResetTask);

    // 应用新模式的全部覆盖设置（CPU/SMU/GPU/NVAPI/电源计划 + 风扇）
    await RestoreAllPerfSettings("switch");

    return Results.Json(new { overrides });
});

app.MapPost("/api/overrides/sync", (SyncOverridesRequest req, ProfileService profileSvc) =>
{
    Log($"[overrides/sync] ← mode={req.Mode}, clearing overrides");
    try { profileSvc.ResetToDefaults(req.Mode); }
    catch (Exception ex) { Log($"[overrides/sync] failed: {ex.Message}"); return Results.BadRequest(); }
    return Results.Ok();
});

app.MapPost("/api/overrides/clear", async (ClearOverridesRequest req, ProfileService profileSvc,
    CpuPowerController cpu, GpuController gpu, NvapiGpuController nv,
    WmiInterface wmi, HardwareAbstractionLayer hal) =>
{
    try
    {
        var fields = new HashSet<string>(req.Fields ?? [], StringComparer.OrdinalIgnoreCase);
        if (fields.Count == 0) return Results.Ok(new { ok = true });
        var mode = string.IsNullOrWhiteSpace(req.Mode) ? CurrentMode() : req.Mode;

        SavePerfOverrides(o =>
        {
            if (fields.Contains("cpuFreqLimitMhz")) o.Cpu.FreqLimitMhz = null;
            if (fields.Contains("cpuTurboDisabled")) o.Cpu.TurboEnabled = null;
            if (fields.Contains("cpuCoreLimit")) o.Cpu.CoreLimitPercent = null;
            if (fields.Contains("cpuPowerPlan")) o.PowerPlan = null;
            if (fields.Contains("gpuCoreFreqMhz") || fields.Contains("gpuFreqLimitEnabled"))
            {
                o.Gpu.CoreFreqMhz = null;
                o.Gpu.FreqLocked = null;
            }
            if (fields.Contains("gpuMemFreqMhz")) o.Gpu.MemFreqLevel = null;
            if (fields.Contains("ocCoreOffsetMhz") || fields.Contains("ocMemOffsetMhz"))
            {
                o.Nvapi.OcCoreOffsetMhz = null;
                o.Nvapi.OcMemOffsetMhz = null;
            }
            if (fields.Contains("gpuTempLimitC")) o.Nvapi.ThermalLimitC = null;
            if (fields.Contains("cpuLongPptW")) o.Smu.StapmLimitW = null;
            if (fields.Contains("cpuShortPptW")) o.Smu.ShortPowerLimitW = null;
            if (fields.Contains("cpuTempLimitC")) o.Smu.TempLimitC = null;
            if (fields.Contains("cpuVoltageOffset")) o.Smu.CoAll = null;
            if (fields.Contains("fanLargeRpmTarget")) o.Fan.LargeRpm = null;
            if (fields.Contains("fanSmallRpmTarget")) o.Fan.SmallRpm = null;
        }, mode);

        if (fields.Contains("cpuFreqLimitMhz"))
        {
            try { await cpu.SetFreqLimitAsync(0); } catch (Exception ex) { Log($"[overrides/clear] CPU freq reset: {ex.Message}"); }
        }
        if (fields.Contains("cpuTurboDisabled"))
        {
            try { await cpu.SetTurboAsync(true); } catch (Exception ex) { Log($"[overrides/clear] CPU turbo reset: {ex.Message}"); }
        }
        if (fields.Contains("cpuCoreLimit"))
        {
            try { await cpu.SetCoreLimitAsync(100); } catch (Exception ex) { Log($"[overrides/clear] CPU core reset: {ex.Message}"); }
        }
        if (fields.Contains("cpuPowerPlan"))
        {
            try { hal.PowerPlan = 0; } catch (Exception ex) { Log($"[overrides/clear] power plan reset: {ex.Message}"); }
        }
        if (fields.Contains("gpuCoreFreqMhz") || fields.Contains("gpuFreqLimitEnabled"))
        {
            try { gpu.ResetGpuClocks(); } catch (Exception ex) { Log($"[overrides/clear] GPU clock reset: {ex.Message}"); }
        }
        if (fields.Contains("gpuMemFreqMhz"))
        {
            try { gpu.ResetMemoryClocks(); } catch (Exception ex) { Log($"[overrides/clear] GPU memory reset: {ex.Message}"); }
        }
        if (fields.Contains("ocCoreOffsetMhz") || fields.Contains("ocMemOffsetMhz"))
        {
            try { if (nv.IsAvailable) nv.SetP0Offset(0, 0); } catch (Exception ex) { Log($"[overrides/clear] NVAPI OC reset: {ex.Message}"); }
        }
        if (fields.Contains("gpuTempLimitC"))
        {
            try { if (nv.IsAvailable) nv.SetThermalLimit(87); } catch (Exception ex) { Log($"[overrides/clear] NVAPI thermal reset: {ex.Message}"); }
        }

        var smuCleared = fields.Overlaps(new[] { "cpuLongPptW", "cpuShortPptW", "cpuTempLimitC", "cpuVoltageOffset" });
        if (smuCleared)
        {
            var thermalMode = mode;
            if (!_modeToThermal.TryGetValue(thermalMode, out var tv))
            {
                var profileData = profileSvc.GetById(mode);
                if (profileData.HasValue && _modeToThermal.TryGetValue(profileData.Value.Entry.ThermalMode, out tv))
                    thermalMode = profileData.Value.Entry.ThermalMode;
                else
                {
                    thermalMode = "office";
                    tv = _modeToThermal[thermalMode];
                }
            }
            try
            {
                if (wmi.Available) wmi.SetThermalMode(tv);
                else hal.ThermalMode = tv;
                Log($"[overrides/clear] SMU cleared, re-applied thermal mode {thermalMode}({tv})");
            }
            catch (Exception ex) { Log($"[overrides/clear] SMU thermal reset: {ex.Message}"); }
        }

        if (fields.Contains("fanLargeRpmTarget") || fields.Contains("fanSmallRpmTarget"))
        {
            try
            {
                wmi.SetFanManual(0, false);
                wmi.SetFanManual(1, false);
            }
            catch (Exception ex) { Log($"[overrides/clear] fan restore: {ex.Message}"); }
        }

        return Results.Ok(new { ok = true, cleared = req.Fields });
    }
    catch (Exception ex)
    {
        Log($"[overrides/clear] ✗ {ex.Message}");
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/overrides/import", (SyncOverridesRequest req, ProfileService profileSvc) =>
{
    if (req.Overrides == null) return Results.BadRequest("overrides required");
    try { profileSvc.SaveOverrides(req.Mode, req.Overrides); }
    catch (Exception ex) { Log($"[overrides/import] failed: {ex.Message}"); return Results.BadRequest(); }
    Log($"[overrides/import] ← mode={req.Mode}");
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

// ---- 通知写入（Shell FileSystemWatcher 监听 notify.json 展示系统通知）----
app.MapPost("/api/notify", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync()) ?? new();
        var title = body.TryGetValue("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
        var message = body.TryGetValue("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "";
        var level = body.TryGetValue("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : "info";
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
            return Results.BadRequest(new { error = "title/message 不能为空" });

        JsonWrite("notify.json", new
        {
            title,
            message,
            level,
            createdAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
        });
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ---- 配置备份/恢复 ----
var backupCategories = new Dictionary<string, string[]>
{
    ["config"] = new[] { "custom-params.json", "fan-curve.json", "gpu-mode.json", "performance-overrides.json", "profiles/.index.json" },
    ["games"] = new[] { "game-profiles.json" },
    ["hotkeys"] = new[] { "hotkey-config.json", "hotkey-status.json" },
    ["appearance"] = new[] { "ui-state.json" },
    ["background"] = new[] { "background-opts.json", "background.jpg" },
    ["autostart"] = new[] { "auto-start-opts.json" },
    // 游戏封面图（含本地上传）——二进制，导出为 base64
    ["covers"] = new[] { "covers/" },
    // SteamGridDB API Key（凭据）——前端默认不勾选，仅自用备份时手动勾选
    ["sgdbkey"] = new[] { "cover-opts.json" },
};

HardwareSignatureDto CurrentHardwareSignature(HardwareDetector detector, HardwareAbstractionLayer hal)
{
    var info = detector.Detect();
    return new HardwareSignatureDto(
        info.Oem.ToString(),
        info.Vendor,
        info.Model,
        hal.GpuDiscreteName ?? "",
        HardwareAbstractionLayer.FanLargeMax,
        HardwareAbstractionLayer.FanSmallMax
    );
}

app.MapPost("/api/backup/export", async (HttpContext ctx, HardwareDetector detector, HardwareAbstractionLayer hal) =>
{
    try
    {
        string[] ProfileFiles() =>
            Directory.Exists(Path.Combine(configDir, "profiles"))
                ? Directory.GetFiles(Path.Combine(configDir, "profiles"), "*.json")
                    .Select(p => "profiles/" + Path.GetFileName(p))
                    .ToArray()
                : Array.Empty<string>();

        string[] CoversFiles() =>
            Directory.Exists(Path.Combine(configDir, "covers"))
                ? Directory.GetFiles(Path.Combine(configDir, "covers"))
                    .Select(p => "covers/" + Path.GetFileName(p))
                    .ToArray()
                : Array.Empty<string>();

        using var reader = new StreamReader(ctx.Request.Body);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var req = JsonSerializer.Deserialize<BackupRequest>(await reader.ReadToEndAsync(), opts);
        var cats = req?.Categories ?? Array.Empty<string>();
        var result = new Dictionary<string, object>();
        foreach (var cat in cats)
        {
            if (!backupCategories.TryGetValue(cat, out var files)) continue;
            if (cat == "config") files = files.Concat(ProfileFiles()).Distinct().ToArray();
            if (cat == "covers") files = CoversFiles();
            var catData = new Dictionary<string, JsonElement>();
            foreach (var f in files)
            {
                var filePath = Path.Combine(configDir, f);
                if (!File.Exists(filePath)) continue;
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png" or ".webp")
                {
                    // 二进制图片：base64 打进备份 JSON
                    var b64 = Convert.ToBase64String(await File.ReadAllBytesAsync(filePath));
                    catData[f] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { __base64 = b64 }));
                }
                else
                {
                    var json = await File.ReadAllTextAsync(filePath);
                    catData[f] = JsonSerializer.Deserialize<JsonElement>(json);
                }
            }
            if (catData.Count > 0) result[cat] = catData;
        }
        var payload = JsonSerializer.Serialize(new
        {
            version = 2,
            exportedAt = DateTime.Now,
            hardwareSignature = CurrentHardwareSignature(detector, hal),
            categories = result
        }, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Results.File(bytes, "application/json", $"douzhanzhe-backup-{ts}.json");
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

app.MapPost("/api/backup/import", async (HttpContext ctx, HardwareDetector detector, HardwareAbstractionLayer hal) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var req = JsonSerializer.Deserialize<BackupImportRequest>(await reader.ReadToEndAsync(), opts);
        if (req?.Data.ValueKind != JsonValueKind.Object || !req.Data.TryGetProperty("categories", out var cats))
            return Results.Json(new { ok = false, error = "无效备份文件" });

        // 硬件签名校验：同机备份/恢复，签名不一致整份拒绝
        var current = CurrentHardwareSignature(detector, hal);
        if (req.Data.TryGetProperty("hardwareSignature", out var sig) && sig.ValueKind == JsonValueKind.Object)
        {
            var imported = sig.Deserialize<HardwareSignatureDto>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported == null ||
                !string.Equals(imported.Oem ?? "", current.Oem, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(imported.CpuVendor ?? "", current.CpuVendor, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(imported.CpuModel ?? "", current.CpuModel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(imported.GpuName ?? "", current.GpuName, StringComparison.OrdinalIgnoreCase) ||
                imported.FanMaxLarge != current.FanMaxLarge ||
                imported.FanMaxSmall != current.FanMaxSmall)
            {
                return Results.Json(new
                {
                    ok = false,
                    error = "硬件签名不匹配，备份与当前机器不一致，已整份拒绝导入",
                    signatureMismatch = true
                });
            }
        }

        int restored = 0;
        foreach (var cat in req.Categories ?? Array.Empty<string>())
        {
            if (!backupCategories.TryGetValue(cat, out var files)) continue;
            if (!cats.TryGetProperty(cat, out var catFiles)) continue;
            foreach (var f in files)
            {
                if (!catFiles.TryGetProperty(f, out var content)) continue;
                var filePath = Path.Combine(configDir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? configDir);
                // 二进制图片（备份条目形如 { "__base64": "..." }）→ 解码还原
                if (content.ValueKind == JsonValueKind.Object &&
                    content.TryGetProperty("__base64", out var b64) && b64.ValueKind == JsonValueKind.String)
                {
                    var tmpBin = filePath + ".tmp";
                    await File.WriteAllBytesAsync(tmpBin, Convert.FromBase64String(b64.GetString() ?? ""));
                    File.Move(tmpBin, filePath, overwrite: true);
                    restored++;
                    continue;
                }
                var tmpPath = filePath + ".tmp";
                await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(content, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmpPath, filePath, overwrite: true);
                restored++;
            }
        }
        return Results.Json(new { ok = true, restored });
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
var autoStartOptsPath = Path.Combine(configDir, "auto-start-opts.json");
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
            // ExecutionTimeLimit 必须与安装器 (douzhanzhe-setup.iss) 的 PT0S 一致：
            // TaskScheduler 库默认 72 小时，超时后任务计划程序会静默硬杀 shell
            // （托盘消失、后端成孤儿进程）。置零 = 无运行时限。
            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
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
            var blur = root.TryGetProperty("blur", out var blv) ? Math.Clamp(blv.GetInt32(), 0, 100) : 45;
            var maskColor = root.TryGetProperty("maskColor", out var mv) && mv.GetString() == "white" ? "white" : "black";
            var source = root.TryGetProperty("source", out var sv) && sv.ValueKind == JsonValueKind.String ? sv.GetString() ?? "local" : "local";
            var interval = root.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() ?? "1h" : "1h";
            var apiUrl = root.TryGetProperty("apiUrl", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() ?? "" : "";
            var hasImage = BgImageFiles().Length > 0;
            return Results.Json(new { enabled, opacity, blur, maskColor, source, interval, apiUrl, hasImage });
        }
        return Results.Json(new { enabled = false, opacity = 50, blur = 45, maskColor = "black", source = "local", interval = "1h", apiUrl = "", hasImage = false });
    }
    catch { return Results.Json(new { enabled = false, opacity = 50, blur = 45, maskColor = "black", source = "local", interval = "1h", apiUrl = "", hasImage = false }); }
});

app.MapPost("/api/background-opts", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
        if (body == null) return Results.Json(new { ok = false, error = "无效请求" });

        // 读取当前配置
        bool enabled = false; int opacity = 50; int blur = 45; string maskColor = "black";
        string source = "local"; string interval = "1h"; string apiUrl = "";
        if (File.Exists(bgOptsPath))
        {
            try
            {
                var old = JsonDocument.Parse(File.ReadAllText(bgOptsPath)).RootElement;
                enabled = old.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                opacity = old.TryGetProperty("opacity", out var o) ? o.GetInt32() : 50;
                blur = old.TryGetProperty("blur", out var b) ? b.GetInt32() : 45;
                maskColor = old.TryGetProperty("maskColor", out var m) && m.GetString() == "white" ? "white" : "black";
                source = old.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "local" : "local";
                interval = old.TryGetProperty("interval", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "1h" : "1h";
                apiUrl = old.TryGetProperty("apiUrl", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            }
            catch { }
        }

        if (body.TryGetValue("enabled", out var ev)) enabled = ev.ValueKind == JsonValueKind.True;
        if (body.TryGetValue("opacity", out var ov)) opacity = Math.Clamp(ov.GetInt32(), 0, 100);
        if (body.TryGetValue("blur", out var bv)) blur = Math.Clamp(bv.GetInt32(), 0, 100);
        if (body.TryGetValue("maskColor", out var mv)) maskColor = mv.GetString() == "white" ? "white" : "black";
        if (body.TryGetValue("source", out var ssv) && ssv.ValueKind == JsonValueKind.String) source = ssv.GetString() ?? "local";
        if (body.TryGetValue("interval", out var siv) && siv.ValueKind == JsonValueKind.String) interval = siv.GetString() ?? "1h";
        if (body.TryGetValue("apiUrl", out var av) && av.ValueKind == JsonValueKind.String) apiUrl = av.GetString() ?? "";

        JsonWrite("background-opts.json", new { enabled, opacity, blur, maskColor, source, interval, apiUrl });
        return Results.Json(new { ok = true, enabled, opacity, blur, maskColor, source, interval, apiUrl });
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

        var bytes = Convert.FromBase64String(b64);
        const int MaxBackgroundBytes = 8 * 1024 * 1024;
        if (bytes.Length > MaxBackgroundBytes)
            return Results.Json(new { ok = false, error = "图片超过 8MB 限制" });

        // 先校验后清理，避免超限/解码失败把现有背景删丢
        foreach (var old in BgImageFiles())
        {
            try { File.Delete(old); }
            catch { /* 忽略被占用的文件，写入时会被覆盖 */ }
        }

        var filePath = Path.Combine(configDir, $"background.{ext}");
        var tmpPath = filePath + ".tmp";
        await File.WriteAllBytesAsync(tmpPath, bytes);
        // 原子替换：先写临时文件，再重命名
        File.Move(tmpPath, filePath, overwrite: true);
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

        ctx.Response.Headers["Cache-Control"] = "no-store";

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
        int opacity = 50; int blur = 45; string maskColor = "black";
        string source = "local"; string interval = "1h"; string apiUrl = "";
        if (File.Exists(bgOptsPath))
        {
            try
            {
                var old = JsonDocument.Parse(File.ReadAllText(bgOptsPath)).RootElement;
                opacity = old.TryGetProperty("opacity", out var o) ? o.GetInt32() : 50;
                blur = old.TryGetProperty("blur", out var b) ? b.GetInt32() : 45;
                maskColor = old.TryGetProperty("maskColor", out var m) && m.GetString() == "white" ? "white" : "black";
                source = old.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "local" : "local";
                interval = old.TryGetProperty("interval", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "1h" : "1h";
                apiUrl = old.TryGetProperty("apiUrl", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            }
            catch { }
        }
        JsonWrite("background-opts.json", new { enabled = false, opacity, blur, maskColor, source, interval, apiUrl });
        return Results.Json(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// 手动立即从网络接口拉取一张壁纸（设置页「立刻切换」按钮）
app.MapPost("/api/background-rotate-now", async (HttpContext ctx) =>
{
    try
    {
        string? requestedUrl = null;
        if (ctx.Request.ContentLength is > 0)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
            if (body != null && body.TryGetValue("url", out var u) && u.ValueKind == JsonValueKind.String)
                requestedUrl = u.GetString();
        }

        var bg = ctx.RequestServices.GetRequiredService<BackgroundRotationService>();
        var result = await bg.RotateNowAsync(requestedUrl, ctx.RequestAborted);
        if (result.StartsWith("ok:"))
            return Results.Json(new { ok = true, url = result.Substring(3) });
        return Results.Json(new { ok = false, error = result.Substring(6) });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});

// ---- 检查更新 (GitHub Releases API) ----
var _updateHttpClient = new HttpClient();
_updateHttpClient.Timeout = TimeSpan.FromSeconds(8);
_updateHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DouzhanzheConsole-UpdateChecker/1.0");

// 版本号读取固定 version.txt（deploy.ps1 / build-installer.ps1 从 package.json 写入）。
// 支持预发布后缀（如 2.0.1-pre.18）：System.Version 不认识 "-pre.N"，
// 拆成「数字版本 + 预发布串」分别保存；比较用 CompareAppVersion（数字优先，SemVer 简化规则）。
var _appVersion = "0.0.0";
Version? _appVersionNum = null;
string? _appPre = null;
try
{
    var versionFile = Path.Combine(AppContext.BaseDirectory, "version.txt");
    if (File.Exists(versionFile))
    {
        var text = File.ReadAllText(versionFile).Trim().TrimStart('v');
        var dash = text.IndexOf('-');
        var numPart = dash >= 0 ? text[..dash] : text;
        if (Version.TryParse(numPart, out var v))
        {
            _appVersionNum = v;
            _appPre = dash >= 0 ? text[(dash + 1)..] : null;
            _appVersion = text; // 保留完整版本串（含预发布后缀）供前端展示
        }
    }
}
catch { /* 读取失败时使用默认值 */ }
Log($"Version: {_appVersion} (numeric {_appVersionNum?.ToString() ?? "?"}, pre {_appPre ?? "-"})");

// 版本比较：数字优先；数字相同按 SemVer 简化规则——无预发布 > 有预发布，
// 两者都有预发布时按末尾数字比较（本项目约定 pre.N）
static int CompareAppVersion(Version a, string? aPre, Version b, string? bPre)
{
    var c = a.CompareTo(b);
    if (c != 0) return c;
    if (aPre == null && bPre == null) return 0;
    if (aPre == null) return 1;
    if (bPre == null) return -1;

    static string PreTail(string pre)
    {
        var digits = new string(pre.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length == 0 ? "0" : digits;
    }
    if (int.TryParse(PreTail(aPre), out var an) && int.TryParse(PreTail(bPre), out var bn) && an != bn)
        return an.CompareTo(bn);
    return string.CompareOrdinal(aPre, bPre);
}

// build-info.json 由打包脚本生成：数字版本保持不变，commit 仅作展示/追溯
var _buildLabel = "";
try
{
    var buildFile = Path.Combine(AppContext.BaseDirectory, "build-info.json");
    if (File.Exists(buildFile))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(buildFile));
        if (doc.RootElement.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.String)
            _buildLabel = full.GetString() ?? "";
        else if (doc.RootElement.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.String)
            _buildLabel = commit.GetString() ?? "";
    }
}
catch { /* build-info 缺失时仅打印数字版本 */ }
if (!string.IsNullOrWhiteSpace(_buildLabel))
    Log($"Build: {_buildLabel}");

app.MapGet("/api/update/check", async () =>
{
    try
    {
        // 拉全部 release（含预发布、排除草稿——/releases/latest 会跳过预发布），
        // 取版本最高的一个再与当前比较：pre.N 安装版才能感知 pre.N+1
        var res = await _updateHttpClient.GetAsync(
            "https://api.github.com/repos/KanzakiK/DOUZHANZHE-Control/releases?per_page=30");

        // 无 release (404) 或网络故障 → 视为无更新
        if (!res.IsSuccessStatusCode)
            return Results.Json(new { available = false, currentVersion = _appVersion,
                reason = "无法获取发布信息" });

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Results.Json(new { available = false, currentVersion = _appVersion,
                reason = "发布数据格式异常" });

        // 在全部 release 中取版本最高的一条（含其 body/发布时间/资产）
        string? bestTag = null, bestBody = null, bestPublished = null, bestUrl = null, bestDl = null, bestPre = null;
        Version? bestNum = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) continue;
            var tag = rel.TryGetProperty("tag_name", out var tg) ? tg.GetString() ?? "" : "";
            var dash = tag.IndexOf('-');
            var numPart = dash >= 0 ? tag[..dash] : tag;
            if (!Version.TryParse(numPart.TrimStart('v'), out var num)) continue;

            string? pre = dash >= 0 ? tag[(dash + 1)..] : null;
            if (bestNum == null || CompareAppVersion(num, pre, bestNum, bestPre) > 0)
            {
                bestNum = num; bestPre = pre; bestTag = tag;
                bestBody = rel.TryGetProperty("body", out var b) ? b.GetString() : null;
                bestPublished = rel.TryGetProperty("published_at", out var p) ? p.GetString() : null;
                bestUrl = rel.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                bestDl = null;
                if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("browser_download_url", out var du) &&
                            du.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(du.GetString()))
                        {
                            var candidate = du.GetString();
                            bestDl = candidate;
                            if (Uri.TryCreate(candidate, UriKind.Absolute, out var assetUri) &&
                                Path.GetExtension(assetUri.AbsolutePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }

        if (bestNum == null)
            return Results.Json(new { available = false, currentVersion = _appVersion,
                reason = "仓库暂无发布版本" });

        // 当前版本解析失败（version.txt 缺失/损坏）→ 视为 0.0.0，任何发布都算可更新
        var curNum = _appVersionNum ?? new Version(0, 0, 0);
        var isNewer = CompareAppVersion(bestNum, bestPre, curNum, _appPre) > 0;

        return Results.Json(new
        {
            available = isNewer,
            currentVersion = _appVersion,
            latestVersion = bestTag?.TrimStart('v'),
            body = bestBody,
            publishedAt = bestPublished,
            url = bestUrl,
            downloadUrl = bestDl
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { available = false, currentVersion = _appVersion,
            error = ex.Message });
    }
});

// ---- 更新安装包下载与启动安装 ----
var updateDownloadDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Douzhanzhe Console", "updates");
Directory.CreateDirectory(updateDownloadDir);

bool IsTrustedUpdateHost(Uri uri)
{
    if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        return false;
    var host = uri.Host.ToLowerInvariant();
    return host == "github.com" || host == "api.github.com" ||
           host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
}

app.MapPost("/api/update/download", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var raw = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(raw);
        var urlText = doc.RootElement.TryGetProperty("url", out var urlEl) &&
                      urlEl.ValueKind == JsonValueKind.String
            ? urlEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(urlText) ||
            !Uri.TryCreate(urlText, UriKind.Absolute, out var url) ||
            !IsTrustedUpdateHost(url))
        {
            return Results.Json(new { ok = false, error = "无效的安装包下载地址" });
        }

        var ext = Path.GetExtension(url.AbsolutePath);
        if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return Results.Json(new { ok = false, error = "仅支持 .exe 安装包下载" });

        var fileName = Path.GetFileName(url.AbsolutePath);
        var targetPath = Path.Combine(updateDownloadDir, fileName);
        using var resp = await _updateHttpClient.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            return Results.Json(new { ok = false, error = $"下载失败: HTTP {(int)resp.StatusCode}" });

        var total = resp.Content.Headers.ContentLength ?? 0;
        if (total > 1L * 1024 * 1024 * 1024)
            return Results.Json(new { ok = false, error = "安装包过大，已中止下载" });

        var tmpPath = targetPath + ".part";
        await using (var fs = File.Create(tmpPath))
        {
            await resp.Content.CopyToAsync(fs);
        }
        File.Move(tmpPath, targetPath, overwrite: true);
        return Results.Json(new
        {
            ok = true,
            path = targetPath,
            fileName,
            size = new FileInfo(targetPath).Length
        });
    }
    catch (Exception ex)
    {
        Log($"Update download error: {ex.Message}");
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/update/install", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var raw = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(raw);
        var pathText = doc.RootElement.TryGetProperty("path", out var pathEl) &&
                       pathEl.ValueKind == JsonValueKind.String
            ? pathEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(pathText))
            return Results.Json(new { ok = false, error = "缺少安装包路径" });

        var fullPath = Path.GetFullPath(pathText);
        var updateRoot = Path.GetFullPath(updateDownloadDir).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            return Results.Json(new { ok = false, error = "安装包不存在或路径非法" });
        }

        Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        Log($"Update install error: {ex.Message}");
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

// ---- 旧版驱动已由 PawnIO 替代，不再预启动 ----

// ---- OSD API ----
app.MapPost("/api/osd/show", (OsdShowRequest req, OsdService osd) =>
{
    if (!string.IsNullOrWhiteSpace(req.Text))
        osd.Show(req.Text);
    return Results.Ok();
});

// ---- Game Profiles API ----
app.MapGet("/api/game-profiles", (GameProfileService svc, GameCoverService covers) =>
{
    var profiles = svc.GetAll().Select(p =>
    {
        var hasCover = covers.HasCover(p.Id);
        var exeMissing = !string.IsNullOrEmpty(p.ExePath) && !File.Exists(p.ExePath);
        return new
        {
            p.Id,
            p.Name,
            p.ExePath,
            p.ExeName,
            p.TargetMode,
            p.Enabled,
            p.Source,
            p.StoreAppId,
            p.SgdbGameId,
            hasCover,
            exeMissing,
            // ?v=封面文件时间戳: 换图后前端 URL 变化自动破缓存
            coverUrl = hasCover ? $"/api/game-covers/{p.Id}/image?v={covers.GetCoverVer(p.Id)}" : null
        };
    }).ToList();
    return Results.Json(new
    {
        enabled = svc.Enabled,
        defaultMode = svc.DefaultMode,
        profiles
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
            Source = req.Source ?? "manual",
            StoreAppId = req.StoreAppId ?? "",
            SgdbGameId = req.SgdbGameId ?? ""
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
            Source = req.Source ?? existing.Source,
            StoreAppId = req.StoreAppId ?? existing.StoreAppId,
            SgdbGameId = req.SgdbGameId ?? existing.SgdbGameId
        });
        return Results.Ok(updated);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/game-profiles/{id}", (string id, GameProfileService svc, GameCoverService covers) =>
{
    svc.Delete(id);
    covers.DeleteCover(id); // 同步清理封面文件（含其它扩展名残留）
    return Results.Ok();
});

app.MapPost("/api/game-profiles/{id}/launch", (string id, GameProfileService svc) =>
{
    var profile = svc.GetById(id);
    if (profile == null)
        return Results.NotFound(new { ok = false, error = "规则不存在" });
    if (string.IsNullOrWhiteSpace(profile.ExePath) || !File.Exists(profile.ExePath))
        return Results.BadRequest(new { ok = false, error = "游戏可执行文件不存在" });

    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = profile.ExePath,
            WorkingDirectory = Path.GetDirectoryName(profile.ExePath) ?? "",
            UseShellExecute = true,
        };
        Process.Start(psi);
        Log($"[game-profiles/launch] {profile.Name} → {profile.ExePath}");
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        Log($"[game-profiles/launch] ✗ {ex.Message}");
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
    }
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
app.MapPost("/api/game-profiles/batch", async (HttpRequest req, GameProfileService profiles, GameCoverService covers) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    if (!body.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "games array required" });

    int added = 0;
    var addedIds = new List<string>();
    foreach (var g in games.EnumerateArray())
    {
        var name = g.TryGetProperty("name", out var n) ? n.GetString() : null;
        var exePath = g.TryGetProperty("exePath", out var ep) ? ep.GetString() : null;
        var targetMode = g.TryGetProperty("targetMode", out var tm) ? tm.GetString() ?? "gaming" : "gaming";
        var source = g.TryGetProperty("source", out var src) ? src.GetString() ?? "scan" : "scan";
        var appId = g.TryGetProperty("appId", out var aid) ? aid.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exePath)) continue;

        var exeName = Path.GetFileName(exePath);
        try
        {
            var created = profiles.Add(new GameProfile
            {
                Name = name,
                ExeName = exeName,
                ExePath = exePath,
                TargetMode = targetMode,
                Source = source,
                Enabled = true,
                StoreAppId = appId
            });
            added++;
            addedIds.Add(created.Id);
        }
        catch { }
    }

    // 自动获取封面: 后台队列抓取, 不阻塞导入响应; 已有封面/已在队列的自动跳过
    int coverQueued = 0;
    var autoCover = body.TryGetProperty("autoCover", out var ac) && ac.ValueKind == JsonValueKind.True;
    if (autoCover && addedIds.Count > 0)
        coverQueued = covers.Enqueue(addedIds, force: false);

    return Results.Ok(new { added, coverQueued });
});


// ---- Game Covers API ----
// 封面图片（?v= 由前端携带, 内容变化时 URL 随之变化, 可长缓存）
app.MapGet("/api/game-covers/{id}/image", async (string id, HttpContext ctx, GameCoverService covers) =>
{
    var path = covers.FindCoverFile(id);
    if (path == null) return Results.NotFound();

    var ext = Path.GetExtension(path).ToLowerInvariant();
    var contentType = ext switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };
    ctx.Response.Headers["Cache-Control"] = "public, max-age=604800"; // 7 天, 靠 ?v= 破缓存

    var bytes = await File.ReadAllBytesAsync(path);
    return Results.File(bytes, contentType);
});

// 上传本地图片（base64 dataUrl, ≤8MB）
app.MapPost("/api/game-covers/{id}/image", async (string id, HttpRequest req, GameProfileService profiles, GameCoverService covers) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    var dataUrl = body.TryGetProperty("dataUrl", out var d) ? d.GetString() : null;
    if (string.IsNullOrEmpty(dataUrl))
        return Results.BadRequest(new { error = "dataUrl required" });
    if (profiles.GetById(id) == null)
        return Results.NotFound(new { error = "规则不存在" });

    var err = covers.UploadFromBase64(id, dataUrl);
    if (err != null) return Results.BadRequest(new { error = err });
    return Results.Ok(new { ok = true, coverVer = covers.GetCoverVer(id) });
});

// 恢复默认（删除封面文件, 回到首字母占位）
app.MapDelete("/api/game-covers/{id}/image", (string id, GameCoverService covers) =>
{
    covers.DeleteCover(id);
    return Results.Ok(new { ok = true });
});

// 手动入队抓取（编辑弹窗单发 / 指定集合）
app.MapPost("/api/game-covers/fetch", async (HttpRequest req, GameCoverService covers) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    var ids = new List<string>();
    if (body.TryGetProperty("profileIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
        foreach (var e in arr.EnumerateArray())
        {
            var s = e.GetString();
            if (!string.IsNullOrEmpty(s)) ids.Add(s);
        }
    var force = body.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
    return Results.Ok(new { queued = covers.Enqueue(ids, force) });
});

// 一键补齐: 所有缺封面的非手动来源游戏入队（manual 防名称误配, 走编辑弹窗人工确认）;
// 游戏文件已不存在的失效规则自动跳过并单独回报
app.MapPost("/api/game-covers/fetch-missing", (GameProfileService profiles, GameCoverService covers) =>
{
    int skippedDead = 0;
    var ids = new List<string>();
    foreach (var p in profiles.GetAll())
    {
        if (p.Source == "manual") continue;
        if (!string.IsNullOrEmpty(p.ExePath) && !File.Exists(p.ExePath)) { skippedDead++; continue; }
        ids.Add(p.Id);
    }
    return Results.Ok(new { queued = covers.Enqueue(ids, force: false), skippedDead });
});

// 抓取进度（前端 1.5s 轮询）
app.MapGet("/api/game-covers/progress", (GameCoverService covers) =>
{
    return Results.Json(covers.GetProgress());
});

// SGDB 候选游戏搜索代理（Key 不下发前端）
app.MapGet("/api/game-covers/search", async (string term, GameCoverService covers) =>
{
    if (string.IsNullOrWhiteSpace(term))
        return Results.BadRequest(new { error = "term required" });
    if (!covers.HasKey)
        return Results.BadRequest(new { error = "未配置 SteamGridDB API Key" });
    var (data, err) = await covers.SearchGamesAsync(term);
    if (err != null) return Results.Json(new { error = err }, statusCode: 502);
    return Results.Json(new { data });
});

// SGDB 候选封面列表代理（缩略图选择用）；steamAppId=按 Steam AppID 精确查询（绕过名称搜索）
app.MapGet("/api/game-covers/candidates", async (string? gameId, string? steamAppId, GameCoverService covers) =>
{
    if (!string.IsNullOrWhiteSpace(steamAppId))
    {
        var appId = GameCoverService.ExtractSteamAppId(steamAppId);
        if (appId == null)
            return Results.BadRequest(new { error = "无法解析 AppID——支持纯数字或 Steam 商店链接（store.steampowered.com/app/...）" });
        if (!covers.HasKey)
            return Results.BadRequest(new { error = "未配置 SteamGridDB API Key" });
        var (d, e) = await covers.GetCandidatesBySteamAppIdAsync(steamAppId);
        if (e != null) return Results.Json(new { error = e }, statusCode: 502);
        return Results.Json(new { data = d });
    }
    if (string.IsNullOrWhiteSpace(gameId))
        return Results.BadRequest(new { error = "gameId or steamAppId required" });
    if (!covers.HasKey)
        return Results.BadRequest(new { error = "未配置 SteamGridDB API Key" });
    var (data, err) = await covers.GetCandidatesAsync(gameId);
    if (err != null) return Results.Json(new { error = err }, statusCode: 502);
    return Results.Json(new { data });
});

// 采用用户选定的候选图（仅允许 https + *.steamgriddb.com, 防 SSRF）
app.MapPost("/api/game-covers/{id}/select", async (string id, HttpRequest req, GameProfileService profiles, GameCoverService covers) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    var url = body.TryGetProperty("url", out var u) ? u.GetString() : null;
    if (string.IsNullOrEmpty(url))
        return Results.BadRequest(new { error = "url required" });
    if (profiles.GetById(id) == null)
        return Results.NotFound(new { error = "规则不存在" });

    var err = await covers.DownloadFromUrlAsync(id, url);
    if (err != null) return Results.BadRequest(new { error = err });
    return Results.Ok(new { ok = true, coverVer = covers.GetCoverVer(id) });
});

// SGDB Key 设置（只回 hasKey, 不回明文）
app.MapGet("/api/game-covers/opts", (GameCoverService covers) =>
{
    return Results.Ok(new { hasKey = covers.HasKey });
});

app.MapPut("/api/game-covers/opts", async (HttpRequest req, GameCoverService covers) =>
{
    var body = await req.ReadFromJsonAsync<JsonElement>();
    var key = body.TryGetProperty("sgdbKey", out var k) ? k.GetString() : null;
    covers.SetSgdbKey(key ?? "");
    return Results.Ok(new { ok = true, hasKey = covers.HasKey });
});


// ---- Profile Service 初始化 ----
var profileSvc = app.Services.GetRequiredService<ProfileService>();
profileSvc.EnsureInitialized(configDir);
var validProfileIds = new HashSet<string>(profileSvc.GetAll().Select(p => p.Id));
if (!validProfileIds.Contains(CurrentMode()))
{
    Log($"[Profiles] 当前模式 {CurrentMode()} 不存在，回退到 cfg-office");
    SetCurrentMode("cfg-office");
}

// ---- 旧配置一次性迁移（1.6.x overrides-*.json → 用户配置，幂等） ----
// 只导入 PerformanceOverrides 嵌套结构的 overrides-*.json；扁平文件(custom-params 等)不导入。
void MigrateLegacyOverrides(string cfgDir, ProfileService svc)
{
    var marker = Path.Combine(cfgDir, "legacy-migration-done.json");
    if (File.Exists(marker)) return;

    var files = Directory.GetFiles(cfgDir, "overrides-*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
    int imported = 0;
    foreach (var file in files)
    {
        var bare = Path.GetFileNameWithoutExtension(file)["overrides-".Length..];
        if (bare.StartsWith("cfg-", StringComparison.OrdinalIgnoreCase)) continue; // 新内置残留，跳过
        try
        {
            var ov = JsonRead<PerformanceOverrides>(file, new PerformanceOverrides());
            if (!HasAnyOverride(ov)) { continue; } // 空/非法跳过
            var thermal = bare switch { "silent" or "office" or "beast" or "gaming" => bare, _ => "office" };
            var created = svc.Create($"旧版-{bare}", thermal);
            if (created == null) continue;
            svc.SaveOverrides(created.Id, ov);
            imported++;
            Log($"[MigrateLegacy] 导入旧配置 → 用户配置 '{created.Id}' (from {Path.GetFileName(file)})");
        }
        catch (Exception ex)
        {
            Log($"[MigrateLegacy] 跳过 {Path.GetFileName(file)}: {ex.Message}");
        }
    }
    if (imported > 0) Log($"[MigrateLegacy] 完成：导入 {imported} 个旧配置");
    // 无论是否导入都写标记，避免每次启动重复扫描；导入成功的源文件一并清理
    try { JsonWrite(marker, new { done = true }); } catch { }
    foreach (var file in files)
    {
        var bare = Path.GetFileNameWithoutExtension(file)["overrides-".Length..];
        if (!bare.StartsWith("cfg-", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); Log($"[MigrateLegacy] 已清理旧源文件: {Path.GetFileName(file)}"); }
            catch (Exception ex) { Log($"[MigrateLegacy] 清理失败: {ex.Message}"); }
        }
    }
}

bool HasAnyOverride(PerformanceOverrides o)
    => o.PowerPlan != null
       || o.Cpu.FreqLimitMhz != null || o.Cpu.TurboEnabled != null || o.Cpu.CoreLimitPercent != null
       || o.Gpu.CoreFreqMhz != null || o.Gpu.FreqLocked != null || o.Gpu.MemFreqLevel != null
       || o.Nvapi.OcCoreOffsetMhz != null || o.Nvapi.OcMemOffsetMhz != null || o.Nvapi.PowerLimitW != null || o.Nvapi.ThermalLimitC != null
       || o.Smu.StapmLimitW != null || o.Smu.ShortPowerLimitW != null || o.Smu.TempLimitC != null || o.Smu.CoAll != null
       || o.Fan.LargeRpm != null || o.Fan.SmallRpm != null;

MigrateLegacyOverrides(configDir, profileSvc);

// ---- Profiles API ----
app.MapGet("/api/profiles", (ProfileService svc) =>
{
    var profiles = svc.GetAll();
    return Results.Json(new { profiles });
});

app.MapGet("/api/profiles/{id}", (string id, ProfileService svc) =>
{
    var result = svc.GetById(id);
    if (result == null) return Results.NotFound(new { error = "配置不存在" });
    return Results.Json(new { entry = result.Value.Entry, overrides = result.Value.Overrides });
});

app.MapPut("/api/profiles/{id}", async (string id, HttpContext ctx, ProfileService svc) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var json = await reader.ReadToEndAsync();
        var overrides = JsonSerializer.Deserialize<PerformanceOverrides>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
        }) ?? new PerformanceOverrides();
        if (!svc.SaveOverrides(id, overrides))
            return Results.NotFound(new { error = "配置不存在" });
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { ok = false, error = ex.Message });
    }
});

app.MapPost("/api/profiles", async (HttpRequest req, ProfileService svc) =>
{
    try
    {
        using var reader = new StreamReader(req.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync()) ?? new();
        string? name = null;
        string? thermalMode = null;
        if (body.TryGetValue("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
            name = nEl.GetString();
        if (body.TryGetValue("thermalMode", out var tEl) && tEl.ValueKind == JsonValueKind.String)
            thermalMode = tEl.GetString();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new { error = "名称不能为空" });
        var created = svc.Create(name, thermalMode);
        if (created == null)
            return Results.BadRequest(new { error = "创建失败" });
        return Results.Json(created);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/profiles/{id}", (string id, ProfileService svc, GameProfileService gameSvc) =>
{
    var entry = svc.GetAll().FirstOrDefault(p => p.Id == id);
    if (entry == null)
        return Results.NotFound(new { error = "配置不存在" });
    if (entry.BuiltIn)
        return Results.BadRequest(new { error = "内置配置不能删除" });
    if (id == CurrentMode())
        return Results.BadRequest(new { error = "当前正在使用此配置，请先切换到其他配置再删除" });

    var boundGames = gameSvc.GetAll().Where(p => p.TargetMode == id).ToList();
    if (boundGames.Count > 0)
        return Results.BadRequest(new { error = $"有 {boundGames.Count} 条游戏规则绑定此配置，请先解除绑定再删除" });

    if (!svc.Delete(id))
        return Results.BadRequest(new { error = "删除失败" });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/profiles/{id}/rename", async (string id, HttpContext ctx, ProfileService svc) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync()) ?? new();
    if (!body.TryGetValue("name", out var nEl) || nEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { error = "名称不能为空" });
    if (!svc.Rename(id, nEl.GetString() ?? ""))
        return Results.BadRequest(new { error = "重命名失败" });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/profiles/{id}/copy", (string id, ProfileService svc) =>
{
    // svc.Copy 已从 ProfileService 复制参数（profiles/），无需再同步 overrides-*.json
    var created = svc.Copy(id);
    if (created == null)
        return Results.BadRequest(new { error = "复制失败" });
    return Results.Json(created);
});

app.MapPost("/api/profiles/{id}/reset", (string id, ProfileService svc) =>
{
    // ResetToDefaults 已清空 ProfileService 参数（保留 thermalMode），无需再清 overrides-*.json
    if (!svc.ResetToDefaults(id))
        return Results.BadRequest(new { error = "重置失败" });
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/profiles/{id}/thermal-mode", async (string id, HttpContext ctx, ProfileService svc) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync()) ?? new();
    if (!body.TryGetValue("thermalMode", out var tEl) || tEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { error = "thermalMode 不能为空" });
    if (!svc.SetThermalMode(id, tEl.GetString() ?? ""))
        return Results.BadRequest(new { error = "设置失败" });
    return Results.Ok(new { ok = true });
});

// ---- SPA fallback (必须在所有 API 路由之后) ----
app.MapFallbackToFile("index.html");

// ---- Start server ----
try
{
    Log("Starting server");
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
public record UiState(string? Theme, string? AccentColor)
{
    public UiState() : this((string?)null, (string?)null) { }
    public string Theme { get; init; } = Theme ?? "dark";
    public string AccentColor { get; init; } = AccentColor ?? "#4cc2ff";
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
public record ClearOverridesRequest([property: JsonPropertyName("mode")] string Mode, [property: JsonPropertyName("fields")] List<string>? Fields);
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



public record BackupRequest(string[]? Categories);
public record BackupImportRequest(JsonElement Data, string[]? Categories);
public record HardwareSignatureDto(
    [property: JsonPropertyName("oem")] string Oem,
    [property: JsonPropertyName("cpuVendor")] string CpuVendor,
    [property: JsonPropertyName("cpuModel")] string CpuModel,
    [property: JsonPropertyName("gpuName")] string GpuName,
    [property: JsonPropertyName("fanMaxLarge")] int FanMaxLarge,
    [property: JsonPropertyName("fanMaxSmall")] int FanMaxSmall
);
