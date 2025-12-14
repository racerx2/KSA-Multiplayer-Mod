using System;
using Brutal.Logging;
using Brutal.ImGuiApi.Abstractions;
using KSA;

namespace KSA.Mods.Multiplayer
{
    public static class MultiplayerCommands
    {
        public static void RegisterCommands()
        {
            var terminal = Program.TerminalInterface;
            if (terminal == null) return;
            
            terminal.RegisterCommand(new Action<string, string, int>(JoinGame));
            terminal.RegisterCommand(new Action(DisconnectGame));
            terminal.RegisterCommand(new Action(ShowMultiplayerStatus));
            terminal.RegisterCommand(new Action<string>(SendChatMessage));
            terminal.RegisterCommand(new Action(ToggleMultiplayerUI));
            terminal.RegisterCommand(new Action(ListRemoteVehicles));
            terminal.RegisterCommand(new Action<string>(GotoRemoteVehicle));
            terminal.RegisterCommand(new Action(ClearMultiplayerLogs));
            terminal.RegisterCommand(new Action(ShowLogDirectory));
        }
        
        [TerminalAction("mp_join", "Join a multiplayer session - usage: mp_join <playerName> <serverAddress> <port>", ArgParseMode.Default)]
        public static async void JoinGame(string playerName, string serverAddress, int port)
        {
            var manager = ModEntry.GetMultiplayerManager();
            if (manager == null) return;
            
            if (await manager.JoinSession(playerName, serverAddress, (ushort)port))
                DefaultCategory.Log.Info("Joined multiplayer session", "JoinGame", nameof(MultiplayerCommands));
        }
        
        [TerminalAction("mp_disconnect", "Disconnect from current multiplayer session", ArgParseMode.Default)]
        public static void DisconnectGame()
        {
            var manager = ModEntry.GetMultiplayerManager();
            if (manager?.IsConnected == true)
                manager.Disconnect();
        }
        
        [TerminalAction("mp_status", "Show current multiplayer connection status", ArgParseMode.Default)]
        public static void ShowMultiplayerStatus()
        {
            var manager = ModEntry.GetMultiplayerManager();
            if (manager == null) return;
            
            DefaultCategory.Log.Info($"Connected: {manager.IsConnected}, Host: {manager.IsHost}", "Status", nameof(MultiplayerCommands));
            if (manager.IsConnected)
            {
                foreach (var player in manager.ConnectedPlayers)
                    DefaultCategory.Log.Info($"  Player: {player}", "Status", nameof(MultiplayerCommands));
                DefaultCategory.Log.Info($"Remote vehicles: {manager.VehicleRenderer?.RemoteVehicleCount ?? 0}", "Status", nameof(MultiplayerCommands));
            }
        }
        
        [TerminalAction("mp_chat", "Send a chat message - usage: mp_chat <message>", ArgParseMode.Unparsed)]
        public static void SendChatMessage(string message)
        {
            var manager = ModEntry.GetMultiplayerManager();
            if (manager?.IsConnected == true && manager.ChatManager != null)
                manager.ChatManager.SendMessage(message);
        }
        
        [TerminalAction("mp_ui", "Toggle the multiplayer UI window", ArgParseMode.Default)]
        public static void ToggleMultiplayerUI()
        {
            ModEntry.GetMultiplayerWindow()?.Toggle();
        }
        
        [TerminalAction("mp_vehicles", "List all remote player vehicles", ArgParseMode.Default)]
        public static void ListRemoteVehicles()
        {
            var manager = ModEntry.GetMultiplayerManager();
            if (manager?.SyncManager == null) return;
            
            foreach (var kvp in manager.SyncManager.GetRemoteVehicles())
                DefaultCategory.Log.Info($"  [{kvp.Value.VehicleId}] Owner: {kvp.Value.OwnerName}", "Vehicles", nameof(MultiplayerCommands));
        }
        
        [TerminalAction("mp_goto", "Teleport to a remote player's vehicle - usage: mp_goto <playerName>", ArgParseMode.Default)]
        public static void GotoRemoteVehicle(string playerName)
        {
            var manager = ModEntry.GetMultiplayerManager();
            if (manager?.SyncManager == null || manager.VehicleRenderer == null) return;
            
            string? targetKey = null;
            foreach (var kvp in manager.SyncManager.GetRemoteVehicles())
            {
                if (kvp.Value.OwnerName.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                {
                    targetKey = kvp.Key;
                    break;
                }
            }
            
            if (targetKey == null) return;
            
            Vehicle? vehicle = manager.VehicleRenderer.GetRemoteVehicle(targetKey);
            if (vehicle == null) return;
            
            Program.GetMainCamera().SetFollow(vehicle, tidalLocking: false, changeControl: true, alert: true);
        }
        
        [TerminalAction("mp_clearlogs", "Clear all multiplayer log files", ArgParseMode.Default)]
        public static void ClearMultiplayerLogs()
        {
            ModLogger.ClearAllLogsGlobal();
            DefaultCategory.Log.Info($"Cleared logs in: {ModLogger.LogDirectory}", "ClearLogs", nameof(MultiplayerCommands));
        }
        
        [TerminalAction("mp_logdir", "Show multiplayer log directory path", ArgParseMode.Default)]
        public static void ShowLogDirectory()
        {
            DefaultCategory.Log.Info($"Log directory: {ModLogger.LogDirectory}", "LogDir", nameof(MultiplayerCommands));
        }
    }
}
