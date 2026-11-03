@echo off
echo Loading image...
setlocal DisableDelayedExpansion
pushd "%~dp0" || exit /b 1
if not exist ".env" goto failed
if not exist "mcp-firebird_latest.tar" goto failed
if not exist "nginx_stable-alpine.tar" goto failed
call docker compose -f "docker-compose.yaml" config --quiet || goto failed
call docker load -i "mcp-firebird_latest.tar" || goto failed
call docker load -i "nginx_stable-alpine.tar" || goto failed
call docker compose -f "docker-compose.yaml" up -d --no-build --pull never --force-recreate || goto failed
echo DONE: Health: http://localhost:8182/health  MCP endpoint: http://localhost:8182/mcp
popd
exit /b 0
:failed
echo ERROR: Update failed. Check .env, Docker and both image archives.
popd
exit /b 1
