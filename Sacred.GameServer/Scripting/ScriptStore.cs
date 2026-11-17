using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sacred.GameServer.Scripting;

/// <summary>
/// Persistent values of the scripts: one dictionary per player key ("id:&lt;lobby perm id&gt;:&lt;hero block&gt;" for lobby players,
/// "name:&lt;name&gt;" otherwise) plus "global". Values are numbers, strings or booleans. Written to a temporary file and moved
/// over the real one, so a crash never leaves half a file; flushed when dirty every few seconds, when a player leaves and at
/// shutdown.
/// </summary>
public sealed class ScriptStore
{
    public const string GlobalKey = "global";
    private readonly string _path;
    private Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.Ordinal);
    private bool _dirty;

    public ScriptStore(string path) => _path = path;

    public int Owners => _values.Count;

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try { _values = JsonSerializer.Deserialize(File.ReadAllText(_path), ScriptStoreJson.Default.DictionaryStringDictionaryStringString) ?? new(StringComparer.Ordinal); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Info($"SCRIPT: data file '{_path}' not read ({e.Message}); starting empty, the old file is kept as .bad.");
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch (IOException) { }
            _values = new(StringComparer.Ordinal);
        }
    }

    public bool TryGet(string owner, string key, out object? value)
    {
        value = null;
        if (!_values.TryGetValue(owner, out var map) || !map.TryGetValue(key, out var raw) || raw.Length < 2) return false;
        value = raw[0] switch
        {
            'n' => double.Parse(raw.AsSpan(2), NumberStyles.Float, CultureInfo.InvariantCulture),
            'b' => raw[2] == '1',
            _ => raw[2..],
        };
        return true;
    }

    /// <summary>Stores a number, string or boolean; null removes the key.</summary>
    public void Set(string owner, string key, object? value)
    {
        if (value is null)
        {
            if (_values.TryGetValue(owner, out var existing) && existing.Remove(key)) _dirty = true;
            return;
        }
        if (!_values.TryGetValue(owner, out var map)) _values[owner] = map = new(StringComparer.Ordinal);
        map[key] = value switch
        {
            double d => "n:" + d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "b:1" : "b:0",
            _ => "s:" + value,
        };
        _dirty = true;
    }

    public void Flush()
    {
        if (!_dirty) return;
        var temporary = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values, ScriptStoreJson.Default.DictionaryStringDictionaryStringString));
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Info($"SCRIPT: data file '{_path}' not written: {e.Message}"); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal sealed partial class ScriptStoreJson : JsonSerializerContext;
