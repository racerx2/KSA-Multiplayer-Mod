using System;
using System.IO;
using System.Reflection;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Centralized logging for the multiplayer mod.
    /// Logs are stored in a "logs" subdirectory next to the mod DLL.
    /// Log filenames include the player name for easy identification.
    /// 
    /// Log files per our architecture:
    /// - Subspace.log: Current subspace ID, time offsets, subspace changes
    /// - Sync.log: Sync events, orbital state when syncing, timestamps
    /// - Players.log: Connected players, their subspaces, join/leave events
    /// - Network.log: Latency per player, connection status changes
    /// - Vehicles.log: Remote vehicles created/destroyed, count
    /// - Events.log: Maneuvers detected, animations detected, what triggered resyncs
    /// - GOTO.log: Teleport attempts, source state, target state, distance offset
    /// - Renderer.log: Vehicle creation/destruction, orbit corrections
    /// - Patches.log: Harmony patch activity
    /// - NameTags.log: Nametag rendering
    /// </summary>
    public static class ModLogger
    {
        private static string? _logDirectory;
        private static string? _playerName;
        private static readonly object _lock = new object();
        private static double _lastHeartbeatTime = 0;
        private const double HEARTBEAT_INTERVAL = 5.0; // seconds
        
        /// <summary>
        /// Gets or sets the player name used in log filenames.
        /// Should be set early during initialization.
        /// </summary>
        public static string PlayerName
        {
            get => _playerName ?? "Unknown";
            set => _playerName = SanitizeFileName(value);
        }
        
        /// <summary>
        /// Gets the log directory path, creating it if necessary.
        /// </summary>
        public static string LogDirectory
        {
            get
            {
                if (_logDirectory == null)
                {
                    lock (_lock)
                    {
                        if (_logDirectory == null)
                        {
                            string? assemblyLocation = Assembly.GetExecutingAssembly().Location;
                            string? modDirectory = Path.GetDirectoryName(assemblyLocation);
                            
                            if (string.IsNullOrEmpty(modDirectory))
                                modDirectory = Environment.CurrentDirectory;
                            
                            _logDirectory = Path.Combine(modDirectory, "logs");
                            
                            try
                            {
                                if (!Directory.Exists(_logDirectory))
                                    Directory.CreateDirectory(_logDirectory);
                            }
                            catch
                            {
                                // If we can't create logs directory, logging will silently fail
                            }
                        }
                    }
                }
                return _logDirectory;
            }
        }
        
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Unknown";
            
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
                name = name.Replace(c, '_');
            
            name = name.Trim();
            if (name.Length > 32)
                name = name.Substring(0, 32);
            
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        }
        
        public static string GetLogPath(string logName)
        {
            return Path.Combine(LogDirectory, $"{logName}_{PlayerName}.log");
        }
        
        /// <summary>
        /// Writes a timestamped message to the specified log file.
        /// Respects EnableDebugLogging setting.
        /// </summary>
        public static void Log(string logName, string message)
        {
            // Check if logging is enabled
            if (!MultiplayerSettings.Current.EnableDebugLogging)
                return;
            
            try
            {
                string logPath = GetLogPath(logName);
                string timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
                File.AppendAllText(logPath, timestampedMessage);
            }
            catch
            {
                // Silently fail if we can't write logs
            }
        }
        
        /// <summary>
        /// Always logs regardless of EnableDebugLogging setting.
        /// Use for critical errors only.
        /// </summary>
        public static void LogAlways(string logName, string message)
        {
            try
            {
                string logPath = GetLogPath(logName);
                string timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
                File.AppendAllText(logPath, timestampedMessage);
            }
            catch { }
        }

        /// <summary>
        /// Logs a periodic heartbeat with current state snapshot.
        /// Call this from Update loop - it self-limits to HEARTBEAT_INTERVAL.
        /// </summary>
        public static void LogHeartbeat(double currentTime)
        {
            if (!MultiplayerSettings.Current.EnableDebugLogging)
                return;
            
            if (currentTime - _lastHeartbeatTime < HEARTBEAT_INTERVAL)
                return;
            
            _lastHeartbeatTime = currentTime;
            
            var manager = MultiplayerManager.Instance;
            if (manager == null)
                return;
            
            // Log state snapshot to multiple files
            var subspaceManager = manager.SubspaceManager;
            var syncManager = manager.SyncManager;
            var vehicleRenderer = manager.VehicleRenderer;
            
            // Subspace heartbeat
            if (subspaceManager != null)
            {
                Log("Subspace", $"HEARTBEAT: Subspace={subspaceManager.CurrentSubspace}, HasSync={subspaceManager.HasInitialSync}");
            }
            
            // Sync heartbeat
            if (syncManager != null)
            {
                Log("Events", $"HEARTBEAT: EventCount={syncManager.EventCount}");
            }
            
            // Vehicle heartbeat
            if (vehicleRenderer != null)
            {
                Log("Vehicles", $"HEARTBEAT: RemoteVehicleCount={vehicleRenderer.RemoteVehicleCount}");
            }
            
            // Network heartbeat
            Log("Network", $"HEARTBEAT: Connected={manager.IsConnected}, IsHost={manager.IsHost}, Players={manager.ConnectedPlayers.Count}");
            
            // Players heartbeat
            if (subspaceManager != null)
            {
                foreach (var player in manager.ConnectedPlayers)
                {
                    int ps = subspaceManager.GetPlayerSubspace(player);
                    Log("Players", $"HEARTBEAT: {player} in Subspace {ps}");
                }
            }
        }
        
        /// <summary>
        /// Clears all log files in the log directory for this player.
        /// </summary>
        public static void ClearAllLogs()
        {
            try
            {
                if (Directory.Exists(LogDirectory))
                {
                    foreach (var file in Directory.GetFiles(LogDirectory, $"*_{PlayerName}.log"))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Clears all log files in the log directory regardless of player name.
        /// </summary>
        public static void ClearAllLogsGlobal()
        {
            try
            {
                if (Directory.Exists(LogDirectory))
                {
                    foreach (var file in Directory.GetFiles(LogDirectory, "*.log"))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch { }
        }
    }
}
