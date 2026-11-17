using System.Buffers.Binary;
using System.Text;
using Sacred.GameServer.Scripting;
using Sacred.GameServer.Wire;
using Sacred.Network.Gameplay;

namespace Sacred.GameServer.Session;

/// <summary>
/// Server-side Lua scripts (Scripting/ScriptHost.cs) wired into the game loop. Events:
/// <list type="bullet">
/// <item>"join" after admission, "enter" when the player's world starts (0x017/0x018), "leave" when it is removed;</item>
/// <item>"chat" for public chat 0x005 [u32][UTF-16 text]; a handler returning true keeps the line from being relayed;</item>
/// <item>"kill" for 0x17D [creature ref][killer word] (GameServer.exe case 0x4C647E applies native event 0x208, death, to its
/// creature with that word): the word is the killing player's slot, or an object of a player (a summon), credited to its
/// owner; otherwise the sender. The creature's type and level come from the server's object world (compact values of its
/// 0x16E/0x16F state). A creature is counted once even if several players report it.</item>
/// </list>
/// Everything runs on the game loop thread; a kick asked for by a script runs after the script call.
/// </summary>
public sealed partial class GameServerHost : IScriptServer
{
    private ScriptHost? _scripts;
    private ScriptStore? _scriptStore;
    private readonly List<(int Slot, string Reason)> _scriptKicks = new();
    private readonly uint[] _recentKills = new uint[512];
    private int _recentKillNext;
    private long _nextScriptFlushMs;
    private bool _inScriptKicks;

    /// <summary>The running script host (null when scripts are off).</summary>
    public ScriptHost? Scripts => _scripts;

    /// <summary>Loads the scripts and their data and starts watching the folder. Call before <see cref="Run"/>.</summary>
    public void StartScripts()
    {
        var options = _config.Scripts;
        _scriptStore = new ScriptStore(options.DataFile);
        _scriptStore.Load();
        _scripts = new ScriptHost(options, this, _scriptStore);
        _scripts.Load();
        _scripts.Watch();
        ExecuteScriptKicks();
    }

    private void PumpScripts(long now)
    {
        if (_scripts is null) return;
        _scripts.Pump(now);
        ExecuteScriptKicks();
        if (now >= _nextScriptFlushMs) { _nextScriptFlushMs = now + 10_000; _scriptStore?.Flush(); }
    }

    private void StopScripts() { _scripts?.Dispose(); _scriptStore?.Flush(); }

    // ---- hooks --------------------------------------------------------------------------------------------------------

    /// <summary>Route: returns true when a script consumed the message.</summary>
    private bool ScriptMessage(PlayerSession s, in GoldMessageView m)
    {
        switch (m.Opcode)
        {
            case 0x005 when m.Payload.Length > 4 && _scripts!.Has("chat"):
            {
                var consumed = _scripts.FireChat(ScriptPlayerOf(s), ChatText(m.Payload[4..]));
                ExecuteScriptKicks();
                return consumed;
            }
            case 0x17D when m.Payload.Length >= 8 && _scripts!.Has("kill"):
                OnKill(s, BinaryPrimitives.ReadUInt32LittleEndian(m.Payload) & 0x7FFFFFFF, BinaryPrimitives.ReadUInt32LittleEndian(m.Payload[4..]));
                return false;
        }
        return false;
    }

    private void ScriptPlayerEvent(string name, PlayerSession s)
    {
        if (_scripts is null || s.Slot == 0 || !_scripts.Has(name)) return;
        _scripts.FirePlayer(name, ScriptPlayerOf(s));
        if (name != "leave") ExecuteScriptKicks();
    }

    private void OnKill(PlayerSession sender, uint victim, uint killerWord)
    {
        foreach (var known in _recentKills) if (known == victim) return;
        _recentKills[_recentKillNext] = victim;
        _recentKillNext = (_recentKillNext + 1) % _recentKills.Length;
        var killer = killerWord is >= 1 and <= MaxSlots && _slots[killerWord] is { } p ? p
            : _world.TryGet(killerWord & 0x7FFFFFFF, out var owned) && owned.Owner is >= 1 and <= MaxSlots && _slots[owned.Owner] is { } owner ? owner
            : sender;
        uint type = 0; var level = 0;
        if (_world.TryGet(victim, out var entry))
        {
            var payload = entry.Payload;
            type = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            level = CompactLevel(payload[(entry.LongPrefix ? 24 : 8)..]);
        }
        _scripts!.FireKill(ScriptPlayerOf(killer), new ScriptMonster(victim, type, level));
        ExecuteScriptKicks();
    }

    /// <summary>Level word of a creature body with compact values (Gold 0x585C60: values at +445, level at +52), or 0.</summary>
    private static int CompactLevel(ReadOnlySpan<byte> body)
    {
        var values = GoldCreatureStateLayout.PrefixSize;
        if (body.Length < values + GoldCreatureValuesLayout.CompactWord56Offset + 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[GoldCreatureStateLayout.MarkerOffset..]) != GoldCreatureStateLayout.Marker ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[values..]) != GoldCreatureValuesLayout.Marker) return 0;
        return BinaryPrimitives.ReadUInt16LittleEndian(body[(values + GoldCreatureValuesLayout.CompactWord56Offset)..]);
    }

    private static string ChatText(ReadOnlySpan<byte> utf16)
    {
        var length = 0;
        while (length + 1 < utf16.Length && length < ChatTextBytes && (utf16[length] | utf16[length + 1]) != 0) length += 2;
        return Encoding.Unicode.GetString(utf16[..length]);
    }

    private ScriptPlayer ScriptPlayerOf(PlayerSession s)
    {
        var level = 0;
        if (_world.TryGet(s.Slot, out var hero)) level = CompactLevel(hero.Payload[(hero.LongPrefix ? 24 : 8)..]);
        var key = s.LobbyIdentity is not (0 or -1) ? $"id:{s.LobbyIdentity}:{s.CharacterBlock}" : "name:" + s.Name;
        return new ScriptPlayer(s.Slot, s.Name, key, s.RosterEntry[RosterClass], level, s.InGame, s.IsHost, s.LobbyIdentity);
    }

    private void ExecuteScriptKicks()
    {
        if (_inScriptKicks || _scriptKicks.Count == 0) return;
        _inScriptKicks = true;
        try
        {
            for (var i = 0; i < _scriptKicks.Count; i++)
            {
                var (slot, reason) = _scriptKicks[i];
                if (_slots[slot] is not { } victim || victim.State == SessionState.Closed) continue;
                Log.Info($"SCRIPT: kicking #{slot} {victim.Name}{(reason.Length > 0 ? $" ({reason})" : "")}.");
                RemovePlayer(victim, ReasonLobbyKick, notifyVictim: true);
            }
            _scriptKicks.Clear();
        }
        finally { _inScriptKicks = false; }
    }

    // ---- IScriptServer ------------------------------------------------------------------------------------------------------

    long IScriptServer.NowMs => _clock.ElapsedMilliseconds;

    IReadOnlyList<ScriptPlayer> IScriptServer.Players()
    {
        var list = new List<ScriptPlayer>(PlayerCount);
        for (var slot = 1; slot <= MaxSlots; slot++)
            if (_slots[slot] is { } p && p.State >= SessionState.SlotAssigned) list.Add(ScriptPlayerOf(p));
        return list;
    }

    ScriptPlayer? IScriptServer.Player(int slot) => PlayerAt(slot) is { } p ? ScriptPlayerOf(p) : null;

    void IScriptServer.Broadcast(string text) => AdminSay(null, text);

    void IScriptServer.Message(int slot, string text) { if (PlayerAt(slot) is { } p) AdminSay(p, text); }

    void IScriptServer.Kick(int slot, string reason) { if (slot is >= 1 and <= MaxSlots) _scriptKicks.Add((slot, reason)); }
}
