using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Sacred.GameServer.Config;
using Sacred.GameServer.Scripting;
using Sacred.GameServer.Session;

namespace Sacred.GameServer.Tests;

/// <summary>scripts-examples/world_events.lua against a fake server: progress, milestones, expiry (also while the server was down), the
/// reward split, rewards for heroes who are offline, the sector filter, the pushed state and the public endpoint.</summary>
[TestClass]
public class WorldEventsScriptTests
{
    private sealed class Folder : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sacred-we-" + Guid.NewGuid().ToString("N"));
        public string Scripts => System.IO.Path.Combine(Path, "scripts");
        public string Data => System.IO.Path.Combine(Path, "data");
        public Folder() { Directory.CreateDirectory(Scripts); }
        public void Write(string name, string source) => File.WriteAllText(System.IO.Path.Combine(Scripts, name), source);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private sealed class Fake : IScriptServer
    {
        public long Now;
        public readonly List<string> Said = new();
        public readonly List<(int Slot, string Text)> Told = new();
        public readonly List<(int Slot, uint Amount)> Gold = new();
        public readonly List<(int Slot, uint? Quality)> Items = new();
        public readonly List<(int Slot, JsonElement Payload)> Pushed = new();
        public readonly HashSet<int> NetPlayers = [];
        public readonly List<ScriptPlayer> All = [new ScriptPlayer(1, "Alice", "name:Alice", 2, 10, true, true, 0), new ScriptPlayer(2, "Bob", "name:Bob", 1, 12, true, false, 0)];
        public long NowMs => Now;
        public IReadOnlyList<ScriptPlayer> Players() => All;
        public ScriptPlayer? Player(int slot) => All.FirstOrDefault(p => p.Slot == slot);
        public void Broadcast(string text) => Said.Add(text);
        public void Message(int slot, string text) => Told.Add((slot, text));
        public void Kick(int slot, string reason) { }
        public ScriptDrop? DropItem(int slot, in ScriptLootRequest request, out string? error)
        {
            Items.Add((slot, request.Quality)); error = null;
            return new ScriptDrop(100, 200, request.Quality ?? 0, "Test ring", "Ring");
        }
        public ScriptDrop? DropGold(int slot, uint amount, out string? error)
        {
            Gold.Add((slot, amount)); error = null;
            return new ScriptDrop(101, GoldPile.ResourceFor(amount), 0, "Gold", "Gold");
        }
        public uint NetRegister(string name, bool serverOnly) => 0x1234;
        public bool NetSupports(int slot) => NetPlayers.Contains(slot);
        public int NetSend(int slot, uint channel, bool json, byte[] data, out string? error)
        {
            error = null;
            Assert.IsTrue(json);
            Pushed.Add((slot, JsonDocument.Parse(data).RootElement.Clone()));
            return 1;
        }
        public ScriptPlayer Alice => All[0];
        public ScriptPlayer Bob => All[1];
        public void Tick(ScriptHost host) { Now += 2000; host.Pump(Now); }
    }

    private static string Source(Func<string, string>? edit = null)
    {
        var text = ReadExample();
        text = text.Replace("first_delay = 60,", "first_delay = 0,").Replace("cooldown = 900,", "cooldown = 5,")
            .Replace("target = 1000, duration = 2 * 3600", "target = 4, duration = 3600")
            .Replace("min_contribution = 10,", "min_contribution = 2,");
        return edit is null ? text : edit(text);
    }

    private static string ReadExample()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Sacred.GameServer", "scripts-examples", "world_events.lua");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("world_events.lua");
    }

    private static (ScriptHost Host, Fake Server) Start(Folder folder, Fake? server = null)
    {
        server ??= new Fake();
        var host = new ScriptHost(new ScriptOptions { Directory = folder.Scripts, DataDirectory = folder.Data }, server);
        host.Load();
        Assert.IsNull(host.Files.Single().Error);
        return (host, server);
    }

    private static ScriptMonster Orc(uint reference) => new(reference, 38, 5, "Orc Warrior");
    private static ScriptMonster Wolf(uint reference) => new(reference, 77, 5, "Wolf");

    private static JsonElement State(ScriptHost host, bool publicOnly = true)
    {
        var answer = host.HandleRequest("world_events", "state", [], publicOnly);
        Assert.AreEqual(200, answer.Status, answer.Body);
        return JsonDocument.Parse(answer.Body).RootElement;
    }

    [TestMethod]
    public void AnEventRunsToItsTargetAndTheRewardIsSharedByContribution()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source());
        var (host, server) = Start(folder);
        Assert.AreEqual(0, server.Said.Count, "the schedule waits for the first tick");
        server.Tick(host);
        StringAssert.Contains(server.Said.Single(), "Orc hunt begins: Slay 1000 orcs");
        Assert.AreEqual("Orc hunt", State(host).GetProperty("active").GetProperty("title").GetString());

        host.FireKill(server.Alice, Wolf(1));                 // not an orc
        host.FireKill(server.Alice, Orc(2));                  // 1/4 = 25 %
        host.FireKill(server.Bob, Orc(3));                    // 2/4 = 50 %
        host.FireKill(server.Alice, Orc(4));                  // 3/4 = 75 %
        Assert.IsTrue(server.Said.Any(s => s.Contains("25% (1/4)")));
        Assert.IsTrue(server.Said.Any(s => s.Contains("50% (2/4)")));
        Assert.IsTrue(server.Said.Any(s => s.Contains("75% (3/4)")));
        Assert.AreEqual(0, server.Gold.Count);
        host.FireKill(server.Alice, Orc(5));                  // 4/4: done (Alice 3, Bob 1)
        var done = server.Said.Last();
        StringAssert.Contains(done, "Orc hunt is complete! 4/4");
        StringAssert.Contains(done, "Alice (3)");
        StringAssert.Contains(done, "1 heroes share the reward", "Bob has 1 kill, 2 are needed");

        // Alice alone qualifies: the whole pool plus gold_each; a common item for everybody and the top prize for the best.
        CollectionAssert.AreEqual(new[] { (1, 50500u) }, server.Gold);
        CollectionAssert.AreEqual(new (int, uint?)[] { (1, 3), (1, 9) }, server.Items);
        Assert.IsTrue(server.Told.Any(t => t.Slot == 1 && t.Text.Contains("50500 gold")));

        // The finished event is the last result; the cooldown applies; the next one is another one in the rotation.
        var state = State(host);
        Assert.IsFalse(state.TryGetProperty("active", out _));
        Assert.IsTrue(state.GetProperty("last").GetProperty("success").GetBoolean());
        server.Said.Clear();
        server.Tick(host);
        Assert.AreEqual(0, server.Said.Count, "cooldown of 5 s");
        server.Tick(host); server.Tick(host);
        Thread.Sleep(5200);
        server.Tick(host);
        StringAssert.Contains(server.Said.Single(), "The great cull begins");
    }

    [TestMethod]
    public void TwoQualifyingHeroesShareThePoolByTheirKills()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source(t => t.Replace("min_contribution = 2,", "min_contribution = 1,")));
        var (host, server) = Start(folder);
        server.Tick(host);
        host.FireKill(server.Alice, Orc(1)); host.FireKill(server.Bob, Orc(2)); host.FireKill(server.Alice, Orc(3)); host.FireKill(server.Alice, Orc(4));
        // pool 50000: Alice 3/4, Bob 1/4; each gets 500 on top
        CollectionAssert.AreEquivalent(new[] { (1, 38000u), (2, 13000u) }, server.Gold);
        Assert.AreEqual(3, server.Items.Count(i => i.Quality == 3) + server.Items.Count(i => i.Quality == 9), "two plain items and one prize");
        Assert.AreEqual(1, server.Items.Count(i => i.Slot == 1 && i.Quality == 9));
        Assert.AreEqual(0, server.Items.Count(i => i.Slot == 2 && i.Quality == 9));
    }

    [TestMethod]
    public void AnEventThatRunsOutFailsWithoutReward()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source(t => t.Replace("target = 4, duration = 3600", "target = 4, duration = 1")));
        var (host, server) = Start(folder);
        server.Tick(host);
        host.FireKill(server.Alice, Orc(1));
        Thread.Sleep(1200);
        server.Tick(host);
        StringAssert.Contains(server.Said.Last(), "Orc hunt has failed: 1/4");
        Assert.AreEqual(0, server.Gold.Count + server.Items.Count);
        Assert.IsFalse(State(host).GetProperty("last").GetProperty("success").GetBoolean());
        host.FireKill(server.Alice, Orc(2));                  // no event: nothing happens, no error
        Assert.AreEqual(0, host.Errors);
    }

    [TestMethod]
    public void AnEventThatRanOutWhileTheServerWasDownIsClosedOnStart()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source(t => t.Replace("target = 4, duration = 3600", "target = 4, duration = 1")));
        var (host, server) = Start(folder);
        server.Tick(host);
        host.FireKill(server.Alice, Orc(1));
        server.Tick(host);                                    // saves the progress
        host.FlushStores(wait: true);
        host.Dispose();
        Thread.Sleep(1200);

        var (_, again) = Start(folder);                       // "restart"
        StringAssert.Contains(again.Said.Single(), "Orc hunt has failed: 1/4");
    }

    [TestMethod]
    public void ARunningEventAndItsContributionsSurviveARestart()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source());
        var (host, server) = Start(folder);
        server.Tick(host);
        host.FireKill(server.Alice, Orc(1)); host.FireKill(server.Alice, Orc(2));
        server.Tick(host);
        host.FlushStores(wait: true);
        host.Dispose();

        var (host2, server2) = Start(folder);
        Assert.AreEqual(0, server2.Said.Count);
        var active = State(host2).GetProperty("active");
        Assert.AreEqual(2, active.GetProperty("count").GetInt32());
        Assert.AreEqual("Alice", active.GetProperty("top")[0].GetProperty("name").GetString());
        host2.FireKill(server2.Alice, Orc(3)); host2.FireKill(server2.Alice, Orc(4));
        StringAssert.Contains(server2.Said.Last(), "Orc hunt is complete! 4/4");
        Assert.AreEqual(50500u, server2.Gold.Single().Amount);
    }

    [TestMethod]
    public void HeroesWhoAreOfflineGetTheirRewardWhenTheyEnter()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source(t => t.Replace("min_contribution = 2,", "min_contribution = 1,")));
        var (host, server) = Start(folder);
        server.Tick(host);
        var offlineBob = server.Bob with { InGame = false };
        server.All[1] = offlineBob;
        host.FireKill(server.Alice, Orc(1)); host.FireKill(offlineBob, Orc(2)); host.FireKill(server.Alice, Orc(3)); host.FireKill(server.Alice, Orc(4));
        CollectionAssert.AreEqual(new[] { (1, 38000u) }, server.Gold, "Bob is not in the world: nothing dropped yet");
        host.FlushStores(wait: true);

        // A restart in between loses nothing: the waiting reward is in the mod's storage.
        host.Dispose();
        var (host2, server2) = Start(folder);
        server2.All[1] = server2.Bob with { InGame = false };
        server2.Tick(host2);
        Assert.AreEqual(0, server2.Gold.Count);
        server2.All[1] = server2.Bob with { InGame = true };
        host2.FirePlayer("enter", server2.Bob);
        CollectionAssert.AreEqual(new[] { (2, 13000u) }, server2.Gold);
        Assert.AreEqual(1, server2.Items.Count(i => i.Slot == 2), "the plain item");
        server2.Tick(host2);
        Assert.AreEqual(1, server2.Gold.Count, "delivered once");
    }

    [TestMethod]
    public void AnEventWithSectorsCountsOnlyKillsInThem()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source(t => t.Replace("names = { \"orc\" },", "sectors = { 2909, 2910 },")));
        var (host, server) = Start(folder);
        server.Tick(host);
        var inside = server.Alice with { Sector = 2910 };
        var outside = server.Bob with { Sector = 100 };
        host.FireKill(outside, Orc(1)); host.FireKill(server.Alice, Orc(2));      // sector 0: unknown
        host.FireKill(inside, Orc(3)); host.FireKill(inside, Wolf(4));            // any creature there
        var active = State(host).GetProperty("active");
        Assert.AreEqual(2, active.GetProperty("count").GetInt32());
        Assert.AreEqual(2, active.GetProperty("sectors").GetArrayLength());

        // !sector tells the number
        Assert.IsTrue(host.FireChat(inside, "!sector"));
        Assert.AreEqual("You are in sector 2910.", server.Told.Last().Text);
    }

    [TestMethod]
    public void TheStateIsPushedToClientsWithTheirOwnSharePerSector()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source(t => t.Replace("names = { \"orc\" },", "sectors = { 2909 },")));
        var server = new Fake();
        server.NetPlayers.Add(1); server.NetPlayers.Add(2);
        var (host, _) = Start(folder, server);
        server.Tick(host);                                    // starts and pushes to both
        Assert.AreEqual(2, server.Pushed.Count);
        host.FireKill(server.Alice with { Sector = 2909 }, Orc(1));
        server.Pushed.Clear();
        server.Tick(host);                                    // progress changed: pushed
        var alice = server.Pushed.Single(p => p.Slot == 1).Payload;
        var bob = server.Pushed.Single(p => p.Slot == 2).Payload;
        Assert.AreEqual("active", alice.GetProperty("st").GetString());
        Assert.AreEqual(1, alice.GetProperty("count").GetInt32());
        Assert.AreEqual(4, alice.GetProperty("target").GetInt32());
        Assert.AreEqual(1, alice.GetProperty("mine").GetInt32());
        Assert.IsFalse(alice.GetProperty("here").GetBoolean(), "sector 0: not seen in the sector yet");
        Assert.AreEqual(0, bob.GetProperty("mine").GetInt32());
        Assert.IsTrue(alice.GetProperty("left").GetDouble() > 3000);

        // moving into the sector pushes at once, with here = true
        server.Pushed.Clear();
        server.All[0] = server.Alice with { Sector = 2909 };
        host.FireSector(server.Alice);
        Assert.IsTrue(server.Pushed.Single().Payload.GetProperty("here").GetBoolean());

        // a client's request is answered, at most once a second; players without the messages get nothing
        server.Pushed.Clear();
        server.NetPlayers.Remove(2);
        Assert.IsTrue(host.FireNet(0x1234, server.Bob, true, "{}"u8.ToArray()));
        Assert.AreEqual(0, server.Pushed.Count);
        Assert.IsTrue(host.FireNet(0x1234, server.Alice, true, "{}"u8.ToArray()));
        Assert.IsTrue(host.FireNet(0x1234, server.Alice, true, "{}"u8.ToArray()));
        Assert.AreEqual(1, server.Pushed.Count);
        Assert.AreEqual(0, host.Errors);
    }

    [TestMethod]
    public void ChatCommandAnswersPrivatelyAndPublicOutputHasNoOwnerKeys()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source());
        var (host, server) = Start(folder);
        Assert.IsTrue(host.FireChat(server.Alice, "!event"));
        StringAssert.Contains(server.Told.Last().Text, "No world event is running");
        server.Tick(host);
        host.FireKill(server.Alice, Orc(1));
        server.Told.Clear();
        Assert.IsTrue(host.FireChat(server.Alice, "!event"));
        Assert.IsTrue(server.Told.Any(t => t.Text.Contains("1/4 (25%)")));
        Assert.IsTrue(server.Told.Any(t => t.Text.Contains("Your share: 1 (2 needed")));
        Assert.IsFalse(host.FireChat(server.Alice, "hello"));

        var json = host.HandleRequest("world_events", "state", [], publicOnly: true).Body;
        Assert.IsFalse(json.Contains("name:Alice") || json.Contains("uid"), json);
        StringAssert.Contains(json, "\"Alice\"");
        var page = host.HandleRequest("world_events", "", [], publicOnly: true);
        Assert.AreEqual(200, page.Status);
        Assert.IsTrue(page.Page);
        StringAssert.Contains(page.Body, "Orc hunt");
        StringAssert.Contains(page.Body, "<b>Alice</b>");
        Assert.AreEqual(0, host.Errors);
    }

    private sealed class LiveServer : IDisposable
    {
        public readonly GameServerHost Host;
        private readonly Thread _thread;
        public LiveServer(Folder folder)
        {
            var config = new GameServerConfig { TcpPort = 0, LanAnnounce = false, TickMilliseconds = 5 };
            config.Scripts.Directory = folder.Scripts; config.Scripts.DataDirectory = folder.Data;
            Host = new GameServerHost(config, new AdmissionRules(null), null);
            Host.StartScripts();
            _thread = new Thread(Host.Run) { IsBackground = true };
            _thread.Start();
        }
        public int Port => Host.TcpPort;
        public void Dispose() { Host.Stop(); _thread.Join(); Host.Dispose(); }
    }

    private static string ChatText(byte[] payload)
    {
        var text = Encoding.Unicode.GetString(payload, 4, payload.Length - 4);
        var end = text.IndexOf(' ');
        return end < 0 ? text : text[..end];
    }

    [TestMethod]
    public void KillsInASectorCountThroughTheRealGameServer()
    {
        using var folder = new Folder();
        folder.Write("world_events.lua", Source(t => t.Replace("names = { \"orc\" },", "sectors = { 2909 },")));
        using var server = new LiveServer(folder);
        using var host = RawClient.Admit(server.Port, "Host", 1);
        host.Send(0x016, default);
        host.Send(0x018, BitConverter.GetBytes(0x1964u)); host.WaitFor(0x018);
        // the scheduler starts the event on its first tick (every 2 s) and tells everybody
        var begins = host.WaitFor(0x005, 6000);
        StringAssert.Contains(ChatText(begins.Payload), "Orc hunt begins");

        void Kill(uint victim)
        {
            var death = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(death, victim);
            BinaryPrimitives.WriteUInt32LittleEndian(death.AsSpan(4), (uint)host.Slot);
            host.Send(0x17D, death);
        }
        var chat = new byte[4 + "!event".Length * 2 + 2];
        Encoding.Unicode.GetBytes("!event", chat.AsSpan(4));
        Kill(9001);                                           // the hero has not entered a sector yet: sector 0, not counted
        host.Send(0x005, chat);
        Assert.IsTrue(ChatText(host.WaitFor(0x003).Payload).Contains("0/4 (0%)"), "outside the sector");

        var entry = Convert.FromHexString("5d0b00001f0b200b210b5e0b9d0b9c0b9b0b5c0b5d0b" + new string('0', 36 * 2) + "7c080000");   // sector 0x0B5D = 2909
        host.Send(0x148, entry);
        host.WaitFor(0x148);
        Kill(9002); Kill(9003); Kill(9003);                   // one reported twice
        host.Send(0x005, chat);
        var lines = new List<string>();
        for (var i = 0; i < 2; i++) lines.Add(ChatText(host.WaitFor(0x003).Payload));
        Assert.IsTrue(lines.Any(l => l.Contains("2/4 (50%)")), string.Join(" | ", lines));
        Assert.IsTrue(lines.Any(l => l.Contains("Your share: 2")), string.Join(" | ", lines));
    }
}
