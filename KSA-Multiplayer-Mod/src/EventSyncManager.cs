using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.Numerics;
using KSA;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>
    /// Event-based vehicle synchronization.
    /// Only syncs when something actually changes (maneuvers, animations).
    /// Lets KSA's deterministic Kepler physics handle smooth motion between syncs.
    /// </summary>
    public class EventSyncManager
    {
        private readonly NetworkManager _networkManager;
        private readonly Dictionary<string, RemoteVehicleData> _remoteVehicles;
        private readonly HashSet<string> _designsSent;
        private string? _localPlayerName;
        private uint _sequenceNumber = 0;
        private const string LogName = "Sync";
        
        // Reference to SubspaceManager for time tracking
        private SubspaceManager? _subspaceManager;
        
        // Event detection state
        private bool _prevEngineOn = false;
        private float _prevThrottle = 0f;
        private byte _prevThrusterFlags = 0;
        private double3 _lastSentVelocity;
        private double _lastEventTime;
        private bool _initialStateSent = false;
        private int _eventCount = 0;
        private double _prevSimulationSpeed = 1.0;
        
        // Vessel switching detection
        private string? _lastControlledVehicleId = null;
        
        // Track all vehicles this player owns (for multi-vehicle sync)
        private readonly HashSet<string> _ownedVehicleIds = new HashSet<string>();
        private double _lastOwnedVehicleSyncTime = 0;
        private const double OwnedVehicleSyncInterval = 5.0; // Sync owned but non-controlled vehicles every 5 seconds
        
        // Reflection for accessing private ManualControlInputs
        private static readonly FieldInfo? _controlInputsField = typeof(Vehicle).GetField("_manualControlInputs", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        public int EventCount => _eventCount;
        
        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        /// <summary>
        /// Set reference to SubspaceManager for player time tracking
        /// </summary>
        public void SetSubspaceManager(SubspaceManager? manager)
        {
            _subspaceManager = manager;
        }
        
        public EventSyncManager(NetworkManager networkManager)
        {
            _networkManager = networkManager;
            _remoteVehicles = new Dictionary<string, RemoteVehicleData>();
            _designsSent = new HashSet<string>();
            
            NetworkPatches.OnVehicleStateReceived += OnVehicleStateReceived;
            NetworkPatches.OnVehicleDesignSyncReceived += OnVehicleDesignSyncReceived;
            
            _networkManager.OnPlayerConnected += (playerName) => {
                _designsSent.Clear();
                _initialStateSent = false;
            };
            
            Log("EventSyncManager initialized");
        }
        
        public void Update(double deltaTime)
        {
            if (!MultiplayerSettings.Current.EnableVesselSync)
                return;
            
            CheckForEvents();
        }

        /// <summary>
        /// Detects maneuvers and animations, sends sync only when events occur
        /// </summary>
        private void CheckForEvents()
        {
            if (string.IsNullOrEmpty(_localPlayerName))
                return;
            
            Vehicle? vehicle = Program.ControlledVehicle;
            if (vehicle == null)
                return;
            
            // Detect vessel switch
            if (vehicle.Id != _lastControlledVehicleId)
            {
                if (_lastControlledVehicleId != null)
                {
                    Log($"VESSEL SWITCH: {_lastControlledVehicleId} -> {vehicle.Id}");
                    // Keep the previous vehicle in owned list (don't remove it!)
                }
                // Add new vehicle to owned list
                _ownedVehicleIds.Add(vehicle.Id);
                _designsSent.Clear();
                _initialStateSent = false;
                _lastControlledVehicleId = vehicle.Id;
            }
            
            // Also sync OTHER owned vehicles at lower frequency
            double currentSimTime = Universe.GetElapsedSimTime().Seconds();
            if (currentSimTime - _lastOwnedVehicleSyncTime >= OwnedVehicleSyncInterval)
            {
                _lastOwnedVehicleSyncTime = currentSimTime;
                SyncOwnedVehicles(vehicle.Id);
            }
            
            string vehicleKey = $"{_localPlayerName}_{vehicle.Id}";
            
            // Always send design first
            if (!_designsSent.Contains(vehicleKey))
            {
                SendVehicleDesign(vehicle, _localPlayerName);
                _designsSent.Add(vehicleKey);
            }
            
            // Get current maneuvering state
            bool engineOn = false;
            float throttle = 0f;
            byte thrusterFlags = 0;
            
            if (_controlInputsField != null)
            {
                var controlInputs = (ManualControlInputs)_controlInputsField.GetValue(vehicle)!;
                engineOn = controlInputs.EngineOn;
                throttle = controlInputs.EngineThrottle;
                thrusterFlags = (byte)controlInputs.ThrusterCommandFlags;
            }
            
            // Detect velocity change (catches autopilot/flight computer thrust too)
            double3 currentVel = vehicle.Orbit.StateVectors.VelocityCci;
            double velDelta = (currentVel - _lastSentVelocity).Length();
            double currentTime = Universe.GetElapsedSimTime().Seconds();
            double timeDelta = currentTime - _lastEventTime;
            double velChangeRate = timeDelta > 0.001 ? velDelta / timeDelta : 0;
            bool isThrusting = velChangeRate > 0.5; // 0.5 m/s² threshold
            
            // Detect state CHANGE in inputs
            bool inputStateChanged = (engineOn != _prevEngineOn) || 
                                     (Math.Abs(throttle - _prevThrottle) > 0.01f) ||
                                     (thrusterFlags != _prevThrusterFlags);
            
            // Currently maneuvering?
            bool inputManeuvering = (engineOn && throttle > 0.01f) || thrusterFlags != 0;
            bool anyManeuvering = inputManeuvering || isThrusting;
            
            // EVENT DETECTION: Send only when something changes
            bool shouldSend = false;
            string eventReason = "";
            
            // Detect time warp ending (coming out of accelerated time)
            double currentSimSpeed = Universe.SimulationSpeed;
            bool warpEnded = (_prevSimulationSpeed > 1.5 && currentSimSpeed <= 1.5);
            
            if (!_initialStateSent)
            {
                shouldSend = true;
                eventReason = "INITIAL_STATE";
            }
            else if (warpEnded)
            {
                // Player just exited time warp - broadcast new position/time
                shouldSend = true;
                eventReason = $"WARP_ENDED: Speed {_prevSimulationSpeed:F1}x -> {currentSimSpeed:F1}x";
            }
            else if (inputStateChanged)
            {
                shouldSend = true;
                eventReason = $"INPUT_CHANGED: Engine={engineOn}, Throttle={throttle:F2}, RCS={thrusterFlags}";
            }
            else if (anyManeuvering)
            {
                // Only send while maneuvering, not constantly
                shouldSend = true;
                eventReason = $"MANEUVERING: VelChange={velChangeRate:F2}m/s²";
            }
            
            // Update previous simulation speed
            _prevSimulationSpeed = currentSimSpeed;
            
            if (shouldSend)
            {
                _eventCount++;
                Log($"EVENT #{_eventCount}: {eventReason}");
                SendVehicleState(vehicle, _localPlayerName, anyManeuvering);
                _lastSentVelocity = currentVel;
                _lastEventTime = currentTime;
                _initialStateSent = true;
            }
            
            // Update previous state
            _prevEngineOn = engineOn;
            _prevThrottle = throttle;
            _prevThrusterFlags = thrusterFlags;
        }

        /// <summary>
        /// Sync owned vehicles that aren't currently controlled (e.g., rocket when player is in EVA)
        /// This ensures vehicles don't disappear when the player switches to a different vehicle.
        /// </summary>
        private void SyncOwnedVehicles(string controlledVehicleId)
        {
            if (string.IsNullOrEmpty(_localPlayerName) || Universe.CurrentSystem == null)
                return;
            
            var vehiclesToRemove = new List<string>();
            
            foreach (string vehicleId in _ownedVehicleIds)
            {
                // Skip the currently controlled vehicle (already synced in main loop)
                if (vehicleId == controlledVehicleId)
                    continue;
                
                // Find this vehicle in the universe
                var astro = Universe.CurrentSystem.Get(vehicleId);
                if (astro is not Vehicle vehicle)
                {
                    // Vehicle no longer exists - mark for removal
                    Log($"Owned vehicle {vehicleId} no longer exists, marking for removal");
                    vehiclesToRemove.Add(vehicleId);
                    continue;
                }
                
                // Ensure design is sent
                string vehicleKey = $"{_localPlayerName}_{vehicleId}";
                if (!_designsSent.Contains(vehicleKey))
                {
                    SendVehicleDesign(vehicle, _localPlayerName);
                    _designsSent.Add(vehicleKey);
                }
                
                // Send position update for this owned vehicle (not maneuvering since not controlled)
                SendVehicleState(vehicle, _localPlayerName, isManeuvering: false);
                Log($"Synced owned vehicle: {vehicleId} (not currently controlled)");
            }
            
            // Remove vehicles that no longer exist
            foreach (var id in vehiclesToRemove)
            {
                _ownedVehicleIds.Remove(id);
            }
        }

        private void SendVehicleDesign(Vehicle vehicle, string playerName)
        {
            string templateId = vehicle.BodyTemplate?.Id ?? vehicle.Id;
            
            Log($"SENDING DESIGN - Player: {playerName}, Vehicle: {vehicle.Id}, Template: {templateId}");
            
            var msg = new VehicleDesignSyncMessage
            {
                VehicleId = vehicle.Id,
                OwnerPlayerName = playerName,
                TemplateId = templateId,
                SequenceNumber = ++_sequenceNumber
            };
            
            _networkManager.SendMessageToAll(msg);
        }

        private void SendVehicleState(Vehicle vehicle, string playerName, bool isManeuvering)
        {
            Celestial? parentCelestial = vehicle.Parent as Celestial;
            
            // Get physics frame and situation from kinematic states
            var kinStates = vehicle.LastKinematicStates;
            byte physFrame = (byte)kinStates.PhysFrame; // 0=CCI, 1=CCF
            byte situation = (byte)kinStates.Situation;
            bool isSurfaceContact = situation >= 2; // Rolling, Landed, Sailing, Floating
            
            double stateTime = vehicle.Orbit.StateVectors.StateTime.Seconds();
            
            // CCI coordinates (always send for compatibility)
            double3 positionCci = vehicle.Orbit.StateVectors.PositionCci;
            double3 velocityCci = vehicle.Orbit.StateVectors.VelocityCci;
            
            // CCF coordinates (for surface situations)
            double3 positionCcf = double3.Zero;
            double3 velocityCcf = double3.Zero;
            doubleQuat body2Frame = doubleQuat.Identity;
            
            if (isSurfaceContact && parentCelestial != null)
            {
                // Get CCF coordinates from kinematic states
                if (kinStates.PhysFrame == PhysicsFrame.Ccf)
                {
                    positionCcf = kinStates.PositionPhys;
                    velocityCcf = kinStates.VelocityPhys;
                    // Body2Ccf orientation
                    body2Frame = kinStates.Body2Phys;
                }
                else
                {
                    // Convert CCI to CCF
                    SimTime simTime = new SimTime(stateTime);
                    doubleQuat cci2Ccf = parentCelestial.GetCci2Ccf(simTime);
                    double angularVel = parentCelestial.GetAngularVelocity();
                    double3 omega = new double3(0, 0, angularVel);
                    
                    positionCcf = positionCci.Transform(cci2Ccf);
                    double3 rotationalVel = double3.Cross(omega, positionCci);
                    velocityCcf = (velocityCci - rotationalVel).Transform(cci2Ccf);
                    
                    // Convert body orientation to CCF
                    doubleQuat cce2Cci = parentCelestial.GetCce2Cci();
                    doubleQuat body2Cci = doubleQuat.Concatenate(vehicle.Body2Cce, cce2Cci);
                    body2Frame = doubleQuat.Concatenate(body2Cci, cci2Ccf);
                }
                
                Log($"SURFACE STATE: Sit={situation}, PhysFrame={physFrame}, PosCCF=({positionCcf.X:F0},{positionCcf.Y:F0},{positionCcf.Z:F0})");
            }
            else
            {
                // Orbital - use CCI orientation
                doubleQuat cce2Cci = parentCelestial?.GetCce2Cci() ?? doubleQuat.Identity;
                body2Frame = doubleQuat.Concatenate(vehicle.Body2Cce, cce2Cci);
            }
            
            // Get control inputs
            bool engineOn = false;
            float throttle = 0f;
            uint thrusterFlags = 0;
            
            if (_controlInputsField != null)
            {
                var controlInputs = (ManualControlInputs)_controlInputsField.GetValue(vehicle)!;
                engineOn = controlInputs.EngineOn;
                throttle = controlInputs.EngineThrottle;
                thrusterFlags = (uint)controlInputs.ThrusterCommandFlags;
            }
            
            // Get rocket thrust values for visual sync
            float[] rocketThrusts = Array.Empty<float>();
            if (vehicle.Parts?.RocketNozzles?.States != null && vehicle.Parts.RocketNozzles.States.Count > 0)
            {
                rocketThrusts = new float[vehicle.Parts.RocketNozzles.States.Count];
                for (int i = 0; i < vehicle.Parts.RocketNozzles.States.Count; i++)
                {
                    rocketThrusts[i] = vehicle.Parts.RocketNozzles.States[i].AverageThrottle;
                }
            }

            var msg = new VehicleStateMessage
            {
                VehicleId = vehicle.Id,
                OwnerPlayerName = playerName,
                ParentBodyId = vehicle.Parent?.Id ?? "Earth",
                StateTimeSeconds = stateTime,
                ServerTimeSeconds = stateTime,
                // CCI coordinates
                PositionCciX = positionCci.X,
                PositionCciY = positionCci.Y,
                PositionCciZ = positionCci.Z,
                VelocityCciX = velocityCci.X,
                VelocityCciY = velocityCci.Y,
                VelocityCciZ = velocityCci.Z,
                // CCF coordinates (for surface)
                PositionCcfX = positionCcf.X,
                PositionCcfY = positionCcf.Y,
                PositionCcfZ = positionCcf.Z,
                VelocityCcfX = velocityCcf.X,
                VelocityCcfY = velocityCcf.Y,
                VelocityCcfZ = velocityCcf.Z,
                PhysFrame = physFrame,
                // Orientation (CCI for orbital, CCF for surface)
                OrientationX = body2Frame.X,
                OrientationY = body2Frame.Y,
                OrientationZ = body2Frame.Z,
                OrientationW = body2Frame.W,
                BodyRatesX = vehicle.BodyRates.X,
                BodyRatesY = vehicle.BodyRates.Y,
                BodyRatesZ = vehicle.BodyRates.Z,
                EngineOn = engineOn,
                EngineThrottle = throttle,
                ThrusterFlags = thrusterFlags,
                IsManeuvering = isManeuvering,
                Situation = situation,
                SequenceNumber = ++_sequenceNumber,
                RocketThrusts = rocketThrusts
            };
            
            _networkManager.SendMessageToAll(msg);
            string frameStr = isSurfaceContact ? "CCF" : "CCI";
            Log($"SENT STATE [{frameStr}] - Seq:{msg.SequenceNumber}, Sit={situation}, T={stateTime:F3}s");
        }

        private void OnVehicleStateReceived(VehicleStateMessage msg)
        {
            if (msg.OwnerPlayerName == _localPlayerName)
                return;
            
            string key = $"{msg.OwnerPlayerName}_{msg.VehicleId}";
            bool isSurface = msg.Situation >= 2;
            string frameStr = isSurface ? "CCF" : "CCI";
            
            Log($"STATE RECEIVED [{frameStr}] - Key: {key}, Sit={msg.Situation}, PhysFrame={msg.PhysFrame}, T={msg.StateTimeSeconds:F3}s");
            
            // Update player's time in SubspaceManager for visibility checks
            if (_subspaceManager != null && !string.IsNullOrEmpty(msg.OwnerPlayerName))
            {
                _subspaceManager.UpdatePlayerTime(msg.OwnerPlayerName, msg.StateTimeSeconds);
            }
            
            // Queue for LMP-style interpolation
            var queue = PositionUpdateQueue.GetOrCreateQueue(key);
            queue.Enqueue(msg);
            
            if (!_remoteVehicles.ContainsKey(key))
            {
                _remoteVehicles[key] = new RemoteVehicleData
                {
                    VehicleId = msg.VehicleId ?? string.Empty,
                    OwnerName = msg.OwnerPlayerName ?? string.Empty,
                    LastUpdate = DateTime.UtcNow
                };
            }
            
            var v = _remoteVehicles[key];
            
            v.TargetPosition = new double3(msg.PositionCciX, msg.PositionCciY, msg.PositionCciZ);
            v.TargetVelocity = new double3(msg.VelocityCciX, msg.VelocityCciY, msg.VelocityCciZ);
            v.TargetOrientation = new doubleQuat(msg.OrientationX, msg.OrientationY, msg.OrientationZ, msg.OrientationW);
            v.ParentBodyId = msg.ParentBodyId ?? "Earth";
            v.LastUpdate = DateTime.UtcNow;
            v.SenderStateTimeSeconds = msg.StateTimeSeconds;
            v.IsOwnerManeuvering = msg.IsManeuvering;
            v.RocketThrusts = msg.RocketThrusts ?? Array.Empty<float>();
            v.NeedsUpdate = true;
            
            // Detect situation change - this triggers orbit update
            if (msg.Situation != v.LastSituation)
            {
                Log($"SITUATION CHANGE [{key}]: {v.LastSituation} -> {msg.Situation}");
                v.SituationChanged = true;
                v.LastSituation = msg.Situation;
            }
            
            if (!v.HasCurrentState)
            {
                v.CurrentPosition = v.TargetPosition;
                v.CurrentVelocity = v.TargetVelocity;
                v.CurrentOrientation = v.TargetOrientation;
                v.HasCurrentState = true;
            }
        }

        private void OnVehicleDesignSyncReceived(VehicleDesignSyncMessage msg)
        {
            Log($"DESIGN RECEIVED - Owner: {msg.OwnerPlayerName}, Vehicle: {msg.VehicleId}, Template: {msg.TemplateId}");
            
            if (msg.OwnerPlayerName == _localPlayerName)
                return;
            
            string key = $"{msg.OwnerPlayerName}_{msg.VehicleId}";
            
            // VESSEL SWITCH: Remove any OTHER vehicles from this player
            var keysToRemove = new List<string>();
            foreach (var kvp in _remoteVehicles)
            {
                if (kvp.Value.OwnerName == msg.OwnerPlayerName && kvp.Key != key)
                {
                    keysToRemove.Add(kvp.Key);
                    Log($"VESSEL SWITCH: Removing {kvp.Key} (player switched to {msg.VehicleId})");
                }
            }
            foreach (var oldKey in keysToRemove)
            {
                _remoteVehicles.Remove(oldKey);
                PositionUpdateQueue.RemoveQueue(oldKey);
            }
            
            if (_remoteVehicles.ContainsKey(key))
            {
                _remoteVehicles[key].TemplateId = msg.TemplateId;
            }
            else
            {
                _remoteVehicles[key] = new RemoteVehicleData
                {
                    VehicleId = msg.VehicleId ?? string.Empty,
                    OwnerName = msg.OwnerPlayerName ?? string.Empty,
                    TemplateId = msg.TemplateId,
                    LastUpdate = DateTime.UtcNow
                };
            }
        }

        public void SetLocalPlayerName(string playerName) => _localPlayerName = playerName;
        public IReadOnlyDictionary<string, RemoteVehicleData> GetRemoteVehicles() => _remoteVehicles;
        
        public void Reset()
        {
            _remoteVehicles.Clear();
            _designsSent.Clear();
            _initialStateSent = false;
            _eventCount = 0;
            _prevSimulationSpeed = 1.0;
            _lastControlledVehicleId = null;
            PositionUpdateQueue.ClearAllQueues();
            Log("EventSyncManager RESET");
        }
        
        public void RemovePlayerVehicles(string playerName)
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in _remoteVehicles)
            {
                if (kvp.Value.OwnerName == playerName)
                    keysToRemove.Add(kvp.Key);
            }
            foreach (var key in keysToRemove)
            {
                _remoteVehicles.Remove(key);
                PositionUpdateQueue.RemoveQueue(key);
                Log($"Removed vehicle data for {key}");
            }
        }

        public void RemoveRemoteVehicle(string key)
        {
            if (_remoteVehicles.Remove(key))
                Log($"Removed remote vehicle: {key}");
        }
        
        /// <summary>
        /// Directly set remote vehicle state (for initial sync)
        /// </summary>
        public void SetSyncedState(string ownerName, string vehicleId, string templateId, string parentBodyId,
            double3 positionCci, double3 velocityCci, doubleQuat orientationCci, double stateTimeSeconds)
        {
            string key = $"{ownerName}_{vehicleId}";
            
            if (!_remoteVehicles.ContainsKey(key))
            {
                _remoteVehicles[key] = new RemoteVehicleData
                {
                    VehicleId = vehicleId,
                    OwnerName = ownerName,
                    TemplateId = templateId,
                    ParentBodyId = parentBodyId,
                    LastUpdate = DateTime.UtcNow
                };
            }
            
            var v = _remoteVehicles[key];
            v.TemplateId = templateId;
            v.ParentBodyId = parentBodyId;
            v.TargetPosition = positionCci;
            v.TargetVelocity = velocityCci;
            v.TargetOrientation = orientationCci;
            v.CurrentPosition = positionCci;
            v.CurrentVelocity = velocityCci;
            v.CurrentOrientation = orientationCci;
            v.SenderStateTimeSeconds = stateTimeSeconds;
            v.HasCurrentState = true;
            v.NeedsUpdate = true;
            v.OrbitSetOnce = false;
            v.IsOwnerManeuvering = false;
            
            Log($"SYNCED STATE SET - Key: {key}, Pos=({positionCci.X:F0},{positionCci.Y:F0},{positionCci.Z:F0}), T={stateTimeSeconds:F3}s");
        }
        
        /// <summary>
        /// Remote vehicle data structure
        /// </summary>
        public class RemoteVehicleData
        {
            public string VehicleId { get; set; } = string.Empty;
            public string OwnerName { get; set; } = string.Empty;
            public DateTime LastUpdate { get; set; }
            public string? TemplateId { get; set; }
            public double3 CurrentPosition { get; set; }
            public double3 TargetPosition { get; set; }
            public double3 CurrentVelocity { get; set; }
            public double3 TargetVelocity { get; set; }
            public doubleQuat CurrentOrientation { get; set; } = doubleQuat.Identity;
            public doubleQuat TargetOrientation { get; set; } = doubleQuat.Identity;
            public string ParentBodyId { get; set; } = string.Empty;
            public bool HasCurrentState { get; set; }
            public double SenderStateTimeSeconds { get; set; }
            public bool NeedsUpdate { get; set; } = true;
            public bool IsOwnerManeuvering { get; set; }
            public bool OrbitSetOnce { get; set; }
            public float[] RocketThrusts { get; set; } = Array.Empty<float>();
            public byte LastSituation { get; set; } = 255; // Invalid initial value to force first update
            public bool SituationChanged { get; set; } = true; // Start true so first state triggers orbit set
        }
    }
}
