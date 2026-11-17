using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sacred.GameServer.Scripting;

/// <summary>
/// One mod's own storage: a key-value file <c>{data folder}/{mod}.json</c>. Values are what Lua can hold: numbers, strings,
/// booleans and nested tables (lists and maps), kept here as <see cref="double"/>, <see cref="string"/>, <see cref="bool"/>,
/// <see cref="List{T}"/> and <see cref="Dictionary{TKey,TValue}"/> and written with <see cref="Utf8JsonWriter"/> (no reflection,
/// Native AOT safe). Per-hero values live under keys starting with <see cref="HeroPrefix"/>, each an object that also holds the
/// hero's character name for ladders. Files are written to a temporary file and moved over the old one. The game server itself
/// keeps no mod state: a mod that wants a ladder, a list of recent deaths or anything else stores it here.
/// </summary>
public sealed class ModStore
{
    public const string HeroPrefix = "@";
    public const string NameKey = "_name";
    public const string UnknownName = "unknown hero";
    private readonly string _path;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private bool _dirty;

    public ModStore(string path) { _path = path; Load(); }

    public string Path => _path;
    public int Count => _values.Count;
    /// <summary>Largest file a mod may keep (serialized characters); saves beyond it fail with an error to the script.</summary>
    public int MaxChars { get; init; } = 16 * 1024 * 1024;

    // ---- plain keys --------------------------------------------------------------------------------------------------

    public bool TryGet(string key, out object? value) => _values.TryGetValue(key, out value);

    public void Set(string key, object? value)
    {
        if (value is null) { if (_values.Remove(key)) _dirty = true; return; }
        _values[key] = value;
        _dirty = true;
    }

    public IEnumerable<string> Keys(string prefix) => _values.Keys.Where(k => !k.StartsWith(HeroPrefix, StringComparison.Ordinal) && k.StartsWith(prefix, StringComparison.Ordinal))
        .OrderBy(k => k, StringComparer.Ordinal);

    // ---- per hero ------------------------------------------------------------------------------------------------------

    public bool TryGetHero(string owner, string key, out object? value)
    {
        value = null;
        return _values.TryGetValue(HeroPrefix + owner, out var map) && map is Dictionary<string, object?> hero && hero.TryGetValue(key, out value);
    }

    public void SetHero(string owner, string? character, string key, object? value)
    {
        if (!_values.TryGetValue(HeroPrefix + owner, out var map) || map is not Dictionary<string, object?> hero)
            _values[HeroPrefix + owner] = hero = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (character is { Length: > 0 }) hero[NameKey] = DisplayName(character);
        if (value is null) hero.Remove(key); else hero[key] = value;
        _dirty = true;
    }

    /// <summary>The heroes with the highest number under <paramref name="key"/>, with their character names (never the owner key).</summary>
    public List<(string Owner, string Name, double Value)> Top(string key, int count)
    {
        var rows = new List<(string, string, double)>();
        foreach (var (stored, map) in _values)
        {
            if (!stored.StartsWith(HeroPrefix, StringComparison.Ordinal) || map is not Dictionary<string, object?> hero || hero.GetValueOrDefault(key) is not double value) continue;
            rows.Add((stored[HeroPrefix.Length..], hero.GetValueOrDefault(NameKey) is string name ? DisplayName(name) : UnknownName, value));
        }
        rows.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        if (rows.Count > count) rows.RemoveRange(count, rows.Count - count);
        return rows;
    }

    /// <summary>A name that is safe to show anybody: the character part of "account.character", never an owner key.</summary>
    public static string DisplayName(string name)
    {
        if (name.StartsWith("id:", StringComparison.Ordinal) || name.StartsWith("name:", StringComparison.Ordinal) || name.StartsWith(HeroPrefix, StringComparison.Ordinal))
            return UnknownName;
        var dot = name.IndexOf('.');
        var character = dot >= 0 ? name[(dot + 1)..] : name;
        return character.Length == 0 ? UnknownName : character;
    }

    // ---- file ------------------------------------------------------------------------------------------------------------

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_path), new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var property in document.RootElement.EnumerateObject()) _values[property.Name] = FromJson(property.Value);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Info($"SCRIPT: mod data '{_path}' not read ({e.Message}); starting empty, the old file is kept as .bad.");
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch (IOException) { }
            _values.Clear();
        }
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
        _ => null,
    };

    /// <summary>The whole store as JSON (also used to check the size limit).</summary>
    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, SkipValidation = false }))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in _values.OrderBy(v => v.Key, StringComparer.Ordinal)) { writer.WritePropertyName(key); Write(writer, value, 0); }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public static void Write(Utf8JsonWriter writer, object? value, int depth)
    {
        switch (value)
        {
            case double d when double.IsFinite(d): writer.WriteNumberValue(d); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case List<object?> list when depth < 32:
                writer.WriteStartArray();
                foreach (var item in list) Write(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case Dictionary<string, object?> map when depth < 32:
                writer.WriteStartObject();
                foreach (var (key, item) in map) { writer.WritePropertyName(key); Write(writer, item, depth + 1); }
                writer.WriteEndObject();
                break;
            default: writer.WriteNullValue(); break;
        }
    }

    public void Flush()
    {
        if (!_dirty) return;
        var temporary = _path + ".tmp";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, Serialize());
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info($"SCRIPT: mod data '{_path}' not written: {e.Message}"); }
    }

    internal static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
