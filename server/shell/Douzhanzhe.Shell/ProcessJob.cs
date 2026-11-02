// SPDX-License-Identifier: GPL-3.0-only
//
// ProcessJob — 子进程回收兜底（Job Object + KILL_ON_JOB_CLOSE）
// ===================================================
// 问题：Shell 被强杀（安装升级时 taskkill /f、任务管理器结束、崩溃）时，
//       它拉起的 API 与 WebView2 渲染进程不会随之退出 → 变成孤儿进程。
//       实测一次异常退出可残留 30+ 个 WebView2 渲染进程，长期挂着约 1 GB 内存。
//
// 方案：把自身与全部子进程放进一个 Job，并设置 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE。
//       Windows 保证：持有 Job 句柄的进程一退出（无论正常退出还是被强杀），
//       Job 内的所有进程都会被系统一并结束 —— 不再产生孤儿。
//
// 兼容性：Win8+ 支持嵌套 Job；若本进程已被外层 Job 限制（某些安装器/沙箱环境），
//         AssignProcessToJobObject 可能失败，此时降级为"不做兜底"，不影响主流程。

using System.Runtime.InteropServices;

namespace Douzhanzhe.Shell;

internal static class ProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    // 句柄必须持有到进程结束，否则 Job 会被提前释放
    private static IntPtr _jobHandle = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 建立 KILL_ON_JOB_CLOSE 的 Job 并把当前进程加入。
    /// 返回 true 表示兜底已生效。
    /// </summary>
    public static bool EnableKillOnClose()
    {
        try
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_jobHandle == IntPtr.Zero)
            {
                Form1.AppLog("Shell", $"Job 创建失败 (err={Marshal.GetLastWin32Error()})，子进程兜底未启用");
                return false;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    Form1.AppLog("Shell", $"Job 设置 KILL_ON_JOB_CLOSE 失败 (err={Marshal.GetLastWin32Error()})");
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            if (!AssignProcessToJobObject(_jobHandle, GetCurrentProcess()))
            {
                Form1.AppLog("Shell", $"AssignProcessToJobObject 失败 (err={Marshal.GetLastWin32Error()})，子进程兜底未启用");
                return false;
            }

            Form1.AppLog("Shell", "已启用 KILL_ON_JOB_CLOSE：退出时自动回收 API 与 WebView2 子进程");
            return true;
        }
        catch (Exception ex)
        {
            Form1.AppLog("Shell", $"Job 兜底初始化异常: {ex.Message}");
            return false;
        }
    }
}
