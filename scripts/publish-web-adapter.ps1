$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$publishDir = Join-Path $repoRoot "dist\LocalWebAdapter-win-x64"

Set-Location (Join-Path $repoRoot "LocalWebAdapter")

dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o $publishDir

Write-Host "Published portable adapter to: $publishDir"
Write-Host "Run LocalWebAdapter.exe, then open http://127.0.0.1:5057"
