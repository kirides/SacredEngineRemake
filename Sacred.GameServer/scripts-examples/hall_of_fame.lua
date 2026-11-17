-- Hall of fame: all-time records (highest level, most creatures defeated, most deaths, longest session, most time online, the
-- mightiest creature defeated), the first hero to reach each level milestone and the first hero to defeat each kind of
-- creature, as a page and JSON endpoints. Own storage (script-data/hall_of_fame.json). Copy into the server's scripts folder.
--
--   /mods/hall_of_fame/            the page          /mods/hall_of_fame/records    JSON: the records
--   /mods/hall_of_fame/firsts      JSON: level milestones and first kills of each creature
-- "!hof" in chat shows the records to the player who asked. Records beaten by another hero are announced to everybody
-- (only above the minimums below, so a fresh server does not shout). Only character names are shown.
server.describe("Hall of fame: all-time records and the first hero to reach each level or defeat each creature, as a page and JSON endpoints.")

local milestones = { 10, 25, 50, 100, 150, 200 }              -- levels announced as "first hero to reach"
local announce_min = { level = 30, kills = 500, deaths = 50, biggest = 40 }   -- records below these are kept, not announced
local keep_first_kills = 60

local records = server.load("records", {})        -- key -> { name, uid, value, time, extra }
local level_firsts = server.load("level_firsts", {})   -- { { level, name, time }, ... }
local first_kills = server.load("first_kills", {})     -- newest first: { creature, name, time }
local seen = server.load("seen_creatures", {})         -- creature name -> true

local titles = {
  level = "Highest level", kills = "Most creatures defeated", deaths = "Most deaths", session = "Longest single session",
  online = "Most time online", biggest = "Mightiest creature defeated",
}
local order = { "level", "kills", "online", "session", "biggest", "deaths" }

local function who(player) return player.character or player.name end

-- A hero takes a record when its value is higher (the holder is kept on a tie). Returns true when the holder changed.
local function try_record(key, player, value, extra)
  local r = records[key]
  if value <= (r and r.value or 0) then return false end
  local changed = not r or r.uid ~= player.uid
  local previous = r
  records[key] = { name = who(player), uid = player.uid, value = value, time = server.time(), extra = extra }
  server.save("records", records)
  if changed and previous and value >= (announce_min[key] or math.huge) then
    server.broadcast(who(player) .. " set a new Hall of Fame record: " .. titles[key]:lower() .. " " .. math.floor(value) ..
      (extra and (" (" .. extra .. ")") or "") .. " - beating " .. previous.name .. "!")
  end
  return changed
end

local function check_levels(player)
  if not player.level or player.level <= 0 then return end
  try_record("level", player, player.level)
  for _, m in ipairs(milestones) do
    if player.level >= m then
      local taken = false
      for _, f in ipairs(level_firsts) do if f.level == m then taken = true end end
      if not taken then
        level_firsts[#level_firsts + 1] = { level = m, name = who(player), time = server.time() }
        server.save("level_firsts", level_firsts)
        server.broadcast(who(player) .. " is the first hero on this server to reach level " .. m .. "!")
      end
    end
  end
end

server.on("kill", function(player, monster)
  local kills = server.add(player, "kills")
  try_record("kills", player, kills)
  local creature = monster.name or ("creature type " .. monster.type)
  if (monster.level or 0) > 0 then try_record("biggest", player, monster.level, creature) end
  if not seen[creature] then
    seen[creature] = true
    table.insert(first_kills, 1, { creature = creature, name = who(player), time = server.time() })
    while #first_kills > keep_first_kills do table.remove(first_kills) end
    server.save("seen_creatures", seen); server.save("first_kills", first_kills)
  end
  check_levels(player)
end)

server.on("death", function(player)
  try_record("deaths", player, server.add(player, "deaths"))
end)

server.on("enter", check_levels)

server.on("leave", function(player)
  check_levels(player)
  local seconds = math.floor(player.online or 0)
  if seconds <= 0 then return end
  try_record("session", player, seconds)
  try_record("online", player, server.add(player, "online", seconds))
end)

-- ---- views -----------------------------------------------------------------------------------------------------------

local function stamp(t)   -- "2026-11-17" (UTC), from Unix seconds
  local z = math.floor(t / 86400) + 719468
  local era = math.floor(z / 146097)
  local doe = z - era * 146097
  local yoe = math.floor((doe - math.floor(doe / 1460) + math.floor(doe / 36524) - math.floor(doe / 146096)) / 365)
  local doy = doe - (365 * yoe + math.floor(yoe / 4) - math.floor(yoe / 100))
  local mp = math.floor((5 * doy + 2) / 153)
  local day = doy - math.floor((153 * mp + 2) / 5) + 1
  local month = mp < 10 and mp + 3 or mp - 9
  return string.format("%04d-%02d-%02d", yoe + era * 400 + (month <= 2 and 1 or 0), month, day)
end

local function shown(key, value)
  if key == "session" or key == "online" then
    local h, m = math.floor(value / 3600), math.floor(value / 60) % 60
    return string.format("%dh %02dm", h, m)
  end
  return tostring(math.floor(value))
end

local function record_rows()
  local rows = {}
  for _, key in ipairs(order) do
    local r = records[key]
    if r then rows[#rows + 1] = { key = key, title = titles[key], name = r.name, value = r.value, shown = shown(key, r.value), time = r.time, extra = r.extra } end
  end
  return rows
end

server.endpoint("records", function()
  local rows = {}
  for i, r in ipairs(record_rows()) do rows[i] = { record = r.key, title = r.title, hero = r.name, value = r.value, extra = r.extra, time = r.time } end
  return rows
end, { public = true, description = "the all-time records as JSON" })

server.endpoint("firsts", function()
  local levels, kills = {}, {}
  for i, f in ipairs(level_firsts) do levels[i] = { level = f.level, hero = f.name, time = f.time } end
  for i, f in ipairs(first_kills) do kills[i] = { creature = f.creature, hero = f.name, time = f.time } end
  return { levels = levels, creatures = kills }
end, { public = true, description = "first hero to reach each level milestone and to defeat each creature, as JSON" })

server.on("chat", function(player, text)
  if text ~= "!hof" then return end
  local rows = record_rows()
  if #rows == 0 then server.message(player, "The Hall of Fame is still empty.") end
  for _, r in ipairs(rows) do
    server.message(player, r.title .. ": " .. r.name .. " (" .. r.shown .. (r.extra and (", " .. r.extra) or "") .. ")")
  end
  return true
end)

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem;font-size:1.3rem;color:#e0a944}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(19rem,1fr));gap:1rem;padding:0 1.2rem 1.2rem}
section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}
ol,ul{margin:0;padding-left:1.3rem}li{margin:.15rem 0}b{color:#fff}small{color:#a39683}.rec{display:flex;justify-content:space-between;gap:1rem;margin:.25rem 0}.rec span:last-child{text-align:right}]]

server.page("", function()
  local h = server.html
  local parts = {
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="60"><title>Hall of fame</title><style>', style, "</style></head><body><h1>Hall of fame</h1><main>",
    "<section><h2>Records</h2>",
  }
  local rows = record_rows()
  if #rows == 0 then parts[#parts + 1] = "<small>no records yet</small>" end
  for _, r in ipairs(rows) do
    parts[#parts + 1] = '<div class="rec"><span>' .. h(r.title) .. '</span><span><b>' .. h(r.name) .. "</b> " .. h(r.shown) ..
      (r.extra and (" <small>" .. h(r.extra) .. "</small>") or "") .. " <small>" .. stamp(r.time) .. "</small></span></div>"
  end
  parts[#parts + 1] = "</section><section><h2>First to reach a level</h2>"
  if #level_firsts == 0 then parts[#parts + 1] = "<small>nobody yet</small>" else
    parts[#parts + 1] = "<ul>"
    for _, f in ipairs(level_firsts) do parts[#parts + 1] = "<li>level " .. f.level .. ": <b>" .. h(f.name) .. "</b> <small>" .. stamp(f.time) .. "</small></li>" end
    parts[#parts + 1] = "</ul>"
  end
  parts[#parts + 1] = "</section><section><h2>First to defeat a creature</h2>"
  if #first_kills == 0 then parts[#parts + 1] = "<small>nobody yet</small>" else
    parts[#parts + 1] = "<ul>"
    for i, f in ipairs(first_kills) do
      if i > 20 then break end
      parts[#parts + 1] = "<li>" .. h(f.creature) .. ": <b>" .. h(f.name) .. "</b> <small>" .. stamp(f.time) .. "</small></li>"
    end
    parts[#parts + 1] = "</ul>"
  end
  parts[#parts + 1] = "</section></main></body></html>"
  return table.concat(parts)
end, { public = true, title = "Hall of fame" })
