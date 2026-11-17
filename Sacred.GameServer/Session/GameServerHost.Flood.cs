using Sacred.GameServer.Net;
using Sacred.GameServer.Wire;

namespace Sacred.GameServer.Session;

/// <summary>
/// Flood protection (<see cref="FloodOptions"/>): per player token buckets for received bytes, messages and chat lines, a
/// per-address connection limit, drop-then-kick. Off in plain relay games unless <c>--flood-protection</c> is given (the
/// original server has none), on with <c>--authoritative</c>. A player that has not entered the world yet uploads freely for
/// <c>FLOOD_UPLOAD_GRACE</c> seconds after admission (a host's world upload is one large burst). Excess chat lines are
/// dropped quietly; other excess traffic is dropped and counted, and <c>FLOOD_KICK_AFTER_DROPS</c> drops within
/// <c>FLOOD_STRIKE_WINDOW</c> seconds remove the player (reason 8).
/// </summary>
public sealed partial class GameServerHost
{
    private const uint ReasonFlood = 8;

    private bool FloodOn => _config.Flood.IsEnabled(_config.Auth.Enabled);

    /// <summary>Messages and frames dropped by the flood protection since start (diagnostics).</summary>
    public long FloodDrops { get; private set; }

    /// <summary>A new link: refused when its address already has the allowed number of open connections.</summary>
    private bool AdmitConnection(IClientLink connection)
    {
        var limit = _config.Flood.MaxConnectionsPerAddress;
        if (limit <= 0 || connection.RemoteEndPoint?.Address is not { } address) return true;
        var open = 0;
        foreach (var other in _sessions)
            if (other.Connection != connection && other.State != SessionState.Closed && !other.Connection.IsClosed &&
                address.Equals(other.Connection.RemoteEndPoint?.Address)) open++;
        if (open < limit) return true;
        Log.Info($"FLOOD: {connection.RemoteEndPoint} refused, {open} connections from that address already open.");
        return false;
    }

    private bool FloodFrame(PlayerSession s, int bytes, long now)
    {
        var grace = FloodGuard.InUploadGrace(s.Flood, s.InGame, now, _config.Flood);
        return Apply(s, FloodGuard.Frame(ref s.Flood, bytes, grace, now, _config.Flood), "data");
    }

    private bool FloodMessage(PlayerSession s, in GoldMessageView m)
    {
        var now = _clock.ElapsedMilliseconds;
        if (s.Flood.AdmittedMs == 0 && s.State >= SessionState.SlotAssigned) s.Flood.AdmittedMs = now;
        var grace = FloodGuard.InUploadGrace(s.Flood, s.InGame, now, _config.Flood);
        return Apply(s, FloodGuard.Message(ref s.Flood, m.Opcode, grace, now, _config.Flood), "messages");
    }

    private bool Apply(PlayerSession s, FloodGuard.Verdict verdict, string what)
    {
        if (verdict == FloodGuard.Verdict.Allow) return true;
        FloodDrops++;
        if (s.Flood.DroppedTotal == 1) Log.Info($"FLOOD: {s.Describe()} exceeds the {what} limit, excess is dropped.");
        if (verdict != FloodGuard.Verdict.Kick || s.State == SessionState.Closed) return false;
        Log.Info($"FLOOD: {s.Describe()} removed after {s.Flood.Strikes} dropped {what} in {_config.Flood.StrikeWindowSeconds} s.");
        if (s.Slot != 0 && _slots[s.Slot] == s) RemovePlayer(s, ReasonFlood, notifyVictim: true);
        else s.Connection.Close();
        return false;
    }
}
