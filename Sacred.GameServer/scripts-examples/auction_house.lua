-- Auction house: the market of the Sacred Gear mod auction-house (sacred-gear-mods src/auction-house) run by the game server
-- itself, so it is open whenever the server runs (not only while the host plays) and players are identified by the server.
-- Copy into the server's scripts folder. Every player needs the Gear mod auction-house (it finds this market over the net
-- channel "auction" and falls back to the host's market on a stock GameServer.exe).
--
--   /mods/auction_house/                   the page: current auctions (name in its quality colour, level, price, seller, time left),
--                                          search and sort in the page
--   /mods/auction_house/listings           JSON, every current auction      /mods/auction_house/recent-sales   JSON, the last sales
-- With WebUi:PublicApi=true also under /public/mods/auction_house/... Character names only: no uids, no accounts.
--
-- Everything lives in this mod's storage (script-data/auction_house.json): listings with their item data, mailboxes, wallets,
-- the sales history and the request log. Every change is written durably (server.flush) before its answer goes out; a failed
-- write is undone and refused. Requests carry ids (rid): a repeated request gets the same answer and changes nothing.
--
-- Closed Net with --authoritative (the server writes the heroes): a listing or a deposit only becomes real after the server has
-- seen the item (or gold) leave the hero in its own hero state and the lobby has stored the hero without it; a mailbox entry is
-- only removed after the server has seen the item (or gold) arrive and the lobby has stored the hero with it. LAN and Open Net:
-- the client's journal and crash check (as with the host's market). Details: sacred-gear-mods src/auction-house/DESIGN.md, 4.

server.describe("Auction house: players sell items to each other for gold (Sacred Gear mod auction-house); the server keeps the market, a page lists the auctions.")

-- ---- market rules (edit here; a reload applies them) -----------------------------------------------------------------------
local config = {
    durations = { 1, 3, 7 },        -- days a listing may run (real time on the server's clock)
    fee_per_day = 0.5,              -- percent of the price per listed day, paid when listing, not returned (0: none)
    fee_min = 1,                    -- gold, when there is a fee
    sale_cut = 5,                   -- percent of the price taken when an item sells (0: none)
    max_per_seller = 20,
    max_listings = 400,
    price_min = 1,
    price_max = 2000000000,
    sweep_seconds = 60,             -- expired listings go back to their sellers' mailboxes this often
    verify_seconds = 30,            -- Closed Net: how long to wait for the server to see an item or gold leave or arrive
    recent_sales = 30,              -- sales on the page and in /recent-sales
    chat_notices = true,            -- "your item sold" also as a server chat line
    page_public = true,             -- the page and the JSON (character names only) without the admin password (WebUi:PublicApi)
}

local CHANNEL = "auction"

-- >>> core.lua: generated from sacred-gear-mods src/auction-house/core.lua, do not edit here. fnv1a 6b46c5e4
-- Regenerate: luajit tools/inline_core.lua core.lua <this file>   (in sacred-gear-mods src/auction-house)
local core = (function()
-- The market: listings, wallets, mailboxes, the rules, persistence. Pure Lua: no game calls, so the same code can run
-- in a player's game (single player, the host of a network game), later in GameServer.exe or in the C# GameServer's
-- Lua (DESIGN.md, section 2). Everything it needs comes in `deps`:
--
--   deps.now()               -> seconds (os.time on the machine that runs the market)
--   deps.load()              -> the saved state table, or nil (no market yet), or nil, error (unreadable: read-only)
--   deps.save(state)         -> true | nil, error (must be atomic: temp file + rename)
--   deps.save_async(state, done)  (instead of save, optional) starts the write; done(ok, err) once it is on disk. While a
--                            write is out, requests and sweeps wait in a queue (`waiting`) and run after it, so a failed
--                            write can be undone without touching later changes
--   deps.rolled_back(state)  (optional) the state after a failed write was undone (to put it back where it is kept)
--   deps.send(slot, msg)     -> sends a message table to a player
--   deps.log(text)           (optional)
--   deps.random_id()         -> a random hex string (optional; the market id)
--
-- Every change is written before its answer is sent. Requests handled in the same tick share one write (`flush`): if
-- that write fails, the whole batch is undone and every changing request of it is refused. Requests that change
-- something carry a request id (rid); the answer to a rid is kept for RID_DAYS days, so a repeated request gets the
-- same answer and changes nothing (DESIGN.md, section 4).
--
-- Identity: `from.key` and `from.name` as the receiving side sees the player. A market inside a server that identifies
-- its players itself (the C# GameServer: a per-server hero id) passes `from.verified = true`: no secret is needed then.
--
-- Plain Lua 5.1 / 5.2 (LuaJIT, and the GameServer's LuaCSharp 5.2 sandbox): no require of other modules, no os, io,
-- goto, bit operations or integer division.
local M = {}
M.VERSION = 1           -- the file format
M.PROTOCOL = 1          -- the messages

local DAY = 86400
local RID_DAYS = 14
local HISTORY = 400     -- sales kept for price discovery
local LOG_KEEP = 200
local MAX_DATA = 16384  -- hex characters of one item's data
local MAX_TEXT = 2000   -- characters of a listing's search text

M.DEFAULT_RULES = {
    durations = {1, 3, 7},          -- days
    fee_per_day = 0.5,              -- percent of the price per listed day, paid from the wallet when listing
    fee_min = 1,                    -- gold, when fee_per_day > 0
    sale_cut = 5,                   -- percent of the price taken when it sells
    max_per_seller = 20,
    max_listings = 400,
    price_min = 1,
    price_max = 2000000000,
    wallet_max = 2000000000,
    page_size = 10,
}

local QUALITY_RANK = {normal = 1, magic = 2, rare = 3, unique = 4, set = 5}
M.QUALITY_RANK = QUALITY_RANK

---------------------------------------------------------------------------------------------------------------
-- helpers
---------------------------------------------------------------------------------------------------------------
local function copy(v)
    if type(v) ~= 'table' then return v end
    local t = {}
    for k, x in pairs(v) do t[k] = copy(x) end
    return t
end
M.copy = copy

local function int(v)
    v = tonumber(v)
    if not v or v ~= v or v == math.huge or v == -math.huge then return nil end
    if v ~= math.floor(v) then return nil end
    return v
end

local function str(v, max)
    if type(v) ~= 'string' then return nil end
    if max and #v > max then return v:sub(1, max) end
    return v
end

-- The game's tooltip text without its codes: `\c` and `\w` (a colour, 8 hex digits) and `\g` (an image: a colour and
-- an image id, 16 characters, the id padded with spaces).
function M.plain(s)
    if type(s) ~= 'string' then return s end
    s = s:gsub('\\g%x%x%x%x%x%x%x%x[%x ][%x ][%x ][%x ][%x ][%x ][%x ][%x ]', '')
    s = s:gsub('\\[cw]%x%x%x%x%x%x%x%x', '')
    return s
end

local function trim(s) return type(s) == 'string' and (s:gsub('^%s+', ''):gsub('%s+$', '')) or s end

-- The item's type id: the first four bytes of its data, little endian (as gear.inv.export writes it).
function M.type_of(hex)
    if type(hex) ~= 'string' or #hex < 8 then return nil end
    local b = {}
    for i = 0, 3 do
        local v = tonumber(hex:sub(i * 2 + 1, i * 2 + 2), 16)
        if not v then return nil end
        b[i + 1] = v
    end
    return b[1] + b[2] * 256 + b[3] * 65536 + b[4] * 16777216
end

local function valid_hex(hex)
    return type(hex) == 'string' and #hex >= 8 and #hex <= MAX_DATA and #hex % 2 == 0 and not hex:find('[^0-9a-f]')
end

local function median(list)
    if #list == 0 then return nil end
    local s = {}
    for i, v in ipairs(list) do s[i] = v end
    table.sort(s)
    local n = #s
    if n % 2 == 1 then return s[(n + 1) / 2] end
    return math.floor((s[n / 2] + s[n / 2 + 1]) / 2)
end
M.median = median

function M.fee(rules, price, days)
    if (rules.fee_per_day or 0) <= 0 then return 0 end
    return math.max(rules.fee_min or 0, math.floor(price * rules.fee_per_day / 100 * days))
end

function M.cut(rules, price)
    return math.floor(price * (rules.sale_cut or 0) / 100)
end

local function new_state(id)
    return {
        version = M.VERSION, market_id = id, next_id = 1, next_mid = 1,
        listings = {}, mail = {}, wallets = {}, players = {}, sales = {}, rids = {}, log = {},
    }
end

-- Fills in what an older or hand-edited file lacks; nil, error if it is not a market file.
local function normalize(s)
    if type(s) ~= 'table' then return nil, 'not a market file' end
    if type(s.version) ~= 'number' then return nil, 'no version' end
    if s.version > M.VERSION then return nil, 'written by a newer version of the mod (' .. s.version .. ')' end
    for _, k in ipairs({'listings', 'mail', 'sales', 'log'}) do
        if type(s[k]) ~= 'table' then s[k] = {} end
    end
    for _, k in ipairs({'wallets', 'players', 'rids'}) do
        if type(s[k]) ~= 'table' then s[k] = {} end
    end
    s.next_id = int(s.next_id) or 1
    s.next_mid = int(s.next_mid) or 1
    for _, l in ipairs(s.listings) do
        if type(l) ~= 'table' or not int(l.id) or not valid_hex(l.data) then return nil, 'a listing is damaged' end
        if l.id >= s.next_id then s.next_id = l.id + 1 end
    end
    for _, m in ipairs(s.mail) do
        if type(m) ~= 'table' or not int(m.mid) then return nil, 'a mailbox entry is damaged' end
        if m.mid >= s.next_mid then s.next_mid = m.mid + 1 end
    end
    return s
end

---------------------------------------------------------------------------------------------------------------
-- the market object
---------------------------------------------------------------------------------------------------------------
local Market = {}
Market.__index = Market

function M.new(deps, rules)
    local self = setmetatable({deps = deps, rules = {}, out = {}, batch = nil, problem = nil, note = nil,
        online = {}, last_sweep = -1e18, writing = false, waiting = {}}, Market)
    for k, v in pairs(M.DEFAULT_RULES) do self.rules[k] = v end
    for k, v in pairs(rules or {}) do self.rules[k] = v end
    self:load()
    return self
end

function Market:log(text)
    if self.deps.log then self.deps.log(text) end
end

function Market:load()
    local s, err = self.deps.load()
    if s == nil and err then
        self.problem = 'the market file cannot be read (' .. tostring(err) .. '); the market is closed until it is fixed'
        self.state = new_state('broken')
        return
    end
    if s == nil then
        local id = self.deps.random_id and self.deps.random_id() or tostring(self.deps.now())
        self.state = new_state(id)
        return
    end
    local n, why = normalize(s)
    if not n then
        self.problem = 'the market file cannot be used (' .. why .. '); the market is closed until it is fixed'
        self.state = new_state('broken')
        return
    end
    self.state = n
end

function Market:set_rules(rules)
    for k, v in pairs(rules) do self.rules[k] = v end
end

-- Remembers where a player is now (their slot) for notifications.
function Market:seen(key, slot)
    if key then self.online[key] = slot end
end

function Market:left(slot)
    for k, s in pairs(self.online) do
        if s == slot then self.online[k] = nil end
    end
end

-- Messages to send after the batch is written: {slot, msg, changing}.
function Market:reply(slot, msg, changing)
    self.out[#self.out + 1] = {slot = slot, msg = msg, changing = changing}
end

-- Called before the first change of a batch: keeps the state to roll back to.
function Market:begin()
    if not self.batch then self.batch = copy(self.state) end
end

function Market:add_log(kind, text)
    local log = self.state.log
    log[#log + 1] = {t = self.deps.now(), kind = kind, text = text}
    while #log > LOG_KEEP do table.remove(log, 1) end
    self:log(kind .. ': ' .. text)
end

-- Writes the batch (if anything changed) and sends the answers. On a failed write the state goes back to where the
-- batch began and every changing answer becomes a refusal. With deps.save_async the answers go out once the write is
-- done; until then new requests wait (handle queues them) and run, as the next batch, after it.
function Market:flush()
    if self.writing then return end
    local out, batch = self.out, self.batch
    self.out, self.batch = {}, nil
    if not batch then return self:send_out(out) end
    if self.deps.save_async then
        self.writing = true
        local done = false
        self.deps.save_async(self.state, function(ok, err)
            if done then return end
            done = true
            self.writing = false
            self:finish(out, batch, ok, err)
            self:drain()
        end)
        return
    end
    local ok, err = self.deps.save(self.state)
    self:finish(out, batch, ok, err)
end

function Market:finish(out, batch, ok, err)
    if not ok then
        self.state = batch
        self:log('market write failed: ' .. tostring(err))
        for _, o in ipairs(out) do
            if o.changing then
                o.msg = {op = 'result', rid = o.msg.rid, ok = false, err = 'the market could not save; nothing changed'}
            end
        end
        -- notifications of the failed batch must not go out
        local kept = {}
        for _, o in ipairs(out) do if o.msg.op ~= 'notify' then kept[#kept + 1] = o end end
        out = kept
        if self.deps.rolled_back then self.deps.rolled_back(self.state) end
    end
    self:send_out(out)
end

function Market:send_out(out)
    for _, o in ipairs(out) do self.deps.send(o.slot, o.msg) end
end

-- After an asynchronous write: the requests that came in meanwhile, as one batch.
function Market:drain()
    if #self.waiting == 0 then return end
    local list = self.waiting
    self.waiting = {}
    for _, w in ipairs(list) do
        if w.sweep then self:sweep(w.force) else self:handle(w.from, w.msg) end
    end
    self:flush()
end

-- Is a write out (deps.save_async)? Then changes must wait.
function Market:busy() return self.writing end

---------------------------------------------------------------------------------------------------------------
-- request ids
---------------------------------------------------------------------------------------------------------------
function Market:rid_record(key, rid)
    local r = self.state.rids[key]
    return r and r[rid]
end

function Market:remember(key, rid, rec)
    local r = self.state.rids[key]
    if not r then r = {} self.state.rids[key] = r end
    rec.t = self.deps.now()
    r[rid] = rec
end

---------------------------------------------------------------------------------------------------------------
-- lookups
---------------------------------------------------------------------------------------------------------------
function Market:listing(id)
    for i, l in ipairs(self.state.listings) do
        if l.id == id then return l, i end
    end
end

function Market:mail_entry(mid)
    for i, m in ipairs(self.state.mail) do
        if m.mid == mid then return m, i end
    end
end

function Market:count_seller(key)
    local n = 0
    for _, l in ipairs(self.state.listings) do if l.seller == key then n = n + 1 end end
    return n
end

function Market:wallet(key)
    return self.state.wallets[key] or 0
end

function Market:add_mail(owner, kind, fields)
    local s = self.state
    local m = {mid = s.next_mid, owner = owner, kind = kind, t = self.deps.now()}
    s.next_mid = s.next_mid + 1
    for k, v in pairs(fields) do m[k] = v end
    s.mail[#s.mail + 1] = m
    return m
end

local function item_mail_fields(l, reason)
    return {reason = reason, data = l.data, type = l.type, name = l.meta.name, quality = l.meta.quality,
        category = l.meta.category, w = l.meta.w, h = l.meta.h, price = l.price}
end

function Market:notify(key, msg)
    local slot = self.online[key]
    if slot then
        msg.op = 'notify'
        self:reply(slot, msg, false)
    end
end

---------------------------------------------------------------------------------------------------------------
-- expiry and pruning
---------------------------------------------------------------------------------------------------------------
-- Moves listings whose time is up to their sellers' mailboxes and forgets old request ids. Call it now and then
-- (every minute is plenty); it starts a batch only if something changes.
function Market:sweep(force)
    if self.problem then return end
    if self.writing then
        for _, w in ipairs(self.waiting) do if w.sweep then return end end
        self.waiting[#self.waiting + 1] = {sweep = true, force = force}
        return
    end
    local now = self.deps.now()
    if not force and now - self.last_sweep < 60 then return end
    self.last_sweep = now
    local s = self.state
    local i = 1
    while i <= #s.listings do
        local l = s.listings[i]
        if l.ends <= now then
            self:begin()
            table.remove(s.listings, i)
            self:add_mail(l.seller, 'item', item_mail_fields(l, 'expired'))
            self:notify(l.seller, {kind = 'expired', name = l.meta.name})
        else
            i = i + 1
        end
    end
    local old = now - RID_DAYS * DAY
    for key, r in pairs(s.rids) do
        for rid, rec in pairs(r) do
            -- a delivery that is still waiting for its confirmation stays
            if (rec.t or 0) < old and not rec.pending then
                self:begin()
                r[rid] = nil
            end
        end
        if next(r) == nil then
            self:begin()
            s.rids[key] = nil
        end
    end
end

---------------------------------------------------------------------------------------------------------------
-- browsing
---------------------------------------------------------------------------------------------------------------
local function lower(s) return type(s) == 'string' and s:lower() or '' end

local function matches(l, q, now)
    if l.ends <= now then return false end
    local m = l.meta
    if q.text and q.text ~= '' then
        local hay = lower(m.name) .. '\n' .. lower(m.text) .. '\n' .. lower(l.seller_name)
        for word in q.text:lower():gmatch('%S+') do
            if not hay:find(word, 1, true) then return false end
        end
    end
    if q.category and q.category ~= '' and m.category ~= q.category then return false end
    if q.quality and q.quality ~= '' and m.quality ~= q.quality then return false end
    if q.min_quality and (QUALITY_RANK[m.quality] or 0) < (QUALITY_RANK[q.min_quality] or 0) then return false end
    if q.class and q.class ~= '' and type(m.classes) == 'table' and #m.classes > 0 then
        local ok = false
        for _, c in ipairs(m.classes) do if c == q.class then ok = true break end end
        if not ok then return false end
    end
    local lvl = m.level or 0
    if q.min_level and lvl < q.min_level then return false end
    if q.max_level and lvl > q.max_level then return false end
    if q.min_price and l.price < q.min_price then return false end
    if q.max_price and l.price > q.max_price then return false end
    if q.seller and q.seller ~= '' and l.seller ~= q.seller then return false end
    if q.not_seller and l.seller == q.not_seller then return false end
    if q.type and l.type ~= q.type then return false end
    return true
end

local SORTS = {
    price = function(l) return l.price end,
    ppl = function(l) return l.price / math.max(1, l.meta.level or 1) end,
    ends = function(l) return l.ends end,
    newest = function(l) return -l.created end,
    name = function(l) return lower(l.meta.name) end,
    level = function(l) return l.meta.level or 0 end,
    quality = function(l) return QUALITY_RANK[l.meta.quality] or 0 end,
}
M.SORTS = {'price', 'ppl', 'ends', 'newest', 'name', 'level', 'quality'}

function Market:row(l)
    local m = l.meta
    return {id = l.id, type = l.type, name = m.name, quality = m.quality, category = m.category, w = m.w, h = m.h,
        level = m.level, classes = m.classes, price = l.price, seller = l.seller_name, seller_key = l.seller,
        ends = l.ends, created = l.created, median = self:median_of(l.type)}
end

function Market:median_of(type_id)
    local prices = {}
    local sales = self.state.sales
    for i = #sales, 1, -1 do
        local s = sales[i]
        if s.type == type_id then
            prices[#prices + 1] = s.price
            if #prices >= 10 then break end
        end
    end
    return median(prices), #prices
end

function Market:browse(q, sort, desc, page)
    q = type(q) == 'table' and q or {}
    local now = self.deps.now()
    local list = {}
    for _, l in ipairs(self.state.listings) do
        if matches(l, q, now) then list[#list + 1] = l end
    end
    local key = SORTS[sort] or SORTS.price
    table.sort(list, function(a, b)
        local ka, kb = key(a), key(b)
        if ka == kb then return a.id < b.id end
        if desc then return ka > kb end
        return ka < kb
    end)
    local per = self.rules.page_size
    local pages = math.max(1, math.ceil(#list / per))
    page = math.max(1, math.min(int(page) or 1, pages))
    local rows = {}
    for i = (page - 1) * per + 1, math.min(#list, page * per) do rows[#rows + 1] = self:row(list[i]) end
    return {rows = rows, total = #list, page = page, pages = pages}
end

---------------------------------------------------------------------------------------------------------------
-- requests
---------------------------------------------------------------------------------------------------------------
local function result(rid, ok, err, extra)
    local r = {op = 'result', rid = rid, ok = ok, err = err}
    for k, v in pairs(extra or {}) do r[k] = v end
    return r
end

function Market:info(key)
    local mine, mail = 0, 0
    for _, l in ipairs(self.state.listings) do if l.seller == key then mine = mine + 1 end end
    local pending = {}
    for _, m in ipairs(self.state.mail) do
        if m.owner == key then
            mail = mail + 1
            if m.delivering then pending[#pending + 1] = m.delivering end
        end
    end
    local r = self.rules
    return {op = 'info', v = M.PROTOCOL, market = self.state.market_id, now = self.deps.now(), problem = self.problem,
        rules = {durations = r.durations, fee_per_day = r.fee_per_day, fee_min = r.fee_min, sale_cut = r.sale_cut,
            max_per_seller = r.max_per_seller, max_listings = r.max_listings, price_min = r.price_min,
            price_max = r.price_max, page_size = r.page_size},
        wallet = self:wallet(key), mine = mine, mail = mail, listings = #self.state.listings, pending = pending}
end

-- Checks the player's key and secret: the first request with a key binds the secret to it.
function Market:identify(from, msg)
    if from.verified then
        -- the server identified the player itself (no secret): its key and name are the truth
        local key, name = str(from.key, 80), str(from.name, 40) or from.key
        if not key or key == '' then return nil, 'no player identity' end
        local p = self.state.players[key]
        if not p then
            self:begin()
            self.state.players[key] = {name = name, first = self.deps.now(), seen = self.deps.now(), verified = true}
        elseif p.name ~= name then
            self:begin()
            p.name = name
        end
        return key, name
    end
    local key = from.key or str(msg.key, 80)
    if not key or key == '' then return nil, 'no player identity' end
    local secret = str(msg.secret, 80)
    if not secret or #secret < 8 then return nil, 'no player secret' end
    local p = self.state.players[key]
    local name = from.name or str(msg.name, 40) or key
    if not p then
        self:begin()
        self.state.players[key] = {name = name, secret = secret, first = self.deps.now(), seen = self.deps.now()}
        return key, name, true
    end
    if p.secret ~= secret then return nil, 'this hero name is taken by another player on this market' end
    if p.name ~= name then
        self:begin()
        p.name = name
    end
    return key, name
end

-- Handles one message from a player. `from`: {slot, key?, name?} (key and name as the receiving game sees that
-- player's hero; for a message from the local game the same). Answers are queued; call flush() after the tick.
function Market:handle(from, msg)
    if type(msg) ~= 'table' or type(msg.op) ~= 'string' then return end
    if self.writing then
        self.waiting[#self.waiting + 1] = {from = from, msg = msg}
        return
    end
    local slot = from.slot
    if self.problem then
        if msg.rid then
            self:reply(slot, result(msg.rid, false, self.problem))
        else
            self:reply(slot, {op = 'info', v = M.PROTOCOL, problem = self.problem, market = self.state.market_id})
        end
        return
    end
    local key, name = self:identify(from, msg)
    if not key then
        if msg.rid then self:reply(slot, result(msg.rid, false, name)) else
            self:reply(slot, {op = 'error', err = name, req = msg.req})
        end
        return
    end
    self:seen(key, slot)
    local fn = self['op_' .. msg.op]
    if not fn then
        self:reply(slot, {op = 'error', err = 'unknown request ' .. msg.op, req = msg.req})
        return
    end
    -- a changing request that was answered before gets the same answer again
    if msg.rid then
        local rid = str(msg.rid, 64)
        if not rid then return end
        local rec = self:rid_record(key, rid)
        if rec and rec.reply and msg.op ~= 'revoke' and msg.op ~= 'collected' then
            self:reply(slot, rec.reply, false)
            return
        end
    end
    fn(self, slot, key, name, msg)
end

function Market:op_hello(slot, key)
    self:reply(slot, self:info(key))
end

function Market:op_browse(slot, key, _, msg)
    local q = type(msg.q) == 'table' and msg.q or {}
    if q.mine then q.seller = key end
    local r = self:browse(q, msg.sort, msg.desc, msg.page)
    r.op, r.req = 'page', msg.req
    self:reply(slot, r)
end

function Market:op_item(slot, _, _, msg)
    local l = self:listing(int(msg.id) or -1)
    if not l then
        self:reply(slot, {op = 'item', id = msg.id, missing = true, req = msg.req})
        return
    end
    self:reply(slot, {op = 'item', id = l.id, data = l.data, req = msg.req})
end

function Market:op_history(slot, _, _, msg)
    local t = int(msg.type)
    local prices = {}
    local sales = self.state.sales
    for i = #sales, 1, -1 do
        if sales[i].type == t then
            prices[#prices + 1] = {price = sales[i].price, t = sales[i].t}
            if #prices >= 10 then break end
        end
    end
    local list = {}
    for i, p in ipairs(prices) do list[i] = p.price end
    self:reply(slot, {op = 'history', type = t, sales = prices, median = median(list), req = msg.req})
end

function Market:op_mail(slot, key, _, msg)
    local list = {}
    for _, m in ipairs(self.state.mail) do
        if m.owner == key then
            list[#list + 1] = {mid = m.mid, kind = m.kind, reason = m.reason, name = m.name, quality = m.quality,
                type = m.type, w = m.w, h = m.h, gold = m.gold, price = m.price, t = m.t, delivering = m.delivering}
        end
    end
    self:reply(slot, {op = 'mail', entries = list, wallet = self:wallet(key), req = msg.req})
end

function Market:op_mine(slot, key, _, msg)
    local rows = {}
    for _, l in ipairs(self.state.listings) do
        if l.seller == key then rows[#rows + 1] = self:row(l) end
    end
    table.sort(rows, function(a, b) return a.ends < b.ends end)
    self:reply(slot, {op = 'mine', rows = rows, wallet = self:wallet(key), req = msg.req})
end

local function clean_meta(meta, data_type)
    meta = type(meta) == 'table' and meta or {}
    local classes = {}
    if type(meta.classes) == 'table' then
        for _, c in ipairs(meta.classes) do
            if type(c) == 'string' and #c <= 20 then classes[#classes + 1] = c end
        end
    end
    local q = QUALITY_RANK[meta.quality] and meta.quality or 'normal'
    local name = trim(M.plain(meta.name))
    return {
        name = str(name ~= '' and name or nil, 80) or ('item ' .. tostring(data_type)),
        quality = q,
        category = str(meta.category, 20) or 'other',
        w = math.max(1, math.min(4, int(meta.w) or 1)),
        h = math.max(1, math.min(6, int(meta.h) or 1)),
        level = math.max(0, math.min(999, int(meta.level) or 0)),
        classes = classes,
        text = str(M.plain(meta.text), MAX_TEXT) or '',
    }
end

function Market:op_sell(slot, key, name, msg)
    local rid, r = msg.rid, self.rules
    local function refuse(err)
        self:begin()
        local rep = result(rid, false, err)
        self:remember(key, rid, {op = 'sell', reply = rep})
        self:reply(slot, rep, true)
    end
    if not rid then return self:reply(slot, {op = 'error', err = 'a sale needs a request id'}) end
    local rec = self:rid_record(key, rid)
    if rec and rec.revoked then return refuse('this sale was withdrawn') end
    if not valid_hex(msg.data) then return refuse('the item data is damaged') end
    local price, days = int(msg.price), int(msg.days)
    if not price or price < r.price_min or price > r.price_max then
        return refuse(('the price must be %d to %d gold'):format(r.price_min, r.price_max))
    end
    local okdays = false
    for _, d in ipairs(r.durations) do if d == days then okdays = true end end
    if not okdays then return refuse('that duration is not offered here') end
    if #self.state.listings >= r.max_listings then return refuse('the market is full') end
    if self:count_seller(key) >= r.max_per_seller then
        return refuse(('you have %d listings already, the most allowed'):format(r.max_per_seller))
    end
    local t = M.type_of(msg.data)
    local meta = clean_meta(msg.meta, t)
    if msg.meta and msg.meta.type and int(msg.meta.type) ~= t then return refuse('the item data does not match its type') end
    local fee = M.fee(r, price, days)
    if self:wallet(key) < fee then return refuse(('the listing fee is %d gold; your wallet has %d'):format(fee, self:wallet(key))) end
    self:begin()
    local s = self.state
    local now = self.deps.now()
    s.wallets[key] = self:wallet(key) - fee
    local l = {id = s.next_id, seller = key, seller_name = name, data = msg.data, type = t, meta = meta, price = price,
        created = now, ends = now + days * DAY, fee = fee, rid = rid}
    s.next_id = s.next_id + 1
    s.listings[#s.listings + 1] = l
    local rep = result(rid, true, nil, {id = l.id, fee = fee, wallet = s.wallets[key], ends = l.ends})
    self:remember(key, rid, {op = 'sell', reply = rep, listing = l.id})
    self:reply(slot, rep, true)
end

function Market:op_cancel(slot, key, _, msg)
    local rid = msg.rid
    if not rid then return end
    self:begin()
    local l, i = self:listing(int(msg.id) or -1)
    local rep
    if not l or l.seller ~= key then
        rep = result(rid, false, 'no such listing of yours (sold or expired already?)')
    else
        table.remove(self.state.listings, i)
        local m = self:add_mail(key, 'item', item_mail_fields(l, 'cancelled'))
        rep = result(rid, true, nil, {mid = m.mid})
    end
    self:remember(key, rid, {op = 'cancel', reply = rep})
    self:reply(slot, rep, true)
end

function Market:op_buy(slot, key, name, msg)
    local rid = msg.rid
    if not rid then return end
    self:begin()
    local s = self.state
    local l, i = self:listing(int(msg.id) or -1)
    local rep
    if not l or l.ends <= self.deps.now() then
        rep = result(rid, false, 'this item is no longer for sale')
    elseif l.seller == key then
        rep = result(rid, false, 'you cannot buy your own item')
    elseif int(msg.price) ~= l.price then
        rep = result(rid, false, 'the price is not what you saw')
    elseif self:wallet(key) < l.price then
        rep = result(rid, false, ('it costs %d gold; your wallet has %d'):format(l.price, self:wallet(key)))
    else
        table.remove(s.listings, i)
        s.wallets[key] = self:wallet(key) - l.price
        local net = l.price - M.cut(self.rules, l.price)
        s.wallets[l.seller] = math.min(self.rules.wallet_max, self:wallet(l.seller) + net)
        local m = self:add_mail(key, 'item', item_mail_fields(l, 'bought'))
        m.seller_name = l.seller_name
        s.sales[#s.sales + 1] = {type = l.type, name = l.meta.name, quality = l.meta.quality, price = l.price,
            t = self.deps.now(), seller = l.seller, buyer = key}
        while #s.sales > HISTORY do table.remove(s.sales, 1) end
        rep = result(rid, true, nil, {mid = m.mid, wallet = s.wallets[key]})
        self:notify(l.seller, {kind = 'sold', name = l.meta.name, price = l.price, net = net, buyer = name})
        self:add_log('sale', ('%s bought %s from %s for %d'):format(key, l.meta.name, l.seller, l.price))
    end
    self:remember(key, rid, {op = 'buy', reply = rep})
    self:reply(slot, rep, true)
end

function Market:op_deposit(slot, key, _, msg)
    local rid = msg.rid
    if not rid then return end
    self:begin()
    local gold = int(msg.gold)
    local rep
    local rec = self:rid_record(key, rid)
    if rec and rec.revoked then
        rep = result(rid, false, 'this deposit was withdrawn')
    elseif not gold or gold <= 0 then
        rep = result(rid, false, 'nothing to deposit')
    elseif self:wallet(key) + gold > self.rules.wallet_max then
        rep = result(rid, false, 'the wallet cannot hold that much')
    else
        self.state.wallets[key] = self:wallet(key) + gold
        rep = result(rid, true, nil, {wallet = self.state.wallets[key]})
    end
    self:remember(key, rid, {op = 'deposit', reply = rep, gold = rep.ok and gold or nil})
    self:reply(slot, rep, true)
end

-- Gold out of the wallet: it becomes a mailbox entry that is delivered at once (the same path as an item).
function Market:op_withdraw(slot, key, _, msg)
    local rid = msg.rid
    if not rid then return end
    self:begin()
    local gold = int(msg.gold)
    local rep
    if not gold or gold <= 0 then
        rep = result(rid, false, 'nothing to take out')
    elseif self:wallet(key) < gold then
        rep = result(rid, false, ('your wallet has %d gold'):format(self:wallet(key)))
    else
        self.state.wallets[key] = self:wallet(key) - gold
        local m = self:add_mail(key, 'gold', {reason = 'withdrawn', gold = gold, name = 'Gold'})
        m.delivering = rid
        rep = {op = 'deliver', rid = rid, mid = m.mid, kind = 'gold', gold = gold, wallet = self.state.wallets[key]}
        self:remember(key, rid, {op = 'withdraw', reply = rep, pending = true, mid = m.mid})
        self:reply(slot, rep, true)
        return
    end
    self:remember(key, rid, {op = 'withdraw', reply = rep})
    self:reply(slot, rep, true)
end

-- A mailbox entry to the player: reserved for this rid until `collected`.
function Market:op_collect(slot, key, _, msg)
    local rid = msg.rid
    if not rid then return end
    local m = self:mail_entry(int(msg.mid) or -1)
    if not m or m.owner ~= key then
        self:begin()
        local rep = result(rid, false, 'nothing like that in your mailbox')
        self:remember(key, rid, {op = 'collect', reply = rep})
        return self:reply(slot, rep, true)
    end
    if m.delivering and m.delivering ~= rid then
        -- already on its way under another request id (an earlier attempt): hand out that delivery again
        local rec = self:rid_record(key, m.delivering)
        if rec and rec.reply then return self:reply(slot, rec.reply, false) end
    end
    self:begin()
    m.delivering = rid
    local rep = {op = 'deliver', rid = rid, mid = m.mid, kind = m.kind, data = m.data, gold = m.gold, name = m.name}
    self:remember(key, rid, {op = 'collect', reply = rep, pending = true, mid = m.mid})
    self:reply(slot, rep, true)
end

-- The player has the delivery: the entry goes.
function Market:op_collected(slot, key, _, msg)
    local rid = str(msg.rid, 64)
    if not rid then return end
    local rec = self:rid_record(key, rid)
    local done = false
    for i, m in ipairs(self.state.mail) do
        if m.owner == key and m.delivering == rid then
            self:begin()
            table.remove(self.state.mail, i)
            done = true
            break
        end
    end
    if rec and rec.pending then
        self:begin()
        rec.pending = nil
    end
    self:reply(slot, {op = 'collected', rid = rid, ok = true, removed = done, wallet = self:wallet(key)}, done)
end

-- The client found, at a game load, that a sale or deposit never reached its savegame (the item is back in the
-- inventory, or the gold): the listing is taken off (its item is dropped: the savegame has it) or the gold taken
-- back out of the wallet. An unknown rid is remembered as withdrawn so a late request with it is refused.
function Market:op_revoke(slot, key, _, msg)
    local rid = str(msg.rid, 64)
    if not rid then return end
    self:begin()
    local rec = self:rid_record(key, rid)
    local what
    if not rec then
        self:remember(key, rid, {op = msg.what or 'sell', revoked = true})
        what = 'unknown'
    elseif rec.revoked then
        what = 'again'
    elseif rec.op == 'sell' and rec.listing then
        local l, i = self:listing(rec.listing)
        if l then
            table.remove(self.state.listings, i)
            what = 'listing removed'
        else
            what = 'already sold or expired'
            self:add_log('duplicate', ('%s: sale %s (listing %d) was not in the savegame but is gone already')
                :format(key, rid, rec.listing))
        end
        rec.revoked = true
    elseif rec.op == 'deposit' and rec.gold then
        self.state.wallets[key] = self:wallet(key) - rec.gold
        what = 'gold taken back'
        rec.revoked = true
    else
        rec.revoked = true
        what = 'nothing to undo'
    end
    self:add_log('revoke', ('%s %s: %s'):format(key, rid, what))
    self:reply(slot, {op = 'revoked', rid = rid, what = what, wallet = self:wallet(key)}, true)
end

-- A delivery the player's hero never got (a server that keeps the heroes found it missing after a reconnect, and no save
-- of the hero can hold it): the mailbox entry is free again, the next `collect` delivers it anew. The rid's answer is
-- forgotten, so the old rid gets a fresh answer. True when an entry was released. Starts a batch: flush() afterwards.
function Market:release(key, rid)
    local released = false
    for _, m in ipairs(self.state.mail) do
        if m.owner == key and m.delivering == rid then
            self:begin()
            m.delivering = nil
            released = true
        end
    end
    local rec = self:rid_record(key, rid)
    if rec then
        self:begin()
        self.state.rids[key][rid] = nil
    end
    if released then self:add_log('release', ('%s %s: delivery released'):format(key, rid)) end
    return released
end

-- The mailbox entry a delivery under `rid` is reserved for, or nil.
function Market:delivery(key, rid)
    for _, m in ipairs(self.state.mail) do
        if m.owner == key and m.delivering == rid then return m end
    end
end

-- For the host's own view (and tests).
function Market:stats()
    local s = self.state
    local gold = 0
    for _, v in pairs(s.wallets) do gold = gold + v end
    return {listings = #s.listings, mail = #s.mail, wallets = gold, sales = #s.sales}
end

return M
end)()
-- <<< core.lua

-- ---- storage: the market state in chunks (one stored value must stay below 4 MB) -----------------------------------------
local CHUNK = 25
local SPLIT = { listings = true, mail = true, rids = true }

local function put_list(prefix, list, old_count)
    local n = 0
    for i = 1, #list, CHUNK do
        n = n + 1
        local part = {}
        for j = i, math.min(#list, i + CHUNK - 1) do part[#part + 1] = list[j] end
        server.save(prefix .. "." .. n, part)
    end
    for i = n + 1, old_count or 0 do server.delete(prefix .. "." .. i) end
    return n
end

local function get_list(prefix, count)
    local list = {}
    for i = 1, count or 0 do
        for _, v in ipairs(server.load(prefix .. "." .. i) or {}) do list[#list + 1] = v end
    end
    return list
end

local chunks = (server.load("market") or {})._chunks or {}

local function store_state(state)
    local top = {}
    for k, v in pairs(state) do if not SPLIT[k] then top[k] = v end end
    local rids = {}
    for key, r in pairs(state.rids) do for rid, rec in pairs(r) do rids[#rids + 1] = { k = key, r = rid, v = rec } end end
    local counts = {
        listings = put_list("listings", state.listings, chunks.listings),
        mail = put_list("mail", state.mail, chunks.mail),
        rids = put_list("rids", rids, chunks.rids),
    }
    top._chunks = counts
    server.save("market", top)
    chunks = counts
end

local function load_state()
    local top = server.load("market")
    if top == nil then return nil end
    local c = top._chunks or {}
    top._chunks = nil
    top.listings = get_list("listings", c.listings)
    top.mail = get_list("mail", c.mail)
    top.rids = {}
    for _, e in ipairs(get_list("rids", c.rids)) do
        top.rids[e.k] = top.rids[e.k] or {}
        top.rids[e.k][e.r] = e.v
    end
    return top
end

-- ---- the market ----------------------------------------------------------------------------------------------------------
local ch = net.channel(CHANNEL, { server_only = true })
local market

local function hex_id()
    return string.format("%08x%08x", server.random(0, 0x7FFFFFF), server.random(0, 0x7FFFFFF))
end

local function plain_number(n) return string.format("%d", math.floor(n or 0)) end

local NOTICES = {
    sold = function(m) return "Auction house: " .. tostring(m.name) .. " sold to " .. tostring(m.buyer) .. " for " .. plain_number(m.price) .. " gold ("
        .. plain_number(m.net) .. " after the cut), in your wallet." end,
    expired = function(m) return "Auction house: " .. tostring(m.name) .. " did not sell; it is back in your mailbox." end,
}

local gold_marks = {}       -- uid .. " " .. rid -> the hero's gold (server view) when a gold delivery went out (Closed Net)

local function send(slot, msg)
    if msg.op == "info" then
        msg.where = "server"
        msg.authoritative = server.authoritative(slot) or nil
    end
    if msg.op == "deliver" and msg.kind == "gold" and server.authoritative(slot) then
        local p = server.player(slot)
        local key = p and (p.uid .. " " .. tostring(msg.rid))
        if key and gold_marks[key] == nil then
            local hero = server.hero(slot)
            gold_marks[key] = hero and hero.gold or false
        end
    end
    if msg.op == "notify" and config.chat_notices and NOTICES[msg.kind] then server.message(slot, NOTICES[msg.kind](msg)) end
    if ch and net.supports(slot) then ch:send(slot, msg) end
end

market = core.new({
    now = function() return math.floor(server.time()) end,
    load = load_state,
    save_async = function(state, done)
        local ok, err = pcall(store_state, state)
        if not ok then return done(nil, err) end
        server.flush(function(written, why) done(written, why) end)
    end,
    rolled_back = function(state)
        local ok, err = pcall(store_state, state)
        if not ok then server.log("auction: the undone state could not be stored: " .. tostring(err)) end
    end,
    send = send,
    log = function(text) server.log("auction: " .. text) end,
    random_id = hex_id,
}, {
    durations = config.durations, fee_per_day = config.fee_per_day, fee_min = config.fee_min, sale_cut = config.sale_cut,
    max_per_seller = config.max_per_seller, max_listings = config.max_listings, price_min = config.price_min, price_max = config.price_max,
})
if market.problem then server.log("auction: " .. market.problem) end

local function from_of(player) return { slot = player.slot, key = player.uid, name = player.name, verified = true } end

-- ---- Closed Net: the server sees items and gold leave and arrive, and saves the hero (DESIGN.md, section 4) -----------------
local sessions = {}         -- uid -> server time the hero joined (this run of the server)
local pending = {}          -- uid .. " " .. rid -> a request waiting for the server to see something

local function entries_hex(data)
    -- gear.inv.export: u32 type, u16 entry count, the 130-byte value records, ...: the records as the server keeps them
    if type(data) ~= "string" or #data < 12 then return nil end
    local lo, hi = tonumber(data:sub(9, 10), 16), tonumber(data:sub(11, 12), 16)
    if not lo or not hi then return nil end
    local count = lo + hi * 256
    if count < 1 or count > 9 or #data < 12 + count * 260 then return nil end
    return data:sub(13, 12 + count * 260)
end

local function in_hero(where) return where == "backpack" or where == "stash" or where == "equipped" end

local function refuse(slot, rid, err, keep)
    send(slot, { op = "result", rid = rid, ok = false, err = err, keep = keep or nil })
end

local function to_core(player, msg)
    market:handle(from_of(player), msg)
    market:flush()
end

-- After the check: the lobby stores the hero, then the request goes to the market.
local function save_then(player, entry, on_saved, on_failed)
    entry.stage = "saving"
    server.save_hero(player, function(ok, err)
        if pending[entry.key] ~= entry then return end
        pending[entry.key] = nil
        if ok then on_saved() else on_failed(err) end
    end)
end

local CHECKS = {}

-- sell: the item (msg.ref) left the hero: destroyed by its owner, the same records as the listing's data.
function CHECKS.sell(e, player, now)
    local item = server.hero_item(player, e.msg.ref)
    local want = entries_hex(e.msg.data)
    if item and item.where == "gone" then
        if item.type ~= core.type_of(e.msg.data) or item.records ~= want then
            pending[e.key] = nil
            server.log("auction: " .. player.name .. ": a sale whose item data is not the item that left the hero (ref " .. tostring(e.msg.ref) .. ")")
            return refuse(player.slot, e.msg.rid, "the item data does not match the item that left your inventory")
        end
        server.claim_item(player, e.msg.ref)
        return save_then(player, e, function() to_core(player, e.msg) end,
            function(err) refuse(player.slot, e.msg.rid, "your hero could not be saved (" .. tostring(err) .. "); the item is yours again") end)
    end
    if now < e.deadline then return end
    pending[e.key] = nil
    if item and item.where ~= "gone" then
        -- the server still has the item (in the hero, or lying in the world): the client must not make a second one
        return refuse(player.slot, e.msg.rid, "the server still sees the item (" .. item.where .. "); it is yours there, rejoin to see it", true)
    end
    refuse(player.slot, e.msg.rid, "the server did not see the item leave your inventory")
end

-- deposit: the hero's gold (server view) went down by exactly the amount since the prepare.
function CHECKS.deposit(e, player, now)
    local hero = server.hero(player)
    if hero and e.mark and hero.gold == e.mark - e.msg.gold then
        return save_then(player, e, function() to_core(player, e.msg) end,
            function(err) refuse(player.slot, e.msg.rid, "your hero could not be saved (" .. tostring(err) .. "); the gold is yours again") end)
    end
    if now < e.deadline then return end
    pending[e.key] = nil
    refuse(player.slot, e.msg.rid, "the server did not see the gold leave your hero")
end

-- collected: the delivered item (msg.ref) is in the hero with the delivery's records, or the hero's gold rose by the amount.
function CHECKS.collected(e, player, now)
    local m = e.entry
    local arrived
    if m.kind == "gold" then
        local hero = server.hero(player)
        local mark = gold_marks[player.uid .. " " .. e.msg.rid]
        arrived = hero and mark and hero.gold >= mark + (m.gold or 0)
    else
        local item = e.msg.ref and server.hero_item(player, e.msg.ref)
        arrived = item and in_hero(item.where) and item.records == entries_hex(m.data)
    end
    if arrived then
        return save_then(player, e, function()
            gold_marks[player.uid .. " " .. e.msg.rid] = nil
            to_core(player, { op = "collected", rid = e.msg.rid })
        end, function(err) send(player.slot, { op = "collected", rid = e.msg.rid, ok = false, retry = true, err = "your hero could not be saved: " .. tostring(err) }) end)
    end
    if now < e.deadline then return end
    pending[e.key] = nil
    local rec = market:rid_record(player.uid, e.msg.rid)
    local delivered = rec and rec.t or 0
    if delivered >= (sessions[player.uid] or 0) then
        -- this session: not seen yet; the client asks again
        return send(player.slot, { op = "collected", rid = e.msg.rid, ok = false, retry = true, err = "the server has not seen it arrive yet" })
    end
    -- handed out in an earlier session (before a crash or a reconnect)
    local saved = server.hero_upload_time(player)
    if saved == nil or saved < delivered then
        -- no save of this hero went out since: the lobby's hero cannot have it. Deliver it again.
        market:release(player.uid, e.msg.rid)
        market:flush()
        return send(player.slot, { op = "collected", rid = e.msg.rid, ok = false, released = true, err = "it never reached your saved hero; collect it again" })
    end
    if m.kind ~= "gold" then
        local want = entries_hex(m.data)
        for _, item in ipairs(server.hero_items(player)) do
            if item.records == want then
                -- the saved hero has it (it came back with the hero): the entry goes
                return to_core(player, { op = "collected", rid = e.msg.rid })
            end
        end
    end
    server.log("auction: " .. player.name .. ": delivery " .. tostring(e.msg.rid) .. " cannot be confirmed (saved since, not in the hero); it stays reserved")
    send(player.slot, { op = "collected", rid = e.msg.rid, ok = false, stuck = true,
        err = "the server cannot tell whether your saved hero got this; it stays reserved for you (ask the server owner)" })
end

local function closed_net(player, msg)
    local key = player.uid .. " " .. tostring(msg.rid)
    local op = msg.op
    if op == "revoke" then
        -- the server's saves are the truth: a sale or deposit was only accepted after the hero was saved without it
        return send(player.slot, { op = "revoked", rid = msg.rid, what = "the server keeps your hero; nothing to undo" })
    end
    if not msg.rid or type(msg.rid) ~= "string" then return to_core(player, msg) end
    if op == "prepare" then
        local done = market:rid_record(player.uid, msg.rid)
        if done and done.reply then return send(player.slot, done.reply) end
        local hero = server.hero(player)
        if not hero then return refuse(player.slot, msg.rid, "the server has no view of your hero yet; try again in a moment") end
        pending[key] = { key = key, kind = "prepared", mark = hero.gold, gold = msg.gold, deadline = server.time() + config.verify_seconds * 4 }
        return send(player.slot, { op = "prepared", rid = msg.rid, gold = msg.gold })
    end
    if op ~= "sell" and op ~= "deposit" and op ~= "collected" then return to_core(player, msg) end
    local done = market:rid_record(player.uid, msg.rid)
    if op ~= "collected" and done and done.reply then return to_core(player, msg) end       -- answered before: the same answer
    local busy = pending[key]
    if busy and busy.kind ~= "prepared" then return end                                   -- already being checked
    local now = server.time()
    if op == "sell" then
        if type(msg.ref) ~= "number" then return refuse(player.slot, msg.rid, "this server needs the item's id (update the auction-house mod)") end
        pending[key] = { key = key, kind = "sell", msg = msg, deadline = now + config.verify_seconds }
    elseif op == "deposit" then
        if not busy or type(msg.gold) ~= "number" or busy.gold ~= msg.gold then
            return refuse(player.slot, msg.rid, "the server did not see this deposit prepared; nothing changed")
        end
        pending[key] = { key = key, kind = "deposit", msg = msg, mark = busy.mark, deadline = now + config.verify_seconds }
    else
        local entry = market:delivery(player.uid, msg.rid)
        if not entry then return to_core(player, msg) end                                 -- confirmed before, or nothing reserved
        pending[key] = { key = key, kind = "collected", msg = msg, entry = entry, deadline = now + config.verify_seconds }
    end
    CHECKS[pending[key].kind](pending[key], player, now)
end

server.every(0.5, function()
    local now = server.time()
    for key, e in pairs(pending) do
        local uid = key:match("^(%S+)")
        local player
        for _, p in ipairs(server.players()) do if p.uid == uid then player = p break end end
        if not player then
            pending[key] = nil
        elseif e.kind == "prepared" then
            if now > e.deadline then pending[key] = nil end
        elseif e.stage ~= "saving" then
            CHECKS[e.kind](e, player, now)
        end
    end
end)

-- ---- players --------------------------------------------------------------------------------------------------------------
if ch then
    ch:on(function(player, msg)
        if type(msg) ~= "table" or type(msg.op) ~= "string" then return end
        if server.authoritative(player) then return closed_net(player, msg) end
        to_core(player, msg)
    end)
end

local function arrived(player)
    if not sessions[player.uid] then sessions[player.uid] = math.floor(server.time()) end
    market:seen(player.uid, player.slot)
end
server.on("join", function(player) sessions[player.uid] = math.floor(server.time()); market:seen(player.uid, player.slot) end)
server.on("enter", arrived)
server.on("leave", function(player)
    market:left(player.slot)
    sessions[player.uid] = nil
    for key in pairs(pending) do if key:sub(1, #player.uid + 1) == player.uid .. " " then pending[key] = nil end end
end)
for _, p in ipairs(server.players()) do arrived(p) end

server.every(config.sweep_seconds, function() market:sweep(true); market:flush() end)
server.on("start", function() market:sweep(true); market:flush() end)

-- ---- web: the page and JSON (character names only) -------------------------------------------------------------------------
local function public_row(l, now)
    local m = l.meta or {}
    return { id = l.id, name = m.name, quality = m.quality, category = m.category, level = m.level or 0, price = l.price,
        seller = l.seller_name, ends = l.ends, left = math.max(0, l.ends - now), created = l.created, median = (market:median_of(l.type)) }
end

local function listings(now)
    local rows = {}
    for _, l in ipairs(market.state.listings) do if l.ends > now then rows[#rows + 1] = public_row(l, now) end end
    table.sort(rows, function(a, b) if a.ends == b.ends then return a.id < b.id end return a.ends < b.ends end)
    return rows
end

local function name_of(key)
    local p = market.state.players[key]
    return p and p.name or "unknown hero"
end

local function recent_sales(n)
    local rows, sales = {}, market.state.sales
    for i = #sales, math.max(1, #sales - n + 1), -1 do
        local s = sales[i]
        rows[#rows + 1] = { name = s.name, quality = s.quality, price = s.price, t = s.t, seller = name_of(s.seller), buyer = name_of(s.buyer) }
    end
    return rows
end

server.endpoint("listings", function() return listings(math.floor(server.time())) end,
    { public = config.page_public, description = "current auctions: name, quality, level, price, seller, time left (JSON)" })
server.endpoint("recent-sales", function(request)
    local n = math.max(1, math.min(200, tonumber(request.query.n) or config.recent_sales))
    return recent_sales(n)
end, { public = config.page_public, description = "the last sales (JSON)" })

local PAGE = [[<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Auction house</title><style>
body{font:14px/1.4 system-ui,sans-serif;margin:0;padding:16px 20px;background:#15120e;color:#e8dcc4}
h1{font-size:20px;margin:0 0 10px}h2{font-size:15px;margin:22px 0 6px}
.bar{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:10px}
input,select{background:#241e17;color:#e8dcc4;border:1px solid #4a3e2e;border-radius:4px;padding:4px 6px}
table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:4px 8px;border-bottom:1px solid #2c2419}
th{cursor:pointer;user-select:none;color:#bfae8c;font-weight:600}th.on::after{content:attr(data-dir)}
td.n{text-align:right;font-variant-numeric:tabular-nums}.dim{color:#8f8168}
.normal{color:#ffffff}.magic{color:#6f8cff}.rare{color:#ffe14a}.unique{color:#ff9d2e}.set{color:#3ad13a}
</style></head><body><h1>Auction house</h1>
<div class="bar"><input id="q" type="search" placeholder="search items and sellers" size="30">
<select id="qu"><option value="">every quality</option><option>normal</option><option>magic</option><option>rare</option><option>unique</option><option>set</option></select>
<span class="dim" id="n"></span></div>
<table><thead><tr><th data-k="name">Item</th><th data-k="level">Level</th><th data-k="price">Price</th><th data-k="median">Recent median</th><th data-k="seller">Seller</th><th data-k="left">Time left</th></tr></thead><tbody id="rows"></tbody></table>
<h2>Recent sales</h2><table><thead><tr><th>Item</th><th>Price</th><th>Seller</th><th>Buyer</th><th>When</th></tr></thead><tbody id="sales"></tbody></table>
<script>var ROWS={{json rows}},SALES={{json sales}},NOW={{json now}};</script>
<script>
var key='left',dir=1,q=document.getElementById('q'),qu=document.getElementById('qu');
function el(t,c,x){var e=document.createElement(t);if(c)e.className=c;if(x!=null)e.textContent=x;return e}
function gold(n){return n==null?'':String(n).replace(/\B(?=(\d{3})+(?!\d))/g,',')}
function left(s){if(s<3600)return Math.max(1,Math.round(s/60))+' min';if(s<172800)return Math.round(s/3600)+' h';return Math.round(s/86400)+' days'}
function ago(t){return left(Math.max(60,NOW-t))+' ago'}
function draw(){var b=document.getElementById('rows'),t=q.value.toLowerCase().split(/\s+/).filter(Boolean),k=qu.value,list=[];
b.textContent='';for(var i=0;i<ROWS.length;i++){var r=ROWS[i],h=((r.name||'')+' '+(r.seller||'')).toLowerCase(),ok=!k||r.quality===k;
for(var j=0;ok&&j<t.length;j++)if(h.indexOf(t[j])<0)ok=false;if(ok)list.push(r)}
list.sort(function(a,c){var x=a[key],y=c[key];if(x==null)x='';if(y==null)y='';if(typeof x==='string'){x=x.toLowerCase();y=String(y).toLowerCase()}return x<y?-dir:x>y?dir:a.id-c.id});
for(i=0;i<list.length;i++){r=list[i];var tr=el('tr');tr.appendChild(el('td',r.quality||'normal',r.name));tr.appendChild(el('td','n',r.level));
tr.appendChild(el('td','n',gold(r.price)));tr.appendChild(el('td','n dim',gold(r.median)));tr.appendChild(el('td','',r.seller));
tr.appendChild(el('td','n',left(r.left)));b.appendChild(tr)}
if(!list.length){tr=el('tr');var td=el('td','dim',ROWS.length?'Nothing matches.':'Nothing for sale right now. Players put items up from a chest in the game (Sacred Gear mod auction-house).');td.colSpan=6;tr.appendChild(td);b.appendChild(tr)}
document.getElementById('n').textContent=list.length+' of '+ROWS.length;
var ths=document.querySelectorAll('th[data-k]');for(i=0;i<ths.length;i++){ths[i].className=ths[i].getAttribute('data-k')===key?'on':'';ths[i].setAttribute('data-dir',dir>0?' ▲':' ▼')}}
var ths=document.querySelectorAll('th[data-k]');for(var i=0;i<ths.length;i++)ths[i].onclick=function(){var k=this.getAttribute('data-k');if(k===key)dir=-dir;else{key=k;dir=1}draw()};
q.oninput=draw;qu.onchange=draw;draw();
var s=document.getElementById('sales');for(i=0;i<SALES.length;i++){var r=SALES[i],tr=el('tr');tr.appendChild(el('td',r.quality||'normal',r.name));
tr.appendChild(el('td','n',gold(r.price)));tr.appendChild(el('td','',r.seller));tr.appendChild(el('td','',r.buyer));tr.appendChild(el('td','dim',ago(r.t)));s.appendChild(tr)}
if(!SALES.length){tr=el('tr');td=el('td','dim','No sales yet.');td.colSpan=5;tr.appendChild(td);s.appendChild(tr)}
</script></body></html>]]

server.page("", function()
    local now = math.floor(server.time())
    return server.render(PAGE, { rows = listings(now), sales = recent_sales(config.recent_sales), now = now })
end, { public = config.page_public, title = "Auction house" })
