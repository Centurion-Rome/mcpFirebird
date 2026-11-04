using System.Security.Cryptography;
using System.Text;
using McpFirebird;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Databases: "Databases__<name>__Host=..." etc., plus the FIREBIRD_* vars from .env as database "default".
var databases = new Dictionary<string, DatabaseOptions>(config.GetSection("Databases").Get<Dictionary<string, DatabaseOptions>>() ?? [], StringComparer.OrdinalIgnoreCase);
if (!string.IsNullOrWhiteSpace(config["FIREBIRD_DATABASE"]))
{
	var db = new DatabaseOptions { Database = config["FIREBIRD_DATABASE"]! };
	db.Host = config["FIREBIRD_HOST"] ?? db.Host;
	db.Port = int.TryParse(config["FIREBIRD_PORT"], out var port) ? port : db.Port;
	db.User = config["FIREBIRD_USER"] ?? db.User;
	db.Password = config["FIREBIRD_PASSWORD"] ?? db.Password;
	db.Charset = config["FIREBIRD_CHARSET"] ?? db.Charset;
	db.WireCrypt = Enum.TryParse<FirebirdSql.Data.FirebirdClient.FbWireCrypt>(config["FIREBIRD_CONF_WIRECRYPT"], true, out var wireCrypt) ? wireCrypt : db.WireCrypt;
	db.WriterUser = config["FIREBIRD_WRITER_USER"] ?? "";
	db.WriterPassword = config["FIREBIRD_WRITER_PASSWORD"] ?? "";
	db.AllowInsertRows = config.GetValue<bool>("FIREBIRD_ALLOW_INSERT_ROWS");
	db.AllowUpdateRows = config.GetValue<bool>("FIREBIRD_ALLOW_UPDATE_ROWS");
	db.AllowDeleteRows = config.GetValue<bool>("FIREBIRD_ALLOW_DELETE_ROWS");
	db.AllowCreateTable = config.GetValue<bool>("FIREBIRD_ALLOW_CREATE_TABLE");
	db.AllowDropTable = config.GetValue<bool>("FIREBIRD_ALLOW_DROP_TABLE");
	databases.TryAdd("default", db);
}
if (databases.Count == 0)
	throw new InvalidOperationException("No Firebird database configured. Set FIREBIRD_DATABASE or Databases__<name>__Database.");
FirebirdWriteService.ValidateConfiguration(databases, config["FIREBIRD_API_KEY"]);

if (config["MCP_ALLOWED_HOSTS"] is { Length: > 0 } allowedHosts)
	config["AllowedHosts"] = allowedHosts.Replace(',', ';');

builder.Services.AddSingleton(new FirebirdService(databases));
builder.Services.AddSingleton(new FirebirdWriteService(databases));
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<FirebirdTools>().WithTools<FirebirdWriteTools>()
	.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
	{
		FirebirdWriteTools.ValidateArguments(context.Params!);
		return await next(context, ct);
	}));

var app = builder.Build();

var apiKey = Encoding.UTF8.GetBytes($"Bearer {config["FIREBIRD_API_KEY"]}");
if (string.IsNullOrWhiteSpace(config["FIREBIRD_API_KEY"]))
	app.Logger.LogWarning("FIREBIRD_API_KEY is empty: MCP endpoint is NOT protected.");
else
	app.Use(async (ctx, next) =>
	{
		if (ctx.Request.Path.StartsWithSegments("/health") || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()), apiKey))
			await next();
		else
			ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
	});

app.MapGet("/health", () => "OK");
app.MapMcp("/mcp");
app.Logger.LogInformation("Configured databases: {Databases}", string.Join(", ", databases.Keys));
app.Run();
