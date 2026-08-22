using System;
using System.Collections.Generic;
using System.IO;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Writes per-category log files for the multiplayer mod.</summary>
    public static class ModLogger
    {
        private static string? _logDirectory;
        private static string? _playerName;
        private static readonly object _lock = new object();
        private static double _lastHeartbeatTime = 0;
        private const double HEARTBEAT_INTERVAL = 5.0; // seconds
        
        // Throttling for high-frequency logs
        private static readonly Dictionary<string, int> _messageCounters = new Dictionary<string, int>();
        private static readonly Dictionary<string, DateTime> _lastLogTimes = new Dictionary<string, DateTime>();
        private const int THROTTLE_EVERY_N = 100; // Log every Nth message for throttled categories
        private const double THROTTLE_MIN_INTERVAL_MS = 1000; // Or at least once per second
        
        /// <summary>Gets or sets the player name used in log filenames.</summary>
        public static string PlayerName
        {
            get => _playerName ?? "Unknown";
            set => _playerName = SanitizeFileName(value);
        }
        
        /// <summary>Returns the KSA_MP_LOGS directory if it exists, otherwise an empty string.</summary>
        private static string SharedLogDirectory()
        {
            try
            {
                string? shared = Environment.GetEnvironmentVariable("KSA_MP_LOGS");
                if (string.IsNullOrWhiteSpace(shared)) return string.Empty;
                return Directory.Exists(shared) ? shared : string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string AssemblyDirectory()
        {
            try
            {
                return Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        /// <summary>Returns the logs subdirectory of the first candidate a test file can be written in.</summary>
        private static string FirstWritable(params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;

                try
                {
                    string dir = candidate.EndsWith("logs") ? candidate : Path.Combine(candidate, "logs");
                    Directory.CreateDirectory(dir);

                    string probe = Path.Combine(dir, ".writetest");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    return dir;
                }
                catch
                {
                    // Not writable - try the next one.
                }
            }

            return Path.Combine(Path.GetTempPath(), "KSA-Multiplayer-logs");
        }

        /// <summary>Gets the log directory path, creating it if necessary.</summary>
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
                            // Pick the first writable location from the candidate list.
                            _logDirectory = FirstWritable(
                                SharedLogDirectory(),
                                AssemblyDirectory(),
                                Path.Combine(Environment.CurrentDirectory, "logs"),
                                Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "KSA-Multiplayer"));
                            
                            try
                            {
                                if (!Directory.Exists(_logDirectory))
                                    Directory.CreateDirectory(_logDirectory);
                            }
                            catch
                            {
                                // Ignore failure to create the log directory.
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
        
        /// <summary>One open, buffered writer per log file.</summary>
        private static readonly Dictionary<string, StreamWriter> _writers = new();
        private static DateTime _lastFlush = DateTime.MinValue;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

        private static void WriteLine(string logName, string message)
        {
            try
            {
                string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

                lock (_lock)
                {
                    if (!_writers.TryGetValue(logName, out StreamWriter? writer))
                    {
                        writer = new StreamWriter(GetLogPath(logName), append: true) { AutoFlush = false };
                        _writers[logName] = writer;
                    }

                    writer.WriteLine(timestamped);

                    // Flush every writer once per interval.
                    DateTime now = DateTime.UtcNow;
                    if (now - _lastFlush >= FlushInterval)
                    {
                        _lastFlush = now;
                        foreach (StreamWriter w in _writers.Values)
                            w.Flush();
                    }
                }
            }
            catch
            {
                // Ignore logging failures.
            }
        }

        /// <summary>Flushes and closes every writer.</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                foreach (StreamWriter w in _writers.Values)
                {
                    try { w.Flush(); w.Dispose(); } catch { }
                }
                _writers.Clear();
            }
        }

        public static void Log(string logName, string message)
        {
            // Check if logging is enabled
            if (!MultiplayerSettings.Current.EnableDebugLogging)
                return;
            
            try
            {
                WriteLine(logName, message);
            }
            catch
            {
                // Ignore log write failures.
            }
        }
        
        /// <summary>
        /// Logs at most once per interval per key, ignoring EnableDebugLogging.
        /// </summary>
        /// <remarks>
        /// Reserved for anomalies a player must be able to report even with
        /// debug logging switched off: non-finite state, a probe that threw, a
        /// vessel the renderer could not place. Routine telemetry belongs in
        /// <see cref="LogThrottledEvery"/>, which the setting silences.
        /// </remarks>
        public static void LogThrottledAlways(string logName, string key, string message,
                                              double minIntervalSeconds = 3.0)
        {
            WriteThrottled(logName, key, message, minIntervalSeconds);
        }

        /// <summary>
        /// Logs at most once per interval per key, honouring EnableDebugLogging.
        /// </summary>
        public static void LogThrottledEvery(string logName, string key, string message,
                                             double minIntervalSeconds = 3.0)
        {
            if (!MultiplayerSettings.Current.EnableDebugLogging)
                return;

            WriteThrottled(logName, key, message, minIntervalSeconds);
        }

        /// <summary>Shared interval throttle behind the two entry points above.</summary>
        private static void WriteThrottled(string logName, string key, string message,
                                           double minIntervalSeconds)
        {
            try
            {
                string fullKey = logName + "|" + key;
                DateTime now = DateTime.Now;

                lock (_lock)
                {
                    if (_lastLogTimes.TryGetValue(fullKey, out DateTime last) &&
                        (now - last).TotalSeconds < minIntervalSeconds)
                    {
                        return;
                    }
                    _lastLogTimes[fullKey] = now;
                }

                WriteLine(logName, message);
            }
            catch { }
        }

        /// <summary>Always logs regardless of EnableDebugLogging.</summary>
        public static void LogAlways(string logName, string message)
        {
            try
            {
                WriteLine(logName, message);
            }
            catch { }
        }

        /// <summary>Logs every Nth message or once per minimum interval.</summary>
        public static void LogThrottled(string logName, string throttleKey, string message, bool forceLog = false)
        {
            if (!MultiplayerSettings.Current.EnableDebugLogging)
                return;
            
            string fullKey = $"{logName}_{throttleKey}";
            DateTime now = DateTime.Now;
            
            lock (_lock)
            {
                // Get or initialize counter
                if (!_messageCounters.TryGetValue(fullKey, out int count))
                {
                    count = 0;
                    _lastLogTimes[fullKey] = now;
                }
                
                count++;
                _messageCounters[fullKey] = count;
                
                // Check if we should log
                bool shouldLog = forceLog;
                
                if (!shouldLog && count >= THROTTLE_EVERY_N)
                {
                    shouldLog = true;
                    _messageCounters[fullKey] = 0;
                }
                
                if (!shouldLog && _lastLogTimes.TryGetValue(fullKey, out DateTime lastTime))
                {
                    if ((now - lastTime).TotalMilliseconds >= THROTTLE_MIN_INTERVAL_MS)
                    {
                        shouldLog = true;
                    }
                }
                
                if (shouldLog)
                {
                    _lastLogTimes[fullKey] = now;
                    try
                    {
                        string throttleInfo = forceLog ? "" : $" (#{count})";
                        WriteLine(logName, $"{throttleInfo} {message}".TrimStart());
                    }
                    catch { }
                }
            }
        }

        /// <summary>Logs a state snapshot at most once per HEARTBEAT_INTERVAL.</summary>
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
            
            // Players heartbeat: how far each player's clock is from ours.
            if (subspaceManager != null)
            {
                foreach (var player in manager.ConnectedPlayers)
                {
                    if (player == manager.LocalPlayerName) continue;
                    double diff = subspaceManager.GetTimeDifference(player);
                    Log("Players", $"HEARTBEAT: {player} is {diff:+0.0;-0.0}s from us");
                }
            }
        }
        
        /// <summary>Deletes every log file in the log directory.</summary>
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
