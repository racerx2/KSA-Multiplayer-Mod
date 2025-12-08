using System;
using Brutal.Logging;

namespace KSA.Mods.Multiplayer
{
    public class ModEntry
    {
        public static string ModName => "KSA Multiplayer";
        public static string ModVersion => "1.0.0";
        
        private static bool _isInitialized = false;
        private static MultiplayerManager? _multiplayerManager;
        private static MultiplayerWindow? _multiplayerWindow;
        
        public static void Initialize()
        {
            if (_isInitialized) return;
            
            try
            {
                // Load settings FIRST so we have the player name for logging
                MultiplayerSettings.Load();
                
                // Set the player name for log files BEFORE any logging happens
                ModLogger.PlayerName = MultiplayerSettings.Current.DefaultPlayerName;
                
                // Apply patches FIRST before any managers are created
                try { NetworkPatches.ApplyPatches(); }
                catch (Exception ex) { DefaultCategory.Log.Warning($"Network patches failed: {ex.Message}", "Initialize", nameof(ModEntry)); }
                
                try { VehiclePatches.ApplyPatches(); }
                catch (Exception ex) { DefaultCategory.Log.Warning($"Vehicle patches failed: {ex.Message}", "Initialize", nameof(ModEntry)); }
                
                _multiplayerManager = new MultiplayerManager();
                _multiplayerManager.Initialize();
                
                _multiplayerWindow = new MultiplayerWindow(_multiplayerManager, new Brutal.Numerics.float2(600f, 400f));
                
                MultiplayerCommands.RegisterCommands();
                
                _isInitialized = true;
                DefaultCategory.Log.Info($"{ModName} v{ModVersion} initialized", "Initialize", nameof(ModEntry));
            }
            catch (Exception ex)
            {
                DefaultCategory.Log.Error($"Failed to initialize: {ex.Message}", "Initialize", nameof(ModEntry));
                throw;
            }
        }
        
        public static void Shutdown()
        {
            if (!_isInitialized) return;
            
            MultiplayerSettings.Save();
            NetworkPatches.RemovePatches();
            VehiclePatches.RemovePatches();
            _multiplayerManager?.Shutdown();
            _multiplayerManager = null;
            _isInitialized = false;
        }
        
        public static void Update(double deltaTime)
        {
            if (_isInitialized)
                _multiplayerManager?.Update(deltaTime);
        }
        
        public static MultiplayerManager? GetMultiplayerManager() => _multiplayerManager;
        public static MultiplayerWindow? GetMultiplayerWindow() => _multiplayerWindow;
        public static bool IsInitialized() => _isInitialized;
    }
}
