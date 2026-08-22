using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Brutal.Numerics;
using KSA;
using KSA.Mods.Multiplayer.Messages;

namespace KSA.Mods.Multiplayer
{
    /// <summary>Event-based vehicle synchronization.</summary>
    public class EventSyncManager
    {
        private readonly NetworkManager _networkManager;
        private readonly Dictionary<string, RemoteVehicleData> _remoteVehicles;
        private readonly HashSet<string> _designsSent;
        private string? _localPlayerName;
        private uint _sequenceNumber = 0;
        private const string LogName = "Sync";
        private bool _publishingEnabled;
        
        // Reference to SubspaceManager for time tracking
        private SubspaceManager? _subspaceManager;
        
        // Event detection state
        private bool _prevEngineOn = false;
        private float _prevThrottle = 0f;
        private byte _prevThrusterFlags = 0;
        private bool _initialStateSent = false;
        private int _eventCount = 0;
        private double _prevSimulationSpeed = 1.0;
        private byte _prevVehicleRegion = 255; // Invalid initial value to force first detection
        
        // Vessel switching detection
        private string? _lastControlledVehicleId = null;
        
        // Track all vehicles this player owns (for multi-vehicle sync)
        private readonly HashSet<string> _ownedVehicleIds = new HashSet<string>();

        /// <summary>Wall-clock time each owned, uncontrolled vessel was last published.</summary>
        private readonly Dictionary<string, DateTime> _ownedVehicleLastSync = new();
        private const double OwnedVehicleSyncInterval = 5.0; // Sync owned but non-controlled vehicles every 5 seconds
        
        // Atmospheric flight periodic sync (prevents stale data during steady descent)
        private double _lastAtmosphericSyncTime = 0;
        private const double AtmosphericSyncInterval = 0.5; // Force sync every 0.5s during atmospheric flight

        // Timestamp of the last realtime-rate publish.
        private DateTime _lastRealtimeSyncAt = DateTime.MinValue;
        private const double RealtimeSyncIntervalSeconds = 1.0 / 15.0; // 15 Hz
        /// <summary>Distance within which a remote player triggers realtime-rate publishing.</summary>
        private const double NearbySyncDistanceMeters = 2000.0;

        /// <summary>Proper acceleration above which the vessel counts as under thrust.</summary>
        private const double ThrustDetectionThreshold = 0.05;

        /// <summary>Maximum wall-clock time the controlled vessel may go without publishing.</summary>
        private const double ControlledHeartbeatSeconds = 1.0;
        
        // Reflection for accessing private ManualControlInputs
        private static readonly FieldInfo? _controlInputsField = typeof(Vehicle).GetField("_manualControlInputs", 
            BindingFlags.NonPublic | BindingFlags.Instance);
        
        public int EventCount => _eventCount;

        private static void Log(string msg) => ModLogger.Log(LogName, msg);
        
        /// <summary>Sets the SubspaceManager reference.</summary>
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
            NetworkPatches.OnVehicleRemoveReceived += OnVehicleRemoveReceived;
            
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

        /// <summary>Sends a sync only when a state-change event occurs.</summary>
        private void CheckForEvents()
        {
            if (!_publishingEnabled || string.IsNullOrEmpty(_localPlayerName))
                return;
            
            Vehicle? vehicle = Program.ControlledVehicle;

            // Publish owned vessels only when there is no usable controlled vessel.
            if (vehicle == null || vehicle.IsDisposed)
            {
                if (vehicle != null)
                    ModLogger.LogThrottled(LogName, "CONTROLLED_DISPOSED",
                        $"{vehicle.Id} has been destroyed - not publishing it");
                else
                    ModLogger.LogThrottled(LogName, "CONTROLLED_NONE",
                        "Not flying anything - publishing owned vessels only");

                // Exclude no vessel from the owned-vessel sync.
                SyncOwnedVehicles(string.Empty);
                _lastControlledVehicleId = null;
                return;
            }

            // Disposed controlled vessels are handled by the guard above.

            // Detect vessel switch
            if (vehicle.Id != _lastControlledVehicleId)
            {
                if (_lastControlledVehicleId != null)
                {
                    Log($"VESSEL SWITCH: {_lastControlledVehicleId} -> {vehicle.Id}");
                    // Keep the previous vehicle in the owned list.
                }
                // Take ownership only if the vessel is not another player's.
                if (!VesselIdentity.IsRemoteName(vehicle.Id))
                {
                    _ownedVehicleIds.Add(vehicle.Id);
                }
                else
                {
                    Log($"NOT PUBLISHING {vehicle.Id}: it belongs to another player - claim it to take over");
                }
                _designsSent.Clear();
                _initialStateSent = false;
                _lastControlledVehicleId = vehicle.Id;
            }
            
            // Sync other owned vehicles.
            double currentSimTime = Universe.GetElapsedTime().Seconds();
            // Per-vessel publish rate is decided inside.
            SyncOwnedVehicles(vehicle.Id);
            
            // Do not publish a vessel owned by another player.
            if (VesselIdentity.IsRemoteName(vehicle.Id))
            {
                ModLogger.LogThrottled(LogName, "SPECTATING",
                    $"Flying {vehicle.Id}, which belongs to another player - not publishing it. " +
                    $"Claim it in the Vessels panel to take over.");
                return;
            }

            string vehicleKey = VesselIdentity.MakeUid(_localPlayerName ?? string.Empty, vehicle.Id);
            
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
            
            double currentTime = Universe.GetElapsedTime().Seconds();

            // Detect thrust from the vessel's proper acceleration.
            double properAccel = vehicle.AccelerationBody.Length();
            bool isThrusting = properAccel > ThrustDetectionThreshold;
            
            // Detect a change in the control inputs.
            bool inputStateChanged = (engineOn != _prevEngineOn) || 
                                     (Math.Abs(throttle - _prevThrottle) > 0.01f) ||
                                     (thrusterFlags != _prevThrusterFlags);
            
            // Determine whether the vessel is maneuvering.
            bool inputManeuvering = (engineOn && throttle > 0.01f) || thrusterFlags != 0;
            bool anyManeuvering = inputManeuvering || isThrusting;
            bool realtimeSyncDue =
                (DateTime.UtcNow - _lastRealtimeSyncAt).TotalSeconds >= RealtimeSyncIntervalSeconds;
            // Heartbeat is due when nothing has been published recently.
            bool heartbeatDue =
                (DateTime.UtcNow - _lastRealtimeSyncAt).TotalSeconds >= ControlledHeartbeatSeconds;
            bool nearbyPlayer = IsRemotePlayerNearby(vehicle);

            // Off rails means the vessel's motion is not analytic.
            bool offRails = !vehicle.Situation.IsOnRails();
            
            // Send only when something changes.
            bool shouldSend = false;
            string eventReason = "";
            
            // Detect time warp ending (coming out of accelerated time)
            double currentSimSpeed = Universe.SimulationSpeed;
            bool warpEnded = (_prevSimulationSpeed > 1.5 && currentSimSpeed <= 1.5);
            
            // Detect VehicleRegion change (atmosphere entry/exit)
            byte currentVehicleRegion = (byte)vehicle.VehicleRegion;
            bool regionChanged = (currentVehicleRegion != _prevVehicleRegion);
            
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
            else if (regionChanged)
            {
                // VehicleRegion changed - atmosphere entry/exit triggers physics mode change
                shouldSend = true;
                string[] regionNames = { "Surface", "LowOrbit", "HighOrbit" };
                string prevName = _prevVehicleRegion < regionNames.Length ? regionNames[_prevVehicleRegion] : "Unknown";
                string currName = currentVehicleRegion < regionNames.Length ? regionNames[currentVehicleRegion] : "Unknown";
                eventReason = $"REGION_CHANGED: {prevName} -> {currName}";
            }
            else if (inputStateChanged)
            {
                shouldSend = true;
                eventReason = $"INPUT_CHANGED: Engine={engineOn}, Throttle={throttle:F2}, RCS={thrusterFlags}";
            }
            else if ((anyManeuvering || offRails) && realtimeSyncDue)
            {
                // Cap continuous maneuver updates to avoid flooding the server.
                shouldSend = true;
                eventReason = anyManeuvering
                    ? $"MANEUVERING: ProperAccel={properAccel:F3}m/s²"
                    : $"OFF_RAILS: sit={vehicle.Situation}";
            }
            else if (nearbyPlayer && realtimeSyncDue)
            {
                shouldSend = true;
                eventReason = "NEARBY_PLAYER_15HZ";
            }
            else if (currentVehicleRegion == 0 && (currentTime - _lastAtmosphericSyncTime) >= AtmosphericSyncInterval)
            {
                // Periodic sync during atmospheric flight.
                shouldSend = true;
                eventReason = $"ATMOSPHERIC_PERIODIC: ProperAccel={properAccel:F3}m/s²";
                _lastAtmosphericSyncTime = currentTime;
            }
            else if (heartbeatDue)
            {
                // Publish a heartbeat when nothing else triggered a send.
                shouldSend = true;
                eventReason = "HEARTBEAT";
            }
            
            // Update previous simulation speed
            _prevSimulationSpeed = currentSimSpeed;
            _prevVehicleRegion = currentVehicleRegion;
            
            if (shouldSend)
            {
                _eventCount++;
                Log($"EVENT #{_eventCount}: {eventReason}");
                SendVehicleState(vehicle, _localPlayerName, anyManeuvering);
                _lastRealtimeSyncAt = DateTime.UtcNow;
                _initialStateSent = true;
                
                // Reset atmospheric sync timer whenever we send during atmospheric flight
                if (currentVehicleRegion == 0)
                {
                    _lastAtmosphericSyncTime = currentTime;
                }
            }
            
            // Update previous state
            _prevEngineOn = engineOn;
            _prevThrottle = throttle;
            _prevThrusterFlags = thrusterFlags;
        }

        /// <summary>Returns whether this vessel is published at the realtime rate.</summary>
        /// <remarks>
        /// Two separate reasons force the realtime rate. A remote player close
        /// enough to watch the vessel needs updates fast enough to look smooth.
        /// An off-rails vessel needs them because its motion is no longer
        /// analytic: remote clients propagate a vessel from its last published
        /// orbit, and that propagation is wrong the moment the vessel stops
        /// following it. On rails with nobody nearby, the orbit alone is exact,
        /// so the keepalive interval is enough.
        /// </remarks>
        private bool ShouldPublishAtRealtimeRate(Vehicle localVehicle)
        {
            if (!localVehicle.Situation.IsOnRails())
                return true;

            return IsRemotePlayerNearby(localVehicle);
        }

        private bool IsRemotePlayerNearby(Vehicle localVehicle)
        {
            string localParentId = localVehicle.Parent?.Id ?? string.Empty;
            if (string.IsNullOrEmpty(localParentId))
                return false;

            double3 localPosition;
            try
            {
                localVehicle.GetPhysicsStatesMutable().GetStatesCci(
                    out localPosition, out _, out _);
            }
            catch
            {
                localPosition = localVehicle.Orbit.StateVectors.PositionCci;
            }

            DateTime now = DateTime.UtcNow;
            foreach (RemoteVehicleData remote in _remoteVehicles.Values)
            {
                if (!remote.HasCurrentState ||
                    remote.ParentBodyId != localParentId ||
                    (now - remote.LastUpdate).TotalSeconds > 2.0)
                {
                    continue;
                }

                if ((remote.TargetPosition - localPosition).Length() <= NearbySyncDistanceMeters)
                    return true;
            }

            return false;
        }

        /// <summary>Syncs owned vehicles that are not currently controlled.</summary>
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
                if (astro is not Vehicle vehicle || vehicle.IsDisposed)
                {
                    _networkManager.SendMessageToAll(new VehicleRemoveMessage
                    {
                        OwnerPlayerName = _localPlayerName,
                        VehicleId = vehicleId,
                        VesselUid = VesselIdentity.MakeUid(_localPlayerName ?? string.Empty, vehicleId)
                    });
                    Log($"Owned vehicle {vehicleId} no longer exists or has been destroyed, marking for removal");
                    vehiclesToRemove.Add(vehicleId);
                    continue;
                }
                
                // Ensure design is sent
                string vehicleKey = VesselIdentity.MakeUid(_localPlayerName ?? string.Empty, vehicleId);
                if (!_designsSent.Contains(vehicleKey))
                {
                    SendVehicleDesign(vehicle, _localPlayerName);
                    _designsSent.Add(vehicleKey);
                }
                
                // The realtime interval is the shortest either branch can ask
                // for, so a vessel published more recently than that is skipped
                // before the proximity test runs. That test walks every known
                // remote vessel, and this loop runs once per frame per owned
                // vessel; running it only when a send could actually follow
                // keeps the cost proportional to sends rather than to frames.
                double sinceLastSend = _ownedVehicleLastSync.TryGetValue(vehicleId, out DateTime last)
                    ? (DateTime.UtcNow - last).TotalSeconds
                    : double.MaxValue;
                if (sinceLastSend < RealtimeSyncIntervalSeconds)
                    continue;

                // Choose the realtime or keepalive publish interval.
                bool realtime = ShouldPublishAtRealtimeRate(vehicle);
                double interval = realtime ? RealtimeSyncIntervalSeconds : OwnedVehicleSyncInterval;
                if (sinceLastSend < interval)
                    continue;

                _ownedVehicleLastSync[vehicleId] = DateTime.UtcNow;

                SendVehicleState(vehicle, _localPlayerName, isManeuvering: false);
                ModLogger.LogThrottled(LogName, $"OWNED_{vehicleId}",
                    $"Publishing owned vehicle {vehicleId} at " +
                    $"{(realtime ? "15Hz (nearby player or off rails)" : "0.2Hz (on rails, no player nearby)")}");
            }
            
            // Remove vehicles that no longer exist
            foreach (var id in vehiclesToRemove)
            {
                _ownedVehicleIds.Remove(id);
                _ownedVehicleLastSync.Remove(id);
            }
        }

        private void SendVehicleDesign(Vehicle vehicle, string playerName)
        {
            string templateId = vehicle.BodyTemplate?.Id ?? vehicle.Id;
            byte[] compressedDesignXml = SerializeVehicleDesign(vehicle);
            
            Log($"SENDING DESIGN - Player: {playerName}, Vehicle: {vehicle.Id}, " +
                $"Template: {templateId}, Payload: {compressedDesignXml.Length} bytes");
            
            var msg = new VehicleDesignSyncMessage
            {
                VehicleId = vehicle.Id,
                OwnerPlayerName = playerName,
                // Stable identity, independent of who currently owns the vessel.
                VesselUid = VesselIdentity.UidFromLocalName(vehicle.Id, playerName),
                TemplateId = templateId,
                SequenceNumber = ++_sequenceNumber,
                CompressedDesignXml = compressedDesignXml
            };
            
            _networkManager.SendMessageToAll(msg);
        }

        private static byte[] SerializeVehicleDesign(Vehicle vehicle)
        {
            try
            {
                VehicleData snapshot = vehicle.SerializeSave();
                var design = new VehicleSaveData
                {
                    Id = snapshot.Id,
                    RootPartInstance = snapshot.RootPartInstance,
                    Character = snapshot.Character,
                    ActiveSequence = snapshot.ActiveSequence,
                    SequenceEnvironments = snapshot.SequenceEnvironments,
                    FuelLinks = snapshot.FuelLinks
                };

                using var xml = new MemoryStream();
                XmlHelper.SerializeWithoutNaN(
                    VehicleSaves.VehicleSerializer, design, xml);
                xml.Position = 0;
                using var compressed = new MemoryStream();
                using (var brotli = new BrotliStream(
                    compressed, CompressionLevel.Fastest, leaveOpen: true))
                {
                    xml.CopyTo(brotli);
                }
                return compressed.ToArray();
            }
            catch (Exception ex)
            {
                ModLogger.LogAlways(LogName,
                    $"DESIGN SERIALIZATION FAILED for {vehicle.Id}: {ex}");
                return Array.Empty<byte>();
            }
        }

        private void SendVehicleState(Vehicle vehicle, string playerName, bool isManeuvering)
        {
            Celestial? parentCelestial = vehicle.Parent as Celestial;
            
            // Read the live physics state.
            ref readonly var bubbleOrigin = ref vehicle.BubbleOrigin;
            byte physFrame = (byte)bubbleOrigin.BubFrame; // 0=CCI, 1=CCF
            byte situation = (byte)vehicle.Situation;
            bool isSurfaceContact = situation >= 2; // Rolling, Landed, Sailing, Floating
            
            double stateTime = Universe.GetElapsedTime().Seconds();
            
            // CCI coordinates (always send for compatibility)
            double3 positionCci;
            double3 velocityCci;
            
            // CCF coordinates (for surface situations)
            double3 positionCcf = double3.Zero;
            double3 velocityCcf = double3.Zero;
            doubleQuat body2Frame = doubleQuat.Identity;

            var physicsStates = vehicle.GetPhysicsStatesMutable();
            physicsStates.GetStatesCci(out positionCci, out velocityCci, out doubleQuat body2Cci);
            
            if (isSurfaceContact && parentCelestial != null)
            {
                physicsStates.GetStatesCcf(out positionCcf, out velocityCcf, out body2Frame);
                
                // Throttle high-frequency surface state logging
                ModLogger.LogThrottled(LogName, "SURFACE_STATE",
                    $"SURFACE STATE: Sit={situation}, PhysFrame={physFrame}, PosCCF=({positionCcf.X:F0},{positionCcf.Y:F0},{positionCcf.Z:F0})");
            }
            else
            {
                body2Frame = body2Cci;
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
            if (vehicle.Parts?.RocketNozzles != null && vehicle.Parts.RocketNozzles.NumModules > 0)
            {
                var nozzleStates = vehicle.Parts.RocketNozzles.States;
                rocketThrusts = new float[nozzleStates.Length];
                for (int i = 0; i < nozzleStates.Length; i++)
                {
                    rocketThrusts[i] = vehicle.Parts.RocketNozzles.States[i].AverageThrustFraction;
                }
            }

            var msg = new VehicleStateMessage
            {
                VehicleId = vehicle.Id,
                OwnerPlayerName = playerName,
                // Stable identity, independent of who currently owns the vessel.
                VesselUid = VesselIdentity.UidFromLocalName(vehicle.Id, playerName),
                IsControlled = ReferenceEquals(Program.ControlledVehicle, vehicle),
                SimulationSpeed = Universe.GetSimulationSpeed(),
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
                VehicleRegion = (byte)vehicle.VehicleRegion,
                SequenceNumber = ++_sequenceNumber,
                RocketThrusts = rocketThrusts
            };
            
            _networkManager.SendMessageToAll(msg);
            string frameStr = isSurfaceContact ? "CCF" : "CCI";
            string[] regionNames = { "Surface", "LowOrbit", "HighOrbit" };
            string regionStr = msg.VehicleRegion < regionNames.Length ? regionNames[msg.VehicleRegion] : "?";
            
            // Throttle high-frequency sent state logging
            ModLogger.LogThrottled(LogName, "SENT_STATE",
                $"SENT STATE [{frameStr}] {vehicle.Id} - Seq:{msg.SequenceNumber}, Sit={situation}, Ctrl={msg.IsControlled}, Region={regionStr}, T={stateTime:F3}s");
        }

        private void OnVehicleStateReceived(VehicleStateMessage msg)
        {
            if (msg.OwnerPlayerName == _localPlayerName)
                return;
            
            // Key on the vessel's stable identity.
            string key = VesselIdentity.UidFromWire(
                msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty);
            bool isSurface = msg.Situation >= 2;
            string frameStr = isSurface ? "CCF" : "CCI";
            
            // Throttle high-frequency state received logging
            ModLogger.LogThrottled(LogName, "STATE_RECV", 
                $"STATE RECEIVED [{frameStr}] - Key: {key}, Sit={msg.Situation}, PhysFrame={msg.PhysFrame}, T={msg.StateTimeSeconds:F3}s");
            
            // Update player's time in SubspaceManager for visibility checks
            if (_subspaceManager != null && !string.IsNullOrEmpty(msg.OwnerPlayerName))
            {
                _subspaceManager.UpdatePlayerTime(msg.OwnerPlayerName, msg.StateTimeSeconds, msg.SimulationSpeed);
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
                    VesselUid = VesselIdentity.UidFromWire(
                        msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty),
                    IsControlled = msg.IsControlled,
                    LastUpdate = DateTime.UtcNow
                };
                ModLogger.LogAlways(LogName,
                    $"First shared state received: {key}, body={msg.ParentBodyId}, situation={msg.Situation}");
            }
            
            var v = _remoteVehicles[key];
            
            v.TargetPosition = new double3(msg.PositionCciX, msg.PositionCciY, msg.PositionCciZ);
            v.TargetVelocity = new double3(msg.VelocityCciX, msg.VelocityCciY, msg.VelocityCciZ);
            v.TargetPositionCcf = new double3(msg.PositionCcfX, msg.PositionCcfY, msg.PositionCcfZ);
            v.TargetVelocityCcf = new double3(msg.VelocityCcfX, msg.VelocityCcfY, msg.VelocityCcfZ);
            v.TargetOrientation = new doubleQuat(msg.OrientationX, msg.OrientationY, msg.OrientationZ, msg.OrientationW);
            v.ParentBodyId = msg.ParentBodyId ?? "Earth";
            v.LastUpdate = DateTime.UtcNow;
            v.SenderStateTimeSeconds = msg.StateTimeSeconds;
            v.IsOwnerManeuvering = msg.IsManeuvering;

            // Refresh which vessel the owner is flying on every update.
            v.IsControlled = msg.IsControlled;
            v.RocketThrusts = msg.RocketThrusts ?? Array.Empty<float>();
            v.NeedsUpdate = true;
            
            // Detect situation change - this triggers orbit update
            if (msg.Situation != v.LastSituation)
            {
                Log($"SITUATION CHANGE [{key}]: {v.LastSituation} -> {msg.Situation}");
                v.SituationChanged = true;
                v.LastSituation = msg.Situation;
            }
            
            // Detect VehicleRegion change - this triggers physics mode switch
            if (msg.VehicleRegion != v.LastVehicleRegion)
            {
                string[] regionNames = { "Surface", "LowOrbit", "HighOrbit" };
                string prevName = v.LastVehicleRegion < regionNames.Length ? regionNames[v.LastVehicleRegion] : "Unknown";
                string currName = msg.VehicleRegion < regionNames.Length ? regionNames[msg.VehicleRegion] : "Unknown";
                Log($"VEHICLE REGION CHANGE [{key}]: {prevName} -> {currName} - PHYSICS MODE: {(msg.VehicleRegion == 0 ? "ENABLED" : "DISABLED")}");
                v.VehicleRegionChanged = true;
                v.LastVehicleRegion = msg.VehicleRegion;
                
                // Update physics mode for this remote vehicle
                VehiclePatches.SetPhysicsMode(key, msg.VehicleRegion == 0); // Surface = physics enabled
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
            
            // Key on the vessel's stable identity.
            string key = VesselIdentity.UidFromWire(
                msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty);

            if (_remoteVehicles.ContainsKey(key))
            {
                byte[] incoming = msg.CompressedDesignXml ?? Array.Empty<byte>();
                byte[] existing = _remoteVehicles[key].CompressedDesignXml ?? Array.Empty<byte>();

                // Flag a design that differs from the one currently built.
                bool changed = incoming.Length != existing.Length ||
                               !incoming.AsSpan().SequenceEqual(existing);

                _remoteVehicles[key].TemplateId = msg.TemplateId;
                _remoteVehicles[key].CompressedDesignXml = incoming;

                if (changed && existing.Length > 0)
                {
                    _remoteVehicles[key].DesignChanged = true;
                    ModLogger.LogAlways(LogName,
                        $"Design changed for {key} ({existing.Length} -> {incoming.Length} bytes) - rebuild queued");
                }
            }
            else
            {
                _remoteVehicles[key] = new RemoteVehicleData
                {
                    VehicleId = msg.VehicleId ?? string.Empty,
                    OwnerName = msg.OwnerPlayerName ?? string.Empty,
                    VesselUid = VesselIdentity.UidFromWire(
                        msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty),
                    TemplateId = msg.TemplateId,
                    CompressedDesignXml =
                        msg.CompressedDesignXml ?? Array.Empty<byte>(),
                    LastUpdate = DateTime.UtcNow
                };
            }
            ModLogger.LogAlways(LogName,
                $"Shared design received: {key}, template={msg.TemplateId}");
        }

        /// <summary>Starts publishing a vessel produced by a structural change.</summary>
        public void TrackOwnedVehicle(string vehicleId)
        {
            if (string.IsNullOrEmpty(vehicleId)) return;
            if (VesselIdentity.IsRemoteName(vehicleId))
            {
                Log($"NOT PUBLISHING {vehicleId}: it belongs to another player");
                return;
            }
            if (_ownedVehicleIds.Add(vehicleId))
            {
                // No timer priming needed; the vessel publishes on the next update.
                Log($"NOW PUBLISHING {vehicleId} (produced by a structural change)");
            }
        }

        /// <summary>Stops publishing a vessel that no longer exists.</summary>
        public void StopPublishing(string vehicleId)
        {
            if (_ownedVehicleIds.Remove(vehicleId))
            {
                _ownedVehicleLastSync.Remove(vehicleId);
                Log($"STOPPED PUBLISHING {vehicleId} - consumed by a structural change");
            }
        }

        /// <summary>Returns the local player name.</summary>
        public string LocalPlayerName => _localPlayerName ?? string.Empty;

        public void SetLocalPlayerName(string playerName) => _localPlayerName = playerName;

        /// <summary>Rebases the event detector onto a new simulation clock and forces a publish.</summary>
        public void RebaseAfterTimeJump(double newSimTimeSeconds)
        {
            _lastAtmosphericSyncTime = newSimTimeSeconds;

            _lastRealtimeSyncAt = DateTime.MinValue;
            _initialStateSent = false;

            // Every remote vessel's measured clock offset was taken against the
            // clock we just left, so all of them are now wrong by the size of
            // the jump. Drop them and let the next sample of each vessel seed a
            // fresh one, rather than have the staleness filter refuse its way
            // back to the truth one sample at a time.
            PositionUpdateQueue.ResetFreshness();

            Log($"REBASE: event detector moved to T={newSimTimeSeconds:F3}s after a clock jump; " +
                "publishing immediately so peers learn the new time");
        }

        public void SetPublishingEnabled(bool enabled)
        {
            _publishingEnabled = enabled;
            if (enabled)
            {
                _designsSent.Clear();
                _initialStateSent = false;
                _lastControlledVehicleId = null;
                _lastRealtimeSyncAt = DateTime.MinValue;
                Log("World publishing enabled; forcing full design and state sync");
            }
            else
            {
                Log("World publishing disabled");
            }
        }

        private void OnVehicleRemoveReceived(VehicleRemoveMessage msg)
        {
            if (msg.OwnerPlayerName == _localPlayerName)
                return;
            // Key on the vessel's stable identity.
            string key = VesselIdentity.UidFromWire(
                msg.VesselUid, msg.OwnerPlayerName ?? string.Empty, msg.VehicleId ?? string.Empty);
            _remoteVehicles.Remove(key);
            PositionUpdateQueue.RemoveQueue(key);
            Log($"Shared vehicle removed: {key}");
        }
        public IReadOnlyDictionary<string, RemoteVehicleData> GetRemoteVehicles() => _remoteVehicles;

        public void Reset()
        {
            _remoteVehicles.Clear();
            _publishingEnabled = false;
            _designsSent.Clear();
            _ownedVehicleIds.Clear();
            _ownedVehicleLastSync.Clear();
            _initialStateSent = false;
            _eventCount = 0;
            _prevSimulationSpeed = 1.0;
            _lastRealtimeSyncAt = DateTime.MinValue;
            _lastControlledVehicleId = null;
            PositionUpdateQueue.ClearAllQueues();
            Log("EventSyncManager RESET");
        }
        
        public void RemoveRemoteVehicle(string key)
        {
            if (_remoteVehicles.Remove(key))
                Log($"Removed remote vehicle: {key}");
        }
        
        // A SetSyncedState that wrote a remote vessel's pose straight into the
        // table stood here, along with a RemovePlayerVehicles above it. Both
        // built their key as "{owner}_{vehicleId}". Vessel keys are uids built
        // by VesselIdentity, whose separator is '|' precisely because KSA ids
        // contain underscores, so neither could match a real entry. State
        // arrives through OnVehicleStateReceived and departures through
        // RemoveRemoteVehicle, which use the uid.
        
        /// <summary>Remote vehicle data.</summary>
        public class RemoteVehicleData
        {
            public string VehicleId { get; set; } = string.Empty;
            public string OwnerName { get; set; } = string.Empty;

            /// <summary>Stable identity of the vessel, "{creator}|{localId}".</summary>
            public string VesselUid { get; set; } = string.Empty;

            /// <summary>Whether the owner is flying this vessel.</summary>
            public bool IsControlled { get; set; }
            public DateTime LastUpdate { get; set; }
            public string? TemplateId { get; set; }
            public byte[] CompressedDesignXml { get; set; } = Array.Empty<byte>();

            /// <summary>Set when a received design differs from the one this vessel was built from.</summary>
            public bool DesignChanged { get; set; }
            public double3 CurrentPosition { get; set; }
            public double3 TargetPosition { get; set; }
            public double3 CurrentVelocity { get; set; }
            public double3 TargetVelocity { get; set; }
            public double3 TargetPositionCcf { get; set; }
            public double3 TargetVelocityCcf { get; set; }
            public doubleQuat CurrentOrientation { get; set; } = doubleQuat.Identity;
            public doubleQuat TargetOrientation { get; set; } = doubleQuat.Identity;
            public string ParentBodyId { get; set; } = string.Empty;
            public bool HasCurrentState { get; set; }
            public double SenderStateTimeSeconds { get; set; }
            public bool NeedsUpdate { get; set; } = true;
            public bool IsOwnerManeuvering { get; set; }

            public float[] RocketThrusts { get; set; } = Array.Empty<float>();
            public byte LastSituation { get; set; } = 255; // Invalid initial value to force first update
            public bool SituationChanged { get; set; } = true; // Start true so first state triggers orbit set
            public byte LastVehicleRegion { get; set; } = 255; // Invalid initial value
            public bool VehicleRegionChanged { get; set; } = true; // Start true so first state triggers physics mode check
        }
    }
}
