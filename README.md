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

### Claude Code permissions
`.claude/settings.json` (project) / `.claude/settings.local.json` (uncommitted) / `~/.claude/settings.json` (global):
```json
{
	"permissions": {
		"allow": ["mcp__firebird__*"]
	}
}
```
Per tool:
```json
{
	"permissions": {
		"allow": [
			"mcp__firebird__list_databases",
			"mcp__firebird__list_tables",
			"mcp__firebird__describe_table",
			"mcp__firebird__execute_query"
		]
	}
}
```
`deny` and `ask` use the same keys; also manageable interactively via `/permissions`.

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

### opencode permissions
Same file as above (`opencode.json(c)`); all rules apply to both config locations. Ordered rules, last match wins:
```jsonc
{
	"$schema": "https://opencode.ai/config.json",
	"permissions": [
		{ "action": "firebird_*", "resource": "*", "effect": "allow" }
	]
}
```
Per tool (e.g. always ask for queries, auto-allow metadata):
```jsonc
{
	"permissions": [
		{ "action": "firebird_list_databases", "resource": "*", "effect": "allow" },
		{ "action": "firebird_list_tables", "resource": "*", "effect": "allow" },
		{ "action": "firebird_describe_table", "resource": "*", "effect": "allow" },
		{ "action": "firebird_execute_query", "resource": "*", "effect": "ask" }
	]
}
```
Effects: `allow` (no prompt), `ask` (wait for decision), `deny` (block). Config reloads on save, no restart needed (`/reload` to force).
