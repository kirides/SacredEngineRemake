# GameServer web UI

A management page and JSON API for `SacredGameServer`, off by default.

```
SacredGameServer --webui-enabled --webui-port=8080 --webui-password=secret --game-dir=/srv/sacred
```

Open `http://127.0.0.1:8080/`: overview, players (kick, ban), live chat, broadcast and private messages from the server,
session settings (lock, pause, max players, name, password, game mode and level before the world exists), end session,
shutdown, ban list. These are the controls of the original GameServer.exe window (its menu: network/lobby, game mode, level,
password, max players, lock) plus the chat and moderation the original never had.

## Configuration (`WebUi:*`)

.NET configuration, lowest to highest priority: environment variables (`SACRED_GS_WebUi__Port=8080`), JSON file (`webui.json`
in the working directory, or `--webui-config=path`), command line (`--WebUi:Port=8080`, `--WebUi:Port 8080`, `--webui-port=8080`).

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | start the UI |
| `Hostname` | `127.0.0.1` | bind address or name (`localhost`, a DNS name, `*`/`0.0.0.0` for all interfaces) |
| `Port` | `8080` | TCP port |
| `Password` | none | HTTP Basic (any user) or `Authorization: Bearer <password>`. **Required** when `Hostname` is not loopback |
| `CorsOrigins` | none | origins allowed to call the API from a browser; `*` allows all |
| `FrameAncestors` | none | who may embed the page in a frame: `self`, `*` or origins (CSP `frame-ancestors`; default `none`) |
| `AllowedHosts` | none | accepted `Host` headers; on password-less loopback only localhost/127.0.0.1/the hostname are accepted (DNS rebinding) |
| `StaticDirectory` | embedded page | serve `index.html` from this directory instead (custom pages, embedding into a site) |
| `PathBase` | none | URL prefix behind a reverse proxy |

Embedding the page on another site: `https://panel.example/gs.html` can load `index.html?api=https://gs.example:8080&token=PASSWORD`
(list the origin in `CorsOrigins`) or frame it (list it in `FrameAncestors`). The token is kept in `sessionStorage` and removed from the URL.

## API

`GET /api/overview | players | bans`, `GET /api/chat?after=ID&wait=SECONDS` (long poll, max 25 s),
`POST /api/{broadcast|message|kick|ban|unban|lock|pause|max-players|password|name|mode|end-session|shutdown}` with a JSON body
(`slot`, `value`, `text`, `reason`, `address`). POST needs `Content-Type: application/json`.

## Design

* `Sacred.GameServer.Web` only knows `IServerAdmin`; `GameServerHost` implements it (`Session/GameServerHost.Admin.cs`).
* The game loop stays the only writer: commands go through a bounded queue and run in the tick; reads come from a snapshot
  refreshed four times a second; chat goes into a preallocated ring of char buffers. No allocation on the packet path.
* Kestrel with `CreateSlimBuilder`, one request delegate, source generated JSON: trim/AOT clean (no ILC warnings).
* Bans are by IP address, kept in memory (they are lost on restart). A banned address is refused at FIRSTCONTACT (reason 3).
* Server messages use origin slot 0 like the server's own frames; how the stock client labels them is unverified.
