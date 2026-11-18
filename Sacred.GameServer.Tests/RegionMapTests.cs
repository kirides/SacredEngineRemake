using System.Text;
using Sacred.GameServer.Data;

namespace Sacred.GameServer.Tests;

/// <summary>The game's regions and the region of every sector, from bin/Balance.bin and World/sectors.keyx (synthetic files; the real game
/// files too when --game-dir like folders exist on this machine).</summary>
[TestClass]
public class RegionMapTests
{
    private static byte[] Balance(params (byte[] Codes, string Key, uint Kills, uint Boss, uint Id)[] regions)
    {
        var bytes = new List<byte>(new byte[0x30]);                       // some data in front of the table
        foreach (var (codes, key, kills, boss, id) in regions)
        {
            var record = new byte[0x40];
            codes.CopyTo(record, 0);
            Encoding.ASCII.GetBytes(key).CopyTo(record, 0x10);
            BitConverter.GetBytes(kills).CopyTo(record, 0x30);
            BitConverter.GetBytes(boss).CopyTo(record, 0x34);
            BitConverter.GetBytes(id).CopyTo(record, 0x3C);
            bytes.AddRange(record);
        }
        bytes.AddRange(new byte[0x40]);                                    // something after it
        return [.. bytes];
    }

    private static byte[] Keyx(params (uint Id, byte Code)[] sectors)
    {
        var bytes = new byte[0x100 + sectors.Length * 0x300];
        bytes[0] = (byte)'W'; bytes[1] = (byte)'L'; bytes[2] = (byte)'K';
        BitConverter.GetBytes((uint)sectors.Length).CopyTo(bytes, 4);
        for (var i = 0; i < sectors.Length; i++)
        {
            var at = 0x100 + i * 0x300;
            BitConverter.GetBytes(sectors[i].Id).CopyTo(bytes, at + 0x24);
            bytes[at + 0x2C0] = sectors[i].Code;
        }
        return bytes;
    }

    [TestMethod]
    public void RegionsAndSectorsAreReadAndJoinedTheFirstListedRegionWinsAnOverlap()
    {
        var regions = RegionMap.ReadRegions(Balance(
            ([1, 2, 13], "UI_REGION_NORTHKERN", 500, 0, 1),
            ([13, 16], "UI_REGION_SOUTHKERN", 300, 1, 2)),
            key => key == "UI_REGION_NORTHKERN" ? "Northkern Fields" : null);
        Assert.AreEqual(2, regions.Count);
        Assert.AreEqual("Northkern Fields", regions[0].Name, "the game's text");
        Assert.AreEqual("Southkern", regions[1].Name, "a readable fallback without the text");
        Assert.AreEqual(500, regions[0].KillsNeeded);
        Assert.IsTrue(regions[1].Boss);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 13 }, regions[0].Codes);
        var map = RegionMap.Build(regions, RegionMap.ReadSectorCodes(Keyx((10, 1), (11, 2), (12, 13), (13, 16), (14, 77), (15, 0))));
        Assert.AreEqual(1, map.RegionAt(10));
        Assert.AreEqual(1, map.RegionAt(12), "code 13 is listed by both regions: the first wins (as in the game)");
        Assert.AreEqual(2, map.RegionAt(13));
        Assert.AreEqual(0, map.RegionAt(14), "a code that no region lists");
        Assert.AreEqual(0, map.RegionAt(999), "an unknown sector");
        CollectionAssert.AreEqual(new uint[] { 10, 11, 12 }, map.SectorsOf[1]);
        CollectionAssert.AreEqual(new uint[] { 13 }, map.SectorsOf[2]);
        Assert.AreEqual((byte)77, map.CodeOfSector[14]);
    }

    [TestMethod]
    public void BrokenFilesGiveNothingOrAnErrorNeverACrash()
    {
        Assert.AreEqual(0, RegionMap.ReadRegions(new byte[100]).Count, "no region table");
        Assert.AreEqual(0, RegionMap.ReadRegions([]).Count);
        Assert.ThrowsException<InvalidDataException>(() => RegionMap.ReadSectorCodes(new byte[0x200]));
        var truncated = Keyx((1, 1), (2, 2));
        BitConverter.GetBytes(500u).CopyTo(truncated, 4);
        Assert.ThrowsException<InvalidDataException>(() => RegionMap.ReadSectorCodes(truncated));
        Assert.AreEqual("Southkern", RegionMap.Prettify("UI_REGION_SOUTHKERN"));
        Assert.AreEqual("Some key", RegionMap.Prettify("SOME_KEY"));
    }

    [TestMethod]
    public void TheRealGameFilesGiveNineRegionsWithSectors()
    {
        var game = new[] { @"B:\Spiele\GOG Games\sacred gold GOG-wetest", Environment.GetEnvironmentVariable("SACRED_GAME_DIR") }
            .FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "bin", "Balance.bin")) && File.Exists(Path.Combine(d, "World", "sectors.keyx")));
        if (game is null) { Assert.Inconclusive("no game folder on this machine"); return; }
        var map = RegionMap.Load(game);
        Assert.AreEqual(9, map.Regions.Count);
        Assert.IsTrue(map.Regions.All(r => r.Key.StartsWith("UI_REGION_")));
        Assert.IsTrue(map.SectorsOf.Count >= 5, "most regions have sectors");
        Assert.IsTrue(map.RegionOfSector.Count > 100);
    }
}
