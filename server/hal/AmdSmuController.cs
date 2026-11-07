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
        // CO 编码: 正值为原值，负值为 0x100000 - abs(offset)
        var encoded = PerCoreCoCodec.EncodeOffset(mV);
        // 保持原有 MP1 0x36 优先（不改变已生效路径），失败时才回退到 Dragon Range RSMU 0x07。
        var mp1Args = new uint[] { encoded };
        var status = _smu.SendMp1(SmuCommands.SetCoAll, ref mp1Args);
        if (status == RyzenSmu.SmuStatus.Ok) return 0;
        var rsmuArgs = new uint[] { encoded };
        var rsmu = _smu.SendRsmu(SmuCommands.SetCoAllRsmu, ref rsmuArgs);
        AppLog.Write("CO", $"coall mv={mV} encoded=0x{encoded:X8} mp1=0x{(byte)status:X2} rsmu=0x{(byte)rsmu:X2}");
        return rsmu == RyzenSmu.SmuStatus.Ok ? 0 : -1;
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

    /// <summary>逐核下发：RSMU 0x06 优先，失败回退 MP1 0x35（与 UXTU AM5 命令表一致）</summary>
    /// <remarks>必须串行：SMU 单邮箱，且 RyzenSmu 内部已用全局 PCI 互斥体保护。</remarks>
    public IReadOnlyList<PerCoreCoResult> ApplyPerCoreCO(IEnumerable<PerCoreCoEntry> entries)
    {
        var results = new List<PerCoreCoResult>();
        foreach (var e in entries)
        {
            var arg = PerCoreCoCodec.Encode(e.Ccd, e.Core, e.Mv);
            var args = new uint[] { arg };
            var status = _smu.SendRsmu(SmuCommands.SetCoPerCoreRsmu, ref args);
            var mailbox = "rsmu";

            if (status != RyzenSmu.SmuStatus.Ok)
            {
                // Dragon Range 用 RSMU 0x06；但 UXTU 的 AM5 通用表仍保留 MP1 0x35，
                // 且 Execute() 会逐个尝试到 OK 为止，这里照此做一次回退。
                var legacyArgs = new uint[] { arg };
                var legacy = _smu.SendMp1(SmuCommands.SetCoPerCore, ref legacyArgs);
                if (legacy == RyzenSmu.SmuStatus.Ok
                    || status is RyzenSmu.SmuStatus.UnknownCmd or RyzenSmu.SmuStatus.Failed or RyzenSmu.SmuStatus.Busy)
                {
                    status = legacy;
                    mailbox = "mp1";
                }
            }

            results.Add(new PerCoreCoResult
            {
                Ccd = e.Ccd,
                Core = e.Core,
                Mv = e.Mv,
                Status = (byte)status,
                Mailbox = mailbox,
                Arg = arg,
            });
            AppLog.Write("CO", $"coper ccd={e.Ccd} core={e.Core} mv={e.Mv} arg=0x{arg:X8} mailbox={mailbox} status=0x{(byte)status:X2}");
        }
        return results;
    }

    /// <summary>逐核写 0：清空覆盖层时显式复位，不依赖固件自行恢复</summary>
    public IReadOnlyList<PerCoreCoResult> ResetPerCoreCO()
        => ApplyPerCoreCO(PerCoreCoTopology.AllCores(0));

    /// <summary>只读探针：get-coper-options (RSMU 0xD5)，不写任何 CO 值</summary>
    public PerCoreCoProbeResult ProbePerCoreCO()
    {
        try
        {
            var args = new uint[6];
            var status = _smu.SendRsmu(SmuCommands.GetCoPerOptions, ref args);
            var result = new PerCoreCoProbeResult
            {
                Ok = status == RyzenSmu.SmuStatus.Ok,
                Status = (byte)status,
                Args = args,
            };
            AppLog.Write("CO", $"probe get-coper-options status=0x{(byte)status:X2} args=[{string.Join(",", args.Select(a => "0x" + a.ToString("X8")))}]");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Write("CO", $"probe failed: {ex.Message}");
            return new PerCoreCoProbeResult { Error = ex.Message };
        }
    }

    public bool Probe()
    {
        // 发送无害读取命令验证 SMU 响应
        var args = Array.Empty<uint>();
        var status = _smu.SendRsmu(SmuCommands.GetPboFusedPowerLimit, ref args);
        return status == RyzenSmu.SmuStatus.Ok;
    }

    public PlatformCapabilities GetCapabilities() => PlatformCapabilities.AmdFull;

    public void Dispose()
    {
        _smu.Dispose();
    }
}
