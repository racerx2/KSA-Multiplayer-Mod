# KSA Multiplayer Mod

A multiplayer modification for Kitten Space Agency (KSA) that enables real-time cooperative gameplay where multiple players can see each other's spacecraft during orbital missions, maneuvers, and surface operations.

## Features

- **Real-time Vehicle Synchronization** - See other players' spacecraft in orbit and on surfaces
- **Event-based Architecture** - Efficient network updates only when significant state changes occur (engine ignition, throttle changes, RCS, maneuvers)
- **LMP-style Subspace System** - Players can time warp independently without breaking multiplayer
- **In-game Chat** - Communicate with other players
- **Player Nametags** - Visual indicators showing player names above their vehicles
- **Time Synchronization** - Sync to other players' simulation time when ready to rendezvous
- **System Validation** - Ensures all players are running the same solar system configuration
- **EVA Support** - Synchronizes KittenEva (astronaut) objects between players
- **Ghost Mode** - Out-of-sync players appear as markers on the map view only

## Repository Structure

```
KSA-Multiplayer-Mod/
├── KSA-Multiplayer-Mod/          # Source code
│   ├── src/                      # C# source files
│   │   ├── Messages/             # Network message definitions
│   │   ├── ModEntry.cs           # Mod entry point
│   │   ├── MultiplayerManager.cs # Core multiplayer logic
│   │   ├── MultiplayerWindow.cs  # ImGui UI
│   │   ├── NetworkManager.cs     # RakNet networking
│   │   ├── RemoteVehicleRenderer.cs
│   │   ├── SubspaceManager.cs    # Time warp handling
│   │   └── ...
│   ├── Build-And-Deploy.ps1      # Build script
│   ├── KSA-Multiplayer-Mod.csproj
│   ├── mod.toml
│   ├── LICENSE
│   └── CHANGELOG.md
│
└── KSA-Multiplayer-Package/      # Installer and distribution
    ├── Launcher/                 # ModLoader executable
    │   ├── KSA.ModLoader.exe
    │   ├── KSA.ModLoader.dll
    │   └── 0Harmony.dll
    ├── Content/Multiplayer/      # Mod files
    │   └── mod.toml
    ├── Install.ps1               # PowerShell installer
    ├── Install.bat               # Batch installer wrapper
    ├── installer.nsi             # NSIS installer script
    └── README.md                 # Detailed usage guide
```

## Requirements

### For Players (Using Pre-built Release)
- **Kitten Space Agency** v2994 or later
- **.NET 10 Desktop Runtime** - [Download here](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
  - Select ".NET Desktop Runtime" for Windows x64
- **Windows** (x64)

### For Developers (Building from Source)
- **.NET 10 SDK** (or .NET 9 SDK)
- **Kitten Space Agency** installed (for assembly references)
- **NSIS** (optional, for building the installer executable)

## Installation (For Players)

### Option 1: Installer (Recommended)
1. Download `KSA-Multiplayer-Setup.exe` from the [Releases](https://github.com/racerx2/KSA-Multiplayer-Mod/releases) page
2. Run the installer and select your KSA installation folder
3. Use the "KSA with Mods" desktop shortcut to launch

### Option 2: Manual Installation
1. Download `KSA.Mods.Multiplayer.dll` from the [Releases](https://github.com/racerx2/KSA-Multiplayer-Mod/releases) page
2. Download or clone the `KSA-Multiplayer-Package` folder
3. Run `Install.ps1` (right-click → Run with PowerShell) or `Install.bat`

For custom KSA paths:
```powershell
.\Install.ps1 -KSAPath "D:\Games\Kitten Space Agency"
```

## Building from Source

### Prerequisites
1. Install .NET 10 SDK (or .NET 9)
2. Install Kitten Space Agency to `C:\Program Files\Kitten Space Agency` (or update paths in `.csproj`)

### Build Steps
```powershell
cd KSA-Multiplayer-Mod
dotnet restore
dotnet build --configuration Release
```

Output: `bin\Release\KSA.Mods.Multiplayer.dll`

### Build and Deploy (Development)
```powershell
.\Build-And-Deploy.ps1
```

This script:
1. Cleans and builds in Release configuration
2. Copies the DLL to your KSA installation
3. Copies the DLL to the Package folder
4. Creates a desktop shortcut

### Building the Installer
Requires [NSIS](https://nsis.sourceforge.io/) installed:
```batch
cd KSA-Multiplayer-Package
makensis installer.nsi
```

Output: `KSA-Multiplayer-Setup.exe`

## Usage

### Quick Start
1. Launch KSA using the "KSA with Mods" desktop shortcut
2. Press `~` to open the console
3. Type `mp_ui` to open the Multiplayer window

### Hosting a Game
```
mp_host <playerName> <port> <maxPlayers>
```
Example: `mp_host RacerX 7777 8`

### Joining a Game
```
mp_join <playerName> <serverIP> <port>
```
Example: `mp_join Player2 192.168.1.100 7777`

### Important: System Matching
**All players must select the same solar system at game startup:**
- Solar System
- Earth and Moon  
- Earth Only

If systems don't match, the connection will be refused.

### Console Commands
| Command | Description |
|---------|-------------|
| `mp_ui` | Toggle multiplayer UI window |
| `mp_host <name> <port> <max>` | Host a server |
| `mp_join <name> <ip> <port>` | Join a server |
| `mp_disconnect` | Disconnect from session |
| `mp_status` | Show connection status |
| `mp_chat <message>` | Send chat message |
| `mp_vehicles` | List remote vehicles |
| `mp_goto <playerName>` | Teleport to a player |
| `mp_clearlogs` | Clear log files |
| `mp_logdir` | Show log directory |

### Subspace System
Players can time warp independently:
- **In Sync (Yellow markers):** Players at same simulation time see each other's vessels in 3D and on map
- **Out of Sync (Red markers):** Players at different times appear as "ghosts" on map only
- **Sync Up:** Use the Time Sync dropdown in the UI to jump forward and match another player

## Architecture

This mod implements the **Luna Multiplayer (LMP) "immortal vessel" pattern**:

- Remote vehicles exist as real Vehicle objects but are excluded from physics simulation
- Only the controlling player simulates physics; others receive and render position updates
- Event-based synchronization transmits updates only on significant state changes
- KSA's deterministic Kepler orbital mechanics handle smooth motion between sync points
- Situation-aware coordinate systems (CCI for orbital, CCF for surface contact)

### Network Protocol
- **Transport:** RakNet (KSA's built-in networking)
- **Serialization:** MemoryPack (binary, efficient)
- **Message Types:** VehicleState, OrbitSync, TimeSync, Chat, SystemCheck, VehicleDesign

## Log Files

When debug logging is enabled, logs are written to:
`<KSA>\Content\Multiplayer\logs\`

Log files include: TimeSync, Subspace, Sync, Players, Network, Vehicles, Events, Renderer, Patches, NameTags

## Dependencies

| Dependency | Version | License | Purpose |
|------------|---------|---------|---------|
| [Harmony](https://github.com/pardeike/Harmony) | 2.4.2 | MIT | Runtime method patching |
| [MemoryPack](https://github.com/Cysharp/MemoryPack) | 1.21.3 | MIT | Binary serialization |

## Troubleshooting

| Issue | Solution |
|-------|----------|
| ".NET Desktop Runtime" error | Install .NET 10 Desktop Runtime from Microsoft |
| "System Mismatch" on connect | All players must select same solar system at startup |
| Mod doesn't load | Use "KSA with Mods" shortcut, not KSA.exe directly |
| Vehicles don't appear | Enable debug logging and check log files; ensure players are time-synced |

## License

MIT License - see [LICENSE](KSA-Multiplayer-Mod/LICENSE)

## Author

**RacerX** - [@racerx2](https://github.com/racerx2)

## Version

Current: **v0.1.0**

See [CHANGELOG](KSA-Multiplayer-Mod/CHANGELOG.md) for version history.
