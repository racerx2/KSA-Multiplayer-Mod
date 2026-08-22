using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Brutal;
using Brutal.RakNetApi;
using KSA.Networking;
using KSA.Networking.Messages;
using System.Reflection;

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
        
        // Custom message IDs shared with the client.
        private const byte MSG_ID_KSA_CHAT_REQUEST = 136;
        private const byte MSG_ID_KSA_CHAT_DISPLAY = 137;
        // 140, 201 and 203 were MultiplayerChat, TimeSync and OrbitSync. No
        // build ever sent them: chat travels on KSA's own chat message and time
        // is carried by the heartbeat. The numbers stay retired rather than
        // reused, so a client from before their removal cannot be misread.
        private const byte MSG_ID_VEHICLE_STATE = 200;
        private const byte MSG_ID_VEHICLE_DESIGN = 202;
        private const byte MSG_ID_SYSTEM_CHECK = 204;
        private const byte MSG_ID_SERVER_HEARTBEAT = 205;
        private const byte MSG_ID_PASSWORD_AUTH = 206;
        private const byte MSG_ID_CRAFT_UPLOAD = 207;
        private const byte MSG_ID_AUTH_STATUS = 208;
        private const byte MSG_ID_CRAFT_LIBRARY = 209;
        private const byte MSG_ID_CRAFT_REQUEST = 210;
        private const byte MSG_ID_VEHICLE_REMOVE = 211;
        private const byte MSG_ID_VESSEL_STRUCTURE = 213;
        private const byte MSG_ID_CRAFT_DATA = 214;
        private const byte MSG_ID_UNDOCK_REQUEST = 215;
        
        private const int HEARTBEAT_INTERVAL_MS = 3000;
        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        
        // Server time tracking. The server is a relay, not a clock: it never
        // advances a time of its own, it only repeats the furthest-ahead time
        // its clients have reported so each client can see where the others are.
        /// <summary>Highest simulation time any client has reported.</summary>
        private double _highestReportedTime = 0;
        private readonly object _timeLock = new();
        
        // Password authentication tracking.
        private readonly Dictionary<ClientId, DateTime> _pendingAuth = new();
        private readonly HashSet<ClientId> _authenticatedClients = new();
        private readonly WorldStateStore _worldState = new();
        // The client sends its password immediately after the join is accepted,
        // so this only has to cover the round trip. The old eleven minutes was
        // sized for an external browser login flow that no longer exists.
        private const double AUTH_TIMEOUT_SECONDS = 30.0;

        /// <summary>Craft players have shared through this server.</summary>
        private readonly CraftLibrary _craftLibrary;

        // Console input thread.
        private Thread? _consoleThread;

        public DedicatedServer(ServerConfig config)
        {
            _config = config;
            _craftLibrary = new CraftLibrary(config.MaxSharedCraftPerPlayer);
        }

        public bool Start()
        {
            ServerConsole.Info($"Server: {_config.ServerName}");
            ServerConsole.Info($"Port: {_config.Port}, Max Players: {_config.MaxPlayers}");
            ServerConsole.Info($"System: {_config.SystemId} ({_config.SystemDisplayName})");
            ServerConsole.Info($"Game Type: {_config.GameType}");
            ValidateGameTypeConfig();

            if (_config.CraftSharingEnabled)
            {
                _craftLibrary.Load();
                ServerConsole.Info(
                    $"Craft sharing: ENABLED ({_craftLibrary.Count} shared, " +
                    $"{_config.MaxSharedCraftPerPlayer} per player)");
            }
            else
            {
                ServerConsole.Info("Craft sharing: DISABLED");
            }

            if (!string.IsNullOrEmpty(_config.Password))
                ServerConsole.Warning("Password protection: ENABLED");
            
            ServerLogger.Log($"Server: {_config.ServerName}");
            ServerLogger.Log($"Starting on port {_config.Port}, max players: {_config.MaxPlayers}");
            
            _instance = RakNetLibrary.CreateInstance();
            _instanceCreated = true;
            
            var socketDescriptor = new SocketDescriptor(null!, (ushort)_config.Port);
            var result = _instance.Startup((ushort)_config.MaxPlayers, 
                new Span<SocketDescriptor>(ref socketDescriptor), -99999);
            
            if (result != StartupResult.RaknetStarted)
            {
                // Reports the startup failure, with a port-in-use hint.
                ServerConsole.Error($"Failed to start RakNet: {result}");
                ServerLogger.Log($"FATAL: could not bind UDP {_config.Port} - RakNet reported {result}");

                if (result == StartupResult.SocketPortAlreadyInUse ||
                    result == StartupResult.SocketFailedToBind)
                {
                    string hint = $"UDP port {_config.Port} is already in use - " +
                                  "another server is probably still running. " +
                                  "Stop it, or set a different \"port\" in server_config.json.";
                    ServerConsole.Error(hint);
                    ServerLogger.Log(hint);
                }

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
            
            // Starts the console input thread.
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
                
                // Kicks clients that never authenticated.
                CheckAuthTimeouts();

                // Console commands run here, on this thread, so they cannot touch the
                // connection tables while the packet loop is writing them.
                DrainConsoleCommands();
                
                Thread.Sleep(10);
            }
            
            Console.CancelKeyPress -= OnCancelKeyPress;
            _loopExited.Set();
        }

        /// <summary>Signalled once Run() has left its loop, so Stop() can wait for it.</summary>
        private readonly ManualResetEventSlim _loopExited = new(false);

        /// <summary>Console input, handed to the main loop rather than run on this thread.</summary>
        private readonly ConcurrentQueue<string> _consoleCommands = new();

        private void ConsoleInputLoop()
        {
            while (_running)
            {
                try
                {
                    var input = Console.ReadLine();
                    if (input == null) return;
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    _consoleCommands.Enqueue(input.Trim());
                }
                catch (Exception ex)
                {
                    ServerLogger.Log($"Console input error: {ex.Message}");
                    return;
                }
            }
        }

        /// <summary>Runs any queued console commands on the main loop's thread.</summary>
        private void DrainConsoleCommands()
        {
            while (_consoleCommands.TryDequeue(out string? command))
            {
                try
                {
                    ProcessCommand(command);
                }
                catch (Exception ex)
                {
                    ServerConsole.Error($"Command failed: {ex.Message}");
                    ServerLogger.Log($"Command '{command}' failed: {ex}");
                }
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

                case "craft":
                    ProcessCraftCommand(arg);
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
        
        /// <summary>Runs the "craft list" and "craft remove" console commands.</summary>
        private void ProcessCraftCommand(string arg)
        {
            if (!_config.CraftSharingEnabled)
            {
                ServerConsole.Warning(
                    "Craft sharing is turned off. Set \"craftSharingEnabled\": true in server_config.json.");
                return;
            }

            var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 0 ? parts[0].ToLower() : "list";
            string target = parts.Length > 1 ? parts[1].Trim() : "";

            switch (sub)
            {
                case "list":
                    ListSharedCraft();
                    break;

                case "remove":
                    if (target.Length == 0)
                    {
                        ServerConsole.Warning("Usage: craft remove <craft name>");
                        break;
                    }

                    string? craftId = _craftLibrary.ResolveCraftId(target);
                    if (craftId == null || !_craftLibrary.Remove(craftId))
                    {
                        ServerConsole.Warning($"No shared craft named '{target}'.");
                        break;
                    }

                    ServerConsole.Admin($"Removed shared craft: {target}");
                    ServerLogger.Log($"Shared craft removed by console: {craftId}");
                    BroadcastCraftLibrary();
                    break;

                default:
                    ServerConsole.Warning("Usage: craft list | craft remove <craft name>");
                    break;
            }
        }

        /// <summary>Prints every craft in the shared library.</summary>
        private void ListSharedCraft()
        {
            CraftLibraryEntry[] entries = _craftLibrary.GetCatalogue();
            if (entries.Length == 0)
            {
                ServerConsole.Info("No craft have been shared.");
                return;
            }

            ServerConsole.Info($"Shared craft ({entries.Length}):");
            foreach (CraftLibraryEntry entry in entries)
            {
                string shared = entry.SharedUtcTicks > 0 && entry.SharedUtcTicks <= DateTime.MaxValue.Ticks
                    ? new DateTime(entry.SharedUtcTicks, DateTimeKind.Utc)
                        .ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "unknown date";

                ServerConsole.Info(
                    $"  {entry.CraftName}  by {entry.OwnerPlayerName}  " +
                    $"{entry.SizeBytes / 1024} KB  {shared}");
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
                
                // Disconnects the banned player.
                var peer = _connections.FirstOrDefault(p => p.ClientId == player.Key);
                if (peer != null)
                    _instance.CloseConnection(peer.Address, true);
            }
            else
            {
                ServerConsole.Warning($"Could not get IP for player '{name}'.");
            }
        }
        
        /// <summary>Translates a player chat request into a display message for all clients.</summary>
        private void HandleChatRequest(ClientId senderId, ReadOnlySpan<byte> payload)
        {
            try
            {
                var request = GameMessage.Deserialise<ChatRequestMessage>(payload);
                if (request == null || string.IsNullOrWhiteSpace(request.Message))
                    return;

                // Identifies the sender by the connection the packet arrived on.
                if (!_players.TryGetValue(senderId, out PlayerInfo? player))
                    return;

                string text = request.Message.Trim();
                if (text.Length > MaxChatLength)
                    text = text.Substring(0, MaxChatLength);

                string line = $"[{player.Name}] {text}";
                BroadcastToAll(new DisplayChatMessage(line).Serialise());
                ServerConsole.Chat(player.Name, text);
            }
            catch (Exception ex)
            {
                ServerConsole.Warning($"Malformed chat request from {senderId}: {ex.Message}");
            }
        }

        /// <summary>Longest chat line accepted.</summary>
        private const int MaxChatLength = 512;

        /// <summary>Stores an uploaded craft and tells everyone the library changed.</summary>
        private void HandleCraftUpload(ClientId senderId, ReadOnlySpan<byte> payload)
        {
            // Nothing a client sends may take the server down, so the whole
            // handler is guarded, not only the deserialisation.
            try
            {
                CraftUploadMessage? upload = GameMessage.Deserialise<CraftUploadMessage>(payload);
                if (upload == null)
                    return;

                // Every field arrived over the wire and may be absent.
                upload.OwnerPlayerName ??= string.Empty;
                upload.CraftName ??= string.Empty;
                upload.SystemId ??= string.Empty;
                upload.GameVersion ??= string.Empty;
                upload.MetaToml ??= string.Empty;
                upload.CompressedVehicleXml ??= Array.Empty<byte>();

                if (!_config.CraftSharingEnabled)
                {
                    SendCraftError(senderId, string.Empty, upload.CraftName,
                        "Craft sharing is turned off on this server.");
                    return;
                }

                // Attributes the upload to the connection it arrived on, not to what it claims.
                if (!_players.TryGetValue(senderId, out PlayerInfo? player))
                    return;

                if (!upload.OwnerPlayerName.Equals(player.Name, StringComparison.Ordinal))
                {
                    ServerLogger.Log(
                        $"Craft upload rejected: '{upload.OwnerPlayerName}' does not match sender {player.Name}");
                    SendCraftError(senderId, string.Empty, upload.CraftName,
                        "The upload was attributed to another player.");
                    return;
                }

                if (!AllowCraftUpload(senderId, out double waitSeconds))
                {
                    SendCraftError(senderId, string.Empty, upload.CraftName,
                        $"You are sharing craft too quickly. Wait {waitSeconds:F0} seconds.");
                    return;
                }

                string? refusal = _craftLibrary.Store(upload, out CraftLibraryEntry? entry);
                if (refusal != null || entry == null)
                {
                    ServerLogger.Log($"Craft upload refused from {player.Name}: {refusal}");
                    SendCraftError(senderId, string.Empty, upload.CraftName,
                        refusal ?? "The craft could not be stored.");
                    return;
                }

                ServerConsole.Network(
                    $"{player.Name} shared craft '{entry.CraftName}' ({entry.SizeBytes / 1024} KB)");
                ServerLogger.Log(
                    $"Craft shared: {entry.CraftId} by {player.Name}, {entry.SizeBytes} bytes");

                BroadcastCraftLibrary();
            }
            catch (Exception ex)
            {
                ServerConsole.Warning($"Malformed craft upload from {senderId}: {ex.Message}");
                ServerLogger.Log($"Craft upload error from {senderId}: {ex}");
            }
        }

        /// <summary>Whether this client may upload now, or how long it must wait.</summary>
        private bool AllowCraftUpload(ClientId senderId, out double waitSeconds)
        {
            DateTime now = DateTime.UtcNow;

            if (_lastCraftUpload.TryGetValue(senderId, out DateTime last))
            {
                double since = (now - last).TotalSeconds;
                if (since < CraftUploadCooldownSeconds)
                {
                    waitSeconds = CraftUploadCooldownSeconds - since;
                    return false;
                }
            }

            _lastCraftUpload[senderId] = now;
            waitSeconds = 0;
            return true;
        }

        /// <summary>Shortest gap allowed between one client's craft uploads.</summary>
        private const double CraftUploadCooldownSeconds = 5.0;

        /// <summary>When each client last uploaded a craft.</summary>
        private readonly Dictionary<ClientId, DateTime> _lastCraftUpload = new();

        /// <summary>Answers a catalogue or craft-data request from one client.</summary>
        private void HandleCraftRequest(ClientId senderId, ReadOnlySpan<byte> payload)
        {
            try
            {
                CraftRequestMessage? request = GameMessage.Deserialise<CraftRequestMessage>(payload);
                if (request == null)
                    return;

                request.CraftId ??= string.Empty;
                request.RequesterPlayerName ??= string.Empty;

                if (!_config.CraftSharingEnabled)
                {
                    SendCraftError(senderId, request.CraftId, string.Empty,
                        "Craft sharing is turned off on this server.");
                    return;
                }

                if (request.RequestKind == CraftRequestMessage.REQUEST_CATALOGUE)
                {
                    SendCraftLibrary(senderId);
                    return;
                }

                CraftDataMessage? craft = _craftLibrary.Fetch(request.CraftId);
                if (craft == null)
                {
                    SendCraftError(senderId, request.CraftId, string.Empty,
                        "That craft is no longer on the server.");
                    return;
                }

                SendTo(senderId, craft.Serialise());

                string requesterName = _players.TryGetValue(senderId, out PlayerInfo? requester)
                    ? requester.Name
                    : senderId.ToString();
                ServerConsole.Network($"{requesterName} downloaded craft '{craft.CraftName}'");
                ServerLogger.Log($"Craft sent to {requesterName}: {craft.CraftId}");
            }
            catch (Exception ex)
            {
                ServerConsole.Warning($"Malformed craft request from {senderId}: {ex.Message}");
                ServerLogger.Log($"Craft request error from {senderId}: {ex}");
            }
        }

        /// <summary>Reports a failed craft operation to the client that asked for it.</summary>
        private void SendCraftError(ClientId targetId, string craftId, string craftName, string error)
        {
            var reply = new CraftDataMessage
            {
                CraftId = craftId ?? string.Empty,
                CraftName = craftName ?? string.Empty,
                Error = error
            };
            SendTo(targetId, reply.Serialise());
        }

        /// <summary>Sends the craft catalogue to one client.</summary>
        private void SendCraftLibrary(ClientId targetId)
        {
            if (!_config.CraftSharingEnabled) return;
            SendTo(targetId, new CraftLibraryMessage(_craftLibrary.GetCatalogue()).Serialise());
        }

        /// <summary>Sends the craft catalogue to every client.</summary>
        private void BroadcastCraftLibrary()
        {
            if (!_config.CraftSharingEnabled) return;
            BroadcastToAll(new CraftLibraryMessage(_craftLibrary.GetCatalogue()).Serialise());
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
            
            // Nothing to report until a client has told the server where it is.
            if (!TryGetServerTime(out double serverTime)) return;
            
            // Broadcasts the heartbeat carrying that time.
            var heartbeatMsg = new ServerHeartbeatMessage(serverTime);
            BroadcastToAll(heartbeatMsg.Serialise());
        }
        
        /// <summary>Returns the highest simulation time reported by any client.</summary>
        private double GetServerTime()
        {
            lock (_timeLock)
            {
                return _highestReportedTime;
            }
        }

        /// <summary>Records a client's simulation time if it exceeds the current highest.</summary>
        private void ReportClientTime(double clientTimeSeconds)
        {
            if (double.IsNaN(clientTimeSeconds) || double.IsInfinity(clientTimeSeconds)) return;
            if (clientTimeSeconds <= 0) return;

            lock (_timeLock)
            {
                if (clientTimeSeconds > _highestReportedTime)
                    _highestReportedTime = clientTimeSeconds;
            }
        }
        
        /// <summary>
        /// Returns the highest reported simulation time, or false while no
        /// client has reported one yet.
        /// </summary>
        /// <remarks>
        /// Until the first report the only time available is zero, which is not
        /// a position any client occupies. Sending it would tell every receiver
        /// the furthest-ahead player is at the epoch.
        /// </remarks>
        private bool TryGetServerTime(out double serverTimeSeconds)
        {
            serverTimeSeconds = GetServerTime();
            return serverTimeSeconds > 0;
        }

        /// <summary>Handles one packet. Nothing a client sends may stop the server.</summary>
        private unsafe void ProcessPacket(Packet* packet)
        {
            try
            {
                DispatchPacket(packet);
            }
            catch (Exception ex)
            {
                ServerConsole.Warning($"Dropped a malformed packet: {ex.Message}");
                ServerLogger.Log($"Packet handling error: {ex}");
            }
        }

        private unsafe void DispatchPacket(Packet* packet)
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

                case MSG_ID_VEHICLE_REMOVE:
                    if (IsAuthenticated(senderId))
                        HandleVehicleRemove(senderId, payload, packet->Data, (int)packet->Length);
                    break;
                    
                case MSG_ID_KSA_CHAT_REQUEST:
                    if (IsAuthenticated(senderId))
                        HandleChatRequest(senderId, payload);
                    break;

                case MSG_ID_CRAFT_UPLOAD:
                    if (IsAuthenticated(senderId))
                        HandleCraftUpload(senderId, payload);
                    break;

                case MSG_ID_CRAFT_REQUEST:
                    if (IsAuthenticated(senderId))
                        HandleCraftRequest(senderId, payload);
                    break;

                // Not relayed. A display message is what this server says, and the
                // client treats a "[Server]" prefix as authoritative - including in
                // its disconnect banner. Player chat goes through MSG_ID_KSA_CHAT_REQUEST,
                // which attributes the sender by connection and caps the length.
                case MSG_ID_KSA_CHAT_DISPLAY:
                    ServerLogger.Log(
                        $"Dropped a display-chat message from {senderId}: clients may not send these");
                    break;

                case MSG_ID_VESSEL_STRUCTURE:
                    if (IsAuthenticated(senderId))
                        HandleVesselStructure(senderId, payload, packet->Data, (int)packet->Length);
                    break;

                case MSG_ID_UNDOCK_REQUEST:
                    if (IsAuthenticated(senderId))
                        HandleUndockRequest(senderId, payload, packet->Data, (int)packet->Length);
                    break;

                case MSG_ID_VEHICLE_DESIGN:
                    if (IsAuthenticated(senderId))
                        HandleVehicleDesign(senderId, payload, packet->Data, (int)packet->Length);
                    break;

                case MSG_ID_VEHICLE_STATE:
                    if (!IsAuthenticated(senderId))
                        break;

                    // Inside the auth gate: _highestReportedTime only ever rises and is
                    // never reset, so one unauthenticated packet claiming a huge time
                    // would poison the server clock for the life of the process.
                    try
                    {
                        var stateMsg = GameMessage.Deserialise<VehicleStateMessage>(payload);
                        if (stateMsg != null && stateMsg.StateTimeSeconds > 0)
                        {
                            ReportClientTime(stateMsg.StateTimeSeconds);
                        }
                    }
                    catch { /* Ignores deserialization errors. */ }

                    HandleVehicleState(senderId, payload, packet->Data, (int)packet->Length);
                    break;
            }
        }

        private bool IsAuthenticated(ClientId clientId) =>
            _authenticatedClients.Contains(clientId);

        /// <summary>Validates the sender and relays a part-tree mutation to the other players.</summary>
        private unsafe void HandleVesselStructure(ClientId senderId, ReadOnlySpan<byte> payload,
                                                  byte* packetData, int packetLength)
        {
            try
            {
                var message = GameMessage.Deserialise<VesselStructureMessage>(payload);
                if (message == null)
                {
                    ServerLogger.Log("Structure event rejected: could not deserialise");
                    return;
                }

                if (!ValidateVehicleOwner(senderId, message.PlayerName))
                {
                    ServerLogger.Log($"Structure event rejected: {message.PlayerName} is not the sender");
                    return;
                }

                ServerConsole.Network(
                    $"Relaying {(message.Action == VesselStructureMessage.ACTION_SPLIT ? "split" : "dock")} " +
                    $"from {message.PlayerName}: {message.PrimaryUid}");
                RelayToOthers(senderId, packetData, packetLength);
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Structure event rejected: {ex.Message}");
            }
        }

        /// <summary>
        /// Relays a passenger's undock request to the stack's owner, and the
        /// owner's answer back.
        /// </summary>
        /// <remarks>
        /// The two directions are checked separately. A request must come from
        /// the player it names as the requester, and an answer from the player
        /// it names as the owner. Without both checks any authenticated client
        /// could ask on someone else's behalf, or answer for an owner who never
        /// saw the request — and the answer is what the requester's client acts
        /// on. The server does not know which vessels exist, so it validates who
        /// is speaking and leaves whether the undock is possible to the owner.
        /// </remarks>
        private unsafe void HandleUndockRequest(
            ClientId senderId, ReadOnlySpan<byte> payload, byte* packetData, int packetLength)
        {
            try
            {
                var message = GameMessage.Deserialise<UndockRequestMessage>(payload);
                if (message == null)
                {
                    ServerLogger.Log("Undock request rejected: could not deserialise");
                    return;
                }

                bool isRequest = message.Status == UndockRequestMessage.STATUS_REQUEST;
                string mustBe = isRequest ? message.RequesterPlayerName : message.OwnerPlayerName;

                if (!ValidateVehicleOwner(senderId, mustBe))
                {
                    ServerLogger.Log(
                        $"Undock {(isRequest ? "request" : "answer")} rejected: sender is not {mustBe}");
                    return;
                }

                string what = message.Status switch
                {
                    UndockRequestMessage.STATUS_REQUEST => "asks",
                    UndockRequestMessage.STATUS_ACCEPTED => "accepted",
                    UndockRequestMessage.STATUS_DECLINED => "declined",
                    _ => "sent an unknown undock status to"
                };

                ServerConsole.Network(
                    $"Undock #{message.RequestId}: {message.RequesterPlayerName} {what} " +
                    $"{message.OwnerPlayerName} ({message.StackUid})");
                RelayToOthers(senderId, packetData, packetLength);
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Undock request rejected: {ex.Message}");
            }
        }

        private unsafe void HandleVehicleDesign(
            ClientId senderId, ReadOnlySpan<byte> payload, byte* packetData, int packetLength)
        {
            try
            {
                var message = GameMessage.Deserialise<VehicleDesignSyncMessage>(payload);
                if (message == null || !ValidateVehicleOwner(senderId, message.OwnerPlayerName))
                    return;

                _worldState.SetDesign(
                    message.OwnerPlayerName, message.VehicleId,
                    new ReadOnlySpan<byte>(packetData, packetLength).ToArray());
                ServerLogger.Log(
                    $"World design cached: {message.OwnerPlayerName}/{message.VehicleId} template={message.TemplateId}");
                RelayToOthers(senderId, packetData, packetLength);
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Vehicle design rejected: {ex.Message}");
            }
        }

        private unsafe void HandleVehicleState(
            ClientId senderId, ReadOnlySpan<byte> payload, byte* packetData, int packetLength)
        {
            try
            {
                var message = GameMessage.Deserialise<VehicleStateMessage>(payload);
                if (message == null || !ValidateVehicleOwner(senderId, message.OwnerPlayerName))
                    return;

                _worldState.SetState(
                    message.OwnerPlayerName, message.VehicleId,
                    new ReadOnlySpan<byte>(packetData, packetLength).ToArray());
                RelayToOthers(
                    senderId, packetData, packetLength,
                    PacketReliability.UnreliableSequenced, 1);
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Vehicle state rejected: {ex.Message}");
            }
        }

        private bool ValidateVehicleOwner(ClientId senderId, string claimedOwner)
        {
            if (_players.TryGetValue(senderId, out PlayerInfo? player) &&
                player.Name.Equals(claimedOwner, StringComparison.Ordinal))
            {
                return true;
            }

            ServerLogger.Log(
                $"Rejected vehicle packet with mismatched owner '{claimedOwner}' from {senderId}");
            return false;
        }

        private unsafe void HandleVehicleRemove(
            ClientId senderId, ReadOnlySpan<byte> payload, byte* packetData, int packetLength)
        {
            try
            {
                var message = GameMessage.Deserialise<VehicleRemoveMessage>(payload);
                if (message == null || !ValidateVehicleOwner(senderId, message.OwnerPlayerName))
                    return;

                _worldState.Remove(message.OwnerPlayerName, message.VehicleId);
                RelayToOthers(senderId, packetData, packetLength);
                ServerLogger.Log(
                    $"World vehicle removed: {message.OwnerPlayerName}/{message.VehicleId}");
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Vehicle remove rejected: {ex.Message}");
            }
        }

        private void SendWorldSnapshot(ClientId targetId)
        {
            string targetName = _players.TryGetValue(targetId, out PlayerInfo? target)
                ? target.Name
                : string.Empty;
            IReadOnlyList<byte[]> packets = _worldState.GetSnapshotPackets(
                targetName, out int designs, out int states);
            foreach (byte[] packet in packets)
                SendTo(targetId, packet);

            ServerLogger.Log(
                $"World snapshot sent to {_players.GetValueOrDefault(targetId)?.Name}: " +
                $"{designs} designs, {states} states");
        }

        private unsafe void HandleJoinRequest(ClientId senderId, ReadOnlySpan<byte> payload, Packet* packet)
        {
            try
            {
                var request = GameMessage.Deserialise<JoinGameRequestMessage>(payload)
                    ?? throw new InvalidDataException("Join request was empty.");
                var playerInfo = request.PlayerInfo
                    ?? throw new InvalidDataException("Join request had no player information.");
                var playerName = playerInfo.Name;
                
                // Reads the joining player's IP address.
                var address = packet->SystemAddress;
                string ip = address.ToString().Split(':')[0];
                
                // Refuses banned IP addresses.
                if (_config.IsIPBanned(ip))
                {
                    ServerConsole.Warning($"Banned player tried to join: {playerName} ({ip})");
                    var response = CreateJoinResponse(
                        false, "You are banned from this server.",
                        new List<KeyValuePair<ClientId, PlayerInfo>>());
                    SendTo(senderId, response.Serialise());
                    
                    var peer = _connections.FirstOrDefault(p => p.ClientId == senderId);
                    if (peer != null)
                        _instance.CloseConnection(peer.Address, true);
                    return;
                }
                
                // Rejects empty, placeholder, duplicate, over-long, and separator-containing names.
                string? nameProblem = ValidateJoinName(playerName, senderId);
                if (nameProblem != null)
                {
                    ServerConsole.Warning($"Rejected join from {ip}: {nameProblem}");
                    var refusal = CreateJoinResponse(
                        false, nameProblem, new List<KeyValuePair<ClientId, PlayerInfo>>());
                    SendTo(senderId, refusal.Serialise());

                    var refusedPeer = _connections.FirstOrDefault(p => p.ClientId == senderId);
                    if (refusedPeer != null)
                        _instance.CloseConnection(refusedPeer.Address, true);
                    return;
                }

                // Marks the client pending authentication when a password is set.
                bool passwordRequired = !string.IsNullOrEmpty(_config.Password);
                if (passwordRequired)
                {
                    _pendingAuth[senderId] = DateTime.UtcNow;
                    ServerConsole.Network(
                        $"Player {playerName} pending password authentication");
                }
                
                // Records the player under a freshly constructed PlayerInfo.
                _players[senderId] = new PlayerInfo(playerName);
                _playerIPs[senderId] = ip;
                
                // Sends the join acceptance.
                var acceptResponse = CreateJoinResponse(
                    true, "Welcome!", CreatePlayerList());
                
                ServerConsole.Network($"Sending join response to {playerName}");
                SendTo(senderId, acceptResponse.Serialise());

                // Sends the server's system configuration.
                var systemCheck = new SystemCheckMessage(_config.SystemId, _config.SystemDisplayName,
                    ServerModVersion, _config.GameType);
                SendTo(senderId, systemCheck.Serialise());
                
                // Sends the message of the day.
                SendMotd(senderId);
                
                // Sends an initial heartbeat once server time is known, so the
                // joining player learns where the others are without waiting
                // out the heartbeat interval.
                if (TryGetServerTime(out double joinTimeSync))
                {
                    var initialHeartbeat = new ServerHeartbeatMessage(joinTimeSync);
                    SendTo(senderId, initialHeartbeat.Serialise());
                    ServerConsole.Network($"Sent initial time sync to {playerName}: {joinTimeSync:F1}s");
                }
                
                ServerLogger.Log($"Player joined: {playerName} ({ip})");
                ServerConsole.PlayerJoin(playerName);
                
                if (!passwordRequired)
                    CompleteAuthentication(senderId, playerName);
            }
            catch (Exception ex)
            {
                ServerLogger.Log($"Join request error: {ex.Message}");
            }
        }
        
        private void HandlePasswordAuth(ClientId senderId, ReadOnlySpan<byte> payload)
        {
            // Ignores the message when no password is configured.
            if (string.IsNullOrEmpty(_config.Password))
            {
                _pendingAuth.Remove(senderId);
                return;
            }
            
            try
            {
                var authMsg = GameMessage.Deserialise<PasswordAuthMessage>(payload);
                string playerName = _players.TryGetValue(senderId, out var info) ? info.Name : "Unknown";

                // A null message used to throw here, and the outer catch only logged -
                // no reply, no disconnect - so the client sat waiting for the whole
                // auth timeout. Treat it as a failed attempt instead.
                string authenticatedName = playerName;
                bool authenticated = authMsg != null && authMsg.Password == _config.Password;

                if (authenticated)
                {
                    if (_players.TryGetValue(senderId, out var authenticatedPlayer))
                        authenticatedPlayer.SetName(authenticatedName);

                    CompleteAuthentication(senderId, authenticatedName);
                    ServerConsole.Success(
                        $"Player {authenticatedName} authenticated successfully");
                    ServerLogger.Log($"Player authenticated: {playerName} -> {authenticatedName}");
                }
                else
                {
                    // Disconnects the client on a wrong password.
                    ServerConsole.Warning($"Authentication failed for {playerName} - kicking");
                    ServerLogger.Log($"Authentication failed for {playerName}");
                    
                    SendTo(senderId,
                        new AuthStatusMessage(false, playerName, "Authentication failed.").Serialise());
                    
                    var peer = _connections.FirstOrDefault(p => p.ClientId == senderId);
                    if (peer != null)
                    {
                        Thread.Sleep(100); // Lets the message flush before closing.
                        _instance.CloseConnection(peer.Address, true);
                    }
                    
                    // Drops the client's tracked state.
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

        private void CompleteAuthentication(ClientId senderId, string playerName)
        {
            _pendingAuth.Remove(senderId);
            _authenticatedClients.Add(senderId);
            SendTo(senderId,
                new AuthStatusMessage(true, playerName, "World ready.").Serialise());
            SendWorldSnapshot(senderId);
            SendCraftLibrary(senderId);
            BroadcastPlayersUpdate();
        }

        /// <summary>Kicks clients that have not authenticated within the timeout.</summary>
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
                
                var kickMsg = new DisplayChatMessage("[Server] No password was sent in time.");
                SendTo(clientId, kickMsg.Serialise());
                
                var peer = _connections.FirstOrDefault(p => p.ClientId == clientId);
                if (peer != null)
                {
                    _instance.CloseConnection(peer.Address, true);
                }
                
                _pendingAuth.Remove(clientId);
                _authenticatedClients.Remove(clientId);
                _players.Remove(clientId);
                _playerIPs.Remove(clientId);
            }
        }

        /// <summary>Returns why a name may not join, or null if it may.</summary>
        private string? ValidateJoinName(string? name, ClientId joiner)
        {
            string trimmed = (name ?? string.Empty).Trim();

            if (trimmed.Length == 0)
                return "Set a player name before joining - the server cannot identify you without one.";

            if (string.Equals(trimmed, DefaultPlayerName, StringComparison.OrdinalIgnoreCase))
                return $"\"{DefaultPlayerName}\" is the default placeholder name. " +
                       "Choose your own name before joining - vessel ownership is tracked by name, " +
                       "so two players sharing one would end up flying each other's craft.";

            if (trimmed.Length > MaxPlayerNameLength)
                return $"That name is too long - keep it to {MaxPlayerNameLength} characters or fewer.";

            // Rejects the uid separator character.
            if (trimmed.Contains('|'))
                return "Player names cannot contain the '|' character.";

            foreach (KeyValuePair<ClientId, PlayerInfo> existing in _players)
            {
                // Skips the joiner's own existing entry.
                if (existing.Key.Equals(joiner)) continue;

                if (string.Equals(existing.Value.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    return $"The name \"{trimmed}\" is already in use on this server. Pick another.";
            }

            return null;
        }

        /// <summary>The client's default placeholder player name.</summary>
        private const string DefaultPlayerName = "Player";

        /// <summary>Longest accepted player name.</summary>
        private const int MaxPlayerNameLength = 32;

        /// <summary>Warns at startup if GameType in the config is not a value KSA recognises.</summary>
        private void ValidateGameTypeConfig()
        {
            string t = (_config.GameType ?? string.Empty).Trim();
            if (string.Equals(t, "Sandbox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "Testing", StringComparison.OrdinalIgnoreCase))
                return;

            ServerConsole.Warning(
                $"gameType \"{_config.GameType}\" is not recognised - KSA accepts only \"Sandbox\" or " +
                "\"Testing\". Every client will be refused until this is corrected.");
        }

        /// <summary>This server's mod version, read from the assembly so it cannot drift from the csproj.</summary>
        private static string ServerModVersion
        {
            get
            {
                var v = typeof(DedicatedServer).Assembly.GetName().Version;
                return v == null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        private void BroadcastPlayersUpdate()
        {
            var msg = new PlayerRosterMessage(
                _players.Where(player => IsAuthenticated(player.Key))
                    .Select(player => player.Value.Name)
                    .OrderBy(name => name)
                    .ToArray(),
                _config.HostPlayerName);
            BroadcastToAll(msg.Serialise());
        }

        private List<KeyValuePair<ClientId, PlayerInfo>> CreatePlayerList() =>
            _players.Where(player => IsAuthenticated(player.Key)).Select(player =>
                new KeyValuePair<ClientId, PlayerInfo>(
                    player.Key, new PlayerInfo(player.Value.Name))).ToList();

        private static JoinGameResponseMessage CreateJoinResponse(
            bool accepted,
            string message,
            List<KeyValuePair<ClientId, PlayerInfo>> players)
        {
            // Builds the response without the public constructor's XML serializer.
            var response = (JoinGameResponseMessage)Activator.CreateInstance(
                typeof(JoinGameResponseMessage), nonPublic: true)!;
            response.Accepted = accepted;
            response.Message = message;
            response.IsDedicated = true;
            response.Players = players;
            typeof(JoinGameResponseMessage)
                .GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(response, new byte[] { 0 });
            return response;
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

                    // Their vessels leave with them. Without this every later joiner
                    // builds the craft of players who left sessions ago, and the store
                    // grows for the life of the process.
                    int dropped = _worldState.RemoveOwner(playerInfo.Name);
                    if (dropped > 0)
                        ServerLogger.Log($"Dropped {dropped} world vessels for {playerInfo.Name}");

                    _players.Remove(clientId);
                    _playerIPs.Remove(clientId);
                    _pendingAuth.Remove(clientId);
                    _authenticatedClients.Remove(clientId);
                    _lastCraftUpload.Remove(clientId);
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

        private unsafe void RelayToOthers(
            ClientId senderId, byte* data, int length,
            PacketReliability reliability = PacketReliability.ReliableOrdered,
            byte orderingChannel = 0)
        {
            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(data, length);
            
            foreach (var peer in _connections)
            {
                if (peer.ClientId != senderId)
                {
                    _instance.Send(span, PacketPriority.HighPriority,
                        reliability, orderingChannel, peer.Address, false);
                }
            }
        }

        /// <summary>Asks the main loop to finish. Safe to call from any thread.</summary>
        public void RequestShutdown() => _running = false;

        public void Stop()
        {
            bool wasRunning = _running;
            _running = false;

            // Run() calls Receive and DeallocatePacket on _instance every iteration.
            // Disposing it underneath that loop is a use-after-free, so wait for the
            // loop to signal that it has left before touching the peer.
            if (wasRunning && _consoleThread != null)
            {
                if (!_loopExited.Wait(TimeSpan.FromSeconds(5)))
                    ServerLogger.Log("Main loop did not stop within 5s; shutting down anyway");
            }

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
