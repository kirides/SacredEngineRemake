# Sacred.GameServer

Headless re-creation of Sacred's `GameServer.exe` (dedicated server) as a .NET 10 console app, intended to run on a cheap
Linux VPS. Native AOT + full trimming (`dotnet publish -c Release -r linux-x64` gives a ~2.3 MB single binary, no IL warnings).

New project only: no existing file of the repository is modified. It reuses the verified layouts/codecs of `Sacred.Network`
(FIRSTCONTACT layout, data fingerprints, build reader). The `Sacred.Network` *client* classes allocate per frame, so the wire
path here is separate and allocation free.

## Status (stage 2: session flow + routing)

| Area | State |
| --- | --- |
| TinCat login / keep-alive / logoff | done, verified against `Sacred.Network` client |
| Gold envelope + messages (raw + zlib) | done, byte-compatible with `Sacred.Network.Protocol`; own zero-alloc inflate/deflate |
| FIRSTCONTACT admission | done: version check, lobby identity duplicate check, 25 data fingerprints with mismatch mask, slot 1..16, host role to first player, 0x11 reply `[flags][slot][token]` (flag 1 host, 4 host world available), 0x1E game flags |
| Rejection 0x3F reasons | 1 version, 2 duplicate identity, 3 refused/locked/full, 4 bad sender, 5 sequence, 6 checksum **or** data mismatch (from `FUN_004eb0a0`) |
| Game password (0x12 -> 0x13/0x14) | done; `MaxPasswordTries` (default 3) is **not** verified against the original |
| Chat 0x003 private / 0x005 public | done (`0x4CA8AA` / `0x4CA771`), public chat honours nothing but sender exclusion; team chat 0x004 **not yet** |
| Host commands 0x019/0x01A lock, 0x01E flags, 0x023 max players, 0x156/0x157 kick | done, host only (others get 0x009 value 0x64); flags merge `(new & 0xFFFE8F72) \| (cur & 0x1708D)` |
| World start 0x017/0x018, world banks 0x1B1..0x1B4 -> 0x1B5 | done as relay: host uploads are stored and served in the original order 0x1B2, 0x1B4, 0x1B3, 0x1B1 |
| Time probes 0x041/0x043/0x044/0x045, pause 0x134/0x135 | done |
| Leave / kick (`FUN_004d6ac0`) | done: 0x157 to the victim, 0x150 to others, host hand-over 0x0D to the first in-game player (`FUN_004cdc50`), otherwise unlock and end the session (reason 0x10) |
| Generic forwarding (`FUN_004d3ea0`) | done for ~80 opcodes, table in `Session/Routing.cs`; see approximations below |
| LAN announcement (UDP broadcast, 0xAE payload) | done, format from `GoldLanAnnouncementCodec` |
| `Gameserver.cfg` keys | `NETWORK_PORT_TCP`, `NETWORK_PORT_LISTEN`, `NETWORK_PASSWORD`, `NETWORK_PRECONFIG_MODE/LEVEL/MAXPLAYERS`, `LADDER_EXPORT` parsed (defaults 2005/2006 are from the exe's default-port functions, mode->flag mapping is best effort) |
| Lobby registration (Open/Closed Internet) | **not yet**: module 0x26B6 against `sacred-lobby` |
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

`dotnet test Sacred.GameServer.Tests` (needs no game files): wire interop with `Sacred.Network` and the BCL zlib, loopback
tests with the remake's own `GoldGameConnection` (admission, host flag, duplicate identity, version and data mismatch,
password, chat, allocation), and session-flow tests with a small raw client (`RawClient.cs`): host start and participant
approval, routing table, in-game filtering, host-only refusal and lock, flag merge, kick, host hand-over, session end, world
bank order, time probes.

## Roadmap (what the original GameServer does that this does not yet)

1. Lobby registration (type 12 login with public IP, announce/reannounce/remove) against `sacred-lobby`; Closed Net
   characters: report join (msg 45), fetch slot (msg 19: 556-byte preview + zlib save), upload on save/leave (msg 18).
   Open Net uses channel 5.
2. Server-side object world: `FUN_004cd040` streams the server's objects to a joiner on 0x017, and the banks are serialized
   from current state instead of replaying the host's upload. `Sacred.GameplayHost` has loader/script pieces to build on.
3. Exact party / sector / visibility filtering for forwarder modes 3..8 (needs 2).
4. Remaining custom cases: 0x004 team chat, 0x00F, the 0x12x object cases, 0x01D savegame, 0x029 character savegame,
   0x104..0x107, 0x141 extras, 0x198.
5. Save/load of heroes (`ItemDataMgr::saveHero`), ladder export (`LADDER_EXPORT`).
