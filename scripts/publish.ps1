# Requires Windows PowerShell 5.1+
# Publishes the WPF desktop app as a self-contained Windows x64 executable.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'publish\win-x64'
$proj = Join-Path $root 'src\Desktop\Desktop.csproj'
$exe  = Join-Path $out 'TeamSer.exe'

if (-not (Test-Path $proj)) {
    Write-Error "Project not found: $proj"
    exit 1
}

Write-Host "Restoring and publishing to $out ..." -ForegroundColor Cyan
dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $out

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

if (-not (Test-Path $exe)) {
    Write-Error "Publish reported success but $exe is missing."
    exit 1
}

Remove-Item (Join-Path $out 'TeamSer.pdb') -ErrorAction SilentlyContinue

$sizeMb = [math]::Round(((Get-Item $exe).Length / 1MB), 1)
Write-Host ""
Write-Host "Published: $exe ($sizeMb MB)" -ForegroundColor Green
Write-Host "Self-contained: no .NET runtime required on the target machine." -ForegroundColor Green
