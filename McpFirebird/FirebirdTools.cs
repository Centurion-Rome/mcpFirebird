using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpFirebird;

[McpServerToolType]
public sealed class FirebirdTools(FirebirdService firebird)
{
	const int MaxRowsLimit = 1000;

	[McpServerTool(Name = "list_databases", ReadOnly = true), Description("Lists the configured Firebird databases. Use the name as 'database' argument of the other tools.")]
	public string ListDatabases() => JsonSerializer.Serialize(firebird.List());

	[McpServerTool(Name = "list_tables", ReadOnly = true), Description("Lists user tables and views of a database.")]
	public Task<string> ListTables([Description("Database name from list_databases")] string database, CancellationToken ct) =>
		firebird.QueryAsync(database, """
			select trim(rdb$relation_name) as "NAME", iif(rdb$view_blr is null, 'TABLE', 'VIEW') as "TYPE"
			from rdb$relations where coalesce(rdb$system_flag, 0) = 0 order by 1
			""", int.MaxValue, ct);

	[McpServerTool(Name = "describe_table", ReadOnly = true), Description("Lists the columns of a table or view (type, nullability, primary key).")]
	public Task<string> DescribeTable([Description("Database name from list_databases")] string database, [Description("Exact table name as returned by list_tables")] string table, CancellationToken ct) =>
		firebird.QueryAsync(database, """
			select trim(rf.rdb$field_name) as "NAME",
				case f.rdb$field_type
					when 7 then 'SMALLINT' when 8 then 'INTEGER' when 16 then 'BIGINT' when 26 then 'INT128'
					when 10 then 'FLOAT' when 27 then 'DOUBLE PRECISION' when 24 then 'DECFLOAT(16)' when 25 then 'DECFLOAT(34)'
					when 12 then 'DATE' when 13 then 'TIME' when 28 then 'TIME WITH TIME ZONE'
					when 35 then 'TIMESTAMP' when 29 then 'TIMESTAMP WITH TIME ZONE'
					when 14 then 'CHAR' when 37 then 'VARCHAR' when 261 then 'BLOB' when 23 then 'BOOLEAN'
					else 'TYPE ' || f.rdb$field_type end as "TYPE",
				f.rdb$field_sub_type as "SUB_TYPE", f.rdb$field_precision as "PRECISION", f.rdb$field_scale as "SCALE",
				f.rdb$character_length as "CHAR_LENGTH",
				iif(coalesce(rf.rdb$null_flag, f.rdb$null_flag, 0) = 1, false, true) as "NULLABLE",
				iif(exists(select 1 from rdb$relation_constraints rc join rdb$index_segments s on s.rdb$index_name = rc.rdb$index_name
					where rc.rdb$relation_name = rf.rdb$relation_name and rc.rdb$constraint_type = 'PRIMARY KEY' and s.rdb$field_name = rf.rdb$field_name), true, false) as "PRIMARY_KEY"
			from rdb$relation_fields rf join rdb$fields f on f.rdb$field_name = rf.rdb$field_source
			where rf.rdb$relation_name = @table order by rf.rdb$field_position
			""", int.MaxValue, ct, ("@table", table));

	[McpServerTool(Name = "execute_query", ReadOnly = true), Description("Executes a SQL statement (Firebird 5 dialect) in a read-only transaction and returns columns and rows as JSON. Writes are rejected by the server.")]
	public Task<string> ExecuteQuery([Description("Database name from list_databases")] string database, [Description("SQL SELECT statement")] string sql, [Description("Maximum rows to return (1-1000)")] int maxRows = 100, CancellationToken ct = default) =>
		firebird.QueryAsync(database, sql, Math.Clamp(maxRows, 1, MaxRowsLimit), ct);
}
