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
// 调用时机：必须在本次 WebView2 启动之前（否则会把自己刚起的进程杀掉）。

using System.Diagnostics;
using System.Management;

namespace Douzhanzhe.Shell;

internal static class OrphanCleanup
{
    private const string UserDataMarker = "Douzhanzhe Console";

    /// <summary>结束遗留的本应用 WebView2 进程，返回结束数量。</summary>
    public static int KillOrphanWebView2()
    {
        int killed = 0;
        try
        {
            var pids = new List<int>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    using (obj)
                    {
                        var cmd = obj["CommandLine"]?.ToString() ?? "";
                        if (cmd.Contains(UserDataMarker, StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(obj["ProcessId"]?.ToString(), out var pid))
                        {
                            pids.Add(pid);
                        }
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
