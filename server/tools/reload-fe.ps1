# reload-fe.ps1 — 前端热更新：构建 + 部署到运行中的 C# 服务器
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projRoot = Resolve-Path "$root\..\.."
$apiDir = Join-Path $projRoot "server\api"

# 1. 清除 Vite 深缓存 + 构建
Push-Location $projRoot
Remove-Item -Recurse -Force ".vite-temp" -ErrorAction SilentlyContinue
Write-Host "Building frontend..." -Foreground Yellow
npm run build 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed!" -Foreground Red; Pop-Location; exit 1 }

# 2. 部署到 C# 项目 wwwroot（dotnet run 从此目录提供静态文件）
$distDir = Join-Path $projRoot "dist"
$wwwrootDir = Join-Path $apiDir "wwwroot"
if (Test-Path $wwwrootDir) { Remove-Item "$wwwrootDir\*" -Recurse -Force }
Copy-Item "$distDir\*" $wwwrootDir -Recurse -Force
Write-Host "✅ Frontend deployed to $wwwrootDir" -Foreground Green

# 3. 检测 Content root 并推送
$port = 3100
$existing = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "🔁 Server on :$port — refresh your browser to see changes" -Foreground Cyan
} else {
    Write-Host "⚠️  No server detected on :$port — start with run.ps1 first" -Foreground Yellow
}
Pop-Location
