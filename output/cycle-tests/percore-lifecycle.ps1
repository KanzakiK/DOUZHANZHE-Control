param(
    [string]$BaseUrl = "http://127.0.0.1:3100",
    [string]$Mode = "cfg-silent",
    [string]$Output = "D:\DOUZHANZHE-Control\output\cycle-tests\lifecycle-results.json"
)

$ErrorActionPreference = "Stop"

function Post-Json([string]$Path, [object]$Body) {
    Invoke-RestMethod -Method Post -Uri ($BaseUrl + $Path) `
        -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 8 -Compress)
}

$clearCoAll = Post-Json "/api/overrides/clear" @{
    mode = $Mode
    fields = @("cpuVoltageOffset")
}

$offsets = @()
for ($i = 0; $i -lt 16; $i++) {
    $offsets += @{
        ccd = [math]::Floor($i / 8)
        core = $i % 8
        mv = if (($i % 2) -eq 0) { -5 } else { -9 }
    }
}

$apply = Post-Json ("/api/smu/co-per-core?mode=" + $Mode) @{ offsets = $offsets }
if (-not $apply.ok -or -not $apply.persisted -or $apply.applied -ne 16) {
    throw "Per-core apply failed: $($apply | ConvertTo-Json -Depth 8 -Compress)"
}

$saved = Invoke-RestMethod -Uri ($BaseUrl + "/api/smu/co-per-core")
if (($saved.perCoreCo | Measure-Object).Count -ne 16) {
    throw "Per-core config was not persisted"
}

$switchAway = Post-Json "/api/overrides/switch" @{ mode = "cfg-office" }
Start-Sleep -Seconds 2
$switchBack = Post-Json "/api/overrides/switch" @{ mode = $Mode }
Start-Sleep -Seconds 2

$current = Invoke-RestMethod -Uri ($BaseUrl + "/api/overrides")
if ($current.mode -ne $Mode) {
    throw "Mode switch did not return to $Mode"
}
if (($current.overrides.smu.perCoreCo | Measure-Object).Count -ne 16) {
    throw "Per-core config missing after mode switch"
}

$result = [ordered]@{
    testedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    mode = $Mode
    clearCoAll = $clearCoAll
    apply = $apply
    savedCount = ($saved.perCoreCo | Measure-Object).Count
    switchAwayMode = $switchAway.mode
    switchBackMode = $switchBack.mode
    finalConfigCount = ($current.overrides.smu.perCoreCo | Measure-Object).Count
}
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Output -Encoding UTF8
$result | ConvertTo-Json -Depth 10
