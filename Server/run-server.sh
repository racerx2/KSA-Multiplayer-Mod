#!/usr/bin/env bash
# Launcher for the KSA Multiplayer Dedicated Server (Linux).
#
# This is a CONSOLE application. It deliberately does NOT set XDG_SESSION_TYPE,
# DISPLAY or WAYLAND_DISPLAY: the server opens no window and must never touch the
# display server.
#
# No KSA assemblies are shipped with the server. It locates the local Kitten Space
# Agency installation at startup and loads the game's managed assemblies from there
# (see KsaInstall.cs); .NET then finds libRakNetDLL.so beside them. Set the install
# path with "gamePath" in server_config.json, the KSA_PATH environment variable, or
# -gamepath <dir>.

SERVERDIR="$(dirname "$(readlink -f "$0")")/bin/Release/net10.0"

if [ ! -x "$SERVERDIR/KSA-Dedicated-Server" ]; then
    echo "ERROR: server binary not found at:"
    echo "  $SERVERDIR/KSA-Dedicated-Server"
    echo
    echo "Build it with:  dotnet build -c Release"
    echo
    read -r -p "Press Enter to close..."
    exit 1
fi

cd "$SERVERDIR" || exit 1
./KSA-Dedicated-Server "$@"
status=$?

echo
echo "Server exited with status $status."
read -r -p "Press Enter to close..."
exit $status
