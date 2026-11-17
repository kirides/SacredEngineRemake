-- Rare-drop feed: a live feed of rare and unique items that appear on the ground, with the creature that dropped them and the
-- hero who defeated it, as a page, JSON/CSV endpoints, a chat announcement and a message to Sacred Gear clients
-- (gear/examples/serverfeed shows it on screen). Own storage (script-data/rare_drops.json). Copy into the server's scripts folder.
--
--   /mods/rare_drops/        the feed page (refreshes every 15 s)     /mods/rare_drops/feed?n=20   JSON    /mods/rare_drops/feed.csv   CSV
--
-- The script API has no "item dropped" event of its own, so the server got one: server.on("drop", function(item, reporter)), fired
-- when a new item object lies on the ground (item = { ref, type, level, rarity, sockets, price, name, sector, x, y }). The game
-- sends no message that says who dropped it: the item is reported by the client that owns the sector. This mod therefore ties a
-- drop to the creature defeated in the last `kill_window` seconds (the most recent one) and ignores items without a kill
-- (chests, items already lying around when a client enters a sector) while `require_kill` is on.
-- Rarity is the item's tier 0..15 as Sacred Gear shows it: 0-4 normal, 5-11 magic, 12-14 rare, 15 unique.
server.describe("Live feed of rare and unique drops (item, tier, creature, hero) as a page, JSON/CSV, chat announcements and a Gear client message.")

local min_rarity = 12        -- announce items of this tier and above (12: rare and unique; 15: only unique)
local require_kill = true   -- only drops that follow a kill within kill_window seconds
local kill_window = 5
local announce = true       -- chat line to everybody
local keep = 100            -- drops kept

local feed = server.load("feed", {})            -- newest first
local kills = {}                                -- recent kills, newest last: { t, hero, creature, level }
local seen = {}                                 -- references already handled
local seen_order = {}

local function tier_name(r)
  if r >= 15 then return "unique" elseif r >= 12 then return "rare" elseif r >= 5 then return "magic" end
  return "normal"
end

server.on("kill", function(player, monster)
  kills[#kills + 1] = { t = server.time(), hero = player.character or player.name,
    creature = monster.name or ("creature type " .. monster.type), level = monster.level }
  while #kills > 20 do table.remove(kills, 1) end
end)

local function recent_kill()
  local now = server.time()
  for i = #kills, 1, -1 do
    if now - kills[i].t <= kill_window then return kills[i] end
  end
end

local channel = net and net.channel and net.channel("rare_drops:feed", { server_only = true })
local function push(payload)
  if channel then channel:send("all", payload) end
end

local function brief(d) return { item = d.item, tier = d.tier, rarity = d.rarity, by = d.by, from = d.from, time = d.time } end

server.on("drop", function(item, reporter)
  if (item.rarity or 0) < min_rarity then return end
  if seen[item.ref] then return end
  seen[item.ref] = true
  seen_order[#seen_order + 1] = item.ref
  if #seen_order > 500 then seen[table.remove(seen_order, 1)] = nil end

  local kill = recent_kill()
  if require_kill and not kill then return end
  local entry = {
    time = server.time(), item = item.name or server.item_name(item.type) or ("item type " .. item.type), type = item.type,
    level = item.level, rarity = item.rarity, tier = tier_name(item.rarity), sockets = item.sockets,
    by = kill and kill.hero or nil, from = kill and kill.creature or nil, from_level = kill and kill.level or nil,
  }
  table.insert(feed, 1, entry)
  while #feed > keep do table.remove(feed) end
  server.save("feed", feed)
  if kill then
    for _, p in ipairs(server.players()) do
      if (p.character or p.name) == kill.hero then server.add(p, "rare") end
    end
  end
  if announce then
    server.broadcast((entry.by and (entry.by .. " found a ") or "A ") .. entry.tier .. " " .. entry.item ..
      (entry.from and (", dropped by " .. entry.from) or "") .. "!")
  end
  push({ kind = "drop", drop = brief(entry) })
end)

if channel then
  -- A client asks for the latest drops when it connects.
  channel:on(function(player, payload)
    local list = {}
    for i = 1, math.min(5, #feed) do list[i] = brief(feed[i]) end
    channel:send(player, { kind = "recent", drops = list })
  end)
end

-- ---- endpoints ---------------------------------------------------------------------------------------------------------

local function rows(n)
  local list = {}
  for i = 1, math.min(n, #feed) do
    local d = feed[i]
    list[i] = { time = d.time, item = d.item, tier = d.tier, rarity = d.rarity, level = d.level, sockets = d.sockets, hero = d.by, creature = d.from }
  end
  return list
end

server.endpoint("feed", function(request) return rows(math.min(tonumber(request.query.n) or 20, keep)) end,
  { public = true, description = "the latest rare drops as JSON: ?n=20" })

server.endpoint("feed.csv", function()
  return server.csv(rows(keep), { "time", "item", "tier", "rarity", "level", "sockets", "hero", "creature" }), "text/csv"
end, { public = true, description = "the rare drops as CSV" })

-- ---- the page ------------------------------------------------------------------------------------------------------------

local function stamp(t)   -- "2026-11-17 09:30" (UTC), from Unix seconds
  t = math.floor(t)
  local z = math.floor(t / 86400) + 719468
  local era = math.floor(z / 146097)
  local doe = z - era * 146097
  local yoe = math.floor((doe - math.floor(doe / 1460) + math.floor(doe / 36524) - math.floor(doe / 146096)) / 365)
  local doy = doe - (365 * yoe + math.floor(yoe / 4) - math.floor(yoe / 100))
  local mp = math.floor((5 * doy + 2) / 153)
  local day = doy - math.floor((153 * mp + 2) / 5) + 1
  local month = mp < 10 and mp + 3 or mp - 9
  local secs = t % 86400
  return string.format("%04d-%02d-%02d %02d:%02d", yoe + era * 400 + (month <= 2 and 1 or 0), month, day, math.floor(secs / 3600), math.floor(secs / 60) % 60)
end

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem;font-size:1.3rem;color:#e0a944}
main{display:grid;grid-template-columns:2fr 1fr;gap:1rem;padding:0 1.2rem 1.2rem}@media(max-width:700px){main{grid-template-columns:1fr}}
section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}
ol{margin:0;padding-left:1.3rem}.drop{margin:.4rem 0;padding:.4rem .6rem;border-left:4px solid #888;background:#1c1610}
.unique{border-color:#e07a2f}.rare{border-color:#e0c44a}.magic{border-color:#4a8fe0}b{color:#fff}small{color:#a39683}]]

server.page("", function()
  local h = server.html
  local parts = {
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="15"><title>Rare drops</title><style>', style, "</style></head><body><h1>Rare drops</h1><main><section><h2>Latest</h2>",
  }
  if #feed == 0 then parts[#parts + 1] = "<small>nothing yet</small>" end
  for i, d in ipairs(feed) do
    if i > 40 then break end
    local cls = (d.tier == "unique" or d.tier == "rare" or d.tier == "magic") and d.tier or ""
    parts[#parts + 1] = '<div class="drop ' .. cls .. '"><b>' .. h(d.item) .. "</b> <small>" .. h(d.tier) .. (d.level and d.level > 0 and (", level " .. d.level) or "") ..
      ((d.sockets or 0) > 0 and (", " .. d.sockets .. " socket(s)") or "") .. "</small><br><small>" ..
      (d.by and ("found by " .. h(d.by)) or "found") .. (d.from and (" - dropped by " .. h(d.from)) or "") .. ", " .. stamp(d.time) .. " UTC</small></div>"
  end
  parts[#parts + 1] = "</section><section><h2>Luckiest heroes</h2>"
  local top = server.top("rare", 10)
  if #top == 0 then parts[#parts + 1] = "<small>nobody yet</small>" else
    parts[#parts + 1] = "<ol>"
    for _, r in ipairs(top) do parts[#parts + 1] = "<li><b>" .. h(r.name) .. "</b> " .. math.floor(r.value) .. "</li>" end
    parts[#parts + 1] = "</ol>"
  end
  parts[#parts + 1] = "</section></main></body></html>"
  return table.concat(parts)
end, { public = true, title = "Rare drop feed" })
