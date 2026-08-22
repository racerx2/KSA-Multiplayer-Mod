using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KSA.Networking;
using KSA.Networking.Messages;
using KSA.Mods.Multiplayer.Messages;
using Brutal.ImGuiApi;

namespace KSA.Mods.Multiplayer
{
    public static class NetworkPatches
    {
        private static Harmony? _harmony;
        private const string LogName = "Network";
        
        // 140, 201 and 203 were MultiplayerChat, TimeSync and OrbitSync. Chat
        // travels on KSA's own chat message, and time is carried by the
        // heartbeat, so nothing ever sent any of the three. Leave the numbers
        // retired rather than reusing them: a client from before their removal
        // can still put them on the wire.
        public const byte MSG_ID_VEHICLE_STATE = 200;
        public const byte MSG_ID_VEHICLE_DESIGN = 202;
        public const byte MSG_ID_SYSTEM_CHECK = 204;
        public const byte MSG_ID_SERVER_HEARTBEAT = 205;
        public const byte MSG_ID_CRAFT_UPLOAD = 207;
        public const byte MSG_ID_AUTH_STATUS = 208;
        public const byte MSG_ID_CRAFT_LIBRARY = 209;
        public const byte MSG_ID_CRAFT_REQUEST = 210;
        public const byte MSG_ID_VEHICLE_REMOVE = 211;
        public const byte MSG_ID_PLAYER_ROSTER = 212;
        public const byte MSG_ID_VESSEL_STRUCTURE = 213;
        public const byte MSG_ID_CRAFT_DATA = 214;
        public const byte MSG_ID_UNDOCK_REQUEST = 215;
        
        // Heartbeat tracking
        private static DateTime _lastHeartbeatReceived = DateTime.MinValue;
        public static DateTime LastHeartbeatReceived => _lastHeartbeatReceived;
        public static bool HasReceivedHeartbeat => _lastHeartbeatReceived != DateTime.MinValue;
        
        // Connection error tracking - captures server messages before disconnect
        private static string? _lastServerMessage = null;
        
        public static event Action<string>? OnChatMessageReceived;
        public static event Action<VehicleStateMessage>? OnVehicleStateReceived;
        public static event Action<VehicleDesignSyncMessage>? OnVehicleDesignSyncReceived;
        public static event Action<VesselStructureMessage>? OnVesselStructureReceived;
        public static event Action<SystemCheckMessage>? OnSystemCheckReceived;
        public static event Action<ServerHeartbeatMessage>? OnServerHeartbeatReceived;
        public static event Action<AuthStatusMessage>? OnAuthStatusReceived;
        public static event Action<VehicleRemoveMessage>? OnVehicleRemoveReceived;
        public static event Action<PlayerRosterMessage>? OnPlayerRosterReceived;
        public static event Action<CraftLibraryMessage>? OnCraftLibraryReceived;
        public static event Action<CraftDataMessage>? OnCraftDataReceived;
        public static event Action<UndockRequestMessage>? OnUndockRequestReceived;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        public static void ApplyPatches()
        {
            Log("ApplyPatches() called");
            
            try
            {
                _harmony = new Harmony("com.ksa.mods.multiplayer.network");
                
                var chatMethod = AccessTools.Method(typeof(DisplayChatMessage), "Execute");
                if (chatMethod != null)
                {
                    var chatPrefix = AccessTools.Method(typeof(NetworkPatches), nameof(DisplayChatMessagePrefix));
                    _harmony.Patch(chatMethod, prefix: new HarmonyMethod(chatPrefix));
                }
                
                var deserialiseMethod = AccessTools.Method(typeof(MessageSerialisation), "Deserialise");
                if (deserialiseMethod != null)
                {
                    var deserialisePrefix = AccessTools.Method(typeof(NetworkPatches), nameof(DeserialisePrefix));
                    _harmony.Patch(deserialiseMethod, prefix: new HarmonyMethod(deserialisePrefix));
                }

                var programMenusHook = AccessTools.Method(
                    typeof(KSA.Program), "DrawProgramMenusHook");
                if (programMenusHook != null)
                {
                    var menuPostfix = AccessTools.Method(
                        typeof(NetworkPatches), nameof(DrawProgramMenusHookPostfix));
                    _harmony.Patch(
                        programMenusHook, postfix: new HarmonyMethod(menuPostfix));
                    Log("Added Multiplayer menu beside HUD");
                }
                
                // Patch ExecuteJoinGameResponse to skip universe deserialization.
                var executeJoinMethod = AccessTools.Method(typeof(NetworkClient), "ExecuteJoinGameResponse");
                if (executeJoinMethod != null)
                {
                    var joinPrefix = AccessTools.Method(typeof(NetworkPatches), nameof(ExecuteJoinGameResponsePrefix));
                    _harmony.Patch(executeJoinMethod, prefix: new HarmonyMethod(joinPrefix));
                    Log("Patched ExecuteJoinGameResponse - universe sync disabled");
                }
                else
                {
                    Log("WARNING: Could not find ExecuteJoinGameResponse to patch!");
                }
                
                // Patch DispatchToAllPlayers to suppress "non authority" warning
                var dispatchMethod = AccessTools.Method(typeof(NetworkPeer), "DispatchToAllPlayers");
                if (dispatchMethod != null)
                {
                    var dispatchPrefix = AccessTools.Method(typeof(NetworkPatches), nameof(DispatchToAllPlayersPrefix));
                    _harmony.Patch(dispatchMethod, prefix: new HarmonyMethod(dispatchPrefix));
                    Log("Patched DispatchToAllPlayers - suppressed non-authority warning");
                }
                
                Log("Network patches applied successfully");
            }
            catch (Exception ex)
            {
                Log($"Patch FAILED: {ex.Message}");
            }
        }
        
        public static bool DisplayChatMessagePrefix(DisplayChatMessage __instance)
        {
            if (!string.IsNullOrEmpty(__instance.Message))
            {
                // Capture server messages for connection error display.
                if (__instance.Message.StartsWith("[Server]"))
                {
                    _lastServerMessage = __instance.Message.Replace("[Server] ", "").Trim();
                    Log($"Server message captured: {_lastServerMessage}");
                }
                
                OnChatMessageReceived?.Invoke(__instance.Message);
            }
            return true;
        }

        public static void DrawProgramMenusHookPostfix()
        {
            if (!ImGui.BeginMenu("Multiplayer"))
                return;

            MultiplayerWindow? window = ModEntry.GetMultiplayerWindow();
            if (ImGui.MenuItem("Open Multiplayer"))
                window?.SetShown(true);

            if (window?.IsShown == true && ImGui.MenuItem("Hide Multiplayer"))
                window.SetShown(false);

            ImGui.EndMenu();
        }
        
        /// <summary>Replaces DispatchToAllPlayers without the non-authority warning log.</summary>
        public static bool DispatchToAllPlayersPrefix(NetworkPeer __instance, GameMessage message)
        {
            // Execute the message locally without the warning log.
            if (Players.HasLocalPlayer)
            {
                message.Execute();
            }
            
            // Access protected Session property via reflection
            var sessionProp = AccessTools.Property(typeof(NetworkPeer), "Session");
            var session = sessionProp?.GetValue(__instance) as NetworkSession;
            session?.BroadcastToAll(message.Serialise());
            
            return false; // Skip original method
        }
        
        /// <summary>
        /// Stands in for a message that could not be read. KSA's OnPeerPacket calls
        /// Shutdown() when deserialisation returns null, from inside the packet loop that
        /// is still holding the packet, which disposes the RakNet instance out from under
        /// it. Handing back a message whose Execute does nothing avoids that.
        /// </summary>
        private sealed class UnreadableMessage : GameMessage
        {
            public UnreadableMessage(byte messageId) : base((GameMessageId)messageId) { }

            public override void Execute() { }
        }

        /// <summary>Reads the mod's own messages, and never lets a bad one reach KSA.</summary>
        public static bool DeserialisePrefix(DecodedPacket packet, ref GameMessage? __result)
        {
            byte messageId = (byte)packet.MessageId;

            try
            {
                if (!DispatchModMessage(messageId, packet, ref __result))
                {
                    // MemoryPack returns null for some corrupt payloads rather than
                    // throwing, and a null result is what makes KSA shut down. Verified
                    // in Tests/Program.cs: 0xFF bytes deserialise to null, a truncated
                    // payload throws.
                    if (__result == null)
                    {
                        ModLogger.LogAlways(LogName,
                            $"DISCARDED unreadable message {messageId}");
                        __result = new UnreadableMessage(messageId);
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                // A truncated or corrupt payload throws out of MemoryPack. Unhandled it
                // would unwind through ProcessAllWaitingPackets and kill the process.
                ModLogger.LogAlways(LogName,
                    $"DISCARDED malformed message {messageId}: {ex.GetType().Name}: {ex.Message}");
                __result = new UnreadableMessage(messageId);
                return false;
            }

            // KSA deserialises its own ids itself.
            if (messageId >= (byte)GameMessageId.FirstGameMessageId &&
                messageId <= (byte)GameMessageId.PlayerStatusChanged)
                return true;

            // Anything else reaches KSA's "No deserialisation for ..." throw, which unwinds
            // out of the packet loop just the same. Drop it here instead.
            ModLogger.LogThrottled(LogName, "UNKNOWN_MSG",
                $"Discarded unknown message id {messageId}");
            __result = new UnreadableMessage(messageId);
            return false;
        }

        /// <summary>Returns true when the message is KSA's to deserialise, not the mod's.</summary>
        private static bool DispatchModMessage(
            byte messageId, DecodedPacket packet, ref GameMessage? __result)
        {
            // Throttle high-frequency deserialize logging (only for mod messages)
            if (messageId >= 140)
                ModLogger.LogThrottled(LogName, "DESERIALIZE", $"DESERIALIZE: MessageId={messageId}");
            
            switch (messageId)
            {
                case MSG_ID_VEHICLE_STATE:
                    var stateMessage = GameMessage.Deserialise<VehicleStateMessage>(packet.Payload);
                    if (stateMessage != null)
                    {
                        stateMessage.Id = (GameMessageId)MSG_ID_VEHICLE_STATE;
                        // Throttle high-frequency state message logging
                        ModLogger.LogThrottled(LogName, "STATE_MSG", 
                            $"STATE MSG - Owner: {stateMessage.OwnerPlayerName}, Engine: {stateMessage.EngineOn}, Throttle: {stateMessage.EngineThrottle:F2}, RCS: {stateMessage.ThrusterFlags}, Nozzles: {stateMessage.RocketThrusts?.Length ?? 0}");
                        OnVehicleStateReceived?.Invoke(stateMessage);
                        if (Network.ActivePeer is NetworkServer)
                        {
                            ModLogger.LogThrottled(LogName, "STATE_RELAY", "STATE MSG RELAY - Server relaying to all");
                            Network.ActivePeer.DispatchToAllPlayers(stateMessage);
                        }
                    }
                    __result = stateMessage;
                    return false;
                    
                case MSG_ID_VESSEL_STRUCTURE:
                    var structureMessage = GameMessage.Deserialise<VesselStructureMessage>(packet.Payload);
                    if (structureMessage != null)
                    {
                        structureMessage.Id = (GameMessageId)MSG_ID_VESSEL_STRUCTURE;
                        Log($"STRUCTURE MSG - {(structureMessage.Action == 0 ? "SPLIT" : "DOCK")} " +
                            $"from {structureMessage.PlayerName}: {structureMessage.PrimaryUid}");
                        OnVesselStructureReceived?.Invoke(structureMessage);
                        if (Network.ActivePeer is NetworkServer)
                            Network.ActivePeer.DispatchToAllPlayers(structureMessage);
                    }
                    __result = structureMessage;
                    return false;

                case MSG_ID_UNDOCK_REQUEST:
                    var undockMessage = GameMessage.Deserialise<UndockRequestMessage>(packet.Payload);
                    if (undockMessage != null)
                    {
                        undockMessage.Id = (GameMessageId)MSG_ID_UNDOCK_REQUEST;
                        Log($"UNDOCK MSG - status={undockMessage.Status} #{undockMessage.RequestId} " +
                            $"{undockMessage.RequesterPlayerName} -> {undockMessage.OwnerPlayerName} " +
                            $"({undockMessage.StackUid})");
                        OnUndockRequestReceived?.Invoke(undockMessage);
                        if (Network.ActivePeer is NetworkServer)
                            Network.ActivePeer.DispatchToAllPlayers(undockMessage);
                    }
                    __result = undockMessage;
                    return false;

                case MSG_ID_VEHICLE_DESIGN:
                    Log($"DESIGN MSG RECEIVED - MessageId: {messageId}");
                    var designMessage = GameMessage.Deserialise<VehicleDesignSyncMessage>(packet.Payload);
                    if (designMessage != null)
                    {
                        Log($"DESIGN MSG PARSED - Owner: {designMessage.OwnerPlayerName}, Vehicle: {designMessage.VehicleId}, Template: {designMessage.TemplateId}");
                        designMessage.Id = (GameMessageId)MSG_ID_VEHICLE_DESIGN;
                        OnVehicleDesignSyncReceived?.Invoke(designMessage);
                        if (Network.ActivePeer is NetworkServer)
                        {
                            Log($"DESIGN MSG RELAY - Server relaying to all players");
                            Network.ActivePeer.DispatchToAllPlayers(designMessage);
                        }
                    }
                    else
                    {
                        Log($"DESIGN MSG FAILED TO PARSE");
                    }
                    __result = designMessage;
                    return false;

                case MSG_ID_SYSTEM_CHECK:
                    Log($"SYSTEM CHECK MSG RECEIVED - MessageId: {messageId}");
                    var systemCheckMessage = GameMessage.Deserialise<SystemCheckMessage>(packet.Payload);
                    if (systemCheckMessage != null)
                    {
                        Log($"SYSTEM CHECK - Host System: {systemCheckMessage.HostSystemId} ({systemCheckMessage.HostSystemDisplayName})");
                        systemCheckMessage.Id = (GameMessageId)MSG_ID_SYSTEM_CHECK;
                        OnSystemCheckReceived?.Invoke(systemCheckMessage);
                        // System check is not relayed.
                    }
                    __result = systemCheckMessage;
                    return false;
                    
                case MSG_ID_SERVER_HEARTBEAT:
                    // Parse heartbeat with server time
                    _lastHeartbeatReceived = DateTime.UtcNow;
                    var heartbeatMsg = GameMessage.Deserialise<ServerHeartbeatMessage>(packet.Payload);
                    if (heartbeatMsg != null)
                    {
                        heartbeatMsg.Id = (GameMessageId)MSG_ID_SERVER_HEARTBEAT;
                        OnServerHeartbeatReceived?.Invoke(heartbeatMsg);
                    }
                    else
                    {
                        // Fallback for old format (just the byte)
                        heartbeatMsg = new ServerHeartbeatMessage();
                    }
                    __result = heartbeatMsg;
                    return false;

                case MSG_ID_AUTH_STATUS:
                    var authStatus = GameMessage.Deserialise<AuthStatusMessage>(packet.Payload);
                    if (authStatus != null)
                    {
                        authStatus.Id = (GameMessageId)MSG_ID_AUTH_STATUS;
                        Log($"AUTH STATUS: Success={authStatus.Success}, Player={authStatus.PlayerName}");
                        OnAuthStatusReceived?.Invoke(authStatus);
                    }
                    __result = authStatus;
                    return false;

                case MSG_ID_VEHICLE_REMOVE:
                    var vehicleRemove = GameMessage.Deserialise<VehicleRemoveMessage>(packet.Payload);
                    if (vehicleRemove != null)
                    {
                        vehicleRemove.Id = (GameMessageId)MSG_ID_VEHICLE_REMOVE;
                        OnVehicleRemoveReceived?.Invoke(vehicleRemove);
                    }
                    __result = vehicleRemove;
                    return false;

                case MSG_ID_PLAYER_ROSTER:
                    var roster = GameMessage.Deserialise<PlayerRosterMessage>(packet.Payload);
                    if (roster != null)
                    {
                        roster.Id = (GameMessageId)MSG_ID_PLAYER_ROSTER;
                        OnPlayerRosterReceived?.Invoke(roster);
                    }
                    __result = roster;
                    return false;

                case MSG_ID_CRAFT_LIBRARY:
                    var craftLibrary = GameMessage.Deserialise<CraftLibraryMessage>(packet.Payload);
                    if (craftLibrary != null)
                    {
                        craftLibrary.Id = (GameMessageId)MSG_ID_CRAFT_LIBRARY;
                        Log($"CRAFT LIBRARY: {craftLibrary.Entries?.Length ?? 0} craft");
                        OnCraftLibraryReceived?.Invoke(craftLibrary);
                    }
                    __result = craftLibrary;
                    return false;

                case MSG_ID_CRAFT_DATA:
                    var craftData = GameMessage.Deserialise<CraftDataMessage>(packet.Payload);
                    if (craftData != null)
                    {
                        craftData.Id = (GameMessageId)MSG_ID_CRAFT_DATA;
                        Log(string.IsNullOrEmpty(craftData.Error)
                            ? $"CRAFT DATA: {craftData.CraftName} by {craftData.OwnerPlayerName}, " +
                              $"{craftData.CompressedVehicleXml?.Length ?? 0} bytes"
                            : $"CRAFT ERROR: {craftData.Error}");
                        OnCraftDataReceived?.Invoke(craftData);
                    }
                    __result = craftData;
                    return false;
                    
                default:
                    return true;
            }
        }
        
        /// <summary>Why the server refused the last join, or null.</summary>
        public static string? JoinRefusedReason { get; private set; }

        /// <summary>Raised with the server's reason when a join is refused.</summary>
        public static event Action<string>? OnJoinRefused;

        /// <summary>Clears the recorded join refusal.</summary>
        public static void ClearJoinRefusal() => JoinRefusedReason = null;

        /// <summary>Skips Universe.DeserializeSave when joining a game.</summary>
        public static bool ExecuteJoinGameResponsePrefix(JoinGameResponseMessage message, NetworkClient __instance)
        {
            if (!message.Accepted)
            {
                // The stock handler calls Shutdown() from here, which disposes the RakNet
                // instance that ProcessAllWaitingPackets is still holding a packet from.
                // That loop then calls DeallocatePacket and Receive on freed memory and
                // the process dies. The refusal is recorded instead, and the disconnect
                // is carried out a frame later, outside the packet loop.
                JoinRefusedReason = string.IsNullOrWhiteSpace(message.Message)
                    ? "The server refused the connection."
                    : message.Message.Trim();

                Log($"Join game denied by server: {JoinRefusedReason}");
                OnJoinRefused?.Invoke(JoinRefusedReason);
                return false;
            }
            
            Log("Join game accepted - keeping local universe (skipping DeserializeSave)");
            
            // Set the players list.
            Players.Set(message.Players?
                .Where(player => player.Value != null)
                .ToList() ?? new List<KeyValuePair<ClientId, PlayerInfo>>());
            Log($"Players list updated: {Players.Count} players");
            
            // Universe deserialization and OnLoaded are skipped.
            
            // Set _netNetStatus to InGame (value = 2) via reflection
            var statusField = AccessTools.Field(typeof(NetworkClient), "_netNetStatus");
            if (statusField != null)
            {
                statusField.SetValue(__instance, 2); // NetStatus.InGame = 2
                Log("Set network status to InGame");
            }
            else
            {
                Log("WARNING: Could not find _netNetStatus field!");
            }
            
            return false; // Skip original method
        }
        
        public static void ResetHeartbeat()
        {
            _lastHeartbeatReceived = DateTime.MinValue;
        }
        
        /// <summary>Returns and clears the last server message.</summary>
        public static string? ConsumeServerMessage()
        {
            var msg = _lastServerMessage;
            _lastServerMessage = null;
            return msg;
        }
        
        /// <summary>Clears the last server message.</summary>
        public static void ClearServerMessage()
        {
            _lastServerMessage = null;
        }
        
        public static void RemovePatches()
        {
            _harmony?.UnpatchAll("com.ksa.mods.multiplayer.network");
        }
    }
}
