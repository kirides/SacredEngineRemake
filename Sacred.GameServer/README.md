# Sacred.GameServer

Headless re-creation of Sacred's `GameServer.exe` (dedicated server) as a .NET 10 console app, intended to run on a cheap
Linux VPS. Native AOT + full trimming (`dotnet publish -c Release -r linux-x64` gives a ~2.3 MB single binary, no IL warnings).

New project only: no existing file of the repository is modified. It reuses the verified layouts/codecs of `Sacred.Network`
(FIRSTCONTACT layout, data fingerprints, build reader). The `Sacred.Network` *client* classes allocate per frame, so the wire
path here is separate and allocation free.

## Status (stage 3: session flow, routing, lobby)

| Area | State |
| --- | --- |
| TinCat login / keep-alive / logoff | done, verified against `Sacred.Network` client |
| Gold envelope + messages (raw + zlib) | done, byte-compatible with `Sacred.Network.Protocol`; own zero-alloc inflate/deflate |
| FIRSTCONTACT admission | done: version check, lobby identity duplicate check, 25 data fingerprints with mismatch mask, slot 1..16, host role to first player, 0x11 reply `[flags][slot][token]` (flag 1 host, 4 host world available), 0x1E game flags |
| Rejection 0x3F reasons | 1 version, 2 duplicate identity (only while the lobby is connected, like `FUN_004eb0a0`), 3 refused/locked/full, 4 bad sender, 5 sequence, 6 checksum **or** data mismatch (from `FUN_004eb0a0`) |
| Game password (0x12 -> 0x13/0x14) | done; `MaxPasswordTries` (default 3) is **not** verified against the original |
| Chat 0x003 private / 0x005 public | done (`0x4CA8AA` / `0x4CA771`), public chat honours nothing but sender exclusion; team chat 0x004 **not yet** |
| Host commands 0x019/0x01A lock, 0x01E flags, 0x023 max players, 0x156/0x157 kick | done, host only (others get 0x009 value 0x64); flags merge `(new & 0xFFFE8F72) \| (cur & 0x1708D)` |
| World start 0x017/0x018, world banks 0x1B1..0x1B4 -> 0x1B5 | done as relay: host uploads are stored and served in the original order 0x1B2, 0x1B4, 0x1B3, 0x1B1 |
| Time probes 0x041/0x043/0x044/0x045, pause 0x134/0x135 | done |
| Leave / kick (`FUN_004d6ac0`) | done: 0x157 to the victim, 0x150 to others, host hand-over 0x0D to the first in-game player (`FUN_004cdc50`), otherwise unlock and end the session (reason 0x10) |
| Generic forwarding (`FUN_004d3ea0`) | done for ~80 opcodes, table in `Session/Routing.cs`; see approximations below |
| LAN announcement (UDP broadcast, 0xAE payload) | done, format from `GoldLanAnnouncementCodec` |
| `Gameserver.cfg` keys | `NETWORK_PORT_TCP`, `NETWORK_PORT_LISTEN`, `NETWORK_PASSWORD`, `NETWORK_PRECONFIG_MODE/LEVEL/MAXPLAYERS`, `LADDER_EXPORT` parsed (defaults 2005/2006 are from the exe's default-port functions, mode->flag mapping is best effort) |
| Lobby registration (Open Internet) | done, see "Lobby" below; verified against `sacred-lobby` (Go) |
| Closed Net characters | relay as in the original: a block 11 from the lobby is sent to the matching player as 0x132; the server-side hero load (`CHARACTER_SAVEGAME`) needs the world (roadmap) |
| Server-side object world (`FUN_004cd040` streams it to a joiner on 0x017) | **not yet**: the biggest gap, see roadmap |

Opcodes not covered by the table or a case are logged once per opcode (`unhandled opcode 0x...`) and dropped, like the
original's default case (`0x4CC598`). Real client traffic against this server shows what is still missing.

### How the dispatcher was mapped

`FUN_004c3f20` (34 KB, Ghidra fails on it) was disassembled with capstone: the switch is on `opcode - 3` (3..0x1D5) through the
byte index table at `0x4CC9C4` and the jump table at `0x4CC6E0`. Every case that only calls the forwarder `FUN_004d3ea0(except,
data, size, opcode, origin, mode, object)` went into `Routing.cs` with its mode; cases with own logic are in
`GameServerHost.Gameplay.cs`, each commented with its case address.

### Approximations (no server world yet)

The forwarder modes 3 (sector visible), 4 (not visible), 5 (party), 6 (object mask), 7 (same sector) and 8 (sector + object)
filter by the server's own sector/object state. Without that world they are sent to every other in-game player, which is a
superset of what the original sends: clients receive messages for objects they may not know (they ignore them), so it is
correct but chattier. Mode 1 (connected) and 2 (in game) are exact.

## Run

```
SacredGameServer --game-dir=/srv/sacred --network-port-tcp=2005 --network-password=secret
SacredGameServer --accept-any-data      # development: skip version/fingerprint checks
```

`--game-dir` needs `Sacred.exe`, `tincat2.dll` and the Pak/Bin data of the supported English Gold install (same files the
client hashes); clients with different data are rejected exactly like the original (reason 6 + mask).
All `Gameserver.cfg` keys can also be passed as `--key=value`.

## Lobby

`--network-lobby=<host>` (`NETWORK_LOBBY`, port `NETWORK_LOBBYPORT`, default 7066) registers the game the way GameServer.exe's
lobby module does (`FUN_0048b880`, vtable 0x533E6C, receive switch 0x48ED40). It runs on its own thread
(`Lobby/LobbyClient.cs`); the game loop only hands it snapshots and reads events (`Session/GameServerHost.Lobby.cs`).

| Message | When | Source |
| --- | --- | --- |
| TinCat logon `user`/`passwor` | connect, reconnect with backoff 2..60 s, keep-alive 15 s, timeout 70 s | TinCat values set in `FUN_0048b880` |
| 12 game record (116 bytes) | after logon: name (cp1252), LAN IP, public IP, port, players, max, flags (server word 0x5848 incl. 0x80), network version 10, client version, channel 5 | `FUN_0048c650` |
| 12 (4 bytes) or 38 from the lobby | public IP; then all admitted players are announced | internal 0x25 (0x4CB1C1) |
| 13 update | players, max players, flags or name changed | `FUN_0048c8d0` via `FUN_004e99a0` |
| 45 player joined `[perm][block][first]` | admission of a player with a lobby identity (not 0/-1); re-sent with first=0 after a reconnect | `FUN_0048d130` / `FUN_004ed690` |
| 46 player left `[perm]` | an announced player leaves | `FUN_0048d1c0` via `FUN_004ea3e0` |
| 47 kick | player removed with reason 8 | internal 0x34 (0x4CBBC4) |
| 50 shutdown | 0x009 0x68 + 0x008 to all, then everyone removed (reason 8) | internal 0x3E, `FUN_004d6c50` |
| 11 data | Closed Net block (> 0x230 bytes, block != 10, perm+block match) sent as 0x132 | internal 0x29 (0x4CB287) |
| 14 logout + TinCat log-off | orderly stop (SIGINT/SIGTERM) | `FUN_0048ca70` |

GameServer.exe never sends 18/19 (its data request/upload slots in the vtable are `xor al, al` stubs), so neither does this server.
`LobbyTests` run against a fake lobby; the same flow was checked against a locally built `sacred-lobby`.

## Allocation rules

* Receive: one `SocketAsyncEventArgs` per connection over an `ArrayPool<byte>` buffer; frames are reassembled in place,
  payloads are rented and returned by the game loop.
* Send: the loop appends frames into a pooled fill buffer; `Flush()` swaps it with the in-flight buffer and reuses one send
  `SocketAsyncEventArgs`.
* Game loop: one thread, fixed step (`TICK_MS`, default 20), preallocated scratch buffers (decode 4 MB, build/envelope 256 KB),
  `ref struct` message views, `stackalloc` for tiny replies. `LoopbackTests.PacketLoopDoesNotAllocate` measures
  `GC.GetAllocatedBytesForCurrentThread` over 250 ticks of relayed chat (compressed and raw envelopes) and requires < 2 KB.
* Allocations remain only at connect/disconnect (connection object, SAEA), in log lines for rare events and on the lobby
  thread (a few small messages per minute).

## Tests

`dotnet test Sacred.GameServer.Tests` (needs no game files): wire interop with `Sacred.Network` and the BCL zlib, loopback
tests with the remake's own `GoldGameConnection` (admission, host flag, duplicate identity, version and data mismatch,
password, chat, allocation), and session-flow tests with a small raw client (`RawClient.cs`): host start and participant
approval, routing table, in-game filtering, host-only refusal and lock, flag merge, kick, host hand-over, session end, world
bank order, time probes; and lobby tests with a fake lobby (game record, announce/leave/update, duplicate identity while
online, lobby kick, Closed Net character, lobby shutdown).

## Roadmap (what the original GameServer does that this does not yet)

1. Server-side object world: `FUN_004cd040` streams the server's objects to a joiner on 0x017, and the banks are serialized
   from current state instead of replaying the host's upload. The world also loads Closed Net heroes (`CHARACTER_SAVEGAME`)
   and uploads them on save/leave.
2. Authoritative mode (optional): the server owns sector state (balance, enemy and game difficulty) from its config instead
   of the first client entering a sector, and checks client reports (hero stats, items, gold, damage, kills) against the
   data tables; later a full simulation (monsters, loot, combat, scripts). John's C++ re-creation of Sacred.exe
   (`sacred-decomp`) is the logic reference.
3. Exact party / sector / visibility filtering for forwarder modes 3..8 (needs 1).
4. Remaining custom cases: 0x004 team chat, 0x00F, the 0x12x object cases, 0x01D savegame, 0x029 character savegame,
   0x104..0x107, 0x141 extras, 0x198.
5. Server-side Lua API for Sacred Gear server mods (events, per-player storage, messages/items; enough for e.g. achievements,
   not full parity with the client API).
6. Ladder export (`LADDER_EXPORT`).
