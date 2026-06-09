# 斗战者控制台 - 自启动 (Auto-Start) 完整实现文档

## 概述

斗战者控制台使用 **Windows 计划任务 (Task Scheduler)** 实现开机自启动，而非注册表或启动文件夹方式。系统托盘 (System Tray) 支持最小化后台运行。

---

## 1. 后端实现 (ASP.NET Core API)

### 1.1 依赖库

**文件**: `server\api\Douzhanzhe.API.csproj`
```xml
<PackageReference Include="TaskScheduler" Version="2.11.0" />
```

**文件**: `server\api\Program.cs` (第 8 行)
```csharp
using Microsoft.Win32.TaskScheduler;
```

### 1.2 配置文件路径

**文件**: `server\api\Program.cs` (第 879-880 行)
```csharp
var autoStartOptsPath = Path.Combine(AppContext.BaseDirectory, "config", "auto-start-opts.json");
Directory.CreateDirectory(Path.GetDirectoryName(autoStartOptsPath)!);
```

配置文件格式:
```json
{
  "enabled": true,
  "minimized": true
}
```

### 1.3 读取/写入配置函数

**文件**: `server\api\Program.cs` (第 883-904 行)
```csharp
// 读取本地缓存的 auto-start 状态(快速路径，无 COM 开销)
(bool enabled, bool minimized) ReadAutoStartOpts()
{
    try
    {
        if (File.Exists(autoStartOptsPath))
        {
            var json = File.ReadAllText(autoStartOptsPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var en = root.TryGetProperty("enabled", out var ev) && ev.ValueKind == JsonValueKind.True;
            var min = root.TryGetProperty("minimized", out var mv) && mv.ValueKind == JsonValueKind.True;
            return (en, min);
        }
    }
    catch { }
    return (false, false);
}

void WriteAutoStartOpts(bool enabled, bool minimized)
{
    File.WriteAllText(autoStartOptsPath, JsonSerializer.Serialize(new { enabled, minimized }));
}
```

### 1.4 API 端点: 获取/设置最小化选项

**文件**: `server\api\Program.cs` (第 906-925 行)
```csharp
app.MapGet("/api/auto-start-opts", () =>
{
    var (_, minimized) = ReadAutoStartOpts();
    return Results.Json(new { minimized });
});

app.MapPost("/api/auto-start-opts", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
        if (body == null || !body.TryGetValue("minimized", out var v) || v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)
            return Results.Json(new { ok = false, error = "需要 { minimized: bool }" });
        var minimized = v.GetBoolean();
        var (enabled, _) = ReadAutoStartOpts();
        WriteAutoStartOpts(enabled, minimized);
        return Results.Json(new { ok = true, minimized });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
```

### 1.5 API 端点: 获取自启动状态 (带异步校验)

**文件**: `server\api\Program.cs` (第 928-960 行)
```csharp
app.MapGet("/api/auto-start", () =>
{
    try
    {
        // 快速路径：先读本地缓存，立即返回
        var (cachedEnabled, _) = ReadAutoStartOpts();

        // 后台异步校验：查计划任务，不一致则修正缓存
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // 等 2 秒再查，避免安装后 Task Scheduler 尚未注册完毕导致误判
                Thread.Sleep(2000);
                using var ts = new TaskService();
                var actual = ts.RootFolder.AllTasks.Any(t => t.Name == "DouzhanzheControl");
                // 二次确认：若缓存为 true 但首次未找到，再等 2 秒重试
                if (!actual && cachedEnabled)
                {
                    Thread.Sleep(2000);
                    actual = ts.RootFolder.AllTasks.Any(t => t.Name == "DouzhanzheControl");
                }
                var (curEnabled, min) = ReadAutoStartOpts();
                if (actual != curEnabled)
                    WriteAutoStartOpts(actual, min);
            }
            catch { /* 校验失败不影响本次响应 */ }
        });

        return Results.Json(new { enabled = cachedEnabled });
    }
    catch { return Results.Json(new { enabled = false }); }
});
```

### 1.6 API 端点: 启用/禁用自启动 (创建/删除计划任务)

**文件**: `server\api\Program.cs` (第 961-1011 行)
```csharp
app.MapPost("/api/auto-start", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await reader.ReadToEndAsync());
        if (body == null || !body.TryGetValue("enabled", out var enabledEl) || enabledEl.ValueKind != JsonValueKind.True && enabledEl.ValueKind != JsonValueKind.False)
            return Results.Json(new { ok = false, error = "需要 { enabled: bool }" });
        var enabled = enabledEl.GetBoolean();

        using var ts = new TaskService();
        if (enabled)
        {
            // 定位 Shell.exe：同目录下查找，或 dev 路径回退
            var apiDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            var shellExe = new[] { "Douzhanzhe.Shell.exe" }
                .Select(f => Path.Combine(apiDir, f))
                .FirstOrDefault(File.Exists);
            if (shellExe == null)
            {
                // 开发环境路径回退
                shellExe = Path.GetFullPath(Path.Combine(apiDir, "..", "..", "..", "..", "shell", "Douzhanzhe.Shell", "bin", "Debug", "net8.0-windows", "Douzhanzhe.Shell.exe"));
            }

            // 读取最小化偏好
            var (_, minimized) = ReadAutoStartOpts();

            var td = ts.NewTask();
            td.RegistrationInfo.Description = "Douzhanzhe Console 开机自启";
            td.Principal.RunLevel = TaskRunLevel.Highest;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.DisallowStartOnRemoteAppSession = false;
            td.Triggers.Add(new LogonTrigger());
            td.Actions.Add(shellExe, minimized ? "--minimized" : "");
            ts.RootFolder.RegisterTaskDefinition("DouzhanzheControl", td);
        }
        else
        {
            if (ts.RootFolder.AllTasks.Any(t => t.Name == "DouzhanzheControl"))
                ts.RootFolder.DeleteTask("DouzhanzheControl");
        }

        // 同步写入本地缓存
        var (_, min) = ReadAutoStartOpts();
        WriteAutoStartOpts(enabled, min);

        return Results.Json(new { ok = true, enabled });
    }
    catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }); }
});
```

**关键点**:
- 计划任务名称: `DouzhanzheControl`
- 触发器: `LogonTrigger` (用户登录时触发)
- 运行级别: `TaskRunLevel.Highest` (管理员权限)
- 启动参数: `--minimized` (如果启用了最小化启动)
- 电池策略: 禁用电池限制 (`DisallowStartIfOnBatteries = false`)

---

## 2. 前端实现 (React + JSX)

### 2.1 组件入口

**文件**: `src\App.jsx` (第 189-192 行)
```jsx
{activeTab === "settings" && (
  <SettingsPanel settings={settings} setSettings={setSettings} uxtuPayload={uxtuPayload}
    showSwitches={true} showKeyboard={true} showSummary={true} showCredits={true} showAutoStart={true}
    showBackground={true} bg={bg} updateBg={updateBg} />
)}
```

### 2.2 SettingsPanel 组件

**文件**: `src\components\panels\SettingsPanel.jsx` (完整实现)

#### 状态初始化 (第 8-11 行)
```jsx
export default function SettingsPanel({ settings, setSettings, uxtuPayload, showSwitches = true, showKeyboard = true, showSummary = true, showSmu = true, showAbout = true, showAutoStart = false, showBackground = false, bg, updateBg }) {
  const toast = useToast();
  const [autoStart, setAutoStart] = useState(() => localStorage.getItem("dz_autostart") === "1");
  const [autoStartMinimized, setAutoStartMinimized] = useState(() => localStorage.getItem("dz_autostart_min") === "1");
```

#### 数据加载 (第 12-22 行)
```jsx
useEffect(() => {
  if (!showAutoStart) return;
  fetch("/api/auto-start")
    .then(r => r.json())
    .then(d => { setAutoStart(!!d.enabled); localStorage.setItem("dz_autostart", d.enabled ? "1" : "0"); })
    .catch(() => {});
  fetch("/api/auto-start-opts")
    .then(r => r.json())
    .then(d => { const m = d.minimized === true; setAutoStartMinimized(m); localStorage.setItem("dz_autostart_min", m ? "1" : "0"); })
    .catch(() => {});
}, [showAutoStart]);
```

#### 切换自启动开关 (第 23-37 行)
```jsx
const toggleAutoStart = (v) => {
  localStorage.setItem("dz_autostart", v ? "1" : "0");
  setAutoStart(v);
  fetch("/api/auto-start", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ enabled: v })
  })
    .then(r => r.json())
    .then(d => {
      if (d.ok) { toast?.(v ? "开机自启已开启" : "开机自启已关闭", "success"); }
      else { setAutoStart(!v); localStorage.setItem("dz_autostart", !v ? "1" : "0"); toast?.(d.error || "设置失败", "error"); }
    })
    .catch(() => { setAutoStart(!v); localStorage.setItem("dz_autostart", !v ? "1" : "0"); toast?.("请求失败", "error"); });
};
```

#### 切换最小化启动 (第 38-52 行)
```jsx
const toggleAutoStartMinimized = (v) => {
  localStorage.setItem("dz_autostart_min", v ? "1" : "0");
  setAutoStartMinimized(v);
  fetch("/api/auto-start-opts", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ minimized: v })
  })
    .then(r => r.json())
    .then(d => {
      if (d.ok) { toast?.(v ? "开机自启最小化已开启" : "开机自启最小化已关闭", "success"); }
      else { setAutoStartMinimized(!v); localStorage.setItem("dz_autostart_min", !v ? "1" : "0"); toast?.(d.error || "设置失败", "error"); }
    })
    .catch(() => { setAutoStartMinimized(!v); localStorage.setItem("dz_autostart_min", !v ? "1" : "0"); toast?.("请求失败", "error"); });
};
```

#### UI 渲染 (第 175-182 行)
```jsx
{showAutoStart && (
  <Card title="开机自启" className="!p-3">
    <div className="space-y-1">
      <SwitchRow label="开机自动启动" checked={autoStart} onChange={toggleAutoStart} />
      <SwitchRow label="开机自启最小化" checked={autoStartMinimized} onChange={toggleAutoStartMinimized} />
    </div>
  </Card>
)}
```

**本地存储键值**:
- `dz_autostart`: "1" 或 "0" (自启动启用状态)
- `dz_autostart_min`: "1" 或 "0" (最小化启动状态)

---

## 3. 系统托盘实现 (WinForms NotifyIcon)

### 3.1 依赖库

**文件**: `server\shell\Douzhanzhe.Shell\Douzhanzhe.Shell.csproj` (第 15 行)
```xml
<PackageReference Include="TaskScheduler" Version="2.11.0" />
```

### 3.2 托盘图标初始化

**文件**: `server\shell\Douzhanzhe.Shell\Form1.cs` (第 11-50 行)
```csharp
public partial class Form1 : Form
{
    private WebView2 _webView;
    private NotifyIcon _trayIcon;
    private ContextMenuStrip _trayMenu;
    private bool _closeToTray = true;

    public Form1()
    {
        Text = "斗战者控制台";
        Width = 1500;
        Height = 1200;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 17, 23);
        Icon = LoadAppIcon();

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
        
        // ... WebView2 initialization
    }
}
```

### 3.3 最小化启动参数处理

**文件**: `server\shell\Douzhanzhe.Shell\Form1.cs` (第 76-87 行)
```csharp
private async void Form1_Load(object? sender, EventArgs e)
{
    // 检查 --minimized 参数：开机自启时隐藏到托盘不显示窗口
    var args = Environment.GetCommandLineArgs();
    if (args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
    {
        WindowState = FormWindowState.Minimized;
        Hide();
    }

    // 启动后端 API(如果尚未运行)
    StartApiIfNotRunning();
    
    // ... WebView2 initialization
}
```

### 3.4 关闭时隐藏到托盘

**文件**: `server\shell\Douzhanzhe.Shell\Form1.cs` (第 226-238 行)
```csharp
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
```

### 3.5 最小化时隐藏到托盘

**文件**: `server\shell\Douzhanzhe.Shell\Form1.cs` (第 240-247 行)
```csharp
private void Form1_Resize(object? sender, EventArgs e)
{
    if (WindowState == FormWindowState.Minimized)
    {
        Hide();
        _trayIcon.ShowBalloonTip(2000, "斗战者控制台", "已最小化到系统托盘。", ToolTipIcon.Info);
    }
}
```

### 3.6 恢复窗口

**文件**: `server\shell\Douzhanzhe.Shell\Form1.cs` (第 249-255 行)
```csharp
private void ShowWindow()
{
    Show();
    WindowState = FormWindowState.Normal;
    BringToFront();
    Activate();
}
```

### 3.7 退出应用 (关闭托盘图标并杀掉后端进程)

**文件**: `server\shell\Douzhanzhe.Shell\Form1.cs` (第 257-298 行)
```csharp
private void ExitApp()
{
    _closeToTray = false;
    _trayIcon.Visible = false;
    _trayIcon.Dispose();

    // 杀掉后端 API 进程(:3100)，避免孤儿进程
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
```

### 3.8 资源清理

**文件**: `server\shell\Douzhanzhe.Shell\Form1.cs` (第 475-484 行)
```csharp
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
```

---

## 4. 安装程序实现 (Inno Setup)

### 4.1 安装程序定义

**文件**: `installer\douzhanzhe-setup.iss` (第 1-20 行)
```pascal
; 斗战者控制台 (Douzhanzhe Console) — Inno Setup 安装脚本
; 版本: 1.3.2
; 许可证: GPL-3.0-only

#define MyAppName "斗战者控制台"
#define MyAppNameEn "Douzhanzhe Console"
#ifndef MyAppVersion
  #define MyAppVersion "1.3.2"
#endif
#define MyAppPublisher "Douzhanzhe"
#define MyAppExeName "Douzhanzhe.Shell.exe"
#define MyAppApiExeName "Douzhanzhe.API.exe"
```

### 4.2 创建计划任务函数

**文件**: `installer\douzhanzhe-setup.iss` (第 225-266 行)
```pascal
function CreateAutoStartTask(): Boolean;
var
  XmlPath: String;
  AppPath: String;
  TaskXml: String;
  ResultCode: Integer;
begin
  Result := False;
  AppPath := ExpandConstant('{app}\{#MyAppExeName}');
  XmlPath := ExpandConstant('{tmp}\douzhanzhe_task.xml');

  // 构建 Task Scheduler XML
  TaskXml := '<?xml version="1.0" encoding="UTF-16"?>' + #13#10 +
    '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">' + #13#10 +
    '  <Triggers>' + #13#10 +
    '    <LogonTrigger><Enabled>true</Enabled></LogonTrigger>' + #13#10 +
    '  </Triggers>' + #13#10 +
    '  <Settings>' + #13#10 +
    '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>' + #13#10 +
    '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>' + #13#10 +
    '    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>' + #13#10 +
    '  </Settings>' + #13#10 +
    '  <Actions Context="Author">' + #13#10 +
    '    <Exec>' + #13#10 +
    '      <Command>"' + AppPath + '"</Command>' + #13#10 +
    '      <Arguments>--minimized</Arguments>' + #13#10 +
    '    </Exec>' + #13#10 +
    '  </Actions>' + #13#10 +
    '  <Principals>' + #13#10 +
    '    <Principal>' + #13#10 +
    '      <RunLevel>HighestAvailable</RunLevel>' + #13#10 +
    '    </Principal>' + #13#10 +
    '  </Principals>' + #13#10 +
    '</Task>';

  if SaveStringToFile(XmlPath, TaskXml, False) then
  begin
    if Exec('schtasks', '/Create /TN "DouzhanzheControl" /XML "' + XmlPath + '" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := (ResultCode = 0);
    DeleteFile(XmlPath);
  end;
end;
```

### 4.3 安装后处理

**文件**: `installer\douzhanzhe-setup.iss` (第 271-321 行)
```pascal
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ConfigDir: String;
  ConfigFile: String;
  StartupLink: String;
begin
  // 安装前：先关闭正在运行的程序 + 卸载内核驱动
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill', '/f /im {#MyAppApiExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc', 'stop WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc', 'delete WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;

  // 安装后：创建计划任务 + 清理旧版快捷方式
  if CurStep = ssPostInstall then
  begin
    // 清理旧版启动文件夹快捷方式(从旧版升级时)
    StartupLink := ExpandConstant('{userstartup}\{#MyAppName}.lnk');
    if FileExists(StartupLink) then
      DeleteFile(StartupLink);

    // 先删除已有计划任务(升级场景)
    Exec('schtasks', '/Delete /TN "DouzhanzheControl" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    if WizardIsTaskSelected('autostart') then
    begin
      if CreateAutoStartTask() then
      begin
        // 强制写入自启配置，确保前端 UI 状态一致
        ConfigDir := ExpandConstant('{app}\config');
        if not DirExists(ConfigDir) then
          CreateDir(ConfigDir);
        ConfigFile := ConfigDir + '\auto-start-opts.json';
        SaveStringToFile(ConfigFile, '{"enabled":true,"minimized":true}', False);
      end;
    end
    else
    begin
      // 用户取消勾选：删除计划任务并更新配置
      ConfigDir := ExpandConstant('{app}\config');
      ConfigFile := ConfigDir + '\auto-start-opts.json';
      if FileExists(ConfigFile) then
        SaveStringToFile(ConfigFile, '{"enabled":false,"minimized":true}', False);
    end;
  end;
end;
```

### 4.4 卸载时清理计划任务

**文件**: `installer\douzhanzhe-setup.iss` (第 326-343 行)
```pascal
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('taskkill', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/f /im {#MyAppApiExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);

  // 停止 WinRing0x64 内核驱动服务
  Exec('sc', 'stop WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc', 'delete WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 删除开机自启计划任务
  Exec('schtasks', '/Delete /TN "DouzhanzheControl" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Sleep(300);
end;
```

### 4.5 安装任务选项

**文件**: `installer\douzhanzhe-setup.iss` (第 372-374 行)
```pascal
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加操作:"
Name: "autostart"; Description: "开机自动启动(后台运行)(&A)"; GroupDescription: "附加操作:"
```

---

## 5. 单实例与权限提升

### 5.1 单实例检测 (互斥锁)

**文件**: `server\shell\Douzhanzhe.Shell\Program.cs` (第 10, 46-58 行)
```csharp
private const string MutexName = @"Global\DouzhanzheShell_SingleInstance";

[STAThread]
static void Main(string[] args)
{
    ApplicationConfiguration.Initialize();

    // 1. 单实例检测
    if (Mutex.TryOpenExisting(MutexName, out var existingMutex))
    {
        // 已有实例运行，激活它的窗口
        ActivateExistingWindow();
        existingMutex.Dispose();
        return;
    }

    // 2. 管理员权限检测与自动提权
    bool isElevated = args.Any(a => a == "--elevated");
    if (!isElevated)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            if (TryElevate(args))
                return; // 提权成功，当前进程退出
        }
    }

    // 3. 全局异常处理
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException += (s, e) => LogCrash("UI Thread", e.Exception);
    AppDomain.CurrentDomain.UnhandledException += (s, e) =>
    {
        if (e.ExceptionObject is Exception ex)
            LogCrash("AppDomain", ex);
    };

    // 4. 创建全局单实例互斥锁
    using var mutex = new Mutex(true, MutexName);

    Application.Run(new Form1());
}
```

### 5.2 激活已有实例窗口

**文件**: `server\shell\Douzhanzhe.Shell\Program.cs` (第 92-134 行)
```csharp
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
        // 如果窗口不可见(隐藏到托盘)，先显示
        if (!IsWindowVisible(hWnd))
            ShowWindow(hWnd, SW_SHOW);

        // 用 WM_SYSCOMMAND SC_RESTORE 恢复窗口
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
```

### 5.3 权限提升 (UAC)

**文件**: `server\shell\Douzhanzhe.Shell\Program.cs` (第 136-188 行)
```csharp
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
```

**提权任务名称**: `DouzhanzheConsole_Elevate`

---

## 6. 架构总结

### 6.1 自启动流程图

```
用户登录 Windows
    ↓
Task Scheduler 触发 LogonTrigger
    ↓
执行 Douzhanzhe.Shell.exe --minimized
    ↓
Program.Main() 检测 --minimized 参数
    ↓
Form1 初始化时隐藏窗口 (Hide())
    ↓
StartApiIfNotRunning() 启动 Douzhanzhe.API.exe
    ↓
WebView2 加载 http://127.0.0.1:3100/
    ↓
程序在后台运行，托盘图标可见
```

### 6.2 配置文件位置

- **安装环境**: `{安装目录}\config\auto-start-opts.json`
- **开发环境**: `server\api\bin\Debug\net8.0-windows\config\auto-start-opts.json`

### 6.3 计划任务详情

| 属性 | 值 |
|------|-----|
| 任务名称 | `DouzhanzheControl` |
| 触发器 | `LogonTrigger` (用户登录) |
| 运行级别 | `HighestAvailable` (管理员) |
| 可执行文件 | `Douzhanzhe.Shell.exe` |
| 启动参数 | `--minimized` |
| 电池策略 | 不限制 (`DisallowStartIfOnBatteries=false`) |
| 执行时限 | 无限制 (`PT0S`) |

### 6.4 关键特性

1. **双重状态同步**: 本地 JSON 缓存 + 计划任务异步校验
2. **快速响应**: API 优先读取本地缓存，立即返回
3. **后台纠错**: 异步检查计划任务是否存在，自动修正缓存
4. **最小化启动**: 支持 `--minimized` 参数隐藏到托盘
5. **单实例保护**: 全局互斥锁防止重复启动
6. **自动提权**: ShellExecute + 计划任务双重提权机制
7. **托盘交互**: 双击恢复窗口，右键菜单显示/退出
8. **气球提示**: 最小化/关闭时显示托盘提示

---

## 7. 相关文件清单

### 后端 (C# / ASP.NET Core)
- `server\api\Program.cs` - API 端点实现
- `server\api\Douzhanzhe.API.csproj` - NuGet 依赖
- `server\shell\Douzhanzhe.Shell\Program.cs` - 入口点、单实例、提权
- `server\shell\Douzhanzhe.Shell\Form1.cs` - 主窗口、托盘图标
- `server\shell\Douzhanzhe.Shell\Douzhanzhe.Shell.csproj` - Shell 项目配置

### 前端 (React / JSX)
- `src\App.jsx` - 主应用组件
- `src\components\panels\SettingsPanel.jsx` - 设置面板

### 安装程序 (Inno Setup)
- `installer\douzhanzhe-setup.iss` - 安装脚本

### 配置文件
- `config\auto-start-opts.json` - 自启动配置

---

## 8. 历史变更记录

**文件**: `CHANGELOG.md`

- **开机自启丢失修复**: 移除计划任务 XML 中不兼容的 `DisallowStartOnRemoteAppSession` 节点
- **系统托盘最小化**: 关闭窗口/最小化均隐藏到托盘
- **管理员权限自动提权**: ShellExecute runas → 计划任务后备方案
- **开机自启选项**: 创建/删除计划任务，配置持久化到 `auto-start-opts.json`
- **开机自启状态持久化**: 本地缓存 + 计划任务异步校验
- **覆盖安装时开机自启状态丢失**: 安装程序强制写入配置 + 后端异步校验加延迟重试

---

**文档生成时间**: 2026-06-09
**项目版本**: 斗战者控制台 v1.3.5
