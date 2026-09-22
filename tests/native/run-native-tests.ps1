# run-native-tests.ps1 - 原生(C++)侧单元测试运行器
#
# 编译并运行 tests\native\test_*.cpp。第三方库是 vendored 的(源码在 ..\..\third_party,
# 见该目录的 README.md), 不需要包管理器 —— 用 VsDevCmd 初始化 MSVC 环境后直接调 cl。
#
# 用法: powershell -File tests\native\run-native-tests.ps1

#requires -Version 7
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$nativeDir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $nativeDir)   # modding\msvc
$thirdParty = Join-Path $root 'third_party'
# vendored 头文件(与主工程 vcxproj 的 AdditionalIncludeDirectories 保持一致)
$includes = @('fmt\include', 'nlohmann_json\include', 'spdlog\include') |
    ForEach-Object { Join-Path $thirdParty $_ }
foreach ($p in $includes) {
    if (-not (Test-Path $p)) { throw "vendored 依赖缺失: $p`n(third_party 应随仓库一起提交)" }
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

    # 每个用例可在同名 .sources 文件里声明额外要编译的源文件(相对仓根, 每行一个),
    # 用来把被测的真实实现(如 config.cpp / modmeta.cpp)链接进来。
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
    # /utf-8 是必须的: fmt 在 MSVC 上有 "Unicode support requires compiling with /utf-8"
    # 静态断言, 且源码里的中文注释在默认 936 代码页下会被破坏。主工程 AdditionalOptions 里
    # 也是这么设的, 测试保持一致。
    # FMT_HEADER_ONLY / SPDLOG_FMT_EXTERNAL 也必须与主工程一致: 同一个 exe 里的所有 TU
    # (含被链接进来的 logging.cpp) 都要按同一种方式使用 fmt, 否则就是 ODR 冲突。
    $lines = @(
        '@echo off'
        "call `"$($vsDevCmd.FullName)`" -arch=x64 -host_arch=x64 -no_logo >nul 2>nul"
        "cl /nologo /utf-8 /std:c++17 /EHsc /W3 /MD /O2 $incArgs /DFMT_HEADER_ONLY /DSPDLOG_FMT_EXTERNAL /Fo:`"$outDir\\`" /Fe:`"$exe`" $srcArgs /link user32.lib"
        'if errorlevel 1 exit /b 1'
        "`"$exe`" `"$root`""
    )
    Set-Content -Path $bat -Value $lines -Encoding ascii

    & cmd.exe /c $bat
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $failed += $src.Name
        Write-Host "  失败 (退出码 $code)" -ForegroundColor Red
    }
    else {
        Write-Host "  通过" -ForegroundColor Green
    }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host "失败: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "全部原生测试通过 ($($sources.Count) 个)" -ForegroundColor Green
