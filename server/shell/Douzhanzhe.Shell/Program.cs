using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace Douzhanzhe.Shell;

static class Program
{
    private const string MutexName = @"Global\DouzhanzheShell_SingleInstance";
    private const string ElevateTaskName = "DouzhanzheConsole_Elevate";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_RESTORE = 0xF120;

    [STAThread]
    static void Main(string[] args)
    {
        // --monitor-off: 独立进程关闭显示器（无 UI、无 WebView2、无消息泵）
        // Shell 快捷键通过启动自身带此参数来关屏，避免在 Shell UI 线程调用 SendMessage
        if (args.Any(a => a == "--monitor-off"))
        {
            SendMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));
            return;
        }

        ApplicationConfiguration.Initialize();

        // ── 1. 单实例检测 ──
        if (Mutex.TryOpenExisting(MutexName, out var existingMutex))
        {
            // 已有实例运行，激活它的窗口
            ActivateExistingWindow();
            existingMutex.Dispose();
            return;
        }

        // ── 2. 管理员权限检测与自动提权 ──
        bool isElevated = args.Any(a => a == "--elevated");
        if (!isElevated)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                if (TryElevate(args))
                    return; // 提权成功，当前进程退出
                // 提权全部失败，以当前权限继续运行
            }
        }

        // ── 3. 全局异常处理 ──
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            LogCrash("UI Thread", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogCrash("AppDomain", ex);
        };

        // ── 4. 创建全局单实例互斥锁 ──
        using var mutex = new Mutex(true, MutexName);

        Application.Run(new Form1());
    }

    /// <summary>
    /// 激活已有实例的主窗口（处理最小化到托盘的情况）
    /// 使用 AttachThreadInput 绕过 Windows 前台窗口限制
    /// </summary>
    private static void ActivateExistingWindow()
    {
        var hWnd = FindWindow(null, "斗战者控制台");
        if (hWnd == IntPtr.Zero)
        {
            MessageBox.Show("斗战者控制台已在后台运行。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 获取当前前台窗口和目标窗口的线程 ID
        var foreWnd = GetForegroundWindow();
        GetWindowThreadProcessId(hWnd, out uint targetPid);
        uint foreThread = GetWindowThreadProcessId(foreWnd, out _);
        uint curThread = (uint)Environment.CurrentManagedThreadId;

        // 将当前线程附加到前台线程的输入队列，绕过 SetForegroundWindow 限制
        if (curThread != foreThread)
            AttachThreadInput(curThread, foreThread, true);

        try
        {
            // 如果窗口不可见（隐藏到托盘），先显示
            if (!IsWindowVisible(hWnd))
                ShowWindow(hWnd, SW_SHOW);

            // 用 WM_SYSCOMMAND SC_RESTORE 恢复窗口（比 ShowWindow 更可靠）
            SendMessage(hWnd, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
            ShowWindow(hWnd, SW_RESTORE);

            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (curThread != foreThread)
                AttachThreadInput(curThread, foreThread, false);
        }
    }

    /// <summary>
    /// 尝试以管理员权限重新启动自身。
    /// 首选 ShellExecute("runas")，失败则回退到计划任务方式。
    /// </summary>
    private static bool TryElevate(string[] args)
    {
        var exePath = Application.ExecutablePath;
        var baseArgs = string.Join(" ", args.Select(a => $"\"{a}\""));
        var elevatedArgs = string.IsNullOrWhiteSpace(baseArgs)
            ? "--elevated" : baseArgs + " --elevated";

        // 方式 1: ShellExecute("runas") — UAC 开启时正常工作
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                Verb = "runas",
                UseShellExecute = true,
                Arguments = elevatedArgs
            };
            Process.Start(psi);
            return true;
        }
        catch { }

        // 方式 2: 计划任务提权 — UAC 关闭时的后备方案
        try
        {
            using var ts = new TaskService();
            var td = ts.NewTask();
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.Actions.Add(new ExecAction(exePath, elevatedArgs, Path.GetDirectoryName(exePath)));
            td.Settings.AllowHardTerminate = true;
            td.Settings.StartWhenAvailable = true;

            // 清理可能存在的同名任务
            try { ts.RootFolder.DeleteTask(ElevateTaskName, false); } catch { }

            var task = ts.RootFolder.RegisterTaskDefinition(ElevateTaskName, td);
            task.Run();

            // 任务已排队，给调度器一点时间启动
            Thread.Sleep(1000);

            try { ts.RootFolder.DeleteTask(ElevateTaskName, false); } catch { }
            return true;
        }
        catch
        {
            LogCrash("Elevation", new Exception("Both ShellExecute and TaskScheduler elevation failed"));
            return false;
        }
    }

    /// <summary>
    /// 写崩溃日志到应用目录下的 crash.log（不可写时回退到 %LOCALAPPDATA%）
    /// </summary>
    internal static void LogCrash(string source, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
            try
            {
                File.AppendAllText(logPath, ""); // 测试是否可写
            }
            catch
            {
                // Program Files 可能无写权限，回退到用户目录
                var fallbackDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Douzhanzhe Console");
                Directory.CreateDirectory(fallbackDir);
                logPath = Path.Combine(fallbackDir, "crash.log");
            }
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n" +
                        $"  Type: {ex.GetType().FullName}\n" +
                        $"  Message: {ex.Message}\n" +
                        $"  StackTrace:\n{ex.StackTrace}\n\n";
            File.AppendAllText(logPath, entry);
        }
        catch { }
    }
}
