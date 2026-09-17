# Packs a self-contained win-x64 folder for another PC. No admin. No installer.
# Build machine needs the .NET SDK. Target PCs do not (runtime is bundled).
# Does not stop any running Mercury.exe. Publishes to dist\Mercury, not bin\Release.
param(
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\Mercury\Mercury.csproj"
$catcherProject = Join-Path $root "src\Mercury.Catcher\Mercury.Catcher.csproj"
$dist = Join-Path $root "dist"
$out = Join-Path $dist "Mercury"
$zipPath = Join-Path $dist "Mercury-portable.zip"
$logPath = Join-Path $dist "publish.log"

function Write-PublishLog {
    param([string]$Message)
    $line = "{0}  {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Add-Content -Path $logPath -Value $line -Encoding utf8
}

function Test-FileLocked {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Close()
        return $false
    } catch {
        return $true
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: 'dotnet' is not on PATH."
    Write-Host "Install the .NET 8 SDK on this build machine, then re-run this script."
    Write-Host "Target PCs do not need the SDK or runtime - this pack bundles .NET."
    exit 1
}

if (-not (Test-Path $project)) {
    Write-Host "ERROR: Project not found: $project"
    exit 1
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Set-Content -Path $logPath -Value ("{0}  Mercury portable publish" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss")) -Encoding utf8
Write-PublishLog "dotnet: $((Get-Command dotnet).Source)"

$exeOut = Join-Path $out "Mercury.exe"
$catcherOut = Join-Path $out "MercuryCatcher.exe"
if (Test-FileLocked $exeOut) {
    Write-Host "ERROR: Cannot overwrite $exeOut because it is in use."
    Write-Host "Close the Mercury window launched from dist\Mercury and re-run."
    Write-Host "This script does not kill Mercury.exe (including copies started from bin\Release)."
    Write-PublishLog "FAIL: $exeOut is locked"
    exit 1
}
if (Test-FileLocked $catcherOut) {
    Write-Host "ERROR: Cannot overwrite $catcherOut because it is in use."
    Write-Host "Close MercuryCatcher.exe launched from dist\Mercury and re-run."
    Write-Host "This script does not kill MercuryCatcher.exe."
    Write-PublishLog "FAIL: $catcherOut is locked"
    exit 1
}

if (Test-Path $out) {
    try {
        Remove-Item -Recurse -Force $out -ErrorAction Stop
    } catch {
        Write-Host "ERROR: Could not clear $out (files in use)."
        Write-Host "Close Mercury if you launched it from dist\Mercury, then re-run."
        Write-Host "This script does not kill Mercury.exe."
        Write-PublishLog "FAIL: could not clear output folder: $($_.Exception.Message)"
        exit 1
    }
}

Write-Host "Publishing self-contained win-x64 folder (not single-file)..."
Write-PublishLog "dotnet publish -c Release -r win-x64 --self-contained true (PublishPortable=false; AfterBuild skipped to avoid a second publish)"

# Same flags as Mercury.csproj AfterBuild. PublishPortable=false so this command does not recurse.
& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:PublishPortable=false `
    -o $out

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet publish failed (exit $LASTEXITCODE). See $logPath"
    Write-PublishLog "FAIL: dotnet publish exit $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "Publishing MercuryCatcher into the same folder..."
Write-PublishLog "dotnet publish Mercury.Catcher -c Release -r win-x64 --self-contained true"
& dotnet publish $catcherProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:ErrorOnDuplicatePublishOutputFiles=false `
    -o $out

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: MercuryCatcher publish failed (exit $LASTEXITCODE). See $logPath"
    Write-PublishLog "FAIL: MercuryCatcher publish exit $LASTEXITCODE"
    exit $LASTEXITCODE
}

if (-not (Test-Path $exeOut)) {
    Write-Host "ERROR: Publish finished but Mercury.exe was not created at $exeOut"
    Write-PublishLog "FAIL: Mercury.exe missing after publish"
    exit 1
}

if (-not (Test-Path $catcherOut)) {
    Write-Host "ERROR: Publish finished but MercuryCatcher.exe was not created at $catcherOut"
    Write-PublishLog "FAIL: MercuryCatcher.exe missing after publish"
    exit 1
}

$readmeSrc = Join-Path $root "README.md"
if (Test-Path $readmeSrc) {
    Copy-Item $readmeSrc (Join-Path $out "README.md") -Force
} else {
    Write-Host "WARNING: README.md not found at repo root; pack has no README."
    Write-PublishLog "WARN: README.md missing"
}

if ($Zip) {
    if (Test-Path $zipPath) {
        Remove-Item -Force $zipPath
    }
    Compress-Archive -Path $out -DestinationPath $zipPath -CompressionLevel Optimal
    Write-PublishLog "zip: $zipPath"
    Write-Host "Zip: $zipPath"
}

Write-PublishLog "OK: $exeOut"
Write-Host ""
Write-Host "Portable folder: $out"
Write-Host "Copy this folder to the other PC and double-click Mercury.exe (sender) or MercuryCatcher.exe (listener)."
Write-Host "No .NET SDK or runtime install is required on the target PC (runtime is bundled)."
Write-Host "No admin. No installer."
if ($Zip) {
    Write-Host "Or copy the zip, unzip it, then double-click Mercury.exe."
}
Write-Host "Log: $logPath"
exit 0
