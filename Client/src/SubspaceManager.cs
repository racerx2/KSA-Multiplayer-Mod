using System;
using System.Collections.Generic;
using System.Reflection;
using KSA;
using Brutal.Numerics;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Simplified subspace manager for multiplayer time synchronization.
    /// 
    /// Core concept: Players at different simulation times exist in different "subspaces".
    /// - Same time (within threshold) = Can see each other fully
    /// - Different times = Ghost mode (orbit visible on map, 3D model hidden in flight)
    /// 
    /// Players sync by jumping their time forward to match another player.
    /// </summary>
    public class SubspaceManager
    {
        private readonly NetworkManager _networkManager;
        private const string LogName = "Subspace";
        
        /// <summary>
        /// Stores a player's game time along with the wall-clock time when we received it.
        /// This allows us to predict their current time without constant network updates.
        /// </summary>
        private struct PlayerTimeData
        {
            public double GameTime;           // Their simulation time when message was received
            public DateTime WallClockTime;    // Real wall-clock time when we received it
            
            public PlayerTimeData(double gameTime, DateTime wallClockTime)
            {
                GameTime = gameTime;
                WallClockTime = wallClockTime;
            }
        }
        
        // Player time tracking: PlayerName -> their time data
        private readonly Dictionary<string, PlayerTimeData> _playerTimeData = new();
        
        // Sync threshold in seconds - players within this are considered "same subspace"
        public const double SYNC_THRESHOLD_SECONDS = 5.0;
        
        // Local player's name for lookups
        private string? _localPlayerName;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        public SubspaceManager(NetworkManager networkManager)
        {
            _networkManager = networkManager;
            Log("SubspaceManager initialized (wall-clock time prediction)");
        }
        
        /// <summary>
        /// Set local player name for time comparisons
        /// </summary>
        public void SetLocalPlayerName(string playerName)
        {
            _localPlayerName = playerName;
            Log($"Local player set: {playerName}");
        }
        
        /// <summary>
        /// Update a player's current simulation time.
        /// Called when we receive TimeSyncMessage or VehicleStateMessage.
        /// Stores both their game time AND the wall-clock time when we received it.
        /// </summary>
        public void UpdatePlayerTime(string playerName, double simTimeSeconds)
        {
            double oldPredictedTime = GetPlayerTime(playerName);
            
            // Store their game time and current wall-clock time
            _playerTimeData[playerName] = new PlayerTimeData(simTimeSeconds, DateTime.UtcNow);
            
            // Log significant time changes
            if (Math.Abs(simTimeSeconds - oldPredictedTime) > 1.0)
            {
                Log($"Player {playerName} time: {simTimeSeconds:F1}s (was {oldPredictedTime:F1}s)");
            }
        }
        
        /// <summary>
        /// Get a player's PREDICTED current simulation time.
        /// Uses wall-clock elapsed time to predict where their game time should be now.
        /// </summary>
        public double GetPlayerTime(string playerName)
        {
            if (!_playerTimeData.TryGetValue(playerName, out var data))
                return 0;
            
            // Calculate how much real time has passed since we received their update
            double realSecondsElapsed = (DateTime.UtcNow - data.WallClockTime).TotalSeconds;
            
            // Predict their current game time (assumes 1x speed)
            // This works because at 1x, game time advances at same rate as wall-clock
            return data.GameTime + realSecondsElapsed;
        }
        
        /// <summary>
        /// Get local simulation time
        /// </summary>
        public double GetLocalTime()
        {
            return Universe.GetElapsedSimTime().Seconds();
        }
        
        /// <summary>
        /// Check if a player is in the same subspace (same time within threshold)
        /// Uses predicted time based on wall-clock elapsed time.
        /// </summary>
        public bool IsInSameSubspace(string playerName)
        {
            if (string.IsNullOrEmpty(playerName))
                return false;
            
            // Local player is always in same subspace as themselves
            if (playerName == _localPlayerName)
                return true;
            
            double localTime = GetLocalTime();
            double theirPredictedTime = GetPlayerTime(playerName);
            
            // If we don't have their time yet, assume different subspace
            if (theirPredictedTime == 0)
                return false;
            
            double timeDiff = Math.Abs(localTime - theirPredictedTime);
            return timeDiff <= SYNC_THRESHOLD_SECONDS;
        }
        
        /// <summary>
        /// Get time difference with another player (positive = they're ahead, negative = behind)
        /// Uses predicted time.
        /// </summary>
        public double GetTimeDifference(string playerName)
        {
            double localTime = GetLocalTime();
            double theirPredictedTime = GetPlayerTime(playerName);
            return theirPredictedTime - localTime;
        }
        
        /// <summary>
        /// Get all players and their time differences from local
        /// </summary>
        public Dictionary<string, double> GetAllTimeDifferences()
        {
            var result = new Dictionary<string, double>();
            double localTime = GetLocalTime();
            
            foreach (var kvp in _playerTimeData)
            {
                if (kvp.Key != _localPlayerName)
                {
                    double theirPredictedTime = GetPlayerTime(kvp.Key);
                    result[kvp.Key] = theirPredictedTime - localTime;
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Sync local time to match target player's time.
        /// This jumps our simulation forward and propagates our vehicle's orbit.
        /// </summary>
        public bool SyncToPlayer(string targetPlayerName)
        {
            double targetTime = GetPlayerTime(targetPlayerName);
            double localTime = GetLocalTime();
            
            if (targetTime <= 0)
            {
                Log($"ERROR: No time data for player {targetPlayerName}");
                return false;
            }
            
            double timeDiff = targetTime - localTime;
            
            if (Math.Abs(timeDiff) <= SYNC_THRESHOLD_SECONDS)
            {
                Log($"Already in sync with {targetPlayerName} (diff: {timeDiff:F2}s)");
                return true;
            }
            
            if (timeDiff < 0)
            {
                Log($"ERROR: Cannot sync backwards in time. {targetPlayerName} is {-timeDiff:F1}s behind.");
                return false;
            }
            
            Log($"SYNC START: Jumping {timeDiff:F1}s forward to match {targetPlayerName}");
            
            // Set universe time via reflection
            SetUniverseTime(targetTime);
            
            // Propagate local vehicle's orbit to new time
            PropagateLocalVehicle(targetTime);
            
            Log($"SYNC COMPLETE: Now at T={targetTime:F1}s");
            return true;
        }
        
        /// <summary>
        /// Force sync local time to a specific value (used for initial server sync).
        /// Unlike SyncToPlayer, this can sync in either direction (forward or backward).
        /// </summary>
        public void ForceTimeSync(double targetTimeSeconds)
        {
            double localTime = GetLocalTime();
            double timeDiff = targetTimeSeconds - localTime;
            
            Log($"FORCE TIME SYNC: Local={localTime:F3}s -> Server={targetTimeSeconds:F3}s (diff={timeDiff:F3}s)");
            
            // Set universe time
            SetUniverseTime(targetTimeSeconds);
            
            // Recreate local vehicle's orbit at new time with current position
            RecreateLocalVehicleOrbit(targetTimeSeconds);
            
            Log($"FORCE TIME SYNC COMPLETE: Now at T={targetTimeSeconds:F3}s");
        }
        
        /// <summary>
        /// Set the universe's _elapsedSimTime directly
        /// </summary>
        private void SetUniverseTime(double timeSeconds)
        {
            var field = typeof(Universe).GetField("_elapsedSimTime",
                BindingFlags.NonPublic | BindingFlags.Static);
            
            if (field != null)
            {
                field.SetValue(null, timeSeconds);
                Log($"Universe time set to {timeSeconds:F3}s");
            }
            else
            {
                Log("ERROR: Could not find _elapsedSimTime field!");
            }
        }
        
        /// <summary>
        /// Propagate local vehicle's orbit to the new time using Kepler physics
        /// </summary>
        private void PropagateLocalVehicle(double newTimeSeconds)
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            if (vehicle == null)
            {
                Log("No controlled vehicle to propagate");
                return;
            }
            
            Celestial? parent = vehicle.Parent as Celestial;
            if (parent == null)
            {
                Log("ERROR: Vehicle has no celestial parent");
                return;
            }
            
            SimTime newTime = new SimTime(newTimeSeconds);
            
            // Get state vectors at new time (Kepler propagation)
            StateVectors newState = vehicle.Orbit.GetStateVectorsAt(newTime);
            
            Log($"Propagating {vehicle.Id}: Old pos -> New pos at T={newTimeSeconds:F1}s");
            
            // Create new orbit at the propagated position
            Orbit newOrbit = Orbit.CreateFromStateCci(
                parent, 
                newTime, 
                newState.PositionCci, 
                newState.VelocityCci, 
                vehicle.OrbitColor
            );
            
            vehicle.SetFlightPlan(new FlightPlan(newOrbit, new KeyHash((uint)vehicle.Id.GetHashCode())));
            vehicle.UpdatePerFrameData();
            
            // CRITICAL: Also update KinematicStates to match the new orbit time
            // This prevents "outdated kinematic states" error when camera switches to surface mode
            UpdateVehicleKinematicStates(vehicle, newOrbit, newState);
            
            // Also update the system
            Universe.CurrentSystem?.UpdatePerFrameData();
            
            Log($"Vehicle {vehicle.Id} propagated to T={newTimeSeconds:F1}s");
        }
        
        /// <summary>
        /// Recreate local vehicle's orbit at a new time using CURRENT position.
        /// Unlike PropagateLocalVehicle, this doesn't move the vehicle - it just
        /// creates an orbit with the new epoch time at the current position.
        /// </summary>
        private void RecreateLocalVehicleOrbit(double newTimeSeconds)
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            if (vehicle == null)
            {
                Log("No controlled vehicle to recreate orbit for");
                return;
            }
            
            Celestial? parent = vehicle.Parent as Celestial;
            if (parent == null)
            {
                Log("ERROR: Vehicle has no celestial parent");
                return;
            }
            
            // Get CURRENT position and velocity (don't propagate)
            var currentPos = vehicle.Orbit.StateVectors.PositionCci;
            var currentVel = vehicle.Orbit.StateVectors.VelocityCci;
            
            SimTime newTime = new SimTime(newTimeSeconds);
            
            Log($"Recreating orbit for {vehicle.Id} at T={newTimeSeconds:F3}s (pos unchanged)");
            
            // Create new orbit at current position but with new epoch time
            Orbit newOrbit = Orbit.CreateFromStateCci(
                parent, 
                newTime, 
                currentPos, 
                currentVel, 
                vehicle.OrbitColor
            );
            
            vehicle.SetFlightPlan(new FlightPlan(newOrbit, new KeyHash((uint)vehicle.Id.GetHashCode())));
            vehicle.UpdatePerFrameData();
            
            // Update KinematicStates - get state vectors from the new orbit
            var newState = newOrbit.StateVectors;
            UpdateVehicleKinematicStates(vehicle, newOrbit, newState);
            
            Universe.CurrentSystem?.UpdatePerFrameData();
            
            Log($"Vehicle {vehicle.Id} orbit recreated at T={newTimeSeconds:F3}s");
        }
        
        /// <summary>
        /// Update vehicle's KinematicStates to match a new orbit.
        /// Uses reflection to access private _lastKinematicStates field.
        /// </summary>
        private void UpdateVehicleKinematicStates(Vehicle vehicle, Orbit orbit, StateVectors stateVectors)
        {
            try
            {
                // Get the private _lastKinematicStates field
                var kinematicField = typeof(Vehicle).GetField("_lastKinematicStates",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (kinematicField == null)
                {
                    Log("WARNING: Could not find _lastKinematicStates field");
                    return;
                }
                
                // Get the current KinematicStates struct
                object? kinematicObj = kinematicField.GetValue(vehicle);
                if (kinematicObj == null)
                {
                    Log("WARNING: _lastKinematicStates is null");
                    return;
                }
                
                KinematicStates kinematic = (KinematicStates)kinematicObj;
                
                // Update the Time field to match the new orbit's state time
                // This is the key fix - keeps KinematicStates.Time in sync with Orbit.StateVectors.StateTime
                kinematic.Time = stateVectors.StateTime;
                kinematic.PositionPhys = stateVectors.PositionCci;
                kinematic.VelocityPhys = stateVectors.VelocityCci;
                kinematic.PhysFrame = PhysicsFrame.Cci;
                kinematic.Parent = orbit.Parent;
                
                // Write the modified struct back
                kinematicField.SetValue(vehicle, kinematic);
                
                Log($"Updated KinematicStates.Time to {stateVectors.StateTime.Seconds():F3}s");
            }
            catch (Exception ex)
            {
                Log($"ERROR updating KinematicStates: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Remove a player from tracking (on disconnect)
        /// </summary>
        public void RemovePlayer(string playerName)
        {
            if (_playerTimeData.Remove(playerName))
            {
                Log($"Removed player time tracking: {playerName}");
            }
        }
        
        /// <summary>
        /// Reset all state
        /// </summary>
        public void Reset()
        {
            _playerTimeData.Clear();
            _localPlayerName = null;
            Log("SubspaceManager RESET");
        }
        
        /// <summary>
        /// Get status string for UI display
        /// </summary>
        public string GetStatusString()
        {
            if (!_networkManager.IsConnected)
                return "Not connected";
            
            double localTime = GetLocalTime();
            int playersInSync = 0;
            int playersOutOfSync = 0;
            
            foreach (var kvp in _playerTimeData)
            {
                if (kvp.Key == _localPlayerName)
                    continue;
                
                double theirPredictedTime = GetPlayerTime(kvp.Key);
                double diff = Math.Abs(theirPredictedTime - localTime);
                if (diff <= SYNC_THRESHOLD_SECONDS)
                    playersInSync++;
                else
                    playersOutOfSync++;
            }
            
            if (playersOutOfSync > 0)
                return $"T={localTime:F0}s ({playersOutOfSync} out of sync)";
            else if (playersInSync > 0)
                return $"T={localTime:F0}s (all synced)";
            else
                return $"T={localTime:F0}s";
        }
        
        /// <summary>
        /// Find the player furthest ahead in time (for sync target)
        /// </summary>
        public string? GetMostAdvancedPlayer()
        {
            string? mostAdvanced = null;
            double maxTime = GetLocalTime();
            
            foreach (var kvp in _playerTimeData)
            {
                if (kvp.Key != _localPlayerName)
                {
                    double theirPredictedTime = GetPlayerTime(kvp.Key);
                    if (theirPredictedTime > maxTime)
                    {
                        maxTime = theirPredictedTime;
                        mostAdvanced = kvp.Key;
                    }
                }
            }
            
            return mostAdvanced;
        }
        
        /// <summary>
        /// Check if sync is available (someone is ahead of us)
        /// </summary>
        public bool IsSyncAvailable()
        {
            double localTime = GetLocalTime();
            
            foreach (var kvp in _playerTimeData)
            {
                if (kvp.Key != _localPlayerName)
                {
                    double theirPredictedTime = GetPlayerTime(kvp.Key);
                    double diff = theirPredictedTime - localTime;
                    if (diff > SYNC_THRESHOLD_SECONDS)
                        return true;
                }
            }
            
            return false;
        }
        
        // Legacy compatibility properties (for existing code)
        public int CurrentSubspace => 0;
        public bool HasInitialSync => true;
        public IReadOnlyDictionary<string, int> PlayerSubspaces => new Dictionary<string, int>();
        public int GetPlayerSubspace(string playerName) => 0;
        public double GetSubspaceOffset(int subspaceId) => 0;
    }
}
