# Sacred.GameServer

Headless re-creation of Sacred's `GameServer.exe` (dedicated server) as a .NET 10 console app, intended to run on a cheap
Linux VPS. Native AOT + full trimming (`dotnet publish -c Release -r linux-x64` gives a ~2.3 MB single binary, no IL warnings).

New project only: no existing file of the repository is modified. It reuses the verified layouts/codecs of `Sacred.Network`
(FIRSTCONTACT layout, data fingerprints, build reader). The `Sacred.Network` *client* classes allocate per frame, so the wire
path here is separate and allocation free.

## Status (stage 1: transport + admission)

| Area | State |
| --- | --- |
| TinCat login / keep-alive / logoff | done, verified against `Sacred.Network` client |
| Gold envelope + messages (raw + zlib) | done, byte-compatible with `Sacred.Network.Protocol`; own zero-alloc inflate/deflate |
| FIRSTCONTACT admission | done: version check, lobby identity duplicate check, 25 data fingerprints with mismatch mask, slot 1..16, host role to first player, 0x11 reply, 0x1E game flags |
| Rejection 0x3F reasons | 1 version, 2 duplicate identity, 3 refused/server full, 4 bad sender, 5 sequence, 6 checksum **or** data mismatch (from `FUN_004eb0a0`) |
| Game password (0x12 -> 0x13/0x14) | done; `MaxPasswordTries` (default 3) is **not** verified against the original |
| Chat relay (0x032) | done: speaker/name stamped by the server, private (recipient > 0) or broadcast |
| Host hand-over when the host leaves (0x0D) | done (simple: next admitted player) |
| LAN announcement (UDP broadcast, 0xAE payload) | done, format from `GoldLanAnnouncementCodec` |
| `Gameserver.cfg` keys | `NETWORK_PORT_TCP`, `NETWORK_PORT_LISTEN`, `NETWORK_PASSWORD`, `NETWORK_PRECONFIG_MODE/LEVEL/MAXPLAYERS`, `LADDER_EXPORT` parsed (defaults 2005/2006 are from the exe's default-port functions, mode->flag mapping is best effort) |
| Lobby registration (Open/Closed Internet) | **not yet**: needs the Go lobby (`sacred-lobby`) protocol, module 0x26B6 |
| World/object simulation, roster, sector/object replication, uploads (0x15..0x18, 0x1B5), script/VM | **not yet**: see roadmap |

Unknown opcodes from admitted players are logged once per opcode (`unhandled opcode 0x...`) so stage 2 can be driven by real
traffic from a real client against this server.

## Run

```
SacredGameServer --game-dir=/srv/sacred --network-port-tcp=2005 --network-password=secret
SacredGameServer --accept-any-data      # development: skip version/fingerprint checks
```

`--game-dir` needs `Sacred.exe`, `tincat2.dll` and the Pak/Bin data of the supported English Gold install (same files the
client hashes); clients with different data are rejected exactly like the original (reason 6 + mask).
All `Gameserver.cfg` keys can also be passed as `--key=value`.

## Allocation rules

* Receive: one `SocketAsyncEventArgs` per connection over an `ArrayPool<byte>` buffer; frames are reassembled in place,
  payloads are rented and returned by the game loop.
* Send: the loop appends frames into a pooled fill buffer; `Flush()` swaps it with the in-flight buffer and reuses one send
  `SocketAsyncEventArgs`.
* Game loop: one thread, fixed step (`TICK_MS`, default 20), preallocated scratch buffers (decode 4 MB, build/envelope 256 KB),
  `ref struct` message views, `stackalloc` for tiny replies. `LoopbackTests.PacketLoopDoesNotAllocate` measures
  `GC.GetAllocatedBytesForCurrentThread` over 250 ticks of relayed chat (compressed and raw envelopes) and requires < 2 KB.
* Allocations remain only at connect/disconnect (connection object, SAEA) and in log lines for rare events.

## Tests

`dotnet test Sacred.GameServer.Tests` (needs no game files): wire interop with `Sacred.Network` and the BCL zlib, plus loopback
tests that drive the server with the remake's own `GoldGameConnection` (admission, host flag, duplicate identity, version and
data mismatch, password, chat relay, allocation).

## Roadmap (what the original GameServer does that this does not yet)

1. Session start-up traffic after admission: roster (0x11 player data variants), time probes 0x43/0x44, player limit, world
   bank requests (0x1B5), participant/host upload and start (0x15..0x18), sector entry/interest, reference ranges.
2. Message routing table (which opcodes are relayed to all, to the host, to one player, or consumed). The original keeps a
   per-opcode table; the receive handler is `FUN_004eb0a0`, the opcode -> message object factory is `FUN_0042b500`.
3. Authoritative world: objects, creatures, sectors, loot (`Sacred.GameplayHost` already has the loader/script pieces).
4. Lobby registration (type 12 login with public IP, announce/reannounce/remove) against `sacred-lobby`.
5. Save/load of heroes (`ItemDataMgr::saveHero`), ladder export (`LADDER_EXPORT`).
