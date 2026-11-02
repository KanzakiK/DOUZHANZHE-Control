; ============================================================
; 斗战者控制台 (Douzhanzhe Console) — Inno Setup 安装脚本
; 版本: 2.0.0
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
#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif
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
  HelperPath: String;
  ResultCode: Integer;
begin
  Result := False;
  // 用本应用自己的 API（WinExe，无控制台窗口）创建开机自启任务，
  // 等价于原先 schtasks /XML 的做法，但不再闪控制台窗口
  HelperPath := ExpandConstant('{app}\{#MyAppApiExeName}');
  if FileExists(HelperPath) then
  begin
    if Exec(HelperPath, '--autostart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Result := (ResultCode = 0);
  end;
end;

// ----------------------------------------------------------
// 安装后：创建计划任务实现开机自启（与后端 API 使用同一机制）
// ----------------------------------------------------------
// ----------------------------------------------------------
// 判断"已安装版本"是否支持 --maintenance 静默维护（2.0.2-pre.5 起）
// ----------------------------------------------------------
// 重要：{app} 里当前装着的 API 可能是老版本，它不认识 --maintenance 参数，
//       会把它当成普通启动参数直接跑成服务端常驻 → 安装器 ewWaitUntilTerminated
//       会永远等下去，整个安装卡死。所以必须先判断版本再决定用哪种方式。
function InstalledVersionSupportsMaintenance(): Boolean;
var
  VerFile: String;
  Lines: TArrayOfString;
  VerText: String;
  PrePos: Integer;
  PreNum: Integer;
begin
  Result := False;
  VerFile := ExpandConstant('{app}\version.txt');
  if not FileExists(VerFile) then Exit;
  if not LoadStringsFromFile(VerFile, Lines) then Exit;
  if Length(Lines) = 0 then Exit;
  VerText := Trim(Lines[0]);
  if VerText = '' then Exit;
  // 主版本必须是 2.0.2
  if CompareText(Copy(VerText, 1, 5), '2.0.2') <> 0 then Exit;
  PrePos := Pos('-pre.', VerText);
  if PrePos = 0 then Exit;                       // 稳定版 2.0.2 不含该能力
  PreNum := StrToIntDef(Trim(Copy(VerText, PrePos + 5, 10)), 0);
  Result := (PreNum >= 5);
end;

// ----------------------------------------------------------
// 安装前：结束正在运行的旧实例 + 清理旧驱动/旧任务/遗留 WebView2
// （覆盖安装时防止 exe 文件被锁）
// ----------------------------------------------------------
procedure StopOldInstances();
var
  ResultCode: Integer;
  HelperPath: String;
begin
  HelperPath := ExpandConstant('{app}\{#MyAppApiExeName}');

  if InstalledVersionSupportsMaintenance() and FileExists(HelperPath) then
  begin
    // 新版：交给本应用自己的助手静默清理（它是 WinExe，无控制台窗口，且会自行退出）
    Exec(HelperPath, '--maintenance', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else
  begin
    // 老版：不认识 --maintenance，必须用 taskkill 结束（老版本本就是控制台程序，会一闪而过）
    Exec('taskkill', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill', '/f /im {#MyAppApiExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  Sleep(1000);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ConfigDir: String;
  ConfigFile: String;
  StartupLink: String;
  WebView2Dir: String;
begin
  // 安装前：结束正在运行的旧实例 + 清理旧驱动/旧任务/遗留 WebView2
  // （覆盖安装时防止 exe 文件被锁）
  // 新版（已装 >= 2.0.2-pre.5）交给本应用自己的 API 以 --maintenance 模式一次做完：
  // 它是 WinExe（无控制台窗口），内部用 API 直接结束进程/停服务/删任务，
  // 不再调 taskkill / sc / schtasks / cmd —— 安装时不会再闪控制台黑框。
  // 老版本不认识 --maintenance（会当成普通启动跑成服务端常驻，导致安装卡死），
  // 故由 StopOldInstances() 按已装版本判断并退回 taskkill。
  if CurStep = ssInstall then
  begin
    StopOldInstances();

    // 清理旧的前端 JS bundle（覆盖安装时 Vite hash 文件名变化，旧 bundle 不会被删除，
    // 导致后端版本号检测用 FirstOrDefault 取到旧版本，从而误报更新）
    if DirExists(ExpandConstant('{app}\wwwroot\assets')) then
      DelTree(ExpandConstant('{app}\wwwroot\assets'), True, True, True);

    // 清理 WebView2 浏览器缓存（覆盖安装时旧 JS bundle 会被缓存，导致版本号不更新）
    // 仅清除 HTTP/GPU/ServiceWorker 缓存，保留 Local Storage / IndexedDB（配置以后端为唯一权威源）
    // index.html 已由后端设置 Cache-Control: no-cache，新 bundle 不会被 HTTP 缓存
    WebView2Dir := ExpandConstant('{localappdata}\Douzhanzhe Console\WebView2');
    if DirExists(WebView2Dir) then
    begin
      // WebView2 实际缓存路径: userDataDir\EBWebView\Default\...
      if DirExists(WebView2Dir + '\EBWebView\Default\Cache') then
        DelTree(WebView2Dir + '\EBWebView\Default\Cache', True, True, True);
      if DirExists(WebView2Dir + '\EBWebView\Default\Code Cache') then
        DelTree(WebView2Dir + '\EBWebView\Default\Code Cache', True, True, True);
      if DirExists(WebView2Dir + '\EBWebView\Default\GPUCache') then
        DelTree(WebView2Dir + '\EBWebView\Default\GPUCache', True, True, True);
      if DirExists(WebView2Dir + '\EBWebView\Default\Service Worker') then
        DelTree(WebView2Dir + '\EBWebView\Default\Service Worker', True, True, True);
      if DirExists(WebView2Dir + '\EBWebView\GrShaderCache') then
        DelTree(WebView2Dir + '\EBWebView\GrShaderCache', True, True, True);
      if DirExists(WebView2Dir + '\EBWebView\ShaderCache') then
        DelTree(WebView2Dir + '\EBWebView\ShaderCache', True, True, True);
    end;
  end;

  // 安装后：创建计划任务 + 清理旧版快捷方式
  if CurStep = ssPostInstall then
  begin
    // 新版 API（WinExe）此时已就位：用它做一次静默清理
    // —— 清掉历史遗留的本应用 WebView2 渲染进程、旧驱动服务与旧计划任务，全程无窗口闪现。
    // （安装前的清理受"已装版本是否支持 --maintenance"限制，见 StopOldInstances）
    Exec(ExpandConstant('{app}\{#MyAppApiExeName}'), '--maintenance', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // 清理旧版启动文件夹快捷方式（从旧版升级时）
    StartupLink := ExpandConstant('{userstartup}\{#MyAppName}.lnk');
    if FileExists(StartupLink) then
      DeleteFile(StartupLink);

    // 先删除已有计划任务（升级场景）—— 由 --maintenance 模式在安装前统一处理，无需再调 schtasks

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
  // 关闭正在运行的程序 + 停止驱动服务 + 删除计划任务 + 清理遗留 WebView2
  // 新版用本应用自己的助手静默完成；老版本不认识 --maintenance，必须用 taskkill
  if InstalledVersionSupportsMaintenance() and
     FileExists(ExpandConstant('{app}\{#MyAppApiExeName}')) then
    Exec(ExpandConstant('{app}\{#MyAppApiExeName}'), '--maintenance', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
  begin
    Exec('taskkill', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill', '/f /im {#MyAppApiExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  Sleep(500);
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
    // 清理 WebView2 运行时数据（实际缓存在 %LOCALAPPDATA% 下，而非安装目录）
    WebView2Dir := ExpandConstant('{localappdata}\Douzhanzhe Console\WebView2');
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
  Excludes: "*.pdb,_bak\*,bin_temp\*,config\*,inpoutx64.dll"
; PawnIO 驱动安装包
Source: "..\dist\publish\api\PawnIO_setup.exe"; DestDir: "{app}"; Flags: ignoreversion; \
  Check: IsAdminLoggedOn
; 配置文件不再预装 — API 在首次启动时按需创建（带安全默认值）

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 安装 PawnIO 内核驱动（静默，不重启）
Filename: "{app}\PawnIO_setup.exe"; Parameters: "-install -silent"; \
  StatusMsg: "正在安装 PawnIO 内核驱动..."; Flags: runascurrentuser; Check: IsAdminLoggedOn
; 旧版驱动服务/文件、旧开机自启任务、遗留 WebView2 的清理
; 已在安装前由 {app}\Douzhanzhe.API.exe --maintenance 静默完成（无控制台窗口闪现），
; 故此处不再调用 sc.exe / cmd.exe。
; 安装完成后启动程序（继承安装程序的管理员权限，避免双重 UAC 弹窗）
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; \
  Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; 清理运行时生成的文件
Type: files; Name: "{app}\config\*.json"
Type: files; Name: "{app}\config\background.*"
Type: files; Name: "{app}\*.log"
Type: files; Name: "{app}\crash.log"
