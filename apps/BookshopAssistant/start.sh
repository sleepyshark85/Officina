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

# The host port each service got, as compose.yaml and .env set it.
port() { address=$(docker compose port "$1" "$2"); echo "${address##*:}"; }
echo
echo "Database:      localhost:$(port postgres 5432)"
echo "Dashboard:     http://localhost:$(port dashboard 18888)"
echo "Export server: http://localhost:$(port filesystem 8000)/mcp"
