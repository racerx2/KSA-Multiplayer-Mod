# KSA Multiplayer Mod

![KSA Multiplayer](title.png)

Multiplayer mod for Kitten Space Agency v3103+

## Requirements

- KSA version 3103 or later
- Windows 10 or later
- Internet access during installation

## Installation

### Option 1: Installer (Recommended)
1. Run `KSA-Multiplayer-Setup-v0.3.0.exe`.
2. Select your KSA installation folder.
3. Start the game using the **KSA with Mods** desktop shortcut.

The installer adds the client mod, a pinned StarMap loader, and a private pinned
.NET runtime. It does not run or install a dedicated server on the player's PC.

### Option 2: Manual Install
Run `scripts\Update-KSAMultiplayer.ps1` from the extracted source or release
bundle. The script requests administrator access and performs the same managed
client installation as Setup.

## Architecture

This mod uses a **dedicated server** architecture:
- **Server** runs independently, no player needs to host
- **Clients** connect to the server to play together

## Running a Server

The server is a separate deployment and is not installed by the player Setup.
Use the Docker deployment in `Server/` and edit `server_config.json` to
customize it:

```json
{
  "ServerName": "My KSA Server",
  "Port": 7777,
  "MaxPlayers": 8,
  "Password": "",
  "SystemId": "Sol",
  "Motd": "Welcome!"
}
```

### Server Commands
- `help` - Show available commands
- `status` - Show server status
- `list` - List connected players
- `kick <name>` - Kick a player
- `ban <name>` - Ban a player
- `unban <ip>` - Unban an IP
- `say <msg>` - Broadcast message
- `quit` - Shutdown server

### Port Forwarding
To allow players outside your network to connect:
1. Forward port 7777 UDP in your router
2. Share your public IP address with players

## Playing

### Important: System Matching
**All players must run the same solar system as configured in the server's `server_config.json`!**
- Solar System
- Earth and Moon  
- Earth Only

### Connecting
1. Launch "KSA with Mods"
2. Open Multiplayer window
3. Enter server IP, port, name, password
4. Click Connect

### Time Sync & Subspace
Players can time warp independently:
- Remote vessels remain visible in 3D in every subspace
- **Sync Button** - Jump forward to match another player's time


### Chat & Features
- Chat with other players
- See player nametags above vehicles
- Teleport to synced players (Cheats menu)

## Log Files

When debug logging is enabled, logs are in:
`%LOCALAPPDATA%\KSA-Multiplayer\logs\`

## Troubleshooting

### "You must install .NET Desktop Runtime" error
Download and install .NET 10 Desktop Runtime from:
https://dotnet.microsoft.com/en-us/download/dotnet/10.0

### "System Mismatch" error
All players must select the same solar system at game startup.

### Mod doesn't load
Make sure you're launching via "KSA with Mods" shortcut.

### Can't connect to server
- Check server is running
- Verify IP address and port
- Check firewall allows port 7777 UDP
- If remote server, ensure port forwarding is configured

### Vehicles don't appear
- Enable debug logging
- Check log files
- Ensure players are "in sync"

## Package Contents

```
KSA-Multiplayer-Package/
├── installer.nsi
├── README.md
├── Launcher/
│   ├── KSA.ModLoader.exe
│   ├── KSA.ModLoader.dll
│   └── ...
├── Content/
│   └── Multiplayer/
│       ├── KSA.Mods.Multiplayer.dll
│       └── mod.toml
└── Server/
    ├── KSA-Dedicated-Server.exe
    ├── KSA-Dedicated-Server.dll
    └── server_config.json
```

## Version History

### v0.3.0 (Current)

Successor to v0.2.1, the last published release. Consolidates all work since,
including changes previously listed under 0.3.x and 0.4.x numbers that were
never released from this repository. See CHANGELOG.md for the full list.

- Cross-player docking, with control handed to the initiator and returned on
  undock; ownership and each player's camera stay with their own craft
- Remote craft advanced to the frame's instant, so distances between a local and
  a remote vessel refer to one moment rather than two
- Empty authoritative shared universe with late-join design and state replay
- Compressed part-tree transfer so other clients reconstruct the actual craft
- Player chat delivered through the dedicated server
- Join-time name validation: no placeholder, duplicate, empty or separator names
- Relicensed to PolyForm Noncommercial 1.0.0
- Smooths nearby vessels using real elapsed time, coalesces buffered movement to
  the newest snapshot, and runs a 15 Hz nearby update rate
- Deployment tooling

### v0.2.0
- **Dedicated server architecture** - Server runs independently (requires KSA installation for game DLLs)
- Server-authoritative time synchronization
- Password protection with timeout
- Server console commands (kick, ban, say, etc.)
- MOTD (Message of the Day)
- Time warp now works correctly (no longer pulled back to real time)
- StarMap mod loader compatibility
- KSA v3057 support

### v0.1.0
- Initial release for KSA v3014
- Host/client architecture
- Real-time vehicle synchronization
- LMP-style subspace system
- In-game chat
- Player nametags
