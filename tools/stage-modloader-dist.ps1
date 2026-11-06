<#
.SYNOPSIS
    把本机构建产物刷新到 dist\modloader\ 分发目录（加载器 + Bootstrap + SDK + 内置 mod）。

.DESCRIPTION
    仓库里不再存放任何预编译程序集：所有 dll/pdb/xml 都由源码构建产出（refs/game 提供编译期
    需要的游戏程序集），再用本脚本组装成 dist\modloader\ 的分发布局。SDK 的 XML 文档与
    dll 同名同目录，mod 作者引用该 dll 时 IDE 才能显示注释。

    CI（.github\workflows\release-modloader.yml）与本地 tools\package-modloader.ps1 都调用
    这一份脚本，保证"本地打出来的东西"和"CI 发布的东西"是同一套布局。

    产物是构建输出，全部被 .gitignore 忽略；随仓库入库的只有配置、mod sidecar 与默认皮肤素材。

.PARAMETER NativeDll
    已构建好的 version.dll 路径（默认 loader\CesiumLoader\bin\Release\version.dll）。

.PARAMETER Configuration
    托管程序集的构建配置，默认 Release。

.EXAMPLE
    pwsh -NoProfile -File tools/stage-modloader-dist.ps1
#>
[CmdletBinding()]
param(
    [string] $NativeDll = 'loader\CesiumLoader\bin\Release\version.dll',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$dist = 'dist\modloader'
$layout = "$dist\AstralParty_ModLoader"

# 内置 mod 清单单一来源: tools\builtin-mods.json（CI、打包脚本、清单生成共用）
$builtInMods = @(
    (Get-Content (Join-Path $PSScriptRoot 'builtin-mods.json') -Raw | ConvertFrom-Json) |
        ForEach-Object { [string]$_ }
)
if ($builtInMods.Count -eq 0) { throw 'tools\builtin-mods.json 里没有内置 mod' }

function Copy-WithSymbols([string] $fromBinary, [string] $toBinary, [switch] $Optional, [switch] $WithDocs)
{
    if (-not (Test-Path -LiteralPath $fromBinary))
    {
        if ($Optional) { return $false }
        throw "缺少构建产物: $fromBinary（先构建对应项目）"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $toBinary) -Force | Out-Null
    Copy-Item -LiteralPath $fromBinary -Destination $toBinary -Force
    # .pdb 与 dll 必须来自同一次链接/编译（PDB 里存着 dll 的 GUID+Age），所以永远从同一个目录成对复制。
    $pdb = [System.IO.Path]::ChangeExtension($fromBinary, '.pdb')
    if (Test-Path -LiteralPath $pdb)
    {
        Copy-Item -LiteralPath $pdb -Destination ([System.IO.Path]::ChangeExtension($toBinary, '.pdb')) -Force
    }
    else
    {
        Write-Warning "缺少符号文件(本次不含): $pdb"
    }
    # XML 文档同样必须与 dll 同名同目录, IDE 引用该 dll 时才会显示注释。
    if ($WithDocs)
    {
        $xml = [System.IO.Path]::ChangeExtension($fromBinary, '.xml')
        if (Test-Path -LiteralPath $xml)
        {
            Copy-Item -LiteralPath $xml -Destination ([System.IO.Path]::ChangeExtension($toBinary, '.xml')) -Force
        }
        else
        {
            Write-Warning "缺少 XML 文档(本次不含): $xml"
        }
    }
    return $true
}

Write-Host '刷新 dist\modloader\ 分发布局'
Copy-WithSymbols (Resolve-Path -LiteralPath $NativeDll).Path "$dist\version.dll" | Out-Null
Copy-WithSymbols "loader\CesiumLoader.Bootstrap\bin\$Configuration\netstandard2.0\CesiumLoader.Bootstrap.dll" `
    "$layout\bootstrap\CesiumLoader.Bootstrap.dll" | Out-Null
Copy-WithSymbols "loader\CesiumLoader.SDK\bin\$Configuration\netstandard2.0\CesiumLoader.SDK.dll" `
    "$layout\sdk\CesiumLoader.SDK.dll" -WithDocs | Out-Null

# WPF Overlay（进程外）：与 version.dll 同发布包，exe + dll + pdb 成组同步
# 已 fork 到 AstralParty.Overlay（higashitaniyume/AstralParty.Overlay），本地优先用 fork 产物，
# 再退回 loader/CesiumLoader.Overlay 旧路径（兼容）。CI 优先从 fork Release 下载。
$forkCandidates = @(
    "$repo\..\AstralParty.Overlay\bin\$Configuration\net8.0-windows\win-x64",
    "$repo\loader\CesiumLoader.Overlay\bin\$Configuration\net8.0-windows\win-x64"
)
$overlayOut = $forkCandidates | Where-Object { Test-Path -LiteralPath "$_\CesiumOverlay.exe" } | Select-Object -First 1
if (-not $overlayOut) { $overlayOut = $forkCandidates[0] }
$overlayStaged = $false
if (Test-Path -LiteralPath "$overlayOut\CesiumOverlay.exe")
{
    $overlayDest = "$layout\overlay"
    New-Item -ItemType Directory -Path $overlayDest -Force | Out-Null
    Copy-Item -LiteralPath "$overlayOut\CesiumOverlay.exe" -Destination "$overlayDest\CesiumOverlay.exe" -Force
    foreach ($ext in @("dll","pdb","deps.json","runtimeconfig.json"))
    {
        $f = "$overlayOut\CesiumOverlay.$ext"
        if (Test-Path -LiteralPath $f) { Copy-Item -LiteralPath $f -Destination "$overlayDest\CesiumOverlay.$ext" -Force }
    }
    # self-contained 产物带大量运行时 dll，整目录同步
    $runtimeFiles = @(
        "$overlayOut\System.*.dll", "$overlayOut\Presentation*.dll", "$overlayOut\Windows*.dll",
        "$overlayOut\*.dll"
    )
    foreach ($pat in $runtimeFiles)
    {
        Get-ChildItem -LiteralPath $overlayOut -Filter (Split-Path -Leaf $pat) -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $overlayDest $_.Name) -Force -ErrorAction SilentlyContinue
        }
    }
    # 默认overlay配置（Toys/拖动写同一文件，存在时不覆盖已有）
    $cfgDefault = "loader\CesiumLoader.Overlay\overlay_config.default.json"
    if (Test-Path -LiteralPath $cfgDefault) { Copy-Item -LiteralPath $cfgDefault -Destination "$overlayDest\overlay_config.json" -Force }
    $overlayStaged = $true
    Write-Host "WPF Overlay 已同步到 $overlayDest"
}
else
{
    Write-Warning "未找到 WPF Overlay 产物: $overlayOut\CesiumOverlay.exe（先构建 loader\CesiumLoader.Overlay）"
}

$staged = @("version.dll", 'AstralParty_ModLoader/bootstrap/CesiumLoader.Bootstrap.dll', 'AstralParty_ModLoader/sdk/CesiumLoader.SDK.dll')
if ($overlayStaged) { $staged += 'AstralParty_ModLoader/overlay/CesiumOverlay.exe' }
foreach ($mod in $builtInMods)
{
    $modDir = "$layout\mods\$mod"
    Copy-WithSymbols "mods\$mod\bin\$Configuration\netstandard2.0\$mod.dll" "$modDir\$mod.dll" | Out-Null
    # sidecar: 大多数 mod 的 sidecar 就在源码旁; ActivityLogMod 只维护在 dist 里。
    $sidecarSrc = "mods\$mod\$mod.json"
    if (Test-Path -LiteralPath $sidecarSrc) { Copy-Item -LiteralPath $sidecarSrc -Destination "$modDir\$mod.json" -Force }
    if (-not (Test-Path -LiteralPath "$modDir\$mod.json")) { throw "缺少 mod sidecar: $modDir\$mod.json" }
    # 默认配置: 若源码目录下带有 config.json 则同步带入分发布局。
    $cfgSrc = "mods\$mod\config.json"
    if (Test-Path -LiteralPath $cfgSrc) { Copy-Item -LiteralPath $cfgSrc -Destination "$modDir\config.json" -Force }
    # 随 mod 分发的素材(提示音等): 整目录镜像。先删后拷, 否则 Copy-Item 会套成 sounds\sounds。
    if (Test-Path -LiteralPath "mods\$mod\sounds")
    {
        if (Test-Path -LiteralPath "$modDir\sounds") { Remove-Item -LiteralPath "$modDir\sounds" -Recurse -Force }
        Copy-Item -LiteralPath "mods\$mod\sounds" -Destination "$modDir\sounds" -Recurse -Force
    }
    # 随 mod 分发的数据包(多语言数据等): 同样整目录镜像。
    if (Test-Path -LiteralPath "mods\$mod\data")
    {
        if (Test-Path -LiteralPath "$modDir\data") { Remove-Item -LiteralPath "$modDir\data" -Recurse -Force }
        Copy-Item -LiteralPath "mods\$mod\data" -Destination "$modDir\data" -Recurse -Force
    }
    $staged += "AstralParty_ModLoader/mods/$mod/$mod.dll"
}

Write-Host "dist 已刷新: $($staged.Count) 个程序集, $($builtInMods.Count) 个内置 mod ($($builtInMods -join ', '))"
