using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Windows.UI.Notifications;

namespace Douzhanzhe.Shell;

public partial class Form1 : Form
{
    private WebView2 _webView;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private bool _closeToTray = true;
    private bool _isStartupMinimized = false;

    // ---- 全局热键（数据驱动架构） ----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_SYSCOMMAND = 0x0112;

    // ---- DWMWA 标题栏样式 ----
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_MICA_TABBED = 39;
    private const int DWM_WINDOW_CORNER_ROUND = 2; // DWMWCP_ROUND

    // 默认快捷键定义（数据驱动，新增功能只需加一行）
    private static readonly (string id, string label, string modifiers, string key, string action)[] DefaultHotkeys =
    [
        ("monitor-off", "关闭屏幕", "ctrl,shift", "Q",   "monitor-off"),
        ("mode-office", "均衡模式", "ctrl,shift", "1",   "mode:office"),
        ("mode-beast",  "野兽模式", "ctrl,shift", "2",   "mode:beast"),
        ("mode-silent", "安静模式", "ctrl,shift", "3",   "mode:silent"),
        ("mode-gaming", "斗战模式", "ctrl,shift", "4",   "mode:gaming"),
    ];

    // config id → 实际动作（mode:xxx 或 monitor-off），注册与 WndProc 共用
    private static readonly Dictionary<string, string> DefaultHotkeyActions =
        DefaultHotkeys.ToDictionary(d => d.id, d => d.action, StringComparer.OrdinalIgnoreCase);

    // event.code → Win32 VK 映射（前端录制不再受 Shift 输出字符影响）
    private static readonly Dictionary<string, uint> EventCodeVkMap = BuildEventCodeVkMap();

    private static Dictionary<string, uint> BuildEventCodeVkMap()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = (uint)Keys.Space,
            ["Enter"] = (uint)Keys.Enter,
            ["NumpadEnter"] = (uint)Keys.Enter,
            ["Tab"] = (uint)Keys.Tab,
            ["Backspace"] = (uint)Keys.Back,
            ["Delete"] = (uint)Keys.Delete,
            ["Insert"] = (uint)Keys.Insert,
            ["Home"] = (uint)Keys.Home,
            ["End"] = (uint)Keys.End,
            ["PageUp"] = (uint)Keys.PageUp,
            ["PageDown"] = (uint)Keys.PageDown,
            ["CapsLock"] = (uint)Keys.CapsLock,
            ["ArrowUp"] = (uint)Keys.Up,
            ["ArrowDown"] = (uint)Keys.Down,
            ["ArrowLeft"] = (uint)Keys.Left,
            ["ArrowRight"] = (uint)Keys.Right,
            ["Backquote"] = (uint)Keys.Oemtilde,
            ["Minus"] = (uint)Keys.OemMinus,
            ["Equal"] = (uint)Keys.Oemplus,
            ["BracketLeft"] = (uint)Keys.OemOpenBrackets,
            ["BracketRight"] = (uint)Keys.OemCloseBrackets,
            ["Backslash"] = (uint)Keys.OemPipe,
            ["Semicolon"] = (uint)Keys.OemSemicolon,
            ["Quote"] = (uint)Keys.OemQuotes,
            ["Comma"] = (uint)Keys.Oemcomma,
            ["Period"] = (uint)Keys.OemPeriod,
            ["Slash"] = (uint)Keys.OemQuestion,
            ["IntlBackslash"] = (uint)Keys.Oem102,
            ["NumpadMultiply"] = (uint)Keys.Multiply,
            ["NumpadAdd"] = (uint)Keys.Add,
            ["NumpadSubtract"] = (uint)Keys.Subtract,
            ["NumpadDecimal"] = (uint)Keys.Decimal,
            ["NumpadDivide"] = (uint)Keys.Divide,
        };
        for (char c = 'A'; c <= 'Z'; c++) map["Key" + c] = (uint)c;
        for (char c = '0'; c <= '9'; c++) map["Digit" + c] = (uint)c;
        for (int i = 0; i <= 9; i++) map["Numpad" + i] = (uint)(Keys.NumPad0 + i);
        for (int i = 1; i <= 24; i++) map["F" + i] = (uint)(Keys.F1 + i - 1);
        return map;
    }

    // 运行时热键映射: winHotkeyId → configId
    private readonly Dictionary<int, string> _hotkeyIdToAction = new();
    private readonly HashSet<int> _registeredWinIds = new();

    private FileSystemWatcher? _hotkeyWatcher;
    private System.Windows.Forms.Timer? _hotkeyPollTimer;
    private DateTime _lastHotkeyConfigWrite = DateTime.MinValue;
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _notifyWatcher;
    private System.Threading.Timer? _notifyDebounceTimer;
    private string? _lastNotifyKey;
    private DateTime _lastNotifyWriteUtc = DateTime.MinValue;

    // ---- 后端进程守护 ----
    private System.Windows.Forms.Timer? _healthTimer;
    private bool _backendWasDown = false;
    private int _healthFailCount = 0;

    private static readonly string _winStatePath = Path.Combine(AppContext.BaseDirectory, "config", "window-state.json");

    private static Icon LoadAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwmAttributes();
        StartConfigWatcher();
        StartNotifyWatcher();
    }

    public Form1()
    {
        // 尽早检测 --minimized 参数，在窗口显示之前设置隐藏状态
        var startupArgs = Environment.GetCommandLineArgs();
        _isStartupMinimized = startupArgs.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        Text = "斗战者控制台";
        Width = 1500;
        Height = 1200;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 17, 23); // 深色背景防白闪
        Icon = LoadAppIcon();

        // 开机自启最小化：在 RestoreWindowState 之前设置，防止恢复最大化状态覆盖
        if (_isStartupMinimized)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
        }

        // 恢复上次关闭时的窗口尺寸和位置
        RestoreWindowState();

        FormClosing += Form1_FormClosing;
        Resize += Form1_Resize;

        // 托盘图标
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("显示主窗口", null, (s, e) => ShowWindow());
        _trayMenu.Items.Add("退出", null, (s, e) => ExitApp());

        _trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "斗战者控制台",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (s, e) => ShowWindow();

        // WebView2 — 先不设 Source，等 API 就绪后再导航
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(13, 17, 23)
        };
        _webView.CoreWebView2InitializationCompleted += (s, e) =>
        {
            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // WebView2 渲染进程崩溃时自动重新加载
                _webView.CoreWebView2.ProcessFailed += (sender, args) =>
                {
                    try { _webView.Reload(); } catch { }
                };
            }
        };
        Controls.Add(_webView);

        Load += Form1_Load;
    }

    private async void Form1_Load(object? sender, EventArgs e)
    {
        // 开机自启最小化：立即隐藏窗口（构造函数已预设状态）
        if (_isStartupMinimized)
        {
            WindowState = FormWindowState.Minimized;
            Hide();
        }

        // 上报权限状态，API 端 /api/platform/info 优先读取该文件
        ReportElevationStatus();

        // 启动后端 API（如果尚未运行）
        StartApiIfNotRunning();

        // 初始化 WebView2 — 用户数据目录放在 %LOCALAPPDATA% 下，避免 Program Files 写入权限问题
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Douzhanzhe Console", "WebView2");

        // 启动时仅清除 HTTP/GPU/ServiceWorker 缓存（防止前端更新后缓存旧版本）
        // 保留 Local Storage / IndexedDB — 页面路由与 UI 状态缓存（配置以后端为唯一权威源）
        // index.html 已由后端设置 Cache-Control: no-cache，新 bundle 不会被 HTTP 缓存
        string[] cacheDirs = { "EBWebView\\Default\\Cache", "EBWebView\\Default\\Code Cache", "EBWebView\\Default\\GPUCache",
                               "EBWebView\\Default\\Service Worker", "EBWebView\\GrShaderCache", "EBWebView\\ShaderCache" };
        foreach (var sub in cacheDirs)
        {
            try
            {
                var path = Path.Combine(userDataDir, sub);
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { /* 单个缓存目录清除失败不影响启动 */ }
        }

        bool webViewOk = false;
        string webViewError = "";
        try
        {
            var envTask = CoreWebView2Environment.CreateAsync(null, userDataDir);
            // 15 秒超时，防止初始化卡死
            if (await System.Threading.Tasks.Task.WhenAny(envTask, System.Threading.Tasks.Task.Delay(15000)) == envTask)
            {
                var env = await envTask;
                var initTask = _webView.EnsureCoreWebView2Async(env);
                if (await System.Threading.Tasks.Task.WhenAny(initTask, System.Threading.Tasks.Task.Delay(15000)) == initTask)
                {
                    await initTask; // 传播可能的异常
                    webViewOk = true;
                }
                else
                {
                    webViewError = "WebView2 EnsureCoreWebView2Async 超时 (15s)";
                }
            }
            else
            {
                webViewError = "WebView2 CreateAsync 超时 (15s)";
            }
        }
        catch (Exception ex)
        {
            webViewError = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (!webViewOk)
        {
            AppLog("Shell", $"WebView2 init failed: {webViewError}");

            // 用 WinForms Label 显示错误
            _webView.Dispose();
            var lbl = new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(13, 17, 23),
                ForeColor = Color.FromArgb(201, 209, 217),
                Font = new Font("Microsoft YaHei UI", 14f),
                Padding = new Padding(40),
                AutoSize = false,
                Text = "界面引擎初始化失败\n\n" +
                       "请确认已安装 Microsoft Edge WebView2 Runtime：\n" +
                       "https://developer.microsoft.com/zh-cn/microsoft-edge/webview2/\n\n" +
                       $"错误详情：{webViewError}"
            };
            Controls.Add(lbl);
            if (_isStartupMinimized) { WindowState = FormWindowState.Minimized; Hide(); }
            return;
        }

        // 等待后端 API 就绪（最多 30 秒）
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        bool apiReady = false;
        for (int i = 0; i < 30; i++)
        {
            try
            {
                var resp = await http.GetAsync("http://127.0.0.1:3100/");
                if (resp.IsSuccessStatusCode)
                {
                    apiReady = true;
                    break;
                }
            }
            catch { }
            await Task.Delay(1000);
        }

        if (!apiReady)
        {
            // 读取 API 启动日志
            var logContent = "";
            try
            {
                var logPath = _appLogPath;
                if (File.Exists(logPath))
                {
                    // 只读最后 50 行，避免页面太长
                    var lines = File.ReadAllLines(logPath);
                    var tail = lines.Skip(Math.Max(0, lines.Length - 50));
                    logContent = System.Net.WebUtility.HtmlEncode(string.Join("\n", tail));
                }
            }
            catch { }

            // API 未响应 — 显示错误页面
            var errorHtml = $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Error</title>
<style>body{{background:#0d1117;color:#c9d1d9;font:16px/1.6 system-ui;padding:40px;max-width:700px;margin:0 auto}}
h1{{color:#f85149;font-size:20px}}p{{color:#8b949e}}code{{background:#161b22;padding:2px 8px;border-radius:4px}}
a{{color:#58a6ff}}pre{{background:#161b22;border:1px solid #30363d;border-radius:8px;padding:16px;overflow:auto;color:#f0883e;font-size:13px;margin-top:16px}}</style></head><body>
<h1>后端服务未响应</h1>
<p>斗战者控制台后端 API 在 30 秒内未能启动。请检查：</p>
<p>1. 安装目录下的 <code>Douzhanzhe.API.exe</code> 是否存在<br>
2. 端口 3100 是否被其他程序占用<br>
3. 是否已安装 <a href='https://dotnet.microsoft.com/download/dotnet/8.0'>.NET 8 Desktop Runtime</a></p>
{(string.IsNullOrEmpty(logContent) ? "" : $"<p style='color:#c9d1d9;margin-top:24px'>启动日志（请截图反馈）：</p><pre>{logContent}</pre>")}
</body></html>";
            _webView.NavigateToString(errorHtml);
            if (_isStartupMinimized) { WindowState = FormWindowState.Minimized; Hide(); }
            return;
        }

        _webView.Source = new Uri("http://127.0.0.1:3100/");

        // 启动后端健康守护：每 8 秒检查一次，连续 2 次失败则重启后端
        _healthTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        _healthTimer.Tick += HealthTimer_Tick;
        _healthTimer.Start();

        // 异步初始化（WebView2、API 轮询）期间 WinForms 可能隐式重新显示了窗口
        // 在所有初始化完成后再次确保窗口隐藏到托盘
        if (_isStartupMinimized)
        {
            WindowState = FormWindowState.Minimized;
            Hide();
        }

        // ---- 全局热键初始化 ----
        RegisterHotkeysFromConfig();
        StartHotkeyWatcher();
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(3000, "斗战者控制台", "程序仍在后台运行，双击托盘图标恢复窗口。", ToolTipIcon.Info);
        }
        else
        {
            SaveWindowState();
        }
    }

    private void Form1_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            if (!_isStartupMinimized)
                _trayIcon.ShowBalloonTip(2000, "斗战者控制台", "已最小化到系统托盘。", ToolTipIcon.Info);
        }
    }

    private void ShowWindow()
    {
        _isStartupMinimized = false;  // 清除开机标志，后续手动最小化正常显示气球通知
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void ExitApp()
    {
        _closeToTray = false;
        _healthTimer?.Stop();
        _healthTimer?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        // 先通知后端优雅关闭（停止内核驱动 + 释放资源）
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.PostAsync("http://127.0.0.1:3100/api/shutdown", null).Wait(5000);
        }
        catch { }

        // 杀掉后端 API 进程（:3100），避免孤儿进程
        KillProcessOnPort(3100);

        // 旧版内核驱动 (inpoutx64/WinRing0) 已由 PawnIO 替代，安装包自动清理
        Application.Exit();
    }

    private static void StopDriverService(string svcName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"stop {svcName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }
    }

    // ---- 统一日志: 写入 logs/app.log，与后端 AppLog 同文件 ----
    private static readonly string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Douzhanzhe Console", "logs");
    private static readonly string _appLogPath = Path.Combine(_logDir, "app.log");

    internal static void AppLog(string tag, string msg)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            File.AppendAllText(_appLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {msg}\n");
        }
        catch { }
    }

    private void ShellLog(string msg) => AppLog("Shell", msg);

    private async void HealthTimer_Tick(object? sender, EventArgs e)
    {
        // 防止重入：上一次检查还没完成时跳过
        _healthTimer?.Stop();

        bool alive = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync("http://127.0.0.1:3100/api/health");
            alive = resp.IsSuccessStatusCode;
        }
        catch { }

        if (alive)
        {
            _healthFailCount = 0;
            // 后端从宕机恢复后，刷新 WebView2
            if (_backendWasDown)
            {
                _backendWasDown = false;
                ShellLog("后端已恢复，刷新 WebView2");
                try { _webView.Reload(); } catch { }
            }
        }
        else
        {
            _healthFailCount++;
            if (_healthFailCount == 1)
                ShellLog("健康检查失败 (1/2)，等待下次确认");

            // 连续 2 次失败（约 16 秒）才触发重启，避免网络抖动误判
            if (_healthFailCount >= 2)
            {
                ShellLog("健康检查连续失败 2 次，重启后端");
                _backendWasDown = true;
                _healthFailCount = 0;
                StartApiIfNotRunning();

                // 等待后端恢复（最多 20 秒）
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                bool recovered = false;
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        var resp = await http.GetAsync("http://127.0.0.1:3100/");
                        if (resp.IsSuccessStatusCode)
                        {
                            ShellLog($"后端重启成功 ({i + 1}s)，刷新 WebView2");
                            try { _webView.Reload(); } catch { }
                            _backendWasDown = false;
                            recovered = true;
                            break;
                        }
                    }
                    catch { }
                    await Task.Delay(1000);
                }
                if (!recovered)
                {
                    ShellLog("后端重启超时 (20s)，WebView2 保持当前状态");
                }
            }
        }

        _healthTimer?.Start();
    }

    private void KillProcessOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains($":{port}") && line.Contains("LISTENING"))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        try { Process.GetProcessById(pid).Kill(); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private bool IsPortListening(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains($":{port}") && line.Contains("LISTENING"))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private string SharedConfigDir()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "config");
        // 开发环境：向上找到仓库根目录（存在 AGENTS.md + server/config）后使用共享 config
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "server", "config");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(d.FullName, "AGENTS.md")))
                return Path.GetFullPath(candidate);
        }
        Directory.CreateDirectory(local);
        return local;
    }

    private void ReportElevationStatus()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            var path = Path.Combine(SharedConfigDir(), "permission.json");
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(new
            {
                isElevated,
                source = "shell",
                pid = Environment.ProcessId,
                reportedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            }));
            File.Move(tmpPath, path, overwrite: true);
            ShellLog($"[Permission] isElevated={isElevated}, reported to {path}");
        }
        catch
        {
            // 权限上报失败不影响启动
        }
    }

    private void StartApiIfNotRunning()
    {
        if (IsPortListening(3100)) return;

        var baseDir = AppContext.BaseDirectory;
        var apiExe = Path.Combine(baseDir, "Douzhanzhe.API.exe");

        AppLog("API-Startup", $"begin, BaseDir={baseDir}");

        if (!File.Exists(apiExe))
        {
            AppLog("API-Startup", $"ERROR: Douzhanzhe.API.exe not found at {apiExe}");
            // 列出目录内容帮助排查
            try {
                var files = Directory.GetFiles(baseDir, "*.exe");
                AppLog("API-Startup", $"EXE files in {baseDir}: {string.Join(", ", files.Select(Path.GetFileName))}");
            } catch { }
            return;
        }

        AppLog("API-Startup", $"Starting: {apiExe}");
        try
        {
            var psi = new ProcessStartInfo(apiExe)
            {
                WorkingDirectory = baseDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "--urls=http://127.0.0.1:3100",
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var proc = Process.Start(psi);
            if (proc == null)
            {
                AppLog("API-Startup", "ERROR: Process.Start returned null");
                return;
            }
            AppLog("API-Startup", $"PID: {proc.Id}");

            // 等 2 秒检查进程是否立即崩溃
            Thread.Sleep(2000);
            if (proc.HasExited)
            {
                AppLog("API-Startup", $"ERROR: Process exited immediately with code {proc.ExitCode}");
                try {
                    var stderr = proc.StandardError.ReadToEnd();
                    if (!string.IsNullOrEmpty(stderr))
                        AppLog("API-Startup", $"STDERR: {stderr[..Math.Min(stderr.Length, 2000)]}");
                    var stdout = proc.StandardOutput.ReadToEnd();
                    if (!string.IsNullOrEmpty(stdout))
                        AppLog("API-Startup", $"STDOUT: {stdout[..Math.Min(stdout.Length, 2000)]}");
                } catch { }
            }
            else
            {
                AppLog("API-Startup", "Process running, waiting for port...");
            }
        }
        catch (Exception ex)
        {
            AppLog("API-Startup", $"ERROR: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 恢复上次关闭时的窗口尺寸、位置和最大化状态
    /// </summary>
    private void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(_winStatePath)) return;
            var json = File.ReadAllText(_winStatePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int w = root.TryGetProperty("width", out var wv) ? wv.GetInt32() : 0;
            int h = root.TryGetProperty("height", out var hv) ? hv.GetInt32() : 0;
            int x = root.TryGetProperty("x", out var xv) ? xv.GetInt32() : int.MinValue;
            int y = root.TryGetProperty("y", out var yv) ? yv.GetInt32() : int.MinValue;
            bool max = root.TryGetProperty("maximized", out var mv) && mv.GetBoolean();

            if (w > 100 && h > 100)
            {
                Width = w;
                Height = h;
            }

            if (x != int.MinValue && y != int.MinValue)
            {
                // 验证保存的位置仍在某个屏幕可见范围内
                var pt = new Point(x, y);
                bool onScreen = false;
                foreach (var scr in Screen.AllScreens)
                {
                    var r = scr.WorkingArea;
                    if (r.Contains(pt) || r.IntersectsWith(new Rectangle(x, y, Math.Max(w, 200), Math.Max(h, 200))))
                    {
                        onScreen = true;
                        break;
                    }
                }
                if (onScreen)
                {
                    StartPosition = FormStartPosition.Manual;
                    Location = new Point(x, y);
                }
            }

            if (max && !_isStartupMinimized)
                WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    /// <summary>
    /// 保存当前窗口尺寸、位置和最大化状态到配置文件
    /// </summary>
    private void SaveWindowState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_winStatePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // 如果窗口最大化，保存 RestoreBounds（恢复前的尺寸）
            var bounds = WindowState == FormWindowState.Maximized ? RestoreBounds : new Rectangle(Location, Size);
            var data = new
            {
                width = bounds.Width,
                height = bounds.Height,
                x = bounds.X,
                y = bounds.Y,
                maximized = WindowState == FormWindowState.Maximized
            };
            File.WriteAllText(_winStatePath, JsonSerializer.Serialize(data));
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int winId = m.WParam.ToInt32();
            if (_hotkeyIdToAction.TryGetValue(winId, out var action))
            {
                ShellLog($"[Hotkey] WndProc WM_HOTKEY: winId={winId}, action={action}");
                if (action == "monitor-off")
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            Arguments = "--monitor-off",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    catch { }
                }
                else if (action.StartsWith("mode:"))
                {
                    var mode = action[5..];
                    Task.Run(async () =>
                    {
                        var ok = await PostApiAsync("/api/overrides/switch", $"{{\"mode\":\"{mode}\"}}");
                        if (!ok) ShellLog($"[Hotkey] 模式切换请求失败: {mode}");
                    });
                }
            }
        }
        const int WM_SETTINGCHANGE = 0x001A;
        if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            string area = Marshal.PtrToStringAuto(m.LParam);
            if (area == "ImmersiveColorSet")
                BeginInvoke(ApplyDwmAttributes);
        }
        base.WndProc(ref m);
    }

    // 依次尝试 3100（安装版）/ 3101（开发版），任一 API 响应成功即返回
    private static async Task<bool> PostApiAsync(string path, string json)
    {
        foreach (var baseUrl in new[] { "http://127.0.0.1:3100", "http://127.0.0.1:3101" })
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync(baseUrl + path, content);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* try next */ }
        }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 清理所有已注册热键
            foreach (var winId in _registeredWinIds)
                UnregisterHotKey(Handle, winId);
            _registeredWinIds.Clear();
            _hotkeyIdToAction.Clear();
            _healthTimer?.Stop();
            _healthTimer?.Dispose();
            _hotkeyPollTimer?.Dispose();
            _hotkeyWatcher?.Dispose();
            _notifyDebounceTimer?.Dispose();
            _notifyWatcher?.Dispose();
            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            _webView?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- 热键管理 ----

    /// <summary>
    /// 解析 config 目录，与 API 端 Program.cs 使用相同逻辑：
    /// 优先 BaseDirectory/config/，若不存在则回退到项目根目录/config/
    /// </summary>
    private void StartConfigWatcher()
    {
        try
        {
            var sharedDir = SharedConfigDir();
            if (!Directory.Exists(sharedDir)) Directory.CreateDirectory(sharedDir);
            _configWatcher = new FileSystemWatcher(sharedDir, "ui-state.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            void OnConfigChanged()
            {
                ShellLog("[DWM] Config file changed, calling ApplyDwmAttributes");
                try { _configWatcher.EnableRaisingEvents = false; BeginInvoke(ApplyDwmAttributes); }
                finally { _configWatcher.EnableRaisingEvents = true; }
            }
            _configWatcher.Changed += (s, e) => OnConfigChanged();
            _configWatcher.Created += (s, e) => OnConfigChanged();
            _configWatcher.Renamed += (s, e) => OnConfigChanged();
        }
        catch { }
    }

    private void StartNotifyWatcher()
    {
        try
        {
            var sharedDir = SharedConfigDir();
            if (!Directory.Exists(sharedDir)) Directory.CreateDirectory(sharedDir);
            _notifyWatcher = new FileSystemWatcher(sharedDir, "notify.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _notifyDebounceTimer = new System.Threading.Timer(_ =>
            {
                try { BeginInvoke(HandleNotifyFileChanged); }
                catch { }
            }, null, Timeout.Infinite, Timeout.Infinite);
            void OnNotifyChanged()
            {
                ShellLog("[Notify] file change event");
                _notifyDebounceTimer?.Change(300, Timeout.Infinite);
            }
            _notifyWatcher.Changed += (s, e) => OnNotifyChanged();
            _notifyWatcher.Created += (s, e) => OnNotifyChanged();
            _notifyWatcher.Renamed += (s, e) => OnNotifyChanged();
            ShellLog("[Notify] watcher started");
        }
        catch (Exception ex)
        {
            ShellLog($"[Notify] watcher start failed: {ex.Message}");
        }
    }

    private void HandleNotifyFileChanged()
    {
        try
        {
            ShellLog("[Notify] handle start");
            var path = Path.Combine(SharedConfigDir(), "notify.json");
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return;
            if (_lastNotifyWriteUtc != DateTime.MinValue && fi.LastWriteTimeUtc <= _lastNotifyWriteUtc)
                return;
            _lastNotifyWriteUtc = fi.LastWriteTimeUtc;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            var level = root.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "info" : "info";
            var createdAt = root.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";

            var key = $"{createdAt}|{title}|{message}";
            if (key == _lastNotifyKey) return;
            _lastNotifyKey = key;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message)) return;

            if (!ShowWindowsToast(title, message, level))
                ShowTrayBalloon(title, message, level);
        }
        catch (Exception ex)
        {
            ShellLog($"[Notify] handle failed: {ex.Message}");
        }
    }

    private bool ShowWindowsToast(string title, string message, string level)
    {
        try
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(message));

            var notifier = ToastNotificationManager.CreateToastNotifier("KanzakiK.DouzhanzheConsole");
            notifier.Show(new ToastNotification(xml));
            ShellLog($"[Notify] toast shown: {title}");
            return true;
        }
        catch (Exception ex)
        {
            ShellLog($"[Notify] toast failed, fallback balloon: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void ShowTrayBalloon(string title, string message, string level)
    {
        try
        {
            var icon = level.ToLowerInvariant() switch
            {
                "warning" => ToolTipIcon.Warning,
                "error" => ToolTipIcon.Error,
                _ => ToolTipIcon.Info
            };
            _trayIcon?.ShowBalloonTip(4000, title, message, icon);
            ShellLog($"[Notify] balloon shown: {title}");
        }
        catch (Exception ex)
        {
            ShellLog($"[Notify] balloon failed: {ex.Message}");
        }
    }

    private string ResolveConfigDir()
    {
        var configDir = Path.Combine(AppContext.BaseDirectory, "config");
        if (!Directory.Exists(configDir))
        {
            var devConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
            if (Directory.Exists(devConfig))
                configDir = devConfig;
        }
        Directory.CreateDirectory(configDir);
        return configDir;
    }

    private string HotkeyConfigPath => Path.Combine(ResolveConfigDir(), "hotkey-config.json");
    private string HotkeyStatusPath => Path.Combine(ResolveConfigDir(), "hotkey-status.json");

    private void RegisterHotkeysFromConfig()
    {
        // 注销所有已注册热键
        foreach (var winId in _registeredWinIds)
            UnregisterHotKey(Handle, winId);
        _registeredWinIds.Clear();
        _hotkeyIdToAction.Clear();

        // 读取配置（合并默认值 + 用户自定义）
        var hotkeys = new Dictionary<string, (string modifiers, string key, bool enabled)>();
        foreach (var def in DefaultHotkeys)
            hotkeys[def.id] = (def.modifiers, def.key, true);

        try
        {
            if (File.Exists(HotkeyConfigPath))
            {
                _lastHotkeyConfigWrite = File.GetLastWriteTime(HotkeyConfigPath);
                var json = File.ReadAllText(HotkeyConfigPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("hotkeys", out var hkObj))
                {
                    foreach (var prop in hkObj.EnumerateObject())
                    {
                        var mods = prop.Value.TryGetProperty("modifiers", out var m) ? m.GetString() ?? "ctrl,shift" : "ctrl,shift";
                        var k = prop.Value.TryGetProperty("key", out var kv) ? kv.GetString() ?? "Q" : "Q";
                        var en = prop.Value.TryGetProperty("enabled", out var ev) ? ev.GetBoolean() : true;
                        hotkeys[prop.Name] = (mods, k, en);
                    }
                }
                else
                {
                    // 兼容旧格式
                    if (doc.RootElement.TryGetProperty("monitorOff", out var mo))
                    {
                        var mods = mo.TryGetProperty("modifiers", out var m) ? m.GetString() ?? "ctrl,shift" : "ctrl,shift";
                        var k = mo.TryGetProperty("key", out var kv) ? kv.GetString() ?? "Q" : "Q";
                        var en = mo.TryGetProperty("enabled", out var ev) ? ev.GetBoolean() : true;
                        hotkeys["monitor-off"] = (mods, k, en);
                    }
                }
            }
        }
        catch { }

        // 全局互斥检测：检查内部重复
        var comboSet = new Dictionary<string, string>(); // "mods+key" → configId
        var conflicts = new List<string>();

        int nextWinId = 1;
        foreach (var kvp in hotkeys)
        {
            var id = kvp.Key;
            var (mods, key, enabled) = kvp.Value;
            if (!enabled) { ShellLog($"[Hotkey] 跳过 (disabled): {id}"); continue; }
            var combo = $"{mods}+{key}".ToLowerInvariant();
            if (comboSet.ContainsKey(combo))
            {
                ShellLog($"[Hotkey] 内部冲突: {id} 与 {comboSet[combo]} 都是 {combo}");
                conflicts.Add(id);
                conflicts.Add(comboSet[combo]);
                continue;
            }
            comboSet[combo] = id;

            int winId = nextWinId++;
            bool ok = TryRegisterHotkey(winId, mods, key);
            ShellLog($"[Hotkey] RegisterHotKey({id}, {mods}+{key}, winId={winId}) = {ok}");
            if (ok)
            {
                // 映射到实际动作：mode-office → mode:office；monitor-off 原样保留
                _hotkeyIdToAction[winId] = DefaultHotkeyActions.TryGetValue(id, out var action) ? action : id;
                _registeredWinIds.Add(winId);
            }
            else
            {
                ShellLog($"[Hotkey] 外部冲突: {id} ({mods}+{key}) 已被其他程序占用");
                conflicts.Add(id); // 外部程序占用
            }
        }
        ShellLog($"[Hotkey] 注册完成: 成功 {_registeredWinIds.Count} 个, 冲突 {conflicts.Count} 个");

        WriteHotkeyStatus(conflicts);
    }

    private bool TryRegisterHotkey(int id, string modifiersStr, string keyStr)
    {
        uint fsModifiers = MOD_NOREPEAT;
        var parts = modifiersStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var m in parts)
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl": case "control": fsModifiers |= MOD_CONTROL; break;
                case "alt": fsModifiers |= MOD_ALT; break;
                case "shift": fsModifiers |= MOD_SHIFT; break;
                case "win": fsModifiers |= MOD_WIN; break;
            }
        }

        uint vk = 0;
        if (EventCodeVkMap.TryGetValue(keyStr, out var mappedVk))
            vk = mappedVk;
        else if (keyStr.Length == 1 && char.IsLetter(keyStr[0]))
            vk = (uint)char.ToUpperInvariant(keyStr[0]);
        else if (keyStr.Length == 1 && char.IsDigit(keyStr[0]))
            vk = (uint)keyStr[0];
        else if (Enum.TryParse<Keys>(keyStr, true, out var parsedKey))
            vk = (uint)parsedKey;
        else
        {
            ShellLog($"[Hotkey] 无法解析按键: {keyStr}，跳过注册（不再 fallback Q）");
            return false;
        }

        return RegisterHotKey(Handle, id, fsModifiers, vk);
    }

    private void WriteHotkeyStatus(List<string> conflicts)
    {
        try
        {
            var dir = Path.GetDirectoryName(HotkeyStatusPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(HotkeyStatusPath,
                JsonSerializer.Serialize(new { conflicts }));
        }
        catch { }
    }

    private void StartHotkeyWatcher()
    {
        var dir = Path.GetDirectoryName(HotkeyConfigPath);
        var file = Path.GetFileName(HotkeyConfigPath);
        if (dir == null || !Directory.Exists(dir)) return;

        _hotkeyWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        // 防抖：短时间内多次变更只触发一次
        System.Timers.Timer? debounce = null;
        _hotkeyWatcher.Changed += (s, e) =>
        {
            debounce?.Stop();
            debounce?.Dispose();
            debounce = new System.Timers.Timer(300) { AutoReset = false };
            debounce.Elapsed += (_, _) =>
            {
                if (InvokeRequired) BeginInvoke(new Action(RegisterHotkeysFromConfig));
                else RegisterHotkeysFromConfig();
            };
            debounce.Start();
        };

        // 定时器轮询回退：每 2 秒检查配置文件写入时间，补偿 FileSystemWatcher 可能漏检
        _hotkeyPollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _hotkeyPollTimer.Tick += (s, e) =>
        {
            try
            {
                if (File.Exists(HotkeyConfigPath))
                {
                    var writeTime = File.GetLastWriteTime(HotkeyConfigPath);
                    if (writeTime > _lastHotkeyConfigWrite)
                    {
                        _lastHotkeyConfigWrite = writeTime;
                        RegisterHotkeysFromConfig();
                    }
                }
            }
            catch { }
        };
        _hotkeyPollTimer.Start();
    }
    // ---- DWMWA 标题栏样式 ----
    private void ApplyDwmAttributes()
    {
        try
        {
            var theme = ReadThemeFromConfig(); ShellLog($"[DWM] ApplyDwmAttributes called, theme='{theme}'");
            bool isDark = theme switch
            {
                "dark" => true,
                "light" => false,
                _ => IsWindowsDarkMode()
            };
            int darkMode = isDark ? 1 : 0;
            DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            int cornerPref = DWM_WINDOW_CORNER_ROUND;
            DwmSetWindowAttribute(this.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
            int mica = 1;
            DwmSetWindowAttribute(this.Handle, DWMWA_MICA_TABBED, ref mica, sizeof(int));
        }
        catch { }
    }

    private string? ReadThemeFromConfig()
    {
        try
        {
            var cfgDir = ResolveConfigDir();
            string[] paths = new[] {
                Path.Combine(cfgDir, "ui-state.json"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "server", "config", "ui-state.json")),
            };
            foreach (var path in paths)
            {
                ShellLog($"[DWM] ReadThemeFromConfig: checking path={path}");
                if (!File.Exists(path)) { ShellLog($"[DWM] ReadThemeFromConfig: path NOT FOUND"); continue; }
                var json = File.ReadAllText(path);
                ShellLog($"[DWM] ReadThemeFromConfig: file content='{json}'");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("theme", out var theme))
                {
                    var val = theme.GetString();
                    ShellLog($"[DWM] ReadThemeFromConfig: found theme='{val}'");
                    return val;
                }
                else
                {
                    ShellLog($"[DWM] ReadThemeFromConfig: file has no 'theme' property");
                }
            }
        }
        catch (Exception ex) { ShellLog($"[DWM] ReadThemeFromConfig: EXCEPTION {ex.GetType().Name}: {ex.Message}"); }
        return null;
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var v = key.GetValue("AppsUseLightTheme");
                return v is int i && i == 0;
            }
        }
        catch { }
        return true;
    }
}
