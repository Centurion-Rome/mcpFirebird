@echo off
setlocal DisableDelayedExpansion
pushd "%~dp0" || exit /b 1
if not exist ".env" goto failed
call dotnet build "McpFirebird.sln" -c Release || goto failed
call docker info >nul 2>&1 || goto failed
copy /y ".env" "serverfiles\.env" >nul || goto failed
call docker compose -f "serverfiles\docker-compose.yaml" config --quiet || goto failed
call docker build -t mcp-firebird:latest -f "Dockerfile" . || goto failed
call docker pull nginx:stable-alpine || goto failed
call docker save -o "serverfiles\mcp-firebird_latest.tar" mcp-firebird:latest || goto failed
call docker save -o "serverfiles\nginx_stable-alpine.tar" nginx:stable-alpine || goto failed
echo DONE: Copy the entire serverfiles folder to the server (contains .env with secrets).
popd
exit /b 0
:failed
echo ERROR: Build or export failed (.env missing?). Do not deploy this bundle.
popd
exit /b 1
