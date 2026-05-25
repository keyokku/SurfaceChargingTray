# Rebuild SurfaceChargingTray.exe (and diagnostic tool) for both architectures.
# Run from any directory:  powershell -File build.ps1 [-Arch all|x64|arm64]
param(
    [ValidateSet('all','x64','arm64')]
    [string]$Arch = 'all'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src        = Join-Path $root 'source'
$srcDiag    = Join-Path $root 'source-diagnostic'
$dist       = Join-Path $root 'dist\v1.4.2'

# Stop running instances so the .exe files aren't locked.
Get-Process -Name SurfaceChargingTray           -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name SurfaceChargingTrayDiagnostic -ErrorAction SilentlyContinue | Stop-Process -Force

# Locate dotnet.exe (winget-installed default location, or PATH fallback).
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$rids = if ($Arch -eq 'all') { @('win-x64','win-arm64') } else { @("win-$Arch") }

# ---- Main app ------------------------------------------------------------
foreach ($rid in $rids) {
    $archName = $rid -replace '^win-',''
    $outDir   = Join-Path $dist $archName
    Write-Host "Building main app: $rid -> $outDir"

    & $dotnet publish (Join-Path $src 'SurfaceChargingTray.csproj') `
        -c Release -r $rid --self-contained false `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "main $rid build failed (exit $LASTEXITCODE)." }

    $exe = Join-Path $outDir 'SurfaceChargingTray.exe'
    "  -> {0:N2} MB" -f ((Get-Item $exe).Length / 1MB)
}

# ---- Diagnostic tool ----------------------------------------------------
foreach ($rid in $rids) {
    $archName = $rid -replace '^win-',''
    $outDir   = Join-Path $dist "diagnostic-$archName"
    Write-Host "Building diagnostic: $rid -> $outDir"

    & $dotnet publish (Join-Path $srcDiag 'SurfaceChargingTrayDiagnostic.csproj') `
        -c Release -r $rid --self-contained false `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=false `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "diagnostic $rid build failed (exit $LASTEXITCODE)." }

    $exe = Join-Path $outDir 'SurfaceChargingTrayDiagnostic.exe'
    "  -> {0:N2} MB" -f ((Get-Item $exe).Length / 1MB)
}

# ---- Stage the single diagnostic zip (both arch exes flat) --------------
if ($Arch -eq 'all') {
    Write-Host "Staging SurfaceChargingTrayDiagnostic.zip"
    $stage = Join-Path $root 'release-staging\v1.4.2\diagnostic'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Path $stage | Out-Null

    # Rename arch-specific exes so users can tell them apart at a glance.
    Copy-Item (Join-Path $dist 'diagnostic-arm64\SurfaceChargingTrayDiagnostic.exe') `
              (Join-Path $stage 'SurfaceChargingTrayDiagnostic-arm64.exe')
    Copy-Item (Join-Path $dist 'diagnostic-x64\SurfaceChargingTrayDiagnostic.exe') `
              (Join-Path $stage 'SurfaceChargingTrayDiagnostic-x64.exe')

    # Bundle a tiny README inside the zip so users know which exe to run.
    $readme = Join-Path $root 'source-diagnostic\README.txt'
    if (Test-Path $readme) { Copy-Item $readme (Join-Path $stage 'README.txt') }

    $zipOut = Join-Path $root 'release-staging\v1.4.2\SurfaceChargingTrayDiagnostic.zip'
    if (Test-Path $zipOut) { Remove-Item -Force $zipOut }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipOut -CompressionLevel Optimal
    "  -> {0:N2} MB" -f ((Get-Item $zipOut).Length / 1MB)
}
