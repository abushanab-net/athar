# Run the Athar production stack on this machine in Docker, built from this checkout (deploy/local.override.yml).
#
#   deploy/local.ps1 up      # build, start, wait until healthy, print the URLs (first run creates deploy/.env.local)
#   deploy/local.ps1 down    # stop; data stays in the athar-local volumes
#   deploy/local.ps1 reset   # stop and delete the local data (the next up seeds again)
#   deploy/local.ps1 logs    # follow the logs
#   deploy/local.ps1 ps      # container status
#
# Optional, before the first up: $env:ATHAR_AI_API_KEY (model gateway key; without it search is degraded), $env:ATHAR_SEED
# (qa by default; minimal for an empty org), $env:ATHAR_LOCAL_PORT (8180 by default; uses that port and the next 2).
param([ValidateSet('up', 'down', 'reset', 'logs', 'ps')][string]$Command = 'up')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$envFile = 'deploy/.env.local'
$compose = @('compose', '-p', 'athar-local', '-f', 'deploy/docker-compose.yml', '-f', 'deploy/local.override.yml', '--env-file', $envFile)

function Secret { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower() }
function Value($name) { ((Get-Content $envFile | Where-Object { $_ -like "$name=*" }) -split '=', 2)[1] }
function Compose { & docker @compose @args; if ($LASTEXITCODE) { throw "docker compose $args failed ($LASTEXITCODE)" } }

function New-Env {
    $port = if ($env:ATHAR_LOCAL_PORT) { [int]$env:ATHAR_LOCAL_PORT } else { 8180 }
    $seed = if ($env:ATHAR_SEED) { $env:ATHAR_SEED } else { 'qa' }
    $aiUrl = if ($env:ATHAR_AI_BASE_URL) { $env:ATHAR_AI_BASE_URL } else { 'http://192.168.1.10:8080/v1' }
    $aiKey = if ($env:ATHAR_AI_API_KEY) { $env:ATHAR_AI_API_KEY } else { 'none' }
    @"
# Local Docker stack (deploy/local.ps1). Git-ignored: holds generated secrets. Delete it with 'reset' to start over.
ATHAR_IMAGE_PREFIX=athar-local
ATHAR_TAG=dev
ATHAR_SEED=$seed
POSTGRES_PASSWORD=$(Secret)
COLLAB_SECRET=$(Secret)
ATHAR_APP_URL=http://localhost:$port
ATHAR_MCP_URL=http://localhost:$($port + 1)
ATHAR_SHARE_URL=http://localhost:$($port + 2)
ATHAR_LAN_PORT=$port
ATHAR_MCP_PORT=$($port + 1)
ATHAR_SHARE_PORT=$($port + 2)
ATHAR_WEB_PORT=$($port + 3)
ATHAR_MAILPIT_PORT=$($port + 5)
ATHAR_AI_BASE_URL=$aiUrl
ATHAR_AI_API_KEY=$aiKey
ATHAR_SMTP_HOST=mailpit
ATHAR_SMTP_PORT=1025
ATHAR_SMTP_STARTTLS=false
ATHAR_SMTP_FROM=athar@localhost
# The backup service is off locally (profile never), but Compose still checks these.
BACKUP_AGE_RECIPIENT=unused
BACKUP_S3_ENDPOINT=unused
BACKUP_S3_BUCKET=unused
BACKUP_S3_ACCESS_KEY_ID=unused
BACKUP_S3_SECRET_ACCESS_KEY=unused
"@ | Set-Content -Path $envFile -Encoding utf8NoBOM
    Write-Host "Created $envFile"
}

switch ($Command) {
    'up' {
        if (-not (Test-Path $envFile)) { New-Env }
        Compose up -d --build --wait
        Write-Host ''
        Write-Host 'Athar is running:'
        Write-Host "  App       $(Value ATHAR_APP_URL)"
        Write-Host "  Agents    $(Value ATHAR_MCP_URL)/mcp  (setup: $(Value ATHAR_MCP_URL)/agent/setup.ps1)"
        Write-Host "  Mailpit   http://localhost:$(Value ATHAR_MAILPIT_PORT)"
        if ((Value ATHAR_SEED) -eq 'qa') {
            Write-Host '  Sign in   qa.admin@qa.athar.local / Qa-password-1!  (any qa user, e.g. qa.member@qa.athar.local)'
        } else {
            Write-Host "  Owner     docker $($compose -join ' ') run --rm web bootstrap --email you@example.com"
        }
        if ((Value ATHAR_AI_API_KEY) -eq 'none') { Write-Host '  AI        no ATHAR_AI_API_KEY: search is degraded (keyword only)' }
    }
    'down' { Compose down }
    'reset' { Compose down -v; Remove-Item -Force -ErrorAction SilentlyContinue $envFile; Write-Host "Local data and $envFile removed." }
    'logs' { Compose logs -f --tail 100 }
    'ps' { Compose ps }
}
