# Requires Windows PowerShell 5.1+
# Helper for the Cloudflare side. PREVIEW / DEPLOY ONLY AFTER AN ADMIN GENERATES THE HASH.
#
# Prerequisites (all done once, by the administrator):
#   1. wrangler login        (opens a browser, requires CLOUDFLARE access)
#   2. wrangler d1 create ts3-auth          -- then paste the returned database_id into wrangler.jsonc
#   3. node scripts/gen-password-hash.mjs   -- produces the argon2id hash
#
# This script NEVER invents the D1 database_id or the password hash for you. Both must be
# produced by real commands you ran, or deploy cannot be trusted.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1          # apply migrations + build check
#   powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Deploy  # also deploys the Worker

param(
    [switch]$Deploy,
    [switch]$SkipMigrations
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 1. Solve the D1 database_id problem honestly: refuse to deploy with the placeholder.
$cfg = Join-Path $root 'wrangler.jsonc'
$json = Get-Content $cfg -Raw
if ($json -match 'REPLACE_WITH_D1_DATABASE_ID') {
    Write-Warning "wrangler.jsonc still contains the placeholder database id."
    Write-Warning "Run this first and paste the result into wrangler.jsonc:"
    Write-Warning "    wrangler d1 create ts3-auth"
    Write-Warning ""
    Write-Warning "Deploy intentionally stopped. Do not deploy a Worker that cannot reach its D1 database."
    exit 1
}

# 2. Check we are logged in to Cloudflare.
$who = @()
try {
    $who = & wrangler whoami 2>&1
} catch {
    $who = @($_.Exception.Message)
}
$whoText = ($who | Out-String)
if ($whoText -match 'Not logged in' -or $whoText -match 'No account configured') {
    Write-Warning "Wrangler is not authenticated."
    Write-Warning "Run 'wrangler login' first. Deploying requires your own Cloudflare account."
    exit 1
}

Write-Host "Deploying D1 migrations..." -ForegroundColor Cyan
if (-not $SkipMigrations) {
    wrangler d1 migrations apply ts3-auth --remote
    if ($LASTEXITCODE -ne 0) { Write-Error "d1 migrations failed."; exit $LASTEXITCODE }
}

Write-Host "Verifying D1 configuration..." -ForegroundColor Cyan
wrangler d1 info ts3-auth --remote
if ($LASTEXITCODE -ne 0) { Write-Error "d1 info failed."; exit $LASTEXITCODE }

if ($Deploy) {
    Write-Host "Deploying Worker..." -ForegroundColor Cyan
    wrangler deploy
    if ($LASTEXITCODE -ne 0) { Write-Error "wrangler deploy failed."; exit $LASTEXITCODE }
    Write-Host "Worker deployed." -ForegroundColor Green
    Write-Host ""
    Write-Host "Remaining manual step (one time, per environment):" -ForegroundColor Yellow
    Write-Host "  wrangler secret put ADMIN_PASSWORD_HASH"
    Write-Host "(paste the hash produced by node scripts/gen-password-hash.mjs)"
}

Write-Host ""
Write-Host "NOTE: no deploy was actually performed against your account unless you used -Deploy," -ForegroundColor Yellow
Write-Host "AND wrangler was authenticated AND the D1 database id was configured." -ForegroundColor Yellow
