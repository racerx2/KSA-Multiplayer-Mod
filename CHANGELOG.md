# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
