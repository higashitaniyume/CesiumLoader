# smoke-speedctl.ps1 - 变速控制文件通道冒烟测试(不依赖游戏)
#
# 做什么:
#   1. 构建原生加载器 (Release x64) 与冒烟宿主 smoke\SpeedCtlSmoke;
#   2. 在宿主的输出目录造出与游戏一致的布局: <宿主目录>\AstralParty_ModLoader\
#      (doorstop_config.json + speed\ + logs\), 并把 version.dll 放到宿主 exe 旁边;
#   3. 运行宿主: 它触发加载器引导线程, 然后像 mod 一样读写 speed\request.txt / state.txt,
#      校验倍率是否真的被应用、非法请求是否被忽略。
#
# 为什么可以离线测: 加载器的控制文件通道在"等 GameAssembly.dll"之前就已就绪,
# 宿主的等待会超时(配置成 1 秒), 但不影响通道。
#
# 用法: pwsh -NoProfile -File tools\smoke-speedctl.ps1

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try
{
    $msbuild = $null
    foreach ($probe in (Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe" -ErrorAction SilentlyContinue))
    {
        $msbuild = $probe.FullName
        break
    }
    if (-not $msbuild) { throw '找不到 MSBuild.exe (需要 Visual Studio + C++ 生成工具)' }

    if (-not $SkipBuild)
    {
        Write-Host '[smoke] 1/3 构建原生加载器 + 宿主'
        & $msbuild 'src\CesiumLoader\CesiumLoader.vcxproj' /p:Configuration=$Configuration /p:Platform=x64 /m /nologo /v:minimal
        if ($LASTEXITCODE -ne 0) { throw "原生加载器构建失败 (exit $LASTEXITCODE)" }
        dotnet build 'smoke\SpeedCtlSmoke\SpeedCtlSmoke.csproj' -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw '冒烟宿主构建失败' }
    }

    $nativeDll = "src\CesiumLoader\bin\$Configuration\version.dll"
    if (-not (Test-Path $nativeDll)) { throw "缺少 $nativeDll" }

    # 宿主输出目录: bin\<Configuration>\net8.0
    $hostDir = Get-ChildItem 'smoke\SpeedCtlSmoke\bin' -Recurse -Filter 'SpeedCtlSmoke.exe' |
        Where-Object { $_.FullName -match "\\$Configuration\\" } |
        Select-Object -First 1
    if (-not $hostDir) { throw '找不到冒烟宿主可执行文件' }
    $hostDir = $hostDir.Directory.FullName

    Write-Host '[smoke] 2/3 布置加载器目录布局'
    $loaderDir = Join-Path $hostDir 'AstralParty_ModLoader'
    foreach ($sub in 'speed', 'logs', 'sdk') { New-Item -ItemType Directory -Path (Join-Path $loaderDir $sub) -Force | Out-Null }
    Copy-Item $nativeDll (Join-Path $hostDir 'version.dll') -Force

    # 真实的 SDK(宿主要反射加载它, 端到端验证 mod 侧路径)
    $sdkDll = "src\CesiumLoader.SDK\bin\$Configuration\netstandard2.0\CesiumLoader.SDK.dll"
    if (-not (Test-Path $sdkDll)) { throw "缺少 $sdkDll (先构建 SDK)" }
    Copy-Item $sdkDll (Join-Path $loaderDir 'sdk\CesiumLoader.SDK.dll') -Force

    # 沙箱 %LOCALAPPDATA%: 加载器会往 <LOCALAPPDATA>\AstralParty_ModLoader\speed 镜像写一份,
    # 不让测试污染真实用户目录。
    $sandboxLocal = Join-Path $hostDir 'sandbox-localappdata'
    if (Test-Path $sandboxLocal) { Remove-Item $sandboxLocal -Recurse -Force }
    New-Item -ItemType Directory -Path $sandboxLocal -Force | Out-Null
    $env:LOCALAPPDATA = $sandboxLocal
    Write-Host "[smoke] 沙箱 LOCALAPPDATA: $sandboxLocal"

    # 宿主进程的 exe 目录就是 loader_root(); 超时都设 1 秒, 让引导线程尽快走到"放弃"分支。
    $config = @'
{
  "enabled": true,
  "consoleEnabled": false,
  "consoleTopmost": false,
  "forwardActivityLog": true,
  "gameAssemblyTimeoutSec": 1,
  "domainTimeoutSec": 1,
  "hybridclrTimeoutSec": 1,
  "speedhackBaseSpeed": 2,
  "speedControlEnabled": true,
  "sdkVersion": "2.2.1"
}
'@
    Set-Content -Path (Join-Path $loaderDir 'doorstop_config.json') -Value $config -Encoding UTF8
    Remove-Item (Join-Path $loaderDir 'speed\*.txt') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $loaderDir 'logs\cesium-loader.log') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $sandboxLocal 'AstralParty_ModLoader') -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host '[smoke] 3/3 运行'
    & (Join-Path $hostDir 'SpeedCtlSmoke.exe')
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "变速控制文件通道冒烟测试失败 (exit $code)" }

    # 加载器日志要等宿主退出才读得到(spdlog 的 sink 独占持有到进程结束), 所以在这里打印
    $logFile = Join-Path $loaderDir 'logs\cesium-loader.log'
    if (Test-Path $logFile) {
      Write-Host '[smoke] --- 加载器日志 ---'
      Select-String -Path $logFile -Pattern 'speedctl|speedhack|倍率|基础' -Encoding UTF8 |
        ForEach-Object { Write-Host "        $($_.Line.TrimEnd())" }
    }

    Write-Host "[smoke] 通过 (version.dll: $((Get-Item $nativeDll).Length) bytes)"
}
finally
{
    Pop-Location
}
