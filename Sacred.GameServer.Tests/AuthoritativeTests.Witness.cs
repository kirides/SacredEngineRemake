using System.Buffers.Binary;
using Sacred.GameServer.Auth;
using Sacred.GameServer.Session;
using Sacred.GameServer.Simulation;
using Sacred.Network.Gameplay;

namespace Sacred.GameServer.Tests;

/// <summary>The witness on a live authoritative server (Closed Net, fake lobby, strict items on): loot near a kill the server saw, an item
/// the player destroyed coming back, a gold pile explaining a gold increase, and a capture of the session.</summary>
public partial class AuthoritativeTests
{
    private static byte[] Positioned(byte[] state, int x, int y, ushort sector)
    {
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(8), x);
        BinaryPrimitives.WriteInt32LittleEndian(state.AsSpan(12), y);
        BinaryPrimitives.WriteUInt32LittleEndian(state.AsSpan(20), sector);
        return state;
    }

    private static byte[] CreatureState(uint reference, uint type, int x, int y, ushort sector)
    {
        var body = new byte[GoldObjectStateLayout.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GoldObjectStateLayout.Marker);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(GoldObjectStateLayout.TypeOffset), type);
        var payload = new byte[24 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, reference);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), type);
        body.CopyTo(payload.AsSpan(24));
        return Positioned(payload, x, y, sector);
    }

    private static byte[] Kill(uint victim, uint killer) => [.. Word(victim), .. Word(killer)];

    [TestMethod]
    public void WitnessExplainsLootNearAKillAndRefusesItFarAway()
    {
        StrictRun(StrictItemsMode.On, (host, player) =>
        {
            player.Send(0x16F, CreatureState(0x7001, 0x200, 10_000, 10_000, 44));
            player.Send(0x17D, Kill(0x7001, player.Slot));
            player.Send(0x16F, Positioned(ItemState(0x5101, 0x12A, 0x41), 10_300, 10_100, 44));
            Assert.AreEqual("world", ItemWhere(host, player, 0x5101), "loot next to the creature");
            player.Send(0x16F, Positioned(ItemState(0x5102, 0x12A, 0x42), 30_000, 30_000, 45));
            Assert.AreEqual("nil", ItemWhere(host, player, 0x5102, not: "world"), "an item far from any kill");
            Assert.AreEqual(1, host.StrictItemsRefused);
            Assert.AreEqual(1, host.Witness!.Explained[(int)ItemSource.Kill]);
        });
    }

    [TestMethod]
    public void WitnessLetsADestroyedItemComeBackOnce()
    {
        StrictRun(StrictItemsMode.On, (host, player) =>
        {
            player.Send(0x16F, CreatureState(0x7002, 0x200, 10_000, 10_000, 44));
            player.Send(0x17D, Kill(0x7002, player.Slot));
            player.Send(0x16F, Positioned(ItemState(0x5201, 0x12A, 0x51), 10_100, 10_000, 44));
            Assert.AreEqual("world", ItemWhere(host, player, 0x5201));
            player.Send(0x167, Word(0x5201));                                 // destroyed (picked up into another form, socketed, ...)
            Assert.AreEqual("gone", ItemWhere(host, player, 0x5201, not: "world"));
            player.Send(0x16F, ItemState(0x5202, 0x12A, 0x51));              // the same content under a new reference, no position
            Assert.AreEqual("world", ItemWhere(host, player, 0x5202, not: "gone"));
            player.Send(0x16F, ItemState(0x5203, 0x12A, 0x51));              // and again: nothing left to rebuild it from
            Assert.AreEqual("nil", ItemWhere(host, player, 0x5203, not: "world"));
            Assert.AreEqual(1, host.Witness!.Explained[(int)ItemSource.Rebuild]);
            Assert.AreEqual(1, host.StrictItemsRefused);
        });
    }

    [TestMethod]
    public void WitnessSourcesAreConfigurable()
    {
        StrictRun(StrictItemsMode.On, (host, player) =>
        {
            player.Send(0x16F, CreatureState(0x7003, 0x200, 10_000, 10_000, 44));
            player.Send(0x17D, Kill(0x7003, player.Slot));
            player.Send(0x16F, Positioned(ItemState(0x5301, 0x12A, 0x61), 10_100, 10_000, 44));
            Assert.AreEqual("nil", ItemWhere(host, player, 0x5301), "kills explain nothing with AUTH_ITEM_SOURCES=token,baseline");
        }, configure: c => c.ApplyLine("AUTH_ITEM_SOURCES=token,baseline"));
    }

    [TestMethod]
    public void RefusedItemsCountAsViolationsWhenConfigured()
    {
        StrictRun(StrictItemsMode.On, (host, player) =>
        {
            player.Send(0x16F, Positioned(ItemState(0x5401, 0x12A, 0x71), 30_000, 30_000, 45));
            Assert.AreEqual("nil", ItemWhere(host, player, 0x5401));
            player.Send(0x16F, Positioned(ItemState(0x5402, 0x12A, 0x72), 30_000, 30_000, 45));
            Assert.IsTrue(player.IsClosed(), "two refused items with AUTH_MAX_VIOLATIONS=2 remove the player");
        }, configure: c => { c.ApplyLine("AUTH_STRICT_VIOLATIONS=1"); c.ApplyLine("AUTH_MAX_VIOLATIONS=2"); });
    }

    private const string GoldScript = """
        server.on("chat", function(p, text)
            if text == "!gold" then local h = server.hero(p) server.save("gold", tostring(h and h.gold or "nil")) return true end
        end)
        """;

    private static byte[] HeroWithGold(byte slot, uint gold)
    {
        var values = GoldCreatureStateLayout.PrefixSize;
        var body = new byte[values + GoldCreatureValuesLayout.CompactMinimumSize + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GoldObjectStateLayout.Marker);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(GoldCreatureStateLayout.MarkerOffset), GoldCreatureStateLayout.Marker);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(values), GoldCreatureValuesLayout.Marker);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(values + GoldCreatureValuesLayout.CompactWord56Offset), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(values + GoldCreatureValuesLayout.CompactWord46Offset), gold);
        var payload = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, slot);
        body.CopyTo(payload.AsSpan(8));
        return payload;
    }

    private static string? GoldOf(GameServerHost host, RawClient player, string? not)
    {
        player.Send(0x005, ChatLine("!gold"));
        return StoredOf(host, "gold", "gold", not);
    }

    [TestMethod]
    public void GoldLedgerSavesOnlyExplainedGold()
    {
        StrictRun(StrictItemsMode.Off, (host, player) =>
        {
            player.Send(0x16E, HeroWithGold(player.Slot, 1000));
            Assert.AreEqual("1000", GoldOf(host, player, null));
            player.Send(0x16E, HeroWithGold(player.Slot, 5000));             // +4000 from nowhere
            Assert.AreEqual("1000", GoldOf(host, player, "nil"), "not taken over into the server's hero");
            var pile = ItemState(0x5501, 0x140C, 0x00);
            BinaryPrimitives.WriteUInt32LittleEndian(pile.AsSpan(24 + GoldItemStateLayout.RecordsOffset + 8), 250);
            player.Send(0x16F, Positioned(pile, 10_000, 10_000, 44));
            player.Send(0x141, [.. Word(0x5501), .. Word(0)]);
            player.Send(0x16E, HeroWithGold(player.Slot, 5250));             // +250 from the pile
            Assert.AreEqual("1250", GoldOf(host, player, "1000"));
            Assert.AreEqual(1, host.Gold!.Unexplained);
            Assert.AreEqual(1, host.GoldClamped);
        }, scripts: new() { ["gold.lua"] = GoldScript }, configure: c => c.ApplyLine("AUTH_STRICT_GOLD=on"));
    }

    [TestMethod]
    public void CaptureRecordsTheSession()
    {
        var folder = Directory.CreateTempSubdirectory("cap-").FullName;
        try
        {
            StrictRun(StrictItemsMode.Off, (host, player) =>
            {
                player.Send(0x17D, Kill(0x7777, player.Slot));
                player.Send(0x005, ChatLine("!item 1"));
                StoredOf(host, "strict", "item");
            }, configure: c => c.CaptureDirectory = folder);
            var file = Directory.GetFiles(folder, "*.sgcap").Single();
            var reader = CaptureReader.Open(file);
            var opcodes = new List<ushort>();
            while (reader.TryRead(out var record)) opcodes.Add(record.Opcode);
            CollectionAssert.IsSubsetOf(new ushort[] { 0x018, 0x17D, 0x005 }, opcodes);
        }
        finally { Directory.Delete(folder, true); }
    }
}
