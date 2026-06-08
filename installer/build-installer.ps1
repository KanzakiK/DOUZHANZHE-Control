# build-installer.ps1 - 一键构�?+ 打包安装程序
# 用法: .\build-installer.ps1 [-Version "1.3.0"]
#
# 前置要求:
#   - Node.js + npm
#   - .NET 8 SDK
#   - Inno Setup 6.1+ (https://jrsoftware.org/isdl.php)

param(
    [string]$Version,
    [switch]$SkipFrontend,
    [switch]$SkipPublish,
    [string]$ISCC = "C:\Users\liufe\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$Root = "d:\DOUZHANZHE-Control"
$PSScriptRoot_Fallback = "d:\DOUZHANZHE-Control\installer"

# ── 0. 版本号同步 ──
if ($Version) {
    Write-Host "[0/6] 同步版本号: $Version ..." -ForegroundColor Cyan
    $AbsRoot = "d:\DOUZHANZHE-Control"

    # CHANGELOG.md
    $Changelog = Join-Path $AbsRoot "CHANGELOG.md"
    $ClText = Get-Content $Changelog -Raw -Encoding UTF8
    $ClText = [regex]::Replace($ClText, '(## \[)\d+\.\d+\.\d+(\] — \d{4}-\d{2}-\d{2})', "`${1}$Version`${2}", [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Set-Content $Changelog -Value $ClText -NoNewline -Encoding UTF8

    # SettingsPanel.jsx
    $Settings = Join-Path $AbsRoot "src\components\panels\SettingsPanel.jsx"
    $StText = Get-Content $Settings -Raw -Encoding UTF8
    $StText = [regex]::Replace($StText, '(<p>Douzhanzhe Console v)\d+\.\d+\.\d+(</p>)', "`${1}$Version`${2}")
    Set-Content $Settings -Value $StText -NoNewline -Encoding UTF8

    # douzhanzhe-setup.iss
    $IssFile = Join-Path $PSScriptRoot_Fallback "douzhanzhe-setup.iss"
    $IssText = Get-Content $IssFile -Raw -Encoding UTF8
    $IssText = $IssText -replace '(; 版本: )\d+\.\d+\.\d+', "`${1}$Version"
    $IssText = $IssText -replace '(#define MyAppVersion ")\d+\.\d+\.\d+(")', "`${1}$Version`${2}"
    Set-Content $IssFile -Value $IssText -NoNewline -Encoding UTF8

    # package.json
    $PkgJson = Join-Path $AbsRoot "package.json"
    $PkgText = Get-Content $PkgJson -Raw -Encoding UTF8
    $PkgText = [regex]::Replace($PkgText, '("version":\s*")\d+\.\d+\.\d+(")', "`${1}$Version`${2}")
    Set-Content $PkgJson -Value $PkgText -NoNewline -Encoding UTF8

    Write-Host "  版本号已同步至 $Version" -ForegroundColor Green
}

# ── 1. 环境检�?──
Write-Host "[1/6] 检查环�?.." -ForegroundColor Cyan
if (-not (Test-Path $ISCC)) {
    Write-Host "错误: 未找�?Inno Setup 6 编译器�? -ForegroundColor Red
    Write-Host "请安�? https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    Write-Host "错误: 未找�?.NET SDK�? -ForegroundColor Red; exit 1
}

# ── 2. 构建前端 ──
if (-not $SkipFrontend) {
    Write-Host "[2/6] 构建前端..." -ForegroundColor Cyan
    Push-Location $_Root
    npm run deploy
    if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }
    Pop-Location
} else {
    Write-Host "[2/6] 跳过前端构建" -ForegroundColor Yellow
}

# ── 3. 发布 API ──
if (-not $SkipPublish) {
    Write-Host "[3/6] 发布 .NET API..." -ForegroundColor Cyan
    $ApiOut = Join-Path $Root "dist\publish\api"
    if (Test-Path $ApiOut) { Remove-Item -Recurse -Force $ApiOut }
    dotnet publish (Join-Path $Root "server\api\Douzhanzhe.API.csproj") `
        -c Release -r win-x64 --self-contained false -o $ApiOut
    if ($LASTEXITCODE -ne 0) { exit 1 }

    Write-Host "[4/6] 发布 Shell..." -ForegroundColor Cyan
    $ShellOut = Join-Path $Root "dist\publish\shell"
    if (Test-Path $ShellOut) { Remove-Item -Recurse -Force $ShellOut }
    dotnet publish (Join-Path $Root "server\shell\Douzhanzhe.Shell\Douzhanzhe.Shell.csproj") `
        -c Release -r win-x64 --self-contained false -o $ShellOut
    if ($LASTEXITCODE -ne 0) { exit 1 }
} else {
    Write-Host "[3/6] 跳过 .NET 发布" -ForegroundColor Yellow
    Write-Host "[4/6] 跳过 .NET 发布" -ForegroundColor Yellow
}

# ── 5. 合并 + 复制工具 ──
Write-Host "[5/6] 合并发布产物..." -ForegroundColor Cyan
$ApiDir = Join-Path $Root "dist\publish\api"
$ShellDir = Join-Path $Root "dist\publish\shell"
$ToolsDir = Join-Path $Root "server\tools"

# 复制 Shell �?API 目录
Copy-Item -Path (Join-Path $ShellDir "*") -Destination $ApiDir -Recurse -Force

# 复制运行时工�?$ToolFiles = @("ryzenadj.exe", "WinRing0x64.dll", "WinRing0x64.sys")
foreach ($f in $ToolFiles) {
    $src = Join-Path $ToolsDir $f
    if (Test-Path $src) {
        Copy-Item $src $ApiDir -Force
        Write-Host "  已复�? $f" -ForegroundColor Green
    } else {
        Write-Host "  警告: 未找�?$f" -ForegroundColor Yellow
    }
}

# 复制 sysinfo-ext.ps1（不�?publish 输出中，需手动复制�?$SysInfoPs1 = Join-Path $Root "server\api\sysinfo-ext.ps1"
if (Test-Path $SysInfoPs1) {
    Copy-Item $SysInfoPs1 $ApiDir -Force
    Write-Host "  已复�? sysinfo-ext.ps1" -ForegroundColor Green
}

# 清理不需要的目录
@("_bak", "bin_temp") | ForEach-Object {
    $d = Join-Path $ApiDir $_
    if (Test-Path $d) { Remove-Item -Recurse -Force $d }
}

$size = (Get-ChildItem -Recurse $ApiDir | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "  合并完成，总大�? $([math]::Round($size, 1)) MB" -ForegroundColor Green

# ── 6. 编译安装�?──
Write-Host "[6/6] 编译 Inno Setup 安装�?.." -ForegroundColor Cyan
$IssFile = Join-Path $PSScriptRoot_Fallback "douzhanzhe-setup.iss"
& $ISCC $IssFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "安装包编译失�?" -ForegroundColor Red; exit 1
}

$SetupFile = Get-ChildItem (Join-Path $Root "dist\installer") -Filter "*.exe" | Select-Object -First 1
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " 安装包已生成: $($SetupFile.Name)" -ForegroundColor Green
Write-Host " 大小: $([math]::Round($SetupFile.Length / 1MB, 1)) MB" -ForegroundColor Green
Write-Host " 位置: $($SetupFile.FullName)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
