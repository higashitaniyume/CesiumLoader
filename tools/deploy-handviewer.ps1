<#
.SYNOPSIS
构建并安装可选 HandViewerMod。必须明确指定游戏目录；先退出游戏。
覆盖前备份现有 mod/SDK 文件，保留玩家 config.json 和加载器配置。
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$GameDir)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$loader = Join-Path $GameDir 'AstralParty_ModLoader'
$configPath = Join-Path $loader 'doorstop_config.json'
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'version.dll')) -or -not (Test-Path -LiteralPath $configPath)) {
    throw '请先在目标游戏中安装 CesiumLoader'
}
if (Get-Process -Name 'AstralParty_CN','AstralParty' -ErrorAction SilentlyContinue) { throw '请先退出游戏再部署' }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if (-not $config.sdkVersion -or [version]$config.sdkVersion -lt [version]'2.3.2') {
    throw '加载器配置的 sdkVersion 需至少 2.3.2；请先更新加载器。本脚本不覆盖玩家加载器配置。'
}
if (-not $PSCmdlet.ShouldProcess($GameDir, '构建并备份替换 HandViewerMod 与 SDK dll/pdb')) { return }
dotnet build (Join-Path $repo 'mods\HandViewerMod\HandViewerMod.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '构建失败' }
$modOutput = Join-Path $repo 'mods\HandViewerMod\bin\Release\netstandard2.0'
$sdkOutput = Join-Path $repo 'loader\CesiumLoader.SDK\bin\Release\netstandard2.0'
$backup = Join-Path $loader ('backups\HandViewerMod-' + [guid]::NewGuid().ToString('N'))
$modTarget = Join-Path $loader 'mods\HandViewerMod'
$sdkTarget = Join-Path $loader 'sdk'
$files = @(
    @{ Source = (Join-Path $modOutput 'HandViewerMod.dll'); Target = (Join-Path $modTarget 'HandViewerMod.dll') },
    @{ Source = (Join-Path $modOutput 'HandViewerMod.pdb'); Target = (Join-Path $modTarget 'HandViewerMod.pdb') },
    @{ Source = (Join-Path $modOutput 'HandViewerMod.json'); Target = (Join-Path $modTarget 'HandViewerMod.json') },
    @{ Source = (Join-Path $sdkOutput 'CesiumLoader.SDK.dll'); Target = (Join-Path $sdkTarget 'CesiumLoader.SDK.dll') },
    @{ Source = (Join-Path $sdkOutput 'CesiumLoader.SDK.pdb'); Target = (Join-Path $sdkTarget 'CesiumLoader.SDK.pdb') }
)
foreach ($file in $files) { if (-not (Test-Path -LiteralPath $file.Source)) { throw "缺少产物: $($file.Source)" } }
New-Item -ItemType Directory -Path $backup,$modTarget,$sdkTarget -Force | Out-Null
foreach ($file in $files) {
    if (Test-Path -LiteralPath $file.Target) { Copy-Item -LiteralPath $file.Target -Destination $backup }
}
foreach ($file in $files) { Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force }
Write-Host "已安装到 $modTarget；旧文件备份在 $backup"
Write-Host '启动延迟约 30 秒；PVE 手牌展开可见时查询，默认 5 秒自动收回（AutoCollapseSeconds=0 不收回）。尚需真机验证 UI 和 HTTPS。'
