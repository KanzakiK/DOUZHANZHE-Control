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

    // ── 自动恢复 (EC PID 解锁) ──
    private bool _recovering;                 // true = 正在解锁 EC，等待风扇恢复后切回 _savedThermalMode
    private int _consecutiveDeviation;        // 未恢复时：连续偏离 tick 计数
    private DateTime _lastRecoveryTime = DateTime.MinValue;
    private int _recoveryCount;
    private const int DeviationThreshold = 3;
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(5);

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
    public int RecoveryCount => _recoveryCount;           // SetThermalMode 自动恢复次数
    public bool Recovering => _recovering;                 // 是否正在解锁 EC（两阶段恢复中）

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
        _recovering = false;
        _lastRecoveryTime = DateTime.MinValue;
        _recoveryCount = 0;

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
    /// 根据目标大扇/小扇 RPM 找到能同时覆盖两者的模式。
    /// 优先级：安静(2) → 均衡(0) → 野兽(1) → 斗战(3)（越安静越好）
    /// 无完美匹配时选最高模式（向下兼容：低转速在任何模式都能工作，
    /// 高转速需要匹配模式 → 选更高的模式更安全）。
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

        // 第二轮：无完美匹配 — 选最高模式
        // 向下兼容：低转速在任何模式都能工作，高转速需要匹配模式
        // 选更高模式 = 更可能覆盖高转速目标
        byte bestMode = 1; // 默认野兽
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

            // 2. 解锁期间：禁止一切写入，仅监控温度和风扇
            if (_recovering)
            {
                TickCount++;
                try
                {
                    ActualCpuFanRpm = _hal.CpuFanRpm;
                    ActualGpuFanRpm = _hal.GpuFanRpm;
                    CurrentItsm = _hal.ReadEcPort(0xE4);
                    EcFanTargetLarge = _hal.ReadEcPort(0x5E);
                    EcFanTargetSmall = _hal.ReadEcPort(0x5A);
                    EcFanShadowLarge = _hal.ReadEcPort(0x5D);
                    EcFanShadowSmall = _hal.ReadEcPort(0x59);
                }
                catch { }

                int expectedLarge = _lastLargeTarget * 100;
                bool fanDeviated = ActualCpuFanRpm > 0 &&
                    Math.Abs(ActualCpuFanRpm - expectedLarge) > 500;

                if (!fanDeviated)
                {
                    // 风扇恢复 → 切回用户原始模式
                    // 前端通过轮询 route-info 检测到 recovering: true→false 后
                    // 用 dispatchFullMode 的稀疏 overrides 重发用户自定义参数
                    _recovering = false;
                    _consecutiveDeviation = 0;
                    _recoveryCount++;
                    try
                    {
                        _wmi.SetThermalMode(_savedThermalMode);
                        Thread.Sleep(500);
                        _log.LogInformation(
                            "[FanCurve] 自动恢复 #{N}: 切回 SetThermalMode({Mode}), 等待前端重发参数",
                            _recoveryCount, _savedThermalMode);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("[FanCurve] 自动恢复切回异常: {Msg}", ex.Message);
                    }
                }
                else if (DateTime.UtcNow - _lastRecoveryTime > RecoveryCooldown)
                {
                    try
                    {
                        _wmi.SetThermalMode(_itsmCurveMode);
                        _hal.WriteEcPort(0xE4, _itsmCurveMode);
                        // 写入风扇目标转速，让 EC 知道我们要的速度
                        byte lt = (byte)_lastLargeTarget, st = (byte)_lastSmallTarget;
                        _wmi.SetFanManual(0, true);
                        _wmi.SetFanSpeed(0, lt);
                        _wmi.SetFanManual(1, true);
                        _wmi.SetFanSpeed(1, st);
                        _hal.WriteEcPort(0x5E, lt);
                        _hal.WriteEcPort(0x5A, st);
                        _lastRecoveryTime = DateTime.UtcNow;
                        _log.LogInformation("[FanCurve] 自动恢复: 重试解锁 SetThermalMode({Mode})", _itsmCurveMode);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("[FanCurve] 重试解锁异常: {Msg}", ex.Message);
                    }
                }
                return; // 解锁期间不做任何其他操作
            }

            // 3. ShouldWrite 回差策略 — 决定是否更新目标值
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

            // 4. ITSM 读回诊断
            byte currentItsm = _hal.ReadEcPort(0xE4);
            CurrentItsm = currentItsm;
            if (currentItsm != _itsmCurveMode) _itsmDeviationCount++;
            RoutedMode = _itsmCurveMode;

            // 5. 只在目标变化时写入 ITSM + WMI 风扇 + EC 直写
            bool largeOk = true, smallOk = true;
            if (targetChanged)
            {
                _hal.WriteEcPort(0xE4, _itsmCurveMode);
                _wmi.SetFanManual(0, true);
                largeOk = _wmi.SetFanSpeed(0, (byte)largeTarget);
                _wmi.SetFanManual(1, true);
                smallOk = _wmi.SetFanSpeed(1, (byte)smallTarget);
                _hal.WriteEcPort(0x5E, (byte)largeTarget);
                _hal.WriteEcPort(0x5A, (byte)smallTarget);
            }

            // 6. 读回诊断
            TickCount++;
            LastWmiLargeOk = largeOk;
            LastWmiSmallOk = smallOk;
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

            // 7. 自动恢复：偏离累计 ≥ 阈值 → 触发解锁
            {
                bool fanDeviated = ActualCpuFanRpm > 0 &&
                    Math.Abs(ActualCpuFanRpm - largeTarget * 100) > 500;

                if (fanDeviated) _consecutiveDeviation++;
                else _consecutiveDeviation = 0;

                if (_consecutiveDeviation >= DeviationThreshold &&
                    DateTime.UtcNow - _lastRecoveryTime > RecoveryCooldown)
                {
                    _recovering = true;
                    try
                    {
                        _wmi.SetThermalMode(_itsmCurveMode);
                        _hal.WriteEcPort(0xE4, _itsmCurveMode);
                        // 写入风扇目标转速，让 EC 知道我们要的速度
                        byte lt2 = (byte)largeTarget, st2 = (byte)smallTarget;
                        _wmi.SetFanManual(0, true);
                        _wmi.SetFanSpeed(0, lt2);
                        _wmi.SetFanManual(1, true);
                        _wmi.SetFanSpeed(1, st2);
                        _hal.WriteEcPort(0x5E, lt2);
                        _hal.WriteEcPort(0x5A, st2);
                        _lastRecoveryTime = DateTime.UtcNow;
                        _log.LogInformation(
                            "[FanCurve] 自动恢复: 解锁 SetThermalMode({Mode}) RPM={Rpm}/{Target}",
                            _itsmCurveMode, ActualCpuFanRpm, largeTarget * 100);
                    }
                    catch (Exception ex)
                    {
                        _recovering = false;
                        _log.LogWarning("[FanCurve] 自动恢复解锁异常: {Msg}", ex.Message);
                    }
                }
            }

            _log.LogInformation(
                "[FanCurve] Tick: hot={Hot}°C → curve={Mode}({ModeName}) L={L}x100 S={S}x100 | WMI: l={Lr} s={Sr} | ITSM={Itsm} | EC: 5E={EcL} 5A={EcS} 5D={EcD} 59={Ec9} RPM={CpuRpm}/{GpuRpm}{Rec}",
                hotspot, _itsmCurveMode, ModeName(_itsmCurveMode), largeTarget, smallTarget,
                largeOk, smallOk, currentItsm,
                EcFanTargetLarge, EcFanTargetSmall, EcFanShadowLarge, EcFanShadowSmall,
                ActualCpuFanRpm, ActualGpuFanRpm, _recovering ? " [解锁中]" : "");
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
