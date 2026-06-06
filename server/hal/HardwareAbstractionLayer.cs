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
    private DateTime _sgGpuTime = DateTime.MinValue;
    private int _sgCpuPct;
    private DateTime _sgCpuTime = DateTime.MinValue;
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
    }

    // ================================================================
    // EC 偏移量常量 (相对 0xFE800400)
    // ================================================================

    private const uint OFF_KBNL   = 0x9A;  // 键盘背光等级 0-3
    private const uint OFF_FNHK   = 0x20;  // bit3: Fn 锁
    private const uint OFF_CALK   = 0x25;  // bit1: CapsLock
    private const uint OFF_NULK   = 0x25;  // bit2: NumLock
    private const uint OFF_FNRC   = 0x25;  // bit3: Fn 状态 (只读?)
    private const uint OFF_ITSM   = 0xE4;  // 智能散热模式 (0-3)
    private const uint OFF_GPUT   = 0xE0;  // GPU 温度
    private const uint OFF_CPUT   = 0xE1;  // CPU 温度
    private const uint OFF_F1HI   = 0x9B;  // CPU 风扇转速高字节
    private const uint OFF_F1LO   = 0x9C;  // CPU 风扇转速低字节
    private const uint OFF_F3HI   = 0x96;  // GPU 风扇转速高字节
    private const uint OFF_F3LO   = 0x97;  // GPU 风扇转速低字节
    private const uint OFF_KBTY   = 0x99;  // 键盘类型

    private const uint OFF_SMPR   = 0x28;  // SMU command
    private const uint OFF_SMST   = 0x29;  // SMU status
    private const uint OFF_SMAD   = 0x2A;  // SMU address
    private const uint OFF_SDAT   = 0x2C;  // SMU data (16-bit)

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

    /// <summary>CPU 温度 (摄氏度) — EC IO 端口 0x1C</summary>
    public byte CpuTemperature => _io.ReadEc(0x1C);

    /// <summary>GPU 温度 (摄氏度) — 优先物理内存，回退 nvidia-smi</summary>
    public byte GpuTemperature
    {
        get
        {
            // 尝试从物理内存读取 GPUT @ 0xFE8004E0
            try
            {
                var val = _io.ReadPhys(DriverBridge.EC_BASE + OFF_GPUT);
                if (val > 0)
                {
                    _lastGpuTemp = val;
                    _lastGpuTempTime = DateTime.UtcNow;
                    return val;
                }
            }
            catch { /* fallback */ }

            // 物理内存返回 0，回退 nvidia-smi（限频 2s）
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

    /// <summary>CPU 风扇转速 (RPM) — EC IO 端口 0x9D/0x9E</summary>
    public ushort CpuFanRpm
    {
        get
        {
            // 双读仲裁：最多 3 次，取首个非零值，消除 EC 16 位竞态
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

    /// <summary>GPU 风扇转速 (RPM) — EC IO 端口 0x96/0x97</summary>
    public ushort GpuFanRpm
    {
        get
        {
            // 双读仲裁：最多 3 次，取首个非零值，消除 EC 16 位竞态
            for (int i = 0; i < 3; i++)
            {
                var hi = _io.ReadEc(0x96);
                var lo = _io.ReadEc(0x97);
                var val = (ushort)((hi << 8) | lo);
                if (val != 0) return val;
            }
            return 0;
        }
    }

    // ================================================================
    // 风扇目标转速控制 — EC 0xB2 (CPU) / 0xB3 (GPU)
    // 写入公式: val = round(rpm / maxRpm * 255)
    // 读取公式: rpm = val * maxRpm / 255
    // ================================================================

    /// <summary>CPU 风扇目标转速 (RPM) — EC 0x5F (value = RPM / 100)</summary>
    public ushort CpuFanControl
    {
        get
        {
            var raw = _io.ReadEc(0x5F);
            return (ushort)(raw * 100);
        }
        set
        {
            var raw = (byte)(Math.Clamp(value / 100, 0, 255));
            _io.WriteEc(0x5F, raw);
        }
    }

    /// <summary>GPU 风扇目标转速 (RPM) — EC 0x5B (value = RPM / 100)</summary>
    public ushort GpuFanControl
    {
        get
        {
            var raw = _io.ReadEc(0x5B);
            return (ushort)(raw * 100);
        }
        set
        {
            var raw = (byte)(Math.Clamp(value / 100, 0, 255));
            _io.WriteEc(0x5B, raw);
        }
    }

    /// <summary>键盘类型</summary>
    public byte KeyboardType => _io.ReadEc(0x99);

    // ================================================================
    // 系统开关 — 读写控制 (通过 EC IO 端口)
    // ================================================================

    /// <summary>Fn 锁状态</summary>
    public bool FnLock
    {
        get => (_io.ReadEc((byte)OFF_FNHK) & (1 << BIT_FNHK)) != 0;
                set
        {
            byte val = _io.ReadEc((byte)OFF_FNHK);
            if (value) val |= (byte)(1 << BIT_FNHK);
            else val &= unchecked((byte)~(1 << BIT_FNHK));
            _io.WritePhys(DriverBridge.EC_BASE + OFF_FNHK, val);
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
            // KBNL 写入不走缓存映射，走独立 SetPhysLong（预映射区域写入无效）
            _io.WritePhys(DriverBridge.EC_BASE + OFF_KBNL, v);
        }
    }

    // ================================================================
    // 性能模式 / 散热模式
    // ================================================================

    /// <summary>散热模式寄存器 (ITSM) — 写入走 WritePhys (SetPhysLong)</summary>
    public byte ThermalMode
    {
        get => _io.ReadEc((byte)OFF_ITSM);
        set => _io.WritePhys(DriverBridge.EC_BASE + OFF_ITSM, value);
    }

    // ================================================================
    // SMU 通信 (预留)
    // ================================================================

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

    /// <summary>SMU 命令寄存器</summary>
    public byte SmuCommand
    {
        get => _io.ReadEc((byte)OFF_SMPR);
        set => _io.WriteEc((byte)OFF_SMPR, value);
    }

    /// <summary>SMU 状态寄存器</summary>
    public byte SmuStatus => _io.ReadEc((byte)OFF_SMST);

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
        get
        {
            try
            {
                ulong addr = DSAD_BASE + (0x0BUL << 1);
                var raw = _io.ReadPhys(addr);
                return (raw & 0x08) != 0;
            }
            catch { return false; }
        }
        set
        {
            try
            {
                ulong addr = DSAD_BASE + (0x0BUL << 1);
                _io.WriteBit(addr, 3, value);
            }
            catch { }
        }
    }

    // ================================================================
    // SMI 触发 (GSMI)
    // ================================================================

    /// <summary>通过 SMI 写入 APM 端口</summary>
    public void SendSmi(byte value)
    {
        // GSMI: APMD = arg, APMC = 0xE4, Sleep(2ms)
        _io.WriteIo(0x72, value);  // APMD
        _io.WriteIo(0x73, 0xE4);   // APMC
        Thread.Sleep(2);
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
            if ((DateTime.UtcNow - _sgCpuTime).TotalSeconds < 2 && _sgCpuPct > 0) return _sgCpuPct;
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor | Where-Object Name -eq '_Total').PercentProcessorTime\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                p.Start();
                if (!p.WaitForExit(3000)) { p.Kill(); return _sgCpuPct; }
                var line = p.StandardOutput.ReadToEnd().Trim();
                if (int.TryParse(line, out var v) && v > 0 && v <= 100)
                {
                    _sgCpuPct = v;
                    _sgCpuTime = DateTime.UtcNow;
                    return v;
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
            if ((DateTime.UtcNow - _sgDiskTime).TotalSeconds < 0.5 && _cpuFreqCache > 0) return _cpuFreqCache;
            return GetCpuFreqDirect();
        }
    }

    private float _cpuFreqCache;
    private DateTime _cpuFreqTime = DateTime.MinValue;
    private float GetCpuFreqDirect()
    {
        if ((DateTime.UtcNow - _cpuFreqTime).TotalSeconds < 1 && _cpuFreqCache > 0)
            return _cpuFreqCache;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation | Where-Object Name -eq '_Total').PercentProcessorPerformance\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.Start();
            if (!p.WaitForExit(3000)) { p.Kill(); return _cpuFreqCache > 0 ? _cpuFreqCache : 2.4f; }
            var line = p.StandardOutput.ReadToEnd().Trim();
            if (float.TryParse(line, out var pct) && pct > 0)
            {
                _cpuFreqCache = (float)(2.4 * (pct / 100.0));
                _cpuFreqTime = DateTime.UtcNow;
                return _cpuFreqCache;
            }
        }
        catch { }
        return _cpuFreqCache > 0 ? _cpuFreqCache : 2.4f;
    }

    public int CpuCores => Environment.ProcessorCount;

    public byte GpuUsage { get { RefreshGpu(); return _sgGpuUsage; } }
    public float GpuFreq { get { RefreshGpu(); return _sgGpuFreq; } }
    public uint GpuVram { get { RefreshGpu(); return _sgGpuVram; } }
    public float GpuVramUsed { get { RefreshGpu(); return _sgGpuVramUsed; } }

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
                    Arguments = "--query-gpu=utilization.gpu,clocks.current.graphics,memory.total,memory.used --format=csv,noheader,nounits",
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
                byte.TryParse(parts[0].Trim(), out _sgGpuUsage);
                if (float.TryParse(parts[1].Trim(), out var f)) _sgGpuFreq = f / 1000f;
                if (float.TryParse(parts[2].Trim(), out var t)) _sgGpuVram = (uint)Math.Round(t / 1024.0);
                if (float.TryParse(parts[3].Trim(), out var u)) _sgGpuVramUsed = (float)(u / 1024.0);
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
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);
            long total = 0, used = 0;
            foreach (var d in drives) { total += d.TotalSize; used += d.TotalSize - d.AvailableFreeSpace; }
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
        _io.Dispose();
    }
}
