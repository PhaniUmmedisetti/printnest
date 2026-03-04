param()

$ErrorActionPreference = "Stop"

$backendRoot = Split-Path -Parent $PSScriptRoot
$sessionFile = Join-Path $backendRoot ".dev-session.json"

if (Test-Path $sessionFile) {
    $session = Get-Content $sessionFile | ConvertFrom-Json

    if ($session.frontendPid) {
        Stop-Process -Id $session.frontendPid -ErrorAction SilentlyContinue
    }

    Remove-Item $sessionFile -Force
}

docker compose --env-file infra/.env down

Write-Host "Backend containers stopped."
Write-Host "Frontend background process stopped."
