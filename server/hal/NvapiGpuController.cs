// SPDX-License-Identifier: MIT
// NvapiGpuController — NVAPI P/Invoke 封装 (nvapi64.dll)
// 基于 RTX 5060 Laptop GPU (Blackwell) 实测数据校准
// 函数 ID 来自 NVFC (github.com/graphitemaster/NVFC)

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Douzhanzhe.HAL;

#region ── 原生结构体 (Blackwell 校准版) ──

/// <summary>
/// NV_GPU_PERF_PSTATES20 — Blackwell layout (7416 bytes)
/// 用于 GetPStates20 (V3/7416, 含 ov 块)
/// 实测: state_count=5, clock_count=2, voltage_count=0
/// per_state = 456 bytes, clock entry = 44 bytes
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NV_GPU_PSTATES20
{
    public const int STRUCT_SIZE = 7416;   // V2/V3 统一大小
    public const int PER_STATE = 456;
    public const int CLK_ENTRY = 44;
    public const int CLK_DELTA_OFF = 12;

    public uint version;
    public uint flags;
    public uint state_count;
    public uint clock_count;
    public uint voltage_count;
    public fixed byte data[STRUCT_SIZE - 20]; // 7396 bytes

    public static uint MakeVersion() => (3u << 16) | (uint)STRUCT_SIZE;    // V3 — Get
    public static uint MakeVersionSet() => (2u << 16) | (uint)STRUCT_SIZE; // V2 — Set (unused now)

    public static byte* ClockPtr(byte* sp, int clkIdx) => sp + 8 + clkIdx * CLK_ENTRY;
    public static uint ClockDomain(byte* cp) => *(uint*)cp;
    public static int ClockDeltaValue(byte* cp) => *(int*)(cp + CLK_DELTA_OFF);
    public static void SetClockDelta(byte* cp, int kHz) => *(int*)(cp + CLK_DELTA_OFF) = kHz;
}

/// <summary>
/// NV_GPU_PERF_PSTATES20 V1 — NvAPIWrapper PerformanceStates20InfoV1 布局 (7316 bytes)
/// UXTU 超频使用: 从零构造, state_count=1, clock_count=2, 直接 Set (不先 Get)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NV_GPU_PSTATES20_V1
{
    public const int STRUCT_SIZE = 7316;   // V1: 无 ov 块
    public const int PER_STATE = 456;
    public const int CLK_ENTRY = 44;

    public uint version;
    public uint flags;
    public uint state_count;
    public uint clock_count;
    public uint voltage_count;
    public fixed byte data[STRUCT_SIZE - 20]; // 7296 bytes

    public static uint MakeVersion() => (1u << 16) | (uint)STRUCT_SIZE; // V1/7316
}

/// <summary>NV_GPU_CLOCK_FREQUENCIES (264 bytes, V2)</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_CLOCK_FREQUENCIES
{
    public uint version;
    public uint clock_type;
    public fixed byte entries[32 * 8]; // {present(u32), frequency(u32)} × 32

    public static uint MakeVersion() => (2u << 16) | 264u;
}

/// <summary>NV_GPU_POWER_POLICIES_INFO_V1 (184 bytes) — Blackwell</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_POWER_INFO
{
    public uint version;
    public uint flags;
    public fixed uint raw[44]; // (184 - 8) / 4

    public static uint MakeVersion() => (1u << 16) | 184u;
}

/// <summary>NV_GPU_POWER_POLICIES_STATUS_V1 (72 bytes) — Blackwell</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_POWER_STATUS
{
    public uint version;
    public uint count;
    public fixed uint raw[16]; // (72 - 8) / 4

    public static uint MakeVersion() => (1u << 16) | 72u;
}

/// <summary>NV_GPU_THERMAL_POLICIES_INFO_V2 (88 bytes) — Blackwell V1
/// entry: controller(4), min(4), default(4), max(4), flags(4) = 5 uint = 20 bytes
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_THERMAL_INFO
{
    public uint version;
    public uint flags;
    public fixed uint raw[20]; // (88 - 8) / 4 = 20

    public static uint MakeVersion() => (1u << 16) | 88u;

    // entry[i] offset: i * 5
    // entry[i].controller = raw[i*5 + 0]
    // entry[i].min = raw[i*5 + 1] (÷256 = °C)
    // entry[i].default = raw[i*5 + 2] (÷256 = °C)
    // entry[i].max = raw[i*5 + 3] (÷256 = °C)
    // entry[i].flags = raw[i*5 + 4]
}

/// <summary>NV_GPU_THERMAL_POLICIES_STATUS_V2 (40 bytes) — Blackwell V1
/// entry: controller(4), value(4), pad(4), pad(4), pad(4) = 5 uint = 20 bytes
/// 实测: entry[0] = {controller=1(GPU), value=22272(=87°C*256), pad, pad, pad}
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_THERMAL_STATUS
{
    public uint version;
    public uint count;
    public fixed uint raw[8]; // (40 - 8) / 4 = 8

    public static uint MakeVersion() => (1u << 16) | 40u;

    // entry[i] offset: i * 5
    // entry[i].controller = raw[i*5 + 0]
    // entry[i].value = raw[i*5 + 1] (÷256 = °C)
}

#endregion

#region ── NVAPI 函数 ID ──

internal static class NvApiId
{
    public const uint Initialize            = 0x0150E828;
    public const uint EnumPhysicalGPUs      = 0xE5AC921F;
    public const uint GPU_GetFullName       = 0x0CEEE8E9F;
    public const uint GPU_GetPStates20      = 0x6FF81213;
    public const uint GPU_SetPStates20      = 0x0F4DAE6B;
    public const uint GPU_GetAllClockFreq   = 0xDCB616C3;
    public const uint GPU_GetPowerInfo      = 0x34206D86;
    public const uint GPU_GetPowerStatus    = 0x70916171;
    public const uint GPU_SetPowerStatus    = 0x0AD95F5ED;
    public const uint GPU_GetThermalInfo    = 0x00D258BB5;
    public const uint GPU_GetThermalStatus  = 0x0E9C425A1;
    public const uint GPU_SetThermalStatus  = 0x034C0B13D;
}

#endregion

/// <summary>
/// NVAPI GPU 控制器 — 直接 P/Invoke nvapi64.dll
/// 基于 RTX 5060 Laptop GPU 实测数据
/// </summary>
public sealed class NvapiGpuController : IDisposable
{
    // ── 委托 ──
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NvApiVoid();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NvApiEnumGPUs(IntPtr[] h, ref int c);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NvApiName(IntPtr g, byte[] n);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NvApiPStates(IntPtr g, IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiClkFreq(IntPtr g, NV_GPU_CLOCK_FREQUENCIES* f);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiPwrInfo(IntPtr g, NV_GPU_POWER_INFO* i);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiPwrSt(IntPtr g, NV_GPU_POWER_STATUS* s);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiThrInfo(IntPtr g, NV_GPU_THERMAL_INFO* i);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiThrSt(IntPtr g, NV_GPU_THERMAL_STATUS* s);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr NvApiQI(uint id);

    private NvApiQI? _qi;
    private NvApiVoid? _init; private NvApiEnumGPUs? _enum; private NvApiName? _name;
    private NvApiPStates? _getPs, _setPs; private NvApiClkFreq? _clk;
    private NvApiPwrInfo? _pwrI; private NvApiPwrSt? _pwrG, _pwrS;
    private NvApiThrInfo? _thrI; private NvApiThrSt? _thrG, _thrS;

    private IntPtr _gpu; private bool _ok;
    public bool IsAvailable => _ok;
    public string GpuName { get; private set; } = "";
    public bool OverclockSupported { get; private set; }

    private const int OK = 0, ERR = -1, NOT_SUPPORTED = -104;

    public bool Init()
    {
        try
        {
            var hMod = NativeLibrary.Load("nvapi64.dll");
            _qi = Marshal.GetDelegateForFunctionPointer<NvApiQI>(NativeLibrary.GetExport(hMod, "nvapi_QueryInterface"));

            T Q<T>(uint id) where T : Delegate
            {
                var fp = _qi(id);
                return fp != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<T>(fp)
                    : throw new InvalidOperationException($"NVAPI 0x{id:X8} not found");
            }

            _init = Q<NvApiVoid>(NvApiId.Initialize);
            _enum = Q<NvApiEnumGPUs>(NvApiId.EnumPhysicalGPUs);
            _name = Q<NvApiName>(NvApiId.GPU_GetFullName);
            _getPs = Q<NvApiPStates>(NvApiId.GPU_GetPStates20);
            _setPs = Q<NvApiPStates>(NvApiId.GPU_SetPStates20);
            _clk = Q<NvApiClkFreq>(NvApiId.GPU_GetAllClockFreq);
            _pwrI = Q<NvApiPwrInfo>(NvApiId.GPU_GetPowerInfo);
            _pwrG = Q<NvApiPwrSt>(NvApiId.GPU_GetPowerStatus);
            _pwrS = Q<NvApiPwrSt>(NvApiId.GPU_SetPowerStatus);
            _thrI = Q<NvApiThrInfo>(NvApiId.GPU_GetThermalInfo);
            _thrG = Q<NvApiThrSt>(NvApiId.GPU_GetThermalStatus);
            _thrS = Q<NvApiThrSt>(NvApiId.GPU_SetThermalStatus);

            if (_init() != OK) { AppLog.Write("NVAPI", "Init failed"); return false; }

            var handles = new IntPtr[4]; int count = 0;
            if (_enum(handles, ref count) != OK || count == 0) return false;
            _gpu = handles[0];

            var nb = new byte[64]; _name(_gpu, nb);
            GpuName = Encoding.ASCII.GetString(nb).TrimEnd('\0');

            OverclockSupported = true;

            _ok = true;
            AppLog.Write("NVAPI", $"OK: {GpuName} | OC={OverclockSupported}");
            return true;
        }
        catch (Exception ex) { AppLog.Write("NVAPI", ex.Message); return false; }
    }

    // ── P-State 偏移 (超频/降频) ──

    /// <summary>读取 P0 时钟偏移 (kHz) — NvAPIWrapper Marshal 模式</summary>
    public unsafe (int coreKhz, int memKhz, bool ok) GetP0Offsets()
    {
        if (!_ok) return (0, 0, false);

        int sz = Marshal.SizeOf<NV_GPU_PSTATES20>();
        var ps = new NV_GPU_PSTATES20();
        ps.version = NV_GPU_PSTATES20.MakeVersion();
        var ptr = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(ps, ptr, false);
            if (_getPs!(_gpu, ptr) != OK) return (0, 0, false);

            byte* raw = (byte*)ptr;
            uint stateCount = *(uint*)(raw + 8);
            uint clockCount = *(uint*)(raw + 12);
            byte* data = raw + 20; // header: version(4)+flags(4)+states(4)+clocks(4)+voltages(4)

            int core = 0, mem = 0;
            for (int i = 0; i < (int)stateCount; i++)
            {
                var sp = data + i * NV_GPU_PSTATES20.PER_STATE;
                if (*(uint*)sp != 0) continue; // state_num != P0
                for (int j = 0; j < (int)clockCount && j < 8; j++)
                {
                    var cp = NV_GPU_PSTATES20.ClockPtr(sp, j);
                    var dom = NV_GPU_PSTATES20.ClockDomain(cp);
                    if (dom == 0) core = NV_GPU_PSTATES20.ClockDeltaValue(cp);
                    else if (dom == 4) mem = NV_GPU_PSTATES20.ClockDeltaValue(cp);
                }
                break;
            }
            return (core, mem, true);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>设置 P0 偏移 (MHz) — NVAPI SetPStates20 V2/7416</summary>
    public int SetP0Offset(int coreMhz, int memMhz)
    {
        if (!_ok || !OverclockSupported) return NOT_SUPPORTED;
        return SetP0OffsetNvapi(coreMhz, memMhz);
    }

    private unsafe int SetP0OffsetNvapi(int coreMhz, int memMhz)
    {
        // UXTU/NvAPIWrapper 模式: 从零构造最小化 V1 结构体, 直接 Set
        // PerformanceStates20InfoV1(state_count=1, clock_count=2, voltage_count=0)
        int sz = Marshal.SizeOf<NV_GPU_PSTATES20_V1>();
        var ptr = Marshal.AllocHGlobal(sz);
        try
        {
            // 整块内存清零 (NvAPIWrapper 的 Instantiate<T> 会初始化所有字段)
            new Span<byte>((void*)ptr, sz).Clear();

            byte* raw = (byte*)ptr;

            // Header (20 bytes)
            *(uint*)(raw + 0) = NV_GPU_PSTATES20_V1.MakeVersion(); // version = (1 << 16) | 7316
            *(uint*)(raw + 4) = 0;                                  // flags
            *(uint*)(raw + 8) = 1;                                  // state_count = 1
            *(uint*)(raw + 12) = 2;                                 // clock_count = 2
            *(uint*)(raw + 16) = 0;                                 // voltage_count = 0

            // P0 State (offset 20, 456 bytes)
            byte* p0 = raw + 20;
            *(uint*)(p0 + 0) = 0;  // state_id = P0_3DPerformance
            *(uint*)(p0 + 4) = 0;  // flags

            // Clock[0]: Graphics (domain=0, offset at p0+8)
            byte* clk0 = p0 + 8;
            *(uint*)(clk0 + 0) = 0;                   // domain = Graphics
            *(uint*)(clk0 + 4) = 0;                   // type
            *(uint*)(clk0 + 8) = 0;                   // flags
            *(int*)(clk0 + 12) = coreMhz * 1000;      // delta value (kHz)
            *(int*)(clk0 + 16) = 0;                   // delta min
            *(int*)(clk0 + 20) = 0;                   // delta max

            // Clock[1]: Memory (domain=4, offset at p0+8+44)
            byte* clk1 = p0 + 8 + NV_GPU_PSTATES20_V1.CLK_ENTRY;
            *(uint*)(clk1 + 0) = 4;                   // domain = Memory
            *(uint*)(clk1 + 4) = 0;                   // type
            *(uint*)(clk1 + 8) = 0;                   // flags
            *(int*)(clk1 + 12) = memMhz * 1000;       // delta value (kHz)
            *(int*)(clk1 + 16) = 0;                   // delta min
            *(int*)(clk1 + 20) = 0;                   // delta max

            var rcSet = _setPs!(_gpu, ptr);
            if (rcSet == OK)
            {
                // 等驱动应用偏移后回读验证
                System.Threading.Thread.Sleep(300);
                var rb = GetP0Offsets();
                var clk = GetCurrentClocks();
                var rbCore = rb.ok ? rb.coreKhz / 1000 : -1;
                var rbMem = rb.ok ? rb.memKhz / 1000 : -1;
                var curCore = clk.ok ? (int)clk.coreMhz : -1;
                var curMem = clk.ok ? (int)clk.memMhz : -1;
                AppLog.Write("NVAPI",
                    $"SetP0Offset: req=({coreMhz},{memMhz})MHz actual_offset=({rbCore},{rbMem})MHz " +
                    $"current_clk=({curCore},{curMem})MHz rc={rcSet}");
            }
            else
            {
                AppLog.Write("NVAPI", $"SetP0Offset: req=({coreMhz},{memMhz})MHz rc={rcSet} [FAILED]");
            }
            return rcSet;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // ── 时钟频率 ──

    public unsafe (float coreMhz, float memMhz, bool ok) GetCurrentClocks()
    {
        if (!_ok) return (0, 0, false);
        var f = new NV_GPU_CLOCK_FREQUENCIES { version = NV_GPU_CLOCK_FREQUENCIES.MakeVersion(), clock_type = 0 };
        if (_clk!(_gpu, &f) != OK) return (0, 0, false);
        byte* ep = f.entries;
        float core = *(uint*)ep != 0 ? *(uint*)(ep + 4) / 1000f : 0;          // domain 0
        float mem = *(uint*)(ep + 4 * 8) != 0 ? *(uint*)(ep + 4 * 8 + 4) / 1000f : 0; // domain 4
        return (core, mem, true);
    }

    // ── 功率限制 (笔记本 GPU 通常不支持) ──

    public unsafe (uint minMw, uint defMw, uint maxMw, bool ok) GetPowerLimitRange()
    {
        if (!_ok) return (0, 0, 0, false);
        var info = new NV_GPU_POWER_INFO { version = NV_GPU_POWER_INFO.MakeVersion() };
        if (_pwrI!(_gpu, &info) != OK) return (0, 0, 0, false);
        // entry[0] at raw offset 0: 5 uints (pstate, pad, pad, min, pad, pad, def, pad, pad, max, pad)
        // NVFC: 11 uints per entry → raw[3]=min, raw[6]=def, raw[9]=max
        return (info.raw[3], info.raw[6], info.raw[9], true);
    }

    public unsafe (uint mw, bool ok) GetPowerLimit()
    {
        if (!_ok) return (0, false);
        var st = new NV_GPU_POWER_STATUS { version = NV_GPU_POWER_STATUS.MakeVersion() };
        if (_pwrG!(_gpu, &st) != OK) return (0, false);
        return (st.raw[2], true);
    }

    public unsafe int SetPowerLimit(uint mw)
    {
        if (!_ok) return ERR;
        var st = new NV_GPU_POWER_STATUS { version = NV_GPU_POWER_STATUS.MakeVersion() };
        if (_pwrG!(_gpu, &st) != OK) return ERR;
        st.count = 1; st.raw[2] = mw;
        return _pwrS!(_gpu, &st);
    }

    // ── 温度限制 (Blackwell V1: 5 uint per entry) ──

    /// <summary>温度限制范围 (°C) — entry[0] offsets: min=raw[1], def=raw[2], max=raw[3]</summary>
    public unsafe (float minC, float defC, float maxC, bool ok) GetThermalLimitRange()
    {
        if (!_ok) return (0, 0, 0, false);
        var info = new NV_GPU_THERMAL_INFO { version = NV_GPU_THERMAL_INFO.MakeVersion() };
        if (_thrI!(_gpu, &info) != OK) return (0, 0, 0, false);
        // entry[0]: raw[0]=ctrl, raw[1]=min, raw[2]=def, raw[3]=max, raw[4]=flags
        return ((int)info.raw[1] / 256f, (int)info.raw[2] / 256f, (int)info.raw[3] / 256f, true);
    }

    /// <summary>当前温度限制 (°C) — entry[0]: raw[0]=ctrl, raw[1]=value</summary>
    public unsafe (float tempC, bool ok) GetThermalLimit()
    {
        if (!_ok) return (0, false);
        var st = new NV_GPU_THERMAL_STATUS { version = NV_GPU_THERMAL_STATUS.MakeVersion() };
        if (_thrG!(_gpu, &st) != OK) return (0, false);
        return ((int)st.raw[1] / 256f, true);
    }

    public unsafe int SetThermalLimit(float tempC)
    {
        if (!_ok) return ERR;
        var st = new NV_GPU_THERMAL_STATUS { version = NV_GPU_THERMAL_STATUS.MakeVersion() };
        if (_thrG!(_gpu, &st) != OK) return ERR;
        st.count = 1;
        st.raw[1] = (uint)(int)(tempC * 256);
        return _thrS!(_gpu, &st);
    }

    // ── 状态摘要 ──

    public NvapiStatus GetStatus()
    {
        var s = new NvapiStatus { Available = _ok, GpuName = GpuName, OverclockSupported = OverclockSupported };
        if (!_ok) return s;

        var clk = GetCurrentClocks();
        if (clk.ok) { s.CoreMhz = clk.coreMhz; s.MemMhz = clk.memMhz; }

        var off = GetP0Offsets();
        if (off.ok) { s.CoreOffsetMhz = off.coreKhz / 1000; s.MemOffsetMhz = off.memKhz / 1000; }

        var pwr = GetPowerLimit();
        if (pwr.ok) s.PowerLimitMw = pwr.mw;
        var pr = GetPowerLimitRange();
        if (pr.ok) { s.PowerMinMw = pr.minMw; s.PowerMaxMw = pr.maxMw; s.PowerDefaultMw = pr.defMw; }

        var thr = GetThermalLimit();
        if (thr.ok) s.ThermalLimitC = thr.tempC;
        var tr = GetThermalLimitRange();
        if (tr.ok) { s.ThermalMinC = tr.minC; s.ThermalMaxC = tr.maxC; s.ThermalDefaultC = tr.defC; }

        return s;
    }

    /// <summary>诊断: dump P-States</summary>
    public unsafe string DumpPStates()
    {
        if (!_ok) return "NVAPI not init";
        int sz = Marshal.SizeOf<NV_GPU_PSTATES20>();
        var ps = new NV_GPU_PSTATES20();
        ps.version = NV_GPU_PSTATES20.MakeVersion();
        var ptr = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(ps, ptr, false);
            int rc = _getPs!(_gpu, ptr);
            if (rc != OK) return $"GetPStates20 rc={rc}";

            byte* raw = (byte*)ptr;
            uint stateCount = *(uint*)(raw + 8);
            uint clockCount = *(uint*)(raw + 12);
            uint voltageCount = *(uint*)(raw + 16);
            byte* data = raw + 20;

            var sb = new StringBuilder();
            sb.AppendLine($"marshalSize={sz} states={stateCount} clocks={clockCount} voltages={voltageCount} OC={OverclockSupported}");
            for (int i = 0; i < (int)stateCount && i < 4; i++)
            {
                var sp = data + i * NV_GPU_PSTATES20.PER_STATE;
                sb.Append($"  P{*(uint*)sp}:");
                for (int j = 0; j < (int)clockCount && j < 8; j++)
                {
                    var cp = NV_GPU_PSTATES20.ClockPtr(sp, j);
                    var dom = NV_GPU_PSTATES20.ClockDomain(cp);
                    var dval = NV_GPU_PSTATES20.ClockDeltaValue(cp);
                    var dmin = *(int*)(cp + 16);
                    var dmax = *(int*)(cp + 20);
                    sb.Append($" clk[{j}]dom={dom}/delta={dval / 1000}MHz/range=[{dmin / 1000},{dmax / 1000}]");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

public struct NvapiStatus
{
    public bool Available, OverclockSupported;
    public string GpuName;
    public float CoreMhz, MemMhz;
    public int CoreOffsetMhz, MemOffsetMhz;
    public uint PowerLimitMw, PowerMinMw, PowerMaxMw, PowerDefaultMw;
    public float ThermalLimitC, ThermalMinC, ThermalMaxC, ThermalDefaultC;
}
