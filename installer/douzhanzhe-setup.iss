; ============================================================
; 斗战者控制台 (Douzhanzhe Console) — Inno Setup 安装脚本
; 版本: 1.2.0
; 许可证: GPL-3.0-only
;
; 依赖: Inno Setup 6.1+ (https://jrsoftware.org/isdl.php)
;
; 编译:
;   用 Inno Setup Compiler 打开此文件，按 F9 编译
;   或命令行: ISCC douzhanzhe-setup.iss
; ============================================================

#define MyAppName "斗战者控制台"
#define MyAppNameEn "Douzhanzhe Console"
#define MyAppVersion "1.3.0-test"
#define MyAppPublisher "Douzhanzhe"
#define MyAppExeName "Douzhanzhe.Shell.exe"
#define MyAppApiExeName "Douzhanzhe.API.exe"

; .NET 8 Desktop Runtime x64 下载链接 (微软官方 CDN)
#define DotNetRuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.27/windowsdesktop-runtime-8.0.27-win-x64.exe"
#define DotNetRuntimeFileName "windowsdesktop-runtime-8.0.27-win-x64.exe"

; ASP.NET Core 8 Runtime x64 下载链接
#define AspNetRuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/8.0.16/dotnet-hosting-8.0.16-win.exe"
#define AspNetRuntimeFileName "dotnet-hosting-8.0.16-win.exe"

; WebView2 Evergreen Bootstrapper
#define WebView2Url "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
#define WebView2FileName "MicrosoftEdgeWebview2Setup.exe"

[Setup]
AppId={{E7D5C2B1-8F3A-4E2D-9B6C-1A2F3E4D5C6B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist\installer
OutputBaseFilename=DouzhanzheConsole-{#MyAppVersion}-Setup
SetupIconFile=..\server\shell\Douzhanzhe.Shell\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Code]
// ----------------------------------------------------------
// 依赖检测与自动下载 (使用 Inno Setup 6.1+ 内置下载能力)
// ----------------------------------------------------------
var
  DownloadPage: TDownloadWizardPage;
  NeedDotNet: Boolean;
  NeedAspNetCore: Boolean;
  NeedWebView2: Boolean;

function IsDotNet8DesktopInstalled(): Boolean;
var
  Installed: Cardinal;
  Version: String;
  FindRec: TFindRec;
  DesktopDir: String;
begin
  Result := False;

  // 检查 1: 独立 Desktop Runtime 安装器创建的键
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\client', 'Install', Installed) then
    if Installed = 1 then begin Result := True; Exit; end;

  // 检查 2: SDK 安装时只有 sharedhost 键，验证版本是否为 8.x
  if RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost', 'Version', Version) then
    if (Length(Version) > 0) and (Version[1] = '8') then begin Result := True; Exit; end;

  // 检查 3: 磁盘文件回退 — 扫描 8.0.* 子目录
  DesktopDir := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if FindFirst(DesktopDir + '\*', FindRec) then
  begin
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
         (Length(FindRec.Name) > 2) and (FindRec.Name[1] = '8') and (FindRec.Name[2] = '.') then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
end;

function IsWebView2Installed(): Boolean;
var
  Version: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    if Version <> '' then begin Result := True; Exit; end;
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    if Version <> '' then begin Result := True; Exit; end;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    if Version <> '' then begin Result := True; Exit; end;
end;

function IsAspNetCore8Installed(): Boolean;
var
  Installed: Cardinal;
  Version: String;
  FindRec: TFindRec;
  AspNetDir: String;
begin
  Result := False;

  // 检查注册表: Hosting Bundle 或独立 ASP.NET Core Runtime 会写入此键
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'Microsoft.AspNetCore.App', Installed) then
    if Installed = 1 then begin Result := True; Exit; end;

  // 磁盘文件回退 — 扫描 8.0.* 子目录
  AspNetDir := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.AspNetCore.App');
  if FindFirst(AspNetDir + '\*', FindRec) then
  begin
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
         (Length(FindRec.Name) > 2) and (FindRec.Name[1] = '8') and (FindRec.Name[2] = '.') then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
    FindClose(FindRec);
  end;
end;

procedure InitializeWizard();
begin
  NeedDotNet := not IsDotNet8DesktopInstalled();
  NeedAspNetCore := not IsAspNetCore8Installed();
  NeedWebView2 := not IsWebView2Installed();

  if NeedDotNet or NeedAspNetCore or NeedWebView2 then
  begin
    DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
    DownloadPage.ShowBaseNameInsteadOfUrl := True;

    if NeedDotNet then
      DownloadPage.Add('{#DotNetRuntimeUrl}', '{#DotNetRuntimeFileName}', '');

    if NeedAspNetCore then
      DownloadPage.Add('{#AspNetRuntimeUrl}', '{#AspNetRuntimeFileName}', '');

    if NeedWebView2 then
      DownloadPage.Add('{#WebView2Url}', '{#WebView2FileName}', '');
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  RuntimePath: String;
  WebView2Path: String;
begin
  Result := True;

  if (CurPageID = wpReady) and (DownloadPage <> nil) then
  begin
    DownloadPage.Show();

    try
      try
        DownloadPage.Download();
      except
        SuppressibleMsgBox(AddPeriod(GetExceptionMessage()), mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;

      // 安装 .NET 8 Desktop Runtime
      if NeedDotNet then
      begin
        DownloadPage.SetText('正在安装 .NET 8 运行时...', '');
        RuntimePath := ExpandConstant('{tmp}\{#DotNetRuntimeFileName}');
        if not Exec(RuntimePath, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
          SuppressibleMsgBox('安装 .NET 8 运行时失败 (错误码: ' + IntToStr(ResultCode) + ')。' + #13#10 +
                             '请手动从 https://dotnet.microsoft.com/download/dotnet/8.0 下载安装。',
                             mbError, MB_OK, IDOK);
      end;

      // 安装 ASP.NET Core 8 Runtime (Hosting Bundle 包含 ASP.NET Core + .NET Runtime)
      if NeedAspNetCore then
      begin
        DownloadPage.SetText('正在安装 ASP.NET Core 运行时...', '');
        RuntimePath := ExpandConstant('{tmp}\{#AspNetRuntimeFileName}');
        if not Exec(RuntimePath, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
          SuppressibleMsgBox('安装 ASP.NET Core 运行时失败 (错误码: ' + IntToStr(ResultCode) + ')。' + #13#10 +
                             '请手动从 https://dotnet.microsoft.com/download/dotnet/8.0 下载安装 Hosting Bundle。',
                             mbError, MB_OK, IDOK);
      end;

      // 安装 WebView2 Runtime
      if NeedWebView2 then
      begin
        DownloadPage.SetText('正在安装 WebView2 运行时...', '');
        WebView2Path := ExpandConstant('{tmp}\{#WebView2FileName}');
        if not Exec(WebView2Path, '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
          SuppressibleMsgBox('安装 WebView2 运行时失败。程序可能无法正常显示界面。',
                             mbError, MB_OK, IDOK);
      end;

    finally
      DownloadPage.Hide();
    end;
  end;
end;

// ----------------------------------------------------------
// 通过 XML 定义创建计划任务（避免 schtasks 引号问题）
// ----------------------------------------------------------
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

  // 构建 Task Scheduler XML（双单引号 '' 在 Pascal 字符串中表示一个单引号）
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

// ----------------------------------------------------------
// 安装后：创建计划任务实现开机自启（与后端 API 使用同一机制）
// ----------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ConfigDir: String;
  ConfigFile: String;
  StartupLink: String;
begin
  // 安装前：先关闭正在运行的程序 + 卸载内核驱动（覆盖安装时防止文件被锁）
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill', '/f /im {#MyAppApiExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // WinRing0x64.sys 是内核驱动，进程退出后驱动服务仍在运行，必须停止服务才能释放文件
    Exec('sc', 'stop WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc', 'delete WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1000);
  end;

  // 安装后：创建计划任务 + 清理旧版快捷方式
  if CurStep = ssPostInstall then
  begin
    // 清理旧版启动文件夹快捷方式（从旧版升级时）
    StartupLink := ExpandConstant('{userstartup}\{#MyAppName}.lnk');
    if FileExists(StartupLink) then
      DeleteFile(StartupLink);

    // 先删除已有计划任务（升级场景）
    Exec('schtasks', '/Delete /TN "DouzhanzheControl" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    if WizardIsTaskSelected('autostart') then
    begin
      if CreateAutoStartTask() then
      begin
        // 强制写入自启配置（覆盖安装时也更新），确保前端 UI 状态一致
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

// ----------------------------------------------------------
// 卸载前：关闭正在运行的程序 + 停止驱动服务 + 删除计划任务
// ----------------------------------------------------------
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('taskkill', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/f /im {#MyAppApiExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);

  // 停止 WinRing0x64 内核驱动服务（否则 .sys 文件被锁定无法删除）
  Exec('sc', 'stop WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc', 'delete WinRing0_1_2_0', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // 删除开机自启计划任务
  Exec('schtasks', '/Delete /TN "DouzhanzheControl" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Sleep(300);
end;

// ----------------------------------------------------------
// 卸载后清理：用户配置 + WebView2 缓存
// ----------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir: String;
  WebView2Dir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 清理 WebView2 运行时数据
    WebView2Dir := ExpandConstant('{app}\{#MyAppExeName}.WebView2');
    if DirExists(WebView2Dir) then
      DelTree(WebView2Dir, True, True, True);

    // 清理用户配置（可选）
    ConfigDir := ExpandConstant('{app}\config');
    if DirExists(ConfigDir) then
    begin
      if MsgBox('是否保留用户配置？（散热曲线、GPU 模式等）' + #13#10 +
                '选"是"保留配置文件，选"否"全部删除。',
                mbConfirmation, MB_YESNO) = IDNO then
        DelTree(ConfigDir, True, True, True);
    end;
  end;
end;

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加操作:"
Name: "autostart"; Description: "开机自动启动（后台运行）(&A)"; GroupDescription: "附加操作:"

[Files]
; API + Shell + 前端 (已合并到同一目录)
Source: "..\dist\publish\api\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "*.pdb,_bak\*,bin_temp\*,config\*.json"
; 用户配置文件 — 仅在首次安装时复制（不覆盖已有配置）
Source: "..\dist\publish\api\config\*"; DestDir: "{app}\config"; Flags: onlyifdoesntexist recursesubdirs createallsubdirs;

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后启动程序（继承安装程序的管理员权限，避免双重 UAC 弹窗）
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; 清理运行时生成的文件
Type: files; Name: "{app}\config\*.json"
Type: files; Name: "{app}\config\background.*"
Type: files; Name: "{app}\*.log"
Type: files; Name: "{app}\crash.log"
