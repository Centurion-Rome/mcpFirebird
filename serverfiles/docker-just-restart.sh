#!/bin/sh
set -u
cd "$(dirname "$0")" || exit 1
[ -f ".env" ] || { echo "ERROR: Restart failed. Check .env and Docker."; exit 1; }
docker compose -f "docker-compose.yaml" up -d --no-build --pull never --force-recreate || { echo "ERROR: Restart failed. Check .env and Docker."; exit 1; }
echo "DONE: Health: http://localhost:8182/health  MCP endpoint: http://localhost:8182/mcp"
exit 0
