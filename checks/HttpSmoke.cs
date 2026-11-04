using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpFirebird;

internal static class HttpSmoke
{
	internal static async Task RunAsync(Action<bool, string> check)
	{
		ProcessStartInfo StartInfo()
		{
			var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			// No inherited database settings/secrets and no .env loading. Every connection target is a dummy.
			info.Environment.Clear();
			foreach (var name in new[] { "SystemRoot", "PATH", "DOTNET_ROOT", "TEMP", "TMP" })
				if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
			info.ArgumentList.Add(typeof(FirebirdService).Assembly.Location);
			foreach (var argument in new[]
			{
				"--urls=http://127.0.0.1:0", "--FIREBIRD_HOST=127.0.0.1", "--FIREBIRD_PORT=1", "--FIREBIRD_DATABASE=unused.fdb",
				"--FIREBIRD_USER=reader", "--FIREBIRD_WRITER_USER=writer", "--FIREBIRD_WRITER_PASSWORD=dummy", "--FIREBIRD_ALLOW_INSERT_ROWS=true",
				"--Databases:erp:Host=127.0.0.1", "--Databases:erp:Port=1", "--Databases:erp:Database=unused.fdb",
				"--Databases:erp:User=reader", "--Databases:erp:WriterUser=writer", "--Databases:erp:WriterPassword=dummy", "--Databases:erp:AllowDeleteRows=true",
			}) info.ArgumentList.Add(argument);
			return info;
		}

		using (var denied = Process.Start(StartInfo())!)
		{
			var stderr = denied.StandardError.ReadToEndAsync();
			var stdout = denied.StandardOutput.ReadToEndAsync();
			await denied.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
			check(denied.ExitCode != 0 && (await stderr + await stdout).Contains("FIREBIRD_API_KEY is required"), "startup rejects enabled writes without API key");
		}

		var info = StartInfo();
		info.ArgumentList.Add("--FIREBIRD_API_KEY=smoke-test-only");
		using var process = Process.Start(info)!;
		var errors = process.StandardError.ReadToEndAsync();
		try
		{
			string? address = null;
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
			{
				if (!line.Contains("Now listening on: ")) continue;
				address = line[(line.IndexOf("Now listening on: ", StringComparison.Ordinal) + "Now listening on: ".Length)..].Trim();
				break;
			}
			if (address is null) throw new Exception("Smoke server failed to start: " + await errors);
			var output = process.StandardOutput.ReadToEndAsync();
			using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
			check((await client.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "health without token");
			check((await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.Unauthorized, "MCP requires token");
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "smoke-test-only");
			client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
			async Task<JsonElement> Rpc(string method, object parameters)
			{
				using var response = await client.PostAsync("/mcp", new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json"));
				response.EnsureSuccessStatusCode();
				var body = await response.Content.ReadAsStringAsync();
				if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
					body = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
				return JsonSerializer.Deserialize<JsonElement>(body);
			}
			var initialized = await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "offline-checks", version = "1" } });
			check(initialized.TryGetProperty("result", out _), "HTTP initialize");
			client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
			var catalog = await Rpc("tools/list", new { });
			check(catalog.GetProperty("result").GetProperty("tools").GetArrayLength() == 9, "HTTP catalog has four reads and five writes");
			var listed = await Rpc("tools/call", new { name = "list_databases", arguments = new { } });
			check(listed.ToString().Contains("default") && listed.ToString().Contains("erp") && !listed.ToString().Contains("dummy"), "HTTP lists both aliases, no writer secret");
			foreach (var database in new[] { "default", "erp" })
			{
				foreach (var tool in new[] { "insert_rows", "update_rows", "delete_rows", "create_table", "drop_table" })
				{
					var arguments = new Dictionary<string, object> { ["database"] = database, ["table"] = "T" };
					if (tool == "insert_rows") arguments["rows"] = Array.Empty<object>();
					if (tool == "update_rows") arguments["values"] = new { };
					if (tool is "delete_rows" or "update_rows") arguments["where"] = new { };
					if (tool.EndsWith("_rows", StringComparison.Ordinal)) arguments["expectedRows"] = 1;
					if (tool == "create_table") arguments["columns"] = Array.Empty<object>();
					var response = await Rpc("tools/call", new { name = tool, arguments });
					check(response.GetProperty("result").GetProperty("isError").GetBoolean(), "HTTP write is rejected");
					var enabled = (database == "default" && tool == "insert_rows") || (database == "erp" && tool == "delete_rows");
					check(response.ToString().Contains(enabled ? (tool == "insert_rows" ? "rows must contain" : "Objects must contain") : "disabled"), "HTTP config/gate " + database + "/" + tool);
				}
			}
			var unknown = await Rpc("tools/call", new { name = "drop_table", arguments = new { database = "default", table = "T", sql = "anything" } });
			check(unknown.ToString().Contains("exactly the documented"), "HTTP unknown argument rejected by filter");
		}
		finally
		{
			if (!process.HasExited) process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
			await errors;
		}
	}
}
