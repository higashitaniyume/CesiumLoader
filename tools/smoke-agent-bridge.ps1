<#
.SYNOPSIS
  AI 接管桥接的离线端到端冒烟: 真的 AstralParty.Mcp.exe + 假游戏。

.DESCRIPTION
  不需要游戏、不需要加载器、不需要对局 —— 用一个临时桥接目录扮演"游戏内 mod",
  然后拿**真正发布出来的 MCP server** 走一遍完整 stdio 会话:

    initialize → tools/list → astral_status/state/pending(读假状态)
      → astral_move(真写命令文件) → 假游戏回执 → 工具返回 ok
      → astral_control / astral_emergency_stop / astral_resume(真写 control.json)
      → 心跳过期后动作工具必须拒绝下发

  覆盖的是"文件契约 + JSON-RPC 协议"这两层最容易在真机上翻车的地方:
    - 桥接目录解析(环境变量 CESIUM_AGENT_DIR)
    - state.json / bridge.json 的字段名与大小写
    - 命令文件名 {seq:D8}-{id}.json、结果文件名 {id}.json、两侧文件都要被清掉
    - Pending.Kind 是游戏侧的小写 camelCase 常量('none'/'move'/'selectRelic'…), 且两侧大小写不敏感
    - 心跳过期时 server 必须拒绝下发命令(并且不留下垃圾命令文件)

  跑法: pwsh -NoProfile -File tools\smoke-agent-bridge.ps1
  成功时最后一行是 [smoke] === 全部通过 ===。
  失败时列出每条断言, 并以退出码 1 结束。

.PARAMETER Exe
  指定 AstralParty.Mcp.exe。默认取仓库里已发布的 framework-dependent 产物;
  不存在就现场 dotnet publish 一份到临时目录。

.PARAMETER Keep
  保留临时桥接目录(排查用), 并在结尾打印路径。

.NOTES
  server 的日志走 stderr, 会直接混在这份输出里(前缀一般是 MCP 自己的日志),
  断言只写在 stdout —— 这也是为什么这个脚本不重定向子进程的 stderr(避免管道写满死锁)。
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# ============================== 断言小工具 ==============================

$script:Passed = 0
$script:Failed = New-Object System.Collections.Generic.List[string]

function Assert-That {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        $script:Passed++
        Write-Host "  [ok]   $Name" -ForegroundColor DarkGreen
    } else {
        $script:Failed.Add(($Name + $(if ($Detail) { "  ($Detail)" } else { '' })))
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor DarkRed }
    }
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Text)
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# 时间戳必须与 AgentBridgeLayout.NowMs() 同一个纪元: 它是 DateTime.UtcNow.Ticks / 10000,
# 也就是"自 0001-01-01 起的毫秒", **不是** Unix 毫秒([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())。
# 用错纪元会让心跳年龄算出天文数字, server 会判成"没有心跳"。
function Get-BridgeNowMs {
    return [long]([DateTime]::UtcNow.Ticks / [TimeSpan]::TicksPerMillisecond)
}

# ============================== 准备桥接目录 ==============================

$bridge = Join-Path ([System.IO.Path]::GetTempPath()) ('astral-agent-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$commands = Join-Path $bridge 'commands'
$results = Join-Path $bridge 'results'
New-Item -ItemType Directory -Path $bridge, $commands, $results -Force | Out-Null

Write-Host '[smoke] 桥接目录: ' -NoNewline
Write-Host $bridge -ForegroundColor Cyan

# ============================== 找 server ==============================

$published = Join-Path $repo 'mcp\AstralParty.Mcp\bin\Release\net8.0\win-x64\publish\AstralParty.Mcp.exe'
if (-not $Exe) { $Exe = $published }
if (-not (Test-Path $Exe)) {
    Write-Host '[smoke] 没找到已发布的 server, 现场 publish(framework-dependent)...'
    $out = Join-Path $bridge '_publish'
    & dotnet publish (Join-Path $repo 'mcp\AstralParty.Mcp\AstralParty.Mcp.csproj') -c Release -r win-x64 `
        --self-contained false -o $out --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'AstralParty.Mcp 发布失败' }
    $Exe = Join-Path $out 'AstralParty.Mcp.exe'
}
if (-not (Test-Path $Exe)) { throw "找不到 MCP server: $Exe" }
Write-Host "[smoke] server: $Exe"

# ============================== 假游戏(mod 的替身) ==============================

$fakeGame = Start-Job -ArgumentList $bridge -ScriptBlock {
    param($bridge)
    $commands = Join-Path $bridge 'commands'
    $results = Join-Path $bridge 'results'
    $log = Join-Path $bridge 'fakegame.log'

    function Write-Utf8NoBom {
        param([string]$Path, [string]$Text)
        [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
    }

    Write-Utf8NoBom -Path (Join-Path $bridge 'fakegame.ready') -Text 'ready'

    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path (Join-Path $bridge 'fakegame.stop')) { break }
        $files = @(Get-ChildItem -Path $commands -Filter '*.json' -ErrorAction SilentlyContinue | Sort-Object Name)
        foreach ($f in $files) {
            # 与 AgentBridgeLayout.CommandFileName / ResultPath 同一套命名
            $id = ($f.Name -replace '^\d{8}-', '') -replace '\.json$', ''
            $body = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
            Add-Content -LiteralPath $log -Value $body -Encoding UTF8
            $result = '{"Schema":1,"Id":"' + $id + '","Ok":true,"Code":"ok","Detail":"已发送(假游戏)","LatencyMs":3}'
            Write-Utf8NoBom -Path (Join-Path $results ($id + '.json')) -Text $result
            Remove-Item -LiteralPath $f.FullName -Force
        }
        Start-Sleep -Milliseconds 10
    }
}

# 等假游戏就绪
$ready = Join-Path $bridge 'fakegame.ready'
for ($i = 0; $i -lt 200 -and -not (Test-Path $ready); $i++) { Start-Sleep -Milliseconds 50 }
if (-not (Test-Path $ready)) { throw '假游戏没能启动' }

# ============================== 假状态(字段名必须与 mod 写的一致) ==============================

function Write-Heartbeat {
    param([int]$AgeMs = 120)
    $now = Get-BridgeNowMs
    $hb = [ordered]@{
        Schema = 1; ModVersion = '1.0.0'; SdkVersion = '2.2.4'; ProcessId = 4242
        StartedAtMs = $now - 60000; LastTickMs = $now - $AgeMs; TickCount = 999; StateSeq = 42
        AgentDir = $bridge; Scene = 'RoomScene'; InRoom = $true; InBattle = $false
        CommandsExecuted = 0; CommandsRejected = 0
    }
    Write-Utf8NoBom -Path (Join-Path $bridge 'bridge.json') -Text ($hb | ConvertTo-Json -Compress)
}

function Write-State {
    # Kind 用真机契约里的小写 camelCase 常量('move'/'none'/'selectRelic'…, 见 AgentPendingKind)。
    # 真机证据: 2026-10-01 抓到的 state.json 里就是 "Kind": "none" —— 桥接模型里没有枚举,
    # 全是 string 常量, 所以不存在"CesiumJson 把枚举写成 PascalCase"这回事。
    param([string]$Kind = 'move', [long]$Sn = 5027)
    $now = Get-BridgeNowMs
    $st = [ordered]@{
        Schema = 1; StateSeq = 7; Scene = 'RoomScene'; InRoom = $true; InBattle = $false
        IsMyTurn = $true; CurrentSn = $Sn; NotMove = $false
        Self = [ordered]@{ PlayerId = 1001; Nick = '冒烟夹具'; Gold = 12; HandCount = 2 }
        Pending = [ordered]@{
            Kind = $Kind; Actionable = $true; Sn = $Sn; Source = 'RawAction'
            SinceMs = $now - 800; DeadlineMs = $now + 4500; RemainingMs = 4500
            Candidates = @(
                [ordered]@{ Id = 3; Name = '地块 3'; Kind = 'land' },
                [ordered]@{ Id = 7; Name = '地块 7'; Kind = 'land'; Price = 3 },
                [ordered]@{ Id = 11; Name = '地块 11'; Kind = 'land'; SoldOut = $true }
            )
            Options = @('astral_move {"landId":<id>}')
            Notes = @('离线冒烟夹具')
        }
    }
    Write-Utf8NoBom -Path (Join-Path $bridge 'state.json') -Text ($st | ConvertTo-Json -Depth 8 -Compress)
}

Write-Heartbeat
Write-State

# ============================== 拉起 server(stdin/stdout = JSON-RPC) ==============================

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $false     # 直接继承, 避免管道写满(见 .NOTES)
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
$psi.EnvironmentVariables['CESIUM_AGENT_DIR'] = $bridge

$proc = [System.Diagnostics.Process]::Start($psi)
$script:NextId = 0

function Invoke-Mcp {
    param([string]$Method, $Params, [int]$TimeoutSec = 25)
    $script:NextId++
    $msg = [ordered]@{ jsonrpc = '2.0'; id = $script:NextId; method = $Method }
    if ($null -ne $Params) { $msg.params = $Params }
    $proc.StandardInput.WriteLine(($msg | ConvertTo-Json -Depth 12 -Compress))
    $proc.StandardInput.Flush()

    $task = $proc.StandardOutput.ReadLineAsync()
    if (-not $task.Wait([TimeSpan]::FromSeconds($TimeoutSec))) { throw "等 $Method 响应超时" }
    $line = $task.Result
    if ($null -eq $line) { throw "${Method}: server 关掉了 stdout" }
    return ($line | ConvertFrom-Json)
}

function Invoke-Tool {
    param([string]$Name, [string]$ArgsJson = '{}', [int]$TimeoutSec = 25)
    $r = Invoke-Mcp -Method 'tools/call' -TimeoutSec $TimeoutSec -Params ([ordered]@{
            name = $Name; arguments = (ConvertFrom-Json $ArgsJson)
        })
    $res = $r.result
    $text = ''
    if ($res -and $res.content) { $text = ($res.content | ForEach-Object { $_.text }) -join "`n" }
    return [pscustomobject]@{ Text = $text; IsError = [bool]($res.isError); Raw = $r }
}

$exitCode = 0
try {
    # ------------------------------ 1. 握手 ------------------------------
    Write-Host '[smoke] --- MCP 握手 / 工具清单 ---'
    $init = Invoke-Mcp -Method 'initialize' -Params ([ordered]@{
            protocolVersion = '2025-06-18'
            capabilities    = [ordered]@{}
            clientInfo      = [ordered]@{ name = 'smoke'; version = '1.0.0' }
        })
    Assert-That 'initialize 协商到 2025-06-18' ($init.result.protocolVersion -eq '2025-06-18') "实际 $($init.result.protocolVersion)"
    Assert-That 'initialize 返回 serverInfo' ([bool]$init.result.serverInfo.name) ''

    $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $proc.StandardInput.Flush()

    $list = Invoke-Mcp -Method 'tools/list' -Params ([ordered]@{})
    $tools = @($list.result.tools)
    Assert-That 'tools/list 返回 32 个工具' ($tools.Count -eq 32) "实际 $($tools.Count)"
    Assert-That '工具名统一 astral_ 前缀' (@($tools | Where-Object { $_.name -notlike 'astral_*' }).Count -eq 0) ''
    Assert-That '包含 astral_move 与 astral_emergency_stop' `
        ((@($tools.name) -contains 'astral_move') -and (@($tools.name) -contains 'astral_emergency_stop')) ''
    Assert-That '包含战斗三件套(询问/出牌/闪避)' `
        (((@($tools.name) -contains 'astral_ask_battle') -and (@($tools.name) -contains 'astral_use_card') -and
          (@($tools.name) -contains 'astral_battle_choice'))) ''
    Assert-That '包含地块/选点四件套(加油站/追击/商人/选点)' `
        (((@($tools.name) -contains 'astral_stop_or_continue') -and (@($tools.name) -contains 'astral_pursue_monster') -and
          (@($tools.name) -contains 'astral_vendor_buy_card') -and (@($tools.name) -contains 'astral_select_point'))) ''
    Assert-That '包含地块应答三件套(复活队友/机制选择/医院) + 炮台/占卜' `
        (((@($tools.name) -contains 'astral_revive_teammate') -and (@($tools.name) -contains 'astral_select_mechanism') -and
          (@($tools.name) -contains 'astral_hospital_check') -and (@($tools.name) -contains 'astral_battery_pick') -and
          (@($tools.name) -contains 'astral_divination_pick'))) ''

    # ------------------------------ 2. 只读工具读假状态 ------------------------------
    Write-Host '[smoke] --- 只读工具 ---'
    $status = Invoke-Tool 'astral_status'
    Assert-That 'astral_status 判定桥接活着' ($status.Text -match '桥接活着') $status.Text
    Assert-That 'astral_status 报出场景' ($status.Text -match 'RoomScene') ''

    $state = Invoke-Tool 'astral_state'
    Assert-That 'astral_state 原样回状态' ($state.Text -match 'StateSeq"\s*:\s*7') ''
    Assert-That 'astral_state 带出玩家昵称' ($state.Text -match '冒烟夹具') ''

    $pending = Invoke-Tool 'astral_pending'
    Assert-That 'astral_pending 识别出移动窗口' ($pending.Text -match '待响应:\s*move') $pending.Text
    Assert-That 'astral_pending 列出候选与价格' (($pending.Text -match '\[0\] 3') -and ($pending.Text -match '价格 3')) ''
    Assert-That 'astral_pending 标出售罄' ($pending.Text -match '已售罄') ''
    Assert-That 'astral_pending 给出可用操作' ($pending.Text -match 'astral_move') ''

    # 真机契约: 没有窗口时 Kind = 'none'(小写, 实测值)
    Write-State -Kind 'none' -Sn 0
    $none = Invoke-Tool 'astral_pending'
    Assert-That 'Kind=none 判为没有窗口' ($none.Text -match '没有需要你响应') $none.Text

    # 大小写容错(防御): 哪一侧哪天写成 PascalCase, 也不该被当成一个名叫 None 的真窗口
    Write-State -Kind 'None' -Sn 0
    $nonePc = Invoke-Tool 'astral_pending'
    Assert-That 'Kind=None(大小写容错) 同样判为没有窗口' ($nonePc.Text -match '没有需要你响应') $nonePc.Text
    Write-State -Kind 'move' -Sn 5027

    # ------------------------------ 3. 动作工具真往返 ------------------------------
    Write-Host '[smoke] --- 命令通道往返 ---'
    $move = Invoke-Tool 'astral_move' '{"landId":7,"sn":5027}'
    Assert-That 'astral_move 拿到假游戏回执' ($move.Text -match '已发送\(假游戏\)') $move.Text
    Assert-That 'astral_move 未报错' (-not $move.IsError) ''

    $cmdLog = Join-Path $bridge 'fakegame.log'
    $logged = if (Test-Path $cmdLog) { Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8 } else { '' }
    Assert-That '命令文件里 tool=move' ($logged -match '"tool"\s*:\s*"move"') $logged
    Assert-That '命令文件里带上了 landId=7' ($logged -match '"landId"\s*:\s*7') $logged
    Assert-That '命令文件带 seq 与 issuedAtMs' (($logged -match '"seq"') -and ($logged -match '"issuedAtMs"')) ''
    Assert-That '命令文件已被清掉' (@(Get-ChildItem $commands -Filter '*.json' -ErrorAction SilentlyContinue).Count -eq 0) ''
    Assert-That '结果文件也被接走(不残留)' (@(Get-ChildItem $results -Filter '*.json' -ErrorAction SilentlyContinue).Count -eq 0) ''

    # 战斗三件套也各走一次真往返(参数必须原样落到命令文件里)
    $ask = Invoke-Tool 'astral_ask_battle' '{"accept":true,"sn":5047}'
    Assert-That 'astral_ask_battle 往返成功' ((-not $ask.IsError) -and ($ask.Text -match '已发送\(假游戏\)')) $ask.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=ask_battle 且 accept=true' `
        (($logged -match '"tool"\s*:\s*"ask_battle"') -and ($logged -match '"accept"\s*:\s*true')) $logged

    $choice = Invoke-Tool 'astral_battle_choice' '{"dodge":false,"sn":5039}'
    Assert-That 'astral_battle_choice 往返成功' ((-not $choice.IsError) -and ($choice.Text -match '已发送\(假游戏\)')) $choice.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=battle_choice 且 dodge=false' `
        (($logged -match '"tool"\s*:\s*"battle_choice"') -and ($logged -match '"dodge"\s*:\s*false')) $logged

    $pass = Invoke-Tool 'astral_use_card' '{"pass":true,"sn":5035}'
    Assert-That 'astral_use_card pass 往返成功' ((-not $pass.IsError) -and ($pass.Text -match '已发送\(假游戏\)')) $pass.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=use_card 且 pass=true' `
        (($logged -match '"tool"\s*:\s*"use_card"') -and ($logged -match '"pass"\s*:\s*true')) $logged

    $noArg = Invoke-Tool 'astral_ask_battle' '{}'
    Assert-That 'astral_ask_battle 缺 accept 时直接报错(不下发)' ($noArg.IsError -eq $true) $noArg.Text

    # 地块/选点四件套(5077 加油站 / 5213 追击 / 5323 商人买卡 / 5067 控移选点)也各走一次真往返
    $soc = Invoke-Tool 'astral_stop_or_continue' '{"stop":true,"sn":5077}'
    Assert-That 'astral_stop_or_continue 往返成功' ((-not $soc.IsError) -and ($soc.Text -match '已发送\(假游戏\)')) $soc.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=stop_or_continue 且 stop=true' `
        (($logged -match '"tool"\s*:\s*"stop_or_continue"') -and ($logged -match '"stop"\s*:\s*true')) $logged

    $pur = Invoke-Tool 'astral_pursue_monster' '{"monsterId":1080857,"sn":5213}'
    Assert-That 'astral_pursue_monster 往返成功' ((-not $pur.IsError) -and ($pur.Text -match '已发送\(假游戏\)')) $pur.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=pursue_monster 且 monsterId 原样(64 位)' `
        (($logged -match '"tool"\s*:\s*"pursue_monster"') -and ($logged -match '"monsterId"\s*:\s*1080857')) $logged

    $ven = Invoke-Tool 'astral_vendor_buy_card' '{"buy":true,"sn":5323}'
    Assert-That 'astral_vendor_buy_card 往返成功' ((-not $ven.IsError) -and ($ven.Text -match '已发送\(假游戏\)')) $ven.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=vendor_buy_card 且 buy=true' `
        (($logged -match '"tool"\s*:\s*"vendor_buy_card"') -and ($logged -match '"buy"\s*:\s*true')) $logged

    $pt = Invoke-Tool 'astral_select_point' '{"point":4,"sn":5067}'
    Assert-That 'astral_select_point 往返成功' ((-not $pt.IsError) -and ($pt.Text -match '已发送\(假游戏\)')) $pt.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=select_point 且 point=4' `
        (($logged -match '"tool"\s*:\s*"select_point"') -and ($logged -match '"point"\s*:\s*4')) $logged

    $purNoArg = Invoke-Tool 'astral_pursue_monster' '{}'
    Assert-That 'astral_pursue_monster 缺 monsterId/pass 时报错(不下发)' ($purNoArg.IsError -eq $true) $purNoArg.Text

    # 地块应答三件套(5233 复活队友 / 5259 机制选择 / 5093 医院)也各走一次真往返
    $rev = Invoke-Tool 'astral_revive_teammate' '{"revive":true,"sn":5233}'
    Assert-That 'astral_revive_teammate 往返成功' ((-not $rev.IsError) -and ($rev.Text -match '已发送\(假游戏\)')) $rev.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=revive_teammate 且 revive=true' `
        (($logged -match '"tool"\s*:\s*"revive_teammate"') -and ($logged -match '"revive"\s*:\s*true')) $logged

    $mech = Invoke-Tool 'astral_select_mechanism' '{"select":true,"sn":5259}'
    Assert-That 'astral_select_mechanism 往返成功' ((-not $mech.IsError) -and ($mech.Text -match '已发送\(假游戏\)')) $mech.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=select_mechanism 且 select=true' `
        (($logged -match '"tool"\s*:\s*"select_mechanism"') -and ($logged -match '"select"\s*:\s*true')) $logged

    $hos = Invoke-Tool 'astral_hospital_check' '{"sn":5093}'
    Assert-That 'astral_hospital_check 往返成功' ((-not $hos.IsError) -and ($hos.Text -match '已发送\(假游戏\)')) $hos.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=hospital_check 且不带业务参数(唯一合法上行)' `
        (($logged -match '"tool"\s*:\s*"hospital_check"') -and ($logged -notmatch '"check"')) $logged

    # 炮台选目标(5063): 两种上行各走一次
    $bat = Invoke-Tool 'astral_battery_pick' '{"targetIds":[1001,2002],"sn":5063}'
    Assert-That 'astral_battery_pick(选目标) 往返成功' ((-not $bat.IsError) -and ($bat.Text -match '已发送\(假游戏\)')) $bat.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=battery_pick 且 targetIds 原样(数组)' `
        (($logged -match '"tool"\s*:\s*"battery_pick"') -and ($logged -match '"targetIds"\s*:\s*\[\s*1001\s*,\s*2002\s*\]')) $logged

    $batLeave = Invoke-Tool 'astral_battery_pick' '{"leave":true,"sn":5063}'
    Assert-That 'astral_battery_pick(离开) 往返成功' ((-not $batLeave.IsError) -and ($batLeave.Text -match '已发送\(假游戏\)')) $batLeave.Text
    $logged = Get-Content -LiteralPath $cmdLog -Raw -Encoding UTF8
    Assert-That '命令文件里 tool=battery_pick 且 leave=true' `
        (($logged -match '"tool"\s*:\s*"battery_pick"') -and ($logged -match '"leave"\s*:\s*true')) $logged

    $batNoArg = Invoke-Tool 'astral_battery_pick' '{}'
    Assert-That 'astral_battery_pick 缺 targetIds/leave 时报错(不下发)' ($batNoArg.IsError -eq $true) $batNoArg.Text

    # 占卜(5069): 带下标与不带参数(默认第 0 张)各走一次
    # 注意 fakegame.log 是**累积**日志(每个命令一行), 所以要只看最后一行
    $div = Invoke-Tool 'astral_divination_pick' '{"index":1,"sn":5069}'
    Assert-That 'astral_divination_pick(选下标) 往返成功' ((-not $div.IsError) -and ($div.Text -match '已发送\(假游戏\)')) $div.Text
    $lastLine = @(Get-Content -LiteralPath $cmdLog -Encoding UTF8) | Select-Object -Last 1
    Assert-That '命令文件里 tool=divination_pick 且 index=1' `
        (($lastLine -match '"tool"\s*:\s*"divination_pick"') -and ($lastLine -match '"index"\s*:\s*1')) $lastLine

    $divDef = Invoke-Tool 'astral_divination_pick' '{}'
    Assert-That 'astral_divination_pick 不传参数也往返成功(默认第 0 张)' `
        ((-not $divDef.IsError) -and ($divDef.Text -match '已发送\(假游戏\)')) $divDef.Text
    $lastLine = @(Get-Content -LiteralPath $cmdLog -Encoding UTF8) | Select-Object -Last 1
    Assert-That '命令文件里 tool=divination_pick 且不带业务参数(默认第 0 张)' `
        (($lastLine -match '"tool"\s*:\s*"divination_pick"') -and ($lastLine -notmatch '"index"')) $lastLine

    # ------------------------------ 4. 开关闸门 ------------------------------
    Write-Host '[smoke] --- 安全开关(control.json) ---'
    $null = Invoke-Tool 'astral_control' '{"enableActions":false,"reason":"冒烟"}'
    $ctl = Get-Content -LiteralPath (Join-Path $bridge 'control.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-That 'astral_control 写只读开关' ($ctl.EnableActions -eq $false) ($ctl | ConvertTo-Json -Compress)
    Assert-That 'control.json 保持 PascalCase(两侧契约)' ((Get-Content -LiteralPath (Join-Path $bridge 'control.json') -Raw) -match '"EnableActions"') ''

    $null = Invoke-Tool 'astral_emergency_stop' '{"reason":"冒烟急停"}'
    $ctl = Get-Content -LiteralPath (Join-Path $bridge 'control.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-That 'astral_emergency_stop 置 PauseActions' ($ctl.PauseActions -eq $true) ''

    $null = Invoke-Tool 'astral_resume'
    $ctl = Get-Content -LiteralPath (Join-Path $bridge 'control.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-That 'astral_resume 清 PauseActions' ($ctl.PauseActions -eq $false) ''
    Assert-That 'astral_resume 不动只读开关' ($ctl.EnableActions -eq $false) ''

    # ------------------------------ 5. 心跳过期必须拒绝下发 ------------------------------
    Write-Host '[smoke] --- 心跳过期保护 ---'
    Write-Heartbeat -AgeMs 60000
    $stale = Invoke-Tool 'astral_move' '{"landId":7,"sn":5027}'
    Assert-That '心跳过期时动作工具报错' ($stale.IsError -eq $true) $stale.Text
    Assert-That '错误里说明是心跳问题' ($stale.Text -match '心跳') $stale.Text
    Assert-That '心跳过期时没有留下命令文件' (@(Get-ChildItem $commands -Filter '*.json' -ErrorAction SilentlyContinue).Count -eq 0) ''

    Write-Heartbeat   # 恢复
    $again = Invoke-Tool 'astral_move' '{"landId":11,"sn":5027}'
    Assert-That '心跳恢复后又能下发' ($again.Text -match '已发送\(假游戏\)') $again.Text
}
finally {
    try { Write-Utf8NoBom -Path (Join-Path $bridge 'fakegame.stop') -Text 'stop' } catch { }
    try { if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(3000) | Out-Null } } catch { }
    try { Stop-Job $fakeGame -ErrorAction SilentlyContinue; Remove-Job $fakeGame -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
if ($script:Failed.Count -gt 0) {
    Write-Host "[smoke] === 失败 $($script:Failed.Count) 项 / 通过 $($script:Passed) 项 ===" -ForegroundColor Red
    foreach ($f in $script:Failed) { Write-Host "  - $f" -ForegroundColor Red }
    if ($Keep) { Write-Host "[smoke] 现场保留在: $bridge" }
    else { Remove-Item -Recurse -Force $bridge -ErrorAction SilentlyContinue }
    exit 1
}

Write-Host "[smoke] === 全部通过 ($($script:Passed) 项断言) ===" -ForegroundColor Green
if ($Keep) { Write-Host "[smoke] 现场保留在: $bridge" }
else { Remove-Item -Recurse -Force $bridge -ErrorAction SilentlyContinue }
exit 0
