<#
.SYNOPSIS
    Publishes one self-contained installer exe per app config.

.DESCRIPTION
    Builds src/ClickWrap.Installer once per apps/*.yaml, embedding that file as install.yaml,
    and drops the result in out/<appId>/.

.PARAMETER App
    Publish only this config (the file name without .yaml). Omit to build every app.

.EXAMPLE
    ./build/publish-installers.ps1
    ./build/publish-installers.ps1 -App race-timer
#>
[CmdletBinding()]
param(
    [string]$App
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/ClickWrap.Installer/ClickWrap.Installer.csproj'
$appsDir = Join-Path $repoRoot 'src/ClickWrap.Installer/apps'
$outRoot = Join-Path $repoRoot 'out'

$configs = if ($App) {
    $path = Join-Path $appsDir "$App.yaml"
    if (-not (Test-Path $path)) { throw "No config at $path" }
    Get-Item $path
}
else {
    Get-ChildItem $appsDir -Filter '*.yaml'
}

if (-not $configs) { throw "No app configs found in $appsDir" }

foreach ($config in $configs) {
    $appId = $config.BaseName

    # PascalCase the app id so race-timer produces RaceTimerSetup.exe
    $assemblyName = (($appId -split '[-_.]' | Where-Object { $_ } | ForEach-Object {
        $_.Substring(0, 1).ToUpper() + $_.Substring(1)
    }) -join '') + 'Setup'

    $outDir = Join-Path $outRoot $appId
    Write-Host "Publishing $appId -> $assemblyName.exe" -ForegroundColor Cyan

    dotnet publish $project `
        -c Release `
        -p:ClickWrapApp=$appId `
        -p:InstallerAssemblyName=$assemblyName `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $appId" }

    $exe = Join-Path $outDir "$assemblyName.exe"
    $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "  $exe ($sizeMb MB)" -ForegroundColor Green
}
