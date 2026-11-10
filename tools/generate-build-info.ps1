param([Parameter(Mandatory)][string]$Output, [string]$Repo = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$hash = 'unknown'
if (Get-Command git -ErrorAction SilentlyContinue) {
    # Windows PowerShell 5 turns native stderr (including harmless Git warnings)
    # into ErrorRecords. Missing Git metadata must not fail an archive build.
    $ErrorActionPreference = 'Continue'
    $value = & git -C $Repo rev-parse --short=8 HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $value -match '^[0-9a-f]{8,40}$') {
        $hash = $value
        & git -c core.safecrlf=false -C $Repo diff --quiet HEAD -- 2>$null
        if ($LASTEXITCODE -eq 1) { $hash += '-dirty' }
    }
    $ErrorActionPreference = 'Stop'
}
$content = "#pragma once`n#define CESIUM_BUILD_COMMIT `"$hash`"`n"
if (-not (Test-Path -LiteralPath $Output) -or [IO.File]::ReadAllText($Output) -cne $content) {
    [IO.File]::WriteAllText($Output, $content, [Text.UTF8Encoding]::new($false))
}
