namespace Sacred.GameServer.Web;

/// <summary>What the web UI may do and see. Implemented by the game server; every member is safe to call from any thread.</summary>
public interface IServerAdmin
{
    ServerOverview Overview();
    IReadOnlyList<PlayerInfo> Players();
    /// <summary>Id of the newest chat entry (0 when none yet).</summary>
    long ChatHead { get; }
    /// <summary>Entries with an id greater than <paramref name="afterId"/>, oldest first (at most <paramref name="max"/>).</summary>
    IReadOnlyList<ChatEntry> ChatSince(long afterId, int max);
    IReadOnlyList<BanEntry> Bans();
    /// <summary>Queues a command for the game loop. False when the queue is full.</summary>
    bool Submit(in AdminCommand command);
}

public sealed record ServerOverview(
    string Name, long Tick, double UptimeSeconds, int TcpPort, int UdpPort, int Players, int MaxPlayers, bool Locked, bool Paused,
    bool PasswordSet, bool WorldAvailable, uint GameFlags, int Mode, int Level, bool LobbyConfigured, bool LobbyOnline, string? LobbyHost, string Version);

public sealed record PlayerInfo(
    int Slot, string Name, string Address, string Transport, bool IsHost, bool InGame, bool Ready, bool PasswordAccepted, uint LatencyMs,
    double ConnectedSeconds);

public enum ChatKind { Public = 0, Private = 1, Broadcast = 2, ServerMessage = 3, System = 4 }

/// <summary>One line of the live log. FromSlot/ToSlot 0 means the server; ToSlot -1 means everybody.</summary>
public sealed record ChatEntry(long Id, long TimeUnixMs, ChatKind Kind, int FromSlot, string FromName, int ToSlot, string Text);

public sealed record BanEntry(string Address, string Reason, long CreatedUnixMs);

public enum AdminCommandKind
{
    None = 0,
    /// <summary>Text to every player.</summary>
    Broadcast,
    /// <summary>Slot, Text: private message from the server.</summary>
    Message,
    /// <summary>Slot, Value = kick reason.</summary>
    Kick,
    /// <summary>Slot, Text = reason: ban the player's address and kick it.</summary>
    Ban,
    /// <summary>Text = address to ban (no player needed).</summary>
    BanAddress,
    /// <summary>Text = address to lift.</summary>
    Unban,
    /// <summary>Value 1 = lock the session against new players, 0 = unlock.</summary>
    Lock,
    /// <summary>Value 1 = pause the game, 0 = continue.</summary>
    Pause,
    SetMaxPlayers,
    /// <summary>Text = new password; empty removes it.</summary>
    SetPassword,
    SetName,
    /// <summary>Value = mode (0..4), Slot = level (0..5); only before the host world exists.</summary>
    SetMode,
    /// <summary>Kick everybody and reset the session.</summary>
    EndSession,
    Shutdown,
}

public readonly record struct AdminCommand(AdminCommandKind Kind, int Slot = 0, int Value = 0, string? Text = null);
