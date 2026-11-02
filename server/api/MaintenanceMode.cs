// SPDX-License-Identifier: MIT
//
// MaintenanceMode — 安装/升级期的静默维护模式
// ===================================================
// 由安装器调用：Douzhanzhe.API.exe --maintenance
//
// 目的：原先安装器里散落着 taskkill / sc / schtasks / cmd 六处外部命令，
//       每个都会弹一个一闪而过的控制台窗口（实测安装时闪 6 个）。
//       这里把清理逻辑全部收进本程序内部直接完成：
//         - 结束旧 Shell / 旧 API 实例（Shell.exe 被占用会导致覆盖安装失败）
//         - 结束本应用遗留的孤儿 WebView2 渲染进程
//         - 停止并删除旧版内核驱动服务（inpoutx64 / WinRing0_1_2_0）
//         - 删除旧版驱动文件
//         - 删除旧版开机自启计划任务
//       子进程一律用 CreateNoWindow 启动，全程无任何窗口闪现。

using System.Diagnostics;
using System.Management;
using Douzhanzhe.HAL;
using Microsoft.Win32.TaskScheduler;

namespace Douzhanzhe.API;

public static class MaintenanceMode
{
    private const string TaskName = "DouzhanzheControl";
    private const string WebView2UserDataMarker = "Douzhanzhe Console";

    private static readonly string[] LegacyServices = { "inpoutx64", "WinRing0_1_2_0" };

    private static readonly string[] LegacyFiles =
    {
        "inpoutx64.dll", "WinRing0x64.dll", "WinRing0x64.sys",
        "WinRing0x64.cat", "ryzenadj.exe"
    };

    /// <summary>命令行含 --maintenance 时执行维护并返回 true（调用方据此直接退出）。</summary>
    public static bool TryRun(string[] args)
    {
        bool maintenance = args.Any(a => a.Equals("--maintenance", StringComparison.OrdinalIgnoreCase));
        bool autostart = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (!maintenance && !autostart) return false;

        try
        {
            if (maintenance) Run();
            if (autostart) CreateAutoStartTask();
        }
        catch (Exception ex) { AppLog.Write("Maintenance", $"执行失败: {ex.GetType().Name}: {ex.Message}"); }
        return true;
    }

    private static void Run()
    {
        var appDir = AppContext.BaseDirectory;
        AppLog.Write("Maintenance", $"开始静默维护（目录 {appDir}）");

        // 1) 结束旧 Shell（不结束自身）
        var (killedShell, residualShell) = KillProcessesByName("Douzhanzhe.Shell", skipSelf: true);
        // 2) 结束其它旧 API 实例（安装器调起的这个就是当前的）
        var (killedApi, residualApi) = KillProcessesByName("Douzhanzhe.API", skipSelf: true);
        // 3) 结束本应用遗留的孤儿 WebView2 渲染进程
        int killedWv2 = KillOrphanWebView2();
        // 4) 停止并删除旧版内核驱动服务
        int removedSvc = StopAndDeleteLegacyServices();
        // 5) 删除旧版驱动文件
        int removedFiles = DeleteLegacyFiles(appDir);
        // 6) 删除旧版开机自启计划任务
        bool taskDeleted = DeleteScheduledTask();

        // 总结行：一句话看清"到底清干净没有"
        AppLog.Write("Maintenance",
            $"静默维护完成：进程结束 {killedShell + killedApi} 个" +
            $"（残留无法终止 {residualShell + residualApi} 个，需重启系统释放）" +
            $" | 遗留 WebView2 结束 {killedWv2} 个" +
            $" | 旧驱动服务清理 {removedSvc} 个" +
            $" | 旧驱动文件删除 {removedFiles} 个" +
            $" | 旧计划任务 {(taskDeleted ? "已删除" : "无")}");
    }

    /// <summary>按进程名结束进程（跳过自身）。返回 (成功数, 无法终止的残留数)。</summary>
    private static (int killed, int residual) KillProcessesByName(string name, bool skipSelf)
    {
        int killed = 0, residual = 0;
        try
        {
            var self = Process.GetCurrentProcess().Id;
            var targets = Process.GetProcessesByName(name);
            foreach (var p in targets)
            {
                try
                {
                    if (skipSelf && p.Id == self) continue;
                    // 0 线程 = 进程已死透、只剩句柄未释放（被外部提权进程持有）。
                    // 这种对象 Kill()/TerminateProcess 都无效（没有线程可终止），
                    // 只能等持句柄者释放它（通常是重启系统）。
                    if (p.Threads.Count == 0)
                    {
                        residual++;
                        AppLog.Write("Maintenance",
                            $"{name} (PID {p.Id}) 是 0 线程的残留进程对象（进程早已终止，仅剩句柄被外部提权进程持有）。" +
                            "TerminateProcess 对它无效（无线程可终止），只能等持句柄者释放（通常重启系统后消失）。" +
                            "该对象不占端口/互斥锁/内存，不影响使用。");
                        continue;
                    }
                    p.Kill();
                    killed++;
                    AppLog.Write("Maintenance", $"已结束进程 {name} (PID {p.Id})");
                }
                catch (Exception ex)
                {
                    // ★ 失败必须记录：此前全部静默吞掉，导致"没清干净"完全不可见
                    residual++;
                    AppLog.Write("Maintenance", $"结束 {name} (PID {p.Id}) 失败: {ex.GetType().Name}: {ex.Message}");
                }
                finally { try { p.Dispose(); } catch { } }
            }
            if (killed > 0) WaitForExit(name, 3000);
        }
        catch (Exception ex)
        {
            AppLog.Write("Maintenance", $"结束 {name} 失败: {ex.Message}");
        }
        return (killed, residual);
    }

    private static void WaitForExit(string name, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { if (Process.GetProcessesByName(name).Length == 0) return; }
            catch { return; }
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// 结束本应用遗留的孤儿 WebView2 渲染进程，返回结束数量。
    /// 判定依据：命令行里含我们专属的 user-data-dir 路径（…\Douzhanzhe Console\WebView2），
    /// 因此绝不会误伤其它应用或 Windows 自身组件的 WebView2。
    /// </summary>
    private static int KillOrphanWebView2()
    {
        int killed = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId FROM Win32_Process WHERE Name='msedgewebview2.exe' AND CommandLine LIKE '%Douzhanzhe Console%'");
            var pids = new List<int>();
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    if (int.TryParse(obj["ProcessId"]?.ToString(), out var pid)) pids.Add(pid);
                }
            }
            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill();
                    killed++;
                    AppLog.Write("Maintenance", $"已结束遗留 WebView2 (PID {pid})");
                }
                catch (Exception ex)
                {
                    // ★ 失败必须记录
                    AppLog.Write("Maintenance", $"结束遗留 WebView2 (PID {pid}) 失败: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (pids.Count > 0) Thread.Sleep(500);
        }
        catch (Exception ex)
        {
            AppLog.Write("Maintenance", $"清理遗留 WebView2 失败: {ex.Message}");
        }
        return killed;
    }

    /// <summary>停止并删除旧版内核驱动服务（用 CreateNoWindow 调用 sc.exe，全程无窗口）。返回实际删除的数量。</summary>
    private static int StopAndDeleteLegacyServices()
    {
        int removed = 0;
        foreach (var svc in LegacyServices)
        {
            var stopCode = RunSilently("sc.exe", $"stop \"{svc}\"");
            var delCode = RunSilently("sc.exe", $"delete \"{svc}\"");
            if (delCode == 0) { removed++; AppLog.Write("Maintenance", $"已删除旧驱动服务 {svc}"); }
            else
            {
                // sc 返回非 0 通常表示"服务不存在"（本来就干净），记录下来便于区分
                AppLog.Write("Maintenance",
                    $"旧驱动服务 {svc}：stop 返回 {stopCode}，delete 返回 {delCode}" +
                    (delCode == 1060 ? "（1060 = 服务不存在，属正常）" : ""));
            }
        }
        return removed;
    }

    private static int DeleteLegacyFiles(string appDir)
    {
        int removed = 0;
        foreach (var f in LegacyFiles)
        {
            try
            {
                var path = Path.Combine(appDir, f);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                    AppLog.Write("Maintenance", $"已删除旧版文件 {f}");
                }
            }
            catch (Exception ex)
            {
                // ★ 失败必须记录
                AppLog.Write("Maintenance", $"删除旧版文件 {f} 失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return removed;
    }

    private static bool DeleteScheduledTask()
    {
        try
        {
            using var service = new TaskService();
            var task = service.GetTask(TaskName);
            if (task == null) return false;
            service.RootFolder.DeleteTask(TaskName, false);
            AppLog.Write("Maintenance", $"已删除旧版计划任务 {TaskName}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("Maintenance", $"删除计划任务失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 创建/更新开机自启计划任务（等价于原先安装器用 schtasks /XML 做的事，
    /// 但改走 Task Scheduler API，不再弹控制台窗口）。
    /// </summary>
    private static void CreateAutoStartTask()
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var shellExe = Path.Combine(appDir, "Douzhanzhe.Shell.exe");
            if (!File.Exists(shellExe)) return;

            using var service = new TaskService();
            var existing = service.RootFolder.AllTasks.FirstOrDefault(t => t.Name == TaskName);
            if (existing != null) service.RootFolder.DeleteTask(TaskName, false);

            var td = service.NewTask();
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;   // PT0S：避免 72h 硬终止 bug
            td.Settings.AllowHardTerminate = true;
            td.Settings.StartWhenAvailable = true;

            td.Triggers.Add(new LogonTrigger());
            td.Actions.Add(new ExecAction(shellExe, "--minimized", appDir));

            service.RootFolder.RegisterTaskDefinition(TaskName, td);
            AppLog.Write("Maintenance", $"已创建开机自启任务 {TaskName}（{shellExe} --minimized）");
        }
        catch (Exception ex)
        {
            AppLog.Write("Maintenance", $"创建开机自启任务失败: {ex.Message}");
        }
    }

    /// <summary>以无窗口方式执行一条命令（CREATE_NO_WINDOW，不闪控制台）。返回退出码。</summary>
    private static int RunSilently(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            AppLog.Write("Maintenance", $"执行 {file} {args} 失败: {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }
}
