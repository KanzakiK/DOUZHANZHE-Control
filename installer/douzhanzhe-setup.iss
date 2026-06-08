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
#define MyAppVersion "1.2.0"
#define MyAppPublisher "Douzhanzhe"
#define MyAppExeName "Douzhanzhe.Shell.exe"
#define MyAppApiExeName "Douzhanzhe.API.exe"

; .NET 8 Desktop Runtime x64 下载链接 (微软官方 CDN)
#define DotNetRuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.27/windowsdesktop-runtime-8.0.27-win-x64.exe"
#define DotNetRuntimeFileName "windowsdesktop-runtime-8.0.27-win-x64.exe"

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
  NeedWebView2: Boolean;

function IsDotNet8DesktopInstalled(): Boolean;
var
  Installed: Cardinal;
begin
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\client', 'Install', Installed) then
    Result := (Installed = 1)
  else
    Result := False;
end;

function IsWebView2Installed(): Boolean;
var
  Version: String;
begin
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    Result := (Version <> '')
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    Result := (Version <> '')
  else
    Result := False;
end;

procedure InitializeWizard();
begin
  NeedDotNet := not IsDotNet8DesktopInstalled();
  NeedWebView2 := not IsWebView2Installed();

  if NeedDotNet or NeedWebView2 then
  begin
    DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
    DownloadPage.ShowBaseNameInsteadOfUrl := True;

    if NeedDotNet then
      DownloadPage.Add('{#DotNetRuntimeUrl}', '{#DotNetRuntimeFileName}', '');

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
// 卸载前：关闭正在运行的程序
// ----------------------------------------------------------
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('taskkill', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/f /im {#MyAppApiExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

// ----------------------------------------------------------
// 卸载后清理用户配置（可选）
// ----------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
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
; 开机自启 (最小化到托盘)
Name: "{autostartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--minimized"; Tasks: autostart

[Run]
; 安装完成后启动程序
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 清理运行时生成的文件
Type: files; Name: "{app}\config\*.json"
Type: files; Name: "{app}\*.log"
