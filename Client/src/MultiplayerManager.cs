using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Brutal.Logging;
using KSA.Networking;
using KSA.Networking.Messages;
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
        private string? _connectionError;
        private bool _pendingDisconnectDueToMismatch;
        
        // Server heartbeat timeout (seconds)
        private const double HEARTBEAT_TIMEOUT_SECONDS = 10.0;
        
        public bool IsHost { get; private set; }
        public bool IsConnected => _networkManager?.IsConnected ?? false;
        public string? LocalPlayerName => _localPlayerName;
        public string? SystemMismatchError => _systemMismatchError;
        public string? ConnectionError => _connectionError;
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
            
            // Subscribe to server heartbeat for authoritative time sync
            NetworkPatches.OnServerHeartbeatReceived += OnServerHeartbeatReceived;
            
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
                // Check for server heartbeat timeout
                if (NetworkPatches.HasReceivedHeartbeat)
                {
                    double secondsSinceHeartbeat = (DateTime.UtcNow - NetworkPatches.LastHeartbeatReceived).TotalSeconds;
                    if (secondsSinceHeartbeat > HEARTBEAT_TIMEOUT_SECONDS)
                    {
                        ModLogger.Log("Network", $"Server heartbeat timeout ({secondsSinceHeartbeat:F1}s) - disconnecting");
                        Disconnect();
                        return;
                    }
                }
                
                _syncManager?.Update(deltaTime);
                _chatManager?.Update(deltaTime);
                _vehicleRenderer?.Update(deltaTime);
                // SubspaceManager doesn't need Update - player times are updated on message receive
                
                // Periodic heartbeat logging
                double currentTime = KSA.Universe.GetElapsedSimTime().Seconds();
                ModLogger.LogHeartbeat(currentTime);
            }
        }
        
        public async Task<bool> JoinSession(string playerName, string serverAddress, ushort port, string password = "")
        {
            ModLogger.Log("Network", $"Joining session: {playerName} connecting to {serverAddress}:{port}");
            ModLogger.PlayerName = playerName;
            
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            NetworkPatches.ResetHeartbeat();
            NetworkPatches.ClearServerMessage();
            _connectionError = null; // Clear any previous connection error
            var result = await (_networkManager?.JoinGame(serverAddress, port, playerName) ?? Task.FromResult(NetworkSession.StartNetworkResult.FailedToConnect));
            
            if (result == NetworkSession.StartNetworkResult.Success)
            {
                IsHost = false;
                _localPlayerName = playerName;
                _syncManager?.SetLocalPlayerName(playerName);
                _subspaceManager?.SetLocalPlayerName(playerName);
                
                // Send password if provided
                if (!string.IsNullOrEmpty(password))
                {
                    var passwordMsg = new KSA.Mods.Multiplayer.Messages.PasswordAuthMessage(password);
                    Dispatch.ToAuthority(passwordMsg);
                    ModLogger.Log("Network", "Sent password authentication");
                }
                
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
            NetworkPatches.ResetHeartbeat();
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
            
            // Capture any server message as connection error (e.g., "Wrong password!")
            var serverMsg = NetworkPatches.ConsumeServerMessage();
            if (!string.IsNullOrEmpty(serverMsg))
            {
                _connectionError = serverMsg;
                ModLogger.Log("Network", $"Connection error captured: {serverMsg}");
            }
            
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
        /// Handle server heartbeat with authoritative time.
        /// Syncs client time to server time on significant drift.
        /// </summary>
        private void OnServerHeartbeatReceived(KSA.Mods.Multiplayer.Messages.ServerHeartbeatMessage message)
        {
            double serverTime = message.ServerTimeSeconds;
            
            // Skip if server time is 0 (not initialized yet)
            if (serverTime <= 0)
                return;
            
            double localTime = KSA.Universe.GetElapsedSimTime().Seconds();
            double timeDiff = serverTime - localTime;
            
            // Only sync FORWARD if server is ahead (positive drift > 1 second)
            // Never sync backward - player may have time warped ahead intentionally
            // Subspace/ghost mode handles players at different times
            if (timeDiff > 1.0)
            {
                ModLogger.Log("Subspace", $"HEARTBEAT TIME SYNC: Server={serverTime:F1}s, Local={localTime:F1}s, Drift={timeDiff:F1}s");
                _subspaceManager?.ForceTimeSync(serverTime);
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
        
        /// <summary>
        /// Clear the connection error (called when user acknowledges it)
        /// </summary>
        public void ClearConnectionError()
        {
            _connectionError = null;
        }
    }
}
