-- Graveyard: recent hero deaths with the name of the killer (creature or hero), the deadliest creatures and the heroes who died
-- most, as a page, JSON/CSV endpoints, an optional chat notice and a message to Sacred Gear clients (gear/examples/serverfeed).
-- Own storage (script-data/graveyard.json). Copy into the server's scripts folder to use it.
--
--   /mods/graveyard/        the page (tombstones, refreshes every 30 s)     /mods/graveyard/recent?n=20   JSON
--   /mods/graveyard/deadliest   JSON: creatures by heroes killed          /mods/graveyard/recent.csv   CSV
-- "!rip" in chat shows the last three deaths. Killer names are the game's own texts, so the server needs --game-dir for them
-- (otherwise "creature type N"). Only character names are shown. A hero death is reported once (several reports within 2 s count once).
server.describe("Graveyard: recent hero deaths with killer names, deadliest creatures and most deaths, as a page, JSON/CSV and a Gear client message.")

local keep = 200
local announce = true       -- "Alice was slain by Orc Warrior" to everybody

local deaths = server.load("deaths", {})         -- newest first: { time, name, level, by, kind, by_level }
local killers = server.load("killers", {})      -- killer name -> heroes killed

local channel = net and net.channel and net.channel("graveyard:feed", { server_only = true })

server.on("death", function(player, by)
  local who = player.character or player.name
  local killer, kind, killer_level
  if by.player then killer, kind = by.player.character or by.player.name, "hero"
  elseif by.name or by.type then killer, kind, killer_level = by.name or ("creature type " .. by.type), "creature", by.level
  else killer, kind = "unknown", "unknown" end
  local entry = { time = server.time(), name = who, level = player.level or 0, by = killer, kind = kind, by_level = killer_level }
  table.insert(deaths, 1, entry)
  while #deaths > keep do table.remove(deaths) end
  server.save("deaths", deaths)
  server.add(player, "deaths")
  if kind ~= "unknown" then
    killers[killer] = (killers[killer] or 0) + 1
    server.save("killers", killers)
  end
  if announce then server.broadcast(who .. (kind == "unknown" and " has died." or (" was slain by " .. killer .. "."))) end
  if channel then channel:send("all", { kind = "death", name = who, level = entry.level, by = killer, killer_kind = kind, time = entry.time }) end
end)

if channel then
  channel:on(function(player)
    local list = {}
    for i = 1, math.min(5, #deaths) do local d = deaths[i]; list[i] = { name = d.name, level = d.level, by = d.by, killer_kind = d.kind, time = d.time } end
    channel:send(player, { kind = "recent", deaths = list })
  end)
end

server.on("chat", function(player, text)
  if text ~= "!rip" then return end
  if #deaths == 0 then server.message(player, "Nobody has died yet.") end
  for i = 1, math.min(3, #deaths) do
    local d = deaths[i]
    server.message(player, d.name .. " (level " .. d.level .. ") - " .. (d.kind == "unknown" and "cause unknown" or ("slain by " .. d.by)))
  end
  return true
end)

local function recent(n)
  local list = {}
  for i = 1, math.min(n, #deaths) do
    local d = deaths[i]
    list[i] = { time = d.time, hero = d.name, level = d.level, killer = d.by, killer_kind = d.kind, killer_level = d.by_level }
  end
  return list
end

local function deadliest()
  local list = {}
  for name, count in pairs(killers) do list[#list + 1] = { killer = name, kills = count } end
  table.sort(list, function(a, b) if a.kills ~= b.kills then return a.kills > b.kills end return a.killer < b.killer end)
  return list
end

server.endpoint("recent", function(request) return recent(math.min(tonumber(request.query.n) or 20, keep)) end,
  { public = true, description = "the latest hero deaths as JSON: ?n=20" })
server.endpoint("recent.csv", function()
  return server.csv(recent(keep), { "time", "hero", "level", "killer", "killer_kind", "killer_level" }), "text/csv"
end, { public = true, description = "the latest hero deaths as CSV" })
server.endpoint("deadliest", function() return deadliest() end, { public = true, description = "killers by heroes killed, as JSON" })

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
.side{display:grid;grid-template-columns:repeat(auto-fill,minmax(16rem,1fr));gap:1rem;padding:0 1.2rem 1rem}
.stones{display:grid;grid-template-columns:repeat(auto-fill,minmax(12rem,1fr));gap:.8rem;padding:0 1.2rem 1.2rem}
.stone{background:#2b2a2c;border:1px solid #4a4a4e;border-radius:40% 40% 6px 6px/28% 28% 6px 6px;padding:1rem .8rem .7rem;text-align:center}
.stone b{display:block;color:#fff;font-size:1.05rem}.stone span{display:block;color:#cfc8bb}.stone small{color:#908a7e}
section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}
ol{margin:0;padding-left:1.3rem}small{color:#a39683}.rip{color:#8a8478;letter-spacing:.2em;font-size:.75rem}]]

server.page("", function()
  local h = server.html
  local parts = {
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="30"><title>Graveyard</title><style>', style, "</style></head><body><h1>Graveyard</h1><div class=\"side\">",
    "<section><h2>Deadliest killers</h2>",
  }
  local top = deadliest()
  if #top == 0 then parts[#parts + 1] = "<small>nobody has died yet</small>" else
    parts[#parts + 1] = "<ol>"
    for i, k in ipairs(top) do
      if i > 8 then break end
      parts[#parts + 1] = "<li>" .. h(k.killer) .. " <small>" .. k.kills .. " heroes</small></li>"
    end
    parts[#parts + 1] = "</ol>"
  end
  parts[#parts + 1] = "</section><section><h2>Most deaths</h2>"
  local most = server.top("deaths", 8)
  if #most == 0 then parts[#parts + 1] = "<small>nobody yet</small>" else
    parts[#parts + 1] = "<ol>"
    for _, r in ipairs(most) do parts[#parts + 1] = "<li>" .. h(r.name) .. " <small>" .. math.floor(r.value) .. "</small></li>" end
    parts[#parts + 1] = "</ol>"
  end
  parts[#parts + 1] = '</section></div><div class="stones">'
  for i, d in ipairs(deaths) do
    if i > 48 then break end
    parts[#parts + 1] = '<div class="stone"><div class="rip">R.I.P.</div><b>' .. h(d.name) .. "</b><span>level " .. d.level .. "</span><span>" ..
      (d.kind == "unknown" and "cause unknown" or ("slain by " .. h(d.by) .. (d.by_level and d.by_level > 0 and (" (level " .. d.by_level .. ")") or ""))) ..
      "</span><small>" .. stamp(d.time) .. " UTC</small></div>"
  end
  if #deaths == 0 then parts[#parts + 1] = "<small>The graveyard is empty.</small>" end
  parts[#parts + 1] = "</div></body></html>"
  return table.concat(parts)
end, { public = true, title = "Graveyard" })
