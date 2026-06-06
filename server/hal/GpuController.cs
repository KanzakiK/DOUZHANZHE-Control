// SPDX-License-Identifier: MIT
// GpuController -- nvidia-smi 子进程封装

using System;
using System.Diagnostics;
using System.Linq;

namespace Douzhanzhe.HAL;

public sealed class GpuController
{
    private const int TimeoutMs = 5000;

    private string RunNvidiaSmi(string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        p.Start();
        if (!p.WaitForExit(TimeoutMs))
        {
            p.Kill();
            throw new TimeoutException("nvidia-smi timed out");
        }
        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException("nvidia-smi failed (exit=" + p.ExitCode + "): " + err);
        }
        return p.StandardOutput.ReadToEnd().Trim();
    }

    public void SetMaxGpuClock(int maxMHz)
    {
        RunNvidiaSmi("--lock-gpu-clocks=0," + maxMHz);
    }

    public void SetLockGpuClocks(int minMHz, int maxMHz)
    {
        RunNvidiaSmi("--lock-gpu-clocks=" + minMHz + "," + maxMHz);
    }

    public void SetExactGpuClock(int mhz)
    {
        RunNvidiaSmi("--lock-gpu-clocks=" + mhz);
    }

    public void ResetGpuClocks()
    {
        RunNvidiaSmi("--reset-gpu-clocks");
    }

    public void SetLockMemoryClocks(int minMHz, int maxMHz)
    {
        RunNvidiaSmi("--lock-memory-clocks=" + minMHz + "," + maxMHz);
    }

    public void SetMaxMemoryClock(int maxMHz)
    {
        RunNvidiaSmi("--lock-memory-clocks=0," + maxMHz);
    }

    public void ResetMemoryClocks()
    {
        RunNvidiaSmi("--reset-memory-clocks");
    }

    public GpuClockInfo GetClockInfo()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.graphics,clocks.current.memory,power.draw --format=csv,noheader,nounits");
        var parts = output.Split(",");
        var info = new GpuClockInfo();
        if (parts.Length >= 1) float.TryParse(parts[0].Trim(), out info.CoreClockMHz);
        if (parts.Length >= 2) float.TryParse(parts[1].Trim(), out info.MemoryClockMHz);
        if (parts.Length >= 3) float.TryParse(parts[2].Trim(), out info.PowerDrawW);
        return info;
    }

    /// <summary>GPU 基准频率 (supported-clocks 第1行 = 固件默认最大频率)</summary>
    public int GetBaseClock()
    {
        var output = RunNvidiaSmi("--query-supported-clocks=gr --format=csv,noheader,nounits");
        var lines = output.Split(new char[] { "\n"[0], "\r"[0] }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return 2700;
        if (int.TryParse(lines[0].Trim(), out var val) && val > 0)
            return val;
        return 2700;
    }

    /// <summary>GPU 硬件最大频率</summary>
    public int GetMaxClock()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.max.graphics --format=csv,noheader,nounits");
        if (int.TryParse(output.Trim(), out var val) && val > 0)
            return val;
        return 3090;
    }
}

public struct GpuClockInfo
{
    public float CoreClockMHz;
    public float MemoryClockMHz;
    public float PowerDrawW;
}
