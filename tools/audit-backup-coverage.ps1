# audit-backup-coverage.ps1 — 备份覆盖审计
# ============================================================
# 交叉核对三份清单，确保所有持久化到 config 目录的数据都有备份策略：
#   1. 代码引用：server/api/*.cs 中 Path.Combine(configDir, "...") / JsonWrite("...") 的字面量
#      （真源——新增功能只要持久化新配置，必然出现新的字面量引用）
#   2. 已分类：Program.cs 中 backupCategories 字典的条目
#   3. 白名单：刻意不备份的运行时 / 机器相关 / 遗留文件（本文件内维护）
# 规则：
#   被引用 ∧ 未分类 ∧ 不在白名单 → FAIL（新增配置必须加入备份分类或白名单）
#   已分类 ∧ 未被引用            → WARN（陈旧条目，考虑清理）
# 用法：powershell -File audit-backup-coverage.ps1；FAIL 时退出码 1（build-installer 会中断构建）

param([string]$ApiDir = (Join-Path $PSScriptRoot "..\server\api"))

$ErrorActionPreference = "Stop"
$fail = 0

# ── 1. 代码引用扫描 ──────────────────────────────────────────
$all = (Get-ChildItem $ApiDir -Filter *.cs | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"

$refs = [System.Collections.Generic.HashSet[string]]::new()
$dirs = [System.Collections.Generic.HashSet[string]]::new()
foreach ($m in [regex]::Matches($all, 'Path\.Combine\(\s*\w*[cC]onfig\w*\s*,\s*"([^"]+)"')) {
    $v = $m.Groups[1].Value
    if ($v -match '\.(json|jpg|jpeg|png|webp)$') { [void]$refs.Add($v) }
    else { [void]$dirs.Add(($v -replace "[\\/]+$","") + "/") }   # 目录型引用 → 前缀
}
foreach ($m in [regex]::Matches($all, 'JsonWrite\(\s*"([^"]+)"')) { [void]$refs.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($all, 'JsonRead[^()]*\(\s*"([^"]+)"')) { [void]$refs.Add($m.Groups[1].Value) }

# ── 2. backupCategories 分类清单 ─────────────────────────────
$class = @{}
if ($all -match 'var\s+backupCategories\s*=\s*new\s+Dictionary<string,\s*string\[\]>\s*\{(?s:(.*?))\n\};') {
    foreach ($m in [regex]::Matches($Matches[1], '\["([^"]+)"\]\s*=\s*new\[\]\s*\{([^}]*)\}')) {
        foreach ($f in [regex]::Matches($m.Groups[2].Value, '"([^"]+)"')) {
            $class[$f.Groups[1].Value] = $m.Groups[1].Value
        }
    }
} else {
    Write-Host "[audit] FAIL: 未能在 Program.cs 中定位 backupCategories 字典" -ForegroundColor Red
    exit 1
}

# ── 3. 白名单：刻意不备份（运行时 / 机器相关 / 遗留）──────────
$allow = @{
    "last-mode.json"             = "当前选中模式（运行时状态）"
    "legacy-migration-done.json" = "迁移完成标记"
    "wmi-scan-result.json"       = "WMI 扫描缓存"
    "permission.json"            = "本机权限状态"
    "window-state.json"          = "窗口位置（机器状态）"
    "background.json"            = "旧版壁纸缓存（遗留）"
    "dashboard-default.json"     = "v1.x SortableDashboard 遗留（组件已删，端点待清理）"
    "notify.json"                = "通知瞬态信号（写后即被 Shell 消费）"
}

# ── 4. 核对 ─────────────────────────────────────────────────
function Test-Classified([string]$ref) {
    if ($class.ContainsKey($ref)) { return $true }
    foreach ($k in $class.Keys) {
        if ($k.EndsWith("/")) { continue }
        foreach ($d in $dirs) { if ($d -and $ref.StartsWith($d, "OrdinalIgnoreCase") -and $k.StartsWith($d, "OrdinalIgnoreCase")) { return $true } }
    }
    foreach ($d in $dirs) { if ($ref.StartsWith($d, "OrdinalIgnoreCase")) { return $true } }
    return $false
}

Write-Host "[audit] 代码引用配置项: $($refs.Count) 个 | 目录: $($dirs.Count) 个 | 已分类: $($class.Count) 项 | 白名单: $($allow.Count) 项"

foreach ($r in ($refs | Sort-Object)) {
    if ($class.ContainsKey($r)) { continue }
    if ($dirs -contains ($r -replace '\.[a-z]+$', '')) { continue }   # 文件位于已分类目录
    $coveredByDir = $false
    foreach ($d in $dirs) { if ($r.StartsWith($d, "OrdinalIgnoreCase")) { $coveredByDir = $true; break } }
    if ($coveredByDir) { continue }
    if ($allow.ContainsKey($r)) {
        Write-Host "[audit]   跳过 $r —— $($allow[$r])" -ForegroundColor DarkGray
        continue
    }
    Write-Host "[audit] FAIL: $r 被代码引用但没有任何备份分类/白名单策略" -ForegroundColor Red
    Write-Host "        → 在 Program.cs backupCategories 增加分类，或在 audit-backup-coverage.ps1 白名单声明不备份理由" -ForegroundColor Red
    $fail = 1
}

foreach ($k in ($class.Keys | Sort-Object)) {
    if ($class[$k] -eq "covers" -and $k.EndsWith("/")) { continue }
    $known = $false
    foreach ($r in $refs) { if ($r -eq $k -or $r.StartsWith($k, "OrdinalIgnoreCase")) { $known = $true; break } }
    foreach ($d in $dirs) { if ($k.StartsWith($d, "OrdinalIgnoreCase")) { $known = $true; break } }
    if (-not $known) {
        Write-Host "[audit] WARN: 已分类条目 $k ($($class[$k])) 在代码中无引用（陈旧？）" -ForegroundColor Yellow
    }
}

if ($fail -ne 0) { exit 1 }
Write-Host "[audit] PASS: 所有持久化配置均有备份策略" -ForegroundColor Green
exit 0
