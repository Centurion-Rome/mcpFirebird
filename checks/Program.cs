using System.Reflection;
using System.Text.Json;
using McpFirebird;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var checks = 0;
void Check(bool condition, string name)
{
	if (!condition) throw new Exception("FAILED: " + name);
	checks++;
}
void Reject(Action action, string name)
{
	try { action(); }
	catch (McpException) { checks++; return; }
	throw new Exception("FAILED (accepted invalid input): " + name);
}
void BadConfig(DatabaseOptions db, string? key, string name)
{
	try { FirebirdWriteService.ValidateConfiguration(new Dictionary<string, DatabaseOptions> { ["test"] = db }, key); }
	catch (InvalidOperationException) { checks++; return; }
	throw new Exception("FAILED (accepted unsafe config): " + name);
}
JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

Check(WriteSql.Identifier("Mixed_Case$1") == "\"Mixed_Case$1\"", "quoted exact identifier");
Check(WriteSql.Identifier(new string('A', 63)).Length == 65, "63-char identifier");
foreach (var name in new[] { "", "A\"; DROP TABLE T;--", "A B", "A.B", "RDB$RELATIONS", "mon$foo", "SEC$USERS", "A\n", "é", new string('A', 64), "1X" })
	Reject(() => WriteSql.Drop(name), "invalid identifier");

var insert = WriteSql.Insert("T", Json("""[{"ID":1,"TEXT":"'); DELETE FROM T;--","FLAG":true,"EMPTY":null},{"ID":2}]"""), 2);
Check(insert.Statements.Count == 2 && insert.ExpectedRows == 2, "atomic batch plan");
Check(insert.Statements[0].Sql == "INSERT INTO \"T\" (\"ID\", \"TEXT\", \"FLAG\", \"EMPTY\") VALUES (@p0, @p1, @p2, @p3)", "parameterized insert");
Check((string)insert.Statements[0].Values[1] == "'); DELETE FROM T;--" && insert.Statements[0].Values[3] == DBNull.Value, "SQL-shaped string is only a value");
var update = WriteSql.Update("T", Json("""{"TEXT":"changed","FLAG":false}"""), Json("""{"ID":1,"EMPTY":null}"""), 1).Statements.Single();
Check(update.Sql == "UPDATE \"T\" SET \"TEXT\" = @p0, \"FLAG\" = @p1 WHERE \"ID\" = @p2 AND \"EMPTY\" IS NULL", "update AND/null predicate");
Check(update.Values.Count == 3 && (long)update.Values[2] == 1, "update parameter numbering");
var delete = WriteSql.Delete("T", Json("""{"ID":1}"""), 1).Statements.Single();
Check(delete.Sql == "DELETE FROM \"T\" WHERE \"ID\" = @p0" && delete.Values.Count == 1, "parameterized delete");
foreach (var value in new[] { "{}", "[]", "null", "{\"ID\":1,\"ID\":2}", "{\"ID\":{\"sql\":\"1=1\"}}", "{\"ID\":[1]}", "{\"ID\":1e100}", "{\"ID\":1e-100}", "{\"ID\":0.123456789012345678901234567891}" })
{
	Reject(() => WriteSql.Delete("T", Json(value), 1), "invalid predicate");
	Reject(() => WriteSql.Update("T", Json(value), Json("{\"ID\":1}"), 1), "invalid assignment");
}
Reject(() => WriteSql.Insert("T", Json("[]"), 1), "empty rows");
Reject(() => WriteSql.Insert("T", Json("[{}]"), 1), "empty row");
Reject(() => WriteSql.Insert("T", Json("[null]"), 1), "null row");
Reject(() => WriteSql.Insert("T", Json("[{\"ID\":1}]"), 2), "insert expected mismatch");
foreach (var count in new[] { -1, 0, 1001 }) Reject(() => WriteSql.Delete("T", Json("{\"ID\":1}"), count), "expectedRows limit");
Reject(() => WriteSql.Insert("T", Json(JsonSerializer.Serialize(Enumerable.Repeat(new { ID = 1 }, 1001))), 1000), "batch limit");
Reject(() => WriteSql.Delete("T", Json(JsonSerializer.Serialize(new { TEXT = new string('x', 32766) })), 1), "string limit");
Reject(() => WriteSql.Delete("T", Json(JsonSerializer.Serialize(Enumerable.Range(0, 129).ToDictionary(i => "C" + i, i => i))), 1), "column count limit");
var scalars = WriteSql.Insert("T", Json("""[{"A":9223372036854775807,"B":12.34,"C":false,"D":null}]"""), 1).Statements.Single().Values;
Check(scalars[0] is long && (decimal)scalars[1] == 12.34m && scalars[2] is false && scalars[3] == DBNull.Value, "scalar conversion");
Check((decimal)WriteSql.Delete("T", Json("{\"ID\":1.00e2}"), 1).Statements.Single().Values[0] == 100m, "lossless exponential numeric value");
Check(WriteSql.Insert("T", Json(JsonSerializer.Serialize(Enumerable.Repeat(new { ID = 1 }, 1000))), 1000).Statements.Count == 1000, "maximum insert batch");

var create = WriteSql.Create("T", Json("""[{"name":"ID","type":"BIGINT","primaryKey":true},{"name":"TITLE","type":"VARCHAR","length":100},{"name":"AMOUNT","type":"DECIMAL","precision":18,"scale":2,"nullable":false}]""")).Statements.Single();
Check(create.Sql == "CREATE TABLE \"T\" (\"ID\" BIGINT NOT NULL, \"TITLE\" VARCHAR(100) CHARACTER SET UTF8, \"AMOUNT\" DECIMAL(18,2) NOT NULL, PRIMARY KEY (\"ID\"))", "allowlisted create");
Check(create.Values.Count == 0 && WriteSql.Drop("T").Statements.Single().Sql == "DROP TABLE \"T\"", "DDL only generated");
Check(WriteSql.Create("T", Json("""[{"name":"C","type":"CHAR","length":8191}]""")).Statements.Single().Sql.Contains("CHAR(8191) CHARACTER SET UTF8"), "maximum UTF8 character length");
foreach (var type in new[] { "SMALLINT", "INTEGER", "BIGINT", "BOOLEAN", "DATE", "TIME", "TIMESTAMP", "DOUBLE PRECISION" })
	Check(WriteSql.Create("T", Json(JsonSerializer.Serialize(new[] { new { name = "C", type } }))).Statements.Count == 1, "allowed type " + type);
foreach (var columns in new[]
{
	"[]", "null", "[null]", "[{}]", "[{\"name\":\"ID\",\"type\":\"INTEGER); DROP TABLE T;--\"}]",
	"[{\"name\":\"ID\",\"type\":\"INTEGER\",\"default\":\"1\"}]",
	"[{\"name\":\"ID\",\"name\":\"OTHER\",\"type\":\"INTEGER\"}]",
	"[{\"name\":\"ID\",\"type\":\"INTEGER\"},{\"name\":\"ID\",\"type\":\"INTEGER\"}]",
	"[{\"name\":\"ID\",\"type\":\"INTEGER\",\"length\":2}]",
	"[{\"name\":\"ID\",\"type\":\"VARCHAR\",\"length\":8192}]",
	"[{\"name\":\"ID\",\"type\":\"CHAR\",\"length\":0}]",
	"[{\"name\":\"ID\",\"type\":\"VARCHAR\"}]",
	"[{\"name\":\"ID\",\"type\":\"DECIMAL\",\"precision\":19,\"scale\":0}]",
	"[{\"name\":\"ID\",\"type\":\"DECIMAL\",\"precision\":2,\"scale\":3}]",
	"[{\"name\":\"ID\",\"type\":\"INTEGER\",\"nullable\":\"true\"}]",
}) Reject(() => WriteSql.Create("T", Json(columns)), "invalid DDL definition");

var emptyConfig = new Dictionary<string, DatabaseOptions> { ["test"] = new() };
FirebirdWriteService.ValidateConfiguration(emptyConfig, null);
Check(Enum.GetValues<WriteOperation>().All(op => !FirebirdWriteService.IsEnabled(emptyConfig["test"], op)), "disabled defaults");
foreach (var operation in Enum.GetValues<WriteOperation>())
{
	var db = new DatabaseOptions { User = "reader", WriterUser = "writer", WriterPassword = "test-only" };
	typeof(DatabaseOptions).GetProperty("Allow" + operation)!.SetValue(db, true);
	Check(Enum.GetValues<WriteOperation>().All(op => FirebirdWriteService.IsEnabled(db, op) == (op == operation)), "independent gate " + operation);
	FirebirdWriteService.ValidateConfiguration(new Dictionary<string, DatabaseOptions> { ["test"] = db }, "test-only-token");
	BadConfig(db, null, "API key required " + operation);
	var service = new FirebirdWriteService(emptyConfig);
	try
	{
		await service.ExecuteAsync("test", operation, () => throw new Exception("Gate ran too late"), default);
		throw new Exception("Disabled write was accepted");
	}
	catch (McpException ex) { Check(ex.Message.Contains("disabled"), "gate before SQL/connection " + operation); }
}
BadConfig(new() { AllowInsertRows = true }, "key", "missing writer");
BadConfig(new() { AllowInsertRows = true, User = "reader", WriterUser = " READER ", WriterPassword = "test" }, "key", "same account");
BadConfig(new() { AllowInsertRows = true, WriterUser = "sysdba", WriterPassword = "test" }, "key", "admin writer");
BadConfig(new() { AllowInsertRows = true, WriterUser = "writer", WriterPassword = " " }, "key", "blank writer password");
try
{
	await new FirebirdWriteService(emptyConfig).ExecuteAsync("unknown", WriteOperation.DropTable, () => throw new Exception("Unknown database gate ran too late"), default);
	throw new Exception("Unknown database accepted");
}
catch (McpException ex) { Check(ex.Message == "Unknown database.", "unknown database before build/connection"); }
var enabledConfig = new Dictionary<string, DatabaseOptions> { ["test"] = new() { AllowDropTable = true, User = "reader", WriterUser = "writer", WriterPassword = "test" } };
try
{
	await new FirebirdWriteService(enabledConfig).ExecuteAsync("test", WriteOperation.DropTable, () => WriteSql.Drop("T"), new CancellationToken(true));
	throw new Exception("Canceled write accepted");
}
catch (OperationCanceledException) { checks++; }
await TransactionChecks.RunAsync(Check);
var named = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
	["Databases:erp:WriterUser"] = "writer", ["Databases:erp:WriterPassword"] = "test", ["Databases:erp:AllowDeleteRows"] = "true",
}).Build().GetSection("Databases").Get<Dictionary<string, DatabaseOptions>>()!;
Check(named["erp"].WriterUser == "writer" && named["erp"].AllowDeleteRows && !named["erp"].AllowDropTable, "named config binding");

var tools = typeof(FirebirdWriteTools).GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null).ToArray();
Check(tools.Length == 5, "five write tools");
foreach (var method in tools)
{
	var tool = McpServerTool.Create(method, new FirebirdWriteTools(new FirebirdWriteService(emptyConfig))).ProtocolTool;
	Check(tool.Annotations?.ReadOnlyHint == false && tool.Annotations.DestructiveHint == true, "write annotations " + tool.Name);
	var arguments = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToDictionary(p => p.Name!, p => p.ParameterType == typeof(string) ? Json("\"test\"") : Json("1"));
	FirebirdWriteTools.ValidateArguments(new() { Name = tool.Name, Arguments = arguments });
	if (arguments.ContainsKey("expectedRows"))
	{
		arguments["expectedRows"] = Json("\"1\"");
		Reject(() => FirebirdWriteTools.ValidateArguments(new() { Name = tool.Name, Arguments = arguments }), "string count rejected " + tool.Name);
		arguments["expectedRows"] = Json("1");
	}
	arguments["sql"] = Json("\"DROP TABLE T\"");
	Reject(() => FirebirdWriteTools.ValidateArguments(new() { Name = tool.Name, Arguments = arguments }), "unknown top-level argument " + tool.Name);
	Reject(() => FirebirdWriteTools.ValidateArguments(new() { Name = tool.Name }), "missing arguments " + tool.Name);
}
FirebirdWriteTools.ValidateArguments(new() { Name = "execute_query" });
Check(typeof(FirebirdTools).GetMethods().Count(m => m.GetCustomAttribute<McpServerToolAttribute>()?.ReadOnly == true) == 4, "four read tools unchanged");
await HttpSmoke.RunAsync(Check);
Console.WriteLine($"PASS: {checks} checks (SQL generation, validation, config/gates, MCP annotations and loopback HTTP). No database connections made.");
