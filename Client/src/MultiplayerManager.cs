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
        private SessionUniverseManager? _sessionUniverseManager;
        private CraftShareManager? _craftShareManager;
        private List<string> _connectedPlayers;
        private bool _isInitialized;
        private string? _localPlayerName;
        private string? _systemMismatchError;
        private string? _connectionError;
        /// <summary>A disconnect that must run on the game thread, not from a packet callback.</summary>
        private bool _pendingDisconnect;
        private bool _isWorldReady;
        
        // Server heartbeat timeout (seconds)
        private const double HEARTBEAT_TIMEOUT_SECONDS = 10.0;
        
        public bool IsHost { get; private set; }
        public bool IsConnected => _networkManager?.IsConnected ?? false;
        public string? LocalPlayerName => _localPlayerName;
        public string? SystemMismatchError => _systemMismatchError;

        /// <summary>Set when the host runs a different mod version, else null.</summary>
        public string? VersionMismatchError { get; private set; }

        /// <summary>Clears the version mismatch error.</summary>
        public void ClearVersionMismatchError() => VersionMismatchError = null;

        /// <summary>Set when the host expects a different game type, else null.</summary>
        public string? GameTypeMismatchError { get; private set; }

        /// <summary>Clears the game type mismatch error.</summary>
        public void ClearGameTypeMismatchError() => GameTypeMismatchError = null;
        public string? ConnectionError => _connectionError;
        public bool IsWorldReady => _isWorldReady;
        public NetworkManager? NetworkManager => _networkManager;
        public EventSyncManager? SyncManager => _syncManager;
        public ChatManager? ChatManager => _chatManager;
        public RemoteVehicleRenderer? VehicleRenderer => _vehicleRenderer;

        /// <summary>Gradual clock convergence onto the furthest-ahead player.</summary>
        public TimeSlew? TimeSlew { get; private set; }

        public SubspaceManager? SubspaceManager => _subspaceManager;
        public NameTagRenderer? NameTagRenderer => _nameTagRenderer;

        /// <summary>Craft sharing with the server's library.</summary>
        public CraftShareManager? CraftShareManager => _craftShareManager;
        public IReadOnlyList<string> ConnectedPlayers => _networkManager?.GetPlayerNames() ?? new List<string>();

        /// <summary>Player the server reports as host, or empty.</summary>
        public string HostPlayerName => _networkManager?.HostPlayerName ?? string.Empty;
        
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
            TimeSlew = new TimeSlew(_subspaceManager);
            _nameTagRenderer = new NameTagRenderer(this);
            _sessionUniverseManager = new SessionUniverseManager();
            _craftShareManager = new CraftShareManager(_networkManager);
            
            // Wire up VehiclePatches to use SubspaceManager for visibility checks
            VehiclePatches.SetSubspaceManager(_subspaceManager);
            
            // Wire up EventSyncManager to update player times
            _syncManager.SetSubspaceManager(_subspaceManager);

            // Give SubspaceManager a reference back to EventSyncManager.
            _subspaceManager.SetEventSyncManager(_syncManager);
            
            // Wire up RemoteVehicleRenderer for visual effect visibility
            _vehicleRenderer.SetSubspaceManager(_subspaceManager);
            
            _networkManager.OnPlayerConnected += OnPlayerConnected;
            _networkManager.OnPlayerDisconnected += OnPlayerDisconnected;
            _networkManager.OnConnectionFailed += OnConnectionFailed;
            _networkManager.OnDisconnected += OnDisconnected;
            
            // Subscribe to system check messages for validation
            NetworkPatches.OnSystemCheckReceived += OnSystemCheckReceived;
            
            // Subscribe to the server heartbeat, which reports how far ahead the
            // furthest player is. It is read-only: nothing rewrites local time.
            NetworkPatches.OnServerHeartbeatReceived += OnServerHeartbeatReceived;

            // Part-tree mutations: staging, decoupling, undocking, docking.
            NetworkPatches.OnVesselStructureReceived += msg => VesselStructure.Apply(msg);
            VesselStructure.OnProduced += SendStructure;

            // A passenger cannot split a stack they do not own, so their undock
            // travels to the owner, who performs it and replicates it back.
            NetworkPatches.OnUndockRequestReceived += UndockRequests.HandleIncoming;
            UndockRequests.OnSend += SendUndockRequest;
            VesselStructure.OnSafeWindow += () => _vehicleRenderer?.DrainDeferredWork();
            VesselStructure.OnVesselCreated += OnVesselCreated;
            VesselStructure.OnRemoteVesselProduced += (uid, vessel, owner) =>
                _vehicleRenderer?.AdoptVessel(uid, vessel, owner);
            VesselStructure.OnVesselConsumed += OnVesselConsumed;
            VesselStructure.OnVesselReplaced += localName =>
            {
                if (!VesselIdentity.IsRemoteName(localName)) return;
                string uid = VesselIdentity.UidFromLocalName(localName, _localPlayerName ?? string.Empty);
                _vehicleRenderer?.ForgetRemoteVehicleForRebuild(uid);
            };

            // Structural replication: staging, decoupling, undocking and docking.
            NetworkPatches.OnAuthStatusReceived += OnAuthStatusReceived;

            // A refused join, which KSA cannot shut down safely on its own.
            NetworkPatches.OnJoinRefused += OnJoinRefused;
            
            _isInitialized = true;
        }
        
        public void Update(double deltaTime)
        {
            if (!_isInitialized) return;
            
            _networkManager?.Update();

            // Expire an undock nobody answered.
            UndockRequests.Update();

            // Converge our clock onto the furthest-ahead player, if we are behind.
            TimeSlew?.Update(IsConnected);
            
            // Handle a disconnect deferred out of a packet callback.
            if (_pendingDisconnect)
            {
                _pendingDisconnect = false;
                ModLogger.Log("Network", "Executing deferred disconnect");
                Disconnect();

                // Only once the session is down, so KSA's wait loop cannot shut a
                // live session down a second time from its cancellation path.
                _networkManager?.CancelJoinWait();
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
                _craftShareManager?.Update(deltaTime);

                // Look for a docking candidate among remote vessels.
                DockingAssist.Update(_vehicleRenderer);
                // SubspaceManager needs no Update call.
                
                // Periodic heartbeat logging
                double currentTime = KSA.Universe.GetElapsedTime().Seconds();
                ModLogger.LogHeartbeat(currentTime);
            }
        }
        
        public async Task<bool> JoinSession(string playerName, string serverAddress, ushort port, string password = "")
        {
            // Refuse a name the server would reject, so no connection is opened at all.
            string? nameProblem = ValidatePlayerName(playerName);
            if (nameProblem != null)
            {
                _connectionError = nameProblem;
                ModLogger.LogAlways("Network", $"Join not attempted: {nameProblem}");
                return false;
            }

            playerName = playerName.Trim();

            ModLogger.Log("Network", $"Joining session: {playerName} connecting to {serverAddress}:{port}");
            ModLogger.PlayerName = playerName;
            
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            NetworkPatches.ResetHeartbeat();
            NetworkPatches.ClearServerMessage();
            NetworkPatches.ClearJoinRefusal();
            _isWorldReady = false;
            _syncManager?.SetPublishingEnabled(false);
            _connectionError = null; // Clear any previous connection error
            var result = await (_networkManager?.JoinGame(serverAddress, port, playerName) ?? Task.FromResult(NetworkSession.StartNetworkResult.FailedToConnect));
            
            if (result == NetworkSession.StartNetworkResult.Success)
            {
                IsHost = false;
                if (!_isWorldReady)
                {
                    _localPlayerName = playerName;
                    VesselStructure.LocalPlayerName = playerName;
                    UndockRequests.LocalPlayerName = playerName;
                    _syncManager?.SetLocalPlayerName(playerName);
                    _subspaceManager?.SetLocalPlayerName(playerName);
                }
                
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

        /// <summary>Shows why the server refused the join and tears the session down safely.</summary>
        private void OnJoinRefused(string reason)
        {
            _connectionError = reason;
            _isWorldReady = false;
            _syncManager?.SetPublishingEnabled(false);

            // Disconnecting here would dispose the RakNet instance while KSA is still
            // reading the packet this refusal arrived in, so it waits for Update().
            _pendingDisconnect = true;
            ModLogger.LogAlways("Network", $"JOIN REFUSED: {reason}");
        }

        /// <summary>The client's default placeholder player name.</summary>
        public const string DefaultPlayerName = "Player";

        /// <summary>Longest player name the server accepts.</summary>
        public const int MaxPlayerNameLength = 32;

        /// <summary>
        /// Returns why a name cannot be used, or null if it can. Mirrors the checks the
        /// server makes that a client can make for itself, so an obviously bad name never
        /// reaches a refusal - KSA cannot survive one, since its own handler disposes the
        /// network session from inside the packet loop that is still reading it.
        /// </summary>
        public static string? ValidatePlayerName(string? name)
        {
            string trimmed = (name ?? string.Empty).Trim();

            if (trimmed.Length == 0)
                return "Enter a player name before connecting.";

            if (string.Equals(trimmed, DefaultPlayerName, StringComparison.OrdinalIgnoreCase))
                return $"\"{DefaultPlayerName}\" is the default placeholder name. " +
                       "Choose your own name before connecting - vessel ownership is tracked " +
                       "by name, so two players sharing one would end up flying each other's craft.";

            if (trimmed.Length > MaxPlayerNameLength)
                return $"That name is too long - keep it to {MaxPlayerNameLength} characters or fewer.";

            if (trimmed.Contains('|'))
                return "Player names cannot contain the '|' character.";

            return null;
        }

        private void OnAuthStatusReceived(AuthStatusMessage message)
        {
            if (!message.Success)
            {
                _connectionError = string.IsNullOrWhiteSpace(message.Message)
                    ? "Authentication failed."
                    : message.Message;
                _isWorldReady = false;
                _syncManager?.SetPublishingEnabled(false);
                return;
            }

            _localPlayerName = message.PlayerName;
            VesselStructure.LocalPlayerName = message.PlayerName;
            UndockRequests.LocalPlayerName = message.PlayerName;
            ModLogger.PlayerName = message.PlayerName;
            _syncManager?.SetLocalPlayerName(message.PlayerName);
            _subspaceManager?.SetLocalPlayerName(message.PlayerName);
            _sessionUniverseManager?.Begin();
            _syncManager?.SetPublishingEnabled(true);
            _isWorldReady = true;
            ModLogger.LogAlways("Network",
                $"Authenticated as {message.PlayerName}; world publishing enabled");
        }
        
        public void Disconnect()
        {
            _vehicleRenderer?.Dispose();
            _sessionUniverseManager?.Restore();
            _networkManager?.Disconnect();
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            _connectedPlayers.Clear();
            _craftShareManager?.OnDisconnected();
            _isWorldReady = false;
            IsHost = false;
            _localPlayerName = null;

            // An ask in flight is answered by a session that no longer exists,
            // and a prompt on screen would outlive the stack it refers to.
            UndockRequests.Reset();
            VehiclePatches.ClearRemoteVehicles();
            NetworkPatches.ResetHeartbeat();
        }
        
        public void Shutdown()
        {
            if (!_isInitialized) return;
            
            Disconnect();
            _vehicleRenderer?.Dispose();
            _craftShareManager?.Shutdown();
            _networkManager?.Dispose();
            _networkManager = null;
            _syncManager = null;
            _chatManager = null;
            _vehicleRenderer = null;
            _sessionUniverseManager = null;
            _craftShareManager = null;
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
                
                // Player time is tracked when their state messages arrive.
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
            
            // Stop tracking the player's time; their vessels remain.
            _subspaceManager?.RemovePlayer(playerName);
            ModLogger.Log("Vehicles",
                $"Owner {playerName} disconnected; shared vessels remain in the universe");
        }
        
        private void OnConnectionFailed(string reason)
        {
            ModLogger.Log("Network", $"Connection failed: {reason}");
            _connectedPlayers.Clear();
            _isWorldReady = false;
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
            _sessionUniverseManager?.Restore();
            _syncManager?.Reset();
            _subspaceManager?.Reset();
            _connectedPlayers.Clear();
            _craftShareManager?.OnDisconnected();
            _isWorldReady = false;
            IsHost = false;
            _localPlayerName = null;

            // An ask in flight is answered by a session that no longer exists,
            // and a prompt on screen would outlive the stack it refers to.
            UndockRequests.Reset();
            VehiclePatches.ClearRemoteVehicles();
        }

        /// <summary>Sends an undock request or answer to the server.</summary>
        private void SendUndockRequest(KSA.Mods.Multiplayer.Messages.UndockRequestMessage msg)
        {
            if (!IsConnected) return;
            Dispatch.ToAuthority(msg);
        }

        /// <summary>Sends a local structural change to the server.</summary>
        private void SendStructure(KSA.Mods.Multiplayer.Messages.VesselStructureMessage msg)
        {
            if (string.IsNullOrEmpty(_localPlayerName) || !IsConnected) return;
            msg.PlayerName = _localPlayerName;
            Dispatch.ToAuthority(msg);
            ModLogger.Log("Structure",
                $"  DISPATCHED   : {(msg.Action == 0 ? "SPLIT" : "DOCK")} {msg.PrimaryUid} -> {msg.NewVesselUid} " +
                $"seq={msg.SequenceNumber}");
            ModLogger.Log("Structure", "=== STAGE END (sender) ===");
        }

        /// <summary>Starts publishing a locally created vessel.</summary>
        private void OnVesselCreated(string localVehicleName)
        {
            if (!VesselIdentity.IsRemoteName(localVehicleName))
                _syncManager?.TrackOwnedVehicle(localVehicleName);
        }

        /// <summary>Drops a consumed vessel from the renderer or stops publishing it.</summary>
        private void OnVesselConsumed(string localVehicleName)
        {
            if (VesselIdentity.IsRemoteName(localVehicleName))
            {
                string uid = VesselIdentity.UidFromLocalName(localVehicleName, _localPlayerName ?? string.Empty);
                _vehicleRenderer?.ForgetRemoteVehicle(uid);

                // Remove the sync manager's record so the vessel is not rebuilt.
                _syncManager?.RemoveRemoteVehicle(uid);
            }
            else
            {
                _syncManager?.StopPublishing(localVehicleName);
            }
        }

        private void OnServerHeartbeatReceived(KSA.Mods.Multiplayer.Messages.ServerHeartbeatMessage message)
        {
            double serverTime = message.ServerTimeSeconds;
            
            // Skip if server time is 0 (not initialized yet)
            if (serverTime <= 0)
                return;
            
            double localTime = KSA.Universe.GetElapsedTime().Seconds();
            double timeDiff = serverTime - localTime;
            
            // Log the time difference only; heartbeats never rewrite universe time.
            if (timeDiff > 1.0)
            {
                ModLogger.LogThrottled("Subspace", "AHEAD",
                    $"Another player is {timeDiff:F1}s ahead (server reports {serverTime:F1}s, " +
                    $"we are at {localTime:F1}s)");
            }
            else if (timeDiff < -1.0)
            {
                ModLogger.LogThrottled("Subspace", "LEADING",
                    $"We are the furthest ahead by {-timeDiff:F1}s - nothing to converge to");
            }
        }
        
        /// <summary>Handles a system check message from the host.</summary>
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
            
            // Version first: a mismatch refuses the connection, so the system check is moot.
            CheckModVersion(message.HostModVersion);
            if (VersionMismatchError != null) return;

            CheckGameType(message.HostGameType);
            if (GameTypeMismatchError != null) return;

            string localSystemId = localSystem.Id;
            string hostSystemId = message.HostSystemId;
            
            ModLogger.Log("Network", $"System check: Host={hostSystemId}, Local={localSystemId}");
            
            if (localSystemId != hostSystemId)
            {
                // Flag a disconnect for the next Update.
                string hostDisplayName = message.HostSystemDisplayName;
                _systemMismatchError =
                    $"The server is running the \"{hostDisplayName}\" system.\n\n" +
                    $"Restart KSA and choose \"{hostDisplayName}\" under Select System on the " +
                    "configuration screen, then reconnect.";
                _pendingDisconnect = true;
                ModLogger.Log("Network", $"SYSTEM MISMATCH: Host={hostSystemId}, Local={localSystemId} - Will disconnect");
            }
            else
            {
                ModLogger.Log("Network", "System check passed - systems match");
            }
        }
        
        /// <summary>Refuses the connection when the host's mod version differs from this client's.</summary>
        private void CheckModVersion(string hostVersion)
        {
            VersionMismatchError = null;

            // An older host predates the version field and sends nothing.
            string reported = string.IsNullOrWhiteSpace(hostVersion) ? "an older version" : $"version {hostVersion}";

            if (!string.IsNullOrWhiteSpace(hostVersion) &&
                string.Equals(hostVersion, ModInfo.Version, StringComparison.OrdinalIgnoreCase))
            {
                ModLogger.Log("Network", $"Version check passed - both sides on {ModInfo.Version}");
                return;
            }

            VersionMismatchError =
                $"The server is running {reported}.\n\n" +
                $"Please update your client. You are running version {ModInfo.Version}.";

            _pendingDisconnect = true;
            ModLogger.LogAlways("Network",
                $"VERSION MISMATCH: host={(string.IsNullOrWhiteSpace(hostVersion) ? "(none)" : hostVersion)} " +
                $"local={ModInfo.Version} - refusing the connection");
        }

        /// <summary>Refuses the connection when the host expects a different game type.</summary>
        private void CheckGameType(string hostGameType)
        {
            GameTypeMismatchError = null;

            // An older host predates the field and sends nothing; there is nothing to check.
            if (string.IsNullOrWhiteSpace(hostGameType)) return;

            string localGameType = GameSettings.Current.System.StartGameType.ToString();

            if (string.Equals(localGameType, hostGameType, StringComparison.OrdinalIgnoreCase))
            {
                ModLogger.Log("Network", $"Game type check passed - both sides on {localGameType}");
                return;
            }

            GameTypeMismatchError =
                $"The server is running {hostGameType} mode.\n\n" +
                $"Restart KSA and choose \"{hostGameType}\" under Game Type on the " +
                $"configuration screen, then reconnect. You are running {localGameType} mode.";

            _pendingDisconnect = true;
            ModLogger.LogAlways("Network",
                $"GAME TYPE MISMATCH: host={hostGameType} local={localGameType} - refusing the connection");
        }

        /// <summary>Clears the system mismatch error.</summary>
        public void ClearSystemMismatchError()
        {
            _systemMismatchError = null;
        }
        
        /// <summary>Clears the connection error.</summary>
        public void ClearConnectionError()
        {
            _connectionError = null;
        }
    }
}
