using Sacred.GameServer.Net;

namespace Sacred.GameServer.Session;

public enum SessionState { Connected, LoggedIn, SlotAssigned, Playing, Closed }

/// <summary>Per client state, owned by the game loop thread.</summary>
public sealed class PlayerSession
{
    public required ClientConnection Connection { get; init; }
    public uint TinCatId;
    public byte Slot;
    public SessionState State;
    public int LobbyIdentity;
    public string Name = "";
    public ushort TxSequence;
    public ushort RxSequence;
    public bool PasswordAccepted;
    public int PasswordTries;
    public bool IsHost;
    public long LastReceiveMs, LastKeepAliveMs;

    public string Describe() => Slot != 0 ? $"#{Slot} {Name}" : $"{Connection.RemoteEndPoint}";
}
