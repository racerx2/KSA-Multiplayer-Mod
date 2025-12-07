# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
