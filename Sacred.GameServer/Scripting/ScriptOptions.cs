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

    public bool IsEnabled => Enabled ?? System.IO.Directory.Exists(Directory);

    public bool Apply(string key, string value)
    {
        switch (key)
        {
            case "SCRIPTS": Enabled = Int(value, 1) != 0; return true;
            case "SCRIPTS_DIR" or "SCRIPTS_DIRECTORY": Directory = value; return true;
            case "SCRIPTS_DATA": DataFile = value; return true;
            case "SCRIPTS_BUDGET": InstructionBudget = Math.Clamp(Int(value, InstructionBudget), 10_000, 100_000_000); return true;
        }
        return false;
    }

    private static int Int(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
