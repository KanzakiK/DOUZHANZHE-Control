// SPDX-License-Identifier: MIT
//
// SmuController — AMD SMU 控制器（ryzenadj.exe 子进程）
// =============================================================

using System;
using System.Diagnostics;
using System.IO;

namespace Douzhanzhe.HAL;

public sealed class SmuController
{
    private readonly string _ryzenadjPath;
    private readonly string _toolsDir;

    public SmuController()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[] {
            Path.Combine(baseDir, "ryzenadj.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "tools", "ryzenadj.exe"),
            Path.Combine(baseDir, "..", "..", "..", "tools", "ryzenadj.exe"),
            Path.Combine(baseDir, "..", "tools", "ryzenadj.exe"),
        };
        foreach (var p in candidates)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full))
            {
                _ryzenadjPath = full;
                _toolsDir = Path.GetDirectoryName(full);
                break;
            }
        }
        if (_ryzenadjPath == null)
        {
            _ryzenadjPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "tools", "ryzenadj.exe"));
            _toolsDir = Path.GetDirectoryName(_ryzenadjPath);
        }
    }

    private int RyzenAdj(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ryzenadjPath,
            Arguments = string.Join(" ", args),
            WorkingDirectory = _toolsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc == null) return -1;
        proc.WaitForExit(15000);
        return proc.ExitCode;
    }

    const int SMU_OK_CRASH = -1073741819; // 0xC0000005: ryzenadj write succeeds then crashes on exit

    public int SetPowerLimit(uint mW)
    {
        var r = RyzenAdj("--stapm-limit=" + mW, "--fast-limit=" + mW, "--slow-limit=" + mW);
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }

    public int SetTempLimit(uint celsius)
    {
        var r = RyzenAdj("--tctl-temp=" + celsius);
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }
    public int SetShortPowerLimit(uint fastMw, uint slowMw)
    {
        var r = RyzenAdj("--fast-limit=" + fastMw, "--slow-limit=" + slowMw);
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }

    public int SetCurveOptimizer(int mV)
    {
        var r = RyzenAdj("--set-coall=" + mV);
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }

    public int SetCpuFreqLimit(uint mhz)
    {
        var r = RyzenAdj("--max-cpuclk=" + mhz);
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }

    public int SetTurboDisabled(bool disabled)
    {
        var cmd = disabled ? "--power-saving=1" : "--max-performance=1";
        var r = RyzenAdj(cmd);
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }

    /// <summary>批量设置所有 SMU 参数，单次 ryzenadj 调用</summary>
    public int BatchApply(uint? stapmMw, uint? fastMw, uint? slowMw, uint? tempC, int? coAllMv, uint? maxClkMhz, bool? turboOff)
    {
        var args = new List<string>();
        if (stapmMw.HasValue) args.Add("--stapm-limit=" + stapmMw.Value);
        if (fastMw.HasValue) args.Add("--fast-limit=" + fastMw.Value);
        if (slowMw.HasValue) args.Add("--slow-limit=" + slowMw.Value);
        if (tempC.HasValue) args.Add("--tctl-temp=" + tempC.Value);
        if (coAllMv.HasValue) args.Add("--set-coall=" + coAllMv.Value);
        if (maxClkMhz.HasValue) args.Add("--max-cpuclk=" + maxClkMhz.Value);
        if (turboOff.HasValue) args.Add(turboOff.Value ? "--power-saving=1" : "--max-performance=1");
        if (args.Count == 0) return 0;
        var r = RyzenAdj(args.ToArray());
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }

    

    public int SetVrmCurrent(uint mA) => throw new System.NotSupportedException("VRM current not supported on this hardware");

    public uint SendRawSmuCommand(uint cmd, uint arg0) => throw new System.NotSupportedException("Raw SMU command not supported, use POST /api/smu/set instead");

    /// <summary>抓取 ryzenadj -i 输出（SMU 参数快照），用于诊断 SetThermalMode 是否影响 SMU</summary>
    public string DumpInfo()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ryzenadjPath,
                Arguments = "-i",
                WorkingDirectory = _toolsDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "(process start failed)";
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            return string.IsNullOrWhiteSpace(stdout) ? "(empty output)" : stdout;
        }
        catch (Exception ex) { return $"(error: {ex.Message})"; }
    }

    public bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ryzenadjPath,
                Arguments = "-i",
                WorkingDirectory = _toolsDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(15000);
            return proc.ExitCode == 0 || proc.ExitCode == 1 || proc.ExitCode == -1073741819;
        }
        catch { return false; }
    }

    public object GetCapabilities()
    {
        return new
        {
            powerLimit = true,
            tempLimit = true,
            shortPowerLimit = true,
            curveOptimizer = true,
            cpuFreqLimit = true,
            turboDisabled = true,
            probe = true,
            vrmCurrent = false,
            rawCommand = false,
            readRegister = false,
        };
    }

    public uint ReadSmnRegister(uint addr) => throw new System.NotSupportedException("SMN register read not supported on this hardware");
}
