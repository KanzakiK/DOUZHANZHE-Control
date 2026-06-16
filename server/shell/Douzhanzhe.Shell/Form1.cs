using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Douzhanzhe.Shell;

public partial class Form1 : Form
{
    private WebView2 _webView;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private bool _closeToTray = true;
    private bool _isStartupMinimized = false;

    // ---- 全局热键 ----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    private const uint SMTO_NORMAL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private const int HOTKEY_ID_MONITOR_OFF = 1;
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    // SC_MONITORPOWER: wParam=2 = turn off
    private const uint WM_SYSCOMMAND = 0x0112;
    private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

    private FileSystemWatcher? _hotkeyWatcher;
    private System.Windows.Forms.Timer? _hotkeyPollTimer;
    private string? _currentHotkeyModifiers;
    private string? _currentHotkeyKey;
    private DateTime _lastHotkeyConfigWrite = DateTime.MinValue;

    private static readonly string _winStatePath = Path.Combine(AppContext.BaseDirectory, "config", "window-state.json");

    private static Icon LoadAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    public Form1()
    {
        // 尽早检测 --minimized 参数，在窗口显示之前设置隐藏状态
        var startupArgs = Environment.GetCommandLineArgs();
        _isStartupMinimized = startupArgs.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        Text = "斗战者控制台";
        Width = 1500;
        Height = 1200;
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

        // 启动后端 API（如果尚未运行）
        StartApiIfNotRunning();

        // 初始化 WebView2 — 用户数据目录放在 %LOCALAPPDATA% 下，避免 Program Files 写入权限问题
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Douzhanzhe Console", "WebView2");

        // 启动时仅清除 HTTP/GPU/ServiceWorker 缓存（防止前端更新后缓存旧版本）
        // 保留 Local Storage / IndexedDB — 前端 overrides 持久化依赖 localStorage
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
            // 写入日志文件
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Douzhanzhe Console");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "api-startup.log"),
                    $"[{DateTime.Now:HH:mm:ss}] WebView2 init failed: {webViewError}\n");
            }
            catch { }

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
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Douzhanzhe Console");
                var logPath = Path.Combine(logDir, "api-startup.log");
                if (File.Exists(logPath))
                    logContent = System.Net.WebUtility.HtmlEncode(File.ReadAllText(logPath));
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
        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        // 杀掉后端 API 进程（:3100），避免孤儿进程
        KillProcessOnPort(3100);

        Application.Exit();
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

    private void StartApiIfNotRunning()
    {
        if (IsPortListening(3100)) return;

        var baseDir = AppContext.BaseDirectory;
        var apiExe = Path.Combine(baseDir, "Douzhanzhe.API.exe");
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Douzhanzhe Console");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "api-startup.log");

        void AppendLog(string msg) {
            try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
        }

        try { File.WriteAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] API startup begin\n[{DateTime.Now:HH:mm:ss}] BaseDir: {baseDir}\n"); } catch { }

        if (!File.Exists(apiExe))
        {
            AppendLog($"ERROR: Douzhanzhe.API.exe not found at {apiExe}");
            // 列出目录内容帮助排查
            try {
                var files = Directory.GetFiles(baseDir, "*.exe");
                AppendLog($"EXE files in {baseDir}: {string.Join(", ", files.Select(Path.GetFileName))}");
            } catch { }
            return;
        }

        AppendLog($"Starting: {apiExe}");
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
                AppendLog("ERROR: Process.Start returned null");
                return;
            }
            AppendLog($"PID: {proc.Id}");

            // 等 2 秒检查进程是否立即崩溃
            Thread.Sleep(2000);
            if (proc.HasExited)
            {
                AppendLog($"ERROR: Process exited immediately with code {proc.ExitCode}");
                try {
                    var stderr = proc.StandardError.ReadToEnd();
                    if (!string.IsNullOrEmpty(stderr))
                        AppendLog($"STDERR: {stderr[..Math.Min(stderr.Length, 2000)]}");
                    var stdout = proc.StandardOutput.ReadToEnd();
                    if (!string.IsNullOrEmpty(stdout))
                        AppendLog($"STDOUT: {stdout[..Math.Min(stdout.Length, 2000)]}");
                } catch { }
            }
            else
            {
                AppendLog("Process running, waiting for port...");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
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
            int hotkeyId = m.WParam.ToInt32();
            if (hotkeyId == HOTKEY_ID_MONITOR_OFF)
            {
                // 与执行按钮调用的后端 API 完全一致的调用
                SendMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2));
            }
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 清理热键
            UnregisterHotKey(Handle, HOTKEY_ID_MONITOR_OFF);
            _hotkeyPollTimer?.Dispose();
            _hotkeyWatcher?.Dispose();
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
        // 先注销旧注册
        UnregisterHotKey(Handle, HOTKEY_ID_MONITOR_OFF);

        // 默认值
        bool enabled = true;
        string modifiers = "ctrl,shift";
        string key = "Q";

        try
        {
            if (File.Exists(HotkeyConfigPath))
            {
                _lastHotkeyConfigWrite = File.GetLastWriteTime(HotkeyConfigPath);
                var json = File.ReadAllText(HotkeyConfigPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("monitorOff", out var mo))
                {
                    if (mo.TryGetProperty("enabled", out var ev)) enabled = ev.GetBoolean();
                    if (mo.TryGetProperty("modifiers", out var mv)) modifiers = mv.GetString() ?? "ctrl,shift";
                    if (mo.TryGetProperty("key", out var kv)) key = kv.GetString() ?? "Q";
                }
            }
        }
        catch { }

        _currentHotkeyModifiers = modifiers;
        _currentHotkeyKey = key;

        if (!enabled)
        {
            WriteHotkeyStatus(false);
            return;
        }

        bool ok = TryRegisterHotkey(HOTKEY_ID_MONITOR_OFF, modifiers, key);
        WriteHotkeyStatus(!ok); // conflict = 注册失败
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
        if (keyStr.Length == 1 && char.IsLetter(keyStr[0]))
            vk = (uint)char.ToUpperInvariant(keyStr[0]);
        else if (keyStr.Length == 1 && char.IsDigit(keyStr[0]))
            vk = (uint)keyStr[0];
        else if (Enum.TryParse<Keys>(keyStr, true, out var parsedKey))
            vk = (uint)parsedKey;
        else
            vk = (uint)Keys.Q; // fallback

        return RegisterHotKey(Handle, id, fsModifiers, vk);
    }

    private void WriteHotkeyStatus(bool conflict)
    {
        try
        {
            var dir = Path.GetDirectoryName(HotkeyStatusPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(HotkeyStatusPath,
                JsonSerializer.Serialize(new { monitorOffConflict = conflict }));
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
}
