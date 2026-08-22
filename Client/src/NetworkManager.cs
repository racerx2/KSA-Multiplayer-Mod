using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Brutal.Logging;
using KSA.Mods.Multiplayer.Messages;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer
{
    public class NetworkManager : IDisposable
    {
        public event Action<string>? OnPlayerConnected;
        public event Action<string>? OnPlayerDisconnected;
        public event Action<string>? OnConnectionFailed;
        public event Action? OnJoinedGame;
        public event Action? OnDisconnected;
        
        private Dictionary<ClientId, string> _trackedPlayers;
        private HashSet<string> _serverRoster = new(StringComparer.OrdinalIgnoreCase);
        private bool _hasServerRoster;
        private bool _isDisposed;
        
        public bool IsOnline => Network.IsOnline;
        public bool IsConnected => Network.IsOnline;
        public bool IsHost => Network.ActivePeer is NetworkServer;
        public bool IsClient => Network.ActivePeer is NetworkClient;
        
        public NetworkManager()
        {
            _trackedPlayers = new Dictionary<ClientId, string>();
            NetworkPatches.OnPlayerRosterReceived += OnPlayerRosterReceived;
        }
        
        /// <summary>Cancels the join wait, which otherwise polls until it times out.</summary>
        private CancellationTokenSource? _joinCancellation;

        /// <summary>
        /// Stops the join wait started by JoinGame. KSA's wait loop only ends when the
        /// status reaches InGame, so a refused join would otherwise poll for the whole
        /// connect timeout. Call this only after the session has been shut down.
        /// </summary>
        public void CancelJoinWait()
        {
            CancellationTokenSource? source = _joinCancellation;
            _joinCancellation = null;
            if (source == null) return;

            try
            {
                source.Cancel();
                source.Dispose();
            }
            catch (Exception ex)
            {
                ModLogger.Log("Network", $"Could not cancel the join wait: {ex.Message}");
            }
        }

        public async Task<NetworkSession.StartNetworkResult> JoinGame(string serverAddress, int port, string playerName, CancellationToken cancellationToken = default)
        {
            var connectOptions = new ConnectOptions(serverAddress, (ushort)port);
            var playerInfo = new PlayerInfo(playerName);

            CancelJoinWait();
            var joinCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _joinCancellation = joinCancellation;

            NetworkSession.StartNetworkResult result;
            try
            {
                result = await Network.JoinGame(connectOptions, playerInfo, joinCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                result = NetworkSession.StartNetworkResult.Cancelled;
            }
            catch (Exception ex)
            {
                // KSA's join wait shuts the peer down from its own cancellation handler.
                // Network.PeerShuttingDown unsubscribes itself on the first shutdown, so
                // that second call dereferences a null event and throws. The join has
                // ended either way, and the session was already taken down on the game
                // thread, so the fault is recorded rather than propagated.
                ModLogger.Log("Network", $"Join wait ended with {ex.GetType().Name}: {ex.Message}");
                result = NetworkSession.StartNetworkResult.Cancelled;
            }
            finally
            {
                if (ReferenceEquals(_joinCancellation, joinCancellation))
                {
                    _joinCancellation = null;
                    joinCancellation.Dispose();
                }
            }
            
            if (result == NetworkSession.StartNetworkResult.Success)
            {
                _hasServerRoster = false;
                _serverRoster.Clear();
                InitializePlayerTracking();
                OnJoinedGame?.Invoke();
            }
            else
                OnConnectionFailed?.Invoke($"Failed to join: {result}");
            
            return result;
        }
        
        public void Update()
        {
            if (!IsOnline)
            {
                if (_trackedPlayers.Count > 0)
                {
                    _trackedPlayers.Clear();
                    OnDisconnected?.Invoke();
                }
                return;
            }
            
            Network.Tick();
            if (!_hasServerRoster)
                CheckPlayerChanges();
        }
        
        public void Disconnect()
        {
            if (IsOnline)
                Network.Shutdown();
            _trackedPlayers.Clear();
            _serverRoster.Clear();
            _hasServerRoster = false;
            HostPlayerName = string.Empty;
            OnDisconnected?.Invoke();
        }

        /// <summary>Player the server reports as host, or empty if none.</summary>
        public string HostPlayerName { get; private set; } = string.Empty;

        private void OnPlayerRosterReceived(PlayerRosterMessage message)
        {
            HostPlayerName = message.HostName ?? string.Empty;
            var current = new HashSet<string>(
                message.PlayerNames ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (string player in current)
                if (!_serverRoster.Contains(player))
                    OnPlayerConnected?.Invoke(player);
            foreach (string player in _serverRoster)
                if (!current.Contains(player))
                    OnPlayerDisconnected?.Invoke(player);
            _serverRoster = current;
            _hasServerRoster = true;
        }
        
        private void InitializePlayerTracking()
        {
            _trackedPlayers.Clear();
            if (Players.HasPlayers)
                foreach (var player in Players.All)
                    if (player.Value != null)
                        _trackedPlayers[player.Key] = player.Value.Name;
        }
        
        private void CheckPlayerChanges()
        {
            if (!Players.HasPlayers && _trackedPlayers.Count == 0) return;
            
            var currentPlayers = new Dictionary<ClientId, string>();
            if (Players.HasPlayers)
                foreach (var player in Players.All)
                    if (player.Value != null)
                        currentPlayers[player.Key] = player.Value.Name;
            
            foreach (var current in currentPlayers)
                if (!_trackedPlayers.ContainsKey(current.Key))
                    OnPlayerConnected?.Invoke(current.Value);
            
            foreach (var tracked in _trackedPlayers)
                if (!currentPlayers.ContainsKey(tracked.Key))
                    OnPlayerDisconnected?.Invoke(tracked.Value);
            
            _trackedPlayers = currentPlayers;
        }
        
        public List<string> GetPlayerNames()
        {
            if (_hasServerRoster)
                return _serverRoster.OrderBy(name => name).ToList();
            if (!Players.HasPlayers || Players.All == null)
                return new List<string>();
            return Players.All
                .Where(p => p.Value != null)
                .Select(p => p.Value.Name)
                .ToList();
        }
        
        public void SendMessageToAll(GameMessage message)
        {
            if (!IsOnline || Network.ActivePeer == null) return;
            if (IsClient && Authority.GameAuthorityId.Value == 0) return;
            
            if (IsHost)
                Network.ActivePeer.DispatchToAllPlayers(message);
            else
                Dispatch.ToAuthority(message);
        }

        /// <summary>Sends a message to the server alone, never to the other players.</summary>
        public void SendToAuthority(GameMessage message)
        {
            if (!IsOnline || Network.ActivePeer == null) return;
            if (Authority.GameAuthorityId.Value == 0) return;

            Dispatch.ToAuthority(message);
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            NetworkPatches.OnPlayerRosterReceived -= OnPlayerRosterReceived;
            Disconnect();
            _isDisposed = true;
        }
    }
}
