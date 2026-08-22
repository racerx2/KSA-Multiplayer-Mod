# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - 2026-08-22

Successor to v0.2.1, the last published release. Consolidates every change
since, the repository having carried no releases in between. The minor version
is stepped because the wire format is not compatible with 0.2.1: both the
client and the server must be on this build.

### Added
- Empty authoritative shared universe with late-join design and state replay
- Reliable vessel lifecycle synchronization and sequenced live state updates
- Compressed KSA part-tree transfer for exact remote-vessel reconstruction
- Reversible empty multiplayer overlay that hides pre-existing local vessels for
  the session and restores them on disconnect without changing save data
- Source-generated player roster protocol shared by the client and server
- Cross-player docking: a Dock entry on the stock port menu, which KSA cannot
  offer for a remote craft because its own menu reads bubble membership
- Multiplayer menu beside the HUD and a persistent window launcher
- Automated local installation and remote deployment helpers
- Join-time player name validation: rejects the default placeholder name,
  duplicates, empty names, and names containing the uid separator
- Server-declared host name in the player roster, replacing a client-side guess
- Mod version check: a client running a different version than the host is
  refused, is told which version the host runs, and is given the releases link
- Game type check: a client whose Game Type differs from the host's is refused
  and told which one to choose on KSA's configuration screen, with a matching
  `gameType` option in the server config
- Craft sharing also offers the craft KSA ships, which live in a separate
  collection and folder (`DefaultVehicleSaves`, `Content/Core/defaultvehicles`)
  from the player's own saves, and a "Share what I'm flying" button that
  snapshots the controlled vessel through `VehicleSaveData.Create` so no save
  file has to exist first
- Craft sharing: upload a saved craft to the server from the multiplayer panel,
  browse what other players have shared, and download one into the vehicle
  folder, where KSA's own VEHICLE SAVES window lists it. Downloads never
  overwrite a craft of the same name; they install alongside it under the
  sharer's name. Governed by `craftSharingEnabled` and
  `maxSharedCraftPerPlayer` in the server config, with `craft list` and
  `craft remove` console commands
- A docked passenger can ask the owner of the merged stack to undock them. The
  owner is prompted and the split runs on their machine, replicating back the
  ordinary way. Until now a passenger's undock reached KSA and failed with
  "does not belong to an update task": the merged stack is a remote vessel on
  their client, remote vessels are deliberately kept out of the physics bubbles,
  and `Vehicle.Split` refuses a vessel that has none
- `LogDockingReadout` setting, off by default, for the per-frame docking trace

### Changed
- Ported the client and server to KSA v2026.8.19.5261
- Docking control model: the player who initiates a dock takes control of the
  merged vessel; the other keeps ownership and their camera, and control is
  handed back on undock
- Remote craft are advanced to the frame's instant in the safe window, so any
  distance computed between a local and a remote vessel refers to one moment
- Nearby vessel interpolation measures real elapsed time instead of assuming a
  fixed 50 FPS client update rate
- Nearby position updates run at 15 Hz and coalesce to the newest snapshot with
  a four-packet queue limit
- Position samples far older than their neighbours are refused rather than
  rendered
- Relicensed from MIT to PolyForm Noncommercial License 1.0.0. Releases already
  published under MIT remain available under MIT; this applies from here forward
- Unified client, package, and server version metadata
- Keeping remote vessels out of the physics bubbles is its own step rather than
  a side effect hidden inside a method named and documented as a logging probe.
  It is what makes a remote vessel remote, and silencing logs could not be
  allowed to switch it off
- Routine telemetry honours the debug-logging setting; the variant that ignores
  it is reserved for anomalies a player must be able to report with logging off
- The docking readout trace is opt-in. It was the largest thing the mod wrote,
  23 MB of a 29 MB session, and it ran every frame of every docking approach

### Removed
- Kick button. It only logged "NOT IMPLEMENTED". Kicking is a server-console
  operation; a client-initiated kick needs an administrator concept the server
  does not have, without which any player could remove any other
- Four settings written to `settings.toml` and never read: `MaxPlayers`,
  `SyncIntervalMs`, `EnablePositionSmoothing`, `InterpolationFactor`
- Three wire types nothing ever sent: `MultiplayerChatMessage` (140),
  `TimeSyncMessage` (201) and `OrbitSyncMessage` (203). Chat travels on KSA's
  own chat message and time is carried by the heartbeat. The ids stay retired
  rather than reused. `TimeSyncMessage`'s handler set local time in either
  direction, which contradicts the forward-only design outright
- Two Harmony patches whose prefixes returned true down every path, on
  `Vehicle.PrepareWorker` and `Vehicle.UpdateRenderData` — both per vehicle per
  frame, the second once more per viewport
- Dead code that would have malfunctioned if revived, having been written when
  vessel keys were separated by `_`: two vessel-removal helpers, a second
  unsubscribed state handler, and a `RegisterRemoteVehicle` overload

### Fixed
- A refused join no longer crashes the client. KSA's `ExecuteJoinGameResponse`
  calls `Shutdown()` from inside `NetworkSession.ProcessAllWaitingPackets`, which
  disposes the RakNet instance that loop is still holding a packet from; the loop
  then calls `DeallocatePacket` and `Receive` on freed memory. The mod records the
  refusal, shows the server's reason, and disconnects a frame later instead. The
  client also checks its own player name before connecting, so the common refusals
  never reach the wire
- Undocking no longer moves the initiator's camera and controls onto the other
  player's craft, which silenced that client's publisher entirely
- Player chat is delivered: the dedicated server translates a chat request into
  a display message for every client instead of relaying the raw request
- Remote vessels no longer replay stale movement when the rendering loop cannot
  drain incoming packets as fast as they arrive
- Remote craft render even when their template ID is absent from another
  player's `ModLibrary`
- Failed remote-vessel creation retries at a bounded interval instead of every
  frame
- Target references are released before a remote vessel is disposed, so nothing
  reads a torn-down craft
- Remote vessels are no longer left visible on the map but absent from the scene
- Client diagnostics moved out of the protected game directory
- Repeated native player-list serialization no longer crashes the dedicated
  server under Wine
- Heartbeat packets no longer rewrite the KSA universe time
- Sender-side chat echo that duplicated the authority-confirmed message
- Teleport targets propagate to the current simulation time, and unsupported
  cross-body teleports are reported instead of failing silently
- The dedicated server's join response carries a payload KSA's Brotli
  deserializer accepts
- Null player entries no longer crash the client UI during join
- An owned but uncontrolled vessel falls back to the keepalive interval when it
  is on rails with nobody near it. The proximity test behind that choice was a
  stub returning true, so every such vessel published at 15 Hz forever and a
  player with several parked craft sent 15 messages a second for each
- A joining player receives the initial time sync. The branch that sent it was
  guarded by a flag only a method with no callers ever set, so it never ran.
  The server also no longer broadcasts a heartbeat claiming time zero before any
  client has reported one
- A remote vessel's measured clock offset is forgotten when its queue is dropped
  and when this client's own clock jumps. It was kept forever, so after a
  reconnect or a time jump the stale offset made the staleness filter refuse
  thirty consecutive samples — about two seconds of frozen remote vessels
- A malformed packet can no longer take the client down, including the payloads
  MemoryPack answers with null rather than an exception, which KSA treats as
  fatal
- The dedicated server no longer races its own console thread, disposes the
  RakNet peer while the packet loop is still reading from it, or lets one
  client's malformed packet stop it

## [0.2.1] - 2025-12-17

### Changed
- Updated for KSA v3103 API changes (KeyHash, KittenEva constructor)
- Server now uses RunServer.cmd launcher to use system .NET 10 runtime
- Removed standalone server exe, now DLL-based with dotnet launcher
- Converted to StarMap attribute-based mod loader (StarMap.API)
- Renamed DLL from KSA.Mods.Multiplayer.dll to Multiplayer.dll for StarMap compatibility

## [0.2.0] - 2025-12-14

### Added
- **Dedicated server architecture** - Server runs independently, no player hosts (requires KSA installation for game DLLs)
- Server console with admin commands (kick, ban, unban, say, status, list)
- Password protection with 5-second timeout enforcement
- MOTD (Message of the Day) support
- Server-authoritative time synchronization on join
- StarMap mod loader compatibility
- Connection error display in client UI (e.g., "Wrong password!")

### Changed
- Upgraded from KSA v3014 to v3057
- Time warp now works correctly - server only syncs forward, never pulls back
- Client connects to dedicated server instead of peer hosting

### Fixed
- Time warp pulling players back to real time
- Clock drift between players on initial join

### Technical
- Server heartbeat broadcasts authoritative time every 3 seconds
- Forward-only time sync prevents time warp interference
- Binary password authentication message with SHA-256 ready

## [0.1.0] - 2025-12-07

### Added
- Initial release
- Real-time vehicle position synchronization between players
- Event-based state synchronization (engine, throttle, RCS)
- In-game chat system
- Player nametags above vehicles
- Time synchronization between host and clients
- System configuration validation (prevents mismatched solar systems)
- KittenEva (EVA astronaut) synchronization support
- Situation-aware coordinate handling (CCI for orbital, CCF for surface)
- Multiplayer UI window with connection controls, player list, and debug info
- Teleport-to-player cheat for testing
- Comprehensive logging system for debugging

### Technical
- Harmony patches for network message handling
- MemoryPack binary serialization for efficient networking
- "Immortal vessel" pattern - remote vehicles excluded from physics simulation
- Support for vehicle switching synchronization
