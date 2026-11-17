using System.Buffers.Binary;
using Sacred.GameServer.Auth;
using Sacred.GameServer.Auth.Hero;
using Sacred.GameServer.Simulation;
using Sacred.GameServer.Wire;
using Sacred.Network.Gameplay;

namespace Sacred.GameServer.Session;

/// <summary>
/// Host side of the witness (docs/authoritative-world-plan.md, M0..M2): feeds <see cref="ItemWitness"/>, <see cref="GoldLedger"/>,
/// <see cref="OpcodeTrail"/> and the capture with what players send. Hooks in existing code, each one call: <c>Route</c> (capture),
/// <c>AuthoritativeFilter</c> (events), <c>RememberRemovedItem</c> (destroyed items, gold piles), <c>CompleteTrade</c>, the two 0x020
/// grants of the sector code, <c>CaptureHeroState</c> (gold) and <c>AuthoritativeLeave</c>; the strict item gate
/// (GameServerHost.ItemTokens.cs) asks <see cref="ExplainCreation"/>.
/// </summary>
public sealed partial class GameServerHost
{
    private ItemWitness? _witness;
    private GoldLedger? _goldLedger;
    private readonly OpcodeTrail _trail = new();
    private CaptureWriter? _capture;
    private bool _captureTried;
    private readonly uint[] _creditedPiles = new uint[256];
    private int _creditedPileNext;

    private WitnessOptions WitnessConfig => _config.Auth.Witness;
    /// <summary>The item witness (diagnostics and tests); null outside authoritative mode.</summary>
    public ItemWitness? Witness => _config.Auth.Enabled ? _witness ??= new ItemWitness(WitnessConfig) : null;
    /// <summary>The gold ledger (diagnostics and tests); null outside authoritative mode.</summary>
    public GoldLedger? Gold => _config.Auth.Enabled ? _goldLedger ??= new GoldLedger(WitnessConfig) : null;
    /// <summary>The capture writer while <c>--capture</c> is on.</summary>
    public CaptureWriter? Capture => _capture;
    /// <summary>Gold increases that were not saved (AUTH_STRICT_GOLD=on).</summary>
    public int GoldClamped { get; private set; }

    /// <summary>Route: every message of an admitted player goes into the capture.</summary>
    private void CaptureMessage(PlayerSession s, in GoldMessageView m)
    {
        if (_config.CaptureDirectory is null) return;
        if (_capture is null)
        {
            if (_captureTried) return;
            _captureTried = true;
            _capture = CaptureWriter.Open(_config.CaptureDirectory, _clock.ElapsedMilliseconds);
            if (_capture is null) return;
        }
        _capture.Append(_clock.ElapsedMilliseconds, s.Slot, m.Opcode, m.Payload);
    }

    private void PumpCapture(long now) => _capture?.Pump(now);

    private void CloseCapture() { _capture?.Dispose(); }

    /// <summary>AuthoritativeFilter: the events the witness explains creations with.</summary>
    private void WitnessObserve(PlayerSession s, in GoldMessageView m)
    {
        var now = _clock.ElapsedMilliseconds;
        _trail.Add(s.Slot, m.Opcode, now);
        var p = m.Payload;
        switch (m.Opcode)
        {
            case 0x17D when p.Length >= 8:
            {
                var victim = BinaryPrimitives.ReadUInt32LittleEndian(p) & 0x7FFFFFFF;
                if (victim is >= 1 and <= MaxSlots || !_world.TryGet(victim, out var entry)) return;
                var (sector, x, y) = Position(entry);
                Witness!.OnKill(victim, sector, x, y, CompactLevel(entry.Payload[(entry.LongPrefix ? 24 : 8)..]), now);
                return;
            }
            case GoldContainerActivationCodec.Opcode when p.Length >= 4:
            {
                var container = BinaryPrimitives.ReadUInt32LittleEndian(p) & 0x7FFFFFFF;
                if (!_world.TryGet(container, out var entry)) return;
                var (sector, x, y) = Position(entry);
                Witness!.OnContainer(container, s.Slot, sector, x, y, now);
                return;
            }
            case GoldItemPickupCodec.RequestOpcode or GoldItemPickupCodec.TakenOpcode when p.Length >= 4:
                CreditGoldPile(s, BinaryPrimitives.ReadUInt32LittleEndian(p) & 0x7FFFFFFF, now);
                return;
        }
    }

    /// <summary>A gold pile (resources 0x140C..0x140F, amount = price) the player picks up, credited once per pile.</summary>
    private void CreditGoldPile(PlayerSession s, uint reference, long now)
    {
        if (!_world.TryGet(reference, out var entry) || entry.Payload.Length < 8) return;
        var type = BinaryPrimitives.ReadUInt32LittleEndian(entry.Payload[4..]);
        if (type is < 0x140C or > 0x140F || Item(reference) is not { } pile) return;
        foreach (var credited in _creditedPiles) if (credited == reference) return;
        _creditedPiles[_creditedPileNext] = reference;
        _creditedPileNext = (_creditedPileNext + 1) % _creditedPiles.Length;
        Gold!.Credit(s.Slot, pile.Price, now);
    }

    /// <summary>RememberRemovedItem: the owner destroyed one of its items (its records may come back); a gold pile counts as picked up.</summary>
    private void WitnessDestroyed(PlayerSession s, uint reference, uint type, ReadOnlySpan<byte> content)
    {
        var now = _clock.ElapsedMilliseconds;
        if (type is >= 0x140C and <= 0x140F) { CreditGoldPile(s, reference, now); return; }
        Witness!.OnDestroyed(s.Slot, type, content, now);
    }

    /// <summary>CompleteTrade: each side may now create the items the other offered, and has the other's gold coming.</summary>
    private void WitnessTrade(PlayerSession a, PlayerSession b)
    {
        if (!_config.Auth.Enabled) return;
        var now = _clock.ElapsedMilliseconds;
        void Give(PlayerSession to, TradeState from)
        {
            foreach (var reference in from.Offer)
            {
                if (reference == 0 || !_world.TryGet(reference & 0x7FFFFFFF, out var entry) || !ItemBody(entry, out var body)) continue;
                if (!SavedObjects.ReadLiveItem(body, out var records, out _)) continue;
                Witness!.OnTradeReceive(to.Slot, ItemWitness.Fnv(SavedObjects.LiveContent(body, records)), now);
            }
            Gold!.Credit(to.Slot, from.Gold, now);
        }
        Give(a, b.Trade);
        Give(b, a.Trade);
    }

    /// <summary>A 0x020 grant: the player streams the sector's ground items now.</summary>
    private void WitnessSectorOwned(PlayerSession s, ushort sector)
    {
        if (_config.Auth.Enabled) Witness!.OnSectorOwned(s.Slot, sector, _clock.ElapsedMilliseconds);
    }

    private void WitnessLeave(PlayerSession s)
    {
        _witness?.Forget(s.Slot);
        _goldLedger?.Forget(s.Slot);
        _trail.Forget(s.Slot);
    }

    /// <summary>The strict item gate asks: where does this client-created item come from? Logged when nothing explains it.</summary>
    private ItemSource ExplainCreation(PlayerSession s, uint reference, uint type, ReadOnlySpan<byte> payload, int prefix, ReadOnlySpan<byte> body, int records)
    {
        var content = SavedObjects.LiveContent(body, records);
        var data = content[(records * ItemWitness.RecordBytes)..];
        ushort sector; int x, y;
        if (prefix == 24 && payload.Length >= 24)
            (sector, x, y) = ((ushort)BinaryPrimitives.ReadUInt32LittleEndian(payload[20..]), BinaryPrimitives.ReadInt32LittleEndian(payload[8..]), BinaryPrimitives.ReadInt32LittleEndian(payload[12..]));
        else (sector, x, y) = (BinaryPrimitives.ReadUInt16LittleEndian(body[GoldObjectStateLayout.SectorOffset..]), BinaryPrimitives.ReadInt32LittleEndian(body[GoldObjectStateLayout.XOffset..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[GoldObjectStateLayout.YOffset..]));
        var facts = new ItemFacts(reference, type, data[25], data[6] & 15, sector, x, y, ItemWitness.Fnv(content));
        return Witness!.Classify(s.Slot, facts, content, _clock.ElapsedMilliseconds);
    }

    private string Trail(PlayerSession s) => _trail.Describe(s.Slot, _clock.ElapsedMilliseconds);

    /// <summary>CaptureHeroState: a new hero state of the player; its gold goes through the ledger. With AUTH_STRICT_GOLD=on an unexplained
    /// increase is written back to the explained figure in the server's copy of the state, so the hero is saved without it.</summary>
    private void WitnessHeroGold(PlayerSession s, LiveHero live, bool full)
    {
        var mode = WitnessConfig.StrictGold;
        if (mode == StrictItemsMode.Off || !HeroSavedByServer(s.Slot)) return;
        var body = full ? live.FullBody.AsSpan(0, live.FullBodyLength) : live.Body.AsSpan(0, live.BodyLength);
        var at = GoldOffset(body, full);
        if (at < 0) return;
        var reported = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        var ledger = Gold!;
        var unexplained = ledger.Report(s.Slot, reported, _clock.ElapsedMilliseconds);
        if (unexplained > 0)
        {
            Log.Info($"AUTH: gold: {s.Describe()}: +{unexplained} gold nothing explains (reported {reported}, explained {ledger.Allowed(s.Slot)}; last: {Trail(s)})"
                + (mode == StrictItemsMode.On ? ", not saved." : " (log mode, accepted)."));
            if (mode == StrictItemsMode.On && WitnessConfig.CountViolations) Violation(s, $"gold +{unexplained} without a source");
        }
        var allowed = ledger.Allowed(s.Slot);
        if (mode == StrictItemsMode.On && allowed < reported)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body[at..], (uint)allowed);
            if (unexplained > 0) GoldClamped++;
        }
    }

    /// <summary>Offset of the hero's gold in a stored hero body (compact word +0x46 or the full block's +62), or -1.</summary>
    private static int GoldOffset(ReadOnlySpan<byte> body, bool full)
    {
        var values = GoldCreatureStateLayout.PrefixSize;
        if (body.Length <= values + GoldCreatureValuesLayout.CompactMinimumSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[GoldCreatureStateLayout.MarkerOffset..]) != GoldCreatureStateLayout.Marker ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[values..]) != GoldCreatureValuesLayout.Marker) return -1;
        if (full) return body.Length >= values + GoldCreatureValuesLayout.FullMinimumSize ? values + GoldCreatureValuesLayout.FullBlock08Offset + 62 : -1;
        var at = values + GoldCreatureValuesLayout.CompactWord46Offset;
        return at + 4 <= body.Length ? at : -1;
    }
}
