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

    // ---- through the real server -------------------------------------------------------------------------------------

    private sealed class Server : IDisposable
    {
        public readonly GameServerHost Host;
        private readonly Thread _thread;
        public Server(Folder folder)
        {
            var config = new GameServerConfig { TcpPort = 0, LanAnnounce = false, TickMilliseconds = 5 };
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
}
