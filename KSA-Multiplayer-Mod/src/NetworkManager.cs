using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Brutal.Logging;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Mods.Multiplayer
{
    public class NetworkManager : IDisposable
    {
        public event Action<string>? OnPlayerConnected;
        public event Action<string>? OnPlayerDisconnected;
        public event Action<string>? OnConnectionFailed;
        public event Action? OnHostStarted;
        public event Action? OnJoinedGame;
        public event Action? OnDisconnected;
        
        private Dictionary<ClientId, string> _trackedPlayers;
        private bool _isDisposed;
        
        public bool IsOnline => Network.IsOnline;
        public bool IsConnected => Network.IsOnline;
        public bool IsHost => Network.ActivePeer is NetworkServer;
        public bool IsClient => Network.ActivePeer is NetworkClient;
        
        public NetworkManager()
        {
            _trackedPlayers = new Dictionary<ClientId, string>();
        }
        
        public NetworkSession.StartNetworkResult StartHost(int port, int maxPlayers, string playerName)
        {
            var serveOptions = new ServeOptions(null, (ushort)port, (ushort)maxPlayers);
            var playerInfo = new PlayerInfo(playerName);
            var result = Network.StartHost(serveOptions, playerInfo);
            
            if (result == NetworkSession.StartNetworkResult.Success)
            {
                InitializePlayerTracking();
                OnHostStarted?.Invoke();
            }
            else
                OnConnectionFailed?.Invoke($"Failed to start host: {result}");
            
            return result;
        }
        
        public async Task<NetworkSession.StartNetworkResult> JoinGame(string serverAddress, int port, string playerName, CancellationToken cancellationToken = default)
        {
            var connectOptions = new ConnectOptions(serverAddress, (ushort)port);
            var playerInfo = new PlayerInfo(playerName);
            var result = await Network.JoinGame(connectOptions, playerInfo, cancellationToken);
            
            if (result == NetworkSession.StartNetworkResult.Success)
            {
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
            CheckPlayerChanges();
        }
        
        public void Disconnect()
        {
            if (!IsOnline) return;
            Network.Shutdown();
            _trackedPlayers.Clear();
            OnDisconnected?.Invoke();
        }
        
        private void InitializePlayerTracking()
        {
            _trackedPlayers.Clear();
            if (Players.HasPlayers)
                foreach (var player in Players.All)
                    _trackedPlayers[player.Key] = player.Value.Name;
        }
        
        private void CheckPlayerChanges()
        {
            if (!Players.HasPlayers && _trackedPlayers.Count == 0) return;
            
            var currentPlayers = new Dictionary<ClientId, string>();
            if (Players.HasPlayers)
                foreach (var player in Players.All)
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
            if (!Players.HasPlayers || Players.All == null)
                return new List<string>();
            return Players.All.Select(p => p.Value.Name).ToList();
        }
        
        public int GetPlayerCount() => Players.Count;
        
        public void SendMessageToAll(GameMessage message)
        {
            if (!IsOnline || Network.ActivePeer == null) return;
            if (IsClient && Authority.GameAuthorityId.Value == 0) return;
            
            if (IsHost)
                Network.ActivePeer.DispatchToAllPlayers(message);
            else
                Dispatch.ToAuthority(message);
        }
        
        public void Dispose()
        {
            if (_isDisposed) return;
            Disconnect();
            _isDisposed = true;
        }
    }
}
