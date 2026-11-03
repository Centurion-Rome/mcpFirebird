@echo off
setlocal DisableDelayedExpansion
pushd "%~dp0" || exit /b 1
if not exist ".env" goto failed
call docker compose -f "docker-compose.yaml" up -d --no-build --pull never --force-recreate || goto failed
echo DONE: Health: http://localhost:8182/health  MCP endpoint: http://localhost:8182/mcp
popd
exit /b 0
:failed
echo ERROR: Restart failed. Check .env and Docker.
popd
exit /b 1
