-- World events: server-wide timed goals ("the realm must slay 1000 orcs in 2 hours"). Everybody who took part shares the reward,
-- the realm is told when an event starts, at 25/50/75 % and how it ended, and Sacred Gear clients with the "world-events" mod
-- show a movable progress bar. Copy into the server's scripts folder (default ./scripts) to use it.
--
--   !event                       progress of the running event (or the next one) to the player who asked
--   !sector                      the number of the sector the player stands in (for the `sectors` lists below)
--   /mods/world_events/          the public dashboard page (character names only)
--   /mods/world_events/state     the same as JSON (public, with WebUi:PublicApi=true also under /public/mods/...)
--
-- Everything is kept in this mod's own storage (script-data/world_events.json): the running event, who contributed what, the last
-- results and rewards still waiting for heroes who were offline. An event that ran out while the server was down is closed
-- (failed) when the server starts. Rewards need the hero in the world: heroes who are not get theirs when they enter.
-- Needs player.sector (the "sector" field of the player table) only for events with `sectors`; `names` needs --game-dir (without
-- it a creature has no name; use `types` then). The client bar needs the custom net messages (NETOPS, on by default).
server.describe("Server-wide timed goals (slay N creatures in a time), shared rewards, chat commands, a public dashboard and a progress bar for Sacred Gear clients.")

-- ---- configuration -----------------------------------------------------------------------------------------------------------

local CONFIG = {
  first_delay = 60,         -- seconds after the very first start of this mod until the first event begins
  cooldown = 900,           -- seconds between the end of one event and the start of the next
  min_players = 1,          -- an event only starts while at least this many heroes are connected
  result_hold = 120,        -- seconds the clients keep showing the result
  order = "sequence",       -- "sequence" (in the list's order) or "random" (never the same one twice in a row)
  milestones = { 25, 50, 75 },
  announce = true,          -- broadcast start, milestones and result to everybody
  max_contributors = 2000,  -- heroes remembered per event (more still count for the total, but get no share)
}

-- One entry per event. Rotation goes through the list; an event ends when its target is reached (success) or its time is up (fail).
--   id, title      short key and the name shown to players
--   goal           "kill": defeat `target` creatures (the only kind for now)
--   target         how many
--   duration       seconds
--   names          only creatures whose name contains one of these words count (needs --game-dir; any case)
--   types          or creature types (numbers), when the server has no game data
--   sectors        only kills in these sectors count, and the client bar shows only there (find the numbers with !sector)
--   text           the goal in words (default: built from the above)
--   min_contribution  kills a hero needs to share the reward (default 1)
--   reward         gold = pool shared by contribution, gold_each = gold for everyone who qualifies,
--                  item = {quality=.., category=.., level=..} (as server.drop_item) one for everyone who qualifies,
--                  top_item = the same for the best contributor only
local EVENTS = {
  { id = "orc-hunt", title = "Orc hunt", goal = "kill", target = 1000, duration = 2 * 3600, names = { "orc" },
    text = "Slay 1000 orcs", min_contribution = 10,
    reward = { gold = 50000, gold_each = 500, item = { quality = 3 }, top_item = { quality = 9 } } },
  { id = "great-cull", title = "The great cull", goal = "kill", target = 3000, duration = 3 * 3600,
    text = "Defeat 3000 creatures of any kind", min_contribution = 20,
    reward = { gold = 150000, gold_each = 1000, item = { quality = 6 }, top_item = { quality = 10 } } },
}

-- ---- helpers -----------------------------------------------------------------------------------------------------------

local PREFIX = "[World event] "
local NOW = server.time

local function say(text) if CONFIG.announce then server.broadcast(PREFIX .. text) end end

local function duration_text(seconds)
  seconds = math.max(0, math.floor(seconds + 0.5))
  if seconds >= 3600 then return string.format("%dh %02dm", math.floor(seconds / 3600), math.floor((seconds % 3600) / 60)) end
  if seconds >= 60 then return string.format("%dm %02ds", math.floor(seconds / 60), seconds % 60) end
  return seconds .. "s"
end

local function copy_list(list, convert)
  if type(list) ~= "table" then return nil end
  local out = {}
  for _, v in ipairs(list) do
    v = convert and convert(v) or v
    if v ~= nil then out[#out + 1] = v end
  end
  if #out == 0 then return nil end
  return out
end

local function number_of(v) return tonumber(v) end
local function lower_of(v) return type(v) == "string" and v ~= "" and v:lower() or nil end

-- A definition as it is stored with a running event (so editing this file does not change an event in progress).
local function snapshot(def)
  local reward = type(def.reward) == "table" and def.reward or {}
  return {
    id = tostring(def.id), title = tostring(def.title or def.id), goal = "kill",
    target = math.max(1, math.floor(tonumber(def.target) or 100)), duration = math.max(1, tonumber(def.duration) or 3600),
    names = copy_list(def.names, lower_of), types = copy_list(def.types, number_of), sectors = copy_list(def.sectors, number_of),
    text = def.text and tostring(def.text) or nil, min_contribution = math.max(1, math.floor(tonumber(def.min_contribution) or 1)),
    reward = {
      gold = math.max(0, math.floor(tonumber(reward.gold) or 0)), gold_each = math.max(0, math.floor(tonumber(reward.gold_each) or 0)),
      item = type(reward.item) == "table" and reward.item or nil, top_item = type(reward.top_item) == "table" and reward.top_item or nil,
    },
  }
end

local definitions = {}
for i, def in ipairs(EVENTS) do
  if type(def) == "table" and def.id and def.target then definitions[#definitions + 1] = def
  else server.log("event #" .. i .. " has no id or target: ignored") end
end

local function goal_text(def)
  if def.text then return def.text end
  local what = "creatures"
  if def.names then what = table.concat(def.names, "/") .. " creatures" end
  local text = "Defeat " .. def.target .. " " .. what
  if def.sectors then text = text .. " in sector " .. table.concat(def.sectors, "/") end
  return text
end

local function reward_text(def)
  local r, parts = def.reward or {}, {}
  local gold = (r.gold or 0) + (r.gold_each or 0)
  if gold > 0 then parts[#parts + 1] = "gold" end
  if r.item then parts[#parts + 1] = "an item" end
  if r.top_item then parts[#parts + 1] = "a prize for the best" end
  return table.concat(parts, ", ")
end

local function in_set(list, value)
  for _, v in ipairs(list) do if v == value then return true end end
  return false
end

-- Is the player where the event happens? (Events without sectors happen everywhere.)
local function in_area(def, player)
  return not def.sectors or in_set(def.sectors, player.sector or 0)
end

local function counts(def, player, monster)
  if not in_area(def, player) then return false end
  if not def.names and not def.types then return true end
  if def.names and monster.name then
    local name = monster.name:lower()
    for _, word in ipairs(def.names) do if name:find(word, 1, true) then return true end end
  end
  if def.types and in_set(def.types, monster.type) then return true end
  return false
end

-- ---- state (kept in this mod's storage) ---------------------------------------------------------------------------------------

local function fresh()
  return { seq = 0, index = 0, history = {}, next_start = NOW() + CONFIG.first_delay }
end

local function load_state()
  local ok, value = pcall(server.load, "state", nil)
  if not ok or type(value) ~= "table" then return fresh() end
  value.seq = tonumber(value.seq) or 0
  value.index = tonumber(value.index) or 0
  value.history = type(value.history) == "table" and value.history or {}
  value.next_start = tonumber(value.next_start) or NOW()
  if value.current ~= nil and (type(value.current) ~= "table" or type(value.current.def) ~= "table"
      or not tonumber(value.current.ends) or type(value.current.players) ~= "table") then
    value.current = nil
  end
  return value
end

local state = load_state()
local pending = (function()
  local ok, value = pcall(server.load, "pending", nil)
  return ok and type(value) == "table" and value or {}
end)()

local dirty, pending_dirty, push_due = false, false, false

local function save_state()
  local ok, err = pcall(server.save, "state", state)
  if ok then dirty = false else server.log("cannot save the state: " .. tostring(err)) end
  if pending_dirty then
    local ok2, err2 = pcall(server.save, "pending", pending)
    if ok2 then pending_dirty = false else server.log("cannot save pending rewards: " .. tostring(err2)) end
  end
end

local function participants(ev)
  local n = 0
  for _ in pairs(ev.players) do n = n + 1 end
  return n
end

local function ranked(ev)
  local list = {}
  for uid, p in pairs(ev.players) do list[#list + 1] = { uid = uid, name = p.name or "?", n = p.n or 0 } end
  table.sort(list, function(a, b)
    if a.n ~= b.n then return a.n > b.n end
    if a.name ~= b.name then return a.name < b.name end
    return a.uid < b.uid
  end)
  return list
end

-- ---- net: the progress bar of the clients -------------------------------------------------------------------------------------

local channel = net and net.channel("worldevents:state", { server_only = true })

local function payload_for(player)
  local t = NOW()
  local ev = state.current
  if ev then
    local def = ev.def
    local mine = ev.players[player.uid]
    return {
      v = 1, st = "active", n = ev.n, title = def.title, text = goal_text(def), count = ev.count, target = def.target,
      left = math.max(0, ev.ends - t), dur = def.duration, here = in_area(def, player), mine = mine and mine.n or 0,
      need = def.min_contribution, people = participants(ev), reward = reward_text(def),
    }
  end
  local last = state.last
  if last and t - last.finished < CONFIG.result_hold then
    return {
      v = 1, st = last.success and "won" or "failed", n = last.n, title = last.title, text = last.text, count = last.count,
      target = last.target, hold = CONFIG.result_hold - (t - last.finished), here = in_area(last, player), people = last.people,
    }
  end
  return { v = 1, st = "idle", next_in = math.max(0, (state.next_start or t) - t), here = false }
end

local function push(player)
  if not channel or not net.supports(player) then return end
  local ok, err = pcall(channel.send, channel, player, payload_for(player))
  if not ok then server.log("push failed: " .. tostring(err)) end
end

local function push_all()
  push_due = false
  if not channel then return end
  for _, player in ipairs(server.players()) do push(player) end
end

-- ---- rewards ----------------------------------------------------------------------------------------------------------------

-- Gives a reward entry { gold, items = { spec, ... } } to a player who is in the world. Never throws.
local function give(player, entry, why)
  local given = {}
  if (entry.gold or 0) > 0 then
    local ok, pile, err = pcall(server.drop_gold, player, entry.gold)
    if ok and pile then given[#given + 1] = entry.gold .. " gold"
    else server.log("no gold for " .. player.name .. ": " .. tostring(ok and err or pile)) end
  end
  for _, spec in ipairs(entry.items or {}) do
    local ok, item, err = pcall(server.drop_item, player, spec)
    if ok and item then given[#given + 1] = (item.name and item.name ~= "") and item.name or "an item"
    else server.log("no item for " .. player.name .. ": " .. tostring(ok and err or item)) end
  end
  if #given > 0 then server.message(player, PREFIX .. why .. ": " .. table.concat(given, ", ") .. " dropped for you.") end
end

local function online_in_world(uid)
  for _, player in ipairs(server.players()) do
    if player.uid == uid and player.in_game then return player end
  end
end

local function grant(uid, name, entry, why)
  local player = online_in_world(uid)
  if player then give(player, entry, why); return end
  local waiting = pending[uid]
  if not waiting then
    local count = 0
    for _ in pairs(pending) do count = count + 1 end
    if count >= 500 then server.log("pending rewards full: no reward kept for " .. name); return end
    waiting = { name = name, gold = 0, items = {}, why = why }
    pending[uid] = waiting
  end
  waiting.gold = (waiting.gold or 0) + (entry.gold or 0)
  for _, spec in ipairs(entry.items or {}) do if #waiting.items < 20 then waiting.items[#waiting.items + 1] = spec end end
  pending_dirty = true
end

local function deliver_pending()
  if next(pending) == nil then return end
  for _, player in ipairs(server.players()) do
    local waiting = pending[player.uid]
    if waiting and player.in_game then
      pending[player.uid] = nil
      pending_dirty = true
      give(player, waiting, waiting.why or "Your reward")
    end
  end
end

-- Splits the reward of a won event: who qualifies, how much gold, who gets the prize.
local function reward_plan(ev)
  local def, plan, total = ev.def, {}, 0
  local list = {}
  for _, p in ipairs(ranked(ev)) do
    if p.n >= def.min_contribution then list[#list + 1] = p; total = total + p.n end
  end
  for i, p in ipairs(list) do
    local entry = { uid = p.uid, name = p.name, n = p.n, gold = def.reward.gold_each, items = {} }
    if def.reward.gold > 0 and total > 0 then entry.gold = entry.gold + math.floor(def.reward.gold * p.n / total) end
    if def.reward.item then entry.items[#entry.items + 1] = def.reward.item end
    if i == 1 and def.reward.top_item then entry.items[#entry.items + 1] = def.reward.top_item end
    plan[#plan + 1] = entry
  end
  return plan
end

-- ---- the life of an event -----------------------------------------------------------------------------------------------------

local function finish(success)
  local ev = state.current
  if not ev then return end
  state.current = nil
  local def, top = ev.def, ranked(ev)
  local record = {
    n = ev.n, id = def.id, title = def.title, text = goal_text(def), success = success, count = ev.count, target = def.target,
    started = ev.started, finished = NOW(), people = #top, sectors = def.sectors, top = {},
  }
  for i = 1, math.min(5, #top) do record.top[i] = { name = top[i].name, n = top[i].n } end
  state.last = record
  table.insert(state.history, 1, { n = record.n, title = record.title, success = success, count = record.count, target = record.target,
    finished = record.finished, people = record.people, best = record.top[1] and record.top[1].name or nil })
  while #state.history > 10 do table.remove(state.history) end
  state.next_start = NOW() + CONFIG.cooldown

  if success then
    local ok, plan = pcall(reward_plan, ev)
    if ok then
      for _, entry in ipairs(plan) do
        local granted, err = pcall(grant, entry.uid, entry.name, entry, "Your reward for '" .. def.title .. "'")
        if not granted then server.log("reward failed for " .. entry.name .. ": " .. tostring(err)) end
      end
      record.rewarded = #plan
    else
      server.log("reward plan failed: " .. tostring(plan))
    end
    local names = {}
    for i = 1, math.min(3, #top) do names[i] = top[i].name .. " (" .. top[i].n .. ")" end
    say(def.title .. " is complete! " .. ev.count .. "/" .. def.target .. " in " .. duration_text(NOW() - ev.started)
      .. ". " .. (record.rewarded or 0) .. " heroes share the reward." .. (#names > 0 and (" Best: " .. table.concat(names, ", ") .. ".") or ""))
  else
    say(def.title .. " has failed: " .. ev.count .. "/" .. def.target .. " when the time ran out.")
  end
  save_state()
  push_all()
end

local function next_index()
  local n = #definitions
  if n == 0 then return nil end
  if CONFIG.order == "random" and n > 1 then
    local pick = server.random(n - 1)
    if (state.index or 0) > 0 and pick >= state.index then pick = pick + 1 end
    return pick
  end
  return ((state.index or 0) % n) + 1
end

local function start_event(index)
  local def = definitions[index]
  if not def then return end
  state.seq = state.seq + 1
  state.index = index
  local snap, t = snapshot(def), NOW()
  state.current = { n = state.seq, def = snap, started = t, ends = t + snap.duration, count = 0, players = {}, marks = {} }
  say(snap.title .. " begins: " .. goal_text(snap) .. " within " .. duration_text(snap.duration) .. "! Reward: "
    .. (reward_text(snap) ~= "" and reward_text(snap) or "glory") .. ". Type !event for the progress.")
  save_state()
  push_all()
end

local function status_lines(player)
  local t, lines = NOW(), {}
  local ev = state.current
  if ev then
    local def = ev.def
    local mine = ev.players[player.uid]
    lines[#lines + 1] = string.format("%s: %s - %d/%d (%d%%), %s left.", def.title, goal_text(def), ev.count, def.target,
      math.min(100, math.floor(ev.count * 100 / def.target)), duration_text(ev.ends - t))
    local own = mine and mine.n or 0
    lines[#lines + 1] = string.format("Your share: %d (%d needed for the reward)%s.", own, def.min_contribution,
      in_area(def, player) and "" or "; you are not in the right sector")
  else
    local last = state.last
    if last then
      lines[#lines + 1] = string.format("%s %s (%d/%d).", last.title, last.success and "was completed" or "failed", last.count, last.target)
    end
    lines[#lines + 1] = #definitions == 0 and "There are no world events configured."
      or ("No world event is running. The next one starts in about " .. duration_text((state.next_start or t) - t) .. ".")
  end
  return lines
end

-- ---- events ------------------------------------------------------------------------------------------------------------------

server.on("kill", function(player, monster)
  local ev = state.current
  if not ev or not counts(ev.def, player, monster) then return end
  ev.count = ev.count + 1
  local mine = ev.players[player.uid]
  if not mine and participants(ev) < CONFIG.max_contributors then
    mine = { n = 0 }
    ev.players[player.uid] = mine
  end
  if mine then mine.n = mine.n + 1; mine.name = player.character end
  dirty, push_due = true, true
  local percent = ev.count * 100 / ev.def.target
  for _, mark in ipairs(CONFIG.milestones) do
    if percent >= mark and ev.count < ev.def.target and not ev.marks[tostring(mark)] then
      ev.marks[tostring(mark)] = true
      say(ev.def.title .. ": " .. mark .. "% (" .. ev.count .. "/" .. ev.def.target .. "), " .. duration_text(ev.ends - NOW()) .. " left.")
    end
  end
  if ev.count >= ev.def.target then finish(true) end
end)

server.on("enter", function(player)
  if state.current then
    for _, line in ipairs(status_lines(player)) do server.message(player, PREFIX .. line) end
  end
  deliver_pending()
  push(player)
end)

server.on("sector", push)

server.on("chat", function(player, text)
  if text == "!event" then
    for _, line in ipairs(status_lines(player)) do server.message(player, PREFIX .. line) end
    return true
  elseif text == "!sector" then
    server.message(player, "You are in sector " .. (player.sector or 0) .. ".")
    return true
  end
end)

-- The clients ask for the state when they have said HELLO (and now and then again); at most once a second each.
local last_hello = {}
if channel then
  channel:on(function(player)
    local t = NOW()
    if (last_hello[player.slot] or 0) + 1 > t then return end
    last_hello[player.slot] = t
    push(player)
  end)
end

local ticks = 0
local function tick()
  local t = NOW()
  ticks = ticks + 1
  local ev = state.current
  if ev and t >= ev.ends then finish(false) end
  if not state.current and t >= (state.next_start or 0) and #definitions > 0 and #server.players() >= CONFIG.min_players then
    start_event(next_index())
  end
  deliver_pending()
  if dirty or pending_dirty then save_state() end
  if push_due or (state.current and ticks % 3 == 0) then push_all() end
end

server.every(2, tick)

-- A server that was down while an event ran out closes it now; a stale schedule is pulled in.
server.on("start", function()
  local t = NOW()
  local ev = state.current
  if ev and t >= ev.ends then finish(false) end
  if (state.next_start or 0) > t + CONFIG.cooldown + CONFIG.first_delay then state.next_start = t + CONFIG.first_delay end
  save_state()
end)

-- ---- the public state, the endpoint and the dashboard page ----------------------------------------------------------------------

local function public_state()
  local t, out = NOW(), { time = NOW() }
  local ev = state.current
  if ev then
    local top = {}
    for i, p in ipairs(ranked(ev)) do
      if i > 10 then break end
      top[i] = { name = p.name, kills = p.n }
    end
    out.active = {
      n = ev.n, id = ev.def.id, title = ev.def.title, text = goal_text(ev.def), count = ev.count, target = ev.def.target,
      percent = math.min(100, math.floor(ev.count * 100 / ev.def.target)), left = math.max(0, ev.ends - t), ends = ev.ends,
      sectors = ev.def.sectors, participants = participants(ev), top = top, reward = reward_text(ev.def),
    }
  else
    out.next_in = math.max(0, (state.next_start or t) - t)
  end
  out.last = state.last and {
    n = state.last.n, title = state.last.title, success = state.last.success, count = state.last.count, target = state.last.target,
    finished = state.last.finished, top = state.last.top,
  } or nil
  out.history = state.history
  out.events = {}
  for i, def in ipairs(definitions) do out.events[i] = { id = def.id, title = def.title, text = goal_text(snapshot(def)), duration = def.duration } end
  return out
end

server.endpoint("state", function() return public_state() end,
  { public = true, description = "the running event, the last result and the history as JSON (character names only)" })

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem;font-size:1.3rem;color:#e0a944}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(19rem,1fr));gap:1rem;padding:0 1.2rem 1.2rem}
section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}
ol,ul{margin:0;padding-left:1.3rem}li{margin:.15rem 0}b{color:#fff}small{color:#a39683}
.bar{height:1.2rem;background:#0e0b08;border:1px solid #5a4a36;border-radius:6px;overflow:hidden;margin:.4rem 0}
.bar i{display:block;height:100%;background:linear-gradient(#e8b84a,#b07a1e)}.ok{color:#8fd08f}.bad{color:#e08f8f}]]

local function section(title, body)
  return "<section><h2>" .. server.html(title) .. "</h2>" .. body .. "</section>"
end

server.page("", function()
  local h, s = server.html, public_state()
  local parts = {
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="10"><title>World events</title><style>', style, '</style></head><body><h1>World events</h1><main>',
  }
  local a = s.active
  if a then
    local body = { "<b>", h(a.title), "</b><br>", h(a.text), '<div class="bar"><i style="width:', tostring(a.percent), '%"></i></div>',
      string.format("%d / %d (%d%%) &middot; %s left &middot; %d heroes took part", a.count, a.target, a.percent, h(duration_text(a.left)), a.participants) }
    if a.sectors then body[#body + 1] = "<br><small>Counts in sector " .. h(table.concat(a.sectors, ", ")) .. " only</small>" end
    if a.reward ~= "" then body[#body + 1] = "<br><small>Reward: " .. h(a.reward) .. "</small>" end
    parts[#parts + 1] = section("Running now", table.concat(body))
    local rows = {}
    for _, p in ipairs(a.top) do rows[#rows + 1] = "<li><b>" .. h(p.name) .. "</b> " .. p.kills .. "</li>" end
    parts[#parts + 1] = section("Top contributors", "<ol>" .. table.concat(rows) .. "</ol>")
  else
    parts[#parts + 1] = section("Running now", "<small>No event right now. The next one starts in about " .. h(duration_text(s.next_in or 0)) .. ".</small>")
  end
  local rows = {}
  for _, e in ipairs(s.history) do
    rows[#rows + 1] = string.format('<li><b>%s</b> <span class="%s">%s</span> <small>%d/%d, %d heroes%s</small></li>', h(e.title),
      e.success and "ok" or "bad", e.success and "completed" or "failed", e.count, e.target, e.people or 0, e.best and (", best: " .. h(e.best)) or "")
  end
  parts[#parts + 1] = section("Recent events", #rows > 0 and ("<ul>" .. table.concat(rows) .. "</ul>") or "<small>none yet</small>")
  local upcoming = {}
  for _, e in ipairs(s.events) do upcoming[#upcoming + 1] = "<li><b>" .. h(e.title) .. "</b> <small>" .. h(e.text) .. ", " .. h(duration_text(e.duration)) .. "</small></li>" end
  parts[#parts + 1] = section("In the rotation", "<ul>" .. table.concat(upcoming) .. "</ul>")
  parts[#parts + 1] = "</main></body></html>"
  return table.concat(parts)
end, { public = true, title = "World events" })
