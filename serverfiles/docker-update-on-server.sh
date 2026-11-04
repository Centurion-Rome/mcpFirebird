#!/bin/sh
set -u
cd "$(dirname "$0")" || exit 1
[ -f ".env" ] || { echo "ERROR: Update failed. Check .env, Docker and both image archives."; exit 1; }
[ -f "mcp-firebird_latest.tar" ] || { echo "ERROR: Update failed. Check .env, Docker and both image archives."; exit 1; }
[ -f "nginx_stable-alpine.tar" ] || { echo "ERROR: Update failed. Check .env, Docker and both image archives."; exit 1; }
echo "Loading image..."
docker compose -f "docker-compose.yaml" config --quiet || { echo "ERROR: Update failed. Check .env, Docker and both image archives."; exit 1; }
docker load -i "mcp-firebird_latest.tar" || { echo "ERROR: Update failed. Check .env, Docker and both image archives."; exit 1; }
docker load -i "nginx_stable-alpine.tar" || { echo "ERROR: Update failed. Check .env, Docker and both image archives."; exit 1; }
docker compose -f "docker-compose.yaml" up -d --no-build --pull never --force-recreate || { echo "ERROR: Update failed. Check .env, Docker and both image archives."; exit 1; }
echo "DONE: Health: http://localhost:8182/health  MCP endpoint: http://localhost:8182/mcp"
exit 0
