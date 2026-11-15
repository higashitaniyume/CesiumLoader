param(
    [string]$DotNet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $taskRoot 'LanguageUnlockMod.csproj'
$modVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'LanguageUnlockMod.json') -Raw | ConvertFrom-Json).version

& $DotNet build $projectFile -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$outputRoot = Join-Path $taskRoot 'outputs'
$packageRoot = Join-Path $outputRoot "package-$modVersion"
$modRoot = Join-Path $packageRoot 'LanguageUnlockMod'
if (Test-Path -LiteralPath $modRoot) { Remove-Item -LiteralPath $modRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $modRoot | Out-Null

$buildRoot = Join-Path $taskRoot 'bin/Release/netstandard2.0'
foreach ($file in @('LanguageUnlockMod.dll', 'LanguageUnlockMod.pdb', 'LanguageUnlockMod.json')) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $file) -Destination $modRoot -Force
}
Copy-Item -LiteralPath (Join-Path $buildRoot 'data') -Destination $modRoot -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $taskRoot 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $modRoot -Force
}

$zipPath = Join-Path $outputRoot "LanguageUnlockMod-$modVersion.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -LiteralPath $modRoot -DestinationPath $zipPath -Force

Write-Output "Package created: $zipPath"
