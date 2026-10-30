// SPDX-License-Identifier: GPL-3.0-only
//
// SystemInfoService — 扩展系统信息服务（C# 直查，替代 sysinfo-ext.ps1 子进程架构）
// ================================================================================
// 数据源：WMI（System.Management）+ 注册表 + nvidia-smi + powercfg
// 缓存三级：静态 24h / 半静态 10min / 易变 60s
// 每项查询独立 try/catch：单项失败返回空值，不影响其余字段（对齐旧 PS1 SilentlyContinue 行为）
// 已在本机（Lecoo Bellator N176 / Ryzen 9 8940HX）逐项验证数据源可用性

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Douzhanzhe.API;

public sealed class SystemInfoService
{
    private readonly object _lock = new();

    private static readonly TimeSpan StaticTtl = TimeSpan.FromHours(24);   // BIOS/OS/内存规格等，几乎不变
    private static readonly TimeSpan MidTtl = TimeSpan.FromMinutes(10);    // 磁盘/电池报告/网卡/显示器
    private static readonly TimeSpan NvTtl = TimeSpan.FromSeconds(60);     // nvidia-smi 驱动/VBIOS
    private static readonly TimeSpan BattPctTtl = TimeSpan.FromSeconds(60);// 电池百分比

    // ── 静态信息 ──
    private DateTime _staticTime = DateTime.MinValue;
    private bool _staticLoaded;
    private string _biosVersion = "", _biosDate = "", _boardInfo = "";
    private string _osName = "", _osBuild = "", _osVersion = "", _osInstallDate = "";
    private string _serialNumber = "", _ecVersion = "", _memoryType = "", _systemManufacturer = "";
    private int _memorySlots;
    private int _memorySpeed;
    private List<object> _sticks = new();

    // ── 半静态（10min）──
    private DateTime _midTime = DateTime.MinValue;
    private List<object> _disks = new();
    private string _diskModel = "", _diskInterface = "";
    private int _diskHealth;
    private string _displaySpec = "", _networkCard = "";
    private string _battChemistry = "", _battStatus = "";
    private int _battCycle, _battDesign, _battFull;

    // ── 易变（60s）──
    private DateTime _nvTime = DateTime.MinValue;
    private string _nvDriver = "", _nvVbios = "";
    private DateTime _battPctTime = DateTime.MinValue;
    private int _battPercent = -1;

    // ── 独立小缓存（供 /api/system/info 用）──
    private string _directX = "";

    /// <summary>设备厂商（注册表直读，毫秒级）。供 /api/system/info 的 systemManufacturer。</summary>
    public string SystemManufacturer
    {
        get { lock (_lock) { EnsureStatic(); return _systemManufacturer; } }
    }

    /// <summary>DirectX 版本（注册表映射，如 "12"）。供 /api/system/info 的 gpuDx。</summary>
    public string DirectXVersion
    {
        get { lock (_lock) { EnsureStatic(); return _directX; } }
    }

    /// <summary>
    /// 构造 /api/system/info-ext 响应。结构向后兼容（旧字段名全保留）+ 新增字段。
    /// </summary>
    public object GetExtSnapshot()
    {
        lock (_lock)
        {
            EnsureStatic();
            if ((DateTime.UtcNow - _midTime) >= MidTtl) { LoadMid(); _midTime = DateTime.UtcNow; }
            if ((DateTime.UtcNow - _nvTime) >= NvTtl) { LoadNvidia(); _nvTime = DateTime.UtcNow; }
            if ((DateTime.UtcNow - _battPctTime) >= BattPctTtl)
            {
                try { using var b = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining FROM Win32_Battery").Get().Cast<ManagementObject>().FirstOrDefault(); _battPercent = b == null ? -1 : Convert.ToInt32(b["EstimatedChargeRemaining"]); }
                catch { _battPercent = -1; }
                _battPctTime = DateTime.UtcNow;
            }

            return new
            {
                // ── 旧字段（结构兼容）──
                biosVersion = _biosVersion,
                biosDate = _biosDate,
                osName = _osName,
                osBuild = _osBuild,
                boardInfo = _boardInfo,
                nvDriver = _nvDriver,
                nvVbios = _nvVbios,
                disks = _disks,
                sticks = _sticks,
                battPercent = _battPercent,
                battDesign = _battDesign,
                battFull = _battFull,
                // ── 新增字段 ──
                osVersion = _osVersion,
                osInstallDate = _osInstallDate,
                serialNumber = _serialNumber,
                ecVersion = _ecVersion,
                memoryType = _memoryType,
                memorySlots = _memorySlots,
                memorySpeed = _memorySpeed,
                diskModel = _diskModel,
                diskInterface = _diskInterface,
                diskHealth = _diskHealth > 0 ? (int?)_diskHealth : null,
                battChemistry = _battChemistry,
                battCycle = _battCycle,
                battStatus = _battStatus,
                displaySpec = _displaySpec,
                networkCard = _networkCard,
            };
        }
    }

    /// <summary>手动失效全部缓存（供未来刷新按钮扩展）。</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _staticLoaded = false;
            _midTime = DateTime.MinValue;
            _nvTime = DateTime.MinValue;
            _battPctTime = DateTime.MinValue;
        }
    }

    // ────────────────────────── 静态加载 ──────────────────────────

    private void EnsureStatic()
    {
        if (_staticLoaded && (DateTime.UtcNow - _staticTime) < StaticTtl) return;
        LoadStatic();
        _staticTime = DateTime.UtcNow;
    }

    private void LoadStatic()
    {
        try
        {
            using var bios = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS").Get().Cast<ManagementObject>().FirstOrDefault();
            if (bios != null)
            {
                _biosVersion = ToStr(bios["SMBIOSBIOSVersion"]);
                _serialNumber = ToStr(bios["SerialNumber"]);
                if (bios["ReleaseDate"] is DateTime d) _biosDate = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        catch { }

        try
        {
            using var board = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard").Get().Cast<ManagementObject>().FirstOrDefault();
            if (board != null) _boardInfo = $"{ToStr(board["Manufacturer"])} {ToStr(board["Product"])}".Trim();
        }
        catch { }

        try
        {
            using var os = new ManagementObjectSearcher("SELECT Caption, BuildNumber, Version, InstallDate FROM Win32_OperatingSystem").Get().Cast<ManagementObject>().FirstOrDefault();
            if (os != null)
            {
                _osName = ToStr(os["Caption"]).Replace("Microsoft", "").Trim();
                _osBuild = ToStr(os["BuildNumber"]);
                _osVersion = ToStr(os["Version"]);
                if (os["InstallDate"] is DateTime d) _osInstallDate = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
        catch { }

        try
        {
            var types = new List<string>();
            var speeds = new List<int>();
            using var mems = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Speed, Manufacturer, SMBIOSMemoryType FROM Win32_PhysicalMemory").Get();
            foreach (var m in mems.Cast<ManagementObject>())
            {
                var speed = Convert.ToInt32(m["ConfiguredClockSpeed"] ?? m["Speed"]);
                speeds.Add(speed);
                _sticks.Add(new
                {
                    sizeGB = (int)Math.Round(Convert.ToDouble(m["Capacity"]) / 1073741824.0),
                    speed,
                    manufacturer = ToStr(m["Manufacturer"]),
                });
                var smbios = Convert.ToInt32(m["SMBIOSMemoryType"]);
                var t = smbios switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => "" };
                if (!string.IsNullOrEmpty(t) && !types.Contains(t)) types.Add(t);
            }
            _memoryType = string.Join("/", types);
            _memorySpeed = speeds.Count > 0 ? speeds.Max() : 0;
        }
        catch { }

        try
        {
            var slots = 0;
            foreach (var a in new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Get().Cast<ManagementObject>())
                slots += Convert.ToInt32(a["MemoryDevices"]);
            _memorySlots = slots;
        }
        catch { }

        // 注册表 BIOS 描述区（msinfo32 同源）：EC 固件版本 + 设备厂商
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key != null)
            {
                var ecMajor = key.GetValue("ECFirmwareMajorRelease");
                var ecMinor = key.GetValue("ECFirmwareMinorRelease");
                if (ecMajor is int em && ecMinor is int en) _ecVersion = $"{em}.{en:D2}";
                _systemManufacturer = ToStr(key.GetValue("SystemManufacturer"));
            }
        }
        catch { }

        // DirectX 版本：4.09.00.0904+ → 12
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
            var v = ToStr(key?.GetValue("Version"));
            if (v.StartsWith("4.09.00.09", StringComparison.Ordinal) && v.Length >= 12 && int.TryParse(v.AsSpan(9), out var build))
                _directX = build >= 904 ? "12" : "11";
            else if (!string.IsNullOrEmpty(v)) _directX = v;
        }
        catch { }

        _staticLoaded = true;
    }

    // ────────────────────────── 半静态（10min）──────────────────────────

    private void LoadMid()
    {
        // 磁盘：型号/容量/接口/健康度（顶层三个字段取内盘 = 容量最大的非 USB 盘）
        try
        {
            var disks = new List<(string Model, long SizeGB, string Pnp, string Status)>();
            foreach (var d in new ManagementObjectSearcher("SELECT Model, Size, PNPDeviceID, Status FROM Win32_DiskDrive").Get().Cast<ManagementObject>())
            {
                var size = d["Size"] == null ? 0 : Convert.ToInt64(d["Size"]);
                disks.Add((ToStr(d["Model"]), size / 1073741824, ToStr(d["PNPDeviceID"]), ToStr(d["Status"])));
            }

            _disks = disks.Select(d =>
            {
                var iface = d.Pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase) ? "NVMe"
                          : d.Pnp.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase) ? "USB" : "SATA";
                var health = d.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
                return (object)new { model = d.Model, sizeGB = (int)d.SizeGB, @interface = iface, health };
            }).ToList();

            var internalDisk = disks.Where(d => !d.Pnp.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(d => d.SizeGB).FirstOrDefault();
            if (internalDisk.Model != null)
            {
                _diskModel = internalDisk.Model;
                _diskInterface = internalDisk.Pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase) ? "NVMe" : "SATA";
                _diskHealth = internalDisk.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
            }
        }
        catch { }

        // 显示器：第一个有当前分辨率的适配器（虚拟屏无分辨率，自然被过滤）
        try
        {
            foreach (var v in new ManagementObjectSearcher("SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController").Get().Cast<ManagementObject>())
            {
                if (v["CurrentHorizontalResolution"] == null) continue;
                var w = Convert.ToInt32(v["CurrentHorizontalResolution"]);
                var h = Convert.ToInt32(v["CurrentVerticalResolution"]);
                var hz = Convert.ToInt32(v["CurrentRefreshRate"]);
                if (w <= 0 || h <= 0) continue;
                _displaySpec = $"{w}×{h} @{hz}Hz";
                break;
            }
        }
        catch { }

        // 网卡：物理 + 已启用，排除常见虚拟网卡关键字
        try
        {
            foreach (var n in new ManagementObjectSearcher("SELECT Name FROM Win32_NetworkAdapter WHERE PhysicalAdapter=true AND NetEnabled=true").Get().Cast<ManagementObject>())
            {
                var name = ToStr(n["Name"]);
                if (name.Length == 0) continue;
                if (VirtualAdapterPattern.IsMatch(name)) continue;
                _networkCard = name;
                break;
            }
        }
        catch { }

        // 电池：类型/状态（Win32_Battery）+ 设计/满充容量与循环次数（powercfg 报告）
        try
        {
            var b = new ManagementObjectSearcher("SELECT Chemistry, BatteryStatus FROM Win32_Battery").Get().Cast<ManagementObject>().FirstOrDefault();
            if (b != null)
            {
                _battChemistry = Convert.ToInt32(b["Chemistry"]) switch
                {
                    3 => "铅酸电池", 4 => "镍镉电池", 5 => "镍氢电池",
                    6 => "锂离子电池", 8 => "锂聚合物电池",
                    _ => "", // 1/2=未知 → 留空，前端兜底"锂离子电池组"
                };
                _battStatus = Convert.ToInt32(b["BatteryStatus"]) switch
                {
                    1 => "电池放电中", 2 => "状态良好", 9 => "充电中",
                    _ => "",
                };
            }
        }
        catch { }

        try
        {
            var report = Path.Combine(Path.GetTempPath(), "dz_batt_report.html");
            using (var p = Process.Start(new ProcessStartInfo("powercfg", $"/batteryreport /output \"{report}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })) { p?.WaitForExit(5000); }
            if (File.Exists(report))
            {
                var html = File.ReadAllText(report);
                _battDesign = ParseInt(Regex.Match(html, "DESIGN CAPACITY.*?(\\d[\\d,]*)\\s*mWh", RegexOptions.Singleline | RegexOptions.IgnoreCase));
                _battFull = ParseInt(Regex.Match(html, "FULL CHARGE CAPACITY.*?(\\d[\\d,]*)\\s*mWh", RegexOptions.Singleline | RegexOptions.IgnoreCase));
                _battCycle = ParseInt(Regex.Match(html, "CYCLE COUNT.{0,200}?</TD>\\s*<TD[^>]*>\\s*([\\d,]+)", RegexOptions.Singleline | RegexOptions.IgnoreCase));
                File.Delete(report);
            }
        }
        catch { }
    }

    // ────────────────────────── 易变（60s）──────────────────────────

    private void LoadNvidia()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=driver_version,vbios_version --format=csv,noheader")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (p != null && p.WaitForExit(3000))
            {
                var line = p.StandardOutput.ReadToEnd().Trim();
                var parts = line.Split(',');
                if (parts.Length >= 2)
                {
                    _nvDriver = parts[0].Trim();
                    _nvVbios = parts[1].Trim();
                }
            }
            else { try { p?.Kill(); } catch { } }
        }
        catch { }
    }

    // ────────────────────────── helpers ──────────────────────────

    private static readonly Regex VirtualAdapterPattern = new(
        "Virtual|VMware|Wintun|TAP|Hyper-V|Loopback|vEthernet|Tailscale|ZeroTier",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ToStr(object? o) => o?.ToString()?.Trim() ?? "";

    private static int ParseInt(Match m)
        => m.Success ? int.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) ? v : 0 : 0;
}
