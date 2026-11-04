using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpFirebird;

[McpServerToolType]
public sealed class FirebirdWriteTools(FirebirdWriteService firebird)
{
	internal static void ValidateArguments(CallToolRequestParams request)
	{
		string[]? names = request.Name switch
		{
			"insert_rows" => ["database", "table", "rows", "expectedRows"],
			"update_rows" => ["database", "table", "values", "where", "expectedRows"],
			"delete_rows" => ["database", "table", "where", "expectedRows"],
			"create_table" => ["database", "table", "columns"],
			"drop_table" => ["database", "table"],
			_ => null,
		};
		if (names is null) return;
		if (request.Arguments is not { } arguments || arguments.Count != names.Length
			|| names.Any(name => !arguments.ContainsKey(name))
			|| arguments["database"].ValueKind != JsonValueKind.String || arguments["table"].ValueKind != JsonValueKind.String)
			throw new McpException("Write tool arguments must contain exactly the documented required properties, with string database and table.");
		if (arguments.TryGetValue("expectedRows", out var expected) && (expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt32(out _)))
			throw new McpException("expectedRows must be a JSON integer.");
	}

	[McpServerTool(Name = "insert_rows", ReadOnly = false, Destructive = true), Description("Inserts 1-1000 rows atomically using the separately configured writer. Disabled unless the server enables insert_rows. JSON values are scalars, never SQL expressions. Requires user approval in the client.")]
	public Task<string> InsertRows(
		[Description("Configured database name")] string database,
		[Description("Exact case-sensitive ordinary user table name")] string table,
		[Description("Nonempty array of objects mapping exact column names to JSON scalars; omitted columns use database defaults")] JsonElement rows,
		[Description("Expected inserted row count, 1-1000; must equal rows length; mismatch rolls back the entire transaction")] int expectedRows,
		CancellationToken ct) => firebird.ExecuteAsync(database, WriteOperation.InsertRows, () => WriteSql.Insert(table, rows, expectedRows), ct);

	[McpServerTool(Name = "update_rows", ReadOnly = false, Destructive = true), Description("Updates rows using AND-equality predicates; null means IS NULL. No SQL expressions or unfiltered updates. Disabled unless server-enabled; requires client approval. Affected row count must match expectedRows or all changes roll back.")]
	public Task<string> UpdateRows(
		[Description("Configured database name")] string database,
		[Description("Exact case-sensitive ordinary user table name")] string table,
		[Description("Nonempty object mapping exact column names to new JSON scalar values")] JsonElement values,
		[Description("Nonempty object mapping exact column names to scalar equality filters, combined with AND")] JsonElement where,
		[Description("Expected affected row count, 1-1000")] int expectedRows,
		CancellationToken ct) => firebird.ExecuteAsync(database, WriteOperation.UpdateRows, () => WriteSql.Update(table, values, where, expectedRows), ct);

	[McpServerTool(Name = "delete_rows", ReadOnly = false, Destructive = true), Description("Deletes rows using nonempty AND-equality predicates; null means IS NULL. Disabled unless server-enabled; requires client approval. Affected row count must match expectedRows or all changes roll back.")]
	public Task<string> DeleteRows(
		[Description("Configured database name")] string database,
		[Description("Exact case-sensitive ordinary user table name")] string table,
		[Description("Nonempty object mapping exact column names to scalar equality filters, combined with AND")] JsonElement where,
		[Description("Expected affected row count, 1-1000")] int expectedRows,
		CancellationToken ct) => firebird.ExecuteAsync(database, WriteOperation.DeleteRows, () => WriteSql.Delete(table, where, expectedRows), ct);

	[McpServerTool(Name = "create_table", ReadOnly = false, Destructive = true), Description("Creates a new persistent user table, never replaces one. Disabled unless server-enabled; requires client approval. Only the documented built-in types are supported; no raw DDL, defaults, computed expressions, foreign keys or triggers.")]
	public Task<string> CreateTable(
		[Description("Configured database name")] string database,
		[Description("New case-sensitive table name: 1-63 ASCII letters/digits/_/$, starting with a letter or _, no system prefixes")] string table,
		[Description("Array of 1-128 objects: name, type (SMALLINT, INTEGER, BIGINT, BOOLEAN, DATE, TIME, TIMESTAMP, DOUBLE PRECISION, CHAR, VARCHAR, DECIMAL); length required for CHAR/VARCHAR (1-8191); precision (1-18) and scale (0-precision) required for DECIMAL; optional nullable (default true), primaryKey (default false, forces NOT NULL). No other properties.")] JsonElement columns,
		CancellationToken ct) => firebird.ExecuteAsync(database, WriteOperation.CreateTable, () => WriteSql.Create(table, columns), ct);

	[McpServerTool(Name = "drop_table", ReadOnly = false, Destructive = true), Description("Permanently drops an ordinary persistent user table and all its rows. Disabled unless server-enabled; requires client approval. No CASCADE or IF EXISTS; views/system/temporary/external tables are rejected. Restore requires a backup.")]
	public Task<string> DropTable(
		[Description("Configured database name")] string database,
		[Description("Exact case-sensitive ordinary user table name to permanently drop")] string table,
		CancellationToken ct) => firebird.ExecuteAsync(database, WriteOperation.DropTable, () => WriteSql.Drop(table), ct);
}
