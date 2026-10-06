# Starts what Bookshop Assistant needs, or leaves it running if it already is: the database, the telemetry dashboard and
# the export server, from compose.yaml beside this script. Another port if one is taken: see compose.yaml.
# No $ErrorActionPreference = 'Stop': Windows PowerShell would then stop at any line docker writes to stderr.
Set-Location $PSScriptRoot

docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Docker is not running, or this user cannot reach it.' -ForegroundColor Red
    exit 1
}

# --build rebuilds the export server's image only when its Dockerfile changed.
docker compose up --detach --wait --build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$db = if ($env:BOOKSHOP_DB_PORT) { $env:BOOKSHOP_DB_PORT } else { 5432 }
$dashboard = if ($env:BOOKSHOP_DASHBOARD_PORT) { $env:BOOKSHOP_DASHBOARD_PORT } else { 18888 }
$exports = if ($env:BOOKSHOP_EXPORTS_PORT) { $env:BOOKSHOP_EXPORTS_PORT } else { 18800 }
Write-Host ''
Write-Host "Database:      localhost:$db"
Write-Host "Dashboard:     http://localhost:$dashboard"
Write-Host "Export server: http://localhost:$exports/mcp"
