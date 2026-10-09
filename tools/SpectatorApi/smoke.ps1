param([int]$Port = 0)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SpectatorApi.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Port -eq 0) {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $Port = $probe.LocalEndpoint.Port; $probe.Stop()
}
$smokeRoot = Join-Path $PSScriptRoot '.smoke'
$work = Join-Path $smokeRoot ([guid]::NewGuid().ToString('N'))
$bridge = Join-Path $work 'bridge'
New-Item -ItemType Directory -Path $bridge -Force | Out-Null
$stdout = Join-Path $work 'stdout.log'
$stderr = Join-Path $work 'stderr.log'
$dll = Join-Path $PSScriptRoot 'bin\Release\net8.0\SpectatorApi.dll'
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [timespan]::FromSeconds(5)
$script:server = $null
$script:checks = 0
$script:token = ''
function Assert($condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    $script:checks++
}
function Read-SharedLog([string]$path) {
    $stream = [System.IO.FileStream]::new($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $reader = [System.IO.StreamReader]::new($stream)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Start-Api {
    $script:server = Start-Process dotnet -ArgumentList @("`"$dll`"", '--bridge', "`"$bridge`"", '--port', $Port) -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [datetime]::UtcNow.AddSeconds(15)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($script:server.HasExited) { throw "Server exited: $([System.IO.File]::ReadAllText($stderr))" }
        if (Test-Path $stdout) {
            $text = Read-SharedLog $stdout
            if ($text -match 'Bearer token: ([0-9a-f]{64})') {
                $script:token = $Matches[1]
                try { $r = Request GET '/health' $null $false; if ($r.Status -eq 200) { return } } catch { }
            }
        }
        Start-Sleep -Milliseconds 50
    }
    throw 'Server startup timeout.'
}
function Stop-Api {
    if ($script:server -and -not $script:server.HasExited) {
        Stop-Process -Id $script:server.Id
        $script:server.WaitForExit()
    }
}
function Make-Request([string]$method, [string]$path, $body = $null, [bool]$auth = $true) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "http://127.0.0.1:$Port$path")
    if ($auth) { $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $script:token) }
    if ($null -ne $body) { $request.Content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json') }
    return $request
}
function Request([string]$method, [string]$path, $body = $null, [bool]$auth = $true) {
    $request = Make-Request $method $path $body $auth
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return @{ Status = [int]$response.StatusCode; Body = $text; Json = ($text | ConvertFrom-Json) }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Publish-Json([string]$path, $value) {
    $temp = "$path.tmp"
    [System.IO.File]::WriteAllText($temp, ($value | ConvertTo-Json -Depth 16 -Compress), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($temp, $path, $true)
}
function Write-State([int]$seconds = 0, [string]$mode = 'pve', [bool]$official = $true) {
    Publish-Json (Join-Path $bridge 'state.json') @{
        sampledUtc = [datetime]::UtcNow.AddSeconds($seconds).ToString('o')
        sessionId = 'fake-session'; mode = $mode; officialSpectating = $official
        players = @(@{ playerId = '9007199254740993'; cards = @(123, 456) })
    }
}
function Write-Result([string]$id, [string]$status, [string]$actualId = '') {
    if (-not $actualId) { $actualId = $id }
    Publish-Json (Join-Path $bridge "results\$id.json") @{ id = $actualId; sessionId = 'fake-session'; status = $status }
}
try {
    Start-Api
    Assert ((Request GET '/health' $null $false).Status -eq 200) 'health is public'
    Assert ((Request GET '/state' $null $false).Status -eq 401) 'state requires token'
    Assert ((Request POST '/leave' '{}' $false).Status -eq 401) 'POST requires token'
    Assert ((Request GET '/commands/00000000000000000000000000000000' $null $false).Status -eq 401) 'results require token'
    Assert ((Request GET '/state').Status -eq 503) 'missing state unavailable'
    Write-State
    $r = Request GET '/state'
    Assert ($r.Status -eq 200 -and $r.Json.players[0].cards.Count -eq 2) 'fresh official PVE cards available'
    Write-State -seconds -6
    $r = Request GET '/state'
    Assert ($r.Status -eq 503 -and $r.Body -notmatch 'cards') 'stale state never exposes cards'
    Write-State -seconds 60
    Assert ((Request GET '/state').Status -eq 503) 'future timestamp rejected'
    Write-State -mode 'pvp'
    Assert ((Request GET '/state').Status -eq 503) 'PVP state rejected'
    Write-State -official $false
    Assert ((Request GET '/state').Status -eq 503) 'unofficial spectating rejected'
    [System.IO.File]::WriteAllText((Join-Path $bridge 'state.json'), '{')
    Assert ((Request GET '/state').Status -eq 503) 'malformed bridge state unavailable'
    [System.IO.File]::WriteAllText((Join-Path $bridge 'state.json'), ('x' * (1048576 + 1)))
    Assert ((Request GET '/state').Status -eq 503) 'oversized state bounded'
    Write-State
    Assert ((Request POST '/watch' '{"watchCode":42}').Status -eq 400) 'numeric watch code rejected'
    Assert ((Request POST '/focus' '{"playerId":42}').Status -eq 400) 'player ID remains a string'
    Assert ((Request POST '/watch' '{"watchCode":" "}').Status -eq 400) 'blank watch code rejected'
    Assert ((Request POST '/watch' '{"watchCode":"ok","extra":true}').Status -eq 400) 'unknown properties rejected'
    Assert ((Request POST '/watch' '{"watchCode":"a","watchCode":"b"}').Status -eq 400) 'duplicate properties rejected'
    Assert ((Request POST '/watch' '{').Status -eq 400) 'malformed request rejected'
    Assert ((Request POST '/watch' ('x' * 4097)).Status -eq 413) 'oversized request rejected'
    Assert ((Request GET '/commands/not-an-id').Status -eq 400) 'unsafe result ID rejected'
    Assert ((Request GET '/commands/00000000000000000000000000000000').Status -eq 404) 'unknown result ID not found'
    $r = Request POST '/watch' '{"watchCode":"fake-watch"}'
    Assert ($r.Status -eq 202 -and $r.Json.id -match '^[0-9a-f]{32}$') 'watch accepted asynchronously'
    $id = $r.Json.id
    $commandPath = Join-Path $bridge "commands\$id.json"
    $command = [System.IO.File]::ReadAllText($commandPath) | ConvertFrom-Json
    Assert ($command.action -eq 'join' -and $command.watchCode -eq 'fake-watch' -and $null -eq $command.playerId) 'atomic command matches contract'
    Assert (@(Get-ChildItem (Join-Path $bridge 'commands') -Filter '*.tmp').Count -eq 0) 'no published temp files'
    Assert ((Request POST '/leave' '{}').Status -eq 409) 'second command rejected'
    Assert ((Request GET "/commands/$id").Status -eq 202) 'in-flight command polling'
    # Simulate mod consuming command; deleting its input must not open the gate.
    [System.IO.File]::Delete($commandPath)
    Assert ((Request POST '/leave' '{}').Status -eq 409) 'consumed input still holds gate'
    Write-Result $id 'pending'
    Assert ((Request POST '/leave' '{}').Status -eq 409) 'interim result still holds gate'
    Assert ((Request GET "/commands/$id").Status -eq 202) 'interim result HTTP status'
    $oldToken = $script:token
    Stop-Api; Start-Api
    Assert ($script:token -ne $oldToken) 'token rotated on restart'
    Assert ((Request POST '/leave' '{}').Status -eq 409) 'pending gate survives API restart'
    Write-Result $id 'done' '00000000000000000000000000000000'
    Assert ((Request POST '/leave' '{}').Status -eq 409) 'mismatched result cannot release gate'
    Write-Result $id 'done'
    Assert ((Request GET "/commands/$id").Status -eq 200) 'terminal result returned'
    $r = Request POST '/focus' '{"playerId":"9007199254740993"}'
    Assert ($r.Status -eq 202) 'focus accepted after terminal result'
    $id = $r.Json.id
    $commandPath = Join-Path $bridge "commands\$id.json"
    $command = [System.IO.File]::ReadAllText($commandPath) | ConvertFrom-Json
    Assert ($command.playerId -ceq '9007199254740993' -and $command.action -eq 'focus') 'large player ID preserved exactly'
    [System.IO.File]::Delete($commandPath)
    Write-Result $id 'timeout'
    # Race two POSTs against the one-slot gate.
    $a = Make-Request POST '/leave' '{}'; $b = Make-Request POST '/leave' '{}'
    try {
        $ta = $client.SendAsync($a); $tb = $client.SendAsync($b)
        $ra = $ta.GetAwaiter().GetResult(); $rb = $tb.GetAwaiter().GetResult()
        try {
            $statuses = @([int]$ra.StatusCode, [int]$rb.StatusCode) | Sort-Object
            Assert ($statuses[0] -eq 202 -and $statuses[1] -eq 409) 'simultaneous submissions yield one accepted command'
        } finally { $ra.Dispose(); $rb.Dispose() }
    } finally { $a.Dispose(); $b.Dispose() }
    Assert (@(Get-ChildItem (Join-Path $bridge 'commands') -Filter '*.json').Count -eq 1) 'queue holds at most one command'
    Write-Output "PASS: $script:checks localhost smoke checks; fake bridge only; server stopped on exit."
    Write-Output "Fake bridge and logs retained at $work"
} finally {
    Stop-Api
    $client.Dispose()
}
