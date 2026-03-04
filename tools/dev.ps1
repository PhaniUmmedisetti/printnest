param(
    [string]$FrontendPath
)

$ErrorActionPreference = "Stop"

$backendRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $backendRoot
$envFile = Join-Path $backendRoot "infra\.env"
$sessionFile = Join-Path $backendRoot ".dev-session.json"

if (-not $FrontendPath) {
    $FrontendPath = Join-Path $workspaceRoot "printnest-staff-pwa"
}

function Get-PrimaryIpv4Address {
    $primaryConfig = Get-NetIPConfiguration |
        Where-Object { $_.IPv4DefaultGateway -ne $null -and $_.NetAdapter.Status -eq "Up" } |
        Select-Object -First 1

    if ($primaryConfig -and $primaryConfig.IPv4Address) {
        return $primaryConfig.IPv4Address.IPAddress
    }

    $fallback = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -ne "127.0.0.1" -and $_.IPAddress -notlike "169.254*" } |
        Select-Object -First 1

    return $fallback?.IPAddress
}

if (-not (Test-Path $envFile)) {
    throw "Missing infra\.env. Copy infra\.env.example to infra\.env and fill in the required secrets first."
}

if (-not (Test-Path $FrontendPath)) {
    throw "Frontend repo not found at '$FrontendPath'. Pass -FrontendPath if your staff PWA repo is elsewhere."
}

if (-not (Test-Path (Join-Path $FrontendPath "package.json"))) {
    throw "Frontend repo at '$FrontendPath' does not contain package.json."
}

if (Test-Path $sessionFile) {
    throw "Existing dev session found. Run .\tools\stop-dev.cmd first."
}

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) {
    throw "Node.js is not available on PATH."
}

$viteCliPath = Join-Path $FrontendPath "node_modules\vite\bin\vite.js"
if (-not (Test-Path $viteCliPath)) {
    throw "Missing Vite CLI at '$viteCliPath'. Run npm install in the staff PWA repo first."
}

$lanIp = Get-PrimaryIpv4Address
$corsOrigins = @("http://localhost:3000")
if ($lanIp) {
    $corsOrigins += "http://${lanIp}:3000"
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$frontendLog = Join-Path $backendRoot ".dev-frontend-$timestamp.log"
$frontendErrorLog = Join-Path $backendRoot ".dev-frontend-$timestamp.err.log"

$frontendProcess = Start-Process -FilePath $nodeCommand.Source -ArgumentList $viteCliPath -WorkingDirectory $FrontendPath -RedirectStandardOutput $frontendLog -RedirectStandardError $frontendErrorLog -WindowStyle Hidden -PassThru

@{
    frontendPath = $FrontendPath
    frontendPid = $frontendProcess.Id
    frontendLog = $frontendLog
    frontendErrorLog = $frontendErrorLog
    startedAtUtc = [DateTime]::UtcNow.ToString("O")
    lanIp = $lanIp
} | ConvertTo-Json | Set-Content $sessionFile

Write-Host "Frontend started in background: http://localhost:3000"
if ($lanIp) {
    Write-Host "Phone frontend URL: http://${lanIp}:3000"
}
Write-Host "Frontend log: $frontendLog"
Write-Host "Press Ctrl+C to stop the backend later, then run .\tools\stop-dev.cmd to clean up the frontend process."

$env:CORS_ALLOWED_ORIGINS = $corsOrigins -join ","
Set-Location $backendRoot
docker compose --env-file infra/.env up --build
