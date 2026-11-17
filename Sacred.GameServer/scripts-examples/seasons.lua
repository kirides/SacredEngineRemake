-- Seasonal ladders, on top of the pattern of ladders.lua: the same kind of boards (creatures defeated, levels gained, time online)
-- but per season. When a season ends (after `length_days`, or by hand) its top heroes are archived and a fresh season starts
-- from zero. Own storage (script-data/seasons.json). Copy into the server's scripts folder to use it.
--
--   /mods/seasons/             the page: this season's standings, the time left and the archive of past seasons
--   /mods/seasons/current      JSON: this season          /mods/seasons/archive   JSON: past seasons, newest first
--   /mods/seasons/end-season   ADMIN only (needs the web password): ends the season now
-- "!season" in chat tells the player the season, the time left and its place. Only character names are shown.
-- Per-hero numbers of a season live under the keys "s<number>.kills" etc.; old seasons' keys stay in the mod's file (small).
server.describe("Seasonal ladders: kills, levels gained and time online per season; seasons end on a timer, are archived and restart from zero.")

local length_days = 30      -- 0: a season ends only by hand (/mods/seasons/end-season)
local keep_archive = 24     -- past seasons kept
local top_n = 10

local boards = {
  { id = "kills", title = "Most creatures defeated", scale = 1, unit = "" },
  { id = "gain", title = "Most levels gained", scale = 1, unit = "" },
  { id = "online", title = "Longest online", scale = 3600, unit = " h" },
}

local state = server.load("state", nil) or { number = 1, start = server.time() }
local archive = server.load("archive", {})      -- newest first: { number, start, finish, boards = { id = { { name, value } } } }

local end_season   -- defined below
local function key(name, number) return "s" .. (number or state.number) .. "." .. name end
local function finish_time() if length_days > 0 then return state.start + length_days * 86400 end end

-- A hero's level at its first sighting in the season is its base; "gain" is the best level minus the base.
local function touch(player)
  if not player.level or player.level <= 0 then return end
  local base = server.get(player, key("base"), 0)
  if base == 0 then server.set(player, key("base"), player.level); base = player.level end
  local gain = player.level - base
  if gain > server.get(player, key("gain"), 0) then server.set(player, key("gain"), gain) end
end

server.on("kill", function(player) server.add(player, key("kills")); touch(player) end)
server.on("enter", touch)
server.on("leave", touch)

server.every(60, function()
  for _, player in ipairs(server.players()) do server.add(player, key("online"), 60); touch(player) end
  local finish = finish_time()
  if finish and server.time() >= finish then end_season() end
end)

local function rows(board, number, n)
  local result = {}
  for _, row in ipairs(server.top(key(board.id, number), n or top_n)) do
    if row.value > 0 then result[#result + 1] = { rank = #result + 1, name = row.name, value = row.value / board.scale } end
  end
  return result
end

local function standings(number)
  local result = {}
  for _, board in ipairs(boards) do result[board.id] = rows(board, number) end
  return result
end

local function place(player)
  for i, row in ipairs(server.top(key("kills"), 1000)) do
    if row.name == (player.character or player.name) then return i, row.value end
  end
end

end_season = function()
  local now = server.time()
  local ended = { number = state.number, start = state.start, finish = now, boards = standings() }
  table.insert(archive, 1, ended)
  while #archive > keep_archive do table.remove(archive) end
  state = { number = state.number + 1, start = now }
  server.save("archive", archive); server.save("state", state)
  local champion = ended.boards.kills[1]
  server.broadcast("Season " .. ended.number .. " is over!" .. (champion and (" Most creatures defeated: " .. champion.name .. " (" .. math.floor(champion.value) .. ").") or "") ..
    " Season " .. state.number .. " has begun, everybody starts at zero.")
  for _, player in ipairs(server.players()) do touch(player) end
end

server.on("chat", function(player, text)
  if text ~= "!season" then return end
  local finish = finish_time()
  local left = finish and math.max(0, math.floor((finish - server.time()) / 3600)) or nil
  local rank, kills = place(player)
  server.message(player, "Season " .. state.number .. (left and (": " .. math.floor(left / 24) .. " d " .. (left % 24) .. " h left.") or ": no end date.") ..
    (rank and (" You are number " .. rank .. " with " .. math.floor(kills) .. " creatures defeated.") or " You have not defeated anything yet."))
  return true
end)

-- ---- endpoints ---------------------------------------------------------------------------------------------------------

server.endpoint("current", function()
  return { season = state.number, start = state.start, finish = finish_time(), boards = standings() }
end, { public = true, description = "this season's standings as JSON" })

server.endpoint("archive", function()
  local list = {}
  for i, s in ipairs(archive) do list[i] = { season = s.number, start = s.start, finish = s.finish, boards = s.boards } end
  return list
end, { public = true, description = "past seasons with their final standings, newest first (JSON)" })

server.endpoint("end-season", function()
  local ended = state.number
  end_season()
  return { ended = ended, season = state.number }
end, { description = "admin: end the current season now and start the next" })

-- ---- the page ------------------------------------------------------------------------------------------------------------

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

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem .2rem;font-size:1.3rem;color:#e0a944}
.sub{padding:0 1.2rem 1rem;color:#a39683}main{display:grid;grid-template-columns:repeat(auto-fill,minmax(17rem,1fr));gap:1rem;padding:0 1.2rem 1rem}
section,details{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}
ol{margin:0;padding-left:1.3rem}li{margin:.15rem 0}b{color:#fff}small{color:#a39683}summary{cursor:pointer;color:#f2c36b}
.past{padding:0 1.2rem 1.2rem;display:grid;gap:.6rem}.past main{padding:.6rem 0 0}]]

local function board_card(board, list)
  local h = server.html
  if #list == 0 then return "<section><h2>" .. h(board.title) .. "</h2><small>nobody yet</small></section>" end
  local parts = { "<section><h2>", h(board.title), "</h2><ol>" }
  for _, r in ipairs(list) do
    local value = board.scale == 1 and tostring(math.floor(r.value)) or string.format("%.1f", r.value)
    parts[#parts + 1] = "<li><b>" .. h(r.name) .. "</b> " .. value .. h(board.unit) .. "</li>"
  end
  parts[#parts + 1] = "</ol></section>"
  return table.concat(parts)
end

local function cards(data)
  local parts = { "<main>" }
  for _, board in ipairs(boards) do parts[#parts + 1] = board_card(board, data[board.id] or {}) end
  parts[#parts + 1] = "</main>"
  return table.concat(parts)
end

server.page("", function()
  local finish = finish_time()
  local parts = {
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="60"><title>Seasons</title><style>', style, "</style></head><body><h1>Season ", tostring(state.number), "</h1>",
    '<div class="sub">started ', stamp(state.start), " UTC", finish and (", ends " .. stamp(finish) .. " UTC") or ", no end date", "</div>",
    cards(standings()),
  }
  if #archive > 0 then
    parts[#parts + 1] = '<h1 style="font-size:1.1rem">Past seasons</h1><div class="past">'
    for _, s in ipairs(archive) do
      parts[#parts + 1] = "<details><summary>Season " .. s.number .. " <small>" .. stamp(s.start) .. " to " .. stamp(s.finish) .. "</small></summary>" ..
        cards(s.boards or {}) .. "</details>"
    end
    parts[#parts + 1] = "</div>"
  end
  parts[#parts + 1] = "</body></html>"
  return table.concat(parts)
end, { public = true, title = "Seasonal ladders" })
