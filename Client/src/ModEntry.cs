using System;
using Brutal.Logging;

namespace KSA.Mods.Multiplayer
{
    public class ModEntry
    {
        public static string ModName => "KSA Multiplayer";
        public static string ModVersion => ModInfo.Version;
        
        private static bool _isInitialized = false;
        private static MultiplayerManager? _multiplayerManager;
        private static MultiplayerWindow? _multiplayerWindow;
        
        public static void Initialize()
        {
            if (_isInitialized) return;
            
            try
            {
                // Loads the mod settings.
                MultiplayerSettings.Load();
                
                // Sets the player name used in log file names.
                ModLogger.PlayerName = MultiplayerSettings.Current.DefaultPlayerName;
                
                // Applies the Harmony patches.
                try { NetworkPatches.ApplyPatches(); }
                catch (Exception ex) { DefaultCategory.Log.Warning($"Network patches failed: {ex.Message}", "Initialize", nameof(ModEntry)); }
                
                try { VesselStructure.ApplyPatches(); }
                catch (Exception ex) { DefaultCategory.Log.Warning($"Structure patches failed: {ex.Message}", "Initialize", nameof(ModEntry)); }

                try { VehiclePatches.ApplyPatches(); }
                catch (Exception ex) { DefaultCategory.Log.Warning($"Vehicle patches failed: {ex.Message}", "Initialize", nameof(ModEntry)); }

                
                _multiplayerManager = new MultiplayerManager();
                _multiplayerManager.Initialize();
                
                _multiplayerWindow = new MultiplayerWindow(_multiplayerManager, new Brutal.Numerics.float2(600f, 400f));
                _multiplayerWindow.SetShown(true);
                
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
            VesselStructure.RemovePatches();

            // Flushes and closes the log writers.
            ModLogger.Shutdown();
            _multiplayerManager?.Shutdown();
            _multiplayerManager = null;
            _isInitialized = false;
        }
        
        public static void Update(double deltaTime)
        {
            if (!_isInitialized) return;

            _multiplayerManager?.Update(deltaTime);

            // Draws the multiplayer window.
            _multiplayerWindow?.Draw(Program.MainViewport);
        }
        
        public static MultiplayerManager? GetMultiplayerManager() => _multiplayerManager;
        public static MultiplayerWindow? GetMultiplayerWindow() => _multiplayerWindow;
        public static bool IsInitialized() => _isInitialized;
    }
}
