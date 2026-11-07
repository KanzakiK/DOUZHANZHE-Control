// SPDX-License-Identifier: MIT
//
// ISmuControl — SMU/功耗控制抽象接口
// 两平台共同实现: AmdSmuController (AMD SMU) / IntelPowerController (Intel MSR)

namespace Douzhanzhe.HAL;

public interface ISmuControl
{
    /// <summary>设置长时功耗限制 (STAPM/PL1)，单位 mW</summary>
    int SetPowerLimit(uint mW);

    /// <summary>设置温度墙，单位 °C</summary>
    int SetTempLimit(uint celsius);

    /// <summary>设置短时功耗限制 (PPT fast/slow)，单位 mW</summary>
    int SetShortPowerLimit(uint fastMw, uint slowMw);

    /// <summary>全核 Curve Optimizer 偏移，单位 mV，负数=降压</summary>
    int SetCurveOptimizer(int mV);

    /// <summary>关闭睿频</summary>
    int SetTurboDisabled(bool disabled);

    /// <summary>批量应用（单次调用完成多项设置，效率更高）</summary>
    int BatchApply(uint? stapmMw, uint? fastMw, uint? slowMw,
                   uint? tempC, int? coAllMv, bool? turboOff);

    /// <summary>逐核 Curve Optimizer（AMD 实现，Intel 返回空结果）</summary>
    /// <remarks>逐条返回状态码，不能只返回一个总 ok：固件可能只拒绝部分核心。</remarks>
    IReadOnlyList<PerCoreCoResult> ApplyPerCoreCO(IEnumerable<PerCoreCoEntry> entries);

    /// <summary>把全部核心的逐核 CO 写回 0（清空用，不依赖固件自行恢复）</summary>
    IReadOnlyList<PerCoreCoResult> ResetPerCoreCO();

    /// <summary>只读探针：读取 CO 能力/参数选项，不写入任何电压曲线</summary>
    PerCoreCoProbeResult ProbePerCoreCO();

    /// <summary>探测 SMU/MSR 是否可达</summary>
    bool Probe();

    /// <summary>返回当前平台的能力集</summary>
    PlatformCapabilities GetCapabilities();
}

/// <summary>拓扑与范围：8940HX = 2 CCD × 8 核。前端通过 /api/smu/co-per-core 读取，不写死。</summary>
/// <remarks>
/// 正偏移（加压）在 N176 BIOS 上未实机验证，暂只开放与现有全核滑块一致的 -30..0。
/// 未来扩展机型时只需改这里（或按 CPUID/APIC ID 动态探测），不要散落到前端。
/// </remarks>
public static class PerCoreCoTopology
{
    public const int CcdCount = 2;
    public const int CoresPerCcd = 8;
    public const int MinOffsetMv = -30;
    public const int MaxOffsetMv = 0;

    public static bool IsValid(int ccd, int core, int mv)
        => ccd >= 0 && ccd < CcdCount
           && core >= 0 && core < CoresPerCcd
           && mv >= MinOffsetMv && mv <= MaxOffsetMv;

    /// <summary>枚举全部核心（清零用）</summary>
    public static List<PerCoreCoEntry> AllCores(int mv = 0)
    {
        var list = new List<PerCoreCoEntry>(CcdCount * CoresPerCcd);
        for (int ccd = 0; ccd < CcdCount; ccd++)
            for (int core = 0; core < CoresPerCcd; core++)
                list.Add(new PerCoreCoEntry { Ccd = ccd, Core = core, Mv = mv });
        return list;
    }
}

/// <summary>单个核心的 CO 设置（同时用于 API 请求与 profiles/*.json 持久化）</summary>
public sealed class PerCoreCoEntry
{
    public int Ccd;
    public int Core;
    public int Mv;

    public PerCoreCoEntry Clone() => new() { Ccd = Ccd, Core = Core, Mv = Mv };
}

/// <summary>单个核心的下发结果。Status 对应 RyzenSmu.SmuStatus 的字节值。</summary>
public sealed class PerCoreCoResult
{
    public int Ccd;
    public int Core;
    public int Mv;
    public byte Status;
    public string Mailbox = "";
    public uint Arg;
}

/// <summary>只读探针结果</summary>
public sealed class PerCoreCoProbeResult
{
    public bool Ok;
    public byte Status;
    public string Mailbox = "rsmu";
    public uint[] Args = Array.Empty<uint>();
    public string? Error;
}

/// <summary>UXTU 0229d72 PresetCommandBuilder.BuildCoperArg 的等价实现</summary>
/// <remarks>
/// 非 HSMP 32 位参数：
///   31:28 CCD | 27:24 8 核分组 | 23:20 组内核心 | 19:0 CO 偏移
///   负值 = 0x100000 - abs(offset)，正值 = 原值
/// </remarks>
public static class PerCoreCoCodec
{
    public static uint EncodeOffset(int mv)
    {
        int magnitude = Math.Min(Math.Abs(mv), 0xFFFFF);
        return mv < 0
            ? (uint)((0x100000 - magnitude) & 0xFFFFF)
            : (uint)(magnitude & 0xFFFFF);
    }

    public static uint EncodeSelector(int ccd, int core)
        => (uint)((((ccd << 4) | ((core / 8) & 0xF)) << 4 | (core % 8 & 0xF)) << 20);

    public static uint Encode(int ccd, int core, int mv)
        => EncodeSelector(ccd, core) | EncodeOffset(mv);
}

/// <summary>平台能力集，前端据此决定渲染哪些控件</summary>
public record PlatformCapabilities
{
    public bool PowerLimit { get; init; }
    public bool TempLimit { get; init; }
    public bool ShortPowerLimit { get; init; }
    public bool CurveOptimizer { get; init; }
    public bool PerCoreCO { get; init; }
    public bool TurboDisabled { get; init; }
    public bool CoreLimit { get; init; }

    /// <summary>AMD 平台完整能力</summary>
    public static PlatformCapabilities AmdFull => new()
    {
        PowerLimit = true,
        TempLimit = true,
        ShortPowerLimit = true,
        CurveOptimizer = true,
        PerCoreCO = true,
        TurboDisabled = true,
        CoreLimit = true,
    };

    /// <summary>Intel 平台精简能力（受 MSR 写白名单限制）</summary>
    public static PlatformCapabilities IntelLimited => new()
    {
        PowerLimit = true,
        TempLimit = false,
        ShortPowerLimit = true,
        CurveOptimizer = false,
        PerCoreCO = false,
        TurboDisabled = false,
        CoreLimit = true,
    };
}
