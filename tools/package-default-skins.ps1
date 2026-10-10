<#
.SYNOPSIS
    为同一版本生成独立默认皮肤包，并校验加载器、皮肤和 SDK 三类发布资产。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Version,
    [string] $OutputDir = 'dist/release'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw '版本号含非法字符' }
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'dist/modloader/AstralParty_ModLoader/skins/default'
$output = if ([IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repo $OutputDir }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$output = (Resolve-Path -LiteralPath $output).Path
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "缺少默认皮肤目录: $source" }
$images = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.Extension -in '.png', '.jpg', '.jpeg', '.bmp' })
if ($images.Count -eq 0) { throw '默认皮肤目录中没有图片，拒绝发布空包' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-ZipFiles([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少发布资产: $path" }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try { @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') }) }
    finally { $archive.Dispose() }
}
foreach ($name in @("cesium-loader-$Version.zip", 'cesium-loader.zip')) {
    $entries = @(Get-ZipFiles (Join-Path $output $name))
    if ($entries -match '(^|/)skins(/|$)') { throw "加载器包包含 skins: $name" }
    if ($entries -notcontains 'version.dll' -or @($entries | Where-Object { $_ -match '^AstralParty_ModLoader/mods/.+\.dll$' }).Count -eq 0) { throw "加载器包缺少加载器或 mod: $name" }
}
foreach ($name in @("cesium-sdk-tools-$Version.zip", 'cesium-sdk-tools.zip')) {
    $entries = @(Get-ZipFiles (Join-Path $output $name))
    if ($entries -notcontains 'cesium.exe' -or $entries -notcontains 'CesiumLoader.SDK.dll') { throw "SDK 工具包内容不完整: $name" }
}

# 独立暂存目录不会污染加载器 staging；只复制 default，不包含其他皮肤方案。
$stage = Join-Path $output ('.default-skins-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$stage = (Resolve-Path -LiteralPath $stage).Path
try {
    $target = Join-Path $stage 'AstralParty_ModLoader/skins'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Recurse
    $zip = Join-Path $output "cesium-default-skins-$Version.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force
    $entries = @(Get-ZipFiles $zip)
    if ($entries.Count -eq 0 -or @($entries | Where-Object { -not $_.StartsWith('AstralParty_ModLoader/skins/default/') }).Count -gt 0) {
        throw '默认皮肤包目录结构不正确'
    }
    $sourceRoot = (Resolve-Path -LiteralPath $source).Path
    $expected = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | ForEach-Object {
        'AstralParty_ModLoader/skins/default/' + $_.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
    })
    if (Compare-Object $expected $entries) { throw '默认皮肤包文件清单与源目录不一致' }
    Copy-Item -LiteralPath $zip -Destination (Join-Path $output 'cesium-default-skins.zip') -Force
    foreach ($package in @('cesium-loader', 'cesium-default-skins', 'cesium-sdk-tools')) {
        $versioned = Join-Path $output "$package-$Version.zip"
        $latest = Join-Path $output "$package.zip"
        if ((Get-FileHash -LiteralPath $versioned).Hash -ne (Get-FileHash -LiteralPath $latest).Hash) { throw "固定名与版本包不一致: $package" }
    }
    Write-Host "六个发布资产校验通过；默认皮肤包包含 $($entries.Count) 个文件、$($images.Count) 张图片。"
}
finally {
    # stage 是本次新建并解析过的独立绝对路径。
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
