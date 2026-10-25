# build-harness.ps1 - 编译并运行调试 shell 的离线联调台
#
# 链接的是加载器自己的 console/shell/config 源码(不含 il2cpp/loader.cpp), 所以这里跑通的
# 提示符重绘、命令分发、配置写回与实机加载器里的行为是同一份代码。
#
# 运行: pwsh -File tests\native\build-harness.ps1

#requires -Version 7
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$nativeDir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $nativeDir)
$out = Join-Path $nativeDir 'out'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$loader = Join-Path $root 'loader\CesiumLoader'
$thirdParty = Join-Path $root 'third_party'
$includes = @(
    $loader,
    (Join-Path $thirdParty 'fmt\include'),
    (Join-Path $thirdParty 'nlohmann_json\include'),
    (Join-Path $thirdParty 'spdlog\include'),
    (Join-Path $thirdParty 'minhook\include')
)

$sources = @(
    (Join-Path $nativeDir 'harness_shell.cpp'),
    (Join-Path $loader 'platform\console.cpp'),
    (Join-Path $loader 'logging\logging.cpp'),
    (Join-Path $loader 'config\config.cpp'),
    (Join-Path $loader 'config\jsonc_patch.cpp'),
    (Join-Path $loader 'runtime\state.cpp'),
    (Join-Path $loader 'shell\command.cpp'),
    (Join-Path $loader 'shell\schema.cpp'),
    (Join-Path $loader 'shell\shell.cpp'),
    (Join-Path $loader 'speed\speedctl.cpp'),
    (Join-Path $loader 'speed\speedhack.cpp'),
    (Join-Path $loader 'window\windowctl.cpp'),
    (Join-Path $thirdParty 'minhook\src\buffer.c'),
    (Join-Path $thirdParty 'minhook\src\hook.c'),
    (Join-Path $thirdParty 'minhook\src\trampoline.c'),
    (Join-Path $thirdParty 'minhook\src\hde\hde32.c'),
    (Join-Path $thirdParty 'minhook\src\hde\hde64.c')
)

foreach ($s in $sources) { if (-not (Test-Path $s)) { throw "源码缺失: $s" } }

$vsDevCmd = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\Common7\Tools\VsDevCmd.bat' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $vsDevCmd) { throw '找不到 VsDevCmd.bat' }

# 联调台自己的"加载器目录": loader_root() = exe 目录\AstralParty_ModLoader
$modLoader = Join-Path $out 'AstralParty_ModLoader'
$modsDir = Join-Path $modLoader 'mods\TurnFocusMod'
New-Item -ItemType Directory -Force -Path $modsDir | Out-Null

$configPath = Join-Path $modLoader 'doorstop_config.json'
@'
{
  // 联调台用的配置(注释必须被 shell 写回时保留)
  "enabled": true,
  "consoleTopmost": false,
  "speedhackBaseSpeed": 2.0
}
'@ | Set-Content -Path $configPath -Encoding utf8

@'
{
  "enabled": true,
  "cooldownSeconds": 1,
  "soundFile": "C:\\tmp\\ding.wav",
  "alwaysPlaySound": false
}
'@ | Set-Content -Path (Join-Path $modsDir 'config.json') -Encoding utf8

$exe = Join-Path $out 'harness_shell.exe'
$bat = Join-Path $out 'build-harness.cmd'
$incArgs = ($includes | ForEach-Object { "/I`"$_`"" }) -join ' '
$srcArgs = ($sources | ForEach-Object { "`"$_`"" }) -join ' '

$lines = @(
    '@echo off'
    "call `"$($vsDevCmd.FullName)`" -arch=x64 -host_arch=x64 -no_logo >nul 2>nul"
    "cl /nologo /utf-8 /std:c++17 /EHsc /W3 /MD /O2 $incArgs /DFMT_HEADER_ONLY /DSPDLOG_FMT_EXTERNAL /Fo:`"$out\\`" /Fe:`"$exe`" $srcArgs /link user32.lib winmm.lib"
    'if errorlevel 1 exit /b 1'
)
Set-Content -Path $bat -Value $lines -Encoding ascii
& cmd.exe /c $bat
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

Write-Host '== 运行联调台(会短暂弹出一个控制台窗口)' -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -PassThru -Wait
Write-Host "退出码: $($p.ExitCode)"

$screen = Join-Path $out 'shell-screen.txt'
if (Test-Path $screen) {
    Write-Host "== 屏幕抓取: $screen" -ForegroundColor Green
    Get-Content $screen -Encoding utf8 | Select-Object -Last 80
} else {
    Write-Host '没有生成 shell-screen.txt' -ForegroundColor Red
}
