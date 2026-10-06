# run-native-tests.ps1 - 原生(C++)侧单元测试运行器
#
# 编译并运行 tests\native\test_*.cpp。第三方库是 vendored 的, 不需要包管理器。

#requires -Version 7
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$nativeDir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $nativeDir)
$thirdParty = Join-Path $root 'third_party'
$includes = @(
    (Join-Path $root 'loader\CesiumLoader'),
    (Join-Path $thirdParty 'fmt\include'),
    (Join-Path $thirdParty 'nlohmann_json\include'),
    (Join-Path $thirdParty 'spdlog\include')
)
foreach ($p in $includes) {
    if (-not (Test-Path $p)) { throw "依赖缺失: $p" }
}

$vsDevCmd = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\Common7\Tools\VsDevCmd.bat' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $vsDevCmd) { throw '找不到 VsDevCmd.bat(需要 Visual Studio C++ 工具链)' }

$sources = Get-ChildItem $nativeDir -Filter 'test_*.cpp' | Sort-Object Name
if ($sources.Count -eq 0) { throw "在 $nativeDir 下没找到 test_*.cpp" }

$outDir = Join-Path $nativeDir 'out'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$failed = @()
foreach ($src in $sources) {
    $exe = Join-Path $outDir ($src.BaseName + '.exe')
    Write-Host "== 编译 $($src.Name)" -ForegroundColor Cyan
    $extraSrc = @()
    $sourcesFile = Join-Path $nativeDir ($src.BaseName + '.sources')
    if (Test-Path $sourcesFile) {
        $extraSrc = Get-Content $sourcesFile |
            Where-Object { $_.Trim() -ne '' -and -not $_.Trim().StartsWith('#') } |
            ForEach-Object { Join-Path $root $_.Trim() }
    }
    $srcArgs = (@($src.FullName) + $extraSrc | ForEach-Object { "`"$_`"" }) -join ' '
    $incArgs = ($includes | ForEach-Object { "/I`"$_`"" }) -join ' '
    $bat = Join-Path $outDir ($src.BaseName + '.build.cmd')
    $lines = @(
        '@echo off'
        "call `"$($vsDevCmd.FullName)`" -arch=x64 -host_arch=x64 -no_logo >nul 2>nul"
        "cl /nologo /utf-8 /std:c++17 /EHsc /W3 /MD /O2 $incArgs /DFMT_HEADER_ONLY /DSPDLOG_FMT_EXTERNAL /Fo:`"$outDir\\`" /Fe:`"$exe`" $srcArgs /link user32.lib"
        'if errorlevel 1 exit /b 1'
        "`"$exe`" `"$root`""
    )
    Set-Content -Path $bat -Value $lines -Encoding ascii
    & cmd.exe /c $bat
    if ($LASTEXITCODE -ne 0) { $failed += $src.Name; Write-Host "  失败" -ForegroundColor Red }
    else { Write-Host "  通过" -ForegroundColor Green }
}
if ($failed.Count -gt 0) { Write-Host "失败: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host "全部原生测试通过 ($($sources.Count) 个)" -ForegroundColor Green
