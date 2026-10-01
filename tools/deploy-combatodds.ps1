<#
.SYNOPSIS
    构建并部署"战斗胜率助手"(CombatOddsMod)到游戏的 CesiumLoader mods 目录, 便于游戏内测试。

.DESCRIPTION
    步骤:
      1. dotnet build mods\CombatOddsMod (Release)
      2. 校验游戏目录已装加载器(version.dll + AstralParty_ModLoader\)
      3. 复制 CombatOddsMod.dll + CombatOddsMod.json 到
         <游戏exe目录>\AstralParty_ModLoader\mods\CombatOddsMod\
    注意: 游戏目录多在 Program Files, 可能需要以管理员身份运行本脚本; 部署前请先退出游戏。

.PARAMETER GameDir
    游戏 exe 所在目录(含 AstralParty_CN.exe 与 version.dll)。默认国服 Steam 安装路径。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXn6CN"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot   # modding\msvc

Write-Host "== 构建 CombatOddsMod (Release) =="
dotnet build (Join-Path $repo 'mods\CombatOddsMod\CombatOddsMod.csproj') -c Release --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$dll = Join-Path $repo 'mods\CombatOddsMod\bin\Release\netstandard2.0\CombatOddsMod.dll'
$json = Join-Path $repo 'mods\CombatOddsMod\CombatOddsMod.json'
if (-not (Test-Path $dll)) { throw "找不到产物: $dll" }
if (-not (Test-Path $json)) { throw "找不到 sidecar: $json" }

if (-not (Test-Path (Join-Path $GameDir 'version.dll'))) {
    Write-Warning "游戏目录下没有 version.dll —— 似乎未安装 CesiumLoader 加载器。"
    Write-Warning "请先用 AstralParty.Toys 的『模组』页安装加载器, 或部署 dist\modloader。"
}
$loaderDir = Join-Path $GameDir 'AstralParty_ModLoader'
if (-not (Test-Path $loaderDir)) { throw "找不到加载器目录: $loaderDir (请先安装加载器)" }

# 游戏是否在运行(占用 DLL 会导致复制失败)。
$proc = Get-Process -Name 'AstralParty_CN' -ErrorAction SilentlyContinue
if ($proc) { Write-Warning "检测到游戏正在运行 —— 新 DLL 需重启游戏才加载, 且可能占用文件导致复制失败。" }

$modDir = Join-Path $loaderDir 'mods\CombatOddsMod'
New-Item -ItemType Directory -Path $modDir -Force | Out-Null
Copy-Item $dll  (Join-Path $modDir 'CombatOddsMod.dll')  -Force
Copy-Item $json (Join-Path $modDir 'CombatOddsMod.json') -Force

# 若 mod 用到了 SDK 里的新 API(如 Players.Roster), 部署里的 sdk\CesiumLoader.SDK.dll 也必须同步,
# 否则运行时会 MissingMethodException。构建 mod 时已连带构建 SDK, 这里把它一并覆盖过去。
$sdkDll = Join-Path $repo 'loader\CesiumLoader.SDK\bin\Release\netstandard2.0\CesiumLoader.SDK.dll'
$sdkDestDir = Join-Path $loaderDir 'sdk'
if ((Test-Path $sdkDll) -and (Test-Path $sdkDestDir)) {
    Copy-Item $sdkDll (Join-Path $sdkDestDir 'CesiumLoader.SDK.dll') -Force
    Write-Host "== 已同步 SDK: $sdkDestDir\CesiumLoader.SDK.dll =="
} else {
    Write-Warning "未同步 SDK(找不到 $sdkDll 或目标 $sdkDestDir); 若用到 SDK 新 API 可能运行时报错。"
}

Write-Host "== 已部署到: $modDir =="
Write-Host "启动游戏后进入一局 PVE 打怪, 战斗胜率会输出到加载器控制台(doorstop_config.json 里 console=true 可见)。"
