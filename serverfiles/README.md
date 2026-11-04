# mcp-firebird

MCP server (C#, streamable HTTP) for one or more Firebird 5 databases (local or remote). Read-only by default, with five independently enabled write tools.

Read tools: `list_databases`, `list_tables`, `describe_table`, `execute_query` (unchanged read-only transaction, max 1000 rows).

Write tools: `insert_rows`, `update_rows`, `delete_rows`, `create_table`, `drop_table`. They remain discoverable when disabled, but the server rejects their execution before opening a connection. All advertise `readOnlyHint: false` and `destructiveHint: true`; these are hints, **not permission enforcement**.

## Configuration
Deployment settings live in `.env` (template: `.env.example`); Docker Compose passes them as environment variables. Direct `dotnet` runs need exported environment variables or .NET configuration; the app does not itself load `.env`.
- `FIREBIRD_*` → database `default`
- `Databases__<name>__Host/Port/Database/User/Password/WireCrypt/Charset` → further databases
- `FIREBIRD_API_KEY` → clients send `Authorization: Bearer <token>`
- `MCP_ALLOWED_HOSTS`, `MCP_BIND`, `MCP_PORT`

Named database settings take precedence if `Databases__default__...` and `FIREBIRD_*` both configure `default`. Other named databases do not inherit the default database's flags or credentials. Flags accept `true`/`false`; absent means `false`, malformed values fail startup. Restart/redeploy the server after configuration changes.

Local Firebird on the docker host: `FIREBIRD_HOST=host.docker.internal`.

### Enable only the required operations

| Tool | Default database environment flag | Named database setting |
| --- | --- | --- |
| `insert_rows` | `FIREBIRD_ALLOW_INSERT_ROWS` | `Databases__<name>__AllowInsertRows` |
| `update_rows` | `FIREBIRD_ALLOW_UPDATE_ROWS` | `Databases__<name>__AllowUpdateRows` |
| `delete_rows` | `FIREBIRD_ALLOW_DELETE_ROWS` | `Databases__<name>__AllowDeleteRows` |
| `create_table` | `FIREBIRD_ALLOW_CREATE_TABLE` | `Databases__<name>__AllowCreateTable` |
| `drop_table` | `FIREBIRD_ALLOW_DROP_TABLE` | `Databases__<name>__AllowDropTable` |

Every flag defaults to `false`. For example, to permit inserts only, set `FIREBIRD_ALLOW_INSERT_ROWS=true`, leave the other four false, and provide:

```dotenv
FIREBIRD_WRITER_USER=mcp_writer
FIREBIRD_WRITER_PASSWORD=replace_with_writer_password
FIREBIRD_API_KEY=replace_with_a_random_secret_token
```

Named databases use `Databases__<name>__WriterUser` and `Databases__<name>__WriterPassword`. Writer connections use the same host/path/charset/wire-encryption settings but **never the read account's credentials**. Startup fails if any enabled database lacks a nonblank API key or writer credentials, or its writer is the read user or `SYSDBA` (case-insensitive). The server does not create users or grant privileges.

Three independent controls apply: **server flag + Firebird grants + client permission**. A client approval cannot enable a server-disabled operation or grant a database privilege. A server flag does not force a client prompt: configure the client examples below. Anyone holding the shared API key can call all server-enabled operations without those client prompts; this is not per-user authorization. Use separate deployments/keys for separate trust groups.

### Least-privilege Firebird accounts

- Use a dedicated read user with only required SELECT grants; never use SYSDBA or the database owner for reads. Do not grant executable procedures/functions that can write through autonomous transactions or external connections.
- Give the separate writer only the required INSERT, UPDATE and/or DELETE grants on approved tables (and SELECT on predicate columns/tables as Firebird requires). Flags restrict statement types, **not which tables**: grants must restrict the tables. Do not grant `RDB$ADMIN` or other administrative roles.
- `create_table` needs Firebird's `CREATE TABLE` privilege. Newly created objects are owned by the creator, so that account has broader rights on its own objects. `drop_table` requires ownership or appropriate `DROP ANY TABLE` authority; avoid the latter on production databases. DDL is inherently broader than row-level grants. Use a separate database/deployment if DDL needs a different trust boundary.
- Review existing DML/DDL triggers, cascades and database routines before enabling writes. Flags constrain direct statements, not indirect trigger/cascade actions. `expectedRows` counts directly affected rows only. Autonomous transactions, sequence increments and external side effects cannot be undone by rolling back this transaction.
- Keep a tested backup before enabling deletion or DDL. Use TLS before exposing the endpoint beyond localhost; Firebird WireCrypt protects a different connection, not the MCP HTTP token. Never commit `.env` or deploy bundles containing secrets.

## Structured write inputs and limits

All writes require `database` and `table`; names are exact, case-sensitive and quoted by the server. Use uppercase for ordinary unquoted Firebird names. Supported identifiers are 1-63 ASCII letters/digits/`_`/`$`, starting with a letter or `_`, excluding `RDB$`, `MON$`, `SEC$` prefixes. Names containing spaces, quotes or non-ASCII characters are deliberately unsupported. Only ordinary persistent user tables are writable; system tables, views, temporary and external tables are rejected in the write transaction.

- `insert_rows`: `rows` is an array of 1-1000 nonempty column/value objects. Omitted columns use database defaults. `expectedRows` must equal its length. Every row is executed in **one transaction**; on failure the server attempts to roll back the batch.
- `update_rows`: `values` is a nonempty column/value object; `where` is a nonempty column/value object.
- `delete_rows`: `where` is a nonempty column/value object. No truncate, unfiltered delete or arbitrary SQL.
- All DML requires integer `expectedRows` from 1 to 1000. Update/delete predicates combine **equality with AND**, with JSON `null` translated to `IS NULL`. No operators, OR, ranges or SQL expressions. After execution, the directly affected count must equal `expectedRows`, otherwise commit is not attempted and rollback is attempted. This checks actual execution, not a racy count-then-write. It is not a limit on rows scanned/temporarily modified; keep predicates selective. Include known old values in `where` when optimistic concurrency protection is needed.
- Column/value and predicate objects allow 1-128 distinct columns. Values are parameterized JSON null/boolean/string/number only (exactly representable as Int64 or Decimal; strings up to 32765 .NET characters). Arrays, nested objects and numbers requiring rounding/underflow are rejected. Database column types and constraints still apply. Use ISO date/time strings for conversion by Firebird; binary/BLOB/array writes and expression values are not supported.
- `create_table`: `columns` is an array of 1-128 definitions with string `name` and exact uppercase `type`. Supported types: `SMALLINT`, `INTEGER`, `BIGINT`, `BOOLEAN`, `DATE`, `TIME`, `TIMESTAMP`, `DOUBLE PRECISION`, `CHAR`, `VARCHAR`, `DECIMAL`. `CHAR`/`VARCHAR` require integer `length` (1-8191), always UTF8; `DECIMAL` requires `precision` (1-18) and `scale` (0-precision). Optional `nullable` defaults to true; `primaryKey` defaults to false and forces NOT NULL. Multiple primary-key columns form one composite key. Unknown/duplicate properties and inapplicable type options are rejected. Firebird also enforces record/index size limits. No default expressions, raw type SQL, computed columns, foreign keys, triggers, domains or replacement of existing relations.
- `drop_table`: only `database` and `table`. No `CASCADE` or `IF EXISTS`; dependent objects can cause failure. A committed drop is permanent, including all table data; restore requires a backup.
- Unknown top-level write arguments are rejected. DDL uses its own transaction, separate from DML; there is no mixed-operation batch. Transactions use snapshot isolation and no-wait conflict handling, with a 30-second timeout per command. Concurrent calls can fail on conflicts; there are no automatic retries. Rollback cleanup is best-effort and does not replace the original error/cancellation if it fails. An error is not proof that rollback succeeded; connection loss or cancellation during commit can leave an uncertain outcome: **verify before retrying**, especially inserts. Write errors omit provider details to avoid leaking paths or values.

Example tool arguments (each operation needs its own approval/flag):

```json
{"database":"default","table":"MCP_NOTES","columns":[{"name":"ID","type":"INTEGER","primaryKey":true},{"name":"TITLE","type":"VARCHAR","length":100}]}
```

Call `create_table` with the above, then `insert_rows` with:

```json
{"database":"default","table":"MCP_NOTES","rows":[{"ID":1,"TITLE":"Draft"}],"expectedRows":1}
```

`update_rows`:

```json
{"database":"default","table":"MCP_NOTES","values":{"TITLE":"Approved"},"where":{"ID":1,"TITLE":"Draft"},"expectedRows":1}
```

`delete_rows`:

```json
{"database":"default","table":"MCP_NOTES","where":{"ID":1},"expectedRows":1}
```

`drop_table`:

```json
{"database":"default","table":"MCP_NOTES"}
```

## Build & deploy
1. `build-and-dockerize.bat` – builds, copies `.env` to `serverfiles/`, exports images to `serverfiles/*.tar`
2. Copy `serverfiles/` to the server, run `docker-update-on-server.bat` (later: `docker-just-restart.bat`)

Health: `http://localhost:8182/health`, MCP endpoint: `http://localhost:8182/mcp`

## Claude Code: connection and permissions

Project `.mcp.json` (set `FIREBIRD_API_KEY` in the client's environment; do not paste the secret into this file):

```json
{
	"mcpServers": {
		"firebird": {
			"type": "http",
			"url": "http://localhost:8182/mcp",
			"headers": { "Authorization": "Bearer ${FIREBIRD_API_KEY}" }
		}
	}
}
```

Alternatively, `claude mcp add --transport http firebird http://localhost:8182/mcp --header "Authorization: Bearer <token>"` writes a local-scope connection; `--scope user` makes it global and `--scope project` shares it. The literal-token CLI form can expose the token in shell history/config; prefer environment substitution above. Approve project server trust when prompted, check `claude mcp list` or `/mcp`. Approving a connection is not the same as approving writes.

Set `.claude/settings.json` (project), `.claude/settings.local.json` (uncommitted) or `~/.claude/settings.json` (global). This example auto-allows only the four existing read tools and asks independently for all five writes:

```json
{
	"permissions": {
		"defaultMode": "default",
		"allow": [
			"mcp__firebird__list_databases",
			"mcp__firebird__list_tables",
			"mcp__firebird__describe_table",
			"mcp__firebird__execute_query"
		],
		"ask": [
			"mcp__firebird__insert_rows",
			"mcp__firebird__update_rows",
			"mcp__firebird__delete_rows",
			"mcp__firebird__create_table",
			"mcp__firebird__drop_table"
		]
	}
}
```

or 

```json
{ "action": "firebird_list_databases", "resource": "*", "effect": "allow" },
  { "action": "firebird_list_tables", "resource": "*", "effect": "allow" },
  { "action": "firebird_describe_table", "resource": "*", "effect": "allow" },
  { "action": "firebird_execute_query", "resource": "*", "effect": "allow" },

  { "action": "firebird_insert_rows", "resource": "*", "effect": "ask" },
  { "action": "firebird_update_rows", "resource": "*", "effect": "ask" },
  { "action": "firebird_delete_rows", "resource": "*", "effect": "ask" },
  { "action": "firebird_create_table", "resource": "*", "effect": "ask" },
  { "action": "firebird_drop_table", "resource": "*", "effect": "ask" }
```

To forbid an operation, move its exact name to `permissions.deny`; to auto-approve one deliberately, move it to `allow`. Rules resolve **deny, then ask, then allow**; a broad deny cannot have a narrow allow exception. Use `/permissions` to inspect effective rules. Do not use bypass/auto-approval modes if manual approval is required, and do not save blanket server approvals. MCP tool rules do not provide per-table authorization; use database grants. Renaming the connection changes the `mcp__firebird__` prefix.

## OpenCode V2: connection and permissions

Use project `opencode.json(c)` or global `~/.config/opencode/opencode.json(c)`. V2 uses **`mcp.servers`**, not server names directly under `mcp`; connections are enabled by default (`disabled: true` disables one). This server uses a bearer key, not OAuth. Set the key in the client's environment:
```json
{
	"$schema": "https://opencode.ai/config.json",
	"mcp": {
		"servers": {
			"firebird": {
				"type": "remote",
				"url": "http://localhost:8182/mcp",
				"oauth": false,
				"headers": { "Authorization": "Bearer {env:FIREBIRD_API_KEY}" }
			}
		}
	},
	"permissions": [
		{ "action": "firebird_*", "resource": "*", "effect": "deny" },
		{ "action": "firebird_list_databases", "resource": "*", "effect": "allow" },
		{ "action": "firebird_list_tables", "resource": "*", "effect": "allow" },
		{ "action": "firebird_describe_table", "resource": "*", "effect": "allow" },
		{ "action": "firebird_execute_query", "resource": "*", "effect": "allow" },
		{ "action": "firebird_insert_rows", "resource": "*", "effect": "ask" },
		{ "action": "firebird_update_rows", "resource": "*", "effect": "ask" },
		{ "action": "firebird_delete_rows", "resource": "*", "effect": "ask" },
		{ "action": "firebird_create_table", "resource": "*", "effect": "ask" },
		{ "action": "firebird_drop_table", "resource": "*", "effect": "ask" }
	]
}
```
Rules are ordered; **last match wins**. The first deny protects future tools; exact exceptions follow. Change an individual effect to `deny` to forbid it, or `allow` to deliberately stop its prompts. MCP resources are `*` for tool permission checks, not table names. The action prefix is the configured server name (`firebird_`). Avoid later broad allow rules and review agent-specific rules and durable saved approvals; choose **Allow once**, not **Allow always**, for manual per-call approval. Code Mode is enabled by default and still enforces each nested tool's permission; no separate blanket `execute` allowance is needed here.

Config reloads on save (`/reload` forces it). Check `opencode mcp list` or `/mcps`. A higher-precedence config with the same server name replaces that server object, so retain all connection fields in overrides.

**Do not auto-allow `firebird_*` or `mcp__firebird__*`.** Older examples did so; remove those rules/saved approvals before enabling writes. Exact read-tool allows do not silently approve future write tools. Use `ask` for `execute_query` too if queries expose sensitive data.

References: [OpenCode V2 MCP](https://opencode.ai/v2/docs/mcp-servers), [V2 permissions](https://opencode.ai/v2/docs/permissions), [Claude Code MCP](https://code.claude.com/docs/en/mcp), [Claude Code permissions](https://code.claude.com/docs/en/permissions).

## Validation

```text
dotnet build McpFirebird.sln -c Release
dotnet run --project checks/McpFirebird.Checks.csproj -c Release
git diff --check
```

The dependency-free checks validate SQL generation, rejected inputs, independent gates/configuration and MCP annotations, plus loopback HTTP authentication, discovery and rejected calls using dummy configuration, without connecting to any database. Before production enablement, validate commits, rollback on row-count mismatch/batch failure, cancellation, metadata restrictions and least-privilege grants against a **disposable Firebird 5 database**, never production.

## Where to set `FIREBIRD_API_KEY`

Server side: `.env` (and the deployed `serverfiles/.env`) → `FIREBIRD_API_KEY=<token>`.

Client side (OpenCode): `~/.config/opencode/opencode.json` (or project `opencode.json(c)`) → `mcp.<name>.headers.Authorization = "Bearer <token>"` (or `"Bearer {env:FIREBIRD_API_KEY}"` with the env var set in OpenCode's process). Replace the placeholder `"Bearer <token>"`.

Client side (Claude Code): `.mcp.json` → `headers: { "Authorization": "Bearer ${FIREBIRD_API_KEY}" }`.

Server and all clients must use the **same** token (byte-identical; comparison is against `Bearer <key>`).
