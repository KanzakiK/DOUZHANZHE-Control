using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.Text.Json;

namespace Douzhanzhe.Shell;

public partial class Form1 : Form
{
    private WebView2 _webView;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private bool _closeToTray = true;

    private static readonly string _winStatePath = Path.Combine(AppContext.BaseDirectory, "config", "window-state.json");

    private static Icon LoadAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    public Form1()
    {
        Text = "斗战者控制台";
        Width = 1500;
        Height = 1200;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 17, 23); // 深色背景防白闪
        Icon = LoadAppIcon();

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
        // 检查 --minimized 参数：开机自启时隐藏到托盘不显示窗口
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = FormWindowState.Minimized;
            Hide();
        }

        // 启动后端 API（如果尚未运行）
        StartApiIfNotRunning();

        // 初始化 WebView2 — 用户数据目录放在 %LOCALAPPDATA% 下，避免 Program Files 写入权限问题
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Douzhanzhe Console", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // WebView2 初始化失败 — 用内置 HTML 显示错误信息
            _webView.Dispose();
            var errorHtml = $@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Error</title>
<style>body{{background:#0d1117;color:#c9d1d9;font:16px/1.6 system-ui;padding:40px;max-width:700px;margin:0 auto}}
h1{{color:#f85149;font-size:20px}}pre{{background:#161b22;border:1px solid #30363d;border-radius:8px;padding:16px;overflow:auto;color:#f0883e}}
a{{color:#58a6ff}}</style></head><body>
<h1>WebView2 初始化失败</h1>
<p>界面引擎无法启动，请确认已安装 <a href='https://developer.microsoft.com/zh-cn/microsoft-edge/webview2/'>Microsoft Edge WebView2 Runtime</a>。</p>
<pre>{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre>
</body></html>";
            var tmpHtml = Path.Combine(Path.GetTempPath(), "douzhanzhe_error.html");
            File.WriteAllText(tmpHtml, errorHtml);
            Controls.Add(new WebBrowser { Dock = DockStyle.Fill, Url = new Uri(tmpHtml) });
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
                var logPath = Path.Combine(AppContext.BaseDirectory, "api-startup.log");
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
            return;
        }

        _webView.Source = new Uri("http://127.0.0.1:3100/");
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
            _trayIcon.ShowBalloonTip(2000, "斗战者控制台", "已最小化到系统托盘。", ToolTipIcon.Info);
        }
    }

    private void ShowWindow()
    {
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
        var logPath = Path.Combine(baseDir, "api-startup.log");

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

            if (max)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            _webView?.Dispose();
        }
        base.Dispose(disposing);
    }
}
