// SPDX-License-Identifier: MIT
//
// GpuController — nvidia-smi 子进程封装
// =========================================
// 职责：
//   在 nvidia-smi CLI 之上提供 GPU 核心/显存频率控制接口。
//
// nvidia-smi 时钟控制语义：
//   --lock-gpu-clocks=min,max  —— 核心频率锁定到 [min, max] 区间
//     - min=0 → 无下限（仅上限限制）
//     - max=3090 → 无上限（仅下限限制）
//     - singleval → min=max=val（锁定到精确频率）
//   --reset-gpu-clocks         —— 恢复核心频率到默认
//   --lock-memory-clocks=min,max —— 显存频率锁定
//   --reset-memory-clocks      —— 恢复显存频率到默认
//
// 注意：min=0 在 nvidia-smi 中表示"不限下限"（GPU 可自由降频到 idle）
//       本机 RTX 5060 GDDR7 最大核心: 3090 MHz, 显存基线: 9001 MHz

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

    /// <summary>核心时钟上限限制：--lock-gpu-clocks=0,max
    /// min=0 表示不设下限，仅限制最大频率</summary>
    public void SetMaxGpuClock(int maxMHz)
    {
        RunNvidiaSmi("--lock-gpu-clocks=0," + maxMHz);
    }

    /// <summary>核心时钟区间锁定：--lock-gpu-clocks=min,max
    /// 同时设下限和上限。min=0 等价于仅上限限制。</summary>
    public void SetLockGpuClocks(int minMHz, int maxMHz)
    {
        RunNvidiaSmi("--lock-gpu-clocks=" + minMHz + "," + maxMHz);
    }

    /// <summary>核心时钟锁定到精确频率：--lock-gpu-clocks=val
    /// min=max=val，GPU 固定在指定频率</summary>
    public void SetExactGpuClock(int mhz)
    {
        RunNvidiaSmi("--lock-gpu-clocks=" + mhz);
    }

    /// <summary>重置 GPU 核心频率为默认：--reset-gpu-clocks</summary>
    public void ResetGpuClocks()
    {
        RunNvidiaSmi("--reset-gpu-clocks");
    }

    /// <summary>显存时钟区间锁定：--lock-memory-clocks=min,max</summary>
    public void SetLockMemoryClocks(int minMHz, int maxMHz)
    {
        RunNvidiaSmi("--lock-memory-clocks=" + minMHz + "," + maxMHz);
    }

    /// <summary>显存时钟上限限制：--lock-memory-clocks=0,max</summary>
    public void SetMaxMemoryClock(int maxMHz)
    {
        RunNvidiaSmi("--lock-memory-clocks=0," + maxMHz);
    }

    /// <summary>重置显存频率为默认：--reset-memory-clocks</summary>
    public void ResetMemoryClocks()
    {
        RunNvidiaSmi("--reset-memory-clocks");
    }

    /// <summary>获取 GPU 时钟/功率遥测</summary>
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
