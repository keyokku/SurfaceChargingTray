# Rebuild SurfaceChargingTray.exe for both architectures into dist/.
# Run from any directory:  powershell -File build.ps1 [-Arch all|x64|arm64]
param(
    [ValidateSet('all','x64','arm64')]
    [string]$Arch = 'all'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'source'
$dist = Join-Path $root 'dist'

# Stop a running tray so the .exe isn't locked.
Get-Process -Name SurfaceChargingTray -ErrorAction SilentlyContinue | Stop-Process -Force

# Locate dotnet.exe (winget-installed default location, or PATH fallback).
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$rids = if ($Arch -eq 'all') { @('win-x64','win-arm64') } else { @("win-$Arch") }

foreach ($rid in $rids) {
    $archName = $rid -replace '^win-',''
    $outDir   = Join-Path $dist $archName
    Write-Host "Building $rid -> $outDir"

    & $dotnet publish (Join-Path $src 'SurfaceChargingTray.csproj') `
        -c Release -r $rid --self-contained `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "$rid build failed (exit $LASTEXITCODE)." }

    $exe = Join-Path $outDir 'SurfaceChargingTray.exe'
    "  -> {0:N2} MB" -f ((Get-Item $exe).Length / 1MB)
}
