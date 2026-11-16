using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Sacred.GameServer.Config;
using Sacred.GameServer.Lan;
using Sacred.GameServer.Net;
using Sacred.GameServer.Wire;
using Sacred.Network.Admission;

namespace Sacred.GameServer.Session;

/// <summary>
/// The dedicated server: accepts clients, performs TinCat login and FIRSTCONTACT admission and runs the fixed-step game loop.
/// Everything below <see cref="Run"/> executes on one thread; I/O threads only enqueue verified frames. The loop and packet paths
/// use preallocated scratch buffers, pooled arrays and reusable socket args and do not allocate.
/// </summary>
public sealed class GameServerHost : IDisposable
{
    public const int MaxSlots = 16;
    private readonly GameServerConfig _config;
    private readonly AdmissionRules _rules;
    private readonly ConcurrentQueue<InboundFrame> _inbox = new();
    private readonly TcpAcceptor _acceptor;
    private readonly PlayerSession?[] _slots = new PlayerSession?[MaxSlots + 1];
    private readonly List<PlayerSession> _sessions = new(64);
    private readonly List<(ClientConnection Connection, long Due)> _retired = new(16);
    private readonly byte[] _batch = new byte[GoldWire.MaximumBatchBytes + 16];   // decode scratch
    private readonly byte[] _build = new byte[256 * 1024];                           // outgoing batch scratch
    private readonly byte[] _envelope = new byte[256 * 1024 + 64];                   // outgoing envelope scratch
    private readonly ZlibInflater _inflater = new();
    private readonly ZlibDeflater _deflater = new();
    private readonly byte[] _relay = new byte[16 * 1024];
    private readonly bool[] _seenUnhandled = new bool[4096];
    private readonly LanAnnouncer? _lan;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private uint _nextTinCatId = 0x100;
    private long _nextLanMs;
    private volatile bool _stop;

    public long Tick { get; private set; }
    public int TcpPort => _acceptor.Port;
    public int PlayerCount { get; private set; }
    /// <summary>Hook for gameplay modules (world simulation) invoked once per tick after packets were processed.</summary>
    public Action<long>? OnTick;

    public GameServerHost(GameServerConfig config, AdmissionRules rules, LanAnnouncer? lan)
    {
        _config = config; _rules = rules; _lan = lan;
        _acceptor = new TcpAcceptor(config.TcpPort, _inbox);
    }

    public void Stop() => _stop = true;

    public void Start() => _acceptor.Start();

    public void Run()
    {
        Start();
        Log.Info($"--- Gameserver Init [Ping: {_config.ListenPort}, TCP: {TcpPort}, Name: {_config.ServerName}, Password: {(_config.Password is null ? "none" : "set")}] ---");
        long next = _clock.ElapsedMilliseconds;
        while (!_stop)
        {
            RunTick();
            next += _config.TickMilliseconds;
            var wait = next - _clock.ElapsedMilliseconds;
            if (wait > 1) Thread.Sleep((int)(wait - 1));
            else if (wait < -250) next = _clock.ElapsedMilliseconds; // fell behind: do not spiral
            while (_clock.ElapsedMilliseconds < next && !_stop) Thread.SpinWait(50);
        }
        foreach (var s in _sessions) s.Connection.Close();
        Log.Info("--- Gameserver Shutdown ---");
    }

    /// <summary>One loop iteration. Public so tests can single-step the server.</summary>
    public void RunTick()
    {
        var now = _clock.ElapsedMilliseconds;
        Tick++;
        while (_acceptor.Accepted.TryDequeue(out var accepted)) Session(accepted);
        while (_inbox.TryDequeue(out var frame))
        {
            try { Handle(frame, now); }
            finally { if (frame.Payload.Length > 0) ArrayPool<byte>.Shared.Return(frame.Payload); }
        }
        for (var i = _sessions.Count - 1; i >= 0; i--)
        {
            var s = _sessions[i];
            if (s.State == SessionState.Closed) continue;
            if (now - s.LastReceiveMs > 30_000) { Log.Info($"{s.Describe()} timed out"); s.Connection.Close(); continue; }
            if (s.State >= SessionState.LoggedIn && now - s.LastKeepAliveMs >= 5_000) { s.LastKeepAliveMs = now; SendControl(s, TinCatWire.StayingAlive, 2); }
        }
        OnTick?.Invoke(Tick);
        foreach (var s in _sessions) s.Connection.Flush();
        for (var i = _retired.Count - 1; i >= 0; i--)
            if (now >= _retired[i].Due) { _retired[i].Connection.ReleaseBuffers(); _retired.RemoveAt(i); }
        if (_lan is not null && now >= _nextLanMs) { _nextLanMs = now + 3000; _lan.Announce(PlayerCount); }
    }

    private PlayerSession Session(ClientConnection connection)
    {
        if (connection.Session is PlayerSession existing) return existing;
        var created = new PlayerSession { Connection = connection, LastReceiveMs = _clock.ElapsedMilliseconds, LastKeepAliveMs = _clock.ElapsedMilliseconds };
        connection.Session = created;
        _sessions.Add(created);
        return created;
    }

    private void Handle(in InboundFrame frame, long now)
    {
        var s = Session(frame.Connection);
        if (frame.Type == InboundFrame.Disconnected) { Retire(s, now); return; }
        if (s.State == SessionState.Closed) return;
        s.LastReceiveMs = now;
        switch (frame.Type)
        {
            case TinCatWire.LoginRequest:
                if (s.State != SessionState.Connected || frame.Length != TinCatWire.LoginPayloadBytes) { s.Connection.Close(); return; }
                AcceptLogin(s, frame.Span);
                break;
            case TinCatWire.CustomData when s.State >= SessionState.LoggedIn:
                HandleCustomData(s, frame.Span, now);
                break;
            case TinCatWire.StayingAlive or TinCatWire.TimeAlignment:
                break;
            case TinCatWire.Logoff:
                s.Connection.Close();
                break;
            default:
                s.Connection.Close(); // unsupported control frame or data before login
                break;
        }
    }

    private void AcceptLogin(PlayerSession s, ReadOnlySpan<byte> request)
    {
        s.TinCatId = _nextTinCatId++;
        s.State = SessionState.LoggedIn;
        var frame = s.Connection.BeginFrame(TinCatWire.HeaderBytes + TinCatWire.LoginPayloadBytes);
        var body = frame[TinCatWire.HeaderBytes..];
        request.CopyTo(body);
        BinaryPrimitives.WriteUInt32LittleEndian(body, TinCatWire.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(body[4..], s.TinCatId);
        TinCatWire.SealFrame(frame, TinCatWire.ServerId, s.TinCatId, TinCatWire.LoginAccepted, TinCatWire.LoginPayloadBytes);
        s.Connection.CommitFrame(frame.Length);
    }

    private void HandleCustomData(PlayerSession s, ReadOnlySpan<byte> payload, long now)
    {
        if (payload.Length < 4 + GoldWire.EnvelopeHeaderBytes || BinaryPrimitives.ReadUInt16LittleEndian(payload) != TinCatWire.GameModule ||
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]) != 0) { Drop(s, "bad TinCat module"); return; }
        var envelope = payload[4..];
        if (!GoldWire.TryDecodeEnvelope(envelope, _batch, _inflater, out var length)) { Drop(s, "bad envelope"); return; }
        var reader = new GoldBatchReader(_batch.AsSpan(0, length));
        while (reader.TryRead(out var message))
        {
            if (s.State == SessionState.Closed) return;
            Dispatch(s, message);
        }
        if (reader.Failed) Drop(s, "bad message batch");
    }

    private void Drop(PlayerSession s, string why) { Log.Info($"{s.Describe()} dropped: {why}"); s.Connection.Close(); }

    // ---- message dispatch -------------------------------------------------------------------------------------------

    private void Dispatch(PlayerSession s, in GoldMessageView m)
    {
        switch (m.Opcode)
        {
            case 0x10: OnFirstContact(s, m); return;
            case 0x12: OnPassword(s, m); return;
        }
        if (s.State < SessionState.SlotAssigned) { Drop(s, $"opcode 0x{m.Opcode:X} before admission"); return; }
        if (m.Sender != s.Slot) { Reject(s, RejectionReason.InvalidSender, 0); return; }
        // Original (FUN_004eb0a0): sequence must advance by one; a gap is reported as R_MESSAGE_ID_WARNING.
        s.RxSequence = m.Sequence;
        if (_config.Password is not null && !s.PasswordAccepted) return; // locked out until the password was accepted
        Route(s, m);
    }

    /// <summary>Gameplay routing. Stage 1 relays chat only; every other opcode is logged once so that the next stage can be driven by real traffic.</summary>
    private void Route(PlayerSession s, in GoldMessageView m)
    {
        switch (m.Opcode)
        {
            case 0x032: // chat: speaker word and name are stamped by the server (no spoofing)
                RelayChat(s, m);
                break;
            case 0x0B: // empty keep-alive pulse of the client
                break;
            default:
                if (m.Opcode < _seenUnhandled.Length && !_seenUnhandled[m.Opcode])
                {
                    _seenUnhandled[m.Opcode] = true;
                    Log.Info($"unhandled opcode 0x{m.Opcode:X3} ({m.Payload.Length} bytes) from {s.Describe()}");
                }
                break;
        }
    }

    private const int ChatNameOffset = 8, ChatTextOffset = 264;

    private void RelayChat(PlayerSession s, in GoldMessageView m)
    {
        if (m.Payload.Length < ChatTextOffset + 2 || m.Payload.Length > _relay.Length) return;
        var chat = _relay.AsSpan(0, m.Payload.Length);
        m.Payload.CopyTo(chat);
        BinaryPrimitives.WriteUInt32LittleEndian(chat, s.Slot);
        var name = chat.Slice(ChatNameOffset, ChatTextOffset - ChatNameOffset);
        name.Clear();
        var chars = Math.Min(s.Name.Length, name.Length / 2 - 1);
        for (var i = 0; i < chars; i++) BinaryPrimitives.WriteUInt16LittleEndian(name[(i * 2)..], s.Name[i]);
        var recipient = BinaryPrimitives.ReadInt32LittleEndian(chat[4..]);
        if (recipient > 0)
        {
            if (recipient <= MaxSlots && _slots[recipient] is { } target && target.State >= SessionState.SlotAssigned)
                SendMessage(target, 0x032, 0, s.Slot, chat);
            return;
        }
        Broadcast(0x032, s.Slot, chat, except: s);
    }

    private void OnFirstContact(PlayerSession s, in GoldMessageView m)
    {
        if (s.State != SessionState.LoggedIn || m.Sender != 0) { Drop(s, "unexpected FIRSTCONTACT"); return; }
        var reason = _rules.Check(m.Payload, out var mask, out var identity);
        if (reason == 0 && identity != -1)
            foreach (var other in _sessions)
                if (other != s && other.State >= SessionState.SlotAssigned && other.LobbyIdentity == identity) { reason = RejectionReason.DuplicateIdentity; break; }
        byte slot = 0;
        if (reason == 0)
        {
            for (byte i = 1; i <= MaxSlots; i++) if (_slots[i] is null) { slot = i; break; }
            if (slot == 0 || PlayerCount >= _config.MaxPlayers) { Reject(s, RejectionReason.LobbyRefused, 0); return; }
        }
        if (reason != 0) { Reject(s, reason, mask); return; }

        s.Slot = slot; _slots[slot] = s; s.State = SessionState.SlotAssigned; PlayerCount++;
        s.LobbyIdentity = identity; s.Name = AdmissionRules.PlayerName(m.Payload) ?? "";
        s.PasswordAccepted = _config.Password is null;
        var hostAssigned = false;
        foreach (var other in _sessions) if (other.IsHost && other.State != SessionState.Closed) hostAssigned = true;
        s.IsHost = !hostAssigned;
        Span<byte> reply = stackalloc byte[6];
        reply[0] = (byte)(s.IsHost ? 1 : 0);
        reply[1] = slot;
        SendMessage(s, 0x11, 0, 0, reply);
        Span<byte> flags = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, _config.GameFlags());
        SendMessage(s, 0x01E, 0, 0, flags);
        Log.Info($"FIRSTCONTACT from {s.Connection.RemoteEndPoint}: '{s.Name}' admitted as #{slot}{(s.IsHost ? " (host)" : "")}");
    }

    private void OnPassword(PlayerSession s, in GoldMessageView m)
    {
        if (s.State != SessionState.SlotAssigned || m.Sender != s.Slot) { Drop(s, "unexpected password message"); return; }
        if (_config.Password is not { } expected) { SendMessage(s, 0x14, 0, 0, default); s.PasswordAccepted = true; return; }
        if (m.Payload.Length == 0 || (m.Payload.Length & 1) != 0) { Log.Info($"Empty password received from player #{s.Slot}!"); SendMessage(s, 0x13, 0, 0, default); return; }
        var ok = m.Payload.Length == expected.Length * 2;
        for (var i = 0; ok && i < expected.Length; i++) ok = BinaryPrimitives.ReadUInt16LittleEndian(m.Payload[(i * 2)..]) == expected[i];
        if (ok)
        {
            s.PasswordAccepted = true; s.State = SessionState.Playing;
            Log.Info($"Player #{s.Slot} has sent the correct password.");
            SendMessage(s, 0x14, 0, 0, default);
            return;
        }
        Log.Info($"#{s.Slot} has sent the wrong password!");
        SendMessage(s, 0x13, 0, 0, default);
        if (++s.PasswordTries >= _config.MaxPasswordTries) { Log.Info($"#{s.Slot} kicked: too many password tries"); s.Connection.Flush(); s.Connection.Close(); }
    }

    private void Reject(PlayerSession s, uint reason, uint mask)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, reason);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], mask);
        SendMessage(s, 0x3F, 0, 0, payload);
        Log.Info($"FIRSTCONTACT from {s.Connection.RemoteEndPoint} rejected: reason {reason}, data mask 0x{mask:X8}");
    }

    private void Retire(PlayerSession s, long now)
    {
        if (s.State == SessionState.Closed) return;
        if (s.Slot != 0 && _slots[s.Slot] == s) { _slots[s.Slot] = null; PlayerCount--; }
        Log.Info($"{s.Describe()} disconnected");
        var wasHost = s.IsHost;
        s.State = SessionState.Closed; s.IsHost = false;
        _sessions.Remove(s);
        _retired.Add((s.Connection, now + 1000));
        if (wasHost)
            foreach (var other in _sessions)
                if (other.State >= SessionState.SlotAssigned) { other.IsHost = true; SendMessage(other, 0x00D, 0, 0, default); Log.Info($"host role moved to #{other.Slot}"); break; }
    }

    // ---- sending ----------------------------------------------------------------------------------------------------

    private void SendControl(PlayerSession s, uint type, int payloadBytes)
    {
        var frame = s.Connection.BeginFrame(TinCatWire.HeaderBytes + payloadBytes);
        frame.Slice(TinCatWire.HeaderBytes, payloadBytes).Clear();
        TinCatWire.SealFrame(frame, TinCatWire.ServerId, s.TinCatId, type, payloadBytes);
        s.Connection.CommitFrame(frame.Length);
    }

    /// <summary>Send one server message (sender 0 = GameServer, origin = originating player).</summary>
    public void SendMessage(PlayerSession s, ushort opcode, byte sender, byte origin, ReadOnlySpan<byte> payload)
    {
        if (s.State == SessionState.Closed || GoldWire.MessageHeaderBytes + payload.Length > _build.Length) return;
        var length = GoldWire.WriteMessage(_build, opcode, sender, origin, ++s.TxSequence, payload);
        SendBatch(s, _build.AsSpan(0, length));
    }

    /// <summary>Send a pre-built batch of messages.</summary>
    public void SendBatch(PlayerSession s, ReadOnlySpan<byte> batch)
    {
        var envelopeLength = GoldWire.EncodeEnvelope(batch, _envelope, _deflater);
        if (envelopeLength < 0) return;
        var frame = s.Connection.BeginFrame(TinCatWire.HeaderBytes + 4 + envelopeLength);
        var body = frame[TinCatWire.HeaderBytes..];
        BinaryPrimitives.WriteUInt16LittleEndian(body, TinCatWire.GameModule);
        BinaryPrimitives.WriteUInt16LittleEndian(body[2..], 0);
        _envelope.AsSpan(0, envelopeLength).CopyTo(body[4..]);
        TinCatWire.SealFrame(frame, TinCatWire.ServerId, s.TinCatId, TinCatWire.CustomData, 4 + envelopeLength);
        s.Connection.CommitFrame(frame.Length);
    }

    /// <summary>Relay a message to every admitted player (server sender 0, retained origin).</summary>
    public void Broadcast(ushort opcode, byte origin, ReadOnlySpan<byte> payload, PlayerSession? except = null)
    {
        for (var slot = 1; slot <= MaxSlots; slot++)
            if (_slots[slot] is { } target && target != except && target.State >= SessionState.SlotAssigned)
                SendMessage(target, opcode, 0, origin, payload);
    }

    public void Dispose()
    {
        _acceptor.Dispose();
        _lan?.Dispose();
    }
}
