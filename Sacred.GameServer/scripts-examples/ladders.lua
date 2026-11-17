-- Ladders: most creatures defeated, highest level and longest time online, kept per hero across sessions and published
-- every 30 seconds for the web UI (/api/boards, and /public/boards when WebUi:PublicApi is on). Copy into the server's
-- scripts folder to use it. "!top" in chat shows the kill ladder to the player who asked.
--
-- Its own endpoints (this file is "ladders.lua", so they live under /mods/ladders/, and /public/mods/ladders/ when public):
--   /mods/ladders/top?board=kills&n=10   JSON rows of a ladder (kills, level, online)
--   /mods/ladders/by-creature            CSV: creature type, best hunter, kills

local size = 10

-- Creature types seen so far (kept in the saved data as one comma separated string).
local types = {}
for t in string.gmatch(server.get(nil, "ladder.types", ""), "[^,]+") do types[tonumber(t)] = true end

server.on("kill", function(player, monster)
  server.add(player, "ladder.kills")
  server.add(player, "ladder.kills." .. monster.type)
  if not types[monster.type] then
    types[monster.type] = true
    local list = {}
    for t in pairs(types) do list[#list + 1] = t end
    table.sort(list)
    server.set(nil, "ladder.types", table.concat(list, ","))
  end
  if player.level > server.get(player, "ladder.level", 0) then server.set(player, "ladder.level", player.level) end
end)

local function remember_level(player)
  if player.level > server.get(player, "ladder.level", 0) then server.set(player, "ladder.level", player.level) end
end
server.on("enter", remember_level)
server.on("leave", remember_level)

-- Online time: every minute each connected hero gets 60 seconds (a crash loses at most one minute).
server.every(60, function()
  for _, player in ipairs(server.players()) do server.add(player, "ladder.online", 60) end
end)

local function rows(key, scale)
  local result = {}
  for i, row in ipairs(server.top(key, size)) do result[i] = { name = row.name, value = row.value / (scale or 1) } end
  return result
end

local function publish()
  server.publish("kills", "Most creatures defeated", rows("ladder.kills"))
  server.publish("level", "Highest level", rows("ladder.level"))
  server.publish("online", "Longest online (hours)", rows("ladder.online", 3600))
end

server.on("start", publish)
server.every(30, publish)

server.on("chat", function(player, text)
  if text ~= "!top" then return end
  for i, row in ipairs(server.top("ladder.kills", 5)) do
    server.message(player, i .. ". " .. row.name .. ": " .. row.value .. " kills")
  end
  return true
end)

-- ---- endpoints ----

local boards = { kills = { "ladder.kills", 1 }, level = { "ladder.level", 1 }, online = { "ladder.online", 3600 } }

server.endpoint("top", function(request)
  local board = boards[request.query.board or "kills"]
  if not board then return "unknown board", "text/plain", 404 end
  local n = math.min(tonumber(request.query.n) or size, 100)
  local result = {}
  for i, row in ipairs(server.top(board[1], n)) do result[i] = { rank = i, name = row.name, value = row.value / board[2] } end
  return result
end, { public = true, description = "ladder rows as JSON: ?board=kills|level|online&n=10" })

server.endpoint("by-creature", function()
  local list = {}
  for t in pairs(types) do list[#list + 1] = t end
  table.sort(list)
  local result = {}
  for _, t in ipairs(list) do
    local best = server.top("ladder.kills." .. t, 1)[1]
    if best then result[#result + 1] = { creature = t, hunter = best.name, kills = best.value } end
  end
  return server.csv(result, { "creature", "hunter", "kills" }), "text/csv"
end, { public = true, description = "best hunter per creature type as CSV" })
