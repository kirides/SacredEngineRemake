-- Bounty board: the server posts a few bounties at a time ("kill 25 orcs", optionally only in some sectors), each hero works on
-- them on their own, and finishing one pays gold and/or an item. Bounties run for a while, expire and are replaced from a pool,
-- so the board rotates by itself. Copy into the server's scripts folder (default ./scripts) to use it.
--
--   !bounty                      the active bounties and your progress (chat, to the player who asked)
--   !where                       the number of the sector you stand in (for the `sectors` lists of the pool below)
--   /mods/bounty_board/          the public dashboard page (character names only)
--   /mods/bounty_board/state     the same as JSON (public, with WebUi:PublicApi=true also under /public/mods/...)
--
-- Sacred Gear clients with the "bounty" mod (gear/examples/bounty of the client repository) show the active bounties and a
-- progress bar in a panel (F8) over the custom net messages (NETOPS, on by default); the channel is "bounty:state" and the
-- server pushes the heroes' own progress to them (see "Protocol" at the end of this comment).
--
-- Everything lives in this mod's own storage (script-data/bounty_board.json): the board, each hero's progress, the last
-- completions. A bounty is a snapshot of its pool entry, so editing the pool does not change bounties already posted.
-- `names` needs --game-dir (without it a creature has no name; use `types`). `sectors` needs player.sector.
-- Rewards: gold is dropped as a pile next to the hero, items with server.drop_item (needs --game-dir).
--
-- Protocol (channel "bounty:state", server_only). The client may send anything (e.g. { req = 1 }) to ask for its state; the
-- server answers with, and pushes on every change of the board or of that hero's progress:
--   { v = 1, next_in = seconds, bounties = { { id, title, text, region, target, n, done, left, reward, claims, level }, ... } }
-- `n` is the hero's own progress, `left` the seconds until the bounty expires (relative, so clocks need not agree).
server.describe("A rotating bounty board: kill N creatures (optionally in some sectors) for gold or items, a public dashboard and a panel for Sacred Gear clients.")

-- ---- configuration -----------------------------------------------------------------------------------------------------------

local CONFIG = {
  slots = 3,                -- bounties on the board at the same time
  duration = 3 * 3600,      -- default lifetime of a bounty, seconds (a pool entry can set its own `duration`)
  announce = true,          -- broadcast new bounties and completions
  history = 10,             -- completions and expiries kept for the dashboard
  max_hunters = 5000,       -- heroes remembered per bounty (more can still play, their progress is just not kept)
}

-- The pool the board draws from (never the same entry twice at once).
--   id, title      short key and the name shown to players
--   names          only creatures whose name contains one of these words count (needs --game-dir; any case)
--   types          or creature types (numbers), when the server has no game data
--   sectors        only kills in these sectors count (find the numbers with !where); region = the name shown for them
--   target         how many: a number, or { min, max } (drawn when the bounty is posted)
--   min_level      heroes below this level do not count (default 1)
--   duration       lifetime in seconds (default CONFIG.duration)
--   reward         gold = coins for each hero who finishes, item = { quality = .., category = .. } (as server.drop_item)
local POOL = {
  { id = "orcs", title = "Orc slayer", names = { "orc" }, target = { 20, 40 }, reward = { gold = 1500 } },
  { id = "undead", title = "Grave watch", names = { "skeleton", "zombie", "ghoul" }, target = { 25, 50 }, reward = { gold = 2500 } },
  { id = "spiders", title = "Web cleaner", names = { "spider" }, target = { 15, 30 }, reward = { gold = 1000, item = { quality = 3 } } },
  { id = "brigands", title = "Road safety", names = { "brigand", "thief", "bandit" }, target = { 20, 35 }, reward = { gold = 2000 } },
  { id = "dragons", title = "Dragon hunter", names = { "dragon" }, target = { 3, 6 }, min_level = 20, reward = { gold = 8000, item = { quality = 6 } } },
  -- A bounty for one region only (sector numbers of your server; !where tells them):
  -- { id = "plains-wolves", title = "Wolves of the plains", names = { "wolf" }, target = { 15, 25 },
  --   sectors = { 1801, 1802 }, region = "Plains of Ancaria", reward = { gold = 1200 } },
}

-- ---- helpers -----------------------------------------------------------------------------------------------------------------

local PREFIX = "[Bounty] "
local NOW = server.time

local function say(text) if CONFIG.announce then pcall(server.broadcast, PREFIX .. text) end end

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
    if convert then v = convert(v) end
    if v ~= nil then out[#out + 1] = v end
  end
  if #out == 0 then return nil end
  return out
end

local function lower_of(v) return type(v) == "string" and v ~= "" and v:lower() or nil end

local function in_set(list, value)
  for _, v in ipairs(list) do if v == value then return true end end
  return false
end

local pool = {}
for i, def in ipairs(POOL) do
  if type(def) == "table" and def.id and (def.names or def.types) and def.target then pool[#pool + 1] = def
  else server.log("pool entry #" .. i .. " needs an id, names or types and a target: ignored") end
end

local function draw_target(spec)
  if type(spec) == "table" then
    local lo = math.max(1, math.floor(tonumber(spec[1]) or 1))
    local hi = math.max(lo, math.floor(tonumber(spec[2]) or lo))
    return lo == hi and lo or server.random(lo, hi)
  end
  return math.max(1, math.floor(tonumber(spec) or 10))
end

local function reward_text(r)
  local parts = {}
  if (r.gold or 0) > 0 then parts[#parts + 1] = r.gold .. " gold" end
  if r.item then parts[#parts + 1] = "an item" end
  return #parts > 0 and table.concat(parts, " and ") or "glory"
end

-- What a bounty asks for, in words.
local function what_text(b)
  local what = b.names and (table.concat(b.names, "/") .. " creatures") or "creatures"
  local text = "Defeat " .. b.target .. " " .. what
  if b.sectors then text = text .. " in " .. (b.region or ("sector " .. table.concat(b.sectors, "/"))) end
  return text
end

-- ---- state -------------------------------------------------------------------------------------------------------------------

local function fresh() return { seq = 0, board = {}, history = {} } end

local function load_state()
  local ok, value = pcall(server.load, "state", nil)
  if not ok or type(value) ~= "table" then return fresh() end
  value.seq = tonumber(value.seq) or 0
  value.history = type(value.history) == "table" and value.history or {}
  local board = {}
  for _, b in ipairs(type(value.board) == "table" and value.board or {}) do
    if type(b) == "table" and tonumber(b.id) and tonumber(b.ends) and tonumber(b.target) and type(b.players) == "table"
        and type(b.reward) == "table" then
      board[#board + 1] = b
    end
  end
  value.board = board
  return value
end

local state = load_state()
local dirty = false

local function save_state()
  local ok, err = pcall(server.save, "state", state)
  if ok then dirty = false else server.log("cannot save the board: " .. tostring(err)) end
end

local function claims(b)
  local n = 0
  for _, p in pairs(b.players) do if p.done then n = n + 1 end end
  return n
end

local function remember(entry)
  table.insert(state.history, 1, entry)
  while #state.history > CONFIG.history do table.remove(state.history) end
end

-- ---- net: the panel of the clients -------------------------------------------------------------------------------------------

local channel = net and net.channel("bounty:state", { server_only = true })

local function payload_for(player)
  local t, list = NOW(), {}
  for _, b in ipairs(state.board) do
    local mine = b.players[player.uid]
    list[#list + 1] = {
      id = b.id, title = b.title, text = what_text(b), region = b.region, target = b.target, n = mine and mine.n or 0,
      done = mine and mine.done or false, left = math.max(0, math.floor(b.ends - t)), reward = reward_text(b.reward),
      claims = claims(b), level = b.min_level,
    }
  end
  local soonest
  for _, b in ipairs(state.board) do soonest = soonest and math.min(soonest, b.ends) or b.ends end
  return { v = 1, bounties = list, count = #list, next_in = soonest and math.max(0, math.floor(soonest - t)) or 0 }
end

local function push(player)
  if not channel or not player or not net.supports(player) then return end
  local ok, err = pcall(channel.send, channel, player, payload_for(player))
  if not ok then server.log("cannot push the board: " .. tostring(err)) end
end

local function push_all()
  if not channel then return end
  for _, player in ipairs(server.players()) do push(player) end
end

-- Kills can come many per second: progress is pushed at most once a second per hero.
local push_due = {}
server.every(1, function()
  if next(push_due) == nil then return end
  local due = push_due
  push_due = {}
  for _, player in ipairs(server.players()) do
    if due[player.slot] then push(player) end
  end
end)

if channel then
  channel:on(function(player) push(player) end)
end

-- ---- the board ---------------------------------------------------------------------------------------------------------------

local function snapshot(def, index, slots)
  local reward = type(def.reward) == "table" and def.reward or {}
  local t = NOW()
  local duration = math.max(60, tonumber(def.duration) or CONFIG.duration)
  -- The first fill staggers the end times so the whole board does not expire at once.
  if index then duration = duration * (0.4 + 0.6 * index / slots) end
  state.seq = state.seq + 1
  return {
    id = state.seq, def = tostring(def.id), title = tostring(def.title or def.id),
    names = copy_list(def.names, lower_of), types = copy_list(def.types, tonumber), sectors = copy_list(def.sectors, tonumber),
    region = def.region and tostring(def.region) or nil, target = draw_target(def.target),
    min_level = math.max(1, math.floor(tonumber(def.min_level) or 1)),
    reward = {
      gold = math.max(0, math.floor(tonumber(reward.gold) or 0)),
      item = type(reward.item) == "table" and reward.item or nil,
    },
    posted = t, ends = t + duration, players = {},
  }
end

local function active_defs()
  local set = {}
  for _, b in ipairs(state.board) do set[b.def] = true end
  return set
end

-- Fills the board up to CONFIG.slots from the pool; returns whether something was posted.
local function fill(first)
  local posted = false
  while #state.board < CONFIG.slots do
    local taken, candidates = active_defs(), {}
    for _, def in ipairs(pool) do if not taken[tostring(def.id)] then candidates[#candidates + 1] = def end end
    if #candidates == 0 then break end
    local def = candidates[server.random(#candidates)]
    local b = snapshot(def, first and (#state.board + 1) or nil, CONFIG.slots)
    state.board[#state.board + 1] = b
    posted = true
    say("New bounty: " .. b.title .. ": " .. what_text(b) .. ". Reward: " .. reward_text(b.reward) .. ". Type !bounty.")
  end
  return posted
end

local function expire()
  local t, changed, kept = NOW(), false, {}
  for _, b in ipairs(state.board) do
    if b.ends <= t then
      changed = true
      remember({ kind = "expired", title = b.title, time = t, claims = claims(b) })
      say("The bounty \"" .. b.title .. "\" has ended (" .. claims(b) .. " hunters finished it).")
    else
      kept[#kept + 1] = b
    end
  end
  state.board = kept
  return changed
end

local function tick()
  local changed = expire()
  if #pool > 0 and fill(#state.board == 0 and state.seq == 0) then changed = true end
  if changed then save_state(); push_all() elseif dirty then save_state() end
end

server.every(5, function() local ok, err = pcall(tick); if not ok then server.log("tick: " .. tostring(err)) end end)
server.on("start", function() local ok, err = pcall(tick); if not ok then server.log("start: " .. tostring(err)) end end)

-- ---- kills -------------------------------------------------------------------------------------------------------------------

local function counts(b, player, monster)
  if (player.level or 0) < b.min_level then return false end
  if b.sectors and not in_set(b.sectors, player.sector or 0) then return false end
  if b.names and monster.name then
    local name = monster.name:lower()
    for _, word in ipairs(b.names) do if name:find(word, 1, true) then return true end end
  end
  if b.types and monster.type and in_set(b.types, monster.type) then return true end
  return false
end

local function hunters(b)
  local n = 0
  for _ in pairs(b.players) do n = n + 1 end
  return n
end

local function pay(player, b)
  local r = b.reward
  if (r.gold or 0) > 0 then
    local ok, drop, err = pcall(server.drop_gold, player, r.gold)
    if not ok or not drop then server.log("no gold for " .. player.character .. ": " .. tostring(ok and err or drop)) end
  end
  if r.item then
    local ok, item, err = pcall(server.drop_item, player, r.item)
    if not ok or not item then server.log("no item for " .. player.character .. ": " .. tostring(ok and err or item)) end
  end
end

local function complete(player, b)
  server.add(player, "bounties")   -- the dashboard's ladder (server.top reads this mod's per-hero numbers)
  remember({ kind = "done", title = b.title, hero = player.character, time = NOW() })
  pcall(server.message, player, PREFIX .. "Bounty complete: " .. b.title .. "! Reward: " .. reward_text(b.reward) .. ".")
  say(player.character .. " completed the bounty \"" .. b.title .. "\".")
  pay(player, b)
end

server.on("kill", function(player, monster)
  if type(monster) ~= "table" then return end
  for _, b in ipairs(state.board) do
    if b.ends > NOW() and counts(b, player, monster) then
      local mine = b.players[player.uid]
      if not mine and hunters(b) < CONFIG.max_hunters then
        mine = { n = 0 }
        b.players[player.uid] = mine
      end
      if mine and not mine.done then
        mine.n = mine.n + 1
        mine.name = player.character
        dirty, push_due[player.slot] = true, true
        if mine.n >= b.target then
          mine.done = true
          complete(player, b)
          save_state()
        end
      end
    end
  end
end)

server.on("enter", function(player)
  if #state.board > 0 then
    pcall(server.message, player, PREFIX .. #state.board .. " bounties are open. Type !bounty.")
  end
  push(player)
end)

server.on("sector", function(player) push_due[player.slot] = true end)

server.on("chat", function(player, text)
  if text == "!where" then
    server.message(player, "You are in sector " .. tostring(player.sector or 0) .. ".")
    return true
  end
  if text ~= "!bounty" and text ~= "!bounties" then return end
  if #state.board == 0 then
    server.message(player, PREFIX .. "The board is empty.")
    return true
  end
  local t = NOW()
  for _, b in ipairs(state.board) do
    local mine = b.players[player.uid]
    local status = mine and mine.done and "done" or ((mine and mine.n or 0) .. "/" .. b.target)
    server.message(player, string.format("%s: %s - %s, %s left. Reward: %s.", b.title, what_text(b), status,
      duration_text(b.ends - t), reward_text(b.reward)))
  end
  return true
end)

-- ---- endpoints and the dashboard page (character names only; everything player-made passes server.html) --------------------------

local function public_state()
  local t, list = NOW(), {}
  for _, b in ipairs(state.board) do
    list[#list + 1] = { id = b.id, title = b.title, text = what_text(b), region = b.region, target = b.target,
      left = math.max(0, math.floor(b.ends - t)), reward = reward_text(b.reward), hunters = hunters(b), claims = claims(b) }
  end
  local recent = {}
  for _, h in ipairs(state.history) do
    recent[#recent + 1] = { kind = h.kind, title = h.title, hero = h.hero, ago = math.max(0, math.floor(t - (h.time or t))), claims = h.claims }
  end
  local top = {}
  for i, row in ipairs(server.top("bounties", 10)) do top[i] = { rank = i, name = row.name, value = row.value } end
  return { bounties = list, recent = recent, top = top }
end

server.endpoint("state", function() return public_state() end,
  { public = true, description = "the active bounties, recent completions and the top hunters as JSON" })

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem;font-size:1.3rem;color:#e0a944}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(19rem,1fr));gap:1rem;padding:0 1.2rem 1.2rem}
section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}
ol,ul{margin:0;padding-left:1.3rem}li{margin:.15rem 0}b{color:#fff}small{color:#a39683}p{margin:.2rem 0}]]

server.page("", function()
  local h = server.html
  local data = public_state()
  local parts = {
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="30"><title>Sacred bounty board</title><style>', style,
    '</style></head><body><h1>Bounty board</h1><main>',
  }
  if #data.bounties == 0 then parts[#parts + 1] = "<section><h2>Open bounties</h2><small>nothing posted</small></section>" end
  for _, b in ipairs(data.bounties) do
    parts[#parts + 1] = "<section><h2>" .. h(b.title) .. "</h2><p>" .. h(b.text) .. "</p><p>Reward: <b>" .. h(b.reward) .. "</b></p><p><small>"
      .. h(duration_text(b.left)) .. " left, " .. b.claims .. " finished, " .. b.hunters .. " hunting</small></p></section>"
  end
  parts[#parts + 1] = "<section><h2>Best bounty hunters</h2>"
  if #data.top == 0 then parts[#parts + 1] = "<small>nobody yet</small>" else
    parts[#parts + 1] = "<ol>"
    for _, r in ipairs(data.top) do parts[#parts + 1] = "<li><b>" .. h(r.name) .. "</b> " .. r.value .. "</li>" end
    parts[#parts + 1] = "</ol>"
  end
  parts[#parts + 1] = "</section><section><h2>Recent</h2>"
  if #data.recent == 0 then parts[#parts + 1] = "<small>nothing yet</small>" else
    parts[#parts + 1] = "<ul>"
    for _, r in ipairs(data.recent) do
      if r.kind == "done" then
        parts[#parts + 1] = "<li><b>" .. h(r.hero or "?") .. "</b> finished " .. h(r.title) .. " <small>" .. h(duration_text(r.ago)) .. " ago</small></li>"
      else
        parts[#parts + 1] = "<li>" .. h(r.title) .. " ended <small>" .. h(duration_text(r.ago)) .. " ago, " .. (r.claims or 0) .. " finished</small></li>"
      end
    end
    parts[#parts + 1] = "</ul>"
  end
  parts[#parts + 1] = "</section></main></body></html>"
  return table.concat(parts)
end, { public = true, title = "Bounty board" })

