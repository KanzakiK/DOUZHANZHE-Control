// SPDX-License-Identifier: MIT
//
// CpuAffinityManager — CPU 核心数限制
// =====================================
// 通过 Process.ProcessorAffinity 设置进程 CPU 亲和性掩码
// 限制可用核心数。新建进程自动应用限制（WMI 监听）。

using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace Douzhanzhe.HAL;

public static class CpuAffinityManager
{
    private static readonly object _syncRoot = new();
    private static ManagementEventWatcher? _watcher;
    private static ulong _mask;
    private static int _currentCoreLimit = -1;

    /// <summary>
    /// 设置全局核心限制。0 = 不限制（全部核心）。
    /// </summary>
    public static void SetCoreLimit(int coreCount)
    {
        if (coreCount < 0) return;
        var totalCores = (int)GetActiveProcessorCount(ALL_GROUPS);
        if (coreCount == 0 || coreCount >= totalCores)
        {
            Reset();
            return;
        }

        var newMask = (1UL << coreCount) - 1;

        lock (_syncRoot)
        {
            if (coreCount == _currentCoreLimit) return;
            _currentCoreLimit = coreCount;
            _mask = newMask;

            ApplyToAllProcesses();

            if (_watcher == null)
            {
                _watcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT ProcessID FROM Win32_ProcessStartTrace"));
                _watcher.EventArrived += OnProcessStarted;
                _watcher.Start();
            }
        }
    }

    public static void Reset()
    {
        lock (_syncRoot)
        {
            _watcher?.Stop();
            _watcher?.Dispose();
            _watcher = null;
            _currentCoreLimit = -1;
            _mask = 0;
        }
    }

    private static void OnProcessStarted(object? _, EventArrivedEventArgs e)
    {
        if (e.NewEvent?.Properties["ProcessID"]?.Value is int pid)
        {
            try
            {
                using var np = Process.GetProcessById(pid);
                TrySetAffinity(np, _mask);
            }
            catch { }
        }
    }

    private static void ApplyToAllProcesses()
    {
        foreach (var p in Process.GetProcesses())
            TrySetAffinity(p, _mask);
    }

    private static void TrySetAffinity(Process proc, ulong mask)
    {
        try { proc.ProcessorAffinity = (IntPtr)mask; }
        catch { }
    }

    public static int GetActiveCoreCount()
    {
        return (int)GetActiveProcessorCount(ALL_GROUPS);
    }

    private const uint ALL_GROUPS = 0xFFFF;
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(uint groupNumber);
}
