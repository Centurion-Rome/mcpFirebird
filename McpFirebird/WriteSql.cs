using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace McpFirebird;

internal enum WriteOperation { InsertRows, UpdateRows, DeleteRows, CreateTable, DropTable }
internal sealed record WriteStatement(string Sql, IReadOnlyList<object> Values);
internal sealed record WritePlan(WriteOperation Operation, string Table, IReadOnlyList<WriteStatement> Statements, int? ExpectedRows);

internal static class WriteSql
{
	internal static string Identifier(string name)
	{
		// Deliberately support only simple ASCII names, preserving exact case with quoting.
		if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z_][A-Za-z0-9_$]{0,62}\z", RegexOptions.CultureInvariant)
			|| new[] { "RDB$", "MON$", "SEC$" }.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
			throw new McpException("Identifiers must be 1-63 ASCII letters/digits/_/$, start with a letter or _, and not use a system prefix.");
		return $"\"{name}\"";
	}

	internal static WritePlan Insert(string table, JsonElement rows, int expectedRows)
	{
		var target = Identifier(table);
		Expected(expectedRows);
		if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 1000 || rows.GetArrayLength() != expectedRows)
			throw new McpException("rows must contain 1-1000 objects and its length must equal expectedRows.");
		var statements = new List<WriteStatement>();
		foreach (var row in rows.EnumerateArray())
		{
			var fields = Fields(row);
			var values = fields.Select(p => Scalar(p.Value)).ToArray();
			statements.Add(new($"INSERT INTO {target} ({string.Join(", ", fields.Select(p => Identifier(p.Name)))}) VALUES ({string.Join(", ", values.Select((_, i) => $"@p{i}"))})", values));
		}
		return new(WriteOperation.InsertRows, table, statements, expectedRows);
	}

	internal static WritePlan Update(string table, JsonElement values, JsonElement where, int expectedRows)
	{
		var target = Identifier(table);
		Expected(expectedRows);
		var parameters = new List<object>();
		var assignments = Fields(values).Select(p => $"{Identifier(p.Name)} = {Parameter(parameters, p.Value)}").ToArray();
		var predicate = Predicate(where, parameters);
		return new(WriteOperation.UpdateRows, table, [new($"UPDATE {target} SET {string.Join(", ", assignments)} WHERE {predicate}", parameters)], expectedRows);
	}

	internal static WritePlan Delete(string table, JsonElement where, int expectedRows)
	{
		var target = Identifier(table);
		Expected(expectedRows);
		var parameters = new List<object>();
		var predicate = Predicate(where, parameters);
		return new(WriteOperation.DeleteRows, table, [new($"DELETE FROM {target} WHERE {predicate}", parameters)], expectedRows);
	}

	internal static WritePlan Create(string table, JsonElement columns)
	{
		var target = Identifier(table);
		if (columns.ValueKind != JsonValueKind.Array || columns.GetArrayLength() is < 1 or > 128)
			throw new McpException("columns must contain 1-128 column definitions.");
		var definitions = new List<string>();
		var names = new HashSet<string>(StringComparer.Ordinal);
		var primaryKey = new List<string>();
		foreach (var column in columns.EnumerateArray())
		{
			if (column.ValueKind != JsonValueKind.Object)
				throw new McpException("Each column must be an object.");
			var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
			foreach (var p in column.EnumerateObject())
				if (!new[] { "name", "type", "length", "precision", "scale", "nullable", "primaryKey" }.Contains(p.Name) || !properties.TryAdd(p.Name, p.Value))
					throw new McpException("Unknown or duplicate column definition property.");
			if (!properties.TryGetValue("name", out var name) || name.ValueKind != JsonValueKind.String
				|| !properties.TryGetValue("type", out var type) || type.ValueKind != JsonValueKind.String)
				throw new McpException("Each column requires string name and type.");
			var quoted = Identifier(name.GetString()!);
			if (!names.Add(quoted)) throw new McpException("Duplicate column name.");
			var sqlType = type.GetString();
			switch (sqlType)
			{
				case "CHAR": case "VARCHAR":
					sqlType += $"({Integer(properties, "length", 1, 8191)}) CHARACTER SET UTF8";
					break;
				case "DECIMAL":
					var precision = Integer(properties, "precision", 1, 18);
					sqlType += $"({precision},{Integer(properties, "scale", 0, precision)})";
					break;
				case "SMALLINT": case "INTEGER": case "BIGINT": case "BOOLEAN":
				case "DATE": case "TIME": case "TIMESTAMP": case "DOUBLE PRECISION":
					break;
				default: throw new McpException("Unsupported column type. Use a documented uppercase type name, not SQL.");
			}
			if ((properties.ContainsKey("length") && type.GetString() is not ("CHAR" or "VARCHAR"))
				|| ((properties.ContainsKey("precision") || properties.ContainsKey("scale")) && type.GetString() != "DECIMAL"))
				throw new McpException("Type options do not apply to this column type.");
			var nullable = Boolean(properties, "nullable", true);
			var key = Boolean(properties, "primaryKey", false);
			if (key) primaryKey.Add(quoted);
			definitions.Add($"{quoted} {sqlType}{(!nullable || key ? " NOT NULL" : "")}");
		}
		if (primaryKey.Count > 0) definitions.Add($"PRIMARY KEY ({string.Join(", ", primaryKey)})");
		return new(WriteOperation.CreateTable, table, [new($"CREATE TABLE {target} ({string.Join(", ", definitions)})", [])], null);
	}

	internal static WritePlan Drop(string table) => new(WriteOperation.DropTable, table, [new($"DROP TABLE {Identifier(table)}", [])], null);

	static void Expected(int rows)
	{
		if (rows is < 1 or > 1000) throw new McpException("expectedRows must be 1-1000.");
	}

	static JsonProperty[] Fields(JsonElement value)
	{
		if (value.ValueKind != JsonValueKind.Object) throw new McpException("Values and predicates must be JSON objects.");
		var fields = value.EnumerateObject().ToArray();
		if (fields.Length is < 1 or > 128 || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
			throw new McpException("Objects must contain 1-128 distinct column names.");
		foreach (var field in fields) Identifier(field.Name);
		return fields;
	}

	static string Predicate(JsonElement where, List<object> parameters) => string.Join(" AND ", Fields(where).Select(p =>
		p.Value.ValueKind == JsonValueKind.Null ? $"{Identifier(p.Name)} IS NULL" : $"{Identifier(p.Name)} = {Parameter(parameters, p.Value)}"));

	static string Parameter(List<object> parameters, JsonElement value)
	{
		var name = "@p" + parameters.Count.ToString(CultureInfo.InvariantCulture);
		parameters.Add(Scalar(value));
		return name;
	}

	static object Scalar(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Null => DBNull.Value,
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.String when value.GetString()!.Length <= 32765 => value.GetString()!,
		JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
		JsonValueKind.Number when value.TryGetDecimal(out var number) && JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(number)) => number,
		_ => throw new McpException("Values must be null, boolean, string (at most 32765 characters), or a number representable as Int64/Decimal; no arrays, objects or SQL expressions."),
	};

	static int Integer(Dictionary<string, JsonElement> properties, string name, int min, int max)
	{
		if (!properties.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n < min || n > max)
			throw new McpException($"{name} must be an integer between {min} and {max}.");
		return n;
	}

	static bool Boolean(Dictionary<string, JsonElement> properties, string name, bool fallback)
	{
		if (!properties.TryGetValue(name, out var value)) return fallback;
		if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new McpException($"{name} must be a boolean.");
		return value.GetBoolean();
	}
}
