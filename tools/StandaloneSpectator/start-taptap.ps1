[CmdletBinding()]
param([int]$Port=18743)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$protocol=Join-Path $workspace 'extracted_dlls'
$handoff='X:\TapTap\PC Games\524022\starengine_BuildPC_CN_TAPTAP_8\CN_TAPTAP_V3.2.1\AstralParty_ModLoader\auth-handoff'
$dll=Join-Path $PSScriptRoot 'bin\Release\net8.0\StandaloneSpectator.dll'
if (!(Test-Path -LiteralPath $dll)) { throw 'Build StandaloneSpectator in Release first.' }
$web=Join-Path $PSScriptRoot 'web\dist'
$data=Join-Path $workspace 'config_extract'
$cache=Join-Path $env:LOCALAPPDATA 'CesiumStandaloneSpectator\auth.protected'
if (!(Test-Path -LiteralPath (Join-Path $web 'index.html'))) { throw 'Build the React website first (see web README).' }
& dotnet $dll --protocol $protocol --handoff $handoff --credential-cache $cache --web-root $web --data $data --port $Port
exit $LASTEXITCODE
