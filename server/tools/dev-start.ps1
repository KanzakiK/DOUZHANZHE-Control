# dev-start.ps1 — 开发态启动 C# HAL API（不构建前端）
# 搭配 `npm run dev` (Vite HMR :5173) 使用
$root = Resolve-Path "$PSScriptRoot\..\api"
$buildDir = Join-Path $root "bin\build"
$runDir = Join-Path $root "bin\run"

# Kill existing process on port 3100
$existing = Get-NetTCPConnection -LocalPort 3100 -ErrorAction SilentlyContinue
if ($existing) {
    $proc = Get-Process -Id $existing.OwningProcess -ErrorAction SilentlyContinue
    if ($proc) { $proc.Kill(); Start-Sleep 2 }
}

# Build
dotnet build "$root\Douzhanzhe.API.csproj" --force 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed!" -Foreground Red; exit 1 }

# Copy build output to run dir
if (Test-Path $runDir) { Remove-Item "$runDir\*" -Recurse -Force }
Copy-Item "$buildDir\*" $runDir -Recurse -Force

# Copy runtime dependencies
$toolsDir = Join-Path $root "..\tools"
@("WinRing0x64.dll", "WinRing0x64.sys", "ryzenadj.exe") | ForEach-Object {
    $src = Join-Path $toolsDir $_
    if (Test-Path $src) { Copy-Item $src $runDir -Force }
}
$inpoutx64 = Join-Path $toolsDir "inpoutx64.dll"
if (Test-Path $inpoutx64) { Copy-Item $inpoutx64 $runDir -Force }

# Launch from run dir
Write-Host "C# HAL API starting on :3100 (dev mode — frontend via Vite HMR :5173)" -Foreground Green
Set-Location $runDir
dotnet Douzhanzhe.API.dll --urls=http://127.0.0.1:3100
