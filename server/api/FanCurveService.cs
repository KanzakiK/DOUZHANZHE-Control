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
//   - 每个写入 tick 都重新 SetFanManual(true)，对抗 EC 回写覆盖

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

    // ── ITSM 路由 + 守护状态 (NEW: EC 直写方案) ──
    private byte _savedThermalMode;           // 启动时保存的 ITSM 值，停止时恢复
    private int _modeChangeCount;             // 统计：模式路由切换次数
    private int _itsmDeviationCount;          // 统计：ITSM 偏离次数
    private DateTime _itsmDeviationWindowStart = DateTime.Now; // 偏离计数窗口起始
    private int _itsmDeviationInWindow;       // 当前窗口内偏离次数
    private bool _wmiChannelLocked;           // WMI 风扇写入通道锁定检测
    private int _wmiWriteFailStreak;          // 连续写入失败计数

    public bool Active => _active;

    // ── API 查询属性 (供 /api/fan-curve/route-info 使用) ──
    public byte CurrentItsm { get; private set; }
    public byte RoutedMode { get; private set; }
    public int LastLargeTarget => _lastLargeTarget;
    public int LastSmallTarget => _lastSmallTarget;
    public int ModeChangeCount => _modeChangeCount;
    public int ItsmDeviationCount => _itsmDeviationCount;
    public bool WmiChannelLocked => _wmiChannelLocked;

    // 各模式风扇转速合法区间 (RPM/100 单位，与前端 FAN_RANGES 对齐)
    // EC 直写方案中：从钳位器变为路由表 — 根据目标转速找到能覆盖它的模式
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
    }

    // ── 启停控制 ──

    public void Start(int? intervalMs = null, int? hysteresisC = null)
    {
        if (_active) return;
        _active = true;
        _intervalMs = Math.Max(1000, intervalMs ?? _intervalMs);
        _hysteresisC = hysteresisC ?? _hysteresisC;
        _lastHotspot = null;
        _lastLargeTarget = 0;
        _lastSmallTarget = 0;

        // 保存当前 ITSM，停止时通过 WMI 恢复正常模式链
        _savedThermalMode = _hal.ReadEcPort(0xE4);
        _modeChangeCount = 0;
        _itsmDeviationCount = 0;
        _itsmDeviationInWindow = 0;
        _itsmDeviationWindowStart = DateTime.Now;
        _wmiChannelLocked = false;
        _wmiWriteFailStreak = 0;

        _timer = new Timer(Tick, null, 0, _intervalMs);
        _log.LogInformation("[FanCurve] 自定义曲线已启动, 间隔 {Ms}ms, 回差 {H}°C, 保存ITSM={Itsm}",
            _intervalMs, _hysteresisC, _savedThermalMode);
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
            _log.LogInformation("[FanCurve] 自定义曲线已停止, 恢复到模式 {Mode}({Val}), 模式切换 {MC} 次, ITSM偏离 {DC} 次",
                ModeName(_savedThermalMode), _savedThermalMode, _modeChangeCount, _itsmDeviationCount);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[FanCurve] 停止恢复异常: {Msg}", ex.Message);
        }
    }

    /// <summary>显式恢复固件风扇控制（进程退出时调用）</summary>
    public void RestoreFirmwareControl()
    {
        _wmi.SetFanManual(0, false);
        _wmi.SetFanManual(1, false);
        _log.LogInformation("[FanCurve] 固件风扇控制已恢复");
    }

    public void Dispose() => RestoreFirmwareControl();

    // ── 模式路由 (NEW: 从钳位器到路由表) ──

    /// <summary>
    /// 根据目标大扇/小扇 RPM 找到能同时覆盖两者的最优模式。
    /// 优先级：安静(2) → 均衡(0) → 野兽(1) → 斗战(3)
    /// 无完美匹配时优先满足大扇（对 CPU 温度影响更大），fallback 斗战(3)。
    /// </summary>
    private static byte RouteMode(int largeTargetDiv100, int smallTargetDiv100)
    {
        byte[] priority = { 2, 0, 1, 3 };

        // 第一轮：找同时覆盖大扇和小扇的模式
        foreach (var mode in priority)
        {
            var range = ModeFanRanges[mode];
            if (largeTargetDiv100 >= range.lMin && largeTargetDiv100 <= range.lMax &&
                smallTargetDiv100 >= range.sMin && smallTargetDiv100 <= range.sMax)
                return mode;
        }

        // 第二轮：无完美匹配，优先满足大扇
        foreach (var mode in priority)
        {
            var range = ModeFanRanges[mode];
            if (largeTargetDiv100 >= range.lMin && largeTargetDiv100 <= range.lMax)
                return mode;
        }

        return 3; // fallback: 斗战模式（最宽上限）
    }

    // ── 定时回调 (核心逻辑：路由 + EC 直写 ITSM + WMI 写转速) ──

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

            // 2. 模式路由：根据目标转速找到最优 ITSM 模式
            byte targetMode = RouteMode(largeTarget, smallTarget);

            // 3. 读取当前 ITSM，按需 EC 直写（不触发 ALIB/GPUD 副作用链）
            byte currentItsm = _hal.ReadEcPort(0xE4);
            CurrentItsm = currentItsm;

            if (currentItsm != targetMode)
            {
                // 检查是否为外部偏离（Fn 热键 / AC 插拔 / 睡眠唤醒）
                if (_active && _lastHotspot.HasValue) // 非首次 tick
                {
                    _itsmDeviationCount++;
                    _itsmDeviationInWindow++;

                    // 1 分钟窗口内偏离统计
                    if ((DateTime.Now - _itsmDeviationWindowStart).TotalSeconds > 60)
                    {
                        _itsmDeviationWindowStart = DateTime.Now;
                        _itsmDeviationInWindow = 1;
                    }
                    if (_itsmDeviationInWindow >= 5)
                    {
                        _log.LogWarning("[FanCurve] ITSM 频繁偏离: 1分钟内 {Count} 次 (cur={Cur} tgt={Tgt})",
                            _itsmDeviationInWindow, currentItsm, targetMode);
                    }
                }

                // EC 直写 ITSM — 绕开 WMAA Case 0x0800 的完整副作用链
                _hal.WriteEcPort(0xE4, targetMode);
                _modeChangeCount++;
                _log.LogInformation("[FanCurve] ITSM 路由: {Cur}({CurName}) → {Tgt}({TgtName}) (L={L} S={S})",
                    currentItsm, ModeName(currentItsm), targetMode, ModeName(targetMode), largeRpm, smallRpm);
                // 短暂等待 EC 切换风扇曲线区间（~100ms）
                Thread.Sleep(100);
            }

            RoutedMode = targetMode;

            // 4. 钳位到目标模式的合法区间（防止 EC 拒绝写入）
            if (ModeFanRanges.TryGetValue(targetMode, out var range))
            {
                largeTarget = Math.Clamp(largeTarget, range.lMin, range.lMax);
                smallTarget = Math.Clamp(smallTarget, range.sMin, range.sMax);
            }

            // 5. ShouldWrite 回差策略
            if (ShouldWrite(hotspot, largeTarget, smallTarget))
            {
                _lastHotspot = hotspot;
                _lastLargeTarget = largeTarget;
                _lastSmallTarget = smallTarget;
            }
            else
            {
                // 回差范围内：保持上次的目标值
                largeTarget = _lastLargeTarget;
                smallTarget = _lastSmallTarget;
            }

            // 6. WMI 写入风扇转速（每个 tick 都写入，对抗 EC 回写覆盖）
            bool manual0 = _wmi.SetFanManual(0, true);
            bool largeOk = _wmi.SetFanSpeed(0, (byte)largeTarget);
            bool manual1 = _wmi.SetFanManual(1, true);
            bool smallOk = _wmi.SetFanSpeed(1, (byte)smallTarget);

            // 7. 写入验证：读回 EC 寄存器确认值生效
            VerifyFanWrite(largeTarget, smallTarget, largeOk, smallOk);

            _log.LogInformation(
                "[FanCurve] Tick: hot={Hot}°C → route={Mode}({ModeName}) L={L}x100 S={S}x100 | WMI: m0={M0} l={Lr} m1={M1} s={Sr} | ITSM={Itsm}",
                hotspot, targetMode, ModeName(targetMode), largeTarget, smallTarget,
                manual0, largeOk, manual1, smallOk, currentItsm);
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
    /// WMI 风扇写入验证 + 通道锁定检测。
    /// 写入后读回 EC 0x5E(大扇)/0x5A(小扇) 确认值生效。
    /// 连续 3 次写入后不匹配 → 标记 WMI 通道为"锁定"（可能由 CPU 频率限制导致）。
    /// </summary>
    private void VerifyFanWrite(int expectedLarge, int expectedSmall, bool wmiLargeOk, bool wmiSmallOk)
    {
        // WMI 返回 false 或 EC 读回值不匹配 → 计数
        if (!wmiLargeOk || !wmiSmallOk)
        {
            _wmiWriteFailStreak++;
            if (_wmiWriteFailStreak >= 3 && !_wmiChannelLocked)
            {
                _wmiChannelLocked = true;
                _log.LogWarning("[FanCurve] WMI 风扇写入通道疑似锁定 (连续 {N} 次 WMI 返回失败)",
                    _wmiWriteFailStreak);
            }
            return;
        }

        // WMI 返回 OK，进一步读回 EC 验证（给 EC 100ms 处理时间）
        Thread.Sleep(100);
        byte actualLarge = _hal.ReadEcPort(0x5E);
        byte actualSmall = _hal.ReadEcPort(0x5A);

        // 允许 ±1 偏差（EC 可能四舍五入）
        bool largeMatch = Math.Abs(actualLarge - expectedLarge) <= 1;
        bool smallMatch = Math.Abs(actualSmall - expectedSmall) <= 1;

        if (!largeMatch || !smallMatch)
        {
            _wmiWriteFailStreak++;
            _log.LogDebug("[FanCurve] 写入验证偏差: large exp={E} act={A} small exp={E2} act={A2} streak={S}",
                expectedLarge, actualLarge, expectedSmall, actualSmall, _wmiWriteFailStreak);
            if (_wmiWriteFailStreak >= 3 && !_wmiChannelLocked)
            {
                _wmiChannelLocked = true;
                _log.LogWarning("[FanCurve] WMI 风扇写入通道锁定检测: 连续 {N} 次写入后 EC 值不匹配",
                    _wmiWriteFailStreak);
            }
        }
        else
        {
            // 写入成功，重置失败计数
            if (_wmiChannelLocked)
            {
                _wmiChannelLocked = false;
                _log.LogInformation("[FanCurve] WMI 风扇写入通道已恢复");
            }
            _wmiWriteFailStreak = 0;
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
