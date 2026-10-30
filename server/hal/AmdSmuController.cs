// SPDX-License-Identifier: MIT
//
// AmdSmuController — AMD SMU 控制器（原生 SMU 邮箱通信）
// 替代旧 SmuController (ryzenadj.exe 子进程)
// 实现 ISmuControl 接口

namespace Douzhanzhe.HAL;

public sealed class AmdSmuController : ISmuControl, IDisposable
{
    private readonly RyzenSmu _smu;

    /// <summary>创建 AmdSmuController，需先加载 RyzenSMU.bin</summary>
    public AmdSmuController()
    {
        var binDir = LocateBinDir();
        if (binDir == null)
            throw new FileNotFoundException("RyzenSMU.bin 目录未找到");

        var binPath = System.IO.Path.Combine(binDir, "RyzenSMU.bin");
        if (!System.IO.File.Exists(binPath))
            throw new FileNotFoundException($"RyzenSMU.bin 不存在 ({binPath})");

        var device = PawnIoDevice.LoadModuleFromFile(binPath);
        if (!device.IsLoaded)
            throw new InvalidOperationException("加载 RyzenSMU.bin 失败");

        _smu = new RyzenSmu(device);
        _smu.Open();
        _smu.InitAm5V1();
    }

    /// <summary>创建 AmdSmuController（注入已加载的 RyzenSMU）</summary>
    public AmdSmuController(RyzenSmu smu)
    {
        _smu = smu ?? throw new ArgumentNullException(nameof(smu));
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

    // ---- ISmuControl 实现 ----

    public int SetPowerLimit(uint mW)
    {
        // mW → 毫瓦参数，SMU 接受毫瓦值
        var args = new uint[] { mW };
        var status = _smu.SendMp1(SmuCommands.StapmLimit, ref args);
        return status == RyzenSmu.SmuStatus.Ok ? 0 : -1;
    }

    public int SetTempLimit(uint celsius)
    {
        var args = new uint[] { celsius };
        var status = _smu.SendMp1(SmuCommands.TctlTemp, ref args);
        return status == RyzenSmu.SmuStatus.Ok ? 0 : -1;
    }

    public int SetShortPowerLimit(uint fastMw, uint slowMw)
    {
        var fastArgs = new uint[] { fastMw };
        var slowArgs = new uint[] { slowMw };
        var fastOk = _smu.SendMp1(SmuCommands.FastLimit, ref fastArgs);
        var slowOk = _smu.SendMp1(SmuCommands.SlowLimit, ref slowArgs);
        return (fastOk == RyzenSmu.SmuStatus.Ok && slowOk == RyzenSmu.SmuStatus.Ok) ? 0 : -1;
    }

    public int SetCurveOptimizer(int mV)
    {
        // CO 编码: 0x100000 - abs(offset)
        var encoded = (uint)(0x100000 - Math.Abs(mV));
        var args = new uint[] { encoded };
        var status = _smu.SendMp1(SmuCommands.SetCoAll, ref args);
        return status == RyzenSmu.SmuStatus.Ok ? 0 : -1;
    }

    public int SetTurboDisabled(bool disabled)
    {
        var cmd = disabled ? SmuCommands.PowerSaving : SmuCommands.MaxPerformance;
        var args = Array.Empty<uint>();
        var status = _smu.SendMp1(cmd, ref args);
        return status == RyzenSmu.SmuStatus.Ok ? 0 : -1;
    }

    public int BatchApply(uint? stapmMw, uint? fastMw, uint? slowMw,
                          uint? tempC, int? coAllMv, bool? turboOff)
    {
        if (stapmMw.HasValue && SetPowerLimit(stapmMw.Value) != 0) return -1;
        if (tempC.HasValue && SetTempLimit(tempC.Value) != 0) return -1;
        if (fastMw.HasValue || slowMw.HasValue)
        {
            var f = fastMw ?? 0;
            var s = slowMw ?? 0;
            if (SetShortPowerLimit(f, s) != 0) return -1;
        }
        if (coAllMv.HasValue && SetCurveOptimizer(coAllMv.Value) != 0) return -1;
        if (turboOff.HasValue && SetTurboDisabled(turboOff.Value) != 0) return -1;
        return 0;
    }

    public int SetPerCoreCO(int coreId, int offset)
    {
        // 编码: (coreId << 20) | (0x100000 - abs(offset))
        var encoded = (uint)(0x100000 - Math.Abs(offset));
        var cmd = ((uint)coreId << 20) | (encoded & 0xFFFFF);
        var args = new uint[] { cmd };
        var status = _smu.SendMp1(SmuCommands.SetCoPerCore, ref args);
        return status == RyzenSmu.SmuStatus.Ok ? 0 : -1;
    }

    public bool Probe()
    {
        // 发送无害读取命令验证 SMU 响应
        var args = Array.Empty<uint>();
        var status = _smu.SendRsmu(0xDc, ref args); // get-pbo-fused-power-limit
        return status == RyzenSmu.SmuStatus.Ok;
    }

    public PlatformCapabilities GetCapabilities() => PlatformCapabilities.AmdFull;

    public void Dispose()
    {
        _smu.Dispose();
    }
}
