# start-all.ps1 - starts Innovia IoT in the right order
# Usage: powershell -ExecutionPolicy Bypass -File .\start-all.ps1 [-Rules] [-Simulator]
param(
    [switch]$Rules,      # also start Rules.Engine
    [switch]$Simulator   # also start Edge.Simulator (register the devices first!)
)

$root = $PSScriptRoot
$compose = Join-Path $root 'deploy/docker-compose.yml'

# Waits until something answers on a TCP port, or gives up after $TimeoutSec
function Wait-Port {
    param([string]$Name, [int]$Port, [int]$TimeoutSec = 90)
    Write-Host "Waiting for $Name on port $Port ..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $client = New-Object System.Net.Sockets.TcpClient
            $client.Connect('localhost', $Port)
            $client.Close()
            Write-Host "  OK: $Name is up" -ForegroundColor Green
            return
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "$Name did not answer on port $Port within $TimeoutSec seconds"
}

# Opens a new PowerShell window that runs one service from its own folder
function Start-IoTService {
    param([string]$Name, [string]$Project)
    $dir = Join-Path $root "src/$Project"
    Write-Host "Starting $Name ..."
    Start-Process powershell -WorkingDirectory $dir -ArgumentList @(
        '-NoExit', '-Command',
        "`$Host.UI.RawUI.WindowTitle = '$Name'; dotnet run --no-build"
    )
}

# 1. Docker: PostgreSQL, Redis, Mosquitto
Write-Host 'Starting Docker containers (db, redis, mosquitto) ...'
docker compose -f $compose up -d
if ($LASTEXITCODE -ne 0) { throw 'docker compose failed. Is Docker Desktop running?' }

Write-Host 'Waiting for PostgreSQL ...'
$tries = 0
do {
    Start-Sleep -Seconds 1
    $tries++
    $null = docker compose -f $compose exec -T db pg_isready -U postgres
} while ($LASTEXITCODE -ne 0 -and $tries -lt 60)
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL did not become ready' }
Write-Host '  OK: PostgreSQL is ready' -ForegroundColor Green
Wait-Port 'Mosquitto' 1883

# 2. Build everything once, so the services don't build Innovia.Shared at the same time
Write-Host 'Building solution ...'
dotnet build (Join-Path $root 'Innovia.IoT.sln')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

# 3. Services in order - each one waits for the previous to be up
Start-IoTService 'DeviceRegistry' 'DeviceRegistry.Api'; Wait-Port 'DeviceRegistry' 5101
Start-IoTService 'Realtime.Hub'   'Realtime.Hub';       Wait-Port 'Realtime.Hub'   5103
Start-IoTService 'Ingest'         'Ingest.Gateway';     Wait-Port 'Ingest'         5102
Start-IoTService 'Portal.Adapter' 'Portal.Adapter';     Wait-Port 'Portal.Adapter' 5104

if ($Rules)     { Start-IoTService 'Rules.Engine'   'Rules.Engine';   Wait-Port 'Rules.Engine' 5000 }
if ($Simulator) { Start-IoTService 'Edge.Simulator' 'Edge.Simulator' }

Write-Host ''
Write-Host 'Innovia IoT is running:' -ForegroundColor Green
Write-Host '  DeviceRegistry  http://localhost:5101/swagger'
Write-Host '  Ingest          http://localhost:5102/swagger'
Write-Host '  Realtime.Hub    http://localhost:5103/hub/telemetry'
Write-Host '  Portal.Adapter  http://localhost:5104/swagger'
Write-Host 'Stop: close the service windows, then run: docker compose -f deploy/docker-compose.yml stop'