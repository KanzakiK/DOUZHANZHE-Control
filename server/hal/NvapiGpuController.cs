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
/// NV_GPU_PERF_PSTATES20 — Blackwell V1 layout (7316 bytes)
/// 实测: state_count=5, clock_count=2, voltage_count=0
/// per_state = (7316 - 20) / 16 = 456 bytes
/// clock entry = 44 bytes (NVFC 为 48, Blackwell 实际 44)
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NV_GPU_PSTATES20
{
    public const int STRUCT_SIZE = 7316;
    public const int PER_STATE = 456;  // (7316-20)/16
    public const int CLK_ENTRY = 44;   // Blackwell clock entry size
    public const int CLK_DELTA_OFF = 12; // frequency_delta.value offset within clock entry

    public uint version;
    public uint flags;
    public uint state_count;
    public uint clock_count;
    public uint voltage_count;
    public fixed byte data[STRUCT_SIZE - 20]; // rest of struct

    public static uint MakeVersion() => (1u << 16) | (uint)STRUCT_SIZE;

    /// <summary>P-State entry pointer: data + stateIdx * PER_STATE</summary>
    public static byte* StatePtr(NV_GPU_PSTATES20* p, int idx)
        => p->data + idx * PER_STATE;

    public static uint StateNum(byte* sp) => *(uint*)sp;
    public static byte* ClockPtr(byte* sp, int clkIdx) => sp + 8 + clkIdx * CLK_ENTRY;
    public static uint ClockDomain(byte* cp) => *(uint*)cp;
    public static int ClockDeltaValue(byte* cp) => *(int*)(cp + CLK_DELTA_OFF);
    public static void SetClockDelta(byte* cp, int kHz) => *(int*)(cp + CLK_DELTA_OFF) = kHz;
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
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiPStates(IntPtr g, NV_GPU_PSTATES20* p);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiClkFreq(IntPtr g, NV_GPU_CLOCK_FREQUENCIES* f);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiPwrInfo(IntPtr g, NV_GPU_POWER_INFO* i);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiPwrSt(IntPtr g, NV_GPU_POWER_STATUS* s);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiThrInfo(IntPtr g, NV_GPU_THERMAL_INFO* i);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate int NvApiThrSt(IntPtr g, NV_GPU_THERMAL_STATUS* s);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr NvApiQI(uint id);

    // KaronOC.dll 委托 (蛟龙控制台超频引擎)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int KaronChangePStates(int coreOffsetMhz, int memOffsetMhz);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int KaronGetPStates(IntPtr gpuHandle, IntPtr outputBuf);

    private NvApiQI? _qi;
    private NvApiVoid? _init; private NvApiEnumGPUs? _enum; private NvApiName? _name;
    private NvApiPStates? _getPs, _setPs; private NvApiClkFreq? _clk;
    private NvApiPwrInfo? _pwrI; private NvApiPwrSt? _pwrG, _pwrS;
    private NvApiThrInfo? _thrI; private NvApiThrSt? _thrG, _thrS;
    private KaronChangePStates? _karonSet; private KaronGetPStates? _karonGet;
    private IntPtr _karonMod;

    private IntPtr _gpu; private bool _ok;
    public bool IsAvailable => _ok;
    public string GpuName { get; private set; } = "";
    public bool OverclockSupported { get; private set; }
    public string OcEngine { get; private set; } = "none"; // "karonoc" | "nvapi" | "none"

    private const int OK = 0, ERR = -1, NOT_SUPPORTED = -104;

    // KaronOC.dll 搜索路径
    private static readonly string[] KaronOCPaths = [
        @"D:\Program Files\JiaoLong7.3\KaronOC.dll",
        @"C:\Program Files\JiaoLong7.3\KaronOC.dll",
        Path.Combine(AppContext.BaseDirectory, "KaronOC.dll"),
    ];

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

            // 1) 优先尝试 KaronOC.dll (蛟龙超频引擎, 绕过 NVAPI 限制)
            //    目标机型固定 (RTX 5060 Laptop GPU)，超频能力已知，仅检测 DLL 是否存在，不调用测试
            if (TryLoadKaronOC())
            {
                OverclockSupported = true;
                OcEngine = "karonoc";
            }
            else
            {
                // 2) 回退: 直接 NVAPI (目标机型已知支持，跳过 SetPStates20 探测)
                OverclockSupported = true;
                OcEngine = "nvapi";
            }

            _ok = true;
            AppLog.Write("NVAPI", $"OK: {GpuName} | OC={OverclockSupported} engine={OcEngine}");
            return true;
        }
        catch (Exception ex) { AppLog.Write("NVAPI", ex.Message); return false; }
    }

    private bool TryLoadKaronOC()
    {
        foreach (var path in KaronOCPaths)
        {
            if (!File.Exists(path)) continue;
            IntPtr mod = IntPtr.Zero;
            try
            {
                mod = NativeLibrary.Load(path);
                var pSet = NativeLibrary.GetExport(mod, "ChangePstatesLevel0Settings");
                var pGet = NativeLibrary.GetExport(mod, "GetPstatesLevel0Settings");
                _karonSet = Marshal.GetDelegateForFunctionPointer<KaronChangePStates>(pSet);
                _karonGet = Marshal.GetDelegateForFunctionPointer<KaronGetPStates>(pGet);
                _karonMod = mod; // 全部成功后才保存句柄
                AppLog.Write("KaronOC", $"Loaded: {path}");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("KaronOC", $"Load failed ({path}): {ex.Message}");
                if (mod != IntPtr.Zero) NativeLibrary.Free(mod);
                _karonSet = null; _karonGet = null; // 清理残留委托
            }
        }
        return false;
    }

    // ── P-State 偏移 (超频/降频) ──

    /// <summary>读取 P0 时钟偏移 (kHz)</summary>
    public unsafe (int coreKhz, int memKhz, bool ok) GetP0Offsets()
    {
        if (!_ok) return (0, 0, false);
        var ps = new NV_GPU_PSTATES20 { version = NV_GPU_PSTATES20.MakeVersion() };
        if (_getPs!(_gpu, &ps) != OK) return (0, 0, false);

        int core = 0, mem = 0;
        for (int i = 0; i < (int)ps.state_count; i++)
        {
            var sp = NV_GPU_PSTATES20.StatePtr(&ps, i);
            if (NV_GPU_PSTATES20.StateNum(sp) != 0) continue;
            for (int j = 0; j < (int)ps.clock_count && j < 8; j++)
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

    /// <summary>设置 P0 偏移 (MHz) — 优先使用 KaronOC, 回退 NVAPI</summary>
    public int SetP0Offset(int coreMhz, int memMhz)
    {
        if (!_ok || !OverclockSupported) return NOT_SUPPORTED;

        // KaronOC 引擎 (蛟龙超频)
        if (_karonSet != null && OcEngine == "karonoc")
            return _karonSet(coreMhz, memMhz);

        // NVAPI 直接引擎 (回退)
        return SetP0OffsetNvapi(coreMhz, memMhz);
    }

    private unsafe int SetP0OffsetNvapi(int coreMhz, int memMhz)
    {
        var ps = new NV_GPU_PSTATES20 { version = NV_GPU_PSTATES20.MakeVersion() };
        if (_getPs!(_gpu, &ps) != OK) return ERR;

        for (int i = 0; i < (int)ps.state_count; i++)
        {
            var sp = NV_GPU_PSTATES20.StatePtr(&ps, i);
            if (NV_GPU_PSTATES20.StateNum(sp) != 0) continue;
            for (int j = 0; j < (int)ps.clock_count && j < 8; j++)
            {
                var cp = NV_GPU_PSTATES20.ClockPtr(sp, j);
                var dom = NV_GPU_PSTATES20.ClockDomain(cp);
                if (dom == 0) NV_GPU_PSTATES20.SetClockDelta(cp, coreMhz * 1000);
                else if (dom == 4) NV_GPU_PSTATES20.SetClockDelta(cp, memMhz * 1000);
            }
            break;
        }
        return _setPs!(_gpu, &ps);
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
        var s = new NvapiStatus { Available = _ok, GpuName = GpuName, OverclockSupported = OverclockSupported, OcEngine = OcEngine };
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
        var ps = new NV_GPU_PSTATES20 { version = NV_GPU_PSTATES20.MakeVersion() };
        int rc = _getPs!(_gpu, &ps);
        if (rc != OK) return $"GetPStates20 rc={rc}";
        var sb = new StringBuilder();
        sb.AppendLine($"sizeof={sizeof(NV_GPU_PSTATES20)} states={ps.state_count} clocks={ps.clock_count} voltages={ps.voltage_count} OC={OverclockSupported}");
        for (int i = 0; i < (int)ps.state_count && i < 4; i++)
        {
            var sp = NV_GPU_PSTATES20.StatePtr(&ps, i);
            sb.Append($"  P{NV_GPU_PSTATES20.StateNum(sp)}:");
            for (int j = 0; j < (int)ps.clock_count && j < 8; j++)
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

    private void ReleaseKaronOC()
    {
        _karonSet = null; _karonGet = null;
        if (_karonMod != IntPtr.Zero) { NativeLibrary.Free(_karonMod); _karonMod = IntPtr.Zero; }
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseKaronOC();
    }
}

public struct NvapiStatus
{
    public bool Available, OverclockSupported;
    public string GpuName, OcEngine;
    public float CoreMhz, MemMhz;
    public int CoreOffsetMhz, MemOffsetMhz;
    public uint PowerLimitMw, PowerMinMw, PowerMaxMw, PowerDefaultMw;
    public float ThermalLimitC, ThermalMinC, ThermalMaxC, ThermalDefaultC;
}
