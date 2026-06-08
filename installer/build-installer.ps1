# build-installer.ps1 - 一键构建 + 打包安装程序
# 用法: .\build-installer.ps1
#
# 前置要求:
#   - Node.js + npm
#   - .NET 8 SDK
#   - Inno Setup 6.1+ (https://jrsoftware.org/isdl.php)

param(
    [switch]$SkipFrontend,
    [switch]$SkipPublish,
    [string]$ISCC = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ── 0. 环境检查 ──
Write-Host "[0/5] 检查环境..." -ForegroundColor Cyan
if (-not (Test-Path $ISCC)) {
    Write-Host "错误: 未找到 Inno Setup 6 编译器。" -ForegroundColor Red
    Write-Host "请安装: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    Write-Host "错误: 未找到 .NET SDK。" -ForegroundColor Red; exit 1
}

# ── 1. 构建前端 ──
if (-not $SkipFrontend) {
    Write-Host "[1/5] 构建前端..." -ForegroundColor Cyan
    Push-Location $Root
    npm run deploy
    if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }
    Pop-Location
} else {
    Write-Host "[1/5] 跳过前端构建" -ForegroundColor Yellow
}

# ── 2. 发布 API ──
if (-not $SkipPublish) {
    Write-Host "[2/5] 发布 .NET API..." -ForegroundColor Cyan
    $ApiOut = Join-Path $Root "dist\publish\api"
    if (Test-Path $ApiOut) { Remove-Item -Recurse -Force $ApiOut }
    dotnet publish (Join-Path $Root "server\api\Douzhanzhe.API.csproj") `
        -c Release -r win-x64 --self-contained false -o $ApiOut
    if ($LASTEXITCODE -ne 0) { exit 1 }

    Write-Host "[3/5] 发布 Shell..." -ForegroundColor Cyan
    $ShellOut = Join-Path $Root "dist\publish\shell"
    if (Test-Path $ShellOut) { Remove-Item -Recurse -Force $ShellOut }
    dotnet publish (Join-Path $Root "server\shell\Douzhanzhe.Shell\Douzhanzhe.Shell.csproj") `
        -c Release -r win-x64 --self-contained false -o $ShellOut
    if ($LASTEXITCODE -ne 0) { exit 1 }
} else {
    Write-Host "[2/5] 跳过 .NET 发布" -ForegroundColor Yellow
    Write-Host "[3/5] 跳过 .NET 发布" -ForegroundColor Yellow
}

# ── 4. 合并 + 复制工具 ──
Write-Host "[4/5] 合并发布产物..." -ForegroundColor Cyan
$ApiDir = Join-Path $Root "dist\publish\api"
$ShellDir = Join-Path $Root "dist\publish\shell"
$ToolsDir = Join-Path $Root "server\tools"

# 复制 Shell 到 API 目录
Copy-Item -Path (Join-Path $ShellDir "*") -Destination $ApiDir -Recurse -Force

# 复制运行时工具
$ToolFiles = @("ryzenadj.exe", "WinRing0x64.dll", "WinRing0x64.sys")
foreach ($f in $ToolFiles) {
    $src = Join-Path $ToolsDir $f
    if (Test-Path $src) {
        Copy-Item $src $ApiDir -Force
        Write-Host "  已复制: $f" -ForegroundColor Green
    } else {
        Write-Host "  警告: 未找到 $f" -ForegroundColor Yellow
    }
}

# 复制 sysinfo-ext.ps1（不在 publish 输出中，需手动复制）
$SysInfoPs1 = Join-Path $Root "server\api\sysinfo-ext.ps1"
if (Test-Path $SysInfoPs1) {
    Copy-Item $SysInfoPs1 $ApiDir -Force
    Write-Host "  已复制: sysinfo-ext.ps1" -ForegroundColor Green
}

# 清理不需要的目录
@("_bak", "bin_temp") | ForEach-Object {
    $d = Join-Path $ApiDir $_
    if (Test-Path $d) { Remove-Item -Recurse -Force $d }
}

$size = (Get-ChildItem -Recurse $ApiDir | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "  合并完成，总大小: $([math]::Round($size, 1)) MB" -ForegroundColor Green

# ── 5. 编译安装包 ──
Write-Host "[5/5] 编译 Inno Setup 安装包..." -ForegroundColor Cyan
$IssFile = Join-Path $PSScriptRoot "douzhanzhe-setup.iss"
& $ISCC $IssFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "安装包编译失败!" -ForegroundColor Red; exit 1
}

$SetupFile = Get-ChildItem (Join-Path $Root "dist\installer") -Filter "*.exe" | Select-Object -First 1
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " 安装包已生成: $($SetupFile.Name)" -ForegroundColor Green
Write-Host " 大小: $([math]::Round($SetupFile.Length / 1MB, 1)) MB" -ForegroundColor Green
Write-Host " 位置: $($SetupFile.FullName)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
