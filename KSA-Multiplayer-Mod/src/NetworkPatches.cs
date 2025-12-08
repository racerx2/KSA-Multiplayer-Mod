using System;
using HarmonyLib;
using KSA.Networking;
using KSA.Networking.Messages;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    public static class NetworkPatches
    {
        private static Harmony? _harmony;
        private const string LogName = "Network";
        
        public const byte MSG_ID_VEHICLE_STATE = 200;
        public const byte MSG_ID_TIME_SYNC = 201;
        public const byte MSG_ID_VEHICLE_DESIGN = 202;
        public const byte MSG_ID_ORBIT_SYNC = 203;
        public const byte MSG_ID_MULTIPLAYER_CHAT = 140;
        public const byte MSG_ID_SYSTEM_CHECK = 204;
        
        public static event Action<string>? OnChatMessageReceived;
        public static event Action<VehicleStateMessage>? OnVehicleStateReceived;
        public static event Action<TimeSyncMessage>? OnTimeSyncReceived;
        public static event Action<VehicleDesignSyncMessage>? OnVehicleDesignSyncReceived;
        public static event Action<OrbitSyncMessage>? OnOrbitSyncReceived;
        public static event Action<SystemCheckMessage>? OnSystemCheckReceived;
        
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
                
                // CRITICAL: Patch ExecuteJoinGameResponse to skip Universe.DeserializeSave
                // Each player keeps their own universe - we only sync vehicle data, not universe state
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
                OnChatMessageReceived?.Invoke(__instance.Message);
            return true;
        }
        
        public static bool DeserialisePrefix(DecodedPacket packet, ref GameMessage? __result)
        {
            byte messageId = (byte)packet.MessageId;
            
            // Throttle high-frequency deserialize logging (only for mod messages)
            if (messageId >= 140)
                ModLogger.LogThrottled(LogName, "DESERIALIZE", $"DESERIALIZE: MessageId={messageId}");
            
            switch (messageId)
            {
                case MSG_ID_MULTIPLAYER_CHAT:
                    var chatMessage = GameMessage.Deserialise<MultiplayerChatMessage>(packet.Payload);
                    if (chatMessage != null)
                    {
                        chatMessage.Id = (GameMessageId)MSG_ID_MULTIPLAYER_CHAT;
                        chatMessage.Execute();
                        if (Network.ActivePeer is NetworkServer)
                            Network.ActivePeer.DispatchToAllPlayers(chatMessage);
                    }
                    __result = chatMessage;
                    return false;
                    
                case MSG_ID_VEHICLE_STATE:
                    var stateMessage = GameMessage.Deserialise<VehicleStateMessage>(packet.Payload);
                    if (stateMessage != null)
                    {
                        stateMessage.Id = (GameMessageId)MSG_ID_VEHICLE_STATE;
                        // Throttle high-frequency state message logging
                        ModLogger.LogThrottled(LogName, "STATE_MSG", 
                            $"STATE MSG - Owner: {stateMessage.OwnerPlayerName}, Engine: {stateMessage.EngineOn}, Throttle: {stateMessage.EngineThrottle:F2}, RCS: {stateMessage.ThrusterFlags}");
                        OnVehicleStateReceived?.Invoke(stateMessage);
                        if (Network.ActivePeer is NetworkServer)
                        {
                            ModLogger.LogThrottled(LogName, "STATE_RELAY", "STATE MSG RELAY - Server relaying to all");
                            Network.ActivePeer.DispatchToAllPlayers(stateMessage);
                        }
                    }
                    __result = stateMessage;
                    return false;
                    
                case MSG_ID_TIME_SYNC:
                    Log($"TIME_SYNC MSG RECEIVED");
                    var timeSyncMessage = GameMessage.Deserialise<TimeSyncMessage>(packet.Payload);
                    if (timeSyncMessage != null)
                    {
                        timeSyncMessage.Id = (GameMessageId)MSG_ID_TIME_SYNC;
                        Log($"TIME_SYNC PARSED - SimTime={timeSyncMessage.SimulationTimeSeconds:F3}s, Seq={timeSyncMessage.SequenceNumber}");
                        OnTimeSyncReceived?.Invoke(timeSyncMessage);
                        if (Network.ActivePeer is NetworkServer)
                            Network.ActivePeer.DispatchToAllPlayers(timeSyncMessage);
                    }
                    __result = timeSyncMessage;
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
                    
                case MSG_ID_ORBIT_SYNC:
                    Log($"ORBIT SYNC MSG RECEIVED - MessageId: {messageId}");
                    var orbitMessage = GameMessage.Deserialise<OrbitSyncMessage>(packet.Payload);
                    if (orbitMessage != null)
                    {
                        Log($"ORBIT SYNC PARSED - Player: {orbitMessage.PlayerName}, IsAck: {orbitMessage.IsAcknowledgment}");
                        orbitMessage.Id = (GameMessageId)MSG_ID_ORBIT_SYNC;
                        OnOrbitSyncReceived?.Invoke(orbitMessage);
                        if (Network.ActivePeer is NetworkServer)
                        {
                            Log($"ORBIT SYNC RELAY - Server relaying to all players");
                            Network.ActivePeer.DispatchToAllPlayers(orbitMessage);
                        }
                    }
                    __result = orbitMessage;
                    return false;
                    
                case MSG_ID_SYSTEM_CHECK:
                    Log($"SYSTEM CHECK MSG RECEIVED - MessageId: {messageId}");
                    var systemCheckMessage = GameMessage.Deserialise<SystemCheckMessage>(packet.Payload);
                    if (systemCheckMessage != null)
                    {
                        Log($"SYSTEM CHECK - Host System: {systemCheckMessage.HostSystemId} ({systemCheckMessage.HostSystemDisplayName})");
                        systemCheckMessage.Id = (GameMessageId)MSG_ID_SYSTEM_CHECK;
                        OnSystemCheckReceived?.Invoke(systemCheckMessage);
                        // DO NOT relay system check - it's only sent host -> client
                    }
                    __result = systemCheckMessage;
                    return false;
                    
                default:
                    return true;
            }
        }
        
        /// <summary>
        /// Skip Universe.DeserializeSave when joining a game.
        /// Each player keeps their own universe state and simulation time.
        /// We only use KSA networking for the transport layer (RakNet).
        /// Vehicle data is exchanged separately via our sync system.
        /// </summary>
        public static bool ExecuteJoinGameResponsePrefix(JoinGameResponseMessage message, NetworkClient __instance)
        {
            if (!message.Accepted)
            {
                Log($"Join game denied by server: {message.Message}");
                // Let original handle the denial/shutdown
                return true;
            }
            
            Log("Join game accepted - keeping local universe (skipping DeserializeSave)");
            
            // Set the players list - this is safe and needed
            Players.Set(message.Players);
            Log($"Players list updated: {Players.Count} players");
            
            // SKIP Universe.DeserializeSave(message.UniverseData)
            // SKIP Universe.OnLoaded()
            // Each player keeps their own universe, own vehicles, own sim time
            
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
        
        public static void RemovePatches()
        {
            _harmony?.UnpatchAll("com.ksa.mods.multiplayer.network");
        }
    }
}
