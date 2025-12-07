# KSA Multiplayer Mod

A multiplayer modification for Kitten Space Agency (KSA) that enables real-time cooperative gameplay where multiple players can see each other's spacecraft during orbital missions, maneuvers, and surface operations.

## Features

- **Real-time Vehicle Synchronization** - See other players' spacecraft in orbit and on surfaces
- **Event-based Architecture** - Efficient network updates only when significant state changes occur (engine ignition, maneuvers, control inputs)
- **In-game Chat** - Communicate with other players
- **Player Nametags** - Visual indicators showing player names above their vehicles
- **Time Synchronization** - Keeps players in sync across the simulation
- **System Validation** - Ensures all players are running the same solar system configuration
- **EVA Support** - Synchronizes KittenEva (astronaut) objects between players

## Requirements

- Kitten Space Agency v2025.12.24.3014 or compatible version
- .NET 9 Runtime
- KSA ModLoader

## Installation

1. Download the latest release from the [Releases](https://github.com/racerx2/KSA-Multiplayer-Mod/releases) page
2. Extract `KSA.Mods.Multiplayer.dll` to your KSA `Content/Multiplayer/` folder
3. Ensure the `mod.toml` file is in the same folder
4. Launch KSA - the mod will load automatically

## Usage

### Hosting a Game
1. Open the Multiplayer window (check KSA's mod menu)
2. Enter your player name
3. Set a port number (default: 7777)
4. Click "Host Server"
5. Share your IP address and port with other players

### Joining a Game
1. Open the Multiplayer window
2. Enter your player name
3. Enter the host's IP address and port
4. Click "Connect"

**Important:** All players must be running the same solar system configuration (Solar System, Earth and Moon, or Earth Only). The mod will display an error if there's a mismatch.

### Controls
- Use the chat box to communicate with other players
- The Debug section shows synchronization status and vehicle information
- Time sync status shows whether you're in sync with other players

## Building from Source

### Prerequisites
- .NET 9 SDK
- Access to KSA game assemblies for reference

### Build Steps
```powershell
cd KSA-Multiplayer-Mod
dotnet restore
dotnet build
```

The compiled DLL will be output to `bin/Debug/KSA.Mods.Multiplayer.dll`

### Deploy
Use the included `Build-And-Deploy.ps1` script to build and copy to your KSA installation.

## Dependencies

| Dependency | Version | License | Purpose |
|------------|---------|---------|---------|
| [Harmony](https://github.com/pardeike/Harmony) | 2.x | MIT | Runtime method patching |
| [MemoryPack](https://github.com/Cysharp/MemoryPack) | 1.x | MIT | Binary serialization for network messages |

## Technical Details

This mod implements the "immortal vessel" pattern where remote vehicles exist as real Vehicle objects but are excluded from the local physics simulation. Only the controlling player simulates physics while others receive and render position updates.

Key implementation details:
- Uses Harmony patches to intercept KSA's networking and vehicle systems
- Binary message serialization via MemoryPack for efficient network traffic
- Situation-aware coordinate systems (CCI for orbital, CCF for surface)
- Deterministic Kepler orbital mechanics handle motion between sync points

## Known Limitations

- Time warp synchronization is not yet fully implemented
- Large time differences between players may cause visual artifacts
- Surface vehicle synchronization may have minor position drift

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Author

**RacerX**

- GitHub: [@racerx2](https://github.com/racerx2)

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for version history.

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## Disclaimer

This mod is provided as-is. Use at your own risk. Always review source code before running third-party modifications.
