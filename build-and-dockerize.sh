#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env ] || { echo "ERROR: .env missing. Do not deploy this bundle."; exit 1; }
dotnet build "McpFirebird.sln" -c Release || { echo "ERROR: Build or export failed (.env missing?). Do not deploy this bundle."; exit 1; }
docker info >/dev/null 2>&1 || { echo "ERROR: Docker not reachable. Do not deploy this bundle."; exit 1; }
cp -f .env serverfiles/.env
docker compose -f serverfiles/docker-compose.yaml config --quiet || { echo "ERROR: Build or export failed (.env missing?). Do not deploy this bundle."; exit 1; }
docker build -t mcp-firebird:latest -f Dockerfile .
docker pull nginx:stable-alpine
docker save -o serverfiles/mcp-firebird_latest.tar mcp-firebird:latest
docker save -o serverfiles/nginx_stable-alpine.tar nginx:stable-alpine
echo "DONE: Copy the entire serverfiles folder to the server (contains .env with secrets)."
