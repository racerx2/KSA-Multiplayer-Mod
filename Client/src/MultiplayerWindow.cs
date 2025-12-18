using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Extensions;
using Brutal.Numerics;
using KSA;

namespace KSA.Mods.Multiplayer
{
    public class MultiplayerWindow : ImGuiWindow, IStaticWindow
    {
        private readonly MultiplayerManager _multiplayerManager;
        private ImInputString _serverIpInput = new ImInputString(256, "localhost");
        private ImInputString _portInput = new ImInputString(10, "7777");
        private ImInputString _playerNameInput = new ImInputString(64, "Player");
        private ImInputString _passwordInput = new ImInputString(64, "");
        private ImInputString _chatInput = new ImInputString(256, "");
        private ImInputString _teleportDistanceInput = new ImInputString(32, "100");
        private double _lastUpdateTime = 0;
        
        // UI State
        private int _selectedTeleportPlayer = 0;
        private int _selectedSyncPlayer = 0;
        private List<string> _chatMessages = new List<string>();
        private const int MAX_CHAT_MESSAGES = 50;
        
        public MultiplayerWindow(MultiplayerManager manager, float2 initialSize) 
            : base(initialSize, lockAspectRatio: false)
        {
            _multiplayerManager = manager;
            SetWindowTitle(ModInfo.WindowTitle);
            
            // Load defaults from settings
            _playerNameInput = new ImInputString(64, MultiplayerSettings.Current.DefaultPlayerName);
            _serverIpInput = new ImInputString(256, MultiplayerSettings.Current.LastServerAddress);
            _portInput = new ImInputString(10, MultiplayerSettings.Current.DefaultServerPort.ToString());
            _passwordInput = new ImInputString(64, MultiplayerSettings.Current.ServerPassword);
            
            // Subscribe to chat messages
            if (_multiplayerManager.ChatManager != null)
            {
                _multiplayerManager.ChatManager.OnMessageReceived += OnChatMessageReceived;
            }
        }
        
        private void OnChatMessageReceived(string sender, string message)
        {
            _chatMessages.Add($"[{sender}]: {message}");
            if (_chatMessages.Count > MAX_CHAT_MESSAGES)
                _chatMessages.RemoveAt(0);
        }
        
        public override void DrawContent(Viewport viewport)
        {
            double currentTime = Universe.GetElapsedSimTime().Seconds();
            double deltaTime = _lastUpdateTime == 0 ? 0.016 : currentTime - _lastUpdateTime;
            _lastUpdateTime = currentTime;
            _multiplayerManager.Update(deltaTime);
            
            // Draw nametags above vehicles
            _multiplayerManager.NameTagRenderer?.DrawNameTags();
            
            // Check for system mismatch error
            if (!string.IsNullOrEmpty(_multiplayerManager.SystemMismatchError))
            {
                DrawSystemMismatchError();
                return; // Don't draw the rest of the UI
            }
            
            // Check for connection error (e.g., wrong password)
            if (!string.IsNullOrEmpty(_multiplayerManager.ConnectionError))
            {
                DrawConnectionError();
            }
            
            DrawConnectionSection();
            
            if (_multiplayerManager.IsConnected)
            {
                ImGui.Separator();
                DrawPlayerList();
                ImGui.Separator();
                DrawChatSection();
                ImGui.Separator();
                DrawSyncSection();
            }
            
            ImGui.Separator();
            DrawSettingsSection();
            ImGui.Separator();
            DrawCheatsSection();
            ImGui.Separator();
            DrawDebugSection();
            ImGui.Separator();
            DrawAboutSection();
        }
        
        private void DrawSystemMismatchError()
        {
            ImGui.TextColored(new float4(1, 0.3f, 0.3f, 1), "⚠ CONNECTION FAILED");
            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.TextColored(new float4(1, 0.8f, 0, 1), "System Mismatch!");
            ImGui.Spacing();
            
            ImGui.TextWrapped(_multiplayerManager.SystemMismatchError ?? "Unknown error");
            
            ImGui.Spacing();
            ImGui.Spacing();
            
            if (ImGui.Button("OK"))
            {
                _multiplayerManager.ClearSystemMismatchError();
            }
        }
        
        private void DrawConnectionError()
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new float4(0.3f, 0.1f, 0.1f, 0.8f));
            ImGui.BeginChild("ConnectionError", new float2(0, 60), ImGuiChildFlags.Borders);
            
            ImGui.TextColored(new float4(1, 0.4f, 0.4f, 1), "⚠ Connection Failed");
            ImGui.TextColored(new float4(1, 0.8f, 0.8f, 1), _multiplayerManager.ConnectionError ?? "Unknown error");
            
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X - 30);
            if (ImGui.SmallButton("X"))
            {
                _multiplayerManager.ClearConnectionError();
            }
            
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        private void DrawConnectionSection()
        {
            // Connection Status Indicator
            if (_multiplayerManager.IsConnected)
            {
                ImGui.TextColored(new float4(0, 1, 0, 1), "● CONNECTED");
                ImGui.SameLine();
                if (_multiplayerManager.IsHost)
                    ImGui.TextColored(new float4(1, 0.8f, 0, 1), "(HOST)");
                else
                    ImGui.Text("(Client)");
            }
            else
            {
                ImGui.TextColored(new float4(1, 0, 0, 1), "● DISCONNECTED");
            }
            
            ImGui.Spacing();
            
            // Connection inputs
            ImGui.Text("Server IP:"); ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputText("##serverip", _serverIpInput))
            {
                MultiplayerSettings.Current.LastServerAddress = _serverIpInput.ToString();
                MultiplayerSettings.Save();
            }
            
            ImGui.Text("Port:"); ImGui.SameLine();
            ImGui.SetNextItemWidth(60);
            if (ImGui.InputText("##port", _portInput))
            {
                if (ushort.TryParse(_portInput.ToString(), out ushort portNum))
                {
                    MultiplayerSettings.Current.DefaultServerPort = portNum;
                    MultiplayerSettings.Save();
                }
            }
            
            ImGui.Text("Name:"); ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputText("##playername", _playerNameInput))
            {
                MultiplayerSettings.Current.DefaultPlayerName = _playerNameInput.ToString();
                MultiplayerSettings.Save();
            }
            
            ImGui.Text("Password:"); ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputText("##password", _passwordInput, ImGuiInputTextFlags.Password))
            {
                MultiplayerSettings.Current.ServerPassword = _passwordInput.ToString();
                MultiplayerSettings.Save();
            }
            
            ImGui.Spacing();
            
            // Connection buttons
            if (!_multiplayerManager.IsConnected)
            {
                if (ImGui.Button("Connect") && ushort.TryParse(_portInput.ToString(), out ushort portNum))
                    _ = _multiplayerManager.JoinSession(_playerNameInput.ToString(), _serverIpInput.ToString(), portNum, _passwordInput.ToString());
            }
            else
            {
                if (ImGui.Button("Disconnect"))
                    _multiplayerManager.Disconnect();
            }
        }
        
        private void DrawPlayerList()
        {
            ImGui.Text("Players:");
            
            var players = _multiplayerManager.ConnectedPlayers;
            var subspaceManager = _multiplayerManager.SubspaceManager;
            bool isHost = _multiplayerManager.IsHost;
            string localPlayer = _multiplayerManager.LocalPlayerName ?? "";
            
            foreach (var player in players)
            {
                // Host indicator
                bool isPlayerHost = (player == "Player"); // TODO: Track actual host name
                
                // Player name with indicators
                string displayName = player;
                if (player == localPlayer)
                    displayName += " (You)";
                if (isPlayerHost)
                    displayName = "★ " + displayName;
                
                if (player == localPlayer)
                    ImGui.TextColored(new float4(0.5f, 1, 0.5f, 1), $"  {displayName}");
                else
                    ImGui.Text($"  {displayName}");
                
                // Time sync status indicator
                if (subspaceManager != null && player != localPlayer)
                {
                    bool sameSubspace = subspaceManager.IsInSameSubspace(player);
                    ImGui.SameLine();
                    if (sameSubspace)
                        ImGui.TextColored(new float4(0, 1, 0, 1), "[SYNC]");
                    else
                    {
                        double diff = subspaceManager.GetTimeDifference(player);
                        ImGui.TextColored(new float4(1, 0.5f, 0, 1), $"[{diff:+0.0;-0.0}s]");
                    }
                }
                
                // Kick button (host only, not for self)
                if (isHost && player != localPlayer)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Kick##{player}"))
                    {
                        KickPlayer(player);
                    }
                }
            }
        }

        private void DrawChatSection()
        {
            ImGui.Text("Chat:");
            
            // Chat history (scrollable)
            float chatHeight = 80;
            ImGui.BeginChild("ChatHistory", new float2(0, chatHeight), ImGuiChildFlags.Borders);
            foreach (var msg in _chatMessages)
            {
                ImGui.TextWrapped(msg);
            }
            // Auto-scroll to bottom
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10)
                ImGui.SetScrollHereY(1.0f);
            ImGui.EndChild();
            
            // Chat input
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
            bool enterPressed = ImGui.InputText("##chatinput", _chatInput, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Send") || enterPressed) && _chatInput.Length > 0)
            {
                string message = _chatInput.ToString();
                _multiplayerManager.ChatManager?.SendMessage(message);
                _chatInput.Clear();
            }
        }
        
        private void DrawSyncSection()
        {
            var subspaceManager = _multiplayerManager.SubspaceManager;
            if (subspaceManager == null) return;
            
            double localTime = subspaceManager.GetLocalTime();
            
            ImGui.Text("Time Sync:");
            ImGui.Text($"  Your Time: {FormatTime(localTime)}");
            
            // Show other players and their time differences
            var timeDiffs = subspaceManager.GetAllTimeDifferences();
            if (timeDiffs.Count > 0)
            {
                ImGui.Spacing();
                ImGui.Text("Other Players:");
                
                foreach (var kvp in timeDiffs)
                {
                    string playerName = kvp.Key;
                    double diff = kvp.Value;
                    bool sameSubspace = Math.Abs(diff) <= SubspaceManager.SYNC_THRESHOLD_SECONDS;
                    
                    // Color: green = same subspace, yellow = behind us, red = ahead of us
                    float4 color;
                    string status;
                    if (sameSubspace)
                    {
                        color = new float4(0, 1, 0, 1); // green
                        status = "IN SYNC";
                    }
                    else if (diff > 0)
                    {
                        color = new float4(1, 0.5f, 0, 1); // orange - they're ahead
                        status = $"+{diff:F1}s (GHOST)";
                    }
                    else
                    {
                        color = new float4(0.7f, 0.7f, 0.7f, 1); // gray - they're behind
                        status = $"{diff:F1}s (behind)";
                    }
                    
                    ImGui.TextColored(color, $"  {playerName}: {status}");
                }
                
                // Build list of players we can sync to (those ahead of us)
                var syncablePlayers = new List<string>();
                var syncableTimeDiffs = new List<double>();
                
                foreach (var kvp in timeDiffs)
                {
                    if (kvp.Value > SubspaceManager.SYNC_THRESHOLD_SECONDS)
                    {
                        syncablePlayers.Add(kvp.Key);
                        syncableTimeDiffs.Add(kvp.Value);
                    }
                }
                
                // Sync dropdown and button
                if (syncablePlayers.Count > 0)
                {
                    ImGui.Spacing();
                    
                    // Validate selection index
                    if (_selectedSyncPlayer >= syncablePlayers.Count)
                        _selectedSyncPlayer = 0;
                    
                    // Player dropdown
                    ImGui.Text("Sync to:");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(120);
                    
                    if (ImGui.BeginCombo("##syncplayer", syncablePlayers[_selectedSyncPlayer]))
                    {
                        for (int i = 0; i < syncablePlayers.Count; i++)
                        {
                            bool isSelected = (_selectedSyncPlayer == i);
                            string label = $"{syncablePlayers[i]} (+{syncableTimeDiffs[i]:F1}s)";
                            if (ImGui.Selectable(label, isSelected))
                                _selectedSyncPlayer = i;
                            if (isSelected)
                                ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                    
                    // Sync button
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Button, new float4(0, 0.6f, 0, 1));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new float4(0, 0.8f, 0, 1));
                    
                    string targetPlayer = syncablePlayers[_selectedSyncPlayer];
                    double targetDiff = syncableTimeDiffs[_selectedSyncPlayer];
                    
                    if (ImGui.Button("Sync"))
                    {
                        if (subspaceManager.SyncToPlayer(targetPlayer))
                        {
                            ModLogger.Log("Sync", $"Synced to {targetPlayer}");
                        }
                    }
                    
                    ImGui.PopStyleColor(2);
                    ImGui.SameLine();
                    ImGui.TextColored(new float4(0, 1, 0, 1), $"← Jump {targetDiff:F1}s forward");
                }
            }
        }
        
        private string FormatTime(double seconds)
        {
            int hours = (int)(seconds / 3600);
            int mins = (int)((seconds % 3600) / 60);
            int secs = (int)(seconds % 60);
            return $"{hours:D2}:{mins:D2}:{secs:D2}";
        }
        
        private void DrawSettingsSection()
        {
            ImGui.Text("Settings:");
            
            bool showNameTags = MultiplayerSettings.Current.ShowNameTags;
            if (ImGui.Checkbox("Show Ship Nametags", ref showNameTags))
            {
                MultiplayerSettings.Current.ShowNameTags = showNameTags;
                MultiplayerSettings.Save();
            }
            
            bool enableLogging = MultiplayerSettings.Current.EnableDebugLogging;
            if (ImGui.Checkbox("Enable Debug Logging", ref enableLogging))
            {
                MultiplayerSettings.Current.EnableDebugLogging = enableLogging;
                MultiplayerSettings.Save();
            }
        }

        private void DrawDebugSection()
        {
            // Collapsible debug section
            if (ImGui.CollapsingHeader("Debug", ImGuiTreeNodeFlags.None))
            {
                // Make section resizable via child window
                ImGui.BeginChild("DebugContent", new float2(0, 200), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeY);
                
                var subspaceManager = _multiplayerManager.SubspaceManager;
                var syncManager = _multiplayerManager.SyncManager;
                var vehicleRenderer = _multiplayerManager.VehicleRenderer;
                
                // Subspace Info
                ImGui.Text("=== Subspace (Time-Based) ===");
                if (subspaceManager != null)
                {
                    ImGui.Text($"Status: {subspaceManager.GetStatusString()}");
                    ImGui.Text($"Sync Threshold: {SubspaceManager.SYNC_THRESHOLD_SECONDS:F1}s");
                    ImGui.Text($"Sync Available: {subspaceManager.IsSyncAvailable()}");
                }
                
                ImGui.Spacing();
                
                // Time Info
                ImGui.Text("=== Time ===");
                ImGui.Text($"Local SimTime: {Universe.GetElapsedSimTime().Seconds():F3}s");
                ImGui.Text($"Simulation Speed: {Universe.SimulationSpeed}x");
                
                ImGui.Spacing();
                
                // Network Info
                ImGui.Text("=== Network ===");
                ImGui.Text($"Connected: {_multiplayerManager.IsConnected}");
                ImGui.Text($"Is Host: {_multiplayerManager.IsHost}");
                ImGui.Text($"Player Count: {_multiplayerManager.ConnectedPlayers.Count}");
                // TODO: Add latency per player when implemented
                
                ImGui.Spacing();
                
                // Vehicle Info
                ImGui.Text("=== Vehicles ===");
                if (vehicleRenderer != null)
                {
                    ImGui.Text($"Remote Vehicles: {vehicleRenderer.RemoteVehicleCount}");
                }
                if (syncManager != null)
                {
                    ImGui.Text($"Events Detected: {syncManager.EventCount}");
                }
                
                ImGui.Spacing();
                
                // Players & Time Sync
                ImGui.Text("=== Players & Time ===");
                if (subspaceManager != null)
                {
                    string localPlayer = _multiplayerManager.LocalPlayerName ?? "";
                    double localTime = subspaceManager.GetLocalTime();
                    
                    foreach (var player in _multiplayerManager.ConnectedPlayers)
                    {
                        double theirTime = subspaceManager.GetPlayerTime(player);
                        double diff = theirTime - localTime;
                        bool sameSubspace = subspaceManager.IsInSameSubspace(player);
                        
                        string status = player == localPlayer ? "(You)" : 
                            (sameSubspace ? "[SYNC]" : $"[{diff:+0.0;-0.0}s]");
                        ImGui.Text($"  {player}: T={theirTime:F1}s {status}");
                    }
                }
                
                ImGui.EndChild();
            }
        }
        
        private void DrawCheatsSection()
        {
            if (!_multiplayerManager.IsConnected)
                return;
            
            if (ImGui.CollapsingHeader("Cheats"))
            {
                DrawTeleportSection();
            }
        }
        
        private void DrawTeleportSection()
        {
            var players = _multiplayerManager.ConnectedPlayers;
            string localPlayer = _multiplayerManager.LocalPlayerName ?? "";
            var subspaceManager = _multiplayerManager.SubspaceManager;
            
            // Build list of other players that are in sync
            var syncedPlayers = new List<string>();
            foreach (var p in players)
            {
                if (p != localPlayer)
                {
                    bool inSync = subspaceManager?.IsInSameSubspace(p) ?? false;
                    if (inSync)
                        syncedPlayers.Add(p);
                }
            }
            
            if (syncedPlayers.Count == 0)
            {
                ImGui.TextColored(new float4(0.7f, 0.7f, 0.7f, 1), "No synced players to teleport to");
                ImGui.TextColored(new float4(1, 0.5f, 0, 1), "(Players must be in sync first)");
                return;
            }
            
            // Player dropdown
            ImGui.Text("Target Player:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            
            string[] playerArray = syncedPlayers.ToArray();
            if (_selectedTeleportPlayer >= playerArray.Length)
                _selectedTeleportPlayer = 0;
            
            if (ImGui.BeginCombo("##teleportplayer", playerArray[_selectedTeleportPlayer]))
            {
                for (int i = 0; i < playerArray.Length; i++)
                {
                    bool isSelected = (_selectedTeleportPlayer == i);
                    if (ImGui.Selectable(playerArray[i], isSelected))
                        _selectedTeleportPlayer = i;
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            
            // Distance input
            ImGui.Text("Distance (m):");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.InputText("##teleportdist", _teleportDistanceInput);
            
            // Teleport button
            ImGui.SameLine();
            if (ImGui.Button("Teleport"))
            {
                string targetPlayer = playerArray[_selectedTeleportPlayer];
                if (float.TryParse(_teleportDistanceInput.ToString(), out float distance))
                {
                    TeleportToPlayer(targetPlayer, distance);
                }
            }
        }

        private void KickPlayer(string playerName)
        {
            ModLogger.Log("Players", $"Kicking player: {playerName}");
            
            // Send kick message to player first
            _multiplayerManager.ChatManager?.SendSystemMessage($"{playerName} was kicked from the server.");
            
            // TODO: Send actual kick message via network, then disconnect them
            // For now just log it
            ModLogger.Log("Players", $"TODO: Implement actual kick for {playerName}");
        }
        
        private void TeleportToPlayer(string targetPlayer, float distance)
        {
            ModLogger.Log("GOTO", $"Teleport requested to {targetPlayer} at {distance}m distance");
            
            var syncManager = _multiplayerManager.SyncManager;
            var vehicleRenderer = _multiplayerManager.VehicleRenderer;
            
            if (syncManager == null || vehicleRenderer == null)
            {
                ModLogger.Log("GOTO", "ERROR: SyncManager or VehicleRenderer is null");
                return;
            }
            
            // Find the target player's remote vehicle data
            var remoteVehicles = syncManager.GetRemoteVehicles();
            EventSyncManager.RemoteVehicleData? targetData = null;
            string targetKey = "";
            
            foreach (var kvp in remoteVehicles)
            {
                if (kvp.Value.OwnerName == targetPlayer)
                {
                    targetData = kvp.Value;
                    targetKey = kvp.Key;
                    break;
                }
            }
            
            if (targetData == null)
            {
                ModLogger.Log("GOTO", $"ERROR: Could not find vehicle data for {targetPlayer}");
                return;
            }
            
            // Get the actual remote vehicle object to get its CURRENT rendered position
            Vehicle? remoteVehicle = vehicleRenderer.GetRemoteVehicle(targetKey);
            if (remoteVehicle == null)
            {
                ModLogger.Log("GOTO", $"ERROR: Remote vehicle object not found for {targetKey}");
                return;
            }
            
            // Get local vehicle
            Vehicle? localVehicle = Program.ControlledVehicle;
            if (localVehicle == null)
            {
                ModLogger.Log("GOTO", "ERROR: No local controlled vehicle");
                return;
            }
            
            // Get parent celestial
            string parentId = string.IsNullOrEmpty(targetData.ParentBodyId) ? "Earth" : targetData.ParentBodyId;
            Celestial? parent = Universe.CurrentSystem?.Get(parentId) as Celestial;
            if (parent == null)
            {
                ModLogger.Log("GOTO", $"ERROR: Parent body {parentId} not found");
                return;
            }
            
            // Get CURRENT rendered position from remote vehicle's orbit (not stale network data)
            // This accounts for clock drift - orbit has propagated since last network update
            var targetPos = remoteVehicle.Orbit.StateVectors.PositionCci;
            var targetVel = remoteVehicle.Orbit.StateVectors.VelocityCci;
            
            // Prograde unit vector
            var progradeDir = targetVel.Normalized();
            
            // Offset position (behind the target in prograde direction)
            var offsetPos = targetPos - progradeDir * distance;
            
            // Use LOCAL time for teleporting LOCAL vehicle
            // (Remote vehicles use sender's time, but our vehicle must use our time)
            SimTime localTime = Universe.GetElapsedSimTime();
            
            // Create new orbit for local vehicle at offset position with same velocity
            Orbit orbit = Orbit.CreateFromStateCci(parent, localTime, offsetPos, targetVel, localVehicle.OrbitColor);
            localVehicle.SetFlightPlan(new FlightPlan(orbit, new KeyHash((uint)localVehicle.Id.GetHashCode())));
            localVehicle.UpdatePerFrameData();
            
            // CRITICAL: Update KinematicStates.Time to match the new orbit's state time
            // Without this, KSA throws "Populating kinematic states from outdated analytic states"
            UpdateVehicleKinematicStates(localVehicle, orbit.StateVectors);
            
            ModLogger.Log("GOTO", $"SUCCESS: Teleported to {targetPlayer} at {distance}m distance");
            ModLogger.Log("GOTO", $"  Target pos: ({targetPos.X:F0},{targetPos.Y:F0},{targetPos.Z:F0})");
            ModLogger.Log("GOTO", $"  New pos: ({offsetPos.X:F0},{offsetPos.Y:F0},{offsetPos.Z:F0})");
            ModLogger.Log("GOTO", $"  State time: {localTime.Seconds():F3}s");
            
            // Force remote vehicle to resync visual to network position
            targetData.SituationChanged = true;
        }
        
        /// <summary>
        /// Update vehicle's KinematicStates to match a new orbit.
        /// Uses reflection to access private _lastKinematicStates field.
        /// </summary>
        private void UpdateVehicleKinematicStates(Vehicle vehicle, StateVectors stateVectors)
        {
            try
            {
                var kinematicField = typeof(Vehicle).GetField("_lastKinematicStates",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (kinematicField == null) return;
                
                object? kinematicObj = kinematicField.GetValue(vehicle);
                if (kinematicObj == null) return;
                
                KinematicStates kinematic = (KinematicStates)kinematicObj;
                
                // Sync times to prevent "outdated analytic states" error
                kinematic.Time = stateVectors.StateTime;
                kinematic.PositionPhys = stateVectors.PositionCci;
                kinematic.VelocityPhys = stateVectors.VelocityCci;
                kinematic.PhysFrame = PhysicsFrame.Cci;
                
                kinematicField.SetValue(vehicle, kinematic);
                ModLogger.Log("GOTO", $"Updated KinematicStates.Time to {stateVectors.StateTime.Seconds():F3}s");
            }
            catch (Exception ex)
            {
                ModLogger.Log("GOTO", $"ERROR updating KinematicStates: {ex.Message}");
            }
        }
        
        private void DrawAboutSection()
        {
            if (ImGui.CollapsingHeader("About", ImGuiTreeNodeFlags.None))
            {
                ImGui.Spacing();
                ImGui.Text(ModInfo.FullName);
                ImGui.Text($"Author: {ModInfo.Author}");
                ImGui.Spacing();
                
                ImGui.Text("GitHub:");
                ImGui.SameLine();
                ImGui.TextColored(new float4(0.4f, 0.7f, 1.0f, 1.0f), ModInfo.GitHubUrl);
                
                if (ImGui.Button("Copy GitHub URL"))
                {
                    ImGui.SetClipboardText(ModInfo.GitHubUrl);
                }
                
                ImGui.Spacing();
            }
        }
    }
}
