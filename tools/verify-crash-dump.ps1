<#
.SYNOPSIS
    检查加载器的崩溃转储是否正常产生（配合 mods\CrashProbeMod 使用）。

.DESCRIPTION
    在 <游戏目录>\AstralParty_ModLoader\logs\crash\ 下找最新的 .dmp，逐项校验：
      1. 目录与文件存在、大小合理
      2. 文件头是 minidump 的 MDMP 签名
      3. 有配套的同名 .txt 说明
      4. 说明里有异常摘要与加载器环境信息（[environment]/[binaries]/[config]/[mods]）
      5. 同一次启动的加载器日志里出现过 "[crash] minidump handler installed"
         —— 证明这次崩溃前崩溃处理器确实装上了
    另外会顺带看一眼同会话的 activity 日志里有没有 CrashProbeMod 的触发记录。

.PARAMETER GameDir
    含 AstralParty_CN.exe 的目录。默认自动探测 Steam 安装位置。

.PARAMETER Dump
    指定某个 .dmp（或它的会话名）来检查，而不是自动取最新的。

.EXAMPLE
    pwsh -NoProfile -File tools\verify-crash-dump.ps1

.EXAMPLE
    pwsh -NoProfile -File tools\verify-crash-dump.ps1 -Dump 20261114-151142-27772
#>
[CmdletBinding()]
param(
    [string] $GameDir,
    [string] $Dump
)

$ErrorActionPreference = 'Stop'

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

    foreach ($lib in @('C:\Program Files (x86)\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary'))
    {
        $common = $lib + '\steamapps\common\Astral Party'
        if (-not (Test-Path -LiteralPath $common)) { continue }
        $exe = Get-ChildItem -LiteralPath $common -Filter 'AstralParty_CN.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($exe) { return $exe.DirectoryName }
    }

    throw '未能自动定位游戏目录，请用 -GameDir 指定 (含 AstralParty_CN.exe 的目录)。'
}

$gameDir = Resolve-GameDir -Explicit $GameDir
$loaderDir = Join-Path $gameDir 'AstralParty_ModLoader'
$logsDir = Join-Path $loaderDir 'logs'
$crashDir = Join-Path $logsDir 'crash'

Write-Host "游戏目录 : $gameDir"
Write-Host "转储目录 : $crashDir"
Write-Host ''

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string] $Step, [bool] $Ok, [string] $Detail)
{
    $script:results.Add([pscustomobject]@{ Step = $Step; Ok = $Ok; Detail = $Detail })
    $tag = if ($Ok) { '[PASS]' } else { '[FAIL]' }
    $color = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host ("{0,-6} {1,-22} {2}" -f $tag, $Step, $Detail) -ForegroundColor $color
}

# ---------------------------------------------------------------- 1. 找 dump
$dmpPath = $null
if ($Dump)
{
    $candidate = if ($Dump.EndsWith('.dmp', [StringComparison]::OrdinalIgnoreCase)) { $Dump } else { Join-Path $crashDir ($Dump + '.dmp') }
    if (Test-Path -LiteralPath $candidate) { $dmpPath = (Resolve-Path -LiteralPath $candidate).Path }
}
elseif (Test-Path -LiteralPath $crashDir)
{
    $dmpPath = Get-ChildItem -LiteralPath $crashDir -Filter '*.dmp' -File -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1 -ExpandProperty FullName
}

$dmpCount = 0
if (Test-Path -LiteralPath $crashDir)
{
    $dmpCount = @(Get-ChildItem -LiteralPath $crashDir -Filter '*.dmp' -File -ErrorAction SilentlyContinue).Count
}

if (-not $dmpPath)
{
    Add-Result '转储文件' $false "没有找到 .dmp (目录里现有 $dmpCount 个)"
    Write-Host ''
    Write-Host '还没有崩溃转储。可能原因：' -ForegroundColor Yellow
    Write-Host '  - 游戏还没崩过（打开设置页触发 CrashProbeMod，或等真实崩溃）'
    Write-Host '  - 游戏卡死过但看门狗没开（nativeWatchdogEnabled=false）'
    Write-Host '  - doorstop_config.json 里 crashDumpEnabled=false'
    Write-Host '  - 崩溃发生在加载器安装异常处理器之前（启动最初约 2 秒）'
    exit 1
}

$dmp = Get-Item -LiteralPath $dmpPath
# 文件名 = <kind>-<会话>；崩溃转储是 <会话>，按需转储带 hang-/manual- 前缀。
$session = $dmp.BaseName
$isHang = $false
if ($session -match '^(hang|manual)-(.+)$')
{
    $isHang = $Matches[1] -eq 'hang'
    $session = $Matches[2]
}
Add-Result '转储文件' $true ("{0} · {1:N0} B · {2:yyyy-MM-dd HH:mm:ss}（目录内共 {3} 个）" -f $dmp.Name, $dmp.Length, $dmp.LastWriteTime, $dmpCount)
Write-Host ("       类型: " + $(if ($isHang) { 'hang dump（卡死，由看门狗主动落盘）' } else { 'crash dump（异常触发）' })) -ForegroundColor DarkGray

# ---------------------------------------------------------------- 2. MDMP 签名
$signatureOk = $false
$signatureText = '(读取失败)'
try
{
    $fs = [System.IO.File]::Open($dmp.FullName, 'Open', 'Read', 'ReadWrite')
    try
    {
        $buffer = New-Object byte[] 4
        $read = $fs.Read($buffer, 0, 4)
        if ($read -eq 4)
        {
            $signatureText = [System.Text.Encoding]::ASCII.GetString($buffer)
            $signatureOk = ($signatureText -eq 'MDMP')
        }
    }
    finally { $fs.Dispose() }
}
catch
{
    $signatureText = $_.Exception.Message
}
Add-Result 'minidump 签名' $signatureOk "文件头 = '$signatureText' (应为 MDMP)"

# ---------------------------------------------------------------- 3. 配套说明文件
$notePath = [System.IO.Path]::ChangeExtension($dmp.FullName, '.txt')
$noteText = $null
$noteOk = Test-Path -LiteralPath $notePath
if ($noteOk)
{
    $noteText = Get-Content -LiteralPath $notePath -Raw
    $noteSize = (Get-Item -LiteralPath $notePath).Length
    Add-Result '配套说明文件' $true ("{0} · {1:N0} B" -f (Split-Path -Leaf $notePath), $noteSize)
}
else
{
    Add-Result '配套说明文件' $false "缺少 $([System.IO.Path]::GetFileName($notePath))"
}

# ---------------------------------------------------------------- 4. 说明内容
if ($noteOk)
{
    $sections = if ($isHang)
    {
        @(
            @{ Name = '卡死摘要';   Pattern = 'CesiumLoader hang dump' },
            @{ Name = '请求信息';   Pattern = '\[request\]' },
            @{ Name = '线程列表';   Pattern = '\[threads\]' },
            @{ Name = '环境段';     Pattern = 'CesiumLoader environment' },
            @{ Name = '二进制指纹'; Pattern = '\[binaries\]' },
            @{ Name = '配置段';     Pattern = '\[config\]' }
        )
    }
    else
    {
        @(
            @{ Name = '异常摘要';   Pattern = 'CesiumLoader crash report' },
            @{ Name = '异常码';     Pattern = 'exception\s*:\s*0x[0-9A-Fa-f]{8}' },
            @{ Name = '环境段';     Pattern = 'CesiumLoader environment' },
            @{ Name = '二进制指纹'; Pattern = '\[binaries\]' },
            @{ Name = '配置段';     Pattern = '\[config\]' },
            @{ Name = '模组段';     Pattern = '\[mods\]' }
        )
    }

    $missing = @()
    foreach ($section in $sections)
    {
        if ($noteText -notmatch $section.Pattern) { $missing += $section.Name }
    }

    $expect = if ($isHang) { '卡死摘要 + [request]/[threads] + 环境信息' } else { '异常摘要 + [environment]/[binaries]/[config]/[mods]' }
    Add-Result '说明内容完整' ($missing.Count -eq 0) $(if ($missing.Count -eq 0) { $expect } else { '缺少: ' + ($missing -join ', ') })
}

# ---------------------------------------------------------------- 5. 同会话加载器日志
$loaderLog = Join-Path $logsDir ("cesium-loader-" + $session + ".log")
if (Test-Path -LiteralPath $loaderLog)
{
    if ($isHang)
    {
        $hit = Select-String -LiteralPath $loaderLog -Pattern '\[watchdog\] hang dump written' | Select-Object -First 1
        if (-not $hit) { $hit = Select-String -LiteralPath $loaderLog -Pattern '\[watchdog\] enabled' | Select-Object -First 1 }
        $ok = $null -ne $hit
        Add-Result '同会话加载器日志' $ok $(if ($ok) { '找到看门狗记录' } else { '日志里没有看门狗记录' })
    }
    else
    {
        $hit = Select-String -LiteralPath $loaderLog -Pattern '\[crash\] minidump handler installed' | Select-Object -First 1
        $ok = $null -ne $hit
        Add-Result '同会话加载器日志' $ok $(if ($ok) { '找到 "[crash] minidump handler installed"' } else { '日志里没有崩溃处理器安装记录' })
    }
}
else
{
    Add-Result '同会话加载器日志' $false ("缺少 cesium-loader-$session.log（会话对不上，可能日志被清理）")
}

# ---------------------------------------------------------------- 附加信息：模组触发记录
$activityLog = Join-Path $logsDir ("activity-mod-" + $session + ".log")
if (Test-Path -LiteralPath $activityLog)
{
    $armed = Select-String -LiteralPath $activityLog -Pattern '已武装|正在触发崩溃' | Select-Object -Last 2
    if ($armed)
    {
        Write-Host ''
        Write-Host '模组触发记录:' -ForegroundColor Cyan
        foreach ($line in $armed) { Write-Host ('  ' + $line.Line.Trim()) }
    }
}

# ---------------------------------------------------------------- 摘要输出
Write-Host ''
if ($noteOk)
{
    Write-Host '--- 说明文件开头 ---' -ForegroundColor Cyan
    $head = ($noteText -split "`r?`n") | Select-Object -First 12
    foreach ($line in $head) { Write-Host ('  ' + $line) }

    $envLines = ($noteText -split "`r?`n") | Where-Object { $_ -match '^(loader|game|os|exe|root|crash dir|reason|source|stall|main tid|rip)\s*:' }
    if ($envLines)
    {
        Write-Host ''
        Write-Host '--- 环境摘要 ---' -ForegroundColor Cyan
        foreach ($line in $envLines) { Write-Host ('  ' + $line) }
    }

    if ($isHang)
    {
        $threadLines = ($noteText -split "`r?`n") | Where-Object { $_ -match '^\s+tid ' } | Select-Object -First 8
        if ($threadLines)
        {
            Write-Host ''
            Write-Host '--- 线程（前 8 条，主线程标 main） ---' -ForegroundColor Cyan
            foreach ($line in $threadLines) { Write-Host ('  ' + $line.Trim()) }
            Write-Host '  用 cdb 逐线程看栈: cdb -z <dmp> -y "<游戏目录>" -c "~*k; q"' -ForegroundColor DarkGray
        }
    }
}

$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ''
if ($failed.Count -eq 0)
{
    Write-Host ("结论: PASS（{0}/{1} 项）— 崩溃转储通路正常" -f $results.Count, $results.Count) -ForegroundColor Green
    Write-Host "转储: $($dmp.FullName)"
    exit 0
}

Write-Host ("结论: FAIL（{0}/{1} 项未通过）" -f $failed.Count, $results.Count) -ForegroundColor Red
foreach ($item in $failed) { Write-Host ("  - {0}: {1}" -f $item.Step, $item.Detail) -ForegroundColor Yellow }
exit 1
