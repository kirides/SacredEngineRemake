using System.Globalization;
using System.Text;
using System.Text.Json;
using Lua;

namespace Sacred.GameServer.Scripting;

/// <summary>An HTTP answer of a script endpoint.</summary>
public readonly record struct ScriptResponse(int Status, string ContentType, string Body);

/// <summary>
/// HTTP endpoints of server scripts. A script registers <c>server.endpoint(path, fn, { public = true, description = "..." })</c>;
/// it is served at <c>/mods/{script file name without .lua}/{path}</c> by the web UI (admin password), and also at
/// <c>/public/mods/...</c> when marked public and the public API is on. The handler gets <c>{ path, query = { name = value } }</c>
/// and returns a string (plus an optional content type and status: CSV, plain text, HTML ...) or a table, sent as JSON.
/// Runs on the game loop thread like every other handler, with the same instruction budget.
/// </summary>
public static class ScriptEndpoints
{
    public sealed record Endpoint(string Mod, string Path, LuaFunction Function, string File, bool Public, string? Description);

    public static string Key(string mod, string path) => mod.ToLowerInvariant() + "/" + Normalize(path);

    public static string Normalize(string path) => path.Trim().Trim('/').ToLowerInvariant();

    public static string ModName(string file) => System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

    /// <summary>Turns a handler's return values into a response.</summary>
    public static ScriptResponse Response(ReadOnlySpan<LuaValue> values)
    {
        if (values.Length == 0 || values[0].Type == LuaValueType.Nil) return new(204, "text/plain; charset=utf-8", "");
        var status = values.Length > 2 && values[2].Type == LuaValueType.Number ? Math.Clamp((int)values[2].Read<double>(), 100, 599) : 200;
        if (values[0].Type == LuaValueType.Table)
            return new(status, "application/json; charset=utf-8", Json(values[0].Read<LuaTable>()));
        var type = values.Length > 1 && values[1].Type == LuaValueType.String ? values[1].Read<string>() : "text/plain; charset=utf-8";
        if (!type.Contains("charset", StringComparison.OrdinalIgnoreCase) && type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) type += "; charset=utf-8";
        return new(status, type, values[0].ToString());
    }

    /// <summary>A Lua table as JSON: a table with only a sequence part (or nothing) is an array, anything else an object. Depth is limited.</summary>
    public static string Json(LuaTable table)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, table, 0);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, LuaValue value, int depth)
    {
        switch (value.Type)
        {
            case LuaValueType.Nil: writer.WriteNullValue(); break;
            case LuaValueType.Boolean: writer.WriteBooleanValue(value.Read<bool>()); break;
            case LuaValueType.Number:
                var number = value.Read<double>();
                if (double.IsFinite(number)) writer.WriteNumberValue(number); else writer.WriteNullValue();
                break;
            case LuaValueType.String: writer.WriteStringValue(value.Read<string>()); break;
            case LuaValueType.Table when depth < 20: Write(writer, value.Read<LuaTable>(), depth + 1); break;
            default: writer.WriteStringValue(value.ToString()); break;
        }
    }

    private static void Write(Utf8JsonWriter writer, LuaTable table, int depth)
    {
        if (table.HashMapCount == 0)                                         // also the empty table: an empty list
        {
            writer.WriteStartArray();
            for (var i = 1; i <= table.ArrayLength; i++) Write(writer, table[i], depth);
            writer.WriteEndArray();
            return;
        }
        writer.WriteStartObject();
        var key = LuaValue.Nil;
        while (table.TryGetNext(key, out var pair))
        {
            key = pair.Key;
            writer.WritePropertyName(pair.Key.Type == LuaValueType.Number
                ? pair.Key.Read<double>().ToString(CultureInfo.InvariantCulture) : pair.Key.ToString());
            Write(writer, pair.Value, depth);
        }
        writer.WriteEndObject();
    }

    /// <summary><c>server.csv(rows [, columns])</c>: rows are tables; with columns, a header line and those fields in that order,
    /// otherwise the sequence part of each row. Fields with commas, quotes or line breaks are quoted.</summary>
    public static string Csv(LuaTable rows, LuaTable? columns)
    {
        var sb = new StringBuilder();
        var names = new List<string>();
        if (columns is not null) for (var i = 1; i <= columns.ArrayLength; i++) names.Add(columns[i].ToString());
        if (names.Count > 0) { AppendLine(sb, names); }
        var fields = new List<string>();
        for (var r = 1; r <= rows.ArrayLength; r++)
        {
            if (rows[r].Type != LuaValueType.Table) continue;
            var row = rows[r].Read<LuaTable>();
            fields.Clear();
            if (names.Count > 0) foreach (var name in names) fields.Add(Text(row[name]));
            else for (var i = 1; i <= row.ArrayLength; i++) fields.Add(Text(row[i]));
            AppendLine(sb, fields);
        }
        return sb.ToString();
    }

    private static string Text(LuaValue value) => value.Type switch
    {
        LuaValueType.Nil => "",
        LuaValueType.Number => value.Read<double>().ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static void AppendLine(StringBuilder sb, List<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var field = fields[i];
            if (field.AsSpan().IndexOfAny(",\"\r\n") >= 0) sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            else sb.Append(field);
        }
        sb.Append("\r\n");
    }
}
