#!/usr/bin/env bash
# Launcher for the KSA Multiplayer Dedicated Server (Linux).
#
# This is a CONSOLE application. It deliberately does not set XDG_SESSION_TYPE,
# DISPLAY or WAYLAND_DISPLAY: the server opens no window and must never touch
# the display server.
#
# No KSA assemblies ship with the server. It locates your Kitten Space Agency
# installation at startup and loads the game's managed assemblies from there;
# .NET then finds libRakNetDLL.so beside them. Point it at the game with any
# one of these, in order of precedence:
#
#   ./run-server.sh -gamepath /path/to/KSA
#   KSA_PATH=/path/to/KSA ./run-server.sh
#   "gamePath": "/path/to/KSA"   in server_config.json
#
# Requires the .NET 10 runtime on PATH.

set -u

SERVERDIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"
DLL="$SERVERDIR/KSA-Dedicated-Server.dll"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "ERROR: 'dotnet' is not on your PATH."
    echo
    echo "Install the .NET 10 runtime, then run this again:"
    echo "  https://dotnet.microsoft.com/download/dotnet/10.0"
    echo
    read -r -p "Press Enter to close..."
    exit 1
fi

if [ ! -f "$DLL" ]; then
    echo "ERROR: KSA-Dedicated-Server.dll was not found next to this script:"
    echo "  $SERVERDIR"
    echo
    read -r -p "Press Enter to close..."
    exit 1
fi

cd "$SERVERDIR" || exit 1
dotnet "$DLL" "$@"
status=$?

echo
echo "Server exited with status $status."
read -r -p "Press Enter to close..."
exit $status
