# KSA Multiplayer

![KSA Multiplayer](title.png)

A multiplayer modification for Kitten Space Agency (KSA) that enables real-time cooperative gameplay. Players can see each other's spacecraft during orbital missions, maneuvers, and surface operations.

## Architecture

This project uses a **dedicated server** architecture:

- **Server** - Standalone executable that relays messages between clients, manages authentication, and maintains authoritative game time
- **Client** - KSA mod that connects to the server and synchronizes vehicle data with other players

## Features

- **Dedicated Server** - Server runs independently; no player needs to host (requires KSA installation for game DLLs)
- **Real-time Vehicle Synchronization** - See other players' spacecraft in orbit and on surfaces
- **Event-based Architecture** - Efficient network updates only on significant state changes
- **In-game Chat** - Communicate with other players
- **Player Nametags** - Visual indicators showing player names above vehicles
- **Server-Authoritative Time Sync** - Keeps all players synchronized
- **Password Protection** - Optional server passwords with timeout enforcement
- **System Validation** - Ensures all players run the same solar system
- **EVA Support** - Synchronizes astronaut objects between players
- **StarMap Compatible** - Works with both native KSA mod loading and [StarMap](https://github.com/StarMapLoader/StarMap) loader
- **Time Warp Support** - Players can warp independently; use Sync button to catch up

## Requirements

- Kitten Space Agency v3103 or compatible
- [StarMap Mod Loader](https://github.com/StarMapLoader/StarMap) (install separately)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (select "Desktop Runtime" for Windows x64)

---

## Installation (End Users)

### Using the Installer (Recommended)

1. **Install [StarMap Mod Loader](https://github.com/StarMapLoader/StarMap/releases) first!**
2. **Install .NET 10 Desktop Runtime**
3. Download `KSA-Multiplayer-Setup.exe` from [Releases](https://github.com/racerx2/KSA-Multiplayer-Mod/releases)
4. Run the installer and select your KSA installation folder
5. Two desktop shortcuts will be created:
   - **KSA with Mods** - Launch StarMap (requires StarMap installed to `C:\Program Files (x86)\StarMap\`)
   - **KSA Dedicated Server** - Run a multiplayer server

### Manual Installation

1. Copy `Launcher/*` to `[KSA Install]\Launcher\`
2. Copy `Content/Multiplayer/*` to `[KSA Install]\Content\Multiplayer\`
3. Copy server files to `[KSA Install]\` root:
   - `RunServer.cmd`
   - `KSA-Dedicated-Server.dll`
   - `KSA-Dedicated-Server.deps.json`
   - `KSA-Dedicated-Server.runtimeconfig.json`
   - `server_config.json`
4. Add to `Content\manifest.toml`:
   ```toml
   [[mods]]
   id = "Multiplayer"
   enabled = true
   ```

### StarMap Mod Loader

This mod requires [StarMap](https://github.com/StarMapLoader/StarMap), the community mod loader. The installer creates a shortcut that launches StarMap from `C:\Program Files (x86)\StarMap\StarMap.exe`. Make sure StarMap is installed and configured to point to your KSA installation before using the shortcut.

---

## Running the Server

### Quick Start

1. Double-click the **KSA Dedicated Server** desktop shortcut
   - Or run `RunServer.cmd` from KSA installation folder

2. The server will start and display:
   ```
   ╔══════════════════════════════════════════╗
   ║      KSA DEDICATED SERVER v1.0.0         ║
   ╚══════════════════════════════════════════╝
   Server: KSA Multiplayer Server
   Port: 7777, Max Players: 8
   ```

### Server Configuration

Edit `server_config.json` in your KSA folder:

```json
{
  "ServerName": "My KSA Server",
  "Port": 7777,
  "MaxPlayers": 8,
  "Password": "",
  "SystemId": "Sol",
  "SystemDisplayName": "Solar System",
  "Motd": "Welcome to KSA Multiplayer!",
  "BannedIPs": []
}
```

| Setting | Description |
|---------|-------------|
| `ServerName` | Display name for your server |
| `Port` | UDP port (default: 7777) |
| `MaxPlayers` | Maximum concurrent players |
| `Password` | Leave empty for no password |
| `SystemId` | `Sol`, `EarthMoon`, or `Earth` |
| `Motd` | Message shown to players on join |

### Server Commands

| Command | Description |
|---------|-------------|
| `help` | Show available commands |
| `status` | Show server status and player count |
| `list` | List connected players |
| `kick <name>` | Kick a player by name |
| `ban <name>` | Ban a player (saves IP) |
| `unban <ip>` | Remove an IP from ban list |
| `banlist` | Show banned IPs |
| `say <message>` | Broadcast server message |
| `quit` | Graceful shutdown |

### Port Forwarding

To allow players outside your local network:
1. Forward port **7777 UDP** in your router
2. Share your public IP address with players

---

## Running the Client

1. Double-click the **KSA with Mods** desktop shortcut
2. In-game, the Multiplayer window opens automatically
3. Enter connection details:
   - **Server IP** - The server's IP address
   - **Port** - Server port (default: 7777)
   - **Name** - Your player name
   - **Password** - Server password (if required)
4. Click **Connect**

### Important Notes

- **All players must run the same solar system as configured in the server's `server_config.json`!** (Solar System, Earth and Moon, or Earth Only)
- System mismatch will show an error and disconnect

### Time Sync & Subspace

Players can time warp independently:
- **In Sync** (green) - See each other's vessels in 3D
- **Out of Sync** (orange) - Vessels appear as "ghosts" (map only)
- **Sync Button** - Jump forward to match another player's time

---

## Development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Kitten Space Agency installed (for assembly references)
- [NSIS](https://nsis.sourceforge.io/Download) (for building installer)

### Project Structure

```
KSA-Multiplayer/
├── Client/                      # Client mod source
│   ├── src/                     # C# source files
│   │   ├── Messages/            # Network message classes
│   │   ├── ModEntry.cs          # Mod entry point
│   │   ├── MultiplayerManager.cs
│   │   ├── NetworkPatches.cs    # Harmony patches
│   │   └── ...
│   ├── KSA-Multiplayer-Mod.csproj
│   └── mod.toml
│
├── Server/                      # Dedicated server source
│   ├── DedicatedServer.cs       # Main server logic
│   ├── ServerConfig.cs          # Configuration handling
│   ├── ServerConsole.cs         # Console UI
│   ├── KSA-Dedicated-Server.csproj
│   └── ...
│
├── KSA-Multiplayer-Package/     # Installer & distribution
│   ├── installer.nsi            # NSIS installer script
│   ├── build.bat                # Build script
│   ├── Content/Multiplayer/     # Client binaries
│   ├── Server/                  # Server binaries
│   └── Launcher/                # Mod loader
│
├── README.md
├── CHANGELOG.md
└── LICENSE
```

### Building the Client Mod

```powershell
cd Client
dotnet build -c Release
```

Output: `Client/bin/Release/KSA.Mods.Multiplayer.dll`

### Building the Server

```powershell
cd Server
dotnet build -c Release
```

Output: `Server/bin/Release/net10.0/KSA-Dedicated-Server.exe`

### Building the Installer Package

The `build.bat` script builds everything and creates the installer:

```powershell
cd KSA-Multiplayer-Package
.\build.bat
```

This will:
1. Build client mod (Release)
2. Build server (Release)
3. Copy binaries to package folders
4. Run NSIS to create `KSA-Multiplayer-Setup.exe`

### Manual Development Workflow

1. Build client:
   ```powershell
   cd Client
   dotnet build -c Release
   copy bin\Release\KSA.Mods.Multiplayer.dll "C:\Program Files\Kitten Space Agency\Content\Multiplayer\"
   ```

2. Build server:
   ```powershell
   cd Server
   dotnet build -c Release
   copy bin\Release\net10.0\KSA-Dedicated-Server.dll "C:\Program Files\Kitten Space Agency\"
   copy bin\Release\net10.0\KSA-Dedicated-Server.deps.json "C:\Program Files\Kitten Space Agency\"
   ```

3. Run server from KSA folder (uses system .NET 10):
   ```powershell
   cd "C:\Program Files\Kitten Space Agency"
   dotnet KSA-Dedicated-Server.dll
   ```

---

## Dependencies

| Component | Dependency | Version | License | Purpose |
|-----------|------------|---------|---------|---------|
| Client | [Harmony](https://github.com/pardeike/Harmony) | 2.x | MIT | Runtime method patching |
| Client | [MemoryPack](https://github.com/Cysharp/MemoryPack) | 1.x | MIT | Binary serialization |
| Server | Brutal Framework | - | - | Networking (RakNet) |
| Server | MemoryPack | 1.x | MIT | Binary serialization |

---

## Technical Details

The mod implements an "immortal vessel" pattern where remote vehicles exist as real Vehicle objects but are excluded from local physics simulation. Only the controlling player simulates physics; others receive position updates.

Key implementation:
- Dedicated server relays messages (star topology)
- Server-authoritative time synchronization (forward-only sync)
- Binary message serialization via MemoryPack
- Situation-aware coordinates (CCI for orbital, CCF for surface)
- Kepler orbital mechanics handle interpolation between sync points

---

## Known Limitations

- Surface vehicle synchronization less tested than orbital

## License

MIT License - see [LICENSE](LICENSE)

## Author

**RacerX** - [@racerx2](https://github.com/racerx2)

## Contributing

Contributions welcome! Submit issues and pull requests.

## Changelog

See [CHANGELOG.md](CHANGELOG.md)
