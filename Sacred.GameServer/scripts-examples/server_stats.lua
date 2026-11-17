-- Server stats dashboard: who is online, the peak, uptime, totals (joins, heroes, kills, deaths, chat lines, hours played), the
-- last 24 hours of players and activity, and the most defeated creatures, as a page and a JSON endpoint. Own storage
-- (script-data/server_stats.json). Copy into the server's scripts folder to use it.
--
--   /mods/server_stats/          the dashboard (server-rendered, refreshes every 30 s, charts are inline SVG: no script)
--   /mods/server_stats/stats     the same numbers as JSON
-- With WebUi:PublicApi=true both also work under /public/mods/server_stats/... for anybody. Only character names are shown.
server.describe("Server stats: players online, peak, uptime, totals and 24 hour activity as a dashboard page and a JSON endpoint.")

local SAMPLE_SECONDS = 300      -- one players-online sample every 5 minutes ...
local KEEP_SAMPLES = 288        -- ... for 24 hours
local KEEP_HOURS = 48           -- hourly activity buckets kept (24 h are shown)
local TOP_CREATURES = 8

local loaded = server.time()    -- "uptime" is the time since this mod (re)loaded: the server's start, or the last script change
local state = server.load("state", nil) or { since = loaded, peak = 0, peak_time = 0 }
local samples = server.load("samples", {})      -- { { t, n }, ... } oldest first
local hours = server.load("hours", {})         -- { { h = hour number, kills, deaths, joins, chat }, ... } oldest first
local kinds = server.load("kinds", {})         -- creature name -> defeats
local dirty = false

local function total(name) return server.get(nil, "total." .. name, 0) end

local function bump(field)
  local h = math.floor(server.time() / 3600)
  local last = hours[#hours]
  if not last or last.h ~= h then
    last = { h = h, kills = 0, deaths = 0, joins = 0, chat = 0 }
    hours[#hours + 1] = last
    while #hours > KEEP_HOURS do table.remove(hours, 1) end
  end
  last[field] = (last[field] or 0) + 1
  dirty = true
end

local function note_peak()
  local n = #server.players()
  if n > state.peak then
    state.peak, state.peak_time = n, server.time()
    server.save("state", state)
  end
end

server.on("join", function(player)
  server.add(nil, "total.joins")
  if server.get(player, "seen", 0) == 0 then
    server.set(player, "seen", 1)
    server.add(nil, "total.heroes")
  end
  bump("joins")
  note_peak()
end)

server.on("kill", function(player, monster)
  server.add(nil, "total.kills")
  local creature = monster.name or ("creature type " .. monster.type)
  kinds[creature] = (kinds[creature] or 0) + 1
  bump("kills")
end)

server.on("death", function() server.add(nil, "total.deaths"); bump("deaths") end)
server.on("chat", function() server.add(nil, "total.chat"); bump("chat") end)   -- returns nothing: the line is relayed as usual

server.every(60, function()
  local n = #server.players()
  if n > 0 then server.add(nil, "total.online", 60 * n) end
end)

server.every(SAMPLE_SECONDS, function()
  samples[#samples + 1] = { t = math.floor(server.time()), n = #server.players() }
  while #samples > KEEP_SAMPLES do table.remove(samples, 1) end
  dirty = true
end)

server.every(30, function()
  if not dirty then return end
  dirty = false
  server.save("samples", samples); server.save("hours", hours); server.save("kinds", kinds)
end)

-- ---- numbers -------------------------------------------------------------------------------------------------------

local function duration(seconds)
  seconds = math.floor(seconds)
  local d, h, m = math.floor(seconds / 86400), math.floor(seconds / 3600) % 24, math.floor(seconds / 60) % 60
  if d > 0 then return string.format("%dd %dh", d, h) end
  if h > 0 then return string.format("%dh %dm", h, m) end
  return string.format("%dm", m)
end

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
  local year = yoe + era * 400 + (month <= 2 and 1 or 0)
  local secs = t % 86400
  return string.format("%04d-%02d-%02d %02d:%02d", year, month, day, math.floor(secs / 3600), math.floor(secs / 60) % 60)
end

local function last_24h()
  local now_h, result = math.floor(server.time() / 3600), {}
  for i = 23, 0, -1 do
    local want, found = now_h - i, nil
    for _, b in ipairs(hours) do if b.h == want then found = b end end
    result[#result + 1] = { hour = want, kills = found and found.kills or 0, deaths = found and found.deaths or 0,
      joins = found and found.joins or 0, chat = found and found.chat or 0 }
  end
  return result
end

local function creatures()
  local list = {}
  for name, count in pairs(kinds) do list[#list + 1] = { creature = name, kills = count } end
  table.sort(list, function(a, b) if a.kills ~= b.kills then return a.kills > b.kills end return a.creature < b.creature end)
  while #list > TOP_CREATURES do table.remove(list) end
  return list
end

local function snapshot()
  local names = {}
  for _, p in ipairs(server.players()) do names[#names + 1] = p.character or p.name end
  table.sort(names)
  local recent = {}
  for _, s in ipairs(samples) do recent[#recent + 1] = { time = s.t, players = s.n } end
  return {
    online = #names, players = names, peak = state.peak, peak_time = state.peak_time,
    uptime = math.floor(server.time() - loaded), tracking_since = state.since,
    totals = { joins = total("joins"), heroes = total("heroes"), kills = total("kills"), deaths = total("deaths"),
      chat = total("chat"), hours_played = math.floor(total("online") / 36) / 100 },
    activity = last_24h(), samples = recent, creatures = creatures(),
  }
end

server.endpoint("stats", function() return snapshot() end, { public = true, description = "players online, peak, uptime, totals, 24 h activity as JSON" })

-- ---- the page --------------------------------------------------------------------------------------------------------

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem;font-size:1.3rem;color:#e0a944}
.tiles,.cards{display:grid;gap:1rem;padding:0 1.2rem 1rem}.tiles{grid-template-columns:repeat(auto-fill,minmax(9rem,1fr))}.cards{grid-template-columns:repeat(auto-fill,minmax(20rem,1fr))}
.tile,section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.7rem 1rem}.tile b{display:block;font-size:1.5rem;color:#fff}.tile small,small{color:#a39683}
h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}ol,ul{margin:0;padding-left:1.3rem}svg{width:100%;height:auto;display:block}.line{fill:none;stroke:#e0a944;stroke-width:2}.bar{fill:#c4753a}.area{fill:#e0a94433}.axis{stroke:#3b3025}]]

local function tile(label, value) return '<div class="tile"><small>' .. server.html(label) .. "</small><b>" .. server.html(tostring(value)) .. "</b></div>" end

local function line_chart(values, w, h)
  local max = 1
  for _, v in ipairs(values) do if v > max then max = v end end
  if #values < 2 then return '<small>collecting data (one sample every 5 minutes)</small>' end
  local pts = {}
  for i, v in ipairs(values) do
    pts[#pts + 1] = string.format("%.1f,%.1f", (i - 1) / (#values - 1) * w, h - 4 - v / max * (h - 8))
  end
  local path = table.concat(pts, " ")
  return string.format('<svg viewBox="0 0 %d %d" role="img" aria-label="players online over 24 hours"><line class="axis" x1="0" y1="%d" x2="%d" y2="%d"/>' ..
    '<polygon class="area" points="0,%d %s %d,%d"/><polyline class="line" points="%s"/><text x="4" y="12" fill="#a39683" font-size="11">max %d</text></svg>',
    w, h, h, w, h, h, path, w, h, path, max)
end

local function bar_chart(buckets, field, w, h)
  local max = 1
  for _, b in ipairs(buckets) do if b[field] > max then max = b[field] end end
  local bw, parts = w / #buckets, {}
  parts[1] = string.format('<svg viewBox="0 0 %d %d" role="img" aria-label="%s per hour">', w, h, server.html(field))
  for i, b in ipairs(buckets) do
    local bh = b[field] / max * (h - 16)
    parts[#parts + 1] = string.format('<rect class="bar" x="%.1f" y="%.1f" width="%.1f" height="%.1f"><title>%s UTC: %d</title></rect>',
      (i - 1) * bw + 1, h - bh, bw - 2, bh, stamp(b.hour * 3600), b[field])
  end
  parts[#parts + 1] = string.format('<text x="4" y="12" fill="#a39683" font-size="11">max %d / hour</text></svg>', max)
  return table.concat(parts)
end

server.page("", function()
  local s, h = snapshot(), server.html
  local counts = {}
  for _, sample in ipairs(s.samples) do counts[#counts + 1] = sample.players end
  local who = {}
  for _, name in ipairs(s.players) do who[#who + 1] = "<li>" .. h(name) .. "</li>" end
  local top = {}
  for _, c in ipairs(s.creatures) do top[#top + 1] = "<li>" .. h(c.creature) .. " <small>" .. c.kills .. "</small></li>" end
  return table.concat({
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="30"><title>Server stats</title><style>', style, "</style></head><body><h1>Server stats</h1>",
    '<div class="tiles">', tile("Online now", s.online), tile("Peak", s.peak), tile("Uptime", duration(s.uptime)),
    tile("Heroes seen", s.totals.heroes), tile("Joins", s.totals.joins), tile("Creatures defeated", s.totals.kills),
    tile("Hero deaths", s.totals.deaths), tile("Chat lines", s.totals.chat), tile("Hours played", s.totals.hours_played), "</div>",
    '<div class="cards">',
    "<section><h2>Online now</h2>", #who > 0 and ("<ul>" .. table.concat(who) .. "</ul>") or "<small>nobody</small>", "</section>",
    "<section><h2>Players, last 24 hours</h2>", line_chart(counts, 400, 110), "</section>",
    "<section><h2>Creatures defeated per hour</h2>", bar_chart(s.activity, "kills", 400, 110), "</section>",
    "<section><h2>Hero deaths per hour</h2>", bar_chart(s.activity, "deaths", 400, 110), "</section>",
    "<section><h2>Most defeated creatures</h2>", #top > 0 and ("<ol>" .. table.concat(top) .. "</ol>") or "<small>nothing yet</small>", "</section>",
    "</div>",
    "<p style='padding:0 1.2rem'><small>Peak ", s.peak > 0 and (s.peak .. " on " .. h(stamp(s.peak_time)) .. " UTC") or "none yet",
    ". Counting since ", h(stamp(s.tracking_since)), " UTC.</small></p>",
    "</body></html>",
  })
end, { public = true, title = "Server stats" })
