# Server mods (Lua) for Sacred.GameServer

Ready-made Lua scripts in `Sacred.GameServer/scripts-examples/`. Each one is a "mod": one `.lua` file with its own storage,
its own chat commands, and (most of them) a web page and JSON endpoints. Some also talk to the Sacred Gear client mods
(the `sacred-bild` repository, `gear/examples/`).

| Mod (script) | What it does | Page | Chat | Gear client mod | Doc |
|---|---|---|---|---|---|
| `bounty_board.lua` | A rotating board of "kill N creatures" bounties paid in gold and/or an item | `/mods/bounty_board/` | `!bounty` `!where` | `bounty` | [bounty-board.md](bounty-board.md) |
| `achievements.lua` | 31 achievements and a daily login streak, with rewards | `/mods/achievements/` | `!ach` `!streak` | `achievements` | [achievements.md](achievements.md) |
| `build_sharer.lua` | Players publish their hero build, everybody browses and searches them | `/mods/build_sharer/` | `!sharebuild` `!unsharebuild` `!builds` | `buildsharer` | [build-sharer.md](build-sharer.md) |
| `quest_helper.lua` | A hero's quest records with an owner-written catalog of hints | `/mods/quest_helper/` | `!quests` | `questhelper` | [quest-helper.md](quest-helper.md) |
| `rare_drops.lua` | Live feed of rare and unique drops | `/mods/rare_drops/` | | `serverfeed` | [feeds.md](feeds.md) |
| `graveyard.lua` | Recent hero deaths with the killer, deadliest creatures | `/mods/graveyard/` | `!rip` | `serverfeed` | [feeds.md](feeds.md) |
| `server_stats.lua` | Online players, peak, uptime, totals, 24 h charts | `/mods/server_stats/` | | | [statistics.md](statistics.md) |
| `hall_of_fame.lua` | All-time records, first to reach a level, first to defeat a creature | `/mods/hall_of_fame/` | `!hof` | | [statistics.md](statistics.md) |
| `seasons.lua` | Ladders per season with an archive | `/mods/seasons/` | `!season` | | [statistics.md](statistics.md) |
| `ladders.lua` | The original ladder example (kills, level, time online) | `/mods/ladders/` | `!top` | | [statistics.md](statistics.md) |

The client half of the Gear mods is documented in the client repository: `docs/GearServerMods.md` (section "Server mods
and their Gear client mods") and `docs/GearSessionMods.md`.

## Install (all of them)

1. Copy the `.lua` file(s) from `Sacred.GameServer/scripts-examples/` into the server's scripts folder (`--scripts-dir`,
   default `./scripts`). Scripts are loaded in name order at start and reloaded half a second after any file changes, so a
   running server needs no restart. `--scripts=0` turns all scripts off.
2. Adjust the settings at the top of the file if you like (every mod has plain `local` tables or constants there; there is no
   separate config file). A changed file is reloaded at once.
3. The mod's data goes to `script-data/<mod>.json` (folder: `--scripts-data`). Delete the file to reset a mod; keep it when you
   move the server. Limits: 8 MB per mod, 32 MB for all (`--scripts-store-max`, `--scripts-store-total-max`).

## Web pages

Pages and endpoints need the web UI (`--webserver=1`, port `--webui-port`, address `--webui-hostname`, default loopback; a
password is required when it is not bound to loopback). They live under `/mods/<mod>/...` (with the admin password, if one is
set). With `WebUi:PublicApi=true` (`--webui-public-api`) the same pages and JSON/CSV endpoints are also served to anybody under
`/public/mods/<mod>/...` (read-only, CORS `*`). Only **character names** are shown: no account names, slots or player ids.
Every player-made text passes `server.html` (escaped). Pages are sent with a sandboxing Content-Security-Policy, so a page
can run its own inline script but cannot load anything from elsewhere. Admin-only endpoints (for example `seasons/end-season`)
are never public.

The server lists every mod page and endpoint in the "Mods" card of the web UI and under `GET /api/mods` (`/public/mods` for
the public ones).

## Other things the mods need

| Need | Mods | Why |
|---|---|---|
| `--game-dir` (the game's folder, for names and item rewards) | `bounty_board` (`names` filters, item rewards), `achievements` (item rewards), `graveyard` (killer names), `rare_drops` (item names), `build_sharer` (skill and item names) | Without it creatures and items have no names: use `types` (bounty board) or expect "creature type N" |
| Hero data on the server (`server.hero`): web UI running, or `--authoritative` | `build_sharer`, `quest_helper` | They read skills, equipment and quest pairs from the server's own copy of the hero |
| Custom net messages (`NETOPS`, on by default) and a Sacred Gear client | the Gear client mods | The original `GameServer.exe` never answers them. Without them each mod still works through chat and the web page |

## Screenshots

The screenshots in [img/](img/) were taken from the real pages served by a built server (loopback only) with **made-up demo
data** (fictional hero names such as Thornwick and Lyra Dawnbrook, invented numbers): the mods' storage files were written
by a script, no real player or game session is shown. "Online now: 0" is real: nobody was connected. The Gear client panels
need the game itself and are **not** pictured anywhere; their layout is described in the client repository's docs.

## Known limits (all mods)

* No script event exists for gold, exploration, quests, items found or level changes, so the mods that would like them read
  what exists (player level at kills, joins and timers). See each mod's page for what it does not cover.
* Rewards are dropped next to the hero with `server.drop_gold` / `server.drop_item`. Unverified with a real client: that the
  owner of the sector can pick up what is dropped (see the server README).
* The mods are examples with simple storage (JSON files, no moderation tools besides editing the file). Edit the Lua file to
  change the rules.
