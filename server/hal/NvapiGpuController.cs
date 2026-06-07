// SPDX-License-Identifier: MIT
// NvapiGpuController — NVAPI P/Invoke 封装 (nvapi64.dll)
// 参考: NVFC (github.com/graphitemaster/NVFC) 结构体 + 函数 ID
// 支持: GPU 超频/降频 (P-State 偏移)、时钟读取、功率限制、温度限制

using System;
using System.Runtime.InteropServices;

namespace Douzhanzhe.HAL;

#region ── 原生结构体 ──

[StructLayout(LayoutKind.Sequential)]
internal struct NV_DELTA_ENTRY
{
    public int value;
    public int value_min;
    public int value_max;
}

// Clock entry: 9 × uint + NV_DELTA_ENTRY(12) = 48 bytes
//  layout: domain(4), type(4), flags(4), delta(12), min_single(4), max(4), vdomain(4), minV(4), maxV(4)
// Voltage entry: 3 × uint + NV_DELTA_ENTRY(12) = 24 bytes
//  layout: domain(4), flags(4), voltage(4), delta(12)
// Per-state: 2 × uint + 8×clock(48) + 4×voltage(24) = 8 + 384 + 96 = 488 bytes
// PSTATES20_V2: 5×uint(20) + 16×state(488) + uint(4) + 4×ov(24) = 20 + 7808 + 4 + 96 = 7928

/// <summary>
/// NV_GPU_PERF_PSTATES20_V2 — 使用 byte buffer 代替 fixed struct 数组
/// Total: 5*4 + 16*488 + 4 + 4*24 = 7928 bytes
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_PSTATES20_V2
{
    public const int STRUCT_SIZE = 5 * 4 + 16 * 488 + 4 + 4 * 24; // 7928

    public uint version;
    public uint flags;
    public uint state_count;
    public uint clock_count;
    public uint voltage_count;

    // 16 state entries, each 488 bytes → 7808 bytes
    public fixed byte states_buf[16 * 488];

    public uint over_voltage_count;
    // 4 over-voltage entries, each 24 bytes → 96 bytes
    public fixed byte over_voltage_buf[4 * 24];

    public static uint MakeVersion() => (2u << 16) | (uint)STRUCT_SIZE;

    // state entry offsets within states_buf:
    //   state_num: +0 (uint)
    //   flags:     +4 (uint)
    //   clocks:    +8 (8 × 48 bytes = 384)
    //   voltages:  +392 (4 × 24 bytes = 96)

    public static byte* StatePtr(NV_GPU_PSTATES20_V2* p, int index)
        => p->states_buf + index * 488;

    public static uint StateNum(byte* sp) => *(uint*)sp;

    /// <summary>clock entry pointer: sp + 8 + clkIdx * 48</summary>
    public static byte* ClockPtr(byte* sp, int clkIdx) => sp + 8 + clkIdx * 48;

    /// <summary>clock domain: offset +0</summary>
    public static uint ClockDomain(byte* cp) => *(uint*)cp;

    /// <summary>frequency_delta.value: offset +12 (int)</summary>
    public static int ClockDeltaValue(byte* cp) => *(int*)(cp + 12);

    /// <summary>set frequency_delta.value: offset +12</summary>
    public static void SetClockDelta(byte* cp, int valueKhz) => *(int*)(cp + 12) = valueKhz;
}

/// <summary>
/// NV_GPU_CLOCK_FREQUENCIES — entry = {present(u32), frequency(u32)} × 32
/// Total: 2*4 + 32*8 = 264 bytes
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_CLOCK_FREQUENCIES
{
    public const int MAX_CLOCKS = 32;
    public uint version;
    public uint clock_type; // 0=current, 1=base, 2=boost
    public fixed byte entries_buf[32 * 8]; // 32 × {present(u32), frequency(u32)}

    public static uint MakeVersion() => (2u << 16) | (2 * 4 + 32 * 8);

    public static uint EntryPresent(byte* ep) => *(uint*)ep;
    public static uint EntryFrequency(byte* ep) => *(uint*)(ep + 4);
}

/// <summary>
/// NV_GPU_POWER_POLICIES_INFO_V1
/// Total: 2*4 + 4*44 = 184 (4 entries × 11 uints)
/// Entry layout: pstate(u32), pad(u32), pad(u32), min(u32), pad(u32), pad(u32), default(u32), pad(u32), pad(u32), max(u32), pad(u32)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_POWER_POLICIES_INFO_V1
{
    public uint version;
    public uint flags;
    public fixed uint raw[44]; // 4 entries × 11 uints

    public static uint MakeVersion() => (1u << 16) | (2 * 4 + 44 * 4);
}

/// <summary>
/// NV_GPU_POWER_POLICIES_STATUS_V1
/// Total: 2*4 + 4*16 = 72 (4 entries × {pstate, pad, power, pad} = 4 uints each... wait)
/// Actually from NVFC: count(u32), then entries[4] each: pstate(u32), pad(u32), power(u32), pad(u32) = 4*uint
/// Total: version(4) + count(4) + 4*(4*4) = 72
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_POWER_POLICIES_STATUS_V1
{
    public uint version;
    public uint count;
    public fixed uint raw[16]; // 4 entries × 4 uints

    public static uint MakeVersion() => (1u << 16) | (2 * 4 + 16 * 4);
}

/// <summary>
/// NV_GPU_THERMAL_POLICIES_INFO_V2
/// Total: 2*4 + 4*28 = 120 (4 entries × 7 uints)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_THERMAL_POLICIES_INFO_V2
{
    public uint version;
    public uint flags;
    public fixed uint raw[28]; // 4 entries × 7 uints

    public static uint MakeVersion() => (2u << 16) | (2 * 4 + 28 * 4);
}

/// <summary>
/// NV_GPU_THERMAL_POLICIES_STATUS_V2
/// Total: 2*4 + 4*12 = 56 (4 entries × {controller, value, flags} = 3 uints)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_THERMAL_POLICIES_STATUS_V2
{
    public uint version;
    public uint count;
    public fixed uint raw[12]; // 4 entries × 3 uints

    public static uint MakeVersion() => (2u << 16) | (2 * 4 + 12 * 4);
}

#endregion

#region ── NVAPI 函数 ID (from NVFC nvapi.cpp) ──

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
/// 支持: 超频(core/mem offset)、读取频率、功率限制、温度限制
/// </summary>
public sealed class NvapiGpuController : IDisposable
{
    // ── 委托类型 ──
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvApiVoid();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvApiEnumGPUs(IntPtr[] handles, ref int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NvApiGetFullName(IntPtr gpu, byte[] name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiGetPStates20(IntPtr gpu, NV_GPU_PSTATES20_V2* pstates);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiSetPStates20(IntPtr gpu, NV_GPU_PSTATES20_V2* pstates);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiGetClockFreq(IntPtr gpu, NV_GPU_CLOCK_FREQUENCIES* freq);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiGetPowerInfo(IntPtr gpu, NV_GPU_POWER_POLICIES_INFO_V1* info);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiGetPowerStatus(IntPtr gpu, NV_GPU_POWER_POLICIES_STATUS_V1* status);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiSetPowerStatus(IntPtr gpu, NV_GPU_POWER_POLICIES_STATUS_V1* status);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiGetThermalInfo(IntPtr gpu, NV_GPU_THERMAL_POLICIES_INFO_V2* info);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiGetThermalStatus(IntPtr gpu, NV_GPU_THERMAL_POLICIES_STATUS_V2* status);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate int NvApiSetThermalStatus(IntPtr gpu, NV_GPU_THERMAL_POLICIES_STATUS_V2* status);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr NvApiQueryInterface(uint id);

    // ── 字段 ──
    private NvApiQueryInterface? _queryInterface;
    private NvApiVoid? _initialize;
    private NvApiEnumGPUs? _enumGPUs;
    private NvApiGetFullName? _getFullName;
    private NvApiGetPStates20? _getPStates20;
    private NvApiSetPStates20? _setPStates20;
    private NvApiGetClockFreq? _getClockFreq;
    private NvApiGetPowerInfo? _getPowerInfo;
    private NvApiGetPowerStatus? _getPowerStatus;
    private NvApiSetPowerStatus? _setPowerStatus;
    private NvApiGetThermalInfo? _getThermalInfo;
    private NvApiGetThermalStatus? _getThermalStatus;
    private NvApiSetThermalStatus? _setThermalStatus;

    private IntPtr _gpuHandle;
    private bool _initialized;

    public bool IsAvailable => _initialized;
    public string GpuName { get; private set; } = "";

    private const int NVAPI_OK = 0;
    private const int NVAPI_ERROR = -1;

    // ── Init ──
    public bool Init()
    {
        try
        {
            var hMod = NativeLibrary.Load("nvapi64.dll");
            var qiPtr = NativeLibrary.GetExport(hMod, "nvapi_QueryInterface");
            _queryInterface = Marshal.GetDelegateForFunctionPointer<NvApiQueryInterface>(qiPtr);

            T Q<T>(uint id) where T : Delegate
            {
                var fptr = _queryInterface(id);
                if (fptr == IntPtr.Zero) throw new InvalidOperationException($"NVAPI 0x{id:X8} not found");
                return Marshal.GetDelegateForFunctionPointer<T>(fptr);
            }

            _initialize       = Q<NvApiVoid>(NvApiId.Initialize);
            _enumGPUs         = Q<NvApiEnumGPUs>(NvApiId.EnumPhysicalGPUs);
            _getFullName      = Q<NvApiGetFullName>(NvApiId.GPU_GetFullName);
            _getPStates20     = Q<NvApiGetPStates20>(NvApiId.GPU_GetPStates20);
            _setPStates20     = Q<NvApiSetPStates20>(NvApiId.GPU_SetPStates20);
            _getClockFreq     = Q<NvApiGetClockFreq>(NvApiId.GPU_GetAllClockFreq);
            _getPowerInfo     = Q<NvApiGetPowerInfo>(NvApiId.GPU_GetPowerInfo);
            _getPowerStatus   = Q<NvApiGetPowerStatus>(NvApiId.GPU_GetPowerStatus);
            _setPowerStatus   = Q<NvApiSetPowerStatus>(NvApiId.GPU_SetPowerStatus);
            _getThermalInfo   = Q<NvApiGetThermalInfo>(NvApiId.GPU_GetThermalInfo);
            _getThermalStatus = Q<NvApiGetThermalStatus>(NvApiId.GPU_GetThermalStatus);
            _setThermalStatus = Q<NvApiSetThermalStatus>(NvApiId.GPU_SetThermalStatus);

            int rc = _initialize();
            if (rc != NVAPI_OK) { Console.WriteLine($"[NVAPI] Initialize rc={rc}"); return false; }

            var handles = new IntPtr[4];
            int count = 0;
            rc = _enumGPUs(handles, ref count);
            if (rc != NVAPI_OK || count == 0) { Console.WriteLine($"[NVAPI] EnumGPUs rc={rc} count={count}"); return false; }
            _gpuHandle = handles[0];

            var nameBytes = new byte[64];
            _getFullName(_gpuHandle, nameBytes);
            GpuName = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');

            _initialized = true;
            Console.WriteLine($"[NVAPI] OK: {GpuName} ({count} GPU)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NVAPI] Init: {ex.Message}");
            return false;
        }
    }

    // ── P-State 偏移 (超频/降频) ──

    /// <summary>读取 P0 时钟偏移 (返回 kHz)</summary>
    public unsafe (int coreKhz, int memKhz, bool ok) GetP0Offsets()
    {
        if (!_initialized) return (0, 0, false);
        var ps = new NV_GPU_PSTATES20_V2 { version = NV_GPU_PSTATES20_V2.MakeVersion() };
        int rc = _getPStates20!(_gpuHandle, &ps);
        if (rc != NVAPI_OK) return (0, 0, false);

        int core = 0, mem = 0;
        for (int i = 0; i < ps.state_count; i++)
        {
            var sp = NV_GPU_PSTATES20_V2.StatePtr(&ps, i);
            if (NV_GPU_PSTATES20_V2.StateNum(sp) != 0) continue; // P0 only
            for (int j = 0; j < ps.clock_count && j < 8; j++)
            {
                var cp = NV_GPU_PSTATES20_V2.ClockPtr(sp, j);
                var dom = NV_GPU_PSTATES20_V2.ClockDomain(cp);
                if (dom == 0) core = NV_GPU_PSTATES20_V2.ClockDeltaValue(cp);
                else if (dom == 4) mem = NV_GPU_PSTATES20_V2.ClockDeltaValue(cp);
            }
            break;
        }
        return (core, mem, true);
    }

    /// <summary>设置 P0 时钟偏移 (参数 MHz, 内部转 kHz)</summary>
    public unsafe int SetP0Offset(int coreMhz, int memMhz)
    {
        if (!_initialized) return NVAPI_ERROR;
        var ps = new NV_GPU_PSTATES20_V2 { version = NV_GPU_PSTATES20_V2.MakeVersion() };
        int rc = _getPStates20!(_gpuHandle, &ps);
        if (rc != NVAPI_OK) return rc;

        for (int i = 0; i < ps.state_count; i++)
        {
            var sp = NV_GPU_PSTATES20_V2.StatePtr(&ps, i);
            if (NV_GPU_PSTATES20_V2.StateNum(sp) != 0) continue;
            for (int j = 0; j < ps.clock_count && j < 8; j++)
            {
                var cp = NV_GPU_PSTATES20_V2.ClockPtr(sp, j);
                var dom = NV_GPU_PSTATES20_V2.ClockDomain(cp);
                if (dom == 0) NV_GPU_PSTATES20_V2.SetClockDelta(cp, coreMhz * 1000);
                else if (dom == 4) NV_GPU_PSTATES20_V2.SetClockDelta(cp, memMhz * 1000);
            }
            break;
        }
        return _setPStates20!(_gpuHandle, &ps);
    }

    // ── 时钟频率 ──

    /// <summary>读取当前核心/显存频率 (MHz)</summary>
    public unsafe (float coreMhz, float memMhz, bool ok) GetCurrentClocks()
    {
        if (!_initialized) return (0, 0, false);
        var freq = new NV_GPU_CLOCK_FREQUENCIES { version = NV_GPU_CLOCK_FREQUENCIES.MakeVersion(), clock_type = 0 };
        int rc = _getClockFreq!(_gpuHandle, &freq);
        if (rc != NVAPI_OK) return (0, 0, false);

        float core = 0, mem = 0;
        // domain 0 = GRAPHICS, domain 4 = MEMORY
        byte* ep = freq.entries_buf;
        if (*(uint*)ep != 0) core = *(uint*)(ep + 4) / 1000f;
        byte* mp = freq.entries_buf + 4 * 8;
        if (*(uint*)mp != 0) mem = *(uint*)(mp + 4) / 1000f;
        return (core, mem, true);
    }

    // ── 功率限制 (mW) ──

    /// <summary>读取功率限制范围 (mW)</summary>
    public unsafe (uint minMw, uint defMw, uint maxMw, bool ok) GetPowerLimitRange()
    {
        if (!_initialized) return (0, 0, 0, false);
        var info = new NV_GPU_POWER_POLICIES_INFO_V1 { version = NV_GPU_POWER_POLICIES_INFO_V1.MakeVersion() };
        int rc = _getPowerInfo!(_gpuHandle, &info);
        if (rc != NVAPI_OK) return (0, 0, 0, false);
        // entry[0]: raw[0]=pstate, raw[3]=min, raw[6]=default, raw[9]=max
        return (info.raw[3], info.raw[6], info.raw[9], true);
    }

    /// <summary>读取当前功率限制 (mW)</summary>
    public unsafe (uint mw, bool ok) GetPowerLimit()
    {
        if (!_initialized) return (0, false);
        var st = new NV_GPU_POWER_POLICIES_STATUS_V1 { version = NV_GPU_POWER_POLICIES_STATUS_V1.MakeVersion() };
        int rc = _getPowerStatus!(_gpuHandle, &st);
        if (rc != NVAPI_OK) return (0, false);
        // entry[0]: raw[0]=pstate, raw[1]=pad, raw[2]=power, raw[3]=pad
        return (st.raw[2], true);
    }

    /// <summary>设置功率限制 (mW)</summary>
    public unsafe int SetPowerLimit(uint mw)
    {
        if (!_initialized) return NVAPI_ERROR;
        var st = new NV_GPU_POWER_POLICIES_STATUS_V1 { version = NV_GPU_POWER_POLICIES_STATUS_V1.MakeVersion() };
        int rc = _getPowerStatus!(_gpuHandle, &st);
        if (rc != NVAPI_OK) return rc;
        st.count = 1;
        st.raw[2] = mw;
        return _setPowerStatus!(_gpuHandle, &st);
    }

    // ── 温度限制 ──

    /// <summary>读取温度限制范围 (值 = °C × 256)</summary>
    public unsafe (int min, int def, int max, bool ok) GetThermalLimitRange()
    {
        if (!_initialized) return (0, 0, 0, false);
        var info = new NV_GPU_THERMAL_POLICIES_INFO_V2 { version = NV_GPU_THERMAL_POLICIES_INFO_V2.MakeVersion() };
        int rc = _getThermalInfo!(_gpuHandle, &info);
        if (rc != NVAPI_OK) return (0, 0, 0, false);
        // entry[0]: raw[0]=controller, raw[1]=pad, raw[2]=min, raw[3]=default, raw[4]=max
        return ((int)info.raw[2], (int)info.raw[3], (int)info.raw[4], true);
    }

    /// <summary>读取当前温度限制 (°C)</summary>
    public unsafe (float tempC, bool ok) GetThermalLimit()
    {
        if (!_initialized) return (0, false);
        var st = new NV_GPU_THERMAL_POLICIES_STATUS_V2 { version = NV_GPU_THERMAL_POLICIES_STATUS_V2.MakeVersion() };
        int rc = _getThermalStatus!(_gpuHandle, &st);
        if (rc != NVAPI_OK) return (0, false);
        return ((int)st.raw[1] / 256f, true);
    }

    /// <summary>设置温度限制 (°C)</summary>
    public unsafe int SetThermalLimit(float tempC)
    {
        if (!_initialized) return NVAPI_ERROR;
        var st = new NV_GPU_THERMAL_POLICIES_STATUS_V2 { version = NV_GPU_THERMAL_POLICIES_STATUS_V2.MakeVersion() };
        int rc = _getThermalStatus!(_gpuHandle, &st);
        if (rc != NVAPI_OK) return rc;
        st.count = 1;
        st.raw[1] = (uint)(int)(tempC * 256);
        st.raw[2] = 1; // flags: enabled
        return _setThermalStatus!(_gpuHandle, &st);
    }

    // ── 状态摘要 ──

    public NvapiStatus GetStatus()
    {
        var s = new NvapiStatus { Available = _initialized, GpuName = GpuName };
        if (!_initialized) return s;

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
        if (tr.ok) { s.ThermalMinC = tr.min / 256f; s.ThermalMaxC = tr.max / 256f; s.ThermalDefaultC = tr.def / 256f; }

        return s;
    }

    public void Dispose() { }
}

public struct NvapiStatus
{
    public bool Available;
    public string GpuName;
    public float CoreMhz, MemMhz;
    public int CoreOffsetMhz, MemOffsetMhz;
    public uint PowerLimitMw, PowerMinMw, PowerMaxMw, PowerDefaultMw;
    public float ThermalLimitC, ThermalMinC, ThermalMaxC, ThermalDefaultC;
}
