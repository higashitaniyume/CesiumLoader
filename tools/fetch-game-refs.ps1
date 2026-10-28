<#
.SYNOPSIS
    把一份游戏提取目录里的引用程序集同步进 refs/game/，并刷新 sources.json 的哈希。

.DESCRIPTION
    SDK、内置模组与测试在编译期绑定游戏热更程序集，这些程序集随仓库放在 refs/game/，
    因此 clone 后无需任何本地准备即可构建。游戏更新后本脚本负责整组替换：

      1. 校验源目录里列出的程序集全部存在（缺一个就退出，不写半新组合）
      2. 拷贝到 refs/game/
      3. 重写 refs/game/sources.json（基线说明 + 每个文件的 SHA256）

    脚本只处理 refs/game/sources.json 里列出的程序集；游戏目录里其它 DLL 不需要入库。

.PARAMETER Source
    游戏提取目录（含 AstralParty.Runtime.dll 等）的路径。

.PARAMETER Baseline
    可选，写进 sources.json 的版本说明，例如 '3.4.0 / Unity 2022'。

.EXAMPLE
    pwsh -NoProfile -File tools/fetch-game-refs.ps1 -Source D:\extract\extracted_dlls -Baseline '3.4.0 / Unity 2022'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Source,
    [string] $Baseline
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$target = Join-Path $repo 'refs/game'
$manifestPath = Join-Path $target 'sources.json'

if (-not (Test-Path -LiteralPath $Source -PathType Container)) { throw "源目录不存在: $Source" }
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "缺少清单: $manifestPath" }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$names = @($manifest.assemblies.PSObject.Properties.Name)
if ($names.Count -eq 0) { throw "sources.json 里没有列出任何程序集: $manifestPath" }

$missing = @($names | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Source $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "源目录缺少程序集: $($missing -join ', ')。请确认这是完整的游戏提取目录。"
}

New-Item -ItemType Directory -Path $target -Force | Out-Null
$hashes = [ordered]@{}
foreach ($name in $names) {
    $from = Join-Path $Source $name
    $to = Join-Path $target $name
    Copy-Item -LiteralPath $from -Destination $to -Force
    $hashes[$name] = (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("  {0,-30} {1}" -f $name, $hashes[$name])
}

$updated = [ordered]@{
    description = $manifest.description
    baseline    = if ($Baseline) { $Baseline } else { $manifest.baseline }
    updatedAt   = (Get-Date).ToString('yyyy-MM-dd')
    assemblies  = $hashes
}
$updated | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "已刷新 $($names.Count) 个引用程序集: $target"
Write-Host '提交 refs/game/ 下的改动, CI 与本地构建会用同一份文件。'
