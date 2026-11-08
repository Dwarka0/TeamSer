# Requires Windows PowerShell 5.1+
# Runs the full local validation: desktop build, desktop tests, backend typecheck, backend tests.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\test-all.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failed = @()

function Step([string]$name, [scriptblock]$action) {
    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$name FAILED (exit $LASTEXITCODE)"
        $script:failed += $name
    } else {
        Write-Host "$name OK" -ForegroundColor Green
    }
}

Step 'Desktop build (Debug)' {
    dotnet build (Join-Path $root 'src\Desktop\Desktop.csproj') -c Debug --nologo
}

Step 'Desktop tests' {
    dotnet test (Join-Path $root 'tests\Desktop.Tests\LTS.Tests.csproj') -c Debug --nologo
}

Step 'Backend typecheck' {
    Push-Location (Join-Path $root 'src\Backend')
    try { npm run typecheck } finally { Pop-Location }
}

Step 'Backend tests' {
    Push-Location (Join-Path $root 'src\Backend')
    try { npm test } finally { Pop-Location }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "All checks passed." -ForegroundColor Green
exit 0
