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

    public int SetVrmCurrent(uint mA) { return 0; }

    public uint SendRawSmuCommand(uint cmd, uint arg0) { return 0xFF; }

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

    public uint ReadSmnRegister(uint addr) { return 0; }
}
