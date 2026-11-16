using System.Globalization;

namespace Sacred.GameServer.Config;

/// <summary>Pre-configured game mode (Gameserver.cfg NETWORK_PRECONFIG_MODE). Numeric order follows the original menu; the flag mapping is a best effort.</summary>
public enum PreconfigMode { None = 0, FreeGame = 1, PlayerKiller = 2, Campaign = 3, CampaignUnderworld = 4 }

/// <summary>
/// Settings of the original dedicated server (Gameserver.cfg, KEY=VALUE) plus remake-only extras.
/// Command line options use the same names as <c>--network-port-tcp=2005</c> (case-insensitive, '_' or '-').
/// </summary>
public sealed class GameServerConfig
{
    public int TcpPort { get; set; } = 2005;
    /// <summary>UDP port used for LAN announcements (original NETWORK_PORT_LISTEN).</summary>
    public int ListenPort { get; set; } = 2006;
    public int LobbyPort { get; set; }
    public string? Password { get; set; }
    public PreconfigMode Mode { get; set; }
    public int Level { get; set; }
    public int MaxPlayers { get; set; } = 16;
    public bool LadderExport { get; set; }

    // Remake extras
    public string ServerName { get; set; } = "Sacred Gameserver";
    public string? GameDirectory { get; set; }
    public string? PublicAddress { get; set; }
    public bool AcceptAnyData { get; set; }
    public bool LanAnnounce { get; set; } = true;
    public int TickMilliseconds { get; set; } = 20;
    public int MaxPasswordTries { get; set; } = 3;
    public string? LobbyHost { get; set; }

    public static GameServerConfig Load(string[] args)
    {
        var config = new GameServerConfig();
        var cfgPath = "Gameserver.cfg";
        foreach (var arg in args)
            if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase)) cfgPath = arg[9..];
        if (File.Exists(cfgPath))
            foreach (var line in File.ReadLines(cfgPath)) config.ApplyLine(line);
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase)) continue;
            config.ApplyLine(arg[2..]);
        }
        return config;
    }

    public void ApplyLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] is '#' or ';' or '/') return;
        var split = line.IndexOf('=');
        var key = (split < 0 ? line : line[..split]).Trim().Replace('-', '_').ToUpperInvariant();
        var value = split < 0 ? "1" : line[(split + 1)..].Trim();
        switch (key)
        {
            case "NETWORK_PORT_TCP" or "PORT": TcpPort = Int(value, TcpPort); break;
            case "NETWORK_PORT_LISTEN" or "LISTEN_PORT": ListenPort = Int(value, ListenPort); break;
            case "NETWORK_LOBBYPORT": LobbyPort = Int(value, LobbyPort); break;
            case "NETWORK_PASSWORD" or "PASSWORD": Password = value.Length == 0 ? null : value; break;
            case "NETWORK_PRECONFIG_MODE": Mode = (PreconfigMode)Math.Clamp(Int(value, 0), 0, 4); break;
            case "NETWORK_PRECONFIG_LEVEL": Level = Math.Clamp(Int(value, 0), 0, 5); break;
            case "NETWORK_PRECONFIG_MAXPLAYERS": MaxPlayers = Math.Clamp(Int(value, 16), 1, 16); break;
            case "LADDER_EXPORT": LadderExport = Int(value, 0) != 0; break;
            case "SERVER_NAME" or "NAME": ServerName = value; break;
            case "GAME_DIR" or "GAME_DIRECTORY": GameDirectory = value; break;
            case "PUBLIC_ADDRESS": PublicAddress = value; break;
            case "ACCEPT_ANY_DATA": AcceptAnyData = Int(value, 1) != 0; break;
            case "LAN_ANNOUNCE": LanAnnounce = Int(value, 1) != 0; break;
            case "TICK_MS": TickMilliseconds = Math.Clamp(Int(value, 20), 5, 100); break;
            case "MAX_PASSWORD_TRIES": MaxPasswordTries = Math.Clamp(Int(value, 3), 1, 100); break;
            case "LOBBY_HOST": LobbyHost = value.Length == 0 ? null : value; break;
        }
    }

    private static int Int(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    /// <summary>The session game-flags word advertised in the LAN announcement and opcode 0x1E.</summary>
    public uint GameFlags()
    {
        uint flags = Mode switch
        {
            PreconfigMode.PlayerKiller => 0x40,
            PreconfigMode.Campaign => 0x10,
            PreconfigMode.CampaignUnderworld => 0x40000,
            _ => 0,
        };
        flags |= Level switch { 1 => 0x100u, 2 => 0x200u, 3 => 0x400u, 4 => 0x800u, _ => 0u };
        return flags;
    }
}
