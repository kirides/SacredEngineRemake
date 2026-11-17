-- Quest helper: shows a hero's quest records with the hints and places this server's owner wrote for them, tells a player when a record
-- is new or changed, and shows which other heroes have the same record. A Sacred Gear panel (gear/examples/questhelper) shows it; chat
-- has "!quests"; a page lists the records on the server. Copy into the server's scripts folder to use it. Storage: script-data/quest_helper.json.
--
--   /mods/quest_helper/                the page: heroes online with their record counts, the most common records, the hints of the catalog
--   /mods/quest_helper/records         JSON: every record the server has seen, its hint and how many heroes have it
-- With WebUi:PublicApi=true the same addresses also work under /public/mods/quest_helper/... (character names only).
--
-- WHAT THE SERVER REALLY KNOWS (read this before expecting a quest log). A hero file carries exactly one kind of quest state: the
-- "quest pairs", 8-byte [key][value] records of the inventory stream that the game appends when a scripted event or a quest item
-- changes something (Sacred.exe 0x5D8280; see Sacred.GameServer README, "Quest pairs"). server.hero(player).quest_pairs gives them.
-- The journal (active quests, their text, the map markers) and the script variables of the quests are world state of the game's
-- interpreter: they are not in a hero file and no message carries them, so this server cannot list "active quests", "available
-- quests" or progress percentages, and nobody has mapped which key belongs to which quest. So this mod is a catalog framework:
--   * every record the hero has is listed (key and value), new and changed records are announced;
--   * a key that is in the CATALOG (below, or in the storage key "catalog" of this mod) gets a name, an area, a hint and value labels;
--   * every other key is shown as "record <key>". Fill the catalog as you learn what a key means.
-- It needs hero data on the server (the web UI or --authoritative); without it the mod says so and shows nothing made up.

server.describe("Quest helper: a hero's quest records (the only quest state a hero file has) with catalog hints and areas, change notices, which heroes share a record; a Sacred Gear panel, chat and a page.")

-- ---- the catalog: key -> { name, area, hint, states = { [value] = label } }. Empty on purpose: nothing here is guessed. ----
-- Example (not a real quest):  CATALOG[1234] = { name = "Example quest", area = "Somewhere", hint = "Talk to X.", states = { [1] = "started", [2] = "done" } }
local CATALOG = {}

local CHANNEL = "questhelper"
local POLL_SECONDS = 5
local MAX_PAIRS = 120            -- records sent to a client at once
local MAX_OTHERS = 6             -- other heroes named per record
local MAX_KNOWN_KEYS = 400       -- records the server remembers (a page lists 50)
local NOTICE_SECONDS = 3         -- at most one chat notice per hero in this time
local BUCKET_CAPACITY, BUCKET_REFILL = 6, 0.5

local function clean(text, max)
  if type(text) ~= "string" then return "" end
  text = string.gsub(text, "%c", " ")
  if #text > max then text = string.sub(text, 1, max) end
  return text
end

-- The catalog of the file plus the owner's additions in storage (JSON keys are strings). Sizes are limited: they are shown to players.
local catalog = {}
local function load_catalog()
  catalog = {}
  local function add(key, entry)
    if type(entry) ~= "table" then return end
    local states = {}
    if type(entry.states) == "table" then
      for value, label in pairs(entry.states) do states[tostring(value)] = clean(tostring(label), 60) end
    end
    catalog[tostring(key)] = { name = clean(entry.name, 60), area = clean(entry.area, 60), hint = clean(entry.hint, 400), states = states }
  end
  for key, entry in pairs(CATALOG) do add(key, entry) end
  local stored = server.load("catalog", {})
  if type(stored) == "table" then
    for key, entry in pairs(stored) do add(key, entry) end
  end
end
load_catalog()

local function catalog_size()
  local n = 0
  for _ in pairs(catalog) do n = n + 1 end
  return n
end

local function key_text(key) return string.format("%d", key) end
local function title_of(key)
  local entry = catalog[key_text(key)]
  return entry and entry.name ~= "" and entry.name or ("record " .. key_text(key))
end

-- ---- what each online hero has: key -> value, read from server.hero ----

local current = {}        -- slot -> { [key] = value } (last poll)
local changed_at = {}     -- slot -> { [key] = server.time() } this session
local last_notice = {}    -- slot -> time
local buckets = {}

local function pairs_of(player)
  local hero, why = server.hero(player)
  if not hero then return nil, why end
  local map, order = {}, {}
  for _, pair in ipairs(hero.quest_pairs) do
    map[key_text(pair.key)] = pair.value
    order[#order + 1] = pair.key
  end
  return map, order, hero
end

-- Records of this server, with the heroes that have them, kept in this mod's storage so the page survives a restart:
-- known[key] = number of heroes that ever had it; per-hero values "q.<key>" are read with server.top.
local known = server.load("known", {})
local function remember(player, key, value)
  local k = key_text(key)
  if not known[k] then
    local total = 0
    for _ in pairs(known) do total = total + 1 end
    if total >= MAX_KNOWN_KEYS then return end
    known[k] = true
    server.save("known", known)
  end
  server.set(player, "q." .. k, value)
end

local function notice(player, text)
  local now = server.time()
  if last_notice[player.slot] and now - last_notice[player.slot] < NOTICE_SECONDS then return end
  last_notice[player.slot] = now
  server.message(player, text)
end

-- The clients that asked for the state get it again when something changed.
local ch = net.channel(CHANNEL)
local watching = {}       -- slot -> true while the client's panel asked for updates

local function others_with(key, except_slot)
  local names = {}
  for slot, map in pairs(current) do
    if slot ~= except_slot and map[key_text(key)] ~= nil then
      local other = server.player(slot)
      if other then names[#names + 1] = other.character end
    end
  end
  table.sort(names)
  while #names > MAX_OTHERS do table.remove(names) end
  return names
end

local function state_for(player)
  local map, order, hero = pairs_of(player)
  if not map then
    return { op = "state", available = false, reason = "the server has no hero data of you yet (it keeps it with its web UI or --authoritative, once your client sent the hero)" }
  end
  local rows = {}
  for i = 1, math.min(#order, MAX_PAIRS) do
    local key = order[i]
    local value = map[key_text(key)]
    local entry = catalog[key_text(key)]
    rows[#rows + 1] = {
      key = key, value = value, name = entry and entry.name ~= "" and entry.name or nil, area = entry and entry.area ~= "" and entry.area or nil,
      hint = entry and entry.hint ~= "" and entry.hint or nil, label = entry and entry.states[key_text(value)] or nil, known = entry ~= nil,
      others = others_with(key, player.slot), changed = (changed_at[player.slot] or {})[key_text(key)],
    }
  end
  local known_count = 0
  for _, row in ipairs(rows) do if row.known then known_count = known_count + 1 end end
  return { op = "state", available = true, total = #order, known = known_count, records = rows, catalog = catalog_size(), time = server.time() }
end

local function push(player)
  if ch and net.supports(player) then ch:send(player, state_for(player)) end
end

-- ---- polling: new and changed records ----

local function poll(player)
  local map, order = pairs_of(player)
  if not map then return end
  local before = current[player.slot]
  current[player.slot] = map
  local times = changed_at[player.slot]
  if not times then times = {}; changed_at[player.slot] = times end
  local news, updates = {}, 0
  for _, key in ipairs(order) do
    local k = key_text(key)
    remember(player, key, map[k])
    if before and before[k] == nil then news[#news + 1] = key; times[k] = server.time()
    elseif before and before[k] ~= map[k] then updates = updates + 1; times[k] = server.time(); news[#news + 1] = key end
  end
  if before and #news > 0 then
    local first = news[1]
    local entry = catalog[key_text(first)]
    local extra = #news > 1 and (" (+" .. (#news - 1) .. " more)") or ""
    local line = "Quest record: " .. title_of(first) .. extra
    if entry and entry.hint ~= "" then line = line .. ". " .. entry.hint end
    notice(player, line)
    if watching[player.slot] then push(player) end
  elseif not before and watching[player.slot] then push(player) end
end

server.every(POLL_SECONDS, function()
  for _, player in ipairs(server.players()) do
    if player.in_game then poll(player) end
  end
end)

server.on("enter", function(player)
  changed_at[player.slot] = nil; last_notice[player.slot] = nil; current[player.slot] = nil
  server.after(3, function()
    local p = server.player(player.slot)
    if p and p.uid == player.uid then poll(p) end
  end)
end)

server.on("leave", function(player)
  current[player.slot] = nil; changed_at[player.slot] = nil; last_notice[player.slot] = nil; watching[player.slot] = nil; buckets[player.slot] = nil
end)

-- ---- the client's requests ----

local function allowed(slot)
  local now = server.time()
  local bucket = buckets[slot]
  if not bucket then bucket = { tokens = BUCKET_CAPACITY, at = now }; buckets[slot] = bucket end
  bucket.tokens = math.min(BUCKET_CAPACITY, bucket.tokens + (now - bucket.at) * BUCKET_REFILL)
  bucket.at = now
  if bucket.tokens < 1 then return false end
  bucket.tokens = bucket.tokens - 1
  return true
end

if ch then
  ch:on(function(player, request)
    if type(request) ~= "table" or type(request.op) ~= "string" then return end
    if not allowed(player.slot) then ch:send(player, { op = "error", error = "too many requests" }); return end
    if request.op == "state" then
      poll(player)                         -- fresh before answering (the poll also seeds the baseline)
      watching[player.slot] = request.watch == true
      ch:send(player, state_for(player))
    elseif request.op == "unwatch" then
      watching[player.slot] = nil
    end
  end)
end

-- ---- chat: !quests ----

server.on("chat", function(player, text)
  if text ~= "!quests" then return end
  local map, order = pairs_of(player)
  if not map then
    server.message(player, "Quest records: the server has no hero data of you yet.")
    return true
  end
  server.message(player, #order .. " quest record" .. (#order == 1 and "" or "s") .. ". Newest first:")
  for i = #order, math.max(1, #order - 4), -1 do
    local key = order[i]
    local entry = catalog[key_text(key)]
    local line = "  " .. title_of(key) .. " = " .. key_text(map[key_text(key)])
    if entry and entry.states[key_text(map[key_text(key)])] then line = line .. " (" .. entry.states[key_text(map[key_text(key)])] .. ")" end
    if entry and entry.area ~= "" then line = line .. " - " .. entry.area end
    server.message(player, line)
  end
  return true
end)

-- ---- the page and the records endpoint ----

local function record_rows()
  local rows = {}
  for k in pairs(known) do
    local heroes = server.top("q." .. k, 1000)
    local entry = catalog[k]
    rows[#rows + 1] = { key = tonumber(k), name = entry and entry.name ~= "" and entry.name or nil, area = entry and entry.area ~= "" and entry.area or nil,
      hint = entry and entry.hint ~= "" and entry.hint or nil, heroes = #heroes }
  end
  table.sort(rows, function(a, b) if a.heroes ~= b.heroes then return a.heroes > b.heroes end return a.key < b.key end)
  while #rows > 50 do table.remove(rows) end
  return rows
end

server.endpoint("records", function() return record_rows() end, { public = true, description = "quest records seen on this server with catalog name, area, hint and the number of heroes that have them" })

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem;font-size:1.3rem;color:#e0a944}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(22rem,1fr));gap:1rem;padding:0 1.2rem 1.2rem}
section{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}h2{margin:0 0 .5rem;font-size:1rem;color:#f2c36b}
ul{margin:0;padding-left:1.2rem}li{margin:.2rem 0}b{color:#fff}small{color:#a39683}]]

local function list_card(title, items, line, empty)
  local parts = { "<section><h2>", server.html(title), "</h2>" }
  if #items == 0 then parts[#parts + 1] = "<small>" .. server.html(empty) .. "</small>"
  else
    parts[#parts + 1] = "<ul>"
    for _, item in ipairs(items) do parts[#parts + 1] = "<li>" .. line(item) .. "</li>" end
    parts[#parts + 1] = "</ul>"
  end
  parts[#parts + 1] = "</section>"
  return table.concat(parts)
end

server.page("", function()
  local h = server.html
  local online = {}
  for _, player in ipairs(server.players()) do
    local map = current[player.slot]
    if map then
      local n = 0
      for _ in pairs(map) do n = n + 1 end
      online[#online + 1] = { name = player.character, level = player.level, records = n }
    end
  end
  table.sort(online, function(a, b) return a.name < b.name end)
  local rows = record_rows()
  local catalog_rows = {}
  for k, entry in pairs(catalog) do catalog_rows[#catalog_rows + 1] = { key = tonumber(k) or 0, entry = entry } end
  table.sort(catalog_rows, function(a, b) return a.key < b.key end)
  return table.concat({
    '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">',
    '<meta http-equiv="refresh" content="30"><title>Quest records</title><style>', style, '</style></head><body><h1>Quest records</h1><main>',
    list_card("Heroes online", online, function(o) return "<b>" .. h(o.name) .. "</b> <small>level " .. tostring(o.level) .. ", " .. tostring(o.records) .. " record(s)</small>" end,
      "nobody with hero data online"),
    list_card("Most common records", rows, function(r)
      return "<b>" .. h(r.name or ("record " .. string.format("%d", r.key))) .. "</b> <small>" .. tostring(r.heroes) .. " hero(es)" .. (r.area and (", " .. h(r.area)) or "") .. "</small>"
    end, "no records seen yet"),
    list_card("Catalog (hints)", catalog_rows, function(c)
      return "<b>" .. h(c.entry.name ~= "" and c.entry.name or ("record " .. string.format("%d", c.key))) .. "</b> <small>" .. h(c.entry.area) .. "</small><br>" .. h(c.entry.hint)
    end, "the catalog is empty: the server owner adds names and hints for record keys"),
    "</main></body></html>",
  })
end, { public = true, title = "Quest records" })
