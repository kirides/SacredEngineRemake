-- Build sharer: players publish their hero build (class, level, skills, equipment) to this server, everybody can browse and search the
-- shared builds on a web page, and Sacred Gear clients can list and fetch them in a panel (the mod gear/examples/buildsharer).
-- Copy into the server's scripts folder to use it. Builds live in this mod's storage (script-data/build_sharer.json).
--
--   /mods/build_sharer/                      the page: every build as a card, with a live filter (text, class); ?q=orc&class=3 filters on the server too
--   /mods/build_sharer/?id=b3                one build in full
--   /mods/build_sharer/builds?q=&class=&sort=new|level&page=1&n=20   JSON list      /mods/build_sharer/build?id=b3   JSON, one build
-- With WebUi:PublicApi=true the same addresses also work under /public/mods/build_sharer/... (character names only: no account, no ids).
-- In chat: "!sharebuild [title]" publishes your build, "!unsharebuild" removes it, "!builds" counts them.
--
-- Where the data comes from. With server.hero (the server keeps hero state with the web UI or --authoritative) the SERVER fills class,
-- level, skills, combat arts and equipment from its own view of the hero: a client cannot make those up ("verified"). Without hero data
-- the build only has class and level as the server knows them plus what the client says about itself (a title, a note, tags and the items
-- it carries): those are shown as "reported". Whatever a client sends is length limited, type checked and HTML-escaped when shown.

server.describe("Build sharer: players publish their hero build (class, level, skills, equipment) and everybody browses and searches them, on a page and in a Sacred Gear panel.")

local CHANNEL = "buildsharer"
local MAX_BUILDS = 200          -- builds kept (one per hero); more are refused until somebody removes theirs
local MAX_TITLE, MAX_NOTE, MAX_TAG, MAX_TAGS, MAX_CARRIED, MAX_ITEM = 40, 240, 16, 5, 12, 48
local MAX_SKILLS, MAX_ARTS, MAX_EQUIPMENT = 8, 40, 19
local MAX_QUERY, MAX_PAGE_SIZE, DEFAULT_PAGE_SIZE = 40, 25, 12
local PUBLISH_COOLDOWN = tonumber(server.load("publish_cooldown", 15)) or 15     -- seconds between two publishes of one hero (storage key "publish_cooldown" changes it)
local BUCKET_CAPACITY, BUCKET_REFILL = 8, 0.5   -- requests a client may burst, and per second after that

-- The hero type numbers of Sacred Gear's gear.world.CLASSES (the FIRSTCONTACT class byte is passed as is: unverified against a real client).
local CLASS_NAMES = { [1] = "Seraphim", [2] = "Gladiator", [3] = "Battle Mage", [4] = "Dark Elf", [5] = "Wood Elf", [6] = "Vampiress", [8] = "Dwarf", [9] = "Daemon" }
local CLASS_IDS = {}
for id, name in pairs(CLASS_NAMES) do CLASS_IDS[string.lower(name)] = id; CLASS_IDS[string.lower((string.gsub(name, " ", "")))] = id end

local function class_name(id) return CLASS_NAMES[id] or ("Class " .. tostring(id)) end
-- Item tiers 0..15 as Sacred Gear reads them (set items cannot be told apart by the tier).
local function tier_name(r) if r >= 15 then return "unique" elseif r >= 12 then return "rare" elseif r >= 5 then return "magic" else return "normal" end end

-- ---- cleaning what clients send ----

local function clean(text, max)
  if type(text) ~= "string" then return "" end
  text = string.gsub(text, "%c", " ")
  text = string.gsub(text, "^%s+", "")
  text = string.gsub(text, "%s+$", "")
  if #text > max then text = string.sub(text, 1, max) end
  return text
end

local function whole(value, low, high, default)
  if type(value) ~= "number" or value ~= value then return default end
  value = math.floor(value)
  if value < low then return low elseif value > high then return high end
  return value
end

local function list_of(value, limit)
  local result = {}
  if type(value) == "table" then
    for i = 1, math.min(#value, limit) do result[#result + 1] = value[i] end
  end
  return result
end

-- ---- storage: one value per build ("build:<id>"), an index in memory ----

local builds, by_owner, search_text, count = {}, {}, {}, 0
local last_publish, buckets = {}, {}

local function build_key(id) return "build:" .. id end

local function search_of(b)
  local parts = { b.name, b.title, b.note, class_name(b.class), table.concat(b.tags, " ") }
  for _, s in ipairs(b.skills) do parts[#parts + 1] = s.name end
  for _, e in ipairs(b.equipment) do parts[#parts + 1] = e.name end
  for _, c in ipairs(b.carried) do parts[#parts + 1] = c end
  return string.lower(table.concat(parts, " "))
end

local function index(b)
  if not builds[b.id] then count = count + 1 end
  builds[b.id] = b; by_owner[b.owner] = b.id; search_text[b.id] = search_of(b)
end

local function unindex(b)
  builds[b.id] = nil; by_owner[b.owner] = nil; search_text[b.id] = nil; count = count - 1
end

for _, key in ipairs(server.keys("build:")) do
  local b = server.load(key)
  -- a damaged or hand-edited value is ignored, never trusted
  if type(b) == "table" and type(b.id) == "string" and type(b.owner) == "string" and type(b.name) == "string" then
    b.tags = list_of(b.tags, MAX_TAGS); b.skills = list_of(b.skills, MAX_SKILLS); b.equipment = list_of(b.equipment, MAX_EQUIPMENT)
    b.arts = list_of(b.arts, MAX_ARTS); b.carried = list_of(b.carried, MAX_CARRIED)
    b.class = whole(b.class, 0, 99, 0); b.level = whole(b.level, 0, 216, 0)
    b.title = clean(b.title, MAX_TITLE); b.note = clean(b.note, MAX_NOTE)
    index(b)
  end
end

-- ---- what leaves the server: a whitelist of fields, never the owner ----

local function summary(b)
  return { id = b.id, name = b.name, class = b.class, class_name = class_name(b.class), level = b.level, title = b.title,
    tags = b.tags, verified = b.verified and true or false, updated = b.updated }
end

local function full(b)
  local view = summary(b)
  view.note = b.note; view.created = b.created; view.experience = b.experience; view.skill_points = b.skill_points
  view.skills = b.skills; view.arts = b.arts; view.equipment = b.equipment; view.carried = b.carried
  view.attributes = b.attributes; view.attribute_points = b.attribute_points
  return view
end

-- ---- assembling a build from the player and its request ----

local function assemble(player, request)
  local old = builds[by_owner[player.uid]]
  local now = server.time()
  local b = {
    id = old and old.id, owner = player.uid, name = player.character, created = old and old.created or now, updated = now,
    title = clean(request.title, MAX_TITLE), note = clean(request.note, MAX_NOTE),
    tags = {}, skills = {}, arts = {}, equipment = {}, carried = {}, verified = false,
  }
  for _, tag in ipairs(list_of(request.tags, MAX_TAGS)) do
    tag = clean(tag, MAX_TAG)
    if #tag > 0 then b.tags[#b.tags + 1] = tag end
  end
  b.class = whole(player.class, 0, 99, 0)
  b.level = whole(player.level, 0, 216, 0)
  local hero = server.hero(player)
  if hero then
    b.verified = true
    b.class = whole(hero.class, 0, 99, b.class); b.level = whole(hero.level, 0, 216, b.level)
    b.experience = hero.experience; b.skill_points = hero.skill_points
    b.attribute_points = hero.attribute_points; b.attributes = hero.attributes
    for _, s in ipairs(list_of(hero.skills, MAX_SKILLS)) do
      b.skills[#b.skills + 1] = { id = s.id, level = s.level, bonus = s.bonus, name = clean(s.name or ("skill " .. s.id), MAX_ITEM) }
    end
    for _, a in ipairs(list_of(hero.arts, MAX_ARTS)) do
      b.arts[#b.arts + 1] = { id = a.id, kind = a.kind, rank = a.rank, name = clean(a.name or ("art " .. a.id), MAX_ITEM) }
    end
    for _, e in ipairs(list_of(hero.equipment, MAX_EQUIPMENT)) do
      b.equipment[#b.equipment + 1] = { slot = e.slot, level = e.level, tier = tier_name(e.rarity), sockets = e.sockets,
        name = clean(e.name or ("item " .. e.type), MAX_ITEM) }
    end
  else
    if b.level == 0 then b.level = whole(request.level, 1, 216, 0) end    -- reported by the client: only when the server has no level
    for _, item in ipairs(list_of(request.carried, MAX_CARRIED)) do
      item = clean(item, MAX_ITEM)
      if #item > 0 then b.carried[#b.carried + 1] = item end
    end
  end
  return b, old
end

local function publish(player, request)
  local now = server.time()
  if last_publish[player.uid] and now - last_publish[player.uid] < PUBLISH_COOLDOWN then
    return nil, "wait a few seconds before publishing again"
  end
  local b, old = assemble(player, request)
  if not old then
    if count >= MAX_BUILDS then return nil, "the build list of this server is full" end
    b.id = "b" .. string.format("%d", server.add(nil, "next_id"))
  end
  local ok, err = pcall(server.save, build_key(b.id), b)
  if not ok then
    if not old then server.add(nil, "next_id", -1) end
    return nil, "the build could not be stored"
  end
  last_publish[player.uid] = now
  index(b)
  return b
end

local function remove(player)
  local id = by_owner[player.uid]
  if not id then return false end
  local b = builds[id]
  server.delete(build_key(id))
  unindex(b)
  return true
end

-- ---- search ----

local function class_filter(value)
  if type(value) == "number" then return value end
  if type(value) ~= "string" or value == "" then return nil end
  return tonumber(value) or CLASS_IDS[string.lower(value)]
end

local function find(q, class, sort, page, per)
  q = string.lower(clean(q, MAX_QUERY)); class = class_filter(class)
  local matched = {}
  for id, b in pairs(builds) do
    if (not class or b.class == class) and (q == "" or string.find(search_text[id], q, 1, true)) then matched[#matched + 1] = b end
  end
  if sort == "level" then
    table.sort(matched, function(a, b) if a.level ~= b.level then return a.level > b.level end return a.id < b.id end)
  else
    table.sort(matched, function(a, b) if a.updated ~= b.updated then return a.updated > b.updated end return a.id < b.id end)
  end
  per = whole(per, 1, MAX_PAGE_SIZE, DEFAULT_PAGE_SIZE)
  local pages = math.max(1, math.ceil(#matched / per))
  page = whole(page, 1, pages, 1)
  local rows = {}
  for i = (page - 1) * per + 1, math.min(#matched, page * per) do rows[#rows + 1] = summary(matched[i]) end
  return { total = #matched, page = page, pages = pages, per = per, builds = rows }
end

-- ---- the net channel ----

local ch = net.channel(CHANNEL)

local function reply(player, payload)
  if ch then ch:send(player, payload) end
end

-- A token bucket per client: a burst of 8 requests, then one every two seconds.
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

server.on("leave", function(player) buckets[player.slot] = nil end)

if ch then
  ch:on(function(player, request)
    if type(request) ~= "table" or type(request.op) ~= "string" then return end
    if not allowed(player.slot) then reply(player, { op = "error", error = "too many requests" }); return end
    local op = request.op
    if op == "list" then
      local result = find(request.q, request.class, request.sort, request.page, request.per)
      result.op = "list"
      reply(player, result)
    elseif op == "get" then
      local b = type(request.id) == "string" and builds[request.id]
      if b then reply(player, { op = "build", build = full(b) }) else reply(player, { op = "error", error = "no such build" }) end
    elseif op == "mine" then
      local b = builds[by_owner[player.uid]]
      reply(player, { op = "mine", build = b and full(b) or nil, count = count, hero_data = server.hero(player) ~= nil })
    elseif op == "publish" then
      local b, err = publish(player, request)
      if b then reply(player, { op = "published", build = full(b) }) else reply(player, { op = "error", error = err }) end
    elseif op == "unpublish" then
      reply(player, { op = "unpublished", removed = remove(player) })
    end
  end)
end

-- ---- chat: !sharebuild [title], !unsharebuild, !builds ----

server.on("chat", function(player, text)
  if text == "!builds" then
    server.message(player, count .. " shared build" .. (count == 1 and "" or "s") .. ". !sharebuild [title] publishes yours.")
    return true
  elseif text == "!unsharebuild" then
    server.message(player, remove(player) and "Your build is removed." or "You have no shared build.")
    return true
  elseif text == "!sharebuild" or string.sub(text, 1, 12) == "!sharebuild " then
    local b, err = publish(player, { title = string.sub(text, 13) })
    if b then
      server.message(player, "Build shared as " .. b.id .. (b.verified and "" or " (the server has no hero data: class and level only)") .. ".")
    else
      server.message(player, "Not shared: " .. err .. ".")
    end
    return true
  end
end)

-- ---- endpoints ----

server.endpoint("builds", function(request)
  local q = request.query
  return find(q.q, q.class, q.sort, tonumber(q.page), tonumber(q.n))
end, { public = true, description = "shared builds as JSON: ?q=text&class=3|Seraphim&sort=new|level&page=1&n=12" })

server.endpoint("build", function(request)
  local b = request.query.id and builds[request.query.id]
  if not b then return "no such build", "text/plain", 404 end
  return full(b)
end, { public = true, description = "one shared build in full as JSON: ?id=b1" })

-- ---- the page: built on the server, every text passes through server.html ----

local style = [[
body{margin:0;font:15px/1.4 system-ui,sans-serif;background:#15110d;color:#efe6d6}h1{margin:0;padding:1rem 1.2rem .3rem;font-size:1.3rem;color:#e0a944}
.bar{display:flex;gap:.6rem;flex-wrap:wrap;padding:.4rem 1.2rem 1rem}input,select{background:#241d16;color:#efe6d6;border:1px solid #3b3025;border-radius:6px;padding:.35rem .5rem;font:inherit}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(19rem,1fr));gap:1rem;padding:0 1.2rem 1.2rem}
.card{background:#241d16;border:1px solid #3b3025;border-radius:10px;padding:.8rem 1rem}.card h2{margin:0;font-size:1.05rem;color:#f2c36b}
a{color:#e0a944}ul{margin:.3rem 0;padding-left:1.2rem}li{margin:.1rem 0}small,.dim{color:#a39683}.tag{display:inline-block;background:#3b3025;border-radius:9px;padding:0 .5rem;margin:0 .2rem .2rem 0;font-size:.85rem}
.ok{color:#8fce7a}.magic{color:#6da7ff}.rare{color:#ffd24a}.unique{color:#ff9a3c}p{margin:.4rem 0}]]

local script = [[
var q=document.getElementById('q'),c=document.getElementById('c'),n=document.getElementById('n');
function f(){var t=q.value.toLowerCase(),k=c.value,s=0,all=document.querySelectorAll('.card');
for(var i=0;i<all.length;i++){var e=all[i],ok=(!t||e.getAttribute('data-s').indexOf(t)>=0)&&(!k||e.getAttribute('data-c')===k);e.style.display=ok?'':'none';if(ok)s++;}
n.textContent=s+' shown';}
q.oninput=f;c.onchange=f;f();]]

local function ago(t)
  local s = math.max(0, server.time() - (t or 0))
  if s < 90 then return "just now" elseif s < 5400 then return string.format("%d min ago", s / 60)
  elseif s < 129600 then return string.format("%d h ago", s / 3600) else return string.format("%d days ago", s / 86400) end
end

local function head(title)
  return table.concat({ '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>',
    server.html(title), '</title><style>', style, '</style></head><body>' })
end

local function tags_html(b)
  local parts = {}
  for _, tag in ipairs(b.tags) do parts[#parts + 1] = '<span class="tag">' .. server.html(tag) .. "</span>" end
  return table.concat(parts)
end

local function badge(b)
  return b.verified and '<small class="ok">server-verified</small>' or "<small>reported by the player</small>"
end

local function card(b)
  local h = server.html
  return table.concat({ '<section class="card" data-c="', tostring(b.class), '" data-s="', h(search_text[b.id] or ""), '"><h2><a href="?id=', h(b.id), '">',
    h(b.name), "</a></h2><div>", h(class_name(b.class)), " &middot; level ", tostring(b.level), " ", badge(b), "</div>",
    b.title ~= "" and ("<p><b>" .. h(b.title) .. "</b></p>") or "", "<div>", tags_html(b), "</div><small>", ago(b.updated), "</small>", "</section>" })
end

local function list_page(request)
  local h = server.html
  local result = find(request.query.q, request.query.class, request.query.sort, 1, MAX_PAGE_SIZE * 8)
  local q = clean(request.query.q, MAX_QUERY)
  local options = { '<option value="">all classes</option>' }
  local wanted = class_filter(request.query.class)
  for _, id in ipairs({ 1, 2, 3, 4, 5, 6, 8, 9 }) do
    options[#options + 1] = '<option value="' .. id .. '"' .. (wanted == id and " selected" or "") .. ">" .. h(class_name(id)) .. "</option>"
  end
  local cards = {}
  for _, row in ipairs(result.builds) do cards[#cards + 1] = card(builds[row.id]) end
  if #cards == 0 then cards[1] = "<p class=\"dim\">Nobody has shared a build yet. In the game: !sharebuild [title], or the Build sharer panel of Sacred Gear.</p>" end
  return table.concat({ head("Shared builds"), '<h1>Shared hero builds</h1><div class="bar"><input id="q" type="search" placeholder="search heroes, skills, items, tags" size="34" value="',
    h(q), '"><select id="c">', table.concat(options), '</select><span class="dim" id="n"></span></div><main>', table.concat(cards), "</main><script>", script, "</script></body></html>" })
end

local function detail_page(b)
  local h = server.html
  local parts = { head(b.name .. " - build"), '<h1><a href="?">Shared builds</a> / ', h(b.name), '</h1><main><section class="card"><h2>', h(class_name(b.class)), ", level ", tostring(b.level),
    "</h2>", badge(b), b.title ~= "" and ("<p><b>" .. h(b.title) .. "</b></p>") or "", b.note ~= "" and ("<p>" .. h(b.note) .. "</p>") or "", "<div>", tags_html(b), "</div>",
    "<small>shared ", ago(b.created), ", updated ", ago(b.updated), "</small></section>" }
  local function section(title, items, line)
    if #items == 0 then return end
    parts[#parts + 1] = '<section class="card"><h2>' .. h(title) .. "</h2><ul>"
    for _, item in ipairs(items) do parts[#parts + 1] = "<li>" .. line(item) .. "</li>" end
    parts[#parts + 1] = "</ul></section>"
  end
  section("Skills", b.skills, function(s) return h(s.name) .. " <b>" .. tostring(s.level) .. "</b>" .. ((s.bonus or 0) > 0 and (" <small>+" .. tostring(s.bonus) .. "</small>") or "") end)
  section("Combat arts", b.arts, function(a) return h(a.name) end)
  section("Equipment", b.equipment, function(e)
    return '<span class="' .. h(e.tier or "normal") .. '">' .. h(e.name) .. "</span> <small>item level " .. tostring(e.level) .. ((e.sockets or 0) > 0 and (", " .. tostring(e.sockets) .. " socketed") or "") .. "</small>"
  end)
  section("Carried (reported by the player)", b.carried, function(c) return h(c) end)
  parts[#parts + 1] = "</main></body></html>"
  return table.concat(parts)
end

server.page("", function(request)
  local id = request.query.id
  if id and builds[id] then return detail_page(builds[id]) end
  return list_page(request)
end, { public = true, title = "Shared builds" })
