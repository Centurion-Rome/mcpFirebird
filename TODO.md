# TODO

- **Security: empty `FIREBIRD_API_KEY`** – MCP endpoint is then unprotected (only a warning is logged). Current `.env` has it empty → set a token before binding to anything other than 127.0.0.1.
- **Security: no TLS** – nginx serves plain HTTP. Add TLS (cert in nginx) before exposing `MCP_BIND` beyond localhost.
- **Security: use a read-only Firebird user** – queries run in a read-only transaction and are always rolled back, but procedures with `AUTONOMOUS TRANSACTION` / `EXECUTE STATEMENT ON EXTERNAL` may still write. A DB user with SELECT-only grants is the real protection (SYSDBA is used currently).
- **Secrets in deploy bundle** – `build-and-dockerize.bat` copies `.env` (passwords) into `serverfiles/`; treat that folder as secret.
- Licenses: `ModelContextProtocol.AspNetCore` (Apache-2.0), `FirebirdSql.Data.FirebirdClient` (IDPL) – both free for commercial use, no MIT-licensed alternative exists.
