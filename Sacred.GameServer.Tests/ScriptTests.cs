using System.Buffers.Binary;
using System.Text;
using Sacred.GameServer.Config;
using Sacred.GameServer.Scripting;
using Sacred.GameServer.Session;

namespace Sacred.GameServer.Tests;

[TestClass]
public class ScriptTests
{
    private sealed class Folder : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sacred-scripts-" + Guid.NewGuid().ToString("N"));
        public Folder() { Directory.CreateDirectory(Path); }
        public string Scripts => System.IO.Path.Combine(Path, "scripts");
        public string Data => System.IO.Path.Combine(Path, "data.json");
        public void Write(string name, string source) { Directory.CreateDirectory(Scripts); File.WriteAllText(System.IO.Path.Combine(Scripts, name), source); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    /// <summary>Script host against a fake server (no sockets).</summary>
    private sealed class FakeServer : IScriptServer
    {
        public long Now;
        public readonly List<string> Said = new();
        public readonly List<(int Slot, string Text)> Told = new();
        public readonly List<int> Kicked = new();
        public readonly List<ScriptPlayer> All = [new ScriptPlayer(1, "Alice", "name:Alice", 2, 10, true, true, 0)];
        public long NowMs => Now;
        public IReadOnlyList<ScriptPlayer> Players() => All;
        public ScriptPlayer? Player(int slot) => All.FirstOrDefault(p => p.Slot == slot);
        public void Broadcast(string text) => Said.Add(text);
        public void Message(int slot, string text) => Told.Add((slot, text));
        public void Kick(int slot, string reason) => Kicked.Add(slot);
        public readonly Dictionary<string, (string Title, IReadOnlyList<(string Name, double Value)> Rows)> Boards = new();
        public void Publish(string name, string title, IReadOnlyList<(string Name, double Value)> rows) => Boards[name] = (title, rows);
        public readonly List<ScriptLootRequest> Drops = new();
        public ScriptDrop? DropItem(int slot, in ScriptLootRequest request, out string? error)
        {
            Drops.Add(request); error = null;
            return new ScriptDrop(100, 200, request.Quality ?? 0, "Test ring", "Ring");
        }
    }

    private static (ScriptHost Host, FakeServer Server, ScriptStore Store) Host(Folder folder, int budget = 1_000_000)
    {
        var options = new ScriptOptions { Directory = folder.Scripts, DataFile = folder.Data, InstructionBudget = budget };
        var server = new FakeServer();
        var store = new ScriptStore(folder.Data);
        var host = new ScriptHost(options, server, store);
        host.Load();
        return (host, server, store);
    }

    private static string Example(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Sacred.GameServer", "scripts-examples", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(name);
    }

    [TestMethod]
    public void SandboxHasNoFilesOrOperatingSystem()
    {
        using var folder = new Folder();
        folder.Write("a.lua", """
            server.set(nil, "io", type(io)); server.set(nil, "os", type(os)); server.set(nil, "dofile", type(dofile))
            server.set(nil, "load", type(load)); server.set(nil, "require", type(require)); server.set(nil, "string", type(string.format))
            """);
        var (host, _, store) = Host(folder);
        foreach (var name in new[] { "io", "os", "dofile", "load", "require" })
        {
            Assert.IsTrue(store.TryGet(ScriptStore.GlobalKey, name, out var value));
            Assert.AreEqual("nil", value, name);
        }
        Assert.IsTrue(store.TryGet(ScriptStore.GlobalKey, "string", out var format));
        Assert.AreEqual("function", format);
        Assert.IsNull(host.Files.Single().Error);
    }

    [TestMethod]
    public void EndlessLoopsAreStoppedAndTheServerGoesOn()
    {
        using var folder = new Folder();
        folder.Write("loop.lua", """
            server.on("join", function(p) while true do end end)
            server.on("join", function(p) server.broadcast("hello " .. p.name) end)
            server.on("leave", function(p) while true do pcall(function() while true do end end) end end)
            """);
        var (host, server, _) = Host(folder, budget: 100_000);
        var player = server.All[0];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        host.FirePlayer("join", player);
        host.FirePlayer("leave", player);
        Assert.IsTrue(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
        CollectionAssert.AreEqual(new[] { "hello Alice" }, server.Said, "the next handler still runs");
        Assert.AreEqual(2, host.Errors);
        // A handler that fails 20 times in a row is switched off.
        for (var i = 0; i < 30; i++) host.FirePlayer("join", player);
        Assert.AreEqual(21, host.Errors);
        Assert.AreEqual(31, server.Said.Count);
    }

    [TestMethod]
    public void LoadErrorsAreReportedPerFile()
    {
        using var folder = new Folder();
        folder.Write("1-good.lua", "server.on('join', function(p) server.broadcast('ok') end)");
        folder.Write("2-bad.lua", "this is not lua");
        var (host, server, _) = Host(folder);
        Assert.IsNull(host.Files[0].Error);
        Assert.IsNotNull(host.Files[1].Error);
        host.FirePlayer("join", server.All[0]);
        CollectionAssert.AreEqual(new[] { "ok" }, server.Said);
    }

    [TestMethod]
    public void TimersStoreAndChatWork()
    {
        using var folder = new Folder();
        folder.Write("a.lua", """
            local ticks = 0
            server.every(1, function() ticks = ticks + 1; server.set(nil, "ticks", ticks) end)
            server.after(2.5, function() server.broadcast("once") end)
            server.on("chat", function(p, text) if text == "!me" then server.message(p, p.name .. " L" .. p.level .. " C" .. p.class); return true end end)
            server.on("kill", function(p, m) server.set(p, "last", m.type); server.add(p, "kills", 2); if m.level > 5 then server.kick(p, "test") end end)
            """);
        var (host, server, store) = Host(folder);
        for (server.Now = 0; server.Now <= 3100; server.Now += 100) host.Pump(server.Now);
        Assert.IsTrue(store.TryGet(ScriptStore.GlobalKey, "ticks", out var ticks));
        Assert.AreEqual(3.0, ticks);
        CollectionAssert.AreEqual(new[] { "once" }, server.Said);
        var alice = server.All[0];
        Assert.IsTrue(host.FireChat(alice, "!me"));
        Assert.IsFalse(host.FireChat(alice, "hello"));
        Assert.AreEqual((1, "Alice L10 C2"), server.Told.Single());
        host.FireKill(alice, new ScriptMonster(4000, 551, 3));
        host.FireKill(alice, new ScriptMonster(4001, 552, 9));
        Assert.IsTrue(store.TryGet("name:Alice", "kills", out var kills));
        Assert.AreEqual(4.0, kills);
        Assert.IsTrue(store.TryGet("name:Alice", "last", out var last));
        Assert.AreEqual(552.0, last);
        CollectionAssert.AreEqual(new[] { 1 }, server.Kicked);
        // Values survive a restart (temporary file + move).
        store.Flush();
        Assert.IsFalse(File.Exists(folder.Data + ".tmp"));
        var reloaded = new ScriptStore(folder.Data);
        reloaded.Load();
        Assert.IsTrue(reloaded.TryGet("name:Alice", "kills", out var again));
        Assert.AreEqual(4.0, again);
    }

    [TestMethod]
    public void LaddersExampleRanksSavedHeroesAndPublishesBoards()
    {
        using var folder = new Folder();
        folder.Write("ladders.lua", Example("ladders.lua"));
        var (host, server, store) = Host(folder);
        // Saved heroes from earlier sessions (names as the server stores them on join).
        foreach (var (key, name, count, level) in new[] { ("id:1:1", "Bob", 30.0, 20.0), ("id:2:1", "Cara", 70.0, 5.0) })
        { store.Set(key, ScriptStore.NameKey, name); store.Set(key, "ladder.kills", count); store.Set(key, "ladder.level", level); }
        store.Set("name:Alice", ScriptStore.NameKey, "Alice");
        var alice = server.All[0];                                        // level 10
        for (uint i = 0; i < 50; i++) host.FireKill(alice, new ScriptMonster(100 + i, 551, 3));
        for (server.Now = 0; server.Now <= 91_000; server.Now += 1000) host.Pump(server.Now);   // online +60 at 60 s, published at 90 s
        var kills = server.Boards["kills"].Rows;
        CollectionAssert.AreEqual(new[] { "Cara", "Alice", "Bob" }, kills.Select(r => r.Name).ToArray());
        Assert.AreEqual(50, kills[1].Value);
        CollectionAssert.AreEqual(new[] { "Bob", "Alice", "Cara" }, server.Boards["level"].Rows.Select(r => r.Name).ToArray());
        Assert.AreEqual(60.0 / 3600, server.Boards["online"].Rows.Single().Value, 1e-9);
        Assert.AreEqual("Highest level", server.Boards["level"].Title);
        Assert.IsTrue(host.FireChat(alice, "!top"));
        Assert.AreEqual("1. Cara: 70 kills", server.Told[0].Text);
    }

    [TestMethod]
    public void ScriptEndpointsAnswerCsvJsonAndErrors()
    {
        using var folder = new Folder();
        folder.Write("ladders.lua", Example("ladders.lua"));
        folder.Write("other.lua", """
            server.endpoint("/Boom/", function() error("broken") end)
            server.endpoint("secret", function(r) return { a = r.query.x, list = { 1, 2 }, empty = {} } end)
            """);
        var (host, server, store) = Host(folder);
        var alice = server.All[0];
        store.Set("id:9:1", ScriptStore.NameKey, "Bob");
        store.Set("name:Alice", ScriptStore.NameKey, "Alice");             // the game server stores this on join
        for (uint i = 0; i < 3; i++) host.FireKill(alice, new ScriptMonster(100 + i, 551, 3));
        host.FireKill(alice, new ScriptMonster(200, 77, 3));
        var csv = host.HandleRequest("ladders", "by-creature", [], publicOnly: true);
        Assert.AreEqual(200, csv.Status);
        StringAssert.StartsWith(csv.ContentType, "text/csv");
        Assert.AreEqual("creature,hunter,kills\r\n77,Alice,1\r\n551,Alice,3\r\n", csv.Body);
        var top = host.HandleRequest("LADDERS", "/top/", [KeyValuePair.Create("board", "kills"), KeyValuePair.Create("n", "1")], publicOnly: true);
        Assert.AreEqual("[{\"rank\":1,\"name\":\"Alice\",\"value\":4}]", top.Body);
        StringAssert.StartsWith(top.ContentType, "application/json");
        Assert.AreEqual(404, host.HandleRequest("ladders", "top", [KeyValuePair.Create("board", "nope")], false).Status);
        Assert.AreEqual(404, host.HandleRequest("ladders", "missing", [], false).Status);
        Assert.AreEqual(500, host.HandleRequest("other", "boom", [], false).Status);
        Assert.AreEqual(404, host.HandleRequest("other", "secret", [], publicOnly: true).Status, "not public");
        var json = host.HandleRequest("other", "secret", [KeyValuePair.Create("x", "1")], false);
        using var doc = System.Text.Json.JsonDocument.Parse(json.Body);
        Assert.AreEqual("1", doc.RootElement.GetProperty("a").GetString());
        Assert.AreEqual(2, doc.RootElement.GetProperty("list").GetArrayLength());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Array, doc.RootElement.GetProperty("empty").ValueKind);
        CollectionAssert.AreEquivalent(new[] { "ladders/top", "ladders/by-creature", "other/boom", "other/secret" },
            host.Endpoints.Select(e => e.Mod + "/" + e.Path).ToArray());
    }

    [TestMethod]
    public void EndpointRequestsRunOnTheGameLoop()
    {
        using var folder = new Folder();
        folder.Write("hello.lua", "server.endpoint('players', function() return #server.players() .. ' online' end, { public = true })");
        using var server = new Server(folder);
        using var a = RawClient.Admit(server.Port, "A", 1);
        var answer = server.Host.ModRequest("hello", "players", [], true, CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreEqual(200, answer.Status);
        Assert.AreEqual("1 online", answer.Body);
        Assert.AreEqual("hello", server.Host.ModEndpoints().Single().Mod);
    }

    [TestMethod]
    public void ChangedScriptsAreReloaded()
    {
        using var folder = new Folder();
        folder.Write("a.lua", "server.on('join', function(p) server.broadcast('v1') end)");
        var (host, server, _) = Host(folder);
        host.FirePlayer("join", server.All[0]);
        folder.Write("a.lua", "server.on('join', function(p) server.broadcast('v2') end)");
        host.RequestReload();
        host.Pump(0); host.Pump(600);
        host.FirePlayer("join", server.All[0]);
        CollectionAssert.AreEqual(new[] { "v1", "v2" }, server.Said);
    }

    [TestMethod]
    public void KillMilestoneExampleAnnouncesAtFifty()
    {
        using var folder = new Folder();
        folder.Write("kill_milestones.lua", Example("kill_milestones.lua"));
        var (host, server, _) = Host(folder);
        var alice = server.All[0];
        for (uint i = 0; i < 49; i++) host.FireKill(alice, new ScriptMonster(5000 + i, 551, 3));
        Assert.AreEqual(0, server.Said.Count);
        host.FireKill(alice, new ScriptMonster(6000, 551, 3));
        CollectionAssert.AreEqual(new[] { "Alice has defeated 50 creatures of type 551!" }, server.Said);
        Assert.IsTrue(host.FireChat(alice, "!kills"));
        Assert.AreEqual("You have defeated 50 creatures.", server.Told.Last().Text);
    }

    private static string? GameDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("SACRED_GAME_DIR") ?? @"B:\Spiele\GOG Games\sacred gold GOG";
        return Directory.Exists(dir) ? dir : null;
    }

    [TestMethod]
    public void LootIsGeneratedForClassLevelAndQuality()
    {
        if (GameDirectory() is not { } dir) { Assert.Inconclusive("no game folder (SACRED_GAME_DIR)"); return; }
        var loot = new LootService(dir);
        var item = loot.Roll(20, 1, 0, 6, Sacred.Core.Pak.Items.SacredItemCategory.Ring, 12345, out var error);
        Assert.IsNotNull(item, error);
        Assert.AreEqual(130, item.Values.Length);
        Assert.AreEqual(Sacred.Core.Pak.Items.SacredItemCategory.Ring, item.Category);
        Assert.AreEqual(item.Type, BinaryPrimitives.ReadUInt32LittleEndian(item.Values), "the object type is the value block's resource");
        Assert.IsTrue(item.Row > 0);
        var same = loot.Roll(20, 1, 0, 6, Sacred.Core.Pak.Items.SacredItemCategory.Ring, 12345, out _);
        CollectionAssert.AreEqual(item.Values, same!.Values, "a seed repeats the roll");
        Assert.IsNull(new LootService(null).Roll(20, 1, 0, null, null, 1, out var missing));
        StringAssert.Contains(missing, "--game-dir");
    }

    [TestMethod]
    public void DropItemOptionsReachTheServer()
    {
        using var folder = new Folder();
        folder.Write("a.lua", """
            server.on("chat", function(p, text)
              local item, err = server.drop_item(p, { quality = 7, category = "ring", level = 33 })
              server.message(p, item.name .. " q" .. item.quality)
              local _, bad = server.drop_item(p, { category = "spoon" })
              server.message(p, bad)
              return true
            end)
            """);
        var (host, server, _) = Host(folder);
        Assert.IsTrue(host.FireChat(server.All[0], "!loot"));
        var request = server.Drops.Single();
        Assert.AreEqual(33, request.Level); Assert.AreEqual(2, request.HeroClass); Assert.AreEqual(7u, request.Quality);
        Assert.AreEqual(Sacred.Core.Pak.Items.SacredItemCategory.Ring, request.Category);
        CollectionAssert.AreEqual(new[] { "Test ring q7", "unknown category spoon" }, server.Told.Select(t => t.Text).ToArray());
    }

    // ---- through the real server -------------------------------------------------------------------------------------

    private sealed class Server : IDisposable
    {
        public readonly GameServerHost Host;
        private readonly Thread _thread;
        public Server(Folder folder, string? gameDirectory = null)
        {
            var config = new GameServerConfig { TcpPort = 0, LanAnnounce = false, TickMilliseconds = 5, GameDirectory = gameDirectory };
            config.Scripts.Directory = folder.Scripts; config.Scripts.DataFile = folder.Data;
            Host = new GameServerHost(config, new AdmissionRules(null), null);
            Host.StartScripts();
            _thread = new Thread(Host.Run) { IsBackground = true };
            _thread.Start();
        }
        public int Port => Host.TcpPort;
        public void Dispose() { Host.Stop(); _thread.Join(); Host.Dispose(); }
    }

    private static byte[] Word(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, value); return b; }
    private static byte[] Chat(string text) { var b = new byte[4 + text.Length * 2 + 2]; Encoding.Unicode.GetBytes(text, b.AsSpan(4)); return b; }
    private static string ChatText(byte[] payload)
    {
        var text = Encoding.Unicode.GetString(payload, 4, payload.Length - 4);
        var end = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    [TestMethod]
    public void KillsChatAndJoinRunThroughTheGameServer()
    {
        using var folder = new Folder();
        folder.Write("kill_milestones.lua", Example("kill_milestones.lua").Replace("[50] = true", "[3] = true"));
        using var server = new Server(folder);
        var host = RawClient.Admit(server.Port, "Host", 1);
        host.Send(0x016, default); host.Send(0x018, Word(0x1964)); host.WaitFor(0x018);
        Assert.AreEqual("Welcome, Host! Type !kills to see your kill count.", ChatText(host.WaitFor(0x003).Payload));
        var joiner = RawClient.Admit(server.Port, "Joiner", 2);
        joiner.Send(0x015, default); joiner.Send(0x017, default); joiner.WaitFor(0x017);
        StringAssert.StartsWith(ChatText(joiner.WaitFor(0x003).Payload), "Welcome, Joiner!");
        using (host) using (joiner)
        {
            // Three creatures die by the joiner's hand (one reported twice).
            foreach (var victim in new uint[] { 9001, 9002, 9002, 9003 })
            {
                var death = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(death, victim);
                BinaryPrimitives.WriteUInt32LittleEndian(death.AsSpan(4), joiner.Slot);
                joiner.Send(0x17D, death);
            }
            var announce = host.WaitFor(0x005);
            Assert.AreEqual((byte)0, announce.Origin);
            Assert.AreEqual("Joiner has defeated 3 creatures of type 0!", ChatText(announce.Payload));
            // "!kills" is answered privately and not relayed.
            joiner.Send(0x005, Chat("!kills"));
            Assert.AreEqual("You have defeated 3 creatures.", ChatText(joiner.WaitFor(0x003).Payload));
            Assert.IsTrue(host.Silent(0x005));
            joiner.Send(0x005, Chat("hi"));
            Assert.AreEqual("hi", ChatText(host.WaitFor(0x005).Payload));
        }
    }

    [TestMethod]
    public void DroppedLootAppearsNextToTheHeroForEverybody()
    {
        if (GameDirectory() is not { } dir) { Assert.Inconclusive("no game folder (SACRED_GAME_DIR)"); return; }
        using var folder = new Folder();
        folder.Write("a.lua", "server.on('chat', function(p, t) local item, err = server.drop_item(p, { quality = 5, category = 'ring', level = 12, class = 1 }); server.message(p, item and item.name or err); return true end)");
        using var server = new Server(folder, dir);
        var host = RawClient.Admit(server.Port, "Host", 1);
        host.Send(0x016, default); host.Send(0x018, Word(0x1964)); host.WaitFor(0x018);
        var joiner = RawClient.Admit(server.Port, "Joiner", 2);
        joiner.Send(0x015, default); joiner.Send(0x017, default); joiner.WaitFor(0x017);
        using (host) using (joiner)
        {
            // The host's hero stands at (5000, 7000) in sector 1234 (a 0x169 state with the 24-byte prefix).
            var hero = new byte[24 + 64];
            BinaryPrimitives.WriteUInt32LittleEndian(hero, host.Slot);
            BinaryPrimitives.WriteInt32LittleEndian(hero.AsSpan(8), 5000);
            BinaryPrimitives.WriteInt32LittleEndian(hero.AsSpan(12), 7000);
            BinaryPrimitives.WriteUInt32LittleEndian(hero.AsSpan(20), 1234);
            host.Send(0x169, hero);
            host.Send(0x005, Chat("!loot"));
            foreach (var client in new[] { joiner, host })
            {
                var state = client.WaitFor(0x16F).Payload;
                var reference = BinaryPrimitives.ReadUInt32LittleEndian(state);
                Assert.IsTrue(reference >= 0x11, "a server reference");
                Assert.AreEqual(5000, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(8)));
                Assert.AreEqual(7000, BinaryPrimitives.ReadInt32LittleEndian(state.AsSpan(12)));
                Assert.AreEqual(1234u, BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(20)));
                var item = Sacred.Network.Gameplay.GoldItemState.FromPayload(state.AsSpan(24));
                Assert.AreEqual(host.Slot, item.PhysicalState.BaseState.Owner, "the receiving player owns the item");
                Assert.AreEqual(BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(4)), item.Instance.ItemType);
                Assert.AreEqual(0, item.Count);
            }
            Assert.IsFalse(string.IsNullOrEmpty(ChatText(host.WaitFor(0x003).Payload)), "the script told the player the item's name");
        }
    }
}
