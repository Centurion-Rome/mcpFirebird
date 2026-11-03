using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using ModelContextProtocol;

namespace McpFirebird;

/// <summary>One configured Firebird database (section "Databases:&lt;name&gt;" or FIREBIRD_* env vars).</summary>
public sealed class DatabaseOptions
{
	public string Host { get; set; } = "localhost";
	public int Port { get; set; } = 3050;
	public string Database { get; set; } = "";
	public string User { get; set; } = "SYSDBA";
	public string Password { get; set; } = "";
	public string Charset { get; set; } = "UTF8";
	public FbWireCrypt WireCrypt { get; set; } = FbWireCrypt.Enabled;
}

/// <summary>Runs read-only queries against the configured databases.</summary>
public sealed class FirebirdService(IReadOnlyDictionary<string, DatabaseOptions> databases)
{
	const int CommandTimeoutSeconds = 30;

	static readonly FbTransactionOptions ReadOnlyTransaction = new()
	{
		TransactionBehavior = FbTransactionBehavior.Read | FbTransactionBehavior.ReadCommitted | FbTransactionBehavior.RecVersion | FbTransactionBehavior.NoWait,
	};

	public IEnumerable<object> List() => databases.Select(d => new { name = d.Key, host = d.Value.Host, port = d.Value.Port, database = d.Value.Database });

	public async Task<string> QueryAsync(string database, string sql, int maxRows, CancellationToken ct, params (string Name, object Value)[] parameters)
	{
		if (!databases.TryGetValue(database, out var db))
			throw new McpException($"Unknown database '{database}'. Configured: {string.Join(", ", databases.Keys)}");
		try
		{
			await using var connection = new FbConnection(new FbConnectionStringBuilder
			{
				DataSource = db.Host,
				Port = db.Port,
				Database = db.Database,
				UserID = db.User,
				Password = db.Password,
				Charset = db.Charset,
				WireCrypt = db.WireCrypt,
			}.ToString());
			await connection.OpenAsync(ct);
			// Read-only transaction: Firebird itself rejects any INSERT/UPDATE/DELETE/DDL.
			await using var transaction = await connection.BeginTransactionAsync(ReadOnlyTransaction, ct);
			await using var command = new FbCommand(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
			foreach (var (name, value) in parameters)
				command.Parameters.AddWithValue(name, value);
			await using var reader = await command.ExecuteReaderAsync(ct);
			var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
			var rows = new List<object?[]>();
			var truncated = false;
			while (await reader.ReadAsync(ct))
			{
				if (rows.Count == maxRows) { truncated = true; break; }
				var row = new object?[reader.FieldCount];
				for (var i = 0; i < row.Length; i++)
					row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
					{
						byte[] bytes => Convert.ToBase64String(bytes),
						FirebirdSql.Data.Types.FbZonedDateTime z => $"{z.DateTime:O} {z.TimeZone}",
						var v when v.GetType().Namespace?.StartsWith("FirebirdSql") == true => v.ToString(), // FbZonedDateTime, FbDecFloat, ...
						var v => v,
					};
				rows.Add(row);
			}
			await transaction.RollbackAsync(ct);
			return JsonSerializer.Serialize(new { columns, rows, truncated });
		}
		catch (FbException ex)
		{
			throw new McpException($"Firebird error: {ex.Message}");
		}
	}
}
