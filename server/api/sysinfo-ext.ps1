# sysinfo-ext.ps1 - 一次性采集扩展系统信息，输出 JSON
$ErrorActionPreference = 'SilentlyContinue'

# BIOS
$bios = Get-CimInstance Win32_BIOS
$biosVersion = $bios.SMBIOSBIOSVersion
$biosDate = if ($bios.ReleaseDate) { $bios.ReleaseDate.ToString('yyyy-MM-dd') } else { '' }

# OS
$os = Get-CimInstance Win32_OperatingSystem
$osName = ($os.Caption -replace 'Microsoft\s*', '').Trim()
$osBuild = $os.BuildNumber

# Motherboard
$board = Get-CimInstance Win32_BaseBoard
$boardInfo = "$($board.Manufacturer) $($board.Product)"

# NVIDIA driver + VBIOS (nvidia-smi)
$nvDriver = ''
$nvVbios = ''
try {
    $smi = & nvidia-smi --query-gpu=driver_version,vbios_version --format=csv,noheader 2>$null
    if ($smi) {
        $parts = $smi.Trim().Split(',')
        $nvDriver = $parts[0].Trim()
        $nvVbios = $parts[1].Trim()
    }
} catch {}

# Disks (model + capacity per drive)
$disks = @()
Get-CimInstance Win32_DiskDrive | ForEach-Object {
    $sizeGB = [math]::Round($_.Size / 1GB)
    $disks += @{ model = $_.Model.Trim(); sizeGB = $sizeGB }
}

# Memory sticks (per DIMM)
$sticks = @()
Get-CimInstance Win32_PhysicalMemory | ForEach-Object {
    $sizeGB = [math]::Round($_.Capacity / 1GB)
    $speed = $_.ConfiguredClockSpeed
    if (-not $speed) { $speed = $_.Speed }
    $sticks += @{ sizeGB = $sizeGB; speed = $speed; manufacturer = $_.Manufacturer }
}

# Battery (wear from powercfg battery report)
$batt = Get-CimInstance Win32_Battery
$battPercent = if ($batt) { $batt.EstimatedChargeRemaining } else { -1 }
$battDesign = 0
$battFull = 0
try {
    $battReport = Join-Path $env:TEMP 'dz_batt_report.html'
    & powercfg /batteryreport /output $battReport 2>$null | Out-Null
    Start-Sleep 1
    if (Test-Path $battReport) {
        $battHtml = Get-Content $battReport -Raw
        $desMatch = [regex]::Match($battHtml, 'DESIGN CAPACITY.*?(\d[\d,]*)\s*mWh', 'Singleline,IgnoreCase')
        $fulMatch = [regex]::Match($battHtml, 'FULL CHARGE CAPACITY.*?(\d[\d,]*)\s*mWh', 'Singleline,IgnoreCase')
        if ($desMatch.Success) { $battDesign = [int]($desMatch.Groups[1].Value -replace ',','') }
        if ($fulMatch.Success) { $battFull   = [int]($fulMatch.Groups[1].Value -replace ',','') }
        Remove-Item $battReport -Force -ErrorAction SilentlyContinue
    }
} catch {}

# Output JSON
@{
    biosVersion  = $biosVersion
    biosDate     = $biosDate
    osName       = $osName
    osBuild      = $osBuild
    boardInfo    = $boardInfo
    nvDriver     = $nvDriver
    nvVbios      = $nvVbios
    disks        = $disks
    sticks       = $sticks
    battPercent  = $battPercent
    battDesign   = $battDesign
    battFull     = $battFull
} | ConvertTo-Json -Compress
