# 素材库 MediaLibrary 安装包构建脚本（阶段 4）
#
# 用法：  pwsh -File installer\build-installer.ps1
# 产出：  installer\dist\MediaLibrary-Setup-<版本>.exe
#
# 步骤：
#   1. dotnet publish 自包含 win-x64（目标电脑无需预装 .NET）
#   2. ISCC 编译 MediaLibrary.iss → 安装包

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repo = Split-Path $installerDir -Parent
$appProject = Join-Path $repo 'src\MediaLibrary.App\MediaLibrary.App.csproj'
$publishDir = Join-Path $installerDir 'publish'

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw '找不到 Inno Setup（ISCC.exe），请先安装：winget install JRSoftware.InnoSetup' }

Write-Host '── 1/2 发布自包含版本（win-x64，含 .NET 运行时 / libvlc / ffmpeg）──' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "    发布目录：$publishDir（$sizeMb MB）"

Write-Host '── 2/2 编译安装包 ──' -ForegroundColor Cyan
Push-Location $installerDir
try {
    & $iscc 'MediaLibrary.iss'
    if ($LASTEXITCODE -ne 0) { throw 'ISCC 编译失败' }
}
finally {
    Pop-Location
}

$setup = Get-ChildItem (Join-Path $installerDir 'dist') -Filter '*.exe' | Select-Object -First 1
Write-Host "完成：$($setup.FullName)（$([math]::Round($setup.Length/1MB,1)) MB）" -ForegroundColor Green
