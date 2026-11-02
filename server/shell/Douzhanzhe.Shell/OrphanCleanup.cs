// SPDX-License-Identifier: GPL-3.0-only
//
// OrphanCleanup — 清理本应用遗留的孤儿 WebView2 渲染进程
// ===================================================
// 背景：Shell 被强杀时（安装升级 taskkill /f、任务管理器结束、崩溃），
//       它拉起的 WebView2 渲染进程不会随之退出 → 变成孤儿，长期堆积占内存。
//
// 判定依据：WebView2 的命令行里带我们专属的 user-data-dir
//       （%LOCALAPPDATA%\Douzhanzhe Console\WebView2），
//       因此只清理"我们自己的"，绝不会误伤其它应用或 Windows 组件的 WebView2。
//
// 安全护栏：只结束"本次启动之前就已经存在"的 PID 集合里的进程。
//   ★ 不用 WMI 的 CreationDate 做判断 —— ManagementObject 取到的 CreationDate
//     是 DMTF 字符串（如 "20261030135715.359014+480"），不是 DateTime，
//     类型判断会静默失效导致护栏被跳过、把我们自己的 WebView2 也杀掉。
//   ★ PID 集合在启动早期用 Process.GetProcessesByName 取（不走 WMI，很快），
//     本次运行新起的 WebView2 根本不在集合里 → 天然安全。

using System.Diagnostics;
using System.Management;

namespace Douzhanzhe.Shell;

internal static class OrphanCleanup
{
    private const string UserDataMarker = "Douzhanzhe Console";

    /// <summary>
    /// 记录当前已存在的 msedgewebview2 进程 PID（须在本次 WebView2 启动之前调用）。
    /// </summary>
    public static HashSet<int> SnapshotExistingWebView2()
    {
        var set = new HashSet<int>();
        try
        {
            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                try { set.Add(p.Id); } catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return set;
    }

    /// <summary>
    /// 结束遗留的本应用 WebView2 进程，返回结束数量。
    /// <paramref name="candidatePids"/> 为启动前记录的 PID 集合（护栏）。
    /// </summary>
    public static int KillOrphanWebView2(HashSet<int> candidatePids)
    {
        int killed = 0;
        try
        {
            var pids = new List<int>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE Name='msedgewebview2.exe' AND CommandLine LIKE '%Douzhanzhe Console%'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        if (!int.TryParse(obj["ProcessId"]?.ToString(), out var pid)) continue;
                        // 护栏：只处理启动前就存在的进程
                        if (!candidatePids.Contains(pid)) continue;
                        pids.Add(pid);
                    }
                }
            }

            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill();
                    killed++;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Form1.AppLog("Shell", $"清理遗留 WebView2 失败: {ex.Message}");
        }
        return killed;
    }
}
