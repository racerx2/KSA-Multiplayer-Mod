using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Brutal.Logging;
using KSA.Networking;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    public class MultiplayerManager
    {
        public static MultiplayerManager? Instance { get; private set; }
        
        private NetworkManager? _networkManager;
        private EventSyncManager? _syncManager;
        private ChatManager? _chatManager;
        private RemoteVehicleRenderer? _vehicleRenderer;
        private SubspaceManager? _subspaceManager;
        private NameTagRenderer? _nameTagRenderer;
        private List<string> _connectedPlayers;
        private bool _isInitialized;
        private string? _localPlayerName;
        private string? _systemMismatchError;
        private bool _pendingDisconnectDueToMismatch;
        
        public bool IsHost { get; private set; }
        public bool IsConnected => _networkManager?.IsConnected ?? false;
        public string? LocalPlayerName => _localPlayerName;
        public string? SystemMismatchError => _systemMismatchError;
        public NetworkManager? NetworkManager => _networkManager;
        public EventSyncManager? SyncManager => _syncManager;
        public ChatManager? ChatManager => _chatManager;
        public RemoteVehicleRenderer? VehicleRenderer => _vehicleRenderer;
        public SubspaceManager? SubspaceManager => _subspaceManager;
        public NameTagRenderer? NameTagRenderer => _nameTagRenderer;
        public IReadOnlyList<string> ConnectedPlayers => _networkManager?.GetPlayerNames() ?? new List<string>();
        
        public MultiplayerManager()
        {
            _connectedPlayers = new List<string>();
        }
        
        public void Initialize()
        {
            if (_isInitialized) return;
            
            Instance = this;
            _networkManager = new NetworkManager();
            _syncManager = new EventSyncManager(_networkManager);
            _chatManager = new ChatManager(_networkManager);
            _vehicleRenderer = new RemoteVehicleRenderer(_syncManager);
            _subspaceManager = new SubspaceManager(_networkManager);
            _nameTagRenderer = new NameTagRenderer(this);
            
            // Wire up VehiclePatches to use SubspaceManager for visibility checks
            VehiclePatches.SetSubspaceManager(_subspaceManager);
            
            // Wire up EventSyncManager to update player times
            _syncManager.SetSubspaceManager(_subspaceManager);
            
            // Wire up RemoteVehicleRenderer for visual effect visibility
            _vehicleRenderer.SetSubspaceManager(_subspaceManager);
            
            _networkManager.OnPlayerConnected += OnPlayerConnected;
            _networkManager.OnPlayerDisconnected += OnPlayerDisconnected;
            _networkManager.OnConnectionFailed += OnConnectionFailed;
            _networkManager.OnDisconnected += OnDisconnected;
            
            // Subscribe to system check messages for validation
            NetworkPatches.OnSystemCheckReceived += OnSystemCheckReceived;
            
            // Subscribe to time sync messages for clock synchronization
            NetworkPatches.OnTimeSyncReceived += OnTimeSyncReceived;
            
            _isInitialized = true;
        }
        
        public void Update(double deltaTime)
        {
            if (!_isInitialized) return;
            
            _networkManager?.Update();
            
            // Handle deferred disconnect due to system mismatch (can't disconnect during callback)
            if (_pendingDisconnectDueToMismatch)
            {
                _pendingDisconnectDueToMismatch = false;
                ModLogger.Log("Network", "Executing deferred disconnect due to system mismatch");
                Disconnect();
                return;
            }
            
            if (IsConnected)
            {
                _syncManager?.Update(deltaTime);
                _chatManager?.Update(deltaTime);
                _vehicleRenderer?.Update(deltaTime);
                // SubspaceManager doesn't need Update - player times are updated on message receive
                
                // Periodic heartbeat logging
                double currentTime = KSA.Universe.GetElapsedSimTime().Seconds();
                ModLogger.LogHeartbeat(currentTime);
            }
        }
        
        public bool HostSession(string playerName, ushort port, ushort maxPlayers)
        {
            ModLogger.Log("Network", $"Starting host session: {playerName} on port {port}, max {maxPlayers} players");
            ModLogger.PlayerName = playerName;
            
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            var result = _networkManager?.StartHost(port, maxPlayers, playerName) ?? NetworkSession.StartNetworkResult.FailedToConnect;
            
            if (result == NetworkSession.StartNetworkResult.Success)
            {
                IsHost = true;
                _localPlayerName = playerName;
                _syncManager?.SetLocalPlayerName(playerName);
                _subspaceManager?.SetLocalPlayerName(playerName);
                ModLogger.Log("Network", "Host session started successfully");
                ModLogger.Log("Subspace", $"Host {playerName} initialized");
                return true;
            }
            ModLogger.Log("Network", $"Failed to start host session: {result}");
            return false;
        }
        
        public async Task<bool> JoinSession(string playerName, string serverAddress, ushort port)
        {
            ModLogger.Log("Network", $"Joining session: {playerName} connecting to {serverAddress}:{port}");
            ModLogger.PlayerName = playerName;
            
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            var result = await (_networkManager?.JoinGame(serverAddress, port, playerName) ?? Task.FromResult(NetworkSession.StartNetworkResult.FailedToConnect));
            
            if (result == NetworkSession.StartNetworkResult.Success)
            {
                IsHost = false;
                _localPlayerName = playerName;
                _syncManager?.SetLocalPlayerName(playerName);
                _subspaceManager?.SetLocalPlayerName(playerName);
                ModLogger.Log("Network", "Joined session successfully");
                return true;
            }
            ModLogger.Log("Network", $"Failed to join session: {result}");
            return false;
        }
        
        public void Disconnect()
        {
            _vehicleRenderer?.Dispose();
            _networkManager?.Disconnect();
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            _connectedPlayers.Clear();
            IsHost = false;
            _localPlayerName = null;
            VehiclePatches.ClearRemoteVehicles();
        }
        
        public void Shutdown()
        {
            if (!_isInitialized) return;
            
            Disconnect();
            _vehicleRenderer?.Dispose();
            _networkManager?.Dispose();
            _networkManager = null;
            _syncManager = null;
            _chatManager = null;
            _vehicleRenderer = null;
            _isInitialized = false;
        }
        
        private void OnPlayerConnected(string playerName)
        {
            ModLogger.Log("Players", $"Player connected: {playerName}");
            ModLogger.Log("Network", $"Player connected: {playerName}");
            
            if (!_connectedPlayers.Contains(playerName))
            {
                _connectedPlayers.Add(playerName);
                if (MultiplayerSettings.Current.ShowJoinLeaveMessages)
                    _chatManager?.AddSystemMessage($"{playerName} joined the game");
                
                // Player time will be tracked automatically when we receive their VehicleStateMessages
                ModLogger.Log("Subspace", $"Player {playerName} connected, awaiting time sync");
                
                // Host sends system check to new client
                if (IsHost)
                {
                    SendSystemCheck();
                    SendTimeSync();
                }
            }
        }
        
        private void OnPlayerDisconnected(string playerName)
        {
            ModLogger.Log("Players", $"Player disconnected: {playerName}");
            ModLogger.Log("Network", $"Player disconnected: {playerName}");
            
            if (_connectedPlayers.Contains(playerName))
            {
                _connectedPlayers.Remove(playerName);
                if (MultiplayerSettings.Current.ShowJoinLeaveMessages)
                    _chatManager?.AddSystemMessage($"{playerName} left the game");
            }
            
            // Remove that player's vehicles and time tracking
            _syncManager?.RemovePlayerVehicles(playerName);
            _vehicleRenderer?.RemovePlayerVehicles(playerName);
            _subspaceManager?.RemovePlayer(playerName);
            ModLogger.Log("Vehicles", $"Removed vehicles for disconnected player: {playerName}");
        }
        
        private void OnConnectionFailed(string reason)
        {
            ModLogger.Log("Network", $"Connection failed: {reason}");
            _connectedPlayers.Clear();
            IsHost = false;
        }
        
        private void OnDisconnected()
        {
            ModLogger.Log("Network", "Disconnected from session");
            
            // Clean up remote vehicles when connection drops
            _vehicleRenderer?.Dispose();
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            _connectedPlayers.Clear();
            IsHost = false;
            _localPlayerName = null;
            VehiclePatches.ClearRemoteVehicles();
        }
        
        /// <summary>
        /// Send system check message to all clients (host only)
        /// </summary>
        private void SendSystemCheck()
        {
            if (!IsHost) return;
            
            var currentSystem = SystemLibrary.Default;
            if (currentSystem == null)
            {
                ModLogger.Log("Network", "WARNING: SystemLibrary.Default is null, cannot send system check");
                return;
            }
            
            string systemId = currentSystem.Id;
            string displayName = currentSystem.DisplayName?.Value ?? systemId;
            
            var message = new SystemCheckMessage(systemId, displayName);
            _networkManager?.SendMessageToAll(message);
            ModLogger.Log("Network", $"Sent system check: {systemId} ({displayName})");
        }
        
        /// <summary>
        /// Send time sync message to all clients (host only)
        /// </summary>
        private void SendTimeSync()
        {
            if (!IsHost) return;
            
            double serverTime = KSA.Universe.GetElapsedSimTime().Seconds();
            
            var message = new TimeSyncMessage
            {
                SimulationTimeSeconds = serverTime,
                SimulationSpeed = 1.0,
                ServerTimestampTicks = DateTime.UtcNow.Ticks,
                IsTimeWarpActive = false,
                SequenceNumber = 0
            };
            
            _networkManager?.SendMessageToAll(message);
            ModLogger.Log("Network", $"Sent time sync: T={serverTime:F3}s");
        }
        
        /// <summary>
        /// Handle time sync message from host (client only)
        /// </summary>
        private void OnTimeSyncReceived(TimeSyncMessage message)
        {
            // Host doesn't need to sync - they sent the message
            if (IsHost) return;
            
            double serverTime = message.SimulationTimeSeconds;
            double localTime = KSA.Universe.GetElapsedSimTime().Seconds();
            double timeDiff = serverTime - localTime;
            
            ModLogger.Log("Network", $"Time sync received: Server={serverTime:F3}s, Local={localTime:F3}s, Diff={timeDiff:F3}s");
            
            // Sync if difference is significant (> 0.5 seconds)
            if (Math.Abs(timeDiff) > 0.5)
            {
                ModLogger.Log("Network", $"Syncing time to server: {serverTime:F3}s");
                _subspaceManager?.ForceTimeSync(serverTime);
            }
            else
            {
                ModLogger.Log("Network", "Time already in sync, no adjustment needed");
            }
        }
        
        /// <summary>
        /// Handle system check message from host (client only)
        /// </summary>
        private void OnSystemCheckReceived(SystemCheckMessage message)
        {
            // Host doesn't need to check - they sent the message
            if (IsHost) return;
            
            var localSystem = SystemLibrary.Default;
            if (localSystem == null)
            {
                ModLogger.Log("Network", "WARNING: Local SystemLibrary.Default is null");
                return;
            }
            
            string localSystemId = localSystem.Id;
            string hostSystemId = message.HostSystemId;
            
            ModLogger.Log("Network", $"System check: Host={hostSystemId}, Local={localSystemId}");
            
            if (localSystemId != hostSystemId)
            {
                // System mismatch - set flag to disconnect on next Update (not during callback!)
                string hostDisplayName = message.HostSystemDisplayName;
                _systemMismatchError = $"Host is running \"{hostDisplayName}\" system.\n\nRestart the game and select \"{hostDisplayName}\" to connect.";
                _pendingDisconnectDueToMismatch = true;
                ModLogger.Log("Network", $"SYSTEM MISMATCH: Host={hostSystemId}, Local={localSystemId} - Will disconnect");
            }
            else
            {
                ModLogger.Log("Network", "System check passed - systems match");
            }
        }
        
        /// <summary>
        /// Clear the system mismatch error (called when user acknowledges it)
        /// </summary>
        public void ClearSystemMismatchError()
        {
            _systemMismatchError = null;
        }
    }
}
