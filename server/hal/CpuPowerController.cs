// SPDX-License-Identifier: MIT
// CpuPowerController -- Windows 电源计划 CPU 控制封装
// 基于蛟龙控制台逆向分析 (reference-consoles.md §2)
// 底层: powercfg.exe (无需管理员权限，无需驱动)

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Douzhanzhe.HAL;

/// <summary>
/// CPU 性能控制 — 通过 Windows powercfg 电源计划 API
/// 支持: 频率限制 / 关闭睿频 / 核心数百分比 / 功耗策略
/// </summary>
public sealed class CpuPowerController : IDisposable
{
    // ── 常量 GUID ──
    // 电源方案子组: 处理器电源设置 (标准 Windows GUID)
    private const string SUB_PROCESSOR = "54533251-82be-4824-96c1-47b60b740d00";

    // 处理器频率限制 (OEM 扩展，蛟龙使用此 GUID)
    private const string SET_PROC_FREQ_LIMIT = "75b0ae3f-bce0-45a7-8c89-c9611c25e100";

    // Processor performance boost mode (标准 Windows)
    private const string SET_PERF_BOOST = "be337238-0d82-4146-a960-4f3749d470c7";

    // Processor maximum state % (标准 Windows)
    private const string SET_PROC_MAX_STATE = "0cc5b647-c1df-4637-891a-dec35c318583";

    // Processor power throttling max (标准 Windows)
    private const string SET_PROC_THROTTLE_MAX = "8baa4a8a-14c6-4451-8e8b-14bdbd197537";

    // Processor hardware threading (标准 Windows)
    private const string SET_PROC_HW_THREADING = "ea062031-0e34-4ff1-9b6d-eb1059334028";

    // Processor idle demotion (标准 Windows)
    private const string SET_PROC_IDLE_DEMOTION = "36687f9e-e3a5-4dbf-b1dc-15eb381c6863";

    private const int TimeoutMs = 3000;
    private bool _disposed;

    // ── 公共 API ──

    /// <summary>
    /// 设置 CPU 最大频率限制 (MHz)
    /// 设为 0 表示取消限制
    /// </summary>
    public async Task SetFreqLimitAsync(int mhz)
    {
        if (mhz < 0) throw new ArgumentOutOfRangeException(nameof(mhz));
        var scheme = GetActiveScheme();
        await DisableOverlayAsync();
        // 设置 AC + DC
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_FREQ_LIMIT, mhz.ToString());
        await Task.Delay(100);
        // 归零再设（蛟龙同款逻辑，确保 Windows 电源管理刷新）
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_FREQ_LIMIT, "0");
        await Task.Delay(100);
        // 重新激活方案
        await SetActiveSchemeAsync(scheme);
    }

    /// <summary>
    /// 启用/禁用睿频 (Turbo Boost)
    /// </summary>
    public async Task SetTurboAsync(bool enabled)
    {
        var scheme = GetActiveScheme();
        await DisableOverlayAsync();
        // 0=禁用, 2=激进模式
        var val = enabled ? "2" : "0";
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PERF_BOOST, val);
        await Task.Delay(100);
        await SetActiveSchemeAsync(scheme);
    }

    /// <summary>
    /// 设置 CPU 核心数限制 (0-100%)
    /// 设为 100 表示无限制
    /// </summary>
    public async Task SetCoreLimitAsync(int percent)
    {
        if (percent < 0 || percent > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "必须 0-100");
        var scheme = GetActiveScheme();
        await DisableOverlayAsync();
        var val = percent.ToString();
        // 蛟龙同款: 3 个参数同时设置
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_THROTTLE_MAX, val);
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_MAX_STATE, val);
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_HW_THREADING, val);
        await Task.Delay(100);
        await SetActiveSchemeAsync(scheme);
    }

    /// <summary>
    /// 恢复所有 CPU 限制到默认 (无限制)
    /// </summary>
    public async Task ResetAllAsync()
    {
        var scheme = GetActiveScheme();
        await DisableOverlayAsync();
        // 频率限制归零 (取消)
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_FREQ_LIMIT, "0");
        // 睿频启用 (激进模式)
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PERF_BOOST, "2");
        // 核心数 100%
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_THROTTLE_MAX, "100");
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_MAX_STATE, "100");
        await SetPowerValueAsync(scheme, SUB_PROCESSOR, SET_PROC_HW_THREADING, "100");
        await Task.Delay(100);
        await SetActiveSchemeAsync(scheme);
    }

    /// <summary>
    /// 读取当前 CPU 电源设置状态
    /// </summary>
    public CpuPowerStatus GetStatus()
    {
        var status = new CpuPowerStatus();
        try
        {
            // 读取睿频状态
            var turbo = QueryPowerValue(SUB_PROCESSOR, SET_PERF_BOOST);
            status.TurboEnabled = turbo != "0";

            // 读取核心数限制
            var coreStr = QueryPowerValue(SUB_PROCESSOR, SET_PROC_MAX_STATE);
            if (int.TryParse(coreStr, out var core))
                status.CoreLimitPercent = core;

            // 读取频率限制
            var freqStr = QueryPowerValue(SUB_PROCESSOR, SET_PROC_FREQ_LIMIT);
            if (int.TryParse(freqStr, out var freq))
                status.FreqLimitMhz = freq;

            status.Available = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CpuPower] GetStatus error: {ex.Message}");
            status.Available = false;
        }
        return status;
    }

    // ── 内部实现 ──

    private string GetActiveScheme()
    {
        var output = RunPowerCfg("/getactivescheme");
        // 输出格式: "电源方案 GUID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx  (方案名称)"
        var match = Regex.Match(output, @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException("无法获取当前电源方案 GUID");
        return match.Groups[1].Value;
    }

    private async Task DisableOverlayAsync()
    {
        await RunPowerCfgAsync("/setactive SCHEME_CURRENT");
        await RunPowerCfgAsync("/overlaysetactive overlay_scheme_none");
        await Task.Delay(50);
    }

    private async Task SetActiveSchemeAsync(string scheme)
    {
        await RunPowerCfgAsync($"/setactive {scheme}");
    }

    private async Task SetPowerValueAsync(string scheme, string subGroup, string setting, string value)
    {
        await RunPowerCfgAsync($"/setacvalueindex {scheme} {subGroup} {setting} {value}");
        await RunPowerCfgAsync($"/setdcvalueindex {scheme} {subGroup} {setting} {value}");
    }

    private string QueryPowerValue(string subGroup, string setting)
    {
        var output = RunPowerCfg("/query SCHEME_CURRENT " + subGroup + " " + setting);
        // 解析输出，提取 DC 值
        var match = Regex.Match(output, @"当前交流电源设置索引:\s*0x([0-9a-fA-F]+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var val))
            return val.ToString();
        // 回退: 尝试十进制格式
        match = Regex.Match(output, @"当前交流电源设置索引:\s*(\d+)");
        if (match.Success) return match.Groups[1].Value;
        return "0";
    }

    private string RunPowerCfg(string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            }
        };
        p.Start();
        if (!p.WaitForExit(TimeoutMs))
        {
            p.Kill();
            throw new TimeoutException("powercfg timed out: " + args);
        }
        return p.StandardOutput.ReadToEnd().Trim();
    }

    private async Task RunPowerCfgAsync(string args)
    {
        await Task.Run(() => RunPowerCfg(args));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

public struct CpuPowerStatus
{
    public bool Available;
    public bool TurboEnabled;
    public int CoreLimitPercent;  // 0-100
    public int FreqLimitMhz;     // 0 = 无限制
}
