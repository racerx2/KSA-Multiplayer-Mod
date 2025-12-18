# KSA Multiplayer Mod

![KSA Multiplayer](title.png)

Multiplayer mod for Kitten Space Agency v3057+

## Requirements

- KSA version 3057 or later
- **.NET 10 Desktop Runtime** (REQUIRED)
  - Download from: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
  - Select ".NET Desktop Runtime" for Windows x64

## Installation

### Option 1: Installer (Recommended)
1. **Install .NET 10 Desktop Runtime first!**
2. Run `KSA-Multiplayer-Setup.exe`
3. Select your KSA installation folder
4. Two desktop shortcuts will be created:
   - **KSA with Mods** - Launch game with multiplayer mod
   - **KSA Dedicated Server** - Run a multiplayer server

### Option 2: Manual Install
1. Copy `Launcher\*` to `KSA\Launcher\`
2. Copy `Content\Multiplayer\*` to `KSA\Content\Multiplayer\`
3. Copy `Server\*` to KSA root folder
4. Add to `Content\manifest.toml`:
   ```toml
   [[mods]]
   id = "Multiplayer"
   enabled = true
   ```

## Architecture

This mod uses a **dedicated server** architecture:
- **Server** runs independently, no player needs to host
- **Clients** connect to the server to play together

## Running a Server

1. Double-click "KSA Dedicated Server" shortcut
2. Or run `RunServer.cmd` from KSA folder
3. Edit `server_config.json` to customize:

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
- **In Sync** - See each other's vessels in 3D
- **Out of Sync** - Vessels appear as "ghosts" (map only)
- **Sync Button** - Jump forward to match another player's time


### Chat & Features
- Chat with other players
- See player nametags above vehicles
- Teleport to synced players (Cheats menu)

## Log Files

When debug logging is enabled, logs are in:
`Content\Multiplayer\logs\`

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

### v0.2.0 (Current)
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
