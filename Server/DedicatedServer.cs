using System;
using System.Collections.Generic;
using System.Linq;
using Brutal;
using Brutal.RakNetApi;
using KSA.Networking;
using KSA.Networking.Messages;

namespace KSA.Multiplayer.DedicatedServer
{
    public class DedicatedServer : IDisposable
    {
        private RakPeerInstance _instance;
        private bool _instanceCreated;
        private readonly List<ConnectedPeer> _connections = new();
        private readonly Dictionary<ClientId, PlayerInfo> _players = new();
        private readonly Dictionary<ClientId, string> _playerIPs = new();
        private bool _running;
        private readonly ServerConfig _config;
        private ClientId _serverClientId;
        
        // Custom message IDs (must match client)
        private const byte MSG_ID_KSA_CHAT_REQUEST = 136;
        private const byte MSG_ID_KSA_CHAT_DISPLAY = 137;
        private const byte MSG_ID_MULTIPLAYER_CHAT = 140;
        private const byte MSG_ID_VEHICLE_STATE = 200;
        private const byte MSG_ID_TIME_SYNC = 201;
        private const byte MSG_ID_VEHICLE_DESIGN = 202;
        private const byte MSG_ID_ORBIT_SYNC = 203;
        private const byte MSG_ID_SYSTEM_CHECK = 204;
        private const byte MSG_ID_SERVER_HEARTBEAT = 205;
        private const byte MSG_ID_PASSWORD_AUTH = 206;
        
        private const int HEARTBEAT_INTERVAL_MS = 3000;
        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        
        // Authoritative server time tracking
        private double _serverTimeSeconds = 0;
        private DateTime _serverTimeStarted = DateTime.MinValue;
        private bool _serverTimeInitialized = false;
        
        // Password authentication tracking
        private readonly Dictionary<ClientId, DateTime> _pendingAuth = new();
        private const double AUTH_TIMEOUT_SECONDS = 5.0;
        
        // Console input handling
        private Thread? _consoleThread;

        public DedicatedServer(ServerConfig config)
        {
            _config = config;
        }

        public bool Start()
        {
            ServerConsole.Info($"Server: {_config.ServerName}");
            ServerConsole.Info($"Port: {_config.Port}, Max Players: {_config.MaxPlayers}");
            ServerConsole.Info($"System: {_config.SystemId} ({_config.SystemDisplayName})");
            if (!string.IsNullOrEmpty(_config.Password))
                ServerConsole.Warning("Password protection: ENABLED");
            
            ServerLogger.Log($"Server: {_config.ServerName}");
            ServerLogger.Log($"Starting on port {_config.Port}, max players: {_config.MaxPlayers}");
            
            _instance = RakNetLibrary.CreateInstance();
            _instanceCreated = true;
            
            var socketDescriptor = new SocketDescriptor(null, (ushort)_config.Port);
            var result = _instance.Startup((ushort)_config.MaxPlayers, 
                new Span<SocketDescriptor>(ref socketDescriptor), -99999);
            
            if (result != StartupResult.RaknetStarted)
            {
                ServerConsole.Error($"Failed to start RakNet: {result}");
                return false;
            }
            
            _instance.SetMaximumIncomingConnections((ushort)_config.MaxPlayers);
            _serverClientId = ClientId.FromGuid(_instance.GetMyGUID());
            _running = true;
            
            ServerConsole.Success("Server started successfully!");
            ServerConsole.Info("Type 'help' for available commands.");
            Console.WriteLine();
            
            return true;
        }

        public unsafe void Run()
        {
            Console.CancelKeyPress += OnCancelKeyPress;
            _lastHeartbeatTime = DateTime.UtcNow;
            
            // Start console input thread
            _consoleThread = new Thread(ConsoleInputLoop) { IsBackground = true };
            _consoleThread.Start();
            
            while (_running)
            {
                Packet* packet = _instance.Receive();
                
                while (packet != null)
                {
                    ProcessPacket(packet);
                    _instance.DeallocatePacket(packet);
                    packet = _instance.Receive();
                }
                
                if ((DateTime.UtcNow - _lastHeartbeatTime).TotalMilliseconds >= HEARTBEAT_INTERVAL_MS)
                {
                    SendHeartbeat();
                    _lastHeartbeatTime = DateTime.UtcNow;
                }
                
                // Check for auth timeouts
                CheckAuthTimeouts();
                
                Thread.Sleep(10);
            }
            
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
        
        private void ConsoleInputLoop()
        {
            while (_running)
            {
                try
                {
                    var input = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    ProcessCommand(input.Trim());
                }
                catch { }
            }
        }
        
        private void ProcessCommand(string input)
        {
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            
            var cmd = parts[0].ToLower();
            var arg = parts.Length > 1 ? parts[1] : "";
            
            switch (cmd)
            {
                case "help":
                    ServerConsole.PrintHelp();
                    break;
                    
                case "status":
                    ServerConsole.PrintStatus(_players.Count, _config.MaxPlayers, 
                        _players.Values.Select(p => p.Name).ToList());
                    break;
                    
                case "list":
                    ListPlayers();
                    break;
                    
                case "say":
                    if (!string.IsNullOrEmpty(arg))
                        BroadcastChat($"[Server] {arg}");
                    else
                        ServerConsole.Warning("Usage: say <message>");
                    break;
                    
                case "kick":
                    if (!string.IsNullOrEmpty(arg))
                        KickPlayer(arg);
                    else
                        ServerConsole.Warning("Usage: kick <player name>");
                    break;
                    
                case "ban":
                    if (!string.IsNullOrEmpty(arg))
                        BanPlayer(arg);
                    else
                        ServerConsole.Warning("Usage: ban <player name>");
                    break;
                    
                case "unban":
                    if (!string.IsNullOrEmpty(arg))
                    {
                        _config.UnbanIP(arg);
                        ServerConsole.Admin($"Unbanned IP: {arg}");
                    }
                    else
                        ServerConsole.Warning("Usage: unban <ip>");
                    break;
                    
                case "banlist":
                    ShowBanList();
                    break;
                    
                case "stop":
                case "quit":
                case "exit":
                    GracefulShutdown();
                    break;
                    
                default:
                    ServerConsole.Warning($"Unknown command: {cmd}. Type 'help' for commands.");
                    break;
            }
        }
        
        private void ListPlayers()
        {
            if (_players.Count == 0)
            {
                ServerConsole.Info("No players connected.");
                return;
            }
            
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"Connected Players ({_players.Count}/{_config.MaxPlayers}):");
            
            int i = 1;
            foreach (var kvp in _players)
            {
                var ip = _playerIPs.TryGetValue(kvp.Key, out var playerIp) ? playerIp : "unknown";
                Console.WriteLine($"  {i}. {kvp.Value.Name} ({ip})");
                i++;
            }
            Console.ResetColor();
            Console.WriteLine();
        }
        
        private void ShowBanList()
        {
            if (_config.BannedIPs.Count == 0)
            {
                ServerConsole.Info("No banned IPs.");
                return;
            }
            
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Banned IPs:");
            foreach (var ip in _config.BannedIPs)
                Console.WriteLine($"  {ip}");
            Console.ResetColor();
            Console.WriteLine();
        }

        private void KickPlayer(string name)
        {
            var player = _players.FirstOrDefault(p => 
                p.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            
            if (player.Value == null)
            {
                ServerConsole.Warning($"Player '{name}' not found.");
                return;
            }
            
            var peer = _connections.FirstOrDefault(p => p.ClientId == player.Key);
            if (peer != null)
            {
                _instance.CloseConnection(peer.Address, true);
                ServerConsole.Admin($"Kicked player: {player.Value.Name}");
            }
        }
        
        private void BanPlayer(string name)
        {
            var player = _players.FirstOrDefault(p => 
                p.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            
            if (player.Value == null)
            {
                ServerConsole.Warning($"Player '{name}' not found.");
                return;
            }
            
            if (_playerIPs.TryGetValue(player.Key, out var ip))
            {
                _config.BanIP(ip);
                ServerConsole.Admin($"Banned player: {player.Value.Name} (IP: {ip})");
                
                // Also kick them
                var peer = _connections.FirstOrDefault(p => p.ClientId == player.Key);
                if (peer != null)
                    _instance.CloseConnection(peer.Address, true);
            }
            else
            {
                ServerConsole.Warning($"Could not get IP for player '{name}'.");
            }
        }
        
        private void BroadcastChat(string message)
        {
            var chatMsg = new DisplayChatMessage(message);
            BroadcastToAll(chatMsg.Serialise());
            ServerConsole.Chat("Server", message.Replace("[Server] ", ""));
        }
        
        private void SendMotd(ClientId targetId)
        {
            if (string.IsNullOrEmpty(_config.Motd)) return;
            
            var motdMsg = new DisplayChatMessage($"[Server] {_config.Motd}");
            SendTo(targetId, motdMsg.Serialise());
        }
        
        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            GracefulShutdown();
        }
        
        public void GracefulShutdown()
        {
            if (!_running) return;
            
            ServerConsole.Warning("Shutting down server...");
            BroadcastChat("Server is shutting down!");
            
            Thread.Sleep(500);
            
            foreach (var peer in _connections.ToList())
                _instance.CloseConnection(peer.Address, true);
            
            Thread.Sleep(100);
            _running = false;
        }
        
        private void SendHeartbeat()
        {
            if (_connections.Count == 0) return;
            
            // Get current authoritative server time
            double serverTime = GetServerTime();
            
            // Serialize the heartbeat message with time
            var heartbeatMsg = new ServerHeartbeatMessage(serverTime);
            BroadcastToAll(heartbeatMsg.Serialise());
        }
        
        /// <summary>
        /// Get current authoritative server time.
        /// Time starts when first player joins and advances with wall-clock time.
        /// </summary>
        private double GetServerTime()
        {
            if (!_serverTimeInitialized)
                return 0;
            
            return _serverTimeSeconds + (DateTime.UtcNow - _serverTimeStarted).TotalSeconds;
        }
        
        /// <summary>
        /// Initialize server time from the first player's time.
        /// Called when receiving first VehicleStateMessage or TimeSyncMessage.
        /// </summary>
        private void InitializeServerTime(double clientTime)
        {
            if (_serverTimeInitialized)
                return;
            
            _serverTimeSeconds = clientTime;
            _serverTimeStarted = DateTime.UtcNow;
            _serverTimeInitialized = true;
            ServerConsole.Info($"Server time initialized to {clientTime:F1}s from first player");
            ServerLogger.Log($"Server time initialized: {clientTime:F1}s");
        }

        private unsafe void ProcessPacket(Packet* packet)
        {
            byte messageId = *packet->Data;
            
            switch ((DefaultMessageIDTypes)messageId)
            {
                case DefaultMessageIDTypes.NewIncomingConnection:
                    OnPeerConnected(packet);
                    return;
                    
                case DefaultMessageIDTypes.DisconnectionNotification:
                case DefaultMessageIDTypes.ConnectionLost:
                    OnPeerDisconnected(packet);
                    return;
            }
            
            if (messageId < 134) return;
            
            var senderId = ClientId.FromGuid(packet->Guid);
            int payloadLength = (int)packet->Length - 1;
            ReadOnlySpan<byte> payload = new ReadOnlySpan<byte>(packet->Data + 1, payloadLength);
            
            switch (messageId)
            {
                case (byte)GameMessageId.FirstGameMessageId:
                    HandleJoinRequest(senderId, payload, packet);
                    break;
                    
                case MSG_ID_PASSWORD_AUTH:
                    HandlePasswordAuth(senderId, payload);
                    break;
                    
                case MSG_ID_KSA_CHAT_REQUEST:
                case MSG_ID_KSA_CHAT_DISPLAY:
                case MSG_ID_MULTIPLAYER_CHAT:
                case MSG_ID_VEHICLE_DESIGN:
                case MSG_ID_ORBIT_SYNC:
                case MSG_ID_TIME_SYNC:
                    // Only relay if client is authenticated (or no password required)
                    if (!_pendingAuth.ContainsKey(senderId))
                        RelayToOthers(senderId, packet->Data, (int)packet->Length);
                    break;
                    
                case MSG_ID_VEHICLE_STATE:
                    // Extract time from VehicleStateMessage to initialize server time
                    if (!_serverTimeInitialized)
                    {
                        try
                        {
                            var stateMsg = GameMessage.Deserialise<VehicleStateMessage>(payload);
                            if (stateMsg != null && stateMsg.StateTimeSeconds > 0)
                            {
                                InitializeServerTime(stateMsg.StateTimeSeconds);
                            }
                        }
                        catch { /* Ignore deserialization errors */ }
                    }
                    
                    // Relay to other players
                    if (!_pendingAuth.ContainsKey(senderId))
                        RelayToOthers(senderId, packet->Data, (int)packet->Length);
                    break;
            }
        }

        private unsafe void HandleJoinRequest(ClientId senderId, ReadOnlySpan<byte> payload, Packet* packet)
        {
            try
            {
                var request = GameMessage.Deserialise<JoinGameRequestMessage>(payload);
                var playerName = request.PlayerInfo.Name;
                
                // Get player IP
                var address = packet->SystemAddress;
                string ip = address.ToString().Split(':')[0];
                
                // Check if banned
                if (_config.IsIPBanned(ip))
                {
                    ServerConsole.Warning($"Banned player tried to join: {playerName} ({ip})");
                    var response = new JoinGameResponseMessage(false, "You are banned from this server.", null!);
                    SendTo(senderId, response.Serialise());
                    
                    var peer = _connections.FirstOrDefault(p => p.ClientId == senderId);
                    if (peer != null)
                        _instance.CloseConnection(peer.Address, true);
                    return;
                }
                
                // Mark as pending auth if password is required
                bool passwordRequired = !string.IsNullOrEmpty(_config.Password);
                if (passwordRequired)
                {
                    _pendingAuth[senderId] = DateTime.UtcNow;
                    ServerConsole.Network($"Player {playerName} pending password authentication");
                }
                
                // Add player
                _players[senderId] = request.PlayerInfo;
                _playerIPs[senderId] = ip;
                
                // Send join response
                var acceptResponse = new JoinGameResponseMessage(true, "Welcome!", null!);
                acceptResponse.Players = _players.Select(p => 
                    new KeyValuePair<ClientId, PlayerInfo>(p.Key, p.Value)).ToList();
                
                SendTo(senderId, acceptResponse.Serialise());
                
                // Send system check
                var systemCheck = new SystemCheckMessage(_config.SystemId, _config.SystemDisplayName);
                SendTo(senderId, systemCheck.Serialise());
                
                // Send MOTD
                SendMotd(senderId);
                
                // Send initial time sync if server time is initialized
                if (_serverTimeInitialized)
                {
                    var initialHeartbeat = new ServerHeartbeatMessage(GetServerTime());
                    SendTo(senderId, initialHeartbeat.Serialise());
                    ServerConsole.Network($"Sent initial time sync to {playerName}: {GetServerTime():F1}s");
                }
                
                ServerLogger.Log($"Player joined: {playerName} ({ip})");
                ServerConsole.PlayerJoin(playerName);
                
                // Broadcast updated player list
                BroadcastPlayersUpdate();
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Join request error: {ex.Message}");
            }
        }
        
        private void HandlePasswordAuth(ClientId senderId, ReadOnlySpan<byte> payload)
        {
            // If no password required, ignore
            if (string.IsNullOrEmpty(_config.Password))
            {
                _pendingAuth.Remove(senderId);
                return;
            }
            
            try
            {
                var authMsg = GameMessage.Deserialise<PasswordAuthMessage>(payload);
                string playerName = _players.TryGetValue(senderId, out var info) ? info.Name : "Unknown";
                
                if (authMsg.Password == _config.Password)
                {
                    // Password correct - remove from pending
                    _pendingAuth.Remove(senderId);
                    ServerConsole.Success($"Player {playerName} authenticated successfully");
                    ServerLogger.Log($"Player {playerName} authenticated");
                }
                else
                {
                    // Password wrong - kick
                    ServerConsole.Warning($"Wrong password from {playerName} - kicking");
                    ServerLogger.Log($"Wrong password from {playerName}");
                    
                    var kickMsg = new DisplayChatMessage("[Server] Wrong password!");
                    SendTo(senderId, kickMsg.Serialise());
                    
                    var peer = _connections.FirstOrDefault(p => p.ClientId == senderId);
                    if (peer != null)
                    {
                        Thread.Sleep(100); // Give time for message to send
                        _instance.CloseConnection(peer.Address, true);
                    }
                    
                    // Clean up
                    _pendingAuth.Remove(senderId);
                    _players.Remove(senderId);
                    _playerIPs.Remove(senderId);
                }
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Password auth error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Check for clients that haven't authenticated within the timeout period.
        /// Kicks them if they haven't sent a valid password.
        /// </summary>
        private void CheckAuthTimeouts()
        {
            if (_pendingAuth.Count == 0) return;
            
            var now = DateTime.UtcNow;
            var toKick = new List<ClientId>();
            
            foreach (var kvp in _pendingAuth)
            {
                if ((now - kvp.Value).TotalSeconds > AUTH_TIMEOUT_SECONDS)
                {
                    toKick.Add(kvp.Key);
                }
            }
            
            foreach (var clientId in toKick)
            {
                string playerName = _players.TryGetValue(clientId, out var info) ? info.Name : "Unknown";
                ServerConsole.Warning($"Auth timeout for {playerName} - kicking");
                ServerLogger.Log($"Auth timeout for {playerName}");
                
                var kickMsg = new DisplayChatMessage("[Server] Authentication timeout - wrong or missing password.");
                SendTo(clientId, kickMsg.Serialise());
                
                var peer = _connections.FirstOrDefault(p => p.ClientId == clientId);
                if (peer != null)
                {
                    _instance.CloseConnection(peer.Address, true);
                }
                
                _pendingAuth.Remove(clientId);
                _players.Remove(clientId);
                _playerIPs.Remove(clientId);
            }
        }

        private void BroadcastPlayersUpdate()
        {
            var msg = new PlayersUpdateMessage(_players.Select(p => 
                new KeyValuePair<ClientId, PlayerInfo>(p.Key, p.Value)).ToList());
            BroadcastToAll(msg.Serialise());
        }

        private unsafe void OnPeerConnected(Packet* packet)
        {
            var peer = new ConnectedPeer(packet->Guid);
            _connections.Add(peer);
            ServerConsole.Network($"Peer connected: {peer.ClientId}");
        }

        private unsafe void OnPeerDisconnected(Packet* packet)
        {
            var clientId = ClientId.FromGuid(packet->Guid);
            var peer = _connections.FirstOrDefault(p => p.ClientId == clientId);
            
            if (peer != null)
            {
                _connections.Remove(peer);
                
                if (_players.TryGetValue(clientId, out var playerInfo))
                {
                    ServerConsole.PlayerLeave(playerInfo.Name);
                    ServerLogger.Log($"Player disconnected: {playerInfo.Name}");
                    _players.Remove(clientId);
                    _playerIPs.Remove(clientId);
                    _pendingAuth.Remove(clientId);
                    BroadcastPlayersUpdate();
                }
            }
        }

        private void SendTo(ClientId targetId, ReadOnlySpan<byte> data)
        {
            var peer = _connections.FirstOrDefault(p => p.ClientId == targetId);
            if (peer == null) return;
            
            _instance.Send(data, PacketPriority.HighPriority, 
                PacketReliability.ReliableOrdered, 0, peer.Address, false);
        }

        private void BroadcastToAll(ReadOnlySpan<byte> data)
        {
            _instance.Send(data, PacketPriority.HighPriority, 
                PacketReliability.ReliableOrdered, 0, 
                new AddressOrGUID(RakNetGUID.UNASSIGNED_RAKNET_GUID), true);
        }

        private unsafe void RelayToOthers(ClientId senderId, byte* data, int length)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(data, length);
            
            foreach (var peer in _connections)
            {
                if (peer.ClientId != senderId)
                {
                    _instance.Send(span, PacketPriority.HighPriority, 
                        PacketReliability.ReliableOrdered, 0, peer.Address, false);
                }
            }
        }

        public void Stop()
        {
            _running = false;
            if (_instanceCreated)
            {
                _instance.Shutdown(1000, 0);
                Thread.Sleep(100);
                _instance.Dispose();
                _instanceCreated = false;
            }
            _connections.Clear();
            _players.Clear();
            _playerIPs.Clear();
            ServerConsole.Info("Server stopped.");
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
