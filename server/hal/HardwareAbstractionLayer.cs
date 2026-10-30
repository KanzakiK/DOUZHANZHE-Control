// SPDX-License-Identifier: MIT
//
// HardwareAbstractionLayer (HAL) — 硬件映射与控制层
// ===================================================
// 职责：
//   在 DriverBridge 之上提供语义化的硬件访问接口。
//   所有物理地址偏移均源自 DSDT/SSDT 反编译确认的 EC 寄存器映射。
//
// 参考:
//   DSDT: OperationRegion (ECF2, SystemMemory, 0xFE800400, 0xFF)
//   /memories/douzhanzhe-dsdt-ec-map.md

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Douzhanzhe.HAL;

public sealed class HardwareAbstractionLayer : IDisposable
{
    private readonly DriverBridge _io;
    private byte _lastGpuTemp;
    private DateTime _lastGpuTempTime = DateTime.MinValue;

    // System telemetry cache fields
    private byte _sgGpuUsage;
    private float _sgGpuFreq;
    private uint _sgGpuVram;
    private float _sgGpuVramUsed;
    private int _sgGpuMemMhz;
    private float _sgGpuPowerDrawW;
    private DateTime _sgGpuTime = DateTime.MinValue;
    private int _sgCpuPct;
    private DateTime _sgCpuTime = DateTime.MinValue;
    private long _cpuIdlePrev, _cpuKernelPrev, _cpuUserPrev;
    private bool _cpuTimesInit;
    private int _sgMemUsage, _sgMemTotal, _sgMemFreq;
    private DateTime _sgMemTime = DateTime.MinValue;
    private int _sgDiskUsage, _sgDiskTotal, _sgDiskFree;
    private DateTime _sgDiskTime = DateTime.MinValue;

    private string _sysModel = "";
    private string _cpuName = "";
    private string _gpuD = "";
    private string _gpuI = "";
    private DateTime _sysInfoTime = DateTime.MinValue;

    public const ushort FanLargeMax = 4400;
    public const ushort FanSmallMax = 8200;

    public HardwareAbstractionLayer()
    {
        _io = DriverBridge.Instance;
        _io.Init();
        if (!_io.Ready)
            AppLog.Write("HAL", "硬件驱动不可用，所有硬件读取将返回安全默认值");
    }

    /// <summary>硬件驱动是否可用</summary>
    public bool DriverAvailable => _io.Ready;

    // ================================================================
    // EC 偏移量常量 (相对 0xFE800400)
    // ================================================================

    private const uint OFF_KBNL   = 0x9A;  // 键盘背光等级 0-3
    private const uint OFF_FNHK   = 0x20;  // bit3: Fn 锁
    private const uint OFF_CALK   = 0x25;  // bit1: CapsLock
    private const uint OFF_NULK   = 0x25;  // bit2: NumLock
    private const uint OFF_ITSM   = 0xE4;  // 智能散热模式 (0-3)
    private const uint OFF_GPUT   = 0xE0;  // GPU 温度
    private const uint OFF_CPUT   = 0xE1;  // CPU 温度
    private const uint OFF_F1HI   = 0x9B;  // CPU 风扇转速高字节
    private const uint OFF_F1LO   = 0x9C;  // CPU 风扇转速低字节
    private const uint OFF_F3HI   = 0x96;  // GPU 风扇转速高字节
    private const uint OFF_F3LO   = 0x97;  // GPU 风扇转速低字节

    private const int BIT_FNHK    = 3;     // 0x20 bit3
    private const int BIT_CALK    = 1;     // 0x25 bit1
    private const int BIT_NULK    = 2;     // 0x25 bit2

    // ================================================================
    // Win32 keybd_event — CapsLock/NumLock 切换
    // ================================================================
    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const byte VK_CAPITAL = 0x14;
    private const byte VK_NUMLOCK = 0x90;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // ================================================================
    // Power Plan — Windows 电源计划切换
    // ================================================================
    [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme", CharSet = CharSet.Unicode)]
    static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme", CharSet = CharSet.Unicode)]
    static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    private static readonly Guid GUID_BALANCED   = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid GUID_PERFORMANCE = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid GUID_POWERSAVE  = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    // Re-mapped: 0=balanced, 1=high performance, 2=power saver
    private static readonly Guid[] PowerPlanGuids = [GUID_BALANCED, GUID_PERFORMANCE, GUID_POWERSAVE];
    private static readonly string[] PowerPlanNames = ["平衡", "高性能", "节能"];

    /// <summary>获取当前电源计划 (0/1/2)</summary>
    public int PowerPlan
    {
        get
        {
            try
            {
                uint ret = PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr);
                if (ret != 0) return -1;
                Guid current = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid))!;
                LocalFree(ptr);
                for (int i = 0; i < PowerPlanGuids.Length; i++)
                    if (current == PowerPlanGuids[i]) return i;
                return -1; // unknown
            }
            catch { return -1; }
        }
        set
        {
            int idx = Math.Clamp(value, 0, 2);
            Guid g = PowerPlanGuids[idx];
            PowerSetActiveScheme(IntPtr.Zero, ref g);
        }
    }

    // ================================================================
    // 遥测 — 只读属性
    // 温度通过 EC IO 端口 0x1C (ec_reader.cs 已验证)
    // 风扇通过 EC IO 端口 (ec_reader.cs 已验证)
    // 系统开关通过物理内存映射 (DSDT 确认)
    // 键盘背光通过物理内存 (ec_kb_map.cs 已验证)
    // ================================================================

    // CPU 温度诊断：首次失败时打印各路径返回值
    private static int _cpuTempDiagCount;

    /// <summary>CPU 温度 (摄氏度) — EC IO 0x1C 优先，物理内存回退</summary>
    public byte CpuTemperature
    {
        get
        {
            // 1) EC IO 端口读 0x1C（v2.0 走 PawnIO LpcACPIEC.bin）
            byte ecIo = 0;
            try { ecIo = _io.ReadEc(0x1C); }
            catch { /* ignore */ }
            if (ecIo > 0 && ecIo < 128) return ecIo;

            // 首次或每 100 次失败打印诊断
            if (++_cpuTempDiagCount == 1 || _cpuTempDiagCount % 100 == 0)
                AppLog.Write("CpuTemp", $"EC_IO(0x1C)=0x{ecIo:X2} (第{_cpuTempDiagCount}次)");

            return 0;
        }
    }

    /// <summary>GPU 温度 (摄氏度) — nvidia-smi</summary>
    public byte GpuTemperature
    {
        get
        {
            if ((DateTime.UtcNow - _lastGpuTempTime).TotalSeconds < 2 && _lastGpuTemp > 0)
                return _lastGpuTemp;

            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi)!;
                proc.WaitForExit(3000);
                var line = proc.StandardOutput.ReadToEnd().Trim();
                if (byte.TryParse(line, out var temp) && temp > 0)
                {
                    _lastGpuTemp = temp;
                    _lastGpuTempTime = DateTime.UtcNow;
                    return temp;
                }
            }
            catch { /* nvidia-smi 不可用 */ }

            return 0;
        }
    }

    /// <summary>CPU 风扇转速 (RPM) — EC IO 协议 (PawnIO LpcACPIEC.bin)</summary>
    public ushort CpuFanRpm
    {
        get
        {
            for (int i = 0; i < 3; i++)
            {
                var hi = _io.ReadEc(0x9D);
                var lo = _io.ReadEc(0x9E);
                var val = (ushort)((hi << 8) | lo);
                if (val != 0) return val;
            }
            return 0;
        }
    }

    private static byte _gpuFanRegBase; // 0=未探测

    /// <summary>GPU 风扇转速 (RPM) — EC IO 协议 (PawnIO LpcACPIEC.bin)</summary>
    public ushort GpuFanRpm
    {
        get
        {
            // 优先走已缓存的寄存器地址
            if (_gpuFanRegBase != 0)
                return ReadFanPair(_gpuFanRegBase);

            // 首次: 尝试已知可能的寄存器对，取第一个非零值
            byte[] candidates = [0x96, 0x9B, 0x93, 0x98];
            foreach (var baseAddr in candidates)
            {
                var val = ReadFanPair(baseAddr);
                if (val != 0)
                {
                    _gpuFanRegBase = baseAddr;
                    return val;
                }
            }
            return 0;
        }
    }

    private ushort ReadFanPair(byte baseAddr)
    {
        for (int i = 0; i < 3; i++)
        {
            var hi = _io.ReadEc(baseAddr);
            var lo = _io.ReadEc((byte)(baseAddr + 1));
            var val = (ushort)((hi << 8) | lo);
            if (val != 0) return val;
        }
        return 0;
    }

    // ================================================================
    // 风扇目标转速状态寄存器 — EC 0x5E (大扇/CPU) / 0x5A (小扇/GPU)
    // 编码公式: val = RPM / 100  (如 3200 RPM → val=32)
    // 注意: 这些寄存器由 EC 固件通过 WMI ACPI 通道写入，
    //       通过 EC IO 端口 (0x62/0x66) 直写会被固件在 <30ms 内覆写回原值。
    //       风扇转速控制应使用 WMI Method 20/21 (Bellator 协议)。
    //       0xB2/0xB3 是 GPU 区域温度传感器，非风扇寄存器。
    // ================================================================

    // ================================================================
    // 系统开关 — 读写控制 (通过 EC IO 端口)
    // ================================================================

    /// <summary>Fn 锁状态 — 写入通过 WMI Method 11（HAL setter 仅作接口占位）</summary>
    public bool FnLock
    {
        get => (_io.ReadEc((byte)OFF_FNHK) & (1 << BIT_FNHK)) != 0;
        set
        {
            byte val = _io.ReadEc((byte)OFF_FNHK);
            if (value) val |= (byte)(1 << BIT_FNHK);
            else val &= unchecked((byte)~(1 << BIT_FNHK));
            // 实际写入走 WMI SetFnLock，这里保留 EC 路径作为回退
            _io.WriteEc((byte)OFF_FNHK, val);
        }
    }

    /// <summary>CapsLock 状态 (通过 Win32 keybd_event 切换)</summary>
    public bool CapsLock
    {
        get => Console.CapsLock;
        set
        {
            if (Console.CapsLock != value)
            {
                keybd_event(VK_CAPITAL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_CAPITAL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }
    }

    /// <summary>NumLock 状态 (通过 Win32 keybd_event 切换)</summary>
    public bool NumLock
    {
        get => Console.NumberLock;
        set
        {
            if (Console.NumberLock != value)
            {
                keybd_event(VK_NUMLOCK, 0, 0, UIntPtr.Zero);
                keybd_event(VK_NUMLOCK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }
    }

    // ================================================================
    // 键盘背光
    // ================================================================

    /// <summary>键盘背光亮度 (0-3)</summary>
    public byte KeyboardBrightness
    {
        get => _io.ReadEc((byte)OFF_KBNL);
        set
        {
            var v = Math.Min((byte)3, value);
            // KBNL 写入: 走 EC IO 协议 (PawnIO LpcACPIEC.bin)
            // 不再依赖 inpoutx64 SetPhysLong（v2.0 已移除）
            _io.WriteEc((byte)OFF_KBNL, v);
        }
    }

    // ================================================================
    // 性能模式 / 散热模式
    // ================================================================

    /// <summary>散热模式 — 读取通过 EC IO，写入通过 WMI Method 8</summary>
    public byte ThermalMode
    {
        get => _io.ReadEc((byte)OFF_ITSM);
        set { /* 写入走 WMI SetThermalMode，HAL setter 仅为接口占位 */ }
    }

    private const string TP_INSTANCE = "ACPI\\BLTP7853\\1";

    public bool TouchpadLocked
    {
        get
        {
            try
            {
                using var p = new Process();
                p.StartInfo.FileName = "powershell";
                p.StartInfo.Arguments = "-NoProfile -Command (Get-PnpDevice -InstanceId '" + TP_INSTANCE + "').Status -eq 'OK'";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                if (p.WaitForExit(2000)) return p.StandardOutput.ReadToEnd().Trim() != "True";
            }
            catch { }
            return false;
        }
        set
        {
            try
            {
                var cmd = value ? "Disable" : "Enable";
                using var p = new Process();
                p.StartInfo.FileName = "powershell";
                p.StartInfo.Arguments = "-NoProfile -Command " + cmd + "-PnpDevice -InstanceId '" + TP_INSTANCE + "' -Confirm:$false";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    // ================================================================
    // dGPU 控制 via DSAD method
    // DSDT: DSAD(Arg0=功能码, Arg1=状态)
    //   Arg0 = 0x0B 是 dGPU
    //   物理地址 = (Arg0 << 1) + 0xFED81E40
    //   写入: bit1=ADPD 控制电源状态
    // ================================================================

    private const ulong DSAD_BASE = 0xFED81E40;


    /// <summary>集显模式 (IgpuOnly) — DSAD 0x0B ADPD(bit3) @ 0xFED81E56
    /// ADPD=1 断电(集显), ADPD=0 通电(混合/独显)</summary>
    public bool IgpuOnly
    {
        get => false;
        set { /* DSAD 直写物理内存已被硬件保护拦截，详见 docs/PAWNIO-MIGRATION-PLAN.md §7 #9 */ }
    }

    // ================================================================
    // EC 协议访问 (IO 端口 0x62/0x66)
    // ================================================================

    /// <summary>通过 EC IO 协议读取寄存器 (备选方法)</summary>
    public byte ReadEcPort(byte reg) => _io.ReadEc(reg);

    /// <summary>通过 EC IO 协议写入寄存器 (备选方法)</summary>
    public void WriteEcPort(byte reg, byte val) => _io.WriteEc(reg, val);

    // ================================================================
    // 系统信息 (WMI 子进程)
    // ================================================================

    public string SystemModel
    {
        get
        {
            if ((DateTime.UtcNow - _sysInfoTime).TotalSeconds < 10 && !string.IsNullOrEmpty(_sysModel))
                return _sysModel;
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_ComputerSystem).Manufacturer + ' ' + (Get-CimInstance Win32_ComputerSystem).Model\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                if (!p.WaitForExit(3000)) { p.Kill(); return _sysModel; }
                var line = p.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(line)) _sysModel = line;
            }
            catch { }
            _sysInfoTime = DateTime.UtcNow;
            return _sysModel;
        }
    }

    public string CpuName
    {
        get
        {
            if ((DateTime.UtcNow - _sysInfoTime).TotalSeconds < 10 && !string.IsNullOrEmpty(_cpuName))
                return _cpuName;
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_Processor).Name\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                if (!p.WaitForExit(3000)) { p.Kill(); return _cpuName; }
                var line = p.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(line)) _cpuName = line;
            }
            catch { }
            _sysInfoTime = DateTime.UtcNow;
            return _cpuName;
        }
    }

    public string GpuDiscreteName
    {
        get
        {
            if ((DateTime.UtcNow - _sysInfoTime).TotalSeconds < 10 && !string.IsNullOrEmpty(_gpuD))
                return _gpuD;
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -match 'VEN_10DE' }).Name\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                if (!p.WaitForExit(3000)) { p.Kill(); return _gpuD; }
                var line = p.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(line)) _gpuD = line;
            }
            catch { }
            _sysInfoTime = DateTime.UtcNow;
            return _gpuD;
        }
    }

    public string GpuIntegratedName
    {
        get
        {
            if ((DateTime.UtcNow - _sysInfoTime).TotalSeconds < 10 && !string.IsNullOrEmpty(_gpuI))
                return _gpuI;
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -match 'VEN_1002' }).Name\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                if (!p.WaitForExit(3000)) { p.Kill(); return _gpuI; }
                var line = p.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(line)) _gpuI = line;
            }
            catch { }
            _sysInfoTime = DateTime.UtcNow;
            return _gpuI;
        }
    }

    // ================================================================
    // 系统遥测
    // ================================================================

    public int CpuUsage
    {
        get
        {
            // 1 秒缓存
            if ((DateTime.UtcNow - _sgCpuTime).TotalSeconds < 1 && _sgCpuPct >= 0) return _sgCpuPct;
            try
            {
                if (GetSystemTimes(out long idle, out long kernel, out long user))
                {
                    if (_cpuTimesInit)
                    {
                        long dIdle   = idle   - _cpuIdlePrev;
                        long dKernel = kernel - _cpuKernelPrev;
                        long dUser   = user   - _cpuUserPrev;
                        // kernel 包含 idle，活跃时间 = kernel - idle + user
                        long active = dKernel - dIdle + dUser;
                        long total  = dKernel + dUser;
                        if (total > 0)
                        {
                            _sgCpuPct = (int)Math.Round((double)active / total * 100.0);
                            _sgCpuPct = Math.Clamp(_sgCpuPct, 0, 100);
                        }
                    }
                    _cpuIdlePrev   = idle;
                    _cpuKernelPrev = kernel;
                    _cpuUserPrev   = user;
                    _cpuTimesInit  = true;
                    _sgCpuTime     = DateTime.UtcNow;
                    return _sgCpuPct;
                }
            }
            catch { }
            return _sgCpuPct;
        }
    }

    public float CpuFreq
    {
        get
        {
            if ((DateTime.UtcNow - _cpuFreqTime).TotalSeconds < 0.5 && _cpuFreqCache > 0) return _cpuFreqCache;
            return GetCpuFreqDirect();
        }
    }

    private float _cpuFreqCache;
    private DateTime _cpuFreqTime = DateTime.MinValue;
    private PerformanceCounter? _cpuFreqCounter;
    private float _cpuBaseFreqGhz;
    private float GetCpuFreqDirect()
    {
        try
        {
            // 惰性初始化持久化 PerformanceCounter（不再每次 new/dispose）
            if (_cpuFreqCounter == null)
            {
                _cpuFreqCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                _cpuFreqCounter.NextValue(); // 首次调用返回 0，用于建立基线
            }

            var pct = _cpuFreqCounter.NextValue();
            if (pct > 0)
            {
                // 惰性检测真实基频（只读一次）
                if (_cpuBaseFreqGhz <= 0)
                {
                    try
                    {
                        using var proc = new Process
                        {
                            StartInfo = new ProcessStartInfo("powershell",
                                "-NoProfile -Command \"(Get-CimInstance Win32_Processor).MaxClockSpeed\"")
                            {
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                CreateNoWindow = true
                            }
                        };
                        proc.Start();
                        if (proc.WaitForExit(3000))
                        {
                            var line = proc.StandardOutput.ReadToEnd().Trim();
                            if (int.TryParse(line, out var mhz) && mhz > 0)
                                _cpuBaseFreqGhz = mhz / 1000f;
                        }
                    }
                    catch { }
                    if (_cpuBaseFreqGhz <= 0) _cpuBaseFreqGhz = 2.4f; // 最终兜底
                }

                _cpuFreqCache = (float)(_cpuBaseFreqGhz * (pct / 100.0));
                _cpuFreqTime = DateTime.UtcNow;
                return _cpuFreqCache;
            }
        }
        catch { _cpuFreqCounter?.Dispose(); _cpuFreqCounter = null; }
        return _cpuFreqCache > 0 ? _cpuFreqCache : 2.4f;
    }

    public int CpuCores => Environment.ProcessorCount;

    // ---- CPU 静态信息（Win32_Processor 直查，进程生命周期缓存一次；CPU 规格不会变）----
    private static readonly object _cpuStaticLock = new();
    private static (int Threads, int MaxFreqMHz, int L3CacheMB)? _cpuStatic;

    private static (int, int, int) GetCpuStatic()
    {
        lock (_cpuStaticLock)
        {
            if (_cpuStatic != null) return _cpuStatic.Value;
            int threads = 0, maxFreq = 0, l3MB = 0;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize FROM Win32_Processor");
                foreach (var o in searcher.Get())
                {
                    threads = Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                    maxFreq = Convert.ToInt32(o["MaxClockSpeed"]);
                    if (o["L3CacheSize"] != null) l3MB = Convert.ToInt32(o["L3CacheSize"]) / 1024; // KB → MB
                    break;
                }
            }
            catch { }
            _cpuStatic = (threads, maxFreq, l3MB);
            return _cpuStatic.Value;
        }
    }

    /// <summary>逻辑处理器数（线程）。0 = 查询失败</summary>
    public int CpuThreads => GetCpuStatic().Item1;
    /// <summary>标称基础频率 MHz（Win32_Processor.MaxClockSpeed，即设备管理器显示的额定值；真实睿频上限需 SMU 读取，暂未实现）。0 = 查询失败</summary>
    public int CpuBaseFreqMHz => GetCpuStatic().Item2;
    /// <summary>三级缓存 MB。0 = 查询失败</summary>
    public int CpuL3CacheMB => GetCpuStatic().Item3;


    public byte GpuUsage { get { RefreshGpu(); return _sgGpuUsage; } }
    public float GpuFreq { get { RefreshGpu(); return _sgGpuFreq; } }
    public uint GpuVram { get { RefreshGpu(); return _sgGpuVram; } }
    public float GpuVramUsed { get { RefreshGpu(); return _sgGpuVramUsed; } }
    public int GpuMemMhz { get { RefreshGpu(); return _sgGpuMemMhz; } }
    public float GpuPowerDrawW { get { RefreshGpu(); return _sgGpuPowerDrawW; } }

    private void RefreshGpu()
    {
        if ((DateTime.UtcNow - _sgGpuTime).TotalSeconds < 2) return;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,clocks.current.graphics,memory.total,memory.used,clocks.current.memory,power.draw --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            if (!p.WaitForExit(3000)) { p.Kill(); return; }
            var parts = p.StandardOutput.ReadToEnd().Trim().Split(',');
            if (parts.Length >= 4)
            {
                // GPU 占用：防归零—已有非零值时不接受 0（nvidia-smi 偶发返回 N/A 或 0）
                if (byte.TryParse(parts[0].Trim(), out var parsedUsage) && (parsedUsage > 0 || _sgGpuUsage == 0))
                    _sgGpuUsage = parsedUsage;
                if (float.TryParse(parts[1].Trim(), out var f)) _sgGpuFreq = f / 1000f;
                if (float.TryParse(parts[2].Trim(), out var t)) _sgGpuVram = (uint)Math.Round(t / 1024.0);
                if (float.TryParse(parts[3].Trim(), out var u)) _sgGpuVramUsed = (float)(u / 1024.0);
                if (parts.Length >= 5 && int.TryParse(parts[4].Trim(), out var mm)) _sgGpuMemMhz = mm;
                if (parts.Length >= 6 && float.TryParse(parts[5].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pw)) _sgGpuPowerDrawW = pw;
                _sgGpuTime = DateTime.UtcNow;
            }
        }
        catch { }
    }

    public int MemoryUsage { get { RefreshMem(); return _sgMemUsage; } }
    public int MemoryTotalGB { get { RefreshMem(); return _sgMemTotal; } }
    public int MemoryFreq { get { RefreshMem(); return _sgMemFreq; } }

    private void RefreshMem()
    {
        if ((DateTime.UtcNow - _sgMemTime).TotalSeconds < 2) return;
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"$totalKB = (Get-CimInstance Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum / 1KB; $os = Get-CimInstance Win32_OperatingSystem; $freq = (Get-CimInstance Win32_PerfFormattedData_Counters_MemoryPerformance -ErrorAction SilentlyContinue).MemoryClock; if (-not $freq) { $freq = (Get-CimInstance Win32_PhysicalMemory | Select-Object -First 1).ConfiguredClockSpeed }; Write-Output ('{0},{1},{2}' -f $totalKB, $os.FreePhysicalMemory, $freq)\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null || !p.WaitForExit(3000)) { p?.Kill(); return; }
            var parts = p.StandardOutput.ReadToEnd().Trim().Split(',');
            if (parts.Length >= 3 && long.TryParse(parts[0], out var total) && total > 0)
            {
                _sgMemTotal = (int)Math.Round(total / 1024.0 / 1024.0);
                if (long.TryParse(parts[1], out var free))
                    _sgMemUsage = (int)Math.Round((1.0 - (double)free / total) * 100);
                int.TryParse(parts[2], out _sgMemFreq);
                _sgMemTime = DateTime.UtcNow;
            }
        }
        catch { }
    }

    public int DiskUsage { get { RefreshDisk(); return _sgDiskUsage; } }
    public int DiskTotalGB { get { RefreshDisk(); return _sgDiskTotal; } }
    public int DiskFreeGB { get { RefreshDisk(); return _sgDiskFree; } }

    private void RefreshDisk()
    {
        if ((DateTime.UtcNow - _sgDiskTime).TotalSeconds < 5) return;
        try
        {
            long total = 0, used = 0;

            // Step 1: Use Get-Disk to enumerate local disk numbers, exclude iSCSI (BusType=9)
            var localDiskNums = new System.Collections.Generic.HashSet<int>();
            using (var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("powershell",
                    "-NoProfile -Command \"Get-Disk | Where-Object BusType -ne 'iSCSI' | Select-Object -ExpandProperty Number\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            })
            {
                p.Start();
                if (!p.WaitForExit(5000)) { p.Kill(); return; }
                var reader = p.StandardOutput.ReadToEnd();
                foreach (var line in reader.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(line.Trim(), out var num))
                        localDiskNums.Add(num);
                }
            }
            if (localDiskNums.Count == 0) return;

            // Step 2: Query Win32_LogicalDisk for local fixed drives (DriveType=3)
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_LogicalDisk WHERE DriveType=3");
            using var diskCollection = searcher.Get();
            foreach (System.Management.ManagementObject disk in diskCollection)
            {
                using (disk)
                {
                    var deviceId = disk["DeviceID"]?.ToString();
                    if (string.IsNullOrEmpty(deviceId)) continue;

                    // Step 3: Navigate LogicalDisk → Partition → DiskDrive, check disk Index
                    var assocQuery = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{deviceId}'}} " +
                                     "WHERE AssocClass=Win32_LogicalDiskToPartition";
                    using var assocSearcher = new System.Management.ManagementObjectSearcher(assocQuery);
                    using var partCollection = assocSearcher.Get();
                    bool onLocalDisk = false;
                    foreach (System.Management.ManagementObject part in partCollection)
                    {
                        using (part)
                        {
                            var partPath = part["__PATH"]?.ToString();
                            if (string.IsNullOrEmpty(partPath)) continue;
                            var ddQuery = $"ASSOCIATORS OF {{{partPath}}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
                            using var ddSearcher = new System.Management.ManagementObjectSearcher(ddQuery);
                            using var ddCollection = ddSearcher.Get();
                            foreach (System.Management.ManagementObject dd in ddCollection)
                            {
                                using (dd)
                                {
                                    var idx = dd["Index"]?.ToString();
                                    if (idx != null && int.TryParse(idx, out var diskNum) && localDiskNums.Contains(diskNum))
                                        onLocalDisk = true;
                                }
                            }
                        }
                    }
                    if (!onLocalDisk) continue;

                    long size = Convert.ToInt64(disk["Size"]);
                    long free = Convert.ToInt64(disk["FreeSpace"]);
                    total += size;
                    used += size - free;
                }
            }
            if (total > 0)
            {
                _sgDiskTotal = (int)Math.Round(total / (1024.0 * 1024 * 1024));
                _sgDiskFree = (int)Math.Round((total - used) / (1024.0 * 1024 * 1024));
                _sgDiskUsage = (int)Math.Round((double)used / total * 100);
                _sgDiskTime = DateTime.UtcNow;
            }
        }
        catch { }
    }

    // ================================================================
    // 健康检查
    // ================================================================

    /// <summary>验证驱动和 EC 通信是否正常 (读 CPU 温度, 正常应为 20-110)</summary>
    public bool HealthCheck()
    {
        try
        {
            var temp = CpuTemperature;
            return temp > 20 && temp < 110;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cpuFreqCounter?.Dispose();
        _io.Dispose();
    }
}
