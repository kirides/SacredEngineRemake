-- Ladders: most creatures defeated, highest level and longest time online, kept per hero across sessions and published
-- every 30 seconds for the web UI (/api/boards, and /public/boards when WebUi:PublicApi is on). Copy into the server's
-- scripts folder to use it. "!top" in chat shows the kill ladder to the player who asked.

local size = 10

server.on("kill", function(player, monster)
  server.add(player, "ladder.kills")
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
