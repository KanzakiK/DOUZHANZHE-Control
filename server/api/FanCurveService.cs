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

    // ShouldWrite 状态跟踪 (对应 BellatorFanControl 的 lastHotspot / lastCpuGpuTarget / lastSysTarget)
    private int? _lastHotspot;
    private int _lastLargeTarget;     // 上次写入的大扇目标 (RPM/100 单位)
    private int _lastSmallTarget;     // 上次写入的小扇目标 (RPM/100 单位)

    public bool Active => _active;

    /// <summary>曲线点：温度(°C) → 大扇/小扇目标 RPM</summary>
    /// <remarks>默认曲线对齐 BellatorFanControl LoadDefaultCurve</remarks>
    public List<FanCurvePoint> Points { get; private set; } = new()
    {
        new(50, 2200, 2000),
        new(55, 2600, 3500),
        new(60, 2900, 4800),
        new(65, 3200, 5900),
        new(70, 3500, 6400),
        new(75, 3800, 6900),
        new(80, 4000, 7500),
        new(85, 4300, 8000),
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
        _configDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "config"));
        if (!Directory.Exists(_configDir))
            _configDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
        Directory.CreateDirectory(_configDir);
    }

    // ── 启停控制 ──

    public void Start(int intervalMs = 5000, int hysteresisC = 3)
    {
        if (_active) return;
        _active = true;
        _intervalMs = Math.Max(1000, intervalMs);
        _hysteresisC = hysteresisC;
        _lastHotspot = null;
        _lastLargeTarget = 0;
        _lastSmallTarget = 0;

        _timer = new Timer(Tick, null, 0, _intervalMs);
        _log.LogInformation("[FanCurve] 自定义曲线已启动, 间隔 {Ms}ms, 回差 {H}°C", _intervalMs, _hysteresisC);
    }

    public void Stop()
    {
        if (!_active) return;
        _active = false;
        _timer?.Dispose();
        _timer = null;
        _lastHotspot = null;

        // 恢复固件风扇控制
        _wmi.SetFanManual(false);
        _log.LogInformation("[FanCurve] 自定义曲线已停止, 固件控制已恢复");
    }

    public void Dispose() => Stop();

    // ── 定时回调 (核心逻辑，对齐 BellatorFanControl.PollAndApply) ──

    private void Tick(object? state)
    {
        if (!_active) return;
        try
        {
            int cpuTemp = _hal.CpuTemperature;
            int gpuTemp = _hal.GpuTemperature;
            int hotspot = Math.Max(cpuTemp, gpuTemp);

            if (hotspot <= 0) return; // 温度无效

            // 查找目标曲线点
            var (largeRpm, smallRpm) = LookupTarget(hotspot);
            int largeTarget = Math.Clamp(largeRpm / 100, 0, 44); // Bellator 协议: RPM/100
            int smallTarget = Math.Clamp(smallRpm / 100, 0, 82);

            // ShouldWrite: 精确复现 BellatorFanControl 的回差策略
            if (!ShouldWrite(hotspot, largeTarget, smallTarget)) return;

            // 每次写入都重新启用手动模式 (对抗 EC 回写覆盖)
            _wmi.SetFanManual(true);
            _wmi.SetFanSpeed(0, (byte)largeTarget); // FanType 0 = 大扇 (CPUGPUFan)
            _wmi.SetFanSpeed(1, (byte)smallTarget); // FanType 1 = 小扇 (SYSFan)

            _lastHotspot = hotspot;
            _lastLargeTarget = largeTarget;
            _lastSmallTarget = smallTarget;

            _log.LogDebug("[FanCurve] hotspot={Hot}°C → large={L}x100rpm small={S}x100rpm",
                hotspot, largeTarget, smallTarget);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[FanCurve] tick 异常: {Msg}", ex.Message);
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
