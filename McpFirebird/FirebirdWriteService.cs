using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using ModelContextProtocol;

namespace McpFirebird;

/// <summary>Independent, opt-in writer connection; never falls back to the read account.</summary>
public sealed class FirebirdWriteService(IReadOnlyDictionary<string, DatabaseOptions> databases)
{
	internal static bool IsEnabled(DatabaseOptions db, WriteOperation operation) => operation switch
	{
		WriteOperation.InsertRows => db.AllowInsertRows,
		WriteOperation.UpdateRows => db.AllowUpdateRows,
		WriteOperation.DeleteRows => db.AllowDeleteRows,
		WriteOperation.CreateTable => db.AllowCreateTable,
		WriteOperation.DropTable => db.AllowDropTable,
		_ => false,
	};

	public static void ValidateConfiguration(IReadOnlyDictionary<string, DatabaseOptions> databases, string? apiKey)
	{
		foreach (var db in databases.Values.Where(db => Enum.GetValues<WriteOperation>().Any(op => IsEnabled(db, op))))
		{
			if (string.IsNullOrWhiteSpace(apiKey))
				throw new InvalidOperationException("FIREBIRD_API_KEY is required when any write operation is enabled.");
			if (string.IsNullOrWhiteSpace(db.WriterUser) || string.IsNullOrWhiteSpace(db.WriterPassword)
				|| string.Equals(db.WriterUser.Trim(), db.User.Trim(), StringComparison.OrdinalIgnoreCase)
				|| string.Equals(db.WriterUser.Trim(), "SYSDBA", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Enabled writes require WriterUser and WriterPassword, with a user distinct from the read account and SYSDBA.");
		}
	}

	internal async Task<string> ExecuteAsync(string database, WriteOperation operation, Func<WritePlan> build, CancellationToken ct)
	{
		if (!databases.TryGetValue(database, out var db)) throw new McpException("Unknown database.");
		if (!IsEnabled(db, operation)) throw new McpException("This write operation is disabled for this database by server configuration.");
		var plan = build();
		if (plan.Operation != operation) throw new InvalidOperationException("Write operation does not match its permission gate.");
		ct.ThrowIfCancellationRequested();
		try
		{
			var connection = new FbConnection(new FbConnectionStringBuilder
			{
				DataSource = db.Host,
				Port = db.Port,
				Database = db.Database,
				UserID = db.WriterUser,
				Password = db.WriterPassword,
				Charset = db.Charset,
				WireCrypt = db.WireCrypt,
			}.ToString());
			return await RunTransactionAsync(connection, async token => await connection.BeginTransactionAsync(new FbTransactionOptions
			{
				TransactionBehavior = FbTransactionBehavior.Write | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait,
			}, token), async dbTransaction =>
			{
				var transaction = (FbTransaction)dbTransaction;
				await CheckTableAsync(connection, transaction, plan, ct);
				var affectedRows = 0;
				foreach (var statement in plan.Statements)
				{
					await using var command = new FbCommand(statement.Sql, connection, transaction) { CommandTimeout = 30 };
					for (var i = 0; i < statement.Values.Count; i++) command.Parameters.AddWithValue($"@p{i}", statement.Values[i]);
					var affected = await command.ExecuteNonQueryAsync(ct);
					if (plan.ExpectedRows.HasValue)
					{
						if (affected < 0 || affected > plan.ExpectedRows.Value - affectedRows)
							throw new McpException("Affected row count exceeded expectedRows or could not be verified; commit was not attempted. Verify database state before retrying.");
						affectedRows += affected;
					}
				}
				if (plan.ExpectedRows.HasValue && affectedRows != plan.ExpectedRows.Value)
					throw new McpException($"Expected {plan.ExpectedRows.Value} affected rows but got {affectedRows}; commit was not attempted. Verify database state before retrying.");
				return JsonSerializer.Serialize(new { database, table = plan.Table, operation = operation.ToString(), affectedRows = plan.ExpectedRows.HasValue ? (int?)affectedRows : null });
			}, ct);
		}
		catch (Exception ex) when (ex is FbException or ArgumentException or FormatException or OverflowException or InvalidCastException)
		{
			// Provider/conversion messages can include paths, SQL values, or trigger exception text.
			throw new McpException("Firebird write failed. Check writer grants, table/column names, value types, constraints and concurrent changes. If the connection failed during commit, verify the outcome before retrying.");
		}
	}

	internal static async Task<string> RunTransactionAsync(DbConnection connection, Func<CancellationToken, Task<DbTransaction>> begin, Func<DbTransaction, Task<string>> write, CancellationToken ct)
	{
		DbTransaction? transaction = null;
		ExceptionDispatchInfo? failure = null;
		string? result = null;
		try
		{
			await connection.OpenAsync(ct);
			transaction = await begin(ct);
			result = await write(transaction);
			ct.ThrowIfCancellationRequested();
			await transaction.CommitAsync(ct);
		}
		catch (Exception ex)
		{
			failure = ExceptionDispatchInfo.Capture(ex);
			if (transaction is not null)
			{
				try { await transaction.RollbackAsync(CancellationToken.None); }
				catch { /* Best-effort cleanup must not replace the original failure or cancellation. */ }
			}
		}
		foreach (var resource in new IAsyncDisposable?[] { transaction, connection })
		{
			if (resource is null) continue;
			try { await resource.DisposeAsync(); }
			catch
			{
				// No earlier failure means CommitAsync completed; cleanup failure must not invite a retry.
				failure ??= ExceptionDispatchInfo.Capture(new McpException("Write committed, but resource cleanup failed. Do not retry the write; verify database state."));
			}
		}
		failure?.Throw();
		return result!;
	}

	static async Task CheckTableAsync(FbConnection connection, FbTransaction transaction, WritePlan plan, CancellationToken ct)
	{
		await using var command = new FbCommand("""
			SELECT RDB$SYSTEM_FLAG, RDB$RELATION_TYPE, RDB$VIEW_BLR, RDB$EXTERNAL_FILE
			FROM RDB$RELATIONS WHERE RDB$RELATION_NAME = @table
			""", connection, transaction) { CommandTimeout = 30 };
		command.Parameters.AddWithValue("@table", plan.Table);
		await using var reader = await command.ExecuteReaderAsync(ct);
		var exists = await reader.ReadAsync(ct);
		if (plan.Operation == WriteOperation.CreateTable)
		{
			if (exists) throw new McpException("A relation with that name already exists; create_table never replaces it.");
		}
		else if (!exists || (!reader.IsDBNull(0) && reader.GetInt16(0) != 0)
			|| reader.IsDBNull(1) || reader.GetInt16(1) != 0 || !reader.IsDBNull(2) || !reader.IsDBNull(3))
			throw new McpException("Writes require an existing ordinary persistent user table (not a system table, view, temporary or external table).");
	}
}
