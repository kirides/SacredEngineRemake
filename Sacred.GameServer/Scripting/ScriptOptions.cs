using System.Globalization;

namespace Sacred.GameServer.Scripting;

/// <summary>Server-side Lua scripts (<c>SCRIPTS_*</c> keys). Scripts run when the scripts folder exists, unless <c>SCRIPTS=0</c>.</summary>
public sealed class ScriptOptions
{
    /// <summary>Explicit switch; null = on when <see cref="Directory"/> exists.</summary>
    public bool? Enabled { get; set; }
    /// <summary>Folder with the <c>*.lua</c> files, loaded in name order and reloaded when one changes.</summary>
    public string Directory { get; set; } = "scripts";
    /// <summary>File for the scripts' persistent per-player and global values (<c>server.set</c>/<c>server.get</c>).</summary>
    public string DataFile { get; set; } = "script-data.json";
    /// <summary>Lua instructions one event handler or timer may run before it is stopped (a mistake such as an endless loop
    /// must not freeze the game).</summary>
    public int InstructionBudget { get; set; } = 1_000_000;
    /// <summary>Largest answer of a script endpoint in characters (larger: 500 "response too large").</summary>
    public int MaxResponseChars { get; set; } = 2 * 1024 * 1024;
    /// <summary>Largest string string.rep may build. Concatenation cannot be capped by LuaCSharp; the instruction budget
    /// bounds how often a script can double a string in one call.</summary>
    public int MaxStringChars { get; set; } = 4 * 1024 * 1024;

    public bool IsEnabled => Enabled ?? System.IO.Directory.Exists(Directory);

    public bool Apply(string key, string value)
    {
        switch (key)
        {
            case "SCRIPTS": Enabled = Int(value, 1) != 0; return true;
            case "SCRIPTS_DIR" or "SCRIPTS_DIRECTORY": Directory = value; return true;
            case "SCRIPTS_DATA": DataFile = value; return true;
            case "SCRIPTS_MAX_RESPONSE": MaxResponseChars = Math.Clamp(Int(value, MaxResponseChars), 1024, 64 * 1024 * 1024); return true;
            case "SCRIPTS_MAX_STRING": MaxStringChars = Math.Clamp(Int(value, MaxStringChars), 1024, 256 * 1024 * 1024); return true;
            case "SCRIPTS_BUDGET": InstructionBudget = Math.Clamp(Int(value, InstructionBudget), 10_000, 100_000_000); return true;
        }
        return false;
    }

    private static int Int(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
