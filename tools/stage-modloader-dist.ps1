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

.PARAMETER NativeDir
    已构建好的原生产物目录（默认 bin\Release），里面必须有 version.dll（薄代理）与
    CesiumLoader.dll（加载器核心）—— 两者是同一层的两个文件，代理按自身目录去找核心。

.PARAMETER Configuration
    托管程序集的构建配置，默认 Release。

.EXAMPLE
    pwsh -NoProfile -File tools/stage-modloader-dist.ps1
#>
[CmdletBinding()]
param(
    [string] $NativeDir = 'bin\Release',
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

# 游戏程序集闸门: SDK 里记着"编译时游戏程序集的 MVID"。
# 换了 refs/game\AstralParty.Runtime.dll 却忘了改这个常量 → 内置 mod 会被整体跳过,
# 或者更糟: 带着旧协议假设的 mod 在游戏里把整局卡死。打包时直接失败, 不让这种包发出去。
$gameAssembly = 'refs\game\AstralParty.Runtime.dll'
if (-not (Test-Path -LiteralPath $gameAssembly)) { throw "缺少游戏热更程序集: $gameAssembly" }
try
{
    $gameMvid = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $gameAssembly).Path).ManifestModule.ModuleVersionId.ToString('D')
}
catch
{
    throw "读不到游戏程序集版本: $gameAssembly ($($_.Exception.Message))"
}
$versionSource = Get-Content 'loader\CesiumLoader.SDK\Manifests\GameVersion.cs' -Raw
$versionMatch = [regex]::Match($versionSource, 'CompiledGameAssemblyMvid\s*=\s*"([0-9a-fA-F\-]+)"')
if (-not $versionMatch.Success) { throw 'loader\CesiumLoader.SDK\Manifests\GameVersion.cs 里找不到 CompiledGameAssemblyMvid' }
$declaredMvid = $versionMatch.Groups[1].Value.ToLowerInvariant()
if ($declaredMvid -ne $gameMvid.ToLowerInvariant())
{
    throw "游戏程序集版本闸门不一致: refs/game 的 MVID 是 $gameMvid, 但 SDK 里写的是 $declaredMvid —— 换游戏程序集后必须同步更新 GameVersion.CompiledGameAssemblyMvid"
}
Write-Host "游戏程序集闸门: $gameMvid"

# 热更侧 BCL 扫描: 拦截"可能被 AOT 裁剪"的成员(3.7.2 事故的直接原因)。
& (Join-Path $PSScriptRoot 'check-hotupdate-apis.ps1')

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

# 原生层是两个文件, 必须成对分发:
#   version.dll      薄代理(Doorstop 式劫持 + 转发系统 version.dll + 加载核心)
#   CesiumLoader.dll 加载器核心(配置/变速/fsredir/profile/shell/IL2CPP 桥...)
# 代理按**自身目录**去找核心(见 loader\shared\cesium_core_abi.h), 所以两者始终同目录:
# 游戏 exe 目录、dist\modloader\、冒烟测试的 bin\<配置>\ 都是这个规则。
$nativeProxy = Join-Path $NativeDir 'version.dll'
$nativeCore = Join-Path $NativeDir 'CesiumLoader.dll'
foreach ($native in @($nativeProxy, $nativeCore))
{
    if (-not (Test-Path -LiteralPath $native))
    {
        throw "缺少原生产物: $native（先构建 loader\CesiumLoader.Proxy 与 loader\CesiumLoader 两个工程）"
    }
}
Copy-WithSymbols (Resolve-Path -LiteralPath $nativeProxy).Path "$dist\version.dll" | Out-Null
Copy-WithSymbols (Resolve-Path -LiteralPath $nativeCore).Path "$dist\CesiumLoader.dll" | Out-Null
Copy-WithSymbols "loader\CesiumLoader.Bootstrap\bin\$Configuration\netstandard2.0\CesiumLoader.Bootstrap.dll" `
    "$layout\bootstrap\CesiumLoader.Bootstrap.dll" | Out-Null
Copy-WithSymbols "loader\CesiumLoader.SDK\bin\$Configuration\netstandard2.0\CesiumLoader.SDK.dll" `
    "$layout\sdk\CesiumLoader.SDK.dll" -WithDocs | Out-Null

$staged = @("version.dll", 'CesiumLoader.dll', 'AstralParty_ModLoader/bootstrap/CesiumLoader.Bootstrap.dll', 'AstralParty_ModLoader/sdk/CesiumLoader.SDK.dll')
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
