param(
    [string]$BaseUrl = "http://127.0.0.1:3100",
    [string]$Mode = "cfg-silent",
    [string]$Backup = "D:\DOUZHANZHE-Control\output\cycle-tests\pre-percore-lifecycle.json"
)

$ErrorActionPreference = "Stop"

function Post-Json([string]$Path, [object]$Body) {
    Invoke-RestMethod -Method Post -Uri ($BaseUrl + $Path) `
        -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 20 -Compress)
}

$clear = Invoke-RestMethod -Method Post -Uri ($BaseUrl + "/api/smu/co-per-core/clear?mode=" + $Mode)
if (-not $clear.ok -or -not $clear.persisted -or $clear.applied -ne 16) {
    throw "Per-core clear failed: $($clear | ConvertTo-Json -Depth 8 -Compress)"
}

$backupData = Get-Content -LiteralPath $Backup -Raw -Encoding UTF8 | ConvertFrom-Json
$profileOverrides = $backupData.categories.config.'profiles/cfg-silent.json'
$restore = Invoke-RestMethod -Method Put -Uri ($BaseUrl + "/api/profiles/" + $Mode) `
    -ContentType "application/json" -Body ($profileOverrides | ConvertTo-Json -Depth 20 -Compress)
if (-not $restore.ok) {
    throw "Config restore failed: $($restore | ConvertTo-Json -Depth 8 -Compress)"
}

$switch = Post-Json "/api/overrides/switch" @{ mode = $Mode }
Start-Sleep -Seconds 2
$current = Invoke-RestMethod -Uri ($BaseUrl + "/api/overrides")
if ($current.overrides.smu.coAll -ne -15) {
    throw "Original coAll=-15 was not restored"
}
if (($current.overrides.smu.perCoreCo | Measure-Object).Count -ne 0) {
    throw "Test per-core values are still present"
}

[ordered]@{
    restoredAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    clear = $clear
    restore = $restore
    finalMode = $current.mode
    coAll = $current.overrides.smu.coAll
    perCoreCount = ($current.overrides.smu.perCoreCo | Measure-Object).Count
} | ConvertTo-Json -Depth 10
