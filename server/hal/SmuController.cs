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
    private readonly object _ryzenAdjLock = new(); // 序列化所有 ryzenadj 调用，防止 SMN 总线并发冲突

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
        lock (_ryzenAdjLock)
        {
            var sw = Stopwatch.StartNew();
            var argStr = string.Join(" ", args);
            var psi = new ProcessStartInfo
            {
                FileName = _ryzenadjPath,
                Arguments = argStr,
                WorkingDirectory = _toolsDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) { AppLog.Write("SMU", $"ryzenadj {argStr} → null (进程启动失败)"); return -1; }
            if (!proc.WaitForExit(15000)) { try { proc.Kill(); } catch { } AppLog.Write("SMU", $"ryzenadj {argStr} → timeout 15s"); }
            var rc = proc.ExitCode;
            sw.Stop();
            // 只记录真正的错误；0/1=成功，SMU_OK_CRASH=上游已知崩溃(写入已完成)
            if (rc != 0 && rc != 1 && rc != SMU_OK_CRASH)
                AppLog.Write("SMU", $"ryzenadj {argStr} → exit={rc} ({sw.ElapsedMilliseconds}ms)");
            return rc;
        }
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

    public int SetTurboDisabled(bool disabled)
    {
        var cmd = disabled ? "--power-saving=1" : "--max-performance=1";
        var r = RyzenAdj(cmd);
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
    }

    /// <summary>批量设置所有 SMU 参数，单次 ryzenadj 调用</summary>
    public int BatchApply(uint? stapmMw, uint? fastMw, uint? slowMw, uint? tempC, int? coAllMv, bool? turboOff)
    {
        var args = new List<string>();
        if (stapmMw.HasValue) args.Add("--stapm-limit=" + stapmMw.Value);
        if (fastMw.HasValue) args.Add("--fast-limit=" + fastMw.Value);
        if (slowMw.HasValue) args.Add("--slow-limit=" + slowMw.Value);
        if (tempC.HasValue) args.Add("--tctl-temp=" + tempC.Value);
        if (coAllMv.HasValue) args.Add("--set-coall=" + coAllMv.Value);
        if (turboOff.HasValue) args.Add(turboOff.Value ? "--power-saving=1" : "--max-performance=1");
        if (args.Count == 0) return 0;
        var r = RyzenAdj(args.ToArray());
        return r == 0 || r == 1 || r == SMU_OK_CRASH ? 0 : r;
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
            cpuFreqLimit = false,
            turboDisabled = true,
            probe = true,
            vrmCurrent = false,
            rawCommand = false,
            readRegister = false,
        };
    }

}
