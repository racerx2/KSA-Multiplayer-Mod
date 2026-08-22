using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Extensions;
using Brutal.Numerics;
using KSA;

namespace KSA.Mods.Multiplayer
{
    /// <summary>The multiplayer panel, drawn with ImGui.Begin.</summary>
    public class MultiplayerWindow
    {
        /// <summary>Window visibility.</summary>
        public bool Show = true;

        private static readonly float2 InitialPosition = new float2(60f, 90f);
        private readonly float2 _initialSize;

        private readonly MultiplayerManager _multiplayerManager;
        private ImInputString _serverIpInput = new ImInputString(256, "localhost");
        private ImInputString _portInput = new ImInputString(10, "7777");
        private ImInputString _playerNameInput = new ImInputString(64, "Player");
        private ImInputString _passwordInput = new ImInputString(64, "");
        private ImInputString _chatInput = new ImInputString(256, "");
        private ImInputString _teleportDistanceInput = new ImInputString(32, "100");
        
        // UI State
        private int _selectedTeleportPlayer = 0;
        private int _selectedSyncPlayer = 0;
        private int _selectedLocalCraft = 0;
        private List<string> _chatMessages = new List<string>();
        private const int MAX_CHAT_MESSAGES = 50;
        
        public MultiplayerWindow(MultiplayerManager manager, float2 initialSize)
        {
            _initialSize = initialSize;
            _multiplayerManager = manager;
            
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
        
        public bool IsShown => Show;
        public void Toggle() => Show = !Show;
        public void SetShown(bool shown) => Show = shown;

        /// <summary>Draws the window for this frame.</summary>
        public void Draw(Viewport viewport)
        {
            try
            {
                // Draw nametags regardless of panel state.
                _multiplayerManager.NameTagRenderer?.DrawNameTags();

                // Another player asking to be undocked needs answering whether
                // or not this panel is open, so it is drawn before the check.
                DrawUndockPrompt();

                if (!Show)
                    return;

                ImGui.SetNextWindowPos(in InitialPosition, ImGuiCond.FirstUseEver, null);
                ImGui.SetNextWindowSize(in _initialSize, ImGuiCond.FirstUseEver);

                // Begin returns false while the window is collapsed.
                if (!ImGui.Begin(ModInfo.WindowTitle, ref Show))
                {
                    ImGui.End();
                    return;
                }

                // End must run even if the content throws, or the ImGui window stack is
                // left unbalanced and every later frame draws into a broken state.
                try
                {
                    DrawContent(viewport);
                }
                finally
                {
                    ImGui.End();
                }
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways("Renderer", $"MultiplayerWindow.Draw failed: {ex}");
            }
        }

        private void DrawContent(Viewport viewport)
        {
            // A version mismatch refuses the connection; nothing else matters.
            if (!string.IsNullOrEmpty(_multiplayerManager.VersionMismatchError))
            {
                DrawVersionMismatchError();
                return;
            }

            // A game type mismatch also refuses the connection.
            if (!string.IsNullOrEmpty(_multiplayerManager.GameTypeMismatchError))
            {
                DrawGameTypeMismatchError();
                return;
            }

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
                ImGui.Separator();
                DrawChatSection();
                ImGui.Separator();
                DrawCraftSection();
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
        
        /// <summary>Reports that the connection was refused because the mod versions differ.</summary>
        private void DrawVersionMismatchError()
        {
            ImGui.TextColored(new float4(1, 0.3f, 0.3f, 1), "\u26A0 CONNECTION REFUSED");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(new float4(1, 0.8f, 0, 1), "Version Mismatch!");
            ImGui.Spacing();

            ImGui.TextWrapped(_multiplayerManager.VersionMismatchError ?? "Unknown error");
            ImGui.Spacing();

            ImGui.Text("Downloads:");
            ImGui.SameLine();
            ImGui.TextColored(new float4(0.4f, 0.7f, 1.0f, 1.0f), ModInfo.GitHubUrl + "/releases");

            if (ImGui.Button("Copy download link"))
                ImGui.SetClipboardText(ModInfo.GitHubUrl + "/releases");
            ImGui.SameLine();
            if (ImGui.Button("OK##version"))
                _multiplayerManager.ClearVersionMismatchError();
        }

        /// <summary>Reports that the connection was refused because the game types differ.</summary>
        private void DrawGameTypeMismatchError()
        {
            ImGui.TextColored(new float4(1, 0.3f, 0.3f, 1), "\u26A0 CONNECTION REFUSED");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(new float4(1, 0.8f, 0, 1), "Game Type Mismatch!");
            ImGui.Spacing();

            ImGui.TextWrapped(_multiplayerManager.GameTypeMismatchError ?? "Unknown error");
            ImGui.Spacing();

            if (ImGui.Button("OK##gametype"))
                _multiplayerManager.ClearGameTypeMismatchError();
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
            ImGui.BeginChild("ConnectionError", new float2(0, 120), ImGuiChildFlags.Borders);

            ImGui.TextColored(new float4(1, 0.4f, 0.4f, 1), "⚠ Connection Failed");

            // Refusal reasons are full sentences, so the text has to wrap.
            ImGui.PushStyleColor(ImGuiCol.Text, new float4(1, 0.8f, 0.8f, 1));
            ImGui.TextWrapped(_multiplayerManager.ConnectionError ?? "Unknown error");
            ImGui.PopStyleColor();

            ImGui.Spacing();
            if (ImGui.SmallButton("Dismiss"))
            {
                _multiplayerManager.ClearConnectionError();
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        private void DrawConnectionSection()
        {
            if (!_multiplayerManager.IsConnected)
            {
                ImGui.TextColored(
                    new float4(0.4f, 0.85f, 1f, 1f),
                    "Join a multiplayer server");
                ImGui.TextWrapped(
                    "Enter the server address and click Connect. " +
                    "If the server is password protected, fill in the password too.");
                ImGui.Spacing();
            }

            // Connection Status Indicator
            if (_multiplayerManager.IsConnected)
            {
                if (_multiplayerManager.IsWorldReady)
                    ImGui.TextColored(new float4(0, 1, 0, 1), "● CONNECTED");
                else
                    ImGui.TextColored(new float4(1, 0.8f, 0, 1), "● AUTHENTICATING");
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
            ImGui.Text("Server address:"); ImGui.SameLine();
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
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputText("##playername", _playerNameInput))
            {
                MultiplayerSettings.Current.DefaultPlayerName = _playerNameInput.ToString();
                MultiplayerSettings.Save();
            }

            // Warns before Connect is pressed rather than after the server refuses.
            if (!_multiplayerManager.IsConnected)
            {
                string? nameProblem = MultiplayerManager.ValidatePlayerName(_playerNameInput.ToString());
                if (nameProblem != null)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new float4(1f, 0.75f, 0.3f, 1f));
                    ImGui.TextWrapped(nameProblem);
                    ImGui.PopStyleColor();
                }
            }

            ImGui.Text("Password:"); ImGui.SameLine();
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputText("##serverpassword", _passwordInput))
            {
                MultiplayerSettings.Current.ServerPassword = _passwordInput.ToString();
                MultiplayerSettings.Save();
            }
            
            ImGui.Spacing();
            
            // Connection buttons
            if (!_multiplayerManager.IsConnected)
            {
                if (ImGui.Button("Connect"))
                {
                    if (ushort.TryParse(_portInput.ToString(), out ushort port))
                    {
                        _ = _multiplayerManager.JoinSession(
                            _playerNameInput.ToString(), _serverIpInput.ToString(), port,
                            _passwordInput.ToString());
                    }
                }
            }
            else
            {
                if (ImGui.Button("Disconnect"))
                    _multiplayerManager.Disconnect();
            }
        }
        
        /// <summary>Draws the ask-to-undock prompt, when another player is waiting on one.</summary>
        /// <remarks>
        /// Its own window rather than a section of the panel: the player being
        /// asked is flying, and may well have the multiplayer panel closed. It
        /// takes no keyboard focus, so it cannot swallow flight controls while
        /// it is up, and it disappears on its own when the ask lapses.
        /// </remarks>
        private void DrawUndockPrompt()
        {
            UndockRequests.Prompt? prompt = UndockRequests.Pending;
            if (prompt == null) return;

            ImGui.SetNextWindowSize(new float2(360, 0), ImGuiCond.Always);
            if (!ImGui.Begin("Undock request###mpUndockAsk", ImGuiWindowFlags.NoCollapse |
                                                             ImGuiWindowFlags.NoFocusOnAppearing |
                                                             ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.End();
                return;
            }

            try
            {
                ImGui.TextWrapped($"{prompt.Requester} is asking to undock from {prompt.StackName}.");
                ImGui.Spacing();
                ImGui.TextColored(new float4(0.7f, 0.7f, 0.7f, 1),
                    $"Port #{prompt.ConnectorIndex} - {prompt.SecondsLeft:F0}s left");
                ImGui.Spacing();

                if (ImGui.Button("Undock them", new float2(150, 0)))
                    UndockRequests.Allow();

                ImGui.SameLine();
                if (ImGui.Button("Decline", new float2(150, 0)))
                    UndockRequests.Decline();
            }
            finally
            {
                ImGui.End();
            }
        }

        private void DrawPlayerList()
        {
            ImGui.Text("Players:");
            
            var players = _multiplayerManager.ConnectedPlayers;
            var subspaceManager = _multiplayerManager.SubspaceManager;
            string localPlayer = _multiplayerManager.LocalPlayerName ?? "";
            
            foreach (var player in players)
            {
                // Stars the player the server reports as host.
                string hostName = _multiplayerManager.HostPlayerName;
                bool isPlayerHost = !string.IsNullOrEmpty(hostName) &&
                    string.Equals(player, hostName, StringComparison.OrdinalIgnoreCase);
                
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
                    if (subspaceManager.IsPlayerTimeStale(player))
                    {
                        // Shows how long the player's time reading has been stale.
                        double age = subspaceManager.SecondsSincePlayerTimeUpdate(player);
                        string label = age < 0 ? "[no data]" : $"[silent {age:F0}s]";
                        ImGui.TextColored(new float4(1, 0.4f, 0.4f, 1), label);
                    }
                    else if (sameSubspace)
                        ImGui.TextColored(new float4(0, 1, 0, 1), "[SYNC]");
                    else
                    {
                        double diff = subspaceManager.GetTimeDifference(player);
                        ImGui.TextColored(new float4(1, 0.5f, 0, 1), $"[{diff:+0.0;-0.0}s]");
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
        
        /// <summary>Draws the craft upload picker and the server's shared craft list.</summary>
        private void DrawCraftSection()
        {
            var craftShare = _multiplayerManager.CraftShareManager;
            if (craftShare == null) return;

            ImGui.Text("Craft Sharing:");

            DrawCraftUploadRow(craftShare);
            DrawCraftLibraryList(craftShare);

            ImGui.TextDisabled("Downloads appear in the Vehicle Editor under VEHICLE SAVES.");

            if (!string.IsNullOrEmpty(craftShare.StatusText))
            {
                float4 colour = craftShare.StatusIsError
                    ? new float4(1f, 0.5f, 0.5f, 1f)
                    : new float4(0.6f, 0.9f, 1f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Text, colour);
                ImGui.TextWrapped(craftShare.StatusText);
                ImGui.PopStyleColor();
            }
        }

        /// <summary>Draws the picker and buttons for sharing one of this machine's saved craft.</summary>
        private void DrawCraftUploadRow(CraftShareManager craftShare)
        {
            var localCraft = craftShare.LocalCraft;

            if (localCraft.Count == 0)
            {
                ImGui.TextDisabled("  No craft found on this machine.");
            }
            else
            {
                if (_selectedLocalCraft >= localCraft.Count)
                    _selectedLocalCraft = 0;

                ImGui.Text("Share:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(200);

                if (ImGui.BeginCombo("##sharecraft", localCraft[_selectedLocalCraft].DisplayName))
                {
                    for (int i = 0; i < localCraft.Count; i++)
                    {
                        bool isSelected = _selectedLocalCraft == i;
                        string label = $"{localCraft[i].DisplayName}  ({localCraft[i].SizeText})";
                        if (ImGui.Selectable(label, isSelected))
                            _selectedLocalCraft = i;
                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                if (ImGui.Button("Upload"))
                    craftShare.UploadCraft(localCraft[_selectedLocalCraft]);
            }

            ImGui.SameLine();
            if (ImGui.Button("Rescan"))
            {
                craftShare.RefreshLocalCraft();
                craftShare.RequestCatalogue();
            }

            // Sharing what is being flown needs no save file to exist first.
            string flying = CraftShareManager.CurrentVesselName();
            if (flying.Length > 0)
            {
                if (ImGui.Button($"Share what I'm flying ({flying})"))
                    craftShare.UploadCurrentVessel();
            }
            else
            {
                ImGui.TextDisabled("  Fly a vessel to share it directly.");
            }
        }

        /// <summary>Draws the craft the server holds, each with a download button.</summary>
        private void DrawCraftLibraryList(CraftShareManager craftShare)
        {
            var catalogue = craftShare.Catalogue;
            ImGui.Text($"Shared craft ({catalogue.Count}):");

            ImGui.BeginChild("SharedCraftList", new float2(0, 110), ImGuiChildFlags.Borders);

            if (catalogue.Count == 0)
            {
                ImGui.TextDisabled("Nothing has been shared on this server yet.");
                ImGui.EndChild();
                return;
            }

            string localPlayer = _multiplayerManager.LocalPlayerName ?? "";

            for (int i = 0; i < catalogue.Count; i++)
            {
                var entry = catalogue[i];

                if (craftShare.IsDownloading(entry.CraftId))
                    ImGui.TextDisabled("...");
                else if (ImGui.SmallButton($"Get##craft{i}"))
                    craftShare.DownloadCraft(entry.CraftId);

                ImGui.SameLine();

                string shared = FormatSharedOn(entry.SharedUtcTicks);
                string line =
                    $"{entry.CraftName}   by {entry.OwnerPlayerName}   " +
                    $"{entry.SizeBytes / 1024} KB   {shared}";

                // Marks the craft this player shared.
                if (entry.OwnerPlayerName.Equals(localPlayer, StringComparison.OrdinalIgnoreCase))
                    ImGui.TextColored(new float4(0.5f, 1f, 0.5f, 1f), line);
                else
                    ImGui.Text(line);
            }

            ImGui.EndChild();
        }

        /// <summary>Formats a shared-on time, or blank when the value is not a date.</summary>
        private static string FormatSharedOn(long utcTicks)
        {
            // An exception here would escape between BeginChild and EndChild.
            if (utcTicks <= 0 || utcTicks > DateTime.MaxValue.Ticks)
                return string.Empty;

            try
            {
                return new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm");
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        }

        private void DrawSyncSection()
        {
            var subspaceManager = _multiplayerManager.SubspaceManager;
            if (subspaceManager == null) return;
            
            double localTime = subspaceManager.GetLocalTime();
            
            ImGui.Text("Time Sync:");
            
            var slew = _multiplayerManager.TimeSlew;
            
            if (slew != null && slew.IsSlewing)
            
            {
            
                ImGui.TextColored(new float4(0.4f, 0.8f, 1f, 1f),
            
                    $"   Catching up: {slew.CurrentGap:F1}s behind, running slightly fast");
            
            }
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
            // Docking controls live in KSA's port context menu, not here.

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
            
            bool logDockingReadout = MultiplayerSettings.Current.LogDockingReadout;
            if (ImGui.Checkbox("Trace Docking Readout (per frame)", ref logDockingReadout))
            {
                MultiplayerSettings.Current.LogDockingReadout = logDockingReadout;
                MultiplayerSettings.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Records docking distance and alignment every frame inside 100 m.\n" +
                                 "Very large logs - switch on only to diagnose a docking problem.");
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
                ImGui.Text($"Local SimTime: {Universe.GetElapsedTime().Seconds():F3}s");
                ImGui.Text($"Simulation Speed: {Universe.SimulationSpeed}x");
                
                ImGui.Spacing();
                
                // Network Info
                ImGui.Text("=== Network ===");
                ImGui.Text($"Connected: {_multiplayerManager.IsConnected}");
                ImGui.Text($"Is Host: {_multiplayerManager.IsHost}");
                ImGui.Text($"Player Count: {_multiplayerManager.ConnectedPlayers.Count}");
                // Not implemented: no per-player latency is measured.
                
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

        // Kicking is a server-console operation ("kick <name>"). There is no
        // client-side kick: the server has no concept of an administrator, so a
        // kick request arriving over the wire could not be told apart from any
        // other client's, and every player would be able to remove every other.
        // A GUI kick needs that authority model first.

        private void TeleportToPlayer(string targetPlayer, float distance)
        {
            ModLogger.Log("GOTO", $"Teleport requested to {targetPlayer} at {distance}m distance");

            try
            {
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
            
            foreach (var kvp in remoteVehicles)
            {
                if (kvp.Value.OwnerName == targetPlayer &&
                    (targetData == null || kvp.Value.LastUpdate > targetData.LastUpdate))
                {
                    targetData = kvp.Value;
                }
            }
            
            if (targetData == null)
            {
                ModLogger.Log("GOTO", $"ERROR: Could not find vehicle data for {targetPlayer}");
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
                TimedAlert.Create($"Cannot teleport: {parentId} was not found", Color.Red, 4.0);
                return;
            }

            if (localVehicle.Parent?.Id != parent.Id)
            {
                ModLogger.Log("GOTO", $"ERROR: Target orbits {parent.Id}, local vehicle orbits {localVehicle.Parent?.Id}");
                TimedAlert.Create("Teleport requires both ships to orbit the same body", Color.Red, 5.0);
                return;
            }

            UniverseTime localTime = Universe.GetElapsedTime();
            double3 targetPos;
            double3 targetVel;
            double3 offsetDirection;

            if (targetData.LastSituation >= 2)
            {
                // Convert the surface state from the body-fixed frame.
                doubleQuat ccf2Cci = parent.GetCcf2Cci(localTime);
                targetPos = targetData.TargetPositionCcf.Transform(ccf2Cci);
                double3 omega = new double3(0, 0, parent.GetAngularVelocity());
                targetVel = targetData.TargetVelocityCcf.Transform(ccf2Cci) +
                    double3.Cross(omega, targetPos);

                double3 horizontalDirection = targetData.TargetVelocityCcf;
                if (horizontalDirection.Length() < 0.1)
                {
                    double3 up = targetData.TargetPositionCcf.Normalized();
                    double3 reference = Math.Abs(up.Z) < 0.9
                        ? new double3(0, 0, 1)
                        : new double3(0, 1, 0);
                    horizontalDirection = double3.Cross(up, reference);
                }

                offsetDirection = horizontalDirection.Normalized().Transform(ccf2Cci);
            }
            else
            {
                // Propagate orbital state from the sender epoch to our current time.
                UniverseTime senderTime = new UniverseTime(targetData.SenderStateTimeSeconds);
                Orbit targetOrbit = Orbit.CreateFromStateCci(
                    parent, senderTime, targetData.TargetPosition,
                    targetData.TargetVelocity, localVehicle.OrbitColor);
                StateVectors targetState = targetOrbit.GetStateVectorsAt(localTime);
                targetPos = targetState.PositionCci;
                targetVel = targetState.VelocityCci;
                offsetDirection = targetVel.Normalized();
            }

            double3 offsetPos = targetPos - offsetDirection * distance;

            // Teleport the vehicle onto the offset orbit.
            Orbit orbit = Orbit.CreateFromStateCci(parent, localTime, offsetPos, targetVel, localVehicle.OrbitColor);
            localVehicle.Teleport(orbit, null, null);
            UpdateVehicleKinematicStates(localVehicle, orbit.StateVectors);
            localVehicle.UpdatePerFrameData();
            Universe.CurrentSystem?.UpdatePerFrameData();

            localVehicle.GetPhysicsStatesMutable().GetStatesCci(
                out double3 actualPosition, out _, out _);
            double teleportError = (actualPosition - offsetPos).Length();
            if (teleportError > Math.Max(25.0, distance * 2.0))
            {
                ModLogger.Log("GOTO",
                    $"Native teleport verification failed by {teleportError:F1}m; applying analytic fallback");
                localVehicle.SetFlightPlan(
                    new FlightPlan(orbit, new KeyHash((uint)localVehicle.Id.GetHashCode())));
                UpdateVehicleKinematicStates(localVehicle, orbit.StateVectors);
                localVehicle.UpdatePerFrameData();
                Universe.CurrentSystem?.UpdatePerFrameData();
                localVehicle.GetPhysicsStatesMutable().GetStatesCci(
                    out actualPosition, out _, out _);
                teleportError = (actualPosition - offsetPos).Length();
            }

            if (teleportError > Math.Max(25.0, distance * 2.0))
                throw new InvalidOperationException(
                    $"KSA kept the ship {teleportError:F0} m from the requested destination.");
            
            ModLogger.Log("GOTO", $"SUCCESS: Teleported to {targetPlayer} at {distance}m distance");
            ModLogger.Log("GOTO", $"  Target pos: ({targetPos.X:F0},{targetPos.Y:F0},{targetPos.Z:F0})");
            ModLogger.Log("GOTO", $"  New pos: ({offsetPos.X:F0},{offsetPos.Y:F0},{offsetPos.Z:F0})");
            ModLogger.Log("GOTO", $"  State time: {localTime.Seconds():F3}s");
            ModLogger.Log("GOTO", $"  Verified error: {teleportError:F2}m");
            
            // Force remote vehicle to resync visual to network position
            targetData.SituationChanged = true;
            Universe.CurrentSystem?.UpdatePerFrameData();
            TimedAlert.Create($"Teleported near {targetPlayer}", Color.Green, 4.0);
            }
            catch (Exception ex)
            {
                ModLogger.Log("GOTO", $"ERROR: Teleport failed: {ex}");
                TimedAlert.Create($"Teleport failed: {ex.Message}", Color.Red, 5.0);
            }
        }

        /// <summary>Updates the vehicle's physics states to match a new orbit.</summary>
        private void UpdateVehicleKinematicStates(Vehicle vehicle, StateVectors stateVectors)
        {
            try
            {
                vehicle.GetPhysicsStatesMutable().UpdateFromAnalytic(
                    vehicle.Orbit, in stateVectors, vehicle.Body2Cce, vehicle.BodyRates, vehicle.Situation);
                ModLogger.Log("GOTO", $"Updated physics states to {stateVectors.StateTime.Seconds():F3}s");
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
                // Show the copyright and license notice.
                ImGui.Text(ModInfo.Copyright);
                ImGui.Text(ModInfo.License);
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
