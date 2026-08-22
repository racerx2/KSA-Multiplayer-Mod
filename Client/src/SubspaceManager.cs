using System;
using System.Collections.Generic;
using System.Reflection;
using KSA;
using Brutal.Numerics;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Tracks each player's simulation time and syncs the local clock to another player's.</summary>
    public class SubspaceManager
    {
        private static readonly FieldInfo? NextSimStepField = typeof(Universe).GetField(
            "_nextSimStep", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo? LastSimStepField = typeof(Universe).GetField(
            "_lastSimStep", BindingFlags.NonPublic | BindingFlags.Static);
        private readonly NetworkManager _networkManager;
        private const string LogName = "Subspace";
        
        /// <summary>Holds a player's game time and the wall-clock time it was received.</summary>
        private struct PlayerTimeData
        {
            public double GameTime;           // Their simulation time when message was received
            public DateTime WallClockTime;    // Real wall-clock time when we received it

            /// <summary>Their simulation speed when the reading was taken.</summary>
            public double SimulationSpeed;
            
            public PlayerTimeData(double gameTime, DateTime wallClockTime, double simulationSpeed = 1.0)
            {
                GameTime = gameTime;
                WallClockTime = wallClockTime;
                SimulationSpeed = simulationSpeed;
            }
        }
        
        // Maps player name to time data.
        private readonly Dictionary<string, PlayerTimeData> _playerTimeData = new();
        
        // Maximum time difference for players to count as the same subspace.
        public const double SYNC_THRESHOLD_SECONDS = 5.0;

        /// <summary>Maximum age of a player time reading before it counts as unknown.</summary>
        public const double PlayerTimeStaleAfterSeconds = 10.0;

        /// <summary>Event sync manager notified after a deliberate clock move.</summary>
        private EventSyncManager? _eventSyncManager;

        public void SetEventSyncManager(EventSyncManager? manager) => _eventSyncManager = manager;
        
        // Local player name.
        private string? _localPlayerName;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        public SubspaceManager(NetworkManager networkManager)
        {
            _networkManager = networkManager;
            Log("SubspaceManager initialized (wall-clock time prediction)");
        }
        
        /// <summary>Sets the local player name.</summary>
        public void SetLocalPlayerName(string playerName)
        {
            _localPlayerName = playerName;

            // Removes any time entry recorded under the local player's own name.
            if (_playerTimeData.Remove(playerName))
                Log($"Discarded a self-recorded time entry for {playerName} (arrived before authentication)");

            Log($"Local player set: {playerName}");
        }
        
        /// <summary>Records a player's simulation time and rate with the current wall-clock time.</summary>
        public void UpdatePlayerTime(string playerName, double simTimeSeconds, double simulationSpeed = 1.0)
        {
            double oldPredictedTime = GetPlayerTime(playerName);
            
            // Stores the game time, receive time, and simulation rate.
            _playerTimeData[playerName] =
                new PlayerTimeData(simTimeSeconds, DateTime.UtcNow, simulationSpeed);
            
            // Logs changes larger than one second.
            if (Math.Abs(simTimeSeconds - oldPredictedTime) > 1.0)
            {
                Log($"Player {playerName} time: {simTimeSeconds:F1}s (was {oldPredictedTime:F1}s)");
            }
        }
        
        /// <summary>Returns a player's predicted current simulation time.</summary>
        public double GetPlayerTime(string playerName)
        {
            if (!_playerTimeData.TryGetValue(playerName, out var data))
                return 0;
            
            // Computes real seconds elapsed since the reading.
            double realSecondsElapsed = (DateTime.UtcNow - data.WallClockTime).TotalSeconds;

            // Returns zero when the reading is stale.
            if (realSecondsElapsed > PlayerTimeStaleAfterSeconds)
                return 0;

            // Advances the stored time at the recorded simulation rate.
            double speed = data.SimulationSpeed > 0.0 ? data.SimulationSpeed : 1.0;
            return data.GameTime + realSecondsElapsed * speed;
        }
        
        /// <summary>Returns the players with a stored time reading.</summary>
        public IEnumerable<string> GetKnownPlayers() => new List<string>(_playerTimeData.Keys);

        /// <summary>The local player name, if known.</summary>
        public string? LocalPlayerName => _localPlayerName;

        /// <summary>Returns true when the player has no reading or the reading is too old.</summary>
        public bool IsPlayerTimeStale(string playerName)
        {
            if (!_playerTimeData.TryGetValue(playerName, out var data))
                return true;
            return (DateTime.UtcNow - data.WallClockTime).TotalSeconds > PlayerTimeStaleAfterSeconds;
        }

        /// <summary>Returns seconds since the player's last time reading, or -1 if none.</summary>
        public double SecondsSincePlayerTimeUpdate(string playerName)
        {
            if (!_playerTimeData.TryGetValue(playerName, out var data))
                return -1;
            return (DateTime.UtcNow - data.WallClockTime).TotalSeconds;
        }

        public double GetLocalTime()
        {
            return Universe.GetElapsedTime().Seconds();
        }
        
        /// <summary>Returns true when a player's predicted time is within the sync threshold.</summary>
        public bool IsInSameSubspace(string playerName)
        {
            if (string.IsNullOrEmpty(playerName))
                return false;
            
            // Treats the local player as always in the same subspace.
            if (playerName == _localPlayerName)
                return true;
            
            double localTime = GetLocalTime();
            double theirPredictedTime = GetPlayerTime(playerName);
            
            // Treats missing time data as a different subspace.
            if (theirPredictedTime == 0)
                return false;
            
            double timeDiff = Math.Abs(localTime - theirPredictedTime);
            return timeDiff <= SYNC_THRESHOLD_SECONDS;
        }
        
        /// <summary>Returns another player's predicted time minus local time.</summary>
        public double GetTimeDifference(string playerName)
        {
            double localTime = GetLocalTime();
            double theirPredictedTime = GetPlayerTime(playerName);
            return theirPredictedTime - localTime;
        }
        
        /// <summary>Returns time differences from local time for all non-stale players.</summary>
        public Dictionary<string, double> GetAllTimeDifferences()
        {
            var result = new Dictionary<string, double>();
            double localTime = GetLocalTime();
            
            foreach (var kvp in _playerTimeData)
            {
                if (kvp.Key != _localPlayerName && !IsPlayerTimeStale(kvp.Key))
                {
                    double theirPredictedTime = GetPlayerTime(kvp.Key);
                    result[kvp.Key] = theirPredictedTime - localTime;
                }
            }
            
            return result;
        }
        
        /// <summary>Jumps local time forward to a player's time and propagates the local vehicle.</summary>
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
            
            // Sets the universe clock to the target time.
            SetUniverseTime(targetTime);
            
            // Propagates the local vehicle's orbit to the new time.
            PropagateLocalVehicle(targetTime);

            // Reads the clock back to confirm the write took effect.
            double achieved = GetLocalTime();
            if (Math.Abs(achieved - targetTime) > 1.0)
            {
                Log($"SYNC FAILED: asked for T={targetTime:F3}s but the clock reads " +
                    $"T={achieved:F3}s - the universe time write did not take");
                return false;
            }

            // Rebases the event sync manager onto the new time.
            _eventSyncManager?.RebaseAfterTimeJump(achieved);

            Log($"SYNC COMPLETE: Now at T={achieved:F1}s");
            return true;
        }
        
        // There is deliberately no unconditional time set here. Time moves in
        // one direction only, through SyncToPlayer: a client may jump forward to
        // meet a player who is ahead, and a player who is behind is left alone.
        // A backwards jump would have to un-simulate everything the player has
        // already flown, which nothing in this design can do.
        
        /// <summary>Sets the universe simulation step to the requested time.</summary>
        private void SetUniverseTime(double timeSeconds)
        {
            // Requires the private simulation-step fields.
            if (NextSimStepField == null || LastSimStepField == null)
                throw new MissingFieldException("KSA Universe simulation-step fields were not found.");

            UniverseTime targetTime = new UniverseTime(timeSeconds);
            SimStep synchronizedStep = new SimStep
            {
                PreviousTime = targetTime,
                NextTime = targetTime,
                DeltaTime = 0
            };

            LastSimStepField.SetValue(null, synchronizedStep);
            NextSimStepField.SetValue(null, synchronizedStep);
            Log($"Universe simulation step set to {timeSeconds:F3}s");
        }
        
        /// <summary>Propagates the local vehicle's orbit to a new time.</summary>
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
            
            UniverseTime newTime = new UniverseTime(newTimeSeconds);
            
            // Gets state vectors at the new time.
            StateVectors newState = vehicle.Orbit.GetStateVectorsAt(newTime);
            
            Log($"Propagating {vehicle.Id}: Old pos -> New pos at T={newTimeSeconds:F1}s");
            
            // Creates an orbit at the propagated position.
            Orbit newOrbit = Orbit.CreateFromStateCci(
                parent, 
                newTime, 
                newState.PositionCci, 
                newState.VelocityCci, 
                vehicle.OrbitColor
            );
            
            vehicle.SetFlightPlan(new FlightPlan(newOrbit, new KeyHash((uint)vehicle.Id.GetHashCode())));
            vehicle.UpdatePerFrameData();
            
            // Updates kinematic states to match the new orbit.
            UpdateVehicleKinematicStates(vehicle, newOrbit, newState);
            
            // Updates the current system.
            Universe.CurrentSystem?.UpdatePerFrameData();
            
            Log($"Vehicle {vehicle.Id} propagated to T={newTimeSeconds:F1}s");
        }
        
        // A re-epoch that leaves the vessel where it is used to live here, for
        // the backwards jump above. The forward path uses PropagateLocalVehicle
        // instead, which advances the vessel along its orbit to the new time —
        // the vessel really has been coasting for those seconds.
        
        /// <summary>Updates a vehicle's physics states to match a new orbit.</summary>
        private void UpdateVehicleKinematicStates(Vehicle vehicle, Orbit orbit, StateVectors stateVectors)
        {
            try
            {
                vehicle.GetPhysicsStatesMutable().UpdateFromAnalytic(
                    orbit, in stateVectors, vehicle.Body2Cce, vehicle.BodyRates, vehicle.Situation);
                Log($"Updated physics states to {stateVectors.StateTime.Seconds():F3}s");
            }
            catch (Exception ex)
            {
                Log($"ERROR updating KinematicStates: {ex.Message}");
            }
        }
        
        /// <summary>Removes a player from time tracking.</summary>
        public void RemovePlayer(string playerName)
        {
            if (_playerTimeData.Remove(playerName))
            {
                Log($"Removed player time tracking: {playerName}");
            }
        }
        
        /// <summary>Clears all tracked state.</summary>
        public void Reset()
        {
            _playerTimeData.Clear();
            _localPlayerName = null;
            Log("SubspaceManager RESET");
        }
        
        /// <summary>Returns a status string for UI display.</summary>
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

        /// <summary>Returns true when a player is ahead by more than the sync threshold.</summary>
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
        
        // Numbered subspaces were removed along with the shared-universe model:
        // every client now runs its own universe and the only thing that
        // matters is how far apart two clocks are, which GetTimeDifference
        // answers. The constant-valued CurrentSubspace, HasInitialSync,
        // PlayerSubspaces, GetPlayerSubspace and GetSubspaceOffset that stood
        // here reported 0 and true to anything that asked.
    }
}
