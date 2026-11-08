# start-dev.ps1 - one-click dev environment launcher (dev API on 3101, installed app keeps 3100)
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$DevPort = 3101
$DevApiPath = Join-Path $Root 'server\api\bin\run\Douzhanzhe.API.exe'

function Write-Step($m) { Write-Host ">>> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  OK: $m" -ForegroundColor Green }
function Write-Warn($m) { Write-Host "  ! $m" -ForegroundColor Yellow }

# ============================================================
# 1. ensure dev API on 3101 (installed app keeps 3100)
# ============================================================
Write-Step "1/3 ensure dev API on port $DevPort ..."
$listener = netstat -ano | Select-String "127.0.0.1:$DevPort" | Select-String 'LISTENING'
if ($listener) {
    $existingPid = [int](($listener -split '\s+')[-1])
    $exePath = $null
    try { $exePath = (Get-CimInstance Win32_Process -Filter "ProcessId = $existingPid").ExecutablePath } catch {}
    if ($exePath -and $exePath -like '*Douzhanzhe-Control*') {
        Write-Ok "Dev API already running (PID=$existingPid)"
    } else {
        Write-Warn "Port $DevPort occupied by unknown process (PID=$existingPid), skip auto start"
    }
} else {
    Write-Ok "port $DevPort is free, starting dev API ..."
    # Backend binary lives in bin/run; build freshly so our dev port actually uses the current code.
    Write-Step '1.5/3 build backend + sync dev run dir ...'
    $runDir = Join-Path $Root 'server\api\bin\run'
    $buildDir = Join-Path $Root 'server\api\bin\build'
    dotnet build (Join-Path $Root 'server\api\Douzhanzhe.API.csproj') --nologo
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Copy-Item "$buildDir\*" $runDir -Recurse -Force
    Start-Process -FilePath $DevApiPath -ArgumentList "--urls=http://127.0.0.1:$DevPort" -WorkingDirectory (Join-Path $Root 'server\api\bin\run') -WindowStyle Hidden
    Start-Sleep 2
}

# ============================================================
# 2. build frontend + deploy to wwwroot
# ============================================================
Write-Step '2/3 build frontend + deploy ...'
Push-Location $Root
try {
    if ($SkipBuild) { & '.\deploy.ps1' -SkipBuild }
    else { & '.\deploy.ps1' }
} catch {
    Write-Host "deploy.ps1 failed: $_" -ForegroundColor Red
    Pop-Location; exit 1
}
Pop-Location

# ============================================================
# 3. verify backend is serving
# ============================================================
Write-Step '3/3 verify ...'
Start-Sleep 1
$running = $false
for ($i = 0; $i -lt 8; $i++) {
    try {
        $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$DevPort/" -UseBasicParsing -TimeoutSec 2
        if ($resp.StatusCode -eq 200) { $running = $true; break }
    } catch {}
    Start-Sleep 1
}
if ($running) { Write-Ok "Dev environment ready! http://127.0.0.1:$DevPort/" }
else { Write-Warn "API may not be ready yet, check: $DevApiPath" }
