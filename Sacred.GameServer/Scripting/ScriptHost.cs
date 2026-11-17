using Lua;
using Lua.Standard;

namespace Sacred.GameServer.Scripting;

/// <summary>A player as scripts see it.</summary>
public sealed record ScriptPlayer(int Slot, string Name, string Key, int Class, int Level, bool InGame, bool IsHost, int LobbyId, double OnlineSeconds = 0);

/// <summary>A defeated creature: its object reference, its type (resource id) and level when the server knows them.</summary>
public readonly record struct ScriptMonster(uint Reference, uint Type, int Level);

/// <summary>What a script asked for with <c>server.drop_item</c>.</summary>
public readonly record struct ScriptLootRequest(int Level, int HeroClass, uint? Quality, Sacred.Core.Pak.Items.SacredItemCategory? Category, uint Seed);

/// <summary>An item a script dropped: reference, type, quality, generated name and category.</summary>
public sealed record ScriptDrop(uint Reference, uint Type, uint Quality, string Name, string Category);

/// <summary>What scripts can do on the server. Implemented by the game server; called on the game loop thread only.</summary>
public interface IScriptServer
{
    long NowMs { get; }
    IReadOnlyList<ScriptPlayer> Players();
    ScriptPlayer? Player(int slot);
    /// <summary>Server chat line to everybody.</summary>
    void Broadcast(string text);
    /// <summary>Private server chat line to one player.</summary>
    void Message(int slot, string text);
    /// <summary>Runs after the current script call returns (removing a player fires "leave" itself).</summary>
    void Kick(int slot, string reason);
    /// <summary>Generates equipment for the player and drops it next to its hero; null with <paramref name="error"/> on failure.</summary>
    ScriptDrop? DropItem(int slot, in ScriptLootRequest request, out string? error);
    /// <summary>Publishes a board (ladder) for the web UI and its public API.</summary>
    void Publish(string name, string title, IReadOnlyList<(string Name, double Value)> rows);
}

/// <summary>
/// The Lua side of server scripts, on LuaCSharp (pure managed Lua 5.2, Native AOT safe). One <see cref="LuaState"/> holds every
/// <c>*.lua</c> file of the scripts folder; they register handlers on the <c>server</c> table:
/// <code>
/// server.on("join"|"enter"|"leave", function(player) end)
/// server.on("chat", function(player, text) return true end)   -- true: the line is not relayed
/// server.on("kill", function(player, monster) end)            -- monster = { ref, type, level }
/// server.every(seconds, fn) / server.after(seconds, fn)
/// server.broadcast(text) / server.message(player or slot, text) / server.kick(player or slot, reason)
/// server.players() / server.player(slot) / server.time() / server.log(text)
/// server.get(player or nil, key [, default]) / server.set(player or nil, key, value) / server.add(player or nil, key, n)
/// server.drop_item(player, { quality = 0..14, category = "ring", level = n, class = n, seed = n })  -- returns item or nil, error
/// server.random([m [, n]])
/// server.top(key, n) -> { { key, name, value }, ... }  (saved numbers of every hero, also offline)
/// server.publish(name, title, rows)                    -- rows = { { name = "...", value = n }, ... } for the web UI
/// </code>
/// Sandbox: basic (without dofile/loadfile/load), string, table, math and bit32 libraries only; no io, os, require or debug.
/// Every handler or timer call gets an instruction budget (<see cref="ScriptOptions.InstructionBudget"/>) enforced through a
/// count hook, so an endless loop is stopped with an error instead of freezing the server. Scripts are the server owner's
/// own code: the sandbox guards against mistakes, not against a hostile script author.
/// </summary>
public sealed class ScriptHost
{
    private const int HookInterval = 1000, MaxConsecutiveErrors = 20;
    private readonly ScriptOptions _options;
    private readonly IScriptServer _server;
    private readonly ScriptStore _store;
    private readonly Dictionary<string, List<Handler>> _handlers = new(StringComparer.Ordinal);
    private readonly List<Timer> _timers = new();
    private readonly List<Timer> _newTimers = new();
    private readonly LuaValue[] _one = new LuaValue[1], _two = new LuaValue[2];
    private LuaState? _state;
    private long _budget;
    private bool _exhausted;
    private string _currentFile = "";
    private FileSystemWatcher? _watcher;
    private volatile bool _changed;
    private long _reloadDueMs;

    private sealed class Handler { public required LuaFunction Function; public required string File; public int Errors; }
    private sealed class Timer { public required LuaFunction Function; public required string File; public long DueMs; public long IntervalMs; public int Errors; }

    public ScriptHost(ScriptOptions options, IScriptServer server, ScriptStore store) { _options = options; _server = server; _store = store; }

    /// <summary>Loaded files and their load errors (diagnostics, web UI).</summary>
    public IReadOnlyList<(string File, string? Error)> Files => _files;
    private readonly List<(string File, string? Error)> _files = new();
    public long Errors { get; private set; }
    public int HandlerCount => _handlers.Values.Sum(h => h.Count);

    /// <summary>(Re)creates the Lua state and runs every script file, then fires "start".</summary>
    public void Load()
    {
        _handlers.Clear(); _timers.Clear(); _newTimers.Clear(); _files.Clear();
        _state = CreateState();
        var files = Directory.Exists(_options.Directory)
            ? Directory.GetFiles(_options.Directory, "*.lua").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : [];
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            string? error = null;
            try
            {
                var source = File.ReadAllText(path);
                _currentFile = name;
                Run(() => _state.DoStringAsync(source, name));
            }
            catch (Exception e) when (IsScriptError(e) || e is IOException or UnauthorizedAccessException) { error = Describe(e); }
            _files.Add((name, error));
            Log.Info(error is null ? $"SCRIPT: loaded {name}." : $"SCRIPT: {name} failed to load: {error}");
        }
        _currentFile = "";
        Fire("start");
        Log.Info($"SCRIPT: {files.Length} file(s), {HandlerCount} handler(s), {_timers.Count + _newTimers.Count} timer(s).");
    }

    /// <summary>Watches the folder: a changed, added or removed script reloads everything half a second later.</summary>
    public void Watch()
    {
        if (_watcher is not null || !Directory.Exists(_options.Directory)) return;
        _watcher = new FileSystemWatcher(_options.Directory, "*.lua") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        _watcher.Changed += (_, _) => _changed = true;
        _watcher.Created += (_, _) => _changed = true;
        _watcher.Deleted += (_, _) => _changed = true;
        _watcher.Renamed += (_, _) => _changed = true;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Requests a reload at the next pump (admin command).</summary>
    public void RequestReload() => _changed = true;

    /// <summary>Game loop: timers and pending reloads.</summary>
    public void Pump(long nowMs)
    {
        if (_changed) { _changed = false; _reloadDueMs = nowMs + 500; }
        if (_reloadDueMs != 0 && nowMs >= _reloadDueMs) { _reloadDueMs = 0; Log.Info("SCRIPT: reloading."); Load(); }
        if (_newTimers.Count > 0) { _timers.AddRange(_newTimers); _newTimers.Clear(); }
        for (var i = _timers.Count - 1; i >= 0; i--)
        {
            var timer = _timers[i];
            if (nowMs < timer.DueMs) continue;
            if (timer.IntervalMs > 0) timer.DueMs = nowMs + timer.IntervalMs; else _timers.RemoveAt(i);
            if (!Call(timer.Function, timer.File, default, ref timer.Errors, out _)) { if (timer.Errors >= MaxConsecutiveErrors) _timers.Remove(timer); }
        }
    }

    public void Dispose() { _watcher?.Dispose(); _watcher = null; }

    // ---- events ---------------------------------------------------------------------------------------------------------

    public bool Has(string name) => _handlers.TryGetValue(name, out var list) && list.Count > 0;

    /// <summary>Calls every handler of <paramref name="name"/>; true when one returned true (for "chat": swallow the line).</summary>
    public bool Fire(string name, LuaValue first = default, LuaValue second = default, int count = 0)
    {
        if (!_handlers.TryGetValue(name, out var list) || list.Count == 0) return false;
        ReadOnlySpan<LuaValue> args = count switch { 0 => default, 1 => One(first), _ => Two(first, second) };
        var consumed = false;
        for (var i = 0; i < list.Count; i++)
        {
            var handler = list[i];
            if (Call(handler.Function, handler.File, args, ref handler.Errors, out var result)) consumed |= result.ToBoolean();
            else if (handler.Errors >= MaxConsecutiveErrors)
            {
                Log.Info($"SCRIPT: {handler.File}: '{name}' handler disabled after {MaxConsecutiveErrors} errors in a row.");
                list.RemoveAt(i--);
            }
        }
        return consumed;
    }

    public bool FirePlayer(string name, ScriptPlayer player) => Has(name) && Fire(name, PlayerTable(player), default, 1);
    public bool FireChat(ScriptPlayer player, string text) => Has("chat") && Fire("chat", PlayerTable(player), text, 2);
    public bool FireKill(ScriptPlayer player, ScriptMonster monster)
    {
        if (!Has("kill")) return false;
        var table = new LuaTable(0, 3) { ["ref"] = monster.Reference, ["type"] = monster.Type, ["level"] = monster.Level };
        return Fire("kill", PlayerTable(player), table, 2);
    }

    private ReadOnlySpan<LuaValue> One(LuaValue a) { _one[0] = a; return _one; }
    private ReadOnlySpan<LuaValue> Two(LuaValue a, LuaValue b) { _two[0] = a; _two[1] = b; return _two; }

    private bool Call(LuaFunction function, string file, ReadOnlySpan<LuaValue> args, ref int errors, out LuaValue result)
    {
        result = LuaValue.Nil;
        if (_state is null) return false;
        try
        {
            _currentFile = file;
            var values = Run(_state.CallAsync(function, args));
            result = values.Length > 0 ? values[0] : LuaValue.Nil;
            errors = 0;
            return true;
        }
        catch (Exception e) when (IsScriptError(e) || e is InvalidOperationException or ArgumentException)
        {
            errors++; Errors++;
            Log.Info($"SCRIPT: {file}: {Describe(e)}");
            return false;
        }
        finally { _currentFile = ""; }
    }

    /// <summary>Runs one Lua entry with a fresh instruction budget. Our functions never await, so the call completes synchronously.</summary>
    private T Run<T>(ValueTask<T> task)
    {
        if (!task.IsCompleted) throw new InvalidOperationException("a script call did not finish synchronously");
        return task.Result;
    }

    private void Run(Func<ValueTask<LuaValue[]>> start)
    {
        _budget = Math.Max(1, _options.InstructionBudget / HookInterval); _exhausted = false;
        Run(start());
    }

    private static bool IsScriptError(Exception e) => e is LuaRuntimeException or LuaParseException or LuaCompileException or LuaStackOverflowException
        or LuaCanceledException or ScriptBudgetException or InvalidCastException;

    private static string Describe(Exception e) => e switch
    {
        ScriptBudgetException => e.Message,
        LuaRuntimeException { InnerException: ScriptBudgetException inner } => inner.Message,
        _ when e.InnerException is ScriptBudgetException inner => inner.Message,
        _ => e.Message,
    };

    // ---- the Lua state ----------------------------------------------------------------------------------------------------

    private LuaState CreateState()
    {
        var state = LuaState.Create();
        state.OpenBasicLibrary(); state.OpenStringLibrary(); state.OpenTableLibrary(); state.OpenMathLibrary(); state.OpenBitwiseLibrary();
        foreach (var name in (ReadOnlySpan<string>)["dofile", "loadfile", "load", "collectgarbage"]) state.Environment[name] = LuaValue.Nil;
        state.Environment["print"] = Fn("print", c =>
        {
            var parts = new string[c.ArgumentCount];
            for (var i = 0; i < parts.Length; i++) parts[i] = c.GetArgument(i).ToString();
            Log.Info($"SCRIPT {_currentFile}: {string.Join('\t', parts)}");
            return c.Return();
        });
        // The budget: every HookInterval instructions; once exhausted it keeps failing until the entry call returns, so a pcall
        // around an endless loop cannot swallow it for long.
        state.SetHook(Fn("budget", c =>
        {
            if (_exhausted || --_budget < 0)
            {
                _exhausted = true;
                throw new ScriptBudgetException(BudgetMessage);
            }
            return c.Return();
        }), "", HookInterval);
        // pcall/xpcall must not swallow the budget: once it is exhausted they rethrow instead of returning false.
        foreach (var name in (ReadOnlySpan<string>)["pcall", "xpcall"])
        {
            var original = state.Environment[name].Read<LuaFunction>();
            state.Environment[name] = new LuaFunction(name, async (c, ct) =>
            {
                if (_exhausted) throw new ScriptBudgetException(BudgetMessage);
                var results = await c.State.CallAsync(original, c.Arguments, ct).ConfigureAwait(false);
                if (_exhausted) throw new ScriptBudgetException(BudgetMessage);
                return c.Return(results);
            });
        }
        state.Environment["server"] = ServerTable();
        return state;
    }

    private string BudgetMessage => $"stopped: more than {_options.InstructionBudget:N0} instructions in one call (endless loop?)";

    private static LuaFunction Fn(string name, Func<LuaFunctionExecutionContext, int> body) =>
        new(name, (context, _) => new ValueTask<int>(body(context)));

    private LuaTable ServerTable()
    {
        var server = new LuaTable();
        server["on"] = Fn("on", c =>
        {
            var name = c.GetArgument<string>(0);
            var function = c.GetArgument<LuaFunction>(1);
            if (!_handlers.TryGetValue(name, out var list)) _handlers[name] = list = new();
            list.Add(new Handler { Function = function, File = _currentFile });
            return c.Return();
        });
        server["every"] = Fn("every", c => AddTimer(c, repeat: true));
        server["after"] = Fn("after", c => AddTimer(c, repeat: false));
        server["broadcast"] = Fn("broadcast", c => { _server.Broadcast(c.GetArgument(0).ToString()); return c.Return(); });
        server["message"] = Fn("message", c => { if (SlotOf(c.GetArgument(0)) is var slot and > 0) _server.Message(slot, c.GetArgument(1).ToString()); return c.Return(); });
        server["kick"] = Fn("kick", c => { if (SlotOf(c.GetArgument(0)) is var slot and > 0) _server.Kick(slot, c.HasArgument(1) ? c.GetArgument(1).ToString() : ""); return c.Return(); });
        server["log"] = Fn("log", c => { Log.Info($"SCRIPT {_currentFile}: {c.GetArgument(0)}"); return c.Return(); });
        server["time"] = Fn("time", c => c.Return(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
        server["players"] = Fn("players", c =>
        {
            var players = _server.Players();
            var table = new LuaTable(players.Count, 0);
            for (var i = 0; i < players.Count; i++) table[i + 1] = PlayerTable(players[i]);
            return c.Return(table);
        });
        server["player"] = Fn("player", c => c.Return(_server.Player((int)c.GetArgument<double>(0)) is { } p ? PlayerTable(p) : LuaValue.Nil));
        server["get"] = Fn("get", c =>
        {
            var owner = OwnerOf(c.GetArgument(0));
            var key = c.GetArgument(1).ToString();
            if (_store.TryGet(owner, key, out var value)) return c.Return(ToLua(value));
            return c.Return(c.HasArgument(2) ? c.GetArgument(2) : LuaValue.Nil);
        });
        server["set"] = Fn("set", c =>
        {
            _store.Set(OwnerOf(c.GetArgument(0)), c.GetArgument(1).ToString(), FromLua(c.HasArgument(2) ? c.GetArgument(2) : LuaValue.Nil));
            return c.Return();
        });
        server["add"] = Fn("add", c =>
        {
            var owner = OwnerOf(c.GetArgument(0));
            var key = c.GetArgument(1).ToString();
            var amount = c.HasArgument(2) ? c.GetArgument<double>(2) : 1;
            var total = (_store.TryGet(owner, key, out var value) && value is double d ? d : 0) + amount;
            _store.Set(owner, key, total);
            return c.Return(total);
        });
        server["drop_item"] = Fn("drop_item", c =>
        {
            var target = c.GetArgument(0);
            var slot = SlotOf(target);
            var options = c.HasArgument(1) && c.GetArgument(1).Type == LuaValueType.Table ? c.GetArgument(1).Read<LuaTable>() : null;
            var player = _server.Player(slot);
            if (player is null) return c.Return(LuaValue.Nil, "no such player");
            double Number(string key, double fallback) => options?[key] is { Type: LuaValueType.Number } n ? n.Read<double>() : fallback;
            Sacred.Core.Pak.Items.SacredItemCategory? category = null;
            if (options?["category"] is { Type: LuaValueType.String } name)
            {
                if (!LootService.Categories.TryGetValue(name.Read<string>(), out var known)) return c.Return(LuaValue.Nil, "unknown category " + name.Read<string>());
                category = known;
            }
            uint? quality = options?["quality"] is { Type: LuaValueType.Number } q ? (uint)Math.Clamp(q.Read<double>(), 0, 14) : null;
            var request = new ScriptLootRequest((int)Number("level", Math.Max(1, player.Level)), (int)Number("class", player.Class), quality, category,
                (uint)Number("seed", Random.Shared.Next()));
            var drop = _server.DropItem(slot, request, out var error);
            if (drop is null) return c.Return(LuaValue.Nil, error ?? "failed");
            return c.Return(new LuaTable(0, 5) { ["ref"] = drop.Reference, ["type"] = drop.Type, ["quality"] = drop.Quality, ["name"] = drop.Name, ["category"] = drop.Category });
        });
        server["top"] = Fn("top", c =>
        {
            var key = c.GetArgument(0).ToString();
            var count = c.HasArgument(1) ? Math.Clamp((int)c.GetArgument<double>(1), 1, 1000) : 10;
            var rows = _store.Top(key, count);
            var table = new LuaTable(rows.Count, 0);
            for (var i = 0; i < rows.Count; i++) table[i + 1] = new LuaTable(0, 3) { ["key"] = rows[i].Owner, ["name"] = rows[i].Name, ["value"] = rows[i].Value };
            return c.Return(table);
        });
        server["publish"] = Fn("publish", c =>
        {
            var name = c.GetArgument(0).ToString();
            var title = c.HasArgument(1) ? c.GetArgument(1).ToString() : name;
            var rows = new List<(string, double)>();
            if (c.HasArgument(2) && c.GetArgument(2).Type == LuaValueType.Table)
            {
                var list = c.GetArgument(2).Read<LuaTable>();
                for (var i = 1; i <= Math.Min(list.ArrayLength, 1000); i++)
                    if (list[i].Type == LuaValueType.Table && list[i].Read<LuaTable>() is var row)
                        rows.Add((row["name"].ToString(), row["value"].Type == LuaValueType.Number ? row["value"].Read<double>() : 0));
            }
            _server.Publish(name, title, rows);
            return c.Return();
        });
        server["random"] = Fn("random", c =>
        {
            if (!c.HasArgument(0)) return c.Return(Random.Shared.NextDouble());
            var low = c.HasArgument(1) ? (int)c.GetArgument<double>(0) : 1;
            var high = (int)c.GetArgument<double>(c.HasArgument(1) ? 1 : 0);
            return c.Return(high < low ? low : Random.Shared.Next(low, high + 1));
        });
        return server;
    }

    private int AddTimer(LuaFunctionExecutionContext c, bool repeat)
    {
        var seconds = Math.Max(0.05, c.GetArgument<double>(0));
        var interval = (long)(seconds * 1000);
        _newTimers.Add(new Timer { Function = c.GetArgument<LuaFunction>(1), File = _currentFile, DueMs = _server.NowMs + interval, IntervalMs = repeat ? interval : 0 });
        return c.Return();
    }

    private static int SlotOf(LuaValue value) => value.Type switch
    {
        LuaValueType.Number => (int)value.Read<double>(),
        LuaValueType.Table when value.Read<LuaTable>()["slot"] is { Type: LuaValueType.Number } slot => (int)slot.Read<double>(),
        _ => 0,
    };

    private static string OwnerOf(LuaValue value) =>
        value.Type == LuaValueType.Table && value.Read<LuaTable>()["key"] is { Type: LuaValueType.String } key ? key.Read<string>() : ScriptStore.GlobalKey;

    private static LuaValue ToLua(object? value) => value switch { double d => d, bool b => b, string s => s, _ => LuaValue.Nil };

    private static object? FromLua(LuaValue value) => value.Type switch
    {
        LuaValueType.Number => value.Read<double>(),
        LuaValueType.Boolean => value.Read<bool>(),
        LuaValueType.String => value.Read<string>(),
        LuaValueType.Nil => null,
        _ => value.ToString(),
    };

    private static LuaTable PlayerTable(ScriptPlayer p) => new(0, 9)
    {
        ["slot"] = p.Slot, ["name"] = p.Name, ["key"] = p.Key, ["class"] = p.Class, ["level"] = p.Level,
        ["in_game"] = p.InGame, ["host"] = p.IsHost, ["id"] = p.LobbyId, ["online"] = p.OnlineSeconds,
    };
}

/// <summary>A script call ran out of its instruction budget.</summary>
public sealed class ScriptBudgetException(string message) : Exception(message);
