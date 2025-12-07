# KSA Multiplayer Mod

Multiplayer mod for Kitten Space Agency v2994+

## Requirements

- KSA version 2994 or later
- **.NET 10 Desktop Runtime** (REQUIRED)
  - Download from: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
  - Select ".NET Desktop Runtime" for Windows x64

## Installation

1. **Install .NET 10 Desktop Runtime first!**
2. Run `Install.bat` (or right-click Install.ps1 and Run with PowerShell)
3. Use the "KSA with Mods" shortcut on your desktop

Or specify a custom KSA path:
```
.\Install.ps1 -KSAPath "D:\Games\Kitten Space Agency"
```

## Usage

1. Launch KSA using the "KSA with Mods" shortcut
2. Press ~ to open console
3. Type `mp_ui` to open the Multiplayer window

### Important: System Matching
**All players must be running the same solar system configuration!**

Before connecting, ensure everyone selected the same system at startup:
- Solar System
- Earth and Moon
- Earth Only

If systems don't match, the connection will be refused with an error message.

### Multiplayer Window Features

**Connection Section:**
- Server IP (remembered between sessions)
- Port and Player Name fields
- Host/Join/Disconnect buttons

**Player List:**
- Shows all connected players
- Green [SYNC] = player in same time
- Orange [+X.Xs] = player ahead/behind in time

**Chat:**
- Send and receive messages with other players

**Time Sync:**
- Dropdown to select which player to sync to
- Only shows players who are ahead in time
- Click "Sync" to jump forward and match their time

**Settings:**
- Toggle nametags
- Toggle debug logging

**Cheats (collapsible):**
- Teleport to another player (only available when synced)

**Debug (collapsible):**
- Subspace status and sync info
- Local simulation time and speed
- Network connection details
- Remote vehicle count and events
- Per-player time tracking

### Subspace System (LMP-Style)

Players can time warp independently without breaking multiplayer:

- **In Sync (Yellow markers):** Players at same simulation time can see each other's vessels in 3D and on map
- **Out of Sync (Red markers on map):** Players at different times see each other as "ghosts" - visible on map only, hidden in 3D flight view
- **Sync Up:** Use the Time Sync dropdown to jump forward and match another player's time

This allows:
- Independent time warping for long journeys
- Rejoining other players when ready
- No desync crashes from time differences


### Console Commands
- `mp_ui` - Toggle multiplayer UI window
- `mp_host <n> <port> <maxPlayers>` - Host a server
- `mp_join <n> <ip> <port>` - Join a server
- `mp_disconnect` - Disconnect from session
- `mp_status` - Show connection status
- `mp_chat <message>` - Send chat message
- `mp_vehicles` - List remote vehicles
- `mp_goto <playerName>` - Teleport to a player's vehicle
- `mp_clearlogs` - Clear all log files
- `mp_logdir` - Show log directory path

## Architecture

This mod uses **event-based synchronization** with **LMP-style subspace**:

- Vehicles sync when maneuvers occur (engine on, RCS, throttle changes)
- Between maneuvers, KSA's deterministic Kepler physics propagates orbits
- Time warp triggers position update when exiting back to 1x
- Players at different simulation times become "ghosts" to each other
- No constant network spam - only syncs when needed

## Log Files

When debug logging is enabled, logs are written to:
`Content\Multiplayer\logs\`

Log files (each includes player name):
- `TimeSync_*.log` - Time synchronization events
- `Subspace_*.log` - Subspace changes
- `Sync_*.log` - Vehicle sync events
- `Players_*.log` - Player join/leave
- `Network_*.log` - Connection status
- `Vehicles_*.log` - Remote vehicles
- `Events_*.log` - Maneuver detection
- `GOTO_*.log` - Teleport operations
- `Renderer_*.log` - Vehicle rendering
- `Patches_*.log` - Harmony patch activity
- `NameTags_*.log` - Nametag rendering

## Troubleshooting

### "You must install .NET Desktop Runtime" error
Download and install .NET 10 Desktop Runtime from:
https://dotnet.microsoft.com/en-us/download/dotnet/10.0

### "System Mismatch" error on connect
All players must select the same solar system at game startup. Restart the game and select the same system as the host.

### Mod doesn't load / No multiplayer menu
Make sure you're launching via "KSA with Mods" shortcut, not the regular KSA.exe

### Vehicles don't appear
1. Check that both players have debug logging enabled
2. Examine the log files in Content\Multiplayer\logs\
3. Ensure players are "in sync" (yellow markers, not red ghosts)

### Markers frozen or not updating
Make sure both players are running the latest version of the mod.

## Package Contents

```
KSA-Multiplayer-Package/
├── Install.ps1
├── Install.bat
├── README.md
├── Launcher/
│   ├── KSA.ModLoader.cmd
│   ├── KSA.ModLoader.exe
│   ├── KSA.ModLoader.dll
│   ├── KSA.ModLoader.deps.json
│   ├── KSA.ModLoader.runtimeconfig.json
│   └── 0Harmony.dll
└── Content/
    └── Multiplayer/
        ├── KSA.Mods.Multiplayer.dll
        └── mod.toml
```

## Version History

### v1.2.0 (Current)
- Updated for KSA v2994
- Fixed ModLoader runtime resolution (moved to Launcher subfolder)
- Desktop shortcut now uses CMD launcher for proper .NET runtime detection

### v1.1.0
- Added LMP-style subspace system for independent time warp
- Added system check (players must use same solar system)
- Added player selection dropdown for time sync
- Ghost mode: out-of-sync players visible on map only (red markers)
- Fixed vehicle destruction on time warp
- Fixed nametag visibility rules for map vs flight view
- Moved Teleport to Cheats section (requires sync)
- Warp-end detection for automatic position updates

### v1.0.0
- Initial release
- Basic multiplayer with vehicle sync
- Chat system
- Nametag rendering
