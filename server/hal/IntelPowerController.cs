// SPDX-License-Identifier: MIT
//
// IntelPowerController — Intel MSR 功耗/温度控制器
// 通过 PawnIO IntelMSR.bin 读写 MSR，实现 ISmuControl 接口

namespace Douzhanzhe.HAL;

public sealed class IntelPowerController : ISmuControl, IDisposable
{
    private readonly PawnIoDevice _pawnIo;
    private bool _msrLocked; // MSR 0x610 bit 63

    // MSR 地址
    private const uint MsrPkgPowerLimit = 0x610;
    private const uint MsrPkgPowerInfo = 0x614;
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrIa32ThermStatus = 0x19C;
    private const uint MsrIa32TemperatureTarget = 0x1A2;
    private const uint MsrPlatformInfo = 0xCE;
    private const uint MsrIa32PerfStatus = 0x198;
    private const uint MsrMperf = 0xE7;
    private const uint MsrAperf = 0xE8;

    /// <summary>IntelMSR.bin 是否已成功加载</summary>
    public bool IsLoaded { get; private set; }

    public IntelPowerController()
    {
        var binDir = LocateBinDir();
        if (binDir == null)
            throw new FileNotFoundException("PawnIO .bin 目录未找到");

        var binPath = System.IO.Path.Combine(binDir, "IntelMSR.bin");
        if (!System.IO.File.Exists(binPath))
            throw new FileNotFoundException($"IntelMSR.bin 不存在 ({binPath})");

        var device = PawnIoDevice.LoadModuleFromFile(binPath);
        if (!device.IsLoaded)
            throw new InvalidOperationException("加载 IntelMSR.bin 失败");

        _pawnIo = device;
        IsLoaded = true;
    }

    public IntelPowerController(PawnIoDevice pawnIo)
    {
        _pawnIo = pawnIo ?? throw new ArgumentNullException(nameof(pawnIo));
        IsLoaded = true;
    }

    static string? LocateBinDir()
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "PawnIO"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "assets", "PawnIO"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "PawnIO"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "..", "assets", "PawnIO"),
        };
        foreach (var c in candidates)
        {
            var full = System.IO.Path.GetFullPath(c);
            if (System.IO.Directory.Exists(full))
                return full;
        }
        return null;
    }

    // ---- MSR 读写 ----

    ulong ReadMsr(uint msrAddr)
    {
        var result = _pawnIo.Execute("ioctl_read_msr", [msrAddr], 2);
        if (result.Length < 2) return 0;
        // 返回值为 64 位: low 32 bits + high 32 bits
        return (ulong)(uint)result[0] | ((ulong)(uint)result[1] << 32);
    }

    void WriteMsr(uint msrAddr, ulong value)
    {
        _pawnIo.Execute("ioctl_write_msr", [(long)(uint)(value & 0xFFFFFFFF), (long)(uint)(value >> 32)], 0);
    }

    // ---- RAPL 工具 ----

    double GetPowerUnit()
    {
        var val = ReadMsr(MsrRaplPowerUnit);
        // bits [3:0] = power unit (瓦)
        var unit = (val & 0xF) > 0 ? 1.0 / (1 << (int)(val & 0xF)) : 0.125;
        return unit;
    }

    // ---- ISmuControl 实现 ----

    public int SetPowerLimit(uint mW)
    {
        if (_msrLocked) return -1;
        try
        {
            var unit = GetPowerUnit();
            var raw = (ulong)Math.Round(mW / 1000.0 / unit);
            if (raw > 0x7FFF) raw = 0x7FFF;

            var current = ReadMsr(MsrPkgPowerLimit);
            // 检查 Lock 位
            if ((current & (1UL << 63)) != 0)
            {
                _msrLocked = true;
                return -1;
            }

            // 保留非 PL1 字段，替换 PL1 功耗值 + 启用位
            var pl1 = (current & 0xFFFF0000FFFF0000) | (raw & 0x7FFF) | (1UL << 24) /* Enable */;
            WriteMsr(MsrPkgPowerLimit, pl1);
            return 0;
        }
        catch { return -1; }
    }

    public int SetTempLimit(uint celsius)
    {
        // IntelMSR.bin 不允许写 0x1FC
        return -1; // NotSupported
    }

    public int SetShortPowerLimit(uint fastMw, uint slowMw)
    {
        if (_msrLocked) return -1;
        try
        {
            var unit = GetPowerUnit();
            var rawFast = (ulong)Math.Round(fastMw / 1000.0 / unit);
            var rawSlow = (ulong)Math.Round(slowMw / 1000.0 / unit);
            if (rawFast > 0x7FFF) rawFast = 0x7FFF;
            if (rawSlow > 0x7FFF) rawSlow = 0x7FFF;

            var current = ReadMsr(MsrPkgPowerLimit);
            if ((current & (1UL << 63)) != 0) { _msrLocked = true; return -1; }

            // PL1 = slow, PL2 = fast
            var pl1 = (rawSlow & 0x7FFF) | (1UL << 24) /* PL1 Enable */;
            var pl2 = (rawFast & 0x7FFF) << 32 | (1UL << 56) /* PL2 Enable */;
            var combined = (current & 0xFFFE0000FFFE0000) | pl1 | pl2;
            WriteMsr(MsrPkgPowerLimit, combined);
            return 0;
        }
        catch { return -1; }
    }

    public int SetCurveOptimizer(int mV) => -1; // Intel 不支持
    public int SetTurboDisabled(bool disabled) => -1; // IntelMSR.bin 不允许写 0x1A0
    // 逐核 Curve Optimizer 是 AMD SMU 专属能力，Intel 一律返回空结果
    public IReadOnlyList<PerCoreCoResult> ApplyPerCoreCO(IEnumerable<PerCoreCoEntry> entries) => Array.Empty<PerCoreCoResult>();
    public IReadOnlyList<PerCoreCoResult> ResetPerCoreCO() => Array.Empty<PerCoreCoResult>();
    public PerCoreCoProbeResult ProbePerCoreCO() => new() { Ok = false, Status = 0, Error = "仅 AMD 平台支持逐核 CO" };

    public int BatchApply(uint? stapmMw, uint? fastMw, uint? slowMw,
                          uint? tempC, int? coAllMv, bool? turboOff)
    {
        if (stapmMw.HasValue && SetPowerLimit(stapmMw.Value) != 0) return -1;
        if (fastMw.HasValue || slowMw.HasValue)
        {
            var f = fastMw ?? 0;
            var s = slowMw ?? 0;
            if (SetShortPowerLimit(f, s) != 0) return -1;
        }
        // temp/CO/turbo 在 Intel 上不支持，跳过
        return 0;
    }

    public bool Probe()
    {
        try
        {
            // 读取 PL1/PL2 信息验证 MSR 可达
            var val = ReadMsr(MsrPkgPowerInfo);
            if (val == 0) return false;
            return true;
        }
        catch { return false; }
    }

    public PlatformCapabilities GetCapabilities() => PlatformCapabilities.IntelLimited;

    public void Dispose()
    {
        _pawnIo.Dispose();
    }
}
