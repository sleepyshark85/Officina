#!/usr/bin/env sh
# Starts what Bookshop Assistant needs, or leaves it running if it already is: the database, the telemetry dashboard and
# the export server, from compose.yaml beside this script. Another port if one is taken: see compose.yaml.
set -eu
cd "$(dirname "$0")"

if ! docker info >/dev/null 2>&1; then
    echo "Docker is not running, or this user cannot reach it." >&2
    exit 1
fi

# --build rebuilds the export server's image only when its Dockerfile changed.
docker compose up --detach --wait --build

echo
echo "Database:      localhost:${BOOKSHOP_DB_PORT:-5432}"
echo "Dashboard:     http://localhost:${BOOKSHOP_DASHBOARD_PORT:-18888}"
echo "Export server: http://localhost:${BOOKSHOP_EXPORTS_PORT:-18800}/mcp"
