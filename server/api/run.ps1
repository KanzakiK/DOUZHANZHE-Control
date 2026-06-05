# run.ps1 鈥?Build & run C# HAL API with isolated output dirs
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Join-Path $root "bin\build"
$runDir = Join-Path $root "bin\run"

# Kill existing process on port 3100
$existing = Get-NetTCPConnection -LocalPort 3100 -ErrorAction SilentlyContinue
if ($existing) {
    $proc = Get-Process -Id $existing.OwningProcess -ErrorAction SilentlyContinue
    if ($proc) { $proc.Kill(); Start-Sleep 2 }
}

# Build (always succeeds because build dir is separate from run dir)
dotnet build "$root\Douzhanzhe.API.csproj" --force 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed!" -Foreground Red; exit 1 }

# Copy build output to run dir (fresh copy every time)
if (Test-Path $runDir) { Remove-Item "$runDir\*" -Recurse -Force }
Copy-Item "$buildDir\*" $runDir -Recurse -Force


# Copy WinRing0 + ryzenadj for SMU subprocess
$toolsDir = Join-Path $root "..\tools"
@("WinRing0x64.dll", "WinRing0x64.sys", "ryzenadj.exe") | ForEach-Object {
    $src = Join-Path $toolsDir $_
    if (Test-Path $src) { Copy-Item $src $runDir -Force }
}
# Copy inpoutx64.dll to run dir
$inpoutx64 = Join-Path (Join-Path $root "..\tools") "inpoutx64.dll"
if (Test-Path $inpoutx64) { Copy-Item $inpoutx64 $runDir -Force }

# Build frontend (vite)
Write-Host "Building frontend (vite)..." -Foreground Yellow
$projRoot = Resolve-Path "$root\..\.."
Push-Location $projRoot
npm run build 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "Frontend build failed!" -Foreground Red; Pop-Location; exit 1 }
$distDir = Join-Path $projRoot "dist"
$wwwrootDir = Join-Path $runDir "wwwroot"
if (Test-Path $distDir) {
    if (Test-Path $wwwrootDir) { Remove-Item "$wwwrootDir\*" -Recurse -Force }
    Copy-Item "$distDir\*" $wwwrootDir -Recurse -Force
    Write-Host "Frontend dist copied to wwwroot" -Foreground Green
}
Pop-Location

# Launch from run dir
Write-Host "Launching C# HAL API from $runDir :3100" -Foreground Green
Set-Location $runDir
dotnet Douzhanzhe.API.dll --urls=http://127.0.0.1:3100

