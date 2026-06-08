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

# Battery
$batt = Get-CimInstance Win32_Battery
$battPercent = if ($batt) { $batt.EstimatedChargeRemaining } else { -1 }
$battStatus = if ($batt) { $batt.Status } else { '' }

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
    battStatus   = $battStatus
} | ConvertTo-Json -Compress
