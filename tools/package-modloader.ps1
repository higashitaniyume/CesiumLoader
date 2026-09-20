<#
.SYNOPSIS
    本地复刻 release-modloader.yml 的打包流程，产出可发布的 cesium-loader 发布包。

.DESCRIPTION
    与 CI (.github\workflows\release-modloader.yml) 保持同一套步骤，便于打 tag 前先在本地验证：
      1. MSBuild 构建原生加载器 version.dll (Release x64)
      2. dotnet 构建托管层: CesiumLoader.Bootstrap / CesiumLoader.SDK / 各 mod
      3. 刷新 dist\modloader\ 分发目录(加载器 + bootstrap + sdk + 内置 mod)
      4. 组装 staging\ 部署布局 + README.txt
      5. 生成 cesium-loader.json 清单(含每个文件的 SHA256)
      6. 构建 cesium CLI 自包含单文件 → sdk-staging\ + README.txt
      7. 打包 zip: cesium-loader-<版本>.zip / cesium-loader.zip
                   cesium-sdk-tools-<版本>.zip / cesium-sdk-tools.zip

    版本号单一来源: src\CesiumLoader.SDK\ModManifest.cs 里的 SdkVersion.Current。
    加载器版本与之一致(加载器横幅同时打印两者, 见 config.h 的 loaderVersion)。

.PARAMETER Version
    覆盖版本号(默认从 ModManifest.cs 读取)。

.PARAMETER OutputDir
    产物输出目录(默认 dist\release)。

.PARAMETER SkipBuild
    跳过编译, 只用 dist\modloader\ 里已有的产物打包(快速重打包)。

.EXAMPLE
    pwsh -File tools\package-modloader.ps1
    pwsh -File tools\package-modloader.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDir = 'dist\release',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# 内置 mod: 随加载器一起分发(mods\{ModId}\{ModId}.dll + sidecar)
$BuiltInMods = @('ActivityLogMod', 'FreeCameraMod')

function Write-Step([string] $text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 版本号
function Resolve-Version
{
    if ($Version) { return $Version }
    $manifest = Join-Path $repo 'src\CesiumLoader.SDK\ModManifest.cs'
    $m = [regex]::Match([System.IO.File]::ReadAllText($manifest), 'Current\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
    if (-not $m.Success) { throw "无法从 ModManifest.cs 解析 SdkVersion.Current, 请用 -Version 显式指定" }
    return $m.Groups[1].Value
}

# ---------------------------------------------------------------- MSBuild 定位
function Resolve-MSBuild
{
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere)
    {
        $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($path)
        {
            $candidate = Join-Path $path.Trim() 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }
    # 兜底: 常见安装位置
    foreach ($probe in (Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue))
    {
        return $probe.FullName
    }
    throw '找不到 MSBuild.exe, 请安装 Visual Studio (含 C++ 生成工具) 或改用 -SkipBuild 复用已有产物'
}

$version = Resolve-Version
Write-Host "CesiumLoader 发布打包" -ForegroundColor Green
Write-Host "  版本    : $version"
Write-Host "  输出目录: $OutputDir"
Write-Host "  内置 mod: $($BuiltInMods -join ', ')"

# ---------------------------------------------------------------- 1/2 构建
if (-not $SkipBuild)
{
    Write-Step '1. 构建原生加载器 (version.dll, Release x64)'
    $msbuild = Resolve-MSBuild
    Write-Host "  MSBuild: $msbuild"
    & $msbuild 'src\CesiumLoader\CesiumLoader.vcxproj' /p:Configuration=Release /p:Platform=x64 /m /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "原生加载器构建失败 (exit $LASTEXITCODE)" }
    $nativeDll = 'src\CesiumLoader\bin\Release\version.dll'
    if (-not (Test-Path $nativeDll)) { throw "version.dll 未生成: $nativeDll" }

    Write-Step '2. 构建托管层 (Bootstrap / SDK / mods / cesium CLI)'
    dotnet build 'src\CesiumLoader.Bootstrap\CesiumLoader.Bootstrap.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrap 构建失败' }
    dotnet build 'src\CesiumLoader.SDK\CesiumLoader.SDK.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'SDK 构建失败' }
    foreach ($mod in $BuiltInMods)
    {
        dotnet build "src\$mod\$mod.csproj" -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "$mod 构建失败" }
    }

    Write-Step '3. 刷新 dist\modloader\ 分发目录'
    $dist = 'dist\modloader'
    $layout = @(
        @{ From = $nativeDll;                                                              To = "$dist\version.dll" },
        @{ From = 'src\CesiumLoader.Bootstrap\bin\Release\netstandard2.0\CesiumLoader.Bootstrap.dll'; To = "$dist\AstralParty_ModLoader\bootstrap\CesiumLoader.Bootstrap.dll" },
        @{ From = 'src\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll';   To = "$dist\AstralParty_ModLoader\sdk\CesiumLoader.SDK.dll" }
    )
    foreach ($item in $layout)
    {
        if (-not (Test-Path $item.From)) { throw "缺少构建产物: $($item.From)" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $item.To) -Force | Out-Null
        Copy-Item $item.From $item.To -Force
    }
    foreach ($mod in $BuiltInMods)
    {
        $modDir = "$dist\AstralParty_ModLoader\mods\$mod"
        New-Item -ItemType Directory -Path $modDir -Force | Out-Null
        $built = "src\$mod\bin\Release\netstandard2.0\$mod.dll"
        if (-not (Test-Path $built)) { throw "缺少 mod 产物: $built" }
        Copy-Item $built "$modDir\$mod.dll" -Force
        # sidecar: 有的 mod 源码里带 .json(如 FreeCameraMod), 有的只在 dist 里维护(如 ActivityLogMod)
        $sidecarSrc = "src\$mod\$mod.json"
        if (Test-Path $sidecarSrc) { Copy-Item $sidecarSrc "$modDir\$mod.json" -Force }
        if (-not (Test-Path "$modDir\$mod.json")) { throw "缺少 mod sidecar: $modDir\$mod.json" }
    }
    Write-Host '  dist 已刷新'
}
else
{
    Write-Step '跳过编译 (-SkipBuild): 复用 dist\modloader\'
}

# ---------------------------------------------------------------- 4/5 组装 + 清单
Write-Step '4. 组装部署布局 (staging)'
if (Test-Path 'staging') { Remove-Item 'staging' -Recurse -Force }
foreach ($sub in @('AstralParty_ModLoader\sdk', 'AstralParty_ModLoader\mods', 'AstralParty_ModLoader\logs', 'AstralParty_ModLoader\bootstrap'))
{
    New-Item -ItemType Directory -Path "staging\$sub" -Force | Out-Null
}
Copy-Item 'dist\modloader\version.dll' 'staging\version.dll' -Force
Copy-Item 'dist\modloader\AstralParty_ModLoader\doorstop_config.json' 'staging\AstralParty_ModLoader\doorstop_config.json' -Force
Copy-Item 'dist\modloader\AstralParty_ModLoader\bootstrap\CesiumLoader.Bootstrap.dll' 'staging\AstralParty_ModLoader\bootstrap\CesiumLoader.Bootstrap.dll' -Force
Copy-Item 'dist\modloader\AstralParty_ModLoader\sdk\CesiumLoader.SDK.dll' 'staging\AstralParty_ModLoader\sdk\CesiumLoader.SDK.dll' -Force

$manifestFiles = @(
    'staging/version.dll',
    'staging/AstralParty_ModLoader/doorstop_config.json',
    'staging/AstralParty_ModLoader/bootstrap/CesiumLoader.Bootstrap.dll',
    'staging/AstralParty_ModLoader/sdk/CesiumLoader.SDK.dll'
)
foreach ($mod in $BuiltInMods)
{
    $from = "dist\modloader\AstralParty_ModLoader\mods\$mod"
    $to = "staging\AstralParty_ModLoader\mods\$mod"
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    Copy-Item "$from\$mod.dll" "$to\$mod.dll" -Force
    Copy-Item "$from\$mod.json" "$to\$mod.json" -Force
    $manifestFiles += "staging/AstralParty_ModLoader/mods/$mod/$mod.dll"
    $manifestFiles += "staging/AstralParty_ModLoader/mods/$mod/$mod.json"
}

$modList = ($BuiltInMods | ForEach-Object { "mods\{$_}\{$_}.dll" }) -join ' / '
$readme = @(
    "cesium-loader-$version",
    "- version.dll                          → 复制到游戏 exe 目录 (Doorstop 式代理加载器)",
    "- AstralParty_ModLoader\doorstop_config.json → 加载器配置 (enabled=false 可完全禁用)",
    "- AstralParty_ModLoader\bootstrap\     → 托管引导程序 (先于 SDK/mods 加载)",
    "- AstralParty_ModLoader\sdk\           → SDK 依赖",
    "- AstralParty_ModLoader\mods\          → 用户 mod (每 mod 一个文件夹: mods\{ModId}\{ModId}.dll)",
    "  随包内置: $modList",
    "- AstralParty_ModLoader\mods\{ModId}\{ModId}.json → mod sidecar (id/版本/权限/enabled 开关/依赖)",
    "- AstralParty_ModLoader\logs\          → 日志目录",
    "解压到游戏 exe 目录即完成安装。",
    "从旧版升级: 请删除游戏目录下的 winmm.dll (旧代理), 换成 version.dll。",
    "⚠ 与本加载器互斥: 旧版独立变速器也使用 version.dll(speedhack-rs) —— 不要同时安装!",
    "  CesiumLoader 已内置变速引擎(加载器功能, 非 mod): 改 doorstop_config.json 的",
    "  speedhackBaseSpeed 即可(2.0=全程2倍速, 1.0=正常), 或直接用 AstralParty.Toys 的模组页面开关。"
) -join [Environment]::NewLine
Set-Content -Path 'staging\README.txt' -Value $readme -Encoding UTF8

Write-Step '5. 生成 cesium-loader.json 清单'
$fileMap = [ordered]@{}
foreach ($file in $manifestFiles)
{
    $rel = $file.Substring('staging/'.Length) -replace '\\', '/'
    $fileMap[$rel] = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = [ordered]@{
    name     = 'CesiumLoader'
    version  = $version
    tag      = "modloader-$version"
    repo     = 'higashitaniyume/CesiumLoader'
    layout   = 'game-dir'
    built_at = (Get-Date).ToUniversalTime().ToString('o')
    files    = $fileMap
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -Path 'staging\cesium-loader.json' -Encoding UTF8

# ---------------------------------------------------------------- 6 SDK 工具包
Write-Step '6. 组装 SDK 工具包 (sdk-staging)'
if (Test-Path 'sdk-tools') { Remove-Item 'sdk-tools' -Recurse -Force }
dotnet publish 'tools\cesium\CesiumCli.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o sdk-tools --nologo
if ($LASTEXITCODE -ne 0) { throw 'cesium CLI 发布失败' }
if (-not (Test-Path 'sdk-tools\cesium.exe')) { throw 'cesium.exe 未生成' }

if (Test-Path 'sdk-staging') { Remove-Item 'sdk-staging' -Recurse -Force }
New-Item -ItemType Directory -Path 'sdk-staging\docs', 'sdk-staging\examples\ActivityLogMod' -Force | Out-Null
Copy-Item 'sdk-tools\cesium.exe' 'sdk-staging\cesium.exe' -Force
Copy-Item 'dist\modloader\AstralParty_ModLoader\sdk\CesiumLoader.SDK.dll' 'sdk-staging\CesiumLoader.SDK.dll' -Force
Copy-Item 'docs\*.md' 'sdk-staging\docs\' -Force
Copy-Item 'src\ActivityLogMod\ModEntry.cs', 'src\ActivityLogMod\AssemblyInfo.cs', 'src\ActivityLogMod\ActivityLogMod.csproj' 'sdk-staging\examples\ActivityLogMod\' -Force
$sdkReadme = @(
    "cesium SDK 工具包 $version",
    "",
    "包含:",
    "- cesium.exe            → mod 脚手架与包分发 CLI (win-x64 自包含, 无需本机 .NET)",
    "- CesiumLoader.SDK.dll  → mod 开发引用 (编译期绑定; cesium new 会自动附带进项目)",
    "- docs\                 → SDK 文档",
    "- examples\ActivityLogMod\ → 示例 mod 源码 (行为日志, 最简单)",
    "",
    "快速开始:",
    "  cesium new MyMod --author 你 --desc ""第一个mod""   # 生成项目模板(SDK 自动附带)",
    "  cesium build MyMod                                  # 构建 (需本机 dotnet SDK)",
    "  cesium package MyMod -o MyMod-1.0.0.zip             # 打包分发",
    "  cesium verify <mods_dir>                            # 离线预检依赖/版本兼容",
    "  cesium --help                                       # 全部命令与帮助"
) -join [Environment]::NewLine
Set-Content -Path 'sdk-staging\README.txt' -Value $sdkReadme -Encoding UTF8

# ---------------------------------------------------------------- 7 打包
Write-Step '7. 打包 zip'
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$loaderZip = Join-Path $OutputDir "cesium-loader-$version.zip"
$loaderLatest = Join-Path $OutputDir 'cesium-loader.zip'
$sdkZip = Join-Path $OutputDir "cesium-sdk-tools-$version.zip"
$sdkLatest = Join-Path $OutputDir 'cesium-sdk-tools.zip'
foreach ($z in @($loaderZip, $loaderLatest, $sdkZip, $sdkLatest)) { if (Test-Path $z) { Remove-Item $z -Force } }

Compress-Archive -Path 'staging\*' -DestinationPath $loaderZip -CompressionLevel Optimal
Copy-Item $loaderZip $loaderLatest -Force
Compress-Archive -Path 'sdk-staging\*' -DestinationPath $sdkZip -CompressionLevel Optimal
Copy-Item $sdkZip $sdkLatest -Force

Write-Host ''
Get-ChildItem $OutputDir -File | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}}, @{n='SHA256';e={(Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0,16)}} | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "打包完成: $OutputDir" -ForegroundColor Green
Write-Host "  清单版本: $((Get-Content 'staging\cesium-loader.json' -Raw | ConvertFrom-Json).version)"
Write-Host "  发布提示: 打 tag 后 CI 会用同一套步骤构建并发布 GitHub Release:"
Write-Host "    git tag modloader-$version && git push origin modloader-$version"
