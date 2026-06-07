using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace Douzhanzhe.Shell;

public partial class Form1 : Form
{
    private WebView2 _webView;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private bool _closeToTray = true;

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
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        };
        Controls.Add(_webView);

        Load += Form1_Load;
    }

    private async void Form1_Load(object? sender, EventArgs e)
    {
        // 先初始化 WebView2
        await _webView.EnsureCoreWebView2Async();

        // 等待后端 API 就绪（最多 30 秒）
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (int i = 0; i < 30; i++)
        {
            try
            {
                var resp = await http.GetAsync("http://127.0.0.1:3100/");
                if (resp.IsSuccessStatusCode)
                {
                    _webView.Source = new Uri("http://127.0.0.1:3100/");
                    return;
                }
            }
            catch { }
            await Task.Delay(1000);
        }
        // 超时仍尝试加载
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
        Application.Exit();
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
