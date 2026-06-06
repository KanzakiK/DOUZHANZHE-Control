// SPDX-License-Identifier: MIT
//
// GpuController — nvidia-smi 子进程封装
// =========================================
// 职责：
//   在 nvidia-smi CLI 之上提供 GPU 核心/显存频率控制接口。
//   所有操作通过 Process.Start 启动 nvidia-smi 子进程完成。
//
// 参考:
//   nvidia-smi --help 确认参数
//   RTX 5060 GDDR7: 核心基线 1342MHz, 显存基线 9001MHz

using System;
using System.Diagnostics;

namespace Douzhanzhe.HAL;

public sealed class GpuController
{
    private const int TimeoutMs = 5000;

    /// <summary>运行 nvidia-smi 命令并返回标准输出</summary>
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

    /// <summary>锁 GPU 核心频率: --lock-gpu-clocks=min,max</summary>
    public void SetLockGpuClocks(int minMHz, int maxMHz)
    {
        RunNvidiaSmi("--lock-gpu-clocks=" + minMHz + "," + maxMHz);
    }

    /// <summary>重置 GPU 核心频率为默认: --reset-gpu-clocks</summary>
    public void ResetGpuClocks()
    {
        RunNvidiaSmi("--reset-gpu-clocks");
    }

    /// <summary>锁显存频率: --lock-memory-clocks=min,max</summary>
    public void SetLockMemoryClocks(int minMHz, int maxMHz)
    {
        RunNvidiaSmi("--lock-memory-clocks=" + minMHz + "," + maxMHz);
    }

    /// <summary>重置显存频率为默认: --reset-memory-clocks</summary>
    public void ResetMemoryClocks()
    {
        RunNvidiaSmi("--reset-memory-clocks");
    }

    /// <summary>获取 GPU 时钟/功率信息</summary>
    public GpuClockInfo GetClockInfo()
    {
        var output = RunNvidiaSmi("--query-gpu=clocks.current.graphics,clocks.current.memory,power.draw --format=csv,noheader,nounits");
        var parts = output.Split(',');
        var info = new GpuClockInfo();
        if (parts.Length >= 1) float.TryParse(parts[0].Trim(), out info.CoreClockMHz);
        if (parts.Length >= 2) float.TryParse(parts[1].Trim(), out info.MemoryClockMHz);
        if (parts.Length >= 3) float.TryParse(parts[2].Trim(), out info.PowerDrawW);
        return info;
    }
}

public struct GpuClockInfo
{
    public float CoreClockMHz;
    public float MemoryClockMHz;
    public float PowerDrawW;
}
