# mcp-firebird

MCP server (C#, streamable HTTP) giving read-only access to one or more Firebird 5 databases (local or remote).

Tools: `list_databases`, `list_tables`, `describe_table`, `execute_query` (read-only transaction, max 1000 rows).

## Configuration
All settings live in `.env` (template: `.env.example`):
- `FIREBIRD_*` → database `default`
- `Databases__<name>__Host/Port/Database/User/Password/WireCrypt/Charset` → further databases
- `FIREBIRD_API_KEY` → clients send `Authorization: Bearer <token>`
- `MCP_ALLOWED_HOSTS`, `MCP_BIND`, `MCP_PORT`

Local Firebird on the docker host: `FIREBIRD_HOST=host.docker.internal`.

## Build & deploy
1. `build-and-dockerize.bat` – builds, copies `.env` to `serverfiles/`, exports images to `serverfiles/*.tar`
2. Copy `serverfiles/` to the server, run `docker-update-on-server.bat` (later: `docker-just-restart.bat`)

Health: `http://localhost:8182/health`, MCP endpoint: `http://localhost:8182/mcp`

## Client config (e.g. Claude Code)
```
claude mcp add --transport http firebird http://localhost:8182/mcp --header "Authorization: Bearer <token>"
```

## Client config (opencode)
`opencode.json` (project root or `~/.config/opencode/opencode.json`):
```json
{
	"$schema": "https://opencode.ai/config.json",
	"mcp": {
		"firebird": {
			"type": "remote",
			"url": "http://localhost:8182/mcp",
			"enabled": true,
			"headers": {
				"Authorization": "Bearer <token>"
			}
		}
	}
}
```
