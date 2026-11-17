-- Achievements and a daily login streak, kept on the server (script-data/achievements.json).
-- Copy into the server's scripts folder to use it.
--
--   * Achievements unlock from what the server really sees: creatures defeated, distinct creature types defeated, "giants"
--     (a creature 10 or more levels above the hero), hero deaths, players defeated, hero level, time online, days visited
--     and the login streak. Each can pay a reward (gold pile, or an item and a gold pile when the server has --game-dir).
--   * Login streak: the first time a hero is in the world on a new UTC day it gets a reward that grows with the streak
--     (100 gold per day up to 1000, an item every 7th day). A missed day starts the streak over at 1.
--   * "!ach" / "!streak" in chat; a web page listing every hero's achievements:
--       /mods/achievements/              the page (with WebUi:PublicApi=true also /public/mods/achievements/)
--       /mods/achievements/players       JSON: character, level, streak, unlocked ids
--       /mods/achievements/achievements  JSON: the list of achievements
--   * Sacred Gear clients (gear/examples/achievements) get a toast for every unlock and for the streak, and a panel, over the
--     custom net channel "achievements:sync". Without that the mod works through chat messages alone.
--
-- Not covered because the script API has no event or data for it: gold earned or held, exploration (the explored map is
-- client-only), quests, items found. Level has no event either: it is read from the player when kills, joins and a 20 s timer
-- come by. Rewards are dropped next to the hero, so they are held until the hero is in the world. Unverified with a real
-- client: that the owner can pick up what drop_gold / drop_item drops (see the server README).
server.describe("Achievements (kills, creature types, levels, online time, deaths) and a daily login streak with gold/item rewards, a web page per hero and a Sacred Gear toast/panel.")

local UTC_OFFSET_HOURS = 0       -- shifts the day change (UTC by default)
local ROSTER_LIMIT = 500         -- heroes shown on the page and kept in the page's roster

-- stat: which counter of the hero decides ("ach.<stat>"); reward: gold and/or item (a generation quality 0..14);
-- shout: tell everybody in chat.
local defs = {
  { id = "kills_1",     name = "First Blood",         desc = "Defeat a creature.",                    stat = "kills",   target = 1,     reward = { gold = 50 } },
  { id = "kills_100",   name = "Monster Hunter",      desc = "Defeat 100 creatures.",                 stat = "kills",   target = 100,   reward = { gold = 250 } },
  { id = "kills_1000",  name = "Slayer",              desc = "Defeat 1,000 creatures.",               stat = "kills",   target = 1000,  reward = { gold = 1000, item = 3 }, shout = true },
  { id = "kills_5000",  name = "Exterminator",        desc = "Defeat 5,000 creatures.",               stat = "kills",   target = 5000,  reward = { gold = 3000, item = 7 }, shout = true },
  { id = "kills_20000", name = "Legend of the Wilds", desc = "Defeat 20,000 creatures.",              stat = "kills",   target = 20000, reward = { gold = 10000, item = 10 }, shout = true },
  { id = "level_10",    name = "Apprentice",          desc = "Reach hero level 10.",                  stat = "level",   target = 10,    reward = { gold = 100 } },
  { id = "level_25",    name = "Veteran",             desc = "Reach hero level 25.",                  stat = "level",   target = 25,    reward = { gold = 400 } },
  { id = "level_50",    name = "Champion",            desc = "Reach hero level 50.",                  stat = "level",   target = 50,    reward = { gold = 1500, item = 4 }, shout = true },
  { id = "level_100",   name = "Hero of Ancaria",     desc = "Reach hero level 100.",                 stat = "level",   target = 100,   reward = { gold = 5000, item = 7 }, shout = true },
  { id = "level_150",   name = "Demigod",             desc = "Reach hero level 150.",                 stat = "level",   target = 150,   reward = { gold = 10000, item = 10 }, shout = true },
  { id = "species_10",  name = "Curious",             desc = "Defeat 10 different kinds of creature.", stat = "species", target = 10,   reward = { gold = 150 } },
  { id = "species_50",  name = "Bestiary Scholar",    desc = "Defeat 50 different kinds of creature.", stat = "species", target = 50,   reward = { gold = 800, item = 3 } },
  { id = "species_150", name = "Master of Beasts",    desc = "Defeat 150 different kinds of creature.", stat = "species", target = 150, reward = { gold = 3000, item = 8 }, shout = true },
  { id = "giants_1",    name = "Giant Slayer",        desc = "Defeat a creature 10 or more levels above your hero.", stat = "giants", target = 1,  reward = { gold = 300 } },
  { id = "giants_25",   name = "Bane of Titans",      desc = "Defeat 25 such creatures.",             stat = "giants",  target = 25,    reward = { gold = 2500, item = 6 }, shout = true },
  { id = "deaths_1",    name = "A Taste of Death",    desc = "Die for the first time.",               stat = "deaths",  target = 1,     reward = { gold = 25 } },
  { id = "deaths_10",   name = "Persistent",          desc = "Die 10 times and get up again.",        stat = "deaths",  target = 10,    reward = { gold = 200 } },
  { id = "deaths_50",   name = "Phoenix",             desc = "Die 50 times and get up again.",        stat = "deaths",  target = 50,    reward = { gold = 1000 } },
  { id = "pvp_1",       name = "Duelist",             desc = "Defeat another player's hero.",         stat = "pvp",     target = 1,     reward = { gold = 200 } },
  { id = "pvp_10",      name = "Gladiator",           desc = "Defeat 10 heroes of other players.",    stat = "pvp",     target = 10,    reward = { gold = 2000, item = 5 }, shout = true },
  { id = "online_1h",   name = "Settled In",          desc = "Spend 1 hour on this server.",          stat = "online",  target = 3600,   reward = { gold = 100 } },
  { id = "online_10h",  name = "Regular",             desc = "Spend 10 hours on this server.",        stat = "online",  target = 36000,  reward = { gold = 800 } },
  { id = "online_100h", name = "Part of the Furniture", desc = "Spend 100 hours on this server.",     stat = "online",  target = 360000, reward = { gold = 6000, item = 8 }, shout = true },
  { id = "days_3",      name = "Returning Visitor",   desc = "Visit on 3 different days.",            stat = "days",    target = 3,     reward = { gold = 150 } },
  { id = "days_14",     name = "Regular Guest",       desc = "Visit on 14 different days.",           stat = "days",    target = 14,    reward = { gold = 700 } },
  { id = "days_60",     name = "Fixture",             desc = "Visit on 60 different days.",           stat = "days",    target = 60,    reward = { gold = 3000, item = 6 }, shout = true },
  { id = "streak_3",    name = "On a Roll",           desc = "Log in 3 days in a row.",               stat = "best",    target = 3,     reward = { gold = 200 } },
  { id = "streak_7",    name = "Week Warrior",        desc = "Log in 7 days in a row.",               stat = "best",    target = 7,     reward = { gold = 700, item = 4 } },
  { id = "streak_14",   name = "Fortnight",           desc = "Log in 14 days in a row.",              stat = "best",    target = 14,    reward = { gold = 2000, item = 6 }, shout = true },
  { id = "streak_30",   name = "Devoted",             desc = "Log in 30 days in a row.",              stat = "best",    target = 30,    reward = { gold = 5000, item = 9 }, shout = true },
  { id = "streak_100",  name = "Unbreakable",         desc = "Log in 100 days in a row.",             stat = "best",    target = 100,   reward = { gold = 20000, item = 12 }, shout = true },
}

local by_stat = {}
for _, d in ipairs(defs) do
  by_stat[d.stat] = by_stat[d.stat] or {}
  table.insert(by_stat[d.stat], d)
end

-- ---- helpers ----

local function stat(player, name) return tonumber(server.get(player, "ach." .. name, 0)) or 0 end

local function unlocked(player)
  local u = server.get(player, "ach.u", nil)
  if type(u) ~= "table" then return {} end
  return u
end

local function count(t) local n = 0; for _ in pairs(t) do n = n + 1 end; return n end

local function today() return math.floor((server.time() + UTC_OFFSET_HOURS * 3600) / 86400) end

local function reward_text(r)
  if not r then return nil end
  local parts = {}
  if r.gold then parts[#parts + 1] = r.gold .. " gold" end
  if r.item then parts[#parts + 1] = "an item" end
  return table.concat(parts, " and ")
end

-- The streak reward: 100 gold per day up to 1000; every 7th day also an item (quality grows with the streak).
local function streak_reward(streak)
  local r = { gold = 100 * math.min(streak, 10) }
  if streak % 7 == 0 then r.item = math.min(14, 4 + math.floor(streak / 7)) end
  return r
end

-- The channel to Sacred Gear clients: nil when the server has no custom net messages (never an error).
local channel
do
  local ok, ch = pcall(net.channel, "achievements:sync", { server_only = true })
  if ok then channel = ch end
end

local function push(player, payload)
  if not channel then return end
  local ok, supported = pcall(net.supports, player)
  if ok and supported then pcall(channel.send, channel, player, payload) end
end

-- ---- the roster for the page: character names only, kept for heroes that are offline too ----

local roster = server.load("roster", {})

local function sync_roster(player)
  local entry = {
    name = player.character, level = math.max(player.level or 0, stat(player, "level")), kills = stat(player, "kills"),
    streak = stat(player, "streak"), best = stat(player, "best"), days = stat(player, "days"),
    u = unlocked(player), seen = math.floor(server.time()),
  }
  entry.count = count(entry.u)
  roster[player.uid] = entry
end

local function save_roster()
  if count(roster) > ROSTER_LIMIT then   -- drop the heroes seen longest ago
    local ids = {}
    for id in pairs(roster) do ids[#ids + 1] = id end
    table.sort(ids, function(a, b) return (roster[a].seen or 0) > (roster[b].seen or 0) end)
    for i = ROSTER_LIMIT + 1, #ids do roster[ids[i]] = nil end
  end
  server.save("roster", roster)
end

-- ---- rewards: dropped next to the hero, so held until the hero is in the world ----

local function queue_reward(player, reward, why)
  if not reward then return end
  local pending = server.get(player, "ach.pending", nil)
  if type(pending) ~= "table" then pending = {} end
  if reward.gold and reward.gold > 0 then pending[#pending + 1] = { gold = reward.gold, why = why } end
  if reward.item then pending[#pending + 1] = { item = reward.item, fallback = 1000, why = why } end
  server.set(player, "ach.pending", pending)
end

local function flush(player)
  if not player.in_game then return end
  local pending = server.get(player, "ach.pending", nil)
  if type(pending) ~= "table" or #pending == 0 then return end
  local rest = {}
  for _, r in ipairs(pending) do
    local done = false
    if r.gold then
      local ok, pile = pcall(server.drop_gold, player, r.gold)
      done = ok and pile ~= nil
    elseif r.item then
      local ok, item = pcall(server.drop_item, player, { quality = r.item })
      if ok and item then done = true
      else   -- no item possible (no --game-dir, ...): the gold instead
        local ok2, pile = pcall(server.drop_gold, player, r.fallback or 1000)
        done = ok2 and pile ~= nil
      end
    else done = true end
    if not done then
      r.tries = (r.tries or 0) + 1
      if r.tries < 8 then rest[#rest + 1] = r else server.log("achievements: gave up on a reward for " .. player.character .. " (" .. tostring(r.why) .. ")") end
    end
  end
  server.set(player, "ach.pending", #rest > 0 and rest or nil)
end

-- ---- unlocking ----

local function state(player)
  local u = unlocked(player)
  local list = {}
  for i, d in ipairs(defs) do
    list[i] = { id = d.id, name = d.name, desc = d.desc, have = math.min(stat(player, d.stat), d.target), need = d.target,
      done = u[d.id], reward = reward_text(d.reward) }
  end
  local streak = stat(player, "streak")
  local last, day = stat(player, "last_day"), today()
  local claimed = last == day
  -- the reward of the next login: tomorrow's when today's is claimed, else today's (the streak goes on only after yesterday)
  local upcoming = (claimed or last == day - 1) and streak + 1 or 1
  return { op = "state", ach = list, streak = streak, best = stat(player, "best"), days = stat(player, "days"), claimed = claimed,
    next = reward_text(streak_reward(upcoming)) }
end

local function unlock(player, d)
  local u = unlocked(player)
  if u[d.id] then return end
  u[d.id] = math.floor(server.time())
  server.set(player, "ach.u", u)
  local text = "Achievement unlocked: " .. d.name .. " - " .. d.desc
  local reward = reward_text(d.reward)
  server.message(player, reward and (text .. " Reward: " .. reward .. ".") or text)
  if d.shout then server.broadcast(player.character .. " earned the achievement \"" .. d.name .. "\"!") end
  queue_reward(player, d.reward, d.name)
  sync_roster(player)
  save_roster()
  push(player, { op = "unlock", id = d.id, name = d.name, desc = d.desc, reward = reward })
  push(player, state(player))
end

local function check(player, which)
  local value = stat(player, which)
  for _, d in ipairs(by_stat[which] or {}) do
    if value >= d.target then unlock(player, d) end
  end
end

local function note_level(player)
  local level = player.level or 0
  if level > stat(player, "level") then
    server.set(player, "ach.level", level)
    check(player, "level")
  end
end

-- ---- the daily streak ----

local function check_login(player)
  if not player.in_game then return end
  local day = today()
  local last = stat(player, "last_day")
  if last >= day then return end
  local streak = (last == day - 1) and (stat(player, "streak") + 1) or 1
  server.set(player, "ach.last_day", day)
  server.set(player, "ach.streak", streak)
  if streak > stat(player, "best") then server.set(player, "ach.best", streak) end
  server.add(player, "ach.days")
  local reward = streak_reward(streak)
  server.message(player, "Daily login: day " .. streak .. " of your streak. Reward: " .. reward_text(reward) .. ".")
  queue_reward(player, reward, "login streak day " .. streak)
  check(player, "days")
  check(player, "best")
  sync_roster(player)
  save_roster()
  push(player, { op = "streak", streak = streak, best = stat(player, "best"), reward = reward_text(reward) })
  push(player, state(player))
end

local function tend(player)
  note_level(player)
  check_login(player)
  flush(player)
end

-- ---- events ----

server.on("enter", tend)
server.on("join", function(player) note_level(player) end)
server.on("leave", function(player)
  note_level(player)
  sync_roster(player)
  save_roster()
end)

server.on("kill", function(player, monster)
  server.add(player, "ach.kills")
  check(player, "kills")
  if monster.type and monster.type > 0 then
    local seen = server.get(player, "ach.seen", nil)
    if type(seen) ~= "table" then seen = {} end
    local key = tostring(monster.type)
    if not seen[key] then
      seen[key] = true
      server.set(player, "ach.seen", seen)
      server.set(player, "ach.species", count(seen))
      check(player, "species")
    end
  end
  if monster.level and monster.level > 0 and (player.level or 0) > 0 and monster.level >= player.level + 10 then
    server.add(player, "ach.giants")
    check(player, "giants")
  end
  note_level(player)
  flush(player)
end)

server.on("death", function(player, by)
  server.add(player, "ach.deaths")
  check(player, "deaths")
  if by and by.player then
    server.add(by.player, "ach.pvp")
    check(by.player, "pvp")
  end
end)

-- Online time (a crash loses at most a minute), level changes, a new day for heroes who stay past midnight, held rewards.
server.every(60, function()
  for _, player in ipairs(server.players()) do
    if player.in_game then
      server.add(player, "ach.online", 60)
      check(player, "online")
    end
  end
end)
server.every(20, function()
  for _, player in ipairs(server.players()) do tend(player) end
end)

-- ---- chat ----

server.on("chat", function(player, text)
  if text == "!ach" or text == "!achievements" then
    local u = unlocked(player)
    local mine = {}
    for _, d in ipairs(defs) do if u[d.id] then mine[#mine + 1] = d end end
    server.message(player, "Achievements: " .. #mine .. " of " .. #defs .. " unlocked.")
    table.sort(mine, function(a, b) return u[a.id] > u[b.id] end)
    for i = 1, math.min(3, #mine) do server.message(player, "  " .. mine[i].name .. " - " .. mine[i].desc) end
    for _, d in ipairs(defs) do
      if not u[d.id] then
        server.message(player, "Next: " .. d.name .. " - " .. d.desc .. " (" .. math.floor(stat(player, d.stat)) .. "/" .. d.target .. ")")
        break
      end
    end
    return true
  elseif text == "!streak" then
    local s = state(player)
    server.message(player, "Login streak: " .. s.streak .. " day(s), best " .. s.best .. ", " .. s.days .. " day(s) visited. "
      .. (s.claimed and "Today's reward is claimed. Tomorrow: " or "Today's reward: ") .. s.next .. ".")
    return true
  end
end)

-- ---- Sacred Gear clients ----

if channel then
  channel:on(function(player, payload)
    if type(payload) == "table" and payload.op == "get" then push(player, state(player)) end
  end)
end

-- ---- endpoints and the page: every name passes through server.html ----

local function board()
  for _, player in ipairs(server.players()) do sync_roster(player) end   -- online heroes are current (not saved here)
  local rows = {}
  for _, entry in pairs(roster) do rows[#rows + 1] = entry end
  table.sort(rows, function(a, b)
    if (a.count or 0) ~= (b.count or 0) then return (a.count or 0) > (b.count or 0) end
    return tostring(a.name) < tostring(b.name)
  end)
  return rows
end

server.endpoint("players", function()
  local result = {}
  for i, entry in ipairs(board()) do
    local ids = {}
    for id in pairs(entry.u or {}) do ids[#ids + 1] = id end
    table.sort(ids)
    result[i] = { character = entry.name, level = entry.level, kills = entry.kills, streak = entry.streak, best_streak = entry.best,
      days = entry.days, count = entry.count or #ids, achievements = ids }
  end
  return result
end, { public = true, description = "every hero's achievements as JSON (character names only)" })

server.endpoint("achievements", function()
  local result = {}
  for i, d in ipairs(defs) do result[i] = { id = d.id, name = d.name, description = d.desc, reward = reward_text(d.reward) } end
  return result
end, { public = true, description = "the list of achievements as JSON" })

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem;font-size:1.3rem;color:#e0a944}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(22rem,1fr));gap:1rem;padding:0 1.2rem 1.2rem}
section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0;font-size:1.05rem;color:#f2c36b}
small{color:#a39683}.chips{margin-top:.5rem;display:flex;flex-wrap:wrap;gap:.3rem}
.chip{border-radius:999px;padding:.1rem .6rem;font-size:.8rem;border:1px solid #3b3025;color:#6f6454}
.chip.on{background:#4a3814;border-color:#e0a944;color:#ffe2a0}p.note{padding:0 1.2rem;color:#a39683;margin:0 0 .8rem}]]

server.page("", function()
  local h = server.html
  local parts = {
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="60"><title>Sacred achievements</title><style>', style, '</style></head><body><h1>Sacred achievements</h1>',
    '<p class="note">', tostring(#defs), ' achievements. Hover a badge for what it asks; the streak counts days in a row on which the hero entered the world (UTC).</p><main>',
  }
  local rows = board()
  if #rows == 0 then parts[#parts + 1] = "<section><small>nobody yet</small></section>" end
  for _, entry in ipairs(rows) do
    local u = entry.u or {}
    parts[#parts + 1] = "<section><h2>" .. h(entry.name) .. "</h2><small>level " .. tostring(math.floor(entry.level or 0)) .. ", "
      .. tostring(math.floor(entry.kills or 0)) .. " kills, " .. (entry.count or 0) .. "/" .. #defs .. " achievements, streak "
      .. tostring(math.floor(entry.streak or 0)) .. " (best " .. tostring(math.floor(entry.best or 0)) .. ")</small><div class=\"chips\">"
    for _, d in ipairs(defs) do
      parts[#parts + 1] = '<span class="chip' .. (u[d.id] and " on" or "") .. '" title="' .. h(d.desc) .. '">' .. h(d.name) .. "</span>"
    end
    parts[#parts + 1] = "</div></section>"
  end
  parts[#parts + 1] = "</main></body></html>"
  return table.concat(parts)
end, { public = true, title = "Achievements" })
