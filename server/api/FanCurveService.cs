// SPDX-License-Identifier: MIT
//
// FanCurveService -- 自定义散热曲线后台服务
// ===========================================
// 参考 BellatorFanControl (github.com/Aveare/BellatorFanControl) 实现:
//   - 定时读取 CPU/GPU 温度，取最大值作为 hotspot
//   - 根据用户定义的曲线表查找目标风扇转速
//   - 通过 WMI ACPI 接口写入硬件
//   - ShouldWrite 回差策略：
//       温度上升 → 目标转速变化就立刻写入
//       温度下降 → 必须降了 >= hysteresis °C 且目标变化才写入
//   - 解锁期间禁止写入，让 EC 安静完成模式切换

using System.Text.Json;
using Douzhanzhe.HAL;

namespace Douzhanzhe.API;

public sealed class FanCurveService : IDisposable
{
    private readonly HardwareAbstractionLayer _hal;
    private readonly WmiInterface _wmi;
    private readonly ILogger<FanCurveService> _log;

    private Timer? _timer;
    private bool _active;
    private int _intervalMs = 5000;   // BellatorFanControl 默认 5s
    private int _hysteresisC = 3;     // 回差 3°C
    private string _configDir;
    private readonly object _tickLock = new(); // 防止 Tick 重叠执行

    // ShouldWrite 状态跟踪 (对应 BellatorFanControl 的 lastHotspot / lastCpuGpuTarget / lastSysTarget)
    private int? _lastHotspot;
    private int _lastLargeTarget;     // 上次写入的大扇目标 (RPM/100 单位)
    private int _lastSmallTarget;     // 上次写入的小扇目标 (RPM/100 单位)

    // ── ITSM 路由状态 ──
    private byte _savedThermalMode;           // 启动时保存的 ITSM 值，停止时恢复
    private byte _itsmCurveMode;              // 曲线目标对应的 ITSM 模式（RouteMode 计算，仅目标变化时更新）
    private int _itsmDeviationCount;          // 统计：ITSM 读回 ≠ 目标模式 的累计次数

    // ── 偏离检测 (仅日志告警，不主动恢复) ──
    private int _consecutiveDeviation;        // 连续偏离 tick 计数

    public bool Active => _active;

    // ── API 查询属性 (供 /api/fan-curve/route-info 使用) ──
    public byte CurrentItsm { get; private set; }
    public byte RoutedMode { get; private set; }
    public int LastLargeTarget => _lastLargeTarget;
    public int LastSmallTarget => _lastSmallTarget;
    public int ItsmDeviationCount => _itsmDeviationCount;

    // ── EC 读回诊断 (供 Debug 页面使用) ──
    public int ActualCpuFanRpm { get; private set; }     // 实际 CPU 风扇 RPM (EC 0x9D/0x9E)
    public int ActualGpuFanRpm { get; private set; }     // 实际 GPU 风扇 RPM (EC 0x96/0x97)
    public int EcFanTargetLarge { get; private set; }    // EC 大扇目标寄存器 (0x5E, RPM/100)
    public int EcFanTargetSmall { get; private set; }    // EC 小扇目标寄存器 (0x5A, RPM/100)
    public int EcFanShadowLarge { get; private set; }    // EC 大扇影子寄存器 (0x5D)
    public int EcFanShadowSmall { get; private set; }    // EC 小扇影子寄存器 (0x59)
    public bool LastWmiLargeOk { get; private set; }     // 最近一次 WMI 大扇写入返回
    public bool LastWmiSmallOk { get; private set; }     // 最近一次 WMI 小扇写入返回
    public int TickCount { get; private set; }           // Tick 执行总次数
    public int ConsecutiveDeviation => _consecutiveDeviation; // 连续偏离 tick 计数
    public int LastCpuTemp { get; private set; }          // 最近一次 Tick 的 CPU 温度
    public int LastGpuTemp { get; private set; }          // 最近一次 Tick 的 GPU 温度
    public int LastHotspot { get; private set; }          // 最近一次 Tick 的 hotspot (max)

    // 各模式风扇转速合法区间 (RPM/100 单位)
    // 路由表：根据目标转速找到能覆盖它的模式，通过 WMI SetThermalMode 切换
    // 上限是该模式 EC 实际能驱动的最高转速（固件限制），超过会被 EC 拒绝
    // "向下兼容"：低转速在任何模式都能工作，高转速只在对应模式下才行
    private static readonly Dictionary<byte, (int lMin, int lMax, int sMin, int sMax)> ModeFanRanges = new()
    {
        [2] = (19, 29, 17, 64),   // silent
        [0] = (26, 35, 59, 69),   // office
        [1] = (32, 38, 64, 72),   // beast
        [3] = (40, 44, 75, 82),   // gaming
    };

    private static readonly string[] ModeNames = { "均衡", "野兽", "安静", "斗战" };
    private static string ModeName(byte m) => m <= 3 ? ModeNames[m] : $"未知({m})";

    /// <summary>曲线点：温度(°C) → 大扇/小扇目标 RPM</summary>
    /// <remarks>默认曲线: 40°C 最低转速 → 50-85°C 渐变 → 90-100°C 满载</remarks>
    public List<FanCurvePoint> Points { get; private set; } = new()
    {
        new(40, 1900, 1700),
        new(50, 2200, 2000),
        new(55, 2600, 3500),
        new(60, 2900, 4800),
        new(65, 3200, 5900),
        new(70, 3500, 6400),
        new(75, 3800, 6900),
        new(80, 4000, 7500),
        new(85, 4300, 8000),
        new(90, 4400, 8200),
        new(95, 4400, 8200),
        new(100, 4400, 8200),
    };

    public FanCurveService(
        HardwareAbstractionLayer hal,
        WmiInterface wmi,
        ILogger<FanCurveService> log)
    {
        _hal = hal;
        _wmi = wmi;
        _log = log;

        // 定位 config 目录（与 Program.cs 逻辑一致）
        _configDir = Path.Combine(AppContext.BaseDirectory, "config");
        if (!Directory.Exists(_configDir))
        {
            var devConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
            if (Directory.Exists(devConfig))
                _configDir = devConfig;
        }
        Directory.CreateDirectory(_configDir);

        // 启动时清理残留状态：如果上次进程被强杀（重启/崩溃），EC 可能残留手动模式
        // 退出 SetFanManual 让 EC 恢复自动 PID 控制，避免重启后风扇转速异常
        try
        {
            _wmi.SetFanManual(0, false);
            _wmi.SetFanManual(1, false);
            _log.LogInformation("[FanCurve] 启动清理: 已退出手动风扇模式");
        }
        catch (Exception ex)
        {
            _log.LogWarning("[FanCurve] 启动清理失败: {Msg}", ex.Message);
        }
    }

    // ── 启停控制 ──

    public void Start(int? intervalMs = null, int? hysteresisC = null)
    {
        if (_active) return;

        // 每次启动都从 fan-curve.json 加载最新配置（曲线点 + 间隔 + 回差）
        // 这样无论从首页按钮还是曲线 tab 启动，行为一致
        LoadConfig();

        _active = true;
        _intervalMs = Math.Max(1000, intervalMs ?? _intervalMs);
        _hysteresisC = hysteresisC ?? _hysteresisC;
        _lastHotspot = null;
        _lastLargeTarget = 0;
        _lastSmallTarget = 0;

        // 保存当前 ITSM，停止时恢复
        _savedThermalMode = _hal.ReadEcPort(0xE4);
        _itsmDeviationCount = 0;
        _consecutiveDeviation = 0;
        TickCount = 0;

        // 启动时先算一次曲线目标 → RouteMode 确定 ITSM 模式
        // 启动后立即写一次 ITSM，不主动切模式（避免触发 ACPI 链）
        int cpuTemp = _hal.CpuTemperature, gpuTemp = _hal.GpuTemperature;
        int hotspot = Math.Max(cpuTemp, gpuTemp);
        var (lr, sr) = LookupTarget(hotspot > 0 ? hotspot : 40);
        _itsmCurveMode = RouteMode(lr / 100, sr / 100);

        _timer = new Timer(Tick, null, 0, _intervalMs);

        // 启动时写一次 ITSM，让 EC 接受曲线对应的模式区间
        _hal.WriteEcPort(0xE4, _itsmCurveMode);

        _log.LogInformation("[FanCurve] 自定义曲线已启动, 间隔 {Ms}ms, 回差 {H}°C, 保存ITSM={Saved}, 曲线ITSM={Curve}",
            _intervalMs, _hysteresisC, _savedThermalMode, _itsmCurveMode);
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        _timer?.Dispose();
        _timer = null;
        _lastHotspot = null;

        // 通过 WMI 正常切换回保存的模式（触发完整 DPTB/GPUD 链），恢复固件控制
        try
        {
            _wmi.SetThermalMode(_savedThermalMode);
            _wmi.SetFanManual(0, false);
            _wmi.SetFanManual(1, false);
            _log.LogInformation("[FanCurve] 自定义曲线已停止, 恢复到模式 {Mode}({Val}), ITSM偏离 {DC} 次",
                ModeName(_savedThermalMode), _savedThermalMode, _itsmDeviationCount);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[FanCurve] 停止恢复异常: {Msg}", ex.Message);
        }
    }

    /// <summary>显式恢复固件风扇控制（进程退出时调用）</summary>
    public void RestoreFirmwareControl()
    {
        try
        {
            // 先恢复 ITSM 模式（触发 ACPI 链让 EC 重新加载正确的风扇表）
            // 再退出手动模式，让 EC 恢复自动 PID 控制
            if (_savedThermalMode > 0)
                _wmi.SetThermalMode(_savedThermalMode);
            _wmi.SetFanManual(0, false);
            _wmi.SetFanManual(1, false);
            _log.LogInformation("[FanCurve] 固件风扇控制已恢复, ITSM={Mode}", _savedThermalMode);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[FanCurve] 固件恢复异常: {Msg}", ex.Message);
        }
    }

    public void Dispose() => RestoreFirmwareControl();

    // ── 模式路由 ──

    /// <summary>
    /// 根据目标大扇/小扇 RPM 选择 ITSM 模式。
    /// 优先级：安静(2) → 均衡(0) → 野兽(1) → 斗战(3)（越安静越好）
    /// 1. 找同时覆盖大扇和小扇的模式
    /// 2. 仅大扇匹配的模式（小扇将在 Tick 中钳位到该模式范围）
    /// 3. 都不匹配时选最高模式（兜底）
    /// </summary>
    private static byte RouteMode(int largeTargetDiv100, int smallTargetDiv100)
    {
        byte[] priority = { 2, 0, 1, 3 };

        // 第一轮：找同时覆盖大扇和小扇的模式（安静优先）
        foreach (var mode in priority)
        {
            var range = ModeFanRanges[mode];
            if (largeTargetDiv100 >= range.lMin && largeTargetDiv100 <= range.lMax &&
                smallTargetDiv100 >= range.sMin && smallTargetDiv100 <= range.sMax)
                return mode;
        }

        // 第二轮：仅大扇匹配的模式（小扇将由调用方钳位）
        foreach (var mode in priority)
        {
            var range = ModeFanRanges[mode];
            if (largeTargetDiv100 >= range.lMin && largeTargetDiv100 <= range.lMax)
                return mode;
        }

        // 第三轮：兜底 — 选最高模式
        byte bestMode = 3; // 默认斗战
        int bestMax = 0;
        foreach (var mode in priority)
        {
            var range = ModeFanRanges[mode];
            int modeMax = Math.Max(range.lMax, range.sMax);
            if (modeMax > bestMax)
            {
                bestMax = modeMax;
                bestMode = mode;
            }
        }
        return bestMode;
    }

    // ── 定时回调 (核心逻辑：路由 + 曲线查找 + WMI 写转速) ──
    // 目标变化时写入 ITSM + WMI 风扇；解锁期间禁止一切写入，仅监控

    private void Tick(object? state)
    {
        if (!_active) return;
        // 防止上一次 Tick 尚未完成时重叠执行（WMI 并发访问可能导致崩溃）
        if (!Monitor.TryEnter(_tickLock)) return;
        try
        {
            int cpuTemp = _hal.CpuTemperature;
            int gpuTemp = _hal.GpuTemperature;
            int hotspot = Math.Max(cpuTemp, gpuTemp);

            if (hotspot <= 0)
            {
                _log.LogWarning("[FanCurve] Tick 跳过: 温度无效 cpuTemp={Cpu} gpuTemp={Gpu}",
                    cpuTemp, gpuTemp);
                return;
            }

            // 1. 曲线查找 → 原始 RPM 值
            var (largeRpm, smallRpm) = LookupTarget(hotspot);
            int largeTarget = largeRpm / 100;  // RPM → RPM/100 (Bellator 协议)
            int smallTarget = smallRpm / 100;

            // 2. ShouldWrite 回差策略 — 决定是否更新目标值
            bool targetChanged = ShouldWrite(hotspot, largeTarget, smallTarget);
            if (targetChanged)
            {
                _lastHotspot = hotspot;
                _lastLargeTarget = largeTarget;
                _lastSmallTarget = smallTarget;
                _itsmCurveMode = RouteMode(largeTarget, smallTarget);
            }
            else
            {
                largeTarget = _lastLargeTarget;
                smallTarget = _lastSmallTarget;
            }

            // 3. 钳位：大小扇都限制在选中模式的合法范围内
            //    主要场景：大小扇不在同一模式区间时，小扇跟随大扇的模式
            {
                var r = ModeFanRanges[_itsmCurveMode];
                largeTarget = Math.Clamp(largeTarget, r.lMin, r.lMax);
                smallTarget = Math.Clamp(smallTarget, r.sMin, r.sMax);
            }

            // 4. ITSM 读回诊断
            byte currentItsm = _hal.ReadEcPort(0xE4);
            CurrentItsm = currentItsm;
            if (currentItsm != _itsmCurveMode) _itsmDeviationCount++;
            RoutedMode = _itsmCurveMode;

            // 5. ITSM 仅在目标变化时写入（避免频繁触发 ACPI 链）
            if (targetChanged)
            {
                _hal.WriteEcPort(0xE4, _itsmCurveMode);
            }

            // 6. 每个 Tick 都刷新 SetFanManual + SetFanSpeed + EC 寄存器
            //    持续声明手动模式控制权，防止 EC 固件定时器退回自动模式
            bool largeOk = _wmi.SetFanManual(0, true) && _wmi.SetFanSpeed(0, (byte)largeTarget);
            bool smallOk = _wmi.SetFanManual(1, true) && _wmi.SetFanSpeed(1, (byte)smallTarget);
            _hal.WriteEcPort(0x5E, (byte)largeTarget);
            _hal.WriteEcPort(0x5A, (byte)smallTarget);

            // 6. 读回诊断
            TickCount++;
            LastWmiLargeOk = largeOk;
            LastWmiSmallOk = smallOk;
            LastCpuTemp = cpuTemp;
            LastGpuTemp = gpuTemp;
            LastHotspot = hotspot;
            try
            {
                ActualCpuFanRpm = _hal.CpuFanRpm;
                ActualGpuFanRpm = _hal.GpuFanRpm;
                EcFanTargetLarge = _hal.ReadEcPort(0x5E);
                EcFanTargetSmall = _hal.ReadEcPort(0x5A);
                EcFanShadowLarge = _hal.ReadEcPort(0x5D);
                EcFanShadowSmall = _hal.ReadEcPort(0x59);
            }
            catch { }

            // 6. 偏离检测：连续偏离时输出告警日志（不主动恢复，避免 SetThermalMode 副作用）
            {
                bool fanDeviated = ActualCpuFanRpm > 0 &&
                    Math.Abs(ActualCpuFanRpm - largeTarget * 100) > 500;

                if (fanDeviated)
                {
                    _consecutiveDeviation++;
                    if (_consecutiveDeviation % 10 == 1) // 每 10 个 tick 告警一次，避免刷屏
                    {
                        _log.LogWarning(
                            "[FanCurve] 偏离告警 #{N}: 目标={Target} 实际={Actual} (差值={Diff})",
                            _consecutiveDeviation, largeTarget * 100, ActualCpuFanRpm,
                            Math.Abs(ActualCpuFanRpm - largeTarget * 100));
                    }
                }
                else
                {
                    _consecutiveDeviation = 0;
                }
            }

            _log.LogInformation(
                "[FanCurve] Tick: hot={Hot}°C → curve={Mode}({ModeName}) L={L}x100 S={S}x100 | WMI: l={Lr} s={Sr} | ITSM={Itsm} | EC: 5E={EcL} 5A={EcS} 5D={EcD} 59={Ec9} RPM={CpuRpm}/{GpuRpm}",
                hotspot, _itsmCurveMode, ModeName(_itsmCurveMode), largeTarget, smallTarget,
                largeOk, smallOk, currentItsm,
                EcFanTargetLarge, EcFanTargetSmall, EcFanShadowLarge, EcFanShadowSmall,
                ActualCpuFanRpm, ActualGpuFanRpm);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[FanCurve] Tick 异常: {Msg}", ex.ToString());
        }
        finally
        {
            Monitor.Exit(_tickLock);
        }
    }

    /// <summary>
    /// BellatorFanControl 的 ShouldWrite 回差策略:
    ///   - 首次运行: 总是写入
    ///   - 温度上升 (hotspot > lastHotspot): 目标转速变化就写入 (快速响应升温)
    ///   - 温度下降: 必须降了 >= hysteresis °C 且目标变化才写入 (防止频繁跳档)
    ///   - 其他情况: 不写入
    /// </summary>
    private bool ShouldWrite(int hotspot, int largeTarget, int smallTarget)
    {
        // 首次运行
        if (!_lastHotspot.HasValue) return true;

        bool targetChanged = largeTarget != _lastLargeTarget || smallTarget != _lastSmallTarget;

        // 温度上升 → 目标变化就写入
        if (hotspot > _lastHotspot.Value) return targetChanged;

        // 温度下降 → 必须超过回差且目标变化
        if ((_lastHotspot.Value - hotspot) >= _hysteresisC) return targetChanged;

        // 其他 (温度不变或微降但未达回差)
        return false;
    }

    // ── 曲线查找 ──
    // 找到 <= 当前温度的最高曲线点；若温度低于所有点，使用第一个点

    public (int largeRpm, int smallRpm) LookupTarget(int temp)
    {
        if (Points.Count == 0) return (1900, 1700);

        var target = Points[0];
        foreach (var p in Points.OrderBy(x => x.Temp))
        {
            if (temp >= p.Temp) target = p;
        }
        return (target.LargeRpm, target.SmallRpm);
    }

    // ── 配置管理 ──

    public void SetPoints(List<FanCurvePoint> points, int? intervalMs = null, int? hysteresisC = null)
    {
        Points = points.OrderBy(p => p.Temp).ToList();
        if (intervalMs.HasValue) _intervalMs = Math.Max(1000, intervalMs.Value);
        if (hysteresisC.HasValue) _hysteresisC = hysteresisC.Value;

        // 如果正在运行，更新定时器间隔
        if (_active && intervalMs.HasValue)
        {
            _timer?.Dispose();
            _timer = new Timer(Tick, null, 0, _intervalMs);
        }
    }

    // ── 持久化 ──

    private string ConfigPath => Path.Combine(_configDir, "fan-curve.json");

    public void SaveConfig()
    {
        var data = new FanCurveConfig
        {
            Points = Points,
            IntervalMs = _intervalMs,
            HysteresisC = _hysteresisC,
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public void LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            var data = JsonSerializer.Deserialize<FanCurveConfig>(json, opts);
            if (data?.Points != null && data.Points.Count >= 2)
            {
                Points = data.Points.OrderBy(p => p.Temp).ToList();
                _intervalMs = data.IntervalMs > 0 ? data.IntervalMs : 5000;
                _hysteresisC = data.HysteresisC > 0 ? data.HysteresisC : 3;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("[FanCurve] 加载配置失败: {Msg}", ex.Message);
        }
    }
}

// ── 数据模型 ──

public record FanCurvePoint(int Temp, int LargeRpm, int SmallRpm);

public class FanCurveConfig
{
    public List<FanCurvePoint> Points { get; set; } = new();
    public int IntervalMs { get; set; } = 5000;
    public int HysteresisC { get; set; } = 3;
}
