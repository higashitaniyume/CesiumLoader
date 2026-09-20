<#
.SYNOPSIS
    把 CesiumLoader 部署到 Astral Party 游戏目录 (version.dll 代理 + AstralParty_ModLoader 布局)。

.DESCRIPTION
    纯部署脚本: 只复制已构建的产物, 不做 git 操作、不改动游戏自身文件、不碰 BepInEx。
    - 原生加载器 version.dll 必须位于游戏 exe 同目录 (Doorstop 式代理)。
    - SDK / mods / 配置放在游戏目录下的 AstralParty_ModLoader\。
    - 游戏实际 exe 目录可能是 Steam 安装目录下的混淆子目录 (如 8vJXn6CN),
      用 -GameDir 指向"含 AstralParty_CN.exe 的目录"。

.PARAMETER GameDir
    游戏 exe 所在目录。默认自动探测 Steam 安装位置。

.PARAMETER Configuration
    使用哪个构建配置的产物 (默认 Release)。

.PARAMETER Build
    部署前先构建解决方案 (MSBuild Release x64)。

.PARAMETER Mods
    要部署的 mod 名 (目录名), 默认全部示例 mod。

.PARAMETER Launch
    部署完成后通过 Steam 启动游戏 (steam://rungameid/<AppId>)。

    注意: **必须由 Steam 拉起游戏**。本作在 SteamManager.Awake() 里校验启动来源,
    直接运行 AstralParty_CN.exe 会得到 "[ERROR] [SteamManager] 非Steam客户端启动,
    退出游戏" 并主动退出 (Player.log 中 SteamPlatform Initialized: False)。
    另外请确保同一时刻只有一个游戏实例: 多实例会各注入一次 version.dll,
    在共享的 logs\cesium-loader.log 里留下两段引导记录。

.EXAMPLE
    pwsh -File tools\deploy.ps1
    pwsh -File tools\deploy.ps1 -GameDir 'D:\Steam\steamapps\common\Astral Party\8vJXn6CN' -Build
    pwsh -File tools\deploy.ps1 -Launch
#>
[CmdletBinding()]
param(
    [string] $GameDir,
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $Build,
    [switch] $Launch,
    [int] $SteamAppId = 2622000,
    [string[]] $Mods = @('ActivityLogMod', 'CameraProbeMod', 'DiagnosticsMod', 'FreeCameraMod')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot          # modding\msvc
$src = Join-Path $repo 'src'

# ---------------------------------------------------------------- 定位游戏目录
function Resolve-GameDir
{
    param([string] $Explicit)

    if ($Explicit)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $Explicit 'AstralParty_CN.exe')))
        {
            throw "指定的 -GameDir 下没有 AstralParty_CN.exe: $Explicit"
        }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $roots = @()
    foreach ($lib in @('C:\Program Files (x86)\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary'))
    {
        # 注意: 不存在的盘符上 Join-Path 会抛异常, 这里改为纯字符串拼接后再探测
        $common = $lib + '\steamapps\common\Astral Party'
        if (Test-Path -LiteralPath $common) { $roots += $common }
    }

    foreach ($root in $roots)
    {
        # 游戏 exe 可能位于混淆子目录下
        $exe = Get-ChildItem -LiteralPath $root -Filter 'AstralParty_CN.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($exe) { return $exe.DirectoryName }
    }

    throw "未能自动定位游戏目录, 请用 -GameDir 指定 (含 AstralParty_CN.exe 的目录)。"
}

$gameDir = Resolve-GameDir -Explicit $GameDir
$loaderDir = Join-Path $gameDir 'AstralParty_ModLoader'

Write-Host "游戏目录 : $gameDir"
Write-Host "配置     : $Configuration"

if (Get-Process -Name 'AstralParty_CN' -ErrorAction SilentlyContinue)
{
    throw '游戏正在运行, 请先退出再部署 (version.dll 被占用时无法覆盖)。'
}

# ---------------------------------------------------------------- 构建 (可选)
if ($Build)
{
    $msbuild = @(
        'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $msbuild) { throw '未找到 MSBuild.exe, 请手动构建后再部署。' }

    Write-Host "构建中   : $msbuild"
    & $msbuild (Join-Path $repo 'CesiumLoader.sln') /p:Configuration=$Configuration /p:Platform=x64 /m /v:m /nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败 (exit $LASTEXITCODE)" }
}

# ---------------------------------------------------------------- 收集产物
$manifest = @()

function Deploy-File
{
    param([string] $From, [string] $To, [switch] $Optional)

    if (-not (Test-Path -LiteralPath $From))
    {
        if ($Optional) { Write-Warning "跳过 (不存在): $From"; return }
        throw "缺少构建产物: $From"
    }

    $dir = Split-Path -Parent $To
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    Copy-Item -LiteralPath $From -Destination $To -Force
    $hash = (Get-FileHash -LiteralPath $To -Algorithm SHA256).Hash.Substring(0, 16)
    $len = (Get-Item -LiteralPath $To).Length
    $script:manifest += [pscustomobject]@{
        File   = $To.Substring($gameDir.Length).TrimStart('\')
        Bytes  = $len
        SHA256 = $hash
    }
}

$netstd = "bin\$Configuration\netstandard2.0"

# 1) 原生加载器 -> 游戏 exe 同目录
if (Test-Path -LiteralPath (Join-Path $gameDir 'version.dll'))
{
    $backup = Join-Path $gameDir 'version.dll.bak'
    Copy-Item -LiteralPath (Join-Path $gameDir 'version.dll') -Destination $backup -Force
    Write-Host "已备份原有 version.dll -> version.dll.bak"
}
Deploy-File -From (Join-Path $repo "bin\$Configuration\version.dll") -To (Join-Path $gameDir 'version.dll')

# 2) 加载器配置
Deploy-File -From (Join-Path $repo 'dist\modloader\AstralParty_ModLoader\doorstop_config.json') `
            -To (Join-Path $loaderDir 'doorstop_config.json')

# 3) SDK (加载器从 sdk\*.dll 载入)
Deploy-File -From (Join-Path $src "CesiumLoader.SDK\$netstd\CesiumLoader.SDK.dll") `
            -To (Join-Path $loaderDir 'sdk\CesiumLoader.SDK.dll')

# 4) 托管 Bootstrap (默认不启用, 但按布局就位)
Deploy-File -From (Join-Path $src "CesiumLoader.Bootstrap\$netstd\CesiumLoader.Bootstrap.dll") `
            -To (Join-Path $loaderDir 'bootstrap\CesiumLoader.Bootstrap.dll') -Optional

# 5) mods: 每个 mod 一个文件夹 (DLL + 同名 sidecar)
foreach ($mod in $Mods)
{
    $from = Join-Path $src "$mod\$netstd"
    if (-not (Test-Path -LiteralPath $from)) { Write-Warning "跳过 mod (未构建): $mod"; continue }

    $dll = Join-Path $from "$mod.dll"
    Deploy-File -From $dll -To (Join-Path $loaderDir "mods\$mod\$mod.dll")

    $json = Join-Path $from "$mod.json"
    if (-not (Test-Path -LiteralPath $json)) { $json = Join-Path $repo "dist\modloader\AstralParty_ModLoader\mods\$mod\$mod.json" }
    Deploy-File -From $json -To (Join-Path $loaderDir "mods\$mod\$mod.json") -Optional
}

# 6) 日志目录
foreach ($d in @('logs')) { $p = Join-Path $loaderDir $d; if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }

# ---------------------------------------------------------------- 汇总
Write-Host ''
Write-Host '已部署:' -ForegroundColor Green
$manifest | Format-Table -AutoSize

# BepInEx 说明: 该目录下的 BepInEx (winhttp.dll Doorstop) 是随游戏安装就存在的既有环境,
# 与 CesiumLoader 的 version.dll 是两个互不相干的代理, 实测可共存。
# 因此本脚本只提示、不做任何改动 —— 不碰游戏目录里的任何既有文件。
$bep = Join-Path $gameDir 'BepInEx\core\BepInEx.Preloader.dll'
if (Test-Path -LiteralPath $bep)
{
    Write-Host '提示: 游戏目录存在 BepInEx (winhttp.dll Doorstop), 属既有环境, 本脚本不做改动。' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host "日志: $loaderDir\logs\cesium-loader.log" -ForegroundColor Cyan

# ---------------------------------------------------------------- 启动 (可选)
if ($Launch)
{
    # 单实例原则: 同一时刻只允许一个游戏进程。两个进程会各自注入一次 version.dll,
    # 共享同一份 cesium-loader.log(日志交错), 并重复加载 mod。
    if (Get-Process -Name 'AstralParty_CN' -ErrorAction SilentlyContinue)
    {
        throw '已有游戏实例在运行, 拒绝再次启动 (避免多实例重复注入)。'
    }

    if (-not (Get-Process -Name 'steam' -ErrorAction SilentlyContinue))
    {
        throw 'Steam 未运行。本作必须由 Steam 拉起, 否则游戏会检测到"非Steam客户端启动"并主动退出。'
    }

    Write-Host "通过 Steam 启动 (appid $SteamAppId)..." -ForegroundColor Cyan
    Start-Process "steam://rungameid/$SteamAppId"
    Write-Host '  注意: 切勿用 Start-Process 直接运行 AstralParty_CN.exe —— 游戏会立即退出。' -ForegroundColor Yellow
}
