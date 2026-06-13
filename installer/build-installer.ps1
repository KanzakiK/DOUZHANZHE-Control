# build-installer.ps1 - 一键构建 + 打包安装程序
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

# ── 版本号自动检测: 未显式指定时从 package.json 读取 ──
if (-not $Version) {
    $PkgPath = Join-Path $Root "package.json"
    if (Test-Path $PkgPath) {
        $PkgRaw = Get-Content $PkgPath -Raw -Encoding UTF8
        if ($PkgRaw -match '"version"\s*:\s*"(\d+\.\d+\.\d+)"') {
            $Version = $matches[1]
            Write-Host "[0/6] 从 package.json 读取版本号: $Version" -ForegroundColor Cyan
        }
    }
    if (-not $Version) {
        Write-Host "[0/6] 警告：未能自动检测版本号，安装包将使用 ISS 默认值" -ForegroundColor Yellow
    }
}

# ── 0. 版本号同步 ──
# 使用 [System.IO.File] 读写以避免 PowerShell 5.1 Set-Content -Encoding UTF8
# 的 BOM 注入和多字节 UTF-8 截断问题
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if ($Version) {
    Write-Host "[0/6] 同步版本号: $Version ..." -ForegroundColor Cyan
    $AbsRoot = "d:\DOUZHANZHE-Control"

    # CHANGELOG.md — 仅替换第一个版本标题（最新条目），不动历史版本
    $Changelog = Join-Path $AbsRoot "CHANGELOG.md"
    $ClText = [System.IO.File]::ReadAllText($Changelog, $utf8NoBom)
    $ClRegex = [regex]::new('(## \[)\d+\.\d+\.\d+(\] — \d{4}-\d{2}-\d{2})')
    $ClText = $ClRegex.Replace($ClText, "`${1}$Version`${2}", 1)
    [System.IO.File]::WriteAllText($Changelog, $ClText, $utf8NoBom)

    # package.json
    $PkgJson = Join-Path $AbsRoot "package.json"
    $PkgText = [System.IO.File]::ReadAllText($PkgJson, $utf8NoBom)
    $PkgText = [regex]::Replace($PkgText, '("version":\s*")\d+\.\d+\.\d+(")', "`${1}$Version`${2}")
    [System.IO.File]::WriteAllText($PkgJson, $PkgText, $utf8NoBom)

    Write-Host "  版本号已同步至 $Version (SettingsPanel/iss 由 ISCC /d 参数覆盖)" -ForegroundColor Green
}

# ── 1. 环境检查 ──
Write-Host "[1/6] 检查环境..." -ForegroundColor Cyan
if (-not (Test-Path $ISCC)) {
    Write-Host "错误: 未找到 Inno Setup 6 编译器！" -ForegroundColor Red
    Write-Host "请安装 https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    Write-Host "错误: 未找到 .NET SDK！" -ForegroundColor Red; exit 1
}

# ── 2. 构建前端 ──
if (-not $SkipFrontend) {
    Write-Host "[2/6] 构建前端..." -ForegroundColor Cyan
    Push-Location $Root
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
    # 清理中间产物，防止无 RID 的增量构建缓存污染 deps.json
    # (开发时 dotnet build 不带 -r，obj/ 缓存会丢失 runtimeTargets → 安装后启动崩溃)
    dotnet clean (Join-Path $Root "server\api\Douzhanzhe.API.csproj") -c Release -v q 2>&1 | Out-Null
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

# 清理开发环境残留配置（dev 值不应打包进安装器）
# 这些文件虽然 .gitignore 排除了，但物理存在于项目目录，dotnet publish 会复制它们。
# 用户的配置文件由 API 在运行时按需创建（带安全默认值），不需要安装器预设。
$PublishConfigDir = Join-Path $ApiDir "config"
if (Test-Path $PublishConfigDir) {
    $DevConfigs = Get-ChildItem $PublishConfigDir -Filter "*.json"
    if ($DevConfigs.Count -gt 0) {
        Remove-Item (Join-Path $PublishConfigDir "*.json") -Force
        Write-Host "  已清理 $($DevConfigs.Count) 个开发配置文件（API 运行时按需创建）" -ForegroundColor Green
    }
}

$size = (Get-ChildItem -Recurse $ApiDir | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "  合并完成，总大小：$([math]::Round($size, 1)) MB" -ForegroundColor Green

# ── 5.5. 验证前端版本号 ──
Write-Host "[5.5/6] 验证前端版本号..." -ForegroundColor Cyan
$WwwRoot = Join-Path $ApiDir "wwwroot"
$JsFile = Get-ChildItem (Join-Path $WwwRoot "assets") -Filter "index-*.js" | Select-Object -First 1
if ($JsFile) {
    $JsContent = Get-Content $JsFile.FullName -Raw -Encoding UTF8
    if ($JsContent -match 'Douzhanzhe Console v(\d+\.\d+\.\d+)') {
        $DetectedVersion = $matches[1]
        if ($Version -and $DetectedVersion -ne $Version) {
            Write-Host "  错误：前端版本号 $DetectedVersion 与预期 $Version 不一致！" -ForegroundColor Red
            exit 1
        }
        Write-Host "  前端版本号：v$DetectedVersion ✅" -ForegroundColor Green
    } else {
        Write-Host "  警告：未在前端文件中找到版本号" -ForegroundColor Yellow
    }
} else {
    Write-Host "  错误：未找到前端 index.js 文件" -ForegroundColor Red
    exit 1
}

# ── 6. 编译安装包 ──
Write-Host "[6/6] 编译 Inno Setup 安装包..." -ForegroundColor Cyan
$IssFile = Join-Path $PSScriptRoot_Fallback "douzhanzhe-setup.iss"
$ISCCArgs = @($IssFile)
if ($Version) {
    $ISCCArgs += "/dMyAppVersion=$Version"
}
& $ISCC $ISCCArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "安装包编译失败！" -ForegroundColor Red; exit 1
}

$SetupFile = Get-ChildItem (Join-Path $Root "dist\installer") -Filter "*.exe" | Select-Object -First 1
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host " 安装包已生成: $($SetupFile.Name)" -ForegroundColor Green
Write-Host " 大小: $([math]::Round($SetupFile.Length / 1MB, 1)) MB" -ForegroundColor Green
Write-Host " 位置: $($SetupFile.FullName)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
