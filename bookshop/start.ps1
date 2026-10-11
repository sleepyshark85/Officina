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

# The host port each service got, as compose.yaml and .env set it.
function Port($service, $port) { ((docker compose port $service $port) -split ':')[-1] }
Write-Host ''
Write-Host "Database:      localhost:$(Port postgres 5432)"
Write-Host "Dashboard:     http://localhost:$(Port dashboard 18888)"
Write-Host "Export server: http://localhost:$(Port filesystem 8000)/mcp"
