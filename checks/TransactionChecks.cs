using System.Data;
using System.Data.Common;
using McpFirebird;
using ModelContextProtocol;

internal static class TransactionChecks
{
	internal static async Task RunAsync(Action<bool, string> check)
	{
		foreach (var stage in new[] { "write", "commit", "open", "begin", "success" })
		foreach (var canceled in new[] { false, true })
		foreach (var rollbackFails in new[] { false, true })
		foreach (var connectionDisposeFails in new[] { false, true })
		foreach (var transactionDisposeFails in new[] { false, true })
		{
			var calls = new List<string>();
			using var cancellation = new CancellationTokenSource();
			Exception original = canceled ? new OperationCanceledException(cancellation.Token) : new McpException("original failure");
			var connection = new TestConnection(calls, stage == "open" ? original : null, connectionDisposeFails);
			var transaction = new TestTransaction(connection, calls, stage == "commit" ? original : null, rollbackFails, transactionDisposeFails);
			Exception? failure = null;
			string? result = null;
			try
			{
				result = await FirebirdWriteService.RunTransactionAsync(connection, token =>
				{
					calls.Add("begin");
					return stage == "begin" ? Task.FromException<DbTransaction>(original) : Task.FromResult<DbTransaction>(transaction);
				}, tx =>
				{
					calls.Add("write");
					if (canceled && stage is "write" or "success") cancellation.Cancel();
					return stage == "write" ? Task.FromException<string>(original) : Task.FromResult("result");
				}, cancellation.Token);
			}
			catch (Exception ex) { failure = ex; }

			var label = $"{stage}, canceled={canceled}, rollback={rollbackFails}, transactionDispose={transactionDisposeFails}, connectionDispose={connectionDisposeFails}";
			if (stage != "success")
				check(ReferenceEquals(failure, original), "original survives cleanup: " + label);
			else if (canceled)
				check(failure is OperationCanceledException oce && oce.CancellationToken == cancellation.Token, "pre-commit cancellation survives cleanup: " + label);
			else if (transactionDisposeFails || connectionDisposeFails)
				check(failure is McpException && failure.Message.Contains("committed") && !failure.Message.Contains("secret"), "post-commit cleanup failure is explicit and sanitized: " + label);
			else
				check(failure is null && result == "result", "healthy write returns result: " + label);

			var expected = new List<string> { "open" };
			if (stage != "open") expected.Add("begin");
			if (stage is not ("open" or "begin"))
			{
				expected.Add("write");
				if (stage != "write" && (!canceled || stage == "commit")) expected.Add("commit");
				if (stage != "success" || canceled) expected.Add("rollback");
				expected.Add("dispose transaction");
				if (stage != "success" || canceled) check(transaction.RollbackToken == CancellationToken.None, "uncanceled rollback: " + label);
			}
			expected.Add("dispose connection");
			check(calls.SequenceEqual(expected), "lifecycle order and exactly-once disposal: " + label);
		}
	}

	sealed class TestConnection(List<string> calls, Exception? openFailure, bool disposeFails) : DbConnection
	{
		[System.Diagnostics.CodeAnalysis.AllowNull]
		public override string ConnectionString { get; set; } = "";
		public override string Database => "test";
		public override string DataSource => "test";
		public override string ServerVersion => "test";
		public override ConnectionState State => ConnectionState.Closed;
		public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
		public override void Close() => throw new NotSupportedException();
		public override void Open() => throw new NotSupportedException();
		protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
		protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
		public override Task OpenAsync(CancellationToken ct)
		{
			calls.Add("open");
			return openFailure is null ? Task.CompletedTask : Task.FromException(openFailure);
		}
		public override ValueTask DisposeAsync()
		{
			calls.Add("dispose connection");
			return disposeFails ? ValueTask.FromException(new InvalidOperationException("secret connection details")) : ValueTask.CompletedTask;
		}
	}

	sealed class TestTransaction(DbConnection connection, List<string> calls, Exception? commitFailure, bool rollbackFails, bool disposeFails) : DbTransaction
	{
		public CancellationToken? RollbackToken { get; private set; }
		public override IsolationLevel IsolationLevel => IsolationLevel.Snapshot;
		protected override DbConnection DbConnection => connection;
		public override void Commit() => throw new NotSupportedException();
		public override void Rollback() => throw new NotSupportedException();
		public override Task CommitAsync(CancellationToken ct)
		{
			calls.Add("commit");
			return commitFailure is null ? Task.CompletedTask : Task.FromException(commitFailure);
		}
		public override Task RollbackAsync(CancellationToken ct)
		{
			calls.Add("rollback");
			RollbackToken = ct;
			return rollbackFails ? Task.FromException(new InvalidOperationException("secret rollback details")) : Task.CompletedTask;
		}
		public override ValueTask DisposeAsync()
		{
			calls.Add("dispose transaction");
			return disposeFails ? ValueTask.FromException(new InvalidOperationException("secret transaction details")) : ValueTask.CompletedTask;
		}
	}
}
